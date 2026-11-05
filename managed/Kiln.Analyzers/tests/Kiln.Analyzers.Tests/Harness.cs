// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Kiln.Analyzers.Tests;

/// <summary>Compiles a small mod against the real Kiln assemblies and runs one analyzer (and optionally a code fix).</summary>
internal static class Harness
{
    private const string GlobalUsings =
        "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; " +
        "global using System.Threading; global using System.Threading.Tasks; global using Kiln;";

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    public static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        foreach (var asm in new[] { "Kiln.Api", "Kiln.Testing", "Kiln.Chat", "Kiln.Players", "Kiln.Combat", "Kiln.Hooks", "Kiln.World", "Kiln.Spawn", "Kiln.Blocks" })
            refs.Add(MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, asm + ".dll")));
        return refs.ToImmutableArray();
    }

    /// <summary>A library the mod references (another assembly's static state, for KILN103/104).</summary>
    public static MetadataReference Library(string source, string name = "SharedLib")
    {
        var comp = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(GlobalUsings, Parse), CSharpSyntaxTree.ParseText(source, Parse) },
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var ms = new MemoryStream();
        var r = comp.Emit(ms);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return MetadataReference.CreateFromImage(ms.ToArray());
    }

    public static CSharpCompilation Compile(string source, IEnumerable<MetadataReference>? extra = null, string name = "TestMod")
    {
        var comp = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(GlobalUsings, Parse, "Usings.cs"), CSharpSyntaxTree.ParseText(source, Parse, "Mod.cs") },
            References.Concat(extra ?? []),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "test source does not compile:\n" + string.Join("\n", errors));
        return comp;
    }

    public static async Task<ImmutableArray<Diagnostic>> Run(DiagnosticAnalyzer analyzer, string source,
        IDictionary<string, string>? properties = null, string? manifest = null, IEnumerable<MetadataReference>? extra = null)
    {
        var comp = Compile(source, extra);
        return await Analyze(comp, analyzer, properties, manifest);
    }

    public static async Task<ImmutableArray<Diagnostic>> Analyze(Compilation comp, DiagnosticAnalyzer analyzer,
        IDictionary<string, string>? properties = null, string? manifest = null)
    {
        var files = manifest is null ? ImmutableArray<AdditionalText>.Empty : ImmutableArray.Create<AdditionalText>(new Text("/mod/kiln.mod.json", manifest));
        var options = new AnalyzerOptions(files, new Options(properties ?? new Dictionary<string, string>()));
        var diags = await comp.WithAnalyzers(ImmutableArray.Create(analyzer), options).GetAnalyzerDiagnosticsAsync();
        var crashed = diags.Where(d => d.Id == "AD0001").ToList();
        Assert.True(crashed.Count == 0, "analyzer threw: " + string.Join("\n", crashed));
        return diags.Where(d => d.Location.SourceTree?.FilePath == "Mod.cs").OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
    }

    /// <summary>The source text a diagnostic points at.</summary>
    public static string At(this Diagnostic d) => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan);

    public static void Ids(ImmutableArray<Diagnostic> diags, params string[] ids) =>
        Assert.True(diags.Select(d => d.Id).SequenceEqual(ids),
            $"expected [{string.Join(", ", ids)}], got:\n{string.Join("\n", diags.Select(d => $"{d.Id} {d.Location.GetLineSpan().StartLinePosition} '{d.At()}': {d.GetMessage()}"))}");

    /// <summary>Applies the first code action <paramref name="fix"/> offers for the first diagnostic and returns the new
    /// source (after the workspace's simplifier and formatter).</summary>
    public static async Task<string?> Fix(DiagnosticAnalyzer analyzer, CodeFixProvider fix, string source,
        IDictionary<string, string>? properties = null)
    {
        using var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var sol = ws.CurrentSolution
            .AddProject(ProjectInfo.Create(pid, VersionStamp.Create(), "TestMod", "TestMod", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                    nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: Parse, metadataReferences: References))
            .AddDocument(DocumentId.CreateNewId(pid), "Usings.cs", GlobalUsings);
        var did = DocumentId.CreateNewId(pid);
        sol = sol.AddDocument(did, "Mod.cs", source, filePath: "Mod.cs");
        var doc = sol.GetDocument(did)!;
        var comp = (await doc.Project.GetCompilationAsync())!;
        var diags = await Analyze(comp, analyzer, properties);
        var d = diags.FirstOrDefault(x => fix.FixableDiagnosticIds.Contains(x.Id));
        Assert.NotNull(d);
        var actions = new List<CodeAction>();
        await fix.RegisterCodeFixesAsync(new CodeFixContext(doc, d!, (a, _) => actions.Add(a), CancellationToken.None));
        if (actions.Count == 0) return null;
        var ops = await actions[0].GetOperationsAsync(CancellationToken.None);
        var changed = ops.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetDocument(did)!;
        return (await changed.GetTextAsync()).ToString().Replace("\r\n", "\n");
    }

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class Options(IDictionary<string, string> global) : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions Empty = new Dict(new Dictionary<string, string>());
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Dict(global);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
    }

    private sealed class Dict(IDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (values.TryGetValue(key, out var v)) { value = v; return true; }
            value = null!;
            return false;
        }
    }

    public static IDictionary<string, string> Props(params (string Key, string Value)[] kv) =>
        kv.ToDictionary(x => "build_property." + x.Key, x => x.Value);
}
