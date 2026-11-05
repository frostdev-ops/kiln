// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Creature and NPC ops over Kiln.Creatures (docs/kiln/services/creatures.md): status, catalog, nearby
/// creatures, inspect, per-creature values and AI settings, reset, spawn, variants, commands. Changes are layers of
/// Kiln.Admin (ops run inside its tick handler), so they revert when Kiln.Admin unloads. Field names and value parsing
/// follow the `creature` console command.</summary>
internal static partial class Ops
{
    private static readonly string[] CreatureFields =
        ["level", "faction", "scale", "xp", "speed", "damage", "home", "loot-labels", "loot-slots", "loot-off", "feature-on", "feature-off", "arsenal"];

    /// <summary>Registers the creature ops when Kiln.Creatures is on this server (false otherwise).</summary>
    internal static bool RegisterCreaturesSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterCreatures(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"creature ops off: Kiln.Creatures is not available ({e.GetType().Name})");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterCreatures(Bridge bridge, Logger log)
    {
        if (!Creatures.Available) return false;
        bridge.Register("creature_status", _ => CreatureStatusJson());
        // {filter?, kind?: enemy|wildlife|animal|villager}
        bridge.Register("creature_catalog", req =>
        {
            var filter = Str(req, "filter");
            CreatureKind? kind = Str(req, "kind") is { Length: > 0 } k
                ? Enum.TryParse<CreatureKind>(k, true, out var kk) ? kk : throw new AdminError("kind: enemy, wildlife, animal or villager")
                : null;
            var list = Creatures.Templates.Where(t => (kind is null || t.Kind == kind) &&
                (filter is null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || t.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
            return new JsonArray([.. list.Select(t => (JsonNode)new JsonObject
            {
                ["guid"] = t.Id.ToString(), ["name"] = t.Name, ["display"] = t.DisplayName, ["kind"] = t.Kind.ToString().ToLowerInvariant(),
                ["faction"] = t.Faction.ToString(), ["level"] = t.Level, ["levelMax"] = t.LevelMax, ["boss"] = t.IsBoss,
                ["arsenal"] = t.Arsenal == Guid.Empty ? null : t.Arsenal.ToString(), ["runtime"] = t.IsRuntime, ["loaded"] = t.IsLoaded,
            })]);
        });
        // {x, y, z, radius}
        bridge.Register("creature_near", req =>
        {
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var r = Number(req, "radius");
            if (r <= 0 || r > 2000) throw new AdminError("radius: 0 to 2000 m");
            return new JsonArray([.. Creatures.Near(at, r).Take(200).Select(e => (JsonNode?)CreatureJson(Creatures.Of(e).Inspect())).Where(n => n is not null)]);
        });
        bridge.Register("creature_inspect", req => CreatureJson(Creatures.Of(new EntityId(Entity(req))).Inspect()) ?? throw new AdminError("not a creature"));
        // {entity, field, value}: the console command's fields, resist:<kind>
        bridge.Register("creature_set", req =>
        {
            var e = new EntityId(Entity(req));
            var cr = Creatures.Of(e);
            if (!cr.Exists) throw new AdminError("not a creature");
            var field = (Str(req, "field") ?? throw new AdminError("field required")).ToLowerInvariant();
            var value = ValueText(req["value"]) ?? throw new AdminError("value required");
            try
            {
                switch (field)
                {
                    case "level": cr.Level = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "faction": cr.Faction = Enum.TryParse<Faction>(value, true, out var f) ? f : throw new AdminError($"unknown faction '{value}'"); break;
                    case "scale": cr.Scale = F(value); break;
                    case "xp": cr.XpFactor = F(value); break;
                    case "speed": cr.SpeedFactor = F(value); break;
                    case "damage": cr.DamageFactor = F(value); break;
                    case "home": cr.Home = Position(value); break;
                    case "loot-labels": cr.Loot.Labels = Guid.Parse(value); break;
                    case "loot-slots":
                    {
                        var p = value.Split([' ', ',', '-'], StringSplitOptions.RemoveEmptyEntries);
                        cr.Loot.Slots = (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[^1], CultureInfo.InvariantCulture));
                        break;
                    }
                    case "loot-off": cr.Loot.Disabled = value is "1" or "true" or "on" or "yes"; break;
                    case "feature-on": cr.Features.Enable(EnemyFeatureSet(value)); break;
                    case "feature-off": cr.Features.Disable(EnemyFeatureSet(value)); break;
                    case "arsenal": cr.Arsenal = Guid.Parse(value); break;
                    default:
                        if (field.StartsWith("resist:", StringComparison.Ordinal) && Enum.TryParse<DamageKind>(field[7..], true, out var k))
                        {
                            cr.Resistance[k] = F(value);
                            break;
                        }
                        throw new AdminError($"unknown field '{field}' ({string.Join(", ", CreatureFields)}, resist:<kind>)");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException or KeyNotFoundException or IndexOutOfRangeException or OverflowException)
            {
                throw new AdminError(ex.Message);
            }
            log.Info($"dashboard creature {e}: {field} = {value}");
            return CreatureJson(cr.Inspect());
        });
        // {entity, path, value?}: AI settings by reflection path ("Enemy01BehaviorState.aggroRange"); value sets
        bridge.Register("creature_ai", req =>
        {
            var cr = Creatures.Of(new EntityId(Entity(req)));
            if (!cr.Exists) throw new AdminError("not a creature");
            var path = Str(req, "path") ?? throw new AdminError("path required");
            try
            {
                if (ValueText(req["value"]) is { } v) cr.Ai.Set(path, v);
                var cur = cr.Ai.Get(path, out var isPrivate);
                return new JsonObject { ["path"] = path, ["value"] = cur, ["private"] = isPrivate };
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException or KeyNotFoundException or InvalidOperationException)
            {
                throw new AdminError(ex.Message);
            }
        });
        bridge.Register("creature_reset", req =>
        {
            var cr = Creatures.Of(new EntityId(Entity(req)));
            if (!cr.Exists) throw new AdminError("not a creature");
            return new JsonObject { ["reset"] = cr.Reset() };
        });
        // {what: variant id | template name/GUID, x, y, z, count?}; tagged "admin", kept when Kiln.Admin unloads
        bridge.RegisterAsync("creature_spawn", async req =>
        {
            var what = Str(req, "what") ?? throw new AdminError("what required (a variant id or template name)");
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var count = req["count"] is null ? 1 : (int)Number(req, "count");
            if (count is < 1 or > 50) throw new AdminError("count: 1 to 50");
            var ids = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                try
                {
                    var e = await Creatures.Spawn(what, at, 0, new CreatureSpawnOptions { Tag = "admin", DespawnOnUnload = false });
                    ids.Add(e.Value);
                }
                catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or NotSupportedException)
                {
                    throw new AdminError(ex.Message);
                }
            }
            log.Info($"dashboard spawned {count} x {what} at {at}");
            return new JsonObject { ["what"] = what, ["entities"] = ids };
        });
        bridge.Register("creature_variants", _ => new JsonObject
        {
            ["variants"] = new JsonArray([.. CreatureVariants.All.Select(v => (JsonNode)new JsonObject
            {
                ["id"] = v.Id, ["base"] = v.Base, ["name"] = v.Name, ["level"] = v.Level, ["faction"] = v.Faction?.ToString(),
                ["scale"] = v.Scale, ["xp"] = v.XpFactor, ["speed"] = v.SpeedFactor, ["damage"] = v.DamageFactor,
                ["resistances"] = new JsonObject(v.Resistances.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key.ToString(), kv.Value))),
                ["lootLabels"] = v.LootLabels?.ToString(), ["lootDisabled"] = v.LootDisabled,
                ["enable"] = v.EnableFeatures.ToString(), ["disable"] = v.DisableFeatures.ToString(),
                ["ai"] = new JsonObject(v.Ai.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))), ["persist"] = v.Persist,
            })]),
            ["spawnRules"] = new JsonArray([.. CreatureVariants.SpawnRules.Select(r => (JsonNode)new JsonObject
            {
                ["template"] = r.Template, ["variant"] = r.Variant, ["chance"] = r.Chance, ["nightOnly"] = r.NightOnly, ["dayOnly"] = r.DayOnly,
            })]),
        });
        // {entity, action: attack|guard|stopguard|stay|heal|moveto, target?, x?, y?, z?}
        bridge.Register("creature_command", req =>
        {
            var e = new EntityId(Entity(req));
            if (!Creatures.Of(e).Exists) throw new AdminError("not a creature");
            var cmd = Creatures.Of(e).Command;
            var action = Str(req, "action") ?? throw new AdminError("action required");
            EntityId Target() => new(Entity(req, "target"));
            try
            {
                switch (action)
                {
                    case "attack": cmd.Attack(Target()); break;
                    case "guard": cmd.Guard(Target()); break;
                    case "stopguard": cmd.StopGuarding(Target()); break;
                    case "stay": cmd.Stay(); break;
                    case "heal": cmd.Heal(); break;
                    case "moveto": cmd.MoveTo(new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"))); break;
                    default: throw new AdminError("action: attack, guard, stopguard, stay, heal or moveto");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or KeyNotFoundException)
            {
                throw new AdminError(ex.Message);
            }
            log.Info($"dashboard creature {e}: {action}");
            return new JsonObject { ["entity"] = e.Value, ["action"] = action, ["sent"] = true };
        });
        return true;
    }

    private static JsonObject? CreatureStatusJson()
    {
        if (Creatures.Status is not { } s) return null;
        return new JsonObject
        {
            ["slots"] = s.Slots, ["aiHooks"] = s.AiHooks, ["readyBit"] = s.ReadyBit, ["arsenals"] = s.Arsenals, ["reflection"] = s.Reflection,
            ["commands"] = s.Commands, ["marks"] = s.Marks, ["labReady"] = s.LabReady, ["catalog"] = s.Catalog, ["catalogLoaded"] = s.CatalogLoaded,
            ["engineCreaturesMissing"] = s.EngineCreaturesMissing, ["arsenalCount"] = s.ArsenalCount, ["entities"] = s.Entities,
            ["layers"] = s.Layers, ["privateCopies"] = s.PrivateCopies, ["pendingFree"] = s.PendingFree, ["stateChanges"] = s.StateChanges,
            ["eventsDelivered"] = s.EventsDelivered, ["eventsDropped"] = s.EventsDropped, ["commandsSent"] = s.CommandsSent,
            ["marksWritten"] = s.MarksWritten, ["copyReads"] = s.CopyReads,
        };
    }

    private static JsonObject? CreatureJson(CreatureInfo? i) => i is null ? null : new JsonObject
    {
        ["entity"] = i.Entity.Value, ["template"] = i.Template.ToString(), ["templateName"] = i.TemplateName,
        ["kind"] = i.Kind.ToString().ToLowerInvariant(), ["hasAi"] = i.HasAi, ["state"] = i.State.ToString(),
        ["awarenessState"] = i.AwarenessState, ["awareness"] = Math.Round(i.Awareness, 3), ["faction"] = i.Faction.ToString(),
        ["level"] = i.Level, ["levelMax"] = i.LevelMax, ["health"] = i.Health, ["healthMax"] = i.HealthMax,
        ["scale"] = Math.Round(i.Scale, 3), ["xp"] = Math.Round(i.XpFactor, 3), ["speed"] = Math.Round(i.SpeedFactor, 3),
        ["damage"] = Math.Round(i.DamageMultiplier, 3),
        ["features"] = new JsonObject
        {
            ["template"] = i.TemplateFeatures.ToString(), ["enabled"] = i.EnabledFeatures.ToString(), ["disabled"] = i.DisabledFeatures.ToString(),
        },
        ["target"] = i.Target.IsNone ? null : i.Target.Value, ["guard"] = i.Guard.IsNone ? null : i.Guard.Value, ["home"] = Pos(i.Home),
        ["loot"] = new JsonObject
        {
            ["labels"] = i.LootLabels == Guid.Empty ? null : i.LootLabels.ToString(), ["min"] = i.LootSlotsMin, ["max"] = i.LootSlotsMax,
            ["disabled"] = i.LootDisabled,
        },
        ["dead"] = i.Dead, ["ready"] = i.Ready, ["forcedReady"] = i.ForcedReady, ["layers"] = i.Layers, ["privateCopies"] = i.PrivateCopies,
    };

    private static string? Str(JsonObject req, string key) => req[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A JSON value as the console command's text: numbers invariant, booleans true/false, {x,y,z} and [a,b]
    /// as space-separated numbers.</summary>
    private static string? ValueText(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Trim(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("R", CultureInfo.InvariantCulture),
        JsonObject o when o["x"] is not null => string.Join(' ', new[] { "x", "y", "z" }.Select(k => o[k]?.GetValue<double>().ToString("R", CultureInfo.InvariantCulture) ?? "0")),
        JsonArray a => string.Join(' ', a.Select(x => x is JsonValue xv && xv.TryGetValue<double>(out var d) ? d.ToString("R", CultureInfo.InvariantCulture) : x?.ToString())),
        _ => n.ToJsonString(),
    };

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private static WorldPosition Position(string s)
    {
        var p = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) throw new AdminError("home: {x, y, z} or \"x y z\"");
        return new WorldPosition(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture),
            double.Parse(p[2], CultureInfo.InvariantCulture));
    }

    private static EnemyFeatures EnemyFeatureSet(string s)
    {
        EnemyFeatures r = 0;
        foreach (var n in s.Split([' ', ',', '|'], StringSplitOptions.RemoveEmptyEntries))
            r |= Enum.TryParse<EnemyFeatures>(n, true, out var f) ? f : throw new AdminError($"unknown feature '{n}'");
        if (r == 0) throw new AdminError("give feature names (Move, Combat, Patrol, ...)");
        return r;
    }
}
