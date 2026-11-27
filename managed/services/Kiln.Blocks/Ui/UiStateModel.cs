// SPDX-License-Identifier: MIT
// The typed shapes of the richer UI blocks (dossier 09 §4.2, §5.2): a window's state (WindowBlock.State, BlockUi.State,
// BlockUi.SetState), a mod's own data for tables and trees (BlockUi.Bindings), and the events that report every committed
// write. In C# and, where a block file has them, the same shape in JSON: int64 and decimal values are canonical decimal
// strings there ("12", "-3.25"), so no tool rounds them through a double.
using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

// ---- state definitions (WindowBlock.State) -------------------------------------------------------------------------

/// <summary>One entry of a window's state (<see cref="WindowBlock.State"/>): a typed value the window's rows bind as
/// <c>state.&lt;key&gt;</c>. It lives while the player is in one world (closing and opening the window keeps it), starts at
/// its initial value in each world and is never saved: a config is the place for what must last.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(UiBoolState), "bool")]
[JsonDerivedType(typeof(UiInt64State), "int64")]
[JsonDerivedType(typeof(UiDecimalState), "decimal")]
[JsonDerivedType(typeof(UiStringState), "string")]
[JsonDerivedType(typeof(UiStringSetState), "stringSet")]
public abstract record UiStateDefinition;

/// <summary>A true/false state entry.</summary>
/// <param name="Initial">The value in each new world; false unless given.</param>
public sealed record UiBoolState(bool Initial = false) : UiStateDefinition;

/// <summary>A whole-number state entry in [<see cref="Min"/>, <see cref="Max"/>]. JSON: the numbers as strings.</summary>
/// <param name="Initial">The value in each new world, within <paramref name="Min"/> and <paramref name="Max"/>.</param>
/// <param name="Min">The smallest value a write may set; no bound unless given.</param>
/// <param name="Max">The largest value a write may set; no bound unless given.</param>
public sealed record UiInt64State(
    [property: JsonConverter(typeof(UiInt64StringConverter))] long Initial,
    [property: JsonConverter(typeof(UiInt64StringConverter))] long Min = long.MinValue,
    [property: JsonConverter(typeof(UiInt64StringConverter))] long Max = long.MaxValue) : UiStateDefinition;

/// <summary>A decimal state entry in [<see cref="Min"/>, <see cref="Max"/>] with at most <see cref="Scale"/> digits after
/// the point (0..6); a value with more is refused, never rounded. JSON: the numbers as strings.</summary>
/// <param name="Initial">The value in each new world, within <paramref name="Min"/> and <paramref name="Max"/>.</param>
/// <param name="Min">The smallest value a write may set; no bound unless given.</param>
/// <param name="Max">The largest value a write may set; no bound unless given.</param>
/// <param name="Scale">At most this many digits after the point, 0 to 6 (6 unless given).</param>
public sealed record UiDecimalState(
    [property: JsonConverter(typeof(UiDecimalStringConverter))] decimal Initial,
    [property: JsonConverter(typeof(UiDecimalStringConverter))] decimal Min = decimal.MinValue,
    [property: JsonConverter(typeof(UiDecimalStringConverter))] decimal Max = decimal.MaxValue,
    int Scale = 6) : UiStateDefinition;

/// <summary>A text state entry of at most <see cref="MaxBytes"/> UTF-8 bytes (up to 4096).</summary>
/// <param name="Initial">The text in each new world; empty unless given.</param>
/// <param name="MaxBytes">The longest text a write may set, in UTF-8 bytes (up to 4096; 256 unless given).</param>
public sealed record UiStringState(string Initial = "", int MaxBytes = 256) : UiStateDefinition;

/// <summary>A set of texts (unique, kept in ordinal order): what a tree's expanded branches bind to. At most
/// <see cref="MaxItems"/> items of <see cref="MaxItemBytes"/> UTF-8 bytes each.</summary>
/// <param name="Initial">The texts in each new world.</param>
/// <param name="MaxItems">The most texts the set may hold (512 unless given).</param>
/// <param name="MaxItemBytes">The longest text an item may be, in UTF-8 bytes (128 unless given).</param>
public sealed record UiStringSetState(IReadOnlyList<string> Initial, int MaxItems = 512, int MaxItemBytes = 128) : UiStateDefinition;

// ---- state values -------------------------------------------------------------------------------------------------

/// <summary>A state value, of one of the types a <see cref="UiStateDefinition"/> declares.</summary>
public abstract record UiStateValue
{
    private UiStateValue() { }

    /// <summary>A true/false.</summary>
    /// <param name="Value">The value.</param>
    public sealed record Bool(bool Value) : UiStateValue;
    /// <summary>A whole number.</summary>
    /// <param name="Value">The number.</param>
    public sealed record Int64(long Value) : UiStateValue;
    /// <summary>A decimal.</summary>
    /// <param name="Value">The number, exact.</param>
    public sealed record Decimal(decimal Value) : UiStateValue;
    /// <summary>A text.</summary>
    /// <param name="Value">The text.</param>
    public sealed record String(string Value) : UiStateValue;

    /// <summary>A set of texts; stored unique and in ordinal order.</summary>
    /// <param name="Value">The texts, unique and in ordinal order.</param>
    public sealed record StringSet(IReadOnlyList<string> Value) : UiStateValue
    {
        /// <inheritdoc/>
        public bool Equals(StringSet? other) => other is not null && Value.SequenceEqual(other.Value, StringComparer.Ordinal);

        /// <inheritdoc/>
        public override int GetHashCode() => Value.Aggregate(17, (h, s) => HashCode.Combine(h, StringComparer.Ordinal.GetHashCode(s)));
    }
}

/// <summary>Which state a write expects to change: the world session's token and the window's revision. A reset (a new
/// world) gives a new session, so revision 0 of one world is never taken for revision 0 of another.</summary>
/// <param name="Session">The world session's token: a new one each time the state is reset for a new world.</param>
/// <param name="Revision">The window's revision in that session: it goes up with each committed change.</param>
public readonly record struct UiStateVersion(ulong Session, ulong Revision);

/// <summary>What an unbound tab bar or tree of a window holds: the selected tab or node, the expanded branches.</summary>
/// <param name="Selected">The selected tab's id or node's key; null when nothing is selected.</param>
/// <param name="Expanded">A tree's open branches, by node key; empty when none is open.</param>
public sealed record UiControlState(string? Selected, IReadOnlyList<string> Expanded);

/// <summary>A window's whole state at one version: the declared entries and the tab bars' and trees' own state.</summary>
/// <param name="Window">The window.</param>
/// <param name="Version">The version this state is at; pass it to a write that expects it.</param>
/// <param name="Values">The declared state entries by key.</param>
/// <param name="Controls">The state of the window's unbound tab bars and trees, by row id.</param>
public sealed record UiStateSnapshot(WindowRef Window, UiStateVersion Version,
    IReadOnlyDictionary<string, UiStateValue> Values, IReadOnlyDictionary<string, UiControlState> Controls);

/// <summary>What <see cref="BlockUi.State"/> returned.</summary>
/// <param name="Ok">True when the call succeeded and <paramref name="Snapshot"/> is set.</param>
/// <param name="Reason">Why not, starting with one of <see cref="UiStateReasons"/>; null when Ok.</param>
/// <param name="Snapshot">The window's state now; null when not Ok.</param>
public sealed record UiStateReadResult(bool Ok, string? Reason, UiStateSnapshot? Snapshot) : IKilnResult;

/// <summary>What <see cref="BlockUi.SetState"/> returned: the snapshot after the write (a write of the same value is
/// accepted and changes nothing), or why it was refused (<see cref="UiStateReasons"/>).</summary>
/// <param name="Ok">True when the write was accepted and <paramref name="Snapshot"/> is set.</param>
/// <param name="Reason">Why it was refused, starting with one of <see cref="UiStateReasons"/>; null when Ok.</param>
/// <param name="Snapshot">The window's state after the write; null when refused.</param>
public sealed record UiStateResult(bool Ok, string? Reason, UiStateSnapshot? Snapshot) : IKilnResult;

/// <summary>Why a state or data call was refused (<see cref="IKilnResult.Reason"/> starts with one of these).</summary>
public static class UiStateReasons
{
    /// <summary>No such window here (not defined, its mod not loaded, or this is not a game with an overlay).</summary>
    public const string UnknownWindow = "UnknownWindow";
    /// <summary>The window declares no such state entry, or no such data source was defined.</summary>
    public const string UnknownKey = "UnknownKey";
    /// <summary>The window or data source belongs to another mod.</summary>
    public const string AccessDenied = "AccessDenied";
    /// <summary>The expected version is not the current one.</summary>
    public const string StaleVersion = "StaleVersion";
    /// <summary>The world changed (the state was reset) after the write was asked for.</summary>
    public const string StaleSession = "StaleSession";
    /// <summary>A selection names a row, node or tab that the window does not show now.</summary>
    public const string UnknownSelection = "UnknownSelection";
    /// <summary>The data a widget's edit was made on changed before the edit could be written.</summary>
    public const string StaleSource = "StaleSource";
    /// <summary>The write would pass a size limit of the window or the mod.</summary>
    public const string Budget = "Budget";
    /// <summary>A newer publication of the same data source replaced this one before it was committed.</summary>
    public const string Superseded = "Superseded";
    /// <summary>The window or the mod went away before the write ran.</summary>
    public const string Unloaded = "Unloaded";
}

/// <summary>Why a window's state changed.</summary>
public enum UiStateChangeCause
{
    /// <summary>A widget of the window (a field, a toggle, a tab, a tree node, a table row).</summary>
    Widget = 0,
    /// <summary>A button's or key's toggle or set action.</summary>
    Action = 1,
    /// <summary>The mod's code (<see cref="BlockUi.SetState"/>).</summary>
    Api = 2,
    /// <summary>The selected row, node or tab is gone from valid data, so the selection moved or was cleared.</summary>
    SelectionInvalidated = 3,
    /// <summary>Expanded branches that are gone from valid data were removed.</summary>
    Pruned = 4,
}

/// <summary>A window's declared state entry changed (one event per changed entry, all at the new version). On the game's
/// main thread in the client world's next step after the write was committed (at once in a menu, with no client world).</summary>
public sealed class UiWindowStateChangedEvent(WindowRef window, string key, UiStateValue? before, UiStateValue? after,
    UiStateVersion version, UiStateChangeCause cause) : KilnEvent
{
    /// <summary>The window whose state changed.</summary>
    public WindowRef Window { get; } = window;
    /// <summary>The state entry's key, as the window declares it.</summary>
    public string Key { get; } = key;
    /// <summary>The entry's value before the change.</summary>
    public UiStateValue? Before { get; } = before;
    /// <summary>The entry's value after the change.</summary>
    public UiStateValue? After { get; } = after;
    /// <summary>The window's state version after the change.</summary>
    public UiStateVersion Version { get; } = version;
    /// <summary>What made the change.</summary>
    public UiStateChangeCause Cause { get; } = cause;
}

/// <summary>An unbound tab bar or tree of a window changed what it holds (the row's id in <see cref="RowId"/>). On the
/// game's main thread, as <see cref="UiWindowStateChangedEvent"/>.</summary>
public sealed class UiControlStateChangedEvent(WindowRef window, string rowId, UiControlState? before, UiControlState after,
    UiStateVersion version, UiStateChangeCause cause) : KilnEvent
{
    /// <summary>The window the tab bar or tree is in.</summary>
    public WindowRef Window { get; } = window;
    /// <summary>The id of the tab bar's or tree's row.</summary>
    public string RowId { get; } = rowId;
    /// <summary>What it held before the change; null when it held nothing yet.</summary>
    public UiControlState? Before { get; } = before;
    /// <summary>What it holds after the change.</summary>
    public UiControlState After { get; } = after;
    /// <summary>The window's state version after the change.</summary>
    public UiStateVersion Version { get; } = version;
    /// <summary>What made the change.</summary>
    public UiStateChangeCause Cause { get; } = cause;
}

/// <summary>A window's state went back to its initial values with a new session token: a new world. On the game's main
/// thread, as <see cref="UiWindowStateChangedEvent"/>.</summary>
public sealed class UiWindowStateResetEvent(WindowRef window, UiStateVersion oldVersion, UiStateVersion newVersion) : KilnEvent
{
    /// <summary>The window whose state was reset.</summary>
    public WindowRef Window { get; } = window;
    /// <summary>The version before the reset, in the old session.</summary>
    public UiStateVersion OldVersion { get; } = oldVersion;
    /// <summary>The version after the reset, with the new session token.</summary>
    public UiStateVersion NewVersion { get; } = newVersion;
}

// ---- a mod's own data (BlockUi.Bindings) ----------------------------------------------------------------------------

/// <summary>The types a data field or scalar has.</summary>
public enum UiScalarType
{
    /// <summary>True or false.</summary>
    Bool,
    /// <summary>A signed 64-bit whole number.</summary>
    Int64,
    /// <summary>An unsigned 64-bit whole number.</summary>
    UInt64,
    /// <summary>An exact decimal.</summary>
    Decimal,
    /// <summary>A finite double.</summary>
    Float64,
    /// <summary>A text.</summary>
    String,
}

/// <summary>One typed value of a data field or a scalar source. JSON (a tree's fields in a block file): true/false, a
/// number (whole numbers read as int64, others as exact decimals) or a string.</summary>
[JsonConverter(typeof(UiScalarJsonConverter))]
public abstract record UiScalar
{
    private UiScalar() { }

    /// <summary>The type of the value.</summary>
    public abstract UiScalarType Type { get; }

    /// <summary>A true/false.</summary>
    /// <param name="Value">The value.</param>
    public sealed record Bool(bool Value) : UiScalar
    {
        /// <inheritdoc/>
        public override UiScalarType Type => UiScalarType.Bool;
    }
    /// <summary>A signed whole number.</summary>
    /// <param name="Value">The number.</param>
    public sealed record Int64(long Value) : UiScalar
    {
        /// <inheritdoc/>
        public override UiScalarType Type => UiScalarType.Int64;
    }
    /// <summary>An unsigned whole number.</summary>
    /// <param name="Value">The number.</param>
    public sealed record UInt64(ulong Value) : UiScalar
    {
        /// <inheritdoc/>
        public override UiScalarType Type => UiScalarType.UInt64;
    }
    /// <summary>An exact decimal.</summary>
    /// <param name="Value">The number.</param>
    public sealed record Decimal(decimal Value) : UiScalar
    {
        /// <inheritdoc/>
        public override UiScalarType Type => UiScalarType.Decimal;
    }
    /// <summary>A finite double (NaN and infinities are refused).</summary>
    /// <param name="Value">The number.</param>
    public sealed record Float64(double Value) : UiScalar
    {
        /// <inheritdoc/>
        public override UiScalarType Type => UiScalarType.Float64;
    }
    /// <summary>A text.</summary>
    /// <param name="Value">The text.</param>
    public sealed record String(string Value) : UiScalar
    {
        /// <inheritdoc/>
        public override UiScalarType Type => UiScalarType.String;
    }
}

/// <summary>A data field: its id (as <c>row.&lt;id&gt;</c> / <c>node.&lt;id&gt;</c> in templates) and type.</summary>
/// <param name="Id">The field's id: a template reads it as <c>{row.&lt;id&gt;}</c> or <c>{node.&lt;id&gt;}</c>.</param>
/// <param name="Type">The type of its values.</param>
public sealed record UiField(string Id, UiScalarType Type);

/// <summary>What shape a data source has.</summary>
public enum UiDataShape
{
    /// <summary>One value (<see cref="UiDataSnapshot.Scalar"/>).</summary>
    Scalar,
    /// <summary>Rows with keys and field values, for a table (<see cref="UiDataSnapshot.Rows"/>).</summary>
    Rows,
    /// <summary>Nodes with parents, labels and field values, for a tree (<see cref="UiDataSnapshot.Tree"/>).</summary>
    Tree,
}

/// <summary>A data source's shape: a scalar of <see cref="ScalarType"/>, or rows / tree nodes with up to 16
/// <see cref="Fields"/> (each required in every row or node).</summary>
/// <param name="Shape">Whether the source is a scalar, rows or a tree.</param>
/// <param name="ScalarType">The scalar's type, for a <see cref="UiDataShape.Scalar"/> source; null for the others.</param>
/// <param name="Fields">The fields of each row or node, up to 16, for a rows or tree source; null for a scalar.</param>
public sealed record UiDataSchema(UiDataShape Shape, UiScalarType? ScalarType = null, IReadOnlyList<UiField>? Fields = null);

/// <summary>One row of a rows source: a unique key (<c>[A-Za-z0-9._:-]{1,128}</c>) and its field values.</summary>
/// <param name="Key">The row's key, unique in the source: <c>[A-Za-z0-9._:-]{1,128}</c>. A table tells its rows apart by it.</param>
/// <param name="Values">The row's value for each field of the source's schema, by field id.</param>
public sealed record UiDataRow(string Key, IReadOnlyDictionary<string, UiScalar> Values);

/// <summary>One node of a tree source: a key unique in the whole tree, its parent's key (null at a root), its label and
/// field values. Children keep the order they are given in.</summary>
/// <param name="Key">The node's key, unique in the whole tree.</param>
/// <param name="ParentKey">The key of the node it is under; null for a root.</param>
/// <param name="Label">The node's label.</param>
/// <param name="Values">The node's value for each field of the source's schema, by field id.</param>
public sealed record UiTreeDataNode(string Key, string? ParentKey, UiText Label, IReadOnlyDictionary<string, UiScalar> Values);

/// <summary>A publication of a data source (<see cref="BlockUi.Bindings.Publish"/>): copied at once, so the caller may
/// change its own collections once Publish has returned.</summary>
public abstract record UiDataSnapshot
{
    private UiDataSnapshot() { }

    /// <summary>A publication of a scalar source.</summary>
    /// <param name="Value">The value, of the schema's scalar type.</param>
    public sealed record Scalar(UiScalar Value) : UiDataSnapshot;
    /// <summary>A publication of a rows source.</summary>
    /// <param name="Values">The rows, in the order a table shows them.</param>
    public sealed record Rows(IReadOnlyList<UiDataRow> Values) : UiDataSnapshot;
    /// <summary>A publication of a tree source.</summary>
    /// <param name="Values">The nodes; children keep the order they are given in.</param>
    public sealed record Tree(IReadOnlyList<UiTreeDataNode> Values) : UiDataSnapshot;
}

/// <summary>What <see cref="BlockUi.Bindings.Publish"/> returned: the source's version after the publication was
/// committed, or why not (<see cref="UiStateReasons.Superseded"/> when a newer one replaced it first).</summary>
/// <param name="Ok">True when the publication was committed.</param>
/// <param name="Reason">Why not, starting with one of <see cref="UiStateReasons"/>; null when Ok.</param>
/// <param name="Version">The source's version after the publication was committed.</param>
public sealed record UiDataPublishResult(bool Ok, string? Reason, ulong Version) : IKilnResult;

/// <summary>A data source of a mod (<see cref="BlockUi.Bindings.Define"/>). Dispose it to take the source away.</summary>
public sealed class UiDataRegistration : IKilnResult, IDisposable
{
    private Action? _remove;

    internal UiDataRegistration(bool ok, string? reason, string id, Action? remove)
    {
        Ok = ok;
        Reason = reason;
        Id = id;
        _remove = remove;
    }

    /// <inheritdoc/>
    public bool Ok { get; }

    /// <inheritdoc/>
    public string? Reason { get; }

    /// <summary>The source's id: windows bind it as <c>mod.&lt;id&gt;</c>.</summary>
    public string Id { get; }

    /// <summary>Takes the source away; windows bound to it show it as unavailable.</summary>
    public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
}

/// <summary>A data source's publication was committed. On the game's main thread, as <see cref="UiWindowStateChangedEvent"/>.</summary>
public sealed class UiBindingValueChangedEvent(string mod, string id, ulong oldVersion, ulong newVersion) : KilnEvent
{
    /// <summary>The id of the mod that owns the data source.</summary>
    public string Mod { get; } = mod;
    /// <summary>The source's id (windows bind it as <c>mod.&lt;id&gt;</c>).</summary>
    public string Id { get; } = id;
    /// <summary>The source's version before this publication.</summary>
    public ulong OldVersion { get; } = oldVersion;
    /// <summary>The source's version after it.</summary>
    public ulong NewVersion { get; } = newVersion;
}

/// <summary>A declared dependency on another mod's stat for UI blocks (<see cref="Blocks.ImportStat(ModValueKey)"/>): it names the
/// stat's type for the blocks that show it; whether this game may read it is still the stat's own read policy.</summary>
public sealed class UiStatImportRegistration : IKilnResult, IDisposable
{
    private Action? _remove;

    internal UiStatImportRegistration(bool ok, string? reason, ModValueKey key, Action? remove)
    {
        Ok = ok;
        Reason = reason;
        Key = key;
        _remove = remove;
    }

    /// <inheritdoc/>
    public bool Ok { get; }

    /// <inheritdoc/>
    public string? Reason { get; }

    /// <summary>The imported stat.</summary>
    public ModValueKey Key { get; }

    /// <summary>Forgets the import (blocks compiled with it keep it).</summary>
    public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
}

// ---- JSON --------------------------------------------------------------------------------------------------------

/// <summary>A whole number as a canonical decimal string ("12", "-3"): no sign but minus, no leading zeros, no spaces.</summary>
internal sealed class UiInt64StringConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.String) throw new JsonException("an int64 state number is a string of digits, as \"12\" or \"-3\"");
        var s = r.GetString()!;
        if (!UiCanonical.TryInt64(s, out var v)) throw new JsonException($"\"{s}\" is not a canonical whole number (digits, a leading minus, no + or spaces, within int64)");
        return v;
    }

    public override void Write(Utf8JsonWriter w, long v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A decimal as a canonical decimal string ("1.25", "-3"): digits, an optional point and fraction, no exponent.</summary>
internal sealed class UiDecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.String) throw new JsonException("a decimal state number is a string, as \"1.25\"");
        var s = r.GetString()!;
        if (!UiCanonical.TryDecimal(s, out var v)) throw new JsonException($"\"{s}\" is not a canonical decimal (digits, an optional point and fraction, a leading minus, no exponent)");
        return v;
    }

    public override void Write(Utf8JsonWriter w, decimal v, JsonSerializerOptions o) => w.WriteStringValue(UiCanonical.Text(v));
}

internal sealed class UiScalarJsonConverter : JsonConverter<UiScalar>
{
    public override UiScalar Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.True: return new UiScalar.Bool(true);
            case JsonTokenType.False: return new UiScalar.Bool(false);
            case JsonTokenType.String: return new UiScalar.String(r.GetString()!);
            case JsonTokenType.Number:
            {
                var raw = System.Text.Encoding.UTF8.GetString(r.HasValueSequence ? r.ValueSequence.ToArray() : r.ValueSpan.ToArray());
                if (r.TryGetInt64(out var l) && !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) return new UiScalar.Int64(l);
                if (r.TryGetUInt64(out var u) && !raw.Contains('.')) return new UiScalar.UInt64(u);
                if (UiCanonical.TryDecimal(raw, out var d)) return new UiScalar.Decimal(d);
                if (r.TryGetDouble(out var f) && double.IsFinite(f)) return new UiScalar.Float64(f);
                throw new JsonException($"{raw} is not a number a field can hold");
            }
            default: throw new JsonException("a field value is true/false, a number or a string");
        }
    }

    public override void Write(Utf8JsonWriter w, UiScalar v, JsonSerializerOptions o)
    {
        switch (v)
        {
            case UiScalar.Bool b: w.WriteBooleanValue(b.Value); break;
            case UiScalar.Int64 l: w.WriteNumberValue(l.Value); break;
            case UiScalar.UInt64 u: w.WriteNumberValue(u.Value); break;
            case UiScalar.Decimal d: w.WriteRawValue(UiCanonical.Text(d.Value)); break;
            case UiScalar.Float64 f: w.WriteNumberValue(f.Value); break;
            case UiScalar.String s: w.WriteStringValue(s.Value); break;
        }
    }
}

/// <summary>Canonical decimal text: what state numbers look like in JSON and what an exact value prints as by default.</summary>
internal static class UiCanonical
{
    public static bool TryInt64(string s, out long v)
    {
        v = 0;
        if (s.Length == 0 || s.Length > 20) return false;
        var digits = s[0] == '-' ? s.AsSpan(1) : s.AsSpan();
        if (digits.Length == 0 || (digits.Length > 1 && digits[0] == '0') || (s[0] == '-' && digits is "0")) return false;
        foreach (var c in digits) if (c is < '0' or > '9') return false;
        return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
    }

    public static bool TryDecimal(string s, out decimal v)
    {
        v = 0;
        if (s.Length == 0 || s.Length > 40) return false;
        var body = s[0] == '-' ? s.AsSpan(1) : s.AsSpan();
        var dot = body.IndexOf('.');
        var whole = dot < 0 ? body : body[..dot];
        var frac = dot < 0 ? ReadOnlySpan<char>.Empty : body[(dot + 1)..];
        if (whole.Length == 0 || (whole.Length > 1 && whole[0] == '0') || (dot >= 0 && frac.Length == 0)) return false;
        foreach (var c in whole) if (c is < '0' or > '9') return false;
        foreach (var c in frac) if (c is < '0' or > '9') return false;
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>The shortest exact text: no exponent, no trailing zeros after the point.</summary>
    public static string Text(decimal v)
    {
        var s = v.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }

    /// <summary>The number of digits after the point a decimal needs (trailing zeros do not count).</summary>
    public static int Scale(decimal v)
    {
        var s = Text(v);
        var dot = s.IndexOf('.');
        return dot < 0 ? 0 : s.Length - dot - 1;
    }
}
