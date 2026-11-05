// SPDX-License-Identifier: MIT
// The schema of a typed mod config (reflection over its class), value conversion with validation, and the JSONC
// writer. Used by Config.cs.
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal enum ConfigValueKind
{
    Bool,
    Int,
    Float,
    String,
    Enum,
    Duration,
    List,
    Section,
}

/// <summary>One entry or section of a config class.</summary>
internal sealed class ConfigNode
{
    public required string Key { get; init; }
    public required string Path { get; init; }
    public required Type ClrType { get; init; }
    public required ConfigValueKind Kind { get; init; }
    public ConfigValueKind ElementKind { get; init; }
    public Type? ElementType { get; init; }
    public string? Comment { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool RequiresRestart { get; init; }
    public bool Synced { get; init; }
    public string[] Options { get; init; } = [];
    public List<ConfigNode> Children { get; } = new();
    public required Func<object, object?> Get { get; init; }
    public required Action<object, object?> Set { get; init; }
    public object? Default { get; set; }

    public bool IsSection => Kind == ConfigValueKind.Section;

    public string TypeName => Kind switch
    {
        ConfigValueKind.List => $"list<{ConfigSchema.KindName(ElementKind)}>",
        _ => ConfigSchema.KindName(Kind),
    };

    public IEnumerable<ConfigNode> Values() =>
        IsSection ? Children.SelectMany(c => c.Values()) : [this];
}

internal static class ConfigSchema
{
    internal static readonly JsonSerializerOptions JsonText = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string KindName(ConfigValueKind k) => k switch
    {
        ConfigValueKind.Bool => "bool",
        ConfigValueKind.Int => "int",
        ConfigValueKind.Float => "float",
        ConfigValueKind.String => "string",
        ConfigValueKind.Enum => "enum",
        ConfigValueKind.Duration => "duration",
        ConfigValueKind.List => "list",
        _ => "section",
    };

    /// <summary>The schema of <paramref name="type"/>: its public read-write properties and fields.</summary>
    public static List<ConfigNode> Build(Type type, object defaults, string prefix = "", bool synced = false, int depth = 0)
    {
        if (depth > 8) throw new ArgumentException($"config sections nested deeper than 8 levels at '{prefix}'");
        var nodes = new List<ConfigNode>();
        var members = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken).Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => !f.IsInitOnly && !f.IsLiteral).OrderBy(f => f.MetadataToken));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            if (m.GetCustomAttribute<ConfigIgnoreAttribute>() is not null) continue;
            if (m.GetCustomAttributes().Any(a => a.GetType().Name == "JsonIgnoreAttribute")) continue;
            var memberType = m is PropertyInfo pi ? pi.PropertyType : ((FieldInfo)m).FieldType;
            var key = m.GetCustomAttribute<ConfigKeyAttribute>()?.Key ?? CamelCase(m.Name);
            if (!keys.Add(key)) throw new ArgumentException($"config key '{key}' appears twice in {type.Name}");
            var path = prefix.Length == 0 ? key : prefix + "." + key;
            Func<object, object?> get = m is PropertyInfo p1 ? o => p1.GetValue(o) : o => ((FieldInfo)m).GetValue(o);
            Action<object, object?> set = m is PropertyInfo p2 ? (o, v) => p2.SetValue(o, v) : (o, v) => ((FieldInfo)m).SetValue(o, v);
            var kind = KindOf(memberType, out var elementType, out var elementKind);
            var comment = m.GetCustomAttribute<CommentAttribute>()?.Text;
            var isSynced = synced || m.GetCustomAttribute<SyncedAttribute>() is not null;
            if (kind == ConfigValueKind.Section)
            {
                comment ??= memberType.GetCustomAttribute<CommentAttribute>()?.Text;
                isSynced |= memberType.GetCustomAttribute<SyncedAttribute>() is not null;
                var section = get(defaults);
                if (section is null)
                {
                    section = Activator.CreateInstance(memberType)!;
                    set(defaults, section);
                }
                var node = new ConfigNode
                {
                    Key = key, Path = path, ClrType = memberType, Kind = kind, Comment = comment, Synced = isSynced,
                    Get = get, Set = set,
                };
                node.Children.AddRange(Build(memberType, section, path, isSynced, depth + 1));
                nodes.Add(node);
                continue;
            }
            var (min, max) = RangeOf(m.GetCustomAttributes());
            if ((min is not null || max is not null) && kind is not (ConfigValueKind.Int or ConfigValueKind.Float or ConfigValueKind.Duration) &&
                !(kind == ConfigValueKind.List && elementKind is ConfigValueKind.Int or ConfigValueKind.Float))
                throw new ArgumentException($"[Range] on '{path}' needs a number or duration");
            var options = kind == ConfigValueKind.Enum ? Enum.GetNames(memberType)
                : kind == ConfigValueKind.List && elementKind == ConfigValueKind.Enum ? Enum.GetNames(elementType!) : [];
            var value = new ConfigNode
            {
                Key = key, Path = path, ClrType = memberType, Kind = kind, ElementKind = elementKind, ElementType = elementType,
                Comment = comment, Min = min, Max = max, Options = options, Synced = isSynced,
                RequiresRestart = m.GetCustomAttribute<RequiresRestartAttribute>() is not null,
                Get = get, Set = set,
            };
            value.Default = Copy(value, get(defaults));
            nodes.Add(value);
        }
        return nodes;
    }

    /// <summary>[Range] from Kiln, or any attribute named RangeAttribute with Minimum/Maximum (DataAnnotations).</summary>
    internal static (double? Min, double? Max) RangeOf(IEnumerable<Attribute> attributes)
    {
        foreach (var a in attributes)
        {
            if (a is RangeAttribute r) return (r.Min, r.Max);
            if (a.GetType().Name != "RangeAttribute") continue;
            var t = a.GetType();
            var min = t.GetProperty("Minimum")?.GetValue(a);
            var max = t.GetProperty("Maximum")?.GetValue(a);
            if (min is IConvertible cmin && max is IConvertible cmax)
                return (cmin.ToDouble(CultureInfo.InvariantCulture), cmax.ToDouble(CultureInfo.InvariantCulture));
        }
        return (null, null);
    }

    public static string CamelCase(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public static ConfigValueKind KindOf(Type t, out Type? elementType, out ConfigValueKind elementKind)
    {
        elementType = null;
        elementKind = ConfigValueKind.String;
        if (Scalar(t) is { } k) return k;
        Type? et = t.IsArray ? t.GetElementType() : null;
        if (et is null && t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>) ||
                def == typeof(IEnumerable<>) || def == typeof(ICollection<>) || def == typeof(IReadOnlyCollection<>))
                et = t.GetGenericArguments()[0];
        }
        if (et is not null)
        {
            elementKind = Scalar(et) ?? throw new ArgumentException($"config lists hold numbers, text, bools, enums or durations, not {et.Name}");
            elementType = et;
            return ConfigValueKind.List;
        }
        if (t.IsClass && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null && !typeof(IEnumerable).IsAssignableFrom(t))
            return ConfigValueKind.Section;
        throw new ArgumentException($"config entries cannot be of type {t.Name}");
    }

    private static ConfigValueKind? Scalar(Type t)
    {
        if (t == typeof(bool)) return ConfigValueKind.Bool;
        if (t == typeof(string)) return ConfigValueKind.String;
        if (t == typeof(TimeSpan)) return ConfigValueKind.Duration;
        if (t.IsEnum) return ConfigValueKind.Enum;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(sbyte) ||
            t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong))
            return ConfigValueKind.Int;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return ConfigValueKind.Float;
        return null;
    }

    // ------------------------------------------------------------------------------------------ values ---------

    /// <summary>A file value: converted, clamped into range (with a warning); null when unusable (warning given).</summary>
    public static bool FromJson(ConfigNode node, JsonElement el, out object? value, List<string> warnings)
    {
        if (node.Kind == ConfigValueKind.List)
        {
            value = null;
            if (el.ValueKind != JsonValueKind.Array)
            {
                warnings.Add($"\"{node.Path}\" must be a list ([...]); using the default");
                return false;
            }
            var items = new List<object?>();
            var i = 0;
            foreach (var item in el.EnumerateArray())
            {
                if (ScalarFromJson(node, node.ElementKind, node.ElementType!, item, $"{node.Path}[{i}]", true, out var v, out var problem))
                {
                    if (problem is not null) warnings.Add(problem);
                    items.Add(v);
                }
                else warnings.Add($"{problem}; dropped");
                i++;
            }
            value = MakeList(node, items);
            return true;
        }
        if (ScalarFromJson(node, node.Kind, node.ClrType, el, node.Path, true, out value, out var why))
        {
            if (why is not null) warnings.Add(why);
            return true;
        }
        warnings.Add($"{why}; using the default {Format(node, node.Default)}");
        return false;
    }

    /// <summary>A value set through the API (or the dashboard): any CLR value, JSON or text. Out of range is an error.</summary>
    public static bool FromObject(ConfigNode node, object? input, out object? value, out string error)
    {
        error = "";
        value = null;
        if (input is JsonNode jn) input = JsonSerializer.SerializeToElement(jn);
        if (input is JsonElement je)
        {
            var warnings = new List<string>();
            if (node.Kind == ConfigValueKind.List)
            {
                if (!FromJson(node, je, out value, warnings) || warnings.Count > 0)
                {
                    error = warnings.Count > 0 ? warnings[0] : $"\"{node.Path}\" must be a list";
                    return false;
                }
                return true;
            }
            if (!ScalarFromJson(node, node.Kind, node.ClrType, je, node.Path, false, out value, out var why))
            {
                error = why ?? "invalid value";
                return false;
            }
            return true;
        }
        if (node.Kind == ConfigValueKind.List)
        {
            if (input is string s)
            {
                try
                {
                    using var doc = JsonDocument.Parse(s.TrimStart().StartsWith('[') ? s : "[" + s + "]", ReadOptions);
                    return FromObject(node, doc.RootElement.Clone(), out value, out error);
                }
                catch (JsonException e)
                {
                    error = $"\"{node.Path}\": not a list: {e.Message}";
                    return false;
                }
            }
            if (input is not IEnumerable seq)
            {
                error = $"\"{node.Path}\" must be a list";
                return false;
            }
            var items = new List<object?>();
            foreach (var item in seq)
            {
                if (!ScalarFromObject(node, node.ElementKind, node.ElementType!, item, out var v, out error)) return false;
                items.Add(v);
            }
            value = MakeList(node, items);
            return true;
        }
        return ScalarFromObject(node, node.Kind, node.ClrType, input, out value, out error);
    }

    private static bool ScalarFromObject(ConfigNode node, ConfigValueKind kind, Type type, object? input, out object? value, out string error)
    {
        value = null;
        error = "";
        string? why;
        switch (input)
        {
            case null:
                error = $"\"{node.Path}\" needs a value";
                return false;
            case string s:
                if (!ParseText(node, kind, type, s, false, out value, out why))
                {
                    error = why ?? "invalid value";
                    return false;
                }
                return true;
            case TimeSpan ts when kind == ConfigValueKind.Duration:
                return CheckDuration(node, ts, false, out value, out error);
            case Enum e when kind == ConfigValueKind.Enum:
                if (e.GetType() != type)
                {
                    error = $"\"{node.Path}\" takes {type.Name}, not {e.GetType().Name}";
                    return false;
                }
                value = e;
                return true;
            case bool b when kind == ConfigValueKind.Bool:
                value = b;
                return true;
            case IConvertible c when kind is ConfigValueKind.Int or ConfigValueKind.Float or ConfigValueKind.Duration && input is not bool:
                double d;
                try
                {
                    d = c.ToDouble(CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    error = $"\"{node.Path}\" needs a number";
                    return false;
                }
                if (kind == ConfigValueKind.Duration) return CheckDuration(node, TimeSpan.FromSeconds(d), false, out value, out error);
                return CheckNumber(node, kind, type, d, false, out value, out error);
            default:
                return ParseText(node, kind, type, Convert.ToString(input, CultureInfo.InvariantCulture) ?? "", false, out value, out why)
                    || Fail(why, out error);
        }
    }

    private static bool Fail(string? why, out string error)
    {
        error = why ?? "invalid value";
        return false;
    }

    private static bool ScalarFromJson(ConfigNode node, ConfigValueKind kind, Type type, JsonElement el, string path, bool clamp,
        out object? value, out string? problem)
    {
        value = null;
        problem = null;
        switch (el.ValueKind)
        {
            case JsonValueKind.True or JsonValueKind.False when kind == ConfigValueKind.Bool:
                value = el.GetBoolean();
                return true;
            case JsonValueKind.Number when kind is ConfigValueKind.Int or ConfigValueKind.Float:
            {
                var d = el.GetDouble();
                if (!CheckNumber(node, kind, type, d, clamp, out value, out var error, path))
                {
                    problem = error;
                    return false;
                }
                if (clamp && error.Length > 0) problem = error;
                return true;
            }
            case JsonValueKind.Number when kind == ConfigValueKind.Duration:
            {
                if (!CheckDuration(node, TimeSpan.FromSeconds(el.GetDouble()), clamp, out value, out var error, path))
                {
                    problem = error;
                    return false;
                }
                if (clamp && error.Length > 0) problem = error;
                return true;
            }
            case JsonValueKind.String:
                return ParseText(node, kind, type, el.GetString()!, clamp, out value, out problem, path);
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False when kind == ConfigValueKind.String:
                value = el.GetRawText();
                return true;
            case JsonValueKind.Number when kind == ConfigValueKind.Enum:
            {
                var raw = el.TryGetInt64(out var n) ? Enum.ToObject(type, n) : null;
                if (raw is not null && Enum.IsDefined(type, raw))
                {
                    value = raw;
                    return true;
                }
                problem = $"\"{path}\" = {el.GetRawText()} is not one of {string.Join(", ", Enum.GetNames(type))}";
                return false;
            }
            default:
                problem = $"\"{path}\" = {Short(el.GetRawText())} is not a {KindName(kind)}";
                return false;
        }
    }

    private static bool ParseText(ConfigNode node, ConfigValueKind kind, Type type, string s, bool clamp, out object? value,
        out string? problem, string? path = null)
    {
        path ??= node.Path;
        value = null;
        problem = null;
        var t = s.Trim();
        switch (kind)
        {
            case ConfigValueKind.String:
                value = s;
                return true;
            case ConfigValueKind.Bool:
                switch (t.ToLowerInvariant())
                {
                    case "true" or "on" or "yes" or "1":
                        value = true;
                        return true;
                    case "false" or "off" or "no" or "0":
                        value = false;
                        return true;
                }
                problem = $"\"{path}\" = \"{Short(s)}\" is not true/false";
                return false;
            case ConfigValueKind.Int or ConfigValueKind.Float:
            {
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    problem = $"\"{path}\" = \"{Short(s)}\" is not a number";
                    return false;
                }
                if (!CheckNumber(node, kind, type, d, clamp, out value, out var error, path))
                {
                    problem = error;
                    return false;
                }
                if (clamp && error.Length > 0) problem = error;
                return true;
            }
            case ConfigValueKind.Enum:
            {
                foreach (var name in Enum.GetNames(type))
                    if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase))
                    {
                        value = Enum.Parse(type, name);
                        return true;
                    }
                problem = $"\"{path}\" = \"{Short(s)}\" is not one of {string.Join(", ", Enum.GetNames(type))}";
                return false;
            }
            case ConfigValueKind.Duration:
            {
                if (!Durations.TryParse(t, out var ts))
                {
                    problem = $"\"{path}\" = \"{Short(s)}\" is not a duration (e.g. \"90s\", \"5m\", \"1h30m\", \"00:05:00\")";
                    return false;
                }
                if (!CheckDuration(node, ts, clamp, out value, out var error, path))
                {
                    problem = error;
                    return false;
                }
                if (clamp && error.Length > 0) problem = error;
                return true;
            }
        }
        problem = $"\"{path}\": unsupported type";
        return false;
    }

    /// <summary>Range and CLR-type checks. With <paramref name="clamp"/>, out-of-range values are clamped and
    /// <paramref name="error"/> holds the warning; without, they fail.</summary>
    private static bool CheckNumber(ConfigNode node, ConfigValueKind kind, Type type, double d, bool clamp, out object? value,
        out string error, string? path = null)
    {
        path ??= node.Path;
        value = null;
        error = "";
        if (!double.IsFinite(d))
        {
            error = $"\"{path}\" must be a finite number";
            return false;
        }
        var (lo, hi) = TypeRange(type);
        var min = Math.Max(node.Min ?? double.NegativeInfinity, lo);
        var max = Math.Min(node.Max ?? double.PositiveInfinity, hi);
        if (kind == ConfigValueKind.Int)
        {
            if (Math.Abs(d - Math.Round(d)) > 1e-9)
            {
                if (!clamp)
                {
                    error = $"\"{path}\" must be a whole number";
                    return false;
                }
                error = $"\"{path}\" = {d.ToString(CultureInfo.InvariantCulture)} is not a whole number; using {Math.Round(d)}";
                d = Math.Round(d);
            }
            min = Math.Ceiling(min);
            max = Math.Floor(max);
        }
        if (d < min || d > max)
        {
            var range = $"{Num(node.Min ?? lo)}..{Num(node.Max ?? hi)}";
            if (!clamp)
            {
                error = $"\"{path}\" must be in {range} (got {Num(d)})";
                return false;
            }
            var c = Math.Clamp(d, min, max);
            error = $"\"{path}\" = {Num(d)} is outside {range}; using {Num(c)}";
            d = c;
        }
        value = type == typeof(double) ? d
            : type == typeof(float) ? (float)d
            : type == typeof(decimal) ? (decimal)d
            : Convert.ChangeType(Math.Round(d), type, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool CheckDuration(ConfigNode node, TimeSpan ts, bool clamp, out object? value, out string error, string? path = null)
    {
        path ??= node.Path;
        error = "";
        value = ts;
        var s = ts.TotalSeconds;
        if (node.Min is { } min && s < min || node.Max is { } max && s > max)
        {
            var lo = node.Min is { } a ? TimeSpan.FromSeconds(a) : TimeSpan.Zero;
            var hi = node.Max is { } b ? TimeSpan.FromSeconds(b) : TimeSpan.MaxValue;
            var range = $"{Durations.Format(lo)}..{(node.Max is null ? "" : Durations.Format(hi))}";
            if (!clamp)
            {
                error = $"\"{path}\" must be in {range} (got {Durations.Format(ts)})";
                value = null;
                return false;
            }
            var c = ts < lo ? lo : ts > hi ? hi : ts;
            error = $"\"{path}\" = {Durations.Format(ts)} is outside {range}; using {Durations.Format(c)}";
            value = c;
        }
        return true;
    }

    private static (double, double) TypeRange(Type t) =>
        t == typeof(int) ? (int.MinValue, int.MaxValue)
        : t == typeof(long) ? (-9.2e18, 9.2e18)
        : t == typeof(short) ? (short.MinValue, short.MaxValue)
        : t == typeof(byte) ? (0, 255)
        : t == typeof(sbyte) ? (-128, 127)
        : t == typeof(ushort) ? (0, ushort.MaxValue)
        : t == typeof(uint) ? (0, uint.MaxValue)
        : t == typeof(ulong) ? (0, 1.8e19)
        : t == typeof(float) ? (float.MinValue, float.MaxValue)
        : (double.MinValue, double.MaxValue);

    private static object MakeList(ConfigNode node, List<object?> items)
    {
        var et = node.ElementType!;
        if (node.ClrType.IsArray)
        {
            var arr = Array.CreateInstance(et, items.Count);
            for (var i = 0; i < items.Count; i++) arr.SetValue(items[i], i);
            return arr;
        }
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(et))!;
        foreach (var i in items) list.Add(i);
        return list;
    }

    /// <summary>A copy of a value (lists are copied so the running value and the default never share one).</summary>
    public static object? Copy(ConfigNode node, object? v)
    {
        if (node.Kind != ConfigValueKind.List || v is not IEnumerable seq) return v;
        return MakeList(node, seq.Cast<object?>().ToList());
    }

    public static bool ValueEquals(object? a, object? b)
    {
        if (a is IEnumerable ea && a is not string && b is IEnumerable eb && b is not string)
            return ea.Cast<object?>().SequenceEqual(eb.Cast<object?>());
        return Equals(a, b);
    }

    // ------------------------------------------------------------------------------------------ text -----------

    /// <summary>A value as JSON text.</summary>
    public static string Format(ConfigNode node, object? v)
    {
        if (v is null) return "null";
        if (node.Kind == ConfigValueKind.List)
        {
            var items = ((IEnumerable)v).Cast<object?>().Select(i => FormatScalar(node.ElementKind, i)).ToList();
            return "[" + string.Join(", ", items) + "]";
        }
        return FormatScalar(node.Kind, v);
    }

    private static string FormatScalar(ConfigValueKind kind, object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => JsonSerializer.Serialize(s, JsonText),
        TimeSpan ts => JsonSerializer.Serialize(Durations.Format(ts), JsonText),
        Enum e => JsonSerializer.Serialize(e.ToString(), JsonText),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(v.ToString(), JsonText),
    };

    /// <summary>A value as a JSON node (schema export).</summary>
    public static JsonNode? ToJson(ConfigNode node, object? v) => JsonNode.Parse(Format(node, v));

    private static string Num(double d) => d switch
    {
        >= 9e18 => "max",
        <= -9e18 => "min",
        _ => d.ToString("0.######", CultureInfo.InvariantCulture),
    };

    private static string Short(string s) => s.Length > 40 ? s[..40] + "..." : s;

    /// <summary>The comment lines written above an entry.</summary>
    public static IEnumerable<string> CommentLines(ConfigNode node)
    {
        if (node.Comment is { Length: > 0 } c)
            foreach (var line in c.Replace("\r\n", "\n").Split('\n'))
                yield return line.TrimEnd();
        if (node.IsSection)
        {
            if (node.Synced) yield return "Synced to clients.";
            yield break;
        }
        var meta = new List<string> { $"Default: {Format(node, node.Default)}" };
        if (node.Min is not null || node.Max is not null)
            meta.Add(node.Kind == ConfigValueKind.Duration
                ? $"Range: {(node.Min is { } a ? Durations.Format(TimeSpan.FromSeconds(a)) : "0s")} .. {(node.Max is { } b ? Durations.Format(TimeSpan.FromSeconds(b)) : "any")}"
                : $"Range: {(node.Min is { } lo ? Num(lo) : "any")} .. {(node.Max is { } hi ? Num(hi) : "any")}");
        if (node.Options.Length > 0) meta.Add($"One of: {string.Join(", ", node.Options)}");
        if (node.Kind == ConfigValueKind.Duration) meta.Add("e.g. \"90s\", \"5m\", \"1h30m\"");
        if (node.RequiresRestart) meta.Add("Requires a restart");
        if (node.Synced) meta.Add("Synced to clients");
        yield return string.Join(" | ", meta);
    }

    /// <summary>Writes a section's entries (values read from <paramref name="target"/>), then the keys the file had
    /// that the mod does not know, kept as written.</summary>
    public static void WriteSection(StringBuilder sb, IReadOnlyList<ConfigNode> nodes, object target, int indent,
        Func<ConfigNode, object, object?> valueOf, IReadOnlyDictionary<string, List<(string Key, string Raw)>> unknown, string sectionPath)
    {
        var pad = new string(' ', indent * 2);
        unknown.TryGetValue(sectionPath, out var extra);
        var count = nodes.Count + (extra?.Count ?? 0);
        var i = 0;
        foreach (var node in nodes)
        {
            i++;
            if (i > 1) sb.Append('\n');
            foreach (var line in CommentLines(node)) sb.Append(pad).Append("// ").Append(line).Append('\n');
            sb.Append(pad).Append(JsonSerializer.Serialize(node.Key, JsonText)).Append(": ");
            if (node.IsSection)
            {
                var child = node.Get(target) ?? Activator.CreateInstance(node.ClrType)!;
                sb.Append("{\n");
                WriteSection(sb, node.Children, child, indent + 1, valueOf, unknown, node.Path);
                sb.Append(pad).Append('}');
            }
            else sb.Append(Format(node, valueOf(node, target)));
            sb.Append(i < count ? ",\n" : "\n");
        }
        if (extra is null) return;
        foreach (var (key, raw) in extra)
        {
            i++;
            sb.Append('\n').Append(pad).Append("// Unknown key: not used by the mod, kept as written.\n");
            sb.Append(pad).Append(JsonSerializer.Serialize(key, JsonText)).Append(": ").Append(raw);
            sb.Append(i < count ? ",\n" : "\n");
        }
    }
}

/// <summary>Durations as text: "250ms", "90s", "5m", "1h30m", "2d", "1.5h", "00:05:00", or a number of seconds.</summary>
internal static class Durations
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToLowerInvariant();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            if (!double.IsFinite(secs) || secs < 0 || secs > 1e10) return false;
            value = TimeSpan.FromSeconds(secs);
            return true;
        }
        if (s.Contains(':'))
            return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out value) && value >= TimeSpan.Zero;
        var total = 0.0;
        var i = 0;
        var any = false;
        while (i < s.Length)
        {
            var start = i;
            while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
            if (i == start) return false;
            if (!double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;
            var u = i;
            while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
            var unit = s[u..i];
            var factor = unit switch
            {
                "ms" => 0.001,
                "s" or "sec" or "secs" or "second" or "seconds" => 1,
                "m" or "min" or "mins" or "minute" or "minutes" => 60,
                "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
                "d" or "day" or "days" => 86400,
                _ => double.NaN,
            };
            if (double.IsNaN(factor)) return false;
            total += n * factor;
            any = true;
            while (i < s.Length && s[i] == ' ') i++;
        }
        if (!any || total > 1e10) return false;
        value = TimeSpan.FromSeconds(total);
        return true;
    }

    public static string Format(TimeSpan t)
    {
        if (t == TimeSpan.MaxValue) return "max";
        if (t < TimeSpan.Zero) return "-" + Format(-t);
        if (t == TimeSpan.Zero) return "0s";
        if (t.TotalSeconds < 1) return $"{t.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms";
        var sb = new StringBuilder();
        if (t.Days > 0) sb.Append(t.Days).Append('d');
        if (t.Hours > 0) sb.Append(t.Hours).Append('h');
        if (t.Minutes > 0) sb.Append(t.Minutes).Append('m');
        var s = t.Seconds + t.Milliseconds / 1000.0;
        if (s > 0) sb.Append(s.ToString("0.###", CultureInfo.InvariantCulture)).Append('s');
        return sb.ToString();
    }
}
