// SPDX-License-Identifier: MIT
// What the generated entry types read and write through. The registry stage (P2: Mod.OnRegistry) gives each entry a store
// of cells that reads the stage's view and records every set as an edit; the tests give a dictionary. Values are kept in
// Kiln's shapes: long for whole numbers, double for numbers, bool, string for an enum's Kiln name, string[] for flags,
// RegistryId for a ref, TimeSpan for a time.
using System;
using System.Collections.Generic;

namespace Kiln.Registry;

// Remove names values (a list of values: the values; a list of entries: each entry's fields), never an index.
internal enum RegistryListOp { Clear, Append, Remove, Replace }

internal interface IRegistryCells
{
    /// <summary>The value of a field (index: the list element, or -1); false when the entry has none.</summary>
    bool TryGet(int field, int index, out object? value);

    /// <summary>Records an absolute edit of a field (index: the list element, or -1).</summary>
    void Set(int field, int index, object? value);

    /// <summary>How many elements a list field has.</summary>
    int Count(int listField);

    /// <summary>Records a list edit.</summary>
    void ListOp(int listField, RegistryListOp op, IReadOnlyList<object?> values);
}

internal static class RegistryCells
{
    private static object? Get(IRegistryCells c, int field, int index) => c.TryGet(field, index, out var v) ? v : null;

    internal static int Int(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => 0,
        long l => checked((int)l),
        int i => i,
        double d => (int)Math.Round(d, MidpointRounding.AwayFromZero),
        var o => throw Mismatch(field, o, "a whole number"),
    };

    internal static double Double(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => 0,
        double d => d,
        long l => l,
        int i => i,
        var o => throw Mismatch(field, o, "a number"),
    };

    internal static bool Bool(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => false,
        bool b => b,
        var o => throw Mismatch(field, o, "true or false"),
    };

    internal static TimeSpan Time(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => TimeSpan.Zero,
        TimeSpan t => t,
        double d => TimeSpan.FromSeconds(d),
        long l => TimeSpan.FromSeconds(l),
        var o => throw Mismatch(field, o, "a duration"),
    };

    internal static RegistryId Id(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => default,
        RegistryId id => id,
        string s => new RegistryId(s),
        var o => throw Mismatch(field, o, "an id"),
    };

    internal static RegistryId? OptionalId(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => null,
        RegistryId id => id,
        string s => new RegistryId(s),
        var o => throw Mismatch(field, o, "an id or nothing"),
    };

    internal static void SetOptional(IRegistryCells c, int field, int index, RegistryId? value)
    {
        if (value is { Value: null })
            throw new ArgumentException("an empty registry id (set null to clear the reference)", nameof(value));
        c.Set(field, index, value);
    }

    /// <summary>An entry of a list as {field: value}: what a removal names.</summary>
    internal static IReadOnlyDictionary<string, object?> Snapshot(IRegistryCells c, int index, (string Field, int Id)[] leaves)
    {
        var d = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, id) in leaves)
            d[name] = c.TryGet(id, index, out var v) ? v : null;
        return d;
    }

    internal static string Text(IRegistryCells c, int field, int index) => Get(c, field, index) switch
    {
        null => "",
        string s => s,
        var o => throw Mismatch(field, o, "a name"),
    };

    internal static int Enum(IRegistryCells c, int field, int index, string[] names)
    {
        var o = Get(c, field, index);
        if (o is null)
            return 0;
        if (o is string s)
        {
            int at = Array.IndexOf(names, s);
            if (at >= 0)
                return at;
        }
        throw Mismatch(field, o, "one of " + string.Join(", ", names));
    }

    // An enum whose one value is no value (loot.fixedRarity's "none", C#'s null; dev.32 api-shape): the index of the others.
    internal static int? OptionalEnum(IRegistryCells c, int field, int index, string[] names, string none)
    {
        var o = Get(c, field, index);
        if (o is null || (o is string n && n == none))
            return null;
        if (o is string s)
        {
            int at = Array.IndexOf(names, s);
            if (at >= 0)
                return at;
        }
        throw Mismatch(field, o, none + " or one of " + string.Join(", ", names));
    }

    internal static void SetOptionalEnum(IRegistryCells c, int field, int index, string[] names, string none, int? value)
    {
        if (value is not { } v)
        {
            c.Set(field, index, none);
            return;
        }
        SetEnum(c, field, index, names, v);
    }

    internal static uint Flags(IRegistryCells c, int field, int index, string[] names)
    {
        var o = Get(c, field, index);
        if (o is null)
            return 0;
        if (o is IEnumerable<string> set)
        {
            uint bits = 0;
            foreach (var s in set)
            {
                int at = Array.IndexOf(names, s);
                if (at < 0)
                    throw Mismatch(field, s, "any of " + string.Join(", ", names));
                bits |= 1u << at;
            }
            return bits;
        }
        throw Mismatch(field, o, "a set of " + string.Join(", ", names));
    }

    internal static void Set(IRegistryCells c, int field, int index, int value) => c.Set(field, index, (long)value);
    internal static void Set(IRegistryCells c, int field, int index, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("a registry number is finite", nameof(value));
        c.Set(field, index, value);
    }
    internal static void Set(IRegistryCells c, int field, int index, bool value) => c.Set(field, index, value);
    internal static void Set(IRegistryCells c, int field, int index, TimeSpan value) => c.Set(field, index, value);
    internal static void Set(IRegistryCells c, int field, int index, RegistryId value)
    {
        if (value.Value is null)
            throw new ArgumentException("an empty registry id", nameof(value));
        c.Set(field, index, value);
    }

    internal static void Set(IRegistryCells c, int field, int index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        c.Set(field, index, value);
    }

    internal static void SetEnum(IRegistryCells c, int field, int index, string[] names, int value)
    {
        if ((uint)value >= (uint)names.Length)
            throw new ArgumentException($"{value} is not one of the field's values ({string.Join(", ", names)})", nameof(value));
        c.Set(field, index, names[value]);
    }

    internal static void SetFlags(IRegistryCells c, int field, int index, string[] names, uint value)
    {
        if (names.Length < 32 && value >> names.Length != 0)
            throw new ArgumentException($"0x{value:x} has flags the field does not have ({string.Join(", ", names)})", nameof(value));
        var set = new List<string>();
        for (int i = 0; i < names.Length; i++)
            if ((value & (1u << i)) != 0)
                set.Add(names[i]);
        c.Set(field, index, set.ToArray());
    }

    private static InvalidOperationException Mismatch(int field, object? value, string want) =>
        new($"registry field 0x{field:x8} holds {value?.GetType().Name ?? "null"} where {want} belongs");
}
