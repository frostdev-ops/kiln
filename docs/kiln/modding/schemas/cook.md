# Kiln cook recipe (assets/cook.json, impact/cook.json), format 1

<!-- Generated from schema/cook.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

How a mod's game-derived files are made on the player's machine from the installed game (mods ship sources and a recipe, never game data). assets/cook.json holds model steps (tools/modelcook: op model, visual) sound steps (tools/soundcook: op sound, cue, voiceover) and animation steps (tools/animcook: op skeleton, clips, graph); each step's cooker is its own "tool", else the file's "tool", else modelcook. impact/cook.json ("tool": "impact") holds Impact programs (tools/impact). Every cooker reads the file with Python's json module: plain JSON, no comments, no trailing commas. Docs: docs/kiln/modding/models.md#cookjson-format-1, docs/kiln/modding/sounds.md, docs/kiln/modding/animations.md, docs/kiln/modding/impact.md.

- Files: `**/assets/cook.json`, `**/impact/cook.json`
- JSON: comments **not allowed**, trailing commas **not allowed**
- Schema: [`schema/cook.schema.json`](../../../../schema/cook.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `format` | `1` | yes |  |
| `tool` | `"modelcook"` \| `"soundcook"` \| `"animcook"` \| `"impact"` |  | The default cooker of the steps (absent: modelcook). Default `"modelcook"`. |

## anchor

{"native": "getSelf", "nth": 0, "section": "code"}, {"pc": 13}, or {"label": "start"\|"end"}.

| Key | Type | Required | Notes |
|---|---|---|---|
| `label` | `"start"` \| `"end"` |  |  |
| `native` | string |  |  |
| `nth` | integer or `"all"` |  |  |
| `pc` | integer |  | min 0 |
| `section` | `"code"` \| `"shutdown"` |  |  |

## animClip

One clip: an animation of the step's glTF file, or (with "from") a clip reworked from another one at cook time. Its name is the key; other steps name it by that.

| Key | Type | Required | Notes |
|---|---|---|---|
| `animation` | string |  | The glTF animation's name. Default: the clip's key. Not with "from". |
| `blend` | object |  | Mixes in another clip, stretched to this one's length: by "weight" every joint, by "bones" only the named joints (not their children unless "children": true). |
| `events` | array of object |  | Timed events inside the clip: {at, sound \| vfx \| foot: left/right \| noise: range, slot, offset, attach, volumeHint}. |
| `from` | string |  | Rework a clip instead of sampling glTF: @input (one of the game's clips, an input of type keen::JointAnimation) or the key of a clip an earlier entry or step made. The operations run in this order, whatever the order of the keys: trim, reverse, speed, mirror, blend, offset. min length 1 |
| `guid` | [guidOrInput](#guidorinput) |  | Default: follows from the mod and the clip's name. @input with a game clip overrides it. |
| `loop` | boolean |  | Default `false`. |
| `mirror` | boolean or object |  | Swaps left and right: true, or { pairs, axis, tolerance }. Pairs are found from the rest pose. |
| `mode` | `"new"` \| `"override"` |  |  |
| `offset` | object |  | Joint -> { rotate: [x, y, z] degrees, translate: [x, y, z] metres }, added on top of every frame in the joint's parent space (glTF's axes). |
| `reverse` | boolean |  | Plays it backwards. |
| `rootMotion` | boolean or object |  | Moves the joint's motion to the entity: true, or {joint (default the root), axes (default xz), rotation}. |
| `speed` | number |  | The length divided by this (2 plays twice as fast), sampled again at 60 frames a second. |
| `tolerance` | [animTolerance](#animtolerance) |  |  |
| `trim` | array of number |  | [start, end]: keeps that span, in seconds of the source. min items 2 max items 2 |

## animRetarget

The file's animations are on a rig of their own: play them on `skeleton` by pairing joints. Each paired joint turns as its source turns away from the reference pose; translations carry over on the hips, scaled by the rigs' heights.

| Key | Type | Required | Notes |
|---|---|---|---|
| `byName` | boolean |  | Pair joints of the same name that the map leaves out. Default `true`. |
| `map` | object or `"names"` |  | Source joint (the file's) -> target joint (the skeleton's; j<index> on a game skeleton). Joints of the same name pair by themselves. |
| `reference` | object |  | A pose of the source that shows the target's rest posture (a T-pose clip for a T-pose skeleton). Default: the file's rest pose. |
| `root` | string |  | The source rig's root joint, for a file without a skin. |
| `scale` | number or `"auto"` |  | How far translations go on the target; auto: the rigs' heights. Default `"auto"`. |
| `skin` | integer |  | Which skin of the file is the source rig. min 0 |
| `translate` | array of string or `"all"` |  | Source joints whose movement carries over, or "all" (rigs that move props or IK helpers by translation). Default: the topmost paired joints, and the hips below a root bone on the ground. |

## animSource

What a state or a layer plays: clip, or blend.

| Key | Type | Required | Notes |
|---|---|---|---|
| `blend` | object |  |  |
| `clip` | string |  | A clip of this recipe. |
| `loop` | boolean |  | Default: the clip's. |
| `speed` | number |  | Default `1`. |

## animStep

| Key | Type | Required | Notes |
|---|---|---|---|
| `op` | `"skeleton"` \| `"clips"` \| `"graph"` | yes | animcook steps are "skeleton", "clips" or "graph" |
| `tool` | [stepTool](#steptool) |  |  |

## animTolerance

Largest error the key reduction may leave.

| Key | Type | Required | Notes |
|---|---|---|---|
| `position` | number |  | Metres. Default `0.0002`. |
| `rotation` | number |  | Quaternion components. Default `0.0005`. |
| `scale` | number |  | Default `0.0005`. |

## assetsRecipe

assets/cook.json: model, sound and animation steps.

| Key | Type | Required | Notes |
|---|---|---|---|
| `blockSounds` | array of string |  | soundcook: the steps `soundcook.py blocks` wrote from the sound blocks of blocks/*.json; it replaces them on its next run. Leave it to the tool. |
| `game` | [game](#game) |  |  |
| `guidNamespace` | [guid](#guid) |  | soundcook: GUIDs of new sounds are UUIDv5(namespace, "<op>:<name>"); pick it once, never change it. |
| `inputs` | object |  | Vanilla inputs read from the installed game: id -> {guid, type, part}. |
| `manifest` | [path](#path) |  | soundcook: the kiln.assets manifest it updates. Default `"assets.json"`. |
| `mod` | string |  | animcook and skinned models: the mod id in the ids of what it makes. Default: the id in the nearest kiln.mod.json. min length 1 |
| `out` | string |  | Output folder, relative to cook.json (gitignored). Default `"cooked"`. min length 1 |
| `steps` | array |  |  |
| `voiceoverFallback` | [voLang](#volang) or null |  | soundcook: the mod-wide default for empty voice slots (null / absent: silence, as the game). |

## attach

A rigid model on one joint: a slot of the skeleton (Head, Chest, HandPalmR, ...) or j<index>. The mesh has no skin; its origin is the joint.

Type: string

## code

Type: string or object

## cueFields

Cue fields (tools/soundcook/sndcook/recipe.py CUE_FIELDS).

| Key | Type | Required | Notes |
|---|---|---|---|
| `avoidRepeatingLastX` | integer |  | min 0 max 4294967295 |
| `gamepadSpeaker` | boolean |  |  |
| `gamepadSpeakerVolume` | number |  |  |
| `gamepadVibration` | boolean |  |  |
| `gamepadVibrationIntensity` | number |  |  |
| `hasRandomPosition` | boolean |  |  |
| `loop` | boolean |  |  |
| `loopSameChosenEntry` | boolean |  |  |
| `maxCount` | integer |  | min 0 max 4294967295 |
| `muteOnMain` | boolean |  |  |
| `pitch` | number |  |  |
| `pitchRandomness` | number |  |  |
| `playScene` | `"Ui"` \| `"Game"` \| `"Music"` \| `"Ambience"` \| `"LoreVoiceover"` |  |  |
| `positional` | [positional](#positional) |  |  |
| `priority` | integer |  | min 0 max 65535 |
| `randomFrontBack` | number |  |  |
| `randomLeftRight` | number |  |  |
| `scene` | `"Sfx"` \| `"Music"` \| `"Ui"` \| `"Voice"` |  |  |
| `sleep` | number |  | Seconds. min 0 |
| `sleepRandomness` | number |  | Seconds. min 0 |
| `stealingMode` | `"Oldest"` \| `"Furthest"` \| `"Ignore"` |  |  |
| `useMaxCountLimiting` | boolean |  |  |
| `volume` | number |  |  |
| `volumeRandomness` | number |  |  |

## cueObject

Type: object

## edit

tools/impact/impact/patch.py: set, config, var, swap, insert, replace, remove, streams, stack.

| Key | Type | Required | Notes |
|---|---|---|---|
| `after` | [anchor](#anchor) |  |  |
| `before` | [anchor](#anchor) |  |  |
| `code` | [code](#code) |  |  |
| `config` | string |  |  |
| `insert` | [code](#code) |  |  |
| `remove` | [anchor](#anchor) |  |  |
| `replace` | [anchor](#anchor) |  |  |
| `section` | `"code"` \| `"shutdown"` |  |  |
| `set` | string |  |  |
| `stack` | integer |  | min 1 |
| `streams` | string |  |  |
| `swap` | string |  |  |
| `var` | string |  |  |
| `with` | string |  |  |

## game

Informational: the side and build the recipe was written on.

| Key | Type | Required | Notes |
|---|---|---|---|
| `madeOn` | string |  |  |
| `side` | `"client"` \| `"server"` |  |  |

## guid

a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)

Type: string

## guidOrInput

a GUID or @<input id>

Type: [guid](#guid) or [inputRef](#inputref)

## impact

impact/cook.json: Impact programs (tools/impact).

| Key | Type | Required | Notes |
|---|---|---|---|
| `game` | [game](#game) |  |  |
| `inputs` | object |  |  |
| `manifest` | [path](#path) |  | The kiln.assets manifest that serves the outputs. Default `"../assets/assets.json"`. |
| `natives` | array of [path](#path) |  | Kiln natives the programs call (impact/natives.json). |
| `out` | string |  | Default `"../assets/cooked/impact"`. min length 1 |
| `steps` | array of object |  |  |
| `tool` | `"impact"` | yes |  |

## input

| Key | Type | Required | Notes |
|---|---|---|---|
| `guid` | [guid](#guid) | yes | Names a **resources** entry (checked against the catalog). |
| `part` | integer |  | Default `0`. min 0 |
| `type` | string | yes | min length 1 |

## inputRef

@<input id>: that input's GUID. Pattern `^@.+$`.

Type: string

## materialRef

a GUID, @<input id>, or the name of a material in this mod's blocks/*.json

Type: [guid](#guid) or [inputRef](#inputref) or string

## modPath

a path relative to the mod's folder min length 1 A file that must exist.

Type: string

## modelStep

| Key | Type | Required | Notes |
|---|---|---|---|
| `op` | `"model"` \| `"visual"` \| `"worn"` | yes | modelcook steps are "model", "visual" or "worn" (a sound step needs "tool": "soundcook") |
| `tool` | [stepTool](#steptool) |  |  |

## path

a path relative to this recipe min length 1 A file that must exist.

Type: string

## positional

positional: {"min": m, "max": m}, false or null

Type: object or `false` or null

## soundName

a name of letters, digits, _ . - (up to 96; unique per op)

Type: string

## soundStep

| Key | Type | Required | Notes |
|---|---|---|---|
| `guid` | [guidOrInput](#guidorinput) |  |  |
| `mode` | `"new"` \| `"override"` |  |  |
| `name` | [soundName](#soundname) | yes |  |
| `op` | `"sound"` \| `"cue"` \| `"voiceover"` | yes | soundcook steps are "sound", "cue" or "voiceover" (a model step needs "tool": "modelcook") |
| `tool` | [stepTool](#steptool) |  |  |

## sourceOpts

gain and normalize exclude each other

| Key | Type | Required | Notes |
|---|---|---|---|
| `channels` | `"mono"` \| `"stereo"` \| `"keep"` |  |  |
| `end` | number |  | min 0 |
| `gain` | number |  |  |
| `normalize` | number |  | Loudness target in LUFS (e.g. -24). |
| `start` | number |  | min 0 |

## stepTool

This step's cooker (default: the file's tool).

Type: `"modelcook"` \| `"soundcook"` \| `"animcook"`

## take

a take is a sound name, @input, a GUID or {"sound": ..., "chance": c}

Type: string or object

## voLang

a voice-over language: en, de, fr, es, zh (or En_Us, De_De, Fr_Fr, Es_Es, Zh_Cn)

Type: string

