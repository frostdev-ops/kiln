// SPDX-License-Identifier: MIT
// Kiln.Audio's server -> client relay: the Kiln channel "kiln:audio" v1 (required: false, so it never blocks a join;
// vanilla clients have no channel and get nothing). The server has no sound system: server-side Sound / Voiceover
// calls become messages to the Kiln clients that should hear them, and a hosting client plays them itself.
//
// Entity ids. A client's world replicates the server's entities; on a host both worlds use the same ids (EMF; Kiln's
// local-player id works in both). On a client joined to a remote server the ids are not proven to match, so
// PlayOn sends the entity's id AND its position: the client follows its entity of that id only when it stands within
// 10 m of the sent position (kiln.audio KILN_AUDIO_VERIFY), else it plays at the position without following. Voice-
// over lines relayed to a remote client play at the speaker's position (no speaker entity).
namespace Kiln;

internal static class AudioRelay
{
    public const string ChannelName = "kiln:audio";
    public const string ChannelVersion = "1";

    // PlayMsg.Kind
    public const byte Kind2D = 0, KindAt = 1, KindOn = 2, KindMusic = 3, KindAll2D = 4;
    // VoiceMsg.Kind
    public const byte VoiceLine = 1, VoiceNarrate = 2, VoiceStopAll = 3;

    private const ushort MsgPlay = 1, MsgStop = 2, MsgVoice = 3;
    private const float VerifyRadius = 10f;

    internal sealed record PlayMsg(byte Kind, byte Scene, Guid Cue, WorldPosition At, EntityId Entity, float Volume, long FadeNs, uint Token);
    internal sealed record StopMsg(uint Token, long FadeNs);
    internal sealed record VoiceMsg(byte Kind, Guid Line, EntityId Speaker, bool HasPosition, WorldPosition At, uint Token, long FadeNs);

    private static readonly object Lock = new();
    private static NetChannel? _channel;
    private static uint _nextToken;
    private static readonly Dictionary<uint, SoundHandle> HostLocal = new();   // server token -> the host's own sound
    private static readonly Dictionary<uint, SoundHandle> Received = new();    // client: server token -> local sound
    private static long _sent, _received;

    public static bool Open => _channel is not null;
    public static long Sent => Interlocked.Read(ref _sent);
    public static long ReceivedCount => Interlocked.Read(ref _received);

    /// <summary>A Kiln client that has the relay channel is connected (server side).</summary>
    public static bool HasListeners
    {
        get
        {
            if (_channel is null) return Client.IsHosting && AudioCore.LocalAvailable;
            try
            {
                return (Client.IsHosting && AudioCore.LocalAvailable) ||
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
                    .Register<PlayMsg>(MsgPlay, EncodePlay, DecodePlay, static (m, ctx) => { if (!FromOwnServer()) OnPlay(m, ctx); }, NetDirection.ToClient)
                    .Register<StopMsg>(MsgStop, static (m, w) => w.UInt32(m.Token).Int64(m.FadeNs),
                        static (ref NetReader r) => new StopMsg(r.UInt32(), r.Int64()), static (m, ctx) => { if (!FromOwnServer()) OnStop(m, ctx); },
                        NetDirection.ToClient)
                    .Register<VoiceMsg>(MsgVoice, EncodeVoice, DecodeVoice, static (m, ctx) => { if (!FromOwnServer()) OnVoice(m, ctx); }, NetDirection.ToClient);
            }
            catch (Exception e)
            {
                AudioLog.Warn($"relay channel {ChannelName} unavailable: {e.Message}");
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
        }
    }

    private static void EncodePlay(PlayMsg m, NetWriter w) =>
        w.Byte(m.Kind).Byte(m.Scene).Guid(m.Cue).Position(m.At).Entity(m.Entity).Single(m.Volume).Int64(m.FadeNs).UInt32(m.Token);
    private static PlayMsg DecodePlay(ref NetReader r) =>
        new(r.Byte(), r.Byte(), r.Guid(), r.Position3(), r.Entity(), r.Single(), r.Int64(), r.UInt32());
    private static void EncodeVoice(VoiceMsg m, NetWriter w) =>
        w.Byte(m.Kind).Guid(m.Line).Entity(m.Speaker).Bool(m.HasPosition).Position(m.At).UInt32(m.Token).Int64(m.FadeNs);
    private static VoiceMsg DecodeVoice(ref NetReader r) =>
        new(r.Byte(), r.Guid(), r.Entity(), r.Bool(), r.Position3(), r.UInt32(), r.Int64());

    private static uint NextToken()
    {
        lock (Lock)
        {
            _nextToken = (_nextToken + 1) & ~AudioCore.RemoteBit;
            if (_nextToken == 0) _nextToken = 1;
            return _nextToken | AudioCore.RemoteBit;
        }
    }

    // ---- server side (tick thread) ----

    public static SoundHandle Play(byte kind, SoundId cue, SoundScene scene, WorldPosition at, EntityId entity, float volume,
        float radius, long fadeNs, EntityId player)
    {
        if (cue.IsNone) return SoundHandle.None;
        Start();
        var token = NextToken();
        var m = new PlayMsg(kind, (byte)scene, cue.Cue, at, entity, Math.Clamp(volume, 0f, 1f), fadeNs, token);
        var n = 0;
        if (_channel is { } ch)
        {
            Services.RequireTickThread("Sound (server relay)");
            n = kind switch
            {
                KindAt or KindOn => ch.SendNear(at, radius > 0 ? radius : 60, m),
                Kind2D => ch.SendTo(player, m) ? 1 : 0,
                _ => ch.SendToAll(m),
            };
            Interlocked.Add(ref _sent, n);
        }
        // a hosting client plays the server's sounds itself, at once; the copy the channel sends its own player is dropped on arrival (FromOwnServer)
        if (Client.IsHosting && AudioCore.LocalAvailable)
        {
            var local = kind switch
            {
                KindAt => AudioCore.PlayLocal(cue, scene, AudioNative.At, EntityId.None, at, volume, 0),
                KindOn => AudioCore.PlayLocal(cue, scene, AudioNative.Follow | AudioNative.Verify | AudioNative.At, entity, at, volume, 0, VerifyRadius),
                KindMusic => AudioCore.PlayLocal(cue, scene, AudioNative.MusicSlot, EntityId.None, default, volume, fadeNs),
                Kind2D => player == Client.LocalPlayer ? AudioCore.PlayLocal(cue, scene, 0, EntityId.None, default, volume, 0) : SoundHandle.None,
                _ => AudioCore.PlayLocal(cue, scene, 0, EntityId.None, default, volume, 0),
            };
            if (!local.IsNone)
                lock (Lock) HostLocal[token] = local;
        }
        AudioLog.Debug($"relay {KindName(kind)} {cue} -> {n} Kiln client(s){(Client.IsHosting ? " + the host" : "")} (token {token & ~AudioCore.RemoteBit})");
        return new SoundHandle(token);
    }

    public static void Stop(SoundHandle h, TimeSpan fade)
    {
        var fadeNs = Math.Max(0, fade.Ticks * 100);
        SoundHandle local;
        lock (Lock) HostLocal.Remove(h.Value, out local);
        if (!local.IsNone) Sound.Stop(local, fade);
        if (_channel is { } ch && !Client.IsClientThread)
        {
            Services.RequireTickThread("Sound.Stop (server relay)");
            Interlocked.Add(ref _sent, ch.SendToAll(new StopMsg(h.Value, fadeNs)));
        }
    }

    public static SoundHandle Voice(byte kind, VoiceoverId line, EntityId speaker, WorldPosition? at, EntityId player, float radius)
    {
        Start();
        var token = NextToken();
        var m = new VoiceMsg(kind, line.Value, speaker, at is not null, at ?? default, token, 0);
        if (_channel is { } ch)
        {
            Services.RequireTickThread("Voiceover (server relay)");
            var n = !player.IsNone ? (ch.SendTo(player, m) ? 1 : 0)
                : at is { } p && radius > 0 ? ch.SendNear(p, radius, m)
                : ch.SendToAll(m);
            Interlocked.Add(ref _sent, n);
        }
        if (Client.IsHosting && (player.IsNone || player == Client.LocalPlayer))
        {
            var local = kind == VoiceNarrate ? Voiceover.Narrate(line) : Voiceover.PlayLocal(line, speaker, speaker.IsNone ? at : null, null);
            if (!local.IsNone)
                lock (Lock) HostLocal[token] = local;
        }
        return new SoundHandle(token);
    }

    public static unsafe void VoiceStop(TimeSpan fade)
    {
        var fadeNs = Math.Max(0, fade.Ticks * 100);
        if (Client.IsHosting)
        {
            var t = AudioNative.T;
            if (t != null) t->VoiceoverStop(fadeNs);
        }
        if (_channel is { } ch)
        {
            Services.RequireTickThread("Voiceover.Stop (server relay)");
            Interlocked.Add(ref _sent, ch.SendToAll(new VoiceMsg(VoiceStopAll, Guid.Empty, EntityId.None, false, default, 0, fadeNs)));
        }
    }

    // ---- client side (main thread) ----

    /// <summary>A hosting client is connected only to its own server, which has already played the sound here (<see cref="Play"/>, <see cref="Voice"/>):
    /// its own player is a network peer as well, so the message would play it a second time.</summary>
    private static bool FromOwnServer()
    {
        if (!Client.IsHosting) return false;
        Interlocked.Increment(ref _received);
        return true;
    }

    private static void OnPlay(PlayMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        var scene = (SoundScene)m.Scene;
        if (!Enum.IsDefined(scene)) scene = SoundScene.Game;
        var cue = new SoundId(m.Cue);
        var h = m.Kind switch
        {
            KindAt => AudioCore.PlayLocal(cue, scene, AudioNative.At, EntityId.None, m.At, m.Volume, 0),
            KindOn => AudioCore.PlayLocal(cue, scene, AudioNative.Follow | AudioNative.Verify | AudioNative.At, m.Entity, m.At, m.Volume, 0, VerifyRadius),
            KindMusic => AudioCore.PlayLocal(cue, scene, AudioNative.MusicSlot, EntityId.None, default, m.Volume, m.FadeNs),
            _ => AudioCore.PlayLocal(cue, scene, 0, EntityId.None, default, m.Volume, 0),
        };
        lock (Lock)
        {
            if (Received.Count > 1024) Prune();
            if (!h.IsNone) Received[m.Token] = h;
        }
        AudioLog.Debug($"server sound {KindName(m.Kind)} {cue} -> {h}");
    }

    private static void OnStop(StopMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        SoundHandle h;
        lock (Lock) Received.Remove(m.Token, out h);
        if (!h.IsNone) Sound.Stop(h, TimeSpan.FromTicks(m.FadeNs / 100));
    }

    private static void OnVoice(VoiceMsg m, NetContext ctx)
    {
        Interlocked.Increment(ref _received);
        if (m.Kind == VoiceStopAll)
        {
            Voiceover.Stop(TimeSpan.FromTicks(m.FadeNs / 100));
            return;
        }
        var line = new VoiceoverId(m.Line);
        // remote client: the speaker's id is not proven to match here; play at the speaker's position instead
        var h = m.Kind == VoiceNarrate
            ? Voiceover.Narrate(line)
            : Voiceover.PlayLocal(line, EntityId.None, m.HasPosition ? m.At : null, null);
        lock (Lock)
            if (!h.IsNone) Received[m.Token] = h;
    }

    private static void Prune()
    {
        foreach (var k in Received.Where(kv => !Sound.IsPlaying(kv.Value)).Select(kv => kv.Key).ToList()) Received.Remove(k);
    }

    private static string KindName(byte k) => k switch
    {
        Kind2D => "2D-for-player", KindAt => "at", KindOn => "on", KindMusic => "music", KindAll2D => "2D", _ => "?",
    };
}

/// <summary>Started by the service autostart (or the first use of Kiln.Audio): opens the relay channel so a client
/// receives server sounds even when none of its mods touched Kiln.Audio.</summary>
internal static class AudioStartup
{
    private static int _started;

    static AudioStartup() => Ensure();

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            AudioRelay.Start();
            CookIndex.ScanLoadedMods();
            unsafe
            {
                AudioLog.Info($"Kiln.Audio up: {(AudioNative.T != null ? "kiln.audio present (client playback)" : "no kiln.audio (server: relay only)")}, " +
                              $"relay channel {(AudioRelay.Open ? AudioRelay.ChannelName + " v" + AudioRelay.ChannelVersion + " open" : "off (no kiln.net)")}");
            }
        }
        catch (Exception e)
        {
            AudioLog.Warn($"startup: {e.Message}");
        }
    }
}
