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
    private sealed class Entry
    {
        public volatile SfPackage? Package;
        public volatile string? Error;
        public long ReadAtMs;
        public int Reading;
    }

    private static readonly ConcurrentDictionary<string, Entry> Packages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();
    // edits waiting for the writer: (folder, key; "" = the switch) -> value and when to write it
    private static readonly Dictionary<(string Folder, string Key), (JsonNode? Value, long DueMs)> Pending = new();
    private static readonly Dictionary<(string Folder, string Key), string> Errors = new();
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
    public static SfPackage? Get(string folder, out string? error)
    {
        var e = Packages.GetOrAdd(folder, _ => new Entry());
        if (Environment.TickCount64 - Interlocked.Read(ref e.ReadAtMs) > 1000 && Interlocked.CompareExchange(ref e.Reading, 1, 0) == 0)
            Task.Run(() => Refresh(folder, e));
        error = e.Error;
        return e.Package;
    }

    private static void Refresh(string folder, Entry e)
    {
        try
        {
            e.Package = ShroudForgePackages.Read(folder);
            e.Error = null;
        }
        catch (Exception x)
        {
            e.Package = null;  // never offer stale editable controls for a broken or removed file
            e.Error = x is SfEditException ? x.Message : $"its files could not be read: {x.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref e.ReadAtMs, Environment.TickCount64);
            Interlocked.Exchange(ref e.Reading, 0);
        }
    }

    /// <summary>Reads a package now (the preview, tests).</summary>
    internal static void Load(string folder)
    {
        var e = Packages.GetOrAdd(folder, _ => new Entry());
        Refresh(folder, e);
    }

    // ------------------------------------------------------------------------------------- writing ----

    /// <summary>Queues a change; <paramref name="settleMs"/> later (no newer change to the same value) the writer saves it.</summary>
    public static void Edit(string folder, string key, JsonNode? value, int settleMs = 0)
    {
        lock (Lock)
        {
            Pending[(folder, key)] = (value, Environment.TickCount64 + settleMs);
            Errors.Remove((folder, key));
        }
        if (Interlocked.Exchange(ref _writer, 1) == 0)
            new Thread(Writer) { IsBackground = true, Name = "kiln shroudforge settings" }.Start();
        Wake.Set();
    }

    private static void Writer()
    {
        while (true)
        {
            List<((string Folder, string Key) Id, JsonNode? Value)> due = [];
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
                try
                {
                    var p = id.Key.Length == 0
                        ? ShroudForgePackages.SetEnabled(id.Folder, value is JsonValue v && v.GetValueKind() == JsonValueKind.True)
                        : ShroudForgePackages.SetSetting(id.Folder, id.Key, value);
                    var e = Packages.GetOrAdd(id.Folder, _ => new Entry());
                    e.Package = p;
                    e.Error = null;
                    Interlocked.Exchange(ref e.ReadAtMs, Environment.TickCount64);
                    result = Saved(p, id.Key);
                    UiEngine.Log(LogLevel.Info, $"ShroudForge mod {p.Id}: {(id.Key.Length == 0 ? "switched " + (p.Enabled ? "on" : "off") : id.Key + " = " + ShroudForgePackages.Render(value))} " +
                                                $"(extended.mod.json, from the Kiln menu)");
                }
                catch (Exception x) // malformed/untrusted metadata must not kill the one settings writer
                {
                    ok = false;
                    result = x is SfEditException ? x.Message : "Could not save: " + x.Message;
                    lock (Lock) Errors[id] = result;
                    UiEngine.Log(LogLevel.Warn, $"ShroudForge mod in {id.Folder}: {(id.Key.Length == 0 ? "switch" : id.Key)} not saved: {result}");
                }
                lock (Lock)
                {
                    // a newer edit of the same value waits for its own turn
                    if (Pending.TryGetValue(id, out var cur) && ReferenceEquals(cur.Value, value)) Pending.Remove(id);
                    Results[id.Folder] = (result, ok);
                }
            }
            var wait = next == long.MaxValue ? 30_000 : (int)Math.Clamp(next - Environment.TickCount64, 1, 30_000);
            Wake.WaitOne(wait);
        }
    }

    private static string Saved(SfPackage p, string key)
    {
        if (key.Length == 0)
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

    private static JsonNode? Shown(string folder, SfSettingDef s)
    {
        lock (Lock)
            return Pending.TryGetValue((folder, s.Key), out var p) ? p.Value : s.Value;
    }

    private static string? ErrorOf(string folder, string key)
    {
        lock (Lock) return Errors.TryGetValue((folder, key), out var e) ? e : null;
    }

    /// <summary>The ShroudForge part of a mod's details: its switch, its settings by group, its buttons.</summary>
    public static void Draw(UiDraw ui, CatalogMod m, float s, float k, Action<UiDraw, string, float> label)
    {
        if (m.Folder is not { } folder) return;
        var p = Get(folder, out var error);
        label(ui, "SWITCH AND SETTINGS", s);
        if (p is null)
        {
            using (ui.PushColor(UiColorSlot.Text, error is null ? GameColors.Muted : GameColors.Warning))
                ui.TextWrapped(error is null ? "Reading its settings..." : $"Its settings cannot be shown: {error}");
            return;
        }
        using var id = ui.Id("sf." + folder);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var x in p.Settings) values[x.Key] = Shown(folder, x);

        // the switch
        bool on;
        lock (Lock) on = Pending.TryGetValue((folder, ""), out var pend) ? pend.Value is JsonValue v && v.GetValueKind() == JsonValueKind.True : p.Enabled;
        var was = on;
        if (ui.Checkbox(on ? "On###sf.switch" : "Off###sf.switch", ref on) && on != was) Edit(folder, "", JsonValue.Create(on));
        using (ui.FontSize(14 * k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
        {
            ui.TextWrapped(p.SwitchLive ? "ShroudForge switches it on or off while you play." : "Switching it on or off takes effect at the next game start (it changes game data when the game starts).");
            if (m.Source == "profile")
                ui.TextWrapped("Your Kiln profile lists this mod: the Kiln launcher sets this switch from the profile when it starts the game. To keep a change, make it in the profile too.");
        }
        Result(ui, folder, k);

        if (p.Settings.Count == 0 && !p.Groups.Any(g => g.Actions.Count > 0))
        {
            ui.Space(1, 6 * s);
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped("This mod has no settings.");
            return;
        }
        var running = Running(m);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in p.Groups)
        {
            ui.Space(1, 10 * s);
            using (ui.FontSize(19 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.GoldBright))
                ui.TextWrapped(g.Label);
            if (g.Description is { } gd)
                using (ui.FontSize(14 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                    ui.TextWrapped(gd);
            foreach (var key in g.Settings)
                if (p.Settings.FirstOrDefault(x => x.Key == key) is { } setting && placed.Add(key)) Setting(ui, folder, p, setting, values, s, k);
            foreach (var a in g.Actions) Action(ui, folder, p, a, running, s, k);
        }
        var rest = p.Settings.Where(x => !placed.Contains(x.Key)).ToArray();
        if (rest.Length > 0)
        {
            if (p.Groups.Count > 0)
            {
                ui.Space(1, 10 * s);
                using (ui.FontSize(19 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.GoldBright))
                    ui.Text("Other settings");
            }
            foreach (var x in rest) Setting(ui, folder, p, x, values, s, k);
        }
        ui.Space(1, 10 * s);
        using (ui.FontSize(13 * k))
        using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
            ui.TextWrapped("Saved to the mod's extended.mod.json, the file ShroudForge and the Kiln launcher read.");
    }

    private static bool Running(CatalogMod m) => m.State == ModRunState.Loaded;

    private static void Result(UiDraw ui, string folder, float k)
    {
        (string Text, bool Ok) r;
        lock (Lock)
            if (!Results.TryGetValue(folder, out r)) return;
        using (ui.FontSize(14 * k))
        using (ui.PushColor(UiColorSlot.Text, r.Ok ? GameColors.Ok : GameColors.Warning))
            ui.TextWrapped(r.Text);
    }

    private static void Setting(UiDraw ui, string folder, SfPackage p, SfSettingDef d, Dictionary<string, JsonNode?> values, float s, float k)
    {
        using var _ = ui.Id(d.Key);
        ui.Space(1, 8 * s);
        var enabled = ShroudForgePackages.IsEnabled(d.Key, p.Settings, values);
        var value = values.TryGetValue(d.Key, out var v) ? v : d.Value;
        using (ui.PushColor(UiColorSlot.Text, enabled ? GameColors.Text : GameColors.Faint))
            ui.TextWrapped(d.Label);
        if (!enabled)
        {
            var cond = d.EnabledWhen!;
            var ctl = p.Settings.FirstOrDefault(x => x.Key == cond.Setting);
            var want = ctl?.Options.FirstOrDefault(o => JsonNode.DeepEquals(o.Value, cond.EqualsValue))?.Label ?? Plain(cond.EqualsValue);
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Faint))
                ui.TextWrapped($"{Plain(value)}  (only used while {ctl?.Label ?? cond.Setting} is {want})");
        }
        else Widget(ui, folder, d, value, s, k);
        using (ui.FontSize(14 * k))
        {
            if (d.Description is { } desc)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(desc);
            if (!d.Live)
                using (ui.PushColor(UiColorSlot.Text, GameColors.Attention)) ui.TextWrapped("Applies at the next game start.");
            if (ErrorOf(folder, d.Key) is { } err)
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

    private static void Widget(UiDraw ui, string folder, SfSettingDef d, JsonNode? value, float s, float k)
    {
        var width = Math.Max(160 * s, ui.Cursor.AvailWidth * 0.7f);
        switch (d.Type, d.Control)
        {
            case (_, "keybind"):
            {
                using (ui.PushColor(UiColorSlot.Text, GameColors.Text))
                    ui.Text("Shortcut: " + (SdlKeys.Label(value is JsonValue kv && kv.TryGetValue<string>(out var ks) ? ks : "") ?? Plain(value)));
                using (ui.FontSize(14 * k))
                using (ui.PushColor(UiColorSlot.Text, GameColors.Muted))
                    ui.TextWrapped("Edit the SDL binding below (not key capture): Ctrl+SDL:66 is Ctrl+F9; Mouse:4 is mouse button 4. Empty disables it. SF checks keyboard availability.");
                TextField(ui, folder, d, value, width, text => JsonValue.Create(text));
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
                var chosen = value as JsonArray ?? [];
                for (var oi = 0; oi < d.Options.Count; oi++)
                {
                    var o = d.Options[oi];
                    var has = chosen.Any(x => JsonNode.DeepEquals(x, o.Value));
                    var before = has;
                    if (ui.Checkbox(o.Label + "###o" + oi, ref has) && has != before)
                    {
                        // ShroudForge keeps the options' order
                        var next = d.Options.Where(x => x == o ? has : chosen.Any(c => JsonNode.DeepEquals(c, x.Value))).Select(x => x.Value?.DeepClone()).ToArray();
                        Edit(folder, d.Key, new JsonArray(next));
                    }
                }
                return;
            }
            case (_, _) when d.Options.Count > 0:
            {
                var labels = d.Options.Select(o => o.Label).ToArray();
                var i = Math.Max(0, d.Options.ToList().FindIndex(o => JsonNode.DeepEquals(o.Value, value) ||
                    (ShroudForgePackages.NumberOf(o.Value) is { } a && a == ShroudForgePackages.NumberOf(value))));
                var before = i;
                ui.NextItemWidth(width);
                if (ui.Combo("##v", ref i, labels) && i != before) Edit(folder, d.Key, d.Options[i].Value?.DeepClone());
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
                TextField(ui, folder, d, value, width, text =>
                    d.Type == "integer" && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? JsonValue.Create(whole) :
                    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) ? JsonValue.Create(x) : null);
                return;
            }
            default:
                TextField(ui, folder, d, value, width, text => JsonValue.Create(text));
                return;
        }
    }

    // A text or number field: what the player types is kept until they leave the field, then saved.
    private static void TextField(UiDraw ui, string folder, SfSettingDef d, JsonNode? value, float width, Func<string, JsonNode?> parse)
    {
        var shown = value is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : Plain(value);
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
        // the field holds the declared length and the whole value with room to type: it never shows a value cut to fit
        // (and the cut text is never saved); a value too long for any field is shown, not edited
        var need = Math.Max(Encoding.UTF8.GetByteCount(text), Encoding.UTF8.GetByteCount(shown));
        var cap = multi ? MaxAreaBytes : MaxLineBytes;
        if (need > cap || text.Contains('\0') || shown.Contains('\0'))
        {
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(Clip(shown, MaxDrawnText));
            using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                ui.TextWrapped($"Too long to edit here ({shown.Length} characters): change it in the mod's extended.mod.json.");
            return;
        }
        ui.NextItemWidth(width);
        var max = (int)Math.Clamp(Math.Max((long)(d.MaxLength ?? 256) * 4, need + 1024L) + 1, 16, cap);
        var changed = multi ? ui.InputMultiline("##v", ref text, max, width, 90 * ui.Scale) : ui.InputText("##v", ref text, max);
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
        else lock (Lock) Errors[(folder, d.Key)] = $"{d.Label}: \"{text}\" is not a number";
    }

    private static void Action(UiDraw ui, string folder, SfPackage p, SfAction a, bool running, float s, float k)
    {
        using var _ = ui.Id("act." + a.Id);
        ui.Space(1, 6 * s);
        var armed = _armed is { } x && x.Folder == folder && x.Action == a.Id && Environment.TickCount64 - x.AtMs < 6000;
        if (ui.Button(armed ? "Confirm: " + a.Label : a.Label))
        {
            if (a.Confirm is not null && !armed) _armed = (folder, a.Id, Environment.TickCount64);
            else
            {
                _armed = null;
                var game = GameDir();
                Task.Run(() =>
                {
                    string text;
                    var ok = true;
                    try
                    {
                        ShroudForgePackages.QueueAction(game, p, a.Id);
                        text = running ? $"Requested: {a.Label}." : $"Requested: {a.Label}. ShroudForge can only handle actions while the mod is active.";
                        UiEngine.Log(LogLevel.Info, $"ShroudForge mod {p.Id}: action {a.Id} requested from the Kiln menu");
                    }
                    catch (Exception e) when (e is SfEditException or IOException or UnauthorizedAccessException)
                    {
                        ok = false;
                        text = $"{a.Label} could not be sent: {e.Message}";
                    }
                    lock (Lock) Results[folder] = (text, ok);
                });
            }
        }
        if (armed && a.Confirm is { } c)
            using (ui.FontSize(14 * k))
            using (ui.PushColor(UiColorSlot.Text, GameColors.Attention))
                ui.TextWrapped(c);
    }

    internal static string GameDir() => ModCatalog.Sources().GameDir;

    internal static void ResetForTests()
    {
        lock (Lock)
        {
            Pending.Clear();
            Errors.Clear();
            Results.Clear();
            Typing.Clear();
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
