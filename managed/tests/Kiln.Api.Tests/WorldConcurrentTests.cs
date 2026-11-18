// SPDX-License-Identifier: MIT
// WorldConcurrent (Kiln.World) over a fake kiln.world table (FakeEcsReads) and the task API's fake pool: every status
// comes through as itself, both read modes are reachable, a frame-quiet read on the server tick is busy without reaching
// the engine, the calls work in a task's segments, its tick hops and on the tick and are refused everywhere else, a
// short buffer reports its size and copies nothing, the reads are unavailable without the members, their capability or
// an exact profile, and OrOnTick takes the tick path then.
using System.Runtime.InteropServices;
using Xunit;

#pragma warning disable xUnit1031, xUnit1051  // the test thread is the mod's own thread; task bodies stand in for mod code

namespace Kiln.Api.Tests;

public sealed class WorldConcurrentTests : TaskTest
{
    private static readonly byte[] Health = [1, 0, 0, 0, 2, 0, 0, 0];  // two ints: 1, 2

    public WorldConcurrentTests()
    {
        FakeEcsReads.Reset();
        Backend.Tables["kiln.world"] = FakeEcsReads.Table;
        WorldNative.Reset();
        WorldConcurrent.Reset();
    }

    public override void Dispose()
    {
        try
        {
            Backend.Tables.Remove("kiln.world");
            WorldNative.Reset();
            WorldConcurrent.Reset();
        }
        finally
        {
            base.Dispose();
        }
    }

    /// <summary>Runs <paramref name="body"/> in a task of <paramref name="mod"/> and gives back its result.</summary>
    private static T InTask<T>(ModContext mod, Func<T> body)
    {
        var result = default(T);
        var task = mod.Tasks.Run("reads", ctx => { result = body(); });
        Ended(task);
        if (task.Fault is { } fault) throw new Xunit.Sdk.XunitException($"the task faulted: {fault}");
        return result!;
    }

    private static ConcurrentEntity Opened(ModContext mod, uint id, WorldKind kind = WorldKind.Server)
    {
        var open = InTask(mod, () => WorldConcurrent.Open(new EntityId(id), kind));
        Assert.Equal(ReadStatus.Ok, open.Status);
        return open.Value;
    }

    // ---- results ----

    [Fact]
    public void A_task_opens_and_reads_an_entity()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var (open, read, bytes) = InTask(mod, () =>
        {
            var open = WorldConcurrent.Open(new EntityId(12));
            return (open, WorldConcurrent.Read<long>(open.Value, "Health"), WorldConcurrent.Read<int>(open.Value, "Health"));
        });
        Assert.Equal(ReadStatus.Ok, open.Status);
        Assert.Equal((12u, WorldKind.Server, 1u, 1ul), (open.Value.Id.Value, open.Value.World, open.Value.Generation, open.Value.Session));
        Assert.Equal((ReadStatus.Ok, 0x0000000200000001L), (read.Status, read.Value));
        Assert.Equal((ReadStatus.Ok, 1), (bytes.Status, bytes.Value));  // the first sizeof(T) bytes, as World.TryRead
        Assert.All(FakeEcsReads.Owners, o => Assert.Equal(mod.Owner, o));  // the task's mod is the owner of every call
    }

    [Fact]
    public void Absent_entity_and_absent_component_are_absent_not_exceptions()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        var (noEntity, noComponent, noTemplate, none) = InTask(mod, () => (WorldConcurrent.Open(new EntityId(99)).Status,
            WorldConcurrent.Read<int>(r, "Inventory").Status, WorldConcurrent.Template(r).Status, WorldConcurrent.Open(EntityId.None).Status));
        Assert.Equal((ReadStatus.Absent, ReadStatus.Absent, ReadStatus.Absent, ReadStatus.Absent), (noEntity, noComponent, noTemplate, none));
    }

    [Fact]
    public void A_recycled_id_is_stale_and_absent_is_another_answer()
    {
        var mod = NewMod("alpha");
        var e = FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        e.Generation = 2;  // the id now names a new entity
        Assert.Equal((ReadStatus.Stale, ReadStatus.Stale), InTask(mod, () => (WorldConcurrent.Validate(r), WorldConcurrent.Read<int>(r, "Health").Status)));
        FakeEcsReads.Entities.Remove((0, 12));
        Assert.Equal(ReadStatus.Absent, InTask(mod, () => WorldConcurrent.Validate(r)));
        var again = FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var fresh = Opened(mod, 12);
        again.Layout = 0x2000;  // a component added or removed: the layout changed
        Assert.Equal(ReadStatus.Stale, InTask(mod, () => WorldConcurrent.Read<int>(fresh, "Health").Status));
    }

    [Fact]
    public void Unreadable_and_template_and_validate_come_through()
    {
        var mod = NewMod("alpha");
        var guid = Guid.NewGuid();
        var e = FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        e.Template = guid;
        var r = Opened(mod, 12);
        FakeEcsReads.UnreadableNext = 1;
        var (unreadable, retried, template, valid) = InTask(mod, () => (WorldConcurrent.Read<int>(r, "Health").Status,
            WorldConcurrent.Read<int>(r, "Health").Status, WorldConcurrent.Template(r), WorldConcurrent.Validate(r)));
        Assert.Equal(ReadStatus.Unreadable, unreadable);
        Assert.Equal(ReadStatus.Ok, retried);
        Assert.Equal((ReadStatus.Ok, guid), (template.Status, template.Value));
        Assert.Equal(ReadStatus.Ok, valid);
    }

    [Fact]
    public void Refs_from_before_a_reload_are_stale_after_it()
    {
        var before = NewMod("alpha");  // the same mod's next load is a new owner, and its owner id is its epoch
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(before, 12);
        var after = NewMod("alpha");
        Assert.Equal(ReadStatus.Ok, InTask(after, () => WorldConcurrent.Open(new EntityId(12)).Status));
        Assert.Equal((ReadStatus.Stale, ReadStatus.Stale), InTask(after, () => (WorldConcurrent.Validate(r), WorldConcurrent.Read<int>(r, "Health").Status)));
    }

    [Fact]
    public void A_default_ref_or_a_missing_name_is_misuse()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        Assert.Throws<ArgumentException>(() => InTaskRethrow(mod, () => WorldConcurrent.Read<int>(default, "Health")));
        Assert.Throws<ArgumentException>(() => InTaskRethrow(mod, () => WorldConcurrent.Validate(default)));
        Assert.Throws<ArgumentException>(() => InTaskRethrow(mod, () => WorldConcurrent.Read<int>(r, "")));
        Assert.Throws<ArgumentException>(() => InTaskRethrow(mod, () => WorldConcurrent.Query(default, new ConcurrentEntity[4])));
        Assert.Throws<ArgumentException>(() => InTaskRethrow(mod, () =>
            WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health") { Center = new WorldPosition(0, 0, 0) }, new ConcurrentEntity[4])));
    }

    private static T InTaskRethrow<T>(ModContext mod, Func<T> body)
    {
        var task = mod.Tasks.Run("misuse", ctx => { body(); });
        Ended(task);
        throw task.Fault ?? new Xunit.Sdk.XunitException("no exception");
    }

    // ---- both read modes ----

    [Fact]
    public void Frame_quiet_reads_are_busy_while_a_frame_runs_and_stable_ones_never_are()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        FakeEcsReads.FrameRunning = true;
        var q = new EntityQuery(WorldKind.Server, "Health") { Mode = ReadMode.FrameQuiet };
        var busy = InTask(mod, () => (WorldConcurrent.Read<int>(r, "Health", ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadBytes(r, "Health", new byte[8], ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadMany(r, new ComponentSet("Health"), ReadMode.FrameQuiet).Status,
            WorldConcurrent.Query(q, new ConcurrentEntity[4]).Status,
            WorldConcurrent.Read<int>(r, "Health").Status));
        Assert.Equal((ReadStatus.Busy, ReadStatus.Busy, ReadStatus.Busy, ReadStatus.Busy, ReadStatus.Ok), busy);
        FakeEcsReads.FrameRunning = false;
        var quiet = InTask(mod, () => (WorldConcurrent.Read<int>(r, "Health", ReadMode.FrameQuiet), WorldConcurrent.Query(q, new ConcurrentEntity[4])));
        Assert.Equal((ReadStatus.Ok, 1, ReadStatus.Ok, 1), (quiet.Item1.Status, quiet.Item1.Value, quiet.Item2.Status, quiet.Item2.Count));
        Assert.True(FakeEcsReads.CallsOf("SnapshotMany") >= 3);  // frame-quiet reads go through snapshot_many with the flag
    }

    [Fact]
    public void A_busy_read_is_retried_after_the_next_gate_window()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        FakeEcsReads.FrameRunning = true;
        var statuses = new List<ReadStatus>();
        var task = mod.Tasks.Run("retry", async ctx =>
        {
            ConcurrentRead<int> read;
            while ((read = WorldConcurrent.Read<int>(r, "Health", ReadMode.FrameQuiet)).Status == ReadStatus.Busy)
            {
                statuses.Add(read.Status);
                await ctx.NextGateWindow();
            }
            statuses.Add(read.Status);
        });
        Pool.WaitIdle();
        FakeEcsReads.FrameRunning = false;
        Pool.Step();
        Ended(task);
        Assert.Equal([ReadStatus.Busy, ReadStatus.Ok], statuses);
    }

    [Fact]
    public void Frame_quiet_on_a_client_world_is_unsupported()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(3, WorldKind.Client, ("Health", Health));
        var r = Opened(mod, 3, WorldKind.Client);
        var s = InTask(mod, () => (WorldConcurrent.Read<int>(r, "Health", ReadMode.FrameQuiet).Status,
            WorldConcurrent.Query(new EntityQuery(WorldKind.Client, "Health") { Mode = ReadMode.FrameQuiet }, new ConcurrentEntity[4]).Status,
            WorldConcurrent.Read<int>(r, "Health").Status));
        Assert.Equal((ReadStatus.Unsupported, ReadStatus.Unsupported, ReadStatus.Ok), s);
    }

    [Fact]
    public void Frame_quiet_on_the_server_tick_is_busy_without_reaching_the_engine()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        var calls = FakeEcsReads.EntityCalls;
        (ReadStatus, ReadStatus, ReadStatus, ReadStatus) onTick = default, inHop = default;
        TickAs(mod, () => onTick = (WorldConcurrent.Read<int>(r, "Health", ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadBytes(r, "Health", new byte[8], ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadMany(r, new ComponentSet("Health"), ReadMode.FrameQuiet).StatusOf(0),
            WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health") { Mode = ReadMode.FrameQuiet }, new ConcurrentEntity[4]).Status));
        var task = mod.Tasks.Run("hop", async ctx =>
        {
            await ctx.ToTick();
            inHop = (WorldConcurrent.Read<int>(r, "Health", ReadMode.FrameQuiet).Status,
                WorldConcurrent.ReadBytes(r, "Health", new byte[8], ReadMode.FrameQuiet).Status,
                WorldConcurrent.ReadMany(r, new ComponentSet("Health"), ReadMode.FrameQuiet).Status,
                WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health") { Mode = ReadMode.FrameQuiet }, new ConcurrentEntity[4]).Status);
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        var busy = (ReadStatus.Busy, ReadStatus.Busy, ReadStatus.Busy, ReadStatus.Busy);
        Assert.Equal(busy, onTick);
        Assert.Equal(busy, inHop);
        Assert.Equal(calls, FakeEcsReads.EntityCalls);  // none of them reached kiln.world
        ReadStatus stable = default;
        TickAs(mod, () => stable = WorldConcurrent.Read<int>(r, "Health").Status);
        Assert.Equal(ReadStatus.Ok, stable);  // a stable read works on the tick
    }

    // ---- threads ----

    [Fact]
    public void A_raw_thread_is_refused()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        Exception? caught = null;
        var raw = new Thread(() =>
        {
            try { WorldConcurrent.Open(new EntityId(12)); }
            catch (Exception e) { caught = e; }
        });
        raw.Start();
        raw.Join();
        var e = Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("inside a Kiln task or on the tick", e.Message);
        Assert.Equal(0, FakeEcsReads.EntityCalls);
        _ = mod;
    }

    [Fact]
    public void Code_after_ConfigureAwait_false_or_in_Task_Run_is_refused()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        Exception? afterConfigureAwait = null, inTaskRun = null;
        var task = mod.Tasks.Run("escapes", async ctx =>
        {
            await Task.Delay(1).ConfigureAwait(false);
            try { WorldConcurrent.Open(new EntityId(12)); }
            catch (Exception e) { afterConfigureAwait = e; }
            await Task.Run(() =>
            {
                try { WorldConcurrent.Open(new EntityId(12)); }
                catch (Exception e) { inTaskRun = e; }
            });
        });
        Ended(task);
        var a = Assert.IsType<InvalidOperationException>(afterConfigureAwait);
        Assert.Contains("ConfigureAwait(false)", a.Message);
        Assert.Contains("alpha/escapes", a.Message);  // it names the task the code came from
        Assert.IsType<InvalidOperationException>(inTaskRun);
        Assert.Equal(0, FakeEcsReads.EntityCalls);
    }

    [Fact]
    public void Segments_tick_hops_and_the_tick_are_accepted()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var seen = new List<ReadStatus>();
        var task = mod.Tasks.Run("everywhere", async ctx =>
        {
            seen.Add(WorldConcurrent.Open(new EntityId(12)).Status);
            await Task.Yield();
            seen.Add(WorldConcurrent.Open(new EntityId(12)).Status);
            await ctx.ToTick();
            seen.Add(WorldConcurrent.Open(new EntityId(12)).Status);
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        TickAs(mod, () => seen.Add(WorldConcurrent.Open(new EntityId(12)).Status));
        Assert.Equal([ReadStatus.Ok, ReadStatus.Ok, ReadStatus.Ok, ReadStatus.Ok], seen);
    }

    [Fact]
    public void The_tick_outside_a_mods_code_is_refused()
    {
        Exception? caught = null;
        Pool.Step(() =>
        {
            try { WorldConcurrent.Open(new EntityId(12)); }
            catch (Exception e) { caught = e; }
        });
        Assert.IsType<InvalidOperationException>(caught);
    }

    // ---- buffers ----

    [Fact]
    public void A_short_buffer_gives_the_size_and_copies_nothing()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        FakeEcsReads.Add(13, WorldKind.Server, ("Health", Health));
        FakeEcsReads.Add(14, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        var small = new byte[] { 9, 9, 9, 9 };
        var refs = new ConcurrentEntity[2];
        var (copy, frameQuietCopy, query) = InTask(mod, () => (WorldConcurrent.ReadBytes(r, "Health", small),
            WorldConcurrent.ReadBytes(r, "Health", small, ReadMode.FrameQuiet), WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health"), refs)));
        Assert.Equal((ReadStatus.TooSmall, 0, 8), (copy.Status, copy.Written, copy.Required));
        Assert.Equal((ReadStatus.TooSmall, 0, 8), (frameQuietCopy.Status, frameQuietCopy.Written, frameQuietCopy.Required));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, small);
        Assert.Equal((ReadStatus.TooSmall, 0, 3), (query.Status, query.Count, query.Required));
        Assert.All(refs, x => Assert.True(x.IsNone));
        var whole = new byte[16];
        var fits = InTask(mod, () => WorldConcurrent.ReadBytes(r, "Health", whole));
        Assert.Equal((ReadStatus.Ok, 8, 8), (fits.Status, fits.Written, fits.Required));
        Assert.Equal(Health, whole[..8]);
    }

    [Fact]
    public void A_short_frame_quiet_buffer_is_too_small_while_a_frame_runs()
    {
        // ecs_snapshot_many sizes the request before it opens its frame-quiet window: a buffer that is too short says so
        // (with the size) even while a frame runs, and only the copy into a buffer that fits can be busy
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        FakeEcsReads.FrameRunning = true;
        var (small, fits) = InTask(mod, () => (WorldConcurrent.ReadBytes(r, "Health", new byte[4], ReadMode.FrameQuiet),
            WorldConcurrent.ReadBytes(r, "Health", new byte[8], ReadMode.FrameQuiet)));
        Assert.Equal((ReadStatus.TooSmall, 0, 8), (small.Status, small.Written, small.Required));
        Assert.Equal((ReadStatus.Busy, 0, 0), (fits.Status, fits.Written, fits.Required));
    }

    [Fact]
    public void A_component_smaller_than_T_is_a_size_mismatch()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);
        var read = InTask(mod, () => WorldConcurrent.Read<Guid>(r, "Health"));
        Assert.Equal((ReadStatus.SizeMismatch, Guid.Empty), (read.Status, read.Value));
    }

    // ---- queries and snapshots ----

    [Fact]
    public void A_query_is_complete_in_ascending_id_bounded_or_changed()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(30, WorldKind.Server, ("Health", Health), ("Inventory", [1])).Position = new WorldPosition(0, 0, 0);
        FakeEcsReads.Add(10, WorldKind.Server, ("Health", Health), ("Inventory", [1])).Position = new WorldPosition(1, 0, 0);
        FakeEcsReads.Add(20, WorldKind.Server, ("Health", Health)).Position = new WorldPosition(0, 0, 0);
        FakeEcsReads.Add(40, WorldKind.Server, ("Health", Health), ("Inventory", [1])).Position = new WorldPosition(50, 0, 0);
        var refs = new ConcurrentEntity[8];
        var all = InTask(mod, () => WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health", "Inventory"), refs));
        Assert.Equal((ReadStatus.Ok, 3, 3), (all.Status, all.Count, all.Required));
        Assert.Equal([10u, 30u, 40u], refs[..3].Select(x => x.Id.Value));
        var near = InTask(mod, () => WorldConcurrent.Query(
            new EntityQuery(WorldKind.Server, "Inventory") { Center = new WorldPosition(0, 0, 0), Radius = 5 }, refs));
        Assert.Equal((ReadStatus.Ok, 2), (near.Status, near.Count));
        Assert.Equal(ReadStatus.Ok, InTask(mod, () => WorldConcurrent.Validate(refs[0])));  // query refs carry the mod's epoch
        FakeEcsReads.ChangedNext = 1;
        var changed = InTask(mod, () => WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health"), refs));
        Assert.Equal((ReadStatus.Changed, 0), (changed.Status, changed.Count));
    }

    [Fact]
    public void Query_refs_keep_the_mods_epoch_when_it_opens_later_and_go_stale_after_a_reload()
    {
        var before = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        FakeEcsReads.Add(13, WorldKind.Server, ("Health", Health));
        var refs = new ConcurrentEntity[4];
        Assert.Equal(ReadStatus.Ok, InTask(before, () => WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health"), refs)).Status);
        // the mod's first Open after its first query must not turn the query's refs stale
        Assert.Equal((ReadStatus.Ok, ReadStatus.Ok), InTask(before, () => (WorldConcurrent.Open(new EntityId(13)).Status, WorldConcurrent.Validate(refs[0]))));
        var queried = refs[0];
        // the next load of the mod only queries: a query ref of the load before is still stale for it
        var after = NewMod("alpha");
        var mine = new ConcurrentEntity[4];
        Assert.Equal(ReadStatus.Ok, InTask(after, () => WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health"), mine)).Status);
        Assert.Equal((ReadStatus.Stale, ReadStatus.Ok), InTask(after, () => (WorldConcurrent.Validate(queried), WorldConcurrent.Validate(mine[0]))));
    }

    [Fact]
    public void A_query_centre_or_radius_that_is_not_finite_is_misuse()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health)).Position = new WorldPosition(0, 0, 0);
        foreach (var q in new[]
        {
            new EntityQuery(WorldKind.Server, "Health") { Center = new WorldPosition(double.NaN, 0, 0), Radius = 5 },
            new EntityQuery(WorldKind.Server, "Health") { Center = new WorldPosition(0, double.PositiveInfinity, 0), Radius = 5 },
            new EntityQuery(WorldKind.Server, "Health") { Center = new WorldPosition(0, 0, 0), Radius = double.PositiveInfinity },
            new EntityQuery(WorldKind.Server, "Health") { Center = new WorldPosition(0, 0, 0), Radius = 1e300 },  // radius squared overflows
        })
            Assert.Throws<ArgumentException>(() => InTaskRethrow(mod, () => WorldConcurrent.Query(q, new ConcurrentEntity[4])));
    }

    [Fact]
    public void Frame_quiet_on_the_tick_says_unavailable_or_unsupported_when_that_is_the_answer()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        FakeEcsReads.Add(3, WorldKind.Client, ("Health", Health));
        var server = Opened(mod, 12);
        var client = Opened(mod, 3, WorldKind.Client);
        var calls = FakeEcsReads.EntityCalls;
        (ReadStatus, ReadStatus, ReadStatus, ReadStatus) onClientWorld = default;
        TickAs(mod, () => onClientWorld = (WorldConcurrent.Read<int>(client, "Health", ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadBytes(client, "Health", new byte[8], ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadMany(client, new ComponentSet("Health"), ReadMode.FrameQuiet).Status,
            WorldConcurrent.Query(new EntityQuery(WorldKind.Client, "Health") { Mode = ReadMode.FrameQuiet }, new ConcurrentEntity[4]).Status));
        var unsupported = ReadStatus.Unsupported;
        Assert.Equal((unsupported, unsupported, unsupported, unsupported), onClientWorld);  // a retry would never help
        Assert.Equal(calls, FakeEcsReads.EntityCalls);
        FakeEcsReads.Capable = false;
        WorldConcurrent.Reset();
        (ReadStatus, ReadStatus, ReadStatus, ReadStatus) off = default;
        TickAs(mod, () => off = (WorldConcurrent.Read<int>(server, "Health", ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadBytes(server, "Health", new byte[8], ReadMode.FrameQuiet).Status,
            WorldConcurrent.ReadMany(server, new ComponentSet("Health"), ReadMode.FrameQuiet).Status,
            WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health") { Mode = ReadMode.FrameQuiet }, new ConcurrentEntity[4]).Status));
        var u = ReadStatus.Unavailable;
        Assert.Equal((u, u, u, u), off);  // not Busy: the reads do not work here at all
    }

    [Fact]
    public void ReadMany_takes_an_empty_component()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health), ("Marker", []));
        var r = Opened(mod, 12);
        var s = InTask(mod, () => WorldConcurrent.ReadMany(r, new ComponentSet("Marker", "Health")));
        Assert.Equal((ReadStatus.Ok, ReadStatus.Ok, ReadStatus.Ok), (s.Status, s.StatusOf(0), s.StatusOf(1)));
        Assert.True(s.Bytes(0).IsEmpty);
        Assert.Equal(Health, s.Bytes(1).ToArray());
    }

    [Fact]
    public void ReadMany_gives_each_component_its_status_and_bytes()
    {
        var mod = NewMod("alpha");
        FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health), ("Inventory", [7, 7, 7]));
        var r = Opened(mod, 12);
        var set = new ComponentSet("Inventory", "Mana", "Health");
        var s = InTask(mod, () => WorldConcurrent.ReadMany(r, set));
        Assert.Equal(ReadStatus.Ok, s.Status);
        Assert.Equal(3, s.Count);
        Assert.Equal([ReadStatus.Ok, ReadStatus.Absent, ReadStatus.Ok], Enumerable.Range(0, 3).Select(s.StatusOf));
        Assert.Equal(new byte[] { 7, 7, 7 }, s.Bytes(0).ToArray());
        Assert.True(s.Bytes(1).IsEmpty);
        Assert.Equal(Health, s.Bytes(2).ToArray());
        Assert.True(s.TryGet<int>(2, out var hp) && hp == 1);
        Assert.False(s.TryGet<int>(0, out _));  // 3 bytes: smaller than an int
        FakeEcsReads.Entities[(0, 12)].Generation = 5;
        var stale = InTask(mod, () => WorldConcurrent.ReadMany(r, set));
        Assert.Equal(ReadStatus.Stale, stale.Status);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(ReadStatus.Stale, stale.StatusOf(i)));
    }

    // ---- gating and the fallback ----

    [Fact]
    public void Present_worlds_come_from_the_context()
    {
        Assert.True(WorldConcurrent.IsPresent(WorldKind.Server));
        Assert.True(WorldConcurrent.IsPresent(WorldKind.Client));
        Assert.False(WorldConcurrent.IsPresent(WorldKind.Prediction));
        FakeEcsReads.Sessions[0] = 0;
        Assert.False(WorldConcurrent.IsPresent(WorldKind.Server));
    }

    public static TheoryData<string> Off => ["no kiln.world", "an older kiln.world", "no capability", "no exact profile"];

    [Theory]
    [MemberData(nameof(Off))]
    public void Without_the_reads_everything_is_unavailable(string why)
    {
        var mod = NewMod("alpha");
        var e = FakeEcsReads.Add(12, WorldKind.Server, ("Health", Health));
        var r = Opened(mod, 12);  // a ref from before
        switch (why)
        {
            case "no kiln.world": Backend.Tables.Remove("kiln.world"); break;
            case "an older kiln.world": FakeEcsReads.Reset(tableSize: 528); FakeEcsReads.Entities[(0, 12)] = e; break;
            case "no capability": FakeEcsReads.Capable = false; break;
            default: FakeEcsReads.ExactProfile = false; break;
        }
        WorldNative.Reset();
        WorldConcurrent.Reset();
        Assert.False(WorldConcurrent.IsAvailable);
        var calls = FakeEcsReads.EntityCalls;
        var s = InTask(mod, () => (WorldConcurrent.Open(new EntityId(12)).Status, WorldConcurrent.Validate(r),
            WorldConcurrent.Read<int>(r, "Health").Status, WorldConcurrent.ReadBytes(r, "Health", new byte[8]).Status,
            WorldConcurrent.ReadMany(r, new ComponentSet("Health")).Status, WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health"), new ConcurrentEntity[2]).Status,
            WorldConcurrent.Template(r).Status));
        var u = ReadStatus.Unavailable;
        Assert.Equal((u, u, u, u, u, u, u), s);
        if (why != "no exact profile") Assert.Equal(calls, FakeEcsReads.EntityCalls);  // without the members or the capability nothing is called
    }

    [Fact]
    public void OrOnTick_runs_in_the_task_when_the_reads_work()
    {
        var mod = NewMod("alpha");
        var where = 0;
        var task = mod.Tasks.Run("fallback", async ctx =>
        {
            var n = await WorldConcurrent.OrOnTick(ctx, () => { where = Environment.CurrentManagedThreadId; return 1; }, () => 2);
            Assert.Equal(1, n);
        });
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.True(Pool.IsWorker(where));
    }

    [Fact]
    public void OrOnTick_takes_the_tick_path_when_they_do_not()
    {
        FakeEcsReads.ExactProfile = false;
        FakeEcsReads.Capable = false;
        WorldConcurrent.Reset();
        var mod = NewMod("alpha");
        var where = 0;
        var offTickRan = false;
        var result = 0;
        var task = mod.Tasks.Run("fallback", async ctx =>
        {
            result = await WorldConcurrent.OrOnTick(ctx, () => { offTickRan = true; return 1; },
                () => { where = Environment.CurrentManagedThreadId; return 2; });
        });
        Pool.WaitIdle();
        Pool.Step();
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.False(offTickRan);
        Assert.Equal(2, result);
        Assert.Equal(Pool.TickThreadId, where);
    }

    [Fact]
    public void OrOnTick_hands_back_an_exception_through_the_task()
    {
        var mod = NewMod("alpha");
        Exception? caught = null;
        var task = mod.Tasks.Run("fallback", async ctx =>
        {
            try { await WorldConcurrent.OrOnTick<int>(ctx, () => throw new FormatException(), () => 2); }
            catch (FormatException e) { caught = e; }
        });
        Ended(task);
        Assert.IsType<FormatException>(caught);
    }

    [Fact]
    public void The_table_mirror_puts_the_members_where_the_header_does()
    {
        Assert.Equal(584, sizeof_table());
        Assert.Equal(528, (int)Marshal.OffsetOf<EcsReadsTable>(nameof(EcsReadsTable.EntityOpen)));
        Assert.Equal(576, (int)Marshal.OffsetOf<EcsReadsTable>(nameof(EcsReadsTable.TemplateUuid)));
        static unsafe int sizeof_table() => sizeof(EcsReadsTable);
    }
}
