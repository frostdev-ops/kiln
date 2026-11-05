// SPDX-License-Identifier: MIT
// Target selectors for typed commands (Minecraft's @s @a @p @r @e with [filters]). Used by Arg.Player/Players/Entity/
// Entities and by Selectors.* for mods. Players come from Kiln.Players, other entities from Kiln.Spawn (the entity
// map: template names, positions). docs/kiln/commands.md has the grammar.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>An entity a selector looked at: its template (null for unknown ones), where it is, and the player record
/// for players.</summary>
public readonly record struct SelectedEntity(EntityId Entity, WorldPosition? Position, string? TemplateName,
    string? DisplayName, TemplateKind Kind, Player? Player)
{
    public bool IsPlayer => Player is not null;
    public override string ToString() => Player is { } p ? $"{p.Name} ({Entity})" : $"{TemplateName ?? "?"} ({Entity})";
}

/// <summary>Sort order of a selector's matches (<c>sort=</c>).</summary>
public enum SelectorSort
{
    /// <summary>Whatever order the world lists them in.</summary>
    Arbitrary,
    Nearest,
    Furthest,
    Random,
}

/// <summary>
/// A parsed target selector: <c>@s</c> (the source), <c>@a</c> (all players), <c>@p</c> (nearest player),
/// <c>@r</c> (random player), <c>@e</c> (entities), each optionally followed by filters in brackets:
/// <c>@e[type=creature,distance=..30,limit=5,sort=nearest]</c>.
/// </summary>
/// <remarks>
/// Filters: <c>type=</c> a kind (<c>player creature npc prop pickup other</c>) or a template name (case-insensitive;
/// <c>*</c> wildcards: <c>Enemy_Vukah*</c>; also the catalog's display name), <c>name=</c> a player name (or a
/// template's display name), <c>distance=</c> metres from the origin (<c>10</c>, <c>..10</c>, <c>5..</c>,
/// <c>5..20</c>), <c>x= y= z=</c> the origin (default: the source player's position), <c>limit=</c>, <c>sort=</c>
/// (<c>nearest furthest random arbitrary</c>). <c>type</c> and <c>name</c> take <c>!</c> to negate and may repeat
/// (all must hold). No spaces needed; spaces inside the brackets are kept together by the command tokenizer.
/// </remarks>
public sealed class EntitySelector
{
    private readonly List<(bool Not, string Value)> _types = new();
    private readonly List<(bool Not, string Value)> _names = new();

    private EntitySelector(char head, string text)
    {
        Head = head;
        Text = text;
    }

    /// <summary>s, a, p, r or e.</summary>
    public char Head { get; }

    /// <summary>The selector as written.</summary>
    public string Text { get; }

    public double? MinDistance { get; private set; }
    public double? MaxDistance { get; private set; }
    public double? X { get; private set; }
    public double? Y { get; private set; }
    public double? Z { get; private set; }
    public int? Limit { get; private set; }
    public SelectorSort? Sort { get; private set; }

    /// <summary>Only players can match (@s @a @p @r, or @e[type=player]).</summary>
    public bool PlayersOnly => Head != 'e' || _types.Any(t => !t.Not && t.Value.Equals("player", StringComparison.OrdinalIgnoreCase));

    /// <summary>At most one match (@s, @p, @r, or limit=1).</summary>
    public bool Single => Head == 's' || EffectiveLimit == 1;

    internal int? EffectiveLimit => Limit ?? (Head is 'p' or 'r' ? 1 : null);

    /// <summary>True when <paramref name="text"/> starts like a selector (@ and one of s a p r e).</summary>
    public static bool IsSelector(string? text) =>
        text is { Length: >= 2 } && text[0] == '@' && "sapreSAPRE".Contains(text[1]) && (text.Length == 2 || text[2] == '[');

    /// <summary>Parses a selector; false with a message for a malformed one.</summary>
    public static bool TryParse(string text, out EntitySelector? selector, out string error)
    {
        selector = null;
        error = "";
        if (!IsSelector(text))
        {
            error = $"'{text}' is not a selector (@s, @a, @p, @r, @e)";
            return false;
        }
        var sel = new EntitySelector(char.ToLowerInvariant(text[1]), text);
        if (text.Length > 2)
        {
            if (!text.EndsWith(']'))
            {
                error = $"'{text}': the filter list needs a closing ]";
                return false;
            }
            var body = text[3..^1];
            foreach (var raw in SplitFilters(body))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                var eq = part.IndexOf('=');
                if (eq <= 0)
                {
                    error = $"'{part}' is not key=value (in {text})";
                    return false;
                }
                var key = part[..eq].Trim().ToLowerInvariant();
                var value = Unquote(part[(eq + 1)..].Trim());
                var not = value.StartsWith('!');
                if (not) value = value[1..].Trim();
                if (value.Length == 0)
                {
                    error = $"{key}= needs a value (in {text})";
                    return false;
                }
                if (not && key is not ("type" or "name"))
                {
                    error = $"{key}= cannot be negated (in {text})";
                    return false;
                }
                switch (key)
                {
                    case "type":
                        if (sel.Head != 'e' && (not || !value.Equals("player", StringComparison.OrdinalIgnoreCase)))
                        {
                            error = $"@{sel.Head} only selects players; use @e[type=...] (in {text})";
                            return false;
                        }
                        sel._types.Add((not, value));
                        break;
                    case "name":
                        sel._names.Add((not, value));
                        break;
                    case "distance":
                        if (!TryRange(value, out var lo, out var hi) || lo < 0 || hi < 0 || lo > hi)
                        {
                            error = $"distance={value} is not a range like 10, ..10, 5.., 5..20 (in {text})";
                            return false;
                        }
                        sel.MinDistance = lo > 0 ? lo : null;
                        sel.MaxDistance = double.IsPositiveInfinity(hi) ? null : hi;
                        break;
                    case "x" or "y" or "z":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) || !double.IsFinite(c))
                        {
                            error = $"{key}={value} is not a number (in {text})";
                            return false;
                        }
                        if (key == "x") sel.X = c;
                        else if (key == "y") sel.Y = c;
                        else sel.Z = c;
                        break;
                    case "limit":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                        {
                            error = $"limit={value} must be a whole number >= 1 (in {text})";
                            return false;
                        }
                        if (sel.Head == 's' && n != 1)
                        {
                            error = $"@s selects one entity (in {text})";
                            return false;
                        }
                        sel.Limit = n;
                        break;
                    case "sort":
                        if (!Enum.TryParse<SelectorSort>(value, true, out var s))
                        {
                            error = $"sort={value} is not nearest, furthest, random or arbitrary (in {text})";
                            return false;
                        }
                        sel.Sort = s;
                        break;
                    default:
                        error = $"unknown filter '{key}' (type, name, distance, x, y, z, limit, sort) in {text}";
                        return false;
                }
            }
        }
        selector = sel;
        return true;
    }

    /// <summary>Evaluates the selector for <paramref name="source"/>. Tick thread (entity positions). Empty with a
    /// message when nothing matched or the selector cannot be evaluated here.</summary>
    public IReadOnlyList<SelectedEntity> Select(CommandSource source, out string error)
    {
        error = "";
        var online = Arg.OnlinePlayers().Where(p => p.InGame).ToList();
        WorldPosition? origin = null;
        if (!source.Player.IsNone) origin = Arg.PositionOf(source.Player);
        if (X is not null || Y is not null || Z is not null)
            origin = new WorldPosition(X ?? origin?.X ?? 0, Y ?? origin?.Y ?? 0, Z ?? origin?.Z ?? 0);

        if (Head == 's')
        {
            if (!source.IsPlayer || source.Player.IsNone)
            {
                error = $"@s needs a player (you are {source.Name})";
                return [];
            }
            var me = online.FirstOrDefault(p => p.Entity == source.Player);
            if (me is null)
            {
                error = "you are not in game";
                return [];
            }
            var self = new SelectedEntity(me.Entity, Arg.PositionOf(me.Entity) ?? me.Position, null, null, TemplateKind.Unknown, me);
            var one = Filter([self], origin, out error);
            if (one.Count == 0 && error.Length == 0) error = $"you do not match {Text}";
            return one;
        }

        var sort = Sort ?? (Head == 'p' ? SelectorSort.Nearest : Head == 'r' ? SelectorSort.Random : SelectorSort.Arbitrary);
        if ((sort is SelectorSort.Nearest or SelectorSort.Furthest || MinDistance is not null || MaxDistance is not null) &&
            origin is null)
        {
            error = $"{Text} needs a position: run it as a player in game or give x=,y=,z=";
            return [];
        }

        var candidates = new List<SelectedEntity>();
        foreach (var p in online)
            candidates.Add(new SelectedEntity(p.Entity, Arg.PositionOf(p.Entity) ?? p.Position, null, null, TemplateKind.Unknown, p));
        if (!PlayersOnly)
        {
            var hint = NameHint();
            IReadOnlyList<SelectedEntity> found;
            try
            {
                found = Arg.FindEntities(hint, MaxDistance is not null ? origin : null, MaxDistance ?? 0, Arg.MaxSelectorScan);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or FileNotFoundException)
            {
                error = $"entities cannot be listed on this server ({e.Message})";
                return [];
            }
            var players = online.Select(p => p.Entity).ToHashSet();
            candidates.AddRange(found.Where(f => !players.Contains(f.Entity)));
        }

        var list = Filter(candidates, origin, out error);
        if (error.Length > 0) return [];
        switch (sort)
        {
            case SelectorSort.Nearest:
                list = list.OrderBy(e => Dist(e, origin!.Value)).ToList();
                break;
            case SelectorSort.Furthest:
                list = list.OrderByDescending(e => Dist(e, origin!.Value)).ToList();
                break;
            case SelectorSort.Random:
                list = list.OrderBy(_ => Arg.Random()).ToList();
                break;
        }
        if (EffectiveLimit is { } lim && list.Count > lim) list = list.Take(lim).ToList();
        if (list.Count == 0)
            error = PlayersOnly && online.Count == 0 ? "no player is online" : $"nothing matched {Text}";
        return list;
    }

    private static double Dist(SelectedEntity e, WorldPosition o) => e.Position is { } p ? p.DistanceTo(o) : double.PositiveInfinity;

    private List<SelectedEntity> Filter(IEnumerable<SelectedEntity> all, WorldPosition? origin, out string error)
    {
        error = "";
        var types = _types.Select(t => (t.Not, Kind: KindOf(t.Value), Pattern: t.Value, Regex: Glob(t.Value))).ToList();
        var names = _names.Select(n => (n.Not, n.Value, Regex: Glob(n.Value))).ToList();
        var outList = new List<SelectedEntity>();
        foreach (var e in all)
        {
            var ok = true;
            foreach (var t in types)
            {
                bool hit;
                if (t.Kind is { } k) hit = k == "player" ? e.IsPlayer : !e.IsPlayer && e.Kind.ToString().Equals(k, StringComparison.OrdinalIgnoreCase);
                else hit = !e.IsPlayer && (Matches(t.Regex, e.TemplateName) || Matches(t.Regex, e.DisplayName));
                if (hit == t.Not)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            foreach (var n in names)
            {
                var hit = e.Player is { } p ? Matches(n.Regex, p.Name) : Matches(n.Regex, e.DisplayName);
                if (hit == n.Not)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            if ((MinDistance is not null || MaxDistance is not null) && origin is { } o)
            {
                var d = Dist(e, o);
                if (double.IsPositiveInfinity(d) || d < (MinDistance ?? 0) || d > (MaxDistance ?? double.MaxValue)) continue;
            }
            outList.Add(e);
        }
        return outList;
    }

    private static readonly string[] Kinds = ["player", "creature", "npc", "prop", "pickup", "other"];

    private static string? KindOf(string v) => Kinds.FirstOrDefault(k => k.Equals(v, StringComparison.OrdinalIgnoreCase));

    // The longest literal part of a positive template-name filter: Kiln.Spawn narrows the scan with it.
    private string? NameHint()
    {
        string? best = null;
        foreach (var (not, v) in _types)
        {
            if (not || KindOf(v) is not null) continue;
            foreach (var part in v.Split('*', StringSplitOptions.RemoveEmptyEntries))
                if (best is null || part.Length > best.Length) best = part;
        }
        return best;
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool Matches(Regex r, string? s) => s is { Length: > 0 } && r.IsMatch(s);

    private static bool TryRange(string v, out double lo, out double hi)
    {
        lo = 0;
        hi = double.PositiveInfinity;
        var dots = v.IndexOf("..", StringComparison.Ordinal);
        static bool Num(string s, out double d) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);
        if (dots < 0)
        {
            if (!Num(v, out lo)) return false;
            hi = lo;
            return true;
        }
        var a = v[..dots];
        var b = v[(dots + 2)..];
        if (a.Length == 0 && b.Length == 0) return false;
        if (a.Length > 0 && !Num(a, out lo)) return false;
        if (b.Length > 0 && !Num(b, out hi)) return false;
        return true;
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;

    private static IEnumerable<string> SplitFilters(string body)
    {
        var sb = new StringBuilder();
        var quote = '\0';
        foreach (var c in body)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                sb.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                sb.Append(c);
                continue;
            }
            if (c == ',')
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        yield return sb.ToString();
    }

    public override string ToString() => Text;
}

/// <summary>Target selectors for mods: the same @s/@a/@p/@r/@e[...] the command arguments accept.</summary>
public static class Selectors
{
    /// <summary>Entities (players included) matching <paramref name="selector"/>, as seen by <paramref name="source"/>.
    /// Tick thread. Throws <see cref="ArgumentException"/> for a malformed selector or when nothing matched.</summary>
    public static IReadOnlyList<SelectedEntity> Select(CommandSource source, string selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        Services.RequireTickThread("Selectors.Select");
        if (!EntitySelector.TryParse(selector, out var sel, out var error)) throw new ArgumentException(error, nameof(selector));
        var list = sel!.Select(source, out error);
        if (list.Count == 0) throw new ArgumentException(error, nameof(selector));
        return list;
    }

    /// <summary>The entity ids of <see cref="Select"/>.</summary>
    public static IReadOnlyList<EntityId> Entities(CommandSource source, string selector) =>
        Select(source, selector).Select(e => e.Entity).ToArray();

    /// <summary>The players among <see cref="Select"/> (a selector that may match other entities is an error).</summary>
    public static IReadOnlyList<Player> Players(CommandSource source, string selector)
    {
        if (!EntitySelector.TryParse(selector, out var sel, out var error)) throw new ArgumentException(error, nameof(selector));
        if (!sel!.PlayersOnly) throw new ArgumentException($"{selector} may select other entities; use @a/@p/@r or type=player", nameof(selector));
        return Select(source, selector).Select(e => e.Player!).ToArray();
    }
}
