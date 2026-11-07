# Custom shaders for mods: feasibility (for kiln.vfx)

Client Steam build 26430625 / svn 1287369 (`enshrouded.exe` sha256 `5d9ef2f1…`). Read offline from the client's files with `tools/kfc`
(nothing kept in the repo), `spirv-dis`/`spirv-val`, and the exe's strings; the shader container, hashes and pipeline streaming are
`docs/re/vfx.md` §6, which this builds on. Tags as there: **[refl] [data] [dis] [spv] [gpu]**, **[hyp]** not verified. Nothing here has run in
the game.

## Short answer
| Kind of shader | What it gives a mod | Feasible | Status (2026-11-07) |
|---|---|---|---|
| **Particle compute module** (level D, `ShaderBlock`) | Its own per-particle code: motion, forces, spawn placement, colour, size, rotation. Drawn by the game's own sprite shaders | **Yes** | Built (0.2.0-dev.16) as *hooks*: a verified hook module linked into Kiln's sprite at start (§1); CPU and GPU harness, not yet seen in the game |
| **Transparent surface shader, one slot** (E1) | A model drawn with a mod's own fragment code, unlit, 10 parameters from its material | **Yes, not built** | Design and plan below (job M, ~2–3 sessions) |
| Many transparent / particle pixel shaders (E2) | A pixel look per sprite or per material | Yes, at more cost | Plan in `docs/kiln/vfx-plan.md` (L) |
| Opaque surface shaders (F1/F2) | Any model's G-buffer code, vertex displacement | Yes, with a large blast radius | Plan in `docs/kiln/vfx-plan.md` (L–XL) |

The particle module is the smallest working path: the game already compiles one compute module per particle entry from a content hash, so a
mod's SPIR-V needs no hook at all, only data. Every surface path needs code substitution in the render path.

## 1. Why the particle module needs no hook **[data] [dis] [spv]**
- A `VfxParticleResource` names its module by `ResourceContentReference` (`strippedCode`, category 2603049049, `bundleId` 1). The pipeline
  streamer loads that blob, checks its `ContentHash`, and creates a compute pipeline with the shared `VfxUpdatePipelineLayout`, entry `cs_main`,
  group size `{32,1,1}` (`docs/re/vfx.md` §6.3, §6.4). Kiln's three starter modules use exactly this; a mod's module is one more blob.
- The ABI is fixed by the layout: set 1 bindings 0–90, push constant `VfxPushConstants`. Every one of the game's 2,921 modules fits it, and the
  kit declares a subset (`native/services/vfx/shaders/kiln_vfx.hlsl`).
- **Trust boundary:** the kit alone cannot sandbox a whole module: helpers can be bypassed and SPIR-V hand-edited, so an allowed binding
  still exposes a whole shared buffer, and a stamp, a binding list or `spirv-val` prove nothing about termination. (That was the first design;
  a review refused it, and until 0.2.0-dev.16 mod modules always fell back.) The design now is that a mod never ships a particle module:
  it ships **hook functions**, and Kiln builds the module.

### What Kiln does with a mod's hook module (`native/services/vfx/cook/spvlink.cpp`)
At every start (and in `kiln-vfxcook shader build|check|link`, the same code), before the game or the driver sees anything:

| Step | What | Why |
|---|---|---|
| Parse | Every instruction against an allowlist with its operand layout (opcode, word count, which words are ids, types, labels, literals); SPIR-V's section order; ids defined once, within the bound (≤ 2^20); hook module ≤ 256 KB | Nothing unknown reaches the linker; framing errors are refused without reading past the module (the original 28fbf10 evidence reports 8,000 mutants under ASan/UBSan; that run predates later linker fixes) |
| Memory | Only Function and Private pointer types; only Private globals; pointers produced only by variables, parameters and access chains; loads, stores, calls, access chains, composites, phis and returns type-checked; values used only in their own function, where their definition dominates | Hook code can't express a buffer access, a forged pointer (undefined non-pointer data remains allowed) |
| Indices | Struct indices constant and in range; constant array/vector indices in range; dynamic ones clamped at link time with `GLSL.std.450 UMin(i, n-1)` (also `OpVectorExtractDynamic`/`InsertDynamic`) | No out-of-bounds access to local memory |
| Termination | No recursion (acyclic call graph). Every DFS back edge targets a block with `OpLoopMerge`. With each header's edges replaced by the edge to its merge block, the CFG must be acyclic. At link time each header increments a Private counter and branches to its merge once the counter passes the cap; `OpUnreachable` (which follows an endless loop's merge) becomes a return. Kiln's trampoline resets the counter before each hook call | Before the cap: at most `cap` header passes, and header-free paths are acyclic. After the cap: execution follows the acyclic graph above. So every hook call ends |
| Budget | Straight-line cost = instructions + inserted index clamps and counter operations + the callee's cost at every call site; must be ≤ 16,384; loop cap = floor(16,384 / cost). Call depth ≤ 64 and live Function-variable storage ≤ 16 KiB | A hook call runs at most about 2 × 16,384 instructions |
| Shape | Capability `Shader` only, no extensions, memory model Logical GLSL450; arithmetic, logic, conversions, composites, structured control flow, GLSL.std.450 without pointer, interpolation or 64-bit forms; operand shapes checked, including matrix dimensions, phi predecessor coverage and provable memory alignment | No barriers, atomics, images, derivatives, subgroups, `OpKill`, or the malformed arithmetic covered by these checks |
| Link | Hook types deduplicated against Kiln's undecorated types; constants and functions renumbered after Kiln's ids; the hooks' signatures must equal Kiln's link points; the stub bodies of `kiln_hook_spawn/update/draw` in `kiln_sprite_hooked.spv` become trampolines; new Private variables join the entry point's interface | The served module is Kiln's sprite plus the hooks, and passes Kiln's own module check (entry `cs_main`, `LocalSize 32 1 1`, capabilities) |

Kiln's sprite sanitizes what a hook hands back (`kSanitizeSprite`, `kSanitizeLook` in `kiln_particle.hlsl`) before it writes state or a draw
record, including a final check after rotation, exposure and flipbook calculations. SSA is checked again with the added header-to-merge edges: a cap must neither bypass a value definition nor leave a phi without an incoming value. `kiln_sprite_hooked.spv` is built with `glslangValidator -Od` (to keep the link points apart), the link points marked `DontInline`,
then `spirv-opt -O` (`shaders/build.sh`). Hook modules are built with `glslangValidator -Od` too: glslc's HLSL legalisation always inlines.

Not checked at start: SPIR-V's structured control-flow rules (a merge or continue target used from outside its construct) and the rest of
`spirv-val`. An accepted but invalid module may fail in the driver's compiler when the effect is first played. Driver behavior on malformed SPIR-V is not guaranteed; these runtime checks do not prove full SPIR-V validity or prevent every possible device failure. `shader build` runs `spirv-val` on the linked module. The original 28fbf10 evidence recorded 3 accepted-but-invalid mutants in 8,000, all rejected by spirv-val's structural pass. That historical run does not validate later linker changes.

Evidence: `tools/vfxcook/tests/test_shader_safety.py` (hand-written hostile modules: storage buffers, inputs, workgroup memory, barriers,
atomics, headerless loops, merge blocks that lead back, recursion, pointer selects, undefined ids, huge arrays and modules, images, extensions,
capabilities, `OpKill`, wrong signatures; truncations; random mutations), `native/services/vfx/shaders/test/test_mod_module.py` (the linked
sample and hostile hooks run in the SPIR-V interpreter, which faults on any out-of-buffer access) and `test_gpu.py --stage hooks` (endless and
nested endless loops, wild indices, NaN everywhere and a full-budget hook at 32,768 particles on an RTX 3080 Ti: worst tick 0.84 ms, no
timeout, lists and state intact).

### What Kiln checks on the linked module
| Check | Why |
|---|---|
| SPIR-V ≤ 1.5, one entry `cs_main`, compute, `LocalSize 32 1 1` | What pipeline creation assumes (`0x1af940` hard-codes the group size); a mismatch is a fatal compile |
| Capabilities `Shader`, `GroupNonUniform`, `GroupNonUniformArithmetic`, `SignedZeroInfNanPreserve`; extension `SPV_KHR_float_controls` only | What the game's own modules use, so what its device was created for |
| ≤ 1 MiB | Compile time on the player's first play: the game's largest (1.76 MB) takes 9.9 s on an RTX 3080 Ti (`docs/re/vfx.md` §3.8) |
| An `OpSourceExtension "kiln-vfx-hooks-N"` stamp on the hook module equal to this Kiln's `KILN_HOOKS_ABI` | Only for a clear "rebuild" message: the stamp authorises nothing |
| The mod's manifest says `"advanced": true` | Decision #5 |
| `kiln/config/vfx.json` `"shaders": false` / `"skipShaderMods"` | Off switches that keep the effect (Kiln's sprite draws it) |

## 2. Surface shaders: what selects a raster pipeline **[refl] [data] [spv]**
- Materials don't name shaders. `RenderEffectId` (11 values) and the flags pick among fixed pipelines in `renderer/renderer.gpc` (132 raster
  pipelines; code blob 7,727,772 B, stored uncompressed in `enshrouded_053.dat` (container flags 0)). Model draws are GPU-driven
  through `RenderMeshDrawData2.pipelineBits` = `effectId | AlphaTested << 8 | DoubleSided << 9 | DisableShadows << 10 | HashedAlpha << 11` and a
  C++ table read by `Culling2_Instanced` (`docs/re/vfx.md` §5.3). The table's contents weren't read **[hyp]** for which effect id reaches which pipeline.
- Vanilla resources use 3 of the 11 effect ids (Pbr 3,403, TerraformingCursor 48, SkyTexture 7). The material loader still has tables for
  **Grid, VfxGrid, Gizmo** (128 slots each, `0x103678`), and Grid materials are made by code (the building grid).

### The Grid slot (E1's candidate) **[spv] [data]**
`GridRfx_Transparent_GpuCulling2_Instanced`: cull none, no depth write, targets `R16G16B16A16_sfloat` + `R8_unorm`, premultiplied blend as the
other transparent passes.

| | Size | Interface |
|---|---|---|
| `vs_main` | 7,232 B, range 7,415,592 | set 0 b3, set 1 b11, set 2 b1–b3 (instances, culled lists); outputs loc 0 flat `uint` (material index), loc 1 `float2` (UV) |
| `fs_main` | **4,852 B**, range 7,422,824, checksum non-zero | inputs loc 0 flat `uint`, loc 1 `float2`; output loc 0 `float4`; reads `g_rfxMaterials` set 2 b0, stride **40** = `GridRfxRenderMaterial` {`axisSize` f32, `lineSize` f32, `color0` float4, `color1` float4} |

So a model whose material has effect Grid would be drawn by this pipeline, and the fragment shader sees exactly its UV and ten floats of its
material. Replacing that one 4,852-byte module gives "a transparent, unlit surface with your own pixel code and ten parameters", with no other
pipeline touched. No light, no scene depth, no time: those would need bindings the module doesn't declare (set 0's global constants are bound for
the vertex stage and could be added; their layout is `GlobalDescriptorSetLayout`, unread).

### Two ways to substitute the code
| Way | How | For | Against |
|---|---|---|---|
| **A. GPC override** | Serve a patched `renderer.gpc`: the code blob with the mod's module appended, `GridRfx`'s `fs` range pointed at it with checksum 0 (skips the XXH3 check, `docs/re/vfx.md` §6.3), and `strippedCode` naming the new blob's real `ContentHash`. Every other range stays as it was | Data only, through kiln.assets as VFX already are; nothing hooked in Vulkan | Needs the original `GpcResource` (71,984 B) and code blob before the renderer opens it. At `on_ready` the resource map isn't up (`resource_read` fails), so Kiln has to read the KFC itself: the blob is stored plain, the resource may be compressed **[hyp]**; a C++ reader of that container is new work (the GPL kfc-parser can't be used). 7.7 MB served per start |
| **B. Shader-module substitution (H5)** | Hook `vkCreateShaderModule`; when the code is 4,852 B with the build's known XXH3, hand the driver the mod's module instead | Small; no game data to read; fails soft by not matching on another build | The render path is kiln.ui's (its loader-reload handling must be reused, not copied). The exe resolves `VK_KHR_maintenance5`, whose pipelines may take `VkShaderModuleCreateInfo` chained into `VkPipelineShaderStageCreateInfo` without `vkCreateShaderModule`: both paths need covering **[hyp: which the game uses]**. Windows needs `vkGetDeviceProcAddr` wrapping (kiln.ui does this already) |

Either way a bad module is a fatal compile ("Error compiling gpc pipeline", `0xabcd10`), so the same kind of checks as §1 run first: entry
`fs_main`, fragment, the exact interface above (locations, types, set 2 b0 with stride 40, nothing else), capabilities within the game's.

## 3. Design for E1 (not built)
- **Block:** `MaterialBlock { Shader = "shaders/build/glow.spv", Constants = [...] }`, kind `transparent`, so a material is the unit a model
  already names. The builder writes a `RenderMaterialResource` with effect Grid, `GridRfxRenderMaterial` data from up to 10 constants, no
  textures. `material.cpp`'s `build_material` is the one place that sets `Effect Pbr` today; the schema already reserves `shader`.
- **Kit:** `kiln_surface.hlsl`: the fragment interface above, `KILN_SURFACE(float2 uv, KilnSurfaceParams p) -> float4`, built by
  a hook function like the particle kit's, linked into a Kiln-owned `fs_main` by the same verifier and linker (`spvlink.cpp`).
- **One shader per game:** there is one Grid pipeline, so one surface module per mod set. With several mods, each mod's function is linked into
  one module that switches on a constant in the material (an id word): that is E2's linker in small, and the reason E1 stops at one slot.
- **Substitution:** way B in `native/services/vfx/surface.cpp`, through a callback kiln.ui would expose for shader-module creation (a request to
  the `render` owner, like `subscribe_pre_overlay`); off unless a mod asks.
- **The model side:** modelcook writes `renderEffectId` Grid and no flags for a material of kind `transparent` (models' owner: an existing field).
- **Fallback:** no match on this build, a check failing, or the switch off: the material is built as a plain Pbr material from its `fallback`
  colour, so the model still draws.

### Plan with sizes
| Step | Job | Evidence it needs |
|---|---|---|
| 1. RE: which pipeline index `pipelineBits` 6 reaches (the C++ table read by `Culling2_Instanced`), and whether a Grid material on a cooked model draws at all | S, offline + 1 client session (an agent's, booked through the coordinator) | A Grid material on a test model shows the game's own grid pattern |
| 2. RE: does the game create raster pipelines through `vkCreateShaderModule` or maintenance5's chained create info | S, offline disassembly of the pipeline creation job | The call sites of the two |
| 3. kiln.ui: a shader-module callback (owner `render`) | S | kiln.ui's tests |
| 4. `surface.cpp`: match, check, substitute; `kiln_surface.hlsl`; build tool stage | M | Harness: the substituted module draws in a headless Vulkan pipeline with the game's `vs_main` (as `tools/vfxcook/tests/material_gpu.py` does for G-buffer) |
| 5. Builder, schema, C#, modelcook field, sample, checklist | S–M | Lab: built and served; client: the model shows the mod's pattern |

This is a feasibility plan. Its trust boundary would be the particle path's: mods supply verified hook functions, Kiln owns the entry point and every resource access.

## 4. What wasn't looked at
The global descriptor set's layout (needed for time or light in a surface shader); the OIT variants' fragment interface (E2); `culling_2.gpc`
(F2); whether Steam's Fossilize replay records a substituted module (it records what the game creates, so likely yes **[hyp]**).
