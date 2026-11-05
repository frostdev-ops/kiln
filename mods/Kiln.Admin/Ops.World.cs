// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    private struct NetworkHealth
    {
        public uint Health;
        public uint HealthMax;
    }

    static partial void HealthOf(EntityId entity, JsonObject player)
    {
        if (World.Available && World.TryRead<NetworkHealth>(entity, "NetworkHealth", out var hp))
        {
            player["health"] = hp.Health;
            player["healthMax"] = hp.HealthMax;
        }
    }

    static partial void RegisterWorld(Bridge bridge, Logger log)
    {
        if (!World.Available) return;
        // Lua contract: {entity, x, y, z, safe?} -> {entity, to{x,y,z}, method}; Kiln adds the outcome.
        bridge.RegisterAsync("teleport", async req =>
        {
            var player = RequirePlayer(req);
            var target = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var safe = req["safe"]?.GetValue<bool>() ?? true;
            var r = await Teleport.To(player.Entity, target, new TeleportOptions { FindGround = safe, Checkpoint = true });
            return Moved(r, target);
        });
        // {entity, target} -> next to another player
        bridge.RegisterAsync("teleport_to_player", async req =>
        {
            var player = RequirePlayer(req);
            var other = RequirePlayer(req, "target");
            var r = await Teleport.ToEntity(player.Entity, other.Entity);
            return Moved(r, r.Target);
        });
        // Lua contract: {near?{x,y,z}, radius?} -> {near[{entity, guid, name, pos}], catalog[], total}
        bridge.Register("enemies", req =>
        {
            var all = World.Find("NetworkEnemy");
            var near = new JsonArray();
            if (req["near"] is JsonObject c)
            {
                var center = new WorldPosition(Number(c, "x"), Number(c, "y"), Number(c, "z"));
                var radius = req["radius"] is JsonValue rv && rv.TryGetValue<double>(out var rd) ? rd : 80;
                foreach (var e in all)
                {
                    if (World.Position(e) is not { } p || p.DistanceTo(center) > radius) continue;
                    var o = new JsonObject { ["entity"] = e.Value, ["pos"] = Pos(p) };
                    TemplateOf(e, o);
                    near.Add(o);
                }
            }
            return new JsonObject { ["near"] = near, ["catalog"] = EnemyCatalog(all), ["total"] = all.Count };
        });
    }

    private static JsonObject Moved(TeleportResult r, WorldPosition target)
    {
        if (!r.Moved) throw new AdminError("Teleport failed: " + r.Reason);
        return new JsonObject
        {
            ["entity"] = r.Entity.Value, ["to"] = Pos(target), ["method"] = "kiln-teleport",
            ["position"] = Pos(r.Position), ["distance"] = r.Distance, ["steps"] = r.Steps, ["flags"] = r.Flags.ToString(),
        };
    }

    /// <summary>Adds guid/name from the spawn service's template lookup, when available.</summary>
    static partial void TemplateOf(EntityId entity, JsonObject enemy);
    private static partial JsonArray EnemyCatalog(IReadOnlyList<EntityId> enemies);
}
