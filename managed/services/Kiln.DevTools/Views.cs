// SPDX-License-Identifier: MIT
// View-models of the DevTools windows that match Shrouded Engine Tweaks' overlay (docs/re/shrouded-engine-tweaks.md):
// Bindings, Resources, Hooks, Game Log, Status and Console. They hold no drawing code, so tests run them headless.
// Cost rule (the dev.18 frame-rate bug came from per-frame scans): a view gathers data only in Refresh, and
// PanelScheduler calls Refresh on the tick thread only while the view's window is open, at most every RefreshMs.
// The draw callback only reads the last published snapshot.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Severity of a row, drawn green/white/yellow/red.</summary>
internal enum RowLevel { Ok, Info, Warn, Error }

/// <summary>SET's filter syntax: comma-separated words, any of which matches ("a,b"); "-word" excludes. Case is ignored.</summary>
internal static class TextFilter
{
    public static bool Matches(string text, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        bool anyPositive = false, hit = false;
        foreach (var raw in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length > 1 && raw[0] == '-')
            {
                if (text.Contains(raw[1..], StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            anyPositive = true;
            if (text.Contains(raw, StringComparison.OrdinalIgnoreCase)) hit = true;
        }
        return !anyPositive || hit;
    }
}

/// <summary>A view behind one window. Refresh runs on the tick thread; draw code reads the published fields only.</summary>
internal abstract class DevView
{
    /// <summary>Least time between two refreshes while open.</summary>
    public virtual int RefreshMs => 1000;
    internal long NextDue;
    internal bool WasOpen;
    internal int Refreshes;
    /// <summary>Gathers the data (tick thread, window open).</summary>
    public abstract void Refresh();
    /// <summary>The window closed: drop anything large.</summary>
    public virtual void Closed() { }
}

/// <summary>Calls <see cref="DevView.Refresh"/> for open views when they are due; nothing at all for closed ones.</summary>
internal static class PanelScheduler
{
    public static int Pump(IEnumerable<(DevView View, bool Open)> views, long nowMs)
    {
        var n = 0;
        foreach (var (v, open) in views)
        {
            if (!open)
            {
                if (v.WasOpen) { v.WasOpen = false; v.Closed(); }
                continue;
            }
            if (!v.WasOpen) { v.WasOpen = true; v.NextDue = 0; }
            if (nowMs < v.NextDue) continue;
            v.NextDue = nowMs + v.RefreshMs;
            try { v.Refresh(); v.Refreshes++; n++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                        or NotSupportedException or TypeLoadException or FileNotFoundException)
            {
                KilnLog.Warn($"devtools: {v.GetType().Name}: {e.Message}");
            }
        }
        return n;
    }
}

// ---------------------------------------------------------------------------------------------------- Bindings ----
internal sealed record BindingRow(string Category, string Id, string Key, string Default, string Description, bool Enabled,
    bool Changed, string? Conflict);

internal sealed class BindingsView : DevView
{
    public volatile IReadOnlyList<BindingRow> Rows = [];
    public override void Refresh() =>
        Rows = Build(KeyBindings.All.Select(b => (b.Category, b.Id, b.Key, b.Default, b.Description, b.Enabled)));

    /// <summary>Rows sorted by category then id; <c>Conflict</c> names the other bindings on the same key.</summary>
    public static IReadOnlyList<BindingRow> Build(IEnumerable<(string Category, string Id, KeyChord Key, KeyChord Default, string Description, bool Enabled)> all)
    {
        var list = all.ToList();
        var byKey = list.Where(b => !b.Key.IsNone).GroupBy(b => b.Key).ToDictionary(g => g.Key, g => g.Select(b => b.Id).ToList());
        return list.OrderBy(b => b.Category, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b =>
            {
                string? conflict = null;
                if (!b.Key.IsNone && byKey[b.Key].Count > 1) conflict = string.Join(", ", byKey[b.Key].Where(i => i != b.Id));
                return new BindingRow(b.Category, b.Id, b.Key.ToString(), b.Default.ToString(), b.Description, b.Enabled,
                    b.Key != b.Default, conflict);
            }).ToList();
    }

    /// <summary>Rebinds (tick thread). "None" unbinds; "default" resets.</summary>
    public static string Rebind(string id, string key)
    {
        if (key.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            if (KeyBindings.Find(id) == null) return $"no key binding '{id}'";
            KeyBindings.Reset(id);
            return $"{id} reset to {KeyBindings.Find(id)?.Key}";
        }
        if (!KeyChord.TryParse(key, out var chord)) return $"unknown key '{key}' (examples: F8, Ctrl+K, None)";
        return KeyBindings.Rebind(id, chord) ? $"{id} = {chord}" : $"no key binding '{id}' (saved for when it registers)";
    }

    public static string Describe(IEnumerable<BindingRow> rows, string filter = "")
    {
        var sb = new StringBuilder();
        var shown = rows.Where(r => TextFilter.Matches($"{r.Category} {r.Id} {r.Key} {r.Description}", filter)).ToList();
        sb.Append(shown.Count).Append(" key binding(s)");
        foreach (var r in shown)
            sb.Append('\n').Append($"[{r.Category}] {r.Id} = {r.Key}{(r.Changed ? $" (default {r.Default})" : "")}{(r.Enabled ? "" : " disabled")}{(r.Conflict != null ? " CONFLICT with " + r.Conflict : "")}");
        return sb.ToString();
    }
}

// --------------------------------------------------------------------------------------------------- Resources ----
internal sealed record ResourceRow(Guid Guid, uint Part)
{
    public string Label => Part == 0 ? Guid.ToString() : $"{Guid} #{Part}";
}

/// <summary>Resources of one type, by type name or hash. The detail text comes from <see cref="Detail"/>.</summary>
internal sealed class ResourcesView : DevView
{
    /// <summary>The detail seam. Raw by default (size and the first bytes); WP-06's reflected view replaces it, e.g.
    /// <c>ResourcesView.Detail = k =&gt; Assets.Reflect(k)?.Describe() ?? ResourcesView.RawDetail(k);</c></summary>
    public static Func<AssetKey, string> Detail = RawDetail;
    /// <summary>Reads the resource list for a type hash (tests replace it).</summary>
    internal static Func<uint, AssetKey[]> List = Assets.ListResources;

    public override int RefreshMs => 250; // only acts on requests; the list itself is read once per type
    private volatile string? _wantedType;
    private volatile ResourceRow? _wantedDetail;
    public volatile string TypeLabel = "";
    public volatile uint TypeHash;
    public volatile IReadOnlyList<ResourceRow> Rows = [];
    public volatile IReadOnlyList<string> Suggestions = [];
    public volatile string DetailText = "";
    public volatile string Message = "Type a resource type name (e.g. keen::ItemInfo) or a hash (0x...), then List.";
    private string? _suggestFor;

    /// <summary>Asks for a type's list at the next refresh (any thread).</summary>
    public void Request(string type) => _wantedType = type.Trim();
    public void Select(ResourceRow row) => _wantedDetail = row;
    /// <summary>Type-name suggestions for the text typed so far, gathered at the next refresh.</summary>
    internal volatile string SuggestText = "";

    public override void Refresh()
    {
        var s = SuggestText;
        if (s != _suggestFor)
        {
            _suggestFor = s;
            Suggestions = s.Length >= 3 && GameTypes.Available ? GameTypes.Search(s).Take(12).Select(t => t.QualifiedName).ToList() : [];
        }
        if (Interlocked.Exchange(ref _wantedType, null) is { } want) Load(want);
        if (Interlocked.Exchange(ref _wantedDetail, null) is { } row)
            DetailText = Detail(new AssetKey(row.Guid, TypeHash, row.Part));
    }

    internal void Load(string type)
    {
        if (!TryHash(type, out var hash)) { Message = $"'{type}' is not a type name or hash"; return; }
        if (!Assets.Available && List == Assets.ListResources) { Message = "kiln.assets is unavailable"; return; }
        var keys = List(hash);
        TypeHash = hash;
        TypeLabel = type;
        Rows = keys.Select(k => new ResourceRow(k.Guid, k.Part)).OrderBy(r => r.Guid).ThenBy(r => r.Part).ToList();
        DetailText = "";
        Message = $"{keys.Length} resource(s) of {type} (0x{hash:x8})";
    }

    public static bool TryHash(string type, out uint hash)
    {
        hash = 0;
        if (string.IsNullOrWhiteSpace(type)) return false;
        if (type.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(type.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
        hash = Assets.TypeHash(type);
        return true;
    }

    public static string RawDetail(AssetKey key)
    {
        var bytes = Assets.ReadResource(key);
        if (bytes == null) return $"{key.Guid}: not readable";
        var sb = new StringBuilder($"{key.Guid} part {key.Part}: {bytes.Length} B");
        for (var i = 0; i < Math.Min(bytes.Length, 256); i += 16)
        {
            sb.Append('\n').Append(i.ToString("x4", CultureInfo.InvariantCulture)).Append("  ");
            sb.Append(Convert.ToHexString(bytes, i, Math.Min(16, bytes.Length - i)));
        }
        return sb.ToString();
    }

    public override void Closed() { Rows = []; DetailText = ""; }
}

// ------------------------------------------------------------------------------------------------------- Hooks ----
internal sealed record HookRates(double DetourCallsPerSecond, double MidCallsPerSecond);

internal sealed class HooksView : DevView
{
    public volatile string Text = "";
    private HooksStats? _last;
    private long _lastMs;
    internal static Func<HooksStats> Read = Hooks.Stats;
    internal static Func<bool> IsAvailable = () => Hooks.Available;

    public override void Refresh()
    {
        if (!IsAvailable()) { Text = "kiln.hooks is unavailable on this build"; return; }
        var now = Environment.TickCount64;
        var cur = Read();
        var rates = _last is { } prev ? Rates(prev, cur, (now - _lastMs) / 1000.0) : null;
        _last = cur;
        _lastMs = now;
        Text = Describe(cur, rates);
    }

    public static HookRates Rates(HooksStats prev, HooksStats cur, double seconds)
    {
        if (seconds <= 0) return new HookRates(0, 0);
        return new HookRates((cur.DetourCalls >= prev.DetourCalls ? cur.DetourCalls - prev.DetourCalls : 0) / seconds,
            (cur.MidCalls >= prev.MidCalls ? cur.MidCalls - prev.MidCalls : 0) / seconds);
    }

    public static string Describe(HooksStats s, HookRates? r) => string.Create(CultureInfo.InvariantCulture,
        $"detour targets {s.DetourTargets}, mid targets {s.MidTargets}, handlers {s.Handlers}, patches {s.Patches}\n" +
        $"detour calls {s.DetourCalls}{(r != null ? $" ({r.DetourCallsPerSecond:F1}/s)" : "")}\n" +
        $"mid calls {s.MidCalls}{(r != null ? $" ({r.MidCallsPerSecond:F1}/s)" : "")}");

    public override void Closed() => _last = null;
}

// ---------------------------------------------------------------------------------------------------- Game log ----
internal readonly record struct LogLine(RowLevel Level, string Text);

/// <summary>Follows the game's own log file (enshrouded.log on a client, logs/enshrouded_server.log on a server)
/// from where it was when the window opened, minus a 64 KiB tail. Keeps 5000 lines.</summary>
internal sealed class GameLogView : DevView
{
    public const int MaxLines = 5000;
    private const int TailBytes = 64 * 1024;
    public override int RefreshMs => 500;
    private readonly LinkedList<LogLine> _lines = new();
    private readonly StringBuilder _partial = new();
    private long _offset = -1;
    public volatile LogLine[] Lines = [];
    public volatile string Path = "";
    internal string? PathOverride;
    /// <summary>Hide the engine's state-machine chatter (SET does the same).</summary>
    public static readonly string[] Noise = ["StateMachine", "state machine", "changed state"];

    public static string? FindLog(string gameRoot)
    {
        foreach (var rel in new[] { "enshrouded.log", System.IO.Path.Combine("logs", "enshrouded_server.log"), "enshrouded_server.log" })
        {
            var p = System.IO.Path.Combine(gameRoot, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public override void Refresh()
    {
        var path = PathOverride ?? FindLog(KilnPaths.Server);
        if (path == null) { Path = "no game log found under " + KilnPaths.Server; return; }
        Path = path;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (_offset < 0 || fs.Length < _offset) _offset = Math.Max(0, fs.Length - TailBytes);
        if (fs.Length == _offset) return;
        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[Math.Min(fs.Length - _offset, 1 << 20)];
        var n = fs.Read(buf, 0, buf.Length);
        _offset += n;
        Append(Encoding.UTF8.GetString(buf, 0, n));
    }

    /// <summary>Adds text; only whole lines are kept, the rest waits for the next chunk.</summary>
    public void Append(string chunk)
    {
        _partial.Append(chunk);
        var s = _partial.ToString();
        var last = s.LastIndexOf('\n');
        if (last < 0) return;
        _partial.Clear().Append(s, last + 1, s.Length - last - 1);
        foreach (var raw in s[..last].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            _lines.AddLast(new LogLine(Classify(line), line));
            if (_lines.Count > MaxLines) _lines.RemoveFirst();
        }
        Lines = [.. _lines];
    }

    public static RowLevel Classify(string line)
    {
        if (line.StartsWith("[E ", StringComparison.Ordinal) || line.Contains("[E]", StringComparison.Ordinal) || line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)) return RowLevel.Error;
        if (line.StartsWith("[W ", StringComparison.Ordinal) || line.Contains("[W]", StringComparison.Ordinal) || line.Contains("warn", StringComparison.OrdinalIgnoreCase)) return RowLevel.Warn;
        return RowLevel.Info;
    }

    public static IEnumerable<LogLine> Filtered(IEnumerable<LogLine> lines, string filter, bool hideNoise) =>
        lines.Where(l => (!hideNoise || !Noise.Any(n => l.Text.Contains(n, StringComparison.OrdinalIgnoreCase)))
                         && TextFilter.Matches(l.Text, filter));

    public void Clear() { _lines.Clear(); Lines = []; }
    public override void Closed() { _lines.Clear(); _partial.Clear(); Lines = []; _offset = -1; }
}

// ------------------------------------------------------------------------------------------------------ Status ----
internal sealed record StatusFile(string Name, DateTime Written, RowLevel Level, IReadOnlyList<(RowLevel Level, string Text)> Lines);

/// <summary>Reads kiln/state/*.json and rates each file green/yellow/red (SET's Status page).</summary>
internal sealed class StatusView : DevView
{
    public override int RefreshMs => 2000;
    public volatile IReadOnlyList<StatusFile> Files = [];
    internal string? DirOverride;
    public string Dir => DirOverride ?? KilnPaths.State;

    public override void Refresh()
    {
        if (!Directory.Exists(Dir)) { Files = []; return; }
        var list = new List<StatusFile>();
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json").Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(f);
            if (info.Length > 4 << 20) { list.Add(new StatusFile(info.Name, info.LastWriteTimeUtc, RowLevel.Info, [(RowLevel.Info, $"{info.Length} B: too large to show")])); continue; }
            JsonNode? doc;
            try { doc = JsonNode.Parse(File.ReadAllText(f)); }
            catch (JsonException e) { list.Add(new StatusFile(info.Name, info.LastWriteTimeUtc, RowLevel.Warn, [(RowLevel.Warn, "not valid JSON: " + e.Message)])); continue; }
            list.Add(Analyze(info.Name, info.LastWriteTimeUtc, doc));
        }
        Files = list;
    }

    /// <summary>Top-level scalars as lines, plus issues: non-empty "errors" (red), non-null "error" (red),
    /// "verified": false (red), null "signatures" entries (yellow), mods that failed or faulted (yellow/red).</summary>
    public static StatusFile Analyze(string name, DateTime written, JsonNode? doc)
    {
        var lines = new List<(RowLevel, string)>();
        if (doc is not JsonObject root) return new StatusFile(name, written, RowLevel.Info, [(RowLevel.Info, doc?.ToJsonString() ?? "null")]);
        Walk(root, "", lines, 0);
        foreach (var (k, v) in root)
            if (v is JsonValue && v.GetValueKind() is not (JsonValueKind.Object or JsonValueKind.Array))
                lines.Add((RowLevel.Info, $"{k}: {v}"));
        var level = lines.Count == 0 ? RowLevel.Ok : lines.Max(l => l.Item1) == RowLevel.Info ? RowLevel.Ok : lines.Max(l => l.Item1);
        return new StatusFile(name, written, level, lines.OrderByDescending(l => l.Item1).ToList());
    }

    private static void Walk(JsonObject o, string path, List<(RowLevel, string)> lines, int depth)
    {
        if (depth > 4) return;
        foreach (var (k, v) in o)
        {
            var p = path.Length == 0 ? k : path + "." + k;
            switch (k)
            {
                case "errors" when v is JsonArray { Count: > 0 } a:
                    foreach (var e in a.Take(10)) lines.Add((RowLevel.Error, $"{p}: {Short(e)}"));
                    continue;
                case "error" when v is JsonValue ev && ev.GetValueKind() == JsonValueKind.String:
                    lines.Add((RowLevel.Error, $"{p}: {ev}"));
                    continue;
                case "verified" when v is JsonValue vv && vv.GetValueKind() == JsonValueKind.False:
                    lines.Add((RowLevel.Error, $"{p}: false"));
                    continue;
                case "signatures" when v is JsonObject sigs:
                    var missing = sigs.Where(s => s.Value == null).Select(s => s.Key).ToList();
                    if (missing.Count > 0)
                        lines.Add((RowLevel.Warn, $"{p}: {missing.Count} of {sigs.Count} not found ({string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", ..." : "")})"));
                    continue;
                case "mods" when v is JsonArray mods:
                    foreach (var m in mods.OfType<JsonObject>())
                    {
                        var state = m["state"]?.ToString() ?? "";
                        var faults = m["faults"] is JsonValue fv && fv.TryGetValue<int>(out var fi) ? fi : 0;
                        var id = m["id"]?.ToString() ?? "?";
                        if (state is "failed" or "faulted" or "error") lines.Add((RowLevel.Error, $"mod {id}: {state} {m["reason"]}"));
                        else if (faults > 0) lines.Add((RowLevel.Warn, $"mod {id}: {faults} fault(s)"));
                    }
                    continue;
            }
            if (v is JsonObject child) Walk(child, p, lines, depth + 1);
        }
    }

    private static string Short(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "null";
        return s.Length > 200 ? s[..200] + "..." : s;
    }
}

// ----------------------------------------------------------------------------------------------------- Console ----
/// <summary>Runs lines through the console folder (kiln/control/console/&lt;name&gt;.cmd -&gt; .result.json, the same
/// path as <c>kilnctl cmd</c>), so permissions and the host-console rules are shared. Keeps 2000 output lines and
/// the last 100 entered lines.</summary>
internal sealed class ConsoleView : DevView
{
    public const int MaxLines = 2000, MaxHistory = 100;
    public override int RefreshMs => 250;
    internal string? DirOverride;
    public string Dir => DirOverride ?? System.IO.Path.Combine(KilnPaths.Control, "console");
    private readonly List<string> _pending = new();
    private readonly LinkedList<LogLine> _out = new();
    private readonly List<string> _history = new();
    private static int _seq;
    public volatile LogLine[] Output = [];
    public volatile string[] History = [];

    /// <summary>Writes one line as a command file (tick thread).</summary>
    public string Submit(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return "";
        _history.Remove(line);
        _history.Add(line);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        History = [.. _history];
        Directory.CreateDirectory(Dir);
        var name = $"devtools-{Environment.ProcessId}-{Interlocked.Increment(ref _seq)}";
        var cmd = System.IO.Path.Combine(Dir, name + ".cmd");
        File.WriteAllText(cmd + ".tmp", line + "\n");
        File.Move(cmd + ".tmp", cmd, overwrite: true);
        _pending.Add(name);
        Add(RowLevel.Info, "> " + line);
        return name;
    }

    public override void Refresh()
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            var result = System.IO.Path.Combine(Dir, _pending[i] + ".result.json");
            if (!File.Exists(result)) continue;
            string text;
            try { text = File.ReadAllText(result); }
            catch (IOException) { continue; } // still being written
            foreach (var l in ParseResult(text)) Add(l.Level, l.Text);
            try { File.Delete(result); } catch (IOException) { }
            _pending.RemoveAt(i--);
        }
    }

    /// <summary>A result file as output lines: each command's output white, errors red, still-running yellow.</summary>
    public static List<LogLine> ParseResult(string json)
    {
        var lines = new List<LogLine>();
        JsonNode? doc;
        try { doc = JsonNode.Parse(json); }
        catch (JsonException e) { lines.Add(new LogLine(RowLevel.Error, "unreadable result: " + e.Message)); return lines; }
        if (doc?["results"] is not JsonArray results) { lines.Add(new LogLine(RowLevel.Warn, doc?.ToJsonString() ?? "empty result")); return lines; }
        foreach (var r in results.OfType<JsonObject>())
        {
            if (r["output"] is JsonArray output)
                foreach (var o in output) lines.Add(new LogLine(RowLevel.Info, o?.ToString() ?? ""));
            if (r["running"]?.GetValueKind() == JsonValueKind.True) lines.Add(new LogLine(RowLevel.Warn, "(still running)"));
            if (r["error"] is JsonValue e && e.GetValueKind() == JsonValueKind.String) lines.Add(new LogLine(RowLevel.Error, e.ToString()));
            else if (r["ok"]?.GetValueKind() == JsonValueKind.False && r["output"] is null) lines.Add(new LogLine(RowLevel.Error, "failed"));
        }
        return lines;
    }

    private void Add(RowLevel level, string text)
    {
        foreach (var l in text.Split('\n'))
        {
            _out.AddLast(new LogLine(level, l));
            if (_out.Count > MaxLines) _out.RemoveFirst();
        }
        Output = [.. _out];
    }

    public void Clear() { _out.Clear(); Output = []; }
    internal int Pending => _pending.Count;
}
