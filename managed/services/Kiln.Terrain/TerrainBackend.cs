// SPDX-License-Identifier: MIT
// Everything Kiln.Terrain asks of the world, behind one interface: the real one calls Kiln.Build, Kiln.Saves and
// Kiln.Spawn as the calling mod; tests and the test kit's fake put an in-memory world behind it.
namespace Kiln;

internal interface ITerrainBackend
{
    /// <summary>The calling mod's id.</summary>
    string Mod { get; }
    /// <summary>The world runs (its save is loaded, so the ledger is this world's).</summary>
    bool WorldRunning { get; }
    string? WorldId { get; }
    /// <summary>Grid size in cells (x, z), or null while unknown.</summary>
    (int X, int Z)? GridCells { get; }
    string KilnVersion { get; }
    DataMatch LoadedData => DataMatch.Exact;
    Task NextTick();
    Task<bool> Load(CellBox box, TimeSpan timeout);
    bool TryRead(CellBox box, out Voxel[] cells);
    /// <summary>One write of <paramref name="cells"/> where <paramref name="mask"/> is set.</summary>
    Task<BuildResult> Write(CellBox box, Voxel[] cells, bool[] mask, bool inBases, bool transient, string label, TimeSpan timeout, bool dryRun = false);
    /// <summary>The mod's book in this world: a live object, saved with the world. <see cref="Save"/> stores it after a
    /// plan was added or removed.</summary>
    TerrainBook Book();
    void Save(TerrainBook book);
}

internal sealed class BuildTerrainBackend(ModContext mod) : ITerrainBackend
{
    public string Mod => mod.Info.Id;
    private readonly long _session = Lifecycle.ServerSession;
    public bool WorldRunning => mod.Alive && _session == Lifecycle.ServerSession && Lifecycle.Phase is not (ServerPhase.Stopping or ServerPhase.Idle) &&
        (Spawn.Available ? Spawn.WorldRunning : Build.Grid is not null);
    public DataMatch LoadedData => WorldData.Loaded;
    // A new world's GUID can be assigned by its first save. Its process session is stable across that save,
    // and WorldRunning invalidates this backend when the session ends. WorldData selects the persistent GUID.
    public string? WorldId => $"session:{_session}";
    public (int X, int Z)? GridCells => Build.Grid is { } g ? (g.CellsX, g.CellsZ) : null;
    public string KilnVersion => Game.KilnVersion;
    public async Task NextTick() => await mod.Scheduler.NextTick();
    public Task<bool> Load(CellBox box, TimeSpan timeout) => Build.Load(box, timeout);
    public bool TryRead(CellBox box, out Voxel[] cells) => Build.TryRead(box, out cells);

    public async Task<BuildResult> Write(CellBox box, Voxel[] cells, bool[] mask, bool inBases, bool transient, string label, TimeSpan timeout, bool dryRun = false)
    {
        var flags = BuildFlags.None;
        if (inBases) flags |= BuildFlags.InBases;
        if (transient) flags |= BuildFlags.Transient;
        if (dryRun) flags |= BuildFlags.DryRun;
        // no journal: an overlay is not a session edit to undo, and its cells would sit in the journal twice
        if (!await Build.Load(box, timeout) || !WorldRunning)
            return new BuildResult(0, 0, false, false, box, 0, BuildRefusal.NotLoaded);
        return Build.Write(box, cells, new BuildOptions { Flags = flags, Journal = false, Label = label, LoadTimeout = timeout }, mask);
    }

    public TerrainBook Book()
    {
        if (!WorldData.Contains(TerrainBook.Key)) return WorldData.Get<TerrainBook>(TerrainBook.Key);
        if (!WorldData.TryGet<TerrainBook>(TerrainBook.Key, out var book))
            throw new InvalidDataException("terrain ledger is null; stored data was kept");
        return book;
    }
    public void Save(TerrainBook book) => WorldData.Set(TerrainBook.Key, book);
}
