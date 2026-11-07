# VFX blocks: the builder

Turns a definition, written in JSON or C#, into the game's `VfxResource` data. Nothing from the game goes into the output, and an author needs no
compiler. `kiln.vfx` runs the builder at boot, and `kiln-vfxcook` runs the same code offline. For playing effects from code, see
[vfx.md](vfx.md). Format research: `docs/re/vfx.md`.

**Status:** the data passes every check below and has run on real GPUs in a standalone Vulkan harness. It has not been seen in the game client yet.

| Layer | Where |
|---|---|
| Builder, portable | `native/services/vfx/cook/` (no Windows, no game memory, no Kiln API) |
| In the game | `native/services/vfx/boot.cpp`, part of `kiln-vfx.dll` |
| Command line | `tools/vfxcook/native` → `kiln-vfxcook` (Linux and Windows) |
| Modules | `native/services/vfx/shaders/build/*.spv`, embedded as committed; building Kiln needs no shader compiler |
| Schema | `schema/kiln-vfx.schema.json`, the `vfx` section of `schema/kiln-blocks.schema.json` |
| Checks, sample | `tools/kfc/kfc/vfx.py`, `tools/vfxcook/tests`, `tools/vfxcook/samples/frost` |

## The definition
A `vfx` section in any `blocks/*.json`. C# blocks serialise to the same shape.

```jsonc
{ "format": 1,
  "vfx": {
    "frost_burst": {
      "particles": [ {
        "burst": 24, "lifetime": [0.4, 0.9], "speed": [2, 5],
        "spread": { "cone": 35 }, "gravity": -4,
        "size": { "linear": [0.15, 0] },
        "color": ["#ffffff", "#7fd4ff"], "alpha": { "linear": [1, 0] },
        "texture": "vfx/shard.png", "blend": "additive"
      } ],
      "light": { "color": "#7fd4ff", "radius": 6, "brightness": 4, "lifetime": 0.6, "fadeOut": 0.5 }
    } } }
```

Block: `particles` (a list), `light`, `sound`, `cullDistance` (far, or `[near, far]`; default `[50, 75]`), `was` (the old name after a rename). It needs
particles, a light, or both. `gameSpriteModule: true` is a diagnostic: the particles draw with one of the game's own sprite modules, which tells a problem
in the data from one in Kiln's module. That module takes the first colour and size, the texture and the mesh, and bakes in the rest. A block using it is
refused if this game no longer has the module. Because it's a choice, the builder reports it as a note, not a warning: kiln.log has it once, at Info, from
the build at start (`vfx: <mod>: blocks/<file>.json: vfx.<name>.gameSpriteModule: a diagnostic: ...`).

| Particle field | Value | Default |
|---|---|---|
| `burst` | particles at the start and at each loop, 0 to 65535 | 0 |
| `rate` | per second; at most 64 spawn per tick | 0 |
| `duration` | seconds of emission. 0 with a rate: until stopped | |
| `delay`, `loop` | seconds; restart after the duration, which must be above 0 | 0, false |
| `maxCount` | alive at once, 1 to 4096 | capacity for count=16, capped at 4096 |
| `lifetime`, `speed` | seconds, m/s: a number or `[min, max]` | 1, 1 |
| `spread` | `"up"`, `"sphere"`, `"outward"` (away from the shape's centre), `{"cone": degrees}` | up |
| `spawnRadius` | metres: start inside a sphere. The same as `"shape": {"sphere": r}` | 0 |
| `shape` | `"point"`, `{"sphere": r, "inner": r2}`, `{"shell": r}`, `{"hemisphere": r, "inner": r2}`, `{"box": [x, y, z]}` (full sizes), `{"disc": r, "inner": r2}`, `{"ring": r}`, `{"line": length}` (along x), in the effect's space | point |
| `align`, `stretch` | `"camera"` or `"velocity"`: a camera-facing quad turns to its motion; metres of extra length per m/s, 0 to 100, with `velocity` only | camera, 0 |
| `shader` | a verified hook module is linked into Kiln's sprite; any refusal uses sprite fallback. Path: `"shaders/build/x.spv"` or `{"file", "constants": [up to 8]}` ([shaders.md](../modding/shaders.md)). Needs `"advanced": true` in the manifest | Kiln's sprite |
| `gravity`, `acceleration` | m/s² on world y; `[x, y, z]`, added together | 0 |
| `drag` | per second | 0 |
| `spin`, `roll` | degrees per second, degrees at spawn: a number or `[min, max]` | 0 |
| `size` | metres over life: a curve. Clamped at 64 | 0.25 |
| `alpha` | 0 to 1 over life: a curve | 1 |
| `color` | sRGB over life: `"#rrggbb"`, a list, or `{"keys": [[t, "#rrggbb"], ...]}` | white |
| `intensity` | multiplies the colour, which may exceed 1 | 1 |
| `texture` | a PNG in the mod, or `{"file": ..., "linear": true}` for data, not colour. Power of two, 4 to 2048 | white |
| `sampler` | `"wrap"`, `"mirror"`, `"clamp"`, `"clampToZero"` | wrap |
| `flipbook` | `{"columns", "rows", "fps"}`. Without `fps` the grid plays once over a particle's life | none |
| `blend`, `lit` | `"alpha"` or `"additive"`; takes ambient and sun light | alpha, false |
| `mesh`, `faceCamera` | `"quad"` or `"quad-xz"`; turn to the camera | quad, true for quad |
| `depthFade`, `emissive` | metres of soft fade against the scene; 0 to 1 | 0 |
| `material` | a [material](../modding/materials.md) of this mod: its albedo as the texture, its tint in the colour, and lit, as far as the particle says nothing itself | none |

A curve is a number, `{"linear": [start, end]}`, a list of evenly spaced values, or `{"keys": [[t, value], ...]}`. Curves are sampled to 8 values.

Light: `color`, `radius` (default 6, clamped at 64), `brightness`, `lifetime` (0: until stopped), `fadeIn`, `fadeOut`, `flicker` (0 to 1), `offset`.

`sound` is accepted and counted in the budget, but not played yet: a VFX needs a sound module first, and the log says so.

A mistake names the block and the field: `frost-fx: blocks/x.json: vfx.frost_burst.particles[0].maxCount: 4097 is outside 1..4096`. A wrong block
registers nothing, and the file's other blocks still load. Unknown fields are errors. A `flipbook` warns when the PNG's size can't divide into the
grid; a grid that divides but doesn't match the art can't be detected.

## Per-play parameters
Every `VfxResource` the builder writes describes the same five parameters, with the same defaults, so a play, a template, a sequence or a buff
can set them by name, and a server can lay the block out without a VFX of its own (`VfxParameterBlock` in Kiln.Blocks):

| Name | DsTypeId | Bytes | Default | Does |
|---|---|---|---|---|
| `color` | `VfxEffectParameterColor` `0x53039620`, RGBA8 sRGB | 0–3 | white | tints particles and light; alpha multiplies alpha |
| `size` | `VfxEffectParameterFloat` `0x90b5085d` | 4–7 | 1 | particle size |
| `speed` | float | 8–11 | 1 | start speed |
| `brightness` | float | 12–15 | 1 | particle colour and light |
| `count` | float | 16–19 | 1 | burst and rate |

Word 15 of the defaults is `KX_MAGIC` ("KILN"): the modules read the block only when it is there, so a start that passed no block can't make an
effect vanish. Factors are clamped to 0..16; a value that isn't finite reads as 1. Layout: `KX_*` in `native/services/vfx/shaders/kiln_vfx_params.h`.

## What a block becomes
Ids follow the rule of all blocks, `UUIDv5(6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77, "<mod>:vfx:<name>")`, the same as `BlockIds.Derive(mod, "vfx", name)` in
C#. With `was`, the old name decides the id.

| Resource | From | Notes |
|---|---|---|
| `keen::VfxResource` | a block | Two entries per particle block (emitter, sprite), one for a light. Parameters live in Kiln's own blocks, `native/services/vfx/shaders/kiln_vfx_params.h` |
| `keen::VfxMeshResource` | `quad`, `quad-xz` | Kiln's, shared by all mods. GUIDs from `kiln:vfx-mesh:<name>` |
| `keen::RenderTextureResource` | a PNG | BC7 with a full mip chain, cached in `kiln/data/vfx/cache`. GUID from `<mod>:vfx-texture:<path>` |
| `keen::RenderMaterialResource` | a block of the `materials` section | [materials.md](../modding/materials.md). GUID from `<mod>:material:<name>`. Its textures are inline, as the game's are: BC7, BC5, BC1, a blob for the chain and one for the mip tail |

Every blob, shader or not, is served under its real `ContentHash`, so the output is a pure function of the input.

## At boot
The game enumerates VFX resources once, so everything is served in `on_ready`, before its entry point.

1. **Fingerprint.** The game's reflection records for every structure the builder and modules rely on are read from the exe image, with no signatures.
   A change turns mod particles off with a log line naming the type, and a change in a material's layouts turns new materials off the same way, each without the other. Game VFX and materials aren't affected.
2. **Budget.** Counts come from the KFC directory (`enshrouded.kfc`) on whatever build this is, except what would need decompressing: mesh memory, sounds and streamed textures are measured per build.
3. **Mods**, in load order, from `blocks/*.json` and `kiln/data/vfx/pending/<id>/`. `kiln/config/vfx.json` can list `"skipMods"`.
4. **Serve** through `kiln.assets`: blobs first, each read back and re-hashed, then meshes and textures, then VFX.
5. **Late check.** At the first FxController update, once `vfx/vfx.gpc` is resident, the binding list of `VfxUpdateDescriptorSetLayout` is compared. A
   difference makes the guard treat Kiln's VFX as unknown.
6. **Own data.** Once the game's VFX table lists them, each Kiln VFX's GUID is looked up there and its resource read back against the bytes Kiln served:
   `vfx: N Kiln VFX resolve to their own data in the game's VFX table`. One that resolves to other data would play another effect under its name, so the
   guard refuses it and the log names it. If none match, the check is taken not to hold on that build and nothing is refused.

## Definitions from code
`Blocks.Define("name", new VfxBlock { ... })` serialises the record to this shape ([blocks.md](../modding/blocks.md#vfx)). `define_json` in the `kiln.vfx` table (`native/core/abi/services/kiln_vfx.h`) takes the blocks of one mod:

```json
{ "format": 1, "mod": "frost-fx", "dir": "<the mod's folder>", "vfx": { "frost_burst": { } }, "materials": { "frost_steel": { } } }
```

It validates and builds them, textures included, without serving them. Mod code runs after the game has enumerated VFX and listed the textures of every material, so a block the game doesn't
already have, unchanged, is kept in `kiln/data/vfx/pending/<id>/` and is there from the next start.

The report is JSON. The result is `KILN_OK`, `KILN_ERR_INVALID_ARGUMENT` when something is wrong, or `KILN_ERR_BUFFER_TOO_SMALL`, with the full length in
`out_len`:

```json
{ "ok": true,
  "blocks": [ { "mod": "frost-fx", "name": "frost_burst", "guid": "428f57a8-db84-5ef4-942b-32129da6d6ba",
                "entries": 3, "emitters": 1, "killTime": 10, "loops": false, "stopAfterSeconds": 0.918,
                "status": "registered" } ],
  "errors": [], "warnings": [], "notes": [], "refusedMods": [],
  "budget": { "vfx": { "game": 1575, "mods": 1, "capacity": 2048 },
              "meshes": { "game": 638, "mods": 1, "capacity": 1024 },
              "meshBytes": { "game": 17567564, "mods": 140, "capacity": 17825792, "margin": 16384 },
              "sounds": { "game": 6, "mods": 0, "capacity": 16 },
              "curves": { "game": 8, "mods": 0, "capacity": 16 } } }
```

- `notes`: what a block chose on purpose and is still worth knowing (`gameSpriteModule`). Kiln.Blocks logs only the `warnings`.
- `status`: `"registered"` means the game has this block with this definition; `"pending"` means it's there from the next start. A block with errors isn't listed.
- `stopAfterSeconds`: a conservative one-shot lifetime: delay, duration, longest particle life and spawn time at count=16. `Vfx.PlayAt` without a `lifetime` applies it on its own. It's absent when `loops` is true: such an effect keeps the rule for other VFX ([vfx.md](vfx.md)), so pass `Vfx.UntilStopped` to keep it going.
- `budget`: this document alone, against the game's numbers. The boot count over all mods is what decides; `build_status` returns it in the same shape, with `"fingerprint"`.
- The manifest rule (`clients`) is checked at boot, not here. A pending file stays until deleted, even if the mod stops defining that block.

## The limits and the checks before them
All of these abort the game when hit. Each has a check that runs first, and a failed check logs a line.

| In the game | Limit | Check |
|---|---|---|
| "too many vfx resources" | 2,048; the game uses 1,575, so 473 for all mods | Budget: the whole mod is refused, and the log says which limit |
| "too many vfx meshes" | 1,024; the game uses 638, so 386 | Budget |
| "out of vfx mesh memory" | 17,825,792 B; the game uses 17,567,564. About 240 KB is left after a 16 KB margin | Budget. Kiln's quad takes 140 B |
| "too many vfx sounds" | 16; the game uses 6, so 10 | Budget |
| "too many vfx curves" | 16; the game uses 8, so 8 | Budget. Blocks use none: curves live in the parameter data |
| "too many streamed textures" | 16,384 slots, one reserved; the game uses 13,930, so 2,453 | Budget: the whole mod is refused, by name. A texture two materials share counts once |
| A Pbr material with no slot: its model silently doesn't draw | 4,096 alive at once; the game has 3,403, so 693 are always safe | Budget |
| "corrupted streamed texture parameters" | a material from after boot, with a texture the renderer didn't list | A definition from code waits for the next start |
| "error loading streamed texture mip tail" | a tail blob not served | Blobs are served before what names them. If serving fails, nothing is registered |
| "SmallDynamicArray is out of elements" | more than 8 images in one feedback list | A block makes at most 4 |
| "invalid vfx pipeline code hash", "error streaming vfx pipeline" | a module's hash is empty, wrong or not served | C++ `ContentHash`, checked against the Python reference and read back after serving. If serving fails, no VFX is registered |
| "error creating vfx compute pipeline" | a module the driver refuses | SPIR-V ≤ 1.5, entry `cs_main`, `LocalSize 32 1 1`, the game's capabilities, at every boot |
| "error streaming vfx mesh data" | a mesh's blob is missing | Blobs are served and read back before any resource names them |
| "invalid vfx mesh reference" | a null mesh GUID | Every sprite names a built-in mesh. An unknown GUID isn't fatal, it draws nothing |
| "corrupted vfx resource reference" | an unknown VFX GUID | The guard in `kiln.vfx` ([vfx.md](vfx.md)) |

These aren't aborts, but the builder holds them too:

| Rule | Why |
|---|---|
| `maxCount` ≤ 4,096, in the schema and the builder | The game reads it on the CPU, where the modules can't enforce it |
| A burst alone gets a duration of 0.001 s | With 0, the emitter runs until the instance stops |
| Mesh blobs are multiples of 28 B | The game computes `vertexOffset` as allocation offset / 28, so another size shifts every mesh loaded after it, the game's included |
| At most 55 entries per VFX, parameter data under 64 KB | The game's largest, and its 16-bit ranges |
| `kiln/config/vfx.json` `{"enabled": false}` | Skips everything before the game can see it |

## kiln-vfxcook
```sh
tools/vfxcook/native/build.sh              # or --windows; KILN_OUT picks the build folder
kiln-vfxcook cook --mod DIR [--mod DIR ...] --out DIR [--kfc FILE] [--cache DIR] [--pending DIR]
kiln-vfxcook check --mod DIR ...           # the report only
kiln-vfxcook hash FILE ...                 # 128-bit hash and ContentHash
kiln-vfxcook modules                       # the starter modules and their hashes
kiln-vfxcook id MOD KIND NAME              # a block's GUID
kiln-vfxcook fingerprint --exe EXE [--gpc FILE] [--kfc FILE]
kiln-vfxcook budget [--kfc FILE] [--counts vfx=N,meshes=N,curves=N,sounds=N,meshBytes=N,materials=N,streamedTextures=N]
kiln-vfxcook material-roundtrip FILE       # {u32 size, bytes} records of RenderMaterialResources, rewritten by the builder's own code
kiln-vfxcook shader build --mod DIR [--glslang PATH] [--linked]   # a mod's shaders/*.hlsl -> hook modules shaders/build/*.spv: stamped, verified, test-linked
kiln-vfxcook shader check FILE.spv ...     # the verify-and-link kiln.vfx runs at boot (JSON: hooks, cost, loop cap, clamped indices)
kiln-vfxcook shader link FILE.spv --out LINKED.spv   # the linked module the game would compile (for the harnesses)
kiln-vfxcook shader kit --out DIR          # the hook kit's headers
```

`cook` writes what the game would be served: `assets.json`, `cooked/` and `report.json`, the report above plus which file is what. It exits 1 on any error
or refusal.

## Tests
```sh
python3 -m unittest discover -s tools/vfxcook/tests
python3 tools/vfxcook/tests/material_gpu.py [--device NAME]   # the game's G-buffer shader over Kiln's materials, on a real GPU
```

Without the game: `ContentHash` against the Python reference; bytes decoded and re-encoded through `tools/kfc`, identical; the structural rules; each field's
value; the budget at each limit; each mistake by name; the schema and the builder agreeing on fields and limits; the data run on the modules through the CPU
harness in `native/services/vfx/shaders/test`.

With the game installed (read only, skipped otherwise): the same rules hold for all 1,575 VFX, 638 meshes and 145 textures of the client. The hand-written
layouts match its reflection, and the fingerprint and budget match its files.

## After a game update
1. `kiln-vfxcook fingerprint --exe <exe> --gpc <vfx/vfx.gpc from tools/kfc> --kfc <kfc>`: what moved, by name.
2. If a layout moved, rebuild the modules and the builder against it, and update `expected_layouts` and `kBindingHash`. Mods' JSON and C# need no change.
3. Run the tests with the game installed. They fail if the game's mesh memory, sound count or streamed textures moved. Those can't be read at boot without decompressing resources, so `GameCounts` holds them as measured.

## Mods' own particle hooks
A particle's `shader` names a *hook module*: SPIR-V of a mod's hook functions only, built from `kiln_hooks.hlsl` + `kiln_hooks_main.hlsl`.
`read_mod_module` (`cook/shader.cpp`) reads at most 1 MiB before allocating, then `link_hook_module` (`cook/spvlink.cpp`) verifies it and links
it into `linkable_sprite()` (`shaders/build/kiln_sprite_hooked.spv`, embedded like the starter modules). The VFX entry's `strippedCode` names the
**linked** module, which is the blob served: never the mod's bytes. The safety model (memory, termination, budget, shape) is in
[shaders.md](../modding/shaders.md#how-it-is-kept-safe) and the evidence in `docs/re/shaders.md` §1. `advanced: true` is required; `shaders: false`
and `skipShaderMods` force the fallback to Kiln's sprite. The report's notes say what linked: `<mod>: <file>: hooks update, draw linked into
Kiln's sprite (N instructions, loops capped at M passes)`.

## Mods' materials in place of the game's ("replaces")
A replacement is built under the game material's GUID and listed in `resources` with `override` and its `mod`; a second mod replacing the same
GUID is listed with `standby` (the report's `served.standby`; not in `assets.json`). kiln.vfx serves each override under an owner
`vfx-reskin:<mod>` (never its own), ties it to the mod's owner when Kiln.Blocks compiles the block (`define_json`'s owner), and on a tick probes
that owner once a second: when it is revoked (the mod unloaded) the override is removed and the next mod in load order, if any, is served. See
[materials.md](../modding/materials.md#it-belongs-to-your-mod). Targets are checked against the original KFC material keys before serving, in both boot and define paths; unknown or mod-defined targets are refused by field. If those keys cannot be read, replacements fail closed. The offline cook without a game-key callback cannot validate target existence.

Explicit release of unattached, boot-only replacements requires Kiln.Blocks' data pass to be running. `Kiln.Content/GameData.cs` currently starts it only for a loaded mod with a `blocks/` folder; `Blocks.Define` also starts it. Without either trigger, a mod that fails before attaching its owner can leave its boot replacement unwatched until restart.

## Cooked ABI compatibility
The JSON format remains 1. Particle ABI 2 grows the sprite block from 272 to 328 bytes and sprite state from 60 to 64 bytes;
it adds the five play parameters. Older, self-contained cooked ABI 1 assets keep their original modules by content hash and have
no play parameters and zero defaults. The offline strict checker accepts that layout as well as ABI 2, rejecting mixed root ABIs.
Re-cooking source definitions produces ABI 2; do not substitute an ABI 2 module into an ABI 1 asset or merely change its stamp.

## Not done yet
- The sound of a VFX, decals, fog and custom meshes.
- Named parameters of a block's own: every Kiln VFX has the five above.
- A definition from code doesn't take effect until the next start.
