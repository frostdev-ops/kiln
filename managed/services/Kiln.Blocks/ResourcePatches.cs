// SPDX-License-Identifier: MIT
// Resource patches (docs/kiln/modding/resource-patches.md): fields of a game resource the game reads once at its start
// (the balancing table), set before it reads them. Staged in Mod.OnBoot like a block, or written in blocks/*.json
// ("resourcePatches"); kiln.content checks them in the boot generation's prepare and writes them when the game's
// resource service is up. Not compiled by Kiln.Blocks: the record is the document kiln.content reads.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>Fields of one game resource, set before the game reads it. Stage it in <see cref="Mod.OnBoot"/>:
/// <c>BootDefinitions.For(boot).Define("more-altars", new ResourcePatchBlock { ... })</c>. Numbers, bools and enums
/// only. Two mods that set one field are not combined: the later mod's boot content is refused.</summary>
/// <example>
/// <code>
/// new ResourcePatchBlock
/// {
///     Resource = Guid.Parse("82706b40-61b1-4b8f-8b23-dcec6971bda1"), Type = "keen::BalancingTable",
///     Set = { ["altarsPerFlameLevel[*]"] = ResourceValue.AtLeast(16), ["experienceNeedPerLevel[0]"] = 1500 },
/// }
/// </code>
/// </example>
public sealed record ResourcePatchBlock : IBlock
{
    /// <summary>The resource's GUID.</summary>
    public Guid Resource { get; init; }

    /// <summary>The resource's qualified type name, as <c>keen::BalancingTable</c>.</summary>
    public string Type { get; init; } = "";

    /// <summary>The part index; 0 for nearly every type.</summary>
    public int Part { get; init; }

    /// <summary>Member path to value. A path: members by name (<c>a.b</c>), <c>[i]</c> an element of a list that exists,
    /// <c>[*]</c> every element.</summary>
    public Dictionary<string, ResourceValue> Set { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>What a resource patch writes into one field: a plain value, or a change of the game's own value
/// (times <see cref="Mul"/>, plus <see cref="Add"/>, then held between <see cref="Min"/> and <see cref="Max"/>). A bool
/// or an enum member takes a plain value only. In JSON a plain value is written as it is, a change as
/// <c>{ "mul": 2, "min": 16 }</c>.</summary>
[JsonConverter(typeof(ResourceValueJson))]
public readonly record struct ResourceValue
{
    /// <summary>The plain value: a number, a bool, or an enum member's name. Null: a change of the game's value.</summary>
    public object? Value { get; init; }

    /// <summary>The game's value is multiplied by this first.</summary>
    public double? Mul { get; init; }

    /// <summary>Then this is added.</summary>
    public double? Add { get; init; }

    /// <summary>The result is at least this.</summary>
    public double? Min { get; init; }

    /// <summary>The result is at most this.</summary>
    public double? Max { get; init; }

    /// <summary>The result is never below the game's value.</summary>
    public bool RaiseOnly { get; init; }

    /// <summary>The result is never above the game's value.</summary>
    public bool LowerOnly { get; init; }

    /// <summary>The member is set to <paramref name="value"/>.</summary>
    public static implicit operator ResourceValue(double value) => new() { Value = value };
    /// <inheritdoc cref="op_Implicit(double)"/>
    public static implicit operator ResourceValue(long value) => new() { Value = value };
    /// <inheritdoc cref="op_Implicit(double)"/>
    public static implicit operator ResourceValue(int value) => new() { Value = (long)value };
    /// <inheritdoc cref="op_Implicit(double)"/>
    public static implicit operator ResourceValue(bool value) => new() { Value = value };
    /// <summary>The member, an enum, is set to the member named <paramref name="name"/>.</summary>
    public static implicit operator ResourceValue(string name) => new() { Value = name ?? throw new ArgumentNullException(nameof(name)) };

    /// <summary>The game's value, but at least <paramref name="min"/>.</summary>
    public static ResourceValue AtLeast(double min) => new() { Min = min };
    /// <summary>The game's value, but at most <paramref name="max"/>.</summary>
    public static ResourceValue AtMost(double max) => new() { Max = max };
    /// <summary>The game's value times <paramref name="factor"/>.</summary>
    public static ResourceValue Times(double factor) => new() { Mul = factor };
    /// <summary>The game's value plus <paramref name="amount"/>.</summary>
    public static ResourceValue Plus(double amount) => new() { Add = amount };

    internal bool IsChange => Mul != null || Add != null || Min != null || Max != null || RaiseOnly || LowerOnly;
}

internal sealed class ResourceValueJson : JsonConverter<ResourceValue>
{
    public override ResourceValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True: return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.String: return reader.GetString()!;
            case JsonTokenType.Number: return reader.TryGetInt64(out var i) ? i : reader.GetDouble();
            case JsonTokenType.StartObject:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                double? Num(string name) => doc.RootElement.TryGetProperty(name, out var p)
                    ? p.ValueKind == JsonValueKind.Number ? p.GetDouble() : throw new JsonException($"\"{name}\" must be a number")
                    : null;
                bool Flag(string name) => doc.RootElement.TryGetProperty(name, out var p) &&
                    (p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : throw new JsonException($"\"{name}\" must be true or false"));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name is not ("mul" or "add" or "min" or "max" or "raiseOnly" or "lowerOnly"))
                        throw new JsonException($"unknown operation \"{p.Name}\" (mul, add, min, max, raiseOnly, lowerOnly)");
                var v = new ResourceValue { Mul = Num("mul"), Add = Num("add"), Min = Num("min"), Max = Num("max"), RaiseOnly = Flag("raiseOnly"), LowerOnly = Flag("lowerOnly") };
                return v.IsChange ? v : throw new JsonException("an empty operation object");
            }
            default: throw new JsonException("a resource patch value is a number, a bool, an enum member's name, or { mul, add, min, max, raiseOnly, lowerOnly }");
        }
    }

    public override void Write(Utf8JsonWriter writer, ResourceValue value, JsonSerializerOptions options)
    {
        if (!value.IsChange)
        {
            switch (value.Value)
            {
                case bool b: writer.WriteBooleanValue(b); break;
                case long l: writer.WriteNumberValue(l); break;
                case double d when double.IsFinite(d): writer.WriteNumberValue(d); break;
                case string s: writer.WriteStringValue(s); break;
                default: throw new JsonException($"a resource patch value is a finite number, a bool or an enum member's name, not {Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "nothing"}");
            }
            return;
        }
        if (value.Value != null) throw new JsonException("a resource patch value is either a plain value or a change of the game's value, not both");
        writer.WriteStartObject();
        void Num(string name, double? v)
        {
            if (v is not { } d) return;
            if (!double.IsFinite(d)) throw new JsonException($"\"{name}\" must be a finite number");
            writer.WriteNumber(name, d);
        }
        Num("mul", value.Mul);
        Num("add", value.Add);
        Num("min", value.Min);
        Num("max", value.Max);
        if (value.RaiseOnly) writer.WriteBoolean("raiseOnly", true);
        if (value.LowerOnly) writer.WriteBoolean("lowerOnly", true);
        writer.WriteEndObject();
    }
}
