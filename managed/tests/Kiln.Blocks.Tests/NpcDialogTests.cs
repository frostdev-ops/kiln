// SPDX-License-Identifier: MIT
// Kiln.Npcs' dialogue parts (dev.32, Marshall/Ryrin's requests) without a game: how a Replace picks per greeting, the
// channel kiln:npcdialog beside kiln:npcspeech v1, the host's own picks per player (SpeechPick.By), farewells, the menu's
// line and talk-page topics, against a recording kiln.npcspeech table with the dev.32 members.
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.BlockTests;

[Collection("NpcSpeech")]
public sealed unsafe class NpcDialogTests : IDisposable
{
    private static long _now = 50_000;
    private static readonly List<string> Calls = new();
    private static readonly List<object> Wire = new();
    private static readonly List<(uint Peer, object Msg)> ToPeers = new();
    private static readonly Queue<KilnSpeechEventRow> Events = new();
    private static bool _host = true, _serverDialog = true;
    private static SpeechNative.Table* _table;
    private static readonly List<SpeechPeer> Peers = new();

    internal readonly record struct KilnSpeechEventRow(uint Npc, byte Type, byte Kind, int Line, uint Value);

    public NpcDialogTests()
    {
        if (_table == null)
        {
            _table = (SpeechNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(SpeechNative.Table));
            _table->HeaderSize = (uint)sizeof(SpeechNative.Table);
            _table->HeaderVersion = SpeechNative.Version;
            _table->Set = &FakeSet;
            _table->Say = &FakeSay;
            _table->Text = &FakeText;
            _table->ClearAll = &FakeClear;
            _table->GetStatus = &FakeStatus;
            _table->SetLines = &FakeSetLines;
            _table->Next = &FakeNext;
            _table->Events = &FakeEvents;
            _table->DialogStatus = &FakeDialogStatus;
        }
        lock (Calls) Calls.Clear();
        Wire.Clear();
        ToPeers.Clear();
        Events.Clear();
        Peers.Clear();
        _host = true;
        _serverDialog = true;
        NpcSpeechEngine.Clock = () => _now;
        NpcSpeechEngine.HostHook = () => _host;
        NpcSpeechEngine.SendHook = m => Wire.Add(m);
        NpcSpeechEngine.SendToHook = (p, m) => ToPeers.Add((p.Id, m));
        NpcSpeechEngine.PeersHook = () => Peers.ToArray();
        NpcSpeechEngine.ServerDialogHook = () => _serverDialog;
        NpcSpeechEngine.LocalPlayerHook = () => Local;
        SpeechNative.TableHook = () => (nint)_table;
        NpcSpeechEngine.ResetForTests();
    }

    public void Dispose()
    {
        NpcSpeechEngine.ResetForTests();
        NpcSpeechEngine.Clock = static () => Environment.TickCount64;
        NpcSpeechEngine.HostHook = null;
        NpcSpeechEngine.SendHook = null;
        NpcSpeechEngine.SendToHook = null;
        NpcSpeechEngine.PeersHook = null;
        NpcSpeechEngine.ServerDialogHook = null;
        NpcSpeechEngine.LocalPlayerHook = null;
        SpeechNative.TableHook = null;
    }

    // ---- the fake table ------------------------------------------------------------------------------------------

    private static string Tags(uint* tags, uint n)
    {
        var t = new List<string>();
        for (var i = 0; i < n; i++) t.Add(tags[i].ToString("x8"));
        return string.Join(",", t);
    }

    [UnmanagedCallersOnly]
    private static int FakeSet(uint npc, uint mode, uint* tags, uint n)
    {
        lock (Calls) Calls.Add($"set {npc} {mode} [{Tags(tags, n)}]");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeSay(uint npc, uint tag, uint ms)
    {
        lock (Calls) Calls.Add($"say {npc} {tag:x8} {ms}");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeText(uint tag, byte* json, nuint len) => 0;

    [UnmanagedCallersOnly]
    private static int FakeClear()
    {
        lock (Calls) Calls.Add("clear");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeStatus(SpeechNative.Status* s) => 0;

    [UnmanagedCallersOnly]
    private static int FakeSetLines(uint npc, uint kind, uint mode, uint* tags, uint n, uint pick, uint seed)
    {
        lock (Calls) Calls.Add($"lines {npc} {kind} {mode} [{Tags(tags, n)}] pick {pick} seed {seed}");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeNext(uint npc, uint kind, int line)
    {
        lock (Calls) Calls.Add($"next {npc} {kind} {line}");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeEvents(SpeechNative.Event* o, uint max, uint* n)
    {
        uint k = 0;
        lock (Calls)
            while (k < max && Events.TryDequeue(out var e))
                o[k++] = new SpeechNative.Event { Npc = e.Npc, Type = e.Type, Kind = e.Kind, Line = e.Line, Value = e.Value };
        *n = k;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeDialogStatus(SpeechNative.DialogStatus* s) => 0;

    private static string[] Snapshot()
    {
        lock (Calls) return Calls.ToArray();
    }

    private static uint Tag(string text) => SpeechText.Tag(SpeechText.Json(new UiText(text)));

    private static readonly EntityId Ramone = new(1159), Hannah = new(1161), Local = new(77), P1 = new(501), P2 = new(502);

    // ---------------------------------------------------------------------------------------- slice 1: picks ----

    [Fact]
    public void Replace_picks_a_new_line_at_each_greeting_by_default()
    {
        var speech = Npcs.Speech(Ramone);
        Assert.Same(SpeechPick.Random, speech.Pick);
        Assert.Equal(NpcLineKind.Greeting, speech.Kind);
        Assert.True(speech.Replace("No food, no work.", "My stomach...").Ok);
        Assert.Contains($"lines {Ramone.Value} 0 2 [{Tag("No food, no work."):x8},{Tag("My stomach..."):x8}] pick 1 seed 0", Snapshot());
        Assert.DoesNotContain(Snapshot(), c => c.StartsWith("set ", StringComparison.Ordinal));   // the dev.32 member, not set()
    }

    [Fact]
    public void In_order_and_seeded_picks_reach_the_bubble_with_their_seed()
    {
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.InOrder;
        speech.Replace("a", "b", "c");
        Assert.Contains(Snapshot(), c => c.StartsWith($"lines {Ramone.Value} 0 2 ", StringComparison.Ordinal) && c.EndsWith(" pick 2 seed 0", StringComparison.Ordinal));
        speech.Pick = SpeechPick.Seeded(1234);
        speech.Replace("a", "b", "c");
        Assert.Contains(Snapshot(), c => c.StartsWith($"lines {Ramone.Value} 0 2 ", StringComparison.Ordinal) && c.EndsWith(" pick 3 seed 1234", StringComparison.Ordinal));
        Assert.Equal("Seeded(1234)", SpeechPick.Seeded(1234).ToString());
        Assert.Throws<ArgumentNullException>(() => SpeechPick.By(null!));
    }

    [Fact]
    public void The_host_sends_v1_speech_for_old_clients_and_lines_with_their_pick_on_kiln_npcdialog()
    {
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.InOrder;
        speech.Replace("No food, no work.");
        var v1 = Assert.Single(Wire.OfType<SpeechMsg>());              // 31.16-31.21 clients: unchanged
        Assert.Equal(SpeechMode.Replace, v1.Mode);
        var v2 = Assert.Single(Wire.OfType<LinesMsg>());
        Assert.Equal((Ramone.Value, NpcLineKind.Greeting, SpeechMode.Replace, (byte)2, 0u), (v2.Npc, v2.Kind, v2.Mode, v2.Pick, v2.Seed));
        Assert.Equal([SpeechText.Json(new UiText("No food, no work."))], v2.Lines);

        Wire.Clear();
        speech.Replace("No food, no work.");
        Assert.Empty(Wire);                                             // the same again: nothing on either channel
        speech.Pick = SpeechPick.Random;
        speech.Replace("No food, no work.");
        Assert.Empty(Wire.OfType<SpeechMsg>());                         // v1 has no pick: nothing new for old clients
        Assert.Equal((byte)1, Assert.Single(Wire.OfType<LinesMsg>()).Pick);
    }

    [Fact]
    public void A_joined_client_takes_greetings_from_kiln_npcdialog_and_ignores_v1_speech_then()
    {
        _host = false;
        var line = SpeechText.Json(new UiText("Feed me"));
        NpcSpeechEngine.Received(new SpeechMsg(Ramone.Value, SpeechMode.Replace, [line]));   // the server has both channels
        Assert.Empty(Snapshot());
        NpcSpeechEngine.Received(new LinesMsg(Ramone.Value, NpcLineKind.Greeting, SpeechMode.Replace, 3, 9, [line]));
        Assert.Contains($"lines {Ramone.Value} 0 2 [{SpeechText.Tag(line):x8}] pick 3 seed 9", Snapshot());
        Assert.Equal(SpeechMode.Replace, Npcs.SpeechOf(Ramone));

        lock (Calls) Calls.Clear();
        _serverDialog = false;                                           // a 31.16-31.21 server: v1 only
        NpcSpeechEngine.Received(new SpeechMsg(Hannah.Value, SpeechMode.Replace, [line]));
        Assert.Contains($"lines {Hannah.Value} 0 2 [{SpeechText.Tag(line):x8}] pick 1 seed 0", Snapshot());   // random per greeting
    }

    [Fact]
    public void A_host_callback_picks_per_player_one_greeting_ahead()
    {
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        Peers.Add(new SpeechPeer(2, P2, Dialog: true));
        Peers.Add(new SpeechPeer(3, new EntityId(503), Dialog: false));   // a 31.21 client: no picks for it
        var seen = new List<NpcLineContext>();
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.By(ctx =>
        {
            seen.Add(ctx);
            return ctx.Player == P2 ? -1 : ctx.Opens % 2;
        });
        speech.Replace("first", "second");
        Assert.Equal((byte)4, Assert.Single(Wire.OfType<LinesMsg>()).Pick);
        Assert.Empty(ToPeers);                                          // the callback is mod code: it runs on the tick
        NpcSpeechEngine.Tick();
        Assert.Contains((1u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, 0)), ToPeers);
        Assert.Contains((2u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, -1)), ToPeers);
        Assert.DoesNotContain(ToPeers, x => x.Peer == 3);
        Assert.Contains($"next {Ramone.Value} 0 0", Snapshot());        // the host's own screen: its local player
        Assert.Contains(seen, c => c.Player == Local && c.Opens == 0 && c.Last == -1 && c.Lines == 2 && c.Kind == NpcLineKind.Greeting);

        ToPeers.Clear();
        NpcSpeechEngine.Received(new OpenedMsg(Ramone.Value, NpcLineKind.Greeting, 0, 1), Peers[0]);
        NpcSpeechEngine.Tick();
        Assert.Equal([(1u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, 1))], ToPeers);
        Assert.Contains(seen, c => c.Player == P1 && c.Opens == 1 && c.Last == 0);

        ToPeers.Clear();
        lock (Calls) Calls.Clear();
        Events.Enqueue(new(Ramone.Value, 0, 0, 0, 1));                  // the host's own bubble showed line 0
        NpcSpeechEngine.Tick();
        Assert.Contains($"next {Ramone.Value} 0 1", Snapshot());
        Assert.Empty(ToPeers);

        ToPeers.Clear();
        speech.Refresh();                                               // the mod's condition changed: ask again
        NpcSpeechEngine.Tick();
        Assert.Equal(2, ToPeers.Count);
    }

    [Fact]
    public void A_throwing_callback_shows_the_games_line_and_the_mod_keeps_running()
    {
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.By(_ => throw new InvalidOperationException("boom"));
        speech.Replace("first");
        NpcSpeechEngine.Tick();
        Assert.Contains((1u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, -1)), ToPeers);
        speech.Pick = SpeechPick.By(_ => 7);                            // out of range: the game's line too
        speech.Replace("first");
        ToPeers.Clear();
        NpcSpeechEngine.Tick();
        Assert.Contains((1u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, -1)), ToPeers);
    }

    [Fact]
    public void A_client_reports_opens_of_host_picked_lines_and_applies_the_hosts_next()
    {
        _host = false;
        var line = SpeechText.Json(new UiText("Feed me"));
        NpcSpeechEngine.Received(new LinesMsg(Ramone.Value, NpcLineKind.Greeting, SpeechMode.Replace, 4, 0, [line]));
        Assert.Contains($"lines {Ramone.Value} 0 2 [{SpeechText.Tag(line):x8}] pick 4 seed 0", Snapshot());
        NpcSpeechEngine.Received(new NextMsg(Ramone.Value, NpcLineKind.Greeting, 0));
        Assert.Contains($"next {Ramone.Value} 0 0", Snapshot());
        Events.Enqueue(new(Ramone.Value, 0, 0, 0, 3));
        NpcSpeechEngine.Tick();
        Assert.Equal(new OpenedMsg(Ramone.Value, NpcLineKind.Greeting, 0, 3), Assert.Single(Wire.OfType<OpenedMsg>()));
    }

    [Fact]
    public void A_peer_that_becomes_ready_gets_the_dialogue_snapshot_with_its_own_pick()
    {
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.By(ctx => 0);
        speech.Replace("first");
        NpcSpeechEngine.Tick();
        var late = new SpeechPeer(9, P1, Dialog: true);
        Peers.Add(late);
        NpcSpeechEngine.DialogSnapshot(late);
        var got = ToPeers.Where(x => x.Peer == 9).Select(x => x.Msg).ToArray();
        Assert.IsType<DialogResetMsg>(got[0]);
        Assert.Contains(got, m => m is LinesMsg { Kind: NpcLineKind.Greeting, Pick: 4 });
        NpcSpeechEngine.Tick();
        Assert.Contains((9u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, 0)), ToPeers);
    }

    [Fact]
    public void The_dialogue_wire_round_trips_and_refuses_what_a_client_could_not_show()
    {
        var w = new NetWriter();
        var m = new LinesMsg(42, NpcLineKind.Farewell, SpeechMode.Replace, 3, 77, [SpeechText.Json(new UiText("Bye"))]);
        NpcSpeechEngine.EncodeLines(m, w);
        var r = new NetReader(w.ToArray());
        var back = NpcSpeechEngine.DecodeLines(ref r);
        Assert.Equal((m.Npc, m.Kind, m.Mode, m.Pick, m.Seed), (back.Npc, back.Kind, back.Mode, back.Pick, back.Seed));
        Assert.Equal(m.Lines, back.Lines);
        foreach (var bad in new[]
                 {
                     new NetWriter().UInt32(1).Byte(3).Byte(2).Byte(1).UInt32(0).Byte(0),   // unknown kind
                     new NetWriter().UInt32(1).Byte(1).Byte(1).Byte(1).UInt32(0).Byte(0),   // a suppressed farewell
                     new NetWriter().UInt32(1).Byte(0).Byte(2).Byte(5).UInt32(0).Byte(0),   // unknown pick
                 })
            Assert.Throws<NetFormatException>(() => { var rr = new NetReader(bad.ToArray()); NpcSpeechEngine.DecodeLines(ref rr); });
    }

    [Fact]
    public void Unloading_or_the_world_ending_ends_the_hosts_picks()
    {
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.By(_ => 0);
        speech.Replace("first");
        NpcSpeechEngine.Tick();
        Wire.Clear();
        NpcSpeechEngine.WorldEnded();
        Assert.Contains(Wire, m => m is DialogResetMsg);
        ToPeers.Clear();
        NpcSpeechEngine.Received(new OpenedMsg(Ramone.Value, NpcLineKind.Greeting, 0, 1), Peers[0]);
        NpcSpeechEngine.Tick();
        Assert.Empty(ToPeers);                                          // nothing left to pick for
    }
    // ------------------------------------------------------------------------------------- slice 2: farewells ----

    [Fact]
    public void Farewells_are_replaced_with_their_pick_on_the_new_channel_only()
    {
        var f = Npcs.Farewells(Ramone);
        Assert.Equal(NpcLineKind.Farewell, f.Kind);
        f.Pick = SpeechPick.InOrder;
        Assert.True(f.Replace("Eat well, Flameborn!", "Bring bread next time.").Ok);
        Assert.Equal(SpeechMode.Replace, f.Mode);
        Assert.Contains($"lines {Ramone.Value} 1 2 [{Tag("Eat well, Flameborn!"):x8},{Tag("Bring bread next time."):x8}] pick 2 seed 0", Snapshot());
        var m = Assert.Single(Wire.OfType<LinesMsg>());
        Assert.Equal((NpcLineKind.Farewell, SpeechMode.Replace, (byte)2), (m.Kind, m.Mode, m.Pick));
        Assert.Empty(Wire.OfType<SpeechMsg>());                          // 31.16-31.21 clients have no farewells
        Assert.Equal(SpeechMode.Game, Npcs.SpeechOf(Ramone));            // the greeting is its own

        var no = f.Suppress();
        Assert.False(no.Ok);
        Assert.Contains("Replace", no.Reason);

        Wire.Clear();
        f.Clear();
        Assert.Contains($"lines {Ramone.Value} 1 0 [] pick 1 seed 0", Snapshot());
        Assert.Equal(SpeechMode.Game, Assert.Single(Wire.OfType<LinesMsg>()).Mode);
        Assert.Throws<ArgumentException>(() => Npcs.Farewells(EntityId.None));
    }

    [Fact]
    public void A_greeting_and_a_farewell_of_one_npc_are_two_overrides()
    {
        Npcs.Speech(Ramone).Suppress();
        Npcs.Farewells(Ramone).Replace("Bye");
        Npcs.Speech(Ramone).Clear();
        Assert.Equal(SpeechMode.Replace, Npcs.Farewells(Ramone).Mode);
        Assert.DoesNotContain(Snapshot(), c => c.StartsWith($"lines {Ramone.Value} 1 0", StringComparison.Ordinal));
        lock (Calls) Calls.Clear();
        NpcSpeechEngine.WorldEnded();
        Assert.Equal(SpeechMode.Game, Npcs.Farewells(Ramone).Mode);
        Assert.Contains("clear", Snapshot());
    }

    [Fact]
    public void A_joined_client_shows_the_hosts_farewells_and_forgets_them_on_the_dialogue_reset()
    {
        _host = false;
        var bye = SpeechText.Json(UiText.Of(("en", "Bye"), ("de", "Tschuess")));
        NpcSpeechEngine.Received(new LinesMsg(Ramone.Value, NpcLineKind.Farewell, SpeechMode.Replace, 1, 0, [bye]));
        Assert.Contains($"lines {Ramone.Value} 1 2 [{SpeechText.Tag(bye):x8}] pick 1 seed 0", Snapshot());
        NpcSpeechEngine.ReceivedReset();                                 // v1's reset: Says (and v1 greetings) only
        Assert.DoesNotContain(Snapshot(), c => c.StartsWith($"lines {Ramone.Value} 1 0", StringComparison.Ordinal));
        NpcSpeechEngine.ReceivedDialogReset();
        Assert.Contains($"lines {Ramone.Value} 1 0 [] pick 1 seed 0", Snapshot());
    }
}
