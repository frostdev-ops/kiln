# kiln.settings: the server's game settings, live

Reads and changes the running world's game settings, the `gameSettings` block of `enshrouded_server.json` (player,
enemy and boss factors, durability, starving, day/night length, tombstone mode, weather, XP, ...), without a restart,
and on request writes them back to `enshrouded_server.json` so they survive one. Evidence and the per-setting table:
[docs/re/settings.md](../../re/settings.md).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_settings.h` (`kiln.settings` v1) |
| Native | `native/services/settings/` → `kiln/services/kiln-settings.dll` |
| Symbols | `sigdb/symbols/server.d/settings.json` (features `settings.live`, `settings.resend`, `settings.world`, `settings.daytime`) |
| Managed | `managed/services/Kiln.Settings/` → `kiln/host/Kiln.Settings.dll`, static class `Kiln.Settings` |
| Test mod | `mods/tests/settings-test/` |

## What a change does
1. It writes the value into the live `keen::ecs::GameSettings` block, the object every game system reads (the same
   memory the game's own admin menu writes). The server uses it from the next step (see the table for settings that
   apply to the *next* event: a buff, a death, a loot roll, a spawn).
2. It sets the game's own "send the settings to the players" flag: the next server-info message carries the whole block
   to every connected player. A player who joins later gets the block from the game at login.
3. It changes nothing on disk. `Save()` writes the live values into `enshrouded_server.json` with
   `"gameSettingsPreset": "Custom"` (the game ignores `gameSettings` with any other preset), keeping every other key;
   the old file stays as `enshrouded_server.json.kiln-backup`. The server reads the file when it starts (and rewrites
   it in its own format then).

No setting needs a restart on the server. Values are checked against the ranges in the server's
`enshrouded_server_readme.txt`; out of range is refused (never clamped) with a message that names the range.
`anyValue: true` skips that check (still sane for the type: factors 0–100, durations 1 s–24 h, valid options).

Readiness: the settings are found at the end of the server's first world step, which is when Kiln loads mods. So
`Settings.Ready` is false during a mod's first `OnLoad`; one step later it is true. The game's own programs start a few
seconds later still (when the scene switches to Run); a change made before that is simply used from then on.

## C#

```csharp
using Kiln;

if (!Settings.Ready) return;                          // e.g. check again on the next tick
var dmg = (double)Settings.Get("enemyDamageFactor");  // bool, double, long (ints, ns durations) or string (options)
var change = Settings.Set("enemyDamageFactor", 2.5);  // -> SettingChange { Before = 1, After = 2.5, Changed = true }
Settings.Set("weatherFrequency", "Rare");             // option by name (any case) or value
Settings.Set("dayTimeDuration", TimeSpan.FromMinutes(45));   // or 2700000000000 (ns) or "45m"
Settings.Set("enableDurability", false);
if (!Settings.TrySet("playerStaminaFactor", 9, out _, out var why)) Log.Warn(why); // "playerStaminaFactor: allowed 0.25 to 4"

foreach (var s in Settings.All)                       // SettingInfo: Name, Group, Kind, Value, Initial, Default,
    Log.Info($"{s.Name} = {s.Value} ({s.Effect})");   //   Min, Max, Options, Flags (Live, PlayersRead, NextEvent, ...)

Settings.Set("bossHealthFactor", 2, resend: false);   // batch several changes ...
Settings.Set("bossDamageFactor", 2, resend: false);
Settings.Resend();                                    // ... and send them to the players once
Settings.Reset("bossHealthFactor");                   // back to the value the server started with
Settings.ResetAll();
string msg = Settings.Save();                         // "saved 38 settings (2 changed) to ...enshrouded_server.json; ..."
var st = Settings.Status;                             // Ready, Revision, ResendsSent, Preset, DayDuration, TimeOfDay, ...
```

- Everything except `Available`, `Ready`, `Status` and `Revision` runs on the tick thread (mods are called there).
- Exceptions: `ArgumentException` (value refused; the message says why), `KeyNotFoundException` (no such setting),
  `InvalidOperationException` (not ready yet / wrong thread), `NotSupportedException` (service unavailable on this
  build), `IOException` from `Save`.
- Values are JSON-friendly, as `enshrouded_server.json` has them: factors `double` (rounded to 6 decimals), whole
  numbers and durations `long` (nanoseconds), options `string`, switches `bool`. `Set` also takes a `JsonElement`.
- `Revision` goes up on every change kiln.settings sees, including one made in the game's admin menu (logged as
  `changed outside kiln.settings`). Poll it to refresh a UI.
- Kiln.Settings holds no per-mod state: nothing to clean up on unload.

### For Kiln.Admin
| Admin op | Kiln |
|---|---|
| `settings_get` → `{values, write_supported}` | `{ values = Settings.Values, write_supported = Settings.Ready }` |
| `settings_set {path, value}` → `{path, before, after}` | `var c = Settings.Set(path, value)` (value as the JSON element) → `{ path, before = c.Before, after = c.After }`; `ArgumentException.Message` is the user-facing refusal |
| "Make permanent" | `Settings.Save()` |

`Settings.All` provides names, ranges and option lists from the game (options from reflection, ranges from the readme).

## C (native services and plugins)

```c
#include "services/kiln_settings.h"
const KilnSettingsApi* s = api->service(KilnStr{"kiln.settings", 13}, 1);   /* in on_ready; NULL = unavailable */
/* on the tick thread: */
uint32_t i;
if (s->find(KilnStr{"enemyDamageFactor", 17}, &i) == KILN_OK) {
    KilnSettingValue v = { .kind = KILN_SETTING_FLOAT, .number = 2.5 }, before;
    if (s->set(owner, i, &v, 0, &before) == KILN_ERR_INVALID_ARGUMENT) { char why[256]; s->last_error(why, sizeof why, NULL); }
}
```
`status` (any thread), `count`, `info`, `find`, `option`, `get`, `set`, `reset`, `resend`, `save`, `last_error`; see the
header for flags (`KILN_SETTINGS_SET_NO_RESEND`, `KILN_SETTINGS_SET_ANY_VALUE`) and value kinds.

## Testing in a lab
```sh
export KILN_OUT=<build dir>
tools/lab/lab.sh new settings 25750                           # once
dotnet build mods/tests/settings-test -c Release -o $KILN_OUT/mods/settings-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/settings --policy any --samples
tools/lab/lab.sh start settings; grep -E 'PASS|FAIL|SUMMARY' ~/srv/enshrouded/labs/settings/kiln/logs/kiln.log
```
The test compares every live value with the lab's `enshrouded_server.json`, changes factors/options/switches/durations
and reads them back (API and the block), checks that the game sent the block (its flag went back to 0), checks the day
/night clock takes new durations and runs at the new speed, checks refusals, `Save()` (file content, key order,
passwords kept, backup) and `Reset`, and restores everything (the config byte for byte). Restart check: `touch
kiln/data/settings-test/arm-restart`, run once (it saves changed values), restart the server: the second run checks the
server started with them, then restores the file. Re-run without a restart: `echo 'reload settings-test' >
kiln/control/st.cmd`.

## Client checklist (needs a joined player)
- After `Set("playerStaminaFactor", 2)` the stamina bar is half as long within a few seconds; back at 4 it returns.
- After `Set("dayTimeDuration", "2m")` the sky moves visibly faster for the player; the player's clock stays in step
  with the server's (no jumps after a reconnect).
- `enableGliderTurbulences false`: no turbulence while gliding; `flameLevelFogRemoval`: altar placement in the Shroud
  follows the new level; `perkCostFactor`: the upgrade cost shown changes.
- `enemyDamageFactor 5`: enemies hit much harder; `enemyHealthFactor 4`: health bars of enemies near the player grow.
- A change in the in-game admin settings menu shows up in kiln.log (`changed outside kiln.settings`) and in
  `Settings.Get`.

## In a game client (hosted worlds)
- A client-hosted world has no `enshrouded_server.json`: its settings belong to the world (the game's world settings,
  stored with the save). `status.config_path` is empty, and `save()` returns UNAVAILABLE with "client-hosted world: there
  is no enshrouded_server.json; ...". Live reads and changes work on the running world as on a server.
- When the game closes the server world, the instance, the block and the world's original values (what `reset()`
  restores) are dropped; the next world is read fresh, even when the game reuses the addresses.
