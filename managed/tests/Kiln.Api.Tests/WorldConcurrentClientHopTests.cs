// SPDX-License-Identifier: MIT
// The task-thread world reads and writes (Kiln.World) from a client-world hop (#82, design 2.3): a client hop runs inside
// the task's scope, so WorldConcurrent's caller check accepts it as it accepts a segment or a server hop, and a
// server-world compare-exchange from the game's main thread reaches kiln.world and meets the engine's write gate like any
// task write (on the server tick itself it would be Busy without reaching kiln.world). Code on the main thread outside a
// task is still refused. Over the fake kiln.world table (FakeEcsReads) and the task API's fake pool with its main thread.
using Xunit;

#pragma warning disable xUnit1031, xUnit1051
#pragma warning disable KILNX001

namespace Kiln.Api.Tests;

public sealed class WorldConcurrentClientHopTests : TaskTest
{
    private static byte[] Le(long v) => BitConverter.GetBytes(v);

    public WorldConcurrentClientHopTests()
    {
        FakeEcsReads.Reset();
        Backend.Tables["kiln.world"] = FakeEcsReads.Table;
        WorldNative.Reset();
        WorldConcurrent.Reset();
        Pool.StartSession(WorldKind.Client);  // a hosting client: the server world and a client world
    }

    public override void Dispose()
    {
        try
        {
            Backend.Tables.Remove("kiln.world");
            WorldNative.Reset();
            WorldConcurrent.Reset();
            Capabilities.Reset();
        }
        finally
        {
            base.Dispose();
        }
    }

    private static long Counter(uint id) => BitConverter.ToInt64(FakeEcsReads.Entities[(0, id)].Components[World.ComponentHash("Counter")]);

    [Fact]
    public void Reads_and_writes_from_a_client_hop_run_in_the_tasks_scope_and_meet_the_gate()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        ReadStatus open = default, read = default;
        long value = 0;
        ExchangeResult<long> write = default;
        var onMain = false;
        KilnTask? task = null;
        Pool.OnMain(() => task = mod.Tasks.Run("client-writes", async ctx =>
        {
            await ctx.ToTick();  // the client world's step, on the game's main thread
            onMain = Pool.IsMainThread;
            var r = WorldConcurrent.Open(new EntityId(12));
            open = r.Status;
            var c = WorldConcurrent.Read<long>(r.Value, "Counter");
            (read, value) = (c.Status, c.Value);
            write = WorldConcurrent.TryCompareExchange(r.Value, "Counter", 5L, 6L);
        }));
        for (var i = 0; i < 10 && !task!.WaitEnded(TimeSpan.FromMilliseconds(20)); i++)
        {
            Pool.WaitIdle();
            Pool.ClientStep();
        }
        Ended(task!);
        Assert.Equal(KilnTaskState.Completed, task!.State);
        Assert.True(onMain);
        Assert.Equal((ReadStatus.Ok, ReadStatus.Ok, 5L), (open, read, value));
        // the same gate a task write meets: it reached kiln.world (not the server tick's in-frame Busy) and wrote
        Assert.Equal(ExchangeStatus.Ok, write.Status);
        Assert.True(write.Written);
        Assert.Equal(6L, Counter(12));
        Assert.Equal(1, FakeEcsReads.Writes);
        Assert.All(FakeEcsReads.Owners, o => Assert.Equal(mod.Owner, o));
    }

    [Fact]
    public void The_main_thread_outside_a_task_is_still_refused()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Counter", Le(5)));
        Exception? refused = null;
        Pool.OnMain(() => refused = Record.Exception(() => WorldConcurrent.Open(new EntityId(12))));
        Assert.IsType<InvalidOperationException>(refused);
        Assert.Equal(0, FakeEcsReads.Writes);
        _ = mod;
    }
}
