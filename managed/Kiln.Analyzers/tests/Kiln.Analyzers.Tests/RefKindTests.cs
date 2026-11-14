// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN111 for every block reference kind: sound, item, perk, ability, material, station, screen, template, and IBlockRef&lt;T&gt; by T.</summary>
public sealed class RefKindTests
{
    private static readonly BlocksAnalyzer A = new();

    // What the SDK's GameIds generator writes from the local catalog, cut down to the names the tests use.
    private const string GameIds = """
        namespace Kiln.GameIds
        {
            public static class GameItems { public const string Iron_Sword = "Iron_Sword"; public const string Wood_Log = "Wood_Log"; }
            public static class GamePerks { public const string Perk_Frost_Damage = "Perk_Frost_Damage"; }
            public static class GameTemplates { public const string Wolf = "Wolf"; }
            public static class GameSounds { public const string Chime = "Chime"; public static class Guids { public const string Chime = "2f3d7c11-90ab-4cde-8f01-23456789abcd"; } }
        }
        """;

    private static string Mod(string body, bool gameIds = true) =>
        "public sealed class M : Mod { public override void OnLoad() {\n" + body + "\n} }\n" + (gameIds ? GameIds : "");

    [Fact]
    public async Task A_name_staged_in_OnBoot_is_a_block_of_the_mod()
    {
        // the guide's example (docs/kiln/modding/onboot.md): names staged with BootDefinitions.Define are the mod's blocks
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot)
                {
                    var defs = BootDefinitions.For(boot);
                    defs.Define("chill", new StatusBlock { Name = "Chill", Seconds = 3 });
                    defs.Define("chill-touch", new EffectBlock { Applies = new StatusApply { Status = "chill" } });
                    defs.Define("chill-bolt", new SpellBlock { Name = "Chill Bolt", Cast = new CastBlock { ManaCost = 2 }, Delivery = new ProjectileBlock { OnHit = ["chill-touch"] } });
                }
            }
            """);
        Assert.Empty(d);
        d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnBoot(BootContext boot) =>
                    BootDefinitions.For(boot).Define("chill-touch", new EffectBlock { Applies = new StatusApply { Status = "chil" } });
            }
            """);
        Ids(d, "KILN111");
        Assert.Contains("chil", d[0].GetMessage());
    }

    [Fact]
    public async Task A_misspelled_sound()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("ding", new SoundBlock { Clips = ["a.wav"] });
            Blocks.Define("a", new EffectBlock { Sound = "dnig" });
            """));
        Ids(d, "KILN111");
        Assert.Contains("dnig", d[0].GetMessage());
    }

    [Fact]
    public async Task A_misspelled_item()
    {
        var d = await Run(A, Mod("""
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["Iron_Swrod"] = 2 } };
            """));
        Ids(d, "KILN111");
        Assert.Contains("Iron_Swrod", d[0].GetMessage());
        Assert.Contains("GameItems", d[0].GetMessage());
        Assert.Equal(DiagnosticSeverity.Info, d[0].Severity);  // looks like the game's: the local catalog may be of another build
    }

    [Fact]
    public async Task A_name_that_is_not_game_style_and_nowhere_is_a_warning()
    {
        var d = await Run(A, Mod("""
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["frost-gme"] = 2 } };
            """));
        Ids(d, "KILN111");
        Assert.Equal(DiagnosticSeverity.Warning, d[0].Severity);
    }

    [Fact]
    public async Task Game_style_names_missing_from_the_local_catalog_are_info_only()
    {
        var d = await Run(A, Mod("""
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["Weapon_T9_1H_SwordNew_01"] = 1 } };
            IReadOnlyList<PerkRef> perks = ["Perk_From_A_Newer_Build"];
            """));
        Ids(d, "KILN111", "KILN111");
        Assert.All(d, x => Assert.Equal(DiagnosticSeverity.Info, x.Severity));
        Assert.Contains("another game build", d[0].GetMessage());
    }

    [Fact]
    public async Task Items_of_the_older_items_api_are_the_mods_own()
    {
        var d = await Run(A, Mod("""
            Content.Items.Register(new ItemDefinition { Guid = Guid.Empty, Name = "glacier_gem", BaseOn = "Wood_Log" });
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["glacier_gem"] = 1, ["glacier_gme"] = 1 } };
            """));
        Ids(d, "KILN111");                                   // the typo only
        Assert.Contains("glacier_gme", d[0].GetMessage());
    }

    [Fact]
    public async Task An_older_api_item_named_at_run_time_could_be_any()
    {
        Ids(await Run(A, Mod("""
            var name = DateTime.Now.Ticks.ToString();
            Content.Items.Register(new ItemDefinition { Guid = Guid.Empty, Name = name, BaseOn = "Wood_Log" });
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["glacier_gem"] = 1 } };
            """)));
    }

    [Fact]
    public async Task A_misspelled_perk()
    {
        var d = await Run(A, Mod("""
            IReadOnlyList<PerkRef> perks = ["Perk_Frost_Damgae"];
            """));
        Ids(d, "KILN111");
        Assert.Contains("Perk_Frost_Damgae", d[0].GetMessage());
    }

    [Fact]
    public async Task Correct_names_pass()
    {
        Ids(await Run(A, Mod("""
            Blocks.Define("ding", new SoundBlock { Clips = ["a.wav"] });
            Blocks.Define("a", new EffectBlock { Sound = "ding", Spawns = "Wolf" });
            Blocks.Define("c", new EffectBlock { Sound = "2f3d7c11-90ab-4cde-8f01-23456789abcd" });
            Blocks.Define("frosty", new PerkBlock { Name = "Frosty" });
            Blocks.Define("bolt", new SpellBlock { Name = "Bolt", Cast = new CastBlock(), Delivery = new ProjectileBlock() });
            var r = new RecipeBlock { Station = "hand", Needs = new Dictionary<ItemRef, int> { ["Iron_Sword"] = 2, ["other-mod:gem"] = 1, ["bolt"] = 1 } };
            var s = new RecipeBlock { Station = "0x1a2b3c4d" };
            IReadOnlyList<PerkRef> perks = ["Perk_Frost_Damage", "frosty", "none", "item:Iron_Sword", "2f3d7c1190ab4cde8f0123456789abcd"];
            """)));
    }

    [Fact]
    public async Task A_cue_of_the_cook_recipe_is_a_sound()
    {
        Ids(await Run(A, Mod("""Blocks.Define("a", new EffectBlock { Sound = "ui_chime" });"""),
            files: [("/mod/assets/cook.json", """{ "sounds": { "ui_chime": { "file": "audio/ui/chime.wav" } } }""")]));
    }

    [Fact]
    public async Task Without_game_ids_a_game_name_could_be_any()
    {
        // no generated GameIds (no local catalog): item, perk and template names can't be checked; a game sound is a GUID, so a sound still can
        var d = await Run(A, Mod("""
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["Iron_Swrod"] = 2 } };
            IReadOnlyList<PerkRef> perks = ["Perk_Frost_Damgae"];
            Blocks.Define("a", new EffectBlock { Spawns = "Wofl", Sound = "Chmie" });
            """, gameIds: false));
        Ids(d, "KILN111");
        Assert.Contains("Chmie", d[0].GetMessage());
    }

    [Fact]
    public async Task Template_station_ability_material_screen()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("a", new EffectBlock { Spawns = "Wofl" });
            var r = new RecipeBlock { Station = "anvl" };
            Blocks.Define("b", new EffectBlock { Casts = "no-such-ability" });
            MaterialRef m = "no-such-material";
            Blocks.Define("c", new EffectBlock { Screen = "no-such-screen" });
            """));
        Ids(d, "KILN111", "KILN111", "KILN111", "KILN111", "KILN111");
        Assert.Contains("template \"Wofl\"", d[0].GetMessage());
        Assert.Contains("anvl", d[1].GetMessage());
        Assert.Contains("no-such-ability", d[2].GetMessage());
        Assert.Contains("no-such-material", d[3].GetMessage());
        Assert.Contains("no-such-screen", d[4].GetMessage());
    }

    [Fact]
    public async Task The_wrong_kind()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("hit", new EffectBlock());
            Blocks.Define("frosty", new PerkBlock { Name = "Frosty" });
            Blocks.Define("a", new EffectBlock { Sound = "hit", Screen = "hit" });
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["frosty"] = 1 } };
            IReadOnlyList<PerkRef> perks = ["hit"];
            """));
        Ids(d, "KILN111", "KILN111", "KILN111", "KILN111");
        Assert.All(d, x => Assert.Contains(" block, and this takes ", x.GetMessage()));
        Assert.Contains("takes a screeneffect", d[1].GetMessage());
    }

    [Fact]
    public async Task The_windows_abilities_and_effects_a_button_or_a_key_names()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("settings", new WindowBlock { Title = "S", Rows = [new TextRow { Text = "x" }] });
            Blocks.Define("hit", new EffectBlock());
            Blocks.Define("nova", new AbilityBlock { Name = "Nova", OnCast = [new EffectBlock()] });
            Blocks.Define("main", new WindowBlock { Title = "M", Rows =
            [
                new ButtonRow { Label = "Go", Does = new UiAction { OpenWindow = "settings" } },
                new ButtonRow { Label = "Cast", Does = new UiAction { CastAbility = "nova" } },
                new ButtonRow { Label = "Hit", Does = new UiAction { ApplyEffect = "hit" } },
                new ButtonRow { Label = "Typo", Does = new UiAction { OpenWindow = "settngs" } },
                new ButtonRow { Label = "Wrong", Does = new UiAction { CloseWindow = "hit" } },
            ] });
            Blocks.Define("key", new KeyBindingBlock { Key = "F8", Does = new UiAction { CastAbility = "no-such-ability" } });
            Blocks.Define("key2", new KeyBindingBlock { Key = "F9", Does = new UiAction { ApplyEffect = "nofx" } });
            """));
        Ids(d, "KILN111", "KILN111", "KILN111", "KILN111");
        Assert.Contains("settngs", d[0].GetMessage());
        Assert.Contains("\"hit\" is an effect block, and this takes a window", d[1].GetMessage());
        Assert.Contains("no-such-ability", d[2].GetMessage());
        Assert.Contains("nofx", d[3].GetMessage());
    }

    [Fact]
    public async Task A_window_is_not_an_item()
    {
        var d = await Run(A, Mod("""
            Blocks.Define("settings", new WindowBlock { Title = "S", Rows = [new TextRow { Text = "x" }] });
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["settings"] = 1 } };
            """));
        Ids(d, "KILN111");
        Assert.Contains("window block", d[0].GetMessage());
    }

    [Theory]
    [InlineData("new LootTableBlock { Entries = [new LootEntry { Item = \"Iron_Sword\" }] }")]
    [InlineData("new RecipeBlock { Output = \"Iron_Sword\" }")]
    [InlineData("new AffixBlock { Name = \"Keen\", Stat = Stat.CritChance, Tiers = [new AffixTier { Min = 0.02, Max = 0.05 }] }")]
    [InlineData("new AffixPoolBlock { Affixes = [new AffixEntry { Affix = \"keen\" }] }")]
    public async Task Non_item_definition_blocks_are_not_recipe_ingredients(string definition)
    {
        var d = await Run(A, Mod($$"""
            Blocks.Define("definition", {{definition}});
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["definition"] = 1 } };
            """));
        Ids(d, "KILN111");
        Assert.Contains("definition", d[0].GetMessage());
    }

    [Fact]
    public async Task A_game_cue_is_named_by_its_guid_only()
    {
        // SoundId.Named looks a name up in the mod's own cook index: a GameSounds name is no cue there
        var d = await Run(A, Mod("""Blocks.Define("a", new EffectBlock { Sound = "Chime" });"""));
        Ids(d, "KILN111");
        Assert.Contains("GameSounds.Guids", d[0].GetMessage());
    }

    [Fact]
    public async Task The_mods_data_packs_count_and_the_game_catalog_does_not()
    {
        // an item a data pack adds is the mod's; the catalog (KilnCatalog) holds every game string, so it must not answer for a misspelled name
        var d = await Run(A, Mod("""
            var r = new RecipeBlock { Needs = new Dictionary<ItemRef, int> { ["Kiln_Sample_Glacier"] = 1, ["Iron_Swrod"] = 1 } };
            """),
            files: [("/mod/content/glacier.json", """{ "format": 1, "newItems": [ { "name": "Kiln_Sample_Glacier", "base": "Iron_Sword" } ] }""")],
            catalog: """{ "items": [ { "name": "Iron_Swrod" } ] }""");
        Ids(d, "KILN111");
        Assert.Contains("Iron_Swrod", d[0].GetMessage());
    }
}
