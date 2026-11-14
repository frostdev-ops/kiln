// SPDX-License-Identifier: MIT
// Public types of Kiln.Terrain. The contract: docs/kiln/services/terrain.md.
namespace Kiln;

/// <summary>Whether an overlay is saved with the world.</summary>
public enum TerrainPersistence
{
    /// <summary>Saved with the world and recorded in the ledger: applied once per world.</summary>
    Kept = 0,
    /// <summary>Does not add a save region or ledger record; may be applied again whenever the mod asks. Cells can
    /// still be saved inside a base or a chunk kept by another edit. This does not guarantee a reset on restart.</summary>
    Transient = 1,
}

/// <summary>How a <see cref="TerrainHeightfield"/> meets the ground that is there.</summary>
public enum TerrainHeightMode
{
    /// <summary>Below the height the column becomes matter, above it air, up to the region's top.</summary>
    Replace = 0,
    /// <summary>Only cells that hold less matter than the patch wants are written; nothing is dug.</summary>
    AddOnTop = 1,
}

/// <summary>What a palette number of a patch stands for.</summary>
/// <param name="Material">0 = air (the cell is dug out); 1 to 116 a terrain material of the game; 128 to 212 a
/// building-block material. Other values are refused: a player without mods has no look for them.</param>
/// <param name="Fill">How full the cell is, 1 to 255 (255 solid). Ignored for air.</param>
public sealed record TerrainPaletteEntry(byte Material, byte Fill = 255);

/// <summary>Where a patch's cells come from: <see cref="TerrainVoxelVolume"/> or <see cref="TerrainHeightfield"/>.</summary>
public abstract record TerrainSource
{
    private protected TerrainSource() { }
}

/// <summary>One palette number per cell of the region, x fastest, then y, then z (<see cref="CellBox.Index"/>).
/// 0 leaves the cell as it is; <c>n</c> is entry <c>n - 1</c> of the patch's palette.</summary>
public sealed record TerrainVoxelVolume(byte[] Cells) : TerrainSource;

/// <summary>One height per column of the region (x fastest, then z), in cells above the region's floor.</summary>
/// <param name="Heights"><c>SizeX * SizeZ</c> heights, each 0 to the region's <c>SizeY</c>. A fraction fills the top
/// cell partly.</param>
/// <param name="Fill">The palette number of the column's matter (not 0, not air).</param>
/// <param name="Mode">How the column meets the ground that is there.</param>
/// <param name="Surface">The palette number of the column's top cells; 0 = the same as <paramref name="Fill"/>.</param>
/// <param name="SurfaceDepth">How many top cells use <paramref name="Surface"/>.</param>
public sealed record TerrainHeightfield(float[] Heights, byte Fill, TerrainHeightMode Mode = TerrainHeightMode.Replace,
    byte Surface = 0, int SurfaceDepth = 0) : TerrainSource;

/// <summary>A bounded patch of ground: an ore vein, a mound, a tunnel. The same shape in JSON:
/// <see cref="FromJson"/>.</summary>
/// <param name="Id">The plan's name inside the mod: 1 to 64 characters of a-z, 0-9, '.', '_', '-'.</param>
/// <param name="Version">1 or higher. A world that has the plan at this version is left alone; a higher version is
/// applied only where the ground still is what the earlier version left.</param>
/// <param name="Region">The cells the patch may touch: at most 256 per axis and 1,048,576 in all.</param>
/// <param name="Source">The cells.</param>
/// <param name="Palette">What the source's numbers mean (number <c>n</c> is entry <c>n - 1</c>).</param>
/// <param name="Persistence">Saved and recorded, or not.</param>
/// <remarks>Removing a plan from a mod does not restore the ground: nothing can delete a saved edit.</remarks>
public sealed record TerrainPatch(string Id, int Version, CellBox Region, TerrainSource Source,
    IReadOnlyList<TerrainPaletteEntry> Palette, TerrainPersistence Persistence = TerrainPersistence.Kept)
{
    /// <summary>Reads a patch from its JSON shape (docs/kiln/services/terrain.md).</summary>
    /// <exception cref="ArgumentException">The text is not a patch: bad JSON, an unknown property or source kind, a
    /// missing field, a wrong length.</exception>
    [AnyThread]
    public static TerrainPatch FromJson(string json) => TerrainJson.Parse(json);

    /// <summary>The patch as JSON; <see cref="FromJson"/> reads it back equal.</summary>
    [AnyThread]
    public string ToJson() => TerrainJson.Write(this);
}

/// <summary>How <see cref="Terrain.Apply"/> runs.</summary>
public sealed class TerrainApplyOptions
{
    /// <summary>Cells written per tick at most (one batch a tick): 512 to 262,144. Default 32,768.</summary>
    public int CellsPerTick { get; init; } = 32768;

    /// <summary>Write inside player bases too. Default false: a block in a base is left alone and listed as a
    /// conflict.</summary>
    public bool InBases { get; init; }

    /// <summary>How long a batch waits for its voxel tiles to stream in. Default 20 s.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Stops the patch between two batches; the batches already written stay.</summary>
    public CancellationToken Cancel { get; init; }

    /// <summary>Told after every batch, on the tick thread. Progress is not a change event.</summary>
    public IProgress<TerrainProgress>? Progress { get; init; }
}

/// <summary>How far an <see cref="Terrain.Apply"/> is.</summary>
public readonly record struct TerrainProgress(int Batches, int BatchCount, long ChangedCells);

/// <summary>How an overlay call ended.</summary>
public enum TerrainState
{
    /// <summary>Every batch is written.</summary>
    Completed = 0,
    /// <summary>The world has this plan at this version: nothing was read or written.</summary>
    AlreadyApplied = 1,
    /// <summary>Written except in the blocks listed as conflicts; those were left alone.</summary>
    Conflict = 2,
    /// <summary>Stopped part way (canceled, tiles did not load, a write was refused): the batches counted are in the
    /// world.</summary>
    Partial = 3,
    /// <summary>Nothing was written.</summary>
    Refused = 4,
}

/// <summary>Why a block was not written.</summary>
public enum TerrainConflictKind
{
    /// <summary>The block no longer is what the plan's earlier version left: someone changed it.</summary>
    PlayerEdit = 0,
    /// <summary>Another mod's kept plan addresses the block.</summary>
    OtherPlan = 1,
    /// <summary>The block lies in a player base (<see cref="TerrainApplyOptions.InBases"/> is off).</summary>
    BaseZone = 2,
}

/// <summary>One block an overlay left alone.</summary>
/// <param name="Block">The block's cells (8 per axis, on the world's 8-cell grid).</param>
/// <param name="Owner">For <see cref="TerrainConflictKind.OtherPlan"/>: "mod/plan".</param>
public sealed record TerrainConflict(TerrainConflictKind Kind, CellBox Block, string? Owner = null);

/// <summary>What <see cref="Terrain.Apply"/> did. It is never all-or-nothing: a patch is spread over ticks.</summary>
/// <param name="ChangedCells">Cells whose value changed, in the batches that were written.</param>
/// <param name="Batches">Batches written to the world by this call.</param>
public sealed record TerrainResult(bool Ok, string? Reason, TerrainState State, string Plan, int Version, long ChangedCells,
    int Batches, IReadOnlyList<TerrainConflict> Conflicts) : IKilnResult;

/// <summary>What <see cref="Terrain.Inspect"/> found. Nothing was written.</summary>
/// <param name="Box">The patch's region.</param>
/// <param name="Cells">Cells the patch addresses.</param>
/// <param name="Blocks">8-cell blocks the patch addresses.</param>
/// <param name="Batches">Ticks an apply takes at least.</param>
/// <param name="EstimatedBytes">What the save grows by at most (1,024 bytes a block, before the game packs them).</param>
/// <param name="Would">How an apply would end now.</param>
/// <param name="Conflicts">The blocks an apply would leave alone.</param>
public sealed record TerrainPlanReport(bool Ok, string? Reason, CellBox Box, long Cells, int Blocks, int Batches,
    long EstimatedBytes, TerrainState Would, IReadOnlyList<TerrainConflict> Conflicts) : IKilnResult;

/// <summary>A kept plan of the calling mod in this world (<see cref="Terrain.Applied"/>).</summary>
/// <param name="Complete">False while an apply is under way or was interrupted.</param>
/// <param name="Blocks">Blocks the plan owns.</param>
/// <param name="GivenUp">Blocks the plan gave up after a conflict.</param>
public sealed record TerrainPlanInfo(string Plan, int Version, CellBox Region, bool Complete, int Blocks, int GivenUp,
    DateTimeOffset AppliedAt);

/// <summary>One batch of an overlay reached the world.</summary>
/// <remarks>Tick thread, after the write and the ledger update. A refusal raises nothing.</remarks>
public sealed class TerrainChangedEvent
{
    internal TerrainChangedEvent(string mod, string plan, int version, CellBox box, long cellsChanged, int batch, int batchCount, bool last)
    {
        Mod = mod;
        Plan = plan;
        Version = version;
        Box = box;
        CellsChanged = cellsChanged;
        Batch = batch;
        BatchCount = batchCount;
        Last = last;
    }

    /// <summary>The mod that owns the plan.</summary>
    public string Mod { get; }
    public string Plan { get; }
    public int Version { get; }

    /// <summary>The cells of this batch.</summary>
    public CellBox Box { get; }
    public long CellsChanged { get; }

    /// <summary>This batch's number, from 1.</summary>
    public int Batch { get; }
    public int BatchCount { get; }

    /// <summary>The patch's last batch.</summary>
    public bool Last { get; }
}
