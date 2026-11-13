// SPDX-License-Identifier: MIT
// Game-tree nodes baked into a mod at build (Kiln.Sdk's bake, managed/Kiln.Sdk/bake; docs/kiln/modding/baked.md). A dedicated server
// reads its tree at boot, before managed mods load, so a node a mod defines in code is cached and in from the next start. The bake
// runs the mod under KilnHarness with Baking on: Define records what the code defines instead of asking kiln.skills, and the bake
// writes it as skills/<mod>.generated.json ("gameTree"), which kiln.skills reads at boot like any skills/*.json of the mod. At run
// time a node the code defines again as it was baked is left as the boot defined it (in the tree); one that differs is defined as
// before, with a warning: the mod's file wins at start.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

public static unsafe partial class GameTree
{
    /// <summary>The "comment" of a file Kiln.Sdk's bake wrote.</summary>
    internal const string BakedComment = "baked by the Kiln SDK at build from what the mod defines in code: docs/kiln/modding/baked.md";

    /// <summary>skills/&lt;mod&gt;.generated.json: the baked file's name.</summary>
    internal static string BakedFileName(string mod) => mod + ".generated.json";

    /// <summary>Kiln.Sdk's bake: <see cref="Available"/> is true and Define records (no kiln.skills under the harness).</summary>
    internal static volatile bool Baking;

    // mod -> GUID -> the node as its file has it (iconFile relative to the mod folder)
    private static readonly Dictionary<string, SortedDictionary<string, JsonObject>> Recorded = new(StringComparer.Ordinal);

    // mod -> why a node could not be baked
    private static readonly Dictionary<string, List<string>> NotBaked = new(StringComparer.Ordinal);

    /// <summary>What a mod defined while baking, in definition order of GUID (stable), and what could not be baked.</summary>
    internal static (IReadOnlyList<JsonObject> Nodes, IReadOnlyList<string> Problems) BakedOf(string mod)
    {
        lock (Recorded)
            return (Recorded.TryGetValue(mod, out var m) ? m.Values.Select(n => (JsonObject)n.DeepClone()).ToList() : [],
                NotBaked.TryGetValue(mod, out var p) ? p.ToList() : []);
    }

    /// <summary>Drops what was recorded for a mod (a bake starts from nothing).</summary>
    internal static void ForgetBaked(string mod)
    {
        lock (Recorded)
        {
            Recorded.Remove(mod);
            NotBaked.Remove(mod);
        }
    }

    /// <summary>The nodes of a request (one node object, an array, or {"nodes": [...]}), and the "mod" it names.</summary>
    internal static (List<JsonObject> Nodes, string? Mod) NodesOf(string json)
    {
        var j = JsonNode.Parse(json);
        var list = j switch
        {
            JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o when o["nodes"] is JsonArray a => a.OfType<JsonObject>().ToList(),
            JsonObject o => [o],
            _ => [],
        };
        return (list, (j as JsonObject)?["mod"]?.GetValue<string>());
    }

    /// <summary>A node as a mod's file holds it: an icon file in the mod's folder by its path from there ('/'), as kiln.skills
    /// resolves a mod file's paths; null when it names a file outside the folder (that path is the machine's, not the mod's).
    /// A relative path in a definition from code is the kiln folder's (as kiln.skills resolves code definitions), so it is
    /// resolved against <paramref name="kilnDirectory"/> first.</summary>
    internal static JsonObject? Portable(JsonObject node, string modDirectory, string kilnDirectory)
    {
        var copy = (JsonObject)node.DeepClone();
        copy.Remove("mod");
        if (copy["iconFile"]?.GetValue<string>() is not { Length: > 0 } icon) return copy;
        var root = Path.GetFullPath(modDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.IsPathRooted(icon) ? icon : Path.Combine(kilnDirectory, icon));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        copy["iconFile"] = full[root.Length..].Replace('\\', '/');
        return copy;
    }

    private static IReadOnlyList<GameNodeInfo> Record(string json, ModContext? mod)
    {
        var (nodes, named) = NodesOf(json);
        var id = named ?? mod?.Info.Id ?? "";
        var dir = mod?.Info.Directory ?? Mods.Get(id)?.Directory ?? "";
        var list = new List<GameNodeInfo>();
        lock (Recorded)
        {
            if (!Recorded.TryGetValue(id, out var m)) Recorded[id] = m = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var n in nodes)
            {
                var name = n["name"]?.GetValue<string>() ?? "";
                if (!Guid.TryParse(n["guid"]?.GetValue<string>(), out var g))
                {
                    list.Add(new GameNodeInfo(Guid.Empty, 0, name, id, "code", "error", -1, -1, "a node needs a \"guid\""));
                    continue;
                }
                if (Portable(n, dir, KilnPaths.Root) is not { } file)
                {
                    if (!NotBaked.TryGetValue(id, out var p)) NotBaked[id] = p = [];
                    p.Add($"game-tree node {name} ({g}): its iconFile {n["iconFile"]} is outside the mod's folder, so the node is not baked (it is in from the next start)");
                    list.Add(new GameNodeInfo(g, GameNodeDefinition.Fnv(g.ToByteArray()), name, id, "code", "pending", -1, -1, null));
                    continue;
                }
                m[g.ToString()] = file;
                list.Add(new GameNodeInfo(g, GameNodeDefinition.Fnv(g.ToByteArray()), name, id, "code", "defined", -1, -1, null));
            }
        }
        return list;
    }

    /// <summary>Whether a node the code defines at run time is the one baked (the bake recorded <see cref="Portable"/> of the same request).</summary>
    internal static bool SameAsBaked(JsonObject request, JsonObject baked, string modDirectory, string kilnDirectory) =>
        Portable(request, modDirectory, kilnDirectory) is { } now && JsonNode.DeepEquals(now, baked);

    /// <summary>The nodes of the mod's own skills/&lt;mod&gt;.generated.json when Kiln.Sdk's bake wrote it, by GUID; empty otherwise.</summary>
    internal static Dictionary<string, JsonObject> BakedFile(string modDirectory, string mod)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // bounded: at most 4 MiB, no link on the way, nothing outside the mod's folder (BakedFiles)
            if (BakedFiles.Read(modDirectory, Path.Combine("skills", BakedFileName(mod))) is not { } text ||
                JsonNode.Parse(text) is not JsonObject o || (string?)o["comment"] != BakedComment) return map;
            if (o["gameTree"]?["nodes"] is JsonArray a)
                foreach (var n in a.OfType<JsonObject>())
                    if (Guid.TryParse(n["guid"]?.GetValue<string>(), out var g)) map[g.ToString()] = n;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }
        return map;
    }

    private static readonly Dictionary<string, string> Told = new(StringComparer.Ordinal);

    // Run time, a mod with a baked file: the nodes the code defines as they were baked, and that kiln.skills has from that file, are
    // left as they are (in the tree since boot; defining them again would only mark them pending). The rest go to kiln.skills.
    private static string Unbaked(string json, ModContext? mod, List<GameNodeInfo> kept)
    {
        List<JsonObject> nodes;
        string? named;
        try
        {
            (nodes, named) = NodesOf(json);
        }
        catch (JsonException)
        {
            return json;  // kiln.skills says what is wrong with it
        }
        var id = named ?? mod?.Info.Id;
        var dir = mod?.Info.Directory is { Length: > 0 } d ? d : id is null ? null : Mods.Get(id)?.Directory;
        if (id is null || dir is not { Length: > 0 }) return json;
        var baked = BakedFile(dir, id);
        if (baked.Count == 0) return json;
        var file = BakedFileName(id);
        var fromFile = Definitions.Where(x => x.Owner == id && x.Source.EndsWith(file, StringComparison.OrdinalIgnoreCase) && x.State is not ("error" or "orphaned"))
            .GroupBy(x => x.Guid.ToString(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rest = new JsonArray();
        foreach (var n in nodes)
        {
            var g = Guid.TryParse(n["guid"]?.GetValue<string>(), out var parsed) ? parsed.ToString() : "";
            if (!baked.TryGetValue(g, out var was))
            {
                rest.Add(n.DeepClone());
                continue;
            }
            var same = SameAsBaked(n, was, dir, KilnPaths.Root);
            Tell(id, g, same
                ? null
                : $"game-tree node {n["name"]} ({g}) is not what was baked into the mod at build (skills/{file}): the tree has the baked one, read at start; " +
                  "code that defines something else after the build needs KilnBake=false in the project, and a rebuild brings the file up to date");
            if (same && fromFile.TryGetValue(g, out var info)) kept.Add(info);
            else rest.Add(n.DeepClone());
        }
        if (rest.Count == nodes.Count) return json;
        return new JsonObject { ["mod"] = id, ["nodes"] = rest }.ToJsonString();
    }

    private static void Tell(string mod, string guid, string? problem)
    {
        lock (Told)
        {
            var key = mod + "/" + guid;
            if (Told.TryGetValue(key, out var was) && was == (problem ?? "")) return;
            Told[key] = problem ?? "";
        }
        if (problem is null) SkillsInternals.Info($"{mod}: game-tree node {guid} is defined in code as it was baked into the mod at build: it is in the tree from the first start");
        else SkillsInternals.Warn($"{mod}: {problem}");
    }
}
