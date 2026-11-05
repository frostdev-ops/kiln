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
- Order of several events-in queue pointers in the iterator (only a single events-in slot is resolved).
- The other-write path (`WriteOther` `0x64e7c0`) and its tracker argument inside a program: not exposed in v1.
- A missing singleton: does the engine skip the program on 1285601 exactly as on 1269579 (entry flag 0x20)? The check
  is in place; no case was seen.
- Players: not testable without a client (see the client checklist in docs/kiln/services/world.md).
