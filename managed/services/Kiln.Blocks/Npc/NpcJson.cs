// SPDX-License-Identifier: MIT
// Creature blocks in blocks/*.json: the sections, and the shapes JSON writes that C# writes as values (a state's name, a clip's name, a rule's
// conditions and actions in kiln.creatures' data-pack forms).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The options blocks/*.json is read with, for what a creature's compile reads itself.</summary>
internal static class BlockJsonAccess
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new Vec3Converter() },
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal static class NpcJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("creatures", BlockJson.Make<CreatureBlock>), ("npcs", BlockJson.Make<NpcBlock>), ("bodies", BlockJson.Make<BodyBlock>), ("animations", BlockJson.Make<AnimationBlock>),
        ("ais", BlockJson.Make<AIBlock>), ("behaviours", BlockJson.Make<BehaviourBlock>), ("attacks", BlockJson.Make<AttackBlock>), ("rules", BlockJson.Make<RuleBlock>),
    ];
}

// "slam", "Idle", "0x1490bed7", a GUID; also as a key of "states"
internal sealed class AnimStateConverter : JsonConverter<AnimState>
{
    public override AnimState? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        r.TokenType == JsonTokenType.String && r.GetString() is { Length: > 0 } s ? new AnimState(s) : throw new JsonException("a state is a name");

    public override void Write(Utf8JsonWriter w, AnimState v, JsonSerializerOptions o) => w.WriteStringValue(v.ToString());
    public override AnimState ReadAsPropertyName(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void WriteAsPropertyName(Utf8JsonWriter w, AnimState v, JsonSerializerOptions o) => w.WritePropertyName(v.ToString());
}

// "Slam", or { "clip": "Slam", "speed": 1.2 }, or { "blend": { ... } }
internal sealed class ClipConverter : JsonConverter<Clip>
{
    private sealed record Shape
    {
        [JsonPropertyName("clip")] public string? Name { get; init; }
        public BlendSpace? Blend { get; init; }
        public double? Speed { get; init; }
        public bool? Loop { get; init; }
        public AnimState? Then { get; init; }
        public bool? RootMotion { get; init; }
        public double? BlendIn { get; init; }
        public string? Reference { get; init; }
        public IReadOnlyList<string>? Bones { get; init; }
    }

    public override Clip? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new Clip(r.GetString()!);
        var s = JsonSerializer.Deserialize<Shape>(ref r, o) ?? throw new JsonException("a clip is a name or an object");
        if (s.Name == null && s.Blend == null) throw new JsonException("a clip needs \"clip\" (a glTF animation's name) or \"blend\"");
        return new Clip { Name = s.Name, Blend = s.Blend, Speed = s.Speed, Loop = s.Loop, Then = s.Then, RootMotion = s.RootMotion, BlendIn = s.BlendIn, Reference = s.Reference, Bones = s.Bones };
    }

    public override void Write(Utf8JsonWriter w, Clip v, JsonSerializerOptions o) =>
        JsonSerializer.Serialize(w, new Shape { Name = v.Name, Blend = v.Blend, Speed = v.Speed, Loop = v.Loop, Then = v.Then, RootMotion = v.RootMotion, BlendIn = v.BlendIn, Reference = v.Reference, Bones = v.Bones }, o);
}

// A rule's "when": kiln.creatures' data-pack forms ({ "healthBelow": 0.5 }, { "custom": "name" }, the game's { "type": ... }). Names follow from the
// block's mod, which only the compile knows: kept as written until then.
internal sealed class RuleConditionsConverter : JsonConverter<IReadOnlyList<RuleCondition>>
{
    public override IReadOnlyList<RuleCondition>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var list = JsonNode.Parse(ref r) as JsonArray ?? throw new JsonException("\"when\" is a list of conditions");
        return list.Select(x => x is JsonObject j ? new RuleCondition(new JsonObject { [NpcRules.Pending] = j.DeepClone() }) : throw new JsonException("a condition is an object")).ToList();
    }

    public override void Write(Utf8JsonWriter w, IReadOnlyList<RuleCondition> v, JsonSerializerOptions o) => new JsonArray(v.Select(c => (JsonNode?)(c.Json[NpcRules.Pending] ?? c.Json).DeepClone()).ToArray()).WriteTo(w);
}

// A rule's "do": { "setPhase": "x" }, { "attack": "slam" }, { "custom": "name" }, { "effect": "burst", "on": "self" }, or the game's { "type": ... }.
internal sealed class RuleActionsConverter : JsonConverter<IReadOnlyList<RuleAction>>
{
    public override IReadOnlyList<RuleAction>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var list = JsonNode.Parse(ref r) as JsonArray ?? throw new JsonException("\"do\" is a list of actions");
        return list.Select(x => x is JsonObject j ? new RuleAction(new JsonObject { [NpcRules.Pending] = j.DeepClone() }) : throw new JsonException("an action is an object")).ToList();
    }

    public override void Write(Utf8JsonWriter w, IReadOnlyList<RuleAction> v, JsonSerializerOptions o) => new JsonArray(v.Select(c => (JsonNode?)(c.Json[NpcRules.Pending] ?? c.Json).DeepClone()).ToArray()).WriteTo(w);
}

/// <summary>Conditions and actions written in a data file, made what kiln.creatures takes once the mod is known.</summary>
internal static class NpcRules
{
    public const string Pending = "blocksJson";

    public static RuleCondition Condition(RuleCondition c, string mod, BlockContext ctx, string path)
    {
        if (c.Json[Pending] is not JsonObject j) return c;
        if (AiJson.Condition(j, mod, ctx, path) is { } ai) return ai;   // npcai's forms (Ai/AiJson.cs)
        try
        {
            using var doc = JsonDocument.Parse(j.ToJsonString());
            return RuleJson.Condition(doc.RootElement, mod);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            throw new BlockError(ctx.Block, path, e.Message);
        }
    }

    /// <summary>An action, with an effect's place-holder as it is (the compile runs effects).</summary>
    public static RuleAction Action(RuleAction a, string mod, BlockContext ctx, string path)
    {
        if (a.Json[Pending] is not JsonObject j) return a;
        if (j.ContainsKey("effect"))
        {
            EffectRef effect;
            try { effect = j["effect"].Deserialize<EffectRef>(BlockJsonAccess.Options)!; }
            catch (JsonException e) { throw new BlockError(ctx.Block, $"{path}.effect", e.Message); }
            var on = j["on"]?.GetValue<string>() switch
            {
                null or "target" => NpcEffectOn.Target,
                "self" => NpcEffectOn.Self,
                var x => throw new BlockError(ctx.Block, $"{path}.on", $"\"{x}\": an effect is on the creature's \"target\" or on itself, \"self\""),
            };
            return NpcRuntime.EffectAction(effect, on);
        }
        if (AiJson.Action(j, mod, ctx, path) is { } ai) return ai;   // npcai's forms (Ai/AiJson.cs)
        try
        {
            using var doc = JsonDocument.Parse(j.ToJsonString());
            return RuleJson.Action(doc.RootElement, mod);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            throw new BlockError(ctx.Block, path, e.Message);
        }
    }
}
