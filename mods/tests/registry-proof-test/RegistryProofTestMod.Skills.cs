// SPDX-License-Identifier: MIT
// Lab-only: the skills registry's live rows (dev.32). The skill tree's nodes are kiln.gamedata's skills section (Content.Skills),
// not a Content registry, and have no GUID: the producer takes them from the id table ("#<node id>"). Each live row gets one
// round trip as the other registries' do (read, raw read by the game's member path, write through kiln.registry.live, read back,
// raw again, revert, the resource's bytes before and after), on a node that is not a root and has no levels. Then, for the
// cost, the read-time evidence (an effect, not judged by kilnctl): the game's own PointsSpent over a scratch tree holding only
// that node (GameTree.Simulate) and the game's NodeById view of its cost (GameTree.Dump), before and while the probe is written.
// The read-only rows (maxLevel, kind, class, unlocks) are kiln.content's (registry/proof.cpp reads them from the base).
using System.Globalization;
using System.Text.Json.Nodes;
using Kiln;

namespace RegistryProofTest;

public sealed unsafe partial class RegistryProofTestMod
{
    private void SampleSkills()
    {
        var mapFile = Path.Combine(_kiln, "regmap", "common", "skills.json");
        if (!File.Exists(mapFile)) { Log.Info("registry-proof-test: skills: no regmap/common/skills.json (an older Kiln)"); return; }
        var map = JsonNode.Parse(File.ReadAllText(mapFile))!;
        var table = _ids["skills"]?.AsObject();
        if (table is null) { Log.Info("registry-proof-test: unproven: skills: no id table yet"); return; }
        var nodes = SkillNodes(table);
        Log.Info($"registry-proof-test: skills: {nodes.Count} nodes in the id table, {Content.Skills.Count} in kiln.gamedata's skills section");
        foreach (var (field, rowNode) in map["rows"]!.AsObject())
        {
            var row = rowNode!.AsObject();
            var paths = row["paths"]?.AsArray().Select(p => (string)p!).ToList() ?? new List<string>();
            if ((string?)row["state"] == "unmapped" || paths.Count != 1 || (string?)row["backend"] != "live") continue;
            if (SampleSkillRow(field, row, paths[0], nodes) is { } s) _samples.Add(s);
        }
    }

    /// <summary>The id table's skill nodes ("#&lt;node id&gt;" -> Kiln id), in table order.</summary>
    internal static List<(uint Node, string Id)> SkillNodes(JsonObject table)
    {
        var nodes = new List<(uint, string)>();
        foreach (var (key, idNode) in table)
            if (key.StartsWith('#') && uint.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var node) && (string?)idNode is { } id)
                nodes.Add((node, id));
        return nodes;
    }

    private JsonObject? SampleSkillRow(string field, JsonObject row, string path, List<(uint Node, string Id)> nodes)
    {
        var q = $"skills.{field}";
        var key = SkillTreeKey();
        var why = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Skip(string reason) => why[reason] = why.GetValueOrDefault(reason) + 1;
        foreach (var (node, id) in nodes)
        {
            var g = Content.Skills.Get(node);
            if (g is null) { Skip("not in kiln.gamedata's skills section"); continue; }
            // a node the effect can show alone: not a root (always unlocked), no levels (its cost counts once)
            if (IsRootOrLeveled(RawValue(g.Get("type")), RawValue(g.Get("maxLevel")))) { Skip("a root or a node with levels"); continue; }
            var readText = Read("skills", id, field, out var rc);
            if (readText is null) { Skip($"read rc={rc}"); continue; }
            var read = JsonNode.Parse(readText);
            var s = new JsonObject { ["field"] = q, ["entry"] = id, ["element"] = null, ["read"] = read?.DeepClone() };
            s["rawBefore"] = new JsonArray(RawValue(g.Get(path)));
            var before = BytesSha(key);
            s["bytesBefore"] = before;
            var probe = Probe(read, row, Array.Empty<string>());
            if (probe is null || JsonNode.DeepEquals(probe, read)) { Skip("no probe"); continue; }
            s["probe"] = probe.DeepClone();
            var spentBefore = Spent(node);
            var treeBefore = TreeCost(node);
            var (ok, refusal, _) = Change("skills", id, field, probe, out var handle);
            if (!ok && IsDisagreement(refusal)) { s["error"] = "the live back-end refused the probe: " + refusal; return s; }
            if (!ok) { Skip("probe refused: " + refusal); continue; }
            var back = Read("skills", id, field);
            s["readBack"] = back is null ? null : JsonNode.Parse(back);
            s["rawAfter"] = new JsonArray(RawValue(Content.Skills.Get(node)!.Get(path)));
            if (field == "cost") SkillCostEffect(id, read, probe, spentBefore, Spent(node), treeBefore, TreeCost(node));
            s["reverted"] = Finish(handle, true).Outcome == Reverted;
            s["bytesAfter"] = BytesSha(key);
            return s;
        }
        Log.Info($"registry-proof-test: no sample of {q} ({nodes.Count} nodes): " + (why.Count == 0 ? "no node" : string.Join(", ", why.Select(p => $"{p.Key} x{p.Value}"))));
        return null;
    }

    /// <summary>A node the effect cannot show alone, or whose kind and levels cannot be read: a root (the game's enum name "Root",
    /// or its value 0 when the reader gives numbers) or a node with levels (maxLevel above 0).</summary>
    internal static bool IsRootOrLeveled(JsonNode? type, JsonNode? maxLevel)
    {
        if (type is not JsonValue t || maxLevel is not JsonValue m || !m.TryGetValue<long>(out var levels)) return true;
        var root = t.TryGetValue<string>(out var name) ? name == "Root" : !t.TryGetValue<long>(out var value) || value == 0;
        return root || levels != 0;
    }

    private static AssetKey? SkillTreeKey()
    {
        var keys = Assets.ListResources(Assets.TypeHash("keen::SkillTreeResource"));
        return keys.Length > 0 ? keys[0] : null;
    }

    /// <summary>The game's own PointsSpent over a scratch tree that holds only <paramref name="node"/> (GameTree.Simulate), null when
    /// kiln.skills cannot run the game's skill functions here.</summary>
    private static int? Spent(uint node)
    {
        try
        {
            if (!GameTree.Available) return null;
            var r = GameTree.Simulate((GameTreeSimOp.UnlockForce, node));
            return r.Count == 1 && r[0].Ok ? r[0].Spent : null;
        }
        catch (Exception x) { Log.Info($"registry-proof-test: skills: the game's skill functions did not run: {x.Message}"); return null; }
    }

    /// <summary>The node's cost as the game's skill tree holds it (its NodeById: GameTree.Dump), null when unreadable.</summary>
    private static long? TreeCost(uint node)
    {
        try { return GameTree.Available ? TreeCostOf(GameTree.Dump(), node) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>A node's "cost" in kiln.skills' tree dump ({"nodes": [{"id": "0x...", "cost": n, ...}]}), null when it is not there.</summary>
    internal static long? TreeCostOf(string dump, uint node)
    {
        var hex = "0x" + node.ToString("x8", CultureInfo.InvariantCulture);
        foreach (var n in JsonNode.Parse(dump)?["nodes"]?.AsArray() ?? new JsonArray())
            if (string.Equals((string?)n?["id"], hex, StringComparison.OrdinalIgnoreCase) && n?["cost"] is JsonValue c && c.TryGetValue<long>(out var cost))
                return cost;
        return null;
    }

    /// <summary>When the game reads skills.cost: its PointsSpent and its tree follow the write at once (the read time "now" in
    /// regmap/common); the suite's effects and the log, never the round trip's judgement.</summary>
    private void SkillCostEffect(string id, JsonNode? read, JsonNode probe, int? spentBefore, int? spentWritten, long? treeBefore, long? treeWritten)
    {
        var was = read is JsonValue r && r.TryGetValue<long>(out var a) ? a : -1;
        var now = probe is JsonValue p && p.TryGetValue<long>(out var b) ? b : -1;
        var observed = $"{id}: the game's PointsSpent of a tree holding only it {Show(spentBefore)} -> {Show(spentWritten)}, its NodeById cost {Show(treeBefore)} -> {Show(treeWritten)}";
        var inconclusive = spentBefore is null || spentWritten is null || treeBefore is null || treeWritten is null;
        var ok = !inconclusive && spentBefore == was && spentWritten == now && treeBefore == was && treeWritten == now;
        _effects.Add(new JsonObject { ["field"] = "skills.cost", ["setting"] = $"{was} -> {now}", ["observed"] = observed,
            ["result"] = inconclusive ? "inconclusive" : ok ? "followed" : "not followed" });
        if (inconclusive) Log.Info($"registry-proof-test: INCONCLUSIVE skills.cost ({was} -> {now}): {observed}");
        else Check(ok, $"skills.cost ({was} -> {now}) is what the game's own skill functions read: {observed}");
        static string Show<T>(T? v) where T : struct => v is { } x ? Convert.ToString(x, CultureInfo.InvariantCulture)! : "unread";
    }
}
