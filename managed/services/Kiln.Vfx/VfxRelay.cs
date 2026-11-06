// SPDX-License-Identifier: MIT
// Kiln.Vfx's server -> client relay: the Kiln channel "kiln:vfx" v1 (required: false, so it never blocks a join; vanilla clients have no
// channel and see nothing new). The server has no VFX: server-side calls become messages to the Kiln clients that should see them,
// and a hosting client plays them itself. A client without the VFX (a mod's, not installed there) ignores it: the guard's rule.
//
// Entity ids: as in Kiln.Audio. On a client joined to a remote server the ids aren't proven to match, so PlayOn sends the entity's id AND
// its position: the client attaches to its entity of that id only when it stands within 10 m of the position (KILN_VFX_VERIFY), else plays at
// the position. Not for joiners: data routes (entities, sequences, buffs) are what reach them (docs/kiln/services/vfx.md).
using System.Numerics;

namespace Kiln;

internal static class VfxRelay
{
    public const string ChannelName = "kiln:vfx";
    public const string ChannelVersion = "1";

    // PlayMsg.Kind
    public const byte KindAt = 1, KindOn = 2, KindFor = 3;

    private const ushort MsgPlay = 1, MsgStop = 2, MsgParam = 3;
    private const float VerifyRadius = 10f;
    private const float DefaultRadius = 120f;  // most VFX cull at 75 m (docs/re/vfx.md section 2)

    internal sealed record PlayMsg(byte Kind, Guid Vfx, WorldPosition At, Quaternion Rotation, float Scale, EntityId Entity, byte Slot,
        Vector3 Local, Vector3 World, Quaternion OffsetRotation, long LifetimeNs, uint Token, IReadOnlyList<VfxParam> Params);
    internal sealed record StopMsg(uint Token, bool Immediate);
    internal sealed record ParamMsg(uint Token, VfxParam Param);

    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static uint _nextToken;
    private static readonly Dictionary<uint, VfxHandle> HostLocal = new();   // server token -> the host's own VFX
    private static readonly Dictionary<uint, VfxHandle> Received = new();    // client: server token -> local VFX
    private static readonly Dictionary<uint, ModContext?> Loops = new();     // server: tokens with no end of their own, by owner
    private static long _sent, _received;

    public static bool Open => _channel is not null;
    public static long Sent => Interlocked.Read(ref _sent);
    public static long ReceivedCount => Interlocked.Read(ref _received);

    /// <summary>A Kiln client with the relay channel is connected (server side), or this host's own client can play.</summary>
    public static bool HasListeners
    {
        get
        {
            if (_channel is null) return Client.IsHosting && VfxCore.LocalAvailable;
            try
            {
                return (Client.IsHosting && VfxCore.LocalAvailable) ||
                       Network.Peers.Any(p => p.State == NetPeerState.Ready && p.HasChannel(ChannelName));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Opens the channel (Kiln's own, not the calling mod's). Idempotent; fails soft without kiln.net.</summary>
    public static void Start()
    {
        lock (Lock)
        {
            if (_channel is not null || !Network.Available) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                _channel = Network.Channel(ChannelName, ChannelVersion, required: false)
                    .Register<PlayMsg>(MsgPlay, EncodePlay, DecodePlay, OnPlay, NetDirection.ToClient)
                    .Register<StopMsg>(MsgStop, static (m, w) => w.UInt32(m.Token).Bool(m.Immediate),
                        static (ref NetReader r) => new StopMsg(r.UInt32(), r.Bool()), OnStop, NetDirection.ToClient)
                    .Register<ParamMsg>(MsgParam, static (m, w) => WriteParam(w.UInt32(m.Token), m.Param),
                        static (ref NetReader r) => new ParamMsg(r.UInt32(), ReadParam(ref r)), OnParam, NetDirection.ToClient);
            }
            catch (Exception e)
            {
                VfxLog.Warn($"relay channel {ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    private static NetWriter WriteQuat(NetWriter w, Quaternion q) => w.Single(q.X).Single(q.Y).Single(q.Z).Single(q.W);
    private static Quaternion ReadQuat(ref NetReader r) => new(r.Single(), r.Single(), r.Single(), r.Single());
    private static NetWriter WriteVec(NetWriter w, Vector3 v) => w.Single(v.X).Single(v.Y).Single(v.Z);
    private static Vector3 ReadVec(ref NetReader r) => new(r.Single(), r.Single(), r.Single());
    private static NetWriter WriteParam(NetWriter w, VfxParam p) => w.UInt32(p.Hash).UInt32(p.Type).Bytes(p.Data);
    private static VfxParam ReadParam(ref NetReader r) => new(r.UInt32(), r.UInt32(), r.Bytes(16));

    private static void EncodePlay(PlayMsg m, NetWriter w)
    {
        w.Byte(m.Kind).Guid(m.Vfx).Position(m.At);
        WriteQuat(w, m.Rotation);
        w.Single(m.Scale).Entity(m.Entity).Byte(m.Slot);
        WriteVec(w, m.Local);
        WriteVec(w, m.World);
        WriteQuat(w, m.OffsetRotation);
        w.Int64(m.LifetimeNs).UInt32(m.Token).Byte((byte)m.Params.Count);
        foreach (var p in m.Params) WriteParam(w, p);
    }

    private static PlayMsg DecodePlay(ref NetReader r)
    {
        var kind = r.Byte();
        var vfx = r.Guid();
        var at = r.Position3();
        var rot = ReadQuat(ref r);
        var scale = r.Single();
        var entity = r.Entity();
        var slot = r.Byte();
        var local = ReadVec(ref r);
        var world = ReadVec(ref r);
        var orot = ReadQuat(ref r);
        var life = r.Int64();
        var token = r.UInt32();
        var n = Math.Min((int)r.Byte(), 16);
        var ps = new List<VfxParam>(n);
        for (var i = 0; i < n; i++) ps.Add(ReadParam(ref r));
        return new PlayMsg(kind, vfx, at, rot, scale, entity, slot, local, world, orot, life, token, ps);
    }

    private static uint NextToken()
    {
        lock (Lock)
        {
            _nextToken = (_nextToken + 1) & ~VfxCore.RemoteBit;
            if (_nextToken == 0) _nextToken = 1;
            return _nextToken | VfxCore.RemoteBit;
        }
    }

    // ---- server side (tick thread) ----

    public static VfxHandle Play(byte kind, Guid vfx, WorldPosition at, Quaternion? rotation, float scale, EntityId entity, Slot slot, Offset offset,
        long lifetimeNs, VfxParams? parameters, float radius, EntityId player)
    {
        Start();
        var token = NextToken();
        var ps = parameters?.Items.Take(16).ToList() ?? [];
        var m = new PlayMsg(kind, vfx, at, rotation ?? Quaternion.Identity, scale, entity, (byte)slot, offset.Local, offset.World,
            offset.Rotation ?? Quaternion.Identity, lifetimeNs, token, ps);
        var n = 0;
        if (_channel is { } ch)
        {
            Services.RequireTickThread("Vfx (server relay)");
            n = kind == KindFor ? (ch.SendTo(player, m) ? 1 : 0)
                : radius < 0 ? ch.SendToAll(m)
                : ch.SendNear(at, radius > 0 ? radius : DefaultRadius, m);
            Interlocked.Add(ref _sent, n);
        }
        if (lifetimeNs < 0)
            lock (Lock) Loops[token] = ModContext.CurrentMod;
        // a hosting client sees the server's VFX itself (its player is not a network peer)
        if (Client.IsHosting && VfxCore.LocalAvailable && (kind != KindFor || player == Client.LocalPlayer))
        {
            var local = PlayHere(kind, vfx, at, rotation, scale, entity, slot, offset, lifetimeNs, parameters);
            if (!local.IsNone)
                lock (Lock) HostLocal[token] = local;
        }
        VfxLog.Debug($"relay {KindName(kind)} {vfx} -> {n} Kiln client(s){(Client.IsHosting ? " + the host" : "")} (token {token & ~VfxCore.RemoteBit})");
        return new VfxHandle(token);
    }

    private static VfxHandle PlayHere(byte kind, Guid vfx, WorldPosition at, Quaternion? rotation, float scale, EntityId entity, Slot slot, Offset offset,
        long lifetimeNs, VfxParams? parameters) =>
        kind == KindOn
            ? VfxCore.PlayLocal(vfx, VfxNative.On | VfxNative.At | VfxNative.Verify, at, rotation, scale, entity, slot, offset, lifetimeNs, parameters, VerifyRadius)
            : VfxCore.PlayLocal(vfx, VfxNative.At, at, rotation, scale, EntityId.None, Slot.Root, default, lifetimeNs, parameters);

    public static void Stop(VfxHandle h, bool immediate)
    {
        VfxHandle local;
        lock (Lock)
        {
            HostLocal.Remove(h.Value, out local);
            Loops.Remove(h.Value);
        }
        if (!local.IsNone) Vfx.Stop(local, immediate);
        SendStop(h.Value, immediate);
    }

    private static void SendStop(uint token, bool immediate)
    {
        if (_channel is { } ch && !Client.IsClientThread)
        {
            Services.RequireTickThread("Vfx.Stop (server relay)");
            Interlocked.Add(ref _sent, ch.SendToAll(new StopMsg(token, immediate)));
        }
    }

    public static void Set(VfxHandle h, VfxParam param)
    {
        VfxHandle local;
        lock (Lock) HostLocal.TryGetValue(h.Value, out local);
        if (!local.IsNone) VfxCore.SetLocal(local, param);
        if (_channel is { } ch && !Client.IsClientThread)
        {
            Services.RequireTickThread("Vfx.Set (server relay)");
            Interlocked.Add(ref _sent, ch.SendToAll(new ParamMsg(h.Value, param)));
        }
    }

    /// <summary>A mod unloads: its loops on clients stop too; nothing else would ever end them.</summary>
    public static void Retire(ModContext mod)
    {
        List<uint> tokens;
        lock (Lock)
        {
            tokens = Loops.Where(kv => ReferenceEquals(kv.Value, mod)).Select(kv => kv.Key).ToList();
            foreach (var t in tokens) Loops.Remove(t);
        }
        if (tokens.Count == 0) return;
        try
        {
            foreach (var t in tokens) Stop(new VfxHandle(t), false);
        }
        catch (Exception e)
        {
            VfxLog.Warn($"{mod.Info.Id} unloaded: its relayed loops could not be stopped here: {e.Message}");
        }
    }

    // ---- client side ----

    private static void OnPlay(PlayMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        VfxParams? ps = null;
        if (m.Params.Count > 0)
        {
            ps = new VfxParams();
            ps.Items.AddRange(m.Params);
        }
        var slot = m.Slot < 36 ? (Slot)m.Slot : Slot.Root;
        var h = PlayHere(m.Kind, m.Vfx, m.At, m.Rotation, m.Scale, m.Entity, slot, new Offset(m.Local, m.World, m.OffsetRotation), m.LifetimeNs, ps);
        lock (Lock)
        {
            if (Received.Count > 1024) Prune();
            if (!h.IsNone) Received[m.Token] = h;
        }
        VfxLog.Debug($"server VFX {KindName(m.Kind)} {m.Vfx} -> {h}");
    }

    private static void OnStop(StopMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        VfxHandle h;
        lock (Lock) Received.Remove(m.Token, out h);
        if (!h.IsNone) Vfx.Stop(h, m.Immediate);
    }

    private static void OnParam(ParamMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        VfxHandle h;
        lock (Lock) Received.TryGetValue(m.Token, out h);
        if (!h.IsNone) VfxCore.SetLocal(h, m.Param);
    }

    // ---- test seam: the receive side in one process ----

    public static VfxHandle Loopback(Guid vfx, WorldPosition at, float scale, EntityId entity, Slot slot, Offset offset, long lifetimeNs, VfxParams? parameters)
    {
        var token = NextToken();
        var w = new NetWriter();
        EncodePlay(new PlayMsg(KindOn, vfx, at, Quaternion.Identity, scale, entity, (byte)slot, offset.Local, offset.World, offset.Rotation ?? Quaternion.Identity,
            lifetimeNs, token, parameters?.Items.Take(16).ToList() ?? []), w);
        var r = new NetReader(w.WrittenSpan);
        OnPlay(DecodePlay(ref r), default!);
        lock (Lock) return Received.TryGetValue(token, out var h) ? h : VfxHandle.None;
    }

    public static void LoopbackStop(VfxHandle local)
    {
        uint token;
        lock (Lock) token = Received.FirstOrDefault(kv => kv.Value == local).Key;
        var w = new NetWriter();
        w.UInt32(token).Bool(false);
        var r = new NetReader(w.WrittenSpan);
        OnStop(new StopMsg(r.UInt32(), r.Bool()), default!);
    }

    private static void Prune()
    {
        foreach (var k in Received.Where(kv => !Vfx.IsPlaying(kv.Value)).Select(kv => kv.Key).ToList()) Received.Remove(k);
    }

    private static string KindName(byte k) => k switch { KindAt => "at", KindOn => "on", KindFor => "for-player", _ => "?" };
}

/// <summary>Started by the service autostart (or the first use of Kiln.Vfx): opens the relay channel so a client receives server VFX even
/// when none of its mods touched Kiln.Vfx.</summary>
internal static class VfxStartup
{
    private static int _started;

    static VfxStartup() => Ensure();

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            VfxRelay.Start();
            ScreenRelay.Start();
            ServiceTick.Add("kiln vfx names", VfxNames.Drain);
            unsafe
            {
                VfxLog.Info($"Kiln.Vfx up: {(VfxNative.T != null ? "kiln.vfx present (client playback)" : "no kiln.vfx (server: relay only)")}, " +
                            $"relay channel {(VfxRelay.Open ? VfxRelay.ChannelName + " v" + VfxRelay.ChannelVersion + " open" : "off (no kiln.net)")}");
            }
        }
        catch (Exception e)
        {
            VfxLog.Warn($"startup: {e.Message}");
        }
    }
}
