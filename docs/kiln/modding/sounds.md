# Custom sounds, music and voice-over

A mod can:
- add new sounds and **cues**, the things the game and code play: UI sounds, 3D sounds, loops, ambience, music;
- replace a vanilla sound, or change a vanilla cue (its takes, volume, distances);
- add **voice-over** lines with one recording per voice language, and narration lines that no NPC speaks.

You record or make audio in any tool. The recipe `assets/cook.json` turns it into the game's format on the player's
machine with `tools/soundcook`, and kiln.assets serves the result to the game at boot. The format and the game's sound
system are in `docs/re/audio.md`; the cooked index is in `docs/kiln/services/audio.md`.

Status:
- **Offline: proven.**
  - Every cooked file decodes and re-encodes byte for byte with tools/kfc.
  - The native encoder equals the Python reference byte for byte.
  - Re-encoding vanilla sounds reproduces the game's own bytes.
- **In game: not yet heard.** Playback is `kiln.audio` / `Kiln.Audio`. Until a real client confirms it, treat every
  in-game claim here as unproven.

## The rule: ship sources and a recipe, never game data
A mod folder holds only what its author made: audio files, JSON, code. The cook runs locally:
- today: `python3 tools/soundcook/soundcook.py recipe <mod>/assets/cook.json`;
- later: the launcher, at install time.

It writes `assets/cooked/`, which is never committed or shipped (`.gitignore`: `assets/cooked/`). New sounds need no
game at all. Only an **override that starts from a vanilla cue or voice-over** reads the installed client (read only),
to copy that cue's other fields. Never put game audio, or audio decoded from the game, in a mod.

```
mods/MySounds/
  kiln.mod.json            side "both" (or "client"), clients "mod": every player needs the audio
  MySounds.dll             a tiny Mod class is enough (the loader needs an entryAssembly today)
  audio/                   your sources, committed
    ui/click.wav
    sfx/boom_1.flac  sfx/boom_2.ogg
    music/forest_night.ogg
    vo/en/hello.wav  vo/de/hello.wav
  assets/
    cook.json              the recipe, committed
    assets.json            kiln.assets entries, committed (written by --update-manifest)
    cooked/                made on the player's machine (gitignored):
                             sound/*.sound.bin  *.qoa  *.cue.bin  *.vo.bin  audio.json  cook-report.json
```

## Sources
- **Formats:**
  - WAV: PCM 8/16/24/32-bit, IEEE float, A-law, mu-law, ADPCM;
  - FLAC;
  - Ogg Vorbis;
  - MP3.

  The format is detected from the content, not the extension.
- **Rate:** anything. The game plays only 48,000 Hz, so other rates are resampled with the speexdsp resampler at
  quality 10. 48 kHz sources skip that step.
- **Channels:** `"channels": "mono" | "stereo" | "keep"`. The game plays 1, 2, 4, 6 or 8 channels.
  - 3D (positional) sounds should be **mono**; the cook warns otherwise.
  - Music and UI may be stereo.
  - `mono` averages the channels; `stereo` duplicates a mono source.
- **Length:**
  - up to 1 s (48,000 frames): stored inline in the sound resource and ready at once;
  - longer: a streamed blob. The first play of a streamed sound waits for its read (`docs/re/audio.md` 3.2). For
    instant effects keep clips at 1 s or less.
- **Levels:** the game's English voice-over sits around −28 dBFS active RMS with cue volume 0.7; UI −30, enemy
  effects −23 (`docs/re/audio.md` 1.3).
  - `"normalize": -28` sets a sound's active RMS (50 ms blocks above −50 dBFS), capped so the peak stays at or below
    −1 dBFS.
  - `"gain": -6` is a fixed change in dB.
  - The cook dithers (TPDF) whenever it changes the samples. Untouched 16-bit 48 kHz WAV/FLAC passes through
    exactly.
- **Loops:** the game loops whole sounds. Cut the source so it loops cleanly: whole cycles, matching ends.
  `"start"`/`"end"` (seconds) trim it in the recipe.

## cook.json (format 1)
The file is plain JSON: the cook tools read it with Python's `json` module, so no comments and no trailing commas
(`kiln lint` reports KL002 and KL003).
```json
{
  "format": 1,
  "tool": "soundcook",
  "out": "cooked",
  "guidNamespace": "<a uuid4 you pick once>",
  "voiceoverFallback": "en",
  "inputs": {
    "confirm": { "guid": "b096387a-cc73-475a-bacd-69ce8cad9fc9", "type": "keen::SoundContainerResource" }
  },
  "steps": [
    { "op": "sound", "name": "click", "src": "../audio/ui/click.wav", "channels": "mono",
      "cue": { "scene": "Ui", "volume": 0.8 } },
    { "op": "sound", "name": "boom_1", "src": "../audio/sfx/boom_1.flac", "channels": "mono", "normalize": -23 },
    { "op": "sound", "name": "boom_2", "src": "../audio/sfx/boom_2.ogg", "channels": "mono", "normalize": -23 },
    { "op": "cue", "name": "boom", "sounds": ["boom_1", "boom_2"], "positional": { "min": 3, "max": 60 },
      "volume": 0.5, "pitchRandomness": 0.05, "maxCount": 4 },
    { "op": "sound", "name": "forest_night", "src": "../audio/music/forest_night.ogg", "channels": "stereo",
      "cue": { "scene": "Music", "loop": true } },
    { "op": "cue", "name": "confirm", "base": "confirm", "guid": "@confirm", "mode": "override", "sounds": ["click"] },
    { "op": "voiceover", "name": "hello", "volume": 0.7, "normalize": -28, "fallback": "en",
      "languages": { "en": ["../audio/vo/en/hello.wav"], "de": ["../audio/vo/de/hello.wav"] },
      "subtitles": { "en": "Hello, traveller.", "de": "Hallo, Reisender." } },
    { "op": "voiceover", "name": "intro", "narration": true, "fallback": null,
      "languages": { "en": ["../audio/vo/en/intro.flac"] } }
  ]
}
```
File level:
- `tool`: the default cooker of the steps (see "One recipe" below).
- `out`: relative to cook.json.
- `guidNamespace`: names -> GUIDs; never change it.
- `voiceoverFallback`: the mod-wide default for empty voice slots; null or absent: silence.
- `inputs`: only for overrides: vanilla resources by GUID + type.

The steps above:
- `click`, `boom_1`, `boom_2`, `forest_night`: sounds (one source file -> one SoundResource); `"cue"` makes a one-take
  cue of the same name.
- `boom`: a cue, which is what game data and code play (takes picked at random).
- `confirm`: an override of a vanilla cue; its other fields come from the player's game.
- `hello`: a voiced dialogue line, with one cue per voice language.
- `intro`: narration, a line no NPC speaks (lore-style); durations are written for the lore queue.

### GUIDs
- Every sound, cue and voice-over gets UUIDv5(`guidNamespace`, `"<op>:<name>"`), e.g. `cue:boom`.
- A voice-over also makes its per-language cues `cue:<vo>.<lang>` and takes `sound:<vo>.<lang>.<i>`.
- An explicit `"guid"` (a GUID or `"@input"`) wins.
- GUIDs are forever: other mods and game data reference them. Pick the namespace once with
  `python3 -c "import uuid; print(uuid.uuid4())"`.
- `cooked/audio.json` lists every final GUID by name. That index is what Kiln.Audio's `SoundId.Named(mod, "boom")`
  reads.

### `sound`
| Key | |
|---|---|
| `name` | letters, digits, `_ . -` |
| `src` | the source file, relative to cook.json, inside the mod folder |
| `channels` | `mono`, `stereo`, `keep` (default) |
| `gain` / `normalize` | dB / target active RMS in dBFS (one of them) |
| `start`, `end` | trim, in seconds |
| `debugName` | default `<mod id>_<name>` (shows in the game's sound log lines) |
| `cue` | `true` or cue fields: also make a cue of the same name with this one take |
| `guid`, `mode` | an override of a vanilla sound: `"guid": "@input"` (type `keen::SoundResource`), `"mode": "override"`. Every vanilla cue that lists that sound plays yours |

### `cue`
| Key | |
|---|---|
| `sounds` | takes: names of this recipe's sounds, `"@input"` or literal vanilla SoundResource GUIDs, or `{"sound": …, "chance": 0.5}` |
| `addSounds` | with `base`: append takes to the vanilla ones (`sounds` replaces them) |
| `base`, `guid`, `mode` | an override: `base` = an input of type `keen::SoundContainerResource`, `"guid": "@thatinput"`, `"mode": "override"`. Every user of that cue changes. With `base` and a new GUID, the vanilla cue is a template for a new one |
| `positional` | `{"min": m, "max": m}`: a 3D cue (attenuates from min to max metres); `false`/absent: 2D |
| `scene` | the container's bus: `Sfx` (default), `Music`, `Ui`, `Voice` |
| `playScene` | Kiln.Audio's default scene: `Ui`, `Game`, `Music`, `Ambience`, `LoreVoiceover` (default from `scene`: Ui → Ui, Music → Music, else Game) |
| `loop`, `loopSameChosenEntry` | loop the chosen take |
| `volume`, `volumeRandomness`, `pitch`, `pitchRandomness` | 0..1 volume (the user's sliders apply on top); pitch 1 = unchanged |
| `sleep`, `sleepRandomness` | seconds between loop repeats |
| `avoidRepeatingLastX` | default min(2, takes − 1) |
| `maxCount`, `useMaxCountLimiting`, `stealingMode`, `priority` | voice limits: default 3, on, `Furthest` (`Oldest`, `Ignore`), 50 (Ui 0) |
| `hasRandomPosition`, `randomLeftRight`, `randomFrontBack`, `muteOnMain`, `gamepadSpeaker`, `gamepadSpeakerVolume`, `gamepadVibration`, `gamepadVibrationIntensity` | the remaining game fields, as named in the game's reflection |

The defaults are the game's most common values (6,951 vanilla cues surveyed).

### `voiceover`
| Key | |
|---|---|
| `languages` | `{"en": [files], "de": [...], "fr", "es", "zh"}`: the game's five voice slots (also `En_Us`, `De_De`, …). Several files are takes, picked at random. Each file may be `{"src", "gain", "normalize", "start", "end", "channels"}` |
| `fallback` | a language whose cue fills the empty slots, or `null` = those slots stay empty: players with that voice language hear nothing, as in the game. Absent: the recipe's `voiceoverFallback` |
| `narration` | `true`: a line without a speaker (lore-style). The resource gets per-language durations (10 ms units, the longest take) for the game's lore queue; its cues default to the vanilla lore settings (`Sfx`, 2D, volume 1) and `playScene` `LoreVoiceover` (ducks music and game) |
| `subtitles` | `{"en": "text", …}` for any text language; goes to `audio.json` for Kiln.Audio |
| cue fields, `cue`, `languageCue` | cue fields on the step apply to every language's cue; `languageCue: {"de": {"volume": 0.4}}` per language (vanilla German and Chinese voice-over is louder and uses lower volumes) |
| `base`, `guid` | an override of a vanilla `keen::VoiceoverResource`: languages you give replace those slots, the others stay vanilla |
| `channels`, `gain`, `normalize` | defaults for the takes (`channels` default `mono`) |

Dialogue cues default to bus `Voice`, 2D: the game's voice-over manager places the line at the speaker. Which
language a player hears is their voice-over setting:
- en → slot 0, de → 1, fr → 2, es → 3, zh → 4;
- any other language → slot 0 (English);
- an empty slot plays nothing.

The subtitles a player reads follow the separate text language.

### One recipe for models and sounds
A step may name its cooker: `{ "tool": "modelcook", "op": "model", … }` in a sound recipe, or
`{ "tool": "soundcook", "op": "sound", … }` in a model recipe. Each tool runs its own steps and hands the others
over: `soundcook.py recipe` runs tools/modelcook for model steps and the other way round; `--no-dispatch` turns
that off. Reports:
- the file's default tool writes `cooked/cook-report.json`;
- the other writes `cooked/cook-report.<tool>.json`.

`docs/kiln/modding/models.md` has the model steps.

## Running it
```sh
python3 tools/soundcook/soundcook.py recipe mods/MySounds/assets/cook.json --update-manifest
python3 tools/soundcook/soundcook.py recipe mods/MySounds/assets/cook.json --check      # cook needed? (no game access)
```
- **Game.** `--game DIR` (default Steam's Enshrouded client) and `--exe EXE` (read only) are needed only for
  overrides and their checks. `--no-game` cooks new sounds with the bundled reflection
  (`tools/soundcook/types/sound-types.json`).
- **The native helper.** `kiln-soundcook` (C: decoders, resampler, QOA encoder) is found in
  `$KILN_OUT/soundcook/bin/`, or built there on first use with `cc` (`tools/soundcook/native/build.sh`). For Windows:
  `build.sh --windows` (clang-cl + xwin, as `native/build.sh`).
- **Without a C compiler.** The pure-Python path cooks 48 kHz WAV only, slower, to the same bytes. `--no-native`
  forces it.
- **`--cross-check`** also encodes every sound with tools/kfc's Python reference and compares the bytes.
- **`--update-manifest`** writes the matching `assets.json` entries and drops stale entries under `cooked/sound/`.
  Without it a mismatch fails the cook.
- **Cook again** when the recipe, a source file or an output changed, or the report says the cook failed;
  `--check` tests exactly that, without the game. The report also records each input's sha256 and layout and the
  game's version and exe sha256, so a launcher can re-cook overrides after a game update. A game update that removes
  a vanilla cue stops the cook with "input … is not in …".

Single files, to try settings:
```sh
python3 tools/soundcook/soundcook.py convert audio/sfx/boom_1.flac --channels mono --normalize -23 -o /tmp/boom.wav
python3 tools/soundcook/soundcook.py wav assets/cooked/sound/boom_1.sound.bin -o /tmp/back.wav   # what the game will play
python3 tools/soundcook/soundcook.py info assets/cooked/sound/boom.cue.bin                       # decoded cue fields
python3 tools/kfc/sound.py info b096387a-cc73-475a-bacd-69ce8cad9fc9      # a vanilla cue and its takes
python3 tools/kfc/sound.py list --grep 'ui_dialog'                        # vanilla sounds by name
```

## Validation (every cook)
Each output is decoded again with tools/kfc and checked:
- **every resource:** re-encodes byte for byte; GUID mode right against the client: `override` must exist, `new`
  must not;
- **sounds:**
  - 48,000 Hz, FramelessQOA;
  - QOA size = ceil(frames/20)·8·channels;
  - inline exactly when ≤ 48,000 frames;
  - the streamed ContentHash is kiln.assets' hash of the blob;
  - clipping ≤ 0.1 % of samples;
  - SNR of the decoded audio against what was encoded. Below 6 dB fails; below 30 dB warns, which is normal for
    noise-like sources (wind, rain): listen with `soundcook.py wav`;
- **cues:** have takes; every take exists (cooked here, or vanilla in the client); distances are sane; 3D cues with
  non-mono takes warn;
- **voice-overs:** every slot names a cue; durations fit 16 bits. Narration takes are therefore at most 655 s.

The report (`cooked/cook-report.json`) lists every check, the levels (peak, active RMS, gain applied), the
encoder used, sources and outputs with sha256.

## Sound blocks
A mod can declare its sounds as blocks instead of writing the steps, in the `sounds` section of `blocks/*.json`:
```jsonc
{ "format": 1,
  "sounds": {
    "frost-hum": { "clips": ["audio/frost_hum.wav"], "volume": 0.6, "range": [3, 40], "loop": true },
    "boom":      { "clips": ["audio/boom_1.wav", "audio/boom_2.ogg"], "range": [5, 80], "pitchRandomness": 0.05, "normalize": -23 } },
  "effects": { "thump": { "sound": "boom" } } }
```
| Field | |
|---|---|
| `clips` | audio files in the mod's folder; more than one: each play picks one (mono for a sound with a `range`) |
| `volume` | 0..1 |
| `range` | `[min, max]` metres; without it the sound is heard alike everywhere (UI, music) |
| `loop`, `scene` (`Sfx`, `Music`, `Ui`, `Voice`), `pitchRandomness`, `normalize` | as the cue and sound steps above |

`kiln cook` first runs `tools/soundcook/soundcook.py blocks <mod>`, which writes the steps into `assets/cook.json`: a `sound` step `<block>.<n>` per clip and a `cue` step `<block>`. It lists them in the recipe's `blockSounds` and replaces only those on the next run, so your own steps stay. A recipe it makes gets a `guidNamespace` that follows from the mod id. Then the recipe cooks as any other. An effect (`Sound = "frost-hum"`, or the handle), a station's running sound and code (`SoundId.Named`) play a block by its name. Its cue's GUID follows from the recipe's namespace, so a server, which cooks nothing, knows it too.

The same block in C#:
```csharp
var hum = Blocks.Define("frost-hum", new SoundBlock { Clips = ["audio/frost_hum.wav"], Volume = 0.6, Range = [3, 40], Loop = true });
Blocks.Define("humming-rack", new StationBlock { Name = "Humming Rack", Processing = new ProcessingBlock { Running = new RunningBlock { Sound = hum } }, ... });
```
A block in code is not visible to the cook: its steps go in `assets/cook.json` by hand, and until they are there the block's warnings say so. A sound block needs `clients: mod`.

## Playing them
- **Game data** plays cues by GUID. Point modded data at your cue: a sequence's `SfxNotifierEvent.sound`, a template's
  `AudioResourceComponent`, an item's `equipSfx`, a boss's `BossMusicTag`, as kiln.content references
  (`docs/re/audio.md` 2.2 lists the referrers).
- **Code** (Kiln clients): Kiln.Audio. The names may still change.
  ```csharp
  var boom = SoundId.Named(Context, "boom");   // reads cooked/audio.json
  Sound.PlayAt(boom, position);
  ```
  - `Sound.Play2D`, `PlayOn(entity)` and `PlayMusic` work the same way.
  - `Voiceover.Play(VoiceoverId.Named(Context, "hello"), npc)` plays a dialogue line; `Voiceover.Narrate(...)` plays
    narration.
- **Overrides** apply wherever the game plays the vanilla cue. Put them in assets.json (boot) so the game never
  loaded the vanilla one first.
- **Who hears it:**
  - **Kiln clients with the mod** hear the sounds: `clients: "mod"`.
  - Vanilla clients hear vanilla sounds.
  - A dedicated server has no sound system:
    - it registers your voice-overs (it keeps the type);
    - with the requested kiln.assets change it skips the `"side": "client"` entries;
    - its cook skips overrides of client-only vanilla cues.

## Sample
`mods/samples/SoundSample`: synthetic tones (no game audio), one frequency per cue, so a recording of the desktop
output can tell them apart. Sources come from `tools/soundcook/samples/tones.py`; run `build-assets.sh --sources` to
regenerate them.

| Cue | Source | Shows |
|---|---|---|
| `ui_chime` | 0.5 s 997 Hz, 16-bit WAV 48 kHz | a UI sound, inline, exact pass-through |
| `stereo_tone` | 4 s 440 Hz left / 660 Hz right, FLAC 44.1 kHz | resampling, streamed, positional 3–60 m (stereo: warned) |
| `blip_3d` | 1200 / 1500 / 1800 Hz, 0.3 s: 24-bit WAV, Ogg 44.1 kHz, MP3 44.1 kHz | the 3D sound: three takes, every decoder |
| `hum_loop` | 2 s 220 Hz, whole cycles | a seamless positional loop |
| `ambience_loop` | 8 s stereo 330/331 Hz with a swell, Ogg | the looped 2D ambience (`playScene` Ambience) |
| `music_track` | 12 s C major arpeggio, Ogg 44.1 kHz | the Music bus |
| `dialog_confirm` (override of `b096387a-…`) | 0.4 s 1661 Hz | a vanilla cue's take replaced, its other fields kept |
| voice-over `greeting` | en: 2 × 523 Hz beeps, de: 3 × 784 Hz beeps | a dialogue line; fr/es/zh fall back to en; subtitles |
| voice-over `narration_intro` | en: 3 × 392 Hz notes, FLAC | narration: durations written; other languages silent (`"fallback": null`) |

Its managed part logs one PASS/FAIL line per check:
- the index exists, and its GUIDs follow the UUIDv5 rule;
- every cooked GUID is in assets.json;
- the fallback and narration slots are as expected;
- where kiln.assets runs, each voice-over reads back as cooked.

## Limits
- **Not heard in game yet.** Serving works the way it does for models: the sound system looks resources and blobs up
  through the slots kiln.assets hooks (`docs/re/audio.md` 3.2). Playback still needs a real client.
- **Loops.** FramelessQOA stores no predictor state, so a loop can only restart from the encoder's start state, as
  a first play does. The cook codes a loop's first slice from silence; a loop that starts near zero comes out
  cleanest. How the game's mixer restarts a looping instance is not verified (client checklist).
- **Reproducibility.** The same sources give the same bytes on Linux and Windows for WAV, FLAC and MP3. For Ogg
  Vorbis about 0.05 % of samples differ by 1 LSB between builds: stb_vorbis's float math under different compilers.
  This is inaudible and changes nothing kiln.assets checks.
- **Sources up to 3 hours each.** Mind the game's sound memory with long streamed music (plan "Risks").
- **No lip-sync:** the game has none (`docs/re/audio.md` 6).

## Tests
```sh
python3 -m unittest discover -s tools/soundcook/tests -v      # 21 tests; game tests skip without the client
python3 -m unittest discover -s tools/kfc/tests -v            # the Python codec (test_sound.py)
```
