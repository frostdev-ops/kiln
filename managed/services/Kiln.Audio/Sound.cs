// SPDX-License-Identifier: MIT
// Kiln.Audio: sounds and music from mods (Forge: SoundEvent registry + Level.playSound / SoundManager.play). Native:
// kiln.audio (native/core/abi/services/kiln_audio.h). Docs: docs/kiln/services/audio.md.
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A cue: a SoundContainerResource GUID (takes, volume, loop, 3D distances), vanilla or a mod's.</summary>
public readonly record struct SoundId(Guid Cue)
{
    public static readonly SoundId None = default;
    public bool IsNone => Cue == Guid.Empty;

    /// <summary>Any cue by GUID (vanilla or modded).</summary>
    public static SoundId Of(Guid cue) => new(cue);

    /// <summary>Any cue by GUID text.</summary>
    public static SoundId Of(string cue) => new(Guid.Parse(cue));

    /// <summary>The calling mod's cue <paramref name="name"/> (its cook recipe's <c>cue</c> step): the GUID from
    /// <c>assets/cooked/audio.json</c>, else UUIDv5(the recipe's guidNamespace, "cue:" + name).</summary>
    public static SoundId Named(ModContext mod, string name)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return new(CookIndex.For(mod.Info.Id, mod.Info.Directory).CueGuid(name));
    }

    /// <summary>Another loaded mod's cue (e.g. a sound pack's), by mod id and cue name.</summary>
    public static SoundId Named(string modId, string name) => new(AudioCore.IndexOf(modId).CueGuid(name));

    public override string ToString() => Cue.ToString();
}

/// <summary>A voice-over line: a VoiceoverResource GUID (one cue per voice language: en, de, fr, es, zh).</summary>
public readonly record struct VoiceoverId(Guid Value)
{
    public static readonly VoiceoverId None = default;
    public bool IsNone => Value == Guid.Empty;
    public static VoiceoverId Of(Guid line) => new(line);
    public static VoiceoverId Of(string line) => new(Guid.Parse(line));

    /// <summary>The calling mod's voice-over <paramref name="name"/> (its recipe's <c>voiceover</c> step).</summary>
    public static VoiceoverId Named(ModContext mod, string name)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return new(CookIndex.For(mod.Info.Id, mod.Info.Directory).VoiceoverGuid(name));
    }

    /// <summary>Another loaded mod's voice-over line.</summary>
    public static VoiceoverId Named(string modId, string name) => new(AudioCore.IndexOf(modId).VoiceoverGuid(name));

    public override string ToString() => Value.ToString();
}

/// <summary>The game's sound scenes (keen::SoundSceneType). Each has its own voices and the player's volume slider.</summary>
public enum SoundScene : uint
{
    Music = 0,
    Ui = 1,
    /// <summary>The world: positional sounds.</summary>
    Game = 2,
    Ambience = 3,
    /// <summary>Lore narration (2 voices; ducks music and game sounds). Use <see cref="Voiceover.Narrate"/>.</summary>
    LoreVoiceover = 6,
}

/// <summary>Where a sound stands.</summary>
public enum SoundState
{
    /// <summary>Unknown or expired handle (finished handles are kept for at least 60 s).</summary>
    None = 0,
    /// <summary>Queued: starts on the game's main thread within a frame (FOLLOW waits for a client step).</summary>
    Pending = 1,
    /// <summary>The game's instance exists. Looping cues stay here until <see cref="Sound.Stop"/>.</summary>
    Playing = 2,
    /// <summary>Ended, stopped or silent (see <see cref="SoundInfo.Reason"/>).</summary>
    Stopped = 3,
    /// <summary>Never started (see <see cref="SoundInfo.Reason"/>).</summary>
    Failed = 4,
}

/// <summary>Why a sound is <see cref="SoundState.Stopped"/> or <see cref="SoundState.Failed"/>.</summary>
public enum SoundEndReason
{
    None = 0, Finished = 1, Stopped = 2, EntityGone = 3, StartFailed = 4, NoSoundSystem = 5, TimedOut = 6, Replaced = 7,
    /// <summary>Voice-over: the line has no take for the player's voice language (the mod's cooked data).</summary>
    Silent = 8,
    /// <summary>Narration: the lore queue did not take it (auto-play "never", queue full).</summary>
    Refused = 9,
    WorldEnded = 10, NotFound = 11,
    /// <summary>A server-relayed handle: no state comes back from clients.</summary>
    Remote = 100,
}

/// <summary>A playing (or finished) sound. Kiln's id: never reused, safe to keep.</summary>
public readonly record struct SoundHandle(uint Value)
{
    public static readonly SoundHandle None = default;
    public bool IsNone => Value == 0;

    /// <summary>A server-side handle (the sound was relayed to Kiln clients).</summary>
    public bool IsRemote => (Value & AudioCore.RemoteBit) != 0;

    public override string ToString() => IsRemote ? $"remote#{Value & ~AudioCore.RemoteBit}" : $"#{Value}";
}

/// <summary>Details of a sound (client side).</summary>
public sealed record SoundInfo(
    SoundHandle Handle, SoundState State, SoundEndReason Reason, SoundScene Scene, uint Instance, int Thread,
    EntityId Entity, bool Following, bool Unmatched, string? VoiceLanguage, TimeSpan? StartDelay, TimeSpan? Played,
    WorldPosition Position);

/// <summary>kiln.audio's state (client side).</summary>
public sealed record AudioStatus(
    bool Available, bool SoundSystem, bool Voiceover, bool Narration, bool VoiceoverManager, bool Frames,
    int MainThread, uint VoiceLanguageId, string VoiceLanguage, float VoiceoverVolume, uint LoreMode, uint LoreQueued,
    uint Live, ulong Played, ulong Failed, ulong GameStarts, ulong GameStartsOffMainThread, uint GameThreads,
    IReadOnlyList<uint> Scenes);

/// <summary>
/// Play sounds and music: 2D (UI), at a world position, attached to an entity, or in Kiln's music slot.
/// </summary>
/// <remarks>
/// <para><b>Client</b> (the game client, any thread): the call returns a handle at once; the sound starts on the game's
/// main thread within a frame. <see cref="IsPlaying"/> is true from the call until a one-shot ends (or
/// <see cref="Stop"/>); a looping cue plays until stopped. Volumes are 0..1 and multiply the cue's own volume and the
/// player's volume slider for the scene.</para>
/// <para><b>Server</b> (dedicated server, or code on a hosting client's server-world tick thread): <see cref="PlayAt"/>,
/// <see cref="PlayOn"/>, <see cref="PlayFor"/>, <see cref="PlayMusic"/> and <see cref="Stop"/> are relayed to Kiln
/// clients over the channel <c>kiln:audio</c> (vanilla clients hear nothing new); a host's own client plays them
/// directly. The returned handle is <see cref="SoundHandle.IsRemote"/>: it can be stopped, not observed.</para>
/// <para>A mod's sounds stop when it unloads. Cues of a mod come from its cooked audio (<c>tools/soundcook</c>,
/// <c>assets/assets.json</c>); see <see cref="SoundId.Named(ModContext, string)"/>.</para>
/// </remarks>
public static unsafe class Sound
{
    /// <summary>Client: kiln.audio runs and the game's sound system is captured. Server: at least one Kiln client has
    /// the relay channel (otherwise nobody would hear anything).</summary>
    public static bool Available => AudioCore.ServerSide ? AudioRelay.HasListeners : AudioCore.LocalAvailable;

    /// <summary>A sound without a position (UI style). <paramref name="scene"/> null: the cue's cooked default
    /// (playScene), else <see cref="SoundScene.Ui"/>.</summary>
    public static SoundHandle Play2D(SoundId cue, float volume = 1f, SoundScene? scene = null)
    {
        var s = scene ?? CookIndex.Cue(cue.Cue)?.DefaultScene ?? SoundScene.Ui;
        if (s == SoundScene.Game) s = SoundScene.Ui;  // a Game-scene sound without a position would sit at the origin
        if (AudioCore.ServerSide) return AudioRelay.Play(AudioRelay.KindAll2D, cue, s, default, EntityId.None, volume, 0, 0, EntityId.None);
        return AudioCore.PlayLocal(cue, s, 0, EntityId.None, default, volume, 0);
    }

    /// <summary>A positional sound at <paramref name="at"/> (Game scene). Server: relayed to Kiln clients within
    /// <paramref name="radius"/> metres (0 = the cue's max distance, else 60 m).</summary>
    public static SoundHandle PlayAt(SoundId cue, WorldPosition at, float volume = 1f, float radius = 0)
    {
        if (AudioCore.ServerSide)
            return AudioRelay.Play(AudioRelay.KindAt, cue, SoundScene.Game, at, EntityId.None, volume, AudioCore.Radius(cue, radius), 0, EntityId.None);
        return AudioCore.PlayLocal(cue, SoundScene.Game, AudioNative.At, EntityId.None, at, volume, 0);
    }

    /// <summary>A positional sound that follows <paramref name="entity"/> (client: a client-world entity; on a host the
    /// ids equal the server's). It stops when the entity is gone. Server: relayed with the entity's id and position;
    /// a client follows its entity of the same id when that one is within 10 m of the sent position, else plays at the
    /// position (see docs/kiln/services/audio.md "Entity ids").</summary>
    public static SoundHandle PlayOn(SoundId cue, EntityId entity, float volume = 1f)
    {
        if (entity.IsNone) throw new ArgumentException("no entity", nameof(entity));
        if (AudioCore.ServerSide)
        {
            Services.RequireTickThread("Sound.PlayOn (server)");
            var at = World.Transform(entity)?.Position ?? throw new ArgumentException($"entity {entity} has no position", nameof(entity));
            return AudioRelay.Play(AudioRelay.KindOn, cue, SoundScene.Game, at, entity, volume, AudioCore.Radius(cue, 0), 0, EntityId.None);
        }
        return AudioCore.PlayLocal(cue, SoundScene.Game, AudioNative.Follow, entity, default, volume, 0);
    }

    /// <summary>Server: a 2D sound for one player (their Kiln client). Client: plays only when
    /// <paramref name="player"/> is the local player.</summary>
    public static SoundHandle PlayFor(EntityId player, SoundId cue, float volume = 1f)
    {
        var s = CookIndex.Cue(cue.Cue)?.DefaultScene ?? SoundScene.Ui;
        if (s == SoundScene.Game) s = SoundScene.Ui;
        if (AudioCore.ServerSide) return AudioRelay.Play(AudioRelay.Kind2D, cue, s, default, EntityId.None, volume, 0, 0, player);
        return player == Client.LocalPlayer ? AudioCore.PlayLocal(cue, s, 0, EntityId.None, default, volume, 0) : SoundHandle.None;
    }

    /// <summary>Music (the Music scene, the player's music slider) in Kiln's music slot: starting one stops the previous
    /// Kiln track (fading over <paramref name="fadeIn"/>, at least 250 ms). Use a cue cooked with <c>loop</c> for a
    /// track that repeats. Server: relayed to every Kiln client.</summary>
    public static SoundHandle PlayMusic(SoundId cue, float volume = 1f, TimeSpan fadeIn = default)
    {
        var fade = Math.Max(0, fadeIn.Ticks * 100);
        if (AudioCore.ServerSide) return AudioRelay.Play(AudioRelay.KindMusic, cue, SoundScene.Music, default, EntityId.None, volume, 0, fade, EntityId.None);
        return AudioCore.PlayLocal(cue, SoundScene.Music, AudioNative.MusicSlot, EntityId.None, default, volume, fade);
    }

    /// <summary>Stops a sound (any thread on a client; tick thread for server handles). <paramref name="fade"/> 0 =
    /// at once; the game's own sounds use 20-100 ms.</summary>
    public static void Stop(SoundHandle h, TimeSpan fade = default)
    {
        if (h.IsNone) return;
        if (h.IsRemote)
        {
            AudioRelay.Stop(h, fade);
            return;
        }
        var t = AudioNative.T;
        if (t != null) t->Stop(h.Value, Math.Max(0, fade.Ticks * 100));
    }

    /// <summary>Changes a playing sound's volume (0..1), optionally fading.</summary>
    public static void SetVolume(SoundHandle h, float volume, TimeSpan fade = default)
    {
        var t = AudioNative.T;
        if (t == null || h.IsNone || h.IsRemote) return;
        t->SetVolume(h.Value, Math.Clamp(volume, 0f, 1f), Math.Max(0, fade.Ticks * 100));
    }

    /// <summary>Moves a playing sound (ends following an entity).</summary>
    public static void SetPosition(SoundHandle h, WorldPosition at)
    {
        var t = AudioNative.T;
        if (t == null || h.IsNone || h.IsRemote) return;
        var v = new AudioNative.Vec3(at);
        t->SetPosition(h.Value, &v);
    }

    /// <summary>True while the sound is queued or playing (false once a one-shot ended, after <see cref="Stop"/>, for
    /// unknown handles, and for server handles).</summary>
    public static bool IsPlaying(SoundHandle h) => State(h) is SoundState.Pending or SoundState.Playing;

    /// <summary>The handle's state (client).</summary>
    public static SoundState State(SoundHandle h)
    {
        var t = AudioNative.T;
        if (t == null || h.IsNone || h.IsRemote) return SoundState.None;
        return (SoundState)t->State(h.Value);
    }

    /// <summary>Details of a handle (client), null when unknown.</summary>
    public static SoundInfo? Info(SoundHandle h)
    {
        var t = AudioNative.T;
        if (t == null || h.IsNone || h.IsRemote) return null;
        var i = new AudioNative.Info { Size = (uint)sizeof(AudioNative.Info) };
        if (t->GetInfo(h.Value, &i) != AudioNative.Ok) return null;
        return new SoundInfo(h, (SoundState)i.State, (SoundEndReason)i.Reason, (SoundScene)i.Scene, i.Instance,
            (int)i.Thread, new EntityId(i.Entity), (i.Flags & 1) != 0, (i.Flags & 2) != 0,
            i.LanguageSlot < 5 ? CookIndex.Slots[i.LanguageSlot] : null,
            i.StartedMs != 0 ? TimeSpan.FromMilliseconds(i.StartedMs - i.QueuedMs) : null,
            i.StartedMs != 0 ? TimeSpan.FromMilliseconds((i.EndedMs != 0 ? i.EndedMs : (ulong)Environment.TickCount64) - i.StartedMs) : null,
            i.Position.ToPosition());
    }

    /// <summary>Stops every sound the calling mod started (client).</summary>
    public static int StopAll(TimeSpan fade = default)
    {
        var t = AudioNative.T;
        if (t == null) return 0;
        return (int)t->StopOwner(Services.CallerOwner, Math.Max(0, fade.Ticks * 100));
    }

    /// <summary>Makes the vanilla (or any) cue <paramref name="cue"/> play <paramref name="replacement"/>'s takes and
    /// settings: the replacement's SoundContainerResource bytes are served for the cue (Kiln.Assets override, owned by
    /// the calling mod). Applies when the game next loads the cue; for cues the game has loaded already, put the
    /// override in assets.json instead (boot time). False when kiln.assets is off or the replacement is unknown.</summary>
    public static bool Replace(SoundId cue, SoundId replacement)
    {
        if (!Assets.Available || cue.IsNone || replacement.IsNone) return false;
        var bytes = Assets.ReadResource(new AssetKey(replacement.Cue, AudioCore.ContainerType));
        if (bytes is null) return false;
        Assets.Override(new AssetKey(cue.Cue, AudioCore.ContainerType), bytes);
        AudioLog.Info($"cue {cue} now plays {replacement}'s takes ({bytes.Length} B container)");
        return true;
    }

    /// <summary>Undoes <see cref="Replace"/> for <paramref name="cue"/> (the calling mod's override).</summary>
    public static void Restore(SoundId cue)
    {
        if (Assets.Available && !cue.IsNone) Assets.RemoveResource(new AssetKey(cue.Cue, AudioCore.ContainerType));
    }

    /// <summary>What a mod's cook says about a cue (null: not a cooked cue of a loaded mod, e.g. vanilla).</summary>
    public static CueInfo? Describe(SoundId cue)
    {
        if (CookIndex.Cue(cue.Cue) is null) CookIndex.ScanLoadedMods();
        return CookIndex.Cue(cue.Cue);
    }

    /// <summary>kiln.audio's state (client), null on a server or unsupported build.</summary>
    public static AudioStatus? Status
    {
        get
        {
            var t = AudioNative.T;
            if (t == null) return null;
            var s = new AudioNative.Status { Size = (uint)sizeof(AudioNative.Status) };
            if (t->GetStatus(&s) != AudioNative.Ok) return null;
            var scenes = new uint[7];
            for (var i = 0; i < 7; i++) scenes[i] = s.Scenes[i];
            return new AudioStatus((s.Flags & AudioNative.HasPlay) != 0, (s.Flags & AudioNative.System) != 0,
                (s.Flags & AudioNative.HasVoiceover) != 0, (s.Flags & AudioNative.HasNarration) != 0,
                (s.Flags & AudioNative.Manager) != 0, (s.Flags & AudioNative.Frames) != 0, (int)s.MainThread,
                s.VoiceLanguage, AudioCore.LanguageKey(s.VoiceLanguage), s.VoiceVolume, s.LoreMode, s.LoreQueued, s.Live,
                s.Played, s.Failed, s.GameStarts, s.GameStartsOffMain, s.GameThreads, scenes);
        }
    }

    /// <summary>Diagnostics as JSON (status, recent handles, the game's own SoundStart threads).</summary>
    public static string StatusJson()
    {
        var t = AudioNative.T;
        if (t == null) return "{\"available\":false}";
        nuint n = 0;
        t->StatusJson(null, 0, &n);
        var buf = new byte[(int)n + 1];
        fixed (byte* p = buf)
            if (t->StatusJson(p, (nuint)buf.Length, &n) != AudioNative.Ok) return "{}";
        return System.Text.Encoding.UTF8.GetString(buf, 0, (int)n);
    }
}

/// <summary>Shared plumbing of Sound / Voiceover / the relay.</summary>
internal static unsafe class AudioCore
{
    public const uint RemoteBit = 0x8000_0000;
    public static readonly uint ContainerType = Assets.TypeHash("keen::SoundContainerResource");
    private static readonly object Lock = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);

    static AudioCore() => AudioStartup.Ensure();

    /// <summary>Client playback works (kiln.audio up and the sound system captured).</summary>
    public static bool LocalAvailable
    {
        get
        {
            var t = AudioNative.T;
            if (t == null) return false;
            var s = new AudioNative.Status { Size = (uint)sizeof(AudioNative.Status) };
            return t->GetStatus(&s) == AudioNative.Ok && (s.Flags & AudioNative.System) != 0;
        }
    }

    /// <summary>The call is server code: a dedicated server, or a hosting client's server thread (anything but the
    /// game's main thread). Such calls relay to Kiln clients (and play on the host's own client).</summary>
    /// <summary>Whether a call here is server code. On a hosting client only the server world's tick thread is: the client
    /// thread, the main thread and thread-pool continuations play on the host's own client (a thread-pool call used to count
    /// as server code and threw on the calls that need the tick thread).</summary>
    public static bool ServerSide => Client.IsDedicatedServer || (Client.IsHosting && Game.IsTickThread && !Client.IsClientThread);

    public static CookIndex IndexOf(string modId)
    {
        var m = Mods.All.FirstOrDefault(x => x.Id == modId) ?? throw new ArgumentException($"mod '{modId}' is not loaded", nameof(modId));
        return CookIndex.For(m.Id, m.Directory);
    }

    public static float Radius(SoundId cue, float radius) =>
        radius > 0 ? radius : (float)(CookIndex.Cue(cue.Cue)?.MaxDistance ?? 60);

    public static ulong Owner()
    {
        var mod = ModContext.CurrentMod;
        if (mod is null) return 0;
        lock (Lock)
        {
            if (Watched.Add(mod))
                mod.Retiring += () =>
                {
                    var t = AudioNative.T;
                    if (t != null)
                    {
                        var n = t->StopOwner(mod.Owner, 50_000_000);
                        if (n > 0) AudioLog.Info($"{mod.Info.Id} unloaded: {n} sound(s) stopped");
                    }
                    Subtitles.Retire(mod);
                    lock (Lock) Watched.Remove(mod);
                };
        }
        return mod.Owner;
    }

    public static SoundHandle PlayLocal(SoundId cue, SoundScene scene, uint flags, EntityId entity, WorldPosition at,
        float volume, long fadeNs, float verifyRadius = 0)
    {
        var t = AudioNative.T;
        if (t == null || cue.IsNone) return SoundHandle.None;
        var p = new AudioNative.Play
        {
            Size = (uint)sizeof(AudioNative.Play),
            Flags = flags,
            Scene = (uint)scene,
            Entity = entity.Value,
            Position = new AudioNative.Vec3(at),
            Volume = float.IsFinite(volume) ? Math.Clamp(volume, 0f, 1f) : 1f,
            VerifyRadius = verifyRadius,
            FadeInNs = fadeNs,
        };
        AudioNative.WriteGuid(cue.Cue, p.Cue);
        return new SoundHandle(t->Play(Owner(), &p));
    }

    public static string LanguageKey(uint languageId) => languageId switch
    {
        0 => "",
        3 => "de",
        5 => "fr",
        7 => "es",
        20 => "zh",
        _ => "en",
    };
}
