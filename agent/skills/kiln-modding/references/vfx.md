# Particles, lights, the game's VFX

Wiki: [vfx](https://frostdev.io/kiln/wiki/vfx). `kiln lint` checks all of it.

## What's been seen in a real game
Nothing built by Kiln, so far: files, budget and checks are proven offline, and effects have run on real GPUs in a test harness. Say "needs a client check" about how anything looks, or whether it shows. A dedicated server draws nothing. Kept current at
https://frostdev.io/kiln/wiki/status.

## Writing one
- Scaffold: `kiln new vfx MyFx`. Bigger example: `mods/samples/VfxSample`.
- A `vfx` section in `blocks/*.json`, or `Blocks.Define("name", new VfxBlock { ... })` with `KilnServices` `Blocks`. Prefer data: a block in a file is there from the first start, and one from code from the next, because the game lists VFX once (dev.27+: Kiln.Sdk bakes code-defined VFX into the mod at build, so they are there from the first start too).
- A particle block needs `burst` or `rate`. Textures are PNGs in `vfx/`, power of two, 4 to 2048.
- Attach with a `Vfx` field on an effect, area, on-hit, cast, projectile or status: a block's name, `"other-mod:name"`, `"game:<name>"` or a GUID. From code: `Vfx.PlayAt`, `Vfx.PlayOn`, `Vfx.Stop`.

## The game's VFX
- Never guess a name: `kiln catalog search fireball --section vfx`, then `kiln vfx info <name>` for its length and parameters.
- VFX have no names of their own. A name is a template or sequence that uses it. Catalog entries without `byName` only have a label: write `"game:<guid>"`. `kiln lint` says when (KL323).
- `GameVfx.*` constants hold GUIDs and need `<KilnGameIds>Vfx</KilnGameIds>` and a catalog. Don't use them in a project others build without one.
- **Parameters** only work on an effect, area or on-hit, and only on a game VFX: `{ "name": "game:X", "parameters": { "radius": 3, "color": "#ff0000", "count": { "int": 7 } } }`. Kiln refuses them on a cast, projectile, status or a VFX block. The game ignores a wrong name or type silently, so run `kiln lint` with a catalog (KL321).

## Rules that matter
- A new VFX needs `side` both and `clients` `mod` (KL211). Only naming the game's VFX doesn't.
- All mods share room for 473 new VFX, 386 meshes, about 240 KB of mesh memory and 10 sounds in VFX (KL320). Past a limit Kiln refuses the whole mod.
- A VFX block's `sound` is counted but not played yet. No decals, fog, custom meshes or custom shaders yet.
- From code, a play reaches only Kiln clients near it, and joiners don't see it. For what every player must see, attach it to data.
