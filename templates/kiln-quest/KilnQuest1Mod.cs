using Kiln;

namespace KilnQuest1;

/// <summary>A second errand, written in C#: a carter, his conversation and a quest he gives. blocks/ilse.json is another one, as data, that names the status
/// defined here. A block's name is its id (the quest's, the NPC's, the dialogue's): keep it, and the ids of the steps, because players' progress is filed under them.</summary>
public sealed class KilnQuest1Mod : Mod
{
    public override void OnLoad()
    {
        // a status for a reward: faster for two minutes
        Blocks.Define("blessed", new StatusBlock
        {
            Name = "Blessed",
            Description = "The road feels shorter.",
            Kind = StatusKind.Buff,
            Seconds = 120,
            Modifies = { [Stat.MoveSpeed] = 0.1 },
        });

        // what Bram says: a node is a greeting and its options, each with a condition (If), what it does (Do) and what he answers (Reply)
        var talk = Blocks.Define("bram-talk", new DialogueBlock
        {
            Start = "hello",
            Nodes =
            {
                ["hello"] = new DialogueNodeBlock
                {
                    Text = "The old road needs wood, and my back needs a rest.",
                    Options =
                    [
                        new OptionBlock { Text = "I'll fetch it.", If = Req.Quest("road-work", "available"), Do = [Do.Start("road-work")], Reply = "Five logs will do." },
                        new OptionBlock { Text = "Here are your logs.", If = Req.Step("road-work.logs", "active"), Do = [Do.Advance("road-work.logs")], Reply = "That mends the road. Take this." },
                    ],
                },
            },
        });

        // the NPC: a carter on the game's villager body, standing 8 to 14 metres from the first player in game, once per world
        Blocks.Define("bram", new NpcBlock
        {
            Name = "Bram the Carter",
            Body = Bodies.Villager,
            Talk = talk,                                   // the handle; "talk": "bram-talk" in JSON
            Place = new NpcPlace { Distance = [8, 14] },
        });

        // the quest: one step, collect five logs and hand them in; its reward is a bundle
        Blocks.Define("road-work", new QuestBlock
        {
            Name = "Road Work",
            Giver = "bram",
            Steps =
            [
                new StepBlock
                {
                    Id = "logs",
                    Text = "Bring Bram five logs.",
                    Objective = Goal.Deliver("Material_T1_Wood_Logs", 5, to: "bram"),
                },
            ],
            Rewards = new RewardBlock { Xp = 800, Items = { ["Material_T1_Twigs"] = 10 }, Status = "blessed" },
        });
    }
}
