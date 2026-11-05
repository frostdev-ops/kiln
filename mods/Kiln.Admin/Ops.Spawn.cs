// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

internal static partial class Ops
{
    private const string AdminTag = "admin";
    private static readonly Dictionary<Guid, (string Name, int Seen)> SeenEnemies = [];

    static partial void TemplateOf(EntityId entity, JsonObject enemy)
    {
        if (!Spawn.Available || Spawn.Inspect(entity) is not { Template: { } guid } info) return;
        enemy["guid"] = guid.ToString();
        enemy["name"] = Spawn.Template(guid)?.ToString() ?? info.TemplateName;
    }

    // Enemies seen in the world, grouped by template, so the dashboard can offer them for spawning (as the Lua mod).
    private static partial JsonArray EnemyCatalog(IReadOnlyList<EntityId> enemies)
    {
        if (!Spawn.Available) return [];
        foreach (var e in enemies)
        {
            if (Spawn.Inspect(e) is not { Template: { } guid } info) continue;
            var name = Spawn.Template(guid)?.ToString() ?? info.TemplateName ?? guid.ToString();
            SeenEnemies[guid] = (name, SeenEnemies.TryGetValue(guid, out var s) ? s.Seen + 1 : 1);
        }
        return new JsonArray([.. SeenEnemies.Select(kv => (JsonNode)new JsonObject
        {
            ["guid"] = kv.Key.ToString(), ["name"] = kv.Value.Name, ["seen"] = kv.Value.Seen,
        })]);
    }

    static partial void RegisterSpawn(Bridge bridge, Logger log)
    {
        if (!Spawn.Available) return;
        // Lua contract: {guid, count, x, y, z, spread?} -> {spawned, failed, reason}
        bridge.RegisterAsync("spawn", async req =>
        {
            if (!Spawn.WorldRunning) throw new AdminError("The world is still loading; try again in a few seconds.");
            var guidText = req["guid"]?.GetValue<string>() ?? throw new AdminError("guid must be a template GUID");
            if (!Guid.TryParse(guidText, out var guid) || Spawn.Template(guid) is not { } template)
                throw new AdminError("guid must be a template GUID");
            var count = req["count"] is null ? 1 : (int)Number(req, "count");
            if (count < 1 || count > 20) throw new AdminError("count must be 1 to 20");
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var spread = req["spread"] is null ? 4 : Number(req, "spread");
            var spawned = await Spawn.Group(template.Name, at, count, spread, new SpawnOptions { Tag = AdminTag });
            return new JsonObject { ["spawned"] = spawned.Count, ["failed"] = count - spawned.Count, ["reason"] = null };
        });
        // Lua contract: {} -> {removed, failed}
        bridge.Register("despawn_spawned", _ =>
        {
            var r = Spawn.CleanupTracked(AdminTag);
            return new JsonObject { ["removed"] = r.Destroyed, ["failed"] = 0, ["missing"] = r.Missing };
        });
        // Kiln: drop an item into the world {item, amount, x, y, z}
        bridge.RegisterAsync("spawn_item", async req =>
        {
            var e = await Spawn.Item((uint)Number(req, "item"), (int)Number(req, "amount"),
                new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z")), 0, new SpawnOptions { Tag = AdminTag });
            return new JsonObject { ["entity"] = e.Value };
        });
    }

    static partial void StateSpawn(JsonObject state)
    {
        if (Spawn.Available) state["spawned"] = Spawn.Tracked.Count(t => t.Tag == AdminTag && t.IsLive);
    }
}
