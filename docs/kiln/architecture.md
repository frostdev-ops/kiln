# Kiln architecture

The client gate list is empty in 0.2.0-dev.29: every capability held in dev.28 is open, subject to its normal dependencies and side restrictions. The remaining 20 have not been confirmed in a real game; the [client pass](client-pass.md) records that confirmation work.


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
   `KilnBootInfo` (v3: also the length of shroudlink's client log before the chain), to the core. On a dedicated
   server the gate first applies a staged Kiln update, if there is one ([staged-updates.md](staged-updates.md)).
2. The jump lands in the proxy's gate on the main thread, before any game code. The gate restores the entry bytes,
   loads `kiln/kiln-core.dll`, calls `kiln_boot` and then runs the entry point. An entry patch a chained DLL made is
   what the gate restored, so it runs next (last in, first out); a DLL loaded during `kiln_boot` (`[late]`, a native
   mod) may patch the entry point too, and its patch runs first. Any failure: the game runs vanilla.
   `KILN_DISABLE=1` skips Kiln (no chain either).
3. `kiln_boot`: paths and log, staged mod updates (`kiln/staged`, [staged-updates.md](staged-updates.md)), `kiln.json`, the profile (`kiln.profile`: play-once, `kiln/profile.json` or the legacy
   layout; sets `KILN_PATH_CONFIG`), image identity (13 ms to hash the exe), the build record, native mods `early`
   ([native-mods.md](native-mods.md)), sigdb (12 ms for all symbols), policy decision, hooks (SimStep, log listener),
   in-core services, service DLLs and plugins, native mods `boot` (and `world` armed), .NET (40 ms) and `Kiln.Host`
   (10 ms), then `kiln/state/boot.json`.
   **The boot phase** (since 0.2.0-dev.28; [modding/onboot.md](modding/onboot.md), [services/boot.md](services/boot.md))
   sits inside this step. kiln.boot decides once, before the service DLLs load, whether this process has a boot
   generation (hooks and .NET on, an enabled mod of this side with `"boot"`, `mods.boot` available; on a game client
   also `mods.boot.client`). If it does, the seven collectors (assets, worldtables, content,
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

Kiln writes `kiln/logs/tick-stats.jsonl` every 60 s: step time, Kiln's own Pre/Post time, interval, subscribers. Since
0.2.0-dev.29 each line also carries `post_parts` (the game's job wait, the window services' end and each Post
subscriber by owner), `pre_parts` (the eight slowest Pre subscribers) and `worst` (the window's slowest step taken
apart). Since 0.2.0-dev.29.19 a server-world gap of a second or more that the world's clock did not cover (a
single-player world stands still while the game's menu is open) goes into `paused_ms` (count, total, max), written only
when there was one, and not into `interval_ms`: a stall shows as a long interval with `clock_rate` staying at 60 (the
clock catches up), a pause as `paused_ms`. Since 0.2.0-dev.29.19 each server and client world line ends with `impact` (the game's impact memory pool: `used_pct`, `peak_pct`, `capacity_mib`, `live`, `live_peak`; [services/abilities.md](services/abilities.md#impact-memory-watch)). A support report keeps the last 1 MiB of the file. `kilnctl tick-compare` compares two such files
(before/after a change; `kiln_manage::tick_compare`, the method in [kilnctl.md](kilnctl.md#measuring-a-task-change-tick-compare-t12)).

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
  `reload <id>`, `reload-test <id> <n>` (one reload per step, then a leak check), `gc`. A `reload` (or an `unload` and a
  `load`) takes the mod's data layers off and applies its current files again: data packs, Kiln.Quests' `quests/*.json` and
  Kiln.Blocks' `blocks/*.json` follow the mod's load, not only its id (0.2.0-dev.31.13; services/content.md "Reloads and edits").
  What a reload covers, key by key, and what waits for a restart: [below](#what-reload-id-covers).

### What `reload <id>` covers
A reload unloads the mod and its dependents (dependents first), reads its `kiln.mod.json` and files again and loads them
in a new load context, after the mods that stay loaded (documented, and its refusals enforced, since 0.2.0-dev.31.17).

| Part of the mod | `reload <id>` |
|---|---|
| `version`, `name`, `description`, `authors` (`author`), `license`, `url`, `updates`, `kilnBuild`, `advanced` (the new load's `ModInfo.Advanced`), `systems` (a declaration for tools) | read again |
| `entryAssembly`, `entryType`, the mod's DLLs, `exports` | the new code loads; dependents reload with it |
| `apiVersion`, `dependencies`, `incompatible`, `features` | checked again. A refusal leaves the mod (and its dependents) unloaded, and the reply says why; a later `load` or `reload` retries |
| `loadAfter`, `loadBefore` | the reloaded mods load after the ones that stay, and keep that place in the load order until the next start (their data packs and registry sections keep the start's place); a changed order takes effect then |
| `side` | refused, `RestartRequired: mod <id>'s new version changes "side" in kiln.mod.json (server -> both): ...` (0.2.0-dev.31.18), before anything unloads, so the running version stays (a dependent whose new version changes its side refuses the reload the same way). A mod's side holds until the next start: the join checks players against the start's side-`both` mod lists and registry values (`bootSync`, network.md "Registry boot values and order"), which leave out the fields a one-side mod writes, so a side changed in place made every Kiln client's join differ until a restart. An `unload` and a `load` with another side is refused the same way (the mod stays unloaded). The side compared is the one the mod had in the start's load order (on a client that may host a world, its `server` mods' too: the registry stage ordered them with that side, though they load only while it hosts, so a new version with another side does not load with the next hosted world either), or for a mod the start did not order, its running version's |
| `clients` | what joining players need follows at the next second (since 0.2.0-dev.31.17; before, up to a minute); players already in the world are not checked again |
| `steamMessages`, `tests` | apply (the new load's owner gets the grant; `autoRun` runs after the load) |
| `writes` | a new entry needs a restart: kiln.world reads the lists when the server starts, and the reloaded mod's tracked writes of a type it adds are refused (`NotDeclared`) until then |
| `boot` | refused, `RestartRequiredBoot: ...`: a mod with boot content in this process, and a mod (or one of its dependents) whose new version adds `boot`; both before anything unloads, so the running versions stay as they were. Boot content (`OnBoot`, `resourcePatches`, `baseRules`, `gearRules`) never changes without a restart |
| data packs (`content/*.json`) | apply again, whole, from the new files or the previous version (services/content.md "When a reload fails partway"), in the place the mod had in the data-pack order at the start (0.2.0-dev.31.18; before, in its new place after the mods that stayed, so a field another mod's pack sets too could change winners until the next start): a field two mods' packs set goes to the same mod as at the start. An `unload` and a `load` put the mod back in that place too; a mod first loaded after the start comes after the others. A pack that throws leaves nothing of the mod's new version behind |
| a data pack's `registry` section | composed again with the other mods', in the start's load order; an edit the start fixed waits for a restart (a restart field, a field the start never read, a section or entry operation added after the start): registry.json says `RestartRequired: ...` ([modding/registries](modding/registries/README.md#what-a-reload-changes)) |
| `blocks/*.json`, `quests/*.json` | apply again (Kiln.Blocks, Kiln.Quests) |
| registry entry points (`[RegistryEntryPoints]`) | `HotReload` runs; `ModLoad` runs again when there is no `HotReload`; `WorldLoad` runs for the world that exists; `GameStart` does not run again: its edits stay as the start composed them, and when the reloaded code differs the reply says `RestartRequired: <id>'s OnRegistry runs at GameStart only ...` |
| native mods (`native.json`, `kiln/native/<id>/`) | refused, `RestartRequired: <id> is a native mod ...`: kiln-core loads their DLLs at start and never unloads them |

**The reply says what waits for a restart** (since 0.2.0-dev.31.17). `reload <id>`'s result waits for the reload's
registry work (the mod's runtime pass, its data packs' application at kiln.content's next Pre; a few steps, at most 5 s),
then adds `; registry: <n> edit(s) need(s) a restart: <mod> <place>, ...` to the message, and its `data` gets
`registry`: `{known, settled, restartRequired, edits: [{mod, registry, id, field, reason, source}]}` for the reloaded
mods (`settled: false`: the world was not ready, so their edits were not all applied yet). This is the hook for tools
such as the Web Admin mod: read `data.registry` of the reload's result, and `counts.restartRequired` plus the `refused`
rows whose reason starts with `RestartRequired: ` in `kiln/state/registry.json` for everything that waits (also what a
live edit left). `kilnctl status` prints the same as one line, and the launcher's Conflicts view lists them first.

**The reply says when a data pack threw** (0.2.0-dev.31.18). When a data pack of a reloaded mod throws while the
application after the reload applies it, kiln.content sets the mod apart (services/content.md "When a reload fails
partway"): the reply adds `; data packs: <mod>'s <mod folder>/content/<file> threw while it applied (<what>) and was set
apart: what it did came off and the packs it had before apply again` (or `... and none of its packs apply`) before the
registry part, and `data` gets `packs`: `{known, setApart: [{mod, file, reason, previous}]}` (`previous`: its packs from
before apply again, whole; `known: false`: no report of an application after the reload was read, for example without kiln.content, off its world's thread, or when no application followed the reload yet: before a world loads, or past the reply's 5 s). The reload itself
still succeeded (`ok` true): the mod is loaded; what applies of its packs is what the reply says. Both parts are
additive: the message starts as before, and `data.affected` and `data.registry` are unchanged. The same rows are the
`setApart` list of kiln.content's pack report (`Content.PackReport()`'s source, `kiln_content.h` `report`).

## Layout on a server
See `tools/deploy-server.sh`. Kiln's own files live under `<server>/kiln/`; only `dinput8.dll` sits next to the exe.
A client adds the launcher's files: `kiln/profile.json`, `kiln/play-once.ini`, `kiln/chain.ini` + `kiln/chain/`, and
the state files in `kiln/state/` ([launcher/contracts.md](launcher/contracts.md) section 4).
