# Shrouded Engine Tweaks mods on Kiln (kiln-set)

Kiln runs Lua mods written for **Shrouded Engine Tweaks** (SET 0.1.1, MIT, github.com/Cacaida/Shrouded-Engine-Tweaks)
through the plugin `kiln/plugins/kiln-set.dll`, subject to the safety restrictions below. You do not need SET's own `powrprof.dll`. Unlike SET, the
mods also run on a **dedicated server**.

## Install a mod
1. Install Kiln as usual (`tools/deploy-server.sh` copies `kiln-set.dll` to `kiln/plugins/` and `kiln-types.dll` to
   `kiln/services/`).
2. Put the mod where SET puts it: `<game>/shrouded_engine_tweaks/mods/<name>/init.lua` (and its other files).
3. Start the server or the game. `kiln/logs/kiln.log` shows each mod as SET does, under `[set]`:
   `[set] Mod loaded: myheal`, `[set] Mods: 2 loaded, 0 failed`. Everything a mod prints goes there too.

kiln-set steps aside when it finds a non-system `powrprof.dll` anywhere in the loaded-module inventory, or a renamed
module containing SET's branding. This is deliberately conservative: an unrelated non-system power proxy also
suppresses kiln-set. Windows' own System32 power DLL does not. The check runs before initialization and before Lua
tick/menu work; a module loaded later stops the runtime and releases its Lua owners. A module inventory that cannot
be inspected also refuses to run. Kiln uses `dinput8.dll`, so installation does not replace SET's file.
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
Every function in SET's api-reference has a Kiln implementation, except those marked missing. The full table, with the
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
- `Hooks.observe` uses kiln.hooks' entry hooks. Targets Kiln itself hooks (the world step, the program runner) are
  refused. A pattern that registered once keeps its target after reload.
- `GetGameBuild()` is the exe's link time stamp, as in SET (server 26430253: 1793791038).
- Bindings are saved in `shrouded_engine_tweaks/bindings.ini` as in SET, the Kiln.DevTools Bindings view lists Kiln `KeyBindings`, so SET's private Lua bindings still use this file.
- `ImGui.BeginChild(id, w, h, border, flags)` uses SET's argument order; the current `kiln.ui` service always draws
  a child border. Window/child nesting is checked and unwound between callbacks, including callbacks that omit End.

## Offline regression tests
`KILN_OUT=<out> bash native/plugins/set/tests/test-lua.sh` builds the real Lua adapter against a
small host shim and tests sandbox escapes, script limits, isolation, reflection and reload without launching the
game. `bash native/plugins/set/test-review.sh` tests the real hook capture adapter with ASan/UBSan, the plugin's
startup/late-load coexistence checks, and mocked ImGui nesting/recovery. These do not verify client rendering.

## Lab test
`mods/tests/set-test/mods/` holds SET's `myheal` example from its modding guide (unchanged) and `settest`, which checks
the API on a server and prints PASS/FAIL lines. The final assembled dev.20 probe reported **39 PASS, 0 FAIL**
in the private `dev20-final-set` lab. This includes localisation, hooks, event posting/observation, and flame-level
changes with restoration. The event check posts the registered `DebugEnemyProjectileEvent` stream and observes
its payload on the next step. The earlier WP15 result (37 PASS, 2 FAIL) used an absent `TestEvent1` stream and
predated the integrated event-layout/cache fixes; it is superseded by the assembled-source run.
Flame progress tests separately reported 61 PASS and 59 PASS after restart; they checked the stored and replicated
level and restoration. They do not prove client fog or placement limits. Copy both folders to `<lab>/shrouded_engine_tweaks/mods/`.
