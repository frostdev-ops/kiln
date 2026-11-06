# VFX, particles, materials and shaders (for kiln.vfx)

Client Steam build 26430625 / svn 1287369 (`enshrouded.exe` sha256 `5d9ef2f1…`, `enshrouded.kfc` version
`1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54Z`); dedicated server Steam 26430253 (`f0d824be…`) for
comparison. RVAs are client RVAs (image base `0x140000000`). Sources:
- the game's own reflection, read offline from the exe with `tools/kfc`;
- every VFX, material and shader resource of the client KFC, decoded with `tools/kfc`;
- SPIR-V read with `spirv-dis`/`spirv-val` (local tools, nothing kept in the repo) and the new `tools/kfc/kfc/spirv.py`;
- capstone and Ghidra 12.1.2 headless decompiles without auto-analysis;
- the client's own `enshrouded.log` and the headers of its cache files.

One client session, hosting an offline throwaway world, measured §4.5; everything else was read offline. No game files, no kfc-parser
code and no third-party code are in the repo.

Evidence tags:
- **[refl]** the game's reflection records;
- **[data]** checked over the game files, with counts;
- **[dis]** disassembly / decompile of the client;
- **[spv]** read from the game's SPIR-V;
- **[log]** the client's own log;
- **[gpu]** run on real GPUs in the standalone harness, with no game involved (§3.8);
- **[rt]** seen in a running game;
- **[hyp]** hypothesis, not verified.

**Only §4.5 is [rt].** Whether the game accepts Kiln's modules, and how they look in the world, needs the running game.

## Short answers
- **Particles run on the GPU, in compute.** Every `VfxParticleResource` names one compute shader by content hash: a raw
  SPIR-V 1.5 module, entry `cs_main`, `LocalSize 32 1 1`, generator "Google spiregg" (DXC), so the source was HLSL. All
  14,352 particle entries, 2,921 unique modules, 156.8 MB. The module simulates and appends draw records to GPU lists;
  a fixed set of raster pipelines draws them. **[data] [spv] [dis]**
- **"Particle shader" means that compute module.** It is not a vertex/fragment pair. The look of a sprite particle comes
  from fields it writes into `keen::TransparentInstanceData` (176 B, named in reflection), drawn by the game's fixed
  `Transparent_Forward*` pipelines. **[refl] [spv]**
- **The game's node graph is compiled away.** Reflection still holds the editor types (`VfxDefinition`, `VfxNode`,
  `VfxExpression*`), but no graph or bytecode ships. `useNewVm` is false in all 1,575. What ships is SPIR-V, one
  variable-length parameter stream per particle (`gpuResourceData`) and patch points that write resource indices into
  that stream at load. **[refl] [data] [spv]**
- **Shared modules are parameterised interpreters.** The largest module (1.76 MB) serves 1,260 particles and reads a
  stream with a phi-driven cursor: each value has a mode word and takes more or fewer words. Stream length varies per use
  of the same module (812–840 B). So a colour is not at a fixed offset, and hand-editing floats needs a decoder per
  module. The compact modules are the opposite: fixed offsets, most values baked in as SPIR-V constants. **[data] [spv]**
- **The particle protocol is read in full (§3.5), and runs on the CPU.** A SPIR-V interpreter runs the game's own emitter,
  sprite and `VfxPrepare` modules against the CPU side as read from the exe, with every list and counter invariant checked.
  Kiln's starter modules (`native/services/vfx/shaders`) pass the same harness, on the CPU and on real GPUs. **[gpu]** They
  have not run inside the game.
- **Shaders live in `keen::GpcResource`** (74 resources): pipelines, layouts, descriptor names, and a code blob of
  concatenated SPIR-V modules, 1,485 modules in 28.7 MB. Debug code is referenced but not shipped. Pipelines are found by
  index, not by name. **[refl] [data] [dis]**
- **The game verifies shader code at run time.** Its pipeline streamer recomputes `ContentHash` over code that covers a
  whole blob and checks an XXH3-64 checksum over ranges. A mismatch logs "[pipeline_streaming] Checksum error…", and for
  VFX ends in a fatal "error streaming vfx pipeline". kiln.assets' sha256 hash rule is therefore wrong for shader blobs.
  **[dis]**
- **Keen's ContentHash is solved.** It is a 128-bit AES-round hash; `tools/kfc/kfc/contenthash.py` reproduces it for
  4,473 of 4,473 blobs tried, and the empty-input result equals the three constants the game tests against. `keen::
  XxHash64` is XXH3-64: 1,485 of 1,485 module checksums match. **[data] [dis]**
- **Unknown GUIDs are fatal.** Asking for a VFX GUID the VFX system did not register at init aborts the process
  ("corrupted vfx resource reference"). Too many resources, a missing pipeline blob or a failed pipeline compile abort too.
  There is no soft failure anywhere in this path. **[dis]**
- **New resources work through kiln.assets, if they are there early.** The VFX system enumerates VFX, mesh and curve
  resources once, through `ResourcesByType` (`S+0x7d0`), a slot kiln.assets hooks. So `assets.json` entries are seen, and
  `Assets.Register` after that point is not, unless the enumeration is re-run (it looks safe to, **[hyp]**). **[dis]**
- **Materials do not choose shaders.** `RenderEffectId` (11 values) plus flag bits choose among fixed pipelines. A
  clone can change every parameter and texture of the PBR block and the flags, and nothing about the code. Vanilla uses
  3 of the 11 effect ids. **[refl] [data]**
- **The server has no VFX.** No `VfxResource`, `RenderMaterialResource` or `GpcResource`, and no `[vfx]` strings beyond the
  shared program names. Its templates and sequences hold the GUIDs anyway. Clients make every effect from replicated
  state. **[data]**
- **Vulkan only.** No D3D imports; DX12 strings exist only for a DXGI present path behind `--enable-dxgi`. Shaders are
  SPIR-V only on this PC build. **[dis] [log]** (`docs/re/client-render.md`.)
- **"Processing Vulkan shaders" is Steam's**, a Fossilize replay in `steamapps/shadercache/1203620`, not game code. The
  game's own cache is `%LOCALAPPDATA%/enshrouded/caches/vk_pipeline_cache.bin`, tagged with the build number. **[data]**

## 1. Inventory **[refl] [data]**
The brief's "VfxResource 2,044" is the number of sound patch points inside VFX (`docs/re/audio.md`). There are 1,575
`VfxResource`s.

| Type | Size, layout hash | Client / server | What |
|---|---|---|---|
| `VfxResource` | 216 B, `0xf6128527` | 1,575 / 0 | One effect (§2). 16 have no particles |
| `VfxMeshResource` | 56 B, `0xe79cbc08` | 638 / 279 | Particle mesh: counts, bounding sphere, blob (§2.5) |
| `VfxCurveResource` | 16 B | 8 / 0 | `samples[]` over a time range. Only 3 are used by patch points |
| `VfxJointCollectionResource` | 24 B | 30 / 4 | Skeleton + joint list for emitting from a body |
| `VfxSystemResource` | 16 B | 1 / 0 | Holds the 3D simplex noise texture |
| `NotifierResource` | 124 B, `0x688596e9` | 2,319 / 2,319 | `defaultVfx` (1,909 set), parameters, `defaultSound` |
| `RenderTextureResource` | 72 B, `0xef11efbc` | 304 / 0 | 272 2D, 28 3D, 4 cube; 263 `bindless`. What VFX images name |
| `RenderDecal` | 44 B | 52 / 0 | Decal data block with texture patch points |
| `RenderMaterialResource` | 40 B, `0x7804bc58` | 3,458 / 0 | §5 |
| `RenderModel` | 352 B | 10,470 / 0 | `docs/re/models.md`. Vertex format `Vfx` has 0 meshes: VFX meshes are a separate type |
| `GpcResource` | 136 B, `0x3ea062ce` | 74 / 0 | §6 |
| `RenderResources`, `RenderSystemResource` | 168 B, 304 B | 1 / 0 | `gpcResources` list, global noise, snow, blue-noise maps |
| `Game38ClientResources` | 2400 B | 1 / 0 | Seven global VFX (`grassVfx`, `waterVfx`, `areaLootVfx`, build gizmos…), LUTs |
| `AmbientVfxCollectionResource` | 32 B | 1 / 0 | 48 ambient effects by environment, weather, time |
| `AmbientPostProcessingResource`, `CharacterScenePostProcessingResource` | 144 B, 96 B | 1, 1 / 0 | Data-only grading (§6.8) |
| `MaterialFeedback` + collection, `WaterSurfaceEffectMaterialResource`, `FishingEffectResource`, `VoxelMaterialVfxDataResource` | | 274 + 1, 2, 1, 1 / 193 + ?, 0, 0, 1 | Tables naming VFX |

Editor-only types exist in reflection but have no resources: `VfxDefinition`, `VfxNode`, `VfxImage`, `VfxMeshObject`,
`VfxJointCollection`, `VfxExpression`, `VfxParameterExpression`, `VfxStateExpression`, 26 `VfxExpressionValue_*`, 21
`VfxEffectParameter*`, `Material`, `PbrMaterial`. `keen::ds::*` mirrors them. References in cooked data keep the editor
type's name: `VfxReference` = `ObjectReference<VfxDefinition>`, and the runtime resource has the same GUID, as with
sounds. The exe has the tool's file names (`vfx_definition_node_list.kbf`, `vfx_particle_node_list.kbf`) and the string
"Enable combined shaders", but no compiler.

**VFX have no names.** `VfxResource` has no `debugName` and a particle's `debugName` is a GUID. Of 1,575, 704 can be named
from a referring template and 1,042 from a template or a sequence. **[data]** A catalog has to derive names.

### Who references VFX (census over the client's resources) **[data]**
| Referrer | Hits | Field |
|---|---|---|
| `MaterialFeedbackCollectionResource` | 6,277 | `collisionEffectMatrixEntry[].effects[].vfx` |
| `TemplateResource` | 5,264 + 16 + 1 | `components[].value.vfx` (`VfxComponentResource` and similar), `levels[].vfx`, `errorVfx`. 4,400 templates |
| `MaterialFeedback` | 1,114 + 740 + 407 × 4 | collision, environment, and per-size event effects |
| `ActorSequenceResource` | 1,993 + 104 | `VfxNotifierEvent.vfx`, `SpawnEntityEvent.vfxOnSpawn` |
| `NotifierResource` | 1,909 | `defaultVfx` |
| `ItemInfo` | 1,429 + 132 | `cursorConfig.addFxConfig.oneShotVfx`, `cursorVfx` |
| `SequenceResource` | 343 | `VfxNotifierEvent` in animation clips |
| `BuffTypeCollectionResource` | 147 + 29 | `whileApplied[].vfx`, `onCreate[].vfx` |
| `AmbientVfxCollectionResource`, `SceneResource`, water, fishing, global | 48, 20, 14, 13, 7 | |

## 2. keen::VfxResource **[refl] [data]**
| Offset | Field | Notes |
|---|---|---|
| 0x00, 0x04 | `cullingDistanceMin/Max` f32 | 50–75 m in 1,214 of 1,575 |
| 0x08 | `killTimeInSeconds` u32 | 10 in 1,547 |
| 0x0c | `emitterCount` u32 | 0–11+ |
| 0x10 | `useNewVm` bool | false in all |
| 0x14 | `particles` BlobArray<`VfxParticleResource` 104 B> | 14,352 in total, up to 55 per VFX |
| 0x1c | `stateInfos` BlobArray<`VfxParticleStateBufferInfo`> | parallel to `particles`: names, types and offsets of state members (§2.2) |
| 0x24 | `springs` BlobArray<`VfxSpringResource` 184 B> | ropes; 5 VFX use them |
| 0x2c | `gpuResourceData` BlobArray<u8> | the parameter stream (§2.3) |
| 0x34..0x64 | `meshes`, `models`, `decals`, `images`, `lightFilters`, `sounds`, `curves` | patch points (§2.4) |
| 0x6c | `parameters` BlobArray<`VfxUserEffectParameterDescription` 12 B> | §2.6 |
| 0x74 | `defaultParameterData` u32[16] | the 64-byte block user parameters live in |
| 0xb4 | `defaultParameterDataResourceReferences` | meshes, models, joint collections that parameters may name |
| 0xcc, 0xd4 | `effectJointCollections`, `jointCollectionCount` | |

All 1,575 re-encode byte for byte through `tools/kfc` (§8).

### 2.1 VfxParticleResource (104 B)
- `debugName` (a GUID), `depth` u16, `stateSizeInBytes` u16 (32–320, median 144), `parent` u32 (`0xffffffff` = none),
  `child` u32, `maxCount` u32 (1–20,000; 8,783 entries are 1, i.e. emitters).
- `code` ResourceContentReference (+0x18): the debug SPIR-V; **not in the KFC** for any of 14,352. Category 1483009432.
- `strippedCode` ResourceContentReference (+0x38): the module the game loads. `category` 2603049049, `bundleId` **1**,
  `sortKey` its index. The selector `0xabde30` returns this one unconditionally. **[dis]**
- `initialState` BlobArray<u8> (present in 1,824), `initializeStateGpuResourceDataRange`,
  `evaluateParametersGpuResourceDataRange` Range16: slices of `gpuResourceData`.

8,858 entries are at depth 0, 5,494 at depth 1. Depth 0 is mostly an emitter (`maxCount` 1) that spawns into its `child`.

### 2.2 State members
`stateInfos[i].members[]` = {name, componentCount, componentType (Float, SignedInt, UnsignedInt, BoolMask), offsetInBytes}.
506 distinct name/type pairs: the graph's vocabulary. Every state starts with `VfxBaseParticleState` (20 B: `emitterIndex`,
`myParentEmitterIndex`, `instanceIndex`, `rngState`, `tickCount`), so user members start at +20. Most common: `age`
14,277, `position` 14,030, `lifetime` 13,946, `ageNormalized`, `timer`, `aux`, `velocity`, `color`, `brightness`, `alpha`,
`scale`, `orientation`, `flipbookFrame`, `spin`. These names are debug data: the shader does not read them.

### 2.3 gpuResourceData
Per VFX: median 3,136 B, largest 24,324 B, 5.67 MB in total. Copied to a GPU buffer at activation (`0x1afb50`) with patch points applied, then read
by the compute shader through `g_resourceDataBuffer`.

The stream is **not a fixed struct per module [data] [spv]**:

| Module (bytes of SPIR-V) | Particles using it | Eval stream length, most common | Words that vary between uses |
|---|---|---|---|
| 1,760,180 | 1,260 | 836, 840, 820, 812 | 158 of 209 |
| 828,880 | 1,142 | 788, 796, 800, 784 | 137 of 197 |
| 700,720 | 952 | 924, 912, 932, 916 | 144 of 231 |
| 731,588 | 763 | 700, 708, 704, 712 | 132 of 175 |
| 1,045,544 | 667 | 424, 420, 416, 412 | 79 of 106 |
| 99,944 | 430 | 140, 152, 128, 132 | 19 of 35 |

Reads go through a cursor built from phis: `srl(add(phi(add(prev, 12), phi(…)), 4), 2)`. Only the first few values have a
constant offset (a taint trace of the 828,880-byte module found 12 words at fixed offsets out of 197). So each value
carries a mode that decides how many words follow. The shipped modules behave like interpreters over an option stream.
**[spv]** The stream format has not been decoded. Reading it means following one module by hand: 48,172 instructions for
the 828,880-byte one.

### 2.4 Patch points **[dis]** (`0x1afb50`)
`VfxPatchPoint{Range16 resourceDataRange}`. The game copies `gpuResourceData`, then writes at `start`:

| Array | Stride | Written | Counts |
|---|---|---|---|
| `meshes` | 20 B | u32 mesh index (resolved through `0x19e8c0`: fatal "invalid vfx mesh reference" on a null GUID, index 0 on an unknown one, which draws nothing) | 25,226 points, 360 unique |
| `models` | 20 B | u32 model handle | 10,017, 801 unique |
| `decals` | 20 B | u32 decal handle | 1,770, 52 unique |
| `images` | 24 B | u32 bindless texture index + u32 sampler index (from `VfxSamplerParameters`) | 24,926, 145 unique |
| `lightFilters` | 20 B | u32 bindless index | 2 |
| `sounds` | 20 B | u32 sound index (`VfxSoundEvent` from the shader → `SoundStart` on the CPU) | 2,044, **6 unique** |
| `curves` | 20 B | 5 words: curve location and time scale | 3 |

So the safe, fully understood knobs on an existing VFX are its textures, meshes, models, decals and sounds.

### 2.5 VfxMeshResource blob **[data]** (`meshData`, category 2543730610)
Vertices at 0, then u16 indices. 28 B per vertex, as `Transparent_Forward`'s `vs_main` decodes it **[spv]**:

| Bytes | Field | Decode |
|---|---|---|
| 12 | position | f32×3 |
| 4 | UV | f16×2, origin top-left |
| 4 | normal | 11:11:10: x = bits 0–10 / 1023.5 − 1, y = bits 11–21 / 1023.5 − 1, z = bits 22–31 / 511.5 − 1 |
| 4 | tangent | 10:10:10:2: each / 511.5 − 1, w = bits 30–31 / 1.5 − 1 |
| 4 | colour | RGBA8 unorm. The fragment shader squares rgb |

These are not `RenderModel`'s encodings. Blob size is a multiple of 28 (638 of 638): vertices, indices, then zeros up to the
next multiple. The game relies on it: `0x1ae46b` computes `vertexOffset` as allocation offset / 28 and `indexOffset` as (allocation offset + 28·V) / 2 **[dis]**, so one
blob off the grid shifts every mesh allocated after it. Mesh memory is 0x1100000 B and vanilla's meshes take 17,567,564 of it, about 240 KB of room for all mods, and running
out is fatal **[dis]**. Billboards use a quad in the XY plane, normal −Z: `e55ea493-…`, at
(∓0.5, ±0.5, 0), UV (0,0) top-left, indices 0 1 2 2 3 0, normal word `0x00200400`, tangent word `0x200803ff`, colour
`0xffffffff`, sphere (0, 0, 0, 0.7071068), 140 B. `25b068a4-…` is the same quad in XZ.

## 2.6 User parameters **[refl] [data]**
`VfxUserEffectParameterDescription` = {`nameHash` FNV-1a of the name, `typeId` DsTypeId, `dataRange` into the 64-byte block,
`jointCollectionIndex`}. 8,049 descriptions on 1,344 VFX, 415 distinct names. 32 names are recovered by hashing strings
(covering 1,486 uses): `radius` 384, `duration` 204, `color` 160, `speed` 113, `count` 83, `lifetime` 77, `targetPosition`
47, `distance` 44, `height` 41, `direction` 36, `brightness` 26, `size` 18, `alpha` 17, `spawnRate` 4, … The rest are
unnamed so far.

Setter: `VfxSetParameter 0x1a4d50(scene, instance, nameHash, typeId, {ptr,size})`. FxController wrappers `0x29d920`–`0x29db60`.
The FxController itself sets `targetPosition` (`0x539c5c1b`) for targeted effects at start (`0x2a1b50`). Callers can also
pass a whole 64-byte block at creation (`hasVfxParameters`), as templates (`VfxParametersTemplateComponent`,
`VfxParametersOverwriteComponent`), sequences and notifiers do.

A value set after the start reaches only what reads it later **[hyp]**. `VFX_Dummy_Impact` (also named `Base_Impact_Explosion_VFX`; `color` at byte 12,
default `#ff00ffff`) is one burst emitter whose particles keep their own `color` state member: with `color` set one FxController update after the
start, its burst showed the default magenta in a client **[rt]**. So kiln.vfx writes the values a VFX describes into `FxParams` (the
block, then `has` = 1) and keeps `VfxSetParameter` for later changes.

### 2.7 Textures a VFX names **[data]**
All 145 are `bindless`, untiled, BC7 (131) or BC5 (14), with `content` category 4010512636, `bundleId` 0, `sortKey` 0 and channel mapping Identity. 136 are Texture2D,
9 Texture3D. The blob holds the mip chain, largest level first. `mipChainContentRanges` has one range per level with more than 4 KiB left to the end of the blob, each
running to the end; a texture with no more than 4 KiB in all has none. That rule holds for every 2D texture checked, with any format that has a chain.

## 3. The simulation: compute modules and draw lists
### 3.1 Passes **[data] [dis]**
`vfx/vfx.gpc` (`ad606c3f-…`) holds the fixed helpers: `VfxPrepare`, four `*Writeback` sorters, `VfxCullInstances`,
`VfxCullInstancesMeshlets`. It also holds `VfxUpdatePipelineLayout`, the layout of every particle module, which has no
pipelines of its own. `vfx/vfx_cpu_drawcall.gpc` holds 6 raster pipelines for model particles (Visibility, GBuffer, Shadows,
each also DoubleSided).

The tick `Vfx_Tick` (`0x1a6660`, from `0x1a5240`) binds each chunk's module and dispatches with the push constant
`data = chunkIndex·4 | phase`. **[dis]** The phases **[dis] [spv]**:

| Phase | When | A module does |
|---|---|---|
| 0 | The last simulation step of a frame, game running | Update and draw |
| 1 | Earlier steps of the same frame (the tick loop catches up in steps) | Update only: modules skip all draw lists |
| 2 | Paused: the step is a fixed 1/60 s (`0xfe502b` ns) | Draw only: no state writes, no spawns, nothing dies. A chunk new this tick gets phase 0 instead |

Update flow in a module, read from the smallest one (9,496 B, three uses; the bigger ones have the same frame) **[spv]**:
1. `chunk = g_chunkUpdate[data >> 2]`;
2. if `GlobalInvocationID.x >= counter[activeParticleCounterByteOffset]`, stop;
3. `id = stateData[oldActiveParticleIndicesOffset + 4·i]`, `state = particleStatesOffset + stateSize·id`;
4. read parameters from `g_resourceDataBuffer` at `evaluateParametersResourceDataOffset`, instance data from
   `g_instanceParameters[state.instanceIndex]`;
5. simulate, write state back;
6. alive: `atomicAdd(nextActiveParticleCounter)`, append to `newActiveParticleIndices`; dead: append to
   `freeParticleIndices`, decrement the emitter's count;
7. append draw records to the feature lists.

The whole protocol, CPU side included, is §3.5.

### 3.2 The ABI **[refl] [data] [spv]**
`VfxUpdatePipelineLayout`: `useBindlessDescriptors = true`, sets {0: `GlobalDescriptorSetLayout`, 1:
`VfxUpdateDescriptorSetLayout`, 4: bindless}, push constants `VfxPushConstants` (8 B: `data`, breadcrumb marker offset).

Every one of the 2,921 modules fits this: all (set, binding) pairs are in the layout. Bindings 83–90 of set 1 are the
layout's 8 static samplers. Set 4 = binding 0 `image[32768]`, binding 1 `sampler[256]`. **[spv]**

Capabilities used by all 2,921: `Shader`, `SignedZeroInfNanPreserve`, `GroupNonUniform`, `GroupNonUniformArithmetic`;
extension `SPV_KHR_float_controls`. Scalar block layout (`spirv-val` needs `--scalar-block-layout`). All have push constants
and no `OpName`s.

Set 1, by what a particle does (particles using each, of 14,352; the full binding list is in `vfx/vfx.gpc`, `kfc.py decode ad606c3f-f737-51b2-9617-e0c5aba82b11 GpcResource`):

| Binding | Name | Stride | Used by |
|---|---|---|---|
| 0 | `g_update` | UBO 1392 (`VfxUpdateBuffer`: time, camera, sun, wind, capacities, counter offsets) | 14,349 |
| 1, 2, 3, 4 | `g_transparentInstance{Unlit,Lit}{Unshaded,Shaded}List`, sort data | 176, 8 | 5,990; 5,489; 7,063 |
| 5, 6 | water mesh, flow mask lists | 144, 60 | 1,412; 1 |
| 7, 8 | `g_injectInstanceList` (volumetric fog) | 116 | 167 |
| 9, 10 | `g_pointLightList` | 52 | 1,025 |
| 11, 12, 35, 36 | decal lists | 112 | 204; 256 |
| 13 | `g_emitterStates` | 32 | 14,345 |
| 17–20 | `g_vfxMeshes`, `g_meshData`, joints | 32, -, 4, 76 | 7,065; 5,245; 2,506 |
| 22, 23 | simplex noise, world height map | | 5,178; 3,505 |
| 24–26, 29, 30, 37, 43–45 | model instances, culling, previous transforms | 128, 48… | 1,280 |
| 27, 28 | sky texture, terraforming cursor instances | 44, 80 | 6; 120 |
| 31, 33, 34 | grass, water displacement, water SDF bodies | 24, 24, 32 | 567; 1,420; 6 |
| 39, 40 | `g_chunkUpdate`, `g_instanceParameters` | 68, 128 (`VfxCombinedEffectParameters`) | all |
| 41, 42 | `g_resourceDataBuffer`, `g_chunkStateDataBuffer` | | all |
| 46–50 | surface pickers, voxel material info | | 2,530; 1,934 |
| 52, 53 | `g_soundEvents`, count | 16 | 980 |
| 54–63 | water bricks, fronts, splash | | 6,587 |
| 72–76 | SDF atlas, BVH (collision) | | 6,891 |
| 81, 82 | breadcrumbs | | all |

The game's `VfxFeature` enum names what a module may do: DrawTransparentMesh, DrawWaterMesh, DrawWaterFlowMaskMesh,
DrawModel, DrawTerraformingCursor, AddDecal, AddScatterDecal, DrawSkyTexture, InjectVolumetricFog, AddPointLight, PlaySound,
AddGrassMovement, AddSnowDisplacement, AddWaterDisplacement, AddWaterSdf, WorldChunks, WaterFallPoints, WaterSplashPoints,
WaterWaveFrontPoints, DynamicHeightmap, WorldSdf. Launch flags `--disable-vfx` and `--disable-vfx-feature-%s` exist
(`0x859320`).

### 3.3 The draw records **[refl] [spv]**
`keen::TransparentInstanceData` (176 B, layout `0x510d10ef`) doubles as indirect draw arguments and per-instance data:
`indexCount`, `instanceCount`, `indexOffset`, `vertexOffset`, `instanceOffset`, `meshIndex`, `image`, `normals` (bindless
texture), `imageSampler`, `uvDistortionMapSampler`, `edgeFadeOutFactor`, `cameraRelativePosition`, `depthFadeDistance`,
`rotation`, `scale`, `color`, `alpha`, `perceptualRoughness`, `uvOffset`, `uvScale`, `uvDistortionOffset`, `uvDistortionScale`,
`uvDistortionStrength`, `uvDistortionMap`, `alphaClipValue`, `alphaClipHalfRange`, `flags`, `motionVectorImage`,
`motionVectorScale`, `flipbookTime`, `encodedFlipbookSize`. The SPIR-V struct has the same offsets.

The `flags` bits have no names in the game. What `fs_main` does with them **[spv]**; values vanilla writes: 0, 1, 2, 3, 5, 6,
0x15, and those with 0x100 **[data]**:

| Bit | Effect | Name used here |
|---|---|---|
| 0 | Output alpha 0, so with premultiplied blending the colour adds | Additive |
| 1 | Colour takes ambient and sun light through the fog volume | Lit |
| 2 | `alpha` becomes a soft erode through `alphaClipValue` and `alphaClipHalfRange` | Clip |
| 3 | The erode reads vertex alpha | ClipVertex |
| 7 | Full shading with the `normals` map | Shaded |
| 8 | Not read by the draw shaders. `SeparateTrianglesByWaterSurfaceAndSize` tests it | Unknown: Kiln leaves it 0 |

Other fields: `image` 0 = white. `encodedFlipbookSize` = columns | rows << 16, `flipbookTime` is fract'd to a frame.
`depthFadeDistance` soft-fades against scene depth above 0.001. `edgeFadeOutFactor` above 0 is a view-angle fade exponent. Colour
result = vertex rgb² × texture rgb × `color`. The draw is `DrawIndexedIndirect` with `gl_InstanceIndex` = `instanceOffset`.

Others: `PointLightData` (52 B), `VolumetricFogInjectInstanceData` (116 B), `ForwardDecalData` (112 B),
`VfxTerraformingCursorInstance`, `VfxSkyTextureInstance`, `WaterFlowMaskMeshInstanceData`, `VfxCullingInstance`.

**Which list [spv].** `g_update.splitTransparentDrawPipelines` = 0: one list (binding 3), counter
`transparentInstanceCounterByteOffset`, capacity `transparentInstanceBufferCapacity`, plus sort data (binding 4) `{key, index}`,
where `h = half(-distance)`, `key = ((h ^ (-(h >> 15) | 0x8000)) & 0xffff) << 16 | (id & 0xffff)`. Not 0: bit 7 goes to binding 3, else
bit 1 to binding 2, else binding 1, each with its own counter and capacity, with no sort data. A full list: `n = atomicAdd(counter, 1)`;
`n >= capacity` undoes the add and skips.

**A point light [spv]** (`PointLightData`, 52 B, sort data `{f32 distance, index}`): `position`; `radiusAndSqrRadius` = half(r²) |
half(r) << 16; `colorAndShadowPositionOffset` = {half(r) << 16 | half(g), half(b) << 16 | half(ox), half(oy) << 16 | half(oz)};
`uniqueId` = instance << 16 | particle id; `orientation` = the quaternion (0,0,0,1) as 4 halves; `spotScaleAndOffset` 0 and flag
bit 1 unset for a point light; flag bit 0 from data (meaning unknown); `filterTexture` 0.

### 3.4 Data only versus fixed
| Change | How | Status |
|---|---|---|
| Texture, mesh, model, decal, sound of a VFX | Patch point GUIDs | Understood [dis] |
| User parameters (colour, radius, duration…) | Default block, or per play | Understood where a VFX exposes them [refl] [dis] |
| Cull distances, kill time, `maxCount` | Fields | Understood; `maxCount` also drives memory |
| Literal rates, sizes, colours in the stream | Words in `gpuResourceData` | Needs a per-module stream decoder (§2.3) |
| Behaviour (forces, shapes, spawn rules) | The SPIR-V | Not data: a different module |
| Sprite look | Fields written to `TransparentInstanceData`, drawn by fixed `Transparent_Forward*` | Fixed unless the raster shader is replaced (§6.6) |

### 3.5 The particle protocol
**Read** from the compact vanilla pair of VFX `54347d4a-…` (emitter 15,544 B, sprite 25,472 B), the 16,836-B light, `VfxPrepare`, and
the CPU side `0x197a20`, `0x1b1aa0`, `0x1a6660`, `0x1a5240`. **Tested** by running those modules in a SPIR-V interpreter
(`native/services/vfx/shaders/test/`) and on real GPUs (§3.8), with the invariants below. Not tested inside the game.

Two levels only: 5,494 of 5,494 children have a depth-0 parent whose `child` points back. `emitterCount` = entries with a child, 1,575 of
1,575 VFX. `child` 0 means none, so a child is never entry 0.

**Allocation [dis] (`0x197a20`, per entry, when an instance gets its memory):**
- Count: **a depth-0 entry always gets 1 state**, whatever `maxCount` says. Others get `maxCount`.
- In the shared state buffer, each block 16-aligned: states, active list A, active list B, free list (u32 each).
- Counters: active A, active B, both start at 1 for depth 0 and 0 otherwise. Free starts at count minus that.
- With a child: an emitter slot, and two more counters at 0 (particle, spawn).

**Init [dis] (`0x1b1aa0`):** two `VfxChunkUpdateBuffer` tables, one per frame parity, swap A and B everywhere (active counter with next
counter, old list with new list, the child's and parent's active lists). `VfxEmitterState` = {particle counter, spawn counter,
`maxParticleCount` = child count, 0, `particleChunkIndex` = child, instance slot}. Depth 0 is seeded: `emitterIndex` = slot or
0xffffffff, `myParentEmitterIndex` 0, instance slot, random `rngState`, tick 0, then `initialState` or zeros; lists hold 0..count-1.
Other entries: the free list holds count-1..0.

**Tick [dis]:** `VfxPrepare` per active emitter: `maxSpawnCount = min(free, maxParticleCount - particleCount)`, spawn counter = 0. Each
entry's next-active counter = 0. Dispatch sorted by depth with a barrier between depths, ceil(count / 32) groups, so invocation ids cover
0..count-1 rounded up. The module's own test ends the extra ones. Then the parity flips.

**A module's duty [spv]:**
1. `chunk = g_chunkUpdate[data >> 2]`. `i = GlobalInvocationID.x`. Stop if `i >= counter[active]`.
2. `id = data[old + 4i]`, `state = particleStates + stateSize * id`. `rng = state.rngState + i`. First tick when `tickCount == 0`.
3. Alive, or phase 2: write state back unless phase 2 (`tickCount + 1`), `n = atomicAdd(counter[next], 1)`, `data[new + 4n] = id`.
4. Dead: `n = atomicAdd(counter[free], 1)`, `data[freeIndices + 4n] = id`, and `atomicAdd(counter[g_emitterStates[myParentEmitterIndex].particleCounter], -1)`.
   Vanilla does this for roots too, so a dying root decrements emitter slot 0's count. Kiln's roots don't.

**A spawn, from a root, not in phase 2 [spv]:**
1. `emitterIndex >= g_update.emitterStateCount`: skip.
2. `atomicAdd(counter[e.spawnCounter], 1) >= e.maxSpawnCount`: undo, skip.
3. `f = atomicAdd(counter[child.free], -1)`, `newId = data[chunk.childParticleFreeList + 4(f - 1)]`.
4. `a = atomicAdd(counter[child.active], 1)`, `data[chunk.childActiveParticleIndices + 4a] = newId`. The child runs in this same tick.
5. `atomicAdd(counter[e.particleCounter], 1)`.
6. Write the whole child state: 0, parent's `emitterIndex`, `e.instanceIndex`, a new seed, tick 0, zeros.

**Other facts [spv]:**
- RNG: `s = s * 0x2c9277b5 + 0xac564b05`; output `x = ((s >> ((s >> 28) + 4)) ^ s) * 0x108ef2d9; (x >> 22) ^ x`.
- World positions in floats; `cameraRelativePosition = (int(round(p * 131072)) - cameraWorldPosition) / 131072`, so ±16,384 m at most.
- Cull: `dot(c, plane.xyz) + plane.w > radius` for 5 planes means outside.
- Billboard: `rotation = cameraOrientation * rollZ`, on the XY quad.
- Instance flags: bit 0 IsActive, bit 1 IsHidden. Vanilla's compact children die at once when not active.
- The CPU also culls: an instance beyond `cullingDistanceMax` loses its memory (`0x1ad320`), and regains it from the start when near.

**End of an instance [dis]:** `VfxDestroyInstance 0x1a93e0` is the stop: when `killTimeInSeconds` is 0 or the instance has no memory, it goes
at once. Otherwise it stays until now + kill time at most, then all its chunks go, whatever is alive. No API reports that an effect has
finished, so a one-shot played from code needs a stop from Kiln. In practice instances go well before the kill time (§4.5). That
IsActive clears at the stop is **[hyp]**, as the upload was not traced.

### 3.6 Kiln's starter modules and their data
`native/services/vfx/shaders`: `kiln_emitter`, `kiln_sprite`, `kiln_light`, ABI header `kiln_vfx.hlsl`, compiled with `glslc` to
`build/*.spv` (9.6, 25.6, 12.6 KB; `Shader` capability only). **The parameter layout is `kiln_vfx_params.h`**, plain `#define`s for both HLSL
and C++.

Every buffer is declared as raw words with explicit offsets, so struct packing can't differ between compilers. Conservative limits, because
these buffers are shared with every other effect: spawn loop 64 per tick, bursts carried over, a clamped child state clear, frustum loop of 5,
all loops fixed.
Non-finite or out-of-world particles die and never draw. Size is clamped to 64 m.

**What a builder writes for a `VfxBlock`:**

| Field | Value |
|---|---|
| `particles` | For each `ParticleBlock`, two entries: root, then child. A `LightBlock` is one more root |
| root | `depth` 0, `parent` 0xffffffff, `child` = next index, `maxCount` 1, `stateSizeInBytes` `KE_STATE_SIZE`, module `kiln_emitter` |
| child | `depth` 1, `parent` = root index, `child` 0, `maxCount` the block's (≤ `KILN_VFX_MAX_PARTICLES`), `stateSizeInBytes` `KS_STATE_SIZE`, module `kiln_sprite` |
| light | `depth` 0, no parent or child, `maxCount` 1, `stateSizeInBytes` `KL_STATE_SIZE`, module `kiln_light` |
| `strippedCode` | The module's real `ContentHash`, category 2603049049, `bundleId` 1, `sortKey` the entry index, as vanilla |
| `code` | Zeros: never selected |
| `debugName` | A GUID as text, as vanilla |
| `initialState` | Empty: state starts zeroed and the modules initialise on tick 0 |
| `evaluateParametersGpuResourceDataRange` | The block's `{start, count}` in `gpuResourceData` |
| `initializeStateGpuResourceDataRange` | `{start, 0}` |
| `stateInfos` | One per entry: the `*_S_*` members and size. Debug data, but keep it correct |
| `gpuResourceData` | The blocks, in order. Total a multiple of 4 (all 1,575 vanilla are) and under 64 KB (Range16) |
| `images` | `{start + KS_IMAGE, 8}`, the `RenderTextureResource`, `VfxSamplerParameters` |
| `meshes` | `{start + KS_MESH, 4}`, a `VfxMeshResource`: Kiln's quad by default, built from §2.5's numbers |
| `sounds` | Later: needs a `kiln_sound` |
| `emitterCount` | The number of roots with a child |
| `cullingDistanceMin/Max` | The block's, default 50, 75 (vanilla's most common) |
| `killTimeInSeconds` | At least the longest particle life + light fade out, rounded up. Vanilla's most common is 10 |
| `useNewVm`, `parameters`, `springs`, `curves`, joints | Empty, false |

Curves are sampled by the builder to 8 evenly spaced values. Colours are linear RGB, so the builder converts sRGB. A burst-only block:
`duration` small and above 0, or the emitter waits for a stop.

### 3.7 Driving a game module instead
Binding particles to one of the game's modules rather than Kiln's gives far less than it looks like.
- The large shared modules take a variable-length stream, undecoded, per module, and the names stay partly guesses.
- The compact modules have fixed offsets but little in them: the 25,472-B sprite exposes draw flags, image, sampler, mesh, an RGBA8 colour, a
  size. Everything else (lifetimes 0.3–0.6 s, upward drift, its curves) is baked in as constants.
- So a `ParticleBlock` can't be expressed on them. What's possible is a retinted, retextured copy of that one effect, tied to one module hash
  that any game update may remove.
- **Its use is diagnostic:** the same VFX with its entries swapped to the 25,472-B module tells the data path (resources, patches, meshes,
  textures) apart from the shader. A builder only has to change the entry's hash and block.

### 3.8 Run on real GPUs **[gpu]**
`native/services/vfx/shaders/test/test_gpu.py` and `gpu/vfxgpu.cpp`: headless Vulkan with no window, swapchain or game. It runs on an NVIDIA RTX 3080 Ti and an AMD iGPU through
RADV (subgroup sizes 32 and 64). With the game's files present, pipeline layouts and comparison shaders come from them at run time. Without
them it derives the layout from Kiln's modules and draws with `test/render_fallback.hlsl`.

| What ran | Result on both GPUs |
|---|---|
| The protocol of §3.5 with real dispatches: chunk seeding, the A/B flip, depth order, barriers between depths and none within | Lists stay a partition of the states and emitter counts match, every tick |
| The CPU suite's scenarios: bursts, rate, loop, overflow, full lists, all phases, stop, hidden, split lists, NaN and huge data | 47 of 47 |
| The game's own emitter, sprite, light and `VfxPrepare` through the same checks | Hold |
| Contention: 32 instances spawning in one depth, all appending to one list, 4,096 particles live | No lost or duplicated slot, no torn or stale state read back, list overflow settles exactly at capacity |
| Draw records as real `vkCmdDrawIndexedIndirect` arguments, drawn by the game's `Transparent_Forward` `vs_main` and `fs_main` | Billboards face the camera at any camera rotation, upright and unmirrored; flipbook frames, additive and alpha blending as §3.3 |
| Kiln's sort keys through the game's `VfxTransparentDrawWriteback` | Nearest on top. The sort itself was done by the test, ascending |
| Cooked output of `kiln-vfxcook` patched as the game patches it | Hashes, wiring and stop times hold. Each effect draws with the cooked quad and the BC7 texture it names, whole mip chain uploaded |

Pipeline compile time, no cache, on the same two GPUs:

| Module | NVIDIA | RADV |
|---|---|---|
| `kiln_emitter`, `kiln_sprite`, `kiln_light` | 19–68 ms | 4–11 ms |
| The game's median module, ~48 KB | 94 ms | 15 ms |
| The game's largest, 1.76 MB | 9.9 s | 3.4 s |
| `Transparent_Forward` (graphics) | 91 ms | - |

**What this leaves open:** whether the game accepts the modules and serves them through kiln.assets; its real camera, exposure and fog values; its radix sort; which queue it
dispatches on; the Lit and Shaded paths with real lighting; the sampler modes a cooked effect names, since the harness draws with one sampler; Windows drivers.

**Validation.** `test_gpu.py --validate` runs any stage under `VK_LAYER_KHRONOS_validation`. `sync`, the default, adds synchronization validation. `gpuav` adds GPU-assisted
validation with robust access off, unlike the game, so it can see out-of-bounds shader access and bad descriptors. Each run first breaks a rule on purpose and stops if the layer
doesn't report it, and a missing layer stops the run with a message. Findings go to `validation-<stage>.json`.

With layer 1.4.363, both modes, on both GPUs, with and without the game's files: no errors in any stage (the cooked stage ran only with them). The only warnings are GPU-assisted
mode's own notes on its settings and speed. This covers the harness's Vulkan calls and the shader paths its scenes run. It says nothing about the game's own calls, and the
game's modules in the compile stage are built, not run.

## 4. How VFX start and stop
### 4.1 The layers **[dis]**
```
ECS programs, UI, game code
  → EcsFxController: VFX at a transform 0x2a0f30 (floats: 0x2a1040), attached 0x2a0e00, targeted 0x2a0c80
                     one-shot wrappers 0x2a0b80 0x2a0bf0 0x2a0b10 0x2a0a90 (lifetime at most 60 s)
                     loop wrappers 0x2a1ef0 0x2a1f20 0x2a1ec0 0x2a1e80 (no end time)
                     FxStop 0x2a1f70 (78 callers), start 0x2a1b50 (entry type 0 = sound, 1 = VFX), update 0x2a28c0
                     (sounds: 0x2a0700, 0x2a0940, wrappers 0x2a05d0 0x2a0650 0x2a1df0 0x2a1e30)
    → RenderCreateVfx 0xf3910 (jump to 0xfd9d0)
      → VfxCreateInstance 0x197de0(out, scene, const Guid*, transform, params)
        → VfxResolve 0x1a0200: GUID → resource handle; refcount; first use calls VfxActivate 0x1afb50
```
- `VfxCreateInstance` takes an instance slot from a free list (0xc0 B each). An empty pool returns handle 0: that is a soft
  failure. Without user parameters it copies the VFX's defaults.
- Stop: `0x1a93e0`, reached through `0xf7250`. Destroy at once: `0x19c9f0`, reached through `0xf6820`. Flags: `0x196100` sets
  `VfxBaseEffectParametersFlag` (`IsActive`, `IsHidden`).
- The registered set is a map in the system: occupancy bits `+0x1be0`, capacity `+0x1be8`, keys `+0x1c00` (16 B), values `+0x1c18`, count
  `+0x1c2c`; slot = low 32 bits of the GUID & (capacity - 1), then linear. A value indexes the VFX table `+0x1c38` (0xc0 B each, the resource
  at `+0x10`). The only other `VfxResolve` caller is the system's own 11 global VFX, from a static table (`0x1b1610`).
- FX entries, in a pool at `FxController+0x98`: type `+0`, parameters `+4` (72 B), transform or attachment `+0x50`, second one `+0xc8`, GUID
  `+0x160`, VFX instance `+0x170`, sound `+0x174`, end time `+0x180` with flag `+0x188`, finish time `+0x190` with flag `+0x198`.
- An attachment is {u32 entity, u8 `AttachmentSlot`, `keen::ecs::Offset` at +8, u8 follow at +0x30}, 52 B. An unresolved one waits.
- **A start that gets no instance retries every frame for ever**, and an end time doesn't stop it (`0x2a28c0`). An entry with an empty GUID
  finishes at once (`0x2a1b50`). A guard that only returned handle 0 would leave unknown GUIDs holding pool slots, which sounds share.
- The start copies "has an end time" into the instance as flag `+0x68` bit 0. Beyond `cullingDistanceMax` such an instance is destroyed
  rather than parked (`0x1ad320`).
- Handles are `slot | generation << 16`. A stale handle is a no-op; an out-of-range one is fatal ("corrupted vfx instance
  handle").
- Nothing here takes a lock, and callers are client ECS programs and game code. The FxController's update runs on the client world's
  thread (§4.5).

### 4.2 Triggers **[refl] [data] [dis]**
| Trigger | Data | Program | Attachment |
|---|---|---|---|
| Entity with `VfxComponent` + `VfxComponentResource{vfx}` (or `VfxResourceDynamic`) | templates | `update_vfx` `0x31e480`, `update_vfx_volatile` `0x31ed90`, targeted `0x31e770` | `slot`, `attachmentOffset`, `attachedToEntity`, `attachToParent`; `ToggleModifier` |
| Sequence event `VfxNotifierEvent` (256 B) | `ActorSequenceResource`, `SequenceResource` | `handle_notifier_events` `0x30d1a0` | `slot`, `notifierOffset`, `attach`, `cancelFromActorSequence`, optional `vfxParameters`, tag filters |
| Spawn | `SpawnEntityEvent.vfxOnSpawn` + `overwriteVfxParameters` | actor programs | spawn transform |
| Notifier | `NotifierResource.defaultVfx` via `ecs::NotifyEvent{type Vfx, typeGuid, transform, slot, offset, attach, vfxParameters}` | `handle_notifier_events` | as the event |
| Buff | `BuffType.onCreate[]`, `whileApplied[]` {slot, offset, sfx, vfx, parameters} | client buff programs | yes |
| Item | `cursorVfx`, `addFxConfig.oneShotVfx`, enemy, material tables | various | |
| Ambient, scene | `AmbientVfxResource`, `SceneVfx` | `ambient_effects` `0x302e30`, scene | position |

**Impact programs do not play VFX.** They spawn templates (config `Template`), and those templates carry the VFX, like
`Explosion_Player_Spell_Fireball_1` (`Impact`, `LifeTime` 1 s, `VfxComponent`, `VfxComponentResource`,
`docs/re/abilities.md`). **[data]**

Bones: `AttachmentSlot` (36 values) goes through the animation graph's `slotBoneIndexMapping` (`docs/re/models.md` §4).
Emitting from a whole body uses `VfxJointCollectionResource` (`g_vfxJoints`).

### 4.3 Lifetime and limits **[dis]** (`VfxSystemCreate 0x198590`)
| Thing | Capacity | Vanilla use | On overflow |
|---|---|---|---|
| VFX resources | 0x800 = 2,048 | 1,575 (room 473) | **fatal** "too many vfx resources" |
| Meshes | 0x400 = 1,024 | 638 | **fatal** "too many vfx meshes" |
| Mesh memory | 0x1100000 = 17,825,792 B | 17,567,564 B, all loaded at init | **fatal** "out of vfx mesh memory". The allocator (`0x1ae2f0`) takes `meshData.hash.size` at alignment 1 |
| Curves | 0x10 = 16, 0x2000 data | 8 | **fatal** |
| Sounds | 0x10 = 16 | 6 unique | **fatal** "too many vfx sounds" |
| Pipelines loaded at once | 0x800 = 2,048 | 2,921 unique modules | LRU: frees an idle VFX and retries (`0x1ac360`) |
| Instances | from config | | handle 0 (soft) |

All VFX, meshes, curves and sounds are registered at init whether used or not. Refcounts free a VFX's GPU data when idle.
`killTimeInSeconds` is the upper limit between a stop and the instance going (§3.5, §4.5).

### 4.4 Activation **[dis]**
1. `VfxInitResources 0x1b0ed0`, state `+0x2138`: 0 → enumerate through `S+0x7d0` three lists (mesh, curve, VFX), register all;
   1 → wait for mesh and curve data; 2 → ready. **While the state is not 2, activation does nothing.**
2. `VfxActivate 0x1afb50`: per particle, `VfxRequestPipeline 0x1a0010(sys, &strippedCode, &name)`: map by content hash,
   refcounted, async. An empty `strippedCode` skips the entry; empty means size 0 with the three empty-hash constants. Size 0 with other words, or a size with all three
   words zero, is fatal ("invalid vfx pipeline code hash"). Failing requests evict and retry.
3. Resolve meshes, models, decals, images, sounds, curves; allocate and upload patched `gpuResourceData`.
4. Until all pipelines exist, nothing runs, so a first play starts late rather than hitching.

### 4.5 Measured in a running client **[rt]**
Client 26430625, hosting an offline world, kiln.vfx hooked on `VfxCreateInstance`, `FxStart` and `FxUpdate`. This says what the code does;
nothing here judges how an effect looks.

- **Thread.** `FxUpdate` ran on the thread the client and prediction worlds step on (the main thread); the hosted server world steps on its
  own. Its first update came about 4 s before the client world's first logged step, at game time 0.
- **One controller.** One `EcsFxController` for the whole session, so one per client world. `FxController+0x20` is the world pointer the
  game's entity lookups take: the local player (entity 1) resolved through it.
- **The registered set** reads as above: 1,575 entries, capacity 4,096, the occupancy bits add up to the count.
- **An unknown GUID, on the game's own paths:** through the FxController it is refused at the start, gets handle `0x4100017e` with instance 0, and
  the entry is gone at the next update. Straight into `RenderCreateVfx`, the detour on `VfxCreateInstance` returns 0. Neither aborts.
- **Calls that ran without a fault:** play at a transform, attached to entity 1 at slot `HandR`, `VfxSetParameter`, `FxStop`, a finish
  written into an entry, `FxLookup`. All from inside the FxController's update.
- **How long an entry lives.** Time from the play until the FxController dropped the entry, all with a kill time of 10 s:

| VFX | Stopped after | Dropped after |
|---|---|---|
| `b0b78a7d…` (a template's explosion) | 0.2 s | 5.6 s |
| | 1 s | 6.1 s |
| | 3 s | 6.1 s |
| | 10 s | 10.0 s, at the stop |
| `081baeeb…` (an impact explosion) | 0.1 s | 1.4 s |
| | 10 s | 10.0 s, at the stop |
| `0717d2bc…` (a zone) | 1.5 s | 3.6 s |
| `3217214c…` (a torch flame, a loop) | 1 s | 2.9 s |

So instances go on their own, well before the kill time, whether stopped early or not, and a stop doesn't end what's already alive at once. The
explosion's three early stops ended within 0.5 s of each other, and it was already gone at 10 s. **[dis]** `0x1ad320` destroys an instance whose
memory slot isn't marked in use that frame; marked means its chunk isn't stopped, or a value read through a pointer per chunk is still ahead
of the chunk's own (a fence or a readback, **[hyp]**). The entry itself is only released once its end time has passed and the instance is gone
(`0x2a28c0`). An entry with no end time isn't released until something stops it.

## 5. Materials **[refl] [data] [dis]**
`RenderMaterialResource` = {`effectId`, `flags`, `data`, `dataTypeSignature`, `images`, `textureFeedbacks`, `debugName`}:

| `RenderEffectId` | Vanilla materials | Block |
|---|---|---|
| `Pbr` 1 | 3,403 | `PbrRfxRenderMaterial` (244 B, `0x1220f31f`) |
| `TerraformingCursor` 9 | 48 | `TerraformingCursorRfxRenderMaterial` (44 B) |
| `SkyTexture` 10 | 7 | `SkyTextureRfxRenderMaterial` (12 B) |
| `Billboard` 0, `PbrTerrain` 2, `PbrBuilding` 3, `PbrRoof` 4, `Gizmo` 5, `Grid` 6, `VfxGrid` 7, `Transparent` 8 | 0 as resources | Terrain and buildings use their own tables; `GridRfxRenderMaterial` 40 B exists |

- `images[]` = {`dataOffset`, `RenderStreamedTexture`}: inline streamed textures whose bindless handles are written into the
  block at `dataOffset`. Formats in use: BC7 (albedo), BC5 (normal), BC1, BC4, BC6H.
- `RenderMaterialFlag`: Tessellated, AlphaTested, DoubleSided, DisableShadows, CanDissolve, Translucent, Foliage, HashedAlpha.
  They and `RenderMeshDrawData2.pipelineBits` route a mesh to a pipeline: `Model_{Visibility,GBuffer,Shadow}[_Discard]
  [_DoubleSided][_HashedAlpha]_GpuCulling2_*`. **[data]**, routing detail **[hyp]**.
- `PbrRfxRenderMaterial`: two layers of albedo, normal, emissive, params, height maps with tint, plus blend map, overlay
  normal, translucency, dissolve glow, anisotropy, vertex-colour swizzles, and `flags` (dissolve, gradient map, AO, opacity,
  contact shadows, anisotropy, disable frost/snow/wetness, glitter, translucency).
- **A clone can change all of that**, including flags. It cannot change the code.

### 5.1 The loader **[dis]**
`MaterialAcquire 0xcf540` (from the model loader, `0xe105f`) finds or adds a GUID in a map, allocates a record of `0xb0` B, and `MaterialUpdate 0xd1490` walks it through states 0 to 3; 4 is failed.
- **The block type is checked.** The loader keeps one table per effect, set at render system create (`0x103678`): {Pbr `PbrRfxRenderMaterial`, **4,096**}, {Gizmo, Grid, VfxGrid, TerraformingCursor, SkyTexture, 128 each}. A material whose `dataTypeSignature` isn't its effect's, or an effect with no table (Billboard, PbrTerrain, PbrBuilding, PbrRoof, Transparent), goes to state 4.
- **Slots come from a free list.** No slot left: state 4. A model with a material in state 4 stays unloaded and doesn't draw; nothing is logged and nothing aborts. Materials hold slots while a model holds them, so 4,096 is what's alive at once, of 3,403 Pbr materials in the game.
- **A block is at most `0x800` B** (fatal), **a feedback list at most 8 images** (fatal, "SmallDynamicArray is out of elements").
- **A material can't bring a new texture late.** It looks each image up by `content.hash` (`0xb5580`) in the map the renderer filled at boot, and a texture that isn't there is fatal: "corrupted streamed texture parameters". So a material has to be there when the map is filled.
- State 1 waits for the mip tail of every image. Then the block is copied, the bindless handle of each image is written at its `dataOffset` and the handle of each feedback list at its own, and the block is uploaded to `g_rfxMaterials`. So every texture and feedback slot is 0 in the data, 13,207 of 13,207.

### 5.2 Textures **[dis] [data]**
`RenderInit 0xbe410` runs once at boot. It lists 8 resource types through `ResourcesByType` (`S+0x7d0`, the slot kiln.assets answers): `BuildingMaterialBlendingResource`, `CardClusterResource`, `RenderDecal`, `DetailScatterResource`, `GiVoxelBuildingMaterialResource`, `RenderMaterialResource`, `SimpleWorldMaterialResource`, `WorldMaterialBlending2Resource`. Every `RenderStreamedTexture` in them goes to `0xb2fb0`, deduplicated by `content.hash`.

| Limit | Value | Over it |
|---|---|---|
| Streamed textures | 16,384 slots (`0x1027d6`), slot 0 reserved. The game uses 13,930 distinct ones, so 2,453 are left | Fatal at boot: "too many streamed textures" |
| Mip ranges of one texture | 16 | Fatal |
| An empty `content.hash` | | Fatal: "invalid streamed texture parameters" |
| A format or size the driver refuses | | Fatal: "could not create texture" |
| Mip tail not served | | Fatal: "error loading streamed texture mip tail: %k" |
| Texture feedback lists alive | 4,095 | Handle 0: nothing logged, and the textures stay at their mip tail |

The rules of a texture in a material, all holding for 13,207 of 13,207 images: a full chain, largest first; `content` holds it all; one range per level with more than 4 KiB left, each to the end; the levels no range starts at are the tail, a second blob, the end of the chain, or the chain itself with no ranges; both blobs under their real `ContentHash`. `content.category` 1840640251, `bundleId` the material GUID's first field, `sortKey` the image's index. Tail category 1185060183, `bundleId` 0, `sortKey` its `hash2`. What reads `bundleId` wasn't followed. 9 images aren't a power of two, so the game takes those.

Image order in vanilla: emissive, albedo, normal, params, height, then the second layer, then anisotropy, tint masks, translucency. A feedback list names a layer's images, without anisotropy or translucency.

### 5.3 What the flags pick **[dis] [spv]**
`0xe1390` makes each mesh's `RenderMeshDrawData2.pipelineBits` from the **model's** `RenderModelMaterial`: `effectId | AlphaTested << 8 | DoubleSided << 9 | DisableShadows << 10 | HashedAlpha << 11`. `Culling2_Instanced` reads a table at `pipelineBits`, or'ing in 256 while the instance is dissolving, and takes 6 bits per pass: 0 means not drawn in that pass. So a model must carry the flags of its materials, as 14,339 of 14,339 do. The table's contents are C++ and weren't read: which pipeline a bit means is from the names, `Model_{Visibility,GBuffer,Shadow}[_Discard|_HashedAlpha][_DoubleSided]_GpuCulling2_*`.

`Foliage` is set on 93 % of Pbr materials, weapons and armour included, and no code found reads it. Over the data, `CanDissolve` is `EnableDissolve` and `HashedAlpha` is `EnableHashedAlpha`, without exception; `AlphaTested` comes with `SampleOpacity`, `FlipBackfaceNormals` with `DoubleSided`, `Translucent` with a `translucencyStrength` above 0.

### 5.4 What the shader reads **[spv]**
`fs_main` of `Model_GBuffer_GpuCulling2_Instanced`, for one layer:

| Field | Use |
|---|---|
| `albedoMap` | Read unconditionally. rgb is the colour. The alpha is what `Model_Visibility_Discard_*` tests: `a < 0.5` kills, fixed in the shader |
| `normalMap` | Read unconditionally. RG × 2 − 1, z rebuilt, against the vertex's tangent frame, with bitangent = w × N × T |
| `materialParamsMap` | Read unconditionally. R metallic, G perceptual roughness, B occlusion if `SampleAo`, A reflectance if `constantReflectance` < 0 |
| `emissiveMap` | Only if not 0. rgb × `intensity` × mix(1, scene exposure, `exposureCorrectionFactor`), × the instance's emissive factor |
| `texCoordScaling` | Multiplies UV 0. The sampler is `g_rfxSamplers[0]`, always wrap |
| Vertex colour | `multiplyAlbedoVertexColorChannelSwizzle` 0 multiplies albedo by linear vertex rgb; 255 is none |

There is **no tint in the block**: `albedoTintShaderData` masks tint with colours from the instance (player dyes), and a material has no constant colour, roughness or metallic. All of that comes from textures. So a block's plain values are built into constant 16 × 16 textures, as the game's own constant maps are.

Checked on real GPUs, headless, with the game's own `fs_main` behind a small vertex shader (`tools/vfxcook/tests/material_gpu.py`): Kiln's block and BC7, BC5 and BC1 textures give the expected albedo, roughness, metallic, reflectance, occlusion, normal and emissive in the G-buffer, on an RTX 3080 Ti and an AMD iGPU. **[gpu]**

### 5.5 Walls, floors and terrain
They are not `RenderMaterialResource`. See `docs/re/building.md` §1.4: each surface is a slot in `BuildingMaterialBlendingResource` or `WorldMaterialBlending2Resource`, with shaders of their own. They share `RenderStreamedTexture` and these limits, so what writes a material's textures fits them too, and their textures count against the same 16,384. A `MaterialBlock` doesn't reach them.

## 6. Shaders
### 6.1 GpcResource **[refl] [data]**
74 resources, GUIDs are UUIDv5 of the path (`renderer/renderer.gpc` = `40e6ba42-…`). Loaded by GUID through the resource
service (`GpcOpen 0xab97d0`, type `0x647628d6`), so kiln.assets can override one. **[dis]**

Contents: `vertexFormats`, `descriptorSetLayouts` (named bindings, stages, stride, static samplers), `pipelineLayouts`
(bindless flag, push constant type), `rasterPipelines`, `computePipelines`, `debugCode` (not shipped), `strippedCode` (one
blob). `*Offset` fields: where this GPC's own objects start after imported ones.

Totals: 217 raster, 648 compute, 1,485 SPIR-V modules, 28.66 MB. Largest: `renderer/renderer.gpc` (132 raster, 7.7 MB),
`renderer/gi/gi.gpc` (6 + 156, 7.5 MB).

A shader = `GpcShaderResource{variants[], entryPoint}`, a variant = `{mask, code, strippedCode}`, and `GpcShaderCode` =
`{Range32 range, XxHash64 checksum}`. `GpcVariantFlag`: `Debug`, `Float16`. So the only permutations at this level are debug
and float16 (23 compute, plus a few others): features are separate named pipelines instead (the 132 in `renderer.gpc`).

Modules: SPIR-V 1.5, generator spiregg. Capabilities across GPCs include `ShaderNonUniform`, `RuntimeDescriptorArray`,
`Float16`, `MeshShadingEXT`, `RayQueryKHR`. Extensions `SPV_KHR_float_controls`, `SPV_EXT_descriptor_indexing`,
`SPV_EXT_mesh_shader`, `SPV_KHR_ray_query`. **[spv]**

Lookup is by index: neither the pipeline `hash` values nor the `signatureHash` occur in the exe. **[data]** C++ knows each GPC
by generated index tables, so reordering breaks the game and replacing code does not. `signatureHash` is compared only
against what was loaded before ("[gpc] incompatible gpc signature", a reload path). **[dis]**

### 6.2 ContentHash and checksums **[data] [dis]**
`0xf9b750` = `{size, h[4:8], h[8:12], h[12:16]}` of `0xf99800` (AES-NI) / `0xf999c0` (software). Algorithm:
`tools/kfc/kfc/contenthash.py`.

| Check | Blobs | Result |
|---|---|---|
| VFX modules | 2,921 | 2,921 match |
| GPC code blobs | 57 | 57 match |
| Random other content (691 MB) | 1,495 | 1,495 match |
| Empty input | - | equals the game's empty-hash constants in `0x1a0010` |
| `keen::XxHash64` = XXH3-64 (Python `xxhash`, verification only) | 1,485 modules | 1,485 match |

`docs/re/models.md` says the algorithm "was not studied" and `docs/re/resources.md` that the game never re-hashes content.
That stays true for ordinary content, and not for shaders:

### 6.3 Pipeline streaming **[dis]**
`PipelineStreaming` (created `0xab2af0`; "[graphics] Created pipeline compilation task queue using group '%k' with %k worker
threads"; `Task-Shader00..06` in the log). Job `0xab1350`, helper `0xab39f0`, for each stage:
1. if the request's checksum is non-zero, XXH3-64 of the code must match;
2. if the range starts at 0 and covers the whole blob, `ContentHash` of the code must equal the blob's hash;
3. on failure: log "[pipeline_streaming] Checksum error in shader pipeline code! Please check your installation files for
   corruption!" and return error 9.

Consumers treat errors as fatal: "error streaming vfx pipeline: %k" and "error creating vfx compute pipeline" (`0x1af940`),
"Error compiling gpc pipeline %d/%d of gpc '%s'" (`0xabcd10`). Content arrives via the content reader, so kiln.assets' blob
path applies: the streamer's blob was read through the same `ContentSubmit` **[hyp, strong]**. VFX requests carry `bundleId`
1; what a bundle id changes for a mod blob is **[hyp]**. No SPIR-V validation happens in game code: bad code reaches the
driver.

**Consequence for Kiln:** a mod's SPIR-V must be registered under its real ContentHash. `assets.json` can do that today:
`{"kind":"blob","hash":"<size>-<h0>-<h1>-<h2>","file":…}` goes to `op_blob_override`, which takes any hash whose size
matches the file. The docs call it "override", and its use for a hash the game does not have is unexercised. A GPC override
must give each module a correct checksum, or 0 (which skips the check).

### 6.4 Pipeline creation details **[dis]**
For VFX (`0x1af940`): name = the particle's debug name, group size hard-coded `{32, 1, 1}`, layout the shared one, entry
`cs_main` (string referenced from the VFX block `0x195b40`; all modules use it). The async job calls `0xfd3fb0`, then the
result goes to `0x1029bd0` (a slot in a 0x58-byte pipeline table).

### 6.5 The frame's fixed layouts **[refl] [data] [log]**
Device: robustBufferAccess, robustImageAccess and nullDescriptor enabled; scalar block layout, descriptor indexing, BDA
available; "Vulkan breadcrumbs are disabled". Queues: graphics family 0, compute family 2, transfer family 1. Depth is
`D32_sfloat`, reverse-Z (`Greater`).

| Pass | Targets | Blend, depth | Pipelines (renderer.gpc) |
|---|---|---|---|
| Visibility | `R32_uint`, `R32G32_uint` | depth write, Greater | `Model_Visibility_*`, `Terrain_Visibility_*`… |
| G-buffer | `R8G8B8A8_srgb`, `A2B10G10R10_unorm`, `R8G8B8A8_uint`, `R32_uint`, `R16G16_snorm`, `R8_unorm` | no blend, depth Equal | `Model_GBuffer_*` (6 targets) |
| Shadows | none | | `Model_Shadow_*` |
| Transparent forward | `R16G16B16A16_sfloat`, 3 × `R8_unorm` | premultiplied (`One`, `InvSrcAlpha`), no depth write | `Transparent_Forward[_Merged][_NoFog]…` |
| Transparent OIT | the same + `R16_unorm` | `One, One`; revealage `Zero, InvSrcColor` | `…_Oit_{Unlit_Unshaded,Lit_Unshaded,Lit_Shaded}` (24) |
| Rfx transparent | as forward | no depth write, cull None | `GridRfx_Transparent_GpuCulling2_Instanced` |
| Sky | `R16G16B16A16_sfloat` | additive, depth Equal | `VfxSkyTexture_Forward` |

`Model_GBuffer_GpuCulling2_Instanced`: layout `PbrModelRfxGBufferPipelineLayout` = {`GlobalDescriptorSetLayout`,
`GBufferPassDescriptorSetLayout`, `PbrModelRfxDescriptorSetLayout`, bindless}. Materials reach the fragment shader as
`g_rfxMaterials` (stride 244, indexed by `materialDataIndex`). `vs_main` 48,504 B, `fs_main` 48,092 B. Scene constants:
`RenderGBufferConstants` (1,024 B, named).

`Transparent_Forward`: `TransparentPipelineLayout`, `g_instanceData` (stride 176), `g_constants` (2,320 B), plus lights, fog,
water and probes. `vs_main` 10,064 B, `fs_main` 23,264 B.

### 6.6 What selects a raster pipeline **[refl] [hyp]**
Model draws are GPU-driven: `culling_2.gpc` reads `RenderMeshDrawData2.pipelineBits` and writes per-pipeline indirect
batches; `VfxCullingPassConstants{normalPipelineShift, dissolvePipelineShift}` and
`VfxWriteCulledDrawCommandsPushConstants.pipelineCount` show the shape. Which C++ tables hold the pipeline order was not read.

### 6.7 Caches **[data] [log]**
- `…/AppData/Local/enshrouded/caches/vk_pipeline_cache.bin` (128 MB here). Header: `VKPC`, a u32, version 6, **1287369 (the
  svn build)**, then the Vulkan header with vendor `0x10de`, device `0x2208`. So a game update invalidates it.
- `0x113fe0`: if the cache is incomplete, "Preload all vfx pipeline once to fill the cache" → `0x1a9390` → `0x1aedb0` walks
  every VFX pipeline. Otherwise "Found complete and up to date pipeline cache - skipping vfx preload". The UI strings
  (`compilingShaders`, `shaderCompileProgress`, `shaderCompileCoreCountHint`) belong to this first-run step.
- A mod's module is not in that preload unless it is registered first (it walks the VFX list **[hyp]**). Otherwise it compiles
  on first use, in the background, and enters the same cache **[hyp]**.
- Steam's **"Processing Vulkan shaders"**: `steamapps/shadercache/1203620/fozpipelinesv6/` (`steam_pipeline_cache.foz`,
  `steamapprun_pipeline_cache.*`, `replay_cache.*`). Steam replays recorded pipelines ahead of launch. It is not game code and
  knows nothing of mods. Fossilize records what the game creates, so mod shaders likely land in the local `steamapprun_*` files
  **[hyp]**. Kiln never touches this folder (user rule: click Skip).

### 6.8 Post-processing **[data] [refl]**
All compute, no push constants. `renderer/post/post_processing.gpc`: Histogram, BilateralGrid (Fill, Blur XYZ), Bloom (Down,
Up, TransparentDownsample), Sharpness, FXAA, `Composite_PreUpscale`, `Composite_PostUpscale`. `composite_frame.gpc`:
`CompositeFrame` = {`g_constants` 48 B, `g_scene`, `g_ui`, `g_result`}, 6,672 B of SPIR-V, entry `cs_main_composite_frame`:
the last step, joining scene and UI. FSR3, DLSS and `depth_of_field` (8 raster) sit around them.

Data-only levers: `AmbientPostProcessingResource` and `CharacterScenePostProcessingResource` (saturation, tone, grading per
situation), the LUTs `colorGradingFogRegular/FogDangerous/LowHealth` (`RenderTextureResource`).

`PostProcessingCompositeConstants` (336 B) holds `colorFilter`, `vignetteColor`, `flashColor`, `toneMapping`.

## 7. The server and networking **[data] [dis]**
- The server's KFC has 0 `VfxResource`, `RenderMaterialResource`, `RenderDecal`, `GpcResource`, but 2,319 `NotifierResource`
  and 279 `VfxMeshResource`, and its templates still list `VfxComponent` + `VfxComponentResource`. The program table is shared,
  so program names prove nothing. It has no `[vfx]` strings.
- No "play VFX" message is known. Clients derive effects from replicated entities (created by template GUID, client's own
  copy), `NetworkActor` sequence ids (client plays its copy), buffs (`BuffNetworkData`), and client-side simulation. Whether
  ECS events such as `ecs::NotifyEvent` cross the network is **[hyp: no]** (`docs/re/audio.md` §5).

**A server-triggered VFX:**

| Client | What can show | How |
|---|---|---|
| Vanilla | Only game VFX | The server spawns a vanilla template carrying it, plays a vanilla sequence, applies a vanilla buff |
| Vanilla, sent a new GUID | Must never happen | Unknown template: "Could not lock template resource" (`docs/re/creatures.md`). Unknown VFX GUID: **abort** at `0x1a0200` |
| Kiln with the mod | Anything | The same data routes with the mod's own templates, sequences, buffs, or a Kiln channel |
| Kiln without the mod, `clients: "kiln"` synced definitions | **Danger**: a synced template naming a mod VFX aborts the client unless a guard is in place | A detour on `VfxCreateInstance` that ignores unregistered GUIDs |

## 8. Proof status
| Claim | Evidence |
|---|---|
| VFX resources read and written back byte-identical | `tools/kfc` decode + encode: `VfxResource` 1,575/1,575, `VfxMeshResource` 638/638, `VfxCurveResource` 8/8, `VfxJointCollectionResource` 30/30, `VfxSystemResource` 1/1, `GpcResource` 74/74, `RenderMaterialResource` 3,458/3,458, `RenderTextureResource` 304/304, `RenderDecal` 52/52, `NotifierResource` 2,319/2,319, `AmbientVfxCollectionResource`, `RenderResources`, `RenderSystemResource`, `VoxelMaterialVfxDataResource`, `WaterSurfaceEffectMaterialResource` 2/2, `AmbientPostProcessingResource`, `CharacterScenePostProcessingResource`, `FishingEffectResource`, `MaterialFeedback` 274/274, `MaterialFeedbackCollectionResource` |
| Shader container understood | Module ranges in every GPC extract to valid SPIR-V; checksums match (§6.2); `spirv-dis` output reads as compute/raster code |
| Particle ABI | All 2,921 modules fit `VfxUpdatePipelineLayout`. The instance layouts match reflection |
| A Kiln-compiled module has the same outer shape | A stub compiled locally with `glslc` (GLSL and HLSL) and `glslangValidator`: SPIR-V 1.5, `LocalSize 32 1 1`, `spirv-val --target-env vulkan1.2 --scalar-block-layout` passes, interface inside the layout, HLSL gives entry `cs_main` (GLSL gives `main`, a one-word rename). Not run on a GPU |
| Content hash | §6.2 |
| Kiln's built VFX have the game's shape | `tools/vfxcook/tests`: decoded and re-encoded byte-identical through `tools/kfc`; the rules in `tools/kfc/kfc/vfx.py` pass on 1,575 of 1,575 game VFX, 638 of 638 meshes, 145 of 145 textures, and on what `kiln-vfxcook` writes. Not seen in a game |
| VFX mesh format | Quad and triangle decode exactly; positions fit the stored bounding spheres |
| Kiln's materials have the game's shape | `tools/vfxcook/tests/test_material.py`: all 3,458 game materials read and rewrite byte-identical through the builder's own writer, the rules in `tools/kfc/kfc/material.py` hold for all of them and for what `kiln-vfxcook` writes. The game's G-buffer shader reads them on real GPUs (§5.4). Not seen in a game |
| **In the game** | The game's own VFX played through kiln.vfx, and its guards: §4.5. Kiln's modules and materials: nothing yet. Offscreen on real GPUs: §3.8, §5.4 |

Tests: `python3 -m unittest tools/kfc/tests/test_contenthash.py`.

## 9. Open questions
RE, offline:
- `TransparentInstanceData.flags` bit 8, `PointLightData.flags` bit 0.
- The parameter stream's format for the top modules (level H only).
- `pipelineBits` and the C++ pipeline tables.
- How `VfxResourceDynamic`, `ToggleModifier` and `VfxTargetedComponent` are driven.
- Whether IsActive clears when an instance is stopped, and how `upload_vfx_effect_parameters` fills `VfxBaseEffectParameters`.

In the running game:
- A kiln.assets blob under a Keen hash; `bundleId` on mod blobs; first-play latency; whether re-running the enumeration is safe.
- What the value behind `0x1ad320`'s per-chunk pointer is, which decides when an instance goes.
