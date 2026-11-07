// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>The creative and rules ops (Kiln.Rules; docs/kiln/services/rules.md). Creative mode and its /creative command
/// are Kiln.Rules' own (<see cref="Creative"/>); these ops only call them, so remote tools can do what the command does.</summary>
internal static partial class Ops
{
    internal static bool RegisterRulesSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterRules(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"creative and rules ops off: Kiln.Rules is not available ({e.GetType().Name})");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterRules(Bridge bridge, Logger log)
    {
        // {entity, on?} -> creative mode on/off for a player (on omitted: toggles)
        bridge.Register("creative", req =>
        {
            var p = RequirePlayer(req);
            var on = req["on"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : !Creative.IsOn(p.Entity);
            var r = Creative.Set(p.Entity, on);
            if (!r.Ok) throw new AdminError(r.Message);
            return new JsonObject
            {
                ["entity"] = p.Entity.Value, ["name"] = p.Name, ["creative"] = r.On, ["client"] = Rules.ClientOf(p.Entity).ToString(),
                ["message"] = r.Message, ["rules"] = EffectiveJson(Rules.Of(p.Entity)),
            };
        });
        // {entity?} -> the rules hooks, presets, active rule sets, creative players and (with entity) one player's rules
        bridge.Register("rules", req =>
        {
            var s = Rules.Status();
            var o = new JsonObject
            {
                ["available"] = s is not null,
                ["hooks"] = s is null ? null : new JsonObject { ["flight"] = s.Flight, ["buildingZone"] = s.BuildingZone, ["placement"] = s.Placement, ["costs"] = s.Costs, ["mining"] = s.Mining, ["shroudTimer"] = s.ShroudTimer, ["gliding"] = s.Gliding },
                ["detail"] = s?.Detail,
                ["presets"] = new JsonArray([.. Rules.Presets.Select(n => (JsonNode)n)]),
                ["sets"] = new JsonArray([.. Rules.Active.Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Id, ["owner"] = r.Owner, ["label"] = r.Label, ["scope"] = r.Scope.ToString(),
                    ["expires"] = r.Expires?.ToString("o"), ["rules"] = JsonNode.Parse(r.Rules.ToJson()),
                })]),
                ["creative"] = new JsonArray([.. Creative.Players.Select(e => (JsonNode)e.Value)]),
            };
            if (req["entity"] is JsonValue) o["player"] = EffectiveJson(Rules.Of(RequirePlayer(req).Entity));
            return o;
        });
        // {entity, item (name or id), count?, level?, rarity?} -> items for a player in creative mode (the creative menu's Take)
        bridge.RegisterAsync("creative_give", async req =>
        {
            var p = RequirePlayer(req);
            var name = req["item"]?.ToString() ?? throw new AdminError("item required");
            if (req["count"] is not null && (req["count"] is not JsonValue cv || !cv.TryGetValue<int>(out _)))
                throw new AdminError("count must be an integer");
            if (req["level"] is not null && (req["level"] is not JsonValue lvj || !lvj.TryGetValue<int>(out _)))
                throw new AdminError("level must be an integer");
            ItemRarity? rarity = null;
            if (req["rarity"] is { } rarityNode)
            {
                if (rarityNode is not JsonValue rv || !rv.TryGetValue<string>(out var text) ||
                    !Enum.TryParse<ItemRarity>(text, true, out var parsed) || !Enum.IsDefined(parsed))
                    throw new AdminError("rarity is Common, Uncommon, Rare, Epic or Legendary");
                rarity = parsed;
            }
            var item = Items.Lookup(name) ?? throw new AdminError($"no item '{name}'");
            var count = req["count"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 1;
            int? level = req["level"] is JsonValue l && l.TryGetValue<int>(out var lv) ? lv : null;
            var r = await CreativeTools.Give(p.Entity, item.Id, count, level, rarity);
            if (!r.Ok) throw new AdminError(r.Message);
            return new JsonObject { ["entity"] = p.Entity.Value, ["item"] = item.Name, ["message"] = r.Message };
        });
        // {mode: "normal" | "stopped" | "fast"} -> the day and night cycle for the whole world (session)
        bridge.Register("creative_cycle", req =>
        {
            var mode = req["mode"]?.ToString() ?? throw new AdminError("mode required");
            if (!Enum.TryParse<TimeCycle>(mode, true, out var cycle) || !Enum.IsDefined(cycle)) throw new AdminError("mode is normal, stopped or fast");
            var r = CreativeTools.SetCycle(cycle, "a remote tool");
            if (!r.Ok) throw new AdminError(r.Message);
            return new JsonObject { ["cycle"] = Time.Cycle.ToString(), ["speed"] = Time.HeldSpeed, ["message"] = r.Message };
        });
        log.Info("creative and rules ops are on (/creative itself is Kiln.Rules')");
        return true;
    }

    private static JsonObject EffectiveJson(EffectiveRules e) => new()
    {
        ["summary"] = e.ToString(), ["flyAnywhere"] = e.FlyAnywhere, ["flightCarriesBody"] = e.FlightCarriesBody,
        ["buildOutsideBases"] = e.BuildOutsideBases, ["ignoreNoBuildZones"] = e.IgnoreNoBuildZones, ["ignoreEnemiesNearby"] = e.IgnoreEnemiesNearby,
        ["buildInShroud"] = e.BuildInShroud, ["ignoreBasePermissions"] = e.IgnoreBasePermissions, ["keepBuildsOutsideBases"] = e.KeepBuildsOutsideBases,
        ["buildCost"] = e.BuildCost, ["buildRefund"] = e.BuildRefund, ["digDamage"] = e.DigDamage, ["invulnerable"] = e.Invulnerable,
        ["minHealth"] = e.MinHealth, ["minStamina"] = e.MinStamina, ["minMana"] = e.MinMana, ["water"] = e.Water?.ToString(),
        ["denied"] = new JsonArray([.. e.Denied.Select(d => (JsonNode)d)]),
        ["from"] = new JsonArray([.. e.From.Select(r => (JsonNode)r.ToString())]),
    };
}
