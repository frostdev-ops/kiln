// SPDX-License-Identifier: MIT
// The kit's fake behind Kiln.Terrain. Kiln.Terrain has no native table: it reaches the world through one internal seam
// (Terrain.BackendFactory), and this fake puts an in-memory voxel world of the running harness behind it. So the
// library's own code runs offline: lowering, batches, the ledger's rules, conflicts, events.
namespace Kiln.TestKit;

/// <summary>One harness's terrain: sparse cells over flat ground, bases, every mod's ledger book, and what was asked.</summary>
internal sealed class TerrainFakeState
{
    public readonly object Lock = new();
    public int GroundY = 100;
    public byte GroundMaterial = 1;
    public string WorldId = "fake-world";
    public bool Loaded = true;
    public readonly Dictionary<(int, int, int), Voxel> Cells = new();
    public readonly List<CellBox> Bases = [];
    public readonly Dictionary<string, TerrainBook> Books = new(StringComparer.Ordinal);
    public readonly List<FakeTerrainWrite> Writes = [];

    public Voxel Get(int x, int y, int z) =>
        Cells.TryGetValue((x, y, z), out var v) ? v : y < GroundY ? Voxel.Of(GroundMaterial) : Voxel.Air;
}

internal sealed class TerrainFake : IKitFake
{
    /// <summary>Not a native service: the name of the library's own row, which is what a test switches off.</summary>
    public string Service => "kiln.terrain";

    public void Register() { }

    public void Start(KilnHarness harness)
    {
        var state = KitFakes.State<TerrainFakeState>(harness);
        Terrain.BackendFactory = () =>
        {
            // a row the test switched off (Capabilities.Override): the member throws as it does in a game without it
            foreach (var row in (ReadOnlySpan<string>)["kiln.terrain", "terrain.overlays"])
                if (FakeTables.Off(row)) throw new KilnUnavailableException(row, "switched off by the test");
            return new Backend(harness, state, HostAccess.Current);
        };
    }

    public void BeforeStep(KilnHarness harness, ulong step) { }

    public void End(KilnHarness harness) => Terrain.BackendFactory = null;

    public void AddCoverage(List<CoverageRow> rows) =>
        rows.Add(new CoverageRow("Terrain", CoverageKind.RecordingFake, ["kiln.terrain", "terrain.overlays", "terrain.baked", "terrain.biomes"],
            "Kiln.Terrain's own code on an in-memory world (KilnHarness.Terrain): flat ground, the cells a test sets, base boxes, and one ledger per mod and " +
            "world id. Inspect, Apply, Forget and Applied run as in the game: batches, the ledger's rules, conflicts, Terrain.Changed. Not modelled: streaming " +
            "(tiles are loaded unless the test says not), the game's save (the ledger lives in the harness, not in the fake kiln.saves), kiln.build's own events, " +
            "and time: from code that is not a mod's, every batch runs in the same step. terrain.baked and terrain.biomes are off, as in the game."));

    private sealed class Backend(KilnHarness harness, TerrainFakeState s, ModContext? mod) : ITerrainBackend
    {
        public string Mod => mod?.Info.Id ?? "kiln";
        public bool WorldRunning => true;
        public string? WorldId { get { lock (s.Lock) return s.WorldId; } }
        public (int X, int Z)? GridCells => (20480, 20480);
        public string KilnVersion => "testkit";

        public async Task NextTick()
        {
            // as a mod: the mod's next tick, so one batch a step; as Kiln (KilnHarness.Run) there is no scheduler to wait on
            if (mod is not null) await mod.Scheduler.NextTick();
        }

        public Task<bool> Load(CellBox box, TimeSpan timeout) { lock (s.Lock) return Task.FromResult(s.Loaded); }

        public bool TryRead(CellBox box, out Voxel[] cells)
        {
            lock (s.Lock)
            {
                cells = new Voxel[box.Count];
                for (var z = 0; z < box.SizeZ; z++)
                for (var y = 0; y < box.SizeY; y++)
                for (var x = 0; x < box.SizeX; x++)
                    cells[box.Index(x, y, z)] = s.Get(box.X + x, box.Y + y, box.Z + z);
                return s.Loaded;
            }
        }

        public Task<BuildResult> Write(CellBox box, Voxel[] cells, bool[] mask, bool inBases, bool transient, string label, TimeSpan timeout, bool dryRun = false)
        {
            lock (s.Lock)
            {
                if (!inBases && s.Bases.Any(b => !TerrainPlan.Intersect(b, box).IsEmpty))
                    return Task.FromResult(new BuildResult(0, 0, false, false, box, 0, BuildRefusal.Base));
                if (dryRun) return Task.FromResult(new BuildResult(0, 0, false, false, box, 0, BuildRefusal.DryRun));
                long changed = 0;
                for (var z = 0; z < box.SizeZ; z++)
                for (var y = 0; y < box.SizeY; y++)
                for (var x = 0; x < box.SizeX; x++)
                {
                    var i = box.Index(x, y, z);
                    if (!mask[i]) continue;
                    var at = (box.X + x, box.Y + y, box.Z + z);
                    if (s.Get(at.Item1, at.Item2, at.Item3) != cells[i]) changed++;
                    s.Cells[at] = cells[i];
                }
                s.Writes.Add(new FakeTerrainWrite(Mod, label, box, changed, transient, harness.CurrentStep));
                return Task.FromResult(new BuildResult(changed, 0, true, !transient, box, 0));
            }
        }

        public TerrainBook Book()
        {
            lock (s.Lock)
            {
                var key = s.WorldId + "|" + Mod;
                if (!s.Books.TryGetValue(key, out var b)) s.Books[key] = b = new TerrainBook();
                return b;
            }
        }

        public void Save(TerrainBook book) { lock (s.Lock) s.Books[s.WorldId + "|" + Mod] = book; }
    }
}
