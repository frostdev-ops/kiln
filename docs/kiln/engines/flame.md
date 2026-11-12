# Flame mods on Kiln (kiln-flame)

**Flame** is the mod runtime inside Creative Mode (Nexus mod 146). Creative Mode ships a small loader (`dinput8.dll`
on clients) that starts the mods in `<game>/mods/`. Kiln starts the same mods through `kiln/plugins/kiln-flame.dll`,
from Kiln profiles as well as from `<game>/mods/`, without Flame's loader. A profile's Flame mods run from Kiln's copy in
`<game>/mods/<id>/`, the folder a Flame mod reads its own files from (below).

Flame has no licence and no public source. `kiln-flame` is Kiln's own code, written from the contract between a Flame
loader and a mod (below); nothing of Flame's loader is used or shipped.

## The contract

Observed from the mod's files and the loader's import and export tables, never by running them:

- A mod is a folder with `mod.json` (`id`, `name`, `version`), `flame.json` (`{"dll": "<file>"}`) and the mod's own
  data (for Creative Mode `sigs/<exe timestamp>-<image size>.json`, `symbols/*.json`, `items.json`, its settings).
- The mod DLL exports one function, `FlameStartRuntime`. The loader calls it with **no arguments and ignores any
  result**, after `LoadLibraryExW(dll, NULL, LOAD_WITH_ALTERED_SEARCH_PATH)`. The mod imports nothing from the
  loader: its whole runtime is inside its DLL.
- **The runtime takes `<game>\mods\<its id>\` for the mod's folder, not the folder of its module.** Seen in a real
  client (2026-11-11): Creative Mode 3.2.0, loaded by kiln-flame from the launcher's Library
  (`...\kiln\library\flame\creative\3.2.0+b415b5cc\creative.dll`), logged `Mod directory:
  S:\steamapps\common\Enshrouded\mods\creative`, `No symbol file for this game build
  (S:\...\mods\creative\sigs\6AEB144C-2A3F000.json)` and `No recipes at S:\...\mods\creative\symbols to resolve
  this build from: game features stay disabled`, showed its "Enshrouded was updated" dialog and switched itself off,
  although that file is in the Library copy. It writes `logs\` and `notice.txt` (and its settings) into that folder
  too (under Kiln that folder held only those). Which id it takes is not visible from outside; for Creative Mode the `mod.json`
  id and the DLL's name are both `creative`, and kiln-flame takes the `mod.json` id.
- The build key is the game exe's `TimeDateStamp` and `SizeOfImage` (`%08X-%X`; the mod's log: `build key
  6AEB144C-2A3F000` for 1.0.1.2). With no `sigs\<build key>.json` it looks for recipes in `symbols\` to resolve the
  build from (its log: "No recipes at ...\symbols to resolve this build from"); with neither its game features stay off.
- The loader does this from a thread it creates in its `DllMain`, not under the loader lock.

## What kiln-flame does

1. Steps aside when Flame's loader runs in this process (see [README.md](README.md#when-the-other-loader-is-installed-too)).
   The launcher copies the profile's Flame mods into `mods/<id>/` while Flame's loader is installed, so it runs them;
   `flame.json` lists them as `own-loader` or `not-loaded`.
2. Finds the mods: the profile's `mods.flame` entries, then every `<game>/mods/<dir>/` with a `flame.json` (drop-ins;
   Kiln's copy of a profile entry, marked `.kiln-library.json`, is that entry, not a second mod). For each one it
   checks, by file only, where the mod will read its own files (`<game>\mods\<mod.json id>\`) and what that folder
   holds for the running game build (`sigs\<build key>.json`, else recipes in `symbols\`, else nothing).
3. Checks each one without running it: `flame.json` names a regular DLL inside the mod folder, with no symlink or junction in its path, and its export
   table has a callable `FlameStartRuntime`. Handles keep the file unchanged through loading.
4. On one worker thread, one mod after the other: `LoadLibraryExW(dll, LOAD_WITH_ALTERED_SEARCH_PATH)`, then
   `FlameStartRuntime()` under a fault guard (DLL initialization is guarded too). A mod that faults is marked failed; the game runs on. Entered DLLs and the compatibility runtime remain mapped for the process lifetime; unload stops queued starts.
   A foreign entry point already running is allowed to finish.
5. Publishes the service `kiln.flame` (`native/plugins/flame/include/kiln_flame.h`): which Flame mods run, and (from
   kiln-flame 1.1.0, `symbols()`) whether the folder each one reads holds symbols for this game build, so Kiln's own
   features that touch the same game systems can go idle while the mod can do its job (Kiln's creative mode while
   Creative Mode runs, below).
6. Writes `kiln/state/flame.json`: per mod `folder` and `dll` (where it ran from), `readsFrom` (where it reads its own
   files), `buildKey`, `symbols` (`found`, `recipes`, `none`, or empty when unknown) and `note` (empty when the mod ran
   from the folder it reads, or that folder is Kiln's copy of it; else where it ran from and that it may not find its
   files there; for Kiln's copy of a mod whose `mod.json` id is not its Library id, that it cannot tell, below).
   Logs `[flame] ...` and `[flame:<id>] ...`: a `note` and `symbols: none` as warnings.

## Where a profile's Flame mod runs from

Activation (`engine_mods::place`, as for Greeneye mods in `greeneye/<id>/`) copies each enabled Flame mod of the
profile into `<game>/mods/<library id>/`, with or without Flame's own loader, with a `.kiln-library.json` marker and a
receipt in `kiln/state/library-placements.json`; the profile names that copy (copy mode: `../mods/<id>`, for a runtime
of `profileReader` 3 or more; an older one gets its `kiln/profiles/` copy, and the mod still finds its files in
`mods/<id>`). What the mod writes there (logs, notice, settings) is the player's: it stays across updates and goes to
`kiln/kept-mod-files/flame/<id>/` when the copy goes (switched off, removed, No profile, uninstall), from where the next
placement puts it back. A copy the player changed stays and is reported. A player's own folder of that name with a mod
in it (a hand install, Flame's installer) stays theirs and runs as a drop-in, as before; the profile then names the
Library entry. A folder there that holds only files a mod wrote (no manifest, no code: Creative Mode's `logs\` and
`notice.txt` from a start under an older Kiln) takes Kiln's copy and keeps those files in it. An id that is no Windows
folder name is not placed (the activation says so) and runs from the Library.

When the profile names another folder (written by an older launcher, the player's own folder kept, or a failed
placement), kiln-flame runs that one and says so: `[flame:creative] ran from <folder>, but a Flame mod reads its own
files from <game>\mods\creative, which does not exist: it may not find its files there ...`. Activating the profile
again in the launcher places the copy.

**The folder is the Library id, not the `mod.json` id.** The launcher places Kiln's copy in `mods\<library id>`. For
a `mod.json` id that is a Kiln id (Creative Mode: `creative`) the two are the same, or differ in case only, which
Windows folders ignore. Another `mod.json` id (`"Creative Mode"`, with spaces or capitals) gets its slug as the Library
id (`creative-mode`, with a hash suffix on a collision), and its copy lands in `mods\creative-mode`. If its runtime
takes `mods\<mod.json id>` for its folder, as kiln-flame assumes, such a mod does not find its `sigs\`, `symbols\`
or settings there, and by its own rule its game features stay off; if it takes the folder of its DLL's name, or its
module's, it does. Which one a Flame runtime takes is not visible from outside. kiln-flame then says what it looked
at and that it cannot tell: `ran from Kiln's copy <game>\mods\creative-mode (its Library id creative-mode), but its
mod.json names the id "Creative Mode": kiln-flame looked for the mod's own files ... in <game>\mods\Creative Mode,
which does not exist, and cannot tell ...`, with `symbols` unknown (empty), never `none`. Such a mod's own log names
the folder it looked in. Host scenario `flame-manifest-id-differs`.

## Coverage

| Flame contract | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `mod.json` `id`, `name`, `version` | read for ids, logs and the status file | `ModInfo` | full; host `flame-legacy` |
| `flame.json` `dll` | the DLL to start; must stay inside the mod folder | - | full; host (`..\outside.dll` refused) |
| `FlameStartRuntime()` (no arguments, result ignored) | called once per mod on kiln-flame's worker thread, fault-guarded | `Mod.OnLoad` | full; host `flame-legacy` (started, faulting mod contained), `flame-profile` (a profile entry in a folder of its own); lab: Creative Mode |
| the mod's folder is `<game>\mods\<id>\` | the launcher places profile mods there and the profile names the copy; kiln-flame reports `readsFrom`, `symbols` and a `note` | - | full; host `flame-reads-from` (a test mod that looks for `mods\<id>\sigs\<build key>.json` as Creative Mode does: found in Kiln's copy, missing from the Library); kiln-manage `flame_creative_mode` |
| loading with `LOAD_WITH_ALTERED_SEARCH_PATH` | same flag (the mod's own dependencies resolve from its folder) | - | full |
| start from a worker thread | same | - | full |
| mods in `<game>/mods/` | read (with the profile rule) | - | full |

1 of 1 entry point, 2 of 2 manifest files. There is no further API: the runtime is inside each mod.

## Creative Mode and Kiln's own creative features

Creative Mode hooks the same game systems as Kiln's creative rules (building cost, flight, time of day). The policy
([universal-loader.md](../universal-loader.md) 3.6): Kiln's own feature yields while the third-party mod runs. Kiln's
features find out through `kiln.flame` (`running("creative")`). This reports true after a faulted start too, because
the failed entry may already have installed hooks or launched threads.

A Creative Mode that cannot do its job does not count. kiln.rules (`native/services/rules/creative.cpp`) and Kiln.Rules
(`CreativeFlame.cs`) also ask `symbols("creative")`:

- `none` (no `sigs\<build key>.json` and no recipes in the folder it reads): by its own rule the mod switches its game
  features off. This is concluded from the files, once, before the mod runs; Kiln does not observe what the mod then
  does, and the rule itself rests on one log of Creative Mode 3.2.0. Kiln's creative mode stays available: its hooks
  install, `/creative on` works, its menu is there. kiln.log, `/creative status` and the `/creative on` reply say why
  (`Creative Mode (the Flame mod "creative") is in the profile, but it has no symbol file for this game build (...) and
  no recipes: by its own rule it switches its game features off, so Kiln's creative mode is available ...`).
- `recipes` (no symbol file for the build, recipes there): the mod may resolve the build and hook the same functions,
  so Kiln's creative mode stays idle; every refusal (`/creative`, the menu) adds that it has no symbol file for this
  build and, if its menu does not open, to switch it off in the Kiln profile to use Kiln's `/creative`.
- `found`, or an older kiln-flame without `symbols()`: idle, as before.

The answer is taken once, before the mod starts; the mod's log text is never read.

Flame's loader is checked before startup and before each queued dispatch. It cannot be stopped if it arrives after a
runtime has already entered; see the [load-order limits](README.md#when-the-other-loader-is-installed-too).
A fault on a mod-owned thread or inside a direct detour is outside these synchronous fault guards.

## Manual test (real client)

1. Add Creative Mode to a Kiln profile in the launcher and activate it. `<game>/mods/creative/` then holds Kiln's copy
   (`flame.json`, `creative.dll`, `sigs/`, `symbols/`, `.kiln-library.json`), and `kiln/profile.json` `mods.flame`
   names that folder. Start the game through Kiln.
2. `kiln.log`: `[flame] kiln-flame 1.1.0 on the client: ...; 1 Flame mod(s)`, `[flame:creative] reads its own files
   from <game>\mods\creative (sigs\<build key>.json is there for this game build)`, then
   `[flame:creative] Creative Mode 3.2.0 (profile): FlameStartRuntime returned after N ms`; `kiln/state/flame.json`:
   `folder` and `readsFrom` both the game's `mods\creative` (under Proton maybe through two drive letters, `Z:\...`
   and the Steam library's), `symbols` `found`, `note` empty. Creative Mode's own `mods\creative\logs\creative.log`
   names a symbol file it found, not "No symbol file for this game build".
3. In a world, Creative Mode's menu key opens its menu, and its features work as with its own loader; `/creative`
   answers that Kiln's creative mode stays idle.
4. Variant: Creative Mode's `mods/creative/` folder installed by hand into `<game>/mods/` (leave its `dinput8.dll` out:
   Kiln is the game's `dinput8.dll`), drop-ins on: the same lines with `(folder)`.

Mod files needed: Creative Mode 3.2.0 from Nexus (a copy was used for the lab run). Its server build ships a
`powrprof.dll` loader per a third-party guide; that file is not in the copy used here.
