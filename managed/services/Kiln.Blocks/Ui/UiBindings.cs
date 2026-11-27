// SPDX-License-Identifier: MIT
// What a window, a HUD line or a condition can show: the values it binds to, and the small languages that name them.
//   game.<name>               a read-only value Kiln.Ui and Kiln.Client already expose (game.fps, game.state, ...): UiGameValues
//   config.<path>             an entry of one of the mod's own configs; config.<config id>/<path> when two configs have the path
//   "FPS {game.fps:0}"        a template: {name} or {name:format}, {{ and }} for braces
//   "game.inGame && !config.x", "game.screen == InGame", "game.fps < 30"      a condition: terms joined by &&
// Since 0.2.0-dev.31 (dossier 09 §4.3) the names are typed and exact: state.<key> (the window's state), mod.<id> (the mod's
// own published data), world.local.* / world.server.* (kiln.ui.snapshot's immutable publication, UiWorldSnapshot.cs),
// stats.<mod>/<id> (05b's synced copy), row.* and node.* (inside a table's cells and a tree's node rows). Values are exact
// CLR boxes (bool, long, ulong, decimal, double, string, an immutable set, rows, a tree) or UiUnavailable: never one double
// for every number. A new name that cannot be read now is Unavailable: it shows as a dash and fails every test.
// Everything here is plain logic with no overlay in it, so it is checked when a block compiles and tested without a game. The values are read when a window
// or a HUD line refreshes, never every frame; a refresh reads them through one UiRefreshContext.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>A binding's type. Number is a finite double (float configs, game.fps); the exact types are the rest.</summary>
internal enum UiValueType { Bool, Number, Text, Int64, UInt64, Decimal, StringSet, Rows, Tree }

/// <summary>Where a binding's value comes from.</summary>
internal enum UiSource { Game, Config, State, StateCount, Mod, WorldLocal, WorldServer, Stats, Row, Node, Available }

/// <summary>A value a block names, with what is known of it when the block compiles.</summary>
internal sealed record UiBinding(string Name, UiValueType Type, string? ConfigId = null, string? Path = null, string? EntryType = null, double? Min = null, double? Max = null,
    IReadOnlyList<string>? Options = null, bool ServerKind = false)
{
    public bool IsConfig => ConfigId != null;
    public UiSource Source { get; init; } = ConfigId != null ? UiSource.Config : UiSource.Game;
    /// <summary>A config entry's <see cref="ConfigEntry.SoftMin"/> / <see cref="ConfigEntry.SoftMax"/> (kiln#155): that end
    /// of <see cref="Min"/>..<see cref="Max"/> is where a slider stops, not a bound of the values the config takes.</summary>
    public bool SoftMin { get; init; }
    /// <summary>See <see cref="SoftMin"/>: the high end.</summary>
    public bool SoftMax { get; init; }
    /// <summary>The low bound a value is checked against (Kiln.Api's ConfigNode.HardMin): <see cref="Min"/> unless it is soft.</summary>
    public double? HardMin => SoftMin ? null : Min;
    /// <summary>The high bound a value is checked against: <see cref="Max"/> unless it is soft.</summary>
    public double? HardMax => SoftMax ? null : Max;
    /// <summary>The state key, data id, world field, stat key (mod:id), stat part or row/node field.</summary>
    public string? Key { get; init; }
    public string? Part { get; init; }
    /// <summary>Rows and trees: their fields (built-in ones included).</summary>
    public IReadOnlyList<UiField>? Fields { get; init; }
    /// <summary>state.*: the declaration.</summary>
    public UiStateDefinition? State { get; init; }
    /// <summary>What the live-values collector must gather for this name.</summary>
    public UiWorldFields World { get; init; }
    /// <summary>X.available: the binding it tests.</summary>
    public UiBinding? Of { get; init; }
    /// <summary>A name of the typed namespaces (dev.31): exact literals, Unavailable fails every test.</summary>
    public bool Strict => Source is not (UiSource.Game or UiSource.Config);
    /// <summary>Holds one value that a template can show.</summary>
    public bool Scalar => Type is not (UiValueType.StringSet or UiValueType.Rows or UiValueType.Tree);
}

/// <summary>What a name may refer to where it is written: the window's state, a table row's or a tree node's fields.</summary>
internal sealed class UiScope
{
    public required string Mod { get; init; }
    public IReadOnlyDictionary<string, UiStateDefinition>? State { get; init; }
    public IReadOnlyList<UiField>? RowFields { get; init; }
    public IReadOnlyList<UiField>? NodeFields { get; init; }
    public UiScope With(IReadOnlyList<UiField>? rowFields = null, IReadOnlyList<UiField>? nodeFields = null) =>
        new() { Mod = Mod, State = State, RowFields = rowFields ?? RowFields, NodeFields = nodeFields ?? NodeFields };
}

/// <summary>A value that cannot be read now (no world, not published yet, deferred): shown as a dash, false in every test.</summary>
internal sealed record UiUnavailable(string Reason)
{
    public static readonly UiUnavailable NoWorld = new("NoWorld");
    public override string ToString() => "\u2014";
}

/// <summary>Rows of a table source, immutable, with the version they were published at.</summary>
internal sealed record UiRowsValue(ImmutableArray<UiRowEntry> Rows, ulong Version)
{
    public static readonly UiRowsValue Empty = new(ImmutableArray<UiRowEntry>.Empty, 0);
}

internal sealed record UiRowEntry(string Key, ImmutableDictionary<string, object> Values);

/// <summary>A tree source's nodes in drawing order (each node before its children), immutable.</summary>
internal sealed record UiTreeValue(ImmutableArray<UiTreeEntry> Nodes, ulong Version);

internal sealed record UiTreeEntry(string Key, string? Parent, UiText Label, int Depth, bool HasChildren, ImmutableDictionary<string, object> Values);

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
    public static UiBinding Resolve(string mod, string spec) => Resolve(new UiScope { Mod = mod }, spec);

    /// <summary>The value a name means where it is written (<paramref name="scope"/>: the window's state, a row's fields).</summary>
    public static UiBinding Resolve(UiScope scope, string spec)
    {
        var mod = scope.Mod;
        spec = (spec ?? "").Trim();
        if (spec.StartsWith("game.", StringComparison.Ordinal))
        {
            var g = UiGameValues.Find(spec[5..]) ?? throw new FormatException($"\"{spec}\" is not a game value; there are: {UiGameValues.Names}");
            return new UiBinding("game." + g.Name, g.Type);
        }
        if (spec.StartsWith("config.", StringComparison.Ordinal) && spec.Length > 7) return Config(mod, spec[7..]);
        if (UiTypedNames.TryResolve(scope, spec, out var typed, out var problem)) return typed!;
        if (problem != null) throw new FormatException(problem);
        if (spec.EndsWith(".available", StringComparison.Ordinal) && spec.Length > 10)
        {
            var inner = Resolve(scope, spec[..^10]);
            if (!inner.Strict) throw new FormatException($"\"{spec}\": .available goes with the typed names (state.*, mod.*, world.*, stats.*, row.*, node.*); {inner.Name} is always there");
            return new UiBinding(spec, UiValueType.Bool) { Source = UiSource.Available, Of = inner, World = inner.World };
        }
        throw new FormatException($"\"{spec}\" is not a value: say game.<name> (one of {UiGameValues.Names}), config.<path> (an entry of this mod's config), "
            + "state.<key>, mod.<id>, world.local.<name>, world.server.<name>, stats.<mod>/<id>, or row.<field> / node.<field> inside a table or tree");
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
        return new UiBinding($"config.{cfg.Id}/{e!.Path}", TypeOf(e.Type), cfg.Id, e.Path, e.Type, e.Min, e.Max, e.Options, cfg.ServerKind)
        {
            SoftMin = e.SoftMin, SoftMax = e.SoftMax,
        };
    }

    private static UiValueType TypeOf(string entryType) => entryType switch
    {
        "bool" => UiValueType.Bool,
        "int" => UiValueType.Int64,
        "float" => UiValueType.Number,
        _ => UiValueType.Text,
    };

    /// <summary>A game or config binding's value as the UI handles it: a bool, a long, a ulong, a decimal, a double or a
    /// string. Null when it cannot be read now (the config is gone, the game does not answer). Typed names read through a
    /// <see cref="UiRefreshContext"/>.</summary>
    public static object? Read(UiBinding b)
    {
        try
        {
            if (b.Source == UiSource.Game) return Normalize(UiGameValues.Find(b.Name[5..])?.Read());
            if (b.Source == UiSource.Config) return UiConfigs.Find(b.ConfigId!)?.Get(b.Path!) is { } v ? Normalize(v) : null;
            return new UiRefreshContext(new UiScope { Mod = "" }).Value(b);
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
        sbyte or short or int or long => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        byte or ushort or uint => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        ulong u => u <= long.MaxValue ? (long)u : u,
        decimal m => m,
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

    /// <summary>A value as text, with a format: a number's .NET format ("0", "0.00", "N0"), a true/false's "yes|no". Exact
    /// numbers are formatted as they are (never through a double); an unavailable value is a dash.</summary>
    public static string Text(object? v, string? format) => v switch
    {
        null => "",
        UiUnavailable => "\u2014",
        bool b => format is { Length: > 0 } && format.IndexOf('|') is var bar and >= 0 ? (b ? format[..bar] : format[(bar + 1)..]) : b ? "true" : "false",
        double d => format is { Length: > 0 } ? d.ToString(format, CultureInfo.InvariantCulture) : d.ToString("0.##", CultureInfo.InvariantCulture),
        long l => format is { Length: > 0 } ? l.ToString(format, CultureInfo.InvariantCulture) : l.ToString(CultureInfo.InvariantCulture),
        ulong u => format is { Length: > 0 } ? u.ToString(format, CultureInfo.InvariantCulture) : u.ToString(CultureInfo.InvariantCulture),
        decimal m => format is { Length: > 0 } ? m.ToString(format, CultureInfo.InvariantCulture) : UiCanonical.Text(m),
        ImmutableArray<string> set => string.Join(", ", set),
        UiRowsValue rows => rows.Rows.Length.ToString(CultureInfo.InvariantCulture),
        UiTreeValue tree => tree.Nodes.Length.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    internal const int MaxFormat = 128;

    /// <summary>Whether a format suits a value of this type, or the reason it does not.</summary>
    public static string? CheckFormat(UiValueType type, string? format)
    {
        if (string.IsNullOrEmpty(format)) return null;
        if (format.Length > MaxFormat) return $"a format is at most {MaxFormat} characters";
        switch (type)
        {
            case UiValueType.Number or UiValueType.Int64 or UiValueType.UInt64 or UiValueType.Decimal:
                try
                {
                    _ = type switch
                    {
                        UiValueType.Int64 => (-12L).ToString(format, CultureInfo.InvariantCulture),
                        UiValueType.UInt64 => 12UL.ToString(format, CultureInfo.InvariantCulture),
                        UiValueType.Decimal => 1.5m.ToString(format, CultureInfo.InvariantCulture),
                        _ => 1.5.ToString(format, CultureInfo.InvariantCulture),
                    };
                }
                catch (FormatException) { return $"\"{format}\" is not a number format (\"0\", \"0.00\", \"#,##0\")"; }
                return null;
            case UiValueType.Bool:
                return format.IndexOf('|') >= 0 ? null : $"\"{format}\": a true/false takes two words with a bar between, as \"yes|no\"";
            case UiValueType.StringSet or UiValueType.Rows or UiValueType.Tree:
                return "a set, rows or a tree is not one value: show its count (state.<key>/count) or its rows";
            default:
                return $"a text takes no format (\"{format}\")";
        }
    }

    public static bool Truthy(object? v) => v switch
    {
        bool b => b,
        double d => d != 0,
        long l => l != 0,
        ulong u => u != 0,
        decimal m => m != 0,
        string s => s.Length > 0,
        UiUnavailable => false,
        null => false,
        _ => true,
    };

    /// <summary>An exact comparison of two numbers of any of the UI's numeric boxes; null when either is not a number.
    /// long, ulong and decimal compare through decimal (exact for every long and ulong); doubles only with doubles.</summary>
    public static int? CompareNumbers(object? a, object? b)
    {
        if (a is double da && b is double db) return da.CompareTo(db);
        if (a is double || b is double)
        {
            // an old float config against a whole literal: as doubles, the way it always was
            if (ToDouble(a) is { } x && ToDouble(b) is { } y) return x.CompareTo(y);
            return null;
        }
        return ToDecimal(a) is { } ma && ToDecimal(b) is { } mb ? ma.CompareTo(mb) : null;
    }

    public static decimal? ToDecimal(object? v) => v switch { long l => l, ulong u => u, decimal m => m, _ => null };

    public static double? ToDouble(object? v) => v switch { double d => d, long l => l, ulong u => u, decimal m => (double)m, _ => null };
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
            if (!binding.Scalar) throw new FormatException($"{{{inner}}}: {binding.Name} is a set, rows or a tree, not one value{(binding.Type == UiValueType.StringSet ? $": show {binding.Name}/count" : "")}");
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
/// with == != &lt; &lt;= &gt; &gt;= (the last four on numbers). A typed name (state.*, mod.*, world.*, stats.*, row.*, node.*) compares exactly, with a literal of its own type
/// (an int64 with a whole number), and an unavailable value fails every term, ! and != too.</summary>
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
            if (!binding.Scalar)
                throw new FormatException($"\"{term}\": {binding.Name} is a set, rows or a tree, not one value: test {binding.Name}.available{(binding.Type == UiValueType.StringSet ? $" or {binding.Name}/count" : "")}");
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
            c._terms.Add(new Term(binding, false, op, Literal(binding, term, op, lit)));
        }
        return c;
    }

    private static object Literal(UiBinding binding, string term, string op, string lit)
    {
        switch (binding.Type)
        {
            case UiValueType.Number:
                if (!double.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    throw new FormatException($"{binding.Name} is a number: \"{term}\" compares it with \"{lit}\"");
                return d;
            case UiValueType.Int64 or UiValueType.UInt64 when !binding.Strict:
                // an int config, as old blocks wrote it: any number, compared exactly
                if (!decimal.TryParse(lit, NumberStyles.Float, CultureInfo.InvariantCulture, out var cm))
                    throw new FormatException($"{binding.Name} is a number: \"{term}\" compares it with \"{lit}\"");
                return cm;
            case UiValueType.Int64:
                if (!long.TryParse(lit, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    throw new FormatException($"{binding.Name} is a whole number (int64): \"{term}\" compares it with \"{lit}\", which is not one");
                return l;
            case UiValueType.UInt64:
                if (!ulong.TryParse(lit, NumberStyles.None, CultureInfo.InvariantCulture, out var ul))
                    throw new FormatException($"{binding.Name} is a whole number of 0 or more (uint64): \"{term}\" compares it with \"{lit}\", which is not one");
                return ul;
            case UiValueType.Decimal:
                if (!decimal.TryParse(lit, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dm))
                    throw new FormatException($"{binding.Name} is a decimal: \"{term}\" compares it with \"{lit}\", which is not one (digits and a point, no exponent)");
                return dm;
            case UiValueType.Bool:
                if (op is not ("==" or "!=")) throw new FormatException($"{binding.Name} is true or false: \"{term}\" can only use == and !=");
                if (!bool.TryParse(lit, out var b)) throw new FormatException($"{binding.Name} is true or false: \"{term}\" compares it with \"{lit}\"");
                return b;
            default:
                if (op is not ("==" or "!=")) throw new FormatException($"{binding.Name} is a text: \"{term}\" can only use == and !=");
                return lit;
        }
    }

    public bool Test(Func<UiBinding, object?> value)
    {
        foreach (var t in _terms)
        {
            var v = value(t.Binding);
            // a typed name that cannot be read now satisfies nothing, not even ! or !=
            if (v is UiUnavailable || (v == null && t.Binding.Strict)) return false;
            bool ok;
            if (t.Op == null) ok = UiBindings.Truthy(v) != t.Not;
            else if (UiBindings.CompareNumbers(v, t.Literal) is { } cmp)
                ok = t.Op switch { "==" => cmp == 0, "!=" => cmp != 0, "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, _ => cmp >= 0 };
            else
                ok = (v, t.Literal) switch
                {
                    (bool a, bool b) => t.Op == "==" ? a == b : a != b,
                    (string a, string b) => string.Equals(a, b, t.Binding.Strict ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) == (t.Op == "=="),
                    // an old value that cannot be read now matches nothing, so "!=" holds and the rest do not
                    _ => t.Op == "!=",
                };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>The names a condition reads, all of them: a HUD line's or a row's demand counts a hidden term too.</summary>
    public UiWorldFields World => _terms.Aggregate(UiWorldFields.None, (f, t) => f | t.Binding.World);
}
