# kiln.world: engine evidence (server svn 1285601)

Build: dedicated server, Steam build 26404334, svn 1285601, exe sha256 `c5c9fe50…`, image base `0x140000000`. Addresses
are RVAs. Sources: EMF 0.3.3 (MIT) `docs/engine_parts/re_custom_programs.md`, `re_teleport.md`, `ENGINE_MAP.md` and its
signature table (`sigdb/evidence/emf-0.3.3-signatures.json`), re-checked here by disassembly (capstone over the PE image,
`tools/sigport/peimg.py`) and then live in the `world` lab (a copied world: base, villagers).

Everything below is captured at boot by `sigdb/symbols/server.d/world.json` (typed holes), not hard-coded. The same
file resolves on the old build 1273043 too, with that build's values (`programs.count` 0xE15350, stride 0x1F20, slice
entity list +0x828 / count +0x830), which is the point of capturing them.

Tags: **static** = read in the code at the address given; **live** = seen in the running lab.

## Programs

| What | Value (1285601) | Where / how | Tag |
|---|---|---|---|
| `RegisterAllPrograms` | `0x245960` (EMF sig 18 unchanged) | one caller, `0x2381f6`: the **last** call of the server world setup (`0x233ee0`); 757 `RegisterProgram` calls, each `call getDesc; mov rdx,rax; xor r8d,r8d; mov rcx,rbx; call RegisterProgram` | static |
| `RegisterProgram` | `0x67ede0` (EMF sig 3) `bool(World*, ProgramDesc*, bool disabled)` | skips desc flag 2 (`test byte [rdx+0x180],2`), linear duplicate-name scan, `cmp ax,0x380` full check, zeroes the entry and fills it | static |
| program count | u16 `world+0xE16F50` (was `0xE15350`, **+0x1C00**) | `66 3B B9 {count}` / `0F B7 86 {count}` / `66 FF 86 {count}`, 3 captures agree | static |
| table capacity | `0x380` (896) | `41 BE 80 03 00 00; cmp ax, r14w` | static |
| table / stride | `world+0x746350`, stride **0x1F28** (was 0x1F20) | `imul rbx,rax,0x1F28; add rbx,0x746350`; entry `+0x00` desc*, `+0x08` u16 id (index+1), `+0x10` name, `+0x20` flags, `+0x24` interval | static |
| name map / init list | `world+0xE17660` / count `world+0xE17658`, list `+0xE16F58` | same function | static |
| "may finalize" | u8 `world+0xE17958` | `FinalizePrograms` `0x6c2500`: `cmp byte [rcx],0; jne; cmp byte [rcx+0xE17958],0` (setter `0x69de40`) | static |
| engine vanilla count | 817 programs | game log `[ecss] Programs: 817` (lab2, vanilla) | live |
| with kiln.world | 821 (kiln_world + 3 self-test programs) | `[ecss] Programs: 821`; kiln.log `program kiln_world registered as #818` | live |

### Descriptor (0x188 bytes, unchanged)
Checked against the game's own static descriptors (in `.data`, returned by 757 one-instruction getters): e.g. `teleport`
at `0x1344550` = write `CurrentTransform, LastTransform, Teleport, TeleportCount`, write-opt `LerpTransform,
MovementCollider, ServerSubmerge, Velocity`, sys-read `ecs::ColliderSystem, ecs::PathGridSystem, ecs::VoxelGrid`,
other-read `PlayerSpawnPoint`, flags 0x10, interval 1. Field reads in `resolveAccess` `0x69ef00` are at exactly the EMF
offsets (+0x18 … +0x170); `RegisterProgram` reads flags `+0x180`, interval `+0x184` (both captured; kiln.world refuses to
register if they move). Struct: `native/services/world/world_internal.h` (`EngineDesc`, static_asserts).

### How names resolve at finalize (why kiln.world validates first)
- `resolvePrograms` `0x69eb20` → `resolveAccess` `0x69ef00` per program. **It stops at the first list that fails**
  (`test al,al; je 0x69f29c` after every list), leaving the later masks empty; the return value is ignored by the caller.
- Component lists: `resolveComponentList` `0x61e3b0(bits, registry, list, dynamic)`: map = `registry + (dynamic ? 0x88 :
  0x30)`, key = `componentHashFromName` `0x6595d0` = FNV-1a32(`"keen::ecs::"` + name, max 0x100); missing → logs
  `Could not find component` and returns false. read/read-opt/write/write-opt/all-read/all-write use the dynamic map
  (`+0x88`); tpl/tpl-opt/with/without/any-of use `+0x30`; other-tpl uses `+0x30` inline.
- Singletons: `0x6264f0` hashes the raw name (FNV-1a32, no prefix) into the map at `registry+0x7459D0`; a missing one
  is **skipped** and the function still returns true (the plan later marks the program skipped, entry flag 0x20).
- Events: `0x626190`, FNV-1a32(`"keen::ecs::"`+name) into the map at `registry+0xE16DB8` (was +0xE151B8, +0x1C00);
  missing → false (abort).
- So kiln.world looks every component and event name up in the same maps before calling `RegisterProgram` and refuses
  the program if one is missing (live: `program kiln_world_selftest_bogus refused: read: unknown component
  'NoSuchComponentKiln'`); a missing singleton only warns; after the first step it reads entry flag 0x20 and reports
  `KILN_PROGRAM_SKIPPED`.
- Slot order = bit order of the access bitsets = the u16 values from those maps. kiln.world lays slots out by name
  (`"keen::ecs::"+name`, byte order) at `register_program`, recomputes them from the real type indices at world
  registration, and refuses the program if they differ (never seen).

### The update and its iterator
- Slice runner check (`0x64ab18`): after `call [entry+0x28]` it reads `ctx.index` (`ProgramContext {J*, u32 index}`); 0 is
  fine, otherwise `index < J+0x838` → fatal `Not all entities were updated in program '%s'` (`0x64abcf`). kiln.world
  sets `index = J+0x838` after a fault mid-iteration and disables the program.
- `IterBegin` `0x663b30` (EMF sig 15 unchanged; the size argument is ignored). Group counts it reads from J (captured):
  T u8 `+0x4B0`, R u16 `+0x4B8`, W u16 `+0x4BA` (records 0x30 bytes), singletons `*(J+0xC0)` count u8 `+0x400`, other-tpl
  u8 `+0x7F8`, other u8 `+0x7A8`, events-in u64 `+0x1A8`, events-out u32 `+0x1B0`, in that order after the ctx slot.
  kiln.world compares these with its own counts on every update (a mismatch disables the program).
- `IterNext` `0x65e610` (hand port: `cmp rax,[r8+0x838]`), `CurrentEntityId` `0x65b870` (`J+0x830` list, 16 B/entity).
- Iterator size formula checked on the game's `teleport` update (`0x916a0`, `mov r8d,0x1B0`): 8·(1 + R1 + 6·W8 + S3 +
  O1) = 0x1B0; its write records sit at consecutive 0x30 strides in name order (CurrentTransform, LastTransform,
  LerpTransform, MovementCollider, ServerSubmerge, Teleport, TeleportCount, Velocity).
- `MarkWritten` `0x66f040` (EMF sig 22) called as `(rec[5], rec[1], rec[2], rec[3], u16 rec[4], u8 rec[4]>>16)`
  (teleport update `0x917f2`).
- Other-entity accessors: `planOtherAccessors` `0x64c670` appends `typeinfo + 0xB2` (stride 0x100) per set bit, i.e.
  per list in type order, at most 0x5D. `ReadOther` `0x64e9e0` = `GetComponentByType` `0x64ea80(out, *(J+0x30),
  accessor /*u16* type*/, entity)` (`JobStorage` `0x65ba10` = `mov rax,[rcx+0x30]`).
- live: self-test programs (each-mode read `LastTransform, CurrentTransform` + optional `TeleportCount, NpcCommon`; raw
  mode `CHUNKED`; SYSTEM with singleton `ecs::PathGridSystem` + other-read `CurrentTransform`) compared every slot with
  `GetComponent`: 1208/1208, 726/726 and 121/121 matches, 0 differences.

## Command buffer

| What | Value | Where | Tag |
|---|---|---|---|
| cmdbuf | `*(J+0x08)` | `0x659270` = `mov rax,[rcx]; mov rax,[rax+8]; ret` (called by the game's teleport program) | static |
| `QueuePush` | `0x5f9f60(cb, cb+0x10, u8 kind, u32 size, u32 entity)` (EMF sig 49) | 16-aligned; capacity check `cmp rax,[rdx+8]` → NULL; header `+0 u16 size, +2 kind, +3 0, +4 u16 cb[0x28], +6 u16 cb[0x2a], +8 entity`; zeroes from +0xC; bumps count `+0x14` and used `+0x10` **after** filling (on this build) | static |
| kind 3 add | size `0x6C + n`, `+0xC` hash, `+0x4C` u16 size, data `+0x6C` | `actor_teleport` `0x86f6c..0x86fa1` (Teleport: size 0xBC, 0x50) | static |
| kind 4 remove | size `0x4C`, `+0xC` hash | game teleport update `0x917c1` | static |
| kind 0x10 checkpoint | size `0x10`, `+0xC` = player entity | helper `0x5fbf80` | static |
| live | `queue_add` of a Teleport onto prop 3825467744 → present after the step; `queue_remove` → gone after the next | self-test | live |

## Entities

| What | Value | Where | Tag |
|---|---|---|---|
| `GetComponent` | `0x64ed50(out{ptr,size}, world, hash, entity)` | registry = `*(world+0xE179C0)`, map `registry+0x88` (captured) → u16 type → `GetComponentByType` | static |
| entity map | `storage+0x148` (storage = `world+0xE179C0`) | `GetEntityTransform` `0x64fd40` and `GetComponentByType` (2 captures); cap `+0x158`, values `+0x188` | static |
| keen hash map | bits `+0x08`, cap `+0x10`, keys `+0x28` (u32), values `+0x40`, count `+0x54` | derived from the entity map (cap, values) + the singleton map's inline fields, cross-checked with the event map's | static |
| entity record | `+0x18` layout, `+0x20` chunk, `+0x30` index; layout bitset `+0`, offsets `+0x84`, strides `+0xA84` | `0x64ea80` | static |
| `GetEntityTransform` | `0x64fd40` → `0x5ea2a0`: CurrentTransform (`0x027ac564` looked up in the `+0x30` map) | static |
| live | 2818 entities; `find(TeleportCount)` = 6 (0.43 ms incl. a one-time cross-check of 256 entities with GetComponent: `find: archetype bitset agrees with GetComponent`); `find(NpcCommon)` = 8 | live |

## Teleport

| What | Value | Where | Tag |
|---|---|---|---|
| component | `Teleport` hash `0x23384d40`, 80 B: target WorldTransform @0 (i64×3 32.32, quat @0x18, scale @0x28), targetEntityId @0x38, applied @0x48, searchBestSpawnPosition @0x49 | game `teleport` update `0x916a0`: `cmp byte [rbx+0x48],0` → kind-4 removal; `mov ebx,[rbx+0x38]` → `ReadOther(PlayerSpawnPoint)`; `cmp byte [rax+0x49],0` → ground search; `mov byte [rax+0x48],1` | static |
| move | 56-byte copy into CurrentTransform and LastTransform.transform, `inc byte [TeleportCount]` (`0x91cbf`), lastTeleportTime @+8 | same | static |
| program #701 needs | write `CurrentTransform, LastTransform, Teleport, TeleportCount` (desc `0x1344550`) | entities without all four keep a stuck Teleport | static |
| dead | `Actor` (`0x009e8b34`) `currentState` u64 `+0xBF8`, bit 7 = Dead | `death` program `0x89660` (desc `0x1340a40`): `or qword [rax+0xBF8],0x80` at `0x8981b`, `and …,~0x80` at `0x89754` | static |
| live | villager 1156: hop +3 m x → moved exactly in **2 steps**, TeleportCount 0→1; back home; FindGround from +3 m → landed on the floor (y 836.5 → 833.47, 5 steps); ToEntity next to villager 1157 (1.35 m off the aim point after the ground search); duplicate in flight refused; prop refused (no TeleportCount); counter ended at 6 after 6 moves | world-test | live |

## Live results (lab `world`, a copied world, 2818 entities)
- Registration: `kiln_world` #818 + 3 self-test programs #819-#821; the items service's `kiln_items` (SYSTEM, 4
  singletons, 7 other-write accessors, 2 events-out) through `register_program` as #822, and its own check
  `accessors verified against the world's component map` passed. Game log: `[ecss] Programs: 821` with the four kiln.world programs (vanilla 817), 822 with `kiln_items`.
- Self-test (`kiln/data/kiln.world/selftest`): `PASS each-mode slots (3678 entities match GetComponent, 0 differ)`,
  `PASS raw-mode … (2208 match, 0 differ)`, `PASS system program singleton + read_other (200 ok, 0 bad)`,
  `PASS queue_add`, `PASS queue_remove`, `PASS watchdog` (a forced Teleport on a prop that the engine never applies:
  kind-4 removal after 10 s, `TIMED_OUT`, component gone; 246 steps).
- `world-test: done, 24 passed, 0 failed` (three runs); `reload-test world-test 6` with a teleport in flight on each
  load: `0 leaked context(s)`.
- Idle cost, 5 min each, no requests (tick-stats.jsonl, minutes 2-5): with kiln.world step p50 0.82-0.91 ms, Kiln Pre
  p50 0.005-0.007 ms; without kiln-world.dll step p50 0.90-0.98 ms, Pre p50 0.006-0.007 ms: no measurable difference
  (one more Pre subscriber; the own program returns at once without work).

## Open questions
- A missing singleton: does the engine skip the program on 1285601 exactly as on 1269579 (entry flag 0x20)? The check
  is in place; no case was seen.
- Players: not testable without a client (see the client checklist in docs/kiln/services/world.md).


## Programs v2 (dev.20)

`EngineDesc.other_write` already reaches the real engine registration at descriptor offset `0xF0`; v2 keeps that
scheduler declaration and uses the tracked `items/WriteOther`, `items/JobTracker`, and `items/JobWriteFlag` symbols
already captured by `items.json` (see [items evidence](items.md), section 12). The helper checks the accessor belongs
to this callback's declared other-write list. It passes the real job's storage, tracker, and write flag.

The event-name resolver sets event-type bits. `IterBegin` copies the resolved queue vector into its input slots.
Input slots therefore follow event type-index order, including when the descriptor names them in reverse order.
Queue records pack at their reflected size, including 4-byte events. EMF and the managed program reader now use
that size without requiring an 8-byte multiple.

Managed server mods load on the first Pre, after scheduler finalization. `kiln.programs.json` therefore reserves real
access declarations at native `on_ready`; `EcsPrograms.Bind` only installs a callback into the reserved entry. An
unbind drains callbacks and leaves the entry inert, so hot reload never rebuilds an active scheduler plan.

The `dev20-wp14` dedicated lab used the stopped `world` lab's populated save and a reviewed runtime dependency
baseline (`56f9e14`), overlaid with current world/types/EMF DLLs and managed API. Observed results:

- EMF: `emf-probe done: 124 PASS, 0 FAIL`; tracked Health write `118 wanted 118`; spawn-time scale `1.750000`
  present on the first read; Start/StopGuardingEvent and packed TestEvent1 each emitted/read three times.
- Managed: `world-programs-test: done, 11 PASS, 0 FAIL`; both input types read three events; Health `118` read back;
  immediate spawn component; unregister/rebind of the reserved descriptor; hook removal stops callbacks.
- Both C# and EMF hooks: `100/100 = profiled 100` for before/after callbacks and the independent runner profiler.
- Existing probes: world `24 passed, 0 failed`; hooks `17 passed, 0 failed`; spawn phases `43`, `9`, and `5` PASS,
  each with `0 FAIL`. Eight reloads with a live program and system hook reported `0 leaked context(s)`.

Update hooks retain at most 64 distinct native trampolines for process lifetime. Only the callback lease retires.
The portable production helper is tested with one world's original update blocked during retire/rebind, including
freeing the old user record under ASan. The old invocation cannot call the new owner's after callback. This tests the
concurrent-world lifetime policy without claiming an actual joined-client/multiple-world run.

## Tracked writes (dev.27; server 26430253, exe `f0d824be…`; client 26430625, exe `5d9ef2f1…`)

How kiln.world v3 changes a component of any entity so that the game's save and replication see it
(`World.Write`, `World.Update`; docs/kiln/services/world.md). Tags: **[dis]** read in the disassembly of the exe
file at the address given, **[cap]** captured by sigdb, **[rt]** seen in the running lab, **[hyp]** not proven.

### The path
The same path kiln.items uses for water (docs/re/items.md section 12), made general: one system program of Kiln's,
`kiln_world_writes` (flags SYSTEM | HOST_ONLY), with the listed component types as its `other_write` list and
`impact::AttributeRegistry` as an optional read singleton. Per request, inside its update:
`ReadOther(storage, accessor, entity)` for the block, then `WriteOther(storage, JobTracker(job), accessor, entity,
JobWriteFlag(ctx))` for the same block, the copy, and for an attribute `AttrChanged(component, registry)`.

| What | Server | Client | Notes | Tag |
|---|---|---|---|---|
| `WriteOther` inner | `0x64e7c0` | (wrapper `0x9b2900`) | `(out, storage, tracker, u16* accessor, u32 entity, u8 flag)`. Returns the empty pair for a null accessor, entity 0, an entity that is not in the entity map (`storage+0x148`, bound check against `+0x158`) or one whose archetype lacks the type (layout bitset). So a stale entity id costs a lookup and nothing else. For a found block in the server world (`storage+8 == 0`) it calls the mark | [dis] |
| the mark | `0x66f170` | `0x9d3280` | `(storage, tracker, entity record, u16 type, u8 flag)`: replication when a peer is connected (`u16 storage+0x7bc2d0 != 0`) and the type's bit in `registry+0x190` is set, through `0x66f2c0`; the save when the entity is one the save tracks (below) and the type's bit in `registry+0x230` is set, through `0x66f360` | [dis] |
| `MarkWritten` | `0x66f040` | | what a program calls for its own write slots: the same two tests on the same two bitsets, the same two callees. A component type without the save bit is marked for the save by nobody, the game's own programs included | [dis] |
| replication bits | `registry+0x190`, one bit per component type index | same | `mov rdx,[r8+rax*8+0x190]; shr rdx,cl; test dl,1` in the mark; sigdb `world/registry.replicated` | [cap] both sides |
| save bits | `registry+0x230`, one bit per component type index | same | `mov rdx,[rax+rdx*8+0x230]` in the mark; sigdb `world/registry.saved` | [cap] both sides |
| which entities the save tracks | entity record `+4` id, `+0xC` u8, `+0xD` flags | | ids 1..1023 (players): only with flag 8; every other entity: when `+0xC != 0` or flag 8. The meaning of the two fields is not decoded | [dis]; meaning [hyp] |

The two bitsets are captured from the mark itself (`@world/WriteMark.bits`, feature `world.component_bits`): one
match each on the server and on the client exe, the same two values. kiln.world reads the two bits at the world's
first live step (inside the world's setup the replication bits are not set yet: see the table below) and hands them
out with every answer (`Saved`, `Replicated`); for a type nobody listed, `World.WriteInfo` answers from a catalog of
every component type of the world's component map (the game's writers and sizes from the setup, both bits from the
first step) that kiln.world publishes whole, so no thread reads the live world's maps for it.

### Which entity a waiting request names
Entity ids are not all fresh: a player's entity is `slot + 1` (docs/re/players.md, `0x160ee0` **[dis]**), so the next
player in a slot gets the id of the one who left. kiln.world therefore keeps two facts with each request: the server
world it was made in (worlds are counted as they register their programs; a request of an earlier one is dropped),
and whether its entity lacked the component at any update while it waited behind a full update (512 a step). Such a
request never runs. A player's removal and the next player's spawn in the slot are several states of the player
record apart (4 removing entity, 5 removing, 0 free, then 1 loading, 2 waiting for the login, 3 spawn), so the gap is
at least one step and the watch sees it **[dis]** of the update switch; that no other entity id is handed out twice in
one world is **[hyp]** (ids grow at every spawn in the lab, and are handed out anew at every start).

### Where the write sits in the step
- The scheduler orders two programs whose access conflicts by registration order, and a program that declares
  `other_write` on a type conflicts with every program that reads or writes that type (FinalizePrograms' pairwise
  matrix, `.out/foundations/systems.md` section 1.4 **[dec]**). kiln.world registers its programs after the game's
  `RegisterAllPrograms` returned, so `kiln_world_writes` runs after every program of the game that touches one of
  its types: **its write is the last of the step for that type.** kiln.items' keep-full policy rests on the same
  rule and was measured: a can drained by the game 20 times was full again at the end of the same step 20 times
  (docs/kiln/services/items.md) **[rt]**.
- The marks go into the job's own tracker (`JobTracker(job)`), as for any program of the game, so the place in the
  step does not matter for the save or for replication: only for the value.
- **What the place does not decide** is the next step. The game's writers of the type run again then, see Kiln's
  value, and do what they do: add to it (a well's refill), clamp it, or write a value they derive from somewhere
  else. No static rule tells these apart, so kiln.world does not guess: it reads which of the game's programs write
  each listed type from the world's own program table (every descriptor's `write`, `write_opt`, `other_write`,
  `other_write_unser` and `all_write` lists; client-world programs, flag 0x1, left out; INIT programs, flag 0x8,
  counted apart) and, for a type with such writers, holds the answer one step and compares the bytes it wrote with
  what the component holds after the game's programs ran once more (`Kept`).

### Which component kinds are proven
| Kind | What is proven | How |
|---|---|---|
| No program of the game writes the type on the server | Kiln's write is the only one: the value stays until a mod changes it; no check is needed | the program table of the running build [rt]; the count is in the lab table below |
| Attributes with event-driven or incremental writers (`Fuel`) | the value stays (`Kept`), the game's formulas run after it, the game's own mirror program copies it for players in the next steps, a save and restart keep it | lab `dev27-world`, below [rt]; kiln.items' `fuel-test` before it |
| A type one of the game's programs derives every step (`NetworkFuel` from `Fuel` by `fuel_network`) | Kiln's write is written over in the next step and the answer says so (`Kept == false`): nothing is lost silently | lab, below [rt] |
| Every other type with writers of the game | the write lands last in its step and is marked; whether it stays is answered per write (`Kept`), not known beforehand | the rule above [dec]; per write [rt] |
| A type without the save bit | not saved, whatever the write | the mark [dis]; lab below |
| Replication to a player's screen | not proven here: a lab has no client. The replication mark is taken on the same path as the game's own writes [dis] | guided checks `world-writes-test/world.tracked-water`, `world.tracked-water-rejoin` |

### What the save does with it (lab) **[rt]**
The save mark (`0x66f360`) works per entity: it compare-exchanges a stamp in the entity's slot of its chunk and puts
the entity on the tracker's list **[dis]**; the type only decides whether the mark is taken at all. In the lab:
- a tracked write of a save-tracked type on an entity a save had already written was there after a save and restart;
  a raw write of the same field on a twin entity was not (three kinds: `Fuel`, `UsedItem`, `ServerToggleState.state`);
- a type without the save bit (`ScaleComponent`) was back at its template's value after the restart, tracked or raw,
  also on an entity that a tracked write of another type had marked for the same save: the save record of that prop
  does not hold the component at all;
- the game writes a save-tracked component through a save record of its own, not as raw bytes:
  `ServerToggleState.lastChange`, written tracked together with `state`, read 0 after the restart while `state` was
  kept. Which fields a type's save record holds is the game's; kiln.world does not know it.

### Lab results (lab `dev27-world`, the standard save with a base, no player; server 26430253) **[rt]**
`mods/tests/world-writes-test`, built with Kiln.Sdk (its manifest lists `NetworkFuel`, `UsedItem`,
`ServerToggleState` and the made-up `NoSuchComponentKiln`; the build added `Fuel` and `ScaleComponent` from the
typed calls). Three boots: 27 PASS, 9 PASS, 11 PASS, 0 FAIL.
```
program kiln_world_writes: the world has no component 'NoSuchComponentKiln'; registered without it
program kiln_world_writes registered as #830 (T0 R0 W0 S1 OT0 O5 EI0 EO0, iterator 56 bytes, flags 0x14)
tracked writes: program kiln_world_writes runs in world 0x…, 5 accessor(s) verified against the component map, attribute formulas on
PASS the build's answer for every component type: 687 types, 404 that no game program writes, 40 tracked for the save, 88 tracked for replication (components.tsv)
   (the review-fix run of the same lab, logs/lab-rf-boot1: 91 for replication; the three listed types below were read too early, see the next list)
PASS a type that is not listed: NotDeclared: Stamina of 2542: no loaded mod lists Stamina under "writes" in its kiln.mod.json: add "writes": ["Stamina"] (…) and restart the server; …
PASS World.Update<Ecs.Fuel> 0 -> 1234: Written: Fuel of 2542 (9 game writers, kept, marked for the save); in 2 steps
PASS the game's own fuel_network program picked the write up: NetworkFuel 1234
PASS the game's formulas ran after the write: 999999 asked, 6400 held (the result says 6400)
PASS a change that throws writes nothing: Faulted: Fuel of 2542: the change threw InvalidOperationException: on purpose; the component is as it was
PASS ComponentWrittenEvent for each write that changed bytes, none for the unchanged or refused ones: 4 event(s)
INFO NetworkFuel (the game copies it from Fuel) written to 77: Written: NetworkFuel of 2542 (4 game writers, written over by the game one step later, a type the save does not track); three steps later it reads 2000
PASS a type with no game writer answers one step later, no check needed: Written: ScaleComponent of 1026 (a type the save does not track); in 1 step(s)
-- boot 2 (entities a save has written; props found again by where they stand: entity ids are handed out anew at every start)
PASS Fuel on containers a save has written: can 4321 (tracked: Written: Fuel of 1028 (9 game writers, kept, marked for the save)), control 3000 (raw)
PASS UsedItem (no game writer, tracked for the save): 1032 tracked (Written: UsedItem of 1032 (marked for the save)), 1044 raw; and a raw ScaleComponent 1.75 on 1032
PASS ScaleComponent (no game writer, not tracked for the save): 1053 tracked 1.25 (…), 1061 raw 1.5
PASS ServerToggleState.state (the game writes the type too, tracked for the save): 1175 tracked to 0 (Written: ServerToggleState of 1175 (28 game writers, kept, marked for the save)), 1177 raw to 0; and lastChange of 1175 tracked to 12345
-- boot 3
PASS Fuel kept the tracked write across the save and restart: 4321 (written 4321)
PASS the raw Fuel control was lost: the save skipped the untracked write: 1000 (raw 3000, saved before it 1000)
PASS UsedItem kept the tracked write across the save and restart: 1032 0x8ec15337 (written 0x8ec15337, before 0xf1147751)
PASS the raw UsedItem control was lost: 1044 0x0cc076a8 (raw 0x8ec15337, saved before it 0x0cc076a8)
INFO the raw ScaleComponent 1.75 on 1032, the prop the tracked UsedItem write marked for the save, reads 1 (before 1)
PASS ScaleComponent, which the save does not track, was not kept either way: tracked 1.25 reads 1 (before 1), raw 1.5 reads 1 (before 1)
PASS ServerToggleState.state kept the tracked write across the save and restart: 1175 0 (written 0, before 1)
PASS the raw ServerToggleState control was lost: 1177 1 (raw 0, saved before it 1)
INFO ServerToggleState.lastChange, written tracked with the same component: reads 0 (written 12345, before 0): the game's save record of the type does not carry this field
```
- **The table** (`components.tsv`, written by the test from `World.WriteInfo` for each of the 687 component types;
  not committed, it is read from the game): 404 types have no writer among the game's server programs, 283 have one
  or more; 40 are tracked for the save (6 of them without a game writer: `DoNotSave`, `LogicGraphComponent`,
  `PerkContainer`, `SpawnTime`, `TeleportZoneConnection`, `UsedItem`), 91 for replication (the `Network*` and
  `Client*` mirrors, transforms, inventories, item state, `ScaleComponent`, `UsedItem`); 20 for both; 63 are
  attributes. The first run said 88 and 19: kiln.world then read a listed type's bits inside the world's setup, where
  the game has not set the replication bits yet (every type reads "no" there), and the three listed types that have
  the bit (`NetworkFuel`, `ScaleComponent`, `UsedItem`) came out "no". Since the review fixes the bits are read at the
  world's first live step; the other 684 types read the same at both times **[rt]**, so the bits are set between
  `RegisterAllPrograms` and the first step and do not change after.
- `Fuel` is not tracked for replication, and `fuel_network` copies it every step into `NetworkFuel`, which is: so a
  player's game gets the value through the derived component. A clear replication bit therefore does not say
  "players never see it" **[rt for the bits; the route is what fuel_network does, not proven on a screen]**.
- Host tests (no game): `native/services/world/tests/run.sh`: boot declarations 26 checks, program boundaries 26
  checks (the write program is registered without the names a world lacks), tracked writes 174 checks since the review fixes
  (every status, the check one step later, the queue and its limits, world identity, the watch over waiting requests'
  entities, the delivery, the watchdog, the catalog, a consumer compiled against the dev.26 header, the layout of
  every field in `tests/abi.json`, the shared boundaries in `tests/parity.json`). The review-fix lab run
  (`dev27-world`, a new lab on the standard save, three boots) gave 27, 9 and 11 PASS, 0 FAIL, the lines above with
  91 for replication.

### Still open
- A player's screen: whether and when a tracked write shows there. Guided checks, above.
- A client host: the write program is HOST_ONLY in the server world of a hosting game, as kiln.items' is; no run yet.
  `World.Update`'s callback may then run on a game worker thread.
- Which fields each type's save record holds (seen: `ServerToggleState` keeps `state`, not `lastChange`).
- The conflict rule of `0x645470` for `other_write` against `read_opt` and `other_write_unser` is taken from the
  earlier decompile, not re-read here.
