// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    static partial void RegisterItems(Bridge bridge, Logger log)
    {
        if (!Items.Available) return;
        // Lua contract: {entity} -> {inventories[{link, entity, category, slots[{item, count, pide}]}], generic, available}
        bridge.Register("inventory", req =>
        {
            var player = RequirePlayer(req);
            var c = Items.Read(player.Entity) ?? throw new AdminError("This player has no readable inventory.");
            var mod = ModItemNames();
            var inventories = new JsonArray();
            for (var i = 0; i < c.Inventories.Count; i++)
            {
                var (entity, category) = c.Inventories[i];
                var slots = new JsonArray();
                foreach (var s in c.Slots.Where(s => (uint)(s.Slot.Value & 0xFFFFFFFF) == entity.Value))
                {
                    var o = new JsonObject { ["item"] = s.Item.Value, ["count"] = s.Count };
                    if (!s.ItemEntity.IsNone) o["pide"] = s.ItemEntity.Value;
                    if (s.IsGear) { o["level"] = s.Level; o["rarity"] = s.Rarity?.ToString(); o["equipped"] = s.IsEquipped; }
                    if (Items.Get(s.Item) is { } info) o["name"] = info.DisplayName ?? mod.GetValueOrDefault(info.Name) ?? info.Label;
                    slots.Add(o);
                }
                inventories.Add(new JsonObject { ["link"] = i + 1, ["entity"] = entity.Value, ["category"] = category.ToString(), ["slots"] = slots });
            }
            return new JsonObject { ["inventories"] = inventories, ["generic"] = c.GenericSlots, ["available"] = c.AvailableSlots };
        });
        // Lua contract: {entity, item, count, maxStack?} -> {given, requested, stacks}; Kiln adds gear {level?, rarity?}.
        bridge.RegisterAsync("give_item", async req =>
        {
            var player = RequirePlayer(req);
            var id = (uint)Number(req, "item");
            var count = (int)Number(req, "count");
            if (count < 1 || count > 100000) throw new AdminError("count must be 1 to 100000");
            if (Items.Get(new ItemId(id)) is null) throw new AdminError($"unknown item {id}");
            Gear? gear = null;
            if (req["level"] is not null || req["rarity"] is not null)
            {
                int? level = req["level"] is null ? null : (int)Number(req, "level");
                ItemRarity? rarity = req["rarity"]?.GetValue<string>() is { } r
                    ? Enum.TryParse<ItemRarity>(r, ignoreCase: true, out var rr) ? rr : throw new AdminError($"unknown rarity {r}")
                    : null;
                gear = new Gear(level, rarity);
            }
            var result = await Items.Give(player.Entity, new ItemId(id), count, gear, GiveOptions.Notify);
            if (!result.Succeeded) throw new AdminError(result.Message.Length > 0 ? result.Message : result.Status.ToString());
            return new JsonObject
            {
                ["given"] = result.Done, ["requested"] = result.Requested, ["stacks"] = result.Changes.Count,
                ["status"] = result.Status.ToString(),
            };
        });
        // {q} -> [{id, name, debug, gear}]: name is what players see (a mod item's own display name too), debug the
        // game's debug name; a mod item is also found by its display name.
        bridge.Register("item_search", req =>
        {
            var q = req["q"]?.GetValue<string>() ?? throw new AdminError("q required");
            var mod = ModItemNames();
            var hits = Items.Search(q, 50).ToList();
            foreach (var (debug, shown) in mod)
                if (hits.Count < 50 && shown.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) && Items.Lookup(debug) is { } extra &&
                    !hits.Any(h => h.Id == extra.Id))
                    hits.Add(extra);
            return new JsonArray([.. hits.Select(i => (JsonNode)new JsonObject
            {
                ["id"] = i.Id.Value, ["name"] = i.DisplayName ?? mod.GetValueOrDefault(i.Name) ?? i.Name, ["debug"] = i.Name, ["gear"] = i.IsGear,
            })]);
        });
    }

    /// <summary>Display names of the items mods define (kiln.content definitions: displayName, else the English or
    /// every-language name in texts), by debug name. Empty without kiln.content.</summary>
    private static Dictionary<string, string> ModItemNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!Content.Available || JsonNode.Parse(NewContent.DefinitionsJson()) is not JsonArray defs) return names;
            foreach (var d in defs.OfType<JsonObject>())
            {
                if (d["kind"]?.GetValue<string>() != "item" || d["name"]?.GetValue<string>() is not { Length: > 0 } debug) continue;
                var texts = d["definition"]?["texts"] as JsonObject;
                var shown = Text(d["displayName"]) ?? Text(texts?["en"]?["name"]) ?? Text(texts?["*"]?["name"]);
                if (shown != null) names[debug] = shown;
            }
        }
        catch (Exception e) when (e is NotSupportedException or System.Text.Json.JsonException or InvalidOperationException or FileNotFoundException or TypeLoadException)
        {
        }
        return names;

        static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var t) && t.Length > 0 ? t : null;
    }
}
