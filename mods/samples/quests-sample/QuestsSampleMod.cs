// SPDX-License-Identifier: MIT
// The Kiln.Quests sample's code. quests/sample.json is the Warden's chain; nothing here is needed to show it. This adds one
// quest in C#, to show that code gets the same game UI with no flag: the journal entry, its counter and the Warden's topic. The
// game's journal is built at boot, before any mod runs, so Kiln writes what code defines down for the next start; this sample
// ships that file as quests/generated/quests-sample.json, so the first start has it too. Docs: docs/kiln/modding/quests.md "Code".
using Kiln;

namespace QuestsSample;

public sealed class QuestsSampleMod : Mod
{
    public override void OnLoad()
    {
        Quests.Define(new QuestDefinition
        {
            Id = "warden_tally",
            Name = UiText.Of(("en", "The Warden's Tally"), ("de", "Die Zählung des Wächters")),
            Giver = "warden",
            Source = "Grassland",
            Requires = [QuestCondition.Quest("ember_errand", "done")],
            Steps =
            [
                new QuestStep
                {
                    Id = "tally",
                    Text = UiText.Of(("en", "Carry ten logs at once"), ("de", "Trage zehn Stämme auf einmal")),
                    Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Custom, Count = 10, Progress = c => LogsOf(c.Player) }],
                },
            ],
            Rewards = [QuestAction.Experience(50)],
        });
    }

    private static int LogsOf(Player? p) => p is null || !Items.Available ? 0 : Items.Read(p.Entity)?.Count(new ItemId(Items.Lookup("Material_T1_Wood_Logs")?.Id.Value ?? 0)) ?? 0;

    [Subscribe]
    private void OnState(QuestStateChangedEvent e)
    {
        if (e.Quest.ModId == Context.Info.Id) Log.Info($"quests-sample: {e}");
    }
}
