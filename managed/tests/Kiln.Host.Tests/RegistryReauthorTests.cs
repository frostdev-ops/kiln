// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

// Registry.Reauthor off the world tick (dev.32 LOOT-2): with kiln-content's kiln.internal.registry v3 the re-authored pass runs on
// a worker thread of Kiln.Host's, its turn is prepared there and lands on the tick in one step (FakeRegistry's v3 stands in for
// kiln-content; registry_live_reauthor_test covers the native side).
public sealed class RegistryReauthorTests : ModsFixture
{
    [RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
    private sealed class WorldOnlyAuthor(Action<RegistryContext> callback) : Mod
    {
        public override void OnRegistry(RegistryContext context) => callback(context);
    }
    private ModContext Owner(FakeRegistry native, string id)
        => HostAccess.CreateContext(new ModInfo(id, id, "1.0.0", Root), native.Host.OwnerCreate(id), DataDir);
    private static void Edit(RegistryContext context, int value)
    {
        context.Capture = () => new JsonObject { ["ir"] = 1, ["mod"] = context.ModId,
            ["stage"] = context.Phase.ToString().ToLowerInvariant(), ["edits"] = new JsonArray(new JsonObject {
                ["registry"] = "items", ["select"] = "game:wood", ["field"] = "maxStack", ["op"] = value }) }.ToJsonString();
    }
    private static int Read(RegistryContext context) =>
        int.Parse(context.ReadValue("items", "game:wood", "maxStack"), System.Globalization.CultureInfo.InvariantCulture);
    // the tick runs until the job's worker is done, then lands it
    private static void Settle(RegistryDispatch dispatch)
    {
        for (var i = 0; i < 20; ++i)
        {
            dispatch.FlushLoads();
            if (dispatch.ReauthorWork is not { } work) return;
            Assert.True(work.Wait(10_000), "the re-author worker finishes");
        }
        Assert.Fail("the re-author job did not land");
    }
    private sealed class Rig : IDisposable
    {
        internal readonly FakeRegistry Native = new() { OffTick = true };
        internal readonly ModManager Manager;
        internal readonly RegistryDispatch Dispatch;
        internal readonly int Tick = Environment.CurrentManagedThreadId;
        internal Rig(RegistryReauthorTests fixture)
        {
            Native.Host.TickThread = () => Environment.CurrentManagedThreadId == Tick;
            Manager = fixture.NewManager(Native.Host);
            Dispatch = new RegistryDispatch(Manager); Dispatch.Prepare(100);
        }
        public void Dispose() { Dispatch.Dispose(); Manager.Dispose(); Native.Dispose(); }
    }

    // The pass runs off the tick, reads below the mod's own layer, and its turn lands on the tick: the new layer in and the
    // earlier one out in one install, no revert after.
    [Fact(Timeout = 30_000)]
    public async Task AReauthoredPassRunsOffTheTickAndLandsInOneTurn()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var factor = 2; var threads = new List<int>();
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) { lock (threads) threads.Add(Environment.CurrentManagedThreadId); Edit(c, Read(c) * factor); } }),
            owner, Root, false);
        rig.Dispatch.FlushLoads();
        Assert.Equal(20, rig.Native.Value); Assert.Equal(new[] { rig.Tick }, threads);  // a world load's pass stays on the tick
        factor = 3;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        var reverts = rig.Native.Reverts;
        Settle(rig.Dispatch);
        Assert.Equal(30, rig.Native.Value);                 // 10 x 3: read below its own layer
        Assert.Equal(1, rig.Native.LayerCount);             // the earlier layer went in the same turn
        Assert.Equal(reverts, rig.Native.Reverts);          // no revert after the commit
        Assert.Equal(1, rig.Native.Installs);
        Assert.Equal(new[] { rig.Tick }, rig.Native.InstallThreads);
        Assert.NotEqual(rig.Tick, threads[1]);              // the pass ran on the worker
        Assert.DoesNotContain(rig.Tick, rig.Native.PrepareThreads);
        Assert.True(rig.Native.PassReadsIn > 0, "the worker's pass reads through read_pass_in");
        Assert.True(rig.Native.Host.Has("landed in"), rig.Native.Host.Dump());
        for (var i = 0; i < 100 && Volatile.Read(ref rig.Native.ReportsBuilt) == 0; ++i) await Task.Delay(20);
        Assert.True(Volatile.Read(ref rig.Native.ReportsBuilt) > 0, "registry.json is built off the tick");
        owner.Retire(); Assert.Equal(10, rig.Native.Value);
    }
    // Requests while a pass runs make one more pass after it, never one per request (coalescing).
    [Fact(Timeout = 30_000)]
    public async Task RequestsDuringAPassAreOneMorePass()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var passes = 0; var value = 40;
        using var hold = new ManualResetEventSlim(true);
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) { Interlocked.Increment(ref passes); hold.Wait(); Edit(c, value); } }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(1, passes);
        hold.Reset();
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        rig.Dispatch.FlushLoads();  // the job starts; its pass waits
        for (var i = 0; i < 5; ++i)
        {
            value = 50 + i;
            Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
            rig.Dispatch.FlushLoads();  // the tick goes on meanwhile
        }
        hold.Set();
        Settle(rig.Dispatch);
        Assert.Equal(3, passes);  // the world load's, the first request's, one for the five made meanwhile
        Assert.Equal(54, rig.Native.Value); Assert.Equal(1, rig.Native.LayerCount);
        owner.Retire();
    }
    // A pass that throws on the worker keeps the earlier layers; asked again, it runs again.
    [Fact(Timeout = 30_000)]
    public async Task AFaultingPassOffTheTickKeepsTheEarlierLayers()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var fail = false;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            if (fail) throw new InvalidOperationException("bad settings");
            Edit(c, 40);
        }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(40, rig.Native.Value);
        fail = true;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(40, rig.Native.Value); Assert.Equal(1, rig.Native.LayerCount); Assert.Equal(0, rig.Native.Installs);
        Assert.True(rig.Native.Host.Has("bad settings"), rig.Native.Host.Dump());
        fail = false;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(40, rig.Native.Value); Assert.Equal(1, rig.Native.LayerCount); Assert.Equal(1, rig.Native.Installs);
        owner.Retire();
    }
    // A pass that calls a tick-thread API off the tick (even one that catches the refusal) never lands that result: the mod's
    // passes run on the tick from then on, and Kiln says so once.
    [Fact(Timeout = 30_000)]
    public async Task APassThatNeedsTheTickRunsOnTheTick()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var threads = new List<int>(); var level = 0;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            try { Game.AssertTickThread(); level = 7; } catch (InvalidOperationException) { level = 0; }  // a mod that swallows it
            Edit(c, 10 + level);
        }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(17, rig.Native.Value);
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(17, rig.Native.Value);  // never 10: the off-tick result built around the refusal was not landed
        Assert.Equal(rig.Tick, threads[^1]); // the pass ran again, on the tick
        Assert.Equal(1, rig.Native.Installs);
        Assert.True(rig.Native.Host.Has("needs the tick thread: its passes run on the world tick"), rig.Native.Host.Dump());
        owner.Retire();
    }
    // review of LOOT-2: most tick-thread members of Kiln's services refuse through Services.RequireTickThread (Progression,
    // Items, World...), not Game.AssertTickThread. A pass that catches that refusal off the tick is not landed either: its
    // mod's passes run on the tick from then on.
    [Fact(Timeout = 30_000)]
    public async Task APassThatCatchesAServiceTickRefusalRunsOnTheTick()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var threads = new List<int>(); var level = 0;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            try { Services.RequireTickThread("Progression.UnlockedNodes"); level = 7; } catch (InvalidOperationException) { level = 0; }
            Edit(c, 10 + level);
        }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(17, rig.Native.Value);
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(17, rig.Native.Value);  // never 10: the result built around the caught refusal was not landed
        Assert.Equal(rig.Tick, threads[^1]);
        Assert.True(rig.Native.Host.Has("calls Progression.UnlockedNodes, which needs the tick thread"), rig.Native.Host.Dump());
        owner.Retire();
    }
    // review of LOOT-2: a pass that does not catch the off-tick refusal faults because of where Kiln ran it, not because of the
    // author's code: it is not logged as the mod's fault and its request is not lost; it runs again on the tick and lands.
    [Fact(Timeout = 30_000)]
    public async Task APassThatThrowsOnTheOffTickRefusalRunsAgainOnTheTick()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var threads = new List<int>(); var passes = 0;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            Game.AssertTickThread();  // a mod written when every WorldLoad pass ran on the tick
            Edit(c, 20 + ++passes);
        }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(21, rig.Native.Value);
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(22, rig.Native.Value);  // the request ran again, on the tick, and landed
        Assert.Equal(rig.Tick, threads[^1]); Assert.Equal(1, rig.Native.Installs); Assert.Equal(1, rig.Native.LayerCount);
        Assert.False(rig.Native.Host.Has("OnRegistry WorldLoad (Registry.Reauthor)"), rig.Native.Host.Dump());
        Assert.True(rig.Native.Host.Has("needs the tick thread: its passes run on the world tick"), rig.Native.Host.Dump());
        owner.Retire();
    }
    // review of LOOT-2: a turn whose bindings keep moving before it lands (stale 2: a busy server's drop-table loads) counts
    // toward the three stale turns in a row like one whose reads moved, so a later one runs whole on the tick and lands; it
    // never loops without end.
    [Fact(Timeout = 30_000)]
    public async Task TurnsWhoseBindingsKeepMovingEndOnTheTick()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var passes = 0;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 80 + Interlocked.Increment(ref passes)); }), owner, Root, false);
        rig.Dispatch.FlushLoads();
        rig.Native.InstallStaleUnlessAnyway = true;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(1, rig.Native.LayerCount); Assert.Equal(80 + passes, rig.Native.Value);
        Assert.True(passes <= 5, $"{passes} passes");
        Assert.Equal(rig.Tick, rig.Native.InstallThreads[^1]);
        owner.Retire();
    }
    // review of LOOT-2: preparations that keep coming back stale in their bindings (three tries each) are stale turns too, not
    // "the registry cannot take the pass yet" with a back-off that ends in an error and drops the request.
    [Fact(Timeout = 30_000)]
    public async Task PreparationsThatKeepComingBackStaleEndOnTheTick()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var passes = 0;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 90 + Interlocked.Increment(ref passes)); }), owner, Root, false);
        rig.Dispatch.FlushLoads();
        rig.Native.PrepareStaleOffTick = true;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(1, rig.Native.LayerCount); Assert.Equal(90 + passes, rig.Native.Value); Assert.Equal(1, rig.Native.Installs);
        Assert.Equal(rig.Tick, rig.Native.InstallThreads[^1]);
        Assert.False(rig.Native.Host.Has("cannot take the pass yet"), rig.Native.Host.Dump());
        owner.Retire();
    }
    // review of LOOT-2: a mod's unload while its re-authored pass runs on the worker waits for the pass (ModContext.UnloadStarting,
    // as every service that calls into a mod from another thread does): its OnUnload and disposal never run beside the pass, no
    // phase of it starts once its unload began, and a pass cut short so lands nothing (its earlier layers stay for the retirement).
    [Fact(Timeout = 30_000)]
    public async Task AnUnloadWaitsForThePassOnTheWorker()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var passes = 0;
        using var hold = new ManualResetEventSlim(true);
        using var entered = new ManualResetEventSlim(false);
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            Interlocked.Increment(ref passes); entered.Set(); hold.Wait(); Edit(c, 40 + passes);
        }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(41, rig.Native.Value);
        hold.Reset(); entered.Reset();
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        rig.Dispatch.FlushLoads();  // the job starts; its pass waits inside the mod's code
        Assert.True(entered.Wait(10_000), "the pass runs on the worker");
        var waits = owner.BeginUnload();
        Assert.NotNull(waits); Assert.Contains("Registry.Reauthor", waits);
        hold.Set();
        Assert.True(rig.Dispatch.ReauthorWork!.Wait(10_000));
        Assert.Null(owner.UnloadWaitsFor());
        Settle(rig.Dispatch);
        Assert.Equal(1, rig.Native.LayerCount); Assert.Equal(41, rig.Native.Value);  // its Final phase did not run: nothing of it lands
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));  // asked again while the unload waits
        Settle(rig.Dispatch);
        Assert.Equal(2, passes);                     // no pass of an unloading mod starts
        Assert.Equal(1, rig.Native.LayerCount); Assert.Equal(41, rig.Native.Value);  // and its layers stay for the retirement
        owner.Retire(); Assert.Equal(10, rig.Native.Value);
    }
    // review of LOOT-2: a landed turn's registry.json is built off the tick; one that found the registry moved meanwhile (a drop
    // table bound, another write) is tried again a few times there before the world tick builds it itself (a hitch of up to a
    // hundred milliseconds on the tick for the full report).
    [Fact(Timeout = 30_000)]
    public async Task AReportThatFindsTheRegistryMovedIsBuiltAgainOffTheTick()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var value = 40;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, value); }), owner, Root, false);
        rig.Dispatch.FlushLoads();
        rig.Native.ReportMoves = 2;
        value = 45;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(45, rig.Native.Value);
        for (var i = 0; i < 200 && Volatile.Read(ref rig.Native.ReportsBuilt) == 0; ++i) await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref rig.Native.ReportsBuilt));
        Assert.Equal(3, Volatile.Read(ref rig.Native.ReportBuilds));
        owner.Retire();
    }
    // The registry moved after the pass read it (another mod's commit): that turn is not landed and the pass runs again.
    [Fact(Timeout = 30_000)]
    public async Task AStaleTurnRunsThePassAgain()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var passes = 0;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) { ++passes; Edit(c, 60 + passes); } }), owner, Root, false);
        rig.Dispatch.FlushLoads();
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        rig.Native.NextStale = 1;
        Settle(rig.Dispatch);
        Assert.Equal(3, passes); Assert.Equal(2, rig.Native.Installs);  // the first turn was stale; the pass ran again and landed
        Assert.Equal(63, rig.Native.Value); Assert.Equal(1, rig.Native.LayerCount);
        owner.Retire();
    }
    // dev.32 api-shape: a pass says where it runs. A point's own pass (the world load's here): Point, on the tick. A pass
    // Registry.Reauthor asked for, on Kiln's worker: ReauthoredOffTick (OffTick: tick-thread APIs are refused); one Kiln runs
    // on the tick (its mod called one off it): ReauthoredOnTick.
    [Fact(Timeout = 30_000)]
    public async Task APassSaysWhereItRuns()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var seen = new List<(RegistryPassKind Pass, bool OffTick, bool Tick)>(); var needTick = false;
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            lock (seen) seen.Add((c.Pass, c.OffTick, Environment.CurrentManagedThreadId == rig.Tick));
            if (needTick && c.OffTick) Game.AssertTickThread();  // refused off the tick: Kiln runs its passes on the tick from then on
            Edit(c, 30 + seen.Count);
        }), owner, Root, false);
        rig.Dispatch.FlushLoads();
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        needTick = true;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Settle(rig.Dispatch);
        Assert.Equal(new[] { (RegistryPassKind.Point, false, true), (RegistryPassKind.ReauthoredOffTick, true, false),
                             (RegistryPassKind.ReauthoredOffTick, true, false), (RegistryPassKind.ReauthoredOnTick, false, true) }, seen);
        owner.Retire();
    }
    // dev.32 api-shape (LOOT-2 review's open finding): one re-author at a time holds across a world change too. A re-authored
    // pass still on the worker when a new world loads keeps that mod's new world pass back until it ends, however long it
    // takes: the two never run beside each other (a mod's pass state need not be thread-safe), and the old turn lands nothing.
    [Fact(Timeout = 60_000)]
    public async Task ANewWorldsPassWaitsForAReauthoredPassStillOnTheWorker()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler"); var inside = 0; var most = 0; var passes = 0;
        using var hold = new ManualResetEventSlim(true);
        using var entered = new ManualResetEventSlim(false);
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            var now = Interlocked.Increment(ref inside);
            lock (hold) most = Math.Max(most, now);
            try
            {
                if (Interlocked.Increment(ref passes) == 2) { entered.Set(); hold.Wait(); }
                Edit(c, 70 + Volatile.Read(ref passes));
            }
            finally { Interlocked.Decrement(ref inside); }
        }), owner, Root, false);
        rig.Dispatch.FlushLoads(); Assert.Equal(1, passes);
        hold.Reset();
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        rig.Dispatch.FlushLoads();  // the job starts; its pass waits inside the mod's code
        Assert.True(entered.Wait(10_000), "the re-authored pass runs on the worker");
        rig.Native.ChangeWorld(2);
        rig.Dispatch.ReauthorWait = TimeSpan.FromMilliseconds(50);
        int held;
        try
        {
            rig.Dispatch.FlushLoads();  // the new world: the tick waits a moment, then goes on without this mod's pass
            rig.Dispatch.FlushLoads();
            held = Volatile.Read(ref passes);
        }
        finally { hold.Set(); }
        Settle(rig.Dispatch);
        Assert.Equal(2, held);  // held: the new world's pass did not run beside the worker's
        Assert.Equal(3, passes); Assert.Equal(1, most);
        Assert.Equal(0, rig.Native.Installs);  // the old world's turn landed nothing
        Assert.Equal(73, rig.Native.Value);    // the new world's own pass
        owner.Retire();
    }
    // The world ended while the pass ran: its turn is for that world and is not landed.
    [Fact(Timeout = 30_000)]
    public async Task ATurnForAnEndedWorldIsDropped()
    {
        await Task.Yield();
        using var rig = new Rig(this);
        var owner = Owner(rig.Native, "leveler");
        using var hold = new ManualResetEventSlim(true);
        rig.Dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) { hold.Wait(); Edit(c, 70); } }), owner, Root, false);
        rig.Dispatch.FlushLoads();
        hold.Reset();
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        rig.Dispatch.FlushLoads();
        rig.Native.ChangeWorld(2);
        hold.Set();
        Settle(rig.Dispatch);
        Assert.Equal(0, rig.Native.Installs);
        Assert.Equal(70, rig.Native.Value);  // the new world's own pass (its world load), not the old world's turn
        owner.Retire();
    }
}
