// SPDX-License-Identifier: MIT
using Kiln;

namespace AudioTest;

/// <summary>
/// kiln.audio / Kiln.Audio test (docs/kiln/services/audio.md). Lines "audio-test: PASS|FAIL|INFO ..." in kiln.log; no
/// audio is recorded: every check reads Kiln's state (handles, the game's instances, the voice-over manager), and the
/// test tones are distinct so a person can confirm them by ear (see the manual test in the doc).
/// <list type="bullet">
/// <item>Client, menu (automatic after load): the sound system is captured; a vanilla UI cue played from OnLoad (main
/// thread) and the fixture beep played from a thread-pool thread go Pending -> Playing -> Stopped(Finished) and were
/// started on the main thread; music loops, its volume fades, a second track replaces it (music slot), Stop ends it;
/// an unknown cue fails soft.</item>
/// <item>Client, in game (automatic, 8 s after the local player appears, once per world): PlayOn the local player (a
/// loop, follows, stays playing until Stop; volume and position calls), PlayAt 10 m east (one-shot), a vanilla loop,
/// voice-over lines (fixture: en/de takes, silence in other voice languages; vanilla villager line), narration
/// (fixture line; a vanilla lore line stopped after 3 s), StopAll, a run-time cue Replace, an unknown voice-over, and
/// the game's own SoundStart calls all on the main thread.</item>
/// <item>Server part (a world this client hosts, or a dedicated server): Sound/Voiceover server calls return remote
/// handles and relay over kiln:audio; on a host the host's own client plays them (Played counter).</item>
/// </list>
/// Console command (kiln/control/console/*.cmd, or chat on a test server): <c>/audiotest status|menu|world|server|
/// play &lt;name|guid&gt; [2d|at|on|music]|vo &lt;name|guid&gt;|narrate &lt;name|guid&gt;|stopall</c>.
/// </summary>
public sealed class AudioTestMod : Mod
{
    private static readonly SoundId VanillaUiConfirm = SoundId.Of("b096387a-cc73-475a-bacd-69ce8cad9fc9");  // sfx_ui_dialog_confirm 0.53 s
    private static readonly SoundId VanillaGrassLoop = SoundId.Of("0851973b-ef97-490c-aaeb-7cc4c476cd50");  // IO_grashopper_loop, 1-15 m
    private static readonly SoundId VanillaReplaceTarget = SoundId.Of("01faaebe-4e09-4455-bed2-7eefb76dad87");  // a vanilla positional loop
    private static readonly VoiceoverId VanillaVillager = VoiceoverId.Of("00a3c13d-c08e-403b-8ef6-1925007416f7");  // villager "surprise", 5 languages
    private static readonly VoiceoverId VanillaLore = VoiceoverId.Of("00391188-81e1-433b-8c6c-1f053c7d0793");  // a lore page, en 8.9 s

    private int _pass, _fail;
    private bool _fixtures;
    private SoundId _uiBeep, _atTone, _loopPulse, _musicChord;
    private VoiceoverId _voLine, _voNarration;
    private uint _worldSuiteSession, _worldSeenSession;
    private long _worldSeenAt;
    private int _running;  // a suite is running
    // The automatic suites are compact (volume at most 0.4, about 2 s per sound: they play on the user's speakers);
    // the "Kiln audio tour" menu entries play the full lengths for the human check.
    private volatile bool _full;
    private WorldPosition? _lastAt;  // where the local player stood when a suite last read it

    private float Vol(float v) => _full ? v : Math.Min(v, 0.4f);
    private int Hold(int compactMs, int fullMs) => _full ? fullMs : compactMs;

    public override void OnLoad()
    {
        Info($"OnLoad: side {Mods.Side}, {(Client.Available && Client.IsClient ? $"client {Client.State}" : "dedicated server")}, " +
             $"thread {Environment.CurrentManagedThreadId}, tick thread {Game.IsTickThread}");
        Commands.Register("audiotest", OnCommand, usage: "status|menu|world|server|play <cue> [2d|at|on|music]|vo <line>|narrate <line>|stopall",
            description: "kiln.audio test");
        try
        {
            _uiBeep = SoundId.Named(Context, "ui_beep");
            _atTone = SoundId.Named(Context, "at_tone");
            _loopPulse = SoundId.Named(Context, "loop_pulse");
            _musicChord = SoundId.Named(Context, "music_chord");
            _voLine = VoiceoverId.Named(Context, "vo_line");
            _voNarration = VoiceoverId.Named(Context, "vo_narration");
            _fixtures = File.Exists(Path.Combine(Context.Info.Directory, "assets", "cooked", "audio.json"));
            Check(_fixtures, $"the fixture tones are cooked (assets/cooked/audio.json): ui_beep {_uiBeep}, vo_line {_voLine}");
        }
        catch (Exception e)
        {
            Check(false, $"fixture names resolve: {e.Message} (run mods/tests/audio-test/build.sh)");
        }

        if (Client.Available && Client.IsClient)
        {
            var st = Sound.Status;
            if (!Check(st is { Available: true }, "kiln.audio runs on the client"))
                return;
            // the first play straight from OnLoad: the mod loads on the game's UI (main) thread in the menu
            var first = Sound.Play2D(VanillaUiConfirm, 0.4f);
            Check(!first.IsNone, $"Play2D from OnLoad returns a handle at once: {first} (state {Sound.State(first)})");
            Client.Tick += OnClientTick;
            // for the manual check (docs/kiln/services/audio.md "Manual test"): the tour plays every test sound
            // again with a caption at the top of the screen naming what should be heard
            Ui.Menus.Add(MenuLocation.MainMenu, "Kiln audio tour", () => Start(MenuSuite, full: true), order: 20);
            Ui.Menus.Add(MenuLocation.PauseMenu, "Kiln audio tour", () => Start(WorldTour, full: true), order: 20);
            Start(() => MenuSuite(first));
        }
        else
        {
            Info("dedicated server: the server part runs at ServerStarted (relay checks)");
        }
        if (Lifecycle.IsServerRunning) Context.Scheduler.Post(ServerPart);
    }

    public override void OnUnload() => Info($"OnUnload: summary: {_pass} PASS, {_fail} FAIL");

    [Subscribe]
    private void OnServerStarted(ServerStartedEvent e)
    {
        // A hosted server world starts about 10 s before the game has loaded it, under the loading screen. Nobody is in the world, the
        // client's voice-over manager isn't running, and the voice-over the relay asks for times out at the load ("voice-over ...
        // FAILED: timed out", once per hosted world): wrong moment for this test, not a bug of the relay. So wait for the host's client.
        if (Client.Available && Client.IsHosting) _ = ServerPartWhenHostIsIn(++_serverPartWait);
        else ServerPart();
    }

    private int _serverPartWait;

    private async Task ServerPartWhenHostIsIn(int wait)
    {
        var t0 = Environment.TickCount64;
        while (Client.LocalPlayer.IsNone)
        {
            if (wait != _serverPartWait) return;  // another world started meanwhile
            if (Environment.TickCount64 - t0 > 180_000)
            {
                Info("server part: the host's client didn't reach the world in 3 min: skipped");
                return;
            }
            await Context.Scheduler.NextTick();
        }
        if (wait == _serverPartWait) ServerPart();
    }

    // ---- menu (client) ----

    private Task MenuSuite() => MenuSuite(Sound.Play2D(VanillaUiConfirm, Vol(0.8f)));

    private async Task WorldTour()
    {
        await WorldSuite();
        if (Mods.IsLoaded("soundsample")) await SoundSampleSuite();
        Caption("");
    }

    private async Task MenuSuite(SoundHandle first)
    {
        Caption(Mods.IsLoaded("soundsample")
            ? "audio-test: dialog confirm, replaced by SoundSample: 1661 Hz (0.4 s)"
            : "audio-test: the game's dialog-confirm click");
        Check(await Until(() => Sound.Status?.SoundSystem == true, 10_000), "the game's sound system and scenes are captured (boot hook)");
        var st = Sound.Status!;
        Info($"status: scenes [{string.Join(",", st.Scenes)}], main thread {st.MainThread}, voice-over {st.Voiceover}, narration {st.Narration}");
        Check(st.Scenes[(int)SoundScene.Ui] != 0 && st.Scenes[(int)SoundScene.Game] != 0 && st.Scenes[(int)SoundScene.Music] != 0,
            "the Ui, Game and Music scene ids are known");

        await OneShot(first, "vanilla UI confirm from OnLoad (main thread)", maxMs: 3000);
        if (_fixtures)
        {
            await Task.Delay(800);
            Caption("audio-test: ui_beep 1320 Hz (0.3 s)");
            var beep = Sound.Play2D(_uiBeep, Vol(0.8f));  // a thread-pool thread: queued, played on the main thread
            await OneShot(beep, "fixture ui_beep (1320 Hz, kiln.assets cue) from a thread-pool thread", maxMs: 2000);
        }
        if (Mods.IsLoaded("soundsample"))
        {
            try
            {
                await Task.Delay(800);
                Caption("SoundSample: ui_chime 997 Hz (0.5 s)");
                await OneShot(Sound.Play2D(SoundId.Named("soundsample", "ui_chime"), Vol(0.8f)), "SoundSample ui_chime (997 Hz)", maxMs: 2000);
            }
            catch (Exception e)
            {
                Check(false, $"SoundSample's ui_chime resolves: {e.Message}");
            }
        }
        else Info("SoundSample not loaded: its cues are skipped");

        if (_fixtures)
        {
            // music slot: loops, volume fade, replaced by the next track, Stop
            await Task.Delay(800);
            Caption("audio-test: music chord C4+E4 (262+330 Hz) fades in, loops every 3 s, gets quieter, restarts, fades out");
            var m1 = Sound.PlayMusic(_musicChord, Vol(0.5f) * 0.6f, TimeSpan.FromSeconds(1));
            Check(await Until(() => Sound.State(m1) == SoundState.Playing, 3000), $"PlayMusic {m1} is playing (fade-in 1 s)");
            await Task.Delay(3500);
            Check(Sound.IsPlaying(m1), $"the looping music track still plays after 3.5 s (3 s loop): {Sound.State(m1)}");
            Sound.SetVolume(m1, Vol(0.5f) * 0.25f, TimeSpan.FromMilliseconds(500));
            await Task.Delay(700);
            Check(Sound.IsPlaying(m1), "SetVolume (fade 500 ms) keeps it playing");
            var m2 = Sound.PlayMusic(_musicChord, Vol(0.5f) * 0.6f);
            Check(await Until(() => Sound.State(m2) == SoundState.Playing, 3000) && await Until(() => !Sound.IsPlaying(m1), 2000),
                $"a second PlayMusic replaces the first: {Describe(m1)}; {Describe(m2)}");
            Check(Sound.Info(m1)?.Reason == SoundEndReason.Replaced, $"the first track ended as Replaced ({Sound.Info(m1)?.Reason})");
            await Task.Delay(Hold(1000, 2000));
            Sound.Stop(m2, TimeSpan.FromMilliseconds(500));
            Check(!Sound.IsPlaying(m2), "Stop: IsPlaying is false at once");
            Check(await Until(() => Sound.Info(m2)?.Reason == SoundEndReason.Stopped, 2000), $"the track ended as Stopped: {Describe(m2)}");
        }

        Info("next: an unknown cue (must fail soft: no crash, state Failed)");
        var bad = Sound.Play2D(SoundId.Of(Guid.NewGuid()));
        // SoundStart returns 0 when it cannot resolve the cue (FAILED/StartFailed); should the game hand out an
        // instance that dies when its load fails, the handle ends as Stopped instead. Either is soft; the log says which.
        Check(await Until(() => Sound.State(bad) is SoundState.Failed or SoundState.Stopped, 3000),
            $"an unknown cue fails soft (no crash, the handle ends): {Describe(bad)}");
        Caption("");
        Info($"menu suite done: {_pass} PASS, {_fail} FAIL so far");
    }

    // ---- in game (client) ----

    private void OnClientTick(in TickInfo tick)
    {
        var session = Client.ClientWorldSession;
        if (Client.LocalPlayer.IsNone || session == _worldSuiteSession) return;
        if (_worldSeenSession != session)
        {
            _worldSeenSession = session;
            _worldSeenAt = Environment.TickCount64;
            Info($"client world session {session}: local player {Client.LocalPlayer}; the world suite starts in 8 s");
            return;
        }
        if (Environment.TickCount64 - _worldSeenAt < 8000 || Volatile.Read(ref _running) != 0) return;
        _worldSuiteSession = session;
        Start(WorldTour);
    }

    private async Task WorldSuite()
    {
        var st = Sound.Status!;
        Info($"world suite: voice-over manager {(st.VoiceoverManager ? "running" : "NOT seen")}, voice language id {st.VoiceLanguageId} " +
             $"(slot {st.VoiceLanguage}), voice-over volume {st.VoiceoverVolume:F2}, lore mode {st.LoreMode}, lore queued {st.LoreQueued}");
        Check(st.VoiceoverManager, "the voice-over manager runs in game (VoiceoverUpdate hook)");
        var me = Client.LocalPlayer;

        // follow the local player: a loop that stays until Stop
        Caption($"audio-test: loop_pulse 330 Hz pulsing twice a second, on you (follows you), quieter after {Hold(2500, 4000) / 1000.0:F1} s and 5 m east, then stops");
        var follow = Sound.PlayOn(_loopPulse, me, Vol(0.7f));
        Check(await Until(() => Sound.State(follow) == SoundState.Playing, 3000), $"PlayOn(local player {me}) plays: {Describe(follow)}");
        var fi = Sound.Info(follow);
        Check(fi is { Following: true }, $"it follows the entity (position {fi?.Position})");
        var at = fi?.Position ?? default;
        _lastAt = at;
        await Task.Delay(Hold(2500, 4000));
        Check(Sound.State(follow) == SoundState.Playing, $"the loop (1 s) still plays after {Hold(2500, 4000) / 1000.0:F1} s: {Describe(follow)}");
        Sound.SetVolume(follow, Vol(0.3f) / 2, TimeSpan.FromMilliseconds(200));
        var moved = new WorldPosition(at.X + 5, at.Y, at.Z);
        Sound.SetPosition(follow, moved);
        await Task.Delay(500);
        fi = Sound.Info(follow);
        Check(fi is { State: SoundState.Playing, Following: false } && fi.Position.DistanceTo(moved) < 0.01,
            $"SetVolume + SetPosition apply (following ends): {fi?.State}, following {fi?.Following}, at {fi?.Position}");
        Sound.Stop(follow, TimeSpan.FromMilliseconds(100));
        Check(!Sound.IsPlaying(follow) && await Until(() => Sound.Info(follow)?.Reason == SoundEndReason.Stopped, 2000),
            $"Stop ends the loop: {Describe(follow)}");

        // at a position, one-shot
        var east = new WorldPosition(at.X + 10, at.Y, at.Z);
        Caption("audio-test: at_tone 880 Hz for 2 s, 10 m east of where you stood");
        await OneShot(Sound.PlayAt(_atTone, east, Vol(0.9f)), $"PlayAt at_tone (880 Hz, 2 s, streamed) 10 m east at {east}", maxMs: 5000, minMs: 1500);

        // a vanilla positional loop
        Caption($"audio-test: the game's grasshopper loop, 3 m north, for {Hold(1500, 3000) / 1000.0:F1} s");
        var grass = Sound.PlayAt(VanillaGrassLoop, new WorldPosition(at.X, at.Y, at.Z + 3), Vol(1f));
        Check(await Until(() => Sound.State(grass) == SoundState.Playing, 3000), $"a vanilla loop (grasshoppers) plays 3 m north: {Describe(grass)}");
        await Task.Delay(Hold(1500, 3000));
        Check(Sound.State(grass) == SoundState.Playing, "the vanilla loop still plays (loop of 10 s)");
        Sound.Stop(grass, TimeSpan.FromMilliseconds(100));

        // voice-over on the dialogue channel: the cooked line has en and de takes only
        st = Sound.Status!;
        var expectVoice = st.VoiceLanguage is "en" or "de";
        Caption($"audio-test: voice-over line: en = two 600 Hz beeps, de = three 700 Hz beeps, other voice languages = silence (yours: {st.VoiceLanguage}); subtitle at the bottom");
        var line = Voiceover.Play(_voLine);
        await Voice(line, $"Voiceover.Play vo_line (voice slot {st.VoiceLanguage}: {(expectVoice ? "a take" : "no take: silence")})", expectVoice, 4000);
        Caption("audio-test: the game's villager 'surprise' voice line");
        await Voice(Voiceover.Play(VanillaVillager), "Voiceover.Play a vanilla villager line (all five languages)", true, 4000);

        // narration through the lore queue
        var expectNarr = st.VoiceLanguage == "en";
        Caption("audio-test: narration: three rising notes 450/500/550 Hz (English voice only); music and game sounds duck");
        var narr = Voiceover.Narrate(_voNarration);
        await Voice(narr, $"Voiceover.Narrate vo_narration (en only; voice slot {st.VoiceLanguage}, lore mode {st.LoreMode})", expectNarr, 8000, allowRefused: true);
        Caption($"audio-test: a game lore narration (English speech), stopped after {Hold(2000, 3000) / 1000.0:F0} s");
        var lore = Voiceover.Narrate(VanillaLore);
        if (await Until(() => Sound.State(lore) == SoundState.Playing, 5000))
        {
            Check(true, $"a vanilla lore line narrates: {Describe(lore)}");
            await Task.Delay(Hold(2000, 3000));
            Sound.Stop(lore, TimeSpan.FromMilliseconds(200));
            Check(await Until(() => Sound.Info(lore)?.Reason == SoundEndReason.Stopped, 3000), $"Stop ends the narration: {Describe(lore)}");
        }
        else Check(Sound.Info(lore)?.Reason is SoundEndReason.Refused or SoundEndReason.Silent, $"a vanilla lore line narrates or is refused/silent by setting: {Describe(lore)}");

        // StopAll from the main thread (attributed to this mod)
        Caption("audio-test: loop_pulse again, stopped by StopAll after it starts");
        var tcs = new TaskCompletionSource<SoundHandle>();
        Client.Post(() => tcs.TrySetResult(Sound.PlayOn(_loopPulse, me, Vol(0.5f))));
        var mine = await tcs.Task;
        await Until(() => Sound.State(mine) == SoundState.Playing, 3000);
        var stopped = new TaskCompletionSource<int>();
        Client.Post(() => stopped.TrySetResult(Sound.StopAll(TimeSpan.FromMilliseconds(50))));
        var n = await stopped.Task;
        Check(n >= 1 && await Until(() => !Sound.IsPlaying(mine), 1000), $"StopAll on the main thread stops this mod's sounds: {n} ({Describe(mine)})");

        // a run-time cue override (the game applies it when it next loads the cue)
        if (_fixtures)
        {
            Caption($"audio-test: a game loop cue replaced at run time: the 330 Hz pulse, 4 m south, {Hold(1500, 3000) / 1000.0:F1} s (or the game's own loop if it was loaded before)");
            // Replace registers an asset as the calling mod: on the main thread, where the mod is known
            var replaced = new TaskCompletionSource<bool>();
            Client.Post(() =>
            {
                try { replaced.TrySetResult(Sound.Replace(VanillaReplaceTarget, _loopPulse)); }
                catch (Exception e) { replaced.TrySetException(e); }  // the suite reports it instead of waiting for good
            });
            var ok = await replaced.Task;
            Check(ok, "Sound.Replace serves the fixture loop for a vanilla loop cue (Kiln.Assets override)");
            var r = Sound.PlayAt(VanillaReplaceTarget, new WorldPosition(at.X, at.Y, at.Z - 4), Vol(1f));
            Check(await Until(() => Sound.State(r) == SoundState.Playing, 3000), $"the replaced cue plays 4 m south (by ear: the 330 Hz pulse): {Describe(r)}");
            await Task.Delay(Hold(1500, 3000));
            Sound.Stop(r, TimeSpan.FromMilliseconds(100));
            Client.Post(() => Sound.Restore(VanillaReplaceTarget));
        }

        Caption("audio-test: an unknown voice-over line: nothing to hear (10 s)");
        Info("next: an unknown voice-over line (must fail soft within ~10 s)");
        var badVo = Voiceover.Play(VoiceoverId.Of(Guid.NewGuid()));
        Check(await Until(() => Sound.State(badVo) is SoundState.Failed or SoundState.Stopped, 13_000), $"an unknown voice-over fails soft: {Describe(badVo)}");

        st = Sound.Status!;
        Check(st.GameStarts > 0 && st.GameStartsOffMainThread == 0,
            $"the game's own SoundStart calls all ran on the main thread: {st.GameStarts} calls, {st.GameStartsOffMainThread} off it, {st.GameThreads} thread(s)");
        Info($"status json: {Sound.StatusJson()}");
        Caption("");
        Info($"world suite done: summary: {_pass} PASS, {_fail} FAIL");
    }

    // ---- SoundSample: its 9 sounds in game ----

    private async Task SoundSampleSuite()
    {
        SoundId C(string n) => SoundId.Named("soundsample", n);
        var me = Client.LocalPlayer;
        var probe = Sound.PlayOn(C("hum_loop"), me, 0f);  // silent: only to read where the player is
        await Until(() => Sound.State(probe) == SoundState.Playing, 3000);
        var at = Sound.Info(probe)?.Position ?? default;
        Sound.Stop(probe);
        var st = Sound.Status!;

        Caption("SoundSample 1/9: ui_chime 997 Hz (0.5 s)");
        await OneShot(Sound.Play2D(C("ui_chime"), Vol(1f)), "SoundSample ui_chime", maxMs: 2000);
        await Task.Delay(800);
        Caption("SoundSample 2/9: dialog confirm override 1661 Hz (0.4 s) instead of the game's click");
        await OneShot(Sound.Play2D(VanillaUiConfirm, Vol(1f)), "SoundSample dialog_confirm override (vanilla cue b096387a)", maxMs: 2000);
        await Task.Delay(800);
        Caption($"SoundSample 3/9: stereo_tone 440 Hz left / 660 Hz right, {(_full ? 4 : 1.5)} s, 8 m east (turn the camera: it pans)");
        var tone = Sound.PlayAt(C("stereo_tone"), new WorldPosition(at.X + 8, at.Y, at.Z), Vol(1f));
        if (_full) await OneShot(tone, "SoundSample stereo_tone (streamed, 4 s)", maxMs: 7000, minMs: 3500);
        else
        {
            Check(await Until(() => Sound.State(tone) == SoundState.Playing, 3000), $"SoundSample stereo_tone (streamed) plays: {Describe(tone)}");
            await Task.Delay(1500);
            Sound.Stop(tone, TimeSpan.FromMilliseconds(200));
        }
        Caption("SoundSample 4/9: blip_3d three times, 5 m west: 1200 / 1500 / 1800 Hz takes (random order, no repeat)");
        for (var i = 0; i < 3; i++)
        {
            await OneShot(Sound.PlayAt(C("blip_3d"), new WorldPosition(at.X - 5, at.Y, at.Z), Vol(1f)), $"SoundSample blip_3d #{i + 1}", maxMs: 2000);
            await Task.Delay(500);
        }
        Caption($"SoundSample 5/9: hum_loop 220 Hz on you for {Hold(2500, 6000) / 1000.0:F1} s (a 2 s loop: listen for a click at each restart)");
        var hum = Sound.PlayOn(C("hum_loop"), me, Vol(0.8f));
        Check(await Until(() => Sound.State(hum) == SoundState.Playing, 3000), $"SoundSample hum_loop plays on the player: {Describe(hum)}");
        await Task.Delay(Hold(2500, 6000));
        Check(Sound.State(hum) == SoundState.Playing, $"SoundSample hum_loop (2 s) still loops after {Hold(2500, 6000) / 1000.0:F1} s: {Describe(hum)}");
        Sound.Stop(hum, TimeSpan.FromMilliseconds(200));
        Caption($"SoundSample 6/9: ambience_loop 330/331 Hz beating swell (2D, Ambience scene) for {Hold(2000, 10_000) / 1000.0:F0} s{(_full ? ": it restarts at 8 s" : "")}");
        var amb = Sound.Play2D(C("ambience_loop"), Vol(0.8f));
        Check(await Until(() => Sound.State(amb) == SoundState.Playing, 3000) && Sound.Info(amb)?.Scene == SoundScene.Ambience,
            $"SoundSample ambience_loop plays in the Ambience scene (cooked playScene): {Describe(amb)}");
        await Task.Delay(Hold(2000, 10_000));
        Check(Sound.State(amb) == SoundState.Playing, $"SoundSample ambience_loop still plays after {Hold(2000, 10_000) / 1000.0:F0} s{(_full ? " (looped at 8 s)" : "")}");
        Sound.Stop(amb, TimeSpan.FromMilliseconds(500));
        Caption($"SoundSample 7/9: music_track C major arpeggio, fades in over 1 s, {(_full ? "12 s to its end" : "2 s then fades out")} (Music slider)");
        var music = Sound.PlayMusic(C("music_track"), Vol(0.8f), TimeSpan.FromSeconds(1));
        if (_full) await OneShot(music, "SoundSample music_track (12 s, not looping)", maxMs: 16_000, minMs: 11_000);
        else
        {
            Check(await Until(() => Sound.State(music) == SoundState.Playing, 3000), $"SoundSample music_track plays in Kiln's music slot: {Describe(music)}");
            await Task.Delay(2000);
            Sound.Stop(music, TimeSpan.FromMilliseconds(500));
            Check(await Until(() => Sound.Info(music)?.Reason == SoundEndReason.Stopped, 2000), $"SoundSample music_track stopped: {Describe(music)}");
        }
        Caption("SoundSample 8/9: greeting: en = two 523 Hz beeps, de = three 784 Hz beeps, fr/es/zh = the English beeps (fallback); subtitle");
        await Voice(Voiceover.Play(VoiceoverId.Named("soundsample", "greeting")),
            $"SoundSample greeting (voice slot {st.VoiceLanguage}; every slot has a take)", true, 5000);
        await Task.Delay(1000);
        Caption("SoundSample 9/9: narration_intro: three 392 Hz notes (English voice only, else silence); subtitle");
        await Voice(Voiceover.Narrate(VoiceoverId.Named("soundsample", "narration_intro")),
            $"SoundSample narration_intro (voice slot {st.VoiceLanguage}; en only)", st.VoiceLanguage == "en", 9000, allowRefused: true);
        Caption("");
        Info($"SoundSample suite done: summary: {_pass} PASS, {_fail} FAIL");
    }

    private HudLine? _caption;
    private volatile string _captionText = "";
    private volatile bool _captionInWorld;

    /// <summary>What the suite says now, only on the screen the suite belongs to: a world suite's text is not for the pause
    /// menu or the main menu it may still be running under.</summary>
    private string CaptionNow()
    {
        var kind = Ui.Screens.Current.Kind;
        return (_captionInWorld ? kind is ScreenKind.InGame or ScreenKind.None : kind is ScreenKind.MainMenu or ScreenKind.Title or ScreenKind.None)
            ? _captionText : "";
    }

    private void Caption(string text)
    {
        if (text.Length > 0) Info($"caption: {text}");
        _captionText = text;
        _captionInWorld = !Client.LocalPlayer.IsNone;
        try
        {
            if (!Ui.Available) return;
            _caption ??= Hud.Show("audio-test.caption", CaptionNow, HudAnchor.TopCenter, Color.Orange);
        }
        catch (Exception e)
        {
            Info($"caption not shown: {e.Message}");
        }
    }

    // ---- server part (tick thread) ----

    private void ServerPart()
    {
        var hosting = Client.Available && Client.IsHosting;
        Info($"server part: {(hosting ? "hosting client" : "dedicated server")}, tick thread {Game.IsTickThread}, Sound.Available {Sound.Available}, " +
             $"Voiceover.Available {Voiceover.Available}");
        if (!hosting)
        {
            var listeners = Network.Peers.Count(x => x.State == NetPeerState.Ready && x.HasChannel("kiln:audio"));
            Check(Sound.Available == listeners > 0,
                $"dedicated server: Sound.Available ({Sound.Available}) is true exactly when a Kiln client has kiln:audio ({listeners})");
            Check(Sound.Status is null, "dedicated server: no kiln.audio (the server has no sound system)");
        }
        else Check(Sound.Available, "hosting: Sound.Available on the server thread (the host's client plays relayed sounds)");
        var at = hosting && World.Transform(Client.LocalPlayer)?.Position is { } p ? new WorldPosition(p.X, p.Y, p.Z + 6) : new WorldPosition(0, 100, 0);
        var before = Sound.Status?.Played ?? 0;
        var h = Sound.PlayAt(_atTone.IsNone ? VanillaUiConfirm : _atTone, at, 0.4f);
        Check(h.IsRemote, $"server-side PlayAt returns a remote handle: {h}");
        var v = Voiceover.Play(_voLine.IsNone ? VanillaVillager : _voLine);
        Check(v.IsRemote || v.IsNone, $"server-side Voiceover.Play relays: {v}");
        Sound.Stop(Sound.PlayMusic(_musicChord.IsNone ? VanillaUiConfirm : _musicChord, 0.3f), TimeSpan.FromMilliseconds(200));
        Check(true, "server-side PlayMusic + Stop relay without errors");
        if (hosting)
            Start(async () =>
            {
                Check(await Until(() => (Sound.Status?.Played ?? 0) > before, 3000),
                    $"the host's own client played the server's PlayAt ({(Sound.Status?.Played ?? 0) - before} start(s))");
            });
    }

    // ---- console command ----

    private void OnCommand(CommandContext ctx)
    {
        var sub = ctx.Count > 0 ? ctx.Args[0].ToLowerInvariant() : "status";
        string Arg(int i) => ctx.Count > i ? ctx.Args[i] : "";
        switch (sub)
        {
            case "status":
                ctx.Reply(Sound.StatusJson());
                ctx.Reply($"summary: {_pass} PASS, {_fail} FAIL");
                break;
            case "menu":
                if (Sound.Status is null) { ctx.Reply("no kiln.audio here"); break; }
                Start(() => MenuSuite(Sound.Play2D(VanillaUiConfirm, 0.4f)));
                ctx.Reply("menu suite started");
                break;
            case "world":
                _worldSuiteSession = 0;
                _worldSeenSession = 0;
                ctx.Reply("the world suite runs again at the next client step + 8 s");
                break;
            case "server":
                ServerPart();
                ctx.Reply("server part ran (see kiln.log)");
                break;
            case "play":
            {
                var cue = ResolveCue(Arg(1));
                var mode = Arg(2).ToLowerInvariant();
                Client.Post(() =>
                {
                    var me = Client.LocalPlayer;
                    var h = mode switch
                    {
                        "on" => Sound.PlayOn(cue, me),
                        "music" => Sound.PlayMusic(cue, 0.4f, TimeSpan.FromSeconds(1)),
                        "at" when _lastAt is { } p => Sound.PlayAt(cue, new WorldPosition(p.X + 5, p.Y, p.Z)),
                        _ => Sound.Play2D(cue),
                    };
                    Info($"command play {cue} {mode}: {h}");
                });
                ctx.Reply($"queued {cue} ({(mode.Length > 0 ? mode : "2d")}) for the main thread");
                break;
            }
            case "vo":
            {
                var line = ResolveLine(Arg(1));
                Client.Post(() => Info($"command vo {line}: {Voiceover.Play(line)}"));
                ctx.Reply($"queued voice-over {line}");
                break;
            }
            case "narrate":
            {
                var line = ResolveLine(Arg(1));
                Client.Post(() => Info($"command narrate {line}: {Voiceover.Narrate(line)}"));
                ctx.Reply($"queued narration {line}");
                break;
            }
            case "stopall":
                Client.Post(() => Info($"command stopall: {Sound.StopAll(TimeSpan.FromMilliseconds(100))} stopped"));
                ctx.Reply("queued stopall");
                break;
            default:
                ctx.Reply("usage: audiotest status|menu|world|server|play <cue> [2d|at|on|music]|vo <line>|narrate <line>|stopall");
                break;
        }
    }

    private SoundId ResolveCue(string s)
    {
        if (Guid.TryParse(s, out var g)) return SoundId.Of(g);
        var dot = s.IndexOf('/');
        return dot > 0 ? SoundId.Named(s[..dot], s[(dot + 1)..]) : SoundId.Named(Context, s.Length > 0 ? s : "ui_beep");
    }

    private VoiceoverId ResolveLine(string s)
    {
        if (Guid.TryParse(s, out var g)) return VoiceoverId.Of(g);
        var dot = s.IndexOf('/');
        return dot > 0 ? VoiceoverId.Named(s[..dot], s[(dot + 1)..]) : VoiceoverId.Named(Context, s.Length > 0 ? s : "vo_line");
    }

    // ---- helpers ----

    private void Start(Func<Task> suite, bool full = false)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Info("a suite is running already");
            return;
        }
        _full = full;
        Info(full ? "tour: full lengths (human check)" : "suite: compact (short, quiet)");
        _ = Task.Run(async () =>
        {
            try { await suite(); }
            catch (Exception e) { Check(false, $"suite threw: {e}"); }
            finally
            {
                Caption("");  // a suite that throws skips its own last Caption(""): the text would stay for good
                Volatile.Write(ref _running, 0);
            }
        });
    }

    private async Task OneShot(SoundHandle h, string what, int maxMs, int minMs = 0)
    {
        if (!Check(!h.IsNone, $"{what}: a handle")) return;
        var sawPlaying = false;
        var t0 = Environment.TickCount64;
        SoundState s;
        while (true)
        {
            s = Sound.State(h);
            if (s == SoundState.Playing) sawPlaying = true;
            if (s is SoundState.Stopped or SoundState.Failed or SoundState.None || Environment.TickCount64 - t0 > maxMs) break;
            await Task.Delay(15);
        }
        var i = Sound.Info(h);
        var main = Sound.Status?.MainThread ?? 0;
        Check(sawPlaying && i is { State: SoundState.Stopped, Reason: SoundEndReason.Finished } && (i.Played?.TotalMilliseconds ?? 0) >= minMs,
            $"{what}: pending -> playing -> finished ({Describe(h)})");
        Check(i is not null && i.Thread == main && main != 0, $"{what}: started on the main thread ({i?.Thread} vs {main})");
    }

    private async Task Voice(SoundHandle h, string what, bool expectAudio, int maxMs, bool allowRefused = false)
    {
        if (!Check(!h.IsNone, $"{what}: a handle (feature on)")) return;
        var sawPlaying = false;
        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < maxMs)
        {
            var s = Sound.State(h);
            if (s == SoundState.Playing) sawPlaying = true;
            if (s is SoundState.Stopped or SoundState.Failed) break;
            await Task.Delay(20);
        }
        var i = Sound.Info(h);
        if (allowRefused && i?.Reason == SoundEndReason.Refused)
        {
            Info($"{what}: refused by the lore queue (the player's auto-play setting): {Describe(h)}");
            return;
        }
        if (expectAudio)
            Check(sawPlaying && i?.Reason == SoundEndReason.Finished, $"{what}: playing -> finished ({Describe(h)})");
        else
            Check(!sawPlaying && i?.Reason == SoundEndReason.Silent, $"{what}: silent as the cooked data says ({Describe(h)})");
    }

    private static string Describe(SoundHandle h) => Sound.Info(h) is { } i
        ? $"{h} {i.State}/{i.Reason}, scene {i.Scene}, instance 0x{i.Instance:x}, thread {i.Thread}" +
          (i.VoiceLanguage is { } l ? $", voice slot {l}" : "") +
          (i.StartDelay is { } d ? $", started after {d.TotalMilliseconds:F0} ms" : "") +
          (i.Played is { } p ? $", played {p.TotalMilliseconds:F0} ms" : "") +
          (i.Following ? ", following" : "") + (i.Unmatched ? ", unmatched" : "")
        : $"{h} (no info)";

    private static async Task<bool> Until(Func<bool> cond, int timeoutMs)
    {
        var t0 = Environment.TickCount64;
        while (true)
        {
            try
            {
                if (cond()) return true;
            }
            catch
            {
                // a condition reading a not-yet-available service: retry
            }
            if (Environment.TickCount64 - t0 > timeoutMs) return false;
            await Task.Delay(20);
        }
    }

    private void Info(string s) => Log.Info($"audio-test: INFO {s}");

    private bool Check(bool ok, string what)
    {
        if (ok)
        {
            Interlocked.Increment(ref _pass);
            Log.Info($"audio-test: PASS {what}");
        }
        else
        {
            Interlocked.Increment(ref _fail);
            Log.Error($"audio-test: FAIL {what}");
        }
        return ok;
    }
}
