// SPDX-License-Identifier: MIT
// Lab test of quest and dialogue blocks (docs/kiln/modding/quests.md "Quests as blocks"). No player: synthetic holders (QuestInput) drive the steps, a world quest
// the game's knowledge. Start it twice (tools/lab/lab.sh stop between; delete kiln/data/blocks-quest-test/state.json to start over):
//   boot 1: what blocks/quests.json defined is registered with the engine and was built into the game's journal and NPC menu at this very start (the knowledge ids
//           are the ones the managed rule gives); its steps advance through Quests commands and a conversation; the C# twin lowers to the same as the JSON one; wrong
//           blocks are refused by block and field. What the C# blocks define is registered too, and written for the next start.
//   boot 2: the chain written in C# is in the game's journal now, built from the file Kiln wrote, and its steps advance; nothing of the data blocks is built twice.
// "blocks-quest-test: PASS ..." / "FAIL ..." lines in kiln.log, then "done: N PASS, M FAIL".
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksQuestTest;

public sealed class BlocksQuestTestMod : Mod
{
    private const string Me = "blocks-quest-test";
    private const string Logs = "Material_T1_Wood_Logs";
    private static readonly QuestHolder Alice = QuestHolder.Synthetic("bq-alice");
    private int _pass, _fail;
    private QuestHandle? _code, _codeWorld, _twinCs;
    private DialogueHandle? _codeTalk;
    private string StatePath => Path.Combine(Context.DataDirectory, "state.json");

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{Me}: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private void Info(string s) => Log.Info($"{Me}: INFO {s}");

    private static string Full(string id) => $"{Me}:{id}";

    public override void OnLoad()
    {
        // the twin of blocks/quests.json's bq-twin, in C#: the two must lower to the same
        _twinCs = Blocks.Define("bq-twin-cs", new QuestBlock
        {
            Name = "Twin",
            Description = "The same, in C# and in JSON.",
            Scope = QuestScope.Party,
            Requires = [Req.Level(3), Req.Not(Req.Quest("bq-data", "failed"))],
            Steps =
            [
                new StepBlock
                {
                    Id = "hunt", Title = "Hunt", Text = "Kill two Vukah", Objective = Goal.Kill(family: "Vukah", count: 2),
                    Spawn = [new StepSpawnBlock { Template = "Enemy_Vukah_Melee", Count = 2, Tag = "bq-hunt", Near = "bq-giver", Distance = [40, 80] }],
                },
                new StepBlock { Id = "report", Text = "Tell the giver", Objective = Goal.Talk("bq-giver", node: "hello"), Rewards = new RewardBlock { Xp = 10, Status = "bq-boon" } },
            ],
            Rewards = new RewardBlock { Xp = 100, Items = { ["Material_T1_Twigs"] = 2 }, Knowledge = ["npc:NPC_Workshop_Bard01.unlock"] },
        });

        // a chain of its own, in C#: a world quest (the game's knowledge follows it without a player), a conversation, an NPC that names the conversation by its handle
        _codeTalk = Blocks.Define("bq-code-talk", new DialogueBlock
        {
            Start = "hello",
            Nodes =
            {
                ["hello"] = new DialogueNodeBlock
                {
                    Text = "From code.",
                    Options = [new OptionBlock { Text = "Begin", If = Req.Quest("bq-code", "available"), Do = [Do.Start("bq-code")], Reply = "Begun." }],
                },
            },
        });
        Blocks.Define("bq-code-npc", new NpcBlock { Name = "Code Giver", Body = Bodies.Villager, Talk = _codeTalk });
        _code = Blocks.Define("bq-code", new QuestBlock
        {
            Name = "Quest from code",
            Giver = "bq-code-npc",
            Steps =
            [
                new StepBlock { Id = "first", Text = "First", Objective = Goal.Custom("bq.code") },
                new StepBlock { Id = "second", Text = "Second", Objective = Goal.Custom("bq.code", 2), Rewards = new RewardBlock { Items = { ["Material_T1_Twigs"] = 1 } } },
            ],
            Rewards = new RewardBlock { Xp = 5, Status = "bq-boon" },
        });
        _codeWorld = Blocks.Define("bq-code-world", new QuestBlock
        {
            Name = "World quest from code",
            Scope = QuestScope.World,
            Steps =
            [
                new StepBlock { Id = "one", Text = "One", Objective = Goal.Custom("bq.cw") },
                new StepBlock { Id = "two", Text = "Two", Objective = Goal.Custom("bq.cw", 2) },
            ],
        });
        _ = RunGuarded();
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
            else Info("all boots done: delete kiln/data/blocks-quest-test/state.json to run again");
            WriteBoot(boot + 1);
        }
        catch (Exception e)
        {
            Check(false, $"the test threw: {e}");
        }
        Log.Info($"{Me}: done: {_pass} PASS, {_fail} FAIL");
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

    // ------------------------------------------------------------------------------------------------ boot 1 -------
    private async Task Boot1()
    {
        Registered();
        Twins();
        Allocated();
        await DataQuestSteps();
        await Conversation();
        await WorldMirror("bq-world", "bq.world", 1, 3);
        Mistakes();
        Check(_code is { }, "the C# chain is defined (it is in the game's journal from the next start)");
        Info($"the C# quest is in the game's journal at this start: {NativeQuests.Quest(Full("bq-code")) is not null} (false on a first start: it is written for the next)");
        Exported(wantCode: true);
    }

    // ---- registered with the engine ----
    private void Registered()
    {
        var (blocks, errors) = Blocks.Report()[Me];
        foreach (var e in errors) Info($"refused: {e}");
        Check(errors.Count == 0, $"no block of this mod was refused: {errors.Count} error(s)");
        Check(new[] { "bq-data", "bq-world", "bq-twin", "bq-talk", "bq-giver", "bq-boon", "bq-code", "bq-code-talk" }.All(blocks.Contains), $"the report names the blocks: {string.Join(", ", blocks)}");
        var q = Quests.Find(Full("bq-data"));
        Check(q is { Steps.Count: 3, Journal: QuestJournalMode.Native, Scope: QuestScope.Player, Giver: "bq-giver" }, $"bq-data is registered with the engine: {q}");
        Check(Blocks.Find("bq-data", Me) is QuestHandle { FullId: "blocks-quest-test:bq-data" } h && ReferenceEquals(h.Definition, q), "its handle names the very definition the engine runs");
        Check(q?.Name.For("de") == "Blockquest", "texts per language reach the definition");
        Check(q?.Rewards.Select(a => a.Kind).SequenceEqual([QuestActionKind.Xp, QuestActionKind.Give, QuestActionKind.Call]) == true, "the reward bundle is xp, items and a status");
        Check(q?.Steps[2].OnDone.Select(a => a.Kind).SequenceEqual([QuestActionKind.Give]) == true, "a step's reward is paid when the step is done");
        Check(Quests.Find(Full("bq-world")) is { Scope: QuestScope.World }, "bq-world is a world quest");
        Check(Quests.Find(Full("bq-code")) is { } c && ReferenceEquals(c, _code!.Definition), "a quest defined in C# is registered the same way");
        var d = Dialogues.Find(Full("bq-talk"));
        Check(d is { Native: true } && d.Nodes["hello"].Options.Count == 3, "the dialogue block is registered, native");
        Check(Dialogues.Find(Full("bq-code-talk")) is { } cd && ReferenceEquals(cd, _codeTalk!.Definition), "and so is the one defined in C#");
        var giver = Quests.NpcDefinitions.FirstOrDefault(n => n.FullId == Full("bq-giver"));
        Check(giver is { Dialogue: "bq-talk", Clone: true }, $"the NPC block names the dialogue block: {giver?.Dialogue}");
        Check(Quests.NpcDefinitions.FirstOrDefault(n => n.FullId == Full("bq-code-npc")) is { Dialogue: "bq-code-talk" }, "an NPC block takes the dialogue's handle as Talk");
        Check(Blocks.Lowered("bq-data", Me)?.Contains("\"quest\"", StringComparison.Ordinal) == true, "what it lowered to is on record (kiln blocks show)");
    }

    private static string QuestJson(string block)
    {
        var o = JsonNode.Parse(Blocks.Lowered(block, Me)!)!["quest"]!.AsObject();
        o.Remove("id");
        return o.ToJsonString();
    }

    // ---- the C# block and the data block say the same ----
    private void Twins()
    {
        Check(QuestJson("bq-twin") == QuestJson("bq-twin-cs"), "the C# twin and the JSON twin lower to the same quest");
        var a = Quests.Find(Full("bq-twin"))!;
        var b = _twinCs!.Definition;
        Check(a.Steps.Count == b.Steps.Count && a.Scope == b.Scope && a.Requires.Count == b.Requires.Count && a.Rewards.Count == b.Rewards.Count
              && a.Steps[0].Spawns[0].Place!.NearNpc == b.Steps[0].Spawns[0].Place!.NearNpc && a.Steps[0].Spawns[0].Place!.MinDistance == 40,
            "and the definitions agree: scope, steps, requirements, rewards, the spawn's place");
    }

    // ---- the game's journal has it, with the knowledge ids the managed rule gives ----
    private void Allocated()
    {
        var m = NativeQuests.Mod(Me);
        if (!Check(m is { Ok: true }, $"the game's start-up build built this mod's native data: {(m is null ? "no data for this mod" : $"ok {m.Ok}, {m.Quests.Count} quests, {m.Lines.Count} lines, {m.Npcs.Count} NPCs, errors {string.Join("; ", m.Errors)}")}"))
            return;
        Info($"built: {string.Join(", ", m!.Quests.Select(x => x.Id))}");
        Check(new[] { "bq-data", "bq-world", "bq-twin" }.All(id => m.Quests.Any(x => x.Id == id)), "the quest blocks of the data file are in the game's journal from the first start");
        Check(m.Quests.Count(x => x.Id == "bq-data") == 1, "and each only once");
        var q = NativeQuests.Quest(Full("bq-data"));
        if (!Check(q is not null && NativeQuests.ShownByGame(Quests.Find(Full("bq-data"))!), "NativeQuests.Quest finds it, and the engine's quest is shown by the game")) return;
        Check(q!.EntryId == NativeIds.Knowledge(Me, "q.bq-data"), $"its journal entry's id is the managed rule's: 0x{q.EntryId:x8}");
        Check(q.Started == NativeIds.Knowledge(Me, "q.bq-data.started") && q.Active == NativeIds.Knowledge(Me, "q.bq-data.active") && q.Done == NativeIds.Knowledge(Me, "q.bq-data.done"),
            $"started, active and done are too: 0x{q.Started:x8}, 0x{q.Active:x8}, 0x{q.Done:x8}");
        var steps = new[] { "ring", "gather", "hand" };
        Check(q.Steps.Select(s => s.Id).SequenceEqual(steps), "its three steps are in the journal in order");
        Check(q.Steps.All(s => s.Visible == NativeIds.Knowledge(Me, $"s.bq-data.{s.Id}.visible") && s.Active == NativeIds.Knowledge(Me, $"s.bq-data.{s.Id}.active") && s.Done == NativeIds.Knowledge(Me, $"s.bq-data.{s.Id}.done")),
            "every step's visible, active and done ids are the rule's");
        Check(q.Steps[0].Count != 0 && q.Steps[0].Total == 2 && q.Steps[1].Count != 0 && q.Steps[1].Total == 3, "steps with a count have the journal's counter (2 and 3)");
        Check(q.Steps[2].Ready != 0, "the hand-in step has its \"ready\" object");
        var all = new[] { q.EntryId, q.Started, q.Active, q.Done }.Concat(q.Steps.SelectMany(s => new[] { s.Visible, s.Active, s.Done })).ToList();
        Check(all.All(id => Knowledge.Info(id) is not null), $"the game's knowledge registry has all {all.Count} of them");
        Check(all.Distinct().Count() == all.Count, "none of them twice");
        Check(Knowledge.Info(q.Active) is { IsPlayer: true, IsWorld: false } && Knowledge.Info(NativeQuests.Quest(Full("bq-world"))!.Active) is { IsWorld: true, IsPlayer: false }, "a player quest's are player objects, a world quest's world objects");
        var npc = NativeQuests.Npc(Full("bq-giver"));
        Check(npc is { IsClone: true } && npc.Dialogue == Full("bq-talk") && npc.NpcId != 0, $"the NPC block is built, as a clone that opens bq-talk: {npc?.Dialogue}, NPC id 0x{npc?.NpcId:x8}");
        Check(m.Lines.Count(l => l.Dialogue == Full("bq-talk")) >= 3, $"the conversation's lines are in the game's menu: {m.Lines.Count(l => l.Dialogue == Full("bq-talk"))}");
    }

    // ---- steps advance through Quests commands ----
    private async Task DataQuestSteps()
    {
        var full = Full("bq-data");
        QuestInput.Clear();
        Quests.Reset(full, Alice);
        QuestInput.SetLevel(Alice, 1);
        Check(Quests.StatusOf(full, Alice) == QuestStatus.Locked && !Quests.Start(full, Alice).Ok, "below level 2 it is locked and does not start (a block's requires)");
        QuestInput.SetLevel(Alice, 2);
        Check(Quests.StatusOf(full, Alice) == QuestStatus.Available, "at level 2 it is available");
        var r = Quests.Start(full, Alice);
        Check(r.Ok && Quests.Progress(full, Alice)?.StepId == "ring", $"it starts at its first step: {r}");
        Quests.Signal("bq.bell", Alice);
        Check(Quests.Progress(full, Alice)!.Counter() == 1, "a signal counts: 1/2");
        Quests.Signal("bq.bell", Alice);
        Check(Quests.Progress(full, Alice)?.StepId == "gather", "2/2 moves to the next step");
        QuestInput.SetInventory(Alice, Logs, 3);
        Check(Quests.Progress(full, Alice)?.StepId == "hand", "collect: three logs move to the hand-in step");
        QuestInput.SetInventory(Alice, Logs, 1);
        var refused = await Quests.Advance(full, Alice, "hand");
        Check(!refused.Ok && refused.Reason.Contains("you need 3", StringComparison.Ordinal), $"deliver is refused without the items: {refused.Reason}");
        QuestInput.SetInventory(Alice, Logs, 3);
        var done = await Quests.Advance(full, Alice, "hand");
        Check(done.Ok && Quests.StatusOf(full, Alice) == QuestStatus.Done, $"the advance takes the items and completes the quest: {done}");
        Check(await WaitFor(() => Quests.RecentActions.Any(l => l.Contains("xp 500: skipped", StringComparison.Ordinal)), 40), "the reward bundle ran: xp (skipped for a holder with no player)");
        Check(Quests.RecentActions.Any(l => l.Contains("give 4 x Material_T1_Twigs: skipped", StringComparison.Ordinal)), "the reward's items ran");
        Check(Quests.RecentActions.Any(l => l.Contains("status bq-boon", StringComparison.Ordinal) && l.Contains("skipped", StringComparison.Ordinal)), "the reward's status ran (skipped for a holder with no player)");
        Check(Quests.RecentActions.Any(l => l.Contains("give 1 x Material_T1_Twigs: skipped", StringComparison.Ordinal)), "the hand-in step's own reward ran");
        Check(Quests.RecentActions.All(l => !(l.Contains("bq-", StringComparison.Ordinal) && l.Contains("FAILED", StringComparison.Ordinal))), "no action of these quests failed");
        Quests.Reset(full, Alice);
        QuestInput.SetInventory(Alice, Logs, 0);
    }

    // ---- a conversation of the dialogue block, with the NPC block that names it ----
    private async Task Conversation()
    {
        var full = Full("bq-data");
        Quests.Reset(full, Alice);
        QuestInput.SetLevel(Alice, 2);
        QuestInput.SetInventory(Alice, Logs, 0);
        var open = Dialogues.Talk(Alice, Full("bq-giver"));
        var v = Dialogues.View(Alice);
        if (!Check(open.Ok && v is { Node: "hello" }, $"talking to the NPC block opens the dialogue block at its start: {open}")) return;
        Check(v!.Text.For("en") == "Hello, tester.", "its text reaches the conversation");
        Check(v.Options.Select(o => o.Index).SequenceEqual([0, 2]), $"the options follow their conditions: {string.Join(",", v.Options.Select(o => o.Index))} (available: yes; hand over: no)");
        var hidden = await Dialogues.Choose(Alice, v.Id, 1);
        Check(!hidden.Ok, $"the server checks an option's condition again: {hidden.Reason}");
        var start = await Dialogues.Choose(Alice, v.Id, 0);
        Check(start.Ok && Quests.StatusOf(full, Alice) == QuestStatus.Active, $"the option's do starts the quest: {start}");
        Quests.Signal("bq.bell", Alice, 2);
        QuestInput.SetInventory(Alice, Logs, 3);
        Check(Quests.Progress(full, Alice)?.StepId == "hand", "the quest is at the hand-in");
        Dialogues.Talk(Alice, Full("bq-giver"));
        v = Dialogues.View(Alice)!;
        Check(v.Options.Any(o => o.Index == 1), "the hand-over option shows once its step is active");
        var hand = await Dialogues.Choose(Alice, v.Id, 1);
        Check(hand.Ok && Quests.StatusOf(full, Alice) == QuestStatus.Done, $"it advances the step, takes the logs and completes the quest: {hand}");
        Dialogues.Close(Alice);
        Quests.Reset(full, Alice);
        QuestInput.SetInventory(Alice, Logs, 0);
    }

    // ---- the game's knowledge follows a world quest of a block ----
    private async Task WorldMirror(string id, string signal, int first, int second)
    {
        var full = Full(id);
        var q = NativeQuests.Quest(full);
        if (!Check(q is not null, $"{id}: it is in the game's journal")) return;
        Quests.Reset(full, QuestHolder.World);
        await Steps(8);
        Check(Quests.Start(full, QuestHolder.World).Ok, $"{id}: a world quest starts");
        await WaitFor(() => Knowledge.World.Has(q!.Active), 100);
        Check(Knowledge.World.Has(q!.Started) && Knowledge.World.Has(q.Active) && Knowledge.World.Has(q.Steps[0].Active), $"{id}: the game's knowledge says started, active and step one active");
        Quests.Signal(signal, QuestHolder.World, first);
        await WaitFor(() => Knowledge.World.Has(q.Steps[0].Done), 100);
        Check(Knowledge.World.Has(q.Steps[0].Done) && Knowledge.World.Has(q.Steps[1].Active), $"{id}: step one done and step two active, in the game's knowledge");
        Quests.Signal(signal, QuestHolder.World, second);
        await WaitFor(() => Knowledge.World.Has(q.Done), 100);
        Check(Quests.StatusOf(full, QuestHolder.World) == QuestStatus.Done && Knowledge.World.Has(q.Done), $"{id}: the last step finishes it, and the game's knowledge says done");
        Quests.Reset(full, QuestHolder.World);
        await WaitFor(() => !Knowledge.World.Has(q.Done), 100);
        Check(!Knowledge.World.Has(q.Done) && !Knowledge.World.Has(q.Started) && !Knowledge.World.Has(q.Steps[0].Done), $"{id}: a reset takes all of it out of the game's knowledge again");
    }

    // ---- wrong blocks are refused by block and field ----
    private static string Refused(Func<object> define, out string? field)
    {
        field = null;
        try
        {
            define();
            return "";
        }
        catch (BlockError e)
        {
            field = e.Field;
            return e.Message;
        }
    }

    private void Mistakes()
    {
        var m = Refused(() => Blocks.Define("bq-bad-1", new QuestBlock { Name = "x", Steps = [] }), out var f);
        Check(f == "steps" && m.Contains("bq-bad-1", StringComparison.Ordinal), $"no steps: refused by block and field: \"{m}\"");
        m = Refused(() => Blocks.Define("bq.bad", new QuestBlock { Name = "x", Steps = [new StepBlock { Id = "a", Text = "x", Objective = Goal.Custom("s") }] }), out f);
        Check(m.Length > 0 && m.Contains("letters, digits", StringComparison.Ordinal), $"a name with a dot is no quest id: \"{m}\"");
        m = Refused(() => Blocks.Define("bq-bad-2", new QuestBlock { Name = "x", Giver = "bq-boon", Steps = [new StepBlock { Id = "a", Text = "x", Objective = Goal.Custom("s") }] }), out f);
        Check(f == "giver" && m.Contains("a status block, not an NPC", StringComparison.Ordinal), $"a giver that is a status: \"{m}\"");
        m = Refused(() => Blocks.Define("bq-bad-3", new QuestBlock { Name = "x", Steps = [new StepBlock { Id = "a", Text = "x", Objective = new ObjectiveBlock { Kill = new KillGoal(), Custom = new CustomGoal { Signal = "s" } } }] }), out f);
        Check(f == "steps[0].objective", $"an objective that is two things: \"{m}\"");
        m = Refused(() => Blocks.Define("bq-bad-4", new QuestBlock { Name = "x", Steps = [new StepBlock { Id = "a", Text = "x", Objective = Goal.Custom("s") }], Rewards = new RewardBlock { Status = "no-such-status" } }), out f);
        Check(f == "rewards.status" && m.Contains("no-such-status", StringComparison.Ordinal), $"a reward status that is no block: \"{m}\"");
        m = Refused(() => Blocks.Define("bq-bad-5", new DialogueBlock { Nodes = { ["start"] = new() { Text = "x", Options = [new OptionBlock { Text = "y", Goto = "nowhere" }] } } }), out f);
        Check(f == "nodes" && m.Contains("nowhere", StringComparison.Ordinal), $"a dialogue that goes nowhere: \"{m}\"");
        m = Refused(() => Blocks.Define("bq-bad-6", new NpcBlock { Name = "x", Body = Bodies.Villager, TalkName = "bq-data" }), out f);
        Check(f == "talk" && m.Contains("a quest block, not a dialogue", StringComparison.Ordinal), $"an NPC whose talk is a quest: \"{m}\"");
        Check(Quests.Find(Full("bq-bad-1")) is null && Quests.Find(Full("bq-bad-3")) is null && Dialogues.Find(Full("bq-bad-5")) is null, "a refused block registers nothing");
    }

    // ---- what the engine wrote for the game's next start ----
    private string NativeFile => Path.Combine(Context.Info.Directory, "..", "..", "data", "kiln-quests", "native", Me + ".json");

    private void Exported(bool wantCode)
    {
        var file = Path.GetFullPath(NativeFile);
        var written = Quests.Definitions.Count(q => q.ModId == Me);
        Info($"native file: {file} (exists {File.Exists(file)})");
        if (!Check(!wantCode || File.Exists(file), "the C# blocks are written for the game's next start (kiln/data/kiln-quests/native/<mod>.json)")) return;
        if (!File.Exists(file)) return;
        var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var quests = doc["quests"]?.AsObject().Select(x => x.Key).ToList() ?? [];
        var dialogues = doc["dialogues"]?.AsObject().Select(x => x.Key).ToList() ?? [];
        Check(quests.Contains("bq-code") && quests.Contains("bq-code-world") && quests.Contains("bq-twin-cs") && dialogues.Contains("bq-code-talk"), $"it has the C# chain: quests {string.Join(", ", quests)}; dialogues {string.Join(", ", dialogues)}");
        Check(!quests.Contains("bq-data") && !quests.Contains("bq-twin") && !quests.Contains("bq-world") && !dialogues.Contains("bq-talk"), "and none of what the data file defines (the game reads that itself, so nothing is built twice)");
        Info($"{written} quests of this mod are defined");
    }

    // ------------------------------------------------------------------------------------------------ boot 2 -------
    private async Task Boot2()
    {
        var m = NativeQuests.Mod(Me);
        if (!Check(m is { Ok: true }, $"the game's build is ok: {string.Join("; ", m?.Errors ?? [])}")) return;
        Info($"built: {string.Join(", ", m!.Quests.Select(x => x.Id))}");
        Check(new[] { "bq-code", "bq-code-world", "bq-twin-cs" }.All(id => m.Quests.Any(x => x.Id == id)), "the quests written in C# are in the game's journal from the second start");
        Check(new[] { "bq-data", "bq-world", "bq-twin" }.All(id => m.Quests.Count(x => x.Id == id) == 1), "and the data blocks' are still there, once each");
        var q = NativeQuests.Quest(Full("bq-code"));
        if (!Check(q is not null && NativeQuests.ShownByGame(_code!.Definition), "NativeQuests.Quest finds the C# quest, and the engine's quest is shown by the game")) return;
        Check(q!.EntryId == NativeIds.Knowledge(Me, "q.bq-code") && q.Active == NativeIds.Knowledge(Me, "q.bq-code.active") && q.Steps.All(s => s.Done == NativeIds.Knowledge(Me, $"s.bq-code.{s.Id}.done")),
            "its knowledge ids are the managed rule's");
        var all = new[] { q.EntryId, q.Started, q.Active, q.Done }.Concat(q.Steps.SelectMany(s => new[] { s.Visible, s.Active, s.Done })).ToList();
        Check(all.All(id => Knowledge.Info(id) is not null), $"the game's knowledge registry has all {all.Count} of them");
        var npc = NativeQuests.Npc(Full("bq-code-npc"));
        Check(npc is { IsClone: true } && npc.Dialogue == Full("bq-code-talk"), $"the NPC block of C# is built, opening the dialogue its handle named: {npc?.Dialogue}");
        Check(m.Lines.Any(l => l.Dialogue == Full("bq-code-talk")), "and the dialogue's lines are in the game's menu");

        // its steps advance through Quests commands: the player quest, with a synthetic holder
        var full = Full("bq-code");
        Quests.Reset(full, Alice);
        Check(Quests.Start(full, Alice).Ok && Quests.Progress(full, Alice)?.StepId == "first", "the C# quest starts at its first step");
        Quests.Signal("bq.code", Alice);
        Check(Quests.Progress(full, Alice)?.StepId == "second", "a signal moves it to the second step");
        Quests.Signal("bq.code", Alice, 2);
        Check(Quests.StatusOf(full, Alice) == QuestStatus.Done, "and two more finish it");
        Check(await WaitFor(() => Quests.RecentActions.Any(l => l.Contains("status bq-boon", StringComparison.Ordinal)), 40), "its reward status ran too (skipped for a holder with no player)");
        Quests.Reset(full, Alice);
        await WorldMirror("bq-code-world", "bq.cw", 1, 2);
        Exported(wantCode: true);
    }
}
