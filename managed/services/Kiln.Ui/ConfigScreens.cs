// SPDX-License-Identifier: MIT
// The Kiln menu's config screens (Forge: the config GUI): one page per mod, generated from each config's schema
// (Kiln.Api's config registry). Numbers with a range get a slider, bools a checkbox, enums a picker, text / durations /
// lists a text field. A slider has a number field beside it: a value is typed there (a wide range is hard to hit by
// dragging). Edits are staged on the page and saved with "Apply" (validated and written by ConfigFile.TrySet
// on the tick thread; until that ran, the page keeps showing what was applied, also while the game is paused and no
// tick comes). Client configs are the player's; a server config is read-only while connected to a server, and
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
    // edits "Apply" handed to the tick thread that are not saved yet: the page shows them. The entry itself holds the old
    // value until the tick ran: a frame or two in a running world, as long as the game is paused in the pause menu
    private static readonly Dictionary<(string Id, string Path), object> Pending = new();
    // what stands in a slider row's number field while it is no value of the entry (half typed, outside the range)
    private static readonly Dictionary<(string Id, string Path), string> Typed = new();
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
            RunCommits();
        });
    }

    /// <summary>Saves what "Apply" queued (the tick thread; the tests call it as the tick would).</summary>
    internal static void RunCommits()
    {
        while (Commits.TryDequeue(out var c)) Commit(c.File, c.Edits);
    }

    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Staged.Clear();
            Errors.Clear();
            Pending.Clear();
            Typed.Clear();
        }
        Commits.Clear();
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
            int staged, pending;
            lock (Lock)
            {
                staged = Staged.Keys.Count(k => k.Id == f.Id);
                pending = Pending.Keys.Count(k => k.Id == f.Id);
            }
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
            // applied, and no tick has come to save it (the main menu, a paused game): the table shows the new values
            if (staged == 0 && pending > 0 && !TickRunning)
                ui.TextDisabled(pending == 1 ? "1 change applied (saved when a world runs)" : $"{pending} changes applied (saved when a world runs)");
            ui.Spacing();
        }
    }

    private static void Entry(UiDraw ui, ConfigFile f, ConfigEntry e, bool readOnly)
    {
        using var _ = ui.Id(e.Path);
        var key = (f.Id, e.Path);
        object? staged, pending;
        string? error, typed;
        lock (Lock)
        {
            Staged.TryGetValue(key, out staged);
            Pending.TryGetValue(key, out pending);
            Errors.TryGetValue(key, out error);
            Typed.TryGetValue(key, out typed);
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
        var shown = staged ?? pending ?? Display(e, e.Value);
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
                NumberField(ui, f, e, typed, i.ToString(CultureInfo.InvariantCulture));
                break;
            }
            case "float" when e.Min is { } lo && e.Max is { } hi:
            {
                var d = shown is double v ? v : Convert.ToDouble(shown, CultureInfo.InvariantCulture);
                var s = (float)d;
                if (ui.Slider("##v", ref s, (float)lo, (float)hi))
                {
                    d = Math.Round((double)s, 4);
                    Stage(f, e.Path, d);
                }
                NumberField(ui, f, e, typed, d.ToString("R", CultureInfo.InvariantCulture));
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
                // the field shows such a value cut and read-only (UiDraw.InputText): say why
                if (!UiDraw.Editable(text, 512))
                    ui.TextDisabled(text.Contains('\0') ? "Read-only here: it holds a NUL character. Change it in the config file."
                        : "Read-only here: longer than 512 bytes. Change it in the config file.");
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

    /// <summary>The number field beside a slider: the value as text, to type one (a slider over a wide range is hard to
    /// set by dragging). A text that is a number of the entry's range is staged as it is typed; any other stays in the
    /// field with the range under it until it is one or the field is left, and the entry's value shows again.</summary>
    private static void NumberField(UiDraw ui, ConfigFile f, ConfigEntry e, string? typed, string current)
    {
        if (!ui.HasLayout) return;  // a kiln.ui that cannot size the field: the slider alone, as before
        var key = (f.Id, e.Path);
        ui.SameLine();
        ui.NextItemWidth(-1);       // the rest of the cell beside the slider
        var text = typed ?? current;
        if (ui.InputText("##n", ref text, 32))
        {
            if (TryNumber(e, text, out var value))
            {
                Stage(f, e.Path, value);
                lock (Lock) Typed.Remove(key);
            }
            else lock (Lock) Typed[key] = text;
        }
        else if (typed is not null && !ui.LastItem.Active)
        {
            lock (Lock) Typed.Remove(key);
        }
        ui.Tooltip("Type the value here.");
        if (typed is not null)
            ui.TextDisabled($"a number from {e.Min?.ToString(CultureInfo.InvariantCulture)} to {e.Max?.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>What a text typed into a slider row's number field means: the value to stage (an int for an "int" entry,
    /// a double for a "float" one). False while it is no number of the entry's range: half typed, or outside it (the
    /// config refuses a value outside its range, it does not clamp). A comma counts as the decimal point when the text
    /// has no point ("2,5").</summary>
    internal static bool TryNumber(ConfigEntry e, string text, out object value)
    {
        value = 0;
        if (e.Min is not { } lo || e.Max is not { } hi) return false;
        var t = text.Trim();
        if (e.Type == "int")
        {
            if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i) || i < lo || i > hi) return false;
            value = i;
            return true;
        }
        if (!t.Contains('.')) t = t.Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d) || d < lo || d > hi) return false;
        value = d;
        return true;
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

    internal static void Stage(ConfigFile f, string path, object value)
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
            foreach (var k in Typed.Keys.Where(k => k.Id == f.Id).ToList()) Typed.Remove(k);
        }
    }

    private static void Apply(ConfigFile f)
    {
        (string, object)[] edits;
        lock (Lock)
        {
            edits = Staged.Where(kv => kv.Key.Id == f.Id).Select(kv => (kv.Key.Path, kv.Value)).ToArray();
            foreach (var (p, v) in edits)
            {
                Staged.Remove((f.Id, p));
                Pending[(f.Id, p)] = v;  // shown until the tick thread saved it (Commit)
            }
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
            bool saved;
            string error;
            try { saved = f.TrySet(path, v, out error); }
            catch (Exception x)
            {
                // nothing may leave an applied value on the page for ever: an edit that threw is a refused one
                saved = false;
                error = x.Message;
            }
            var key = (f.Id, path);
            lock (Lock)
            {
                // from here the entry's own value is what the page shows again (a newer Apply of this entry waits on)
                if (Pending.TryGetValue(key, out var p) && ReferenceEquals(p, value)) Pending.Remove(key);
                // refused: back on the page to fix, unless the player has changed the entry again since
                if (!saved && !Staged.ContainsKey(key))
                {
                    Errors[key] = error;
                    Staged[key] = value;
                }
            }
            if (saved) ok++;
            else failed.Add($"{path}: {error}");
        }
        Backend.Current.Log(0, failed.Count > 0 ? LogLevel.Warn : LogLevel.Info,
            $"ui: config {f.Id}: {ok} change(s) saved from the Kiln menu{(failed.Count > 0 ? "; refused: " + string.Join("; ", failed) : "")}");
        if (failed.Count > 0) Hud.Toast($"{f.Id}: {failed[0]}", ToastKind.Error, 5);
        else Hud.Toast($"{f.Id}: saved", ToastKind.Success, 2);
    }
}
