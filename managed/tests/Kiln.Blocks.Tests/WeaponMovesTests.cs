// SPDX-License-Identifier: MIT
// A weapon's moves, with no game: what a block lowers to (clones, GUIDs, states, the plan, the slots' selectors), that JSON and C# say the same, what is
// refused offline, the slot matcher, and what content ready makes of it, over made-up item slots and sequences (MovePlanner is pure; MoveRuntime only
// reads the game into the same rows). The game's own tables are not used here: the rows below are invented.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("player-state seam")]
public sealed class WeaponMovesTests : IDisposable
{
    private const string Mod = "moves-test";

    public WeaponMovesTests() => PlayerStateSeam.Lookup = null;
    public void Dispose() => PlayerStateSeam.Lookup = null;

    private static ModBlocks NewMod() => new(Mod, 1) { ManifestClients = "mod" };

    private static JsonObject Lower<T>(T b, string name = "cinderbrand") where T : class, IBlock => Compiler.Lower(NewMod(), name, b).Low.Json;

    private static BlockError Refused<T>(T b) where T : class, IBlock => Assert.Throws<BlockError>(() => Lower(b));

    private const string Json = """
        { "name": "Cinderbrand", "class": "sword",
          "moves": {
            "primary": [ { "animation": "0x11223344", "like": "Attack_Player_1H_Single_LightChain1", "hitAt": 0.28 },
                         { "animation": "cinder-slash-2", "like": "Attack_Player_1H_Single_LightChain2", "hitAt": 0.31 } ],
            "heavy":   { "animation": "6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", "hitAt": 0.9, "length": 2.2 },
            "block":   { "animation": "cinder-guard" } } }
        """;

    private static WeaponBlock CSharp() => new()
    {
        Name = "Cinderbrand", Class = WeaponClass.Sword,
        Moves = new WeaponMoves
        {
            Primary =
            [
                new MoveBlock { Animation = "0x11223344", Like = "Attack_Player_1H_Single_LightChain1", HitAt = 0.28 },
                new MoveBlock { Animation = "cinder-slash-2", Like = "Attack_Player_1H_Single_LightChain2", HitAt = 0.31 },
            ],
            Heavy = new MoveBlock { Animation = "6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", HitAt = 0.9, Length = 2.2 },
            Block = new MoveBlock { Animation = "cinder-guard" },
        },
    };

    // ---- what a block lowers to ----

    [Fact]
    public void A_weapon_with_moves_lowers_to_its_clones_states_links_and_slots()
    {
        var j = Lower(CSharp());
        var seqs = j["sequences"]!.AsArray();
        // the moves that name their sequence are cloned at compile; the heavy and the block keep the class's, cloned when content is ready
        Assert.Equal(["moves-test.cinderbrand.primary.1", "moves-test.cinderbrand.primary.2"], seqs.Select(x => (string)x!["name"]!));
        Assert.Equal(BlockIds.Derive(Mod, "sequence", "cinderbrand.primary.1").ToString(), (string)seqs[0]!["guid"]!);
        Assert.Equal(BlockIds.Derive(Mod, "sequence", "cinderbrand.primary.2").ToString(), (string)seqs[1]!["guid"]!);
        Assert.Equal("Attack_Player_1H_Single_LightChain1", (string)seqs[0]!["base"]!);
        Assert.Equal("Attack_Player_1H_Single_LightChain2", (string)seqs[1]!["base"]!);

        var moves = j["moves"]!.AsObject();
        var clones = moves["clones"]!.AsArray();
        Assert.Equal(["primary", "primary", "heavy", "block"], clones.Select(c => (string)c!["slot"]!));
        Assert.Equal("moves-test.cinderbrand.heavy", (string)clones[2]!["name"]!);
        Assert.Equal(BlockIds.Derive(Mod, "sequence", "cinderbrand.heavy").ToString(), (string)clones[2]!["guid"]!);
        Assert.Equal("(the slot's own)", (string)clones[2]!["like"]!);
        // states: an id becomes 16 bytes with that hash; a GUID is itself; a name waits for content ready
        Assert.Equal(0x11223344u, BlockIds.Fnv(Guid.Parse((string)clones[0]!["animation"]![0]!["guid"]!)));
        Assert.Equal("cinder-slash-2", (string)clones[1]!["animation"]![0]!["lookup"]!);
        Assert.Equal("6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", (string)clones[2]!["animation"]![0]!["guid"]!);
        Assert.Equal(0.9, (double)clones[2]!["timing"]!["hitAt"]!);
        Assert.Equal(2.2, (double)clones[2]!["timing"]!["length"]!);
        // links: each primary link to the next, the last to the first, their heavy follow-ups to the heavy; the heavy's primary to the first link
        Assert.Equal("moves-test.cinderbrand.primary.2", (string)clones[0]!["links"]!["Primary"]!);
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)clones[1]!["links"]!["Primary"]!);
        Assert.Equal("moves-test.cinderbrand.heavy", (string)clones[1]!["links"]!["Heavy"]!);
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)clones[2]!["links"]!["Primary"]!);
        // the slots by selector; the air heavy follows the primary's first link
        var slots = moves["slots"]!.AsObject();
        Assert.Equal(["primary", "heavy", "heavyAir", "block"], slots.Select(s => s.Key));
        Assert.Equal("Heavy", (string)slots["heavy"]!["triggeredBy"]!);
        Assert.Equal(["IsGrounded"], slots["heavy"]!["requirements"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(["IsAirborne"], slots["heavyAir"]!["requirements"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)slots["heavyAir"]!["plays"]!);
    }

    [Fact]
    public void Json_and_csharp_lower_to_the_same()
    {
        Assert.Equal(ItemCompileGoldenTests.Text(Lower(CSharp())), ItemCompileGoldenTests.Text(Lower(BlockJson.Parse<WeaponBlock>(Json))));
    }

    [Fact]
    public void A_weapon_without_moves_lowers_as_before()
    {
        var plain = Lower(new WeaponBlock { Name = "Cinderbrand", Class = WeaponClass.Sword });
        Assert.Null(plain["moves"]);
        Assert.Null(plain["sequences"]);
    }

    [Fact]
    public void A_was_keeps_the_clones_ids()
    {
        var renamed = Lower(CSharp() with { Was = "cinderbrand" }, "ember-brand");
        Assert.Equal(BlockIds.Derive(Mod, "sequence", "cinderbrand.primary.1").ToString(), (string)renamed["sequences"]![0]!["guid"]!);
        Assert.Equal("moves-test.ember-brand.primary.1", (string)renamed["sequences"]![0]!["name"]!);
    }

    [Fact]
    public void A_chain_of_one_is_one_move()
    {
        var one = Lower(new WeaponBlock { Name = "A", Class = WeaponClass.Axe, Moves = new() { Primary = new MoveBlock { Like = "X" } } });
        var list = Lower(BlockJson.Parse<WeaponBlock>("""{ "name": "A", "class": "axe", "moves": { "primary": [ { "like": "X" } ] } }"""));
        Assert.Equal(ItemCompileGoldenTests.Text(one), ItemCompileGoldenTests.Text(list));
        Assert.Equal("moves-test.cinderbrand.primary", (string)one["sequences"]![0]!["name"]!);
    }

    [Fact]
    public void An_own_class_has_its_premades_slots()
    {
        var own = new WeaponClass { Like = "shield" };
        Assert.Equal("moves.primary", Refused(new WeaponBlock { Name = "A", Class = own, Moves = new() { Primary = new MoveBlock { HitAt = 0.3 } } }).Field);
        Lower(new WeaponBlock { Name = "A", Class = own, Moves = new() { Parry = new MoveBlock { Animation = "0x00000001" } } });
    }

    // ---- refused offline ----

    [Fact]
    public void A_slot_the_class_lacks_is_refused_naming_the_weapon_the_slot_and_what_it_has()
    {
        var e = Refused(new WeaponBlock { Name = "Guard", Class = WeaponClass.Shield, Moves = new() { Heavy = new MoveBlock { HitAt = 1 } } });
        Assert.Equal("moves.heavy", e.Field);
        Assert.Contains("weapon Guard", e.Message);
        Assert.Contains("block, parry", e.Message);
        Assert.Contains("none", Refused(new WeaponBlock { Name = "S", Class = WeaponClass.Staff, Moves = new() { Primary = new MoveBlock { HitAt = 1 } } }).Message);
    }

    [Theory]
    [InlineData("""{ "primary": { } }""", "moves.primary", "changes nothing")]
    [InlineData("""{ "primary": [ { "like": "A" }, { } ] }""", "moves.primary[1]", "changes nothing")]
    [InlineData("""{ }""", "moves", "no slot")]
    [InlineData("""{ "heavy": { "hitAt": 0 } }""", "moves.heavy.hitAt", "more than 0")]
    [InlineData("""{ "heavy": { "hitAt": 2, "length": 1.5 } }""", "moves.heavy.hitAt", "not inside")]
    [InlineData("""{ "heavy": { "length": -1 } }""", "moves.heavy.length", "more than 0")]
    [InlineData("""{ "block": { "hitAt": 0.2 } }""", "moves.block.hitAt", "no blow")]
    [InlineData("""{ "parry": { "animation": "0x123" } }""", "moves.parry.animation", "0x and 8 hex")]
    [InlineData("""{ "parry": { "animation": "" } }""", "moves.parry.animation", "names a state")]
    [InlineData("""{ "parry": { "animation": [ null ] } }""", "moves.parry.animation", "names no state")]
    [InlineData("""{ "parry": { "animation": "a/b/c" } }""", "moves.parry.animation", "not a state's name")]
    [InlineData("""{ "primary": { "like": " " } }""", "moves.primary.like", "sub-sequence")]
    public void Wrong_moves_are_refused_by_field(string moves, string field, string says)
    {
        var e = Refused(BlockJson.Parse<WeaponBlock>($$"""{ "name": "W", "class": "sword", "moves": {{moves}} }"""));
        Assert.Equal(field, e.Field);
        Assert.Contains(says, e.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Numbers_that_are_not_finite_are_refused(double n)
    {
        Assert.Equal("moves.heavy.hitAt", Refused(new WeaponBlock { Name = "W", Class = WeaponClass.Sword, Moves = new() { Heavy = new MoveBlock { HitAt = n } } }).Field);
        Assert.Equal("moves.heavy.length", Refused(new WeaponBlock { Name = "W", Class = WeaponClass.Sword, Moves = new() { Heavy = new MoveBlock { Length = n } } }).Field);
    }

    [Fact]
    public void Json_takes_no_field_a_move_does_not_have()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<WeaponBlock>("""{ "name": "W", "class": "sword", "moves": { "primary": { "speed": 2 } } }"""));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<WeaponBlock>("""{ "name": "W", "class": "sword", "moves": { "kick": { "hitAt": 2 } } }"""));
    }

    // ---- the slot matcher ----

    private static SlotRow Row(string trigger, string special = "Invalid", params string[] req) => new(trigger, req, special, Guid.NewGuid());

    [Fact]
    public void Slots_are_found_by_selector_in_any_order_and_every_match_counts()
    {
        var sel = MoveSlots.Of(WeaponClass.Daggers);
        IReadOnlyList<SlotRow> a = [Row("Primary", "Invalid", "IsAirborne_Not_Falling"), Row("Heavy", "Invalid", "IsGrounded"), Row("Primary"), Row("Primary", "Invalid", "IsAirborne_Not_Falling"),
            Row("Heavy", "Invalid", "IsAirborne_Falling", "IsAboveTarget"), Row("Interact", "Sneak_Attack"), Row("Interact", "Enemy_Execution"), Row("Heavy", "Invalid", "IsAirborne")];
        Assert.Equal([2], MoveSlots.Match(a, sel[MoveSlot.Primary]));
        Assert.Equal([1], MoveSlots.Match(a, sel[MoveSlot.Heavy]));
        Assert.Equal([7], MoveSlots.Match(a, sel[MoveSlot.HeavyAir]));
        Assert.Equal([0, 3], MoveSlots.Match(a, sel[MoveSlot.JumpAttack]));          // listed twice, both swapped
        Assert.Equal([4], MoveSlots.Match(a, sel[MoveSlot.PlungeAttack]));          // requirements are a set: their order does not count
        Assert.Equal([5], MoveSlots.Match(a, sel[MoveSlot.SneakAttack]));
        Assert.Equal([6], MoveSlots.Match(a, sel[MoveSlot.Execution]));
        Assert.Empty(MoveSlots.Match(a, sel[MoveSlot.Parry]));                       // a slot the item no longer has
        // the same slots in another order are found at their new places
        var b = a.Reverse().ToList();
        Assert.Equal([5], MoveSlots.Match(b, sel[MoveSlot.Primary]));
        Assert.Equal([4, 7], MoveSlots.Match(b, sel[MoveSlot.JumpAttack]));
        // the blunt two-handers' special needs the ground, the others' does not
        Assert.Equal(["IsGrounded"], MoveSlots.Of(WeaponClass.Hammer)[MoveSlot.Special].Requirements);
        Assert.Empty(MoveSlots.Of(WeaponClass.Sword)[MoveSlot.Special].Requirements);
    }

    // ---- content ready, over made-up rows ----

    private sealed class FakeStates(Dictionary<string, ModStateAnswer> mine, Dictionary<string, GameStateAnswer[]> game) : IPlayerStateLookup
    {
        public IReadOnlyList<GameStateAnswer> FindGameStates(string name) => game.TryGetValue(name, out var g) ? g : [];
        public ModStateAnswer? Find(string mod, string name) => mod == Mod && mine.TryGetValue(name, out var s) ? s : null;
    }

    private static EventRow Ev(string type, double t, double d = 0) => new(type, 0, t, d);

    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid(), H = Guid.NewGuid(), Block = Guid.NewGuid(), Jump = Guid.NewGuid(), Like1 = Guid.NewGuid(), Like2 = Guid.NewGuid();

    private static SubSequence Swing(Guid self, Guid primaryNext, Guid heavyNext, double hit = 0.2) => MoveTimingTests.Sub(1.6,
        Ev("SetAnimationEvent", 0) with { State = Guid.NewGuid() },
        Ev("SetActionOportunity", 0.18) with { TriggeredBy = "Primary", FollowUp = primaryNext, FollowUpTime = 0.45 },
        Ev("SetActionOportunity", 0.18) with { TriggeredBy = "Heavy", FollowUp = heavyNext, FollowUpTime = 0.55 },
        Ev("SetActionOportunity", 0.2) with { TriggeredBy = "Evade", FollowUpTime = 0.5 },
        Ev("SpawnImpact", hit, 0.15), Ev("SpawnImpact", hit, 0.15), Ev("EnableAbilityEvent", 0.5)) with { Sequence = self };

    private static readonly Dictionary<Guid, SubSequence> Game = new()
    {
        [P1] = Swing(P1, P2, H), [P2] = Swing(P2, P1, H), [H] = Swing(H, Guid.Empty, H, 0.9) with { Length = 2.4 },
        [Like1] = Swing(Like1, P2, H, 0.25), [Like2] = Swing(Like2, P1, H, 0.3),
        [Block] = MoveTimingTests.Sub(0.4, Ev("SetAnimationEvent", 0) with { State = Guid.NewGuid() }, Ev("LoopEvent", 0.1, 0.2)) with { Sequence = Block },
    };

    private static IReadOnlyList<SubSequence>? Read(string key) =>
        key switch { "Attack_Player_1H_Single_LightChain1" => [Game[Like1]], "Attack_Player_1H_Single_LightChain2" => [Game[Like2]], _ => Guid.TryParse(key, out var g) && Game.TryGetValue(g, out var s) ? [s] : null };

    // a sword's slots in an order of their own
    private static readonly IReadOnlyList<SlotRow> Slots =
    [
        new("Block", [], "Invalid", Block), new("Heavy", ["IsAirborne"], "Invalid", P1), new("Primary", ["IsAirborne_Not_Falling"], "Invalid", Jump),
        new("Heavy", ["IsGrounded"], "Invalid", H), new("Primary", [], "Invalid", P1), new("Parry", [], "Invalid", Guid.NewGuid()),
    ];

    private static (MoveWork Work, JsonObject Item, Dictionary<string, JsonObject> Seq) Ready(WeaponBlock b, IReadOnlyList<SlotRow>? slots = null)
    {
        var json = Lower(b);
        Assert.True(Compiler.Plans.TryGetValue(json, out var plan));
        var (work, bases, at) = MovePlanner.Find(plan!, slots ?? Slots, Read);
        MovePlanner.Apply(plan!, work, bases, at, PlayerStateSeam.Lookup);
        var item = work.Rules.SingleOrDefault(r => r.Section == "itemdata").Rules ?? new JsonObject();
        return (work, item, work.Rules.Where(r => r.Section == "sequences").ToDictionary(r => r.Entry, r => r.Rules));
    }

    [Fact]
    public void Content_ready_swaps_every_matching_slot_retimes_and_links_the_chain()
    {
        PlayerStateSeam.Lookup = new FakeStates(new()
        {
            ["cinder-slash-2"] = new(BlockIds.Derive(Mod, "state", "cinder-slash-2"), PlayerStateKind.State, PlayerStateStatus.Live, ""),
            ["cinder-guard"] = new(BlockIds.Derive(Mod, "state", "cinder-guard"), PlayerStateKind.State, PlayerStateStatus.Live, ""),
        }, new());
        var (w, item, seq) = Ready(CSharp());
        Assert.Empty(w.Problems);
        // slots by selector, not by index: the primary at 4, the air heavy (following the first link) at 1, the heavy at 3, the block at 0
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)item["sequences[4].sequence"]!);
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)item["sequences[1].sequence"]!);
        Assert.Equal("moves-test.cinderbrand.heavy", (string)item["sequences[3].sequence"]!);
        Assert.Equal("moves-test.cinderbrand.block", (string)item["sequences[0].sequence"]!);
        Assert.Equal(4, item.Count);
        // the class's own heavy and block are cloned now, from the slots they replace
        Assert.Equal([H.ToString(), Block.ToString()], w.Clones.Select(c => (string)c["base"]!));
        Assert.Equal(BlockIds.Derive(Mod, "sequence", "cinderbrand.heavy").ToString(), (string)w.Clones[0]["guid"]!);
        // states
        var p1 = seq["moves-test.cinderbrand.primary.1"];
        Assert.Equal(0x11223344u, BlockIds.Fnv(Guid.Parse((string)p1["events[SetAnimationEvent#0].animationName"]!)));
        Assert.Equal(BlockIds.Derive(Mod, "state", "cinder-slash-2").ToString(), (string)seq["moves-test.cinderbrand.primary.2"]["events[SetAnimationEvent#0].animationName"]!);
        Assert.Equal(BlockIds.Derive(Mod, "state", "cinder-guard").ToString(), (string)seq["moves-test.cinderbrand.block"]["events[SetAnimationEvent#0].animationName"]!);
        // the retime from the clone's own events: the like's blow at 0.25 lands at 0.28
        Assert.Equal(0.28, (double)p1["events[SpawnImpact#0].time"]!, 12);
        Assert.Equal(0.28, (double)p1["events[SpawnImpact#1].time"]!, 12);
        Assert.Equal(0.31, (double)seq["moves-test.cinderbrand.primary.2"]["events[SpawnImpact#0].time"]!, 12);
        Assert.Equal(2.2, (double)seq["moves-test.cinderbrand.heavy"]["length"]!, 12);
        // links: Primary to the next link, the last to the first; Heavy to the heavy clone; the heavy's Heavy to itself, as the game's names itself
        Assert.Equal("moves-test.cinderbrand.primary.2", (string)p1["events[SetActionOportunity#0].followUp"]!);
        Assert.Equal("moves-test.cinderbrand.heavy", (string)p1["events[SetActionOportunity#1].followUp"]!);
        Assert.Null(p1["events[SetActionOportunity#2].followUp"]);                  // an evade opening with no follow-up stays
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)seq["moves-test.cinderbrand.primary.2"]["events[SetActionOportunity#0].followUp"]!);
        Assert.Equal("moves-test.cinderbrand.heavy", (string)seq["moves-test.cinderbrand.heavy"]["events[SetActionOportunity#1].followUp"]!);
        Assert.Null(seq["moves-test.cinderbrand.heavy"]["events[SetActionOportunity#0].followUp"]);   // no primary follow-up in the game's heavy: none made
    }

    [Fact]
    public void A_heavy_alone_keeps_the_primarys_links_and_a_chain_without_like_follows_the_classs_chain()
    {
        var b = new WeaponBlock { Name = "W", Class = WeaponClass.Sword, Moves = new() { Primary = [new MoveBlock { HitAt = 0.3 }, new MoveBlock { HitAt = 0.4 }, new MoveBlock { HitAt = 0.5 }] } };
        var (w, item, seq) = Ready(b);
        // link 1 is the slot's, link 2 its follow-up; the class's chain has two, so the third has none to start from
        Assert.Equal([P1.ToString(), P2.ToString()], w.Clones.Select(c => (string)c["base"]!));
        Assert.Contains(w.Problems, p => p.Contains("moves.primary[2]") && p.Contains("2 links"));
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)item["sequences[4].sequence"]!);
        // with no heavy move, a primary link's heavy follow-up stays the game's
        Assert.Null(seq["moves-test.cinderbrand.primary.1"]["events[SetActionOportunity#1].followUp"]);
        // and with the third link gone, the chain is the two that are there
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)seq["moves-test.cinderbrand.primary.2"]["events[SetActionOportunity#0].followUp"]!);
    }

    [Fact]
    public void A_slot_the_item_no_longer_has_is_reported_and_the_rest_still_applies()
    {
        var slots = Slots.Where(s => s.Trigger != "Block").ToList();
        var (w, item, _) = Ready(CSharp(), slots);
        Assert.Contains(w.Problems, p => p.StartsWith("block weapon cinderbrand, moves.block:", StringComparison.Ordinal) && p.Contains("Block"));
        Assert.Equal("moves-test.cinderbrand.primary.1", (string)item["sequences[3].sequence"]!);
        Assert.DoesNotContain(w.Clones, c => (string)c["name"]! == "moves-test.cinderbrand.block");
    }

    [Fact]
    public void Without_player_states_a_name_is_reported_at_ready_and_the_motion_keeps_its_own()
    {
        var (w, _, seq) = Ready(CSharp());
        Assert.Contains(w.Problems, p => p.Contains("moves.primary[1].animation") && p.Contains("not available in this build"));
        Assert.Contains(w.Problems, p => p.Contains("moves.block.animation") && p.Contains("not available in this build"));
        Assert.Null(seq["moves-test.cinderbrand.primary.2"]["events[SetAnimationEvent#0].animationName"]);
        Assert.False(seq.ContainsKey("moves-test.cinderbrand.block"));
        // what is known offline still goes in
        Assert.NotNull(seq["moves-test.cinderbrand.primary.1"]["events[SetAnimationEvent#0].animationName"]);
    }

    [Fact]
    public void The_lookup_answers_mod_states_game_states_and_says_what_it_cannot_name()
    {
        var stateId = 0xabcdef01u;
        PlayerStateSeam.Lookup = new FakeStates(new()
        {
            ["live"] = new(BlockIds.Derive(Mod, "state", "live"), PlayerStateKind.State, PlayerStateStatus.Live, ""),
            ["refused"] = new(BlockIds.Derive(Mod, "state", "refused"), PlayerStateKind.State, PlayerStateStatus.Refused, "its clip is not served"),
        }, new()
        {
            ["Parry_FullBody"] = [new("SM_Main", "Parry_FullBody", PlayerStateKind.State, [stateId])],
            ["Attack_Heavy"] = [new("SM_A", "Attack_Heavy", PlayerStateKind.State, [1]), new("SM_B", "Attack_Heavy", PlayerStateKind.State, [2])],
            ["Idle_Block"] = [new("SM_Block_Locomotion", "Idle_Block", PlayerStateKind.State, [])],
            ["Jump_Attack_Start_FullBody"] = [new("SM_Jump_Attack_FullBody", "Jump_Attack_Start_FullBody", PlayerStateKind.State, [5, 6, 7, 8])],
        });
        var mod = NewMod();
        var ctx = new BlockContext(mod, "weapon w");
        (Guid Guid, PlayerStateKind? Kind)? R(string name, out string? problem) => MoveStates.Resolve(Mod, MoveStates.Parse(ctx, name, "a")!, PlayerStateSeam.Lookup, out problem);
        Assert.Equal(BlockIds.Derive(Mod, "state", "live"), R("live", out var p)!.Value.Guid);
        Assert.Null(p);
        Assert.Null(R("refused", out p));
        Assert.Contains("refused: its clip is not served", p);
        Assert.Equal(stateId, BlockIds.Fnv(R("Parry_FullBody", out p)!.Value.Guid));
        Assert.Null(R("Attack_Heavy", out p));
        Assert.Contains("SM_A/Attack_Heavy, SM_B/Attack_Heavy", p);
        Assert.Null(R("Idle_Block", out p));
        Assert.Contains("entered by no id", p);
        Assert.Null(R("Jump_Attack_Start_FullBody", out p));
        Assert.Contains("0x00000005, 0x00000006", p);
        Assert.Null(R("nothing-like-it", out p));
        Assert.Contains("no state of mod moves-test and none of the game's", p);
    }

    [Fact]
    public void A_list_of_states_names_each_state_event_and_a_short_motion_is_reported()
    {
        var plunge = MoveTimingTests.Sub(1.7, Ev("SetAnimationEvent", 0), Ev("SetAnimationEvent", 0.25), Ev("SpawnImpact", 0.4, 0.1), Ev("SetAnimationEvent", 0.4)) with { Sequence = Jump };
        var states = new List<(Guid, PlayerStateKind?)?> { (Guid.NewGuid(), null), null, (Guid.NewGuid(), null) };
        var problems = new List<string>();
        var rules = MovePlanner.SubRules("weapon w", "moves.plungeAttack", plunge, states, null, null, MoveTiming.Swing, null, problems);
        Assert.Empty(problems);
        Assert.NotNull(rules["events[SetAnimationEvent#0].animationName"]);
        Assert.Null(rules["events[SetAnimationEvent#1].animationName"]);          // null: the fall keeps its own
        Assert.NotNull(rules["events[SetAnimationEvent#2].animationName"]);
        states.Add((Guid.NewGuid(), null));
        MovePlanner.SubRules("weapon w", "moves.plungeAttack", plunge, states, null, null, MoveTiming.Swing, null, problems);
        Assert.Contains("4 states, and the motion has 3 SetAnimationEvents", Assert.Single(problems));
    }

    [Fact]
    public void A_sequence_with_two_sub_sequences_gets_its_rules_on_each()
    {
        var two = new[]
        {
            MoveTimingTests.Sub(1.6, Ev("SetAnimationEvent", 0), Ev("SpawnImpact", 0.8, 0.1)) with { Sequence = Like1, Index = 0 },
            MoveTimingTests.Sub(1.6, Ev("SetAnimationEvent", 0), Ev("SpawnImpact", 0.65, 0.1)) with { Sequence = Like1, Index = 1 },
        };
        var w = MovePlanner.Cast(Mod, "weapon w", "moves.special", "m.w.special", two, [new AnimationRef("0x00000009", BlockIds.WithFnv(9), null)], 0.5, MoveTiming.Swing, null);
        Assert.Empty(w.Problems);
        Assert.Equal(["m.w.special", "m.w.special#1"], w.Rules.Select(r => r.Entry));
        Assert.All(w.Rules, r => Assert.Equal(0.5, (double)r.Rules["events[SpawnImpact#0].time"]!, 12));
    }
}
