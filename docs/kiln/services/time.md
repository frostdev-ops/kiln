# kiln.time: time of day and weather

The server world's day/night clock. A change reaches every client through the game's own ServerInfo sync and is
saved with the world (it survives a restart). Evidence and offsets: [docs/re/time.md](../../re/time.md).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_time.h` (`kiln.time` v2: v1 + the clock's speed + choosing the weather) |
| Native | `native/services/time/time.cpp` -> `kiln/services/kiln-time.dll` |
| Symbols | `sigdb/symbols/server.d/time.json` (features `time.read`, `time.set`, `time.weather`, `time.weather_choose`, `time.scale`, `time.sync_probe`) |
| Managed | `managed/services/Kiln.Time` -> `kiln/host/Kiln.Time.dll` (static class `Kiln.Time`) |
| Test | `mods/tests/chat-test` (`WeatherTest.cs`: choosing the weather), `mods/tests/creative-test` (the clock's speed) (PASS/FAIL lines in `kiln/logs/kiln.log`); `managed/tests/Kiln.Time.Tests` (offline); `native/services/time/test-time.sh` (host) |

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

### The clock's speed (day and night cycle)
```csharp
if (Time.CanSetSpeed)                     // feature time.scale on this build
{
    Time.SetCycle(TimeCycle.Stopped);     // the sun stands still (speed 0); sleeping doesn't move it
    Time.SetCycle(TimeCycle.Fast);        // a whole day and night in Time.FastCycle (90 s)
    Time.SetSpeed(4);                     // any speed 0..1000 (1 = the game's normal speed)
    Time.SetSpeed(null);                  // = SetCycle(TimeCycle.Normal): the game decides again
    double? held = Time.HeldSpeed;        // null while the game decides; Time.Cycle reads it as a TimeCycle
}
```
- While Kiln holds a speed, the game's own speed change (its sleep system asks for a faster clock while players sleep,
  and for 1 again after) is refused, and a world that loads or re-anchors gets the held speed back at the next step.
- The speed travels in the daytime anchor the server already syncs, so every client (vanilla too) follows; it is
  re-sent only when it changes.
- Session state: it ends when the world closes or a new world starts, including when the old world's stop event was missed (the save keeps the time of day, not the speed). Failed speed changes preserve the previous hold.
- `/creative cycle normal|stopped|fast` and the creative menu use it (docs/kiln/services/rules.md "Creative menu").

### Choosing the weather (since 0.2.0-dev.27)
The server's weather is a few layers (9 on game build 26430253): layer 0 is the clouds over the whole world, each of
the others a region (rain, or snowy rain in the cold one), with "nothing" between its phenomena. When a layer's time is
up the game rolls its next phenomenon by its table's weights. Kiln reads that table from the game's data and starts a
phenomenon through the game's own start of one (what its own weather states do), so the game picks its place in the
region, runs its fade-in and fade-out, and tells every player's game. Capability `time.weather_choose` (sigdb feature);
evidence: [docs/re/time.md](../../re/time.md), "Choosing the weather".

```csharp
IReadOnlyList<WeatherPhenomenon> all = Time.WeatherPhenomena;     // the game's table: clouds_few .. rain, rain_snow
foreach (WeatherLayer l in Time.Weather) Log.Info(l.ToString());  // "layer 0: clouds_overcast, next roll in 88 s"
await Time.SetWeather("rain", TimeSpan.FromMinutes(10));           // every region's rain, ten game minutes
await Time.SetWeather("clouds_overcast", layer: 0);              // held: Kiln picks it at each of the game's rolls
await Time.SetWeather("none");                                    // nothing on every layer: a clear sky (held)
WeatherResult r = await Time.ReleaseWeather(now: true);           // the game's weather again, rolled at once
Events.On<WeatherChangedEvent>(e => Log.Info(e.ToString()));      // the game's rolls and Kiln's changes
```

| Member | Side, thread | What |
|---|---|---|
| `Time.WeatherPhenomena` | `[ServerOnly][TickThreadOnly]` | the game's table: `Index`, `Name` (the game's debug name), `Guid`, `Layer` (-1: in no layer's roll, the game's debug entries), `Effect`, `Probability`, `MinDuration`, `MaxDuration`; empty until the weather ran once |
| `Time.Weather` | `[ServerOnly][TickThreadOnly]` | every layer: `Current` (a layer's "nothing" when nothing runs, null right after a reset), `Remaining` (game time to its next roll), `Held`, `HeldPhenomenon`, `Choices` |
| `Time.SetWeather(name, duration = null, layer = null)` | `[ServerOnly][TickThreadOnly]` | starts the phenomenon of that name on every layer that has one (or on `layer`); `"none"` is the layer's nothing. `duration`: that long (game time) until the game rolls the layer again, then the game's weather; null: held until released |
| `Time.SetWeather(WeatherPhenomenon, duration = null)` | same | one phenomenon on its own layer |
| `Time.ReleaseWeather(layer = null, now = false)` | same | Kiln's holds end; with `now` the layer's phenomenon ends and the game rolls at once, without it the running one finishes its time first |
| `WeatherResult` (`IKilnResult`) | value | `Ok`, `Reason`, `Layers` (as they are when it completes) |
| `WeatherChangedEvent` | `[ServerOnly]`, tick thread | `Layer`, `Before`, `After`, `ByKiln` (Kiln started or ended something there), `Held`; read once a step from the first subscription on |
| `Time.WeatherChoiceAvailable`, `Time.MaxWeatherDuration` (30 days) | any | |

- The one error rule: a request the game did not run is a result with `Ok == false` and a reason (the server world
  closed, its weather did not update for 10 s, the game's call faulted); a bad argument throws `ArgumentException` (an
  unknown name, a phenomenon in no layer's roll, a layer that is not there, a duration not between 1 s and 30 days); a
  capability that is off throws `KilnUnavailableException("time.weather_choose")` (an older Kiln, a game build whose
  weather table is not known, or the weather has not run yet in this world).
- It runs in the server's next weather update (the same frame as the next sim step, after it); the task completes in
  the step after.
- Weather is timed on the day/night clock (game time): a held clock speed (`Time.SetSpeed`) speeds it up too.
- A duration is the time until the game rolls the layer again; the phenomenon's fade-out follows while the next one
  fades in (the game rolls when a run starts to fade, so Kiln adds the phenomenon's despawn fade to the game's override).
- **Restart: it does not survive, by design of the game.** After a restart the layers run what the game rolls anew
  (lab: a two-hour snowy rain Kiln left before an autosave and a stop was "nothing" with 373 s to its next roll after
  the load; the load also restarts the game clock in its first cycle, docs/re/time.md "Persistence"). A hold is Kiln's
  and ends with the process too. Kiln does not restore either: a mod that wants its weather after a restart sets it
  again once the world runs.
- A rain is a cloud at a random place in its region, of a random size, drifting: choosing it does not put it over a
  player. Where it is is the game's; Kiln does not move it.
- The game's debug phenomena (`debug_rain`, `debug_blizzard`, `debug_dragon` on 26430253) lie in no layer's range and
  are not offered: the game never runs them, and what they do in a region was not tried.

## C (native plugins)
```c
const KilnTimeApi* t = api->service(KilnStr{"kiln.time", 9}, 1);   // in on_ready; NULL = unavailable
if (t && t->header.size >= offsetof(KilnTimeApi, set_time_scale)) {
    KilnTimeState s; if (t->get(&s) == KILN_OK) { /* s.hours ... */ }
    t->set_time_of_day(13.5, KILN_TIME_SET_FORWARD);              // tick thread
    t->reset_weather();                                         // tick thread
}
if (t && t->header.size >= offsetof(KilnTimeApi, weather_table) && t->set_time_scale && t->time_scale_hold &&
    t->time_scale_hold() > -1.5f)
    t->set_time_scale(0.0f);                                      // tick thread: stop the clock; -1 gives it back
if (t && t->header.size >= sizeof(KilnTimeApi)) {                 // v2: the weather (tick thread)
    KilnTimeWeatherLayer layers[16]; uint32_t n = 0;
    t->weather_layers(layers, 16, sizeof layers[0], &n);           // current, held, next_ns, now_ns per layer
    KilnTimeWeatherRequest r = { sizeof r, 0, 1, 4, 600000000000ll };  // layer 1, table index 4, ten game minutes
    t->weather_set(owner, &r, on_done, user, NULL);                // on_done(const KilnTimeWeatherResult*, user): next Pre
}
```
`weather_table` lists the game's phenomena (`KilnTimeWeatherPhenomenon`); a request's `phenomenon` must lie in the layer's
range (`first .. first + count - 1`) or be `KILN_TIME_WEATHER_NOTHING`; `KILN_TIME_WEATHER_HOLD` holds it,
`KILN_TIME_WEATHER_RELEASE` (+ `KILN_TIME_WEATHER_NOW`) hands a layer back.
Errors: `KILN_ERR_UNAVAILABLE` (no world yet / build lacks it), `KILN_ERR_WRONG_THREAD`, `KILN_ERR_INVALID_ARGUMENT`
(non-finite scale, scale above 1000, NaN time, unknown mode).

## For Kiln.Admin
`Time.State` gives `{hours, clock, isDay, dayLength, nightLength}`; a set request maps to `Time.Set(hours)` (or
`Time.Set(text)` for "HH:MM"), inside the tick thread. The command registry can expose it to chat as well, e.g.
`Commands.Register("time", ..., CommandPermission.Admin)` (see chat-test's `/kt-time`).

## Client checklist
1. Join the lab, run `/kt-time 22:30` (admin user group) or have a mod call `Time.Set(22.5)`: the sky turns to night
   within a second, without a reconnect.
2. `/kt-time 6 game` then `Time.ResetWeather()`: clouds/rain disappear and new weather arrives later.
3. Stop and start the server: the time is where it was at the last save (autosave every 30 s, and on shutdown).
4. The weather a mod chose: the guided check `time.weather` (mods/tests/client-pass, session 1).

## In a game client (hosted worlds)
- kiln.time binds the **server world's** `g38_daytime::Daytime` and `WeatherSystem` singletons at the first step of
  each server-world session (`time: server world ...: Daytime ... (setTimeOfDay saw ...: same)`). A client's own world
  has a Daytime of its own; `setTimeOfDay` calls on it are ignored (logged once). On the dedicated server the singletons
  are the same objects the hooks saw before (checked in the lab), so nothing changes there.
- Weather works on the client too (`client.d/time.json`: the client's WeatherSystem update is a larger function; the
  layer layout is the same). Only a WeatherSystem of mode 0 that is the server world's own singleton is used.
- When the game closes the server world, the Daytime, WeatherSystem, GameServer and a pending sync check are released;
  the next session binds its own.
