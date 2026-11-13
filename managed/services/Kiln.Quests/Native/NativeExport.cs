// SPDX-License-Identifier: MIT
// A quest a mod defines in C# gets the game's journal like one from a file. The game's resources are built at boot, before any
// managed mod runs (docs/re/quests.md R4), so what code defines is written down here in the file format, and kiln-quests.dll
// builds it at the next start: kiln/data/kiln-quests/native/<mod>.json. Only what the game's UI shows goes into it: objectives,
// conditions and rewards keep running from the C# objects. A mod can ship that file as quests/generated/<anything>.json, and then
// the first start has it too. Kiln.Sdk's bake (managed/Kiln.Sdk/bake) writes it into the mod at build: quests/generated/<mod>.json,
// this Document with BakedComment; at run time Write says whether what the code defines still matches it.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static class NativeExport
{
    private static readonly HashSet<string> Dirty = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The "comment" of a file Kiln.Sdk's bake wrote; how Kiln tells it from a file the author wrote.</summary>
    internal const string BakedComment = "baked by the Kiln SDK at build from what the mod defines in code: docs/kiln/modding/baked.md";

    /// <summary>Kiln.Sdk's bake runs the mod under KilnHarness and takes <see cref="Document"/> itself: nothing is written to
    /// Kiln's data folder meanwhile.</summary>
    internal static volatile bool Baking;

    /// <summary>The text of a document, as both this writer and the bake write it.</summary>
    internal static string Serialize(JsonObject doc) => doc.ToJsonString(Pretty);

    public static void Changed(string modId)
    {
        lock (Dirty)
            if (modId.Length > 0) Dirty.Add(modId);
    }

    // quests and dialogues that blocks/*.json defines ("q:<mod>:<id>", "d:<mod>:<id>"): the boot build reads them from the mod's folder itself (native/services/quests
    // native_pack.cpp), so what is written for code leaves them out, and nothing is built twice or lingers here after it is taken out of the file
    private static readonly HashSet<string> FromData = new(StringComparer.Ordinal);

    /// <summary>Kiln.Blocks: this quest (<paramref name="kind"/> 'q') or dialogue ('d') comes from a data file, or no longer does.</summary>
    internal static void DefinedInData(char kind, string fullId, bool data)
    {
        lock (Dirty)
        {
            if (data) FromData.Add($"{kind}:{fullId}");
            else FromData.Remove($"{kind}:{fullId}");
        }
    }

    private static bool InData(char kind, string fullId)
    {
        lock (Dirty) return FromData.Contains($"{kind}:{fullId}");
    }

    /// <summary>From the service tick.</summary>
    public static void Flush()
    {
        if (Baking) return;
        string[] mods;
        lock (Dirty)
        {
            if (Dirty.Count == 0) return;
            mods = Dirty.ToArray();
            Dirty.Clear();
        }
        foreach (var m in mods)
        {
            try
            {
                Write(m);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                QuestsInternals.Warn($"{m}: the quests it defines in code cannot be written for the game's journal: {e.Message}");
            }
        }
    }

    private static void Write(string mod)
    {
        var doc = Document(mod, out var missing);
        CompareBaked(mod, doc);
        var dir = Path.Combine(QuestsInternals.DataOwner.DataDirectory, "native");
        var file = Path.Combine(dir, mod + ".json");
        if (doc is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        var text = doc.ToJsonString(Pretty);
        if (!File.Exists(file) || File.ReadAllText(file) != text)
        {
            Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, file, true);
            QuestsInternals.Info($"{mod}: what it defines in code is written for the game's UI: {file}");
        }
        foreach (var q in missing)
            QuestsInternals.Warn($"{q} is defined in code, and the game's journal is built at boot: it shows from the next start on. Until then it runs with chat lines only. " +
                                 $"To have it at the first start, build the mod with Kiln.Sdk (its build bakes the file in; when it was, the build log says why it could not), " +
                                 $"or ship {file} as quests/generated/{mod}.json in the mod");
    }

    private static readonly Dictionary<string, string> Told = new(StringComparer.Ordinal);

    /// <summary>The mod's own quests/generated/&lt;mod&gt;.json when Kiln.Sdk's bake wrote it (its comment says so); null otherwise,
    /// a file the author wrote included: that one is theirs, as before.</summary>
    internal static JsonObject? BakedFile(string modDirectory, string mod)
    {
        var file = Path.Combine(modDirectory, "quests", "generated", mod + ".json");
        try
        {
            if (!File.Exists(file)) return null;
            return JsonNode.Parse(File.ReadAllText(file)) is JsonObject o && (string?)o["comment"] == BakedComment ? o : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What differs between a baked file and what the code defines now (the game's UI part): null when nothing does.</summary>
    internal static string? BakedDifference(JsonObject baked, JsonObject? now)
    {
        var diffs = new List<string>();
        foreach (var (section, what) in new[] { ("npcs", "NPC"), ("quests", "quest"), ("dialogues", "dialogue") })
        {
            var b = baked[section] as JsonObject;
            var n = now?[section] as JsonObject;
            foreach (var (id, value) in n ?? [])
            {
                if (b is null || !b.TryGetPropertyValue(id, out var was)) diffs.Add($"{what} {id} is not in it");
                else if (!JsonNode.DeepEquals(was, value)) diffs.Add($"{what} {id} differs");
            }
            foreach (var (id, _) in b ?? [])
                if (n is null || !n.ContainsKey(id)) diffs.Add($"{what} {id} is only in it");
        }
        return diffs.Count == 0 ? null : string.Join(", ", diffs.Take(6)) + (diffs.Count > 6 ? $" and {diffs.Count - 6} more" : "");
    }

    // A mod built with the bake ships what its code defined at build: the game built that at this start. Said once per outcome.
    private static void CompareBaked(string mod, JsonObject? doc)
    {
        if (Mods.Get(mod)?.Directory is not { Length: > 0 } dir || BakedFile(dir, mod) is not { } baked) return;
        var diff = BakedDifference(baked, doc);
        var key = diff ?? "";
        lock (Told)
        {
            if (Told.TryGetValue(mod, out var was) && was == key) return;
            Told[mod] = key;
        }
        if (diff is null)
            QuestsInternals.Info($"{mod}: what it defines in code matches quests/generated/{mod}.json, baked into the mod at build: the game's journal has it from the first start");
        else
            QuestsInternals.Warn($"{mod}: what it defines in code is not what was baked into the mod at build (quests/generated/{mod}.json): {diff}. At start its quests and NPCs win over what Kiln keeps " +
                                 "from code, so the game's journal shows the baked version (a dialogue shows the code's from the next start); code that defines something else after the build (from settings, other mods or the game) " +
                                 "needs KilnBake=false in the project, and a rebuild brings the file up to date");
    }

    /// <summary>The file-format document of what a mod defined in code and the game's UI shows. Null: nothing.</summary>
    internal static JsonObject? Document(string mod, out List<string> missing)
    {
        missing = [];
        var quests = new JsonObject();
        var npcs = new JsonObject();
        var dialogues = new JsonObject();
        lock (Quests.Gate)
        {
            foreach (var e in Quests.Registry.Values)
            {
                if (e.FromJson || e.Def.ModId != mod || e.Def.Journal != QuestJournalMode.Native || InData('q', e.Def.FullId)) continue;
                quests[e.Def.Id] = Quest(e.Def);
                if (NativeQuests.Quest(e.Def.FullId) is null) missing.Add(e.Def.FullId);
            }
            foreach (var (_, e) in Quests.NpcRegistry)
                if (!e.FromJson && e.Def.ModId == mod) npcs[e.Def.Id] = Npc(e.Def);
            foreach (var (_, e) in Quests.DialogueRegistry)
                if (!e.FromJson && e.Def.ModId == mod && e.Def.Native && !InData('d', e.Def.FullId)) dialogues[e.Def.Id] = Dialogue(e.Def);
        }
        if (quests.Count == 0 && npcs.Count == 0 && dialogues.Count == 0) return null;
        var doc = new JsonObject { ["format"] = 1, ["comment"] = "written by Kiln from what the mod defines in code: docs/kiln/modding/quests.md, Code" };
        if (npcs.Count > 0) doc["npcs"] = npcs;
        if (quests.Count > 0) doc["quests"] = quests;
        if (dialogues.Count > 0) doc["dialogues"] = dialogues;
        return doc;
    }

    private static JsonNode Text(UiText t)
    {
        var o = new JsonObject();
        foreach (var k in t.Languages.OrderBy(x => x, StringComparer.Ordinal)) o[k] = t.For(k);
        return o;
    }

    private static JsonArray Pos(WorldPosition p) => [p.X, p.Y, p.Z];

    private static JsonObject Place(QuestPlace p)
    {
        var o = new JsonObject();
        if (p.At is { } at) o["at"] = Pos(at);
        if (p.NearNpc is { } n) o["near"] = n;
        else if (p.NearPlayer) o["near"] = "player";
        if (p.MinDistance > 0 || p.MaxDistance > 0) o["distance"] = new JsonArray(p.MinDistance, p.MaxDistance);
        if (p.InBase) o["inBase"] = true;
        if (p.Exact) o["exact"] = true;
        return o;
    }

    private static JsonObject Quest(QuestDefinition q)
    {
        var o = new JsonObject { ["name"] = Text(q.Name) };
        if (q.Scope != QuestScope.Player) o["scope"] = q.Scope.ToString().ToLowerInvariant();
        if (q.Main) o["main"] = true;
        if (q.Giver is { } g) o["giver"] = g;
        if (q.Source is { } s) o["source"] = s;
        if (q.Priority is { } p) o["priority"] = p;
        if (q.Requires.Count > 0) o["requires"] = new JsonArray(q.Requires.Select(c => (JsonNode)Condition(c)).ToArray());
        if (q.Repeatable) o["repeatable"] = true;
        o["steps"] = new JsonArray(q.Steps.Select(x => (JsonNode)Step(x)).ToArray());
        return o;
    }

    private static JsonObject Step(QuestStep s)
    {
        var o = new JsonObject { ["id"] = s.Id, ["text"] = Text(s.Text) };
        if (s.Title is { } t) o["title"] = Text(t);
        if (s.Voice is { } v) o["voice"] = v;
        if (s.RecommendedLevel > 0) o["recommendedLevel"] = s.RecommendedLevel;
        if (s.NoMarker) o["marker"] = false;
        else if (s.Marker is { } m)
        {
            var mo = new JsonObject();
            if (m.At is { } at) mo["at"] = Pos(at);
            if (m.Name is { } mn) mo["name"] = Text(mn);
            if (m.Base is { } mb) mo["base"] = mb;
            o["marker"] = mo;
        }
        if (s.Site is { } site) o["site"] = Place(site);
        if (s.Spawns.Count > 0) o["spawn"] = new JsonArray(s.Spawns.Select(x => (JsonNode)new JsonObject { ["template"] = x.Template, ["count"] = x.Count }).ToArray());
        o["objectives"] = new JsonArray(s.Objectives.Select(x => (JsonNode)Objective(x)).ToArray());
        return o;
    }

    // what the boot build reads of an objective is its counter and its place; the rest keeps the file valid for kiln lint
    private static JsonObject Objective(QuestObjective x)
    {
        var key = x.Kind.ToString().ToLowerInvariant();
        if (x.Kind == QuestObjectiveKind.Knowledge) return new JsonObject { ["knowledge"] = x.Knowledge };
        var b = new JsonObject();
        if (x.Kind is QuestObjectiveKind.Kill or QuestObjectiveKind.Collect or QuestObjectiveKind.Deliver or QuestObjectiveKind.Craft or QuestObjectiveKind.Build or QuestObjectiveKind.Custom) b["count"] = x.Count;
        if (x.Template is { } t) b["template"] = t;
        if (x.Family is { } fam) b["family"] = fam;
        if (x.Tag is { } tag) b["tag"] = tag;
        if (x.Near is { } near) b["near"] = Pos(near);
        if (x.At is { } at) b["at"] = Pos(at);
        if (x.Offset is { } off) b["offset"] = Pos(off);
        if (x.Marker is { } mk) b["marker"] = mk;
        if (x.Site) b["site"] = true;
        if (x.Npc is { } n) b["npc"] = n;
        if (x.To is { } to) b["to"] = to;
        if (x.Item is { } item) b[x.Kind == QuestObjectiveKind.Build ? "prop" : "item"] = item;
        if (x.Recipe is { } r) b["recipe"] = r;
        if (x.Kind == QuestObjectiveKind.Custom) b["signal"] = x.Signal ?? "code";
        return new JsonObject { [key] = b };
    }

    private static JsonObject Condition(QuestCondition c)
    {
        switch (c.Kind)
        {
            case QuestConditionKind.Knowledge: return new JsonObject { ["knowledge"] = c.Name };
            case QuestConditionKind.Quest: return new JsonObject { ["quest"] = c.Name, ["is"] = c.Is ?? "done" };
            case QuestConditionKind.Step: return new JsonObject { ["step"] = c.Name, ["is"] = c.Is ?? "done" };
            case QuestConditionKind.Level: return new JsonObject { ["level"] = c.Count };
            case QuestConditionKind.Item: return new JsonObject { ["item"] = c.Name, ["count"] = c.Count };
            case QuestConditionKind.All: return new JsonObject { ["all"] = new JsonArray(c.Children.Select(x => (JsonNode)Condition(x)).ToArray()) };
            case QuestConditionKind.Any: return new JsonObject { ["any"] = new JsonArray(c.Children.Select(x => (JsonNode)Condition(x)).ToArray()) };
            case QuestConditionKind.Not: return new JsonObject { ["not"] = Condition(c.Children[0]) };
            default: return new JsonObject { ["predicate"] = c.Name ?? "code" };  // the engine evaluates it
        }
    }

    private static JsonObject Npc(QuestNpcDefinition n)
    {
        var o = new JsonObject();
        if (n.Template is { } t) o["template"] = t;
        if (n.Name is { } name) o["names"] = Text(name);
        if (n.Dialogue is { } d) o["dialogue"] = d;
        if (n.Clone)
        {
            var c = new JsonObject();
            if (n.CloneGuid is { } g) c["guid"] = g.ToString();
            if (n.CloneName is { } cn) c["name"] = cn;
            o["clone"] = c;
        }
        if (n.Place is { } p) o["place"] = Place(p);
        return o;
    }

    private static JsonObject Dialogue(DialogueDefinition d)
    {
        var nodes = new JsonObject();
        foreach (var (id, n) in d.Nodes)
        {
            var no = new JsonObject { ["text"] = Text(n.Text) };
            if (n.Voice is { } v) no["voice"] = v;
            if (n.Hint is { } h) no["hint"] = h;
            var options = new JsonArray();
            foreach (var op in n.Options)
            {
                var oo = new JsonObject { ["text"] = Text(op.Text) };
                if (op.If is { } c) oo["if"] = Condition(c);
                if (op.Goto is { } g) oo["goto"] = g;
                if (op.Reply is { } r) oo["reply"] = Text(r);
                if (op.Voice is { } ov) oo["voice"] = ov;
                if (op.Once) oo["once"] = true;
                if (op.Close) oo["close"] = true;
                if (op.Hint is { } oh) oo["hint"] = oh;
                // the boot build asks only which step a hand-in advances
                var advances = op.Do.Where(a => a.Kind == QuestActionKind.Advance && a.Name is not null).Select(a => (JsonNode)new JsonObject { ["advance"] = a.Name }).ToArray();
                if (advances.Length > 0) oo["do"] = new JsonArray(advances);
                options.Add(oo);
            }
            if (options.Count > 0) no["options"] = options;
            nodes[id] = no;
        }
        var o = new JsonObject { ["start"] = d.Start, ["nodes"] = nodes };
        if (d.Greet is { } greet) o["greet"] = greet;
        if (d.Hint is { } hint) o["hint"] = hint;
        if (d.Greetings.Count > 0)
            o["greetings"] = new JsonArray(d.Greetings.Select(g =>
            {
                var go = new JsonObject { ["text"] = Text(g.Text) };
                if (g.If is { } c) go["if"] = Condition(c);
                if (g.Voice is { } v) go["voice"] = v;
                if (g.Hint is { } h) go["hint"] = h;
                return (JsonNode)go;
            }).ToArray());
        if (d.Farewell.Count > 0) o["farewell"] = new JsonArray(d.Farewell.Select(t => (JsonNode)new JsonObject { ["text"] = Text(t) }).ToArray());
        if (d.Transition.Count > 0) o["transition"] = new JsonArray(d.Transition.Select(t => (JsonNode)new JsonObject { ["text"] = Text(t) }).ToArray());
        return o;
    }
}
