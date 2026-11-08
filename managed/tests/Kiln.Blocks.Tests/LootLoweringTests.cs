// SPDX-License-Identifier: MIT
// Loot tables, creature drops and recipes of their own, with no game: what they lower to, what is refused by block and field, how a table rolls, and that a
// data file's form says what the C# says. The death itself, the pickups and the catalog are the lab's (mods/tests/blocks-loot-test).
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class LootLoweringTests : IDisposable
{
    private static readonly (Func<bool> Tick, Func<bool> Client, Func<bool> Running) Real = (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning);

    public LootLoweringTests()
    {
        // a creature block compiled as a game client does: its server part waits, its Lowered is written
        NpcServerSide.OnTickThread = () => false;
        NpcServerSide.IsClient = () => true;
        NpcServerSide.ServerRunning = () => false;
    }

    public void Dispose() => (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning) = Real;

    private static ModBlocks Mod(string id, string clients = "kiln") => new(id, 1) { ManifestClients = clients };

    private static JsonObject Lower<T>(ModBlocks mod, string name, T b) where T : class, IBlock => Compiler.Lower(mod, name, b).Low.Json;

    private static readonly LootTableBlock Scrap = new()
    {
        Entries =
        [
            new LootEntry { Item = "Material_T1_Metal_Scraps", CountMin = 2, CountMax = 5 },
            new LootEntry { Item = "Material_T1_Twigs", CountMin = 1, Chance = 0.25 },
        ],
    };

    // ---- the table ----

    [Fact]
    public void A_table_lowers_its_entries_with_the_defaults_filled_in()
    {
        var t = Lower(Mod("loot-a", "none"), "scrap", Scrap)["lootTable"]!;
        Assert.Equal(1, t["rolls"]!.GetValue<int>());
        Assert.Equal("each", t["pick"]!.GetValue<string>());
        var first = t["entries"]![0]!;
        Assert.Equal("Material_T1_Metal_Scraps", first["item"]!.GetValue<string>());
        Assert.Equal(2, first["countMin"]!.GetValue<int>());
        Assert.Equal(5, first["countMax"]!.GetValue<int>());
        Assert.Equal(1, first["chance"]!.GetValue<double>());
        var second = t["entries"]![1]!;
        Assert.Equal(1, second["countMax"]!.GetValue<int>());     // no countMax: as many as countMin says
        Assert.Equal(0.25, second["chance"]!.GetValue<double>());
    }

    [Fact]
    public void A_table_asks_nothing_of_players_games_but_a_recipe_asks_for_kiln()
    {
        // clients "none": a table is the server's alone
        Lower(Mod("loot-none", "none"), "scrap", Scrap);
        var e = Assert.Throws<BlockError>(() => Lower(Mod("recipe-none", "none"), "r", new RecipeBlock { Output = "Material_T1_Charcoal", Needs = { ["Material_T1_Wood_Logs"] = 2 } }));
        Assert.Contains("clients", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("entries", "entries")]
    [InlineData("rolls", "rolls")]
    [InlineData("item", "entries[0].item")]
    [InlineData("countMin", "entries[0].countMin")]
    [InlineData("countMax", "entries[0].countMax")]
    [InlineData("chance", "entries[0].chance")]
    [InlineData("weight", "entries[0].weight")]
    [InlineData("zeroWeights", "entries")]
    [InlineData("hugeWeights", "entries")]
    public void Wrong_values_are_refused_by_block_and_field(string what, string field)
    {
        static LootEntry E(Func<LootEntry, LootEntry>? edit = null) => (edit ?? (x => x))(new LootEntry { Item = "Material_T1_Twigs" });
        var table = what switch
        {
            "entries" => new LootTableBlock { Entries = [] },
            "rolls" => new LootTableBlock { Entries = [E()], Rolls = 65 },
            "item" => new LootTableBlock { Entries = [E(x => x with { Item = " " })] },
            "countMin" => new LootTableBlock { Entries = [E(x => x with { CountMin = 0 })] },
            "countMax" => new LootTableBlock { Entries = [E(x => x with { CountMin = 4, CountMax = 3 })] },
            "chance" => new LootTableBlock { Entries = [E(x => x with { Chance = 1.5 })] },
            "weight" => new LootTableBlock { Entries = [E(x => x with { Weight = double.NaN })] },
            "zeroWeights" => new LootTableBlock { Entries = [E(x => x with { Weight = 0 })], Pick = LootPick.One },
            "hugeWeights" => new LootTableBlock { Entries = [E(x => x with { Weight = double.MaxValue }), E(x => x with { Weight = double.MaxValue })], Pick = LootPick.One },
            _ => throw new ArgumentException(what),
        };
        var e = Assert.Throws<BlockError>(() => Lower(Mod("loot-bad"), "bad", table));
        Assert.Equal(field, e.Field);
        Assert.Contains("bad", e.Message, StringComparison.Ordinal);        // the block, as every refusal names it
    }

    [Fact]
    public void Rolls_follow_the_entries_and_the_seed()
    {
        var plan = Compiler.LootPlanOf(new BlockContext(Mod("loot-roll"), "loot table roll"), Scrap, "");
        var a = plan.Roll(new Random(5));
        var b = plan.Roll(new Random(5));
        Assert.Equal(a, b);                                                       // the same seed, the same roll
        var scraps = a.Single(s => s.Item == "Material_T1_Metal_Scraps");
        Assert.InRange(scraps.Count, 2, 5);
        var rng = new Random(11);
        var twigs = Enumerable.Range(0, 2000).Count(_ => plan.Roll(rng).Any(s => s.Item == "Material_T1_Twigs"));
        Assert.InRange(twigs, 400, 600);                                          // chance 0.25 of 2000
    }

    [Fact]
    public void Each_tries_every_entry_and_One_picks_one_by_weight()
    {
        var ctx = new BlockContext(Mod("loot-pick"), "loot table pick");
        var entries = new[] { new LootEntry { Item = "Material_T1_Twigs", Weight = 1 }, new LootEntry { Item = "Material_T1_Plant_Fibre", Weight = 3 } };
        var each = Compiler.LootPlanOf(ctx, new LootTableBlock { Entries = entries }, "");
        Assert.Equal(2, each.Roll(new Random(1)).Count);                          // both, each at chance 1
        var one = Compiler.LootPlanOf(ctx, new LootTableBlock { Entries = entries, Pick = LootPick.One }, "");
        var rng = new Random(3);
        var picked = Enumerable.Range(0, 2000).Select(_ => Assert.Single(one.Roll(rng)).Item).ToList();
        Assert.InRange(picked.Count(x => x == "Material_T1_Plant_Fibre"), 1350, 1650);   // 3 of 4
        // a weight of 0 is never picked
        var never = Compiler.LootPlanOf(ctx, new LootTableBlock { Entries = [entries[0] with { Weight = 0 }, entries[1]], Pick = LootPick.One }, "");
        Assert.All(Enumerable.Range(0, 200).Select(_ => Assert.Single(never.Roll(rng))), s => Assert.Equal("Material_T1_Plant_Fibre", s.Item));
    }

    [Fact]
    public void One_never_falls_back_to_an_entry_of_weight_0()
    {
        // a Random at the very top of its range: rounding must not hand the roll to the last entry when that one cannot be picked
        var plan = Compiler.LootPlanOf(new BlockContext(Mod("loot-edge"), "loot table edge"),
            new LootTableBlock { Pick = LootPick.One, Entries = [new LootEntry { Item = "Material_T1_Twigs", Weight = 0.2 }, new LootEntry { Item = "Material_T1_Plant_Fibre", Weight = 0.1 }, new LootEntry { Item = "Material_T1_Stone", Weight = 0 }] }, "");
        Assert.Equal("Material_T1_Plant_Fibre", Assert.Single(plan.Roll(new TopRandom())).Item);
    }

    private sealed class TopRandom : Random
    {
        public override double NextDouble() => 1.0;   // past what Random gives, as rounding can make x
        public override int Next(int minValue, int maxValue) => minValue;
    }

    [Fact]
    public void A_reloading_mod_forgets_its_creatures_drops()
    {
        var plan = Compiler.LootPlanOf(new BlockContext(Mod("loot-forget"), "loot table forget"), Scrap, "");
        LootRuntime.Register("loot-forget.v1", "loot-forget", "v1", plan);
        LootRuntime.Register("loot-other.v1", "loot-other", "v1", plan);
        new ModBlocks("loot-forget", 1).Reset();
        Assert.False(LootRuntime.HasDrops("loot-forget.v1"));
        Assert.True(LootRuntime.HasDrops("loot-other.v1"));
        LootRuntime.Register("loot-other.v1", "loot-other", "v1", null);
        Assert.False(LootRuntime.HasDrops("loot-other.v1"));
    }

    [Fact]
    public void Rolls_add_up_per_item_and_a_stack_holds_65535()
    {
        var ctx = new BlockContext(Mod("loot-sum"), "loot table sum");
        var three = Compiler.LootPlanOf(ctx, new LootTableBlock { Rolls = 3, Entries = [new LootEntry { Item = "Material_T1_Twigs", CountMin = 4 }] }, "");
        Assert.Equal([new LootStack("Material_T1_Twigs", 12)], three.Roll(new Random(1)));
        var big = Compiler.LootPlanOf(ctx, new LootTableBlock { Rolls = 3, Entries = [new LootEntry { Item = "Material_T1_Twigs", CountMin = 30000 }] }, "");
        Assert.Equal([new LootStack("Material_T1_Twigs", 65535), new LootStack("Material_T1_Twigs", 24465)], big.Roll(new Random(1)));
    }

    [Fact]
    public void A_chance_of_nothing_gives_nothing()
    {
        var plan = Compiler.LootPlanOf(new BlockContext(Mod("loot-zero"), "loot table zero"), new LootTableBlock { Entries = [new LootEntry { Item = "Material_T1_Twigs", Chance = 0 }] }, "");
        Assert.Empty(plan.Roll(new Random(1)));
    }

    [Fact]
    public void A_table_names_the_mods_items_by_their_block_names()
    {
        var mod = Mod("loot-own");
        mod.Handles["ember-shard"] = new BlockHandle<ItemBlock>("loot-own", "ember-shard", Guid.NewGuid(), "loot-own.ember-shard", new ItemBlock { Name = "Ember Shard" });
        var j = Lower(mod, "cache", new LootTableBlock { Entries = [new LootEntry { Item = "ember-shard" }] })["lootTable"]!;
        Assert.Equal("loot-own.ember-shard", j["entries"]![0]!["item"]!.GetValue<string>());
    }

    [Fact]
    public void A_block_that_is_not_an_item_is_refused_as_one()
    {
        var mod = Mod("loot-kind");
        var table = Compiler.Define(mod, "pile", Scrap);
        var e = Assert.Throws<BlockError>(() => Lower(mod, "other", new LootTableBlock { Entries = [new LootEntry { Item = "pile" }] }));
        Assert.Equal("entries[0].item", e.Field);
        Assert.Contains("not an item", e.Problem, StringComparison.Ordinal);
        Assert.IsType<LootTableHandle>(table);
        var r = Assert.Throws<BlockError>(() => Lower(mod, "makes-pile", new RecipeBlock { Output = "pile", Needs = { ["Material_T1_Twigs"] = 1 } }));
        Assert.Equal("output", r.Field);
    }

    // ---- the data file says what the C# says ----

    [Fact]
    public void The_JSON_form_is_the_same_table()
    {
        const string json = """
            { "rolls": 2, "pick": "one", "entries": [ { "item": "Material_T1_Twigs", "countMin": 2, "countMax": 4, "chance": 0.5, "weight": 3 }, { "item": "Material_T1_Plant_Fibre" } ] }
            """;
        var t = BlockJson.Parse<LootTableBlock>(json);
        Assert.Equal(2, t.Rolls);
        Assert.Equal(LootPick.One, t.Pick);
        Assert.Equal(new LootEntry { Item = "Material_T1_Twigs", CountMin = 2, CountMax = 4, Chance = 0.5, Weight = 3 }, t.Entries[0]);
        Assert.Equal(new LootEntry { Item = "Material_T1_Plant_Fibre" }, t.Entries[1]);
        var js = Mod("loot-json");
        BlockJson.Make<LootTableBlock>(js, "twin", JsonDocument.Parse(json).RootElement);
        var cs = Mod("loot-json");
        Compiler.Define(cs, "twin", t);
        Assert.Equal(cs.Lowered["twin"].ToJsonString(), js.Lowered["twin"].ToJsonString());
    }

    [Fact]
    public void A_misspelt_field_is_refused()
    {
        Assert.Throws<JsonException>(() => BlockJson.Parse<LootTableBlock>("""{ "entries": [ { "item": "Material_T1_Twigs", "count": 3 } ] }"""));
        Assert.Throws<JsonException>(() => BlockJson.Parse<LootTableBlock>("""{ "entries": [ { "countMin": 3 } ] }"""));   // no item
        Assert.Throws<JsonException>(() => BlockJson.Parse<LootTableBlock>("""{ "pick": "all", "entries": [ { "item": "Material_T1_Twigs" } ] }"""));
    }

    [Fact]
    public void Both_sections_are_kinds_of_data_file()
    {
        var kinds = ItemJson.Kinds.Select(k => k.Section).ToList();
        Assert.Contains("lootTables", kinds);
        Assert.Contains("recipes", kinds);
    }

    // ---- creature drops ----

    [Fact]
    public void A_creature_names_a_table_of_the_mod_or_writes_one_inline()
    {
        var mod = Mod("loot-creatures");
        Compiler.Define(mod, "scrap", Scrap);
        Compiler.Define(mod, "named", new CreatureBlock { Body = Bodies.Vukah, Drops = "scrap" });
        Compiler.Define(mod, "inline", new CreatureBlock { Body = Bodies.Vukah, Drops = new LootTableBlock { Rolls = 2, Entries = [new LootEntry { Item = "Material_T1_Twigs", CountMin = 7 }] } });
        Compiler.Define(mod, "plain", new CreatureBlock { Body = Bodies.Vukah });
        var named = mod.Lowered["named"]["creature"]!["drops"]!;
        Assert.Equal("Material_T1_Metal_Scraps", named["entries"]![0]!["item"]!.GetValue<string>());
        var inline = mod.Lowered["inline"]["creature"]!["drops"]!;
        Assert.Equal(2, inline["rolls"]!.GetValue<int>());
        Assert.Equal(7, inline["entries"]![0]!["countMin"]!.GetValue<int>());
        Assert.Null(mod.Lowered["plain"]["creature"]!["drops"]);
    }

    [Fact]
    public void A_creature_refuses_drops_that_are_not_a_table_by_block_and_field()
    {
        var mod = Mod("loot-creature-bad");
        mod.Handles["a-spell"] = new BlockHandle<EffectBlock>("loot-creature-bad", "an-effect", Guid.NewGuid(), "loot-creature-bad.an-effect", new EffectBlock());
        var wrongKind = Assert.Throws<BlockError>(() => Compiler.Define(mod, "c1", new CreatureBlock { Body = Bodies.Vukah, Drops = "a-spell" }));
        Assert.Equal("drops", wrongKind.Field);
        var missing = Assert.Throws<BlockError>(() => Compiler.Define(mod, "c2", new CreatureBlock { Body = Bodies.Vukah, Drops = "nowhere" }));
        Assert.Equal("drops", missing.Field);
        Assert.Contains("nowhere", missing.Problem, StringComparison.Ordinal);
        // an inline table is checked the same way, with the field under drops
        var bad = Assert.Throws<BlockError>(() => Compiler.Define(mod, "c3", new CreatureBlock { Body = Bodies.Vukah, Drops = new LootTableBlock { Entries = [new LootEntry { Item = "Material_T1_Twigs", Chance = 2 }] } }));
        Assert.Equal("drops.entries[0].chance", bad.Field);
    }

    [Fact]
    public void A_creature_in_a_data_file_takes_drops_by_name_or_inline()
    {
        var mod = Mod("loot-creature-json");
        Compiler.Define(mod, "scrap", Scrap);
        BlockJson.Make<CreatureBlock>(mod, "by-name", JsonDocument.Parse("""{ "body": "vukah", "drops": "scrap" }""").RootElement);
        BlockJson.Make<CreatureBlock>(mod, "inline", JsonDocument.Parse("""{ "body": "vukah", "drops": { "entries": [ { "item": "Material_T1_Twigs", "countMin": 3 } ] } }""").RootElement);
        Assert.Equal(2, mod.Lowered["by-name"]["creature"]!["drops"]!["entries"]!.AsArray().Count);
        Assert.Equal(3, mod.Lowered["inline"]["creature"]!["drops"]!["entries"]![0]!["countMin"]!.GetValue<int>());
    }

    // ---- the item's own place in the game's tables is still its own ----

    [Fact]
    public void An_item_still_goes_into_the_games_tables_by_its_drop_places()
    {
        var j = Lower(Mod("loot-places"), "shard", new ItemBlock
        {
            Name = "Shard",
            Loot = new LootBlock { Tables = [new DropPlace { DropsLike = "Material_T1_Metal_Scraps", CountMin = 1, CountMax = 2, Chance = 0.25 }] },
        });
        var drop = j["itemParts"]!["drops"]![0]!;
        Assert.Equal("like:Material_T1_Metal_Scraps", drop["table"]!.GetValue<string>());
        Assert.Equal(0.25, drop["chance"]!.GetValue<double>());
    }

    // ---- a recipe of its own ----

    private static readonly RecipeBlock Charcoal = new()
    {
        Output = "Material_T1_Charcoal", Makes = 2, Station = "0x4da88f73", Needs = { ["Material_T1_Wood_Logs"] = 3, ["Material_T1_Twigs"] = 1 }, Comfort = 2,
    };

    [Fact]
    public void A_recipe_of_its_own_lowers_to_a_recipe_at_its_station()
    {
        var j = Lower(Mod("recipe-a"), "charcoal-fast", Charcoal);
        var r = j["recipes"]!.AsArray().Single()!;
        Assert.Equal("recipe-a.charcoal-fast", r["name"]!.GetValue<string>());
        Assert.Equal("0x4da88f73", r["station"]!.GetValue<string>());
        Assert.Equal(["Material_T1_Wood_Logs", "Material_T1_Twigs"], r["inputs"]!.AsArray().Select(x => x!["item"]!.GetValue<string>()));
        Assert.Equal([3, 1], r["inputs"]!.AsArray().Select(x => x!["count"]!.GetValue<int>()));
        var output = r["outputs"]!.AsArray().Single()!;
        Assert.Equal("Material_T1_Charcoal", output["item"]!.GetValue<string>());
        Assert.Equal(2, output["count"]!.GetValue<int>());
        Assert.Equal("Material_T1_Charcoal", j["recipeOf"]!.GetValue<string>());
        Assert.Equal("2", r["fields"]!["comfortLevel"]!.GetValue<string>());
    }

    [Fact]
    public void A_recipe_has_the_same_id_for_the_same_mod_and_name()
    {
        var a = Compiler.Lower(Mod("recipe-id"), "charcoal-fast", Charcoal);
        var b = Compiler.Lower(Mod("recipe-id"), "charcoal-fast", Charcoal);
        Assert.Equal(a.Handle.Guid, b.Handle.Guid);
        Assert.Equal(a.Low.Json["recipes"]![0]!["guid"]!.GetValue<string>(), a.Handle.Guid.ToString());
        Assert.NotEqual(a.Handle.Guid, Compiler.Lower(Mod("recipe-id"), "charcoal-slow", Charcoal).Handle.Guid);
        Assert.NotEqual(a.Handle.Guid, Compiler.Lower(Mod("recipe-other"), "charcoal-fast", Charcoal).Handle.Guid);
    }

    [Fact]
    public void A_recipe_names_its_own_fields_when_it_is_wrong()
    {
        var mod = Mod("recipe-bad");
        Assert.Equal("output", Assert.Throws<BlockError>(() => Lower(mod, "r", Charcoal with { Output = null })).Field);
        Assert.Equal("needs", Assert.Throws<BlockError>(() => Lower(mod, "r", Charcoal with { Needs = new Dictionary<ItemRef, int>() })).Field);
        Assert.Equal("makes", Assert.Throws<BlockError>(() => Lower(mod, "r", Charcoal with { Makes = 0 })).Field);
        Assert.Equal("needs[Material_T1_Twigs]", Assert.Throws<BlockError>(() => Lower(mod, "r", Charcoal with { Needs = new Dictionary<ItemRef, int> { ["Material_T1_Twigs"] = 0 } })).Field);
        Assert.Equal("seconds", Assert.Throws<BlockError>(() => Lower(mod, "r", Charcoal with { Seconds = 0 })).Field);
        var station = Assert.Throws<BlockError>(() => Lower(mod, "r", Charcoal with { Station = "no-such-station" }));
        Assert.Equal("station", station.Field);
        Assert.Contains("not a station", station.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inline_recipe_still_says_output_is_for_a_stations_list()
    {
        var e = Assert.Throws<BlockError>(() => Lower(Mod("recipe-inline"), "x", new ItemBlock { Name = "X", Recipe = Charcoal }));
        Assert.Equal("recipe.output", e.Field);
    }

    [Fact]
    public void The_JSON_recipe_is_the_same_recipe()
    {
        const string json = """{ "output": "Material_T1_Charcoal", "makes": 2, "station": "0x4da88f73", "needs": { "Material_T1_Wood_Logs": 3, "Material_T1_Twigs": 1 }, "comfort": 2 }""";
        var fromJson = BlockJson.Parse<RecipeBlock>(json);
        Assert.Equal(Charcoal.Output, fromJson.Output);
        Assert.Equal(Charcoal.Needs.OrderBy(x => x.Key.Value), fromJson.Needs.OrderBy(x => x.Key.Value));
        Assert.Equal(Lower(Mod("recipe-json"), "charcoal-fast", Charcoal).ToJsonString(), Lower(Mod("recipe-json"), "charcoal-fast", fromJson).ToJsonString());
    }
}
