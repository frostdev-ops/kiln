// SPDX-License-Identifier: MIT
// kiln.settings lab test. Prints "PASS <name>: ..." / "FAIL <name>: ..." lines and a SUMMARY line to kiln.log.
// Everything it changes is restored at the end (live values and enshrouded_server.json, byte for byte).
// Player-free: the observable effects it checks are the game's own day/night clock (it takes new durations from the
// settings block every step) and the game's send flag (SendServerInfo sends the block and clears the flag).
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace SettingsTest;

public sealed class SettingsTestMod : Mod
{
    private int _pass, _fail;
    private bool _noReset;
    private readonly CancellationTokenSource _stop = new();

    public override void OnLoad()
    {
        Log.Info($"settings-test: loaded (kiln.settings available: {Settings.Available})");
        _ = RunAsync();
    }

    public override void OnUnload() => _stop.Cancel();

    private void Pass(string name, string detail) { _pass++; Log.Info($"PASS {name}: {detail}"); }
    private void Fail(string name, string detail) { _fail++; Log.Error($"FAIL {name}: {detail}"); }
    private void Check(bool ok, string name, string detail) { if (ok) Pass(name, detail); else Fail(name, detail); }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop.IsCancellationRequested; i++) await Context.Scheduler.NextTick();
    }

    private async Task RunAsync()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Fail("crash", e.ToString());
        }
        finally
        {
            if (!_noReset)
                try { Settings.ResetAll(); } catch { /* best effort */ }
            Log.Info($"SUMMARY settings-test: {_pass} PASS, {_fail} FAIL");
        }
    }

    private async Task Run()
    {
        if (!Settings.Available)
        {
            Fail("available", "kiln.settings is not loaded");
            return;
        }
        var wait = Stopwatch.StartNew();
        while (!Settings.Ready && wait.Elapsed < TimeSpan.FromSeconds(30)) await Steps(1);
        Check(Settings.Ready, "ready", $"live settings found after {wait.ElapsedMilliseconds} ms");
        if (!Settings.Ready) return;
        // Not ready during OnLoad (first world step) is expected; one more step settles the first resend counters.
        await Steps(2);

        var status = Settings.Status;
        Check(status.Count >= 38, "catalog", $"{status.Count} settings from the game's reflection");
        Check(status.VerifiedSingleton, "singleton", "the instance's block is the world's ecs::GameSettings singleton");
        Check(status.ResendsToPlayers, "resend.known", "the game's send flag is known on this build");
        Log.Info($"settings-test: status {status}");

        var all = Settings.All;
        foreach (var s in all)
            Log.Info($"settings-test: {s.Name} = {Show(s.Value)} [{s.Kind}, {s.Group}, {s.Flags}, range {s.Min}..{s.Max}, " +
                     $"default {Show(s.Default)}] {s.Effect}");

        if (await PersistCheck(status.ConfigPath)) return;  // second boot of the restart check: verified and restored
        CompareWithConfig(all, status.ConfigPath, status.Preset);
        await LiveFactors();
        await Kinds();
        Refusals();
        await SaveRoundTrip(status.ConfigPath);
        await DayNight();
        await ResetAll(all);
        if (File.Exists(Path.Combine(Context.DataDirectory, "arm-restart"))) PersistArm(status.ConfigPath);
    }

    // ---- 8. survives a restart: arm (touch kiln/data/settings-test/arm-restart), restart the server, check ----
    private string Marker => Path.Combine(Context.DataDirectory, "restart-check.json");

    private void PersistArm(string path)
    {
        var original = File.ReadAllBytes(path);
        Settings.Set("enemyDamageFactor", 1.5);
        Settings.Set("weatherFrequency", "Rare");
        Settings.Set("nightTimeDuration", TimeSpan.FromMinutes(7));
        var msg = Settings.Save();
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Marker, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["original"] = System.Convert.ToBase64String(original),
            ["expect"] = new Dictionary<string, object> { ["enemyDamageFactor"] = 1.5, ["weatherFrequency"] = "Rare", ["nightTimeDuration"] = 420000000000L },
        }));
        File.Delete(Path.Combine(Context.DataDirectory, "arm-restart"));
        Settings.ResetAll();
        Log.Info($"settings-test: restart check armed ({msg}); restart the server to finish it");
    }

    private async Task<bool> PersistCheck(string path)
    {
        if (!File.Exists(Marker)) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(Marker));
        var expect = doc.RootElement.GetProperty("expect");
        var diffs = new List<string>();
        foreach (var p in expect.EnumerateObject())
        {
            var now = Settings.Get(p.Name);
            var ok = now switch
            {
                double d => Math.Abs(d - p.Value.GetDouble()) < 1e-6,
                string s => s == p.Value.GetString(),
                long l => l == p.Value.GetInt64(),
                _ => false,
            };
            if (!ok) diffs.Add($"{p.Name} = {Show(now)}, saved {p.Value}");
        }
        Check(diffs.Count == 0, "save.restart",
            "after a restart the server started with the values Save() wrote" + (diffs.Count > 0 ? ": " + string.Join("; ", diffs) : ""));
        var original = System.Convert.FromBase64String(doc.RootElement.GetProperty("original").GetString()!);
        File.WriteAllBytes(path, original);
        File.Delete(Marker);
        try { File.Delete(path + ".kiln-backup"); } catch { /* best effort */ }
        using var orig = JsonDocument.Parse(original);
        var gs = orig.RootElement.GetProperty("gameSettings");
        foreach (var p in expect.EnumerateObject()) Settings.Set(p.Name, gs.GetProperty(p.Name));  // the world's values before
        _noReset = true;
        Log.Info("settings-test: restart check done; enshrouded_server.json restored byte for byte, live values set back");
        await Steps(1);
        return true;
    }

    // ---- 1. everything the game holds == what enshrouded_server.json says (preset Custom) ----
    private void CompareWithConfig(IReadOnlyList<SettingInfo> all, string path, string? preset)
    {
        JsonElement gs;
        string cfgPreset;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            cfgPreset = doc.RootElement.TryGetProperty("gameSettingsPreset", out var p) ? p.GetString() ?? "" : "";
            gs = doc.RootElement.GetProperty("gameSettings").Clone();
        }
        catch (Exception e)
        {
            Fail("config.read", $"{path}: {e.Message}");
            return;
        }
        Check(string.Equals(cfgPreset, preset, StringComparison.OrdinalIgnoreCase), "config.preset",
            $"file preset {cfgPreset}, server started with {preset}");
        if (!string.Equals(cfgPreset, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("settings-test: preset is not Custom: the game uses the preset's values, not the file's; compare skipped");
            return;
        }
        int same = 0, missing = 0;
        var diffs = new List<string>();
        foreach (var s in all)
        {
            if (!gs.TryGetProperty(s.Name, out var j))
            {
                missing++;
                diffs.Add($"{s.Name} not in the file");
                continue;
            }
            if (Same(s, j)) same++;
            else diffs.Add($"{s.Name}: game {Show(s.Value)} vs file {j}");
        }
        var extra = gs.EnumerateObject().Select(p => p.Name).Where(n => all.All(s => !string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        Check(diffs.Count == 0 && extra.Count == 0, "config.compare",
            $"{same}/{all.Count} equal to {Path.GetFileName(path)}" + (diffs.Count > 0 ? "; " + string.Join("; ", diffs) : "") +
            (extra.Count > 0 ? "; keys the game does not have: " + string.Join(", ", extra) : ""));
    }

    private static bool Same(SettingInfo s, JsonElement j) => s.Kind switch
    {
        SettingKind.Bool => j.ValueKind is JsonValueKind.True or JsonValueKind.False && j.GetBoolean() == (bool)s.Value,
        SettingKind.Float => j.ValueKind == JsonValueKind.Number && Math.Abs(j.GetDouble() - (double)s.Value) < 1e-4,
        SettingKind.Enum => j.ValueKind == JsonValueKind.String && string.Equals(j.GetString(), s.Value as string, StringComparison.OrdinalIgnoreCase),
        _ => j.ValueKind == JsonValueKind.Number && j.TryGetInt64(out var l) && l == (long)s.Value,
    };

    // ---- 2. factors: set, read back, game memory, the game sends the block ----
    private async Task LiveFactors()
    {
        foreach (var (name, target) in new[] { ("enemyDamageFactor", 2.5), ("playerStaminaFactor", 2.0), ("bossHealthFactor", 0.5) })
        {
            var before = Settings.Status;
            var info = Settings.Info(name);
            var want = Math.Abs((double)info.Value - target) < 1e-6 ? target / 2 : target;
            var change = Settings.Set(name, want);
            var get = (double)Settings.Get(name);
            var mem = (double)Settings.Info(name).Value; // native re-reads the live block
            Check(Math.Abs(get - want) < 1e-6 && Math.Abs(mem - want) < 1e-6 && change.Changed, $"set.{name}",
                $"{Show(change.Before)} -> {Show(change.After)}; Get {get}, block {mem}");
            Check(Settings.Status.ResendsRequested == before.ResendsRequested + 1, $"resend.requested.{name}",
                $"send flag set ({Settings.Status.ResendsRequested} requests)");
            var sw = Stopwatch.StartNew();
            while (Settings.Status.ResendsSent <= before.ResendsSent && sw.ElapsedMilliseconds < 3000) await Steps(1);
            Check(Settings.Status.ResendsSent > before.ResendsSent, $"resend.sent.{name}",
                $"the game sent the block to the players and cleared its flag after {sw.ElapsedMilliseconds} ms");
            var back = Settings.Set(name, info.Value);
            Check(Math.Abs((double)Settings.Get(name) - (double)info.Value) < 1e-6, $"restore.{name}",
                $"{Show(back.Before)} -> {Show(back.After)}");
            await Steps(2);
        }
    }

    // ---- 3. day/night: the game's clock takes the new durations, and time runs at the new speed ----
    private async Task DayNight()
    {
        var st = Settings.Status;
        if (st.DayDuration is null)
        {
            Fail("daytime.probe", "the day/night clock is not readable on this build");
            return;
        }
        // Kiln (and mods) start during the world's load; the game's programs, server_update_daytime among them, only run
        // once the scene is in Run. Wait until the clock moves.
        var wait = Stopwatch.StartNew();
        var first = st.TimeOfDay;
        while (Settings.Status.TimeOfDay == first && wait.Elapsed < TimeSpan.FromSeconds(120)) await Steps(5);
        Check(Settings.Status.TimeOfDay != first, "daytime.running",
            $"the game's day/night clock runs ({first:hh\\:mm\\:ss} -> {Settings.Status.TimeOfDay:hh\\:mm\\:ss} after {wait.Elapsed.TotalSeconds:F0} s)");
        await Steps(25);
        st = Settings.Status;
        var day0 = (long)Settings.Get("dayTimeDuration");
        var night0 = (long)Settings.Get("nightTimeDuration");
        Check(st.DayDuration!.Value.Ticks * 100 == day0 && st.NightDuration!.Value.Ticks * 100 == night0, "daytime.matches",
            $"clock uses day {st.DayDuration} / night {st.NightDuration}, settings say {day0 / 60e9} / {night0 / 60e9} min");

        var rate0 = await TodRate(3000);
        var changes0 = Settings.Status.DaytimeChanges;
        var two = TimeSpan.FromMinutes(2);
        Settings.Set("dayTimeDuration", two, resend: false);
        Settings.Set("nightTimeDuration", "2m");
        await Steps(3);
        var st1 = Settings.Status;
        Check(st1.DayDuration == two && st1.NightDuration == two && st1.DaytimeChanges > changes0, "daytime.applied",
            $"clock now day {st1.DayDuration} / night {st1.NightDuration}, re-anchored {st1.DaytimeChanges - changes0}x (live, no restart)");
        var rate1 = await TodRate(3000);
        var isDay = st1.TimeOfDay is { } tod && tod >= TimeSpan.FromHours(6) && tod < TimeSpan.FromHours(18);
        var expect = (isDay ? day0 : night0) / two.TotalNanoseconds();
        var ratio = rate1 / Math.Max(rate0, 1e-9);
        Check(rate0 > 0 && ratio > expect * 0.7 && ratio < expect * 1.3, "daytime.speed",
            $"{(isDay ? "day" : "night")} at {st1.TimeOfDay:hh\\:mm}: time of day ran {rate0:F1} -> {rate1:F1} game-s per s " +
            $"(x{ratio:F1}, expected x{expect:F1})");

        Settings.Set("dayTimeDuration", day0, resend: false);
        Settings.Set("nightTimeDuration", night0);
        await Steps(3);
        var st2 = Settings.Status;
        Check(st2.DayDuration!.Value.Ticks * 100 == day0 && st2.NightDuration!.Value.Ticks * 100 == night0, "daytime.restore",
            $"clock back to day {st2.DayDuration} / night {st2.NightDuration}");
    }

    // Game seconds of time-of-day per real second, measured over `ms`.
    private async Task<double> TodRate(int ms)
    {
        var t0 = Settings.Status.TimeOfDay!.Value;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) await Steps(1);
        var t1 = Settings.Status.TimeOfDay!.Value;
        var d = (t1 - t0).TotalSeconds;
        if (d < 0) d += 86400;
        return d / sw.Elapsed.TotalSeconds;
    }

    // ---- 4. other kinds: enum, bool, whole number ----
    private async Task Kinds()
    {
        foreach (var (name, value) in new (string, object)[]
                 {
                     ("weatherFrequency", "Rare"), ("tombstoneMode", "Everything"), ("randomSpawnerAmount", "Many"),
                     ("enableDurability", "off"), ("pacifyAllEnemies", true), ("flameLevelFogRemoval", 15),
                     ("fromHungerToStarving", "15m"), ("perkUpgradeRecyclingFactor", 0.3),
                 })
        {
            var info = Settings.Info(name);
            var change = Settings.Set(name, value);
            var now = Settings.Get(name);
            var ok = change.Changed || Equals(info.Value, now);
            var back = Settings.Set(name, info.Value);
            Check(ok && Equals(Settings.Get(name), info.Value), $"kind.{info.Kind}.{name}",
                $"{Show(change.Before)} -> {Show(now)} -> {Show(back.After)}");
        }
        await Steps(2);
    }

    // ---- 5. refusals: the documented ranges, unknown options, unknown names ----
    private void Refusals()
    {
        Expect("enemyDamageFactor", 10.0, "allowed 0.25 to 5");
        Expect("playerStaminaFactor", 0.1, "allowed 0.25 to 4");
        Expect("weatherFrequency", "Monsoon", "Disabled");
        Expect("dayTimeDuration", TimeSpan.FromMinutes(90), "2 to 60 minutes");
        Expect("flameLevelFogRemoval", 21, "allowed 0 to 20");
        Expect("enableDurability", 0.5, "");
        try
        {
            Settings.Set("noSuchSetting", 1);
            Fail("refuse.unknown", "accepted an unknown name");
        }
        catch (KeyNotFoundException e)
        {
            Pass("refuse.unknown", e.Message);
        }
        var ok = Settings.TrySet("enemyDamageFactor", 6.0, out _, out var err, anyValue: true);
        Check(ok && Math.Abs((double)Settings.Get("enemyDamageFactor") - 6.0) < 1e-6, "anyValue",
            $"6 accepted past the documented range with anyValue ({err})");
        Settings.Reset("enemyDamageFactor");
    }

    private void Expect(string name, object value, string fragment)
    {
        var before = Settings.Get(name);
        var ok = !Settings.TrySet(name, value, out _, out var error);
        Check(ok && error.Contains(fragment, StringComparison.Ordinal) && Equals(Settings.Get(name), before),
            $"refuse.{name}", $"{Show(value)} refused: \"{error}\"; value stays {Show(before)}");
    }

    // ---- 6. Save writes enshrouded_server.json and keeps everything else ----
    private async Task SaveRoundTrip(string path)
    {
        var original = File.ReadAllBytes(path);
        try
        {
            using var before = JsonDocument.Parse(original);
            var oldDamage = Settings.Get("enemyDamageFactor");
            Settings.Set("enemyDamageFactor", 1.75);
            Settings.Set("weatherFrequency", "Often");
            var msg = Settings.Save();
            using var after = JsonDocument.Parse(File.ReadAllBytes(path));
            var gs = after.RootElement.GetProperty("gameSettings");
            var keysBefore = before.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            var keysAfter = after.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            var groupsOk = after.RootElement.GetProperty("userGroups").GetArrayLength() ==
                           before.RootElement.GetProperty("userGroups").GetArrayLength() &&
                           after.RootElement.GetProperty("userGroups")[0].GetProperty("password").GetString() ==
                           before.RootElement.GetProperty("userGroups")[0].GetProperty("password").GetString();
            Check(Math.Abs(gs.GetProperty("enemyDamageFactor").GetDouble() - 1.75) < 1e-9 &&
                  gs.GetProperty("weatherFrequency").GetString() == "Often" &&
                  after.RootElement.GetProperty("gameSettingsPreset").GetString() == "Custom" &&
                  keysBefore.SequenceEqual(keysAfter) && groupsOk && File.Exists(path + ".kiln-backup"),
                "save", $"{msg}; keys kept in order, user groups and passwords kept, backup written");
            Settings.Set("enemyDamageFactor", oldDamage);
            Settings.Reset("weatherFrequency");
            var msg2 = Settings.Save();
            using var again = JsonDocument.Parse(File.ReadAllBytes(path));
            var gs2 = again.RootElement.GetProperty("gameSettings");
            var allSame = Settings.All.All(s => gs2.TryGetProperty(s.Name, out var j) && Same(s, j));
            Check(allSame, "save.again", $"{msg2}; every value in the file equals the live block");
        }
        finally
        {
            File.WriteAllBytes(path, original);
            try { File.Delete(path + ".kiln-backup"); } catch { /* best effort */ }
            Log.Info("settings-test: enshrouded_server.json restored byte for byte");
        }
        await Steps(1);
    }

    // ---- 7. reset: back to the world's own values ----
    private async Task ResetAll(IReadOnlyList<SettingInfo> initial)
    {
        Settings.Set("enemyHealthFactor", 3.0);
        Settings.Set("aggroPoolAmount", "Extreme");
        var n = Settings.ResetAll();
        await Steps(2);
        var now = Settings.All;
        var diffs = initial.Where(s => !Equals(s.Value, now[s.Index].Value)).Select(s => $"{s.Name} {Show(s.Value)} vs {Show(now[s.Index].Value)}").ToList();
        Check(n == 2 && diffs.Count == 0 && now.All(s => !s.Changed), "reset",
            $"{n} reset; every setting equals its value at the start" + (diffs.Count > 0 ? ": " + string.Join("; ", diffs) : ""));
    }

    private static string Show(object? v) => v switch
    {
        null => "-",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan t => $"{t.TotalMinutes:0.##} min",
        _ => v.ToString() ?? "",
    };
}

internal static class TimeSpanExt
{
    public static double TotalNanoseconds(this TimeSpan t) => t.Ticks * 100.0;
}
