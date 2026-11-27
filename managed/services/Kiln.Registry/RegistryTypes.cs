// SPDX-License-Identifier: MIT
// The hand-written part of Kiln.Registry: ids, the field attribute, the field catalog's record. The entry types and the
// catalog's rows are generated (Generated/*.g.cs, tools/registrygen/registrygen.py).
using System;
using System.Collections.Generic;

namespace Kiln.Registry;

/// <summary>A registry entry's Kiln id: <c>game:&lt;stable name&gt;</c> for the game's entries, <c>&lt;mod id&gt;:&lt;name&gt;</c> for a
/// mod's. Lower case; a game entry keeps its id across game updates (Kiln's alias table follows renames).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public readonly record struct RegistryId
{
    /// <summary>The id as text (<c>game:material_t1_wood_logs</c>).</summary>
    public string Value { get; }

    /// <summary>Makes an id from its text. ArgumentException when it is not &lt;namespace&gt;:&lt;name&gt; in lower case.</summary>
    public RegistryId(string value)
    {
        if (!IsValid(value))
            throw new ArgumentException($"\"{value}\" is not a registry id (<mod id or game>:<name>, lower case)", nameof(value));
        Value = value;
    }

    /// <summary>A game entry's id from its stable name (<c>Game("material_t1_wood_logs")</c>).</summary>
    public static RegistryId Game(string name) => new("game:" + name);

    /// <summary>Whether the id names a game entry.</summary>
    public bool IsGame => Value is not null && Value.StartsWith("game:", StringComparison.Ordinal);

    /// <summary>The part before the colon: "game" or the mod's id.</summary>
    public string Mod => Value is null ? "" : Value[..Value.IndexOf(':')];

    /// <summary>The part after the colon.</summary>
    public string Name => Value is null ? "" : Value[(Value.IndexOf(':') + 1)..];

    /// <summary>Parses an id; false when the text is not one.</summary>
    public static bool TryParse(string? text, out RegistryId id)
    {
        id = default;
        if (!IsValid(text))
            return false;
        id = new RegistryId(text!);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value ?? "";

    // The one id grammar (tools/registrygen KILN_ID, native regmap.cpp is_kiln_id, the data section's schema):
    // <namespace>:<name>; namespace [a-z0-9][a-z0-9_.-]*, name [a-z0-9_.-]+, then optionally a clash suffix ~<8 to 32 hex>.
    private static readonly System.Text.RegularExpressions.Regex s_grammar =
        new("^[a-z0-9][a-z0-9_.-]*:[a-z0-9_.-]+(~[0-9a-f]{8,32})?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsValid(string? s) => !string.IsNullOrEmpty(s) && s.Length <= 256 && s_grammar.IsMatch(s);
}

/// <summary>An entry of a registry (ItemEntry, RecipeEntry, ...).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public interface IRegistryEntry
{
    /// <summary>The entry's Kiln id.</summary>
    RegistryId Id { get; }

    /// <summary>The registry's name (items, recipes, ...).</summary>
    string Registry { get; }
}

/// <summary>Marks a generated property with its qualified Kiln field and when an edit of it takes effect.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RegistryFieldAttribute : Attribute
{
    /// <summary>The attribute of one generated property.</summary>
    public RegistryFieldAttribute(string field, RegistryApplies applies)
    {
        Field = field;
        Applies = applies;
    }

    /// <summary>The qualified field (<c>items.maxStack</c>, <c>recipes.inputs[].count</c>).</summary>
    public string Field { get; }

    /// <summary>When an edit of the field takes effect.</summary>
    public RegistryApplies Applies { get; }

    /// <summary>Multiplayer visibility: now, worldLoad, rejoin, restart, serverOnly, localOnly, or unknown. Separate from local Applies.</summary>
    public string JoinedClient { get; set; } = "unknown";
}

/// <summary>The type of a registry field.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public enum RegistryFieldType
{
    /// <summary>A whole number.</summary>
    Int,
    /// <summary>A number.</summary>
    Float,
    /// <summary>True or false.</summary>
    Bool,
    /// <summary>One Kiln name of a fixed set.</summary>
    Enum,
    /// <summary>Any of a fixed set of Kiln names.</summary>
    Flags,
    /// <summary>An entry of another registry, by its id.</summary>
    Ref,
    /// <summary>A duration (seconds in data files).</summary>
    Time,
    /// <summary>A list.</summary>
    List,
    /// <summary>A list's element with fields of its own.</summary>
    Struct,
}

/// <summary>What edits a field takes.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public enum RegistryOps
{
    /// <summary>None: read-only.</summary>
    None,
    /// <summary>A value.</summary>
    Set,
    /// <summary>A value, or relative operations (mul, add, min, max, raiseOnly, lowerOnly) that stack across mods.</summary>
    Number,
    /// <summary>List operations: clear, append, remove, replace.</summary>
    List,
    /// <summary>A list of fixed length: one element at a time (<c>xpPerLevel[2]</c>) with a value or a number operation.</summary>
    Indexed,
}

/// <summary>One field of a registry schema (Kiln names only): what <see cref="RegistrySchema"/> lists.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public sealed class RegistryFieldSpec
{
    internal RegistryFieldSpec(int id, string field, RegistryFieldType type, RegistryFieldType? element, RegistryApplies applies,
        RegistryOps ops, double? min, double? max, int maxItems, string[]? values, string? reference, string? unit, string? formula,
        bool readOnly, bool optional, int fixedLength, string? lengthField, int lengthOffset, string since)
    {
        Optional = optional;
        FixedLength = fixedLength;
        LengthField = lengthField;
        LengthOffset = lengthOffset;
        Id = id;
        Field = field;
        Type = type;
        Element = element;
        Applies = applies;
        Ops = ops;
        Min = min;
        Max = max;
        MaxItems = maxItems;
        Values = values ?? Array.Empty<string>();
        Reference = reference;
        Unit = unit;
        Formula = formula;
        ReadOnly = readOnly;
        Since = since;
    }

    /// <summary>The field's permanent id ((registry id &lt;&lt; 16) | field id; never reused).</summary>
    public int Id { get; }
    /// <summary>The qualified field (<c>items.damage.fire</c>).</summary>
    public string Field { get; }
    /// <summary>The registry (the part before the first dot).</summary>
    public string Registry => Field[..Field.IndexOf('.')];
    /// <summary>The field's path in its registry (<c>damage.fire</c>).</summary>
    public string Path => Field[(Field.IndexOf('.') + 1)..];
    /// <summary>The field's type.</summary>
    public RegistryFieldType Type { get; }
    /// <summary>A list's element type (null for other fields).</summary>
    public RegistryFieldType? Element { get; }
    /// <summary>When an edit takes effect.</summary>
    public RegistryApplies Applies { get; }
    /// <summary>When a joined Kiln client consumes a server edit: now, worldLoad, rejoin, restart, serverOnly, localOnly, or unknown. See the registry guide.</summary>
    public string JoinedClient { get; internal init; } = "unknown";
    /// <summary>What edits the field takes.</summary>
    public RegistryOps Ops { get; }
    /// <summary>The lowest value (a list of numbers: of its elements).</summary>
    public double? Min { get; }
    /// <summary>The highest value (a list of numbers: of its elements).</summary>
    public double? Max { get; }
    /// <summary>A list: at most this many elements (0: no limit).</summary>
    public int MaxItems { get; }
    /// <summary>An enum's or flags' Kiln names.</summary>
    public IReadOnlyList<string> Values { get; }
    /// <summary>A ref: the registry whose ids it holds.</summary>
    public string? Reference { get; }
    /// <summary>The unit (s, m), if any.</summary>
    public string? Unit { get; }
    /// <summary>The Kiln formula between the field and the game's numbers (gear.power), if any.</summary>
    public string? Formula { get; }
    /// <summary>Read-only: no edit takes it.</summary>
    public bool ReadOnly { get; }
    /// <summary>The schema version that published it.</summary>
    public string Since { get; }
    /// <summary>A ref that may be absent (null clears it).</summary>
    public bool Optional { get; }
    /// <summary>A list of exactly this many elements (0: not fixed, or see <see cref="LengthField"/>).</summary>
    public int FixedLength { get; }
    /// <summary>A list as long as this field's value plus <see cref="LengthOffset"/> (null: none).</summary>
    public string? LengthField { get; }
    /// <summary>See <see cref="LengthField"/>.</summary>
    public int LengthOffset { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Field} ({Type}, {Applies})";
}

/// <summary>Every registry field Kiln publishes, from schema/registry/*.json.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/README.md")]
public static partial class RegistrySchema
{
    /// <summary>Every field, registry by registry.</summary>
    public static IReadOnlyList<RegistryFieldSpec> Fields => s_fields;

    /// <summary>The registries' names and schema versions.</summary>
    public static IReadOnlyDictionary<string, string> Versions => s_versions;

    /// <summary>How many edits of each registry one call takes (the schema's editsPerCall, 4096 where it declares none): one
    /// OnRegistry callback (one phase of one entry point) or one Registry.Change. A call with more edits of a registry is
    /// refused whole, with the reason in kiln.log and registry.json; the mod's other calls still apply.</summary>
    public static IReadOnlyDictionary<string, int> EditsPerCall => s_editsPerCall;

    /// <summary>A field by its qualified name (<c>items.maxStack</c>); an index in a list path counts as [] (<c>recipes.inputs[2].count</c>).
    /// Null when there is none.</summary>
    public static RegistryFieldSpec? Find(string qualifiedField)
    {
        ArgumentNullException.ThrowIfNull(qualifiedField);
        string key = Normalise(qualifiedField);
        foreach (var f in s_fields)
            if (string.Equals(f.Field, key, StringComparison.Ordinal))
                return f;
        return null;
    }

    internal static string Normalise(string field)
    {
        var sb = new System.Text.StringBuilder(field.Length);
        for (int i = 0; i < field.Length; i++)
        {
            char c = field[i];
            if (c == '[')
            {
                int end = field.IndexOf(']', i);
                if (end < 0)
                    return field;
                sb.Append("[]");
                i = end;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
