// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN124 and KILN125: hints from a mod's old paths and Native-tier code toward the registry fields that cover them,
/// from the generated table (RegistryHints.g.cs, made by tools/registrygen from the schemas and regmap/common), and the fix
/// that moves a one-to-one data pack value into the "registry" section.</summary>
public class RegistryHintTests
{
    private static async Task<ImmutableArray<Diagnostic>> Run(string source, params (string Path, string Text)[] files)
    {
        var comp = Harness.Compile(source);
        var options = new AnalyzerOptions(files.Select(f => (AdditionalText)new FileText(f.Path, f.Text)).ToImmutableArray());
        var diags = await comp.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RegistryHintAnalyzer()), options).GetAnalyzerDiagnosticsAsync();
        Assert.DoesNotContain(diags, d => d.Id == "AD0001");
        return diags.OrderBy(d => d.Location.GetLineSpan().Path).ThenBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
    }

    private const string Empty = "public sealed class M : Mod { }";

    [Fact]
    public void TheTableComesFromTheSchemasAndRegmap()
    {
        Assert.Contains(RegistryHints.Registries, r => r.Name == "items");
        Assert.Contains(RegistryHints.Paths, p => p is { Root: "keen::ItemInfo", Path: "maxstacksize", Field: "items.maxStack", Exact: true });
        Assert.Equal<(string, bool)?>(("items.maxStack", true), RegistryHintAnalyzer.Covering("keen::ItemInfo", "maxStackSize"));
        Assert.Equal("items.power", RegistryHintAnalyzer.Covering("keen::ItemInfo", "damageSetup.dmgMod")?.Field);
        Assert.False(RegistryHintAnalyzer.Covering("keen::ItemInfo", "damageSetup.dmgMod")!.Value.Exact);  // through a formula
        Assert.Null(RegistryHintAnalyzer.Covering("keen::ItemInfo", "noSuchMember"));
        // a list's element is covered by the list, never one to one
        Assert.Equal<(string, bool)?>(("balance.altarsPerFlameLevel", false), RegistryHintAnalyzer.Covering("keen::BalancingTable", "altarsPerFlameLevel[*]"));
    }

    [Fact]
    public async Task DataPackPathsARegistryFieldCovers()
    {
        var pack = """
        { "format": 1,
          // BigStacks-style
          "items": { "Material_T1_Wood_Logs": { "maxStackSize": 500, "damageSetup.dmgMod": { "mul": 1.5 }, "weight": 2 },
                     "Material_*": { "maxStackSize": { "mul": 4 } } },
          "templates": { "Enemy_Vukah_Melee": { "LifeTime.lifeTime": 5 } } }
        """;
        var diags = await Run(Empty, ("/mod/content/stacks.json", pack));
        var hints = diags.Where(d => d.Id == "KILN124").ToList();
        Assert.Equal(3, hints.Count);
        Assert.All(hints, d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
        Assert.Contains("items.maxStack", hints[0].GetMessage());
        Assert.Equal("true", hints[0].Properties[RegistryHintAnalyzer.FixableKey]);
        Assert.Contains("items.power", hints[1].GetMessage());
        Assert.False(hints[1].Properties.ContainsKey(RegistryHintAnalyzer.FixableKey));  // a formula
        Assert.False(hints[2].Properties.ContainsKey(RegistryHintAnalyzer.FixableKey));  // a glob and a relative op
        Assert.Equal("/mod/content/stacks.json", hints[0].Location.GetLineSpan().Path);
        Assert.Equal(2, hints[0].Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task ResourcePatchesInBlocksFiles()
    {
        var blocks = """
        { "format": 1, "resourcePatches": { "more-altars": { "format": 1, "resource": "82706b40-61b1-4b8f-8b23-dcec6971bda1",
          "type": "keen::BalancingTable", "set": { "altarsPerFlameLevel[*]": { "min": 16 }, "playerLevelCap": 30 } } } }
        """;
        var diags = (await Run(Empty, ("/mod/blocks/boot.json", blocks))).Where(d => d.Id == "KILN124").ToList();
        Assert.Contains(diags, d => d.GetMessage().Contains("balance.altarsPerFlameLevel"));
        Assert.All(diags, d => Assert.Contains("a resource patch of BalancingTable", d.GetMessage()));
        Assert.All(diags, d => Assert.False(d.Properties.ContainsKey(RegistryHintAnalyzer.FixableKey)));  // boot documents are not moved
    }

    [Fact]
    public async Task ResourcePatchesInOnBootCode()
    {
        var src = """
        public sealed class M : Mod
        {
            private static readonly Guid Table = Guid.Parse("82706b40-61b1-4b8f-8b23-dcec6971bda1");
            public override void OnBoot(BootContext boot)
            {
                var block = new ResourcePatchBlock { Resource = Table, Type = "keen::BalancingTable" };
                block.Set["altarsPerFlameLevel[*]"] = new ResourceValue { Min = 16 };
                var other = new ResourcePatchBlock { Resource = Table, Type = "keen::BalancingTable", Set = { ["playerLevelCap"] = new ResourceValue { Min = 30 } } };
                BootDefinitions.For(boot).Define("altars", block);
                BootDefinitions.For(boot).Define("cap", other);
            }
        }
        """;
        var diags = (await Run(src)).Where(d => d.Id == "KILN124").ToList();
        Assert.Equal(2, diags.Count);
        Assert.Contains("balance.altarsPerFlameLevel", diags[0].GetMessage());
        Assert.Contains("balance.levelCap", diags[1].GetMessage());
        Assert.All(diags, d => Assert.Contains("a resource patch of BalancingTable", d.GetMessage()));
    }

    [Fact]
    public async Task ContentEntryPathsInCode()
    {
        var src = """
        public sealed class M : Mod
        {
            public override void OnLoad()
            {
                Content.Items.Get("Material_T1_Wood_Logs")!.Set("maxStackSize", 500);
                var e = Content.Items.Get("Material_T1_Wood_Logs");
                e?.Set("weight", 2);
                string p = "maxStackSize";
                e?.Set(p, 3);
            }
        }
        """;
        var diags = (await Run(src)).Where(d => d.Id == "KILN124").ToList();
        Assert.Single(diags);  // the constant path; "weight" no registry covers; a computed path is not followed
        Assert.Contains("items.maxStack", diags[0].GetMessage());
    }

    [Fact]
    public async Task NativeTierHooksAndMemoryWrites()
    {
        var src = """
        using System.Runtime.InteropServices;
        public sealed class M : Mod
        {
            private static void Handler(ref Registers regs) { }
            public override void OnLoad()
            {
                ulong image = 0x140000000;
                nuint b = (nuint)image;
                Hooks.Mid(b + 0x93dea0, Handler);
                Memory.Write<int>(b + 16, 5);
                var own = Marshal.AllocHGlobal(16);
                Marshal.WriteInt32(own, 1);
                Marshal.WriteInt32((nint)b, 1);
            }
        }
        """;
        var diags = (await Run(src)).Where(d => d.Id == "KILN125").ToList();
        Assert.Equal(3, diags.Count);  // the hook, Memory.Write, the write into an address it did not allocate
        Assert.All(diags, d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
        Assert.Contains("hooks the game's code by address", diags[0].GetMessage());
    }

    [Fact]
    public async Task AMemberIsTheFieldWhoseOwnMemberItIsNotAFormulaThatReadsIt()
    {
        var pack = """{ "items": { "Wood": { "itemLevelRange.minLevel": 5, "damageSetup.speedMod": 1.2 } } }""";
        var hints = (await Run(Empty, ("/mod/content/levels.json", pack))).Where(d => d.Id == "KILN124").ToList();
        Assert.Equal(2, hints.Count);
        Assert.Contains("items.levels.min", hints[0].GetMessage());
        Assert.DoesNotContain("armor", hints[0].GetMessage());
        Assert.Contains("items.speed", hints[1].GetMessage());
        Assert.DoesNotContain("items.power", hints[1].GetMessage());
        Assert.All(hints, d => Assert.Equal("true", d.Properties[RegistryHintAnalyzer.FixableKey]));
    }

    [Fact]
    public async Task AConfigurationIsTheFieldThatNamesIt()
    {
        // SpellTweaks' form: one configuration of the impact list by its name
        var pack = """{ "items": { "Ammo_T1_Spell_Fire_Ball_lvl10": { "impactValues.ManaCost": 30 }, "Ability_DoubleJump": { "impactValues": { "StaminaCost": 5 } } } }""";
        var hints = (await Run(Empty, ("/mod/content/spelltweaks.json", pack))).Where(d => d.Id == "KILN124").ToList();
        Assert.Equal(2, hints.Count);
        Assert.Contains("items.manaCost", hints[0].GetMessage());
        Assert.DoesNotContain("staminaCost", hints[0].GetMessage());
        Assert.Contains("items.staminaCost", hints[1].GetMessage());
        Assert.All(hints, d => Assert.False(d.Properties.ContainsKey(RegistryHintAnalyzer.FixableKey)));  // a configuration: no fix
    }

    [Fact]
    public void TheFixMovesAOneToOneValueIntoTheRegistrySection()
    {
        var pack = """
        { "format": 1,
          "items": { "Material_T1_Wood_Logs": { "maxStackSize": 500, "weight": 2 } } }
        """;
        var start = pack.IndexOf("\"maxStackSize\"", StringComparison.Ordinal);
        var moved = RegistryHintFix.MoveText(pack, start, "items.maxStack", "items", "Material_T1_Wood_Logs");
        Assert.NotNull(moved);
        var doc = JsonSpans.Parse(moved!)!;
        Assert.Null(doc.Find("items")!.Value.Find("Material_T1_Wood_Logs")!.Value.Find("maxStackSize"));
        Assert.Equal("2", doc.Find("items")!.Value.Find("Material_T1_Wood_Logs")!.Value.Find("weight")!.Value.Text);
        // the registry section's own form (schema/registry-section.schema.json): {"alter": [edits]}, one edit of the Kiln field
        var section = RegistrySection(moved!);
        var edit = Assert.Single(section["alter"]!.AsArray())!;
        Assert.Equal("items", edit["registry"]!.GetValue<string>());
        Assert.Equal("Material_T1_Wood_Logs", edit["select"]!["id"]!.GetValue<string>());
        Assert.Equal("maxStack", edit["field"]!.GetValue<string>());
        Assert.Equal(500, edit["op"]!.GetValue<int>());
        // into an existing registry section's alter list, and {"set": v}
        var withRegistry = """
        { "format": 1, "items": { "Wood": { "maxStackSize": { "set": 99 } } },
          "registry": { "alter": [ { "registry": "items", "select": { "id": "game:wood" }, "field": "rarity", "op": "rare" } ] } }
        """;
        moved = RegistryHintFix.MoveText(withRegistry, withRegistry.IndexOf("\"maxStackSize\"", StringComparison.Ordinal), "items.maxStack", "items", "Wood");
        var alter = RegistrySection(moved!)["alter"]!.AsArray();
        Assert.Equal(2, alter.Count);
        Assert.Equal("rarity", alter[0]!["field"]!.GetValue<string>());
        Assert.Equal(99, alter[1]!["op"]!.GetValue<int>());
        // a section with add only: alter joins it
        var addOnly = """{ "items": { "Wood": { "maxStackSize": 7 } }, "registry": { "add": [] } }""";
        Assert.Equal(7, RegistrySection(RegistryHintFix.MoveText(addOnly, addOnly.IndexOf("\"maxStackSize\"", StringComparison.Ordinal), "items.maxStack", "items", "Wood")!)["alter"]![0]!["op"]!.GetValue<int>());
        // the registry section already edits that field of that entry (by its id or its old name): no fix, both stay
        var both = """{ "items": { "Wood": { "maxStackSize": 1 } }, "registry": { "alter": [ { "registry": "items", "select": { "id": "game:wood" }, "field": "maxStack", "op": 2 } ] } }""";
        Assert.Null(RegistryHintFix.MoveText(both, both.IndexOf("\"maxStackSize\"", StringComparison.Ordinal), "items.maxStack", "items", "Wood"));
        // a registry section that does not read as one (a stage it does not have) is left alone
        var odd = """{ "items": { "Wood": { "maxStackSize": 1 } }, "registry": { "items": {} } }""";
        Assert.Null(RegistryHintFix.MoveText(odd, odd.IndexOf("\"maxStackSize\"", StringComparison.Ordinal), "items.maxStack", "items", "Wood"));
        // an entry with conditions, a glob, an item number: not one entry by its key
        var where = """{ "items": { "Wood": { "@where": { "rarity": "Rare" }, "maxStackSize": 1 } } }""";
        Assert.Null(RegistryHintFix.MoveText(where, where.IndexOf("\"maxStackSize\"", StringComparison.Ordinal), "items.maxStack", "items", "Wood"));
        var glob = """{ "items": { "Wood_Log?": { "maxStackSize": 1 } } }""";
        Assert.Null(RegistryHintFix.MoveText(glob, glob.IndexOf("\"maxStackSize\"", StringComparison.Ordinal), "items.maxStack", "items", "Wood_Log?"));
    }

    // the "registry" section of a fixed file, checked the way the stage reads it (native ir.cpp lower_section): an object of
    // add / alter / final lists of edits with registry, select.id, field and op
    private static System.Text.Json.Nodes.JsonObject RegistrySection(string text)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        var section = doc["registry"]!.AsObject();
        Assert.All(section, kv => Assert.Contains(kv.Key, new[] { "add", "alter", "final" }));
        foreach (var (_, list) in section)
            Assert.All(list!.AsArray(), e => Assert.True(e!["registry"] is not null && e["select"]?["id"] is not null && e["field"] is not null && e["op"] is not null, e.ToJsonString()));
        return section;
    }

    [Fact]
    public async Task OnlyAnEntryNamedByItsKeyIsFixable()
    {
        var pack = """
        { "items": { "Wood": { "maxStackSize": 1 }, "Wood_Log?": { "maxStackSize": 2 }, "0x1234abcd": { "maxStackSize": 3 }, "4711": { "maxStackSize": 4 },
                     "Fire Fly Bait": { "maxStackSize": 5 }, "Stone": { "@where": { "rarity": "Rare" }, "maxStackSize": 6 } } }
        """;
        var hints = (await Run(Empty, ("/mod/content/stacks.json", pack))).Where(d => d.Id == "KILN124").ToList();
        Assert.Equal(6, hints.Count);  // every one is a hint
        Assert.Equal(new[] { "true", null, null, null, null, null },
            hints.Select(d => d.Properties.TryGetValue(RegistryHintAnalyzer.FixableKey, out var v) ? v : null).ToArray());
    }

    [Fact]
    public async Task TypedBlocksAreNotOldPaths()
    {
        // a blocks file's "items" are typed blocks under Kiln's names (the Stable tier): no hint, whatever their keys
        var blocks = """{ "format": 1, "items": { "my_gem": { "name": "Gem", "rarity": "rare", "stack": 20 } }, "recipes": { "my_gem": { "duration": 2 } } }""";
        Assert.DoesNotContain(await Run(Empty, ("/mod/blocks/gems.json", blocks)), d => d.Id == "KILN124");
        // the same keys in a data pack are the game's paths
        var pack = """{ "items": { "Material_T1_Wood_Logs": { "rarity": "Rare" } } }""";
        Assert.Contains(await Run(Empty, ("/mod/content/rarity.json", pack)), d => d.Id == "KILN124");
    }

    [Fact]
    public async Task AHookBySymbolNameIsNotAHookByAddress()
    {
        var src = """
        public sealed class M : Mod
        {
            private static void Handler(ref Registers regs) { }
            public override void OnLoad()
            {
                Hooks.Mid(Symbols.Get("my-mod/Site"), Handler);
                var at = Symbols.Get("my-mod/Other");
                Hooks.Mid(at, Handler);
                if (Symbols.TryGet("my-mod/Third", out var third)) Hooks.Mid(third, Handler);
                Hooks.Mid(Symbols.Find("48 8B ?? 90") + 4, Handler);  // a pattern the mod scans for itself: an address
            }
        }
        """;
        var diags = (await Run(src)).Where(d => d.Id == "KILN125").ToList();
        Assert.Single(diags);
        Assert.Contains("by address", diags[0].GetMessage());
    }

    [Fact]
    public async Task TheFixIsOfferedOnTheDataPackInTheWorkspace()
    {
        var pack = """{ "format": 1, "items": { "Wood": { "maxStackSize": 500 } } }""";
        using var ws = new AdhocWorkspace();
        var pid = ProjectId.CreateNewId();
        var aid = DocumentId.CreateNewId(pid);
        var sol = ws.CurrentSolution
            .AddProject(ProjectInfo.Create(pid, VersionStamp.Create(), "TestMod", "TestMod", LanguageNames.CSharp, metadataReferences: Harness.References))
            .AddDocument(DocumentId.CreateNewId(pid), "Mod.cs", Empty)
            .AddAdditionalDocument(aid, "stacks.json", SourceText.From(pack), filePath: "/mod/content/stacks.json");
        var diags = await Run(Empty, ("/mod/content/stacks.json", pack));
        var d = diags.Single(x => x.Id == "KILN124");
        var actions = new List<CodeAction>();
        var fix = new RegistryHintFix();
        await fix.RegisterCodeFixesAsync(new CodeFixContext(sol.GetAdditionalDocument(aid)!, d, (a, _) => actions.Add(a), CancellationToken.None));
        var action = Assert.Single(actions);
        var ops = await action.GetOperationsAsync(CancellationToken.None);
        var changed = ops.OfType<ApplyChangesOperation>().Single().ChangedSolution.GetAdditionalDocument(aid)!;
        var text = (await changed.GetTextAsync()).ToString();
        Assert.Contains("\"registry\": {\"alter\": [{\"registry\": \"items\", \"select\": {\"id\": \"Wood\"}, \"field\": \"maxStack\", \"op\": 500}]}", text);
        Assert.DoesNotContain("maxStackSize", text);
        RegistrySection(text);
    }

    private sealed class FileText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}
