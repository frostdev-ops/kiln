// SPDX-License-Identifier: MIT
// WorldConcurrent on the kit's kiln.world (World/EcsReadsFake.cs over the fake world, with the shared rules of
// Shared/EcsReadsLogic.cs): a task reads the fake world's entities from a worker, a bounded query finds what stands near,
// a player's slot reused is a new entity (stale), a component added is a new layout (stale), the reads switch off as on a
// build with no exact profile (and OrOnTick takes the tick path), BusyReads makes frame-quiet reads busy, and a container
// carries the game's Inventory component.
using TkEvents;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class WorldConcurrentKitTests
{
    private const ulong AdaSteam = 76561198000000701, BoSteam = 76561198000000702;

    private static (KilnHarness Kiln, ModHandle Mod) Start()
    {
        var kiln = new KilnHarness();
        var mod = kiln.Load<TkEventsMod>(new ModManifest("tk-events"));
        kiln.Step();
        return (kiln, mod);
    }

    /// <summary>Runs <paramref name="read"/> in a task of the mod (on a worker) and gives back what it returned.</summary>
    private static T InTask<T>(KilnHarness kiln, ModHandle mod, Func<T> read)
    {
        var task = kiln.As(mod, () => mod.Context.Tasks.Run<T>("read", async ctx => read()));
        kiln.Tasks.Settle();
        Assert.True(task.State == KilnTaskState.Completed, $"the task {task.State}: {task.Fault}");
        return task.Result;
    }

    [Fact]
    public void A_task_reads_the_fake_world_and_finds_the_containers_near_a_point()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var near = kiln.Entities.Create(new WorldPosition(10, 0, 10));
        var far = kiln.Entities.Create(new WorldPosition(60, 0, 10));
        kiln.Items.AddContainer(near);
        kiln.Items.AddContainer(far);
        var plain = kiln.Entities.Create(new WorldPosition(11, 0, 10));  // no inventory
        Assert.True(kiln.Run(() => WorldConcurrent.IsAvailable && WorldConcurrent.IsPresent(WorldKind.Server)));
        var (found, ids, x, size) = InTask(kiln, mod, () =>
        {
            var refs = new ConcurrentEntity[8];
            var q = WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Inventory") { Center = new WorldPosition(10, 0, 12), Radius = 5 }, refs);
            var x = WorldConcurrent.Read<long>(refs[0], "CurrentTransform").Value / 4294967296.0;  // x, 32.32 fixed point
            var bytes = new byte[256];
            var copy = WorldConcurrent.ReadBytes(refs[0], "Inventory", bytes);
            return (q.Status, refs[..q.Count].Select(r => r.Id).ToArray(), x, copy.Written);
        });
        Assert.Equal(ReadStatus.Ok, found);
        Assert.Equal([near], ids);
        Assert.Equal(10, x);
        Assert.Equal(96, size);  // keen::ecs::Inventory: 8 ItemStacks of 12 bytes
        Assert.DoesNotContain(plain, ids);
        Assert.DoesNotContain(far, ids);
    }

    [Fact]
    public void A_container_carries_the_games_Inventory_component()
    {
        using var kiln = new KilnHarness();
        var chest = kiln.Entities.Create(new WorldPosition(0, 0, 0));
        kiln.Items.AddContainer(chest);
        Assert.Equal(96, kiln.Entities.GetBytes(chest, "Inventory")!.Length);
        Assert.True(kiln.Run(() => World.Has(chest, "Inventory")));
    }

    [Fact]
    public void A_reused_player_slot_and_a_new_component_make_a_ref_stale()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var ada = kiln.Players.Join("Ada", AdaSteam, position: new WorldPosition(1, 0, 1));
        kiln.Step();
        var id = ada.Entity;
        var r = InTask(kiln, mod, () => WorldConcurrent.Open(id));
        Assert.Equal(ReadStatus.Ok, r.Status);
        ada.Leave();
        kiln.Step();
        var bo = kiln.Players.Join("Bo", BoSteam, position: new WorldPosition(2, 0, 2));
        kiln.Step();
        Assert.Equal(id, bo.Entity);  // the slot's id comes back for the next player
        Assert.Equal(ReadStatus.Stale, InTask(kiln, mod, () => WorldConcurrent.Validate(r.Value)));
        var chest = kiln.Entities.Create(new WorldPosition(0, 0, 0));
        var c = InTask(kiln, mod, () => WorldConcurrent.Open(chest)).Value;
        Assert.Equal(ReadStatus.Ok, InTask(kiln, mod, () => WorldConcurrent.Validate(c)));
        kiln.Items.AddContainer(chest);  // a component added: another layout
        Assert.Equal(ReadStatus.Stale, InTask(kiln, mod, () => WorldConcurrent.Validate(c)));
    }

    [Fact]
    public void Switched_off_the_reads_are_unavailable_and_OrOnTick_takes_the_tick_path()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var chest = kiln.Entities.Create(new WorldPosition(0, 0, 0));
        kiln.Capabilities.Override("world.ecs_read", false);
        Assert.False(kiln.Run(() => WorldConcurrent.IsAvailable));
        Assert.Equal(ReadStatus.Unavailable, InTask(kiln, mod, () => WorldConcurrent.Open(chest).Status));
        var where = kiln.As(mod, () => mod.Context.Tasks.Run<bool>("fallback", async ctx =>
            await WorldConcurrent.OrOnTick(ctx, () => Game.IsTickThread, () => Game.IsTickThread)));
        kiln.Tasks.Settle();
        Assert.True(where.Result);
    }

    [Fact]
    public void BusyReads_makes_the_next_frame_quiet_reads_busy()
    {
        var (kiln, mod) = Start();
        using var _ = kiln;
        var chest = kiln.Entities.Create(new WorldPosition(0, 0, 0));
        kiln.Run(() => kiln.Entities.BusyReads = 2);
        var seen = new List<ReadStatus>();
        var task = kiln.As(mod, () => mod.Context.Tasks.Run("retry", async ctx =>
        {
            var e = WorldConcurrent.Open(chest).Value;
            ConcurrentRead<long> r;
            while ((r = WorldConcurrent.Read<long>(e, "CurrentTransform", ReadMode.FrameQuiet)).Status == ReadStatus.Busy)
            {
                seen.Add(r.Status);
                await ctx.NextGateWindow();
            }
            seen.Add(r.Status);
            seen.Add(WorldConcurrent.Read<long>(e, "CurrentTransform").Status);
        }));
        kiln.Tasks.Settle();
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.Equal([ReadStatus.Busy, ReadStatus.Busy, ReadStatus.Ok, ReadStatus.Ok], seen);
        Assert.Equal(0, kiln.Run(() => kiln.Entities.BusyReads));
    }
}
