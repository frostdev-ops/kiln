// SPDX-License-Identifier: MIT
// The overlay engine against the contract (docs/kiln/services/terrain.md): lowering and its refusals, batches and the
// budget, the ledger's rules (re-run, upgrade over an edit, resume, other mods, bases, worlds), and the events.
using Kiln;
using Xunit;

namespace TerrainTests;

public sealed class OverlayTests
{
    private static readonly TerrainPaletteEntry[] Palette = [new(12), new(0), new(40, 128)];

    /// <summary>A solid box of palette number <paramref name="number"/>.</summary>
    private static TerrainPatch Solid(string id, int version, CellBox region, byte number = 1, TerrainPersistence keep = TerrainPersistence.Kept)
    {
        var cells = new byte[region.Count];
        Array.Fill(cells, number);
        return new TerrainPatch(id, version, region, new TerrainVoxelVolume(cells), Palette, keep);
    }

    private sealed class Run(MemoryWorld world, string mod = "mod-a", TerrainSession? session = null)
    {
        public readonly MemoryWorld World = world;
        public readonly TerrainSession Session = session ?? new TerrainSession();
        public readonly List<TerrainChangedEvent> Events = [];

        public TerrainResult Apply(TerrainPatch patch, TerrainApplyOptions? options = null)
        {
            var o = options ?? new TerrainApplyOptions();
            var t = new TerrainEngine(World.For(mod), Session, Events.Add).Apply(TerrainPlan.Lower(patch), o, TerrainPlan.BatchEdge(o.CellsPerTick));
            Assert.True(t.IsCompleted, "the in-memory world never waits");
            return t.GetAwaiter().GetResult();
        }

        public TerrainPlanReport Inspect(TerrainPatch patch, TerrainApplyOptions? options = null)
        {
            var o = options ?? new TerrainApplyOptions();
            return new TerrainEngine(World.For(mod), Session, null).Inspect(TerrainPlan.Lower(patch), o, TerrainPlan.BatchEdge(o.CellsPerTick)).GetAwaiter().GetResult();
        }

        public TerrainEngine Engine => new(World.For(mod), Session, null);
    }

    // ---- lowering ----

    [Fact]
    public void A_patch_that_is_no_patch_throws_before_anything_is_read()
    {
        var box = new CellBox(64, 96, 64, 8, 8, 8);
        var ok = Solid("vein", 1, box);
        TerrainPlan.Lower(ok);
        TerrainPatch[] bad =
        [
            ok with { Id = "" }, ok with { Id = "Vein" }, ok with { Id = new string('a', 65) }, ok with { Version = 0 },
            ok with { Region = new CellBox(64, 96, 64, 0, 8, 8) },
            Solid("vein", 1, new CellBox(0, 0, 0, 257, 1, 1)),
            ok with { Region = new CellBox(64, 96, 64, 8, 8, 9) },                    // 576 cells for 512 numbers
            ok with { Region = new CellBox(-8, 96, 64, 8, 8, 8) },
            ok with { Region = new CellBox(64, 8190, 64, 8, 8, 8) },                  // above y 8191
            ok with { Palette = [] },
            ok with { Palette = [new(117)] }, ok with { Palette = [new(127)] }, ok with { Palette = [new(213)] },
            ok with { Palette = [new(12, 0)] },
            ok with { Source = new TerrainVoxelVolume([.. Enumerable.Repeat((byte)4, 512)]) },   // palette has three
            ok with { Source = new TerrainVoxelVolume(new byte[512]) },               // addresses nothing
            ok with { Source = new TerrainHeightfield([.. Enumerable.Repeat(float.NaN, 64)], 1) },
            ok with { Source = new TerrainHeightfield([.. Enumerable.Repeat(9f, 64)], 1) },       // above SizeY
            ok with { Source = new TerrainHeightfield(new float[63], 1) },
            ok with { Source = new TerrainHeightfield(new float[64], 2) },            // fill is air
            ok with { Source = new TerrainHeightfield(new float[64], 1, Surface: 9) },
            ok with { Persistence = (TerrainPersistence)7 },
        ];
        for (var i = 0; i < bad.Length; i++)
        {
            var e = Record.Exception(() => TerrainPlan.Lower(bad[i]));
            Assert.True(e is ArgumentException, $"case {i} did not throw ArgumentException: {e?.GetType().Name ?? "nothing"}");
        }
        // 2^20 cells is the most: 128 x 64 x 128 passes, 128 x 65 x 128 does not
        TerrainPlan.Lower(Solid("big", 1, new CellBox(0, 0, 0, 128, 64, 128)));
        Assert.Throws<ArgumentException>(() => TerrainPlan.Lower(Solid("big", 1, new CellBox(0, 0, 0, 128, 65, 128))));
        Assert.Throws<ArgumentException>(() => TerrainPlan.BatchEdge(511));
        Assert.Throws<ArgumentException>(() => TerrainPlan.BatchEdge(262145));
    }

    [Fact]
    public void A_region_outside_the_grid_throws_once_the_grid_is_known()
    {
        var run = new Run(new MemoryWorld());
        var e = Record.Exception(() => run.Apply(Solid("edge", 1, new CellBox(20476, 96, 64, 8, 8, 8))));
        Assert.IsType<ArgumentException>(e);
        Assert.Empty(run.World.Writes);
    }

    [Fact]
    public void A_heightfield_lowers_to_columns_with_a_surface_and_a_partly_filled_top()
    {
        var region = new CellBox(64, 100, 64, 2, 4, 1);
        var patch = new TerrainPatch("mound", 1, region, new TerrainHeightfield([2.5f, 0f], Fill: 1, Surface: 3, SurfaceDepth: 1), Palette);
        var plan = TerrainPlan.Lower(patch);
        Voxel At(int x, int y) => plan.Target[region.Index(x, y, 0)];
        Assert.Equal(Voxel.Of(12), At(0, 0));
        Assert.Equal(Voxel.Of(12), At(0, 1));
        Assert.Equal(Voxel.Of(40, 64), At(0, 2));           // half of the surface entry's fill 128, surface material
        Assert.Equal(Voxel.Air, At(0, 3));
        Assert.Equal(Voxel.Air, At(1, 0));                  // height 0: the column is dug out
        Assert.Equal(8, plan.AddressedCells);               // replace addresses every cell

        var add = TerrainPlan.Lower(patch with { Source = new TerrainHeightfield([2.5f, 0f], 1, TerrainHeightMode.AddOnTop) });
        Assert.Equal(3, add.AddressedCells);                // adding never addresses the air above
        Assert.True(add.OnlyAdd);
    }

    [Fact]
    public void Adding_on_top_never_digs()
    {
        var world = new MemoryWorld();
        var region = new CellBox(64, 96, 64, 1, 8, 1);      // ground is solid below y 100
        world.Set(64, 101, 64, Voxel.Of(7));                // a rock a player left above the surface
        var run = new Run(world);
        var r = run.Apply(new TerrainPatch("add", 1, region, new TerrainHeightfield([6f], 1, TerrainHeightMode.AddOnTop), Palette));
        Assert.Equal(TerrainState.Completed, r.State);
        Assert.Equal(1, r.ChangedCells);                    // only y 100 was air below the height
        Assert.Equal(Voxel.Of(1), world.Get(64, 99, 64));   // the ground stayed the ground's material
        Assert.Equal(Voxel.Of(12), world.Get(64, 100, 64));
        Assert.Equal(Voxel.Of(7), world.Get(64, 101, 64));  // solid already: left alone
    }

    [Fact]
    public void The_same_patch_lowers_to_the_same_content_and_json_reads_back_equal()
    {
        var box = new CellBox(64, 96, 64, 8, 8, 8);
        var a = TerrainPlan.Lower(Solid("vein", 1, box));
        var b = TerrainPlan.Lower(Solid("other-name", 5, box));
        Assert.Equal(a.ContentHash, b.ContentHash);         // id and version are not content
        Assert.NotEqual(a.ContentHash, TerrainPlan.Lower(Solid("vein", 1, box, 3)).ContentHash);
        Assert.NotEqual(a.ContentHash, TerrainPlan.Lower(Solid("vein", 1, box.Offset(new CellPos(8, 0, 0)))).ContentHash);

        foreach (var patch in new[]
                 {
                     Solid("vein", 3, box, keep: TerrainPersistence.Transient),
                     new TerrainPatch("mound", 1, new CellBox(64, 100, 64, 2, 4, 1), new TerrainHeightfield([2.5f, 0f], 1, TerrainHeightMode.AddOnTop, 3, 1), Palette),
                 })
        {
            var back = TerrainPatch.FromJson(patch.ToJson());
            var (x, y) = (TerrainPlan.Lower(patch), TerrainPlan.Lower(back));
            Assert.Equal((patch.Id, patch.Version, patch.Region, patch.Persistence), (back.Id, back.Version, back.Region, back.Persistence));
            Assert.Equal(x.ContentHash, y.ContentHash);
            Assert.Equal(x.Target, y.Target);
            Assert.Equal(x.Addressed, y.Addressed);
        }
    }

    [Fact]
    public void Json_that_is_not_a_patch_throws()
    {
        const string good = """{ "id": "v", "version": 1, "region": { "x": 64, "y": 96, "z": 64, "sizeX": 1, "sizeY": 1, "sizeZ": 1 }, "palette": [ { "material": 12 } ], "source": { "kind": "voxelVolume", "cells": "AQ==" } }""";
        Assert.Equal(1, TerrainPlan.Lower(TerrainPatch.FromJson(good)).AddressedCells);
        string[] bad =
        [
            "", "[]", "{", good.Replace("\"version\": 1", "\"version\": \"1\""), good.Replace("\"id\": \"v\", ", ""),
            good.Replace("voxelVolume", "signedDistance"), good.Replace("\"cells\": \"AQ==\"", "\"cells\": \"*\""),
            good.Replace("\"sizeZ\": 1", "\"sizeZ\": 1, \"sizeW\": 1"), good.Replace("\"material\": 12", "\"material\": 300"),
            good.Replace("\"version\": 1,", "\"version\": 1, \"persistance\": \"kept\","),
        ];
        foreach (var text in bad) Assert.Throws<ArgumentException>(() => TerrainPatch.FromJson(text));
    }

    // ---- batches and budget ----

    [Fact]
    public void Batches_run_in_a_fixed_order_one_per_tick_and_never_over_the_budget()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var region = new CellBox(60, 90, 60, 40, 20, 40);   // not on the 8-grid on purpose
        var progress = new List<TerrainProgress>();
        var r = run.Apply(Solid("hill", 1, region), new TerrainApplyOptions { CellsPerTick = 4096, Progress = new Collect(progress) });
        Assert.Equal(TerrainState.Completed, r.State);
        Assert.True(r.Ok);
        var boxes = world.Writes.Select(w => w.Box).ToList();
        Assert.Equal(TerrainPlan.Lower(Solid("hill", 1, region)).Batches(16), boxes);
        Assert.Equal(boxes.OrderBy(b => b.Z).ThenBy(b => b.Y).ThenBy(b => b.X).ToList(), boxes);
        Assert.Equal(region.Count, boxes.Sum(b => b.Count));
        Assert.All(world.Writes, w => Assert.InRange(w.Masked, 1, 4096));
        Assert.All(boxes, b => Assert.True(b.X / 16 == (b.X + b.SizeX - 1) / 16 && b.Y / 16 == (b.Y + b.SizeY - 1) / 16 && b.Z / 16 == (b.Z + b.SizeZ - 1) / 16));
        Assert.Equal(Enumerable.Range(0, boxes.Count).ToList(), world.Writes.Select(w => w.Tick).ToList());   // one write a tick
        Assert.Equal(r.Batches, boxes.Count);
        Assert.Equal(region.Count, r.ChangedCells);
        Assert.Equal(boxes.Count, progress.Count);
        Assert.Equal(new TerrainProgress(boxes.Count, boxes.Count, region.Count), progress[^1]);
        // the same patch on a fresh world: the same writes
        var again = new MemoryWorld();
        new Run(again).Apply(Solid("hill", 1, region), new TerrainApplyOptions { CellsPerTick = 4096 });
        Assert.Equal(boxes, again.Writes.Select(w => w.Box).ToList());
    }

    private sealed class Collect(List<TerrainProgress> into) : IProgress<TerrainProgress>
    {
        public void Report(TerrainProgress value) => into.Add(value);
    }

    [Fact]
    public void A_cancel_after_n_batches_reports_n_and_a_second_call_finishes_the_rest()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("hill", 1, new CellBox(64, 96, 64, 32, 8, 8));   // four batches of 8 x 8 x 8 at 512 a tick
        using var cancel = new CancellationTokenSource();
        world.OnTick = () => { if (world.Ticks == 2) cancel.Cancel(); };
        var r = run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512, Cancel = cancel.Token });
        Assert.Equal((TerrainState.Partial, false, 2, "canceled"), (r.State, r.Ok, r.Batches, r.Reason));
        Assert.Equal(2 * 512, r.ChangedCells);
        Assert.Equal(2, run.Events.Count);
        Assert.False(Assert.Single(run.Engine.Applied()).Complete);

        world.OnTick = null;
        var rest = run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512 });
        Assert.Equal((TerrainState.Completed, 2), (rest.State, rest.Batches));   // the two done batches were not written again
        Assert.Equal(2 * 512, rest.ChangedCells);
        Assert.True(Assert.Single(run.Engine.Applied()).Complete);
    }

    [Fact]
    public void Tiles_that_do_not_load_and_a_refused_write_stop_the_patch_as_partial()
    {
        var world = new MemoryWorld { Loaded = false };
        var run = new Run(world);
        var patch = Solid("hill", 1, new CellBox(64, 96, 64, 16, 8, 8));
        var r = run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512 });
        Assert.Equal((TerrainState.Partial, 0), (r.State, r.Batches));
        Assert.Contains("did not load", r.Reason);
        Assert.Empty(world.Writes);

        world.Loaded = true;
        world.RefuseWrite = b => b.X == 72 ? BuildRefusal.Canceled : BuildRefusal.None;
        r = run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512 });
        Assert.Equal((TerrainState.Partial, 1), (r.State, r.Batches));
        Assert.Contains("refused", r.Reason);
        Assert.Single(run.Events);
    }

    // ---- the ledger ----

    [Fact]
    public void A_second_apply_of_the_same_version_reads_and_writes_nothing()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("vein", 1, new CellBox(64, 96, 64, 8, 8, 8));
        Assert.Equal(TerrainState.Completed, run.Apply(patch).State);
        var writes = world.Writes.Count;
        world.Loaded = false;                               // a read would now fail: the no-op must not need one
        var again = run.Apply(patch);
        Assert.Equal((TerrainState.AlreadyApplied, true, 0L, 0), (again.State, again.Ok, again.ChangedCells, again.Batches));
        Assert.Equal(writes, world.Writes.Count);
        Assert.Single(run.Events);
    }

    [Fact]
    public void Other_content_under_the_same_version_and_a_downgrade_are_refused()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var box = new CellBox(64, 96, 64, 8, 8, 8);
        run.Apply(Solid("vein", 2, box));
        var writes = world.Writes.Count;
        var other = run.Apply(Solid("vein", 2, box, 3));
        Assert.Equal((TerrainState.Refused, false), (other.State, other.Ok));
        Assert.Contains("without a new version", other.Reason);
        var down = run.Apply(Solid("vein", 1, box));
        Assert.Equal(TerrainState.Refused, down.State);
        Assert.Contains("downgrade", down.Reason);
        Assert.Equal(writes, world.Writes.Count);
        Assert.Equal(2, Assert.Single(run.Engine.Applied()).Version);
    }

    [Fact]
    public void An_upgrade_writes_only_the_blocks_nobody_changed()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var box = new CellBox(64, 96, 64, 16, 8, 8);        // two blocks
        run.Apply(Solid("vein", 1, box));
        world.Set(66, 98, 66, Voxel.Air);                   // a player digs one cell of the first block
        run.Events.Clear();

        var seen = run.Inspect(Solid("vein", 2, box, 3));
        Assert.Equal((true, TerrainState.Conflict), (seen.Ok, seen.Would));
        Assert.Equal(new TerrainConflict(TerrainConflictKind.PlayerEdit, new CellBox(64, 96, 64, 8, 8, 8)), Assert.Single(seen.Conflicts));
        Assert.Equal(Voxel.Of(12), world.Get(73, 98, 66));  // inspect wrote nothing

        var up = run.Apply(Solid("vein", 2, box, 3));
        Assert.Equal((TerrainState.Conflict, false), (up.State, up.Ok));
        Assert.Equal(new TerrainConflict(TerrainConflictKind.PlayerEdit, new CellBox(64, 96, 64, 8, 8, 8)), Assert.Single(up.Conflicts));
        Assert.Equal(512, up.ChangedCells);
        Assert.Equal(Voxel.Air, world.Get(66, 98, 66));     // the player's cell
        Assert.Equal(Voxel.Of(12), world.Get(65, 98, 66));  // and the rest of that block: version 1, untouched
        Assert.Equal(Voxel.Of(40, 128), world.Get(73, 98, 66));
        Assert.Single(run.Events);

        // the plan gave the block up: asking again says so and writes nothing; version 3 leaves it alone too
        var writes = world.Writes.Count;
        var again = run.Apply(Solid("vein", 2, box, 3));
        Assert.Equal((TerrainState.Conflict, 0), (again.State, again.Batches));
        Assert.Single(again.Conflicts);
        Assert.Equal(writes, world.Writes.Count);
        var info = Assert.Single(run.Engine.Applied());
        Assert.Equal((2, 1, 1), (info.Version, info.Blocks, info.GivenUp));
        var three = run.Apply(Solid("vein", 3, box, 1));
        Assert.Equal(TerrainState.Conflict, three.State);
        Assert.Equal(Voxel.Of(12), world.Get(65, 98, 66));
        Assert.Equal(Voxel.Of(12), world.Get(73, 98, 66));  // the clean block followed to version 3
    }

    [Fact]
    public void An_upgrade_that_grows_writes_the_new_blocks_and_one_that_shrinks_lets_go_of_the_old()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        run.Apply(Solid("vein", 1, new CellBox(64, 96, 64, 8, 8, 8)));
        var grown = run.Apply(Solid("vein", 2, new CellBox(64, 96, 64, 16, 8, 8), 3));
        Assert.Equal((TerrainState.Completed, 1024L), (grown.State, grown.ChangedCells));
        Assert.Equal(2, Assert.Single(run.Engine.Applied()).Blocks);
        var shrunk = run.Apply(Solid("vein", 3, new CellBox(72, 96, 64, 8, 8, 8), 1));
        Assert.Equal(TerrainState.Completed, shrunk.State);
        Assert.Equal(1, Assert.Single(run.Engine.Applied()).Blocks);
        // the block version 3 no longer addresses is free: another mod may take it
        var other = new Run(world, "mod-b", run.Session);
        Assert.Equal(TerrainState.Completed, other.Apply(Solid("b", 1, new CellBox(64, 96, 64, 8, 8, 8), 3)).State);
    }

    [Fact]
    public void An_interrupted_apply_resumes_by_the_fingerprints()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("hill", 1, new CellBox(64, 96, 64, 24, 8, 8));   // three blocks, one a tick
        using var cancel = new CancellationTokenSource();
        world.OnTick = () => { if (world.Ticks == 2) cancel.Cancel(); };
        run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512, Cancel = cancel.Token });
        world.OnTick = null;
        world.Set(64, 97, 64, Voxel.Of(5));                 // someone changes a block that was written already
        var writes = world.Writes.Count;
        var r = run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512 });
        Assert.Equal(TerrainState.Conflict, r.State);
        Assert.Equal(new CellBox(64, 96, 64, 8, 8, 8), Assert.Single(r.Conflicts).Block);
        Assert.Equal(Voxel.Of(5), world.Get(64, 97, 64));
        Assert.Equal(writes + 1, world.Writes.Count);       // only the third block was still to write
        Assert.Equal(new CellBox(80, 96, 64, 8, 8, 8), world.Writes[^1].Box);
    }

    [Fact]
    public void Another_mods_plan_on_the_same_block_is_refused_by_name_and_nothing_is_written()
    {
        var world = new MemoryWorld();
        var a = new Run(world);
        a.Apply(Solid("vein", 1, new CellBox(64, 96, 64, 8, 8, 8)));
        var b = new Run(world, "mod-b", a.Session);
        var writes = world.Writes.Count;
        var r = b.Apply(Solid("mound", 1, new CellBox(70, 98, 70, 8, 8, 8), 3));   // touches a's block and seven others
        Assert.Equal((TerrainState.Refused, false), (r.State, r.Ok));
        Assert.Equal(new TerrainConflict(TerrainConflictKind.OtherPlan, new CellBox(64, 96, 64, 8, 8, 8), "mod-a/vein"), Assert.Single(r.Conflicts));
        Assert.Equal(writes, world.Writes.Count);
        Assert.Empty(b.Events);
        Assert.Empty(b.Engine.Applied());
        var seen = b.Inspect(Solid("mound", 1, new CellBox(70, 98, 70, 8, 8, 8), 3));
        Assert.Equal(TerrainState.Refused, seen.Would);
        Assert.Equal("mod-a/vein", Assert.Single(seen.Conflicts).Owner);

        // once a forgets the plan the block is nobody's; the cells a wrote stay
        Assert.True(a.Engine.Forget("vein"));
        Assert.False(a.Engine.Forget("vein"));
        Assert.Equal(Voxel.Of(12), world.Get(64, 96, 64));
        Assert.Equal(TerrainState.Completed, b.Apply(Solid("mound", 1, new CellBox(70, 98, 70, 8, 8, 8), 3)).State);
    }

    [Fact]
    public void A_plan_that_is_still_being_applied_holds_its_blocks_against_another_mod()
    {
        var world = new MemoryWorld();
        var a = new Run(world);
        var b = new Run(world, "mod-b", a.Session);
        TerrainResult? fromB = null;
        // at a's first tick boundary, b tries the block a has not reached yet
        world.OnTick = () =>
        {
            if (fromB is not null) return;
            world.OnTick = null;
            fromB = b.Apply(Solid("late", 1, new CellBox(72, 96, 64, 8, 8, 8), 3));
        };
        var r = a.Apply(Solid("hill", 1, new CellBox(64, 96, 64, 16, 8, 8)), new TerrainApplyOptions { CellsPerTick = 512 });
        Assert.Equal(TerrainState.Completed, r.State);
        Assert.Equal(TerrainState.Refused, fromB!.State);
        Assert.Equal("mod-a/hill", Assert.Single(fromB.Conflicts).Owner);
        Assert.Equal(Voxel.Of(12), world.Get(72, 96, 64));
    }

    [Fact]
    public void A_block_in_a_base_is_left_alone_unless_bases_are_allowed()
    {
        var world = new MemoryWorld();
        world.Bases.Add(new CellBox(66, 90, 60, 4, 20, 20));    // inside the first of two blocks
        var run = new Run(world);
        var box = new CellBox(64, 96, 64, 16, 8, 8);
        var seen = run.Inspect(Solid("vein", 1, box));
        Assert.Equal(TerrainState.Conflict, seen.Would);
        Assert.Equal(new TerrainConflict(TerrainConflictKind.BaseZone, new CellBox(64, 96, 64, 8, 8, 8)), Assert.Single(seen.Conflicts));
        Assert.All(world.Writes, w => Assert.True(w.DryRun));
        Assert.Equal(Voxel.Of(1), world.Get(73, 98, 66));

        var r = run.Apply(Solid("vein", 1, box));
        Assert.Equal(TerrainState.Conflict, r.State);
        Assert.Equal(new TerrainConflict(TerrainConflictKind.BaseZone, new CellBox(64, 96, 64, 8, 8, 8)), Assert.Single(r.Conflicts));
        Assert.Equal(512, r.ChangedCells);
        Assert.Equal(Voxel.Of(1), world.Get(65, 98, 66));       // the base's block: the ground as it was
        Assert.Equal(Voxel.Of(12), world.Get(73, 98, 66));
        var again = run.Apply(Solid("vein", 1, box));
        Assert.Equal(TerrainConflictKind.BaseZone, Assert.Single(again.Conflicts).Kind);

        var free = new Run(new MemoryWorld { Bases = { new CellBox(66, 90, 60, 4, 20, 20) } });
        var all = free.Apply(Solid("vein", 1, box), new TerrainApplyOptions { InBases = true });
        Assert.Equal((TerrainState.Completed, 1024L), (all.State, all.ChangedCells));
    }

    [Fact]
    public void A_ledger_belongs_to_its_world()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("vein", 1, new CellBox(64, 96, 64, 8, 8, 8));
        run.Apply(patch);
        world.WorldId = "world-b";
        world.Edits.Clear();                                // another world: untouched ground
        Assert.Empty(run.Engine.Applied());
        var r = run.Apply(patch);
        Assert.Equal((TerrainState.Completed, 512L), (r.State, r.ChangedCells));
        // and world b's plans do not hold blocks in world a
        world.WorldId = "world-a";
        Assert.Single(run.Engine.Applied());
    }

    [Fact]
    public void A_transient_patch_is_not_recorded_and_may_run_again()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("pit", 1, new CellBox(64, 96, 64, 8, 8, 8), 2, TerrainPersistence.Transient);
        Assert.Equal(TerrainState.Completed, run.Apply(patch).State);
        Assert.All(world.Writes, w => Assert.True(w.Transient));
        Assert.Empty(run.Engine.Applied());
        world.Edits.Clear();                                // the restart brought the ground back
        var again = run.Apply(patch);
        Assert.Equal((TerrainState.Completed, 4 * 64L), (again.State, again.ChangedCells));   // y 96..99 were ground
    }

    [Fact]
    public void The_plan_limit_per_mod_and_world_is_64()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        for (var i = 0; i < 64; i++)
            Assert.Equal(TerrainState.Completed, run.Apply(Solid($"p{i}", 1, new CellBox(64 + i * 8, 96, 64, 8, 8, 8))).State);
        var r = run.Apply(Solid("one-more", 1, new CellBox(64, 96, 128, 8, 8, 8)));
        Assert.Equal(TerrainState.Refused, r.State);
        Assert.Contains("64", r.Reason);
        Assert.Equal(TerrainState.AlreadyApplied, run.Apply(Solid("p3", 1, new CellBox(64 + 24, 96, 64, 8, 8, 8))).State);
    }

    // ---- events ----

    [Fact]
    public void One_event_per_written_batch_and_none_for_a_refusal()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("hill", 4, new CellBox(64, 96, 64, 24, 8, 8));
        run.Apply(patch, new TerrainApplyOptions { CellsPerTick = 512 });
        Assert.Equal(3, run.Events.Count);
        for (var i = 0; i < 3; i++)
        {
            var e = run.Events[i];
            Assert.Equal(("mod-a", "hill", 4, i + 1, 3, i == 2), (e.Mod, e.Plan, e.Version, e.Batch, e.BatchCount, e.Last));
            Assert.Equal(new CellBox(64 + 8 * i, 96, 64, 8, 8, 8), e.Box);
            Assert.Equal(512, e.CellsChanged);
        }
        run.Apply(patch);                                   // already applied
        run.Apply(patch with { Version = 3 });              // refused
        Assert.Equal(3, run.Events.Count);
    }

    [Fact]
    public void Inspect_reports_size_and_writes_nothing()
    {
        var world = new MemoryWorld();
        var run = new Run(world);
        var patch = Solid("hill", 1, new CellBox(60, 96, 64, 12, 8, 8));   // 60..71: two blocks, 4 + 8 wide
        var r = run.Inspect(patch, new TerrainApplyOptions { CellsPerTick = 512, InBases = true });
        Assert.Equal((true, null, TerrainState.Completed), (r.Ok, r.Reason, r.Would));
        Assert.Equal((patch.Region, 768L, 2, 2, 2048L), (r.Box, r.Cells, r.Blocks, r.Batches, r.EstimatedBytes));
        Assert.Empty(world.Writes);
        Assert.Empty(world.Edits);
        Assert.Empty(run.Engine.Applied());

        world.Running = false;
        var off = run.Inspect(patch);
        Assert.Equal((false, TerrainState.Refused), (off.Ok, off.Would));
        world.Running = true;
        world.Loaded = false;
        Assert.False(run.Inspect(patch).Ok);
    }
}

/// <summary>The public face on the test kit's harness: rows, the error rule's order, the tick thread.</summary>
public sealed class FacadeTests
{
    private static TerrainPatch Vein => new("vein", 1, new CellBox(64, 96, 64, 1, 1, 1), new TerrainVoxelVolume([1]), [new TerrainPaletteEntry(12)]);

    [Fact]
    public void Without_kiln_build_the_overlay_members_throw_naming_their_row()
    {
        using var kiln = new Kiln.TestKit.KilnHarness();
        kiln.Step(2);
        Assert.True(kiln.Run(() => Capabilities.Is("kiln.terrain")));
        Assert.False(kiln.Run(() => Capabilities.Is("terrain.overlays")));
        Assert.False(kiln.Run(() => Capabilities.Is("terrain.baked")));
        Assert.False(kiln.Run(() => Capabilities.Is("terrain.biomes")));
        Assert.Equal("terrain.overlays", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => (object)Terrain.Apply(Vein))).Capability);
        Assert.Equal("terrain.overlays", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Terrain.Forget("vein"))).Capability);
        Assert.Equal("terrain.overlays", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Terrain.Applied)).Capability);
        // a bad argument is a bad argument whatever the rows say
        Assert.Throws<ArgumentException>(() => kiln.Run(() => (object)Terrain.Apply(Vein with { Version = 0 })));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => (object)Terrain.Apply(Vein, new TerrainApplyOptions { CellsPerTick = 1 })));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Terrain.Forget("No Such Id")));
    }
}
