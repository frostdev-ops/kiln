# Kiln in the game client

Kiln runs in `enshrouded.exe` as well as in the dedicated server. In the client it boots the same way (dinput8 proxy,
entry gate, core, symbol database, services, .NET host). When the client **hosts** a world (single player or co-op
host), the game runs a full server world in the same process, and all of Kiln's server-side services work there. When
it joins someone else's world, only the client world runs locally. RE evidence: `docs/re/client.md`.

Status: **runtime-verified** on client build 26404966 (`sigdb/builds/client/ce6fdba6….json`,
`"verified": true`): two launches through Steam (GE-Proton11-11), a new throwaway world
hosted online (Host_Online) and offline (Host_Offline), two hosted sessions in one process, two clean exits. The server
regression passes too: `build c5c9fe50…: verified`, all services load, tick-stats format unchanged. Not covered yet:
joining a remote world (REMOTE), service write paths on the client, kiln.ui / kiln.net.

Measured (kiln.log, tick-stats.jsonl, probe mod):
| | server world (hosted) | client world | prediction |
|---|---|---|---|
| thread | GameServer (692 / 688) | main (392 / 388) | main |
| steps/s | 60.0 (clock 60/s) | 60.0 (clock 60/s) | 80-90 (re-simulation) |
| step p50 / p99 | 2.4 / 10 ms | 1.06 / 2.0 ms | 0.2 / 2.1 ms |
| Kiln Pre p50 (13 subscribers) | 0.029 ms | 0.003 ms | - |

## Boot and side
- The proxy is the same `dinput8.dll` as on the server. The client imports `DINPUT8!DirectInput8Create` too. The entry
  gate patches the SteamStub `.bind` entry point and restores it before the stub runs.
- The **side** comes from the exe's PDB name (`enshrouded.pdb` = client, `enshrouded_server.pdb` = server), falling
  back to the exe name. `kiln.log`: `side: client (pdb enshrouded.pdb)`.
- The side selects the definitions (`kiln/sigdb/<side>.json` + `<side>.d/`), the build records (`sigdb/builds/<side>/`)
  and the `kiln.json` section for that side.
- Steam launch options: none are needed with GE-Proton11-11. Its wine loads a native `dinput8.dll` from the game folder
  without an override (tested, see `docs/re/client.md`). For older Proton builds the fallback is
  `WINEDLLOVERRIDES="dinput8=n,b" %command%`. The user's entry already has
  `WINEDLLOVERRIDES="dinput8,dbghelp,winmm=n,b" DXVK_FRAME_RATE=117 %command%`, which works as is.
- The game's crash reporter is its own exe started again (`enshrouded.exe --upload-dump "<dir>" --process_duration <t>`, also on the
  dedicated server). The proxy leaves that process alone: no core, no chain, kiln.log, proxy.log and play-once.ini untouched, so the
  crashed session's kiln.log stays `kiln.log` until the next real start.
- `KILN_DISABLE=1 %command%` starts the game without Kiln (and without chained DLLs). Removing `dinput8.dll` does the
  same permanently. The launcher's "play vanilla once" does it for one start without touching Steam: it writes
  `kiln/play-once.ini` `mode=vanilla`, which the proxy deletes as it reads it ([profiles.md](profiles.md)).

## Profiles, chain loading, native mods
On a client, the Kiln Launcher activates a **profile** by writing `<client>/kiln/profile.json`: the managed, native,
plugin and EMF mods to load (usually `Z:\…` paths into the launcher library), the config folder and the per-mod data
folder. Steam's Play button then boots that profile. No profile.json = the drop-in folders as before. Another tool's
`dinput8.dll` (EMF's loader) is moved to `kiln/chain/` and loaded by Kiln's proxy from `kiln/chain.ini`. Details:
[profiles.md](profiles.md), [native-mods.md](native-mods.md). What the last start did: `kiln/logs/proxy.log`,
`kiln/state/{boot,mods,native}.json`, or `tools/client/client.sh status`. For a bug report: the launcher's Logs page,
"Save report...", or `kilnctl report --dir <client>` ([launcher/support-report.md](launcher/support-report.md)).

## Install, remove, inspect: `tools/client/client.sh`
```
tools/client/client.sh status                 # build record, installed?, kiln.json, profile, play-once, chain.ini,
                                              # last boot (state/boot.json, mods.json), game running?, launch options
tools/client/client.sh deploy --real --policy any [--core-only] [--probe]
tools/client/client.sh undeploy --real [--purge]
tools/client/client.sh deploy --real --policy any --mod <built mod folder>   # extra mods; undeploy removes them
                                              # (an active profile with kiln drop-ins off gets "dropins" on; undeploy
                                              # puts it back)
tools/client/client.sh logs [n] | report [n] | check | backup-saves
```
- The default target is the Steam folder (`~/.local/share/Steam/steamapps/common/Enshrouded`). Writing there needs
  `--real`. `--dir <folder>` targets a copy instead; a copy only needs `enshrouded.exe`, not the 40 GB of data.
- Deploy writes only `<client>/dinput8.dll` and `<client>/kiln/` (core, services, Kiln's own plugins such as
  kiln-emf, recorded in `kiln/.kiln-plugins`, sigdb, .NET, host). A `dinput8.dll` that is not Kiln's (another loader) is
  copied to `~/.local/share/kiln/client-backups/<time>/` first, along with the previous `kiln.json`.
- Undeploy removes Kiln's `dinput8.dll` and restores the original one. `--purge` moves `kiln/` into a backup folder;
  it never deletes it.
- Both refuse while the game runs. Saves are never touched: they live in Steam's `userdata/<id>/1203620/remote/` and the
  prefix. `backup-saves` only copies them out.
- `--core-only` deploys no service DLLs (staged tests). `--probe` adds the read-only probe mod `tools/client/probe`.

## kiln.json per side
```json
{ "policy": "verified-only", "clr": true, "plugins": true, "log_level": "info",
  "client": { "policy": "any" } }
```
A `"server"` or `"client"` section overrides the top-level keys on that side (`config.cpp: apply_side`).

## Symbol database per side
- `sigdb/symbols/client.json` is the core set. It is the server's core set unchanged (all of its patterns match the
  client once, and the functions are the same), plus `WorldDestroy` (feature `world.lifecycle`) and `IsLocalPlayer`
  (`client.local_player`).
- `kiln/sigdb/client.d/` is **composed** at deploy (`tools/sigport/sides.py`):
  - `server.d/<name>.json` is used unchanged, unless `client.d` has a file of the same name.
  - `client.d/<name>.json` with `"extends": "server.d/<name>.json"` is an overlay. Its entries replace the base's by
    name, and `"remove": {"symbols": [], "constants": [], "features": []}` drops base entries.
  - `client.d/<name>.json` without `"extends"` is a standalone client definition (e.g. `net.json`, `ui.json`).
- Overlays today: `chat.json`, `time.json`, `saves.json` and `gameplay.json` (patterns that differ on the client). The
  only feature off on the client is `time.weather`, plus `content.loot`, which is off on the server too.
- Tools:
  - `tools/sigport/sigdb_check.py sigdb/symbols/client.json <exe>` resolves exactly what the core will see.
  - `tools/sigport/sidecompare.py <server exe> <client exe>` compares the two sides function by function.
  - `tools/sigport/build_record.py client <exe> [--verified --note ...]` writes the build record.

## Worlds, threads, ticks
| World (kind) | where | thread | Kiln API |
|---|---|---|---|
| server (0) | dedicated server; a client **while hosting** | the game's server thread ("GameServer" in a client, created at start-up) | `subscribe_tick` / `post` / `on_tick_thread` (unchanged), `Context.Tick`, `Scheduler`, every service |
| client (2) | a client in game (hosting or joined) | the game's main thread | `subscribe_world_tick(…, KILN_WORLD_CLIENT, …)`, `post_world`, `on_world_thread(2)`; managed `Client.Tick`, `Client.Post` |
| prediction (1) | a client in game | main thread | `subscribe_world_tick(…, 1, …)`; not a full world: only a storage (`world(1) + world.storage`) inside an object the client world owns, on the client world's singletons and entity tables; it stops with the client world. **It steps first in each frame, before the client world's Pre**: what it reads must be in place before that (docs/re/content.md section 9) |

- A world **starts** at its first step: a new world pointer of that kind, logged
  `tick: client world 0x… started (session N, thread T)`.
- It **stops** when the game destroys it (hook on `WorldDestroy`), logged `tick: server world 0x… stopping`. This
  happens when you leave to the menu, and at exit.
- In the **main menu** no world steps, so nothing of Kiln runs: no Pre/Post, no posted tasks, no managed mods' ticks.
- Hosting again gives a new session. The world pointer may even be the same address, since the allocator reuses it.
  Seen at runtime: session 2 had the same server, client and prediction addresses as session 1.
- `KilnTickInfo.clock_delta` and `wall_delta_ms` are 0 at a world's first step, and `clock_delta` is never negative
  (the prediction clock steps back when it re-simulates).
- Kiln skips SimStep calls that do not advance the world (`ticked == 0`). Client-world ticks therefore follow its
  fixed step, not the frame rate.
- EMF measured the hosted server world at 60 Hz (the dedicated server: 24.5 Hz). Use `ClockDelta`/`WallDelta` for
  time, never step counts.
- `tick-stats.jsonl` lines carry `"world": "server" | "client" | "prediction"` on a client. A dedicated server's lines
  keep their old shape.

### C ABI (`kiln_api.h`, appended to `KilnApi`; check `size`)
`side()`, `world(kind)`, `on_world_thread(kind)`, `subscribe_world_tick(owner, kind, phase, fn, user, out)`,
`post_world(owner, kind, fn, user)`, `subscribe_world(owner, KilnWorldEventFn, user, out)` (KilnWorldEvent:
`KILN_WORLD_STARTED` before the world's first Pre on its thread; `KILN_WORLD_STOPPING` on the destroying thread, world
still intact) and `session(KilnSessionInfo*)`.
- `session` gives the state, the per-kind worlds/sessions/steps and whether stops are seen. States:
  - `NONE`: booting, menu or loading.
  - `DEDICATED`: a dedicated server with its world running.
  - `HOST`: a client hosting; the server world is here.
  - `REMOTE`: a client joined elsewhere; there is no server world here.
- `KilnBuildInfo.side` is now real (0 server, 1 client).
- `unsubscribe` removes these handles as well.

### In-core service `kiln.client` v1 (`abi/services/kiln_client.h`)
- `state` (any thread): side, session, generation, `local_player`, per-kind sessions and steps.
- `subscribe_tick`, `unsubscribe` and `post` for the client world, plus `on_client_thread` and `client_world`.
- `local_player` is read at each client-world Pre from the client world's `LocalPlayerData` singleton (`+4`). On a host
  the same id works with server-world services; EMF confirmed that ids are shared.

### Managed: `Kiln.Client` (assembly `managed/services/Kiln.Client`)
```csharp
if (Client.IsClient && Client.IsHosting) Log.Info($"single player / host; me = {Client.LocalPlayer}");
Client.Tick += (in TickInfo t) => { /* main thread, client world step */ };
Client.Post(() => { /* main thread */ });            // from the server tick thread or anywhere
Context.Scheduler.Post(() => { /* server tick thread: call services here */ });
```
- `Client.State` is `Menu`, `DedicatedServer`, `Hosting` or `Remote`.
- `IsClient`, `IsHosting`, `IsRemote` and `InGame` are shortcuts. `LocalPlayer` and `HasLocalPlayer` give the local
  player.
- `ClientWorldSession`, `ServerWorldSession` and `ClientWorldSteps` count worlds and steps. `IsClientThread` tells
  whether the caller is on the main thread.
- `NextTick()`: `await Client.NextTick()` continues on the main thread. An `await` inside a `Client.Tick` handler also
  comes back to the main thread.
- `Client.Tick` handlers belong to the subscribing mod and end when it unloads. A handler that throws five times in a
  row is removed.

### Mods and sides
The core gives the side to the loader (`KilnBuildInfo.side` / `KilnApi.side`; Kiln.Host logs `side client`).
Manifests say `"side": "server" | "client" | "both"`.

| Process | `client` and `both` mods | `server` mods |
|---|---|---|
| dedicated server | `both`: at the first server-world step, for the whole run (`client`: skipped) | the same |
| client, main menu | load once in the menu (kiln.ui's UI frame; else at the first world step) and stay loaded until the game exits | skipped: `side 'server': loads while this client hosts a world` |
| client hosting a world | still loaded; the hosted server world drives `Context.Tick` (server thread) | **load at the hosted server world's first step, unload when that world ends** |
| client joined to another server | still loaded; the client world drives `Context.Tick` (main thread) | not loaded (no server world here) |

**Server mods on a hosting client.** Kiln.Host (`ModManager.StartHosting/StopHosting`, driven from
`HostRuntime`):
- Start: the hosted server world's first step, on its thread (GameServer), during the game's loading screen. The
  `server` mods resolve against the mods already running (they may depend on `both` mods), then the `client`/`both`
  mods that failed in the menu for want of one of them. Then `ServerStarting` and `ServerStarted` (`Source`
  `world-started`) reach every mod. kiln.log: `hosting: N of M server-side mod folder(s) loaded for the hosted world in
  X ms: ...`, `lifecycle: hosted server world started (step S); N server-side mod(s) loaded for it`.
- Stop: the game destroys the server world when the player leaves to the menu, joins another server or quits.
  `KILN_WORLD_STOPPING` arrives on the server thread with the world intact: `ServerStoppingEvent` (`Reason`
  `world-stopping`) to every mod, then each `server` mod unloads (dependents first, `OnUnload`, its owner revoked: its
  ticks, commands, event handlers, god-mode holds, hooks go). kiln.log: `hosting: the hosted world ended (...): N mod(s)
  unloaded in X ms: ...`. Mods that came with them go back to `failed` ("a server-side dependency unloaded with the
  hosted world; it loads again with the next one"); `client`/`both` mods stay loaded (`Lifecycle.Phase` = `Idle`).
- The next hosted world loads new instances: static state of a server mod does not survive between hosted worlds (keep
  it in `Context.DataDirectory` or world data). A server mod that failed or was unloaded by hand in one hosted world is
  tried again in the next.
- `load`/`reload` (control folder, `Mods.LoadAsync`/`ReloadAsync`) accept `server` mods while hosting; in the menu they
  answer the skip reason. `state/mods.json` and `Mods.All` show `skipped` in the menu, `loaded` while hosting.
- kiln.json `"mods": {"hosted": false}` keeps `server` mods off on a client (`side 'server': not loaded on a client
  (kiln.json mods.hosted is false)`). Both reasons are `HostedReasons` in managed/Kiln.Api/Mods.cs (Kiln.Host writes
  them, Kiln.Ui reads them; kiln-manage's `profile::GAME_HOSTED_*` copies are checked against that file).
- What players read (a player took Loot Leveler and Enemy Leveler for server-only when the launcher said "not loaded
  on a client", 2026-11-11): the launcher and the in-game Mods page never call a Kiln `server` mod skipped or
  server-only on a client (kiln.log and `state/mods.json` keep `skipped` and the reason above).
  - In-game Mods page (`ModCatalog`): state **Waiting**, "Starts with your own world, when you play alone or friends
    join you. It does not run in someone else's world."; the side chip says "Your world only" (other engines' `server`
    mods keep "Server only"). With `hosted` false: "Not loaded", "Off in this game: its kiln.json turns off mods that
    start with your world (mods.hosted is false)." and how to turn it back on.
  - Launcher, profile Mods page, Client: the mods that start with the world come after the load order, lit and
    unnumbered, "Starts with your world" (the long sentence above on hover); the aside counts them ("2 mods load on a
    client. 1 starts with your world."). Play: "3 of 3 mods load on this client. Loot Leveler starts with your world."
    `kiln_manage::profile::resolve_preview_for` predicts it: the menu resolution, then a second one with
    `Environment.hosting` (the Rust twin of `ResolveEnvironment.Hosting`; fixture `side-server-hosting.json`); what loads
    only in the second starts with the world (`ResolvePreview.withWorld`: server mods and the mods that need one), a
    server mod that fails there says "Will not start with your world: <reason>". `profile_resolve` takes the game's id
    to read its kiln.json `mods.hosted`.
  - Support report: a note `hosted-mods` ("... starts with the player's own world ..., so "skipped" in the main menu is
    expected"), a warning `hosted-off` for `hosted` false.
  - Not reworded: a `client`/`both` mod that needs a server mod is `failed` in the menu (`dependency ... did not load
    (skipped: side 'server': not loaded on a client)`, the resolver's text) and in-game shows Failed until the world
    starts and loads it; the launcher shows it as starting with the world.
- Without the WorldDestroy hook (`world.lifecycle` off) the stop is seen late: when the next hosted world starts (its
  first step unloads the old instances first), or when the client world steps with no live server world (joined
  elsewhere; `session()` counts a world that stopped stepping 3 s ago as gone). Then no `ServerStopping` is posted (the
  world is gone).

**Why load-when-hosting and not at boot.** What the code and the game allow decided it:
1. A server mod gets the contract it has on a dedicated server: `OnLoad` on the server tick thread at the server
   world's first step, with the world there (services answer), then `ServerStarting`/`ServerStarted`. At boot or in the
   menu no server world exists: world services answer `unavailable`/`wrong thread`, and a mod written for a dedicated
   server fails in `OnLoad` or faults on its first tick.
2. Joined to someone else's server there is no server world and nothing a server mod may touch; loaded at boot it would
   keep running there (the client world drives `Context.Tick`) against a world it does not own.
3. The game gives a clean stop: the WorldDestroy hook runs on the server thread before the world goes, so mods save and
   unregister with the world intact, exactly once per hosted world.
4. A client hosts several worlds in one process and entity ids repeat (the host's player is entity 1 in each);
   services reset their per-world state at `KILN_WORLD_STOPPING`. A new mod instance per hosted world gives server mods
   the same guarantee without extra work in every mod.
5. Mods live in collectible load contexts, so unloading is cheap and leak-checked (Kiln.Host.Tests `HostingTests`:
   two hosted worlds, 0 leaked contexts).
Cost: one `OnLoad` per hosted world, during its loading screen.

**`both` mods.** Loaded once, they see every hosted world through the lifecycle events: do server work between
`ServerStartedEvent` and `ServerStoppingEvent` (`Lifecycle.IsServerRunning`), client work in `Client.Tick` /
`Client.State`. `Mods.Side` is the process side (`Client` on a host), so do not use it to decide whether to do server
work. Sample: `mods/tests/hosted-both-test`.

**Native mods** (`native.json`) keep the process side: a `server` native mod is skipped on a client. A DLL cannot be
unloaded when the hosted world ends, so it could not follow hosted worlds; a native mod that should work on a hosting
client declares `"side": "both"` and checks `api->session()` (`KILN_SESSION_HOST`) or `subscribe_world`.

Loading client mods in a joined (remote) session works the same as in a hosted one (menu load); the client world
drives their ticks.

Test mods: `mods/tests/hosted-test` (`server`: lifecycle lines, `/hosted status|deadcheck|revive`) and
`mods/tests/hosted-both-test` (`both`: server part per hosted world, `/hostedboth`).

Runtime-verified (client 26430625, build verified; a LOCAL world hosted Offline twice in one process):
- Menu: `mod hosted-test 0.1.0 skipped: side 'server': loads while this client hosts a world`; `mods: 6 loaded, 0
  failed, 1 skipped`.
- Hosted world 1 and 2: `hosting: 1 of 1 server-side mod folder(s) loaded for the hosted world in 11.6 ms` / `5.7 ms`,
  hosted-test `OnLoad: side Client, client Hosting, server world session N, ... phase Booting, tick thread True`, then
  ServerStarting/ServerStarted (`world-started`), `the host player alive: GetHealth(1) = 293/293, game mark False`,
  `/hosted deadcheck` 8/8 in each world. hosted-both-test: `server part starts (session 2)` with the same instance.
- Back to the menu (each time): ServerStopping (`world-stopping`) to both mods on the server thread, hosted-test
  `OnUnload ... summary: 13 PASS, 0 FAIL` / `12 PASS, 0 FAIL`, `hosting: the hosted world ended (the game is closing it): 1 mod(s) unloaded
  in 10.5 ms` / `5.4 ms`, `6 mod(s) stay loaded`; in the menu `/hosted` answers `Unknown command`, `/hostedboth`
  answers (phase Idle). kiln.combat: `server world closed (the game is closing it)`, then `world singletons: ...` at
  the next world's first step. 0 `[E` lines; the game quit normally.
- Not run: a joined (remote) session (outward-facing).

### For service authors: what changes in a client
- Hooks on engine functions fire for **every** world: client and prediction worlds too. Check the world kind
  (`world + world.storage + storage.kind`) or compare with `api->world(KILN_WORLD_SERVER)` before treating a world or a
  singleton as the server's.
- A client process can run several server worlds one after another. Reset per-world state on
  `KILN_WORLD_STOPPING` (`subscribe_world`) or when `api->world(0)` changes. Never keep a world, singleton or
  GameServer pointer from a previous session.
- Kiln's own services do this (players, time, chat, settings, spawn, saves, items; world already did): see
  `docs/re/client.md`, "Services in a hosted world", and each service's "In a game client" section.
- kiln.combat: at the server world's `KILN_WORLD_STOPPING` it drops held hits and damage requests (their
  status becomes Dropped), deaths, kills, corpses, falls and god-mode holds, and forgets the world's `ImpactSystem` and
  `AttributeRegistry`; the next world binds its own at its first Pre (before its first hit conclusion). kiln.log:
  `server world closed (the game is closing it): N held hit(s) ... forgotten; waiting for the next server world`.
  Before, the old singletons stayed bound for up to 250 steps of the next hosted world.
- `Combat.GetHealth(...).Dead` reported a dead-then-healed host player as `413/413 dead` (session 7, `abt heal` after the
  player died from a fall). Cause: `Dead` was the Actor's raw dead bit, which only the game's `death` program rewrites,
  once per step (Health < 1: set; Health >= 1: cleared). Any health change outside it (Kiln's Heal/SetHealth, god mode)
  left it one step behind; not a wrong world or a stale offset (`actor.state` 0xBF8 is captured on both svn 1287369
  builds, and kiln.combat reads the server world). `Dead` is now the mark as that program will leave it, read through
  the game's own state test (`combat/ActorStateTest`, with the actor's override layer): see
  `docs/kiln/services/combat.md` and `docs/re/combat.md` "Death".

## Runtime verification
The steps below are the checklist for a new game build. Results: `docs/re/client.md`, "Runtime".

First run (core + services + probe, policy any, saves backed up first):
1. `client.sh backup-saves`, then `client.sh deploy --real --policy any --probe`, then start through Steam.
2. Main menu, about 1 min. `kiln.log` should show:
   - `side: client (pdb enshrouded.pdb)`, `sigdb: N/N symbols`, `build ce6fdba6…: UNVERIFIED (policy any) [client]`.
   - `SimStep hooked`, `WorldDestroy hooked`, every service loaded, `boot complete`.
   - No `world … started` while in the menu; `tick-stats.jsonl` stays empty. The game is responsive.
3. Host a world (single player). The log should show:
   - `server world … started (thread T1)`, then `client world`, `prediction world` (thread T2 = main thread).
   - Services' first-step lines and `client: local player entity N`.
   - Probe lines every 10 s: `state Hosting`, the server world at about 60 steps/s, the client-world rate, the entity
     count, the player list (the local player), the time of day. No faults.
   - After 60 s, `tick-stats.jsonl` lines per world.
4. Back to the menu: `client world … stopping`, `server world … stopping`, probe state `Menu`. In the menu no further
   ticks (the stats stop). Optional: host again, check `session 2` and that the services follow.
5. Quit: the game exits cleanly (enshrouded.log `Shutdown 'Steam'`, no crash dialog, process gone).
6. `client.sh undeploy --real`.
7. If all of this holds, re-run `build_record.py client … --verified --note "…"`, deploy the record, and test once more
   with `policy verified-only`.

Session 2: join the client-core lab server (`tools/lab/lab.sh start client-core`, UDP 25870) to check `REMOTE`: no
server world, the client world ticks, the local player id. This needs a way to reach the lab from the client: a LAN or
direct connect.
