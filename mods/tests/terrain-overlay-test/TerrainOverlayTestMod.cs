// SPDX-License-Identifier: MIT
// Kiln.Terrain lab test, player free. Prints "terrain-overlay-test: PASS|FAIL|INFO ..." lines, "READY stop" when the
// driver (run.py) should stop the server, and "DONE" at the end. Three boots (kiln/data/terrain-overlay-test/state.json):
//   1  world A, fresh: inspect, apply a vein (three blocks, one a tick), a transient cube of one full default batch (its
//      timing is the INFO line the docs' "cells per tick" comes from), a patch inside the base (refused), a two-block
//      plan whose one block a BuildEditingEvent handler edits while the overlay writes it (that block is left), a save.
//   2  world A after a restart: the vein is back and recorded, the same version is a no-op, and version 2 over one cell
//      someone dug leaves that block alone. A save.
//   3  world B (the driver moved world A's save away): no record, no overlay cells, and the vein applies there afresh.
// The test area is the plateau kiln.build's own lab test uses (x 3024, z 1548), 790 m from the lab save's only base.
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace TerrainOverlayTest;

public sealed class State
{
    public int Phase { get; set; } = 1;
    public string WorldA { get; set; } = "";
    public int[] Region { get; set; } = [];
    public byte Ground { get; set; }
    public byte One { get; set; }
    public byte Two { get; set; }
}

public sealed class TerrainOverlayTestMod : Mod
{
    private const string Tag = "terrain-overlay-test";
    private State _s = new();
    private int _fail;
    private string StatePath => Path.Combine(Context.DataDirectory, "state.json");

    public override void OnLoad()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        if (File.Exists(StatePath)) _s = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
        _ = Run();
    }

    private void Check(bool ok, string name, string detail = "")
    {
        if (!ok) _fail++;
        var line = $"{Tag}: {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " " + detail : "")}";
        if (ok) Context.Log.Info(line);
        else Context.Log.Warn(line);
    }

    private void Info(string m) => Context.Log.Info($"{Tag}: INFO {m}");

    private async Task Run()
    {
        var phase = _s.Phase;
        try
        {
            for (var i = 0; i < 24 * 300 && !(Spawn.Available && Spawn.WorldRunning && Build.Available && Build.Grid is not null); i++)
                await Context.Scheduler.NextTick();
            for (var i = 0; i < 24 * 5; i++) await Context.Scheduler.NextTick();
            Info($"phase {phase}, world {Saves.WorldId}, terrain.overlays: {Capabilities.Why("terrain.overlays")}");
            switch (phase)
            {
                case 1: await Fresh(); break;
                case 2: await AfterRestart(); break;
                case 3: await OtherWorld(); break;
                default: Info($"phase {phase}: nothing to do (delete {Context.DataDirectory} to start over)"); break;
            }
        }
        catch (Exception e)
        {
            Check(false, $"phase-{phase}-threw", e.ToString());
        }
        File.WriteAllText(StatePath, JsonSerializer.Serialize(_s));
        Context.Log.Info(phase >= 3 ? $"{Tag}: DONE fail={_fail}" : $"{Tag}: READY stop fail={_fail}");
    }

    private CellBox Region => new(_s.Region[0], _s.Region[1], _s.Region[2], _s.Region[3], _s.Region[4], _s.Region[5]);

    private TerrainPatch Vein(int version, byte material)
    {
        var cells = new byte[Region.Count];
        Array.Fill(cells, (byte)1);
        return new TerrainPatch("vein", version, Region, new TerrainVoxelVolume(cells), [new TerrainPaletteEntry(material)]);
    }

    private static readonly TerrainApplyOptions Slow = new() { CellsPerTick = 512 };

    private async Task Fresh()
    {
        // the ground under the plateau, and three blocks just below its surface on the 8-cell grid
        var column = Build.BoxBetween(new WorldPosition(3024, 850, 1548), new WorldPosition(3024, 960, 1548));
        var cells = await Build.ReadAsync(column);
        var top = -1;
        for (var y = column.SizeY - 1; y >= 0 && top < 0; y--)
            if (cells[column.Index(0, y, 0)] is { IsSolid: true, Material: > 0 and < 128 }) top = y;
        if (top < 16) { Check(false, "area", "no ground under the test area"); return; }
        _s.Ground = cells[column.Index(0, top, 0)].Material;
        _s.One = (byte)(_s.Ground == 20 ? 22 : 20);
        _s.Two = (byte)(_s.Ground == 21 ? 23 : 21);
        var y0 = (column.Y + top - 12) / 8 * 8;
        _s.Region = [column.X / 8 * 8, y0, column.Z / 8 * 8, 24, 8, 8];
        _s.WorldA = Saves.WorldId ?? "";
        var region = Region;
        Info($"ground material {_s.Ground}, vein region {region}, materials {_s.One} then {_s.Two}");

        // a lab that ran this test before still has its records (the world's data outlives the driver's reset)
        foreach (var old in Terrain.Applied) Terrain.Forget(old.Plan);

        var seen = await Terrain.Inspect(Vein(1, _s.One), Slow);
        Check(seen is { Ok: true, Would: TerrainState.Completed, Cells: 24 * 8 * 8, Blocks: 3, Batches: 3 } && seen.Conflicts.Count == 0,
            "overlay.inspect", $"cells {seen.Cells} blocks {seen.Blocks} batches {seen.Batches} would {seen.Would} ok {seen.Ok} {seen.Reason}");

        var events = new List<TerrainChangedEvent>();
        void On(TerrainChangedEvent e) => events.Add(e);
        Terrain.Changed += On;
        var r = await Terrain.Apply(Vein(1, _s.One), Slow);
        Terrain.Changed -= On;
        var after = await Build.ReadAsync(region);
        var all = after.All(c => c == Voxel.Of(_s.One));
        Check(r is { Ok: true, State: TerrainState.Completed, Batches: 3 } && all && events.Count == 3 && events[^1].Last,
            "overlay.applied", $"{r.ChangedCells} {r.Batches} (state {r.State}, events {events.Count}, cells read back {(all ? "equal" : "DIFFERENT")}) {r.Reason}");

        // the default budget: a transient cube of 32 x 32 x 32 cells above the ground next to it, on the 32-cell grid, so
        // it is exactly one batch of 32,768 cells; then the same cube as air again (two batches timed, nothing kept)
        var cube = new CellBox((region.X + 64) / 32 * 32, (y0 + 48) / 32 * 32, region.Z / 32 * 32, 32, 32, 32);
        var full = new byte[cube.Count];
        Array.Fill(full, (byte)1);
        var clock = Stopwatch.StartNew();
        var m = await Terrain.Apply(new TerrainPatch("cube", 1, cube, new TerrainVoxelVolume(full), [new TerrainPaletteEntry(_s.One)], TerrainPersistence.Transient));
        var up = clock.ElapsedMilliseconds;
        clock.Restart();
        var d = await Terrain.Apply(new TerrainPatch("cube", 1, cube, new TerrainVoxelVolume(full), [new TerrainPaletteEntry(0)], TerrainPersistence.Transient));
        Info($"overlay.budget transient cube {cube}: fill {m.State} {m.ChangedCells} cells in {m.Batches} batch(es), {up} ms; clear {d.State} {d.ChangedCells} cells in {d.Batches} batch(es), {clock.ElapsedMilliseconds} ms {m.Reason}{d.Reason}");
        Check(m is { State: TerrainState.Completed, Batches: 1, ChangedCells: 32768 } && d is { State: TerrainState.Completed, ChangedCells: 32768 }
              && Terrain.Applied.All(p => p.Plan != "cube"), "overlay.transient-not-recorded", $"{m.State} {m.ChangedCells}, {d.State} {d.ChangedCells}");

        // a patch inside the save's base
        var bases = Build.Bases;
        if (bases.Count == 0)
        {
            Check(false, "overlay.base-refused", "the lab's save has no base (make the lab with --save)");
        }
        else
        {
            var c = Build.CellOf(bases[0].Center);
            var inBase = new CellBox(c.X / 8 * 8, c.Y / 8 * 8, c.Z / 8 * 8, 8, 8, 8);
            var before = await Build.ReadAsync(inBase);
            var patch = new TerrainPatch("in-base", 1, inBase, new TerrainVoxelVolume([.. Enumerable.Repeat((byte)1, 512)]), [new TerrainPaletteEntry(_s.One)]);
            var would = await Terrain.Inspect(patch);
            var b = await Terrain.Apply(patch);
            var same = (await Build.ReadAsync(inBase)).AsSpan().SequenceEqual(before);
            Check(b is { Ok: false, State: TerrainState.Conflict, ChangedCells: 0 } && b.Conflicts.Count == 1 && b.Conflicts[0].Kind == TerrainConflictKind.BaseZone
                  && same && would.Would == TerrainState.Conflict,
                "overlay.base-refused", $"state {b.State}, {b.ChangedCells} cells, conflicts {string.Join(",", b.Conflicts.Select(x => x.Kind))}, inspect {would.Would}, cells {(same ? "unchanged" : "CHANGED")}");
        }
        await EditingHandlerEditKept(region, y0);
        var save = await Saves.Request();
        Info($"save: {save}");
        _s.Phase = 2;
    }

    // Another mod's BuildEditingEvent handler writes one cell of a block while the overlay writes that block: kiln.build's
    // guard (Build.WriteIfUnchanged) refuses it after the handler and before the commit, the overlay gives that block up,
    // and the batch's other block is written.
    private async Task EditingHandlerEditKept(CellBox vein, int y0)
    {
        // two blocks, one default batch (both inside one 32-cell cube), next to the vein and not sharing a block with it
        var box = new CellBox(vein.X / 32 * 32, y0, vein.Z / 32 * 32 + 32, 16, 8, 8);
        var chosen = box with { SizeX = 8 };
        var at = new CellPos(box.X + 3, box.Y + 2, box.Z + 3);
        var before = await Build.ReadAsync(box);
        var mark = before[box.Index(3, 2, 3)] == Voxel.Of(_s.Two) ? Voxel.Air : Voxel.Of(_s.Two);   // a value the cell does not hold
        var label = "terrain guarded v1";
        var cells = new byte[box.Count];
        Array.Fill(cells, (byte)1);
        BuildResult? foreign = null;
        string? origin = null;
        var inside = false;
        TerrainResult r;
        using (Events.On<BuildEditingEvent>(e =>
        {
            // the overlay's own edit of the chosen block, once; the handler's own write posts the event again (re-entry)
            if (inside || foreign is not null || e.Label != label || Apart(e.Box, chosen)) return;
            inside = true;
            origin = e.Origin;
            try { foreign = Build.Write(new CellBox(at.X, at.Y, at.Z, 1, 1, 1), [mark], new BuildOptions { Journal = false, Label = "an editing handler's edit" }); }
            finally { inside = false; }
        }))
        {
            r = await Terrain.Apply(new TerrainPatch("guarded", 1, box, new TerrainVoxelVolume(cells), [new TerrainPaletteEntry(_s.One)]));
        }
        var now = await Build.ReadAsync(box);
        var left = true;
        var written = true;
        for (var z = 0; z < 8; z++)
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = box.Index(x, y, z);
            if (x >= 8) written &= now[i] == Voxel.Of(_s.One);
            else if ((x, y, z) != (3, 2, 3)) left &= now[i] == before[i];
        }
        var kept = now[box.Index(3, 2, 3)] == mark;
        var plan = Terrain.Applied.FirstOrDefault(p => p.Plan == "guarded");
        Check(foreign is { Changed: true } && r is { Ok: false, State: TerrainState.Conflict, Batches: 1 } && r.Conflicts.Count == 1
              && r.Conflicts[0] == new TerrainConflict(TerrainConflictKind.PlayerEdit, chosen) && kept && left && written
              && plan is { Complete: true, Blocks: 1, GivenUp: 1 },
            "overlay.editing-handler-edit-kept",
            $"handler wrote {mark} at {at} ({foreign?.CellsChanged} cell), apply {r.State} {r.ChangedCells} cells in {r.Batches} batch(es), conflicts " +
            $"{string.Join(";", r.Conflicts.Select(x => $"{x.Kind} {x.Block}"))}, the handler's cell {(kept ? "kept" : $"OVERWRITTEN ({now[box.Index(3, 2, 3)]})")}, " +
            $"the rest of its block {(left ? "as it was" : "TOUCHED")}, the other block {(written ? "written" : "NOT written")}, record {plan}, event origin {origin} {r.Reason}");
    }

    private static bool Apart(CellBox a, CellBox b) =>
        a.X >= b.X + b.SizeX || b.X >= a.X + a.SizeX || a.Y >= b.Y + b.SizeY || b.Y >= a.Y + a.SizeY || a.Z >= b.Z + b.SizeZ || b.Z >= a.Z + a.SizeZ;

    private async Task AfterRestart()
    {
        var region = Region;
        var back = await Build.ReadAsync(region);
        var plan = Terrain.Applied.FirstOrDefault(p => p.Plan == "vein");
        Check(Saves.WorldId == _s.WorldA && back.All(c => c == Voxel.Of(_s.One)) && plan is { Version: 1, Complete: true, Blocks: 3, GivenUp: 0 },
            "overlay.kept-after-restart", $"record {plan}, cells {(back.All(c => c == Voxel.Of(_s.One)) ? "equal" : "DIFFERENT")}");

        var writes = Build.Status?.Writes ?? 0;
        var again = await Terrain.Apply(Vein(1, _s.One), Slow);
        Check(again is { Ok: true, State: TerrainState.AlreadyApplied, ChangedCells: 0, Batches: 0 } && (Build.Status?.Writes ?? 0) == writes,
            "overlay.rerun-noop", $"state {again.State}, kiln.build writes {writes} -> {Build.Status?.Writes}");

        // someone digs one cell of the first block (an ordinary Kiln.Build edit, as a player's dig is to the ledger)
        var dug = new CellPos(region.X + 2, region.Y + 3, region.Z + 2);
        var edit = Build.Write(new CellBox(dug.X, dug.Y, dug.Z, 1, 1, 1), [Voxel.Air], new BuildOptions { Journal = false, Label = "a foreign edit" });
        var up = await Terrain.Apply(Vein(2, _s.Two), Slow);
        var now = await Build.ReadAsync(region);
        bool BlockIs(int block, Voxel v, CellPos? except = null)
        {
            for (var z = 0; z < 8; z++)
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var p = new CellPos(region.X + block * 8 + x, region.Y + y, region.Z + z);
                if (p == except) continue;
                if (now[region.Index(block * 8 + x, y, z)] != v) return false;
            }
            return true;
        }
        var first = new CellBox(region.X, region.Y, region.Z, 8, 8, 8);
        Check(edit.Changed && up is { Ok: false, State: TerrainState.Conflict, ChangedCells: 1024 } && up.Conflicts.Count == 1
              && up.Conflicts[0] == new TerrainConflict(TerrainConflictKind.PlayerEdit, first)
              && now[region.Index(2, 3, 2)].IsAir && BlockIs(0, Voxel.Of(_s.One), dug) && BlockIs(1, Voxel.Of(_s.Two)) && BlockIs(2, Voxel.Of(_s.Two)),
            "overlay.upgrade-conflict", $"state {up.State}, {up.ChangedCells} cells, conflicts {string.Join(";", up.Conflicts.Select(x => $"{x.Kind} {x.Block}"))}, " +
                                        $"dug cell {now[region.Index(2, 3, 2)]}, block 0 {(BlockIs(0, Voxel.Of(_s.One), dug) ? "left" : "TOUCHED")}, blocks 1-2 {(BlockIs(1, Voxel.Of(_s.Two)) && BlockIs(2, Voxel.Of(_s.Two)) ? "version 2" : "NOT version 2")}");
        var save = await Saves.Request();
        Info($"save: {save}");
        _s.Phase = 3;
    }

    private async Task OtherWorld()
    {
        var region = Region;
        var cells = await Build.ReadAsync(region);
        var overlay = cells.Count(c => c == Voxel.Of(_s.One) || c == Voxel.Of(_s.Two));
        var world = Saves.WorldId ?? "";
        var none = Terrain.Applied.Count == 0;
        var r = await Terrain.Apply(Vein(1, _s.One), Slow);
        Check(world.Length > 0 && world != _s.WorldA && none && overlay < cells.Length / 2 && r is { State: TerrainState.Completed, Batches: 3 },
            "overlay.world-b-clean", $"world {world} (a was {_s.WorldA}), records {(none ? "none" : "SOME")}, {overlay} of {cells.Length} cells look like the overlay, apply there: {r.State} {r.Reason}");
        _s.Phase = 4;
    }
}
