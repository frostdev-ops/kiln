// SPDX-License-Identifier: MIT
// The look of a building material: what a mod author writes, in C# and (the same shape) as "surface" in blocks/*.json. kiln.surfaces cooks it into the
// client's surface tables at boot. Every field stays null until it is said, so what reaches the cooker is what was written and the defaults are the
// cooker's alone. docs/kiln/modding/world-blocks.md, "Surfaces".
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The six layers of a building surface. A block's side, top and bottom faces; its worn edges, and their top; a noise layer that breaks the rest up.</summary>
public enum SurfacePart { Side, Top, Bottom, Edge, EdgeTop, Noise }

/// <summary>How the maps lie on a block. A roof material always maps as a roof.</summary>
public enum SurfaceMapping { Default, Wood }

/// <summary>
/// How a building material looks: walls, floors, ceilings and stairs of it.
/// <code>
/// Surface = new SurfaceBlock
/// {
///     Side = new SurfaceLayer { Albedo = "textures/moss-brick.png", Normal = "textures/moss-brick_n.png", Roughness = "textures/moss-brick_r.png", Height = "textures/moss-brick_h.png" },
///     Top = new SurfaceLayer { Albedo = "textures/moss-top.png", Tiling = 2 },
///     Edge = SurfaceLayer.Of(GameMaterials.StoneAncientMoonstone),
/// }
/// </code>
/// On a new material, a layer left out repeats <see cref="Side"/> (<see cref="EdgeTop"/> repeats <see cref="Edge"/>). On a retexture
/// (<see cref="MaterialTweakBlock.Surface"/>) a layer left out stays the game's own. Write it inline, or define it once
/// (<c>Blocks.Define("moss", new SurfaceBlock { ... })</c>, or a <c>surfaces</c> section in JSON) and give its handle or name to several materials.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record SurfaceBlock : IBlock
{
    /// <summary>The sides of a block, and where the other layers start from on a new material.</summary>
    public SurfaceLayer? Side { get; init; }
    /// <summary>The top face.</summary>
    public SurfaceLayer? Top { get; init; }
    /// <summary>The bottom face.</summary>
    public SurfaceLayer? Bottom { get; init; }
    /// <summary>The worn edges.</summary>
    public SurfaceLayer? Edge { get; init; }
    /// <summary>The top of the worn edges. Default: <see cref="Edge"/>.</summary>
    public SurfaceLayer? EdgeTop { get; init; }
    /// <summary>A layer the cooker mixes in by noise, to break up repeats.</summary>
    public SurfaceLayer? Noise { get; init; }
    /// <summary>Metres the edges are rounded by, 0 to 1. Default 0.01, as most of the game's.</summary>
    public float? Bevel { get; init; }
    /// <summary>How the edge layer blends in, 0 to 1, as the game keeps it: 1 is a hard edge (most of the game's), 0.5 a softer one.</summary>
    public float? EdgeCurve { get; init; }
    /// <summary>Where the noise layer starts to show and where it covers all, in noise units. Default (0.33, 0.75).</summary>
    [JsonConverter(typeof(SurfaceRangeConverter))]
    public (float Start, float Full)? NoiseRange { get; init; }
    /// <summary>The edge layer only on the sides of a block. Default true.</summary>
    public bool? EdgeOnlySide { get; init; }
    /// <summary>How the maps lie. Default <see cref="SurfaceMapping.Default"/>.</summary>
    public SurfaceMapping? Mapping { get; init; }
    /// <summary>A game material whose edge decoration (crystals, moss, rivets) it shows: a <c>GameMaterials</c> name, or <c>"none"</c>. Default none; a surface whose
    /// side starts from a game material keeps that material's.</summary>
    public string? Decoration { get; init; }

    /// <inheritdoc cref="VfxBlock.Comment"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>
/// One layer of a surface. PNGs are in your mod's folder, a power of two on both sides, 4 to 4096. Maps can differ in size.
/// A layer can start from a game material's layer, <see cref="Of"/>: what you write replaces that map, the rest stays the game's.
/// In JSON a layer can also be the name of another one: <c>"top": "side"</c>; in C#, <c>Top = SurfacePart.Side</c>.
/// </summary>
[JsonConverter(typeof(SurfaceLayerConverter))]
public sealed record SurfaceLayer
{
    /// <summary>Colour: a PNG, sRGB.</summary>
    public string? Albedo { get; init; }
    /// <summary><c>#rrggbb</c>, sRGB: multiplies the albedo. Without one it is the colour.</summary>
    public string? Tint { get; init; }
    /// <summary>A normal map, green up as in glTF and Blender: <c>"n.png"</c>, or with a strength or <c>YDown</c> for a DirectX map.</summary>
    public NormalMap? Normal { get; init; }
    /// <summary>Perceptual roughness: a PNG (its red channel) or a value 0 to 1. Default 0.8.</summary>
    public SurfaceValue? Roughness { get; init; }
    /// <summary>A PNG (red) or 0 to 1. Default 0.</summary>
    public SurfaceValue? Metallic { get; init; }
    /// <summary>Non-metal specular: a PNG (red) or 0 to 1. Default 0.5.</summary>
    public SurfaceValue? Reflectance { get; init; }
    /// <summary>Height, for displacement and for blending layers: a PNG (red) or 0 to 1. Default 0.5.</summary>
    public SurfaceValue? Height { get; init; }
    /// <summary>Ambient occlusion: a PNG (red) or 0 to 1. Default 1.</summary>
    public SurfaceValue? Ao { get; init; }
    /// <summary>What glows: a PNG, <c>#rrggbb</c>, or a <see cref="SurfaceEmissive"/>.</summary>
    public SurfaceEmissive? Emissive { get; init; }
    /// <summary>How many times the maps repeat every 4 m. Default 1, as most of the game's surfaces.</summary>
    public float? Tiling { get; init; }
    /// <summary>How far the height map pushes the surface, in metres. Default (0.1, -0.05) with a height map, none without.</summary>
    public SurfaceDisplacement? Displacement { get; init; }
    /// <summary>A game material this layer starts from: a <c>GameMaterials</c> name.</summary>
    public string? Game { get; init; }
    /// <summary>Which of its layers. Default: the same as the one this is.</summary>
    public SurfacePart? Layer { get; init; }

    internal SurfacePart? SameAs { get; init; }

    /// <summary>A game material's layer, as it is: <c>SurfaceLayer.Of(GameMaterials.StoneAncientMoonstone, SurfacePart.Edge)</c>. Use <c>with { ... }</c> to change a map of it.</summary>
    public static SurfaceLayer Of(string gameMaterial, SurfacePart? layer = null) => new() { Game = gameMaterial, Layer = layer };

    /// <summary>The same as another layer of this surface.</summary>
    public static implicit operator SurfaceLayer(SurfacePart part) => new() { SameAs = part };
}

/// <summary>A PNG (its red channel) or a value 0 to 1.</summary>
[JsonConverter(typeof(SurfaceValueConverter))]
public readonly record struct SurfaceValue
{
    internal string? File { get; private init; }
    internal float? Value { get; private init; }

    public static implicit operator SurfaceValue(string png) => new() { File = png };
    public static implicit operator SurfaceValue(float value) => new() { Value = value };
}

/// <summary>What glows. <c>"runes.png"</c> and <c>"#7fd4ff"</c> are the short forms.</summary>
[JsonConverter(typeof(SurfaceEmissiveConverter))]
public sealed record SurfaceEmissive
{
    /// <summary>A PNG, sRGB.</summary>
    public string? Map { get; init; }
    /// <summary><c>#rrggbb</c>, sRGB: multiplies the map. Without one it is the colour.</summary>
    public string? Color { get; init; }
    /// <summary>Intensity. Default 1.</summary>
    public float? Strength { get; init; }
    /// <summary>0 to 1: how much it follows the camera's exposure correction. Default 0.75.</summary>
    public float? Exposure { get; init; }
    /// <summary>How much it lights what is around it, through the game's global illumination. Default 4.</summary>
    public float? Gi { get; init; }

    public static implicit operator SurfaceEmissive(string mapOrColour) => mapOrColour.StartsWith('#') ? new() { Color = mapOrColour } : new() { Map = mapOrColour };
}

/// <summary>How far the height map pushes the surface, in metres: the height's 0 to 1 times <see cref="Scale"/>, plus <see cref="Offset"/>.</summary>
public sealed record SurfaceDisplacement
{
    /// <summary>0 to 2.</summary>
    public float? Scale { get; init; }
    /// <summary>-2 to 2.</summary>
    public float? Offset { get; init; }
}

// ------------------------------------------------------------------------------------------------- the JSON shapes -----

internal sealed class SurfaceLayerConverter : JsonConverter<SurfaceLayer>
{
    private static SurfacePart Part(string? s) => s switch
    {
        "side" => SurfacePart.Side, "top" => SurfacePart.Top, "bottom" => SurfacePart.Bottom, "edge" => SurfacePart.Edge, "edgeTop" => SurfacePart.EdgeTop, "noise" => SurfacePart.Noise,
        _ => throw new JsonException($"\"{s}\" is not a layer; there are side, top, bottom, edge, edgeTop, noise"),
    };

    internal static string Name(SurfacePart p) => JsonNamingPolicy.CamelCase.ConvertName(p.ToString());

    public override SurfaceLayer? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new SurfaceLayer { SameAs = Part(r.GetString()) };
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("a layer: {\"albedo\", ...}, a game material's {\"game\", \"layer\"}, or the name of another layer");
        var l = new SurfaceLayer();
        foreach (var p in JsonDocument.ParseValue(ref r).RootElement.EnumerateObject())
            l = p.Name switch
            {
                "albedo" => l with { Albedo = p.Value.GetString() },
                "tint" => l with { Tint = p.Value.GetString() },
                "normal" => l with { Normal = p.Value.Deserialize<NormalMap>(o) },
                "roughness" => l with { Roughness = p.Value.Deserialize<SurfaceValue>(o) },
                "metallic" => l with { Metallic = p.Value.Deserialize<SurfaceValue>(o) },
                "reflectance" => l with { Reflectance = p.Value.Deserialize<SurfaceValue>(o) },
                "height" => l with { Height = p.Value.Deserialize<SurfaceValue>(o) },
                "ao" => l with { Ao = p.Value.Deserialize<SurfaceValue>(o) },
                "emissive" => l with { Emissive = p.Value.Deserialize<SurfaceEmissive>(o) },
                "tiling" => l with { Tiling = p.Value.GetSingle() },
                "displacement" => l with { Displacement = Displacement(p.Value) },
                "game" => l with { Game = p.Value.GetString() },
                "layer" => l with { Layer = Part(p.Value.GetString()) },
                "comment" => l,
                _ => throw new JsonException($"\"{p.Name}\" is not a field of a surface layer; there are albedo, tint, normal, roughness, metallic, reflectance, height, ao, emissive, tiling, displacement, game, layer"),
            };
        return l;
    }

    private static SurfaceDisplacement Displacement(JsonElement e)
    {
        var d = new SurfaceDisplacement();
        foreach (var p in e.EnumerateObject())
            d = p.Name switch
            {
                "scale" => d with { Scale = p.Value.GetSingle() },
                "offset" => d with { Offset = p.Value.GetSingle() },
                _ => throw new JsonException($"\"{p.Name}\" is not a field of a displacement; there are scale, offset"),
            };
        return d;
    }

    public override void Write(Utf8JsonWriter w, SurfaceLayer v, JsonSerializerOptions o)
    {
        if (v.SameAs is { } same)
        {
            w.WriteStringValue(Name(same));
            return;
        }
        w.WriteStartObject();
        if (v.Game != null) w.WriteString("game", v.Game);
        if (v.Layer is { } layer) w.WriteString("layer", Name(layer));
        if (v.Albedo != null) w.WriteString("albedo", v.Albedo);
        if (v.Tint != null) w.WriteString("tint", v.Tint);
        if (v.Normal != null)
        {
            w.WritePropertyName("normal");
            JsonSerializer.Serialize(w, v.Normal, o);
        }
        foreach (var (name, value) in new[] { ("roughness", v.Roughness), ("metallic", v.Metallic), ("reflectance", v.Reflectance), ("height", v.Height), ("ao", v.Ao) })
        {
            if (value is not { } x) continue;
            w.WritePropertyName(name);
            JsonSerializer.Serialize(w, x, o);
        }
        if (v.Emissive != null)
        {
            w.WritePropertyName("emissive");
            JsonSerializer.Serialize(w, v.Emissive, o);
        }
        if (v.Tiling is { } tiling) w.WriteNumber("tiling", tiling);
        if (v.Displacement is { } d)
        {
            w.WriteStartObject("displacement");
            if (d.Scale is { } s) w.WriteNumber("scale", s);
            if (d.Offset is { } off) w.WriteNumber("offset", off);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }
}

internal sealed class SurfaceValueConverter : JsonConverter<SurfaceValue>
{
    public override SurfaceValue Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
    {
        JsonTokenType.Number => r.GetSingle(),
        JsonTokenType.String => r.GetString()!,
        _ => throw new JsonException("takes a PNG or a value 0 to 1"),
    };

    public override void Write(Utf8JsonWriter w, SurfaceValue v, JsonSerializerOptions o)
    {
        if (v.File != null) w.WriteStringValue(v.File);
        else w.WriteNumberValue(v.Value ?? 0);
    }
}

internal sealed class SurfaceEmissiveConverter : JsonConverter<SurfaceEmissive>
{
    public override SurfaceEmissive? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        var e = new SurfaceEmissive();
        foreach (var p in JsonDocument.ParseValue(ref r).RootElement.EnumerateObject())
            e = p.Name switch
            {
                "map" => e with { Map = p.Value.GetString() },
                "color" => e with { Color = p.Value.GetString() },
                "strength" => e with { Strength = p.Value.GetSingle() },
                "exposure" => e with { Exposure = p.Value.GetSingle() },
                "gi" => e with { Gi = p.Value.GetSingle() },
                "comment" => e,
                _ => throw new JsonException($"\"{p.Name}\" is not a field of emissive; there are map, color, strength, exposure, gi"),
            };
        return e;
    }

    public override void Write(Utf8JsonWriter w, SurfaceEmissive v, JsonSerializerOptions o)
    {
        if (v is { Strength: null, Exposure: null, Gi: null } && (v.Map == null) != (v.Color == null))
        {
            w.WriteStringValue(v.Map ?? v.Color);
            return;
        }
        w.WriteStartObject();
        if (v.Map != null) w.WriteString("map", v.Map);
        if (v.Color != null) w.WriteString("color", v.Color);
        if (v.Strength is { } s) w.WriteNumber("strength", s);
        if (v.Exposure is { } x) w.WriteNumber("exposure", x);
        if (v.Gi is { } gi) w.WriteNumber("gi", gi);
        w.WriteEndObject();
    }
}

internal sealed class SurfaceRangeConverter : JsonConverter<(float Start, float Full)?>
{
    public override (float Start, float Full)? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var a = JsonSerializer.Deserialize<float[]>(ref r, o);
        return a is { Length: 2 } ? (a[0], a[1]) : throw new JsonException("takes [start, full]");
    }

    public override void Write(Utf8JsonWriter w, (float Start, float Full)? v, JsonSerializerOptions o)
    {
        if (v is not { } x)
        {
            w.WriteNullValue();
            return;
        }
        w.WriteStartArray();
        w.WriteNumberValue(x.Start);
        w.WriteNumberValue(x.Full);
        w.WriteEndArray();
    }
}
