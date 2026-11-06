// SPDX-License-Identifier: MIT
// World progress's index of the game's own data on this machine (server or client), built once per process on a
// background thread through kiln.assets (resource reads work on any thread):
//   1. places: the knowledge query db and the world scene's global chunk -> spires, shrines, roots, obelisks (well under a second);
//   2. bosses: every template and every chunk of the world scene -> bosses and root positions (a few seconds).
// Chests are looked up on demand, one chunk at a time. Nothing is written to disk.
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Kiln;

/// <summary>Where the game's resources come from: kiln.assets in the game, a folder of dumps in tests.</summary>
internal interface IGameResources
{
    bool Available { get; }
    AssetKey[] List(uint type);
    byte[]? Read(AssetKey key);
}

internal sealed class AssetResources : IGameResources
{
    public bool Available => Assets.Available;
    public AssetKey[] List(uint type) => Assets.ListResources(type);
    public byte[]? Read(AssetKey key) => Assets.ReadResource(key);
}

internal static class WorldIndex
{
    /// <summary>The source of the game's data (tests set a folder of dumps before the first use).</summary>
    internal static IGameResources Source { get; set; } = new AssetResources();

    private static readonly object Lock = new();
    private static Thread? _thread;
    private static volatile WorldCatalog? _places;
    private static volatile IReadOnlyList<Boss>? _bosses;
    private static volatile IReadOnlyList<ShroudRoot>? _roots;
    private static volatile WorldIndexState _placesState, _bossesState;
    private static double _placesSeconds, _bossesSeconds;
    private static readonly List<string> ProblemList = new();
    private static readonly ConcurrentDictionary<Guid, TemplateData?> Templates = new();
    private static SceneInfo? _scene;

    /// <summary>The world scene: its GUID, the number of regular chunks per side and the global chunk's part.</summary>
    internal sealed record SceneInfo(Guid Guid, int Width, int Global);

    public const double ChunkSize = 256;

    public static WorldIndexState PlacesState => _placesState;
    public static WorldIndexState BossesState => _bossesState;
    public static double PlacesSeconds => _placesSeconds;
    public static double BossesSeconds => _bossesSeconds;

    public static IReadOnlyList<string> Problems
    {
        get
        {
            lock (ProblemList) return ProblemList.ToArray();
        }
    }

    private static void Problem(string s)
    {
        lock (ProblemList)
            if (ProblemList.Count < 32) ProblemList.Add(s);
        QuestsInternals.Warn("world progress: " + s);
    }

    /// <summary>Spires, shrines, roots, obelisks; null until built.</summary>
    public static WorldCatalog? Places
    {
        get
        {
            EnsureStarted();
            return _places;
        }
    }

    /// <summary>The places when built, without starting the build.</summary>
    public static WorldCatalog? PlacesIfBuilt => _places;

    /// <summary>Roots with positions once the full scan ran, else the roots of <see cref="Places"/>.</summary>
    public static IReadOnlyList<ShroudRoot>? Roots => _roots ?? Places?.Roots;

    public static IReadOnlyList<Boss>? Bosses
    {
        get
        {
            EnsureStarted();
            return _bosses;
        }
    }

    /// <summary>Starts the build once (any thread); cheap afterwards.</summary>
    public static void EnsureStarted()
    {
        if (_thread is not null) return;
        lock (Lock)
        {
            if (_thread is not null) return;
            _placesState = _bossesState = WorldIndexState.Building;
            _thread = new Thread(Run) { IsBackground = true, Name = "kiln world progress index", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    /// <summary>Blocks until the places (or also the bosses) are built or failed. Not on the tick thread.</summary>
    public static bool Wait(bool bosses, TimeSpan timeout)
    {
        EnsureStarted();
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var s = bosses ? _bossesState : _placesState;
            if (s is WorldIndexState.Ready or WorldIndexState.Failed) return s == WorldIndexState.Ready;
            Thread.Sleep(20);
        }
        return false;
    }

    private static void Run()
    {
        try
        {
            if (!Source.Available)
            {
                Problem("kiln.assets is not loaded: the game's data cannot be read");
                _placesState = _bossesState = WorldIndexState.Failed;
                return;
            }
            var sw = Stopwatch.StartNew();
            var queries = ReadQueries();
            var scene = FindScene() ?? throw new InvalidDataException("no world scene (keen::SceneEntityChunkResource) in the game's data");
            _scene = scene;
            var global = Chunk(scene.Global, all: false, want: null).Placements;
            var catalog = WorldCatalogBuilder.Places(queries, global, TemplateOf, ScopeOf, SkillPointsOf, new ProblemSink());
            _places = catalog;
            _placesSeconds = sw.Elapsed.TotalSeconds;
            _placesState = WorldIndexState.Ready;
            QuestsInternals.Info($"world progress: {catalog.Spires.Count} spires, {catalog.Shrines.Count} flame shrines and sanctums, " +
                                 $"{catalog.Roots.Count} shroud roots, {catalog.Obelisks.Count} obelisks from the game's data in {_placesSeconds:0.00} s");
            try
            {
                RunBosses(scene, catalog);
            }
            catch (Exception e)
            {
                Problem($"bosses: {e.GetType().Name}: {e.Message}");
                _bossesState = WorldIndexState.Failed;
            }
        }
        catch (Exception e)
        {
            Problem($"index: {e.GetType().Name}: {e.Message}");
            if (_placesState != WorldIndexState.Ready) _placesState = WorldIndexState.Failed;
            _bossesState = WorldIndexState.Failed;
        }
    }

    private static void RunBosses(SceneInfo scene, WorldCatalog catalog)
    {
        var sw = Stopwatch.StartNew();
        var keys = Source.List(GameLayout.TemplateType);
        var all = new List<TemplateData>(keys.Length);
        foreach (var k in keys)
            if (k.Part == 0 && TemplateOf(k.Guid) is { } t)
                all.Add(t);
        var placers = WorldCatalogBuilder.BossPlacers(all);
        var placements = new List<ScenePlacement>();
        for (var part = 0; part <= scene.Global; part++)
        {
            try
            {
                placements.AddRange(Chunk(part, all: false, want: placers.ContainsKey).Placements);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException)
            {
                Problem($"chunk {part}: {e.Message}");
            }
        }
        var bosses = WorldCatalogBuilder.Bosses(placements, placers, ScopeOf);
        _roots = WorldCatalogBuilder.WithPositions(catalog.Roots, placements);
        _bosses = bosses;
        _bossesSeconds = sw.Elapsed.TotalSeconds;
        _bossesState = WorldIndexState.Ready;
        QuestsInternals.Info($"world progress: {bosses.Count} boss placements ({bosses.Count(b => b.Tracked)} with a kill record) from " +
                             $"{all.Count} templates and {scene.Global + 1} chunks in {_bossesSeconds:0.00} s");
    }

    private sealed class ProblemSink : ICollection<string>
    {
        public int Count => 0;
        public bool IsReadOnly => false;
        public void Add(string item) => Problem(item);
        public void Clear() { }
        public bool Contains(string item) => false;
        public void CopyTo(string[] array, int arrayIndex) { }
        public bool Remove(string item) => false;
        public IEnumerator<string> GetEnumerator() => Enumerable.Empty<string>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- reads ----

    private static List<QueryRecord> ReadQueries()
    {
        var keys = Source.List(GameLayout.QueryDbType);
        if (keys.Length == 0) throw new InvalidDataException("no keen::GameKnowledgeQueryResourceDb in the game's data");
        var bytes = Source.Read(keys[0]) ?? throw new InvalidDataException("the knowledge query db could not be read");
        return GameDataReader.ReadQueryDb(bytes);
    }

    /// <summary>The scene with the most chunk parts; its last part is the global chunk, the others a square grid.</summary>
    private static SceneInfo? FindScene()
    {
        var keys = Source.List(GameLayout.SceneChunkType);
        var best = keys.GroupBy(k => k.Guid).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (best is null) return null;
        var global = (int)best.Max(k => k.Part);
        var width = (int)Math.Round(Math.Sqrt(global));
        if (width * width != global) Problem($"the world scene has {global} regular chunks, not a square grid: chest lookups are off");
        return new SceneInfo(best.Key, width * width == global ? width : 0, global);
    }

    internal static TemplateData? TemplateOf(Guid guid)
    {
        if (guid == Guid.Empty) return null;
        return Templates.GetOrAdd(guid, g =>
        {
            try
            {
                var bytes = Source.Read(new AssetKey(g, GameLayout.TemplateType));
                return bytes is null ? null : GameDataReader.ReadTemplate(g, bytes);
            }
            catch (InvalidDataException e)
            {
                Problem($"template {g}: {e.Message}");
                return null;
            }
        });
    }

    private static (Guid[] Templates, List<ScenePlacement> Placements) Chunk(int part, bool all, Func<Guid, bool>? want, bool withLoot = false)
    {
        var scene = _scene ?? throw new InvalidOperationException("no scene yet");
        var bytes = Source.Read(new AssetKey(scene.Guid, GameLayout.SceneChunkType, (uint)part))
                    ?? throw new InvalidDataException($"chunk {part} of the world scene is missing");
        return GameDataReader.ReadChunk(bytes, part, all, want, withLoot);
    }

    internal static ProgressScope ScopeOf(uint id)
    {
        try
        {
            if (Knowledge.Available && Knowledge.Info(id) is { } i)
            {
                if ((i.Flags & KnowledgeObjectFlags.World) != 0)
                    return (i.Flags & KnowledgeObjectFlags.Twin) != 0 ? ProgressScope.Both : ProgressScope.World;
                if ((i.Flags & KnowledgeObjectFlags.Player) != 0) return ProgressScope.Character;
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
        }
        return ProgressScope.World;
    }

    private static int SkillPointsOf(uint id)
    {
        try
        {
            return Knowledge.Available && Knowledge.Info(id) is { } i ? i.SkillPoints : 0;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return 0;
        }
    }

    // ---- chests (on demand) ----

    private static readonly Dictionary<int, List<(ScenePlacement P, TemplateData T)>> ChestChunks = new();
    private static readonly Queue<int> ChestOrder = new();

    /// <summary>Chest placements within <paramref name="radius"/> metres (horizontally) of a point, nearest first. Reads the
    /// chunks the circle touches (kept for the last 32 chunks). Any thread; waits for the places index.</summary>
    public static List<(ScenePlacement P, TemplateData T)> ChestsNear(WorldPosition at, double radius)
    {
        EnsureStarted();
        if (!Wait(false, TimeSpan.FromSeconds(30)) || _scene is not { Width: > 0 } scene) return [];
        var found = new List<(ScenePlacement, TemplateData)>();
        var x0 = (int)Math.Floor((at.X - radius) / ChunkSize);
        var x1 = (int)Math.Floor((at.X + radius) / ChunkSize);
        var z0 = (int)Math.Floor((at.Z - radius) / ChunkSize);
        var z1 = (int)Math.Floor((at.Z + radius) / ChunkSize);
        for (var cz = Math.Max(0, z0); cz <= Math.Min(scene.Width - 1, z1); cz++)
            for (var cx = Math.Max(0, x0); cx <= Math.Min(scene.Width - 1, x1); cx++)
                foreach (var c in ChestsOf(cz * scene.Width + cx))
                    if (WorldCatalogBuilder.Horizontal(c.P.Position, at) <= radius)
                        found.Add(c);
        found.Sort((a, b) => WorldCatalogBuilder.Horizontal(a.Item1.Position, at).CompareTo(WorldCatalogBuilder.Horizontal(b.Item1.Position, at)));
        return found;
    }

    private static List<(ScenePlacement P, TemplateData T)> ChestsOf(int part)
    {
        lock (ChestChunks)
            if (ChestChunks.TryGetValue(part, out var hit))
                return hit;
        var list = new List<(ScenePlacement, TemplateData)>();
        foreach (var p in Chunk(part, all: false, want: null, withLoot: true).Placements)
            if (p.LootId != 0 && TemplateOf(p.Template) is { IsChest: true } t)
                list.Add((p, t));
        lock (ChestChunks)
        {
            if (ChestChunks.TryAdd(part, list)) ChestOrder.Enqueue(part);
            while (ChestOrder.Count > 32) ChestChunks.Remove(ChestOrder.Dequeue());
        }
        return list;
    }

    /// <summary>The chest placement of a loot container id near a point (the chunk of the point and its neighbours).</summary>
    public static (ScenePlacement P, TemplateData T)? ChestById(uint lootId, WorldPosition near)
    {
        foreach (var c in ChestsNear(near, ChunkSize))
            if (c.P.LootId == lootId) return c;
        return null;
    }
}
