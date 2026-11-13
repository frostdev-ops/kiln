// SPDX-License-Identifier: MIT
// kiln:rules: the server tells each Kiln player's game its own rules for the client-side hooks (the fly-mode gate and
// the glider in the prediction world, the build cursor). Opened by Kiln itself on both sides (not by a mod), not required: a client
// without it joins as before and gets only the server-side rules.
//
// Messages (channel v4; additions never change the version, which must match exactly): 0 RulesMsg to the client
// (dev.24 appends the glider steering limit; an older client reads the fields it knows, NetChannel ignores the rest),
// 1 FlightCapability to the server, 2 ClientHooks to the server (since dev.24: the predicted hooks this game has; an
// older server has no handler for it and drops it unread; dev.27 adds the NoClip hook's bit, which an older server
// ignores and an older game never sets).
namespace Kiln;

internal static unsafe class RulesNet
{
    private const ushort MsgRules = 0;
    internal const ushort MsgClientHooks = 2;
    internal const string ChannelVersion = "4";  // character flight needs matching prediction hooks, including on older Kiln clients
    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static RulesNativePart _local = RulesNativePart.Default;
    private readonly record struct FlightCapability(bool Enabled);
    private static readonly HashSet<NetPeer> FlightPeers = new();
    private static bool? _flightSent;

    /// <summary>The client's own kiln.rules hooks its prediction applies (KILN_RULES_HOOK_* bits; the server reads
    /// <see cref="RulesNative.HookGlideSteer"/> and, since dev.27, <see cref="RulesNative.HookNoClip"/>).</summary>
    internal readonly record struct ClientHooks(uint Hooks);
    private const uint PredictedHooks = RulesNative.HookGlideSteer | RulesNative.HookNoClip;
    private static readonly HashSet<NetPeer> SteerPeers = new();
    private static readonly HashSet<NetPeer> NoClipPeers = new();  // since dev.27: games that confirmed the collision hook
    private static uint? _hooksSent;
    private static uint _clientHooks;  // this game's hooks as ClientFlight last read them (client tick)

    internal static bool NoClipReady(NetPeer? peer)
    {
        lock (Lock) return peer is { State: NetPeerState.Ready } && NoClipPeers.Contains(peer);
    }

    internal static bool SteerReady(NetPeer? peer)
    {
        lock (Lock) return peer is { State: NetPeerState.Ready } && SteerPeers.Contains(peer);
    }

    internal static void ClientHooksOf(NetPeer peer, uint hooks)
    {
        lock (Lock)
        {
            if ((hooks & RulesNative.HookGlideSteer) != 0 && peer.State == NetPeerState.Ready) SteerPeers.Add(peer);
            else SteerPeers.Remove(peer);
            if ((hooks & RulesNative.HookNoClip) != 0 && peer.State == NetPeerState.Ready) NoClipPeers.Add(peer);
            else NoClipPeers.Remove(peer);
        }
        RulesRuntime.Dirty();
    }

    internal static void WriteClientHooks(ClientHooks m, NetWriter w) => w.UInt32(m.Hooks);
    internal static ClientHooks ReadClientHooks(ref NetReader r) => new(r.UInt32());

    private static void ClientPredicts(in TickInfo _)
    {
        try
        {
            // Sent when it changes: the steering hook is installed at boot or never, NoClip's with character flight's at
            // the first world. ClientFlight read the status this tick already (it costs a lock per slot).
            if (Client.IsHosting) return;
            var hooks = _clientHooks & PredictedHooks;
            if (_hooksSent != hooks && _channel?.SendToServer(new ClientHooks(hooks)) == true) _hooksSent = hooks;
        }
        catch (Exception) { } // no client world or channel yet; retry next client tick
    }

    internal static bool FlightReady(NetPeer? peer)
    {
        lock (Lock) return peer is { State: NetPeerState.Ready } && FlightPeers.Contains(peer);
    }

    internal static void FlightCapabilityOf(NetPeer peer, bool enabled)
    {
        lock (Lock)
        {
            if (enabled && peer.State == NetPeerState.Ready) FlightPeers.Add(peer);
            else FlightPeers.Remove(peer);
        }
        RulesRuntime.Dirty();
    }

    private static void ClientFlight(in TickInfo _)
    {
        try
        {
            if (Client.IsHosting) return;
            _clientHooks = RulesNative.Hooks;
            var enabled = (_clientHooks & RulesNative.HookFlight) != 0 && !CreativeFlame.Running;
            if (_flightSent != enabled && _channel?.SendToServer(new FlightCapability(enabled)) == true) _flightSent = enabled;
        }
        catch (Exception) { } // no client world or channel yet; retry next client tick
    }

    // v2: + the Shroud floor and the glider values; v3 (released in 0.2.0-dev.15): + the glider's climb rate; v4 the same
    // fields, and since dev.24 the glider steering limit at the end (an older server's message ends before it: 0)
    internal readonly record struct RulesMsg(uint Flags, float BuildCost, float BuildRefund, float DigDamage, float ShroudFloor,
        float GlideSink, float GlideThrust, float GlideMaxSpeed, float GlideLift, float GlideCoast, float GlideClimb,
        float GlideSteerLimit = 0f);

    internal static void WriteRules(RulesMsg m, NetWriter w) => w.UInt32(m.Flags).Single(m.BuildCost).Single(m.BuildRefund)
        .Single(m.DigDamage).Single(m.ShroudFloor).Single(m.GlideSink).Single(m.GlideThrust).Single(m.GlideMaxSpeed).Single(m.GlideLift)
        .Single(m.GlideCoast).Single(m.GlideClimb).Single(m.GlideSteerLimit);

    // kiln.net may pad a frame by up to 3 zero bytes: a field is read only when all 4 of its bytes are there
    internal static RulesMsg ReadRules(ref NetReader r) => new(r.UInt32(), r.Single(), r.Single(), r.Single(), r.Single(), r.Single(),
        r.Single(), r.Single(), r.Single(), r.Single(), r.Single(), r.Remaining >= 4 ? r.Single() : 0f);

    internal static RulesMsg MessageOf(RulesNativePart p) => new(p.Flags, p.BuildCost, p.BuildRefund, p.DigDamage, p.ShroudFloor,
        p.GlideSink, p.GlideThrust, p.GlideMaxSpeed, p.GlideLift, p.GlideCoast, p.GlideClimb, p.GlideSteerLimit);

    internal static RulesNativePart PartOf(RulesMsg m) => new(m.Flags, Clean(m.BuildCost), Clean(m.BuildRefund), Clean(m.DigDamage),
        Clean(m.ShroudFloor, 0f), Clean(m.GlideSink), Clean(m.GlideThrust, 0f), Clean(m.GlideMaxSpeed, 0f), Clean(m.GlideLift, 0f),
        Clean(m.GlideCoast, 0f), Clean(m.GlideClimb, 0f), Clean(m.GlideSteerLimit, 0f));

    public static bool Open => _channel is not null;

    /// <summary>The local player's rules as this game applies them (client).</summary>
    public static RulesNativePart Local
    {
        get { lock (Lock) return _local; }
    }

    public static void Start()
    {
        lock (Lock)
        {
            if (_channel is not null || !Network.Available) return;
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;
            try
            {
                _channel = Network.Channel(Rules.ChannelName, ChannelVersion, required: false)
                    .Register<RulesMsg>(MsgRules, static (m, w) => WriteRules(m, w), static (ref NetReader r) => ReadRules(ref r),
                        static (m, _) => OnRules(m), NetDirection.ToClient)
                    .Register<FlightCapability>(1, static (m, w) => w.Bool(m.Enabled),
                        static (ref NetReader r) => new FlightCapability(r.Bool()),
                        static (m, c) => FlightCapabilityOf(c.Sender, m.Enabled), NetDirection.ToServer)
                    .Register<ClientHooks>(MsgClientHooks, static (m, w) => WriteClientHooks(m, w),
                        static (ref NetReader r) => ReadClientHooks(ref r),
                        static (m, c) => ClientHooksOf(c.Sender, m.Hooks), NetDirection.ToServer);
                Network.Disconnected += _ => { _flightSent = null; _hooksSent = null; SetLocal(RulesNativePart.Default); };
                Network.ServerReady += _ => { _flightSent = null; _hooksSent = null; };
                Events.On<NetPeerLeftEvent>(e => { FlightCapabilityOf(e.Peer, false); ClientHooksOf(e.Peer, 0); });
                Events.On<ServerStoppingEvent>(_ => { lock (Lock) { FlightPeers.Clear(); SteerPeers.Clear(); NoClipPeers.Clear(); } });
                Client.Tick += ClientFlight;
                Client.Tick += ClientPredicts;
            }
            catch (Exception e)
            {
                RulesRuntime.Log(LogLevel.Warn, $"channel {Rules.ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    // Client: the server's word on this player's rules. A hosting game applies its own player's rules directly.
    private static void OnRules(RulesMsg m)
    {
        try
        {
            if (Client.IsHosting) return;
        }
        catch (Exception)
        {
            // no kiln.client
        }
        SetLocal(PartOf(m));
    }

    private static float Clean(float v, float fallback = 1f) => float.IsFinite(v) && v >= 0 ? v : fallback;

    public static void SetLocal(RulesNativePart part)
    {
        // A hosting world's tick and the client's disconnect handler can both publish here. Keep the whole snapshot
        // and its native update together so a reader never sees mixed fields and native writes cannot be reordered.
        lock (Lock)
        {
            if (part == _local) return;
            _local = part;
            var t = RulesNative.T;
            if (t == null) return;
            var p = RulesNative.Of(part);
            t->SetLocal(part.IsDefault ? null : &p);
            RulesRuntime.Log(LogLevel.Info, $"local player's rules: flags 0x{part.Flags:x}, build cost x{part.BuildCost}, glide sink x{part.GlideSink}" +
                ((part.Flags & RulesNative.GlideBoost) != 0
                    ? $", boost {part.GlideThrust} m/s² to {part.GlideMaxSpeed} m/s, lift {part.GlideLift}, climb {part.GlideClimb} m/s"
                    : "") +
                ((part.Flags & RulesNative.GlideSteer) != 0 ? $", glider steering up to {part.GlideSteerLimit}°" : "") +
                ((part.Flags & RulesNative.NoClip) != 0 ? ", NoClip" : ""));
        }
    }

    /// <summary>Server: sends a player's rules to their game; false when it has no Kiln with this channel.</summary>
    public static bool Send(EntityId player, RulesNativePart part)
    {
        var ch = _channel;
        if (ch is null) return false;
        try
        {
            return ch.SendTo(player, MessageOf(part));
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Started by Kiln at the first service tick on both sides: every Kiln game has the rules channel, and every
/// world (a dedicated server's, or one a game hosts or plays offline) has <c>/creative</c>.</summary>
internal static class RulesStartup
{
    static RulesStartup()
    {
        try
        {
            RulesNet.Start();
        }
        catch (Exception e)
        {
            RulesRuntime.Log(LogLevel.Warn, $"startup: {e.Message}");
        }
        try
        {
            CreativeNet.Start();
            _ = CreativeFlame.Running;  // logs once when Creative Mode runs here
            CreativeMenu.Start();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"the creative menu is not available: {e.Message}");
        }
        try
        {
            Creative.RegisterCommand();
            NoClip.RegisterCommand();
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or InvalidOperationException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"/creative and /noclip are not available: {e.Message}");
        }
    }
}
