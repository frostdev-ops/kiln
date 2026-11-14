// SPDX-License-Identifier: MIT
// A weapon's follow-up routes, resolved: pure, on rows read from the game (or made up by a test).
//  - CheckOffline: what is wrong with the routes as written (limits, a slot the weapon's moves do not name, a link the chain lacks, two routes that
//    select the same windows whatever the game's data is).
//  - Resolve: each route against the ORIGINAL events of its move's motion. A window is a SetActionOportunity with a follow-up of its own; its key is
//    (slot, link, sub-sequence, its place among the SetActionOportunity events in the motion's own order). Occurrence counts the windows of the route's
//    trigger in that sub-sequence from 0. Two routes whose windows intersect are refused, both named, whatever they lead to: there is no last-wins.
//  - Plan: the whole generation (the clones to define, their rules, the item's slots, what to read back), or every problem. Nothing is partly planned.
// The generation's hash is over what it was made from: the authored moves and routes, the motions' rows and the item's slots. It names the clones, so the
// same moves on the same game data give the same GUIDs on a server and on its clients (which is why the executable's build is not in it: the two
// executables differ, the data does not).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One generation of a routed weapon's moves, ready to stage: all of it applies or none.</summary>
internal sealed class RouteGeneration
{
    public required string Hash { get; init; }
    /// <summary>The clones to define, one per move: {guid, name, base, fields}.</summary>
    public required IReadOnlyList<JsonObject> Clones { get; init; }
    /// <summary>The clones' rules by entry (a clone's name, or name#k for a later sub-sequence).</summary>
    public required IReadOnlyList<(string Entry, JsonObject Rules)> SequenceRules { get; init; }
    /// <summary>The item's slots: path -> clone name. The switch.</summary>
    public required JsonObject SlotRules { get; init; }
    /// <summary>The item's slot indices and the GUID each plays after the switch (the read-back).</summary>
    public required IReadOnlyDictionary<int, Guid> SlotGuids { get; init; }
    /// <summary>Every follow-up the rules set: (clone name, sub-sequence, K of the window) -> the GUID it must read back as.</summary>
    public required IReadOnlyDictionary<(string Clone, int Sub, int K), Guid> Links { get; init; }
    /// <summary>slot/link -> the clone's GUID.</summary>
    public required IReadOnlyDictionary<string, Guid> LiveClones { get; init; }
    public required string Item { get; init; }
}

internal static class MoveRoutes
{
    public const int MaxRoutes = 64, MaxLinks = 16, MaxHashes = 16;

    private static string Name(MoveFollowUpTrigger t) => t.ToString();

    /// <summary>Whether two selectors of one move select a common window whenever both select any: the same trigger and sub-sequence, and the same occurrence or one of them all.</summary>
    private static bool SameWindows(MoveFollowUpBlock a, MoveFollowUpBlock b) =>
        a.Trigger == b.Trigger && a.SubSequence == b.SubSequence && (a.Occurrence == null || b.Occurrence == null || a.Occurrence == b.Occurrence);

    /// <summary>What is wrong with the routes whatever the game's data is. Throws <see cref="BlockError"/>.</summary>
    public static void CheckOffline(string block, IReadOnlyList<PlannedMove> moves)
    {
        foreach (var m in moves)
        {
            if (m.FollowUps is not { } routes) continue;
            var at = $"{m.Path}.followUps";
            if (routes.Count > MaxRoutes) throw new BlockError(block, at, $"{routes.Count} routes: a move takes at most {MaxRoutes}");
            for (var i = 0; i < routes.Count; i++)
            {
                var r = routes[i] ?? throw new BlockError(block, $"{at}[{i}]", "a route is an object with trigger and to, not null");
                if (!Enum.IsDefined(r.Trigger)) throw new BlockError(block, $"{at}[{i}].trigger", $"{(int)r.Trigger} is not a trigger: Primary, Heavy, Block, Parried");
                if (!Enum.IsDefined(r.To)) throw new BlockError(block, $"{at}[{i}].to", $"{(int)r.To} is not a slot");
                if (r.SubSequence < 0) throw new BlockError(block, $"{at}[{i}].subSequence", $"{r.SubSequence}: sub-sequences count from 0");
                if (r.Occurrence is < 0) throw new BlockError(block, $"{at}[{i}].occurrence", $"{r.Occurrence}: windows count from 0 (leave it out for all of them)");
                if (r.Link < 1 || r.Link > MaxLinks) throw new BlockError(block, $"{at}[{i}].link", $"{r.Link}: links count from 1, and a chain has at most {MaxLinks}");
                var slot = (MoveSlot)r.To;
                var chain = moves.Where(x => x.Slot == slot).ToList();
                if (chain.Count == 0)
                    throw new BlockError(block, $"{at}[{i}].to", $"this weapon's moves do not say what {MoveSlots.Key(slot)} plays: a route leads to a move of the same weapon (a move with only a like plays the class's own there)");
                if (r.Link > chain.Count)
                    throw new BlockError(block, $"{at}[{i}].link", $"{MoveSlots.Key(slot)} has {chain.Count} link{(chain.Count == 1 ? "" : "s")}, so there is no link {r.Link}");
                for (var j = 0; j < i; j++)
                    if (SameWindows(routes[j], r))
                        throw new BlockError(block, $"{at}[{i}]", routes[j] == r
                            ? $"the same route as followUps[{j}]: a window is routed once"
                            : $"selects windows followUps[{j}] selects too ({Name(r.Trigger)} in sub-sequence {r.SubSequence}): a window is routed once, and neither route wins over the other");
            }
        }
    }

    /// <summary>Each route against its move's original events. Problems name the block's field; with any, the result is not to be used.</summary>
    public static Dictionary<(PlannedMove Move, int Sub, int K), PlannedMove> Resolve(MovePlan plan, IReadOnlyDictionary<PlannedMove, IReadOnlyList<SubSequence>> bases, List<string> problems)
    {
        var routed = new Dictionary<(PlannedMove, int, int), PlannedMove>();
        foreach (var m in plan.Moves)
        {
            if (m.FollowUps is not { Count: > 0 } routes) continue;
            if (!bases.TryGetValue(m, out var subs)) continue;      // its motion is missing: Find said so
            var by = new Dictionary<(int Sub, int K), int>();
            for (var i = 0; i < routes.Count; i++)
            {
                var r = routes[i];
                var at = $"block {plan.Block}, {m.Path}.followUps[{i}]";
                var target = plan.Moves.FirstOrDefault(x => x.Slot == (MoveSlot)r.To && x.Link == r.Link - 1);
                if (target == null || !bases.ContainsKey(target))
                {
                    problems.Add($"{at}: {MoveSlots.Key((MoveSlot)r.To)} link {r.Link} is not a move this weapon has in the game");
                    continue;
                }
                if (subs.FirstOrDefault(x => x.Index == r.SubSequence) is not { } sub)
                {
                    problems.Add($"{at}.subSequence: the motion has {subs.Count} sub-sequence{(subs.Count == 1 ? "" : "s")}, so there is no sub-sequence {r.SubSequence}");
                    continue;
                }
                var trigger = Name(r.Trigger);
                var windows = sub.Events.Where(e => e.Type == "SetActionOportunity" && e.TriggeredBy == trigger && e.FollowUp != Guid.Empty).ToList();
                if (windows.Count == 0)
                {
                    problems.Add($"{at}: the motion has no follow-up window for {trigger}{(r.SubSequence > 0 ? $" in sub-sequence {r.SubSequence}" : "")}: a route points a window the motion has, it makes none");
                    continue;
                }
                if (r.Occurrence is { } occ)
                {
                    if (occ >= windows.Count)
                    {
                        problems.Add($"{at}.occurrence: the motion has {windows.Count} follow-up window{(windows.Count == 1 ? "" : "s")} for {trigger}, so there is no occurrence {occ}");
                        continue;
                    }
                    windows = [windows[occ]];
                }
                foreach (var e in windows)
                {
                    if (by.TryGetValue((sub.Index, e.K), out var other))
                    {
                        problems.Add($"{at}: selects a window followUps[{other}] selects too ({MoveSlots.Key(m.Slot!.Value)} link {m.Link + 1}, sub-sequence {sub.Index}, window {e.K}): a window is routed once");
                        continue;
                    }
                    by[(sub.Index, e.K)] = i;
                    routed[(m, sub.Index, e.K)] = target;
                }
            }
        }
        return routed;
    }

    private static string N(double? d) => d is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "-";

    /// <summary>The hash of what a generation is made from.</summary>
    public static string Hash(MovePlan plan, IReadOnlyDictionary<PlannedMove, IReadOnlyList<SubSequence>> bases, IReadOnlyDictionary<MoveSlot, int[]> at)
    {
        var t = new StringBuilder();
        t.Append("kiln move routes 1\n").Append(plan.Mod).Append('\n').Append(plan.Key).Append('\n').Append(plan.Item).Append('\n').Append(plan.HeavyAirFollowsPrimary ? 1 : 0).Append('\n');
        foreach (var (slot, idx) in at.OrderBy(x => x.Key))
            t.Append("slot ").Append(MoveSlots.Key(slot)).Append(' ').Append(plan.Slots[slot]).Append(' ').Append(string.Join(",", idx)).Append('\n');
        foreach (var m in plan.Moves)
        {
            t.Append("move ").Append(MoveSlots.Key(m.Slot!.Value)).Append(' ').Append(m.Link).Append(' ').Append(m.Like ?? "-").Append(' ').Append(N(m.HitAt)).Append(' ').Append(N(m.Length)).Append('\n');
            if (m.Animation != null)
                foreach (var a in m.Animation) t.Append(" state ").Append(a == null ? "-" : a.Guid is { } g ? g.ToString() : "name:" + a.Name).Append('\n');
            if (m.FollowUps != null)
                foreach (var r in m.FollowUps)
                    t.Append(" route ").Append(r.Trigger).Append(' ').Append(r.SubSequence).Append(' ').Append(r.Occurrence?.ToString(CultureInfo.InvariantCulture) ?? "*").Append(' ')
                        .Append(r.To).Append(' ').Append(r.Link).Append('\n');
            if (!bases.TryGetValue(m, out var subs)) continue;
            foreach (var s in subs)
            {
                t.Append(" sub ").Append(s.Sequence).Append(' ').Append(s.Index).Append(' ').Append(N(s.Length)).Append('\n');
                foreach (var e in s.Events)
                {
                    t.Append("  ").Append(e.Type).Append(' ').Append(e.K).Append(' ').Append(N(e.Time)).Append(' ').Append(N(e.Duration)).Append(' ').Append(e.TriggeredBy ?? "-").Append(' ')
                        .Append(e.FollowUp).Append(' ').Append(N(e.FollowUpTime)).Append(' ').Append(e.State);
                    foreach (var (k, v) in e.Spans.OrderBy(x => x.Key, StringComparer.Ordinal)) t.Append(' ').Append(k).Append('=').Append(N(v));
                    t.Append('\n');
                }
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(t.ToString())));
    }

    /// <summary>The key of a move in <see cref="MoveApplicationResult.LiveClones"/>: <c>slot/link</c>.</summary>
    public static string LiveKey(PlannedMove m) => $"{MoveSlots.Key(m.Slot!.Value)}/{m.Link + 1}";

    /// <summary>The whole generation from the game's rows, or every problem (then no generation: nothing of it is to be applied).</summary>
    public static (RouteGeneration? Generation, List<string> Problems) Plan(MovePlan plan, IReadOnlyList<SlotRow>? slots, Func<string, IReadOnlyList<SubSequence>?> read, IPlayerStateLookup? lookup)
    {
        var (found, bases, at) = MovePlanner.Find(plan, slots, read);
        var problems = found.Problems.ToList();
        var routes = Resolve(plan, bases, problems);
        if (problems.Count > 0) return (null, problems);
        // Find reports every move without a motion and every slot the item lacks: with no problem, each move has its base and each slot its place
        var hash = Hash(plan, bases, at);
        var guids = plan.Moves.ToDictionary(m => m, m => BlockIds.Derive(plan.Mod, "sequence", $"{plan.Key}.routes.{hash}.{MoveSlots.Key(m.Slot!.Value)}.{m.Link + 1}"));
        string Clone(PlannedMove m) => $"{m.CloneName}.r{hash[..12]}";
        var work = new MoveWork();
        MovePlanner.Apply(plan, work, bases, at, lookup, Clone, routes);
        if (work.Problems.Count > 0) return (null, work.Problems);
        var byName = plan.Moves.ToDictionary(Clone, m => m, StringComparer.Ordinal);
        var links = new Dictionary<(string, int, int), Guid>();
        var sequences = new List<(string, JsonObject)>();
        JsonObject slotRules = new();
        foreach (var (section, entry, rules) in work.Rules)
        {
            if (section == "itemdata")
            {
                slotRules = rules;
                continue;
            }
            sequences.Add((entry, rules));
            var hashAt = entry.LastIndexOf('#');
            var (clone, sub) = hashAt > 0 && int.TryParse(entry.AsSpan(hashAt + 1), out var k) ? (entry[..hashAt], k) : (entry, 0);
            var events = bases[byName[clone]].First(s => s.Index == sub).Events;
            foreach (var (path, value) in rules)
            {
                if (!path.EndsWith(".followUp", StringComparison.Ordinal) || value == null) continue;
                var e = events.First(x => x.Type == "SetActionOportunity" && $"{x.Path}.followUp" == path);
                links[(clone, sub, e.K)] = guids[byName[(string)value!]];
            }
        }
        var slotGuids = new Dictionary<int, Guid>();
        foreach (var (path, value) in slotRules)
        {
            // "sequences[<i>].sequence"
            var i = int.Parse(path.AsSpan(path.IndexOf('[') + 1, path.IndexOf(']') - path.IndexOf('[') - 1), CultureInfo.InvariantCulture);
            slotGuids[i] = guids[byName[(string)value!]];
        }
        if (slotGuids.Count == 0) return (null, [$"block {plan.Block}, moves: none of its slots is on the item, so there is nothing to switch"]);
        return (new RouteGeneration
        {
            Hash = hash,
            Item = plan.Item,
            Clones = plan.Moves.Select(m => new JsonObject { ["guid"] = guids[m].ToString(), ["name"] = Clone(m), ["base"] = bases[m][0].Sequence.ToString(), ["fields"] = new JsonObject() }).ToList(),
            SequenceRules = sequences,
            SlotRules = slotRules,
            SlotGuids = slotGuids,
            Links = links,
            LiveClones = plan.Moves.ToDictionary(LiveKey, m => guids[m], StringComparer.Ordinal),
        }, problems);
    }
}
