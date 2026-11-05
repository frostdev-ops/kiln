// SPDX-License-Identifier: MIT
// Kiln.Quests: mod folder quests/*.json, format 1 (schema/kiln-quests.schema.json, docs/kiln/modding/quests.md). Comments
// and trailing commas are allowed. Unknown keys are ignored here (kiln lint warns about them): Kiln.Quests/Native reads
// its own keys (npcs.*.place, voiceovers, ...) from the same files.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>What one mod folder's quests/*.json define.</summary>
internal sealed class QuestPack
{
    public readonly List<QuestDefinition> Quests = new();
    public readonly List<DialogueDefinition> Dialogues = new();
    public readonly List<QuestNpcDefinition> Npcs = new();
    public readonly List<string> Errors = new();
}

internal static class QuestJson
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static QuestPack LoadDirectory(string modDirectory)
    {
        var pack = new QuestPack();
        var dir = Path.Combine(modDirectory, "quests");
        if (!Directory.Exists(dir)) return pack;
        foreach (var f in Directory.GetFiles(dir).Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            try
            {
                Read(JsonNode.Parse(File.ReadAllText(f), documentOptions: Options) as JsonObject ?? throw new InvalidDataException("the file is not a JSON object"), name, pack);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                pack.Errors.Add($"{name}: {e.Message}");
            }
        }
        return pack;
    }

    /// <summary>Reads one file's object into the pack (a bad entry is reported and skipped; the others load).</summary>
    public static void Read(JsonObject root, string file, QuestPack pack)
    {
        var format = root["format"] is JsonValue fv && fv.TryGetValue<int>(out var fi) ? fi : 0;
        if (format != 1) throw new InvalidDataException($"\"format\": {root["format"]?.ToJsonString() ?? "missing"} (this Kiln reads 1)");
        Section(root, "npcs", file, pack, (id, o) => pack.Npcs.Add(Npc(id, o)));
        Section(root, "quests", file, pack, (id, o) => pack.Quests.Add(Quest(id, o)));
        Section(root, "dialogues", file, pack, (id, o) => pack.Dialogues.Add(Dialogue(id, o)));
    }

    private static void Section(JsonObject root, string key, string file, QuestPack pack, Action<string, JsonObject> read)
    {
        if (root[key] is null) return;
        if (root[key] is not JsonObject s)
        {
            pack.Errors.Add($"{file}: \"{key}\" must be an object");
            return;
        }
        foreach (var (id, v) in s)
        {
            try
            {
                read(id, v as JsonObject ?? throw new InvalidDataException("must be an object"));
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or FormatException or InvalidOperationException)
            {
                pack.Errors.Add($"{file}: {key}.{id}: {e.Message}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------- definitions ------
    private static QuestNpcDefinition Npc(string id, JsonObject o)
    {
        var d = new QuestNpcDefinition
        {
            Id = id,
            Template = Str(o, "template"),
            Dialogue = Str(o, "dialogue"),
            Name = OptText(o["names"] ?? o["name"]),
        };
        if (o["clone"] is { } clone && !(clone is JsonValue cv && cv.TryGetValue<bool>(out var on) && !on))
        {
            d.Clone = true;
            if (clone is JsonObject co)
            {
                if (Str(co, "guid") is { } g) d.CloneGuid = Guid.Parse(g);
                d.CloneName = Str(co, "name");
            }
        }
        if (o["place"] is JsonObject place)
        {
            d.Place = Place(place);
            if (Pos(place["at"]) is { } at) d.At = at;
        }
        else if (Pos(o["at"]) is { } at2) d.At = at2;
        if (o["talkRange"] is { } r) d.TalkRange = Number(r, "talkRange");
        return d;
    }

    private static QuestDefinition Quest(string id, JsonObject o)
    {
        var q = new QuestDefinition
        {
            Id = id,
            Name = Text(o["name"], id),
            Description = OptText(o["description"]),
            Giver = Str(o, "giver"),
            Main = Bool(o, "main", false),
            Auto = Bool(o, "auto", false),
            Repeatable = Bool(o, "repeatable", false),
            Retryable = Bool(o, "retryable", true),
            Abandonable = Bool(o, "abandonable", true),
            Hidden = Bool(o, "hidden", false),
            Source = Str(o, "source"),
            Priority = o["priority"] is { } prio ? (int)Number(prio, "priority") : null,
        };
        q.Scope = Str(o, "scope")?.ToLowerInvariant() switch
        {
            null or "player" => QuestScope.Player,
            "party" => QuestScope.Party,
            "world" => QuestScope.World,
            var s => throw new InvalidDataException($"scope \"{s}\": player, party or world"),
        };
        q.Journal = Str(o, "journal")?.ToLowerInvariant() switch
        {
            null or "native" => QuestJournalMode.Native,
            "chat" => QuestJournalMode.Chat,
            "kiln" => Retired(id),
            var s => throw new InvalidDataException($"journal \"{s}\": native (the default) or chat"),
        };
        if (o["partyRadius"] is { } pr) q.PartyRadius = Number(pr, "partyRadius");
        q.Requires = List(o["requires"], Condition);
        q.Rewards = List(o["rewards"], Action);
        q.OnStart = List(o["onStart"], Action);
        q.OnFail = List(o["onFail"], Action);
        q.Steps = List(o["steps"], Step);
        return q;
    }

    private static QuestJournalMode Retired(string id)
    {
        QuestsInternals.Warn($"quest {id}: \"journal\": \"kiln\" is gone (Kiln has no journal of its own any more): it runs in the game's journal");
        return QuestJournalMode.Native;
    }

    /// <summary>{"at": [x, y, z]} and/or {"near": "player" | npc id | [x, y, z], "distance": metres | [min, max], "margin": m, "inBase": bool, "exact": bool}.</summary>
    private static QuestPlace Place(JsonObject o)
    {
        var p = new QuestPlace { At = Pos(o["at"]), InBase = Bool(o, "inBase", false), Exact = Bool(o, "exact", false) };
        if (o["near"] is { } near)
        {
            if (near is JsonValue v && v.TryGetValue<string>(out var name))
            {
                if (name.Equals("player", StringComparison.OrdinalIgnoreCase)) p.NearPlayer = true;
                else p.NearNpc = name;
            }
            else p.At = Pos(near) ?? throw new InvalidDataException("near: \"player\", an NPC's id, or [x, y, z]");
        }
        if (p.At is null && p.NearNpc is null) p.NearPlayer = true;
        if (o["distance"] is JsonArray d && d.Count == 2)
        {
            p.MinDistance = Number(d[0]!, "distance");
            p.MaxDistance = Number(d[1]!, "distance");
        }
        else if (o["distance"] is { } one) p.MinDistance = p.MaxDistance = Number(one, "distance");
        if (o["margin"] is { } margin) p.BaseMargin = Number(margin, "margin");
        return p;
    }

    private static QuestSpawn StepSpawn(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new InvalidDataException("a step's spawn must be an object");
        var s = new QuestSpawn
        {
            Template = Need(o, "template", "spawn"), Count = Int(o, "count", 1), Tag = Str(o, "tag"), Level = Int(o, "level", 0), Retries = Int(o, "retries", 5),
        };
        if (o["spread"] is { } sp) s.Spread = Number(sp, "spread");
        if (o["near"] is not null || o["at"] is not null) s.Place = Place(o);
        return s;
    }

    private static QuestStep Step(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new InvalidDataException("a step must be an object");
        var s = new QuestStep
        {
            Id = Str(o, "id") ?? throw new InvalidDataException("a step needs an \"id\""),
            Text = Text(o["text"], ""),
            Title = OptText(o["title"]),
            Voice = Str(o, "voice"),
            RecommendedLevel = Int(o, "recommendedLevel", 0),
            OnEnter = List(o["onEnter"], Action),
            OnDone = List(o["onDone"], Action),
        };
        if (o["objective"] is { } single) s.Objectives.Add(Objective(single));
        s.Objectives.AddRange(List(o["objectives"], Objective));
        if (o["marker"] is JsonObject m)
            s.Marker = new QuestMarker(Pos(m["at"]), m["radius"] is { } r ? Number(r, "radius") : 0, Bool(m, "waypoint", true), OptText(m["name"]), Str(m, "base"));
        else if (o["marker"] is JsonValue mv && mv.TryGetValue<bool>(out var mark) && !mark) s.NoMarker = true;
        if (o["site"] is JsonObject site) s.Site = Place(site);
        if (o["spawn"] is JsonArray many) s.Spawns = List(many, StepSpawn);
        else if (o["spawn"] is JsonObject one) s.Spawns.Add(StepSpawn(one));
        return s;
    }

    private static QuestObjective Objective(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new InvalidDataException("an objective must be an object");
        var text = OptText(o["text"]);
        QuestObjective r;
        if (o["kill"] is JsonObject k)
        {
            r = new QuestObjective { Kind = QuestObjectiveKind.Kill, Template = Str(k, "template"), Family = Str(k, "family"), Tag = Str(k, "tag"), Count = Int(k, "count", 1) };
            if (r.Template is null && r.Family is null && r.Tag is null) throw new InvalidDataException("kill needs template, family or tag");
            r.Near = Pos(k["near"]);
            if (k["radius"] is { } kr) r.Radius = Number(kr, "radius");
            r.CountDestroyed = Bool(k, "countDestroyed", false);
        }
        else if (o["collect"] is JsonObject c)
            r = new QuestObjective { Kind = QuestObjectiveKind.Collect, Item = Need(c, "item", "collect"), Count = Int(c, "count", 1) };
        else if (o["deliver"] is JsonObject d)
            r = new QuestObjective { Kind = QuestObjectiveKind.Deliver, Item = Need(d, "item", "deliver"), Count = Int(d, "count", 1), To = Str(d, "to") };
        else if (o["reach"] is JsonObject a)
        {
            r = new QuestObjective { Kind = QuestObjectiveKind.Reach, At = Pos(a["at"]) ?? Pos(a["position"]), Offset = Pos(a["offset"]), Marker = Str(a, "marker"), Site = Bool(a, "site", false) };
            if (r.At is null && r.Offset is null && r.Marker is null && !r.Site) throw new InvalidDataException("reach needs at, offset, marker or site");
            if (a["radius"] is { } ar) r.Radius = Number(ar, "radius");
        }
        else if (o["talk"] is JsonObject t)
            r = new QuestObjective { Kind = QuestObjectiveKind.Talk, Npc = Need(t, "npc", "talk"), Line = Str(t, "line"), Node = Str(t, "node") };
        else if (o["craft"] is JsonObject cr)
        {
            r = new QuestObjective { Kind = QuestObjectiveKind.Craft, Item = Str(cr, "item"), Recipe = Str(cr, "recipe"), Count = Int(cr, "count", 1) };
            if (r.Item is null && r.Recipe is null) throw new InvalidDataException("craft needs item or recipe");
        }
        else if (o["build"] is JsonObject b)
            r = new QuestObjective { Kind = QuestObjectiveKind.Build, Item = Need(b, "prop", "build"), Count = Int(b, "count", 1) };
        else if (Str(o, "knowledge") is { } name)
            r = new QuestObjective { Kind = QuestObjectiveKind.Knowledge, Knowledge = name };
        else if (o["knowledge"] is JsonObject kn)
            r = new QuestObjective { Kind = QuestObjectiveKind.Knowledge, Knowledge = Need(kn, "id", "knowledge") };
        else if (Str(o, "custom") is { } signal)
            r = new QuestObjective { Kind = QuestObjectiveKind.Custom, Signal = signal, Count = Int(o, "count", 1) };
        else if (o["custom"] is JsonObject cu)
            r = new QuestObjective { Kind = QuestObjectiveKind.Custom, Signal = Need(cu, "signal", "custom"), Count = Int(cu, "count", 1) };
        else
            throw new InvalidDataException("an objective needs one of kill, collect, deliver, reach, talk, craft, build, knowledge, custom");
        r.Text = text;
        return r;
    }

    private static QuestCondition Condition(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new InvalidDataException("a condition must be an object");
        if (o["all"] is JsonArray all) return new QuestCondition { Kind = QuestConditionKind.All, Children = List(all, Condition) };
        if (o["any"] is JsonArray any) return new QuestCondition { Kind = QuestConditionKind.Any, Children = List(any, Condition) };
        if (o["not"] is { } not) return new QuestCondition { Kind = QuestConditionKind.Not, Children = [Condition(not)] };
        if (Str(o, "knowledge") is { } k)
            return o["is"] is JsonValue kv && kv.TryGetValue<bool>(out var kb) && !kb
                ? new QuestCondition { Kind = QuestConditionKind.Not, Children = [QuestCondition.HasKnowledge(k)] }
                : QuestCondition.HasKnowledge(k);
        if (o["level"] is { } lv) return QuestCondition.Level((int)Number(lv, "level"));
        if (Str(o, "quest") is { } q) return QuestCondition.Quest(q, Str(o, "is") ?? "done");
        if (Str(o, "step") is { } s) return QuestCondition.QuestStep(s, Str(o, "is") ?? "done");
        if (Str(o, "item") is { } i) return QuestCondition.HasItem(i, Int(o, "count", 1));
        if (Str(o, "predicate") is { } p) return new QuestCondition { Kind = QuestConditionKind.Predicate, Name = p };
        throw new InvalidDataException("a condition needs one of knowledge, level, quest, step, item, predicate, all, any, not");
    }

    private static QuestAction Action(JsonNode? n)
    {
        var o = n as JsonObject ?? throw new InvalidDataException("an action must be an object");
        if (o["xp"] is { } xp)
        {
            var amount = Number(xp, "xp");
            if (amount is < 0 or > uint.MaxValue) throw new InvalidDataException($"xp {amount}: 0..{uint.MaxValue}");
            return new QuestAction { Kind = QuestActionKind.Xp, Value = (uint)amount };
        }
        if (Str(o, "item") is { } give) return new QuestAction { Kind = QuestActionKind.Give, Name = give, Count = Int(o, "count", 1) };
        if (Str(o, "take") is { } take) return new QuestAction { Kind = QuestActionKind.Take, Name = take, Count = Int(o, "count", 1) };
        if (Str(o, "knowledge") is { } kn)
            return new QuestAction { Kind = QuestActionKind.Knowledge, Name = kn, Value = (uint)Int(o, "value", 1), Target = Str(o, "target") };
        if (Str(o, "revoke") is { } rv)
            return new QuestAction { Kind = QuestActionKind.Revoke, Name = rv, Value = (uint)(Int(o, "flag", -1) + 1), Target = Str(o, "target") };
        if (Str(o, "start") is { } st) return new QuestAction { Kind = QuestActionKind.Start, Name = st, Force = Bool(o, "force", false) };
        if (Str(o, "complete") is { } co) return new QuestAction { Kind = QuestActionKind.Complete, Name = co };
        if (Str(o, "fail") is { } fa) return new QuestAction { Kind = QuestActionKind.Fail, Name = fa };
        if (Str(o, "advance") is { } ad) return new QuestAction { Kind = QuestActionKind.Advance, Name = ad, Force = Bool(o, "force", false) };
        if (Str(o, "reset") is { } re) return new QuestAction { Kind = QuestActionKind.Reset, Name = re };
        if (Str(o, "spawn") is { } sp)
            return new QuestAction
            {
                Kind = QuestActionKind.Spawn, Name = sp, Count = Int(o, "count", 1), At = Pos(o["at"]), Offset = Pos(o["offset"]),
                Yaw = o["yaw"] is { } y ? Number(y, "yaw") : 0, Tag = Str(o, "tag"), BindNpc = Str(o, "bindNpc"), Transient = Bool(o, "transient", false),
                Exact = Bool(o, "exact", false),
            };
        if (Str(o, "despawn") is { } ds) return new QuestAction { Kind = QuestActionKind.Despawn, Name = ds };
        if (o["teleport"] is { } tp)
            return new QuestAction { Kind = QuestActionKind.Teleport, At = Pos(tp) ?? throw new InvalidDataException("teleport: [x, y, z]"), Offset = Pos(o["offset"]) };
        if (o["chat"] is { } ch) return new QuestAction { Kind = QuestActionKind.Chat, Text = Text(ch, "") };
        if (o["toast"] is { } to) return new QuestAction { Kind = QuestActionKind.Toast, Text = Text(to, ""), ToastKind = Str(o, "kind") };
        if (o["waypoint"] is { } wp)
        {
            // false clears it; [x, y, z] is a place; true with "offset" is relative to the player
            if (wp is JsonValue wv && wv.TryGetValue<bool>(out var on) && !on) return new QuestAction { Kind = QuestActionKind.Waypoint, Clear = true };
            return new QuestAction { Kind = QuestActionKind.Waypoint, At = Pos(wp), Offset = Pos(o["offset"]) };
        }
        if (Str(o, "talk") is { } tk) return new QuestAction { Kind = QuestActionKind.Talk, Name = tk, Node = Str(o, "node") };
        if (Str(o, "signal") is { } sg) return new QuestAction { Kind = QuestActionKind.Signal, Name = sg, Count = Int(o, "amount", 1) };
        if (Str(o, "call") is { } ca) return new QuestAction { Kind = QuestActionKind.Call, Name = ca };
        throw new InvalidDataException("an action needs one of xp, item, take, knowledge, revoke, start, complete, fail, advance, reset, spawn, despawn, teleport, chat, toast, waypoint, talk, signal, call");
    }

    private static DialogueDefinition Dialogue(string id, JsonObject o)
    {
        var d = new DialogueDefinition
        {
            Id = id, Start = Str(o, "start") ?? "start", Native = Bool(o, "native", true), Hint = Str(o, "hint"),
            Greet = o["greet"] is JsonValue gv && gv.TryGetValue<bool>(out var greet) ? greet : null,
        };
        foreach (var g in o["greetings"] as JsonArray ?? [])
        {
            var go = g as JsonObject ?? throw new InvalidDataException("a greeting must be an object");
            d.Greetings.Add(new DialogueGreeting { Text = Text(go["text"], ""), If = go["if"] is { } gi ? Condition(gi) : null, Voice = Str(go, "voice"), Hint = Str(go, "hint") });
        }
        foreach (var f in o["farewell"] as JsonArray ?? []) d.Farewell.Add(Text(f is JsonObject fo ? fo["text"] : f, ""));
        foreach (var t in o["transition"] as JsonArray ?? []) d.Transition.Add(Text(t is JsonObject to ? to["text"] : t, ""));
        if (o["nodes"] is not JsonObject nodes) throw new InvalidDataException("a dialogue needs \"nodes\"");
        foreach (var (nid, nv) in nodes)
        {
            var no = nv as JsonObject ?? throw new InvalidDataException($"node {nid} must be an object");
            var node = new DialogueNode
            {
                Speaker = OptText(no["speaker"]),
                Text = Text(no["text"], ""),
                Voice = Str(no, "voice"),
                Hint = Str(no, "hint"),
                Goto = Str(no, "goto"),
                Do = List(no["do"], Action),
            };
            foreach (var op in no["options"] as JsonArray ?? [])
            {
                var oo = op as JsonObject ?? throw new InvalidDataException($"node {nid}: an option must be an object");
                node.Options.Add(new DialogueOption
                {
                    Text = Text(oo["text"], "..."),
                    If = oo["if"] is { } cond ? Condition(cond) : null,
                    Do = List(oo["do"], Action),
                    Goto = Str(oo, "goto"),
                    Reply = OptText(oo["reply"]),
                    Voice = Str(oo, "voice"),
                    Once = Bool(oo, "once", false),
                    Close = Bool(oo, "close", false),
                    Hint = Str(oo, "hint"),
                });
            }
            d.Nodes[nid] = node;
        }
        return d;
    }

    // ------------------------------------------------------------------------------------------------ helpers ------
    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Need(JsonObject o, string key, string what) =>
        Str(o, key) ?? throw new InvalidDataException($"{what} needs \"{key}\"");

    private static bool Bool(JsonObject o, string key, bool dflt) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : dflt;

    private static double Number(JsonNode n, string what) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : throw new InvalidDataException($"{what} must be a number");

    private static int Int(JsonObject o, string key, int dflt)
    {
        if (o[key] is not { } n) return dflt;
        var d = Number(n, key);
        return d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : throw new InvalidDataException($"{key} must be a whole number");
    }

    private static WorldPosition? Pos(JsonNode? n)
    {
        if (n is JsonObject o && o["x"] is { } x && o["y"] is { } y && o["z"] is { } z)
            return new WorldPosition(Number(x, "x"), Number(y, "y"), Number(z, "z"));
        if (n is not JsonArray a) return null;
        if (a.Count != 3 || a.Any(e => e is null)) throw new InvalidDataException("a position is [x, y, z]");
        return new WorldPosition(Number(a[0]!, "x"), Number(a[1]!, "y"), Number(a[2]!, "z"));
    }

    private static UiText Text(JsonNode? n, string dflt) => OptText(n) ?? new UiText(dflt);

    private static UiText? OptText(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => new UiText(s),
        JsonObject o => new UiText(o.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)),
        _ => throw new InvalidDataException("a text is a string or {\"en\": \"...\", \"de\": \"...\"}"),
    };

    private static List<T> List<T>(JsonNode? n, Func<JsonNode?, T> read)
    {
        var list = new List<T>();
        if (n is null) return list;
        if (n is not JsonArray a) throw new InvalidDataException("expected a list");
        foreach (var e in a) list.Add(read(e));
        return list;
    }
}
