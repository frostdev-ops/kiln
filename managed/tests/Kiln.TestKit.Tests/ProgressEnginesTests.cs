// SPDX-License-Identifier: MIT
// Kiln.Quests' and Kiln.Skills' engines are managed code: a quest or a talent tree a mod defines runs on the kit's fake
// players and saves with no table of its own. The game's journal, markers, skill tree and levels are not faked.
using TkProgress;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class ProgressEnginesTests
{
    private const ulong AdaSteam = 76561198000000901;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static (KilnHarness Kiln, FakePlayer Ada) Start()
    {
        var kiln = new KilnHarness();
        try
        {
            kiln.Load<TkProgressMod>();
            var ada = kiln.Players.Join("Ada", AdaSteam);
            kiln.Step(2);
            return (kiln, ada);
        }
        catch
        {
            kiln.Dispose();
            throw;
        }
    }

    [Fact]
    public void AQuestAModDefinesIsStartedAdvancedAndCompletedForAFakePlayer()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        Assert.Equal("status: Available step -", Said(kiln.RunCommand(ada, "tkq-status")));
        Assert.Equal("start: ok True Active", Said(kiln.RunCommand(ada, "tkq-start")));
        Assert.Single(kiln.Lines("tk-progress", "quest started: tally"));
        Assert.Equal("status: Active step gather", Said(kiln.RunCommand(ada, "tkq-status")));
        Assert.Equal("start: ok False Active it is already active", Said(kiln.RunCommand(ada, "tkq-start")));
        kiln.RunCommand(ada, "tkq-advance");
        kiln.Step();
        Assert.Equal(["quest step: tally gather -> report"], kiln.Lines("tk-progress", "quest step:"));
        Assert.Equal("status: Active step report", Said(kiln.RunCommand(ada, "tkq-status")));
        Assert.Equal("complete: ok True Done", Said(kiln.RunCommand(ada, "tkq-complete")));
        Assert.Single(kiln.Lines("tk-progress", "quest completed: tally"));
        Assert.StartsWith("status: Done", Said(kiln.RunCommand(ada, "tkq-status")));
    }

    [Fact]
    public void ATalentNodeAModDefinesIsUnlockedForAFakePlayerAndReadBack()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        Assert.Equal("talents: 2 of 2 (0 spent); spark=0,flare=0,core=0,forbidden=0", Said(kiln.RunCommand(ada, "tks-state")));
        Assert.StartsWith("unlock: ok False level 0 needs one of:", Said(kiln.RunCommand(ada, "tks-unlock flare")));  // needs spark
        Assert.Equal("unlock: ok True level 1", Said(kiln.RunCommand(ada, "tks-unlock spark")));
        Assert.Equal("unlock: ok True level 1", Said(kiln.RunCommand(ada, "tks-unlock flare")));
        Assert.Contains("needs 2 point(s), 0 available", Said(kiln.RunCommand(ada, "tks-unlock core")));
        Assert.Equal(["talent: Unlock spark -> level 1, 1 left", "talent: Unlock flare -> level 1, 0 left"], kiln.Lines("tk-progress", "talent:"));
        Assert.Equal("talents: 0 of 2 (2 spent); spark=1,flare=1,core=0,forbidden=0", Said(kiln.RunCommand(ada, "tks-state")));
    }

    [Fact]
    public void AHandlerCanRefuseAnUnlock()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        Assert.Contains("not for you", Said(kiln.RunCommand(ada, "tks-unlock forbidden")));
        Assert.Empty(kiln.Lines("tk-progress", "talent:"));
    }
}
