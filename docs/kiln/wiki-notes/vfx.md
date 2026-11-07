# Wiki note: VFX (wave 2026-11-07, agent `vfx`)

For the guide agent: what changed for the public guide (frostdev.io/kiln/wiki), with suggested text. Sources: `docs/kiln/modding/vfx.md`,
`docs/kiln/modding/shaders.md`, `docs/kiln/modding/materials.md`, `docs/kiln/services/vfx-cook.md`. **Nothing here has been seen in a game yet**;
say "new, being tested" until the client checklist in `docs/kiln/vfx-plan.md` has run.

## Effects page: new sections

**Spawn shapes.** *Particles can start anywhere on a shape instead of a single point: `"shape": {"ring": 1.5}`, or `sphere` (with `inner` for a
hollow one), `shell`, `hemisphere`, `box`, `disc`, `line`. Add `"spread": "outward"` and they fly away from the shape's centre: a shockwave ring,
a burst out of a sphere, sparks thrown off a line. The shape follows the effect's scale and rotation.*

**Streaks.** *`"align": "velocity"` turns each particle to point the way it moves, and `"stretch": 0.05` lengthens it with its speed: sparks, rain,
magic missiles' trails.*

**One effect, many looks.** *Every effect you write takes five parameters when it plays: `color`, `size`, `speed`, `brightness` and `count`. They
multiply what the block says, so one ember burst can be blue on a frost spell and twice as big on a boss. Set them from code
(`Vfx.PlayAt(burst, at, parameters: new VfxParams().Set("color", new Color(64, 160, 255)))`) or on a spell's cast, a status or an effect:
`"vfx": { "name": "warm_glow", "parameters": { "color": "#80c0ff" } }`.*

Replace the "Not yet" line: *Not yet: an effect's own sound, decals, fog, custom meshes, and particles that draw a model.*

## New page: Particle shaders (advanced)
*When the fields aren't enough, write the particle's behaviour in HLSL. You don't write a whole GPU program: you write hooks into Kiln's own
particle module (what happens when a particle is born, every tick, and before it's drawn), and Kiln keeps everything that talks to the game. Build
it once with `kiln-vfxcook shader build --mod <your mod>`; the compiled `.spv` ships in your mod and players need no compiler. A mod with its own
GPU code says `"advanced": true` in its manifest. If a player's Kiln can't use your module (built for another Kiln version, refused by a check, or
turned off), Kiln draws the particles itself, so your effect still shows.* Then the hook table and the example from `docs/kiln/modding/shaders.md`,
and a link to the sample `ShaderSample` (a curl-noise swirl next to the same effect without its module).

## Materials page: new section
*A material can take the place of one of the game's: `"replaces": "<the game material's GUID>"` changes every item, prop or creature that uses it,
with no model of your own. Keep the original's kind (opaque, cutout, ...): the game's models are built for it.* Plus how to find a GUID
(`kfc.py`, a model's `materials`), and that it is client-side only.

## Forge-parity table
Particles: "built (being tested)". Shader packs / custom render types: "particle shaders built (being tested); surface shaders researched, one
transparent slot planned (`docs/re/shaders.md`)".

## For server owners / players
*`kiln/config/vfx.json` `{"shaders": false}` turns every mod's particle shaders off (their effects then draw with Kiln's own particles);
`"skipShaderMods": ["mod-id"]` does it for one mod.*
