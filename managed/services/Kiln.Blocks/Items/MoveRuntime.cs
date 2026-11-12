// SPDX-License-Identifier: MIT
// Moves when content is ready: the game's data read through kiln.gamedata (the made item's `sequences`, the sequences' events and lengths), handed to
// MovePlanner, and what it decides registered as BlockRuntime's other ready work is (tool.breaksInto does the same). Two passes: the clones of the class's own
// sequences are defined in the first, and their rules are added in the next, once kiln.gamedata has them in the world.
using System.Text.Json.Nodes;

namespace Kiln;

internal static class MoveRuntime
{
    private static readonly string[] Spans = ["lerpDuration", "hitStopDuration", "blendInDuration", "blendOutDuration"];

    /// <summary>What a read of the game's data can throw: reported by block, and the move keeps its class's.</summary>
    private static bool Readable(Exception e) => e is KeyNotFoundException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException;

    public static void Weapon(ModBlocks mod, MovePlan plan, JsonObject json, Action<string> problem)
    {
        try { WeaponNow(mod, plan, json, problem); }
        catch (Exception e) when (Readable(e)) { problem($"block {plan.Block}, moves: the game's data could not be read: {e.Message}"); }
    }

    private static void WeaponNow(ModBlocks mod, MovePlan plan, JsonObject json, Action<string> problem)
    {
        var read = Reader();
        var (work, bases, at) = MovePlanner.Find(plan, Slots(plan.Item), read);
        foreach (var p in work.Problems) problem(p);
        foreach (var def in work.Clones)
        {
            try { BlocksBridge.DefineClone(Content.Sequences, def, mod.Owner); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException)
            {
                problem($"block {plan.Block}, moves: the clone {def["name"]} of {def["base"]} was refused: {e.Message}");
                if (bases.Keys.FirstOrDefault(m => m.CloneName == (string?)def["name"]) is { } gone) bases.Remove(gone);
            }
        }
        void Second(Action<string> problem2)
        {
            var w = new MoveWork();
            MovePlanner.Apply(plan, w, bases, at, PlayerStateSeam.Lookup);
            foreach (var p in w.Problems) problem2(p);
            foreach (var (section, entry, rules) in w.Rules) BlockRuntime.AddRules(mod, section, entry, rules);
            var ready = work.Report.DeepClone().AsObject();
            ready["rules"] = new JsonObject(w.Rules.Select(r => KeyValuePair.Create($"{r.Section} {r.Entry}", (JsonNode?)r.Rules.DeepClone())));
            lock (json) json["movesReady"] = ready;
            mod.ReportDirty = true;
        }
        // a clone made now is "pending" until kiln.gamedata has put it in the world; BlockRuntime's next pass waits for that
        if (work.Clones.Count > 0) BlockRuntime.AddReady(mod, Second);
        else Second(problem);
    }

    /// <summary>A cast's motion (a wand attack's, a staff spell's): the clone is the compile's.</summary>
    public static void Cast(ModBlocks mod, string block, string path, string clone, string baseName, IReadOnlyList<AnimationRef?>? animation, double? hitAt,
        Func<EventRow, bool> damage, JsonObject json, Action<string> problem)
    {
        MoveWork w;
        try { w = MovePlanner.Cast(mod.Id, block, path, clone, Reader()(baseName), animation, hitAt, damage, PlayerStateSeam.Lookup); }
        catch (Exception e) when (Readable(e))
        {
            problem($"block {block}, {path}: the game's data could not be read: {e.Message}");
            return;
        }
        foreach (var p in w.Problems) problem(p);
        foreach (var (section, entry, rules) in w.Rules) BlockRuntime.AddRules(mod, section, entry, rules);
        lock (json) ((json["movesReady"] ??= new JsonObject()).AsObject())[path] = new JsonObject(w.Rules.Select(r => KeyValuePair.Create(r.Entry, (JsonNode?)r.Rules.DeepClone())));
        mod.ReportDirty = true;
    }

    // ---- reading the game's data ------------------------------------------------------------------------------------------------------------------

    /// <summary>A value as kiln.gamedata's JSON gives it: the original when the node has one (a clone copies the game's original), else the value.</summary>
    private static JsonNode? Original(JsonNode? node) => node is JsonObject o && o.ContainsKey("path") ? (o["original"] is { } orig && orig.GetValueKind() == o["value"]?.GetValueKind() ? orig : o["value"]) : node;

    private static double Seconds(JsonNode? n) => n switch
    {
        JsonObject o when o["value"] is { } v => Seconds(v),
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        _ => 0,
    };

    private static Guid GuidOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : Guid.Empty;

    private static IReadOnlyList<string> Flags(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(x => x?.ToString() ?? "").ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', '|', ' '], StringSplitOptions.RemoveEmptyEntries),
        _ => [],
    };

    /// <summary>The item's slots, or null when the game has no such item.</summary>
    private static IReadOnlyList<SlotRow>? Slots(string item)
    {
        if (Content.ItemData.Get(item) is not { } data) return null;
        if (Original(data.GetJson("sequences")) is not JsonArray list) return [];
        return list.Select(x => x as JsonObject).Where(x => x != null)
            .Select(x => new SlotRow(x!["triggeredBy"]?.ToString() ?? "", Flags(x["requirements"]), x["specialInteractionType"]?.ToString() ?? "Invalid", GuidOf(x["sequence"]))).ToList();
    }

    /// <summary>One event of the game's JSON as a row; <paramref name="seen"/> counts the events of each type so far.</summary>
    internal static EventRow Row(JsonObject ev, Dictionary<string, int> seen)
    {
        var type = ((string?)ev["$type"] ?? "").Split(':')[^1];
        var k = seen.GetValueOrDefault(type);
        seen[type] = k + 1;
        var spans = new Dictionary<string, double>();
        foreach (var f in Spans)
            if (ev[f] != null) spans[f] = Seconds(ev[f]);
        return new EventRow(type, k, Seconds(ev["time"]), Seconds(ev["duration"]))
        {
            Spans = spans,
            FollowUpTime = ev["followUpTime"] != null ? Seconds(ev["followUpTime"]) : null,
            TriggeredBy = ev["triggeredBy"]?.ToString(),
            FollowUp = GuidOf(ev["followUp"]),
            State = GuidOf(ev["animationName"] ?? ev["overlayName"]),
        };
    }

    /// <summary>A sequence's sub-sequences by name or GUID: the game's registry holds each sub-sequence as an entry with its sequence's GUID.</summary>
    private static Func<string, IReadOnlyList<SubSequence>?> Reader()
    {
        Dictionary<Guid, List<GameDataEntry>>? byGuid = null;
        return key =>
        {
            if (Content.Sequences.Get(key) is not { } e) return null;
            if (byGuid == null)
            {
                byGuid = new();
                var all = Content.Sequences;
                for (var i = 0; i < all.Count; i++)
                {
                    var x = all[i];
                    if (!byGuid.TryGetValue(x.Guid, out var l)) byGuid[x.Guid] = l = new();
                    l.Add(x);
                }
            }
            var subs = byGuid.TryGetValue(e.Guid, out var list) ? list : [e];
            return subs.Select((s, i) =>
            {
                var seen = new Dictionary<string, int>();
                var events = Original(s.GetJson("events")) is JsonArray a ? a.OfType<JsonObject>().Select(ev => Row(ev, seen)).ToList() : [];
                return new SubSequence(s.Guid, i, Seconds(Original(s.GetJson("length"))), events);
            }).ToList();
        };
    }
}
