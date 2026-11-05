// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Protection zones from the dashboard (kiln.gameplay). Zones are kept in kiln/data/kiln-admin/zones.json and
/// re-added when Kiln.Admin loads, so they survive restarts and reloads.</summary>
internal static partial class Ops
{
    private sealed record SavedZone(string Name, double X, double Y, double Z, double Radius, string[] Deny, ulong[] Owners,
        bool AllowAdmins);

    private static readonly Dictionary<string, (SavedZone Saved, ZoneHandle Handle)> AdminZones = new(StringComparer.OrdinalIgnoreCase);
    private static string ZonesFile => Path.Combine(DataDirectory, "zones.json");

    static partial void RegisterGameplay(Bridge bridge, Logger log)
    {
        if (!Gameplay.Available) return;
        LoadZones(log);
        bridge.Register("gameplay_status", _ => GameplayState());
        bridge.Register("zones_list", _ => ZonesJson());
        // {name, x, y, z, radius, deny?: ["build","break","terraform","interact","loot","craft","spawn"], owners?: [steamId], allowAdmins?}
        bridge.Register("zones_add", req =>
        {
            var name = req["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 64) throw new AdminError("name required (up to 64 characters)");
            var radius = Number(req, "radius");
            if (radius is < 1 or > 2000) throw new AdminError("radius must be 1 to 2000 metres");
            var deny = req["deny"] is JsonArray d ? d.Select(n => n?.GetValue<string>() ?? "").ToArray()
                : ["build", "break", "terraform", "interact", "loot"];
            var owners = req["owners"] is JsonArray o ? o.Select(n => ParseSteamId(n)).ToArray() : [];
            var saved = new SavedZone(name, Number(req, "x"), Number(req, "y"), Number(req, "z"), radius, deny, owners,
                req["allowAdmins"]?.GetValue<bool>() ?? true);
            var zone = ToZone(saved);
            if (AdminZones.TryGetValue(name, out var existing))
            {
                existing.Handle.Update(zone);
                AdminZones[name] = (saved, existing.Handle);
            }
            else AdminZones[name] = (saved, Gameplay.Protect(zone));
            SaveZones();
            log.Info($"zone '{name}' set: radius {radius} m at ({saved.X:F0}, {saved.Y:F0}, {saved.Z:F0}), deny {string.Join(",", deny)}, {owners.Length} owner(s)");
            return ZonesJson();
        });
        bridge.Register("zones_remove", req =>
        {
            var name = req["name"]?.GetValue<string>() ?? throw new AdminError("name required");
            if (!AdminZones.Remove(name, out var z)) throw new AdminError($"no zone named '{name}'");
            z.Handle.Dispose();
            SaveZones();
            log.Info($"zone '{name}' removed");
            return ZonesJson();
        });
    }

    private static ProtectionZone ToZone(SavedZone s)
    {
        var deny = GameplayActions.None;
        foreach (var a in s.Deny)
            deny |= Enum.TryParse<GameplayActions>(a, ignoreCase: true, out var v) && v != GameplayActions.None
                ? v : throw new AdminError($"unknown action '{a}' (build, break, terraform, interact, loot, craft, spawn)");
        return new ProtectionZone
        {
            Name = s.Name, Center = new WorldPosition(s.X, s.Y, s.Z), Radius = s.Radius, Shape = ZoneShape.Sphere,
            Deny = deny, AllowedSteamIds = s.Owners, AllowAdmins = s.AllowAdmins,
        };
    }

    private static ulong ParseSteamId(JsonNode? n)
    {
        if (n is JsonValue v && (v.TryGetValue<ulong>(out var u) || (v.TryGetValue<string>(out var s) && ulong.TryParse(s, out u))) &&
            u > 76561197960265728UL)
            return u;
        throw new AdminError("owners must be SteamID64s");
    }

    private static void LoadZones(Logger log)
    {
        if (!File.Exists(ZonesFile)) return;
        try
        {
            foreach (var s in JsonSerializer.Deserialize<SavedZone[]>(File.ReadAllText(ZonesFile)) ?? [])
                AdminZones[s.Name] = (s, Gameplay.Protect(ToZone(s)));
            log.Info($"{AdminZones.Count} protection zone(s) restored from {ZonesFile}");
        }
        catch (Exception e)
        {
            log.Error(e, $"could not restore zones from {ZonesFile}");
        }
    }

    private static void SaveZones()
    {
        Directory.CreateDirectory(DataDirectory);
        var tmp = ZonesFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(AdminZones.Values.Select(z => z.Saved).ToArray(),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, ZonesFile, overwrite: true);
    }

    private static JsonArray ZonesJson() => new([.. AdminZones.Values.Select(z => (JsonNode)new JsonObject
    {
        ["name"] = z.Saved.Name, ["id"] = z.Handle.Id, ["active"] = z.Handle.Active,
        ["pos"] = Pos(new WorldPosition(z.Saved.X, z.Saved.Y, z.Saved.Z)), ["radius"] = z.Saved.Radius,
        ["deny"] = new JsonArray([.. z.Saved.Deny.Select(a => (JsonNode)a)]),
        ["owners"] = new JsonArray([.. z.Saved.Owners.Select(o => (JsonNode)o.ToString())]), ["allowAdmins"] = z.Saved.AllowAdmins,
    })]);

    private static JsonObject GameplayState()
    {
        var s = Gameplay.Status;
        return s is null ? new JsonObject { ["available"] = false } : new JsonObject
        {
            ["available"] = true, ["intercept"] = s.Intercept, ["craftHook"] = s.CraftHook, ["buildHooks"] = s.BuildHooks,
            ["terraformHook"] = s.TerraformHook, ["spawnHook"] = s.SpawnHook, ["zones"] = s.Zones, ["denied"] = s.Denied,
            ["canceled"] = s.Canceled,
        };
    }
}
