// SPDX-License-Identifier: MIT
// Public value types of Kiln.Build. Usage: docs/kiln/services/build.md.
using System.Numerics;

namespace Kiln;

/// <summary>One voxel cell as the game stores it (a 0.5 m cube on svn 1285601): <see cref="Material"/> and
/// <see cref="Fill"/> 0..255 (255 = solid; values between place the surface inside the cell; 0 = no matter). Terrain
/// and building blocks are both voxels: materials 1..127 are terrain, 128..255 building-block materials (the material
/// a block item places, <see cref="Build.MaterialOf"/>). The game leaves a surface's material on the air cells next to
/// it (fill 0, material set).</summary>
public readonly record struct Voxel(ushort Raw)
{
    public byte Material => (byte)(Raw & 0xFF);
    public byte Fill => (byte)(Raw >> 8);
    public bool IsAir => Fill == 0;
    public bool IsSolid => Fill == 255;
    /// <summary>A building-block material (128..255).</summary>
    public bool IsBuilding => Material >= 128;

    public static Voxel Air => default;

    /// <summary>A cell of <paramref name="material"/> filled to <paramref name="fill"/> (255 solid).</summary>
    public static Voxel Of(byte material, byte fill = 255) => new((ushort)(fill << 8 | material));

    public override string ToString() => Raw == 0 ? "air" : $"m{Material}/{Fill}";
}

/// <summary>Cell coordinates (integer index into the voxel grid; see <see cref="Build.CellOf"/>).</summary>
public readonly record struct CellPos(int X, int Y, int Z)
{
    public static CellPos operator +(CellPos a, CellPos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CellPos operator -(CellPos a, CellPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public override string ToString() => $"{X},{Y},{Z}";
}

/// <summary>A box of cells: low corner <c>(X, Y, Z)</c> and size. Cells inside are x fastest, then y, then z:
/// <c>index = (z * SizeY + y) * SizeX + x</c>.</summary>
public readonly record struct CellBox(int X, int Y, int Z, int SizeX, int SizeY, int SizeZ)
{
    public CellPos Min => new(X, Y, Z);

    /// <summary>The cell just outside the high corner.</summary>
    public CellPos End => new(X + SizeX, Y + SizeY, Z + SizeZ);

    public long Count => (long)SizeX * SizeY * SizeZ;
    public bool IsEmpty => SizeX <= 0 || SizeY <= 0 || SizeZ <= 0;

    public int Index(int x, int y, int z) => (z * SizeY + y) * SizeX + x;

    public bool Contains(CellPos p) =>
        p.X >= X && p.Y >= Y && p.Z >= Z && p.X < X + SizeX && p.Y < Y + SizeY && p.Z < Z + SizeZ;

    /// <summary>The box spanning two corner cells (both included, any order).</summary>
    public static CellBox FromCorners(CellPos a, CellPos b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), z0 = Math.Min(a.Z, b.Z);
        return new CellBox(x0, y0, z0, Math.Max(a.X, b.X) - x0 + 1, Math.Max(a.Y, b.Y) - y0 + 1, Math.Max(a.Z, b.Z) - z0 + 1);
    }

    public CellBox Offset(CellPos d) => this with { X = X + d.X, Y = Y + d.Y, Z = Z + d.Z };

    public override string ToString() => $"{X},{Y},{Z} +{SizeX}x{SizeY}x{SizeZ}";
}

/// <summary>The voxel grid's geometry: world metres of cell (0,0,0)'s low corner and the cell size.</summary>
public sealed record VoxelGrid(double CellSize, WorldPosition Origin, int CellsX, int CellsZ, int TileCells, int Level)
{
    public double SizeX => CellsX * CellSize;
    public double SizeZ => CellsZ * CellSize;
}

/// <summary>How a Kiln edit may act. Kiln's edits never pass the game's building programs, so kiln.gameplay's
/// protection zones do not apply unless <see cref="RespectZones"/> is set; player bases are protected unless
/// <see cref="InBases"/>.</summary>
[Flags]
public enum BuildFlags
{
    None = 0,
    /// <summary>Refuse when a <c>Gameplay.Protect</c> zone denies the <see cref="BuildOptions.Actor"/> building or
    /// terraforming there (for edits made on a player's behalf).</summary>
    RespectZones = 1 << 0,
    /// <summary>Allow touching a player base's build zone.</summary>
    InBases = 1 << 1,
    /// <summary>Props: allow templates that found a base (flame altars).</summary>
    AllowAltars = 1 << 2,
    /// <summary>Write even where the voxel tiles are not streamed in (cells around the edit may read as air).</summary>
    Unloaded = 1 << 3,
    /// <summary>Compute and report; change nothing.</summary>
    DryRun = 1 << 4,
    /// <summary>Do not keep the edit across restarts (the game's own rule for terrain outside bases).</summary>
    Transient = 1 << 5,
}

/// <summary>Optional settings of an edit.</summary>
public sealed class BuildOptions
{
    public BuildFlags Flags { get; init; }

    /// <summary>The player the edit is for (zones with <see cref="BuildFlags.RespectZones"/>, journal owner, events);
    /// none = Kiln itself.</summary>
    public EntityId Actor { get; init; }

    /// <summary>Record the edit in the session journal so <see cref="Build.Undo"/> can revert it. Default true.</summary>
    public bool Journal { get; init; } = true;

    /// <summary>How long an async edit waits for the voxel tiles to stream in. Default 20 s.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Shown in the journal and events ("paste outpost", "/build dig").</summary>
    public string? Label { get; init; }

    public static BuildOptions Default { get; } = new();
}

/// <summary>Why an edit changed nothing.</summary>
public enum BuildRefusal
{
    None = 0,
    /// <summary>A protection zone denies the actor (<see cref="BuildFlags.RespectZones"/>).</summary>
    Zone,
    /// <summary>The box touches a player base (no <see cref="BuildFlags.InBases"/>).</summary>
    Base,
    /// <summary>The voxel tiles did not stream in in time.</summary>
    NotLoaded,
    /// <summary><see cref="BuildFlags.DryRun"/>.</summary>
    DryRun,
    /// <summary>A <see cref="BuildEditingEvent"/> handler canceled it.</summary>
    Canceled,
}

/// <summary>What an edit did.</summary>
/// <param name="CellsChanged">Cells whose value changed.</param>
/// <param name="BlocksWritten">8x8x8 blocks the engine committed.</param>
/// <param name="Verified">The cells read back equal to what was written.</param>
/// <param name="Kept">The touched chunks are in Kiln's save regions (the game saves them).</param>
/// <param name="Box">The cells the edit covered.</param>
/// <param name="Zone">The denying zone (<see cref="BuildRefusal.Zone"/>) or base altar (<see cref="BuildRefusal.Base"/>).</param>
public sealed record BuildResult(long CellsChanged, int BlocksWritten, bool Verified, bool Kept, CellBox Box,
    double Milliseconds, BuildRefusal Refusal = BuildRefusal.None, uint Zone = 0) : IKilnResult
{
    public bool Changed => CellsChanged > 0 && Refusal == BuildRefusal.None;
    public bool Refused => Refusal is not (BuildRefusal.None or BuildRefusal.DryRun);

    /// <summary>The edit was not refused (<see cref="Refused"/> is false): it ran, or was a dry run. An edit that ran
    /// and found nothing to change is still ok; <see cref="Changed"/> says whether cells changed. Since 0.2.0-dev.27.</summary>
    public bool Ok => !Refused;

    /// <summary>Why the edit was refused, in words (<see cref="Refusal"/> is the code); null when <see cref="Ok"/>.
    /// Since 0.2.0-dev.27.</summary>
    public string? Reason => Refusal switch
    {
        BuildRefusal.None or BuildRefusal.DryRun => null,
        BuildRefusal.Zone => $"a protection zone denies the edit (zone {Zone})",
        BuildRefusal.Base => $"the box touches a player base (altar {Zone})",
        BuildRefusal.NotLoaded => "the voxel tiles did not stream in in time",
        BuildRefusal.Canceled => "a BuildEditingEvent handler canceled the edit",
        _ => Refusal.ToString(),
    };
}

/// <summary>An edit Kiln could not run at all (service unavailable, bad box, engine fault).</summary>
public sealed class BuildException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

public enum ShapeKind
{
    Box = 1,
    Sphere = 2,
    /// <summary>Vertical cylinder.</summary>
    Cylinder = 3,
}

/// <summary>A region for shape edits, in world metres.</summary>
/// <param name="Half">Box: half extents. Sphere: radius in X. Cylinder: radius in X, half height in Y.</param>
public readonly record struct BuildShape(ShapeKind Kind, WorldPosition Center, Vector3 Half)
{
    public static BuildShape Box(WorldPosition center, double halfX, double halfY, double halfZ) =>
        new(ShapeKind.Box, center, new Vector3((float)halfX, (float)halfY, (float)halfZ));

    public static BuildShape Sphere(WorldPosition center, double radius) =>
        new(ShapeKind.Sphere, center, new Vector3((float)radius, (float)radius, (float)radius));

    public static BuildShape Cylinder(WorldPosition center, double radius, double halfHeight) =>
        new(ShapeKind.Cylinder, center, new Vector3((float)radius, (float)halfHeight, (float)radius));
}

/// <summary>The shape operations of <see cref="Build"/>.</summary>
public enum ShapeOp
{
    Dig = 1,
    Add = 2,
    Flatten = 3,
    Smooth = 4,
    Paint = 5,
    Replace = 6,
}

[Flags]
public enum PropFlags
{
    None = 0,
    /// <summary>Has UsedItem: a building prop (placed by a player, or by Kiln with an item); dismantling gives it back.</summary>
    Placed = 1,
    /// <summary>A flame altar (BaseIdComponent).</summary>
    Altar = 2,
    /// <summary>Its template is saved by the game.</summary>
    Saved = 4,
}

/// <summary>Which entities <see cref="Build.Props"/> returns. Creatures, villagers and players never.</summary>
[Flags]
public enum PropFilter
{
    /// <summary>Building props only (UsedItem): what players placed with the build hammer.</summary>
    Placed = 1,
    /// <summary>Only templates the game saves.</summary>
    Saved = 2,
    /// <summary>Every static entity, loose pickups included.</summary>
    All = 4,
    /// <summary>Any static entity the game saves except pickups (props of a point of interest, decorations).</summary>
    Static = 0,
}

/// <summary>A prop in the world.</summary>
public sealed record PlacedProp(EntityId Entity, Guid Template, string TemplateName, WorldPosition Position, Quaternion Rotation,
    Vector3 Scale, uint ItemId, TemplateKind Kind, PropFlags Flags);

/// <summary>A player base: the game's own build zone around a flame altar (BuildZoneSystem).</summary>
public sealed record PlayerBase(EntityId Altar, WorldPosition Center, Vector3 Half, bool Abandoned)
{
    public bool Contains(WorldPosition p) =>
        Math.Abs(p.X - Center.X) <= Half.X && Math.Abs(p.Y - Center.Y) <= Half.Y && Math.Abs(p.Z - Center.Z) <= Half.Z;
}

/// <summary>kiln.build's state (any thread).</summary>
public sealed record BuildStatus(bool Read, bool Write, bool Props, bool World, bool Persist, ulong Reads, ulong Writes,
    ulong CellsRead, ulong CellsWritten, ulong BlocksWritten, ulong PropsPlaced, ulong PropsRemoved, ulong Refused,
    ulong KeptChunks, ulong SavesSeen, string Detail);
