// SPDX-License-Identifier: MIT
// A weapon's follow-up routes, with no game: what is refused as written, how a route resolves against a motion's own windows (the cases of the
// research contract: all against one occurrence, identical routes, disjoint occurrences, different sub-sequences, a window with no follow-up,
// explicit over the chain's own link), and the application as one generation over a made-up game that can refuse any step: nothing is half applied,
// the status and the event say Applied only after the switch, and the same plan twice changes nothing. Every row below is invented.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("player-state seam")]
public sealed class MoveFollowUpTests : IDisposable
{
    private const string Mod = "routes-test";
    private readonly Func<string, bool> _held = BlockCapabilities.Held;
    private readonly IMoveHost _host = MoveApplications.Host;
    private readonly Action<KilnEvent> _sink = MoveApplications.Sink;
    private readonly List<WeaponMovesApplicationChangedEvent> _events = new();
    private readonly FakeGame _game = new();

    public MoveFollowUpTests()
    {
        PlayerStateSeam.Lookup = null;
        BlockCapabilities.Held = _ => false;
        MoveApplications.Reset();
        MoveApplications.Host = _game;
        MoveApplications.Sink = ev => _events.Add((WeaponMovesApplicationChangedEvent)ev);
    }

    public void Dispose()
    {
        PlayerStateSeam.Lookup = PlayerAnimLookup.Instance;
        BlockCapabilities.Held = _held;
        MoveApplications.Reset();
        MoveApplications.Host = _host;
        MoveApplications.Sink = _sink;
    }

    private static ModBlocks NewMod() => new(Mod, 7) { ManifestClients = "mod" };
    private static JsonObject Lower(WeaponBlock b, string name = "warden") => Compiler.Lower(NewMod(), name, b).Low.Json;
    private static BlockError Refused(WeaponBlock b) => Assert.Throws<BlockError>(() => Lower(b));

    private static MoveFollowUpBlock Route(MoveFollowUpTrigger trigger, WeaponMoveSlot to, int? occurrence = null, int sub = 0, int link = 1) =>
        new() { Trigger = trigger, To = to, Occurrence = occurrence, SubSequence = sub, Link = link };

    // a sword whose block hands over to its own parry on a parried blow and to its first light attack on Primary
    private static WeaponBlock Warden(params MoveFollowUpBlock[] blockRoutes) => new()
    {
        Name = "Warden", Class = WeaponClass.Sword,
        Moves = new WeaponMoves
        {
            Primary = [new MoveBlock { Like = "Light1", HitAt = 0.3 }, new MoveBlock { Like = "Light2" }],
            Parry = new MoveBlock { Like = "ParryAll" },
            Block = new MoveBlock
            {
                FollowUps = blockRoutes.Length > 0 ? blockRoutes : [Route(MoveFollowUpTrigger.Parried, WeaponMoveSlot.Parry), Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary)],
            },
        },
    };

    // ---- the made-up game ----

    private static EventRow Ev(string type, double t, double d = 0) => new(type, 0, t, d);
    private static EventRow Window(string trigger, double t, Guid to, double at = 0.4) => Ev("SetActionOportunity", t) with { TriggeredBy = trigger, FollowUp = to, FollowUpTime = at };

    private static readonly Guid L1 = Guid.NewGuid(), L2 = Guid.NewGuid(), HeavyG = Guid.NewGuid(), BlockG = Guid.NewGuid(), ParryG = Guid.NewGuid(), Kick = Guid.NewGuid(), VanillaParry = Guid.NewGuid();

    private static SubSequence Light(Guid self, Guid next) => MoveTimingTests.Sub(1.6,
        Ev("SetAnimationEvent", 0) with { State = Guid.NewGuid() },
        Window("Evade", 0.18, Guid.Empty, 0.5),
        // one input, two rows: a skill decides which the game takes. They must stay two rows
        Window("Primary", 0.2, next, 0.45), Window("Primary", 0.2, next, 0.55),
        Window("Heavy", 0.2, HeavyG, 0.5),
        Ev("SpawnImpact", 0.25, 0.15)) with { Sequence = self };

    private static readonly Dictionary<string, IReadOnlyList<SubSequence>> Game = new()
    {
        ["Light1"] = [Light(L1, L2)], ["Light2"] = [Light(L2, L1)],
        ["ParryAll"] = [MoveTimingTests.Sub(0.8, Ev("SetAnimationEvent", 0) with { State = Guid.NewGuid() }, Window("Primary", 0, Kick, 0.25)) with { Sequence = ParryG }],
        // the block: a parried blow leads to the game's parry, a Primary to the first light attack; its second sub-sequence has a Primary window of its own
        [BlockG.ToString()] =
        [
            MoveTimingTests.Sub(0.4, Ev("SetAnimationEvent", 0) with { State = Guid.NewGuid() }, Window("Parried", 0, VanillaParry, 0), Window("Primary", 0.066, L1, 0.35),
                Ev("LoopEvent", 0.26, 0.13)) with { Sequence = BlockG },
            MoveTimingTests.Sub(0.4, Window("Primary", 0.1, L1, 0.3)) with { Sequence = BlockG, Index = 1 },
        ],
    };

    private static readonly IReadOnlyList<SlotRow> Slots =
    [
        new("Block", [], "Invalid", BlockG), new("Heavy", ["IsAirborne"], "Invalid", L1), new("Heavy", ["IsGrounded"], "Invalid", HeavyG),
        new("Primary", [], "Invalid", L1), new("Parry", [], "Invalid", VanillaParry),
    ];

    private sealed class FakeGame : IMoveHost
    {
        public ulong ContentGeneration { get; set; } = 3;
        public IReadOnlyList<SlotRow>? SlotRows = MoveFollowUpTests.Slots;
        public readonly Dictionary<Guid, (string State, string? Error)> Defined = new();
        public readonly List<string> DefinedNames = new();
        public readonly Dictionary<(string Clone, int Sub, int K), Guid> LinksNow = new();
        public readonly Dictionary<int, Guid> SlotsNow = new();
        public readonly Dictionary<string, Guid> ByName = new();
        public readonly List<JsonObject> Applied = new();
        public readonly Queue<Action<Action<string>>> Pending = new();
        public readonly List<(string Section, string Entry, JsonObject Rules)> Kept = new();
        public int RefuseDefineAt = -1, Defines;
        public Func<JsonObject, string?>? ApplyError;
        public bool SwitchTakesOnlyFirst;

        public IReadOnlyList<SlotRow>? Slots(string item) => SlotRows;
        public Func<string, IReadOnlyList<SubSequence>?> Reader() => key => Game.TryGetValue(key, out var s) ? s : null;
        public IPlayerStateLookup? Lookup => null;
        public IReadOnlyDictionary<Guid, (string State, string? Error)> Clones() => Defined;

        public void Define(JsonObject d, ulong owner)
        {
            if (Defines++ == RefuseDefineAt) throw new InvalidOperationException("the registry is full");
            var g = Guid.Parse((string)d["guid"]!);
            Defined[g] = ("registered", null);
            ByName[(string)d["name"]!] = g;
            DefinedNames.Add((string)d["name"]!);
        }

        public IReadOnlyList<string> Apply(ulong owner, JsonObject doc, string source)
        {
            Applied.Add(doc.DeepClone().AsObject());
            if (ApplyError?.Invoke(doc) is { } error) return [error];
            if (doc["sequences"] is JsonObject seqs)
                foreach (var (entry, rules) in seqs)
                {
                    var hash = entry.IndexOf('#');
                    var (clone, sub) = hash > 0 ? (entry[..hash], int.Parse(entry[(hash + 1)..])) : (entry, 0);
                    foreach (var (path, v) in rules!.AsObject())
                        if (path.EndsWith(".followUp")) LinksNow[(clone, sub, int.Parse(path[(path.IndexOf('#') + 1)..path.IndexOf(']')]))] = ByName[(string)v!];
                }
            if (doc["itemdata"] is JsonObject items)
                foreach (var (_, rules) in items)
                {
                    var n = 0;
                    foreach (var (path, v) in rules!.AsObject())
                    {
                        if (SwitchTakesOnlyFirst && n++ > 0) continue;
                        var i = int.Parse(path[(path.IndexOf('[') + 1)..path.IndexOf(']')]);
                        if (v is JsonObject) SlotsNow.Remove(i);       // {"reset": true}: the class's again
                        else SlotsNow[i] = ByName[(string)v!];
                    }
                }
            return [];
        }

        public Guid? Link(string clone, int sub, int k) => LinksNow.TryGetValue((clone, sub, k), out var g) ? g : null;
        public Guid? Slot(string item, int index) => SlotsNow.TryGetValue(index, out var g) ? g : null;
        public void Later(ModBlocks mod, Action<Action<string>> next) => Pending.Enqueue(next);
        public void Keep(ModBlocks mod, IReadOnlyList<(string Section, string Entry, JsonObject Rules)> rules) => Kept.AddRange(rules);
        public void Drop(ModBlocks mod, IReadOnlyList<(string Section, string Entry, JsonObject Rules)> rules) => Kept.RemoveAll(r => rules.Any(x => ReferenceEquals(x.Rules, r.Rules)));

        /// <summary>Ready passes until nothing is waiting.</summary>
        public List<string> Pump()
        {
            var problems = new List<string>();
            while (Pending.Count > 0) Pending.Dequeue()(problems.Add);
            return problems;
        }
    }

    private (ModBlocks Mod, MovePlan Plan) Start(WeaponBlock b, string name = "warden")
    {
        var mod = NewMod();
        var json = Compiler.Lower(mod, name, b).Low.Json;
        Assert.True(Compiler.Plans.TryGetValue(json, out var plan));
        MoveApplications.Start(mod, plan!, json);
        return (mod, plan!);
    }

    private static MoveApplicationResult Status(string name = "warden") => MoveApplications.Status(Mod, name)!;

    // ---- the shape ----

    [Fact]
    public void The_public_slots_are_the_planners_slots_in_the_same_order()
    {
        Assert.Equal(Enum.GetNames<MoveSlot>(), Enum.GetNames<WeaponMoveSlot>());
        foreach (var s in Enum.GetValues<WeaponMoveSlot>()) Assert.Equal(s.ToString(), ((MoveSlot)s).ToString());
    }

    [Fact]
    public void Json_and_csharp_lower_to_the_same_and_the_plan_lists_the_routes()
    {
        const string json = """
            { "name": "Warden", "class": "sword",
              "moves": { "primary": [ { "like": "Light1", "hitAt": 0.3 }, { "like": "Light2" } ], "parry": { "like": "ParryAll" },
                         "block": { "followUps": [ { "trigger": "parried", "to": "parry" }, { "trigger": "primary", "to": "primary" } ] } } }
            """;
        var a = Lower(Warden());
        Assert.Equal(ItemCompileGoldenTests.Text(a), ItemCompileGoldenTests.Text(Lower(BlockJson.Parse<WeaponBlock>(json))));
        var block = a["moves"]!["clones"]!.AsArray().Single(c => (string)c!["slot"]! == "block")!;
        Assert.Equal(["Parried", "Primary"], block["followUps"]!.AsArray().Select(r => (string)r!["trigger"]!));
        Assert.Equal("parry", (string)block["followUps"]![0]!["to"]!);
        // a routed weapon's clones are a set per generation: none is defined at compile, also not those with a like
        Assert.Null(a["sequences"]);
    }

    [Fact]
    public void A_weapon_without_follow_ups_keeps_its_clones_and_has_no_status()
    {
        var plain = Lower(Warden() with { Moves = new WeaponMoves { Primary = new MoveBlock { Like = "Light1" } } });
        Assert.Equal(BlockIds.Derive(Mod, "sequence", "warden.primary").ToString(), (string)plain["sequences"]![0]!["guid"]!);
        Assert.Null(plain["moves"]!["generation"]);
        Assert.Null(plain["moves"]!["clones"]![0]!["followUps"]);
        Assert.Null(MoveApplications.Status(Mod, "warden"));
    }

    [Fact]
    public void A_held_capability_refuses_the_definition_by_name_and_leaves_other_weapons_alone()
    {
        BlockCapabilities.Held = name => name == "blocks.move_followups";
        var e = Assert.Throws<KilnUnavailableException>(() => Lower(Warden()));
        Assert.Contains("blocks.move_followups", e.Message);
        Lower(Warden() with { Moves = new WeaponMoves { Primary = new MoveBlock { Like = "Light1" } } });
    }

    // ---- refused as written ----

    [Fact]
    public void Routes_wrong_as_written_are_refused_with_their_field()
    {
        // the target slot is not one of the weapon's moves
        Assert.Equal("moves.block.followUps[0].to", Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Heavy))).Field);
        // the chain has two links
        Assert.Equal("moves.block.followUps[0].link", Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, link: 3))).Field);
        Assert.Equal("moves.block.followUps[0].link", Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, link: 0))).Field);
        Assert.Equal("moves.block.followUps[0].occurrence", Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, occurrence: -1))).Field);
        Assert.Equal("moves.block.followUps[0].subSequence", Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, sub: -1))).Field);
        Assert.Equal("moves.block.followUps[0].trigger", Refused(Warden(Route((MoveFollowUpTrigger)9, WeaponMoveSlot.Primary))).Field);
        var many = Enumerable.Range(0, 65).Select(i => Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, occurrence: i)).ToArray();
        Assert.Equal("moves.block.followUps", Refused(Warden(many)).Field);
        Lower(Warden(many[..64]));
    }

    [Fact]
    public void Identical_routes_and_all_against_one_occurrence_are_refused_as_written_naming_both()
    {
        var same = Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, occurrence: 0);
        var e = Refused(Warden(same, same));
        Assert.Equal("moves.block.followUps[1]", e.Field);
        Assert.Contains("followUps[0]", e.Message);
        // all Primary windows and Primary occurrence 0 select a common window, whichever comes first and whatever they lead to
        e = Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary), Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Parry, occurrence: 0)));
        Assert.Equal("moves.block.followUps[1]", e.Field);
        Assert.Contains("followUps[0]", e.Message);
        Refused(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Parry, occurrence: 0), Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary)));
        // disjoint occurrences, another trigger and another sub-sequence do not overlap
        Lower(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, occurrence: 0), Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Parry, occurrence: 1),
            Route(MoveFollowUpTrigger.Parried, WeaponMoveSlot.Parry), Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, sub: 1)));
    }

    // ---- the resolver, on the game's rows ----

    private static (Dictionary<(PlannedMove Move, int Sub, int K), PlannedMove> Routes, List<string> Problems, MovePlan Plan) Resolve(WeaponBlock b, bool offline = true)
    {
        MovePlan plan;
        if (offline) Assert.True(Compiler.Plans.TryGetValue(Lower(b), out plan!));
        else
        {
            // past the offline check, as a plan whose overlap only the data shows would be: the same plan with the routes put in afterwards
            Assert.True(Compiler.Plans.TryGetValue(Lower(Warden()), out plan!));
            plan = plan with { Moves = plan.Moves.Select(m => m.Slot == MoveSlot.Block ? m with { FollowUps = b.Moves!.Block!.FollowUps } : m).ToList() };
        }
        var (work, bases, _) = MovePlanner.Find(plan, Slots, new FakeGame().Reader());
        Assert.Empty(work.Problems);
        var problems = new List<string>();
        return (MoveRoutes.Resolve(plan, bases, problems), problems, plan);
    }

    [Fact]
    public void A_route_selects_only_windows_with_a_follow_up_of_its_trigger_in_its_sub_sequence()
    {
        var (routes, problems, plan) = Resolve(Warden());
        Assert.Empty(problems);
        var block = plan.Moves.Single(m => m.Slot == MoveSlot.Block);
        // the block's SetActionOportunity events of sub-sequence 0: #0 Parried, #1 Primary. Sub-sequence 1's Primary window is not selected by SubSequence 0
        Assert.Equal([(0, 0), (0, 1)], routes.Keys.Where(k => k.Move == block).Select(k => (k.Sub, k.K)).Order());
        Assert.Equal(MoveSlot.Parry, routes[(block, 0, 0)].Slot);
        Assert.Equal((MoveSlot.Primary, 0), (routes[(block, 0, 1)].Slot!.Value, routes[(block, 0, 1)].Link));
    }

    [Fact]
    public void Occurrences_count_the_triggers_windows_with_a_follow_up_in_the_motions_order()
    {
        // on the first light attack: Evade (no follow-up, never counted) is #0, the two Primary rows #1 and #2, Heavy #3
        var sword = Warden() with
        {
            Moves = Warden().Moves! with
            {
                Primary =
                [
                    new MoveBlock { Like = "Light1", FollowUps = [Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Parry, occurrence: 1)] },
                    new MoveBlock { Like = "Light2" },
                ],
                Block = null,
            },
        };
        var (routes, problems, plan) = Resolve(sword);
        Assert.Empty(problems);
        var first = plan.Moves.Single(m => m.Slot == MoveSlot.Primary && m.Link == 0);
        Assert.Equal([(first, 0, 2)], routes.Keys);
        // and through the planner: only that row leaves the chain, the other Primary row and the Heavy row keep the chain's own links
        var (gen, planProblems) = MoveRoutes.Plan(plan, Slots, new FakeGame().Reader(), null);
        Assert.Empty(planProblems);
        var rules = gen!.SequenceRules.Single(r => r.Entry.StartsWith("routes-test.warden.primary.1.r")).Rules;
        var name = gen.Clones.ToDictionary(c => (string)c["name"]!, c => Guid.Parse((string)c["guid"]!));
        Assert.Equal(gen.LiveClones["primary/2"], name[(string)rules["events[SetActionOportunity#1].followUp"]!]);
        Assert.Equal(gen.LiveClones["parry/1"], name[(string)rules["events[SetActionOportunity#2].followUp"]!]);
        Assert.Null(rules["events[SetActionOportunity#0].followUp"]);     // the Evade window has no follow-up: none is made
        Assert.Null(rules["events[SetActionOportunity#3].followUp"]);     // Heavy: the weapon has no heavy move, the game's stays
    }

    [Fact]
    public void A_route_with_no_window_a_missing_sub_sequence_or_occurrence_is_a_problem_by_field()
    {
        // the block has no Heavy window
        var (_, p1, _) = Resolve(Warden(Route(MoveFollowUpTrigger.Heavy, WeaponMoveSlot.Primary)));
        Assert.Contains("moves.block.followUps[0]: the motion has no follow-up window for Heavy", Assert.Single(p1));
        var (_, p2, _) = Resolve(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, sub: 2)));
        Assert.Contains("followUps[0].subSequence", Assert.Single(p2));
        var (_, p3, _) = Resolve(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary, occurrence: 1)));
        Assert.Contains("followUps[0].occurrence", Assert.Single(p3));
    }

    [Fact]
    public void Routes_that_meet_on_a_window_in_the_data_are_refused_naming_both_and_the_window()
    {
        var (routes, problems, _) = Resolve(Warden(Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Primary), Route(MoveFollowUpTrigger.Primary, WeaponMoveSlot.Parry, occurrence: 0)), offline: false);
        var text = Assert.Single(problems);
        Assert.Contains("followUps[1]", text);
        Assert.Contains("followUps[0]", text);
        Assert.Contains("window 1", text);
        // no last-wins: the first route's target stays in the table, and the plan as a whole is refused
        Assert.Equal(MoveSlot.Primary, routes.Values.Single().Slot);
    }

    [Fact]
    public void The_plan_hash_follows_the_routes_and_the_data_and_nothing_else()
    {
        string Hash(WeaponBlock b, IReadOnlyList<SlotRow>? slots = null)
        {
            Assert.True(Compiler.Plans.TryGetValue(Lower(b), out var plan));
            var (gen, problems) = MoveRoutes.Plan(plan!, slots ?? Slots, new FakeGame().Reader(), null);
            Assert.Empty(problems);
            return gen!.Hash;
        }
        var a = Hash(Warden());
        Assert.Equal(a, Hash(Warden()));
        Assert.NotEqual(a, Hash(Warden(Route(MoveFollowUpTrigger.Parried, WeaponMoveSlot.Parry))));
        Assert.NotEqual(a, Hash(Warden(), [.. Slots.Reverse()]));
    }

    // ---- the application ----

    [Fact]
    public void A_generation_applies_whole_and_says_so_once_after_the_switch()
    {
        Start(Warden());
        Assert.Equal((MoveApplicationState.PendingContent, false, "PendingContent"), (Status().State, Status().Ok, Status().Reason));
        // the first pass plans and stages: the clones are defined, nothing is applied, nothing is said
        _game.Pending.Dequeue()(_ => { });
        Assert.Equal(4, _game.Defined.Count);
        Assert.Empty(_game.Applied);
        Assert.Empty(_events);
        Assert.Equal(MoveApplicationState.PendingContent, Status().State);
        Assert.Empty(_game.Pump());
        var now = Status();
        Assert.Equal((true, MoveApplicationState.Applied, "", 1ul, 1ul, 3ul), (now.Ok, now.State, now.Reason, now.RequestedGeneration, now.AppliedGeneration, now.ContentGeneration));
        Assert.Equal(["block/1", "parry/1", "primary/1", "primary/2"], now.LiveClones.Keys.Order());
        // two documents: the clones' rules, then the item's slots alone
        Assert.Equal(2, _game.Applied.Count);
        Assert.NotNull(_game.Applied[0]["sequences"]);
        Assert.Null(_game.Applied[0]["itemdata"]);
        Assert.Equal(["format", "itemdata"], _game.Applied[1].Select(p => p.Key));
        // block at 0, the air heavy following the primary at 1, the primary at 3, the parry at 4; the heavy at 2 is not the weapon's
        Assert.Equal(new Dictionary<int, Guid> { [0] = now.LiveClones["block/1"], [1] = now.LiveClones["primary/1"], [3] = now.LiveClones["primary/1"], [4] = now.LiveClones["parry/1"] }, _game.SlotsNow);
        // the block's windows lead to this weapon's own parry and first light attack; the rows of the light chain link to each other
        var name = _game.ByName.ToDictionary(x => x.Value, x => x.Key);
        var block = name[now.LiveClones["block/1"]];
        Assert.Equal(now.LiveClones["parry/1"], _game.LinksNow[(block, 0, 0)]);
        Assert.Equal(now.LiveClones["primary/1"], _game.LinksNow[(block, 0, 1)]);
        Assert.False(_game.LinksNow.ContainsKey((block, 1, 0)));        // the second sub-sequence was not routed
        Assert.Equal(now.LiveClones["primary/2"], _game.LinksNow[(name[now.LiveClones["primary/1"]], 0, 1)]);
        Assert.Equal(now.LiveClones["primary/2"], _game.LinksNow[(name[now.LiveClones["primary/1"]], 0, 2)]);
        // the GUIDs are the generation's, not the stable clones'
        Assert.DoesNotContain(BlockIds.Derive(Mod, "sequence", "warden.block"), now.LiveClones.Values);
        var ev = Assert.Single(_events);
        Assert.Equal((Mod, "warden", MoveApplicationState.PendingContent, MoveApplicationState.Applied), (ev.ModId, ev.BlockName, ev.Before.State, ev.After.State));
        Assert.Same(now, ev.After);
        // kept for later content applications: the clones' rules and the slots
        Assert.Contains(_game.Kept, r => r.Section == "itemdata");
        Assert.Equal(_game.Applied[0]["sequences"]!.AsObject().Count + 1, _game.Kept.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_clone_the_game_refuses_leaves_the_slots_and_the_generation_and_a_retry_applies_once(int nth)
    {
        _game.RefuseDefineAt = nth;
        var (started, _) = Start(Warden());
        var problems = _game.Pump();
        Assert.Contains("the registry is full", Assert.Single(problems));
        var now = Status();
        Assert.Equal((false, MoveApplicationState.Refused, 1ul, 0ul), (now.Ok, now.State, now.RequestedGeneration, now.AppliedGeneration));
        Assert.Contains("was refused: the registry is full", now.Reason);
        Assert.Empty(now.LiveClones);
        Assert.Empty(_game.Applied);            // no rule and no slot was written
        Assert.Empty(_game.SlotsNow);
        Assert.Empty(_game.Kept);
        Assert.Equal(MoveApplicationState.Refused, Assert.Single(_events).After.State);
        // on the same content nothing is tried again by itself
        MoveApplications.Ready(started, _ => { });
        Assert.Empty(_game.Pending);
        // the retry: the clones staged before the refusal are not defined twice
        Assert.True(MoveApplications.Retry(Mod, "warden", _ => { }));
        Assert.Empty(_game.Pump());
        now = Status();
        Assert.Equal((true, MoveApplicationState.Applied, 2ul, 2ul), (now.Ok, now.State, now.RequestedGeneration, now.AppliedGeneration));
        Assert.Equal(4, _game.DefinedNames.Distinct().Count());
        Assert.Equal(4, _game.DefinedNames.Count);
        Assert.Equal([MoveApplicationState.Refused, MoveApplicationState.Applied], _events.Select(e => e.After.State));
    }

    [Fact]
    public void A_refusal_on_older_content_is_tried_again_when_content_is_applied_again()
    {
        _game.SlotRows = null;                  // the item is not in the game yet
        var (mod, _) = Start(Warden());
        _game.Pump();
        Assert.Equal(MoveApplicationState.Refused, Status().State);
        Assert.Contains("no item", Status().Reason);
        _game.SlotRows = Slots;
        MoveApplications.Ready(mod, _ => { });  // same content: nothing
        Assert.Empty(_game.Pending);
        Assert.Empty(_game.Defined);
        _game.ContentGeneration = 4;
        MoveApplications.Ready(mod, _ => { });
        _game.Pump();
        Assert.Equal((MoveApplicationState.Applied, 2ul, 4ul), (Status().State, Status().AppliedGeneration, Status().ContentGeneration));
    }

    [Fact]
    public void A_rule_the_game_refuses_ends_the_attempt_before_any_slot_changes()
    {
        _game.ApplyError = doc => doc["sequences"] != null ? "events[SetActionOportunity#1].followUp: no such field" : null;
        Start(Warden());
        _game.Pump();
        Assert.Equal(MoveApplicationState.Refused, Status().State);
        Assert.Contains("no such field", Status().Reason);
        Assert.Single(_game.Applied);           // the slots' document was never sent
        Assert.Empty(_game.SlotsNow);
        Assert.Equal(0ul, Status().AppliedGeneration);
    }

    [Fact]
    public void A_link_that_does_not_read_back_ends_the_attempt_before_any_slot_changes()
    {
        Start(Warden());
        _game.Pending.Dequeue()(_ => { });
        // the game takes the document and says nothing, but one link is not what was set
        var real = _game.ApplyError;
        _game.ApplyError = doc =>
        {
            if (doc["sequences"] is JsonObject s) s.Remove(s.First().Key);
            return null;
        };
        _game.Pump();
        Assert.Equal(MoveApplicationState.Refused, Status().State);
        Assert.Contains("did not read back", Status().Reason);
        Assert.Empty(_game.SlotsNow);
        _game.ApplyError = real;
    }

    [Fact]
    public void A_switch_that_does_not_take_whole_is_put_back_in_the_same_pass()
    {
        // first a generation that applies
        Start(Warden());
        _game.Pump();
        var first = Status();
        var slots = new Dictionary<int, Guid>(_game.SlotsNow);
        // then other routes, and a game that takes only the first slot of the switch
        _game.SwitchTakesOnlyFirst = true;
        var (mod2, plan2) = (NewMod(), (MovePlan?)null);
        Assert.True(Compiler.Plans.TryGetValue(Compiler.Lower(mod2, "warden", Warden(Route(MoveFollowUpTrigger.Parried, WeaponMoveSlot.Parry))).Low.Json, out plan2));
        // the same weapon, replanned in place (what a changed block does while its mod stays loaded)
        Replan(plan2!);                         // planned and staged; its completion waits for the next pass
        var problems = new List<string>();
        _game.Pending.Dequeue()(problems.Add);
        var now = Status();
        Assert.Equal((MoveApplicationState.Refused, 2ul, 1ul), (now.State, now.RequestedGeneration, now.AppliedGeneration));
        Assert.Contains("were not switched", now.Reason);
        Assert.Equal(first.LiveClones, now.LiveClones);
        // the put-back document went out after the switch: the same slots, the previous generation's clones (the fake takes only its first path too,
        // which is the one the failed switch had written)
        Assert.Contains("itemdata", _game.Applied[^1].Select(p => p.Key));
        Assert.Equal(slots, _game.SlotsNow);
    }

    // a second plan for a weapon that is applied: the entry is the same, its plan is not (tests only: the product gets a new plan from a new definition)
    private void Replan(MovePlan plan)
    {
        var entries = typeof(MoveApplications).GetField("Entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var entry = ((System.Collections.IDictionary)entries)[(Mod, "warden")]!;
        entry.GetType().GetField("Plan")!.SetValue(entry, plan);
        Assert.True(MoveApplications.Retry(Mod, "warden", _ => { }));
    }

    [Fact]
    public void A_first_switch_that_fails_puts_the_classs_slots_back()
    {
        _game.ApplyError = doc => doc["itemdata"] is JsonObject items && items.First().Value!.AsObject().First().Value is not JsonObject ? "sequences[0].sequence: not a reference" : null;
        Start(Warden());
        _game.Pump();
        Assert.Equal((MoveApplicationState.Refused, 0ul), (Status().State, Status().AppliedGeneration));
        var back = _game.Applied[^1]["itemdata"]!.AsObject().First().Value!.AsObject();
        Assert.All(back, p => Assert.True((bool)p.Value!["reset"]!));
        Assert.Equal(4, back.Count);
        Assert.Empty(_game.Kept);
    }

    [Fact]
    public void The_same_plan_again_defines_nothing_writes_nothing_and_raises_nothing()
    {
        Start(Warden());
        _game.Pump();
        var (defines, applied, first) = (_game.Defines, _game.Applied.Count, Status());
        Assert.True(MoveApplications.Retry(Mod, "warden", _ => { }));
        Assert.Empty(_game.Pending);
        Assert.Equal((defines, applied), (_game.Defines, _game.Applied.Count));
        Assert.Same(first, Status());
        Assert.Single(_events);
    }

    [Fact]
    public void A_stale_completion_is_discarded()
    {
        Start(Warden());
        _game.Pending.Dequeue()(_ => { });      // staged; its completion waits
        var stale = _game.Pending.Dequeue();
        _game.RefuseDefineAt = -1;
        // a newer attempt takes the weapon before the first completes
        Assert.True(Compiler.Plans.TryGetValue(Lower(Warden(Route(MoveFollowUpTrigger.Parried, WeaponMoveSlot.Parry))), out var other));
        Replan(other!);
        stale(_ => { });
        Assert.Empty(_game.Applied);            // the stale one wrote nothing
        Assert.Empty(_events);
        _game.Pump();
        Assert.Equal((MoveApplicationState.Applied, 2ul, 2ul), (Status().State, Status().RequestedGeneration, Status().AppliedGeneration));
        Assert.Single(_events);
    }

    [Fact]
    public void A_seventeenth_plan_is_refused_as_restart_required_without_a_write()
    {
        Start(Warden());
        _game.Pump();
        for (var i = 1; i < MoveRoutes.MaxHashes; i++)
        {
            // another plan each time: the first light attack's blow moves
            Assert.True(Compiler.Plans.TryGetValue(Lower(Warden() with { Moves = Warden().Moves! with { Primary = [new MoveBlock { Like = "Light1", HitAt = 0.3 + i * 0.01 }, new MoveBlock { Like = "Light2" }] } }), out var p));
            Replan(p!);
            Assert.Empty(_game.Pump());
            Assert.Equal((ulong)(i + 1), Status().AppliedGeneration);
        }
        var (defines, applied, before) = (_game.Defines, _game.Applied.Count, Status());
        Assert.True(Compiler.Plans.TryGetValue(Lower(Warden() with { Moves = Warden().Moves! with { Primary = [new MoveBlock { Like = "Light1", HitAt = 0.9 }, new MoveBlock { Like = "Light2" }] } }), out var last));
        Replan(last!);
        Assert.Empty(_game.Pending);
        Assert.StartsWith("RestartRequired", Status().Reason);
        Assert.Equal((MoveApplicationState.Refused, before.AppliedGeneration, defines, applied), (Status().State, Status().AppliedGeneration, _game.Defines, _game.Applied.Count));
        Assert.Equal(before.LiveClones, Status().LiveClones);
        // a plan it has had before is still taken
        Assert.True(Compiler.Plans.TryGetValue(Lower(Warden()), out var again));
        Replan(again!);
        Assert.Empty(_game.Pump());
        Assert.Equal(MoveApplicationState.Applied, Status().State);
        Assert.Equal(defines, _game.Defines);
    }

    [Fact]
    public void A_mod_that_goes_reports_removed_with_the_generation_it_had_and_drops_its_kept_rules()
    {
        Start(Warden());
        _game.Pump();
        Assert.NotEmpty(_game.Kept);
        MoveApplications.Forget(Mod);
        var now = Status();
        Assert.Equal((false, MoveApplicationState.Removed, "Removed", 1ul), (now.Ok, now.State, now.Reason, now.AppliedGeneration));
        Assert.Empty(now.LiveClones);
        Assert.Empty(_game.Kept);
        Assert.Equal([MoveApplicationState.Applied, MoveApplicationState.Removed], _events.Select(e => e.After.State));
        // once: a second Forget and a late pass change nothing
        MoveApplications.Forget(Mod);
        Assert.False(MoveApplications.Retry(Mod, "warden", _ => { }) && _game.Pending.Count > 0);
        Assert.Equal(2, _events.Count);
    }

    // ---- the query ----

    // the class Blocks is hidden by this assembly's namespace Kiln.Blocks
    private static readonly Type TheBlocks = typeof(IBlock).Assembly.GetType("Kiln.Blocks", true)!;

    private static MoveApplicationResult MovesStatus(BlockHandle<WeaponBlock> weapon)
    {
        try { return (MoveApplicationResult)TheBlocks.GetMethod("MovesStatus")!.Invoke(null, [weapon])!; }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    [Fact]
    public void The_status_query_refuses_a_held_row_a_stale_handle_and_a_weapon_without_routes()
    {
        var mod = (ModBlocks)TheBlocks.GetMethod("ModFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [Mod, (Func<ulong>)(() => 7)])!;
        mod.Reset();
        // the handles as Blocks.Define makes them (it needs kiln.content, which a test has not): the routed weapon started, the plain one not
        var routed = new BlockHandle<WeaponBlock>(Mod, "warden", Guid.NewGuid(), "routes-test.warden", Warden());
        var plain = new BlockHandle<WeaponBlock>(Mod, "plain", Guid.NewGuid(), "routes-test.plain", new WeaponBlock { Name = "Plain", Class = WeaponClass.Sword });
        mod.Handles["warden"] = routed;
        mod.Handles["plain"] = plain;
        Start(Warden());
        var gamedata = BlockCapabilities.GameData;
        try
        {
            BlockCapabilities.GameData = () => true;
            Assert.Equal(MoveApplicationState.PendingContent, MovesStatus(routed).State);
            Assert.Contains("no followUps", Assert.Throws<ArgumentException>(() => MovesStatus(plain)).Message);
            Assert.Throws<ArgumentNullException>(() => MovesStatus(null!));
            // a handle the mod no longer has under that name
            mod.Handles.TryRemove("warden", out _);
            Assert.Throws<ArgumentException>(() => MovesStatus(routed));
            mod.Handles["warden"] = routed;
            BlockCapabilities.Held = name => name == "blocks.move_followups";
            Assert.Contains("blocks.move_followups", Assert.Throws<KilnUnavailableException>(() => MovesStatus(routed)).Message);
            BlockCapabilities.Held = _ => false;
            BlockCapabilities.GameData = () => false;
            Assert.Throws<KilnUnavailableException>(() => MovesStatus(routed));
        }
        finally
        {
            BlockCapabilities.GameData = gamedata;
            mod.Reset();
        }
    }
}
