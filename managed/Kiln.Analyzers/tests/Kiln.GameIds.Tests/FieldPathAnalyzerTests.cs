// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Kiln.GameIds.Tests;

public sealed class FieldPathAnalyzerTests
{
    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string? catalog = null, bool fields = false)
    {
        var global = new Dictionary<string, string>();
        var cat = catalog ?? TypedHarness.Fixture;
        Compilation comp = TypedHarness.Compilation(source);
        if (fields)
        {
            var (o, _) = TypedHarness.Generate(cat, "Fields", source);
            comp = o;
        }
        var options = new AnalyzerOptions([new TypedHarness.Text(TypedHarness.CatalogPath, cat)], new TypedHarness.Provider(global));
        var diags = await comp.WithAnalyzers([new FieldPathAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        return diags.Where(d => d.Location.SourceTree?.FilePath == "Mod.cs").OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
    }

    private static string Body(string statements) => "class Mod { void M(GameDataEntry param, ContentEntry cparam) { " + statements + " } }";

    private static void Ids(ImmutableArray<Diagnostic> diags, params string[] ids) =>
        Assert.True(diags.Select(d => d.Id).SequenceEqual(ids), $"expected [{string.Join(", ", ids)}], got:\n{string.Join("\n", diags.Select(d => d.ToString()))}");

    [Fact]
    public async Task A_typo_is_an_error_with_the_field_it_meant()
    {
        var d = Assert.Single(await Analyze(Body("""Content.Templates.Get("x")!.Set("LifeTime.lifeTme", 5.0);""")));
        Assert.Equal("KILN194", d.Id);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Contains("LifeTime has no field 'lifeTme'", d.GetMessage());
        Assert.Contains("Did you mean 'LifeTime.lifeTime'?", d.GetMessage());
        Assert.Equal("LifeTime.lifeTime", d.Properties[FieldPathAnalyzer.SuggestionKey]);
        Assert.Equal("\"LifeTime.lifeTme\"", d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan));
    }

    [Theory]
    [InlineData("Content.Items.Get(\"x\")!.Set(\"maxStackSise\", 1);", "maxStackSize")]
    [InlineData("Content.ItemData.Get(\"x\")!.SetJson(\"damageSetup.dmgMood\", \"1\");", "damageSetup.dmgMod")]
    [InlineData("Content.Items.Get(\"x\")!.Data().Get(\"damageSetp.dmgMod\");", "damageSetup.dmgMod")]
    [InlineData("Content.GameData(GameDataSection.Buffs).Get(\"x\")!.Reset(\"defaultLifeTim\");", "defaultLifeTime")]
    [InlineData("Content.Registry(ContentKind.Recipes).Get(\"x\")!.GetNumber(\"craftingDuratin\");", "craftingDuration")]
    [InlineData("var t = Content.Templates.Get(\"x\")!; t.Original(\"LifeTim.lifeTime\");", "LifeTime.lifeTime")]
    [InlineData("var t = Content.Templates.Get(\"x\") ?? throw new Exception(); t.GetNumber(\"LifeTime.lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("Content.Templates.Get(\"x\")?.GetJson(\"LifeTime.lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("if (Content.Templates.Get(\"x\") is { } t) t.Fields(\"LifeTime.lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("foreach (var t in Content.Templates) t.Get(\"LifeTime.lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("Content.Templates[0].Get(\"LifeTime.lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("Content.Templates.First(e => e.Name == \"x\").Get(\"LifeTime.lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("const string p = \"LifeTime.\"; Content.Templates.Get(\"x\")!.Get(p + \"lifeTme\");", "LifeTime.lifeTime")]
    [InlineData("Content.Items.Get(\"x\")!.Set(\"dmgMod\", 1);", "damageSetup.dmgMod")]   // a segment left out
    public async Task Sections_are_followed_through_the_code(string statements, string suggestion)
    {
        var d = Assert.Single(await Analyze(Body(statements)));
        Assert.Equal("KILN194", d.Id);
        Assert.Equal(suggestion, d.Properties[FieldPathAnalyzer.SuggestionKey]);
    }

    [Theory]
    [InlineData("Content.Items.Get(\"x\")!.Set(\"maxStackSize\", 1);")]
    [InlineData("Content.Items.Get(\"x\")!.GetNumber(\"damageSetup.dmgMod\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.GetJson(\"damageSetup\");")]                      // a structure: its fields
    [InlineData("Content.ItemData.Get(\"x\")!.GetJson();")]
    [InlineData("Content.ItemData.Get(\"x\")!.Fields();")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"perkIds[1]\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"tags[2].tag\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"tags[*].tag\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"tags[00000000-0000-0000-0000-000000000001].tag\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"maxstacksize\");")]                         // kiln.gamedata falls back to ignoring case
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues.ManaCost\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues.manacost\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues.OtherSpawn\");")]              // any name of a config
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues.0x12345678\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues[mymod.Radius]\");")]           // a program's own name
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues.ManaCost.scaleFactor\");")]    // a field of the config
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"impactValues.simple[0]\");")]
    [InlineData("Content.ItemData.Get(\"x\")!.Config(\"ManaCost\").Set(1);")]
    [InlineData("Content.Items.Get(\"x\")!.Config(\"ManaCost\").Set(1);")]
    [InlineData("Content.Templates.Get(\"x\")!.Config(\"SpawnThing\").Set(\"y\");")]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"family\");")]                              // the template's own field
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"LifeTime#1.lifeTime\");")]                 // the second of a component
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"lifetime.lifeTime\");")]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"DoNotSave\");")]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"Colliders.shapes[0].radius\");")]          // below a variant the catalog cannot see
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"Colliders.shapes[Sphere#0].radius\");")]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"Colliders.shapes[Sphere].radius.more\");")]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"LifeTime.lifeTime.value\");")]             // a wrapper's own field
    [InlineData("Content.Arsenals.Get(\"x\")!.Set(\"anything.at.all\", 1);")]                   // no field list
    [InlineData("Content.Drops.Get(\"x\")!.Get(\"[3].countMin\");")]
    [InlineData("var t = Content.Templates.Get(\"x\")!; t = Content.Items.Get(\"y\")!.Data(); t.Get(\"maxStackSize\");")]  // written again: unknown
    public async Task Valid_paths_and_what_cannot_be_known_stay_silent(string statements) => Ids(await Analyze(Body(statements)));

    [Theory]
    [InlineData("param.Set(\"no.such.path\", 1);")]
    [InlineData("cparam.Get(\"no.such.path\");")]
    [InlineData("var e = param; e.Set(\"no.such.path\", 1);")]
    [InlineData("var path = \"not\" + Guid.NewGuid(); Content.ItemData.Get(\"x\")!.Get(path);")]
    public async Task Unknown_sections_and_computed_paths_are_not_guessed(string statements) => Ids(await Analyze(Body(statements)));

    [Theory]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"maxStackSize.more\");", "KILN194", null)]        // nothing below a number
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"rarity.more\");", "KILN194", null)]
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"maxStackSize[0]\");", "KILN194", null)]          // not a list
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"perkIds[2]\");", "KILN194", "perkIds[1]")]       // the list has 2
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"tags.tag\");", "KILN194", "tags[0].tag")]        // a list needs an index
    [InlineData("Content.ItemData.Get(\"x\")!.Get(\"tags[0\");", "KILN194", null)]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"LifeTim#1.lifeTime\");", "KILN194", "LifeTime#1.lifeTime")]
    [InlineData("Content.Templates.Get(\"x\")!.Get(\"Nonsense\");", "KILN194", null)]
    [InlineData("Content.ItemData.Get(\"x\")!.Set(\"impactValues.ManaCst\", 1);", "KILN195", "impactValues.ManaCost")]
    [InlineData("Content.Items.Get(\"x\")!.Config(\"ManaCst\").Set(1);", "KILN195", "ManaCost")]
    [InlineData("Content.Templates.Get(\"x\")!.Set(\"ImpactConfiguration.configValues.SpawnThng\", \"y\");", "KILN195", "ImpactConfiguration.configValues.SpawnThing")]
    public async Task Wrong_paths(string statements, string id, string? suggestion)
    {
        var d = Assert.Single(await Analyze(Body(statements)));
        Assert.Equal(id, d.Id);
        d.Properties.TryGetValue(FieldPathAnalyzer.SuggestionKey, out var s);
        Assert.Equal(suggestion, s);
    }

    [Fact]
    public async Task Writing_an_identity_field_warns_reading_it_does_not()
    {
        var d = Assert.Single(await Analyze(Body("""Content.ItemData.Get("x")!.Set("itemId", 1); Content.ItemData.Get("x")!.Get("itemId");""")));
        Assert.Equal("KILN196", d.Id);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
    }

    [Fact]
    public async Task Definitions_are_checked()
    {
        var diags = await Analyze("""
            class Mod
            {
                void M()
                {
                    Content.Templates.Define(new GameDataClone { Guid = Guid.Empty, Name = "n", Base = "b", Fields = { ["LifeTime.lifeTme"] = 5, ["LifeTime.lifeTime"] = 5 } });
                    Content.Buffs.Define(new GameDataClone { Guid = Guid.Empty, Name = "n", Base = "b", Fields = new() { ["defaultLifeTim"] = 5 } });
                    Content.Items.Register(new ItemDefinition { Guid = Guid.Empty, Name = "n", BaseOn = "b", Fields = new Dictionary<string, object> { ["maxStackSise"] = 1, ["maxStackSize"] = 1 } });
                    Content.Items.Register(new ItemDefinition { Guid = Guid.Empty, Name = "n", BaseOn = "b", Fields = new Dictionary<string, object> { { "rarty", "Epic" } } });
                    Content.Recipes.Register(new RecipeDefinition { Guid = Guid.Empty, Name = "n", Station = "s", Fields = new Dictionary<string, object> { ["craftingDuratin"] = 1 } });
                    var free = new GameDataClone { Guid = Guid.Empty, Name = "n", Base = "b", Fields = { ["whatever.it.is"] = 5 } };   // no section yet
                }
            }
            """);
        Ids(diags, "KILN194", "KILN194", "KILN194", "KILN194", "KILN194");
        Assert.Equal(new[] { "LifeTime.lifeTime", "defaultLifeTime", "maxStackSize", "rarity", "craftingDuration" }, diags.Select(d => d.Properties[FieldPathAnalyzer.SuggestionKey]));
    }

    [Fact]
    public async Task A_typed_field_of_another_section_is_refused()
    {
        var source = """
            using Kiln.GameIds;
            class Mod
            {
                void M()
                {
                    Content.Templates.Get("x")!.Set(GameFields.Items.MaxStackSize, 1);
                    Content.Items.Get("x")!.Get(GameFields.Templates.LifeTime.LifeTime);
                    Content.ItemData.Get("x")!.Set(GameFields.Items.MaxStackSize, 1);
                    Content.Items.Get("x")!.Set(GameFields.Items.DamageSetup.DmgMod, 1f);
                    var f = GameFields.Items.MaxStackSize;
                    Content.Templates.Get("x")!.Set(f, 1);   // left to the check at run time
                }
            }
            """;
        Ids(await Analyze(source, fields: true), "KILN197", "KILN197");
    }

    [Fact]
    public async Task The_code_fix_writes_the_suggestion()
    {
        const string source = """class Mod { void M() { Content.Templates.Get("x")!.Set("LifeTime.lifeTme", 5.0); } }""";
        using var ws = new Microsoft.CodeAnalysis.AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var did = DocumentId.CreateNewId(pid);
        var sol = ws.CurrentSolution
            .AddProject(ProjectInfo.Create(pid, VersionStamp.Create(), "TestMod", "TestMod", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable), metadataReferences: TypedHarness.Refs()))
            .AddDocument(DocumentId.CreateNewId(pid), "Usings.cs", "global using System; global using Kiln;")
            .AddDocument(did, "Mod.cs", source, filePath: "Mod.cs");
        var doc = sol.GetDocument(did)!;
        var comp = (await doc.Project.GetCompilationAsync())!;
        var options = new AnalyzerOptions([new TypedHarness.Text(TypedHarness.CatalogPath, TypedHarness.Fixture)], new TypedHarness.Provider(new()));
        var d = Assert.Single(await comp.WithAnalyzers([new FieldPathAnalyzer()], options).GetAnalyzerDiagnosticsAsync());
        var actions = new List<Microsoft.CodeAnalysis.CodeActions.CodeAction>();
        await new Kiln.Analyzers.FieldPathCodeFix().RegisterCodeFixesAsync(new Microsoft.CodeAnalysis.CodeFixes.CodeFixContext(doc, d, (a, _) => actions.Add(a), CancellationToken.None));
        var ops = await Assert.Single(actions).GetOperationsAsync(CancellationToken.None);
        var changed = ops.OfType<Microsoft.CodeAnalysis.CodeActions.ApplyChangesOperation>().Single().ChangedSolution.GetDocument(did)!;
        Assert.Contains("\"LifeTime.lifeTime\"", (await changed.GetTextAsync()).ToString());
    }

    [Fact]
    public async Task Without_a_catalog_nothing_is_checked()
    {
        var comp = TypedHarness.Compilation(Body("""Content.Templates.Get("x")!.Set("LifeTime.lifeTme", 5.0);"""));
        var options = new AnalyzerOptions([], new TypedHarness.Provider(new()));
        Assert.Empty(await comp.WithAnalyzers([new FieldPathAnalyzer()], options).GetAnalyzerDiagnosticsAsync());
    }

    [Fact]
    public async Task A_broken_catalog_is_not_this_analyzers_to_report() =>
        Ids(await Analyze(Body("""Content.Templates.Get("x")!.Set("LifeTime.lifeTme", 5.0);"""), catalog: "{\"kind\": \"kiln.catalog\", \"fields\": {"));

    /// <summary>KILN_CATALOG_TEST=&lt;client.json&gt; KILN_LIVE_PATHS=&lt;paths the typed-test lab mod wrote&gt;: every path kiln.gamedata itself
    /// reports for the game's entries must pass the check, or the analyzer could refuse a path that works.</summary>
    [Fact]
    public void Real_catalog_accepts_every_path_the_running_game_reports()
    {
        var catalog = Environment.GetEnvironmentVariable("KILN_CATALOG_TEST");
        var live = Environment.GetEnvironmentVariable("KILN_LIVE_PATHS");
        if (string.IsNullOrEmpty(catalog) || string.IsNullOrEmpty(live) || !File.Exists(catalog) || !File.Exists(live)) return;
        var text = File.ReadAllText(catalog);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = new FieldIndex(FieldCatalog.Read(text));
        TestContext.Current.TestOutputHelper?.WriteLine($"catalog of {text.Length / 1024} KiB indexed in {sw.ElapsedMilliseconds} ms");
        var total = 0;
        var wrong = new List<string>();
        var warned = new List<string>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(live));
        foreach (var section in doc.RootElement.EnumerateObject())
            foreach (var path in section.Value.EnumerateArray().Select(p => p.GetString()!))
            {
                total++;
                var r = index.Check(section.Name, path);
                if (r.Verdict == PathVerdict.Unknown) wrong.Add($"{section.Name} {path}: {r.What}");
                // a config whose name has a dot is reported without the brackets a path needs: only a warning either way
                if (r.Verdict == PathVerdict.UnknownConfig) warned.Add($"{section.Name} {path}: {r.What}");
            }
        TestContext.Current.TestOutputHelper?.WriteLine($"{total} live paths, {wrong.Count} refused, {warned.Count} warned about: {string.Join("; ", warned)}");
        Assert.True(total > 1000 && wrong.Count == 0 && warned.Count < 10, $"{total} paths; " + string.Join("\n", wrong.Concat(warned).Take(60)));
    }

    /// <summary>KILN_CATALOG_TEST=&lt;client.json&gt;: every constant path the repository's mods give these APIs. A false alarm here
    /// would break somebody's build. Paths that are wrong on purpose are named.</summary>
    [Fact]
    public async Task Real_catalog_accepts_what_the_repositorys_mods_write()
    {
        var path = Environment.GetEnvironmentVariable("KILN_CATALOG_TEST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "mods", "samples"))) root = Path.GetDirectoryName(root)!;
        var catalog = File.ReadAllText(path);
        var additional = new TypedHarness.Text(TypedHarness.CatalogPath, catalog);
        var refs = TypedHarness.Refs();
        var found = new List<string>();
        var files = Directory.EnumerateFiles(Path.Combine(root, "mods"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/")).ToList();
        var checkedFiles = 0;
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            if (!text.Contains("Content.")) continue;
            checkedFiles++;
            var comp = TypedHarness.Compilation(text, refs, f);
            var options = new AnalyzerOptions([additional], new TypedHarness.Provider(new()));
            foreach (var d in await comp.WithAnalyzers([new FieldPathAnalyzer()], options).GetAnalyzerDiagnosticsAsync())
                if (d.Location.SourceTree?.FilePath == f)
                    found.Add($"{Path.GetRelativePath(root, f)}:{d.Location.GetLineSpan().StartLinePosition.Line + 1} {d.Id} {d.GetMessage()}");
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"{checkedFiles} files; {found.Count} diagnostics");
        foreach (var x in found) TestContext.Current.TestOutputHelper?.WriteLine(x);
        var unexpected = found.Where(x => !IntendedInTests(x)).ToList();
        Assert.True(unexpected.Count == 0, string.Join("\n", unexpected));
    }

    // Lab tests check the refusals at run time, so they hold wrong paths on purpose.
    private static bool IntendedInTests(string line) =>
        line.Contains("noSuchField") || line.Contains("NoSuchAlias") || (line.Contains("content-test/ContentTestMod.cs") && line.Contains("KILN196 'itemId'")) ||
        line.Contains("TypedFields/Refused/");
}
