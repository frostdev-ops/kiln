// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Kiln.Analyzers;

/// <summary>KILN121: a member marked <c>[RequiresCapability("row")]</c> (on itself, its property or event, or a type
/// around it) is used with nothing in reach that asks for the row. In reach, within the enclosing member: a test of
/// <c>Capabilities.Is(row)</c> or of a property that asks the same (the generated guard table: <c>NoClip.Available</c>,
/// <c>Time.WeatherChoiceAvailable</c>, ...) that the use sits under (<c>if</c>, <c>?:</c>, <c>&amp;&amp;</c>,
/// <c>while</c>) or that leaves before it (<c>if (!Capabilities.Is(row)) return;</c>); a <c>try</c> that catches
/// <c>KilnUnavailableException</c> or a base of it; the enclosing member, local function, lambda or type marked with the
/// same row; a [KilnTest] or [ClientCheck] method; the row in kiln.mod.json's <c>features</c>. Also: a lambda handed
/// to a member that needs the row; a use on an object that came from a use of the same row; an object of the member's
/// assembly that Kiln hands to a lambda. One step beyond the member: a private method (or local function) that is only
/// used, by calls or as a method group, where the row is in reach counts as in reach itself (a command that catches the
/// exception for the helpers it calls). A helper of the mod that does the check is not followed: that is the
/// documented cost of a rule that looks at one member at a time (docs/kiln/modding/analyzers.md#kiln121).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapabilityAnalyzer : DiagnosticAnalyzer
{
    /// <summary>Diagnostic property: the rows not asked for, comma-separated.</summary>
    public const string RowsKey = "rows";
    /// <summary>Diagnostic property: "wrap" when the use is a whole expression statement the fix can put under an if.</summary>
    public const string FixKey = "fix";

    private static readonly Regex FeaturesKey = new("\"features\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.CultureInvariant);
    private static readonly Regex Quoted = new("\"([^\"]+)\"", RegexOptions.CultureInvariant);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptors.CapabilityUnchecked);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var c = start.Compilation;
            var attribute = c.GetTypeByMetadataName("Kiln.RequiresCapabilityAttribute");
            var capabilities = c.GetTypeByMetadataName("Kiln.Capabilities");
            if (attribute is null || capabilities is null) return;
            var unavailable = (INamedTypeSymbol?)c.GetTypeByMetadataName("Kiln.KilnUnavailableException") ?? c.GetTypeByMetadataName("System.NotSupportedException");
            var state = new State(c, attribute, capabilities, unavailable, Features(start.Options));
            start.RegisterOperationAction(state.Analyze, OperationKind.Invocation, OperationKind.PropertyReference,
                OperationKind.EventAssignment, OperationKind.ObjectCreation);
        });
    }

    /// <summary>The rows the project's kiln.mod.json requires ("features"), and KilnFeatures, which the SDK passes as
    /// KilnFeaturesList (comma-separated: .editorconfig cuts a value at ';'): Kiln does not load the mod when one of them
    /// is off.</summary>
    private static HashSet<string> Features(AnalyzerOptions options)
    {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in options.AdditionalFiles)
        {
            if (!string.Equals(Path.GetFileName(file.Path), "kiln.mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.GetText()?.ToString() is not { } text || FeaturesKey.Match(text) is not { Success: true } m) continue;
            foreach (Match q in Quoted.Matches(m.Groups[1].Value)) rows.Add(q.Groups[1].Value);
        }
        var global = options.AnalyzerConfigOptionsProvider.GlobalOptions;
        if (global.TryGetValue("build_property.KilnFeaturesList", out var list) || global.TryGetValue("build_property.KilnFeatures", out list))
            foreach (var f in list.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (f.Trim() is { Length: > 0 } t) rows.Add(t);
        return rows;
    }

    private sealed class State(Compilation compilation, INamedTypeSymbol attribute, INamedTypeSymbol capabilities,
        INamedTypeSymbol? unavailable, HashSet<string> features)
    {
        private readonly ConcurrentDictionary<ISymbol, string[]> _rows = new(SymbolEqualityComparer.Default);
        private readonly ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<string, HashSet<IMethodSymbol>>> _covered =
            new(SymbolEqualityComparer.Default);

        public void Analyze(OperationAnalysisContext ctx)
        {
            var op = ctx.Operation;
            var member = TickThreadAnalyzer.Member(op);
            // an init accessor (a block record's Animated = ...) shapes a value; the row matters where the value is used
            if (member is null || member is IMethodSymbol { IsInitOnly: true } || InNameOf(op)) return;
            var rows = RowsOf(member);
            if (rows.Length == 0) return;
            var model = op.SemanticModel!;
            var node = op.Syntax;
            var handedIn = HandedIn(Receiver(op), member, model, ctx.CancellationToken);
            var missing = rows.Where(row => !features.Contains(row) && !handedIn && !FromSameRow(Receiver(op), row, model, ctx.CancellationToken, 0) &&
                                            !Guarded(node, row, model, ctx.CancellationToken) &&
                                            !CoveredByCallers(node, row, model, ctx.CancellationToken)).ToList();
            if (missing.Count == 0) return;
            var props = ImmutableDictionary<string, string?>.Empty.Add(RowsKey, string.Join(",", missing));
            if (Wrappable(node)) props = props.Add(FixKey, "wrap");
            ctx.ReportDiagnostic(Diagnostic.Create(Descriptors.CapabilityUnchecked, ResultAnalyzer.ReportAt(node), props,
                Syntax.Display(member), string.Join(" and ", missing), string.Join(") and Capabilities.Is(", missing.Select(r => $"\"{r}\""))));
        }

        /// <summary>The rows <paramref name="s"/> needs: [RequiresCapability] on it, on the property or event of an
        /// accessor, and on the types around it.</summary>
        public string[] RowsOf(ISymbol s) => _rows.GetOrAdd(s, m =>
        {
            if (m is IMethodSymbol { ReducedFrom: { } reduced }) m = reduced;
            m = m.OriginalDefinition;
            var rows = new List<string>();
            Add(m);
            if (m is IMethodSymbol { AssociatedSymbol: { } assoc }) Add(assoc);
            for (var t = m.ContainingType; t != null; t = t.ContainingType) Add(t);
            return rows.Distinct(StringComparer.Ordinal).ToArray();

            void Add(ISymbol x)
            {
                foreach (var a in x.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute) &&
                        a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is string row && row.Length > 0)
                        rows.Add(row);
            }
        });

        private static bool InNameOf(IOperation op)
        {
            for (var p = op.Parent; p != null; p = p.Parent)
                if (p is INameOfOperation) return true;
            return false;
        }

        // ---------------------------------------------------------------------------------------- in reach ---------

        /// <summary>True when something around <paramref name="node"/> in its member asks for <paramref name="row"/>
        /// (or catches its exception, or the member passes the requirement on).</summary>
        private bool Guarded(SyntaxNode node, string row, SemanticModel model, CancellationToken ct)
        {
            var child = node;
            for (var parent = node.Parent; parent != null; child = parent, parent = parent.Parent)
            {
                switch (parent)
                {
                    case IfStatementSyntax ifs:
                        if (child == ifs.Statement && IsTest(ifs.Condition, row, true, model, ct, 0)) return true;
                        if (child == ifs.Else && IsTest(ifs.Condition, row, false, model, ct, 0)) return true;
                        break;
                    case ConditionalExpressionSyntax ce:
                        if (child == ce.WhenTrue && IsTest(ce.Condition, row, true, model, ct, 0)) return true;
                        if (child == ce.WhenFalse && IsTest(ce.Condition, row, false, model, ct, 0)) return true;
                        break;
                    case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } conj:
                        if (child == conj.Right && IsTest(conj.Left, row, true, model, ct, 0)) return true;
                        break;
                    case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression } disj:
                        if (child == disj.Right && IsTest(disj.Left, row, false, model, ct, 0)) return true;
                        break;
                    case WhileStatementSyntax ws:
                        if (child == ws.Statement && IsTest(ws.Condition, row, true, model, ct, 0)) return true;
                        break;
                    case TryStatementSyntax ts:
                        if (child == ts.Block && Catches(ts, model, ct)) return true;
                        break;
                    case BlockSyntax b:
                        if (LeavesBefore(b.Statements, child, row, model, ct)) return true;
                        break;
                    case SwitchSectionSyntax sec:
                        if (LeavesBefore(sec.Statements, child, row, model, ct)) return true;
                        break;
                    case LocalFunctionStatementSyntax:
                        // a local function runs where it is called, not where it is written: what precedes its
                        // declaration says nothing about that. Marked with the row, it passes it on; otherwise its calls
                        // are judged (CoveredByCallers).
                        return model.GetDeclaredSymbol(parent, ct) is { } fn && RowsOf(fn).Contains(row);
                    case AnonymousFunctionExpressionSyntax:
                        // a lambda marked [RequiresCapability] passes it on; otherwise look further out (the code around
                        // it ran its checks before the delegate was made: when in doubt, say nothing)
                        if (model.GetSymbolInfo(parent, ct).Symbol is IMethodSymbol lambda && RowsOf(lambda).Contains(row)) return true;
                        // a callback handed to a member that needs the row (Content.OnReady(_ => ...)): it is only kept,
                        // and so only runs, when that call went through
                        if (parent.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: { } call } } &&
                            model.GetSymbolInfo(call, ct).Symbol is { } taker && RowsOf(taker).Contains(row))
                            return true;
                        break;
                    case MemberDeclarationSyntax md:
                        // the member (method, accessor's property, ctor, field initializer's type) passes the requirement on
                        var declared = md is BaseFieldDeclarationSyntax f && f.Declaration.Variables.Count > 0
                            ? model.GetDeclaredSymbol(f.Declaration.Variables[0], ct)
                            : model.GetDeclaredSymbol(md, ct);
                        if (declared is not null && RowsOf(declared).Contains(row)) return true;
                        if (declared is IMethodSymbol method && IsTestMethod(method)) return true;
                        if (md is BaseTypeDeclarationSyntax) return false;
                        break;
                    case AccessorDeclarationSyntax acc:
                        if (model.GetDeclaredSymbol(acc, ct) is { } accessor && RowsOf(accessor).Contains(row)) return true;
                        break;
                }
            }
            return false;
        }

        /// <summary>A [KilnTest] or [ClientCheck] method: Kiln's test runner reports whatever it throws as the test's
        /// failure, which is what a test that needs a capability that is off should show.</summary>
        private static bool IsTestMethod(IMethodSymbol m)
        {
            foreach (var a in m.GetAttributes())
                if (a.AttributeClass is { Name: "KilnTestAttribute" or "ClientCheckAttribute" } c &&
                    c.ContainingNamespace?.ToDisplayString() is "Kiln.Testing" or "Kiln")
                    return true;
            return false;
        }

        /// <summary>An earlier statement of the same list leaves when the row is off: <c>if (!Capabilities.Is(row))
        /// return;</c> (throw, continue, break, ...), or <c>if (Capabilities.Is(row)) { ... } else return;</c>.</summary>
        private bool LeavesBefore(SyntaxList<StatementSyntax> statements, SyntaxNode child, string row, SemanticModel model, CancellationToken ct)
        {
            if (child is not StatementSyntax stmt) return false;
            var index = statements.IndexOf(stmt);
            for (var i = 0; i < index; i++)
            {
                if (statements[i] is not IfStatementSyntax ifs) continue;
                if (IsTest(ifs.Condition, row, false, model, ct, 0) && !EndReachable(ifs.Statement, model)) return true;
                if (ifs.Else is { } e && IsTest(ifs.Condition, row, true, model, ct, 0) && !EndReachable(e.Statement, model)) return true;
            }
            return false;
        }

        private static bool EndReachable(StatementSyntax s, SemanticModel model)
        {
            switch (s)
            {
                case ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax or GotoStatementSyntax:
                    return false;
                case YieldStatementSyntax y when y.IsKind(SyntaxKind.YieldBreakStatement):
                    return false;
                case BlockSyntax { Statements.Count: 0 }:
                    return true;
            }
            try
            {
                var flow = model.AnalyzeControlFlow(s);
                return flow is null || !flow.Succeeded || flow.EndPointIsReachable;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return true;  // not a region the compiler can analyze: assume it goes on
            }
        }

        private bool Catches(TryStatementSyntax ts, SemanticModel model, CancellationToken ct)
        {
            foreach (var clause in ts.Catches)
            {
                if (clause.Declaration is null) return true;  // catch { }
                if (model.GetTypeInfo(clause.Declaration.Type, ct).Type is not { } caught) continue;
                for (INamedTypeSymbol? t = unavailable; t != null; t = t.BaseType)
                    if (SymbolEqualityComparer.Default.Equals(t, caught)) return true;  // a filter (when ...) is not judged
            }
            return false;
        }

        /// <summary>True when <paramref name="e"/> being <paramref name="positive"/> means <paramref name="row"/> is on
        /// (or the author looked at the row): Capabilities.Is(row), a guard property, a bool local or own property set from
        /// one; !, &amp;&amp;, ||, <c>is true/false</c> and <c>== true/false</c> around them.</summary>
        private bool IsTest(ExpressionSyntax e, string row, bool positive, SemanticModel model, CancellationToken ct, int depth)
        {
            while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
            switch (e)
            {
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } neg:
                    return IsTest(neg.Operand, row, !positive, model, ct, depth);
                // && when true and || when false hold for each side. The other two (`if (!on && !NoClip.Available)
                // return;` in mods/samples/AdminTools, where turning NoClip off never needs the row) mean the author looked
                // at the row and knows when it matters: when in doubt, the rule says nothing.
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression or (int)SyntaxKind.LogicalOrExpression } both:
                    return IsTest(both.Left, row, positive, model, ct, depth) || IsTest(both.Right, row, positive, model, ct, depth);
                case IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax lit } } isp
                    when lit.IsKind(SyntaxKind.TrueLiteralExpression) || lit.IsKind(SyntaxKind.FalseLiteralExpression):
                    return IsTest(isp.Expression, row, positive == lit.IsKind(SyntaxKind.TrueLiteralExpression), model, ct, depth);
                case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression or (int)SyntaxKind.NotEqualsExpression } eq
                    when eq.Right is LiteralExpressionSyntax r && (r.IsKind(SyntaxKind.TrueLiteralExpression) || r.IsKind(SyntaxKind.FalseLiteralExpression)):
                    return IsTest(eq.Left, row, positive == (r.IsKind(SyntaxKind.TrueLiteralExpression) == eq.IsKind(SyntaxKind.EqualsExpression)), model, ct, depth);
            }
            // a bool local set once, or a bool property of the mod: what it was set from, the same way round
            // (`bool off = !NoClip.Available; if (off) return;`)
            if (depth < 2 && Definition(e, model, ct) is { } def)
                return IsTest(def.Expression, row, positive, def.Model, ct, depth + 1);
            return positive && Asks(e, row, model, ct);
        }

        /// <summary>One term that is true only when the row is on.</summary>
        private bool Asks(ExpressionSyntax e, string row, SemanticModel model, CancellationToken ct)
        {
            // Capabilities.Get(row).IsAvailable
            if (e is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "IsAvailable", Expression: InvocationExpressionSyntax get } &&
                IsCapabilitiesCall(get, "Get", row, model, ct))
                return true;
            if (e is InvocationExpressionSyntax inv) return IsCapabilitiesCall(inv, "Is", row, model, ct);
            return model.GetSymbolInfo(e, ct).Symbol is IPropertySymbol prop && KilnApis.GuardRows(prop).Contains(row);
        }

        /// <summary>What a bool name stands for: the initializer of a local written only where it is declared, or the
        /// getter of a property of the mod (<c>=> e</c>, <c>get => e</c>, <c>get { return e; }</c>).</summary>
        private (ExpressionSyntax Expression, SemanticModel Model)? Definition(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            if (e is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) return null;
            switch (model.GetSymbolInfo(e, ct).Symbol)
            {
                case IPropertySymbol { Type.SpecialType: SpecialType.System_Boolean } prop:
                    return Source(prop, ct);
                case ILocalSymbol { Type.SpecialType: SpecialType.System_Boolean } local
                    when local.DeclaringSyntaxReferences.Length == 1 &&
                         local.DeclaringSyntaxReferences[0].GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: { } init } decl &&
                         decl.SyntaxTree == model.SyntaxTree && !WrittenAgain(local, decl, model, ct):
                    return (init, model);
                default:
                    return null;
            }
        }

        private bool IsCapabilitiesCall(InvocationExpressionSyntax inv, string name, string row, SemanticModel model, CancellationToken ct) =>
            model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol { Parameters.Length: 1 } m && m.Name == name &&
            SymbolEqualityComparer.Default.Equals(m.ContainingType, capabilities) &&
            inv.ArgumentList.Arguments.Count == 1 && RowOf(inv.ArgumentList.Arguments[0].Expression, model, ct) == row;

        /// <summary>The string an argument names: a constant (literal, const), or a static readonly string field of the
        /// mod initialized with one.</summary>
        private string? RowOf(ExpressionSyntax e, SemanticModel model, CancellationToken ct)
        {
            if (model.GetConstantValue(e, ct) is { HasValue: true, Value: string s }) return s;
            if (model.GetSymbolInfo(e, ct).Symbol is IFieldSymbol { IsStatic: true, IsReadOnly: true, Type.SpecialType: SpecialType.System_String } f &&
                f.DeclaringSyntaxReferences.Length == 1 && f.DeclaringSyntaxReferences[0].GetSyntax(ct) is VariableDeclaratorSyntax { Initializer.Value: { } init } &&
                compilation.SyntaxTrees.Contains(init.SyntaxTree))
            {
#pragma warning disable RS1030 // the field's own tree: the initializer is a constant expression
                var m = init.SyntaxTree == model.SyntaxTree ? model : compilation.GetSemanticModel(init.SyntaxTree);
#pragma warning restore RS1030
                if (m.GetConstantValue(init, ct) is { HasValue: true, Value: string v }) return v;
            }
            return null;
        }

        private (ExpressionSyntax Expression, SemanticModel Model)? Source(IPropertySymbol p, CancellationToken ct)
        {
            if (p.DeclaringSyntaxReferences.Length != 1 || p.DeclaringSyntaxReferences[0].GetSyntax(ct) is not PropertyDeclarationSyntax pd ||
                !compilation.SyntaxTrees.Contains(pd.SyntaxTree))
                return null;
            var get = pd.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var e = pd.ExpressionBody?.Expression ?? get?.ExpressionBody?.Expression ??
                    (get?.Body is { Statements.Count: 1 } b && b.Statements[0] is ReturnStatementSyntax { Expression: { } r } ? r : null);
            if (e is null) return null;
#pragma warning disable RS1030 // the property's own tree
            return (e, compilation.GetSemanticModel(pd.SyntaxTree));
#pragma warning restore RS1030
        }

        private static bool WrittenAgain(ILocalSymbol local, VariableDeclaratorSyntax decl, SemanticModel model, CancellationToken ct)
        {
            var body = Syntax.FunctionBody(Syntax.EnclosingFunction(decl));
            if (body is null) return true;
            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Identifier.ValueText != local.Name) continue;
                var written = id.Parent is AssignmentExpressionSyntax a && a.Left == id ||
                              id.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword };
                if (written && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id, ct).Symbol, local)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------ the object it is used on -----

        /// <summary>What the member is used on: the instance, or the first argument of an extension method.</summary>
        private static IOperation? Receiver(IOperation op) => op switch
        {
            IInvocationOperation i => i.Instance ?? (i.TargetMethod.IsExtensionMethod && i.Arguments.Length > 0 ? i.Arguments[0].Value : null),
            IPropertyReferenceOperation p => p.Instance,
            IEventAssignmentOperation { EventReference: IEventReferenceOperation e } => e.Instance,
            _ => null,
        };

        /// <summary>True when the object is a parameter of a lambda handed to Kiln (an argument of a Kiln member, a Kiln
        /// delegate type, or a property of a Kiln block) and is of a type of the member's own assembly (an NpcView in a
        /// creature rule's <c>Act.Custom(npc => ...)</c>): Kiln made it and handed it over, so its service runs.</summary>
        private static bool HandedIn(IOperation? receiver, ISymbol member, SemanticModel model, CancellationToken ct)
        {
            while (receiver is IConversionOperation c) receiver = c.Operand;
            if (receiver is not IParameterReferenceOperation { Parameter: { ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }, Type: { } type } p })
                return false;
            if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, member.ContainingAssembly)) return false;
            var lambda = p.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct).FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
            if (lambda is null || lambda.SyntaxTree != model.SyntaxTree) return false;
            var taker = lambda.Parent switch
            {
                ArgumentSyntax { Parent: ArgumentListSyntax { Parent: { } call } } => model.GetSymbolInfo(call, ct).Symbol,
                AssignmentExpressionSyntax { Left: { } left } a when a.Right == lambda => model.GetSymbolInfo(left, ct).Symbol,
                _ => null,
            };
            return IsKiln(taker?.ContainingAssembly) ||
                   model.GetTypeInfo(lambda, ct).ConvertedType is INamedTypeSymbol { TypeKind: TypeKind.Delegate } d && IsKiln(d.ContainingAssembly);
        }

        private static bool IsKiln(IAssemblySymbol? a) => a?.Name.StartsWith("Kiln", StringComparison.Ordinal) == true;

        /// <summary>True when the object came from a use of a member that needs the same row: that use ran first, so the
        /// row was on (the first use is the one reported). Follows a chain of calls and properties, ?., and a local that
        /// is set once from such a chain (its declaration, a foreach over it, a pattern on it).</summary>
        private bool FromSameRow(IOperation? o, string row, SemanticModel model, CancellationToken ct, int depth)
        {
            for (var i = 0; o != null && i < 16; i++)
            {
                switch (o)
                {
                    case IConversionOperation c:
                        o = c.Operand;
                        continue;
                    case IParenthesizedOperation p:
                        o = p.Operand;
                        continue;
                    case IConditionalAccessInstanceOperation:
                        var access = o.Parent;
                        while (access is not null and not IConditionalAccessOperation) access = access.Parent;
                        o = (access as IConditionalAccessOperation)?.Operation;
                        continue;
                    case IInvocationOperation inv:
                        if (RowsOf(inv.TargetMethod).Contains(row)) return true;
                        o = Receiver(inv);
                        continue;
                    case IPropertyReferenceOperation pr:
                        if (TickThreadAnalyzer.Member(pr) is { } getter && RowsOf(getter).Contains(row)) return true;
                        o = pr.Instance;
                        continue;
                    case IObjectCreationOperation oc:
                        return oc.Constructor is { } ctor && RowsOf(ctor).Contains(row);
                    case IFieldReferenceOperation fr:
                        o = fr.Instance;
                        continue;
                    case IArrayElementReferenceOperation ae:
                        o = ae.ArrayReference;
                        continue;
                    case ILocalReferenceOperation lr:
                        return depth < 2 && LocalFromSameRow(lr.Local, row, model, ct, depth + 1);
                    default:
                        return false;
                }
            }
            return false;
        }

        private bool LocalFromSameRow(ILocalSymbol local, string row, SemanticModel model, CancellationToken ct, int depth)
        {
            if (local.DeclaringSyntaxReferences.Length != 1) return false;
            var decl = local.DeclaringSyntaxReferences[0].GetSyntax(ct);
            if (decl.SyntaxTree != model.SyntaxTree) return false;
            ExpressionSyntax? from = decl switch
            {
                VariableDeclaratorSyntax { Initializer.Value: { } init } => init,
                ForEachStatementSyntax fe => fe.Expression,
                SingleVariableDesignationSyntax d => d.FirstAncestorOrSelf<IsPatternExpressionSyntax>()?.Expression,
                _ => null,
            };
            if (from is null || decl is VariableDeclaratorSyntax vd && WrittenAgain(local, vd, model, ct)) return false;
            return FromSameRow(model.GetOperation(from, ct), row, model, ct, depth);
        }

        // ------------------------------------------------------------------------------ in reach of the callers ------

        /// <summary>True when the private method or local function around <paramref name="node"/> is only ever called,
        /// and every call is in reach of <paramref name="row"/> (or is itself in such a method).</summary>
        private bool CoveredByCallers(SyntaxNode node, string row, SemanticModel model, CancellationToken ct)
        {
            var fn = node.Ancestors().FirstOrDefault(a => a is LocalFunctionStatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax);
            if (fn is not (LocalFunctionStatementSyntax or MethodDeclarationSyntax)) return false;
            if (model.GetDeclaredSymbol(fn, ct) is not IMethodSymbol m || m.ContainingType is not { } type) return false;
            var byRow = _covered.GetOrAdd(type, _ => new ConcurrentDictionary<string, HashSet<IMethodSymbol>>(StringComparer.Ordinal));
            return byRow.GetOrAdd(row, r => Covered(type, r, ct)).Contains(m);
        }

        /// <summary>The private methods and local functions of <paramref name="type"/> that are in reach of
        /// <paramref name="row"/> through their callers: used at least once, carrying no attribute a caller outside the
        /// source could go by, and every call or method group of them in reach (greatest fixpoint, so a method that calls
        /// itself does not lose its cover by that call).</summary>
        private HashSet<IMethodSymbol> Covered(INamedTypeSymbol type, string row, CancellationToken ct)
        {
            var decls = Syntax.Declarations(type, compilation, ct).ToList();
            var candidates = new Dictionary<IMethodSymbol, List<(SyntaxNode Site, SemanticModel Model, IMethodSymbol? Caller)>>(SymbolEqualityComparer.Default);
            foreach (var (root, model) in decls)
            {
                foreach (var n in root.DescendantNodes())
                {
                    if (n is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax)) continue;
                    if (model.GetDeclaredSymbol(n, ct) is IMethodSymbol m && IsCandidate(m, type))
                        candidates[m] = new List<(SyntaxNode, SemanticModel, IMethodSymbol?)>();
                }
            }
            if (candidates.Count == 0) return new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
            var names = new HashSet<string>(candidates.Keys.Select(c => c.Name), StringComparer.Ordinal);
            foreach (var (root, model) in decls)
            {
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    if (!names.Contains(name.Identifier.ValueText) || name.Parent is MethodDeclarationSyntax or LocalFunctionStatementSyntax) continue;
                    var info = model.GetSymbolInfo(name, ct);
                    var target = (info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null)) as IMethodSymbol;
                    if (target is null || !candidates.TryGetValue(target.OriginalDefinition, out var sites)) continue;
                    ExpressionSyntax expr = name.Parent is MemberAccessExpressionSyntax ma && ma.Name == name ? ma : name;
                    if (expr.Parent is InvocationExpressionSyntax inv && inv.Expression == expr)
                        sites.Add((inv, model, Caller(inv, model, ct)));
                    else if (expr.Ancestors().Any(a => a is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } }))
                        continue;
                    else
                        // a method group (Context.Tick.Pre += OnPre): a delegate made here, called later by whoever got it;
                        // like a lambda written in its place, it is in reach when this spot is
                        sites.Add((expr, model, Caller(expr, model, ct)));
                }
            }
            var covered = new HashSet<IMethodSymbol>(candidates.Where(c => c.Value.Count > 0).Select(c => c.Key), SymbolEqualityComparer.Default);
            bool changed;
            do
            {
                changed = false;
                foreach (var m in covered.ToList())
                {
                    foreach (var (site, model, caller) in candidates[m])
                    {
                        if (caller is not null && covered.Contains(caller)) continue;
                        if (Guarded(site, row, model, ct)) continue;
                        covered.Remove(m);
                        changed = true;
                        break;
                    }
                }
            } while (changed);
            return covered;
        }

        private static IMethodSymbol? Caller(SyntaxNode site, SemanticModel model, CancellationToken ct)
        {
            var fn = site.Ancestors().FirstOrDefault(a => a is LocalFunctionStatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax);
            return fn is LocalFunctionStatementSyntax or MethodDeclarationSyntax ? model.GetDeclaredSymbol(fn, ct) as IMethodSymbol : null;
        }

        private bool IsCandidate(IMethodSymbol m, INamedTypeSymbol type)
        {
            if (!SymbolEqualityComparer.Default.Equals(m.ContainingType, type)) return false;
            if (m.MethodKind == MethodKind.LocalFunction) { }
            else if (m.MethodKind != MethodKind.Ordinary || m.DeclaredAccessibility != Accessibility.Private ||
                     m.IsAbstract || m.IsVirtual || m.IsOverride || m.IsExtern || m.IsPartialDefinition || m.PartialImplementationPart is not null)
                return false;
            // [Command], [Subscribe], [KilnTest], ...: Kiln (or another caller outside the source) calls it
            foreach (var a in m.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute)) continue;
                var ns = a.AttributeClass?.ContainingNamespace?.ToDisplayString() ?? "";
                if (!(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------- code fix -------

        /// <summary>The use is the whole of an expression statement (a call, an awaited call, <c>_ = call</c>, a
        /// property set) that declares no variable: the fix can put the statement under <c>if (Capabilities.Is(row))</c>.</summary>
        private static bool Wrappable(SyntaxNode node)
        {
            var e = node as ExpressionSyntax;
            if (e is null) return false;
            while (e.Parent is AwaitExpressionSyntax or ParenthesizedExpressionSyntax) e = (ExpressionSyntax)e.Parent;
            if (e.Parent is AssignmentExpressionSyntax a && (a.Right == e && a.Left is IdentifierNameSyntax { Identifier.ValueText: "_" } ||
                                                             a.Left == e && a.IsKind(SyntaxKind.SimpleAssignmentExpression)))
                e = a;
            // a variable the statement declares (out var h, a pattern's x) is used after it: inside the new block it is gone
            return e.Parent is ExpressionStatementSyntax es && es.Expression == e && es.Parent is BlockSyntax or SwitchSectionSyntax &&
                   !es.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax)
                       .Any(n => n is DeclarationExpressionSyntax or SingleVariableDesignationSyntax or ParenthesizedVariableDesignationSyntax);
        }
    }
}
