// SPDX-License-Identifier: MIT
// Checked reads and writes of the game's scalar values by their reflected kind: what Reflected components, events and
// resources use for every field. Pure (no game, no native calls), so it is unit-tested on its own.
// The checking rules follow Shrouded Engine Tweaks' documented write checks (MIT): a value must fit its field, an enum
// takes only its own values (by number or by name, any case), a bool is true/false or 0/1.
using System.Buffers.Binary;
using System.Globalization;

namespace Kiln;

/// <summary>The game's primitive kinds (kiln_types.h KILN_TYPE_*).</summary>
public enum GameTypeKind : byte
{
    None = 0, Bool = 1, U8, S8, U16, S16, U32, S32, U64, S64, F32, F64, Enum, Bitmask8, Bitmask16, Bitmask32, Bitmask64,
    Typedef, Struct, StaticArray, DsArray, DsString, DsOptional, DsVariant, BlobArray, BlobString, BlobOptional, BlobVariant,
    ObjectReference, Guid,
}

/// <summary>Reads and writes one scalar value with the game's layout and Kiln's checks.</summary>
public static class GameValueCodec
{
    /// <summary>True for kinds stored as plain numbers (bool, integers, floats, enums, bitmasks).</summary>
    public static bool IsScalar(GameTypeKind k) => k is >= GameTypeKind.Bool and <= GameTypeKind.Bitmask64;

    private static bool Signed(GameTypeKind k) => k is GameTypeKind.S8 or GameTypeKind.S16 or GameTypeKind.S32 or GameTypeKind.S64;

    private static bool ValidSize(GameTypeKind kind, int size) => kind switch
    {
        GameTypeKind.Bool or GameTypeKind.U8 or GameTypeKind.S8 or GameTypeKind.Bitmask8 => size == 1,
        GameTypeKind.U16 or GameTypeKind.S16 or GameTypeKind.Bitmask16 => size == 2,
        GameTypeKind.U32 or GameTypeKind.S32 or GameTypeKind.F32 or GameTypeKind.Bitmask32 => size == 4,
        GameTypeKind.U64 or GameTypeKind.S64 or GameTypeKind.F64 or GameTypeKind.Bitmask64 => size == 8,
        GameTypeKind.Enum => size is 1 or 2 or 4 or 8,
        _ => false,
    };

    private static void CheckSize(GameTypeKind kind, int size)
    {
        if (!ValidSize(kind, size)) throw new ArgumentException($"invalid {size}-byte {kind} layout");
    }

    /// <summary>The value at <paramref name="data"/>: bool, long (integers, enums, bitmasks), ulong (u64) or double (floats).</summary>
    public static object Read(ReadOnlySpan<byte> data, GameTypeKind kind, int size) => Read(data, kind, size, null);

    /// <summary>Reads a scalar, preserving declared enum values when their stored bits represent a negative value.</summary>
    public static object Read(ReadOnlySpan<byte> data, GameTypeKind kind, int size,
                              IReadOnlyList<(string Name, long Value)>? enumValues)
    {
        CheckSize(kind, size);
        if (data.Length < size) throw new ArgumentException($"{size} bytes needed, {data.Length} given");
        switch (kind)
        {
            case GameTypeKind.Bool: return data[0] != 0;
            case GameTypeKind.F32: return (double)BinaryPrimitives.ReadSingleLittleEndian(data);
            case GameTypeKind.F64: return BinaryPrimitives.ReadDoubleLittleEndian(data);
            case GameTypeKind.U64: return BinaryPrimitives.ReadUInt64LittleEndian(data);
        }
        if (!IsScalar(kind)) throw new NotSupportedException($"{kind} is not a scalar");
        ulong raw = size switch
        {
            1 => data[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(data), 4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(data), _ => throw new NotSupportedException($"{size}-byte {kind}"),
        };
        if (kind == GameTypeKind.Enum && enumValues != null)
        {
            var mask = size == 8 ? ulong.MaxValue : (1UL << (size * 8)) - 1;
            foreach (var entry in enumValues)
                if (((ulong)entry.Value & mask) == raw) return entry.Value;
        }
        if (Signed(kind) && size < 8 && ((raw >> (size * 8 - 1)) & 1) != 0) raw |= ulong.MaxValue << (size * 8);
        return (long)raw;
    }

    /// <summary>Checks <paramref name="value"/> against the field and writes it. Throws <see cref="ArgumentException"/>
    /// (nothing written) when it does not fit: out of range, not a whole number, not one of the enum's values.</summary>
    /// <param name="data">Where the field's bytes are.</param>
    /// <param name="kind">The field's kind.</param>
    /// <param name="size">The field's size in bytes.</param>
    /// <param name="value">bool, a whole number, a float, a numeric string, or an enum value's name.</param>
    /// <param name="enumValues">The enum's values (name, value); empty for an enum that lists none (any value).</param>
    /// <param name="field">The field's name, for error messages.</param>
    public static void Write(Span<byte> data, GameTypeKind kind, int size, object? value, IReadOnlyList<(string Name, long Value)>? enumValues = null,
                             string field = "value")
    {
        CheckSize(kind, size);
        if (data.Length < size) throw new ArgumentException($"{size} bytes needed, {data.Length} given");
        switch (kind)
        {
            case GameTypeKind.Bool:
                data[0] = value switch
                {
                    bool b => (byte)(b ? 1 : 0),
                    _ when TryInteger(value, out var i) && i is 0 or 1 => (byte)i,
                    _ => throw new ArgumentException($"{field} is a bool: true, false, 0 or 1"),
                };
                return;
            case GameTypeKind.F32:
            {
                var d = ToDouble(value, field);
                if (!double.IsFinite(d) || Math.Abs(d) > float.MaxValue) throw new ArgumentException($"{field} ({d}) does not fit a float32");
                BinaryPrimitives.WriteSingleLittleEndian(data, (float)d);
                return;
            }
            case GameTypeKind.F64:
            {
                var d = ToDouble(value, field);
                if (!double.IsFinite(d)) throw new ArgumentException($"{field} takes a finite number");
                BinaryPrimitives.WriteDoubleLittleEndian(data, d);
                return;
            }
        }
        if (!IsScalar(kind)) throw new NotSupportedException($"{field}: {kind} values are not written directly");
        long v;
        if (kind == GameTypeKind.Enum && value is string name && !TryInteger(value, out _))
        {
            var hit = enumValues?.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not { Name: not null } h) throw new ArgumentException($"{field}: '{name}' is not one of {Names(enumValues)}");
            v = h.Value;
        }
        else if (kind == GameTypeKind.U64 && (value is ulong || value is string))
        {
            if (!ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var u))
                throw new ArgumentException($"{field} takes an unsigned 64-bit whole number");
            BinaryPrimitives.WriteUInt64LittleEndian(data, u);
            return;
        }
        else if (!TryInteger(value, out v))
        {
            throw new ArgumentException($"{field} takes a whole number{(kind == GameTypeKind.Enum ? " or a name" : "")}, not {value ?? "null"}");
        }
        if (!Fits(v, kind, size)) throw new ArgumentException($"{field}: {v} does not fit a {size * 8}-bit {(Signed(kind) ? "signed" : "unsigned")} {kind}");
        if (kind == GameTypeKind.Enum && enumValues is { Count: > 0 } && !enumValues.Any(e => e.Value == v))
            throw new ArgumentException($"{field}: {v} is not one of {Names(enumValues)}");
        var raw = (ulong)v;
        switch (size)
        {
            case 1: data[0] = (byte)raw; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)raw); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)raw); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(data, raw); break;
            default: throw new NotSupportedException($"{size}-byte {kind}");
        }
    }

    /// <summary>Whether <paramref name="v"/> fits a field of that kind and size.</summary>
    public static bool Fits(long v, GameTypeKind kind, int size)
    {
        if (!ValidSize(kind, size)) return false;
        if (kind == GameTypeKind.Bool) return v is 0 or 1;
        if (kind is GameTypeKind.F32 or GameTypeKind.F64) return true;
        if (size == 8) return Signed(kind) || kind == GameTypeKind.Enum || v >= 0 || kind is >= GameTypeKind.Bitmask8 and <= GameTypeKind.Bitmask64;
        var bits = size * 8;
        if (Signed(kind)) return v >= -(1L << (bits - 1)) && v < (1L << (bits - 1));
        if (kind == GameTypeKind.Enum) return v >= -(1L << (bits - 1)) && v < (1L << bits);  // the game stores enums either way
        return v >= 0 && v < (1L << bits);
    }

    /// <summary>A value as text: numbers, bools, bitmasks in hex, enums as <c>Name (n)</c>.</summary>
    public static string Format(object value, GameTypeKind kind, IReadOnlyList<(string Name, long Value)>? enumValues = null)
    {
        switch (value)
        {
            case bool b: return b ? "true" : "false";
            case double d: return d.ToString("G9", CultureInfo.InvariantCulture);
            case ulong u: return u.ToString(CultureInfo.InvariantCulture);
            case long l when kind is >= GameTypeKind.Bitmask8 and <= GameTypeKind.Bitmask64: return "0x" + l.ToString("x", CultureInfo.InvariantCulture);
            case long l when kind == GameTypeKind.Enum:
                var n = enumValues?.FirstOrDefault(e => e.Value == l).Name;
                return n is null ? l.ToString(CultureInfo.InvariantCulture) : $"{n} ({l})";
            default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }

    private static string Names(IReadOnlyList<(string Name, long Value)>? values) =>
        values is null || values.Count == 0 ? "its values" : string.Join(", ", values.Take(12).Select(e => e.Name)) + (values.Count > 12 ? ", ..." : "");

    private static bool TryInteger(object? value, out long v)
    {
        switch (value)
        {
            case int i: v = i; return true;
            case long l: v = l; return true;
            case short s: v = s; return true;
            case byte b: v = b; return true;
            case sbyte sb: v = sb; return true;
            case ushort us: v = us; return true;
            case uint ui: v = ui; return true;
            case ulong ul when ul <= long.MaxValue: v = (long)ul; return true;
            case double d when double.IsFinite(d) && Math.Floor(d) == d && d >= long.MinValue && d < 9223372036854775808.0: v = (long)d; return true;
            case float f when float.IsFinite(f) && MathF.Floor(f) == f && f >= long.MinValue && f < 9223372036854775808.0: v = (long)f; return true;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p): v = p; return true;
            default: v = 0; return false;
        }
    }

    private static double ToDouble(object? value, string field) => value switch
    {
        double d => d,
        float f => f,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ when TryInteger(value, out var i) => i,
        _ => throw new ArgumentException($"{field} takes a number, not {value ?? "null"}"),
    };
}
