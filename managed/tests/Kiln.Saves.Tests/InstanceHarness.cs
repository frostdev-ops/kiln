// SPDX-License-Identifier: MIT
// The real Kiln.Saves store (SavesRuntime.Store), its generations on disk in a test folder, the public InstanceData API, and the
// fake game behind IInstanceWorld. Only the native saves table is absent: the runtime is marked active by reflection.
using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln;

[CollectionDefinition("instance data", DisableParallelization = true)]
public sealed class InstanceDataCollection;

public sealed class Kills
{
    public int Count { get; set; }
    public string? Tag { get; set; }
}

public sealed class Note
{
    public string Text { get; set; } = "";
}

internal sealed class InstanceHarness : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo State = typeof(SavesRuntime).GetField("_state", BindingFlags.NonPublic | BindingFlags.Static)!;
    public static readonly Guid ChestTemplate = Guid.Parse("0000c4e5-0000-0000-0000-000000000001");
    public static readonly WorldPosition A = new(10, 0, 10), B = new(20, 0, 10);
    public static readonly CharacterKey Hero = new(0x1122334455667788, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000001"));

    public readonly string Root = Path.Combine(AppContext.BaseDirectory, "instance-tests", Guid.NewGuid().ToString("N"));
    public FakeInstanceWorld World;
    public readonly ModContext Mod = TestMods.Create("sword-mod");
    public InstanceKey<Kills> Kills;
    public readonly List<KilnEvent> Events = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly int _stateBefore;

    public static WorldStore Store => SavesRuntime.Store;

    public InstanceHarness(string worldId = "instances-unit")
    {
        _stateBefore = (int)State.GetValue(null)!;
        SavesRuntime.EndSession();
        State.SetValue(null, 1);  // the runtime counts as started; the native table stays absent
        Directory.CreateDirectory(Path.Combine(Root, "manifests"));
        Directory.CreateDirectory(Path.Combine(Root, "blobs"));
        OpenFolder(Root, worldId);
        World = new FakeInstanceWorld();
        Store.Instances.World = World;
        Store.Instances.StartMatching();
        Kills = Declare<Kills>("kills");
        _subscriptions.Add(Kiln.Events.On<InstanceDataRestoredEvent>(Events.Add));
        _subscriptions.Add(Kiln.Events.On<InstanceDataLostEvent>(Events.Add));
        _subscriptions.Add(Kiln.Events.On<InstanceDataParkedEvent>(Events.Add));
    }

    public static void OpenFolder(string root, string worldId)
    {
        typeof(WorldStore).GetProperty("Root")!.SetValue(Store, root);
        typeof(WorldStore).GetProperty("WorldId")!.SetValue(Store, worldId);
        typeof(WorldStore).GetField("_opened", Hidden)!.SetValue(Store, true);
    }

    public InstanceKey<T> Declare<T>(string name, InstanceKeyOptions? options = null, ModContext? mod = null) where T : class, new() =>
        TestMods.As(mod ?? Mod, () => InstanceData.Key<T>(name, options));

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        SavesRuntime.EndSession();
        Store.Instances.World = null;
        State.SetValue(null, _stateBefore);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the mod's calls ----
    public T Get<T>(ItemInstanceRef item, InstanceKey<T> key) where T : class, new() => TestMods.As(Mod, () => item.Get(key));

    public Kills Get(ItemInstanceRef item) => Get(item, Kills);

    public int? Count(ItemInstanceRef item) => TestMods.As(Mod, () => item.TryGet(Kills, out var k) ? k.Count : (int?)null);

    public void Tag(ItemInstanceRef item, int count) => TestMods.As(Mod, () => item.Set(Kills, new Kills { Count = count, Tag = $"#{count}" }));

    // ---- saves and restarts ----
    public void Save(uint revision)
    {
        Store.Stage(revision, SaveReason.Request, late: false);
        Store.Commit(revision, 1000 + revision, SaveReason.Request);
    }

    /// <summary>Only the staging of a save (the process dies before the world file is written, or before Kiln confirms it).</summary>
    public void StageOnly(uint revision) => Store.Stage(revision, SaveReason.Request, late: false);

    /// <summary>A new process loads the world file of <paramref name="fileRevision"/> (and file time): the store reads the
    /// generation that belongs to it, the game's items come back with new ids, and the first tick matches.</summary>
    public DataMatch Restart(uint fileRevision, FakeInstanceWorld world, uint? unix = null, bool tick = true)
    {
        World = world;
        Store.Instances.World = world;
        typeof(WorldStore).GetMethod("Load", Hidden)!.Invoke(Store, [fileRevision, unix ?? 1000 + fileRevision]);
        Store.Instances.StartMatching();
        if (tick) Store.Instances.Tick();
        return Store.Match;
    }

    public void Tick() => Store.Instances.Tick();

    /// <summary>Past the 10-minute window of world holders.</summary>
    public void EndWindow()
    {
        World.Step += InstanceStore.MatchWindowSteps + 1;
        Store.Instances.Tick();
    }

    public JsonObject? Bundle(uint revision, string scope)
    {
        var manifest = Directory.EnumerateFiles(Path.Combine(Root, "manifests"), $"{revision:D10}*.json").Single();
        var entry = JsonNode.Parse(File.ReadAllText(manifest))!["bundles"]![scope];
        if (entry is null) return null;
        var sha = entry["sha256"]!.GetValue<string>();
        return JsonNode.Parse(File.ReadAllBytes(Store.BlobPath(sha)))!.AsObject();
    }

    public IReadOnlyList<string> Scopes(uint revision)
    {
        var manifest = Directory.EnumerateFiles(Path.Combine(Root, "manifests"), $"{revision:D10}*.json").Single();
        return ((JsonObject)JsonNode.Parse(File.ReadAllText(manifest))!["bundles"]!).Select(kv => kv.Key).ToList();
    }

    public List<T> Of<T>() where T : KilnEvent => Events.OfType<T>().ToList();

    public IReadOnlyList<ParkedInstance<Kills>> Parked() => TestMods.As(Mod, () => InstanceData.Parked(Kills));
}
