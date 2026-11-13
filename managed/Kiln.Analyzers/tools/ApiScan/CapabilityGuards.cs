// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Kiln.Analyzers.Tools;

/// <summary>KILN121's guard table: the public static bool properties named "...Available" or "...Ready" that answer the same question
/// as <c>Capabilities.Is(row)</c> for a row, so a mod that asks them has asked for the row. A property is a guard for a
/// row when its getter is (an <c>&amp;&amp;</c> of terms with) <c>Capabilities.Is(row)</c>, a one-line helper that
/// returns <c>Capabilities.Is(its argument)</c> called with the row, <c>T != null</c> for a native table <c>T</c> that
/// comes from <c>Services.Get&lt;...&gt;("kiln.x", ...)</c> (the core's <c>kiln.x</c> row says exactly that the table is
/// here), or another guard; or when its summary names the row as its capability or feature
/// (<c>(capability &lt;c&gt;kiln.time&lt;/c&gt;)</c>, the services' convention) or names the service in plain words
/// ("True when kiln.knowledge is loaded").</summary>
internal static class ApiScanGuards
{
    internal sealed class Guard
    {
        public readonly SortedSet<string> Rows = new(StringComparer.Ordinal);
        /// <summary>Documentation ids of other bool properties this one is an &amp;&amp; of (resolved after the scan).</summary>
        public readonly SortedSet<string> Via = new(StringComparer.Ordinal);
        public readonly List<string> Why = new();
    }

    // "capability <c>x</c>", "capabilities <c>x</c>, <c>y</c>", "the capability <see cref="Capability"/>", "feature <c>x</c>"
    private static readonly Regex Named = new(
        @"(?:capabilit(?:y|ies)|features?)\b((?:\s*(?:,|and|or)?\s*(?:<c>[A-Za-z0-9._/-]+</c>|<see\s+cref=""[^""]+""\s*/>))+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Item = new(@"<c>([A-Za-z0-9._/-]+)</c>|<see\s+cref=""([^""]+)""\s*/>", RegexOptions.CultureInvariant);
    // a service's own row named in plain words: "True when kiln.knowledge is loaded", "(kiln.content v3 on a build ...)"
    private static readonly Regex Service = new(@"(?<![\w./-])kiln\.[a-z][a-z0-9_]*(?!\.?\w)", RegexOptions.CultureInvariant);
    private static readonly Regex Summary = new(@"<summary>(.*?)</summary>", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>The guard facts of one property declaration; null when it is no candidate (not static bool "...Available"
    /// or "...Ready", or no getter) or it names no row in either way.</summary>
    internal static Guard? Read(SemanticModel model, PropertyDeclarationSyntax pd, CancellationToken ct = default)
    {
        if (model.GetDeclaredSymbol(pd, ct) is not { IsStatic: true, Type.SpecialType: SpecialType.System_Boolean, GetMethod: not null } p ||
            !(p.Name.EndsWith("Available", StringComparison.Ordinal) || p.Name.EndsWith("Ready", StringComparison.Ordinal)))
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
        foreach (var row in DocumentedRows(p, model.Compilation, ct))
            if (g.Rows.Add(row)) g.Why.Add($"summary: {row}");
        return g.Rows.Count > 0 || g.Via.Count > 0 ? g : null;
    }

    /// <summary>The rows the property's summary names: as its capability or feature, or a service (<c>kiln.x</c>) in
    /// plain words.</summary>
    internal static IEnumerable<string> DocumentedRows(IPropertySymbol p, Compilation compilation, CancellationToken ct = default)
    {
        var xml = p.GetDocumentationCommentXml(cancellationToken: ct);
        if (string.IsNullOrEmpty(xml) || Summary.Match(xml) is not { Success: true } s) yield break;
        foreach (Match m in Service.Matches(s.Groups[1].Value)) yield return m.Value;
        foreach (Match m in Named.Matches(s.Groups[1].Value))
        {
            foreach (Match item in Item.Matches(m.Groups[1].Value))
            {
                if (item.Groups[1].Success) yield return item.Groups[1].Value;
                else if (DocumentationCommentId.GetFirstSymbolForDeclarationId(item.Groups[2].Value, compilation) is IFieldSymbol
                         { HasConstantValue: true, ConstantValue: string c })
                    yield return c;
            }
        }
    }

    // `x != null`, `null != x`, `x is not null`: x
    private static ExpressionSyntax? NotNull(ExpressionSyntax e) => e switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression, Right: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } b => b.Left,
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.NotEqualsExpression, Left: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } b => b.Right,
        IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } } } } i => i.Expression,
        _ => null,
    };

    /// <summary>The service a native table property or field holds: the name of the one <c>Services.Get</c> call (a
    /// constant) in its declaration (its getter, or the property that fills the field); null when there is none or
    /// more than one name.</summary>
    internal static string? TableRow(ISymbol table, Compilation compilation, CancellationToken ct = default)
    {
        if (table is not (IPropertySymbol { IsStatic: true } or IFieldSymbol { IsStatic: true })) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in table.DeclaringSyntaxReferences)
        {
            var decl = r.GetSyntax(ct);
            if (decl is VariableDeclaratorSyntax) decl = decl.Parent?.Parent ?? decl;  // a field: its declaration
            if (!compilation.SyntaxTrees.Contains(decl.SyntaxTree)) continue;
            var model = compilation.GetSemanticModel(decl.SyntaxTree);
            foreach (var inv in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(inv, ct).Symbol is not IMethodSymbol { Name: "Get", IsGenericMethod: true } m ||
                    m.ContainingType?.ToDisplayString() != "Kiln.Services" || inv.ArgumentList.Arguments.Count < 1) continue;
                if (model.GetConstantValue(inv.ArgumentList.Arguments[0].Expression, ct) is { HasValue: true, Value: string name }) names.Add(name);
                else return null;
            }
        }
        return names.Count == 1 ? names.First() : null;
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
            var body = md.ExpressionBody?.Expression ?? (md.Body?.Statements is [ReturnStatementSyntax { Expression: { } e }] ? e : null);
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
