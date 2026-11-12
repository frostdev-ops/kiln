# Animations: clips, skeletons and state machines from glTF

A mod can bring its own animation, authored in any tool that exports glTF 2.0:
- **clips**, new or replacing one of the game's;
- a **skeleton** made from the glTF armature;
- a **state-machine graph** that plays them: states, blend spaces, additive layers, and chains that move on by themselves;
- root motion, and events on the clip's timeline (sound, effect, footstep, noise).

The recipe `assets/cook.json` turns the glTF into the game's formats with `tools/animcook`, on the modder's or player's
machine, and kiln.assets serves the result to the game at boot. `kiln cook` runs it. The formats and the engine's side are
in `docs/re/npc-internals.md`.

Status:
- **Proven on a server:** glTF in, cooked, read back, played by the dedicated server's animation engine
  (`mods/tests/animcook-test`, 29 checks): clips on their authored curves, blend spaces, nesting past the 34-transition limit,
  layers with bone masks, chains, a mod's own float inputs.
- **Proven offline:** the codec rebuilds all 5,461 of the game's clips byte for byte. Our encoder, run on every frame of each of
  them at the default tolerance, stays within 0.12 degrees and 1.2 mm on 99% of clips (worst position 13.6 mm, on one clip of 155
  joints), and its files are about 7% larger than the game's. A creature authored in Blender and exported by its glTF exporter,
  cooked and decoded again, matches Blender's own evaluation within 0.6 mm (`tools/animcook/tests/run_blender_check.py`).
- **Proven in a client:** a mod's own skinned held model with its own skeleton, clip and graph, drawn in a real client and
  seen by the owner on 2026-11-11: Kiln Equipment's Sunspire, a skinned model on a 2-joint skeleton with a looping clip and a
  one-state graph, in a copy of `Visuals_Staff_01_Magic_Basic01`. A dedicated server does not run the graph of a held
  model; what it needs of such a model is in [models.md](models.md#on-a-dedicated-server).
- **States added to the player's graph:** proven on a dedicated server, not yet seen in a client: see
  [Player states](#player-states-states-of-your-own-in-the-players-graph).
- **Not proven:** a creature's body drawn in the client, a body actually moved by root motion. See [Manual checks](#manual-checks).

## A recipe

```json
{
  "format": 1,
  "tool": "animcook",
  "steps": [
    { "op": "skeleton", "name": "golem", "gltf": "src/golem.glb" },
    { "op": "clips", "skeleton": "golem", "gltf": "src/golem.glb", "clips": {
        "idle":   { "animation": "Idle", "loop": true },
        "walk_f": { "animation": "WalkF", "loop": true },
        "walk_b": { "animation": "WalkB", "loop": true },
        "slam":   { "animation": "Slam", "events": [ { "at": 0.1, "foot": "left" }, { "at": 0.5, "noise": 8 } ] },
        "step":   { "animation": "Step", "rootMotion": true },
        "flinch": { "animation": "Flinch" },
        "ref":    { "animation": "Ref" } } },
    { "op": "graph", "name": "golem", "skeleton": "golem",
      "slots": { "HandR": "hand_r" },
      "states": {
        "idle": { "engine": "Idle", "clip": "idle" },
        "walk": { "engine": "Walk", "blend": { "input": "moveDirection", "clips": { "-180": "walk_b", "0": "walk_f" } } },
        "slam": { "clip": "slam", "loop": false, "then": "slam_end" },
        "slam_end": { "clip": "idle" } },
      "layers": [
        { "name": "flinch", "overlay": "hit", "clip": "flinch", "reference": "ref", "bones": [ "arm_r", "hand_r" ] } ] }
  ]
}
```

IDs are derived from the mod's id and the names you give, so a recipe has no GUIDs. `mod` in the recipe overrides the id
found in the nearest `kiln.mod.json`.

### `skeleton`

| Key | Meaning |
|---|---|
| `name` | the name steps refer to |
| `gltf` | the file |
| `skin`, `root` | which skin or root node, when the file has several |

Joints are ordered parents first. A node above the root (Blender's armature object) is baked into the root; several roots
get a synthetic root. Space is converted from glTF to the game's left-handed one by mirroring X. The limit is 255 joints.

### `clips`

`skeleton` is a name from a `skeleton` step, or `"@input"` for one of the game's skeletons (see `inputs` in
[cook.json](schemas/cook.md)). `gltf` is needed unless every clip of the step has `from`. Per clip:

| Key | Meaning |
|---|---|
| `animation` | the glTF animation's name; defaults to the clip's key. Not with `from` |
| `loop` | plays as a loop; a warning when its last frame differs from its first by more than 2 degrees or 2 cm |
| `rootMotion` | `true`, or `{ "joint", "axes", "rotation" }`: the motion is taken out of the joint and written as the clip's root motion |
| `events` | `{ "at": seconds, ... }` with `sound`, `vfx`, `foot` or `noise` |
| `tolerance` | `{ "rotation", "position", "scale" }`: how far the keys may stray |
| `guid`, `mode` | `"guid": "@input"` replaces one of the game's clips |

All of glTF's interpolations are read (LINEAR, STEP, CUBICSPLINE) and sampled at 60 frames a second. An animation that moves a
node outside the skeleton is an error; a joint the animation doesn't touch holds its rest pose.

#### Clips reworked from other clips: `from`

A clip can be made from another clip at cook time instead of from glTF: one of the game's clips, or a clip an earlier entry or
step of the recipe made. The result is game data when the source is the game's, so it is made on the machine that cooks, from
the installed game, and stays in `cooked/` like every cooked file: nothing of the game ships with the mod.

```json
{ "op": "clips", "skeleton": "@player_skeleton",
  "clips": { "cinder_guard": { "from": "@block_idle", "mirror": true, "speed": 0.8, "trim": [0.1, 1.4] } } }
```

`@block_idle` is an input of type `keen::JointAnimation`, `@player_skeleton` one of type `keen::ModelHierarchyResource`
([cook.json](schemas/cook.md)). A step whose clips all have `from` needs no `gltf`. A clip has `from` or `animation`, not both.

| Key | Meaning |
|---|---|
| `from` | `"@input"` (a game clip) or the key of a clip made earlier in the recipe; it must be a clip of the step's skeleton |
| `trim` | `[start, end]`, seconds of the source: keeps that span |
| `reverse` | `true`: plays it backwards |
| `speed` | above 0: the length is divided by it (2 is twice as fast), sampled again at 60 frames a second |
| `mirror` | `true`, or `{ "pairs": { "j12": "j40" }, "axis": "x", "tolerance": 0.005 }`: swaps left and right |
| `blend` | `{ "with": clip, "weight": w }` or `{ "with": clip, "bones": [joints] \| { joint: weight }, "children": true }` |
| `offset` | `{ joint: { "rotate": [x, y, z], "translate": [x, y, z] } }`: degrees and metres on top of every frame |

**The order is fixed**, whatever the order of the keys: `trim`, `reverse`, `speed`, `mirror`, `blend`, `offset`. So `trim` is
always in seconds of the source, `blend.with` is mixed into the mirrored clip (it is not mirrored itself), and an offset comes
last. To change the order, make one clip from another: `"b": { "from": "a", ... }`.

- **trim** keeps `[start, end]`; the result lasts `end - start` (rounded to whole frames) and its first and last poses are the
  source's at `start` and `end`. A span outside the clip is an error.
- **reverse**: frame k is the source's frame n - k.
- **speed** reads the source between its frames with the game's own sampler, so speed 0.5 is not a stretched copy of 30 samples
  a second. Speed 2 keeps every other frame's worth of motion: a joint that snaps within one frame (some hand and IK joints of the
  player's clips turn 120 to 173 degrees in a frame) cannot keep its snap.
- **mirror** swaps left and right, see below.
- **blend** mixes another clip in, stretched to this clip's length (a warning says so when the lengths differ). By `weight` every
  joint is interpolated (slerp for rotations, lerp for positions and scales): 0 is this clip, 1 the other. By `bones` each named
  joint comes from the other clip (a list: weight 1) or is mixed by its weight, every other joint stays. `bones` means exactly the
  named joints, as a graph layer's `bones` does; `"children": true` gives a named joint's weight to its children too (a child named
  itself keeps its own). Upper body of one clip and legs of another: `"bones": ["j10"], "children": true`.
- **offset** turns and moves named joints on top of every frame, in the joint's parent space and in glTF's axes (as Blender shows
  the parent; the game's space mirrors x). `rotate` is x, then y, then z, in degrees. Other joints keep their values exactly.

Joints are named `j<index>` on a game skeleton and by their names on a skeleton of the recipe, as everywhere in animcook.

`loop`, `tolerance`, `guid` and `mode` work on a `from` clip as on any clip: `"guid": "@input"` replaces that game clip. `loop`
defaults to the source's, except after a `trim` (the seam rarely matches; the usual warning tells). `events` given on a `from`
clip replace the source's events (a warning says how many). `rootMotion: false` drops the source's root motion. `rootMotion: true`
(or `{ ... }`) takes root motion out of the pose and works only when the source has none: a source with root motion is refused,
because its motion is already out of the pose and follows the operations by itself.

**Siblings.** A game clip can carry root motion (`RootMotionAnimation` and `LveAnimation`) and events (`AnimationSequenceContainer`
with its `SequenceResource`s: foot plants, sounds, effects, foley, noise) under its GUID. They follow each operation or are dropped
with a warning in the cook report that names the clip:

| Operation | Root motion | Events |
|---|---|---|
| `trim` | cut to the span, started again at the new first frame (placed where the source's path starts) | inside the span: moved to the new time; outside: dropped, with a warning |
| `reverse` | the path walked backwards | times reversed (an event with a duration keeps covering the same motion) |
| `speed` | the same path over the new length | times and durations divided by the speed |
| `mirror` | reflected across the plane (sideways motion and turns change side) | left and right foot plants swap; slots with a left and a right (`FootL`/`FootR`, `HandL`/`HandR`, `KneeL`/`KneeR`, `HandPalmL`/`HandPalmR`, `FootRL`/`FootRR`) swap; a world offset is reflected; a slot with no other side (`ArmL`) and a local offset stay, with a warning |
| `blend` | mixed with the other clip's by the weight of the skeleton's root joint (by `weight`: that weight; by `bones`: the root's weight, 0 when it is not named); a clip without root motion counts as standing still; the other clip's root motion is dropped with a warning when the root takes none of it | the source's stay; the other clip's are dropped, with a warning |
| `offset` | unchanged | unchanged |

The `LveAnimation` is written again from the resulting path, as for an authored clip. An event of a type the cook's reflection
cannot write is dropped with a warning (with the installed game's reflection, every event type of the game's clips is known).
When a reworked clip replaces a game clip (`"guid": "@input"`) and has no root motion or no events where the game's clip has them,
the game's are replaced by a still path or an empty event list, with a warning: otherwise the old ones would play with the new clip.

**Mirror pairing.** A game skeleton's joints have no names, so left and right are found from the rest pose, on a skeleton of the
recipe the same way (names such as `_l` and `_r` are not read). The plane of symmetry goes through the model origin, normal to
`axis` (default `x`, the side axis of the game's space and of glTF). Parents first, each joint looks among the children of its
parent's mirror for a joint at the mirror image of its rest position, within `tolerance` metres (default 0.3 % of the skeleton's
size, at least 1 mm: 5.5 mm on the player). A joint at its own mirror image is a centre joint. Where several joints sit at the
same place (eye and jaw helpers), the one whose rest frame mirrors the way its parent's does wins; if two fit within 5 degrees of
each other the pairing is ambiguous and the cook refuses it: name the partner in `mirror.pairs`. `pairs` overrides the pairing for
the joints it names; a pair whose parents are not a pair is refused. A joint with no partner is unplaced: under a centre joint it
is mirrored in place, under a paired joint it keeps its own local values and follows its parent. The cook report lists the pairs,
the number of centre joints and the unplaced joints.

Mirroring a pose is more than a swap: each joint takes its partner's turn away from the rest pose, reflected across the plane, on
its own rest frame (`R'k = refl(Rj Rj0^-1) Rk0`), and its partner's translation reflected plus the difference of their rest
positions. So the left and right side may have rest frames that are not mirror images of each other (on the player, 39 of
the 41 pairs are turned 180 degrees against each other's mirror image), and mirroring twice gives the clip back.

On the player's skeleton (102 joints) the pairing finds 41 pairs and 17 centre joints and is not ambiguous; `j66`, `j67` and
`j96` (children of `j44`, `j43` and `j70` with no joint at their mirror image) are unplaced and follow their parent.

**Limits.** No retargeting of a `from` clip: the source must be a clip of the step's skeleton (a `with` of another skeleton is
an error, by the clips' hierarchy hash). A mirror of a skeleton that is not symmetric about a plane through its origin finds few
pairs: see the report. Scale tracks are swapped between a pair, not reflected. A reworked clip is sampled at 60 frames a second
and encoded again, so it carries the encoder's error once more (below).

**Proven offline** (`tools/animcook/tests/test_clipops.py`, no game): on the sample golem and on an asymmetric rig of the test
(right arm's rest frames turned 90 degrees about the bone, a finger only on the left, a prop only on the right), through the
encoder: mirror twice is the source within 0.3 degrees and 1 mm; mirror once puts the left arm's model-space motion on the right
arm; reverse twice is the source, reverse once has frame k equal to the source's n - k; speed 2 and 0.5 halve and double the
length and match the source at 2t and t/2; trim keeps the span; a blend by mask takes each joint from the clip it names, by weight
0, 0.25, 0.5 and 1 it is the source, a quarter, half and all the way to the other clip in model space; an offset turns its joint by
the angle given and leaves every other joint's values alone; root motion and events through each operation as in the table; the
refusals (unknown `from`, `from` with `animation`, a `with` of another skeleton, a trim outside the clip, speed 0, an unknown joint,
an ambiguous mirror). Each test was checked to fail with its operation broken.

**Proven on the game's clips** (`tools/animcook/tests/game_clipops_check.py`, reads the installed game, writes only its report):
41 of the 1,216 clips of the player's skeleton, among them the block idle `263941ec-3361-42b0-96e2-35a29f22224b`, 16 with root
motion and 25 with events, each operation through the encoder and compared with the game's own clip in model space. Worst (median):

| Round trip | Rotation | Position | Root motion | Events |
|---|---|---|---|---|
| mirror twice | 0.47 (0.28) degrees | 6.0 (2.4) mm | exact | all back, same times |
| reverse twice | 0.47 (0.30) degrees | 5.8 (2.4) mm | exact | all back, same times |
| trim of the whole span | 0.35 (0.21) degrees | 4.5 (1.8) mm | exact | all back |
| speed 2 alone, against the game's clip at 2t | 0.31 (0.20) degrees | 3.5 (1.7) mm | exact | all kept |
| speed 0.5 alone, against the game's clip at t/2 | 0.34 (0.22) degrees | 5.5 (1.7) mm | exact | all kept |
| speed 2 then 0.5 | 178 (4.4) degrees | 504 (14) mm | 0.29 m | all back |

A trim of the whole span is the encoder's own error (one pass); the others add a second pass or a resampling. Speed 2
then 0.5 loses what 30 samples a second cannot hold: the worst clips have joints that snap 120 to 173 degrees within one frame.

**Not proven:** anything drawn or felt in the client: a mirrored block or a slowed idle on the player, root motion of a reworked
clip moving the body, a mirrored clip's foot plants landing on the right foot. The LveAnimation's exact use by the engine was
never read (as for authored clips).

The step's `retarget` plays a file authored on another rig on `skeleton`: see [Retargeting](#retargeting).

### Retargeting

Without `retarget`, a clips file must carry the skeleton's own joints and rest pose (for a game skeleton, joints `j<index>` from a `kfc.py` export).
With it, the file may be any rig: an animation pack, another tool's skeleton, a creature of other proportions.

```json
{ "op": "clips", "skeleton": "@vukah", "gltf": "src/anim-pack.glb",
  "retarget": { "map": { "mixamorig:Hips": "j1", "mixamorig:Spine": "j2", "mixamorig:Head": "j17" } },
  "clips": { "idle": { "animation": "Idle", "loop": true } } }
```

`@vukah` is an input of the recipe of type `keen::ModelHierarchyResource` ([cook.json](schemas/cook.md)); a skeleton step's name works the same.

| Key | Meaning |
|---|---|
| `map` | The file's joint -> the skeleton's joint (`j<index>` on a game skeleton). Joints of the same name pair by themselves |
| `byName` | `false`: only `map` pairs |
| `skin`, `root` | Which armature of the file is the source rig, as in a `skeleton` step |
| `reference` | `{ "animation", "at" }`: a frame of the file that shows the skeleton's rest posture, when the file's rest pose doesn't (an A-pose rig for a T-pose skeleton) |
| `translate` | The file's joints whose movement carries over, or `"all"`. Default: the topmost paired joints, and the hips below a root bone on the ground |
| `scale` | How far translations go; default `"auto"`, the rigs' hip heights (or whole heights) |

How it works: each paired joint turns in model space as its source joint turns away from the reference pose, so rest orientations and bone lengths
may differ freely. Joints of the skeleton with no pair keep their rest pose on their parent. Positions come from the skeleton's own bones, except on the
`translate` joints, whose travel is scaled. Both rigs must face +Z with y up, as glTF files do. The report's `retarget` section lists what paired and what
didn't.

`retarget: {}` enables pairing by name with the defaults; `false` disables retargeting. Invalid parent chains, zero joint scales and non-finite
transforms are refused. If both rigs have zero usable height, automatic translation scaling uses 1; an explicit positive, finite `scale` overrides it.

Not done here: foot or hand IK after the transfer (feet may slide when legs differ in length), and mirroring left onto right.

Proven offline: `tools/animcook/tests/test_retarget.py` plays the sample's animations on rigs with other names, rest frames turned up to 120 degrees, 1.5
times the size and joints left out or added, and every joint lands where it must within 0.01 mm. `tools/animcook/tests/game_retarget_check.py` takes 55
of the game's clips on 5 skeletons (4 to 112 joints), re-expresses each on a rig of other names, turned rest frames and 1.3 times the size, and
retargets it back through the encoder: with `"translate": "all"` it is the game's clip again within 0.32 degrees and 13 mm, the encoder's own tolerance.
With the default, rotations still match; joints the game moves by translation (props, IK helpers) stay at their rest offset, as designed.

Proven on a dedicated server: `mods/tests/retarget-test` (assets from its `build-assets.py`, no game data) gives a creature a body and clips retargeted
from a rig of other joint names and 1.3 times its size. Its graph runs, it stands in Idle, and its slam attack raises the engine's right hand to 2.09 m,
against 2.11 m for the motion as authored on the body's own rig (the bigger rig's bones would give 2.44 m): 5 PASS. Not seen drawn in the client.

### `graph`

A state's source is one of `clip` or `blend`; a state's id comes from `engine` (a name the engine knows, below), `id`, or
its name.

| Key | Meaning |
|---|---|
| `engine` | this state is what the engine asks for: `Idle`, `Walk`, `Run`, `Sprint`, `IdleCombat`, `Fall`, `IdleSneak`, `Sneak`, `Dead`, `Stun` |
| `clip` | a clip's key |
| `blend` | `{ "input", "clips": { "value": clip }, "circular", "smoothing" }`: a blend space on a float input |
| `then` | moves on to that state when its clip ends |
| `loop`, `speed`, `blendIn`, `restart` | playback and how it is entered |

A float input is an engine name (`moveDirection`, `leanYaw`, `lookPitch`, ...) or a name of your own, which a sequence sets
with `SetAnimationFloatInputEvent` (with a `duration`, on a body with Locomotion).

Layers play over the states: always on, or through an `overlay` such as `hit`, the one the engine fires on a hit reaction.
Additive layers need a `reference` clip. `bones` limits a layer to exactly those joints, not their children: a list, or
`{ joint: weight }`. `weight`, `fadeIn`, `fadeOut` and `mode` (`additive`, `override`) tune it.

**What the compiler does for you:**
- **More than 34 states.** A machine holds at most 34 global transitions. Past that, states go into nested machines, each
  entered on a list of ids. You name states; the graph's shape is not your concern.
- **Chains.** A `then` sequence becomes its own machine, entered from outside. Inside one machine a global transition on the
  first state would pull it back as long as the input holds that id.
- **Layers.** An overlay becomes the rest, play, play-again pattern of the game's own graphs.

**Limits it reports:** an entity holds 16 float inputs and the engine fills 12 on a creature that moves, so a graph gets 4 of
its own; 255 joints; a clip's joints against its skeleton (by the hierarchy hash); a blend or layer's unknown clip or bone.

### What it writes

Into `<out>` (default `cooked/`), listed in `assets/assets.json` with `--update-manifest`:

| File | What |
|---|---|
| `<name>.skeleton.bin`, `<name>.skeleton.json` | the skeleton, and a description of it for other tools |
| `clip_<name>.bin`, `.rootmotion.bin`, `.lve.bin`, `.events.bin`, `.notify.bin` | the clip and its siblings under its GUID; `.events.<i>.bin` when a reworked game clip has several event sequences |
| `clips.json` | each clip's guid, duration, loop |
| `graph_<name>.bin`, `<name>.graph.json` | the graph, and its states with ids and GUIDs, nodes, inputs, overlays, warnings |
| `mask_<graph>.<layer>.bin` | a layer's bone mask |
| `cook-report.json` | sources and hashes: what makes a later cook skip |

A sequence or template names a state by the GUID in `<name>.graph.json`. The game's own graphs and states are in the
`graphs` section of the [catalog](catalogs.md).

## Player states: states of your own in the player's graph

This is about the player's **animation graph**. The game's own player states (gliding, sitting, ...) are something else:
[player-states.md](player-states.md).

The player is animated by one graph of the game's: a set of state machines in which each state plays a clip, and the game
moves to a state when a sequence (an attack, a guard, a cast) asks for it. A weapon of your own that swings, guards or casts in
its own way needs states of its own in that graph. You don't edit the graph: you list your states in
`assets/player-states.json`, each one placed **like** a state of the game's, and Kiln adds the states of every enabled mod to
the game's graph when the game starts.

### The file

`<mod>/assets/player-states.json`:

```json
{
  "format": 1,
  "states": {
    "cinder-slash-1":  { "clip": "cinder_slash_1", "like": "Attack_Sword_Shield_01" },
    "cinder-slash-2":  { "clip": "cinder_slash_2", "like": "Attack_Sword_Shield_02", "blendIn": 0.12 },
    "cinder-uppercut": { "clip": "cinder_uppercut", "like": "SM_Attack_Sword_Shield_FullBody/Attack_Jump_Uppercut", "speed": 1.1 },
    "cinder-guard":    { "clip": "cinder_guard", "like": "Block_FullBody", "loop": true },
    "cinder-cast":     { "clip": "cinder_cast", "like": "Staff_Cast_U_Shoot" }
  }
}
```

| Key | Meaning |
|---|---|
| the state's name (the key) | 1 to 64 characters, no `/`, upper and lower case differ, unique in the mod |
| `clip` | a clip of your mod's cook, by its key in the recipe's [clips](#clips) step; or a clip's GUID written out (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`), one that the game or another mod serves |
| `like` | the game's state yours is placed like (its **neighbour**): its name, or `Machine/State` when several machines have a state of that name |
| `blendIn` | seconds to blend into your state, 0 to 5. Default: the neighbour's |
| `loop` | `true` or `false`. Default: the clip's own |
| `speed` | how fast the clip plays, above 0. Default: the neighbour's, which is 1 in every state of the game |

Strict JSON: no comments, no trailing commas, no key twice, no key outside the table. The mod's id comes from its
`kiln.mod.json`; the file has none. Schema: `schema/player-states.schema.json`.

### `like`: where your state goes

Your state goes into the neighbour's machine and takes the neighbour's part in everything around it:
- it is entered and left as the neighbour is: the same blend times, the same ways out to other states (after a dodge roll, the
  same cancel into the dodge attack), and back to rest when an overlay ends;
- it plays your clip as the neighbour plays its own: the same layer and bone mask (a guard like `Block_FullBody` plays over the
  legs that keep walking while you block);
- wherever the game lists the neighbour (the lists that open its machine, reset the upper body, or switch off foot placement),
  your state is listed too.

The neighbours that load. The names are the game's; all of them are in the `graphs` section of the [catalog](catalogs.md),
and `PlayerAnimStates.FindGameStates` (below) finds them.

| Machine | What it plays | States you can name | Kind |
|---|---|---|---|
| `SM_Attack_Unarmed_FullBody` | unarmed attacks | every state (2) | `state` |
| `SM_Attack_Sword_Shield_FullBody` | melee attacks of the sword-and-shield family | every state (8) | `state` |
| `SM_Attack_Hammer_FullBody` | melee attacks of the hammer family (its states include axe attacks) | every state (16) | `state` |
| `SM_Attack_Greatsword_FullBody` | melee attacks of the greatsword family | every state (12) | `state` |
| `SM_Attack_Dual_Daggers_FullBody` | melee attacks of the dual-dagger family | every state (13) | `state` |
| `SM_Jump_Attack_FullBody` | the attack out of a jump, in three parts | `Jump_Attack_Start_FullBody`, `Jump_Attack_Loop_FullBody`, `Jump_Attack_End_FullBody` | `state` |
| `SM_Main` | the whole body | `Block_FullBody` (the guard), `Parry_FullBody`, `DodgeRoll_FullBody`, `Dodge_Attack_FullBody` (the attack out of a dodge), `Sneak_Attack_FullBody`, `Attack_Skill_Kick_FullBody` (the kick) | `state` |
| `SM_Attack_Staff_FullBody` | staff attacks of the whole body | every state (4) | `state` |
| `SM_Attack_Wand_FullBody` | wand attacks of the whole body | every state (2) | `state` |
| `SM_Staff_Overlay` | staff casts, channels and reloads, over a body that keeps moving | 12 of its 14 states | `overlay` |
| `SM_Wand_Overlay` | wand casts and reloads | 5 of its 6 states | `overlay` |
| `SM_Ground_Bow_Overlay`, `SM_Air_Bow_Overlay` | bow shots and reloads, on the ground and in the air | 4 states each | `overlay` |
| `SM_Ranged_Attack_Additive` | bow shots and reloads while sneaking | `Bow_Sneak_Shoot`, `Bow_Sneak_Reload`, `Bow_Sneak_Reload_Fast` | `overlay` |

Refused, each with its reason:
- `Idle_Block` and `Move_Block`: the legs while blocking, chosen by how the player moves, never by a sequence. A guard of your
  own is like `Block_FullBody`.
- The `DoNothing` of an overlay machine: the rest state every cast or shot goes back to; nothing enters it.
- `Ground_Bow_Overlay`, `Staff_Overlay` and `Air_Bow_Overlay` in `SM_Ranged_Attack_Additive`: each holds a whole machine. Name
  one of that machine's states.
- `Attack_Magic_Wand_01_bis`: no overlay of its own; the game enters it only from `Attack_Magic_Wand_01`.
- `Staff_Channel_U_Loop`: entered on two overlays, not on one of its own.
- Every state of the other machines (walking, swimming, climbing, tools, ...).

### How a sequence enters your state

Each of your states has a GUID, made from your mod's id and the state's name: UUIDv5 of `<mod>:state:<name>` in Kiln's
namespace, the same rule as `BlockIds.Derive(mod, "state", name)` and `PlayerAnimStates.GuidOf(mod, name)`. A sequence names
that GUID:
- kind `state` (the state takes over what the body does): in a `SetAnimationEvent`, as `animationName`;
- kind `overlay` (the state plays over whatever the body does, a cast while walking): in a `SetAnimationOverlayEvent`, as
  `overlayName`.

The kind follows from the neighbour's machine (the table above), and `cooked/player-states.json` lists it. A sequence of your
mod that sets the event plays your state; the proof runs below used copies of the game's own sequences with the GUID changed.
The graph compares a number made from the GUID (FNV-1a-32 of its 16 bytes): a name whose number the game already uses is
refused, and another name fixes it.

### The clip

- It is made for the player's skeleton (102 joints). The merge compares it with the neighbour's own clip and refuses a clip of
  another skeleton.
- It is served: cooked by your mod and listed in `assets/assets.json` (`--update-manifest`), or a GUID the game or another
  enabled mod serves.
- It is blended in and out where the neighbour's clip is, with the neighbour's blend times: start and end it in poses of the
  same family as the neighbour's (an attack from the combat stance and back to it, a guard held up), or the blends show.
- For an overlay and for a block, your clip plays in the neighbour's layer, under its bone mask: the arms of a cast, the guard
  over the legs. The rest of the body plays what the game plays.
- Its length, loop, root motion and events are the clip's own.

### Checking it

```
python3 tools/animcook/animcook.py player-states assets/player-states.json
```

checks one file against the player's graph of the installed game: each state live, or refused with its reason (exit code 1
when one is refused). `--graph FILE.bin` gives it a graph to check against; with neither a game nor `--graph` it checks the
file's shape and names only and says what it could not check.

The cook checks it too: `animcook.py recipe assets/cook.json` with a `player-states.json` beside the recipe checks it after its
own steps (so your clips exist), prints one line per state, writes `cooked/player-states.json` (per state its `guid`, `id`,
`kind`, `machine`, `like`, `status` and `reason`) and fails when a state is refused. A recipe without the file cooks as
before.

```
python3 tools/animcook/animcook.py player-graph --mod DIR [--mod DIR ...] --out DIR
```

merges the states of several mods into the game's graph as the game does at boot, and writes the graph
(`player-graph.bin`) and its report (`player-graph.report.json`) into `--out`. The graph is made from the game's files, so
`--out` may not be inside a git work tree.

### At boot

When the game or the server starts, Kiln reads the player's graph once, adds the states of every enabled mod, and serves the
result for the whole run, on the server and on every player's game: the same mods give the same graph, byte for byte. With no
player states in any mod, nothing is read and the game's graph is the game's own. A state that cannot load is left out with
its reason, and the others load; `PlayerAnimStates.Find` gives the reason. A merge that cannot be proven on the installed game
(a game update changed the format) serves nothing: the game's graph stays, and every state is reported unavailable.

What kiln.log says, one of these per start, and a warning line with the reason for each refused state:

```
playeranim: 3 state(s) of 1 mod(s) merged into the player's graph: 3 machine(s), 437260 -> 438620 bytes, 67 ms (types 4.7, clip facts 0.1, merge 62.4, put 0.1), hash b92020749983197c
playeranim: no mod defines a player state: the game's graph is untouched
playeranim: no player state could be merged (1 refused): the game's graph is untouched
playeranim: off: <reason>: the game's graph is untouched, 3 state(s) unavailable
```

The same, with every state's status and reason, is in `<game>/kiln/data/playeranim/status.json`. The service is
`kiln.playeranim`; `"enabled": false` in `<game>/kiln/config/playeranim.json` turns it off (the game's graph is served, and
mods' player states are unavailable).

### Your manifest, and joining a server

`kiln.mod.json` has to state both keys, written out:

```json
"side": "both", "clients": "mod"
```

The server plays every player's graph and every player's game draws it, so your mod has to be on both. Without both keys
every player state of the mod is refused with that reason. With them, a player without Kiln, or with Kiln but without your
mod, cannot join a server that runs it unless the server sets `modSync` to `off` ([network.md](../network.md#join-handshake-and-mod-sync),
[manifest.md](manifest.md#clients)). A player whose player graph differs from the server's (another version of your mod, other
mods with player states) is refused at join too, also with `modSync` set to `warn`.

### From C#

`Kiln.PlayerAnim`, the static class `PlayerAnimStates` (both sides, any thread):
- `Find(mod, name)`: your state as a `PlayerAnimStateInfo`, with its GUID, id, kind, machine, `Status` (`Live`, `Refused`,
  `Pending`, `Unavailable`) and `Reason`;
- `FindGameStates(name)`: the game's states of that name, with their machine, kind and the ids that enter them;
- `GuidOf(mod, name)`: a state's GUID, by the rule alone;
- `Graph` and `GraphHash`: which graph this side serves;
- `Define(PlayerAnimState)`: a state from code. It can never change the running game, and in this release it is not
  built: the result is `Refused`, "definitions from code are not available in this build; write the state in
  assets/player-states.json". Put your states in the file.

### Limits

- **No hot reload.** A change (to the file, to the set of enabled mods) applies from the next start of the game
  or server: a player graph served while a player's graph runs ends the server, so the graph, and every clip a served state
  plays, stays as it was at boot.
- **34 ways in per machine.** A machine holds 34 global transitions (one per state that can be entered from any state); a
  full-body machine that would go past 34 takes all of its mod states into nested machines of 34 each (a machine inside one
  state of the machine), but an overlay machine
  does not nest: it has room for 21 (`SM_Staff_Overlay`), 30 (`SM_Ground_Bow_Overlay`), 30 (`SM_Air_Bow_Overlay`), 28
  (`SM_Ranged_Attack_Additive`) and 29 (`SM_Wand_Overlay`) states across all mods, and the states past that, in order of mod
  id and then state name, are refused.
- **`SM_Main` has room for six.** Six states like its states load as they are; with a seventh, all of its mod states nest,
  and a nested state loses the neighbour's ways out to other states: a mod's dodge roll then no longer cancels into the game's
  dodge attack.
- **One clip for every weapon.** The game picks the clip of a jump attack or a guard per weapon family; your state plays its
  one clip for every weapon.
- **No way from one mod state to another** but the next sequence: your states copy the game's transitions, never each
  other's, so a mod's dodge roll cancels into the game's dodge attack and not into a mod's.
- **No twin for a second wand cast.** A second cast of your wand state started before the first has ended continues the
  clip instead of restarting it; after the first has ended it plays from the start.
- **Unarmed states don't chain back.** The unarmed attacks are entered only from each other, so a state like
  `Attack_Unarmed_01` goes on to `Attack_Unarmed_02` but not straight on to `Attack_Unarmed_01`.
- **Game updates.** A neighbour the game renames or removes refuses the states that name it; a format the merge cannot prove
  on a new game build turns all player states off, and the game's own graph stays.

### Status

**Proven on a dedicated server.** Lab server svn 1287369, a spawned `1_Player` (the game's player character) with no client
behind it, the right hand's slot (`HandR`) read from the server every 2 steps (both hands for the staff, wand and bow cases),
the merged graph served in place of the game's as an
override, sequences copied from the game's with the event's GUID changed. "Control" is the same run with the game's own
graph:
- A state like `Attack_Sword_Shield_02` playing the hammer's `Attack_Heavy_01` clip. Control: the state's id reaches the
  graph, the hand moves at most 0.22 m and stays 0.569 m on average from the game's own state with that clip. Merged: the
  hand moves 1.20 m and stays 0.116 m on average from it; the same game sequence on two players differs by 0.05 m. The game's
  own states still enter, a second player spawned later behaves the same, and the game's log is the same with and without
  the merged graph.
- Entered, each in its own graph and run: a state like `Block_FullBody` (with the game's own guard clip it gives the game's
  block pose within 0.019 m; the game against itself, 0.015 m), a state like `DodgeRoll_FullBody`, 30 states nested beside a
  hammer state (the first, a middle and the last), and `SM_Main` with 7 nested states.
- Staff, wand and bow states (the overlays set by edited copies of the game's casts), the hands' mean distance to the game's
  own state with the same clip:

  | A state like | Control | Merged | The game against itself |
  |---|---|---|---|
  | a staff overlay state | 0.53 m | 0.17 to 0.25 m | 0.22 m |
  | a wand overlay state | 0.67 m | 0.13 m | 0.09 m |
  | a bow overlay state | 0.30 to 0.36 m | 0.07 to 0.12 m | 0.14 m |
  | a staff full-body state | 0.37 m | 0.035 to 0.042 m | 0.10 m |

  The staff overlay case is the weakest: its two shoot clips are hard to tell apart in its 0.4 s window, and it rests on the
  hands moving 1.1 to 1.2 m merged against 0.28 m in the control. `SM_Staff_Overlay` filled to its 21 states: the first and
  the last enter. Each overlay state goes back to the rest state when its overlay ends, and the game's casts still play.
- A player graph served while a player's graph runs ends the server; served before any player exists, it is used.

**Proven offline.** `python3 -m unittest discover -s tools/animcook/tests` (no game) checks each rule of the merge and each
refusal on a small graph, and each test was checked to fail with its rule broken. `tools/animcook/tests/game_playergraph_check.py`
runs the merge on the game's own graph: a state beside each of the 94 neighbours, every refusal, two mods in both orders
giving the same bytes, nesting, a full overlay machine. Kiln's native merge gives the same bytes and the same report as
animcook's for every kind of neighbour (`native/services/playeranim/tests/run.sh`, 369 checks: 63 merge cases, 82 definition
cases, 160 generated graphs, and the game check's scenarios on the game's own graph). In C++ on the game's graph, merging a
state beside every neighbour takes 24 to 32 ms.

**Not proven:** anything a client draws, a second player seeing a mod's state, root motion of a mod's clip moving the body,
and a player who joined (every run used a `1_Player` with no client behind it).

## Skeleton output for mesh tools

A tool that binds a mesh reads either of these, not the binary.

**From Python**, `tools/animcook/anicook/skeleton.py`:

```python
sk = skeleton.from_gltf("src/golem.glb")       # or load_sidecar("cooked/golem.skeleton.json")
sk.names, sk.parents                           # joint order is the one clips and vertex weights must use
sk.rest                                        # (translation, rotation xyzw, scale) per joint, parent-relative, game space
sk.joint_of_node(gltf_node)                    # None when the node is no joint: map skins[i].joints through this
sk.hierarchy_hash, sk.name_hashes
```

**From anything else**, `<name>.skeleton.json`:

```json
{ "format": 1, "tool": "animcook", "guid": "...", "source": "src/golem.glb", "hierarchyHash": "0x...",
  "joints": [ { "index": 0, "name": "root", "parent": null, "nameHash": "0x...", "translation": [0,0,0], "rotation": [0,0,0,1], "scale": [1,1,1] } ] }
```

tools/modelcook binds meshes to a recipe's `skeleton` step without the sidecar: see [Skinned models](models.md#skinned-models). The sidecar exists only after the `skeleton` step ran, so any other
tool should run after it. The compiled form's
`skinToJointMatrices` are the inverse of the chained rest pose, columns x, y, z, so a mesh's inverse bind matrices have to agree
with it after the same mirror on X.

## Tools

```
python3 tools/animcook/animcook.py recipe assets/cook.json [--update-manifest] [--check]   # what `kiln cook` runs
python3 tools/animcook/animcook.py info FILE.glb                                           # its armatures and animations
python3 tools/animcook/animcook.py clip cooked/clip_idle.bin                               # a cooked clip, decoded
python3 tools/animcook/animcook.py player-states assets/player-states.json                # player states against the game's graph
python3 tools/animcook/animcook.py player-graph --mod DIR --out DIR                        # the merged player graph, outside git
python3 tools/kfc/kfc.py graph GUID                                                        # a game graph, readable
python3 -m unittest discover -s tools/animcook/tests                                       # needs no game
python3 tools/animcook/tests/game_retarget_check.py                                        # retargeting on the game's clips
python3 tools/animcook/tests/game_playergraph_check.py                                     # the player-state merge on the game's graph
python3 tools/animcook/tests/run_blender_check.py                                          # needs Blender
```

A cook needs no installed game, unless a recipe names one of its resources; the type layouts are bundled and compared with the
game's when there is one.

## Manual checks

These need your eyes in the game client. They deploy Kiln into your game folder and run on a local world, so the steps back up
your saves first.

**1. A cooked clip plays** (the Vukah's idle, its head turned 30 degrees):

```
python3 tools/animcook/samples/clip_override.py --check head
dotnet build mods/tests/clipcheck/ClipCheck.csproj -c Release -o /tmp/clipcheck
tools/client/client.sh backup-saves
tools/client/client.sh deploy --real --policy any --mod /tmp/clipcheck
```

Start the game and load a local world. Once in the world, with `<game>` your Enshrouded folder:

```
echo 'clipcheck spawn' > <game>/kiln/control/console/c.cmd
```

A Vukah appears 4 m from you, friendly and standing still (`<game>/kiln/control/console/c.result.json` says where).

- **Pass:** its head is tilted about 30 degrees through the whole idle, and moves smoothly, with no jump about once a second
  (those would be the clip's 63-frame segment joins).
- **Fail:** the head is untilted (the override isn't served: see `assets:` lines in `<game>/kiln/logs/kiln.log`), the body is
  scrambled, or it jumps at the joins.

**2. The mesh follows a bone** (a Fogger's arm bends away and back over its idle):

Quit the game, run `clip_override.py --check arm`, build and deploy again as above, and spawn again.

- **Pass:** the arm bends slowly away and back, and the mesh follows the bone with no spikes.

**Afterwards:** quit the game, then `tools/client/client.sh undeploy --real`.
