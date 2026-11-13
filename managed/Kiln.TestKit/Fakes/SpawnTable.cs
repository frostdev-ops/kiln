// SPDX-License-Identifier: MIT
// The fake kiln.spawn (native/core/abi/services/kiln_spawn.h, v1 with its appended capabilities()) under the real Kiln.Spawn.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_spawn.h</c> v1 (with the appended request fields and <c>capabilities()</c>). The sizes are
/// pinned by tests to Kiln.Spawn's private mirrors.</summary>
internal static unsafe class SpawnAbi
{
    public const string Service = "kiln.spawn";
    public const uint Version = 1;
    public const int RequestV1Size = 104;
    public const uint NoTrack = 1, Transient = 2, AllowUnknown = 4;
    public const uint WorldNone = 0, WorldLoading = 1, WorldRunning = 2;
    public const uint TrackedLive = 0, TrackedMissing = 1;
    public const uint SourceShipped = 1, SourceEngine = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Guid16
    {
        public fixed byte B[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Request
    {
        public uint Size, Flags;
        public Guid16 Template;
        public NativeStr TemplateName;
        public Vec3 Position;
        public float Yaw;
        public int Level;
        public NativeStr Tag, Origin;
        public void* Components;
        public uint ComponentCount, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TemplateInfo
    {
        public uint Size, Kind;
        public Guid16 Guid;
        public fixed byte Name[128];
        public fixed byte Display[64];
        public uint Source, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tracked
    {
        public uint Size, Entity;
        public ulong Serial;
        public Guid16 Template;
        public Vec3 Position, SpawnPosition;
        public long SpawnedUnix;
        public uint State, Flags;
        public fixed byte Tag[64];
        public fixed byte Origin[64];
        public fixed byte Name[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Snapshot
    {
        public uint Size, HasTemplate;
        public Guid16 Template;
        public Vec3 Position;
        public fixed float Rotation[4];
        public float Yaw;
        public uint Tracked;
        public ulong Serial;
        public long SpawnTime;
        public uint HasSpawnTime, Parent;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Cleanup
    {
        public uint Size, Destroyed, Missing, Kept;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Request*, uint*, int> Spawn;
        public delegate* unmanaged[Cdecl]<uint, uint, Vec3, int, uint, NativeStr, NativeStr, uint*, int> SpawnItem;
        public delegate* unmanaged[Cdecl]<uint, int> Destroy;
        public delegate* unmanaged[Cdecl]<uint, Snapshot*, int> EntityInfo;
        public delegate* unmanaged[Cdecl]<Guid16*, NativeStr, Vec3, double, uint*, nuint, nuint*, int> Find;
        public delegate* unmanaged[Cdecl]<NativeStr, TemplateInfo*, int> TemplateFind;
        public delegate* unmanaged[Cdecl]<Guid16*, TemplateInfo*, int> TemplateGet;
        public delegate* unmanaged[Cdecl]<nuint> TemplateCount;
        public delegate* unmanaged[Cdecl]<nuint, TemplateInfo*, int> TemplateAt;
        public delegate* unmanaged[Cdecl]<nuint> TrackedCount;
        public delegate* unmanaged[Cdecl]<nuint, Tracked*, int> TrackedAt;
        public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, Cleanup*, int> Cleanup;
        public delegate* unmanaged[Cdecl]<uint*, int> Refresh;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> WorldKey;
        public delegate* unmanaged[Cdecl]<uint> WorldState;
        public delegate* unmanaged[Cdecl]<uint> Capabilities;
    }

    public static Guid ReadGuid(Guid16* g) => new(new ReadOnlySpan<byte>(g->B, 16));

    public static void WriteGuid(Guid id, Guid16* g) => id.TryWriteBytes(new Span<byte>(g->B, 16));

    public static void CopyText(string text, byte* dst, int capacity)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var n = Math.Min(bytes.Length, capacity - 1);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--;  // never half a character
        for (var i = 0; i < n; i++) dst[i] = bytes[i];
        dst[n] = 0;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.spawn</c> table. Modelled: spawn, spawn_item, destroy, entity_info,
/// find, the template reads, the tracked list, cleanup, refresh, world_key, world_state. capabilities() is 0: extra spawn
/// components (<c>spawn.components</c>) are not modelled.</summary>
internal static unsafe class SpawnTable
{
    private static SpawnAbi.Table* _table;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (SpawnAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(SpawnAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(SpawnAbi.Table), Version = SpawnAbi.Version };
        t->Spawn = &Spawn;
        t->SpawnItem = &SpawnItem;
        t->Destroy = &Destroy;
        t->EntityInfo = &EntityInfo;
        t->Find = &Find;
        t->TemplateFind = &TemplateFind;
        t->TemplateGet = &TemplateGet;
        t->TemplateCount = &TemplateCount;
        t->TemplateAt = &TemplateAt;
        t->TrackedCount = &TrackedCount;
        t->TrackedAt = &TrackedAt;
        t->Cleanup = &Cleanup;
        t->Refresh = &Refresh;
        t->WorldKey = &WorldKey;
        t->WorldState = &WorldState;
        t->Capabilities = &Capabilities;
        _table = t;
        return (nint)t;
    }

    private static SpawnFake? Fake => FakeTables.Active(SpawnAbi.Service)?.SpawnFake;

    // The tick functions: off the tick thread WRONG_THREAD, with no harness (or switched off) UNAVAILABLE.
    private static int Tick(out SpawnFake fake)
    {
        fake = null!;
        if (Fake is not { } f) return FakeTables.Unavailable;
        if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
        fake = f;
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Spawn(SpawnAbi.Request* req, uint* out_)
    {
        try
        {
            if (out_ != null) *out_ = 0;
            if (req == null || req->Size < SpawnAbi.RequestV1Size) return FakeTables.InvalidArgument;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            var guid = SpawnAbi.ReadGuid(&req->Template);
            var name = req->TemplateName.ToString();
            var p = new WorldPosition(req->Position.X, req->Position.Y, req->Position.Z);
            rc = f.Spawn(guid, name, p, req->Yaw, req->Level, req->Flags, req->Tag.ToString(), req->Origin.ToString(), 0, 0, out var id);
            if (out_ != null) *out_ = id;
            return rc;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SpawnItem(uint itemId, uint amount, SpawnAbi.Vec3 position, int level, uint flags, NativeStr tag, NativeStr origin, uint* out_)
    {
        try
        {
            if (out_ != null) *out_ = 0;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            if (itemId == 0 || amount == 0 || amount > 65535) return FakeTables.InvalidArgument;
            var t = tag.ToString();
            rc = f.Spawn(SpawnFake.PickupId, null, new WorldPosition(position.X, position.Y, position.Z), 0, Math.Max(level, 0), flags,
                t.Length == 0 ? "item" : t, origin.ToString(), itemId, (int)amount, out var id);
            if (out_ != null) *out_ = id;
            return rc;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Destroy(uint entity)
    {
        try
        {
            var rc = Tick(out var f);
            return rc != FakeTables.Ok ? rc : f.Destroy(entity);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityInfo(uint entity, SpawnAbi.Snapshot* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(SpawnAbi.Snapshot)) return FakeTables.InvalidArgument;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            var size = out_->Size;
            *out_ = default;
            out_->Size = size;
            return f.Info(entity, out_);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Find(SpawnAbi.Guid16* guid, NativeStr nameFilter, SpawnAbi.Vec3 center, double radius, uint* out_, nuint capacity, nuint* count)
    {
        try
        {
            if (count != null) *count = 0;
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            if (!f.Can(SpawnFake.EntitiesFeature)) return FakeTables.Unavailable;
            var c = new WorldPosition(center.X, center.Y, center.Z);
            if (radius > 0 && !SpawnFake.Finite(c)) return FakeTables.InvalidArgument;
            var hits = f.Find(guid == null ? Guid.Empty : SpawnAbi.ReadGuid(guid), nameFilter.ToString(), c, radius);
            if (count != null) *count = (nuint)hits.Count;
            for (var i = 0; i < hits.Count && (nuint)i < capacity && out_ != null; i++) out_[i] = hits[i];
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateFind(NativeStr name, SpawnAbi.TemplateInfo* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(SpawnAbi.TemplateInfo)) return FakeTables.InvalidArgument;
            if (Fake?.FindTemplate(name.ToString()) is not { } t) return FakeTables.NotFound;
            SpawnFake.Fill(t, out_);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateGet(SpawnAbi.Guid16* guid, SpawnAbi.TemplateInfo* out_)
    {
        try
        {
            if (guid == null || out_ == null || out_->Size < (uint)sizeof(SpawnAbi.TemplateInfo)) return FakeTables.InvalidArgument;
            if (Fake?.Template(SpawnAbi.ReadGuid(guid)) is not { } t) return FakeTables.NotFound;
            SpawnFake.Fill(t, out_);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint TemplateCount()
    {
        try
        {
            return (nuint)(Fake?.Templates.Length ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateAt(nuint index, SpawnAbi.TemplateInfo* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(SpawnAbi.TemplateInfo)) return FakeTables.InvalidArgument;
            var all = Fake?.Templates ?? [];
            if (index >= (nuint)all.Length) return FakeTables.NotFound;
            SpawnFake.Fill(all[(int)index], out_);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint TrackedCount()
    {
        try
        {
            return (nuint)(Fake?.TrackedSnapshot.Length ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TrackedAt(nuint index, SpawnAbi.Tracked* out_)
    {
        try
        {
            if (out_ == null || out_->Size < (uint)sizeof(SpawnAbi.Tracked)) return FakeTables.InvalidArgument;
            var all = Fake?.TrackedSnapshot ?? [];
            if (index >= (nuint)all.Length) return FakeTables.NotFound;
            *out_ = all[(int)index];
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Cleanup(NativeStr tag, NativeStr origin, SpawnAbi.Cleanup* out_)
    {
        try
        {
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            var (destroyed, missing, kept) = f.Cleanup(tag.ToString(), origin.ToString());
            if (out_ != null)
            {
                out_->Destroyed = (uint)destroyed;
                out_->Missing = (uint)missing;
                out_->Kept = (uint)kept;
            }
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Refresh(uint* live)
    {
        try
        {
            var rc = Tick(out var f);
            if (rc != FakeTables.Ok) return rc;
            var n = f.RefreshTracked();
            if (live != null) *live = (uint)n;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WorldKey(byte* buffer, nuint capacity, nuint* outLen)
    {
        try
        {
            if (Fake is not { } f || f.WorldKey is not { } key) return FakeTables.Unavailable;
            var bytes = Encoding.UTF8.GetBytes(key);
            if (outLen != null) *outLen = (nuint)bytes.Length;
            if (buffer == null || capacity < (nuint)bytes.Length) return FakeTables.BufferTooSmall;
            for (var i = 0; i < bytes.Length; i++) buffer[i] = bytes[i];
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint WorldState()
    {
        try
        {
            return Fake?.WorldState ?? SpawnAbi.WorldNone;
        }
        catch
        {
            return SpawnAbi.WorldNone;
        }
    }

    // Extra spawn components are not modelled: the engine copies them into the new entity by the size its own types
    // have, which the kit cannot check for every component.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Capabilities() => 0;
}

/// <summary>A template the fake <c>kiln.spawn</c> knows: the generic item pickup, and what the test added.</summary>
internal sealed record FakeTemplate(Guid Id, string Name, string Display, TemplateKind Kind, uint Source);

/// <summary>One harness's kiln.spawn: the templates the game has loaded (as the test says), what Kiln spawned, its
/// tracked list, and every call a mod made. Entities live in the harness's fake world (<see cref="EntitiesFake"/>).</summary>
internal sealed unsafe class SpawnFake
{
    /// <summary>The game's generic pickup (kiln_spawn.h's GUID example), which spawn_item creates.</summary>
    public static readonly Guid PickupId = Guid.Parse("a56d2777-daf1-4164-90d4-b7b12749c723");
    public const string PickupName = "Base_LootPickup_Generic_Gravity";

    // The sigdb features kiln.spawn serves, which the kit reports as rows of its own (FakeTables.Register).
    public const string CreateFeature = "spawn.create", DestroyFeature = "spawn.destroy", EntitiesFeature = "spawn.entities",
        TemplatesFeature = "spawn.templates";

    public static readonly string[] Features = [CreateFeature, DestroyFeature, EntitiesFeature, TemplatesFeature];

    /// <summary>Kiln.Spawn's own rows: the session's (the server world runs) and the one the fake does not model.</summary>
    public const string WorldRow = "spawn.world", ComponentsRow = "spawn.components";

    public const string NoServer = "no server runs in this process now (a KilnHarness's world runs from its first step)";

    internal sealed class Spawned
    {
        public required uint Entity;
        public required FakeTemplate Template;
        public required WorldPosition SpawnPosition;
        public required float Yaw;
        public required int Level;
        public required uint Flags;
        public required string Tag, Origin;
        public required uint ItemId;
        public required int Amount;
        public required ulong Step, Serial;
        public required long SpawnTime;
        public required long SpawnedUnix;
        public WorldPosition LastPosition;
        public bool Live = true;
    }

    private readonly KilnHarness _harness;
    private readonly object _lock = new();
    private readonly List<FakeTemplate> _templates = [];
    private FakeTemplate[] _sorted = [];
    private readonly Dictionary<uint, Spawned> _byEntity = [];
    private readonly List<Spawned> _all = [];
    private readonly List<Spawned> _tracked = [];
    private SpawnAbi.Tracked[] _trackedSnapshot = [];
    private readonly List<FakeSpawnCall> _calls = [];
    private ulong _serial;

    public SpawnFake(KilnHarness harness)
    {
        _harness = harness;
        _templates.Add(new FakeTemplate(PickupId, PickupName, "Item pickup", TemplateKind.Pickup, SpawnAbi.SourceShipped | SpawnAbi.SourceEngine));
        Sort();
    }

    // ---- the test's side ----

    public FakeTemplate AddTemplate(string name, TemplateKind kind, string display, Guid id)
    {
        lock (_lock)
        {
            if (id == Guid.Empty) id = NameGuid(name);
            foreach (var t in _templates)
            {
                if (t.Id == id) throw new ArgumentException($"template {id} is the game's already ({t.Name})", nameof(id));
                if (string.Equals(t.Name, name, StringComparison.Ordinal)) throw new ArgumentException($"the game has a template {name} already", nameof(name));
            }
            var template = new FakeTemplate(id, name, display, kind, SpawnAbi.SourceEngine);
            _templates.Add(template);
            Sort();
            return template;
        }
    }

    // A stable GUID for a template the test names without one: the first 16 bytes of SHA-256 of the name, as a version 8
    // (custom) GUID, so the same name gives the same id in every run.
    private static Guid NameGuid(string name)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("kiln-testkit-template:" + name));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }

    private void Sort() =>
        _sorted = [.. _templates.OrderBy(t => t.Name.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(t => t.Id)];

    public IReadOnlyList<FakeSpawnCall> Calls
    {
        get
        {
            lock (_lock) return [.. _calls];
        }
    }

    /// <summary>What Kiln spawned that is still in the world (tick thread).</summary>
    public IReadOnlyList<Spawned> Alive
    {
        get
        {
            lock (_lock) return [.. _all.Where(s => s.Live && World is { } w && w.Exists(s.Entity))];
        }
    }

    public Spawned? Of(uint entity)
    {
        lock (_lock) return _byEntity.GetValueOrDefault(entity);
    }

    // ---- reads ----

    public FakeTemplate[] Templates
    {
        get
        {
            lock (_lock) return _sorted;
        }
    }

    public FakeTemplate? Template(Guid id)
    {
        lock (_lock) return _templates.FirstOrDefault(t => t.Id == id);
    }

    /// <summary>A template by name (exact, then ignoring case), display name (ignoring case) or GUID text.</summary>
    public FakeTemplate? FindTemplate(string name)
    {
        if (name.Length == 0) return null;
        lock (_lock)
        {
            return _sorted.FirstOrDefault(t => t.Name == name)
                   ?? _sorted.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? _sorted.FirstOrDefault(t => t.Display.Length > 0 && string.Equals(t.Display, name, StringComparison.OrdinalIgnoreCase))
                   ?? (Guid.TryParse(name, out var g) ? _templates.FirstOrDefault(t => t.Id == g) : null);
        }
    }

    public static void Fill(FakeTemplate t, SpawnAbi.TemplateInfo* out_)
    {
        var size = out_->Size;
        *out_ = default;
        out_->Size = size;
        out_->Kind = (uint)t.Kind;
        SpawnAbi.WriteGuid(t.Id, &out_->Guid);
        SpawnAbi.CopyText(t.Name, out_->Name, 128);
        SpawnAbi.CopyText(t.Display, out_->Display, 64);
        out_->Source = t.Source;
        out_->Flags = 0;  // the template's own components are not known offline (TemplateInfo.NeverSaved is null)
    }

    public SpawnAbi.Tracked[] TrackedSnapshot
    {
        get
        {
            lock (_lock) return _trackedSnapshot;
        }
    }

    public string? WorldKey => _harness.WorldFake?.Id.ToString("D");

    /// <summary>The server world runs once the harness's server has started (ServerStartedEvent): before that it loads.</summary>
    public uint WorldState => FakeTables.Off(WorldRow) ? SpawnAbi.WorldNone : _harness.State switch
    {
        HarnessState.Running => SpawnAbi.WorldRunning,
        HarnessState.Created or HarnessState.Starting => SpawnAbi.WorldLoading,
        _ => SpawnAbi.WorldNone,
    };

    /// <summary>A game feature of kiln.spawn's works unless the test switched it off.</summary>
    public bool Can(string feature) => !FakeTables.Off(feature);

    private EntitiesFake? World => _harness.EntitiesFake;

    public static bool Finite(WorldPosition p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    // ---- spawning (tick thread) ----

    public int Spawn(Guid guid, string? name, WorldPosition p, float yaw, int level, uint flags, string tag, string origin, uint itemId,
        int amount, out uint id)
    {
        id = 0;
        var step = _harness.CurrentStep;
        var kind = itemId != 0 ? FakeSpawnKind.Item : FakeSpawnKind.Entity;
        var subject = itemId != 0 ? $"item 0x{itemId:x8} x{amount}" : guid != Guid.Empty ? guid.ToString() : name ?? "";
        var rc = SpawnCore(guid, name, p, yaw, level, flags, tag, origin, itemId, amount, out id);
        Record(new FakeSpawnCall(step, ModOf(origin), kind, subject, p, tag, rc, new EntityId(id)));
        return rc;
    }

    private int SpawnCore(Guid guid, string? name, WorldPosition p, float yaw, int level, uint flags, string tag, string origin, uint itemId,
        int amount, out uint id)
    {
        id = 0;
        if (!Can(CreateFeature)) return FakeTables.Unavailable;
        if (WorldState != SpawnAbi.WorldRunning || World is not { } world) return FakeTables.Unavailable;
        var template = guid != Guid.Empty ? Template(guid) : FindTemplate(name ?? "");
        if (template is null) return FakeTables.NotFound;  // the game has no such template loaded
        if (!Finite(p) || !float.IsFinite(yaw) || level < 0 || level > 1000) return FakeTables.InvalidArgument;
        id = world.Create(p);
        WriteYaw(world, id, yaw);
        var tracked = (flags & SpawnAbi.NoTrack) == 0;
        Spawned s;
        lock (_lock)
        {
            var serial = tracked ? ++_serial : 0;
            // SpawnTime as kiln.spawn writes it: the ECS clock now (ns), plus Kiln's 1..997 ns marker for a tracked spawn
            var spawnTime = _harness.VirtualMs * 1_000_000 + (tracked ? 1 + (long)(serial % 997) : 0);
            s = new Spawned
            {
                Entity = id, Template = template, SpawnPosition = p, LastPosition = p, Yaw = yaw, Level = level, Flags = flags, Tag = tag,
                Origin = origin, ItemId = itemId, Amount = amount, Step = _harness.CurrentStep, Serial = serial, SpawnTime = spawnTime,
                SpawnedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            _byEntity[id] = s;
            _all.Add(s);
            if (tracked) _tracked.Add(s);
        }
        try
        {
            world.Set(id, "SpawnTime", BitConverter.GetBytes(s.SpawnTime), false);
        }
        catch (ArgumentException)
        {
            // Kiln.Game's types beside the tests do not know SpawnTime: the entity has no generation then
        }
        PublishTracked();
        return FakeTables.Ok;
    }

    // The rotation kiln.spawn gives a new entity: yaw about +Y, as a quaternion in CurrentTransform at 0x18.
    private static void WriteYaw(EntitiesFake world, uint id, float yaw)
    {
        if (world.Bytes(id, "CurrentTransform") is not { Length: >= WorldAbi.TransformMinSize } bytes) return;
        var half = yaw * Math.PI / 360.0;
        BitConverter.TryWriteBytes(bytes.AsSpan(0x18), 0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x1C), (float)Math.Sin(half));
        BitConverter.TryWriteBytes(bytes.AsSpan(0x20), 0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x24), (float)Math.Cos(half));
        world.Set(id, "CurrentTransform", bytes, false);
    }

    public int Destroy(uint entity)
    {
        var step = _harness.CurrentStep;
        var rc = DestroyCore(entity);
        Record(new FakeSpawnCall(step, ModOf(null), FakeSpawnKind.Destroy, $"entity {entity}", null, Of(entity)?.Tag ?? "", rc, new EntityId(entity)));
        return rc;
    }

    private int DestroyCore(uint entity)
    {
        if (entity is >= 1 and <= 1023) return FakeTables.InvalidArgument;  // players and the engine's reserved ids
        if (!Can(DestroyFeature)) return FakeTables.Unavailable;
        if (World is not { } world || !world.Destroy(entity)) return FakeTables.NotFound;
        lock (_lock)
            if (_byEntity.TryGetValue(entity, out var s))
            {
                s.Live = false;
                _tracked.Remove(s);
            }
        PublishTracked();
        return FakeTables.Ok;
    }

    public int Info(uint entity, SpawnAbi.Snapshot* out_)
    {
        if (World is not { } world || !world.Exists(entity)) return FakeTables.NotFound;
        var mine = Of(entity) is { Live: true } s ? s : null;
        var transform = world.TransformOf(entity);
        if (mine is null && transform is null) return FakeTables.NotFound;  // neither a template nor a transform
        if (mine is not null)
        {
            out_->HasTemplate = 1;
            SpawnAbi.WriteGuid(mine.Template.Id, &out_->Template);
            if (mine.Serial != 0 && (mine.Flags & SpawnAbi.NoTrack) == 0)
            {
                out_->Tracked = 1;
                out_->Serial = mine.Serial;
            }
        }
        if (transform is { } x)
        {
            out_->Position = new SpawnAbi.Vec3 { X = x.Position.X, Y = x.Position.Y, Z = x.Position.Z };
            out_->Rotation[0] = x.RX;
            out_->Rotation[1] = x.RY;
            out_->Rotation[2] = x.RZ;
            out_->Rotation[3] = x.RW;
            out_->Yaw = (float)(2.0 * Math.Atan2(x.RY, x.RW) * 180.0 / Math.PI);
        }
        if (world.Bytes(entity, "SpawnTime") is { Length: 8 } st)
        {
            out_->SpawnTime = BitConverter.ToInt64(st);
            out_->HasSpawnTime = 1;
        }
        return FakeTables.Ok;
    }

    /// <summary>Entities of a template (or whose template name contains <paramref name="filter"/>, ignoring case; empty:
    /// any entity of the world), within <paramref name="radius"/> of <paramref name="center"/> (0 or less: anywhere),
    /// nearest first.</summary>
    public List<uint> Find(Guid guid, string filter, WorldPosition center, double radius)
    {
        if (World is not { } world) return [];
        var ids = new SortedSet<uint>(world.Entities);
        foreach (var p in _harness.PlayersFake?.Present ?? [])
            if (p.InGame) ids.Add(p.Entity.Value);
        var hits = new List<(double D, uint Id)>();
        foreach (var id in ids)
        {
            var template = Of(id) is { Live: true } s ? s.Template : null;
            if (guid != Guid.Empty ? template?.Id != guid
                : filter.Length > 0 && (template is null || !template.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                continue;
            double d = 0;
            if (radius > 0)
            {
                if ((world.PositionOf(id) ?? PlayerPosition(id)) is not { } at) continue;
                d = (at.X - center.X) * (at.X - center.X) + (at.Y - center.Y) * (at.Y - center.Y) + (at.Z - center.Z) * (at.Z - center.Z);
                if (d > radius * radius) continue;
            }
            hits.Add((d, id));
        }
        return [.. hits.OrderBy(h => h.D).ThenBy(h => h.Id).Select(h => h.Id)];
    }

    private WorldPosition? PlayerPosition(uint entity) =>
        _harness.PlayersFake?.Present.FirstOrDefault(p => p.Entity.Value == entity)?.Position;

    public (int Destroyed, int Missing, int Kept) Cleanup(string tag, string origin)
    {
        var step = _harness.CurrentStep;
        int destroyed = 0, missing = 0, kept = 0;
        List<Spawned> records;
        lock (_lock) records = [.. _tracked];
        foreach (var r in records)
        {
            if ((tag.Length > 0 && r.Tag != tag) || (origin.Length > 0 && r.Origin != origin))
            {
                kept++;
                continue;
            }
            if (World is { } world && world.Destroy(r.Entity)) destroyed++;
            else missing++;
            lock (_lock)
            {
                r.Live = false;
                _tracked.Remove(r);
            }
        }
        PublishTracked();
        Record(new FakeSpawnCall(step, ModOf(null), FakeSpawnKind.Cleanup, $"tag '{tag}' origin '{origin}'", null, tag, FakeTables.Ok, EntityId.None));
        return (destroyed, missing, kept);
    }

    /// <summary>Re-checks every tracked record against the world (where it stands, whether it is there); returns how many
    /// are live.</summary>
    public int RefreshTracked()
    {
        PublishTracked();
        lock (_lock) return _trackedSnapshot.Count(r => r.State == SpawnAbi.TrackedLive);
    }

    /// <summary>The start of a step: the tracked list follows the world, as kiln.spawn re-checks it by itself, and the
    /// kit's session row follows the server.</summary>
    public void BeforeStep()
    {
        PublishTracked();
        Rows();
    }

    /// <summary>The kit's session row of kiln.spawn: available while the server world runs, pending otherwise.</summary>
    public void Rows() => FakeTables.Session(WorldRow, WorldState == SpawnAbi.WorldRunning, _harness.State == HarnessState.Running
        ? "switched off by the test" : "the server world is not running yet (its save loads first)");

    /// <summary>A harness starts: its server world loads from the first step.</summary>
    public static void Starting() => FakeTables.Session(WorldRow, false, "the server world is not running yet (its save loads first)");

    /// <summary>The harness ends: the server world is gone.</summary>
    public static void Ended() => FakeTables.Session(WorldRow, false, NoServer);

    private void PublishTracked()
    {
        var world = World;
        lock (_lock)
        {
            var list = new SpawnAbi.Tracked[_tracked.Count];
            for (var i = 0; i < _tracked.Count; i++)
            {
                var r = _tracked[i];
                var live = r.Live && world is not null && world.Exists(r.Entity);
                if (live && world!.PositionOf(r.Entity) is { } at) r.LastPosition = at;
                var t = new SpawnAbi.Tracked
                {
                    Size = (uint)sizeof(SpawnAbi.Tracked),
                    Entity = live ? r.Entity : 0,
                    Serial = r.Serial,
                    Position = new SpawnAbi.Vec3 { X = r.LastPosition.X, Y = r.LastPosition.Y, Z = r.LastPosition.Z },
                    SpawnPosition = new SpawnAbi.Vec3 { X = r.SpawnPosition.X, Y = r.SpawnPosition.Y, Z = r.SpawnPosition.Z },
                    SpawnedUnix = r.SpawnedUnix,
                    State = live ? SpawnAbi.TrackedLive : SpawnAbi.TrackedMissing,
                    Flags = r.Flags,
                };
                SpawnAbi.WriteGuid(r.Template.Id, &t.Template);
                SpawnAbi.CopyText(r.Tag, t.Tag, 64);
                SpawnAbi.CopyText(r.Origin, t.Origin, 64);
                SpawnAbi.CopyText(r.Template.Name, t.Name, 128);
                list[i] = t;
            }
            _trackedSnapshot = list;
        }
    }

    private static string? ModOf(string? origin) =>
        origin is { Length: > 0 } ? origin : HostAccess.Current?.Info.Id;

    private void Record(FakeSpawnCall call)
    {
        lock (_lock) _calls.Add(call);
    }
}
