// SPDX-License-Identifier: MIT
// Affixes without a game: definitions and their refusals, rolls, the item calls on Kiln.Saves' real instance store (over
// its tests' fake game), a save and a restart, and the pass that keeps what is held on a wearer equal to what the wearer's
// worn and held items say, against a fake of the game (who wears what; stat changes started and ended). That a stat
// change moves the game's attribute is the lab's part (mods/tests/affix-test).
#define AFFIX_TEARDOWN
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class AffixTests : IDisposable
{
    private sealed class FakeGame : AffixRuntime.IAffixGame
    {
        public readonly List<EntityId> In = [];
        public readonly Dictionary<uint, List<ItemInstanceRef>> Bags = new(), Worn = new();
        public readonly Dictionary<uint, (EntityId Wearer, Stat Stat, double Value)> Running = new();
        public readonly List<string> Log = [];
        public bool Refuse;
        private uint _next = 90_000;

        public IReadOnlyList<EntityId> Wearers() => In;
        public IReadOnlyList<ItemInstanceRef>? Carried(EntityId wearer) => Bags.GetValueOrDefault(wearer.Value);
        public IReadOnlyList<ItemInstanceRef> Acting(EntityId wearer) => Worn.GetValueOrDefault(wearer.Value) ?? [];
        public bool Exists(EntityId helper) => Running.ContainsKey(helper.Value);

        public EntityId? Start(EntityId wearer, Stat stat, double value)
        {
            if (Refuse) return null;
            var e = new EntityId(_next++);
            Running[e.Value] = (wearer, stat, value);
            Log.Add($"start {stat.Key} {value} on {wearer}");
            return e;
        }

        /// <summary>kiln.spawn's destruction is on (false: a stat change could not be ended here).</summary>
        public bool Ends = true;

        /// <summary>Ending fails now (kiln.spawn refuses the destruction): the entity stays, and its value still counts.</summary>
        public bool EndFails;

#if AFFIX_TEARDOWN
        public bool CanEnd => Ends;

        public bool End(EntityId helper)
        {
            if ((EndFails || !Ends) && Running.ContainsKey(helper.Value))
            {
                Log.Add("end failed");
                return false;
            }
            if (Running.Remove(helper.Value, out var r)) Log.Add($"end {r.Stat.Key} {r.Value} on {r.Wearer}");
            return true;
        }
#else
        public void End(EntityId helper)
        {
            if ((EndFails || !Ends) && Running.ContainsKey(helper.Value))
            {
                Log.Add("end failed");
                return;
            }
            if (Running.Remove(helper.Value, out var r)) Log.Add($"end {r.Stat.Key} {r.Value} on {r.Wearer}");
        }
#endif

        /// <summary>What the game would add up to on a wearer for a stat.</summary>
        public double Sum(EntityId wearer, Stat stat) => Running.Values.Where(r => r.Wearer == wearer && ReferenceEquals(r.Stat, stat)).Sum(r => r.Value);
    }

    private static readonly InstanceFields Sword = Rec.Of(Rec.Sword, level: 7), Ring = Rec.Of(Rec.Ring, level: 2);
    private readonly InstanceHarness _h = new();
    private readonly FakeGame _game = new();
    private readonly EntityId _hero;
    private readonly AffixHandle _bear, _keen, _fleet;
    private readonly Func<ItemInstanceRef, int> _levelOf;

    public AffixTests()
    {
        AffixRuntime.ResetForTests();
        _levelOf = AffixRuntime.LevelOf;
        AffixRuntime.LevelOf = item => _h.World.Items.TryGetValue(item.Entity.Id.Value, out var i) ? i.Fields.Level : 0;  // no kiln.items here: the fake game's level
        _hero = _h.World.Join(Hero);
        _game.In.Add(_hero);
        _bear = Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1, Max = 3 }, new AffixTier { Min = 4, Max = 8, Weight = 0.5 }] });
        _keen = Define("keen", new AffixBlock { Name = "Keen", Stat = Stat.CritChance, Stacks = false, Group = "edge", Tiers = [new AffixTier { Min = 0.02, Max = 0.05 }] });
        _fleet = Define("fleet", new AffixBlock { Name = "Fleet", Stat = Stat.MoveSpeed, Group = "edge", Tiers = [new AffixTier { Min = 0.05, Max = 0.15, Step = 0.05 }] });
    }

    public void Dispose()
    {
        AffixRuntime.LevelOf = _levelOf;
        AffixRuntime.ResetForTests();
        _h.Dispose();
    }

    private AffixHandle Define(string name, AffixBlock block) => TestMods.As(_h.Mod, () => Affixes.Define(name, block));
    private AffixPoolHandle Pool(string name, AffixPoolBlock block, Action? define = null)
    {
        define?.Invoke();
        return TestMods.As(_h.Mod, () => Affixes.DefinePool(name, block));
    }
    private T As<T>(Func<T> body) => TestMods.As(_h.Mod, body);

    private void Pass()
    {
        var affixed = new Dictionary<uint, (ItemInstanceRef, IReadOnlyList<RolledAffix>)>();
        foreach (var item in _h.World.Items.Values)
            if (Affixes.Of(item.Ref) is { Count: > 0 } rolls) affixed[item.Ref.Entity.Id.Value] = (item.Ref, rolls);
        AffixRuntime.Pass(_game, affixed);
    }

    private ItemInstanceRef Give(int slot, InstanceFields fields, bool worn)
    {
        var r = _h.World.Give(_hero, slot, fields);
        if (!_game.Bags.TryGetValue(_hero.Value, out var bag)) _game.Bags[_hero.Value] = bag = [];
        bag.Add(r);
        if (worn) Wear(r);
        return r;
    }

    private void Wear(ItemInstanceRef r)
    {
        if (!_game.Worn.TryGetValue(_hero.Value, out var worn)) _game.Worn[_hero.Value] = worn = [];
        if (!worn.Contains(r)) worn.Add(r);
    }

    private void TakeOff(ItemInstanceRef r) => _game.Worn.GetValueOrDefault(_hero.Value)?.Remove(r);

    // ---- definitions ----

    [Fact]
    public void DefinitionsSayWhatIsWrongByField()
    {
        static AffixTier T(double min, double max) => new() { Min = min, Max = max };
        BlockError Bad(string name, AffixBlock b) => Assert.Throws<BlockError>(() => Define(name, b));
        Assert.Null(Bad("Bad Name", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [T(1, 2)] }).Field);
        Assert.Equal("name", Bad("a", new AffixBlock { Name = "", Stat = Stat.Strength, Tiers = [T(1, 2)] }).Field);
        Assert.Equal("tiers", Bad("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [] }).Field);
        Assert.Equal("tiers[0]", Bad("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [T(0, 2)] }).Field);
        Assert.Equal("tiers[0]", Bad("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [T(-1, 2)] }).Field);
        Assert.Equal("tiers[1]", Bad("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [T(1, 2), T(1.5, 2)] }).Field);
        Assert.Equal("tiers[0].max", Bad("a", new AffixBlock { Name = "x", Stat = Stat.MoveSpeed, Tiers = [T(0.2, 0.1)] }).Field);
        Assert.Equal("tiers[0].step", Bad("a", new AffixBlock { Name = "x", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.1, Max = 0.2, Step = 0 }] }).Field);
        Assert.Equal("tiers[0].weight", Bad("a", new AffixBlock { Name = "x", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.1, Max = 0.2, Weight = -1 }] }).Field);
        Assert.Equal("tiers", Bad("a", new AffixBlock { Name = "x", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.1, Max = 0.2, Weight = 0 }] }).Field);
        Assert.Equal("tiers", Bad("a", new AffixBlock { Name = "x", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.1, Max = 0.2, MinItemLevel = 5 }] }).Field);
        Assert.Equal("group", Bad("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Group = "", Tiers = [T(1, 2)] }).Field);
        // a curse: negative ranges are fine
        Assert.Equal("sword-mod:curse", Define("curse", new AffixBlock { Name = "Cursed", Stat = Stat.Strength, Tiers = [T(-5, -1)] }).Id);

        BlockError BadPool(AffixPoolBlock b) => Assert.Throws<BlockError>(() => Pool("p", b));
        Assert.Equal("affixes", BadPool(new AffixPoolBlock { Affixes = [] }).Field);
        Assert.Equal("affixes[0].affix", BadPool(new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "nope" }] }).Field);
        Assert.Equal("affixes[1].affix", BadPool(new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "sword-mod:bear" }] }).Field);
        Assert.Equal("max", BadPool(new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }], Min = 2, Max = 1 }).Field);
        Assert.Equal("max", BadPool(new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }], Max = 9 }).Field);
        Assert.Equal("items", BadPool(new AffixPoolBlock { Items = [], Affixes = [new AffixEntry { Affix = "bear" }] }).Field);
        Assert.Throws<InvalidOperationException>(() => Affixes.Define("no-mod", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [T(1, 2)] }));
    }

    [Fact]
    public void ADataFileDefinesTheSameShapes()
    {
        var file = As(() => Affixes.Load("""
            { "format": 1,
              "affixes": { "json-bear": { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 2, "max": 4 }, { "min": 5, "max": 9, "weight": 0.5, "minItemLevel": 20 } ], "group": "suffix" },
                           "json-ward": { "name": "Ember Ward", "stat": "resist-fire", "stacks": false, "tiers": [ { "min": 0.05, "max": 0.1, "step": 0.05 } ] } },
              "pools": { "json-blades": { "items": [ "Weapon_T1_1H_SwordEpic_01" ], "affixes": [ { "affix": "json-bear" }, { "affix": "json-ward", "weight": 2 } ], "min": 1, "max": 2 } } }
            """));
        Assert.Equal(["sword-mod:json-bear", "sword-mod:json-ward"], file.Affixes.Select(a => a.Id));
        var bear = Affixes.Find("sword-mod:json-bear")!.Block;
        Assert.Same(Stat.Strength, bear.Stat);
        Assert.Equal((2, "suffix", 20, 0.5), (bear.Tiers.Count, bear.Group, bear.Tiers[1].MinItemLevel, bear.Tiers[1].Weight));
        var ward = Affixes.Find("sword-mod:json-ward")!.Block;
        Assert.False(ward.Stacks);
        Assert.Equal(0.05, ward.Tiers[0].Step);
        var pool = Assert.Single(file.Pools).Block;
        Assert.Equal((1, 2, "Weapon_T1_1H_SwordEpic_01", 2.0), (pool.Min, pool.Max, pool.Items![0].Value, pool.Affixes[1].Weight));
        Assert.Contains("not a stat", Assert.Throws<BlockError>(() => As(() => Affixes.Load("""{ "format": 1, "affixes": { "x": { "name": "x", "stat": "strenght", "tiers": [ { "min": 1, "max": 2 } ] } } }"""))).Message);
        Assert.Equal("format", Assert.Throws<BlockError>(() => As(() => Affixes.Load("""{ "format": 2 }"""))).Field);
        Assert.Throws<BlockError>(() => As(() => Affixes.Load("""{ "format": 1, "affixs": {} }""")));  // a misspelt section is not silently nothing
    }

    [Fact]
    public void AModsUnloadTakesItsDefinitionsAndLeavesTheRolls()
    {
        var other = TestMods.Create("ring-mod");
        TestMods.As(other, () => Affixes.Define("glow", new AffixBlock { Name = "Glowing", Stat = Stat.MaxMana, Tiers = [new AffixTier { Min = 5, Max = 5 }] }));
        var ring = Give(4, Ring, worn: true);
        Assert.True(As(() => Affixes.Add(ring, "ring-mod:glow")).Ok);
        Pass();
        Assert.Equal(5, _game.Sum(_hero, Stat.MaxMana));
        TestMods.Retire(other);
        Assert.Null(Affixes.Find("ring-mod:glow"));
        Pass();
        Assert.Equal(0, _game.Sum(_hero, Stat.MaxMana));            // it does nothing while its mod is away
        Assert.Equal("ring-mod:glow", Assert.Single(Affixes.Of(ring)).Affix);   // and stays on the item
        var back = TestMods.Create("ring-mod");
        TestMods.As(back, () => Affixes.Define("glow", new AffixBlock { Name = "Glowing", Stat = Stat.MaxMana, Tiers = [new AffixTier { Min = 5, Max = 5 }] }));
        Pass();
        Assert.Equal(5, _game.Sum(_hero, Stat.MaxMana));            // the mod is back: so is the effect
    }

    // ---- one item ----

    [Fact]
    public void RollsStayInTheirRangesAndRepeatWithASeed()
    {
        var pool = Pool("blades", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "keen" }, new AffixEntry { Affix = "fleet", Weight = 2 }] });
        var sword = Give(3, Sword, worn: false);
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < 300; i++)
        {
            var r = As(() => Affixes.Roll(sword, pool, new Random(i), replace: true));
            var a = Assert.Single(r.Affixes);
            seen[a.Affix] = seen.GetValueOrDefault(a.Affix) + 1;
            switch (a.Affix)
            {
                case "sword-mod:bear":
                    Assert.True(a.Tier == 1 ? a.Value is >= 1 and <= 3 : a.Value is >= 4 and <= 8, a.ToString());
                    Assert.Equal(Math.Floor(a.Value), a.Value);
                    break;
                case "sword-mod:keen":
                    Assert.InRange(a.Value, 0.02, 0.05);
                    Assert.Equal(Math.Round(a.Value, 2), a.Value);
                    break;
                default:
                    Assert.Contains(a.Value, new[] { 0.05, 0.1, 0.15 });
                    break;
            }
        }
        Assert.Equal(3, seen.Count);
        Assert.True(seen["sword-mod:fleet"] > seen["sword-mod:bear"] && seen["sword-mod:fleet"] > seen["sword-mod:keen"], string.Join(", ", seen));
        Assert.Equal(As(() => Affixes.Roll(sword, pool, new Random(5), true)).Affixes, As(() => Affixes.Roll(sword, pool, new Random(5), true)).Affixes);
    }

    [Fact]
    public void ARollObeysThePoolsCountItsGroupsAndTheItemsLevel()
    {
        var sword = Give(3, Sword, worn: false);
        var two = Pool("two", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "keen" }, new AffixEntry { Affix = "fleet" }], Min = 2, Max = 3 });
        for (var i = 0; i < 50; i++)
        {
            var r = As(() => Affixes.Roll(sword, two, new Random(i), replace: true));
            Assert.Equal(2, r.Affixes.Count);                                              // keen and fleet share a group: never both
            Assert.Single(r.Affixes, a => a.Affix is "sword-mod:keen" or "sword-mod:fleet");
        }
        // the sword's item level is 7: a tier that asks level 8 never rolls, however heavy; one that asks 7 does
        Define("late", new AffixBlock { Name = "Late", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 1, Max = 1 }, new AffixTier { Min = 100, Max = 100, MinItemLevel = 8, Weight = 1000 }] });
        var late = Pool("late-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "late" }] });
        for (var i = 0; i < 20; i++) Assert.Equal(1, Assert.Single(As(() => Affixes.Roll(sword, late, new Random(i), true)).Affixes).Value);
        Define("due", new AffixBlock { Name = "Due", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 1, Max = 1 }, new AffixTier { Min = 100, Max = 100, MinItemLevel = 7, Weight = 1000 }] });
        var due = Pool("due-pool", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "due" }] });
        Assert.Contains(Enumerable.Range(0, 20), i => As(() => Affixes.Roll(sword, due, new Random(i), true)).Affixes[0] is { Tier: 2, Value: 100 });
        var none = Pool("none", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }], Min = 0, Max = 0 });
        Assert.True(As(() => Affixes.Clear(sword)));
        var empty = As(() => Affixes.Roll(sword, none));
        Assert.True(empty.Ok);
        Assert.Empty(empty.Affixes);
    }

    [Fact]
    public void TheRulesRefuseAndBadArgumentsThrow()
    {
        var sword = Give(3, Sword, worn: false);
        var pool = Pool("blades", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }] });
        var changes = new List<AffixesChangedEvent>();
        using var sub = Events.On<AffixesChangedEvent>(changes.Add);
        var first = As(() => pool.Roll(sword, new Random(1)));
        Assert.True(first.Ok);
        Assert.Null(first.Reason);
        var again = As(() => pool.Roll(sword));
        Assert.False(again.Ok);
        Assert.Contains("already has affixes", again.Reason);
        Assert.Equal(first.Affixes, again.Affixes);
        Assert.False(As(() => Affixes.Add(sword, "bear", 2)).Ok);                          // the same affix twice
        Assert.True(As(() => Affixes.Add(sword, "keen", 1, 0.03)).Ok);
        var group = As(() => Affixes.Add(sword, "fleet"));
        Assert.False(group.Ok);
        Assert.Contains("same group \"edge\"", group.Reason);
        Assert.Equal(2, changes.Count);                                                    // only the two that changed something
        Assert.Equal((1, 2), (changes[1].Before.Count, changes[1].Now.Count));
        Assert.Equal("+3% Crit Chance (Keen)", As(() => Affixes.Of(sword))[1].Text);

        Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(sword, "no-such")));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(sword, "bear", 0)));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(sword, "bear", 3)));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(Give(9, Ring, false), "bear", 1, 1.5)));   // a fraction for a whole-number stat
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(Give(10, Ring, false), "fleet", 1, 0)));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Of(ItemInstanceRef.None)));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Roll(ItemInstanceRef.None, pool)));
        Assert.Throws<ArgumentNullException>(() => Affixes.Roll(sword, null!));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Set(sword, [new RolledAffix("no-colon", "x", "strength", 1, 1)])));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Set(sword, Enumerable.Range(0, 9).Select(i => new RolledAffix($"m:a{i}", "x", "strength", 1, 1)).ToList())));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Set(sword, [new RolledAffix("m:a", "x", "strength", 1, 1), new RolledAffix("m:a", "x", "strength", 1, 2)])));

        var ring = Give(4, Ring, false);
        for (var i = 0; i < Affixes.MaxPerItem; i++)
        {
            Define($"n{i}", new AffixBlock { Name = $"N{i}", Stat = Stat.MaxMana, Tiers = [new AffixTier { Min = 1, Max = 1 }] });
            Assert.True(As(() => Affixes.Add(ring, $"n{i}")).Ok);
        }
        var full = As(() => Affixes.Add(ring, "bear"));
        Assert.False(full.Ok);
        Assert.Contains("at most 8", full.Reason);

        // a stale ref: reads say "none", writes throw (the item is gone)
        _h.World.Destroy(ring);
        Assert.Empty(As(() => Affixes.Of(ring)));
        Assert.False(As(() => Affixes.Remove(ring, "bear")));
        Assert.Throws<ArgumentException>(() => As(() => Affixes.Add(ring, "bear")));

        var gone = Pool("gone", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }] });
        Pool("gone", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "keen" }] });   // defined again: the old handle is not the pool any more
        Assert.Throws<ArgumentException>(() => As(() => gone.Roll(sword, replace: true)));
    }

    [Fact]
    public void TheTextSaysWhatAPlayerReads()
    {
        Assert.Equal("+5 Strength (of the Bear)", new RolledAffix("m:bear", "of the Bear", "strength", 1, 5).Text);
        Assert.Equal("-3 Max Health (Frail)", new RolledAffix("m:frail", "Frail", "max-health", 1, -3).Text);
        Assert.Equal("+5% Move Speed (Fleet)", new RolledAffix("m:fleet", "Fleet", "move-speed", 1, 0.05).Text);
        Assert.Equal("+12.5% Resist Fire (Ember Ward)", new RolledAffix("m:ward", "Ember Ward", "resist-fire", 1, 0.125).Text);
        Assert.Equal("-10% Sprint Speed (Heavy)", new RolledAffix("m:heavy", "Heavy", "sprint-speed", 1, -0.1).Text);
    }

    [Fact]
    public void TheRollsAreItemDataOfKilnsOwnAndComeBackAfterARestart()
    {
        var chest = _h.World.Chest(ChestTemplate, A);
        var sword = _h.World.Give(chest, 2, Sword);
        Assert.True(As(() => Affixes.Set(sword, [new RolledAffix("sword-mod:bear", "of the Bear", "strength", 2, 6), new RolledAffix("sword-mod:fleet", "Fleet", "move-speed", 1, 0.1)])).Ok);
        _h.Save(1);
        var bundle = _h.Bundle(1, InstanceStore.WorldPrefix + Affixes.DataMod)!;        // kept under Kiln's own owner, not a mod's
        var stored = bundle["holders"]![0]!["records"]![0]!["keys"]![Affixes.DataKey]!.ToJsonString();
        Assert.Equal("{\"Rolls\":[{\"Id\":\"sword-mod:bear\",\"Name\":\"of the Bear\",\"Stat\":\"strength\",\"Tier\":2,\"Value\":6},{\"Id\":\"sword-mod:fleet\",\"Name\":\"Fleet\",\"Stat\":\"move-speed\",\"Tier\":1,\"Value\":0.1}]}", stored);
        // what a player's game gets is that JSON: it reads back as the same affixes
        Assert.Equal(As(() => Affixes.Of(sword)), AffixRuntime.Parse(stored));
        Assert.Null(AffixRuntime.Parse("{ not json"));
        Assert.Equal(InstanceSync.Nearby, Store.Instances.KeyOf(Affixes.DataMod, Affixes.DataKey)!.Sync);   // it travels to players near the item

        _h.Restart(1, FakeInstanceWorld.Load(_h.World.SaveGame()));
        var back = _h.World.At(_h.World.HolderAt(ChestTemplate, A), 2);
        Assert.NotEqual(sword, back);
        Assert.Equal([("sword-mod:bear", 2, 6.0), ("sword-mod:fleet", 1, 0.1)], As(() => Affixes.Of(back)).Select(a => (a.Affix, a.Tier, a.Value)));
    }

    // ---- the pass ----

    [Fact]
    public void WhatIsWornActsAndWhatIsOnlyCarriedDoesNot()
    {
        var applied = new List<AffixesAppliedEvent>();
        using var sub = Events.On<AffixesAppliedEvent>(applied.Add);
        var sword = Give(3, Sword, worn: false);
        As(() => Affixes.Add(sword, "bear", 1, 3));
        Pass();
        Assert.Empty(_game.Running);                                       // in the bag
        Assert.Empty(As(() => Affixes.Active(_hero)));
        Wear(sword);
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        Assert.Equal(new ActiveAffix("sword-mod:bear", "strength", 3, sword.Entity.Id), Assert.Single(As(() => Affixes.Active(_hero))));
        Pass();
        Pass();
        Assert.Equal(["start strength 3 on " + _hero], _game.Log);          // nothing is started twice
        TakeOff(sword);
        Pass();
        Assert.Empty(_game.Running);
        Assert.Equal(["start strength 3 on " + _hero, "end strength 3 on " + _hero], _game.Log);
        Assert.Equal([1, 0], applied.Select(e => e.Now.Count));
        Assert.Equal(_hero, applied[0].Wearer);
    }

    [Fact]
    public void TwoWornItemsAddUpUnlessTheAffixDoesNotStack()
    {
        var a = Give(3, Sword, worn: true);
        var b = Give(4, Sword, worn: true);
        As(() => Affixes.Add(a, "bear", 1, 3));
        As(() => Affixes.Add(b, "bear", 2, 5));
        As(() => Affixes.Add(a, "keen", 1, 0.03));
        As(() => Affixes.Add(b, "keen", 1, 0.05));
        Pass();
        Assert.Equal(8, _game.Sum(_hero, Stat.Strength));                   // stacks: 3 + 5
        Assert.Equal(0.05, _game.Sum(_hero, Stat.CritChance), 6);           // does not: the highest
        TakeOff(b);
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        Assert.Equal(0.03, _game.Sum(_hero, Stat.CritChance), 6);           // the highest of what is still worn
    }

    [Fact]
    public void AChangedRollTheItemGoneAndTheWearerGoneAllEndWhatWasHeld()
    {
        var sword = Give(3, Sword, worn: true);
        As(() => Affixes.Add(sword, "bear", 1, 3));
        Pass();
        As(() => Affixes.Set(sword, [new RolledAffix("sword-mod:bear", "of the Bear", "strength", 2, 6)]));   // upgraded
        Pass();
        Assert.Equal(6, _game.Sum(_hero, Stat.Strength));                   // the old value ended, the new one started: never both
        Assert.Single(_game.Running);

        _h.World.Destroy(sword);                                            // trashed while worn
        _game.Bags[_hero.Value].Remove(sword);
        TakeOff(sword);
        Pass();
        Assert.Empty(_game.Running);

        var ring = Give(5, Ring, worn: true);
        As(() => Affixes.Add(ring, "bear", 1, 2));
        Pass();
        Assert.Single(_game.Running);
        _game.In.Clear();                                                   // the player left
        Pass();
        Assert.Empty(_game.Running);
        Assert.Empty(As(() => Affixes.Active(_hero)));
        Assert.Equal("end strength 2 on " + _hero, _game.Log[^1]);
    }

    [Fact]
    public void AStatChangeTheGameEndedIsStartedAgainAndOneThatCouldNotStartIsTriedAgain()
    {
        var sword = Give(3, Sword, worn: true);
        As(() => Affixes.Add(sword, "bear", 1, 3));
        _game.Refuse = true;                                                // the wearer is not in the world yet
        Pass();
        Assert.Empty(_game.Running);
        Assert.Empty(As(() => Affixes.Active(_hero)));
        _game.Refuse = false;
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        _game.Running.Clear();                                              // the game ended the entity by itself (and with it took the value back)
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));                   // it is held again, once
        Assert.Single(_game.Running);
        Assert.Contains(" 1 found ended by the game", AffixRuntime.Describe());
    }

    [Fact]
    public void AnUnreadableInventoryHoldsNothingAndAnotherPlayersItemIsNotMine()
    {
        var friend = _h.World.Join(new CharacterKey(0x2233445566778899, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000002")));
        _game.In.Add(friend);
        var sword = Give(3, Sword, worn: true);
        As(() => Affixes.Add(sword, "bear", 1, 3));
        _game.Bags[friend.Value] = [];
        _game.Worn[friend.Value] = [sword];                                 // the game says it acts for the friend, who does not carry it
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        Assert.Equal(0, _game.Sum(friend, Stat.Strength));
        _game.Bags.Remove(_hero.Value);                                     // the hero's inventory cannot be read now (not initialized)
        Pass();
        Assert.Equal(0, _game.Sum(_hero, Stat.Strength));
    }

    // ---- review of b30f339d: teardown, a redefined affix, a pool's minimum ----

    [Fact]
    public void AStatChangeThatCouldNotBeEndedIsKeptTriedAgainAndNeverDoubled()
    {
        var sword = Give(3, Sword, worn: true);
        As(() => Affixes.Add(sword, "bear", 1, 3));
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        _game.EndFails = true;                                              // kiln.spawn refuses the destruction now
        TakeOff(sword);
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));                   // it still acts: the game could not end it
        Assert.Single(As(() => Affixes.Active(_hero)));                     // and Kiln says so
        Wear(sword);
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));                   // put on again: that change is held again, never a second one
        Assert.Single(_game.Running);
        TakeOff(sword);
        Pass();
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        _game.EndFails = false;                                             // the next try works
        Pass();
        Assert.Equal(0, _game.Sum(_hero, Stat.Strength));
        Assert.Empty(_game.Running);
        Assert.Empty(As(() => Affixes.Active(_hero)));

        // a changed roll while the old change cannot end: the new one waits, so the two never add up
        Wear(sword);
        Pass();
        _game.EndFails = true;
        As(() => Affixes.Set(sword, [new RolledAffix("sword-mod:bear", "of the Bear", "strength", 2, 6)]));
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
        _game.EndFails = false;
        Pass();
        Pass();
        Assert.Equal(6, _game.Sum(_hero, Stat.Strength));

        // the wearer leaves while its change cannot end: it is still tried, until it is gone
        _game.EndFails = true;
        _game.In.Clear();
        Pass();
        Assert.Single(_game.Running);
        _game.EndFails = false;
        Pass();
        Assert.Empty(_game.Running);
    }

    [Fact]
    public void NothingStartsWhereAStatChangeCouldNotBeEnded()
    {
        _game.Ends = false;                                                 // spawn.destroy is off on this build
        var sword = Give(3, Sword, worn: true);
        As(() => Affixes.Add(sword, "bear", 1, 3));
        Pass();
        Pass();
        Assert.Empty(_game.Running);
        Assert.Empty(As(() => Affixes.Active(_hero)));
        _game.Ends = true;
        Pass();
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));
    }

    [Fact]
    public void ARollKeepsTheStatItWasRolledWithWhenItsAffixIsDefinedAgain()
    {
        var sword = Give(3, Sword, worn: true);
        As(() => Affixes.Add(sword, "bear", 1, 3));                         // +3 Strength
        Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.05, Max = 0.1 }] });   // now Move Speed
        Pass();
        Assert.Equal("+3 Strength (of the Bear)", As(() => Affixes.Of(sword))[0].Text);   // what players' games show
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));                   // is what acts
        Assert.Equal(0, _game.Sum(_hero, Stat.MoveSpeed));
        Assert.Equal("strength", Assert.Single(As(() => Affixes.Active(_hero))).Stat);

        var ring = Give(4, Ring, worn: true);
        As(() => Affixes.Add(ring, "bear", 1, 0.05));                       // a new roll takes the new meaning
        Pass();
        Assert.Equal(0.05, _game.Sum(_hero, Stat.MoveSpeed), 6);
        Assert.Equal(3, _game.Sum(_hero, Stat.Strength));

        var odd = Give(5, Ring, worn: true);                                // a stat Kiln cannot name does nothing
        As(() => Affixes.Set(odd, [new RolledAffix("sword-mod:bear", "of the Bear", "my-own-stat", 1, 2)]));
        Pass();
        Assert.Equal(2, _game.Running.Count);
    }

    [Fact]
    public void APoolThatCanNeverGiveItsMinimumIsRefusedAndOneThatCannotNowSaysWhy()
    {
        // keen and fleet share the group "edge": these three give at most two
        Assert.Equal("min", Assert.Throws<BlockError>(() => Pool("three", new AffixPoolBlock
            { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "keen" }, new AffixEntry { Affix = "fleet" }], Min = 3, Max = 3 })).Field);
        Assert.Equal("min", Assert.Throws<BlockError>(() => Pool("lonely", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }], Min = 2, Max = 2 })).Field);
        Assert.Equal("min", Assert.Throws<BlockError>(() => Pool("weightless", new AffixPoolBlock
            { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "keen", Weight = 0 }], Min = 2, Max = 2 })).Field);
        // an affix whose only tier for every item level never comes up is refused where it is defined
        Assert.Equal("tiers", Assert.Throws<BlockError>(() => Define("late-only", new AffixBlock
            { Name = "x", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 1, Max = 1, Weight = 0 }, new AffixTier { Min = 5, Max = 5, MinItemLevel = 3 }] })).Field);

        // a pool of two that can, until one of its affixes leaves with its mod
        var other = TestMods.Create("ring-mod");
        TestMods.As(other, () => Affixes.Define("glow", new AffixBlock { Name = "Glowing", Stat = Stat.MaxMana, Tiers = [new AffixTier { Min = 5, Max = 5 }] }));
        var pair = Pool("pair", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "ring-mod:glow" }], Min = 2, Max = 2 });
        var sword = Give(3, Sword, worn: false);
        var first = As(() => pair.Roll(sword, new Random(1)));
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(2, first.Affixes.Count);
        TestMods.Retire(other);
        var changes = new List<AffixesChangedEvent>();
        using var sub = Events.On<AffixesChangedEvent>(changes.Add);
        var again = As(() => pair.Roll(sword, new Random(2), replace: true));
        Assert.False(again.Ok);
        Assert.Contains("at least 2", again.Reason);
        Assert.Contains("ring-mod:glow", again.Reason);
        Assert.Equal(first.Affixes, again.Affixes);                          // the item keeps what it had
        Assert.Equal(first.Affixes, As(() => Affixes.Of(sword)));
        Assert.Empty(changes);

        // two affixes defined again into one group: the pool asks two, one can roll
        var plain = Give(4, Ring, worn: false);
        var duo = Pool("duo", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "solo" }], Min = 2, Max = 2 },
            define: () => Define("solo", new AffixBlock { Name = "Solo", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 1, Max = 1 }] }));
        Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Group = "g", Tiers = [new AffixTier { Min = 1, Max = 3 }] });
        Define("solo", new AffixBlock { Name = "Solo", Stat = Stat.MaxHealth, Group = "g", Tiers = [new AffixTier { Min = 1, Max = 1 }] });
        var grouped = As(() => duo.Roll(plain, new Random(3)));
        Assert.False(grouped.Ok);
        Assert.Contains("share a group", grouped.Reason);
        Assert.Empty(As(() => Affixes.Of(plain)));
    }
}
