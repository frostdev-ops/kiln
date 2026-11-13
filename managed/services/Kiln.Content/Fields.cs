// SPDX-License-Identifier: MIT
// Typed paths for kiln.content and kiln.gamedata: what the generated Kiln.GameIds.GameFields hands out. A Field<T> is the
// same string path a data pack writes, with the section it belongs to and the type of its value.
// Guide: docs/kiln/modding/typed-fields.md.
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A path into a game-data entry with the type of its value: <c>GameFields.Items.DamageSetup.DmgMod</c> is a
/// <c>Field&lt;float&gt;</c> with the path <c>damageSetup.dmgMod</c>.</summary>
/// <param name="Section">The catalog section it belongs to: <c>items</c>, <c>templates</c>, ...</param>
/// <param name="Path">The string a data pack writes.</param>
public readonly record struct Field<T>(string Section, string Path)
{
    /// <summary>A path below this one that the catalog does not list, such as a field of one type of a list of variants:
    /// <c>Colliders[0].DataArray[0].Below&lt;float&gt;("shape.radius")</c>.</summary>
    public Field<TBelow> Below<TBelow>(string rest) => new(Section, FieldPath.Join(Path, rest));

    public override string ToString() => Path;
}

/// <summary>A field Kiln will not change: an identity field (ids, GUIDs) or a name. It can be read.</summary>
public readonly record struct ReadOnlyField<T>(string Section, string Path)
{
    public override string ToString() => Path;
}

/// <summary>The value of a <see cref="Field{T}"/>. It is a type of its own so that the field alone decides <c>T</c>: a plain
/// <c>5</c> is accepted for a <c>ushort</c> field, and a <c>1.5</c> (a double) is a compile error for a <c>float</c> one.</summary>
public readonly struct FieldValue<T>
{
    private FieldValue(T value) => Value = value;

    public T Value { get; }

    public static implicit operator FieldValue<T>(T value) => new(value);
}

/// <summary>Paths for generated code.</summary>
public static class FieldPath
{
    /// <summary>The path of a list's element. <paramref name="count"/> &gt; 0: the list has that many.</summary>
    public static string Element(string list, int index, int count = 0)
    {
        if (index < 0 || (count > 0 && index >= count))
            throw new ArgumentOutOfRangeException(nameof(index), index, count > 0 ? $"{list} has {count} entries" : $"{list}: an index is 0 or more");
        return list + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
    }

    internal static string Join(string path, string rest) => rest.StartsWith('[') ? path + rest : path + "." + rest;
}

/// <summary>Field values for a definition, by typed path: <c>new FieldValues { { GameFields.Items.MaxStackSize, 1 } }</c>. It
/// is what <c>ItemDefinition.Fields</c> and <c>RecipeDefinition.Fields</c> take; <c>GameDataClone.Values</c> is one.</summary>
public sealed class FieldValues : IReadOnlyDictionary<string, object>
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);

    public void Add<T>(Field<T> field, FieldValue<T> value)
    {
        _values[field.Path] = FieldConvert.ToJson(value.Value) ?? throw new ArgumentNullException(nameof(value), $"{field.Path} needs a value");
        _sections[field.Path] = field.Section;
    }

    /// <summary>Every field must belong to <paramref name="section"/>.</summary>
    internal void Require(string section, string what)
    {
        foreach (var (path, s) in _sections)
            if (s != section) throw new ArgumentException($"{what}: {path} is a field of {s}, not of {section}");
    }

    internal IEnumerable<KeyValuePair<string, JsonNode>> Json => _values.Select(kv => new KeyValuePair<string, JsonNode>(kv.Key, (JsonNode)kv.Value));

    public object this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<object> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out object value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class FieldConvert
{
    /// <summary>The value as a data pack writes it.</summary>
    public static JsonNode? ToJson<T>(T value) => value switch
    {
        null => null,
        bool v => JsonValue.Create(v),
        byte v => JsonValue.Create(v),
        sbyte v => JsonValue.Create(v),
        short v => JsonValue.Create(v),
        ushort v => JsonValue.Create(v),
        int v => JsonValue.Create(v),
        uint v => JsonValue.Create(v),
        long v => JsonValue.Create(v),
        ulong v => JsonValue.Create(v),
        float v => JsonValue.Create(v),
        double v => JsonValue.Create(v),
        TimeSpan v => JsonValue.Create(v.TotalSeconds),
        Guid v => JsonValue.Create(v.ToString("D")),
        string v => JsonValue.Create(v),
        Enum v => EnumJson(v),
        JsonNode v => v.DeepClone(),
        _ => throw new ArgumentException($"a field cannot hold a {value.GetType().Name}"),
    };

    private static JsonNode EnumJson(Enum value)
    {
        if (!value.GetType().IsDefined(typeof(FlagsAttribute), false)) return JsonValue.Create(EnumName(value))!;
        return new JsonArray(EnumNames(value).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
    }

    private static IEnumerable<string> EnumNames(Enum flags)
    {
        var bits = Convert.ToUInt64(flags, CultureInfo.InvariantCulture);
        foreach (Enum one in Enum.GetValues(flags.GetType()))
        {
            var b = Convert.ToUInt64(one, CultureInfo.InvariantCulture);
            if (b != 0 && (bits & b) == b) yield return EnumName(one);
        }
    }

    // The game's name of a member: its own name, unless the name is not a C# identifier (then the generator says so).
    private static string EnumName(Enum value)
    {
        var name = value.ToString();
        var attr = value.GetType().GetField(name)?.GetCustomAttribute<EnumMemberAttribute>();
        return attr?.Value ?? name;
    }

    /// <summary>Flag sets as kiln.content writes them: <c>A,B</c>.</summary>
    public static string FlatEnum(Enum value) =>
        value.GetType().IsDefined(typeof(FlagsAttribute), false) ? string.Join(",", EnumNames(value)) : EnumName(value);

    private static object ParseEnum(Type type, string names)
    {
        ulong bits = 0;
        var flags = type.IsDefined(typeof(FlagsAttribute), false);
        foreach (var part in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Enum? hit = null;
            foreach (Enum one in Enum.GetValues(type))
                if (EnumName(one) == part)
                {
                    hit = one;
                    break;
                }
            if (hit is null) throw new InvalidOperationException($"\"{part}\" is not a value of {type.Name}: was this mod built against another game build?");
            if (!flags) return hit;
            bits |= Convert.ToUInt64(hit, CultureInfo.InvariantCulture);
        }
        return Enum.ToObject(type, bits);
    }

    /// <summary>A leaf's value as <typeparamref name="T"/>.</summary>
    public static T FromValue<T>(ContentValue v)
    {
        var t = typeof(T);
        object? r =
            t == typeof(bool) ? v.AsDouble != 0 :
            t == typeof(byte) ? (byte)v.Integer :
            t == typeof(sbyte) ? (sbyte)v.Integer :
            t == typeof(short) ? (short)v.Integer :
            t == typeof(ushort) ? (ushort)v.Integer :
            t == typeof(int) ? (int)v.Integer :
            t == typeof(uint) ? (uint)v.Integer :
            t == typeof(long) ? v.Integer :
            t == typeof(ulong) ? (ulong)v.Integer :
            t == typeof(float) ? (float)v.AsDouble :
            t == typeof(double) ? v.AsDouble :
            t == typeof(TimeSpan) ? TimeSpan.FromSeconds(v.Number) :
            t == typeof(Guid) ? v.Guid :
            t.IsEnum ? (v.Name is null ? Enum.ToObject(t, v.Integer) : ParseEnum(t, v.Name)) :
            t == typeof(string) ? v.Name ?? v.Guid.ToString() :
            throw new ArgumentException($"a field cannot hold a {t.Name}");
        return (T)r;
    }
}

/// <summary>Typed access to game data: the overloads of <c>Get</c>, <c>Original</c>, <c>Set</c> and <c>Reset</c> that take a
/// <see cref="Field{T}"/>. Tick thread, like the string paths they stand for; the same layers, the same owner.</summary>
public static class FieldAccess
{
    private static string Name(GameDataSection s) => s.ToString().ToLowerInvariant();

    private static string Name(ContentKind k) => k.ToString().ToLowerInvariant();

    private static void Require(string have, string section, string path)
    {
        if (have != section) throw new ArgumentException($"{path} is a field of {section}, not of {have}");
    }

    // ---- kiln.gamedata entries ----

    /// <summary>Sets a field as the calling mod. Returns how many leaves changed (a field below <c>All</c> fans out).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public static int Set<T>(this GameDataEntry entry, Field<T> field, FieldValue<T> value)
    {
        Require(Name(entry.Section), field.Section, field.Path);
        var json = FieldConvert.ToJson(value.Value) ?? throw new ArgumentNullException(nameof(value));
        return entry.SetJson(field.Path, json.ToJsonString());
    }

    /// <summary>Not possible: Kiln does not change identity fields and names.</summary>
    [Obsolete("This is an identity field or a name: Kiln does not change it.", error: true)]
    public static int Set<T>(this GameDataEntry entry, ReadOnlyField<T> field, FieldValue<T> value) =>
        throw new InvalidOperationException($"{field.Path} is read-only");

    /// <summary>The current value.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public static T Get<T>(this GameDataEntry entry, Field<T> field)
    {
        Require(Name(entry.Section), field.Section, field.Path);
        return Read<T>(entry, field.Path, original: false);
    }

    /// <inheritdoc cref="Get{T}(GameDataEntry, Field{T})"/>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public static T Get<T>(this GameDataEntry entry, ReadOnlyField<T> field)
    {
        Require(Name(entry.Section), field.Section, field.Path);
        return Read<T>(entry, field.Path, original: false);
    }

    /// <summary>The game's original value.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public static T Original<T>(this GameDataEntry entry, Field<T> field)
    {
        Require(Name(entry.Section), field.Section, field.Path);
        return Read<T>(entry, field.Path, original: true);
    }

    /// <summary>Takes the calling mod's layers of the field (and below) off. False when it had none.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.gamedata")]
    public static bool Reset<T>(this GameDataEntry entry, Field<T> field)
    {
        Require(Name(entry.Section), field.Section, field.Path);
        return entry.Reset(field.Path);
    }

    private static T Read<T>(GameDataEntry entry, string path, bool original)
    {
        if (typeof(T) == typeof(JsonNode) || typeof(T) == typeof(string))
        {
            var j = entry.GetJson(path);
            var node = j is JsonObject o ? o[original ? "original" : "value"] : j;
            if (typeof(T) == typeof(string)) return (T)(object)(node is JsonValue s && s.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "");
            return (T)(object)node?.DeepClone()!;
        }
        return FieldConvert.FromValue<T>(original ? entry.Original(path) : entry.Get(path));
    }

    // ---- kiln.content entries ----

    // Numbers, flags and enums go through kiln.content's flat schema when it has the path, which is what its other
    // setters use; references and what the flat schema lacks go through kiln.gamedata's walk of the same struct.
    private static ContentField? Flat(ContentEntry entry, string path, bool write)
    {
        var f = Content.Registry(entry.Kind).Field(path);
        return f is not null && (!write || f.Writable) ? f : null;
    }

    /// <inheritdoc cref="Set{T}(GameDataEntry, Field{T}, FieldValue{T})"/>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static int Set<T>(this ContentEntry entry, Field<T> field, FieldValue<T> value)
    {
        Require(Name(entry.Kind), field.Section, field.Path);
        if (Flat(entry, field.Path, write: true) is not null)
            switch (value.Value)
            {
                case bool v:
                    entry.Set(field.Path, v);
                    return 1;
                case byte or sbyte or short or ushort or int or uint or long:
                    entry.Set(field.Path, Convert.ToInt64(value.Value, CultureInfo.InvariantCulture));
                    return 1;
                case float v:
                    entry.Set(field.Path, double.Parse(v.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    return 1;
                case double v:
                    entry.Set(field.Path, v);
                    return 1;
                case TimeSpan v:
                    entry.Set(field.Path, v);
                    return 1;
                case Enum v:
                    entry.Set(field.Path, FieldConvert.FlatEnum(v));
                    return 1;
            }
        return entry.Data().Set(new Field<T>("items", field.Path), value);
    }

    /// <inheritdoc cref="Set{T}(GameDataEntry, ReadOnlyField{T}, FieldValue{T})"/>
    [Obsolete("This is an identity field or a name: Kiln does not change it.", error: true)]
    public static int Set<T>(this ContentEntry entry, ReadOnlyField<T> field, FieldValue<T> value) =>
        throw new InvalidOperationException($"{field.Path} is read-only");

    /// <inheritdoc cref="Get{T}(GameDataEntry, Field{T})"/>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static T Get<T>(this ContentEntry entry, Field<T> field)
    {
        Require(Name(entry.Kind), field.Section, field.Path);
        return ReadContent<T>(entry, field.Path, original: false);
    }

    /// <inheritdoc cref="Get{T}(GameDataEntry, Field{T})"/>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static T Get<T>(this ContentEntry entry, ReadOnlyField<T> field)
    {
        Require(Name(entry.Kind), field.Section, field.Path);
        return ReadContent<T>(entry, field.Path, original: false);
    }

    /// <inheritdoc cref="Original{T}(GameDataEntry, Field{T})"/>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static T Original<T>(this ContentEntry entry, Field<T> field)
    {
        Require(Name(entry.Kind), field.Section, field.Path);
        return ReadContent<T>(entry, field.Path, original: true);
    }

    /// <inheritdoc cref="Reset{T}(GameDataEntry, Field{T})"/>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static bool Reset<T>(this ContentEntry entry, Field<T> field)
    {
        Require(Name(entry.Kind), field.Section, field.Path);
        // whichever way the field was set: this mod's layer of it comes off
        var flat = Flat(entry, field.Path, write: false) is not null && entry.Reset(field.Path);
        return (entry.Kind == ContentKind.Items && entry.Data().Reset(field.Path)) || flat;
    }

    private static T ReadContent<T>(ContentEntry entry, string path, bool original)
    {
        var flat = Flat(entry, path, write: false);
        if (flat is not null && typeof(T) != typeof(JsonNode) && typeof(T) != typeof(string))
            return FieldConvert.FromValue<T>(original ? entry.Original(path) : entry.Get(path));
        return Read<T>(entry.Data(), path, original);
    }
}
