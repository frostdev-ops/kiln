# Shrouded Engine Tweaks mods on Kiln (kiln-set)

Kiln runs Lua mods written for **Shrouded Engine Tweaks** (SET 0.1.1, MIT, github.com/Cacaida/Shrouded-Engine-Tweaks)
unchanged, through the plugin `kiln/plugins/kiln-set.dll`. You do not need SET's own `powrprof.dll`. Unlike SET, the
mods also run on a **dedicated server**.

## Install a mod
1. Install Kiln as usual (`tools/deploy-server.sh` copies `kiln-set.dll` to `kiln/plugins/` and `kiln-types.dll` to
   `kiln/services/`).
2. Put the mod where SET puts it: `<game>/shrouded_engine_tweaks/mods/<name>/init.lua` (and its other files).
3. Start the server or the game. `kiln/logs/kiln.log` shows each mod as SET does, under `[set]`:
   `[set] Mod loaded: myheal`, `[set] Mods: 2 loaded, 0 failed`. Everything a mod prints goes there too.

When SET's own `powrprof.dll` runs in the process, kiln-set steps aside (`kiln-set steps aside: SET's own
powrprof.dll is loaded ...`): the mods are SET's to run. Kiln uses `dinput8.dll` and SET `powrprof.dll`, so both can be
installed side by side; Kiln never replaces SET's file.

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
  (kiln.items; by GUID or debug name), `settings` / `changeSettings` (kiln.settings, checked against its ranges). They
  act on the host player in a hosted game and on the first player on a dedicated server.
- **Missing**: `GameOptions.*` (raises "game options not found yet"), `Res.text` and English names (nil), `Admin.send`,
  `Admin.setLevel`, `Admin.setFlameLevel` (raise an error saying so). SET's overlay windows other than the Systems view
  (Kiln's own Systems window) are not there: no Bindings, Resources, Hooks, Game Log, Settings/Status or ImGui Debug
  window yet; the console is the file above.

## Differences from SET
- Writes to components skip the game's dirty-marking, as in SET; on a server Kiln is authoritative, so writes there
  reach players when the game next sends the field.
- `Hooks.observe` uses kiln.hooks' entry hooks. Targets Kiln itself hooks (the world step, the program runner) are
  refused. A pattern that registered once keeps its target after reload.
- `GetGameBuild()` is the exe's link time stamp, as in SET (server 26430253: 1793791038).
- Bindings are saved in `shrouded_engine_tweaks/bindings.ini` as in SET, but there is no Bindings window yet.

## Lab test
`mods/tests/set-test/mods/` holds SET's `myheal` example from its modding guide (unchanged) and `settest`, which checks
the API on a server and prints PASS/FAIL lines. Copy both folders to `<lab>/shrouded_engine_tweaks/mods/`.
