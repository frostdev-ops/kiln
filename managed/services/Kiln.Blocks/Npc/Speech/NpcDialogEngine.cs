// SPDX-License-Identifier: MIT
// Kiln.Npcs since 0.2.0-dev.32 (NPC dialogue for mods, Marshall/Ryrin's requests): how a Replace picks per greeting,
// farewells, the dialogue menu's own line and talk-page topics, on their own channel kiln:npcdialog v1 (required: false),
// so 31.16-31.21 clients keep kiln:npcspeech v1 exactly as they know it. docs/kiln/services/npc-speech.md.
//   1 Lines   ToClient  u32 npc, u8 kind, u8 mode, u8 pick, u32 seed, u8 n, n x string (text JSON): the effective override
//   2 Next    ToClient  u32 npc, u8 kind, i32 line: a host-picked line (SpeechPick.By) for this player's next open
//   3 Topics  ToClient  u32 npc, u8 n, n x {u32 id, string label, string reply ("" none), u8 hint, bool close}: the host's page
//   4 MenuSay ToClient  u32 npc, string (text JSON): a host's reply to this player in that NPC's menu (Npcs.MenuSay)
//   5 Reset   ToClient  bool: forget everything this channel sent (the snapshot's first message)
//   6 Opened  ToServer  u32 npc, u8 kind, i32 line, u32 opens: a host-picked line showed on this player's screen
//   7 Picked  ToServer  u32 npc, u32 id: this player picked one of the host's topics
// A dev.32 client whose server has this channel takes greetings from Lines (with their pick) and ignores v1 Speech.
// SpeechPick.By runs where its override lives (the host for the host's mods, a client for its own mods) on the tick
// thread, one greeting ahead: for every Kiln player when the override begins or Refresh() asks, then after each open.
using System.Collections.Concurrent;

namespace Kiln;

internal sealed record LinesMsg(uint Npc, NpcLineKind Kind, SpeechMode Mode, byte Pick, uint Seed, string[] Lines);
internal sealed record NextMsg(uint Npc, NpcLineKind Kind, int Line);
internal sealed record OpenedMsg(uint Npc, NpcLineKind Kind, int Line, uint Opens);
internal sealed record DialogResetMsg(bool All);
internal sealed record MenuSayMsg(uint Npc, string Text);
internal sealed record TopicWire(uint Id, string Label, string Reply, byte Hint, bool Close);
internal sealed record TopicsMsg(uint Npc, TopicWire[] Topics);
internal sealed record PickedMsg(uint Npc, uint Id);

/// <summary>A Kiln client as the host sees it: its peer id, its player, whether it has kiln:npcdialog (Local: this
/// process's own player, whose picks go to kiln.npcspeech here).</summary>
internal sealed record SpeechPeer(uint Id, EntityId Player, bool Dialog, object? Net = null, bool Local = false);

internal static partial class NpcSpeechEngine
{
    public const string DialogChannel = "kiln:npcdialog";
    public const string DialogVersion = "1";
    public const ushort MsgLines = 1, MsgNext = 2, MsgTopics = 3, MsgMenuSay = 4, MsgDialogReset = 5, MsgOpened = 6, MsgPicked = 7;

    /// <summary>Test seams: per-peer sends, the host's Kiln clients, whether the server has kiln:npcdialog, the local player.</summary>
    internal static Action<SpeechPeer, object>? SendToHook;
    internal static Func<IReadOnlyList<SpeechPeer>>? PeersHook;
    internal static Func<bool>? ServerDialogHook;
    internal static Func<EntityId>? LocalPlayerHook;
    /// <summary>Test seam: a peer's player now (production: the NetPeer's, which the network fills in once the
    /// client's character exists).</summary>
    internal static Func<SpeechPeer, EntityId>? PlayerOfHook;
    /// <summary>Kiln.TestKit: where Npcs.MenuSay goes in a kit (the kit is the host and has no players' games).</summary>
    internal static Action<uint, EntityId, UiText>? ReplyHook;

    private static NetChannel? _dialog;
    private static IDisposable? _tick;
    // what this process's menu and bubbles were told per (npc, kind) other than greetings (those are in Applied)
    private static readonly Dictionary<(uint, NpcLineKind), SpeechEffect> AppliedLines = new();
    // what the Kiln clients were sent on kiln:npcdialog per (npc, kind) (host)
    private static readonly Dictionary<(uint, NpcLineKind), SpeechEffect> SentLines = new();
    // the By overrides this process decides, and each player's opens of them
    private static readonly Dictionary<(uint, NpcLineKind), SpeechEffect> Deciding = new();
    private static readonly Dictionary<(uint, NpcLineKind, uint), (int Opens, int Last)> ByState = new();
    private static readonly ConcurrentQueue<(uint Npc, NpcLineKind Kind, SpeechPeer? To)> ByWork = new();
    // host picks for a ready client whose character does not exist yet: asked once it does, given up after WaitMs
    private static readonly List<(uint Npc, NpcLineKind Kind, SpeechPeer To, long Until)> Waiting = new();
    private const long WaitMs = 60_000;
    private static readonly SpeechPeer LocalTarget = new(0, EntityId.None, false, null, Local: true);
    // talk pages: what this process's menu and the clients were told per NPC, and the picks due on the tick
    private static readonly Dictionary<uint, TopicWire[]> AppliedTopics = new();
    private static readonly Dictionary<uint, TopicWire[]> SentTopics = new();
    private static readonly ConcurrentQueue<(uint Npc, uint Id, EntityId Player)> PickWork = new();
    private static int _byWarned;
    // What a player's game reports (Opened, Picked) is its word: each player has a budget a real game stays far under
    // (a greeting needs 1 s hidden, a topic's reply box is dismissed before the next pick), and the rest is dropped, so a
    // modified game cannot run a mod's By callback or OnPick once per message (the network allows 200 a second).
    internal const int OpenedPerSecond = 10, OpenedBurst = 20, PicksPerSecond = 4, PickBurst = 8;
    private static readonly Dictionary<(ulong Player, bool Pick), Budget> Budgets = new();
    private static int _floodWarned;

    private sealed class Budget(int perSecond, int burst, long now)
    {
        private double _tokens = burst;
        private long _at = now;

        public bool Take(long now)
        {
            if (now > _at)
            {
                _tokens = Math.Min(burst, _tokens + (now - _at) * perSecond / 1000.0);
                _at = now;
            }
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }

    /// <summary>Host: one more Opened (or Picked) of this player's game fits its budget; else it is dropped (logged).</summary>
    private static bool Within(EntityId player, bool pick)
    {
        bool ok;
        lock (Gate)
        {
            var now = Clock();
            if (!Budgets.TryGetValue((player.Value, pick), out var b))
                Budgets[(player.Value, pick)] = b = pick ? new Budget(PicksPerSecond, PickBurst, now) : new Budget(OpenedPerSecond, OpenedBurst, now);
            ok = b.Take(now);
        }
        if (!ok && Interlocked.Increment(ref _floodWarned) <= 5)
            ModLog.Warn($"kiln.npcspeech: player {player}'s game reports {(pick ? "topic picks" : "dialogue opens")} faster than a player can " +
                        $"({(pick ? PicksPerSecond : OpenedPerSecond)} a second): the rest are dropped");
        return ok;
    }

    internal static bool ServerHasDialog
    {
        get
        {
            if (ServerDialogHook is { } hook) return hook();
            try { return Network.Server is { } s && s.HasChannel(DialogChannel); }
            catch (Exception) { return false; }
        }
    }

    private static EntityId LocalPlayer()
    {
        if (LocalPlayerHook is { } hook) return hook();
        try { return Client.HasLocalPlayer ? Client.LocalPlayer : EntityId.None; }
        catch (Exception) { return EntityId.None; }
    }

    /// <summary>The host's ready Kiln clients (Dialog: they have kiln:npcdialog).</summary>
    private static IReadOnlyList<SpeechPeer> DialogPeers()
    {
        if (PeersHook is { } hook) return hook();
        if (!IsHost) return [];
        try
        {
            return Network.Peers.Where(p => p.State == NetPeerState.Ready && (p.HasChannel(DialogChannel) || p.HasChannel(ChannelName)))
                .Select(PeerOf).ToArray();
        }
        catch (Exception) { return []; }
    }

    internal static SpeechPeer PeerOf(NetPeer p) => new(p.Id, p.Player, p.HasChannel(DialogChannel), p);

    /// <summary>The peer's player now (EntityId.None until its character exists, and for a lab peer).</summary>
    private static EntityId PlayerOf(SpeechPeer p) => PlayerOfHook?.Invoke(p) ?? (p.Net is NetPeer np ? np.Player : p.Player);

    /// <summary>Tests: host picks waiting for a client's character.</summary>
    internal static int WaitingForPlayers
    {
        get { lock (Gate) return Waiting.Count; }
    }

    private static NetChannel? RegisterDialog() =>
        Network.Channel(DialogChannel, DialogVersion, required: false, maxMessageBytes: 96 * 1024)
            .Register<LinesMsg>(MsgLines, EncodeLines, DecodeLines, static (m, _) => Received(m), NetDirection.ToClient)
            .Register<NextMsg>(MsgNext, static (m, w) => w.UInt32(m.Npc).Byte((byte)m.Kind).Int32(m.Line),
                static (ref NetReader r) => new NextMsg(r.UInt32(), Kind(r.Byte()), r.Int32()), static (m, _) => Received(m), NetDirection.ToClient)
            .Register<TopicsMsg>(MsgTopics, EncodeTopics, DecodeTopics, static (m, _) => Received(m), NetDirection.ToClient)
            .Register<MenuSayMsg>(MsgMenuSay, EncodeMenuSay, DecodeMenuSay, static (m, _) => Received(m), NetDirection.ToClient)
            .Register<PickedMsg>(MsgPicked, static (m, w) => w.UInt32(m.Npc).UInt32(m.Id), static (ref NetReader r) => new PickedMsg(r.UInt32(), r.UInt32()),
                static (m, ctx) => Received(m, PeerOf(ctx.Sender)), NetDirection.ToServer)
            .Register<DialogResetMsg>(MsgDialogReset, static (m, w) => w.Bool(m.All), static (ref NetReader r) => new DialogResetMsg(r.Bool()),
                static (_, _) => ReceivedDialogReset(), NetDirection.ToClient)
            .Register<OpenedMsg>(MsgOpened, static (m, w) => w.UInt32(m.Npc).Byte((byte)m.Kind).Int32(m.Line).UInt32(m.Opens),
                static (ref NetReader r) => new OpenedMsg(r.UInt32(), Kind(r.Byte()), r.Int32(), r.UInt32()),
                static (m, ctx) => Received(m, PeerOf(ctx.Sender)), NetDirection.ToServer);

    private static NpcLineKind Kind(byte b) => b <= (byte)NpcLineKind.MenuLine ? (NpcLineKind)b : throw new NetFormatException($"unknown line kind {b}");

    private static void StartTick()
    {
        lock (Gate)
        {
            if (_tick is not null) return;
            try { _tick = BlocksBridge.AddTick("kiln npc speech", _ => Tick()); }
            catch (Exception) { _tick = null; }
        }
    }

    private static int SendDialog(object m)
    {
        if (_dialog is null) return 0;
        return m switch
        {
            LinesMsg s => _dialog.SendToAll(s),
            TopicsMsg s => _dialog.SendToAll(s),
            DialogResetMsg s => _dialog.SendToAll(s),
            OpenedMsg s => _dialog.SendToServer(s) ? 1 : 0,
            PickedMsg s => _dialog.SendToServer(s) ? 1 : 0,
            _ => 0,
        };
    }

    private static void SendToPeer(SpeechPeer peer, object m)
    {
        if (SendToHook is { } hook) { hook(peer, m); return; }
        if (_dialog is null || peer.Net is not NetPeer np) return;
        try
        {
            _ = m switch
            {
                LinesMsg s => _dialog.SendTo(np, s),
                NextMsg s => _dialog.SendTo(np, s),
                MenuSayMsg s => _dialog.SendTo(np, s),
                TopicsMsg s => _dialog.SendTo(np, s),
                DialogResetMsg s => _dialog.SendTo(np, s),
                _ => false,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or KilnUnavailableException or ArgumentException)
        {
            if (Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: could not send to {np}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------- lines of a kind ----

    /// <summary>Every kind of the NPC (an owner went, a layer was dropped).</summary>
    private static void ChangedAll(uint npc)
    {
        Changed(npc);
        ChangedLines(npc, NpcLineKind.Farewell);
        ChangedLines(npc, NpcLineKind.MenuLine);
        ChangedTopics(npc);
    }

    /// <summary>The NPC's farewell or menu line changed (maybe): this process's menu follows, and as the host the clients.</summary>
    internal static SpeechResult ChangedLines(uint npc, NpcLineKind kind)
    {
        lock (Order)
        {
            var eff = Book.Effective(npc, kind, Clock());
            var note = ApplyLines(npc, kind, eff);
            if (IsHost) BroadcastLines(npc, kind, eff);
            NotePick(npc, kind, eff);
            if (!IsHost && !SpeechNative.Loaded) return SpeechResult.No(note ?? "kiln.npcspeech is not here: this game shows no Kiln speech");
            return note is null ? SpeechResult.Yes : new SpeechResult(true, note);
        }
    }

    private static string? ApplyLines(uint npc, NpcLineKind kind, SpeechEffect eff)
    {
        var key = (npc, kind);
        lock (Gate)
            if (AppliedLines.TryGetValue(key, out var was) ? was.SameLines(eff) && was.Pick.SameAs(eff.Pick) : eff.Mode == SpeechMode.Game)
                return null;
        if (!SpeechNative.Loaded) return null;  // a dedicated server: its clients show it
        string? note = null;
        var tags = new List<uint>();
        var mode = eff.Mode;
        if (mode == SpeechMode.Replace)
            foreach (var line in eff.Lines)
            {
                if (Register(line, ref note) is not { } tag)
                {
                    tags.Clear();
                    mode = SpeechMode.Game;
                    break;
                }
                tags.Add(tag);
            }
        var rc = SpeechNative.SetLines(npc, kind, mode, tags.ToArray(), eff.Pick.Code, eff.Pick.Seed);
        var whole = mode == eff.Mode && rc == SpeechNative.Ok;
        if (rc != SpeechNative.Ok)
            note ??= Capabilities.Why(kind == NpcLineKind.Farewell ? SpeechNative.FarewellCapability : SpeechNative.MenuLineCapability);
        lock (Gate)
        {
            if (whole && eff.Mode != SpeechMode.Game) AppliedLines[key] = eff;
            else AppliedLines.Remove(key);
        }
        if (note is not null && Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: {note}");
        return note;
    }

    /// <summary>Host: the NPC's effective line of <paramref name="kind"/> to every client with kiln:npcdialog.</summary>
    private static void BroadcastLines(uint npc, NpcLineKind kind, SpeechEffect eff)
    {
        var key = (npc, kind);
        lock (Gate)
        {
            var was = SentLines.TryGetValue(key, out var w) ? w : SpeechEffect.None;
            // what goes on the wire: mode, lines, the pick's kind and seed (another By callback is the same message)
            if (was.SameLines(eff) && (eff.Mode == SpeechMode.Game || (was.Pick.Code == eff.Pick.Code && was.Pick.Seed == eff.Pick.Seed))) return;
            if (eff.Mode == SpeechMode.Game) SentLines.Remove(key);
            else SentLines[key] = eff;
        }
        Send(LinesOf(npc, kind, eff));
    }

    private static LinesMsg LinesOf(uint npc, NpcLineKind kind, SpeechEffect eff) =>
        new(npc, kind, eff.Mode, eff.Mode == SpeechMode.Game ? (byte)0 : eff.Pick.Code, eff.Mode == SpeechMode.Game ? 0 : eff.Pick.Seed,
            eff.Lines.Select(SpeechText.Json).ToArray());

    // ---------------------------------------------------------------------------------------- the host's picks ----

    /// <summary>A By override this process decides began, changed or ended: ask its callback for every player.</summary>
    private static void NotePick(uint npc, NpcLineKind kind, SpeechEffect eff)
    {
        var key = (npc, kind);
        var decide = eff.Mode == SpeechMode.Replace && eff.Pick.Choose is not null && eff.Layer == SpeechLayer.Local;
        lock (Gate)
        {
            if (!decide)
            {
                if (Deciding.Remove(key)) DropState(key);
                return;
            }
            if (Deciding.TryGetValue(key, out var was) && was.SameLines(eff) && was.Pick.SameAs(eff.Pick)) return;
            Deciding[key] = eff;
            DropState(key);  // a new override: each player's opens count from 0
        }
        ByWork.Enqueue((npc, kind, null));
    }

    private static void DropState((uint, NpcLineKind) key)
    {
        foreach (var k in ByState.Keys.Where(k => k.Item1 == key.Item1 && k.Item2 == key.Item2).ToArray()) ByState.Remove(k);
    }

    internal static void Refresh(uint npc, NpcLineKind kind)
    {
        lock (Gate)
            if (!Deciding.ContainsKey((npc, kind))) return;
        ByWork.Enqueue((npc, kind, null));
    }

    /// <summary>Kiln's service tick: what the game reported, then the callbacks that are due.</summary>
    internal static void Tick()
    {
        if (SpeechNative.Loaded)
            foreach (var e in SpeechNative.DrainEvents())
            {
                if (e.Type == SpeechNative.EventOpen && e.Kind <= (byte)NpcLineKind.MenuLine) Opened(e.Npc, (NpcLineKind)e.Kind, e.Line, e.Value);
                else if (e.Type == SpeechNative.EventTopic) TopicPicked(e.Npc, e.Value);
            }
        List<(uint Npc, NpcLineKind Kind, SpeechPeer To, long Until)> due;
        lock (Gate)
        {
            var now = Clock();
            due = Waiting.Where(x => !PlayerOf(x.To).IsNone).ToList();
            Waiting.RemoveAll(x => due.Contains(x) || now > x.Until);
        }
        foreach (var d in due) ByWork.Enqueue((d.Npc, d.Kind, d.To));
        while (ByWork.TryDequeue(out var w)) Decide(w.Npc, w.Kind, w.To);
        while (PickWork.TryDequeue(out var p)) RunPick(p.Npc, p.Id, p.Player);
    }

    /// <summary>This game showed a host-picked line: decide the next one here, or tell the host.</summary>
    private static void Opened(uint npc, NpcLineKind kind, int line, uint opens)
    {
        bool mine;
        lock (Gate) mine = Deciding.ContainsKey((npc, kind));
        if (mine)
        {
            var player = LocalPlayer();
            lock (Gate) ByState[(npc, kind, player.Value)] = ((int)Math.Min(opens, int.MaxValue), line);
            ByWork.Enqueue((npc, kind, LocalTarget));
            return;
        }
        if (!IsHost && Book.Effective(npc, kind, Clock()) is { Mode: SpeechMode.Replace, Layer: SpeechLayer.Remote } eff &&
            eff.Pick.Code == SpeechNative.PickHost)
            Send(new OpenedMsg(npc, kind, line, opens));
    }

    /// <summary>Host: a client showed a host-picked line.</summary>
    internal static void Received(OpenedMsg m, SpeechPeer from)
    {
        if (!IsHost || from.Player.IsNone) return;
        lock (Gate)
            if (!Deciding.ContainsKey((m.Npc, m.Kind))) return;
        if (!Within(from.Player, pick: false)) return;
        lock (Gate)
        {
            if (!Deciding.ContainsKey((m.Npc, m.Kind))) return;
            ByState[(m.Npc, m.Kind, from.Player.Value)] = ((int)Math.Min(m.Opens, int.MaxValue), m.Line);
        }
        ByWork.Enqueue((m.Npc, m.Kind, from));
    }

    /// <summary>Runs the callback of a By override for one player (<paramref name="to"/>), or every player (null).</summary>
    private static void Decide(uint npc, NpcLineKind kind, SpeechPeer? to)
    {
        SpeechEffect eff;
        lock (Gate)
            if (!Deciding.TryGetValue((npc, kind), out eff!)) return;
        if (eff.Pick.Choose is not { } choose) return;
        var targets = new List<SpeechPeer>();
        if (to is null || to.Local)
            if (SpeechNative.Loaded && LocalPlayer() is { IsNone: false } local) targets.Add(LocalTarget with { Player = local });
        if (to is null) targets.AddRange(DialogPeers().Where(p => p.Dialog).Select(p => p with { Player = PlayerOf(p) }).Where(p => !p.Player.IsNone));
        else if (!to.Local)
        {
            var player = PlayerOf(to);
            if (player.IsNone)
            {
                // a client that became ready before its character exists: ask once it does (never for no one)
                lock (Gate)
                    if (!Waiting.Any(x => x.Npc == npc && x.Kind == kind && x.To.Id == to.Id)) Waiting.Add((npc, kind, to, Clock() + WaitMs));
            }
            else targets.Add(to with { Player = player });
        }
        foreach (var t in targets)
        {
            (int Opens, int Last) st;
            lock (Gate) st = ByState.TryGetValue((npc, kind, t.Player.Value), out var s) ? s : (0, -1);
            var ctx = new NpcLineContext(new EntityId(npc), t.Player, kind, st.Opens, st.Last, eff.Lines.Count);
            var line = Choose(choose, ctx);
            if (t.Local) SpeechNative.Next(npc, kind, line);
            else SendToPeer(t, new NextMsg(npc, kind, line));
        }
    }

    private static int Choose(Func<NpcLineContext, int> choose, NpcLineContext ctx)
    {
        var outer = ModContext.CurrentMod;
        try
        {
            ModContext.CurrentMod = ModContext.OwnerOf(choose);
            var i = choose(ctx);
            return i >= -1 && i < ctx.Lines ? i : -1;
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref _byWarned) <= 5)
                ModLog.Warn($"kiln.npcspeech: a SpeechPick.By callback for {ctx.Npc} ({ctx.Kind}, player {ctx.Player}) threw; the game's line shows: {e.Message}");
            return -1;
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // ---------------------------------------------------------------------------------------- replies ----

    /// <summary>A reply in one player's dialogue menu with the NPC: this game's own player through kiln.npcspeech, a
    /// joined player's Kiln (with kiln:npcdialog) through a MenuSay message.</summary>
    internal static SpeechResult MenuSay(EntityId npc, EntityId player, UiText text)
    {
        Start();
        if (ReplyHook is { } hook)
        {
            hook(npc.Value, player, text);
            return SpeechResult.Yes;
        }
        if (SpeechNative.Loaded && LocalPlayer() is { IsNone: false } local && local == player)
        {
            string? note = null;
            if (Register(text, ref note) is not { } tag) return new SpeechResult(true, note);
            return SpeechNative.MenuSay(npc.Value, tag) == SpeechNative.Ok
                ? SpeechResult.Yes
                : SpeechResult.No(Capabilities.Why(SpeechNative.MenuLineCapability) ?? "npcspeech.menu_line is off here");
        }
        if (!IsHost) return SpeechResult.No($"{player} is not this game's player: a joined client's reply shows on its own screen only");
        var peer = DialogPeers().FirstOrDefault(p => p.Player == player);
        if (peer is null) return SpeechResult.No($"{player} has no Kiln client on this server: the game's menu stays as it is");
        if (!peer.Dialog)
            return SpeechResult.No($"{player}'s Kiln has no {DialogChannel} (0.2.0-dev.31.21 or older): the game's menu stays as it is");
        SendToPeer(peer, new MenuSayMsg(npc.Value, SpeechText.Json(text)));
        return SpeechResult.Yes;
    }

    internal static void Received(MenuSayMsg m)
    {
        if (IsHost) return;
        UiText text;
        try { text = SpeechText.FromJson(m.Text); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { return; }
        string? note = null;
        if (Register(text, ref note) is { } tag) SpeechNative.MenuSay(m.Npc, tag);
        else if (Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: {note}");
    }

    internal static void EncodeMenuSay(MenuSayMsg m, NetWriter w) => w.UInt32(m.Npc).String(m.Text);

    internal static MenuSayMsg DecodeMenuSay(ref NetReader r) => new(r.UInt32(), r.String(MaxLineBytes));

    // ---------------------------------------------------------------------------------------- talk pages ----

    /// <summary>The line id a topic shows with on Kiln clients: FNV-1a-32 of "kiln.npctopic", the mod and the topic's id
    /// (never 0 or 1, the game's farewell and missing-requirement ids).</summary>
    internal static uint TopicId(string owner, string id)
    {
        var h = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("kiln.npctopic\n" + owner + "\n" + id))
        {
            h ^= b;
            h *= 16777619u;
        }
        return h <= 1 ? h + 2 : h;
    }

    internal static SpeechResult SetTopics(EntityId npc, string owner, NpcTopic[] topics)
    {
        if (npc.IsNone) throw new ArgumentException("no NPC (EntityId.None)", nameof(npc));
        if (topics.Length > NpcTopics.MaxTopics)
            throw new ArgumentException($"an NPC's page holds {NpcTopics.MaxTopics} topics, not {topics.Length}", nameof(topics));
        foreach (var t in topics) (t ?? throw new ArgumentNullException(nameof(topics))).Check();
        if (topics.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != topics.Length)
            throw new ArgumentException("two topics with one id", nameof(topics));
        Start();
        Book.SetTopics(SpeechLayer.Local, npc.Value, owner, topics.Select(t => (t, TopicId(owner, t.Id))).ToArray());
        return ChangedTopics(npc.Value);
    }

    private static TopicWire[] WireOf(IEnumerable<SpeechBook.TopicEntry> page) =>
        page.Select(e => new TopicWire(e.Wire, SpeechText.Json(e.Topic.Label), e.Topic.Reply is { } r ? SpeechText.Json(r) : "", (byte)e.Topic.Hint,
            e.Topic.Close)).ToArray();

    /// <summary>The NPC's page changed (maybe): this process's menu follows, and as the host the clients.</summary>
    internal static SpeechResult ChangedTopics(uint npc)
    {
        lock (Order)
        {
            var note = ApplyTopics(npc);
            if (IsHost) BroadcastTopics(npc);
            if (!IsHost && !SpeechNative.Loaded) return SpeechResult.No(note ?? "kiln.npcspeech is not here: this game shows no Kiln speech");
            return note is null ? SpeechResult.Yes : new SpeechResult(true, note);
        }
    }

    private static string? ApplyTopics(uint npc)
    {
        var page = WireOf(Book.EffectiveTopics(npc));
        lock (Gate)
            if (AppliedTopics.TryGetValue(npc, out var was) ? was.SequenceEqual(page) : page.Length == 0) return null;
        if (!SpeechNative.Loaded) return null;
        string? note = null;
        var rows = new List<SpeechNative.Topic>();
        foreach (var t in page)
        {
            if (Register(SpeechText.FromJson(t.Label), ref note) is not { } label) continue;
            uint reply = 0;
            if (t.Reply.Length > 0 && Register(SpeechText.FromJson(t.Reply), ref note) is { } r) reply = r;
            if (t.Reply.Length > 0 && reply == 0) continue;  // a reply that cannot show: leave the row out, not an empty box
            rows.Add(new SpeechNative.Topic { Id = t.Id, Label = label, Reply = reply, Hint = t.Hint, Close = (byte)(t.Close ? 1 : 0) });
        }
        var rc = SpeechNative.Topics(npc, rows.ToArray());
        if (rc != SpeechNative.Ok) note ??= Capabilities.Why(SpeechNative.TopicsCapability) ?? "npcspeech.topics is off here";
        lock (Gate)
        {
            if (rc == SpeechNative.Ok && note is null && page.Length > 0) AppliedTopics[npc] = page;
            else if (rc == SpeechNative.Ok && page.Length == 0) AppliedTopics.Remove(npc);
            else AppliedTopics.Remove(npc);
        }
        if (note is not null && Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: {note}");
        return note;
    }

    private static void BroadcastTopics(uint npc)
    {
        var page = WireOf(Book.EffectiveTopics(npc, SpeechLayer.Local));
        lock (Gate)
        {
            if (SentTopics.TryGetValue(npc, out var was) ? was.SequenceEqual(page) : page.Length == 0) return;
            if (page.Length == 0) SentTopics.Remove(npc);
            else SentTopics[npc] = page;
        }
        Send(new TopicsMsg(npc, page));
    }

    /// <summary>This game's menu reported a pick of a Kiln topic: run it here (its own topic), or tell the host.</summary>
    private static void TopicPicked(uint npc, uint id)
    {
        var e = Book.EffectiveTopics(npc).FirstOrDefault(t => t.Wire == id);
        if (e is null) return;
        if (e.Layer == SpeechLayer.Local)
        {
            if (LocalPlayer() is { IsNone: false } player) PickWork.Enqueue((npc, id, player));
        }
        else if (!IsHost) Send(new PickedMsg(npc, id));
    }

    /// <summary>Host: a client picked one of the host's topics.</summary>
    internal static void Received(PickedMsg m, SpeechPeer from)
    {
        if (!IsHost || from.Player.IsNone) return;
        if (Book.EffectiveTopics(m.Npc, SpeechLayer.Local).Any(t => t.Wire == m.Id) && Within(from.Player, pick: true))
            PickWork.Enqueue((m.Npc, m.Id, from.Player));
    }

    /// <summary>Runs a pick's OnPick on the tick thread, as the mod that set the topic.</summary>
    private static void RunPick(uint npc, uint id, EntityId player)
    {
        var e = Book.EffectiveTopics(npc, SpeechLayer.Local).FirstOrDefault(t => t.Wire == id);
        if (e?.Topic.OnPick is not { } onPick) return;
        var outer = ModContext.CurrentMod;
        try
        {
            ModContext.CurrentMod = ModContext.OwnerOf(onPick);
            onPick(new NpcTopicContext(new EntityId(npc), player, e.Topic, e.Owner));
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _byWarned) <= 5)
                ModLog.Warn($"kiln.npcspeech: topic {e.Owner}/{e.Topic.Id} of {npc} threw in OnPick (player {player}): {ex.Message}");
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    /// <summary>Kiln.TestKit: a pick of the topic <paramref name="topicId"/> (any mod's) by a player, run now.</summary>
    internal static bool PickLocal(EntityId npc, string topicId, EntityId player)
    {
        var e = Book.EffectiveTopics(npc.Value, SpeechLayer.Local).FirstOrDefault(t => t.Topic.Id == topicId);
        if (e is null) return false;
        RunPick(npc.Value, e.Wire, player);
        return true;
    }

    internal static void Received(TopicsMsg m)
    {
        if (IsHost) return;
        var list = new List<(NpcTopic, uint)>();
        try
        {
            foreach (var t in m.Topics)
                list.Add((new NpcTopic($"server:{t.Id:x8}", SpeechText.FromJson(t.Label))
                {
                    Reply = t.Reply.Length > 0 ? SpeechText.FromJson(t.Reply) : null,
                    Hint = (NpcTopicHint)t.Hint,
                    Close = t.Close,
                }, t.Id));
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or ArgumentException) { return; }
        Book.SetTopics(SpeechLayer.Remote, m.Npc, "server", list);
        ChangedTopics(m.Npc);
    }

    internal static void EncodeTopics(TopicsMsg m, NetWriter w)
    {
        if (m.Topics.Length > NpcTopics.MaxTopics) throw new NetFormatException("too many topics");
        w.UInt32(m.Npc).Byte((byte)m.Topics.Length);
        foreach (var t in m.Topics) w.UInt32(t.Id).String(t.Label).String(t.Reply).Byte(t.Hint).Bool(t.Close);
    }

    internal static TopicsMsg DecodeTopics(ref NetReader r)
    {
        var npc = r.UInt32();
        var n = r.Byte();
        if (n > NpcTopics.MaxTopics) throw new NetFormatException($"{n} topics");
        var topics = new TopicWire[n];
        for (var i = 0; i < n; i++)
        {
            var id = r.UInt32();
            var label = r.String(MaxLineBytes);
            var reply = r.String(MaxLineBytes);
            var hint = r.Byte();
            if (hint > (byte)NpcTopicHint.MainQuest) throw new NetFormatException($"topic hint {hint}");
            topics[i] = new TopicWire(id, label, reply, hint, r.Bool());
        }
        return new TopicsMsg(npc, topics);
    }

    // ---------------------------------------------------------------------------------------- client side ----

    internal static void Received(LinesMsg m)
    {
        if (IsHost) return;
        UiText[] lines;
        SpeechPick pick;
        try
        {
            lines = m.Lines.Select(SpeechText.FromJson).ToArray();
            pick = m.Mode == SpeechMode.Game ? SpeechPick.Random : SpeechPick.FromWire(m.Pick, m.Seed);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or ArgumentOutOfRangeException) { return; }
        if (m.Mode == SpeechMode.Replace && lines.Length == 0) return;
        Book.Set(SpeechLayer.Remote, m.Npc, "server", m.Kind, m.Mode, lines, 0, pick);
        if (m.Kind == NpcLineKind.Greeting) Changed(m.Npc);
        else ChangedLines(m.Npc, m.Kind);
    }

    internal static void Received(NextMsg m)
    {
        if (IsHost) return;
        SpeechNative.Next(m.Npc, m.Kind, m.Line);
    }

    internal static void ReceivedDialogReset()
    {
        if (IsHost) return;
        foreach (var npc in Book.DropLayer(SpeechLayer.Remote, _ => true, says: false, topics: true)) ChangedAll(npc);
    }

    /// <summary>Host: everything of kiln:npcdialog to one client that just became ready (a Reset first).</summary>
    internal static void DialogSnapshot(SpeechPeer peer)
    {
        if (!IsHost) return;
        lock (Order)
        {
            var now = Clock();
            SendToPeer(peer, new DialogResetMsg(true));
            foreach (var npc in Book.Npcs())
                foreach (var kind in new[] { NpcLineKind.Greeting, NpcLineKind.Farewell, NpcLineKind.MenuLine })
                {
                    var eff = Book.Effective(npc, kind, now);
                    if (eff.Mode == SpeechMode.Game) continue;
                    SendToPeer(peer, LinesOf(npc, kind, eff));
                    if (eff.Pick.Choose is not null) ByWork.Enqueue((npc, kind, peer));
                }
            foreach (var npc in Book.Npcs())
                if (WireOf(Book.EffectiveTopics(npc, SpeechLayer.Local)) is { Length: > 0 } page) SendToPeer(peer, new TopicsMsg(npc, page));
        }
    }

    private static void ForgetDialog()
    {
        lock (Gate)
        {
            AppliedLines.Clear();
            SentLines.Clear();
            Deciding.Clear();
            ByState.Clear();
            AppliedTopics.Clear();
            SentTopics.Clear();
            Waiting.Clear();
            Budgets.Clear();
        }
        while (ByWork.TryDequeue(out _)) { }
        while (PickWork.TryDequeue(out _)) { }
    }

    internal static void EncodeLines(LinesMsg m, NetWriter w)
    {
        if (m.Lines.Length > SpeechText.MaxLines) throw new NetFormatException("too many lines");
        w.UInt32(m.Npc).Byte((byte)m.Kind).Byte((byte)m.Mode).Byte(m.Pick).UInt32(m.Seed).Byte((byte)m.Lines.Length);
        foreach (var l in m.Lines) w.String(l);
    }

    internal static LinesMsg DecodeLines(ref NetReader r)
    {
        var npc = r.UInt32();
        var kind = Kind(r.Byte());
        var mode = r.Byte();
        if (mode > (byte)SpeechMode.Replace) throw new NetFormatException($"unknown speech mode {mode}");
        if (kind != NpcLineKind.Greeting && mode == (byte)SpeechMode.Suppress) throw new NetFormatException($"a {kind} is replaced or the game's");
        var pick = r.Byte();
        if (pick > SpeechNative.PickHost) throw new NetFormatException($"unknown pick {pick}");
        var seed = r.UInt32();
        var n = r.Byte();
        if (n > SpeechText.MaxLines) throw new NetFormatException($"{n} lines");
        var lines = new string[n];
        for (var i = 0; i < n; i++) lines[i] = r.String(MaxLineBytes);
        return new LinesMsg(npc, kind, (SpeechMode)mode, pick, seed, lines);
    }
}
