// SPDX-License-Identifier: MIT
// The list fields of a registry entry. Three shapes, so that only the edits that compose across mods compile:
//   RegistryValueList<T>  a list of plain values (perks): Clear, Append, Remove (by value), Replace, and one element set by
//                         index (perks[0], as a data pack's element edit)
//   RegistryEntryList<T>  a list of entries with fields (a recipe's inputs): Clear, AppendNew (the new element's fields go in
//                         the append), RemoveWhere, and the entries' own fields
//   RegistryFixedList<T>  a list of fixed length (xpPerLevel): read and set one element at a time
// The list operations are recorded by value, as the data section's "remove" names values: two mods' removals and appends
// stack. An element set by index is an absolute edit of that element, as a data pack's "perks[0]" is.
using System;
using System.Collections;
using System.Collections.Generic;

namespace Kiln.Registry;

/// <summary>A list field of plain values (perks, ...). Reading gives the list as the stage sees it; Clear and Replace are
/// absolute list edits, Append and Remove relative ones (they stack with other mods' edits).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public sealed class RegistryValueList<T> : IReadOnlyList<T>
{
    private readonly IRegistryCells _cells;
    private readonly int _field;
    private readonly Func<IRegistryCells, int, T> _element;
    private readonly Action<IRegistryCells, int, T>? _set;

    internal RegistryValueList(IRegistryCells cells, int field, Func<IRegistryCells, int, T> element, Action<IRegistryCells, int, T>? set = null)
    {
        _cells = cells;
        _field = field;
        _element = element;
        _set = set;
    }

    /// <summary>How many values the list has.</summary>
    public int Count => _cells.Count(_field);

    /// <summary>The value at an index; setting it records an absolute edit of that element (a data pack's <c>"perks[0]"</c>,
    /// since 0.2.0-dev.32). ArgumentOutOfRangeException outside the list.</summary>
    public T this[int index]
    {
        get => (uint)index < (uint)Count ? _element(_cells, index) : throw Outside(index);
        set
        {
            if ((uint)index >= (uint)Count)
                throw Outside(index);
            if (_set is null)
                throw new InvalidOperationException("the list is read-only");
            _set(_cells, index, value);
        }
    }

    private ArgumentOutOfRangeException Outside(int index) => new(nameof(index), index, $"the list has {Count} values");

    /// <summary>Empties the list.</summary>
    public void Clear() => _cells.ListOp(_field, RegistryListOp.Clear, Array.Empty<object?>());

    /// <summary>Appends values.</summary>
    public void Append(params T[] values) => _cells.ListOp(_field, RegistryListOp.Append, Box(values, nameof(values)));

    /// <summary>Removes every element equal to one of these values.</summary>
    public void Remove(params T[] values) => _cells.ListOp(_field, RegistryListOp.Remove, Box(values, nameof(values)));

    /// <summary>Replaces the whole list with these values.</summary>
    public void Replace(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var boxed = new List<object?>();
        foreach (var v in values)
            boxed.Add(v);
        _cells.ListOp(_field, RegistryListOp.Replace, boxed);
    }

    private static object?[] Box(T[] values, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Length == 0)
            throw new ArgumentException("no values", name);
        var boxed = new object?[values.Length];
        for (int i = 0; i < values.Length; i++)
            boxed[i] = values[i];
        return boxed;
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            yield return _element(_cells, i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A list field whose elements have fields of their own (a recipe's inputs, a drop table's entries). AppendNew adds
/// an element whose fields you then set; RemoveWhere removes the elements a condition picks, by their values; Clear empties
/// the list.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public sealed class RegistryEntryList<T> : IReadOnlyList<T>
{
    private readonly IRegistryCells _cells;
    private readonly int _field;
    private readonly Func<IRegistryCells, int, T> _element;
    private readonly Func<IRegistryCells, int, IReadOnlyDictionary<string, object?>> _snapshot;

    internal RegistryEntryList(IRegistryCells cells, int field, Func<IRegistryCells, int, T> element,
        Func<IRegistryCells, int, IReadOnlyDictionary<string, object?>> snapshot)
    {
        _cells = cells;
        _field = field;
        _element = element;
        _snapshot = snapshot;
    }

    /// <summary>How many elements the list has.</summary>
    public int Count => _cells.Count(_field);

    /// <summary>The element at an index. ArgumentOutOfRangeException outside the list.</summary>
    public T this[int index] => (uint)index < (uint)Count ? _element(_cells, index)
        : throw new ArgumentOutOfRangeException(nameof(index), index, $"the list has {Count} elements");

    /// <summary>Empties the list.</summary>
    public void Clear() => _cells.ListOp(_field, RegistryListOp.Clear, Array.Empty<object?>());

    /// <summary>Appends a blank element and returns it, to set its fields.</summary>
    public T AppendNew()
    {
        _cells.ListOp(_field, RegistryListOp.Append, new object?[] { null });
        return _element(_cells, Count - 1);
    }

    /// <summary>Removes every element the condition picks; the edit names them by their values, so it stacks with other
    /// mods' edits of the same list. Returns how many it picked.</summary>
    public int RemoveWhere(Func<T, bool> where)
    {
        ArgumentNullException.ThrowIfNull(where);
        var picked = new List<object?>();
        int n = Count;
        for (int i = 0; i < n; i++)
            if (where(_element(_cells, i)))
                picked.Add(_snapshot(_cells, i));
        if (picked.Count > 0)
            _cells.ListOp(_field, RegistryListOp.Remove, picked);
        return picked.Count;
    }

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            yield return _element(_cells, i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A list field of fixed length (xpPerLevel: exactly 5; a value per flame level): read and set one element at a
/// time; it never grows or shrinks.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public sealed class RegistryFixedList<T> : IReadOnlyList<T>
{
    private readonly IRegistryCells _cells;
    private readonly int _field;
    private readonly Func<IRegistryCells, int, T> _get;
    private readonly Action<IRegistryCells, int, T> _set;

    internal RegistryFixedList(IRegistryCells cells, int field, Func<IRegistryCells, int, T> get, Action<IRegistryCells, int, T> set)
    {
        _cells = cells;
        _field = field;
        _get = get;
        _set = set;
    }

    /// <summary>How many elements the list has (its fixed length).</summary>
    public int Count => _cells.Count(_field);

    /// <summary>The element at an index; setting it records an absolute edit of that element. ArgumentOutOfRangeException
    /// outside the list.</summary>
    public T this[int index]
    {
        get => (uint)index < (uint)Count ? _get(_cells, index) : throw Outside(index);
        set
        {
            if ((uint)index >= (uint)Count)
                throw Outside(index);
            _set(_cells, index, value);
        }
    }

    private ArgumentOutOfRangeException Outside(int index) => new(nameof(index), index, $"the list has exactly {Count} elements");

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator()
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            yield return _get(_cells, i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
