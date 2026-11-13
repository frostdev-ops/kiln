// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kiln.Analyzers.Tools;

/// <summary>KILN121's guard table: the public static bool properties (of any name) that answer the same question
/// as <c>Capabilities.Is(row)</c> for a row, so a mod that asks them has asked for the row. Only the property's own code
/// counts, and only a term it always asks: its getter is an <c>&amp;&amp;</c> of terms, and a term is
/// <c>Capabilities.Is(row)</c>, a one-line helper that returns <c>Capabilities.Is(its argument)</c> called with the row,
/// <c>T != null</c> for a native table <c>T</c> that comes from <c>Services.Get&lt;...&gt;("kiln.x", ...)</c> (which
/// returns the table exactly when it is there and records the core's <c>kiln.x</c> row available: Services.cs, Get and
/// Found), or another guard. A summary is not code: what it names makes no guard, and a getter that asks one row on
/// one side and another on the other (<c>ServerSide ? relay : local</c>) asks neither always.</summary>
internal static class ApiScanGuards
{
    internal sealed class Guard
    {
        public readonly SortedSet<string> Rows = new(StringComparer.Ordinal);
        /// <summary>Documentation ids of other bool properties this one is an &amp;&amp; of (resolved after the scan).</summary>
        public readonly SortedSet<string> Via = new(StringComparer.Ordinal);
        public readonly List<string> Why = new();
    }

    /// <summary>The guard facts of one property declaration; null when it is no candidate (not a static bool with a
    /// getter) or its getter always asks no row.</summary>
    internal static Guard? Read(SemanticModel model, PropertyDeclarationSyntax pd, CancellationToken ct = default)
    {
        // any name: what the getter asks decides (World.SupportsTrackedWrites is WorldCapabilities.Is(TrackedWritesCapability))
        if (model.GetDeclaredSymbol(pd, ct) is not { IsStatic: true, Type.SpecialType: SpecialType.System_Boolean, GetMethod: not null })
            return null;
        var g = new Guard();
        if (Getter(pd) is { } expr)
        {
            foreach (var term in Terms(expr))
            {
                switch (model.GetSymbolInfo(term, ct).Symbol)
                {
                    case IMethodSymbol m when term is InvocationExpressionSyntax inv && (IsCapabilitiesIs(m) || WrapsIs(m, model.Compilation, ct)) &&
                                              Constant(model, inv, ct) is { } row:
                        if (g.Rows.Add(row)) g.Why.Add($"{m.Name}(\"{row}\")");
                        break;
                    case IPropertySymbol { IsStatic: true, Type.SpecialType: SpecialType.System_Boolean } q when q.GetDocumentationCommentId() is { } qid:
                        g.Via.Add(qid);
                        break;
                    default:
                        if (NotNull(term) is { } table && model.GetSymbolInfo(table, ct).Symbol is { } t && TableRow(t, model.Compilation, ct) is { } service)
                            if (g.Rows.Add(service)) g.Why.Add($"{table} != null: Services.Get(\"{service}\")");
                        break;
                }
            }
        }
        // { var t = AiNative.T; if (t == null) return false; ...; return ...; }: every other answer passed the check
        if (NullCheckFirst(pd, model, ct) is { } checkedRow && g.Rows.Add(checkedRow)) g.Why.Add($"returns false first when the table of {checkedRow} is null");
        return g.Rows.Count > 0 || g.Via.Count > 0 ? g : null;
    }

    /// <summary>The row of the table a block getter tests first: before <c>if (t == null) return false;</c> (t a table, or
    /// a local set from one) there are only declarations and plain statements, no return; so any answer the getter gives
    /// after that passed the check. Null for any other shape (a cached answer returned before the check: Saves.Available's
    /// SavesRuntime.Ensure()).</summary>
    private static string? NullCheckFirst(PropertyDeclarationSyntax pd, SemanticModel model, CancellationToken ct)
    {
        var get = pd.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (get?.Body is not { } body) return null;
        foreach (var s in body.Statements)
        {
            if (s is IfStatementSyntax { Else: null } ifs && NullTest(ifs.Condition) is { } tested &&
                ifs.Statement is ReturnStatementSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.FalseLiteralExpression } } or
                    BlockSyntax { Statements: [.., ReturnStatementSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.FalseLiteralExpression } }] } &&
                model.GetSymbolInfo(tested, ct).Symbol is { } table)
                return TableRow(table, model.Compilation, ct);
            if (s is not (LocalDeclarationStatementSyntax or ExpressionStatementSyntax) ||
                s.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax).OfType<ReturnStatementSyntax>().Any())
                return null;
        }
        return null;
    }

    // `x == null`, `null == x`, `x is null`: x
    private static ExpressionSyntax? NullTest(ExpressionSyntax e) => e switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression, Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } b => b.Left,
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression, Left: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } b => b.Right,
        IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } } i => i.Expression,
        _ => null,
    };

    // `x != null`, `null != x`, `x is not null`: x
    private static ExpressionSyntax? NotNull(ExpressionSyntax e) => e switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression, Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } b => b.Left,
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression, Left: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } b => b.Right,
        IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } } } i => i.Expression,
        _ => null,
    };

    /// <summary>The service a native table property, field or local holds: the one constant name its declaration asks
    /// for with <c>Services.Get&lt;T&gt;(name, ...)</c> or <c>Backend.Current.Service(name, ...)</c> (what Services.Get
    /// calls), also through the other tables, table properties and methods it reads (<c>T => BuildNative.T</c>,
    /// <c>_table = PlayerAnimNative.Get()</c>, <c>var t = CreaturesNative.T;</c>), up to three steps; null when it asks
    /// for none or for more than one.</summary>
    internal static string? TableRow(ISymbol table, Compilation compilation, CancellationToken ct = default)
    {
        // the value tested must be a table itself (a pointer), not something read from one (Attributes' Ids(...) is not null)
        var type = table switch { IPropertySymbol p => p.Type, IFieldSymbol f => f.Type, ILocalSymbol l => l.Type, IMethodSymbol m => m.ReturnType, _ => null };
        if (type is not IPointerTypeSymbol) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        return Collect(table, 0) && names.Count == 1 ? names.First() : null;

        // false: a name that is not a constant (nothing can be said)
        bool Collect(ISymbol symbol, int depth)
        {
            if (depth > 3 || !seen.Add(symbol)) return true;
            if (symbol is not (IPropertySymbol { IsStatic: true } or IFieldSymbol { IsStatic: true } or IMethodSymbol { IsStatic: true } or ILocalSymbol))
                return true;
            foreach (var r in symbol.DeclaringSyntaxReferences)
            {
                var decl = r.GetSyntax(ct);
                if (decl is VariableDeclaratorSyntax && symbol is IFieldSymbol) decl = decl.Parent?.Parent ?? decl;  // a field: its declaration
                if (!compilation.SyntaxTrees.Contains(decl.SyntaxTree)) continue;
                var model = compilation.GetSemanticModel(decl.SyntaxTree);
                foreach (var n in decl.DescendantNodesAndSelf())
                {
                    if (n is InvocationExpressionSyntax inv && model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol m)
                    {
                        var asks = m is { Name: "Get", IsGenericMethod: true } && m.ContainingType?.ToDisplayString() == "Kiln.Services" ||
                                   m.Name == "Service" && m.ContainingType?.Name is "IKilnBackend";
                        if (asks)
                        {
                            if (inv.ArgumentList.Arguments.Count < 1 ||
                                model.GetConstantValue(inv.ArgumentList.Arguments[0].Expression, ct) is not { HasValue: true, Value: string name })
                                return false;
                            names.Add(name);
                        }
                        else if (m.IsStatic && SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, compilation.Assembly) &&
                                 m.ReturnType is IPointerTypeSymbol && !Collect(m.OriginalDefinition, depth + 1))
                            return false;
                    }
                    else if (n is IdentifierNameSyntax id && model.GetSymbolInfo(id, ct).Symbol is IPropertySymbol { IsStatic: true, Type: IPointerTypeSymbol } other &&
                             SymbolEqualityComparer.Default.Equals(other.ContainingAssembly, compilation.Assembly) && !Collect(other, depth + 1))
                        return false;
                }
            }
            return true;
        }
    }

    // `=> e`, `{ get => e; }`, `{ get { return e; } }` (one return; locals before it are fine)
    private static ExpressionSyntax? Getter(PropertyDeclarationSyntax pd)
    {
        if (pd.ExpressionBody is { } eb) return eb.Expression;
        var get = pd.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (get?.ExpressionBody is { } geb) return geb.Expression;
        var returns = get?.Body?.Statements.OfType<ReturnStatementSyntax>().ToList();
        return returns is [{ Expression: { } r }] ? r : null;
    }

    /// <summary>The terms of an &amp;&amp; chain (parentheses removed).</summary>
    internal static IEnumerable<ExpressionSyntax> Terms(ExpressionSyntax e)
    {
        while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
        if (e is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } conj)
            return Terms(conj.Left).Concat(Terms(conj.Right));
        return new[] { e };
    }

    private static bool IsCapabilitiesIs(IMethodSymbol m) =>
        m is { Name: "Is", Parameters.Length: 1 } && m.ContainingType?.ToDisplayString() == "Kiln.Capabilities";

    // private static bool Can(string capability) => Capabilities.Is(capability);
    private static bool WrapsIs(IMethodSymbol m, Compilation compilation, CancellationToken ct)
    {
        if (m.Parameters is not [{ Type.SpecialType: SpecialType.System_String } parameter] || m.ReturnType.SpecialType != SpecialType.System_Boolean) return false;
        foreach (var r in m.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax(ct) is not MethodDeclarationSyntax md || !compilation.SyntaxTrees.Contains(md.SyntaxTree)) continue;
            // `=> Capabilities.Is(x)`, or a body whose last statement returns it after plain calls
            // (WorldCapabilities.Is: `{ Ensure(); return Capabilities.Is(capability); }`)
            var statements = md.Body?.Statements ?? default;
            var body = md.ExpressionBody?.Expression ??
                       (statements.Count > 0 && statements[statements.Count - 1] is ReturnStatementSyntax { Expression: { } e } &&
                        statements.Take(statements.Count - 1).All(s => s is ExpressionStatementSyntax) ? e : null);
            if (body is not InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax arg }] } inv) return false;
            var model = compilation.GetSemanticModel(md.SyntaxTree);
            return model.GetSymbolInfo(inv, ct).Symbol is IMethodSymbol inner && IsCapabilitiesIs(inner) &&
                   SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(arg, ct).Symbol, parameter);
        }
        return false;
    }

    private static string? Constant(SemanticModel model, InvocationExpressionSyntax inv, CancellationToken ct) =>
        inv.ArgumentList.Arguments is [{ } a] && model.GetConstantValue(a.Expression, ct) is { HasValue: true, Value: string s } ? s : null;

    /// <summary>Rows of every guard with the ones it reaches through other guards (any assembly), by documentation id.</summary>
    internal static SortedDictionary<string, (SortedSet<string> Rows, string Why)> Resolve(IReadOnlyDictionary<string, Guard> guards)
    {
        var result = new SortedDictionary<string, (SortedSet<string> Rows, string Why)>(StringComparer.Ordinal);
        foreach (var (id, g) in guards)
        {
            var rows = new SortedSet<string>(g.Rows, StringComparer.Ordinal);
            var why = new List<string>(g.Why);
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var todo = new Queue<string>(g.Via);
            while (todo.Count > 0)
            {
                var via = todo.Dequeue();
                if (!seen.Add(via) || !guards.TryGetValue(via, out var other)) continue;
                foreach (var row in other.Rows)
                    if (rows.Add(row)) why.Add($"via {via.Substring(2)}");
                foreach (var next in other.Via) todo.Enqueue(next);
            }
            if (rows.Count > 0) result[id] = (rows, string.Join("; ", why.Distinct()));
        }
        return result;
    }
}
