// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Dashboard ops backed by Kiln services. Each service's ops are added here once that service lands
/// (players, settings, teleport, inventory/give_item, enemies/spawn/despawn_spawned, time, broadcast).</summary>
internal static partial class Ops
{
    // One file per service (Ops.<Service>.cs) implements these; a service without a file simply has no ops.
    static partial void RegisterPlayers(Bridge bridge, Logger log);
    static partial void StatePlayers(JsonObject state);
    static partial void RegisterWorld(Bridge bridge, Logger log);
    static partial void RegisterItems(Bridge bridge, Logger log);
    static partial void RegisterSpawn(Bridge bridge, Logger log);
    static partial void StateSpawn(JsonObject state);
    static partial void RegisterSettings(Bridge bridge, Logger log);
    static partial void RegisterChatTime(Bridge bridge, Logger log);
    static partial void RegisterCombat(Bridge bridge, Logger log);
    static partial void StateCombat(JsonObject state);
    static partial void RegisterSaves(Bridge bridge, Logger log);
    static partial void RegisterGameplay(Bridge bridge, Logger log);
    static partial void RegisterContent(Bridge bridge, Logger log);
    static partial void RegisterConfig(Bridge bridge, Logger log);
    static partial void RegisterMods(Bridge bridge, Logger log);
    static partial void StateMods(JsonObject state);

    /// <summary>Kiln.Admin's own data directory (kiln/data/kiln-admin), set before RegisterAll.</summary>
    internal static string DataDirectory { get; set; } = "";

    public static void RegisterAll(Bridge bridge, Logger log)
    {
        RegisterPlayers(bridge, log);
        RegisterWorld(bridge, log);
        RegisterItems(bridge, log);
        RegisterSpawn(bridge, log);
        RegisterSettings(bridge, log);
        RegisterChatTime(bridge, log);
        RegisterCombat(bridge, log);
        RegisterSaves(bridge, log);
        RegisterGameplay(bridge, log);
        RegisterContent(bridge, log);
        RegisterConfig(bridge, log);
        RegisterMods(bridge, log);
    }

    public static void AddState(JsonObject state)
    {
        StatePlayers(state);
        StateSpawn(state);
        StateCombat(state);
        StateMods(state);
    }

    internal static JsonObject Pos(WorldPosition p) => new() { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };

    internal static double Number(JsonObject req, string key) =>
        req[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : throw new AdminError($"{key} must be a number");

    internal static uint Entity(JsonObject req, string key = "entity") =>
        req[key] is JsonValue v && v.TryGetValue<double>(out var d) && d >= 1 && d <= uint.MaxValue && d == Math.Floor(d)
            ? (uint)d : throw new AdminError($"{key} must be an entity id");
}
