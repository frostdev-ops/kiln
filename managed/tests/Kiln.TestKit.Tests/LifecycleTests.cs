// SPDX-License-Identifier: MIT
using TkLifeBase;
using TkLifeUser;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class LifecycleTests
{
    [Fact]
    public void TwoModsSeeTheGamesOrder()
    {
        List<(string Mod, string Text)> order;
        using (var kiln = new KilnHarness())
        {
            // given in the "wrong" order: the loader puts the dependency first
            var user = kiln.Load<TkLifeUserMod>(TestMods.User());
            var b = kiln.Load<TkLifeBaseMod>(TestMods.Base());
            Assert.Equal(ModState.Pending, user.State);
            Assert.Equal(HarnessState.Created, kiln.State);
            kiln.Step();
            Assert.Equal(HarnessState.Running, kiln.State);
            Assert.True(user.IsLoaded && b.IsLoaded);
            Assert.Equal(1UL, Lifecycle.StartedAtStep);
            Assert.True(Lifecycle.ModsLoaded);
            kiln.Step(3);
            kiln.Stop();
            Assert.Equal(HarnessState.Stopped, kiln.State);
            Assert.Equal(ModState.Unloaded, b.State);
            order = kiln.Logs.Where(l => l.ModId is not null && l.Text.StartsWith("life: ", StringComparison.Ordinal))
                .Select(l => (l.ModId!, l.Text[6..])).ToList();
        }
        Assert.Equal(new List<(string, string)>
        {
            ("tk-base", "OnLoad phase Booting"),
            ("tk-user", "OnLoad phase Booting; tk-base loaded True"),
            ("tk-base", "ModsLoaded tk-base,tk-user"),
            ("tk-user", "ModsLoaded tk-base,tk-user"),
            ("tk-base", "Starting step 1 phase Starting"),
            ("tk-user", "Starting step 1 phase Starting"),
            ("tk-base", "Started step 1 first-step at 1"),
            ("tk-user", "Started step 1 first-step at 1"),
            ("tk-base", "Stopping SIGINT phase Stopping"),
            ("tk-user", "Stopping SIGINT phase Stopping"),
            ("tk-user", "OnUnload phase Stopping"),
            ("tk-base", "OnUnload phase Stopping"),
        }, order);
    }

    [Fact]
    public void ATestCanStepBetweenStartingAndStarted()
    {
        using var kiln = new KilnHarness(new KilnHarnessOptions { WaitForWorldRunning = true });
        kiln.Load<TkLifeBaseMod>(TestMods.Base());
        kiln.Step(5);
        Assert.Equal(HarnessState.Starting, kiln.State);
        Assert.Equal(ServerPhase.Starting, Lifecycle.Phase);
        Assert.Empty(kiln.Lines("tk-base", "life: Started"));
        kiln.ReportWorldRunning();
        kiln.Step();
        Assert.Equal(ServerPhase.Running, Lifecycle.Phase);
        Assert.Equal(["life: Started step 6 game-log at 6"], kiln.Lines("tk-base", "life: Started"));
    }

    [Fact]
    public void TheGamesShutdownLineStopsTheServerAndDisposeStopsTheRest()
    {
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkLifeBaseMod>(TestMods.Base());
            kiln.Step();
            kiln.GameLog("[gameflow] Trigger gameflow shutdown");
            kiln.Step();
            Assert.Equal(HarnessState.Stopped, kiln.State);
            Assert.Equal(["life: Stopping game-log phase Stopping"], kiln.Lines("tk-base", "life: Stopping"));
            kiln.Step();  // a stopping server steps on, with nothing loaded
        }
        string folder;
        List<string> stopping;
        using (var kiln = new KilnHarness())
        {
            folder = kiln.ServerDirectory;
            kiln.Load<TkLifeBaseMod>(TestMods.Base());
            kiln.Step();
            kiln.Dispose();
            stopping = kiln.Lines("tk-base", "life: Stopping");
        }
        Assert.Equal(["life: Stopping SIGINT phase Stopping"], stopping);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void AModLoadedAfterTheStartIsAHotLoad()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkLifeBaseMod>(TestMods.Base());
        kiln.Step(2);
        var user = kiln.Load<TkLifeUserMod>(TestMods.User());
        Assert.True(user.IsLoaded);
        Assert.Equal(["life: OnLoad phase Running; tk-base loaded True"], kiln.Lines("tk-user", "life: OnLoad"));
        Assert.Empty(kiln.Lines("tk-user", "life: ModsLoaded"));
        Assert.Equal("tk-user", user.Info.Id);
    }
}
