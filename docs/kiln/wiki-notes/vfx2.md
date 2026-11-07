# Wiki note: particle shaders and material reskins are back (0.2.0-dev.16)

Both were switched off in the dev.15 review. They are on again, with hook verification, linking and mod-owned material replacements.

## For the guide's "Particle shaders" page
Suggested text:

> **Your own particle code (advanced).** Write HLSL *hooks*, small functions that change a particle when it is born (`KILN_SPAWN`), every tick
> (`KILN_UPDATE`) and before it is drawn (`KILN_DRAW`). Include `kiln_hooks.hlsl`, define your hooks, include `kiln_hooks_main.hlsl`, and run
> `kiln-vfxcook shader build --mod <your mod>` (needs `glslangValidator`). Point a particle's `"shader"` at the `.spv` it writes and set
> `"advanced": true` in `kiln.mod.json`.
>
> When the game starts, Kiln checks your module and links your hooks into its own particle code. Your hooks only ever see the one particle
> they are given: they can't touch the game's buffers, every loop is capped, every array index is kept in range, and anything non-finite they
> return is caught. If the runtime checks refuse a module, Kiln's own sprite draws the particle and `kiln.log` says why. Players can turn mods' particle code
> off with `"shaders": false` in `kiln/config/vfx.json`.

Changes from the earlier page: the kit is now `kiln_hooks.hlsl` / `kiln_hooks_main.hlsl` (the old names still work); the compiler is
`glslangValidator`, not `glslc`; old `.spv` files must be rebuilt (Kiln says so in the log); `kiln_curve` is gone from the hook kit.

## For the guide's "Materials" page
Suggested text:

> **Reskinning a game material.** `"replaces": "<GUID of a game material>"` draws your material wherever the game uses that one: items, props,
> creatures. Keep the original's kind. The reskin belongs to your mod: unload the mod and Kiln takes it off (the game draws its own material
> again from the next start, since it loads materials once). If two mods reskin the same material, the first in the load order wins and the
> other takes over if the first unloads.

## Facts to keep straight
- Status: tested in the CPU and GPU harnesses and on a server lab; not yet seen in the real client.
- The sample mods are `ShaderSample` (Ctrl+Alt+F6/F7/F8) and `MaterialSample` (the workbench wood becomes frost steel).
- Runtime hook checks are not a complete SPIR-V validator or a guarantee about driver behavior on malformed modules.
- Explicit cleanup of unattached boot reskins requires the Kiln.Blocks data pass to have started; see the startup limitation in materials.md.
- Full details: `docs/kiln/modding/shaders.md`, `docs/kiln/modding/materials.md`.
