# Models and meshes: the KFC4 files, keen::RenderModel, skeletons (for tools/kfc, tools/modelcook)

Client Steam build 26404966 / svn 1285601 (`enshrouded.exe` sha256 `ce6fdba6…`, `enshrouded.kfc` version
`1285601|^/game38/branches/release_1.0|2026-11-03T10:13:01.005543Z`), dedicated server 1285601 (`c5c9fe50…`) for
comparison. Sources:
- EMF 0.3.3's MIT outline of the files (`engine_parts/re_content.md` 1.1);
- Kiln's own reading of the game files with `tools/kfc`, the game's own reflection (read offline from the exe);
- capstone over the model loader;
- Blender renders of decoded models.

kfc-parser's documentation (KFC2 era) was read as hypotheses only; no kfc-parser code was read or used.

Evidence tags:
- **[data]**: checked over the game files, with counts;
- **[refl]**: the game's reflection records;
- **[dis]**: disassembly;
- **[render]**: decoded geometry rendered and inspected;
- **[inf]**: inferred, not verified;
- **[rt]**: seen in game (see "Proof status").

## Short answers
- **Weapon model chain:**
  - ItemInfo `equipment.visualEntity` → a `keen::ecs::TemplateResource` with `ecs::ModelResource{model}` and `ecs::SlotAttachment{HandR}`. ItemInfo `equipment.equipmentEntityModelSetup.model` and `iconModel` hold the same GUID.
  - That GUID names a **`keen::RenderModel`** resource. `keen::Model` is the editor type and is never cooked: 0 resources.
  - The RenderModel's single content blob `renderData` holds every GPU buffer. **[data] [refl]**
- **Vertices:** planar, one stream per attribute, in the order of the format letters. For `PNTCT` (99.4 % of all meshes):
  - P: 8 B, 21-bit unorm x/y/z in the model's quantization box;
  - N, T: 4 B each, 10:10:10:2 snorm;
  - C: 4 B, RGBA8;
  - T: 4 B, UV as 2 × f16.

  Indices are u16, local to their vertex group. **[data] [render]**
- **LODs:** `lods[]` = {first mesh, mesh count, pixel size}. Each LOD has its own index range and usually shares the vertex group of LOD 0. **[data]**
- **Materials:** `materials[]` = {RenderMaterialResource GUID, effect, flags}. A material holds a `PbrRfxRenderMaterial` parameter block (identified by its layout hash) and its textures inline: BC7 albedo, BC5 normal, BC1 parameters. **[data] [refl]**
- **Attachment:**
  - A weapon's origin is its grip. Visual template `SlotAttachment {HandR, FirstValid, identity offset}`.
  - The animation graph maps slot HandR to joint 94 of the player skeleton (`slotBoneIndexMapping`).
  - Blade along +y, guard along z, thickness along x. **[data]**
- **Spaces are left-handed** (x right/east, y up, z forward/north). Both sides convert: tools/kfc exports glTF by mirroring x, and modelcook cooks back the same way. **[data]**
- **Skinned armor:**
  - Uses ItemInfo `equipment.visualModel` (a RenderModel GUID directly), plus `hideMask` (body parts) and `blendShapeMask`.
  - The RenderModel names the player skeleton (`hierarchy`). It carries 4 × u8 joint indices + 4 × u8 weights per vertex, a joint map into the skeleton, blend shapes and cloth. **[data]**
- **The format is proven offline, both ways:**
  - The decoder/encoder re-encodes **91,904 of 91,909** game resources byte for byte (all 146 types).
  - modelcook re-cooks the vanilla Bronze Sword from its glTF export. LOD 0 indices come back identical, positions within 1 quantum (0.6 µm), UVs and colours identical. **[data]**
- **In game, it renders** (client 1285601):
  - kiln.assets served ModelSample's cooked RenderModel and its renderData blob;
  - the held Bronze Sword drew the cube-blade sword with its spiral of small cubes;
  - no model loader errors **[rt]**.

  Still open: the handedness marker read upright, the icon, and crafting the new item (section 6).

## 1. KFC4 files **[data]**
Three kinds of files:
- `enshrouded.kfc` (9.4 MB): the directory;
- `enshrouded.kfc_resources` (215 MB): every resource, zstd compressed;
- `enshrouded_000..063.dat`: content blobs. 64 containers, a power of two; 055–063 are empty.

The server's are `enshrouded_server.*`: 10 containers, 43,958 resources, 85 types. The server has no RenderModel and
no RenderMaterialResource: models are client-only.

**Header.** Little-endian. A location is {u32 offset relative to the location field itself, u32 count}.

| Offset | Content |
|---|---|
| 0x00 | `"KFC4"`, u32 end of the tables (0x8f5e08), u32 12, u32 0 |
| 0x10 | version string (65 bytes, no NUL) |
| 0x18 | containers {u64 size, u64 content count} × 64 |
| 0x20, 0x28 | empty |
| 0x30 | resource stream (one 20-byte entry) {u32 decompressed size 482,311,432, u32 compressed size 214,937,276 (= sum of the chunks' compressed sizes), u32 resource count 91,909, u32 0, u32 chunk count 59} |
| 0x38 | resource order: u32 × 91,909, indices into the key list; bundles slice this list |
| 0x40/0x48/0x50 | content map: buckets {u32 first, u32 count} × 131,072; keys `ContentHash {u32 size, h0, h1, h2}` × 101,492; values {u32 offset, u16 flags 0, u16 container, u64 0} |
| 0x58/0x60/0x68 | resource map: buckets × 131,072; keys {guid[16], u32 type hash, u32 part, u64 0} × 91,909; values {u32 offset in the decompressed stream, u32 size} |
| 0x70/0x78/0x80 | bundles: buckets × 256; keys u32 type hash × 146; values {u32 **type layout hash**, u32 first, u32 count} |
| 0x88 | resource chunks {u32 file offset, u32 stored size (4 KiB aligned), u32 compressed size, u32 decompressed offset, u32 decompressed size} × 59: zstd frames of 8 MiB (the last real one 4.0 MiB, then an empty 9-byte frame) |

Notes:
- Keys and values are parallel arrays, so a reader needs no bucket hash. tools/kfc builds its own dictionaries.
- Resources in the decompressed stream are 16-byte aligned and may cross chunk borders.
- Content offsets are 4 KiB aligned. A content blob's size is the first u32 of its hash.
- GUID bytes are in `keen::Guid` memory order (.NET `Guid.ToByteArray`). Bronze Sword `1d622bd9-8170-41b1-822e-d3984b386d95` = `d92b621d7081b141822ed3984b386d95`, and FNV-1a over those bytes = 0x43108313 = its ItemId.
- The bundle value's first u32 equals the type record's `+0x54` (layout hash) for all 146 client and 85 server bundles. tools/kfc `info` checks this, so a reflection/data mismatch shows at once.
- Mod blobs: kiln.assets keys them as `{size, sha256[0:4], [4:8], [8:12]}`. The game never re-hashes ordinary content at run time. Shader code is the exception: Keen's content hash is solved in `docs/re/vfx.md` §6, for what needs it.

## 2. Reflection and the blob layout **[refl] [data]**
The records `content/TypeByHash` returns at run time are static data in `.data`/`.rdata`, and their pointers are
preferred-base VAs. So `tools/kfc/kfc/refl.py` reads them from the exe file. It scans 8-aligned positions whose
`+0x20/+0x28` name hashes (FNV-1a) to `+0x50`. That gives 12,658 records on the client, with no unresolved field type.

Record layout (`docs/re/settings.md`) plus:
- `+0x44` u16 alignment;
- `+0x54` u32 layout hash (= the .kfc bundle value);
- `+0x4C` upper bits are flags (e.g. 0x412, 0xe12, 0x218).

A struct's `inner` is its **base**:
- its own fields do not repeat the base's;
- offsets are absolute;
- base fields come first.

1,345 structs have a base with fields, e.g. `AimSnapModifierSettings : OptionalCameraModifierSettings{enabled}`.
`keen::Vector3`'s inner `float3` has the same fields at the same offsets, a harmless alias.

**Serialized resources** are the root struct, then its out-of-line data. Every rule below is confirmed by decoding
and re-encoding every resource of the client KFC: 91,904 of 91,909 come back byte-identical. The 5 others are large
arrays the decoder caps.
- **Order and alignment:** out-of-line data is appended in field order, depth first. A `BlobArray` reserves its whole element block, then the elements' own data follows. Each block is aligned to its element type's alignment.
- **Self-relative offsets:**
  - `BlobArray {i32 rel, u32 count}`, `BlobString {i32 rel, u32 len}`, `BlobOptional {i32 rel}` (0 = none): `rel` counts from the field itself.
  - `BlobVariant {u32 type hash, i32 rel, u32 size}`: `rel` counts from the `rel` field (header + 4). `size` is the variant struct's own size, without its out-of-line data (the case kfc-parser's notes leave open).
- **No padding** at the end: a resource ends with its last data byte.
- **Values:**
  - Bitmask values are bit indices of the flag enum (155 of 155 bitmask enums);
  - `object_reference` and `guid` are 16 bytes;
  - `ContentHash` is the struct {size, hash0..2};
  - `ResourceContentReference` = {ContentHash hash, HashKey32 category, u32 bundleId, u64 sortKey}.

## 3. keen::RenderModel (352 bytes, layout hash 0x0a4db420; 10,470 resources) **[refl] [data]**
| Offset | Field | Notes |
|---|---|---|
| 0x00 | `debugName` string | `wpn_sword_01` |
| 0x10 | `boundingBox` AxisAlignedBox {Vector3 min, max} | exact bounds of all vertices (cooker checked: equal to the decoded positions) |
| 0x30 | `boundingSphere` Sphere | conservative; the game's algorithm is not the box centre (sword: centre y 0.248, box centre 0.384) |
| 0x40/0x4c | `positionScale`, `positionOffset` float3 | the quantization box: offset = min − m, scale = size + 2m with m = 0.2 × the largest half extent (10,390 of 10,390 quantized models, relative error < 1e-5) |
| 0x58 | `wholeModelRadius` | the largest vertex distance from the origin (sword 0.941778 = its tip; the cooker reproduces it exactly) |
| 0x5c | `materials` BlobArray<RenderModelMaterial 20 B> | {ObjectReference<RenderMaterialResource>, RenderEffectId u8 (Pbr = 1), RenderMaterialFlagMask u8} |
| 0x64 | `meshlets` BlobArray<RenderModelMeshlet 20 B> | empty in all 10,470 models (meshletData is always 0 bytes) |
| 0x6c | `meshes` BlobArray<RenderModelMesh 56 B> | below |
| 0x74 | `lods` BlobArray<RenderModelLod 12 B> | {meshOffset, meshCount, f32 pixelSize}; 1–8 LODs (346 models have 1); LOD 0 has pixelSize 0 |
| 0x7c | `foliageAnimationShaderData` BlobOptional | |
| 0x80 | `hierarchy` ObjectReference<ModelHierarchyResource> | skeleton (null for rigid models) |
| 0x90 | `blendShapes` BlobArray<RenderBlendResourceData {BlendShapeId ref}> | 0x98 `blendShapeMask` u32 |
| 0x9c | `globalScale` f32 | 1.0 on 10,050 models; what a skeleton was cooked at (section 5). Only cloth colliders use it |
| 0xa0 | `hasCardClusterLod` bool | loader then also opens 2 `CardClusterResource`s **[dis]** |
| 0xa4 / 0xac | `sdf`, `skinnedTree` | empty for weapons |
| 0xd4 | `skinningMap` BlobArray<u16> | model joint → skeleton joint |
| 0xdc / 0xe0 / 0xe4 / 0x100 | `cloth`, `chain`, `collider`, `sleeves` | cloth: `RenderModelClothData` (NvCloth meshes) |
| 0x108 / 0x10a | `jointCount`, `hierarchyJointCount` u16 | wand 2/3, armor chest 22/102 |
| 0x110 | `renderData` ResourceContentReference | the GPU blob; `category` 1497973154 in all 10,470, bundleId 0, sortKey 0 |
| 0x130..0x150 | `indexData`, `meshletData`, `vertexData`, `skinningData`, `blendShapeData` Range32 {u32 start, u32 bytes} | byte ranges in renderData |

`RenderModelMesh` (56 B):
- `boundingSphere`;
- `materialIndex`, `vertexCount`;
- `indexOffset`, `indexCount`, counted in indices;
- `meshletOffset`, `meshletCount`;
- `vertexDataByteOffset`: the start of the vertex group in vertexData;
- `blendShapeByteOffset`;
- `skinVertexOffset`: the vertex index of the group in the skinning stream, the cumulative vertex count of the previous groups;
- `vertexFormat` u8: P 0, PC 1, PCT 2, PNT 3, PNTCT 4, Vfx 5;
- `vertexStride` u8;
- `clothMeshIndex` u8;
- `positionQuantization` u8: none 0, x21y21z21_unorm 1.

Survey of all meshes:

| Format | Stride | Quantization | Meshes |
|---|---|---|---|
| PNTCT | 24 | x21y21z21 | 76,627 (16,419 of them skinned) |
| P | 8 | none | 425 |
| PCT | 16 | none | 48 |

PNT and PC do not occur.

**renderData layout:**
- Order: vertexData at 0, then skinningData, then blendShapeData, then indexData. Ranges are 4-byte aligned. The blob ends with the last range.
- Empty ranges: blendShapeData and, on rigid models, skinningData are {0, 0}. meshletData is {the end of indexData rounded up to 4, 0}, so on a blob of odd length it starts past the end.
- The loader (`0xe0070` on the client, "[model_loader] could not create gpu vertex data of model '%k'!" …) handles each non-empty range the same way. It allocates GPU memory of `count` bytes ("could not allocate %d bytes of mesh data"). It then queues a content read of `renderData` limited to {start, count}, and the bytes are uploaded as they are. **[dis]**
- So the formats below are what the shaders read.
- The loader also checks:
  - `lods` must not be empty: state 0x16, the model is not loaded;
  - every material is acquired through the material system (`0xcf470`); a failure stops the load.

### Vertex streams **[data] [render]**
A vertex group starts at `vertexData.start + vertexDataByteOffset` and has `vertexCount` vertices. Its streams follow
one another, each holding `vertexCount` elements. For PNTCT:

| Stream | Bytes | Encoding |
|---|---|---|
| position | 8 | `lo` = u32 at +0, `hi` = u32 at +4: x = `lo >> 11`; y = `(lo & 0x7FF) << 10 \| (hi >> 21) & 0x3FF`; z = `hi & 0x1FFFFF`; bit 63 unused (0 in every vertex). p = offset + q / (2^21 − 1) × scale |
| normal | 4 | 10:10:10:2 snorm: x bits 0–9, y 10–19, z 20–29 (two's complement, / 511), w bits 30–31 = 01 (+1) |
| tangent | 4 | same; w = handedness (+1 = 01, −1 = 11) |
| colour | 4 | RGBA8 unorm (0xffffffff on weapons) |
| texcoord | 4 | u, v as float16; origin top-left (the albedo maps onto the sword correctly without a flip) |

How the encodings were checked:
- The 21-bit split was found by searching every bit offset. Only this layout puts the decoded positions exactly on the stored bounding box: x 0.3854..0.6146, y 0.0833..0.9167 and z 0.21..0.79 of the box, symmetric as the model is.
- Decoded normals have length 1.0000 ± 0.0005 as snorm; unorm gives 1.14 ± 0.18.
- For quantization `none` (formats P, PCT) the position is 4 × f16 (x, y, z, 1). PCT is P, C, T(2 × f16).

**Indices** are u16 in all 10,470 models: indexData bytes = 2 × the sum of indexCount. They are local to the mesh's group: per mesh, min 0 and max < vertexCount. Triangle lists.

**Winding / handedness:** the component cross product (b − a) × (c − a) points against the stored normals for 100 % of the triangles of every model checked. That is a left-handed frame with counter-clockwise front faces, or a right-handed one with clockwise faces. Two independent facts decide it:
1. The player skeleton (`87b29df6…`, 102 joints, bind pose from `defaultPoseJoints`; `skinToJointMatrices` agree) has eye, brow and jaw joints at z +0.09..+0.12 m and hair joints at z −0.09..−0.16 m: it faces +z. The joint that slot `HandR` maps to is at x +0.54 and HandL at −0.54: right = +x, up = +y, forward = +z, i.e. left-handed.
2. The world map has x east and z north (the Interactive Map's port of the game's map, pixel y = 10240 − z): left-handed.

Exporting to glTF (right-handed, front +Z):
- mirror x on positions, normals and tangents, and flip tangent w;
- keep the index order.

The triangles then wind counter-clockwise around their normals, and characters still face +Z.

### LODs and groups
- **Bronze Sword:** 8 LODs over one vertex group of 374 vertices: 536, 348, 226, 146, 94, 60, 38, 24 triangles; pixelSize 0, 0.0104, 0.0166, 0.0273, 0.0410, 0.0614, 0.0922, 0.1382.
- **Multi-material models** (e.g. `global_trophies_lizard_boss_t4`): one group per material, and each LOD lists one mesh per material.
- **Selection:** pixelSize is the threshold for each LOD. That the engine uses the projected size against it is inferred **[inf]**; a single-LOD model always draws LOD 0.

### Materials **[refl] [data]**
`keen::RenderMaterialResource` (40 B; 3,458 resources):
- `effectId`, `flags`;
- `data`: a BlobArray<u8> parameter block;
- `dataTypeSignature` = the layout hash of the block's type. 0x1220f31f = `keen::PbrRfxRenderMaterial` (244 B) in 3,403 materials, plus TerraformingCursor 48, SkyTexture 7;
- `images` {u32 `dataOffset` = the texture slot in the block, `RenderStreamedTexture` inline: size, mip count, `PixelFormat`, content reference, mip-tail content, per-mip `mipChainContentRanges`};
- `textureFeedbacks`, `debugName`.

The Bronze Sword's material `71165a08…`:
- `albedoMap[0]` at +20: BC7 sRGB 2048² (12 mips);
- `normalMap[0]` at +28: BC5;
- `materialParamsMap[0]` at +44: BC1;
- `constantReflectance` 0.503.

The albedo decoded with Pillow (DDS wrapper, mip 2): steel and a leather grip wrap. **[render]**

Material flags: 2,104 of 3,000 sampled models carry flag bit 6, which reflection names `Foliage`, the sword included. Either the name lies or the bit means more than foliage **[open]**. The cooker copies the base material's flags.

## 4. Attachment to the hand **[data] [refl]**
- **The visual template.** `Visuals_Sword_01_Grassland01` (`fc562683…`) holds these components: `DoNotSave`, `Hide`, `ModelComponent`, `ModelResource{model 9c38c7f0…}`, `RenderTransform` (identity), `SlotAttachment{offset identity, attachmentTarget FirstValid, attachmentSlot HandR}`, `VisualEquipment`.
- **The model's origin is placed in the HandR slot frame.** The sword's bounding box runs from y −0.17 (pommel) to +0.94 (tip): the hand holds the origin.
- **Slots to joints.** `keen::AttachmentSlot` has 36 values (Root 0, Head 1, …, HandR 8, HandL 9, …, HandPalmR 14, Generic00..19). `AnimationGraphResource2_0.slotBoneIndexMapping` (StaticArray<AnimationJointIndex, 36>) maps each slot to a joint of the graph's hierarchy. For the player skeleton `87b29df6…`: HandR → 94, HandL → 64, HandPalmR → 74, Head → 17, Pelvis → 1.
- **At run time** the slot transforms live in `ecs::AnimationGraphSlotComponent.localSlots[36]` (float3x4). That the visual entity follows `localSlots[HandR]` with its `SlotAttachment.offset` is inferred **[inf]**.
- **Bind pose** of joint 94: (0.54, 0.933, −0.103) m.
- **Item-level offsets.** `ItemInfo.equipment.equipmentEntityModelSetup` = {model, animation, `ecs::Offset {localOffset, worldOffset, orientationOffset}`, isSet}; the Bronze Sword's offset is identity. The icon uses `iconModel`, `iconScene` and `iconRenderOffset`.
- **For custom weapons:** grip at the origin, blade along +y, guard along z, thickness along x. Vanilla one-handed sword: x ±0.033, y −0.17..0.94, z ±0.15.

## 5. Skinned models (armor) **[data]**
`Armor_T1_Mage_Wizard_DPS_Chest` (`3f94668d…`):
- **Item fields:** `equipment.visualModel` = `b83610ec…` (`armor_m_ear_garments_01_chest`, also `iconModel`), `equipment.hideMask` [BodyPart_Chest, Waist, UpperArms, LowerArms], `blendShapeMask` [NoArmor]. It has no visual template and no equipment model setup.
- **The model:**
  - 7 LODs × 2 materials;
  - `hierarchy` = the player skeleton `87b29df6…` (102 joints);
  - `jointCount` 22, `skinningMap` 22 × u16 skeleton joint indices (1, 2, 3, 6, 7, 10, …);
  - `skinVertexOffset` 0 / 189 per group;
  - one blend shape (`BlendShapeId 3ec83c3b…`, mask 1; blendShapeData 11,694 B; `blendShapeByteOffset` per mesh);
  - cloth on the 189-vertex group (`clothMeshIndex` 1; `RenderModelClothData` with NvCloth parameters).
- **Skinning stream** (8 B per vertex, 1,342 skinned models): 4 × u8 joint indices into `skinningMap`, then 4 × u8 weights.
  - Unused slots have weight 0. Over all 1,342 skinned models and 5,857,527 vertices the weights sum to 255 and descend, and no used slot follows an unused one. An unused slot's index is 0 in 99.8 % of cases; the others keep the index of a weight that rounded to 0.
  - Groups index it from `skinVertexOffset`.
  - Check on the chest: map each vertex's heaviest index through `skinningMap` to a skeleton joint. The joint lies
    0.18 m from the vertex (median; 95 % within 0.28 m). Reading the index directly as a skeleton joint gives 0.98 m,
    and a random joint 0.62 m. So the indices go through `skinningMap`.
  - Exported as a skinned glTF with `kfc.py model <guid> --gltf F --skin` (102 joints, bind pose from
    `defaultPoseJoints`, inverse binds from the same chain) and imported into Blender:
    - the bind pose deforms the mesh by at most 1 µm, so the joint transforms and the mesh's bind space agree;
    - rotating the HandR chain's upper arm by 60° bends the robe's sleeve smoothly **[render]**;
    - the robe faces the front camera (glTF +Z), and the HandR arm is on the character's right: a second check of the
      handedness conversion.
- **`skinningMap`** is ascending in 1,287 of 1,342 models and carries exactly the joints with weight in 1,341. Of the 55 others, 52 have chains: their trailing entries are 0, and vertices use them, so a model joint there means more than its map entry (presumably the chain simulation moves it). The other 3: two head armors with 0 entries like that, and a turkey with one joint listed last.
- **Equal weights:** the order among them has no rule by index (19,445 ascending, 20,387 descending). It doesn't change what's drawn.
- **LODs on skinned models:** 1,315 of 1,342 share one vertex group between LODs. 101 drop a material in a coarser LOD. At most 8 LODs, 12 materials, 30,861 vertices in a group, 163 model joints, 172 skeleton joints.
- **How the client poses it [dis]** (client svn 1287369, RVAs):
  - `0x119993` runs over a pose's model instances. It reads the model's `hierarchy` and goes on only if those 16 bytes equal the GUID of the pose's skeleton (`[rdx]`, `[rdx+8]`), so a model on another GUID gets no joints. `jointCount` 0 also skips.
  - The skeleton's `jointCount` sizes a scratch array of 64-byte matrices. It grows by a callback; a failed grow logs `[render_system] out of skin matrix scratch memory`. So there's no fixed joint limit here.
  - `0x171b20` chains the local pose to globals through `jointParentIndices`. `0x1716f0` multiplies each by its `skinToJointMatrices` entry (`[hierarchy+0x28]`). So **`skinToJointMatrices` is the inverse bind the client uses**, on every skeleton.
  - Then per `skinningMap` entry it copies `matrices[entry]` as 3x4 (48 B per joint) into a ring buffer of `jointCount` entries (`0x1716b0`).
  - The joint limit in the format is the stream's u8 index: 256 model joints.
- **The bind pose isn't always the default pose [data].** On 248 of 257 skeletons `defaultPoseJoints` chained is the inverse of `skinToJointMatrices` within 0.001. One more is off by 0.027. On 8 (wands `beccb0be`, a watering can, a villager, rabbits, capybaras, a test cube) some joints differ by a half turn or a scale, and weights do sit on them (50 models). Since the client multiplies by `skinToJointMatrices`, clips must take those joints away from their default pose **[inf]**. So a bind is checked against `skinToJointMatrices`, and `kfc.py model --skin` writes its rest pose from it on these.
- **`globalScale` [data] [dis].** 420 models have one other than 1.0, 152 of them skinned, from 0.01 to 100. It's a value of the skeleton: `keen::ModelHierarchyData.globalScale` in the source type, and none of the 249 skeletons with models has two values. It's already in the stored vertices and joints: a Fogger at 1.15 stands 2.45 m, a Scavenger at 1.1 stands 2.12 m, and vertices at any value agree with their joints without it. At run time the model system reads it in `0xe6dee`, `0xe74ea`, `0xe93d0`, `0xe9ace`, `0xeccdd` and `0xedfff`, which pass it to `0xe8770`, `0xec170` and `0xebf80`. Those multiply the centres and radii of the cloth collider spheres and capsules by it (`collider` at `+0xe4`). 31 other functions read a float at this offset and weren't traced; they sit outside the model system's code. So as far as read it matters only with cloth colliders, and the cooker writes what the skeleton's other models have, for fidelity.
- **Materials:** no skinning-specific flag: 245 materials are used by both rigid and skinned models. All skinned models use effect `Pbr`. The loader keeps material slots in a growing array (`0xe0fbd`); a material that fails to resolve fails the load.
- **`ModelHierarchyResource`** (72 B; 257 client, 246 server):
  - `hierarchyId`, `jointNames` (u32 hashes);
  - `jointParentIndices` (u16, 0xffff = root);
  - `unbendableJoints`;
  - `skinToJointMatrices` (Matrix43: inverse bind; the joint origin solves Rᵀx + p = 0 and matches the chained pose);
  - `jointCount`, `defaultPose`, `defaultPoseJoints` ({quaternion, position, scale}, parent-relative).
- **Joint names:** the hashes are not FNV-1a or CRC-32 (plain or lower-case) of any of 555,684 strings in the resources or 93,953 in the exe. The names are stripped **[open]**.
- **Skinned weapons exist** (`wpn_wand_t4_01`: 2 joints of a 3-joint hierarchy `beccb0be…`).

## 6. Proof status
| Claim | Evidence |
|---|---|
| KFC4 directory, chunks, contents | tools/kfc reads all 91,909 resources and every content blob it was asked for; synthetic-fixture tests |
| The bytes the game keeps in memory are the KFC bytes | asset-layer's lab (server 1285601): kiln.assets `resource_read` of the Bronze Sword's `keen::ItemInfo` (5,409 B) is byte-identical to `kfc.py get` of the same resource from the server KFC **[rt]** |
| Reflection from the exe; blob layout rules | 12,658 records; 146/146 layout hashes match the .kfc; 91,904 / 91,909 resources re-encode byte-exact |
| Vertex / index format | positions on the bounding box exactly (3 models), unit normals, correct texture mapping in Blender (sword + albedo), u16 index ranges in all 10,470 models |
| Handedness | skeleton facing + HandR side; map axes; 100 % consistent winding |
| Cooker | vanilla sword re-cooked: LOD0 indices identical, positions ≤ 1 quantum, normals 4/1496 bytes and tangents 5/1496 bytes differ (rounding), UVs/colours identical, wholeModelRadius identical; cube sword: decode → re-encode byte-exact, round trip error 0.36 µm |
| Cook recipes | `assets/cook.json` run by `modelcook.py recipe`: a synthetic-game test cooks override, new model and visual template; ModelSample's recipe cooks against the real game with every check passing |
| Hand attachment | template components; slotBoneIndexMapping; bind pose |
| Skin stream writer | `tools/modelcook/tests/game_skin_check.py stream`: the 1,290 skinned models without chains, 5,367,310 vertices, through the writer. Every vertex keeps its joints and weights. 1,063 models byte-identical; in the others the bytes differ in unused slots (29,234 keeping an old index) and in 5 models whose map repeats or leaves out an entry |
| Skinned cooker on game models | `game_skin_check.py cook`: all 1,342 skinned models exported to glTF and cooked (5.85 M vertices). Indices identical in all; same ranges; same `globalScale`; positions within 1.42 quanta (float32 in the glTF), normals dot ≥ 0.999998. The 1,285 with a clip, posed by their largest clip: at most 0.021 mm from the game's model (dragon boss, moving 62 m), and 0.019 mm on the 152 with `globalScale` other than 1. Same map in 1,280 of the 1,284 without chains. 6 models trip a check for reasons in the data: 3 have a vertex with a zero normal, and 3 have a quantum (5-76 nm) below float32 precision. Cloth, chains and blend shapes are left out, so this says nothing about those |
| Skinning math against an independent evaluator | `tools/modelcook/tests/run_blender_check.py`: a creature authored in Blender (armature object moved, turned and scaled; mesh object with its own transform; smooth weights of 4 influences), exported by Blender, cooked, posed with the client's math: 0.01 mm from Blender's armature modifier at rest, at most 3.05 mm in poses that move 1 m (weights are in 255ths) |
| Skinning against the glTF | `tools/modelcook/tests/test_skin.py`: the sample figure posed by its cooked clips against the glTF per its specification: 0.6 µm at rest, at most 2.4 mm in a pose |
| Inverse binds on a running engine | `mods/tests/skincheck`, dedicated server svn 1287369, no player, 9 PASS: the figure's skeleton, clip and graph run in the engine, slots HandR, Head and Pelvis within 0.0005 of the cooked clip's joints, and model vertices skinned by those joints within 0.26 mm of the cook's numbers **[rt]**. The engine's joint and the client's math agree on this; the client's GPU skinning itself wasn't run |
| **Skinned in game** | **Not proven.** Nothing skinned that was cooked here has been drawn by a client. The manual checks are in `docs/kiln/modding/models.md` |
| **In game** | **renders** (client). Boot: "modelsample: icons 0, textures 0, resources 3, blobs 2, errors 0" and "content map rebuilt: 101492 game blobs + N Kiln entries". At world load: no `[model_loader]` errors. The held Bronze Sword (override of `9c38c7f0`) draws the cooked cube-blade sword; the screenshot shows the spiral of small cubes and the box grip at the right hand **[rt]**. Not checked yet: the chirality marker read upright (the blade hung down, seen along its axis); whether the icon re-renders from the new model (the hotbar icons looked like thin vanilla blades, but it is not known whether those items were Bronze Swords: open); crafting `Kiln_Sample_TowerSword` (registered with its recipe, model and template per the log; the crafting UI lists "2542 + 7 new" recipes) |

Renders (local, never committed): `export/bronze_sword.lod0.gltf` (vanilla, decoded),
`export/cooked_cube_sword.gltf` (decoded back from modelcook's output). Headless Blender (`blender -b`, Workbench) with the
decoded albedo.

## 7. What skinned armor needs
Creature bodies are cooked today (docs/kiln/modding/models.md, Skinned models). Armor adds the following.

1. **Skeleton binding.** Done: cook against the player skeleton GUID (`87b29df6…`) by joint index. `kfc.py model <armor> --gltf F --skin` writes joints `j<index>` (the name hash is in `extras`) and the skin, artists weight to those bones, and modelcook maps the glTF joints to skeleton indices.
2. **Body hiding and blend shapes.**
   - `hideMask` decides which body parts the armor hides. Copy it from the base armor item.
   - `blendShapeMask` and per-model blend shapes adapt the armor to body shapes (data 11,694 B on the chest; format not decoded). First target: an armor model without blend shapes. Test whether the game accepts `blendShapes` = [] for the item's mask.
3. **Cloth.** NvCloth data (`RenderModelClothData`, `RenderClothMeshResource`): leave it out, since `clothMeshIndex` 0 and `cloth` null are valid on most models.
4. **Gender / body variants.** `armor_m_*` names suggest per-body models; find where the female variant is chosen (`EquipmentModel {BlendShapeMask mask, model}` lists, `CharacterPresetCollection`).
5. **Animations.**
   - `keen::JointAnimation` (5,461) = {u64 hierarchyHash, position/scale factors, headers, compressed `vf32_data`, fps, frameCount, jointCount}.
   - Plus `AnimationSequenceContainer`, `LveAnimation`, `RootMotionAnimation` and the anim graph (`AnimationGraphResource2_0`, 279).
   - New weapon animations would need the codec (headers + packed data), then sequences. That is research on its own; rigid weapons reuse the base weapon type's animations, which kitbash already chooses through `weaponCategoryReference` / the template.
6. **Validation route.** Done for geometry and skin, over every skinned model (section 6); the client test is open.

## 8. Open questions
- In game:
  - the chirality marker of the cube sword, read with the blade upright (spiral strands nearest the camera rise "/"
    when the conversion is right);
  - whether weapon icons re-render from an overridden model. Weapon icons are drawn from `iconModel` through the
    item icon scene (render to texture). If that render runs, or is cached, before the override's renderData is
    streamed, the icon keeps the vanilla shape while the held model is new (asset-layer's documented upload limit)
    **[inf]**. Check: look at the Bronze Sword's inventory icon on a save past the intro;
  - the new item `Kiln_Sample_TowerSword`: craft it, hold it, read its icon, and check ModelSample's three PASS
    lines.

  Settled there: the streamer reads a kiln.assets blob for renderData.
- `boundingSphere` algorithm (the cooker writes a conservative sphere); meshlets (never cooked: `createMeshlets` exists as a converter option).
- Material flag bit 6 ("Foliage") on weapons; `RenderModelMaterial.materialFlags` = the material's own flags (sword: equal).
- Joint name hash; blend shape data; cloth data (section 7).
- How the renderer culls a skinned mesh animated outside its stored `boundingBox`, which is the exact bind-pose bounds on the game's own models.
- Pickup models (an item lying on the ground): which reference the pickup entity draws (`overridePickupEntity` is null on weapons).
