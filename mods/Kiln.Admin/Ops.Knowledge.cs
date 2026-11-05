// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln.Admin;

/// <summary>Knowledge and vanilla-quest ops over kiln.knowledge (Kiln.Quests' <see cref="Knowledge"/> and
/// <see cref="Journal"/>; docs/kiln/services/knowledge.md "Kiln.Admin"), and the admin chat commands /knowledge and
/// /quests. Targets: "world" or a player (entity number or name). Ids: "0x…", a number or a knowledge name
/// ("npc:NPC_Workshop_Bard01.unlock", "quest:0x8755c335", "query:…").</summary>
internal static partial class Ops
{
    internal static bool RegisterKnowledgeSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterKnowledge(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"knowledge ops off: Kiln.Quests is not available ({e.GetType().Name})");
            return false;
        }
    }

    private static readonly string[] KnowledgeOps =
    [
        "knowledge_status", "knowledge_get", "knowledge_list", "knowledge_grant", "knowledge_revoke", "knowledge_set",
        "knowledge_resolve", "knowledge_npcs", "knowledge_security", "knowledge_security_set", "quests_list", "quest_state",
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterKnowledge(Bridge bridge, Logger log)
    {
        if (!Knowledge.Available)
        {
            foreach (var op in KnowledgeOps)
                bridge.Register(op, _ => throw new AdminError("knowledge (kiln.knowledge) is unavailable on this server build"));
            return false;
        }
        bridge.Register("knowledge_status", _ => KnowStatusJson());
        // {target, id|name}
        bridge.Register("knowledge_get", req =>
        {
            var target = KnowTarget(req);
            var n = KnowName(req);
            var o = new JsonObject { ["target"] = KnowTargetJson(target), ["name"] = n.Text, ["id"] = Hex(n.Id), ["query"] = n.IsQuery };
            if (n.IsQuery || n.Requirement.Type != KnowledgeQueryType.SimpleBool || n.Requirement.CompareValue != 1)
                o["holds"] = Knowledge.TryQuery(target, n.Requirement, target.IsNone ? KnowledgeMode.Shared : KnowledgeMode.Player);
            if (!n.IsQuery)
            {
                var s = KnowCall(() => Knowledge.Get(target, n.Id));
                o["known"] = s is not null;
                if (s is { } st) o["state"] = StateJson(st);
                if (Knowledge.Info(n.Id) is { } info) o["info"] = InfoJson(info);
            }
            return o;
        });
        // {target}: every set id
        bridge.Register("knowledge_list", req =>
        {
            var target = KnowTarget(req);
            var list = KnowCall(() => Knowledge.List(target));
            return new JsonObject
            {
                ["target"] = KnowTargetJson(target), ["count"] = list.Count,
                ["entries"] = new JsonArray([.. list.Select(s => (JsonNode)new JsonObject
                {
                    ["id"] = Hex(s.Id), ["value"] = s.Value, ["name"] = Knowledge.NameOf(s.Id),
                })]),
            };
        });
        // {target, id|name, value?}: value = Counter amount / Flags flag number
        bridge.RegisterAsync("knowledge_grant", async req =>
        {
            var target = KnowTarget(req);
            var id = KnowId(req);
            return ResultJson(await KnowCall(() => Knowledge.For(target).Grant(id, (uint)KnowInt(req, "value", 1, 0, int.MaxValue))));
        });
        // {target, id|name, flag?} (flag: one flag of a Flags object; default all)
        bridge.RegisterAsync("knowledge_revoke", async req =>
        {
            var target = KnowTarget(req);
            var id = KnowId(req);
            return ResultJson(await KnowCall(() => Knowledge.For(target).Revoke(id, KnowInt(req, "flag", -1, -1, 31))));
        });
        // {target, id|name, value}: exact value (direct write)
        bridge.RegisterAsync("knowledge_set", async req =>
        {
            var target = KnowTarget(req);
            var id = KnowId(req);
            if (req["value"] is null) throw new AdminError("value is required");
            return ResultJson(await KnowCall(() => Knowledge.For(target).Set(id, (uint)KnowInt(req, "value", 0, 0, int.MaxValue))));
        });
        bridge.Register("knowledge_resolve", req =>
        {
            var n = KnowName(req);
            return new JsonObject
            {
                ["name"] = n.Text, ["id"] = Hex(n.Id), ["query"] = n.IsQuery, ["requirement"] = QueryJson(n.Requirement),
                ["catalogName"] = Knowledge.NameOf(n.Id, n.IsQuery),
            };
        });
        bridge.Register("knowledge_npcs", _ => new JsonArray([.. Knowledge.Npcs.Select(n => (JsonNode)new JsonObject
        {
            ["name"] = n.Name, ["npcId"] = Hex(n.NpcId), ["unlock"] = QueryJson(n.Unlock), ["unlocked"] = n.Unlocked,
            ["worldKnowledge"] = n.Unlock.IsEmpty ? null : Knowledge.TryQuery(EntityId.None, n.Unlock, KnowledgeMode.Shared),
            ["craftingAssistant"] = n.CraftingAssistant, ["workshop"] = Hex(n.Workshop), ["summonItem"] = Hex(n.SummonItem),
        })]));
        bridge.Register("knowledge_security", _ => SecurityJson(Knowledge.Security.Current));
        // {vetoClientKnowledge?, checkDialogue?, dialogueRange?, logClientKnowledge?, save?}
        bridge.Register("knowledge_security_set", req =>
        {
            var c = Knowledge.Security.Current;
            var s = c with
            {
                VetoClientKnowledge = KnowBool(req, "vetoClientKnowledge") ?? c.VetoClientKnowledge,
                CheckDialogue = KnowBool(req, "checkDialogue") ?? c.CheckDialogue,
                LogClientKnowledge = KnowBool(req, "logClientKnowledge") ?? c.LogClientKnowledge,
                DialogueRange = req["dialogueRange"] is JsonValue v && v.TryGetValue<double>(out var d) && d is >= 0 and <= 1000 ? d : c.DialogueRange,
            };
            Knowledge.Security.Apply(s, KnowBool(req, "save") ?? false);
            return SecurityJson(Knowledge.Security.Current);
        });
        bridge.Register("quests_list", _ => new JsonArray([.. Journal.Quests().Select(q => (JsonNode)QuestJson(q))]));
        // {target, quest}: per step visible / done (the game's evaluator)
        bridge.Register("quest_state", req =>
        {
            var target = KnowTarget(req);
            var qn = req["quest"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new AdminError("quest is required (\"quest:0x…\")");
            var q = Journal.Find(qn) ?? throw new AdminError($"no journal quest {qn}");
            var st = KnowCall(() => Journal.State(target, q));
            return new JsonObject
            {
                ["quest"] = QuestJson(q), ["target"] = KnowTargetJson(target), ["started"] = st.Started, ["completed"] = st.Completed,
                ["rewarded"] = st.Rewarded,
                ["steps"] = new JsonArray([.. st.Steps.Select(x => (JsonNode)new JsonObject
                {
                    ["index"] = x.Step.Index + 1, ["visible"] = x.Visible, ["done"] = x.Done, ["visibleQuery"] = QueryJson(x.Step.Visible),
                    ["doneQuery"] = QueryJson(x.Step.Done), ["marker"] = Hex(x.Step.MapMarkerTag),
                })]),
            };
        });
        var commands = Commands.RegisterAll(new KnowledgeCommands());
        log.Info($"knowledge ops on ({Knowledge.Status}); chat commands: {string.Join(", ", commands.Select(c => "/" + c.Info.Name))}");
        return true;
    }

    // ---- helpers (Know* to stay clear of the other Ops files) ----
    private static string Hex(uint v) => $"0x{v:x8}";

    private static EntityId KnowTarget(JsonObject req)
    {
        switch (req["target"] ?? req["entity"])
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (s.Equals("world", StringComparison.OrdinalIgnoreCase)) return EntityId.None;
                if (uint.TryParse(s, out var e)) return new EntityId(e);
                return Players.Find(s) is { InGame: true } p ? p.Entity : throw new AdminError($"no player '{s}' in game");
            case JsonValue v when v.TryGetValue<double>(out var d) && d >= 0 && d <= 1023 && d == Math.Floor(d):
                return new EntityId((uint)d);
            default:
                throw new AdminError("target is required: \"world\", a player entity or a player name");
        }
    }

    private static JsonNode KnowTargetJson(EntityId t) => t.IsNone ? "world" : (JsonNode)t.Value;

    private static KnowledgeName KnowName(JsonObject req)
    {
        var name = req["name"] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : null;
        name ??= req["id"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<double>(out var d) && d >= 0 && d <= uint.MaxValue && d == Math.Floor(d) => ((uint)d).ToString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(name)) throw new AdminError("id or name is required");
        return KnowCall(() => Knowledge.TryResolve(name, out var n) ? n : throw new AdminError($"unknown knowledge name '{name}'"));
    }

    private static uint KnowId(JsonObject req)
    {
        var n = KnowName(req);
        return n.IsQuery ? throw new AdminError($"'{n.Text}' is a query (a condition), not knowledge: it cannot be granted or revoked") : n.Id;
    }

    private static int KnowInt(JsonObject req, string key, int dflt, int min, int max) => req[key] switch
    {
        null => dflt,
        JsonValue v when v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d >= min && d <= max => (int)d,
        _ => throw new AdminError($"{key} must be a whole number {min}..{max}"),
    };

    private static bool? KnowBool(JsonObject req, string key) => req[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static T KnowCall<T>(Func<T> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            throw new AdminError(e.Message);
        }
    }

    private static JsonObject ResultJson(KnowledgeResult r) => new()
    {
        ["ok"] = r.Ok, ["op"] = r.Op.ToString(), ["status"] = r.Status, ["target"] = KnowTargetJson(r.Target), ["id"] = Hex(r.Id),
        ["name"] = Knowledge.NameOf(r.Id), ["before"] = r.Before, ["after"] = r.After, ["changed"] = r.Changed, ["steps"] = r.Steps,
        ["reason"] = r.Reason, ["request"] = r.Request,
    };

    private static JsonObject StateJson(KnowledgeState s) => new()
    {
        ["value"] = s.Value, ["set"] = s.IsSet, ["type"] = s.Type.ToString(), ["present"] = s.Present, ["step"] = s.Step,
    };

    private static JsonObject InfoJson(KnowledgeInfo i) => new()
    {
        ["flags"] = i.Flags.ToString(), ["worldType"] = i.WorldType.ToString(), ["playerType"] = i.PlayerType.ToString(),
        ["worldFlags"] = i.WorldFlags, ["playerFlags"] = i.PlayerFlags, ["experienceId"] = Hex(i.ExperienceId), ["skillPoints"] = i.SkillPoints,
    };

    private static JsonObject QueryJson(KnowledgeQuery q) => new()
    {
        ["id"] = Hex(q.Id), ["type"] = q.Type.ToString(), ["compare"] = q.Compare.ToString(), ["value"] = q.CompareValue,
        ["playerOnly"] = q.PlayerOnly, ["name"] = q.IsEmpty ? null : Knowledge.NameOf(q.Id, q.Type == KnowledgeQueryType.Extern),
    };

    private static JsonObject QuestJson(JournalQuest q) => new()
    {
        ["name"] = q.Name, ["entryId"] = Hex(q.EntryId), ["nameTag"] = Hex(q.NameTag), ["source"] = q.Source.ToString(),
        ["type"] = q.Type.ToString(), ["main"] = q.IsMain, ["tutorial"] = q.IsTutorial, ["steps"] = q.Steps.Count, ["rewards"] = q.Rewards,
    };

    private static JsonObject SecurityJson(KnowledgeSecurity s) => new()
    {
        ["vetoClientKnowledge"] = s.VetoClientKnowledge, ["checkDialogue"] = s.CheckDialogue, ["dialogueRange"] = s.DialogueRange,
        ["logClientKnowledge"] = s.LogClientKnowledge, ["allowedIds"] = s.AllowedIds,
    };

    private static JsonObject KnowStatusJson()
    {
        var s = Knowledge.Status;
        if (s is null) return new JsonObject { ["available"] = false };
        return new JsonObject
        {
            ["available"] = true, ["knowledge"] = s.Knowledge, ["changes"] = s.Changes, ["journal"] = s.Journal, ["npc"] = s.Npc,
            ["dialogue"] = s.Dialogue, ["clientInput"] = s.ClientInput, ["program"] = s.Program, ["world"] = s.World, ["applying"] = s.Applying,
            ["worldObjects"] = s.WorldObjects, ["playerObjects"] = s.PlayerObjects, ["worldEntries"] = s.WorldEntries, ["players"] = s.Players,
            ["npcs"] = s.Npcs, ["quests"] = s.Quests, ["queries"] = s.Queries, ["pending"] = s.Pending, ["applied"] = s.Applied,
            ["failed"] = s.Failed, ["changesSeen"] = s.ChangesSeen, ["clientOps"] = s.ClientOps, ["clientVetoed"] = s.ClientVetoed,
            ["dialogueEvents"] = s.DialogueEvents, ["dialogueHeld"] = s.DialogueHeld, ["dialogueDropped"] = s.DialogueDropped,
            ["queueMax"] = s.QueueMax, ["reason"] = s.Reason,
        };
    }
}

/// <summary>Admin chat commands for knowledge and vanilla quests (permission level Admin; nodes kiln-admin.command.*).</summary>
internal sealed class KnowledgeCommands
{
    private static EntityId Target(string who) =>
        who.Equals("world", StringComparison.OrdinalIgnoreCase) ? EntityId.None
        : Players.Find(who) is { InGame: true } p ? p.Entity
        : throw new CommandException($"'{who}' is neither \"world\" nor a player in game");

    [Command("knowledge", Description = "knowledge: /knowledge <world|player> get|grant|revoke <id or name> [value|flag]",
        Permission = CommandPermission.Admin)]
    private async Task KnowledgeCmd(CommandContext c, string who, [Choice("get", "grant", "revoke", "count")] string action, string name = "",
        int value = -1)
    {
        var target = Target(who);
        var scope = Knowledge.For(target);
        try
        {
            switch (action.ToLowerInvariant())
            {
                case "count":
                    c.Reply($"{scope}: {scope.List().Count} set ids");
                    return;
                case "get":
                    var n = Knowledge.Resolve(name);
                    var s = n.IsQuery ? null : scope.Get(n.Id);
                    c.Reply($"{n}: {(n.IsQuery ? $"holds {Knowledge.TryQuery(target, n.Requirement)?.ToString() ?? "?"}" : s?.ToString() ?? "not an object of this target")}");
                    return;
                case "grant":
                    var rg = await scope.Grant(name, value < 0 ? 1u : (uint)value);
                    c.Reply($"{scope}: {rg}");
                    return;
                case "revoke":
                    var rr = await scope.Revoke(name, value);
                    c.Reply($"{scope}: {rr}");
                    return;
                default:
                    throw new CommandException("action: get, grant, revoke or count");
            }
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            throw new CommandException(e.Message);
        }
    }

    [Command("journal", Description = "vanilla journal quests: /journal <world|player> [quest:0x…]", Permission = CommandPermission.Admin)]
    private void QuestsCmd(CommandContext c, string who, string quest = "")
    {
        var target = Target(who);
        if (quest.Length > 0)
        {
            var q = Journal.Find(quest) ?? throw new CommandException($"no journal quest {quest}");
            c.Reply(Journal.State(target, q).ToString());
            return;
        }
        var states = Journal.States(target);
        c.Reply($"{(target.IsNone ? "world" : who)}: {states.Count(s => s.Started)} of {states.Count} quests started, " +
                $"{states.Count(s => s.Completed == true)} complete");
    }
}
