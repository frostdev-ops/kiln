// SPDX-License-Identifier: MIT
// MaterialBlock: a surface for models, written from nothing, in C# and (the same shape) in the "materials" section of blocks/*.json. kiln.vfx builds it into the game's
// RenderMaterialResource and its textures. Every field stays null until it is said, so that what reaches the builder is what was written and the defaults are the builder's alone.
// The shape: schema/kiln-vfx.schema.json, docs/kiln/modding/materials.md.
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>
/// A surface on the game's own shaders. Name it in a model step of <c>assets/cook.json</c>, or in <see cref="ParticleBlock.Material"/>:
/// <code>
/// Blocks.Define("frost_steel", new MaterialBlock
/// {
///     Kind = MaterialKind.Metal,
///     Albedo = "textures/steel.png",
///     Normal = "textures/steel_n.png",
///     Roughness = 0.3f,
///     Tint = "#bfe0ff",
///     Emissive = new EmissiveBlock { Map = "textures/runes.png", Color = "#7fd4ff", Strength = 8 },
/// });
/// </code>
/// Everything has a default: a material with no maps is a flat colour. A mod with a new material needs <c>clients: "mod"</c>: the textures have to be on every player's game.
/// The game lists every material's textures once at start, so one defined from code that it hasn't seen is there from the next start. One in a data file is there at once.
/// </summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record MaterialBlock : IBlock
{
    /// <summary>What it is, and where every other field starts from. Default <see cref="MaterialKind.Opaque"/>.</summary>
    public MaterialKind? Kind { get; init; }
    /// <summary>Colour: a PNG in your mod's folder, sRGB, a power of two on both sides, 4 to 4096. Its alpha is what a cutout cuts by.</summary>
    public string? Albedo { get; init; }
    /// <summary><c>#rrggbb</c>, sRGB: multiplies the albedo. Without one it is the colour.</summary>
    public string? Tint { get; init; }
    /// <summary>A tangent-space normal map, green up as in glTF and Blender.</summary>
    public NormalMap? Normal { get; init; }
    /// <summary>A PNG in the game's own layout, linear: R metallic, G roughness, B occlusion, and A reflectance if <see cref="Reflectance"/> says so.</summary>
    public string? Params { get; init; }
    /// <summary>A PNG in glTF's layout, linear: G roughness, B metallic.</summary>
    public string? MetallicRoughness { get; init; }
    /// <summary>A PNG, linear, in R.</summary>
    public string? Occlusion { get; init; }
    /// <summary>0 to 1: the value without a map, a factor with one. Default by kind.</summary>
    public float? Roughness { get; init; }
    /// <summary>0 to 1: the value without a map, a factor with one. Default by kind.</summary>
    public float? Metallic { get; init; }
    /// <summary>Non-metal specular, 0 to 1, or <see cref="MaterialReflectance.FromParams"/>. Default 0.503, what nearly all of the game's materials use.</summary>
    public MaterialReflectance? Reflectance { get; init; }
    /// <summary>What glows: a PNG, <c>#rrggbb</c>, or an <see cref="EmissiveBlock"/>.</summary>
    public EmissiveBlock? Emissive { get; init; }
    /// <summary>Cut where the albedo's alpha is under this: true is 0.5.</summary>
    public MaterialAlphaTest? AlphaTest { get; init; }
    /// <summary>Draw both faces, with the normal turned on the back.</summary>
    public bool? DoubleSided { get; init; }
    /// <summary>Casts a shadow. Default true.</summary>
    public bool? Shadows { get; init; }
    /// <summary>Default true.</summary>
    public bool? ContactShadows { get; init; }
    /// <summary>Takes frost in the cold. Default true.</summary>
    public bool? Frost { get; init; }
    /// <summary>Takes snow. Default true.</summary>
    public bool? Snow { get; init; }
    /// <summary>Darkens in the rain. Default true.</summary>
    public bool? Wetness { get; init; }
    /// <summary>Multiply the albedo by the model's vertex colour.</summary>
    public bool? VertexColor { get; init; }
    /// <summary>0 to 1: how much light comes through from behind.</summary>
    public float? Translucent { get; init; }
    /// <summary>The game can fade it in and out with an edge glow, as it does to creatures and placed objects: true, or a <see cref="DissolveBlock"/>.</summary>
    public DissolveBlock? Dissolve { get; init; }
    /// <summary>Repeats of every map: a number, or <c>(u, v)</c>. Default 1.</summary>
    public MaterialUvScale? UvScale { get; init; }
    /// <summary>Reserved for surface shaders, which aren't built yet. Naming one is an error: a material must not silently draw without the shader it asks for.</summary>
    public string? Shader { get; init; }
    /// <summary>The name this block had before a rename. Ids come from the oldest name.</summary>
    public string? Was { get; init; }
    /// <summary>The GUID of one of the game's materials: this block draws in its place on every model that uses it, items, props and creatures alike.
    /// Keep the original's <see cref="Kind"/>: the models carry its cutout and double-sided flags, and a mismatch draws wrong (never fatally). The block's id is that GUID.
    /// The replacement belongs to this mod: unloading the mod takes it off (the game draws its own again from the next start), and if two mods replace the same
    /// material the first in the load order draws (docs/kiln/modding/materials.md).</summary>
    public string? Replaces { get; init; }

    /// <inheritdoc cref="VfxBlock.Comment"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>What a material is. Each maps to the game's own shader variants, by the flags it sets.</summary>
public enum MaterialKind
{
    /// <summary>Solid. Roughness 0.7, not metal.</summary>
    Opaque,
    /// <summary>Solid metal. Metallic 1, roughness 0.35.</summary>
    Metal,
    /// <summary>Holes where the albedo's alpha is under 0.5.</summary>
    Cutout,
    /// <summary>Leaves: a cutout, double sided, and light comes through.</summary>
    Foliage,
    /// <summary>Thin and rough: double sided.</summary>
    Cloth,
    /// <summary>Glows, strength 4: with its own <see cref="MaterialBlock.Emissive"/>, or else with its surface.</summary>
    Emissive,
    /// <summary>Soft edges from a dither pattern. The nearest thing to transparency on the game's shaders.</summary>
    Dithered,
}

/// <summary>A normal map: a PNG (<c>"n.png"</c>), or with a <see cref="Strength"/>.</summary>
[JsonConverter(typeof(NormalMapConverter))]
public sealed record NormalMap(string Map, float? Strength = null, bool? YDown = null)
{
    public static implicit operator NormalMap(string map) => new(map);
}

/// <summary>What glows. <c>"runes.png"</c> and <c>"#7fd4ff"</c> are the short forms.</summary>
[JsonConverter(typeof(EmissiveConverter))]
public sealed record EmissiveBlock
{
    /// <summary>A PNG, sRGB.</summary>
    public string? Map { get; init; }
    /// <summary><c>#rrggbb</c>, sRGB: multiplies the map. Without one it is the colour.</summary>
    public string? Color { get; init; }
    /// <summary>Default 1, 4 for <see cref="MaterialKind.Emissive"/>.</summary>
    public float? Strength { get; init; }
    /// <summary>0 to 1: how much it follows the camera's exposure correction. Default 0.75.</summary>
    public float? Exposure { get; init; }

    public static implicit operator EmissiveBlock(string mapOrColour) => mapOrColour.StartsWith('#') ? new() { Color = mapOrColour } : new() { Map = mapOrColour };
}

/// <summary>How a material dissolves. <c>true</c> is the short form.</summary>
[JsonConverter(typeof(DissolveConverter))]
public sealed record DissolveBlock
{
    /// <summary>The edge's glow, <c>#rrggbb</c>. Default white.</summary>
    public string? Color { get; init; }
    /// <summary>The edge's width. Default 0.05.</summary>
    public float? Width { get; init; }
    /// <summary>Default 0.5.</summary>
    public float? NoiseTiling { get; init; }
    /// <summary>0 to 1: noise against distance from the centre. Default 0.3.</summary>
    public float? NoiseBlend { get; init; }

    internal bool? Plain { get; init; }

    public static implicit operator DissolveBlock(bool on) => new() { Plain = on };
}

/// <summary>A number, or <see cref="FromParams"/>.</summary>
[JsonConverter(typeof(ReflectanceConverter))]
public readonly record struct MaterialReflectance
{
    internal float? Value { get; private init; }

    /// <summary>Read it from the alpha of <see cref="MaterialBlock.Params"/>.</summary>
    public static readonly MaterialReflectance FromParams = default;
    public static implicit operator MaterialReflectance(float value) => new() { Value = value };
}

/// <summary><c>true</c>, <c>false</c>, or the alpha to cut at.</summary>
[JsonConverter(typeof(AlphaTestConverter))]
public readonly record struct MaterialAlphaTest
{
    internal float? Cutoff { get; private init; }
    internal bool On { get; private init; }

    public static implicit operator MaterialAlphaTest(bool on) => new() { On = on };
    public static implicit operator MaterialAlphaTest(float cutoff) => new() { Cutoff = cutoff, On = true };
}

/// <summary>A number, or <c>(u, v)</c>.</summary>
[JsonConverter(typeof(UvScaleConverter))]
public readonly record struct MaterialUvScale(float U, float V)
{
    public static implicit operator MaterialUvScale(float value) => new(value, value);
    public static implicit operator MaterialUvScale((float U, float V) scale) => new(scale.U, scale.V);
}

/// <summary>A material of this mod: its name, or the handle from <see cref="Blocks.Define(string, MaterialBlock)"/>.</summary>
[JsonConverter(typeof(MaterialRefConverter))]
public readonly record struct MaterialRef(string Name)
{
    public static implicit operator MaterialRef(string name) => new(name);
    public static implicit operator MaterialRef(BlockHandle<MaterialBlock> handle) => new(handle.Name);
}

/// <summary>What <see cref="Blocks.Define(string, MaterialBlock)"/> gives back. Its <see cref="BlockHandle.Name"/> goes in a recipe's <c>material</c>; the GUID is what a model holds.</summary>
public sealed class MaterialBlockHandle : BlockHandle<MaterialBlock>
{
    internal MaterialBlockHandle(string mod, string name, Guid guid, MaterialBlock block, VfxBlockStatus status) : base(mod, name, guid, $"{mod}.{name}", block) => Status = status;

    /// <summary>Whether this game has it now. The game lists every material's textures once at start.</summary>
    public VfxBlockStatus Status { get; internal set; }
}

// ------------------------------------------------------------------------------------------------- the JSON shapes -----

internal sealed class NormalMapConverter : JsonConverter<NormalMap>
{
    public override NormalMap? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        string? map = null;
        float? strength = null;
        bool? yDown = null;
        foreach (var p in JsonDocument.ParseValue(ref r).RootElement.EnumerateObject())
            switch (p.Name)
            {
                case "map": map = p.Value.GetString(); break;
                case "strength": strength = p.Value.GetSingle(); break;
                case "yDown": yDown = p.Value.GetBoolean(); break;
                case "comment": break;
                default: throw new JsonException($"\"{p.Name}\" is not a field of a normal map; there are map, strength, yDown");
            }
        return new NormalMap(map ?? throw new JsonException("a normal map needs \"map\""), strength, yDown);
    }

    public override void Write(Utf8JsonWriter w, NormalMap v, JsonSerializerOptions o)
    {
        if (v.Strength == null && v.YDown == null)
        {
            w.WriteStringValue(v.Map);
            return;
        }
        w.WriteStartObject();
        w.WriteString("map", v.Map);
        if (v.Strength is { } s) w.WriteNumber("strength", s);
        if (v.YDown is { } y) w.WriteBoolean("yDown", y);
        w.WriteEndObject();
    }
}

internal sealed class EmissiveConverter : JsonConverter<EmissiveBlock>
{
    public override EmissiveBlock? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        var e = new EmissiveBlock();
        foreach (var p in JsonDocument.ParseValue(ref r).RootElement.EnumerateObject())
            e = p.Name switch
            {
                "map" => e with { Map = p.Value.GetString() },
                "color" => e with { Color = p.Value.GetString() },
                "strength" => e with { Strength = p.Value.GetSingle() },
                "exposure" => e with { Exposure = p.Value.GetSingle() },
                "comment" => e,
                _ => throw new JsonException($"\"{p.Name}\" is not a field of emissive; there are map, color, strength, exposure"),
            };
        return e;
    }

    public override void Write(Utf8JsonWriter w, EmissiveBlock v, JsonSerializerOptions o)
    {
        if (v is { Strength: null, Exposure: null } && (v.Map == null) != (v.Color == null))
        {
            w.WriteStringValue(v.Map ?? v.Color);
            return;
        }
        w.WriteStartObject();
        if (v.Map != null) w.WriteString("map", v.Map);
        if (v.Color != null) w.WriteString("color", v.Color);
        if (v.Strength is { } s) w.WriteNumber("strength", s);
        if (v.Exposure is { } x) w.WriteNumber("exposure", x);
        w.WriteEndObject();
    }
}

internal sealed class DissolveConverter : JsonConverter<DissolveBlock>
{
    public override DissolveBlock? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType is JsonTokenType.True or JsonTokenType.False) return r.GetBoolean();
        var d = new DissolveBlock();
        foreach (var p in JsonDocument.ParseValue(ref r).RootElement.EnumerateObject())
            d = p.Name switch
            {
                "color" => d with { Color = p.Value.GetString() },
                "width" => d with { Width = p.Value.GetSingle() },
                "noiseTiling" => d with { NoiseTiling = p.Value.GetSingle() },
                "noiseBlend" => d with { NoiseBlend = p.Value.GetSingle() },
                "comment" => d,
                _ => throw new JsonException($"\"{p.Name}\" is not a field of dissolve; there are color, width, noiseTiling, noiseBlend"),
            };
        return d;
    }

    public override void Write(Utf8JsonWriter w, DissolveBlock v, JsonSerializerOptions o)
    {
        if (v.Plain is { } on)
        {
            w.WriteBooleanValue(on);
            return;
        }
        w.WriteStartObject();
        if (v.Color != null) w.WriteString("color", v.Color);
        if (v.Width is { } x) w.WriteNumber("width", x);
        if (v.NoiseTiling is { } tiling) w.WriteNumber("noiseTiling", tiling);
        if (v.NoiseBlend is { } blend) w.WriteNumber("noiseBlend", blend);
        w.WriteEndObject();
    }
}

internal sealed class ReflectanceConverter : JsonConverter<MaterialReflectance>
{
    public override MaterialReflectance Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return r.GetSingle();
        return r.TokenType == JsonTokenType.String && r.GetString() == "params" ? MaterialReflectance.FromParams : throw new JsonException("takes a number or \"params\"");
    }

    public override void Write(Utf8JsonWriter w, MaterialReflectance v, JsonSerializerOptions o)
    {
        if (v.Value is { } x) w.WriteNumberValue(x);
        else w.WriteStringValue("params");
    }
}

internal sealed class AlphaTestConverter : JsonConverter<MaterialAlphaTest>
{
    public override MaterialAlphaTest Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType is JsonTokenType.True or JsonTokenType.False) return r.GetBoolean();
        return r.TokenType == JsonTokenType.Number ? r.GetSingle() : throw new JsonException("takes true, false or a number");
    }

    public override void Write(Utf8JsonWriter w, MaterialAlphaTest v, JsonSerializerOptions o)
    {
        if (v.Cutoff is { } c) w.WriteNumberValue(c);
        else w.WriteBooleanValue(v.On);
    }
}

internal sealed class UvScaleConverter : JsonConverter<MaterialUvScale>
{
    public override MaterialUvScale Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return r.GetSingle();
        var a = JsonSerializer.Deserialize<float[]>(ref r, o);
        return a is { Length: 2 } ? new MaterialUvScale(a[0], a[1]) : throw new JsonException("takes a number or [u, v]");
    }

    public override void Write(Utf8JsonWriter w, MaterialUvScale v, JsonSerializerOptions o)
    {
        if (v.U == v.V)
        {
            w.WriteNumberValue(v.U);
            return;
        }
        w.WriteStartArray();
        w.WriteNumberValue(v.U); w.WriteNumberValue(v.V);
        w.WriteEndArray();
    }
}

internal sealed class MaterialRefConverter : JsonConverter<MaterialRef>
{
    public override MaterialRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? throw new JsonException("a material's name"));
    public override void Write(Utf8JsonWriter w, MaterialRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Name);
}
