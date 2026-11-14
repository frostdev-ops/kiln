// SPDX-License-Identifier: MIT
// JSON of external kinds: a serializer per registration (so a provider's types leave with it), the canonical form plans are
// frozen in and hashed over, and the "provider:plural" sections of blocks/*.json.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class KindJson
{
    /// <summary>Strict block options with caches of their own: a registration's types are not rooted in the shared ones.</summary>
    internal static JsonSerializerOptions NewOptions() => new(BlockJson.SharedOptions)
    {
        // Equal options can share System.Text.Json's caching context. A resolver unique to this registration keeps
        // collectible provider types out of the process-wide BlockJson metadata cache.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>JSON with every object's keys in ordinal order and no white space: equal values give equal bytes.</summary>
    internal static string Canonical(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, MaxDepth = 256 })) Write(w, node);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void Write(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null: w.WriteNullValue(); break;
            case JsonObject o:
                w.WriteStartObject();
                foreach (var (k, v) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(k);
                    Write(w, v);
                }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var v in a) Write(w, v);
                w.WriteEndArray();
                break;
            default: node.WriteTo(w); break;
        }
    }

    /// <summary>How deep a value nests (each object or array adds 1) and how many objects and arrays it has.</summary>
    internal static (int Depth, int Nodes) Measure(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                int depth = 0, nodes = 1;
                foreach (var (_, v) in o)
                {
                    var (d, n) = Measure(v);
                    depth = Math.Max(depth, d);
                    nodes += n;
                }
                return (depth + 1, nodes);
            }
            case JsonArray a:
            {
                int depth = 0, nodes = 1;
                foreach (var v in a)
                {
                    var (d, n) = Measure(v);
                    depth = Math.Max(depth, d);
                    nodes += n;
                }
                return (depth + 1, nodes);
            }
            default: return (0, 0);
        }
    }

    /// <summary>One block of an external section: <c>{"format": 1, ...payload}</c>. The format is checked and taken off, the
    /// rest is the kind's record, read strictly.</summary>
    internal static IBlock ReadRecord(KindEntry kind, string name, JsonElement value)
    {
        var label = $"{kind.Section}.{name}";
        if (value.ValueKind != JsonValueKind.Object) throw new BlockError(label, null, "a block is an object");
        var (type, options) = (kind.RecordType, kind.Options);
        if (kind.Retired || type == null || options == null) throw new BlockError(label, null, $"block kind {kind.Id} is no longer registered: its provider mod was unloaded");
        var payload = JsonObject.Create(value) ?? throw new BlockError(label, null, "a block is an object");
        if (!payload.TryGetPropertyValue("format", out var format)) throw new BlockError(label, "format", $"a block of kind {kind.Id} says \"format\": 1");
        if (format is not JsonValue f || !f.TryGetValue<int>(out var n) || n != 1) throw new BlockError(label, "format", $"{format?.ToJsonString() ?? "null"} is not a format of kind {kind.Id}; only 1");
        payload.Remove("format");
        try
        {
            return payload.Deserialize(type, options) as IBlock ?? throw new BlockError(label, null, "null is no block");
        }
        catch (JsonException e)
        {
            throw new BlockError(label, e.Path is { Length: > 1 } p ? p[2..] : null, e.Message);
        }
    }

    /// <summary>A record as its kind's canonical JSON.</summary>
    internal static string RecordJson(KindEntry kind, string label, IBlock block, JsonSerializerOptions options)
    {
        try
        {
            return Canonical(JsonSerializer.SerializeToNode(block, block.GetType(), options));
        }
        catch (Exception e) when (e is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new BlockError(label, null, $"a block of kind {kind.Id} must be plain data, the same in C# and JSON: {e.Message}");
        }
    }
}
