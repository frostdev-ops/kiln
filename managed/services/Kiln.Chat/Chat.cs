// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Rights of the user group a player joined with (the password in <c>enshrouded_server.json</c>).</summary>
[Flags]
public enum ChatPermissions : uint
{
    None = 0,
    KickBan = 1 << 0,
    AccessInventories = 1 << 1,
    EditBase = 1 << 2,
    EditWorld = 1 << 3,
    ExtendBase = 1 << 4,
    ReceiveExp = 1 << 5,
}

/// <summary>A connected player as the chat sees them.</summary>
/// <param name="Entity">Player entity; <see cref="EntityId.None"/> while not spawned.</param>
/// <param name="Name">The platform name the chat shows as speaker.</param>
/// <param name="Connection">The game's connection handle (chat lines carry it).</param>
/// <param name="Reader">The player's chat reader slot.</param>
/// <param name="Account">The game's account id hash.</param>
/// <param name="Permissions">User-group rights.</param>
public sealed record ChatPlayer(EntityId Entity, string Name, uint Connection, uint Reader, ulong Account,
    ChatPermissions Permissions)
{
    /// <summary>The user group may kick and ban (Kiln's default meaning of "admin").</summary>
    public bool CanKickBan => (Permissions & ChatPermissions.KickBan) != 0;
}

/// <summary>A line a player typed. Raised on the tick thread before anyone sees it.</summary>
public sealed class ChatMessage
{
    internal ChatMessage(ulong id, ChatPlayer? player, uint connection, string text, bool isCommand)
    {
        Id = id;
        Player = player;
        Connection = connection;
        Text = text;
        IsCommand = isCommand;
    }

    /// <summary>Running number since the server started.</summary>
    public ulong Id { get; }

    /// <summary>The sender, or null when their connection record was not found.</summary>
    public ChatPlayer? Player { get; }

    public EntityId Sender => Player?.Entity ?? EntityId.None;
    public string SenderName => Player?.Name ?? "";

    /// <summary>The sender's connection handle.</summary>
    public uint Connection { get; }

    /// <summary>The text as typed (UTF-8 from the client).</summary>
    public string Text { get; }

    /// <summary>The text starts with <see cref="Commands.Prefix"/>: after the handlers it goes to the command
    /// registry (and is not broadcast) unless a handler cancels it.</summary>
    public bool IsCommand { get; }

    /// <summary>Set to true to drop the line: nobody sees it, not even the sender, and no command runs.</summary>
    public bool Cancel { get; set; }

    public override string ToString() => $"#{Id} {SenderName}: {Text}";
}

public delegate void ChatMessageHandler(ChatMessage message);

/// <summary>Counters of the native service.</summary>
public readonly record struct ChatStatus(bool ReceiveHooked, bool SendReady, int Players, int Subscribers,
    ulong Received, ulong Consumed, ulong Sent, ulong InboxCount);

/// <summary>The newest line in the server's chat inbox (diagnostics).</summary>
public readonly record struct ChatLine(uint Type, uint Connection, ulong Account, uint ReaderMask, string Text);

/// <summary>
/// The server's text chat (service <c>kiln.chat</c>): see players' lines (and drop them), send server lines.
/// </summary>
/// <remarks>
/// A server line shows on the recipient's screen as their own line ("&lt;their name&gt;: [Server] text"): the client
/// only shows lines spoken by a known player, so Kiln makes each recipient the speaker of their copy and sends that
/// copy to them alone. <see cref="ServerPrefix"/> marks it. Text chat must be on (<c>"enableTextChat": true</c>).
/// </remarks>
public static unsafe class Chat
{
    private const int Ok = 0, NotFound = 2, Unavailable = 3, WrongThread = 4;
    internal const int MaxBytes = 1024;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePlayer
    {
        public uint Size;
        public uint Entity, Connection, Reader;
        public ulong Account;
        public uint Permissions, State;
        public fixed byte Name[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMessage
    {
        public uint Size, Type;
        public ulong Id;
        public NativePlayer Sender;
        public NativeStr Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        public uint Size;
        public byte ReceiveHooked, SendReady, R0, R1;
        public uint Players, Subscribers;
        public ulong Received, Consumed, Sent, InboxCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLineInfo
    {
        public uint Size, Type, Connection, ReaderMask;
        public ulong Account;
        public uint TextLen, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<NativeMessage*, void*, byte>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, uint*, int> Send;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, uint, int> Say;
        public delegate* unmanaged[Cdecl]<NativePlayer*, uint, uint*, int> Players;
        public delegate* unmanaged[Cdecl]<uint, NativePlayer*, int> FindPlayer;
        public delegate* unmanaged[Cdecl]<NativeStatus*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, int> Inject;
        public delegate* unmanaged[Cdecl]<NativeLineInfo*, byte*, nuint, int> LastLine;
    }

    private static Table* _table;
    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.chat", 1));

    private static readonly object Lock = new();
    private static readonly List<(ChatMessageHandler Handler, ModContext? Owner, int Faults)> Handlers = new();
    private static bool _subscribed;

    static Chat()
    {
        ModCalls.OnRetire(mod =>
        {
            lock (Lock) Handlers.RemoveAll(h => ReferenceEquals(h.Owner, mod));
        });
    }

    /// <summary>True when the native service is loaded on this game build.</summary>
    public static bool Available => T != null;

    /// <summary>Text put in front of every server line (<see cref="Broadcast"/>, <see cref="Tell"/>, command replies),
    /// because each recipient sees the line as their own.</summary>
    public static string ServerPrefix { get; set; } = "[Server] ";

    /// <summary>Every line a player types, on the tick thread, before it is shown to anyone. Set
    /// <see cref="ChatMessage.Cancel"/> to drop it. Handlers are removed when the mod that added them unloads.</summary>
    public static event ChatMessageHandler? Received
    {
        add
        {
            if (value is null) return;
            var owner = ModCalls.Caller;
            ModCalls.Watch(owner);
            lock (Lock) Handlers.Add((value, owner, 0));
            EnsureSubscribed();
        }
        remove
        {
            if (value is null) return;
            lock (Lock)
            {
                var i = Handlers.FindLastIndex(h => h.Handler == value);
                if (i >= 0) Handlers.RemoveAt(i);
            }
        }
    }

    /// <summary>Sends a server line to every connected player. Returns how many were addressed. Tick thread only.</summary>
    public static int Broadcast(string text)
    {
        var t = Require();
        Services.RequireTickThread("Chat.Broadcast");
        var total = 0;
        foreach (var line in Lines(ServerPrefix, text))
        {
            uint delivered = 0;
            Check(SendRaw(t, 0, line, &delivered), "Chat.Broadcast");
            total = Math.Max(total, (int)delivered);
        }
        return total;
    }

    /// <summary>Sends a server line to one player. Returns false when they are not connected. Tick thread only.</summary>
    public static bool Tell(EntityId player, string text)
    {
        var t = Require();
        Services.RequireTickThread("Chat.Tell");
        if (player.IsNone) return false;
        var any = false;
        foreach (var line in Lines(ServerPrefix, text))
        {
            uint delivered = 0;
            var rc = SendRaw(t, player.Value, line, &delivered);
            if (rc == NotFound) return false;
            Check(rc, "Chat.Tell");
            any |= delivered > 0;
        }
        return any;
    }

    /// <summary>Adds a line spoken by <paramref name="speaker"/> (as if they typed it, no prefix), shown to everyone
    /// or only to <paramref name="to"/>. Returns false when a player is not connected. Tick thread only.</summary>
    public static bool Say(EntityId speaker, string text, EntityId to = default)
    {
        var t = Require();
        Services.RequireTickThread("Chat.Say");
        foreach (var line in Lines("", text))
        {
            var bytes = Utf8(line);
            int rc;
            fixed (byte* p = bytes) rc = t->Say(speaker.Value, new NativeStr { Ptr = p, Len = (nuint)bytes.Length }, to.Value);
            if (rc == NotFound) return false;
            Check(rc, "Chat.Say");
        }
        return true;
    }

    /// <summary>Connected players (including ones still loading, whose <see cref="ChatPlayer.Entity"/> is none).</summary>
    public static IReadOnlyList<ChatPlayer> Players
    {
        get
        {
            var t = T;
            if (t == null) return Array.Empty<ChatPlayer>();
            var buf = stackalloc NativePlayer[64];
            uint count = 0;
            var rc = t->Players(buf, 64, &count);
            if (rc != Ok && rc != 5) return Array.Empty<ChatPlayer>();
            var list = new List<ChatPlayer>((int)Math.Min(count, 64));
            for (var i = 0; i < Math.Min(count, 64u); i++) list.Add(Convert(&buf[i]));
            return list;
        }
    }

    public static ChatPlayer? FindPlayer(EntityId entity)
    {
        var t = T;
        if (t == null || entity.IsNone) return null;
        NativePlayer p;
        return t->FindPlayer(entity.Value, &p) == Ok ? Convert(&p) : null;
    }

    /// <summary>A connected player by name: exact match (ignoring case) first, else a unique prefix.</summary>
    public static ChatPlayer? FindPlayer(string name)
    {
        var all = Players;
        var exact = all.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var prefix = all.Where(p => p.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    public static ChatStatus Status
    {
        get
        {
            var t = T;
            if (t == null) return default;
            NativeStatus s;
            if (t->Status(&s) != Ok) return default;
            return new ChatStatus(s.ReceiveHooked != 0, s.SendReady != 0, (int)s.Players, (int)s.Subscribers, s.Received,
                s.Consumed, s.Sent, s.InboxCount);
        }
    }

    /// <summary>Tools for tests: inject lines as if a player sent them, read the newest inbox line.</summary>
    public static class Diagnostics
    {
        /// <summary>Queues <paramref name="text"/> as if the player with connection handle
        /// <paramref name="connection"/> (0 = nobody) typed it. It reaches <see cref="Received"/> and the command
        /// registry at the next step and, unless consumed, the chat. Any thread.</summary>
        public static void Inject(uint connection, string text)
        {
            var t = Require();
            EnsureSubscribed();
            var bytes = Utf8(text);
            int rc;
            fixed (byte* p = bytes) rc = t->Inject(connection, new NativeStr { Ptr = p, Len = (nuint)bytes.Length });
            Check(rc, "Chat.Diagnostics.Inject");
        }

        /// <summary>The newest line in the server's chat inbox, or null when it is empty. Tick thread only.</summary>
        public static ChatLine? LastLine()
        {
            var t = Require();
            Services.RequireTickThread("Chat.Diagnostics.LastLine");
            NativeLineInfo info;
            var buf = stackalloc byte[MaxBytes + 1];
            var rc = t->LastLine(&info, buf, (nuint)(MaxBytes + 1));
            if (rc == NotFound) return null;
            Check(rc, "Chat.Diagnostics.LastLine");
            var len = (int)Math.Min(info.TextLen, (uint)MaxBytes);
            return new ChatLine(info.Type, info.Connection, info.Account, info.ReaderMask, Encoding.UTF8.GetString(buf, len));
        }
    }

    // ---- internals ----

    internal static bool EnsureSubscribed()
    {
        var t = T;
        if (t == null) return false;
        lock (Lock)
        {
            if (_subscribed) return true;
            ulong handle;
            var rc = t->Subscribe(0, &OnNativeMessage, null, &handle);
            if (rc != Ok)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"kiln.chat: cannot receive players' lines (kiln error {rc})");
                return false;
            }
            _subscribed = true;
            Backend.Current.Log(0, LogLevel.Info, "kiln.chat: Kiln.Chat receives players' lines");
            return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte OnNativeMessage(NativeMessage* m, void* user)
    {
        try
        {
            var text = m->Text.ToString();
            // The service fills the sender from the connection record; without one only the handle is set.
            var player = m->Sender.Entity != 0 || m->Sender.Name[0] != 0 ? Convert(&m->Sender) : null;
            var message = new ChatMessage(m->Id, player, m->Sender.Connection, text, Commands.IsCommandLine(text));
            return Dispatch(message) ? (byte)1 : (byte)0;
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.chat: dispatch threw: {e}");
            return 0;
        }
    }

    /// <summary>Handlers, then commands. Returns true when the line must not reach the chat.</summary>
    internal static bool Dispatch(ChatMessage message)
    {
        (ChatMessageHandler Handler, ModContext? Owner, int Faults)[] handlers;
        lock (Lock) handlers = Handlers.ToArray();
        foreach (var h in handlers)
        {
            if (ModCalls.Invoke(h.Owner, () => h.Handler(message), "Chat.Received handler")) continue;
            lock (Lock)
            {
                var i = Handlers.FindIndex(x => x.Handler == h.Handler && ReferenceEquals(x.Owner, h.Owner));
                if (i < 0) continue;
                var faults = Handlers[i].Faults + 1;
                if (faults >= 5)
                {
                    Handlers.RemoveAt(i);
                    Backend.Current.Log(0, LogLevel.Error,
                        $"kiln.chat: a Chat.Received handler of {ModCalls.OwnerName(h.Owner)} failed 5 times and was removed");
                }
                else Handlers[i] = (Handlers[i].Handler, Handlers[i].Owner, faults);
            }
        }
        if (Events.HasSubscribers<ChatMessageEvent>()) return DispatchEvent(message);
        if (message.Cancel) return true;
        if (message.IsCommand) return Commands.RunFromChat(message);
        return false;
    }

    /// <summary>The bus part of <see cref="Dispatch"/>: <see cref="ChatMessageEvent"/>, then cancel / rewrite.</summary>
    private static bool DispatchEvent(ChatMessage message)
    {
        var ev = Events.Post(new ChatMessageEvent(message) { Canceled = message.Cancel });
        if (ev.Canceled) return true;
        if (!ev.TextChanged) return message.IsCommand && Commands.RunFromChat(message);
        var text = ev.Text ?? "";
        if (message.IsCommand && Commands.IsCommandLine(text)) return Commands.RunFromChat(message, text);
        if (text.Trim().Length == 0) return true;  // rewritten to nothing: drop it
        if (!message.Sender.IsNone && Available)
        {
            try
            {
                if (Say(message.Sender, text)) return true;
            }
            catch (InvalidOperationException)
            {
                // the sender left or the chat is not ready: pass the original on
            }
        }
        Backend.Current.Log(0, LogLevel.Warn,
            $"kiln.chat: line #{message.Id} was rewritten by a ChatMessageEvent handler, but its sender has no entity; passed on unchanged");
        return message.IsCommand && Commands.RunFromChat(message);
    }

    private static Table* Require() =>
        T != null ? T : throw new NotSupportedException("kiln.chat is unavailable on this game build");

    private static int SendRaw(Table* t, uint to, string line, uint* delivered)
    {
        var bytes = Utf8(line);
        fixed (byte* p = bytes) return t->Send(to, new NativeStr { Ptr = p, Len = (nuint)bytes.Length }, delivered);
    }

    /// <summary>One chat line per text line, prefixed, cut to the chat's byte limit.</summary>
    internal static IEnumerable<string> Lines(string prefix, string text)
    {
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            yield return Clip(prefix + line, MaxBytes);
        }
    }

    /// <summary>Cuts text to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a character.</summary>
    internal static string Clip(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes) break;
            sb.Append(rune.ToString());
            bytes += n;
        }
        return sb.ToString();
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static ChatPlayer Convert(NativePlayer* p)
    {
        var len = 0;
        while (len < 63 && p->Name[len] != 0) len++;
        var name = Encoding.UTF8.GetString(p->Name, len);
        return new ChatPlayer(new EntityId(p->Entity), name, p->Connection, p->Reader, p->Account,
            (ChatPermissions)p->Permissions);
    }

    private static void Check(int rc, string api)
    {
        switch (rc)
        {
            case Ok: return;
            case 1: throw new ArgumentException($"{api}: invalid argument");
            case Unavailable: throw new InvalidOperationException($"{api}: chat not available yet (no game server seen) or not on this build");
            case WrongThread: throw new InvalidOperationException($"{api} must be called on the tick thread (use Scheduler.Post)");
            default: throw new InvalidOperationException($"{api} failed (kiln error {rc})");
        }
    }
}
