// SPDX-License-Identifier: MIT
// One typed view over every registry in a registry callback: any registry by name, its fields by Kiln path, typed by the
// generated catalog (RegistrySchema). It makes the same edits the generated entry types make (the same cells, the same IR),
// so a registry this Kiln.Registry has no hand-written property for (one a later schema adds) is reachable with no code of
// its own. Since 0.2.0-dev.32 (dev.32 J1).
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Kiln.Registry;

/// <summary>An entry of any registry, its fields by Kiln path (<c>"maxStack"</c>, <c>"inputs[2].count"</c>,
/// <c>"perks[1]"</c>), typed by <see cref="RegistrySchema"/>. What the generated entry types (<see cref="ItemEntry"/>, ...)
/// read and write, for every registry of the schema, including ones without a typed property on
/// <see cref="RegistryStage"/>. Get it from <see cref="RegistryStage.Entries(string)"/>.</summary>
[Experimental("KILNX031"), RegistryThreadOnly]
public sealed class RegistryEntry : IRegistryEntry
{
    private readonly IRegistryCells _cells;
    internal RegistryEntry(string registry, RegistryId id, IRegistryCells cells) => (Registry, Id, _cells) = (registry, id, cells);

    /// <inheritdoc/>
    public RegistryId Id { get; }

    /// <inheritdoc/>
    public string Registry { get; }

    /// <summary>The schema row of a field of this registry. <see cref="ArgumentException"/> when the registry has no such field.</summary>
    /// <param name="field">A Kiln field path; an index reads as the list's row (<c>inputs[2].count</c> is <c>inputs[].count</c>).</param>
    public RegistryFieldSpec Spec(string field) => Resolve(field, out _).Spec;

    /// <summary>The field's value: a whole number as <see cref="long"/>, a number as <see cref="double"/>, <see cref="bool"/>,
    /// an enum's Kiln name as <see cref="string"/>, flags as <see cref="string"/>[], an id as <see cref="RegistryId"/>, a time
    /// as <see cref="TimeSpan"/>; null when the entry has no value of it. A list field reads its element count through
    /// <see cref="Count"/> and its elements by index.</summary>
    /// <param name="field">A Kiln field path (<c>"maxStack"</c>, <c>"inputs[0].count"</c>, <c>"perks[1]"</c>).</param>
    /// <exception cref="ArgumentException">The registry has no such field, or a list's element needs an index.</exception>
    public object? Get(string field)
    {
        var (spec, index) = Resolve(field, out var element);
        if (spec.Type == RegistryFieldType.List && index < 0) throw new ArgumentException($"{Registry}.{spec.Path} is a list: read its elements by index or Count it", nameof(field));
        _ = element;
        return _cells.TryGet(spec.Id, index, out var value) ? value : null;
    }

    /// <summary>The field's value converted to <typeparamref name="T"/> (<see cref="int"/>, <see cref="long"/>,
    /// <see cref="float"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>, <see cref="string"/>[],
    /// <see cref="RegistryId"/>, <see cref="TimeSpan"/>); <see cref="InvalidCastException"/> when it does not convert. When the
    /// entry has no value of the field (a creature without the component it lives in), a nullable <typeparamref name="T"/>
    /// (<c>int?</c>, <see cref="RegistryId"/>?, <see cref="string"/>) reads null and any other throws
    /// <see cref="InvalidOperationException"/>, never a silent 0 or false (since 0.2.0-dev.32); <see cref="TryGet{T}"/> asks.</summary>
    /// <param name="field">A Kiln field path.</param>
    public T Get<T>(string field)
    {
        var value = Get(field);
        if (value is null)
            return default(T) is null ? default! : throw new InvalidOperationException($"{Registry}[{Id.Value}] has no value of {field}: read it as a nullable type or with TryGet");
        return As<T>(value, field);
    }

    /// <summary>The field's value converted to <typeparamref name="T"/>, as <see cref="Get{T}(string)"/>; false when the entry has
    /// no value of it (since 0.2.0-dev.32).</summary>
    /// <param name="field">A Kiln field path.</param>
    /// <param name="value">The value; default when there is none.</param>
    /// <exception cref="ArgumentException">The registry has no such field, or a list's element needs an index.</exception>
    /// <exception cref="InvalidCastException">The value does not convert to <typeparamref name="T"/>.</exception>
    public bool TryGet<T>(string field, [MaybeNullWhen(false)] out T value)
    {
        var v = Get(field);
        if (v is null) { value = default; return false; }
        value = As<T>(v, field);
        return true;
    }

    private T As<T>(object value, string field)
    {
        if (value is T t) return t;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is IConvertible && (target.IsPrimitive || target == typeof(decimal)))
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        throw new InvalidCastException($"{Registry}.{field} holds a {value.GetType().Name}, not a {typeof(T).Name}");
    }

    /// <summary>Sets the field (an absolute edit, in the callback's pass), checked against its schema type first: a whole
    /// number for an int, a number for a float, true or false, one of an enum's Kiln names, a <see cref="RegistryId"/>
    /// (or its text) for an id, a <see cref="TimeSpan"/> or seconds for a time. Ranges are checked where the edit is composed,
    /// as for every edit.</summary>
    /// <param name="field">A Kiln field path.</param>
    /// <param name="value">The value (null clears an optional id).</param>
    /// <exception cref="ArgumentException">No such field, a read-only one, a list without an index, or a value of another type.</exception>
    public void Set(string field, object? value)
    {
        var (spec, index) = Resolve(field, out var element);
        if (spec.ReadOnly) throw new ArgumentException($"{Registry}.{spec.Path} is read-only", nameof(field));
        if (spec.Type == RegistryFieldType.List && index < 0) throw new ArgumentException($"{Registry}.{spec.Path} is a list: set an element by index", nameof(field));
        _cells.Set(spec.Id, index, Checked(spec, element, value, field));
    }

    /// <summary>How many elements a list field has.</summary>
    /// <param name="listField">The list's Kiln path (<c>"inputs"</c>).</param>
    public int Count(string listField)
    {
        var (spec, _) = Resolve(listField, out _);
        if (spec.Type != RegistryFieldType.List) throw new ArgumentException($"{Registry}.{spec.Path} is not a list", nameof(listField));
        return _cells.Count(spec.Id);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Registry}[{Id.Value}]";

    // "inputs[2].count" -> (inputs[].count, 2); "perks[1]" -> (perks, 1) with the element's type; "maxStack" -> (maxStack, -1)
    private (RegistryFieldSpec Spec, int Index) Resolve(string field, out RegistryFieldType type)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        int index = -1;
        var open = field.IndexOf('[');
        if (open >= 0)
        {
            var close = field.IndexOf(']', open);
            if (close < 0 || !int.TryParse(field.AsSpan(open + 1, close - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out index) ||
                field.IndexOf('[', close) >= 0)
                throw new ArgumentException($"{Registry}.{field}: one index, a whole number in brackets", nameof(field));
        }
        var spec = RegistrySchema.Find(Registry + "." + field);
        if (spec is null && open >= 0 && field.EndsWith(']'))
        {
            var list = RegistrySchema.Find(Registry + "." + field[..open]);
            if (list is { Type: RegistryFieldType.List, Element: not RegistryFieldType.Struct and not null })
            {
                type = list.Element.Value;
                return (list, index);
            }
        }
        if (spec is null) throw new ArgumentException($"{Registry} has no field {RegistrySchema.Normalise(field)}", nameof(field));
        type = spec.Type == RegistryFieldType.List && index >= 0 && spec.Element is { } e ? e : spec.Type;
        return (spec, index);
    }

    private object? Checked(RegistryFieldSpec spec, RegistryFieldType type, object? value, string field)
    {
        string place = $"{Registry}.{field}";
        switch (type)
        {
            case RegistryFieldType.Int:
                return value switch
                {
                    int or long or short or byte or sbyte or ushort or uint => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                    double d when Math.Abs(d - Math.Round(d)) == 0 && !double.IsInfinity(d) => (long)d,
                    _ => throw new ArgumentException($"{place} takes a whole number", nameof(value)),
                };
            case RegistryFieldType.Float:
                return value switch
                {
                    double d when double.IsFinite(d) => d,
                    float f when float.IsFinite(f) => (double)f,
                    int or long or short or byte => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                    _ => throw new ArgumentException($"{place} takes a finite number", nameof(value)),
                };
            case RegistryFieldType.Bool:
                return value is bool b ? b : throw new ArgumentException($"{place} takes true or false", nameof(value));
            case RegistryFieldType.Enum:
                return value is string s && spec.Values.Contains(s) ? s
                    : throw new ArgumentException($"{place} takes one of {string.Join(", ", spec.Values)}", nameof(value));
            case RegistryFieldType.Flags:
                return value is IEnumerable<string> names && names.All(spec.Values.Contains) ? names.ToArray()
                    : throw new ArgumentException($"{place} takes a set of {string.Join(", ", spec.Values)}", nameof(value));
            case RegistryFieldType.Ref:
                return value switch
                {
                    null when spec.Optional => null,
                    RegistryId id when id.Value is not null => id,
                    string text when RegistryId.TryParse(text, out var parsed) => parsed,
                    _ => throw new ArgumentException($"{place} takes a Kiln id", nameof(value)),
                };
            case RegistryFieldType.Time:
                return value switch
                {
                    TimeSpan t => t,
                    double seconds when double.IsFinite(seconds) => TimeSpan.FromSeconds(seconds),
                    int or long => TimeSpan.FromSeconds(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                    _ => throw new ArgumentException($"{place} takes a TimeSpan or seconds", nameof(value)),
                };
            default:
                throw new ArgumentException($"{place} is a {type}: edit it through its fields or list operations", nameof(field));
        }
    }
}
