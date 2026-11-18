// SPDX-License-Identifier: MIT
// The player's look of Kiln's UI (0.2.0-dev.29.16, docs/kiln/ui.md "Looks and themes"): kiln.json's "ui" section (look,
// theme, background, opacity, scale), read at start and again when the file changes (checked every 2 seconds), changed on
// Kiln's menu (Mods > Kiln settings > Look; shown at once, written back to kiln.json half a second after the last change with
// a value edit that keeps the file's comments), and handed to kiln.ui (set_look). KilnLook gives Kiln's own pages the
// palette of the look in use.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>kiln.json's <c>ui</c> section, clamped. <paramref name="Language"/>: the language of Kiln's own texts, "auto" (the
/// game's) or a tag (UiLanguage, KilnText).</summary>
internal sealed record LookSettings(string Look, string Theme, float Background, float Opacity, float Scale, string Language = KilnText.Auto)
{
    public static readonly LookSettings Default = new("game", "", 0.55f, 1f, 1f);
    public static readonly string[] Looks = ["game", "translucent", "kiln", "theme"];

    public LookSettings Clamped() => this with
    {
        Look = Looks.Contains(Look) ? Look : "game",
        Theme = Theme ?? "",
        Background = Clamp(Background, 0.1f, 1f, 0.55f),
        Opacity = Clamp(Opacity, 0.3f, 1f, 1f),
        Scale = Clamp(Scale, 0.75f, 1.5f, 1f),
        Language = LanguageOf(Language),
    };

    /// <summary>"auto" for null, blank or any spelling of auto; else the tag as written canonically ("zh-CN").</summary>
    public static string LanguageOf(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), KilnText.Auto, StringComparison.OrdinalIgnoreCase)
            ? KilnText.Auto
            : KilnText.Canonical(language);

    private static float Clamp(float v, float lo, float hi, float fallback) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    public UiLook Kind => Look switch { "translucent" => UiLook.Translucent, "kiln" => UiLook.Kiln, "theme" => UiLook.Theme, _ => UiLook.Game };

    public UiPlayerLook ToPublic() => new(Kind, Kind == UiLook.Theme && Theme.Length > 0 ? Theme : null, Background, Opacity, Scale);
}

internal static unsafe class PlayerLook
{
    /// <summary>Held while kiln.json is read or written in this process: by the look and by Kiln's graphics settings
    /// (GraphicsTools), which write their sections of the same file (each reads the file, changes its section, writes it
    /// back: two at once would lose one's change).</summary>
    internal static readonly object FileLock = new();
    private static volatile LookSettings _current = LookSettings.Default;
    private static int _started;
    private static Timer? _watch, _save;
    private static DateTime _stamp;
    private static volatile bool _savePending;

    /// <summary>The setting in use.</summary>
    internal static LookSettings Current => _current;

    /// <summary>Tests: kiln.json somewhere else.</summary>
    internal static string? PathOverride;

    internal static string KilnJson => PathOverride ?? Path.Combine(KilnPaths.Root, "kiln.json");

    /// <summary>This kiln.ui takes the player's look (its looks tail).</summary>
    internal static bool Available
    {
        get
        {
            var t = UiNative.Looks;
            return t != null && t->SetLook != null;
        }
    }

    /// <summary>Reads kiln.json, hands the look to kiln.ui and watches the file (once; a game client with the looks tail).</summary>
    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;  // also without the looks tail: the language is read here
        Reload();
        _watch = new Timer(_ => Poll(), null, 2000, 2000);
    }

    private static void Poll()
    {
        try
        {
            if (_savePending) return;
            var stamp = Stamp();
            if (stamp == _stamp) return;
            Reload();
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Debug, "look: " + e.Message);
        }
    }

    private static DateTime Stamp()
    {
        try { return File.Exists(KilnJson) ? File.GetLastWriteTimeUtc(KilnJson) : default; }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    private static void Reload()
    {
        LookSettings s;
        lock (FileLock)
        {
            _stamp = Stamp();
            s = Read(KilnJson, w => UiEngine.Log(LogLevel.Warn, "kiln.json: " + w));
        }
        if (s == _current) return;
        _current = s;
        UiEngine.Log(LogLevel.Info, $"look: {Describe(s)} (kiln.json)");
        Apply();
        UiLanguage.Apply(s.Language);
    }

    internal static string Describe(LookSettings s) =>
        $"{s.Look}{(s.Kind == UiLook.Theme ? " " + s.Theme : "")}{(s.Kind == UiLook.Translucent ? $", background {s.Background:0.##}" : "")}, " +
        $"opacity {s.Opacity:0.##}, scale {s.Scale:0.##}";

    /// <summary>kiln.json's <c>ui</c> section (the defaults for what it lacks; values clamped). <paramref name="warn"/> hears
    /// about a file it cannot read and an unknown look.</summary>
    internal static LookSettings Read(string path, Action<string>? warn = null)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return LookSettings.Default;
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"not readable ({e.Message}): the default look");
            return LookSettings.Default;
        }
        return Parse(text, warn);
    }

    internal static LookSettings Parse(string text, Action<string>? warn = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("ui", out var ui) || ui.ValueKind != JsonValueKind.Object)
                return LookSettings.Default;
            var d = LookSettings.Default;
            string Str(string key, string def) => ui.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? def : def;
            float Num(string key, float def) => ui.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) ? (float)x : def;
            var look = Str("look", d.Look).Trim().ToLowerInvariant();
            if (!LookSettings.Looks.Contains(look))
            {
                warn?.Invoke($"ui.look \"{look}\" is not game, translucent, kiln or theme: the game's look");
                look = "game";
            }
            return new LookSettings(look, Str("theme", "").Trim(), Num("background", d.Background), Num("opacity", d.Opacity), Num("scale", d.Scale),
                Str("language", KilnText.Auto)).Clamped();
        }
        catch (JsonException e)
        {
            warn?.Invoke($"not valid JSON ({e.Message}): the default look");
            return LookSettings.Default;
        }
    }

    /// <summary>Hands the setting to kiln.ui. A theme that is not offered (its mod is not loaded, or not yet) is the game's look
    /// until it is. Any thread.</summary>
    internal static void Apply()
    {
        var t = UiNative.Looks;
        if (t == null || t->SetLook == null) return;
        var s = _current;
        UiNative.Look l = default;
        l.Size = (uint)sizeof(UiNative.Look);
        l.Kind = (uint)s.Kind;
        if (s.Kind == UiLook.Theme)
        {
            var theme = UiThemes.Find(s.Theme);
            if (theme is { OfferedToPlayer: true }) l.Theme = theme.Handle;
            else l.Kind = (uint)UiLook.Game;
        }
        l.Background = s.Background;
        l.Opacity = s.Opacity;
        l.Scale = s.Scale;
        t->SetLook(&l);
    }

    /// <summary>Tests and the preview: this setting in use at once (kiln.ui is told), not saved.</summary>
    internal static void Use(LookSettings s)
    {
        _current = s.Clamped();
        Apply();
        UiLanguage.Apply(_current.Language);
    }

    /// <summary>A theme came or went: the player's may be among them.</summary>
    internal static void ThemesChanged()
    {
        if (_current.Kind == UiLook.Theme) Apply();
    }

    /// <summary>The player changed the look on Kiln's menu: shown at once, saved to kiln.json half a second after the last
    /// change (a worker).</summary>
    internal static void Change(LookSettings s)
    {
        s = s.Clamped();
        if (s == _current) return;
        _current = s;
        Apply();
        UiLanguage.Apply(s.Language);
        _savePending = true;
        lock (FileLock)
        {
            _save ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _save.Change(500, Timeout.Infinite);
        }
    }

    private static void SaveNow()
    {
        try
        {
            lock (FileLock)
            {
                var s = _current;
                if (Save(KilnJson, s) is { } error) UiEngine.Log(LogLevel.Warn, $"look: not saved to kiln.json: {error}");
                else UiEngine.Log(LogLevel.Info, $"look: {Describe(s)} saved to kiln.json");
                _stamp = Stamp();
            }
        }
        finally
        {
            _savePending = false;
        }
    }

    /// <summary>Writes the setting into kiln.json's <c>ui</c> section (the rest of the file as it was); null, or what went
    /// wrong.</summary>
    internal static string? Save(string path, LookSettings s)
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

    private static string Num(float v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The JSON(C) text with the <c>ui</c> section set to <paramref name="s"/>: values spliced in place, missing keys
    /// (or the section) added; comments and layout kept. Throws <see cref="SfEditException"/> for text that is not a JSON
    /// object.</summary>
    internal static string Splice(string text, LookSettings s)
    {
        var values = new (string Key, string Json)[]
        {
            ("look", ShroudForgePackages.Quote(s.Look)), ("theme", ShroudForgePackages.Quote(s.Theme)),
            ("background", Num(s.Background)), ("opacity", Num(s.Opacity)), ("scale", Num(s.Scale)),
            ("language", ShroudForgePackages.Quote(s.Language)),
        };
        // the language is written once it is not auto (or the file has it already): a file that never chose one keeps its
        // other lines as they were
        if (s.Language == KilnText.Auto && !HasLanguage(text)) values = values[..^1];
        if (text.Trim().Length == 0)
        {
            var nl = "\n";
            return "{" + nl + "  \"ui\": {" + nl + string.Join("," + nl, values.Select(v => "    " + ShroudForgePackages.Quote(v.Key) + ": " + v.Json)) +
                   nl + "  }" + nl + "}" + nl;
        }
        var root = JsonSpans.Parse(text);
        if (root.Kind != '{') throw new SfEditException("kiln.json is not a JSON object");
        var ui = root.Get("ui");
        if (ui is null || ui.Kind != '{')
        {
            var indent = root.Members.Count > 0 ? JsonSpans.LineIndent(text, root.Members[^1].KeyStart) : "";
            var obj = new JsonObject();
            foreach (var (k, json) in values) obj[k] = JsonNode.Parse(json);
            var rendered = ShroudForgePackages.Render(obj, indent, text.Contains("\r\n", StringComparison.Ordinal));
            if (ui is null) return JsonSpans.InsertMember(text, root, "ui", rendered);
            return text[..ui.Start] + rendered + text[ui.End..];
        }
        foreach (var (key, json) in values)
        {
            root = JsonSpans.Parse(text);
            ui = root.Get("ui")!;
            var v = ui.Get(key);
            if (v is null) text = JsonSpans.InsertMember(text, ui, key, json);
            else if (text[v.Start..v.End] != json) text = text[..v.Start] + json + text[v.End..];
        }
        return text;
    }

    private static bool HasLanguage(string text)
    {
        try
        {
            if (text.Trim().Length == 0) return false;
            var root = JsonSpans.Parse(text);
            return root.Kind == '{' && root.Get("ui") is { Kind: '{' } ui && ui.Get("language") is not null;
        }
        catch (SfEditException)
        {
            return false;
        }
    }

    internal static void ResetForTests()
    {
        _current = LookSettings.Default;
        _started = 0;
        _watch?.Dispose();
        _watch = null;
        _save?.Dispose();
        _save = null;
        _savePending = false;
        PathOverride = null;
    }
}

/// <summary>The palette of the look in use, for Kiln's own pages (<see cref="GameColors"/>) and <see cref="UiDraw.Palette"/>.</summary>
internal static unsafe class KilnLook
{
    private static UiPalette _player = UiPalette.Neutral;
    private static bool _gameColors, _gameFont;
    private static (byte State, byte Language)? _glyphs;

    /// <summary>The player's palette as of this frame's start.</summary>
    internal static UiPalette Player => Override ?? _player;

    /// <summary>The game's colours (else the stand-in) and font were read from the game, as of this frame's start.</summary>
    internal static bool GameColorsRead => _gameColors;
    internal static bool GameFontRead => _gameFont;

    /// <summary>kiln.ui's glyphs of a Chinese, Japanese or Korean language as of this frame's start (UiNative.Glyphs*, the
    /// keen::LanguageId); null when this kiln.ui does not say.</summary>
    internal static (byte State, byte Language)? Glyphs => _glyphs;

    /// <summary>Tests and the preview: a palette in place of kiln.ui's.</summary>
    internal static UiPalette? Override;

    /// <summary>The start of an overlay frame (render thread, before any look is pushed): the player's palette.</summary>
    internal static void BeginFrame()
    {
        if (Info() is { } i)
        {
            _player = i.Palette;
            _gameColors = i.GameColors;
            _gameFont = i.GameFont;
            _glyphs = (i.Glyphs, i.GlyphLanguage);
        }
    }

    /// <summary>Inside a frame: the palette of the look pushed now (the player's when none is).</summary>
    internal static UiPalette PaletteHere() => Override ?? Info()?.Palette ?? _player;

    private static (UiPalette Palette, bool GameColors, bool GameFont, byte Glyphs, byte GlyphLanguage)? Info()
    {
        var t = UiNative.Looks;
        if (t == null || t->GetLookInfo == null) return null;
        UiNative.LookInfo i = default;
        i.Size = (uint)sizeof(UiNative.LookInfo);
        if (t->GetLookInfo(&i) != UiNative.Ok) return null;
        var n = (int)Math.Min(i.RoleCount, 16);
        return (UiPalette.From(new ReadOnlySpan<uint>(i.Roles, n)), i.GameColors != 0, i.GameFont != 0, i.Glyphs, i.GlyphLanguage);
    }
}

/// <summary>Mods > Kiln settings > Look: the player's look of Kiln's UI.</summary>
internal static class LookPage
{
    private static int _scaleDrag = -1;  // the size slider's value while it is held (applied when let go: the page would move under it)
    private static string _itemsKey = "\0";
    private static byte[] _items = [];
    private static string[] _ids = [];

    /// <summary>The look choices: the three looks, then the themes mods offer (and the player's theme while it is not).</summary>
    internal static (string[] Labels, string[] Ids) Choices(LookSettings cur, IReadOnlyList<UiTheme> offered)
    {
        var labels = new List<string> { KilnText.T("settings.look.game"), KilnText.T("settings.look.translucent"), KilnText.T("settings.look.kiln") };
        var ids = new List<string> { "game", "translucent", "kiln" };
        foreach (var t in offered)
        {
            labels.Add(KilnText.T("settings.look.theme", ("theme", t.DisplayName), ("mod", t.ModName)));
            ids.Add("theme:" + t.Id);
        }
        if (cur.Kind == UiLook.Theme && cur.Theme.Length > 0 && !offered.Any(t => t.Id == cur.Theme))
        {
            labels.Add(KilnText.T("settings.look.theme_missing", ("theme", cur.Theme)));
            ids.Add("theme:" + cur.Theme);
        }
        return (labels.ToArray(), ids.ToArray());
    }

    internal static string IdOf(LookSettings s) => s.Kind == UiLook.Theme ? "theme:" + s.Theme : s.Look;

    internal static LookSettings Pick(LookSettings cur, string id) =>
        id.StartsWith("theme:", StringComparison.Ordinal) ? cur with { Look = "theme", Theme = id[6..] } : cur with { Look = id };

    public static void Draw(UiDraw ui, float s, Action<UiDraw, string, float> caption)
    {
        if (!PlayerLook.Available) return;
        var cur = PlayerLook.Current;
        var (labels, ids) = Choices(cur, UiThemes.Offered());
        var key = string.Join("\n", labels);
        if (key != _itemsKey)
        {
            _itemsKey = key;
            _items = UiDraw.ComboItems(labels);
            _ids = ids;
        }
        caption(ui, KilnText.T("settings.look.caption"), s);
        var index = Math.Max(0, Array.IndexOf(_ids, IdOf(cur)));
        ui.NextItemWidth(360 * s);
        if (ui.Combo(KilnText.T("settings.look.look") + "##kiln.look", ref index, _items, _ids.Length) && index < _ids.Length)
            PlayerLook.Change(Pick(cur, _ids[index]));
        cur = PlayerLook.Current;
        if (cur.Kind == UiLook.Translucent)
        {
            var bg = (int)MathF.Round(cur.Background * 100);
            ui.NextItemWidth(360 * s);
            if (ui.Slider(KilnText.T("settings.look.background") + "##kiln.look.bg", ref bg, 10, 100)) PlayerLook.Change(cur with { Background = bg / 100f });
        }
        cur = PlayerLook.Current;
        var op = (int)MathF.Round(cur.Opacity * 100);
        ui.NextItemWidth(360 * s);
        if (ui.Slider(KilnText.T("settings.look.opacity") + "##kiln.look.opacity", ref op, 30, 100)) PlayerLook.Change(cur with { Opacity = op / 100f });
        var size = _scaleDrag >= 0 ? _scaleDrag : (int)MathF.Round(PlayerLook.Current.Scale * 100);
        ui.NextItemWidth(360 * s);
        ui.Slider(KilnText.T("settings.look.size") + "##kiln.look.scale", ref size, 75, 150);
        var it = ui.LastItem;
        if (it.Active) _scaleDrag = size;
        else if (_scaleDrag >= 0 || size != (int)MathF.Round(PlayerLook.Current.Scale * 100))
        {
            PlayerLook.Change(PlayerLook.Current with { Scale = size / 100f });  // let go (or a keyboard / gamepad step)
            _scaleDrag = -1;
        }
        ui.TextWrapped(KilnLook.GameColorsRead ? KilnText.T("settings.look.game_read") : KilnText.T("settings.look.game_not_read"));
        ui.TextDisabled(KilnText.T("settings.look.saved"));
        ui.Space(1, 14 * s);
        LanguagePage.Draw(ui, s, caption);
    }

    internal static void ResetForTests()
    {
        LanguagePage.ResetForTests();
        _scaleDrag = -1;
        _itemsKey = "\0";
        _items = [];
        _ids = [];
    }
}

/// <summary>Mods > Kiln settings > Language (beside Look): the language of Kiln's own texts. "Automatic" follows the game's
/// text language; each language Kiln has texts for is listed in its own name. A change shows at once and is saved with the
/// look (kiln.json <c>ui.language</c>).</summary>
internal static class LanguagePage
{
    private static string _itemsKey = "\0";
    private static byte[] _items = [];
    private static string[] _ids = [];

    /// <summary>The choices: auto (naming the game's language), English, then every locale file, in their own names; the
    /// player's setting too while no file has it.</summary>
    internal static (string[] Labels, string[] Ids) Choices(string setting)
    {
        var labels = new List<string> { KilnText.T("settings.language.auto", ("language", KilnText.NameOf(KilnText.Setting == KilnText.Auto ? KilnText.Language : GameLanguage.Current))) };
        var ids = new List<string> { KilnText.Auto };
        foreach (var tag in KilnText.Languages())
        {
            labels.Add(KilnText.NameOf(tag));
            ids.Add(KilnText.Canonical(tag));
        }
        if (setting != KilnText.Auto && !ids.Contains(setting, StringComparer.OrdinalIgnoreCase))
        {
            labels.Add(KilnText.T("settings.language.missing", ("language", setting)));
            ids.Add(setting);
        }
        return (labels.ToArray(), ids.ToArray());
    }

    public static void Draw(UiDraw ui, float s, Action<UiDraw, string, float> caption)
    {
        var cur = PlayerLook.Current;
        var (labels, ids) = Choices(cur.Language);
        var key = string.Join("\n", labels);
        if (key != _itemsKey)
        {
            _itemsKey = key;
            _items = UiDraw.ComboItems(labels);
            _ids = ids;
        }
        caption(ui, KilnText.T("settings.language.caption"), s);
        var index = Math.Max(0, Array.FindIndex(_ids, i => string.Equals(i, cur.Language, StringComparison.OrdinalIgnoreCase)));
        ui.NextItemWidth(360 * s);
        if (ui.Combo(KilnText.T("settings.language.language") + "##kiln.language", ref index, _items, _ids.Length) && index < _ids.Length)
            PlayerLook.Change(PlayerLook.Current with { Language = _ids[index] });
        ui.TextDisabled(KilnText.T("settings.language.saved"));
        ui.Space(1, 14 * s);
    }

    internal static void ResetForTests()
    {
        _itemsKey = "\0";
        _items = [];
        _ids = [];
    }
}
