// SPDX-License-Identifier: MIT
// What an AIBlock's newer fields and the rule kinds of RuleKinds.cs become when a creature block compiles: AI values on each creature (or its
// template), feature switches, a flee rule in its arsenal, a cast's delivery and launcher. Called from NpcCompiler at four marked places.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class AiLowering
{
    private static string N(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The AI values the fields stand for, <c>Enemy01BehaviorState</c> paths. <see cref="AIBlock.Tune"/> comes after and wins.</summary>
    internal static void Values(AIBlock ai, IDictionary<string, string> v)
    {
        if (ai.NightSight is { } ns) v["combatCommitmentSetup.view.nightRangeFactor"] = N(ns);
        if (ai.Hears is { } h) v["noiseListenerSetup.isEnabled"] = h ? "true" : "false";
        if (ai.Hearing is { } hr) v["noiseListenerSetup.listenRangeFactor"] = N(hr);
        if (ai.Threat is { } t) v["threatFactor"] = N(t);
        if (ai.HelpRadius is { } hrad) v["callForHelpSetup.radius"] = N(hrad);
        if (ai.Roam is { } r)
        {
            if (r.Min is { } a) v["roamingSetup.minRadius"] = N(a);
            if (r.Max is { } b) v["roamingSetup.maxRadius"] = N(b);
            if (r.FromHome is { } c) v["roamingSetup.maxHomeZoneDistance"] = N(c);
        }
        if (ai.Flee is { } f)
        {
            if (f.Min is { } a) v["fleeSetup.minFleeRadius"] = N(a);
            if (f.Max is { } b) v["fleeSetup.maxFleeRadius"] = N(b);
            if (f.Seconds is { } s) v["fleeSetup.maxFleeDuration"] = N(s);
        }
    }

    /// <summary>The game's features the block turns on.</summary>
    internal static EnemyFeatures Enabled(AIBlock ai)
    {
        var on = ai.Enable ?? EnemyFeatures.None;
        if (ai.Roam != null) on |= EnemyFeatures.Roaming;
        if (ai.CallsForHelp == true) on |= EnemyFeatures.CallForHelp;
        return on;
    }

    /// <summary>The game's features the block turns off.</summary>
    internal static EnemyFeatures Disabled(AIBlock ai)
    {
        var off = ai.Disable ?? EnemyFeatures.None;
        if (ai.ReturnsHome == false) off |= EnemyFeatures.ReturnHome | EnemyFeatures.FleeHome;
        if (ai.CallsForHelp == false) off |= EnemyFeatures.CallForHelp;
        return off;
    }

    /// <summary>Checks the fields, by block and field.</summary>
    internal static void Check(BlockContext ctx, AIBlock a, string path)
    {
        string P(string f) => string.IsNullOrEmpty(path) ? f : $"{path}.{f}";
        void Range(double? v, double lo, double hi, string field)
        {
            if (v is { } x && (x < lo || x > hi || !double.IsFinite(x))) throw new BlockError(ctx.Block, P(field), $"{x} is outside {N(lo)}..{N(hi)}");
        }
        Range(a.NightSight, 0, 4, "nightSight");
        Range(a.Hearing, 0, 10, "hearing");
        Range(a.Threat, 0, 100, "threat");
        Range(a.HelpRadius, 0, 200, "helpRadius");
        Range(a.SenseEvery, 0.05, 60, "senseEvery");
        if (a.Roam is { } r)
        {
            Range(r.Min, 0, 500, "roam.min");
            Range(r.Max, 0, 500, "roam.max");
            Range(r.FromHome, 0, 1000, "roam.fromHome");
            if (r.Min > r.Max) throw new BlockError(ctx.Block, P("roam"), $"min {r.Min} is more than max {r.Max}");
        }
        if (a.Flee is { } f)
        {
            Range(f.Below, 0, 1, "flee.below");
            Range(f.Min, 0, 500, "flee.min");
            Range(f.Max, 0, 500, "flee.max");
            Range(f.Seconds, 0, 600, "flee.seconds");
            Range(f.Cooldown, 0, 3600, "flee.cooldown");
            if (f.Min > f.Max) throw new BlockError(ctx.Block, P("flee"), $"min {f.Min} is more than max {f.Max}");
        }
        if (a.Enable is { } en && a.Disable is { } dis && (en & dis) != 0)
            throw new BlockError(ctx.Block, P("enable"), $"{en & dis} is both turned on and off");
        foreach (var (k, rel) in a.Relations ?? new Dictionary<Faction, Relation>())
        {
            if (k == Faction.None) throw new BlockError(ctx.Block, P("relations"), "Faction.None is not a faction");
            if (!Enum.IsDefined(rel)) throw new BlockError(ctx.Block, P($"relations[{k}]"), $"{(int)rel} is not a relation: game, hostile, friendly or neutral");
        }
        if (a.SenseFilter != null && a.SenseFilterName != null) throw new BlockError(ctx.Block, P("senseFilter"), "a sense filter from code or by name, not both");
        if (a.SenseFilterName is { } n && NpcRuntime.Lookup(AiRuntime.Filters, n) == null)
            throw new BlockError(ctx.Block, P("senseFilter"), $"no sense filter \"{n}\": register it with Blocks.SenseFilter(\"{n}\", (npc, other) => ...) in OnLoad");
        if ((a.Relations is { Count: > 0 } || a.SenseFilter != null || a.SenseFilterName != null) && !AiNative.Loaded && Lifecycle.IsServerRunning)
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {ctx.Mod.Id}: {ctx.Block}: {P("relations")}: kiln.npcai is not loaded on this build: the creature fights as the game decides");
    }

    /// <summary>Rules the AI block adds to the creature's arsenal: running away below a share of its health.</summary>
    internal static List<ArsenalRule> Rules(AIBlock ai, string name)
    {
        var list = new List<ArsenalRule>();
        if (ai.Flee is { Below: { } below } f)
            list.Add(new ArsenalRule
            {
                Name = $"{name}.flee", Every = 0.5, Cooldown = f.Cooldown,
                If = [Is.InCombat, Is.HealthBelow(below)],
                Do = [Act.Flee()],
            });
        return list;
    }

    // ---- casts: a placeholder until the creature compiles ----

    private static readonly object Gate = new();
    private static readonly Dictionary<int, IDeliveryBlock> PendingCasts = new();
    private static int _next = 1;

    internal static RuleAction CastAction(IDeliveryBlock d)
    {
        lock (Gate)
        {
            var id = _next++;
            PendingCasts[id] = d;
            return new RuleAction(new JsonObject { ["blocksCast"] = id });
        }
    }

    /// <summary>A rule's action as the creature compiles it: a cast becomes its delivery, a launcher program and a queued run of it; anything else
    /// comes back null.</summary>
    internal static RuleAction? Lower(BlockContext ctx, Lowered low, string mod, string key, string name, RuleAction act, string path, NpcCreature info)
    {
        if (act.Json["blocksCast"]?.GetValue<int>() is not { } id) return null;
        IDeliveryBlock? d;
        lock (Gate) PendingCasts.TryGetValue(id, out d);
        if (d == null) throw new BlockError(ctx.Block, path, "a cast that is not one of this process's: write it as Act.Cast(...) in the rule");
        var tag = path.Replace('[', '.').Replace("]", "");
        var ckey = $"{key}.{tag}.cast";
        var lowered = Compiler.LowerDelivery(ctx, low, ckey, $"{name}.{tag}", d, $"{path}.cast", primary: false);
        var (program, blob, listing) = EffectEmitter.LauncherProgram(ctx, $"{ckey}.launch", lowered.PlacedEntity, lowered.Speed);
        low.Program(program, $"{ckey}.launch", blob, listing);
        var creature = info;
        return Act.Custom(npc => AiRuntime.Cast(creature, program, npc));
    }

    /// <summary>A cast written in a data file: <c>{ "cast": { "type": "projectile", ... } }</c>.</summary>
    internal static RuleAction CastFromJson(JsonNode d, BlockContext ctx, string path)
    {
        IDeliveryBlock delivery;
        try { delivery = d.Deserialize<IDeliveryBlock>(BlockJsonAccess.Options)!; }
        catch (System.Text.Json.JsonException e) { throw new BlockError(ctx.Block, $"{path}.cast", e.Message); }
        return CastAction(delivery);
    }
}
