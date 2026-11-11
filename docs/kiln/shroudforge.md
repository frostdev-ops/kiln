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
3. In the game, open **Mods → ShroudForge**, pick a mod, and use its **Switch and settings** panel. This is Kiln's
   existing in-process menu, not a second window; no separate ShroudForge installation is needed. The Kiln Launcher
   and `mods/<mod>/extended.mod.json` edit the same settings. The panel says which changes need a game restart.
   Runtime-only mods normally accept changes within a second or two; startup patches and their switches need a restart.
   If the mod is in a Kiln profile, also change its enabled switch in the launcher profile to keep it across launches.
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
- **Shortcut:** when Kiln hosts SF without a running Modloader helper, SF's menu key (**F9**, `SDL:66`, by default)
  and `shroudforge.ui.toggle_modloader()` toggle Kiln's ShroudForge page. Remap `shroudforge.menu` in Kiln's **Keys**
  tab. A supported configured keyboard chord is used initially; an explicitly empty, mouse-only, Meta or unsupported
  chord gets no Kiln default binding, not a surprise F9. The **Mods** entry remains available.
- **Installed / self-hosted SF:** an installed `shroudforge/shroudforge.exe` still supplies SF's own Modloader and
  World Editor windows, respecting SF's module switches. Kiln does not consume the real helper's toggle event.
  If that hosted helper exits, Kiln takes over the menu shortcut. Self-hosted SF retains its own shortcut behavior.
  The bundled runtime does not include the World Editor window or Debug Console.
- **Mod shortcuts:** ShroudForge mods with keyboard or mouse shortcuts (a `keybind` setting, read through
  `shroudforge.input`) get their key state from Kiln in the game: Kiln passes the game window's own key presses to
  the bundled runtime, so no separate ShroudForge installation is needed for them. `kiln.log` says where the key
  state comes from (`ShroudForge mod shortcuts: key state from Kiln: ...`), and so does `kiln/state/shroudforge.json`
  (`keys`). This has been checked with ShroudForge's real runtime in a test without the game, not yet in a game
  client. Shortcuts react only while the game window has the keyboard (as with ShroudForge itself); AltGr counts as
  right Alt. To record a new shortcut, type it in the setting (for example `Ctrl+SDL:66`) on the Mods page.
  - When you use your installed ShroudForge (an upstream release, which has no such interface), its own
    `shroudforge/shroudforge.exe` supplies the key state, as before. `KILN_SF_KEY_FEED=off` in the launch environment
    goes back to that exe with the bundled runtime too; without an installed ShroudForge the shortcuts then do
    nothing, and `kiln.log` shows `[sf] ... Keyboard shortcuts unavailable`.
  - Mods that read the game's own movement keys, such as SF Unlimited Flight's glider steering, use neither; this
    does not change the open W/S steering report ([shroudforge-compat.md](shroudforge-compat.md#55-held-ws-glider-steering-unresolved-client-check)).
- **Steam Deck / Linux:** when Kiln hosts ShroudForge you need no extra `WINEDLLOVERRIDES` beyond Kiln's own
  (`dinput8=n,b`). Its switches/settings use the game's existing Kiln overlay, so they do not depend on gamescope
  presenting a separate WebView window. Actual Deck rendering and controller navigation still need client testing.
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
  Updated kiln-eml leaves SF-owned archive output alone when idle. Before its own archive changes, it invalidates only
  recognized SF prepared-asset cache state (SF's own shapes: an `applied` document of this target, root
  `schemaVersion` 1) under SF's write lock, which it holds until its last archive write, so SF reapplies its asset mods
  on its next start. It never takes SF's startup lease (`<runtime>/startup-assets.lock`). Unknown state, a busy write
  lock, an SF preparation in progress (`preparing`) and SF's own unfinished publication
  (`.<exe>.shroudforge-stage/pending.json`) refuse the handoff; pre-existing stale state is not automatically scrubbed. See
  [EML archive ownership](eml.md#the-run) for the restoration rules and verification limits.
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
- `shroudforge.input` keybind queries get their key state from Kiln with the bundled runtime in a game client (see
  "Mod shortcuts" above), and from ShroudForge's own `shroudforge.exe` with an installed upstream ShroudForge. On a
  dedicated server they answer "not held" and "not pressed". Reading the game's own input components through
  `runtime.ecs` does not depend on either.
- Kiln's own services may change the same game data your mod changes (for example game settings). The last write wins.
  Use `runtime.overrides` so ShroudForge restores your changes when your mod is switched off.
- The Kiln Launcher and in-game Mods panel read your `extended.mod.json` metadata: label, description, control,
  min/max/step, text lengths, typed options, groups, `enabledWhen` and `applyAt` / `restartRequired`. Declare them fully.
  Edits splice only the changed value, preserving comments and layout. A fractional setting keeps its fraction (`2.0`),
  as ShroudForge types a setting (and a list, by its first member) by the number it reads; a file in which an earlier
  Kiln wrote a whole value without it (`[2, 1.5]` next to option keys such as `"1.5"`, which ShroudForge refuses) is
  read as the fractional setting and repaired by the next change. The in-game panel uses checkboxes, sliders,
  numeric/text/multiline fields and choice lists; radio/segmented use a combo, colors use text. Keybind settings use
  editable canonical SDL strings, not press-to-capture (for example `Ctrl+SDL:66|Mouse:4`, empty to disable).
  Numeric and yes/no choice lists must keep at least one selection: ShroudForge infers the option type from the first
  member, so clearing one would turn its choices into text. Text choice lists may be empty.
  SF's SDL backend additionally checks keyboard availability. Group action buttons write SF's public UI action requests;
  the mod must be active and register `shroudforge.ui.on_action` to handle them. The panel does not promise execution.
- To write a Kiln (C#) mod instead, see the coverage table in [shroudforge-compat.md](shroudforge-compat.md#8-api-coverage)
  for Kiln's equivalent of each ShroudForge function.
