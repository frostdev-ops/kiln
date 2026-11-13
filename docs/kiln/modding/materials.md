# Materials

A material is a surface for a model: its colour, how metal and rough it is, what glows. You write one from PNGs and numbers, in `blocks/*.json` or C#. There is no game material to start from, no GUID to type and no compiler to install. It draws with the game's own shaders.

**Status:** the bytes pass every check below, and the game's own G-buffer shader reads them correctly on real GPUs, offscreen. It has not been seen in the game yet.

```jsonc
{ "format": 1,
  "materials": {
    "frost_steel": {
      "kind": "metal",
      "albedo": "textures/steel.png",
      "normal": "textures/steel_n.png",
      "metallicRoughness": "textures/steel_mr.png",
      "tint": "#dff2ff",
      "emissive": { "map": "textures/runes.png", "color": "#7fd4ff", "strength": 8 }
    } } }
```
```csharp
var steel = Blocks.Define("frost_steel", new MaterialBlock
{
    Kind = MaterialKind.Metal,
    Albedo = "textures/steel.png",
    Normal = "textures/steel_n.png",
    MetallicRoughness = "textures/steel_mr.png",
    Tint = "#dff2ff",
    Emissive = new EmissiveBlock { Map = "textures/runes.png", Color = "#7fd4ff", Strength = 8 },
});
```

**Use the data file for anything a model names.** The game lists the textures of every material once at start, before mod code runs. So a block in `blocks/*.json` is there at once, and one from C# is kept and is there from the next start: `handle.Status` says which, and a toast tells you. A mod built with Kiln.Sdk has its C# materials at the first start too: the build bakes them into `blocks/generated/<mod>.json` ([baked.md](baked.md)). Until then a model that names it doesn't draw.

A mod with a material needs `"clients": "mod"`: every player has to have the textures. `kiln lint` says so (KL211).

## Kinds
A kind says what the surface is, and where the other fields start from. A field you write always wins.

| `kind` | What | Starts with | The game's pipelines |
|---|---|---|---|
| `opaque` (default) | Solid | roughness 0.7, metallic 0 | `Model_{Visibility,GBuffer,Shadow}` |
| `metal` | Solid metal | metallic 1, roughness 0.35 | the same |
| `cutout` | Holes where the albedo's alpha is under 0.5: chain mail, lace | `alphaTest` | `_Discard` for visibility and shadows |
| `foliage` | Leaves: a cutout seen from both sides, light comes through | `alphaTest`, `doubleSided`, `translucent` 1, roughness 0.8 | `_Discard_DoubleSided` |
| `cloth` | Thin and rough, seen from both sides | `doubleSided`, roughness 0.85 | `_DoubleSided` |
| `emissive` | Glows: with its own `emissive`, or else with its surface | emissive strength 4 | as opaque |
| `dithered` | Soft edges from a dither pattern: hair, fur, thin things | hashed alpha test | `_HashedAlpha` |

`transparent` isn't a kind yet: the game has no way to draw a transparent model from a material's data alone. It comes with surface shaders. Until then `dithered` is the nearest thing.

## Fields
| Field | Value | Default |
|---|---|---|
| `albedo` | PNG, sRGB. Its alpha is what a cutout cuts by | white |
| `tint` | `#rrggbb`. Multiplies the albedo, built into the texture; without one it is the colour | white |
| `normal` | PNG, tangent space, green up as in glTF and Blender; or `{"map", "strength", "yDown"}`, `yDown` for DirectX maps | flat |
| `metallicRoughness` | PNG in glTF's layout: G roughness, B metallic | |
| `occlusion` | PNG, in R | none |
| `params` | PNG in the game's layout: R metallic, G roughness, B occlusion. Instead of the two above | |
| `roughness`, `metallic` | 0 to 1. The value without a map, a factor with one | by kind |
| `reflectance` | 0 to 1, non-metal specular; or `"params"`, read from the alpha of `params` | 0.503 |
| `emissive` | PNG, `#rrggbb`, or `{"map", "color", "strength", "exposure"}`. `exposure`, 0 to 1, is how much it follows the camera | none; 0.75 |
| `alphaTest` | `true` or the alpha to cut under. The game's shaders cut at 0.5, so another value rescales the albedo's alpha | by kind |
| `doubleSided` | Draw both faces, turning the normal on the back | by kind |
| `shadows`, `contactShadows` | Default true | true |
| `frost`, `snow`, `wetness` | Whether the weather shows on it | true |
| `vertexColor` | Multiply the albedo by the model's vertex colour | false |
| `translucent` | 0 to 1: light through from behind | by kind |
| `dissolve` | `true` or `{"color", "width", "noiseTiling", "noiseBlend"}`: the game can fade it in and out with an edge glow | off |
| `uvScale` | Repeats of every map: a number or `[u, v]`. Textures always wrap | 1 |
| `shader` | Reserved for surface shaders. Naming one is an error for now | |
| `was` | The old name after a rename: the id stays | |

PNGs are relative to your mod's folder, a power of two on both sides, 4 to 4096. What's missing is a small constant texture Kiln makes, shared by every material that needs the same one. Textures are compressed the first time and cached in `kiln/data/vfx/cache`: BC7 for colour, BC5 for normals, BC1 for the rest.

A mistake names the block and the field: `frost-fx: blocks/m.json: materials.steel.roughness: 2 is more than 1`. A wrong block registers nothing, and the file's other blocks still load. Schema: `schema/kiln-vfx.schema.json`.

## On a model
In a model step of `assets/cook.json`, `material` and `materialMap` take a block's name as well as a game material ([models.md](models.md)):
```json
{ "op": "model", "gltf": "src/frost_sword.gltf", "guid": "<a fixed GUID>", "mode": "new", "name": "frost_sword", "stem": "frost_sword",
  "materialMap": { "blade": "frost_steel", "grip": "frost_grip" } }
```
Rigid and skinned models work the same way. The model needs no `base`. The step reads the block for its flags, so changing the block's `kind` means cooking again; `modelcook.py recipe --check` notices.

### From a glTF's own materials
`"materials": "gltf"` makes a block of every glTF material the step doesn't otherwise map:
```json
{ "op": "model", "gltf": "src/ember_sword.gltf", "guid": "<a fixed GUID>", "mode": "new", "stem": "ember_sword", "materials": "gltf" }
```
It writes `blocks/<stem>.materials.json`, with blocks named `<stem>.<glTF material>`, and any PNGs embedded in the glTF to `<out>/materials/<stem>/`. A PNG beside the glTF stays where it is, and has to be inside the mod. Copy a block into your own file and name it in `materialMap` when you want to change it.

That blocks file is the one thing a model's cook writes outside `assets/cooked`. It is made from your glTF alone (no game data), so it is the same bytes on a server and in every player's game, and a cook only writes it when it differs. Ship it with the mod (cook before you pack): then a cook changes nothing of your mod on any machine ([models.md](models.md), "What a cook writes").

| glTF | Block |
|---|---|
| `baseColorTexture`, `baseColorFactor` | `albedo`, `tint` |
| `metallicRoughnessTexture`, `metallicFactor`, `roughnessFactor` | `metallicRoughness`, `metallic`, `roughness` |
| `normalTexture`, its `scale` | `normal`, `strength` |
| `occlusionTexture` | `occlusion`. Its `strength` isn't kept: a warning |
| `emissiveTexture`, `emissiveFactor`, `KHR_materials_emissive_strength` | `emissive` |
| `alphaMode` `MASK`, `alphaCutoff` | `kind: cutout`, `alphaTest` |
| `alphaMode` `BLEND` | `kind: dithered`, with a warning |
| `doubleSided` | `doubleSided` |
| `KHR_texture_transform` scale | `uvScale`. An offset or rotation is dropped, with a warning |
| `COLOR_0` that isn't all white | `vertexColor` |

The cook refuses by name what can't work: an image that isn't a PNG, a size that isn't a power of two, a second UV set. A sampler that doesn't repeat, and `KHR_materials_unlit`, warn.

## On a particle
A sprite draws with the game's transparent shaders, not a surface's, so a particle takes what fits: `"material": "frost_steel"` gives it the albedo as its texture, the tint in its colour, and `lit`. What the particle says itself wins. It names a material of its own mod.

## In place of one of the game's materials
`"replaces": "<GUID>"` draws the block wherever the game draws that material: every item, prop or creature model that uses it, with no model
cooked. The block gets the game material's id, so it takes no new material slot (its textures still count).
```jsonc
"materials": { "workbench_frost": { "replaces": "39745562-5d58-4749-bc64-582e5630324a", "kind": "opaque",
                                    "albedo": "textures/steel.png", "tint": "#bfe0ff", "normal": "textures/steel_n.png" } }
```
- Find a GUID with `kfc.py decode <model GUID> keen::RenderModel` (its `materials[].material`) or `kfc.py list --type keen::RenderMaterialResource`;
  the material's `debugName` says what it is (`npc_props_workbench00` above).
- **Keep the original's kind.** Models carry their materials' cutout, double-sided and shadow flags, so a replacement that changes them is drawn
  by the wrong shader variant: never fatal, but holes may fill or the back faces vanish.
- It is a client-side look: a mod that only replaces materials may be `"side": "client"`.
- `"was"` has no meaning on a replacement (its id is the game's), and one mod can replace a material once.

### It belongs to your mod
The replacement is built at start, like every material, and kiln.assets serves it as an override owned by **your mod** (an owner named
`vfx-reskin:<mod>`), never by kiln.vfx. When Kiln.Blocks compiles the block as your mod loads, kiln.vfx ties the override to your mod's own
lifetime:

| What happens | What Kiln does | In kiln.log |
|---|---|---|
| The game starts | Serves the override | `vfx: <mod>: material <name> draws in place of the game material <GUID>` |
| Your mod unloads (`kiln mods unload`, a failed reload, a dependency unloading) | Takes the override off within a second. If another mod replaces the same material, the next one in the load order is served | `vfx: <mod>: material <name> no longer replaces <GUID> (the mod unloaded): the game draws its own material again from the next start` |
| Your mod loads again | Serves it again (the first in the load order wins back its place) | `vfx: <mod>: material <name> replaces <GUID> again (the mod loaded again)` |
| Two mods replace one material | The first in the load order draws; the second stands by and draws if the first unloads | `vfx: <second>: material <name>: another mod earlier in the load order replaces <GUID> already: that one draws (this one stands by, ...)` |
| The GUID is not a material of this game | Refused against the original game material keys; mod-defined targets are also refused | `materials.<name>.replaces: not a material of this game build (mod-defined materials cannot be replaced)` |

**What only changes at the next start.** The game reads every material once, when it starts, and keeps it. Taking the override off means
kiln.assets no longer serves it, so the next start (with your mod disabled) draws the game's own material; models already on screen keep the
replaced look until then. Kiln does not change any of the game's material state at run time, so there is nothing else to restore. A replacement
that is new or changed since the start (from code, or an edited file) is kept for the next start, like any new material.

Once Kiln.Blocks' data pass is running, data-only mods and mods that fail before their blocks compile are released explicitly when their state becomes unloaded, disabled, failed or skipped. Pending mods keep their boot replacements while waiting to load. Reload clears the old data cache and recompiles once; a revoked code owner is not reused by that data pass.

The data pass currently starts only when some loaded mod has a `blocks/` folder (or code calls `Blocks.Define`). If no mod starts it, a boot replacement from a mod that fails before attaching its owner has no unload watcher and stays served until restart. This startup limitation remains; the normal attached-owner watcher is independent of the data pass.

## Limits
| What | Room | Over it |
|---|---|---|
| Materials | 4,096 alive at once, and the game has 3,403: 693 for all mods | The whole mod is refused, by name |
| Streamed textures | 16,384 slots, and the game uses 13,930: 2,453 for all mods. A texture shared by materials counts once | The whole mod is refused, by name |

In the log: `vfx: built 4 Kiln material(s): materials 3403+4 of 4096, streamed textures 13930+12 of 16383`, and `vfx: <mod>: over the game's limits, ...` for a refusal. Without the refusal the textures would stop the game at boot. A game update that changes a material's layout turns new materials off with a line naming the type, and models that name one don't draw until Kiln is updated.

**Walls, floors and terrain aren't materials like these.** They are slots in tables of their own, drawn by other shaders (`docs/re/building.md`). A `MaterialBlock` draws on models: items, props, creatures.

## Not yet
- Two blended layers, height maps, anisotropy, player-dye masks and overlay normals. The writer carries them, since the game's own materials use them; a block can't say them yet.
- Surface shaders. `shader` is the place for them. Transparent ones come next, then opaque.
- A particle that draws a model with a material needs a particle module of its own.

The sample is `mods/samples/MaterialSample`. Format research: `docs/re/vfx.md` section 5.
