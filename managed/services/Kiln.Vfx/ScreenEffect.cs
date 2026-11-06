// SPDX-License-Identifier: MIT
// What a full-screen effect is: colour grading, a tint, a vignette, a fill for fades and flashes, and how it comes and goes. kiln.screen draws it
// (native/core/abi/services/kiln_screen.h); ScreenEffects plays it. docs/kiln/services/screen.md.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>
/// A full-screen effect. Every field left out changes nothing:
/// <code>
/// var hurt = new ScreenEffect { Tint = new ScreenColor("#ff4030", 0.35f), Vignette = new ScreenVignette { Color = "#400000", Amount = 0.8f }, FadeIn = 0.1f, Seconds = 0.3f, FadeOut = 0.6f };
/// var cold = new ScreenEffect { Temperature = -0.6f, Saturation = 0.7f };          // until stopped
/// var flash = new ScreenEffect { Fill = "#ffffff", Seconds = 0, FadeOut = 0.5f };  // a white flash
/// </code>
/// The picture is graded in linear light, in this order: exposure and temperature, contrast, saturation, gamma, tint, vignette, fill. Then the whole
/// effect is mixed in by <see cref="Strength"/> and its fades. Several effects playing at once run one after another, by <see cref="Order"/>.
/// It is drawn over the frame the game presents, so it also covers the game's own UI.
/// </summary>
public record ScreenEffect
{
    /// <summary>Lower runs first. Among equals, the one played first.</summary>
    public int? Order { get; init; }
    /// <summary>0 to 1: how much of the whole effect shows. Default 1.</summary>
    public float? Strength { get; init; }
    /// <summary>Seconds to reach full, 0 to 60. Default 0.</summary>
    public float? FadeIn { get; init; }
    /// <summary>Seconds at full, 0 to 3600. Left out: until it is stopped. 0 with a fade out is a flash.</summary>
    public float? Seconds { get; init; }
    /// <summary>Seconds to fade once it ends or is stopped, 0 to 60. Default 0.</summary>
    public float? FadeOut { get; init; }
    /// <summary>Stops, -8 to 8: 1 is twice as bright. Default 0.</summary>
    public float? Exposure { get; init; }
    /// <summary>Around mid grey, 0.05 to 4. Default 1.</summary>
    public float? Contrast { get; init; }
    /// <summary>0 is grey, 0 to 4. Default 1.</summary>
    public float? Saturation { get; init; }
    /// <summary>Above 1 lifts the mid tones, 0.1 to 5. Default 1.</summary>
    public float? Gamma { get; init; }
    /// <summary>-1 cold to 1 warm, keeping brightness. Default 0.</summary>
    public float? Temperature { get; init; }
    /// <summary>A colour the picture is multiplied by: <c>"#7fd4ff"</c>, or with how much: <c>new ScreenColor("#7fd4ff", 0.5f)</c>.</summary>
    public ScreenColor? Tint { get; init; }
    /// <summary>Darker (or coloured) edges: <c>0.6f</c> is how much, or a <see cref="ScreenVignette"/>.</summary>
    public ScreenVignette? Vignette { get; init; }
    /// <summary>The whole picture goes to this colour, by its amount: a fade to black, or a flash with a fade out.</summary>
    public ScreenColor? Fill { get; init; }

    /// <summary>Seconds from start to gone, or null: until it is stopped.</summary>
    [JsonIgnore]
    public double? Length => Seconds is { } s ? (FadeIn ?? 0) + s + (FadeOut ?? 0) : null;

    /// <summary>What is wrong, by field as written in JSON: <c>("tint.color", "\"red\" is not #rrggbb")</c>. Empty: nothing.</summary>
    public IReadOnlyList<(string Field, string Problem)> Problems()
    {
        var out_ = new List<(string, string)>();
        void Range(string field, float? v, float lo, float hi)
        {
            if (v is not { } x) return;
            if (!float.IsFinite(x)) out_.Add((field, $"{x} is not a number"));
            else if (x < lo || x > hi) out_.Add((field, $"{x.ToString(CultureInfo.InvariantCulture)} is outside {lo.ToString(CultureInfo.InvariantCulture)}..{hi.ToString(CultureInfo.InvariantCulture)}"));
        }
        void Colour(string field, string? c)
        {
            if (c != null && !ScreenColor.TryParse(c, out _)) out_.Add((field, $"\"{c}\" is not a colour: #rrggbb"));
        }
        Range("strength", Strength, 0, 1);
        Range("fadeIn", FadeIn, 0, 60);
        Range("seconds", Seconds, 0, 3600);
        Range("fadeOut", FadeOut, 0, 60);
        Range("exposure", Exposure, -8, 8);
        Range("contrast", Contrast, 0.05f, 4);
        Range("saturation", Saturation, 0, 4);
        Range("gamma", Gamma, 0.1f, 5);
        Range("temperature", Temperature, -1, 1);
        if (Tint is { } t)
        {
            Colour("tint.color", t.Color);
            Range("tint.amount", t.Amount, 0, 1);
        }
        if (Vignette is { } v)
        {
            Colour("vignette.color", v.Color);
            Range("vignette.amount", v.Amount, 0, 1);
            Range("vignette.radius", v.Radius, 0, 1);
            Range("vignette.softness", v.Softness, 0.001f, 2);
        }
        if (Fill is { } f)
        {
            Colour("fill.color", f.Color);
            Range("fill.amount", f.Amount, 0, 1);
        }
        return out_;
    }
}

/// <summary>A colour and how much of it: <c>"#ff4030"</c> is all of it.</summary>
[JsonConverter(typeof(ScreenColorConverter))]
public sealed record ScreenColor(string Color, float Amount = 1)
{
    public static implicit operator ScreenColor(string color) => new(color);

    /// <summary><c>#rrggbb</c> as sRGB values from 0 to 1.</summary>
    public static bool TryParse(string text, out (float R, float G, float B) rgb)
    {
        rgb = default;
        if (text is not { Length: 7 } || text[0] != '#' || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = (((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        return true;
    }
}

/// <summary>Darker or coloured edges. <c>0.6f</c> is the short form: that much black.</summary>
[JsonConverter(typeof(ScreenVignetteConverter))]
public sealed record ScreenVignette
{
    /// <summary>0 to 1 at the corners. Default 1.</summary>
    public float? Amount { get; init; }
    /// <summary><c>#rrggbb</c>. Default black.</summary>
    public string? Color { get; init; }
    /// <summary>Where it starts: 0 the centre, 1 the corners. Default 0.5.</summary>
    public float? Radius { get; init; }
    /// <summary>How far past <see cref="Radius"/> it takes to reach full, 0.001 to 2. Default 0.5.</summary>
    public float? Softness { get; init; }

    public static implicit operator ScreenVignette(float amount) => new() { Amount = amount };
}

internal sealed class ScreenColorConverter : JsonConverter<ScreenColor>
{
    public override ScreenColor? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new ScreenColor(r.GetString()!);
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("a colour is \"#rrggbb\", or { \"color\": \"#rrggbb\", \"amount\": 0.5 }");
        string? color = null;
        float amount = 1;
        using var doc = JsonDocument.ParseValue(ref r);
        foreach (var p in doc.RootElement.EnumerateObject())
            switch (p.Name)
            {
                case "color": color = p.Value.GetString(); break;
                case "amount": amount = p.Value.GetSingle(); break;
                default: throw new JsonException($"a colour has \"color\" and \"amount\", not \"{p.Name}\"");
            }
        return new ScreenColor(color ?? throw new JsonException("a colour needs \"color\": \"#rrggbb\""), amount);
    }

    public override void Write(Utf8JsonWriter w, ScreenColor v, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        w.WriteString("color", v.Color);
        w.WriteNumber("amount", v.Amount);
        w.WriteEndObject();
    }
}

internal sealed class ScreenVignetteConverter : JsonConverter<ScreenVignette>
{
    public override ScreenVignette? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.Number) return new ScreenVignette { Amount = r.GetSingle() };
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("a vignette is how much (0.6), or { \"amount\", \"color\", \"radius\", \"softness\" }");
        var v = new ScreenVignette();
        using var doc = JsonDocument.ParseValue(ref r);
        foreach (var p in doc.RootElement.EnumerateObject())
            v = p.Name switch
            {
                "amount" => v with { Amount = p.Value.GetSingle() },
                "color" => v with { Color = p.Value.GetString() },
                "radius" => v with { Radius = p.Value.GetSingle() },
                "softness" => v with { Softness = p.Value.GetSingle() },
                _ => throw new JsonException($"a vignette has \"amount\", \"color\", \"radius\" and \"softness\", not \"{p.Name}\""),
            };
        return v;
    }

    public override void Write(Utf8JsonWriter w, ScreenVignette v, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        if (v.Amount is { } a) w.WriteNumber("amount", a);
        if (v.Color is { } c) w.WriteString("color", c);
        if (v.Radius is { } r) w.WriteNumber("radius", r);
        if (v.Softness is { } s) w.WriteNumber("softness", s);
        w.WriteEndObject();
    }
}
