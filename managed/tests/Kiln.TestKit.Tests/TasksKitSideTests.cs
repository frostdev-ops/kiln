// SPDX-License-Identifier: MIT
// Kiln tasks on the harness's client sides (#82, KilnHarnessOptions.Side): a joined client has no server world (a hop
// there is refused, the mod's fallback can be tested) and its client world is the step; a hosting client has both
// worlds, the client world on a main thread of its own. LeaveWorld / EnterWorld end and start the worlds' sessions as a
// stop and a re-host or re-join do: a task bound to the old ones never hops into the new ones.
using TkEvents;
using Xunit;

// The task bodies stand in for mod code (their own waits, no test token).
#pragma warning disable xUnit1051

namespace Kiln.TestKit.Tests;

public sealed class TasksKitSideTests
{
    private static (KilnHarness Kiln, ModHandle Mod) Start(HarnessSide side)
    {
        var kiln = new KilnHarness(new KilnHarnessOptions { Side = side });
        var mod = kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        return (kiln, mod);
    }

    [Fact]
    public void On_a_joined_client_a_server_hop_is_refused_and_the_client_world_takes_the_mods_tick()
    {
        var (kiln, mod) = Start(HarnessSide.JoinedClient);
        using var _ = kiln;
        var seen = new List<string>();
        var task = kiln.As(mod, () => mod.Context.Tasks.Run("fallback", async ctx =>
        {
            try
            {
                await ctx.ToTick(WorldKind.Server);
            }
            catch (KilnUnavailableException e)
            {
                lock (seen) seen.Add($"server refused: {e.Capability}");
            }
            await ctx.ToTick();  // started on the joined client's tick: the client world
            lock (seen) seen.Add($"home {ctx.TickWorld}, tick {Game.IsTickThread}");
        }));
        kiln.Tasks.Settle();
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal(["server refused: tasks.pool", "home Client, tick True"], seen);
    }

    [Fact]
    public void On_a_hosting_client_main_thread_tasks_come_back_to_the_main_thread()
    {
        var (kiln, mod) = Start(HarnessSide.HostingClient);
        using var _ = kiln;
        var seen = new List<string>();
        KilnTask? client = null;
        kiln.OnMainThread(() => client = mod.Context.Tasks.Run("client", async ctx =>
        {
            await ctx.ToTick();
            lock (seen) seen.Add($"client hop: tick {Game.IsTickThread}");  // the main thread is not the mods' tick here
            await ctx.ToTick(WorldKind.Server);
            lock (seen) seen.Add($"server hop: tick {Game.IsTickThread}");
        }));
        var server = kiln.As(mod, () => mod.Context.Tasks.Run("server", async ctx =>
        {
            await ctx.ToTick();
            lock (seen) seen.Add($"server task: {ctx.TickWorld}");
        }));
        kiln.Tasks.Settle();
        Assert.Equal(KilnTaskState.Completed, client!.State);
        Assert.Equal(KilnTaskState.Completed, server.State);
        Assert.Contains("client hop: tick False", seen);
        Assert.Contains("server hop: tick True", seen);
        Assert.Contains("server task: Server", seen);
    }

    [Fact]
    public void Leaving_the_world_ends_its_tasks_hops_and_a_new_world_takes_none_of_them()
    {
        var (kiln, mod) = Start(HarnessSide.HostingClient);
        using var _ = kiln;
        using var computing = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var ran = false;
        KilnTask? task = null;
        kiln.OnMainThread(() => task = mod.Context.Tasks.Run("leaves", async ctx =>
        {
            computing.Set();
            go.Wait();
            await ctx.ToTick();
            ran = true;
        }));
        Assert.True(computing.Wait(TimeSpan.FromSeconds(20)));
        kiln.LeaveWorld();
        kiln.EnterWorld();
        go.Set();
        kiln.Tasks.Settle();
        Assert.False(ran);
        Assert.Equal(KilnTaskState.Cancelled, task!.State);
        Assert.DoesNotContain(kiln.Logs, l => l.Level <= LogLevel.Warn && l.Text.Contains("leaves", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dedicated_server_has_no_main_thread()
    {
        var (kiln, _) = Start(HarnessSide.DedicatedServer);
        using var __ = kiln;
        Assert.Throws<InvalidOperationException>(() => kiln.OnMainThread(() => { }));
    }
}
