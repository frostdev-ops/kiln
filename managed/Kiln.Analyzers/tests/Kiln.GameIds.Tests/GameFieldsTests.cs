// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Kiln.GameIds.Tests;

/// <summary>Shared by the tests of GameFields and of the path analyzer: a catalog as an additional file, real Kiln assemblies.</summary>
internal static class TypedHarness
{
    public const string CatalogPath = "/catalog/client.json";
    public static readonly string Fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "client.json"));

    public sealed class Text(string path, string? text) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => text is null ? null : SourceText.From(text);
    }

    private sealed class Opts(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    public sealed class Provider(Dictionary<string, string> global) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Opts(global);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Opts(new());
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            new Opts(textFile.Path == CatalogPath ? new() { ["build_metadata.AdditionalFiles.KilnCatalog"] = "true" } : new());
    }

    /// <summary>The framework, and optionally Kiln.Api and Kiln.Content as a mod sees them.</summary>
    public static MetadataReference[] Refs(bool kiln = true)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var refs = tpa.Where(p => Path.GetFileName(p).StartsWith("System.") || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll").Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        if (kiln)
            foreach (var a in new[] { "Kiln.Api", "Kiln.Content" })
                refs.Add(MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory, a + ".dll")));
        return refs.ToArray();
    }

    public static CSharpCompilation Compilation(string source, MetadataReference[]? refs = null, string file = "Mod.cs") =>
        CSharpCompilation.Create("TestMod",
            [CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using Kiln;", path: "Usings.cs"),
             CSharpSyntaxTree.ParseText(source, path: file)],
            refs ?? Refs(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    public static (Compilation Out, GeneratorDriverRunResult Run) Generate(string catalog, string sections, string source, MetadataReference[]? refs = null)
    {
        var global = new Dictionary<string, string> { ["build_property.KilnGameIdsList"] = sections, ["build_property.UsingKilnSdk"] = "true" };
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new GameIdsGenerator().AsSourceGenerator()], [new Text(CatalogPath, catalog)], optionsProvider: new Provider(global));
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(source, refs), out var output, out _);
        return (output, driver.GetRunResult());
    }

    public static List<Diagnostic> Errors(Compilation c) => c.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    public static void NoErrors(Compilation c)
    {
        var errors = Errors(c);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(15)));
    }
}

public sealed class GameFieldsTests
{
    private static readonly string Fixture = TypedHarness.Fixture;

    [Fact]
    public void Paths_become_typed_members()
    {
        var (output, run) = TypedHarness.Generate(Fixture, "Fields", """
            using Kiln.GameIds;
            public static class Use
            {
                public static object[] All() => new object[]
                {
                    (Field<ushort>)GameFields.Items.MaxStackSize,
                    (Field<float>)GameFields.Items.DamageSetup.DmgMod,
                    (Field<ushort>)GameFields.Items.ItemLevelRange.MinLevel,
                    (Field<GameEnums.ItemRarity>)GameFields.Items.Rarity,
                    (Field<GameEnums.ItemRarityMask>)GameFields.Items.DisableRarityGeneration,
                    (ReadOnlyField<uint>)GameFields.Items.ItemId,
                    (ReadOnlyField<string>)GameFields.Items.DebugName,
                    (Field<uint>)GameFields.Items.PerkIds[1],
                    (Field<uint>)GameFields.Items.PerkIds.All,
                    (Field<System.Text.Json.Nodes.JsonNode?>)GameFields.Items.Tags.Self,
                    (Field<Guid>)GameFields.Items.Tags[2].Tag,
                    (Field<float>)GameFields.Items.Tags.All.Weight,
                    (Field<string>)GameFields.Items.Equipment.PlacedEntity,
                    (Field<int>)GameFields.Items.ImpactValues.ManaCost,
                    (Field<GameEnums.PushForce>)GameFields.Items.ImpactValues.Push,
                    (Field<float>)GameFields.Items.ImpactValues.ById<float>(0x0d0d0004),
                    (Field<float>)GameFields.Items.ImpactValues.Named<float>("mymod.Radius"),
                    (Field<TimeSpan>)GameFields.Templates.LifeTime.LifeTime,
                    (Field<uint>)GameFields.Templates.Health.HealthMax,
                    (Field<string>)GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnThing,
                    (Field<double>)GameFields.Templates.ImpactConfiguration.ConfigValues.Mixed,
                    (Field<System.Text.Json.Nodes.JsonNode?>)GameFields.Templates.Colliders.Shapes["Sphere#1"],
                    GameFields.Templates.Colliders.Shapes[0].Below<float>("radius"),
                    (Field<TimeSpan>)GameFields.Buffs.DefaultLifeTime,
                    (Field<TimeSpan>)GameFields.Recipes.CraftingDuration,
                };
            }
            """);
        TypedHarness.NoErrors(output);
        Assert.Empty(run.Diagnostics);
        var want = new (string Section, string Path)[]
        {
            ("items", "maxStackSize"), ("items", "damageSetup.dmgMod"), ("items", "itemLevelRange.minLevel"), ("items", "rarity"), ("items", "disableRarityGeneration"),
            ("items", "itemId"), ("items", "debugName"), ("items", "perkIds[1]"), ("items", "perkIds[*]"), ("items", "tags"), ("items", "tags[2].tag"), ("items", "tags[*].weight"),
            ("items", "equipment.placedEntity"), ("items", "impactValues.ManaCost"), ("items", "impactValues.Push"), ("items", "impactValues.0x0d0d0004"), ("items", "impactValues[mymod.Radius]"),
            ("templates", "LifeTime.lifeTime"), ("templates", "Health.healthMax"), ("templates", "ImpactConfiguration.configValues.SpawnThing"), ("templates", "ImpactConfiguration.configValues.Mixed"),
            ("templates", "Colliders.shapes[Sphere#1]"), ("templates", "Colliders.shapes[0].radius"), ("buffs", "defaultLifeTime"), ("recipes", "craftingDuration"),
        };
        var got = Run(output).Select(o => ((string)o.GetType().GetProperty("Section")!.GetValue(o)!, (string)o.GetType().GetProperty("Path")!.GetValue(o)!)).ToArray();
        Assert.Equal(want, got);
    }

    private static object[] Run(Compilation output)
    {
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
        ms.Position = 0;
        var alc = new AssemblyLoadContext("generated", isCollectible: true);
        alc.Resolving += (ctx, name) => File.Exists(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll")) ? ctx.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll")) : null;
        try
        {
            return (object[])alc.LoadFromStream(ms).GetType("Use")!.GetMethod("All")!.Invoke(null, null)!;
        }
        finally
        {
            alc.Unload();
        }
    }

    [Theory]
    [InlineData("e.Set(GameFields.Items.DamageSetup.DmgMod, 1.5f);", true)]
    [InlineData("e.Set(GameFields.Items.DamageSetup.DmgMod, 1);", true)]               // an int fits a float
    [InlineData("e.Set(GameFields.Items.MaxStackSize, 5);", true)]                     // a constant fits a ushort
    [InlineData("e.Set(GameFields.Items.Rarity, GameEnums.ItemRarity.Epic);", true)]
    [InlineData("e.Set(GameFields.Items.DisableRarityGeneration, GameEnums.ItemRarityMask.Common | GameEnums.ItemRarityMask.Epic);", true)]
    [InlineData("e.Set(GameFields.Items.Equipment.PlacedEntity, \"Some_Template\");", true)]
    [InlineData("var r = e.Get(GameFields.Items.Rarity); GameEnums.ItemRarity x = r; bool b = e.Reset(GameFields.Items.Rarity);", true)]
    [InlineData("uint id = e.Get(GameFields.Items.ItemId);", true)]
    [InlineData("e.Set(GameFields.Items.DamageSetup.DmgMod, 1.5);", false)]            // a double does not fit a float
    [InlineData("e.Set(GameFields.Items.DamageSetup.DmgMod, \"1.5\");", false)]
    [InlineData("e.Set(GameFields.Items.MaxStackSize, 70000);", false)]                // out of range for a ushort
    [InlineData("e.Set(GameFields.Items.Rarity, \"Epic\");", false)]
    [InlineData("e.Set(GameFields.Items.ItemId, 5u);", false)]                         // read-only: an obsolete error
    [InlineData("e.Set(GameFields.Items.DamageSetup, 1f);", false)]                    // a structure is not a value
    public void Types_are_checked_by_the_compiler(string statement, bool compiles)
    {
        var source = "using Kiln.GameIds; class Use { void M(GameDataEntry e, ContentEntry c) { " + statement + " " + statement.Replace("e.", "c.") .Replace("var r", "var r2").Replace(" x ", " x2 ").Replace(" b ", " b2 ").Replace("uint id", "uint id2").Replace("= r;", "= r2;") + " } }";
        var (output, _) = TypedHarness.Generate(Fixture, "Fields", source);
        var errors = TypedHarness.Errors(output);
        if (compiles) Assert.True(errors.Count == 0, statement + "\n" + string.Join("\n", errors.Take(5)));
        else Assert.True(errors.Count > 0, statement + " should not compile");
    }

    [Fact]
    public void Definitions_take_typed_values()
    {
        var (output, _) = TypedHarness.Generate(Fixture, "Fields", """
            using Kiln.GameIds;
            class Use
            {
                object Clone() => new GameDataClone
                {
                    Guid = Guid.Empty, Name = "n", Base = "b",
                    Values = { { GameFields.Templates.LifeTime.LifeTime, TimeSpan.FromSeconds(5) }, { GameFields.Templates.Health.HealthMax, 5 } },
                };
                object Item() => new ItemDefinition
                {
                    Guid = Guid.Empty, Name = "n", BaseOn = "b",
                    Fields = new FieldValues { { GameFields.Items.MaxStackSize, 1 }, { GameFields.Items.Rarity, GameEnums.ItemRarity.Epic } },
                };
            }
            """);
        TypedHarness.NoErrors(output);
    }

    [Fact]
    public void Opt_in_and_needs_kiln_content()
    {
        // the default sections have no GameFields
        Assert.DoesNotContain(TypedHarness.Generate(Fixture, GameIdsGenerator.DefaultSections, "class Use { }").Run.Results[0].GeneratedSources, s => s.HintName.Contains("GameFields"));
        // All has them when the project can compile them, and is silent when it cannot
        Assert.Contains(TypedHarness.Generate(Fixture, "All", "class Use { }").Run.Results[0].GeneratedSources, s => s.HintName.Contains("GameFields"));
        var (noKiln, run) = TypedHarness.Generate(Fixture, "All", "class Use { }", TypedHarness.Refs(kiln: false));
        Assert.DoesNotContain(run.Results[0].GeneratedSources, s => s.HintName.Contains("GameFields"));
        Assert.Empty(run.Diagnostics);
        TypedHarness.NoErrors(noKiln);
        // asked for by name: it says what is missing
        var (_, asked) = TypedHarness.Generate(Fixture, "Fields", "class Use { }", TypedHarness.Refs(kiln: false));
        Assert.Equal("KILN193", Assert.Single(asked.Diagnostics).Id);
    }

    [Fact]
    public void One_section_alone_costs_less()
    {
        var (output, run) = TypedHarness.Generate(Fixture, "Fields.Items,Fields.buffs,Fields.Spells", """
            using Kiln.GameIds;
            class Use
            {
                Field<ushort> A = GameFields.Items.MaxStackSize;
                Field<TimeSpan> B = GameFields.Buffs.DefaultLifeTime;
            }
            """);
        TypedHarness.NoErrors(output);
        Assert.Equal("KILN192", Assert.Single(run.Diagnostics).Id);   // Spells
        var src = string.Concat(run.Results[0].GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.DoesNotContain("Templates", src);
        Assert.DoesNotContain("FactionType", src);                    // an enum only templates use
        Assert.Contains("enum ItemRarity", src);
    }

    [Fact]
    public void An_older_catalog_still_gives_what_it_has_and_says_so()
    {
        const string cat = """
            {"kind":"kiln.catalog","format":1,"recipe":1,"game":{"side":"client"},"sections":{},
             "fields":{"items":[{"path":"maxStackSize","type":"uint16"},{"path":"rarity","type":"ItemRarity","enum":["Common","Rare"]},
                                {"path":"tags","type":"BlobArray<ItemTagWeight>"},{"path":"tags[*].tag","type":"Guid"},{"path":"cost","type":"float"}]}}
            """;
        var (output, run) = TypedHarness.Generate(cat, "Fields", """
            using Kiln.GameIds;
            class Use
            {
                Field<ushort> A = GameFields.Items.MaxStackSize;
                Field<GameEnums.ItemRarity> B = GameFields.Items.Rarity;
                Field<float> C = GameFields.Items.Cost;
            }
            """);
        TypedHarness.NoErrors(output);
        Assert.Equal("KILN198", Assert.Single(run.Diagnostics).Id);
    }

    [Fact]
    public void Odd_names_stay_valid_csharp()
    {
        const string cat = """
            {"kind":"kiln.catalog","format":1,"recipe":3,"game":{"side":"client"},
             "sections":{"configs":[{"id":"0x00000001","name":"class","names":["class"],"kind":"float32"},{"id":"0x00000002","name":"1","names":["1"],"kind":"float32"},
                                    {"id":"0x00000003","name":"Path","names":["Path"],"kind":"bool"},{"id":"0x00000004","name":"Twice","names":["Twice"],"kind":"float32"},
                                    {"id":"0x00000005","name":"twice","names":["twice"],"kind":"bool"}]},
             "fields":{"items":[
               {"path":"items","type":"uint8","kind":"uint8"},{"path":"class","type":"uint8","kind":"uint8"},{"path":"Class","type":"uint8","kind":"uint8"},
               {"path":"path.path","type":"bool","kind":"bool"},{"path":"self.self","type":"bool","kind":"bool"},{"path":"item.all","type":"bool","kind":"bool"},
               {"path":"item.item","type":"bool","kind":"bool"},{"path":"list","type":"BlobArray<X>","kind":"list"},{"path":"list[*].item","type":"bool","kind":"bool"},
               {"path":"list[*].self","type":"bool","kind":"bool"},{"path":"nested[*]","type":"BlobArray<Y>","kind":"list"},{"path":"nested[*][*]","type":"uint8","kind":"uint8"},
               {"path":"two words.and-more","type":"bool","kind":"bool"},{"path":"configs","type":"impact::Configurations","kind":"configs"},
               {"path":"a::b","type":"a::State","kind":"enum","enum":["One","Two"]},{"path":"c","type":"b::State","kind":"enum","enum":["Three"]},
               {"path":"d","type":"Odd","kind":"enum","enum":["2Way","class","Has Space","has_space"]},{"path":"e","type":"Gapped","kind":"enum","enum":["A","B"],"enumNumbers":[3,9]},
               {"path":"f","type":"Bitmask8<ns::Flag>","kind":"flags","enum":["A","B"],"enumNumbers":[1,6]},
               {"path":"g","type":"Huge","kind":"flags","enum":[]},{"path":"h","type":"Big","kind":"enum","enum":["A"],"enumNumbers":[4294967296]}
             ]}}
            """;
        var (output, run) = TypedHarness.Generate(cat, "Fields", """
            using Kiln.GameIds;
            class Use
            {
                Field<GameEnums.A_State> S1 = GameFields.Items.A_b;
                Field<GameEnums.B_State> S2 = GameFields.Items.C;
                Field<GameEnums.Odd> O = GameFields.Items.D;
                GameEnums.Odd Way = GameEnums.Odd._2Way, Kw = GameEnums.Odd.@class;
                Field<GameEnums.Gapped> G = GameFields.Items.E;
                Field<GameEnums.FlagMask> M = GameFields.Items.F;
                ulong Bits = (ulong)(GameEnums.FlagMask.A | GameEnums.FlagMask.B);
                Field<GameEnums.Big> B = GameFields.Items.H;
                Field<byte> N = GameFields.Items.Nested[1][2];
            }
            """);
        TypedHarness.NoErrors(output);
        Assert.Empty(run.Diagnostics);
        var enums = run.Results[0].GeneratedSources.Single(s => s.HintName.Contains("GameEnums")).SourceText.ToString();
        Assert.Contains("[global::System.Runtime.Serialization.EnumMember(Value = \"2Way\")]", enums);
        Assert.Contains("[global::System.Runtime.Serialization.EnumMember(Value = \"Has Space\")]", enums);
        Assert.Contains("B = 9,", enums);
        Assert.Contains("A = 1UL << 1,", enums);
    }

    [Fact]
    public void Real_catalog_when_given()
    {
        var path = Environment.GetEnvironmentVariable("KILN_CATALOG_TEST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var sw = Stopwatch.StartNew();
        var (output, run) = TypedHarness.Generate(text, "Fields", """
            using Kiln.GameIds;
            class Use
            {
                Field<ushort> A = GameFields.Items.MaxStackSize;
                Field<float> B = GameFields.Items.DamageSetup.DmgMod;
                Field<GameEnums.ItemRarity> C = GameFields.Items.Rarity;
                Field<TimeSpan> D = GameFields.Templates.LifeTime.LifeTime;
                Field<float> E = GameFields.Templates.Enemy01BehaviorState.AggroSetup.ViewRange;
                Field<string> F = GameFields.Templates.ImpactConfiguration.ConfigValues.SpawnTemplate;
                Field<GameEnums.PushBackForce> G = GameFields.Templates.ImpactConfiguration.ConfigValues.PushBackStrength;
                Field<int> H = GameFields.Items.ImpactValues.ManaCost;
                Field<string> I = GameFields.Buffs.ImpactValues.BuffType;
            }
            """);
        var gen = sw.Elapsed;
        TypedHarness.NoErrors(output);
        var sources = run.Results[0].GeneratedSources.Where(s => s.HintName.Contains("GameField") || s.HintName.Contains("GameEnums")).ToList();
        var size = sources.Sum(s => s.SourceText.Length);
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        Assert.True(emit.Success);
        TestContext.Current.TestOutputHelper?.WriteLine($"real catalog: {sources.Count} files, {size / 1024} KiB of source, {ms.Length / 1024} KiB of dll, generate {gen.TotalMilliseconds:F0} ms, " +
                                                        $"generate + compile {sw.Elapsed.TotalMilliseconds:F0} ms");
        foreach (var part in new[] { "Fields.Items", "Fields.Buffs,Fields.Perks", "Fields.Templates" })
        {
            sw.Restart();
            var (one, _) = TypedHarness.Generate(text, part, "class Use { }");
            using var dll = new MemoryStream();
            Assert.True(one.Emit(dll).Success);
            TestContext.Current.TestOutputHelper?.WriteLine($"{part}: {dll.Length / 1024} KiB of dll, generate + compile {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
    }
}
