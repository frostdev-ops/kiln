# Action speed: the sequence clock, the animation graph delta, bow timing and the melee sampler's sites

Client Steam build 26430625 (`enshrouded.exe`, sha256 5d9ef2f1...), dedicated server 26430253 (`enshrouded_server.exe`,
f0d824be...), svn 1287369. RVAs are given as C (client) / S (server). Static evidence only: the exe bytes read with
`tools/sigport` (peimg, sigdb_check's resolver), capstone 5, the `.pdata` unwind table, the program descriptors
(`tools/typegen/catalog.py`'s readers, which give each program's update) and the reflected layouts in `sigdb/types/`.
Nothing was run. Kiln's definitions: `sigdb/symbols/{server,client}.d/combat-speed.json`. Checks:
`tools/sigport/tests/test_combat_speed.py`. Service: `native/services/combat/action_speed.cpp`, guide
[docs/kiln/services/combat.md](../kiln/services/combat.md#action-speed).

Input: the Combat And Attack Tweaks author's native notes for their beta.6 (20 sites, both exes). Sites 01-18 are this
lane's (19 and 20, the local input, are kiln.client's). Each of the author's patterns matches exactly once where they say
and nowhere on the other exe where they say it doesn't. Every call target and caller they name checks out. The two
corrections are in the table: site 17's stock bit test and site 18's displacement.

## Sites

| # | Kiln symbol | C | S | Agrees | Evidence |
|---|---|---|---|---|---|
| 01 | `speed/SeqClock` | 0x21A83F | - | yes | In the per-actor sequence advance 0x21A800 (`speed/SeqAdvance`). Both branches before it (0x9BC010 or 0x9C4DA0, by the result of 0x9D1A00) return a pointer to the step's elapsed time in a stack slot (`[rsp+0x60]` or `[rsp+0x78]`, the helper's home area). `mov rbx,[rax]` then loads it, and 0x18FF30 advances `[rdi+0x180]` by it. At the site: rax = that pointer, rbx = the row accessor (the helper's rdx), rdi = the ActionSequence (rcx). With 7 pushes and `sub rsp,0x20`, the return address is at `[rsp+0x58]`. |
| 02 | `speed/SeqClock` | - | 0x377DC | yes | 0x377A0, the same helper. rsi = the ActionSequence; rdi is the delta the next instruction loads. 6 pushes and `sub rsp,0x28`: the return address is again at `[rsp+0x58]`. Before the time arithmetic, the server version compares the times against ±INT64_MAX sentinels, so Kiln leaves sentinel values alone. |
| 03 | `speed/SeqServerRet` | 0x21A6EE | 0x3768E | yes | The E8 at match+6 targets 0x21A800 / 0x377A0. It sits in 0x219EE0 / 0x36E80, whose only caller is 0x274B7A / 0x86C7A in actor_simulation_start's update 0x274620 / 0x86720 (program descriptor). |
| 04 | `speed/SeqClientRet` | 0x2656C6 | - | yes | The E8 at match targets 0x21A800, in actor_client_play's update 0x2653B0 (client-only program, flag 1). The helper has exactly two callers on C (03 and 04) and one on S (03). Checked by a rel32 scan of `.text`. |
| 05 | `world/CurrentEntityId` | 0x9BF9E0 | 0x65B870 | yes | Already Kiln's: `world/CurrentEntityId`, also used by kiln.combat's falls, kiln.rules, quests and dyes. `uint32* (accessor, uint32* out)`. |
| 06 | `speed/GraphBind` | 0x286080 | 0x97D70 | yes | In the binding wrapper 0x286040 / 0x97D30 (`speed/GraphWrap`), after 0x93ACB0 / 0x773B90 found the instance. rax = the instance, rsi = the wrapper's r9 (the input payload), `[rsp+0x50]` = its fifth argument (the state id), `[rsp+0x28]` = its return address. 0x93F970 copies the payload into the instance (`+0x1B8`, 0x210 bytes) and keeps the state. The wrapper doesn't touch rbp, so the caller's frame is intact. |
| 07 | `speed/GraphServerRet` | 0x34AC1F | 0x10A27F | yes | The E8 targets the wrapper, in server_update_animation_graph_2's update 0x34A6E0 / 0x109D40. The program keeps the row's entity at `[rbp-0x7C]`: `call world/CurrentEntityId; mov ecx,[rax]; ...; mov [rbp-0x7C],ecx` at 0x34A9FD / 0x10A05D (`@speed/GraphServerActor`). |
| 08 | `speed/GraphClientRet` | 0x307BAA | - | yes | The E8 targets the same wrapper, in client_update_animation_graph_2's update 0x307580. The entity is at `[rbp+0x14]` (0x307913, `@speed/GraphClientActor`). The wrapper has exactly these two callers on C and one on S. |
| 09 | `speed/GraphEval` | 0x92ED10 | 0x7673F0 | yes | The evaluator's entry. With `r8b == 0` it stores xmm3 into the instance at `+0x54C`, otherwise it loads `+0x54C` into xmm6 (`@speed/GraphEval.cache`), so scaling xmm3 on the first pass scales both passes. It is reached from a job, not from the wrapper: 0x947120 (no direct caller, a function pointer) calls it with `r8d = 0`, rcx = the instance and `xmm3 = min(prepared delta, cap)`. The other callers are 0x946F50 and 0x9477C0. So the binding is matched by instance, not by thread. |
| 10 | `speed/BowTiming` | 0x2EBE15 | 0xD7DB5 | yes | In server_player_ranged_weapon's update 0x2EBC70 / 0xD7C10. `lea rcx,[rbp+0x1B0]; call 0x476700 / 0x20EF80` builds `keen::ranged_weapon::WeaponParameters` (weaponType u8 +0, chargeDuration +8, fireDuration +0x10; reflected layout) and returns it in rax. The site is the next instruction, which copies the 32 bytes to `[rbp+0x238]`. r12 = the update's rcx (the row accessor). The parameters reach the ranged state machine 0x478490 (C), which latches them into its own copy (`+0x18`) only on a state change: from Holster, or to a lower state (0x4785DB). So a draw in progress keeps the durations it started with, as the author says. |
| 11 | `speed/HandSlots` | 0x34AF4B | 0x10A5AB | yes | server_update_animation_graph_slots_2's update 0x34AC90 / 0x10A2F0, the end of a row before `world/IterNext` (the E8 targets 0x9C2780 / 0x65E610, Kiln's `world/IterNext`). r15 = the accessor. |
| 12 | `speed/SlotToWorld` | 0xF80010 | 0x81C080 | yes | Function entry. 12+ direct callers on C (slot attachments everywhere), 8 on S. |
| 13 | `speed/ColliderCreate` | 0x3F741B | 0x1986BB | yes | sequence_event_collider's update 0x3F7100 / 0x1983A0 (descriptor flags 0x200). The E8 targets 0x97F810 / 0x61B6A0, followed by `jmp` over the move branch. |
| 14 | `speed/ColliderMove` | 0x3F74B0 | 0x198750 | yes | The same update. The E8 targets 0xA21D70 / 0x6BE620. |
| 15 | `speed/CastShape` | 0x9A6CC0 | 0x642B50 | yes | Function entry. Two callers on each exe (C 0x331A40 and 0x33D920). 0x331A40 also calls site 18's getter. |
| 16 | `speed/ContactWriter` | 0x9A7B30 | 0x6439C0 | yes | Inside 0x9A6D00 / 0x642B90: `mov rdx,[r13+8]; ...; inc rdx; cmp rdx,[r13+0x10]`, the list's count against its capacity. |
| 17 | `speed/ImpactContacts` | 0x123ED53 | 0x995C33 | partly | In 0x123EA50 / 0x995930, whose only caller is impact_prepare's update 0x3F6950 / 0x197BF0. **The stock instruction at the selected address tests bit 1 (`test byte [rsi+0x1F3], 2`), not bit 0.** The author's own condition on bit 0 of +0x1F3 is their logic, so check it against the game's when the sampler is written. |
| 18 | `speed/CollisionBackend` | 0x9CFFA0 | 0x66BEF0 | yes (their fix) | `cmp byte [rip+disp32],0; sete al; ret`. The flag is at the 7-byte instruction's end + disp32: 0x283F334 / 0x14DB314. beta.6 read +6, the neighbouring byte, which the author reports themselves. Kiln names the getter, not the byte: a sigdb rel32 hole counts from the hole's end (+6) and would repeat the beta.6 bug. Six callers on each exe. |

The mid sites (01/02, 06, 09, 10) were checked: the 5-byte jump covers whole instructions, none of them RIP-relative,
and no branch in the containing function lands inside the relocated bytes. At 0x21A83F the first instruction is
itself a branch target (`jmp 0x21A83F` at 0x21A833), which is fine because the jump lands on the hook's start.

## Programs, worlds and how Kiln tells the runs apart

| Program | C update | S update | Flags (C) | Runs in |
|---|---|---|---|---|
| actor_simulation_start | 0x274620 | 0x86720 | 0 | server world (authority), client prediction storage |
| actor_client_play | 0x2653B0 | - | 1 (client) | client world (presentation) |
| server_update_animation_graph_2 | 0x34A6E0 | 0x109D40 | 0 | server world, prediction |
| client_update_animation_graph_2 | 0x307580 | - | 1 | client world |
| server_player_ranged_weapon | 0x2EBC70 | 0xD7C10 | 0 | server world, prediction |

None of the three shared programs has flag 0x1, 0x10 or 0x40, so kiln.world can put a PREDICTION wrap on each
(`systems.wraps.prediction`). CATT tells authority from prediction with Kiln's managed `Systems.Wrap(WorldKind.Prediction)`
around the stock call, plus return-address filters. kiln.combat.speed uses the same wraps natively
(`game_program_wrap` with SERVER, PREDICTION and CLIENT stages on these five programs). A BEFORE stage marks the thread
with the world kind, the program and the step. The AFTER stage clears it. A stage runs on the thread that runs the
slice, around the game's update, so a mid hook inside the update sees the mark of the run it is in. A hook that finds
no mark scales nothing. Each helper has exactly the anchored callers above, so the program mark identifies the path.
The anchors stay in sigdb as resolve-time proof of that: their rel32 must name the hooked helper or the feature is off.

## What Kiln does differently from beta.6, and why

- **Classification (the "routes").** beta.6 routes by SequenceRuntimeId plus known graph states and overlays, which are
  game-data constants in the mod. Kiln classifies the running action from its own trigger context, read through the
  game's reflection (`keen::actor::ActionSequence`: `triggerContext.triggerType` +0x18, `triggerContext.usedItem.itemId`
  +0x38; `playbackContext.currentTime` +0x180, which equals the clock site's captured displacement), and the used item's
  `EquipmentSlotType` from kiln.items' catalog:
  - MeleeWeapon with Primary, Primary_Tap, Primary_Hold or Heavy is Melee;
  - Tool with the same triggers or Tool is Tool;
  - RangeWeapon or MidRangeWeapon with a Primary trigger is Bow, but only while the actor's ranged weapon type at site 10 is Bow (0), so staffs and wands are never Bow.

  Everything else (blocks, dodges, spells, eating, interactions) keeps its timing. A guest has no item catalog (it is read
  from the server world's item registry), so the server sends its classified item list over kiln.net.
- **Graph scaling** applies while the actor's classified action runs (recorded by the sequence hook in the same or the
  previous step of that world), to the whole graph instance of that actor. beta.6 confines it to matching graph states
  and overlays. The difference: legs keep the rate while a player moves during an attack.
- **Bow:** as beta.6, the fresh, skill-adjusted durations are divided at site 10, only for weaponType Bow.

## Fragile places

- **Site 09** is the evaluator of every animation graph in the world (players, creatures, NPCs). When a rate is set,
  each evaluation costs a lookup in a small instance table. When none is set, the links aren't installed at all.
- **The evaluation runs later on a job** (0x947120), so the binding is matched by instance pointer. An instance freed
  and reused by another actor's graph is cleared at that actor's next bind, which happens before each evaluation, and
  entries older than 250 ms are ignored.
- **Prediction and presentation compare the row's entity with kiln.client's local player.** On a host the ids are
  shared (kiln_client.h). For a guest, the same id in the prediction and client worlds is unverified in a real game. If
  it differs, the guest's own swing stays at server timing, which is the documented fallback.
- **The register contract differs per exe** (rdi vs rsi). It is captured from the instruction's ModRM
  (`speed/seq.modrm`, fixed 0xA7 / 0x86), and the service accepts only those two bases.

## Not verified here
- That the elapsed time at 01/02 is the only input to event evaluation for the step (the author's emulation says so; a
  lab run of the server shows it on authority).
- The sampler's runtime layouts (sites 11-17, the 0x60-byte BoxColliderData, CollisionEvent 0x40, the hand slots):
  the author's notes, not re-derived here. The sampler is not in this release.
