# Flame mods on Kiln (kiln-flame)

**Flame** is the mod runtime inside Creative Mode (Nexus mod 146). Creative Mode ships a small loader (`dinput8.dll`
on clients) that starts the mods in `<game>/mods/`. Kiln starts the same mods through `kiln/plugins/kiln-flame.dll`,
from Kiln profiles as well as from `<game>/mods/`, without Flame's loader.

Flame has no licence and no public source. `kiln-flame` is Kiln's own code, written from the contract between a Flame
loader and a mod (below); nothing of Flame's loader is used or shipped.

## The contract

Observed from the mod's files and the loader's import and export tables, never by running them:

- A mod is a folder with `mod.json` (`id`, `name`, `version`), `flame.json` (`{"dll": "<file>"}`) and the mod's own
  data (for Creative Mode `sigs/<exe timestamp>-<image size>.json`, `symbols/*.json`, `items.json`, its settings).
- The mod DLL exports one function, `FlameStartRuntime`. The loader calls it with **no arguments and ignores any
  result**, after `LoadLibraryExW(dll, NULL, LOAD_WITH_ALTERED_SEARCH_PATH)`. The mod imports nothing from the
  loader: its whole runtime is inside its DLL and finds its own folder from its module.
- The loader does this from a thread it creates in its `DllMain`, not under the loader lock.

## What kiln-flame does

1. Steps aside when Flame's loader runs in this process (see [README.md](README.md#when-the-other-loader-is-installed-too)).
2. Finds the mods: the profile's `mods.flame` entries, then every `<game>/mods/<dir>/` with a `flame.json`.
3. Checks each one without running it: `flame.json` names a DLL inside the mod folder, the DLL exists and its export
   table has `FlameStartRuntime`.
4. On one worker thread, one mod after the other: `LoadLibraryExW(dll, LOAD_WITH_ALTERED_SEARCH_PATH)`, then
   `FlameStartRuntime()` under a fault guard. A mod that faults is marked failed; the game runs on. A DLL is never
   unloaded.
5. Publishes the service `kiln.flame` (`native/plugins/flame/include/kiln_flame.h`): which Flame mods run, so Kiln's
   own features that touch the same game systems can go idle (Kiln's creative hooks while Creative Mode runs).
6. Writes `kiln/state/flame.json`; logs `[flame] ...` and `[flame:<id>] ...`.

## Coverage

| Flame contract | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `mod.json` `id`, `name`, `version` | read for ids, logs and the status file | `ModInfo` | full; host `flame-legacy` |
| `flame.json` `dll` | the DLL to start; must stay inside the mod folder | - | full; host (`..\outside.dll` refused) |
| `FlameStartRuntime()` (no arguments, result ignored) | called once per mod on kiln-flame's worker thread, fault-guarded | `Mod.OnLoad` | full; host `flame-legacy` (started, faulting mod contained), `flame-profile` (mod outside `<game>/mods` finds its own folder); lab: Creative Mode |
| loading with `LOAD_WITH_ALTERED_SEARCH_PATH` | same flag (the mod's own dependencies resolve from its folder) | - | full |
| start from a worker thread | same | - | full |
| mods in `<game>/mods/` | read (with the profile rule) | - | full |

1 of 1 entry point, 2 of 2 manifest files. There is no further API: the runtime is inside each mod.

## Creative Mode and Kiln's own creative features

Creative Mode hooks the same game systems as Kiln's creative rules (building cost, flight, time of day). The policy
([universal-loader.md](../universal-loader.md) 3.6): Kiln's own feature yields while the third-party mod runs. Kiln's
features find out through `kiln.flame` (`running("creative")`); Kiln's creative rules do not consult it yet.

## Manual test (real client)

1. Install Creative Mode's `mods/creative/` folder into `<game>/mods/` (leave its `dinput8.dll` out: Kiln is the
   game's `dinput8.dll`). Start the game through Kiln.
2. `kiln.log`: `[flame] kiln-flame 1.0.0 on the client: ...; 1 Flame mod(s)`, then
   `[flame:creative] Creative Mode 3.2.0 (folder): FlameStartRuntime returned after N ms`.
3. In a world, Creative Mode's menu key opens its menu, and its features work as with its own loader.
4. Variant: list it in a Kiln profile (`mods.flame`, the folder in the launcher library) instead of `<game>/mods`; the
   same lines appear with `(profile)`.

Mod files needed: Creative Mode 3.2.0 from Nexus (a copy was used for the lab run). Its server build ships a
`powrprof.dll` loader per a third-party guide; that file is not in the copy used here.
