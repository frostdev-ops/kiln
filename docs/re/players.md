# Players on the dedicated server: what the game keeps, and where (svn 1285601)

Service: `kiln.players` (`native/services/players`, `sigdb/symbols/server.d/players.json`, usage in
`docs/kiln/services/players.md`). Sources: static analysis of the current server exe (svn 1285601, sha `c5c9fe50…`)
with capstone + `.pdata` function bounds and Ghidra 12.1.2 headless decompilation (no auto-analysis, functions created
on demand), the old server build 1273043 for comparison, EMF 0.3.3
docs (`re_chat.md`, `re_teleport.md` §5.3/§9, `inventory_crafting.md`, `ENGINE_MAP.md`), real server logs, and the
lab (no client). RVAs below are for svn 1285601 unless marked "old".

## Summary

| What | Where | How sure |
|---|---|---|
| Player records | `ServerPlayers` singleton = GameServer `+0x1E0`, **16 records x 0x148**, record `i` = slot `i` | High (code, both builds; lab: map + GameServer cross-check) |
| Player entity | `(handle & 0x3F) + 1`, i.e. **slot + 1 (1..16)**; record `+0x18` once spawned | High (code `0x160ee0`; logs: "Player EntityId 1" is slot 0) |
| Name | record `+0x20` keen string `{char* ptr; u64 len; u64 cap; allocator}` | High (code: every `'%k'` player log line formats `record+0x20`) |
| State | record `+0x0A` u8: 0 free, 1 loading, 2 waiting for machine login, 3 in game, 4 removing entity, 5 removing, 6 failed | High (code: the update switch) |
| User group | record `+0x0C` u32 group id, `+0x10` u8 permission bits, `+0x74` u32 (group id again) | High for the fields, Medium for the id's meaning |
| Account id | record `+0x48` u64 (from the session's player info) | High for the field; its relation to SteamID64 unknown |
| Connection | record `+0x00` connection handle (slot + generation << 6), `+0x04` machine (peer) handle | High |
| SteamID64 | only the online layer's log line `[online] Added peer N(g) (steamid:<id64>)` | High (format strings; real logs) |
| Admin bit | `BasicPlayerInfos[entity-1] +0x9D` bit 0 (16 entries x 0xA0) | High for the read (game's own `IsAdmin`), Medium for what sets it |
| Singleton map | world `+0x7462F8` (unchanged since 1269579/1273043), entries at world `+0x7462D0` | High (code, both builds; lab lookup == hook capture) |
| Join / leave | record state transitions; mirrored by fixed log lines | High (code + real logs) |

## The game server and its singletons

`GameServer` init (EMF "GameServer init", `0x140e3a350` in the 1269579 client) creates the server world and registers
`ServerPlayers` on it. Current build, `0x7af7c0` (body around `0x7afa0f`):

```
7afa0f  call  0x14061efd0                 ; create the world
7afa14  lea   rcx, [rip+..] -> "ServerPlayers"
7afa1b  mov   [rdi+0x1d0], rax            ; GameServer+0x1D0 = world
7afa27  lea   r8, [rdi+0x1e0]             ; object = GameServer+0x1E0
7afa48  mov   qword [rsp+0x38], 0xd       ; name length 13
7afa51  call  0x14067e720                 ; RegisterSingleton(world, &name, object, &opts)
7afa64  lea   r8, [rdi+0x1660]            ; next singleton: "QuestHelper" at GameServer+0x1660
```

`0x1E0 + 16 x 0x148 = 0x1660`: the 16 records end exactly where the next singleton starts. Other GameServer fields
seen on the way: `+0x10` the session, `+0x28` the ban list (0xD0-byte entries: `+0` u64 account, `+8` and `+0x68`
strings, used by "[server] Account %s/%s was unbanned."), `+0x1D8` the server world's system block (returned by
ServerWorldSetup `0x233ee0`, caller `0x7b0246`: `mov [rdi+0x1d8], rax`), `+0x16A0` the machine table (17 x 8 bytes,
indexed by `peer & 0x7F`), `+0x1830`/`+0x1838` the user-group table (`{u32 id; u8 permissions}` pairs, count).

## RegisterSingleton and the world's singleton map

`RegisterSingleton` (EMF signature 2) `0x67e720` (old `0x677240`): `bool(world, {char* ptr; u64 len}* name, void*
object, Options* opts)`. It hashes the name with FNV-1a-32 (`0x811c9dc5`, `0x1000193`), refuses a duplicate, appends a
0xE8-byte entry and rebuilds the hash map:

| Field | Offset | Evidence (instruction in `0x67e720`) |
|---|---|---|
| singleton array `{entries*, count, capacity, grow fn}` | world `+0x7462D0` | `lea r15,[rdi+0x7462d0]`; `imul r14,[r15+8],0xe8` |
| map base (lookup helper argument) | world `+0x7462F8` | `lea rcx,[rdi+0x7462f8]` before `call 0x675690` |
| map bitset / capacity / keys (u32) / values (u16) / count | `+0x746300` / `+0x746308` / `+0x746320` / `+0x746338` / `+0x74634C` | the probe loop at `0x67e775..0x67e7db` |
| entry: inline name `{ptr,len,cap=0x80,..}` + buffer at `+0x20` | entry `+0x00` | `lea rax,[r14+0x20]; mov [r14],rax; mov qword [r14+0x10],0x80` |
| entry: name hash | entry `+0xA0` | `mov [r14+0xa0], ebx` |
| entry: object | entry `+0xA8` | `mov [r14+0xa8], r12` |
| entry: access pointer (`opts+0x30` or the object) | entry `+0xB0` | `cmovne r12,rax; mov [r14+0xb0],r12` |
| entry: flags byte (`opts[0]`), index | entry `+0xB8`, `+0xBC` | |

Lookup = `i = hash & (cap-1)`, linear probe while the bit is set, `keys[i] == hash` gives `values[i]` = entry index.
The map sits **before** the program table, so the +0x1C00 growth of 1285601 did not move it: `sigdb_check` captures
the same nine values on both builds (below). EMF's "world+0x7462F8 in 1269579" is still right.

Lab (every boot, `kiln.log`):
```
players: world 00000000B2410080 (server world: yes), ServerPlayers 00000000ACC7D0A8 (map 00000000ACC7D0A8: match),
BasicPlayerInfos 00000000FC6733D8 (map 00000000FC6733D8: match), GameServer+0x1d0 = world: yes
```
The objects the RegisterSingleton hook saw, the objects the map lookup returns, and `*(ServerPlayers - 0x1E0 + 0x1D0)`
all agree. kiln.world (an independent worker) reports the same server world (`world 0xb2410080`).

## The player record (ServerPlayers, 16 x 0x148)

The per-step player update `0x7bed90` (pattern anchor `players/UpdatePlayers` at `0x7bee48`, old `0x7b58b8`) loops
`for i in 0..15: rec = GameServer + 0x1E0 + i*0x148` (`imul r14,r15,0x148; add r14,0x1e0; add r14,rsi`; loop bound
`cmp r15,0x10`) and switches on `rec+0x0A`. Every other function finds a record by handle the same way:
`rec = GameServer + 0x1E0 + (handle & 0x3F) * 0x148; check rec[0] == handle` (e.g. "machine logged in" `0x7af4e0`:
`and eax,0x3f; imul rcx,rax,0x148; add rcx,0x1e0; add rcx,rdi; cmp [rcx],edx`).

| Offset | Type | Meaning | Evidence |
|---|---|---|---|
| `+0x00` | u32 | connection handle (`slot | generation << 6`; 0 = free) | lookup above; EMF: 0x40 for the first player |
| `+0x04` | u32 | machine (peer) handle; `& 0x7F` indexes GameServer `+0x16A0` | `0x7af692 mov eax,[rcx+4]; and eax,0x7f` |
| `+0x08`/`+0x09` | u8 | failure flags (reason) | AddPlayer `0x7af3d3`/`0x7af3d7` |
| `+0x0A` | u8 | state: 0 free, 1 loading (spawn position search), 2 loaded (waits for the machine's login), 3 in game, 4 removing entity, 5 removing, 6 failed | update switch; AddPlayer sets 1 (`0x7af3dd`), machine login 2 -> 3 (`0x7af6a4`) |
| `+0x0C` | u32 | user-group id (from the session's player info) | `0x7bf4b2 mov [r14+0xc],eax` |
| `+0x10` | u8 | permission bits of that group | `0x7bf4ae mov [r14+0x10],dl` (from the group table `+0x1830`) |
| `+0x18` | u32 | player entity (0 until spawned) | spawn `FUN_140628e20(world, handle & 0x3F, ...)` -> `rec[6]`; "Remove Entity" passes `[r14+0x18]` |
| `+0x20` | keen string | the player's name `{ptr, len, cap, allocator}` | every `'%k'` player line formats `lea rax,[r14+0x20]` |
| `+0x48` | u64 | account id (session player info `+0x20`) | AddPlayer `0x7af3a7 mov [rdi+0x48],rax` after `call 0x140a01f50` |
| `+0x50` | 16 B | `CharacterId` (GameMessageLoginRequest.characterId) | record setup `0x7ac718 movups [rsi+0x50],xmm0`; docs/re/characters.md |
| `+0x60`, `+0x70` | 16 B, 8 B | the character's base id and respawn point from `known_users` | spawn point `0x7aded0` |
| `+0x74` | u32 | group id (copy from the table entry) | `0x7bf49d mov [r14+0x74],edx` |
| `+0x100..+0x138` | WorldTransform | spawn transform (position i64 x3 32.32, rotation, scale) given to the spawned entity as `CurrentTransform` (0x27ac564, 0x38 bytes) | update case 3 |
| `+0x141..+0x143` | u8 | save/shutdown bookkeeping (`+0x143`: 1 shutdown message, 2 save-data-error shutdown) | update case 4 |

Lab, empty server: all 16 records have handle, peer, state, entity, name length and account 0; record 0's only non-zero
qwords are the name string's allocator and the spawn transform's identity rotation/scale (1.0 floats):
```
players: record[0] 00000000ACC7D0A8 non-zero qwords: +0x38=6ffffc6c3990 +0x120=3f80000000000000 +0x128=3f8000003f800000 +0x130=3f800000
```

**Entity id.** `0x160ee0` (old `0x15bb20`): `mov eax,[rcx]; test eax,eax; jnz; mov eax,[default]; ret; and eax,0x3f;
inc eax; ret` = `entity = (handle & 0x3F) + 1`. Used for the spawn, the record reset and the player transform. So
player entities are **1..16** on a 16-slot server (the ABI's "1..1023" is the engine's general player range; the
`IsAdmin` bound `entity-1 <= 0x3FE` is that). Real logs agree: "Replication Checkpoint … for Player 0" pairs with
"Player EntityId 1 teleported", "player index 1" with "EntityId 2".

**States and log lines** (update `0x7bed90`):
- case 1: spawn position search (`[server] No Valid Spawn Position was found` -> 6), then 2.
- machine login `0x7af4e0`: for every record of the machine in state 2: state 3 and `[server] Machine '%u': Player '%k' logged in`.
- case 3: if `+0x18 == 0` spawn the entity (`[server] Could not spawn Player Entity` -> 6), then
  `[server] Player '%k' logged in with Permissions:\n` plus `\t - <name>\n` for each of bits 0-5 (localized names of
  enum `0x159b9659`: CanKickBan, CanAccessInventories, CanEditBase, CanEditWorld, CanExtendBase, CanReceiveEXP), logged
  as **one message** (the lab confirms the mirror delivers multi-line messages: "the log mirror delivers multi-line
  messages (3 so far)").
- case 4: `[server] Remove Entity for Player '%k'`, destroys `+0x18`; optional `[server] Send Shutdown Message to Player
  '%k'` / `Send Save Data Error Shutdown Message`; -> 5.
- case 5: `[server] Remove Player '%k'`, frees the machine slot, resets the record (`0x7ad860`: clears `+0x18`, `+0x00`,
  `+0x0A`, the strings at `+0x80` and `+0xC0`).
- case 6: disconnects the machine (`0x7b6020(GameServer, peer, reason)`).

**Permissions and user groups.** The config's `userGroups` (`enshrouded_server.json`: name, password, canKickBan,
canAccessInventories, canEditWorld, canEditBase, canExtendBase, reservedSlots; validated in `0x53ff50`: "user groups
passwords must be unique", "Only one user group can be without password", "Game role without password has more rights
than a password protected one") become the `{u32 id; u8 permissions}` table at GameServer `+0x1830` (the ctor
`0x7ab000` ORs `0x20` = CanReceiveEXP into every entry, and masks with `0xC1` in one mode). In case 3 the record copies
the entry whose id matches the session's group id. Bits: `0x01` KickBan, `0x02` AccessInventories, `0x04` EditBase,
`0x08` EditWorld, `0x10` ExtendBase, `0x20` ReceiveExp (bit order = log order; the strings sit in that order in
`.rdata` at `0x12b92c0..0x12b9460`). How the id maps to a group **name** was not traced (open).

## BasicPlayerInfos (admin bit)

Registered by ServerWorldSetup (`0x233ee0`) on the server world: name `"BasicPlayerInfos"` (16 bytes), object
`systems + 0x498` where `systems` is the 0xEA0-byte block ServerWorldSetup allocates first (`mov edx,0xea0; call
[alloc]`), which GameServer keeps at `+0x1D8`. `0x498 + 16 x 0xA0 = 0xE98`: **16 entries**, as many as player slots.

| Code | Old (1273043) | What |
|---|---|---|
| `IsAdmin` `0x165720` | `0x160330` | `bool(entity, infos)`: `e = entity-1; if e > 0x3FE false; entry = infos + e*0xA0; entry+0 == entity && (entry+0x9D & 1)` (`lea rdx,[rax+rax*4]; shl rdx,5`) |
| `0x163010` | `0x15dc30` | `u32*(out, entity, infos)`: entry `+0x04` (connection handle; EMF: matches the chat lines' speaker handle) |
| `0x163040` | | `u8*(out, entity, infos)`: the whole `+0x9D` byte |
| `0x233410` | | entry ctor: zeroes `+0x18..+0x98` and `+0x9D` |

`IsAdmin` gates the admin client's actions (EMF: `admin_player` teleport/daytime/kill, the admin inventory create). The
code that **sets** `+0x9D` was not found (no direct writer in GameServer code; candidates by byte pattern were other
structs). kiln.players reports it as `IsAdmin` with "known" only when the entry names the entity, exactly as the game's
own check. For server rights (kick/ban, inventories, building) use the record's permission bits.

Lab, empty server: every entry's `+0x00`, `+0x04`, `+0x9D` are 0; the entries hold four preallocated arrays
(allocator, pointer, capacity triples, as EMF noted: "its pointers at +0x20, +0x40, +0x60, +0x80 are arrays"):
```
players: BasicPlayerInfos[0] 00000000FC6733D8 non-zero qwords: +0x18=b41f388 +0x20=f082ed20 +0x28=100 +0x30=b41f388
+0x38=b41f388 +0x40=f082ed48 +0x48=400 +0x58=b41f388 +0x60=f082f150 +0x68=40 +0x70=b41f388 +0x78=b41f388 +0x80=f082f170 +0x88=100
```

## Steam ids and account ids

- The record's `+0x48` comes from the session's per-player info (`0x140a01f50(out, session, handle)`: a
  double-buffered session block, 0x2668 per buffer, 0x150-byte player infos at `+0x178`; the copy `0x9f7a20`). The ban
  list keys on a u64 account too. Whether this u64 **is** the SteamID64 or a hash of a platform id string (the chat
  messages call it `accountIdHash`) needs a joined client (client checklist).
- SteamID64 appears only in the online layer's log: format strings `"[online] Added peer %k (%llu)"` (`0x132f380`) and
  `"[online] Added peer %k (%s)"` (`0x132f5f0`), the latter printing `steamid:<id64>`, then `"[online] Client '%k'
  authenticated by steam"`. kiln.players takes it from the mirrored log and pairs it with the next
  `[session] Remote player added. Player handle: N(g)`.

## Join and leave in the game log

Real lines (live server, svn 1285601; names and Steam ids replaced; full files in `mods/tests/players-test/fixtures/`):

```
[I 00:53:09,031] [online] Session accepted with peer (steamid:76561190000000001)
[I 00:53:09,031] [online] Added peer 0(1) (steamid:76561190000000001)
[I 00:53:10,483] [online] Client '76561190000000001' authenticated by steam
[I 00:53:10,749] [session] Add remote machine index 1 (id: 3282338659).
[I 00:53:10,750] [session] Remote player added. Player handle: 0(0)
[I 00:53:10,924] [Server] Creating player knowledge replication state for player index 0
[I 00:53:15,846] [server] Machine '1': Player '0(0)' logged in
[I 00:53:15,847] [server] Player 'Alder' logged in with Permissions:
[I 00:53:15,847] 	 - CanKickBan
          ... CanAccessInventories, CanEditBase, CanEditWorld, CanExtendBase, CanReceiveEXP
[I 00:53:15,847]
...
[I 02:27:44,161] [server] Remove Entity for Player 'Birch'
[I 02:27:44,166] [server] Remove Player 'Birch'
[I 02:27:44,230] [online] Removed peer 1(1)
[I 02:27:44,235] [session] Player removed. Player handle: 1(1)
```
At shutdown: `[game_server] Switching state from Run to Shutdown`, then per player `Remove Entity for Player`, `Send
Shutdown Message to Player`, `Remove Player`, and `[online] Removing all peers`. Connections that never log in:
`[session] Timed out while waiting for player. Remove Player: N(g)` (`0x1332f20`) or `Unable to wait for player. Could
not get game info. Remove Player: N(g)` (`0x1332ed0`). The first number of a player handle is the slot (entity - 1).
Every one of these format strings is also in build 1273043's exe (checked byte for byte).

## Kick (not in v1)

- `0x7b6020(GameServer, peer handle, reason u8)` builds and sends a disconnect message to the machine; the update calls
  it for failed records (reasons 2-5 seen). It is the likely primitive of a kick.
- `[online] Dedicated server kick not implemented.` (`0x13305d0`) / `Only host can kick Players.`: the online layer's
  kick is a client-host feature; the dedicated server's kick goes through `AccountActionType::Kick` (an
  `AccountActionEvent` from the `admin_player` program, gated by `CanKickBan`). The handler was not traced.
- Not implemented: it cannot be tested without a client. Open question for Kiln.Admin.

## Signatures (sigdb/symbols/server.d/players.json)

`sigdb_check.py` on a temp merge of `server.json` + `players.json`; every pattern is unique and every capture agrees:

| Symbol / probe | svn 1285601 | 1273043 | Captures |
|---|---|---|---|
| `players/RegisterSingleton` | `0x67e720` | `0x677240` | map `+0x7462F8`, bits `+0x746300`, cap `+0x746308`, keys `+0x746320`, values `+0x746338`, count `+0x74634C`, array `+0x7462D0`, entry stride `0xE8`, object `+0xA8`, hash `+0xA0` (2 votes) |
| `players/IsAdmin` | `0x165720` | `0x160330` | `info.max_index 0x3FE`, `info.admin 0x9D` (`info.stride 0xA0` fixed: `lea x,[x+x*4]; shl x,5`) |
| `players/InfoHandle` | `0x163010` | `0x15dc30` | `info.handle 0x4` |
| `players/HandleToEntity` | `0x160ee0` | `0x15bb20` | (feature check: entity = (handle & 0x3F) + 1) |
| `players/MachineLoggedIn` (anchor inside `0x7af4e0`) | `0x7af663` | `0x7a6433` | `rec.stride 0x148`, `server.records 0x1E0`, `rec.state 0xA`, `rec.peer 0x4` |
| `@players/ServerPlayersSingleton` | | | `server.world 0x1D0`, `server.records 0x1E0` |
| `players/UpdatePlayers` (anchor inside `0x7bed90`) | `0x7bee48` | `0x7b58b8` | `rec.stride`, `server.records`; scoped probes: `rec.count 0x10`, `rec.group 0xC`, `rec.permissions 0x10`, `rec.group_current 0x74`, `rec.name 0x20`, `rec.entity 0x18` |
| `@players/AddPlayer.fields` | | | `rec.account 0x48`, `rec.state 0xA` |

`server.records` has 3 agreeing captures, `rec.stride` and `rec.state` 2 each. Features: `players.records`,
`players.admin`, `players.singleton_map`; all on, on both builds.

## Lab evidence (labs/players, svn 1285601, no client)

- `players-test`: **30 PASS, 0 FAIL** with every service of the tree deployed (chat, example, items, players, settings,
  spawn, time, world), and 27/27 (before the probe checks existed) with only example + players. 3 hot reloads:
  `0 leaked context(s)` with `Players.Joined`/`Left` subscribed.
- Position path: GetEntityTransform on the captured world, 32.32 -> metres, equals kiln.world's own position read on
  the save's first entities (`probe entity 1025: players (3799.8, 832.5, 1433.8) world (3799.8, 832.5, 1433.8)`,
  worst 0 m). 1025 is the vault's flame altar: the live server's log teleports players to `(3799.75, 833.75, 1435.0)`
  on the same save.
- Simulation (`KILN_PLAYERS_SIMULATE`, the fixture replayed one line per step into the live tracker): the service logged
  `joined slot 0 entity 1 'Alder' (steam 76561190000000001, admin 0, perms 0x3f) [event 1]` ... `left slot 0 entity 1
  'Alder'` [event 4], and the test mod's handlers got `+Alder@1, +Birch@2, -Birch@2, -Alder@1` with lookups by name
  (case-insensitive), Steam id and entity working inside the handler (10/10 PASS).
- Cost: `scan cost over 1000 steps: avg 2.2 us, max 43.8 us` (empty server).

## Open questions

1. Is record `+0x48` the SteamID64 or a hash? (client)
2. Who sets BasicPlayerInfos `+0x9D`; is it ever set on a dedicated server (EMF calls it the game admin bit)? (client
   with each user group) Partly answered (docs/re/progression.md §2): the quest rewards test `+0x9D & 0x20`
   (CanReceiveEXP) and `IsAdmin` tests bit 0 (CanKickBan), so the byte holds the user group's permission bits; a client
   joined with the Admin group read `admin 1, perms 0x3f`.
3. User-group id -> group name (the ctor `0x7ab000` copies the table from its config argument; the conversion from the
   config's 0x58-byte group entries was not traced).
4. The kick path for `AccountActionType::Kick` on the dedicated server.

## Carried-player fall detection (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; dossier chapter 19a §2.7, riding VERIFY Session-5 V-S5b/c confirmed with
qualifications. Carry/input/client behavior is in [mounts.md](mounts.md); damage interception in [combat.md](combat.md).

[static] `fall_damage_detection` S **`0x89e50`**, descriptor `0x1341390`, reads Actor/CurrentTransform/
DynamicLocomotion/MovementCollider/Velocity, writes DynamicFallDamage. It is `void(ProgramContext*)`, not a tick-call
helper. DynamicFallDamage **16 B** (metadata `0xee3900`): wasFalling u8 `+0`, resetFallAltitudeOnApex u8 `+1`,
fallStartAltitude f32 `+4`, detectedFallDistance f32 `+8`, detectedFallDamagePercentage f32 `+0xc`. Fields agree
with reflection on both builds; no new client detection RVA is asserted here.

Falling condition: DynamicLocomotion.state `+0x59` is Falling 2, or Sliding 1 when S `0x94c3f0` terrain config's
isSlidingMaterial byte `+0x2d` is false. Start altitude persists while falling. With apex-reset byte set, positive
Velocity.y updates start altitude (S `0x8a1c5..0x8a252`). ResetFallAltitudeOnApexEvent **`0x790aaee4`** and
ResetFallDamageEvent **`0xab025386`** reset the tracked altitude/state.

Distance is computed **only on a Grounded (Actor flag 0), non-falling step**, with wasFalling set and
MovementCollider `+0x48 results.preventFallDamage` clear (checks S `0x89ef9/0x89fc6/0x89f47`). Converts world Y
signed 32.32 using 2^-32, stores start−current at `+8` (S `0x8a015..0x8a04d`); final wasFalling store `0x8a35a`.
Calculation S `0x89cb0` turns distance into percentage; infliction `0x8a3c0` writes Health directly, no ApplyHit.
Gravity-off/velocity-zero does **not** by itself reset this fall history. A rider may flicker grounded on its mount
and be charged for a long carried descent when grounded returns.

[lab] Controlled mortal rider on .3 Fog Dragon: start held 1003.4 m, later grounded at 978.3 → **25.50 m**, fraction 1,
damage 100/lethal, HP 0/100. Combined per-step writes `+0=0,+1=0,+4=currentY,+8=0` **and EntityFallEvent cancel**
kept 593 lab rows at 100/100 and 671 outdoor client rows at 313/313. `+0xc` was not reset; retain cancellation backstop.
Neither reset-only nor event-only was tested, and emitting ResetFallDamageEvent every carried step was a proposed
service path, not the proven mechanism of that combined probe.

[seen] Original outdoor session 5 rider death had **no fall telemetry**: it is strongly consistent with this reproduced
mechanism, not directly attributed. Mount still sank 3 m/s when horizontal speed reached 0. Later guarded/pinned run
survived and landed; render pin fixed drawn rider alignment but not camera or prediction. Full product fall waiver,
dismount/teleport and guard-removal behavior remains unimplemented/unverified.
