// SPDX-License-Identifier: MIT
// Lowering of Mod.OnBoot's staged definitions into the boot generation's IR (chapter 08 §4.3 steps 1-3, §5), and their runtime activation.
//
// Lowering. Kiln.Host calls LowerOwner (by reflection: Kiln.Host does not reference Kiln.Blocks) once per author, after its OnBoot, with the
// staged canonical JSON. Each definition compiles through the normal Compiler.Lower into a scratch ledger while Lowered.Sink is a BootSink and
// Lowered._bootLowering is set: what a runtime Define registers with the services becomes documents of the collectors instead
//   program  -> assets   {"entries": [{"kind": "resource", "guid", "type": "0xd4922d88", "mode": "any", "data": "<base64>"}]}
//   clone    -> content  {"op": "clone", "section": "templates"|"sequences"|"perks"|"buffs", "definition": {...}}
//   item     -> content  {"op": "define", "kind": "items", "definition": NewContent.ToJson(item)}
//   recipe   -> content  {"op": "define", "kind": "recipes", "definition": NewContent.ToJson(recipe)}
//   a game-tree talent's node -> skills {"nodes": [node]}
// and nothing reaches a service. Ids come from the same BlockIds rules as a runtime Define, so both give the same GUIDs. A data file of the mod
// with the same kind and name wins: the staged definition is left out with a ShadowedByFile warning.
//
// Activation. A committed boot mod's runtime instance (Kiln.Host after its OnLoad, every activation) defines the committed definitions again
// into its runtime ledger with a sink that registers nothing (the generation did): the runtime parts (rules applied at content ready, late
// programs, icons, carriers) run as for any block, and BootDefinitionsCommittedEvent goes out.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class BootLowering
{
    /// <summary>Set on the tick thread while a committed boot mod's definitions are defined again for its runtime instance.</summary>
    [ThreadStatic] internal static bool Activating;

    private static readonly object Gate = new();
    private static readonly Queue<string> Pending = new();
    private static IDisposable? _tick;

    static BootLowering() => BootGeneration.Activated = Activate;

    /// <summary>One author's staged definitions -> the collectors' documents. Request: {"id", "clients", "dir", "staged": [{"kind", "key",
    /// "json"}]}. Answer: {"ok", "reason"?, "docs": {"assets": [...], "content": [...], "skills": [...]}, "definitions": ["kind/key"],
    /// "issues": [{"kind", "key", "code", "reason"}]}. A definition that does not compile refuses the author (ok false, its reason).</summary>
    internal static string LowerOwner(string request)
    {
        var req = JsonNode.Parse(request)!.AsObject();
        var id = (string)req["id"]!;
        var mod = new ModBlocks(id, 0) { ManifestClients = (string?)req["clients"] };
        var shadow = DataFileNames((string?)req["dir"]);
        var sink = new BootSink();
        var definitions = new JsonArray();
        var issues = new JsonArray();
        var previousSink = Lowered._sinkOverride;
        Lowered._sinkOverride = sink;
        Lowered._bootLowering = true;
        try
        {
            foreach (var s in req["staged"]!.AsArray())
            {
                var kind = (string)s!["kind"]!;
                var key = (string)s["key"]!;
                if (shadow.Contains((kind, key)))
                {
                    issues.Add(new JsonObject { ["kind"] = kind, ["key"] = key, ["code"] = "ShadowedByFile",
                        ["reason"] = $"blocks/*.json of {id} defines {kind}/{key} too: the data file wins" });
                    continue;
                }
                var type = BootDefinitions.Kinds.First(k => k.Value == kind).Key;
                sink.Key = key;
                // a resource patch is not compiled: its record is the patches collector's document, which checks it in prepare
                if (kind == BootDefinitions.ResourcePatches)
                {
                    try
                    {
                        var patch = JsonSerializer.Deserialize<ResourcePatchBlock>((string)s["json"]!, BlockJson.SharedOptions)!;
                        if (patch.Resource == Guid.Empty || patch.Type.Length == 0 || patch.Set.Count == 0)
                            return new JsonObject { ["ok"] = false, ["reason"] = $"{kind}/{key}: a resource patch names a resource, its type and at least one field to set" }.ToJsonString();
                        sink.Patch(key, JsonNode.Parse((string)s["json"]!)!.AsObject());
                    }
                    catch (JsonException e)
                    {
                        return new JsonObject { ["ok"] = false, ["reason"] = $"{kind}/{key}: {e.Message}" }.ToJsonString();
                    }
                    definitions.Add($"{kind}/{key}");
                    continue;
                }
                try
                {
                    var block = (IBlock)JsonSerializer.Deserialize((string)s["json"]!, type, BlockJson.SharedOptions)!;
                    var (handle, low, _) = Lower(mod, key, block, type);
                    low.Commit();
                    mod.Handles[key] = handle;
                    if (low.Json["node"] is JsonObject node) sink.Skills(key, node);
                }
                catch (Exception e) when (e is BlockError or JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    return new JsonObject { ["ok"] = false, ["reason"] = $"{kind}/{key}: {e.Message}" }.ToJsonString();
                }
                definitions.Add($"{kind}/{key}");
            }
        }
        finally
        {
            Lowered._sinkOverride = previousSink;
            Lowered._bootLowering = false;
        }
        return new JsonObject { ["ok"] = true, ["docs"] = sink.Docs(), ["definitions"] = definitions, ["issues"] = issues }.ToJsonString();
    }

    private static (BlockHandle, Lowered, string) Lower(ModBlocks mod, string key, IBlock block, Type type) => block switch
    {
        StatusBlock b => Compiler.Lower(mod, key, b),
        EffectBlock b => Compiler.Lower(mod, key, b),
        ItemBlock b => Compiler.Lower(mod, key, b),
        SpellBlock b => Compiler.Lower(mod, key, b),
        TalentBlock b => Compiler.Lower(mod, key, b),
        _ => throw new ArgumentException($"{type.Name} is not a boot kind"),
    };

    private static void Define(ModBlocks mod, string key, IBlock block)
    {
        switch (block)
        {
            case StatusBlock b: Compiler.Define(mod, key, b); break;
            case EffectBlock b: Compiler.Define(mod, key, b); break;
            case ItemBlock b: Compiler.Define(mod, key, b); break;
            case SpellBlock b: Compiler.Define(mod, key, b); break;
            case TalentBlock b: Compiler.Define(mod, key, b); break;
            default: throw new ArgumentException($"{block.GetType().Name} is not a boot kind");
        }
    }

    /// <summary>The (kind, name) pairs the mod's blocks/*.json define: those win over staged code.</summary>
    private static HashSet<(string, string)> DataFileNames(string? dir)
    {
        var names = new HashSet<(string, string)>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(Path.Combine(dir, "blocks"))) return names;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(dir, "blocks"), "*.json"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject doc) continue;
                foreach (var (section, value) in doc)
                    if (value is JsonObject entries)
                        foreach (var (name, _) in entries) names.Add((section, name));
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
        return names;
    }

    /// <summary>The registrations of a boot lowering, as the collectors' documents.</summary>
    internal sealed class BootSink : Lowered.ILoweredSink
    {
        public string Key = "";
        private readonly JsonArray _assets = [], _content = [], _skills = [], _patches = [];
        private int _n;

        private string Next() => $"{Key}#{++_n}";

        public void Program(ulong owner, Guid guid, byte[] blob) =>
            _assets.Add(Doc(new JsonObject { ["entries"] = new JsonArray(new JsonObject
            {
                ["kind"] = "resource", ["guid"] = guid.ToString(), ["type"] = "0xd4922d88", ["mode"] = "any", ["data"] = Convert.ToBase64String(blob),
            }) }));

        public void Clone(ulong owner, string section, JsonObject def) =>
            _content.Add(Doc(new JsonObject { ["op"] = "clone", ["section"] = section, ["definition"] = def.DeepClone() }));

        public void Item(ulong owner, ItemDefinition d) =>
            _content.Add(Doc(new JsonObject { ["op"] = "define", ["kind"] = "items", ["definition"] = NewContent.ToJson(d) }));

        public void Recipe(ulong owner, RecipeDefinition d) =>
            _content.Add(Doc(new JsonObject { ["op"] = "define", ["kind"] = "recipes", ["definition"] = NewContent.ToJson(d) }));

        public void Skills(string key, JsonObject node) =>
            _skills.Add(new JsonObject { ["key"] = key, ["document"] = new JsonObject { ["nodes"] = new JsonArray(node.DeepClone()) } });

        public void Patch(string key, JsonObject document) => _patches.Add(new JsonObject { ["key"] = key, ["document"] = document.DeepClone() });

        private JsonObject Doc(JsonObject document) => new() { ["key"] = Next(), ["document"] = document };

        public JsonObject Docs() => new()
        {
            ["assets"] = _assets.DeepClone(), ["content"] = _content.DeepClone(), ["skills"] = _skills.DeepClone(), ["patches"] = _patches.DeepClone(),
        };
    }

    /// <summary>Registers nothing: the boot generation did (a committed definition defined again for its runtime instance).</summary>
    private sealed class NoSink : Lowered.ILoweredSink
    {
        public void Program(ulong owner, Guid guid, byte[] blob) { }
        public void Clone(ulong owner, string section, JsonObject def) { }
        public void Item(ulong owner, ItemDefinition d) { }
        public void Recipe(ulong owner, RecipeDefinition d) { }
    }

    // ---- activation ----

    /// <summary>Kiln.Host: a committed boot mod's runtime instance loaded. Done on the next pass of Kiln.Blocks' tick.</summary>
    internal static void Activate(string modId)
    {
        lock (Gate)
        {
            Pending.Enqueue(modId);
            _tick ??= BlocksBridge.AddTick("kiln blocks boot", OnTick);
        }
    }

    private static void OnTick(ulong step)
    {
        while (true)
        {
            string id;
            lock (Gate)
            {
                if (Pending.Count == 0) return;
                id = Pending.Dequeue();
            }
            ActivateNow(id);
        }
    }

    internal static void ActivateNow(string id)
    {
        var committed = BootGeneration.Committed(id);
        if (committed.Count == 0) return;
        var mod = Blocks.ModFor(id, () => BlocksBridge.ServiceOwner($"blocks:{id}"));
        if (mod.Owner == 0) mod.Owner = BlocksBridge.ServiceOwner($"blocks:{id}");
        var previous = Lowered._sinkOverride;
        Lowered._sinkOverride = new NoSink();
        Activating = true;
        var done = new List<string>();
        try
        {
            foreach (var s in committed)
            {
                var type = BootDefinitions.Kinds.FirstOrDefault(k => k.Value == s.Kind).Key;
                if (type == null) continue;
                if (s.Kind == BootDefinitions.ResourcePatches)
                {
                    done.Add($"{s.Kind}/{s.Key}");  // written by kiln.content when the game's resources came up: nothing to define
                    continue;
                }
                try
                {
                    var block = (IBlock)JsonSerializer.Deserialize(s.Json, type, BlockJson.SharedOptions)!;
                    Define(mod, s.Key, block);
                    done.Add($"{s.Kind}/{s.Key}");
                }
                catch (Exception e)
                {
                    var why = e.Message;
                    lock (mod.Errors) mod.Errors.Add($"boot definition {s.Kind}/{s.Key}: {why}");
                    BlocksBridge.Log(LogLevel.Error, $"blocks: {id}: boot definition {s.Kind}/{s.Key} did not activate: {why}");
                }
            }
        }
        finally
        {
            Lowered._sinkOverride = previous;
            Activating = false;
        }
        BlocksBridge.Log(LogLevel.Info, $"blocks: {id}: {done.Count} boot definition(s) of generation {BootGeneration.Number} active for this runtime instance");
        Events.Post(new BootDefinitionsCommittedEvent(id, BootGeneration.Number, done));
    }
}
