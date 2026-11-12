# Particle shaders: your own GPU code for a particle (advanced)

A `ParticleBlock` already covers bursts, rates, shapes, curves, flipbooks and stretched sparks with no code ([vfx.md](vfx.md)). When that isn't
enough (particles that swirl through noise, orbit a point, follow a path, pulse, react to the wind) you write **hooks** in HLSL: small functions
that change a particle when it is born, every tick, and before it is drawn. Kiln links them into its own sprite module when the game starts.

**Status (dev.26):** built since 0.2.0-dev.16 and tested on the CPU harness and on an RTX 3080 Ti (hostile hooks included); not yet seen in the game.
Research and evidence: `docs/re/shaders.md`.

## What you write
```hlsl
// mods/MyFx/shaders/swirl.hlsl
#include "kiln_hooks.hlsl"

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
#include "kiln_hooks_main.hlsl"
```

| Hook | When | May change |
|---|---|---|
| `KILN_SPAWN(inout KilnSprite s, KilnContext c)` | once, at birth, after Kiln placed it on the block's shape | everything in `s` |
| `KILN_UPDATE(inout KilnSprite s, KilnContext c)` | every tick, after Kiln applied velocity, gravity, drag and spin | everything in `s`; `s.alive = false` kills it |
| `KILN_DRAW(inout KilnLook l, KilnSprite s, KilnContext c)` | before each draw, with the look from the block's curves | `l.scale`, `l.color`, `l.alpha`, `l.roll`; `l.visible = false` skips the draw |

`KilnSprite`: `position`, `velocity` (world, metres), `age`, `life` (seconds), `roll`, `spin` (radians), `aux` (one float of your own, kept from tick
to tick), `rng`, `alive`. `KilnContext`: `dt`, `t` (age / life), `origin`, `rotation`, `scale` (the effect's), `wind`, `sun`, `play` (the
per-play parameters, see below), `user0`, `user1` (the block's eight constants), `time` (the game's VFX clock; its unit isn't verified, so use
`s.age` for anything that needs seconds). `params` is there for Kiln's own code: a hook has no buffer to read it with.

Helpers: `kiln_random(s)`, `kiln_random_range(s, a, b)`, `kiln_random_direction(s)`, `kiln_noise(p)` (smooth, -1..1), `kiln_curl(p, time)` (a swirl
that doesn't bunch particles up), `kiln_rotate(q, v)`. The kit has no curve reader (`kiln_curve` needs the parameter buffer): the block's curves
arrive already applied in `KilnLook`.

Sources written for the earlier kit (`#include "kiln_particle.hlsl"` ... `#include "kiln_particle_main.hlsl"`) build unchanged: those names now
point at the hook kit.

## Build
```sh
kiln-vfxcook shader build --mod mods/MyFx        # every shaders/*.hlsl -> shaders/build/*.spv: compiled, stamped, verified and test-linked
kiln-vfxcook shader build --mod mods/MyFx --linked   # also writes build/*.linked.spv, the module the game would compile
kiln-vfxcook shader check mods/MyFx/shaders/build/swirl.spv
kiln-vfxcook shader kit --out mods/MyFx/shaders/kit   # the kit's headers, for your editor
```
It needs `glslangValidator` on your PATH (Vulkan SDK, or your distribution's `glslang`), or `--glslang PATH`. It compiles with `-Od` so your
hooks stay separate functions (glslc can't: its HLSL path always inlines); the driver optimises the linked module. When `spirv-val` is installed
the linked result is validated too. The report gives each module's hooks, its cost (instructions, counting every call), the loop cap and how
many array indices were clamped. Players never need a compiler. Commit the source and the `.spv`.

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

**The mod's manifest needs `"advanced": true`.** The launcher shows that.

## How it is kept safe
Your `.spv` is a *hook module*: your functions and nothing else. The game never compiles it as it is. At every start Kiln verifies it and links
it into its own sprite module (`native/services/vfx/cook/spvlink.cpp`); what the game compiles is that linked module. Nothing trusts the stamp,
the compiler or the names in the file: a hand-written module gets the same treatment.

| Rule | What it rules out |
|---|---|
| **No memory but its own.** Every pointer in the hook module is to Function or Private memory (the particle's local copy). No buffer, image, push constant, input, shared (workgroup) or device-address variable can be declared. Kiln renumbers the module's ids, so hook code can only name its own values, never one of Kiln's buffers | Reading or writing the game's shared buffers: other particles, other effects, the draw lists |
| **Pointers only from variables, parameters and access chains**, and loads, stores, calls and access chains are type-checked | Forged or undefined pointers |
| **Every index is in range.** Constant indices are checked; every computed index into an array, vector or matrix is clamped (`UMin(i, length-1)`) | Out-of-bounds access to local arrays (which a GPU may not fault on) |
| **Every loop ends.** Every cycle in a function's control flow has to pass a loop header; each header pass adds one to a per-particle counter, and past the cap the loop leaves through its merge block (Kiln checks that this always leads out). The counter is reset before each hook call | Endless or very long loops: a hung GPU, a lost device |
| **A bounded amount of work.** A hook's straight-line cost includes inserted index clamps and counter operations, with every callee counted at each call site. Cost must be ≤ 16,384; loop cap = floor(16,384 / cost). Exiting loops can add up to another straight-line traversal; no recursion | A hook that is short but calls itself, or a chain of calls that doubles at each step |
| **Only plain arithmetic.** An allowlist of opcodes: maths, logic, comparisons, conversions, composites, structured control flow, GLSL.std.450 without its pointer and interpolation forms. Capability `Shader` only; no extensions | Barriers, atomics, images, derivatives, subgroup operations, `OpKill`, anything the device wasn't made for |
| **Bounded size.** Hook module ≤ 256 KB, local memory ≤ 16 KB across a live call stack, call depth ≤ 64, private ≤ 4 KB, arrays ≤ 4,096 elements | Huge scratch allocations, slow compiles |
| **What a hook returns is checked** by Kiln's code before it is used: a particle whose position, velocity, age or life isn't finite dies; other non-finite fields fall back (roll, spin, aux to 0, a look to invisible); velocity, spin and colour are clamped. Final rotation, colour and flipbook time are checked again after draw calculations, which can overflow even with finite inputs. Kiln's own guards then apply as for every particle (world bounds, scale, the list capacities) | NaN or infinity reaching the draw lists or the stored state |
| **Fallback, per module.** Any refusal draws the particle with Kiln's own sprite, says why once in kiln.log, and the block keeps its id | A broken mod hiding an effect or stopping the game |

Kiln's own part of the linked module (spawning, the particle lists, culling, the sort key, the draw record, every buffer access) is Kiln's
shipped code, `shaders/build/kiln_sprite_hooked.spv`, compiled from `kiln_sprite_hooked.hlsl` when Kiln is built.

**What it can't prevent.** A hook can still make its own effect look wrong, cost GPU time up to the budget (about a millisecond for 32,768
particles running a hook at the full budget on an RTX 3080 Ti), or, if it is malformed in a way the runtime checks don't cover (the full
SPIR-V type and structure rules are `spirv-val`'s, run by `shader build` but not at start), make the driver's compiler fail when the effect is
first played, which can end the game like any crash. These checks bound the supported instruction subset; they are not a complete SPIR-V validator or a guarantee about a driver's handling of malformed input. The instruction budget is not a wall-clock deadline or a guarantee against device loss on every GPU.

## When it isn't used
`kiln.log` says why once at start:

`vfx: my-fx: blocks/vfx.json: vfx.swirl.particles[0].shader: shaders/build/swirl.spv: <why>: Kiln's own sprite draws this particle instead`

and, when it links: `vfx: my-fx: shaders/build/swirl.spv: hooks update, draw linked into Kiln's sprite (4316 instructions)`.

| Why | Fix |
|---|---|
| `has no Kiln hook stamp` | Build it with `kiln-vfxcook shader build`, not plain glslang |
| `was built for hook ABI N, and this Kiln has hook ABI M` | Rebuild with this Kiln's tool. Your HLSL rarely needs a change |
| `is a whole particle module from an older Kiln (kiln-vfx-abi stamp)` | Rebuild: the tool now builds hook modules from the same source |
| `a pointer to storage class N`, `opcode N ... is outside what a hook module may use`, `capability N`, `an extension` | Only the kit and plain arithmetic: no buffers, textures, barriers or atomics in a hook |
| `its hooks are N instructions long counting every call (at most 16384)` | Smaller hooks, fewer calls inside calls |
| `a cycle in its control flow that doesn't pass a loop header`, `a loop header whose branch Kiln can't cap` | Hand-written or post-processed SPIR-V: build from HLSL with the tool |
| `defines none of kiln_hook_spawn, kiln_hook_update, kiln_hook_draw` | Define `KILN_SPAWN`, `KILN_UPDATE` or `KILN_DRAW` before including `kiln_hooks_main.hlsl` |
| `a mod with its own GPU code has to say "advanced": true` | The manifest |
| `mods' own particle shaders are off` | The player's `kiln/config/vfx.json` has `"shaders": false`, or lists your mod in `"skipShaderMods"` |

The linked module is about 41 KB plus your hooks (the sample's is 48 KB); the first play waits for the driver to compile it (Kiln's own
sprite, 33 KB, takes 0.08 s on an RTX 3080 Ti).

## Per-play parameters
Every Kiln VFX, with or without hooks, takes five parameters by name: `color` (a tint; its alpha multiplies alpha), `size`, `speed`,
`brightness`, `count` (burst and rate), all factors on what the block says (0 to 16). Set them from code (`Vfx.PlayAt(..., parameters: new
VfxParams().Set("color", new Color(64, 160, 255)).Set("size", 2f))`, `Vfx.Set(handle, "brightness", 0.5f)`), or on a block's `vfx` field
(`{"name": "glow", "parameters": {"color": "#80c0ff"}}`) on a cast, a status or an effect. A hook sees them in `c.play`; Kiln's sprite applies
them before your `KILN_DRAW` runs.

## Sample
`mods/samples/ShaderSample`: `swirl` (this page's hooks), `swirl_plain` (the same block on Kiln's sprite alone, to compare) and `swirl_broken`
(a hook module built without the tool, so it has no stamp and falls back), on Ctrl+Alt+F6/F7/F8.
