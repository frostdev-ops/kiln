// SPDX-License-Identifier: MIT
// Public value types of Kiln.Build. Usage: docs/kiln/services/build.md.
using System.Numerics;

namespace Kiln;

/// <summary>One voxel cell as the game stores it (a 0.5 m cube on svn 1285601): <see cref="Material"/> and
/// <see cref="Fill"/> 0..255 (255 = solid; values between place the surface inside the cell; 0 = no matter). Terrain
/// and building blocks are both voxels: materials 1..127 are terrain, 128..255 building-block materials (the material
/// a block item places, <see cref="Build.MaterialOf"/>). The game leaves a surface's material on the air cells next to
/// it (fill 0, material set).</summary>
/// <param name="Raw">The engine's own 16-bit cell value: the material in the low byte and the fill in the high byte
/// (<c>material | fill &lt;&lt; 8</c>).</param>
public readonly record struct Voxel(ushort Raw)
{
    /// <summary>The material id (the low byte of <see cref="Raw"/>): 1..127 terrain, 128..255 a building-block material;
    /// 0 is no material.</summary>
    public byte Material => (byte)(Raw & 0xFF);

    /// <summary>How much matter the cell holds (the high byte of <see cref="Raw"/>): 255 is solid, 0 is none, and values
    /// between place the surface inside the cell.</summary>
    public byte Fill => (byte)(Raw >> 8);

    /// <summary>True when <see cref="Fill"/> is 0, whatever the material (the game leaves a surface's material on the air
    /// cells next to it). Only the fill byte is tested: a building block's fill is paint (see <see cref="BlockInfo"/>),
    /// so use <see cref="Build.BlockAt"/> to tell air from an unpainted building block.</summary>
    public bool IsAir => Fill == 0;

    /// <summary>True when <see cref="Fill"/> is 255, a fully solid cell.</summary>
    public bool IsSolid => Fill == 255;

    /// <summary>A building-block material (128..255).</summary>
    public bool IsBuilding => Material >= 128;

    /// <summary>The empty cell: material 0 and fill 0 (<see cref="Raw"/> 0).</summary>
    public static Voxel Air => default;

    /// <summary>A cell of <paramref name="material"/> filled to <paramref name="fill"/> (255 solid).</summary>
    public static Voxel Of(byte material, byte fill = 255) => new((ushort)(fill << 8 | material));

    /// <summary><c>air</c> for a raw value of 0, otherwise <c>m&lt;material&gt;/&lt;fill&gt;</c>, for example
    /// <c>m143/255</c>.</summary>
    public override string ToString() => Raw == 0 ? "air" : $"m{Material}/{Fill}";
}

/// <summary>Cell coordinates (integer index into the voxel grid; see <see cref="Build.CellOf"/>).</summary>
/// <param name="X">The cell index along x.</param>
/// <param name="Y">The cell index along y, the vertical axis.</param>
/// <param name="Z">The cell index along z.</param>
public readonly record struct CellPos(int X, int Y, int Z)
{
    /// <summary>Adds two cell positions component by component.</summary>
    public static CellPos operator +(CellPos a, CellPos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/> component by component.</summary>
    public static CellPos operator -(CellPos a, CellPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>The position as <c>x,y,z</c>.</summary>
    public override string ToString() => $"{X},{Y},{Z}";
}

/// <summary>A box of cells: low corner <c>(X, Y, Z)</c> and size. Cells inside are x fastest, then y, then z:
/// <c>index = (z * SizeY + y) * SizeX + x</c>.</summary>
/// <param name="X">The x index of the low corner cell.</param>
/// <param name="Y">The y index of the low corner cell.</param>
/// <param name="Z">The z index of the low corner cell.</param>
/// <param name="SizeX">The number of cells along x.</param>
/// <param name="SizeY">The number of cells along y.</param>
/// <param name="SizeZ">The number of cells along z.</param>
public readonly record struct CellBox(int X, int Y, int Z, int SizeX, int SizeY, int SizeZ)
{
    /// <summary>The low corner cell, <c>(X, Y, Z)</c>.</summary>
    public CellPos Min => new(X, Y, Z);

    /// <summary>The cell just outside the high corner.</summary>
    public CellPos End => new(X + SizeX, Y + SizeY, Z + SizeZ);

    /// <summary>The number of cells in the box: <c>SizeX * SizeY * SizeZ</c>.</summary>
    public long Count => (long)SizeX * SizeY * SizeZ;

    /// <summary>True when a size is zero or negative, so the box holds no cells. The edit calls refuse an empty box.</summary>
    public bool IsEmpty => SizeX <= 0 || SizeY <= 0 || SizeZ <= 0;

    /// <summary>Where the cell at <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/> sits in an array of the
    /// box's cells (x fastest, then y, then z). The three are counted from the box's low corner, not cell coordinates,
    /// and are not checked against the size.</summary>
    public int Index(int x, int y, int z) => (z * SizeY + y) * SizeX + x;

    /// <summary>Whether the cell <paramref name="p"/> (cell coordinates) lies inside the box. The cell at
    /// <see cref="End"/> is outside.</summary>
    public bool Contains(CellPos p) =>
        p.X >= X && p.Y >= Y && p.Z >= Z && p.X < X + SizeX && p.Y < Y + SizeY && p.Z < Z + SizeZ;

    /// <summary>The box spanning two corner cells (both included, any order).</summary>
    public static CellBox FromCorners(CellPos a, CellPos b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y), z0 = Math.Min(a.Z, b.Z);
        return new CellBox(x0, y0, z0, Math.Max(a.X, b.X) - x0 + 1, Math.Max(a.Y, b.Y) - y0 + 1, Math.Max(a.Z, b.Z) - z0 + 1);
    }

    /// <summary>The same box moved by <paramref name="d"/> cells; the size stays.</summary>
    public CellBox Offset(CellPos d) => this with { X = X + d.X, Y = Y + d.Y, Z = Z + d.Z };

    /// <summary>The box as <c>x,y,z +SizeXxSizeYxSizeZ</c>, for example <c>10,4,10 +8x8x8</c>.</summary>
    public override string ToString() => $"{X},{Y},{Z} +{SizeX}x{SizeY}x{SizeZ}";
}

/// <summary>The voxel grid's geometry: world metres of cell (0,0,0)'s low corner and the cell size.</summary>
/// <param name="CellSize">The edge length of a cell in metres (0.5 on the current build).</param>
/// <param name="Origin">The world position of cell (0, 0, 0)'s low corner, in metres.</param>
/// <param name="CellsX">The grid's extent in cells along x: valid cells are below it (20480 on the current builds).</param>
/// <param name="CellsZ">The grid's extent in cells along z: valid cells are below it (20480 on the current builds).</param>
/// <param name="TileCells">The size of a streaming tile in cells along x (the game streams 128 m tiles around players
/// and bases).</param>
/// <param name="Level">The engine's edit level of the voxel pyramid that the cells belong to (6 on the current build, which
/// makes them 0.5 m).</param>
public sealed record VoxelGrid(double CellSize, WorldPosition Origin, int CellsX, int CellsZ, int TileCells, int Level)
{
    /// <summary>The grid's extent along x in metres: <see cref="CellsX"/> times <see cref="CellSize"/>.</summary>
    public double SizeX => CellsX * CellSize;

    /// <summary>The grid's extent along z in metres: <see cref="CellsZ"/> times <see cref="CellSize"/>.</summary>
    public double SizeZ => CellsZ * CellSize;
}

/// <summary>How a Kiln edit may act. Kiln's edits never pass the game's building programs, so kiln.gameplay's
/// protection zones do not apply unless <see cref="RespectZones"/> is set; player bases are protected unless
/// <see cref="InBases"/>.</summary>
[Flags]
public enum BuildFlags
{
    /// <summary>No flag: protection zones are not checked, an edit touching a player base is refused, the edit goes only
    /// where the voxel tiles are loaded, and it is saved with the world.</summary>
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
    /// <summary>Gone after the next restart: no save region of Kiln's, and where the game's save writes the cells anyway
    /// (inside a base, or in a chunk another edit keeps) Kiln puts back the earlier cells at the next start; a cell changed
    /// since keeps that change. Not with <see cref="Unloaded"/> (<see cref="ArgumentException"/>); refused
    /// (<see cref="BuildException"/>) when the transient journal is full or cannot be written (a full disk), or
    /// the world's identity is unknown (no kiln.saves).</summary>
    Transient = 1 << 5,
}

/// <summary>Optional settings of an edit.</summary>
public sealed class BuildOptions
{
    /// <summary>How the edit may act (<see cref="BuildFlags"/>). Default none.</summary>
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

    /// <summary>The options a call uses when it is given none: no flags, no actor, journaled, the default load timeout and
    /// no label.</summary>
    public static BuildOptions Default { get; } = new();
}

/// <summary>Why an edit changed nothing.</summary>
public enum BuildRefusal
{
    /// <summary>The edit was not refused.</summary>
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
    /// <summary><see cref="Build.WriteIfUnchanged"/>: when the edit was about to commit, the world no longer held the
    /// expected cells (a <see cref="BuildEditingEvent"/> handler, or another edit since the caller read them, changed
    /// them). Nothing was written. Since 0.2.0-dev.28.</summary>
    Changed,
}

/// <summary>What an edit did.</summary>
/// <param name="CellsChanged">Cells whose value changed.</param>
/// <param name="BlocksWritten">8x8x8 blocks the engine committed.</param>
/// <param name="Verified">The cells read back equal to what was written.</param>
/// <param name="Kept">The touched chunks are in Kiln's save regions (the game saves them).</param>
/// <param name="Box">The cells the edit covered.</param>
/// <param name="Milliseconds">The time the engine's writer took, in milliseconds.</param>
/// <param name="Refusal">Why the edit changed nothing; <see cref="BuildRefusal.None"/> when it was not refused.
/// <see cref="BuildRefusal.DryRun"/> is reported here too but does not count as <see cref="Refused"/>.</param>
/// <param name="Zone">The denying zone (<see cref="BuildRefusal.Zone"/>) or base altar (<see cref="BuildRefusal.Base"/>).</param>
public sealed record BuildResult(long CellsChanged, int BlocksWritten, bool Verified, bool Kept, CellBox Box,
    double Milliseconds, BuildRefusal Refusal = BuildRefusal.None, uint Zone = 0) : IKilnResult
{
    /// <summary>True when cells changed and the edit was not refused or a dry run (<see cref="CellsChanged"/> is above
    /// zero and <see cref="Refusal"/> is <see cref="BuildRefusal.None"/>).</summary>
    public bool Changed => CellsChanged > 0 && Refusal == BuildRefusal.None;

    /// <summary>True when the edit was refused: <see cref="Refusal"/> is anything but <see cref="BuildRefusal.None"/> or
    /// <see cref="BuildRefusal.DryRun"/>.</summary>
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
        BuildRefusal.Changed => "the cells changed after they were read (a BuildEditingEvent handler or another edit); nothing was written",
        _ => Refusal.ToString(),
    };
}

/// <summary>An edit Kiln could not run at all (service unavailable, bad box, engine fault).</summary>
public sealed class BuildException(string message, int code = 0) : Exception(message)
{
    /// <summary>The kiln.build result code behind the failure; 0 when the exception did not come from one.</summary>
    public int Code { get; } = code;
}

/// <summary>The geometry of a <see cref="BuildShape"/>.</summary>
public enum ShapeKind
{
    /// <summary>An axis-aligned box; <see cref="BuildShape.Half"/> holds its half extents.</summary>
    Box = 1,
    /// <summary>A sphere; <see cref="BuildShape.Half"/> holds its radius in X.</summary>
    Sphere = 2,
    /// <summary>Vertical cylinder.</summary>
    Cylinder = 3,
}

/// <summary>A region for shape edits, in world metres.</summary>
/// <param name="Kind">The geometry of the shape.</param>
/// <param name="Center">The centre of the shape in world metres.</param>
/// <param name="Half">Box: half extents. Sphere: radius in X. Cylinder: radius in X, half height in Y.</param>
public readonly record struct BuildShape(ShapeKind Kind, WorldPosition Center, Vector3 Half)
{
    /// <summary>A box around <paramref name="center"/> with these half extents (metres).</summary>
    public static BuildShape Box(WorldPosition center, double halfX, double halfY, double halfZ) =>
        new(ShapeKind.Box, center, new Vector3((float)halfX, (float)halfY, (float)halfZ));

    /// <summary>A sphere around <paramref name="center"/> with this radius (metres).</summary>
    public static BuildShape Sphere(WorldPosition center, double radius) =>
        new(ShapeKind.Sphere, center, new Vector3((float)radius, (float)radius, (float)radius));

    /// <summary>A vertical cylinder around <paramref name="center"/> with this radius and half height (metres).</summary>
    public static BuildShape Cylinder(WorldPosition center, double radius, double halfHeight) =>
        new(ShapeKind.Cylinder, center, new Vector3((float)radius, (float)halfHeight, (float)radius));
}

/// <summary>The shape operations of <see cref="Build"/>.</summary>
public enum ShapeOp
{
    /// <summary>Removes terrain and blocks inside the shape (<see cref="Build.Dig"/>).</summary>
    Dig = 1,
    /// <summary>Fills the shape with a material (<see cref="Build.Add"/>).</summary>
    Add = 2,
    /// <summary>Levels the shape's footprint to a height (<see cref="Build.Flatten"/>).</summary>
    Flatten = 3,
    /// <summary>Blurs the surface inside the shape (<see cref="Build.Smooth"/>).</summary>
    Smooth = 4,
    /// <summary>Gives the non-air cells inside the shape a material, keeping their fill (<see cref="Build.Paint"/>).</summary>
    Paint = 5,
    /// <summary>Turns cells of one material inside the shape into another (<see cref="Build.Replace"/>).</summary>
    Replace = 6,
}

/// <summary>What a prop is (<see cref="PlacedProp.Flags"/>).</summary>
[Flags]
public enum PropFlags
{
    /// <summary>None of the flags below.</summary>
    None = 0,
    /// <summary>Has UsedItem: a building prop (placed by a player, or by Kiln with an item); dismantling gives it back.</summary>
    Placed = 1,
    /// <summary>A flame altar (BaseIdComponent).</summary>
    Altar = 2,
    /// <summary>Its template is saved by the game.</summary>
    Saved = 4,
}

/// <summary>Which entities <see cref="Build.Props(WorldPosition, WorldPosition, PropFilter)"/> returns. Creatures, villagers and players never.</summary>
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
/// <param name="Entity">The prop's entity.</param>
/// <param name="Template">The GUID of the prop's template.</param>
/// <param name="TemplateName">The name of the prop's template.</param>
/// <param name="Position">The prop's position in world metres.</param>
/// <param name="Rotation">The prop's orientation.</param>
/// <param name="Scale">The prop's scale along x, y and z.</param>
/// <param name="ItemId">The item its UsedItem names, which dismantling gives back; 0 when it has none.</param>
/// <param name="Kind">The template's kind (kiln.spawn's).</param>
/// <param name="Flags">What the prop is: placed, a flame altar, saved by the game.</param>
public sealed record PlacedProp(EntityId Entity, Guid Template, string TemplateName, WorldPosition Position, Quaternion Rotation,
    Vector3 Scale, uint ItemId, TemplateKind Kind, PropFlags Flags);

/// <summary>A player base: the game's own build zone around a flame altar (BuildZoneSystem).</summary>
/// <param name="Altar">The flame altar entity that owns the zone.</param>
/// <param name="Center">The centre of the build zone in world metres.</param>
/// <param name="Half">Half the zone's size along x, y and z, in metres.</param>
/// <param name="Abandoned">True when the game marks the base as abandoned.</param>
public sealed record PlayerBase(EntityId Altar, WorldPosition Center, Vector3 Half, bool Abandoned)
{
    /// <summary>Whether a world position lies inside the zone: within <see cref="Half"/> of <see cref="Center"/> on all
    /// three axes, the edges included.</summary>
    public bool Contains(WorldPosition p) =>
        Math.Abs(p.X - Center.X) <= Half.X && Math.Abs(p.Y - Center.Y) <= Half.Y && Math.Abs(p.Z - Center.Z) <= Half.Z;
}

/// <summary>kiln.build's state (any thread).</summary>
/// <param name="Read">Voxel reads work on this build.</param>
/// <param name="Write">Voxel writes work on this build.</param>
/// <param name="Props">Prop placement works on this build.</param>
/// <param name="World">The server world runs and the voxel world was found.</param>
/// <param name="Persist">Kiln's save regions are hooked into the game's save, so Kiln's edits are saved.</param>
/// <param name="Reads">Voxel reads served since kiln.build started.</param>
/// <param name="Writes">Voxel writes done since kiln.build started.</param>
/// <param name="CellsRead">Cells read since kiln.build started.</param>
/// <param name="CellsWritten">Cells whose value a write changed since kiln.build started.</param>
/// <param name="BlocksWritten">8x8x8 blocks committed since kiln.build started.</param>
/// <param name="PropsPlaced">Props placed since kiln.build started.</param>
/// <param name="PropsRemoved">Props removed since kiln.build started.</param>
/// <param name="Refused">Edits and prop placements that were refused or failed since kiln.build started.</param>
/// <param name="KeptChunks">The 16 m chunks in Kiln's save regions.</param>
/// <param name="SavesSeen">World saves whose region list Kiln extended.</param>
/// <param name="Detail">A note on why something is off, in words (for example that the voxel world is not initialized
/// yet). Diagnostic text, not a stable code.</param>
public sealed record BuildStatus(bool Read, bool Write, bool Props, bool World, bool Persist, ulong Reads, ulong Writes,
    ulong CellsRead, ulong CellsWritten, ulong BlocksWritten, ulong PropsPlaced, ulong PropsRemoved, ulong Refused,
    ulong KeptChunks, ulong SavesSeen, string Detail);
