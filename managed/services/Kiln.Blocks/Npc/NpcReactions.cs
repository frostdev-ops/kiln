// SPDX-License-Identifier: MIT
// A body of your own reacts and dies in its own states. The base creature's template names its hit reaction, stun, parry and death sequences, which set
// that creature's states: on a graph of your own those would hold it still. Each becomes a clone whose state events play the animation's states (Stagger
// for a hit, the engine's Dead for a death; an engine state the graph has stays), and the template names the clones. Done when content is ready: the
// sequences are the game's data, read from the running game, so a game update that changes them is followed.
using System.Text.Json.Nodes;

namespace Kiln;

internal static class NpcReactions
{
    // Template fields that name a sequence a creature plays when something happens to it, besides HitReactionBehavior's (all of them are taken).
    private static readonly (string Path, bool Death)[] Fields =
    [
        ("Actor.deathSequence", true),
        ("Enemy01BehaviorState.parrySetup.parriedSequence", false),
        ("Enemy01BehaviorState.parrySetup.parriedStunSequence", false),
        ("Enemy01BehaviorState.parrySetup.parriedStunLongDurationSequence", false),
        ("Enemy01BehaviorState.stunnedSetup.stunnedSequence", false),
    ];

    /// <summary>What a creature's reactions became: base sequence -> clone, for tests and <c>kiln blocks</c>.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, Guid>> Made = new(StringComparer.Ordinal);

    public static void Add(ModBlocks mod, string key, string name, string baseTemplate, string template, NpcCompiler.Graph graph)
    {
        if (!graph.States.TryGetValue("Stagger", out var stagger) || stagger.Guid is not { } staggerGuid)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: creature {name}: its graph has no Stagger state (cooked before Kiln made one): cook again, or its hit reactions hold it still");
            return;
        }
        var dead = AnimState.Engine.First(e => e.Name == "Dead").Guid!.Value;
        var known = graph.States.Values.Where(s => s.Guid != null).Select(s => s.Guid!.Value).ToHashSet();
        var done = false;
        BlocksBridge.OnReady(() => done || (done = Make(mod, key, name, baseTemplate, template, known, staggerGuid, dead)), () => !done);
    }

    private static bool Make(ModBlocks mod, string key, string name, string baseTemplate, string template, HashSet<Guid> known, Guid stagger, Guid dead)
    {
        if (Content.Templates.Get(baseTemplate) is not { } t) return false;
        var paths = new List<(string Path, bool Death)>();
        if (Value(t.GetJson("HitReactionBehavior")) is JsonObject hits)
            foreach (var (kind, v) in hits)
                if (v is JsonObject o && o.ContainsKey("hitSequence")) paths.Add(($"HitReactionBehavior.{kind}.hitSequence", false));
        paths.AddRange(Fields);
        var rules = new JsonObject();
        var clones = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (path, death) in paths)
        {
            string? text;
            try { text = t.Get(path).ToString(); }
            catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException) { continue; }
            if (!Guid.TryParse(text, out var seq) || seq == Guid.Empty) continue;
            if (!clones.TryGetValue($"{seq}:{death}", out var clone))
            {
                var s = Content.Sequences.Get(seq.ToString());
                if (s == null || Value(s.GetJson("events")) is not JsonArray events) continue;
                var fields = new JsonObject();
                var k = 0;
                foreach (var ev in events)
                {
                    if (ev is not JsonObject e || e["$type"]?.ToString() != "SetAnimationEvent") continue;
                    var state = Guid.TryParse(e["animationName"]?.ToString(), out var g) ? g : Guid.Empty;
                    // an engine state the graph has stays (its Dead, Stun, Fall); anything else of the base creature's plays this one's
                    if (!known.Contains(state)) fields[$"events[SetAnimationEvent#{k}].animationName"] = (death ? dead : stagger).ToString();
                    k++;
                }
                if (fields.Count == 0) continue;   // it plays only states the graph has
                clone = BlockIds.Derive(mod.Id, "sequence", $"{key}.react.{seq:N}.{(death ? "death" : "hit")}");
                var def = new JsonObject { ["guid"] = clone.ToString(), ["name"] = $"{mod.Id}.{name}.react.{clones.Count}", ["base"] = seq.ToString(), ["fields"] = fields };
                try { BlocksBridge.DefineClone(Content.Sequences, def, mod.Owner); }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
                {
                    problems.Add($"{path}: {e.Message}");
                    continue;
                }
                clones[$"{seq}:{death}"] = clone;
            }
            rules[path] = clone.ToString();
        }
        if (rules.Count > 0) BlockRuntime.AddRules(mod, "templates", template, rules);
        Made[$"{mod.Id}:{name}"] = clones.ToDictionary(x => x.Key, x => x.Value);
        foreach (var p in problems) BlocksBridge.Log(LogLevel.Error, $"blocks: {mod.Id}: creature {name}: a reaction sequence: {p}");
        BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: creature {name}: {clones.Count} hit, stun and death sequence(s) of {baseTemplate} play its own states, {rules.Count} field(s) of its template name them");
        return true;
    }

    private static JsonNode? Value(JsonNode? j) => j is JsonObject o && o.ContainsKey("value") && o.ContainsKey("path") ? o["value"] : j;
}
