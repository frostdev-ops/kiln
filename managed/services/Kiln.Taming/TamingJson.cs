// SPDX-License-Identifier: MIT
// Tamings in JSON (dossier 19e 5.1's shape, as a definition of its own): one object or an array of them, every field
// checked by name and type, item ids resolved to exact items when read (no search for "food" when a step runs), and every
// error naming the exact field path ($.steps[1].count). The result is the same TamingDefinition C# makes, validated by the
// engine's own rules. docs/kiln/modding/taming.md.
//
//   { "id": "my-mod:wolf", "creatures": ["game:enemy_wildbeast_wolf"], "pet": "my-mod:wolf-pet",
//     "steps": [ { "kind": "weaken", "maxHealthFraction": 0.3 },
//                { "kind": "feed", "item": "game:food_t2_dog_bait", "count": 1 },
//                { "kind": "tool", "item": "game:tool_t1_whistle", "wear": 1, "allowBreak": false },
//                { "kind": "native", "steps": 1 } ],
//     "rangeMeters": 3, "requireThresholdUntilCommit": true, "inactivitySeconds": 30, "maxAttemptSeconds": 300,
//     "refund": "nonRefundable", "removeTarget": true, "summonPet": true }
using System.Globalization;
using System.Text.Json;

namespace Kiln;

internal static class TamingJson
{
    private static readonly string[] Top =
        ["id", "creatures", "steps", "rangeMeters", "requireThresholdUntilCommit", "inactivitySeconds", "maxAttemptSeconds", "refund", "pet", "removeTarget", "summonPet"];

    /// <summary>The definitions in <paramref name="json"/>; <paramref name="item"/> resolves an item id (its path for
    /// errors) to the exact item.</summary>
    /// <exception cref="ArgumentException">The first field that is wrong, with its path.</exception>
    internal static IReadOnlyList<TamingDefinition> Read(string json, Func<string, string, ItemId> item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { throw new ArgumentException($"$: not JSON ({e.Message})", nameof(json)); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var list = new List<TamingDefinition>();
                var i = 0;
                foreach (var e in root.EnumerateArray()) list.Add(One(e, $"$[{i++}]", item));
                return list;
            }
            return [One(root, "$", item)];
        }
    }

    private static ArgumentException Bad(string path, string why) => new($"{path}: {why}", "json");

    private static TamingDefinition One(JsonElement o, string path, Func<string, string, ItemId> item)
    {
        if (o.ValueKind != JsonValueKind.Object) throw Bad(path, "a taming is an object");
        foreach (var p in o.EnumerateObject())
            if (Array.IndexOf(Top, p.Name) < 0) throw Bad($"{path}.{p.Name}", $"not a field of a taming (fields: {string.Join(", ", Top)})");
        var d = new TamingDefinition
        {
            Id = Str(o, "id", path) ?? throw Bad($"{path}.id", "a taming needs an id (my-mod:wolf)"),
            Steps = Steps(o, path, item),
            Creatures = Strings(o, "creatures", path) ?? throw Bad($"{path}.creatures", "a taming names the creatures it tames"),
            RangeMeters = Num(o, "rangeMeters", path) ?? 3,
            RequireThresholdUntilCommit = Bool(o, "requireThresholdUntilCommit", path) ?? true,
            InactivitySeconds = Num(o, "inactivitySeconds", path) ?? 30,
            MaxAttemptSeconds = Num(o, "maxAttemptSeconds", path) ?? 300,
            Refund = Refund(o, path),
            Pet = Str(o, "pet", path),
            RemoveTarget = Bool(o, "removeTarget", path) ?? true,
            SummonPet = Bool(o, "summonPet", path) ?? true,
        };
        try { TamingEngine.Validate(d); }
        catch (ArgumentException e) { throw Bad(path, e.Message.Replace(" (Parameter 'd')", "")); }
        return d;
    }

    private static IReadOnlyList<TamingStep> Steps(JsonElement o, string path, Func<string, string, ItemId> item)
    {
        if (!o.TryGetProperty("steps", out var a) || a.ValueKind != JsonValueKind.Array) throw Bad($"{path}.steps", "a taming has a list of 1 to 64 steps");
        var list = new List<TamingStep>();
        var i = 0;
        foreach (var s in a.EnumerateArray())
        {
            var at = $"{path}.steps[{i++}]";
            if (s.ValueKind != JsonValueKind.Object) throw Bad(at, "a step is an object with a kind");
            var kind = Str(s, "kind", at) ?? throw Bad($"{at}.kind", "a step has a kind: feed, weaken, tool or native");
            string[] fields = kind switch
            {
                "feed" => ["kind", "item", "count"],
                "weaken" => ["kind", "maxHealthFraction"],
                "tool" => ["kind", "item", "wear", "allowBreak"],
                "native" => ["kind", "steps"],
                _ => throw Bad($"{at}.kind", $"\"{kind}\" is not a step kind (feed, weaken, tool, native)"),
            };
            foreach (var p in s.EnumerateObject())
                if (Array.IndexOf(fields, p.Name) < 0) throw Bad($"{at}.{p.Name}", $"not a field of a {kind} step (fields: {string.Join(", ", fields)})");
            list.Add(kind switch
            {
                "feed" => new FeedStep(Item(s, at, item), Int(s, "count", at) ?? 1),
                "weaken" => new WeakenStep(Num(s, "maxHealthFraction", at) ?? throw Bad($"{at}.maxHealthFraction", "a weaken step needs maxHealthFraction in (0, 1]")),
                "tool" => new UseToolStep(Item(s, at, item), Int(s, "wear", at) ?? 1, Bool(s, "allowBreak", at) ?? false),
                _ => new NativeStep(Int(s, "steps", at) ?? 1),
            });
        }
        return list;
    }

    private static ItemId Item(JsonElement s, string at, Func<string, string, ItemId> resolve) =>
        resolve(Str(s, "item", at) ?? throw Bad($"{at}.item", "the step names its item (an items registry id: game:<name>)"), $"{at}.item");

    private static TamingRefundPolicy Refund(JsonElement o, string path) => Str(o, "refund", path) switch
    {
        null or "nonRefundable" => TamingRefundPolicy.NonRefundable,
        "refundOnFail" => TamingRefundPolicy.RefundOnFail,
        "escrow" => TamingRefundPolicy.Escrow,
        var other => throw Bad($"{path}.refund", $"\"{other}\" is not a refund policy (nonRefundable, refundOnFail, escrow)"),
    };

    private static string? Str(JsonElement o, string name, string path)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : throw Bad($"{path}.{name}", "a string");
    }

    private static IReadOnlyList<string>? Strings(JsonElement o, string name, string path)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Array) throw Bad($"{path}.{name}", "a list of strings");
        var list = new List<string>();
        var i = 0;
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) throw Bad($"{path}.{name}[{i}]", "a string");
            list.Add(e.GetString()!);
            i++;
        }
        return list;
    }

    private static double? Num(JsonElement o, string name, string path)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : throw Bad($"{path}.{name}", "a finite number");
    }

    private static int? Int(JsonElement o, string name, string path)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : throw Bad($"{path}.{name}", "a whole number");
    }

    private static bool? Bool(JsonElement o, string name, string path)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Bad($"{path}.{name}", "true or false");
    }

    /// <summary>The game's items by their items registry ids (docs/kiln/modding/registries/items.md): <c>game:&lt;name&gt;</c>
    /// (the debug name in lower case), <c>game:&lt;name&gt;~&lt;first 8 hex digits of its GUID&gt;</c>,
    /// <c>game:item_&lt;8 hex&gt;</c>, or an item id <c>0x&lt;hex&gt;</c>. A mod's items are named in C# by their ItemId.</summary>
    internal static ItemId GameItems(string id, string path) => ResolveItem(id, path, Items.Lookup);

    internal static ItemId ResolveItem(string id, string path, Func<string, ItemInfo?> lookup)
    {
        if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return lookup(id)?.Id ?? throw Bad(path, $"ItemUnknown: no item {id} on this build");
        if (!id.StartsWith("game:", StringComparison.Ordinal))
            throw Bad(path, $"ItemUnknown: \"{id}\" is not a game item's registry id (game:<name>); a mod's own items are named in C# by their ItemId");
        var name = id["game:".Length..];
        string? hex = null;
        var tilde = name.IndexOf('~');
        if (tilde >= 0)
        {
            hex = name[(tilde + 1)..];
            name = name[..tilde];
        }
        if (hex is null && name.StartsWith("item_", StringComparison.Ordinal) && name.Length == "item_".Length + 8 &&
            uint.TryParse(name.AsSpan("item_".Length), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return lookup($"0x{raw:x8}")?.Id ?? throw Bad(path, $"ItemUnknown: no item {id} on this build");
        var info = lookup(name);
        if (info is null || !string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase)
            || hex is not null && !info.Guid.ToString("N").StartsWith(hex, StringComparison.OrdinalIgnoreCase))
            throw Bad(path, $"ItemUnknown: the game has no item {id} on this build");
        return info.Id;
    }
}
