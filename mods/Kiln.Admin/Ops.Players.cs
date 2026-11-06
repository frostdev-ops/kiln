// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    /// <summary>Same shape as the Lua mod: [{entity, pos{x,y,z}, ...}] plus name/steamId/admin/permissions.</summary>
    internal static JsonArray PlayerList()
    {
        var list = new JsonArray();
        foreach (var p in Players.All)
        {
            if (!p.InGame) continue;
            var o = new JsonObject
            {
                ["entity"] = p.Entity.Value,
                ["name"] = p.Name,
                ["steamId"] = p.SteamId.ToString(),
                ["admin"] = p.IsAdmin,
                ["permissions"] = p.Permissions.ToString(),
            };
            if (p.Position is { } pos) o["pos"] = Pos(pos);
            HealthOf(p.Entity, o);
            CombatOf(p.Entity, o);
            // without kiln.combat: the replicated vitals (NetworkPlayerAttributes, one step old)
            if (o["healthMax"] is null && p.Snapshot() is { HealthMax: > 0 } snap)
            {
                o["health"] = Math.Max(0, snap.Health ?? 0);
                o["healthMax"] = snap.HealthMax;
            }
            list.Add(o);
        }
        return list;
    }

    /// <summary>Adds health/healthMax from the NetworkHealth component when kiln.world is available (creatures have
    /// it; player characters don't). <see cref="CombatOf"/> runs after it and wins.</summary>
    static partial void HealthOf(EntityId entity, JsonObject player);

    /// <summary>Adds god (invulnerable) and the live health/healthMax when kiln.combat is available.</summary>
    static partial void CombatOf(EntityId entity, JsonObject player);

    static partial void RegisterPlayers(Bridge bridge, Logger log)
    {
        if (!Players.Available)
        {
            bridge.Register("players", _ => throw new AdminError("kiln.players is unavailable on this server build"));
            return;
        }
        bridge.Register("players", _ => PlayerList());
    }

    // Deaths, like the Lua mod: a player whose health drops to 0 from above 0 gets a grave marker (last 50).
    private static readonly Dictionary<uint, long> LastHealth = [];
    private static readonly List<JsonObject> Deaths = [];

    static partial void StatePlayers(JsonObject state)
    {
        if (!Players.Available) return;
        var list = PlayerList();
        var seen = new HashSet<uint>();
        foreach (var node in list)
        {
            if (node is not JsonObject p || p["entity"] is null) continue;
            var e = p["entity"]!.GetValue<uint>();
            seen.Add(e);
            if (p["health"] is not JsonValue h || p["pos"] is not JsonObject pos) continue;
            var health = h.TryGetValue<int>(out var hi) ? hi : h.TryGetValue<uint>(out var hu) ? (long)hu : -1;
            if (health < 0) continue;
            if (health == 0 && LastHealth.TryGetValue(e, out var prev) && prev > 0)
            {
                Deaths.Add(new JsonObject { ["entity"] = e, ["pos"] = pos.DeepClone(), ["clock"] = Environment.TickCount64 / 1000.0 });
                if (Deaths.Count > 50) Deaths.RemoveAt(0);
            }
            LastHealth[e] = health;
        }
        foreach (var gone in LastHealth.Keys.Where(k => !seen.Contains(k)).ToList()) LastHealth.Remove(gone);
        state["players"] = list;
        state["deaths"] = new JsonArray([.. Deaths.Select(d => (JsonNode)d.DeepClone())]);
    }

    internal static Player RequirePlayer(JsonObject req, string key = "entity")
    {
        var id = new EntityId(Entity(req, key));
        return Players.Find(id) is { InGame: true } p ? p : throw new AdminError("That player is no longer in the world.");
    }
}
