# Custom 3D models

A mod can:
- give a vanilla item a new mesh (an override of its `keen::RenderModel`);
- give a new item a model of its own;
- cook a [skinned model](#skinned-models) for a creature, on a skeleton of its own or one of the game's.

You model in Blender (or anything that writes glTF 2.0). The cook recipe `assets/cook.json` turns the glTF into the
game's format on the player's machine with `tools/modelcook`. kiln.assets serves the result to the game at boot.
The format is documented in `docs/re/models.md`.

Status:
- **Offline: proven.** Decode, cook and re-decode all work; the vanilla Bronze Sword re-cooks to nearly the same
  bytes.
- **In game: an overridden weapon model renders**. Still to check: the handedness marker read
  upright, icons, and a crafted new item.
- **Skinned models: proven offline, on a dedicated server, and drawn in a client as a held model with moving parts**
  (2026-11-11, Kiln Equipment's Sunspire); a creature's body has not been seen drawn yet. See [Skinned models](#skinned-models). Blend shapes are written (morph targets,
  [cosmetics.md](cosmetics.md)); cloth and chains aren't.

## The rule: ship sources and a recipe, never game data
A mod folder holds only what its author made:
- glTF, PNG, JSON;
- the code;
- `assets/cook.json`: how to make the game-derived files from the player's own installed game.

The cook runs locally:
- today `tools/modelcook/modelcook.py recipe <mod>/assets/cook.json`;
- later the launcher, at install time.

It reads the game read-only and writes `assets/cooked/`. That folder is never committed or shipped
(`.gitignore`: `assets/cooked/`). This applies to anything taken from the game:
- base fields of a vanilla model;
- material and template references;
- a modified copy of a vanilla template.

Each such thing is named in the recipe by GUID and type. `assets/assets.json` (committed) names the cooked files by
their paths under `cooked/`.

```
mods/MyWeapons/
  kiln.mod.json                 side "both", clients "mod" (models are drawn by every player's client)
  MyWeapons.dll                 any managed code (a tiny one is fine)
  content/swords.json           item definitions (kiln.content), referencing the GUIDs below
  assets/
    assets.json                 kiln.assets entries, committed (files under cooked/)
    cook.json                   the recipe, committed
    src/frost_sword.gltf        your model, committed
    cooked/                     made on the player's machine: *.bin, *.blob, cook-report.json
```

### cook.json (format 1)
The file is plain JSON: the cook tools read it with Python's `json` module, so no comments and no trailing commas
(`kiln lint` reports KL002 and KL003).
```json
{
  "format": 1,
  "tool": "modelcook",
  "out": "cooked",
  "game": { "side": "client", "madeOn": "1285601" },
  "inputs": {
    "bronze_model":  { "guid": "9c38c7f0-fd0a-41a6-b91e-9a16287b8f59", "type": "keen::RenderModel" },
    "bronze_visual": { "guid": "fc562683-2a8e-472b-8c37-db2dfb36d449", "type": "keen::ecs::TemplateResource" }
  },
  "steps": [
    { "op": "model", "gltf": "src/frost_sword.gltf", "lods": [], "pixelSizes": [],
      "base": "bronze_model",
      "guid": "@bronze_model",
      "mode": "override",
      "name": "frost_sword", "stem": "frost_sword",
      "material": "<GUID or @input>",
      "materialMap": { "blade": "<GUID>" } },
    { "op": "visual", "base": "bronze_visual", "model": "<the new model GUID>", "guid": "<a new fixed GUID>",
      "name": "Visuals_Frost_Sword", "stem": "frost_sword_visual" }
  ]
}
```
File level:
- `tool`: the cooker that runs the steps.
- `out`: the output folder, relative to cook.json.
- `game`: informational.
- `inputs`: vanilla inputs, read from the installed game: GUID + type (+ part).

`op: "model"` turns a glTF (+ coarser LOD files) into `cooked/<stem>.bin` (RenderModel) and `cooked/<stem>.blob` (GPU
data):
- `base`: the template for every non-geometry field (materials unless set below).
- `guid`: `"@input"` is that input's GUID (an override), or give a new fixed GUID.
- `mode`: `"override"` or `"new"`.
- `material`: optional, one material for every primitive: a game material's GUID or `@input`, or the name of a [material block](materials.md) of this mod.
- `materialMap`: optional, glTF material name -> the same.
- `materials`: `"gltf"` makes a material block of every glTF material the map doesn't cover ([materials.md](materials.md)). No `base` or game material is needed then.

`op: "visual"` makes a visual template for a new item: a copy of `base` whose ModelResource points at `model`.

**One recipe for models and sounds.** A step may name its own cooker with `"tool"`, which defaults to the file's
`tool`, which defaults to `modelcook`. A mod with models and sounds keeps one `cook.json`:
```json
{ "format": 1, "tool": "modelcook", "out": "cooked", "guidNamespace": "<uuid, for the sound steps>",
  "inputs": { "bronze_model": { "guid": "9c38c7f0-…", "type": "keen::RenderModel" } },
  "steps": [
    { "op": "model", "gltf": "src/frost_sword.gltf", "base": "bronze_model", "guid": "@bronze_model", "mode": "override" },
    { "tool": "soundcook", "op": "sound", "name": "frost_hum", "src": "../audio/frost_hum.wav", "cue": { "loop": true } } ] }
```
- Each tool runs only its own steps; a step's index stays its position in `steps`.
- `modelcook.py recipe` hands the other steps to `tools/soundcook/soundcook.py`, and vice versa; `--no-dispatch`
  turns that off.
- An input used only by another tool's steps is left to that tool.
- Reports: the file's default tool writes `cooked/cook-report.json`, the other writes `cooked/cook-report.<tool>.json`.

Recipes without step tools cook exactly as before. ModelSample's re-cook is byte-identical: `.bin`, `.blob`,
`assets.json` and the report. The sound steps are documented in `docs/kiln/modding/sounds.md`.

Running it:
- `modelcook.py recipe assets/cook.json` cooks, validates every model (below) and writes `cooked/cook-report.json`.
- `--update-manifest` writes the matching entries into `assets.json`; without it, a mismatch is reported.
- `--check` says whether a cook is needed, without reading the game.

When to cook again, from `cook-report.json`:
- the recipe's sha256 changed;
- the game's `.kfc` version or exe sha256 changed;
- an input resource's sha256 or its type's layout hash changed;
- an output is missing or changed.

A game update that moves or removes an input stops the cook with a clear error: "input … is not in …", or a layout
mismatch.

### What a cook writes

Players do not run the cook by hand: the launcher cooks a mod before the game starts when it was installed, updated or
switched on, or when the game was updated ([launcher/app.md](../launcher/app.md), "Cooking mods"). Authors run
`kiln cook` (and `kiln build` does); a server's operator runs `kiln cook <mod> --game <server folder>`. A game loads
only the cooked files a cook of that game made: [cooking.md](cooking.md).

A cooked mod is the same mod on every machine: a dedicated server and each player's game cook it from their own game
files, and outside one folder nothing differs.

- **Everything made from the game goes into `assets/cooked/`**, with the cook's reports and index files
  (`cook-report*.json`, `clips.json`, `audio.json`, `<name>.model.json`). That folder is never shipped (`kiln pack` and a
  server's modpack leave it out) and does not count as the mod: the hash a server advertises for a mod and the one a
  player's game compares it with leave `assets/cooked` out (the mod's identity,
  [launcher/contracts.md](../launcher/contracts.md) 5.11). A server's cook makes no models and its report names the
  server's files; neither makes it another mod.
- **`"out"` is `assets/cooked` or a folder in it.** `"cooked"` in `assets/cook.json`, `"../assets/cooked/impact"` in
  `impact/cook.json`. Every cook tool refuses another place and says what to change:
  `"out": "built" is …, outside the mod's assets/cooked folder (…). A cook writes only there …`. The same check
  (`cookpaths.py`, one text in the four tools) is made for every file before the first one is written: a step's `stem` or
  `name` with `..` or a `/` in it is refused, the two folders have to be spelled `assets/cooked` in lower case (on
  Windows `Cooked` would be another folder to the mod's hash), and no folder or file on the way may be a link. The
  manifest a cook writes is the mod's `assets/assets.json` and no other file.
- **Outside it a cook writes only what follows from your own files**, the same bytes on every machine, and only when they
  differ from what is there:
  - the entries of the cooked files in `assets/assets.json` (`--update-manifest`, which `kiln cook` passes). They name
    files, GUIDs and types, not bytes, so they are the same on a server (which writes a skipped model's entries as a
    client's cook does) and after a game update;
  - the material blocks made from a glTF, `blocks/<stem>.materials.json` (`"materials": "gltf"` and the looks of
    [cosmetics.md](cosmetics.md); [materials.md](materials.md));
  - the steps of your sound blocks in `assets/cook.json` ([sounds.md](sounds.md)).

  Cook once before you ship and ship these files (`kiln build` and `kiln pack` cook first): then no cook on any
  machine changes your mod, and a machine that has not cooked yet has the same mod as one that has. A mod that ships
  without them still cooks to the same files everywhere, but an uncooked copy of it is another mod than a cooked one.
- A manifest a hand wrote is left as it is: a cook that finds every entry there does not write the file again.

What happens after a game update: the cooked bytes change (cook again), `assets/assets.json` does not, unless the update
changes which files a step makes. That is the one case a cook adds entries on a player's machine: a clip taken from one of
the game's (`"from": "@input"`) carries that clip's root motion and events when the game has them, each a file of its own.
A server and its players on the same game build get the same entries; across builds they could differ until both are
updated, as the cooked clips do.

## Modelling conventions (weapons)
- **Units and origin.** Metres. The origin is the hand grip: the game places the model's origin in the hand's
  attachment slot (`SlotAttachment HandR`).
- **Axes (Blender):** the blade points up (Blender +Z, glTF +Y). The crossguard lies along Blender ±Y (glTF ±Z). The
  flat of the blade faces Blender ±X.
- **Size.** The vanilla one-handed Bronze Sword spans 0.07 × 1.12 × 0.31 m: grip at 0, tip at +0.94, pommel at −0.17.
- **Handedness.** Export with Blender's defaults: glTF, +Y up, "Apply transforms". The game is left-handed, and
  modelcook mirrors x for you. The glTF exporter of tools/kfc does the inverse, so an exported vanilla model
  re-imports where it was.
- **Geometry limits.**
  - One glTF primitive per material.
  - Each primitive at most 65,535 vertices: indices are u16.
  - Triangles only. A skinned glTF needs `skeleton` in its step. Morph targets named after the game's blend shapes (`Female`, `Hat`, `Tight`, ...) are written on a model with a
    `skeleton`, and left out with a warning elsewhere; sparse ones (Blender's shape keys) are read.
  - A mesh node named `reference_*` is left out: a body to model around.
  - Node transforms are applied.
- **UVs.** Top-left origin, as glTF. With the base model's material, your UVs sample its textures. ModelSample maps
  its cubes onto the Bronze Sword's steel and leather.
- **LODs.** One LOD is fine (346 vanilla models have one). For more, add coarser files in `lods` and thresholds in
  `pixelSizes` (vanilla sword: 0, 0.0104, 0.0166, 0.0273, …).
- **Attributes.** Normals and tangents are computed when the glTF has none. Colours default to white.

## A new item with its own model
1. In cook.json: a `model` step with a new fixed GUID (`"mode": "new"`), and a `visual` step copying the base item's
   `equipment.visualEntity` template with `model` = that GUID.
2. In the item definition (kiln.content data pack or C#), point the references at them. `refs` accepts GUIDs that
   kiln.assets serves:
```json
{ "guid": "56f83e52-ac3b-4f15-ada8-67702ce8e9e6", "name": "Kiln_Sample_TowerSword",
  "base": "Weapon_T1_1H_SwordEpic_01", "displayName": "Kiln Tower Sword",
  "refs": { "equipment.visualEntity": "699ccb5f-97a5-4f31-b257-f13c61952589",
            "equipment.equipmentEntityModelSetup.model": "08525f9a-f6f9-46f4-8c3e-003e481d740d",
            "iconModel": "08525f9a-f6f9-46f4-8c3e-003e481d740d" } }
```
GUIDs are forever:
- pick them once (`python3 -c "import uuid; print(uuid.uuid4())"`) and never change them;
- the item's GUID gives its ItemId, which saves store.

## Validation (every cook)
Each model is decoded again with tools/kfc and checked:
- re-encodes byte for byte;
- the content hash matches the blob (kiln.assets' SHA-256 rule);
- ranges and indices are valid;
- the materials exist in the game, or are blocks of this mod;
- the winding is like vanilla's;
- the fields, vertex format and renderData category are the same as the base's;
- positions come back within the quantization step (≈ 1 µm on a sword), normals within 0.5 %, UVs within f16
  precision.

The report lists every check. To look at the result:
```sh
python3 tools/kfc/kfc.py model cooked --bin assets/cooked/frost_sword.bin --gltf /tmp/check.gltf   # then open in Blender
python3 tools/kfc/kfc.py model 9c38c7f0-fd0a-41a6-b91e-9a16287b8f59 --gltf bronze.gltf  # a vanilla one
python3 tools/kfc/kfc.py find-items 'Sword'     # item -> model / visual template GUIDs
```

## Skinned models

A glTF with a skin cooks to a skinned model when its step names a `skeleton`, either a `skeleton` step of the same recipe or one of the game's skeletons:

```json
{
  "format": 1,
  "tool": "modelcook",
  "inputs": { "fur": { "guid": "<a keen::RenderMaterialResource>", "type": "keen::RenderMaterialResource" } },
  "steps": [
    { "tool": "animcook", "op": "skeleton", "name": "golem", "gltf": "src/golem.glb" },
    { "op": "model", "gltf": "src/golem.glb", "lods": ["src/golem_lod1.glb"], "pixelSizes": [0, 0.05],
      "skeleton": "golem", "materialMap": { "fur": "@fur" }, "name": "golem", "stem": "golem" }
  ]
}
```

The animation steps are in [animations.md](animations.md). The order of the steps doesn't matter: a model step reads the skeleton step's glTF itself, so it binds to the same joints and the same
GUID whichever tool runs first.

| Key | Meaning |
|---|---|
| `skeleton` | The name of a `skeleton` step, `"@input"` of type `keen::ModelHierarchyResource`, or `"@player"`, the player's |
| `attach` | A mesh with no skin, rigid on one joint of the skeleton: a slot's name (`Head`, `Chest`, `HandPalmR`, ...) or `j<index>`. The file's origin is the joint |
| `material`, `materialMap`, `base` | What to draw with. One is needed. `base` (a game model) gives the default materials, and is optional on a skinned model |
| `guid`, `mode` | Without `base` or with `"mode": "new"`, the GUID follows from `<mod>:model:<name>`. For an override: `"guid": "@input"`, `"mode": "override"` |
| `globalScale` | Rarely needed, see [below](#globalscale) |

### Authoring

- **Bind in the rest pose.** Export with the armature at rest. glTF allows a mesh bound in another pose; the game can't store that, so the cook stops and says which joints disagree.
- **Vertices.** The file's skin decides where they go, so an armature object that is moved, turned or scaled is fine (the skeleton step bakes it into the root). A skinned mesh node's own
  transform is ignored, as in glTF.
- **Joints are matched by name**, so LOD files with their own node order bind the same way. On a game skeleton the names are `j<index>`, since the game's names are stripped. Start from
  an export of one of its models, which keeps the armature and bind pose: `python3 tools/kfc/kfc.py model <guid> --gltf body.gltf --skin`.
- **A mesh with no skin under a bone** follows that bone (a club parented to the hand). A mesh with no skin and no joint above it stops the cook, which also catches the startup cube.

The sample `tools/modelcook/samples/rigged_figure.py` writes such a creature with no outside data; `samples/mannequin.py` writes a mesh for one of the game's skeletons.

### Limits

| Limit | Value | Where it comes from | When it's passed |
|---|---|---|---|
| Joints in a model | 256 with weight | the stream's u8 index; the client's own path has no fixed cap | error |
| Joints in a skeleton | 255 | u8 bone indices in masks and IK chains. The largest in the game has 172, and more is untested in a client | error (animcook) |
| Weights per vertex | 4, summing to 255 | the stream | keeps the heaviest 4. Over 10 % of a vertex's weight lost: error. Over 1 %: warning |
| Unweighted vertex | none allowed | it would collapse to the origin | error |
| Vertices per primitive | 65,535 | u16 indices | error |
| LODs | 8 | the most in any of the game's 10,470 models; the limit in the engine wasn't found | error |
| `pixelSizes` | one per LOD, 0 first, rising | the game's data | error |
| Materials | 12 | the most in any game model; the loader keeps a growing array | error |
| Material's effect | `Pbr` | every skinned model of the game | error |

A coarser LOD may use fewer materials (101 of the game's models do) and any joints of the skeleton. Primitives with identical vertices share a vertex group, as the game's LODs do.

Not written: cloth, chains, sleeves, card clusters, and a skinned tree. None of them is needed for a creature (no creature model uses blend shapes, and cloth and chains are
optional on the ones that have them).

### globalScale

`globalScale` doesn't resize a model. The game's models carry a size their skeleton was cooked at (1.3 on the Vukah) and it's already in their vertices and joints. At run time the client
multiplies only the cloth colliders by it, so without cloth the value does nothing. The cook stores what the `base` or the game's models on that skeleton store, and 1 on a skeleton of your own.
To change a creature's size, scale the glTF, or its `ScaleComponent` per entity.

### Status

- **Proven offline.** All 1,342 of the game's skinned models go through glTF and this cooker: indices identical, positions within about one quantum, the same joints and weights, and posed by a
  clip of their own they stay within 0.021 mm of the original. A creature authored and exported by Blender, cooked and posed, stays within 3.05 mm of Blender's own armature modifier
  (weights are stored in 255ths); its rest pose within 0.01 mm.
- **Proven on a dedicated server** (`mods/tests/skincheck`, 9 checks): the sample figure's own skeleton, clips and graph run in the server's animation engine, and model vertices skinned by the
  engine's joints land within 0.26 mm of the cook's numbers. The states a clone's AI asks for answer. A kill with no player around sets no Dead state, on the game's own Vukah either, so that part
  is left for the client.
- **Proven in a client:** a mod's own skinned held model with its own skeleton, clip and graph, drawn in a real client
  and seen by the owner on 2026-11-11: Kiln Equipment's Sunspire, a skinned model on a 2-joint skeleton with a looping
  clip and a one-state graph, in a copy of `Visuals_Staff_01_Magic_Basic01`.
- **Not proven:** a creature's body drawn by a client (on the game's skeleton or its own), its far LOD and its shadow.
  See [Manual checks](#manual-checks).

### On a dedicated server

A held model with moving parts is a template (a copy of one of the game's staffs) with a skinned model, a skeleton, a
clip and a graph. What a dedicated server does with it, from lab runs on server svn 1287369 with a mod whose files are
all its author's (a staff of 2 joints, one looping clip, a one-state graph), held by a `1_Player` with no client behind it:

| What | Does the server need it for a held model? | What happens without it |
|---|---|---|
| The template, under its GUID | **Yes.** The server spawns the held entity from it (a child of the player). Clients create that entity by the same GUID (read from `docs/re/content.md` 8.3, not run) | If the server has a different template (a mod that leaves the moving one out where nothing is cooked), every client shows the server's choice. A client that lacks the server's template does not create the held weapon at all ([network.md](../network.md), "A Kiln client that lacks a template"; read from the docs, not run) |
| The graph, skeleton and clip | **No.** The held entity has the server's graph components, but the server never starts its graph: its handle stays 0 for 15 s, for the game's own staff the same | Nothing for the held entity. kiln.assets warns once per missing file, and kiln.content refuses the template's graph field (`... is not a loaded keen::ObjectReference<keen::anim_graph::tool_graph::AnimationGraph2_0>`), so the copy keeps the base staff's graph |
| The skinned and rigid `RenderModel`s and blobs | **No.** A server has no `RenderModel`; kiln.content takes a model GUID there without checking it | kiln.assets warns per file, nothing else |

A template spawned by itself (not held) is different: then the server does run its graph. With the cooked files it runs
the mod's own (the head joint turns about 45 degrees every 10 steps, at 1.4 m as authored). Without them a copy made by
kiln.content runs the base staff's graph instead, and a template served raw that names a graph nobody serves gets an
instance that never becomes ready: the game logs `Trying to reset animation graph instance[graph ID = …] … but not
ready!`, and nothing crashes.

So a server needs no cooked files for a held model with moving parts, but it must define the same template GUIDs as the
clients. Define the moving template on every machine, cooked or not: where the cooked graph is missing, give it the
rigid model and leave the graph field out. The server then spawns the moving template, and each client draws it with
what it cooked. Not seen yet: a joined player's held entity (the runs used a `1_Player` with no client behind it), and
what a client shows when its cook differs from the server's.

### Validation

Besides the checks above, a cook reads the stream back: every vertex covered, weights sum to 255 in descending order, indices inside the map, offsets cumulative, the skeleton's GUID and joint
count. The report's `skin` section gives the joint counts and how far vertices are from their heaviest joint.

| Check | What it does | Needs |
|---|---|---|
| `python3 -m unittest discover -s tools/modelcook/tests` | Reading, the limits, cooked data posed as the client poses it against the glTF per its specification | nothing |
| `python3 tools/modelcook/tests/run_blender_check.py` | A creature authored and exported by Blender, cooked, posed, against Blender's own armature modifier | Blender |
| `python3 tools/modelcook/tests/game_skin_check.py stream` | Every skinned game model's stream through the writer, against the game's bytes | the game |
| `python3 tools/modelcook/tests/game_skin_check.py cook` | Every skinned game model exported to glTF, cooked, compared, posed by one of its clips | the game, some minutes |
| `python3 tools/modelcook/tests/skin_render.py` | Draws cooked or game models in a pose, for your eyes | Pillow |
| `mods/tests/skincheck` on a dedicated server | The server's animation engine runs the skeleton, clip and graph; vertices skinned by its joints against the cook's numbers. Prints `skincheck: PASS` lines by itself. Servers have no models, so kiln.assets warns that the Vukah's override is skipped | a server |

### Building on it

What a tool that generates creature bodies needs:

**Input.** A model step as above. `name` is what the GUID follows from, `stem` what the files are called.

**Output**, in the recipe's `out` folder:

| File | What |
|---|---|
| `<stem>.bin`, `<stem>.blob` | The RenderModel and its GPU data, listed in `assets.json` with `--update-manifest` |
| `<stem>.model.json` | `{ guid, name, skeleton, joints, lods, materials, boundingBox, globalScale, morphs, attach }`: what a template or a block needs, without the binary |
| `cook-report.json` | `steps[i]`: `guid`, `mode`, `checks`, `warnings`, `skin` |

**Using it in a template:**
- `ModelResource.model` is the model's `guid`.
- `AnimationGraphResourceComponent2.animationGraph` is the graph's `guid` (`<name>.graph.json`).
- **The graph's skeleton has to be the model's.** Each frame the client poses a model only if its `hierarchy` equals the pose's skeleton GUID, by value; otherwise the mesh gets no joints. A
  `<stem>.model.json` and a `<name>.graph.json` must name the same `skeleton`.
- A clone of a game creature also needs a graph that answers the state ids its AI and arsenal ask for. `mods/tests/skincheck/assets/cook.json` lists the Vukah's, and
  [animations.md](animations.md) says how a state takes an `id`. `mods/tests/skincheck/build-assets.py` makes the template copy.
- Gear on slots needs `slots` in the graph step: `SlotAttachment` and `SpawnImpact` colliders read them.

**From Python:** `cook/steps.py` `model(k, t, gltf_files, base, guid=..., target=..., global_scale=...)` is what a step runs, with `target` from `cook/skin.py` `Target.from_skeleton(sk, guid)` or
`steps.game_target(k, t, guid)`. `cook/skin.py` raises `SkinError` for what would draw wrong, with a message meant for the author, and returns warnings for the rest.

### Manual checks

These need your eyes in the game client; neither creature has been seen drawn yet (a held model with moving parts has, see
[Status](#status)). They deploy Kiln into your game folder and run on a local world, so the steps back up your saves first. They are
checks H3 and H4 of docs/re/npc-internals.md section 8.

```
python3 mods/tests/skincheck/build-assets.py
dotnet build mods/tests/skincheck/SkinCheck.csproj -c Release -o /tmp/skincheck
tools/client/client.sh backup-saves
tools/client/client.sh deploy --real --policy any --mod /tmp/skincheck
```

Start the game and load a local world, on the LOCAL tab. Commands go to a file, with `<game>` your Enshrouded folder; the answer is in `<game>/kiln/control/console/c.result.json`:

```
echo 'skincheck vukah still' > <game>/kiln/control/console/c.cmd
```

`kiln.log` is `<game>/kiln/logs/kiln.log`. At boot it should say `assets:` with no errors for the mod. In every check, look there for `[model_loader]`, `failed to initialize Animation Graph
Instance` and `Could not find description`: none should appear.

**1. A cooked mesh on the game's skeleton** (H3). `skincheck vukah still` puts a Vukah 5 m from you, friendly and standing still. It's a stick figure of tan tubes, the size of a Vukah.

- **Pass:** its limbs and head move through the Vukah's idle, smoothly, the tubes staying joined at the joints. Then `skincheck clear` and `skincheck vukah`: a hostile one walks to you and
  attacks, and hits land about where the arm swings. `skincheck kill` shows it die.
- **Fail:** vertices shoot off to the world's origin or far away (spikes: the joint map or weights are wrong); limbs swap sides or the figure is mirrored (the bind matrices); the mesh stands
  still while the shadow or the hit boxes move (no joints arrive: compare the model's `hierarchy`); it disappears at some angles when an arm swings out (culling: see below).

**2. A creature of our own files** (H4). `skincheck clear`, then `skincheck figure still`. A figure of tubes about 2 m tall, with a club in its **right** hand. Its own skeleton, clips, graph and
model; only the behaviour is a copy of the Vukah's.

- **Pass, standing:** it breathes slowly: chest and arms move about 4 degrees over 2 seconds.
- **Pass, as an enemy:** `skincheck clear`, then `skincheck figure`. It sees you, comes at you with its legs swinging (the walk or the run), and swings the club overhead and down when close. Hitting it
  flinches its chest. `skincheck kill`, or your own weapon: it falls on its back and stays, then disappears as any enemy does.
- **Fail:** T-pose or stuck in one pose (the graph isn't running, see the log lines above), a state it never leaves (a state id the AI asks for is missing from the recipe: note what it was
  doing), the club in its left hand (mirrored).

**If the first check fails and you want the second:** quit, `build-assets.py --no-override`, build and deploy again. The Vukah then keeps its own model.

**What to note even when it passes:**
- a culling pop: the model's box is the exact bind-pose bounds, as in the game's own models, and how the renderer culls a mesh animated outside it wasn't read;
- whether the far LOD (back away about 50 m) is the coarser body, with no jump in pose;
- whether the shadow follows the pose.

**Afterwards:** quit the game, then `tools/client/client.sh undeploy --real`. Don't use `level`, `xp` or `skillpoints` for these checks: on a real client they can fire Steam achievements.

## Sample
`mods/samples/ModelSample`:
- the Bronze Sword becomes a cube-blade sword (override);
- `Kiln_Sample_TowerSword` (crafted by hand from one twig) has its own tower model and visual template.

Building: `build-assets.sh` runs the recipe. The managed part only logs PASS/FAIL for the new item's references.

The cube sword carries a chirality marker, a right-handed spiral of small cubes. With the blade upright, the strands
nearest the camera rise to the right ("/") when the handedness conversion is right.
