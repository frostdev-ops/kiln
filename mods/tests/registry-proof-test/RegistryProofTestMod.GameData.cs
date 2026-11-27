// SPDX-License-Identifier: MIT
// Lab-only (as RegistryProofTestMod.cs): the live samples of dev.32's registries whose entries are kiln.gamedata's, buffs (the
// buff collection's records) and abilities (action sequences). Their entries come from Content.Buffs / Content.Sequences (kiln
// .content's enumeration has no such registry); a row's members are read raw by kiln.gamedata paths from the entry it lists (a
// buff's record; a sequence's first part, so an ability's "subSequences[0]." is left off); the bytes are the buff collection's
// and the sequence resource's. An eventTime row (abilities.timing.*) is probed earlier, never later: the first event of a kind
// moved earlier stays the first of its kind, so its raw path names it before and after the write.
using System.Globalization;
using System.Text.Json.Nodes;
using Kiln;

namespace RegistryProofTest;

public sealed unsafe partial class RegistryProofTestMod
{
    private static readonly string[] GameDataLive = { "buffs", "abilities" };
    private const string SubSequence0 = "subSequences[0].";

    private static GameDataRegistry GameDataOf(string registry) => registry == "buffs" ? Content.Buffs : Content.Sequences;

    private static uint Fnv1a(byte[] data)
    {
        var h = 2166136261u;
        foreach (var b in data) h = (h ^ b) * 16777619u;
        return h;
    }

    /// <summary>The kiln.gamedata path of a row's member from the entry kiln.gamedata lists.</summary>
    internal static string GameDataPath(string registry, string path) =>
        registry == "abilities" && path.StartsWith(SubSequence0, StringComparison.Ordinal) ? path[SubSequence0.Length..] : path;

    /// <summary>An eventTime probe: 0.75 x the first event's time (earlier, so it stays the first of its kind); none at 0.</summary>
    internal static JsonNode? EventTimeProbe(JsonNode? v) =>
        v is JsonValue t && t.TryGetValue<double>(out var s) && s > 0 ? JsonValue.Create(Math.Round(s * 0.75, 6)) : null;

    private static AssetKey? GameDataResourceOf(string registry, GameDataEntry e)
    {
        if (registry == "abilities") return new AssetKey(e.Guid, "keen::actor::ActorSequenceResource");
        var keys = Assets.ListResources(Assets.TypeHash("keen::BuffTypeCollectionResource"));
        return keys.Length > 0 ? keys[0] : null;
    }

    private static JsonNode? GameDataRaw(string registry, GameDataEntry e, string path)
    {
        try { return RawValue(e.Get(GameDataPath(registry, path))); }
        catch (Exception) { return null; }
    }

    /// <summary>Every mapped row of a gamedata registry over the game's entries kiln.gamedata lists now (clones are no registry
    /// entry; an ability is its sequence's first part).</summary>
    private void SampleGameData(string registry)
    {
        var map = JsonNode.Parse(File.ReadAllText(Path.Combine(_kiln, "regmap", "common", registry + ".json")))!;
        var table = _ids[registry]?.AsObject();
        if (table is null) { Log.Info($"registry-proof-test: unproven: {registry}: no id table yet"); return; }
        var entries = new List<(GameDataEntry E, string Id)>();
        var seen = 0;
        foreach (var e in GameDataOf(registry))
        {
            if (e.IsClone) continue;
            if (registry == "abilities" && e.Id != Fnv1a(e.Guid.ToByteArray())) continue;  // a later part of a sequence (kiln.gamedata's sub_id)
            ++seen;
            var id = registry == "buffs"
                ? (string?)table["#" + e.Id.ToString(CultureInfo.InvariantCulture)]
                : (string?)table[BytesText(e.Guid)] ?? (string?)table[e.Guid.ToString()];
            if (id != null) entries.Add((e, id));
        }
        Log.Info($"registry-proof-test: {registry}: {seen} game entries, {entries.Count} with a Kiln id (of {table.Count})");
        foreach (var (field, rowNode) in map["rows"]!.AsObject())
        {
            var row = rowNode!.AsObject();
            var paths = row["paths"]?.AsArray().Select(p => (string)p!).ToList() ?? new List<string>();
            if ((string?)row["state"] == "unmapped" || paths.Count == 0) continue;
            if (SampleGameDataRow(registry, field, row, paths, entries) is { } s) _samples.Add(s);
        }
    }

    private JsonObject? SampleGameDataRow(string registry, string field, JsonObject row, List<string> paths, List<(GameDataEntry E, string Id)> entries)
    {
        var q = $"{registry}.{field}";
        var conv = (string?)row["conv"] ?? "";
        var backend = (string?)row["backend"] ?? "";
        var why = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Skip(string reason) => why[reason] = why.GetValueOrDefault(reason) + 1;
        JsonArray RawOf(GameDataEntry e) => new(paths.Select(p => GameDataRaw(registry, e, p)).ToArray());
        var tries = 0;
        var candidates = backend != "live" && conv == "bool" ? entries.OrderByDescending(x => Read(registry, x.Id, field) == "true").ToList() : entries;
        foreach (var (e, id) in candidates.Take(600))
        {
            var readText = Read(registry, id, field, out var rc);
            if (readText is null) { Skip($"read rc={rc}"); continue; }
            var read = JsonNode.Parse(readText);
            if (read is null) { Skip("null or empty"); continue; }
            var s = new JsonObject { ["field"] = q, ["entry"] = id, ["element"] = null, ["read"] = read.DeepClone() };
            var key = GameDataResourceOf(registry, e);
            s["rawBefore"] = RawOf(e);
            if (s["rawBefore"] is JsonArray rb && rb.Any(x => x is null)) { Skip("raw member unreadable"); continue; }
            s["bytesBefore"] = BytesSha(key);
            if (backend != "live") return s;  // read-only: read only
            var probe = conv == "eventTime" ? EventTimeProbe(read) : ProbeOf(read, s["rawBefore"] as JsonArray, row, Array.Empty<string>());
            if (probe is null || JsonNode.DeepEquals(probe, read)) { Skip("no probe"); continue; }
            var (ok, refusal, _) = Change(registry, id, field, probe, out var handle);
            s["probe"] = probe.DeepClone();
            if (!ok && IsDisagreement(refusal))
            {
                s["error"] = "the live back-end refused the probe: " + refusal;
                return s;
            }
            if (!ok)
            {
                Skip("probe refused: " + refusal);
                if (++tries >= 20) break;
                continue;
            }
            var back = Read(registry, id, field);
            s["readBack"] = back is null ? null : JsonNode.Parse(back);
            s["rawAfter"] = RawOf(e);
            var revert = Finish(handle, true);
            s["reverted"] = revert.Outcome == Reverted;
            s["bytesAfter"] = BytesSha(key);
            return s;
        }
        Log.Info($"registry-proof-test: no sample of {q} ({entries.Count} entries): " + (why.Count == 0 ? "no entry" : string.Join(", ", why.Select(p => $"{p.Key} x{p.Value}"))));
        return null;
    }
}
