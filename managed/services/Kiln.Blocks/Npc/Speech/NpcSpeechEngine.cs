// SPDX-License-Identifier: MIT
// Kiln.Npcs behind the API: the book (SpeechBook), what this process's bubbles show (kiln.npcspeech), and the channel
// kiln:npcspeech v1 (required: false). The host (a server world runs here) sends every change of an NPC's effective
// speech to every Kiln client and a whole snapshot to one that becomes ready; a joined client keeps what it gets in the
// Remote layer, and its own mods' calls in the Local layer (its own screen only).
//   1 Speech  ToClient  u32 npc, u8 mode, u8 n, n x string (a line's text JSON)
//   2 Say     ToClient  u32 npc, string (text JSON; "" ends it), u32 ms left
//   3 Reset   ToClient  bool: forget everything the server sent (the snapshot's first message)
// Since 0.2.0-dev.32 the greetings' picks, farewells, the menu's line and topics travel on kiln:npcdialog
// (NpcDialogEngine.cs); this channel stays as 31.16-31.21 clients know it.
namespace Kiln;

internal sealed record SpeechMsg(uint Npc, SpeechMode Mode, string[] Lines);
internal sealed record SayMsg(uint Npc, string Text, uint Ms);
internal sealed record SpeechResetMsg(bool All);

/// <summary>Started at the first service tick (ServiceTick.AutoStart): the channel must be there before anyone joins.</summary>
internal static class NpcSpeechStartup
{
    static NpcSpeechStartup() => NpcSpeechEngine.Start();
}

internal static partial class NpcSpeechEngine
{
    public const string ChannelName = "kiln:npcspeech";
    public const string ChannelVersion = "1";
    public const ushort MsgSpeech = 1, MsgSay = 2, MsgReset = 3;
    internal const int MaxLineBytes = SpeechText.MaxBytes;
    /// <summary>Distinct texts a world registers with kiln.content (which keeps each for the process).</summary>
    internal const int MaxTexts = 4096;

    internal static readonly SpeechBook Book = new();
    private static readonly object Gate = new();
    // One change at a time, from the book to kiln.npcspeech and the clients (any thread: a mod's threads, a joined
    // client's network thread): what came last is what the bubble and the clients end with, and a late joiner's
    // snapshot is not overtaken by an older change. Held while calling kiln.npcspeech, kiln.content and the channel,
    // none of which calls back into Kiln.Npcs.
    private static readonly object Order = new();
    private static readonly Dictionary<uint, SpeechEffect> Applied = new();   // what this process's bubbles were told (greetings)
    private static readonly Dictionary<uint, SpeechEffect> Sent = new();      // what the clients were sent (host)
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal);
    private static NetChannel? _channel;
    private static bool _started;
    private static int _warned;

    /// <summary>Test seams: the clock (ms), whether this process is the host, and what goes out (null: the channel).</summary>
    internal static Func<long> Clock = static () => Environment.TickCount64;
    internal static Func<bool>? HostHook;
    internal static Action<object>? SendHook;

    internal static SpeechLayer Layer => SpeechLayer.Local;  // a mod's own calls, on any side
    internal static bool IsHost => HostHook?.Invoke() ?? Lifecycle.IsServerRunning;

    internal static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own channel and handlers, not a mod's
        try
        {
            Events.On<ServerStoppingEvent>(_ => WorldEnded(), EventPriority.Lowest);
            if (!Network.Available) return;
            _channel = Network.Channel(ChannelName, ChannelVersion, required: false, maxMessageBytes: 96 * 1024)
                .Register<SpeechMsg>(MsgSpeech, EncodeSpeech, DecodeSpeech, static (m, _) => Received(m), NetDirection.ToClient)
                .Register<SayMsg>(MsgSay, static (m, w) => w.UInt32(m.Npc).String(m.Text).UInt32(m.Ms),
                    static (ref NetReader r) => new SayMsg(r.UInt32(), r.String(MaxLineBytes), r.UInt32()), static (m, _) => Received(m),
                    NetDirection.ToClient)
                .Register<SpeechResetMsg>(MsgReset, static (m, w) => w.Bool(m.All), static (ref NetReader r) => new SpeechResetMsg(r.Bool()),
                    static (_, _) => ReceivedReset(), NetDirection.ToClient);
            _dialog = RegisterDialog();
            Events.On<NetPeerReadyEvent>(e =>
            {
                Snapshot(e.Peer);
                if (e.Peer.HasChannel(DialogChannel)) DialogSnapshot(PeerOf(e.Peer));
            }, EventPriority.Monitor);
            Network.Disconnected += _ => ServerGone();
        }
        catch (Exception e)
        {
            ModLog.Warn($"kiln.npcspeech: the channel {ChannelName} is unavailable, NPC speech stays on this machine: {e.Message}");
        }
        finally
        {
            StartTick();
            ModContext.CurrentMod = outer;
        }
    }

    /// <summary>The calling mod's id ("kiln" outside a mod); its state goes when it unloads.</summary>
    internal static string Owner()
    {
        Start();
        var mod = ModContext.CurrentMod;
        if (mod is null) return "kiln";
        var id = mod.Info.Id;
        lock (Gate)
            if (Watched.Add(id))
                mod.Retiring += () =>
                {
                    lock (Gate) Watched.Remove(id);
                    foreach (var npc in Book.DropOwner(id)) ChangedAll(npc);
                };
        return id;
    }

    internal static SpeechResult Set(EntityId npc, string owner, SpeechMode mode, UiText[] lines, int priority) =>
        Set(npc, owner, NpcLineKind.Greeting, mode, lines, priority, SpeechPick.Random);

    internal static SpeechResult Set(EntityId npc, string owner, NpcLineKind kind, SpeechMode mode, UiText[] lines, int priority, SpeechPick pick)
    {
        Start();
        Book.Set(SpeechLayer.Local, npc.Value, owner, kind, mode, lines, priority, pick);
        return kind == NpcLineKind.Greeting ? Changed(npc.Value) : ChangedLines(npc.Value, kind);
    }

    internal static SpeechResult Say(EntityId npc, string owner, UiText text, TimeSpan duration, SayOptions options)
    {
        lock (Order)
        {
            var now = Clock();
            var cur = Book.Effective(npc.Value, now);
            if (!options.Interrupt && cur.Say is not null && cur.SayOwner != owner)
                return SpeechResult.No($"{cur.SayOwner}'s Say is still showing over {npc}");
            Book.Say(SpeechLayer.Local, npc.Value, owner, text, now + (long)duration.TotalMilliseconds);
            return Changed(npc.Value);
        }
    }

    internal static void StopSay(EntityId npc, string owner)
    {
        if (Book.StopSay(SpeechLayer.Local, npc.Value, owner)) Changed(npc.Value);
    }

    // ---------------------------------------------------------------------------------------------- apply / send ----

    /// <summary>The NPC's effective speech changed (maybe): this process's bubbles follow, and as the host the clients.</summary>
    internal static SpeechResult Changed(uint npc)
    {
        lock (Order)
        {
            var now = Clock();
            var eff = Book.Effective(npc, now);
            var note = ApplyHere(npc, eff, now);
            if (IsHost)
            {
                Broadcast(npc, eff, now);
                BroadcastLines(npc, NpcLineKind.Greeting, eff);
            }
            NotePick(npc, NpcLineKind.Greeting, eff);
            if (!IsHost && !SpeechNative.Loaded) return SpeechResult.No(note ?? "kiln.npcspeech is not here: this game shows no Kiln speech");
            return note is null ? SpeechResult.Yes : new SpeechResult(true, note);
        }
    }

    /// <summary>Tells kiln.npcspeech; null when it shows, else a note on why it does not here. Recorded as applied only
    /// when it all went in, so the next change of that NPC tries again (kiln.content's hooks may come later).</summary>
    private static string? ApplyHere(uint npc, SpeechEffect eff, long now)
    {
        lock (Gate)
            if (Applied.TryGetValue(npc, out var was) && was.SameAs(eff) && was.Pick.SameAs(eff.Pick)) return null;
        if (!SpeechNative.Loaded)  // a dedicated server has no bubbles: its clients show them, nothing to note
        {
            Remember(npc, eff);
            return IsHost ? null : "kiln.npcspeech is not here: this game shows no Kiln speech (an unknown game build?)";
        }
        string? note = null;
        var mode = eff.Mode;
        var tags = new List<uint>();
        if (mode == SpeechMode.Replace)
        {
            // every line's text first: a tag nothing answers would show an empty bubble, so the game keeps its lines
            foreach (var line in eff.Lines)
            {
                if (Register(line, ref note) is not { } tag)
                {
                    tags.Clear();
                    break;
                }
                tags.Add(tag);
            }
            if (tags.Count == 0) mode = SpeechMode.Game;
        }
        var whole = mode == eff.Mode;
        var rc = SpeechNative.HasLines
            ? SpeechNative.SetLines(npc, NpcLineKind.Greeting, mode, tags.ToArray(), eff.Pick.Code, eff.Pick.Seed)
            : SpeechNative.Set(npc, mode, tags.ToArray());
        if (rc == SpeechNative.Unavailable)
        {
            note = Capabilities.Why(SpeechNative.BubbleCapability);
            whole = false;
        }
        uint sayTag = 0, ms = 0;
        if (eff.Say is { } say && eff.SayUntilMs > now)
        {
            if (Register(say, ref note) is { } tag)
            {
                sayTag = tag;
                ms = (uint)Math.Clamp(eff.SayUntilMs - now, 1, (long)Npcs.MaxSay.TotalMilliseconds);
            }
            else whole = false;
        }
        if (SpeechNative.Say(npc, sayTag, ms) == SpeechNative.Unavailable)
        {
            note ??= Capabilities.Why(SpeechNative.BubbleCapability);
            whole = false;
        }
        if (whole) Remember(npc, eff);
        else lock (Gate) Applied.Remove(npc);
        if (note is not null && Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: {note}");
        return note;
    }

    private static void Remember(uint npc, SpeechEffect eff)
    {
        lock (Gate)
        {
            if (eff.SameAs(SpeechEffect.None)) Applied.Remove(npc);
            else Applied[npc] = eff;
        }
    }

    /// <summary>The text's tag, once kiln.content answers it; null (and why, in <paramref name="note"/>) when not.</summary>
    private static uint? Register(UiText text, ref string? note)
    {
        var json = SpeechText.Json(text);
        var tag = SpeechText.Tag(json);
        switch (SpeechNative.Text(tag, json))
        {
            case SpeechNative.Ok:
                return tag;
            case SpeechNative.TooManyTexts:
                note = $"this world already has {MaxTexts} distinct NPC speech texts (kiln.content keeps each for the session): " +
                       "a new one is not shown; keep numbers and names that change often out of the texts";
                return null;
            default:
                note = "kiln.content's loca hooks are not here: Kiln's lines cannot show, the game keeps its own (Suppress still works)";
                return null;
        }
    }

    private static void Broadcast(uint npc, SpeechEffect eff, long now)
    {
        SpeechEffect? was;
        lock (Gate)
        {
            Sent.TryGetValue(npc, out was);
            if (was is not null && was.SameAs(eff)) return;
            if (eff.SameAs(SpeechEffect.None)) Sent.Remove(npc);
            else Sent[npc] = eff;
        }
        was ??= SpeechEffect.None;
        if (eff.Mode != was.Mode || !eff.Lines.Select(SpeechText.Json).SequenceEqual(was.Lines.Select(SpeechText.Json)))
            Send(new SpeechMsg(npc, eff.Mode, eff.Lines.Select(SpeechText.Json).ToArray()));
        if (eff.SayUntilMs != was.SayUntilMs || SpeechText.Json(eff.Say) != SpeechText.Json(was.Say))
            Send(eff.Say is { } s && eff.SayUntilMs > now
                ? new SayMsg(npc, SpeechText.Json(s), (uint)Math.Clamp(eff.SayUntilMs - now, 1, (long)Npcs.MaxSay.TotalMilliseconds))
                : new SayMsg(npc, "", 0));
    }

    private static void Send(object m)
    {
        if (SendHook is { } hook) { hook(m); return; }
        if (_channel is null && _dialog is null) return;
        try
        {
            _ = m switch
            {
                SpeechMsg s when _channel is not null => _channel.SendToAll(s),
                SayMsg s when _channel is not null => _channel.SendToAll(s),
                SpeechResetMsg s when _channel is not null => _channel.SendToAll(s),
                SpeechMsg or SayMsg or SpeechResetMsg => 0,
                _ => SendDialog(m),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or KilnUnavailableException or ArgumentException)
        {
            // ArgumentException: over the channel's limit (network.json's maxMessageBytes set below what 32 lines need)
            if (Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: could not send to the clients: {e.Message}");
        }
    }

    private static void SendTo(NetChannel channel, NetPeer peer, object m)
    {
        try
        {
            _ = m switch
            {
                SpeechMsg s => channel.SendTo(peer, s),
                SayMsg s => channel.SendTo(peer, s),
                SpeechResetMsg s => channel.SendTo(peer, s),
                _ => false,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or KilnUnavailableException or ArgumentException)
        {
            if (Interlocked.Increment(ref _warned) <= 3) ModLog.Warn($"kiln.npcspeech: could not send to {peer}: {e.Message}");
        }
    }

    /// <summary>Host: everything to one client that just became ready (a Reset first).</summary>
    internal static void Snapshot(NetPeer peer)
    {
        if (!IsHost || _channel is not { } channel || !peer.HasChannel(ChannelName)) return;
        lock (Order)
        {
            var now = Clock();
            SendTo(channel, peer, new SpeechResetMsg(true));
            foreach (var npc in Book.Npcs())
            {
                var eff = Book.Effective(npc, now);
                if (eff.Mode != SpeechMode.Game) SendTo(channel, peer, new SpeechMsg(npc, eff.Mode, eff.Lines.Select(SpeechText.Json).ToArray()));
                if (eff.Say is { } s && eff.SayUntilMs > now)
                    SendTo(channel, peer, new SayMsg(npc, SpeechText.Json(s), (uint)Math.Clamp(eff.SayUntilMs - now, 1, (long)Npcs.MaxSay.TotalMilliseconds)));
            }
        }
    }

    // ---------------------------------------------------------------------------------------------- client side ----

    internal static void Received(SpeechMsg m)
    {
        if (IsHost) return;  // the host's own screen follows its book
        if (ServerHasDialog) return;  // a dev.32 server: kiln:npcdialog's Lines carry the greeting with its pick
        UiText[] lines;
        try { lines = m.Lines.Select(SpeechText.FromJson).ToArray(); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { return; }
        if (m.Mode == SpeechMode.Replace && lines.Length == 0) return;
        Book.Set(SpeechLayer.Remote, m.Npc, "server", NpcLineKind.Greeting, m.Mode, lines, 0, SpeechPick.Random);
        Changed(m.Npc);
    }

    internal static void Received(SayMsg m)
    {
        if (IsHost) return;
        UiText? text = null;
        if (m.Text.Length > 0)
        {
            try { text = SpeechText.FromJson(m.Text); }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { return; }
        }
        var ms = Math.Min(m.Ms, (uint)Npcs.MaxSay.TotalMilliseconds);
        Book.Say(SpeechLayer.Remote, m.Npc, "server", text is null || ms == 0 ? null : text, Clock() + ms);
        Changed(m.Npc);
    }

    internal static void ReceivedReset()
    {
        if (IsHost) return;
        // its Says, and the greetings when this server sends them here (not on kiln:npcdialog, whose Reset is its own)
        var greetings = !ServerHasDialog;
        foreach (var npc in Book.DropLayer(SpeechLayer.Remote, k => greetings && k == NpcLineKind.Greeting, says: true)) Changed(npc);
    }

    private static void ServerGone()
    {
        lock (Order)
        {
            foreach (var npc in Book.DropLayer(SpeechLayer.Remote)) ChangedAll(npc);
            foreach (var npc in Book.DropLayer(SpeechLayer.Local)) ChangedAll(npc);
            Forget();
        }
    }

    /// <summary>The server world ends: every override and Say ends (here and, for clients still connected, there).</summary>
    internal static void WorldEnded()
    {
        lock (Order)
        {
            var touched = Book.DropLayer(SpeechLayer.Local);
            if (touched.Length > 0 && IsHost)
            {
                Send(new SpeechResetMsg(true));
                Send(new DialogResetMsg(true));
            }
            Forget();
        }
    }

    private static void Forget()
    {
        lock (Gate)
        {
            Applied.Clear();
            Sent.Clear();
        }
        ForgetDialog();
        SpeechNative.ClearAll();
        // the next world registers its texts again (kiln.content keeps the same text as it is): its own bound, and a
        // test kit's next harness, whose fake starts without texts, gets them
        SpeechNative.ForgetTexts();
    }

    /// <summary>Tests: back to a fresh start.</summary>
    internal static void ResetForTests()
    {
        Book.DropLayer(SpeechLayer.Local);
        Book.DropLayer(SpeechLayer.Remote);
        lock (Gate)
        {
            Applied.Clear();
            Sent.Clear();
        }
        ForgetDialog();
        SpeechNative.ForgetTexts();
    }

    internal static void EncodeSpeech(SpeechMsg m, NetWriter w)
    {
        if (m.Lines.Length > SpeechText.MaxLines) throw new NetFormatException("too many speech lines");
        w.UInt32(m.Npc).Byte((byte)m.Mode).Byte((byte)m.Lines.Length);
        foreach (var l in m.Lines) w.String(l);
    }

    internal static SpeechMsg DecodeSpeech(ref NetReader r)
    {
        var npc = r.UInt32();
        var mode = r.Byte();
        if (mode > (byte)SpeechMode.Replace) throw new NetFormatException($"unknown speech mode {mode}");
        var n = r.Byte();
        if (n > SpeechText.MaxLines) throw new NetFormatException($"{n} speech lines");
        var lines = new string[n];
        for (var i = 0; i < n; i++) lines[i] = r.String(MaxLineBytes);
        return new SpeechMsg(npc, (SpeechMode)mode, lines);
    }
}
