// SPDX-License-Identifier: MIT
// Kiln.Quests engine lab test (no player): prints "quest-engine-test: PASS ..." / "FAIL ..." lines to kiln.log.
//   boot 1: definitions and load errors; synthetic holders through every objective, condition and action reachable
//           without a player; events; conversations with server-side refusals; reload; a world quest whose kill objective
//           counts a real death (Combat.Kill) and a real destroy (Spawn.Destroy), with a world knowledge reward. Leaves a
//           half-done player quest and a done world quest for boot 2.
//   boot 2: the sidecar kept them; finishes the quest; puts the world back (revokes the knowledge, resets).
// Stop the server with tools/lab/lab.sh stop between boots, so it writes the save. Delete kiln/data/quest-engine-test/state.json
// to start over (on a copy of the save: the first run unlocks the Bard's knowledge until boot 2 revokes it).
using System.Text.Json.Nodes;
using Kiln;

namespace QuestEngineTest;

public sealed class QuestEngineTestMod : Mod
{
    private const string Mod = "quest-engine-test";
    private const string Logs = "Material_T1_Wood_Logs";
    private int _pass, _fail;
    private readonly List<string> _events = new();
    private string StatePath => Path.Combine(Context.DataDirectory, "state.json");
    private static readonly QuestHolder Alice = QuestHolder.Synthetic("qe-alice"), Bob = QuestHolder.Synthetic("qe-bob"), Carol = QuestHolder.Synthetic("qe-carol");

    private void Info(string s) => Log.Info("quest-engine-test: INFO " + s);

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"quest-engine-test: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task<bool> WaitFor(Func<bool> done, int steps = 200)
    {
        for (var i = 0; i < steps; i++)
        {
            if (done()) return true;
            await Context.Scheduler.NextTick();
        }
        return done();
    }

    public override void OnLoad()
    {
        Quests.RegisterAction("qe.reward", _ =>
        {
            _events.Add("called qe.reward");
            return Task.CompletedTask;
        });
        Quests.RegisterPredicate("qe.gate", c => c.Holder == Alice);
        Events.On<QuestStartedEvent>(e =>
        {
            _events.Add($"started {e.Quest.Id} {e.Holder.Key}");
            if (e.Quest.Id == "cancel_me") e.Cancel();
        });
        Events.On<QuestStepEvent>(e => _events.Add($"step {e.Quest.Id}.{e.StepId} -> {e.Next?.Id ?? "(end)"}"));
        Events.On<QuestCompletedEvent>(e => _events.Add($"completed {e.Quest.Id}"));
        Events.On<QuestFailedEvent>(e => _events.Add($"failed {e.Quest.Id}: {e.Reason}"));
        Events.On<QuestStateChangedEvent>(e => _events.Add($"changed {e.Quest.Id} {e.OldStatus}->{e.Status} {e.StepId}"));
        Events.On<QuestObjectiveProgressEvent>(e => _events.Add($"progress {e.Quest.Id}.{e.Step.Id}[{e.Index}] {e.OldValue}->{e.Value}/{e.Target}"));
        _ = RunGuarded();
    }

    private async Task RunGuarded()
    {
        try
        {
            if (!await WaitFor(() => Lifecycle.IsServerRunning && Knowledge.Status is { Applying: true }, 15000))
            {
                Check(false, "the world did not run (or kiln.knowledge never applied) within 10 minutes");
                return;
            }
            await Steps(30);
            var boot = ReadBoot();
            Info($"boot {boot}");
            if (boot == 1) await Boot1();
            else if (boot == 2) await Boot2();
            else Info("all boots done: delete kiln/data/quest-engine-test/state.json to run again");
            WriteBoot(boot + 1);
        }
        catch (Exception e)
        {
            Check(false, $"the test threw: {e}");
        }
        Log.Info($"quest-engine-test: done: {_pass} PASS, {_fail} FAIL");
    }

    private int ReadBoot()
    {
        try
        {
            return File.Exists(StatePath) && JsonNode.Parse(File.ReadAllText(StatePath))?["boot"]?.GetValue<int>() is { } b ? b : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private void WriteBoot(int boot)
    {
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(StatePath, new JsonObject { ["boot"] = boot }.ToJsonString());
    }

    private string Full(string id) => $"{Mod}:{id}";

    private bool Saw(string text) => _events.Any(e => e.Contains(text, StringComparison.Ordinal));

    private QuestStatus St(string id, QuestHolder h) => Quests.StatusOf(Full(id), h);

    private string? StepOf(string id, QuestHolder h) => Quests.Progress(Full(id), h)?.StepId;

    private void Fresh(string id, params QuestHolder[] holders)
    {
        foreach (var h in holders) Quests.Reset(Full(id), h);
    }

    // ------------------------------------------------------------------------------------------------ boot 1 -------
    private async Task Boot1()
    {
        await Definitions();
        await LockedAndChain();
        await Conversations();
        await GiverWithoutDialogue();
        await PartyQuest();
        await FailRetryRepeat();
        await CodeDefinitions();
        await Reloading();
        await WorldQuest();
        await NpcsInTheWorld();
        Persist();
        Waypoints();
    }

    // kiln.log of lab human, 23:57: a task another service completes continues on the thread pool, unless it comes back. Everything
    // after it in an action list then failed in Game.AssertTickThread, half way through: rewards went out, the mirror did not.
    private async Task ActionsStayOnTheTickThread()
    {
        var threads = new List<bool>();
        Quests.RegisterAction("qe.offthread", async _ =>
        {
            await Task.Delay(30).ConfigureAwait(false);  // a completion on the thread pool, like Items.Take's
            Check(!Game.IsTickThread, "the test's own action really is off the tick thread here");
        });
        Quests.RegisterAction("qe.where", _ =>
        {
            threads.Add(Game.IsTickThread);
            return Task.CompletedTask;
        });
        Quests.Define(new QuestDefinition
        {
            Id = "offthread",
            Name = "Off the thread",
            Steps = [new QuestStep { Id = "a", Text = "x", Objectives = [QuestObjective.Custom("qe.offthread.never")],
                OnEnter =
                [
                    new QuestAction { Kind = QuestActionKind.Call, Name = "qe.offthread" },
                    new QuestAction { Kind = QuestActionKind.Call, Name = "qe.where" },
                    new QuestAction { Kind = QuestActionKind.Advance, Name = $"{Full("offthread")}.a", Force = true },
                    new QuestAction { Kind = QuestActionKind.Call, Name = "qe.where" },
                ] }],
        });
        Fresh("offthread", Alice);
        Check(Quests.Start(Full("offthread"), Alice).Ok, "a quest whose actions wait on another thread starts");
        Check(await WaitFor(() => threads.Count == 2, 120), $"the actions after the wait all ran: {threads.Count} of 2");
        Check(threads.All(x => x), $"actions after a wait run on the tick thread: {string.Join(",", threads)}");
        Check(St("offthread", Alice) == QuestStatus.Done, $"an advance after a wait finishes the quest: {St("offthread", Alice)}");
        Check(Quests.RecentActions.All(l => !(l.Contains("offthread", StringComparison.Ordinal) && l.Contains("FAILED", StringComparison.Ordinal))), "no action failed");
        Fresh("offthread", Alice);
    }

    private async Task Definitions()
    {
        var s = Quests.Status;
        Info($"status: {s.Quests} quests, {s.Dialogues} dialogues, {s.Npcs} NPCs, waypoints {s.Waypoints}");
        Check(Quests.Find(Full("chain")) is { Steps.Count: 10 }, "quests/test.json loaded: chain has 10 steps");
        Check(Quests.Find("chain") is not null, "a quest is found by its own id when one mod has it");
        Check(Dialogues.Find(Full("warden_talk")) is { Nodes.Count: 4 }, "dialogue warden_talk has 4 nodes");
        Check(Quests.NpcDefinitions.Count(n => n.ModId == Mod) == 2, "2 NPC definitions, an unknown key next to them ignored");
        // files a mod should not ship: written here, so kiln lint's run over the repository stays clean
        var dir = Path.Combine(Context.Info.Directory, "quests");
        var bad1 = Path.Combine(dir, "badquest.json");
        var bad2 = Path.Combine(dir, "wrong-format.json");
        File.WriteAllText(bad1, """
            { "format": 1, "quests": {
                "no_steps": { "name": "No steps" },
                "bad_scope": { "name": "Bad scope", "scope": "galaxy", "steps": [ { "id": "a", "objective": { "custom": "x" } } ] },
                "bad_objective": { "name": "Bad objective", "steps": [ { "id": "a", "objective": { "dance": {} } } ] },
                "good_next_to_bad": { "name": "Good", "steps": [ { "id": "a", "text": "Fine", "objective": { "custom": "qe.never" } } ] }
              },
              "dialogues": { "bad_goto": { "start": "a", "nodes": { "a": { "text": "x", "options": [ { "text": "y", "goto": "nowhere" } ] } } } } }
            """);
        File.WriteAllText(bad2, """{ "format": 9 }""");
        Quests.Reload();
        var errors = Quests.Status.LoadErrors;
        Check(Quests.Find(Full("good_next_to_bad")) is not null, "a good quest loads next to bad ones in one file");
        foreach (var bad in new[] { "no_steps", "bad_scope", "bad_objective" })
            Check(Quests.Find(Full(bad)) is null && errors.Any(e => e.Contains(bad, StringComparison.Ordinal)), $"{bad} is refused and reported: {errors.FirstOrDefault(e => e.Contains(bad, StringComparison.Ordinal))}");
        Check(errors.Any(e => e.Contains("wrong-format.json", StringComparison.Ordinal) && e.Contains("reads 1", StringComparison.Ordinal)), "format 9 is refused with its file named");
        Check(errors.Any(e => e.Contains("nowhere", StringComparison.Ordinal)), "a goto to an unknown node is reported");
        File.Delete(bad1);
        File.Delete(bad2);
        Quests.Reload();
        Check(Quests.Status.LoadErrors.Count == 0 && Quests.Find(Full("good_next_to_bad")) is null, $"the bad files gone, a reload has no errors left: {Quests.Status.LoadErrors.Count}");
        Check(Quests.Find(Full("chain")) is { Journal: QuestJournalMode.Native, Scope: QuestScope.Player }, "defaults: the game's journal, scope player");
        Check(new QuestDefinition { Id = "x" }.Journal == QuestJournalMode.Native, "a quest defined in C# defaults to the game's journal too");
        Check(Quests.Find(Full("hidden_one")) is { Journal: QuestJournalMode.Chat }, "\"journal\": \"chat\" stays what it says");
        Check(Dialogues.Find(Full("warden_talk")) is { Native: false }, "\"native\": false stays a chat conversation");
        await ActionsStayOnTheTickThread();
    }

    private async Task LockedAndChain()
    {
        QuestInput.Clear();
        Fresh("chain", Alice);
        _events.Clear();
        var c = Full("chain");
        QuestInput.SetLevel(Alice, 2);
        Check(St("chain", Alice) == QuestStatus.Locked, "chain is locked below level 3");
        Check(!Quests.Start(c, Alice).Ok, "a locked quest does not start");
        QuestInput.SetLevel(Alice, 3);
        Check(St("chain", Alice) == QuestStatus.Available, "chain is available at level 3");
        var r = Quests.Start(c, Alice);
        Check(r.Ok && St("chain", Alice) == QuestStatus.Active && StepOf("chain", Alice) == "hunt", $"chain starts at step hunt: {r}");
        Check(!Quests.Start(c, Alice).Ok, "an active quest does not start twice");
        Check(Saw("started chain"), "QuestStartedEvent fired");
        Check(Saw("changed chain Available->Active hunt"), "QuestStateChangedEvent fired after the start");

        // kill: family and area
        QuestInput.Kill(Alice, "Enemy_Scavenger_Melee01", at: new WorldPosition(1, 0, 1));
        Check(Quests.Progress(c, Alice)!.Counter() == 0, "kill: another family does not count");
        QuestInput.Kill(Alice, "Enemy_Vukah_Melee", at: new WorldPosition(9000, 0, 0));
        Check(Quests.Progress(c, Alice)!.Counter() == 0, "kill: outside the area does not count");
        QuestInput.Kill(Bob, "Enemy_Vukah_Melee", at: new WorldPosition(1, 0, 1));
        Check(Quests.Progress(c, Alice)!.Counter() == 0, "kill: someone else's kill does not count on a player quest");
        QuestInput.Kill(Alice, "Enemy_Vukah_Melee", at: new WorldPosition(1, 0, 1));
        Check(Quests.Progress(c, Alice)!.Counter() == 1, "kill: 1/2 counted");
        Check(Saw("progress chain.hunt[0] 0->1/2"), "QuestObjectiveProgressEvent fired");
        QuestInput.Kill(Alice, "Enemy_Vukah_Ranger", at: new WorldPosition(2, 0, 2));
        Check(StepOf("chain", Alice) == "gather", "kill: 2/2 moves to step gather");
        Check(Saw("step chain.hunt -> gather"), "QuestStepEvent fired");

        // collect: a level, follows the inventory
        QuestInput.SetInventory(Alice, Logs, 1);
        Check(Quests.Progress(c, Alice)!.Counter() == 1, "collect: follows the inventory (1/3)");
        QuestInput.SetInventory(Alice, Logs, 0);
        Check(Quests.Progress(c, Alice)!.Counter() == 0, "collect: drops when the items are gone");
        QuestInput.SetInventory(Alice, Logs, 3);
        Check(StepOf("chain", Alice) == "go", "collect: 3/3 moves to step go");

        // reach: an absolute place, then an offset from where the step started
        QuestInput.Reach(Alice, new WorldPosition(50, 0, 50));
        Check(StepOf("chain", Alice) == "go", "reach: too far does not count");
        QuestInput.Reach(Alice, new WorldPosition(11, 0, 11));
        Check(StepOf("chain", Alice) == "walk", "reach: within the radius moves to step walk");
        Check(Quests.Find(c)!.Step("go")!.Marker is { Radius: 5 }, "the step's marker is parsed");
        QuestInput.Reach(Alice, new WorldPosition(15, 0, 11));
        Check(StepOf("chain", Alice) == "walk", "reach offset: 4 m of 20 does not count");
        QuestInput.Reach(Alice, new WorldPosition(31, 0, 11));
        Check(StepOf("chain", Alice) == "meet", "reach offset: 20 m from the step's start moves to step meet");

        // talk
        QuestInput.Talk(Alice, "clerk", modId: Mod);
        Check(StepOf("chain", Alice) == "meet", "talk: another NPC does not count");
        QuestInput.Talk(Alice, "warden", modId: Mod);
        Check(StepOf("chain", Alice) == "signal", "talk: the Warden moves to step signal");

        // custom, craft, build, several objectives
        Quests.Signal("qe.bell", Alice);
        Check(Quests.Progress(c, Alice)!.Counter() == 1, "custom: 1/2");
        Quests.Signal("qe.other", Alice);
        Check(Quests.Progress(c, Alice)!.Counter() == 1, "custom: another signal does not count");
        Quests.Signal("qe.bell", Alice);
        Check(StepOf("chain", Alice) == "make", "custom: 2/2 moves to step make");
        QuestInput.Craft(Alice, "Other_Item");
        QuestInput.Craft(Alice, Logs, 2);
        Check(StepOf("chain", Alice) == "place", "craft: 2 of the item moves to step place");
        QuestInput.Build(Alice, Logs);
        Check(StepOf("chain", Alice) == "both", "build: the prop moves to step both");
        Quests.Signal("qe.two", Alice, 2);
        Check(StepOf("chain", Alice) == "both", "two objectives: one full is not enough");
        Quests.Signal("qe.one", Alice);
        Check(StepOf("chain", Alice) == "turnin", "two objectives: both full move to step turnin");

        // deliver: needs and takes the items
        QuestInput.SetInventory(Alice, Logs, 2);
        var refused = await Quests.Advance(c, Alice, "turnin");
        Check(!refused.Ok && refused.Reason.Contains("you need 3", StringComparison.Ordinal), $"deliver: refused without the items: {refused.Reason}");
        Check(!(await Quests.Advance(c, Alice, "hunt")).Ok, "advance: the wrong step is refused");
        QuestInput.SetInventory(Alice, Logs, 5);
        var done = await Quests.Advance(c, Alice, "turnin");
        Check(done.Ok && St("chain", Alice) == QuestStatus.Done, $"deliver: takes the items and completes the quest: {done}");
        Check(QuestInputCount(Alice) == 2, "deliver: took exactly 3 of 5");
        Check(Saw("completed chain") && Saw("changed chain Active->Done"), "QuestCompletedEvent and QuestStateChangedEvent fired");
        Check(await WaitFor(() => Quests.RecentActions.Any(l => l.Contains("xp 1500: skipped", StringComparison.Ordinal)), 20), "rewards ran: XP is skipped for a holder without a player");
        Check(Quests.RecentActions.Any(l => l.Contains($"give 2 x {Logs}: skipped", StringComparison.Ordinal)), "rewards ran: items are skipped for a holder without a player");
        Check(!Quests.CanStart(c, Alice), "a done quest does not start again");
        Check(Quests.Progress(c, Alice) is { Status: QuestStatus.Done, StepId: null, Completions: 1 }, "done: no step, 1 completion");
    }

    private static int QuestInputCount(QuestHolder h)
    {
        // read through a condition: the only public view of a synthetic inventory
        for (var n = 8; n > 0; n--)
            if (QuestConditionHolds(QuestCondition.HasItem(Logs, n), h)) return n;
        return 0;
    }

    private static bool QuestConditionHolds(QuestCondition c, QuestHolder h) => Quests.Evaluate(c, new QuestContext(h, null, EntityId.None, Mod));

    private async Task Conversations()
    {
        var c = Full("errand");
        Fresh("errand", Alice);
        QuestInput.SetInventory(Alice, Logs, 0);
        QuestInput.Reach(Alice, new WorldPosition(100, 0, 100));  // the Warden's place
        var talk = Dialogues.Talk(Alice, Full("warden"));
        Check(talk.Ok, $"talk: the Warden opens his dialogue: {talk}");
        var v = Dialogues.View(Alice);
        if (!Check(v is { Node: "hello" }, $"the conversation is at node hello: {v?.Node}")) return;
        Check(v!.Text.For("de") == "Halt. Was willst du?", "texts per language reach the conversation");
        Check(v.Speaker?.For("en") == "Warden", "the speaker is the NPC's name");
        var shown = v.Options.Select(o => o.Index).ToArray();
        Check(shown.SequenceEqual([0, 3, 4]), $"shown options follow the conditions: {string.Join(",", shown)} (no work done, level 3, no logs)");

        // a client that sends a hidden option: the server checks again
        var hidden = await Dialogues.Choose(Alice, v.Id, 2);
        Check(!hidden.Ok && Dialogues.View(Alice)?.Node == "hello", $"server-side refusal: a hidden option's condition fails: {hidden.Reason}");
        var bad = await Dialogues.Choose(Alice, v.Id, 99);
        Check(!bad.Ok && Dialogues.View(Alice)?.Node == "hello", $"an option that does not exist is refused: {bad.Reason}");
        var stale = await Dialogues.Choose(Alice, v.Id + 1000, 4);
        Check(!stale.Ok, $"another conversation's id is refused: {stale.Reason}");

        // an option's condition that turned false after it was shown
        Quests.Start(c, Alice, true);
        var late = await Dialogues.Choose(Alice, v.Id, 0);
        Check(!late.Ok && Dialogues.View(Alice)?.Node == "hello", $"an option shown earlier is checked again when chosen: {late.Reason}");
        Quests.Reset(c, Alice);

        // option -> action -> goto -> a node without options -> the end
        var view = Dialogues.View(Alice)!;
        var start = await Dialogues.Choose(Alice, view.Id, 0);
        Check(start.Ok && St("errand", Alice) == QuestStatus.Active && Dialogues.View(Alice)?.Node == "started", $"option: do start + goto: {start}");
        var cont = await Dialogues.Choose(Alice, Dialogues.View(Alice)!.Id, -1);
        Check(cont.Ok && Dialogues.View(Alice) is null, "a node without options continues to the end: the conversation closes");

        // deliver through a conversation: a failing action refuses the option and keeps the node
        Dialogues.Talk(Alice, Full("warden"));
        view = Dialogues.View(Alice)!;
        Check(view.Options.Any(o => o.Index == 1), "the hand-over option shows once its step is active");
        var lacking = await Dialogues.Choose(Alice, view.Id, 1);
        Check(!lacking.Ok && lacking.Reason.Contains("you need 2", StringComparison.Ordinal) && Dialogues.View(Alice)?.Node == "hello", $"option: a failing action refuses it: {lacking.Reason}");
        QuestInput.SetInventory(Alice, Logs, 2);
        view = Dialogues.View(Alice)!;
        _events.Clear();
        var turned = await Dialogues.Choose(Alice, view.Id, 1);
        Check(turned.Ok && St("errand", Alice) == QuestStatus.Done && Dialogues.View(Alice)?.Node == "thanks", $"option: advance takes the items, completes, moves on: {turned}");
        Check(await WaitFor(() => Saw("called qe.reward"), 20), "a registered code action ran as the quest's reward (JSON call)");
        Check(await Dialogues.Choose(Alice, Dialogues.View(Alice)!.Id, -1) is { Ok: true } && Dialogues.View(Alice) is null, "the last node ends it");

        // veto through the event
        using (Events.On<ConversationOptionEvent>(e =>
               {
                   if (e.Node == "hello" && e.Option == 4)
                   {
                       e.RefuseMessage = "not now";
                       e.Cancel();
                   }
               }))
        {
            Dialogues.Talk(Alice, Full("warden"));
            var vetoed = await Dialogues.Choose(Alice, Dialogues.View(Alice)!.Id, 4);
            Check(!vetoed.Ok && vetoed.Reason == "not now" && Dialogues.View(Alice) is not null, "ConversationOptionEvent can refuse an option");
        }
        Dialogues.Close(Alice);
        Check(Dialogues.View(Alice) is null, "Close ends a conversation");
        Dialogues.Talk(Alice, Full("warden"));
        var walking = Dialogues.View(Alice)!;
        QuestInput.Reach(Alice, new WorldPosition(900, 0, 900));
        var gone = await Dialogues.Choose(Alice, walking.Id, 4);
        Check(!gone.Ok && Dialogues.View(Alice) is null, $"walking away from the NPC ends the conversation: {gone.Reason}");
        QuestInput.Reach(Alice, new WorldPosition(100, 0, 100));
        var missing = Dialogues.Talk(Alice, "no_such_npc");
        Check(!missing.Ok, $"an unknown NPC: {missing.Reason}");
        var apply = await Dialogues.ApplyOption(EntityId.None, EntityId.None, Full("warden_talk"), "hello", 4);
        Check(!apply.Ok, $"ApplyOption wants a player: {apply.Reason}");
    }

    private async Task GiverWithoutDialogue()
    {
        Fresh("from_clerk", Alice);
        QuestInput.SetInventory(Alice, Logs, 0);
        QuestInput.Reach(Alice, new WorldPosition(200, 0, 200));  // the Clerk's place
        var open = Dialogues.Talk(Alice, Full("clerk"));
        var v = Dialogues.View(Alice);
        Check(open.Ok && v is { Options.Count: 2 }, $"an NPC without a dialogue offers the quests it gives: {v?.Options.Count} options");
        var accept = await Dialogues.Choose(Alice, v!.Id, 0);
        Check(accept.Ok && St("from_clerk", Alice) == QuestStatus.Active && Dialogues.View(Alice) is null, $"accepting starts the quest: {accept}");
        QuestInput.SetInventory(Alice, Logs, 1);
        Dialogues.Talk(Alice, Full("clerk"));
        v = Dialogues.View(Alice);
        Check(v is { Options.Count: 2 } && v.Options[0].Text.For("en") == "Bring the Clerk one log", "the same NPC takes what its step wants");
        var hand = await Dialogues.Choose(Alice, v!.Id, 0);
        Check(hand.Ok && St("from_clerk", Alice) == QuestStatus.Done, $"handing over completes it: {hand}");
        var nothing = Dialogues.Talk(Alice, Full("clerk"));
        Check(!nothing.Ok && nothing.Reason.Contains("nothing for you", StringComparison.Ordinal), $"nothing left: {nothing.Reason}");
    }

    private async Task PartyQuest()
    {
        var c = Full("party_hunt");
        Fresh("party_hunt", Alice, Bob, Carol);
        QuestInput.Reach(Alice, new WorldPosition(0, 0, 0));
        QuestInput.Reach(Bob, new WorldPosition(20, 0, 0));
        QuestInput.Reach(Carol, new WorldPosition(500, 0, 0));
        var r = Quests.Start(c, Alice);
        Check(r.Ok && St("party_hunt", Bob) == QuestStatus.Active, $"party: a start reaches a member within 50 m: {r}");
        Check(St("party_hunt", Carol) == QuestStatus.Available, "party: a player 500 m away is not started");
        Quests.Start(c, Carol);
        _events.Clear();
        QuestInput.Kill(Alice, "Enemy_Vukah_Melee", "qe-party");
        Check(St("party_hunt", Alice) == QuestStatus.Done && St("party_hunt", Bob) == QuestStatus.Done, "party: the kill completes the killer and the member near them");
        Check(St("party_hunt", Carol) == QuestStatus.Active, "party: the far member gets no credit");
        Check(_events.Count(e => e.StartsWith("completed party_hunt", StringComparison.Ordinal)) == 2, "party: one completion event per member");
        Check(await WaitFor(() => Quests.RecentActions.Count(l => l.Contains("party_hunt rewards: xp 10", StringComparison.Ordinal)) >= 2, 20), "party: each member's rewards ran");
    }

    private async Task FailRetryRepeat()
    {
        var f = Full("fragile");
        Fresh("fragile", Alice);
        Quests.Start(f, Alice);
        _events.Clear();
        var failed = Quests.Fail(f, Alice, "lab");
        Check(failed.Ok && St("fragile", Alice) == QuestStatus.Failed && Saw("failed fragile: lab"), $"Fail: {failed}");
        Check(await WaitFor(() => Saw("changed fragile Active->Failed"), 5), "a failure is a state change");
        Check(!Quests.CanStart(f, Alice), "retryable false: it cannot start again");
        Quests.Reset(f, Alice);
        Quests.Start(f, Alice);
        var gaveUp = Quests.Abandon(f, Alice);
        Check(gaveUp.Ok && St("fragile", Alice) == QuestStatus.Failed, "abandon without retry: failed");
        Quests.Reset(f, Alice);

        var a = Full("again");
        Fresh("again", Alice);
        Quests.Start(a, Alice);
        Quests.Signal("qe.again", Alice);
        Check(St("again", Alice) == QuestStatus.Done && Quests.CanStart(a, Alice), "repeatable: done and can start again");
        Quests.Start(a, Alice);
        Quests.Signal("qe.again", Alice);
        Check(Quests.Progress(a, Alice)!.Completions == 2, "repeatable: 2 completions");

        var g = Full("plain");
        Fresh("plain", Alice);
        Quests.Start(g, Alice);
        var dropped = Quests.Abandon(g, Alice);
        Check(dropped.Ok && Quests.Progress(g, Alice) is null && Quests.CanStart(g, Alice), "abandon with retry: back to available");
        Check(!Quests.Abandon(Full("world_hunt"), Alice).Ok, "a world quest cannot be given up");

        var cancel = Quests.Start(Full("cancel_me"), Alice);
        Check(!cancel.Ok && St("cancel_me", Alice) == QuestStatus.Available && cancel.Reason.Contains("canceled", StringComparison.Ordinal), $"QuestStartedEvent can refuse: {cancel.Reason}");

        var c = Full("chain");
        var setStep = Quests.SetStep(c, Carol, "gather");
        Check(setStep.Ok && StepOf("chain", Carol) == "gather", "admin: put a holder at a step");
        Check(!Quests.SetStep(c, Carol, "nope").Ok, "admin: an unknown step is refused");
        Check(Quests.Complete(c, Carol).Ok && St("chain", Carol) == QuestStatus.Done, "admin: complete now");
        Check(Quests.Reset(c, Carol).Ok && Quests.Progress(c, Carol) is null, "admin: reset forgets it");
        Check(QuestConditionHolds(QuestCondition.Quest("chain", "done"), Alice), "condition quest is done");
        Check(QuestConditionHolds(QuestCondition.QuestStep("chain.hunt", "done"), Alice), "condition step is done");
        Check(!QuestConditionHolds(new QuestCondition { Kind = QuestConditionKind.Not, Children = [QuestCondition.Quest("chain", "done")] }, Alice), "condition not");
        Check(QuestConditionHolds(new QuestCondition { Kind = QuestConditionKind.Any, Children = [QuestCondition.Level(99), QuestCondition.Level(1)] }, Alice), "condition any");
        Check(!QuestConditionHolds(new QuestCondition { Kind = QuestConditionKind.All, Children = [QuestCondition.Level(99), QuestCondition.Level(1)] }, Alice), "condition all");
        Check(QuestConditionHolds(new QuestCondition { Kind = QuestConditionKind.Predicate, Name = "qe.gate" }, Alice) &&
              !QuestConditionHolds(new QuestCondition { Kind = QuestConditionKind.Predicate, Name = "qe.gate" }, Bob), "condition predicate registered by code");
        Check(!QuestConditionHolds(new QuestCondition { Kind = QuestConditionKind.Predicate, Name = "qe.nobody" }, Alice), "an unknown predicate is false");
        Check(QuestConditionHolds(QuestCondition.HasKnowledge("npc:NPC_Workshop_Farmer01.unlock"), QuestHolder.World) ||
              !QuestConditionHolds(QuestCondition.HasKnowledge("npc:NPC_Workshop_Bard01.unlock"), QuestHolder.World), "condition knowledge reads the world's");
    }

    private async Task CodeDefinitions()
    {
        var count = 0;
        var paid = false;
        var q = Quests.Define(new QuestDefinition
        {
            Id = "code_quest",
            Name = UiText.Of(("en", "From Code")),
            Steps =
            [
                new QuestStep { Id = "poll", Text = "Polled", Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Custom, Progress = _ => count, Count = 2 }] },
                new QuestStep { Id = "go", Text = "Then this", Objectives = [QuestObjective.Custom("qe.code")] },
                new QuestStep { Id = "gone", Text = "Removed later", Objectives = [QuestObjective.Custom("qe.code.gone")] },
            ],
            Rewards = [QuestAction.Code(_ =>
            {
                paid = true;
                return Task.CompletedTask;
            })],
        });
        Check(q.FullId == Full("code_quest"), $"Quests.Define takes the calling mod's id: {q.FullId}");
        Fresh("code_quest", Alice);
        Quests.Start(q.FullId, Alice);
        count = 2;
        QuestInput.SetInventory(Alice, Logs, 1);  // evaluates the holder's polled objectives, as the poll does
        Check(StepOf("code_quest", Alice) == "go", "a C# progress function moves the step");
        Quests.Signal("qe.code", Alice);
        Check(StepOf("code_quest", Alice) == "gone", "a C# objective by signal moves the step");

        // the definition changes under a running quest: its place survives
        Quests.Define(new QuestDefinition
        {
            Id = "code_quest",
            Name = "From Code",
            Steps = [new QuestStep { Id = "poll", Text = "x", Objectives = [QuestObjective.Custom("qe.x")] }, new QuestStep { Id = "go", Text = "y", Objectives = [QuestObjective.Custom("qe.code")] }],
            Rewards = q.Rewards,
        });
        Check(StepOf("code_quest", Alice) == "go", "a redefined quest without the current step continues at a step of the same place");
        Quests.Signal("qe.code", Alice);
        Check(St("code_quest", Alice) == QuestStatus.Done, "and finishes");
        Check(await WaitFor(() => paid, 20), "a C# reward ran");
        try
        {
            Quests.Define(new QuestDefinition { Id = "bad id", Steps = [new QuestStep { Id = "a" }] });
            Check(false, "an invalid id should throw");
        }
        catch (ArgumentException e)
        {
            Check(true, $"an invalid id throws: {e.Message}");
        }
        try
        {
            Dialogues.Define(new DialogueDefinition { Id = "bad", Start = "nope" });
            Check(false, "a dialogue without its start node should throw");
        }
        catch (ArgumentException e)
        {
            Check(true, $"a dialogue without its start node throws: {e.Message}");
        }
    }

    private async Task Reloading()
    {
        var chain = Quests.Progress(Full("chain"), Alice);
        var extra = Path.Combine(Context.Info.Directory, "quests", "extra.json");
        File.WriteAllText(extra, """{ "format": 1, "quests": { "added": { "name": "Added", "steps": [ { "id": "a", "text": "x", "objective": { "custom": "qe.never" } } ] } } }""");
        try
        {
            var before = Quests.Definitions.Count;
            Quests.Reload();
            Check(Quests.Find(Full("added")) is not null && Quests.Definitions.Count == before + 1, $"reload: a new file's quest appears ({before} -> {Quests.Definitions.Count})");
            Check(Quests.Find(Full("code_quest")) is not null, "reload keeps what code defined");
            Check(Quests.Progress(Full("chain"), Alice)?.Status == chain?.Status && Quests.Progress(Full("chain"), Alice)?.Completions == chain?.Completions, "reload keeps states");
        }
        finally
        {
            File.Delete(extra);
        }
        Quests.Reload();
        Check(Quests.Find(Full("added")) is null, "reload: a removed file's quest is gone");
        await Steps(1);
    }

    private async Task WorldQuest()
    {
        var hunt = Full("world_hunt");
        Check(St("world_hunt", QuestHolder.World) == QuestStatus.Active || await WaitFor(() => St("world_hunt", QuestHolder.World) == QuestStatus.Active, 60), "a world quest with auto starts by itself");
        Check(Quests.Find(hunt)!.Scope == QuestScope.World && St("world_hunt", Alice) == QuestStatus.Active, "a world quest reads the same for any holder");
        var bard = Knowledge.Get(EntityId.None, Knowledge.Resolve("npc:NPC_Workshop_Bard01.unlock").Id);
        Check(bard is { IsSet: false }, $"before: the Bard's unlock is not set: {bard}");
        QuestInput.Kill(null, "Enemy_Vukah_Melee", "other-tag");
        Check(Quests.Progress(hunt, QuestHolder.World)!.Counter() == 0, "world: another tag does not count");

        if (!Check(Spawn.Lookup("Enemy_Vukah_Melee") is not null, "template Enemy_Vukah_Melee")) return;
        WorldPosition? anchor = null;
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
            foreach (var id in Spawn.FindByName(filter, max: 4))
                if (anchor is null && Spawn.Inspect(id) is { } info)
                    anchor = info.Position;
        if (!Check(anchor is not null, $"anchor {anchor}")) return;
        var a = anchor!.Value;

        // a real death: the game's death program, EntityDeathEvent
        var first = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(a.X + 40, a.Y + 0.5, a.Z), 0, new SpawnOptions { Tag = "qe-world", Flags = SpawnFlags.Transient });
        Info($"spawned {first} at {a.X + 40:F0}, {a.Z:F0}");
        await Steps(4);
        var kill = await Combat.Kill(first);
        Info($"Combat.Kill: {kill}");
        Check(await WaitFor(() => Quests.Progress(hunt, QuestHolder.World)?.Counter() == 1, 100), $"world: a real death counts 1/2 (EntityDeathEvent, tag qe-world): {Quests.Progress(hunt, QuestHolder.World)?.Counter()}");

        // a real destroy: EntityDestroyedEvent, then the reward: world knowledge through kiln.progression's neighbour kiln.knowledge
        var second = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(a.X + 43, a.Y + 0.5, a.Z), 0, new SpawnOptions { Tag = "qe-world", Flags = SpawnFlags.Transient });
        await Steps(4);
        _events.Clear();
        Check(Spawn.Destroy(second), "Spawn.Destroy the second creature");
        Check(await WaitFor(() => St("world_hunt", QuestHolder.World) == QuestStatus.Done, 100), "world: a real destroy (countDestroyed) completes the quest");
        Check(Saw("completed world_hunt"), "the world quest's completion event fired");
        Check(await WaitFor(() => Knowledge.Get(EntityId.None, Knowledge.Resolve("npc:NPC_Workshop_Bard01.unlock").Id) is { IsSet: true }, 100),
            "the reward is in the game's knowledge: world Bool npc:NPC_Workshop_Bard01.unlock is set");
        Check(await WaitFor(() => Knowledge.World.Has("npc:NPC_Workshop_Bard01"), 200), "and the game's NPC requirement holds for the world");
        Check(await WaitFor(() => St("world_know", QuestHolder.World) == QuestStatus.Done, 100), "a world quest's knowledge objective follows that knowledge (polled)");
        Check(!Quests.CanStart(hunt, QuestHolder.World), "a done world quest stays done");
    }

    private async Task NpcsInTheWorld()
    {
        // a named villager the world has not unlocked is destroyed by the game (unique_npc_spawns); the game's test villagers
        // have no unlock requirement, so they stay: docs/re/creatures.md 7.6
        const string hugo = "test_Townsfolk_Male_03";
        Dialogues.Close(Alice);
        QuestInput.Reach(Alice, new WorldPosition(5000, 0, 5000));
        var nobody = Dialogues.Talk(Alice);
        Check(!nobody.Ok, $"talk with nobody near: {nobody.Reason}");
        QuestInput.Reach(Alice, new WorldPosition(102, 0, 100));
        var spot = Dialogues.Talk(Alice);
        Check(spot.Ok && Dialogues.View(Alice)?.Dialogue == Full("warden_talk"), "talk near an NPC's place, no entity there: the place counts");
        Dialogues.Close(Alice);

        WorldPosition? anchor = null;
        foreach (var id in Spawn.FindByName("FlameAltar", max: 1))
            anchor = Spawn.Inspect(id)?.Position;
        if (!Check(anchor is not null, "anchor for the NPC") || Spawn.Lookup(hugo) is null)
        {
            Info($"{hugo} is not spawnable here: skipped");
            return;
        }
        var a = anchor!.Value;
        var at = new WorldPosition(a.X + 50, a.Y + 0.5, a.Z + 10);

        // a spawn action with bindNpc: what the sample's Warden does in code
        Quests.DefineNpc(new QuestNpcDefinition { Id = "hugo", Name = "Hugo", Dialogue = "warden_talk" });
        Quests.Define(new QuestDefinition
        {
            Id = "spawns",
            Name = "Spawns",
            OnStart = [new QuestAction { Kind = QuestActionKind.Spawn, Name = hugo, At = at, Tag = "qe-npc", BindNpc = "hugo", Transient = true, Exact = true }],
            Steps = [new QuestStep { Id = "a", Text = "x", Objectives = [QuestObjective.Talk("hugo")], OnDone = [new QuestAction { Kind = QuestActionKind.Despawn, Name = "qe-npc" }] }],
        });
        Fresh("spawns", Alice);
        Quests.Start(Full("spawns"), Alice);
        var ok = await WaitFor(() => Quests.RecentActions.Any(l => l.Contains($"spawned {hugo}", StringComparison.Ordinal)), 60);
        Check(ok, $"the spawn action spawned {hugo}");
        await Steps(125);
        var found = Spawn.FindByName(hugo, at, 30, 4);
        Check(found.Count == 1, $"a test villager stays in the world for 5 s (a locked named one is destroyed within 3): {found.Count} found");
        QuestInput.Reach(Alice, new WorldPosition(at.X + 2, at.Y, at.Z));
        var talk = Dialogues.Talk(Alice);
        var v = Dialogues.View(Alice);
        Check(talk.Ok && v is not null && found.Count == 1 && v.Npc == found[0], $"talk finds the bound entity: {talk}, NPC entity {v?.Npc}");
        Check(St("spawns", Alice) == QuestStatus.Done, "and that talk finished the step");
        Dialogues.Close(Alice);
        Check(await WaitFor(() => Spawn.FindByName(hugo, at, 30, 4).Count == 0, 60), "the despawn action removed it");

        // found by template, no binding
        var second = await Spawn.Entity(hugo, at, 0, new SpawnOptions { Tag = "qe-npc", Flags = SpawnFlags.Transient });
        await Steps(4);
        Quests.DefineNpc(new QuestNpcDefinition { Id = "hugo2", Name = "Hugo again", Template = hugo, Dialogue = "warden_talk", TalkRange = 6 });
        QuestInput.Reach(Alice, new WorldPosition(at.X + 2, at.Y, at.Z));
        var byTemplate = Dialogues.Talk(Alice);
        Check(byTemplate.Ok && Dialogues.View(Alice)?.Npc == second, $"an NPC is found by its template near the player: {byTemplate}, entity {Dialogues.View(Alice)?.Npc}");
        Dialogues.Close(Alice);
        Spawn.Destroy(second);
    }

    private void Persist()
    {
        var p = Full("persist");
        Fresh("persist", Alice);
        Quests.Start(p, Alice);
        Quests.Signal("qe.persist", Alice, 3);
        Check(Quests.Progress(p, Alice) is { StepId: "a" } x && x.Counter() == 3, "persist: 3 of 5, left for the next boot");
    }

    private void Waypoints()
    {
        var s = Quests.Status;
        var (requests, written) = Quests.WaypointCounters;
        Check(s.Waypoints, $"kiln.questmark's program runs in this world (no player: {requests} requests, {written} written; the write itself is client check C1)");
    }

    // ------------------------------------------------------------------------------------------------ boot 2 -------
    private async Task Boot2()
    {
        var p = Full("persist");
        var x = Quests.Progress(p, Alice);
        Check(x is { Status: QuestStatus.Active, StepId: "a" } && x.Counter() == 3, $"sidecar: a player's quest kept its step and counter over the restart: {x}");
        Check(Quests.Progress(Full("chain"), Alice) is { Status: QuestStatus.Done, Completions: 1 }, "sidecar: a done quest stays done with its count");
        Check(St("world_hunt", QuestHolder.World) == QuestStatus.Done, "sidecar: the world quest stays done (auto does not start it again)");
        Check(Knowledge.World.Has("npc:NPC_Workshop_Bard01"), "the game's save kept the reward knowledge");
        Quests.Signal("qe.persist", Alice, 2);
        Check(Quests.Progress(p, Alice)?.StepId == "b", "and the quest goes on from where it was");
        Quests.Signal("qe.persist.b", Alice);
        Check(St("persist", Alice) == QuestStatus.Done, "and finishes");

        // put the lab's world back
        var r = await Knowledge.World.Revoke("npc:NPC_Workshop_Bard01.unlock");
        Check(r.Ok && !Knowledge.World.Has("npc:NPC_Workshop_Bard01"), $"the Bard's unlock is revoked: {r}");
        foreach (var id in new[] { "world_hunt", "world_know" }) Quests.Reset(Full(id), QuestHolder.World);
        foreach (var d in Quests.Definitions.Where(q => q.ModId == Mod))
        {
            Quests.Reset(d.FullId, Alice);
            Quests.Reset(d.FullId, Bob);
            Quests.Reset(d.FullId, Carol);
        }
        QuestInput.Clear();
    }
}
