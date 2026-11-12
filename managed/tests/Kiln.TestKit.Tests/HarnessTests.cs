// SPDX-License-Identifier: MIT
using System.Diagnostics;
using TkLifeBase;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class HarnessTests
{
    [Fact]
    public void TheTempServerFolderHasKilnsTreeAndGoesAway()
    {
        string server;
        using (var kiln = new KilnHarness())
        {
            server = kiln.ServerDirectory;
            Assert.Equal(Path.Combine(server, "kiln"), kiln.KilnDirectory);
            foreach (var d in new[] { kiln.ConfigDirectory, kiln.DataDirectory, kiln.ModsDirectory, kiln.LogsDirectory, kiln.ControlDirectory, kiln.StateDirectory })
                Assert.True(Directory.Exists(d), d);
            Assert.Equal(kiln.ConfigDirectory, Config.Directory);
            var mod = kiln.Load<TkLifeBaseMod>(TestMods.Base());
            Assert.True(File.Exists(Path.Combine(kiln.ModsDirectory, "tk-base", "TkLifeBase.dll")));
            Assert.True(File.Exists(Path.Combine(kiln.ModsDirectory, "tk-base", "TkLifeBase.pdb")));
            Assert.False(File.Exists(Path.Combine(kiln.ModsDirectory, "tk-base", "Kiln.Api.dll")));
            kiln.Step();
            Assert.Equal(Path.Combine(kiln.DataDirectory, "tk-base"), mod.Context.DataDirectory);
            Assert.Contains("life: OnLoad", File.ReadAllText(Path.Combine(kiln.LogsDirectory, "kiln.log")));
            Assert.Contains("tk-base", File.ReadAllText(Path.Combine(kiln.StateDirectory, "mods.json")));
        }
        Assert.False(Directory.Exists(server));
    }

    [Fact]
    public void TwoHarnessesOneAfterTheOtherSeeFreshStateAndAWorkingServiceTick()
    {
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkLifeBaseMod>(TestMods.Base());
            kiln.Step();  // ends at step 1, the step the next harness starts with
        }
        using (var kiln = new KilnHarness())
        {
            Assert.Equal(ServerPhase.Booting, Lifecycle.Phase);
            Assert.False(Lifecycle.ModsLoaded);
            Assert.Empty(Mods.All);
            Assert.False(TestMods.Subscribed("tk-base"));
            var seen = new List<ulong>();
            kiln.Run(() => Events.On<Probe>(_ => seen.Add(kiln.CurrentStep)));
            Events.Enqueue(new Probe());  // Kiln's service tick posts it at the start of the next step
            kiln.Step();
            Assert.Equal([1UL], seen);
            Assert.Empty(kiln.Lines("tk-base", "life:"));
        }
    }

    private sealed class Probe : KilnEvent;

    [Fact]
    public async Task ASecondHarnessWaitsForTheFirst()
    {
        var first = new KilnHarness();
        var sw = Stopwatch.StartNew();
        var second = Task.Run(() => new KilnHarness(), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var h = await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(sw.ElapsedMilliseconds >= 300);
    }

    [Fact]
    public void ASecondHarnessGivesUpAfterItsTimeout()
    {
        using var first = new KilnHarness();
        var e = Assert.Throws<TimeoutException>(() => new KilnHarness(new KilnHarnessOptions { WaitForOtherHarness = TimeSpan.FromMilliseconds(200) }));
        Assert.Contains("another KilnHarness is still alive", e.Message);
    }

    [Fact]
    public void AHarnessCannotBeCreatedOnTheTickThread()
    {
        using var kiln = new KilnHarness();
        var e = Assert.Throws<InvalidOperationException>(() => kiln.Run(() => new KilnHarness()));
        Assert.Contains("tick thread", e.Message);
    }

    [Fact]
    public void ExceptionsComeBackWithTheirStackAndCallsNestOnTheTickThread()
    {
        using var kiln = new KilnHarness();
        var e = Assert.Throws<FormatException>(() => kiln.Run(Thrower));
        Assert.Contains(nameof(Thrower), e.StackTrace);
        kiln.Run(() => kiln.Step(2));  // inline on the tick thread
        Assert.Equal(2UL, kiln.CurrentStep);
        Assert.Equal(3, kiln.StepUntil(() => kiln.CurrentStep == 5));
        Assert.Throws<TimeoutException>(() => kiln.StepUntil(() => false, maxSteps: 3));
    }

    [Fact]
    public void AnAbandonedHarnessRefusesEveryCall()
    {
        var kiln = new KilnHarness();
        kiln.TickThread.Abandon("a test blocked the tick thread (watchdog)");
        Assert.Equal(HarnessState.Abandoned, kiln.State);
        var e = Assert.Throws<HarnessStoppedException>(() => kiln.Step());
        Assert.Contains("watchdog", e.Message);
        kiln.Dispose();
        using var next = new KilnHarness();  // the next one is not blocked by it
    }

    [Fact]
    public void AsRunsCodeAsTheMod()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<TkLifeBaseMod>(TestMods.Base());
        kiln.Step();
        var (current, sync) = kiln.As(mod, () => (Mods.Current?.Id, SynchronizationContext.Current is not null));
        Assert.Equal("tk-base", current);
        Assert.True(sync);
        Assert.Null(kiln.Run(() => Mods.Current));
        kiln.As(mod, () => Config.Register<Settings>());
        Assert.Equal("tk-base", Config.Find("tk-base")!.ModId);
    }

    public sealed class Settings
    {
        public int Value { get; set; }
    }

    private static void Thrower() => throw new FormatException("from the tick thread");
}
