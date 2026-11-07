// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Rules.Tests;

[CollectionDefinition(nameof(CreativeFlameTests), DisableParallelization = true)]
public sealed class CreativeFlameCollection;

/// <summary>Kiln's creative mode goes idle while Creative Mode (a Flame mod) runs in the same game.</summary>
[Collection(nameof(CreativeFlameTests))]
public sealed class CreativeFlameTests : IDisposable
{
    private static readonly EntityId P = new(5);

    public CreativeFlameTests() => CreativeFlame.Reset();

    public void Dispose()
    {
        CreativeFlame.Probe = null;
        CreativeFlame.Reset();
    }

    [Fact]
    public void WithoutKilnFlameNothingIsIdle()
    {
        // the unit-test host has no services: no kiln.flame, so Kiln's creative mode works as before
        Assert.False(CreativeFlame.Running);
        Assert.DoesNotContain("Creative Mode", Creative.Status());
    }

    [Fact]
    public void CreativeModeRunningRefusesEveryCreativeEntryPoint()
    {
        CreativeFlame.Probe = () => true;
        var on = Creative.Set(P, true);
        Assert.False(on.Ok);
        Assert.False(on.On);
        Assert.Contains("Creative Mode", on.Message);
        Assert.False(Creative.IsOn(P));
        Assert.False(Creative.Set(P, false).On);  // turning off still answers

        Assert.False(CreativeTools.SetTime(12, "the console").Ok);
        Assert.False(CreativeTools.SetCycle(TimeCycle.Stopped, "the console").Ok);
        var give = CreativeTools.Give(P, new ItemId(1)).GetAwaiter().GetResult();
        Assert.False(give.Ok);
        Assert.Contains("Creative Mode", give.Message);
        Assert.False(CreativeTools.CanGive(P));
        Assert.Contains("Creative Mode", CreativeMap.Set(1) ?? "");
        Assert.StartsWith(CreativeFlame.Note, Creative.Status());
    }

    [Fact]
    public void TheStateTellsTheMenuItIsIdleAndGrantsNoTools()
    {
        CreativeFlame.Probe = () => true;
        var st = CreativeServer.StateOf(P);
        Assert.True((st.Access & CreativeAccess.Idle) != 0);
        Assert.Equal(CreativeAccess.None, st.Access & (CreativeAccess.Give | CreativeAccess.Time | CreativeAccess.Cycle | CreativeAccess.On));
        Assert.Contains("Creative Mode", st.Hooks);
    }

    [Fact]
    public void OnceSeenRunningItStaysSeen()
    {
        var asks = 0;
        CreativeFlame.Probe = () => ++asks == 1;
        Assert.True(CreativeFlame.Running);
        Assert.True(CreativeFlame.Running);  // a mod that entered keeps its hooks: no second ask
        Assert.Equal(1, asks);
    }
}
