// SPDX-License-Identifier: MIT
// Quest and dialogue blocks in blocks/*.json: the sections, and the shapes JSON writes that C# writes as values (a text that is a string or a text per language,
// a marker that is false, a step's spawn that is one or a list, a signal that is a name).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

internal static class QuestBlockJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("quests", BlockJson.Make<QuestBlock>), ("dialogues", BlockJson.Make<DialogueBlock>),
    ];
}

// "Hello", or { "en": "Hello", "de": "Hallo" }: the game's language keys
internal sealed class UiTextConverter : JsonConverter<UiText>
{
    public override UiText? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => ReadText(ref r);

    public static UiText ReadText(ref Utf8JsonReader r)
    {
        if (r.TokenType == JsonTokenType.String) return new UiText(r.GetString() ?? "");
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("a text is a string, or { \"en\": \"...\", \"de\": \"...\" }");
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            var language = r.GetString() ?? "";
            r.Read();
            if (r.TokenType != JsonTokenType.String) throw new JsonException($"the text for \"{language}\" is a string");
            texts[language] = r.GetString() ?? "";
        }
        if (texts.Count == 0) throw new JsonException("a text per language needs at least one language: { \"en\": \"...\" }");
        return new UiText(texts);
    }

    public static void WriteText(Utf8JsonWriter w, UiText v)
    {
        if (v.Languages.Count == 1 && v.Languages.First() == "*") w.WriteStringValue(v.For("*"));
        else
        {
            w.WriteStartObject();
            foreach (var language in v.Languages.Order(StringComparer.Ordinal)) w.WriteString(language, v.For(language));
            w.WriteEndObject();
        }
    }

    public override void Write(Utf8JsonWriter w, UiText v, JsonSerializerOptions o) => WriteText(w, v);
}

internal sealed class UiTextListConverter : JsonConverter<IReadOnlyList<UiText>>
{
    public override IReadOnlyList<UiText>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("a list of texts");
        var list = new List<UiText>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) list.Add(UiTextConverter.ReadText(ref r));
        return list;
    }

    public override void Write(Utf8JsonWriter w, IReadOnlyList<UiText> v, JsonSerializerOptions o)
    {
        w.WriteStartArray();
        foreach (var t in v) UiTextConverter.WriteText(w, t);
        w.WriteEndArray();
    }
}

// false: no marker; an object: its fields
internal sealed class MarkerConverter : JsonConverter<MarkerBlock>
{
    public override MarkerBlock? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => r.TokenType switch
    {
        JsonTokenType.False => MarkerBlock.None,
        JsonTokenType.True => new MarkerBlock(),
        JsonTokenType.StartObject => JsonSerializer.Deserialize<MarkerBlock>(ref r, o),
        _ => throw new JsonException("a marker is an object, or false for none"),
    };

    public override void Write(Utf8JsonWriter w, MarkerBlock v, JsonSerializerOptions o)
    {
        if (v.Hidden) w.WriteBooleanValue(false);
        else JsonSerializer.Serialize(w, v, o);
    }
}

// one object, or a list of them
internal sealed class OneOrManyConverter<T> : JsonConverter<IReadOnlyList<T>>
{
    public override IReadOnlyList<T>? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType != JsonTokenType.StartArray) return [JsonSerializer.Deserialize<T>(ref r, o)!];
        var list = new List<T>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) list.Add(JsonSerializer.Deserialize<T>(ref r, o)!);
        return list;
    }

    public override void Write(Utf8JsonWriter w, IReadOnlyList<T> v, JsonSerializerOptions o) => JsonSerializer.Serialize(w, v.ToArray(), o);
}

// "signal", or { "signal": "name", "count": 3 }
internal sealed class CustomGoalConverter : JsonConverter<CustomGoal>
{
    public override CustomGoal? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) =>
        r.TokenType == JsonTokenType.String ? new CustomGoal { Signal = r.GetString() ?? "" } : JsonSerializer.Deserialize<CustomGoal>(ref r, o);

    public override void Write(Utf8JsonWriter w, CustomGoal v, JsonSerializerOptions o)
    {
        if (v.Count == 1) w.WriteStringValue(v.Signal);
        else JsonSerializer.Serialize(w, v, o);
    }
}
