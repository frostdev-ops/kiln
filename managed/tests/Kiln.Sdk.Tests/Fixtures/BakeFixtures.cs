// SPDX-License-Identifier: MIT
// Mods for the bake tests: what an OnLoad can do to a build (never return, leave a thread, throw, need the game) and the
// same definitions made in another order.
using Kiln;

namespace BakeFixtures;

internal static class Errands
{
    public static QuestDefinition Quest(string id, string name, int count) => new()
    {
        Id = id,
        Name = UiText.Of(("en", name), ("de", name + " (de)")),
        Scope = QuestScope.World,
        Steps = [new QuestStep { Id = "count", Text = $"Count {count}", Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Custom, Count = count, Progress = _ => 0 }] }],
    };
}

/// <summary>Two quests, the first first.</summary>
public sealed class QuestsAB : Mod
{
    public override void OnLoad()
    {
        Quests.Define(Errands.Quest("alpha", $"Alpha {1.5}", 2));
        Quests.Define(Errands.Quest("beta", "Beta", 3));
    }
}

/// <summary>The same two quests, the other order.</summary>
public sealed class QuestsBA : Mod
{
    public override void OnLoad()
    {
        Quests.Define(Errands.Quest("beta", "Beta", 3));
        Quests.Define(Errands.Quest("alpha", $"Alpha {1.5}", 2));
    }
}

/// <summary>Leaves a foreground thread running and returns: the bake process must still end.</summary>
public sealed class ForegroundThread : Mod
{
    public override void OnLoad()
    {
        new Thread(() => Thread.Sleep(Timeout.Infinite)) { IsBackground = false, Name = "a mod's forgotten thread" }.Start();
        Quests.Define(Errands.Quest("alpha", "Alpha", 2));
    }
}

/// <summary>Never returns from OnLoad: the bake's deadline ends it.</summary>
public sealed class NeverReturns : Mod
{
    public override void OnLoad() => Thread.Sleep(Timeout.Infinite);
}

/// <summary>Throws from OnLoad.</summary>
public sealed class Throws : Mod
{
    public override void OnLoad() => throw new InvalidOperationException("this mod's OnLoad throws");
}

/// <summary>Needs a service only the game has (kiln.assets).</summary>
public sealed class NeedsTheGame : Mod
{
    public override void OnLoad()
    {
        Quests.Define(Errands.Quest("alpha", "Alpha", 2));
        Assets.RegisterIcon(Guid.NewGuid(), new byte[] { 1, 2, 3 });
    }
}
