// SPDX-License-Identifier: MIT
using TkEvents;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public void AwaitNextTickAdvancesOneStepPerStep()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        kiln.Run(() => Mods.Send("tk-events", "await"));
        kiln.Step();  // the message arrives; the loop awaits its first NextTick
        var sent = kiln.CurrentStep;
        Assert.Empty(kiln.Lines("tk-events", "await"));
        kiln.Step(3);
        var lines = kiln.LogsOf("tk-events").Where(l => l.Text.StartsWith("await ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["await 0 on tick thread True", "await 1 on tick thread True", "await 2 on tick thread True"], lines.Select(l => l.Text));
        Assert.Equal([sent + 1, sent + 2, sent + 3], lines.Select(l => l.Step));
    }

    [Fact]
    public void WorkerThreadsPostToTheTickThreadAndCannotTouchTickApi()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        Assert.Contains("loaded; tick thread True", kiln.Lines("tk-events", "loaded"));
        kiln.Run(() => Mods.Send("tk-events", "worker"));
        kiln.Step();
        var at = kiln.CurrentStep;
        Assert.Contains(kiln.Lines("tk-events", "worker: Events.Post"), l => l.StartsWith("worker: Events.Post refused: This must run on the tick thread", StringComparison.Ordinal));
        Assert.Contains("worker: tick thread False", kiln.Lines("tk-events", "worker: tick"));
        Assert.Empty(kiln.Lines("tk-events", "posted work"));
        kiln.Step();
        var ran = Assert.Single(kiln.LogsOf("tk-events"), l => l.Text.StartsWith("posted work", StringComparison.Ordinal));
        Assert.Equal("posted work ran; tick thread True", ran.Text);
        Assert.Equal(at + 1, ran.Step);
    }

    [Fact]
    public async Task GameIsTickThreadOnlyOnTheHarnessThread()
    {
        using var kiln = new KilnHarness();
        Assert.False(Game.IsTickThread);
        Assert.True(kiln.Run(() => Game.IsTickThread));
        Assert.False(await Task.Run(() => Game.IsTickThread, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => Events.Post(new ServerStartedProbe()));
        Assert.Equal(KitBackend.KilnVersion, Game.KilnVersion);
    }

    [Fact]
    public void TicksAdvanceLikeADedicatedServer()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        var deltas = new List<uint>();
        for (var i = 0; i < 20; i++)
        {
            kiln.Run(() => Mods.Send("tk-events", "tick"));
            kiln.Step(2);  // delivered at the first, logged in its Pre
        }
        var ticks = kiln.Lines("tk-events", "tick step");
        Assert.Equal(20, ticks.Count);
        foreach (var t in ticks)
        {
            var parts = t.Split(' ');
            deltas.Add(uint.Parse(parts[6]));
            Assert.Equal("40.8", parts[8]);
        }
        Assert.All(deltas, d => Assert.InRange(d, 2u, 3u));
        Assert.Contains(2u, deltas);
        Assert.Contains(3u, deltas);
    }

    private sealed class ServerStartedProbe : KilnEvent;
}
