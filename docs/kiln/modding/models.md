# Custom 3D models (rigid weapons first)

A mod can:
- give a vanilla item a new mesh (an override of its `keen::RenderModel`);
- give a new item a model of its own.

You model in Blender (or anything that writes glTF 2.0). The cook recipe `assets/cook.json` turns the glTF into the
game's format on the player's machine with `tools/modelcook`. kiln.assets serves the result to the game at boot.
The format is documented in `docs/re/models.md`.

Status:
- **Offline: proven.** Decode, cook and re-decode all work; the vanilla Bronze Sword re-cooks to nearly the same
  bytes.
- **In game: an overridden weapon model renders**. Still to check: the handedness marker read
  upright, icons, and a crafted new item.
- **Skinned armor and animations: not yet.** Research plan in docs/re/models.md section 7.

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
- `material`: optional, one RenderMaterialResource for every primitive.
- `materialMap`: optional, glTF material name -> material GUID.

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
  - Triangles only; no skins, morph targets or sparse accessors yet.
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
- the materials exist in the game;
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

## Sample
`mods/samples/ModelSample`:
- the Bronze Sword becomes a cube-blade sword (override);
- `Kiln_Sample_TowerSword` (crafted by hand from one twig) has its own tower model and visual template.

Building: `build-assets.sh` runs the recipe. The managed part only logs PASS/FAIL for the new item's references.

The cube sword carries a chirality marker, a right-handed spiral of small cubes. With the blade upright, the strands
nearest the camera rise to the right ("/") when the handedness conversion is right.
