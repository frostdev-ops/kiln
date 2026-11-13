// SPDX-License-Identifier: MIT
// Runs one mod folder under KilnHarness with the services' recorders on and writes what its code defined into the folder:
// docs/kiln/modding/baked.md. Route: the mod's own OnLoad, not attributes read by a generator, because a mod computes its
// definitions (loops over its data, names built from strings, a helper library's builders, blocks compiled from JSON); only
// running the code gives what the game gets at run time.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln.TestKit;

namespace Kiln.Sdk.Bake;

/// <summary>One bake: the mod folder it ran, and what it wrote, removed, left alone and could not do.</summary>
internal sealed class BakeResult
{
    public string ModId { get; set; } = "";
    public List<string> Written { get; } = [];
    public List<string> Unchanged { get; } = [];
    public List<string> Removed { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Warnings { get; } = [];
}

internal sealed class BakeOptions
{
    /// <summary>Server steps after the mod loaded: its OnLoad, the server's start and what a mod defines in its first steps.
    /// Below 11, the first step at which Kiln's services write their own files.</summary>
    public int Steps { get; init; } = 10;

    /// <summary>Where the harness makes its server folder (default the system's temp folder).</summary>
    public string? TempDirectory { get; init; }

    /// <summary>Make no files, say what would be written (tests and --check).</summary>
    public bool DryRun { get; init; }
}

internal static class Baker
{
    public const string Comment = "baked by the Kiln SDK at build from what the mod defines in code: docs/kiln/modding/baked.md";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions Relaxed = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The mod id of a folder's kiln.mod.json; null when it has none.</summary>
    internal static string? ModIdOf(string folder) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "kiln.mod.json")), documentOptions: Relaxed)?["id"]?.GetValue<string>() is { Length: > 0 } id ? id : null;

    /// <summary>The three files, relative to the mod folder.</summary>
    public static string QuestsFile(string mod) => Path.Combine("quests", "generated", mod + ".json");
    public static string SkillsFile(string mod) => Path.Combine("skills", GameTree.BakedFileName(mod));
    public static string VfxFile(string mod) => Path.Combine("blocks", "generated", mod + ".json");

    public static BakeResult Run(string modFolder, BakeOptions? options = null)
    {
        options ??= new BakeOptions();
        var result = new BakeResult();
        var folder = Path.GetFullPath(modFolder);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "kiln.mod.json")), documentOptions: Relaxed) as JsonObject
                       ?? throw new InvalidDataException($"{folder}/kiln.mod.json is not a JSON object");
        var id = manifest["id"]?.GetValue<string>() is { Length: > 0 } i ? i : throw new InvalidDataException($"{folder}/kiln.mod.json has no id");
        result.ModId = id;
        if (manifest["entryAssembly"] is null)
        {
            result.Notes.Add("a data-only mod: nothing runs, so nothing is baked");
            Clean(folder, id, result, options);
            return result;
        }
        if ((string?)manifest["side"] == "client")
        {
            result.Notes.Add("a client-only mod: the bake runs a server, which does not load it; nothing is baked");
            Clean(folder, id, result, options);
            return result;
        }

        NativeExport.Baking = true;
        GameTree.Baking = true;
        VfxNative.Baking = true;
        GameTree.ForgetBaked(id);
        VfxNative.ForgetBaked(id);
        JsonObject? quests, skills, vfx;
        try
        {
            using var harness = new KilnHarness(new KilnHarnessOptions
            {
                ThrowOnModFault = false,
                CrashReports = false,
                TempDirectory = options.TempDirectory,
                WaitForOtherHarness = TimeSpan.FromMinutes(5),
            });
            var handle = harness.LoadFolder(folder);
            try
            {
                harness.Step(Math.Max(1, options.Steps));
            }
            catch (ModLoadException e)
            {
                // the loader refused it, or its OnLoad threw (most often: it needs a service only the game has): a limit of the bake,
                // not a fault of the mod, so a note and not a warning
                result.Notes.Add($"not baked: the mod does not load without the game ({e.Message}); what its code defines is there from the next " +
                                 "start, as without the bake (docs/kiln/modding/baked.md, \"What is not baked\")");
                Clean(folder, id, result, options);
                return result;
            }
            foreach (var line in harness.LogsOf(id).Concat(harness.Logs.Where(l => l.ModId is null && l.Text.Contains(id, StringComparison.Ordinal))))
                if (line.Level == LogLevel.Error) result.Warnings.Add($"[{line.Source}] {line.Text}");
            if (!handle.IsLoaded)
            {
                result.Notes.Add($"not baked: the mod does not load without the game ({handle.State.ToString().ToLowerInvariant()}: {handle.Error ?? "no reason given"}); " +
                                 "what its code defines is there from the next start, as without the bake (docs/kiln/modding/baked.md, \"What is not baked\")");
                Clean(folder, id, result, options);
                return result;
            }
            quests = QuestsDocument(id);
            skills = SkillsDocument(id, result);
            vfx = VfxDocument(id, folder);
        }
        finally
        {
            NativeExport.Baking = false;
            GameTree.Baking = false;
            VfxNative.Baking = false;
        }
        Write(folder, id, quests, skills, vfx, result, options);
        RemoveOtherIds(folder, id, result, options);
        return result;
    }

    /// <summary>Nothing baked this time (the mod did not load, a data-only or client-only mod, or the bake failed): what an
    /// earlier bake wrote goes, so it does not win at start over what the code defines now; the author's files stay.</summary>
    internal static void Clean(string folder, string id, BakeResult result, BakeOptions options)
    {
        Write(folder, id, null, null, null, result, options);
        RemoveOtherIds(folder, id, result, options);
    }

    /// <summary>Baked files named for another mod id (left from a rename: the start-up builds read every file there under
    /// this mod's name) go; the author's files stay.</summary>
    internal static void RemoveOtherIds(string folder, string id, BakeResult result, BakeOptions options)
    {
        foreach (var (dir, pattern, mine) in new[]
                 {
                     (Path.Combine(folder, "quests", "generated"), "*.json", QuestsFile(id)),
                     (Path.Combine(folder, "skills"), "*.generated.json", SkillsFile(id)),
                     (Path.Combine(folder, "blocks", "generated"), "*.json", VfxFile(id)),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, pattern).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file);
                if (string.Equals(relative, mine, StringComparison.Ordinal) || !IsBaked(file)) continue;
                if (!options.DryRun) File.Delete(file);
                result.Removed.Add(relative.Replace('\\', '/'));
            }
        }
    }

    /// <summary>The files this bake left in the mod folder ('/'), for the build's own bookkeeping (KilnBake's list).</summary>
    internal static IEnumerable<string> Outputs(BakeResult result) => result.Written.Concat(result.Unchanged).Order(StringComparer.Ordinal);

    /// <summary>What Kiln.Quests would write for the game's journal (Native/NativeExport.cs), with the bake's comment.</summary>
    internal static JsonObject? QuestsDocument(string mod)
    {
        var doc = NativeExport.Document(mod, out _);
        if (doc is null) return null;
        doc["comment"] = Comment;
        // by id, so the same definitions give the same file whatever order Kiln.Quests holds them in (the game's build reads
        // each section by id; the run-time comparison ignores the order)
        foreach (var section in new[] { "npcs", "quests", "dialogues" })
            if (doc[section] is JsonObject o)
                doc[section] = new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
        return doc;
    }

    internal static JsonObject? SkillsDocument(string mod, BakeResult result)
    {
        var (nodes, problems) = GameTree.BakedOf(mod);
        result.Warnings.AddRange(problems);
        if (nodes.Count == 0) return null;
        return new JsonObject
        {
            ["format"] = 1,
            ["comment"] = Comment,
            ["gameTree"] = new JsonObject { ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)n).ToArray()) },
        };
    }

    /// <summary>The VFX and materials the code handed the builder, less those of the mod's blocks/*.json: kiln.vfx reads those
    /// at boot itself (Kiln.Blocks hands a data file's blocks to the builder too, to learn their status).</summary>
    internal static JsonObject? VfxDocument(string mod, string folder)
    {
        var data = DataBlockNames(folder);
        var doc = new JsonObject { ["format"] = 1, ["comment"] = Comment };
        foreach (var (section, blocks) in VfxNative.BakedOf(mod).OrderBy(kv => kv.Key == "vfx" ? 0 : 1))
        {
            var o = new JsonObject();
            foreach (var (name, block) in blocks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                if (!data.Contains($"{section}/{name}")) o[name] = block;
            if (o.Count > 0) doc[section] = o;
        }
        return doc.Count > 2 ? doc : null;
    }

    /// <summary>"vfx/&lt;name&gt;" and "materials/&lt;name&gt;" of every blocks/*.json in the folder.</summary>
    internal static HashSet<string> DataBlockNames(string folder)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var dir = Path.Combine(folder, "blocks");
        if (!Directory.Exists(dir)) return names;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            JsonObject? o;
            try { o = JsonNode.Parse(File.ReadAllText(file), documentOptions: Relaxed) as JsonObject; }
            catch (JsonException) { continue; }
            foreach (var section in new[] { "vfx", "materials" })
                if (o?[section] is JsonObject s)
                    foreach (var (name, _) in s) names.Add($"{section}/{name}");
        }
        return names;
    }

    private static void Write(string folder, string mod, JsonObject? quests, JsonObject? skills, JsonObject? vfx, BakeResult result, BakeOptions options)
    {
        Put(folder, QuestsFile(mod), quests, "quests (quests/generated)", result, options);
        Put(folder, SkillsFile(mod), skills, "game-tree nodes (skills)", result, options);
        Put(folder, VfxFile(mod), vfx, "VFX and materials (blocks/generated)", result, options);
    }

    /// <summary>Writes one file, removes a stale one the bake wrote, and leaves a file of the author's alone.</summary>
    private static void Put(string folder, string relative, JsonObject? doc, string what, BakeResult result, BakeOptions options)
    {
        var file = Path.Combine(folder, relative);
        var shown = relative.Replace('\\', '/');
        var exists = File.Exists(file);
        var ours = exists && IsBaked(file);
        if (exists && !ours)
        {
            if (doc is not null) result.Notes.Add($"{shown} is the mod's own file: left as it is, so its {what} are not baked");
            return;
        }
        if (doc is null)
        {
            if (!ours) return;
            if (!options.DryRun) File.Delete(file);
            result.Removed.Add(shown);
            return;
        }
        var text = doc.ToJsonString(Pretty) + "\n";
        if (ours && File.ReadAllText(file) == text)
        {
            result.Unchanged.Add(shown);
            return;
        }
        if (!options.DryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, file, true);
        }
        result.Written.Add(shown);
    }

    /// <summary>A file the bake wrote (its comment says so).</summary>
    internal static bool IsBaked(string file)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(file), documentOptions: Relaxed) is JsonObject o && (string?)o["comment"] == Comment;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}
