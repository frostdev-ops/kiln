// SPDX-License-Identifier: MIT
// Moves on casts, with no game: a wand attack's motion and a staff spell's cast take animation and hitAt (the release), on their premades. What they lower to,
// what is refused, and what content ready makes of a made-up motion: a full-body state or an overlay, by what the motion has, and the retime on the shots.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("player-state seam")]
public sealed class CastMovesTests : IDisposable
{
    private const string Mod = "cast-moves-test";

    public CastMovesTests() => PlayerStateSeam.Lookup = null;
    public void Dispose() => PlayerStateSeam.Lookup = null;

    private static JsonObject Lower<T>(T b, string name = "storm") where T : class, IBlock => Compiler.Lower(new ModBlocks(Mod, 1) { ManifestClients = "mod" }, name, b).Low.Json;

    private const string Bolt = """{ "type": "projectile", "onHit": [ { "damage": { "ice": 10 } } ] }""";

    [Fact]
    public void A_wand_attacks_motion_takes_a_state_and_a_release()
    {
        var j = Lower(BlockJson.Parse<WandBlock>($$"""
            { "name": "Storm", "primary": { "cast": { "manaCost": 4 }, "delivery": {{Bolt}} },
              "heavy": { "cast": { "manaCost": 9 }, "moves": { "like": "heavy", "animation": "0x0badf00d", "hitAt": 1.2 }, "delivery": {{Bolt}} } }
            """));
        var m = j["moves"]!["heavy.moves"]!;
        Assert.Equal("cast-moves-test.storm.heavy", (string)m["clone"]!);
        Assert.Equal("heavy", (string)m["like"]!);
        Assert.Equal(WandMoves.Heavy.Sequence, (string)m["sequence"]!);
        Assert.Equal(0x0badf00du, BlockIds.Fnv(Guid.Parse((string)m["animation"]![0]!["guid"]!)));
        Assert.Equal(1.2, (double)m["timing"]!["hitAt"]!);
        Assert.Null(j["moves"]!["primary.moves"]);
        // the C# form is the same
        var cs = Lower(new WandBlock
        {
            Name = "Storm",
            Primary = new WandAttack { Cast = new CastBlock { ManaCost = 4 }, Delivery = BlockJson.Parse<SpellBlock>($$"""{ "name": "x", "cast": { }, "delivery": {{Bolt}} }""").Delivery },
            Heavy = new WandAttack { Cast = new CastBlock { ManaCost = 9 }, Moves = WandMoves.Heavy with { Animation = "0x0badf00d", HitAt = 1.2 }, Delivery = BlockJson.Parse<SpellBlock>($$"""{ "name": "x", "cast": { }, "delivery": {{Bolt}} }""").Delivery },
        });
        Assert.Equal(ItemCompileGoldenTests.Text(j), ItemCompileGoldenTests.Text(cs));
    }

    [Fact]
    public void A_wand_object_without_like_is_the_attacks_own_motion()
    {
        var j = Lower(BlockJson.Parse<WandBlock>($$"""{ "name": "Storm", "primary": { "cast": { "manaCost": 4 }, "moves": { "hitAt": 0.3 }, "delivery": {{Bolt}} } }"""));
        Assert.Equal("bolt", (string)j["moves"]!["primary.moves"]!["like"]!);
        Assert.Equal(WandMoves.Bolt.Sequence, (string)j["sequences"]![0]!["base"]!);
    }

    [Fact]
    public void A_wand_release_is_hitAt_or_releaseAt_and_not_two_numbers()
    {
        string W(string cast, string moves) => $$"""{ "name": "Storm", "primary": { "cast": { "manaCost": 4{{cast}} }, "moves": {{moves}}, "delivery": {{Bolt}} } }""";
        var e = Assert.Throws<BlockError>(() => Lower(BlockJson.Parse<WandBlock>(W(", \"releaseAt\": 0.3", """{ "like": "bolt", "hitAt": 0.4 }"""))));
        Assert.Equal("primary.moves.hitAt", e.Field);
        Assert.Contains("releaseAt", e.Message);
        // the same number is one release: the whole motion is retimed, so the shots alone are not moved by hand
        var same = Lower(BlockJson.Parse<WandBlock>(W(", \"releaseAt\": 0.3", """{ "like": "bolt", "hitAt": 0.3 }""")));
        Assert.Null(same["sequenceRules"]!["primary"]!["events[SpawnEntityEvent#0].time"]);
        var old = Lower(BlockJson.Parse<WandBlock>(W(", \"releaseAt\": 0.3", "\"bolt\"")));
        Assert.Equal(0.3, (double)old["sequenceRules"]!["primary"]!["events[SpawnEntityEvent#0].time"]!);
        Assert.Null(old["moves"]);
    }

    [Fact]
    public void A_motion_other_than_the_premades_is_refused_with_the_reason()
    {
        var e = Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<WandBlock>($$"""{ "name": "S", "primary": { "cast": { }, "moves": { "like": "Attack_Player_Wand_FireChain_normal" }, "delivery": {{Bolt}} } }"""));
        Assert.Contains("shots are not known", e.Message);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BlockJson.Parse<WandBlock>($$"""{ "name": "S", "primary": { "cast": { }, "moves": { "speed": 2 }, "delivery": {{Bolt}} } }"""));
    }

    [Fact]
    public void A_staff_cast_takes_a_state_and_a_release()
    {
        var j = Lower(BlockJson.Parse<SpellBlock>($$"""{ "name": "Lob", "cast": { "manaCost": 9, "animation": { "like": "lob", "animation": "6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", "hitAt": 1.2 } }, "delivery": {{Bolt}} }"""));
        var m = j["moves"]!["cast.animation"]!;
        Assert.Equal("cast-moves-test.storm.cast", (string)m["clone"]!);
        Assert.Equal(CastAnimation.Lob.Sequence, (string)j["sequences"]![0]!["base"]!);
        Assert.Equal("6b1e0c52-93a4-4f7e-b2d8-1c5a7e9f3d40", (string)m["animation"]![0]!["guid"]!);
        Assert.Equal("cast-moves-test.storm.cast", (string)j["itemRules"]!["sequences[0].sequence"]!);
        // the charge time stays the premade's
        Assert.Equal(CastAnimation.Lob.ChargeSeconds, (double)j["itemRules"]!["impactValues.0x579b2657"]!);
        var e = Assert.Throws<BlockError>(() => Lower(BlockJson.Parse<SpellBlock>($$"""{ "name": "Lob", "cast": { "releaseAt": 1, "animation": { "hitAt": 1.2 } }, "delivery": {{Bolt}} }""")));
        Assert.Equal("cast.animation.hitAt", e.Field);
        // an object that only names the premade is the premade
        Assert.Null(Lower(BlockJson.Parse<SpellBlock>($$"""{ "name": "Lob", "cast": { "animation": { "like": "lob" } }, "delivery": {{Bolt}} }"""))["moves"]);
    }

    [Fact]
    public void A_wand_cast_still_refuses_a_staffs_animation()
    {
        var e = Assert.Throws<BlockError>(() => Lower(BlockJson.Parse<WandBlock>($$"""{ "name": "S", "primary": { "cast": { "animation": { "hitAt": 0.3 } }, "delivery": {{Bolt}} } }""")));
        Assert.Equal("primary.cast.animation", e.Field);
    }

    // ---- content ready, over made-up motions ----

    private static EventRow Ev(string type, double t, double d = 0) => new(type, 0, t, d);

    // a staff's FixedAnim-like cast: an overlay while it raises the staff, then the full-body cast state, the shot, the hand-back
    private static readonly SubSequence Fixed = MoveTimingTests.Sub(2.0, Ev("SetAnimationOverlayEvent", 0), Ev("LoopEvent", 0.2, 0.2), Ev("SetAnimationEvent", 0.4),
        Ev("SpawnEntityEvent", 0.65), Ev("EnableAbilityEvent", 0.9));
    // a wand chain-like cast: two overlays, two shots, one of them silenced
    private static readonly SubSequence Overlay = MoveTimingTests.Sub(1.0, Ev("SetAnimationOverlayEvent", 0), Ev("SetAnimationOverlayEvent", 0.3), Ev("SpawnEntityEvent", 0.35),
        Ev("SpawnEntityEvent", 0.4), Ev("EnableAbilityEvent", 0.6));
    private static readonly SubSequence Neither = MoveTimingTests.Sub(1.0, Ev("SpawnEntityEvent", 0.35));

    private static AnimationRef Id(uint id) => new($"0x{id:x8}", BlockIds.WithFnv(id), null);

    [Fact]
    public void A_cast_with_a_full_body_state_and_an_overlay_takes_the_full_body_one_and_an_overlay_cast_its_overlay()
    {
        var w = MovePlanner.Cast(Mod, "spell s", "cast.animation", "c", [Fixed], [Id(7)], null, e => e.Type == "SpawnEntityEvent", null);
        Assert.Empty(w.Problems);
        Assert.Equal(7u, BlockIds.Fnv(Guid.Parse((string)w.Rules[0].Rules["events[SetAnimationEvent#0].animationName"]!)));
        w = MovePlanner.Cast(Mod, "wand s", "primary.moves", "c", [Overlay], [Id(8)], null, e => e.Type == "SpawnEntityEvent", null);
        Assert.Equal(8u, BlockIds.Fnv(Guid.Parse((string)w.Rules[0].Rules["events[SetAnimationOverlayEvent#0].overlayName"]!)));
        w = MovePlanner.Cast(Mod, "wand s", "primary.moves", "c", [Neither], [Id(8)], null, e => e.Type == "SpawnEntityEvent", null);
        Assert.Contains("no SetAnimationEvent and no SetAnimationOverlayEvent", Assert.Single(w.Problems));
    }

    [Fact]
    public void A_mod_overlay_state_goes_to_the_overlay_event_even_where_there_is_a_full_body_one()
    {
        PlayerStateSeam.Lookup = new Overlays();
        var w = MovePlanner.Cast(Mod, "spell s", "cast.animation", "c", [Fixed], [new AnimationRef("glow", null, "glow")], null, e => e.Type == "SpawnEntityEvent", PlayerStateSeam.Lookup);
        Assert.Empty(w.Problems);
        Assert.Equal(BlockIds.Derive(Mod, "state", "glow").ToString(), (string)w.Rules[0].Rules["events[SetAnimationOverlayEvent#0].overlayName"]!);
        Assert.Null(w.Rules[0].Rules["events[SetAnimationEvent#0].animationName"]);
    }

    private sealed class Overlays : IPlayerStateLookup
    {
        public IReadOnlyList<GameStateAnswer> FindGameStates(string name) => [];
        public ModStateAnswer? Find(string mod, string name) => new(BlockIds.Derive(mod, "state", name), PlayerStateKind.Overlay, PlayerStateStatus.Live, "");
    }

    [Fact]
    public void A_wands_release_retimes_the_motion_on_its_firing_shots()
    {
        // the second shot fires, the first is silenced: hitAt is about the one that fires
        var w = MovePlanner.Cast(Mod, "wand s", "primary.moves", "c", [Overlay], null, 0.6, e => e.Type == "SpawnEntityEvent" && e.K == 1, null);
        Assert.Empty(w.Problems);
        var r = w.Rules[0].Rules;
        Assert.Equal(0.6, (double)r["events[SpawnEntityEvent#1].time"]!, 12);
        Assert.Equal(0.35 * 0.6 / 0.4, (double)r["events[SpawnEntityEvent#0].time"]!, 12);
        Assert.Equal(1.2, (double)r["length"]!, 12);
    }
}
