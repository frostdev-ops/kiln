// SPDX-License-Identifier: MIT
// Where the fixtures go and how they are written: away from the lab's base (its zone pacifies creatures and refuses edits),
// at the flattest of four candidate spots, the floor 1 m above the highest ground under the whole footprint so no terrain
// reaches into a lane; one Transient write (journaled, put back at the next start).
using System.Text.Json.Nodes;
using Kiln;

namespace PetsClearanceLab;

/// <summary>A fixture in world metres.</summary>
internal sealed record WorldLane(Lane Lane, double FloorY, WorldPosition Start, WorldPosition Target, double PassageZ0, double PassageZ1,
    (double X0, double X1) Interior, double WallTop);

internal sealed record Site(CellBox Box, double Cell, IReadOnlyList<WorldLane> Lanes)
{
    public WorldLane? Control => Lanes.FirstOrDefault(l => l.Lane.Spec.Kind == "control");
}

public sealed partial class PetsClearanceLabMod
{
    private static readonly (double X, double Z)[] Offsets = [(200, 0), (-200, 0), (0, 200), (0, -200)];

    private async Task<Site?> BuildSite()
    {
        var grid = Build.Grid;
        if (grid is null || !Capabilities.Is("build.write"))
        {
            Info($"FAIL no voxel grid or build.write ({Capabilities.Why("build.write")}): no fixtures");
            Evidence.Write("site", new JsonObject { ["error"] = "build unavailable" });
            return null;
        }
        var cell = grid.CellSize;
        var (lanes, sx, sy, sz) = Geometry.Layout(Plan.Specs);
        var bases = Build.Bases;
        var anchor = bases.Count > 0 ? bases[0].Center : new WorldPosition(3685, 835, 1480);
        // the flattest candidate whose ground stays inside the read range
        (CellPos Min, int Top, int Spread)? best = null;
        foreach (var (ox, oz) in Offsets)
        {
            var at = new WorldPosition(Math.Floor(anchor.X + ox), anchor.Y, Math.Floor(anchor.Z + oz));
            var c = Build.CellOf(at);
            var read = new CellBox(c.X, c.Y - 128, c.Z, sx, 256, sz);
            try { await Build.Load(read, TimeSpan.FromSeconds(30)); }
            catch (Exception e) { Info($"site ({ox}, {oz}): loading: {e.Message}"); continue; }
            Voxel[]? cells = null;
            for (var i = 0; i < 20 && !Build.TryRead(read, out cells); i++) await Wait(0.25);
            if (cells is null) { Info($"site ({ox}, {oz}): not readable"); continue; }
            int top = int.MinValue, low = int.MaxValue;
            var clipped = false;
            for (var z = 0; z < sz; z++)
                for (var x = 0; x < sx; x++)
                {
                    var h = -1;
                    for (var y = 255; y >= 0; y--)
                        if (!cells[x + sx * (y + 256 * z)].IsAir) { h = y; break; }
                    if (h == 255) clipped = true;
                    if (h < 0) continue;
                    top = Math.Max(top, h);
                    low = Math.Min(low, h);
                }
            if (clipped || top == int.MinValue) { Info($"site ({ox}, {oz}): ground out of range"); continue; }
            var spread = top - low;
            Info($"site ({ox}, {oz}): ground {Out.F(Build.CornerOf(new CellPos(c.X, read.Y + low, c.Z)).Y)}..{Out.F(Build.CornerOf(new CellPos(c.X, read.Y + top, c.Z)).Y)} m");
            if (best is null || spread < best.Value.Spread) best = (new CellPos(c.X, read.Y, c.Z), top, spread);
        }
        if (best is null)
        {
            Info("FAIL no site for the fixtures");
            Evidence.Write("site", new JsonObject { ["error"] = "no site" });
            return null;
        }
        var floorTop = best.Value.Min.Y + best.Value.Top + 3;   // the floor's top 1 m above the highest ground cell's top
        var box = new CellBox(best.Value.Min.X, floorTop - Geometry.Floor, best.Value.Min.Z, sx, sy, sz);
        var material = Material();
        var solid = Geometry.Solid(lanes, sx, sy, sz);
        var voxels = solid.Select(s => s ? Voxel.Of(material) : Voxel.Air).ToArray();
        BuildResult r;
        try { r = await Build.WriteAsync(box, voxels, new BuildOptions { Flags = BuildFlags.Transient, Label = "pets-clearance fixtures", Journal = false }); }
        catch (Exception e) when (e is BuildException or InvalidOperationException or ArgumentException)
        {
            Info($"transient write refused ({e.Message}); writing a kept edit (a disposable lab world)");
            r = await Build.WriteAsync(box, voxels, new BuildOptions { Label = "pets-clearance fixtures", Journal = false });
        }
        Info($"fixtures written: {r.CellsChanged} cells changed, verified {r.Verified}, refusal {r.Refusal}, box {box}, material {material}");
        if (r.Refusal != BuildRefusal.None)
        {
            Evidence.Write("site", new JsonObject { ["error"] = $"write refused: {r.Refusal}" });
            return null;
        }
        // the ray's and the path grid's caches follow some steps after a write
        await Wait(3);
        var c0 = Build.CornerOf(box.Min);
        double X(double cx) => c0.X + cx * cell;
        double Z(double cz) => c0.Z + cz * cell;
        var floorY = Build.CornerOf(new CellPos(box.X, box.Y + Geometry.Floor, box.Z)).Y;
        double Y(double cy) => floorY + cy * cell;
        var world = new List<WorldLane>();
        foreach (var l in lanes)
        {
            var wl = new WorldLane(l, floorY, new WorldPosition(X(l.Start.X), floorY + 0.05, Z(l.Start.Z)), new WorldPosition(X(l.Target.X), floorY + 0.05, Z(l.Target.Z)),
                Z(l.PassageZ0), Z(l.PassageZ1), (X(l.StartRoom.X0), X(l.StartRoom.X1)), Y(Geometry.WallHeight));
            world.Add(wl);
            JsonArray B(Cells c) => Out.Box(X(c.X0), X(c.X1), Y(c.Y0), Y(c.Y1), Z(c.Z0), Z(c.Z1));
            Evidence.Write("fixture", new JsonObject
            {
                ["name"] = l.Spec.Name, ["fixtureKind"] = l.Spec.Kind, ["lane"] = l.Index,
                ["gap"] = Out.R(l.Spec.GapMeters), ["headroom"] = l.Spec.Head == 0 ? null : Out.R(l.Spec.HeadMeters), ["length"] = Out.R(l.Spec.Length * cell),
                ["floorY"] = Out.R(floorY), ["wallTop"] = Out.R(wl.WallTop), ["start"] = Out.Pos(wl.Start), ["target"] = Out.Pos(wl.Target),
                ["passageZ"] = new JsonArray(Out.R(wl.PassageZ0), Out.R(wl.PassageZ1)),
                ["startRoom"] = B(l.StartRoom), ["exitRoom"] = B(l.ExitRoom),
                ["passage"] = new JsonArray(l.Passage.Select(p => (JsonNode)B(p)).ToArray()),
                ["ceiling"] = l.Ceiling is { } ce ? B(ce) : null,
            });
        }
        Evidence.Write("site", new JsonObject
        {
            ["box"] = new JsonArray(box.X, box.Y, box.Z, box.SizeX, box.SizeY, box.SizeZ), ["cell"] = cell, ["material"] = material,
            ["floorY"] = Out.R(floorY), ["cellsChanged"] = r.CellsChanged, ["verified"] = r.Verified, ["kept"] = r.Kept,
            ["anchor"] = Out.Pos(anchor), ["lanes"] = world.Count,
        });
        return new Site(box, cell, world);
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
