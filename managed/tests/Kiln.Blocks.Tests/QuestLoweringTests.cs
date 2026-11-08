// SPDX-License-Identifier: MIT
// Quest and dialogue blocks: what they lower to (the QuestDefinition and DialogueDefinition Quests.Define and Dialogues.Define take), in C# and in JSON, and what
// they refuse. No game and no engine: Compiler.Lower stops before the commit.
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class QuestLoweringTests
{
    private static ModBlocks Mod() => new("quest-test", 1) { ManifestClients = "mod" };

    private static BlockHandle Add(ModBlocks mod, string name, IBlock block)
    {
        var h = Compiler.Lower(mod, name, block).Handle;
        mod.Handles[name] = h;
        return h;
    }

    private static QuestDefinition Quest(QuestBlock q, ModBlocks? mod = null, string name = "lost-crates") =>
        ((QuestHandle)Compiler.Lower(mod ?? Mod(), name, q).Handle).Definition;

    private static DialogueDefinition Dialogue(DialogueBlock d, ModBlocks? mod = null, string name = "ilse-talk") =>
        ((DialogueHandle)Compiler.Lower(mod ?? Mod(), name, d).Handle).Definition;

    private static BlockError Refused(Action act) => Assert.Throws<BlockError>(act);

    private static QuestBlock Simple(params StepBlock[] steps) => new() { Name = "Errand", Steps = steps };

    private static StepBlock Step(string id = "go", ObjectiveBlock? objective = null) => new() { Id = id, Text = "Do it", Objective = objective ?? Goal.Custom("x") };

    // ---- a whole quest ----

    private static QuestBlock Crates() => new()
    {
        Name = UiText.Of(("en", "The Lost Crates"), ("de", "Die verlorenen Kisten")), Giver = "ilse", Scope = QuestScope.Party, Source = "Blacksmith", Priority = 60,
        Requires = [Req.Level(8), Req.Not(Req.Quest("other", "failed"))],
        OnStart = [Do.Chat("Good luck")],
        Steps =
        [
            new StepBlock
            {
                Id = "bandits", Title = "Drive off the scavengers", Text = "They took the crates on the old road.",
                Objective = Goal.Kill(tag: "crates", count: 6),
                Spawn = [new StepSpawnBlock { Template = "Enemy_Scavenger_Melee01", Count = 6, Tag = "crates", Near = "ilse", Distance = [60, 120] }],
                Marker = new MarkerBlock { Name = "The Old Road" },
            },
            new StepBlock
            {
                Id = "return", Text = "Bring three logs back", Objective = Goal.Deliver("Material_T1_Wood_Logs", 3, to: "ilse"),
                Rewards = new RewardBlock { Items = { ["Material_T1_Twigs"] = 2 } }, OnDone = [Do.Chat("Thanks")],
            },
        ],
        Rewards = new RewardBlock { Xp = 1500, Items = { ["Weapon_T1_1H_SwordEpic_01"] = 1 }, Knowledge = ["npc:NPC_Workshop_Bard01.unlock"] },
    };

    [Fact]
    public void A_quest_lowers_to_what_the_engine_runs()
    {
        var mod = Mod();
        var ilse = Add(mod, "ilse", new NpcBlock { Name = "Ilse", Body = Bodies.Villager, Was = "ilse-old" });
        var q = Quest(Crates(), mod);
        Assert.Equal("lost-crates", q.Id);
        Assert.Equal("quest-test:lost-crates", q.FullId);
        Assert.Equal("Die verlorenen Kisten", q.Name.For("de"));
        Assert.Equal(QuestJournalMode.Native, q.Journal);
        Assert.Equal(QuestScope.Party, q.Scope);
        Assert.Equal("Blacksmith", q.Source);
        Assert.Equal(60, q.Priority);
        Assert.Equal("ilse-old", q.Giver);   // the NPC block's id in the engine: what it was first named
        Assert.Equal("ilse", ilse.Name);
        Assert.Equal([QuestConditionKind.Level, QuestConditionKind.Not], q.Requires.Select(c => c.Kind));
        Assert.Equal(QuestConditionKind.Quest, q.Requires[1].Children[0].Kind);
        Assert.Equal("failed", q.Requires[1].Children[0].Is);
        Assert.Equal(QuestActionKind.Chat, q.OnStart[0].Kind);

        Assert.Equal(["bandits", "return"], q.Steps.Select(s => s.Id));
        var bandits = q.Steps[0];
        Assert.Equal(QuestObjectiveKind.Kill, bandits.Objectives[0].Kind);
        Assert.Equal("crates", bandits.Objectives[0].Tag);
        Assert.Equal(6, bandits.Objectives[0].Count);
        Assert.Equal("Drive off the scavengers", bandits.Title!.For("en"));
        var spawn = Assert.Single(bandits.Spawns);
        Assert.Equal("Enemy_Scavenger_Melee01", spawn.Template);
        Assert.Equal(5, spawn.Retries);
        Assert.Equal(4, spawn.Spread);
        Assert.Equal("ilse-old", spawn.Place!.NearNpc);
        Assert.Equal((60, 120), (spawn.Place.MinDistance, spawn.Place.MaxDistance));
        Assert.Equal("The Old Road", bandits.Marker!.Name!.For("en"));

        var ret = q.Steps[1];
        Assert.Equal("ilse-old", ret.Objectives[0].To);
        Assert.Equal(QuestObjectiveKind.Deliver, ret.Objectives[0].Kind);
        // a step's reward comes before its own hooks
        Assert.Equal([QuestActionKind.Give, QuestActionKind.Chat], ret.OnDone.Select(a => a.Kind));
        Assert.Equal([QuestActionKind.Xp, QuestActionKind.Give, QuestActionKind.Knowledge], q.Rewards.Select(a => a.Kind));
        Assert.Equal(1500u, q.Rewards[0].Value);
        Assert.Equal("Weapon_T1_1H_SwordEpic_01", q.Rewards[1].Name);
    }

    [Fact]
    public void The_json_form_and_the_csharp_form_lower_to_the_same()
    {
        const string json = """
            { "name": { "en": "The Lost Crates", "de": "Die verlorenen Kisten" }, "giver": "ilse", "scope": "party", "source": "Blacksmith", "priority": 60,
              "requires": [ { "level": 8 }, { "not": { "quest": "other", "is": "failed" } } ],
              "onStart": [ { "chat": "Good luck" } ],
              "steps": [
                { "id": "bandits", "title": "Drive off the scavengers", "text": "They took the crates on the old road.",
                  "objective": { "kill": { "tag": "crates", "count": 6 } },
                  "spawn": { "template": "Enemy_Scavenger_Melee01", "count": 6, "tag": "crates", "near": "ilse", "distance": [60, 120] },
                  "marker": { "name": "The Old Road" } },
                { "id": "return", "text": "Bring three logs back", "objective": { "deliver": { "item": "Material_T1_Wood_Logs", "count": 3, "to": "ilse" } },
                  "rewards": { "items": { "Material_T1_Twigs": 2 } }, "onDone": [ { "chat": "Thanks" } ] } ],
              "rewards": { "xp": 1500, "items": { "Weapon_T1_1H_SwordEpic_01": 1 }, "knowledge": [ "npc:NPC_Workshop_Bard01.unlock" ] } }
            """;
        var fromJson = BlockJson.Parse<QuestBlock>(json);
        var a = Compiler.Lower(Mod(), "lost-crates", fromJson).Low.Json.ToJsonString();
        var b = Compiler.Lower(Mod(), "lost-crates", Crates()).Low.Json.ToJsonString();
        Assert.Equal(b, a);
        var qa = Quest(fromJson);
        var qb = Quest(Crates());
        Assert.Equal(qb.Steps[0].Spawns[0].Place!.NearNpc, qa.Steps[0].Spawns[0].Place!.NearNpc);
        Assert.Equal(qb.Steps[0].Marker!.Name!.For("en"), qa.Steps[0].Marker!.Name!.For("en"));
        Assert.Equal(qb.Rewards.Select(x => (x.Kind, x.Name, x.Value)), qa.Rewards.Select(x => (x.Kind, x.Name, x.Value)));
        Assert.Equal(qb.Name.For("de"), qa.Name.For("de"));
        Assert.Contains("\"quest\"", a);
        Assert.Contains("\"lost-crates\"", a);
    }

    [Fact]
    public void Every_objective_lowers_the_way_the_json_loader_reads_it()
    {
        var q = Quest(new QuestBlock
        {
            Name = "All", Steps =
            [
                new StepBlock
                {
                    Id = "all", Text = "x", Site = new PlaceBlock { Near = "player", Distance = [30, 45] },
                    Objectives =
                    [
                        new ObjectiveBlock { Kill = new KillGoal { Family = "Vukah", Count = 2, Near = [1, 2, 3], Radius = 30, CountDestroyed = true }, Text = "Hunt" },
                        Goal.Collect("Material_T1_Wood_Logs", 5), Goal.Deliver("Material_T1_Wood_Logs", 1, "ilse"), Goal.ReachSite(8), Goal.Reach(1, 2, 3), Goal.Talk("npc:NPC_Workshop_Bard01", node: "hi"),
                        Goal.Craft("Weapon_T1_1H_SwordEpic_01"), Goal.Build("Building_Chair"), Goal.Knowing("npc:NPC_Workshop_Bard01.unlock"), Goal.Custom("rung", 3),
                    ],
                },
            ],
        });
        var o = q.Steps[0].Objectives;
        Assert.Equal(Enum.GetValues<QuestObjectiveKind>().Order(), o.Select(x => x.Kind).Distinct().Order());
        Assert.True(o[0].CountDestroyed);
        Assert.Equal(30, o[0].Radius);
        Assert.Equal(new WorldPosition(1, 2, 3), o[0].Near);
        Assert.Equal("Hunt", o[0].Text!.For("en"));
        Assert.Equal("ilse", o[2].To);   // no NPC block of that name here: an NPC of quests/*.json, as written
        Assert.True(o[3].Site);
        Assert.Equal(8, o[3].Radius);
        Assert.Equal(new WorldPosition(1, 2, 3), o[4].At);
        Assert.Equal("npc:NPC_Workshop_Bard01", o[5].Npc);   // a name with a colon that is no block's stays as written
        Assert.Equal("hi", o[5].Node);
        Assert.Equal("Building_Chair", o[7].Item);
        Assert.Equal(("rung", 3), (o[9].Signal, o[9].Count));
        Assert.True(q.Steps[0].Site!.NearPlayer);
        Assert.Equal((30, 45), (q.Steps[0].Site!.MinDistance, q.Steps[0].Site!.MaxDistance));
    }

    [Fact]
    public void Conditions_and_actions_lower_to_the_engines_kinds()
    {
        var q = Quest(new QuestBlock
        {
            Name = "C", Requires = [Req.All(Req.Level(3), Req.Any(Req.Item("Material_T1_Twigs", 2), Req.Knowledge("npc:X.unlock")), Req.Step("other.a", "active"), Req.Predicate("p"))],
            OnStart =
            [
                Do.Xp(10), Do.Give("Material_T1_Twigs", 2), Do.Take("Material_T1_Twigs"), Do.Knowledge("npc:X.unlock", "world"), Do.Revoke("npc:X.unlock"), Do.Start("o", force: true), Do.Complete("o"),
                Do.Fail("o"), Do.Advance("o.a", force: true), Do.Reset("o"), Do.Signal("s", 2), Do.Talk("t", "n"), Do.Call("c"), Do.Teleport(1, 2, 3), Do.Chat("hi"),
                new ActionBlock { Spawn = "Enemy_Vukah_Melee", Count = 2, Offset = [5, 0, 0], Tag = "t", Transient = true }, new ActionBlock { Despawn = "t" },
            ],
            Steps = [Step()],
        });
        var all = q.Requires[0];
        Assert.Equal(QuestConditionKind.All, all.Kind);
        Assert.Equal([QuestConditionKind.Level, QuestConditionKind.Any, QuestConditionKind.Step, QuestConditionKind.Predicate], all.Children.Select(c => c.Kind));
        Assert.Equal(3, all.Children[0].Count);
        Assert.Equal(2, all.Children[1].Children[0].Count);
        Assert.Equal("active", all.Children[2].Is);
        Assert.Equal("p", all.Children[3].Name);
        Assert.Equal(Enum.GetValues<QuestActionKind>().Where(k => k != QuestActionKind.Toast && k != QuestActionKind.Waypoint).Order(), q.OnStart.Select(a => a.Kind).Order());
        Assert.True(q.OnStart.First(a => a.Kind == QuestActionKind.Start).Force);
        Assert.Equal("world", q.OnStart.First(a => a.Kind == QuestActionKind.Knowledge).Target);
        Assert.Equal(0u, q.OnStart.First(a => a.Kind == QuestActionKind.Revoke).Value);   // every flag: the engine's 0 is "all"
        Assert.Equal(new WorldPosition(5, 0, 0), q.OnStart.First(a => a.Kind == QuestActionKind.Spawn).Offset);
        Assert.Equal("n", q.OnStart.First(a => a.Kind == QuestActionKind.Talk).Node);
    }

    // ---- names a block resolves ----

    [Fact]
    public void An_item_block_of_the_mod_is_its_game_name_and_an_npc_block_its_engine_id()
    {
        var mod = Mod();
        var gem = Add(mod, "gem", new ItemBlock { Name = "Gem" });
        var q = Quest(new QuestBlock
        {
            Name = "N", Steps = [new StepBlock { Id = "a", Text = "x", Objectives = [Goal.Collect("gem", 2), Goal.Craft("gem")] }],
            Rewards = new RewardBlock { Items = { ["gem"] = 3 } },
        }, mod);
        Assert.Equal(gem.GameName, q.Steps[0].Objectives[0].Item);
        Assert.Equal(gem.GameName, q.Steps[0].Objectives[1].Item);
        Assert.Equal(gem.GameName, q.Rewards[0].Name);
        Assert.Equal(3, q.Rewards[0].Count);
    }

    [Fact]
    public void A_name_that_is_a_block_of_the_wrong_kind_is_refused_with_the_field()
    {
        var mod = Mod();
        Add(mod, "frost", new StatusBlock { Name = "Frost", Seconds = 3 });
        var e = Refused(() => Quest(new QuestBlock { Name = "N", Giver = "frost", Steps = [Step()] }, mod));
        Assert.Equal("giver", e.Field);
        Assert.Contains("a status block, not an NPC", e.Message);
        e = Refused(() => Quest(new QuestBlock { Name = "N", Steps = [new StepBlock { Id = "a", Text = "x", Objective = Goal.Collect("frost") }] }, mod));
        Assert.Equal("steps[0].objective.collect.item", e.Field);
        Assert.Contains("not an item", e.Message);
        e = Refused(() => Quest(new QuestBlock { Name = "N", Requires = [Req.Quest("frost")], Steps = [Step()] }, mod));
        Assert.Equal("requires[0].quest", e.Field);
    }

    [Fact]
    public void An_other_mods_npc_keeps_its_mod_in_the_name()
    {
        // not a block of another loaded mod here: the name stays as written, for the engine to resolve
        var q = Quest(new QuestBlock { Name = "N", Giver = "other-mod:ilse", Steps = [Step()] });
        Assert.Equal("other-mod:ilse", q.Giver);
    }

    // ---- rewards ----

    [Fact]
    public void A_reward_status_is_a_code_action_and_a_loot_table_waits_for_its_blocks()
    {
        var mod = Mod();
        Add(mod, "chilled", new StatusBlock { Name = "Chilled", Seconds = 8 });
        var q = Quest(new QuestBlock
        {
            Name = "R", Steps = [Step()],
            Rewards = new RewardBlock { Xp = 5, Items = { ["Material_T1_Twigs"] = 1 }, Status = "chilled" },
        }, mod);
        Assert.Equal([QuestActionKind.Xp, QuestActionKind.Give, QuestActionKind.Call], q.Rewards.Select(a => a.Kind));
        Assert.NotNull(q.Rewards[2].Run);

        // a game status by GUID, and one by id (the GUID is found when it is paid)
        Assert.NotNull(Quest(new QuestBlock { Name = "R", Steps = [Step()], Rewards = new RewardBlock { Status = "6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77" } }, mod).Rewards[0].Run);
        Assert.NotNull(Quest(new QuestBlock { Name = "R", Steps = [Step()], Rewards = new RewardBlock { Status = "0x5f5d2f5c" } }, mod).Rewards[0].Run);
        var e = Refused(() => Quest(new QuestBlock { Name = "R", Steps = [Step()], Rewards = new RewardBlock { Status = "no-such" } }, mod));
        Assert.Equal("rewards.status", e.Field);

        QuestRewards.LootTable = null;
        e = Refused(() => Quest(new QuestBlock { Name = "R", Steps = [Step()], Rewards = new RewardBlock { LootTable = "boss-drops" } }, mod));
        Assert.Equal("rewards.lootTable", e.Field);
        Assert.Contains("loot table", e.Message);
        try
        {
            QuestRewards.LootTable = (ctx, table, path) => [QuestAction.Item($"from-{table}")];
            var withLoot = Quest(new QuestBlock { Name = "R", Steps = [Step()], Rewards = new RewardBlock { Xp = 1, LootTable = "boss-drops" } }, mod);
            Assert.Equal("from-boss-drops", withLoot.Rewards[1].Name);
        }
        finally { QuestRewards.LootTable = null; }
    }

    [Fact]
    public void A_reward_that_pays_nothing_is_refused()
    {
        var e = Refused(() => Quest(new QuestBlock { Name = "R", Steps = [Step()], Rewards = new RewardBlock() }));
        Assert.Equal("rewards", e.Field);
    }

    // ---- what is refused ----

    [Theory]
    [InlineData("a.b")]
    [InlineData("my quest")]
    public void A_quest_id_is_letters_digits_underscore_and_dash(string name)
    {
        var e = Refused(() => Quest(Simple(Step()), name: name));
        Assert.Contains("letters, digits", e.Message);
    }

    [Fact]
    public void Steps_are_checked_before_anything_registers()
    {
        Assert.Equal("steps", Refused(() => Quest(new QuestBlock { Name = "N", Steps = [] })).Field);
        Assert.Equal("steps[1].id", Refused(() => Quest(Simple(Step("a"), Step("a")))).Field);
        Assert.Equal("steps[0].id", Refused(() => Quest(Simple(Step("a b")))).Field);
        Assert.Equal("steps[0].text", Refused(() => Quest(Simple(new StepBlock { Id = "a", Objective = Goal.Custom("x") }))).Field);
        Assert.Equal("steps[0].objective.kill.count", Refused(() => Quest(Simple(Step("a", Goal.Kill(tag: "t", count: 0))))).Field);
        Assert.Equal("steps[0].objective.kill", Refused(() => Quest(Simple(Step("a", new ObjectiveBlock { Kill = new KillGoal() })))).Field);
        Assert.Equal("steps[0].objective", Refused(() => Quest(Simple(Step("a", new ObjectiveBlock())))).Field);
        Assert.Equal("steps[0].objective", Refused(() => Quest(Simple(Step("a", new ObjectiveBlock { Knowledge = "x", Custom = new CustomGoal { Signal = "y" } })))).Field);
        Assert.Equal("steps[0].objective.reach", Refused(() => Quest(Simple(Step("a", new ObjectiveBlock { Reach = new ReachGoal() })))).Field);
        Assert.Equal("steps[0].objective.reach.at", Refused(() => Quest(Simple(Step("a", new ObjectiveBlock { Reach = new ReachGoal { At = [1, 2] } })))).Field);
        Assert.Equal("steps[0].site.distance", Refused(() => Quest(Simple(new StepBlock { Id = "a", Text = "x", Objective = Goal.Custom("x"), Site = new PlaceBlock { Distance = [1, 2, 3] } }))).Field);
        Assert.Equal("steps[0].marker.at", Refused(() => Quest(Simple(new StepBlock { Id = "a", Text = "x", Objective = Goal.Custom("x"), Marker = new MarkerBlock { At = [1] } }))).Field);
    }

    [Fact]
    public void A_reach_of_the_site_needs_a_site_and_a_hint_is_one_of_the_games()
    {
        var e = Refused(() => Quest(Simple(Step("a", Goal.ReachSite()))));
        Assert.Equal("steps[0].objective.reach.site", e.Field);
        Assert.Contains("site", e.Message);
        Assert.Equal("hint", Refused(() => Dialogue(new DialogueBlock { Hint = "Loud", Nodes = { ["start"] = new() { Text = "x" } } })).Field);
    }

    [Fact]
    public void A_source_is_a_quest_source_of_the_game()
    {
        var e = Refused(() => Quest(new QuestBlock { Name = "N", Source = "Wizard", Steps = [Step()] }));
        Assert.Equal("source", e.Field);
    }

    [Fact]
    public void Conditions_and_actions_are_one_thing_each()
    {
        Assert.Equal("requires[0]", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [new ConditionBlock { Level = 3, Knowledge = "x" }], Steps = [Step()] })).Field);
        Assert.Equal("requires[0]", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [new ConditionBlock()], Steps = [Step()] })).Field);
        Assert.Equal("requires[0].is", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [new ConditionBlock { Level = 3, Is = "done" }], Steps = [Step()] })).Field);
        Assert.Equal("requires[0].is", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [Req.Quest("x", "sleeping")], Steps = [Step()] })).Field);
        Assert.Equal("requires[0].is", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [Req.Step("x.a", "available")], Steps = [Step()] })).Field);
        Assert.Equal("requires[0].step", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [Req.Step("nodot")], Steps = [Step()] })).Field);
        Assert.Equal("requires[0].all", Refused(() => Quest(new QuestBlock { Name = "N", Requires = [Req.All()], Steps = [Step()] })).Field);
        Assert.Equal("onStart[0]", Refused(() => Quest(new QuestBlock { Name = "N", OnStart = [new ActionBlock { Start = "a", Complete = "b" }], Steps = [Step()] })).Field);
        Assert.Equal("onStart[0]", Refused(() => Quest(new QuestBlock { Name = "N", OnStart = [new ActionBlock()], Steps = [Step()] })).Field);
        Assert.Equal("onFail[0].advance", Refused(() => Quest(new QuestBlock { Name = "N", OnFail = [Do.Advance("nodot")], Steps = [Step()] })).Field);
        Assert.Equal("onStart[0].target", Refused(() => Quest(new QuestBlock { Name = "N", OnStart = [Do.Knowledge("npc:X.unlock", "everyone")], Steps = [Step()] })).Field);
        Assert.Equal("onStart[0].count", Refused(() => Quest(new QuestBlock { Name = "N", OnStart = [Do.Give("Material_T1_Twigs", 0)], Steps = [Step()] })).Field);
    }

    // ---- dialogues ----

    private static DialogueBlock Talk() => new()
    {
        Start = "hello", Hint = "MainQuest", Greet = true,
        Greetings = [new GreetingBlock { Text = "Back again", If = Req.Quest("lost-crates", "done") }],
        Farewell = [new UiText("Bye")],
        Nodes =
        {
            ["hello"] = new DialogueNodeBlock
            {
                Text = "Scavengers took our crates.",
                Options =
                [
                    new OptionBlock { Text = "I'll find them.", If = Req.Quest("lost-crates", "available"), Do = [Do.Start("lost-crates")], Reply = "Thank you.", Hint = "Attention", Once = true },
                    new OptionBlock { Text = "Here.", If = Req.Step("lost-crates.return", "active"), Do = [Do.Advance("lost-crates.return")], Goto = "thanks" },
                    new OptionBlock { Text = "Farewell.", Goto = "end", Close = true },
                ],
            },
            ["thanks"] = new DialogueNodeBlock { Text = "The road is safe again.", Do = [Do.Xp(5)] },
        },
    };

    [Fact]
    public void A_dialogue_lowers_to_a_native_conversation()
    {
        var d = Dialogue(Talk());
        Assert.Equal("ilse-talk", d.Id);
        Assert.True(d.Native);
        Assert.Equal("hello", d.Start);
        Assert.Equal("MainQuest", d.Hint);
        Assert.True(d.Greet);
        Assert.Equal("Back again", d.Greetings[0].Text.For("en"));
        Assert.Equal(QuestConditionKind.Quest, d.Greetings[0].If!.Kind);
        Assert.Equal("Bye", d.Farewell[0].For("en"));
        var hello = d.Nodes["hello"];
        Assert.Equal(3, hello.Options.Count);
        Assert.Equal("Thank you.", hello.Options[0].Reply!.For("en"));
        Assert.True(hello.Options[0].Once);
        Assert.Equal("Attention", hello.Options[0].Hint);
        Assert.Equal(QuestActionKind.Start, hello.Options[0].Do[0].Kind);
        Assert.Equal("thanks", hello.Options[1].Goto);
        Assert.True(hello.Options[2].Close);
        Assert.Equal(QuestActionKind.Xp, d.Nodes["thanks"].Do[0].Kind);
    }

    [Fact]
    public void The_json_dialogue_lowers_the_same_as_the_csharp_one()
    {
        const string json = """
            { "start": "hello", "hint": "MainQuest", "greet": true,
              "greetings": [ { "text": "Back again", "if": { "quest": "lost-crates", "is": "done" } } ],
              "farewell": [ "Bye" ],
              "nodes": {
                "hello": { "text": "Scavengers took our crates.", "options": [
                  { "text": "I'll find them.", "if": { "quest": "lost-crates", "is": "available" }, "do": [ { "start": "lost-crates" } ], "reply": "Thank you.", "hint": "Attention", "once": true },
                  { "text": "Here.", "if": { "step": "lost-crates.return", "is": "active" }, "do": [ { "advance": "lost-crates.return" } ], "goto": "thanks" },
                  { "text": "Farewell.", "goto": "end", "close": true } ] },
                "thanks": { "text": "The road is safe again.", "do": [ { "xp": 5 } ] } } }
            """;
        var a = Compiler.Lower(Mod(), "ilse-talk", BlockJson.Parse<DialogueBlock>(json)).Low.Json.ToJsonString();
        var b = Compiler.Lower(Mod(), "ilse-talk", Talk()).Low.Json.ToJsonString();
        Assert.Equal(b, a);
        var da = Dialogue(BlockJson.Parse<DialogueBlock>(json));
        var db = Dialogue(Talk());
        foreach (var node in db.Nodes.Keys)
        {
            Assert.Equal(db.Nodes[node].Options.Count, da.Nodes[node].Options.Count);
            for (var i = 0; i < db.Nodes[node].Options.Count; i++)
            {
                Assert.Equal(db.Nodes[node].Options[i].Text.For("en"), da.Nodes[node].Options[i].Text.For("en"));
                Assert.Equal(db.Nodes[node].Options[i].If?.Kind, da.Nodes[node].Options[i].If?.Kind);
                Assert.Equal(db.Nodes[node].Options[i].Goto, da.Nodes[node].Options[i].Goto);
            }
        }
    }

    [Fact]
    public void A_dialogue_that_goes_nowhere_is_refused()
    {
        var e = Refused(() => Dialogue(new DialogueBlock { Nodes = { ["start"] = new() { Text = "x", Options = [new OptionBlock { Text = "y", Goto = "nowhere" }] } } }));
        Assert.Equal("nodes", e.Field);
        Assert.Contains("nowhere", e.Message);
        Assert.Contains("no start node", Refused(() => Dialogue(new DialogueBlock { Start = "hello", Nodes = { ["start"] = new() { Text = "x" } } })).Message);
        Assert.Equal("nodes", Refused(() => Dialogue(new DialogueBlock())).Field);
        Assert.Equal("nodes.start.options[0].text", Refused(() => Dialogue(new DialogueBlock { Nodes = { ["start"] = new() { Options = [new OptionBlock { Text = null! }] } } })).Field);
        Assert.Equal("greetings[0].text", Refused(() => Dialogue(new DialogueBlock { Greetings = [new GreetingBlock { Text = null! }], Nodes = { ["start"] = new() } })).Field);
    }

    // ---- an NPC names a block dialogue ----

    [Fact]
    public void An_npc_names_a_dialogue_block_by_name_or_by_handle()
    {
        var mod = Mod();
        var talk = (DialogueHandle)Add(mod, "tobin-talk", Talk());
        var byName = Compiler.Lower(mod, "tobin", new NpcBlock { Name = "Tobin", Body = Bodies.Fisher, TalkName = "tobin-talk" }).Low.Json["npc"]!;
        Assert.Equal("tobin-talk", (string?)byName["dialogue"]);
        var byHandle = Compiler.Lower(mod, "tobin", new NpcBlock { Name = "Tobin", Body = Bodies.Fisher, Talk = talk }).Low.Json["npc"]!;
        Assert.Equal("tobin-talk", (string?)byHandle["dialogue"]);
        // a name that is no block is one of quests/*.json: as written
        var file = Compiler.Lower(mod, "tobin", new NpcBlock { Name = "Tobin", Body = Bodies.Fisher, TalkName = "from-a-file" }).Low.Json["npc"]!;
        Assert.Equal("from-a-file", (string?)file["dialogue"]);
    }

    [Fact]
    public void An_npc_that_names_another_kind_of_block_as_its_dialogue_is_refused()
    {
        var mod = Mod();
        Add(mod, "some-quest", Simple(Step()));
        var e = Refused(() => Compiler.Lower(mod, "tobin", new NpcBlock { Name = "Tobin", Body = Bodies.Fisher, TalkName = "some-quest" }));
        Assert.Equal("talk", e.Field);
        Assert.Contains("a quest block, not a dialogue", e.Message);
        e = Refused(() => Quest(new QuestBlock { Name = "N", OnStart = [Do.Talk("some-quest")], Steps = [Step()] }, mod));
        Assert.Equal("onStart[0].talk", e.Field);
    }

    // ---- JSON ----

    [Fact]
    public void Json_shapes_that_c_writes_as_values()
    {
        var q = BlockJson.Parse<QuestBlock>("""
            { "name": "N", "steps": [
                { "id": "a", "text": { "en": "Hi", "de": "Hallo" }, "marker": false, "objective": { "custom": "rung" }, "spawn": [ { "template": "T1" }, { "template": "T2", "at": [1, 2, 3] } ] },
                { "id": "b", "text": "x", "marker": { "at": [1, 2, 3], "waypoint": false }, "objectives": [ { "custom": { "signal": "s", "count": 4 } } ], "spawn": { "template": "T3" } } ] }
            """);
        var def = Quest(q);
        Assert.True(def.Steps[0].NoMarker);
        Assert.Equal("Hallo", def.Steps[0].Text.For("de"));
        Assert.Equal(("rung", 1), (def.Steps[0].Objectives[0].Signal, def.Steps[0].Objectives[0].Count));
        Assert.Equal(2, def.Steps[0].Spawns.Count);
        Assert.Null(def.Steps[0].Spawns[0].Place);
        Assert.Equal(new WorldPosition(1, 2, 3), def.Steps[0].Spawns[1].Place!.At);
        Assert.Equal(new WorldPosition(1, 2, 3), def.Steps[1].Marker!.At);
        Assert.False(def.Steps[1].Marker!.Waypoint);
        Assert.Equal(("s", 4), (def.Steps[1].Objectives[0].Signal, def.Steps[1].Objectives[0].Count));
        Assert.Single(def.Steps[1].Spawns);
    }

    [Theory]
    [InlineData("""{ "name": "N", "steps": [ { "id": "a", "objective": { "custom": "x" } } ], "bogus": 1 }""")]
    [InlineData("""{ "name": "N", "steps": [ { "id": "a", "objective": { "kill": { "tag": "t", "cunt": 2 } } } ] }""")]
    [InlineData("""{ "steps": [ { "id": "a" } ] }""")]
    [InlineData("""{ "name": "N" }""")]
    [InlineData("""{ "name": { }, "steps": [] }""")]
    [InlineData("""{ "name": "N", "scope": "galaxy", "steps": [] }""")]
    public void Unknown_keys_missing_names_and_bad_values_are_refused_by_the_reader(string json) =>
        Assert.Throws<JsonException>(() => BlockJson.Parse<QuestBlock>(json));

    [Fact]
    public void The_sections_are_in_the_data_loaders_table()
    {
        var kinds = ((IEnumerable<(string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)>)QuestBlockJson.Kinds).Select(k => k.Section);
        Assert.Equal(["quests", "dialogues"], kinds);
        var shown = Compiler.Lower(Mod(), "lost-crates", Crates()).Low;
        Assert.Contains("quest lost-crates: 2 step(s), 3 reward(s)", shown.Summary());
        Assert.Contains("dialogue ilse-talk: 2 node(s)", Compiler.Lower(Mod(), "ilse-talk", Talk()).Low.Summary());
    }

    [Fact]
    public void A_quest_asks_for_the_mod_on_every_players_game()
    {
        var mod = new ModBlocks("quest-test", 1) { ManifestClients = "kiln" };
        var e = Refused(() => Compiler.Lower(mod, "q", Simple(Step())));
        Assert.Contains("clients", e.Message);
        Assert.Contains("mod", e.Message);
        e = Refused(() => Compiler.Lower(mod, "d", Talk()));
        Assert.Contains("clients", e.Message);
    }

    [Fact]
    public void The_lowered_report_names_the_quest()
    {
        var j = Compiler.Lower(Mod(), "lost-crates", Crates()).Low.Json["quest"]!;
        Assert.Equal("lost-crates", (string?)j["id"]);
        Assert.Equal("party", (string?)j["scope"]);
        Assert.Equal(["xp 1500", "item Weapon_T1_1H_SwordEpic_01 x1", "knowledge npc:NPC_Workshop_Bard01.unlock"], j["rewards"]!.AsArray().Select(x => (string?)x));
        Assert.Equal(["kill x6"], j["steps"]![0]!["objectives"]!.AsArray().Select(x => (string?)x));
    }
}
