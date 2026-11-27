// SPDX-License-Identifier: MIT
// PetBlock (19d §5.1, package 1): C# and blocks/*.json lower to the same Kiln.Pets PetDefinition; a game creature is named by
// its creatures-registry id and a creature block by <mod>:<name>; every range is refused by field path; Kiln's defaults are the
// safe ones (players protected, a bodyless recovery anchor) and every one of them can be the mod's other choice.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class PetBlockTests : IDisposable
{
    private static readonly (Func<bool> Tick, Func<bool> Client, Func<bool> Running) Real = (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning);

    public PetBlockTests()
    {
        NpcServerSide.OnTickThread = () => false;
        NpcServerSide.IsClient = () => true;
        NpcServerSide.ServerRunning = () => false;
    }

    public void Dispose() => (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning) = Real;

    private static ModBlocks Mod(string id, string clients = "none") => new(id, 1) { ManifestClients = clients };

    private static JsonObject Pet(ModBlocks mod, string name, PetBlock b) => (JsonObject)Compiler.Lower(mod, name, b).Low.Json["pet"]!;

    [Fact]
    public void A_pet_on_a_game_creature_keeps_its_registry_id_and_asks_nothing_of_players_games()
    {
        var pet = Pet(Mod("pets-a"), "hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf" });
        Assert.Equal("pets-a:hound", pet["id"]!.GetValue<string>());
        Assert.Equal("game:enemy_wildbeast_wolf", pet["creature"]!.GetValue<string>());
    }

    [Fact]
    public void The_defaults_are_the_safe_ones()
    {
        var pet = Pet(Mod("pets-b"), "hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf" });
        Assert.Equal("off", pet["targets"]!["pvp"]!.GetValue<string>());
        Assert.Null(pet["targets"]!["hostileFactions"]);
        Assert.Equal("recoveryAnchor", pet["downed"]!["outcome"]!.GetValue<string>());
        Assert.Equal("queryInput", pet["movement"]!["mode"]!.GetValue<string>());
        Assert.Equal(3, pet["revive"]!["channelSeconds"]!.GetValue<double>());
        Assert.Null(pet["revive"]!["cost"]);
        Assert.Equal(1, pet["limits"]!["livePerCharacter"]!.GetValue<int>());
        Assert.True(pet["hud"]!["bar"]!.GetValue<bool>());
    }

    // API-SHAPE-2 A3: a pet's limits are "limits" (PetLimits), never "caps" (capabilities are rows, not counts)
    [Fact]
    public void A_pets_limits_are_limits_in_json_and_caps_is_not_a_field()
    {
        var p = BlockJson.Parse<PetBlock>("""{ "creature": "game:enemy_wildbeast_wolf", "limits": { "livePerCharacter": 2 } }""");
        Assert.Equal(2, p.Limits.LivePerCharacter);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<PetBlock>("""{ "creature": "game:enemy_wildbeast_wolf", "caps": { "livePerCharacter": 2 } }"""));
    }

    // API-SHAPE-2 C2: durations are TimeSpan in C#; blocks/*.json keeps its seconds
    [Fact]
    public void Json_seconds_become_TimeSpan_durations()
    {
        var p = BlockJson.Parse<PetBlock>("""
            { "creature": "game:enemy_wildbeast_wolf", "movement": { "repathSeconds": 1.25, "stuckSeconds": 8 }, "revive": { "channelSeconds": 4.5 } }
            """);
        Assert.Equal((TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(4.5)), (p.Movement.RepathInterval, p.Movement.StuckAfter, p.Revive.ChannelDuration));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<PetBlock>("""{ "creature": "game:enemy_wildbeast_wolf", "revive": { "channelSeconds": "3" } }"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<PetBlock>("""{ "creature": "game:enemy_wildbeast_wolf", "revive": { "channelSeconds": 1e300 } }"""));
        var t = BlockJson.Parse<TamingBlock>("""{ "creatures": ["game:enemy_wildbeast_wolf"], "steps": [ { "kind": "native" } ], "inactivitySeconds": 45, "maxAttemptSeconds": 90 }""");
        Assert.Equal((TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(90)), (t.InactivityTimeout, t.MaxAttemptDuration));
    }

    [Fact]
    public void Every_choice_is_the_mods()
    {
        var pet = Pet(Mod("pets-c"), "duelist", new PetBlock
        {
            Creature = "game:enemy_wildbeast_wolf",
            Targets = new PetTargetPolicy { Pvp = PetPvpMode.ByModRule, HostileFactions = [Faction.Vukah] },
            Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Release },
            Movement = new PetMovementPolicy { FollowDistance = 5, RunDistance = 12, AllowCatchUp = false },
            Revive = new ReviveBlock { Allowed = false },
            Limits = new PetLimits { LivePerCharacter = 3 },
            Hud = new PetHudPolicy { Bar = false },
        });
        Assert.Equal("byModRule", pet["targets"]!["pvp"]!.GetValue<string>());
        Assert.Equal("vukah", pet["targets"]!["hostileFactions"]![0]!.GetValue<string>());
        Assert.Equal("release", pet["downed"]!["outcome"]!.GetValue<string>());
        Assert.False(pet["movement"]!["allowCatchUp"]!.GetValue<bool>());
        Assert.False(pet["revive"]!["allowed"]!.GetValue<bool>());
        Assert.Equal(3, pet["limits"]!["livePerCharacter"]!.GetValue<int>());
        Assert.False(pet["hud"]!["bar"]!.GetValue<bool>());
    }

    [Fact]
    public void Csharp_and_json_lower_to_the_same_definition()
    {
        var cs = Pet(Mod("pets-d"), "hound", new PetBlock
        {
            Creature = "game:enemy_wildbeast_wolf", Name = "Companion",
            Level = new PetLevelPolicy { Mode = PetLevelMode.OwnerAtSummon, Level = 4, Scale = 1.5 },
            Movement = new PetMovementPolicy { FollowDistance = 4, RepathInterval = TimeSpan.FromSeconds(1), RunDistance = 9, CatchUpDistance = 40, StuckAfter = TimeSpan.FromSeconds(6), GroundSearchMeters = 2 },
            Revive = new ReviveBlock { ChannelDuration = TimeSpan.FromSeconds(5), RangeMeters = 2, HealthFraction = 0.25, CancelOnMovement = false, MaxOwnerDisplacement = 1 },
            Downed = new PetDownedPolicy { Outcome = PetDownedOutcome.Absent },
        });
        var block = BlockJson.Parse<PetBlock>("""
            {
              "creature": "game:enemy_wildbeast_wolf", "name": "Companion",
              "level": { "mode": "ownerAtSummon", "level": 4, "scale": 1.5 },
              "movement": { "followDistance": 4, "repathSeconds": 1, "runDistance": 9, "catchUpDistance": 40, "stuckSeconds": 6, "groundSearchMeters": 2 },
              "revive": { "channelSeconds": 5, "rangeMeters": 2, "healthFraction": 0.25, "cancelOnMovement": false, "maxOwnerDisplacement": 1 },
              "downed": { "outcome": "absent" }
            }
            """);
        var json = Pet(Mod("pets-d"), "hound", block);
        Assert.Equal(cs.ToJsonString(), json.ToJsonString());
    }

    [Fact]
    public void A_creature_block_is_named_by_its_mod_and_name()
    {
        var mod = Mod("pets-e");
        var wolf = Compiler.Define(mod, "wolf", new CreatureBlock { Body = Bodies.Vukah });
        var byHandle = Pet(mod, "wolf-pet", new PetBlock { Creature = (BlockHandle<CreatureBlock>)wolf });
        Assert.Equal("pets-e:wolf", byHandle["creature"]!.GetValue<string>());
        var byName = Pet(mod, "wolf-pet-2", new PetBlock { Creature = "wolf" });
        Assert.Equal("pets-e:wolf", byName["creature"]!.GetValue<string>());
        var inline = Pet(mod, "inline-pet", new PetBlock { Creature = new CreatureBlock { Body = Bodies.Vukah } });
        Assert.Equal("pets-e:inline-pet.creature", inline["creature"]!.GetValue<string>());
    }

    [Fact]
    public void A_name_that_is_no_creature_is_refused()
    {
        var mod = Mod("pets-f");
        Compiler.Define(mod, "scrap", new LootTableBlock { Entries = [new LootEntry { Item = "Material_T1_Twigs" }] });
        var e = Assert.Throws<BlockError>(() => Compiler.Lower(mod, "p", new PetBlock { Creature = "scrap" }));
        Assert.Equal("creature", e.Field);
        var missing = Assert.Throws<BlockError>(() => Compiler.Lower(mod, "p", new PetBlock { Creature = "nowhere" }));
        Assert.Equal("creature", missing.Field);
        Assert.Contains("game:<name>", missing.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("level.level")]
    [InlineData("level.scale")]
    [InlineData("movement.followDistance")]
    [InlineData("movement.repathSeconds")]
    [InlineData("movement.runDistance")]
    [InlineData("movement.catchUpDistance")]
    [InlineData("movement.stuckSeconds")]
    [InlineData("movement.groundSearchMeters")]
    [InlineData("revive.channelSeconds")]
    [InlineData("revive.rangeMeters")]
    [InlineData("revive.healthFraction")]
    [InlineData("revive.maxOwnerDisplacement")]
    [InlineData("revive.cost.count")]
    [InlineData("targets.hostileFactions")]
    [InlineData("limits.retainedPerCharacter")]
    [InlineData("limits.livePerCharacter")]
    [InlineData("limits.livePerWorld")]
    [InlineData("name")]
    public void Wrong_values_are_refused_by_field(string field)
    {
        var ok = new PetBlock { Creature = "game:enemy_wildbeast_wolf" };
        var bad = field switch
        {
            "level.level" => ok with { Level = new PetLevelPolicy { Level = 256 } },
            "level.scale" => ok with { Level = new PetLevelPolicy { Scale = 9 } },
            "movement.followDistance" => ok with { Movement = new PetMovementPolicy { FollowDistance = 0.5 } },
            "movement.repathSeconds" => ok with { Movement = new PetMovementPolicy { RepathInterval = TimeSpan.Zero } },
            "movement.runDistance" => ok with { Movement = new PetMovementPolicy { FollowDistance = 8, RunDistance = 5 } },
            "movement.catchUpDistance" => ok with { Movement = new PetMovementPolicy { CatchUpDistance = 200 } },
            "movement.stuckSeconds" => ok with { Movement = new PetMovementPolicy { StuckAfter = TimeSpan.FromSeconds(31) } },
            "movement.groundSearchMeters" => ok with { Movement = new PetMovementPolicy { GroundSearchMeters = 9 } },
            "revive.channelSeconds" => ok with { Revive = new ReviveBlock { ChannelDuration = TimeSpan.FromSeconds(61) } },
            "revive.rangeMeters" => ok with { Revive = new ReviveBlock { RangeMeters = 0.5 } },
            "revive.healthFraction" => ok with { Revive = new ReviveBlock { HealthFraction = 0 } },
            "revive.maxOwnerDisplacement" => ok with { Revive = new ReviveBlock { MaxOwnerDisplacement = 3 } },
            "revive.cost.count" => ok with { Revive = new ReviveBlock { Cost = new ReviveCostBlock("Material_T1_Twigs", 0) } },
            "targets.hostileFactions" => ok with { Targets = new PetTargetPolicy { HostileFactions = [Faction.Player] } },
            "limits.retainedPerCharacter" => ok with { Limits = new PetLimits { RetainedPerCharacter = 129 } },
            "limits.livePerCharacter" => ok with { Limits = new PetLimits { LivePerCharacter = 0 } },
            "limits.livePerWorld" => ok with { Limits = new PetLimits { LivePerWorld = 257 } },
            "name" => ok with { Name = new string('x', 65) },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var e = Assert.Throws<BlockError>(() => Compiler.Lower(Mod("pets-g"), "p", bad));
        Assert.Equal(field, e.Field);
    }

    // The SDK's bake compiles blocks against a catalog that is readable and lacks most items (31.19's PetsSample build:
    // "revive.cost.item: the game has no item Material_T1_Twigs"); a game client's OnLoad has no items at all. An item name
    // is checked once content is ready, as loot tables and furniture do, never refused while compiling.
    [Fact]
    public void A_cost_item_is_checked_once_content_is_ready_not_refused_while_compiling()
    {
        var real = PetCompiler.FindItem;
        try
        {
            var block = new PetBlock { Creature = "game:enemy_wildbeast_wolf", Revive = new ReviveBlock { Cost = new ReviveCostBlock("Material_T1_Twigs", 3) } };
            PetCompiler.FindItem = _ => null;
            var missing = Mod("pets-cost-a");
            Compiler.Lower(missing, "p", block).Low.CommitAfter();
            var problems = new List<string>();
            foreach (var check in missing.ReadyChecks.ToList()) check(problems.Add);
            Assert.Contains("block pet p, revive.cost.item: the game has no item \"Material_T1_Twigs\"", Assert.Single(problems), StringComparison.Ordinal);

            var late = Mod("pets-cost-b");
            Compiler.Lower(late, "p", block).Low.CommitAfter();
            PetCompiler.FindItem = n => n == "Material_T1_Twigs" ? (new ItemId(77), false) : null;
            problems.Clear();
            foreach (var check in late.ReadyChecks.ToList()) check(problems.Add);
            Assert.Empty(problems);
            Assert.Equal(77u, PetsRuntime.Definition("pets-cost-b:p")!.Revive.Cost!.Item.Value);

            PetCompiler.FindItem = _ => (new ItemId(78), true);
            var gear = Mod("pets-cost-c");
            var e = Assert.Throws<BlockError>(() => Compiler.Lower(gear, "p", block));
            Assert.Equal("revive.cost.item", e.Field);
        }
        finally
        {
            PetCompiler.FindItem = real;
        }
    }

    // A dedicated server reads its item catalog (kiln.items) only once its world runs, after content is ready; the game's data
    // (kiln.content) has the items then, as loot tables use it. PETS-E2E lab a3: PetsSample's duelist got "revive.cost.item: the
    // game has no item Material_T1_Twigs" at content ready and every revive of it was refused CostUnavailable ("only 0 of 3
    // there") while its owner held 10 twigs.
    [Fact]
    public void A_cost_item_resolves_through_the_games_data_before_the_item_catalog_is_readable()
    {
        var (catalog, data) = (PetCompiler.CatalogItem, PetCompiler.DataItem);
        try
        {
            PetCompiler.CatalogItem = _ => null;   // a dedicated server before its world runs
            PetCompiler.DataItem = n => n == "Material_T1_Twigs" ? new ItemId(77) : null;
            var block = new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame", Revive = new ReviveBlock { Cost = new ReviveCostBlock("Material_T1_Twigs", 3) } };
            var mod = Mod("pets-cost-server");
            Compiler.Lower(mod, "p", block).Low.CommitAfter();
            var problems = new List<string>();
            foreach (var check in mod.ReadyChecks.ToList()) check(problems.Add);
            Assert.Empty(problems);
            Assert.Equal(77u, PetsRuntime.Definition("pets-cost-server:p")!.Revive.Cost!.Item.Value);
        }
        finally
        {
            (PetCompiler.CatalogItem, PetCompiler.DataItem) = (catalog, data);
        }
    }

    [Fact]
    public void A_renamed_pet_keeps_its_id()
    {
        var pet = Pet(Mod("pets-h"), "companion", new PetBlock { Creature = "game:enemy_wildbeast_wolf", Was = "hound" });
        Assert.Equal("pets-h:hound", pet["id"]!.GetValue<string>());
    }
}
