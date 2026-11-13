// SPDX-License-Identifier: MIT
// The fake kiln.chat (native/core/abi/services/kiln_chat.h, v1) under the real Kiln.Chat.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_chat.h</c> v1. The sizes are pinned by tests to Kiln.Chat's private mirrors.</summary>
internal static unsafe class ChatAbi
{
    public const string Service = "kiln.chat";
    public const uint Version = 1;
    public const int MaxText = 1024;
    public const int NameMax = 64;

    [StructLayout(LayoutKind.Sequential)]
    public struct Player
    {
        public uint Size, Entity, Connection, Reader;
        public ulong Account;
        public uint Permissions, State;
        public fixed byte Name[NameMax];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public uint Size, Type;
        public ulong Id;
        public Player Sender;
        public NativeStr Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte ReceiveHooked, SendReady, R0, R1;
        public uint Players, Subscribers;
        public ulong Received, Consumed, Sent, InboxCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LineInfo
    {
        public uint Size, Type, Connection, ReaderMask;
        public ulong Account;
        public uint TextLen, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<Message*, void*, byte>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, uint*, int> Send;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, uint, int> Say;
        public delegate* unmanaged[Cdecl]<Player*, uint, uint*, int> Players;
        public delegate* unmanaged[Cdecl]<uint, Player*, int> FindPlayer;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, int> Inject;
        public delegate* unmanaged[Cdecl]<LineInfo*, byte*, nuint, int> LastLine;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.chat</c> table. Modelled: subscribe and unsubscribe (process-wide,
/// as Kiln.Chat subscribes once per process), send, say, players, find_player, status, inject. Unavailable: last_line
/// (the game's inbox and its line types are not modelled).</summary>
internal static unsafe class ChatTable
{
    private static ChatAbi.Table* _table;

    // Native subscribers live as long as the process (Kiln.Chat subscribes once); each harness delivers to them.
    private static readonly object SubscribersLock = new();
    private static readonly List<(ulong Handle, nint Fn, nint User)> Subscribers = [];
    private static ulong _nextHandle;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (ChatAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(ChatAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(ChatAbi.Table), Version = ChatAbi.Version };
        t->Subscribe = &Subscribe;
        t->Unsubscribe = &Unsubscribe;
        t->Send = &Send;
        t->Say = &Say;
        t->Players = &Players;
        t->FindPlayer = &FindPlayer;
        t->GetStatus = &GetStatus;
        t->Inject = &Inject;
        t->LastLine = &LastLine;
        _table = t;
        return (nint)t;
    }

    private static ChatFake? Fake => FakeTables.Active(ChatAbi.Service)?.ChatFake;

    public static (ulong Handle, nint Fn, nint User)[] SubscribersNow
    {
        get
        {
            lock (SubscribersLock) return [.. Subscribers];
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(ulong owner, delegate* unmanaged[Cdecl]<ChatAbi.Message*, void*, byte> fn, void* user, ulong* handle)
    {
        try
        {
            if (fn == null || handle == null) return FakeTables.InvalidArgument;
            if (FakeTables.Off(ChatFake.ReceiveFeature)) return FakeTables.Unavailable;
            lock (SubscribersLock)
            {
                var h = ++_nextHandle;
                Subscribers.Add((h, (nint)fn, (nint)user));
                *handle = h;
            }
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(ulong handle)
    {
        lock (SubscribersLock) return Subscribers.RemoveAll(s => s.Handle == handle) > 0 ? FakeTables.Ok : FakeTables.NotFound;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Send(uint to, NativeStr text, uint* delivered)
    {
        try
        {
            if (delivered != null) *delivered = 0;
            if (Fake is not { } f) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            var rc = f.Send(to, text.ToString(), out var n);
            if (delivered != null) *delivered = (uint)n;
            return rc;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Say(uint speaker, NativeStr text, uint to)
    {
        try
        {
            if (Fake is not { } f) return FakeTables.Unavailable;
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            return f.Say(speaker, text.ToString(), to);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Players(ChatAbi.Player* out_, uint capacity, uint* count)
    {
        try
        {
            if (count == null || (out_ == null && capacity > 0)) return FakeTables.InvalidArgument;
            *count = 0;
            if (Fake is not { } f || FakeTables.Off(ChatFake.PlayersFeature)) return FakeTables.Unavailable;
            var all = f.Connected();
            *count = (uint)all.Count;
            for (var i = 0; i < all.Count && i < capacity; i++) out_[i] = ChatFake.Native(all[i]);
            return all.Count > capacity ? FakeTables.BufferTooSmall : FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FindPlayer(uint entity, ChatAbi.Player* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            if (Fake is not { } f || FakeTables.Off(ChatFake.PlayersFeature)) return FakeTables.Unavailable;
            if (f.Connected().FirstOrDefault(p => p.Entity.Value == entity) is not { } player) return FakeTables.NotFound;
            *out_ = ChatFake.Native(player);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetStatus(ChatAbi.Status* out_)
    {
        try
        {
            if (out_ == null) return FakeTables.InvalidArgument;
            var s = new ChatAbi.Status { Size = (uint)sizeof(ChatAbi.Status) };
            lock (SubscribersLock) s.Subscribers = (uint)Subscribers.Count;
            Fake?.Fill(ref s);  // else nothing is hooked and nothing can be sent: no server runs
            *out_ = s;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Inject(uint connection, NativeStr text)
    {
        try
        {
            // chat.cpp inject(): a text, and the send side (the line goes through Kiln's own queue)
            if (text.Ptr == null || text.Len == 0) return FakeTables.InvalidArgument;
            if (Fake is not { } f || FakeTables.Off(ChatFake.SendFeature)) return FakeTables.Unavailable;
            f.Queue(connection, text.ToString());
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    // The game's inbox, its line types and reader masks are not modelled: KilnHarness.Chat and FakePlayer.Chat say what
    // was shown to whom.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LastLine(ChatAbi.LineInfo* info, byte* text, nuint capacity) =>
        FakeTables.OnTickThread ? FakeTables.Unavailable : FakeTables.WrongThread;

    /// <summary>Hands a typed line to every native subscriber; true when one consumed it.</summary>
    public static bool Deliver(ChatAbi.Message* message)
    {
        var consumed = false;
        foreach (var (_, fn, user) in SubscribersNow)
            if (((delegate* unmanaged[Cdecl]<ChatAbi.Message*, void*, byte>)fn)(message, (void*)user) != 0)
                consumed = true;
        return consumed;
    }
}

/// <summary>One harness's chat: what players typed (delivered to the subscribers at the start of the next step, as the
/// game's receive loop hands them over in the next sim step's Pre), and every line each fake player was shown.</summary>
internal sealed unsafe class ChatFake
{
    public const string SendFeature = "chat.send", ReceiveFeature = "chat.receive", PlayersFeature = "chat.players";

    public static readonly string[] Features = [SendFeature, ReceiveFeature, PlayersFeature];

    private static long _messageId;  // running number since the process started (the game's: since boot)

    private readonly KilnHarness _harness;
    private readonly object _lock = new();
    private readonly Queue<(uint Connection, string Text)> _incoming = new();
    private readonly List<FakeChatMessage> _typed = [];
    private readonly List<FakeChatLine> _sent = [];
    private readonly Dictionary<FakePlayer, List<FakeChatLine>> _shown = new(ReferenceEqualityComparer.Instance);
    private ulong _received, _consumed, _sentCount;

    public ChatFake(KilnHarness harness) => _harness = harness;

    /// <summary>The game server and its chat system have been seen: from the step the server started.</summary>
    public bool SendReady => _harness.State == HarnessState.Running;

    public void Fill(ref ChatAbi.Status s)
    {
        lock (_lock)
        {
            s.ReceiveHooked = FakeTables.Off(ReceiveFeature) ? (byte)0 : (byte)1;
            s.SendReady = SendReady && !FakeTables.Off(SendFeature) ? (byte)1 : (byte)0;
            s.Players = (uint)Connected().Count;
            s.Received = _received;
            s.Consumed = _consumed;
            s.Sent = _sentCount;
        }
    }

    /// <summary>The connected players: the fake players in game, by slot.</summary>
    public List<FakePlayer> Connected() =>
        [.. (_harness.PlayersFake?.Present ?? []).Where(p => p.InGame).OrderBy(p => p.Slot)];

    public static ChatAbi.Player Native(FakePlayer p)
    {
        var n = new ChatAbi.Player
        {
            Size = (uint)sizeof(ChatAbi.Player),
            Entity = p.Entity.Value,
            Connection = p.Handle,
            Reader = (uint)p.Slot,
            Permissions = (uint)p.Rights,
        };
        SpawnAbi.CopyText(p.Name, n.Name, ChatAbi.NameMax);
        return n;
    }

    // ---- the test's side ----

    public void Queue(uint connection, string text)
    {
        lock (_lock) _incoming.Enqueue((connection, Clip(text)));
    }

    public IReadOnlyList<FakeChatLine> ShownTo(FakePlayer player)
    {
        lock (_lock) return _shown.TryGetValue(player, out var list) ? [.. list] : [];
    }

    public IReadOnlyList<FakeChatMessage> Typed
    {
        get
        {
            lock (_lock) return [.. _typed];
        }
    }

    public IReadOnlyList<FakeChatLine> Sent
    {
        get
        {
            lock (_lock) return [.. _sent];
        }
    }

    // ---- the receive loop: the start of a step, after the players' scan ----

    public void BeforeStep(ulong step)
    {
        List<(uint Connection, string Text)> lines;
        lock (_lock)
        {
            if (_incoming.Count == 0) return;
            lines = [.. _incoming];
            _incoming.Clear();
        }
        var connected = Connected();
        var hooked = FakeTables.Active(ChatAbi.Service) is not null && !FakeTables.Off(ReceiveFeature);
        foreach (var (connection, text) in lines)
        {
            var sender = connected.FirstOrDefault(p => p.Handle == connection);
            var bytes = Encoding.UTF8.GetBytes(text);
            bool consumed;
            fixed (byte* t = bytes)
            {
                var m = new ChatAbi.Message
                {
                    Size = (uint)sizeof(ChatAbi.Message),
                    Type = 0,
                    Id = (ulong)Interlocked.Increment(ref _messageId),
                    Sender = sender is null ? new ChatAbi.Player { Size = (uint)sizeof(ChatAbi.Player), Connection = connection } : Native(sender),
                    Text = new NativeStr { Ptr = t, Len = (nuint)bytes.Length },
                };
                // the receive hook hands lines to subscribers; with it (or kiln.chat) off the game's chat takes them alone
                consumed = hooked && ChatTable.Deliver(&m);
            }
            lock (_lock)
            {
                _received++;
                if (consumed) _consumed++;
                _typed.Add(new FakeChatMessage(step, sender, text, consumed));
            }
            // a line nobody consumed goes into the chat: every player is sent it, the sender too (the only way the sender
            // sees their own line); a line with no speaker is sent but not shown
            if (!consumed && sender is not null)
                foreach (var p in Connected())
                    Show(p, new FakeChatLine(step, $"{sender.Name}: {text}", sender.Name, null, null));
        }
    }

    // ---- server lines (tick thread) ----

    public int Send(uint to, string text, out int delivered)
    {
        delivered = 0;
        if (FakeTables.Off(SendFeature) || !SendReady) return FakeTables.Unavailable;
        var recipients = to == 0 ? Connected() : Connected().Where(p => p.Entity.Value == to).ToList();
        if (to != 0 && recipients.Count == 0) return FakeTables.NotFound;
        text = Clip(text);
        var step = _harness.CurrentStep;
        var mod = HostAccess.Current?.Info.Id;
        foreach (var p in recipients) Show(p, new FakeChatLine(step, text, null, mod, p));
        lock (_lock)
        {
            _sent.Add(new FakeChatLine(step, text, null, mod, to == 0 ? null : recipients[0]));
            _sentCount += (ulong)recipients.Count;
        }
        delivered = recipients.Count;
        return FakeTables.Ok;
    }

    public int Say(uint speaker, string text, uint to)
    {
        if (FakeTables.Off(SendFeature) || !SendReady) return FakeTables.Unavailable;
        var connected = Connected();
        if (connected.FirstOrDefault(p => p.Entity.Value == speaker) is not { } who) return FakeTables.NotFound;
        var recipients = to == 0 ? connected : connected.Where(p => p.Entity.Value == to).ToList();
        if (to != 0 && recipients.Count == 0) return FakeTables.NotFound;
        text = Clip(text);
        var step = _harness.CurrentStep;
        var mod = HostAccess.Current?.Info.Id;
        foreach (var p in recipients) Show(p, new FakeChatLine(step, $"{who.Name}: {text}", who.Name, mod, p));
        lock (_lock)
        {
            _sent.Add(new FakeChatLine(step, $"{who.Name}: {text}", who.Name, mod, to == 0 ? null : recipients[0]));
            _sentCount += (ulong)recipients.Count;
        }
        return FakeTables.Ok;
    }

    private void Show(FakePlayer player, FakeChatLine line)
    {
        lock (_lock)
        {
            if (!_shown.TryGetValue(player, out var list)) _shown[player] = list = [];
            list.Add(line);
        }
    }

    // KILN_CHAT_MAX_TEXT: longer text is cut at a character boundary
    private static string Clip(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= ChatAbi.MaxText) return text;
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > ChatAbi.MaxText) break;
            sb.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return sb.ToString();
    }
}
