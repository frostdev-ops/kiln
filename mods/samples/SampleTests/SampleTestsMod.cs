// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.Loader;
using Kiln;
using Kiln.Testing;
using SampleLib.Api;
using Assert = Kiln.Testing.Assert;

namespace SampleTests;

/// <summary>Tests can live on the Mod itself (they get the running instance) or on any class with a parameterless
/// constructor.</summary>
public sealed class SampleTestsMod : Mod
{
    private readonly List<ImcMessage> _replies = [];

    public override void OnLoad() => Context.Messages.On("echo-reply", m => _replies.Add(m));

    [KilnTest(Order = -1)]
    public void Manifest_metadata_is_exposed()
    {
        Assert.Equal("sample-tests", Context.Info.Id);
        Assert.True(Context.Info.Dependencies.Any(d => d.Id == "sample-lib" && d.VersionRange == "^1.2" && !d.Optional));
        Assert.True(Context.Info.Dependencies.Any(d => d.Id == "sample-consumer" && d.Optional));
        Assert.Equal(ModSide.Server, Context.Info.Side);
    }

    [KilnTest]
    public async Task Await_comes_back_on_the_tick_thread(TestContext t)
    {
        Assert.True(Game.IsTickThread, "starts on the tick thread");
        await t.Ticks(3);
        Assert.True(Game.IsTickThread, "after 3 ticks");
        await Task.Delay(50);  // a timer thread completes it; the mod's context brings it back
        Assert.True(Game.IsTickThread, "after Task.Delay");
    }

    [KilnTest]
    public void Load_order_follows_dependencies()
    {
        var lib = Assert.NotNull(Mods.Get("sample-lib"));
        var me = Assert.NotNull(Mods.Current);
        Assert.Equal(ModState.Loaded, lib.State);
        Assert.True(lib.LoadIndex >= 0 && lib.LoadIndex < me.LoadIndex, $"sample-lib #{lib.LoadIndex} before sample-tests #{me.LoadIndex}");
        if (Mods.Get("sample-consumer") is { IsLoaded: true } consumer)
            Assert.True(consumer.LoadIndex < me.LoadIndex, "optional dependency present: loads first");
    }

    [KilnTest]
    public void Exported_types_are_shared_with_the_provider()
    {
        Assert.True(Greeters.All.Count > 0, "sample-lib registered a greeter");
        Assert.Equal("kiln-mod:sample-lib", AssemblyLoadContext.GetLoadContext(typeof(IGreeter).Assembly)?.Name);
        Assert.Contains("hello tests", Greeters.All[0].Greet("tests"));
    }

    [KilnTest]
    public async Task Imc_round_trip(TestContext t)
    {
        _replies.Clear();
        Assert.True(Mods.Send("sample-lib", "echo", new EchoRequest("ping", 7)), "sample-lib is loaded");
        Assert.False(Mods.Send("sample-nowhere", "echo"), "no such mod");
        await t.Until(() => _replies.Count > 0, TimeSpan.FromSeconds(5), "the echo reply");
        Assert.Equal("sample-lib", _replies[0].Sender);
        Assert.Equal(new EchoRequest("ping", 7), _replies[0].PayloadAs<EchoRequest>());
    }

    [KilnTest]
    public void Lifecycle_reports_a_running_server()
    {
        Assert.True(Lifecycle.ModsLoaded);
        Assert.Equal(ServerPhase.Running, Lifecycle.Phase);
        Assert.True(Lifecycle.StartedAtStep > 0);
    }

    [KilnTest]
    public void Resolver_rejections_are_visible()
    {
        foreach (var e in Mods.All.Where(e => !e.IsLoaded)) Context.Log.Info($"  not loaded: {e.Id} [{e.State}] {e.Error}");
        if (Mods.Get("sample-cycle-a") is { } cycle) Assert.Contains("dependency cycle", cycle.Error);
        if (Mods.Get("sample-client-only") is { } client) Assert.Equal(ModState.Skipped, client.State);
    }

    [KilnTest(Skip = "shows how a skipped test is reported")]
    public void Skipped_example() => Assert.Fail("never runs");
}

public sealed class TimingTests
{
    [KilnTest]
    public async Task Delay_waits_wall_time(TestContext t)
    {
        var sw = Stopwatch.StartNew();
        await t.Delay(TimeSpan.FromMilliseconds(200));
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 190, 5000);
    }

    [KilnTest]
    public void Mod_stats_are_counted()
    {
        var stats = Mods.Get("sample-lib")!.Stats;
        Assert.True(stats.Steps > 0, "sample-lib ran some steps");
        Assert.Equal(0, stats.Faults);
    }
}
