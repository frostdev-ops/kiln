// SPDX-License-Identifier: MIT
// Mod localisation (ShroudForge's locales/*.json, i18n.get and locale(); Enshwick's texts per language and language()). A
// mod ships its strings in <mod folder>/locales/<language>.json; Mod.Text(key, args) answers in the language of the game,
// falling back to the language's main variety and then to English. A dedicated server answers in English. Usage:
// docs/kiln/modding/lifecycle.md.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>
/// A mod's strings in the languages it ships: one JSON file per language in <c>locales/</c> beside <c>kiln.mod.json</c>
/// (<c>en.json</c>, <c>de.json</c>, <c>pt-BR.json</c>). Get it as <see cref="ModContext.Locale"/>; <see cref="Mod.Text"/>
/// is the short way to ask.
/// </summary>
/// <remarks>
/// <para>File format: one JSON object (comments and trailing commas are fine). Values are strings. A nested object makes
/// dotted keys: <c>{ "shop": { "buy": "Buy" } }</c> is <c>shop.buy</c>. Placeholders are <c>{0}</c> (the first argument),
/// <c>{name}</c> (a named value, see <see cref="Get"/>) and <c>{count:N0}</c> (a format for numbers). <c>{{</c> and <c>}}</c>
/// write a brace.</para>
/// <para>Which text answers: the file of the game's language (<see cref="Game.Language"/>, "de-de"), then of its main
/// variety ("de"), then <c>en</c>. A few regional languages look in a closer relative first (<see cref="LookupOrder"/>).
/// A key no file has comes back as the key itself, so a missing text shows up on screen, and is logged once.</para>
/// <para>Any thread. The files are read when first needed and again on <see cref="Reload"/> or a mod reload.</para>
/// </remarks>
public sealed class ModLocale
{
    /// <summary>The language every other falls back to.</summary>
    public const string Fallback = "en";

    private static readonly Regex Tag = new("^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

    private readonly string _modId;
    private readonly string _directory;
    private readonly Logger? _log;
    private readonly object _lock = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private volatile Tables? _tables;

    internal ModLocale(string modId, string directory, Logger? log)
    {
        _modId = modId;
        _directory = directory;
        _log = log;
    }

    /// <summary>The <c>locales</c> folder (it need not exist).</summary>
    public string Directory => _directory;

    /// <summary>The languages that have a file, as lower-case tags ("de", "pt-br"), sorted.</summary>
    public IReadOnlyList<string> Languages => Load().Languages;

    /// <summary>Every key any file has, sorted.</summary>
    public IReadOnlyList<string> Keys => Load().Keys;

    /// <summary>The text of <paramref name="key"/> in the language of the game (<see cref="Game.Language"/>), with its
    /// placeholders filled. <paramref name="args"/>: values for <c>{0}</c>, <c>{1}</c>; or one dictionary
    /// (<c>IReadOnlyDictionary&lt;string, object?&gt;</c>) or one object (<c>new { count = 3 }</c>) for <c>{count}</c>.
    /// A placeholder without a value stays as written.</summary>
    public string Get(string key, params object?[] args) => GetIn(Game.Language, key, args);

    /// <summary>Like <see cref="Get"/> for another language, e.g. one a player chose in your own settings.</summary>
    public string GetIn(string language, string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (TryGetRaw(language, key, out var text)) return args.Length == 0 && text.IndexOf('{') < 0 && text.IndexOf('}') < 0 ? text : Fill(text, args);
        Report(key, language);
        return key;
    }

    /// <summary>The text of <paramref name="key"/> in the language of the game, placeholders as written. False when no
    /// file has it.</summary>
    public bool TryGet(string key, out string text) => TryGetRaw(Game.Language, key, out text);

    /// <summary>Some file in the game language's lookup order has <paramref name="key"/>.</summary>
    public bool Has(string key) => TryGetRaw(Game.Language, key, out _);

    /// <summary>The keys the English file has that <paramref name="language"/> has no text for in its own files: its file
    /// and its main variety's ("de-at" and "de"), without English. A language without a file is owed every key; English
    /// is owed none. What a translator still has to do; the lint rule KL212 reports the same.</summary>
    public IReadOnlyList<string> Missing(string language)
    {
        var t = Load();
        if (!t.ByLanguage.TryGetValue(Fallback, out var english)) return [];
        var tag = Normalize(language);
        if (tag == Fallback || tag.StartsWith("en-", StringComparison.Ordinal)) return [];  // English itself
        var own = LookupOrder(tag).Where(x => x is not (Fallback or "en-us")).ToArray();
        return english.Keys.Where(k => !own.Any(x => t.ByLanguage.TryGetValue(x, out var table) && table.ContainsKey(k)))
            .Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads the files again.</summary>
    public void Reload()
    {
        lock (_lock) _reported.Clear();
        _tables = null;
        Load();
    }

    /// <summary>The language tags tried for <paramref name="language"/>, in order: the language itself, its closest
    /// relatives, its main variety, English. "de-de" gives <c>de-de, de, en, en-us</c>; "es-mx" gives
    /// <c>es-mx, es-419, es, en, en-us</c>; "nb-no" gives <c>nb-no, nb, no, en, en-us</c>.</summary>
    public static IReadOnlyList<string> LookupOrder(string language)
    {
        var l = Normalize(language);
        var chain = new List<string>(6);
        void Add(string t)
        {
            if (!chain.Contains(t)) chain.Add(t);
        }
        Add(l);
        var dash = l.IndexOf('-');
        var primary = dash > 0 ? l[..dash] : l;
        if (l is "es-mx") Add("es-419");  // Latin American Spanish before Spain's
        Add(primary);
        if (primary is "nb") Add("no");  // Norwegian has two codes in the game
        else if (primary is "no") Add("nb");
        Add(Fallback);
        Add("en-us");
        return chain;
    }

    /// <summary>"pt_BR", "PT-br" and " pt-br " are all "pt-br". Empty gives "en-us".</summary>
    public static string Normalize(string? language)
    {
        var s = (language ?? "").Trim().Replace('_', '-').ToLowerInvariant();
        return s.Length == 0 ? "en-us" : s;
    }

    // ---------------------------------------------------------------------------------------------- internals ----

    internal static ModLocale Of(ModContext mod) => new(mod.Info.Id, mod.Info.Directory.Length == 0 ? "" : System.IO.Path.Combine(mod.Info.Directory, "locales"), mod.Log);

    private bool TryGetRaw(string language, string key, out string text)
    {
        var t = Load();
        foreach (var tag in t.Chain(language))
            if (t.ByLanguage.TryGetValue(tag, out var table) && table.TryGetValue(key, out text!)) return true;
        text = "";
        return false;
    }

    private void Report(string key, string language)
    {
        lock (_lock)
            if (!_reported.Add(key)) return;
        var t = Load();
        var why = t.Languages.Length == 0 ? $"the mod has no locales/*.json ({_directory})" : $"no file of {string.Join(", ", ModLocale.LookupOrder(language))} has it (files: {string.Join(", ", t.Languages)})";
        Write(LogLevel.Warn, $"text '{key}' of mod {_modId}: {why}; showing the key");
    }

    private void Write(LogLevel level, string text)
    {
        if (_log is not null) _log.Write(level, text);
        else Backend.Current.Log(0, level, text);
    }

    private Tables Load()
    {
        if (_tables is { } have) return have;
        lock (_lock)
        {
            if (_tables is { } again) return again;
            return _tables = Read();
        }
    }

    private Tables Read()
    {
        var by = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (_directory.Length > 0 && System.IO.Directory.Exists(_directory))
                foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").Order(StringComparer.Ordinal))
                {
                    var tag = Normalize(System.IO.Path.GetFileNameWithoutExtension(file));
                    if (!Tag.IsMatch(tag))
                    {
                        Write(LogLevel.Warn, $"locale file {System.IO.Path.GetFileName(file)} of mod {_modId}: the name must be a language tag (en, de, pt-BR); ignored");
                        continue;
                    }
                    if (by.ContainsKey(tag))
                    {
                        Write(LogLevel.Warn, $"locale file {System.IO.Path.GetFileName(file)} of mod {_modId}: the language {tag} has a file already; ignored");
                        continue;
                    }
                    var table = ReadFile(file);
                    if (table is not null) by[tag] = table;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Write(LogLevel.Warn, $"locales of mod {_modId} ({_directory}) cannot be read: {e.Message}");
        }
        return new Tables(by);
    }

    private Dictionary<string, string>? ReadFile(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), ConfigSchema.ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                Write(LogLevel.Warn, $"locale file {name} of mod {_modId} must hold one {{ ... }} object; ignored");
                return null;
            }
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(doc.RootElement, "", table, name);
            return table;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Write(LogLevel.Warn, $"locale file {name} of mod {_modId} cannot be read: {e.Message}; ignored");
            return null;
        }
    }

    private void Flatten(JsonElement obj, string prefix, Dictionary<string, string> into, string file)
    {
        foreach (var p in obj.EnumerateObject())
        {
            var key = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.String:
                    into[key] = p.Value.GetString() ?? "";
                    break;
                case JsonValueKind.Object:
                    Flatten(p.Value, key, into, file);
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    into[key] = p.Value.ToString();
                    break;
                default:
                    Write(LogLevel.Warn, $"locale file {file} of mod {_modId}: '{key}' is not a text; ignored");
                    break;
            }
        }
    }

    /// <summary>Fills <c>{0}</c>, <c>{name}</c>, <c>{name:format}</c>; <c>{{</c> and <c>}}</c> are braces.</summary>
    internal static string Fill(string template, object?[] args)
    {
        var sb = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                sb.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i++;
            }
            else if (c == '{' && template.IndexOf('}', i + 1) is var end and > 0)
            {
                var token = template.AsSpan(i + 1, end - i - 1);
                var colon = token.IndexOf(':');
                var name = (colon >= 0 ? token[..colon] : token).Trim().ToString();
                var spec = colon >= 0 ? token[(colon + 1)..].ToString() : null;
                if (name.Length > 0 && TryValue(name, args, out var value)) sb.Append(Text(value, spec));
                else sb.Append(template, i, end - i + 1);
                i = end;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Text(object? v, string? spec) => v switch
    {
        null => "",
        IFormattable f => f.ToString(spec, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static bool TryValue(string name, object?[] args, out object? value)
    {
        value = null;
        if (name.All(char.IsAsciiDigit))
        {
            if (!int.TryParse(name, CultureInfo.InvariantCulture, out var n) || n >= args.Length) return false;
            value = args[n];
            return true;
        }
        if (args.Length != 1 || args[0] is null) return false;
        switch (args[0])
        {
            case IReadOnlyDictionary<string, object?> ro:
                foreach (var (k, v) in ro)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = v;
                        return true;
                    }
                return false;
            case IDictionary<string, object?> rw:
                foreach (var (k, v) in rw)
                    if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = v;
                        return true;
                    }
                return false;
            case string or IFormattable:
                return false;
            default:
            {
                // an object's property: new { count = 3 }. No cache: a static one would pin a mod's types.
                var p = args[0]!.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (p is null || p.GetIndexParameters().Length > 0) return false;
                value = p.GetValue(args[0]);
                return true;
            }
        }
    }

    private sealed class Tables
    {
        private readonly Dictionary<string, string[]> _chains = new(StringComparer.Ordinal);

        internal Tables(Dictionary<string, Dictionary<string, string>> byLanguage)
        {
            ByLanguage = byLanguage;
            Languages = byLanguage.Keys.Order(StringComparer.Ordinal).ToArray();
            Keys = byLanguage.Values.SelectMany(t => t.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        internal Dictionary<string, Dictionary<string, string>> ByLanguage { get; }
        internal string[] Languages { get; }
        internal string[] Keys { get; }

        internal string[] Chain(string language)
        {
            var l = Normalize(language);
            lock (_chains)
            {
                if (!_chains.TryGetValue(l, out var chain))
                {
                    if (_chains.Count > 64) _chains.Clear();  // a language per call would be a bug of the caller's
                    _chains[l] = chain = LookupOrder(l).ToArray();
                }
                return chain;
            }
        }
    }
}

/// <summary>Where the game's language comes from.</summary>
internal static class GameLanguageSource
{
    /// <summary>Tests: what the game would say.</summary>
    internal static Func<string?>? Override;

    private static Func<string>? _client;
    private static bool _clientTried;

    /// <summary>"en-us" on a dedicated server (and in a client that cannot say); on a client the language of the game's texts
    /// as kiln.ui reads it from the game (Kiln.Ui's <c>GameLanguage.Current</c>, "de-de"). The client's value is read through
    /// reflection, so a mod that never loads Kiln.Ui does not load it for this.</summary>
    internal static string Current()
    {
        if (Override is { } o) return ModLocale.Normalize(o());
        if (Mods.Side != ModSide.Client) return "en-us";
        if (!_clientTried)
        {
            _clientTried = true;
            try
            {
                var p = Type.GetType("Kiln.GameLanguage, Kiln.Ui")?.GetProperty("Current", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p?.GetGetMethod() is { } getter) _client = getter.CreateDelegate<Func<string>>();
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"the game's language is not available: {e.Message}; mod texts use English");
            }
        }
        try
        {
            return _client is { } read ? ModLocale.Normalize(read()) : "en-us";
        }
        catch (Exception)
        {
            return "en-us";
        }
    }

    internal static void ResetForTests()
    {
        Override = null;
        _client = null;
        _clientTried = false;
    }
}
