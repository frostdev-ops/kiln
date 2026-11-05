// SPDX-License-Identifier: MIT
// ApiScan: derives the analyzers' API tables from Kiln's own sources, so the rules follow what the code enforces at
// run time instead of a hand-kept list.
//   tick thread: a member that (directly, or through same-assembly calls made outside lambdas) reaches
//                Services.RequireTickThread, Game.AssertTickThread or `if (!<x>.IsTickThread) throw ...`.
//                A method that branches on IsTickThread any other way is "thread-aware" (it dispatches itself, e.g.
//                Scheduler.Post when off the tick thread) and does not inherit its callees' requirement.
//   advanced:    a member that reaches HooksGate.RequireAdvanced (Kiln.Hooks: hooks, symbols, raw memory).
// Usage: ApiScan --repo <kiln repo> --stage <dir with the built Kiln dlls> --refs <Microsoft.NETCore.App.Ref ref dir>
//                --out <KilnApis.g.cs>
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var opts = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string Opt(string k) => opts.TryGetValue(k, out var v) ? v : throw new ArgumentException($"--{k} is required");
var repo = Path.GetFullPath(Opt("repo"));
var stage = Path.GetFullPath(Opt("stage"));
var refDir = Path.GetFullPath(Opt("refs"));
var outFile = Path.GetFullPath(Opt("out"));

var projects = new List<(string Name, string Dir)>
{
    ("Kiln.Api", Path.Combine(repo, "managed/Kiln.Api")),
    ("Kiln.Testing", Path.Combine(repo, "managed/Kiln.Testing")),
};
var services = new List<string>();
foreach (var dir in Directory.GetDirectories(Path.Combine(repo, "managed/services")).OrderBy(d => d, StringComparer.Ordinal))
{
    var name = Path.GetFileName(dir);
    if (!File.Exists(Path.Combine(dir, name + ".csproj"))) continue;
    projects.Add((name, dir));
    if (name.StartsWith("Kiln.", StringComparison.Ordinal)) services.Add(name.Substring(5));
}

var frameworkRefs = Directory.GetFiles(refDir, "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToList();
var stageDlls = Directory.GetFiles(stage, "*.dll");
var parse = new CSharpParseOptions(LanguageVersion.Preview);
const string implicitUsings = "global using System; global using System.Collections.Generic; global using System.IO; " +
                              "global using System.Linq; global using System.Net.Http; global using System.Threading; " +
                              "global using System.Threading.Tasks;";

var tick = new SortedDictionary<string, string>(StringComparer.Ordinal);
var advanced = new SortedDictionary<string, string>(StringComparer.Ordinal);
var completed = new SortedDictionary<string, string>(StringComparer.Ordinal);
var errors = 0;

// Two passes: the second sees the first's completed-task members of other assemblies (Kiln.Creatures awaits Kiln.Spawn).
var completedAnywhere = new HashSet<string>(StringComparer.Ordinal);
for (var pass = 0; pass < 2; pass++)
foreach (var (name, dir) in projects)
{
    var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .OrderBy(f => f, StringComparer.Ordinal).ToList();
    var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f)).ToList();
    trees.Add(CSharpSyntaxTree.ParseText(implicitUsings, parse, "ImplicitUsings.g.cs"));
    var refs = frameworkRefs.Concat(stageDlls
            .Where(p => !string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)))
        .ToList();
    var comp = CSharpCompilation.Create(name, trees, refs,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable));
    // CS8795: [GeneratedRegex] partials, whose other half a source generator writes (not run here).
    var diags = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS8795").ToList();
    if (diags.Count > 0 && pass == 0)
    {
        // Binding still works for the rest; report so a broken scan is visible.
        Console.Error.WriteLine($"apiscan: {name}: {diags.Count} compile error(s), first: {diags[0]}");
        errors += diags.Count;
    }

    var facts = new Dictionary<IMethodSymbol, Facts>(SymbolEqualityComparer.Default);
    foreach (var tree in trees)
    {
        var model = comp.GetSemanticModel(tree);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            IMethodSymbol? m = null;
            SyntaxNode? body = null;
            switch (node)
            {
                case BaseMethodDeclarationSyntax md:
                    m = model.GetDeclaredSymbol(md) as IMethodSymbol;
                    body = (SyntaxNode?)md.Body ?? md.ExpressionBody;
                    if (md is ConstructorDeclarationSyntax { Initializer: { } init }) Scan(model, init, Get(m), comp);
                    break;
                case AccessorDeclarationSyntax ad:
                    m = model.GetDeclaredSymbol(ad) as IMethodSymbol;
                    body = (SyntaxNode?)ad.Body ?? ad.ExpressionBody;
                    break;
                case PropertyDeclarationSyntax { ExpressionBody: { } eb } pd:
                    m = (model.GetDeclaredSymbol(pd) as IPropertySymbol)?.GetMethod;
                    body = eb;
                    break;
                case IndexerDeclarationSyntax { ExpressionBody: { } ib } idx:
                    m = (model.GetDeclaredSymbol(idx) as IPropertySymbol)?.GetMethod;
                    body = ib;
                    break;
            }
            if (m is null || body is null) continue;
            var facts0 = Get(m);
            facts0.ReturnsTask = m.ReturnType is INamedTypeSymbol { Name: "Task" or "ValueTask" } rt &&
                                 rt.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";
            facts0.Async = m.IsAsync;
            Scan(model, body, facts0, comp);
        }
    }

    Facts Get(IMethodSymbol? m)
    {
        if (m is null) return new Facts();
        m = m.OriginalDefinition;
        if (!facts.TryGetValue(m, out var f)) facts[m] = f = new Facts();
        return f;
    }

    // Fixpoint over the same-assembly call graph.
    bool changed;
    do
    {
        changed = false;
        foreach (var (m, f) in facts)
        {
            foreach (var callee in f.Calls)
            {
                if (!facts.TryGetValue(callee, out var cf)) continue;
                if (!f.Tick && !f.ThreadAware && cf.Tick)
                {
                    f.Tick = true;
                    f.TickWhy = $"via {Short(callee)}";
                    changed = true;
                }
                if (!f.Advanced && cf.Advanced)
                {
                    f.Advanced = true;
                    f.AdvancedWhy = $"via {Short(callee)}";
                    changed = true;
                }
            }
        }
    } while (changed);

    // Methods whose returned task is always complete (Task.FromResult & co., directly or through same-assembly calls).
    do
    {
        changed = false;
        foreach (var (m, f) in facts)
        {
            if (f.Completed || !f.ReturnsTask) continue;
            bool Done(IMethodSymbol? r) => r is null || (facts.TryGetValue(r, out var rf) ? rf.Completed
                : r.GetDocumentationCommentId() is { } rid && completedAnywhere.Contains(rid));
            var completes = f.Async
                ? !f.OtherAwait && f.Awaits.All(a => Done(a))                        // every await finds its task complete
                : !f.OtherReturn && f.Returns.Count > 0 && f.Returns.All(r => Done(r));
            if (completes)
            {
                f.Completed = true;
                changed = true;
            }
        }
    } while (changed);

    foreach (var (m, f) in facts)
    {
        if (!IsPublicApi(m)) continue;
        var id = m.GetDocumentationCommentId();
        if (id is null || id == "M:Kiln.Game.AssertTickThread") continue;  // the check itself: meant to be called anywhere
        if (f.Tick) tick[id] = $"{Short(m)}: {f.TickWhy}";
        if (f.Advanced) advanced[id] = $"{Short(m)}: {f.AdvancedWhy}";
        if (f.Completed)
        {
            completed[id] = Short(m);
            completedAnywhere.Add(id);
        }
    }
}

var sb = new StringBuilder();
sb.AppendLine("// SPDX-License-Identifier: MIT");
sb.AppendLine("// <auto-generated> by managed/Kiln.Analyzers/tools/gen-apis.sh (ApiScan) from Kiln's managed sources. Do not edit.");
sb.AppendLine("// Regenerate after changing Kiln.Api, Kiln.Testing or a managed service.");
sb.AppendLine("namespace Kiln.Analyzers;");
sb.AppendLine();
sb.AppendLine("internal static partial class KilnApis");
sb.AppendLine("{");
sb.AppendLine("    /// <summary>Documentation ids of public members that throw when called off the tick thread (KILN101).</summary>");
sb.AppendLine("    internal static readonly string[] TickThread =");
sb.AppendLine("    {");
foreach (var (id, why) in tick) sb.AppendLine($"        \"{id}\", // {why}");
sb.AppendLine("    };");
sb.AppendLine();
sb.AppendLine("    /// <summary>Documentation ids of public members gated on the advanced tier, kiln.mod.json \"advanced\": true (KILN107).</summary>");
sb.AppendLine("    internal static readonly string[] Advanced =");
sb.AppendLine("    {");
foreach (var (id, why) in advanced) sb.AppendLine($"        \"{id}\", // {why}");
sb.AppendLine("    };");
sb.AppendLine();
sb.AppendLine("    /// <summary>Documentation ids of public Task-returning members whose task is always already complete (they");
sb.AppendLine("    /// return Task.FromResult/FromException): reading .Result does not block (KILN105).</summary>");
sb.AppendLine("    internal static readonly string[] CompletedTasks =");
sb.AppendLine("    {");
foreach (var (id, why) in completed) sb.AppendLine($"        \"{id}\", // {why}");
sb.AppendLine("    };");
sb.AppendLine();
sb.AppendLine("    /// <summary>Kiln's managed services: assembly Kiln.&lt;name&gt;, referenced through KilnServices (KILN108).</summary>");
sb.AppendLine("    internal static readonly string[] Services =");
sb.AppendLine("    {");
foreach (var s in services) sb.AppendLine($"        \"{s}\",");
sb.AppendLine("    };");
sb.AppendLine("}");
File.WriteAllText(outFile, sb.ToString().Replace("\r\n", "\n"));
Console.WriteLine($"apiscan: {tick.Count} tick-thread, {advanced.Count} advanced, {completed.Count} completed-task members, {services.Count} services -> {outFile}" +
                  (errors > 0 ? $" ({errors} compile errors while scanning)" : ""));
return 0;

static string Short(IMethodSymbol m) => m.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

static bool IsPublicApi(IMethodSymbol m)
{
    static bool Visible(Accessibility a) => a is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
    if (!Visible(m.DeclaredAccessibility)) return false;
    if (m.AssociatedSymbol is { } assoc && !Visible(assoc.DeclaredAccessibility)) return false;
    for (var t = m.ContainingType; t != null; t = t.ContainingType)
        if (!Visible(t.DeclaredAccessibility)) return false;
    return true;
}

static void Scan(SemanticModel model, SyntaxNode body, Facts f, Compilation comp)
{
    if (body is ArrowExpressionClauseSyntax arrow) AddReturn(model, arrow.Expression, f);
    foreach (var n in Walk(body))
    {
        switch (n)
        {
            case ReturnStatementSyntax { Expression: { } rexpr }:
                AddReturn(model, rexpr, f);
                break;
            case AwaitExpressionSyntax aw:
                if (model.GetSymbolInfo(aw.Expression).Symbol is IMethodSymbol awaited && aw.Expression is InvocationExpressionSyntax)
                    f.Awaits.Add((awaited.ReducedFrom ?? awaited).OriginalDefinition);
                else f.OtherAwait = true;
                break;
            case ForEachStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword }:
            case UsingStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword }:
            case LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword }:
                f.OtherAwait = true;
                break;
            case IfStatementSyntax ifs when IsTickGuard(ifs):
                f.Tick = true;
                f.TickWhy = "if (!IsTickThread) throw";
                break;
            case IdentifierNameSyntax { Identifier.ValueText: "IsTickThread" } id when !InsideTickGuard(id):
                f.ThreadAware = true;
                break;
            case InvocationExpressionSyntax inv:
            {
                var s = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (s is null) break;
                var full = $"{s.ContainingType?.ToDisplayString()}.{s.Name}";
                if (full is "Kiln.Services.RequireTickThread" or "Kiln.Game.AssertTickThread")
                {
                    f.Tick = true;
                    f.TickWhy = full.Substring(5);
                }
                else if (full == "Kiln.HooksGate.RequireAdvanced")
                {
                    f.Advanced = true;
                    f.AdvancedWhy = "HooksGate.RequireAdvanced";
                }
                else AddCall(f, s, comp);
                break;
            }
            case BaseObjectCreationExpressionSyntax oc:
                if (model.GetSymbolInfo(oc).Symbol is IMethodSymbol ctor) AddCall(f, ctor, comp);
                break;
            case ConstructorInitializerSyntax ci:
                if (model.GetSymbolInfo(ci).Symbol is IMethodSymbol bctor) AddCall(f, bctor, comp);
                break;
            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
            {
                var expr = (ExpressionSyntax)n;
                if (n is IdentifierNameSyntax && n.Parent is MemberAccessExpressionSyntax ma && ma.Name == n) break; // seen via the member access
                var sym = model.GetSymbolInfo(expr).Symbol;
                if (sym is IPropertySymbol p)
                {
                    var assigned = expr.Parent is AssignmentExpressionSyntax a && a.Left == expr;
                    var compound = assigned && !((AssignmentExpressionSyntax)expr.Parent!).IsKind(SyntaxKind.SimpleAssignmentExpression);
                    if ((!assigned || compound) && p.GetMethod is { } g) AddCall(f, g, comp);
                    if (assigned && p.SetMethod is { } st) AddCall(f, st, comp);
                }
                else if (sym is IEventSymbol e && expr.Parent is AssignmentExpressionSyntax ea && ea.Left == expr)
                {
                    var acc = ea.IsKind(SyntaxKind.AddAssignmentExpression) ? e.AddMethod : e.RemoveMethod;
                    if (acc is not null) AddCall(f, acc, comp);
                }
                break;
            }
        }
    }
}

// A returned value: already-complete task, the result of another method, or something else.
static void AddReturn(SemanticModel model, ExpressionSyntax e, Facts f)
{
    while (e is ParenthesizedExpressionSyntax p) e = p.Expression;
    if (e is ConditionalExpressionSyntax c)
    {
        AddReturn(model, c.WhenTrue, f);
        AddReturn(model, c.WhenFalse, f);
        return;
    }
    if (e is ThrowExpressionSyntax) return;
    var sym = model.GetSymbolInfo(e).Symbol;
    if (sym is IMethodSymbol { Name: "FromResult" or "FromException" or "FromCanceled" } m &&
        m.ContainingType?.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask")
    {
        f.Returns.Add(null);
        return;
    }
    if (sym is IPropertySymbol { Name: "CompletedTask" }) { f.Returns.Add(null); return; }
    if (sym is IMethodSymbol callee && e is InvocationExpressionSyntax)
    {
        f.Returns.Add((callee.ReducedFrom ?? callee).OriginalDefinition);
        return;
    }
    f.OtherReturn = true;
}

// Descendants without deferred code (lambdas, anonymous methods, local functions).
static IEnumerable<SyntaxNode> Walk(SyntaxNode body) =>
    body.DescendantNodesAndSelf(n => n == body || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

static void AddCall(Facts f, IMethodSymbol s, Compilation comp)
{
    s = (s.ReducedFrom ?? s).OriginalDefinition;
    if (SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, comp.Assembly)) f.Calls.Add(s);
}

// `if (!x.IsTickThread) <throw>` without an else: the canonical guard.
static bool IsTickGuard(IfStatementSyntax ifs)
{
    if (ifs.Else is not null) return false;
    if (ifs.Condition is not PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not) return false;
    var operand = not.Operand is ParenthesizedExpressionSyntax par ? par.Expression : not.Operand;
    var named = operand switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        IdentifierNameSyntax id => id.Identifier.ValueText,
        _ => null,
    };
    if (named != "IsTickThread") return false;
    return ifs.Statement is ThrowStatementSyntax ||
           ifs.Statement is BlockSyntax { Statements: [ThrowStatementSyntax] };
}

static bool InsideTickGuard(SyntaxNode n) =>
    n.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault() is { } ifs && IsTickGuard(ifs) && ifs.Condition.Span.Contains(n.Span);

sealed class Facts
{
    public bool Tick, ThreadAware, Advanced, Completed, ReturnsTask, Async;
    // Return values: null = Task.FromResult/FromException/FromCanceled/CompletedTask; a method = whatever it returns;
    // Other = anything else.
    public readonly List<IMethodSymbol?> Returns = new();
    public bool OtherReturn;
    // async methods: what they await (a method whose task is complete keeps the method synchronous).
    public readonly List<IMethodSymbol> Awaits = new();
    public bool OtherAwait;
    public string TickWhy = "", AdvancedWhy = "";
    public readonly HashSet<IMethodSymbol> Calls = new(SymbolEqualityComparer.Default);
}
