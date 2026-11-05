// SPDX-License-Identifier: MIT
// The Kiln menu's config screens (Forge: the config GUI): one page per mod, generated from each config's schema
// (Kiln.Api's config registry). Numbers with a range get a slider, bools a checkbox, enums a picker, text / durations /
// lists a text field. Edits are staged on the page and saved with "Apply" (validated and written by ConfigFile.TrySet
// on the tick thread). Client configs are the player's; a server config is read-only while connected to a server, and
// entries that hold the server's value (config sync, [Synced]) are read-only too. docs/kiln/sync.md, docs/kiln/ui.md.
using System.Collections.Concurrent;
using System.Globalization;

namespace Kiln;

internal static class ConfigScreens
{
    private static readonly object Lock = new();
    // staged edits (render thread): (config id, path) -> value (bool, int, double, enum name, or text)
    private static readonly Dictionary<(string Id, string Path), object> Staged = new();
    private static readonly Dictionary<(string Id, string Path), string> Errors = new();
    private static readonly ConcurrentQueue<(ConfigFile File, (string Path, object Value)[] Edits)> Commits = new();
    private static int _started;
    private static volatile bool _ticking;
    private static DateTime _lastTick;

    private static void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        ServiceTick.Add("kiln config screens", static (in TickInfo _) =>
        {
            _lastTick = DateTime.UtcNow;
            _ticking = true;
            while (Commits.TryDequeue(out var c)) Commit(c.File, c.Edits);
        });
    }

    /// <summary>The mod has configs to show.</summary>
    public static bool HasConfigs(string modId)
    {
        try { return Config.All.Any(f => f.ModId == modId); }
        catch (Exception) { return false; }
    }

    /// <summary>Why a config cannot be edited here now (null: it can).</summary>
    private static string? ReadOnly(ConfigFile f)
    {
        if (f.Kind != ConfigKind.Server) return null;
        if (Client.IsClient && Client.IsRemote)
            return Network.ServerHasKiln
                ? "Server config: read-only while connected to a server. Synced entries show the server's values."
                : "Server config: read-only while connected to a server (it applies when you host).";
        return null;
    }

    private static bool TickRunning => _ticking && (DateTime.UtcNow - _lastTick).TotalSeconds < 2;

    // ---- drawing (render thread) ----

    public static void Draw(UiDraw ui, string modId)
    {
        EnsureStarted();
        IReadOnlyList<ConfigFile> files;
        try { files = Config.All.Where(f => f.ModId == modId).ToArray(); }
        catch (Exception) { files = []; }
        foreach (var f in files)
        {
            using var _ = ui.Id("cfg." + f.Id);
            var kind = f.Kind.ToString().ToLowerInvariant();
            if (!ui.CollapsingHeader($"{f.Id} ({kind})", defaultOpen: true)) continue;
            ui.TextDisabled(Path.GetFileName(f.FilePath));
            ui.Tooltip(f.FilePath);
            var ro = ReadOnly(f);
            if (ro is not null) ui.Text(Color.Yellow, ro);
            if (f.ServerValues.Count > 0)
                ui.Text(Color.Cyan, $"{f.ServerValues.Count} entr{(f.ServerValues.Count == 1 ? "y holds" : "ies hold")} the server's value while connected (read-only).");
            if (f.LastError is { } err) ui.Text(Color.Red, "file: " + err);
            foreach (var w in f.Warnings.Take(3)) ui.Text(Color.Yellow, w);
            string? section = null;
            IReadOnlyList<ConfigEntry> entries;
            try { entries = f.Entries; }
            catch (Exception) { entries = []; }
            if (ui.BeginTable("entries", "Entry", "Value", ""))
            {
                foreach (var e in entries)
                {
                    var dot = e.Path.LastIndexOf('.');
                    var sec = dot > 0 ? e.Path[..dot] : null;
                    if (sec != section)
                    {
                        section = sec;
                        if (sec is not null)
                        {
                            ui.NextRow();
                            ui.NextColumn();
                            ui.Text(Color.Orange, sec);
                        }
                    }
                    Entry(ui, f, e, ro is not null || e.FromServer);
                }
                ui.EndTable();
            }
            int staged;
            lock (Lock) staged = Staged.Keys.Count(k => k.Id == f.Id);
            if (staged > 0)
            {
                if (ui.Button($"Apply ({staged})")) Apply(f);
                ui.SameLine();
                if (ui.Button("Undo")) Undo(f);
                if (!TickRunning)
                {
                    ui.SameLine();
                    ui.TextDisabled("(saved when a world runs)");
                }
            }
            else if (ro is null && ui.SmallButton("All to defaults"))
            {
                foreach (var e in entries)
                    if (!e.FromServer && !ConfigSchemaEquals(e.Value, e.Default) && e.Default is not null)
                        Stage(f, e.Path, Display(e, e.Default));
            }
            ui.Spacing();
        }
    }

    private static void Entry(UiDraw ui, ConfigFile f, ConfigEntry e, bool readOnly)
    {
        using var _ = ui.Id(e.Path);
        var key = (f.Id, e.Path);
        object? staged;
        string? error;
        lock (Lock)
        {
            Staged.TryGetValue(key, out staged);
            Errors.TryGetValue(key, out error);
        }
        ui.NextRow();
        ui.NextColumn();
        var name = e.Path[(e.Path.LastIndexOf('.') + 1)..];
        var marks = (e.FromServer ? " [server]" : "") + (e.Synced ? " (synced)" : "") + (e.RequiresRestart ? " (restart)" : "");
        if (staged is not null) ui.Text(Color.Yellow, "* " + name + marks);
        else if (e.FromServer) ui.Text(Color.Cyan, name + marks);
        else ui.Text(name + marks);
        ui.Tooltip(Describe(e));
        ui.NextColumn();
        if (readOnly)
        {
            ui.TextDisabled(Show(e, e.Value));
            if (e.FromServer) ui.Tooltip($"The server's value. Yours: {Show(e, e.FileValue)} (back when you leave).");
            ui.NextColumn();
            return;
        }
        var shown = staged ?? Display(e, e.Value);
        switch (e.Type)
        {
            case "bool":
            {
                var b = shown is bool v && v;
                if (ui.Checkbox("##v", ref b)) Stage(f, e.Path, b);
                break;
            }
            case "int" when e.Min is { } lo && e.Max is { } hi && hi - lo <= 100_000:
            {
                var i = shown is int v ? v : Convert.ToInt32(shown, CultureInfo.InvariantCulture);
                if (ui.Slider("##v", ref i, (int)Math.Ceiling(lo), (int)Math.Floor(hi))) Stage(f, e.Path, i);
                break;
            }
            case "float" when e.Min is { } lo && e.Max is { } hi:
            {
                var d = (float)(shown is double v ? v : Convert.ToDouble(shown, CultureInfo.InvariantCulture));
                if (ui.Slider("##v", ref d, (float)lo, (float)hi)) Stage(f, e.Path, Math.Round((double)d, 4));
                break;
            }
            case "enum" when e.Options.Count > 0:
            {
                var cur = shown as string ?? e.Options[0];
                var idx = Math.Max(0, e.Options.ToList().FindIndex(o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase)));
                if (ui.SmallButton("<")) Stage(f, e.Path, e.Options[(idx + e.Options.Count - 1) % e.Options.Count]);
                ui.SameLine();
                ui.Text(cur);
                ui.SameLine();
                if (ui.SmallButton(">")) Stage(f, e.Path, e.Options[(idx + 1) % e.Options.Count]);
                ui.Tooltip(string.Join(", ", e.Options));
                break;
            }
            default:
            {
                var text = shown as string ?? Show(e, shown);
                if (ui.InputText("##v", ref text, 512)) Stage(f, e.Path, text);
                ui.Tooltip(e.Type switch
                {
                    "duration" => "a duration: 30s, 5m, 1h30m",
                    var t when t.StartsWith("list") => "a list: [1, 2, 3] or 1, 2, 3",
                    _ => e.Type,
                });
                break;
            }
        }
        if (error is not null) ui.Text(Color.Red, error);
        ui.NextColumn();
        if (staged is not null || (!ConfigSchemaEquals(e.Value, e.Default) && e.Default is not null))
        {
            if (ui.SmallButton("default")) Stage(f, e.Path, Display(e, e.Default));
            ui.Tooltip($"Default: {Show(e, e.Default)}");
        }
    }

    private static string Describe(ConfigEntry e)
    {
        var lines = new List<string>();
        if (e.Comment is { Length: > 0 } c) lines.Add(c);
        lines.Add($"{e.Path}: {e.Type}, default {Show(e, e.Default)}" +
                  (e.Min is not null || e.Max is not null ? $", range {e.Min?.ToString(CultureInfo.InvariantCulture) ?? "-inf"}..{e.Max?.ToString(CultureInfo.InvariantCulture) ?? "inf"}" : ""));
        if (e.Options.Count > 0) lines.Add("options: " + string.Join(", ", e.Options));
        if (e.Synced) lines.Add("Synced: a server sends its value to clients.");
        if (e.RequiresRestart) lines.Add("Requires a restart: a change applies when the mod loads again.");
        if (e.FromServer) lines.Add("Holds the server's value while connected.");
        return string.Join("\n", lines);
    }

    /// <summary>What a widget edits for a value: bool, int, double, enum name, or text (durations, lists, strings).</summary>
    private static object Display(ConfigEntry e, object? v) => e.Type switch
    {
        "bool" => v is true,
        "int" when e.Min is not null && e.Max is not null && e.Max - e.Min <= 100_000 => Convert.ToInt32(v ?? 0, CultureInfo.InvariantCulture),
        "float" when e.Min is not null && e.Max is not null => Convert.ToDouble(v ?? 0.0, CultureInfo.InvariantCulture),
        "enum" => v?.ToString() ?? "",
        _ => Show(e, v),
    };

    private static string Show(ConfigEntry e, object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        TimeSpan t => FormatDuration(t),
        double d => d.ToString("G6", CultureInfo.InvariantCulture),
        float x => x.ToString("G6", CultureInfo.InvariantCulture),
        string s when e.Type == "string" => s,
        System.Collections.IEnumerable seq when v is not string => "[" + string.Join(", ", seq.Cast<object?>().Select(x => x switch
        {
            TimeSpan t => FormatDuration(t),
            string s => "\"" + s + "\"",
            IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
            _ => x?.ToString() ?? "",
        })) + "]",
        IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static string FormatDuration(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "0s";
        var parts = new List<string>();
        if (t.Days > 0) parts.Add($"{t.Days}d");
        if (t.Hours > 0) parts.Add($"{t.Hours}h");
        if (t.Minutes > 0) parts.Add($"{t.Minutes}m");
        if (t.Seconds > 0 || t.Milliseconds > 0) parts.Add(t.Milliseconds > 0 ? $"{t.Seconds + t.Milliseconds / 1000.0:0.###}s" : $"{t.Seconds}s");
        return string.Concat(parts);
    }

    private static bool ConfigSchemaEquals(object? a, object? b) => ConfigSchema.ValueEquals(a, b);

    private static void Stage(ConfigFile f, string path, object value)
    {
        lock (Lock)
        {
            Staged[(f.Id, path)] = value;
            Errors.Remove((f.Id, path));
        }
    }

    private static void Undo(ConfigFile f)
    {
        lock (Lock)
        {
            foreach (var k in Staged.Keys.Where(k => k.Id == f.Id).ToList()) Staged.Remove(k);
            foreach (var k in Errors.Keys.Where(k => k.Id == f.Id).ToList()) Errors.Remove(k);
        }
    }

    private static void Apply(ConfigFile f)
    {
        (string, object)[] edits;
        lock (Lock)
        {
            edits = Staged.Where(kv => kv.Key.Id == f.Id).Select(kv => (kv.Key.Path, kv.Value)).ToArray();
            foreach (var (p, _) in edits) Staged.Remove((f.Id, p));
        }
        if (edits.Length > 0) Commits.Enqueue((f, edits));
    }

    // ---- saving (tick thread) ----

    private static void Commit(ConfigFile f, (string Path, object Value)[] edits)
    {
        var ok = 0;
        var failed = new List<string>();
        foreach (var (path, value) in edits)
        {
            var v = value is string s ? s : value;
            if (f.TrySet(path, v, out var error)) ok++;
            else
            {
                failed.Add($"{path}: {error}");
                lock (Lock)
                {
                    Errors[(f.Id, path)] = error;
                    Staged[(f.Id, path)] = value;  // keep it on the page to fix
                }
            }
        }
        Backend.Current.Log(0, failed.Count > 0 ? LogLevel.Warn : LogLevel.Info,
            $"ui: config {f.Id}: {ok} change(s) saved from the Kiln menu{(failed.Count > 0 ? "; refused: " + string.Join("; ", failed) : "")}");
        if (failed.Count > 0) Hud.Toast($"{f.Id}: {failed[0]}", ToastKind.Error, 5);
        else Hud.Toast($"{f.Id}: saved", ToastKind.Success, 2);
    }
}
