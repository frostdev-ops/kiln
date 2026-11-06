// SPDX-License-Identifier: MIT
// The item blocks in blocks/*.json: a section per kind (BlockJson reads them), and the converters of the fields that take a part.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

internal static class ItemJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("items", BlockJson.Make<ItemBlock>), ("wands", BlockJson.Make<WandBlock>), ("armor", BlockJson.Make<ArmorBlock>), ("stations", BlockJson.Make<StationBlock>), ("cosmetics", BlockJson.Make<CosmeticBlock>), ("wornLooks", BlockJson.Make<WornLook>),
        ("looks", BlockJson.Make<VisualBlock>), ("weaponStats", BlockJson.Make<WeaponStats>), ("armorStats", BlockJson.Make<ArmorStats>),
        ("weaponClasses", BlockJson.Make<WeaponClass>), ("armorSlots", BlockJson.Make<ArmorSlot>), ("armorSets", BlockJson.Make<ArmorSet>), ("onHits", BlockJson.Make<OnHitBlock>),
        ("props", BlockJson.Make<PropBlock>), ("placements", BlockJson.Make<PlacementBlock>), ("comforts", BlockJson.Make<ComfortBlock>), ("processing", BlockJson.Make<ProcessingBlock>), ("menus", BlockJson.Make<MenuBlock>),
    ];
}

internal sealed class PartRefConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(PartRef<>);
    public override JsonConverter? CreateConverter(Type t, JsonSerializerOptions o) =>
        (JsonConverter?)Activator.CreateInstance(typeof(PartRefConverter<>).MakeGenericType(t.GetGenericArguments()[0]));
}

internal sealed class PartRefConverter<T> : JsonConverter<PartRef<T>> where T : class, IBlock
{
    public override PartRef<T>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new PartRef<T>(null, r.GetString());
        return new PartRef<T>(JsonSerializer.Deserialize<T>(ref r, o), null);
    }

    public override void Write(Utf8JsonWriter w, PartRef<T> v, JsonSerializerOptions o)
    {
        if (v.Name != null) w.WriteStringValue(v.Name);
        else JsonSerializer.Serialize(w, v.Inline, o);
    }
}

// "onHit": [ "burst", { … } ], "onHit": "hits", or "onHit": { "effects": [ … ] }
internal sealed class HitsRefConverter : JsonConverter<HitsRef>
{
    public override HitsRef? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString()!;
        if (r.TokenType == JsonTokenType.StartObject) return JsonSerializer.Deserialize<OnHitBlock>(ref r, o)!;
        var list = new HitsRef();
        foreach (var e in JsonSerializer.Deserialize<List<EffectRef>>(ref r, o) ?? []) list.Add(e);
        return list;
    }

    public override void Write(Utf8JsonWriter w, HitsRef v, JsonSerializerOptions o)
    {
        if (v.Part != null) JsonSerializer.Serialize(w, v.Part, o);
        else JsonSerializer.Serialize(w, v.ToList(), o);
    }
}

internal sealed class PerkRefConverter : JsonConverter<PerkRef>
{
    public override bool HandleNull => true;
    public override PerkRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.TokenType == JsonTokenType.Null ? null : r.GetString());
    public override void Write(Utf8JsonWriter w, PerkRef v, JsonSerializerOptions o)
    {
        if (v.Value == null) w.WriteNullValue();
        else w.WriteStringValue(v.Value);
    }
}

internal sealed class StationRefConverter : JsonConverter<StationRef>
{
    public override StationRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, StationRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

// a premade by its key, also as a dictionary's key: "bonus": { "health": 60 }
internal sealed class EquipBonusConverter : JsonConverter<EquipBonus>
{
    private static EquipBonus Find(string? key) => EquipBonus.All.FirstOrDefault(x => x.Key == key)
        ?? throw new JsonException($"\"{key}\" is not an EquipBonus; there are {string.Join(", ", EquipBonus.All.Select(x => x.Key))}");
    public override EquipBonus? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Find(r.GetString());
    public override void Write(Utf8JsonWriter w, EquipBonus v, JsonSerializerOptions o) => w.WriteStringValue(v.Key);
    public override EquipBonus ReadAsPropertyName(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => Find(r.GetString());
    public override void WriteAsPropertyName(Utf8JsonWriter w, EquipBonus v, JsonSerializerOptions o) => w.WritePropertyName(v.Key);
}
