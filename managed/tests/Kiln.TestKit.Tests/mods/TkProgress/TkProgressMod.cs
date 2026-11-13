// SPDX-License-Identifier: MIT
using Kiln;

namespace TkProgress;

/// <summary>A quest and a talent tree defined in code, driven by commands as a player, with their events logged: what
/// Kiln.Quests' and Kiln.Skills' own engines do on the kit's players and saves.</summary>
public sealed class TkProgressMod : Mod
{
    public override void OnLoad()
    {
        Quests.Define(new QuestDefinition
        {
            Id = "tally",
            Name = "The Tally",
            Steps =
            [
                new QuestStep { Id = "gather", Text = "Gather three logs" },
                new QuestStep { Id = "report", Text = "Report back" },
            ],
        });
        Skills.Register(new TalentTree
        {
            Id = "tk-progress:arts",
            Name = "Arts",
            Points = TalentPoints.Own(start: 2, perLevel: 0),
            Nodes =
            [
                new TalentNode { Id = "spark", Cost = 1, Column = 0, Row = 0 },
                new TalentNode { Id = "flare", Cost = 1, Column = 1, Row = 0, Requires = ["spark"] },
                new TalentNode { Id = "core", Cost = 2, Column = 2, Row = 0, Requires = ["flare"] },
                new TalentNode { Id = "forbidden", Cost = 1, Column = 0, Row = 1 },
            ],
        });
        Commands.Register("tkq-start", ctx => ctx.Reply($"start: {Said(Quests.Start("tally", QuestHolder.Of(ctx.Source.Player)))}"));
        Commands.Register("tkq-advance", async ctx => ctx.Reply($"advance: {Said(await Quests.Advance("tally", QuestHolder.Of(ctx.Source.Player)))}"));
        Commands.Register("tkq-complete", ctx => ctx.Reply($"complete: {Said(Quests.Complete("tally", QuestHolder.Of(ctx.Source.Player)))}"));
        Commands.Register("tkq-status", ctx =>
        {
            var p = Quests.Progress("tally", QuestHolder.Of(ctx.Source.Player));
            ctx.Reply($"status: {Quests.StatusOf("tally", QuestHolder.Of(ctx.Source.Player))} step {p?.StepId ?? "-"}");
        });
        Commands.Register("tks-unlock", ctx => ctx.Reply($"unlock: {Said(Skills.Unlock(ctx.Source.Player, "tk-progress:arts", ctx.Args[0]))}"));
        Commands.Register("tks-state", ctx =>
        {
            var holder = Skills.HolderOf(ctx.Source.Player);
            ctx.Reply($"talents: {Skills.Points("tk-progress:arts", holder)}; " +
                      string.Join(",", new[] { "spark", "flare", "core", "forbidden" }.Select(n => $"{n}={Skills.LevelOf("tk-progress:arts", n, holder)}")));
        });
    }

    private static string Said(QuestResult r) => $"ok {r.Ok} {r.Status} {r.Reason}".TrimEnd();

    private static string Said(TalentResult r) => $"ok {r.Ok} level {r.Level} {r.Reason}".TrimEnd();

    [Subscribe]
    private void OnStarted(QuestStartedEvent e) => Log.Info($"quest started: {e.Quest.Id} for {e.Holder.Key}");

    [Subscribe]
    private void OnStep(QuestStepEvent e) => Log.Info($"quest step: {e.Quest.Id} {e.StepId} -> {e.Next?.Id ?? "end"}");

    [Subscribe]
    private void OnCompleted(QuestCompletedEvent e) => Log.Info($"quest completed: {e.Quest.Id} for {e.Holder.Key}");

    [Subscribe]
    private void OnUnlocking(TalentUnlockingEvent e)
    {
        if (e.Node.Id != "forbidden") return;
        e.Reason = "not for you";
        e.Cancel();
    }

    [Subscribe]
    private void OnTalent(TalentChangedEvent e) => Log.Info($"talent: {e.Change.Kind} {e.Change.Node} -> level {e.Change.NewLevel}, {e.Change.Points} left");
}
