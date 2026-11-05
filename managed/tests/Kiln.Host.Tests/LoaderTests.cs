// SPDX-License-Identifier: MIT
using Kiln.Host;
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;
using TestContext = Kiln.Testing.TestContext;

namespace Kiln.Host.Tests;

/// <summary>Dependencies, exports, cascade reload, IMC, the Mods API and Kiln.Testing with the real sample mods.</summary>
public sealed class LoaderTests : ModsFixture
{
    [Fact]
    public void Loads_in_dependency_order_and_shares_exported_types()
    {
        InstallLibSet();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Equal(["sample-lib", "sample-consumer", "sample-tests"], mods.Mods.Select(m => m.Id));
        Assert.True(host.Has("IGreeter type from kiln-mod:sample-lib"), host.Dump());
        Assert.True(host.Has("\"hello sample-consumer, from sample-lib 1.2.0\""));
        Assert.True(host.Has("optional sample-absent loaded: False"));
        Assert.True(host.Has("mods: 3 loaded, 0 failed, 0 skipped; load order: sample-lib, sample-consumer, sample-tests"));
        var lib = Kiln.Mods.Get("sample-lib")!;
        Assert.Equal(("1.2.0", ModState.Loaded, 0), (lib.Version, lib.State, lib.LoadIndex));
        Assert.Equal(["SampleLib.Api.dll"], lib.Info!.Exports);
        Assert.Contains("sample-lib 1.2.0 [loaded]", File.ReadAllText(Path.Combine(LogsDir, "mods.txt")));
    }

    [Fact]
    public void Imc_is_delivered_on_the_next_step_and_dropped_with_its_target()
    {
        InstallLibSet();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.False(host.Has("IMC reply"));
        Run(mods, () => host.Has("sample-consumer: IMC reply from sample-lib"), max: 5, sleepMs: 0);
        Assert.True(host.Has("IMC reply from sample-lib: EchoRequest { Text = hello over IMC, Number = 42 } (same type: True)"), host.Dump());
        Assert.False(Kiln.Mods.Send("sample-nowhere", "x"));
        var target = mods.Find("sample-lib")!.ModContext;
        Assert.True(Kiln.Mods.Send("sample-lib", "nobody-handles-this", 1));
        Assert.Equal(1, target.Messages.Queued);
        Assert.True(mods.Unload("sample-lib", out _));
        Assert.Equal(0, target.Messages.Queued);
        Assert.False(Kiln.Mods.Send("sample-lib", "echo"));
    }

    [Fact]
    public void Reload_cascades_to_dependents_and_leaks_nothing()
    {
        InstallLibSet();
        var host = new FakeHost();
        using var mods = NewManager(host);
        ModReloadedEvent? reloaded = null;
        using var sub = Events.On<ModReloadedEvent>(e => reloaded = e);
        mods.LoadAll();
        ReloadTenTimes(mods);  // no mod objects may stay in this frame's (debug) locals
        Assert.True(host.Has("mod sample-tests unloaded (its dependency sample-lib unloaded)"));
        Assert.NotNull(reloaded);
        Assert.Equal(["sample-consumer", "sample-tests"], reloaded!.Cascade);
        Assert.True(reloaded.Ok);
        Assert.True(mods.Unload("sample-lib", out _));  // cascades: dependents first
        Assert.Empty(mods.Mods);
        var leaks = mods.CollectAndCountLeaks(out var leaked);
        Assert.True(leaks == 0, string.Join(", ", leaked));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ReloadTenTimes(ModManager mods)
    {
        var first = mods.Find("sample-lib")!.Context;
        for (var i = 0; i < 10; i++)
        {
            mods.Pre(Tick((ulong)i + 1), []);
            Assert.True(mods.Reload("sample-lib", out var error), error);
        }
        Assert.Equal(["sample-lib", "sample-consumer", "sample-tests"], mods.Mods.Select(m => m.Id));
        Assert.NotSame(first, mods.Find("sample-lib")!.Context);
    }

    [Fact]
    public void Missing_dependency_fails_then_loads_when_the_dependency_arrives()
    {
        Install("SampleConsumer", "sample-consumer");
        WriteManifest("sample-cycle-a", """{"id":"sample-cycle-a","version":"1.0.0","apiVersion":"0.2","entryAssembly":"N.dll","dependencies":{"sample-cycle-b":"*"}}""");
        WriteManifest("sample-cycle-b", """{"id":"sample-cycle-b","version":"1.0.0","apiVersion":"0.2","entryAssembly":"N.dll","dependencies":{"sample-cycle-a":"*"}}""");
        InstallHello();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Equal(["hello"], mods.Mods.Select(m => m.Id));
        var consumer = Kiln.Mods.Get("sample-consumer")!;
        Assert.Equal(ModState.Failed, consumer.State);
        Assert.Equal("missing dependency sample-lib ^1.0", consumer.Error);
        Assert.StartsWith("dependency cycle: sample-cycle-a -> sample-cycle-b -> sample-cycle-a", Kiln.Mods.Get("sample-cycle-b")!.Error);
        Assert.True(host.Has("mod sample-consumer 0.3.0 not loaded: missing dependency sample-lib ^1.0"));

        var lib = Install("SampleLib", "sample-lib");
        Assert.True(mods.TryLoad(lib, out var error, out var affected), error);
        Assert.Equal(["sample-lib", "sample-consumer"], affected);
        Assert.True(Kiln.Mods.IsLoaded("sample-consumer"));
    }

    [Fact]
    public void Reload_of_an_unknown_id_loads_its_folder()
    {
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.False(mods.Reload("sample-lib", out _));
        Install("SampleLib", "sample-lib");
        Assert.True(mods.Reload("sample-lib", out var error), error);
        Assert.True(Kiln.Mods.IsLoaded("sample-lib"));
        Assert.False(mods.Reload("../sample-lib", out _));
    }

    [Fact]
    public void A_mod_whose_OnLoad_throws_fails_its_dependents()
    {
        InstallLibSet();
        // break sample-lib: an entry type that does not exist
        var dir = Path.Combine(ModsDir, "sample-lib");
        var json = File.ReadAllText(Path.Combine(dir, Manifest.FileName)).Replace("\"entryAssembly\"", "\"entryType\": \"SampleLib.Nope\", \"entryAssembly\"");
        File.WriteAllText(Path.Combine(dir, Manifest.FileName), json);
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Assert.Empty(mods.Mods);
        Assert.Contains("TypeLoadException", Kiln.Mods.Get("sample-lib")!.Error);
        Assert.Equal("dependency sample-lib failed to load", Kiln.Mods.Get("sample-consumer")!.Error);
    }

    [Fact]
    public async Task Mods_api_requests_run_at_the_next_step()
    {
        InstallLibSet();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        var reload = Kiln.Mods.ReloadAsync("sample-consumer");
        Assert.False(reload.IsCompleted);
        mods.ProcessRequests();
        var r = await reload;
        Assert.True(r.Ok, r.Message);
        Assert.Equal(["sample-consumer", "sample-tests"], r.Affected);  // sample-tests optionally depends on it
        var unload = await RunRequest(mods, Kiln.Mods.UnloadAsync("sample-lib"));
        Assert.Equal(["sample-tests", "sample-consumer", "sample-lib"], unload.Affected);
        Assert.Equal(ModState.Unloaded, Kiln.Mods.Get("sample-consumer")!.State);
        var load = await RunRequest(mods, Kiln.Mods.LoadAsync("sample-lib"));
        Assert.True(load.Ok, load.Message);
        Assert.False((await RunRequest(mods, Kiln.Mods.LoadAsync("../../etc"))).Ok);

        ModOperationResult? viaCallback = null;
        var thread = 0;
        Kiln.Mods.Reload("sample-lib", r => (viaCallback, thread) = (r, Environment.CurrentManagedThreadId));
        Assert.Null(viaCallback);
        mods.ProcessRequests();
        Assert.True(viaCallback!.Ok, viaCallback.Message);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);  // on the tick thread, synchronously after the operation
    }

    private static async Task<ModOperationResult> RunRequest(ModManager mods, Task<ModOperationResult> request)
    {
        mods.ProcessRequests();
        return await request;
    }

    [Fact]
    public void Kiln_testing_runs_the_sample_test_mod()
    {
        InstallLibSet();
        var host = new FakeHost();
        using var mods = NewManager(host);
        mods.LoadAll();
        Lifecycle.Phase = ServerPhase.Running;
        Lifecycle.ModsLoaded = true;
        Lifecycle.StartedAtStep = 1;
        TestReport? report = null;
        string? error = null;
        Assert.True(mods.StartTests("sample-tests", null, (r, e) => (report, error) = (r, e), out var startError), startError);
        Assert.False(mods.StartTests("sample-tests", null, null, out var again));
        Assert.Contains("already running", again);
        Run(mods, () => report is not null || error is not null, from: 2);
        Assert.Null(error);
        Assert.NotNull(report);
        Assert.True(report!.Ok, host.Dump());
        Assert.Equal(1, report.Skipped);
        Assert.True(report.Passed >= 9, report.ToString());
        Assert.True(host.Has("PASS SampleTestsMod.Imc_round_trip"));
        Assert.True(host.Has("SKIP SampleTestsMod.Skipped_example: shows how a skipped test is reported"));
        var json = File.ReadAllText(Path.Combine(LogsDir, "tests", "sample-tests.json"));
        Assert.Contains("\"ok\": true", json);
        Assert.Contains("\"outcome\": \"skip\"", json);
    }

    [Fact]
    public async Task Test_runner_reports_failures_and_timeouts()
    {
        var host = new FakeHost();
        var probe = new ProbeMod();
        var context = HostAccess.CreateContext(new ModInfo("probe", "probe", "1.0.0", Root), 1, DataDir);
        HostAccess.Bind(probe, context);
        var sync = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context.Scheduler.SynchronizationContext);
        Task<TestReport> run;
        try
        {
            run = TestRunner.RunAsync(probe, [typeof(ProbeMod).Assembly], new TestRunOptions { Timeout = TimeSpan.FromMilliseconds(300), Filter = "Probe" });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(sync);
        }
        for (var i = 0; i < 400 && !run.IsCompleted; i++)
        {
            Pump(context);
            await Task.Delay(5, Xunit.TestContext.Current.CancellationToken);
        }
        var report = await run;
        Assert.Equal(["ProbeMod.Probe_passes", "ProbeMod.Probe_fails", "ProbeMod.Probe_hangs", "ProbeMod.Probe_throws", "ProbeMod.Probe_bad_signature"],
            report.Results.Select(r => r.Name));
        Assert.Equal([TestOutcome.Pass, TestOutcome.Fail, TestOutcome.Timeout, TestOutcome.Fail, TestOutcome.Fail], report.Results.Select(r => r.Outcome));
        Assert.Equal("expected <1>, got <2>", report.Results[1].Message);
        Assert.Equal("ArgumentException: nope", report.Results[3].Message);
        Assert.Contains("one TestContext", report.Results[4].Message);
        Assert.True(probe.Ran, "instance tests run on the mod instance itself");
        Assert.False(report.Ok);
    }

    private static void Pump(ModContext ctx)
    {
        HostAccess.Current = ctx;
        var sync = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx.Scheduler.SynchronizationContext);
        try
        {
            int n;
            lock (ctx.PendingLock) n = ctx.Pending.Count;
            for (; n > 0; n--)
            {
                Action a;
                lock (ctx.PendingLock) a = ctx.Pending.Dequeue();
                a();
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(sync);
            HostAccess.Current = null;
        }
    }

    public sealed class ProbeMod : Mod
    {
        public bool Ran;

        [KilnTest(Order = 1)]
        public void Probe_passes() => Ran = true;

        [KilnTest(Order = 2)]
        public void Probe_fails() => Kiln.Testing.Assert.Equal(1, 2);

        [KilnTest(Order = 3)]
        public async Task Probe_hangs(TestContext t)
        {
            while (true) await t.Ticks();
        }

        [KilnTest(Order = 4)]
        public Task Probe_throws() => throw new ArgumentException("nope");

        [KilnTest(Order = 5)]
        public void Probe_bad_signature(int x) { }
    }
}
