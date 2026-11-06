// SPDX-License-Identifier: MIT
// Kiln.Network: mod channels between clients and the server (Forge: SimpleChannel / NetworkRegistry, the FML
// handshake and mod-mismatch rejection). Transport: kiln.net (game channel 12, docs/re/network.md). Protocol and
// usage: docs/kiln/network.md.
using System.Text.Json;

namespace Kiln;

/// <summary>Which way a message travels.</summary>
public enum NetDirection
{
    /// <summary>Client to server (handled on the server's tick thread).</summary>
    ToServer = 1,

    /// <summary>Server to client (handled on the client's main thread).</summary>
    ToClient = 2,

    Both = ToServer | ToClient,
}

/// <summary>Where a peer stands.</summary>
public enum NetPeerState
{
    /// <summary>The game connection exists; the Kiln handshake has not finished.</summary>
    Connecting = 0,

    /// <summary>Handshake done: channels the peer also has can be used.</summary>
    Ready = 1,

    /// <summary>The peer has no Kiln (a vanilla client, or a server without Kiln.Network).</summary>
    Vanilla = 2,

    /// <summary>The server refused the client (mod mismatch); it is being disconnected.</summary>
    Rejected = 3,

    /// <summary>Gone.</summary>
    Closed = 4,
}

/// <summary>A mod as the other side reported it in the handshake.</summary>
public sealed record NetRemoteMod(string Id, string Version, ModSide Side);

/// <summary>A message body type with its own codec (Forge: a message class with encode/decode).</summary>
/// <example><code>
/// public record Ping(int N) : INetMessage&lt;Ping&gt;
/// {
///     public void Encode(NetWriter w) => w.Int32(N);
///     public static Ping Decode(ref NetReader r) => new(r.Int32());
/// }
/// </code></example>
public interface INetMessage<TSelf> where TSelf : INetMessage<TSelf>
{
    void Encode(NetWriter w);
    static abstract TSelf Decode(ref NetReader r);
}

public delegate T NetDecoder<T>(ref NetReader reader);

public delegate void NetRawHandler(NetContext context, ushort messageId, ReadOnlySpan<byte> body);

/// <summary>A connection: on the server, one client; on a client, the server (<see cref="IsServer"/>).</summary>
public sealed class NetPeer
{
    internal NetPeer(uint id) => Id = id;

    /// <summary>kiln.net's peer id (server side: the game's machine handle).</summary>
    public uint Id { get; }

    /// <summary>This is the server (on a client).</summary>
    public bool IsServer => Id == NetNative.PeerServer;

    /// <summary>A lab test peer (tools/nettest).</summary>
    public bool IsLab => !IsServer && (Id & NetNative.PeerLab) != 0;

    public NetPeerState State { get; internal set; }

    /// <summary>The player's platform name (server side; "server" on a client).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The player's entity (server side), <see cref="EntityId.None"/> before the game created it.</summary>
    public EntityId Player { get; internal set; }

    /// <summary>The game's account id of the player (server side), 0 when unknown.</summary>
    public ulong AccountId { get; internal set; }

    /// <summary>The peer's Kiln version (null until the handshake).</summary>
    public string? KilnVersion { get; internal set; }

    /// <summary>The peer's mods with <c>side</c> both or client (client) / both (server), from the handshake.</summary>
    public IReadOnlyList<NetRemoteMod> Mods { get; internal set; } = [];

    /// <summary>Handshake properties the peer sent (see <see cref="Network.SetHandshakeProperty"/>).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; internal set; } = new Dictionary<string, string>();

    /// <summary>Mismatch notes from the handshake (missing / extra / different mods).</summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = [];

    /// <summary>Last measured round trip (Kiln ping), null before the first.</summary>
    public TimeSpan? RoundTrip { get; internal set; }

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public long MessagesIn, MessagesOut, BytesIn, BytesOut, Dropped;

    /// <summary>Whether the peer has channel <paramref name="name"/> (with a matching version).</summary>
    public bool HasChannel(string name) => Channels.ContainsKey(name);

    internal readonly Dictionary<string, ushort> Channels = new(StringComparer.Ordinal);  // name -> id used on the wire
    internal Dictionary<string, string> Declared = new(StringComparer.Ordinal);            // server role: client's channels (name -> version)
    internal readonly Dictionary<ushort, string> ChannelNames = new();                    // client role: id -> name
    internal readonly Dictionary<uint, Reassembly> Partial = new();
    internal uint NextSeq;
    internal int HelloAttempts;
    internal DateTime NextHelloAt, LastHelloAt;
    internal DateTime? InGameSince;
    internal DateTime? HeldSince;  // server role: its Gameplay Ready waits in the gate (docs/kiln/network.md)
    internal DateTime? DefinitionsSince;  // server role: Kiln-only entities wait for its content ack
    internal bool Cleared;                // server role: kiln.net sends it Kiln-only entities (withhold_set off)
    internal uint ClearedTemplates;       // ... of the Kiln-only templates there were at its last clear
    internal DateTime? KickAt;
    internal double TokensMsgs, TokensBytes;
    internal DateTime TokensAt = DateTime.UtcNow;
    internal readonly Queue<DateTime> RecentDrops = new();
    internal uint PingNonce;
    internal DateTime PingSentAt;

    internal sealed class Reassembly(ushort channel, ushort message, int total, int count)
    {
        public readonly ushort Channel = channel, Message = message;
        public readonly byte[] Buffer = new byte[total];
        public readonly bool[] Have = new bool[count];
        public int Received, Filled;
        public readonly DateTime Started = DateTime.UtcNow;
    }

    public override string ToString() =>
        IsServer ? $"server ({State})" : $"{(Name.Length > 0 ? Name : $"peer 0x{Id:x}")} ({State}{(Player.IsNone ? "" : $", entity {Player}")})";
}

/// <summary>Who sent a message and how to answer.</summary>
public sealed class NetContext
{
    internal NetContext(NetChannel channel, NetPeer sender, NetDirection direction)
    {
        Channel = channel;
        Sender = sender;
        Direction = direction;
    }

    public NetChannel Channel { get; }

    /// <summary>The client (server side) or the server (client side).</summary>
    public NetPeer Sender { get; }

    public NetDirection Direction { get; }

    /// <summary>The sending player's entity (server side).</summary>
    public EntityId Player => Sender.Player;

    /// <summary>Sends <paramref name="message"/> back to the sender on the same channel.</summary>
    public bool Reply<T>(T message) where T : notnull => Channel.SendTo(Sender, message);
}

/// <summary>
/// A named, versioned message channel (Forge's SimpleChannel). Create with <see cref="Network.Channel"/>; register
/// message types with <c>Register</c>; send with <c>SendToServer</c> / <c>SendTo</c> / <c>SendToAll</c> /
/// <c>SendNear</c>. The channel belongs to the mod that created it and closes when that mod unloads.
/// </summary>
/// <remarks>
/// <para>Both sides must create the channel with the same name and version; a peer without it (or with another
/// version) gets nothing, and sends to it return false. <see cref="Required"/> channels count in the mod sync check.</para>
/// <para>Threads: server side, handlers run on the tick thread and sends must be made there; client side, handlers
/// run on the main thread (the client world's tick) and <c>SendToServer</c> may be called from any thread.</para>
/// </remarks>
public sealed class NetChannel : IDisposable
{
    internal NetChannel(string name, string version, bool required, ModContext? owner, int maxMessageBytes)
    {
        Name = name;
        Version = version;
        Required = required;
        Owner = owner;
        MaxMessageBytes = maxMessageBytes;
    }

    public string Name { get; }
    public string Version { get; }
    public bool Required { get; }

    /// <summary>Largest message on this channel (bytes, after encoding); never above the server's limit.</summary>
    public int MaxMessageBytes { get; }

    internal ModContext? Owner { get; }
    internal ushort ServerId;  // id the server assigned (server role)
    internal bool Closed;
    internal readonly Dictionary<ushort, MessageType> ById = new();
    internal readonly Dictionary<Type, MessageType> ByType = new();
    internal NetRawHandler? Raw;
    private ushort _nextMessage;

    internal sealed class MessageType
    {
        public required ushort Id;
        public required Type Type;
        public required Action<object, NetWriter> Encode;
        public required Func<ReadOnlyMemory<byte>, object> Decode;
        public required Action<object, NetContext>? Handler;
        public required NetDirection Direction;
    }

    public long MessagesIn, MessagesOut, BytesIn, BytesOut, DecodeErrors;

    /// <summary>Registers message type <typeparamref name="T"/> under <paramref name="id"/> with explicit codecs.</summary>
    public NetChannel Register<T>(ushort id, Action<T, NetWriter> encode, NetDecoder<T> decode,
        Action<T, NetContext>? handler = null, NetDirection direction = NetDirection.Both) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(encode);
        ArgumentNullException.ThrowIfNull(decode);
        lock (NetEngine.Lock)
        {
            if (ById.ContainsKey(id)) throw new ArgumentException($"channel {Name}: message id {id} is taken");
            if (ByType.ContainsKey(typeof(T))) throw new ArgumentException($"channel {Name}: {typeof(T).Name} is registered");
            var mt = new MessageType
            {
                Id = id,
                Type = typeof(T),
                Encode = (o, w) => encode((T)o, w),
                Decode = m =>
                {
                    var r = new NetReader(m.Span);
                    return decode(ref r);
                },
                Handler = handler is null ? null : (o, c) => handler((T)o, c),
                Direction = direction,
            };
            ById[id] = mt;
            ByType[typeof(T)] = mt;
            if (id >= _nextMessage) _nextMessage = (ushort)(id + 1);
        }
        return this;
    }

    /// <summary>Registers <typeparamref name="T"/> (its own Encode/Decode) under the next free id. Both sides must
    /// register their types in the same order (or pass explicit ids).</summary>
    public NetChannel Register<T>(Action<T, NetContext>? handler = null, NetDirection direction = NetDirection.Both)
        where T : INetMessage<T>
    {
        ushort id;
        lock (NetEngine.Lock) id = _nextMessage;
        return Register(id, handler, direction);
    }

    /// <summary>Registers <typeparamref name="T"/> (its own Encode/Decode) under <paramref name="id"/>.</summary>
    public NetChannel Register<T>(ushort id, Action<T, NetContext>? handler, NetDirection direction = NetDirection.Both)
        where T : INetMessage<T> =>
        Register<T>(id, static (m, w) => m.Encode(w), static (ref NetReader r) => T.Decode(ref r), handler, direction);

    /// <summary>Registers <typeparamref name="T"/> as JSON (System.Text.Json, reflection) under the next free id.
    /// Convenient, larger and slower than a binary codec. After the mod unloads, System.Text.Json's process-wide
    /// accessor cache keeps its types for a second or two (hot reload then reports the context as alive until it
    /// expires); use the <c>JsonTypeInfo</c> overload to avoid that.</summary>
    public NetChannel RegisterJson<T>(Action<T, NetContext>? handler = null, NetDirection direction = NetDirection.Both)
        where T : notnull
    {
        ushort id;
        lock (NetEngine.Lock) id = _nextMessage;
        // Options of its own: System.Text.Json caches type metadata per options instance, and the shared default
        // instance would keep the mod's types (and its load context) alive after the mod unloads.
        var options = new JsonSerializerOptions();
        return Register<T>(id, (m, w) => w.Raw(JsonSerializer.SerializeToUtf8Bytes(m, options)),
            // TrimEnd: kiln.net pads game-transport frames with up to 3 zero bytes; JSON never ends in NUL
            (ref NetReader r) => JsonSerializer.Deserialize<T>(r.Rest().TrimEnd((byte)0), options) ?? throw new NetFormatException("null JSON message"),
            handler, direction);
    }

    /// <summary>Registers <typeparamref name="T"/> as JSON with source-generated metadata (a
    /// <c>JsonSerializerContext</c>'s <c>JsonTypeInfo</c>) under the next free id. Prefer this over the reflection
    /// overload in mods that hot-reload: System.Text.Json's reflection path caches member accessors process-wide for a
    /// moment after use, which keeps an unloaded mod's load context alive until that cache expires.</summary>
    public NetChannel RegisterJson<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        Action<T, NetContext>? handler = null, NetDirection direction = NetDirection.Both) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        ushort id;
        lock (NetEngine.Lock) id = _nextMessage;
        return Register<T>(id, (m, w) => w.Raw(JsonSerializer.SerializeToUtf8Bytes(m, typeInfo)),
            (ref NetReader r) => JsonSerializer.Deserialize(r.Rest().TrimEnd((byte)0), typeInfo) ?? throw new NetFormatException("null JSON message"),
            handler, direction);
    }

    /// <summary>Receives every message of this channel whose id has no registered type (raw bytes).</summary>
    public NetChannel OnRaw(NetRawHandler handler)
    {
        Raw = handler;
        return this;
    }

    // ---- sending ----

    /// <summary>Client side: sends to the server. False when not connected to a Kiln server that has this channel.</summary>
    public bool SendToServer<T>(T message) where T : notnull => NetEngine.SendToServer(this, Encode(message));

    /// <summary>Sends to one peer (server side: a client; client side: <see cref="Network.Server"/>).</summary>
    public bool SendTo<T>(NetPeer peer, T message) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(peer);
        return peer.IsServer ? SendToServer(message) : NetEngine.SendToPeer(this, peer, Encode(message));
    }

    /// <summary>Server side: sends to the player <paramref name="player"/>. False when they have no Kiln or lack the channel.</summary>
    public bool SendTo<T>(EntityId player, T message) where T : notnull =>
        Network.PeerOf(player) is { } p && NetEngine.SendToPeer(this, p, Encode(message));

    /// <summary>Server side: sends to every Kiln client that has this channel. Returns how many got it.</summary>
    public int SendToAll<T>(T message, NetPeer? except = null) where T : notnull
    {
        var e = Encode(message);
        var n = 0;
        foreach (var p in Network.Peers)
            if (p != except && p.State == NetPeerState.Ready && NetEngine.SendToPeer(this, p, e)) n++;
        return n;
    }

    /// <summary>Server side: sends to Kiln clients whose player is within <paramref name="radius"/> metres of
    /// <paramref name="position"/>. Tick thread (reads positions).</summary>
    public int SendNear<T>(WorldPosition position, double radius, T message, NetPeer? except = null) where T : notnull
    {
        Services.RequireTickThread("NetChannel.SendNear");
        var e = Encode(message);
        var n = 0;
        foreach (var p in Network.Peers)
        {
            if (p == except || p.State != NetPeerState.Ready || p.Player.IsNone) continue;
            WorldPosition? pos;
            try
            {
                pos = Players.PositionOf(p.Player);
            }
            catch (Exception)
            {
                pos = null;
            }
            if (pos is { } at && at.DistanceTo(position) <= radius && NetEngine.SendToPeer(this, p, e)) n++;
        }
        return n;
    }

    /// <summary>Sends raw bytes as message <paramref name="messageId"/> (for peers using <see cref="OnRaw"/>).</summary>
    public bool SendRaw(NetPeer peer, ushort messageId, ReadOnlySpan<byte> body) =>
        peer.IsServer
            ? NetEngine.SendToServer(this, new NetEngine.Encoded(messageId, body.ToArray()))
            : NetEngine.SendToPeer(this, peer, new NetEngine.Encoded(messageId, body.ToArray()));

    internal NetEngine.Encoded Encode<T>(T message) where T : notnull
    {
        MessageType? mt;
        lock (NetEngine.Lock) ByType.TryGetValue(message.GetType(), out mt);
        if (mt is null) throw new ArgumentException($"channel {Name}: {message.GetType().Name} is not registered");
        var w = new NetWriter();
        mt.Encode(message, w);
        if (w.Length > MaxMessageBytes) throw new ArgumentException($"channel {Name}: {message.GetType().Name} is {w.Length} bytes (limit {MaxMessageBytes})");
        return new NetEngine.Encoded(mt.Id, w.ToArray());
    }

    /// <summary>Closes the channel (also done when the owning mod unloads).</summary>
    public void Dispose() => NetEngine.Close(this);

    public override string ToString() => $"{Name} v{Version}";
}

/// <summary>Counters of Kiln.Network and its transport.</summary>
public readonly record struct NetworkStatus(
    bool Available, bool ServerRole, bool ClientRole, bool LabTransport, int Peers, int ReadyPeers, int Channels,
    uint MaxFrame, long FramesIn, long FramesOut, long BytesIn, long BytesOut, long Dropped, long SendFailures,
    string? ConfigError);

/// <summary>Kiln's mod networking: channels, peers, the join handshake and mod sync.</summary>
public static class Network
{
    /// <summary>The transport (kiln.net) works on this build.</summary>
    public static bool Available => NetNative.Available;

    /// <summary>Creates (or returns the same mod's existing) channel <paramref name="name"/>. Use a name prefixed with
    /// your mod id ("mymod:main"). <paramref name="required"/>: a client lacking this channel counts as lacking the
    /// mod in the mod sync check.</summary>
    public static NetChannel Channel(string name, string version = "1", bool required = true, int maxMessageBytes = 0) =>
        NetEngine.Open(name, version, required, maxMessageBytes);

    /// <summary>Server side: connected clients (Kiln or vanilla), a snapshot.</summary>
    public static IReadOnlyList<NetPeer> Peers => NetEngine.ServerPeers();

    /// <summary>Client side: the server connection, null when not in a game.</summary>
    public static NetPeer? Server => NetEngine.ServerLink;

    /// <summary>Client side: connected to a server that speaks Kiln (handshake done).</summary>
    public static bool ServerHasKiln => NetEngine.ServerLink is { State: NetPeerState.Ready };

    /// <summary>Server side: the peer of a player (null when not connected).</summary>
    public static NetPeer? PeerOf(EntityId player) => NetEngine.PeerOfPlayer(player);

    /// <summary>The channels open in this process.</summary>
    public static IReadOnlyList<NetChannel> Channels => NetEngine.AllChannels();

    /// <summary>The settings in use (<c>kiln/config/network.json</c>).</summary>
    public static NetworkConfig Config => NetEngine.Config;

    /// <summary>Re-reads <c>kiln/config/network.json</c>.</summary>
    public static void ReloadConfig() => NetEngine.ReloadConfig();

    /// <summary>A value exchanged in the handshake (e.g. a content hash); the other side sees it in
    /// <see cref="NetPeer.Properties"/>. The provider runs when the handshake is built.</summary>
    public static void SetHandshakeProperty(string key, Func<string> provider) => NetEngine.SetProperty(key, provider);

    public static NetworkStatus Status => NetEngine.Status();

    // ---- server modpacks (docs/kiln/network.md "Server modpacks") ----

    /// <summary>Server side: this server's stable id (network.json <c>serverId</c>, else
    /// <c>kiln/data/kiln-network/server-id</c>, generated once).</summary>
    public static string ServerId => NetAdvertise.ServerId(NetEngine.Config);

    /// <summary>Server side: the name sent to clients (network.json <c>serverName</c>, else enshrouded_server.json).</summary>
    public static string? ServerName => NetAdvertise.ServerName(NetEngine.Config);

    /// <summary>Server side: what REJECT/WELCOME advertise: id, name, modpack and the mods players need (manifest
    /// <c>clients: "mod"</c>, with the kdh1 of their folders; <see cref="NetServerAdvert.Pending"/> until hashed).</summary>
    public static NetServerAdvert Advertised => NetEngine.Advertised();

    /// <summary>Server side: hash the required mods again now (they are refreshed when the mod list changes and every
    /// 60 s anyway).</summary>
    public static void RefreshRequired() => NetAdvertise.Refresh();

    /// <summary>Client side: what the server sent in its REJECT/WELCOME (null before an answer).</summary>
    public static NetServerAdvert? ServerAdvert => NetEngine.ServerAdvert;

    /// <summary>The last network.json load error (null when it loaded or is absent).</summary>
    public static string? ConfigError => NetEngine.ConfigError;

    // ---- client-side events (main thread). Server-side events are on the event bus (NetClientHandshakeEvent ...). ----

    /// <summary>Client: the server answered the handshake (Kiln channels usable).</summary>
    public static event Action<NetPeer>? ServerReady
    {
        add => NetEngine.ServerReadyHandlers.Add(value);
        remove => NetEngine.ServerReadyHandlers.Remove(value);
    }

    /// <summary>Client: the server refused this client (reason, details).</summary>
    public static event Action<string, IReadOnlyList<string>>? Rejected
    {
        add => NetEngine.RejectedHandlers.Add(value);
        remove => NetEngine.RejectedHandlers.Remove(value);
    }

    /// <summary>Client: the server has no Kiln (no answer to the handshake).</summary>
    public static event Action<NetPeer>? ServerVanilla
    {
        add => NetEngine.ServerVanillaHandlers.Add(value);
        remove => NetEngine.ServerVanillaHandlers.Remove(value);
    }

    /// <summary>Client: the connection to the server is gone.</summary>
    public static event Action<NetPeer>? Disconnected
    {
        add => NetEngine.DisconnectedHandlers.Add(value);
        remove => NetEngine.DisconnectedHandlers.Remove(value);
    }
}
