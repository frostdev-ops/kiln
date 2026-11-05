// SPDX-License-Identifier: MIT
// Kiln.Audio: voice-over lines (an NPC's voice, with subtitles) and narration (the lore queue), through the game's own
// voice-over manager (docs/re/audio.md 3.3 and 6, docs/kiln/services/audio.md).
namespace Kiln;

/// <summary>
/// Voice-over: <see cref="Play"/> speaks a line on the game's dialogue channel (what NPC dialogue uses: one line at a
/// time, the player's voice-over language and volume, at the speaker); <see cref="Narrate"/> queues a line like a lore
/// reading (LoreVoiceover scene: music and game sounds duck; the player's lore auto-play setting applies).
/// </summary>
/// <remarks>
/// <para>Languages: the game picks the line's take for the player's voice-over language (en, de, fr, es, zh; every
/// other language plays English). Kiln plays exactly what the cooked VoiceoverResource says: an empty slot is silence
/// (<see cref="SoundEndReason.Silent"/>). Whether empty slots get another language's take is the mod's choice at cook
/// time (<c>"fallback"</c> in its recipe).</para>
/// <para>Subtitles: the text in the game's text language, from the call or from the mod's cooked
/// <c>subtitles</c>, shown at the bottom of the screen while the line plays (Kiln HUD).</para>
/// <para>In game only (the manager runs with the client world). Server calls relay to Kiln clients like
/// <see cref="Sound"/>.</para>
/// </remarks>
public static unsafe class Voiceover
{
    /// <summary>Client: the voice-over feature resolved on this build. Server: a Kiln client has the relay channel.</summary>
    public static bool Available => AudioCore.ServerSide ? AudioRelay.HasListeners : HasFeature(AudioNative.HasVoiceover);

    /// <summary>Client: narration (the lore queue) resolved on this build.</summary>
    public static bool NarrationAvailable => AudioCore.ServerSide ? AudioRelay.HasListeners : HasFeature(AudioNative.HasNarration);

    /// <summary>Speaks <paramref name="line"/> as <paramref name="speaker"/> (a client-world entity; the line stays at
    /// it). No speaker: at the local player. Replaces the line playing on the dialogue channel. Server: relayed to Kiln
    /// clients within 40 m of the speaker.</summary>
    public static SoundHandle Play(VoiceoverId line, EntityId speaker = default, UiText? subtitle = null)
    {
        if (line.IsNone) return SoundHandle.None;
        if (AudioCore.ServerSide)
        {
            Services.RequireTickThread("Voiceover.Play (server)");
            var at = speaker.IsNone ? (WorldPosition?)null : World.Transform(speaker)?.Position;
            return AudioRelay.Voice(AudioRelay.VoiceLine, line, speaker, at, EntityId.None, 40);
        }
        return PlayLocal(line, speaker, null, subtitle);
    }

    /// <summary>Speaks <paramref name="line"/> at a position (no speaker entity).</summary>
    public static SoundHandle PlayAt(VoiceoverId line, WorldPosition at, UiText? subtitle = null)
    {
        if (line.IsNone) return SoundHandle.None;
        if (AudioCore.ServerSide) return AudioRelay.Voice(AudioRelay.VoiceLine, line, EntityId.None, at, EntityId.None, 40);
        return PlayLocal(line, EntityId.None, at, subtitle);
    }

    /// <summary>Server: speaks the line for one player's Kiln client (speaker optional).</summary>
    public static SoundHandle PlayFor(EntityId player, VoiceoverId line, EntityId speaker = default)
    {
        if (line.IsNone || player.IsNone) return SoundHandle.None;
        if (!AudioCore.ServerSide) return player == Client.LocalPlayer ? PlayLocal(line, speaker, null, null) : SoundHandle.None;
        Services.RequireTickThread("Voiceover.PlayFor (server)");
        var at = speaker.IsNone ? (WorldPosition?)null : World.Transform(speaker)?.Position;
        return AudioRelay.Voice(AudioRelay.VoiceLine, line, speaker, at, player, 0);
    }

    /// <summary>Stops the line Kiln is playing on the dialogue channel (not the game's own dialogue). Server: relayed
    /// to every Kiln client.</summary>
    public static void Stop(TimeSpan fade = default)
    {
        if (AudioCore.ServerSide)
        {
            AudioRelay.VoiceStop(fade);
            return;
        }
        var t = AudioNative.T;
        if (t != null) t->VoiceoverStop(Math.Max(0, fade.Ticks * 100));
    }

    /// <summary>Narrates <paramref name="line"/> through the lore queue (as a lore reading or journal entry): it plays
    /// at once or after the current lore line, per the player's setting. Lines meant for narration carry durations
    /// (the cook's <c>narration</c> flag). <paramref name="nameLoca"/>/<paramref name="descriptionLoca"/>: loca tag ids
    /// the game's lore UI may show (0 = none). Server: relayed to every Kiln client.</summary>
    public static SoundHandle Narrate(VoiceoverId line, UiText? subtitle = null, uint nameLoca = 0, uint descriptionLoca = 0)
    {
        if (line.IsNone) return SoundHandle.None;
        if (AudioCore.ServerSide) return AudioRelay.Voice(AudioRelay.VoiceNarrate, line, EntityId.None, null, EntityId.None, 0);
        var t = AudioNative.T;
        if (t == null) return SoundHandle.None;
        var g = stackalloc byte[16];
        AudioNative.WriteGuid(line.Value, g);
        var h = new SoundHandle(t->Narrate(AudioCore.Owner(), g, nameLoca, descriptionLoca));
        if (!h.IsNone) Subtitles.Show(h, line, subtitle);
        return h;
    }

    /// <summary>Server: narrates the line for one player's Kiln client.</summary>
    public static SoundHandle NarrateFor(EntityId player, VoiceoverId line)
    {
        if (line.IsNone || player.IsNone) return SoundHandle.None;
        if (!AudioCore.ServerSide) return player == Client.LocalPlayer ? Narrate(line) : SoundHandle.None;
        return AudioRelay.Voice(AudioRelay.VoiceNarrate, line, EntityId.None, null, player, 0);
    }

    /// <summary>True while the line is queued or playing (<see cref="Sound.IsPlaying"/>).</summary>
    public static bool IsPlaying(SoundHandle h) => Sound.IsPlaying(h);

    /// <summary>What a mod's cook says about a line (languages, subtitles), null when not a cooked line of a loaded mod.</summary>
    public static VoiceoverInfo? Describe(VoiceoverId line)
    {
        if (CookIndex.Voiceover(line.Value) is null) CookIndex.ScanLoadedMods();
        return CookIndex.Voiceover(line.Value);
    }

    internal static SoundHandle PlayLocal(VoiceoverId line, EntityId speaker, WorldPosition? at, UiText? subtitle)
    {
        var t = AudioNative.T;
        if (t == null) return SoundHandle.None;
        var g = stackalloc byte[16];
        AudioNative.WriteGuid(line.Value, g);
        AudioNative.Vec3 v = at is { } p ? new AudioNative.Vec3(p) : default;
        var h = new SoundHandle(t->VoiceoverPlay(AudioCore.Owner(), g, speaker.Value, at is null ? null : &v));
        if (!h.IsNone) Subtitles.Show(h, line, subtitle);
        return h;
    }

    private static bool HasFeature(uint flag)
    {
        var t = AudioNative.T;
        if (t == null) return false;
        var s = new AudioNative.Status { Size = (uint)sizeof(AudioNative.Status) };
        return t->GetStatus(&s) == AudioNative.Ok && (s.Flags & flag) != 0;
    }
}

/// <summary>Subtitle lines on the Kiln HUD while a voice-over handle plays.</summary>
internal static class Subtitles
{
    private sealed class Line(SoundHandle handle, HudLine hud, ModContext? mod, long minUntil)
    {
        public readonly SoundHandle Handle = handle;
        public readonly HudLine Hud = hud;
        public readonly ModContext? Mod = mod;
        public readonly long MinUntil = minUntil;  // keep at least this long after the start (short or silent lines)
        public readonly long Created = Environment.TickCount64;
        public long StartedAt;
    }

    private static readonly object Lock = new();
    private static readonly List<Line> Lines = new();
    private static Timer? _timer;
    private static int _seq;

    public static void Show(SoundHandle h, VoiceoverId line, UiText? text)
    {
        var info = Voiceover.Describe(line);
        text ??= info is { Subtitles.Count: > 0 } ? new UiText(info.Subtitles) : null;
        if (text is null || !Ui.Available) return;
        var t = text;
        var id = $"kiln.audio.subtitle.{Interlocked.Increment(ref _seq)}";
        HudLine hud;
        try
        {
            hud = Hud.Show(id, () => t.ToString(), HudAnchor.BottomCenter, Color.White);
        }
        catch (Exception e)
        {
            AudioLog.Warn($"subtitle not shown: {e.Message}");
            return;
        }
        var keepMs = Math.Max(1500, info?.MaxDurationMs ?? 0);
        lock (Lock)
        {
            Lines.Add(new Line(h, hud, ModContext.CurrentMod, keepMs));
            _timer ??= new Timer(_ => Sweep(), null, 200, 200);
        }
        AudioLog.Info($"subtitle for {h}: \"{t}\"");
    }

    private static void Sweep()
    {
        List<Line> done = new();
        lock (Lock)
        {
            var now = Environment.TickCount64;
            foreach (var l in Lines)
            {
                var st = Sound.State(l.Handle);
                if (st == SoundState.Playing && l.StartedAt == 0) l.StartedAt = now;
                var live = st is SoundState.Pending or SoundState.Playing;
                // a line that ended keeps its text until the cooked duration has passed; a silent one (no take for the
                // voice language) shows its text for that long too; a failed one goes at once
                if (st is SoundState.Failed or SoundState.None ||
                    (!live && now - (l.StartedAt != 0 ? l.StartedAt : l.Created) >= l.MinUntil))
                    done.Add(l);
            }
            foreach (var l in done) Lines.Remove(l);
            if (Lines.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
        foreach (var l in done)
        {
            try { l.Hud.Dispose(); } catch { /* the HUD may be gone already */ }
        }
    }

    public static void Retire(ModContext mod)
    {
        List<Line> gone;
        lock (Lock)
        {
            gone = Lines.Where(l => ReferenceEquals(l.Mod, mod)).ToList();
            foreach (var l in gone) Lines.Remove(l);
        }
        foreach (var l in gone)
        {
            try { l.Hud.Dispose(); } catch { }
        }
    }
}
