# Writing a Kiln service

A service is one capability (teleport, items, chat, ...) with three layers. The template is `example`:

| Layer | Where | Template |
|---|---|---|
| Symbols | `sigdb/symbols/server.d/<svc>.json` (merged with `server.json` at boot) | `sigdb/symbols/server.json` |
| C table | `native/core/abi/services/kiln_<svc>.h` | `kiln_example.h` |
| Native | `native/services/<svc>/` → `kiln/services/kiln-<svc>.dll` | `native/services/example/` |
| Managed | `managed/services/Kiln.<Svc>/` → `kiln/host/Kiln.<Svc>.dll`, shared by all mods | `managed/services/Kiln.Example/` |
| Test mod | `mods/tests/<svc>-test/` (prints `PASS`/`FAIL` lines) | `mods/samples/Hello/` |

## Native rules
- `kiln_plugin_main(api, owner, info)`: check `api->abi_version == KILN_ABI_VERSION`, resolve symbols with
  `api->symbol` / constants with `api->constant` / features with `api->feature`, install hooks (safetyhook is linked
  into every service: `#include "safetyhook.hpp"`), subscribe ticks, then `api->register_service(owner, name, &table)`.
  Missing pieces: return `KILN_ERR_UNAVAILABLE` (or publish a table whose functions return it). Never crash.
- The server world and its singletons: `api->server_world()` (0 before the first server-world step) and
  `api->singleton(world, "ServerPlayers")` (exact registered name; tick thread). Use these instead of hooking
  RegisterSingleton yourself (core feature `singletons`, layout from docs/re/players.md).
- Other services: look them up in `info->on_ready` (`api->service("kiln.world", 1)`), not in `kiln_plugin_main`.
- Threads: game state only on the tick thread, in Pre (no world jobs running) or Post (after `WaitWorldJobs`), or
  inside an ECS program update with declared access. Calls from managed mods arrive on the tick thread.
- Tables only grow at the end; bump the version for additions. Strings are UTF-8 `KilnStr`. Entities are
  `KilnEntity` (u32). Positions `KilnVec3` in metres (engine: 32.32 fixed, `round(m * 2^32)`).
- Names you define in sigdb are prefixed with your service: symbols `items/InventoryCommit`, constants
  `items/tx.size`. Unprefixed names belong to the core file (`SimStep`, `world.storage`, `GetEntityTransform`, ...)
  and may be used, never redefined.

## Managed rules
- Public API as static classes in namespace `Kiln` (e.g. `Kiln.Teleport`), no pointers in public signatures.
- `Services.Get<T>(name, version)` gives the native table (null = unavailable: throw `NotSupportedException` or
  return a failed result; expose `Available`). `Services.RequireTickThread(api)` for game-state calls.
- Async work: return `Task`/`Task<T>` completed on the tick thread. Anything that holds a mod's delegates or
  continuations must be dropped when that mod unloads: get the caller with `ModContext.CurrentMod` and subscribe to its
  `Retiring` event.
- Mods reference service assemblies with `Private="false" ExcludeAssets="runtime"`.

## Build, deploy, test
```sh
export KILN_OUT=<build dir>          # your own build dir
native/build.sh                                  # all native targets incl. your service
tools/lab/lab.sh new <lab> <port> [--save <savegame dir>]   # once
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any --samples  # builds host, services, mods in $KILN_OUT
tools/lab/lab.sh start <lab>; less ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log; tools/lab/lab.sh stop <lab>
```
Test mods: `dotnet build mods/tests/<svc>-test -c Release -o $KILN_OUT/mods/<id> --artifacts-path $KILN_OUT/dotnet`;
the deploy copies every mod folder under `$KILN_OUT/mods/`. Offline symbol check:
`python3 tools/sigport/sigdb_check.py sigdb/symbols/server.json <exe>` (merge your `server.d` file into a temp copy, or
extend the tool to read `server.d`).
