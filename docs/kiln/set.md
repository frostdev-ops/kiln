# Shrouded Engine Tweaks mods on Kiln (kiln-set)

Kiln runs Lua mods written for **Shrouded Engine Tweaks** (SET 0.2.0, MIT, github.com/Cacaida/Shrouded-Engine-Tweaks)
through the plugin `kiln/plugins/kiln-set.dll`, subject to the safety restrictions below. You do not need SET's own `powrprof.dll`. Unlike SET, the
mods also run on a **dedicated server**.

## Install a mod
1. Install Kiln as usual (`tools/deploy-server.sh` copies `kiln-set.dll` to `kiln/plugins/` and `kiln-types.dll` to
   `kiln/services/`).
2. Put the mod where SET puts it: `<game>/shrouded_engine_tweaks/mods/<name>/init.lua` (and its other files).
3. Start the server or the game. `kiln/logs/kiln.log` shows each mod as SET does, under `[set]`:
   `[set] Mod Loaded: myheal`, `[set] Mods: 2 loaded, 0 failed` (SET 0.2.0's wording; 0.1.1 wrote `Mod loaded:`). Everything a mod prints goes there too.

kiln-set steps aside when it finds a non-system `powrprof.dll` anywhere in the loaded-module inventory, or a renamed
module containing SET's branding. This is deliberately conservative: an unrelated non-system power proxy also
suppresses kiln-set. Windows' own System32 power DLL does not. The check runs before initialization and before Lua
tick/menu work; a module loaded later stops the runtime and releases its Lua owners. Kiln uses `dinput8.dll`, so
installation does not replace SET's file.

A module inventory that cannot be read is not SET. It fails now and then for a moment, for example while a thread
of the game ends. kiln-set and kiln.types then keep running and look again one second later. They step aside only
when a SET or power proxy is actually seen, or when **five** looks in a row fail (about four seconds). One clean
look resets the count. At startup the five looks happen at once, 20 ms apart. Five, because one failed look says
nothing, a lasting fault shows in a few seconds, and each look is costly under Wine. Stepping aside while the game
runs undoes the mods' resource changes first.
The types service uses the same detector for its runner hook, avoiding competing system controls. If SET loads
after Kiln installed that hook, the adapter restores all switches and remains passive until process exit.

## When mods run
| | Dedicated server | Game, main menu | Game, a world you host |
|---|---|---|---|
| Mods load | the first world step | when the UI runs | (already loaded) |
| `Tick.add`, `onUpdate` | every server step | every UI frame | every server step |
| `Observe` handlers | after each step | - | after each step |
| `Draw.add`, `ImGui.*` | never (no overlay) | every overlay frame | every overlay frame |
| `Keys.*` | never | when the game window has focus | same |

## Console
SET's console runs Lua typed into its overlay. On Kiln, put lines in a file `<game>/kiln/control/set/<any>.lua`: at
the next tick each line runs (expressions print their value), the output goes to `kiln.log`, and the file is deleted.
```sh
echo 'Ecs.fields("Health")' > <server>/kiln/control/set/x.lua
```

## What works
Every function in SET 0.2.0's api-reference has a Kiln implementation, except those marked missing. The full table, with the
matching Kiln C# API and how each was tested, is in
[docs/re/shrouded-engine-tweaks.md](../re/shrouded-engine-tweaks.md#api-coverage).
- **Ecs, Types, Systems, Events, Hooks, Res, Tick, lifecycle, require/io/json**: SET's own Lua code, on Kiln's
  services (kiln.types, kiln.world, kiln.assets, kiln.hooks).
- **Admin**: on Kiln's typed services: `setTime` (kiln.time), `teleport` (kiln.world), `spawn` (kiln.spawn), `give`
  (kiln.items; by GUID, debug name or English item name), `setLevel` (kiln.progression),
  `setFlameLevel` (kiln.knowledge), `settings` / `changeSettings` (kiln.settings, checked against its ranges). They
  act on the host player in a hosted game and on the first player on a dedicated server.
- **Texts**: `Res.text` resolves shipped item/template names. Other English localisation tags need the locally
  generated client text catalog (`Texts` in `Kiln.Content`); a server without that catalog returns nil.
- **Tools**: `Kiln.DevTools` supplies Bindings, Resources, Hooks, Game Log, Status and Console windows, as well as
  Systems and Types. They are Kiln windows, with view-model tests; their drawing and input still need a client.
  Bindings lists Kiln `KeyBindings`, and Hooks lists Kiln hook statistics; they do not expose SET's private Lua binding
  registry or its full capture diagnostics.
- **Missing**: `GameOptions.*` (raises "game options not found yet"), `Admin.send` (raises an unavailable error), and
  ImGui Debug. SET's `settings.ini` editor is not reproduced by the Status window.

## New in SET 0.2.0
- **`Hooks.rule`, `Hooks.param`, `Hooks.list`**: rules are data. kiln-set applies them inside the hooked call, on the
  game's thread, without Lua. On a mod's own pattern (`builds = {...}` required) a rule changes number arguments.
  On a catalog target it may also change the listed fields, block the call or change the result.
  - Arguments: written back into the registers or the caller's stack slots by the kiln.hooks entry hook.
  - Fields: written with a plain guarded copy. A field marked "on the stack" is only changed when it is on the
    calling thread's stack. A read or write that fails turns that rule off until restart; the call stays unchanged.
  - `block`: the entry hook returns to the game's caller (the documented kiln.hooks early return).
  - `after`: needs code after the original. Kiln has a typed kiln.hooks raw link for `combat_xp`, the only catalog
    target that allows it. It is installed with the first such rule and stays.
- **Catalog targets `combat_xp` and `hit`**: SET's entries, unchanged, checked at startup (build, pattern, anchors).
  SET lists the client builds 1792148057, 1792507660 and 1793791052. On any other build, a dedicated server
  included, both are off: `Hooks.rule` returns nil with a yellow line, as on SET. `kiln.log` has a `[set] Catalog:`
  line per target.
- **`Features()`**: SET's ids with Kiln's state.

  | Id | On Kiln |
  |---|---|
  | `game_build` | compatible on SET's three client builds and on the server build 1793791038 (Kiln's lab) |
  | `resources` | kiln.assets is there; available once the game's data is loaded |
  | `english_names` | always (item and template names; other tags need the client text catalog) |
  | `game_options`, `field_of_view` | never: "Not available on Kiln" |
  | `systems` | kiln.types can switch systems (only two may be switched off, see below) |
  | `events`, `admin`, `admin_give`, `admin_settings` | the Kiln service is there; available in a world you host |
  | `overlay` | kiln.ui on a client; "Dedicated server: No overlay" on a server |
  | `combat_xp`, `hit` | the catalog check, with its reason |
  | `hooks` (Kiln only) | kiln.hooks is running |
  | `admin_send` (Kiln only) | never: use the Admin helpers |
  | `mod_settings_page` (Kiln only) | never: values are saved, but there is no page and `onChange` never runs |
- **`Settings.define{...}`**: values are typed, clamped, and saved to the mod's `settings.json` within a second, at
  once on a reload or when the game closes. Unknown keys in the file are kept. A wrong type falls back to the default
  with a yellow line. A broken file becomes `settings.json.bad`. Edit the file by hand while the game is closed.
- **`settings.json` belongs to `Settings.define`**: `io.open("settings.json", "w")` returns nil and a message. Reading
  works.
- **Key names**: Space, Enter, Tab, arrows, numpad, and punctuation by its US name (`Semicolon`, `Equals`, ...).
  `bindings.ini` saves names; files with `0xNN` codes still load.
- **Changes go back when a mod stops**: resource edits (`Res`) made by a mod are undone when it fails while loading,
  on `reload()`, and when kiln-set steps aside. A value the console or the game changed since is left alone. The
  console's own edits are not undone. `Systems.enable` was already undone per mod (kiln.types owners).
- **`load()`**: a function built with `load()` belongs to the mod that registers it (Kiln did this before 0.2.0).
- **Log lines** use SET 0.2.0's wording (`Mod Loaded:`, `Mod Failed:`, `Mod Skipped:`, `Hook '...' removed`).

## What Kiln does not show
SET 0.2.0's own overlay additions are not part of kiln-set:
- the **Mod Settings** page (so `onChange` never runs; `Features().mod_settings_page` says so),
- the **Entities** window (inspector),
- the **Compatibility** window (`Features()` has the same data; `kiln.log` has the catalog lines),
- the **bug report** dialog and `debugLog` setting,
- the Hooks window's rule rows (rule hits, refusals and conflicts are kept, but no Kiln window lists them yet).

## Differences from SET
- Only `stamina_depletion_sprint` and `fall_damage_infliction` can be disabled; critical and unknown systems are
  refused. Disable requests are per mod and released on initialization failure, reload and shutdown. Another mod's
  active request keeps the system disabled. See [kiln.types](services/types.md#systems).
- Lua execution has instruction/deadline limits, including protected calls and coroutines. A callback that errors
  is removed while other mods continue. This bounds Lua execution; it is not preemption of arbitrary native code.
- `require`, `io.open` and `io.lines` use the mod's folder and reject traversal, devices, links/reparse points and
  paths outside it. `load` accepts source text in that mod's environment. Process execution, native library loading,
  `debug` and shared mutable standard libraries are unavailable. Console files are trusted server-owner input.
- Reflection checks the actual component buffer extent. Fixed arrays are checked per element; game-owned string
  and dynamic-container writes are refused. Event posting additionally validates the registered event's complete
  inline payload. NaN and infinity are refused for numeric game fields.
- `Res` lookups and resource views require the tick thread. Kiln resource buffers can move on the next tick;
  views re-resolve their key and validate the current length on every access. Read/copy values in Tick for Draw.
- Writes to components skip the game's dirty-marking, as in SET; on a server Kiln is authoritative, so writes there
  reach players when the game next sends the field.
- `Hooks.observe` and `Hooks.rule` use kiln.hooks' entry hooks. Targets Kiln itself hooks (the world step, the
  program runner) are refused. A pattern that registered once keeps its target after reload.
- Catalog anchors are compared with live memory, not with the exe file. A patch at an anchor makes the target fail
  its check ("Anchor n differs"); it is then off, not wrong.
- A hook that runs after kiln-set's in the kiln.hooks chain sees the arguments after the rules, and a blocked call
  does not reach it. SET's own observers see blocked calls; so do kiln-set's.
- A rule's field write never lifts page protection. SET's does not either.
- `GetGameBuild()` is the exe's link time stamp, as in SET (server 26430253: 1793791038). `GetVersion()` is "0.2.0".
- The launcher treats a placed mod folder that changed as the player's. A mod that saves `settings.json` (or any
  file) into its folder makes the launcher refuse to place the profile again ("managed copy changed"). This is
  older than 0.2.0, but `Settings.define` makes it more likely. It is an open point for the launcher.
- Bindings are saved in `shrouded_engine_tweaks/bindings.ini` as in SET, the Kiln.DevTools Bindings view lists Kiln `KeyBindings`, so SET's private Lua bindings still use this file.
- `ImGui.BeginChild(id, w, h, border, flags)` uses SET's argument order; the current `kiln.ui` service always draws
  a child border. Window/child nesting is checked and unwound between callbacks, including callbacks that omit End.

## Offline regression tests
`KILN_OUT=<out> bash native/plugins/set/tests/test-lua.sh` builds the real Lua adapter against a
small host shim and tests sandbox escapes, script limits, isolation, reflection and reload without launching the
game. `bash native/plugins/set/test-review.sh` tests the real hook capture adapter with ASan/UBSan, the plugin's
startup/late-load coexistence checks (a failed module inventory included), mocked ImGui nesting/recovery, and SET
0.2.0's rules: the rule engine, the catalog checks, and rules through the real hook adapter on a fake kiln.hooks
(`tests/rules_review_test.cpp`). `tests/set020_cases.inl` runs `Features()`, key names, `Settings.define`,
`Hooks.rule` and the write log through the Lua bridge. These do not verify client rendering, and no rule has run in
the real game yet.

## Lab test
`mods/tests/set-test/mods/` holds SET's `myheal` example from its modding guide (unchanged) and `settest`, which checks
the API on a server and prints PASS/FAIL lines. The final assembled dev.20 probe reported **39 PASS, 0 FAIL**
in the private `dev20-final-set` lab. `settest` has since gained three SET 0.2.0 checks (`Features()`,
`Settings.define`, the read-only `settings.json`) and expects `GetVersion() == "0.2.0"`; these have not run in a lab
yet. The dev.20 run includes localisation, hooks, event posting/observation, and flame-level
changes with restoration. The event check posts the registered `DebugEnemyProjectileEvent` stream and observes
its payload on the next step. The earlier WP15 result (37 PASS, 2 FAIL) used an absent `TestEvent1` stream and
predated the integrated event-layout/cache fixes; it is superseded by the assembled-source run.
Flame progress tests separately reported 61 PASS and 59 PASS after restart; they checked the stored and replicated
level and restoration. They do not prove client fog or placement limits. Copy both folders to `<lab>/shrouded_engine_tweaks/mods/`.
