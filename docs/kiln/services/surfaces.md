# kiln.surfaces: building surfaces

How a building material looks. Walls, floors and roofs aren't drawn from `RenderMaterialResource`s: every building material is a slot in three tables of
the client's, read by the wall shaders. kiln.surfaces writes a slot for every new material kiln.worldtables placed and for every game material a
`MaterialTweakBlock` retextures, with maps cooked from the mod's PNGs. The modder's guide is [modding/world-blocks.md](../modding/world-blocks.md),
"Surfaces". Evidence: [docs/re/building.md](../../re/building.md) 1.4.

**Status:** the bytes pass every check below, and a slot built from a game material's maps is the game's slot to the byte, the configuration a client was
seen to draw. A slot with Kiln's own cooked maps has not been seen in the game yet.

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_surfaces.h` (`kiln.surfaces` v1: state, status JSON) |
| Cooker, portable | `native/services/surfaces/cook/` (no Windows, no game memory, no Kiln API). `surfacecheck.cpp` reads a surface and is also linked into kiln.worldtables |
| In the game | `native/services/surfaces/surfaces.cpp` → `kiln-surfaces.dll`, a kiln.worldtables table writer |
| Command line | `tools/surfacecook/native` → `kiln-surfacecook` (Linux and Windows) |
| Managed | `managed/services/Kiln.Blocks/World/Surface*.cs`: `SurfaceBlock`, `SurfaceLayer` |
| Tests, sample | `tools/surfacecook/tests`, `mods/tests/surfaces-test`, `mods/samples/SurfaceSample` |

## The tables
| Resource | Per slot | Count |
|---|---|---|
| `BuildingMaterialBlendingResource` | 392 B: six layers (side, top, bottom, edge, edgeTop, noise) of eight maps, a feedback list and six numbers, then the slot's blend numbers | `layerCount`; the data has room for 128 |
| `GiVoxelBuildingMaterialResource` | 180 B: top, side and bottom faces, each the averages of its layer and its albedo and emissive maps | `materialCount`; room for 128 |
| `BuildingMaterialParametersResource` | 12 B: mapping, edge-only-side, edge decoration rules (models by index) | a list as long as the count |

A slot's maps are `RenderStreamedTexture`s in the table's own `textures`, one per patch point, each in the game's format for that map: BC7 sRGB albedo, BC5
normal, BC4 metallic, roughness, reflectance, height and AO, BC1 sRGB emissive. A full mip chain, largest level first, one range per level with more than
4 KiB left, the rest a mip tail of its own; both blobs under their real `ContentHash`. `content.category` 1013777168, `bundleId` the material's GUID's first
field, `sortKey` the layer's number × 256 + the map's (edgeTop shares the edge's number, as in the game). The GI faces name the same textures. A map nobody
gave is a 16 × 16 constant, as the game's are.

The shaders bound a slot by the count, and draw a plain fallback stone past it. So the count ends at the last live material: kept materials (absent mods)
come after it and draw the fallback.

## At boot
1. **Fingerprint.** The reflection records of every type written (13 of them, `surface_layouts()`) and the data blocks' type signatures. A difference turns
   surfaces off with a line naming the type; the game's tables stay as they are and new materials draw the fallback.
2. **The moment.** kiln.worldtables runs its table writers once it has given ids, on the game's thread and before the renderer reads the tables
   (it resolves them once, by fixed GUIDs, at creation).
3. **Read** the three tables through kiln.assets. A dedicated server has none: there kiln.surfaces does nothing.
4. **Cook** every live material (`material_at`) and every tweak with a surface (`tweak_at`). PNGs are read from the mod's folder; chains are cached in
   `kiln/data/surfaces/cache` by pixels and format, so a second start encodes nothing. A game material a layer names is found by `game_material_id`.
5. **Budget.** New textures count against the game's 16,384 streamed textures, by content, after what the game (13,930) and kiln.vfx's materials use.
   A surface over it is refused by name and its material falls back.
6. **Serve** the blobs, each read back, then the three tables as overrides. All or nothing: a refusal takes back what was served.
7. **Icons.** A new material whose block names no `icon` gets one: a block of its surface (side and top albedo, the sides shaded), 256 × 256, drawn on the CPU
   from a small mip and registered with kiln.assets for the block item's GUID. A refused icon costs only the picture.

A surface that fails (a missing PNG, a size that isn't a power of two, a game material that doesn't exist) is logged with its block and field. Its
new material then draws like the game's Rough Stone, so the count stays whole; a retexture leaves the game's look.

## kiln-surfacecook
```sh
tools/surfacecook/native/build.sh            # or --windows; KILN_OUT picks the build folder
kiln-surfacecook roundtrip --blend F --gi F --params F       # read and write back, whole and slot by slot; 0 = byte-identical
kiln-surfacecook layouts --blend F --gi F                    # every texture against the streaming layout the cooker writes
kiln-surfacecook cook --blend F --gi F --params F --mod DIR [--mod DIR ...] --out DIR [--names FILE] [--blobs DIR] [--cache DIR] [--room N]
kiln-surfacecook check JSON [--tweak 1]                      # a surface: what's wrong with it, and its normal form
kiln-surfacecook hash FILE [--from N]
```
The tables are the client's resources as `tools/kfc` exports them. `cook` gives ids with kiln.worldtables' own code and writes what the game would be
served: the three tables, `blobs/`, `icons/`, `report.json`.

## Tests
```sh
python3 -m unittest discover -s tools/surfacecook/tests
native/services/worldtables/tests/run.sh          # the merge, with the surface check linked in
KILN_OUT=<build dir> mods/tests/surfaces-test/run.py --lab mylab
```
Without the game: the parser, field by field. With the game installed (read only): its three tables read and write back byte for byte, whole and through
the slot model; all 3,878 of its textures have the layout the cooker writes, and a blob of each format and size hashes as its table says; a cooked mod
decodes with `tools/kfc` and re-encodes to the same bytes, only the slots written change, every texture follows the rules above, a surface taken whole
from a game material is that material's slot to the byte, and mistakes, the texture budget and the id limit refuse by name. The lab test runs on a
server, one start per scenario: material blocks with surfaces take ids and keep them across restarts, a block from C# lowers to what its JSON twin does,
a wrong surface is refused by name, the game's TNT digs the new materials and their loot is their recipe's main input (or their own block when asked),
and the 50th material is refused by name.

## After a game update
Run the tests with the game installed. A moved layout shows in the fingerprint (kiln.log) and in `roundtrip`; update `surface_layouts()` and the offsets in
`cook/`. Mods' JSON and C# need no change, and the cache is rebuilt as needed.

## Not proven
How a cooked surface looks: near and far, its mips, the normal map's direction on each face, the GI averages, emissive. That takes a client.
