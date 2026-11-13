# Carry, riding, flight and moving decks (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; old server O svn 1273043 `d7baa768…` only where given. RVAs, Win64 ABI.
Source: dev.28 dossier chapters 19a–19c, riding REPORT/VERIFY including Session-5 claims. Original 22 claims:
18 confirmed/four corrected, plus a second flight-store path missing from the initial plan. Later run 7 exercised
both stores and data-driven animation overrides. `[seen]` is coordinator-run sessions 3–5, not new testing here.
Movement/steering ownership and pet handoff are in [creatures.md](creatures.md) §15; camera in [client-render.md](client-render.md),
fall detection in [players.md](players.md). No production mounts/vehicle service is implied by these notes.

## Carry transforms and scale [static] [refl] [lab]

| Function | S / C RVA | Contract |
|---|---|---|
| server_entity_attachment | `0x108a50` / `0x3493f0` | `void(ProgramContext*)`, flags 0x200; other-read target CurrentTransform, tracked own transform write |
| apply Offset | `0x6017d0` / `0x965bc0` | `void(WorldTransform*,const Offset*)`; rotate local/add world offset, compose orientation, leave scale |
| ServerSlotAttachment compute | `0xe3190` / `0x324540` | `void(ctx,iter*)`, engine-owned access; slot/fallback behavior below |
| server_slot_attachment_write | `0x109780` / `0x34a120` | if updated, copy attachment transform (position only when ignoreOrientation), clear updated |
| interaction_attach_guest | `0xa4a00` / `0x2a7590` | scheduled seat handling: **one Teleport**, not continuous carry |

EntityAttachment 44 B/hash `0x73d5ab7d`: target EntityId `+0`, Offset 40 B `+4` (local float3 `+0`, world float3 `+0xc`,
quaternion `+0x18`). It copies the target's **whole transform, including scale**, then applies offset. Missing target
leaves the attached entity rather than providing a general automatic cleanup guarantee.

ServerSlotAttachment 112 B/hash `0x430f162d`: Offset `+0`, target mode byte `+0x28` (FirstValid 0/Root 1/Parent 2),
slot byte `+0x29` (36 slots, Root 0..Generic19=35), ignoreOrientation `+0x2a`, target EntityId `+0x2c`, computed
WorldTransform 56 B `+0x30`, updated `+0x68`. SlotAttachment 48 B/hash `0x09561436` is the client render twin without
computed transform. Target id 0 resolves through parent. Queue components through a declared ECS program, not offer-only
component_change. Do not call these update/compute entries with fabricated or retained contexts.

**VERIFY correction:** scale=1 only on the **animation-slot path**, when target ServerAnimationGraphSlotComponent
and requested bit exist. Missing target storage returns. Without slot data, targetMode!=Root returns without general
removal; Root fallback copies the whole CurrentTransform **including scale**, with narrower parent/removal branches.
Thus unrigged scaled bodies do not get the slot path's scale protection. A Root **joint** may also differ from entity
orientation: sabertooth Root was rotated 180°; two seat z offsets appeared on the opposite side until converted.

[lab] Runtime add/remove of both carry kinds on a spawned player worked. At 7 m/s with 90° turn, 60 steps, entity/Root-slot
same-step error 0.0000 m; previous-step error up to 0.6606 m. Animated Chest slot bobbed relative to entity. Mount teleport
was followed same step; teleporting a still-attached rider was undone. EntityAttachment inherited scale 0.5;
rigged slot carry kept rider scale 1. Two slot seats survived a turn; removing one left the other attached.

Rider physics remains live: velocity reached 29–42 m/s, causing a fling after detach. Per-step zero velocity plus
Gravity.isActive `+0x10=0` kept |v|<=1.25 in that fixture. Carry can pull rider through terrain; collider disable did
not make it collision-safe. Conversely a seat **inside mount body blocks climb**: .3 dragon seat 1.8 m, requested 6 m/s
up but 0 m rise/1.5 s (even with rider collider disabled); seat 2.6 m rose 9.1 m. Scale-aware seat clearance is necessary.

[lab] Registry bits EntityAttachment/ServerSlotAttachment/SlotAttachment all unsaved, replication bit clear;
CurrentTransform saved/replicated; Gravity/MovementCollider replicated. **Bits do not settle derived transfer.**
Sessions 3/4 actually observed EntityAttachment on client-world rider, and session 4 prediction rider too. That route
was not explained by the bit census; do not conclude all carry components always arrive or never arrive.
Real-player character save/restore of carries remains untested; synthetic player bodies do not prove it.

## Input, animation and possession [static] [refl] [lab]

Steering overwrite/removal contracts are in creatures §15. Engine order: steering_locomotion → locomotion_state →
locomotion_update_velocity → locomotion_execution/flying step → gravity/velocity/rotation → collider resolution →
entity/slot carry. Kiln's appended write programs do not retroactively change earlier consumers in the same step.

Actor raw Sitting bit 9 persists and prevents own locomotion, while stick still arrives. It is **not a sitting pose**.
PlayerInput stick `+0x2c0`, camera quaternion `+0x40`, held flags `+0x3b0` after mask `+0x3a8` (Jump 32/Sprint 53/
Sneak 55). Building-fly program S `0xcd080` copies full 456-byte ActorInput into the spark then zeroes player's move;
this is existing possession, not a ready predicted-mount implementation. predictEntity=true on player/spark, false
on dragon/vulture and surveyed creature bodies.

Locomotion animation S `0x1d7b90` / C `0x4382d0` uses computed speed and threshold **hysteresis**, not a direct
selector→state table. Earlier writes to ActorInput.defaultAnimationStateId, enemy state's animation fields and
ServerAnimationGraphInput `+0x22c` were overwritten. The alternate **data path was later proven**: DynamicNetworkLocomotion
64 B currentIdleAnimation/currentWalkAnimation `+0x30/+0x34`, written each step, made the .3 dragon use Fly_Glide at
Run/Walk and Idle_Grounded at idle. No native animation-selection hook is required for that tested override.
Movement attribute slots 3/4/5 control walk/run/sprint; a walk-speed write increased 40-step travel 3.8→18.2 m.

## Flying step and both velocity stores [static], overrides [lab]

`void FlyingStep(iterator*)` S `0x1d16c0` / C `0x431e20`, called from locomotion_execution S `0x1dcf90` / C `0x43d6d0`.
Iterator Flying* `+8`, DynamicFlying* `+0xc8`, Velocity writable record `+0x158`; early return if components absent.
Flying states 0..8: None,NotFlying,Flying,Flapping,Hovering,StartFlying,StopFlying,Fall,StuckOnGround. Flying actor flag 39
causes locomotion state 3. It keeps Y in normalized input and approaches requested velocity with maxAcceleration,
unless rootMotionMode bit 0. A Post Velocity write alone is overwritten by this mover.

| MID | S / C / O RVA | Register contract |
|---|---|---|
| mounts/FlyVelocityStore, nonzero input | `0x1d224c` / `0x4329ac` / `0x1cc95c` | RAX=Velocity*, XMM6 low XY, EBX=Z float bits, RSI=iterator |
| mounts/FlyVelocityZeroStore, near-zero input | **`0x1d214a`** / `0x4328aa` / `0x1cc85a` | same; native zero branch skips first site |

Both are after flying/root-motion guards and write-record marking S `0xe2b40` / C `0x323ef0`. **Cover both** for
release-to-glide/momentum; nonzero-only hook misses release. `FlyApproach` S `0x1c0370` / C `0x420b90` reconstructs as
`float3*(out,const float3* v,const float3* target,float maxAccel,float k,float dt)`, bounded acceleration helper;
inspection anchor, not qualified arbitrary-thread call. Preserve native guards and exact floating-point register lanes.

[lab] Run 7: Zero input+override(0,6,0) rose 9.93 m/40 steps, site 2 hits 40/site 1 zero. Nonzero input+override(0,0,12)
moved 20 m/40 steps, site 1 hits 40, despite native inputVelocity(30,0,0). Hook off restored native zero. This settles
VERIFY's initial missing-zero-store blocker, not a complete momentum controller or client prediction implementation.

Flying template 176 B: maxAcceleration `+0`, start/stop/fly/hover/flap/hit-air/stuck sequences
`+4/+0x14/+0x24/+0x34/+0x44/+0x54/+0x64`, flapSetup `+0x78`, animationHandling/fallOnHit `+0x98/+0x99`,
flyAfterSpawn `+0xa9`. DynamicFlying 32 B: state `+0`, next flap Time `+8`, paused/allowed/fall-on-death
`+0x18/+0x1a/+0x1b`. StartFlying/StopFlying/EnterFlyingStateEvent 16 B: Time `+0`, targetId `+8`.
Start/Stop were lab-executed; original run 2 **did not emit Enter** (already flying). Stop high above ground caused a
fall, not landing. Start played lift-off/root-motion then returned controllable flight. Later session 5 reached grounded
after controlled descent; that is not proof every Stop-at-altitude is a safe landing or plays the graph's landing clips.

## Fog Dragon and client sessions [data] [lab] [seen]

Fog Dragon `2a96cb70-4c58-42d9-82b1-2b3163ca0502`: Flying maxAccel 100/start-only/flyAfterSpawn, Gravity inactive,
PathWalker.usePathFinding=false. Graph `a5a7f877…`, 172 joints, 36 mapped slots including Generic00–19; fly/hover/turn/lift-off/
landing/ground-idle states exist, **no ground walk and no Run state**. Live hover vultures also have start-only Flying
sequence config (older birds' fuller sequences are not representative). A mount clone must not inherit boss healthbar,
music/phases, kill knowledge, marker or combat camera as if those were riding features. Stock dragon camera profile
only covers combat modes 5/6/14 (client-render note), not size-aware mount following.

- **Session 3 hosted:** with steering present, 0 m; removed, visible sabertooth Walk and 90° turns. Alive run B carried
  drawn rider at (0,1.4,0) exactly; stock client attachment already aligned it, render pin no improvement. First run's
  character had died in Shroud before rides: its later stick/delay data is invalid. Timing included measurement window/
  acceleration; it is not a pure network latency benchmark.
- **Session 4 joined Kiln and vanilla:** native EntityAttachment rider followed sabertooth; joined unpinned spread
  <=0.0016 m. Pin did not improve camera. Both clients drew rider **upright/standing**, despite Sitting condition. This
  establishes an unscaled known-body carry result, not all scaled/slot paths.
- **Session 5 joined, .3 dragon, slot carry 10–25 m/s:** server offset always (0,2.6,0), but all three attachment lookups
  on **client-world rider** were absent while RenderTransform reads worked. **Prediction presence unresolved**: every
  prediction read failed; probe conflated absent/unavailable/fault and never queried prediction SlotAttachment.
  Unpinned rider trailed/lagged (up to 0.96 m frame jump); render pin aligned drawn pose exactly, not camera. A product
  prediction-aware pin was not tested. “Start gliding” prompt persisted; Space sent Jump+Glider, server stayed Sitting.
- Original outdoor death lacked fall telemetry: **strongly consistent** with fall damage, not directly captured.
  Dragon was still sinking 3 m/s as horizontal speed reached 0, not levelled out. Controlled mortal-rider reproduction
  then proved 25.50 m lethal game fall. Combined four-field reset+fall-event cancel kept HP 100/100 in lab and 313/313
  on 671 outdoor rows; reset-only/event-only not established. Full detection conditions are in players note.
- Stock camera repeatedly sat inside/under body. **Cause not isolated**: mount collision versus too-short boom/pivot
  remains inferred (camera chapter corrects riding's stronger wording). Render pin did not steady it; size-aware
  renderer-only camera/profile was still untested. No claim of an arbitrary vanilla profile transport follows.

## Moving decks [static] [lab]

MovingPlatform collider's slot writer S `0x109340` / C `0x349ce0` copies computed transform and sets
InertialFrame.lastDeltaPosition=(new−old) in metres; inertial_frame_world S `0x8b530` resets frames first.
Player MovementCollider.results.attachmentEntityId selects platform frame in movement resolution. This is a standing
passenger path, **not seat carry**. A prototype 4 m deck slot-attached to sabertooth carried a spawned player through
60 walking steps/turn with horizontal drift 0.020 m and matching inertial frames. Owner prediction/deck replication and
a new Kiln-cloned moving collider remain untested. A boar with enemy state+SteeringAgent removed moved 9.77 m/40 steps
and took damage 99→89: a no-AI **locomotion body**, not the old no-locomotion 12-component puppet or static prop.

## Signature scope [static]

Scratch mounts/* anchors resolve on S/C/O: SteeringLocomotion `0x112f00/0x3538a0/0x10e970`,
EntityAttachmentUpdate `0x108a50/0x3493f0/0x104590`, SlotAttachmentCompute `0xe3190/0x324540/0xdf290`,
LocomotionAnimation `0x1d7b90/0x4382d0/0x1d22a0`, FlyingStep `0x1d16c0/0x431e20/0x1cbdd0`, plus both stores above.
Captures tpl/dyn/velocity 8/0xc8/0x158. Momentum scratch feature also depends on existing rules/CurrentEntityId;
standalone fragment being off is not a failed store match. No production sigdb change here. Full controller, leases,
boarding/dismount, ownership, flight equations and vehicle persistence designs remain in dossier 19a–19c.
