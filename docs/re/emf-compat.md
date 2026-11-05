# kiln-emf: EMF 0.3.3 mods on Kiln (how and evidence)

`native/plugins/emf/` builds `kiln-emf.dll`, a Kiln plugin that gives unmodified EMF (Enshrouded Modding Framework
0.3.3, MIT, "EMF contributors") mods an `EmfApi` v21 table built on Kiln's services. The user guide is
[docs/kiln/emf.md](../kiln/emf.md). EMF's own core is described in [emf-core.md](emf-core.md).

## The table
- **Header:** `native/plugins/emf/include/emf/emf_api.h` is EMF's own header, unchanged below an MIT notice
  (`licenses/EMF-LICENSE.txt`).
- **Size:** 73 function slots, `{version 21, size 592}`.
- **Every slot is filled.** `emf::init` checks only `size >= sizeof(EmfApi)`, so an empty slot would crash a mod.
  Unsupported functions are stubs that log once and return a failure. The probe checks this (`every function slot is
  filled`).
- **Who called:** most calls do not name their mod. kiln-emf finds the caller from the return address (`_ReturnAddress`,
  then the module that holds it) or from the callback's module, then from the thread's current context (a mod's
  `emf_mod_load` or callback).
- **Kiln owners:** each mod gets one, `emf:<id>`. It carries the mod's log lines and its content layers
  (big_stacks' `maxStackSize` changes are its own `kiln.content` layer).

## Load sequence (`plugin.cpp`, `mods.cpp`)
Everything runs in `on_ready`: after all services and plugins ran `kiln_plugin_main`, still before the game's entry
point.
1. **Step aside** when EMF's real core can run in this process:
   - `GetModuleHandleW("emf_core.dll")`; or
   - a loaded module that exports `emf_loader_identity` / `emf_core_start` (EMF's `dinput8.dll` loader, chained by
     Kiln's proxy in DllMain).

   Chain lists that name EMF's loader without it being loaded (`dinput8-chain.ini`, `kiln/chain.ini`, a DLL with
   EMF's `EMF-LOADER-` marker in `kiln/chain/`) are only logged. When stepping aside, kiln-emf reads
   `emf/emf_core.log` at server step 50. If EMF went idle there, it logs how to switch EMF's loader off.
2. **Discover** the mods:
   - With `kiln.profile` v1 and mode ACTIVE/ONCE: the profile's `emf` entries (folders with `mod.json`), plus
     `<game>/emf/mods` when `dropins()` has the EMF bit.
   - In mode LEGACY, or without `kiln.profile`: `<game>/emf/mods` only.
   - In mode NO_MODS/INVALID: nothing.
   - Folder mods and loose DLLs follow EMF's reference.md: the id is 1-48 of `a-z 0-9 _ . -`, and a loose DLL's id is
     its lower-case file name. The first copy of an id wins.
3. **Decide** each mod. Reasons are logged, and kept in `state/emf.json` and `mod_info`:
   - the switch (ini `enabled`, else `mod.json` `enabled`; a profile entry with `enabled: 0` is always off);
   - `"emf"` > 21 is refused;
   - `side: "client"` on a dedicated server is skipped;
   - `main` makes a script mod (status SCRIPT, run by `lua`);
   - required dependencies that are missing, off, rejected, failed or of the wrong version reject the mod. Version
     ranges are EMF's: `^ ~ >= > < <= = x * a - b ||` plus comma/space AND.
4. **Order:** Kahn's algorithm over required and usable optional dependencies, ties by source name. An optional-only
   cycle goes by name. A required cycle rejects its mods.
5. **Load:** `LoadLibraryExW(dll, LOAD_WITH_ALTERED_SEARCH_PATH)`, then `emf_mod_load(&table)` inside
   `seh_call`. A false return or a fault (C++ exceptions included, code 0xE06D7363) marks the mod FAILED. There is no
   `FreeLibrary`, ever. A dependency that failed rejects its dependents.
6. **After all loads:** the programs that `on_event` / `register_program` asked for go to `kiln.world`. They must be
   registered before the server world finalizes its programs, which happens at its first step. Then the ticks are
   subscribed and `state/emf.json` is written.

## Threads and callbacks
- **"After the world's step, on its thread, no jobs running"** (EMF's rule) maps to Kiln's Post phase:
  - server world: `subscribe_tick(POST)`, after `WaitWorldJobs`;
  - client and prediction worlds: `subscribe_world_tick(kind, POST)`.
- **Post, in this order:** `on_event` deliveries (copied during the step by kiln-emf's programs), queued commands
  and chat lines, `run_on_world`, `on_tick`, then setting-change callbacks.
- **Server Pre:**
  - `kiln.saves` events become `on_load` / `on_save`, read with `next_event` polling. `set_pump` is not used: it has
    one owner, managed Kiln.Saves.
  - World-setting writes, teleport READY timers, `emf/commands.txt`.
- **Results that `kiln.items` / `kiln.world` deliver in Pre:** put/take and teleport results run there. That is still
  "after the step" and as safe as Post. A result requested inside a command keeps the command's context, so the
  reply still reaches the player.
- **Guards:** every mod callback runs under `guarded()` (an SEH guard). A fault logs the module+offset and disables
  that callback only. The server runs on.
- **The world-callback rule:** `spawn`, `destroy` and `give_item` refuse outside a world callback on the server world
  (thread-local `t_ctx`), as EMF does.

## Function mapping (by area)
- **Worlds:**
  - `server_world` / `client_world` = `api->world(kind)`.
  - `world_kind` compares with the live worlds, then reads `storage.kind`.
  - `world_tick` reads the u32 at `world + world.storage + storage.clock`.
  - `ecs_time_ns` = `storage.clock_period × clock`. Probe: tick 1048 = 17.5 s.
- **Components:**
  - Kiln's component hash (FNV-1a of `keen::ecs::<Name>`) equals EMF's types.json `qualifiedHash`: CurrentTransform
    `0x027ac564`, Health `0xdc101091`, PlayerState `0x6ff967bb`, NetworkHealth `0x216eb5f9`.
  - `get_component` on the server world = `kiln.world.component`. On other worlds it calls the game's
    `GetComponent(Slice*, world, hash, entity)` under SEH, on that world's thread.
  - `get_template_component` = `GetTemplateComponent(world, hash, entity)`. The size comes from `TypeByHash(hash)`
    +0x40 (probe: ActiveNpcState 456 bytes, the size EMF's header has).
  - `NpcCommon` is template-only, so `get_component` returns null for it, exactly as in EMF.
- **for_each_entity:**
  - It walks the storage's entity map. The layout is derived as `kiln.world` derives it: capacity/values from
    `world/storage.entities*`, bits/keys from the singleton map, both cross-checked. Snapshot first, then callbacks.
  - Probe: 2818 entities, 2702 with a 56-byte CurrentTransform.
  - Client and prediction worlds use the same layout from the client's composed sigdb (offline check: the world
    constants resolve on enshrouded.exe 26404966); not yet run in game.
- **Items:**
  - `for_each_item` / `find_item` / `item_info` use the `kiln.items` catalog. The max stack is read live from
    `kiln.content` (`maxStackSize`): the catalog is a load-time snapshot, and big_stacks would otherwise raise again on
    every pass.
  - `item_data` = the `kiln.content` entry address. Size `TypeByHash("keen::ItemInfo")` = 2088.
  - `set_item_max_stack` = `kiln.content.set(owner, ITEMS, id, "maxStackSize")`, which also turns on the stack guard.
- **Inventories:**
  - `put_items` / `put_items_ex` use `kiln.items.give` with NOTIFY (+LEVEL/RARITY).
  - `take_items` uses `kiln.items.take` (+EXACT_SLOT).
  - At most 64 requests wait at a time, as in EMF.
- **Spawn:**
  - `spawn` = `kiln.spawn.spawn` with NO_TRACK, and TRANSIENT unless the template is an NPC. EMF: "not saved
    (except villagers)".
  - `give_item` = `kiln.spawn.spawn_item` 1 m above the player.
  - `destroy` refuses ids 1..1023.
- **Combat:**
  - `attribute_get` HEALTH / MAX_HEALTH = `kiln.combat.health`.
  - `attribute_add` HEALTH = `set_health(clamp(current + delta, 0, max))`.
  - `damage` = `kiln.combat.damage`: reaction 2 (light), aggro toward the attacker.
  - Probe: Vukah 119/119, killed by a 1119 hit, whose HitEvent reached `on_event`.
- **Teleport:** `kiln.world.teleport` (FIND_GROUND/FACE_YAW/CHECKPOINT; EMF's bounds 0..10240 / -1000..5000).
  - The Kiln result becomes MOVED/FAILED + OFF_TARGET/RAW_TARGET + LAST.
  - NOTIFY_READY: the first callback has no LAST. READY+LAST follows 1 s later for players and at once for other
    entities. Kiln cannot yet see when a player's game is ready.
  - Probe: villager 1160 moved 2 m (1.07 m off after the ground search) and back.
- **Chat:**
  - `chat_tell` / `chat_broadcast` = `kiln.chat.send(to, line)`, applying EMF's line rules (10 lines, then "...";
    1024 bytes; control characters become '?'; 30 lines per mod in 10 s). Off the tick thread the send is posted.
  - `player_name` comes from `kiln.chat.find_player`, else `kiln.players`.
- **Commands:**
  - A `kiln.chat` subscriber (Pre) consumes `/<registered EMF command>` and runs it after the step.
  - Admin = the game's admin bit (`kiln.players`), or `[chat] admins` / `everyone`. `deny` and the rate limit are
    EMF's.
  - `emf/commands.txt` is renamed, read and deleted every 0.5 s, and runs as the console.
  - `/help` is left to Kiln.Chat. EMF's built-ins answer as `emf.help`, `emf.mods`, `emf.settings`, `emf.set`.
- **Saves:** `on_load` = `KILN_SAVE_EVENT_LOADED` (slot), `on_save` = WRITTEN, `request_save` =
  `kiln.saves.request(REQUEST)`.
- **World settings:**
  - `kiln.settings`, using EMF's short names (`loot`, `day_minutes`, ...). Durations are given in minutes (Kiln uses
    ns).
  - Values are cached in Pre whenever `status.revision` moves, so `get` and `info` work from any thread.
  - `set` / `reset` are queued for the next Pre. Changes from anyone fire `on_world_setting_changed`.
  - `persist` is stored in `<configDir>/emf/world_settings/<slot>.txt` and applied again after each load.
- **Programs:**
  - `register_program(_def)` = a `kiln.world` SYSTEM|HOST_ONLY program in `each` mode. The mod's
    `fn(program_context)` runs inside it on a worker thread.
  - `program_singleton` / `program_read_other` read the iterator slots.
  - `emit_event` = the game's emitter through the job in the events-out slot, as `kiln.gameplay` does
    (`gameplay/JobEventBuffer`, `JobProgramId`, `EmitEvent`, and `ProgramTime` for the timestamp). The size comes from
    reflection; 256 bytes or more is refused.
  - `read_events` walks the events-in queue: one event type per program, the limit of `kiln.world` v1.
  - Probe: 3 StopGuardingEvents with a marker (a guarding entity nobody has) were sent and read back the next step.
- **on_event:** one SYSTEM program `emf_ev_<Event>` per event type (events_in = [Event]). It copies records during
  the step and delivers them in Post.
  - The record stride is the event's reflected size. EMF refuses sizes that are not a multiple of 8, and kiln-emf
    refuses them too.
  - Queue layout: `combat/evq.*` constants, the walk `kiln.combat` uses.
  - Live sizes on svn 1285601: EntityDiedEvent 16, HitEvent 248, PickupEvent 48, CraftRecipeEvent 56.
- **Hooks and patterns:**
  - `resolve` uses the alias table below.
  - `hook` / `hook_pattern` / `hook_address` = `safetyhook::InlineHook` with StartDisabled: `*original` is set
    before `enable()`. Only executable image memory is accepted.
  - `find_pattern` scans the exe file's sections. Probe: EMF's SimStep pattern finds SimStep although Kiln's own
    hook patched it in memory. REL32 follow works, and an ambiguous pattern is refused.
  - Probe: `hook("SimStep")` chained over Kiln's core SimStep hook: 410 detour calls in 17 s.
- **write_memory:** every page must be MEM_COMMIT + MEM_PRIVATE and not executable. Write-protected data pages are
  unlocked for the copy (`VirtualProtect`) and restored. The probe wrote item data, and a write into code was refused.
- **game_build:**
  - `known` = `build_info.verified`. sleep idles on an unknown build, and Kiln's verified builds are the ones whose
    layouts are checked.
  - `number` = the svn from `kiln/sigdb/builds/<side>/<sha>.json`. Probe: number 1285601, known 1, server 1.
- **Settings and mod info** (`settings.cpp`): see docs/kiln/emf.md. The INI parser and writer are kiln-emf's own and
  keep other sections and keys. `<game>/emf/config/<id>.ini` is a mirror, written when content differs. The schema
  snapshot follows contracts.md 5.5: `type` is the Kiln kind, `emfType` / `choices` / `options` / `step` / `group` /
  `enabledWhen` ride along.

## Signature aliases (EMF name → Kiln sigdb), server svn 1285601
`tools/sigport/sigdb_check.py` resolved Kiln's symbols offline against the server exe (sha `c5c9fe50…`). Each EMF
entry below matched the same RVA that EMF's pattern finds (`sigdb/evidence/emf-0.3.3-signatures.json`, hand ports):

| EMF | Kiln | RVA |
|---|---|---|
| SimStep, GetComponent, GetEntityTransform, GetTemplateComponent, SpawnWrapper, GameLog, AddLogListener, RegisterSingleton, WaitWorldJobs | same name (core) | 0x6c8f10, 0x64ed50, 0x64fd40, 0x64fae0, 0x620d00, 0x575890, 0x545890, 0x67e720, 0x6ca080 |
| RegisterProgram, RegisterAllPrograms, IterBegin, IterNext, CurrentEntityId, MarkWritten, ReadOther, QueuePush | `world/*` | 0x67ede0, 0x245960, 0x663b30, 0x65e610, 0x65b870, 0x66f040, 0x64e9e0, 0x5f9f60 |
| CreateEntity, DestroyEntity | `spawn/*` | 0x623ce0, 0x63e8a0 |
| InventoryTxCtor/TxInit/AddItems/Commit/Rollback/FreeArrays/RemoveItems | `items/*` | 0x3ed10 … 0x1b0d90 |
| SetTimeOfDay | `time/SetTimeOfDay` | 0x959820 |
| Save2WriteWorld, LoadSavegameStep, RequestServerSave | `saves/*` | 0x72cb30, 0x7b1a10, 0x7b9390 |
| EmitEvent | `gameplay/EmitEvent` | 0x5fcfc0 |
| ChatReceive, ChatLogAdd | `chat/ServerReceive`, `chat/ChatLogAdd` | 0x7bdb20, 0x915200 |
| AttrFind, AllocPendingHit | `combat/*` | 0x991820, 0x98c040 |
| SendServerInfo | `settings/SendServerInfo` | 0x7c2610 |
| NpcWorker, NpcSetState, NpcSetDecision, ServerWorldSetup, WriteOther | `emf/*` (sigdb/symbols/server.d/emf.json, EMF's patterns) | 0x1f7850, 0x1f6da0, 0x1f65b0, 0x233ee0, 0x64e7c0 |
| AttrAdd | `emf/AttrAdd`: the call target captured from EMF's call-site pattern | 0x190810 |
| ClientWorldSetup, QueueChatMessage, UiFrame, PregameState, CharacterSelected | `emf/*` (sigdb/symbols/client.d/emf.json, an overlay of server.d/emf.json) | client 0x3a5fd0, 0xd72cb0, 0xda2020, 0x872a10, 0xde9ee0 |
| PostSelectCharacter, PostSelectWorld, PostSessionArea, PostStart | none: EMF's patterns encode a struct offset that moved on client 26404966 | - |

- **Server:** 42 of 51 names resolve. The other 9 are client-only code.
- **Client** (enshrouded.exe 26404966, offline `sigdb_check.py` with the composed client.d): all 11 `emf/*` names
  resolve (server.d's 6 and client.d's 5; e.g. AttrAdd 0x3ef490, NpcWorker 0x45c810). The 4 `Post*` names are open
  RE.

## Step-aside test (lab `emf`)
The lab was set up as EMF's installer followed by Kiln's installer would leave it: EMF's `dinput8.dll` as
`kiln/chain/emf-dinput8.dll` with `[chain] chain\emf-dinput8.dll`, and EMF's `emf_core.dll`, `setup/`, `tools/`,
`vk/` in `emf/`.
- **proxy.log:** `chain: chain.ini [chain]: ...\kiln\chain\emf-dinput8.dll loaded`, `entry gate installed (1 chained DLL(s) handled)`.
- **emf_loader.log:** `held the server's start 115 ms until emf_core started`. EMF's core was already loaded at
  kiln-emf's `on_ready`.
- **kiln.log:** `kiln-emf steps aside: EMF's core is loaded (...\emf\emf_core.dll); the EMF mods are EMF's to load`.
- **emf_core.log:** `game sha256 c5c9fe50… (UNKNOWN build: signatures only)` … `signatures: 31 of 51 usable` …
  `a required signature is missing: EMF stays idle, no mods loaded`.
- **kiln.log, 4 s later:** `EMF's core went idle on this game build ...: no EMF mod runs. To run them with kiln-emf
  instead, switch EMF's loader off: put ';' in front of its line in kiln/chain.ini ...`.
- **With `;chain\emf-dinput8.dll`:** the proxy chains nothing, kiln-emf logs `...emf-dinput8.dll is EMF's loader,
  not loaded (switched off in kiln/chain.ini)` and loads the 14 DLL mods.
- **The server ran normally in both setups.** EMF names Kiln's proxy "ShroudForge" in its own log (`next to the game:
  dinput8.dll = ShroudForge`), but nothing breaks.

## Lab evidence (`tools/emf/lab-check.py emf`, svn 1285601, a copied world, no players)
- `kiln-emf: 14 of 23 EMF mod(s) loaded`. The other 9 are the 8 Lua script mods (status script, `[emf:lua] ready:
  8 Lua mod(s)`) and clock (side client).
- `[emf:big_stacks] raised the stack size of 2252 item(s) (x4.0, at most 9999)`. Kiln side: `PASS Kiln.Content:
  Material_T1_Wood_Logs max stack 1000 (original 250) = 250 x4`, `PASS Kiln.Content: 2252 items raised by the formula,
  1961 unchanged (do not stack), 0 differ`.
- server-admin results files: `ping` `{"api":21}`, `items` `{"count":4213}`, `settings.set` `{"requested":1.5,"value":1.5}`, the
  reset back to 2, `world_setting_set refused: experienceCombatFactor: allowed 0.25 to 2`, `save` `{"saved":true,"worldSlot":1}`.
- `[emf:loot_scale] hook loot_scale.factor at exe+0x941430`.
- `[emf:emf_probe] emf-probe done: 72 PASS, 0 FAIL`; `[mod:emf-check] emf-check done: 13 PASS, 0 FAIL`.
- The commands from `emf/commands.txt` all answered: herald `players` / `herald.say`, `sleep.status`, `tp.list` /
  `tp.pos`, `lua.status`, `qs.list`, `raids.status` / `raids.world on`, and the Lua mods' commands.
- No faulted callbacks. 0 FAIL across five lab boots.

## In a client (enshrouded.exe 26404966, GE-Proton11-11, offline)
Deployed with `tools/client/client.sh deploy --real --policy any` (kiln-emf through `bin/plugins` /
`kiln/.kiln-plugins`); `<client>/emf/mods/clock/` (clock.dll + mod.json, unmodified) was the only EMF mod.
- `kiln-emf 0.1.0 (EmfApi v21, 592 bytes) on the client; services: world 1, ..., client 1, ui 1, profile 1`.
- `[emf:clock] ready: the time of day in the bottom right corner (24 h)`; `kiln-emf: 1 of 1 EMF mod(s) loaded`
  (side `client` loads on the client).
- In the hosted world: `[service:kiln-ui.dll] ui: first overlay frame drawn (1920x1280)`. A screenshot showed "03:13"
  in the bottom-right corner, and "03:16" a few seconds later. Kiln's client probe logged `time 3.18 h` at 11:30:40
  and `time 3.29 h` at 11:30:50 (03:11 → 03:17). So `on_tick` on the client world, `world_kind`, `singleton(client
  world, g38_daytime::Daytime)` and `hud_set` through `kiln.ui` all work.
- Not covered: `notify`, prediction-world ticks seen by a mod,
  setting changes while running.

## Cost (lab, 3-minute runs, 14 DLL mods + 8 Lua mods, no players)
`kiln/logs/tick-stats.jsonl`, compared with the same lab without kiln-emf.dll:

| | step p50 / p99 | Post p50 / p99 | steps/s |
|---|---|---|---|
| without kiln-emf | 1.16-2.21 / 9.1-16.2 ms | 0.02 / 0.7-1.9 ms | 22.6-23.6 |
| with kiln-emf | 1.26-1.29 / 8.0-10.1 ms | 0.57-0.59 / 9.8-13.6 ms | 22.8-22.9 |

The step rate is within the host's noise; other labs ran at the same time. The Post time is the mods' own work.
`state/emf.json` reports it per mod (`tickMsPerStep`, `tickMsMax`):

| Mod | ms/step | worst call |
|---|---|---|
| server-admin | 1.09 | 722 ms, once: its 1 Hz `state.json` publish (players scan + write-through file moves under Wine) |
| lua (8 scripts) | 0.80 | 44.7 ms |
| big_stacks | 0.11 | 7.9 ms (its 2 ms slices while raising) |
| bulk_craft, loot_magnet, villager_jobs, companion, raids | 0.03-0.06 | 3-13 ms |
| herald, sleep, quick_stack, teleports | ≤ 0.006 | ≤ 1.4 ms |

## Tools
- `tools/emf/lab-check.py <lab>`: installs the probe and the cross-check, starts the lab, drives server-admin and
  `commands.txt`, and prints the per-mod table.
- `tools/emf/admin-op.py <server> <op> [k=v]`: one server-admin file request.
- `mods/tests/emf-test/probe/`: the conformance probe, an EMF mod that uses only `emf_api.h` (CMake target
  `emf-probe`).
- `mods/tests/emf-test/check/`: the Kiln-side cross-check (managed).

## Open questions / limits
- **EntityDiedEvent** is sent by the game only for actors it simulates near a player. kiln.combat's own reader saw
  none in the lab either, so delivery needs a real client. HitEvent delivery is proven.
- **NOTIFY_READY** for players is a 1 s timer. EMF measured 0.3-3 s, so a real readiness signal (the player's
  loading state) is open RE.
- **Not yet available:**
  - `program_write_other` and `read_events` over several events-in: `kiln.world` v1 has no API for them.
  - `hook_program`, `spawn_ex` with components, `wheel_open`.
  - Stamina/mana in `attribute_*`.
  - `for_each_entity`, programs and `on_event` on client worlds.
- **Client side** (HUD via `kiln.ui`, notes, client and prediction ticks) is built but not yet run in game.
- `NOTIFY_READY`, the HUD layout (positions are estimates of EMF's corners) and `notify` styling are approximations.
