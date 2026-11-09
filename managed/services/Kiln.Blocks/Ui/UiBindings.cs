// SPDX-License-Identifier: MIT
// What a window, a HUD line or a condition can show: the values it binds to, and the small languages that name them.
//   game.<name>               a read-only value Kiln.Ui and Kiln.Client already expose (game.fps, game.state, ...): UiGameValues
//   config.<path>             an entry of one of the mod's own configs; config.<config id>/<path> when two configs have the path
//   "FPS {game.fps:0}"        a template: {name} or {name:format}, {{ and }} for braces
//   "game.inGame && !config.x", "game.screen == InGame", "game.fps < 30"      a condition: terms joined by &&
// Everything here is plain logic with no overlay in it, so it is checked when a block compiles and tested without a game. The values are read when a window
// or a HUD line refreshes, never every frame.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiln;

internal enum UiValueType { Bool, Number, Text }

/// <summary>A value a block names, with what is known of it when the block compiles.</summary>
internal sealed record UiBinding(string Name, UiValueType Type, string? ConfigId = null, string? Path = null, string? EntryType = null, double? Min = null, double? Max = null,
    IReadOnlyList<string>? Options = null, bool ServerKind = false)
{
    public bool IsConfig => ConfigId != null;
}

/// <summary>The part of a config the UI needs: Kiln.Api's <see cref="ConfigFile"/> in the game, a fake in tests.</summary>
internal interface IUiConfig
{
    string Id { get; }
    /// <summary>A server config: read-only while this game is joined to a server.</summary>
    bool ServerKind { get; }
    IReadOnlyList<ConfigEntry> Entries { get; }
    object? Get(string path);
    bool IsFromServer(string path);
    /// <summary>Tick thread.</summary>
    bool TrySet(string path, object? value, out string error);
}

internal sealed class RealConfig(ConfigFile file) : IUiConfig
{
    public string Id => file.Id;
    public bool ServerKind => file.Kind == ConfigKind.Server;
    public IReadOnlyList<ConfigEntry> Entries => file.Entries;
    public object? Get(string path) => file.Get(path);
    public bool IsFromServer(string path) => file.IsFromServer(path);
    public bool TrySet(string path, object? value, out string error) => file.TrySet(path, value, out error);
}

/// <summary>Where configs come from. Tests replace both.</summary>
internal static class UiConfigs
{
    public static Func<string, IReadOnlyList<IUiConfig>> OfMod = static mod => [.. Config.All.Where(f => f.ModId == mod).Select(f => (IUiConfig)new RealConfig(f))];
    public static Func<string, IUiConfig?> Find = static id => Config.Find(id) is { } f ? new RealConfig(f) : null;
}

/// <summary>A read-only value of the running game. Each is safe to read from any thread, and costs a native read at most.</summary>
internal sealed record GameValue(string Name, UiValueType Type, string Doc, Func<object?> Read);

internal static class UiGameValues
{
    private static readonly object FpsGate = new();
    private static long _fpsAt;
    private static long _fpsPresents;
    private static double _fps;

    public static readonly IReadOnlyList<GameValue> All =
    [
        new("fps", UiValueType.Number, "frames per second the game presents, measured over at least half a second", Fps),
        new("frameMs", UiValueType.Number, "milliseconds the overlay's last frame took on the CPU", () => Safe(() => Ui.Status.FrameMs, 0.0)),
        new("state", UiValueType.Text, "what runs here: Menu, Hosting (this game hosts the world), Remote (it joined one), DedicatedServer", () => Safe(() => Client.Available ? Client.State.ToString() : "Menu", "Menu")),
        new("inGame", UiValueType.Bool, "playing, hosting or joined", () => Safe(() => Client.Available && Client.InGame, false)),
        new("screen", UiValueType.Text, "the game screen that is open: InGame, PauseMenu, MainMenu, Inventory, Map, Crafting, ... (docs/kiln/ui.md ScreenKind)", () => Safe(() => Ui.Screens.Current.Kind.ToString(), "None")),
        new("language", UiValueType.Text, "the game's text language: en-us, de-de, ...", () => Safe(() => GameLanguage.Current, "en-us")),
        new("clock", UiValueType.Text, "the computer's time, HH:mm", () => DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)),
    ];

    public static GameValue? Find(string name) => All.FirstOrDefault(v => v.Name == name);

    public static string Names => string.Join(", ", All.Select(v => "game." + v.Name));

    private static object? Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return fallback; }
    }

    private static object? Fps()
    {
        long presents;
        try { presents = Ui.Status.Presents; }
        catch (Exception e) when (e is not OutOfMemoryException) { return 0.0; }
        var now = Stopwatch.GetTimestamp();
        lock (FpsGate)
        {
            // too long since the last read: start again rather than average over a gap
            if (_fpsAt == 0 || now - _fpsAt > 5 * Stopwatch.Frequency)
            {
                _fpsAt = now;
                _fpsPresents = presents;
                return _fps = 0;
            }
            var seconds = (now - _fpsAt) / (double)Stopwatch.Frequency;
            if (seconds >= 0.5)
            {
                _fps = Math.Max(0, presents - _fpsPresents) / seconds;
                _fpsAt = now;
                _fpsPresents = presents;
            }
            return _fps;
        }
    }
}

internal static class UiBindings
{
    /// <summary>The value a name means, or <see cref="FormatException"/> saying what is wrong with it.</summary>
    public static UiBinding Resolve(string mod, string spec)
    {
        spec = (spec ?? "").Trim();
        if (spec.StartsWith("game.", StringComparison.Ordinal))
        {
            var g = UiGameValues.Find(spec[5..]) ?? throw new FormatException($"\"{spec}\" is not a game value; there are: {UiGameValues.Names}");
            return new UiBinding("game." + g.Name, g.Type);
        }
        if (spec.StartsWith("config.", StringComparison.Ordinal) && spec.Length > 7) return Config(mod, spec[7..]);
        throw new FormatException($"\"{spec}\" is not a value: say game.<name> (one of {UiGameValues.Names}) or config.<path> (an entry of this mod's config)");
    }

    private static UiBinding Config(string mod, string rest)
    {
        var configs = UiConfigs.OfMod(mod);
        if (configs.Count == 0)
            throw new FormatException($"mod {mod} has no config registered, so config.{rest} names nothing: call Config.Register<T>() in OnLoad, before Blocks.Define");
        var path = rest;
        IReadOnlyList<IUiConfig> from = configs;
        var slash = rest.IndexOf('/');
        if (slash > 0)
        {
            var id = rest[..slash];
            path = rest[(slash + 1)..];
            var one = configs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
                      ?? throw new FormatException($"mod {mod} has no config \"{id}\"; its configs are: {string.Join(", ", configs.Select(c => c.Id))}");
            from = [one];
        }
        var hits = from.Select(c => (Config: c, Entry: c.Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))).Where(x => x.Entry != null).ToList();
        if (hits.Count == 0)
        {
            var some = from.SelectMany(c => c.Entries.Select(e => e.Path)).Take(8).ToList();
            throw new FormatException($"no config entry \"{path}\" in {string.Join(", ", from.Select(c => c.Id))}; its entries start with: {string.Join(", ", some)}");
        }
        if (hits.Count > 1)
            throw new FormatException($"config entry \"{path}\" is in more than one of this mod's configs ({string.Join(", ", hits.Select(h => h.Config.Id))}): say config.<config id>/{path}");
        var (cfg, e) = hits[0];
        return new UiBinding($"config.{cfg.Id}/{e!.Path}", TypeOf(e.Type), cfg.Id, e.Path, e.Type, e.Min, e.Max, e.Options, cfg.ServerKind);
    }

    private static UiValueType TypeOf(string entryType) => entryType switch
    {
        "bool" => UiValueType.Bool,
        "int" or "float" => UiValueType.Number,
        _ => UiValueType.Text,
    };

    /// <summary>The binding's value as the UI handles it: a bool, a double or a string. Null when it cannot be read now (the config is gone, the game does not answer).</summary>
    public static object? Read(UiBinding b)
    {
        try
        {
            if (!b.IsConfig) return Normalize(UiGameValues.Find(b.Name[5..])?.Read());
            return UiConfigs.Find(b.ConfigId!)?.Get(b.Path!) is { } v ? Normalize(v) : null;
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FileNotFoundException or TypeLoadException)
        {
            return null;
        }
    }

    public static object? Normalize(object? v) => v switch
    {
        null => null,
        bool or string => v,
        double d => d,
        float f => (double)f,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        TimeSpan t => Duration(t),
        System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(x => Text(Normalize(x), null))) + "]",
        _ => v.ToString(),
    };

    private static string Duration(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "0s";
        var sb = new StringBuilder();
        if (t.Days > 0) sb.Append(t.Days).Append('d');
        if (t.Hours > 0) sb.Append(t.Hours).Append('h');
        if (t.Minutes > 0) sb.Append(t.Minutes).Append('m');
        if (t.Seconds > 0 || t.Milliseconds > 0) sb.Append(t.Milliseconds > 0 ? (t.Seconds + t.Milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) : t.Seconds.ToString(CultureInfo.InvariantCulture)).Append('s');
        return sb.ToString();
    }

    /// <summary>A value as text, with a format: a number's .NET custom format ("0", "0.00"), a true/false's "yes|no".</summary>
    public static string Text(object? v, string? format) => v switch
    {
        null => "",
        bool b => format is { Length: > 0 } && format.IndexOf('|') is var bar and >= 0 ? (b ? format[..bar] : format[(bar + 1)..]) : b ? "true" : "false",
        double d => format is { Length: > 0 } ? d.ToString(format, CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Whether a format suits a value of this type, or the reason it does not.</summary>
    public static string? CheckFormat(UiValueType type, string? format)
    {
        if (string.IsNullOrEmpty(format)) return null;
        switch (type)
        {
            case UiValueType.Number:
                try { 1.5.ToString(format, CultureInfo.InvariantCulture); }
                catch (FormatException) { return $"\"{format}\" is not a number format (\"0\", \"0.00\", \"#,##0\")"; }
                return null;
            case UiValueType.Bool:
                return format.IndexOf('|') >= 0 ? null : $"\"{format}\": a true/false takes two words with a bar between, as \"yes|no\"";
            default:
                return $"a text takes no format (\"{format}\")";
        }
    }

    public static bool Truthy(object? v) => v switch { bool b => b, double d => d != 0, string s => s.Length > 0, null => false, _ => true };
}

/// <summary>A text with values in it: <c>FPS {game.fps:0}</c>. <c>{{</c> and <c>}}</c> are braces.</summary>
internal sealed class UiTemplate
{
    private readonly List<(string Literal, UiBinding? Binding, string? Format)> _parts = new();

    public IReadOnlyList<UiBinding> Bindings => _parts.Where(p => p.Binding != null).Select(p => p.Binding!).ToList();
    public bool IsPlain => _parts.All(p => p.Binding == null);

    /// <summary>Reads a template; <see cref="FormatException"/> names what is wrong in it.</summary>
    public static UiTemplate Parse(string text, Func<string, UiBinding> resolve)
    {
        var t = new UiTemplate();
        var literal = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c == '}') throw new FormatException($"a lone '}}' at {i + 1} in \"{text}\": write }}}} for a brace");
            if (c != '{') { literal.Append(c); continue; }
            var end = text.IndexOf('}', i + 1);
            if (end < 0) throw new FormatException($"'{{' at {i + 1} in \"{text}\" is never closed: write {{{{ for a brace, or {{name}} for a value");
            var inner = text[(i + 1)..end];
            var colon = inner.IndexOf(':');
            var name = (colon >= 0 ? inner[..colon] : inner).Trim();
            var format = colon >= 0 ? inner[(colon + 1)..] : null;
            if (name.Length == 0) throw new FormatException($"an empty {{}} at {i + 1} in \"{text}\": name a value, as {{game.fps}}");
            var binding = resolve(name);
            if (UiBindings.CheckFormat(binding.Type, format) is { } problem) throw new FormatException($"{{{inner}}}: {problem}");
            if (literal.Length > 0) { t._parts.Add((literal.ToString(), null, null)); literal.Clear(); }
            t._parts.Add(("", binding, format));
            i = end;
        }
        if (literal.Length > 0) t._parts.Add((literal.ToString(), null, null));
        return t;
    }

    public string Render(Func<UiBinding, object?> value)
    {
        if (_parts.Count == 1 && _parts[0].Binding == null) return _parts[0].Literal;
        var sb = new StringBuilder();
        foreach (var (literal, binding, format) in _parts)
            sb.Append(binding == null ? literal : UiBindings.Text(value(binding), format));
        return sb.ToString();
    }
}

/// <summary>A text in the game's languages whose every variant is a <see cref="UiTemplate"/> (or plain text), checked when the block compiles.</summary>
internal sealed class UiLine
{
    private readonly Dictionary<string, UiTemplate> _byText = new(StringComparer.Ordinal);

    private UiLine(UiText text) => Text = text;

    public UiText Text { get; }
    public IReadOnlyList<UiBinding> Bindings => _byText.Values.SelectMany(t => t.Bindings).DistinctBy(b => b.Name).ToList();
    public bool IsPlain => _byText.Values.All(t => t.IsPlain);

    /// <summary>Every language's text, read. <see cref="FormatException"/> says which language, and what.</summary>
    public static UiLine Compile(UiText text, Func<string, UiBinding> resolve, bool templated = true)
    {
        var line = new UiLine(text);
        foreach (var language in text.Languages)
        {
            var s = text.For(language);
            if (line._byText.ContainsKey(s)) continue;
            try { line._byText[s] = templated ? UiTemplate.Parse(s, resolve) : UiTemplate.Parse(s.Replace("{", "{{").Replace("}", "}}"), resolve); }
            catch (FormatException e) { throw new FormatException(language == "*" ? e.Message : $"[{language}] {e.Message}"); }
        }
        return line;
    }

    public string Render(string language, Func<UiBinding, object?> value)
    {
        var s = Text.For(language);
        return _byText.TryGetValue(s, out var t) ? t.Render(value) : s;
    }
}

/// <summary>A condition: terms joined by &amp;&amp;. A term is a name (true when a bool is true, a number is not 0, a text is not empty), <c>!name</c>, or <c>name op value</c>
/// with == != &lt; &lt;= &gt; &gt;= (the last four on numbers).</summary>
internal sealed class UiCondition
{
    private sealed record Term(UiBinding Binding, bool Not, string? Op, object? Literal);

    private static readonly Regex TermPattern = new(@"^(?<not>!\s*)?(?<name>[A-Za-z0-9_.\-/]+)\s*(?:(?<op>==|!=|<=|>=|<|>)\s*(?<lit>.+))?$", RegexOptions.CultureInvariant);
    private readonly List<Term> _terms = new();

    public string Source { get; private init; } = "";
    public IReadOnlyList<UiBinding> Bindings => _terms.Select(t => t.Binding).DistinctBy(b => b.Name).ToList();

    public static UiCondition Parse(string text, Func<string, UiBinding> resolve)
    {
        var c = new UiCondition { Source = text };
        foreach (var raw in text.Split("&&"))
        {
            var term = raw.Trim();
            var m = TermPattern.Match(term);
            if (term.Length == 0 || !m.Success)
                throw new FormatException($"\"{term}\" in \"{text}\" is not a condition: a name, !name, or name == value (== != < <= > >=), joined with &&");
            var binding = resolve(m.Groups["name"].Value);
            var not = m.Groups["not"].Success;
            if (!m.Groups["op"].Success)
            {
                c._terms.Add(new Term(binding, not, null, null));
                continue;
            }
            if (not) throw new FormatException($"\"{term}\": ! goes with a name alone; say {binding.Name} != value");
            var op = m.Groups["op"].Value;
            var lit = m.Groups["lit"].Value.Trim();
            if (lit.Length >= 2 && lit[0] is '"' or '\'' && lit[^1] == lit[0]) lit = lit[1..^1];
            object literal;
            switch (binding.Type)
            {
                case UiValueType.Number:
                    if (!double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        throw new FormatException($"{binding.Name} is a number: \"{term}\" compares it with \"{lit}\"");
                    literal = d;
                    break;
                case UiValueType.Bool:
                    if (op is not ("==" or "!=")) throw new FormatException($"{binding.Name} is true or false: \"{term}\" can only use == and !=");
                    if (!bool.TryParse(lit, out var b)) throw new FormatException($"{binding.Name} is true or false: \"{term}\" compares it with \"{lit}\"");
                    literal = b;
                    break;
                default:
                    if (op is not ("==" or "!=")) throw new FormatException($"{binding.Name} is a text: \"{term}\" can only use == and !=");
                    literal = lit;
                    break;
            }
            c._terms.Add(new Term(binding, false, op, literal));
        }
        return c;
    }

    public bool Test(Func<UiBinding, object?> value)
    {
        foreach (var t in _terms)
        {
            var v = value(t.Binding);
            bool ok;
            if (t.Op == null) ok = UiBindings.Truthy(v) != t.Not;
            else
                ok = (v, t.Literal) switch
                {
                    (double a, double b) => t.Op switch { "==" => a == b, "!=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, _ => a >= b },
                    (bool a, bool b) => t.Op == "==" ? a == b : a != b,
                    (string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase) == (t.Op == "=="),
                    // a value that cannot be read now matches nothing, so "!=" holds and the rest do not
                    _ => t.Op == "!=",
                };
            if (!ok) return false;
        }
        return true;
    }
}
