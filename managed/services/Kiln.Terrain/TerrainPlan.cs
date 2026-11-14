// SPDX-License-Identifier: MIT
// A patch checked and lowered to cells: what every cell of the region should become and which cells the patch addresses.
// Pure: no world, no engine. The rules are the contract's (docs/kiln/services/terrain.md).
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kiln;

internal sealed class TerrainPlan
{
    public const int MaxAxis = 256;
    public const long MaxCells = 1 << 20;
    public const int MaxY = 8192;          // kiln.build's own ceiling (voxel.cpp)
    public const int MinCellsPerTick = 512;
    public const int MaxCellsPerTick = 1 << 18;
    public const int MaxPlans = 64;
    public const int BytesPerBlock = 1024; // 512 cells of two bytes

    public required TerrainPatch Patch { get; init; }
    public required CellBox Region { get; init; }
    /// <summary>The wanted value of each cell of the region (x fastest, then y, then z); meaningful where addressed.</summary>
    public required Voxel[] Target { get; init; }
    public required bool[] Addressed { get; init; }
    /// <summary>Write a cell only where the ground holds less matter (a heightfield added on top).</summary>
    public required bool OnlyAdd { get; init; }
    public required long AddressedCells { get; init; }
    /// <summary>Region, palette, source and persistence; not id or version.</summary>
    public required string ContentHash { get; init; }

    public static bool ValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
        foreach (var c in id)
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        return true;
    }

    /// <summary>Checks a patch and lowers it. Throws <see cref="ArgumentException"/> for anything the contract refuses
    /// that can be seen without a world.</summary>
    public static TerrainPlan Lower(TerrainPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!ValidId(patch.Id)) throw new ArgumentException("a plan id is 1 to 64 characters of a-z, 0-9, '.', '_', '-'", nameof(patch));
        if (patch.Version < 1) throw new ArgumentException($"plan '{patch.Id}': version {patch.Version} is below 1", nameof(patch));
        if (!Enum.IsDefined(patch.Persistence)) throw new ArgumentException($"plan '{patch.Id}': unknown persistence {(int)patch.Persistence}", nameof(patch));
        var r = patch.Region;
        if (r.IsEmpty) throw new ArgumentException($"plan '{patch.Id}': the region {r} is empty", nameof(patch));
        if (r.SizeX > MaxAxis || r.SizeY > MaxAxis || r.SizeZ > MaxAxis || r.Count > MaxCells)
            throw new ArgumentException($"plan '{patch.Id}': the region {r} is too big (at most {MaxAxis} cells per axis, {MaxCells} cells)", nameof(patch));
        if (r.X < 0 || r.Z < 0 || r.Y < 0 || (long)r.Y + r.SizeY > MaxY)
            throw new ArgumentException($"plan '{patch.Id}': the region {r} is outside the voxel grid (x and z from 0, y from 0 to {MaxY - 1})", nameof(patch));
        var palette = patch.Palette ?? throw new ArgumentException($"plan '{patch.Id}': no palette", nameof(patch));
        if (palette.Count is < 1 or > 255) throw new ArgumentException($"plan '{patch.Id}': a palette has 1 to 255 entries, not {palette.Count}", nameof(patch));
        var values = new Voxel[palette.Count + 1];
        for (var i = 0; i < palette.Count; i++)
        {
            var e = palette[i] ?? throw new ArgumentException($"plan '{patch.Id}': palette entry {i + 1} is null", nameof(patch));
            if (e.Material == 0) { values[i + 1] = Voxel.Air; continue; }
            if (e.Material is > 116 and < 128 or > 212)
                throw new ArgumentException($"plan '{patch.Id}': palette entry {i + 1} names material {e.Material}; an overlay takes 1 to 116 (terrain) and 128 to 212 (building blocks)", nameof(patch));
            if (e.Fill == 0) throw new ArgumentException($"plan '{patch.Id}': palette entry {i + 1} has fill 0; use material 0 for air", nameof(patch));
            values[i + 1] = Voxel.Of(e.Material, e.Fill);
        }

        var n = checked((int)r.Count);
        var target = new Voxel[n];
        var addressed = new bool[n];
        long count = 0;
        var onlyAdd = false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> head = stackalloc byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(head, r.X);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], r.Y);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], r.Z);
        BinaryPrimitives.WriteInt32LittleEndian(head[12..], r.SizeX);
        BinaryPrimitives.WriteInt32LittleEndian(head[16..], r.SizeY);
        BinaryPrimitives.WriteInt32LittleEndian(head[20..], r.SizeZ);
        BinaryPrimitives.WriteInt32LittleEndian(head[24..], (int)patch.Persistence);
        hash.AppendData(head);
        foreach (var v in values.AsSpan(1)) { Span<byte> b = [(byte)v.Raw, (byte)(v.Raw >> 8)]; hash.AppendData(b); }

        switch (patch.Source)
        {
            case TerrainVoxelVolume vol:
            {
                var cells = vol.Cells ?? throw new ArgumentException($"plan '{patch.Id}': the voxel volume has no cells", nameof(patch));
                if (cells.Length != n) throw new ArgumentException($"plan '{patch.Id}': {cells.Length} cells for a region of {n}", nameof(patch));
                for (var i = 0; i < n; i++)
                {
                    var p = cells[i];
                    if (p == 0) continue;
                    if (p > palette.Count) throw new ArgumentException($"plan '{patch.Id}': cell {i} uses palette number {p}; the palette has {palette.Count}", nameof(patch));
                    target[i] = values[p];
                    addressed[i] = true;
                    count++;
                }
                hash.AppendData([1]);
                hash.AppendData(cells);
                break;
            }
            case TerrainHeightfield hf:
            {
                var heights = hf.Heights ?? throw new ArgumentException($"plan '{patch.Id}': the heightfield has no heights", nameof(patch));
                if (heights.Length != r.SizeX * r.SizeZ) throw new ArgumentException($"plan '{patch.Id}': {heights.Length} heights for {r.SizeX} x {r.SizeZ} columns", nameof(patch));
                if (!Enum.IsDefined(hf.Mode)) throw new ArgumentException($"plan '{patch.Id}': unknown heightfield mode {(int)hf.Mode}", nameof(patch));
                if (hf.Fill == 0 || hf.Fill > palette.Count || values[hf.Fill].IsAir)
                    throw new ArgumentException($"plan '{patch.Id}': the heightfield's fill must be a palette number of matter", nameof(patch));
                if (hf.Surface > palette.Count || (hf.Surface != 0 && values[hf.Surface].IsAir))
                    throw new ArgumentException($"plan '{patch.Id}': the heightfield's surface must be 0 or a palette number of matter", nameof(patch));
                if (hf.SurfaceDepth < 0 || hf.SurfaceDepth > r.SizeY) throw new ArgumentException($"plan '{patch.Id}': surface depth {hf.SurfaceDepth} is outside 0 to {r.SizeY}", nameof(patch));
                onlyAdd = hf.Mode == TerrainHeightMode.AddOnTop;
                var fill = values[hf.Fill];
                var surface = hf.Surface == 0 ? fill : values[hf.Surface];
                for (var z = 0; z < r.SizeZ; z++)
                for (var x = 0; x < r.SizeX; x++)
                {
                    var h = heights[z * r.SizeX + x];
                    if (!float.IsFinite(h) || h < 0 || h > r.SizeY)
                        throw new ArgumentException($"plan '{patch.Id}': height {h} of column {x},{z} is outside 0 to {r.SizeY}", nameof(patch));
                    var full = (int)MathF.Floor(h);
                    var frac = h - full;
                    var top = frac > 0 ? full : full - 1;   // the highest cell with matter; -1 = none
                    for (var y = 0; y < r.SizeY; y++)
                    {
                        var i = r.Index(x, y, z);
                        if (y > top)
                        {
                            if (onlyAdd) continue;          // adding never digs: the air above is not addressed
                            target[i] = Voxel.Air;
                        }
                        else
                        {
                            var m = hf.SurfaceDepth > 0 && y > top - hf.SurfaceDepth ? surface : fill;
                            target[i] = y == top && frac > 0
                                ? Voxel.Of(m.Material, (byte)Math.Clamp((int)MathF.Round(frac * m.Fill), 1, 255))
                                : m;
                        }
                        addressed[i] = true;
                        count++;
                    }
                }
                Span<byte> meta = [2, hf.Fill, (byte)hf.Mode, hf.Surface, 0, 0, 0, 0];
                BinaryPrimitives.WriteInt32LittleEndian(meta[4..], hf.SurfaceDepth);
                hash.AppendData(meta);
                Span<byte> f = stackalloc byte[4];
                foreach (var h in heights) { BinaryPrimitives.WriteSingleLittleEndian(f, h); hash.AppendData(f); }
                break;
            }
            case null:
                throw new ArgumentException($"plan '{patch.Id}': no source", nameof(patch));
            default:
                throw new ArgumentException($"plan '{patch.Id}': unknown source {patch.Source.GetType().Name}", nameof(patch));
        }
        if (count == 0) throw new ArgumentException($"plan '{patch.Id}': the patch addresses no cell", nameof(patch));
        return new TerrainPlan
        {
            Patch = patch, Region = r, Target = target, Addressed = addressed, OnlyAdd = onlyAdd, AddressedCells = count,
            ContentHash = Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 8)),
        };
    }

    /// <summary>The batch edge for a cells-per-tick budget: the largest of 64, 32, 16, 8 whose cube fits.</summary>
    public static int BatchEdge(int cellsPerTick)
    {
        if (cellsPerTick is < MinCellsPerTick or > MaxCellsPerTick)
            throw new ArgumentException($"CellsPerTick {cellsPerTick} is outside {MinCellsPerTick} to {MaxCellsPerTick}", "options");
        foreach (var e in (ReadOnlySpan<int>)[64, 32, 16])
            if ((long)e * e * e <= cellsPerTick) return e;
        return 8;
    }

    /// <summary>The region cut on the world's <paramref name="edge"/>-cell grid, in z, y, x order, without the pieces
    /// that address no cell.</summary>
    public List<CellBox> Batches(int edge)
    {
        var list = new List<CellBox>();
        foreach (var b in Cut(Region, edge))
            if (AnyAddressed(b)) list.Add(b);
        return list;
    }

    /// <summary>The pieces of <paramref name="box"/> on the world's <paramref name="edge"/>-cell grid: z, then y, then x.</summary>
    public static IEnumerable<CellBox> Cut(CellBox box, int edge)
    {
        var end = box.End;
        for (var z = FloorTo(box.Z, edge); z < end.Z; z += edge)
        for (var y = FloorTo(box.Y, edge); y < end.Y; y += edge)
        for (var x = FloorTo(box.X, edge); x < end.X; x += edge)
        {
            int x0 = Math.Max(x, box.X), y0 = Math.Max(y, box.Y), z0 = Math.Max(z, box.Z);
            yield return new CellBox(x0, y0, z0, Math.Min(x + edge, end.X) - x0, Math.Min(y + edge, end.Y) - y0, Math.Min(z + edge, end.Z) - z0);
        }
    }

    public static int FloorTo(int v, int edge) => v - (int)((uint)v % (uint)edge);   // v is never negative here

    public bool AnyAddressed(CellBox part)
    {
        for (var z = part.Z; z < part.Z + part.SizeZ; z++)
        for (var y = part.Y; y < part.Y + part.SizeY; y++)
        {
            var row = Region.Index(part.X - Region.X, y - Region.Y, z - Region.Z);
            if (Addressed.AsSpan(row, part.SizeX).Contains(true)) return true;
        }
        return false;
    }

    /// <summary>The 8-cell blocks (their part inside the region) that hold an addressed cell.</summary>
    public IEnumerable<CellBox> Blocks()
    {
        foreach (var b in Cut(Region, 8))
            if (AnyAddressed(b)) yield return b;
    }

    /// <summary>A block's name in the ledger: its corner on the 8-cell grid.</summary>
    public static string BlockKey(CellBox part) => $"{FloorTo(part.X, 8)},{FloorTo(part.Y, 8)},{FloorTo(part.Z, 8)}";

    /// <summary>The whole 8-cell block <paramref name="part"/> lies in.</summary>
    public static CellBox WholeBlock(CellBox part) => new(FloorTo(part.X, 8), FloorTo(part.Y, 8), FloorTo(part.Z, 8), 8, 8, 8);

    /// <summary>The fingerprint of the cells of <paramref name="part"/> taken out of <paramref name="cells"/>, the cells of
    /// <paramref name="box"/>: SHA-256 of the values, first 8 bytes.</summary>
    public static string Fingerprint(CellBox box, ReadOnlySpan<Voxel> cells, CellBox part)
    {
        Span<byte> buf = stackalloc byte[16];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var z = part.Z; z < part.Z + part.SizeZ; z++)
        for (var y = part.Y; y < part.Y + part.SizeY; y++)
        {
            var row = box.Index(part.X - box.X, y - box.Y, z - box.Z);
            for (var x = 0; x < part.SizeX; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buf, cells[row + x].Raw);
                hash.AppendData(buf[..2]);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 8));
    }

    /// <summary>The cells two boxes share; empty when none.</summary>
    public static CellBox Intersect(CellBox a, CellBox b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), z0 = Math.Max(a.Z, b.Z);
        int x1 = Math.Min(a.X + a.SizeX, b.X + b.SizeX), y1 = Math.Min(a.Y + a.SizeY, b.Y + b.SizeY), z1 = Math.Min(a.Z + a.SizeZ, b.Z + b.SizeZ);
        return x1 <= x0 || y1 <= y0 || z1 <= z0 ? default : new CellBox(x0, y0, z0, x1 - x0, y1 - y0, z1 - z0);
    }
}
