// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Mod configs, permissions and typed commands (Kiln.Api config registry, Kiln.Permissions, Kiln.Chat).</summary>
internal static partial class Ops
{
    static partial void RegisterConfig(Bridge bridge, Logger log)
    {
        bridge.Register("config_list", _ => Config.Schema());
        bridge.Register("config_get", req => RequireConfig(req).Schema());
        // {id, path, value} value as JSON (number, string, bool, array)
        bridge.Register("config_set", req =>
        {
            var file = RequireConfig(req);
            var path = req["path"]?.GetValue<string>() ?? throw new AdminError("path required");
            if (!file.TrySet(path, req["value"]?.DeepClone(), out var error)) throw new AdminError(error);
            log.Info($"dashboard set config {file.Id}: {path}");
            return file.Schema();
        });
        bridge.Register("config_reload", req =>
        {
            var file = RequireConfig(req);
            file.Reload();
            return file.Schema();
        });

        {
            bridge.Register("permissions_get", _ => Permissions.ToJson());
            // {node, steamId | entity} -> decision
            bridge.Register("permissions_check", req => PermissionErrors(() =>
            {
                var node = req["node"]?.GetValue<string>() ?? throw new AdminError("node required");
                var d = Permissions.Explain(Subject(req), node);
                return new JsonObject
                {
                    ["allowed"] = d.Allowed, ["node"] = d.Node, ["level"] = d.Level.ToString(), ["rule"] = d.Rule, ["from"] = d.From,
                    ["groups"] = new JsonArray([.. d.Groups.Select(g => (JsonNode)g)]),
                };
            }));
            // {steamId, node, allow: true|false|null, name?} (name: the note kept next to the SteamID in permissions.json)
            bridge.Register("permissions_player_node", req => PermissionErrors(() =>
            {
                Permissions.SetPlayerNode(SteamId(req), Node(req), req["allow"]?.GetValue<bool>(), PlayerNote(req));
                return Permissions.ToJson();
            }));
            // {steamId, group, add: bool, name?}
            bridge.Register("permissions_player_group", req => PermissionErrors(() =>
            {
                var group = req["group"]?.GetValue<string>() ?? throw new AdminError("group required");
                if (req["add"]?.GetValue<bool>() ?? true) Permissions.AddPlayerToGroup(SteamId(req), group, PlayerNote(req));
                else Permissions.RemovePlayerFromGroup(SteamId(req), group);
                return Permissions.ToJson();
            }));
            // {group, admin?, inherits?: [..], node?, allow?}
            bridge.Register("permissions_group", req => PermissionErrors(() =>
            {
                var group = req["group"]?.GetValue<string>() ?? throw new AdminError("group required");
                var inherits = req["inherits"] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToArray() : null;
                Permissions.SetGroup(group, req["admin"]?.GetValue<bool>(), inherits);
                if (req["node"] is not null) Permissions.SetGroupNode(group, Node(req), req["allow"]?.GetValue<bool>());
                return Permissions.ToJson();
            }));
            bridge.Register("permissions_group_remove", req => PermissionErrors(() =>
            {
                Permissions.RemoveGroup(req["group"]?.GetValue<string>() ?? throw new AdminError("group required"));
                return Permissions.ToJson();
            }));
            // {userGroup, groups: [..]}
            bridge.Register("permissions_map_user_group", req => PermissionErrors(() =>
            {
                var ug = req["userGroup"]?.GetValue<string>() ?? throw new AdminError("userGroup required");
                var groups = req["groups"] is JsonArray a ? a.Select(n => n?.GetValue<string>() ?? "").ToArray() : [];
                Permissions.MapUserGroup(ug, groups);
                return Permissions.ToJson();
            }));
            bridge.Register("permissions_reload", _ => PermissionErrors(() =>
            {
                Permissions.Reload();
                return Permissions.ToJson();
            }));
        }

        bridge.Register("commands_describe", _ => Commands.Describe(CommandSource.Remote("dashboard")));
        // {text, steamId?} -> completion suggestions for a partial command line
        bridge.Register("command_complete", req =>
        {
            var text = req["text"]?.GetValue<string>() ?? "";
            var src = req["steamId"] is not null ? CommandSource.ForSteamId(SteamId(req)) : CommandSource.Remote("dashboard");
            var c = Commands.Complete(src, text);
            return new JsonObject { ["start"] = c.Start, ["suggestions"] = new JsonArray([.. c.Suggestions.Select(s => (JsonNode)s)]) };
        });
        bridge.Register("events_stats", _ => new JsonArray([.. Events.Subscriptions.GroupBy(s => s.Type.Name)
            .Select(g => (JsonNode)new JsonObject { ["event"] = g.Key, ["handlers"] = g.Count() })]));
    }

    /// <summary>Invalid nodes, unknown groups and the like are the operator's mistakes: send the message, no stack trace.</summary>
    private static JsonNode? PermissionErrors(Func<JsonNode?> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AdminError(e.Message);
        }
    }

    private static ConfigFile RequireConfig(JsonObject req)
    {
        var id = req["id"]?.GetValue<string>() ?? throw new AdminError("id required");
        return Config.Find(id) ?? throw new AdminError($"no config '{id}'");
    }

    private static string? PlayerNote(JsonObject req) =>
        req["name"]?.GetValue<string>()?.Trim() is { Length: > 0 and <= 64 } n ? n : null;

    private static string Node(JsonObject req) => req["node"]?.GetValue<string>() is { Length: > 0 } n ? n : throw new AdminError("node required");

    private static ulong SteamId(JsonObject req)
    {
        var n = req["steamId"];
        if (n is JsonValue v && (v.TryGetValue<ulong>(out var u) || (v.TryGetValue<string>(out var s) && ulong.TryParse(s, out u))) && u != 0)
            return u;
        throw new AdminError("steamId must be a SteamID64");
    }

    private static PermissionSubject Subject(JsonObject req) =>
        req["steamId"] is not null ? PermissionSubject.ForSteamId(SteamId(req))
        : req["entity"] is not null ? PermissionSubject.ForPlayer(RequirePlayer(req))
        : throw new AdminError("steamId or entity required");
}
