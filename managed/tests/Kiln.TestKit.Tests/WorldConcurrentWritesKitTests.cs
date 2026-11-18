// SPDX-License-Identifier: MIT
// WorldConcurrent's compare-exchange writes on the kit's kiln.world (World/EcsReadsFake.cs over the fake world, with the
// shared rules of Shared/EcsWritesLogic.cs): a task changes a plain component it was told is untracked, a conflict hands
// back the current bytes, a component whose tracking the test did not give is not writable (as kiln.world answers when it
// cannot read the bits), a tracked one neither, BusyWrites makes the gate busy for the retry loop, the row off is a build
// without the writes, and a committed write comes back as ComponentExchangedEvent on the next step, naming the mod.
using TkEvents;
using Xunit;

#pragma warning disable KILNX001

namespace Kiln.TestKit.Tests;

public sealed class WorldConcurrentWritesKitTests
{
    private static (KilnHarness Kiln, ModHandle Mod, EntityId Chest) Start(bool untracked = true)
    {
        var kiln = new KilnHarness();
        var mod = kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        var chest = kiln.Entities.Create(new WorldPosition(10, 0, 10));
        kiln.Items.AddContainer(chest);
        if (untracked) kiln.Entities.Track("Inventory", save: false, replication: false);
        return (kiln, mod, chest);
    }

    private static T InTask<T>(KilnHarness kiln, ModHandle mod, Func<KilnTaskContext, Task<T>> work)
    {
        var task = kiln.As(mod, () => mod.Context.Tasks.Run<T>("write", work));
        kiln.Tasks.Settle();
        Assert.True(task.State == KilnTaskState.Completed, $"the task {task.State}: {task.Fault}");
        return task.Result;
    }

    private static byte[] Changed(byte[] bytes)
    {
        var b = bytes.ToArray();
        b[0] ^= 1;
        return b;
    }

    [Fact]
    public void A_task_exchanges_a_plain_untracked_component_and_the_event_follows()
    {
        var (kiln, mod, chest) = Start();
        using var _ = kiln;
        var before = kiln.Entities.GetBytes(chest, "Inventory")!;
        var policy = kiln.Run(() => WorldConcurrent.WritePolicy("Inventory"));
        Assert.True(policy.IsWritable, policy.ToString());
        Assert.Equal(96, policy.Size);
        var seen = new List<ComponentExchangedEvent>();
        using var sub = kiln.Run(() => Events.On<ComponentExchangedEvent>(seen.Add));
        var want = Changed(before);
        var (status, written, conflict, current) = InTask(kiln, mod, async ctx =>
        {
            var e = WorldConcurrent.Open(chest).Value;
            var r = WorldConcurrent.TryCompareExchangeBytes(e, "Inventory", before, want);
            var cur = new byte[96];
            var c = WorldConcurrent.TryCompareExchangeBytes(e, "Inventory", before, want, default, cur);  // `before` is stale now
            return (r.Status, r.Written, c.Status, cur);
        });
        Assert.Equal((ExchangeStatus.Ok, true, ExchangeStatus.Conflict), (status, written, conflict));
        Assert.Equal(want, current);
        Assert.Equal(want, kiln.Entities.GetBytes(chest, "Inventory"));
        Assert.Empty(seen);  // delivered on the tick, after the write
        kiln.Step();
        var ev = Assert.Single(seen);
        Assert.Equal((chest, "tk-events"), (ev.Entity, ev.Mod));
        Assert.True(ev.Is("Inventory"));
    }

    // Kiln's one subscription to the writes' events is made once per process, as the service's are: every harness after
    // the first gets its events too, naming the mod.
    [Fact]
    public void Every_harness_gets_the_events_not_only_the_first()
    {
        for (var round = 0; round < 3; round++)
        {
            var (kiln, mod, chest) = Start();
            using var _ = kiln;
            var before = kiln.Entities.GetBytes(chest, "Inventory")!;
            var seen = new List<ComponentExchangedEvent>();
            using var sub = kiln.Run(() => Events.On<ComponentExchangedEvent>(seen.Add));
            var status = InTask(kiln, mod, async ctx =>
                WorldConcurrent.TryCompareExchangeBytes(WorldConcurrent.Open(chest).Value, "Inventory", before, Changed(before)).Status);
            Assert.Equal(ExchangeStatus.Ok, status);
            kiln.Step();
            var ev = Assert.Single(seen);
            Assert.True((chest, "tk-events") == (ev.Entity, ev.Mod), $"round {round}: {ev.Entity} {ev.Mod ?? "(no mod)"}");
        }
    }

    [Fact]
    public void Tracking_the_test_did_not_give_is_unknown_and_tracked_is_refused()
    {
        var (kiln, mod, chest) = Start(untracked: false);
        using var _ = kiln;
        var before = kiln.Entities.GetBytes(chest, "Inventory")!;
        Assert.Equal(WriteReason.TrackingUnknown, kiln.Run(() => WorldConcurrent.WritePolicy("Inventory")).Reason);
        var unknown = InTask(kiln, mod, async ctx =>
            WorldConcurrent.TryCompareExchangeBytes(WorldConcurrent.Open(chest).Value, "Inventory", before, Changed(before)));
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.TrackingUnknown), (unknown.Status, unknown.Reason));
        kiln.Entities.Track("Inventory", save: true, replication: false);
        var tracked = kiln.Run(() => WorldConcurrent.WritePolicy("Inventory"));
        Assert.Equal((ExchangeStatus.NotWritable, WriteReason.Tracked, true), (tracked.Status, tracked.Reason, tracked.IsTracked));
        Assert.Equal(before, kiln.Entities.GetBytes(chest, "Inventory"));
    }

    [Fact]
    public void A_busy_gate_is_retried_after_the_next_gate_window()
    {
        var (kiln, mod, chest) = Start();
        using var _ = kiln;
        var before = kiln.Entities.GetBytes(chest, "Inventory")!;
        kiln.Entities.BusyWrites = 1;
        var (first, then) = InTask(kiln, mod, async ctx =>
        {
            var e = WorldConcurrent.Open(chest).Value;
            var r = WorldConcurrent.TryCompareExchangeBytes(e, "Inventory", before, Changed(before));
            var first = r.Status;
            while (r.Status == ExchangeStatus.Busy)
            {
                await ctx.NextGateWindow();
                r = WorldConcurrent.TryCompareExchangeBytes(e, "Inventory", before, Changed(before));
            }
            return (first, r.Status);
        });
        Assert.Equal((ExchangeStatus.Busy, ExchangeStatus.Ok), (first, then));
        Assert.Equal(Changed(before), kiln.Entities.GetBytes(chest, "Inventory"));
    }

    [Fact]
    public void On_the_tick_it_is_busy_and_writes_nothing()
    {
        var (kiln, mod, chest) = Start();
        using var _ = kiln;
        var before = kiln.Entities.GetBytes(chest, "Inventory")!;
        var r = InTask(kiln, mod, async ctx => WorldConcurrent.Open(chest).Value);
        var onTick = kiln.As(mod, () => WorldConcurrent.TryCompareExchangeBytes(r, "Inventory", before, Changed(before)));
        Assert.Equal((ExchangeStatus.Busy, WriteReason.InFrame), (onTick.Status, onTick.Reason));
        Assert.Equal(before, kiln.Entities.GetBytes(chest, "Inventory"));
    }

    [Fact]
    public void The_row_off_is_a_build_without_the_writes()
    {
        var (kiln, mod, chest) = Start();
        using var _ = kiln;
        kiln.Capabilities.Override(WorldConcurrent.WriteCapability, false);
        WorldConcurrent.Reset();
        var before = kiln.Entities.GetBytes(chest, "Inventory")!;
        Assert.False(kiln.Run(() => WorldConcurrent.CanWrite));
        var off = InTask(kiln, mod, async ctx =>
            WorldConcurrent.TryCompareExchangeBytes(WorldConcurrent.Open(chest).Value, "Inventory", before, Changed(before)));
        Assert.Equal(ExchangeStatus.Unavailable, off.Status);
        Assert.True(kiln.Run(() => WorldConcurrent.IsAvailable));  // the reads stay on
    }
}
