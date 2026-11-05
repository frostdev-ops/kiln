// SPDX-License-Identifier: MIT
// Argument types of typed commands (Brigadier's ArgumentType). Build them with Arg.*.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>Reads arguments from the tokens of a command line.</summary>
internal sealed class ArgReader(CommandSource source, string line, IReadOnlyList<CommandLine.Token> tokens, int index)
{
    public CommandSource Source { get; } = source;
    public int Index { get; } = index;
    public int Remaining => tokens.Count - Index;
    public string Token(int k = 0) => tokens[Index + k].Text;

    /// <summary>The raw text from the current token to the end of the line, as typed.</summary>
    public string Rest() => Index < tokens.Count ? line[tokens[Index].Start..].TrimEnd() : "";
}

/// <summary>The type of a command argument: how text becomes a value, what it suggests while typing. Use the
/// factories of <see cref="Arg"/>.</summary>
public abstract class ArgumentType
{
    /// <summary>Short type name: "int", "double", "bool", "word", "string", "greedy", "enum", "player", "players",
    /// "entity", "entities", "position", "item", "duration", "time".</summary>
    public abstract string Name { get; }

    /// <summary>The CLR type of the parsed value (what <see cref="CommandContext.Get{T}"/> returns).</summary>
    public abstract Type ValueType { get; }

    /// <summary>For help: "whole number 1..1000".</summary>
    public abstract string Describe();

    /// <summary>How many tokens it reads (0 = everything that is left).</summary>
    internal virtual int Tokens => 1;

    internal abstract bool TryParse(ArgReader r, out object? value, out string error);

    internal virtual IEnumerable<string> Suggest(string prefix, CommandSource source) => [];

    internal virtual JsonObject ToJson() => new() { ["type"] = Name, ["describe"] = Describe() };

    public override string ToString() => Name;

    internal static IEnumerable<string> StartingWith(IEnumerable<string> options, string prefix) =>
        options.Where(o => o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Factories for <see cref="ArgumentType"/>s.</summary>
public static class Arg
{
    /// <summary>A whole number in <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static ArgumentType Int(int min = int.MinValue, int max = int.MaxValue) => new IntArg(min, max);

    /// <summary>A number (invariant culture, "1.5") in <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static ArgumentType Double(double min = double.MinValue, double max = double.MaxValue) => new DoubleArg(min, max);

    /// <summary>true/false, on/off, yes/no, 1/0.</summary>
    public static ArgumentType Bool() => new BoolArg();

    /// <summary>One word (no spaces).</summary>
    public static ArgumentType Word() => new StringArg(StringMode.Word);

    /// <summary>One word, or "a quoted text".</summary>
    public static ArgumentType String() => new StringArg(StringMode.Quotable);

    /// <summary>Everything to the end of the line, as typed.</summary>
    public static ArgumentType Greedy() => new StringArg(StringMode.Greedy);

    /// <summary>A name of <typeparamref name="T"/> (any case).</summary>
    public static ArgumentType Enum<T>() where T : struct, System.Enum => new EnumArg(typeof(T));

    public static ArgumentType Enum(Type enumType) =>
        enumType.IsEnum ? new EnumArg(enumType) : throw new ArgumentException($"{enumType} is not an enum");

    /// <summary>One of <paramref name="options"/> (any case); the value is the option as declared.</summary>
    public static ArgumentType Choice(params string[] options) =>
        options is { Length: > 0 } ? new ChoiceArg(options) : throw new ArgumentException("a choice needs options");

    /// <summary>One online player: name (exact, else a unique prefix, any case), entity id, <c>@s</c> (the player
    /// running the command), <c>@p</c> (the nearest player), <c>@r</c> (a random player), or any player selector that
    /// matches one (<c>@a[name=Al*,limit=1]</c>; see <see cref="EntitySelector"/>). Value: <see cref="Player"/>.</summary>
    public static ArgumentType Player() => new PlayerArg(false);

    /// <summary>Like <see cref="Player"/>, plus <c>@a</c> (every player in game) and filtered selectors
    /// (<c>@a[distance=..30]</c>, <c>@r[limit=2]</c>). Value: <c>IReadOnlyList&lt;Player&gt;</c>.</summary>
    public static ArgumentType Players() => new PlayerArg(true);

    /// <summary>One entity: an entity id (a number, not checked for existence), <c>@s</c>, <c>@p</c>, <c>@r</c>, or a
    /// selector that matches exactly one (<c>@e[type=creature,sort=nearest,limit=1]</c>). Value:
    /// <see cref="EntityId"/>.</summary>
    public static ArgumentType Entity() => new EntityArg(false);

    /// <summary>Entities: ids, player names or any selector (<c>@e[type=Enemy_Vukah*,distance=..40]</c>, <c>@a</c>).
    /// Value: <c>IReadOnlyList&lt;EntityId&gt;</c>.</summary>
    public static ArgumentType Entities() => new EntityArg(true);

    /// <summary>Three coordinates "x y z" in metres; <c>~</c> or <c>~5</c> is relative to the player running the
    /// command. Value: <see cref="WorldPosition"/>.</summary>
    public static ArgumentType Position() => new PositionArg();

    /// <summary>An item by name, display name or id (Kiln.Items catalog). Value: <see cref="ItemInfo"/>.</summary>
    public static ArgumentType Item() => new ItemArg();

    /// <summary>A duration: "90s", "5m", "1h30m", "250ms", "00:05:00", or seconds. Value: <see cref="TimeSpan"/>.</summary>
    public static ArgumentType Duration(TimeSpan? min = null, TimeSpan? max = null) => new DurationArg(min, max);

    /// <summary>A time of day: "7", "7.5", "07:30", "7pm", "noon", "midnight", "dawn", "dusk". Value: hours
    /// (<see cref="double"/>, 0..24).</summary>
    public static ArgumentType TimeOfDay() => new TimeOfDayArg();

    // ---- lookups (tests replace them) ----
    internal static Func<IReadOnlyList<global::Kiln.Player>> OnlinePlayers = () =>
    {
        try
        {
            return Kiln.Players.Available ? Kiln.Players.All : [];
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return [];
        }
    };

    internal static Func<EntityId, WorldPosition?> PositionOf = e =>
    {
        try
        {
            return Kiln.Players.PositionOf(e);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    };

    /// <summary>Entities for @e (players are added from <see cref="OnlinePlayers"/>): (template-name hint or null,
    /// centre or null, radius (0 = anywhere), max) → entities with template and position. Kiln.Spawn by default.</summary>
    internal static Func<string?, WorldPosition?, double, int, IReadOnlyList<SelectedEntity>> FindEntities = SpawnEntities;

    /// <summary>At most this many entities are looked at for one @e selector.</summary>
    internal static int MaxSelectorScan = 8192;

    internal static Func<double> Random = () => System.Random.Shared.NextDouble();

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<SelectedEntity> SpawnEntities(string? hint, WorldPosition? near, double radius, int max)
    {
        var ids = hint is { Length: > 0 }
            ? Spawn.FindByName(hint, near, radius, max)
            : Spawn.Find(System.Guid.Empty, near, radius, max);
        var list = new List<SelectedEntity>(ids.Count);
        foreach (var id in ids)
        {
            var info = Spawn.Inspect(id);
            if (info is null) continue;
            var tmpl = info.Template is { } g ? Spawn.Template(g) : null;
            list.Add(new SelectedEntity(id, info.Position, tmpl?.Name ?? info.TemplateName,
                tmpl is { DisplayName.Length: > 0 } ? tmpl.DisplayName : null, tmpl?.Kind ?? TemplateKind.Unknown, null));
        }
        return list;
    }

    internal static Func<string, ItemInfo?> FindItem = s => Items.Lookup(s);
    internal static Func<string, int, IReadOnlyList<ItemInfo>> SearchItems = (s, n) => Items.Search(s, n);
}

internal sealed class IntArg(int min, int max) : ArgumentType
{
    public int Min { get; } = min;
    public int Max { get; } = max;
    public override string Name => "int";
    public override Type ValueType => typeof(int);
    public override string Describe() => "whole number" + Range();

    private string Range() => Min == int.MinValue && Max == int.MaxValue ? ""
        : Max == int.MaxValue ? $" >= {Min}" : Min == int.MinValue ? $" <= {Max}" : $" {Min}..{Max}";

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        var s = r.Token();
        if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
        {
            error = $"'{s}' is not a whole number";
            return false;
        }
        if (v < Min || v > Max)
        {
            error = $"{v} is out of range ({Describe()})";
            return false;
        }
        value = (int)v;
        error = "";
        return true;
    }

    internal override JsonObject ToJson()
    {
        var o = base.ToJson();
        if (Min != int.MinValue) o["min"] = Min;
        if (Max != int.MaxValue) o["max"] = Max;
        return o;
    }
}

internal sealed class DoubleArg(double min, double max) : ArgumentType
{
    public override string Name => "double";
    public override Type ValueType => typeof(double);

    public override string Describe() => "number" + (min <= double.MinValue && max >= double.MaxValue ? ""
        : max >= double.MaxValue ? $" >= {N(min)}" : min <= double.MinValue ? $" <= {N(max)}" : $" {N(min)}..{N(max)}");

    private static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        var s = r.Token();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
        {
            error = $"'{s}' is not a number";
            return false;
        }
        if (v < min || v > max)
        {
            error = $"{N(v)} is out of range ({Describe()})";
            return false;
        }
        value = v;
        error = "";
        return true;
    }

    internal override JsonObject ToJson()
    {
        var o = base.ToJson();
        if (min > double.MinValue) o["min"] = min;
        if (max < double.MaxValue) o["max"] = max;
        return o;
    }
}

internal sealed class BoolArg : ArgumentType
{
    public override string Name => "bool";
    public override Type ValueType => typeof(bool);
    public override string Describe() => "on/off";

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        error = "";
        switch (r.Token().ToLowerInvariant())
        {
            case "1" or "true" or "on" or "yes":
                value = true;
                return true;
            case "0" or "false" or "off" or "no":
                value = false;
                return true;
        }
        error = $"'{r.Token()}' is not on/off";
        return false;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source) => StartingWith(["on", "off", "true", "false"], prefix);
}

internal enum StringMode
{
    Word,
    Quotable,
    Greedy,
}

internal sealed class StringArg(StringMode mode) : ArgumentType
{
    public override string Name => mode switch { StringMode.Word => "word", StringMode.Greedy => "greedy", _ => "string" };
    public override Type ValueType => typeof(string);
    public override string Describe() => mode switch { StringMode.Word => "one word", StringMode.Greedy => "text to the end of the line", _ => "word or \"quoted text\"" };
    internal override int Tokens => mode == StringMode.Greedy ? 0 : 1;

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        error = "";
        if (mode == StringMode.Greedy)
        {
            value = r.Rest();
            return true;
        }
        var s = r.Token();
        if (mode == StringMode.Word && s.Any(char.IsWhiteSpace))
        {
            value = null;
            error = $"'{s}' must be one word";
            return false;
        }
        value = s;
        return true;
    }
}

internal sealed class EnumArg(Type type) : ArgumentType
{
    public override string Name => "enum";
    public override Type ValueType => type;
    public override string Describe() => "one of " + string.Join(", ", System.Enum.GetNames(type).Select(n => n.ToLowerInvariant()));

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        var s = r.Token();
        foreach (var n in System.Enum.GetNames(type))
            if (string.Equals(n, s, StringComparison.OrdinalIgnoreCase))
            {
                value = System.Enum.Parse(type, n);
                error = "";
                return true;
            }
        value = null;
        error = $"'{s}' is not {Describe()}";
        return false;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source) =>
        StartingWith(System.Enum.GetNames(type).Select(n => n.ToLowerInvariant()), prefix);

    internal override JsonObject ToJson()
    {
        var o = base.ToJson();
        o["options"] = new JsonArray([.. System.Enum.GetNames(type).Select(n => (JsonNode)n.ToLowerInvariant())]);
        return o;
    }
}

internal sealed class ChoiceArg(string[] options) : ArgumentType
{
    public override string Name => "choice";
    public override Type ValueType => typeof(string);
    public override string Describe() => "one of " + string.Join(", ", options);

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        var s = r.Token();
        value = options.FirstOrDefault(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase));
        error = value is null ? $"'{s}' is not {Describe()}" : "";
        return value is not null;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source) => StartingWith(options, prefix);

    internal override JsonObject ToJson()
    {
        var o = base.ToJson();
        o["options"] = new JsonArray([.. options.Select(n => (JsonNode)n)]);
        return o;
    }
}

internal sealed class PlayerArg(bool many) : ArgumentType
{
    public override string Name => many ? "players" : "player";
    public override Type ValueType => many ? typeof(IReadOnlyList<global::Kiln.Player>) : typeof(global::Kiln.Player);
    public override string Describe() => many ? "player name, @s, @p, @r or @a[...]" : "player name, @s, @p or @r";

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        var s = r.Token();
        if (EntitySelector.IsSelector(s))
        {
            if (!EntitySelector.TryParse(s, out var sel, out error)) return false;
            if (!sel!.PlayersOnly)
            {
                error = $"{s} may select other entities; a player argument takes @s, @p, @r, @a or type=player";
                return false;
            }
            if (!many && !sel.Single && sel.Head == 'a' && sel.Limit is null)
            {
                error = $"{s} selects several players; use @p, @r or limit=1";
                return false;
            }
            var found = sel.Select(r.Source, out error);
            if (found.Count == 0) return false;
            if (!many && found.Count != 1)
            {
                error = $"{s} matched {found.Count} players; this argument takes one";
                return false;
            }
            var players = found.Select(e => e.Player!).ToList();
            value = many ? (IReadOnlyList<global::Kiln.Player>)players : players[0];
            error = "";
            return true;
        }
        var online = Arg.OnlinePlayers().Where(p => p.InGame).ToList();
        if (!Resolve(s, online, out var one, out error)) return false;
        value = many ? new[] { one! } : one;
        error = "";
        return true;
    }

    internal static bool Resolve(string s, IReadOnlyList<global::Kiln.Player> online, out global::Kiln.Player? player, out string error)
    {
        error = "";
        player = null;
        if (uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            player = online.FirstOrDefault(p => p.Entity.Value == id);
            if (player is not null) return true;
        }
        var exact = online.Where(p => string.Equals(p.Name, s, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            player = exact[0];
            return true;
        }
        var prefix = online.Where(p => p.Name.StartsWith(s, StringComparison.OrdinalIgnoreCase)).ToList();
        if (prefix.Count == 1)
        {
            player = prefix[0];
            return true;
        }
        error = prefix.Count > 1
            ? $"'{s}' matches {prefix.Count} players: {string.Join(", ", prefix.Select(p => p.Name).Take(5))}"
            : $"no player '{s}' is online";
        return false;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source)
    {
        var names = Arg.OnlinePlayers().Where(p => p.InGame).Select(p => p.Name).ToList();
        names.AddRange(["@s", "@p", "@r"]);
        if (many) names.AddRange(["@a", "@a["]);
        return StartingWith(names, prefix);
    }
}

internal sealed class EntityArg(bool many) : ArgumentType
{
    public override string Name => many ? "entities" : "entity";
    public override Type ValueType => many ? typeof(IReadOnlyList<EntityId>) : typeof(EntityId);
    public override string Describe() => many ? "entity ids, player names or a selector (@e[type=...,distance=..])"
        : "entity id, @s, @p, @r or a selector matching one";

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        var s = r.Token();
        if (EntitySelector.IsSelector(s))
        {
            if (!EntitySelector.TryParse(s, out var sel, out error)) return false;
            // @s keeps its old meaning for entity arguments: the source's entity, in game or not.
            if (sel!.Head == 's' && s.Length == 2)
            {
                if (r.Source.Player.IsNone)
                {
                    error = $"@s needs a player (you are {r.Source.Name})";
                    return false;
                }
                value = many ? new[] { r.Source.Player } : r.Source.Player;
                error = "";
                return true;
            }
            var found = sel.Select(r.Source, out error);
            if (found.Count == 0) return false;
            if (!many && found.Count != 1)
            {
                error = $"{s} matched {found.Count} entities; add limit=1 (and sort=nearest) for one";
                return false;
            }
            value = many ? found.Select(e => e.Entity).ToArray() : found[0].Entity;
            error = "";
            return true;
        }
        if (uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
        {
            value = many ? new[] { new EntityId(id) } : new EntityId(id);
            error = "";
            return true;
        }
        if (many)
        {
            // a comma list of ids
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = new List<EntityId>();
            foreach (var part in parts)
            {
                if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var one) || one == 0) break;
                ids.Add(new EntityId(one));
            }
            if (ids.Count == parts.Length && ids.Count > 0)
            {
                value = ids.ToArray();
                error = "";
                return true;
            }
            var online = Arg.OnlinePlayers().Where(p => p.InGame).ToList();
            if (PlayerArg.Resolve(s, online, out var player, out _))
            {
                value = new[] { player!.Entity };
                error = "";
                return true;
            }
        }
        error = $"'{s}' is not an entity id or a selector";
        return false;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source) =>
        StartingWith(many ? ["@e[", "@e", "@a", "@p", "@r", "@s"] : ["@s", "@p", "@r", "@e["], prefix);
}

internal sealed class PositionArg : ArgumentType
{
    public override string Name => "position";
    public override Type ValueType => typeof(WorldPosition);
    public override string Describe() => "x y z (~ = relative to you)";
    internal override int Tokens => 3;

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        if (r.Remaining < 3)
        {
            error = "a position needs three numbers: x y z";
            return false;
        }
        WorldPosition? origin = null;
        var v = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var s = r.Token(i);
            var relative = s.StartsWith('~');
            var num = relative ? s[1..] : s;
            var d = 0.0;
            if (num.Length > 0 && (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d) || !double.IsFinite(d)))
            {
                error = $"'{s}' is not a coordinate";
                return false;
            }
            if (!relative && num.Length == 0)
            {
                error = $"'{s}' is not a coordinate";
                return false;
            }
            if (relative)
            {
                if (origin is null)
                {
                    if (r.Source.Player.IsNone)
                    {
                        error = $"~ needs a player position (you are {r.Source.Name})";
                        return false;
                    }
                    origin = Arg.PositionOf(r.Source.Player);
                    if (origin is null)
                    {
                        error = "your position is unknown";
                        return false;
                    }
                }
                d += i switch { 0 => origin.Value.X, 1 => origin.Value.Y, _ => origin.Value.Z };
            }
            v[i] = d;
        }
        value = new WorldPosition(v[0], v[1], v[2]);
        error = "";
        return true;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source) => StartingWith(["~"], prefix);
}

internal sealed class ItemArg : ArgumentType
{
    public override string Name => "item";
    public override Type ValueType => typeof(ItemInfo);
    public override string Describe() => "item name or id";

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        var s = r.Token();
        try
        {
            value = Arg.FindItem(s);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or FileNotFoundException)
        {
            value = null;
            error = "items are not available on this server";
            return false;
        }
        error = value is null ? $"no item '{s}'" : "";
        return value is not null;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source)
    {
        if (prefix.Length < 2) return [];
        try
        {
            return Arg.SearchItems(prefix, 20).Select(i => i.Name).Where(n => n.Length > 0 && !n.Any(char.IsWhiteSpace));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or FileNotFoundException)
        {
            return [];
        }
    }
}

internal sealed class DurationArg(TimeSpan? min, TimeSpan? max) : ArgumentType
{
    public override string Name => "duration";
    public override Type ValueType => typeof(TimeSpan);

    public override string Describe() => "duration (90s, 5m, 1h30m)" +
        (min is null && max is null ? "" : $" {(min is { } a ? Durations.Format(a) : "0s")}..{(max is { } b ? Durations.Format(b) : "")}");

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        value = null;
        var s = r.Token();
        if (!Durations.TryParse(s, out var ts))
        {
            error = $"'{s}' is not a duration (e.g. 90s, 5m, 1h30m)";
            return false;
        }
        if (min is { } lo && ts < lo || max is { } hi && ts > hi)
        {
            error = $"{Durations.Format(ts)} is out of range ({Describe()})";
            return false;
        }
        value = ts;
        error = "";
        return true;
    }
}

internal sealed class TimeOfDayArg : ArgumentType
{
    public override string Name => "time";
    public override Type ValueType => typeof(double);
    public override string Describe() => "time of day (7, 07:30, 7pm, noon, dusk)";

    internal override bool TryParse(ArgReader r, out object? value, out string error)
    {
        var s = r.Token();
        if (Time.TryParse(s, out var h))
        {
            value = h;
            error = "";
            return true;
        }
        value = null;
        error = $"'{s}' is not a time of day (7, 07:30, 7pm, noon, midnight, dawn, dusk)";
        return false;
    }

    internal override IEnumerable<string> Suggest(string prefix, CommandSource source) =>
        StartingWith(["dawn", "noon", "dusk", "midnight", "06:00", "12:00", "18:00"], prefix);
}
