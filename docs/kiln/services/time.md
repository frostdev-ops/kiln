# kiln.time: time of day and weather

The server world's day/night clock. A change reaches every client through the game's own ServerInfo sync and is
saved with the world (it survives a restart). Evidence and offsets: [docs/re/time.md](../../re/time.md).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_time.h` (`kiln.time` v1) |
| Native | `native/services/time/time.cpp` -> `kiln/services/kiln-time.dll` |
| Symbols | `sigdb/symbols/server.d/time.json` (features `time.read`, `time.set`, `time.weather`, `time.sync_probe`) |
| Managed | `managed/services/Kiln.Time` -> `kiln/host/Kiln.Time.dll` (static class `Kiln.Time`) |
| Test | `mods/tests/chat-test` (PASS/FAIL lines in `kiln/logs/kiln.log`) |

## C#
```csharp
// reference: <ProjectReference Include=".../managed/services/Kiln.Time/Kiln.Time.csproj" Private="false" ExcludeAssets="runtime" />
if (Time.Available && Time.Ready)
{
    DayTimeState s = Time.State;          // Hours, HoursField, IsDay, DayStart, NightStart, DayLength, NightLength,
                                          // TimeScale, ChangeCounter, SyncedCounter, GameTime, EcsTime, Weather...
    Log.Info($"it is {s.Clock}");         // "13:30"
    Time.OfDay = 7.5;                     // = Time.Set(7.5): forward to the next 07:30
    Time.Set("19:45");                    // also "7pm", "noon", "midnight", "dawn", "dusk", "7.25"
    Time.Set(20, TimeSetMode.GameReset);  // the game's own setTimeOfDay (day counter restarts)
    Time.ResetWeather();                  // end the current weather; every layer rolls a new one this frame
}
```
- `Time.Set` / `ResetWeather` must run on the tick thread (mod code: OnLoad, Tick handlers, Scheduler continuations,
  commands). Reads work anywhere.
- `TimeSetMode.Forward` (default) moves the clock forward to the next time it shows that hour and keeps the game clock
  counting up, so weather keeps changing. `GameReset` does what the admin menu does: the game clock restarts at day 0
  and weather timed on the old clock can stall; call `ResetWeather()` after it.
- `Hours` reflects a set at once; `HoursField` is the game's own field, updated in the next sim step.
- Day/night **lengths are read-only** here: the game re-applies `GameSettings.dayTimeDuration/nightTimeDuration` every
  step. Change them through the game settings (kiln.settings).
- **Set the time only once the world runs.** The world steps for a few seconds while the save loads, then the load sets
  the saved time and overwrites anything set before. Wait for the game log line
  `Switching state from LoadEcsScene to Run` (`Context.GameLog.Line`) when your mod loads with the server.
- `SyncedCounter == ChangeCounter` means the server has put the change into its ServerInfo message to clients.

## C (native plugins)
```c
const KilnTimeApi* t = api->service(KilnStr{"kiln.time", 9}, 1);   // in on_ready; NULL = unavailable
KilnTimeState s;  if (t && t->get(&s) == KILN_OK) { /* s.hours ... */ }
t->set_time_of_day(13.5, KILN_TIME_SET_FORWARD);                  // tick thread
t->reset_weather();                                               // tick thread
```
Errors: `KILN_ERR_UNAVAILABLE` (no world yet / build lacks it), `KILN_ERR_WRONG_THREAD`, `KILN_ERR_INVALID_ARGUMENT`
(NaN, unknown mode).

## For Kiln.Admin
`Time.State` gives `{hours, clock, isDay, dayLength, nightLength}`; a set request maps to `Time.Set(hours)` (or
`Time.Set(text)` for "HH:MM"), inside the tick thread. The command registry can expose it to chat as well, e.g.
`Commands.Register("time", ..., CommandPermission.Admin)` (see chat-test's `/kt-time`).

## Client checklist
1. Join the lab, run `/kt-time 22:30` (admin user group) or have a mod call `Time.Set(22.5)`: the sky turns to night
   within a second, without a reconnect.
2. `/kt-time 6 game` then `Time.ResetWeather()`: clouds/rain disappear and new weather arrives later.
3. Stop and start the server: the time is where it was at the last save (autosave every 30 s, and on shutdown).

## In a game client (hosted worlds)
- kiln.time binds the **server world's** `g38_daytime::Daytime` and `WeatherSystem` singletons at the first step of
  each server-world session (`time: server world ...: Daytime ... (setTimeOfDay saw ...: same)`). A client's own world
  has a Daytime of its own; `setTimeOfDay` calls on it are ignored (logged once). On the dedicated server the singletons
  are the same objects the hooks saw before (checked in the lab), so nothing changes there.
- Weather works on the client too (`client.d/time.json`: the client's WeatherSystem update is a larger function; the
  layer layout is the same). Only a WeatherSystem of mode 0 that is the server world's own singleton is used.
- When the game closes the server world, the Daytime, WeatherSystem, GameServer and a pending sync check are released;
  the next session binds its own.
