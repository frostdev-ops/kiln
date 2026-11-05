# Audio: Keen's sound engine, sound resources, voice-over (for kiln.audio, tools/kfc/sound.py)

Client Steam build 26430625 / svn 1287369 (`enshrouded.exe` sha256 `5d9ef2f1…`, `enshrouded.kfc` version
`1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54Z`); dedicated server Steam 26430253 (`f0d824be…`) for
comparison. RVAs are client RVAs (image base `0x140000000`) unless marked "server". Sources:
- the game's own reflection, read offline from the exe with `tools/kfc` (12,658 types);
- every sound resource of the client and server KFC, decoded with `tools/kfc` and the new `tools/kfc/sound.py`;
- disassembly (capstone) and Ghidra 12.1.2 headless decompiles without auto-analysis, function bounds from `.pdata`;
- the QOA specification (qoaformat.org; spec and reference code MIT) for the codec's names. No game files, no
  kfc-parser code and no third-party code are in the repo.

Evidence tags:
- **[refl]** the game's reflection records;
- **[data]** checked over the game files, with counts;
- **[dis]** disassembly / decompile of the client (or server where marked);
- **[hyp]** hypothesis, not verified. Nothing here was seen in a running client yet (no **[rt]**): see the manual test in `docs/kiln/services/audio.md`.

## Short answers
- **Keen's own engine, no middleware.** No Wwise/FMOD/other markers or DLLs (the client folder ships only
  `steam_api64`, DLSS and Aftermath). Output is WASAPI ("[sound] WASAPI: …", 70+ `[sound]` log strings). Opus is
  linked only for voice chat ("[voice_chat] Could not create opus encoder!"). **[data] [dis]**
- **Codec: QOA without headers.** `keen::SoundDataFormat` has two values, `Uncompressed` (7 sounds) and
  `FramelessQOA` (16,524). FramelessQOA is the QOA codec (LMS predictor + 3.2 bits/sample residuals) with no file
  header, frame headers or stored predictor state, little-endian 64-bit slices interleaved by channel. Every sound is
  **48,000 Hz**; the loader rejects other rates. **[refl] [data] [dis]**
- **Proof of the codec:** Kiln's clean-room decoder (`tools/kfc/kfc/sound.py`) decodes the game's sounds to clean
  audio (speech harmonics and formants in a spectrogram, silent heads and tails, no clipping). Its reference encoder,
  fed the decoded PCM of a whole game sound, re-creates the game's bytes slice for slice: only the padding bits of
  the short last slice differ (3 of 3 sounds checked: 17,121/17,122, 2,547/2,548, 6,070/6,071 slices). **[data]**
- **Data model:** `SoundResource` (samples) ← `SoundContainerResource` (the cue: random pick, volume/pitch
  randomness, loop, 3D distances, voice limiting, scene) ← referenced by GUID from everything that makes noise:
  sequence `SfxNotifierEvent`s, template `AudioResourceComponent`s, items, materials, VFX, UI. Voice-over adds
  `VoiceoverResource` = 5 per-language container GUIDs + durations. All 27,088 sound-related resources re-encode
  byte for byte with `tools/kfc`. **[refl] [data]**
- **Sounds are client-only.** The dedicated server has no sound system (no `[sound]` strings), no `SoundResource`
  and no `SoundContainerResource`. It keeps the 1,276 `VoiceoverResource`s (references only) and the MIDI songs
  (instrument timing). Clients make every sound themselves from replicated state: sequences, spawned templates,
  dialogue state, ambience (biome, weather, time), boss tags. **[data] [dis]**
- **One play function for everything:** `SoundStart` `0x106ee50(SoundSystem*, unused, u32 scene, const Guid*
  container, const PlayParams*) → u32 instance` resolves the container GUID through the game's resource service. UI,
  notifiers, audio components, music and voice-over all end there. Stop `0x106fc50`, volume `0x1069c10`, position
  `0x1069ba0`, is-playing `0x105e8e0`. **[dis]**
- **New sounds can be served by kiln.assets as they are.** The sound system looks resources up through the resource
  service slots +0x7d8/+0x7e8 and reads streamed samples through +0x808, exactly the slots kiln.assets hooks for
  new and overridden resources and blobs. **[dis]** (In-game confirmation pending.)
- **Voice-over:** a dialogue line has parallel `bodyTexts[]` (loca ids: the subtitles) and `voiceovers[]`
  (`VoiceoverResource` GUIDs). The voice-over language is its own setting (`LanguageId`): En_Us → slot 0, De_De 1,
  Fr_Fr 2, Es_Es 3, Zh_Cn 4, anything else → English; an empty slot plays nothing. No lip-sync, viseme or facial data
  exists; a line's `sequence` plays a body gesture. **[refl] [data] [dis]**

## 1. Engine, output, codec

### 1.1 The engine **[data] [dis]**
- Strings: `[sound] WASAPI: Create Device...`, `[sound] Sound platform start up returned error: '%s'.`,
  `[sound] Sound allocator allocation:%,k/%,k`, scene/bus/listener/ducking errors (§3), `[sound] underflow in
  streaming instance %k`. Capture (microphone) is WASAPI too, for voice chat.
- No middleware: no `Wwise`/`AK::`/`Audiokinetic`/`FMOD` strings, no `.bnk`/`.pck` banks, no audio DLLs.
- `[voice_chat] Could not create opus encoder/decoder!`: Opus is voice chat only. No Vorbis, ADPCM or MP3 decoder
  strings.
- The **server** exe has no `[sound]` string at all; it has the voice-chat relay strings and the ECS program names
  (the program table is shared).

### 1.2 FramelessQOA **[dis] [data]**
Decoder: slice decoder `0x753590`, stream decoder `0x10552f0` (8 channels max), LMS init `0x753570`. The exe holds
QOA's dequantisation table as int32 at file offset `0x16b41c0` (RVA `0x16b55c0`), referenced only by
`0x753590`/`0x753ad0`.

| Property | Value | How checked |
|---|---|---|
| Rate | 48,000 frames/s in all 16,531 sounds; loader `0x1076620` compares `framesPerSecond` with `0xBB80` and fails the sound ("Sound resource with invalid sample rate '%d' has not the default of '%d'") | [data] [dis] |
| Slice | one **little-endian** u64 per 20 samples of one channel: bits 63..60 scale factor index, then 20 × 3-bit residual indices from bit 59 down. A short last slice is left-aligned | `mov r8, qword [r13 + r8*8]` reads native order; BE decoding clips 75–100 % of samples, LE gives clean audio [dis] [data] |
| Predictor | per channel: history {0,0,0,0}, weights {0, 0, −8192, 16384} (`0x753570`); predict `Σ h·w >> 13` in 32-bit; residual `dequant[sf][q]`; clamp to int16; weights `±= r >> 4` by the sign of history | [dis] |
| Frames | none: no header, no per-frame LMS state, no reset. The predictor runs over the whole sound (sample-to-sample jumps at 5,120-sample boundaries are 0.99–1.13 × the mean, i.e. no seams) | [data] |
| Channels | slice i of channel c is u64 number `i·channels + c` (`0x10552f0` loops channels per slice index); stereo channels correlate 0.37–0.83 when decoded this way | [dis] [data] |
| Size | `ceil(frameCount / 20) · 8 · channels` bytes: exact for all 16,524 QOA sounds | [data] |
| Uncompressed | interleaved little-endian int16 (7 waterfall loops; size = frames · 2 · channels) | [data] |

The encoder that made the game's data is QOA's reference algorithm with the same start state (re-encoding decoded
PCM reproduces the slices, see Short answers). Only the residuals past `frameCount` in the last slice differ (Keen's
tool fills them; Kiln's encoder writes zeros); they are never played.

### 1.3 Survey of the 16,531 SoundResources **[data]**
| | Count |
|---|---|
| Mono / Stereo / Quad / 5.1 / 7.1 | 15,170 / 1,354 / 5 / 2 / 0 |
| Inline (`data`), ≤ 1.0 s (≤ 48,000 frames) | 3,569 (52 MB) |
| Streamed (`streamedData`, content blob), > 1 s | 12,962 (3.58 GB); `category` 3016399412 in all, bundleId 0, sortKey 0 |
| Lengths | 1–2 s: 5,396 … ≥ 30 s: 1,820 (music, ambience, lore) |

5.1 and quad files are ambience beds (`amb_loop_dungeon_01`, `amb_city_soundscape_01`); their channel order is not
checked **[hyp: FL FR C LFE SL SR]**.

Loudness (active RMS of 50 ms blocks above −50 dBFS, ~20 clips each): English VO −28.5 dBFS (peak −12.9), German VO
−26.1 (−7.9), Chinese VO −17.8 (−2.8), enemy SFX −22.8 (−5.5), UI −30.1 (−7.4). Container volumes compensate
(English VO 0.7, German 0.4, Chinese 0.15), so the levels heard end up near −32…−34 dBFS for all three
languages. **[data]**

## 2. Resources and how they reference each other **[refl] [data]**

### 2.1 Types
`keen::X` is the editor type; the cooked type is `keen::XResource` with the same GUID (`SoundContainer` →
`SoundContainerResource`, `Voiceover` → `VoiceoverResource`), like Model → RenderModel. References in cooked data
keep the editor type's name (`SoundContainerReference` = `ObjectReference<keen::SoundContainer>`).

| Type (layout hash) | Count client / server | Fields |
|---|---|---|
| `SoundResource` (64 B, 0x6d36d524) | 16,531 / 0 | `channelConfiguration` u8, `format` u8, `framesPerSecond` u16, `frameCount` u32, `duration` Time (ns), `data` BlobArray<u8> (inline), `streamedData` ResourceContentReference, `debugName` |
| `SoundContainerResource` (112 B) | 6,951 / 0 | `mode` (Random only), `scene` (Sfx 0, Music 1, Ui 2, Voice 3), `loop`, `loopSameChosenEntry`, `avoidRepeatingLastX`, `volume`, `volumeRandomness`, `pitch`, `pitchRandomness`, `sleep`, `sleepRandomness` (Time), `hasPosition`, `minDistance`, `maxDistance`, `hasRandomPosition`, `randomLeftRight/FrontBack`, `muteOnMain`, gamepad speaker/vibration, `useMaxCountLimiting`, `maxCount`, `stealingMode` (Oldest/Furthest/Ignore), `priority` u16, `entries` BlobArray<BlobVariant<SoundContainerResourceEntry>>, `extraRumble` |
| entry `SoundContainerResourceSoundEntry` (20 B) | 20,781 entries | `chance` f32, `sound` SoundResource GUID. (`SoundContainerVoiceoverEntry` exists in reflection; 0 uses) |
| `VoiceoverResource` (92 B) | 1,276 / 1,276 | `languages[5]` SoundContainerResource GUIDs (en, de, fr, es, zh), `durations[5]` u16 |
| `VoicePackResource` (288 B) | 10 / 0 | 18 containers: attackShort/Long/Finisher…, die, hit, jump, exhausted, staminaDrain, customization, 6 emotes, underWater (player voices; `VoicePackDirectoryResource`, `CharacterSetup.voicePackId`) |
| `AmbientSfxCollectionResource` | 1 / 0 | `effects` 542, `loops` 248, `music` 89 (`AmbientSfxResource`: environment, weather mask, inFog/indoors, start/end time of day, playbackType InBattle/NotInBattle/Always, volume, pitch, sleep, distances, `soundStart`/`soundCount` into `sounds`), `sounds` 3,912 SoundResource GUIDs (direct, no container) |
| `NotifierResource` | 2,319 / 2,319 (the server keeps the references; the cues they name are client-only) | `defaultSound` container + VFX + `playerVoice` (CharacterUtterance) + `soundVolumeHint` |
| `MidiResource`, `MidiSongResource`, `SamplerInstrumentResource` | 15, 15, 7 (server: MIDI only) | player instruments (lute, flute, harp, …): MIDI songs played through sampler instruments |
| reverb, water, waterfall presets | 1–7 each | environment DSP parameters |

Container survey: scene Sfx 5,843 (2,234 positional, 184 looping), Voice 770 (278 positional), Ui 310, Music 28;
3,243 have one entry, 242 ten or more; the most common distance pairs are 1–30 m, 3–50 m, 1–50 m; maxCount
limiting is on in 6,947, stealing Furthest in 6,949. **[data]**

Voice-over survey: languages set (en, de, fr, es, zh): 1,190 × {en, de, zh}, 85 × all five, 1 × {en, de, fr, es}.
`durations` are set in 525 (all lore/journal-style lines), in **10 ms units** = the longest clip of that language's
container (301 of 301 checked equal); the voice-over manager multiplies them by 10,000,000 ns (`0x1233660`).
**[data] [dis]**

### 2.2 Who plays what (census over the client's resources) **[data]**
| Referrer | Count | Field |
|---|---|---|
| `SequenceResource` (animation clip notifies) | 7,554 | `SfxNotifierEvent.sound` |
| `ActorSequenceResource` (abilities, attacks, NPC actions) | 3,755 + 258 | `SfxNotifierEvent.sound`, `VoiceoverNotifierEvent.voiceover` |
| `MaterialFeedback(Collection)` | ~10,000 | collision/footstep/impact sfx per material pair and size |
| `AmbientSfxCollectionResource` | 3,912 | biome effects, loops and **music** (SoundResources directly) |
| `NotifierResource` | 2,264 | `defaultSound` |
| `VfxResource` | 2,044 | `sounds.soundContainer` (sound patch points inside VFX) |
| `ItemInfo` | ~5,000 | `equipSfx`, `unequipSfx`, build-cursor one-shots |
| `TemplateResource` | 1,173 + ~2,300 | `AudioResourceComponent` (looping/one-shot audio of an entity), `DestroyOnLoot`, `MiningNode`, `Extinguish`, `OnDestroy`, enemy `notifierSetup` (awareness barks), `BossMusicTag.bossMusic` (51), … |
| `TemplateResource` voice-over | 746 + 525 + 428 + 524 | `NpcDialog` options / init lines / farewell lines, `LoreReading.voiceover` |
| `JournalRegistryResource` | 525 | `JournalEntry.voiceover` |
| `FbUiBundle` | 217 | `FbUiSounds*` (every menu sound), title music, event jingles |
| `Game38ClientResources` | 9 | pregame/credits music, lore intro/outro music per language |

Music: the 28 Music-scene containers are boss loops (`BossMusicTag` on 11 boss templates), a combat loop, combat
start/end jingles (`AmbientEventSoundsResource`), menu, credits and lore intro/outro. **Exploration music** is the
ambient system: 89 `music` entries of the one `AmbientSfxCollectionResource` (`G38_Biome_*` tracks, 40–200 s
stereo), chosen by environment (biome/dungeon), weather, time of day and battle state.

## 3. The client sound system **[dis]**

### 3.1 Objects
- **SoundSystem**: created by `0x1053c60` (WASAPI, allocator, mixer thread) from the app state machine `0x90a7b0`,
  stored at `app+0x21f80`; accessor `0x907c90` (`mov rax,[rcx+0x21f80]; ret`), used by ~20 client state functions.
  Fields seen: `+0x118`/`+0x120` the resource service, `+0x130` the resource IO, `+0x2d20` the command queue to the
  mixer (`0x1066150`), `+0x2a2a8` the instance pool.
- **Scenes and listeners** (`0x845e10`, called once from `0x8520a0` with the struct at `client+0x698`): u32 scene
  ids `[7]` then listener ids `[7]` (`+0x1c`), indexed by `keen::SoundSceneType`:

  | Index | Scene | createScene (`0x104c4c0`) instances, voices |
  |---|---|---|
  | 0 | Music | 8, 8 |
  | 1 | Ui | 128, 128 |
  | 2 | Game | 8,192, 128 |
  | 3 | Ambience | 1,024, 128 |
  | 4 | CharacterCreation | 128, 128 |
  | 5 | GlobalVoicechat | 32, 32 |
  | 6 | LoreVoiceover | 2, 2 |

  LoreVoiceover ducks Music and Game (`0x106d770(sys, scene[6], scene[0|2])` + `0x106d920` parameters). One listener
  per scene (`0x104c2e0`). **[dis]** (index = enum value: the listener loop walks `SoundSceneType` 0..6 by
  reflection.)
- **Scene volumes** follow the user settings (`g38_options::common::user::SoundSettings`: master, music, ui, game,
  voice, voicechat, ambience, instruments, petsLivestock, npcs, voiceover, water; `voiceoverLanguage` LanguageId)
  through `0x106d9f0` ("Could not find Scene '%k' to set volume") **[refl] [dis]**; which slider feeds which scene is
  **[hyp]** (by name).

### 3.2 The play API (all on the SoundSystem)
| Function | RVA | Signature (as used by the game) |
|---|---|---|
| **SoundStart** | `0x106ee50` | `u32 (SoundSystem*, u64 unused, u32 sceneId, const Guid* container, const PlayParams* p)`: looks the scene up (`0x105b890`; "Could not find scene '%k' to start sound instance."), allocates an instance, resolves `{container, type 0xf257640c}` with the resource service (`0x1080b60` → slot +0x7d8), copies the params, dispatches (`0x10738b0`: random pick, `avoidRepeatingLastX`, positioned or random-positioned start `0x106f470`/`0x1072fb0`). Returns the instance id, 0 on failure. The only client site that resolves a SoundContainerResource by type hash. |
| SoundStop | `0x106fc50` | `void (SoundSystem*, u32 instance, i64 fadeNs)` (game uses 20, 50, 100 ms) |
| SoundSetVolume | `0x1069c10` | `void (SoundSystem*, u32 instance, f32 volume, i64 fadeNs)`; volume 0..1 (logs "Instance Volume should be between 0 and 1") |
| SoundSetPosition | `0x1069ba0` | `void (SoundSystem*, u32 instance, const i64 pos[3])` (the FX controller calls it every frame for attached sounds) |
| SoundIsPlaying | `0x105e8e0` | `bool (SoundSystem*, u32 instance)` |

`PlayParams` (0x28 B): `+0x00` u64 (0 in every caller seen); `+0x08` x, `+0x10` y, `+0x18` z as **i64 32.32
fixed-point metres** (world position; callers compute `(i64)(f × 2³²)`, constant `0x4f800000`); `+0x20` f32
volume; `+0x24` u8 (→ instance flag 0x10); `+0x25` u8 flags: bit 0 → 0x02, bit 1 → 0x04 (the FX controller sets it
for the local player's own sounds), bit 2 → 0x08 (set by a UI caller). The flag meanings are **[hyp]**.

Per-sound data: when an entry is chosen, its `SoundResource` GUID is resolved (`0x105da70`, type `0xdf74e485`) and
loaded on demand (`0x106f730` queue → `0x1076620`): inline `data` is used when its count is non-zero, otherwise the
whole `streamedData` blob is read into memory through the resource IO (`0x1082ec0` → slot **+0x808**,
"[sound] Failed to start load content '%k' error=%k."). The mixer thread decodes QOA from memory in 20-sample
slices (`0x10552f0`, "underflow in streaming instance"). So the first play of a not-yet-loaded long sound waits for
its read. **[dis]**

Threads: no lock in the start/allocate path (`0x106ee50`, `0x1060f20`); commands go to the mixer through a queue.
Callers are the UI, the client-world ECS programs and the voice-over manager **[dis]**; whether they all run on
one thread (main) is **[hyp]**, to log in a real client.

### 3.3 Game-side players
- **UI** (`0x85bb20`, `0xa4f310`, …): `SoundStart(sys, 0, uiScene, &container, {0, 0, 0, 0, volume 1.0, flags})`,
  no position. Containers from `FbUiBundle.sounds` (`FbUiSounds`: 16 groups, 223 references). **[dis] [data]**
- **EcsFxController** (one per client world; `+0x10` SoundSystem*, `+0x44` scene id, `+0x74` local player entity,
  `+0x78` f32 volume[7] per `SoundVolumeHint` (GenericSfx, Player, Pet, Livestock, FriendlyNpc, Enemy, Voiceover),
  pool `+0x98`). Entry points: play at a world position `0x2a0700`, play attached to an entity `0x2a0940`
  (wrappers `0x2a05d0`, `0x2a0650`, `0x2a1e30`), position update `0x2a1800`, volume `0x2a1890`, stop `0x2a1f70`
  (78 callers). Entries of type 0 are sounds, type 1 VFX; the start `0x2a1b50` calls `SoundStart(sys, 0,
  ctrl+0x44, &guid, {pos, volume[hint] × event volume, flags})`. Users: `handle_notifier_events` (`0x30d1a0`:
  sequence/animation notifiers), `play_audio_component` (`0x312610`: `AudioComponent` + `AudioResourceComponent`
  of a template), `play_audio_component_volatile`, `actor_voiceover_events` (`0x2767d0`), `footdown_effects`,
  `area_deposit_effects`, `extinguish_client`, `spell_ammunition_charge_sfx`, `factory_station_sfx`,
  `looped_movement_fx`, `player_building_spark_sfx`, `player_slide_fx`, `local_player_state_indicator`, fishing
  (program names from the program records that point at the callers). **[dis]**
- **Voice-over manager** (`0x1231000`–`0x1234800`): `+0x8` resource service, `+0x10` SoundSystem*, `+0x18` scene,
  `+0x20` position i64[3], `+0x38` pending `VoiceoverResource` GUID, `+0x4c` speaker entity, `+0x50` instance,
  `+0x54` playing, `+0x58` start pending, `+0x60` lore queue (0x48 B entries, count u8 at `+0x4e0`), `+0x700`
  voice-over LanguageId, `+0x704`/`+0x708` volumes.
  - `VoiceoverRequest` `0x1232500(mgr, const Guid* vo, u32 speakerEntity, u8 languageHint)`: hint 0 = all
    languages; 1..5 = only when the voice-over language is En_Us/De_De/Fr_Fr/Es_Es/Zh_Cn. Replaces the pending GUID.
  - `VoiceoverSetPosition` `0x1233420(mgr, const i64 pos[3])`; update `0x1233520` (stop the old line with a 20/50 ms
    fade, start the new one); start `0x1232e40` (resolve type `0x53e9cf8a`, pick `languages[slot]`: En_Us 0, De_De
    1, Fr_Fr 2, Es_Es 3, Zh_Cn 4, **other languages → English**; empty slot → no sound; `SoundStart(sys, 0,
    mgr+0x18, &container, {pos, volume mgr+0x708})`); lore queue `0x1233660` (durations × 10 ms).

## 4. Playback paths

| What | Trigger (where it runs) | Path | Tag |
|---|---|---|---|
| UI click, menu, HUD event | UI code (client) | UI → SoundStart on the Ui scene, 2D | [dis] |
| Ability/attack/footstep sounds | `SfxNotifierEvent` in an actor sequence or animation clip; the server replicates the sequence (`NetworkActor`) and the animation state, the client plays its own copy (docs/re/abilities.md §6) | `handle_notifier_events` → FxController (attached to the actor's slot + `notifierOffset`, or at the event position) → SoundStart on the world scene | [refl] [dis]; [hyp] that the client's sequence copy fires the notifiers |
| Entity sound (projectile hum, campfire, station loop) | template `AudioComponent` + `AudioResourceComponent{soundContainer}` on a replicated entity | `play_audio_component` → FxController, attached, `stopOnDestroy` | [refl] [data] [dis] |
| 3D one-shots from gameplay | `DestroyOnLoot.sfx`, `MiningNode.sfx`, enemy awareness (`EnemySfxEvent{type, transform, sfxGuid}`, emitted by `0x369100` on the client and by server `0x1287f0`) | FxController at a position | [refl] [dis] |
| Biome / dungeon music | ambient system: `AmbientSfxCollectionResource.music` by environment, weather, time, battle | Music scene | [data]; the exact player code not traced |
| Boss music | `BossMusicTag{bossMusic}` on the boss template | Music scene | [data] |
| Menu / credits / lore intro music | `FbUiBundle`, `Game38ClientResources` | Music / Ui scene | [data] |
| NPC dialogue voice-over | dialogue UI on the client emits `SelectDialogOptionEvent{interactingEntityId, selectedOptionId, selectedOptionVoiceoverId}` | `play_dialog_voiceover` `0x460360`: takes the last event with a voice-over GUID, writes `NpcDialogClientSync{selectedOptionId, selectedOptionVoiceoverId}`, calls `VoiceoverRequest(mgr, &vo, entity, 0)`, then positions the voice-over at the speaker's `CurrentTransform` (fallback: the player + 1.5 m up) | [dis] |
| Wake-up / scripted lines | `VoiceoverNotifierEvent{voiceover, languageHint}` in an NPC's actor sequence | `actor_voiceover_events` → manager resolves the language container (`0x1232450`) → FxController attached to the actor | [data] [dis] |
| Lore readings, journal | `LoreReading.voiceover`, `JournalEntry.voiceover`; `LoreVoiceoverAction` (player action) / `LoreVoiceover` event / `LoreVoiceoverState` | `lore_reading_data`, `handle_voiceover_events` → manager lore queue on the LoreVoiceover scene (ducks music and game); options `VoiceoverAutoPlayMode` (Immediately/Queue/Never), `VoiceoverAutoPauseMode` | [refl] [dis] (queue), [hyp] for the server's role |
| Player voice (grunts, emotes) | `VoicePackResource` chosen by `CharacterSetup.voicePackId`; `PlayerUtteranceEvent` | FxController | [refl] [data] |

**What a Kiln client service can call:** `SoundStart`/`Stop`/`SetVolume`/`SetPosition`/`IsPlaying` with the
SoundSystem pointer and a scene id (both captured from `0x845e10`'s arguments at boot, or from the first
`SoundStart` call), and `VoiceoverRequest` + `VoiceoverSetPosition` for dialogue-style voice-over (manager pointer
from `play_dialog_voiceover`'s context, to be captured). The FxController entry points are the alternative for
sounds that follow an entity's slot, but need the client world's controller pointer.

## 5. Server involvement **[data] [dis]**
- The server has no sound system, no SoundResource/SoundContainerResource and no voice data; it never sends a
  "play sound" message that we know of. Its `VoiceoverResource`s are references (no code on the server resolves
  type `0x53e9cf8a`).
- It drives sounds indirectly, through state the clients replicate: actor sequences (SfxNotifier/VoiceoverNotifier),
  animation states, spawned templates (AudioComponent), enemy awareness, dialogue state (`select_dialog_option_input`
  runs on both sides; the client one turns replicated option counters into `ServerSelectDialogOptionEvent`s), boss
  tags. Whether a server-emitted `EnemySfxEvent` reaches clients is **[hyp: no, ECS events are local to their
  world]**.
- Consequence: a server-only (vanilla-client) mod can make vanilla sounds happen only by replicated gameplay (spawn a
  vanilla template that has an AudioComponent, play a vanilla actor sequence on an actor). It cannot play a sound by
  id and cannot add one.

## 6. Voice-over in detail **[refl] [data] [dis]**
- **Binding.** `NpcDialogResource` (inline in the NPC template's `NpcDialog` component, not a separate resource):
  `initLines[]`, `transitionLines[]`, `farewellLines[]`, `dialogOptions[]`, each with `bodyTexts[]` (LocaTagId)
  and `voiceovers[]` (VoiceoverResource GUIDs) as parallel arrays, plus `sequence` (gesture), knowledge
  requirement/unlocks, `uiHintType`, start/end actions; options add `optionText`. The Bard's first init line has a
  null voice-over and a sequence with a `VoiceoverNotifierEvent` instead. Editor-side the line is `NpcDialogText
  {body LocaTagReference, voiceover, locaDirectionNote}`.
- **Takes.** A language's container may hold several takes of the same line (the Bard's greeting: `VO_bard_greeting_
  01_A/B/C_EN`), picked at random with `avoidRepeatingLastX`.
- **Subtitles** are the line's loca texts in the *text* language (kiln.content's loca hooks already answer new tag
  ids per language, docs/kiln/services/content.md). Voice language and text language are independent settings.
- **Languages.** 5 voice slots; the voice-over language comes from the user setting; no fallback from an empty slot
  to English (French/Spanish players hear nothing for the 1,190 lines without fr/es, unless the options menu limits
  the choice: **[hyp]**).
- **Durations** (10 ms units) exist for lore/journal lines and feed the lore queue timing.
- **Lip-sync / facial animation:** none. No type or string for visemes, phonemes, lips, jaw or mouth; NPC "talking"
  is a body animation state set by the line's sequence (`SetAnimationEvent`). Head/brow/jaw joints exist in the
  player skeleton but no data drives them from audio.

## 7. Adding and overriding sounds

### 7.1 A new sound
Needs, on every Kiln **client** (server: nothing, or the `VoiceoverResource` for dialogue lines, harmless):
1. `SoundResource` bytes (new GUID): format FramelessQOA, `framesPerSecond` 48000 (anything else fails to load),
   `frameCount`, `duration`, inline `data` for ≤ 48,000 frames or `streamedData` = a content blob of the QOA bytes
   (category 3016399412 like the game's);
2. the blob (kiln.assets `blob` entry; its `ContentHash` patched into `streamedData.hash` with assets.json's
   `patch` rule, or computed by the cooker: `{size, sha256[0:12]}`);
3. a `SoundContainerResource` (new GUID) with one or more entries → the sound GUIDs, plus scene, volume, pitch,
   distances, loop, limits. This GUID is what everything plays;
4. something that plays it: a Kiln API call (§4 "What a Kiln client service can call"), or game data that
   references the container (a sequence's `SfxNotifierEvent.sound`, a template's `AudioResourceComponent`, an item's
   `equipSfx`, a `BossMusicTag`, a UI bundle entry, an ambient music entry), served as an override.

No bank, registry or sound-specific index exists: containers and sounds are plain resources found by GUID, loaded on
demand. kiln.assets already serves new resources (synthetic handle, type list) and blobs through the hooked slots
the sound system uses. **[dis]**

Offline proof: `tools/kfc` encodes a new `SoundResource` (78 B) and `SoundContainerResource` (144 B) for a 1.5 s
tone with the reference encoder (28,800 B blob); decoding the bytes back gives the same fields and the tone at
73 dB SNR. All 16,531 SoundResources, 6,951 containers, 1,276 voice-overs, 10 voice packs, 2,319 notifiers and the
ambient collection re-encode byte for byte. **[data]** In-game playback is unverified.

### 7.2 Overrides
- **Replace a clip** (same length or not): override the `SoundResource` (new header + new blob, or a blob override
  of the same size). Every container that lists it changes.
- **Replace or extend a cue**: override the `SoundContainerResource` (entries → new sounds; volume, distances).
  Every user of that cue changes (e.g. one footstep container for all grass footsteps).
- **Change one user only**: override the referrer (the sequence's `SfxNotifierEvent.sound`, the template's
  `AudioResourceComponent`, the item's `equipSfx`) to point at a new container.
- **Music**: override `AmbientSfxCollectionResource` (add `music` entries + `sounds`), a boss template's
  `BossMusicTag`, or the menu entries in `FbUiBundle` / `Game38ClientResources`.
- **Voice-over**: a new `VoiceoverResource` per line (container GUIDs per language) and the dialogue data that names
  it (§6). Timing: overrides of already-loaded sounds apply when the game loads them again; boot-time (assets.json)
  registration avoids the race, as for textures.

### 7.3 The cook: audio in, FramelessQOA out
- **Input decode**: WAV (16/24/32-bit PCM, 32-bit float), FLAC, Ogg Vorbis, optionally MP3.
- **Resample** to 48 kHz (games' sources are 48 kHz; a 44.1 kHz file must be converted, the loader refuses it).
- **Channels**: mono for anything positional (all positional game sounds are mono or stereo; 3D sounds should be
  mono), stereo for music/UI; up to 8 supported by the decoder.
- **Encode** FramelessQOA (the QOA reference algorithm, slices little-endian, no headers). Reference: `encode_qoa` in
  `tools/kfc/kfc/sound.py` (pure Python, ~0.4 s per second of mono audio: fine for tests, too slow for VO packs).
- **Loudness**: match the game (§1.3): e.g. VO at about −28 dBFS active RMS with container volume 0.7, or louder
  sources with a lower container volume.

Libraries (all permissive; none copied into the repo by this research):

| Need | Library | Licence | Note |
|---|---|---|---|
| QOA encode | own port of the QOA reference algorithm (`qoa.h`, Dominic Szablewski) | MIT | ~150 lines; output must match `tools/kfc` slice for slice |
| WAV/FLAC/MP3 decode | dr_wav, dr_flac, dr_mp3 (dr_libs, David Reid) or miniaudio (bundles them) | public domain or MIT-0 | single headers, C |
| Ogg Vorbis decode | stb_vorbis (Sean Barrett) | public domain or MIT | Kiln already vendors stb |
| Resample | libsamplerate (BSD-2) or speexdsp resampler (BSD-3); miniaudio's linear resampler for SFX | BSD | sinc quality for music |
| Rust (launcher) alternative | hound (WAV), claxon (FLAC), lewton (Vorbis), rubato (resampler) | Apache-2.0 / MIT | if the cook moves into kiln-manage |

Avoid: libsndfile (LGPL), ffmpeg (LGPL/GPL), sox/libsoxr (GPL/LGPL), Opus/Vorbis *encoders* (not needed).

## 8. Vanilla clients vs Kiln clients
| Feature | Vanilla client | Kiln client (with the mod) |
|---|---|---|
| A new sound, music track, voice-over line | silent (unknown GUID: the start fails or the container has no data) | plays |
| An override of a vanilla sound/cue | hears vanilla | hears the override |
| A server mod changing which **vanilla** container a server-replicated thing uses (sequence on an actor, spawned template) | hears it, if the data it reads is its own vanilla copy (sequences and templates are client copies: a server-side edit of a notifier's container is **not** seen; [hyp], as docs/re/abilities.md §6) | hears the edit only if the client has the same edited data (mod on the client) |
| Server-triggered "play sound X at P" (Kiln.Network) | nothing (no channel) | plays |
| Custom dialogue (new lines, options, voice) | vanilla dialogue (its own template copy) and possible mismatches with the server's options | full |

So audio mods are `clients: "mod"` (the audio files come with the modpack); dialogue mods are `side: "both"`,
`clients: "mod"` (servers refuse vanilla clients).

## 9. Tools and reproduction
- `tools/kfc/sound.py` (new, read-only): `list --grep RX`, `info GUID` (sound, container or voice-over followed to
  its clips), `wav GUID [-o DIR]` (decode a sound, all takes of a container, all languages of a voice-over),
  `roundtrip IN.wav` (encode + decode with the reference codec, size and SNR).
- `tools/kfc/kfc/sound.py`: `decode_qoa`, `decode_pcm16`, `encode_qoa`, `decode_sound(kfc, desc)`, WAV I/O,
  tables. Tests: `tools/kfc/tests/test_sound.py` (synthetic: tables equal the exe's, sizes, a known slice, LE layout,
  round trips, interleave, re-encode stability).
- Survey numbers above: decode every resource of the type with `tools/kfc` (`kfc.py list --type keen::SoundResource`,
  `decode`), as in the scripts under the worker's scratch dir (not committed).

## 10. Open questions
- In game: does a kiln.assets-served container/sound play (first play latency of a streamed one)? Which thread calls
  `SoundStart`? Does the dialogue UI's `SelectDialogOptionEvent` fire for a new option with a new voice-over GUID?
- `PlayParams` flag bits (`+0x24`, `+0x25`) and `+0x00`; how `hasPosition = false` containers treat a position;
  which bus a container's `scene` field selects inside the target scene ("Failed to get scene bus '%k'").
- Which settings slider sets which scene/bus; whether `npcsVolume`/`petsLivestockVolume` are the FxController's
  per-hint volumes.
- The voice-over manager's owner (a client object; capture its pointer via `play_dialog_voiceover`'s first call or
  `0x1233520`).
- Whether the options menu lets a player pick French/Spanish voice-over (1,190 lines have no fr/es take).
- The ambient system's music selection code and how its `sounds[]` are prefetched.
- `NpcDialogClientSync`: replicated to other players, or client-local? (Who hears another player's dialogue.)
- The procedural "buffer" instance (no SoundResource; used by voice chat) as a way to stream PCM Kiln generates.
