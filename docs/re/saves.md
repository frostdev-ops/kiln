# World saves on the dedicated server (svn 1285601)

Evidence for `kiln.saves` (native/services/saves, managed/services/Kiln.Saves). Build: dedicated server svn 1285601
(Steam build 26404334, exe sha256 `c5c9fe50…`), image base `0x140000000`; addresses are RVAs. Old build 1273043
(that build's exe) in brackets where it was checked. Static analysis: python capstone,
a `.pdata`-based caller walk (`E8 rel32` scan + chained unwind info), `tools/sigport/sigdb_check.py`. Runtime: lab
`saves` (a copied save, world `11111111-2222-4333-8444-555555555555`), no player, test mod
`mods/tests/saves-test`. Starting point: EMF 0.3.3 docs (MIT) `engine_parts/re_saves.md`, `plumbing.md` 3.2-3.5,
`ENGINE_MAP.md` (client builds 1269579/1273043).

## Functions

| Name | RVA [1273043] | Signature / role | Source |
|---|---|---|---|
| requestServerSave | `0x7b9390` [`0x7afea0`] | `void (GameServerInstance*)`: if `inst+0x18AA == 0`, save state `ss+0x78 == 0` and `ss+0x70` bit 2 (world running): sets bit 0 (pending), copies the play time `inst+0x17B8` to `ss+0x80`, state 1, `inst+0x17B0 = now` (ns, `0x55cf40`). `ss = *(inst+0x20)` | disasm; EMF pattern |
| serverInstanceTick (Run) | `0x7bbeb0` | per server tick: ECS world update `0x6c8e40` (which calls SimStep `0x6c8f10`) at `+0xc64`/`+0xd16`, then serverSaveTick at `+0xdbb`, then the autosave timer at `+0xfad`: `if (now - inst+0x17B0 > 0x6FC23AC00 /*30e9 ns*/) requestServerSave(inst)` | caller walk |
| serverSaveTick | `0x7c2d00` | `(result*, saveSys, ctx, u8 shutdownFlag = inst+0x1820)`; runs serverSaveStateMachine `0x7c3aa0` (which waits for the previous Save2 job `0x734270`, snapshots the ECS world `0x62df70` = ecsCreateSavegameSnapshot, hands the blobs to Save2) | EMF patterns, caller walk |
| save2WriteWorld | `0x72cb30` [`0x724360`] | `void (Save2System*, sd2User*)` on the Save2Thread (called once, from its loop `0x729620`). Writes `<saveDirectory>/world<N>.ksd` and its `backup/` copy | EMF pattern |
| loadSavegameStep | `0x7b1a10` [`0x7a8780`] | `KeenResult* (out, inst)`: instance state 3; `out->code` 1 while loading, 0 once (called from `0x7bd610`) | EMF pattern |
| serverShutdown (state 6) | `0x7b9060` | first call: `inst+0x1820 = 1`, `requestServerSave` unless `reason == 2`, then the final client saves (`0x70eaf0(save2, handle, flag)` for the 16 player records at `inst+0x1E0`, stride `0x148`); returns "busy" while `ss+0x70` bit 0 or `ss+0x78` is set or a player's final save runs, with a 10 s limit (`0x2540BE400` ns) | disasm |
| message dispatcher | `0x7bdb20` | a jump table on the message type; one case sets a player record's state 3 (in game) to 4 (removing entity) and calls `requestServerSave`: the game saves when a player leaves | disasm |

`requestServerSave` has exactly these three callers on both builds. Kiln tells them apart by the return address
(sigdb symbols `saves/RetAutosave` `0x7bce74`, `saves/RetShutdown` `0x7b90bb`, `saves/RetPlayerLeave` `0x7be2df`
[`0x7b3904`, `0x7afbcb`, `0x7b4d6f`]); its own calls set a thread-local reason first.

### Order inside one server tick
1. ECS world update → SimStep (Kiln's Pre, the step, Kiln's Post).
2. serverSaveTick: a pending save is snapshotted and handed to the Save2Thread here.
3. Autosave timer → `requestServerSave`.

So a save requested by the timer in tick N is snapshotted in tick N+1, after that tick's sim step. Kiln's hook on
`requestServerSave` runs on the server thread, which is the tick thread (`saves: world loaded: ... server thread 388
(tick thread)`), so `WorldSavingEvent` handlers run before the snapshot. Kiln calls `WaitWorldJobs(world)` first
(SimStep returns with grid jobs running), which makes the handler context equal to Post.

## Layouts

- **GameServerInstance** (unchanged from 1273043): `+0x20` ServerSaveSys*, `+0x1C8` Save2System*, `+0x1754` u16 world slot
  id, `+0x1756` u8 slot valid (0 = adventure), `+0x17B0` i64 last save request (ns), `+0x17B8` play time (ns),
  `+0x1820` u8 shutting down, `+0x18AA` u8 "no saves" (requests refused while set), `+0x18C0` u8 new world.
- **ServerSaveSys**: `+0x08` Save2System*, `+0x70` flags (bit 0 pending, bit 1 enabled, bit 2 world running, bit 3 error),
  `+0x78` u8 state (0 idle, 1 requested ...), `+0x80` play time copy.
- **Save2System on the dedicated server** is not the client's layout (EMF's `+0xB7287`/`+0xB728C`/`+0xB7294`/`+0xB72A4`
  are client offsets): `+0xDF` u16 world slot id, `+0xE4` u64 result of the last world job (byte 0: 0 = ok; 3 while
  writing), `+0xEC` 16-byte world GUID, `+0xFC` u32 revision (`+0x10` from the GUID; `save2WriteWorld` builds the
  container ref with `rev + 1` and stores it back only on success, `0x72dbe6`). Captured as sigdb holes from
  `save2WriteWorld` itself (EMF's "code offsets" idea): `mov [rcx+0xE4], rax` at `+0x5a`, `lea rsi, [rdi+0xEC]`,
  `movzx eax, word [rdi+0xDF]` at `+0x2c7` and `+0x36e`, `mov eax, [rsi+0x10]; inc eax`.
- **Slot id**: low byte = index + 1, high byte = storage (0 Local). The lab's world is `0x0001` = `world0.ksd`.
- **World file header** (`<saveDirectory>/world0.ksd`): `"SV2C"`, `+0x0C` GUID (Windows byte order, .NET
  `new Guid(bytes)`), `+0x1C` u32 revision, `+0x20` u32 UTC seconds of the write. `backup/world0.ksd.backup-0` has the
  same revision as the main file (the game writes both); `backup-1..9` are older revisions at uneven spacing.
- All of it resolves on both builds without edits (`sigdb/symbols/server.d/saves.json`, 13 constants, 9 symbols).

## What a save looks like (lab, kiln.log)

```
saves: save #2 accepted (autosave, revision 3007 next, step 988, handlers run now, before the game's snapshot)
saves-test: INFO Saving Autosave revision 3007 (before snapshot: True, step 988): counter -> 1
saves: world written: revision 3007, world0.ksd, header ok (rev 3007, 1793770110), 119.9 ms after the request (autosave save #2, Save2Thread 384)
saves: save #4 accepted (request, revision 3008 next, step 992, ...)
saves: world written: revision 3008, world0.ksd, header ok (rev 3008, 1793770110), 46.9 ms after the request (request save #4, Save2Thread 384)
saves: save #10 accepted (shutdown, revision 3011 next, step 1210, ...)
saves: world written: revision 3011, world0.ksd, header ok (rev 3011, 1793770119), 56.6 ms after the request (shutdown save #10, ...)
```
- Autosaves come every 30 s (the timer resets on any request, including Kiln's). A request to file complete took
  30-120 ms; the file's header revision always equalled the Save2System revision read in the hook.
- The game logs nothing for a world save; `[server_save] Clear Chunk Persistent Data` appears only at load/shutdown.
- Refused requests (`Refused = 1` at boot) are the timer's calls before the world runs (flag bit 2 clear).
- The shutdown save comes ~100 ms after `[app] Trigger gameflow shutdown, exit: Ctrl_C`, inside the 218 ms the game
  spends in its Shutdown state. Kiln.Host unloads mods on the stop signal before that (`lifecycle: SIGINT received;
  stopping mods ...`), so Kiln.Saves gives mods the shutdown `WorldSavingEvent` at `ServerStoppingEvent` instead.

## Entity identity across a restart (for EntityData)

The game has no persistent entity id: loaded entities are renumbered (from 1025 on the client, EMF), the save keeps
template + CurrentTransform + a few components (EMF: CurrentTransform, OverrideLevel, CustomPlayerString,
BaseIdComponent), and Kiln's SpawnTime marker is not restored (docs/re/spawn.md, Persistence). Templates with the
DoNotSave tag never reach the save (3,654 of 12,269; most enemies, the generic loot pickup). Kiln therefore keys an
attachment by **template GUID + position at the save** and matches loaded entities of that template: props come back at
exactly their saved position (matched within 0.5 m), creatures/NPCs within 16 m (nearest unclaimed). Villagers are
unique per template (EMF ENGINE_MAP), so the radius is not a problem there. Player entities are slots (1..16) and map to
the player's Steam id instead.

## Verified in the lab (saves-test, 8 boots, 54 PASS / 0 FAIL)
- SIGINT restart: `the world file's revision 3012 is the shutdown save (ledger: {"rev":3012,"counter":6,...,"reason":"Shutdown"})`,
  `mod data generation 3012 (Exact, 0000003012-1793770317.json, 3 bundles)`.
- kill -9 during back-to-back saves (512 KB payload per save), three times: `after kill -9 #1: world file revision 3047,
  mod data Exact generation 3047: counter 41 (ledger 41) ... ledger sha match True`; in an earlier run two of three
  kills landed between the world write and Kiln's confirmation and loaded `(Staged, 0000003070.pending.json)` with the
  right data. A kill during a blob write left `….json.tmp-252-137` (144 bytes, never referenced); no manifest or blob
  was ever torn (`sidecar intact: 36 manifests, 108 blob references, 0 bad`).
- Restore of a Kiln backup at boot: `saves: restore applied before the game started: 20261104-053120-t1 (2 files)`, then
  `world file revision 3009 = backup t1's revision 3009`, `mod data generation 3009 (Exact, 0000003009-1793770280.json)`.
- The game's own backup file of the abandoned timeline (`world0.ksd.backup-3`, revision 3136, written before the restore)
  put in place offline: `mod data generation 3136 (Exact, 0000003136-1793771327.json)`, counter = ledger. Revision
  numbers repeat across timelines; the file time in the manifest name tells them apart.
- Entity attachments: a saved barrel `(was 2542) is back as entity 1311 with its attachment`; a Vukah's (DoNotSave) was
  session only; a destroyed barrel's attachment was dropped within 2.5 s.

## Safety notes
- Never write into the save folder's root: the game's migration scan moves unknown short file names there
  (EMF `re_saves.md` §4). Kiln's sidecars live in `kiln/data/worlds/<id>/`, backups in `kiln/backups/`.
- Restores replace `world<N>.ksd` only from `kiln_plugin_main`, before the game's entry point (it has not opened its save
  yet); the files replaced are copied to `kiln/backups/_before-restore-<stamp>/` first.

## Open questions
- `inst+0x18AA` (requests refused while set): when is it set? Not seen set on the dedicated server.
- The dispatcher's message type for the "player left" save (no client in the lab: client checklist).
- Save2's retry path (serverSaveTick retries up to 4 times on error): never seen; a failed write would show as a
  `FAILED` event and the next success as `WRITTEN` with the same revision.
