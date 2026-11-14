# Kiln architecture

```
enshrouded_server.exe / enshrouded.exe
 └─ imports DINPUT8 ──► <game>/dinput8.dll              native/proxy  (forwards to system dinput8)
                          ├─ DllMain: play-once.ini, chain loading (kiln/chain.ini, dinput8-chain.ini)
                          └─ entry gate ──► kiln/kiln-core.dll   native/core
                                              ├─ profile  kiln/profile.json → kiln.profile (mods, configDir, dataDir)
                                              ├─ native   native mods (native.json): early / boot / world phases
                                              ├─ image    exe sha256 + CodeView identity
                                              ├─ sigdb    kiln/sigdb/server.json → symbols, constants, features
                                              ├─ policy   verified-only | any  (unverified build = safe idle)
                                              ├─ tick     SimStep detour: Pre / step / WaitWorldJobs / Post
                                              ├─ log      kiln/logs/kiln.log + game-log mirror (addLogListener)
                                              ├─ registry owners, subscriptions, posted tasks
                                              ├─ plugins  kiln/services, kiln/plugins, profile plugins (kiln_plugin_main)
                                              ├─ state    kiln/state/{boot,native}.json for the launcher
                                              └─ clr      kiln/dotnet + kiln/host/Kiln.Host.dll
                                                            └─ ModManager: the profile's kiln mods and/or kiln/mods/<id>/
                                                               in collectible contexts; state/mods.json
```

## Boot
1. The game loads `dinput8.dll` (Kiln's proxy) as a static import. Its `DllMain` reads and deletes
   `kiln/play-once.ini` (vanilla: stop here), loads the chained DLLs (`kiln/chain.ini [chain]`, EMF's
   `dinput8-chain.ini`, `kiln/chain.ini [profile]` = dllmain-phase native mods; [profiles.md](profiles.md)), then
   overwrites the exe's entry point with a 14-byte jump and returns. Results go to `kiln/logs/proxy.log` and, in
   `KilnBootInfo` (v3: also the length of shroudlink's client log before the chain), to the core.
2. The jump lands in the proxy's gate on the main thread, before any game code. The gate restores the entry bytes,
   loads `kiln/kiln-core.dll`, calls `kiln_boot` and then runs the entry point. An entry patch a chained DLL made is
   what the gate restored, so it runs next (last in, first out); a DLL loaded during `kiln_boot` (`[late]`, a native
   mod) may patch the entry point too, and its patch runs first. Any failure: the game runs vanilla.
   `KILN_DISABLE=1` skips Kiln (no chain either).
3. `kiln_boot`: paths and log, `kiln.json`, the profile (`kiln.profile`: play-once, `kiln/profile.json` or the legacy
   layout; sets `KILN_PATH_CONFIG`), image identity (13 ms to hash the exe), the build record, native mods `early`
   ([native-mods.md](native-mods.md)), sigdb (12 ms for all symbols), policy decision, hooks (SimStep, log listener),
   in-core services, service DLLs and plugins, native mods `boot` (and `world` armed), .NET (40 ms) and `Kiln.Host`
   (10 ms), then `kiln/state/boot.json`.
   **The boot phase** (since 0.2.0-dev.28; [modding/onboot.md](modding/onboot.md), [services/boot.md](services/boot.md))
   sits inside this step. kiln.boot decides once, before the service DLLs load, whether this process has a boot
   generation (hooks and .NET on, an enabled mod of this side with `"boot"`, `mods.boot` available; on a game client
   also `mods.boot.client`, held in this build). If it does, the seven collectors (assets, worldtables, content,
   skills, knowledge, vfx, patches) register in `kiln_plugin_main` or `on_ready` and keep their definitions back
   instead of loading them there; registration closes once every `on_ready` ran. Inside the .NET start, `Kiln.Host`
   runs each boot mod's `OnBoot` on a throwaway copy, lowers what it staged, and commits: kiln.boot prepares every
   collector in that order, leaves out a failed mod and the mods that require it, and publishes one generation. Then,
   before chain.ini `[late]` DLLs and before the game's entry point, the core checks it: anything but a published
   generation writes `progress.json` ("failed") and `boot.json` (section `boot`) and ends the process with exit code
   0x4B01; the game is never entered. Without a boot generation nothing of this runs and every collector loads in
   `on_ready` as before.
4. Managed mods load at the first server-world step, on the tick thread (on a client: client/both mods in the menu,
   side "server" mods with each hosted server world; docs/kiln/client.md "Mods and sides").

## Symbol database
`sigdb/symbols/server.json` lists each symbol's strategies (byte patterns, optionally scoped to the first N bytes of
another symbol). Patterns carry typed holes that capture layout constants while matching
(`48 8D BA {world.storage:d32} 44 89`) or call targets (`E9 {WaitTaskGroup:rel32}`). A constant needs `min_votes`
agreeing captures (`world.storage` is captured from four functions). Features name what they need; a missing piece
disables that feature only. The same file resolves on build 1273043 (`world.storage` 0xE15DC0) and on svn 1285601
(0xE179C0) without edits; `tools/sigport/sigdb_check.py` runs the resolver offline against any exe.

Policy: `verified-only` (default) installs hooks only when `kiln/sigdb/builds/server/<sha256>.json` says
`"verified": true`; otherwise Kiln logs a report (`kiln/logs/sigdb-report.json`) and stays idle. Labs use `any`.

## Tick model
The dedicated server steps its server world (kind 0) every ~40.9 ms (24.5 Hz); the game's 60 Hz clock advances 2-3
per step. Per step:
- **Pre**: posted tasks, then Pre subscribers. No job of the world is running.
- **step**: the original SimStep.
- **Post** (only while someone subscribed): `WaitWorldJobs(world)`, then Post subscribers. The server's SimStep
  returns with grid and range-query jobs still running; touching entities before the wait races them (EMF notes).

Kiln writes `kiln/logs/tick-stats.jsonl` every 60 s: step time, Kiln's own Pre/Post time, interval, subscribers.

## C ABI (`native/core/abi/kiln_api.h`, ABI 1)
One append-only table: log, build info, features, paths, owners, tick subscribe/post, game-log mirror, and an unstable
`symbol`/`constant` tier. Everything registered belongs to an owner; `owner_revoke` removes all of it. Callbacks into
consumers run under a structured-exception guard: a faulting subscriber is removed, the server keeps running.

## Managed host
- `Kiln.Api` (what mods reference) and `Kiln.Host` load into the default load context (falls back to hostfxr's
  isolated context if that fails). Each mod gets a collectible `AssemblyLoadContext`; assemblies load from bytes so
  the files can be replaced while the mod runs; `Kiln.Api` is shared by name.
- `Mod.OnLoad/OnUnload`, `Context.Tick.Pre/Post`, `Context.GameLog.Line` (delivered on the tick thread),
  `Context.Scheduler.Post` / `await NextTick()` (a per-mod SynchronizationContext brings `await` back to the tick
  thread; work queued during a drain waits for the next step).
- Faults: an exception in a mod's handler is logged; after 5 the mod is disabled and its registrations revoked.
- GC: workstation + concurrent, 512 MB hard limit; a pacer runs gen0 collections at the end of a step after 8 MB of
  allocation, where the frame has slack.
- Control folder `kiln/control/<name>.cmd` → `<name>.result.json`: `mods`, `load <folder>`, `unload <id>`,
  `reload <id>`, `reload-test <id> <n>` (one reload per step, then a leak check), `gc`.

## Layout on a server
See `tools/deploy-server.sh`. Kiln's own files live under `<server>/kiln/`; only `dinput8.dll` sits next to the exe.
A client adds the launcher's files: `kiln/profile.json`, `kiln/play-once.ini`, `kiln/chain.ini` + `kiln/chain/`, and
the state files in `kiln/state/` ([launcher/contracts.md](launcher/contracts.md) section 4).
