// SPDX-License-Identifier: MIT
// An in-memory voxel world behind ITerrainBackend: flat ground, sparse edits, player bases, one ledger book per mod,
// and a record of every write. Several mods share one world through For(mod).
using Kiln;

namespace TerrainTests;

internal sealed class MemoryWorld
{
    public const int GroundY = 100;         // cells below are stone (material 1), above air
    public string WorldId = "world-a";
    public bool Running = true;
    public bool Loaded = true;
    public int Ticks;
    public readonly Dictionary<(int, int, int), Voxel> Edits = new();
    public readonly List<CellBox> Bases = [];
    public readonly Dictionary<string, TerrainBook> Books = new();
    public readonly List<(string Mod, CellBox Box, int Masked, int Tick, bool Transient, bool DryRun)> Writes = [];
    /// <summary>Called before each real write; return a refusal to refuse it.</summary>
    public Func<CellBox, BuildRefusal>? RefuseWrite;
    /// <summary>Called at every NextTick (a test edits the world or cancels between batches).</summary>
    public Action? OnTick;
    public Action? AfterWrite;
    /// <summary>Called inside each write where kiln.build posts BuildEditingEvent: after the overlay read the cells and
    /// decided, before the guard compares them and the write commits. A test makes another mod's edit here.</summary>
    public Action<CellBox>? Editing;
    /// <summary>Writes the guard refused (<see cref="BuildRefusal.Changed"/>).</summary>
    public int Stale;
    public Action? OnLoad;
    public Exception? BookError;
    public DataMatch Match = DataMatch.Exact;

    public Voxel Get(int x, int y, int z) => Edits.TryGetValue((x, y, z), out var v) ? v : y < GroundY ? Voxel.Of(1) : Voxel.Air;
    public void Set(int x, int y, int z, Voxel v) => Edits[(x, y, z)] = v;

    public ITerrainBackend For(string mod) => new Backend(this, mod);

    private sealed class Backend(MemoryWorld w, string mod) : ITerrainBackend
    {
        public string Mod => mod;
        public bool WorldRunning => w.Running;
        public string? WorldId => w.WorldId;
        public (int X, int Z)? GridCells => (20480, 20480);
        public string KilnVersion => "test";
        public DataMatch LoadedData => w.Match;

        public Task NextTick()
        {
            w.Ticks++;
            w.OnTick?.Invoke();
            return Task.CompletedTask;
        }

        public Task<bool> Load(CellBox box, TimeSpan timeout)
        {
            w.OnLoad?.Invoke();
            return Task.FromResult(w.Loaded);
        }

        public bool TryRead(CellBox box, out Voxel[] cells)
        {
            cells = new Voxel[box.Count];
            for (var z = 0; z < box.SizeZ; z++)
            for (var y = 0; y < box.SizeY; y++)
            for (var x = 0; x < box.SizeX; x++)
                cells[box.Index(x, y, z)] = w.Get(box.X + x, box.Y + y, box.Z + z);
            return w.Loaded;
        }

        public Task<BuildResult> Write(CellBox box, Voxel[] cells, bool[] mask, bool inBases, bool transient, string label, TimeSpan timeout, bool dryRun = false,
            Voxel[]? expected = null)
        {
            w.Writes.Add((mod, box, mask.Count(m => m), w.Ticks, transient, dryRun));
            w.Editing?.Invoke(box);
            // the guard, as Build.WriteIfUnchanged: every masked cell must still be what the overlay read
            if (expected is not null)
                for (var z = 0; z < box.SizeZ; z++)
                for (var y = 0; y < box.SizeY; y++)
                for (var x = 0; x < box.SizeX; x++)
                {
                    var i = box.Index(x, y, z);
                    if (!mask[i] || w.Get(box.X + x, box.Y + y, box.Z + z) == expected[i]) continue;
                    w.Stale++;
                    return Task.FromResult(new BuildResult(0, 0, false, false, box, 0, BuildRefusal.Changed));
                }
            if (!inBases && w.Bases.Any(b => !TerrainPlan.Intersect(b, box).IsEmpty))
                return Task.FromResult(new BuildResult(0, 0, false, false, box, 0, BuildRefusal.Base));
            if (w.RefuseWrite?.Invoke(box) is { } r and not BuildRefusal.None)
                return Task.FromResult(new BuildResult(0, 0, false, false, box, 0, r));
            if (dryRun) return Task.FromResult(new BuildResult(0, 0, false, false, box, 0, BuildRefusal.DryRun));
            long changed = 0;
            for (var z = 0; z < box.SizeZ; z++)
            for (var y = 0; y < box.SizeY; y++)
            for (var x = 0; x < box.SizeX; x++)
            {
                var i = box.Index(x, y, z);
                if (!mask[i]) continue;
                if (w.Get(box.X + x, box.Y + y, box.Z + z) != cells[i]) changed++;
                w.Set(box.X + x, box.Y + y, box.Z + z, cells[i]);
            }
            w.AfterWrite?.Invoke();
            return Task.FromResult(new BuildResult(changed, 0, true, !transient, box, 0));
        }

        public TerrainBook Book()
        {
            if (w.BookError is { } error) throw error;
            var key = w.WorldId + "|" + mod;
            if (!w.Books.TryGetValue(key, out var b)) w.Books[key] = b = new TerrainBook();
            return b;
        }

        public void Save(TerrainBook book) => w.Books[w.WorldId + "|" + mod] = book;
    }
}
