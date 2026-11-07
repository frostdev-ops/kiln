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

Each plugin checks whether the engine's own loader runs in the same process, and **steps aside** when it does: the
mods are then that loader's to start, never both. The log says `<plugin> steps aside: ...`. When the other loader's
files are in the game folder but did not start (for example under Wine or Proton, where those proxy names are not
loaded), the plugin says so and loads the mods itself.

| Engine | How Kiln knows its loader runs | Why stepping aside keeps the setup working |
|---|---|---|
| Greeneye | Greeneye Loader's own per-process claim (`Local\Greeneye-Loader-<pid>`) exists | Greeneye Loader itself lets only one copy run. If Kiln starts first, Kiln takes the claim, so a copy of Greeneye Loader that loads later stays idle, as a second Greeneye Loader does. |
| Flame | a module in the game folder or `kiln/chain/` carries Flame's entry-point name without exporting it (Flame's proxy, chained by Kiln) | Flame's loader starts the mods in `<game>/mods` from its own thread; starting them again would run the mod twice. Switch its line off in `kiln/chain.ini` to let Kiln start them (and from profiles). |
| Shroudtopia | `shroudtopia.dll` is loaded, or a `winmm.dll` from the game folder that names Shroudtopia or Shroudweave | Shroudtopia's loader thread already drives its mods. |

## Status files and logs

Each plugin writes `kiln/state/<engine>.json` (every mod, its status and reason) and logs under its own tag in
`kiln/logs/kiln.log`: `[greeneye] ...`, and a mod's own lines as `[greeneye:<id>] ...`. Greeneye and Shroudtopia also
keep the log file their mod authors ask for in bug reports: `kiln/logs/greeneye-loader.log`,
`kiln/logs/shroudtopia.log`.

## Tests

`native/plugins/compat/tests/run.py` runs each plugin in a stand-in game process under plain Wine with test mods that
check the whole API, and `native/plugins/compat/tests/lab.sh` puts read-only probes into a lab dedicated server.
