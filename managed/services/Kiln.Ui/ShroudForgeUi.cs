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
    private sealed record State(SfPackage? Package, string? Error, View? View, string? Hash, int Texts = -1, bool Library = false);

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
    // the texts are made when drawn (the language may change while they show)
    private static readonly Dictionary<(string Folder, string? Key), Func<string>> Errors = new();
    // bumped (under Lock) whenever Pending or Errors change: the page's views of the packages are made again then
    private static long _version;
    // what the last write of each mod did, in words (shown under the switch)
    private static readonly Dictionary<string, (Func<string> Text, bool Ok)> Results = new(StringComparer.OrdinalIgnoreCase);
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
        var texts = KilnText.Generation;
        SfPackage? package = null;
        string? error = null;
        View? view = null;
        var hash = FilesHash(folder);
        var library = IsLibraryEntry(folder);
        try
        {
            // the files as last read (and the page's view of them) stay: a re-read of the same bytes makes nothing new
            // (an error made in another language is made again)
            if (hash is not null && e.Now is { Hash: { } last } now && last == hash && (now.Package is not null || now.Error is not null) &&
                (now.Error is null || now.Texts == texts)) return;
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
            error = x is SfEditException ? x.Message : KilnText.T("shroudforge.files_unreadable", ("error", x.Message));
        }
        finally
        {
            lock (e)
            {
                // a save that published while this read ran has newer values than this read may have seen
                if (e.Generation == generation && (package is not null || error is not null)) e.Now = new State(package, error, view, hash, texts, library);
            }
            Interlocked.Exchange(ref e.ReadAtMs, Environment.TickCount64);
            Interlocked.Exchange(ref e.Reading, 0);
        }
    }

    /// <summary>The folder is an entry of Kiln's Library (<c>&lt;library&gt;/&lt;kind&gt;/&lt;id&gt;/&lt;version&gt;+&lt;hash8&gt;</c>,
    /// with its import record <c>&lt;folder&gt;.json</c> beside it; kiln-manage library.rs record_path): files the launcher
    /// checks against their hash before it copies them, which ShroudForge never runs. A change there breaks the entry (the
    /// launcher then calls the mod "changed in its folder"), so the page shows such a folder and never writes it. Review of
    /// 0.2.0-dev.31.24: 0.2.0-dev.31.23's page drew the launcher's compatibility entry from the Library folder and saved
    /// its switch and settings there (HTR, Nexus 347). Worker threads only (file access).</summary>
    internal static bool IsLibraryEntry(string folder)
    {
        try
        {
            var record = folder.TrimEnd('/', '\\') + ".json";
            var info = new FileInfo(record);
            if (!info.Exists || info.Length > 1 << 20) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(record));
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String &&
                   r.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String && r.TryGetProperty("imported", out _);
        }
        catch (Exception) { return false; }
    }

    /// <summary>The page's last read found <paramref name="folder"/> to be a Library entry (<see cref="IsLibraryEntry"/>).</summary>
    internal static bool IsLibraryCopy(string folder) => Packages.TryGetValue(folder, out var e) && e.Now.Library;

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
                return KilnText.T("shroudforge.value_unreadable", ("setting", Clip(s.Key, 80)), ("error", e.Message));
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
                Func<string> result;
                var ok = true;
                var e = Packages.GetOrAdd(id.Folder, _ => new Entry());
                var before = FileBytes(id.Folder);
                try
                {
                    if (IsLibraryEntry(id.Folder)) throw new SfEditException(KilnText.T("shroudforge.library_copy"));
                    var p = id.Key is null
                        ? ShroudForgePackages.SetEnabled(id.Folder, value is JsonValue v && v.GetValueKind() == JsonValueKind.True)
                        : SetSetting(id.Folder, id.Key, value);
                    Publish(id.Folder, e, p, null);
                    var key = id.Key;
                    result = () => Saved(p, key);
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
                        var message = x.Message;
                        result = () => KilnText.T("shroudforge.save.unreadable_after", ("error", message));
                        Publish(id.Folder, e, null, result());
                        UiEngine.Log(LogLevel.Warn, $"ShroudForge mod in {id.Folder}: {what} written, but extended.mod.json cannot be read back: {x.Message}");
                    }
                    else
                    {
                        var message = x.Message;
                        result = x is SfEditException ? () => message : () => KilnText.T("shroudforge.save.failed", ("error", message));
                        UiEngine.Log(LogLevel.Warn, $"ShroudForge mod in {id.Folder}: {what} not saved: {(x is SfEditException ? message : "Could not save: " + message)}");
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
        // a kiln-backend start's change applies at the next start
        if (NextStartOnly(p.Folder)) p = p with { SwitchLive = false, Settings = p.Settings.Select(x => x with { Live = false }).ToList() };
        if (key is null)
            return p.SwitchLive
                ? KilnText.T(p.Enabled ? "shroudforge.save.switch_live_on" : "shroudforge.save.switch_live_off", ("mod", p.Name))
                : KilnText.T(p.Enabled ? "shroudforge.save.switch_next_on" : "shroudforge.save.switch_next_off", ("mod", p.Name));
        var s = p.Settings.FirstOrDefault(x => x.Key == key);
        return s is { Live: true } ? KilnText.T("shroudforge.save.setting_live", ("setting", s.Label))
            : KilnText.T("shroudforge.save.setting_next", ("setting", s?.Label ?? key));
    }

    /// <summary>A setting's text field had the keyboard in the frame before <paramref name="frame"/> (or in it), or a
    /// shortcut's capture box waits for a press: the page's section keys (Tab, LB / RB) leave it alone, as they do the
    /// search box.</summary>
    internal static bool FieldActive(ulong frame) => _activeFrame != 0 && frame >= _activeFrame && frame - _activeFrame <= 1 || Capturing;

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
        return p.Repair.Count > 3
            ? KilnText.T("shroudforge.repair_more", ("settings", string.Join(", ", names)), ("more", p.Repair.Count - 3))
            : KilnText.T("shroudforge.repair", ("settings", string.Join(", ", names)));
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
        /// <summary>The <see cref="KilnText.Generation"/> its texts were made in.</summary>
        public int Texts;
        /// <summary>The keyboard layout its shortcut names were made in (<see cref="KeyLayout.Stamp"/>).</summary>
        public long Layout;
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
        var view = new View { Package = p, Version = Interlocked.Read(ref _version), Texts = KilnText.Generation, Layout = KeyLayout.Stamp(), Id = "sf." + folder,
            RebuildOnWorker = p.Settings.Count > DrawnSettings };
        // The setting cap alone does not bound a frame: one list can have thousands of checkboxes, as can the
        // groups/actions outside settings. Do not truncate editable options (a write could then lose selections).
        var metadataLeft = 1000;
        bool Fits(int count) => (metadataLeft -= count) >= 0;
        if (!Fits(p.Groups.Count) || !p.Groups.All(g => Fits(g.Actions.Count)) || !p.Settings.All(d => Fits(d.Options.Count)))
        {
            view.Error = KilnText.T("shroudforge.too_many");
            return view;
        }
        try { Build(view, folder, p); }
        catch (Exception e)  // a value no reader can use: said once, not thrown every frame
        {
            view.Error = KilnText.T("shroudforge.value_error", ("error", e.Message));
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
        if (view is not null && view.Version == version && view.Texts == KilnText.Generation && view.Layout == KeyLayout.Stamp()) return view;
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
            if (ReferenceEquals(e.Now.Package, p) && (e.Now.View is not { } old || old.Version < view.Version ||
                                                      old.Version == view.Version && (old.Texts != view.Texts || old.Layout != view.Layout)))
                e.Now = e.Now with { View = view };
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
                if (key is not null && string.Equals(f, folder, StringComparison.Ordinal)) errors[key] = text();
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
                var want = ctl?.Options.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, cond.EqualsValue))?.Label ?? Shown(cond.EqualsValue);
                disabled = Clip(KilnText.T("shroudforge.only_used_while", ("value", Shown(value)), ("setting", ctl?.Label ?? cond.Setting), ("want", want)), MaxDrawnText);
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
                KeyLabel = d.Control == "keybind" ? Clip(value is JsonValue kv && kv.GetValueKind() == JsonValueKind.String ? SfShortcuts.Label(text) : text, 200) : "",
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

    /// <summary>What a ShroudForge mod that says "waiting" waits for, in plain words, or null when it does not wait. The
    /// mod's own words (its effect report, or ShroudForge's reason) are in <see cref="CatalogMod.Reason"/>: a mod that
    /// changes the world waits for a world this game hosts, SF Unlimited Flight also whenever the player is not gliding,
    /// so a player who opens the menu between flights always sees it waiting (player report, Nexus, 13 Nov 2026).</summary>
    internal static string? WaitingFor(CatalogMod m)
    {
        if (m.Engine != "shroudforge" || m.Word != "waiting" || m.Reason is not { } why) return null;
        // ShroudForge's own reason (ModCatalog's SfRefine: catalog.sf.error, "ShroudForge: " + its error): the word it starts with
        if (Unfill(why, KilnText.T("catalog.sf.error", ("error", Hole))) is { } err)
        {
            if (err.StartsWith("restart-required", StringComparison.Ordinal))
                return KilnText.T("shroudforge.waits.restart");
            if (err.StartsWith("dependency-blocked", StringComparison.Ordinal))
                return KilnText.T("shroudforge.waits.dependency");
            return null;
        }
        // the mod's own report (SfRefine: catalog.sf.effect, "ShroudForge Lua is active; reported effect: waiting. " + its
        // detail): whole phrases the mod reports, never a word somewhere inside another one
        if (Unfill(why, KilnText.T("catalog.sf.effect", ("effect", "waiting"), ("detail", Hole))) is not { } reported) return null;
        var detail = reported.Trim();
        switch (detail)
        {
            case "Waiting for typed ECS access and the authoritative write scope":
                return KilnText.T("shroudforge.waits.write_access");
            case "Waiting for an authoritative world":
                return KilnText.T("shroudforge.waits.world");
            case "Player query is pending":
                return KilnText.T("shroudforge.waits.player");
        }
        // ShroudForge's override tracker summary (track_runtime_overrides.lua `summary`): "<message> | writes=N | <reason>
        // | ...", nested once more for SF Unlimited Flight's steering. Gliding is what it waits for only when every reason
        // in it is that one.
        var reasons = detail.Split(" | ").Skip(1).Where(p => !p.StartsWith("writes=", StringComparison.Ordinal)).ToArray();
        if (reasons.Length > 0 && reasons.All(r => r == "Glider settings: Player is not actively using a glider"))
            return KilnText.T("shroudforge.waits.glide");
        return KilnText.T("shroudforge.waits.other");
    }

    // what a text made with this in place of one value holds there
    private const string Hole = "\u0001";

    /// <summary>The value <paramref name="text"/> holds where <paramref name="made"/> (a text made with <see cref="Hole"/> for
    /// one value) has the hole, or null when the text is not one of those.</summary>
    private static string? Unfill(string text, string made)
    {
        var at = made.IndexOf(Hole, StringComparison.Ordinal);
        if (at < 0) return null;
        var before = made[..at];
        var after = made[(at + Hole.Length)..];
        return text.Length >= before.Length + after.Length && text.StartsWith(before, StringComparison.Ordinal) && text.EndsWith(after, StringComparison.Ordinal)
            ? text[before.Length..^after.Length] : null;
    }

    /// <summary>The ShroudForge part of a mod's details: its switch, its settings by group, its buttons.</summary>
    public static void Draw(UiDraw ui, CatalogMod m, float s, float k, Action<UiDraw, string, float> label)
    {
        // a start Kiln's interpreter has: its part truth, requested against current
        if (m.Sfi is { } sfiRow) SfiLines(ui, sfiRow, s, k, label);
        if (m.Folder is not { } folder) return;
        // the writer's words for this folder follow the row it was drawn for
        NextStart[folder] = m.Sfi is { NextStartOnly: true };
        if (WaitingFor(m) is { } waits)
        {
            label(ui, KilnText.T("shroudforge.labels.waits"), s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Text)) ui.TextWrapped(waits);
        }
        var now = Current(folder, out var error);
        var p = now.Package;
        // Kiln's Library copy of the mod (the details name its folder): never written here (IsLibraryEntry)
        if (now.Library)
        {
            label(ui, KilnText.T("shroudforge.labels.settings"), s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Warning)) ui.TextWrapped(KilnText.T("shroudforge.library_copy"));
            return;
        }
        // what the mod published for the player, which ShroudForge's own window shows in its news (a hosted or installed
        // start: a kiln-backend start never reads ShroudForge's folders)
        if (m.Sfi is null) News(ui, m, folder, p, s, k, label);
        label(ui, KilnText.T("shroudforge.labels.settings"), s);
        var view = ViewFor(folder, Packages.GetOrAdd(folder, _ => new Entry()), now);
        if (p is null || view?.Error is not null || view is null)
        {
            error = view?.Error ?? error;
            using (ui.PushColor(UiColorSlot.Text, error is null ? GameColors.Muted : GameColors.Warning))
                ui.TextWrapped(error is null ? KilnText.T("shroudforge.reading") : KilnText.T("shroudforge.cannot_show", ("error", Clip(error, MaxDrawnText))));
            Result(ui, folder, k, problemsOnly: true);  // what the last save did to the file stays visible
            return;
        }
        using var id = ui.Id(view.Id);

        // the switch
        var on = view.SwitchOn;
        var was = on;
        if (ui.Checkbox((on ? KilnText.T("shroudforge.on") : KilnText.T("shroudforge.off")) + "###sf.switch", ref on) && on != was) Edit(folder, null, JsonValue.Create(on));
        // (until the interpreter observes package files while the game runs, every change in a
        // kiln-backend start applies at the next start)
        var switchLive = p.SwitchLive && !NextStartOnly(folder);
        using (ui.FontSize(14 * k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
        {
            ui.TextWrapped(switchLive ? KilnText.T("shroudforge.switch_live") : KilnText.T("shroudforge.switch_next"));
            if (m.Source == "profile")
                ui.TextWrapped(KilnText.T("shroudforge.switch_profile"));
        }
        Result(ui, folder, k);
        if (RepairNote(p) is { } repair)
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Warning))
                ui.TextWrapped(KilnText.T("shroudforge.repair_here", ("repair", repair)));

        if (view.Empty)
        {
            ui.Space(1, 6 * s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(KilnText.T("shroudforge.no_settings"));
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
                    ui.Text(KilnText.T("shroudforge.other_settings"));
            }
            foreach (var item in view.Rest) Setting(ui, folder, item, s, k);
        }
        if (view.Hidden > 0)
        {
            ui.Space(1, 10 * s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                ui.TextWrapped(KilnText.Plural("shroudforge.hidden", view.Hidden, ("total", p.Settings.Count), ("shown", DrawnSettings)));
        }
        ui.Space(1, 10 * s);
        using (ui.FontSize(13 * k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
            ui.TextWrapped(KilnText.T("shroudforge.saved_to"));
        if (m.Sfi is not null)
            using (ui.FontSize(13 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
                ui.TextWrapped(SfiNextStartNote);
    }

    /// <summary>The most notices of one mod drawn in its details (newest first).</summary>
    internal const int NewsShown = 3;

    /// <summary>A hosted mod's newest notices (shroudforge.notifications.publish, ShroudForgeNews.cs): a ShroudForge mod
    /// cannot draw on the game's HUD, so this is where it tells the player what it found or what to press, and why it
    /// opens ShroudForge's window (here: this page). netjunk, Altar Cleanup's "Ask before cleaning up". A mod with
    /// shortcuts also gets the line that they work once the menu is closed: Kiln keeps every key press from ShroudForge
    /// while its menu has the keyboard (kiln-shroudforge key_feed.cpp), as ShroudForge counts none while its own window is
    /// in front (create_mod_api.rs is_game_focused).</summary>
    private static void News(UiDraw ui, CatalogMod m, string folder, SfPackage? p, float s, float k, Action<UiDraw, string, float> label)
    {
        var all = ShroudForgeNews.Cached();
        if (all.Count == 0) return;
        var dir = Path.GetFileName(folder);
        List<SfNews>? mine = null;
        foreach (var n in all)
        {
            if (n.ModId != m.Id && n.ModId != p?.Id && n.ModId != dir) continue;
            (mine ??= []).Add(n);
            if (mine.Count == NewsShown) break;
        }
        if (mine is null) return;
        label(ui, KilnText.T("shroudforge.news.label"), s);
        foreach (var n in mine)
        {
            using (ui.FontSize(13 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
                ui.Text(When(n.UpdatedAt));
            if (n.Title.Length > 0)
                using (ui.FontSize(17 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.GoldBright))
                    ui.TextWrapped(Clip(n.Title, MaxDrawnText));
            if (n.Message.Length > 0)
                using (ui.PushColor(UiColorSlot.Text, n.Level is "warn" or "warning" or "error" ? GameColors.Warning : GameColors.Text))
                    ui.TextWrapped(Clip(n.Message, MaxDrawnText));
            ui.Space(1, 4 * s);
        }
        if (p is not null && p.Settings.Any(x => x.Control == "keybind"))
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                ui.TextWrapped(KilnText.T("shroudforge.news.keys_after_menu", ("key", ShroudForgeMenu.KeyName)));
    }

    /// <summary>When a notice came, in the player's local time (the date too when it was not today).</summary>
    internal static string When(long unixSeconds)
    {
        if (unixSeconds is < 0 or > 253402300799) return "";
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        return at.Date == DateTimeOffset.Now.Date ? at.ToString("t", CultureInfo.CurrentCulture) : at.ToString("g", CultureInfo.CurrentCulture);
    }

    // Kiln's interpreter's rows
    // per package folder: the row last drawn for it was a kiln-backend start's (render thread writes, the writer reads)
    private static readonly ConcurrentDictionary<string, bool> NextStart = new(StringComparer.OrdinalIgnoreCase);

    internal const string SfiNextStartNote =
        "Kiln's interpreter reads this file when the game starts: switch and setting changes apply at the next start.";

    /// <summary>The row last drawn for <paramref name="folder"/> was a kiln-backend start's: its changes apply at the next
    /// start, whatever the package declares (no IO: the render thread and the writer ask).</summary>
    internal static bool NextStartOnly(string folder) => NextStart.TryGetValue(folder, out var v) && v;

    private static void SfiLines(UiDraw ui, SfiRow row, float s, float k, Action<UiDraw, string, float> label)
    {
        label(ui, row.Branch == SfiBranchKind.External ? "SHROUDFORGE'S OWN LOADER" : "KILN'S INTERPRETER", s);
        using (ui.FontSize(14 * k))
            foreach (var (text, warn) in row.Lines)
                using (ui.PushColor(UiColorSlot.Text, warn ? GameColors.Warning : GameColors.Muted))
                    ui.TextWrapped(Clip(text, MaxDrawnText));
    }

    // ShroudForge's runtime runs it in this process now (its state.json lists it active), whatever its effect reports
    private static bool Running(CatalogMod m) => m.Active;

    private static void Result(UiDraw ui, string folder, float k, bool problemsOnly = false)
    {
        (Func<string> Text, bool Ok) r;
        lock (Lock)
            if (!Results.TryGetValue(folder, out r) || (problemsOnly && r.Ok)) return;
        using (ui.FontSize(14 * k))
        using (ui.PushColor(UiColorSlot.Text, r.Ok ? GameColors.Ok : GameColors.Warning))
            ui.TextWrapped(r.Text());
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
            // in a kiln-backend start a live setting applies at the next start too
            var nextStart = !d.Live || NextStartOnly(folder);
            if (nextStart)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Attention)) ui.TextWrapped(KilnText.T("shroudforge.next_start"));
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

    // a value in a sentence: true and false as the words on and off
    private static string Shown(JsonNode? v) => v switch
    {
        JsonValue jv when jv.GetValueKind() == JsonValueKind.True => KilnText.T("shroudforge.value_on"),
        JsonValue jv when jv.GetValueKind() == JsonValueKind.False => KilnText.T("shroudforge.value_off"),
        _ => Plain(v),
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
                var none = KilnText.T("shroudforge.shortcut_off");
                using (ui.PushColor(UiColorSlot.Text, GameColors.Text))
                    ui.Text(KilnText.T("shroudforge.shortcut", ("key", item.KeyLabel.Length > 0 ? item.KeyLabel : none)));
                using (ui.FontSize(14 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                    ui.TextWrapped(KilnText.T("shroudforge.shortcut_help"));
                CaptureBox(ui, folder, item, none, s);
                // what the player types the way Kiln names keys ("Ctrl+G", "F5", "Mouse4") is saved in ShroudForge's form; text
                // that is no shortcut goes on as typed, and the check says why it is refused
                TextField(ui, folder, d, item.Text, width, text => JsonValue.Create(SfShortcuts.Normalize(text) ?? text));
                return;
            }
            case ("boolean", _) when d.Options.Count == 0:
            {
                var b = value is JsonValue bv && bv.GetValueKind() == JsonValueKind.True;
                var before = b;
                if (ui.Checkbox((b ? KilnText.T("shroudforge.on") : KilnText.T("shroudforge.off")) + "###v", ref b) && b != before) Edit(folder, d.Key, JsonValue.Create(b));
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

    // ------------------------------------------------------------------------------------- a shortcut's capture box ----

    private static string CaptureId(string folder, string key) => "sf\n" + folder + "\n" + key;

    /// <summary>A ShroudForge shortcut's capture box is waiting for a press, or a press just ended one (ImGui gets that key
    /// in a later frame): the page's section keys leave it alone (a captured Tab is the shortcut, not the next section).</summary>
    internal static bool Capturing =>
        UiEngine.CaptureId is { } id && id.StartsWith("sf\n", StringComparison.Ordinal) || UiEngine.CaptureJustEndedFor("sf\n");

    /// <summary>The shortcut as a button: click it, then press the key or mouse button (with Ctrl, Shift or Alt); Kiln saves
    /// ShroudForge's code for that key (UiEngine.Key takes the press on the window thread, kept from the game and the key
    /// bindings). Click it again to stop waiting; Backspace or Delete clears the shortcut, as Unbind does.</summary>
    private static void CaptureBox(UiDraw ui, string folder, Item item, string none, float s)
    {
        var d = item.Def;
        var id = CaptureId(folder, d.Key);
        var armed = UiEngine.CaptureId == id;
        if (armed) UiEngine.CaptureDrawn(id);
        var shown = armed ? KilnText.T("keys.press") : item.KeyLabel.Length > 0 ? item.KeyLabel : none;
        // (Enter or Space that just ended the box also reaches ImGui, which would press the focused box again)
        if (ui.Button(shown + "###capture", 240 * s) && !UiEngine.CaptureJustEnded(id))
        {
            if (armed) UiEngine.ArmCapture(null);
            else
            {
                var label = item.Label;
                UiEngine.ArmCapture(new UiEngine.CaptureBox(id, (vk, scan, mods) => Captured(folder, d.Key, label, vk, scan, mods)));
            }
        }
        if (item.Text.Length == 0) return;
        ui.SameLine();
        if (ui.SmallButton(KilnText.T("keys.unbind") + "###clear"))
        {
            if (armed) UiEngine.ArmCapture(null);
            Edit(folder, d.Key, JsonValue.Create(""));
        }
    }

    /// <summary>A press in the capture box of setting <paramref name="key"/> (window thread): saved in ShroudForge's form, or
    /// refused with the reason under the setting.</summary>
    internal static UiEngine.CaptureResult Captured(string folder, string key, string label, uint vk, uint scan, KeyMods mods)
    {
        if (vk == 0x1B) return UiEngine.CaptureResult.Done;  // Escape (when kiln.ui lets one through): stop waiting
        if (vk is 0x08 or 0x2E && mods == KeyMods.None)
        {
            Edit(folder, key, JsonValue.Create(""));  // Backspace, Delete: no shortcut
            return UiEngine.CaptureResult.Done;
        }
        var (binding, why) = SfShortcuts.FromPress(vk, scan, mods);
        if (binding is not null)
        {
            Edit(folder, key, JsonValue.Create(binding));
            return UiEngine.CaptureResult.Done;
        }
        var name = KeyNames.Name(vk);
        Func<string> text = why switch
        {
            SfShortcuts.Refusal.Wheel => () => KilnText.T("shroudforge.capture_wheel", ("setting", label)),
            SfShortcuts.Refusal.Pad => () => KilnText.T("shroudforge.capture_pad", ("setting", label)),
            _ => () => KilnText.T("shroudforge.capture_unknown", ("setting", label), ("key", name)),
        };
        lock (Lock)
        {
            Errors[(folder, key)] = text;
            _version++;
        }
        return why == SfShortcuts.Refusal.Wheel ? UiEngine.CaptureResult.Pass : UiEngine.CaptureResult.Refused;
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
                    ? KilnText.T("shroudforge.field_nul")
                    : KilnText.T("shroudforge.field_long", ("length", shown.Length)));
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
                var (label, typed) = (Clip(d.Label, 200), Clip(text, 200));
                Errors[(folder, d.Key)] = () => KilnText.T("shroudforge.not_a_number", ("setting", label), ("text", typed));
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
        // a kiln-backend start prunes ended launches' bridge folders once, on a worker
        SfiBridge.Start();
        ui.Space(1, 6 * s);
        var armed = _armed is { } x && x.Folder == folder && x.Action == a.Id && Environment.TickCount64 - x.AtMs < 6000;
        bool clicked;
        // only a mod ShroudForge runs now can take an action: a request left in its folder would run at a later start
        using (ui.PushColor(UiColorSlot.Text, running ? GameColors.Text : GameColors.Faint))
            clicked = ui.Button(armed ? KilnText.T("shroudforge.action.confirm", ("action", label)) : label);
        if (clicked && !running)
        {
            lock (Lock) Results[folder] = (() => KilnText.T("shroudforge.action.not_running", ("action", label)), false);
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
                        // (in a kiln-backend start Kiln's interpreter takes the request through
                        // its UI bridge, never ShroudForge's folder; SfiBridge.Send is null for any other start)
                        if (SfiBridge.Send(p, a.Id, label) is { } kiln)
                        {
                            lock (Lock) Results[folder] = kiln;
                            return;
                        }
                        Func<string> text;
                        var ok = true;
                        try
                        {
                            var game = GameDir();
                            // ShroudForge keeps a request of an active mod without a handler for ever and reads 32 per turn:
                            // one of Kiln's own per mod at a time
                            if (Waiting(game, p.Id))
                            {
                                ok = false;
                                text = () => KilnText.T("shroudforge.action.still_waiting", ("action", label), ("mod", p.Name));
                            }
                            else
                            {
                                QueueAction(game, p, a.Id);
                                text = () => KilnText.T("shroudforge.action.requested", ("action", label));
                                UiEngine.Log(LogLevel.Info, $"ShroudForge mod {p.Id}: action {a.Id} requested from the Kiln menu");
                            }
                        }
                        catch (Exception e)  // whatever the files or the config hold: the player gets a line, not a silent worker
                        {
                            ok = false;
                            var message = e.Message;
                            text = () => KilnText.T("shroudforge.action.failed", ("action", label), ("error", message));
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
        NextStart.Clear();
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
}

/// <summary>ShroudForge's menu shortcut (F9 by default) and <c>shroudforge.ui.toggle_modloader()</c> when no ShroudForge
/// Modloader window runs. kiln-shroudforge then says so in kiln/state/shroudforge.json (<c>menu: "kiln"</c>) and holds
/// the named event ShroudForge's runtime signals; both open the Kiln menu on the ShroudForge mods.</summary>
internal static class ShroudForgeMenu
{
    internal const string BindingId = "shroudforge.menu";
    private static int _started;
    private static KeyBinding? _binding;

    /// <summary>The key that toggles Kiln's ShroudForge page (F9 unless the player remapped it), for the page's words.</summary>
    internal static string KeyName => _binding is { Key: { IsNone: false } key } ? key.ToString() : "F9";

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
                // a kiln-backend start runs no ShroudForge runtime to make the menu's event: Kiln.Ui
                // makes it, and keeps the key for the whole start (the page also says why mods did
                // not run when the interpreter refused)
                if (OperatingSystem.IsWindows() && SfiBridge.ReadClaim(src).Mode == SfiMode.Kiln)
                {
                    TakeKiln();
                    return;
                }
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

    internal static string EventName => $"Local\\ShroudForge.ModMenu.Open.{Environment.ProcessId}";

    private static void Take(string? sfKey)
    {
        var h = OpenEventW(SYNCHRONIZE, false, EventName);
        if (h == 0)
        {
            UiEngine.Log(LogLevel.Warn, $"ShroudForge's menu event is missing (error {Marshal.GetLastPInvokeError()}): use Kiln's Mods entry");
            return;
        }
        var src = ModCatalog.Sources();
        Serve(sfKey, h, () =>
        {
            var f = ModCatalog.ReadState(src, "shroudforge.json");
            var claim = Read(f.Current ? f.Doc : null);
            return !(claim.Final || claim.MenuBy == "helper");
        }, hosted: true);
    }

    /// <summary>A kiln-backend start: Kiln.Ui makes ShroudForge's menu event (auto-reset: presses that come before a wait
    /// are one toggle) that kiln-sfi's shroudforge.ui.toggle_modloader() signals, and ShroudForge's key (F9 unless the
    /// player remaps it in Keys) toggles Kiln's ShroudForge page.</summary>
    private static void TakeKiln()
    {
        var h = CreateKilnEvent(out var existed);
        if (h == 0)
        {
            UiEngine.Log(LogLevel.Warn, $"Kiln's interpreter: ShroudForge's menu event could not be made (error {Marshal.GetLastPInvokeError()}): use Kiln's Mods entry");
            return;
        }
        if (existed)
            UiEngine.Log(LogLevel.Warn, "Kiln's interpreter: ShroudForge's menu event already existed in this process; Kiln.Ui uses it");
        Serve(null, h, () => true);
    }

    /// <summary>The kiln-mode menu event: auto-reset, not signalled. <paramref name="existed"/>: another owner made it first.</summary>
    internal static nint CreateKilnEvent(out bool existed)
    {
        var h = CreateEventW(0, false, false, EventName);
        existed = h != 0 && Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS;
        return h;
    }

    internal static uint WaitForTests(nint h, uint ms) => WaitForSingleObject(h, ms);
    internal static void CloseForTests(nint h) => CloseHandle(h);

    /// <summary>Holds ShroudForge's menu key while <paramref name="keep"/> says so, toggling the page at each signal.</summary>
    private static void Serve(string? sfKey, nint h, Func<bool> keep, bool hosted = false)
    {
        try
        {
            var mod = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's own binding: no mod's unload removes it
            try
            {
                // Do not silently steal F9 when SF explicitly has no key or a mouse/Meta binding we cannot translate.
                var key = SdlKeys.ToKilnKey(sfKey ?? "SDL:66") ?? "None";
                _binding = KeyBindings.Register(BindingId, key, Toggle, KilnText.T("shroudforge.binding"), "ShroudForge");
                _binding.WhileCapturing = true;
                var bound = _binding;
                KilnText.Changed += () => bound.Description = KilnText.T("shroudforge.binding");  // the Keys page in Kiln's language
                UiEngine.Log(LogLevel.Info, $"ShroudForge has no Modloader window: key {_binding.Key} and shroudforge.ui.toggle_modloader() toggle Kiln's ShroudForge page (remap in Keys; Mods is always available)");
            }
            finally { ModContext.CurrentMod = mod; }
            // a hosted start: the mods' new notices as Kiln's toasts (no ShroudForge window shows its feed)
            var toasts = hosted ? new SfNewsToasts(StartSeconds()) : null;
            while (true)
            {
                var wait = WaitForSingleObject(h, hosted ? 1000u : 2000u);
                if (wait != 0 && wait != 258) break;  // WAIT_TIMEOUT: check runtime lifecycle too
                if (!keep()) break;
                if (toasts is not null) PollNews(toasts, ShroudForgeNews.Reload, UiEngine.AddToast, Environment.TickCount64 / 1000.0);
                if (wait == 0 && hosted) ModToggled(ShroudForgeNews.Reload, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                else if (wait == 0) Toggle();
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
    public static void Toggle() => Toggled();

    /// <summary><see cref="Toggle"/>; true when it opened the page.</summary>
    private static bool Toggled()
    {
        var page = ModManagerHost.Page;
        if (Ui.MenuOpen && ShowsShroudForge(page))
        {
            page.CancelRequest();
            Ui.MenuOpen = false;
            return false;
        }
        Open();
        return true;
    }

    /// <summary>The page shows ShroudForge's mods, or will at its next frame: a press before the page drew a frame finds its
    /// request still pending, which is the ShroudForge page too.</summary>
    private static bool ShowsShroudForge(ModManagerPage page) =>
        page.PendingRequest == "shroudforge" || page.PendingRequest is null && page.Section == ModManagerSection.Mods && page.Filter.Kind == "shroudforge";

    public static void Open(string? mod = null)
    {
        ModManagerHost.Page.Request(new EngineFilter("shroudforge"), mod);
        Ui.MenuOpen = true;
    }

    /// <summary>When this game process started (Unix seconds); now when that cannot be read.</summary>
    private static long StartSeconds()
    {
        try { return new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToUnixTimeSeconds(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    /// <summary>ShroudForge's runtime signalled the menu event (a mod's shroudforge.ui.toggle_modloader()) in a hosted
    /// start: toggle the page as the shortcut does, and when that opened it, choose the mod whose notice came just now.
    /// A mod opens ShroudForge's window to show what it published (Altar Cleanup: what its scan found and "press the key
    /// again"); it may publish just before or just after it asks, so the notices are read again for a moment (menu
    /// thread: file IO is fine here).</summary>
    internal static void ModToggled(Func<IReadOnlyList<SfNews>> read, Func<long> nowSeconds, int tries = 8, int waitMs = 200)
    {
        var since = nowSeconds() - 3;
        if (!Toggled()) return;
        try
        {
            for (var i = 0; i < tries; i++)
            {
                if (ShroudForgeNews.Fresh(read(), nowSeconds(), since) is { } mod)
                {
                    // still open on ShroudForge: the player did not close it or go to another section meanwhile
                    if (Ui.MenuOpen && ShowsShroudForge(ModManagerHost.Page)) Open(mod);
                    return;
                }
                if (waitMs > 0) Thread.Sleep(waitMs);
            }
        }
        catch (Exception e) { NewsFailed(e); }  // the page is open: only the mod's row is not chosen
    }

    /// <summary>A hosted start's new notices as Kiln's toasts (the menu thread, at each wake: <paramref name="read"/> is
    /// ShroudForgeNews.Reload, <paramref name="toast"/> UiEngine.AddToast). Never throws: what goes wrong with
    /// ShroudForge's file must not end the thread that holds F9 and toggle_modloader().</summary>
    internal static void PollNews(SfNewsToasts toasts, Func<IReadOnlyList<SfNews>> read, Action<string, ToastKind, double> toast, double nowSeconds)
    {
        try
        {
            foreach (var n in toasts.Poll(read(), nowSeconds))
            {
                var (text, kind) = SfNewsToasts.Text(n);
                toast(text, kind, SfNewsToasts.Seconds);
            }
        }
        catch (Exception e) { NewsFailed(e); }
    }

    private static int _newsFailed;

    // said once a game in kiln.log; the shortcut and the page work on
    private static void NewsFailed(Exception e)
    {
        if (Interlocked.Exchange(ref _newsFailed, 1) == 0)
            UiEngine.Log(LogLevel.Warn, $"ShroudForge mod news: {e.GetType().Name}: {e.Message} (said once a game; F9 and toggle_modloader() still work)");
    }

    private const uint SYNCHRONIZE = 0x00100000;
    private const int ERROR_ALREADY_EXISTS = 183;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateEventW(nint attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32")]
    private static extern bool CloseHandle(nint h);

    [DllImport("kernel32")]
    private static extern uint WaitForSingleObject(nint h, uint ms);
}
