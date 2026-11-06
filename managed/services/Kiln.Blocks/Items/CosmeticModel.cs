// SPDX-License-Identifier: MIT
// Cosmetics: what the game's five vanity slots show instead of the armour under them. C# records, and the same shape in blocks/*.json ("cosmetics", and "wornLooks" for a look of its own).
// A WornLook is a part: armor can wear one as well. docs/kiln/modding/cosmetics.md.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>Which vanity slot it goes in. It shows instead of the armour in that slot, and gives none of its stats.</summary>
public enum CosmeticSlot { Head, UpperBody, Arms, LowerBody, Feet }

/// <summary>A body part or a worn piece a look hides while it is worn. Hidden, it is not drawn; hair and beards also have cuts for a hat or a hood: <see cref="Fit"/>.</summary>
public enum BodyPart
{
    Head, Hair, EyeBrow, Beard, Neck, Collar, Chest, Waist, Crotch, UpperArms, LowerArms, Hands, UpperLegs, LowerLegs, Feet,
    /// <summary>The armour or cosmetic in a slot: a long coat can hide what is on the legs.</summary>
    ArmorHead, ArmorUpperBody, ArmorArms, ArmorLowerBody, ArmorFeet,
    RingLeft, RingRight, Shield, MainHand,
}

/// <summary>A fit: the flags the game raises for what a character wears and is. A look's <see cref="WornLook.Fits"/> are the flags it raises, so that the character's other parts change
/// to suit it: hair for a hood, legs for tight boots. A model has blend shapes for flags, and <see cref="WornLook.Variants"/> are whole models for them. The values are the game's bits.</summary>
[Flags]
[JsonConverter(typeof(FitConverter))]
public enum Fit : uint
{
    None = 0,
    /// <summary>A female character. Every piece follows it.</summary>
    Female = 1u << 0,
    /// <summary>Raised by a head piece: hair changes to its cut under a hat.</summary>
    Hat = 1u << 1,
    /// <summary>Raised by a head piece: hair changes to its cut under a hood.</summary>
    Hood = 1u << 2,
    /// <summary>The face's jaw shape, one of sixteen, raised by the character's head: a beard follows it with a morph target or a variant of that name.</summary>
    Jaw01 = 1u << 3,
    Jaw02 = 1u << 4,
    Jaw03 = 1u << 5,
    Jaw04 = 1u << 6,
    Jaw05 = 1u << 7,
    Jaw06 = 1u << 8,
    Jaw07 = 1u << 9,
    Jaw08 = 1u << 10,
    Jaw09 = 1u << 11,
    Jaw10 = 1u << 12,
    Jaw11 = 1u << 13,
    Jaw12 = 1u << 14,
    Jaw13 = 1u << 15,
    Jaw14 = 1u << 16,
    Jaw15 = 1u << 17,
    Jaw16 = 1u << 18,
    /// <summary>Raised by an upper body piece that leaves the hips free: the legs change to suit it.</summary>
    NoArmor = 1u << 19,
    /// <summary>Raised by a shield in hand, unless the weapon hides it: the upper body and arms can change.</summary>
    Shield = 1u << 20,
    /// <summary>Raised by tight gloves or boots: sleeves change to suit them.</summary>
    Tight = 1u << 21,
    /// <summary>Upper body: sleeves, a tighter cut.</summary>
    ArmsTight01 = 1u << 22,
    /// <summary>Lower body: a tight belt.</summary>
    LegsBeltTight = 1u << 23,
    /// <summary>Raised by tight boots: the legs change to suit them.</summary>
    LegsFeetTight = 1u << 24,
    /// <summary>Lower body: another cut for tight boots.</summary>
    LegsFeetTight01 = 1u << 25,
}

/// <summary>Where a rigid look sits: the game's slots on the player's skeleton, or <c>j&lt;index&gt;</c> for any joint. The model's origin is the joint and its axes are the character's: y up, facing +z.</summary>
public static class WornAttach
{
    public const string Head = "Head", Chest = "Chest", Pelvis = "Pelvis", FootLeft = "FootL", FootRight = "FootR", HandLeft = "HandPalmL", HandRight = "HandPalmR",
        KneeLeft = "KneeL", KneeRight = "KneeR", ArmLeft = "ArmL";
}

/// <summary>What something looks like on the body: a model of your own or a game item's, what it hides and how the rest of the character fits around it. A part: inline, from
/// <see cref="Blocks.Define{T}"/>, or by name. A look with files is drawn by every player's game: the mod needs <c>clients: mod</c>.</summary>
public sealed record WornLook : IBlock
{
    /// <summary>A game item, or an item of this mod, whose whole look this takes: its models, what it hides and how it fits. Anything else you say here wins.</summary>
    public ItemRef? Like { get; init; }

    /// <summary>The model: a glTF in the mod's folder. Skinned on the player's skeleton (<c>kiln skeleton player</c> writes one to weight against), or a plain mesh with <see cref="Attach"/>. A morph target
    /// named <c>Female</c> is the shape on a female body, and so one model is both. Also <c>item:&lt;name&gt;</c>, or the GUID of a model your <c>assets/assets.json</c> serves.</summary>
    [ClientsModWhen(OwnFile)]
    public string? Model { get; init; }

    /// <summary>A second file for a female body, instead of a morph target.</summary>
    [ClientsModWhen(OwnFile)]
    public string? Female { get; init; }

    /// <summary>Whole models for what the character wears or is: <c>[Fit.Shield] = "models/coat-shield.glb"</c>, <c>[Fit.Female | Fit.Tight] = …</c>. The most specific one that applies is drawn, and
    /// <see cref="Model"/> otherwise. Which flags a slot looks at is the game's: the head and feet only <see cref="Fit.Female"/>.</summary>
    [JsonConverter(typeof(FitVariantsConverter))]
    public IDictionary<Fit, string> Variants { get; init; } = new Dictionary<Fit, string>();

    /// <summary>What it makes the rest of the character do: <c>Fit.Hood</c> gives hair its hood cut. The game's own hats and hoods say this, and hair that has no such cut stays as it is.</summary>
    [JsonConverter(typeof(FitsConverter))]
    public Fit? Fits { get; init; }

    /// <summary>What is not drawn while this is worn. Nothing else is hidden: a chest piece that covers the chest has to say so.</summary>
    [JsonConverter(typeof(NamedEnumListConverter<BodyPart>))]
    public IReadOnlyList<BodyPart>? Hides { get; init; }

    /// <summary>A rigid model: a mesh with no skin, on this slot (<see cref="WornAttach"/>) or <c>j&lt;index&gt;</c>. Its origin is the joint, so it needs no armature.</summary>
    public string? Attach { get; init; }

    /// <summary>Coarser files of <see cref="Model"/>, from the next level on. One level is fine.</summary>
    public IReadOnlyList<string>? Lods { get; init; }

    /// <summary>When each level shows: one per level, 0 first. Null: a default.</summary>
    public IReadOnlyList<double>? PixelSizes { get; init; }

    /// <summary>What draws it: a glTF material's name to a material block's name or a game material's GUID, and <c>"*"</c> for every material. A glTF material it leaves out becomes a block of its own.</summary>
    public IDictionary<string, string> Materials { get; init; } = new Dictionary<string, string>();

    /// <summary>The icon: a PNG in the mod's folder, or <c>item:&lt;name&gt;</c>. Null: the game draws the model.</summary>
    [ClientsModWhen("(?i)\\.png$")]
    public string? Icon { get; init; }

    internal const string OwnFile = "(?i)\\.(glb|gltf)$";
}

/// <summary>An armor set's look for a slot: a <see cref="VisualBlock"/> (a game item's look) or a <see cref="WornLook"/>, inline, from <see cref="Blocks.Define{T}"/>, or by name.</summary>
[JsonConverter(typeof(ArmorLookConverter))]
public sealed class ArmorLook
{
    internal ArmorLook(PartRef<VisualBlock>? visual, PartRef<WornLook>? worn, string? name) { Visual = visual; Worn = worn; Name = name; }
    public PartRef<VisualBlock>? Visual { get; }
    public PartRef<WornLook>? Worn { get; }
    /// <summary>A block's name, of either kind: which one is found when the set is used.</summary>
    public string? Name { get; }
    public static implicit operator ArmorLook(VisualBlock block) => new(block, null, null);
    public static implicit operator ArmorLook(WornLook block) => new(null, block, null);
    public static implicit operator ArmorLook(BlockHandle<VisualBlock> handle) => new(handle, null, null);
    public static implicit operator ArmorLook(BlockHandle<WornLook> handle) => new(null, handle, null);
    public static implicit operator ArmorLook(string name) => new(null, null, name);
}

/// <summary>A piece for a vanity slot: it shows over what the player wears there, with no stats of its own and none taken away. Made exactly as its recipe says.</summary>
public sealed record CosmeticBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    [JsonConverter(typeof(NamedEnumConverter<CosmeticSlot>))]
    public required CosmeticSlot Slot { get; init; }
    public required PartRef<WornLook> Look { get; init; }

    /// <summary>Takes the game's dyes. They change what a material has a tint mask for: a game material does, a material block not yet.</summary>
    public bool Dyes { get; init; }

    public RecipeBlock? Recipe { get; init; }
    public LootBlock? Loot { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }

    /// <summary>Game fields this block does not cover, set on the item it makes: <c>Tweak = { { GameFields.Items.MaxStackSize, 5 } }</c>. It wins over what the block sets.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

// The converters below sit on the properties: blocks/*.json's options read every enum with a converter of their own, which wins over one on the type, and says nothing of what the names are.

/// <summary>An enum by its camel-case name, and a mistake lists the names.</summary>
internal sealed class NamedEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    internal static string Name(T v) => JsonNamingPolicy.CamelCase.ConvertName(v.ToString());

    internal static T Parse(string? text) =>
        Enum.GetValues<T>().FirstOrDefault(v => string.Equals(v.ToString(), text, StringComparison.OrdinalIgnoreCase)) is var v && string.Equals(v.ToString(), text, StringComparison.OrdinalIgnoreCase)
            ? v : throw new JsonException($"\"{text}\" is not a {typeof(T).Name}; there are {string.Join(", ", Enum.GetValues<T>().Select(Name))}");

    public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        r.TokenType == JsonTokenType.String ? Parse(r.GetString()) : throw new JsonException($"a {typeof(T).Name} is a name: {string.Join(", ", Enum.GetValues<T>().Select(Name))}");

    public override void Write(Utf8JsonWriter w, T v, JsonSerializerOptions o) => w.WriteStringValue(Name(v));
}

internal sealed class NamedEnumListConverter<T> : JsonConverter<IReadOnlyList<T>?> where T : struct, Enum
{
    public override IReadOnlyList<T>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException($"a list of names: {string.Join(", ", Enum.GetValues<T>().Select(NamedEnumConverter<T>.Name))}");
        var list = new List<T>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) list.Add(NamedEnumConverter<T>.Parse(r.TokenType == JsonTokenType.String ? r.GetString() : null));
        return list;
    }

    public override void Write(Utf8JsonWriter w, IReadOnlyList<T>? v, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        foreach (var x in v ?? []) w.WriteStringValue(NamedEnumConverter<T>.Name(x));
        w.WriteEndArray();
    }
}

internal sealed class FitsConverter : JsonConverter<Fit?>
{
    public override Fit? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType == JsonTokenType.Null ? null : new FitConverter().Read(ref r, typeof(Fit), o);
    public override void Write(Utf8JsonWriter w, Fit? v, JsonSerializerOptions o)
    {
        if (v is { } f) w.WriteStringValue(FitConverter.Text(f));
        else w.WriteNullValue();
    }
}

internal sealed class FitVariantsConverter : JsonConverter<IDictionary<Fit, string>>
{
    public override IDictionary<Fit, string> Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("variants is an object: { \"shield\": \"models/....glb\" }");
        var d = new Dictionary<Fit, string>();
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            var key = FitConverter.Many(r.GetString()!);
            r.Read();
            if (!d.TryAdd(key, r.GetString() ?? throw new JsonException("a variant is a file"))) throw new JsonException($"variant {FitConverter.Text(key)} is given twice");
        }
        return d;
    }

    public override void Write(Utf8JsonWriter w, IDictionary<Fit, string> v, JsonSerializerOptions o)
    {
        w.WriteStartObject();
        foreach (var (k, f) in v) w.WriteString(FitConverter.Text(k), f);
        w.WriteEndObject();
    }
}

// A set's slot: a name, or an object that is a worn look when it says what only a worn look says, or names a glTF; otherwise a look.
internal sealed class ArmorLookConverter : JsonConverter<ArmorLook>
{
    private static readonly string[] WornOnly = ["female", "variants", "fits", "hides", "attach", "lods", "pixelSizes", "materials"];

    public override ArmorLook? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        using var doc = JsonDocument.ParseValue(ref r);
        var e = doc.RootElement;
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException("a look is a name or an object");
        var worn = e.EnumerateObject().Any(p => WornOnly.Contains(p.Name))
                   || (e.TryGetProperty("model", out var m) && m.GetString() is { } f && (f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)));
        return worn ? e.Deserialize<WornLook>(o)! : e.Deserialize<VisualBlock>(o)!;
    }

    public override void Write(Utf8JsonWriter w, ArmorLook v, JsonSerializerOptions o)
    {
        if (v.Name != null) w.WriteStringValue(v.Name);
        else if (v.Worn != null) JsonSerializer.Serialize(w, v.Worn, o);
        else JsonSerializer.Serialize(w, v.Visual, o);
    }
}

// "fits": "hood" or [ "hood", "hat" ]; a variant's key is "shield" or "female+tight"
internal sealed class FitConverter : JsonConverter<Fit>
{
    private static uint One(string? name) =>
        Enum.TryParse<Fit>(name, ignoreCase: true, out var f) && f != Fit.None && Enum.IsDefined(f) ? (uint)f
            : throw new JsonException($"\"{name}\" is not a fit; there are {string.Join(", ", Enum.GetNames<Fit>().Where(n => n != "None").Select(JsonNamingPolicy.CamelCase.ConvertName))}");

    internal static Fit Many(string text) => (Fit)text.Split('+', ',').Select(s => s.Trim()).Aggregate(0u, (m, s) => m | One(s));

    internal static string Text(Fit v) => string.Join("+", Enum.GetValues<Fit>().Where(f => f != Fit.None && v.HasFlag(f)).Select(f => JsonNamingPolicy.CamelCase.ConvertName(f.ToString())));

    public override Fit Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return Many(r.GetString()!);
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("a fit is a name, or a list of names");
        var m = Fit.None;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) m |= Many(r.GetString() ?? "");
        return m;
    }

    public override void Write(Utf8JsonWriter w, Fit v, JsonSerializerOptions o) => w.WriteStringValue(Text(v));
    public override Fit ReadAsPropertyName(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Many(r.GetString()!);
    public override void WriteAsPropertyName(Utf8JsonWriter w, Fit v, JsonSerializerOptions o) => w.WritePropertyName(Text(v));
}
