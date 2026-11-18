// SPDX-License-Identifier: MIT
// Kiln's own graphics tools (0.2.0-dev.29.18, docs/kiln/graphics.md): kiln.json's "graphics" section (the frame cap, the
// DLSS render preset and render scale, the readout), read at start and again when the file changes (checked every 2
// seconds), changed on the Kiln menu's Graphics page (applied at once, written back to kiln.json half a second after the
// last change with a value edit that keeps the file's comments), and handed to kiln.ui (graphics_set). kiln.ui reads the
// file itself at its own start too: the frame cap works from the first frame, and whether DLSS gets hooked is decided
// before the game sets DLSS up (native/services/ui/graphics.cpp).
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>How the DLSS render size is chosen (kiln.json <c>graphics.dlssScale</c>; KILN_UI_DLSS_SCALE_*).</summary>
internal enum DlssScaleMode
{
    /// <summary>DLSS's own for the game's DLSS mode.</summary>
    Game = 0,
    /// <summary>One render scale per DLSS mode.</summary>
    PerMode = 1,
    /// <summary>One render scale for every DLSS mode.</summary>
    Custom = 2,
}

/// <summary>kiln.json's <c>graphics</c> section, clamped.</summary>
internal sealed record GraphicsSettings(int FpsCap, string DlssPreset, DlssScaleMode DlssScale, float RenderScale, float QualityScale,
    float BalancedScale, float PerformanceScale, float UltraPerformanceScale, bool Readout)
{
    /// <summary>DLSS's own scales by mode (the defaults of the per-mode setting).</summary>
    public const float DlssQuality = 0.667f, DlssBalanced = 0.58f, DlssPerformance = 0.5f, DlssUltraPerformance = 0.333f;
    public const float MinScale = 0.33f, MaxScale = 1f;
    public const int MinFps = 20, MaxFps = 500;

    public static readonly GraphicsSettings Default = new(0, "default", DlssScaleMode.Game, 0.67f, DlssQuality, DlssBalanced, DlssPerformance,
        DlssUltraPerformance, false);

    /// <summary>The render presets Kiln offers: the ones nvngx_dlss.dll 310.9.1 (the game's) has transformer models for.</summary>
    public static readonly string[] Presets = ["default", "J", "K", "L", "M"];

    /// <summary>NVSDK_NGX_DLSS_Hint_Render_Preset of a name: 0 default, J 10 .. M 13.</summary>
    public static uint PresetValue(string preset) => preset.Length == 1 && preset[0] is >= 'J' and <= 'M' ? (uint)(preset[0] - 'A' + 1) : 0;

    public static int ClampFps(int fps) => fps <= 0 ? 0 : Math.Clamp(fps, MinFps, MaxFps);

    private static float Scale(float v, float fallback) => float.IsFinite(v) ? Math.Clamp(v, MinScale, MaxScale) : fallback;

    /// <summary>"default", or a preset Kiln offers by its name in any case; null for anything else.</summary>
    public static string? PresetOf(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        if (t.Equals("default", StringComparison.OrdinalIgnoreCase)) return "default";
        var u = t.ToUpperInvariant();
        return Presets.Contains(u) ? u : null;
    }

    public GraphicsSettings Clamped() => this with
    {
        FpsCap = ClampFps(FpsCap),
        DlssPreset = PresetOf(DlssPreset) ?? "default",
        DlssScale = Enum.IsDefined(DlssScale) ? DlssScale : DlssScaleMode.Game,
        RenderScale = Scale(RenderScale, 0.67f),
        QualityScale = Scale(QualityScale, DlssQuality),
        BalancedScale = Scale(BalancedScale, DlssBalanced),
        PerformanceScale = Scale(PerformanceScale, DlssPerformance),
        UltraPerformanceScale = Scale(UltraPerformanceScale, DlssUltraPerformance),
    };

    /// <summary>A DLSS setting off its default.</summary>
    public bool DlssSet => DlssPreset != "default" || DlssScale != DlssScaleMode.Game;

    public static string ScaleName(DlssScaleMode m) => m switch { DlssScaleMode.PerMode => "perMode", DlssScaleMode.Custom => "custom", _ => "game" };
}

/// <summary>kiln.json "graphics": reading, saving, and handing it to kiln.ui.</summary>
internal static unsafe class GraphicsTools
{
    /// <summary>kiln.json's lock, the look's too (PlayerLook.FileLock): both write their sections of the one file.</summary>
    internal static object FileLock => PlayerLook.FileLock;
    // _current and _savePending change together under it, and kiln.ui is told under it: a reload of the file never
    // overwrites a change the player just made, and kiln.ui ends with the setting in use
    private static readonly object StateLock = new();
    private static volatile GraphicsSettings _current = GraphicsSettings.Default;
    private static int _started;
    private static Timer? _watch, _save;
    private static DateTime _stamp;
    private static volatile bool _savePending;

    /// <summary>The setting in use.</summary>
    internal static GraphicsSettings Current => _current;

    /// <summary>Tests: kiln.json somewhere else.</summary>
    internal static string? PathOverride;

    internal static string KilnJson => PathOverride ?? Path.Combine(KilnPaths.Root, "kiln.json");

    /// <summary>This kiln.ui has Kiln's graphics tools (its graphics tail).</summary>
    internal static bool Available
    {
        get
        {
            var t = UiNative.GraphicsTable;
            return t != null && t->GraphicsSet != null && t->GraphicsStatusFn != null;
        }
    }

    /// <summary>Reads kiln.json, hands the settings to kiln.ui and watches the file (once; a game client with the tail).</summary>
    internal static void Start()
    {
        if (!Available) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Reload(quiet: true);  // kiln.ui read the same file at its start (and logged it)
        _watch = new Timer(_ => Poll(), null, 2000, 2000);
    }

    private static void Poll()
    {
        try
        {
            if (_savePending) return;
            if (Stamp() == _stamp) return;
            Reload(quiet: false);
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Debug, "graphics: " + e.Message);
        }
    }

    private static DateTime Stamp()
    {
        try { return File.Exists(KilnJson) ? File.GetLastWriteTimeUtc(KilnJson) : default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    /// <summary>Reads kiln.json and uses its settings (the 2-second look at the file; tests call it at once).</summary>
    internal static void Reload(bool quiet)
    {
        GraphicsSettings s;
        lock (FileLock)
        {
            _stamp = Stamp();
            s = Read(KilnJson, quiet ? null : w => UiEngine.Log(LogLevel.Warn, "kiln.json: " + w));
        }
        lock (StateLock)
        {
            // a change on the Graphics page since: it is saved soon, and the file read here is older than it
            if (_savePending || s == _current) return;
            _current = s;
            ApplyLocked();
        }
        if (!quiet) UiEngine.Log(LogLevel.Info, $"graphics: {Describe(s)} (kiln.json)");
    }

    internal static string Describe(GraphicsSettings s)
    {
        var parts = new List<string>();
        if (s.FpsCap > 0) parts.Add($"frame cap {s.FpsCap} fps");
        if (s.DlssPreset != "default") parts.Add($"DLSS preset {s.DlssPreset}");
        if (s.DlssScale == DlssScaleMode.Custom) parts.Add(string.Create(CultureInfo.InvariantCulture, $"DLSS render scale {s.RenderScale:0.##} for every mode"));
        else if (s.DlssScale == DlssScaleMode.PerMode)
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"DLSS render scale per mode {s.QualityScale:0.###}/{s.BalancedScale:0.###}/{s.PerformanceScale:0.###}/{s.UltraPerformanceScale:0.###}"));
        if (s.Readout) parts.Add("readout on");
        return parts.Count == 0 ? "all off" : string.Join(", ", parts);
    }

    /// <summary>kiln.json's <c>graphics</c> section (the defaults for what it lacks; values clamped).</summary>
    /// <remarks>A file that is not valid JSON keeps the settings in use (at start, the defaults), as one that cannot be read
    /// does: a half-saved or mistyped file does not turn the player's frame cap off in the middle of a game.</remarks>
    internal static GraphicsSettings Read(string path, Action<string>? warn = null)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return GraphicsSettings.Default;
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"not readable ({e.Message}): Kiln's graphics settings stay as they were at start");
            return _current;
        }
        return Parse(text, warn, _current);
    }

    /// <summary>kiln.json's text: its <c>graphics</c> section, clamped; <paramref name="whenInvalid"/> (else the defaults)
    /// for text that is not JSON.</summary>
    internal static GraphicsSettings Parse(string text, Action<string>? warn = null, GraphicsSettings? whenInvalid = null)
    {
        try
        {
            if (text.Trim().Length == 0) return GraphicsSettings.Default;
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("graphics", out var g) || g.ValueKind != JsonValueKind.Object)
                return GraphicsSettings.Default;
            var d = GraphicsSettings.Default;
            float Num(string key, float def) => g.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? (float)x : def;
            var fps = g.TryGetProperty("fpsCap", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetDouble(out var fd) && double.IsFinite(fd)
                ? (int)Math.Clamp(Math.Round(fd), -1, 100000) : d.FpsCap;
            var preset = d.DlssPreset;
            if (g.TryGetProperty("dlssPreset", out var p) && p.ValueKind != JsonValueKind.Null)
            {
                preset = GraphicsSettings.PresetOf(p.ValueKind == JsonValueKind.String ? p.GetString() : null) ?? "default";
                if (preset == "default" && !(p.ValueKind == JsonValueKind.String && GraphicsSettings.PresetOf(p.GetString()) == "default"))
                    warn?.Invoke("graphics.dlssPreset is not default, J, K, L or M: default");
            }
            var mode = d.DlssScale;
            if (g.TryGetProperty("dlssScale", out var m) && m.ValueKind != JsonValueKind.Null)
            {
                var name = m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                mode = name switch { "game" => DlssScaleMode.Game, "perMode" => DlssScaleMode.PerMode, "custom" => DlssScaleMode.Custom, _ => DlssScaleMode.Game };
                if (name is not ("game" or "perMode" or "custom")) warn?.Invoke("graphics.dlssScale is not game, perMode or custom: game");
            }
            var readout = g.TryGetProperty("readout", out var r) && r.ValueKind == JsonValueKind.True;
            return new GraphicsSettings(fps, preset, mode, Num("dlssRenderScale", d.RenderScale), Num("dlssQualityScale", d.QualityScale),
                Num("dlssBalancedScale", d.BalancedScale), Num("dlssPerformanceScale", d.PerformanceScale),
                Num("dlssUltraPerformanceScale", d.UltraPerformanceScale), readout).Clamped();
        }
        catch (JsonException e)
        {
            warn?.Invoke($"not valid JSON ({e.Message}): Kiln's graphics settings stay {(whenInvalid is null ? "at their defaults" : "as they are")}");
            return whenInvalid ?? GraphicsSettings.Default;
        }
    }

    /// <summary>The settings as kiln.ui takes them.</summary>
    internal static UiNative.Graphics ToNative(GraphicsSettings s)
    {
        UiNative.Graphics g = default;
        g.Size = (uint)sizeof(UiNative.Graphics);
        g.FpsCap = (uint)s.FpsCap;
        g.DlssPreset = GraphicsSettings.PresetValue(s.DlssPreset);
        g.DlssScale = (uint)s.DlssScale;
        g.CustomScale = s.RenderScale;
        g.ModeScale[0] = s.QualityScale;
        g.ModeScale[1] = s.BalancedScale;
        g.ModeScale[2] = s.PerformanceScale;
        g.ModeScale[3] = s.UltraPerformanceScale;
        g.Readout = s.Readout ? 1u : 0u;
        return g;
    }

    /// <summary>Hands the setting in use to kiln.ui. Any thread.</summary>
    internal static void Apply()
    {
        lock (StateLock) ApplyLocked();
    }

    private static void ApplyLocked()
    {
        var t = UiNative.GraphicsTable;
        if (t == null || t->GraphicsSet == null) return;
        var g = ToNative(_current);
        t->GraphicsSet(&g);
    }

    /// <summary>What kiln.ui's tools do now, or null on a kiln.ui without them.</summary>
    internal static UiNative.GraphicsStatus? Status()
    {
        var t = UiNative.GraphicsTable;
        if (t == null || t->GraphicsStatusFn == null) return null;
        UiNative.GraphicsStatus st = default;
        st.Size = (uint)sizeof(UiNative.GraphicsStatus);
        return t->GraphicsStatusFn(&st) == UiNative.Ok ? st : null;
    }

    /// <summary>Tests and the preview: this setting in use at once (kiln.ui is told), not saved.</summary>
    internal static void Use(GraphicsSettings s)
    {
        lock (StateLock)
        {
            _current = s.Clamped();
            ApplyLocked();
        }
    }

    /// <summary>The player changed a setting on the Graphics page: applied at once, saved to kiln.json half a second after the
    /// last change (a worker).</summary>
    internal static void Change(GraphicsSettings s)
    {
        s = s.Clamped();
        lock (StateLock)
        {
            if (s == _current) return;
            _savePending = true;
            _current = s;
            ApplyLocked();
            _save ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _save.Change(500, Timeout.Infinite);
        }
    }

    private static void SaveNow()
    {
        var s = _current;
        try
        {
            lock (FileLock)
            {
                s = _current;
                if (Save(KilnJson, s) is { } error) UiEngine.Log(LogLevel.Warn, $"graphics: not saved to kiln.json: {error}");
                else UiEngine.Log(LogLevel.Info, $"graphics: {Describe(s)} saved to kiln.json");
                _stamp = Stamp();
            }
        }
        finally
        {
            // a change while this one was written: its own save follows, and the file is not read back before it
            lock (StateLock)
                if (s == _current) _savePending = false;
        }
    }

    /// <summary>Writes the setting into kiln.json's <c>graphics</c> section (the rest of the file as it was); null, or what
    /// went wrong.</summary>
    internal static string? Save(string path, GraphicsSettings s)
    {
        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var updated = Splice(text, s);
            if (updated == text) return null;
            var tmp = path + ".kiln-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    f.Write(new UTF8Encoding(false).GetBytes(updated));
                    f.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SfEditException or JsonException)
        {
            return e.Message;
        }
    }

    private static string Num(float v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The JSON(C) text with the <c>graphics</c> section set to <paramref name="s"/>: values spliced in place, missing
    /// keys (or the section) added; comments and layout kept. Throws <see cref="SfEditException"/> for text that is not a
    /// JSON object.</summary>
    internal static string Splice(string text, GraphicsSettings s)
    {
        var values = new (string Key, string Json)[]
        {
            ("fpsCap", s.FpsCap.ToString(CultureInfo.InvariantCulture)), ("dlssPreset", ShroudForgePackages.Quote(s.DlssPreset)),
            ("dlssScale", ShroudForgePackages.Quote(GraphicsSettings.ScaleName(s.DlssScale))), ("dlssRenderScale", Num(s.RenderScale)),
            ("dlssQualityScale", Num(s.QualityScale)), ("dlssBalancedScale", Num(s.BalancedScale)),
            ("dlssPerformanceScale", Num(s.PerformanceScale)), ("dlssUltraPerformanceScale", Num(s.UltraPerformanceScale)),
            ("readout", s.Readout ? "true" : "false"),
        };
        if (text.Trim().Length == 0)
        {
            const string nl = "\n";
            return "{" + nl + "  \"graphics\": {" + nl + string.Join("," + nl, values.Select(v => "    " + ShroudForgePackages.Quote(v.Key) + ": " + v.Json)) +
                   nl + "  }" + nl + "}" + nl;
        }
        var root = JsonSpans.Parse(text);
        if (root.Kind != '{') throw new SfEditException("kiln.json is not a JSON object");
        var g = root.Get("graphics");
        if (g is null || g.Kind != '{')
        {
            var indent = root.Members.Count > 0 ? JsonSpans.LineIndent(text, root.Members[^1].KeyStart) : "";
            var obj = new JsonObject();
            foreach (var (k, json) in values) obj[k] = JsonNode.Parse(json);
            var rendered = ShroudForgePackages.Render(obj, indent, text.Contains("\r\n", StringComparison.Ordinal));
            if (g is null) return JsonSpans.InsertMember(text, root, "graphics", rendered);
            return text[..g.Start] + rendered + text[g.End..];
        }
        foreach (var (key, json) in values)
        {
            root = JsonSpans.Parse(text);
            g = root.Get("graphics")!;
            var v = g.Get(key);
            if (v is null) text = JsonSpans.InsertMember(text, g, key, json);
            else if (!SameValue(text[v.Start..v.End], json)) text = text[..v.Start] + json + text[v.End..];
        }
        return text;
    }

    // 0.5 and 0.50 are the same value: the player's spelling stays
    private static bool SameValue(string a, string b) =>
        a == b || (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                   double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y);

    internal static void ResetForTests()
    {
        _current = GraphicsSettings.Default;
        _started = 0;
        _watch?.Dispose();
        _watch = null;
        _save?.Dispose();
        _save = null;
        _savePending = false;
        PathOverride = null;
    }
}

/// <summary>The Kiln menu's Graphics section: Kiln's frame cap, DLSS settings and readout, and what they do now.</summary>
internal static unsafe class GraphicsPage
{
    // KilnUiGraphicsStatus.flags (kiln_ui.h)
    internal const uint CapActive = 1, DlssHooked = 2, DlssActive = 4, DlssRestart = 8, DlssPreset = 16, DlssScaled = 32, Counting = 64,
        DlssLimited = 128;
    // KilnUiGraphicsStatus.aside_* bits (kiln_ui.h KILN_UI_GFX_ASIDE_*), in the order they are named
    internal static readonly (uint Bit, string Key)[] AsideKeys =
    [
        (1, "optiscaler"), (2, "streamline"), (4, "vkfg"), (8, "dlsstweaks"), (16, "nvngx"), (32, "ngx_patched"), (64, "overlay_off"),
    ];

    private static int _capDrag = -1;  // the frame cap slider's value while it is held (applied when let go)
    private static int _textsGeneration = -1;
    private static byte[] _presetItems = [], _scaleItems = [];

    /// <summary>The tools a status steps aside for, by name ("OptiScaler, DLSSTweaks").</summary>
    internal static string AsideNames(uint bits) =>
        string.Join(", ", AsideKeys.Where(a => (bits & a.Bit) != 0).Select(a => KilnText.T("graphics.aside." + a.Key)));

    /// <summary>The DLSS mode's name (NVSDK_NGX_PerfQuality_Value).</summary>
    internal static string ModeName(int quality) => quality switch
    {
        0 => KilnText.T("graphics.mode.performance"), 1 => KilnText.T("graphics.mode.balanced"), 2 => KilnText.T("graphics.mode.quality"),
        3 => KilnText.T("graphics.mode.ultra_performance"), 4 => KilnText.T("graphics.mode.ultra_quality"), 5 => "DLAA", _ => "?",
    };

    internal static string PresetName(uint preset) => preset == 0 ? KilnText.T("graphics.preset.default") :
        preset is >= 1 and <= 26 ? ((char)('A' + preset - 1)).ToString() : preset.ToString(CultureInfo.InvariantCulture);

    /// <summary>The status lines the page shows (plain: tests).</summary>
    internal static IReadOnlyList<string> StatusLines(GraphicsSettings s, UiNative.GraphicsStatus? status)
    {
        var lines = new List<string>();
        if (status is not { } st)
        {
            lines.Add(KilnText.T("graphics.status.unavailable"));
            return lines;
        }
        if (s.FpsCap > 0)
            lines.Add(st.AsideCap != 0 ? KilnText.T("graphics.status.cap_aside", ("fps", s.FpsCap), ("tools", AsideNames(st.AsideCap)))
                : KilnText.T("graphics.status.cap_on", ("fps", s.FpsCap)));
        if ((st.Flags & Counting) != 0 && st.RenderedFps > 0)
            lines.Add(KilnText.T("graphics.status.fps", ("rendered", MathF.Round(st.RenderedFps)), ("output", MathF.Round(st.OutputFps)),
                ("ms", st.FrameMs.ToString("0.0", CultureInfo.InvariantCulture))));
        if (st.AsideDlss != 0)
            lines.Add(KilnText.T("graphics.status.dlss_aside", ("tools", AsideNames(st.AsideDlss))));
        else if ((st.Flags & DlssActive) != 0)
        {
            lines.Add(KilnText.T("graphics.status.dlss_active", ("mode", ModeName(st.DlssQuality)), ("render", $"{st.RenderWidth}x{st.RenderHeight}"),
                ("output", $"{st.DlssOutputWidth}x{st.DlssOutputHeight}"), ("preset", PresetName(st.DlssPreset))));
            if ((st.Flags & DlssLimited) != 0) lines.Add(KilnText.T("graphics.status.dlss_limited", ("mode", ModeName(st.DlssQuality))));
        }
        else if ((st.Flags & DlssHooked) != 0)
            lines.Add(KilnText.T("graphics.status.dlss_idle"));
        else if ((st.Flags & DlssRestart) == 0 && (s.DlssSet || s.Readout))
            lines.Add(KilnText.T("graphics.status.dlss_none"));
        if ((st.Flags & DlssRestart) != 0) lines.Add(KilnText.T("graphics.status.restart"));
        return lines;
    }

    private static void Texts()
    {
        var g = KilnText.Generation;
        if (g == _textsGeneration) return;
        _presetItems = UiDraw.ComboItems([KilnText.T("graphics.preset.default"), "J", "K", "L", "M"]);
        _scaleItems = UiDraw.ComboItems([KilnText.T("graphics.scale.game"), KilnText.T("graphics.scale.per_mode"), KilnText.T("graphics.scale.custom")]);
        _textsGeneration = g;
    }

    private static int Percent(float v) => (int)MathF.Round(v * 100);

    public static void Draw(UiDraw ui)
    {
        var s = ui.Scale;
        if (!GraphicsTools.Available)
        {
            ui.TextWrapped(KilnText.T("graphics.status.unavailable"));
            return;
        }
        Texts();
        var cur = GraphicsTools.Current;
        var st = GraphicsTools.Status();
        var w = 380 * s;
        void Caption(string text) => ui.Text(GameColors.Gold.WithAlpha(210), text);
        void Hint(string text)
        {
            using (ui.PushColor(UiColorSlot.Text, GameColors.Muted)) ui.TextWrapped(text);
        }

        // ---- the frame cap ----
        Caption(KilnText.T("graphics.cap.caption"));
        var capOn = cur.FpsCap > 0;
        if (ui.Checkbox(KilnText.T("graphics.cap.on") + "##kiln.gfx.cap.on", ref capOn))
            GraphicsTools.Change(cur with { FpsCap = capOn ? 60 : 0 });
        cur = GraphicsTools.Current;
        if (cur.FpsCap > 0)
        {
            var fps = _capDrag >= 0 ? _capDrag : cur.FpsCap;
            ui.NextItemWidth(w);
            ui.Slider(KilnText.T("graphics.cap.fps") + "##kiln.gfx.cap.fps", ref fps, GraphicsSettings.MinFps, 360);
            var it = ui.LastItem;
            if (it.Active) _capDrag = fps;
            else if (_capDrag >= 0 || fps != cur.FpsCap)
            {
                GraphicsTools.Change(GraphicsTools.Current with { FpsCap = fps });  // let go (or a keyboard / gamepad step)
                _capDrag = -1;
            }
        }
        Hint(KilnText.T("graphics.cap.hint"));
        ui.Space(1, 12 * s);

        // ---- DLSS ----
        cur = GraphicsTools.Current;
        Caption(KilnText.T("graphics.dlss.caption"));
        var preset = Math.Max(0, Array.IndexOf(GraphicsSettings.Presets, cur.DlssPreset));
        ui.NextItemWidth(w);
        if (ui.Combo(KilnText.T("graphics.dlss.preset") + "##kiln.gfx.preset", ref preset, _presetItems, GraphicsSettings.Presets.Length))
            GraphicsTools.Change(cur with { DlssPreset = GraphicsSettings.Presets[preset] });
        Hint(KilnText.T("graphics.dlss.preset_hint"));
        cur = GraphicsTools.Current;
        var mode = (int)cur.DlssScale;
        ui.NextItemWidth(w);
        if (ui.Combo(KilnText.T("graphics.dlss.scale") + "##kiln.gfx.scale", ref mode, _scaleItems, 3))
            GraphicsTools.Change(cur with { DlssScale = (DlssScaleMode)mode });
        cur = GraphicsTools.Current;
        if (cur.DlssScale == DlssScaleMode.Custom)
        {
            var v = Percent(cur.RenderScale);
            ui.NextItemWidth(w);
            if (ui.Slider(KilnText.T("graphics.dlss.render_scale") + "##kiln.gfx.custom", ref v, 33, 100)) GraphicsTools.Change(cur with { RenderScale = v / 100f });
        }
        else if (cur.DlssScale == DlssScaleMode.PerMode)
        {
            ScaleSlider(ui, w, 2, cur.QualityScale, v => GraphicsTools.Current with { QualityScale = v });
            ScaleSlider(ui, w, 1, cur.BalancedScale, v => GraphicsTools.Current with { BalancedScale = v });
            ScaleSlider(ui, w, 0, cur.PerformanceScale, v => GraphicsTools.Current with { PerformanceScale = v });
            ScaleSlider(ui, w, 3, cur.UltraPerformanceScale, v => GraphicsTools.Current with { UltraPerformanceScale = v });
        }
        Hint(KilnText.T("graphics.dlss.scale_hint"));
        ui.Space(1, 12 * s);

        // ---- the readout ----
        Caption(KilnText.T("graphics.readout.caption"));
        var readout = GraphicsTools.Current.Readout;
        if (ui.Checkbox(KilnText.T("graphics.readout.on") + "##kiln.gfx.readout", ref readout))
            GraphicsTools.Change(GraphicsTools.Current with { Readout = readout });
        Hint(KilnText.T("graphics.readout.hint"));
        ui.Space(1, 12 * s);

        // ---- what the tools do now ----
        Caption(KilnText.T("graphics.status.caption"));
        foreach (var line in StatusLines(GraphicsTools.Current, st)) ui.TextWrapped(line);
        ui.TextDisabled(KilnText.T("graphics.saved"));
    }

    // One DLSS mode's render scale (NVSDK_NGX_PerfQuality_Value `quality`), in percent.
    private static void ScaleSlider(UiDraw ui, float w, int quality, float value, Func<float, GraphicsSettings> with)
    {
        var v = Percent(value);
        ui.NextItemWidth(w);
        if (ui.Slider(KilnText.T("graphics.dlss.mode_scale", ("mode", ModeName(quality))) + "##kiln.gfx.mode." + quality, ref v, 33, 100))
            GraphicsTools.Change(with(v / 100f));
    }

    internal static void ResetForTests()
    {
        _capDrag = -1;
        _textsGeneration = -1;
        _presetItems = [];
        _scaleItems = [];
    }
}
