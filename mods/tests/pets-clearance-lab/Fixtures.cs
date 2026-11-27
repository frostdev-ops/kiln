// SPDX-License-Identifier: MIT
// The calibration fixtures (dossier 19d, gate C6): sealed lanes of building blocks side by side, each a start room, one
// measured passage and an exit room, so a walking route from the start to the exit must cross the passage (a wall top
// or a fly is no walk). Geometry is in 0.5 m cells relative to the box's minimum corner; Box() turns it into world
// metres. Every lane: a 1 m floor, 1 m side and end walls 5 m high; rooms 5 m wide (start 7 m, exit at least 8 m long).
namespace PetsClearanceLab;

/// <summary>One passage. <paramref name="Gap"/> and <paramref name="Head"/> in cells (Head 0: open to the wall top).</summary>
internal sealed record FixtureSpec(string Name, string Kind, int Gap, int Head, int Length)
{
    public double GapMeters => Gap * Geometry.Cell;
    public double HeadMeters => Head == 0 ? double.PositiveInfinity : Head * Geometry.Cell;

    /// <summary>The fixtures of the brief: straight corridors 0.5/1/2 m wide with 1/2/3 m headroom (3 m long), a door-wall
    /// doorway (2 x 2.5 m, as the game's 4 m door wall cuts it, 0.5 m deep), two S-bends (two 90 degree corners) 1 and
    /// 2 m wide, and the wide/high control (the room's whole 5 m width, open). Two more close the 0.5 m steps a 2 m body
    /// needs: a 1.5 m gap under 3 m and 1.5 m of headroom over 2 m (tools/pets-clearance/profiles.py: a pair vouches for
    /// a body only when no passage one cell narrower or lower than it went unmeasured).</summary>
    public static IReadOnlyList<FixtureSpec> All { get; } = BuildAll();

    private static List<FixtureSpec> BuildAll()
    {
        var all = new List<FixtureSpec>();
        foreach (var g in new[] { 1, 2, 4 })
            foreach (var h in new[] { 2, 4, 6 })
                all.Add(Corridor(g, h));
        all.Add(Corridor(3, 6));
        all.Add(Corridor(4, 3));
        all.Add(new FixtureSpec("doorway-w2-h2.5", "doorway", 4, 5, 1));
        all.Add(new FixtureSpec("corner-w1", "corner", 2, 0, 8 + 2));
        all.Add(new FixtureSpec("corner-w2", "corner", 4, 0, 8 + 4));
        all.Add(new FixtureSpec("control-w5-open", "control", Geometry.Interior, 0, 6));
        return all;
    }

    private static FixtureSpec Corridor(int gap, int head) =>
        new(FormattableString.Invariant($"corridor-w{gap * Geometry.Cell:0.#}-h{head * Geometry.Cell:0.#}"), "corridor", gap, head, 6);
}

/// <summary>An axis-aligned box in cells, [X0, X1) x [Y0, Y1) x [Z0, Z1).</summary>
internal readonly record struct Cells(int X0, int X1, int Y0, int Y1, int Z0, int Z1)
{
    public bool Contains(int x, int y, int z) => x >= X0 && x < X1 && y >= Y0 && y < Y1 && z >= Z0 && z < Z1;
}

/// <summary>A lane's layout in cells: where the body starts, where it walks to, and the free space of each part.</summary>
internal sealed record Lane(FixtureSpec Spec, int Index, int X0, Cells StartRoom, IReadOnlyList<Cells> Passage, Cells ExitRoom,
    Cells? Ceiling, (double X, double Z) Start, (double X, double Z) Target, int PassageZ0, int PassageZ1);

internal static class Geometry
{
    public const double Cell = 0.5;
    public const int Wall = 2, Interior = 10, StartRoom = 14, ExitRoom = 16, WallHeight = 10, Floor = 2;
    /// <summary>The start: lane centre, 4.5 m before the passage. The target: lane centre, 5 m past it.</summary>
    public const int StartBefore = 9, TargetAfter = 10;

    public static int PassageStart => Wall + StartRoom;

    /// <summary>The lanes for <paramref name="specs"/> in order along +X, and the box's size in cells (X, Y with the floor, Z).</summary>
    public static (IReadOnlyList<Lane> Lanes, int SizeX, int SizeY, int SizeZ) Layout(IReadOnlyList<FixtureSpec> specs)
    {
        var longest = specs.Count == 0 ? 0 : specs.Max(s => s.Length);
        var sizeZ = PassageStart + longest + ExitRoom + Wall;
        var lanes = new List<Lane>();
        for (var i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            var x0 = i * (Interior + Wall);
            int xi0 = x0 + Wall, xi1 = xi0 + Interior;
            int p0 = PassageStart, p1 = p0 + s.Length;
            var top = WallHeight;
            var head = s.Head == 0 ? top : Math.Min(s.Head, top);
            var start = new Cells(xi0, xi1, 0, top, Wall, p0);
            var exit = new Cells(xi0, xi1, 0, top, p1, sizeZ - Wall);
            var passage = new List<Cells>();
            switch (s.Kind)
            {
                case "corner":
                    // an S-bend: in at the left wall, across, out at the right wall (two 90 degree corners)
                    passage.Add(new Cells(xi0, xi0 + s.Gap, 0, head, p0, p0 + 4 + s.Gap));
                    passage.Add(new Cells(xi0, xi1, 0, head, p0 + 4, p0 + 4 + s.Gap));
                    passage.Add(new Cells(xi1 - s.Gap, xi1, 0, head, p0 + 4, p1));
                    break;
                default:
                    var left = xi0 + (Interior - s.Gap) / 2;
                    passage.Add(new Cells(left, left + s.Gap, 0, head, p0, p1));
                    break;
            }
            Cells? ceiling = s.Head == 0 ? null : new Cells(xi0, xi1, head, top, p0, p1);
            var cx = (xi0 + xi1) / 2.0;
            lanes.Add(new Lane(s, i, x0, start, passage, exit, ceiling, (cx, p0 - StartBefore), (cx, p1 + TargetAfter), p0, p1));
        }
        var sizeX = specs.Count * (Interior + Wall) + Wall;
        return (lanes, sizeX, Floor + WallHeight, sizeZ);
    }

    /// <summary>Whether cell (x, y, z) is free (air): y counts from the floor's top (the floor is y -2 and -1).</summary>
    public static bool Free(IReadOnlyList<Lane> lanes, int x, int y, int z)
    {
        if (y < 0) return false;
        foreach (var l in lanes)
        {
            if (l.StartRoom.Contains(x, y, z) || l.ExitRoom.Contains(x, y, z)) return true;
            foreach (var p in l.Passage)
                if (p.Contains(x, y, z)) return true;
        }
        return false;
    }

    /// <summary>The box's cells, x fastest, then y (from the floor's bottom), then z: solid where not free.</summary>
    public static bool[] Solid(IReadOnlyList<Lane> lanes, int sizeX, int sizeY, int sizeZ)
    {
        var solid = new bool[sizeX * sizeY * sizeZ];
        for (var z = 0; z < sizeZ; z++)
            for (var y = 0; y < sizeY; y++)
                for (var x = 0; x < sizeX; x++)
                    solid[x + sizeX * (y + sizeY * z)] = !Free(lanes, x, y - Floor, z);
        return solid;
    }
}
