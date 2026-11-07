// SPDX-License-Identifier: MIT
// VfxBlock: an effect written from nothing, in C# and (the same shape) in the "vfx" section of blocks/*.json. kiln.vfx builds it into the game's
// VfxResource. Every field stays null until it is said, so that what reaches the builder is what was written and the defaults are the builder's alone.
// The shape: schema/kiln-vfx.schema.json, docs/kiln/services/vfx-cook.md.
using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>
/// One effect: particles, a light. Attach it with a <c>Vfx</c> field, or play it from code:
/// <code>
/// var burst = Blocks.Define("frost_burst", new VfxBlock
/// {
///     Particles = [new ParticleBlock { Burst = 24, Lifetime = (0.4f, 0.9f), Speed = (2f, 5f), Spread = Shape.Cone(35), Gravity = -4f,
///                                      Size = Curve.Linear(0.15f, 0f), Color = Gradient.Of("#ffffff", "#7fd4ff"), Alpha = Curve.Linear(1f, 0f),
///                                      Texture = "vfx/shard.png", Blend = Blend.Additive }],
///     Light = new LightBlock { Color = "#7fd4ff", Radius = 6f, Brightness = 4f, Lifetime = 0.6f, FadeOut = 0.5f },
/// });
/// Vfx.PlayAt(burst, position);
/// </code>
/// A mod with a new VFX needs <c>clients: "mod"</c>: the effect has to be on every player's game. The build works that out.
/// </summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record VfxBlock : IBlock
{
    /// <summary>What spawns and how it looks.</summary>
    public IReadOnlyList<ParticleBlock>? Particles { get; init; }
    /// <summary>A point light for the life of the effect.</summary>
    public LightBlock? Light { get; init; }
    /// <summary>A sound's name. Not played yet: counted in the budget.</summary>
    public string? Sound { get; init; }
    /// <summary>Metres beyond which nothing is simulated or drawn: far, or <c>(near, far)</c>. Default (50, 75).</summary>
    public VfxRange? CullDistance { get; init; }
    /// <summary>The name this block had before a rename. Ids come from the oldest name.</summary>
    public string? Was { get; init; }

    /// <summary>Data files may say it. It is a diagnostic for the builder, and has no meaning in code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool GameSpriteModule { init { } }
    /// <inheritdoc cref="GameSpriteModule"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>What spawns and how it looks. It needs a <see cref="Burst"/> or a <see cref="Rate"/>.</summary>
public sealed record ParticleBlock
{
    /// <summary>Particles at the start, and at each loop. 0 to 65535.</summary>
    public int? Burst { get; init; }
    /// <summary>Particles per second. At most 64 spawn per tick.</summary>
    public float? Rate { get; init; }
    /// <summary>Seconds of emission. Without it a rate runs until the effect is stopped; a burst alone needs none.</summary>
    public float? Duration { get; init; }
    /// <summary>Seconds before the first spawn.</summary>
    public float? Delay { get; init; }
    /// <summary>Start again after the <see cref="Duration"/>, which must be above 0.</summary>
    public bool? Loop { get; init; }
    /// <summary>Particles alive at once, 1 to 4096. Derived when left out.</summary>
    public int? MaxCount { get; init; }
    /// <summary>Seconds a particle lives: a number, or <c>(min, max)</c>. Default 1.</summary>
    public VfxRange? Lifetime { get; init; }
    /// <summary>Metres per second at spawn: a number, or <c>(min, max)</c>. Default 1.</summary>
    public VfxRange? Speed { get; init; }
    /// <summary>Direction at spawn, around the effect's up. Default <see cref="Shape.Up"/>.</summary>
    public Shape? Spread { get; init; }
    /// <summary>Metres: start inside a sphere this big. The same as <c>Shape = SpawnShape.Sphere(r)</c>; say one or the other.</summary>
    public float? SpawnRadius { get; init; }
    /// <summary>Where particles start, in the effect's space: <see cref="SpawnShape.Sphere"/>, <see cref="SpawnShape.Shell"/>, <see cref="SpawnShape.Hemisphere"/>, <see cref="SpawnShape.Box(Vector3)"/>,
    /// <see cref="SpawnShape.Disc"/>, <see cref="SpawnShape.Ring"/>, <see cref="SpawnShape.Line"/>. Default: the effect's position. With <see cref="Shape.Outward"/> they fly away from its centre.</summary>
    public SpawnShape? Shape { get; init; }
    /// <summary><see cref="ParticleAlign.Velocity"/>: a camera-facing quad turns to point the way it moves (sparks, rain, streaks). Default <see cref="ParticleAlign.Camera"/>.</summary>
    public ParticleAlign? Align { get; init; }
    /// <summary>With <see cref="ParticleAlign.Velocity"/>: extra length in metres per metre per second of speed, 0 to 100.</summary>
    public float? Stretch { get; init; }
    /// <summary>Advanced: this mod's own particle hooks, a hook module built from HLSL with <c>kiln-vfxcook shader build</c> and linked into Kiln's sprite at start (docs/kiln/modding/shaders.md). Needs <c>"advanced": true</c> in the mod's manifest. If it can't be
    /// used (missing, built for another Kiln, refused by a check, shaders turned off) Kiln's own sprite draws the particle, so the effect still shows.</summary>
    public ParticleShader? Shader { get; init; }
    /// <summary>Metres per second squared on world y: negative falls.</summary>
    public float? Gravity { get; init; }
    /// <summary>Metres per second squared in world space, added to <see cref="Gravity"/>.</summary>
    public Vector3? Acceleration { get; init; }
    /// <summary>Per second: velocity *= exp(-drag * dt).</summary>
    public float? Drag { get; init; }
    /// <summary>Degrees per second: a number, or <c>(min, max)</c>.</summary>
    public VfxRange? Spin { get; init; }
    /// <summary>Degrees at spawn: a number, or <c>(min, max)</c>.</summary>
    public VfxRange? Roll { get; init; }
    /// <summary>Metres over the particle's life. Default 0.25.</summary>
    public Curve? Size { get; init; }
    /// <summary>0 to 1 over the particle's life. Default 1.</summary>
    public Curve? Alpha { get; init; }
    /// <summary>sRGB over the particle's life. Default white.</summary>
    public Gradient? Color { get; init; }
    /// <summary>Multiplies the colour, which may then exceed 1.</summary>
    public float? Intensity { get; init; }
    /// <summary>A PNG in your mod's folder, power of two on both sides, 4 to 2048. Without one the particle is white.</summary>
    public VfxTexture? Texture { get; init; }
    /// <summary>How the texture repeats. Default <see cref="Sampler.Wrap"/>.</summary>
    public Sampler? Sampler { get; init; }
    /// <summary>The texture is a grid of frames.</summary>
    public Flipbook? Flipbook { get; init; }
    /// <summary>Default <see cref="Blend.Alpha"/>.</summary>
    public Blend? Blend { get; init; }
    /// <summary>Take ambient and sun light through the fog volume.</summary>
    public bool? Lit { get; init; }
    /// <summary>Default <see cref="ParticleMesh.Quad"/>.</summary>
    public ParticleMesh? Mesh { get; init; }
    /// <summary>Turn the mesh to the camera. Default: true for a quad.</summary>
    public bool? FaceCamera { get; init; }
    /// <summary>Metres of soft fade against the scene.</summary>
    public float? DepthFade { get; init; }
    /// <summary>0 to 1: how much the colour follows the camera's exposure correction.</summary>
    public float? Emissive { get; init; }
    /// <summary>A material of this mod: its albedo as the <see cref="Texture"/>, its tint in the <see cref="Color"/>, and <see cref="Lit"/>, as far as the particle says nothing itself. A sprite draws with
    /// the game's transparent shaders, so the rest of a material doesn't apply.</summary>
    public MaterialRef? Material { get; init; }

    /// <inheritdoc cref="VfxBlock.Comment"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>One point light for the life of the effect.</summary>
public sealed record LightBlock
{
    /// <summary><c>#rrggbb</c>, sRGB. Default white.</summary>
    public string? Color { get; init; }
    /// <summary>Metres. Default 6.</summary>
    public float? Radius { get; init; }
    public float? Brightness { get; init; }
    /// <summary>Seconds. Default 0: until the effect is stopped.</summary>
    public float? Lifetime { get; init; }
    public float? FadeIn { get; init; }
    /// <summary>Seconds, at the end of the lifetime or after the effect stops.</summary>
    public float? FadeOut { get; init; }
    /// <summary>0 to 1.</summary>
    public float? Flicker { get; init; }
    /// <summary>Metres, in the effect's space.</summary>
    public Vector3? Offset { get; init; }

    /// <inheritdoc cref="VfxBlock.Comment"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>A number, or a range to pick from: <c>0.5f</c>, <c>(0.4f, 0.9f)</c>.</summary>
[JsonConverter(typeof(VfxRangeConverter))]
public readonly record struct VfxRange(float Min, float Max)
{
    public static implicit operator VfxRange(float value) => new(value, value);
    public static implicit operator VfxRange((float Min, float Max) range) => new(range.Min, range.Max);
}

/// <summary>The value of a named VFX parameter: a number (<c>3f</c>, a float), a colour (<c>"#ff0000"</c>, <c>"#ff000080"</c>), a <see cref="Vector3"/>, or <see cref="Int"/>. The game matches
/// the type as well as the name.</summary>
[JsonConverter(typeof(VfxValueConverter))]
public readonly record struct VfxValue
{
    internal object Value { get; private init; }

    public static implicit operator VfxValue(float value) => new() { Value = value };
    public static implicit operator VfxValue(string colour) => new() { Value = colour };
    public static implicit operator VfxValue(Vector3 value) => new() { Value = value };
    public static implicit operator VfxValue(Color value) => new() { Value = $"#{value.R:x2}{value.G:x2}{value.B:x2}{value.A:x2}" };

    /// <summary>A whole-number parameter, as the game's <c>count</c>. A plain number is a float.</summary>
    public static VfxValue Int(int value) => new() { Value = value };
}

/// <summary>Where particles head at spawn: <see cref="Up"/>, <see cref="Sphere"/>, <see cref="Outward"/>, or <see cref="Cone"/>.</summary>
[JsonConverter(typeof(ShapeConverter))]
public sealed record Shape
{
    internal string? Word { get; private init; }
    internal float Degrees { get; private init; }

    public static readonly Shape Up = new() { Word = "up" };
    public static readonly Shape Sphere = new() { Word = "sphere" };
    /// <summary>Away from the centre of the <see cref="ParticleBlock.Shape"/>: out of a sphere, along a disc's plane, across a line. Up at the very centre.</summary>
    public static readonly Shape Outward = new() { Word = "outward" };
    /// <summary>A cone around up: half angle in degrees, 0 to 180.</summary>
    public static Shape Cone(float degrees) => new() { Degrees = degrees };
}

/// <summary>Where particles start, in the effect's space (its scale and rotation apply). Sizes in metres.</summary>
[JsonConverter(typeof(SpawnShapeConverter))]
public sealed record SpawnShape
{
    internal string Kind { get; private init; } = "point";
    internal float A { get; private init; }
    internal float? Inner { get; private init; }
    internal Vector3 Size { get; private init; }

    /// <summary>At the effect's position (the default).</summary>
    public static readonly SpawnShape Point = new();
    /// <summary>Inside a sphere; with <paramref name="inner"/>, outside that radius too.</summary>
    public static SpawnShape Sphere(float radius, float? inner = null) => new() { Kind = "sphere", A = radius, Inner = inner };
    /// <summary>On a sphere's surface.</summary>
    public static SpawnShape Shell(float radius) => new() { Kind = "shell", A = radius };
    /// <summary>Inside the upper half of a sphere.</summary>
    public static SpawnShape Hemisphere(float radius, float? inner = null) => new() { Kind = "hemisphere", A = radius, Inner = inner };
    /// <summary>Inside a box this big, centred.</summary>
    public static SpawnShape Box(Vector3 size) => new() { Kind = "box", Size = size };
    /// <summary>Inside a box this big, centred.</summary>
    public static SpawnShape Box(float x, float y, float z) => Box(new Vector3(x, y, z));
    /// <summary>On a flat disc in the effect's horizontal plane; with <paramref name="inner"/>, outside that radius too.</summary>
    public static SpawnShape Disc(float radius, float? inner = null) => new() { Kind = "disc", A = radius, Inner = inner };
    /// <summary>On a circle in the effect's horizontal plane.</summary>
    public static SpawnShape Ring(float radius) => new() { Kind = "ring", A = radius };
    /// <summary>On a line this long along the effect's x, centred.</summary>
    public static SpawnShape Line(float length) => new() { Kind = "line", A = length };
}

/// <summary>Which way a camera-facing quad turns.</summary>
public enum ParticleAlign
{
    /// <summary>Upright to the camera, turned by <see cref="ParticleBlock.Roll"/> and <see cref="ParticleBlock.Spin"/>.</summary>
    Camera,
    /// <summary>Its up points the way it moves on screen; <see cref="ParticleBlock.Stretch"/> lengthens it with speed.</summary>
    Velocity,
}

/// <summary>Advanced: a mod's own particle hooks (a hook module). <c>"shaders/build/swirl.spv"</c>, or with <see cref="Constants"/> that the hooks read as <c>c.user0</c> and <c>c.user1</c>.</summary>
[JsonConverter(typeof(ParticleShaderConverter))]
public sealed record ParticleShader(string File, IReadOnlyList<float>? Constants = null)
{
    public static implicit operator ParticleShader(string file) => new(file);
}

/// <summary>A number over a particle's life: a constant (<c>0.2f</c>), <see cref="Linear"/>, evenly spaced values (<see cref="Of"/>), or <see cref="Keys"/>.</summary>
[JsonConverter(typeof(CurveConverter))]
public sealed record Curve
{
    internal float? Constant { get; private init; }
    internal (float Start, float End)? Line { get; private init; }
    internal float[]? Values { get; private init; }
    internal (float T, float Value)[]? KeyList { get; private init; }

    public static Curve Linear(float start, float end) => new() { Line = (start, end) };
    public static Curve Of(params float[] values) => new() { Values = values };
    /// <summary>Values at times from 0 to 1, growing.</summary>
    public static Curve Keys(params (float T, float Value)[] keys) => new() { KeyList = keys };
    public static implicit operator Curve(float value) => new() { Constant = value };
}

/// <summary>A colour over a particle's life: one (<c>"#7fd4ff"</c>), evenly spaced ones (<see cref="Of"/>), or <see cref="Keys"/>.</summary>
[JsonConverter(typeof(GradientConverter))]
public sealed record Gradient
{
    internal string? One { get; private init; }
    internal string[]? List { get; private init; }
    internal (float T, string Colour)[]? KeyList { get; private init; }

    public static Gradient Of(params string[] colours) => new() { List = colours };
    /// <summary>Colours at times from 0 to 1, growing.</summary>
    public static Gradient Keys(params (float T, string Colour)[] keys) => new() { KeyList = keys };
    public static implicit operator Gradient(string colour) => new() { One = colour };
}

/// <summary>A PNG in the mod's folder. <see cref="Linear"/>: the image holds data, not colour.</summary>
[JsonConverter(typeof(VfxTextureConverter))]
public sealed record VfxTexture(string File, bool? Linear = null)
{
    public static implicit operator VfxTexture(string file) => new(file);
}

/// <summary>The texture is a grid of frames. Without <see cref="Fps"/> the grid plays once over a particle's life.</summary>
public sealed record Flipbook
{
    public int? Columns { get; init; }
    public int? Rows { get; init; }
    public float? Fps { get; init; }
}

public enum Blend { Alpha, Additive }
public enum Sampler { Wrap, Mirror, Clamp, ClampToZero }
public enum ParticleMesh
{
    /// <summary>Faces the camera.</summary>
    Quad,
    /// <summary>Lies flat.</summary>
    [JsonStringEnumMemberName("quad-xz")] QuadXz,
}

/// <summary>What <see cref="Blocks.Define(string, VfxBlock)"/> gives back: a block's handle, which <see cref="Vfx"/>'s calls also take.</summary>
public sealed class VfxBlockHandle : BlockHandle<VfxBlock>
{
    internal VfxBlockHandle(string mod, string name, Guid guid, VfxBlock block, VfxBlockStatus status) : base(mod, name, guid, $"{mod}.{name}", block) => Status = status;

    /// <summary>Whether this game has it now.</summary>
    public VfxBlockStatus Status { get; internal set; }

    public static implicit operator VfxId(VfxBlockHandle handle) => VfxId.Of(handle.Guid);
}

/// <summary>Whether a <see cref="VfxBlock"/> is in this game. The game lists its VFX once at start, so a definition from code that it has not seen yet is there from the next start.</summary>
public enum VfxBlockStatus
{
    /// <summary>The game has this block with this definition.</summary>
    Registered,
    /// <summary>Kept for the next start. Until then what names it shows nothing.</summary>
    Pending,
    /// <summary>Nothing draws here: a dedicated server, or kiln.vfx is off. What names it still carries its id, for the clients that have it.</summary>
    NotHere,
}

// ------------------------------------------------------------------------------------------------- the JSON shapes -----

internal sealed class Vec3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        var a = JsonSerializer.Deserialize<float[]>(ref r, o);
        return a is { Length: 3 } ? new Vector3(a[0], a[1], a[2]) : throw new JsonException("takes [x, y, z]");
    }

    public override void Write(Utf8JsonWriter w, Vector3 v, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        w.WriteNumberValue(v.X); w.WriteNumberValue(v.Y); w.WriteNumberValue(v.Z);
        w.WriteEndArray();
    }
}

internal sealed class VfxRangeConverter : JsonConverter<VfxRange>
{
    public override VfxRange Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return r.GetSingle();
        var a = JsonSerializer.Deserialize<float[]>(ref r, o);
        return a is { Length: 2 } ? new VfxRange(a[0], a[1]) : throw new JsonException("takes a number or [min, max]");
    }

    public override void Write(Utf8JsonWriter w, VfxRange v, JsonSerializerOptions o)
    {
        if (v.Min == v.Max)
        {
            w.WriteNumberValue(v.Min);
            return;
        }
        w.WriteStartArray();
        w.WriteNumberValue(v.Min); w.WriteNumberValue(v.Max);
        w.WriteEndArray();
    }
}

// a number (a float), "#rrggbb", [x, y, z], or {"int": n}
internal sealed class VfxValueConverter : JsonConverter<VfxValue>
{
    public override VfxValue Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return r.GetSingle();
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        using var doc = JsonDocument.ParseValue(ref r);
        var e = doc.RootElement;
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 3) return new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("int", out var n) && n.TryGetInt32(out var i)) return VfxValue.Int(i);
        throw new JsonException("a parameter is a number, \"#rrggbb\", [x, y, z] or {\"int\": n}");
    }

    public override void Write(Utf8JsonWriter w, VfxValue v, JsonSerializerOptions o)
    {
        switch (v.Value)
        {
            case float f: w.WriteNumberValue(f); break;
            case string s: w.WriteStringValue(s); break;
            case Vector3 x:
                w.WriteStartArray();
                w.WriteNumberValue(x.X); w.WriteNumberValue(x.Y); w.WriteNumberValue(x.Z);
                w.WriteEndArray();
                break;
            case int i:
                w.WriteStartObject();
                w.WriteNumber("int", i);
                w.WriteEndObject();
                break;
            default: w.WriteNullValue(); break;
        }
    }
}

internal sealed class ShapeConverter : JsonConverter<Shape>
{
    public override Shape? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String)
            return r.GetString() switch
            {
                "up" => Shape.Up,
                "sphere" => Shape.Sphere,
                "outward" => Shape.Outward,
                var s => throw new JsonException($"\"{s}\" is not a spread; there are \"up\", \"sphere\", \"outward\" and {{\"cone\": degrees}}"),
            };
        using var doc = JsonDocument.ParseValue(ref r);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("cone", out var cone) && cone.ValueKind == JsonValueKind.Number)
            return Shape.Cone(cone.GetSingle());
        throw new JsonException("a spread is \"up\", \"sphere\", \"outward\" or {\"cone\": degrees}");
    }

    public override void Write(Utf8JsonWriter w, Shape v, JsonSerializerOptions o)
    {
        if (v.Word != null)
        {
            w.WriteStringValue(v.Word);
            return;
        }
        w.WriteStartObject();
        w.WriteNumber("cone", v.Degrees);
        w.WriteEndObject();
    }
}

// "point", {"sphere": r, "inner": r2}, {"shell": r}, {"hemisphere": r}, {"box": [x, y, z]}, {"disc": r}, {"ring": r}, {"line": length}: the builder's shape (cook/define.cpp)
internal sealed class SpawnShapeConverter : JsonConverter<SpawnShape>
{
    public override SpawnShape? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String)
            return r.GetString() == "point" ? SpawnShape.Point : throw new JsonException("a shape is \"point\" or one of {\"sphere\": r}, {\"shell\": r}, {\"hemisphere\": r}, {\"box\": [x, y, z]}, {\"disc\": r}, {\"ring\": r}, {\"line\": length}");
        using var doc = JsonDocument.ParseValue(ref r);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException("a shape is an object such as {\"sphere\": 2}");
        string? kind = null;
        JsonElement value = default;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name is "inner" or "comment") continue;
            if (kind != null) throw new JsonException("one shape at a time");
            kind = p.Name;
            value = p.Value;
        }
        float Number(JsonElement x) => x.ValueKind == JsonValueKind.Number && x.TryGetSingle(out var n) && float.IsFinite(n)
            ? n : throw new JsonException("shape sizes must be finite numbers");
        float? inner = e.TryGetProperty("inner", out var i) ? Number(i) : null;
        if (inner != null && kind is not ("sphere" or "hemisphere" or "disc"))
            throw new JsonException("inner applies only to sphere, hemisphere or disc");
        return kind switch
        {
            "sphere" => SpawnShape.Sphere(Number(value), inner),
            "shell" => SpawnShape.Shell(Number(value)),
            "hemisphere" => SpawnShape.Hemisphere(Number(value), inner),
            "disc" => SpawnShape.Disc(Number(value), inner),
            "ring" => SpawnShape.Ring(Number(value)),
            "line" => SpawnShape.Line(Number(value)),
            "box" when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 3
                => SpawnShape.Box(Number(value[0]), Number(value[1]), Number(value[2])),
            _ => throw new JsonException("a shape needs one of sphere, shell, hemisphere, box, disc, ring, line"),
        };
    }

    public override void Write(Utf8JsonWriter w, SpawnShape v, JsonSerializerOptions o)
    {
        if (v.Kind == "point")
        {
            w.WriteStringValue("point");
            return;
        }
        w.WriteStartObject();
        if (v.Kind == "box")
        {
            w.WriteStartArray("box");
            w.WriteNumberValue(v.Size.X); w.WriteNumberValue(v.Size.Y); w.WriteNumberValue(v.Size.Z);
            w.WriteEndArray();
        }
        else w.WriteNumber(v.Kind, v.A);
        if (v.Inner is { } inner) w.WriteNumber("inner", inner);
        w.WriteEndObject();
    }
}

internal sealed class ParticleShaderConverter : JsonConverter<ParticleShader>
{
    public override ParticleShader? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        using var doc = JsonDocument.ParseValue(ref r);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("file", out var file) || file.GetString() is not { } path)
            throw new JsonException("a shader is the path of a compiled .spv, or {\"file\": path, \"constants\": [up to 8 numbers]}");
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("file" or "constants" or "comment")) throw new JsonException($"\"{p.Name}\" is not part of a shader; it takes file and constants");
        return new ParticleShader(path, e.TryGetProperty("constants", out var c) ? c.EnumerateArray().Select(x => x.GetSingle()).ToArray() : null);
    }

    public override void Write(Utf8JsonWriter w, ParticleShader v, JsonSerializerOptions o)
    {
        if (v.Constants is not { Count: > 0 })
        {
            w.WriteStringValue(v.File);
            return;
        }
        w.WriteStartObject();
        w.WriteString("file", v.File);
        w.WriteStartArray("constants");
        foreach (var x in v.Constants) w.WriteNumberValue(x);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}

internal sealed class CurveConverter : JsonConverter<Curve>
{
    public override Curve? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return r.GetSingle();
        if (r.TokenType == JsonTokenType.StartArray) return Curve.Of(JsonSerializer.Deserialize<float[]>(ref r, o)!);
        using var doc = JsonDocument.ParseValue(ref r);
        var e = doc.RootElement;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("linear", out var l) && l.GetArrayLength() == 2)
            return Curve.Linear(l[0].GetSingle(), l[1].GetSingle());
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("keys", out var k))
            return Curve.Keys(k.EnumerateArray().Select(x => (x[0].GetSingle(), x[1].GetSingle())).ToArray());
        throw new JsonException("a curve is a number, a list of values, {\"linear\": [start, end]} or {\"keys\": [[t, value], ...]}");
    }

    public override void Write(Utf8JsonWriter w, Curve v, JsonSerializerOptions o)
    {
        if (v.Constant is { } c) w.WriteNumberValue(c);
        else if (v.Line is { } l)
        {
            w.WriteStartObject();
            w.WriteStartArray("linear");
            w.WriteNumberValue(l.Start); w.WriteNumberValue(l.End);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else if (v.KeyList is { } keys)
        {
            w.WriteStartObject();
            w.WriteStartArray("keys");
            foreach (var (time, value) in keys)
            {
                w.WriteStartArray();
                w.WriteNumberValue(time); w.WriteNumberValue(value);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else
        {
            w.WriteStartArray();
            foreach (var x in v.Values ?? []) w.WriteNumberValue(x);
            w.WriteEndArray();
        }
    }
}

internal sealed class GradientConverter : JsonConverter<Gradient>
{
    public override Gradient? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        if (r.TokenType == JsonTokenType.StartArray) return Gradient.Of(JsonSerializer.Deserialize<string[]>(ref r, o)!);
        using var doc = JsonDocument.ParseValue(ref r);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var k))
            return Gradient.Keys(k.EnumerateArray().Select(x => (x[0].GetSingle(), x[1].GetString() ?? "")).ToArray());
        throw new JsonException("a gradient is a colour, a list of colours or {\"keys\": [[t, \"#rrggbb\"], ...]}");
    }

    public override void Write(Utf8JsonWriter w, Gradient v, JsonSerializerOptions o)
    {
        if (v.One != null) w.WriteStringValue(v.One);
        else if (v.KeyList is { } keys)
        {
            w.WriteStartObject();
            w.WriteStartArray("keys");
            foreach (var (time, colour) in keys)
            {
                w.WriteStartArray();
                w.WriteNumberValue(time); w.WriteStringValue(colour);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        else
        {
            w.WriteStartArray();
            foreach (var x in v.List ?? []) w.WriteStringValue(x);
            w.WriteEndArray();
        }
    }
}

internal sealed class VfxTextureConverter : JsonConverter<VfxTexture>
{
    public override VfxTexture? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        using var doc = JsonDocument.ParseValue(ref r);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("file", out var file) || file.GetString() is not { } path)
            throw new JsonException("a texture is a path to a PNG, or {\"file\": path, \"linear\": true}");
        foreach (var p in e.EnumerateObject())
            if (p.Name is not ("file" or "linear")) throw new JsonException($"\"{p.Name}\" is not part of a texture; it takes file and linear");
        return new VfxTexture(path, e.TryGetProperty("linear", out var l) ? l.GetBoolean() : null);
    }

    public override void Write(Utf8JsonWriter w, VfxTexture v, JsonSerializerOptions o)
    {
        if (v.Linear == null)
        {
            w.WriteStringValue(v.File);
            return;
        }
        w.WriteStartObject();
        w.WriteString("file", v.File);
        w.WriteBoolean("linear", v.Linear.Value);
        w.WriteEndObject();
    }
}
