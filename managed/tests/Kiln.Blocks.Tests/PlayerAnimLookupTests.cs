// SPDX-License-Identifier: MIT
// The seam's answer from Kiln.PlayerAnim, with no native table: the mapping of the library's records and enums, what the adapter says when it cannot answer
// (no service, the capability off, the graph off or not up yet), a name resolved through it, and the capability rows of item blocks' features.
using Xunit;

namespace Kiln.BlockTests;

[Collection("player-state seam")]
public sealed class PlayerAnimLookupTests : IDisposable
{
    private const string Mod = "lookup-test";

    public void Dispose() => PlayerStateSeam.Lookup = PlayerAnimLookup.Instance;

    private static PlayerAnimLookup Lookup(bool available = true, bool capability = true, PlayerAnimGraphState graph = PlayerAnimGraphState.Merged,
        Func<string, IReadOnlyList<PlayerAnimGameState>>? game = null, Func<string, string, PlayerAnimStateInfo?>? find = null) =>
        new(() => available, () => capability, () => graph, () => "anim.player_states is unavailable: off on this game build (policy, from playeranim).",
            game ?? (_ => []), find ?? ((_, _) => null));

    private static PlayerAnimStateInfo Info(string name, PlayerAnimStateStatus status, PlayerAnimStateKind kind = PlayerAnimStateKind.State, string reason = "") =>
        new(Mod, name, PlayerAnimStates.GuidOf(Mod, name), PlayerAnimStates.IdOf(PlayerAnimStates.GuidOf(Mod, name)), kind, status, "Block_FullBody", "SM_Main", reason);

    // ---- the mapping ----

    [Theory]
    [InlineData(PlayerAnimStateStatus.Live, (int)PlayerStateStatus.Live)]
    [InlineData(PlayerAnimStateStatus.Refused, (int)PlayerStateStatus.Refused)]
    [InlineData(PlayerAnimStateStatus.Pending, (int)PlayerStateStatus.Pending)]
    [InlineData(PlayerAnimStateStatus.Unavailable, (int)PlayerStateStatus.Unavailable)]
    public void Each_status_maps_to_the_seams(PlayerAnimStateStatus from, int expected)
    {
        var to = (PlayerStateStatus)expected;
        var a = PlayerAnimLookup.Map(Info("guard", from, reason: "why"));
        Assert.Equal(to, a.Status);
        Assert.Equal("why", a.Reason);
        Assert.Equal(PlayerAnimStates.GuidOf(Mod, "guard"), a.Guid);
    }

    [Theory]
    [InlineData(PlayerAnimStateKind.State, (int)PlayerStateKind.State)]
    [InlineData(PlayerAnimStateKind.Overlay, (int)PlayerStateKind.Overlay)]
    public void Each_kind_maps_to_the_seams(PlayerAnimStateKind from, int expected)
    {
        var to = (PlayerStateKind)expected;
        Assert.Equal(to, PlayerAnimLookup.Map(from));
        Assert.Equal(to, PlayerAnimLookup.Map(Info("x", PlayerAnimStateStatus.Live, from)).Kind);
        Assert.Equal(to, PlayerAnimLookup.Map(new PlayerAnimGameState("SM", "X", from, [1])).Kind);
    }

    [Theory]
    [InlineData(new uint[0])]
    [InlineData(new uint[] { 0xeb2a5b2e })]
    [InlineData(new uint[] { 5, 6, 7, 8 })]
    public void A_game_state_keeps_its_machine_name_and_every_id(uint[] ids)
    {
        var g = PlayerAnimLookup.Map(new PlayerAnimGameState("SM_Jump_Attack_FullBody", "Jump_Attack_Start_FullBody", PlayerAnimStateKind.State, ids));
        Assert.Equal(("SM_Jump_Attack_FullBody", "Jump_Attack_Start_FullBody"), (g.Machine, g.Name));
        Assert.Equal(ids, g.Ids);
    }

    // ---- when it cannot answer ----

    [Fact]
    public void Without_the_service_a_name_is_reported_and_the_motion_keeps_its_own()
    {
        var r = MoveStates.Resolve(Mod, new AnimationRef("guard", null, "guard"), Lookup(available: false), out var problem);
        Assert.Null(r);
        Assert.Contains("the kiln.playeranim service is not loaded on this side", problem);
        Assert.Contains("keeps its own", problem);
    }

    [Fact]
    public void With_the_capability_off_the_reason_is_the_tables()
    {
        var r = MoveStates.Resolve(Mod, new AnimationRef("guard", null, "guard"), Lookup(capability: false), out var problem);
        Assert.Null(r);
        Assert.Contains("anim.player_states is unavailable: off on this game build", problem);
        // a member that throws because the capability went off: its message
        var throwing = Lookup(find: (_, _) => throw new KilnUnavailableException("anim.player_states", "the merge is off"));
        Assert.Null(MoveStates.Resolve(Mod, new AnimationRef("guard", null, "guard"), throwing, out problem));
        Assert.Contains("the merge is off", problem);
    }

    [Theory]
    [InlineData(PlayerAnimGraphState.Off, "off on this game build")]
    [InlineData(PlayerAnimGraphState.Waiting, "not up yet")]
    public void A_graph_that_is_off_or_waiting_does_not_answer(PlayerAnimGraphState graph, string says)
    {
        Assert.Contains(says, Lookup(graph: graph).NotAnswering);
        Assert.Null(Lookup(graph: PlayerAnimGraphState.Untouched).NotAnswering);   // the game's own graph: its states still answer
        Assert.Null(Lookup(graph: PlayerAnimGraphState.Merged).NotAnswering);
    }

    [Fact]
    public void A_guid_or_an_id_needs_no_lookup()
    {
        var g = Guid.NewGuid();
        Assert.Equal(g, MoveStates.Resolve(Mod, new AnimationRef(g.ToString(), g, null), Lookup(available: false), out var problem)!.Value.Guid);
        Assert.Null(problem);
    }

    // ---- a name through the adapter ----

    [Fact]
    public void A_name_resolves_through_the_library()
    {
        var lookup = Lookup(
            game: n => n == "Parry_FullBody" ? [new PlayerAnimGameState("SM_Main", "Parry_FullBody", PlayerAnimStateKind.State, [0xfeec6023])] : [],
            find: (m, n) => n switch
            {
                "glow" => Info("glow", PlayerAnimStateStatus.Live, PlayerAnimStateKind.Overlay),
                "later" => Info("later", PlayerAnimStateStatus.Pending, reason: "kept for the next start"),
                _ => null,
            });
        var own = MoveStates.Resolve(Mod, new AnimationRef("glow", null, "glow"), lookup, out var p)!.Value;
        Assert.Equal((PlayerAnimStates.GuidOf(Mod, "glow"), (PlayerStateKind?)PlayerStateKind.Overlay), (own.Guid, own.Kind));
        Assert.Null(p);
        Assert.Equal(0xfeec6023u, BlockIds.Fnv(MoveStates.Resolve(Mod, new AnimationRef("Parry_FullBody", null, "Parry_FullBody"), lookup, out p)!.Value.Guid));
        Assert.Null(MoveStates.Resolve(Mod, new AnimationRef("later", null, "later"), lookup, out p));
        Assert.Contains("pending: kept for the next start", p);
        // the seam's default is this adapter
        Assert.IsType<PlayerAnimLookup>(PlayerAnimLookup.Instance);
        PlayerStateSeam.Lookup = PlayerAnimLookup.Instance;
        Assert.Same(PlayerAnimLookup.Instance, PlayerStateSeam.Lookup);
    }

    // ---- the capability rows ----

    [Fact]
    public void The_rows_follow_what_the_process_has()
    {
        Assert.Equal(CapabilityState.Available, BlockCapabilities.MovesRow(true).State);
        var noMoves = BlockCapabilities.MovesRow(false);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing), (noMoves.State, noMoves.Reason));
        Assert.Contains("kiln.gamedata", noMoves.Text);
        Assert.Equal(CapabilityState.Available, BlockCapabilities.HeldRow(true, true).State);
        foreach (var off in new[] { BlockCapabilities.HeldRow(false, true), BlockCapabilities.HeldRow(true, false), BlockCapabilities.HeldRow(false, false) })
            Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing), (off.State, off.Reason));
        Assert.Contains("kiln.gamedata", BlockCapabilities.HeldRow(false, true).Text);
        Assert.Contains("kiln.assets", BlockCapabilities.HeldRow(true, false).Text);
        Assert.Equal(CapabilityReason.None, BlockCapabilities.HeldRow(true, true).Reason);
    }

    [Fact]
    public void The_rows_are_reported_once_by_name()
    {
        var real = BlockCapabilities.Report;
        var got = new System.Collections.Concurrent.ConcurrentBag<string>();
        try
        {
            BlockCapabilities.Report = (name, _) => got.Add(name);
            BlockCapabilities.Rearm();
            BlockCapabilities.Ensure();
            BlockCapabilities.Ensure();
            Assert.Equal(["blocks.animated_held", "blocks.move_followups", "blocks.weapon_moves"], got.Order());
        }
        finally
        {
            BlockCapabilities.Report = real;
        }
    }
}
