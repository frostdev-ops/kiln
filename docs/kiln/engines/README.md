# Other mod loaders on Kiln

Kiln loads mods written for other Enshrouded mod loaders through compatibility plugins in `kiln/plugins/`. Each
plugin reads the other loader's mod format and gives its mods the same API that loader gives them, built on Kiln. You
keep the mods as they are; you do not need the other loader's proxy DLL.

| Engine | Plugin | Mods it loads | Page |
|---|---|---|---|
| Greeneye Loader 1.2.2 (API 2) | `kiln-greeneye.dll` | `greeneye/<id>/` with `mod.json` and a DLL exporting `GreeneyeModStart` | [greeneye.md](greeneye.md) |
| Flame (Creative Mode's loader) | `kiln-flame.dll` | `mods/<id>/` with `flame.json` and a DLL exporting `FlameStartRuntime` | [flame.md](flame.md) |
| Shroudtopia 0.1.1 / 1.0 r1 | `kiln-shroudtopia.dll` | `mods/*.dll` and `mods/<name>/` exporting `CreateModInstance` | [shroudtopia.md](shroudtopia.md) |
| EMF 0.3.3 (API v21) | `kiln-emf.dll` | `emf/mods/` | [../emf.md](../emf.md) |
| Enshwick 1.0.0-alpha.13 | `kiln-enshwick.dll` (hosts Enshwick's own loader) | `mods/<author.name>/`: DLL mods, `main.luau`, `pack.toml` data packs | [enshwick.md](enshwick.md) |
| shroudlink 0.8.0 (SDK ABI 8), server | `kiln-shroudlink.dll` (shroudlink's own server core, built from source) | `shroudlink_mods/*.dll`; EML data mods go to kiln-eml | [shroudlink.md](shroudlink.md) |
| Ember (server SDK, no ABI published yet) | `kiln-ember.dll` (finds and reports; starts nothing yet) | `ember/mods/<mod>/` with one library | [ember.md](ember.md) |

Running such a mod runs that mod's own code in the game process, as with any native mod. Install mods only from
sources you trust.

## Where mods are found

The same rule for every engine:

1. **The profile's list.** A Kiln Launcher profile can list mods of an engine under `mods.<engine>` in
   `kiln/profile.json`, each entry `{ "id", "path", "version", "enabled" }` as for the other kinds (`path` is the mod
   folder; for Shroudtopia also a DLL). They load first, in the profile's order. `"enabled": false` keeps a mod off.
2. **The engine's own folder** (`greeneye/`, `mods/`) is read too when the profile has no `mods.<engine>` list (the
   launcher does not manage that engine, so what you installed by hand keeps working), when the profile has
   `"dropins": true`, or when there is no profile at all.
3. Play-once **no-mods** and an invalid profile load nothing.
4. A mod listed in the profile wins over a folder with the same id.

## When the other loader is installed too

Each plugin checks whether the engine's own loader runs in the same process, and **steps aside** when it sees it before starting mods. The log says `<plugin> steps aside: ...`. When the other loader's
files are in the game folder but did not start (for example under Wine or Proton, where those proxy names are not
loaded), the plugin says so and loads the mods itself.

| Engine | How Kiln knows its loader runs | Why stepping aside keeps the setup working |
|---|---|---|
| Greeneye | Greeneye Loader's own per-process claim (`Local\Greeneye-Loader-<pid>`) exists | Creating the named claim is atomic. A loader honoring that same claim stays idle when Kiln took it first; Kiln yields when the claim already exists. |
| Flame | a module in the game folder or `kiln/chain/` carries Flame's entry-point name without exporting it (Flame's proxy, chained by Kiln) | Flame's loader starts the mods in `<game>/mods` from its own thread; starting them again would run the mod twice. Switch its line off in `kiln/chain.ini` to let Kiln start them (and from profiles). |
| Shroudtopia | `shroudtopia.dll` is loaded, or a module from the game folder or `kiln/chain/` names Shroudtopia or Shroudweave | Shroudtopia's loader thread already drives its mods. |
| Enshwick | its `powrprof.dll` stub (texts `enshwick-loader-stub`) is loaded, its claim `Local\enshwick-stub-<pid>` exists, or a module exporting `enshwick_start` is loaded | Kiln takes the same claim when it starts the loader, so a stub that starts later stays idle (the stub's own rule); when the stub is first, it runs Enshwick and Kiln starts nothing. |
| shroudlink | a loaded module exports both `CrashHandlerFactory` and `DirectInput8Create` (its core as `steam_api64.dll`, `crashhandler64.dll`, `dinput8.dll`, chained or not) or is named `shroudlink_core.dll`; backstop: its claim `chestsort_server_<pid>` | That core loads `shroudlink_mods/` itself; both copies honor the same claim, so only one hooks. |
| Ember | a DLL from the server folder or `kiln/chain/` carries `ember-loader` / `ember_loader` | Ember's loader runs its mods; kiln-ember starts none anyway until Ember publishes its ABI. |

Flame and Shroudtopia are checked again before mod dispatch, including after Shroudtopia's boot delay. Renamed
proxies in `kiln/chain/` are recognized. These engines have no cooperative process claim or shutdown contract:
a real loader arriving after a mod has entered can still start that mod again. Kiln stops further dispatch when it
observes such a loader, but cannot undo arbitrary hooks or threads already started. Disable one loader before
starting the process when both could arrive in that order.

**Path policy.** A mod DLL must be a regular file inside its authorized root: the mod folder, the engine folder for a
mod found there, or the parent of an explicitly listed flat DLL. From that root down (the root included, so a mod
folder or a profile's listed folder must not itself be a link), symlinks, junctions, `..`, alternate streams and
device names are refused, and the opened file's final path must still lie under the root. The root may sit anywhere,
such as a profile pointing at a library outside the game folder (Kiln's launcher library through `Z:\...`). Folders
above the root are the player's layout and are not judged: a Steam library behind a symlink, or Wine/Proton drive
links such as `Z:\` (itself a link to `/`), are fine. Under Wine, symlinks to directories are refused like on Windows,
but Wine follows a symlink to a *file* in every Win32 query, so the plugins cannot see that one case; the installer
that writes the mod folder must not create such links. Directory and file handles keep every path component and the
DLL contents stable from export validation through loading. A callable, file-backed export is required; forwarded and
data exports do not qualify.
The export precheck does not validate dependencies or make native mod code safe.

Compatibility runtimes and entered mod DLLs stay mapped for the process lifetime. Revoking a plugin stops queued
work and cancellable watchers; it cannot terminate foreign threads or reclaim trampolines they may still call.
Fault guards cover synchronous loader-invoked initialization and lifecycle calls. Mod-owned threads and
arbitrary-signature detours execute directly and can still crash the process.

## Status files and logs

Each plugin writes `kiln/state/<engine>.json` (every mod, its status and reason) and logs under its own tag in
`kiln/logs/kiln.log`: `[greeneye] ...`, and a mod's own lines as `[greeneye:<id>] ...`. Greeneye and Shroudtopia also
keep the log file their mod authors ask for in bug reports: `kiln/logs/greeneye-loader.log`,
`kiln/logs/shroudtopia.log`.

## Tests

`native/plugins/compat/tests/run.py` runs each plugin in a stand-in game process under plain Wine with test mods that
exercise API, path, malformed-file, lifecycle and concurrency regressions, and `native/plugins/compat/tests/lab.sh` puts read-only probes into a lab dedicated server.
