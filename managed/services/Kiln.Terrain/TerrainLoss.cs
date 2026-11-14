// SPDX-License-Identifier: MIT
// Ownership-loss detection (docs/kiln/services/terrain.md, "When the ledger is gone"). The ledger lives in the world's
// Kiln data, next to the world save, not in it: if that data or the ledger key is lost while the edited ground stays,
// a kept plan with no record would be taken for a first install and write over what changed since. Kiln writes nothing
// into the game's world file (WorldData, the entity and item stores and kiln.build's kept chunks are all files of
// their own under kiln/data), so no marker survives with the ground; the ground itself is the evidence. Before a kept
// plan with no record writes anything, its region is read and every block it addresses is asked: does this block
// already hold the plan's result, and does the plan's own shape show in it? One such block means the plan was applied
// here before: refuse, write nothing. Tick thread.
namespace Kiln;

internal static class TerrainLoss
{
    /// <summary>The one call before a kept plan's first install (Apply and Inspect, once the plan's record is known to
    /// be missing from a valid <paramref name="book"/>). Null: no record is missing that the ground could show, apply as
    /// a first install. Otherwise the state and reason to return instead, with nothing written and nothing recorded:
    /// <see cref="TerrainState.Refused"/> when the ground already holds the plan's result (the ledger was lost),
    /// <see cref="TerrainState.Partial"/> when the ground could not be read to decide (as a batch that cannot load).</summary>
    public static async Task<(TerrainState State, string Reason)?> Check(ITerrainBackend backend, TerrainPlan plan, TerrainBook book, TimeSpan loadTimeout)
    {
        var patch = plan.Patch;
        if (patch.Persistence != TerrainPersistence.Kept || book.Plans.ContainsKey(patch.Id)) return null;
        var world = backend.WorldId ?? "";
        var box = Around(plan.Region, backend.GridCells);
        bool loaded;
        try { loaded = await backend.Load(box, loadTimeout); }
        catch (BuildException e) { return (TerrainState.Partial, e.Message); }
        if (!backend.WorldRunning || (backend.WorldId ?? "") != world) return (TerrainState.Partial, "the world stopped or changed");
        if (!loaded || !backend.TryRead(box, out var cells))
            return (TerrainState.Partial, $"the voxel tiles under {box} did not load, so the ground could not be checked for an earlier apply of plan '{patch.Id}'");
        var found = Recognised(plan, box, cells);
        if (found.Count == 0) return null;
        var reason = Reason(patch.Id, found, backend.LoadedData);
        Kiln.Backend.Current.Log(0, LogLevel.Warn, $"terrain: {backend.Mod}: {reason}");
        return (TerrainState.Refused, reason);
    }

    /// <summary>The refusal's text: what was found, why it is refused, and what an operator can do.</summary>
    internal static string Reason(string plan, IReadOnlyList<CellBox> blocks, DataMatch data)
    {
        var cause = data == DataMatch.None
            ? "this world has no Kiln data that could be read, so no terrain ledger records it (the world's Kiln data was lost or is unreadable)"
            : "this mod's terrain ledger has no record of it (the ledger was lost or reset, or the plan was forgotten)";
        return $"ownership lost: the ground already holds plan '{plan}' in {blocks.Count} block(s) (first {blocks[0].X},{blocks[0].Y},{blocks[0].Z}), " +
               $"but {cause}. Nothing was written, so whatever changed there since is kept. To own it again, restore this world's Kiln data " +
               "from a backup that matches the world save (Saves.RestoreOnNextStart, or a kilnctl backup); adopting the ground as it " +
               "stands is not available in this version";
    }

    /// <summary>The region and the cells around it (one on each side, inside the grid): the plan's edge at the region's
    /// border is seen against the ground outside.</summary>
    internal static CellBox Around(CellBox r, (int X, int Z)? grid)
    {
        int x0 = Math.Max(0, r.X - 1), y0 = Math.Max(0, r.Y - 1), z0 = Math.Max(0, r.Z - 1);
        long x1 = (long)r.X + r.SizeX + 1, y1 = Math.Min((long)r.Y + r.SizeY + 1, TerrainPlan.MaxY), z1 = (long)r.Z + r.SizeZ + 1;
        if (grid is { } g)
        {
            x1 = Math.Min(x1, Math.Max(g.X, (long)r.X + r.SizeX));
            z1 = Math.Min(z1, Math.Max(g.Z, (long)r.Z + r.SizeZ));
        }
        return new CellBox(x0, y0, z0, (int)(x1 - x0), (int)(y1 - y0), (int)(z1 - z0));
    }

    /// <summary>The blocks (whole, on the 8-cell grid) that show an earlier apply of <paramref name="plan"/>, read from
    /// <paramref name="cells"/>, the cells of <paramref name="box"/> (which holds the region). A block shows it when a
    /// fresh apply would change none of its cells AND the plan's shape can be seen in it:
    /// <list type="bullet">
    /// <item>a voxel volume or a replacing heightfield: a cell the plan sets to matter, holding exactly that, borders a
    /// cell the plan sets to another value and that holds it (an edge inside the plan: matter and air, two palette
    /// entries); or else every cell of the ground that borders the block's matter (inside the region and not the plan's,
    /// or just outside the region) holds something other than that matter;</item>
    /// <item>a heightfield added on top: every column top in the block that holds exactly what the plan puts there has less
    /// matter right above it (the plan's surface, not ground that was higher), and there is at least one.</item>
    /// </list>
    /// Air the plan digs is never evidence by itself (a tunnel's mouth in open air looks the same before and after). A
    /// block a player changed is not "already the result": it is protected when another block of the plan shows it.</summary>
    internal static List<CellBox> Recognised(TerrainPlan plan, CellBox box, ReadOnlySpan<Voxel> cells)
    {
        var list = new List<CellBox>();
        foreach (var part in plan.Blocks())
            if (HoldsResult(plan, box, cells, part) && (plan.OnlyAdd ? ShowsSurface(plan, box, cells, part) : ShowsEdge(plan, box, cells, part)))
                list.Add(TerrainPlan.WholeBlock(part));
        return list;
    }

    /// <summary>A fresh apply would change no cell of <paramref name="part"/>: the same test the engine's batch makes.</summary>
    private static bool HoldsResult(TerrainPlan plan, CellBox box, ReadOnlySpan<Voxel> cells, CellBox part)
    {
        var r = plan.Region;
        for (var z = part.Z; z < part.Z + part.SizeZ; z++)
        for (var y = part.Y; y < part.Y + part.SizeY; y++)
        for (var x = part.X; x < part.X + part.SizeX; x++)
        {
            var ri = r.Index(x - r.X, y - r.Y, z - r.Z);
            if (!plan.Addressed[ri]) continue;
            var now = cells[box.Index(x - box.X, y - box.Y, z - box.Z)];
            var want = plan.Target[ri];
            if (plan.OnlyAdd ? now.Fill < want.Fill : now != want) return false;
        }
        return true;
    }

    private static readonly (int X, int Y, int Z)[] Faces = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];

    private static bool ShowsEdge(TerrainPlan plan, CellBox box, ReadOnlySpan<Voxel> cells, CellBox part)
    {
        var r = plan.Region;
        var ground = 0;
        var groundGoesOn = false;
        for (var z = part.Z; z < part.Z + part.SizeZ; z++)
        for (var y = part.Y; y < part.Y + part.SizeY; y++)
        for (var x = part.X; x < part.X + part.SizeX; x++)
        {
            var ri = r.Index(x - r.X, y - r.Y, z - r.Z);
            if (!plan.Addressed[ri] || plan.Target[ri].IsAir) continue;
            var c = cells[box.Index(x - box.X, y - box.Y, z - box.Z)];   // equals the target: HoldsResult
            foreach (var (dx, dy, dz) in Faces)
            {
                var u = new CellPos(x + dx, y + dy, z + dz);
                if (!box.Contains(u)) continue;
                var cu = cells[box.Index(u.X - box.X, u.Y - box.Y, u.Z - box.Z)];
                if (r.Contains(u) && plan.Addressed[r.Index(u.X - r.X, u.Y - r.Y, u.Z - r.Z)])
                {
                    var tu = plan.Target[r.Index(u.X - r.X, u.Y - r.Y, u.Z - r.Z)];
                    if (tu != c && cu == tu) return true;   // the plan's own edge, both sides as the plan left them
                    continue;
                }
                if (cu == c) groundGoesOn = true;           // the ground goes on as the plan's matter: no edge there
                else ground++;
            }
        }
        return ground > 0 && !groundGoesOn;
    }

    private static bool ShowsSurface(TerrainPlan plan, CellBox box, ReadOnlySpan<Voxel> cells, CellBox part)
    {
        var r = plan.Region;
        var tops = 0;
        for (var z = part.Z; z < part.Z + part.SizeZ; z++)
        for (var y = part.Y; y < part.Y + part.SizeY; y++)
        for (var x = part.X; x < part.X + part.SizeX; x++)
        {
            var ri = r.Index(x - r.X, y - r.Y, z - r.Z);
            if (!plan.Addressed[ri] || plan.Target[ri].IsAir) continue;
            var above = new CellPos(x, y + 1, z);
            if (r.Contains(above) && plan.Addressed[r.Index(x - r.X, y + 1 - r.Y, z - r.Z)]) continue;   // not the column's top
            var c = cells[box.Index(x - box.X, y - box.Y, z - box.Z)];
            if (c != plan.Target[ri] || !box.Contains(above)) continue;   // left alone (the ground was fuller), or nothing to see above
            if (cells[box.Index(x - box.X, y + 1 - box.Y, z - box.Z)].Fill >= c.Fill) return false;   // ground above: not the plan's surface
            tops++;
        }
        return tops > 0;
    }
}
