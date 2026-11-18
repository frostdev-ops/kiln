// SPDX-License-Identifier: MIT
// Kiln's own in-game texts (docs/kiln/translating-game.md). One JSON file per language, runtime/locales/<language>.json in
// the repository, shipped as <game>/kiln/locales/<language>.json; the format of a mod's locales (ModLocale: nested objects
// make dotted keys, {0} {name} {count:N0} placeholders, {{ }} for braces, plurals as .one / .other). English is built into
// Kiln.Api as well, so a missing or broken en.json never puts keys on screen. Which language: kiln.json ui.language ("auto",
// the default, follows the game's text language, Game.Language; a tag such as "zh-CN" forces one), looked up as the
// language itself, its main variety, then English (ModLocale.LookupOrder); a key a translation lacks is English. One table
// for both sides: Kiln.Ui hands kiln.ui the keys under "native." (set_texts) whenever the table changes.
namespace Kiln;

/// <summary>Kiln's own in-game texts in the language Kiln's UI shows. Any thread; lookups take no lock.</summary>
internal static class KilnText
{
    /// <summary>The setting that follows the game's text language.</summary>
    internal const string Auto = "auto";

    /// <summary>Keys under this prefix are drawn by kiln-ui.dll (the game's menus, the footer, the fallback window).</summary>
    internal const string NativePrefix = "native.";

    /// <summary>The key of a file's own language name ("English", "简体中文"), shown in the Language setting.</summary>
    internal const string LanguageNameKey = "language.name";

    /// <summary>Plural forms a key may end in (CLDR's categories). Kiln's English uses <c>.one</c> and <c>.other</c>.</summary>
    internal static readonly string[] PluralForms = ["zero", "one", "two", "few", "many", "other"];

    private const string EnglishResource = "Kiln.locales.en.json";
    private const int MaxReported = 256;

    // Table: every key in the language in use (English where it has none). Origin: the language each plural group came from
    // (its rule picks the form). Gated: the overlay cannot draw that language yet (its glyphs are loading or failed), so
    // the overlay's texts are English (EnglishTable) while the texts the game draws (Native) stay the language's.
    private sealed record State(string Setting, string Language, string GameRaw, Dictionary<string, string> Table,
                                Dictionary<string, string> Origin, Dictionary<string, string> EnglishTable,
                                Dictionary<string, string> EnglishOrigin, bool Gated, int Generation)
    {
        public Dictionary<string, string> Shown => Gated ? EnglishTable : Table;
        public Dictionary<string, string> ShownOrigin => Gated ? EnglishOrigin : Origin;
        public string ShownLanguage => Gated ? Fallback : Language;
    }

    private static volatile bool _glyphs = true;

    private static readonly object Gate = new();
    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
    private static volatile State? _state;
    private static ModLocale? _files;
    private static Dictionary<string, string>? _embedded;
    private static HashSet<string>? _pluralGroups;
    private static string? _directory;
    private static int _generation;

    /// <summary>The table changed (another language, a reload): Kiln.Ui hands kiln.ui the native texts again, pages drop
    /// what they made of the old texts. Raised on the thread that changed it, outside the lock.</summary>
    internal static event Action? Changed;

    /// <summary>Tests and the preview: the locales folder (default <c>&lt;kiln&gt;/locales</c>).</summary>
    internal static string Directory
    {
        get => _directory ?? Path.Combine(KilnPaths.Root, "locales");
        set
        {
            lock (Gate)
            {
                _directory = value;
                _files = null;
                _state = null;
            }
        }
    }

    /// <summary>kiln.json <c>ui.language</c> as set: <see cref="Auto"/> or a language tag.</summary>
    internal static string Setting => Current().Setting;

    /// <summary>The language Kiln's UI shows now, normalized ("zh-cn", "en-us"): the setting's, or the game's for auto.</summary>
    internal static string Language => Current().Language;

    /// <summary>Changes each time the table does: a page that keeps text it made compares it.</summary>
    internal static int Generation => Current().Generation;

    /// <summary>The overlay's texts are English for now although <see cref="Language"/> is not: Kiln.Ui's glyphs of that
    /// language (the game's own Chinese, Japanese or Korean font) are still loading, or could not be loaded.</summary>
    internal static bool Gated => Current().Gated;

    /// <summary>Whether drawing <paramref name="language"/> needs glyphs the overlay's Latin fonts lack (Chinese, Japanese,
    /// Korean): kiln.ui loads the game's own font of that language group first.</summary>
    internal static bool NeedsGlyphs(string language)
    {
        var l = ModLocale.Normalize(language);
        var dash = l.IndexOf('-');
        return (dash > 0 ? l[..dash] : l) is "zh" or "ja" or "ko";
    }

    /// <summary>Kiln.Ui, each overlay frame: whether the overlay can draw the language in use (kiln.ui's glyph state). Until it
    /// can, Kiln's overlay texts are English, so a player never sees boxes.</summary>
    internal static void Glyphs(bool ready)
    {
        if (_glyphs == ready) return;
        _glyphs = ready;
        var st = _state;
        if (st is null || st.Gated != (!ready && NeedsGlyphs(st.Language))) Rebuild(st?.Setting ?? Auto, st?.GameRaw, force: false);
    }

    /// <summary>The player's setting (kiln.json <c>ui.language</c>): <see cref="Auto"/> (also for null, empty or blank) or a
    /// tag. Any thread; a change applies at once.</summary>
    internal static void Configure(string? setting)
    {
        var s = string.IsNullOrWhiteSpace(setting) || string.Equals(setting.Trim(), Auto, StringComparison.OrdinalIgnoreCase) ? Auto : setting.Trim();
        Rebuild(s, null, force: false);
    }

    /// <summary>With the setting <see cref="Auto"/>: follows the game's text language (<paramref name="gameLanguage"/>, else
    /// <see cref="Game.Language"/>). Kiln.Ui calls it at each overlay frame; cheap while nothing changed.</summary>
    internal static void Refresh(string? gameLanguage = null)
    {
        var st = _state;
        if (st is not null && st.Setting != Auto) return;
        var game = gameLanguage ?? GameLanguageSource.Current();
        if (st is not null && string.Equals(st.GameRaw, game, StringComparison.Ordinal)) return;
        Rebuild(st?.Setting ?? Auto, game, force: false);
    }

    /// <summary>The files are read again at the next language change (Kiln.Ui, when the player changes the setting: a
    /// translator switches away and back to see an edit).</summary>
    internal static void ForgetFiles()
    {
        lock (Gate)
        {
            _files = null;
            Reported.Clear();
        }
    }

    /// <summary>Reads the files again (a translator's edit while the game runs).</summary>
    internal static void Reload()
    {
        lock (Gate)
        {
            _files = null;
            Reported.Clear();
        }
        var st = _state;
        Rebuild(st?.Setting ?? Auto, st?.GameRaw, force: true);
    }

    /// <summary>The text of <paramref name="key"/> (no placeholders filled). A key no file has is the key itself, logged
    /// once: the check in Kiln.Ui.Tests keeps that from shipping.</summary>
    internal static string T(string key)
    {
        if (Current().Shown.TryGetValue(key, out var text)) return text;
        Report(key);
        return key;
    }

    /// <summary>The text with <c>{0}</c>, <c>{1}</c> filled from <paramref name="args"/> (or <c>{name}</c> from one
    /// dictionary or object, as <see cref="ModLocale.Get"/>).</summary>
    internal static string T(string key, params object?[] args) => args.Length == 0 ? T(key) : ModLocale.Fill(T(key), args);

    /// <summary>The text with <c>{name}</c> placeholders filled: <c>T("mods.count", ("count", 3))</c>.</summary>
    internal static string T(string key, params (string Name, object? Value)[] values) =>
        values.Length == 0 ? T(key) : ModLocale.Fill(T(key), [Named(values)]);

    /// <summary>The plural form of <paramref name="key"/> for <paramref name="count"/> (<c>key.one</c> / <c>key.other</c>,
    /// the language's rule), with <c>{count}</c> and <paramref name="values"/> filled.</summary>
    internal static string Plural(string key, long count, params (string Name, object? Value)[] values)
    {
        var st = Current();
        var table = st.Shown;
        var form = PluralForm(st.ShownOrigin.TryGetValue(key, out var from) ? from : st.ShownLanguage, count);
        if (!table.TryGetValue(key + "." + form, out var text) && !table.TryGetValue(key + ".other", out text) &&
            !table.TryGetValue(key + ".one", out text) && !table.TryGetValue(key, out text))
        {
            Report(key + ".other");
            return key;
        }
        var all = new (string, object?)[values.Length + 1];
        all[0] = ("count", count);
        values.CopyTo(all, 1);
        return ModLocale.Fill(text, [Named(all)]);
    }

    /// <summary>Whether the table has <paramref name="key"/> (in any language of the lookup, English included).</summary>
    internal static bool Has(string key) => Current().Shown.ContainsKey(key);

    /// <summary>The keys under <see cref="NativePrefix"/> with their texts now (for kiln.ui). The game draws them with its own
    /// fonts, which have every language's glyphs, so they are the language's also while the overlay's texts are gated.
    /// kiln.ui picks a plural form as English does (.one for 1, else .other): for a group whose language has no singular
    /// form .one is its .other.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> Native()
    {
        var st = Current();
        var native = st.Table.Where(p => p.Key.StartsWith(NativePrefix, StringComparison.Ordinal)).ToDictionary(StringComparer.Ordinal);
        foreach (var (b, from) in st.Origin)
            if (b.StartsWith(NativePrefix, StringComparison.Ordinal) && PluralForm(from, 1) == "other" && native.TryGetValue(b + ".other", out var other))
                native[b + ".one"] = other;
        return native.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The languages Kiln has texts for: English, then the files in the locales folder, as normalized tags.</summary>
    internal static IReadOnlyList<string> Languages()
    {
        var files = Files().Languages;
        return new[] { "en" }.Concat(files.Where(l => l != "en")).ToArray();
    }

    /// <summary>A language's own name ("简体中文") as its file gives it (<see cref="LanguageNameKey"/>), else its main
    /// variety's ("en-us": English), else the tag.</summary>
    internal static string NameOf(string language)
    {
        var tag = ModLocale.Normalize(language);
        var dash = tag.IndexOf('-');
        foreach (var t in dash > 0 ? [tag, tag[..dash]] : new[] { tag })
        {
            if (Files().TableOf(t) is { } table && table.TryGetValue(LanguageNameKey, out var name) && name.Length > 0) return name;
            if (t == Fallback && English().TryGetValue(LanguageNameKey, out var en)) return en;
        }
        return Canonical(tag);
    }

    /// <summary>Kiln's English as built in (the file runtime/locales/en.json of this build).</summary>
    internal static IReadOnlyDictionary<string, string> English() => Embedded();

    /// <summary>"zh-cn" as it is written in kiln.json and in a file name: "zh-CN" (also "es-419", "zh-Hans").</summary>
    internal static string Canonical(string language)
    {
        var parts = ModLocale.Normalize(language).Split('-');
        for (var i = 1; i < parts.Length; i++)
            parts[i] = parts[i].Length switch
            {
                2 => parts[i].ToUpperInvariant(),
                4 => char.ToUpperInvariant(parts[i][0]) + parts[i][1..],
                _ => parts[i],
            };
        return string.Join('-', parts);
    }

    /// <summary>The CLDR plural category of <paramref name="n"/> among one / other: languages without a singular form (Chinese,
    /// Japanese, Korean, Vietnamese, Thai, Indonesian, Malay) always "other"; French and Brazilian Portuguese "one" for 0
    /// and 1; every other language "one" for 1. A translation that needs more forms (few, many) writes them; Kiln's
    /// English has one and other, so a missing form falls back to .other.</summary>
    internal static string PluralForm(string language, long n)
    {
        var l = ModLocale.Normalize(language);
        var dash = l.IndexOf('-');
        var primary = dash > 0 ? l[..dash] : l;
        if (primary is "zh" or "ja" or "ko" or "vi" or "th" or "id" or "ms" or "lo" or "my" or "km") return "other";
        if (primary is "fr" || l is "pt-br" || (primary is "pt" && l is not "pt-pt")) return n is 0 or 1 ? "one" : "other";
        return n == 1 ? "one" : "other";
    }

    // ---------------------------------------------------------------------------------------------- internals ----

    private static Dictionary<string, object?> Named((string Name, object? Value)[] values)
    {
        var d = new Dictionary<string, object?>(values.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var (n, v) in values) d[n] = v;
        return d;
    }

    private static State Current() => _state ?? Rebuild(Auto, null, force: false);

    private static State Rebuild(string setting, string? gameRaw, bool force)
    {
        State made;
        lock (Gate)
        {
            var old = _state;
            gameRaw ??= old?.GameRaw ?? SafeGame();
            var language = ModLocale.Normalize(setting == Auto ? gameRaw : setting);
            var gated = !_glyphs && NeedsGlyphs(language);
            if (!force && old is not null && old.Setting == setting && old.Language == language && old.Gated == gated)
            {
                if (old.GameRaw != gameRaw) _state = old with { GameRaw = gameRaw };
                return _state!;
            }
            var (table, origin) = !force && old is not null && old.Language == language ? (old.Table, old.Origin) : Build(language);
            var (english, englishOrigin) = !force && old is not null ? (old.EnglishTable, old.EnglishOrigin) : Build(Fallback);
            var changed = old is null || force || old.Language != language || old.Gated != gated;
            made = new State(setting, language, gameRaw, table, origin, english, englishOrigin, gated,
                             changed ? Interlocked.Increment(ref _generation) : old!.Generation);
            _state = made;
            if (!changed) return made;
        }
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log(LogLevel.Warn, "texts: a listener of the language change failed: " + e.Message);
        }
        return made;
    }

    private static string SafeGame()
    {
        try
        {
            return GameLanguageSource.Current();
        }
        catch (Exception)
        {
            return "en-us";
        }
    }

    // The table of one language: English (built in, then the file), then each language of the lookup from the farthest to
    // the language itself. A language that writes any form of a plural group replaces the whole group, so a Chinese .other
    // is never mixed with the English .one.
    private static (Dictionary<string, string> Table, Dictionary<string, string> Origin) Build(string language)
    {
        var table = new Dictionary<string, string>(Embedded(), StringComparer.Ordinal);
        var origin = PluralGroups().ToDictionary(g => g, _ => Fallback, StringComparer.Ordinal);
        var files = Files();
        var chain = ModLocale.LookupOrder(language);
        for (var i = chain.Count - 1; i >= 0; i--)
            if (files.TableOf(chain[i]) is { } layer) Over(table, origin, layer, chain[i]);
        return (table, origin);
    }

    private const string Fallback = ModLocale.Fallback;

    private static void Over(Dictionary<string, string> table, Dictionary<string, string> origin, IReadOnlyDictionary<string, string> layer, string tag)
    {
        var groups = PluralGroups();
        HashSet<string>? replaced = null;
        foreach (var key in layer.Keys)
            if (PluralBase(key) is { } b && groups.Contains(b)) (replaced ??= new(StringComparer.Ordinal)).Add(b);
        if (replaced is not null)
            foreach (var b in replaced)
            {
                foreach (var f in PluralForms) table.Remove(b + "." + f);
                origin[b] = tag;
            }
        foreach (var (k, v) in layer) table[k] = v;
    }

    private static string? PluralBase(string key)
    {
        var dot = key.LastIndexOf('.');
        return dot > 0 && PluralForms.Contains(key[(dot + 1)..]) ? key[..dot] : null;
    }

    // The plural groups: the keys English has as <base>.other.
    private static HashSet<string> PluralGroups()
    {
        if (_pluralGroups is { } have) return have;
        var g = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in Embedded().Keys)
            if (k.EndsWith(".other", StringComparison.Ordinal)) g.Add(k[..^6]);
        return _pluralGroups = g;
    }

    private static ModLocale Files()
    {
        if (_files is { } f) return f;
        lock (Gate) return _files ??= new ModLocale("kiln", Directory, null);
    }

    private static Dictionary<string, string> Embedded()
    {
        if (_embedded is { } e) return e;
        Dictionary<string, string>? table = null;
        try
        {
            using var s = typeof(KilnText).Assembly.GetManifestResourceStream(EnglishResource);
            if (s is not null)
            {
                using var r = new StreamReader(s);
                table = ModLocale.ParseText(r.ReadToEnd(), w => Log(LogLevel.Error, "texts: Kiln's built-in English: " + w));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Log(LogLevel.Error, "texts: Kiln's built-in English cannot be read: " + ex.Message);
        }
        return _embedded = table ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static void Report(string key)
    {
        lock (Reported)
            if (Reported.Count >= MaxReported || !Reported.Add(key)) return;
        Log(LogLevel.Warn, $"texts: Kiln has no text '{key}' (language {Language}); showing the key");
    }

    private static void Log(LogLevel level, string text)
    {
        try
        {
            Backend.Current.Log(0, level, text);
        }
        catch (Exception)
        {
            // no backend (a unit test without one): nothing to tell
        }
    }

    /// <summary>Tests: back to the start (no folder override, the setting auto, no listeners kept).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _directory = null;
            _files = null;
            _state = null;
            _glyphs = true;
            Reported.Clear();
        }
        Changed = null;
    }

    /// <summary>Tests: whether a key was reported missing.</summary>
    internal static bool WasReported(string key)
    {
        lock (Reported) return Reported.Contains(key);
    }
}
