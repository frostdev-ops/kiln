// SPDX-License-Identifier: MIT
// Client-world hops through the host bridge (#82): a task started on the game's main thread belongs to the client world;
// its hops go through the lease table's session-checked tick_hop_in, are dispatched as KILN_LEASE_CLIENT_HOP on the main
// thread through the host's client frame, and the client world's STOPPING flushes them cancelled without touching the
// server world's. The frame itself: the mods' tick (drive lock) on a joined client, plain main-thread work while hosting.
using Kiln.Host;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Host.Tests;

public sealed class TaskClientWorldTests : HostTaskTest
{
    private KilnTask OnMain(ModContext mod, string name, Func<KilnTaskContext, Task> work)
    {
        KilnTask? task = null;
        Native.OnMain(() => task = mod.Tasks.Run(name, work));
        return task!;
    }

    [Fact]
    public void A_task_started_on_the_main_thread_hops_to_the_client_world_through_the_host()
    {
        Native.StartClientSession();
        var mod = NewMod("alpha");
        WorldKind home = default;
        var onMain = false;
        var task = OnMain(mod, "client", async ctx =>
        {
            home = ctx.TickWorld;
            await ctx.ToTick();
            onMain = Native.IsMainThread;
        });
        Assert.True(Until(() => Native.PendingClientHops == 1));
        Assert.Equal(0, Native.PendingHops);
        Native.Step();  // the server world's step runs none of it
        Assert.Equal(1, Native.PendingClientHops);
        Native.ClientStep();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(WorldKind.Client, home);
        Assert.True(onMain);
        lock (ClientHops) Assert.Equal([(mod.Owner, true)], ClientHops);
        lock (Hops) Assert.Empty(Hops);
        Assert.True(Native.TickHopInCalls >= 1, "the host uses the session-checked member");
        Quiesce();
    }

    [Fact]
    public void A_server_task_reaches_the_client_world_with_an_overload_and_back()
    {
        Native.StartClientSession();
        var mod = NewMod("alpha");
        var seen = new List<string>();
        KilnTask? task = null;
        Native.OnTick(() => task = mod.Tasks.Run("cross", async ctx =>
        {
            await ctx.ToTick(WorldKind.Client);
            seen.Add($"client {Native.IsMainThread}");
            await ctx.NextGateWindow(WorldKind.Client);
            await ctx.ToTick();
            seen.Add($"server {Native.IsTickThread}");
        }));
        for (var i = 0; i < 20 && !task!.IsTerminal; i++)
        {
            Native.WaitIdle();
            Native.ClientStep();
            Native.Step();
        }
        Ended(task!);
        Assert.Equal(KilnTaskState.Completed, task!.State);
        Assert.Equal(["client True", "server True"], seen);
        Assert.True(Native.AfterStepWorldCalls >= 1, "the client window used resume_after_step_world");
        Quiesce();
    }

    [Fact]
    public void The_client_worlds_stop_flushes_its_hops_cancelled_and_leaves_the_servers()
    {
        Native.StartClientSession();
        var mod = NewMod("alpha");
        var clientRan = false;
        var client = OnMain(mod, "client", async ctx =>
        {
            await ctx.ToTick();
            clientRan = true;
        });
        var serverRan = false;
        KilnTask? server = null;
        Native.OnTick(() => server = mod.Tasks.Run("server", async ctx =>
        {
            await ctx.ToTick();
            serverRan = true;
        }));
        Assert.True(Until(() => Native.PendingClientHops == 1 && Native.PendingHops == 1));
        Native.OnMain(Native.StopClientSession);  // a hosting client leaves: the client world first
        Ended(client);
        Assert.Equal(KilnTaskState.Cancelled, client.State);
        Assert.False(clientRan);
        Assert.Equal(1, Native.PendingHops);
        Native.Step();
        Ended(server!);
        Assert.True(serverRan);
        Assert.Equal(KilnTaskState.Completed, server!.State);
        Assert.False(Host.Has("faulted"));
        Quiesce();
    }

    [Fact]
    public void A_client_hop_after_the_client_world_was_replaced_cancels_the_task()
    {
        Native.StartClientSession();
        var mod = NewMod("alpha");
        using var go = new ManualResetEventSlim();
        var ran = false;
        var task = OnMain(mod, "rejoin", async ctx =>
        {
            go.Wait(Long);
            await ctx.ToTick();
            ran = true;
        });
        Native.OnMain(Native.StopClientSession);
        Native.OnMain(Native.StartClientSession);  // joined another world meanwhile
        go.Set();
        Ended(task);
        Native.ClientStep();
        Assert.False(ran);
        Assert.Equal(KilnTaskState.Cancelled, task.State);
        Assert.Equal(0, Native.PendingClientHops);
        Assert.False(Host.Has("faulted"));
        Quiesce();
    }

    [Fact]
    public void Wait_is_refused_on_the_main_thread()
    {
        Native.StartClientSession();
        var mod = NewMod("alpha");
        using var go = new ManualResetEventSlim();
        var task = mod.Tasks.Run("waited", _ => go.Wait(Long));
        InvalidOperationException? refused = null;
        Native.OnMain(() => refused = Assert.Throws<InvalidOperationException>(() => task.Wait()));
        go.Set();
        Ended(task);
        Assert.Contains("main thread", refused!.Message);
        Quiesce();
    }

    // ---- the frame (ClientHopFrame, used by HostRuntime.ClientTaskHop) ----

    [Fact]
    public void On_a_joined_client_a_client_hop_is_the_mods_tick_under_the_drive_lock()
    {
        var drive = new object();
        var driver = 0;
        bool locked = false, asMods = false, asClient = false;
        var me = Environment.CurrentManagedThreadId;
        var seenDriver = 0;
        ClientHopFrame.Run(serverWorldLive: false, drive, ref driver, () =>
        {
            asMods = true;
            locked = Monitor.IsEntered(drive);
            seenDriver = Volatile.Read(ref driver);
        }, () => asClient = true);
        Assert.True(asMods && !asClient);
        Assert.True(locked);
        Assert.Equal(me, seenDriver);  // Game.IsTickThread is true there
        Assert.Equal(0, driver);       // and restored after
    }

    [Fact]
    public void While_hosting_a_client_hop_never_waits_for_the_server_threads_drive_lock()
    {
        var drive = new object();
        var driver = 0;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var server = new Thread(() =>
        {
            lock (drive)
            {
                held.Set();
                release.Wait(Long);
            }
        }) { IsBackground = true };
        server.Start();
        Assert.True(held.Wait(Long));
        var ran = false;
        var asMods = false;
        var runner = Task.Run(() => ClientHopFrame.Run(serverWorldLive: true, drive, ref driver, () => asMods = true, () => ran = true));
        var done = runner.Wait(TimeSpan.FromSeconds(5));
        release.Set();
        server.Join(Long);
        Assert.True(done, "the client hop waited for the server thread's lock");
        Assert.True(ran && !asMods);
        Assert.Equal(0, driver);  // Game.IsTickThread stays false on the main thread while hosting
    }
}
