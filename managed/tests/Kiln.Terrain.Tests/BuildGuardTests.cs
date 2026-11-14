// SPDX-License-Identifier: MIT
// Kiln.Build's guarded write (Build.WriteIfUnchanged) by itself, on the fake kiln.build table: the compare runs after the
// BuildEditingEvent handlers and right before the commit, over the cells the write sets. And Kiln.Terrain's real backend
// (BuildTerrainBackend) handing the overlay's read to it. Kiln.Build has no offline test project of its own.
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace TerrainTests;

[Collection(nameof(BuildGuardTests))]
public sealed class BuildGuardTests
{
    private static readonly CellBox Box = new(64, 104, 64, 16, 8, 8);   // two blocks of air above the fake's ground
    private static readonly CellPos Foreign = new(66, 106, 66);         // a cell of the first block
    private static readonly BuildOptions Overlay = new() { Journal = false, Label = "overlay" };

    private static Voxel[] Fill(Voxel v)
    {
        var cells = new Voxel[Box.Count];
        Array.Fill(cells, v);
        return cells;
    }

    private static KilnHarness Start()
    {
        FakeBuildTable.Use();
        var kiln = new KilnHarness();
        kiln.Step(2);
        FakeBuildTable.Reset();
        return kiln;
    }

    /// <summary>A handler of the guarded write's BuildEditingEvent writes <paramref name="value"/> at <paramref name="at"/>
    /// (its own write posts the event again, under another label, which it leaves alone).</summary>
    private static EventSubscription EditOnce(CellPos at, Voxel value) =>
        Events.On<BuildEditingEvent>(e =>
        {
            if (e.Label != Overlay.Label) return;
            Build.Write(new CellBox(at.X, at.Y, at.Z, 1, 1, 1), [value], new BuildOptions { Journal = false, Label = "foreign" });
        });

    private static bool AllAirExcept(CellBox box, CellPos except)
    {
        for (var z = box.Z; z < box.Z + box.SizeZ; z++)
        for (var y = box.Y; y < box.Y + box.SizeY; y++)
        for (var x = box.X; x < box.X + box.SizeX; x++)
            if (new CellPos(x, y, z) != except && FakeBuildTable.Get(new CellPos(x, y, z)) != Voxel.Air) return false;
        return true;
    }

    [Fact]
    public void A_cell_an_editing_handler_changes_refuses_the_whole_write_and_nothing_of_it_is_written()
    {
        using var kiln = Start();
        var edits = new List<BuildEditEvent>();
        var r = kiln.Run(() =>
        {
            using var editing = EditOnce(Foreign, Voxel.Of(7));
            using var after = Events.On<BuildEditEvent>(edits.Add);
            var before = Build.Read(Box);
            return Build.WriteIfUnchanged(Box, Fill(Voxel.Of(12)), before, Overlay);
        });
        Assert.Equal((false, BuildRefusal.Changed, 0L, false), (r.Ok, r.Refusal, r.CellsChanged, r.Changed));
        Assert.Contains("changed after they were read", r.Reason);
        Assert.Equal(Voxel.Of(7), FakeBuildTable.Get(Foreign));     // the handler's cell
        Assert.True(AllAirExcept(Box, Foreign));                     // and not one cell of the guarded write
        Assert.Equal(1, FakeBuildTable.Commits);                     // only the handler's write reached the writer
        // the handler's write, then the refusal: BuildEditEvent carries it as it carries a refusal by the world
        Assert.Equal(new string?[] { "foreign", "overlay" }, edits.Select(e => e.Label).ToArray());
        Assert.Equal(BuildRefusal.Changed, edits[1].Result!.Refusal);
        Assert.False(edits[1].Changed);
    }

    [Fact]
    public void Without_a_change_it_writes_as_Write_does_and_compares_only_the_cells_it_sets()
    {
        using var kiln = Start();
        var plain = kiln.Run(() => Build.WriteIfUnchanged(Box, Fill(Voxel.Of(12)), Build.Read(Box), Overlay));
        Assert.Equal((true, BuildRefusal.None, 1024L, true), (plain.Ok, plain.Refusal, plain.CellsChanged, plain.Verified));
        Assert.Equal(Voxel.Of(12), FakeBuildTable.Get(Foreign));

        // a mask of the second block only: the handler's cell in the first block is not compared, and stays
        FakeBuildTable.Reset();
        var mask = new bool[Box.Count];
        for (var i = 0; i < mask.Length; i++) mask[i] = i % Box.SizeX >= 8;
        var r = kiln.Run(() =>
        {
            using var editing = EditOnce(Foreign, Voxel.Of(7));
            return Build.WriteIfUnchanged(Box, Fill(Voxel.Of(12)), Build.Read(Box), Overlay, mask);
        });
        Assert.Equal((true, BuildRefusal.None, 512L), (r.Ok, r.Refusal, r.CellsChanged));
        Assert.Equal(Voxel.Of(7), FakeBuildTable.Get(Foreign));
        Assert.Equal(Voxel.Of(12), FakeBuildTable.Get(new CellPos(73, 106, 66)));
    }

    [Fact]
    public void Wrong_sizes_throw_before_any_event_and_missing_tiles_are_not_loaded()
    {
        using var kiln = Start();
        var posted = 0;
        kiln.Run(() =>
        {
            using var editing = Events.On<BuildEditingEvent>(_ => posted++);
            var cells = Fill(Voxel.Of(12));
            Assert.Equal("expected", Assert.Throws<ArgumentException>(() => Build.WriteIfUnchanged(Box, cells, cells.AsSpan(1), Overlay)).ParamName);
            Assert.Equal("cells", Assert.Throws<ArgumentException>(() => Build.WriteIfUnchanged(Box, cells.AsSpan(1), cells, Overlay)).ParamName);
            Assert.Equal("mask", Assert.Throws<ArgumentException>(() => Build.WriteIfUnchanged(Box, cells, cells, Overlay, new bool[3])).ParamName);
            Assert.Throws<ArgumentException>(() => Build.WriteIfUnchanged(default, [], [], Overlay));
        });
        Assert.Equal(0, posted);

        var expected = kiln.Run(() => Build.Read(Box));
        FakeBuildTable.Loaded = false;
        var r = kiln.Run(() => Build.WriteIfUnchanged(Box, Fill(Voxel.Of(12)), expected, Overlay));
        Assert.Equal((false, BuildRefusal.NotLoaded), (r.Ok, r.Refusal));
        Assert.Equal(0, FakeBuildTable.Commits);
    }

    [Fact]
    public async Task The_overlays_backend_hands_the_cells_it_read_to_the_guarded_write()
    {
        using var kiln = Start();
        var mod = kiln.Load<TerrainFixture>(new ModManifest("terrain-guard"));
        kiln.Step(2);
        var backend = new BuildTerrainBackend(mod.Context);
        var expected = kiln.Run(() => Build.Read(Box));
        var mask = new bool[Box.Count];
        Array.Fill(mask, true);

        var guarded = kiln.As(mod, () =>
        {
            using var editing = EditOnce(Foreign, Voxel.Of(7));
            return backend.Write(Box, Fill(Voxel.Of(12)), mask, false, false, "overlay", TimeSpan.FromSeconds(1), expected: expected);
        });
        kiln.StepUntil(() => guarded.IsCompleted, 20);
        Assert.Equal(BuildRefusal.Changed, (await guarded).Refusal);
        Assert.Equal(Voxel.Of(7), FakeBuildTable.Get(Foreign));
        Assert.True(AllAirExcept(Box, Foreign));

        // without expected cells it is the plain write it was before
        var plain = kiln.As(mod, () => backend.Write(Box, Fill(Voxel.Of(12)), mask, false, false, "overlay", TimeSpan.FromSeconds(1)));
        kiln.StepUntil(() => plain.IsCompleted, 20);
        Assert.Equal((BuildRefusal.None, 1024L), ((await plain).Refusal, (await plain).CellsChanged));
    }
}

/// <summary>The fake kiln.build table is one per process: its tests do not run beside each other.</summary>
[CollectionDefinition(nameof(BuildGuardTests), DisableParallelization = true)]
public sealed class BuildGuardCollection;
