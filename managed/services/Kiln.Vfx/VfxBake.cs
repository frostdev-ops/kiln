// SPDX-License-Identifier: MIT
// VFX and materials baked into a mod at build (Kiln.Sdk's bake, managed/Kiln.Sdk/bake; docs/kiln/modding/baked.md). The game lists its
// VFX and materials once at start, so one a mod defines in code is kept in kiln/data/vfx/pending/<mod>/ and there from the next start.
// The bake runs the mod under KilnHarness with Baking on: there is no kiln.vfx there, so this records what Kiln.Blocks hands the
// builder, and the bake writes it as blocks/generated/<mod>.json, which kiln.vfx reads at boot beside the mod's blocks/*.json
// (native/services/vfx/boot.cpp, read_mod). At run time the builder compares what code defines with what it built at boot, as for any
// block: the same is "registered", anything else is kept for the next start as before.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class VfxNative
{
    /// <summary>The "comment" of a file Kiln.Sdk's bake wrote.</summary>
    internal const string BakedComment = "baked by the Kiln SDK at build from what the mod defines in code: docs/kiln/modding/baked.md";

    /// <summary>Kiln.Sdk's bake: with no kiln.vfx, the status is an empty build and definitions are recorded.</summary>
    internal static volatile bool Baking;

    private const string BakingStatus = "{\"ok\":true,\"refusedMods\":[],\"baking\":true}";

    // mod -> section ("vfx", "materials") -> name -> the block as a data file has it
    private static readonly Dictionary<string, Dictionary<string, SortedDictionary<string, JsonNode>>> Recorded = new(StringComparer.Ordinal);

    /// <summary>What a mod's code handed the builder while baking: section -> name -> block.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonNode>> BakedOf(string mod)
    {
        lock (Recorded)
        {
            var result = new Dictionary<string, IReadOnlyDictionary<string, JsonNode>>(StringComparer.Ordinal);
            if (Recorded.TryGetValue(mod, out var sections))
                foreach (var (section, blocks) in sections)
                    result[section] = blocks.ToDictionary(kv => kv.Key, kv => kv.Value.DeepClone(), StringComparer.Ordinal);
            return result;
        }
    }

    /// <summary>Drops what was recorded for a mod (a bake starts from nothing).</summary>
    internal static void ForgetBaked(string mod)
    {
        lock (Recorded) Recorded.Remove(mod);
    }

    // The builder's report, as kiln.vfx gives it for a definition it keeps for the next start.
    private static string Record(string json)
    {
        JsonObject? doc;
        // a data file's block comes as the bytes the boot read: comments and trailing commas as its file has them
        try { doc = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject; }
        catch (JsonException) { doc = null; }
        if (doc is null || doc["mod"]?.GetValue<string>() is not { Length: > 0 } mod)
            return "{\"ok\":false,\"errors\":[\"expected {\\\"format\\\": 1, \\\"mod\\\": id, \\\"dir\\\": folder, \\\"vfx\\\": {...}, \\\"materials\\\": {...}}\"]}";
        if (doc["release"] is JsonValue)
            return "{\"ok\":true,\"released\":0}";
        var report = new JsonObject { ["ok"] = true, ["warnings"] = new JsonArray(), ["errors"] = new JsonArray() };
        lock (Recorded)
        {
            if (!Recorded.TryGetValue(mod, out var sections)) Recorded[mod] = sections = new(StringComparer.Ordinal);
            foreach (var (section, list) in new[] { ("vfx", "blocks"), ("materials", "materials") })
            {
                if (doc[section] is not JsonObject blocks) continue;
                if (!sections.TryGetValue(section, out var kept)) sections[section] = kept = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
                var built = new JsonArray();
                foreach (var (name, block) in blocks)
                {
                    if (block is null) continue;
                    kept[name] = block.DeepClone();
                    built.Add(new JsonObject { ["name"] = name, ["status"] = "pending" });
                }
                report[list] = built;
            }
        }
        return report.ToJsonString();
    }
}
