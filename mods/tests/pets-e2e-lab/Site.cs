// SPDX-License-Identifier: MIT
// The walk's site: a sealed lane of building blocks beside the bot, so a pet that follows its walking owner from the
// first room to the second must walk through one 2 m gap (a route over a wall or around it does not exist), and a pad
// 85 m away for the catch-up. In 0.5 m cells: a 1 m floor; 1 m walls 5 m high; a 10 m wide interior; room A 15 m long,
// a 1 m cross wall with a 2 m gap in its middle, room B 30 m long. Each floor sits 1 m above the highest ground under it
// (as the clearance lab's fixtures). One Transient write each (a kept edit if the server refuses that: a disposable lab).
using System.Text.Json.Nodes;
using Kiln;

namespace PetsE2eLab;

public sealed partial class PetsE2eLabMod
{
    private const int Wall = 2, Interior = 20, RoomA = 30, Cross = 2, RoomB = 60, WallHeight = 10, Floor = 2, GapCells = 4;
    private const int SizeX = Wall + Interior + Wall, SizeY = Floor + WallHeight, SizeZ = Wall + RoomA + Cross + RoomB + Wall;
    private const int PadCells = 20;

    [Command("e2e site", Description = "builds the walk's lane beside the caller and the catch-up pad", Permission = CommandPermission.Admin)]
    private void SiteCommand(CommandContext c, string tag)
    {
        var (who, _) = Caller(c);
        var at = World.Position(who) ?? throw new CommandException("the caller has no position");
        Later("site", () => BuildSite(tag, at));
        c.Reply("{}");
    }

    private async Task BuildSite(string tag, WorldPosition near)
    {
        if (Build.Grid is not { } grid || !Capabilities.Is("build.write"))
        {
            Write("site", new JsonObject { ["tag"] = tag, ["error"] = $"no voxel grid or build.write: {Capabilities.Why("build.write")}" });
            return;
        }
        var cell = grid.CellSize;
        (CellPos Min, int Top, int Spread)? best = null;
        foreach (var (ox, oz) in new[] { (20.0, -20.0), (-32.0, -20.0), (-6.0, 20.0), (-6.0, -70.0) })
        {
            var at = new WorldPosition(Math.Floor(near.X + ox), near.Y, Math.Floor(near.Z + oz));
            var g = await Ground(at, SizeX, SizeZ);
            if (g is null) continue;
            Info($"site ({ox}, {oz}): spread {g.Value.Spread} cells");
            if (best is null || g.Value.Spread < best.Value.Spread) best = g;
        }
        // no ground read in range (the box writes air as well, so terrain inside it is cut away): the floor's top at the
        // caller's feet, beside the caller
        var feet = Build.CellOf(near);
        best ??= (new CellPos(feet.X + 40, feet.Y - 3, feet.Z - 40), 0, -1);
        var floorTop = best.Value.Spread < 0 ? feet.Y : best.Value.Min.Y + best.Value.Top + 3;
        var box = new CellBox(best.Value.Min.X, floorTop - Floor, best.Value.Min.Z, SizeX, SizeY, SizeZ);
        var material = Material();
        var cells = new Voxel[SizeX * SizeY * SizeZ];
        for (var z = 0; z < SizeZ; z++)
            for (var y = 0; y < SizeY; y++)
                for (var x = 0; x < SizeX; x++)
                    cells[x + SizeX * (y + SizeY * z)] = Free(x, y - Floor, z) ? Voxel.Air : Voxel.Of(material);
        var r = await Put(box, cells, "pets-e2e lane");
        if (r is null || r.Refusal != BuildRefusal.None)
        {
            Write("site", new JsonObject { ["tag"] = tag, ["error"] = $"lane write refused: {r?.Refusal}" });
            return;
        }
        var c0 = Build.CornerOf(box.Min);
        var floorY = Build.CornerOf(new CellPos(box.X, box.Y + Floor, box.Z)).Y;
        double X(double cx) => c0.X + cx * cell;
        double Z(double cz) => c0.Z + cz * cell;
        var midX = X(Wall + Interior / 2.0);
        var gapZ0 = Z(Wall + RoomA);
        var gapZ1 = Z(Wall + RoomA + Cross);
        // the pad: a floor 85 m along +X from the lane's middle
        var padNear = new WorldPosition(Math.Floor(midX + 85), near.Y, Math.Floor(Z(SizeZ / 2.0)));
        JsonObject? pad = null;
        var pg = await Ground(padNear, PadCells, PadCells);
        var pc0 = Build.CellOf(padNear);
        pg ??= (new CellPos(pc0.X, floorTop - 3, pc0.Z), 0, -1);
        if (pg is { } p)
        {
            var padTop = p.Spread < 0 ? floorTop : p.Min.Y + p.Top + 3;
            // the pad: a floor with 4 m of air over it (where the ground is higher, a room cut into it)
            const int padAir = 8;
            var pbox = new CellBox(p.Min.X, padTop - Floor, p.Min.Z, PadCells, Floor + padAir, PadCells);
            var pcells = new Voxel[PadCells * (Floor + padAir) * PadCells];
            for (var z = 0; z < PadCells; z++)
                for (var y = 0; y < Floor + padAir; y++)
                    for (var x = 0; x < PadCells; x++)
                        pcells[x + PadCells * (y + (Floor + padAir) * z)] = y < Floor ? Voxel.Of(material) : Voxel.Air;
            var pr = await Put(pbox, pcells, "pets-e2e pad");
            var pc = Build.CornerOf(pbox.Min);
            var padY = Build.CornerOf(new CellPos(pbox.X, pbox.Y + Floor, pbox.Z)).Y;
            pad = new JsonObject
            {
                ["center"] = Pos(new WorldPosition(pc.X + PadCells * cell / 2, padY + 0.05, pc.Z + PadCells * cell / 2)), ["refusal"] = pr?.Refusal.ToString(),
            };
        }
        await Wait(3);   // the ray's and the path grid's caches follow some steps after a write
        Write("site", new JsonObject
        {
            ["tag"] = tag, ["box"] = new JsonArray(box.X, box.Y, box.Z, box.SizeX, box.SizeY, box.SizeZ), ["cell"] = cell, ["material"] = material,
            ["floorY"] = R(floorY), ["corner"] = Pos(c0), ["midX"] = R(midX), ["gapX"] = new JsonArray(R(X(Wall + (Interior - GapCells) / 2.0)), R(X(Wall + (Interior + GapCells) / 2.0))),
            ["gapZ"] = new JsonArray(R(gapZ0), R(gapZ1)), ["roomA"] = new JsonArray(R(Z(Wall)), R(gapZ0)), ["roomB"] = new JsonArray(R(gapZ1), R(Z(SizeZ - Wall))),
            ["interiorX"] = new JsonArray(R(X(Wall)), R(X(Wall + Interior))), ["wallTop"] = R(floorY + WallHeight * cell),
            ["changed"] = r.CellsChanged, ["verified"] = r.Verified, ["kept"] = r.Kept, ["pad"] = pad,
        });
        Info($"site {tag}: lane at {box}, floor {floorY:F2}, pad {pad?.ToJsonString()}");
    }

    /// <summary>Free (air) cells of the lane: y counts from the floor's top.</summary>
    private static bool Free(int x, int y, int z)
    {
        if (y < 0 || y >= WallHeight) return false;
        if (x < Wall || x >= Wall + Interior || z < Wall || z >= SizeZ - Wall) return false;
        if (z >= Wall + RoomA && z < Wall + RoomA + Cross)
            return x >= Wall + (Interior - GapCells) / 2 && x < Wall + (Interior + GapCells) / 2;
        return true;
    }

    private async Task<(CellPos Min, int Top, int Spread)?> Ground(WorldPosition at, int sx, int sz)
    {
        var c = Build.CellOf(at);
        var read = new CellBox(c.X, c.Y - 128, c.Z, sx, 256, sz);
        try { await Build.Load(read, TimeSpan.FromSeconds(30)); }
        catch (Exception e)
        {
            Info($"ground at {at}: loading: {e.Message}");
            return null;
        }
        Voxel[]? cells = null;
        var ok = false;
        for (var i = 0; i < 20 && !(ok = Build.TryRead(read, out cells)); i++) await Wait(0.25);
        if (cells is null || !ok)
        {
            Info($"ground at {at}: not readable");
            return null;
        }
        int top = int.MinValue, low = int.MaxValue, clipped = 0, air = 0;
        for (var z = 0; z < sz; z++)
            for (var x = 0; x < sx; x++)
            {
                var h = -1;
                for (var y = 255; y >= 0; y--)
                    if (!cells[x + sx * (y + 256 * z)].IsAir) { h = y; break; }
                if (h == 255) clipped++;
                if (h < 0) air++;
                if (h < 0 || h == 255) continue;
                top = Math.Max(top, h);
                low = Math.Min(low, h);
            }
        if (clipped > 0 || top == int.MinValue)
        {
            Info($"ground at {at} (cell {c}): {clipped} columns solid to the top, {air} all air, of {sx * sz}; top {top}");
            return null;
        }
        return (new CellPos(c.X, read.Y, c.Z), top, top - low);
    }

    private async Task<BuildResult?> Put(CellBox box, Voxel[] voxels, string label)
    {
        try { return await Build.WriteAsync(box, voxels, new BuildOptions { Flags = BuildFlags.Transient, Label = label, Journal = false }); }
        catch (Exception e) when (e is BuildException or InvalidOperationException or ArgumentException)
        {
            Info($"{label}: transient write refused ({e.Message}); a kept edit (a disposable lab world)");
            try { return await Build.WriteAsync(box, voxels, new BuildOptions { Label = label, Journal = false }); }
            catch (Exception x) when (x is BuildException or InvalidOperationException or ArgumentException)
            {
                Info($"{label}: refused: {x.Message}");
                return null;
            }
        }
    }

    private static byte Material()
    {
        foreach (var name in new[] { "Block_T1_Stone_Rough_CRAFTING", "Block_T1_Wood_Rough_CRAFTING" })
            try
            {
                if (Items.Lookup(name) is { } item && Build.MaterialOf(item.Id.Value) is var m and >= 128) return m;
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException) { }
        return 143;   // Rough Wood Block (docs/re/building.md)
    }
}
