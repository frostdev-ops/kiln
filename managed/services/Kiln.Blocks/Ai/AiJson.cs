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
        if (j.Count == 0 || j.ContainsKey("type")) return null;
        var key = j.Select(p => p.Key).FirstOrDefault(k => k is "inCombat" or "state" or "hasTarget" or "targetIsPlayer" or "targetHealthBelow" or
            "hitWithin" or "chance" or "targetAtHome" or "enemiesNear" or "alliesNear" or "summonsBelow" or "not");
        if (key == null) return null;
        var v = j[key];
        try
        {
            Fields(j, key);
            return key switch
            {
                "not" => v is JsonObject inner
                    ? NpcRules.Condition(new RuleCondition(new JsonObject { [NpcRules.Pending] = inner.DeepClone() }), mod, ctx, $"{path}.not").Not()
                    : throw new JsonException("not needs a condition object"),
                "inCombat" => Bool(v) ? Is.InCombat : Is.InCombat.Not(),
                "state" => Is.InState(Strings(v).Select(s => State(s)).ToArray()),
                "hasTarget" => Bool(v) ? Is.HasTarget : Is.HasTarget.Not(),
                "targetIsPlayer" => Is.TargetIsPlayer(Bool(v)),
                "targetHealthBelow" => Is.TargetHealthBelow(Num(v)),
                "hitWithin" => Is.HitWithin(Num(v)),
                "chance" => Is.Chance(Num(v)),
                "targetAtHome" => Bool(v) ? Is.TargetAtHome : Is.TargetAtHome.Not(),
                "enemiesNear" => Near(v, (c, m) => Is.EnemiesNear(c, m)),
                "alliesNear" => Near(v, (c, m) => Is.AlliesNear(c, m)),
                "summonsBelow" => Is.SummonsBelow(Int(v)),
                _ => null,
            };
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or JsonException or OverflowException)
        {
            throw new BlockError(ctx.Block, $"{path}.{key}", e.Message);
        }
    }

    /// <summary>One of this file's actions, or null.</summary>
    internal static RuleAction? Action(JsonObject j, string mod, BlockContext ctx, string path)
    {
        if (j.Count == 0 || j.ContainsKey("type")) return null;
        var key = j.Select(p => p.Key).FirstOrDefault(k => k is "flee" or "backOff" or "play" or "immune" or "pause" or "enableFeature" or
            "disableFeature" or "heal" or "summon" or "cast" or "apply");
        if (key == null) return null;
        var v = j[key];
        try
        {
            Fields(j, key, key == "play" ? "wait" : key == "apply" ? "on" : null);
            switch (key)
            {
                case "flee": if (!Bool(v)) throw new ArgumentException("flee must be true"); return Act.Flee();
                case "backOff": return Pair(v, (a, b) => Act.BackOff(a, b));
                case "play": return Act.Play(Guid.Parse(Text(v)), j["wait"]?.GetValue<bool>() ?? false);
                case "immune": return Act.Immune(Bool(v));
                case "pause": return Pair(v, (a, b) => Act.Pause(a, b));
                case "enableFeature": return Act.EnableFeature(Feature(Text(v)));
                case "disableFeature": return Act.DisableFeature(Feature(Text(v)));
                case "heal": return Act.Heal(Num(v));
                case "summon": return Summon(v ?? throw new JsonException("a summon needs a creature"), mod);
                case "cast": return AiLowering.CastFromJson(v ?? throw new JsonException("cast needs a delivery"), ctx, path);
                case "apply":
                {
                    var status = (v ?? throw new JsonException("apply needs a status")).Deserialize<StatusApply>(BlockJsonAccess.Options) ?? throw new JsonException("\"apply\" names a status");
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
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or JsonException or KeyNotFoundException or OverflowException)
        {
            throw new BlockError(ctx.Block, $"{path}.{key}", e.Message);
        }
    }

    // "summon": "imp" or { "creature": "imp", "count": 2, "radius": 3, "seconds": 30, "side": "own" }
    private static RuleAction Summon(JsonNode v, string mod)
    {
        if (v is JsonValue) return AiRuntime.SummonAction(AiRulesApi.SummonSpec(Text(v), mod, 1, 3, 0, SummonSide.Summoner));
        var o = v as JsonObject ?? throw new JsonException("\"summon\" is a creature's name or { \"creature\": ... }");
        foreach (var p in o)
        {
            if (p.Key is not ("creature" or "count" or "radius" or "seconds" or "side"))
                throw new JsonException($"\"{p.Key}\" is not a field of a summon: creature, count, radius, seconds, side");
            if (p.Value == null) throw new JsonException($"{p.Key} cannot be null");
        }
        var creature = o["creature"]?.GetValue<string>() ?? throw new JsonException("a summon needs \"creature\"");
        var count = o["count"]?.GetValue<int>() ?? 1;
        if (count is < 1 or > 16) throw new ArgumentException($"count {count} is outside 1..16");
        var side = SummonSide.Summoner;
        if (o["side"] is { } sideNode)
        {
            var sideName = Text(sideNode);
            if (!Enum.TryParse(sideName, true, out side) || !Enum.IsDefined(side) || !string.Equals(Enum.GetName(side), sideName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{sideName}: expected summoner or own");
        }
        return AiRuntime.SummonAction(AiRulesApi.SummonSpec(creature, mod, count, o["radius"] is { } r ? Num(r) : 3,
            o["seconds"] is { } sec ? Num(sec) : 0, side));
    }

    private static bool Bool(JsonNode? v) => v?.GetValue<bool>() ?? throw new JsonException("a boolean is needed");
    private static double Num(JsonNode? v) => v?.GetValue<double>() ?? throw new JsonException("a number is needed");

    private static IEnumerable<string> Strings(JsonNode? v) =>
        v is JsonArray a ? a.Select(Text) : v is JsonValue ? [Text(v)] : throw new JsonException("a name or a list of names");

    private static T Pair<T>(JsonNode? v, Func<double, double, T> make) =>
        v is JsonArray { Count: 2 } a ? make(Num(a[0]), Num(a[1])) : throw new JsonException("two numbers: [min, max]");

    // [count, metres] or { "count": 2, "within": 10 }
    private static RuleCondition Near(JsonNode? v, Func<int, double, RuleCondition> make) => v switch
    {
        JsonArray { Count: 2 } a => make(Int(a[0]), Num(a[1])),
        JsonObject o => NearObject(o, make),
        _ => throw new JsonException("[count, metres] or { \"count\": n, \"within\": metres }"),
    };
    private static int Int(JsonNode? v) => v?.GetValue<int>() ?? throw new JsonException("an integer is needed");
    private static string Text(JsonNode? v) => v?.GetValue<string>() ?? throw new JsonException("a name is needed");

    private static EnemyState State(string s) => Enum.TryParse<EnemyState>(s, true, out var state) && Enum.IsDefined(state) && state != EnemyState.Invalid && string.Equals(Enum.GetName(state), s, StringComparison.OrdinalIgnoreCase)
        ? state : throw new ArgumentException($"{s}: unknown enemy state");

    private static void Fields(JsonObject j, string key, string? extra = null)
    {
        foreach (var p in j)
        {
            if (p.Key != key && p.Key != extra) throw new JsonException($"{p.Key}: use one kind per condition or action; {key} accepts only {key}" + (extra == null ? "" : $" and {extra}"));
            if (p.Value == null) throw new JsonException($"{p.Key} cannot be null");
        }
    }

    private static EnemyFeatures Feature(string name)
    {
        if (!Enum.GetNames<EnemyFeatures>().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"{name}: unknown AI feature name");
        return Enum.Parse<EnemyFeatures>(name, true);
    }

    private static RuleCondition NearObject(JsonObject o, Func<int, double, RuleCondition> make)
    {
        foreach (var p in o)
        {
            if (p.Key is not ("count" or "within")) throw new JsonException($"{p.Key}: expected count or within");
            if (p.Value == null) throw new JsonException($"{p.Key} cannot be null");
        }
        return make(o["count"] is { } count ? Int(count) : 1, Num(o["within"]));
    }

}
