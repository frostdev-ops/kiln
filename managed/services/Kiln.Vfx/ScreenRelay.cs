// SPDX-License-Identifier: MIT
// Screen effects from a server: the Kiln channel "kiln:screen" v1 (required: false, so it never blocks a join; vanilla clients have no channel and
// see nothing). Each play carries the whole effect, so a client needs Kiln, not the mod that played it. A hosting client draws its own player's.
// A client stops what the server started when it leaves the server, so nothing a server started outlives the session.
namespace Kiln;

internal static unsafe class ScreenRelay
{
    public const string ChannelName = "kiln:screen";
    public const string ChannelVersion = "1";
    public const uint RemoteBit = 0x8000_0000;
    public const byte ToAll = 0, ToPlayer = 1, ToNear = 2;

    private const ushort MsgPlay = 1, MsgStop = 2, MsgStrength = 3;
    private const int Fields_ = 24;  // the effect after its size, in the header's order

    internal sealed record PlayMsg(uint Token, float[] Effect);
    internal sealed record StopMsg(uint Token, bool Immediate);
    internal sealed record StrengthMsg(uint Token, float Strength);

    private sealed class Sent_(ModContext? owner, double? ends)
    {
        public ModContext? Owner { get; } = owner;
        public double? Ends { get; } = ends;
        public ScreenHandle Host;
    }

    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static uint _nextToken;
    private static readonly Dictionary<uint, Sent_> Tokens = new();               // server: what it started and hasn't stopped
    private static readonly Dictionary<uint, ScreenHandle> Received = new();      // client: server token -> what plays here
    private static long _sent, _received;
    private static IDisposable? _tick;

    public static bool Open => _channel is not null;
    public static long Sent => Interlocked.Read(ref _sent);
    public static long ReceivedCount => Interlocked.Read(ref _received);

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
                    .Register<PlayMsg>(MsgPlay, static (m, w) => WriteEffect(w.UInt32(m.Token), m.Effect), static (ref NetReader r) => new PlayMsg(r.UInt32(), ReadEffect(ref r)),
                        static (m, ctx) => { if (!FromOwnServer()) OnPlay(m, ctx); }, NetDirection.ToClient)
                    .Register<StopMsg>(MsgStop, static (m, w) => w.UInt32(m.Token).Bool(m.Immediate), static (ref NetReader r) => new StopMsg(r.UInt32(), r.Bool()),
                        static (m, ctx) => { if (!FromOwnServer()) OnStop(m, ctx); }, NetDirection.ToClient)
                    .Register<StrengthMsg>(MsgStrength, static (m, w) => w.UInt32(m.Token).Single(m.Strength), static (ref NetReader r) => new StrengthMsg(r.UInt32(), r.Single()),
                        static (m, ctx) => { if (!FromOwnServer()) OnStrength(m, ctx); }, NetDirection.ToClient);
                _tick ??= ServiceTick.Add("kiln screen relay", static (in TickInfo t) => Sweep(t.Step));
            }
            catch (Exception e)
            {
                ScreenLog.Warn($"relay channel {ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    // ---- the wire: the native effect's fields, in the header's order ----

    internal static float[] Fields(ScreenNative.Effect n)
    {
        var f = new float[Fields_];
        f[0] = BitConverter.Int32BitsToSingle(n.Order);
        f[1] = n.Strength; f[2] = n.FadeIn; f[3] = n.Seconds; f[4] = n.FadeOut; f[5] = n.Exposure; f[6] = n.Contrast; f[7] = n.Saturation; f[8] = n.Gamma;
        f[9] = n.Temperature;
        for (var i = 0; i < 4; i++)
        {
            f[10 + i] = n.Tint[i];
            f[14 + i] = n.Vignette[i];
            f[20 + i] = n.Fill[i];
        }
        f[18] = n.VignetteRadius;
        f[19] = n.VignetteSoftness;
        return f;
    }

    private static ScreenNative.Effect ToNative(float[] f)
    {
        var n = new ScreenNative.Effect
        {
            Size = (uint)sizeof(ScreenNative.Effect), Order = BitConverter.SingleToInt32Bits(f[0]), Strength = f[1], FadeIn = f[2], Seconds = f[3], FadeOut = f[4],
            Exposure = f[5], Contrast = f[6], Saturation = f[7], Gamma = f[8], Temperature = f[9], VignetteRadius = f[18], VignetteSoftness = f[19],
        };
        for (var i = 0; i < 4; i++)
        {
            n.Tint[i] = f[10 + i];
            n.Vignette[i] = f[14 + i];
            n.Fill[i] = f[20 + i];
        }
        return n;
    }

    private static NetWriter WriteEffect(NetWriter w, float[] f)
    {
        foreach (var x in f) w.Single(x);
        return w;
    }

    private static float[] ReadEffect(ref NetReader r)
    {
        var f = new float[Fields_];
        for (var i = 0; i < Fields_; i++) f[i] = r.Single();
        return f;
    }

    // ---- server side (tick thread) ----

    private static uint NextToken()
    {
        lock (Lock)
        {
            _nextToken = (_nextToken + 1) & ~RemoteBit;
            if (_nextToken == 0) _nextToken = 1;
            return _nextToken | RemoteBit;
        }
    }

    private static double Now => Environment.TickCount64 / 1000.0;

    public static ScreenHandle Play(ScreenEffect effect, byte to, EntityId player, WorldPosition at, float radius)
    {
        Start();
        var owner = ScreenEffects.Caller();
        var token = NextToken();
        var fields = Fields(ScreenNative.From(effect));
        var sent = new Sent_(owner, effect.Length is { } l ? Now + l : null);
        var n = 0;
        if (_channel is { } ch)
        {
            Services.RequireTickThread("ScreenEffects (server relay)");
            var m = new PlayMsg(token, fields);
            n = to switch
            {
                ToPlayer => ch.SendTo(player, m) ? 1 : 0,
                ToNear => ch.SendNear(at, radius, m),
                _ => ch.SendToAll(m),
            };
            Interlocked.Add(ref _sent, n);
        }
        // a hosting client plays it here, at once; the copy the channel sends its own player is dropped on arrival (FromOwnServer)
        var host = false;
        if (Client.IsHosting && Client.HasLocalPlayer)
        {
            host = to switch
            {
                ToPlayer => player == Client.LocalPlayer,
                ToNear => World.Position(Client.LocalPlayer) is not { } p || Math.Sqrt((p.X - at.X) * (p.X - at.X) + (p.Y - at.Y) * (p.Y - at.Y) + (p.Z - at.Z) * (p.Z - at.Z)) <= radius,
                _ => true,
            };
            if (host) sent.Host = ScreenEffects.PlayLocal(effect, owner?.Owner ?? 0);
        }
        lock (Lock) Tokens[token] = sent;
        ScreenLog.Debug($"relay {(to == ToPlayer ? $"for {player}" : to == ToNear ? $"near {at} ({radius} m)" : "to all")} -> {n} Kiln client(s){(host ? " + the host" : "")} (token {token & ~RemoteBit})");
        return new ScreenHandle(token);
    }

    public static void Stop(uint token, bool immediate)
    {
        Sent_? s;
        lock (Lock) Tokens.Remove(token, out s);
        if (s?.Host is { IsNone: false } host) ScreenEffects.Stop(host, immediate);
        if (_channel is { } ch)
        {
            Services.RequireTickThread("ScreenEffects.Stop (server relay)");
            Interlocked.Add(ref _sent, ch.SendToAll(new StopMsg(token, immediate)));
        }
    }

    public static void SetStrength(uint token, float strength)
    {
        Sent_? s;
        lock (Lock) Tokens.TryGetValue(token, out s);
        if (s?.Host is { IsNone: false } host) ScreenEffects.SetStrength(host, strength);
        if (_channel is { } ch)
        {
            Services.RequireTickThread("ScreenEffects.SetStrength (server relay)");
            Interlocked.Add(ref _sent, ch.SendToAll(new StrengthMsg(token, strength)));
        }
    }

    public static bool IsPlaying(uint token)
    {
        lock (Lock) return Tokens.TryGetValue(token, out var s) && (s.Ends is not { } end || end > Now);
    }

    /// <summary>A mod unloads: what it started on clients stops too.</summary>
    public static void Retire(ModContext mod)
    {
        List<uint> mine;
        lock (Lock) mine = Tokens.Where(kv => ReferenceEquals(kv.Value.Owner, mod)).Select(kv => kv.Key).ToList();
        if (mine.Count == 0) return;
        try
        {
            foreach (var t in mine) Stop(t, false);
        }
        catch (Exception e)
        {
            ScreenLog.Warn($"{mod.Info.Id} unloaded: its relayed screen effects could not be stopped here: {e.Message}");
            lock (Lock) foreach (var t in mine) Tokens.Remove(t);
        }
    }

    private static void Sweep(ulong step)
    {
        if (step % 60 != 7) return;
        lock (Lock)
        {
            // server: what ran out on its own needs no stop
            foreach (var k in Tokens.Where(kv => kv.Value.Ends is { } end && end + 1 < Now).Select(kv => kv.Key).ToList()) Tokens.Remove(k);
            // client: the server it came from is gone
            if (Received.Count == 0 || Network.Server is not null || Client.IsHosting) return;
        }
        List<ScreenHandle> left;
        lock (Lock)
        {
            left = Received.Values.ToList();
            Received.Clear();
        }
        foreach (var h in left) ScreenEffects.Stop(h, immediate: true);
        ScreenLog.Info($"left the server: {left.Count} screen effect(s) it started stopped");
    }

    // ---- client side ----

    /// <summary>A hosting client is connected only to its own server, which has already played the effect here when its player was meant to
    /// see it: its own player is a network peer as well, so the message would play it a second time.</summary>
    private static bool FromOwnServer()
    {
        if (!Client.IsHosting) return false;
        Interlocked.Increment(ref _received);
        return true;
    }

    private static void OnPlay(PlayMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        var t = ScreenNative.T;
        if (t == null || !ScreenEffects.LocalAvailable) return;
        var n = ToNative(m.Effect);
        var h = new ScreenHandle(t->Play(0, &n));
        lock (Lock)
        {
            if (Received.Count > 256)
                foreach (var k in Received.Where(kv => t->IsPlaying(kv.Value.Value) == 0).Select(kv => kv.Key).ToList()) Received.Remove(k);
            if (!h.IsNone) Received[m.Token] = h;
        }
        ScreenLog.Debug($"server screen effect {m.Token & ~RemoteBit} -> {h}");
    }

    private static void OnStop(StopMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        ScreenHandle h;
        lock (Lock) Received.Remove(m.Token, out h);
        ScreenEffects.Stop(h, m.Immediate);
    }

    private static void OnStrength(StrengthMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        ScreenHandle h;
        lock (Lock) Received.TryGetValue(m.Token, out h);
        if (!h.IsNone && float.IsFinite(m.Strength)) ScreenEffects.SetStrength(h, Math.Clamp(m.Strength, 0, 1));
    }

    // ---- test seam ----

    public static float[] RoundTrip(ScreenEffect effect)
    {
        var w = new NetWriter();
        WriteEffect(w.UInt32(1 | RemoteBit), Fields(ScreenNative.From(effect)));
        var r = new NetReader(w.WrittenSpan);
        r.UInt32();
        return Fields(ToNative(ReadEffect(ref r)));
    }
}
