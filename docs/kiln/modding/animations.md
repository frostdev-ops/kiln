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
  one-state graph, in a copy of `Visuals_Staff_01_Magic_Basic01`. A dedicated server runs such a graph too; what it needs of
  the cooked files is in [models.md](models.md#on-a-dedicated-server).
- **Not proven:** a creature's body drawn in the client, a body actually moved by root motion, and a state added to the
  player's graph. See [Manual checks](#manual-checks).

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
[cook.json](schemas/cook.md)). Per clip:

| Key | Meaning |
|---|---|
| `animation` | the glTF animation's name; defaults to the clip's key |
| `loop` | plays as a loop; a warning when its last frame differs from its first by more than 2 degrees or 2 cm |
| `rootMotion` | `true`, or `{ "joint", "axes", "rotation" }`: the motion is taken out of the joint and written as the clip's root motion |
| `events` | `{ "at": seconds, ... }` with `sound`, `vfx`, `foot` or `noise` |
| `tolerance` | `{ "rotation", "position", "scale" }`: how far the keys may stray |
| `guid`, `mode` | `"guid": "@input"` replaces one of the game's clips |

All of glTF's interpolations are read (LINEAR, STEP, CUBICSPLINE) and sampled at 60 frames a second. An animation that moves a
node outside the skeleton is an error; a joint the animation doesn't touch holds its rest pose.

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
| `clip_<name>.bin`, `.rootmotion.bin`, `.lve.bin`, `.events.bin`, `.notify.bin` | the clip and its siblings under its GUID |
| `clips.json` | each clip's guid, duration, loop |
| `graph_<name>.bin`, `<name>.graph.json` | the graph, and its states with ids and GUIDs, nodes, inputs, overlays, warnings |
| `mask_<graph>.<layer>.bin` | a layer's bone mask |
| `cook-report.json` | sources and hashes: what makes a later cook skip |

A sequence or template names a state by the GUID in `<name>.graph.json`. The game's own graphs and states are in the
`graphs` section of the [catalog](catalogs.md).

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
python3 tools/kfc/kfc.py graph GUID                                                        # a game graph, readable
python3 -m unittest discover -s tools/animcook/tests                                       # needs no game
python3 tools/animcook/tests/game_retarget_check.py                                        # retargeting on the game's clips
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
