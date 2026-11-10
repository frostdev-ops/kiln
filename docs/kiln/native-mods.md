# Native mods (DLL mods)

A native mod is one or more DLLs that Kiln loads with `LoadLibrary`. It may know nothing about Kiln (a plain DLL mod
whose `DllMain` does the work) or be Kiln-aware (an `init` export that gets the Kiln API). Each mod folder has a
`native.json`. Contract: [launcher/contracts.md](launcher/contracts.md) 5.4 and 5.9, `schema/native.schema.json`,
`native/core/abi/kiln_native.h`. Profiles and chain.ini: [profiles.md](profiles.md).

## Where they come from
- The profile's `mods.native` entries (folders, usually in the launcher library), in profile mode.
- The drop-in folder `<game>/kiln/native/<folder>/` (a folder with `native.json`): with no profile, or when the
  profile's `dropins` includes `native`. A drop-in whose id the profile lists too is skipped. A `.disabled` file in a
  drop-in folder switches it off. Servers use `kiln/native/<id>/` (the launcher syncs it there).
- Not under play-once `no-mods`, not with an invalid profile.

## native.json
```jsonc
{
  // comments and trailing commas are allowed
  "format": 1,
  "id": "creative-mode",                 // required, [a-z0-9][a-z0-9._-]{0,63}
  "version": "1.3.0",                    // required, semver
  "name": "Creative Mode",
  "side": "client",                      // client (default) | server | both
  "dlls": ["creative.dll", "lib/creative-ui.dll"],   // required: relative paths, loaded in this order
  "phase": "early",                      // early (default) | boot | world | dllmain
  "init": "kiln_native_init",            // optional export of dlls[0]; null = a plain DLL mod
  "requires": ["fps-unlock"],            // must be present and load first, else this mod fails
  "after": [], "before": [],             // ordering only, when present
  "incompatible": ["creative-legacy"],   // or {"id": "<range>"}
  "enabled": true,                       // default switch; the player's state and the profile override it
  "kiln": ">=0.2.0-dev",                 // Kiln versions it works with
  "builds": []                           // exe sha256s it was made for; others load with the note "untested build"
}
```
Unknown keys are kept and logged as a warning.

## Phases
| Phase | When | Hooks | Notes |
|---|---|---|---|
| `dllmain` | in the proxy's `DllMain`, before Kiln's gate and before any game code | no Kiln yet | listed in `kiln/chain.ini [profile]` (the launcher writes it); `init` not allowed; inside the loader lock: start a thread, never wait. A drop-in dllmain mod loads only when chain.ini lists it |
| `early` (default) | first in `kiln_boot`, after the log, kiln.json, the profile and the build check, before anything is hooked | `hooks_enabled` 0 | runs in safe idle too |
| `boot` | after Kiln's service DLLs and plugins (`kiln_plugin_main` + `on_ready`) | as Kiln decided | runs in safe idle too (then services answer NULL) |
| `world` | at the first world start: the server world on a dedicated server, the client world in a game client | on | on that world's thread (server: GameServer/tick thread; client: main thread); skipped in safe idle |

Within a phase the order follows the managed resolver's rules: `requires` (a dependency), `after`, `before`, ties by
id; a cycle fails every mod in it ("load-order cycle: a -> b -> a"), its dependents fail next. A mod may require one
of an earlier phase, not a later one. A dllmain mod whose DLL `kiln/chain.ini [late]` defers loads after everything
else in `kiln_boot`, so an `early` or `boot` mod cannot require it: that mod fails at startup with the reason
(`requires <id>, whose DLL kiln/chain.ini [late] loads after the plugins ...`). A `world` mod can. Each DLL loads with `LoadLibraryExW(LOAD_WITH_ALTERED_SEARCH_PATH)` under an
SEH guard (a fault marks the mod failed, a crash report is written, the game goes on). **Kiln never calls
FreeLibrary** on a native mod.

## Switches and policy
- Enabled precedence: a profile entry with `enabled: false` wins, then `<configDir>/native.json`
  (`{"format": 1, "mods": {"<id>": {"enabled": false}}}`, written by the launcher), then native.json `enabled`.
- `kiln.json`: `"native": {"policy": "always" | "verified-only" | "never"}` (also in the `server`/`client`
  sections). `always` (default) loads native mods on any build, in safe idle too; `verified-only` only when the
  game build's record says verified; `never` loads none. Refused mods get state `policy`. Dllmain mods are loaded by
  the proxy before kiln.json is read: they keep loading and carry a note instead. A dllmain DLL that
  `chain.ini [late]` defers has not loaded yet, so the policy, the side and the switches above apply to it: a disabled,
  other-side or refused mod's late DLL is not loaded (`state/native.json` `chain.late`: status `skipped`, with the reason).
- A mod switched off after the proxy loaded its DLL (a compat rule, `<configDir>/native.json`, a profile entry) still
  runs: the loader shows it as `loaded` with a note, and a compat rule for it is reported as not applied. Activate the
  profile again in the launcher so `chain.ini` leaves the DLL out.
- Other checks: the side, `kiln` (a dev build counts as its release), missing DLLs, `requires`, `incompatible`.

## A Kiln-aware native mod
```c
#include "kiln_native.h"

__declspec(dllexport) int KILN_CALL kiln_native_init(const KilnNativeContext* ctx) {
    if (ctx->size < sizeof(KilnNativeContext)) return KILN_NATIVE_INIT_UNSUPPORTED;  /* older core */
    ctx->api->log(ctx->owner, KILN_LOG_INFO, (KilnStr){"hello", 5});
    if (ctx->hooks_enabled) { /* services: ctx->api->service("kiln.players", 1), subscribe_tick, ... */ }
    return KILN_NATIVE_INIT_OK;  /* or _FAILED (state failed), _UNSUPPORTED (state unsupported, not an error) */
}
```
`init` runs once, right after `dlls[0]` loads (the other DLLs load after it), on the thread of its phase. The context
(and its strings) lives for the process:
- `api`: the same `KilnApi` as plugins get. `owner`: owner `native:<id>` of everything you register; it is revoked
  when init fails, faults or returns UNSUPPORTED.
- `module`: `dlls[0]`'s HMODULE. `id`, `version`, `directory` (the mod folder).
- `config_dir`: `<configDir>/native/<id>` and `data_dir`: `<dataDir>/<id>`, both created before init.
- `phase`, `side`, `hooks_enabled`, `play_once`.
- `api->service(KilnStr{"kiln.profile", 12}, 1)` answers in every phase, safe idle included.
Sample: `native/samples/native-mod/` (phase boot). Template for tests: `native/samples/native-test/`.

## Status: `<game>/kiln/state/native.json`
Rewritten after the discovery and after each phase:
```json
{ "format": 1, "pid": 412, "written": "…", "policy": "always", "phase": "boot", "hooks": true,
  "mods": [ { "id": "creative-mode", "version": "1.3.0", "folder": "Z:\\…", "source": "profile", "phase": "early",
              "state": "loaded", "reason": null, "notes": [], "loadIndex": 0,
              "dlls": [ { "file": "creative.dll", "status": "loaded", "error": 0, "ms": 3.1 } ],
              "init": { "export": "kiln_native_init", "result": 0, "fault": null, "ms": 0.4 } } ] }
```
States: `pending` (its phase has not run yet), `loaded`, `failed` (reason), `disabled`, `skipped` (other side, phase
world in safe idle, a dllmain mod not in chain.ini, a drop-in the profile overrides), `policy`, `unsupported`.
`kiln.log` has one line per mod (`native mod <id> <version> loaded (boot, 1 DLL(s), kiln_native_init ok, 1.2 ms)`), and
boot.json a summary (`native: {loaded, failed, pending}`).

## Testing
- `mods/tests/native-test/stage.py`: 18 cases (each phase, ordering, requires, a cycle, a failing / unsupported /
  faulting init, side, switches, a missing DLL, an old `kiln` range, an untested build) as drop-ins or as a profile
  with Z:\ paths. Each Kiln-aware case logs `native-test <id>: <check> PASS|FAIL`.
