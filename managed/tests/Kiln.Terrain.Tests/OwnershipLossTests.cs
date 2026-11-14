// SPDX-License-Identifier: MIT
// Ownership-loss detection (TerrainLoss, docs/kiln/services/terrain.md "When the ledger is gone"): a kept plan with no
// ledger record over ground that already holds its result is refused with a reason and writes nothing; ground that
// holds none of it, or only what the ground had anyway, is a first install as before; a plan with a record is never
// checked. The loss is made in the in-memory world as it happens in a game: the mod's book (the ledger key) is gone,
// and for a lost sidecar Kiln.Saves reports no data for the world (DataMatch.None); the cells stay.
using Kiln;
using Xunit;

namespace TerrainTests;

public sealed class OwnershipLossTests
{
    private const byte Ore = 12, Stone = 1;   // MemoryWorld's ground is stone below y 100, air from it up
    private const string Mod = "mod-a";

    /// <summary>The engine, which runs the ownership-loss check itself (Apply: before a kept plan's first record is made;
    /// Inspect: at the end of its kept-plan checks).</summary>
    private sealed class Checked(MemoryWorld world)
    {
        public readonly MemoryWorld World = world;
        public readonly TerrainSession Session = new();
        public readonly List<TerrainChangedEvent> Events = [];

        public TerrainResult Apply(TerrainPatch patch, TerrainApplyOptions? options = null)
        {
            var o = options ?? new TerrainApplyOptions();
            return Done(new TerrainEngine(World.For(Mod), Session, Events.Add).Apply(TerrainPlan.Lower(patch), o, TerrainPlan.BatchEdge(o.CellsPerTick)));
        }

        public TerrainPlanReport Inspect(TerrainPatch patch)
        {
            var o = new TerrainApplyOptions();
            return Done(new TerrainEngine(World.For(Mod), Session, null).Inspect(TerrainPlan.Lower(patch), o, TerrainPlan.BatchEdge(o.CellsPerTick)));
        }

        public TerrainEngine Engine => new(World.For(Mod), Session, null);

        private static T Done<T>(Task<T> t)
        {
            Assert.True(t.IsCompleted, "the in-memory world never waits");
            return t.GetAwaiter().GetResult();
        }
    }

    /// <summary>The check by itself, as the engine calls it.</summary>
    private static (TerrainState State, string Reason)? CheckNow(MemoryWorld world, TerrainPatch patch)
    {
        var backend = world.For(Mod);
        var t = TerrainLoss.Check(backend, TerrainPlan.Lower(patch), backend.Book(), TimeSpan.FromSeconds(1));
        Assert.True(t.IsCompleted, "the in-memory world never waits");
        return t.GetAwaiter().GetResult();
    }

    /// <summary>The engine alone (what the first install does without the check).</summary>
    private static TerrainResult Plain(MemoryWorld world, TerrainPatch patch)
    {
        var o = new TerrainApplyOptions();
        return new TerrainEngine(world.For(Mod), new TerrainSession(), null).Apply(TerrainPlan.Lower(patch), o, TerrainPlan.BatchEdge(o.CellsPerTick)).GetAwaiter().GetResult();
    }

    private static readonly CellBox Two = new(64, 96, 64, 16, 8, 8);   // two blocks, half in the ground

    /// <summary>The plan shapes a mod writes: an ore vein inside its region, a whole box of ore, a replacing heightfield
    /// with air above it, and a mound added on top of the ground (the OreVein sample's mound).</summary>
    private static TerrainPatch Shape(string name, int version = 1, byte material = Ore)
    {
        switch (name)
        {
            case "vein":
            {
                var cells = new byte[Two.Count];
                for (var z = 0; z < Two.SizeZ; z++)
                for (var y = 0; y < Two.SizeY; y++)
                for (var x = 0; x < Two.SizeX; x++)
                    if ((x + y + z) % 3 == 0) cells[Two.Index(x, y, z)] = 1;   // ore cells, each with ground around it
                return new TerrainPatch("vein", version, Two, new TerrainVoxelVolume(cells), [new TerrainPaletteEntry(material)]);
            }
            case "box":
            {
                var cells = new byte[Two.Count];
                Array.Fill(cells, (byte)1);
                return new TerrainPatch("box", version, Two, new TerrainVoxelVolume(cells), [new TerrainPaletteEntry(material)]);
            }
            case "replace":
            {
                var region = new CellBox(64, 96, 64, 16, 16, 16);   // eight blocks; the upper four stay air
                return new TerrainPatch("terrace", version, region, new TerrainHeightfield([.. Enumerable.Repeat(6.5f, 256)], 1),
                    [new TerrainPaletteEntry(material)]);
            }
            case "mound":
            {
                var heights = new float[16 * 16];
                for (var z = 0; z < 16; z++)
                for (var x = 0; x < 16; x++)
                    heights[z * 16 + x] = MathF.Max(0, 6f - MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (z - 7.5f) * (z - 7.5f)));
                return new TerrainPatch("mound", version, new CellBox(64, 99, 64, 16, 6, 16),
                    new TerrainHeightfield(heights, 1, TerrainHeightMode.AddOnTop), [new TerrainPaletteEntry(material)]);
            }
            default: throw new ArgumentException(name);
        }
    }

    /// <summary>What a lost ledger looks like: the mod's book is gone; a lost sidecar also leaves the world without data.</summary>
    private static void Lose(MemoryWorld world, bool wholeSidecar)
    {
        Assert.True(world.Books.Remove(world.WorldId + "|" + Mod));
        if (wholeSidecar) world.Match = DataMatch.None;
    }

    /// <summary>Every cell of the region and two around it, and the edit store itself.</summary>
    private static (Voxel[] Cells, List<KeyValuePair<(int, int, int), Voxel>> Edits) Snapshot(MemoryWorld world, CellBox region)
    {
        var around = new CellBox(region.X - 2, region.Y - 2, region.Z - 2, region.SizeX + 4, region.SizeY + 4, region.SizeZ + 4);
        world.For("reader").TryRead(around, out var cells);
        return (cells, world.Edits.OrderBy(e => e.Key).ToList());
    }

    private static void Unchanged((Voxel[] Cells, List<KeyValuePair<(int, int, int), Voxel>> Edits) before, MemoryWorld world, CellBox region)
    {
        var after = Snapshot(world, region);
        Assert.True(before.Cells.AsSpan().SequenceEqual(after.Cells), "a cell changed");
        Assert.Equal(before.Edits, after.Edits);
    }

    // ---- ledger lost, the ground still holds the plan ----

    [Theory]
    [InlineData("vein", true)]
    [InlineData("vein", false)]
    [InlineData("box", true)]
    [InlineData("box", false)]
    [InlineData("replace", true)]
    [InlineData("mound", true)]
    [InlineData("mound", false)]
    public void A_lost_ledger_over_ground_that_still_holds_the_plan_is_refused_and_nothing_is_written(string shape, bool wholeSidecar)
    {
        var world = new MemoryWorld();
        var patch = Shape(shape);
        Assert.Equal(TerrainState.Completed, new Checked(world).Apply(patch).State);
        Lose(world, wholeSidecar);
        var before = Snapshot(world, patch.Region);
        var writes = world.Writes.Count;

        var run = new Checked(world);                       // a new session: the restart after the loss
        var r = run.Apply(patch);
        Assert.Equal((false, TerrainState.Refused, 0L, 0), (r.Ok, r.State, r.ChangedCells, r.Batches));
        Assert.StartsWith("ownership lost: the ground already holds plan '" + patch.Id + "'", r.Reason);
        Assert.Contains(wholeSidecar ? "this world has no Kiln data that could be read" : "this mod's terrain ledger has no record of it", r.Reason);
        Unchanged(before, world, patch.Region);
        Assert.Equal(writes, world.Writes.Count);           // no write at all, not even a dry run
        Assert.Empty(run.Events);
        Assert.Empty(run.Engine.Applied());                 // nothing recorded: asking again is refused again
        Assert.Equal(TerrainState.Refused, run.Apply(patch).State);
        Unchanged(before, world, patch.Region);
    }

    [Fact]
    public void A_block_a_player_changed_is_kept_when_another_block_still_shows_the_plan()
    {
        var world = new MemoryWorld();
        var patch = Shape("vein");
        new Checked(world).Apply(patch);
        world.Set(64, 96, 64, Voxel.Air);                   // a player digs an ore cell of the first block
        world.Set(65, 97, 64, Voxel.Of(7));                 // and puts a rock next to it
        Lose(world, wholeSidecar: true);
        var before = Snapshot(world, patch.Region);

        var run = new Checked(world);
        var r = run.Apply(patch);
        Assert.Equal(TerrainState.Refused, r.State);
        Assert.Contains("in 1 block(s) (first 72,96,64)", r.Reason);   // the untouched block is the evidence
        Assert.Equal(Voxel.Air, world.Get(64, 96, 64));
        Assert.Equal(Voxel.Of(7), world.Get(65, 97, 64));
        Unchanged(before, world, patch.Region);
        Assert.Empty(run.Events);
    }

    [Fact]
    public void Inspect_says_that_an_apply_would_be_refused_and_probes_nothing()
    {
        var world = new MemoryWorld();
        var patch = Shape("vein");
        new Checked(world).Apply(patch);
        Lose(world, wholeSidecar: false);
        var writes = world.Writes.Count;
        var seen = new Checked(world).Inspect(patch);
        Assert.Equal((true, TerrainState.Refused, 2), (seen.Ok, seen.Would, seen.Blocks));
        Assert.StartsWith("ownership lost:", seen.Reason);
        Assert.Equal(writes, world.Writes.Count);
    }

    [Fact]
    public void After_Forget_ground_that_still_holds_the_plan_is_refused_too()
    {
        var world = new MemoryWorld();
        var patch = Shape("box");
        var run = new Checked(world);
        run.Apply(patch);
        world.Set(64, 96, 64, Voxel.Air);                   // a player's dig, not to be filled in again
        Assert.True(run.Engine.Forget("box"));
        var r = run.Apply(patch);
        Assert.Equal(TerrainState.Refused, r.State);
        Assert.Contains("or the plan was forgotten", r.Reason);
        Assert.Equal(Voxel.Air, world.Get(64, 96, 64));
    }

    [Fact]
    public void A_new_version_after_a_lost_ledger_is_stopped_only_where_it_agrees_with_the_ground()
    {
        var world = new MemoryWorld();
        new Checked(world).Apply(Shape("vein"));
        Lose(world, wholeSidecar: true);

        // version 2 keeps the first block's ore and changes the second block's: the first block shows the plan
        var two = Shape("vein", 2);
        var cells = ((TerrainVoxelVolume)two.Source).Cells.ToArray();
        for (var z = 0; z < Two.SizeZ; z++)
        for (var y = 0; y < Two.SizeY; y++)
        for (var x = 8; x < 16; x++)
            if (cells[Two.Index(x, y, z)] == 1) cells[Two.Index(x, y, z)] = 2;
        var partly = two with { Source = new TerrainVoxelVolume(cells), Palette = [new TerrainPaletteEntry(Ore), new TerrainPaletteEntry(40, 128)] };
        var r = new Checked(world).Apply(partly);
        Assert.Equal(TerrainState.Refused, r.State);
        Assert.Contains("(first 64,96,64)", r.Reason);

        // a version 2 that differs in every block cannot be told from a first install (the limit the docs state)
        var all = new Checked(world).Apply(Shape("vein", 2, 40));
        Assert.Equal(TerrainState.Completed, all.State);
    }

    // ---- ledger lost, the ground holds none of the plan: a first install as before ----

    [Theory]
    [InlineData("vein")]
    [InlineData("box")]
    [InlineData("replace")]
    [InlineData("mound")]
    public void Ground_that_holds_none_of_the_plan_is_a_first_install_as_before(string shape)
    {
        var patch = Shape(shape);
        var plain = new MemoryWorld();
        var expected = Plain(plain, patch);

        foreach (var data in new[] { DataMatch.None, DataMatch.Exact })
        {
            var world = new MemoryWorld { Match = data };
            var run = new Checked(world);
            Assert.Equal((true, TerrainState.Completed), (run.Inspect(patch).Ok, run.Inspect(patch).Would));
            var r = run.Apply(patch);
            Assert.Equal((true, TerrainState.Completed), (r.Ok, r.State));
            Assert.Equal((expected.ChangedCells, expected.Batches), (r.ChangedCells, r.Batches));
            Assert.Equal(plain.Edits.OrderBy(e => e.Key), world.Edits.OrderBy(e => e.Key));
            Assert.Equal(plain.Writes.Count, world.Writes.Count(w => !w.DryRun));   // Inspect's probes are dry runs
            Assert.Equal(r.Batches, run.Events.Count);
        }
    }

    [Fact]
    public void When_every_block_was_changed_since_the_loss_cannot_be_seen_and_the_plan_installs()
    {
        var world = new MemoryWorld();
        var patch = Shape("vein");
        new Checked(world).Apply(patch);
        world.Set(64, 96, 64, Voxel.Air);                   // one dug cell in each block
        world.Set(72, 96, 65, Voxel.Air);
        Lose(world, wholeSidecar: true);
        var r = new Checked(world).Apply(patch);
        Assert.Equal((TerrainState.Completed, 2L), (r.State, r.ChangedCells));   // the docs' limit: both cells are ore again
        Assert.Equal(Voxel.Of(Ore), world.Get(64, 96, 64));
    }

    [Fact]
    public void Blocks_that_only_repeat_the_ground_are_not_taken_for_an_earlier_apply()
    {
        var world = new MemoryWorld();
        var run = new Checked(world);

        // a mound added on top below ground that is higher: every column is full already, nothing is added
        var under = new TerrainPatch("under", 1, new CellBox(64, 88, 64, 16, 6, 16),
            new TerrainHeightfield([.. Enumerable.Repeat(3f, 256)], 1, TerrainHeightMode.AddOnTop), [new TerrainPaletteEntry(Stone)]);
        var u = run.Apply(under);
        Assert.Equal((TerrainState.Completed, 0L), (u.State, u.ChangedCells));

        // a box of the ground's own material whose top meets the surface: the ground goes on beside and below it
        var own = new TerrainPatch("own", 1, new CellBox(64, 92, 64, 8, 8, 8), new TerrainVoxelVolume([.. Enumerable.Repeat((byte)1, 512)]),
            [new TerrainPaletteEntry(Stone)]);
        var r = run.Apply(own);
        Assert.Equal((TerrainState.Completed, 0L), (r.State, r.ChangedCells));

        // a dig in open air above the ground (a tunnel's mouth): air is never evidence
        var mouth = new TerrainPatch("mouth", 1, new CellBox(80, 100, 64, 8, 8, 8), new TerrainVoxelVolume([.. Enumerable.Repeat((byte)1, 512)]),
            [new TerrainPaletteEntry(0)]);
        Assert.Equal(TerrainState.Completed, run.Apply(mouth).State);
        Assert.Equal(3, run.Engine.Applied().Count);
    }

    // ---- a plan with its ledger, transient plans, ground that cannot be read ----

    [Fact]
    public void A_plan_with_its_record_is_never_checked_and_behaves_as_before()
    {
        var world = new MemoryWorld();
        var run = new Checked(world);
        var patch = Shape("vein");
        run.Apply(patch);
        var loads = 0;
        world.OnLoad = () => loads++;
        world.Loaded = false;                               // a read would fail: the check must not need one
        Assert.Null(CheckNow(world, patch));
        Assert.Equal(0, loads);
        Assert.Equal(TerrainState.AlreadyApplied, run.Apply(patch).State);
        world.OnLoad = null;
        world.Loaded = true;

        world.Set(64, 96, 64, Voxel.Air);                   // the ledger's own rule decides an upgrade, as before
        var up = run.Apply(Shape("vein", 2, 40));
        Assert.Equal(TerrainState.Conflict, up.State);
        Assert.Equal(new CellBox(64, 96, 64, 8, 8, 8), Assert.Single(up.Conflicts).Block);
        Assert.Equal(Voxel.Air, world.Get(64, 96, 64));
    }

    [Fact]
    public void A_transient_patch_is_never_checked()
    {
        var world = new MemoryWorld();
        var patch = Shape("box") with { Persistence = TerrainPersistence.Transient };
        var run = new Checked(world);
        run.Apply(patch);
        Assert.Null(CheckNow(world, patch));
        Assert.Equal(TerrainState.Completed, run.Apply(patch).State);
    }

    [Fact]
    public void Ground_that_cannot_be_read_is_not_written()
    {
        var world = new MemoryWorld { Loaded = false };
        var run = new Checked(world);
        var r = run.Apply(Shape("vein"));
        Assert.Equal((false, TerrainState.Partial, 0), (r.Ok, r.State, r.Batches));
        Assert.Contains("did not load", r.Reason);
        Assert.Empty(world.Writes);
        Assert.Empty(run.Engine.Applied());

        var switched = new MemoryWorld();
        switched.OnLoad = () => switched.WorldId = "world-b";
        var s = new Checked(switched).Apply(Shape("vein"));
        Assert.Equal((TerrainState.Partial, "the world stopped or changed"), (s.State, s.Reason));
        Assert.Empty(switched.Writes);

        var thrown = new MemoryWorld { OnLoad = () => throw new BuildException("kiln.build is not ready") };
        var t = new Checked(thrown).Apply(Shape("vein"));
        Assert.Equal((TerrainState.Partial, "kiln.build is not ready"), (t.State, t.Reason));
        Assert.Empty(thrown.Writes);
    }

    [Fact]
    public void The_cells_read_around_a_region_stay_inside_the_grid()
    {
        Assert.Equal(new CellBox(63, 95, 63, 18, 10, 10), TerrainLoss.Around(new CellBox(64, 96, 64, 16, 8, 8), (20480, 20480)));
        Assert.Equal(new CellBox(0, 0, 0, 8, 9, 8), TerrainLoss.Around(new CellBox(0, 0, 0, 8, 8, 8), (8, 8)));          // no x, y or z below 0, none past the grid
        Assert.Equal(new CellBox(9, 8179, 9, 6, 13, 6), TerrainLoss.Around(new CellBox(10, 8180, 10, 4, 12, 4), null)); // none above y 8191
    }

    [Fact]
    public void The_refusal_says_what_was_lost_and_what_an_operator_can_do()
    {
        var blocks = new[] { new CellBox(64, 96, 64, 8, 8, 8), new CellBox(72, 96, 64, 8, 8, 8) };
        Assert.Equal(
            "ownership lost: the ground already holds plan 'vein' in 2 block(s) (first 64,96,64), but this world has no Kiln data that could be read, " +
            "so no terrain ledger records it (the world's Kiln data was lost or is unreadable). Nothing was written, so whatever changed there since " +
            "is kept. To own it again, restore this world's Kiln data from a backup that matches the world save (Saves.RestoreOnNextStart, or a " +
            "kilnctl backup); adopting the ground as it stands is not available in this version",
            TerrainLoss.Reason("vein", blocks, DataMatch.None));
        Assert.Contains("but this mod's terrain ledger has no record of it (the ledger was lost or reset, or the plan was forgotten). Nothing was written",
            TerrainLoss.Reason("vein", blocks, DataMatch.Exact));

        var world = new MemoryWorld();
        new Checked(world).Apply(Shape("vein"));
        Lose(world, wholeSidecar: true);
        Assert.Equal(TerrainLoss.Reason("vein", blocks, DataMatch.None), new Checked(world).Apply(Shape("vein")).Reason);
    }
}
