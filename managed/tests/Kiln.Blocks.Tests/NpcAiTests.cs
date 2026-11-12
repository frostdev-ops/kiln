// SPDX-License-Identifier: MIT
// AIBlock's newer fields and the rule kinds of Ai/RuleKinds.cs, with no game: what they lower to, what is refused by block and field, and that a data
// file's forms say what the C# says. Compiled as a game client does (off the tick thread): the creature's server part waits, its Lowered is written.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class NpcAiTests : IDisposable
{
    private static readonly (Func<bool> Tick, Func<bool> Client, Func<bool> Running) Real = (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning);

    public NpcAiTests()
    {
        NpcServerSide.OnTickThread = () => false;
        NpcServerSide.IsClient = () => true;
        NpcServerSide.ServerRunning = () => false;
    }

    public void Dispose() => (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning) = Real;

    private static ModBlocks Mod(string id) => new(id, 1);

    private static readonly AIBlock Tuned = AIs.Vukah with
    {
        NightSight = 0.4, Hears = false, Hearing = 1.5, Threat = 2, HelpRadius = 12, CallsForHelp = true, ReturnsHome = false,
        Roam = new RoamBlock { Min = 2, Max = 8, FromHome = 30 },
        Flee = new FleeBlock { Below = 0.3, Min = 10, Max = 20, Seconds = 6, Cooldown = 15 },
        Enable = EnemyFeatures.Prowl, Disable = EnemyFeatures.UseTaunt,
        Relations = new Dictionary<Faction, Relation> { [Faction.Player] = Relation.Neutral, [Faction.Scavenger] = Relation.Hostile },
    };

    [Fact]
    public void The_fields_become_the_AIs_values()
    {
        var v = new SortedDictionary<string, string>();
        AiLowering.Values(Tuned, v);
        Assert.Equal("0.4", v["combatCommitmentSetup.view.nightRangeFactor"]);
        Assert.Equal("false", v["noiseListenerSetup.isEnabled"]);
        Assert.Equal("1.5", v["noiseListenerSetup.listenRangeFactor"]);
        Assert.Equal("2", v["threatFactor"]);
        Assert.Equal("12", v["callForHelpSetup.radius"]);
        Assert.Equal("2", v["roamingSetup.minRadius"]);
        Assert.Equal("8", v["roamingSetup.maxRadius"]);
        Assert.Equal("30", v["roamingSetup.maxHomeZoneDistance"]);
        Assert.Equal("10", v["fleeSetup.minFleeRadius"]);
        Assert.Equal("20", v["fleeSetup.maxFleeRadius"]);
        Assert.Equal("6", v["fleeSetup.maxFleeDuration"]);
    }

    [Fact]
    public void The_feature_switches_follow_the_fields()
    {
        Assert.Equal(EnemyFeatures.Prowl | EnemyFeatures.Roaming | EnemyFeatures.CallForHelp, AiLowering.Enabled(Tuned));
        Assert.Equal(EnemyFeatures.UseTaunt | EnemyFeatures.ReturnHome | EnemyFeatures.FleeHome, AiLowering.Disabled(Tuned));
        Assert.Equal(EnemyFeatures.CallForHelp, AiLowering.Disabled(AIs.Vukah with { CallsForHelp = false }));
        Assert.Equal(EnemyFeatures.None, AiLowering.Enabled(AIs.Vukah));
    }

    [Fact]
    public void A_creature_lowers_its_AI_onto_each_creature_and_a_flee_rule_into_its_arsenal()
    {
        var mod = Mod("ai-lower");
        Compiler.Define(mod, "skulker", new CreatureBlock { Body = Bodies.Vukah, AI = Tuned });
        var c = mod.Lowered["skulker"]["creature"]!;
        var ai = c["variantValues"]!["Ai"]!.AsObject();
        Assert.Equal("0.4", ai["combatCommitmentSetup.view.nightRangeFactor"]!.GetValue<string>());
        Assert.Equal("6", ai["fleeSetup.maxFleeDuration"]!.GetValue<string>());
        Assert.Contains("ReturnHome", c["variantValues"]!["Disable"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(c["arsenal"]);   // the flee rule gives it an arsenal of its own
        var rules = mod.Lowered["skulker"]["arsenal"]!["rules"]!.AsArray();
        var flee = rules.Single(r => r!["name"]!.GetValue<string>() == "skulker.flee")!;
        Assert.Equal(15, flee["cooldown"]!.GetValue<double>());
        var text = flee.ToJsonString();
        Assert.Contains("EnemyHealthPercentageConstrain", text, StringComparison.Ordinal);
        Assert.Contains("StateConstrain", text, StringComparison.Ordinal);
        Assert.Contains("TriggerStateConditionAction", text, StringComparison.Ordinal);
        Assert.Contains("WantsToFlee", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_creature_without_the_new_fields_lowers_as_before()
    {
        var mod = Mod("ai-plain");
        Compiler.Define(mod, "plain", new CreatureBlock { Body = Bodies.Vukah, AI = AIs.Vukah with { Sight = 20 } });
        var c = mod.Lowered["plain"]["creature"]!;
        Assert.Null(c["arsenal"]);
        Assert.Equal("None", c["variantValues"]!["Disable"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("flee.below", 1.5)]
    [InlineData("nightSight", -1.0)]
    [InlineData("roam", 9.0)]
    public void Wrong_values_are_refused_by_block_and_field(string field, double value)
    {
        var ai = field switch
        {
            "flee.below" => AIs.Vukah with { Flee = new FleeBlock { Below = value } },
            "nightSight" => AIs.Vukah with { NightSight = value },
            _ => AIs.Vukah with { Roam = new RoamBlock { Min = value, Max = 3 } },
        };
        var e = Assert.Throws<BlockError>(() => Compiler.Define(Mod("ai-bad"), "bad", new CreatureBlock { Body = Bodies.Vukah, AI = ai }));
        Assert.Contains("bad", e.Message, StringComparison.Ordinal);
        Assert.Contains(field, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sense_filter_by_name_must_be_registered()
    {
        var e = Assert.Throws<BlockError>(() => Compiler.Define(Mod("ai-filter"), "picky", new CreatureBlock { Body = Bodies.Vukah, AI = AIs.Vukah with { SenseFilterName = "nobody-registered-this" } }));
        Assert.Contains("senseFilter", e.Message, StringComparison.Ordinal);
        NpcRuntime.Named(AiRuntime.Filters, "registered-filter", (Func<NpcView, EntityId, bool>)((_, _) => true));
        Compiler.Define(Mod("ai-filter2"), "picky", new CreatureBlock { Body = Bodies.Vukah, AI = AIs.Vukah with { SenseFilterName = "registered-filter" } });
    }

    [Fact]
    public void Feature_actions_take_one_feature()
    {
        Assert.Contains("\"feature\":\"Prowl\"", Act.EnableFeature(EnemyFeatures.Prowl).ToJson(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Act.EnableFeature(EnemyFeatures.Prowl | EnemyFeatures.Patrol));
        Assert.Throws<ArgumentException>(() => Act.DisableFeature(EnemyFeatures.None));
    }

    [Fact]
    public void The_game_kinds_are_the_games_types()
    {
        Assert.Contains("IsPlayerTargetConstrain", Is.TargetIsPlayer().ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"isPlayer\":false", Is.TargetIsPlayer(false).ToJson(), StringComparison.Ordinal);
        Assert.Contains("CurrentTargetHealthPercentageConstrain", Is.TargetHealthBelow(0.3).ToJson(), StringComparison.Ordinal);
        Assert.Contains("LastHitReceivedConstrain", Is.HitWithin(2).ToJson(), StringComparison.Ordinal);
        Assert.Contains("RandomChanceConstrain", Is.Chance(0.3).ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"Combat\"", Is.InCombat.ToJson(), StringComparison.Ordinal);
        Assert.Contains("RunAwayAction", Act.BackOff(3, 6).ToJson(), StringComparison.Ordinal);
        Assert.Contains("SetImmuneAction", Act.Immune().ToJson(), StringComparison.Ordinal);
        Assert.Contains("PauseAction", Act.Pause(1, 2).ToJson(), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => Is.Chance(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Act.Heal(-0.1));
    }

    private static JsonNode Rules(ModBlocks mod, string block) => mod.Lowered[block]["arsenal"]!["rules"]!;

    [Fact]
    public void A_data_files_rule_says_what_the_CSharp_says()
    {
        var cs = Mod("ai-twins");
        Compiler.Define(cs, "wary", new CreatureBlock
        {
            Body = Bodies.Vukah,
            Behaviour = Behaviours.VukahMelee with
            {
                Rules =
                [
                    new RuleBlock
                    {
                        Name = "panic", Every = 10, Chance = 0.3, Cooldown = 5,
                        When = [Is.HealthBelow(0.5), Is.TargetIsPlayer(), Is.HitWithin(3), Is.InCombat, Is.TargetHealthBelow(0.9)],
                        Do = [Act.Flee(), Act.Immune(), Act.BackOff(3, 6), Act.Pause(1, 2), Act.EnableFeature(EnemyFeatures.Prowl), Act.Heal(0.25)],
                    },
                ],
            },
        });
        var js = Mod("ai-twins");
        var rule = """
            { "name": "panic", "every": 10, "chance": 0.3, "cooldown": 5,
              "when": [ { "healthBelow": 0.5 }, { "targetIsPlayer": true }, { "hitWithin": 3 }, { "inCombat": true }, { "targetHealthBelow": 0.9 } ],
              "do": [ { "flee": true }, { "immune": true }, { "backOff": [3, 6] }, { "pause": [1, 2] }, { "enableFeature": "Prowl" }, { "heal": 0.25 } ] }
            """;
        var json = $$"""{ "body": "vukah", "behaviour": { "like": "vukah-melee", "rules": [ {{rule}} ] } }""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        BlockJson.Make<CreatureBlock>(js, "wary", doc.RootElement);
        Assert.Equal(Rules(cs, "wary").ToJsonString(), Rules(js, "wary").ToJsonString());
        Assert.Contains("code", Rules(js, "wary").ToJsonString(), StringComparison.Ordinal);   // the heal: Kiln's, queued
    }

    [Fact]
    public void Data_file_forms_are_refused_by_field()
    {
        var mod = Mod("ai-badjson");
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "body": "vukah", "behaviour": { "rules": [ { "when": [ { "chance": 3 } ], "do": [ { "flee": true } ] } ] } }""");
        var e = Assert.Throws<BlockError>(() => BlockJson.Make<CreatureBlock>(mod, "rash", doc.RootElement));
        Assert.Contains("when[0].chance", e.Message, StringComparison.Ordinal);
        using var doc2 = System.Text.Json.JsonDocument.Parse("""{ "body": "vukah", "behaviour": { "rules": [ { "do": [ { "summon": { "creature": "x", "count": 40 } } ] } ] } }""");
        var e2 = Assert.Throws<BlockError>(() => BlockJson.Make<CreatureBlock>(mod, "horde", doc2.RootElement));
        Assert.Contains("do[0].summon", e2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summon_and_its_count_condition_are_Kilns_queued_kinds()
    {
        var mod = Mod("ai-summon");
        Compiler.Define(mod, "caller", new CreatureBlock
        {
            Body = Bodies.Vukah,
            Behaviour = Behaviours.None with
            {
                Rules = [new RuleBlock { Name = "call", Every = 10, Chance = 0.3, When = [Is.HealthBelow(0.5), Is.SummonsBelow(2), Is.EnemiesNear(1, 20)], Do = [Act.Summon("Enemy_Vukah_Melee", 2, seconds: 30)] }],
            },
        });
        var text = Rules(mod, "caller").ToJsonString();
        Assert.Contains("EnemyHealthPercentageConstrain", text, StringComparison.Ordinal);
        Assert.Contains("\"kiln\":\"code\"", text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Act.Summon("x", 0));
    }
    [Theory]
    [InlineData("{\"summon\":null}", "summon")]
    [InlineData("{\"summon\":{\"creature\":\"x\",\"seconds\":-1}}", "summon")]
    [InlineData("{\"summon\":{\"creature\":\" \"}}", "summon")]
    [InlineData("{\"summon\":{\"creature\":\"x\",\"side\":\"99\"}}", "summon")]
    [InlineData("{\"summon\":{\"creature\":\"x\",\"count\":null}}", "summon")]
    [InlineData("{\"summon\":{\"creature\":\"x\",\"side\":\"0\"}}", "summon")]
    [InlineData("{\"enableFeature\":\"1\"}", "enableFeature")]
    [InlineData("{\"pause\":[3,1]}", "pause")]
    [InlineData("{\"backOff\":[null,1]}", "backOff")]
    [InlineData("{\"flee\":false}", "flee")]
    [InlineData("{\"heal\":0.1,\"summon\":\"x\"}", "heal")]
    [InlineData("{\"on\":\"self\",\"apply\":null}", "apply")]
    public void Malformed_actions_report_the_block_and_field(string action, string field)
    {
        var json = "{\"body\":\"vukah\",\"behaviour\":{\"rules\":[{\"do\":[" + action + "]}]}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = Assert.Throws<BlockError>(() => BlockJson.Make<CreatureBlock>(Mod("ai-refuse"), "bad", doc.RootElement));
        Assert.Contains("bad", error.Message, StringComparison.Ordinal);
        Assert.Contains("do[0]." + field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"summonsBelow\":1.5}", "summonsBelow")]
    [InlineData("{\"enemiesNear\":[-1,4]}", "enemiesNear")]
    [InlineData("{\"state\":\"999\"}", "state")]
    [InlineData("{\"state\":\"4\"}", "state")]
    [InlineData("{\"alliesNear\":{\"count\":null,\"within\":2}}", "alliesNear")]
    [InlineData("{\"chance\":0.5,\"hasTarget\":true}", "chance")]
    [InlineData("{\"hitWithin\":null}", "hitWithin")]
    [InlineData("{\"not\":null}", "not")]
    public void Malformed_conditions_report_the_field(string condition, string field)
    {
        var json = "{\"body\":\"vukah\",\"behaviour\":{\"rules\":[{\"when\":[" + condition + "],\"do\":[{\"flee\":true}]}]}}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var error = Assert.Throws<BlockError>(() => BlockJson.Make<CreatureBlock>(Mod("ai-condition-refuse"), "bad", doc.RootElement));
        Assert.Contains("when[0]." + field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_summons_count_toward_caps_and_failed_spawns_release_capacity()
    {
        var limits = new AiLimits();
        limits.Begin(1);
        var leases = Enumerable.Range(0, AiLimits.PerCreature).Select(_ => limits.Reserve("a", 1, 0)).ToList();
        Assert.All(leases, x => Assert.NotNull(x));
        limits.Begin(2);
        Assert.Null(limits.Reserve("a", 1, 0));
        leases[0]!.Dispose();
        Assert.NotNull(limits.Reserve("a", 1, 0));
        limits.Release("a");
        Assert.All(leases, x => Assert.False(x!.Active));
        Assert.NotNull(limits.Reserve("a", 1, 0));
    }

    [Fact]
    public void Population_is_bounded_across_creatures_mods_and_recursive_summons()
    {
        var limits = new AiLimits();
        for (uint i = 0; i < AiLimits.Total; i++)
        {
            limits.Begin(i);
            Assert.NotNull(limits.Reserve(i < AiLimits.PerMod ? "a" : "b", i, 0));
        }
        limits.Begin(999);
        Assert.Null(limits.Reserve("a", 1000, 0));
        Assert.Null(limits.Reserve("c", 1000, 0));
        limits.Release("a");
        Assert.NotNull(limits.Reserve("c", 1000, AiLimits.MaxDepth - 1));
        Assert.Null(limits.Reserve("c", 1000, AiLimits.MaxDepth));
    }

    [Fact]
    public void Queued_actions_and_summon_births_have_per_tick_budgets()
    {
        var limits = new AiLimits();
        limits.Begin(1);
        for (var i = 0; i < 4; i++) Assert.True(limits.Action(1));
        Assert.False(limits.Action(1));
        limits.Begin(1); // another handler on the same tick cannot reset the budget
        Assert.False(limits.Action(1));
        for (uint i = 2; i <= 16; i++)
            for (var j = 0; j < 4; j++) Assert.True(limits.Action(i));
        Assert.False(limits.Action(17));
        for (uint i = 0; i < 16; i++) Assert.NotNull(limits.Reserve("a", i, 0));
        Assert.Null(limits.Reserve("a", 16, 0));
        limits.Begin(2);
        Assert.True(limits.Action(1));
        Assert.NotNull(limits.Reserve("a", 16, 0));
    }

    [Fact]
    public void CSharp_rejects_invalid_values_before_registering_deferred_code()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Is.EnemiesNear(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Is.AlliesNear(1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Is.SummonsBelow(-1));
        Assert.Throws<ArgumentException>(() => Act.BackOff(8, 2));
        Assert.Throws<ArgumentException>(() => Act.Pause(8, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Act.Summon("x", seconds: double.NaN));
        Assert.Throws<ArgumentException>(() => Act.Summon("x", side: (SummonSide)99));
        Assert.Throws<ArgumentException>(() => Act.EnableFeature((EnemyFeatures)0x80000000));
        Assert.Throws<ArgumentException>(() => Is.InState(new[] { (EnemyState)254 }));
    }

    // Loaded through a collectible context below; its delegate must not root that context after unload.
    public static bool CollectibleFilter(NpcView npc, EntityId other) => true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RegisterCollectibleFilter()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("npcai-filter-cleanup", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(NpcAiTests).Assembly.Location);
        var method = assembly.GetType(typeof(NpcAiTests).FullName!)!.GetMethod(nameof(CollectibleFilter))!;
        var filter = method.CreateDelegate<Func<NpcView, EntityId, bool>>();
        AiRuntime.RegisterFilter("collectible-filter", filter);
        Assert.NotNull(AiRuntime.FindFilter("kiln", "collectible-filter"));
        context.Unload();
        Assert.Null(AiRuntime.FindFilter("kiln", "collectible-filter"));
        return new WeakReference(context);
    }

    [Fact]
    public void Unloading_removes_named_filters_and_releases_the_collectible_context()
    {
        var context = RegisterCollectibleFilter();
        for (var i = 0; i < 10 && context.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(context.IsAlive);
    }

    private sealed class ListedMods(params ModEntry[] entries) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => entries;
        public ModSide Side => ModSide.Server;
        public ModEntry? Get(string id) => Array.Find(entries, e => e.Id == id);
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }

    private static ModEntry Loaded(ModContext instance) =>
        new(instance.Info.Id, instance.Info.Name, instance.Info.Version, ".", ModSide.Server, ModState.Loaded, null, [], 0, instance.Info);

    // Kiln.Host publishes its mod list after OnLoad returns: during a boot's OnLoad the list still has the entry from before the load (Pending, with
    // a ModInfo object of its own). What the mod registered there belongs to the instance that runs, so the first tick must not take the new entry
    // for a reload (npcai-test, dev.24: every creature block defined in OnLoad lost its relations and filter on the first tick).
    [Fact]
    public void What_a_mod_registers_in_OnLoad_stays_with_its_instance()
    {
        const string id = "ai-onload";
        var instance = HostAccess.CreateContext(new ModInfo(id, "AI OnLoad", "1", "."), 0, ".");
        var previousMods = ModsBackend.Current;
        var previousCaller = ModContext.CurrentMod;
        (NpcServerSide.OnTickThread, NpcServerSide.IsClient, NpcServerSide.ServerRunning) = (() => true, () => false, () => true);
        try
        {
            // OnLoad at boot: the entry from before the load, as Kiln.Host makes it (its own ModInfo)
            ModsBackend.Current = new ListedMods(new ModEntry(id, "AI OnLoad", "1", ".", ModSide.Server, ModState.Pending, null, [], -1, new ModInfo(id, "AI OnLoad", "1", ".")));
            ModContext.CurrentMod = instance;
            AiRuntime.RegisterFilter("onload-filter", (_, _) => true);
            ModContext.CurrentMod = previousCaller;
            ModsBackend.Current = new ListedMods(Loaded(instance));   // after OnLoad: the list names it
            AiRuntime.OnTick(1);
            Assert.NotNull(AiRuntime.FindFilter(id, "onload-filter"));

            // a reload is another instance under the same id: what the old one registered goes
            var reloaded = HostAccess.CreateContext(new ModInfo(id, "AI OnLoad", "1", "."), 0, ".");
            ModsBackend.Current = new ListedMods(Loaded(reloaded));
            AiRuntime.OnTick(2);
            Assert.Null(AiRuntime.FindFilter(id, "onload-filter"));
        }
        finally
        {
            AiRuntime.Retire(id);
            ModContext.CurrentMod = previousCaller;
            ModsBackend.Current = previousMods;
        }
    }

    [Fact]
    public void Json_action_modifier_order_does_not_change_the_action()
    {
        var mod = Mod("ai-order");
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "body": "vukah", "behaviour": { "rules": [ { "do": [ { "wait": true, "play": "11111111-1111-1111-1111-111111111111" } ] } ] } }""");
        BlockJson.Make<CreatureBlock>(mod, "ordered", doc.RootElement);
        Assert.Contains("PlayActionSequenceAction", Rules(mod, "ordered").ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("waitUntilFinished", Rules(mod, "ordered").ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_game_kinds_can_have_fields_that_are_also_shorthand_names()
    {
        var mod = Mod("ai-raw-kinds");
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "body": "vukah", "behaviour": { "rules": [ { "when": [ { "type": "RandomChanceConstrain", "chance": 0.5 } ], "do": [ { "type": "SetImmuneAction", "immune": true } ] } ] } }""");
        BlockJson.Make<CreatureBlock>(mod, "raw", doc.RootElement);
        var text = Rules(mod, "raw").ToJsonString();
        Assert.Contains("RandomChanceConstrain", text, StringComparison.Ordinal);
        Assert.Contains("SetImmuneAction", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_not_recurses_into_AI_conditions()
    {
        var mod = Mod("ai-not");
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "body": "vukah", "behaviour": { "rules": [ { "when": [ { "not": { "inCombat": true } } ], "do": [ { "flee": true } ] } ] } }""");
        BlockJson.Make<CreatureBlock>(mod, "negated", doc.RootElement);
        var text = Rules(mod, "negated").ToJsonString();
        Assert.Contains("StateConstrain", text, StringComparison.Ordinal);
        Assert.Contains("\"negated\":true", text, StringComparison.Ordinal);
    }

}
