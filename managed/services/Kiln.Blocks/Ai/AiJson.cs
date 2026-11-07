// SPDX-License-Identifier: MIT
// The JSON forms of RuleKinds.cs in blocks/*.json: a rule's "when" and "do" items. NpcRules (Npc/NpcJson.cs) asks here first; what is not one of
// these goes on to kiln.creatures' forms and the game's raw { "type": ... }.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class AiJson
{
    /// <summary>One of this file's conditions, or null.</summary>
    internal static RuleCondition? Condition(JsonObject j, string mod, BlockContext ctx, string path)
    {
        if (j.Count == 0) return null;
        var (key, v) = j.First();
        try
        {
            return key switch
            {
                "inCombat" => Bool(v) ? Is.InCombat : Is.InCombat.Not(),
                "state" => Is.InState(Strings(v).Select(s => Enum.Parse<EnemyState>(s, ignoreCase: true)).ToArray()),
                "hasTarget" => Bool(v) ? Is.HasTarget : Is.HasTarget.Not(),
                "targetIsPlayer" => Is.TargetIsPlayer(Bool(v)),
                "targetHealthBelow" => Is.TargetHealthBelow(Num(v)),
                "hitWithin" => Is.HitWithin(Num(v)),
                "chance" => Is.Chance(Num(v)),
                "targetAtHome" => Bool(v) ? Is.TargetAtHome : Is.TargetAtHome.Not(),
                "enemiesNear" => Near(v, (c, m) => Is.EnemiesNear(c, m)),
                "alliesNear" => Near(v, (c, m) => Is.AlliesNear(c, m)),
                "summonsBelow" => Is.SummonsBelow((int)Num(v)),
                _ => null,
            };
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or JsonException)
        {
            throw new BlockError(ctx.Block, $"{path}.{key}", e.Message);
        }
    }

    /// <summary>One of this file's actions, or null.</summary>
    internal static RuleAction? Action(JsonObject j, string mod, BlockContext ctx, string path)
    {
        if (j.Count == 0) return null;
        var (key, v) = j.First();
        try
        {
            switch (key)
            {
                case "flee": return Act.Flee();
                case "backOff": return Pair(v, (a, b) => Act.BackOff(a, b));
                case "play": return Act.Play(Guid.Parse(v!.GetValue<string>()), j["wait"]?.GetValue<bool>() ?? false);
                case "immune": return Act.Immune(Bool(v));
                case "pause": return Pair(v, (a, b) => Act.Pause(a, b));
                case "enableFeature": return Act.EnableFeature(Enum.Parse<EnemyFeatures>(v!.GetValue<string>(), ignoreCase: true));
                case "disableFeature": return Act.DisableFeature(Enum.Parse<EnemyFeatures>(v!.GetValue<string>(), ignoreCase: true));
                case "heal": return Act.Heal(Num(v));
                case "summon": return Summon(v!, mod);
                case "cast": return AiLowering.CastFromJson(v!, ctx, path);
                case "apply":
                {
                    var status = v.Deserialize<StatusApply>(BlockJsonAccess.Options) ?? throw new JsonException("\"apply\" names a status");
                    var on = j["on"]?.GetValue<string>() switch
                    {
                        null or "target" => NpcEffectOn.Target,
                        "self" => NpcEffectOn.Self,
                        var x => throw new ArgumentException($"\"{x}\": a status goes on the creature's \"target\" or on itself, \"self\""),
                    };
                    return Act.Apply(status, on);
                }
                default: return null;
            }
        }
        catch (BlockError) { throw; }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            throw new BlockError(ctx.Block, $"{path}.{key}", e.Message);
        }
    }

    // "summon": "imp" or { "creature": "imp", "count": 2, "radius": 3, "seconds": 30, "side": "own" }
    private static RuleAction Summon(JsonNode v, string mod)
    {
        if (v is JsonValue) return AiRuntime.SummonAction(new SummonSpec(v.GetValue<string>(), mod, 1, 3, 0, SummonSide.Summoner));
        var o = v as JsonObject ?? throw new JsonException("\"summon\" is a creature's name or { \"creature\": ... }");
        foreach (var p in o)
            if (p.Key is not ("creature" or "count" or "radius" or "seconds" or "side"))
                throw new JsonException($"\"{p.Key}\" is not a field of a summon: creature, count, radius, seconds, side");
        var creature = o["creature"]?.GetValue<string>() ?? throw new JsonException("a summon needs \"creature\"");
        var count = o["count"]?.GetValue<int>() ?? 1;
        if (count is < 1 or > 16) throw new ArgumentException($"count {count} is outside 1..16");
        var side = o["side"]?.GetValue<string>() is { } s ? Enum.Parse<SummonSide>(s, ignoreCase: true) : SummonSide.Summoner;
        return AiRuntime.SummonAction(new SummonSpec(creature, mod, count, AiRulesApi.Positive(o["radius"]?.GetValue<double>() ?? 3, "radius"),
            Math.Max(0, o["seconds"]?.GetValue<double>() ?? 0), side));
    }

    private static bool Bool(JsonNode? v) => v?.GetValue<bool>() ?? true;
    private static double Num(JsonNode? v) => v?.GetValue<double>() ?? throw new JsonException("a number is needed");

    private static IEnumerable<string> Strings(JsonNode? v) =>
        v is JsonArray a ? a.Select(x => x!.GetValue<string>()) : v is JsonValue ? [v.GetValue<string>()] : throw new JsonException("a name or a list of names");

    private static T Pair<T>(JsonNode? v, Func<double, double, T> make) =>
        v is JsonArray { Count: 2 } a ? make(a[0]!.GetValue<double>(), a[1]!.GetValue<double>()) : throw new JsonException("two numbers: [min, max]");

    // [count, metres] or { "count": 2, "within": 10 }
    private static RuleCondition Near(JsonNode? v, Func<int, double, RuleCondition> make) => v switch
    {
        JsonArray { Count: 2 } a => make(a[0]!.GetValue<int>(), a[1]!.GetValue<double>()),
        JsonObject o => make(o["count"]?.GetValue<int>() ?? 1, o["within"]?.GetValue<double>() ?? throw new JsonException("\"within\": metres")),
        _ => throw new JsonException("[count, metres] or { \"count\": n, \"within\": metres }"),
    };
}
