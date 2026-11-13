// SPDX-License-Identifier: MIT
// chat-test, kiln.time v2 (0.2.0-dev.27): choosing the weather on a lab server. The game's weather table (names from its
// data), a timed phenomenon that runs its time and then hands back to the game's own roll, a hold that survives the
// game's rolls until released, a release that lets the game roll at once, "none" on every layer, the refusals, and
// WeatherChangedEvent for Kiln's changes and the game's. The clock runs 20 times faster meanwhile (weather is timed on
// the day/night clock), and is given back at the end.
// Restart (opt-in, <data>/weather.arm): the run ends by leaving a two-hour snowy rain and a hold; the next run checks that
// neither came back (the game rolls its weather anew after a load; Kiln does not restore it).
// What a player sees is the guided check time.weather in mods/tests/client-pass (Session1.cs).
using System.Text.Json;
using Kiln;

namespace ChatTest;

public sealed partial class ChatTestMod
{
    private readonly List<WeatherChangedEvent> _weatherEvents = new();

    private static WeatherLayer LayerOf(int i) => Time.Weather[i];

    private async Task<bool> WaitFor(Func<bool> done, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            if (done()) return true;
            await Ticks(1);
        }
        return done();
    }

    private async Task WeatherChecks()
    {
        if (!await WaitFor(() => Time.WeatherChoiceAvailable, 2450))
        {
            Check("weather.available", false, $"capability {Capabilities.Why("time.weather_choose")}; the server's weather did not run");
            return;
        }
        var table = Time.WeatherPhenomena;
        Log.Info($"INFO weather table: {string.Join(", ", table.Select(p => p.ToString()))}");
        Check("weather.table", table.Count > 0 && table.Any(p => p.Name.StartsWith("clouds_", StringComparison.Ordinal) && p.Layer == 0) &&
              table.Any(p => p.Name == "rain" && p.Effect == WeatherEffect.Rain && p.IsRolled),
            $"{table.Count} phenomena from the game's data, {table.Count(p => p.IsRolled)} rolled, {table.Count(p => !p.IsRolled)} in no layer ({string.Join(", ", table.Where(p => !p.IsRolled).Select(p => p.Name))})");
        Log.Info($"INFO weather now: {string.Join(" | ", Time.Weather)}");

        var persist = Path.Combine(Context.DataDirectory, "weather.json");
        if (File.Exists(persist))
        {
            await WeatherAfterRestart(persist);
            return;
        }
        using var sub = Events.On<WeatherChangedEvent>(e => _weatherEvents.Add(e));
        var speed = Time.HeldSpeed;
        Time.SetSpeed(20);  // a game minute in three real seconds: the game's own rolls come quickly
        try
        {
            await TimedAndBack();
            await HoldAndRelease();
            await NoneEverywhere();
            await OrderAndQueue();
            WeatherRefusals();
        }
        finally
        {
            Time.SetSpeed(speed);
        }
        if (File.Exists(Path.Combine(Context.DataDirectory, "weather.arm"))) await ArmWeatherRestart(persist);
    }

    // A timed choice: overcast on the clouds layer for 30 s of game time, then the game rolls by itself (its roll never
    // repeats the running phenomenon, so the clouds change).
    private async Task TimedAndBack()
    {
        var before = LayerOf(0).Current;
        _weatherEvents.Clear();
        var target = before?.Name == "clouds_overcast" ? "clouds_fluffy" : "clouds_overcast";
        var r = await Time.SetWeather(target, TimeSpan.FromSeconds(30), layer: 0);
        var l0 = LayerOf(0);
        // the layer is rolled again 30 s after the start (Kiln adds the phenomenon's fade-out to the game's override)
        Check("weather.set.timed", r.Ok && l0.Current?.Name == target && !l0.Held && l0.Remaining > TimeSpan.FromSeconds(20) &&
              l0.Remaining <= TimeSpan.FromSeconds(31), $"{r}; layer 0 now {l0}");
        Check("weather.event.kiln", await WaitFor(() => _weatherEvents.Any(e => e.Layer == 0 && e.ByKiln && e.After?.Name == target), 10),
            $"WeatherChangedEvent [{string.Join("; ", _weatherEvents)}]");
        _weatherEvents.Clear();
        var back = await WaitFor(() => LayerOf(0).Current?.Name != target, 25 * 30);
        var after = LayerOf(0);
        Check("weather.timed.back", back && after.Current is { IsNothing: false } && !after.Held,
            $"after 30 s of game time the game rolled layer 0 itself: {target} -> {after.Current} ({after})");
        Check("weather.event.game", await WaitFor(() => _weatherEvents.Any(e => e.Layer == 0 && !e.ByKiln), 10),
            $"WeatherChangedEvent for the game's roll [{string.Join("; ", _weatherEvents)}]");
    }

    // A hold: rain on one region until released, kept through the game's own rolls; a release with `now` lets the game
    // roll at once (its roll skips the running phenomenon, so a region with one rain gets nothing).
    private async Task HoldAndRelease()
    {
        var rainy = Time.Weather.FirstOrDefault(l => l.Index > 0 && l.Choices.Count(c => !c.IsNothing) == 1 && l.Choices.Any(c => c.Name == "rain"));
        if (rainy == null)
        {
            Check("weather.hold", false, "no region with one rain");
            return;
        }
        var i = rainy.Index;
        var r = await Time.SetWeather("rain", layer: i);
        var l = LayerOf(i);
        Check("weather.hold.set", r.Ok && l.Current?.Name == "rain" && l.Held && l.HeldPhenomenon?.Name == "rain", $"{r}");
        // wait out the first run (2 to 6 game minutes: 6 to 18 s at 20x): the roll shows as the time left jumping up (a
        // new run's own duration; it need not be longer than the first one's) and as a restart event of the held rain
        var firstEnds = l.Remaining;
        var last = firstEnds;
        _weatherEvents.Clear();
        var rolled = await WaitFor(() =>
        {
            var now = LayerOf(i);
            var up = now.Remaining > last + TimeSpan.FromSeconds(5);
            last = now.Remaining;
            return up || now.Current?.Name != "rain";
        }, 25 * 40);
        await Ticks(2);
        var again = LayerOf(i);
        var restart = _weatherEvents.FirstOrDefault(e => e.Layer == i && e.ByKiln && e.Held && e.Before?.Name == "rain" && e.After?.Name == "rain");
        Check("weather.hold.kept", rolled && again.Current?.Name == "rain" && again.Held && restart != null,
            $"the game's roll came ({firstEnds.TotalSeconds:0} s left before) and Kiln kept rain: {again}; event: {restart?.ToString() ?? "none"}");
        _weatherEvents.Clear();
        var rel = await Time.ReleaseWeather(i, now: true);
        await Ticks(2);
        var free = LayerOf(i);
        Check("weather.release", rel.Ok && !free.Held && free.Current is { IsNothing: true },
            $"released with now: the game rolled at once and picked {free.Current} ({free})");
    }

    private async Task NoneEverywhere()
    {
        var r = await Time.SetWeather("none", TimeSpan.FromMinutes(10));
        var all = Time.Weather;
        Check("weather.none", r.Ok && r.Layers.Count == all.Count && all.All(l => l.Current is { IsNothing: true }),
            $"{r.Layers.Count} layers set to nothing: {string.Join(" | ", all)}");
        var back = await Time.ReleaseWeather(now: true);
        await Ticks(2);
        Check("weather.release.all", back.Ok && Time.Weather.All(l => !l.Held), $"{back}");
    }

    // Every change is one event in order, a restart included; a request that does not fit the queue is a refused result
    // with nothing of it queued; readiness answers off the tick thread.
    private async Task OrderAndQueue()
    {
        Check("weather.ready.anythread", await Task.Run(() => Time.WeatherChoiceAvailable), "Time.WeatherChoiceAvailable read on a worker thread");
        var choices = Time.Weather[0].Choices.Where(p => !p.IsNothing).Select(p => p.Name).Distinct().ToList();
        if (choices.Count < 2)
        {
            Check("weather.event.order", false, $"layer 0 has fewer than two phenomena: {string.Join(", ", choices)}");
            return;
        }
        var (a, b) = (choices[0], choices[1]);
        var start = await Time.SetWeather(a, TimeSpan.FromMinutes(10), layer: 0);
        await Ticks(2);
        _weatherEvents.Clear();
        var both = await Task.WhenAll(Time.SetWeather(b, TimeSpan.FromMinutes(10), layer: 0), Time.SetWeather(a, TimeSpan.FromMinutes(10), layer: 0));
        await Ticks(2);
        var mine = _weatherEvents.Where(e => e.Layer == 0 && e.ByKiln).ToList();
        Check("weather.event.order", start.Ok && both.All(r => r.Ok) && mine.Count == 2 && mine[0].Before?.Name == a && mine[0].After?.Name == b &&
                                     mine[1].Before?.Name == b && mine[1].After?.Name == a,
            $"{a}, then {b} and {a} again in one step: [{string.Join("; ", mine)}]");
        _weatherEvents.Clear();
        var again = await Time.SetWeather(a, TimeSpan.FromMinutes(10), layer: 0);
        await Ticks(2);
        var restart = _weatherEvents.Where(e => e.Layer == 0 && e.ByKiln).ToList();
        Check("weather.event.restart", again.Ok && restart.Count == 1 && restart[0].Before?.Name == a && restart[0].After?.Name == a,
            $"{a} started again while it runs (a new instance): [{string.Join("; ", restart)}]");

        // a release with now, then a timed choice on the same layer in one step: the choice is Kiln's, and the game's roll at
        // its end is the game's own (the release's pending roll was superseded)
        _weatherEvents.Clear();
        var released = Time.ReleaseWeather(0, now: true);
        var timed = Time.SetWeather(b, TimeSpan.FromSeconds(30), layer: 0);
        await Task.WhenAll(released, timed);
        await WaitFor(() => _weatherEvents.Count(e => e.Layer == 0) >= 2, 25 * 20);
        var seq = _weatherEvents.Where(e => e.Layer == 0).ToList();
        Check("weather.event.superseded", timed.Result.Ok && seq.Count >= 2 && seq[0].ByKiln && seq[0].After?.Name == b && !seq[1].ByKiln,
            $"release now, then {b} for 30 s in one step, then the game's roll: [{string.Join("; ", seq)}]");

        // "none" on every layer, again and again in one step: what fits runs, the next is a refused result, nothing half done
        var layers = Time.Weather.Count;
        var accepted = new List<Task<WeatherResult>>();
        WeatherResult? refused = null;
        Exception? thrown = null;
        try
        {
            for (var i = 0; i < 300 / layers + 2 && refused == null; i++)
            {
                var t = Time.SetWeather("none", TimeSpan.FromMinutes(10));
                if (t.IsCompleted && !t.Result.Ok) refused = t.Result;
                else accepted.Add(t);
            }
        }
        catch (Exception e)
        {
            thrown = e;
        }
        var results = await Task.WhenAll(accepted);
        Check("weather.queue.full", thrown == null && refused is { Ok: false } && refused.Reason!.Contains("nothing of it was queued") &&
                                    accepted.Count == 256 / layers && results.All(r => r.Ok),
            $"{accepted.Count} requests of {layers} layers ran, then: {refused?.Reason ?? thrown?.Message ?? "no refusal"}");
        var back = await Time.ReleaseWeather(now: true);
        await Ticks(2);
        Check("weather.queue.after", back.Ok && Time.Weather.All(l => !l.Held), $"released after the full queue: {back}");
    }

    private void WeatherRefusals()
    {
        Check("weather.refused.name", Throws<ArgumentException>(() => _ = Time.SetWeather("snowstorm")), "an unknown name is a bad argument");
        var debug = Time.WeatherPhenomena.FirstOrDefault(p => !p.IsRolled);
        if (debug != null)
            Check("weather.refused.debug", Throws<ArgumentException>(() => _ = Time.SetWeather(debug)), $"{debug} is in no layer's roll");
        Check("weather.refused.layer", Throws<ArgumentOutOfRangeException>(() => _ = Time.SetWeather("rain", layer: 99)), "layer 99");
        Check("weather.refused.duration", Throws<ArgumentOutOfRangeException>(() => _ = Time.SetWeather("rain", TimeSpan.FromDays(40))), "40 days");
    }

    private static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T) { return true; }
        catch { return false; }
        return false;
    }

    // ---- across a restart ----
    private async Task ArmWeatherRestart(string persist)
    {
        File.Delete(Path.Combine(Context.DataDirectory, "weather.arm"));
        var snowy = Time.Weather.FirstOrDefault(l => l.Choices.Any(c => c.Name == "rain_snow"));
        var rainy = Time.Weather.FirstOrDefault(l => l.Index > 0 && l.Index != snowy?.Index && l.Choices.Any(c => c.Name == "rain"));
        if (snowy == null || rainy == null)
        {
            Check("weather.persist.arm", false, "no snowy and rainy region");
            return;
        }
        var a = await Time.SetWeather("rain_snow", TimeSpan.FromHours(2), layer: snowy.Index);
        var b = await Time.SetWeather("rain", layer: rainy.Index);  // a hold: Kiln's, gone after a restart
        var s = LayerOf(snowy.Index);
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(persist, JsonSerializer.Serialize(new Dictionary<string, double>
        {
            ["snowy"] = snowy.Index, ["rainy"] = rainy.Index, ["remaining"] = s.Remaining.TotalSeconds,
        }));
        Check("weather.persist.arm", a.Ok && b.Ok, $"left {s} and a hold on layer {rainy.Index}; wait for an autosave (30 s), stop and start the server");
    }

    private async Task WeatherAfterRestart(string persist)
    {
        var rec = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(persist))!;
        File.Delete(persist);
        var snowy = LayerOf((int)rec["snowy"]);
        var rainy = LayerOf((int)rec["rainy"]);
        Check("weather.persist.timed", snowy.Remaining < TimeSpan.FromSeconds(rec["remaining"] - 3600),
            $"after the load the layer runs the game's weather, not the two hours Kiln left: {snowy} (had {rec["remaining"]:0} s left at the set)");
        Check("weather.persist.hold", !rainy.Held, $"Kiln's hold ended with the process: {rainy}");
        var r = await Time.ReleaseWeather((int)rec["snowy"], now: true);
        Check("weather.persist.cleanup", r.Ok, $"{r}");
    }
}
