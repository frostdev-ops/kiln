# Particle shaders: your own GPU code for a particle (advanced)

A `ParticleBlock` already covers bursts, rates, shapes, curves, flipbooks and stretched sparks with no code ([vfx.md](vfx.md)). When that isn't
enough (particles that swirl through noise, orbit a point, follow a path, pulse, react to the wind) you can write the particle's update in HLSL.
Kiln compiles nothing at run time: you build the module once, and the `.spv` ships in your mod.

**Status:** built and tested on the CPU harness; not yet seen in the game. Checklist steps P1–P5 in `docs/kiln/vfx-plan.md`. Research and the
checks: `docs/re/shaders.md`.

## What you write
Your module is Kiln's own sprite module with *hooks*: Kiln keeps everything that touches the game's shared buffers (spawning, the particle
lists, culling, drawing), and you change what a particle does and how it looks.

```hlsl
// mods/MyFx/shaders/swirl.hlsl
#include "kiln_particle.hlsl"

void swirl_update(inout KilnSprite s, KilnContext c)
{
    s.velocity += kiln_curl(s.position * c.user0.y, 0.0f) * c.user0.x * c.dt;   // constants from the block
}

void swirl_draw(inout KilnLook l, KilnSprite s, KilnContext c)
{
    l.color *= 1.0f + 0.3f * length(s.velocity);    // faster is brighter
}

#define KILN_UPDATE swirl_update
#define KILN_DRAW swirl_draw
#include "kiln_particle_main.hlsl"
```

| Hook | When | May change |
|---|---|---|
| `KILN_SPAWN(inout KilnSprite s, KilnContext c)` | once, at birth, after Kiln placed it on the block's shape | everything in `s` |
| `KILN_UPDATE(inout KilnSprite s, KilnContext c)` | every tick, after Kiln applied velocity, gravity, drag and spin | everything in `s`; `s.alive = false` kills it |
| `KILN_DRAW(inout KilnLook l, KilnSprite s, KilnContext c)` | before each draw, with the look from the block's curves | `l.scale`, `l.color`, `l.alpha`, `l.roll`; `l.visible = false` skips the draw |

`KilnSprite`: `position`, `velocity` (world, metres), `age`, `life` (seconds), `roll`, `spin` (radians), `aux` (one float of your own, kept from tick
to tick), `rng`, `alive`. `KilnContext`: `dt`, `t` (age / life), `origin`, `rotation`, `scale` (the effect's), `wind`, `sun`, `play` (the
per-play parameters, see below), `user0`, `user1` (the block's eight constants), `params` (the raw block, for `kParamF(c.params + KS_...)`), `time`
(the game's VFX clock; its unit isn't verified, so use `s.age` for anything that needs seconds).

Helpers: `kiln_random(s)`, `kiln_random_range(s, a, b)`, `kiln_random_direction(s)`, `kiln_noise(p)` (smooth, -1..1), `kiln_curl(p, time)` (a swirl
that doesn't bunch particles up), `kiln_rotate(q, v)`, `kiln_curve(base, t)`.

## Build
```sh
kiln-vfxcook shader build --mod mods/MyFx        # every shaders/*.hlsl -> shaders/build/*.spv, stamped and checked
kiln-vfxcook shader check mods/MyFx/shaders/build/swirl.spv
kiln-vfxcook shader kit --out mods/MyFx/shaders/kit   # the kit's headers, for your editor
```
It needs `glslc` on your PATH (Vulkan SDK, or your distribution's `shaderc`), or `--glslc PATH`. Players never need a compiler. Commit the
source and the `.spv`.

## Use it
```jsonc
{ "format": 1,
  "vfx": {
    "swirl": {
      "particles": [ {
        "rate": 60, "duration": 4, "lifetime": [2, 3], "shape": { "disc": 1.5 }, "spread": "sphere",
        "size": { "keys": [[0, 0], [0.15, 0.12], [1, 0]] }, "color": ["#a0f0ff", "#4060ff"],
        "texture": "vfx/dot.png", "blend": "additive",
        "shader": { "file": "shaders/build/swirl.spv", "constants": [6, 0.8, 0.4] }
      } ] } } }
```
C#: `Shader = new ParticleShader("shaders/build/swirl.spv", [6f, 0.8f, 0.4f])`. Every other field of the block still works: your hooks start from
what it says.

**The mod's manifest needs `"advanced": true`**: this code runs on every player's GPU, and a bad loop can hang it. The launcher shows that.

## When it isn't used
Kiln never lets the game compile a module that failed a check. The particle is then drawn by Kiln's own sprite, so the effect still shows, and
`kiln.log` says why once at start:

`vfx: my-fx: blocks/vfx.json: vfx.swirl.particles[0].shader: shaders/build/swirl.spv: <why>: Kiln's own sprite draws this particle instead`

| Why | Fix |
|---|---|
| `has no Kiln ABI stamp` | Build it with `kiln-vfxcook shader build`, not plain `glslc` |
| `was built for particle ABI N, and this Kiln has ABI M` | Rebuild with this Kiln's tool. Your HLSL rarely needs a change |
| `uses set S binding B, which the kit doesn't declare` | Only the kit's buffers are allowed |
| `capability ... is outside what the game's modules use`, `LocalSize is not 32 1 1`, `the entry point is not cs_main` | Don't change `kiln_particle_main.hlsl`'s entry |
| `a mod with its own GPU code has to say "advanced": true` | The manifest |
| `mods' own particle shaders are off` | The player's `kiln/config/vfx.json` has `"shaders": false`, or lists your mod in `"skipShaderMods"` |

Size: up to 1 MiB; above 256 KB the tool warns, as the first play waits for the driver to compile it (the sample's 70 KB module compiles in
about 0.2 s on an RTX 3080 Ti; Kiln's own sprite, 33 KB, in 0.08 s).

## Per-play parameters
Every Kiln VFX, with or without a shader, takes five parameters by name: `color` (a tint; its alpha multiplies alpha), `size`, `speed`,
`brightness`, `count` (burst and rate), all factors on what the block says (0 to 16). Set them from code (`Vfx.PlayAt(..., parameters: new
VfxParams().Set("color", new Color(64, 160, 255)).Set("size", 2f))`, `Vfx.Set(handle, "brightness", 0.5f)`), or on a block's `vfx` field
(`{"name": "glow", "parameters": {"color": "#80c0ff"}}`) on a cast, a status or an effect. A module sees them in `c.play`; Kiln's sprite applies
them before your `KILN_DRAW` runs.

## Sample
`mods/samples/ShaderSample`: `swirl` (this page's module), `swirl_plain` (the same block on Kiln's sprite, to compare) and `swirl_broken` (a
module that fails its checks on purpose), on Ctrl+Alt+F6/F7/F8.
