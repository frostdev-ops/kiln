// SPDX-License-Identifier: MIT
// Kiln.ActionSpeed (kiln.combat.speed, mod API 0.2.28): the kinds' numbers, per-mod values and their product, clamping,
// authority, unload, the wire.
using Kiln;
using Xunit;

namespace Kiln.Contract.Tests;

public sealed class ActionKindTests
{
    [Fact]
    public void The_kinds_keep_the_native_and_wire_numbers()
    {
        // kiln_action_speed.h KILN_ACTION_MELEE/BOW/TOOL and the kiln:speed wire carry these numbers; append only.
        Assert.Equal(0, (int)ActionKind.Melee);
        Assert.Equal(1, (int)ActionKind.Bow);
        Assert.Equal(2, (int)ActionKind.Tool);
        Assert.Equal(["Melee", "Bow", "Tool"], Enum.GetNames<ActionKind>());
    }
}

/// <summary>Kiln.ActionSpeed's state on a fake kiln.combat.speed table: each mod's values and their product, the range,
/// players only, authority, a mod's unload, a player's departure, Changed, and the kiln:speed payload.</summary>
[Collection("action speed")]
public sealed unsafe class ActionSpeedStateTests : FakeTableTest
{
    private static readonly List<(float[] Defaults, (uint Entity, float[] Rates)[] Players)> Pushed = [];
    private static uint _parts = SpeedNative.Sequence | SpeedNative.Graph | SpeedNative.Bow | SpeedNative.Wraps;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSetRates(float* defaults, SpeedNative.Rate* players, uint count)
    {
        var d = new[] { defaults[0], defaults[1], defaults[2] };
        var p = new (uint, float[])[count];
        for (var i = 0; i < count; i++) p[i] = (players[i].Entity, [players[i].Rates[0], players[i].Rates[1], players[i].Rates[2]]);
        Pushed.Add((d, p));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(SpeedNative.Status* s)
    {
        s->Parts = _parts;
        var text = "melee contact sampling is not in this Kiln release; "u8;
        for (var i = 0; i < text.Length; i++) s->Detail[i] = text[i];
        return 0;
    }

    private readonly ModContext _other;
    private bool _joined;

    public ActionSpeedStateTests()
    {
        Pushed.Clear();
        _parts = SpeedNative.Sequence | SpeedNative.Graph | SpeedNative.Bow | SpeedNative.Wraps;
        var t = Alloc<SpeedNative.Table>();
        t->Header = new ServiceHeader { Size = (uint)sizeof(SpeedNative.Table), Version = 1 };
        t->SetRates = &FakeSetRates;
        t->GetStatus = &FakeStatus;
        Serve("kiln.combat.speed", t, typeof(SpeedNative), "_t");
        ActionSpeedState.Reset();
        Swap(typeof(ActionSpeedState), nameof(ActionSpeedState.IsJoinedClient), (Func<bool>)(() => _joined));
        Swap(typeof(ActionSpeedState), nameof(ActionSpeedState.Roster), (Func<IEnumerable<uint>>)(() => [1, 2]));
        _other = HostAccess.CreateContext(new ModInfo("other", "Other", "1.0.0", "."), 78, ".");
    }

    public override void Dispose()
    {
        ActionSpeedState.Reset();
        base.Dispose();
    }

    private void As(ModContext mod) => HostAccess.Current = mod;
    private static readonly EntityId P1 = new(1), P2 = new(2);

    [Fact]
    public void Nothing_set_is_the_game_s_timing_everywhere()
    {
        foreach (var k in Enum.GetValues<ActionKind>())
            Assert.Equal(1.0, ActionSpeed.Get(P1, k));
        Assert.Empty(Pushed);  // nothing pushed, nothing hooked
    }

    [Fact]
    public void A_mod_s_default_applies_to_every_player_and_its_own_value_overrides_it()
    {
        ActionSpeed.SetDefault(ActionKind.Melee, 2.0);
        Assert.Equal(2.0, ActionSpeed.Get(P1, ActionKind.Melee));
        Assert.Equal(2.0, ActionSpeed.Get(new EntityId(900), ActionKind.Melee));  // a later join too
        Assert.Equal(1.0, ActionSpeed.Get(P1, ActionKind.Bow));
        ActionSpeed.Set(P1, ActionKind.Melee, 3.0);
        Assert.Equal(3.0, ActionSpeed.Get(P1, ActionKind.Melee));
        Assert.Equal(2.0, ActionSpeed.Get(P2, ActionKind.Melee));
        ActionSpeed.Clear(P1, ActionKind.Melee);
        Assert.Equal(2.0, ActionSpeed.Get(P1, ActionKind.Melee));  // the mod's default again
        ActionSpeed.ClearDefault(ActionKind.Melee);
        Assert.Equal(1.0, ActionSpeed.Get(P1, ActionKind.Melee));
        // the native table got each effective view, and the last one is the identity again
        var last = Pushed[^1];
        Assert.Equal([1f, 1f, 1f], last.Defaults);
        Assert.Empty(last.Players);
    }

    [Fact]
    public void Mods_multiply_each_keeping_its_own_values_and_the_product_is_clamped()
    {
        As(Mod);
        ActionSpeed.SetDefault(ActionKind.Melee, 2.0);
        ActionSpeed.Set(P1, ActionKind.Melee, 4.0);
        As(_other);
        ActionSpeed.SetDefault(ActionKind.Melee, 3.0);
        ActionSpeed.SetDefault(ActionKind.Tool, 0.5);
        Assert.Equal(6.0, ActionSpeed.Get(P2, ActionKind.Melee));     // 2 x 3
        Assert.Equal(10.0, ActionSpeed.Get(P1, ActionKind.Melee));    // 4 (writer's own for P1) x 3 = 12, clamped
        Assert.Equal(0.5, ActionSpeed.Get(P1, ActionKind.Tool));
        ActionSpeed.SetDefault(ActionKind.Tool, 0.5);
        As(Mod);
        ActionSpeed.SetDefault(ActionKind.Tool, 0.5);
        Assert.Equal(0.5, ActionSpeed.Get(P2, ActionKind.Tool));      // 0.25, clamped to Min
        // the other mod's Clear touches only its own values
        As(_other);
        ActionSpeed.Clear(P1);
        Assert.Equal(10.0, ActionSpeed.Get(P1, ActionKind.Melee));
        var last = Pushed[^1];
        Assert.Equal([6f, 1f, 0.5f], last.Defaults);
        var p1 = Assert.Single(last.Players);
        Assert.Equal((1u, new[] { 10f, 1f, 0.5f }), (p1.Entity, p1.Rates));
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(10.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    public void A_multiplier_outside_the_range_throws_and_changes_nothing(double bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionSpeed.SetDefault(ActionKind.Melee, bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionSpeed.Set(P1, ActionKind.Bow, bad));
        Assert.Equal(1.0, ActionSpeed.Get(P1, ActionKind.Melee));
        Assert.Empty(Pushed);
        ActionSpeed.SetDefault(ActionKind.Melee, ActionSpeed.Min);
        ActionSpeed.SetDefault(ActionKind.Bow, ActionSpeed.Max);
        Assert.Equal((0.5, 10.0), (ActionSpeed.Get(P1, ActionKind.Melee), ActionSpeed.Get(P1, ActionKind.Bow)));
    }

    [Fact]
    public void Players_only()
    {
        var npc = Assert.Throws<ArgumentException>(() => ActionSpeed.Set(new EntityId(5000), ActionKind.Melee, 2.0));
        Assert.Contains("not a player", npc.Message);
        Assert.Throws<ArgumentException>(() => ActionSpeed.Set(EntityId.None, ActionKind.Melee, 2.0));
        Assert.Equal(1.0, ActionSpeed.Get(new EntityId(5000), ActionKind.Melee));
        ActionSpeed.SetDefault(ActionKind.Melee, 2.0);
        Assert.Equal(1.0, ActionSpeed.Get(new EntityId(5000), ActionKind.Melee));  // a default is for players too
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionSpeed.SetDefault((ActionKind)7, 2.0));
    }

    [Fact]
    public void A_joined_client_cannot_write_and_reads_the_server_s_values()
    {
        _joined = true;
        foreach (var call in new Action[]
                 {
                     () => ActionSpeed.SetDefault(ActionKind.Melee, 2.0), () => ActionSpeed.ClearDefault(ActionKind.Melee),
                     () => ActionSpeed.Set(P1, ActionKind.Melee, 2.0), () => ActionSpeed.Clear(P1, ActionKind.Melee),
                     () => ActionSpeed.Clear(P1),
                 })
            Assert.Contains("joined client", Assert.Throws<InvalidOperationException>(call).Message);
        // the server's rates arrive over kiln:speed: Get reads them and the native table applies them here
        var seen = new List<ActionSpeedChanged>();
        ActionSpeed.Changed += seen.Add;
        ActionSpeedRelay.Receive(new ActionSpeedRelay.RatesMsg(1, [2f, 1f, 1f], [(1u, [5f, 1.5f, 1f])]));
        Assert.Equal(5.0, ActionSpeed.Get(P1, ActionKind.Melee));
        Assert.Equal(1.5, ActionSpeed.Get(P1, ActionKind.Bow));
        Assert.Equal(2.0, ActionSpeed.Get(P2, ActionKind.Melee));
        Assert.Equal(([2f, 1f, 1f], 1u), (Pushed[^1].Defaults, Pushed[^1].Players.Single().Entity));
        Assert.Equal([new(P1, ActionKind.Melee, 5.0), new(P1, ActionKind.Bow, 1.5), new(P2, ActionKind.Melee, 2.0)], seen);
        ActionSpeed.Changed -= seen.Add;
    }

    [Fact]
    public void A_mod_s_unload_takes_its_values_and_handlers_and_keeps_the_others()
    {
        As(Mod);
        ActionSpeed.SetDefault(ActionKind.Melee, 2.0);
        var writerSaw = 0;
        ActionSpeed.Changed += _ => writerSaw++;
        As(_other);
        ActionSpeed.SetDefault(ActionKind.Melee, 3.0);
        ActionSpeed.Set(P2, ActionKind.Bow, 4.0);
        var seen = new List<ActionSpeedChanged>();
        ActionSpeed.Changed += seen.Add;
        writerSaw = 0;
        HostAccess.Current = null;
        _other.Retire();
        Assert.Equal(2.0, ActionSpeed.Get(P1, ActionKind.Melee));
        Assert.Equal(1.0, ActionSpeed.Get(P2, ActionKind.Bow));
        Assert.Empty(seen);           // the unloaded mod's handler went with it
        Assert.Equal(3, writerSaw);   // P1 melee 6 -> 2, P2 melee 6 -> 2, P2 bow 4 -> 1
        Assert.Equal([2f, 1f, 1f], Pushed[^1].Defaults);
        Assert.Empty(Pushed[^1].Players);
        Mod.Retire();
        Assert.Equal(1.0, ActionSpeed.Get(P1, ActionKind.Melee));
        Assert.Equal([1f, 1f, 1f], Pushed[^1].Defaults);
    }

    [Fact]
    public void A_player_who_leaves_loses_every_mod_s_values_for_that_id()
    {
        ActionSpeed.Set(P2, ActionKind.Tool, 3.0);
        As(_other);
        ActionSpeed.Set(P2, ActionKind.Tool, 2.0);
        Assert.Equal(6.0, ActionSpeed.Get(P2, ActionKind.Tool));
        ActionSpeedState.Departed(2);
        Assert.Equal(1.0, ActionSpeed.Get(P2, ActionKind.Tool));  // a later player with id 2 starts fresh
        Assert.Empty(Pushed[^1].Players);
    }

    [Fact]
    public void Changed_fires_once_per_effective_change_and_not_for_a_repeat()
    {
        var seen = new List<ActionSpeedChanged>();
        ActionSpeed.Changed += seen.Add;
        ActionSpeed.SetDefault(ActionKind.Bow, 1.5);
        ActionSpeed.SetDefault(ActionKind.Bow, 1.5);  // the same again
        Assert.Equal([new(P1, ActionKind.Bow, 1.5), new(P2, ActionKind.Bow, 1.5)], seen);
        seen.Clear();
        ActionSpeed.Set(P1, ActionKind.Bow, 1.5);    // the effective value stays
        Assert.Empty(seen);
        ActionSpeed.Changed -= seen.Add;
        ActionSpeed.SetDefault(ActionKind.Bow, 2.0);
        Assert.Empty(seen);
    }

    [Fact]
    public void Status_names_each_part_and_why_sampling_is_off()
    {
        var s = ActionSpeed.Status;
        Assert.True(s.Sequence && s.Graph && s.BowTiming);
        Assert.False(s.MeleeSampling);
        Assert.True(ActionSpeed.Available);
        Assert.Contains("sampling is not in this Kiln release", s.Reason);
        _parts = SpeedNative.Bow | SpeedNative.Wraps;  // the sequence sites did not resolve
        Assert.False(ActionSpeed.Available);
        Assert.False(ActionSpeed.Status.Sequence);
        _parts = SpeedNative.Sequence | SpeedNative.Graph | SpeedNative.Bow;  // no kiln.world wraps: the runs cannot be told apart
        Assert.False(ActionSpeed.Available);
        Assert.False(ActionSpeed.Status.BowTiming);
    }

    [Fact]
    public void The_payload_carries_the_whole_view_byte_for_byte()
    {
        ActionSpeed.SetDefault(ActionKind.Melee, 2.0);
        ActionSpeed.Set(P2, ActionKind.Bow, 1.25);
        var m = ActionSpeedRelay.ToMsg(ActionSpeedState.Compute());
        var w = new NetWriter();
        ActionSpeedRelay.EncodeRates(w, m);
        var bytes = w.WrittenSpan.ToArray();
        // version, 3 defaults, count, then entity + 3 rates per player
        Assert.Equal(1 + 12 + 2 + (4 + 12), bytes.Length);
        Assert.Equal(1, bytes[0]);
        Assert.Equal(2f, BitConverter.ToSingle(bytes, 1));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 13));
        Assert.Equal(2u, BitConverter.ToUInt32(bytes, 15));
        Assert.Equal([2f, 1.25f, 1f], [BitConverter.ToSingle(bytes, 19), BitConverter.ToSingle(bytes, 23), BitConverter.ToSingle(bytes, 27)]);
        var r = new NetReader(bytes);
        var back = ActionSpeedRelay.FromMsg(ActionSpeedRelay.DecodeRates(ref r));
        Assert.Equal(2.0, back.Rate(2, 0));
        Assert.Equal(1.25, back.Rate(2, 1));
        Assert.Equal(2.0, back.Rate(9, 0));
    }

    [Fact]
    public void A_bad_payload_is_refused_or_cleaned()
    {
        var w = new NetWriter();
        ActionSpeedRelay.EncodeRates(w, new ActionSpeedRelay.RatesMsg(2, [1f, 1f, 1f], []));
        var bytes = w.WrittenSpan.ToArray();
        Assert.Throws<NetFormatException>(() =>
        {
            var r = new NetReader(bytes);
            ActionSpeedRelay.DecodeRates(ref r);
        });
        // out of range and non-finite rates are clamped or dropped to 1; non-player ids are dropped
        var v = ActionSpeedRelay.FromMsg(new ActionSpeedRelay.RatesMsg(1, [50f, float.NaN, 0.1f], [(5000u, [3f, 3f, 3f]), (4u, [2f, 2f, 2f])]));
        Assert.Equal([10.0, 1.0, 0.5], v.Defaults);
        Assert.Equal([4u], v.Players.Keys);
        // the item list round-trips and is bounded
        var iw = new NetWriter();
        ActionSpeedRelay.EncodeItems(iw, new ActionSpeedRelay.ItemsMsg([(42u, (byte)0), (77u, (byte)1)]));
        var ir = new NetReader(iw.WrittenSpan);
        Assert.Equal([(42u, (byte)0), (77u, (byte)1)], ActionSpeedRelay.DecodeItems(ref ir).Items);
        var big = new NetWriter().UInt32(9000);
        Assert.Throws<NetFormatException>(() =>
        {
            var br = new NetReader(big.WrittenSpan);
            ActionSpeedRelay.DecodeItems(ref br);
        });
    }
}
