# ShroudForge mods with Kiln

Kiln runs ShroudForge's Lua mods unchanged. Kiln ships ShroudForge's runtime, built from Kiln's official fork
([frostdev-ops/shroudforge](https://github.com/frostdev-ops/shroudforge)), so ShroudForge mods work even if you never
installed ShroudForge. If you have ShroudForge installed, you keep it: your mod folders and settings stay where they
are, and with one switch Kiln can run your own ShroudForge copy instead of the bundled one. Design and API coverage:
[shroudforge-compat.md](shroudforge-compat.md).

## For players

1. Install Kiln. Its `shroudforge` component (on by default) puts ShroudForge's runtime in `kiln/plugins/shroudforge/`.
   If ShroudForge was already installed, Kiln replaces only its `dinput8.dll` and leaves `dbghelp.dll`, `winmm.dll`, the
   `shroudforge/` folder and `mods/` alone.
2. Put ShroudForge mods where ShroudForge reads them: `<game>/mods/<mod>/`, or the folder set in ShroudForge's settings.
3. Turn mods on and change their settings. Use the Kiln Launcher, ShroudForge's in-game Modloader window, or
   `mods/<mod>/extended.mod.json`. A setting marked "live" applies while you play.
4. To run your installed ShroudForge instead of Kiln's copy, switch on **Settings → Kiln (kiln.json) → ShroudForge mods
   → "Use my installed ShroudForge"** in the launcher (in the file: `"shroudforge": {"useInstalled": true}`), then
   restart the game. If your copy is missing or broken, Kiln uses its own and says so in `kiln.log`.
5. Start the game or the server through Kiln. `kiln/logs/kiln.log` names the runtime that runs
   (`ShroudForge runtime: bundled 1.7.9 ...` or `installed ...`) and shows ShroudForge's log as `[sf] ...` lines.
   `kiln/state/shroudforge.json` says how ShroudForge ran:
   - `hosted` (with `source` bundled or installed);
   - `self-hosted`: ShroudForge started itself through its own `dbghelp.dll`, next to Kiln;
   - `off`, `not-installed`, or `failed` with a `reason`. The game and Kiln run on in every case.

Notes:
- **Steam Deck / Linux:** when Kiln hosts ShroudForge you need no extra `WINEDLLOVERRIDES` beyond Kiln's own
  (`dinput8=n,b`).
- **Updates:** the bundled runtime updates with Kiln. While Kiln hosts your installed copy, ShroudForge does not update
  itself at game start, so its updater cannot replace Kiln's `dinput8.dll`. To update your own ShroudForge, run its
  updater (`shroudforge/shroudforge.exe`) with the game closed. If it puts its own `dinput8.dll` back, reinstall Kiln
  from the launcher; your mods stay.
- **Returning to standalone ShroudForge:** uninstall Kiln with the game closed. If the proxy is still Kiln's, the
  original SF proxy comes back byte-for-byte; installing twice or updating Kiln keeps the first backup. Reinstalling Kiln also recovers missing
  `dbghelp.dll` / `winmm.dll` from older switch-from-ShroudForge receipts, after checking their hashes. A newer helper
  or proxy you installed yourself is kept instead, with the original backup's recovery path in the result.
- **Profile copies:** activation never changes switches or settings in a mod folder you installed yourself. It only
  changes Kiln's own copies. Deactivating a profile or uninstalling removes unchanged copies from SF's mods folder.
  Edited settings, extra files, changed code and old receipts without exact settings proof are kept in place; the
  launcher names each retained folder and why. **Those retained mods may still run in standalone SF.** With the game
  closed, review or move the listed folders if you do not want them. Your Library copies and `kiln/` settings stay.
- **Game archives:** uninstall does not blindly copy `.kfc.bak` over the game or delete `.bak.integrity`. These can
  belong to an earlier SF/EML setup. Runtime asset patches and SF's own state/config remain the runtime's responsibility;
  an installer roundtrip is not a claim that every startup asset mod has been reversed. Missing build-profile files
  seeded into SF's runtime folder at startup, plus SF's logs/cache/state, can remain; uninstall does not sweep them.
- **Turning ShroudForge off for one start:** set `KILN_SHROUDFORGE=0` in the launch environment, or use play-once
  "without mods".
- ShroudForge needs a game profile for your game build. The bundled runtime brings the profiles of its release, and a
  Kiln update brings new ones. Your own ShroudForge downloads them with its updates.
- Licences: Kiln is MIT. The bundled `shroudforge-runtime.dll` links kfc-parser and is GPL-3.0. Its licences, notice and
  source pointer are in `kiln/plugins/shroudforge/`, and every Kiln release includes its full source.

## For mod authors

Write your mod for ShroudForge exactly as its documentation says (`mod.json`, `extended.mod.json`, `src/mod.lua`,
`shroudforge.*`, `runtime.*` and the EML `game.*` API). Under Kiln the same runtime runs it, so you need nothing extra.
Things to know:
- Kiln's own services may change the same game data your mod changes (for example game settings). The last write wins.
  Use `runtime.overrides` so ShroudForge restores your changes when your mod is switched off.
- The Kiln Launcher builds its settings screen from your `extended.mod.json` settings: label, description, control,
  min/max/step, options, groups, `enabledWhen` and `applyAt`. Declare them fully.
- To write a Kiln (C#) mod instead, see the coverage table in [shroudforge-compat.md](shroudforge-compat.md#8-api-coverage)
  for Kiln's equivalent of each ShroudForge function.
