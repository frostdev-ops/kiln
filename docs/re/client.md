# The game client (`enshrouded.exe`): what Kiln needs to know

Build: Steam build 26404966, `enshrouded.exe` sha256 `ce6fdba63e27744943b695921383a29133285d359e07671f6ca33a47e2147618`,
CodeView `3DDA78E3796049C0B30CD1604A0E7846-205`. Static analysis of a copy (python + capstone, `tools/sigport/*`), the
game's own logs in the client folder, EMF 0.3.3 docs (`ENGINE_MAP.md`, client builds 1269579/1273043; MIT) and the
ShroudForge client profile `modules/game-runtime/profiles/enshrouded/client/26404966.json` (MIT). RVAs are this build's.
Runtime confirmation: section "Runtime" below. Lines marked (static)
were not observed directly.

## Identity and boot
- PE: `.text` 0x1000..0x13ce6f0 plaintext, entry point 0x2a05310 inside a SteamStub `.bind` section (RX). Kiln's entry
  gate (ShroudForge `startup_gate.h`, which ShroudForge used on this client) patches the 14 entry bytes and restores
  them before the stub runs, so the stub sees its own code unchanged; it then runs the DRM check and jumps to the real
  entry in `.text`. Kiln boots before the stub (it reads `.text` and hooks it then). The SteamStub 3.1 header
  (XOR-decoded at entry - 0xF0, signature 0xC0DEC0DF, app id 1203620) has flags 0x6 = NoModuleVerification |
  NoEncryption, original entry 0x134ab78: the stub neither decrypts nor checksums `.text`, so early hooks are invisible
  to it; it only does the ownership and debugger checks. (static)
- shroudlink's client addon, which Kiln's start lets finish its scans while the entry point is held, calls one game
  function during them: its item-tooltip state init (`48 8B 05 ?? ?? ?? ?? 33 D2 48 89 41 08`, one match, RVA 0xb1f1a0
  on build 26430625, `5d9ef2f1…`). It is a leaf that only fills the caller's buffer with zeros and with one 8-byte value
  it reads (13 times) from `.rdata` 0x1d8a7b8: read-only, not in the IAT, no base relocation, 0 in the file, and the stub
  does not touch `.rdata` (flags 0x6 on this build too). So the value is constant; calling it before the game's own
  start-up code changes nothing. (static)
- Imports `DINPUT8!DirectInput8Create` and `dbghelp!MiniDumpWriteDump`, like the server: the same proxy works.
- Side detection: the RSDS PDB path is `...\games\game38\game38_client\...\enshrouded.pdb` (server:
  `...\game38_server\...\enshrouded_server.pdb`). The core uses the PDB file name, then the exe name
  (`image.cpp: detect_side`).
- DLL load order: GE-Proton11-11's wine (11.0) loads a native `dinput8.dll` from the game folder without any override.
  Test: the client-core lab server started as `WINEDLLOVERRIDES="mshtml=" wine ./enshrouded_server.exe` (no dinput8
  entry; neither prefix has one in `[Software\\Wine\\DllOverrides]`) booted Kiln normally (`kiln.log`: "boot complete",
  "first server-world step"). Proton's launcher adds no dinput8 override for app 1203620 (`proton`: only CW-bug
  entries for other app ids). A Steam entry for 1203620 can carry
  `WINEDLLOVERRIDES="dinput8,dbghelp,winmm=n,b" DXVK_FRAME_RATE=117 %command%` (left from ShroudForge), which also
  works. Older Proton/Wine builds may need the override: keep it as the documented fallback.

## Same engine, same server code
The client exe contains the server-world code (it hosts single player and co-op games). The world struct layout is the
server's (`world.storage` 0xE179C0, kind at storage+8, clock +0x4DC, period +0x4C0, singleton map +0x7462F8, programs
+0x746350 / 0xE16F50; ShroudForge's profile agrees: `executionEntityManagerOffset` 14776768 = 0xE179C0,
`executionModeOffset` 0xE179C8 = kind).

`tools/sigport/sidecompare.py <server exe> <client exe>` resolves both sides' definitions and compares every symbol found
on both sides instruction by instruction (registers kept; large displacements, rip-relative operands and branch targets
masked) over its .pdata function (all of `server.d` at that time):

- 111 symbols resolve on both sides, 105 of them structurally identical (similarity >= 0.95).
- Core set (`server.json`): all 11 resolve with unchanged patterns and are identical: SimStep 0xa2c580, WaitWorldJobs
  0xa2d6f0 (WaitTaskGroup 0x8f0a50), GetComponent 0x9b2de0, GetEntityTransform 0x9b3dd0, GetTemplateComponent 0x9b3b70,
  SpawnWrapper 0x984d90, GameLog 0x8b53f0, AddLogListener 0x885db0, RegisterSingleton 0x9e26a0 (+ all field probes).
- Below 0.95: `chat/ServerReceive` 0.64 (same function: same GameServer offsets +0x10/+0x1d0/+0x1698..., the client
  build keeps the GameServer in r15 instead of r13, has a 0x2C0 smaller frame and two log calls fewer);
  `chat/ReceiveAddLine`, `saves/RetAutosave`, `saves/Save2WriteWorld` (mid-function sites: compared from the site to the
  function's end); `time/WeatherRemoveInstance` 0.55 (really different: weather instance stride 0x50 vs 0x48).
- Features: 61 on the server; before the client overlays 54 of them also resolved on the client (off: `time.weather`,
  `saves.write`, `saves.reasons`, `gameplay.build`, `chat.receive`, `time.sync_probe`; `net.kick/players/transport`
  belong to the server-only `net.json`: the client uses its own standalone `client.d/net.json` with
  `net.client`/`net.host`). With the overlays below, everything but `time.weather` (and `content.loot`, then off on
  the server too) was on: 115 symbols on both sides, 108 identical. Client only: `world.lifecycle`,
  `client.local_player`, `net.client`, `net.host`. Since then `client.d/time.json` ports `time/WeatherUpdate` for the
  client, and the newest client record (26430625, `sigdb/builds/client/5d9ef2f1….json`) has all 177 features on,
  `time.weather` and `content.loot` included.

Ported for the client (`sigdb/symbols/client.d/`, overlays over `server.d`):
- `chat.json`: ReceiveAddLine. Client site 0xe61fea in ServerReceive 0xe61540:
  `49 8B 57 {gs.chat:d8} 4C 8D 4D ?? 48 89 44 24 40 48 8D 8D ?? ?? ?? ?? 8B 85 ...  C6 45 ?? 00 E8 {ChatLogAdd:rel32}`
  (server: `49 8B 55 ..`, `48 8D 4D ??`). Also captures `chat/gs.chat` = 0x60, which the whole chat service needs.
- `time.json`: ServerGameDaytimeSync 0x4a1bc0. Its tail `4D 8B 80 {sg.daytime:d32} E9 {DaytimeSyncIfChanged:rel32} CC`
  matches twice in the client (0xd2f3a6 is another function); anchored on the int3 before it, offset 1.
  `sg.daytime` = 0x2D0 as on the server.
- `saves.json`: Save2WriteWorld 0xe1b470 (same prologue) keeps its first argument in rsi (server rdi), and that object
  is much larger in the client: guid +0xB72A4, slot +0xB7297, result +0xB729C (server +0xEC, +0xDF, +0xE4; EMF found
  +0xB729C/+0xB7297 on client 1273043). The four field probes are re-cut with the ModRM bytes open (they match on both
  exes). RetPlayerLeave 0xe61c83: `42 C6 84 38 EA 01 00 00 04 E8` (server `.. 28 ..`, r13 vs r15).
- `gameplay.json`: the op.player probe after EmitEvent (0x49313e): the client does not reload rsi from the stack and its
  local sits at rsp+0x40; op.player = 0x14C on both.
- Not ported: `time/WeatherUpdate` (server 0xa23810). The client's 0xaac5a0 (the only caller of WeatherUpdateLayers
  0xaac170) is a larger function: client-side phenomena after the layer update (`cmp dword [rdi+8], 1`, a loop over
  `[rdi+0x88]`), and WeatherRemoveInstance 0xaa9f70 indexes instances with stride 0x50 (server 0x48). The mode field
  stays at +8 (`39 6F 08` / server `83 7B 08 00`). kiln.time's weather ops are server-struct code: its owner decides.

## Worlds in the client
Kinds (u8 at storage+8): 0 server, 1 prediction, 2 client (EMF; log tags `ecss`, `ecsp`, `ecsc`).

- WorldCreate 0x983060 `(desc*) -> world*`: allocates 0x15FD3C0 bytes, constructs, `world+0xE179C8 = desc[0]` (the only
  write of the kind byte in .text), returns the world. Two callers:
  - 0xe53b7f in GameServer init 0xe53930: the server world, stored at `GameServer+0x1D0` (EMF: GameServer init).
  - 0x84cf96 in 0x84ccc0 (`mov byte [rbp], 2`): the client world, stored at `GameClient+0x1D0`.
- WorldDestroy 0x9a1ea0 `(world)`: `cmp byte [rcx],3; mov byte [rcx],3` (state "shutting down"), tears the world down
  and tail-jumps the allocator's free with the world. Callers: 0xe517c9 in the GameServer teardown 0xe51680
  (`mov rcx,[rbx+0x1d0]; call; mov [rbx+0x1d0], rbp`) and 0x84ea37 in the GameClient teardown 0x84e9e0 (same shape).
  The same function exists on the dedicated server at 0x63de10 (pattern unique on both; the server sigdb does not use it
  yet). Pattern (`client.json`): `48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 54 41 55 41 56 41 57 48 83 EC 40
  80 39 03 48 8B F9 74 03 C6 01 03 4C 8D B1 {world.storage:d32} 45 33 FF 45 38 7E {storage.kind:d8} 0F 85`.
- SimStep 0xa2c580 has three callers, as on the server:
  - 0xa2c4ee in 0xa2c4b0: `if (world.state == 2 && !byte[world+0x15F55CC]) SimStep(storage, 1)`; called twice from the
    GameServer update 0xe5f990. The server world.
  - 0xa1fbbe in 0xa1fa00 "world update" (no direct callers: called indirectly): `ticked = tick(+0xE17E9C) !=
    prev(+0xE17EA0)`, so a frame without a new tick calls SimStep with ticked = 0 (Kiln skips those).
  - 0xa28445 in 0xa282e0 (from 0xa00d00, from 0xa1fa00 with `world+0x15F56C0`): a sub-world stepped with the parent;
    likely the prediction world (EMF: "pointer to a second storage (prediction?)"). (static)
- Threads: the "ServerThread" app creation step starts thread "GameServer" at startup (client log: `Started thread ...
  (GameServer)` right after `start creation step ServerThread`); its callback 0xe525b0 (ShroudForge
  `threadCallbackRva` 15017392) calls 0xe61030, which calls the GameServer update 0xe5f990 and, on shutdown, the
  teardown 0xe51680. So the server world steps and is destroyed on the GameServer thread, which lives for the whole
  process. The client world steps from the client game update on the main thread (EMF 1.1) and is destroyed there.
  (static; the runtime check logs the thread of each world's first step)
- Rates: EMF measured the hosted server world at 60 Hz (16.6 ms) and the client world following the server's tick.
  (to be measured: `tick-stats.jsonl` lines carry `"world"` on the client)

Session states (strings 0x1d85d20: `Lobby`, `Host_Offline`, `Host_Online`, `Client_Online`; steps `HostOffline`,
`HostOnline`, `JoinOnline`, `JoinPlayers`). The client's logs (`<client>/backup/*.log`) show:
- Hosting (single player with "Friends" session mode): `[Session] ... from 'Lobby' to 'Host_Online'`, then
  `[play] start creation step GameServerCreate`, `GameServerInit` (about 11 s: `[ecss] Programs: 817`, `Initialized
  12,267 template infos`), `GameClientCreate`/`GameClientInit` (`[ecsc] ...`), `GameClientSpawn`.
- Joining: `'Lobby' to 'Client_Online'`; GameServerCreate/Init print their names and do nothing (no `[ecss]` lines):
  no server world in the process.
- Leaving: `[play] start destruction step GameClientSpawn ... GameClientCreate` (client world gone, the server sends the
  final client save), then `destruction step GameServerInit`, `GameServerCreate` (server world gone), then
  `'Host_Online' to 'Lobby'`.
Kiln derives the session from the worlds: server world alive = HOST, else client world alive = REMOTE, else NONE.

## Local player
- `LocalPlayerData` is a client-world singleton (EMF 1.5; string 0x152b740, registered from 0x3a5fd0).
- isLocalPlayer 0x3bb660: `39 4A 04 0F 94 C0 C3` = `*(u32*)(lpd+4) == id`, 24 callers (client programs), padded by
  int3 on both sides. `client.json`: `CC 39 4A {client.local_player.entity:d8} 0F 94 C0 C3 CC` (unique; without the
  padding a second hit at 0x13ca2d1). Entity ids are shared by the client and server worlds on a host (EMF).

## Services in a hosted world (audit and fixes)
Services hook functions that every world runs. In a client those hooks also fire for the client and prediction worlds,
and a second hosted game in the same process gives a new server world (seen at runtime: often at the same addresses).
Each service now resets its per-world state on the server world's `KILN_WORLD_STOPPING` (and, without the destroy hook,
when a new session starts) and binds the next world at its first step. Per service (docs/kiln/services/<svc>.md, "In a
game client"):
- kiln.world: RegisterAllPrograms hook checks the kind and re-registers into each new server world (unchanged).
- kiln.items: own-registration hook checks the kind; per session: accessors and registry re-verified, queued requests
  DROPPED.
- kiln.players: RegisterSingleton captures only kind-0 worlds; per session: LEFT for everyone at STOPPING, singletons and
  validation dropped, the next world validated.
- kiln.time: binds the server world's `g38_daytime::Daytime` / `WeatherSystem` singletons (same objects as the hooks saw
  on the dedicated server: checked in the lab); ignores another world's Daytime; weather ported to the client.
- kiln.chat, kiln.settings: drop the GameServer / instance (settings also the world's original values) at STOPPING.
  kiln.settings on a client: no server config (`save()` refuses with a message).
- kiln.saves: client save folders (Steam cloud / local, by slot storage), read only; staged restores refused on a client;
  per session: state NONE, the next load reported again.
- kiln.spawn: world key from kiln.saves on a client; sidecar saved at STOPPING, reopened per session.

## Runtime
Two launches through Steam (`steam://rungameid/1203620`, GE-Proton11-11, an existing launch option), Kiln
deployed with `tools/client/client.sh deploy --real --policy any --probe`. kiln.ui and kiln.net were left out. 
- Boot: `side: client (pdb enshrouded.pdb)`, `sigdb: 185/187 symbols, 299/300 constants resolved in 516 ms` (only
  time/WeatherUpdate + weather.mode), `UNVERIFIED (policy any) [client]`, `SimStep hooked`, `WorldDestroy hooked`, every
  service DLL loaded, `.NET up in 36 ms`, boot complete 0.7 s after start. The SteamStub ran normally after the gate.
- Main menu: no world steps, nothing ticks (kiln.log silent, `tick-stats.jsonl` empty), the game is responsive.
- Hosting (new world, `world2.ksd`, slot 0x0103; Host_Online in launch 1, Host_Offline in launch 2):
  - The server world starts first, on thread 692/688 (GameServer), during GameServerInit, at 24.7 steps/s while
    loading, then 60.0.
  - The prediction and client worlds start about 4.5 s later, on thread 392/388 (the main thread), at 60.0
    (prediction 80-90).
  - `client: LocalPlayerData ... found`, `local player entity 1`. The probe shows the player's position changing as
    the character walks, `players: Alder#1` and 22.5k entities.
  - kiln.world logs `RegisterAllPrograms for a world of kind 2: not the server world` and registers its 4 programs into
    the server world only; its own program runs.
- Leaving: `client world ... stopping`, then `prediction world ... stopping with the client world` (added after
  launch 1, where the prediction world kept a stale pointer), then `server world ... stopping`, 0.5 s apart, matching
  the game's destruction steps. kiln.saves observes the "player left" and "shutdown" saves in between.
- Session 2 in the same process: `server world 0x3006b0bed80 started (session 2)` (same addresses as session 1).
  kiln.world re-registers its programs, local player 1 again, the probe works.
  - Finding: the first step's `clock_delta` jumped (+4294957286), because the clock restarts with a new world. Fixed
    in the core: 0 at a world's first step and never negative.
- Exit: the game's shutdown log is the same as vanilla (`Shutdown 'Steam' done`, last line `Shutdown 'Debug' ...`), no
  crash, the process ends in about 15 s.
- Services, as seen in the logs (read-only run):
  - kiln.saves: its save-folder logic looks for `S:\steamapps\common\Enshrouded\savegame`; the client saves through
    Steam cloud (`header unreadable` each time).
  - kiln.settings: looks for `enshrouded_server.json` in the client folder.
  - kiln.spawn: tracks under `unknown-world`, because no save is found in that folder.
  - kiln.players: saw the session-1 leave only at session 2's first step ("left slot 0 ... [event 2]").
  - kiln.time: Daytime captured once; same address in session 2.

## The camera, health-bar line of sight, and a frozen client (26430625, 2026-11-06)
A hosted creative session froze about two minutes after an alt-tab (the system menu, HudScreen 2, open; the player
standing still). Main thread at 100 % CPU inside the game; the server thread waiting on a lock behind it;
`tick-stats.jsonl`: client and server world steps of 18.4 s each from then on. Evidence: a stack and register dump of
the live process (read-only gdb), the program table, Ghidra decompiles. **[rt] [dis] [dec]**
- **What spins:** `fill_ui_healthbar` (client program, update `0x30bd70`, descriptor `0x1e2b140`). For every entity with
  `NetworkHealth` + `RenderTransform` (enemies, wildlife, minions) within **150 m of the local player's
  `RenderTransform`** (`comiss` against 22500 at `0x30bfab`), it casts a voxel line of sight (`0xe88ee0` → `0x9b2b50`
  → `0x9b2bf0` → the DDA `0xa0f770`, 0.5 m cells, `0x9c4e30` per cell) **from `ClientPlayerInput.data.camera.position`**
  (the local player's, +0x20, a `WorldPosition` in 32.32 fixed point) to the entity + 1.5 m. Nothing bounds the ray's
  length: the camera is assumed to be near the player.
- **The bad value:** the ray in the frozen frame went from the camera (3790.55, **-2147482752**, 1467.67) to an entity at
  (3811.75, 767.16 - 1.5, 1523.25); the player's `RenderTransform` was (3792.98, 846.24, 1469.41). So the camera's x and
  z were normal and its Y was 2^31 m below the player's: **Y = INT64_MIN + (about 850-960 m)**, the signature of a
  float that was out of range or NaN when the game converted it (`cvttss2si` gives 0x8000000000000000) and added it to
  the Y of a position near the player. The DDA then walked about 1.5 billion cells per health bar, every step.
- **Who writes the camera:** `0x3af340` (chunk `0x3af43e`) fills `ClientPlayerInput.camera` from the 3C camera object:
  position and orientation from `0xe98ea0`, distance `+0x14a4`, fovY `+0x20c4`, aspect `+0x20b8`, near/far
  `+0x20bc`/`+0x20c0`. `0xe98ea0` builds the transform as pivot (`+0x1468` WorldPosition) + the offset `+0x148c` rotated
  by the camera's three quaternions (`+0x14b0`, `+0x14c0`, `+0x14d0`) − distance × forward, each term converted float →
  int64 separately. The pivot comes from `0xec4f60` (camera update, unclamped dt from the step's ns): blended camera pose
  (`0xe85b20`, per component, int64 deltas through float) + a direction × `+0x68` − direction × `+0x217c`; the pose from
  the camera modes (`0xec0b90` → `0xe76340`). `player_camera_transform` (`0x2b7900`) adds the camera shake (`0xec02e0`;
  a glider wobble while `DynamicNetworkLocomotion.state` is 4) to the render camera, not to `ClientPlayerInput`.
  Which float overflowed was not found statically **[hyp]**; no Kiln hook is on this path (the client's Kiln hooks that
  session: kiln.rules' fly gate, cursor flags and the three glider hooks, all no-ops for that player's rules then: flags
  0x1f, sink 1; kiln-ui passes focus messages through and only holds `SetCursorPos`/`ClipCursor` while its own menu is
  open).
- **Why it looked like a freeze on alt-tab:** the camera may have been wrong for a while before; the rays start only
  when a health-bar entity comes within 150 m of the player, and each then costs seconds.
- **Kiln's guards (kiln-rules, client only; `sigdb/symbols/client.d/rules.json`):**
  - `rules.healthbar_guard`: a mid hook on the `call` at `0x30c4af` (`rules/client.HealthbarRay`, scoped to
    `rules/client.Healthbar` = `0x30bd70`; the call target `rules/client.LineOfSight` checked by its prologue; the
    player's slots checked by two patterns: x in xmm14, y in xmm15, z at `[rbp + 0x278]`). A ray longer than 1 km, or
    a non-finite one, is made zero-length (start := end; the DDA returns at once under 1e-20 m: "nothing in the way").
    A replacement point must be finite and in the world: end, start, player, then the origin as a final fallback.
    Normal rays at most 1 km long are unchanged; the camera check only reads game memory.
    A camera outside the world logs once per session at W (both points, the player) and then a count every 30 s; a
    camera inside the world but over 1 km away (a building flight far from the character: FlyAnywhere leaves it
    behind) logs once at I.
  - `rules.camera_check`: read only, a mid hook after the last camera field (`0x3af55f`, `rules/client.CameraInput`;
    the camera transform `0xe98ea0` checked by three patterns that fix the field offsets). A camera outside the world
    (1 km margin) or with non-finite orientation/lens logs once at W: the camera, the player (from the health-bar
    program or, on a host, the server world), the orientation and lens, the last frame times (the time between two
    camera writes) and the session's longest, then the camera object's `+0x50` target, `+0x68`, `+0x6c`, the pivot
    `+0x1468`, the floats `+0x1480..+0x14dc` (offset, distance, the three quaternions) and `+0x217c`. A camera more
    than 1 km from the player for 120 frames, after it was once near, logs the same once at I.
  The camera's own view stays wrong: they keep the game running and name the source; the overflow itself is the
  game's. A better fix once the source is known: put a camera more than a few km from the player back on it.
