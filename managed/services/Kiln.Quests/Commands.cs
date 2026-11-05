// SPDX-License-Identifier: MIT
// Kiln.Quests chat commands. /quests, /quest and /talk are the text fallback for "journal": "chat" quests, the ones vanilla
// clients can play: the game's UI cannot reach those players, because a client builds it from the mod's files. A native
// quest's mod refuses them at the door. /questadmin is for admins, the console and remote tools.
namespace Kiln;

internal static class QuestCommands
{
    private static string L(UiText? t) => t?.For(Quests.ChatLanguage) ?? "";

    public static void Start()
    {
        QuestsInternals.RunAs(null, () =>
        {
            Add("quests", QuestsCmd, CommandPermission.Everyone, "", "your quests: active, available, done");
            Add("quest", QuestCmd, CommandPermission.Everyone, "<id> | abandon <id> | start <id>", "one quest's steps; give up or start it");
            Add("talk", TalkCmd, CommandPermission.Everyone, "[number | end]", "talk to the quest NPC next to you; answer a conversation");
            Add("questadmin", AdminCmd, CommandPermission.Admin,
                "list | status | reload | state <who> [quest] | start|complete|fail|reset <who> <quest> | step <who> <quest> <step> | advance <who> <quest> | signal <name> [who] [amount]",
                "Kiln quests: definitions, states, changes (who: world, a player, or synthetic:<name>)");
        });
    }

    private static void Add(string name, CommandHandler handler, CommandPermission permission, string usage, string description)
    {
        try
        {
            Commands.Register(name, handler, permission, usage, description);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            QuestsInternals.Warn($"command /{name}: {e.Message}");
        }
    }

    private static bool Self(CommandContext c, out QuestHolder h)
    {
        h = default;
        if (!c.Source.IsPlayer || c.Source.Player.IsNone)
        {
            c.Reply("only players have quests (admins: /questadmin state <who>)");
            return false;
        }
        h = QuestHolder.Of(c.Source.Player);
        return true;
    }

    private static void QuestsCmd(CommandContext c)
    {
        if (!Self(c, out var h)) return;
        var shown = 0;
        foreach (var q in Quests.Definitions)
        {
            if (NativeQuests.ShownByGame(q)) continue;  // in the game's journal
            var status = Quests.StatusOf(q, h);
            if (q.Hidden && status != QuestStatus.Active) continue;
            if (status == QuestStatus.Locked) continue;
            shown++;
            c.Reply($"{q.Id}: {L(q.Name)} [{status}]{StepLine(q, h)}");
        }
        if (shown == 0) c.Reply("no quests for you right now");
    }

    private static string StepLine(QuestDefinition q, QuestHolder h)
    {
        var p = Quests.Progress(q.FullId, h);
        if (p is not { Status: QuestStatus.Active, StepId: { } id } || q.Step(id) is not { } s) return "";
        var o = s.Objectives.Count == 1 ? s.Objectives[0] : null;
        return $": {L(s.Text)}{(o is { Count: > 1 } ? $" ({p.Counter()}/{o.Count})" : "")}";
    }

    private static void QuestCmd(CommandContext c)
    {
        if (!Self(c, out var h)) return;
        if (c.Count == 0)
        {
            QuestsCmd(c);
            return;
        }
        var op = c.Args[0].ToLowerInvariant();
        switch (op)
        {
            case "abandon":
                c.Reply(Quests.Abandon(Need(c.Arg(1)).FullId, h).ToString());
                return;
            case "start":
                var q0 = Need(c.Arg(1));
                c.Reply(q0.Giver is not null ? $"talk to {q0.Giver} about this" : Quests.Start(q0, h, false).ToString());
                return;
        }
        var q = Need(c.Args[0]);
        c.Reply($"{L(q.Name)} [{Quests.StatusOf(q, h)}]");
        if (q.Description is { } d) c.Reply(L(d));
        var p = Quests.Progress(q.FullId, h);
        if (p is null) return;
        var index = p.StepId is null ? (p.Status == QuestStatus.Done ? q.Steps.Count : -1) : p.StepIndex;
        for (var i = 0; i < q.Steps.Count && i <= index; i++)
        {
            var s = q.Steps[i];
            c.Reply($"  {(i < index ? "[x]" : "[ ]")} {L(s.Text)}");
        }
    }

    private static async Task TalkAsync(CommandContext c, QuestHolder h, int number)
    {
        var r = await Dialogues.ChooseShown(h, number).OnTick();
        if (!r.Ok) c.Reply(r.Reason);
    }

    private static void TalkCmd(CommandContext c)
    {
        if (!Self(c, out var h)) return;
        var open = Dialogues.View(h) is not null;
        if (c.Count == 0)
        {
            if (open)
            {
                _ = TalkAsync(c, h, 0);
                return;
            }
            var r = Dialogues.Talk(h);
            if (!r.Ok) c.Reply(r.Reason);
            return;
        }
        if (c.Args[0].Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            Dialogues.Close(h);
            return;
        }
        if (!open)
        {
            c.Reply("you are not in a conversation: /talk next to a quest NPC");
            return;
        }
        _ = TalkAsync(c, h, c.Int(0));
    }

    private static QuestDefinition Need(string id) => Quests.Find(id) ?? throw new CommandException($"no quest \"{id}\" (/quests lists them)");

    /// <summary>"world", a player's name, or "synthetic:&lt;name&gt;" (tests without a player).</summary>
    private static QuestHolder Who(string who)
    {
        try
        {
            return QuestHolder.Parse(who);
        }
        catch (ArgumentException e)
        {
            throw new CommandException(e.Message);
        }
    }

    private static void AdminCmd(CommandContext c)
    {
        if (c.Count == 0) throw c.Usage();
        var op = c.Args[0].ToLowerInvariant();
        switch (op)
        {
            case "list":
                foreach (var q in Quests.Definitions) c.Reply($"{q.FullId}: {L(q.Name)} ({q.Scope}, {q.Journal}{(q.Journal == QuestJournalMode.Native && !NativeQuests.ShownByGame(q) ? " NOT BUILT: see kiln.log" : "")}, {q.Steps.Count} steps{(q.Auto ? ", auto" : "")})");
                c.Reply($"{Quests.Definitions.Count} quest(s), {Dialogues.Definitions.Count} dialogue(s), {Quests.NpcDefinitions.Count} NPC(s)");
                return;
            case "status":
                var s = Quests.Status;
                c.Reply($"{s.Quests} quests, {s.Dialogues} dialogues, {s.Npcs} NPCs, {s.Conversations} open conversations, {s.Transitions} transitions, " +
                        $"{s.Actions} actions ({s.ActionFailures} failed), waypoints {(s.Waypoints ? "on" : "off")}, {s.Errors} load errors");
                foreach (var e in s.LoadErrors) c.Reply("  " + e);
                return;
            case "reload":
                c.Reply($"{Quests.Reload()} quest(s) from files");
                return;
            case "state":
            {
                var h = Who(c.Arg(1));
                if (c.Count > 2)
                {
                    var q = Need(c.Args[2]);
                    c.Reply($"{q.FullId}: {Quests.Progress(q.FullId, h)?.ToString() ?? Quests.StatusOf(q, h).ToString()}");
                    return;
                }
                foreach (var p in Quests.ProgressOf(h)) c.Reply(p.ToString());
                return;
            }
            case "start":
                c.Reply(Quests.Start(Need(c.Arg(2)).FullId, Who(c.Arg(1)), true).ToString());
                return;
            case "complete":
                c.Reply(Quests.Complete(Need(c.Arg(2)).FullId, Who(c.Arg(1))).ToString());
                return;
            case "fail":
                c.Reply(Quests.Fail(Need(c.Arg(2)).FullId, Who(c.Arg(1)), "admin").ToString());
                return;
            case "reset":
                c.Reply(Quests.Reset(Need(c.Arg(2)).FullId, Who(c.Arg(1))).ToString());
                return;
            case "step":
                c.Reply(Quests.SetStep(Need(c.Arg(2)).FullId, Who(c.Arg(1)), c.Arg(3)).ToString());
                return;
            case "advance":
                _ = AdvanceAsync(c, Need(c.Arg(2)).FullId, Who(c.Arg(1)));
                return;
            case "signal":
                Quests.Signal(c.Arg(1), c.Count > 2 ? Who(c.Args[2]) : null, c.Count > 3 ? c.Int(3) : 1);
                c.Reply("sent");
                return;
            default:
                throw c.Usage($"unknown action '{op}'");
        }
    }

    private static async Task AdvanceAsync(CommandContext c, string quest, QuestHolder h)
    {
        var r = await Quests.Advance(quest, h, null, true).OnTick();
        c.Reply(r.ToString());
    }
}
