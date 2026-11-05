# NPC internals: bodies, animation, perception, behaviour (for Kiln's building blocks)

Client Steam build 26430625 / svn 1287369 (`enshrouded.exe` sha256 `5d9ef2f1…`, `enshrouded.kfc` version
`1287369|^/game38/branches/release_1.0|2026-11-04T11:17:54Z`), dedicated server Steam 26430253 (`f0d824be…`). RVAs are
**server** RVAs (image base `0x140000000`) unless the hook map gives both. Sources:
- the game's own reflection, read offline from the exe with `tools/kfc` (12,658 types);
- every clip, skeleton, graph, sequence, arsenal and creature template of the client KFC;
- the ECS program descriptors parsed from both exes (reads, writes, flags: 652 server, 861 client);
- Ghidra 12.1.2 headless decompiles without auto-analysis, capstone, function bounds from `.pdata` with chained unwind
  fragments merged;
- a windowless lab server (no player, no client).

This is a map for building and hooking, not a list of what the game allows. Game code is a hook target. Background: `docs/re/creatures.md`, `models.md`,
`abilities.md`. No kfc-parser code was read. No game data is in the repo.

Evidence tags:
- **[refl]** the game's reflection records;
- **[data]** checked over the game files, with counts;
- **[dis]** decompile or disassembly;
- **[rt]** seen in the running lab, **dedicated server only, no player, no client**. Nothing here was seen drawn;
- **[hyp]** hypothesis, not verified.

## Short answers
- **Our own files run in the game's animation engine [rt].** A skeleton, two clips and a state-machine graph written from
  nothing by `tools/kfc` (`anim.py`, `animgraph.py`), served by kiln.assets, loaded and played on the server. The
  server's slot transform followed the cooked curve: ±29.99° of the authored ±30°, −89.94° of the authored −90°, 0.18 mm from
  forward kinematics. A sequence switched state with a state id that exists nowhere in the game. So the compile-time
  converter is the right design and the formats below are enough to write it. What is still unseen is the client drawing it.
- **The clip codec is solved.** `keen::JointAnimation` is a segmented cubic Hermite spline with per-track key masks.
  `tools/kfc/kfc/anim.py` parses and rebuilds **5,461 of 5,461 clips byte-identical** and encodes dense pose streams with
  error bounds. Skeletons, root motion and graphs re-encode byte-identical too (section 7). **[data]**
- **The graph is data, not code.** 279 graphs, 20,514 nodes, 48 node kinds in use, node ids free. A new graph from
  scratch works **[rt]**. A custom graph talks to the engine through a small input contract (section 2.5).
- **A new skeleton is fine; a body need not fit an existing one.** Nothing validates `hierarchyHash` against the graph at
  run time (43 of the clips the game's graphs use match no skeleton **[data]**, and ours ran). Limit: 255 joints.
- **Skinned bodies are one gap away.** `tools/modelcook` lacks glTF skins, the skin stream and `skinningMap`; the format
  is documented (section 1.1). Blend shapes, cloth and chains are optional and can come later.
- **Creature collision is simple shapes on the root**, 1 or 11 sets per `ColliderMode`, no per-bone hit zones, no ragdoll in
  any template. Weak spots are child entities on slots. **[data]**
- **AI is a fixed FSM plus two layers of data, and every decision has a clean choke point.** All 53 hook targets are found
  by a masked prologue signature, 16-32 bytes for all but one, on 3 server builds (svn 1273043, 1285601, 1287369) and on the client (section 3.5).
- **Modder-defined rules fit inside the game's own data [rt].** The arsenal's constraints and actions are `BlobVariant`s
  dispatched on a type hash, and an unknown hash is harmless. With two detours, a rule using Kiln's own constraint and action types
  ran from the game's own arsenal resource: `constraint false: evaluated 18 times, action 0`, `constraint true: evaluated 10 times, action ran 10`.
  This changes what a `BehaviourBlock` should be: **see section 4.3.**
- **Attack choice can be replaced by rewriting one output [rt].** A detour on `SelectAttack` set the index, and the Vukah played that
  attack's sequence.
- **A one-byte per-entity switch turns the enemy AI off and leaves the body running [rt]**: `DynamicEnemy01BehaviorState+0x498`.
  No hook needed. Then C# drives through `ActorInput` and sequences, 3.8 µs per entity per step before caching.
- **A puppet needs only 12 components, an AI enemy 39, a villager 33 [rt, server].** The rest of a template is client
  presentation, loot, scaling, sensors.
- **Custom bodies need Kiln on every client; custom minds do not.** Template, graph, clips, skeleton, model and sequences must be on the client at boot (`clients: "mod"`).
  Arsenal data, AI overrides and hooks stay on the server, so on a vanilla body vanilla clients just see the result.

## 1. BodyBlock

### 1.1 Skinned RenderModel: what the cooker writes **[data] [refl]**
The layout is in `docs/re/models.md` 3 and 5. Checked again on `char_enemy_vukah` (`7d9ae3f5-…`): 22,533 LOD 0 vertices, 86 model joints of a 97-joint
skeleton, 5 LODs × 3 materials = 15 meshes.

| Field | What to write |
|---|---|
| `hierarchy` | the skeleton's GUID |
| `hierarchyJointCount` | the skeleton's `jointCount` |
| `jointCount`, `skinningMap` | u16 per model joint: model joint → skeleton joint. Only joints with weights need an entry (86 of 97) |
| `renderData` | `vertexData`, then `skinningData`, then `blendShapeData`, then `indexData`; 4-byte aligned. Vukah: 540,792 + 180,264 + 0 + 302,514 B |
| Skin stream | 8 B per vertex: 4 × u8 indices into `skinningMap`, 4 × u8 weights summing to 255. Unused slots weight 0. So at most 256 model joints |
| `mesh.skinVertexOffset` | cumulative vertex count of the groups before it |
| Vertices | `PNTCT` in bind pose, game space, as for rigid models. 16,419 skinned meshes use it |
| `boundingBox` | exact bounds of the bind-pose vertices (Vukah: stored = decoded). How the renderer culls a mesh animated outside it was not read **[hyp]**; a conservative box costs nothing |
| `positionScale`, `positionOffset`, `wholeModelRadius` | as for rigid models |
| `materials` | as for rigid models. `RenderMaterialResource` + textures: `tools/modelcook`, kiln.assets `texture`. VFX and shaders are a separate topic |
| `blendShapes`, `cloth`, `chain`, `sleeves`, `collider`, `sdf`, `skinnedTree` | empty is valid. In the 267 creature models: cloth 96, cloth collider 28, chain 11, sdf 1, **blend shapes 0**, sleeves 0, skinned tree 0. `modelcook` already forces them empty |
| `globalScale` | 1.0 on weapons, **1.3 on the Vukah**, not 1 on 142 of 267 creature models. What it multiplies was not traced: stored vertices and skeleton already agree without it **[hyp: culling or LOD]**. Write 1.0 |

**LODs:** 8 (152 models), 7 (45), 6 (48), 5 (18), 4 (3), 1 (1). `lods` must not be empty. One LOD is legal, as in `models.md`.

**What `tools/modelcook` lacks (`cook/gltf.py` raises "skinned meshes are not supported yet"):**
1. read `JOINTS_0`, `WEIGHTS_0`, `skins[].joints` and `inverseBindMatrices`;
2. normalise to the 4 heaviest weights, quantise to sum 255, remap to used joints (`skinningMap`);
3. write the skin stream and range, per-group `skinVertexOffset`, `hierarchy`, the joint counts;
4. cook the skeleton (1.2) or bind to a game skeleton by index;
5. validation: decode, re-encode, deform at bind within 1 quantum (the `models.md` check), joint distance as in `models.md` 5.

`tools/kfc` already exports a skinned model and now a clip to glTF, so a vanilla creature can round-trip as the reference test.

### 1.2 Skeleton: `keen::ModelHierarchyResource` (72 B; 257 resources, 2-172 joints, median 15) **[refl] [data] [rt]**
A new skeleton can exist. Written from nothing, it loaded and animated on the server **[rt]**.

| Offset | Field | Rule |
|---|---|---|
| 0x00 | `hierarchyId` | the resource's own GUID |
| 0x10 | `jointNames` u32[] | any unique hashes. The game's names are stripped (`models.md`) and all 257 skeletons have them unique. The only use seen is the clip's `hierarchyHash`; no search was made for others **[hyp]** |
| 0x18 | `jointParentIndices` u16[] | 0xFFFF = root, a parent before its child |
| 0x20 | `unbendableJoints` u16[] | may be empty. Meaning not read **[hyp: a knee/elbow list for IK]** |
| 0x28 | `skinToJointMatrices` Matrix43[] | the inverse of the chained rest pose: 249 of 257 skeletons within 0.02, columns x, y, z stored in that order, then translation. Only the client's skinning can use it **[hyp]** |
| 0x30 | `jointCount` | |
| 0x34 | `defaultPose` {vf32 blocks, `alignedJointCount`} | `alignedJointCount` = jointCount rounded up to 4. Blocks: orientation float4 × aligned, position float4 × aligned, scale float4 × aligned, read by the sampler for `default` tracks **[dis] [data]** |
| 0x40 | `defaultPoseJoints` AnimationJoint[aligned] | the same pose as a struct; padding entries are identity |

`JointAnimation.hierarchyHash` = **FNV-1a 64 over the `jointNames` array bytes**: 190 of the clips' 191 distinct hashes match a skeleton, with the same joint count **[data]**. It is not a run-time
check: 437 clips carry the one remaining hash, 43 of them referenced by graphs **[data]**. Skeletons with the same names share a hash and clips (257 resources, 201 hashes).

**Limits [refl] [dis]:** bones are by index, never by name. u8 bone indices appear in `BoneMaskEntryResource`, `BlendProfileEntryResource`, `IKChainItem_Runtime`;
`AnimationJoint[256]` static arrays and the log `doJointExportJoints: joint capacity %,u < hierarchy %,u`. So 255 joints; the game's maximum is 172.

### 1.3 Colliders, hit zones, ragdoll, scale **[data] [refl]**
- `ColliderResourceComponent.colliders`: 1 entry (74 templates) or 11 (621): one per `keen::ecs::ColliderMode` {Default, Sitting, Lying, Evading, ClimbingWall, Death, Invisible,
  Swimming, DivingAlive, DivingDeath, Flying}. Each `ColliderResourceData` {`dataArray` of `BlobVariant<ColliderData>`, flags, `physicsSetup`, `attachmentSlot`, `joltObjects`}.
- Shapes over all 701 creature-like templates: capsule 13,708, sphere 6,766, box 397. Each has `offset`, `orientation`, `flags {isA, checkAgainst}`, `colliderModeSetup` and a `shape`. **`isA`:** Steering 5,994, Combat 5,599, BlockCamera 5,352,
  Solid 5,342, Interaction 4,618, PathGridInteraction 495, Force 80, Sight 58, Walkable 47.
- **All 6,905 entries are on slot Root.** So hit shapes do not follow the bones. The Vukah: 1-4 capsules per mode.
  `joltObjects` {flags, hash, `shapeIndexList`} index shapes into Jolt bodies. **Jolt** is the physics engine: `RagdollSettings`, `Skeleton`, `SkeletalAnimation` in the exe's strings are Jolt's own RTTI.
- **Weak spots and limb hit zones are child entities:** `SubEntities[]` {template, slot, `attach`, `addImpactHierarchy`, `copyFaction`...} with their own collider, `CombatFlags {propagateHits}` and `DamageSusceptibility`. They do follow a slot,
  so a mod gets per-limb hit zones with one small template per slot (`docs/re/creatures.md` 3.3). Only 36 slots exist: 16 named + 20 generic.
- **Ragdoll: none.** No template has a ragdoll component; death is the graph's `Dead` state plus `deathSequence`. Doing real ragdolls would mean driving Jolt from Kiln on the client: a separate project, not a cook step.
- **Scale:** `ScaleComponent` per entity and `CurrentTransform.scale`, known to reach vanilla clients (`docs/kiln/services/creatures.md`).
  `MovementColliderConfig` (Vukah: width 1, height 2, step 1) and `PathWalker` do not scale by themselves **[hyp]**: set them per body.

### 1.4 Armor and visual equipment on an NPC **[data] [refl]**
Two ways of having a body exist:
- **`ModelResource`**: one RenderModel (532 of 538 enemies; 31 of 58 villagers). Gear is `SubEntities` on slots, with `SlotAttachment`, `VisualEquipment` and no skinned armor.
- **`CharacterSetup` + `NewCharacterSetup`** (all 58 villagers, 0 enemies) with **`RenderEquipment` + `Inventory`** (30 villagers, 4 of them also with a `ModelResource`, 1 villager with neither): the player's character system: gender, presets, colours, items as skinned armor on the player skeleton `87b29df6-…` (102 joints), with `hideMask` and `blendShapeMask` per body part.

So armor binds through **the same skeleton**, by joint index: a custom skinned armor is cooked against the skeleton the wearer uses (`models.md` 7). On a custom body, rigid gear on a slot is the working route: it needs only `slotBoneIndexMapping` (2.4).
Blend shape data remains undecoded (`models.md` 8), needed only to adapt armor to the player's body shapes.

### 1.5 Is an author's skinned glTF possible?
Yes. Everything above is a writer's job with no unknown format left except blend shapes and cloth, which are optional. Unverified: the client drawing it (section 8).

## 2. AnimationBlock

### 2.1 Clip: `keen::JointAnimation` (48 B root; 5,461 resources, 168.8 MB, all 60 fps) **[refl] [dis] [data]**
Sampler: server `0x775300` (the checked variant, client `0x93c420`), a fragment at `0x773db4` and siblings. Decoded from the sampler, then proven on all clips. Implementation: `tools/kfc/kfc/anim.py`.

| Offset | Field | Meaning |
|---|---|---|
| 0x00 | `hierarchyHash` u64 | 1.2 |
| 0x08 | `positionFactor` f32 | position value = i16 × this |
| 0x0c | `positionTangent` f32 | position tangent per frame = i16 × this |
| 0x10 | `scaleFactor` f32 | scale value = i16 × this; scale tangents use 2⁻¹⁷ |
| 0x14 | `headers` BlobArray<JointAnimationHeader 20 B> | one per segment |
| 0x1c | `data` BlobArray<vf32 16 B> | the track data |
| 0x24 | `fps` u16 | 60 in all |
| 0x26 | `frameCount` u16 | N; duration = N / fps (Vukah Idle: 640 → 10.667 s = the graph's `animationDuration`) |
| 0x28 | `jointCount` u16 | the skeleton's |
| 0x2a | `flags` u16 | 0 clamp (2,817 clips), 1 loop (2,644). Sampler: loop = frame mod N; clamp past the end = frame N−1 + 0.999 |

**Segments:** each covers `[startTime, endTime)` = exactly 63 frames (21,480 of 21,480, the last one runs past N). `JointAnimationHeader` = {`dataOffset` u32 bytes into `data`, `startTime`, `endTime`,
`usedOrientationJoints`, `nonConstOrientationJoints`, `usedPositionJoints`, `nonConstPositionJoints`, `usedScaleJoints`, `nonConstScaleJoints`}, all u16.

**A segment's bytes**, in order, for orientation, then position, then scale:
```
u64 mask[nonConst]                 bit i = a key at frame startTime + i
for each animated track:           nonConst of them
    u16 joint
    key[popcount(mask)]: value[c], tangent[c]      c = 4 x i16 (orientation) or 3 x i16 (position, scale)
for each constant track:           used - nonConst of them
    u16 joint, value[c]
u16 n; u16 joint[n]                joints this segment takes from the skeleton's default pose
pad to 8 bytes
```
Segments follow each other at 8-byte boundaries. The `data` array ends at `align16(end + 16)`: the sampler reads 16 bytes at unaligned positions.

**Sampling a track at frame f (float), key frames f0 ≤ f < f1, u = (f − f0) / (f1 − f0), d = f1 − f0:**
```
v = v0·(2u³−3u²+1) + t0·d·(u³−2u²+u) + v1·(−2u³+3u²) + t1·d·(u³−u²)
```
| Channel | value scale | tangent scale (per frame) | after |
|---|---|---|---|
| orientation | 1/32767 | 2⁻¹⁷ (`0xbdc7b0`) | normalise (rsqrtps): stored keys have norm 0.99995..1.0 |
| position | `positionFactor` | `positionTangent` | |
| scale | `scaleFactor` | 2⁻¹⁷, as orientation: the sampler reloads that constant before the scale block. Confirmed on 4,006 dense keys: against central differences the median error is 0.00025 with 2⁻¹⁷ and 0.076 with `scaleFactor` | |

**What a writer must keep:**
- Bit 0 of every mask set: 747,895 of 747,895. A track needs a key at or before every frame it serves.
- A key after the last frame: the last key of every track is at exactly frame N (240,401 of 240,401 in last segments), one past the last whole frame, both for loop and for clamp. So N+1 samples. For a loop, make frame N equal frame 0.
- A key at bit 63 in every segment but the last: 507,494 of 507,494 non-last masks. The next segment starts at that same frame, so both carry it.
- Tangents: stored tangents match central differences (median difference 0.0009 per frame on 46,783 dense keys), so C¹ across segments comes from one dense curve.
- Every joint in exactly one of the three lists, per channel, per segment: 64,440 of 64,440 channels. Each list sorted by joint: 64,440 of 64,440. The sampler itself writes by index, so the order is probably free **[hyp]**; keep it sorted.

Our encoder `anim.encode` (dense samples, error-bounded greedy key choice, central-difference tangents): Vukah Idle 95,072 B vs the game's 86,304; player clip 40,272 vs 93,728; Vukah Attack_01 86,288 vs 74,432. Default tolerances: 0.0005 per quaternion component, 0.2 mm, 0.0005 scale. Max error against the game's own clip was 0.9-1.5°, one case 2 cm: the clamp of tangents at i16 is the first thing to tune when writing the real cooker.

**glTF to clip:** sample the source at 60 fps to N+1 frames, convert each local TRS to game space (mirror x: position (−x, y, z), rotation (x, −y, −z, w), the inverse of `tools/kfc`'s export), hemisphere-fix quaternions per joint, then `anim.encode` + `anim.build`. Any rotation order or easing is gone by then; there is nothing else to preserve.

### 2.2 Root motion and LVE **[refl] [data]**
Both are **sibling resources under the clip's own GUID**: 578 clips have all three. `SampleAnimationClipNodeDefinition.rootMotionAnimation` names it (547 distinct references).
- `RootMotionAnimation` (32 B) {`orientations` float4[N+1], `positions` float3[N+1], `startToEnd`, `fps`, `frameCount`}: one uncompressed sample per frame (200 of 200 checked).
- `LveAnimation` (40 B) {`trackName` crc32, `orientationKeys` {time, values, controls}, `positionKeys`, `startToEnd`, `fps`, `frameCount`, `flags`}: a sparse keyed version, used by `SetAnimationEvent.usePureLveLocomotion`. Its exact interpolation was not read.
- The cooker writes both from the root's motion, or leaves them out: 4,883 of 5,461 clips have neither.
- `update_root_motion_ag2` feeds it into `DynamicLocomotion.rootMotionTranslation`; root motion moves the server's entity, so it is gameplay **[refl]**.

### 2.3 Notifies, foot planting, overlays **[refl] [data]**
- **Notifies:** a third sibling, `AnimationSequenceContainer` {`sequences` → `SequenceResource`}, used when the clip node says `hasSequences`. `SequenceResource` {`events`, `length`, `loop`, `name`}. Over 2,901 sequences: SfxNotifier 7,565, FoleyEffect 1,344, EnvironmentEffect 1,324, LeftFootPlanting 635, RightFootPlanting 555, ClothSimulation 514, VfxNotifier 343, TriggerNoise 131, Notifier 63.
  They are presentation, plus noise that AI hears. A cooked clip's notify track is one more plain resource.
- **Foot planting** is these two events plus `footstepSyncRole`/`footstepSyncPolicy` on clips and blend spaces, `IsRightFootNext` nodes. **Feet IK** exists only on two graphs (`BipedFeetIKSolver`, `GenericFeetIKSolver`, `FeetIKSettings`). Not needed for a first custom body.
- **Overlays:** `SetAnimationOverlayEvent` and `HitReactionConfig.hitReactionOverlay` set an `OverlayId`; `IsOverlayActive` nodes test it. An `AnimationGraphOverlayInputs` ring buffer carries them in the graph inputs. Hit flinches on the Vukah use it: a `Hit_Flinch` state machine additively layered by a `LayerBlend`, driven by overlay `0xc6b6f9d4` (used by 267 graphs).
- **Retargeting:** `RetargetSolverNodeDefinition` (6 nodes) and `RetargetDataResource` (3; per-bone scale and elongation factors, by index), body-type handling on humanoids. **All 683 creature graph components have `retargetData` empty.** A clip plays on one joint layout; to share clips across different proportions, cook a clip per skeleton: it is cheap now.

### 2.4 Graph: `AnimationGraphResource2_0` (212 B; 279 graphs: 2-2,944 nodes) **[refl] [data] [rt]**
**It is entirely data.** Fields: parameter id lists (float, uint, int, bool, id), `usedInputIds`, `hierarchy`, `slotBoneIndexMapping[36]` (slot → joint), `rootNode`, `postProcessRootNode`, feet IK node ids, `hasGenderMapping`, `nodeDefinitions`, `clothColliderReference`, `ikChains`, `syncNodes`, `feetIKSettingsReference`. `tools/kfc graph <guid>` prints one. Debug names survive for states, so the Vukah's reads `Idle`, `Walk`, `Run`, `Attack_01`, `Hit_Stagger`...

| Nodes (count over all graphs) | Kind |
|---|---|
| `PoseResult` 4,568, `SampleAnimationClip` 4,383, `BlendSpace_1D` 495, `LayerBlend` 208, `EmptyAdditivePose` 116, `SelectPoseByParameterId` 110, `SelectPoseByBool` 95, `UseCachedPose` 52, `SaveCachedPose` 26, `Container` 20 | poses |
| `StateMachineState` 4,267, `StateMachineTransition` 1,071, `StateMachine` 572 | state machines |
| `IdParameterEqualsConstant` 3,661, `IsOverlayActive` 314, `IdParameterEqualsConstantList` 158, `BoolParameter` 81, `BoolNegate` 52, `IDParameter` 43, `IdEqualsConstant` 43, `BoolOr` 29, `BoolAnd` 27, `BoolOrMultiple` 27 | conditions |
| `OffsetBone`, `RetargetSolver`, `BipedFeetIKSolver`, `GenericFeetIKSolver`, `ConstraintOrientation`, float math and others (under 15 each) | rare |

Reflection knows about 70 node definitions, so more exist than the game's data uses **[refl]**.

**Writer's rules:**
- Node `id` is any unique u32; references are by id. We used FNV-1a of our own names **[rt]**.
- Inputs before users in the array: all 4,568 PoseResults follow their input **[data]**.
- `rootNode` is a `PoseResult` in all 279 **[data]**.
- A condition: `IdParameterEqualsConstant {parameterHash = parameterId = the input's hash, value = FNV-1a of the state GUID}`.
- `StateMachine`: `states`, `entryState`, `globalTransitions[34]` {`toState`, `duration`, root motion and event modes, `conditionNode`, curve, profile} + `numGlobalTransitions`. **Maximum 34 per machine:** nest machines for more (the player's graph does; `tools/animcook` does it for you, 40 states run **[rt server]**). Local `StateMachineTransition` nodes go in `transitions` with `transitionRefInfo`; a state can also auto-transition at its end.
- `StateMachineState`: `poseResult`, `parentStateMachineId`, `retriggerBlendDuration`, `restartOnRetrigger`, `startAtRandomTime`.
- `SampleAnimationClip`: `animation`, `animationHash` = FNV-1a 32 of the clip GUID (4,383 of 4,383), `animationDuration`, `loop`; `additiveReferenceAnimation` set to the same clip in the game's nodes. `sendEvents` true.
- `usedInputIds`: every state id the graph compares against (a superset in all 279).
- `slotBoneIndexMapping`: joint 0 for unused slots. Our lab graph set Head, Pelvis, HandR, HandL; `slotsInitialized` came back with those bits **[rt]**.
- `tools/kfc/kfc/animgraph.py` holds this as code.

**Whether a game graph can be edited in place:** states and clips in a graph are just data, so yes, a modified copy under override is the same writer. **Writing from scratch works** and is simpler than editing: the Stickman's graph has 10 nodes.

### 2.5 The engine's contract with a graph **[dis] [data] [rt]**
**The state input.** Id input hash `0x5d2bfb0a` (written by the engine at `0x778850`, from GUID `c9dfabdd-3ada-4103-8d47-3c9cc49565e3`). It holds the FNV-1a of a `StateId` GUID. `ServerAnimationGraphInput+0x22c animationStateId` shows it. Sources:
- a sequence's `SetAnimationEvent.animationName` **[rt]: state went `0x1490bed7` → `0x237b3b13` (ours) → back to `0x1490bed7` when the sequence ended**;
- the AI's own choices: `currentIdleAnimation`, `currentWalkAnimation` in `DynamicEnemy01BehaviorState`, set by `SetIdleAnimationAction` / `SetWalkAnimationAction`;
- template fields: `combatSetup.combatIdleAnimation`, `patrolSetup.patrolAnimation`, `fleeSetup.fleeAnimation`, `sleepSetup.sleepAnimation`, `blockingSetup.blockWalkAnimation`, `EnemySettings.idleAnimation`, `FlyingEnemy.idleAnimation`, `Wildlife.idleAnimation`, the swim and dive set in `LocomotionWaterConfig`;
- **`locomotion_animation` (`0x1d7bcf`) picks from a fixed table in `.rdata` (`0xbe6a04`, `0xbe6b0c..0xbe6b44`):**

| Hash | State (name from the game's graphs) | Transitions to it in the graphs |
|---|---|---|
| `0x1490bed7` (GUID `2e701760-…`) | **Idle**: every creature's default | 174 |
| `0x1a8dbbbb` | **Walk** | 111 |
| `0x28689701` | **Run** | 90 |
| `0x25369c6d` | Sprint | 8 |
| `0x5d8fed2b` | Idle_Combat | 34 |
| `0xdd657be9` | Fall | 54 |
| `0x30387abb`, `0x230ee7d7` | Idle_Sneak, Sneak | 3, 1 |

So **a custom graph must define at least Idle, plus Walk and Run if it moves and Fall if it can fall**; these ids are the vocabulary. 530 distinct state ids are used through sequences, shared across creatures. Everything else can be new GUIDs: a state id is not a resource, so nothing has to be registered **[rt]**.

**Float inputs, by GUID hash, all from `locomotion_animation`** (a graph lists what it wants in `floatParameters`) **[dis]**:

| Hash | Source | Used for |
|---|---|---|
| `0x28bb2ff6` | computed move direction | **Vukah's Walk blend space, −180..180** |
| `0x232a4502` | computed | **Hit direction: Hit1, Hit2, Hit_Stagger blend spaces, −180..180** |
| `0x399d81a5`, `0xa6a829b0` | `ClientLocomotion.currentLeanYawAngle`, `currentLeanPitchAngle`, degrees | lean (41, 12 graphs) |
| `0x6728f981`, `0x3c857884` | `currentLookYawAngle`, `currentLookPitchAngle`, degrees | look |
| `0x8a0ff150`, `0xe8aad2a8` | `currentToTargetLookYawAngle`, `…PitchAngle`, degrees | look at target |
| `0x198e319e`, `0x85218118` | `currentHeadLookAtYawAngle`, `…PitchAngle`, degrees | head look |
| `0xe9046158`, `0x31e304e7`, `0x251ea4f4`, `0xac322f74`, `0x168af2e0` | computed; `0x168af2e0` = `DynamicLocomotion+0x184 ledgeHeight` | the player's graph mainly; not named here |

Inputs are capped per entity: `AnimationGraphInputs` holds **16 float, 8 uint, 17 bool, 2 int, 4 id** (`[animation] No space for animation graph float input` otherwise) **[refl]**. Sequences can set floats and bools by GUID (`SetAnimationFloatInputEvent`, `SetAnimationBoolInputEvent` **[refl]**; setters by GUID `0x74fd40`, `0x74fc90`), so a mod's own inputs need no code: invent a GUID, put FNV(GUID) in the graph's list, set it from a sequence **[rt server]**. Three limits, all seen on the lab **[rt server]**: `SetAnimationFloatInputEvent` is handled in `actor_locomotion` (setter `0x7719d0`), so the body needs Locomotion; the request lives for the event's `duration` (0 sets nothing that lasts), with at most 4 requests at a time (`animationFloatInputRequests[4]`); and a standing Vukah already fills **12 of the 16** float slots by itself (`28bb2ff6 e9046158 31e304e7 3c857884 e8aad2a8 8a0ff150 198e319e 85218118 251ea4f4 399d81a5 a6a829b0 ac322f74`), which leaves **4 for a mod's own**. The game's data never uses this event.

**How it runs, server and client [refl] [rt]:**
```
server: sequences, AI, locomotion_animation -> ServerAnimationGraphInput
        server_update_animation_graph_2 -> the graph evaluates (AnimationGraphSystem), handle in ServerAnimationGraphComponent2
        server_update_animation_graph_slots_2 -> ServerAnimationGraphSlotComponent.localSlots[36]: attachments, impact colliders on slots
        update_root_motion_ag2 -> root motion into the body
        server_to_network_animation_graph_input -> NetworkAnimationGraphInput (568 B): state id, retrigger count, overlay ring, inputs
client: network_to_client_animation_graph_input -> ClientAnimationGraphInput, client_animationgraph_input_lerp
        client_update_animation_graph_2, update_animation_graph_pose_2, pose_model_2 -> the drawn pose
```
**Both sides need the same graph and clips.** The server's matter: root motion and slot positions are gameplay. A vanilla server-side change to a clip therefore changes melee hit positions, as `docs/re/abilities.md` 5 suspected: `SpawnImpact` colliders with an `attachmentSlot` read these slots, whose values follow the clip **[rt for the slots; hyp for the impact]**.

### 2.6 Proof in the lab **[rt]**
`Kiln_NpcRe_Stickman`: 6 joints, 2 clips (±30° loop 2 s, 0..−90..0 over 1.5 s), 10 graph nodes, all Kiln's GUIDs, served through kiln.assets; 0 errors (lines from two runs):
```
template lookup: Kiln_NpcRe_Stickman ; spawned stickman 2542
S t=47   handle=33554470 state=0x1490bed7 slotmask=0x30b HandR=[1 0 0  0 1 0  0 0 1  0.8500 1.7000 0.0000]        rest pose: 0.25+0.6, 0.9+0.5+0.3 (ours)
S t=423  state=0x1490bed7 HandR=[0.8788 0.4771 0 -0.4771 0.8788 0 0 0 1 0.7773 1.9863 0]   the idle clip's yaw, on the cooked curve
PlaySequence swing: True
S t=6792 state=0x237b3b13 HandR=[0.0470 -0.9987 0 0.9987 0.0470 0 0 0 1 0.2782 1.1008 0]   the swing clip, near −90°
S t=8099 state=0x1490bed7 HandR=[0.8788 0.4771 ...]                                        back in the idle clip
```
Over 330 samples: forward-kinematics residual 0.18 mm, idle ±29.99° of ±30° (1.5° residual is wall clock against game clock), swing peak −89.94° of −90°. The same graph on a template of 12 components played the same way.

**The cooker, on the same lab** (`mods/tests/animcook-test`: glTF in, `tools/animcook`, served by kiln.assets, read back from the slots) **[rt server]**: LINEAR, STEP and CUBICSPLINE keys on the authored curve; a blend space on `moveDirection` and on a mod's own float input; 40 states on nested machines, with no entry-state artefact crossing between groups; an additive layer through the `hit` overlay over a held pose, with a bone mask (masked head moved 0.02 mm); a chain of states moving on by itself and starting over when entered again.

Two graph rules from those runs: a global transition on a chain's first state pulls the machine back into it as long as the input holds its id, so **a chain has to be its own machine**, entered from outside; and `SetAnimation` needs a state id only the outer machine compares.

**Root motion reaches the body's request, not yet its position.** The cooked 2 m in 2 s gives `rootMotionTranslation` 0.0500 per step on the Golem, exactly the curve at 20 steps a second, with mode `0x2`. The Golem does not move on the lab, and neither does a vanilla Vukah playing its own `Attack_01` (0.3462 per step, moved 0): it is the lab, not the cook **[rt server]**. Probably no streamed terrain for the character controller to move on without a player **[hyp]**. Check in the client.

**Not shown:** anything drawn.

## 3. AIBlock: perception, targeting, movement

### 3.1 What is data and what is code **[data] [dis]**
| Part | Data, per template (private copy per entity through `CreatureRef.Ai`) | Code to hook when the data does not reach far enough |
|---|---|---|
| Sight | `aggroSetup` {viewRange, nightViewRangeFactor, viewConeHalfAngle, aggroRange, gains, loss, thresholds, pools}, `Aggro {applyEnemyValue, sightCheckSlot}`, `targetSelection` (232 B), `TargetSelectionSettings` | `enemy_combat_targets` `0x13e580`: `0x13fdf0` sight and hearing per candidate, `0x13cf30` LoS (`0x9300b0`, the raycast, 29 callers) |
| Threat | `threatFactor`, arsenal `threat`, `Threat_Pool*`, `AllyAggroMultiplier` attributes | `enemy_threat_events`, `threat_timestep`, `0x13cab0` add aggro |
| Target choice | parameters above, rate limit in `DynamicAggro.nextAllowedTargetChangeTime +0xb10` (0-0.5 s random) | sort `0x13be50`, then `SelectedTargets[0]`; `post_enemy_combat_targets` runs right after |
| Factions | `Faction` per entity, 16 values. `DamageModAgainst_Faction_*` | **the relation is code**: leaf `0x94a690(Faction*, Faction*, opt*)`, client `0xea3440`, 6 direct callers, no `.pdata`: equal → true; both on the player side {7 Player, 8 AutomatedPlayer, 11 PlayerAlly} → true. New relations need a hook on it. A new faction would be a raw value above 15 in the u8 field; attributes such as `DamageModAgainst_Faction_*` have no slot for it **[hyp]**. Some checks may be inlined **[hyp]** |
| Leash, home | `homeZoneSetup.radius`, `EnemySettings.leashLength`, `maxTargetDistanceToHomeZone`, feature bits ReturnHome, FleeHome. Constraints `IsTargetInsideMovementLeash`, `IsTargetInHomeZone` | condition TargetOutsideLeash raised only from `0x133e70` |
| Movement | `Movement` speeds, `Locomotion` (632 B), `SteeringLocomotion`, `combatSetup` distances, `movementSetup`, `PathWalker`, `PathFinder`, `MovementColliderConfig`, `SlopeConfig` | `MoveRequest` `0x1169e0`, `path_walking`, `pathfinding` |
| Awareness, noise | `awarenessSetup`, `investigateSetup`, `notifierSetup`; TriggerNoise events | `0x1329e0` awareness, `0x125f70` noise and projectiles |

### 3.2 Pathfinding **[refl] [data] [dis] [rt]**
- **A dynamic 3D grid built from the voxels and colliders**, `ecs::PathGridSystem`, in half-metre units (`PathGridDistance {halfMeters}`); cells appear on demand (`PathGridCellCreationReason`: PathQuery, FindWalkablePosition...), and the system has low memory states. Hence no path without streamed terrain (`creatures.md` 2.3).
- **Each node has a type:** `PathNodeType` {TerrainEdge, Tar, Mud, Road, RoadCenter, Fog, DeadlyFog, Lava, Building, Collider, WalkablePlatform, NoNpcMovementCollider, NoNpcMovementTerrain, NoNpcMovementBuilding, NpcAvoid, WaterSurface, UnderWater, WaterGround, ClosedDoor}. `PathWalker.pathNodeRequirements {requiredTypes, forbiddenTypes}` per template: forbid Tar + DeadlyFog + Lava (327), nothing (151), everything wet and no-NPC-marked (128), Tar + DeadlyFog (82), require Tar + Mud (1 creature). Data, per body.
- **Body size is two numbers:** `PathWalker.horizontalRadius` and `height`, which become `DynamicPathWalker.minHorizontalSpace` / `minVerticalSpace`. Game values: radius 0 (674 of 689), 0.5 (4), 1 (8), 2 (3); height 0.5 (28), 2 (658), 3.5 (3). Node search only rejects nodes without the space. **No fixed size classes**: other values should work at a search cost **[hyp]**. A large body on tight terrain gets `NoPathFound`.
- **`usePathFinding` false (24 templates, e.g. `Enemy_Boss_Fog_Dragon`):** moves straight at its target. The usual route for very large bodies.
- **Move types:** `PathMoveType` {Move, Fly, JumpUp, JumpDown, JumpHorizontal, OpenDoor, Swim, Dive, MoveOnWaterGround}; `DynamicPathWalker.moveTypes` is a mask, set per request. `PathWalker.jumpSequence`, `openDoorSequence`, `teleportSequence` name sequences.
- **Flying:** `Flying` + `FlyingEnemy` on 84 templates (heights, arrive distances, flap, start, stop, hover sequences), arsenal `StartFlyingAction` (31 uses). Data on top of the same walker.
- **Swimming:** `LocomotionWaterConfig` on all (685 + 4 of two kinds) with animations by state id, `canSwim` / `canDive` at run time, enemies usually forbid water nodes and use the `AvoidWater` state.
- **Limits in the search** (`0x60900b`): `PathGrid : Max path node limit reached`, `OpenNodes capacity limit reached`, `DynamicPathWalker.maxIterations`.
- **Taking it over:** `disablePathfinding +0xf1`, `targetPosition +0xb0` and friends are plain fields, read by `path_walking`. A mod can walk a body along its own path by writing `ActorInput` and leaving the walker idle, or just ask for targets.

### 3.3 Friendly NPCs against enemies **[data] [dis]**
| | Enemy | Villager | Wildlife | Farm animal |
|---|---|---|---|---|
| Program | `enemy01` | `npc_active_behavior` + `npc_behavior` | `wildlife` | `animal_behavior` |
| States | 30, one table in `.data` (the table is filled at first use) | 18 in `keen::active_npc::State`, code | 21 | 16 |
| Tuning | `Enemy01BehaviorState` 2,520 B + arsenal | `ActiveNpcState` 456 B: `decisionSetup {idle, inspect, sit, roam}` weights, `sleepSetup`, `roamingSetup`, `socializingSetup`, sequences | `Wildlife` 736 B | `Animal`, `AnimalSaved` |
| Attacks | arsenal | none | none | none |
| Pause | Spawning until ready (Actor bit 50) | worker pauses at `closestPlayerDistance` ≥ 150 m; stays in Start without players | | |
| Identity | any number | one per `NpcId` = FNV(template GUID); locked roster NPCs are destroyed | any | |
| Body | `ModelResource` | `CharacterSetup`/`RenderEquipment` (30) or `ModelResource` (31), 4 with both | `ModelResource` | `ModelResource` |
| Replicated | `NetworkEnemy` | `npc_active_behavior_to_client` → `ClientActiveNpcState`, `NpcDialogClientSync` | `NetworkWildlife` | `NetworkAnimal` |

Friendly here means faction: 43 enemy-AI templates are Player or PlayerAlly, and `guardSetup.canGuardPlayer` is on 79 (`creatures.md` 2.6). **A "friendly with attacks" NPC is the enemy program with a friendly faction**; a villager cannot fight without new code.

### 3.4 Schedules and routines **[dis] [refl]**
Villagers have no schedule data beyond `sleepSetup` (`NpcCommon.sleepTime`, `wakeUpTime`, `bedId`). `DecideTask` `0x1fb8c0` in order: three scripted flags (`0x1ebea0`, `0x1ebe80`, `0x1ebe90`), sleep check `0x1f0490` → WantsToSleep + FindBed, a pending target → WantsToSocialize + GoToTarget, then a weighted random over the four weights, skipping the last action. "Work" is `Crafting {workshop}` + `npc_update_important_locations` picking what to stand at, not a job system. A real daily routine is the clearest case for a hook on `DecideTask` or for driving by `NpcCommandEvent`.

### 3.5 Hook map: every decision point (server 26430253 / client 26430625, svn 1287369)
**Reads and writes** at the program level are the game's own access lists (the 0x188-byte descriptors, section 7). **Thread:** everything here runs on the game's job threads inside the step, except flag `0x4` programs **[hyp: serial]**. A handler must be thread-safe and must not call tick-thread APIs (`docs/kiln/hooks.md`). **Rates** are from the lab with 3 forced-ready Vukahs; a joined player raises the step rate (60/s seen, `services/creatures.md`). **Signature:** a prologue pattern with RIP displacements and branch targets masked, found exactly once on each of svn 1287369, 1285601 and 1273043.

| Decision point | Server / client RVA | What it is, what it takes | Rate [rt] (lab, 24.3 steps/s) | Prologue signature |
|---|---|---|---|---|
| **Enemy, whole AI** | | | | |
| `enemy01` per-entity function | `0x12ce30` / `0x36d740` | Gate: `DynamicEnemy01BehaviorState+0x498` (0 skips the rest). Builds the context, finds the arsenal by GUID, runs the behaviour update. 7 args: mid hook only | 1 per creature per step | 32 B, unique on all 3 servers |
| Behaviour update | `0x135470` / `0x375d80` | The whole decision pass: raises conditions, runs rules, dispatches the state. Arg: the context | 1 per creature per step | 16 B, unique on all 3 servers |
| HandleCondition | `0x12c4b0` / `0x36cdc0` | `(ctx, cond)`: `table[state*0x21+cond]`, feature gates. Veto or redirect any transition here | 2.6 per creature per step | 24 B, unique on all 3 servers |
| SetState | `0x12af10` / `0x36b820` | `(ctx, new)`: exit and enter. One caller (HandleCondition) | 0.3/s for 3 creatures | 16 B, unique on all 3 servers |
| StateDispatch | `0x13a7f0` / `0x37b100 or 0x460900` | `(ctx, state, phase 0 enter / 1 execute / 2 exit)`. New states would go here: return early for ours | 0.95 per creature per step | 16 B, unique on all 3 servers; client needs a longer one (2 hits) |
| **Enemy, combat** | | | | |
| Combat state | `0x133030` / `0x373940 or 0xc74ca0` | Combat(6) handler | 0.4 per fighter per step | 16 B, unique on all 3 servers; client needs a longer one (2 hits) |
| Combat sub-state | `0x133e70` / `0x374780` | `(ctx, CombatState, phase)`: Move, Attack, AttackExecution, Block, Prowl, Flanking, Dodging, Taunt, CallForHelp, TargetNotReachable, TargetOutsideLeash... Raises TargetUnreachable and TargetOutsideLeash | 0.46 per fighter per step | 16 B, unique on all 3 servers |
| SelectAttack | `0x1266a0` / `0x366fb0` | `(out, ctx, AttackStyle)` walks the arsenal: style mask, availability, `selectionChance`, least-used first, random among ties. `out[0]` = error (0 ok), `out+8` = index. 7 callers | 0.4/s for 2 fighters | 16 B, unique on all 3 servers |
| AttackAvailable | `0x126e60` / `0x367770` | `(ctx, index)`: range, cooldown, constraints for one attack. One caller | 0.4/s | 16 B, unique on all 3 servers |
| ConstraintEval | `0x11d9e0` / `0x35e380` | `(out, ctx, variant*, extra)`: the arsenal constraint dispatcher, binary tree on the variant hash; an unknown hash is false | 3.2/s for 3 creatures | 32 B, unique on all 3 servers |
| ActionExec | `0x11eed0` / `0x35f870` | `(ctx, state, variant*, flag)`: the arsenal action dispatcher; an unknown hash does nothing | rare | 24 B, unique on all 3 servers |
| Behaviour rules | `0x1207b0` / `0x361150` | `(ctx, mode)`: walks `behaviors[]` of one `BehaviorMode`, timers, chance, then constraints and actions. Phases change here (`SetPhaseAction`) | 14/s for 3 creatures | 24 B, unique on all 3 servers |
| Start attack | `0x12ab70` / `0x36b480` | Starts the chosen attack: combat state, rules. 6 callers | with SelectAttack | 16 B, unique on all 3 servers |
| TriggerSequence | `0x369d0` / `0x219a30` | `(out, actor, trigger context, sequence hash, trigger, input type)`: every sequence start of the game. 61 callers | on start | 112 B, unique on all 3 servers |
| **Enemy, movement** | | | | |
| MoveRequest | `0x1169e0` / `0x357380` | `(ctx, distance, mode)`: writes `DynamicPathWalker` (target, arrive distances), returns from the walker state. 10 callers: Guard, Patrol, Roaming, ReturnHome, Flee... | 2.2/s for 3 creatures, more when walking | 16 B, unique on all 3 servers |
| `path_walking` | `0x1ff610` / `0x466ea0` | Reads `PathWalker`, `DynamicPathWalker`; writes `ActorInput`. The boundary between AI and body | 1 per creature per step | 16 B, unique on all 3 servers |
| `pathfinding` | `0x203ab0` / `0x46b340` | Runs queries (`DynamicPathFinder`) against the path grid | 1 per creature per step | 16 B, unique on all 3 servers |
| **Enemy, perception and targets** | | | | |
| `enemy_combat_targets` | `0x13e580` / `0x37ee90` | Sight, aggro, LoS, sort: writes `DynamicAggro`, `SelectedTargets`. Inner: `0x13fdf0` gather by sight, `0x13cab0` add aggro on a candidate, `0x13f8f0` events, `0x13be50` sort | 1 per step per group | 16 B, unique on all 3 servers |
| `post_enemy_combat_targets` | `0x13f220` / `0x37fb30` | Runs after it: the place to overwrite `SelectedTargets` | 1 per step | 24 B, unique on all 3 servers |
| `enemy_sensor` | `0xf4040` / `0x334540` | Enemy range queries; uses the faction leaf | only while players are near: 0 in the lab | 16 B, unique on all 3 servers |
| `friend_sensor`, `flee_sensor`, `danger_sensor` | `0xf7050` / `0x337550` | `0xf7050`, `0xf6380`, `0xf2de0` | per step when active | 24 B, unique on all 3 servers |
| `enemy_threat_events` | `0x1310b0` / `0x3719c0` | `AddThreatTargetEvent` reader (SYSTEM flag 0x4) | 1 per step | 16 B, unique on all 3 servers |
| `threat_timestep` | `0x5baf0` / `0x23ec20` | Threat decay | 1 per step | 16 B, unique on all 3 servers |
| **Villager** | | | | |
| `npc_active_behavior` | `0x1f86b0` / `0x45de10` | Reads `ActiveNpcState`, `NpcState`, `PathWalker`; writes `DynamicActiveNpcState`, `ActiveNpcStateSaved`, `Actor`, `ActorInput`, `NpcCommon` | 1 per step per group | 16 B, unique on all 3 servers |
| Villager worker | `0x1f7850` / `0x45c8f0` | Per NPC: commands (`0x1f3320`), player distance pause at 150 m, then the state machine. Skip it to take over | 1 per villager per step | 16 B, unique on all 3 servers |
| Villager FSM | `0x1fe460` / `0x465d40` | Runs enter, update, exit | 0 in the lab (paused) | 24 B, unique on all 3 servers |
| Villager dispatch | `0x1ef600` / `0x452370` | Per-state switch | 0 in the lab | 24 B, unique on all 3 servers |
| Villager SetState | `0x1f6da0` / `0x45b320` | `(ctx, state)`. 12 callers | on change | 24 B, unique on all 3 servers |
| FinishState | `0x1f7750` / `0x45bcd0` | Ends a state. 12 callers | on change | 24 B, unique on all 3 servers |
| DecideTask | `0x1fb8c0` / `0x4631a0` | Order: scripted flags, sleep (`0x1f0490`), a pending target, then a weighted pick over `decisionSetup` {idle, inspect, sit, roam} that avoids the last action. **The routine decision** | on task end | 16 B, unique on all 3 servers |
| SetDecision | `0x1f65b0` / `0x45a8a0` | `(ctx, Decision)`: writes the decision. 3 callers | on change | 16 B, unique on all 3 servers |
| NpcCommand handler | `0x1f3320` / `0x4565f0` | `NpcCommandEvent` Reset, GoToPosition | per step | 16 B, unique on all 3 servers |
| `npc_active_target` | `0x1f8f20` / `0x45e680` | Picks interaction targets near the NPC | 1 per step | 32 B, unique on all 3 servers |
| `npc_socializing`, `find_npc_to_npc_interactions` | `0x1f9fe0` / `0x45f740` | `0x1f9fe0`, `0x1f80c0` | 1 per step | 24 B, unique on all 3 servers |
| `npc_update_important_locations` | `0x1fa3b0` / `0x45fb90` | What villagers walk to in a base | 1 per step | 16 B, unique on all 3 servers |
| `npc_behavior` | `0x1f97e0` / `0x45ef40` | Dialogue reaction: sequences, turning (`docs/re/creatures.md` 7.3) | on event | 16 B, unique on all 3 servers |
| **Other** | | | | |
| `wildlife`, worker | `0x21f880` / `0x488400` | Worker `0x21e8b0`. 21 states in `keen::wildlife::State` | per step when active | 16 B, unique on all 3 servers |
| `animal_behavior` | `0x4c500` / `0x22f200` | 16 states in `keen::animal::State` | per step when active | 16 B, unique on all 3 servers |
| `update_hit_reactions` | `0x10ef00` / `0x34f8a0` | Picks the hit sequence and overlay per `HitReactionConfig` | on hit | 16 B, unique on all 3 servers |
| `enemy_scaling` | `0x12fa80` / `0x370390` | Level and player count to stats | per update interval | 16 B, unique on all 3 servers |
| `locomotion_animation` | `0x1d7b90` / `0x4382d0` | Writes the graph inputs (section 2.5) | 1 per creature per step | 16 B, unique on all 3 servers |
| `actor_locomotion` | `0x7e490` / `0x26bcb0` | `ActorInput` to velocity | 1 per creature per step | 16 B, unique on all 3 servers |

Rows the table groups: 53 functions, all unique on all 3 servers with 16-32 bytes (`TriggerSequence` needs 112: use a second anchor). On the client 51 are unique and 2 hit twice (`StateDispatch`, `Combat state`): extend those. All RVAs of svn 1285601 and 1287369 are equal **[data]**. Full patterns: regenerate with the method in section 7. They need no new sigdb concept: `sigdb/symbols/server.d/creatures.json` already holds `SetState`, `HandleCondition`, `Enemy01Update`, `Enemy01Entity` this way.

**Program access lists worth having at hand [dis]** (T = template, R = read, W = written):
- `enemy01`: T `Enemy01BehaviorState`, `EnemyArsenalReference`, `PathWalker`; R `ClearPathToTarget`, `CurrentTransform`, `Faction`, `Health`, `DynamicHitReactionBehavior`, `Velocity`; W `Actor`, `ActorInput`, `DynamicEnemy01BehaviorState`, `DynamicFleeSensor`, `DynamicLocomotion`, `Movement`, `DynamicNetworkLocomotion`, `DynamicPathWalker`, `ServerAnimationGraphInput`, `Stamina`; plus optional `DynamicAggro`, `CombatFlags`, `DynamicEnemyAiming`, `DynamicEnemyPhases`, `DynamicFlying`, `SelectedTargets`, `SteeringAgent`... System reads: arsenal and enemy registries, `PathGridSystem`, `VoxelGrid`, `Daytime`, `WeatherSystem`.
- `enemy_combat_targets`: T `Aggro`, `Enemy01BehaviorState`, `PathWalker`; W `DynamicAggro`, `SelectedTargets`.
- `path_walking`: T `PathWalker`; R `Actor`, `CurrentTransform`, `Movement`, `MovementVector`; W `ActorInput`, `DynamicPathFinder`, `DynamicPathWalker`, `SteeringAgent`.
- `npc_active_behavior`: T `ActiveNpcState`, `MovementColliderConfig`, `NpcState`, `PathWalker`; W `DynamicActiveNpcState`, `ActiveNpcStateSaved`, `Actor`, `ActorInput`, `InteractionAttachmentGuest`, `DynamicLocomotion`, `DynamicNetworkLocomotion`, `NpcCommon`, `DynamicNpcState`, `DynamicPathWalker`, `SteeringAgent`, `DynamicSteeringLocomotion`.

**The context all `enemy01` functions take (`rcx`) [dis]:** `+0x00` the ECS program context (`CurrentEntityId` works on it), `+0x08` write records, `+0x28` template slots, `+0x48` `DynamicEnemy01BehaviorState*`, `+0x50` `Enemy01BehaviorState*`, `+0x58` the arsenal record (`attacks` BlobArray at +0x10), `+0x60` time. Already mapped by kiln.creatures (`creatures.md` 11).

## 4. BehaviourBlock: what it decides to do

### 4.1 Is there a behaviour tree? **[data] [dis]**
No tree, no script language. Three layers, as `creatures.md` 2.4 said; now mapped through the code:
1. **The FSM**, one table for all (`HandleCondition` + `SetState`), states as C++ handlers (`StateDispatch`).
2. **Per-template tuning:** `Enemy01BehaviorState`, feature bits.
3. **The arsenal, the real behaviour data:** 201 arsenals, 619 attacks, 399 rules. A rule is {`mode` (OnUpdate, OnEnter, OnExit, OnStartAttack, OnEndAttack, OnEnterState, OnExitState), intervals, cooldown, chance, constraints, actions}. So a rule system already exists: if conditions, then actions, on triggers.

**Attack selection [dis]:** `SelectAttack(out, ctx, style)` `0x1266a0`: for each attack with the wanted `AttackStyle` bit (Normal, Retaliate, CounterAttack, StunBreak, Special, Initiate, InitiateFollowUp, IgnoreThreat, Flanking): `AttackAvailable` (range, cooldown group, selection constraints), a roll under `selectionChance`, then a random one among those with the fewest uses (`dyn+0xd1+index`, indexes below 32); the last attack counts as most used, so it is not repeated while another qualifies. Attacks with the same `executionAttackSequence` share a counter. Output: `out[0]` error (0x11 no arsenal), `out+8` index, `out+0xc` its `threat`, `out+0x10` the availability code. Combat state then runs its sub-state machine (`CombatState`, 20 values).
**Phases, boss logic [dis] [data]:** data entirely: `SetPhaseAction` in rules, `IsInPhaseConstrain`, `EnemyHealthPercentageConstrain`, `DynamicEnemyPhases`, up to 12 phases in `EnemyPhases`. `CustomBehavior` is a combat state rules can enter.
**Sequences and Impact programs:** the "what happens" end. An attack's sequence holds `SetAnimationEvent`, `SpawnImpact`, sounds; the Impact program deals the damage (`abilities.md`). So state and damage timing travel together in one resource. Both are data; both can be new.

### 4.2 Three ways to give an NPC its own mind

| | A. Data | B. Rules in the arsenal, with Kiln's own constraint and action kinds | C. C# drives, game AI off |
|---|---|---|---|
| How | Author arsenal, setups, sequences | 2 hooks (4.3), handlers on job threads | `+0x498 = 0`, per tick `ActorInput`, `PlaySequence`, `Abilities.Cast`, `RunImpact` |
| Reach | Everything the game's designers could do | That, plus any C#/native condition and effect, on the game's triggers | Anything |
| Hooks | 0 | 2 | 0 for enemies. Villagers: 1 (`0x1f7850` skip) |
| Cost | none | ~100 ns per call + handler; the lab's rate is 3.2 constraint evaluations per second for 3 creatures | 3.8 µs per entity per step, uncached [rt]; about 400 µs per step for 100 NPCs |
| Thread | tick-free | job threads: handler must be thread-safe, or read a table the tick updates | tick thread: full API |
| Client | nothing extra | nothing extra | nothing extra |
| Loses | what data cannot say | nothing | the game's combat states, blocking, dodging, flanking, hit reaction logic, unless rewritten |
| Vanilla client | can watch, if the body is vanilla | same | same |

**Recommendation:** make B the backbone of a `BehaviourBlock`, with C as the escape hatch per NPC, and full replacement of individual decisions (attack pick, target pick, transitions) as named hooks. A pure C# brain throws away working combat AI; a pure data one stops at what Keen's designers wanted.

### 4.3 Modder-defined constraints and actions **[rt] [dis]**
`ConstraintEval(out, ctx, variant*, extra)` and `ActionExec(ctx, state, variant*, flag)` start with `*variant` = the `BlobVariant` type hash and walk a comparison tree. An unknown hash falls out: constraint false, action no-op **[dis]**. The arsenal loader does not validate the hash: 201 arsenals load as one blob **[rt]**. So:
```
Rule in the game's arsenal: { mode OnUpdate, interval 0.5 s, constraints [ {hash KILN_CONSTRAINT, rel, size} ], actions [ {hash KILN_ACTION, rel, size} ] }
Hook head of ConstraintEval: if hash == KILN_CONSTRAINT: run handler, out[0] = 0, out[8] = result, return
Hook head of ActionExec:     if hash == KILN_ACTION: run handler, return
```
Lab, the Vukah's arsenal + one rule (217,024 B against 216,904), Kiln hashes FNV of `kiln::npc::Constraint` / `kiln::npc::Action`:
```
E no hooks yet: 4 s with the Kiln variants in the arsenal and nobody handling them
E alive True Idle
E constraint false: rule evaluated 18 times, action 0 times
E constraint true: rule evaluated 10 times, action ran 10 times
```
The payload after the header is any bytes, so a mod's constraint and action can carry their own data and ids: a `kiln::npc::Call {block id, args}` dispatched to C#. Without Kiln's hooks loaded they do nothing, safely. **Attacks, sequences and everything else in an arsenal entry stay usable**, and a rule can mix game and Kiln kinds. The same trick should work for `AttackCommandArray` and `AttackDataVariant` **[hyp]**, not looked at.

Handler rule: these run on job threads. A handler may read only what is safe there (a table the tick publishes, or the context's own components). Effects with world access should be queued to the tick. Managed handlers work (the lab's were C#); on reload the `hooks.md` caveat applies.

### 4.4 Replacing one decision **[rt]**
Lab `E fight`, Vukah, arsenal attacks 0 Normal (0 to 2.5 m), 1 Retaliate/CounterAttack, 2 IgnoreThreat (5 to 22 m); a detour on `SelectAttack` overwrites `out+8`:
```
E natural: SelectAttack 5 calls, picks 0:5                         (attack 0's sequence)
E fight force=1: SelectAttack 29 calls, picks 1:6; sequences 16f4db39:644, 947bf0f6:72, b49eeb00:54      (hp fell on both)
E fight force=1: SelectAttack 26 calls, picks 1:9; sequences 16f4db39:330, 947bf0f6:113, a3531d29:39, b49eeb00:280
```
`b49eeb00` is FNV(attack 1's sequence GUID), `947bf0f6` attack 0's, `16f4db39` the heavy hit reaction, `a3531d29` the taunt. So the forced attack plays, though attack 1's style gate does not normally allow it there. Attack 0 still played at times: another start path (the attack rules, or before the pick) exists and was not traced. Two pairs at another spot in the lab never started fighting in 400 steps; the spot, not forcing, is the likely reason, since this happened with forcing off only and a natural pick happened elsewhere **[hyp]**.

**Same pattern for the others:**
- target: overwrite `SelectedTargets` in a `post_enemy_combat_targets` hook, or send `AddThreatTargetEvent` (already works, `creatures.md` 12);
- transitions: `HandleCondition(ctx, cond)`: change `cond`, or return without calling;
- move: replace `MoveRequest` (10 callers), return the walker status code (0 arrived, 1 walking, 2 stuck, 3 unreachable, 4 waiting);
- a new FSM state: `HandleCondition` to enter a free state number, `StateDispatch` to run it. Free numbers: the table is `30 × 33` bytes, and `State` values above 29 would index past the table **[hyp: stay within, reuse `CustomBehavior` combat state or `ControlledByAttack` (14)]**.

### 4.5 C# driving: what it takes and costs **[rt]**
```
C before: Roaming
C AI off: state byte 15, pos (3845.8, 833.0, 1463.8)          +0x498 = 0: state stays 15 for 175 steps
C puppet write cost 3773 ns per entity per tick (GetComponent + 7 writes, C#)
C PlaySequence Vukah attack with the AI off: True
C seq now (1213876837, …) NetworkActor.sequenceRuntimeId 485a4a65      = FNV(sequence GUID), the replicated field
C AI on again: Idle
```
- **Turn off:** one byte, per entity, no hook. `enemy_combat_targets` keeps maintaining `SelectedTargets` and aggro, free perception for your code. `NetworkEnemy.state` freezes at the last value; if client presentation reads it (`link_toggle_state_to_enemy_states`), set it **[hyp]**.
- **Drive:** `ActorInput`: `desiredWorldMoveInput +0x198`, `desiredWorldLookDirection +0x1b8`, `locomotionSpeed +0x1c4` (Zero, Sneak, Walk, Run, Sprint, Swim, Dive...), `defaultAnimationStateId +0`; `DynamicPathWalker` for walking on paths; `Abilities.PlaySequence`, `Cast`, `RunImpact` for actions.
- **Replicates correctly:** position, velocity, `NetworkActor`, `NetworkAnimationGraphInput`, `NetworkHealth`: the same fields the AI's result goes through. kiln.creatures' `kiln_creatures_marks` already handles `MarkWritten` for what Kiln changes (`services/creatures.md`).
- **Not shown:** that `ActorInput` writes produce walking. In the lab the entity is in LocomotionState Falling with no terrain (`vel -20`, `inputVelocity 0`), as expected without a player. On the client checklist.
- **Villagers** have no enable byte. Skip the worker `0x1f7850` per entity with a table the tick publishes (1 hook, 1 per villager per step), or use `NpcCommandEvent` GoTo and Reset (no hook, already in `Villagers`, ignored in state Start).

### 4.6 Hook costs in numbers
- Forwarding detour ~65 ns, through a managed handler ~100 ns (`hooks.md`). Mid hooks ran in the lab on 27 sites at once with no fault: 15,144 calls in 10 s.
- A sensible enemy set: `HandleCondition`, `SelectAttack`, `ConstraintEval`, `ActionExec`, a per-entity gate. With 100 creatures: about 360 calls per step on the hot ones. **About 40 µs per step at 100 ns**, of a 16-40 ms step.
- Handlers on hot functions should be native or a table lookup; `SelectAttack`, `ConstraintEval`, `ActionExec`, `Behaviour rules` are rare enough for C#.
- Install cost: `safetyhook` freezes threads briefly once at load. `hooks.md` notes that two mods sharing a very hot function stall on reload under Wine; a single Kiln dispatcher per target avoids that, so these belong in `kiln.creatures`, not in mods.

## 5. A whole NPC

### 5.1 Minimum components **[data] [rt, server]**
A template is a flat list, and a program runs on whatever matches its required lists, so the minimum is the union for the programs you want. Measured by spawning reduced templates (`Kiln_NpcRe_*`):

| Kind | Components | Result in the lab |
|---|---|---|
| **Puppet** (no AI, no locomotion) | 12: `CurrentTransform`, `Actor`, `ActorInput`, `NetworkActor`, `AnimationGraphResourceComponent2`, `ServerAnimationGraphInput`, `NetworkAnimationGraphInput`, `ServerAnimationGraphComponent2`, `ServerAnimationGraphSlotComponent`, `Velocity`, `LastTransform`, `Faction` | lives, graph handle valid, slots update, sequences drive it |
| **Enemy** | 39: the puppet + `Enemy01BehaviorState`, `EnemyArsenalReference`, `Aggro`, `ClearPathToTarget`, `ClientLocomotion`, `EnemyScaling`, `FleeSensor`, `HitReactionBehavior`, `Locomotion`, `MovementCollider`, `MovementColliderConfig`, `MovementVector`, `Health`, `InertialFrame`, `Level`, `Movement`, `MovementSensor`, `NetworkEnemy`, `NetworkLocomotion`, `PathFinder`, `PathWalker`, `SelectedTargets`, `Stamina`, `SteeringAgent`, `SteeringLocomotion`, `NetworkHealth`, `NetworkLevel` | lives, `hp 119`, state Spawning, as the full Vukah. Decisions, movement and damage on it not exercised |
| **Villager** | 33: the puppet's + `ActiveNpcState`, `ActiveNpcStateSaved`, `NpcState`, `NpcCommon`, `NpcActionSequences`, `InertialFrame`, `InteractionAttachmentGuest`, `InteractionOfferRuntime`, `Locomotion`, `Movement`, `MovementCollider`, `MovementColliderConfig`, `MovementVector`, `MovementSensor`, `NetworkLocomotion`, `PathFinder`, `PathWalker`, `SelectedTargets`, `Shelter`, `SteeringAgent`, `SteeringLocomotion` | lives, `DynamicActiveNpcState` present, its own graph runs (no roster entry: the uniqueness rule only) |
| **Vendor** | villager + `Crafting {workshop, workshopId}`, `CraftingInteraction`, `Inventory`; `NpcDialog` to talk (`creatures.md` 7) | not spawned reduced: `services/npcs.md`'s clones cover it |

These sets are what the server's programs need to run on the entity. A real NPC adds:
- **presence and combat, on both sides:** `Collider`, `ColliderResourceComponent`, `CombatFlags`, `DamageSusceptibility`, `DamageMod`, `Gravity`, `SlopeConfig`, `BuffContainer`, `RandomLoot`...;
- **drawing, on the client:** `ModelResource`, `ModelComponent`, `RenderTransform`, `AnimationGraphComponent2`, `AnimationGraphSlotComponent`, `ClientAnimationGraphInput`, `ClientAnimationGraphInputLerp`, `ClientActor`, `ClientCollider`, `Fade`, `HealthBar`, `UiOffsets`...

Until a client test says otherwise, keep what the game's creature of that kind has in common: the enemy's "in all 538" list is 28 components, "in over 90 %" adds about 70 (`ClearPathToTarget`, `AnimationGraphResourceComponent2`, `ColliderResourceComponent`, `ModelResource`, `HealthBar`, `Debug`, ...).

### 5.2 What can be from nothing, what should be copied **[rt] [data]**
The lab's reduced templates were still **copied values** from the Vukah's and the Blacksmith's. Every value is a plain field, and `ComponentDefaults`-style generation from reflection is possible, but `Enemy01BehaviorState` has hundreds of fields with no documented neutral values. So:
- **From nothing:** skeleton, clips, graph, model, material, root motion, notifies, sequences, Impact programs, arsenal entries, weak spot children, state and input ids.
- **Start from a game template and override:** `Enemy01BehaviorState`, `Locomotion`, `ActiveNpcState`, `HitReactionBehavior`, colliders. Not because they must be, but because good values come from tuning. `kiln.content`'s templates path and `CreatureRef.Ai` work field by field.
- **A hybrid template works today:** one component's data from one creature and the rest from another; a template is a list. Faction, level, scale etc. per entity.

**Attention, skeleton family:** template fields name state ids, sequences name state ids and slots, `HitReactionBehavior` names sequences. A custom graph must either define those state ids or the template must point at its own sequences. Missing a state id leaves the graph in its current state, it does not crash **[rt: `0x5897a0fd`, which our graph lacked, did nothing]**.

### 5.3 Replication: what a Kiln client must have **[refl] [dis]**
- **Must be on every client at boot** (`clients: "mod"`, no sync possible, as for quests): the template, model, materials and textures, skeleton, graph, clips, root motion, notify sequences, actor sequences (client plays its own copy by id), VFX and sounds.
- **Server only:** `Enemy01BehaviorState`, arsenal (`g38_enemy::EnemyArsenalRegistry`, `EnemyArsenalReference` server only), `SubEntities`, all hooks, all brains in C#. No client code runs for behaviour.
- A vanilla client cannot join: unknown template → crash (`gamedata.md`: kept from it). A body-less server-side change on a **vanilla** body stays vanilla-compatible: arsenal rules (Kiln kinds included), AI overrides, hooks.
- The client's prediction world (kind 1) runs some of the same programs for the local player only **[hyp]** (`abilities.md` 8); not creatures.

### 5.4 Saves **[data] [dis]**
- `DoNotSave` on most enemies (527 of 538). Villagers: `ForceSave`, `ActiveNpcStateSaved`.
- A save keeps template GUID, transform and a few components (`docs/re/spawn.md`). A removed mod's entities vanish on load (`services/npcs.md` lab). **So fixed GUIDs forever**, as everywhere in Kiln.
- AI on or off (`+0x498`), private copies and layers are not saved: re-apply on load (`CreatureVariant.Persist`, `EntityData`).

### 5.5 Journal and roster
Unchanged: `services/npcs.md` works on any template with `ActiveNpcState`. Roster cap 64, one per NpcId, `clone: true` already. A custom-bodied NPC there only needs `uiRendering` {model, animation, graph} pointing at cooked resources for the directory portrait **[hyp, unseen]**, and `UiNpcRenderingOptions.defaultAnimation` a state id it defines.

## 6. Corrections to earlier notes
- `creatures.md` 3.1 and `services/creatures.md` "attacks never land" without a player: **in this lab they did**, with two forced-ready Vukahs 1.5 to 2 m apart: `a Dead hp 0, b ReturnHome hp 23`; `x hp 8436 of 10871`. So collision and damage between creatures work with no player where the entities are; it is walking that needs terrain **[rt]**.
- `abilities.md` table: animation graph data "may be baked": a served graph worked; live editing remains untried.
- `models.md` 3: `globalScale` is not always 1.0: 142 of 267 creature models differ.
- `creatures.md` 4: "must match the animation graph" is a data rule of authors, not an engine check.
- kiln.content's `sequences.add` refuses a new StateId GUID: `… is not a loaded keen::ObjectReference<keen::animationgraph::StateId>`. A state id is not a resource: the check must let any GUID through. Diff for its owner in the final report.

## 7. Proof status

| Claim | Evidence |
|---|---|
| Clip codec | `anim.parse`/`build`: 5,461 of 5,461 byte-identical; 21,480 segments; key norms 0.99995..1.0 on the clips checked; `tools/kfc/tests/test_anim.py` (6 tests; `KILN_ANIM_ALL=1` for every clip) |
| Clip semantics | Vukah Attack_01 decoded, skinned with our own LBS on its own model (CPU, numpy, Pillow), drawn: a recognisable swing. The same pose stream through `anim.encode` draws the same **[data]** |
| All creature resources round-trip through the reader | byte-identical: JointAnimation 5,461, ModelHierarchyResource 257, RootMotionAnimation 578, LveAnimation 578, AnimationSequenceContainer 2,896, SequenceResource 2,901, AnimationGraphResource2_0 279, RetargetData 3, BoneMask 9, BlendProfile 1, FeetIKSettings 2, ActorSequenceResource 2,157, arsenal 1 + 1 |
| Inverse bind rule | 249 of 257 skeletons within 0.02 of the inverse chained rest pose; the 8 others not studied |
| Export | `kfc.py clip <guid> --gltf F --model <model>`: skeleton, skinned mesh, sampled animation: valid JSON with 97 joints, 291 channels. Not opened in Blender here (no GPU use) |
| From-scratch skeleton, clips, graph, state id on the engine | server lab, section 2.6 **[rt]** |
| Reduced templates | server lab, section 5.1 **[rt]** |
| Arsenal extension, attack override, AI off | server lab, sections 4.3-4.5 **[rt]** |
| Call rates | server lab, 10 s, 27 mid hooks **[rt]** |
| Signatures | 53 masked prologues on 3 servers + client **[data]** |
| **Not proven** | anything drawn by a client; `ActorInput` producing walking; a skinned cooked model; custom notifies; blend spaces and layers from scratch; a custom body with `Enemy01BehaviorState` actually attacking on its own new skeleton in front of a player |

Reproduce (the method; the scratch tools are not in the repo):
- program table: scan `.rdata`/`.data` for {name ptr, len, `+0x10` a `.text` pointer}: lists at `+0x18` step 0x10 of {ptr to {name, len}[], count}, `+0x180` flags. Lists: `+0x18` template required, `+0x28` template optional, `+0x38` read, `+0x48` optional read, `+0x58` write, `+0x68` optional write, `+0x98` system read;
- the lab test of the cooker: `mods/tests/animcook-test`.

## 8. Manual checks in the client
H1 and H2 are `tools/animcook/samples/clip_override.py` with `mods/tests/clipcheck`; the steps are in `docs/kiln/modding/animations.md`.

| # | Build | See | Tells us |
|---|---|---|---|
| H1 | Override one Vukah clip with `anim.encode` output: read Idle `d099cbe2-…`, add 30° to the head joint's rotation in every frame, `anim.build`, kiln.assets `override`, side both. `/creature spawn Enemy_Vukah_Melee`, stand back | The Vukah idles with its head turned, smoothly, no jumps at 1.05 s intervals (segment joints) | Clip writer on the client's sampler |
| H2 | H1 with a clip that moves an arm off the bind pose by 60° over 2 s, on `Enemy_Fogger_*` | The bent arm bends, the mesh follows | Same on another skeleton |
| H3 | A cooked skinned model on the Vukah's skeleton (needs skin support in modelcook) on a Vukah override | The cooked mesh animates with all the Vukah's clips, no spikes | Skin stream, `skinningMap`, inverse binds on the game's skeleton |
| H4 | A skeleton, model, clips and graph of our own on a clone of the Vukah, Idle + Walk + Run + one attack + Dead | Spawns, idles, walks to you with `Walk`, swings, dies. kiln.log: no `failed to initialize Animation Graph Instance`, no `Could not find description`, no `[model_loader]` | The whole body + animation path |
| H5 | `Creatures.Of(e)` with `+0x498 = 0`, `ActorInput` per tick toward the player | Walks with its walk animation, foot speed matches, turns | The C# puppet's walking; which inputs the graph gets |
| H6 | The arsenal rule of 4.3 on a Vukah near a player, action = heal or chat line | Fires only when your condition holds | Kiln rules in real combat |
| H7 | `SelectAttack` forced to the charge attack (`2`) | Always charges within its range | Override in real combat |

**Expected failures worth looking for:** a culling pop if `boundingBox` is too small for the animated mesh; `globalScale`'s role; hit reactions playing no state on a custom graph that lacks `Hit*` ids; a custom graph's overlay at `0xc6b6f9d4` missing.

## 9. Per block: what to build or hook

| Block part | Data or code | What we build or hook | Needs (cook tool, hook, service) | Size of job | Risk on game update | Confidence |
|---|---|---|---|---|---|---|
| **Body:** skinned mesh from glTF | data | Skin support in `tools/modelcook` (1.1) | modelcook; kiln.assets as is | S (days) | low: format stable since KFC4, 91,904 of 91,909 byte-exact | high offline; client draw unseen |
| Body: new skeleton | data | `animgraph.skeleton_value`, glTF armature to it | `tools/animcook` | S | low | high **[rt server]** |
| Body: materials, textures | data | existing | modelcook | none | low | high (`models.md`) |
| Body: blend shapes, cloth, chains | data, format partly unknown | leave out first; RE blend data when armor needs it | RE, modelcook | M-L per feature, optional | low | medium; not needed for NPCs: 0 of 267 creature models use blend shapes |
| Body: colliders, hit zones | data | JSON → `ColliderResourceData` per mode; weak spot child templates per slot | `templates` writer | S | low: reflection | high data; **[rt]** untested from scratch |
| Body: ragdoll | absent | not built; Jolt from Kiln on the client | client-side physics project | **L-XL**: everything is new | medium | low, and unnecessary |
| **Animation:** clip from glTF | data, codec solved | **`animcook`**: glTF sampler + `anim.encode`, tangents tuning | new tool around `anim.py`; kiln.assets | S-M | low: format unchanged across builds | high **[rt server]** |
| Animation: root motion, LVE | data | Sibling resources from the root's motion | `tools/animcook` | S | low | high format; LVE interpolation not read |
| Animation: notifies | data | Sibling `AnimationSequenceContainer` + `SequenceResource` | animcook | S | low | high format; client effect unseen |
| Animation: graph from scratch | data | `animgraph.GraphBuilder` grown to blend spaces, layers, nesting, conditions; JSON or simple templates | graph writer; node reflection covers it | M | low-medium: 212-byte root unchanged, more node kinds possible | high for state machines **[rt]**; blend and layers same writer, untested |
| Animation: engine input contract | code, fixed | Document and export hashes as constants; custom inputs by GUID | constants in the writer | S | medium: hashes are `.rdata` GUIDs, the kinds may gain entries | high **[dis]** |
| Animation: feet IK, retargeting | data, on 2 and 6 nodes | Later | graph writer | M | low | medium |
| **AI:** tuning | data | Existing `Ai.Set`, `templates`; a block-level JSON | kiln.creatures | S | low: reflection paths | high |
| AI: target choice | code | Hook `post_enemy_combat_targets` or event `AddThreatTargetEvent` | 1 hook; existing command | S | low: unique on all builds | high for the event; hook **[dis]** |
| AI: aggro, sight | code + data | Mostly data. Hook `0x13cab0` to add or block candidates | 1 hook | S-M | low | medium: inner flow only outlined |
| AI: factions, new relations | code | Hook leaf `0x94a690`, table of relations; check inlined copies | 1 hook, more if inlined | M | medium: leaf, 6 callers, inlining can change | medium |
| AI: leash, home | data | Existing `Home`, `EnemySettings`, feature bits | kiln.creatures | S | low | high |
| AI: movement requests | code | Hook `MoveRequest` or write `DynamicPathWalker` | 1 hook | S-M | low | medium: status codes read, replacement untested |
| AI: pathing, body sizes | data + grid code | Set `horizontalRadius`, `height`, node requirements; `usePathFinding` false for giants | template values | S | low | medium: sizes beyond 2 m unseen |
| AI: new path types, custom grid | code | Replace `pathfinding` per entity | large RE of `0x60900b`, cell format | **L** | medium-high: big functions | low; alternative: a mod's own A* writing `ActorInput` |
| AI: flying, swimming | data + code | Existing components, states by id | template values, graph states | S-M | low | medium: not run |
| **Behaviour:** rules from JSON | data | Arsenal writer per block (attacks, rules, constraints, actions as JSON) + registry merge per mod | kiln.content `arsenals` grown to add | M | low | high **[rt: override + rule]** |
| Behaviour: **custom constraints and actions** | data + 2 hooks | Dispatcher in kiln.creatures, `Kiln.Blocks` kinds | 2 hooks, tick-published table | M | low: both unique on all builds | high **[rt]** |
| Behaviour: attack selection | code | Hook `SelectAttack` | 1 hook | S | low | high **[rt]**; second start path untraced |
| Behaviour: transitions, new states | code + table | Hook `HandleCondition` (already mid-hooked), `StateDispatch` | 2 hooks | M | low-medium: table size may change | medium |
| Behaviour: phases, boss logic | data | Existing rule actions | arsenal writer | S | low | high |
| Behaviour: C# brain (per NPC) | code off | `+0x498`, `ActorInput`, sequences, a `NpcBrain` per-tick API | `kiln.creatures` field, no hook | S-M | low: field in reflection (offset by name) | high for off and sequences **[rt]**; walking unseen |
| Behaviour: villager routines | code | Hook `DecideTask` / `SetDecision`, or `NpcCommandEvent` from C# | 1-2 hooks or none | M | low | medium: rates 0 in the lab, needs a player |
| **Whole NPC:** templates per block | data | Component sets of 5.1, defaults from a chosen base | template writer | M | low | high server; client list needs H4 |
| Whole NPC: replication | engine | Nothing new: `clients: "mod"`, boot resources | manifest rule | none | low | high by design; unseen |
| Whole NPC: roster, dialogue | data | Existing `services/npcs.md`; portrait fields | existing | S | low | high; custom-body portrait unseen |

S = up to a few days for one developer, M = a week or two, L = weeks, XL = a project on its own.

**Real costs, in one place:**
- **Hooks:** about 5 for a full enemy brain (`HandleCondition`, `SelectAttack`, `ConstraintEval`, `ActionExec`, `post_enemy_combat_targets`), 2 for a villager; about 40 µs per step for 100 creatures. All server side.
- **The client must have** all body and animation resources at boot; no client hooks for behaviour. Hosted worlds use the same hooks at the client's RVAs.
- **What cannot be verified without the real client:** drawing and deformation, client playback, notifies, cull, overlays, walking on terrain, everything in section 8.
- **Harder than the rest, with the reason:** a custom pathing grid (big functions, a cell format not read, works today with the mod's own A* over `ActorInput` instead), and real ragdolls (no engine code path at all, Jolt bodies would have to be built and driven on the client alone). They are costs, and neither blocks a custom NPC.

### Recommended order
1. **Animation cooker** on `anim.py` + `animgraph.py`: clip, skeleton, state machine graph from glTF. H1 and H2 as soon as it can override one clip. Everything else draws on it.
2. **Skinned bodies** in `tools/modelcook`. H3, then H4: first truly custom creature.
3. **Arsenal writer + the 2-hook dispatcher:** `BehaviourBlock` rules from JSON with Kiln kinds. This needs no client change, so it can also ship for vanilla bodies at once.
4. **`AIBlock`** as JSON over tuning, plus target and move hooks; add the `NpcBrain` puppet API (`+0x498`). H5, H6, H7.
5. **`SelectAttack`, `HandleCondition`** as public override points in `kiln.creatures`.
6. Villager routines, factions, flying and swimming checks; blend shapes and cloth when armor or a modder asks.

### Closest alternatives where something is costly
| Wanted | Costly because | Do this meanwhile |
|---|---|---|
| Ragdoll | no path in the engine, client physics | a `Dead` state with several clips and random pick; Death collider mode |
| Bone-accurate hit zones | colliders stay on Root | child entities on slots with their own colliders, as the game's weak spots |
| A new AI state | table bound | a `CustomBehavior` combat state, or `ControlledByAttack`, driven by rules and sequences |
| Own path grid | large RE | own A* on `FindGround`, `ActorInput`, or `usePathFinding` false |
| New faction relations | leaf + possible inlining | `AddThreatTargetEvent`, per-entity faction writes, guard orders |
