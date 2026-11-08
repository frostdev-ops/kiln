// SPDX-License-Identifier: MIT
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
            Blocks.Define("b", new EffectBlock { Sound = "Chime" });
            Blocks.Define("c", new EffectBlock { Sound = "2f3d7c11-90ab-4cde-8f01-23456789abcd" });
            Blocks.Define("frosty", new PerkBlock { Name = "Frosty" });
            var r = new RecipeBlock { Station = "hand", Needs = new Dictionary<ItemRef, int> { ["Iron_Sword"] = 2, ["other-mod:gem"] = 1 } };
            var s = new RecipeBlock { Station = "0x1a2b3c4d" };
            IReadOnlyList<PerkRef> perks = ["Perk_Frost_Damage", "frosty", "none", "item:Iron_Sword"];
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
}
