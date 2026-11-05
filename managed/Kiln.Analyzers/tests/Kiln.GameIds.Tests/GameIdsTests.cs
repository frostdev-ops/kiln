// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using System.Diagnostics;
using Kiln.GameIds;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Kiln.GameIds.Tests;

public sealed class GameIdsTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "testdata", "client.json");

    private sealed class Text(string path, string? text) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => text is null ? null : SourceText.From(text);
    }

    private sealed class Opts(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    private sealed class Provider(Dictionary<string, string> global, HashSet<string> catalogs) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Opts(global);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Opts(new());
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            new Opts(catalogs.Contains(textFile.Path) ? new() { ["build_metadata.AdditionalFiles.KilnCatalog"] = "true" } : new());
    }

    private static readonly MetadataReference[] Refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll" or "System.Console.dll")
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToArray();

    /// <summary>Runs the generator; returns the output compilation (with <paramref name="usage"/>) and its results.</summary>
    private static (Compilation Out, GeneratorDriverRunResult Run) Generate(string? catalogText, string? sections = null, bool sdk = true,
                                                                             string usage = "class Use { }", string path = "/catalog/client.json")
    {
        var global = new Dictionary<string, string>();
        if (sections is not null) global["build_property.KilnGameIds"] = sections;
        if (sdk) global["build_property.UsingKilnSdk"] = "true";
        var texts = catalogText is null ? [] : new AdditionalText[] { new Text(path, catalogText), new Text("/other/notes.json", "{}") };
        var comp = CSharpCompilation.Create("t", [CSharpSyntaxTree.ParseText(usage)], Refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new GameIdsGenerator().AsSourceGenerator()], texts,
            optionsProvider: new Provider(global, [path]));
        driver = driver.RunGeneratorsAndUpdateCompilation(comp, out var output, out _);
        return (output, driver.GetRunResult());
    }

    private static void NoErrors(Compilation c)
    {
        var errors = c.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(10)));
    }

    [Fact]
    public void Fixture_gives_typed_constants_with_docs()
    {
        var (output, run) = Generate(File.ReadAllText(Fixture), usage: """
            class Use
            {
                const string Sword = Kiln.GameIds.GameItems.Test_Frost_Blade;
                const uint SwordId = Kiln.GameIds.GameItems.Ids.Test_Frost_Blade;
                const string SwordGuid = Kiln.GameIds.GameItems.Guids.Test_Frost_Blade;
                const string Perk = Kiln.GameIds.GamePerks.Perk_Test_Frost_Damage;
                const string Buff = Kiln.GameIds.GameBuffs.Chilled_0e0e0001;
                const string Node = Kiln.GameIds.GameSkills.Blink_0f0f0001;
                const string Forge = Kiln.GameIds.GameWorkshops.Test_Workshop_Forge;
                const string Build = Kiln.GameIds.GameCatalog.SteamBuild;
            }
            """);
        NoErrors(output);
        var src = string.Join("\n", run.Results[0].GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("public const string Test_Frost_Blade = \"Test_Frost_Blade\";", src);
        Assert.Contains("/// <summary>Frost Blade (Weapons, Rare, tier 2, stack 1, 0x0a11b1ad,", src);
        Assert.Contains("/// <remarks>A test sword that is always cold.</remarks>", src);
        Assert.Contains("public const uint Test_Frost_Blade = 0x0a11b1adu;", src);
        Assert.Contains("public const string Chilled_0e0e0001 = \"0x0e0e0001\";", src);   // buffs: the 0x selector
        Assert.Contains("public const string Test_Workshop_Forge = \"0x0c0c0001\";", src);  // stations: the 0x id
        Assert.DoesNotContain("class GameTemplates", src);                                       // opt-in
        Assert.Empty(run.Diagnostics);
    }

    [Fact]
    public void All_adds_templates_and_skips_generated_names()
    {
        var (output, run) = Generate(File.ReadAllText(Fixture), "All");
        NoErrors(output);
        var names = run.Results[0].GeneratedSources.Select(s => s.HintName).ToList();
        Assert.Contains("Kiln.GameIds.GameTemplates.g.cs", names);
        Assert.Contains("Kiln.GameIds.GameSequences.g.cs", names);
        var seq = run.Results[0].GeneratedSources.Single(s => s.HintName == "Kiln.GameIds.GameSequences.g.cs").SourceText.ToString();
        Assert.Contains("Attack_Test_Frostling_Slash", seq);
        Assert.DoesNotContain("#1", seq);
    }

    [Fact]
    public void Vfx_constants_are_guids_named_after_what_uses_them()
    {
        var (output, run) = Generate(File.ReadAllText(Fixture), "Vfx");
        NoErrors(output);
        var vfx = run.Results[0].GeneratedSources.Single(s => s.HintName == "Kiln.GameIds.GameVfx.g.cs").SourceText.ToString();
        Assert.Contains("public const string Explosion_Test_Frost = \"000003e9-7e57-4a11-9c00-0000000003e9\";", vfx);
        Assert.Contains("public const string Vfx_000003eb = \"000003eb-7e57-4a11-9c00-0000000003eb\";", vfx);
        Assert.Contains("parameters radius: float, color: color, targetPosition: float3", vfx);
        Assert.Contains("10 s", vfx);
        Assert.DoesNotContain("GameVfx", string.Join(",", Generate(File.ReadAllText(Fixture)).Run.Results[0].GeneratedSources.Select(s => s.HintName)));
    }

    [Fact]
    public void None_generates_nothing()
    {
        var (_, run) = Generate(File.ReadAllText(Fixture), "None");
        Assert.Empty(run.Results[0].GeneratedSources);
        Assert.Empty(run.Diagnostics);
    }

    [Fact]
    public void Missing_catalog_is_one_info_diagnostic_only_with_the_sdk()
    {
        var (_, run) = Generate(null);
        Assert.Equal("KILN190", Assert.Single(run.Diagnostics).Id);
        Assert.Equal(DiagnosticSeverity.Info, run.Diagnostics[0].Severity);
        Assert.Empty(Generate(null, sdk: false).Run.Diagnostics);
    }

    [Fact]
    public void Broken_catalog_and_unknown_sections_warn()
    {
        Assert.Equal("KILN191", Assert.Single(Generate("{\"kind\": \"kiln.catalog\", \"sections\": {").Run.Diagnostics).Id);
        Assert.Equal("KILN191", Assert.Single(Generate("{\"kind\": \"other\"}").Run.Diagnostics).Id);
        var (_, run) = Generate(File.ReadAllText(Fixture), "Items;Spells");
        Assert.Equal("KILN192", Assert.Single(run.Diagnostics).Id);
    }

    [Fact]
    public void Identifiers_are_valid_and_collisions_numbered()
    {
        const string cat = """
            {"kind":"kiln.catalog","format":1,"game":{"side":"client"},"sections":{"items":[
              {"name":"class","id":"0x00000001"},{"name":"2H-Sword (old)","id":"0x00000002"},{"name":"2H_Sword_old","id":"0x00000003"},
              {"name":"GameItems","id":"0x00000004"},{"name":"Ids"},{"name":"Quote\"Item"},{"name":"0123abcd-0000-4000-8000-000000000000"}]}}
            """;
        var (output, run) = Generate(cat, usage: """
            class Use
            {
                const string A = Kiln.GameIds.GameItems.@class;
                const string B = Kiln.GameIds.GameItems._2H_Sword_old;
                const string C = Kiln.GameIds.GameItems._2H_Sword_old_2;
                const string D = Kiln.GameIds.GameItems.GameItems_2;
                const string E = Kiln.GameIds.GameItems.Ids_2;
                const string F = Kiln.GameIds.GameItems.Quote_Item;
            }
            """);
        NoErrors(output);
        var src = run.Results[0].GeneratedSources.Single(s => s.HintName == "Kiln.GameIds.GameItems.g.cs").SourceText.ToString();
        Assert.Contains("= \"2H-Sword (old)\";", src);
        Assert.Contains("= \"Quote\\\"Item\";", src);
        Assert.DoesNotContain("0123abcd", src);   // a GUID-named entry has no useful identifier
    }

    [Fact]
    public void Real_catalog_when_given()
    {
        var path = Environment.GetEnvironmentVariable("KILN_CATALOG_TEST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var sw = Stopwatch.StartNew();
        var (output, run) = Generate(text, "All");
        var gen = sw.Elapsed;
        NoErrors(output);
        var consts = run.Results[0].GeneratedSources.Sum(s => s.SourceText.Lines.Count(l => l.ToString().Contains("public const string")));
        TestContext.Current.TestOutputHelper?.WriteLine($"real catalog: {text.Length / 1024} KiB, All sections: {consts} constants, generate {gen.TotalMilliseconds:F0} ms, " +
                          $"generate + compile {sw.Elapsed.TotalMilliseconds:F0} ms");
        sw.Restart();
        Generate(text);
        TestContext.Current.TestOutputHelper?.WriteLine($"default sections: {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(consts > 4000);
    }
}
