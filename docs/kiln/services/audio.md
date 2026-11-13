# kiln.audio / Kiln.Audio: sounds, music, voice-over and narration

Play a mod's sounds (or the game's) from code: 2D, at a position, on an entity, as music, and voice-over lines with
subtitles, through Keen's own sound system in the game client. Forge: `SoundEvent` + `Level.playSound` /
`SoundManager.play`. Research: `docs/re/audio.md`; making the sounds:
`docs/kiln/modding/sounds.md` (tools/soundcook, sample `mods/samples/SoundSample`).

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/client.d/audio.json` (standalone client file): features `audio.play`, `audio.voiceover`, `audio.narration` |
| C table | `native/core/abi/services/kiln_audio.h` (`kiln.audio` v1) |
| Native | `native/services/audio/` → `kiln/services/kiln-audio.dll` (client only; on a dedicated server it returns "unavailable" at boot and hooks nothing) |
| Managed | `managed/services/Kiln.Audio/` → `kiln/host/Kiln.Audio.dll` (namespace `Kiln`: `Sound`, `Voiceover`, `SoundId`, `VoiceoverId`, `SoundHandle`) |
| Network | Kiln channel `kiln:audio` v1, `required: false` (never blocks a join) |
| Test | `mods/tests/audio-test` (`build.sh`: the mod + its own test tones) |

Status: offline and lab-verified; the boot capture, the thread rule and the voice-over manager hook seen
running in a real client (passive, see "Verification"); playback needs the manual test below.

## C# (mods)
```xml
<ProjectReference Include="…/managed/services/Kiln.Audio/Kiln.Audio.csproj" Private="false" ExcludeAssets="runtime" />
```
```csharp
using Kiln;

// cues of this mod, by the names in its cook recipe (assets/cooked/audio.json; else UUIDv5 of assets/cook.json)
var chime = SoundId.Named(Context, "ui_chime");
var hum = SoundId.Named(Context, "hum_loop");
var vanilla = SoundId.Of("b096387a-cc73-475a-bacd-69ce8cad9fc9");     // any cue by GUID
var other = SoundId.Named("soundsample", "blip_3d");                 // another loaded mod's cue

SoundHandle h = Sound.Play2D(chime);                                   // UI style (scene: the cue's cooked default, else Ui)
Sound.PlayAt(other, new WorldPosition(x, y, z), volume: 0.8f);         // positional, Game scene
var loop = Sound.PlayOn(hum, Client.LocalPlayer);                       // follows the entity, stops when it is gone
Sound.SetVolume(loop, 0.3f, TimeSpan.FromMilliseconds(200));
Sound.Stop(loop, TimeSpan.FromMilliseconds(100));
Sound.PlayMusic(SoundId.Named(Context, "music_track"), 0.8f, TimeSpan.FromSeconds(1));  // Kiln's music slot
bool on = Sound.IsPlaying(h);                                          // Pending or Playing
SoundInfo? i = Sound.Info(h);                                          // state, reason, scene, instance, thread, position
Sound.StopAll();                                                       // this mod's sounds
Sound.Replace(vanilla, chime);                                         // run-time cue override (Kiln.Assets)

var line = VoiceoverId.Named(Context, "greeting");
Voiceover.Play(line, speaker: npc);                                    // dialogue channel, subtitles from the cook
Voiceover.Narrate(VoiceoverId.Named(Context, "narration_intro"));     // lore queue
Voiceover.Stop();
```

### Behaviour
- **Any thread, one game thread.** Every call returns at once with a handle in `Pending`. kiln.audio runs the game call
  on the game's main thread: sounds in the drain after the next UI frame (kiln.ui.tick, so menus work too) or before
  the next client-world step, whichever comes first; voice-over inside the game's voice-over update. No game sound
  function is ever called from another thread (`docs/re/audio.md` 3.2: no locks in the start path).
- **States** (`Sound.State`): `Pending` → `Playing` (the game's instance exists) → `Stopped` (`Finished`, `Stopped`,
  `EntityGone`, `Replaced`, `Silent`, `WorldEnded`) or `Failed` (`StartFailed`, `NoSoundSystem`, `TimedOut`,
  `NotFound`, `Refused`). `IsPlaying` = `Pending` or `Playing`; one-shots go true → false by themselves, loops stay
  true until `Stop`. After `Stop`, `IsPlaying` is false and `Info` reads `Stopped`/`Stopped` at once. Finished handles
  are kept 60 s, then `None`.
- **Cooked lengths.** The game keeps the instance of a streamed take (over 1 s) alive after its audio has ended, for
  seconds or until the world ends; the lore queue waits for it too, so music stays ducked and the next lore line waits.
  A one-shot of a mod's cooked audio (a cue without `loop`, a voice-over line, a narration) that still plays its cooked
  length plus a margin (a quarter of it, at least 0.5 s) after it started is stopped with a 30 ms fade and reads
  `Stopped`/`Finished` (kiln.log `#N still playing ... past its cooked length (... ms): ended`). The game's own cues have
  no cooked length and keep the instance rule.
- **Unknown cues.** `SoundStart` hands out an instance for any GUID, one that never plays and never ends. A cue that is in
  neither the game's cue catalog nor a mod's (kiln.assets) fails at once: `Failed`/`NotFound`.
- **Handles** are Kiln ids (never reused); the game's instance id behind one is generation-checked by the game, so a
  stale handle never touches another sound.
- **Volume** 0..1 multiplies the cue's own volume and the player's slider for the scene. `PlayMusic` fades in (best
  effort: a streamed take still loading starts at full volume) and stops the previous Kiln track (fade ≥ 250 ms).
- **Scenes** (`SoundScene`): Music, Ui, Game (the world), Ambience, LoreVoiceover. `Play2D` uses the cue's cooked
  `playScene` (Ui/Music/Ambience; `Game` would sit at the origin, so it becomes Ui). `PlayAt`/`PlayOn` use Game.
- **PlayOn** follows the entity's transform every client step (`SoundSetPosition`) and stops with a 100 ms fade when
  the entity is gone. It waits for a client step (in game). `SetPosition` ends following.
- **World end.** When the client world stops, Kiln's Game/Ambience sounds, following sounds and voice-over lines stop
  (`WorldEnded`). Music and Ui sounds go on into the menu.
- **Mod unload**: its sounds stop (50 ms fade), its subtitle lines go, its `Replace` overrides are removed by
  Kiln.Assets.
- **Fail soft**: no kiln.audio (dedicated server, unknown build, symbols missing) → `Sound.Status` null, client calls
  return `SoundHandle.None`; an unknown cue ends `Failed`/`StartFailed`.

### Server side (relay)
On a hosting client only the server world's tick thread counts as server code; the main thread, the client thread and thread-pool
continuations play on the host's own client (a thread-pool call used to count as server code, and `PlayOn` threw there).
Server code (a dedicated server, or a hosting client's server thread; tick thread) calls the same methods:
`PlayAt(cue, at, volume, radius)` (Kiln clients within `radius`, default the cue's max distance or 60 m), `PlayOn`,
`PlayFor(player, cue)`, `PlayMusic`, `Play2D` (everyone), `Stop`, `Voiceover.Play/PlayFor/Narrate/NarrateFor/Stop`.
They become `kiln:audio` messages; vanilla clients have no channel and hear nothing new. A hosting client's own
player is not a network peer, so the host plays the server's sounds itself. The handle is remote
(`SoundHandle.IsRemote`): it can be stopped, not observed (`IsPlaying` false). Server `Sound.Available` is true when a
Kiln client with the channel is connected (or the host can play locally).

Messages (`AudioRelay.cs`, little-endian): 1 `Play` {u8 kind (0 2D-for-player, 1 at, 2 on, 3 music, 4 2D), u8 scene,
Guid cue, f64 x y z, u32 entity, f32 volume, i64 fadeNs, u32 token}; 2 `Stop` {u32 token, i64 fadeNs}; 3 `Voice` {u8
kind (1 line, 2 narrate, 3 stop all), Guid line, u32 speaker, bool hasPosition, f64 x y z, u32 token, i64 fadeNs}.
Tokens have bit 31 set.

### Entity ids (server → client)
What holds for the mapping:
- **On a host** the client world and the server world use the same entity ids (EMF; Kiln's local-player id works in
  both, docs/re/client.md). Server-side `PlayOn` there follows the same id directly.
- **On a client joined to a remote server** the ids are not proven equal (the client exe has an `[ecs%c] Entity id %k
  already exists` check on creation, which suggests replicated entities keep the server's ids, but no run confirms
  it). So `PlayOn` relays the id **and** the entity's position; the client follows its entity of that id only when it
  stands within 10 m of the sent position (`KILN_AUDIO_VERIFY`), else it plays at the position without following
  and logs `entity N is X m from the expected position ... (ids differ on this client)`. A joined session's log
  answers the question (client checklist item C9). Relayed voice-over lines play at the speaker's position on remote
  clients (no speaker entity).

### Voice-over and narration
- `Voiceover.Play(line, speaker)` uses the game's dialogue channel (`VoiceoverRequest` + the manager's own update):
  A voice-over needs a client world (the manager's update runs there). Asked for in a hosted world before the host's client is in, it times out after 10 s with `voice-over ... FAILED: timed out`: nobody is there to hear it. audio-test's relay check asked that at `ServerStartedEvent` and logged this once per hosted world; it now waits for the host's client.
  one line at a time (it replaces the current one, also the game's), the player's **voice-over language** and voice-
  over volume. With a speaker (client-world entity) the game keeps the line at that entity; without, at a given
  position or at the player (+1.5 m, the game's fallback).
- `Voiceover.Narrate(line)` pushes the line into the lore queue like a lore reading (`LoreVoiceover` scene; music and
  game sounds duck; the player's lore auto-play mode applies: play now, queue, or never → `Refused`).
- **Languages.** The game picks `languages[slot]`: En_Us 0, De_De 1, Fr_Fr 2, Es_Es 3, Zh_Cn 4, every other voice
  language → English. **Kiln does no fallback**: an empty slot is silence (`Silent`), exactly what the mod's cooked
  data says (its recipe's `"fallback"` fills slots at cook time). kiln.audio reads the line's VoiceoverResource
  through kiln.assets before playing and logs `no take for voice language id N (slot de): silent, as the cooked data
  says`.
- Narration of an empty slot is not queued (the game would keep such an entry at the head of its lore queue forever:
  its update pops the head only when a non-zero instance ends, `docs/re/audio.md` addendum below), nor of a line that
  is neither Kiln's nor the game's (`NotFound`).
- **Subtitles**: `subtitle` (a `UiText`) or the cooked `subtitles` (text per language), drawn at the bottom of the
  screen (Kiln HUD) while the line plays and at least for its cooked duration (also when the line is silent, so a
  player without that voice language still reads it). The game's own lore UI may show the narration's name and
  description loca tags (`nameLoca`, `descriptionLoca`).
- `Voiceover.Stop` stops Kiln's dialogue line; `Sound.Stop(handle)` on a narration stops it when it is the playing head
  of the queue. A queued one reads `Stopped` at once and is stopped when it gets there.

### Cooked index
`<mod>/assets/cooked/audio.json` (written by tools/soundcook):
names → final GUIDs, cue properties (`playScene`, loop, distances), voice-over languages (`null` = empty slot),
subtitles, durations. `Sound.Describe(cue)` / `Voiceover.Describe(line)` return what the cook wrote. Without the file
names map to UUIDv5(`guidNamespace` of `assets/cook.json`, `"cue:<name>"` / `"voiceover:<name>"`), the cook's rule
(checked against Python's `uuid.uuid5`).

## C (native plugins)
`kiln_audio.h`: `status`, `play(owner, KilnAudioPlay*)` (flags `AT`, `FOLLOW`, `VERIFY`, `MUSIC_SLOT`), `stop`,
`set_volume`, `set_position`, `state`, `info` (`KilnSoundInfo`), `stop_owner`, `voiceover_play(owner, vo, speaker,
pos)`, `voiceover_stop`, `narrate(owner, vo, name_loca, desc_loca)`, `status_json`. Any thread. The service cannot see
`owner_revoke`: call `stop_owner` yourself.

## How it works
| Piece | Game function (client 26430625 RVA) | Kiln |
|---|---|---|
| capture | `SoundScenesInit` `0x845e10(table, SoundSystem*, reverb)`, once at start-up | post-hook: SoundSystem*, scene ids [7], listener ids [7]; log `audio: sound system 0x…, scenes music=… ui=… game=…` |
| play | `SoundStart` `0x106ee50(sys, 0, scene, &guid, &PlayParams)` → instance (0 = failed) | called from the drain through the probe link's continuation (when the probe is on), so Kiln's own calls are not counted |
| probe | the same, a kiln.hooks raw link (`audio/SoundStart`) | counts the game's own calls per thread (`KILN_AUDIO_PROBE=0` turns it off); first call per thread and a 30 s summary in kiln.log |
| stop / volume / position / alive | `0x106fc50`, `0x1069c10`, `0x1069ba0`, `0x105e8e0` (= the instance lookup `0x105b580` is non-null) | per handle in the drain |
| follow | core `GetEntityTransform(clientWorld, id)` in the client-world Pre | `SoundSetPosition` when it moved |
| dialogue voice-over | `VoiceoverUpdate` `0x1233520(mgr)` (every frame in game, main thread); `VoiceoverRequest` `0x1232500`, `VoiceoverSetPosition` `0x1233420` | requests run in a pre-hook with the manager the game passes (no stored pointer); the post-hook reads +pending/+playing/+instance |
| narration | `VoiceoverLorePush` `0x1232570(mgr, &vo, nameLoca, descLoca, u16 version)` (what the `LoreVoiceover` program calls) | in the same pre-hook; post-hook scans the lore queue (16 × 0x48 at mgr+0x60, count mgr+0x4e0) |

The patterns match client 26430625 and the previous client 26404966 unchanged (`sigdb_check.py`); every manager
offset is captured, cross-checked by a second capture where the code has one (`vo.sys` 4 captures, `vo.volume` 2,
`vo.language` 2; `SoundIsPlaying`, `SoundStop`, `SoundSetVolume`, `SoundStart` also captured from their callers).

### RE addendum (to docs/re/audio.md)
- `PlayParams` → instance: +0x00 → inst+0x40, xyz → inst+0x60, volume → inst+0x4c; instance ids are index (20 bits) +
  generation (12 bits), checked by `0x105b580`.
- Voice-over manager: `+0x58` "start pending" is set by `VoiceoverRequest` and cleared at the end of the manager's
  frame function `0x12339b0` (which calls `VoiceoverUpdate` then the lore update `0x1233660`); a request for the
  line already pending is ignored. A line whose resource is not loaded yet is retried every frame. After the line
  ends, `VoiceoverUpdate` clears the pending GUID.
- The program `0x460545` moves the dialogue line to the speaker's transform every frame (`0x1232440` = `mgr+0x4c`).
- Lore queue: entries {GUID, …, +0x10 resource handle, +0x14 instance, +0x18 playing, +0x30 duration ns}; the head
  plays in scene `mgr+0x1c` (LoreVoiceover) with volume `mgr+0x704` (0 while a dialogue line plays), PlayParams
  `+0x24 = 1`. The head is popped (`0x1232210`) when its non-zero instance stops; an empty language slot leaves
  instance 0 with playing set, so that entry would stay at the head. `LorePush` inserts at the front in mode 0, appends
  in mode 1 (max 16), drops otherwise (`mgr+0x724`, the auto-play setting [hyp: Immediately/Queue/Never]), and keeps
  a 16-entry history at `mgr+0x4e8`.
- The `LoreVoiceover` event (40 B) is {+8 voiceover, +24 playerEntityId, +28 name, +32 description}; the program
  `0x30dd70` pushes its events and the `LoreVoiceoverState` singleton's line.

## Verification
### Offline (done)
- `sigdb_check.py sigdb/symbols/client.json` on client 26430625 (`5d9ef2f1…`): all 15 audio symbols, 2 captured
  symbols and 17 constants resolve, features `audio.play`, `audio.voiceover`, `audio.narration` on; the same on the
  previous client 26404966.
- Struct sizes C = C# (72 / 88 / 144 / 104 B, `static_assert` + a scratch check); UUIDv5 equals Python's.
- audio-test's fixtures (`fixtures/make_tones.py`, tools/kfc): every SoundResource / SoundContainerResource /
  VoiceoverResource re-encodes byte for byte, `streamedData.hash` at +24, decoded tone SNR 72 dB.

### Lab (dedicated server)
```
[service:kiln-audio.dll] audio: not a game client (the dedicated server has no sound system): kiln.audio unavailable here; ...
[kiln] net: channel kiln:audio v1 open (id 4, kiln)
[kiln] audio: Kiln.Audio up: no kiln.audio (server: relay only), relay channel kiln:audio v1 open
audio-test: PASS dedicated server: Sound.Available (False) is true exactly when a Kiln client has kiln:audio (0)
audio-test: PASS server-side PlayAt returns a remote handle: remote#1
audio-test: PASS dedicated server: Sound.Available (True) is true exactly when a Kiln client has kiln:audio (1)
```
A fake Kiln client on the lab transport (kiln.net `labPort`, nettest's frames) with `kiln:audio` v1 received, after
`/audiotest server` from the console folder: `VoiceMsg kind 1 line 7412911f-… (58 B)`, `PlayMsg kind 3 scene 0 cue
0d4407e8-… vol 0.30 token 0x80000006 (62 B)`, `StopMsg token 0x80000006 fade 200000000 ns (12 B)`. PlayAt goes to
peers near the position, so a lab peer without a player gets none (as designed). 0 `[E` lines; SoundSample 52 PASS.

### Client, passive
kiln-audio.dll ran in a real client (26430625, LOCAL world1.ksd hosted, no audio mod loaded), 0 warnings or errors:
```
audio: kiln.audio v1 ready (play, voice-over, narration, SoundStart probe); waiting for the game's sound system
audio: drains after every UI frame (kiln.ui.tick) and before every client-world step
audio: sound system 0x10022dbf338, scenes music=65536 ui=65537 game=65541 ambience=65542 character=65540 voicechat=65539 lore=65538; listeners 65536 65537 65538 65539 65540 65541 65542
audio: main-thread drain runs on thread 400 (UI frames)
audio: the game calls SoundStart on thread 400 (main thread) (scene 65537)
audio: the game's SoundStart calls in the first 30+ s: 4, 0 not on the main thread; threads {400 (main): 4}
tick: client world 0x3003c83ab00 started (session 1, thread 400)
audio: voice-over manager 0x3004eea5be0 running (thread 400): voice language id 1 (slot en), voice scene 65541, lore scene 65538, lore mode 1
```
So: the boot hook captures the system and scenes (ids are 0x10000 + creation order: Music, Ui, Lore, Voicechat,
Character, Game, Ambience); UI frames, client-world steps, the voice-over manager and every SoundStart the game made
in that session ran on the main thread (no other thread was ever logged); the manager's dialogue scene is the Game
scene and its lore scene the LoreVoiceover scene; the lore mode was 1 (queue).

### Client: manual test
Deploy, play and listen; every check is also a kiln.log line, so nothing needs recording.
A suite's caption shows only on the kind of screen it belongs to (world suites during play, menu suites in the main menu) and goes when the suite ends, also when it throws. audio-test runs **compact** suites by itself (volume at most 0.4, about 2 s per sound) and shows an orange caption at
the top of the screen naming what should be heard; the main-menu and pause-menu entry **Kiln audio tour** replays them
at full length.

Deploy:
```sh
export KILN_OUT=<build dir>
mods/tests/audio-test/build.sh                 # the test mod + its own tones (reads a copy of the client exe's reflection)
dotnet build mods/samples/SoundSample/SoundSample.csproj -c Release -o $KILN_OUT/mods/soundsample --artifacts-path $KILN_OUT/dotnet
#   (SoundSample's assets/cooked/ must exist: else KILN_OUT=… EXE=<exe copy> mods/samples/SoundSample/build-assets.sh first)
tools/client/client.sh backup-saves
tools/client/client.sh deploy --real --policy any --mod $KILN_OUT/mods/audio-test --mod $KILN_OUT/mods/soundsample
```
The deploy builds kiln-audio.dll / Kiln.Audio.dll with the tree and composes `client.d/audio.json`.

| # | Where | What you hear | kiln.log |
|---|---|---|---|
| H1 | boot | nothing | `audio: kiln.audio v1 ready (play, voice-over, narration, SoundStart probe)`, `audio: sound system 0x…, scenes music=… ui=… game=…` |
| H2 | main menu, right after it appears (caption) | a short high blip, 0.4 s, both ears: 1661 Hz (SoundSample's replacement of the game's dialog-confirm click) | `audio: #1 play b096387a-… scene ui … (main)`, `audio-test: PASS vanilla UI confirm from OnLoad (main thread): pending -> playing -> finished` |
| H3 | menu, ~1.5 s later | a short beep 1320 Hz, 0.3 s, both ears | `PASS fixture ui_beep (1320 Hz, kiln.assets cue) from a thread-pool thread` |
| H4 | menu, ~1.5 s later | a 997 Hz chime, 0.5 s, both ears | `PASS SoundSample ui_chime (997 Hz)` |
| H5 | menu | a soft two-note chord (C4+E4, 262+330 Hz) fading in over 1 s, steady ~3.5 s (it loops every 3 s: no gap should be heard), getting quieter, a short re-start, about 1 s more, fading out | `PASS PlayMusic … is playing`, `PASS a second PlayMusic replaces the first`, `PASS the track ended as Stopped` |
| H6 | menu | nothing (an unknown cue) | `audio: #N sound ... FAILED: no such cue or voice-over line ...`, `PASS an unknown cue fails soft` |
| H7 | Play → **LOCAL** tab → slot 1 → host offline; stand still | 8 s after spawning the captions start | `saves: world loaded: slot 0x0002 (world1.ksd)`, `audio: voice-over manager 0x… running`, `audio-test: INFO client world session 1: local player 1; the world suite starts in 8 s` |
| H8 | in game | a 330 Hz tone pulsing twice a second, centred on the player (equal in both ears, it moves with you if you walk), 2.5 s; then quieter and 5 m to the east (turn the camera: it moves between ears); stops | `PASS PlayOn(local player 1) plays`, `PASS it follows the entity`, `PASS SetVolume + SetPosition apply`, `PASS Stop ends the loop` |
| H9 | in game | an 880 Hz tone, 2 s, from 10 m east (louder in the ear facing east) | `PASS PlayAt at_tone (880 Hz, 2 s, streamed) 10 m east ...: pending -> playing -> finished` (a streamed take: `#N still playing ... past its cooked length (2000 ms): ended` when the game keeps its instance) |
| H10 | in game | the game's grasshopper chirping, 3 m north, 1.5 s | `PASS a vanilla loop (grasshoppers) plays 3 m north` |
| H11 | in game | voice-over language **English**: two 600 Hz beeps; **German**: three 700 Hz beeps; French/Spanish/Chinese: silence. Subtitle at the bottom: "[audio-test] two beeps (English)" / "drei Pieptöne (Deutsch)" | `audio: #N voice-over 7412911f-… requested: speaker 0, language slot en -> cue …`, `voice-over playing`, `PASS Voiceover.Play vo_line …` (silent case: `no take for voice language id N … silent, as the cooked data says`) |
| H12 | in game | a short male villager exclamation (the game's own voice line) | `PASS Voiceover.Play a vanilla villager line` |
| H13 | in game | English voice: three rising notes 450 / 500 / 550 Hz (2.4 s), music and world sounds dip while they play; other voice languages: silence. Subtitle "[audio-test] narration: three rising notes" | `audio: #N narration dc54029b-… pushed into the lore queue (mode 1 …)`, `narration at the head of the lore queue (playing)`, `PASS Voiceover.Narrate vo_narration` |
| H14 | in game | a game lore narration (English speech), about 2 s, then it fades out | `PASS a vanilla lore line narrates`, `PASS Stop ends the narration` |
| H15 | in game | the 330 Hz pulse again, very briefly (cut by StopAll) | `PASS StopAll on the main thread stops this mod's sounds: 1` |
| H16 | in game | the 330 Hz pulse from 4 m south, 1.5 s (a game loop cue replaced at run time; the game's own loop instead is also a valid outcome if it was loaded before) | `PASS Sound.Replace serves the fixture loop …`, `PASS the replaced cue plays 4 m south` |
| H17 | in game | nothing for ~10 s (an unknown voice-over line) | `PASS an unknown voice-over fails soft` |
| H18 | in game (log only) | – | `PASS the game's own SoundStart calls all ran on the main thread: N calls, 0 off it, 1 thread(s)` |
| S1 | in game, caption "SoundSample 1/9" | 997 Hz chime, 0.5 s | `PASS SoundSample ui_chime` |
| S2 | 2/9 | 1661 Hz blip, 0.4 s (the dialog-confirm override) | `PASS SoundSample dialog_confirm override` |
| S3 | 3/9 | from 8 m east: 440 Hz in the left channel and 660 Hz in the right channel of the source, 1.5 s (tour: 4 s; turn the camera and it pans) | `PASS SoundSample stereo_tone (streamed) plays` |
| S4 | 4/9 | three short blips from 5 m west at 1200, 1500 and 1800 Hz in random order, never the same twice in a row | `PASS SoundSample blip_3d #1..#3` |
| S5 | 5/9 | a steady 220 Hz hum on the player, 2.5 s (tour: 6 s): listen for a click when the 2 s loop restarts | `PASS SoundSample hum_loop plays on the player`, `… still loops` |
| S6 | 6/9 | a 2D swell of 330/331 Hz (a slow beat), 2 s (tour: 10 s, restarting at 8 s) | `PASS SoundSample ambience_loop plays in the Ambience scene` |
| S7 | 7/9 | a C major arpeggio fading in over 1 s, 2 s, fading out (tour: 12 s to its end) | `PASS SoundSample music_track plays in Kiln's music slot` |
| S8 | 8/9 | English voice: two 523 Hz beeps; German: three 784 Hz beeps; French/Spanish/Chinese: the English two beeps (the mod's fallback). Subtitle "Hello, traveller." / "Hallo, Reisender." | `PASS SoundSample greeting` |
| S9 | 9/9 | English voice: three 392 Hz notes (3.2 s), music dips; other voice languages: silence. Subtitle "Long ago, the Flame burned bright over Embervale." | `PASS SoundSample narration_intro` |
| H19 | Settings: voice-over language German, then Escape → **Kiln audio tour** | H11 three 700 Hz beeps, H13 silence (subtitle still shown), S8 three 784 Hz beeps, S9 silence | `language slot de`, `silent, as the cooked data says` |
| H20 | Settings: voice-over language French, tour again | H11 silence, S8 the English beeps | `language slot fr` |
| H21 | sliders: music to 0 → H5/S7 silent; voice-over volume to 0 → H11/S8 silent; then back | – | (states still PASS) |
| H22 | Escape → quit to the main menu, then Quit | no Kiln sound continues into the menu except music/UI ones, and no audio-test caption or subtitle stays on the menu; the game closes normally | `audio: client world stopping: Kiln's world sounds and voice-over lines end at the next frame`; each suite's own summary: `audio-test: INFO world suite done: summary: N PASS, 0 FAIL`. Leaving a world mid-suite: `audio-test: INFO world suite stopped: its world ended (the player left) (...)`. A game client doesn't unload its mods when the game quits, so `OnUnload: summary` shows only when the mod is unloaded (a dedicated server's stop, a reload) |

Directions: x is east, z north. "East" is to the right on the map; the 3D sounds pan as the camera turns.

Joined game: `/audiotest server` on a Kiln server with the client
near the player: the client's log line `entity N is X m from the expected position` (ids differ) or a following
sound (ids match) answers the remote entity-id question (C9).

## Limits
- Not heard in game yet: only the boot capture, the threads and the voice-over manager hook ran in a client (passive);
  playback, voice-over and narration calls need the manual test.
- A streamed take's end: Kiln knows it from the cooked length only, so a mod's streamed one-shot ends at most a margin
  after its length, and a take that starts late (its first read) can lose that much of its tail. The game's own streamed
  cues are judged by their instance.
- Sounds played from a thread-pool thread have no owner mod (the calling mod is known on its own threads only): they don't
  stop when the mod unloads, and `StopAll` doesn't reach them.
- `Voiceover.Play` for a remote client's relayed line has no speaker entity (position only).
- Narration cannot be withdrawn while queued behind another line; Stop applies when it reaches the head.
- `fade_in` is best effort for streamed takes; `PlayParams` flag bits stay 0 (their meaning is [hyp]).
- The relay has no feedback: server handles cannot be observed.
- Without kiln.ui (no `kiln.ui.tick`) sounds start only in game (client steps).

## The contract (dev.27)

Kiln.Audio follows [the API contract](../modding/contract.md) as far as it applies: its calls never throw for a missing capability (they return
`SoundHandle.None`), so no member carries `[RequiresCapability]`.

- **Capabilities.** In the game client: `kiln.audio` and the sigdb features `audio.play`, `audio.voiceover`,
  `audio.narration`. On the server side nothing is played here; `audio.relay` (managed, session, refreshed every 25
  steps) says what `Sound.Available` and `Voiceover.Available` say there: available while a Kiln client with the
  `kiln:audio` channel is connected (or this host's own client can play), pending while the channel is open and
  nobody listens, unavailable without the channel (`needs kiln.net`), `wrong-side` in a client that only joined.
- **Threads.** Reads (`IsPlaying`, `State`, `Info`, `Describe`, `Status`, `SetVolume`, `SetPosition`) are `[AnyThread]`
  (kiln_audio.h: every function any thread). The play and stop calls carry no thread attribute: any thread in the
  game client, the tick thread on the server side, where they go through the relay. The analyzer rule KILN101 reads
  the code and still lists them as tick-thread calls.
