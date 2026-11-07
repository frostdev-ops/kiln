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

    [Fact]
    public void LateCreativeModeRefusesAnExistingCreativeSessionAndRetiresItsRules()
    {
        var set = new RuleSet(RuleScope.ForPlayer(P), PlayerRules.Creative, "kiln", "creative", null);
        var sets = (Dictionary<EntityId, RuleSet>)typeof(Creative).GetField("Sets",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        sets[P] = set;
        try
        {
            CreativeFlame.Probe = () => false;
            Assert.True(Creative.IsOn(P));
            CreativeFlame.Probe = () => true;
            Assert.False(Creative.Set(P, true).Ok); // "already on" cannot bypass the Flame refusal
            Assert.False(Creative.IsOn(P));
            typeof(CreativeNet).GetMethod("Drain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null);
            Assert.False(set.Active);
            Assert.Empty(Creative.Players);
        }
        finally { sets.Remove(P); set.Dispose(); }
    }
}

public sealed unsafe class CreativeCycleHandoffTests
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TimeTable
    {
        public uint Size, Version;
        public nint Get, Set, Weather;
        public delegate* unmanaged[Cdecl]<float, int> Scale;
        public delegate* unmanaged[Cdecl]<float> Hold;
    }
    private static float _speed;
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static float Hold() => _speed;
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int Scale(float speed) { _speed = speed; return 0; }

    [Fact]
    public void LateFlameStartReleasesOnlyTheClockSpeedCreativeWasHolding()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var field = typeof(Time).GetField("_table", flags)!;
        var old = field.GetValue(null);
        var held = typeof(CreativeTools).GetField("_cycleSpeed", flags)!;
        TimeTable table = new() { Size = (uint)sizeof(TimeTable), Version = 1, Scale = &Scale, Hold = &Hold };
        try
        {
            field.SetValue(null, System.Reflection.Pointer.Box(&table, field.FieldType));
            _speed = 0;
            held.SetValue(null, (double?)0);
            CreativeTools.StepAside();
            Assert.Equal(-1, _speed); // stopped sun returns to the game's clock
            _speed = 2;
            held.SetValue(null, (double?)0);
            CreativeTools.StepAside();
            Assert.Equal(2, _speed); // a later writer's speed is preserved
        }
        finally { field.SetValue(null, old); held.SetValue(null, null); }
    }
}
