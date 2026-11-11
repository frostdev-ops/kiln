// SPDX-License-Identifier: MIT
// ShroudForge's mods in the Kiln menu: the switch and the settings of each ShroudForge mod, drawn in the Mods page's
// details column (ModManager.cs), and ShroudForge's menu shortcut when no ShroudForge Modloader window runs.
//
// Why here: ShroudForge's own "Modloader window" is a separate program (shroudforge.exe, a WebView2 window) that only
// ShroudForge's own install brings. Kiln's bundled ShroudForge runtime has no such program, and on a Steam Deck
// (gamescope) a separate window is not a reliable in-game interface. ShroudForge's runtime reads the mods' files about once
// a second and applies what changed, so writing those files (ShroudForge.cs) is all a menu needs.
//
// Threads: Draw runs on the render thread and never touches a file. Packages are read on a worker (at most once a
// second per mod while the page shows it); edits go to one writer thread, which waits until a slider rests.
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class ShroudForgeUi
{
    /// <summary>What a read or a save left of a package, published as one: the package (or why there is none), the page's
    /// view of it (made on the worker that read it) and a hash of the files it was read from.</summary>
    private sealed record State(SfPackage? Package, string? Error, View? View, string? Hash);

    private sealed class Entry
    {
        public volatile State Now = new(null, null, null, null);
        public SfPackage? Package => Now.Package;
        public string? Error => Now.Error;
        public long ReadAtMs;
        public int Reading;
        public int Building;
        /// <summary>Bumped by each save that publishes the package (under the entry's lock): a read that began before it
        /// is older than what the save published.</summary>
        public long Generation;
    }

    private static readonly ConcurrentDictionary<string, Entry> Packages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();
    // edits waiting for the writer: (folder, key; null = the switch, "" is a setting like any other) -> value and when to write it
    private static readonly Dictionary<(string Folder, string? Key), (JsonNode? Value, long DueMs)> Pending = new();
    private static readonly Dictionary<(string Folder, string? Key), string> Errors = new();
    // bumped (under Lock) whenever Pending or Errors change: the page's views of the packages are made again then
    private static long _version;
    // what the last write of each mod did, in words (shown under the switch)
    private static readonly Dictionary<string, (string Text, bool Ok)> Results = new(StringComparer.OrdinalIgnoreCase);
    private static readonly AutoResetEvent Wake = new(false);
    private static int _writer;
    private static (string Folder, string Action, long AtMs)? _armed;
    // text fields keep what the player types until they leave the field (with the frame the field was last drawn)
    private static readonly Dictionary<(string, string), (string Text, ulong Frame)> Typing = new();
    private static ulong _activeFrame;  // the last frame a setting's text field had the keyboard

    /// <summary>Slider and other quick edits are written once the value rests this long.</summary>
    internal const int SettleMs = 350;

    // the largest values a one-line and a multi-line field edit (UiDraw.InputText / InputMultiline clamp to these)
    private const int MaxLineBytes = 1 << 16, MaxAreaBytes = 1 << 20;
    // the most of one text (a label, a description, a value shown as text) the page draws per frame
    internal const int MaxDrawnText = 2000;

    /// <summary><paramref name="s"/>, or its first <paramref name="max"/> characters and "..." (never half a character).</summary>
    internal static string Clip(string s, int max)
    {
        if (s.Length <= max) return s;
        var n = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return string.Concat(s.AsSpan(0, n), "...");
    }

    // ------------------------------------------------------------------------------------- reading ----

    /// <summary>The package in <paramref name="folder"/> as last read (render thread: no file access). Starts a read on a
    /// worker when the last one is older than a second.</summary>
    public static SfPackage? Get(string folder, out string? error) => Current(folder, out error).Package;

    private static State Current(string folder, out string? error)
    {
        var e = Packages.GetOrAdd(folder, _ => new Entry());
        if (Environment.TickCount64 - Interlocked.Read(ref e.ReadAtMs) > 1000 && Interlocked.CompareExchange(ref e.Reading, 1, 0) == 0)
            Task.Run(() => Refresh(folder, e));
        var now = e.Now;
        error = now.Error;
        return now;
    }

    private static void Refresh(string folder, Entry e)
    {
        var generation = Interlocked.Read(ref e.Generation);
        SfPackage? package = null;
        string? error = null;
        View? view = null;
        var hash = FilesHash(folder);
        try
        {
            // the files as last read (and the page's view of them) stay: a re-read of the same bytes makes nothing new
            if (hash is not null && e.Now is { Hash: { } last } now && last == hash && (now.Package is not null || now.Error is not null)) return;
            package = ShroudForgePackages.Read(folder);
            if (Unreadable(package) is { } why)
            {
                package = null;
                error = why;
            }
            if (package is not null) view = ViewOf(folder, package);
        }
        catch (Exception x)
        {
            // never offer stale editable controls for a broken or removed file
            package = null;
            view = null;
            error = x is SfEditException ? x.Message : $"its files could not be read: {x.Message}";
        }
        finally
        {
            lock (e)
            {
                // a save that published while this read ran has newer values than this read may have seen
                if (e.Generation == generation && (package is not null || error is not null)) e.Now = new State(package, error, view, hash);
            }
            Interlocked.Exchange(ref e.ReadAtMs, Environment.TickCount64);
            Interlocked.Exchange(ref e.Reading, 0);
        }
    }

    /// <summary>A hash of mod.json and extended.mod.json as they are now, or null when they cannot be read.</summary>
    private static string? FilesHash(string folder)
    {
        try
        {
            // Match the reader's plain-file rule before reusing a package. Identical bytes through a new link
            // must not hide its rejection, nor cache that rejection after the player replaces the link with a file.
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return null;
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (var name in new[] { "mod.json", "extended.mod.json" })
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path))
                {
                    sha.AppendData([0]);
                    continue;
                }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (f.Length > ShroudForgePackages.MaxFileBytes) return null;
                var bytes = new byte[f.Length];
                f.ReadExactly(bytes);
                sha.AppendData([1]);
                sha.AppendData(BitConverter.GetBytes(bytes.LongLength));
                sha.AppendData(bytes);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception) { return null; }
    }

    /// <summary>Why a value of the package cannot be used (read on this worker, not on the render thread), or null. A string
    /// with an unpaired surrogate escape (<c>"\ud83d"</c>, a cut emoji) parses, but every later read of it throws; so does
    /// a number no double holds.</summary>
    internal static string? Unreadable(SfPackage p)
    {
        static void Check(JsonNode? n)
        {
            switch (n)
            {
                case JsonArray a:
                    foreach (var x in a) Check(x);
                    break;
                case JsonObject o:
                    foreach (var (_, x) in o) Check(x);
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    v.GetValue<string>();
                    break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                    ShroudForgePackages.NumberOf(v);
                    break;
            }
        }
        foreach (var s in p.Settings)
        {
            try
            {
                Check(s.Value);
                foreach (var o in s.Options) Check(o.Value);
                Check(s.EnabledWhen?.EqualsValue);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException)
            {
                return $"the value of setting {Clip(s.Key, 80)} cannot be read ({e.Message})";
            }
        }
        return null;
    }

    /// <summary>What a save leaves: the package it read back (or none, with why), with its view made here, on the writer.</summary>
    private static void Publish(string folder, Entry e, SfPackage? package, string? error)
    {
        View? view = null;
        try { if (package is not null) view = ViewOf(folder, package); }
        catch (Exception) { view = null; }  // the render thread makes it then
        lock (e)
        {
            e.Generation++;
            // The other menu can write after the save's read-back. A hash taken here could belong to that newer
            // file, not to this package, and suppress every later refresh. Let the next read establish the hash.
            e.Now = new State(package, error, view, null);
        }
        Interlocked.Exchange(ref e.ReadAtMs, Environment.TickCount64);
    }

    /// <summary>extended.mod.json's bytes now (empty when there is none), or null when they cannot be read.</summary>
    private static byte[]? FileBytes(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "extended.mod.json");
            var info = new FileInfo(path);
            if (!info.Exists) return [];
            if (info.Length > ShroudForgePackages.MaxFileBytes) return null;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[f.Length];
            f.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Reads a package now (the preview, tests).</summary>
    internal static void Load(string folder)
    {
        var e = Packages.GetOrAdd(folder, _ => new Entry());
        Refresh(folder, e);
    }

    // ------------------------------------------------------------------------------------- writing ----

    /// <summary>Queues a change of setting <paramref name="key"/> (null: the mod's switch); <paramref name="settleMs"/> later
    /// (no newer change to the same value) the writer saves it.</summary>
    public static void Edit(string folder, string? key, JsonNode? value, int settleMs = 0)
    {
        lock (Lock)
        {
            Pending[(folder, key)] = (value, Environment.TickCount64 + settleMs);
            Errors.Remove((folder, key));
            _version++;
        }
        if (Interlocked.Exchange(ref _writer, 1) == 0)
            new Thread(Writer) { IsBackground = true, Name = "kiln shroudforge settings" }.Start();
        Wake.Set();
    }

    /// <summary>The data layer's setting write. Tests replace it with one that changes the file and then fails, which
    /// the real writer no longer does since it writes fractional numbers as ShroudForge reads them.</summary>
    internal static Func<string, string, JsonNode?, SfPackage> SetSetting = ShroudForgePackages.SetSetting;

    private static void Writer()
    {
        while (true)
        {
            List<((string Folder, string? Key) Id, JsonNode? Value)> due = [];
            long next = long.MaxValue;
            lock (Lock)
            {
                var now = Environment.TickCount64;
                foreach (var (id, (value, at)) in Pending)
                    if (at <= now) due.Add((id, value));
                    else next = Math.Min(next, at);
            }
            foreach (var (id, value) in due)
            {
                string result;
                var ok = true;
                var e = Packages.GetOrAdd(id.Folder, _ => new Entry());
                var before = FileBytes(id.Folder);
                try
                {
                    var p = id.Key is null
                        ? ShroudForgePackages.SetEnabled(id.Folder, value is JsonValue v && v.GetValueKind() == JsonValueKind.True)
                        : SetSetting(id.Folder, id.Key, value);
                    Publish(id.Folder, e, p, null);
                    result = Saved(p, id.Key);
                    UiEngine.Log(LogLevel.Info, $"ShroudForge mod {p.Id}: {(id.Key is null ? "switched " + (p.Enabled ? "on" : "off") : id.Key + " = " + ShroudForgePackages.Render(value))} " +
                                                $"(extended.mod.json, from the Kiln menu)");
                }
                catch (Exception x) // malformed/untrusted metadata must not kill the one settings writer
                {
                    ok = false;
                    var what = id.Key is null ? "switch" : id.Key;
                    if (before is not null && FileBytes(id.Folder) is { } after && !before.AsSpan().SequenceEqual(after))
                    {
                        // the file changed but cannot be read back: not "not saved", and no controls of the old package
                        result = $"extended.mod.json was changed, but it cannot be read back: {x.Message}. ShroudForge may refuse the mod until the file is fixed.";
                        Publish(id.Folder, e, null, result);
                        UiEngine.Log(LogLevel.Warn, $"ShroudForge mod in {id.Folder}: {what} written, but extended.mod.json cannot be read back: {x.Message}");
                    }
                    else
                    {
                        result = x is SfEditException ? x.Message : "Could not save: " + x.Message;
                        UiEngine.Log(LogLevel.Warn, $"ShroudForge mod in {id.Folder}: {what} not saved: {result}");
                    }
                    lock (Lock) Errors[id] = result;
                }
                lock (Lock)
                {
                    // a newer edit of the same value waits for its own turn
                    if (Pending.TryGetValue(id, out var cur) && ReferenceEquals(cur.Value, value)) Pending.Remove(id);
                    Results[id.Folder] = (result, ok);
                    _version++;
                }
            }
            var wait = next == long.MaxValue ? 30_000 : (int)Math.Clamp(next - Environment.TickCount64, 1, 30_000);
            Wake.WaitOne(wait);
        }
    }

    private static string Saved(SfPackage p, string? key)
    {
        if (key is null)
            return p.SwitchLive ? $"Saved: {p.Name} is requested {(p.Enabled ? "on" : "off")}; a running ShroudForge normally picks this up within a second or two. Check its state above."
                : $"Saved: {p.Name} is configured {(p.Enabled ? "on" : "off")} for the next game start.";
        var s = p.Settings.FirstOrDefault(x => x.Key == key);
        return s is { Live: true } ? $"Saved: {s.Label} can apply while its mod is active; ShroudForge normally picks this up within a second or two."
            : $"Saved: {s?.Label ?? key} is configured for the next game start.";
    }

    /// <summary>A setting's text field had the keyboard in the frame before <paramref name="frame"/> (or in it): the page's
    /// section keys (Tab, LB / RB) leave it alone, as they do the search box.</summary>
    internal static bool FieldActive(ulong frame) => _activeFrame != 0 && frame >= _activeFrame && frame - _activeFrame <= 1;

    /// <summary>Waits until every queued edit is written (tests, the preview).</summary>
    internal static void Flush()
    {
        for (var i = 0; i < 400; i++)
        {
            lock (Lock)
                if (Pending.Count == 0) return;
            Wake.Set();
            Thread.Sleep(10);
        }
        throw new TimeoutException("ShroudForge settings writer did not finish");
    }

    // ------------------------------------------------------------------------------------- drawing ----

    /// <summary>What the data layer's <see cref="SfPackage.Repair"/> means for the player, or null when nothing needs it.</summary>
    internal static string? RepairNote(SfPackage p)
    {
        if (p.Repair.Count == 0) return null;
        var names = p.Repair.Take(3).Select(key => Clip(p.Settings.FirstOrDefault(x => x.Key == key)?.Label ?? key, 80)).ToList();
        var more = p.Repair.Count > 3 ? $" and {p.Repair.Count - 3} more" : "";
        return $"ShroudForge refuses this mod until its settings are saved once: an earlier Kiln wrote {string.Join(", ", names)}{more} " +
               "without its fraction.";
    }

    /// <summary>The most settings the page draws of one package; a line says how many more it has.</summary>
    internal const int DrawnSettings = 300;

    /// <summary>One setting as the page draws it: everything a frame needs, made when the view is.</summary>
    private sealed record Item(SfSettingDef Def, string Label, string? Description, JsonNode? Value, bool Enabled, string? Disabled,
        string[] OptionLabels, string? Error)
    {
        /// <summary>ImGui's id for it (its key, or its place when the key is long).</summary>
        public string Id { get; init; } = "";
        /// <summary>The value as a text field shows it (a string as it is, anything else as JSON).</summary>
        public string Text { get; init; } = "";
        /// <summary>Keybinds: the shortcut as Kiln names it.</summary>
        public string KeyLabel { get; init; } = "";
        /// <summary>Lists: each option's checkbox label and whether the value holds it.</summary>
        public string[] OptionIds { get; init; } = [];
        public bool[] Chosen { get; init; } = [];
        /// <summary>Choices: the combo's items as ImGui takes them, and the one the value is.</summary>
        public byte[] ComboItems { get; init; } = [];
        public int ComboIndex { get; init; }
    }

    /// <summary>What the page draws of a package, made on the worker that read it (and again after an edit or an error),
    /// never per frame: the first <see cref="DrawnSettings"/> settings by group with their shown values (a pending edit,
    /// else the file's), which can be changed now, and every text clipped to what a frame draws.</summary>
    private sealed class View
    {
        public required SfPackage Package;
        public long Version;
        public string? Error;
        public string Id = "";
        public bool SwitchOn;
        public (string Label, string? Description, Item[] Settings, (SfAction Action, string Label, string? Confirm)[] Actions)[] Groups = [];
        public Item[] Rest = [];
        public bool Empty;
        /// <summary>Settings the package has that the page does not draw.</summary>
        public int Hidden;
        /// <summary>The last build had too many settings or too much value text to repeat on the render thread.</summary>
        public bool RebuildOnWorker;
    }

    private static View ViewOf(string folder, SfPackage p)
    {
        var view = new View { Package = p, Version = Interlocked.Read(ref _version), Id = "sf." + folder,
            RebuildOnWorker = p.Settings.Count > DrawnSettings };
        // The setting cap alone does not bound a frame: one list can have thousands of checkboxes, as can the
        // groups/actions outside settings. Do not truncate editable options (a write could then lose selections).
        var metadataLeft = 1000;
        bool Fits(int count) => (metadataLeft -= count) >= 0;
        if (!Fits(p.Groups.Count) || !p.Groups.All(g => Fits(g.Actions.Count)) || !p.Settings.All(d => Fits(d.Options.Count)))
        {
            view.Error = "too many choices, groups or actions for this page (1000 combined at most). Change settings in the mod's extended.mod.json; the file is unchanged.";
            return view;
        }
        try { Build(view, folder, p); }
        catch (Exception e)  // a value no reader can use: said once, not thrown every frame
        {
            view.Error = $"a value cannot be read ({e.Message})";
            UiEngine.Log(LogLevel.Warn, $"ShroudForge mod in {folder}: its settings cannot be shown: {e.Message}");
        }
        return view;
    }

    /// <summary>The view to draw this frame, or null while none is made. A view made before the last edit or error is made
    /// again: at once for a small view, on a worker for a larger one (this frame draws the last).</summary>
    private static View? ViewFor(string folder, Entry e, State now)
    {
        if (now.Package is not { } p) return null;
        var version = Interlocked.Read(ref _version);
        var view = now.View is { } v && ReferenceEquals(v.Package, p) ? v : null;
        if (view is not null && view.Version == version) return view;
        if (view is { RebuildOnWorker: false })
        {
            view = ViewOf(folder, p);
            Keep(e, p, view);
            return view;
        }
        if (Interlocked.CompareExchange(ref e.Building, 1, 0) == 0)
            Task.Run(() =>
            {
                try { Keep(e, p, ViewOf(folder, p)); }
                finally { Interlocked.Exchange(ref e.Building, 0); }
            });
        return view;
    }

    private static void Keep(Entry e, SfPackage p, View view)
    {
        lock (e)
            if (ReferenceEquals(e.Now.Package, p) && (e.Now.View is not { } old || old.Version <= view.Version)) e.Now = e.Now with { View = view };
    }

    private static void Build(View view, string folder, SfPackage p)
    {
        var byKey = new Dictionary<string, SfSettingDef>(StringComparer.Ordinal);
        foreach (var x in p.Settings) byKey.TryAdd(x.Key, x);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (Lock)
        {
            view.Version = _version;
            foreach (var x in p.Settings) values[x.Key] = Pending.TryGetValue((folder, x.Key), out var pe) ? pe.Value : x.Value;
            foreach (var ((f, key), text) in Errors)
                if (key is not null && string.Equals(f, folder, StringComparison.Ordinal)) errors[key] = text;
            var pending = Pending.TryGetValue((folder, null), out var sw);
            view.SwitchOn = pending ? sw.Value is JsonValue v && v.GetValueKind() == JsonValueKind.True : p.Enabled;
        }
        var rule = new EnabledRule(byKey, values);
        var made = 0;
        var textLeft = 4096;
        Item Make(SfSettingDef d)
        {
            var value = values.TryGetValue(d.Key, out var v) ? v : d.Value;
            var enabled = rule.Of(d.Key);
            string? disabled = null;
            if (!enabled)
            {
                var cond = d.EnabledWhen!;
                var ctl = byKey.GetValueOrDefault(cond.Setting);
                var want = ctl?.Options.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, cond.EqualsValue))?.Label ?? Plain(cond.EqualsValue);
                disabled = Clip($"{Plain(value)}  (only used while {ctl?.Label ?? cond.Setting} is {want})", MaxDrawnText);
            }
            var labels = d.Options.Select(o => Clip(o.Label, MaxDrawnText)).ToArray();
            var text = value is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : Plain(value);
            // Even a one-setting package can have a huge list. Remember its cost on this worker instead of
            // serializing it again on the render thread just because another setting was edited.
            view.RebuildOnWorker |= (textLeft -= text.Length) < 0;
            var chosen = value as JsonArray;
            var combo = 0;
            for (var oi = 0; oi < d.Options.Count; oi++)
            {
                var o = d.Options[oi];
                if (!JsonNode.DeepEquals(o.Value, value) && !(ShroudForgePackages.NumberOf(o.Value) is { } a && a == ShroudForgePackages.NumberOf(value))) continue;
                combo = oi;
                break;
            }
            return new Item(d, Clip(d.Label, MaxDrawnText), d.Description is { } desc ? Clip(desc, MaxDrawnText) : null, value, enabled, disabled,
                labels, errors.TryGetValue(d.Key, out var err) ? Clip(err, MaxDrawnText) : null)
            {
                Id = d.Key.Length <= 200 ? d.Key : "#" + made++,
                Text = text,
                KeyLabel = d.Control == "keybind" ? Clip(SdlKeys.Label(value is JsonValue kv && kv.GetValueKind() == JsonValueKind.String ? text : "") ?? text, 200) : "",
                OptionIds = labels.Select((l, oi) => l + "###o" + oi).ToArray(),
                Chosen = d.Options.Select(o => chosen?.Any(x => JsonNode.DeepEquals(x, o.Value)) == true).ToArray(),
                ComboItems = d.Options.Count > 0 ? UiDraw.ComboItems(labels) : [],
                ComboIndex = combo,
            };
        }
        // the settings in the order the page shows them (by group, then the rest), the first DrawnSettings of them made
        var left = DrawnSettings;
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<(string, string?, Item[], (SfAction, string, string?)[])>();
        foreach (var g in p.Groups)
        {
            var items = new List<Item>();
            foreach (var key in g.Settings)
                if (byKey.TryGetValue(key, out var d) && placed.Add(key) && left > 0)
                {
                    items.Add(Make(d));
                    left--;
                }
            groups.Add((Clip(g.Label, MaxDrawnText), g.Description is { } gd ? Clip(gd, MaxDrawnText) : null, items.ToArray(),
                g.Actions.Select(a => (a, Clip(a.Label, MaxDrawnText), a.Confirm is { } c ? Clip(c, MaxDrawnText) : null)).ToArray()));
        }
        var rest = new List<Item>();
        foreach (var x in p.Settings)
            if (!placed.Contains(x.Key) && left > 0)
            {
                rest.Add(Make(x));
                left--;
            }
        view.Groups = groups.ToArray();
        view.Rest = rest.ToArray();
        view.Hidden = Math.Max(0, byKey.Count - (DrawnSettings - left));
        view.Empty = p.Settings.Count == 0 && !p.Groups.Any(g => g.Actions.Count > 0);
    }

    /// <summary><see cref="ShroudForgePackages.IsEnabled"/> over an index by key, remembering each answer: the same rule
    /// (the enabledWhen chain holds for the values; a missing controller or a cycle disables), each chain walked once.</summary>
    internal sealed class EnabledRule(IReadOnlyDictionary<string, SfSettingDef> byKey, IReadOnlyDictionary<string, JsonNode?> values)
    {
        private readonly Dictionary<string, bool> _known = new(StringComparer.Ordinal);
        private readonly List<string> _path = [];
        private readonly HashSet<string> _onPath = new(StringComparer.Ordinal);

        public bool Of(string key)
        {
            if (_known.TryGetValue(key, out var known)) return known;
            _path.Clear();
            _onPath.Clear();
            var current = key;
            bool result;
            while (true)
            {
                if (_known.TryGetValue(current, out result)) break;
                if (!byKey.TryGetValue(current, out var s)) { result = false; break; }
                if (s.EnabledWhen is not { } cond)
                {
                    _known[current] = result = true;
                    break;
                }
                if (!_onPath.Add(current)) { result = false; break; }  // a cycle
                _path.Add(current);
                if (!values.TryGetValue(cond.Setting, out var v) || !Same(v, cond.EqualsValue)) { result = false; break; }
                current = cond.Setting;
            }
            // every setting on the way holds its own condition, so each is enabled exactly when the end of the chain is
            foreach (var k in _path) _known[k] = result;
            return _known.TryGetValue(key, out known) ? known : result;
        }

        // ShroudForge compares numbers by value (5 = 5.0)
        private static bool Same(JsonNode? a, JsonNode? b) =>
            a is JsonValue av && b is JsonValue bv && av.GetValueKind() == JsonValueKind.Number && bv.GetValueKind() == JsonValueKind.Number
                ? ShroudForgePackages.NumberOf(av) == ShroudForgePackages.NumberOf(bv)
                : JsonNode.DeepEquals(a, b);
    }

    /// <summary>The ShroudForge part of a mod's details: its switch, its settings by group, its buttons.</summary>
    public static void Draw(UiDraw ui, CatalogMod m, float s, float k, Action<UiDraw, string, float> label)
    {
        if (m.Folder is not { } folder) return;
        var now = Current(folder, out var error);
        var p = now.Package;
        label(ui, "SWITCH AND SETTINGS", s);
        var view = ViewFor(folder, Packages.GetOrAdd(folder, _ => new Entry()), now);
        if (p is null || view?.Error is not null || view is null)
        {
            error = view?.Error ?? error;
            using (ui.PushColor(UiColorSlot.Text, error is null ? GameColors.Muted : GameColors.Warning))
                ui.TextWrapped(error is null ? "Reading its settings..." : $"Its settings cannot be shown: {Clip(error, MaxDrawnText)}");
            Result(ui, folder, k, problemsOnly: true);  // what the last save did to the file stays visible
            return;
        }
        using var id = ui.Id(view.Id);

        // the switch
        var on = view.SwitchOn;
        var was = on;
        if (ui.Checkbox(on ? "On###sf.switch" : "Off###sf.switch", ref on) && on != was) Edit(folder, null, JsonValue.Create(on));
        using (ui.FontSize(14 * k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
        {
            ui.TextWrapped(p.SwitchLive ? "ShroudForge switches it on or off while you play." : "Switching it on or off takes effect at the next game start (it changes game data when the game starts).");
            if (m.Source == "profile")
                ui.TextWrapped("Your Kiln profile lists this mod: the Kiln launcher sets this switch from the profile when it starts the game. To keep a change, make it in the profile too.");
        }
        Result(ui, folder, k);
        if (RepairNote(p) is { } repair)
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Warning))
                ui.TextWrapped(repair + " Changing any setting or its switch here repairs it.");

        if (view.Empty)
        {
            ui.Space(1, 6 * s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped("This mod has no settings.");
            return;
        }
        var running = Running(m);
        foreach (var g in view.Groups)
        {
            ui.Space(1, 10 * s);
            using (ui.FontSize(19 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.GoldBright))
                ui.TextWrapped(g.Label);
            if (g.Description is { } gd)
                using (ui.FontSize(14 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                    ui.TextWrapped(gd);
            foreach (var item in g.Settings) Setting(ui, folder, item, s, k);
            foreach (var a in g.Actions) Action(ui, folder, p, a.Action, a.Label, a.Confirm, running, s, k);
        }
        if (view.Rest.Length > 0)
        {
            if (view.Groups.Length > 0)
            {
                ui.Space(1, 10 * s);
                using (ui.FontSize(19 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.GoldBright))
                    ui.Text("Other settings");
            }
            foreach (var item in view.Rest) Setting(ui, folder, item, s, k);
        }
        if (view.Hidden > 0)
        {
            ui.Space(1, 10 * s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                ui.TextWrapped($"{view.Hidden} more setting{(view.Hidden == 1 ? " is" : "s are")} not shown here (this mod has {p.Settings.Count}; " +
                               $"the page shows the first {DrawnSettings}). Change them in the mod's extended.mod.json.");
        }
        ui.Space(1, 10 * s);
        using (ui.FontSize(13 * k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
            ui.TextWrapped("Saved to the mod's extended.mod.json, the file ShroudForge and the Kiln launcher read.");
    }

    // ShroudForge's runtime runs it in this process now (its state.json lists it active), whatever its effect reports
    private static bool Running(CatalogMod m) => m.Active;

    private static void Result(UiDraw ui, string folder, float k, bool problemsOnly = false)
    {
        (string Text, bool Ok) r;
        lock (Lock)
            if (!Results.TryGetValue(folder, out r) || (problemsOnly && r.Ok)) return;
        using (ui.FontSize(14 * k))
        using (ui.PushColor(UiColorSlot.Text, r.Ok ? GameColors.Ok : GameColors.Warning))
            ui.TextWrapped(r.Text);
    }

    private static void Setting(UiDraw ui, string folder, Item item, float s, float k)
    {
        var d = item.Def;
        using var _ = ui.Id(item.Id);
        ui.Space(1, 8 * s);
        using (ui.PushColor(UiColorSlot.Text, item.Enabled ? GameColors.Text : GameColors.Faint))
            ui.TextWrapped(item.Label);
        if (item.Disabled is { } why)
        {
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
                ui.TextWrapped(why);
        }
        else Widget(ui, folder, item, s, k);
        using (ui.FontSize(14 * k))
        {
            if (item.Description is { } desc)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(desc);
            if (!d.Live)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Attention)) ui.TextWrapped("Applies at the next game start.");
            if (item.Error is { } err)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Warning)) ui.TextWrapped(err);
        }
    }

    private static string Plain(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.True => "on",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.False => "off",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.String => jv.GetValue<string>(),
        _ => ShroudForgePackages.Render(v),
    };

    private static void Widget(UiDraw ui, string folder, Item item, float s, float k)
    {
        var d = item.Def;
        var value = item.Value;
        var width = Math.Max(160 * s, ui.Cursor.AvailWidth * 0.7f);
        switch (d.Type, d.Control)
        {
            case (_, "keybind"):
            {
                using (ui.PushColor(UiColorSlot.Text, GameColors.Text))
                    ui.Text("Shortcut: " + item.KeyLabel);
                using (ui.FontSize(14 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                    ui.TextWrapped("Edit the SDL binding below (not key capture): Ctrl+SDL:66 is Ctrl+F9; Mouse:4 is mouse button 4. Empty disables it. SF checks keyboard availability.");
                TextField(ui, folder, d, item.Text, width, text => JsonValue.Create(text));
                return;
            }
            case ("boolean", _) when d.Options.Count == 0:
            {
                var b = value is JsonValue bv && bv.GetValueKind() == JsonValueKind.True;
                var before = b;
                if (ui.Checkbox(b ? "On###v" : "Off###v", ref b) && b != before) Edit(folder, d.Key, JsonValue.Create(b));
                return;
            }
            case ("array", _):
            {
                for (var oi = 0; oi < d.Options.Count; oi++)
                {
                    var has = item.Chosen[oi];
                    if (!ui.Checkbox(item.OptionIds[oi], ref has) || has == item.Chosen[oi]) continue;
                    // ShroudForge keeps the options' order
                    var next = d.Options.Select((x, xi) => (x, xi)).Where(t => t.xi == oi ? has : item.Chosen[t.xi]).Select(t => t.x.Value?.DeepClone()).ToArray();
                    Edit(folder, d.Key, new JsonArray(next));
                }
                return;
            }
            case (_, _) when d.Options.Count > 0:
            {
                var i = item.ComboIndex;
                var before = i;
                ui.NextItemWidth(width);
                if (ui.Combo("##v", ref i, item.ComboItems, d.Options.Count) && i != before) Edit(folder, d.Key, d.Options[i].Value?.DeepClone());
                return;
            }
            case ("integer", not "number") when d.Min is { } lo && d.Max is { } hi && lo <= hi && lo >= int.MinValue && hi <= int.MaxValue && hi - lo <= 1_000_000:
            {
                var n = (int)Math.Round(ShroudForgePackages.NumberOf(value) ?? lo);
                var before = n;
                ui.NextItemWidth(width);
                if (ui.Slider("##v", ref n, (int)Math.Ceiling(lo), (int)Math.Floor(hi)))
                {
                    if (d.Step is > 1 and var st) n = (int)Math.Clamp(lo + Math.Round((n - lo) / st) * st, lo, hi);
                    if (n != before) Edit(folder, d.Key, JsonValue.Create((long)n), SettleMs);
                }
                return;
            }
            case ("number", not "number") when d.Min is { } lo && d.Max is { } hi && lo <= hi && lo >= -float.MaxValue && hi <= float.MaxValue:
            {
                var f = (float)(ShroudForgePackages.NumberOf(value) ?? lo);
                var before = f;
                ui.NextItemWidth(width);
                if (ui.Slider("##v", ref f, (float)lo, (float)hi))
                {
                    double x = f;
                    if (d.Step is > 0 and var st) x = Math.Clamp(lo + Math.Round((x - lo) / st) * st, lo, hi);
                    x = Math.Round(x, 4);
                    if (Math.Abs(x - before) > 1e-9) Edit(folder, d.Key, d.Type == "integer" ? JsonValue.Create((long)Math.Round(x)) : JsonValue.Create(x), SettleMs);
                }
                return;
            }
            case ("number" or "integer", _):
            {
                TextField(ui, folder, d, item.Text, width, text =>
                    d.Type == "integer" && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? JsonValue.Create(whole) :
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) ? JsonValue.Create(x) : null);
                return;
            }
            default:
                TextField(ui, folder, d, item.Text, width, text => JsonValue.Create(text));
                return;
        }
    }

    // A text or number field: what the player types is kept until they leave the field, then saved.
    private static void TextField(UiDraw ui, string folder, SfSettingDef d, string shown, float width, Func<string, JsonNode?> parse)
    {
        var id = (folder, d.Key);
        var frame = ui.FrameNumber;
        string text;
        lock (Lock)
        {
            // what was typed into a field that then went away (another section, the menu closed) without the player leaving
            // it is dropped: never saved, and never shown as if it were the value
            if (Typing.TryGetValue(id, out var gone) && frame > gone.Frame + 1) Typing.Remove(id);
            text = Typing.TryGetValue(id, out var t) ? t.Text : shown;
        }
        var multi = d.Control == "textarea";
        // the field holds the whole value with room to type: it never shows a value cut to fit (and the cut text is never
        // saved); a value too long for any field is shown, not edited
        var cap = multi ? MaxAreaBytes : MaxLineBytes;
        if (!UiDraw.Editable(text, cap) || !UiDraw.Editable(shown, cap))
        {
            var nul = shown.Contains('\0') || text.Contains('\0');
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(Clip(shown.Replace('\0', ' '), MaxDrawnText));
            using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                ui.TextWrapped(nul
                    ? "This value holds a NUL character, which a text field cannot show or keep: change it in the mod's extended.mod.json."
                    : $"Too long to edit here ({shown.Length} characters): change it in the mod's extended.mod.json.");
            return;
        }
        ui.NextItemWidth(width);
        // always the same size for a field: ImGui sizes its own copy of the text once, when the field becomes active, and
        // takes the size given every frame as its bound, so a size that grew while the player types let ImGui write past
        // its buffer. A declared maxLength is not a limit here; Check refuses a longer value when it is saved.
        var changed = multi ? ui.InputMultiline("##v", ref text, cap, width, 90 * ui.Scale) : ui.InputText("##v", ref text, cap);
        var it = ui.LastItem;
        if (it.Active) _activeFrame = frame;
        lock (Lock)
        {
            if (changed) Typing[id] = (text, frame);
            else if (Typing.TryGetValue(id, out var cur)) Typing[id] = (cur.Text, frame);
            if (!it.Deactivated || !Typing.Remove(id)) return;
        }
        if (text == shown) return;
        if (parse(text) is { } parsed) Edit(folder, d.Key, parsed);
        else
            lock (Lock)
            {
                Errors[(folder, d.Key)] = $"{Clip(d.Label, 200)}: \"{Clip(text, 200)}\" is not a number";
                _version++;
            }
    }

    /// <summary>The file hand-off; tests can hold it while another click arrives.</summary>
    internal static Action<string, SfPackage, string> QueueAction = ShroudForgePackages.QueueAction;
    // Worker-only: the waiting check and the atomic file hand-off are one operation, including across mod folders.
    private static readonly object ActionGate = new();

    private static void Action(UiDraw ui, string folder, SfPackage p, SfAction a, string label, string? confirm, bool running, float s, float k)
    {
        using var _ = ui.Id("act." + a.Id);
        ui.Space(1, 6 * s);
        var armed = _armed is { } x && x.Folder == folder && x.Action == a.Id && Environment.TickCount64 - x.AtMs < 6000;
        bool clicked;
        // only a mod ShroudForge runs now can take an action: a request left in its folder would run at a later start
        using (ui.PushColor(UiColorSlot.Text, running ? GameColors.Text : GameColors.Faint))
            clicked = ui.Button(armed ? "Confirm: " + label : label);
        if (clicked && !running)
        {
            lock (Lock) Results[folder] = ($"{label} was not sent: ShroudForge runs actions only while it runs the mod (see its state above). Nothing was queued.", false);
        }
        else if (clicked)
        {
            if (a.Confirm is not null && !armed) _armed = (folder, a.Id, Environment.TickCount64);
            else
            {
                _armed = null;
                Task.Run(() =>
                {
                    lock (ActionGate)
                    {
                        string text;
                        var ok = true;
                        try
                        {
                            var game = GameDir();
                            // ShroudForge keeps a request of an active mod without a handler for ever and reads 32 per turn:
                            // one of Kiln's own per mod at a time
                            if (Waiting(game, p.Id))
                            {
                                ok = false;
                                text = $"{label}: an earlier request for {p.Name} (from this or an earlier game start) is still waiting for ShroudForge, " +
                                       "which runs it once the mod takes it; nothing new was queued.";
                            }
                            else
                            {
                                QueueAction(game, p, a.Id);
                                text = $"Requested: {label}.";
                                UiEngine.Log(LogLevel.Info, $"ShroudForge mod {p.Id}: action {a.Id} requested from the Kiln menu");
                            }
                        }
                        catch (Exception e)  // whatever the files or the config hold: the player gets a line, not a silent worker
                        {
                            ok = false;
                            text = $"{label} could not be sent: {e.Message}";
                            UiEngine.Log(LogLevel.Warn, $"ShroudForge mod {p.Id}: action {a.Id} could not be sent: {e.Message}");
                        }
                        lock (Lock) Results[folder] = (text, ok);
                    }
                });
            }
        }
        if (armed && confirm is { } c)
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                ui.TextWrapped(c);
    }

    internal static string GameDir() => ModCatalog.Sources().GameDir;

    /// <summary>A request Kiln queued for mod <paramref name="id"/>, in this game or an earlier one, is still in ShroudForge's
    /// actions folder (ShroudForgePackages.QueueAction names them <c>&lt;id&gt;-&lt;time&gt;-&lt;guid&gt;-&lt;pid&gt;.json</c>).
    /// ShroudForge never drops one by age, so one from an earlier start may still run.</summary>
    private static bool Waiting(string game, string id)
    {
        var dir = Path.Combine(ShroudForgePackages.Folder(game, "ui"), "actions");
        if (!Directory.Exists(dir)) return false;
        var ours = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(id) + "-[0-9]+-[0-9a-f]{32}-[0-9]+\\.json$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(dir, "*.json").Any(f => ours.IsMatch(Path.GetFileName(f)));
    }

    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Pending.Clear();
            Errors.Clear();
            Results.Clear();
            Typing.Clear();
            _version++;
        }
        _activeFrame = 0;
        Packages.Clear();
    }
}

/// <summary>ShroudForge's key names (SDL scancodes, <c>Ctrl+SDL:66</c>) as Kiln's (<c>Ctrl+F9</c>).</summary>
internal static class SdlKeys
{
    private static readonly Dictionary<int, string> Names = Build();

    private static Dictionary<int, string> Build()
    {
        var d = new Dictionary<int, string>();
        for (var i = 0; i < 26; i++) d[4 + i] = ((char)('A' + i)).ToString();
        for (var i = 1; i <= 9; i++) d[29 + i] = i.ToString(CultureInfo.InvariantCulture);
        d[39] = "0";
        d[40] = "Enter"; d[41] = "Escape"; d[42] = "Backspace"; d[43] = "Tab"; d[44] = "Space"; d[45] = "Minus"; d[46] = "Equals";
        d[47] = "LeftBracket"; d[48] = "RightBracket"; d[49] = "Backslash"; d[51] = "Semicolon"; d[52] = "Quote"; d[53] = "Backquote";
        d[54] = "Comma"; d[55] = "Period"; d[56] = "Slash"; d[57] = "CapsLock";
        for (var i = 0; i < 12; i++) d[58 + i] = "F" + (i + 1);
        d[71] = "ScrollLock"; d[72] = "Pause"; d[73] = "Insert"; d[74] = "Home"; d[75] = "PageUp"; d[76] = "Delete"; d[77] = "End";
        d[78] = "PageDown"; d[79] = "Right"; d[80] = "Left"; d[81] = "Down"; d[82] = "Up"; d[83] = "NumLock"; d[84] = "NumpadDivide";
        d[85] = "NumpadMultiply"; d[86] = "NumpadSubtract"; d[87] = "NumpadAdd"; d[88] = "Enter";
        for (var i = 1; i <= 9; i++) d[88 + i] = "Numpad" + i;
        d[98] = "Numpad0"; d[99] = "NumpadDecimal";
        for (var i = 0; i < 12; i++) d[104 + i] = "F" + (13 + i);
        return d;
    }

    /// <summary>The keyboard part of a ShroudForge shortcut as a Kiln key (<c>F9</c>, <c>Ctrl+Shift+K</c>); null when it
    /// has none Kiln can bind (a mouse button only, a Meta chord, an unknown key).</summary>
    public static string? ToKilnKey(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return null;
        foreach (var alternative in binding.Split('|'))
        {
            var parts = alternative.Split('+');
            var key = parts[^1];
            if (!key.StartsWith("SDL:", StringComparison.Ordinal) ||
                !int.TryParse(key.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || !Names.TryGetValue(code, out var name))
                continue;
            var mods = parts[..^1];
            if (mods.Any(m => m is not ("Ctrl" or "Alt" or "Shift"))) return null;
            var text = string.Join("+", mods.OrderBy(m => m switch { "Ctrl" => 0, "Shift" => 1, _ => 2 }).Append(name));
            return KeyChord.TryParse(text, out _) ? text : null;
        }
        return null;
    }

    /// <summary>A ShroudForge shortcut for people: the keyboard part as Kiln names it, else the raw text.</summary>
    public static string? Label(string binding) => ToKilnKey(binding) ?? (binding.Length > 0 ? binding : null);
}

/// <summary>ShroudForge's menu shortcut (F9 by default) and <c>shroudforge.ui.toggle_modloader()</c> when no ShroudForge
/// Modloader window runs. kiln-shroudforge then says so in kiln/state/shroudforge.json (<c>menu: "kiln"</c>) and holds
/// the named event ShroudForge's runtime signals; both open the Kiln menu on the ShroudForge mods.</summary>
internal static class ShroudForgeMenu
{
    internal const string BindingId = "shroudforge.menu";
    private static int _started;
    private static KeyBinding? _binding;

    /// <summary>The status file's word for who answers ShroudForge's menu; null while it is not decided (or not this start).</summary>
    internal readonly record struct Claim(string? MenuBy, string? Key, bool Final);

    /// <summary>What kiln/state/shroudforge.json says (pure: tests).</summary>
    internal static Claim Read(JsonElement? doc)
    {
        if (doc is not { ValueKind: JsonValueKind.Object } d) return new Claim(null, null, false);
        string? S(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var mode = S("mode");
        var by = S("menu");
        // a status that can no longer change: ShroudForge does not run here, or runs on its own
        var final = mode is "self-hosted" or "off" or "not-installed" or "failed" || S("side") == "server" ||
                    S("runtime") == "stopped" || by is "off" or "unavailable";
        return new Claim(mode == "hosted" && !final ? by : null, S("menuKey"), final);
    }

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || !OperatingSystem.IsWindows()) return;
        new Thread(Watch) { IsBackground = true, Name = "kiln shroudforge menu" }.Start();
    }

    private static void Watch()
    {
        try
        {
            var src = ModCatalog.Sources();
            // kiln-shroudforge decides early; a ShroudForge window that closes later hands its shortcut over then
            for (var i = 0; ; i++)
            {
                var f = ModCatalog.ReadState(src, "shroudforge.json");
                var c = Read(f.Current ? f.Doc : null);
                if (c.MenuBy == "kiln")
                {
                    Take(c.Key);
                    return;
                }
                if (c.Final) return;
                Thread.Sleep(i < 20 ? 3000 : 15000);
            }
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Warn, "ShroudForge menu shortcut: " + e.Message);
        }
    }

    private static void Take(string? sfKey)
    {
        var name = $"Local\\ShroudForge.ModMenu.Open.{Environment.ProcessId}";
        var h = OpenEventW(SYNCHRONIZE, false, name);
        if (h == 0)
        {
            UiEngine.Log(LogLevel.Warn, $"ShroudForge's menu event is missing (error {Marshal.GetLastPInvokeError()}): use Kiln's Mods entry");
            return;
        }
        try
        {
            var mod = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's own binding: no mod's unload removes it
            try
            {
                // Do not silently steal F9 when SF explicitly has no key or a mouse/Meta binding we cannot translate.
                var key = SdlKeys.ToKilnKey(sfKey ?? "SDL:66") ?? "None";
                _binding = KeyBindings.Register(BindingId, key, Toggle, "ShroudForge mods: open Kiln's ShroudForge page", "ShroudForge");
                _binding.WhileCapturing = true;
                UiEngine.Log(LogLevel.Info, $"ShroudForge has no Modloader window: key {_binding.Key} and shroudforge.ui.toggle_modloader() toggle Kiln's ShroudForge page (remap in Keys; Mods is always available)");
            }
            finally { ModContext.CurrentMod = mod; }
            var src = ModCatalog.Sources();
            while (true)
            {
                var wait = WaitForSingleObject(h, 2000);
                if (wait != 0 && wait != 258) break;  // WAIT_TIMEOUT: check runtime lifecycle too
                var f = ModCatalog.ReadState(src, "shroudforge.json");
                var claim = Read(f.Current ? f.Doc : null);
                if (claim.Final || claim.MenuBy == "helper") break;
                if (wait == 0) Toggle();
            }
        }
        finally
        {
            _binding?.Dispose();
            _binding = null;
            CloseHandle(h);
        }
    }

    /// <summary>The shortcut: opens the Kiln menu on ShroudForge, or closes it when it already shows ShroudForge.</summary>
    public static void Toggle()
    {
        var page = ModManagerHost.Page;
        // a press before the page drew a frame finds its request still pending: that is the ShroudForge page too
        var shown = page.PendingRequest == "shroudforge" || page.PendingRequest is null && page.Section == ModManagerSection.Mods && page.Filter.Kind == "shroudforge";
        if (Ui.MenuOpen && shown)
        {
            page.CancelRequest();
            Ui.MenuOpen = false;
        }
        else Open();
    }

    public static void Open()
    {
        ModManagerHost.Page.Request(new EngineFilter("shroudforge"));
        Ui.MenuOpen = true;
    }

    private const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(nint h);

    [DllImport("kernel32")]
    private static extern uint WaitForSingleObject(nint h, uint ms);
}
