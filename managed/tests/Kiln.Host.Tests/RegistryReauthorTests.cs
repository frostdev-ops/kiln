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
