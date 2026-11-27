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
            _table->MenuSay = &FakeMenuSay;
            _table->Topics = &FakeTopics;
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
        NpcSpeechEngine.PlayerOfHook = null;
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

    [UnmanagedCallersOnly]
    private static int FakeTopics(uint npc, SpeechNative.Topic* t, uint n)
    {
        var rows = new List<string>();
        var ids = new HashSet<uint>();
        for (var i = 0; i < n; i++)
        {
            // as kiln.npcspeech's valid_topics: an id 0 or 1, or one id twice, refuses the whole page
            if (t[i].Id <= 1 || !ids.Add(t[i].Id)) return 1;
            rows.Add($"{t[i].Id:x8}:{t[i].Label:x8}:{t[i].Reply:x8}:{t[i].Hint}:{t[i].Close}");
        }
        lock (Calls) Calls.Add($"topics {npc} [{string.Join(",", rows)}]");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FakeMenuSay(uint npc, uint tag)
    {
        lock (Calls) Calls.Add($"menusay {npc} {tag:x8}");
        return 0;
    }

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
    public void A_peer_without_its_player_yet_gets_its_pick_when_the_player_comes_and_never_for_no_one()
    {
        // lab run2: a ready peer without a player got a Next, the callback asked for EntityId.None
        var calls = new List<EntityId>();
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.By(ctx =>
        {
            calls.Add(ctx.Player);
            return 0;
        });
        speech.Replace("first");
        NpcSpeechEngine.Tick();
        calls.Clear();
        var player = EntityId.None;
        NpcSpeechEngine.PlayerOfHook = p => p.Id == 9 ? player : p.Player;
        var peer = new SpeechPeer(9, EntityId.None, Dialog: true);
        NpcSpeechEngine.DialogSnapshot(peer);
        NpcSpeechEngine.Tick();
        Assert.DoesNotContain(ToPeers, x => x.Msg is NextMsg);
        Assert.Empty(calls);                                              // never asked for no one
        player = P1;                                                      // its character spawned
        NpcSpeechEngine.Tick();
        Assert.Contains((9u, (object)new NextMsg(Ramone.Value, NpcLineKind.Greeting, 0)), ToPeers);
        Assert.Equal([P1], calls);

        ToPeers.Clear();
        var lab = new SpeechPeer(10, EntityId.None, Dialog: true);        // a peer that never has a player (a lab peer)
        NpcSpeechEngine.DialogSnapshot(lab);
        NpcSpeechEngine.Tick();
        _now += 120_000;
        NpcSpeechEngine.Tick();
        NpcSpeechEngine.Tick();
        Assert.DoesNotContain(ToPeers, x => x.Msg is NextMsg);
        Assert.Equal(0, NpcSpeechEngine.WaitingForPlayers);               // given up, not kept forever
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

    // ------------------------------------------------------------------------- slice 3: the menu's own line ----

    [Fact]
    public void The_menu_line_is_set_per_npc_with_its_pick_for_dev32_clients()
    {
        var m = Npcs.MenuLine(Ramone);
        Assert.Equal(NpcLineKind.MenuLine, m.Kind);
        Assert.True(m.Replace("I can't work like this. Bring me food.").Ok);
        Assert.Contains($"lines {Ramone.Value} 2 2 [{Tag("I can't work like this. Bring me food."):x8}] pick 1 seed 0", Snapshot());
        Assert.Equal(NpcLineKind.MenuLine, Assert.Single(Wire.OfType<LinesMsg>()).Kind);
        Assert.Empty(Wire.OfType<SpeechMsg>());
        Assert.False(m.Suppress().Ok);
        Assert.Throws<ArgumentException>(() => Npcs.MenuLine(EntityId.None));
    }

    [Fact]
    public void A_condition_per_player_is_a_By_pick_asked_again_at_Refresh()
    {
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        Peers.Add(new SpeechPeer(2, P2, Dialog: true));
        var fed = new HashSet<EntityId> { P1 };
        var m = Npcs.MenuLine(Hannah);
        m.Pick = SpeechPick.By(ctx => fed.Contains(ctx.Player) ? -1 : 0);   // only to players who have not fed her
        m.Replace("Have you brought bread?");
        NpcSpeechEngine.Tick();
        Assert.Contains((1u, (object)new NextMsg(Hannah.Value, NpcLineKind.MenuLine, -1)), ToPeers);
        Assert.Contains((2u, (object)new NextMsg(Hannah.Value, NpcLineKind.MenuLine, 0)), ToPeers);
        ToPeers.Clear();
        fed.Add(P2);
        m.Refresh();
        NpcSpeechEngine.Tick();
        Assert.Contains((2u, (object)new NextMsg(Hannah.Value, NpcLineKind.MenuLine, -1)), ToPeers);
    }

    [Fact]
    public void A_reply_goes_to_one_players_menu()
    {
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        Peers.Add(new SpeechPeer(2, P2, Dialog: false));               // a 31.21 client
        var r = Npcs.MenuSay(Ramone, P1, "Thank you, Flameborn!");
        Assert.True(r.Ok, r.ToString());
        Assert.Contains((1u, (object)new MenuSayMsg(Ramone.Value, SpeechText.Json(new UiText("Thank you, Flameborn!")))), ToPeers);
        var old = Npcs.MenuSay(Ramone, P2, "Thank you!");
        Assert.False(old.Ok);
        Assert.Contains("kiln:npcdialog", old.Reason);
        Assert.False(Npcs.MenuSay(Ramone, new EntityId(999), "Who?").Ok);   // not a Kiln player here

        var mine = Npcs.MenuSay(Ramone, Local, UiText.Of(("en", "Thanks!"), ("de", "Danke!")));   // the host's own player
        Assert.True(mine.Ok);
        Assert.Contains($"menusay {Ramone.Value} {SpeechText.Tag(SpeechText.Json(UiText.Of(("en", "Thanks!"), ("de", "Danke!")))):x8}", Snapshot());
        Assert.Throws<ArgumentException>(() => Npcs.MenuSay(Ramone, EntityId.None, "x"));
        Assert.Throws<ArgumentException>(() => Npcs.MenuSay(Ramone, P1, ""));
    }

    [Fact]
    public void A_joined_client_shows_a_reply_from_the_host_and_the_wire_round_trips()
    {
        _host = false;
        var text = SpeechText.Json(new UiText("Thank you!"));
        NpcSpeechEngine.Received(new MenuSayMsg(Ramone.Value, text));
        Assert.Contains($"menusay {Ramone.Value} {SpeechText.Tag(text):x8}", Snapshot());
        NpcSpeechEngine.Received(new MenuSayMsg(Ramone.Value, "not json"));   // ignored
        var w = new NetWriter();
        NpcSpeechEngine.EncodeMenuSay(new MenuSayMsg(5, text), w);
        var rd = new NetReader(w.ToArray());
        Assert.Equal(new MenuSayMsg(5, text), NpcSpeechEngine.DecodeMenuSay(ref rd));
    }

    // ----------------------------------------------------------------------------------- slice 4: talk pages ----

    private static readonly List<(string Topic, EntityId Player, EntityId Npc)> Picks = new();

    private static NpcTopic Feed() => new("feed", "Here, have some bread.")
    {
        Reply = "Thank you, Flameborn!",
        Hint = NpcTopicHint.Attention,
        OnPick = ctx => Picks.Add((ctx.Topic.Id, ctx.Player, ctx.Npc)),
    };

    [Fact]
    public void A_villager_gets_a_talk_page_of_the_mods_topics()
    {
        Picks.Clear();
        var page = Npcs.Topics(Ramone);
        NpcTopic Bye() => new("bye", "I have to go.") { Reply = "Take care.", Close = true };
        NpcTopic How() => new("how", "How are you?");               // no reply: the host answers, or nothing shows
        Assert.True(page.Set(Feed(), Bye(), How()).Ok);
        Assert.Equal(["feed", "bye", "how"], page.List.Select(t => t.Id));
        var feed = NpcSpeechEngine.TopicId("kiln", "feed");
        var bye = NpcSpeechEngine.TopicId("kiln", "bye");
        var how = NpcSpeechEngine.TopicId("kiln", "how");
        Assert.Contains($"topics {Ramone.Value} [{feed:x8}:{Tag("Here, have some bread."):x8}:{Tag("Thank you, Flameborn!"):x8}:2:0," +
                        $"{bye:x8}:{Tag("I have to go."):x8}:{Tag("Take care."):x8}:0:1," +
                        $"{how:x8}:{Tag("How are you?"):x8}:00000000:0:0]", Snapshot());
        var m = Assert.Single(Wire.OfType<TopicsMsg>());
        Assert.Equal((Ramone.Value, 3), (m.Npc, m.Topics.Length));
        Assert.Equal((feed, (byte)2, false), (m.Topics[0].Id, m.Topics[0].Hint, m.Topics[0].Close));
        Assert.True(m.Topics[1].Close);
        Assert.Equal("", m.Topics[2].Reply);
        Assert.Empty(Wire.OfType<SpeechMsg>());
        Assert.Throws<ArgumentException>(() => page.Add(new NpcTopic("x", "y") { Close = true }));   // Close needs a Reply

        Wire.Clear();
        page.Set(Feed(), Bye(), How());
        Assert.Empty(Wire);                                             // the same page again: nothing goes out
        Assert.True(page.Remove("bye").Ok);
        Assert.True(page.Remove("how").Ok);
        Assert.Contains($"topics {Ramone.Value} [{feed:x8}:{Tag("Here, have some bread."):x8}:{Tag("Thank you, Flameborn!"):x8}:2:0]", Snapshot());
        page.Clear();
        Assert.Contains($"topics {Ramone.Value} []", Snapshot());
        Assert.Empty(Wire.OfType<TopicsMsg>().Last().Topics);
    }

    [Fact]
    public void A_joined_players_pick_runs_the_handler_on_the_host_and_its_reply_goes_to_that_player()
    {
        Picks.Clear();
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        Npcs.Topics(Ramone).Set(Feed() with { OnPick = ctx => { Picks.Add((ctx.Topic.Id, ctx.Player, ctx.Npc)); ctx.Reply("You already fed me today."); } });
        var feed = NpcSpeechEngine.TopicId("kiln", "feed");
        NpcSpeechEngine.Received(new PickedMsg(Ramone.Value, feed), Peers[0]);
        Assert.Empty(Picks);                                            // mod code: on the tick
        NpcSpeechEngine.Tick();
        Assert.Equal([("feed", P1, Ramone)], Picks);
        Assert.Contains((1u, (object)new MenuSayMsg(Ramone.Value, SpeechText.Json(new UiText("You already fed me today.")))), ToPeers);

        NpcSpeechEngine.Received(new PickedMsg(Ramone.Value, 12345), Peers[0]);   // not a topic of this host
        NpcSpeechEngine.Received(new PickedMsg(Hannah.Value, feed), Peers[0]);   // not that NPC's
        NpcSpeechEngine.Tick();
        Assert.Single(Picks);
    }

    [Fact]
    public void The_hosts_own_pick_comes_from_kiln_npcspeech()
    {
        Picks.Clear();
        Npcs.Topics(Ramone).Set(Feed());
        Events.Enqueue(new(Ramone.Value, 1, 0, 0, NpcSpeechEngine.TopicId("kiln", "feed")));
        NpcSpeechEngine.Tick();
        Assert.Equal([("feed", Local, Ramone)], Picks);
        Assert.Empty(Wire.OfType<PickedMsg>());
    }

    [Fact]
    public void A_joined_client_shows_the_hosts_topics_and_sends_their_picks_but_runs_its_own()
    {
        Picks.Clear();
        _host = false;
        var label = SpeechText.Json(new UiText("Here, have some bread."));
        NpcSpeechEngine.Received(new TopicsMsg(Ramone.Value, [new TopicWire(0x7001, label, "", 2, false)]));
        Assert.Contains($"topics {Ramone.Value} [00007001:{SpeechText.Tag(label):x8}:00000000:2:0]", Snapshot());
        Events.Enqueue(new(Ramone.Value, 1, 0, 0, 0x7001));
        NpcSpeechEngine.Tick();
        Assert.Equal(new PickedMsg(Ramone.Value, 0x7001), Assert.Single(Wire.OfType<PickedMsg>()));

        Npcs.Topics(Ramone).Set(Feed());                               // this player's own client-only mod: added after
        var mine = NpcSpeechEngine.TopicId("kiln", "feed");
        Assert.Contains(Snapshot(), c => c.StartsWith($"topics {Ramone.Value} [{mine:x8}:", StringComparison.Ordinal) && c.Contains(",00007001:"));
        Events.Enqueue(new(Ramone.Value, 1, 0, 0, mine));
        NpcSpeechEngine.Tick();
        Assert.Equal([("feed", Local, Ramone)], Picks);
        Assert.Single(Wire.OfType<PickedMsg>());                         // its own pick stays here

        NpcSpeechEngine.ReceivedDialogReset();
        Assert.DoesNotContain(",00007001:", Snapshot().Last());
    }

    [Fact]
    public void Topics_of_several_mods_add_up_after_each_other_up_to_sixteen()
    {
        NpcSpeechEngine.SetTopics(Ramone, "a", [new NpcTopic("x", "A's")]);
        NpcSpeechEngine.SetTopics(Ramone, "b", Enumerable.Range(0, 16).Select(i => new NpcTopic($"t{i}", $"B's {i}")).ToArray());
        var last = Snapshot().Last(c => c.StartsWith($"topics {Ramone.Value} ", StringComparison.Ordinal));
        Assert.Equal(16, last.Split(',').Length);                        // the page holds 16; the rest waits
        Assert.StartsWith($"topics {Ramone.Value} [{NpcSpeechEngine.TopicId("a", "x"):x8}:", last);
        NpcSpeechEngine.SetTopics(Ramone, "a", []);
        last = Snapshot().Last(c => c.StartsWith($"topics {Ramone.Value} ", StringComparison.Ordinal));
        Assert.Contains($"{NpcSpeechEngine.TopicId("b", "t15"):x8}:", last);
    }

    [Fact]
    public void Topics_are_checked_and_their_ids_are_stable()
    {
        Assert.Equal(NpcSpeechEngine.TopicId("living-larder", "feed"), NpcSpeechEngine.TopicId("living-larder", "feed"));
        Assert.NotEqual(NpcSpeechEngine.TopicId("living-larder", "feed"), NpcSpeechEngine.TopicId("other", "feed"));
        Assert.True(NpcSpeechEngine.TopicId("m", "t") > 1);
        Assert.Throws<ArgumentException>(() => new NpcTopic("", "x"));
        Assert.Throws<ArgumentException>(() => new NpcTopic("x", ""));
        Assert.Throws<ArgumentException>(() => new NpcTopic("x", "y") { Reply = new string('a', 201) }.Check());
        var page = Npcs.Topics(Ramone);
        Assert.Throws<ArgumentException>(() => page.Set(Enumerable.Range(0, 17).Select(i => new NpcTopic($"t{i}", "x")).ToArray()));
        Assert.Throws<ArgumentException>(() => page.Set(new NpcTopic("a", "x"), new NpcTopic("a", "y")));
        Assert.Throws<ArgumentException>(() => Npcs.Topics(EntityId.None));
    }

    [Fact]
    public void The_topic_wire_round_trips_and_the_world_ending_clears_pages()
    {
        var m = new TopicsMsg(9, [new TopicWire(0x7001, SpeechText.Json(new UiText("a")), SpeechText.Json(new UiText("b")), 3, true)]);
        var w = new NetWriter();
        NpcSpeechEngine.EncodeTopics(m, w);
        var r = new NetReader(w.ToArray());
        var back = NpcSpeechEngine.DecodeTopics(ref r);
        Assert.Equal((m.Npc, m.Topics[0]), (back.Npc, back.Topics[0]));
        var bad = new NetWriter().UInt32(9).Byte(1).UInt32(0x7001).String("{}").String("").Byte(4).Bool(false);
        Assert.Throws<NetFormatException>(() => { var rr = new NetReader(bad.ToArray()); NpcSpeechEngine.DecodeTopics(ref rr); });

        Npcs.Topics(Ramone).Set(Feed());
        lock (Calls) Calls.Clear();
        NpcSpeechEngine.WorldEnded();
        Assert.Empty(Npcs.Topics(Ramone).List);
        Assert.Contains("clear", Snapshot());
    }

    [Fact]
    public void A_players_game_cannot_run_the_hosts_callbacks_faster_than_a_player_could()
    {
        // review: Picked and Opened are a player's game's word; a game that floods them ran OnPick and the By callback
        // once per message (up to the network's 200 messages a second)
        Picks.Clear();
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        Peers.Add(new SpeechPeer(2, P2, Dialog: true));
        Npcs.Topics(Ramone).Set(Feed());
        var feed = NpcSpeechEngine.TopicId("kiln", "feed");
        for (var i = 0; i < 100; i++) NpcSpeechEngine.Received(new PickedMsg(Ramone.Value, feed), Peers[0]);
        NpcSpeechEngine.Received(new PickedMsg(Ramone.Value, feed), Peers[1]);   // another player has a budget of their own
        NpcSpeechEngine.Tick();
        Assert.Equal(NpcSpeechEngine.PickBurst, Picks.Count(p => p.Player == P1));
        Assert.Single(Picks, p => p.Player == P2);
        _now += 1000;                                                             // a second later: a few more
        for (var i = 0; i < 100; i++) NpcSpeechEngine.Received(new PickedMsg(Ramone.Value, feed), Peers[0]);
        NpcSpeechEngine.Tick();
        Assert.Equal(NpcSpeechEngine.PickBurst + NpcSpeechEngine.PicksPerSecond, Picks.Count(p => p.Player == P1));

        var calls = 0;
        var speech = Npcs.Speech(Hannah);
        speech.Pick = SpeechPick.By(_ =>
        {
            calls++;
            return 0;
        });
        speech.Replace("first");
        NpcSpeechEngine.Tick();
        calls = 0;
        ToPeers.Clear();
        for (var i = 0; i < 100; i++) NpcSpeechEngine.Received(new OpenedMsg(Hannah.Value, NpcLineKind.Greeting, 0, (uint)i + 1), Peers[0]);
        NpcSpeechEngine.Tick();
        Assert.Equal(NpcSpeechEngine.OpenedBurst, calls);
        Assert.Equal(NpcSpeechEngine.OpenedBurst, ToPeers.Count(x => x.Peer == 1 && x.Msg is NextMsg));
    }

    [Fact]
    public void An_opened_line_the_override_does_not_have_reaches_the_callback_as_none()
    {
        // review: Opened's line is a player's game's word; it reached the callback's ctx.Last as sent (7 of 2 lines, -9)
        Peers.Add(new SpeechPeer(1, P1, Dialog: true));
        var seen = new List<NpcLineContext>();
        var speech = Npcs.Speech(Ramone);
        speech.Pick = SpeechPick.By(ctx =>
        {
            seen.Add(ctx);
            return 0;
        });
        speech.Replace("first", "second");
        NpcSpeechEngine.Tick();
        foreach (var line in new[] { 7, -9, 2 })
        {
            seen.Clear();
            NpcSpeechEngine.Received(new OpenedMsg(Ramone.Value, NpcLineKind.Greeting, line, 3), Peers[0]);
            NpcSpeechEngine.Tick();
            Assert.Equal(-1, Assert.Single(seen).Last);
        }
        seen.Clear();
        NpcSpeechEngine.Received(new OpenedMsg(Ramone.Value, NpcLineKind.Greeting, 1, 4), Peers[0]);
        NpcSpeechEngine.Tick();
        Assert.Equal((4, 1, P1), (Assert.Single(seen).Opens, seen[0].Last, seen[0].Player));
    }

    [Fact]
    public void A_peer_whose_player_comes_after_the_override_began_still_gets_its_pick()
    {
        // review: an override set (or a Refresh) while a ready peer had no character yet asked the callback for the other
        // players only; that peer's game showed a line nobody chose until it reported an open
        var player = EntityId.None;
        NpcSpeechEngine.PlayerOfHook = p => p.Id == 9 ? player : p.Player;
        Peers.Add(new SpeechPeer(9, EntityId.None, Dialog: true));
        var calls = new List<EntityId>();
        var plea = Npcs.MenuLine(Ramone);
        plea.Pick = SpeechPick.By(ctx =>
        {
            calls.Add(ctx.Player);
            return -1;
        });
        plea.Replace("Feed me first.");
        NpcSpeechEngine.Tick();
        Assert.DoesNotContain(ToPeers, x => x.Msg is NextMsg);
        player = P1;                                                     // its character spawned
        NpcSpeechEngine.Tick();
        Assert.Contains((9u, (object)new NextMsg(Ramone.Value, NpcLineKind.MenuLine, -1)), ToPeers);
        Assert.Contains(P1, calls);
        Assert.DoesNotContain(EntityId.None, calls);
        ToPeers.Clear();
        player = EntityId.None;                                          // the same at a Refresh
        plea.Refresh();
        NpcSpeechEngine.Tick();
        player = P1;
        NpcSpeechEngine.Tick();
        Assert.Contains((9u, (object)new NextMsg(Ramone.Value, NpcLineKind.MenuLine, -1)), ToPeers);
    }

    [Fact]
    public void A_mod_on_both_sides_with_the_same_topic_keeps_its_page_on_the_client()
    {
        // review: a mod on the host and on a joined client that sets the same topic on both gave that client's
        // kiln.npcspeech one line id twice, which refuses the whole page (and the note blamed npcspeech.topics)
        _host = false;
        var feed = NpcSpeechEngine.TopicId("kiln", "feed");
        NpcSpeechEngine.Received(new TopicsMsg(Ramone.Value,
        [
            new TopicWire(feed, SpeechText.Json(new UiText("Here, have some bread.")), SpeechText.Json(new UiText("Thank you, Flameborn!")), 2, false),
            new TopicWire(0x7001, SpeechText.Json(new UiText("Other")), "", 0, false),
        ]));
        var r = Npcs.Topics(Ramone).Set(Feed());
        Assert.True(r.Ok && r.Reason is null, r.ToString());
        var page = Snapshot().Last(c => c.StartsWith($"topics {Ramone.Value} ", StringComparison.Ordinal));
        Assert.Equal(2, page.Split(',').Length);                         // the topic once, then the host's other one
        Assert.StartsWith($"topics {Ramone.Value} [{feed:x8}:", page);
        Assert.Contains(",00007001:", page);
    }
}
