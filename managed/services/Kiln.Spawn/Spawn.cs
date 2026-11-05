// SPDX-License-Identifier: MIT
// Managed API over kiln.spawn (native/core/abi/services/kiln_spawn.h). Usage: docs/kiln/services/spawn.md.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>What a template is, judged from its name.</summary>
public enum TemplateKind
{
    Unknown = 0,
    /// <summary>Enemies and animals (Enemy_*, Animal_*, FakePlayer_* ...).</summary>
    Creature = 1,
    /// <summary>Loose item pickups.</summary>
    Pickup = 2,
    /// <summary>Props (Prop_*).</summary>
    Prop = 3,
    /// <summary>Villagers. The game keeps one per template: a second copy stays idle.</summary>
    Npc = 4,
    /// <summary>Everything else (effects, attacks, spawners, buildings ...).</summary>
    Other = 5,
}

/// <summary>Spawn behaviour switches.</summary>
[Flags]
public enum SpawnFlags
{
    None = 0,
    /// <summary>Do not record the entity in Kiln's per-world list; you remove it yourself.</summary>
    NoTrack = 1,
    /// <summary>Tag the entity with the game's DoNotSave component: it is never written to the save.</summary>
    Transient = 2,
    /// <summary>Spawn a template GUID even when the game's template list could not be read.</summary>
    AllowUnknown = 4,
}

/// <summary>A spawnable template: GUID (game byte order = <see cref="Guid.ToByteArray()"/>), name and kind.</summary>
/// <param name="Id">The template GUID.</param>
/// <param name="Name">The engine's template name ("Enemy_Vukah_Melee").</param>
/// <param name="DisplayName">In-game name from Kiln's catalog ("Vukah"), or "".</param>
/// <param name="Kind">What it is, judged from the name.</param>
/// <param name="Shipped">Listed in Kiln's shipped catalog.</param>
/// <param name="LoadedByGame">Loaded by the running server (spawnable).</param>
/// <param name="NeverSaved">The template carries the game's DoNotSave tag: its entities never reach the save (null:
/// unknown on this build). False does not promise the game saves them (enemies are not saved either; see the
/// kiln.spawn docs).</param>
/// <param name="Expires">The template has a LifeTime: the game removes its entities after a while.</param>
public sealed record TemplateInfo(Guid Id, string Name, string DisplayName, TemplateKind Kind, bool Shipped, bool LoadedByGame,
    bool? NeverSaved = null, bool Expires = false)
{
    public override string ToString() => DisplayName.Length > 0 ? $"{Name} ({DisplayName})" : Name;
}

/// <summary>An entity Kiln spawned in this world (kept in <c>kiln/data/spawn/&lt;world&gt;.json</c>).</summary>
public sealed record TrackedEntity(
    ulong Serial,
    EntityId Entity,
    Guid Template,
    string TemplateName,
    WorldPosition Position,
    WorldPosition SpawnPosition,
    DateTimeOffset SpawnedAt,
    bool IsLive,
    SpawnFlags Flags,
    string Tag,
    string Origin);

/// <summary>What <see cref="Spawn.CleanupTracked"/> did.</summary>
public readonly record struct CleanupResult(int Destroyed, int Missing, int Kept);

/// <summary>An entity's template and transform at the moment of the call.</summary>
public sealed record EntityInfo(EntityId Entity, Guid? Template, string? TemplateName, WorldPosition Position, float YawDegrees,
    bool IsTracked, ulong Serial, long? SpawnTime, EntityId Parent);

/// <summary>An extra component for <see cref="SpawnOptions.Components"/>: the engine copies <see cref="Data"/> into
/// the new entity as component <see cref="Hash"/> (overriding the template's value, or adding the component).</summary>
/// <param name="Hash">The component's qualified hash, FNV-1a-32 of <c>"keen::ecs::&lt;Name&gt;"</c>
/// (<see cref="Of(string, byte[])"/> computes it).</param>
/// <param name="Data">The component's bytes; the length must equal the engine's component size, else the engine
/// refuses the spawn (<see cref="SpawnException"/>, and the game logs the expected size).</param>
public sealed record SpawnComponent(uint Hash, byte[] Data)
{
    /// <summary>A component by name (<c>"Faction"</c> or <c>"keen::ecs::Faction"</c>).</summary>
    public static SpawnComponent Of(string name, byte[] data) => new(HashOf(name), data);

    /// <summary>A component from an unmanaged value (its bytes, e.g. a struct that mirrors the engine's layout).</summary>
    public static unsafe SpawnComponent Of<T>(string name, T value) where T : unmanaged
    {
        var bytes = new byte[sizeof(T)];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in value);
        return new(HashOf(name), bytes);
    }

    /// <summary>FNV-1a-32 of <c>"keen::ecs::&lt;name&gt;"</c> (a name with "::" is hashed as given).</summary>
    public static uint HashOf(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var full = name.Contains("::", StringComparison.Ordinal) ? name : "keen::ecs::" + name;
        uint h = 0x811c9dc5;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(full)) h = (h ^ b) * 0x01000193;
        return h;
    }
}

/// <summary>Optional spawn settings.</summary>
public sealed class SpawnOptions
{
    /// <summary>Label stored with the tracked record (cleanup can select by it), e.g. "admin" or "raids".</summary>
    public string? Tag { get; init; }

    /// <summary>&gt; 0: the entity's level (OverrideLevel). 0 = the template's own.</summary>
    public int Level { get; init; }

    public SpawnFlags Flags { get; init; }

    /// <summary>Extra components given to the engine with the spawn (at most 64; one value per component). A
    /// component Kiln sets itself (CurrentTransform, OverrideLevel, DoNotSave) takes your value instead; SpawnTime is
    /// refused unless <see cref="SpawnFlags.NoTrack"/> (Kiln marks tracked spawns with it). Examples: Faction,
    /// ImpactHierarchy (the root of an impact), Impact (the program of a generic impact template). Needs a kiln.spawn
    /// build with <see cref="Spawn.SupportsComponents"/>; otherwise the spawn throws <see cref="NotSupportedException"/>.
    /// </summary>
    public IReadOnlyList<SpawnComponent>? Components { get; init; }
}

/// <summary>The game (or Kiln) refused a spawn; <see cref="Code"/> is the native result code.</summary>
public sealed class SpawnException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Creates and destroys server-world entities from templates (creatures, item drops, props), and keeps a per-world
/// list of what Kiln spawned so it can be removed again: spawned entities can persist in the save.
/// </summary>
/// <remarks>
/// Everything that touches the world runs on the tick thread (a mod's handlers, <c>OnLoad</c> and continuations after
/// <c>await</c> already do); other threads get <see cref="InvalidOperationException"/>. The tasks complete before the
/// call returns (the engine creates the entity at once); game programs see the entity from the next sim step. Entity
/// ids change after a server restart; Kiln re-finds tracked entities by itself.
/// </remarks>
public static unsafe class Spawn
{
    // ---- native layout (kiln_spawn.h) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct GuidBytes
    {
        public fixed byte B[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vec3
    {
        public double X, Y, Z;
        public Vec3(WorldPosition p) { X = p.X; Y = p.Y; Z = p.Z; }
        public WorldPosition ToPosition() => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Request
    {
        public uint Size;
        public uint Flags;
        public GuidBytes Template;
        public NativeStr TemplateName;
        public Vec3 Position;
        public float Yaw;
        public int Level;
        public NativeStr Tag;
        public NativeStr Origin;
        // appended later in v1 (KILN_SPAWN_CAP_COMPONENTS)
        public NativeComponent* Components;
        public uint ComponentCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeComponent
    {
        public uint Hash;
        public uint Flags;
        public byte* Data;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTemplate
    {
        public uint Size;
        public uint Kind;
        public GuidBytes Guid;
        public fixed byte Name[128];
        public fixed byte Display[64];
        public uint Source;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTracked
    {
        public uint Size;
        public uint Entity;
        public ulong Serial;
        public GuidBytes Template;
        public Vec3 Position;
        public Vec3 SpawnPosition;
        public long SpawnedUnix;
        public uint State;
        public uint Flags;
        public fixed byte Tag[64];
        public fixed byte Origin[64];
        public fixed byte Name[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSnapshot
    {
        public uint Size;
        public uint HasTemplate;
        public GuidBytes Template;
        public Vec3 Position;
        public fixed float Rotation[4];
        public float Yaw;
        public uint Tracked;
        public ulong Serial;
        public long SpawnTime;
        public uint HasSpawnTime;
        public uint Parent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCleanup
    {
        public uint Size;
        public uint Destroyed;
        public uint Missing;
        public uint Kept;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Request*, uint*, int> Spawn;
        public delegate* unmanaged[Cdecl]<uint, uint, Vec3, int, uint, NativeStr, NativeStr, uint*, int> SpawnItem;
        public delegate* unmanaged[Cdecl]<uint, int> Destroy;
        public delegate* unmanaged[Cdecl]<uint, NativeSnapshot*, int> EntityInfo;
        public delegate* unmanaged[Cdecl]<GuidBytes*, NativeStr, Vec3, double, uint*, nuint, nuint*, int> Find;
        public delegate* unmanaged[Cdecl]<NativeStr, NativeTemplate*, int> TemplateFind;
        public delegate* unmanaged[Cdecl]<GuidBytes*, NativeTemplate*, int> TemplateGet;
        public delegate* unmanaged[Cdecl]<nuint> TemplateCount;
        public delegate* unmanaged[Cdecl]<nuint, NativeTemplate*, int> TemplateAt;
        public delegate* unmanaged[Cdecl]<nuint> TrackedCount;
        public delegate* unmanaged[Cdecl]<nuint, NativeTracked*, int> TrackedAt;
        public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, NativeCleanup*, int> Cleanup;
        public delegate* unmanaged[Cdecl]<uint*, int> Refresh;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> WorldKey;
        public delegate* unmanaged[Cdecl]<uint> WorldState;
        // appended later in v1 (check Header.Size)
        public delegate* unmanaged[Cdecl]<uint> Capabilities;
    }

    private const int V1RequestSize = 104;

    private const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4;

    private static Table* _table;

    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.spawn", 1));

    /// <summary>True when the native service is loaded and usable on this game build.</summary>
    public static bool Available => T != null;

    // ---- spawning ----

    /// <summary>Spawns a template by name ("Enemy_Vukah_Melee"), display name ("Vukah") or GUID text at
    /// <paramref name="position"/> (metres; placed exactly there, so pick a point on the ground), turned
    /// <paramref name="yawDegrees"/> about the up axis.</summary>
    public static Task<EntityId> Entity(string template, WorldPosition position, float yawDegrees = 0, SpawnOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        if (System.Guid.TryParse(template, out var g)) return Entity(g, position, yawDegrees, options);
        return Run(() => SpawnCore(default, template, position, yawDegrees, options));
    }

    /// <summary>Spawns a template by GUID (see <see cref="TemplateInfo.Id"/>).</summary>
    public static Task<EntityId> Entity(Guid template, WorldPosition position, float yawDegrees = 0, SpawnOptions? options = null)
    {
        if (template == System.Guid.Empty) throw new ArgumentException("empty template GUID", nameof(template));
        return Run(() => SpawnCore(template, null, position, yawDegrees, options));
    }

    /// <summary>Spawns <paramref name="count"/> (1..50) copies on a circle of radius <paramref name="spread"/> around
    /// <paramref name="center"/> (the admin "spawn N enemies near a player"). Faults only if none could be spawned.</summary>
    public static Task<IReadOnlyList<EntityId>> Group(string template, WorldPosition center, int count, double spread = 4,
        SpawnOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 50);
        RequireTickThread("Spawn.Group");
        var ids = new List<EntityId>(count);
        Exception? first = null;
        for (var i = 0; i < count; i++)
        {
            var a = i * 2 * Math.PI / count;
            var r = count > 1 ? spread : 0;
            var p = new WorldPosition(center.X + Math.Cos(a) * r, center.Y, center.Z + Math.Sin(a) * r);
            var yaw = (float)(a * 180 / Math.PI + 180);  // face the centre
            try
            {
                ids.Add(System.Guid.TryParse(template, out var g)
                    ? SpawnCore(g, null, p, yaw, options)
                    : SpawnCore(default, template, p, yaw, options));
            }
            catch (Exception e) when (e is SpawnException or NotSupportedException)
            {
                first ??= e;
            }
        }
        return ids.Count == 0 && first != null
            ? Task.FromException<IReadOnlyList<EntityId>>(first)
            : Task.FromResult<IReadOnlyList<EntityId>>(ids);
    }

    /// <summary>Drops <paramref name="amount"/> (1..65535) of an item (the game's ItemId, e.g. from kiln.items) as a
    /// loose pickup at <paramref name="position"/>: the game's generic pickup with gravity, collected like loot. It
    /// falls once a player is near (the server does not simulate empty areas). Not saved by the game.</summary>
    public static Task<EntityId> Item(uint itemId, int amount, WorldPosition position, int level = 0, SpawnOptions? options = null)
    {
        if (itemId == 0) throw new ArgumentException("item id 0", nameof(itemId));
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amount, 65535);
        return Run(() =>
        {
            var t = Require("Spawn.Item");
            uint id = 0;
            int rc;
            var tagBytes = Utf8(options?.Tag ?? "item");
            var originBytes = Utf8(Origin);
            fixed (byte* tag = tagBytes)
            fixed (byte* origin = originBytes)
            {
                rc = t->SpawnItem(itemId, (uint)amount, new Vec3(position), Math.Max(level, options?.Level ?? 0),
                    (uint)(options?.Flags ?? SpawnFlags.None), new NativeStr { Ptr = tag, Len = (nuint)tagBytes.Length },
                    new NativeStr { Ptr = origin, Len = (nuint)originBytes.Length }, &id);
            }
            Check(rc, $"item {itemId} x{amount}");
            if (Events.HasSubscribers<EntitySpawnedEvent>())
                Events.Post(new EntitySpawnedEvent(new EntityId(id), SpawnedKind.Item, null, null, position, itemId, amount, options?.Tag, Origin));
            return new EntityId(id);
        });
    }

    /// <summary>True when this kiln.spawn honours <see cref="SpawnOptions.Components"/>.</summary>
    public static bool SupportsComponents
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(Table)) return false;
            return (t->Capabilities() & 1) != 0;
        }
    }

    private static EntityId SpawnCore(Guid guid, string? name, WorldPosition p, float yaw, SpawnOptions? options)
    {
        var t = Require("Spawn.Entity");
        var comps = options?.Components;
        if (comps is { Count: > 0 })
        {
            if (!SupportsComponents)
                throw new NotSupportedException("Spawn: SpawnOptions.Components needs a newer kiln.spawn on this server");
            if (comps.Count > 64) throw new ArgumentException("at most 64 extra spawn components", nameof(options));
            return SpawnWithComponents(t, guid, name, p, yaw, options!, comps);
        }
        return SpawnRaw(t, guid, name, p, yaw, options, null, 0);
    }

    private static EntityId SpawnWithComponents(Table* t, Guid guid, string? name, WorldPosition p, float yaw,
        SpawnOptions options, IReadOnlyList<SpawnComponent> comps)
    {
        // Pin every component's bytes for the duration of the native call (the engine copies them at once).
        var handles = new GCHandle[comps.Count];
        var native = stackalloc NativeComponent[comps.Count];
        try
        {
            for (var i = 0; i < comps.Count; i++)
            {
                var c = comps[i] ?? throw new ArgumentException($"Components[{i}] is null", nameof(options));
                if (c.Data is not { Length: > 0 }) throw new ArgumentException($"Components[{i}] has no data", nameof(options));
                handles[i] = GCHandle.Alloc(c.Data, GCHandleType.Pinned);
                native[i] = new NativeComponent
                {
                    Hash = c.Hash, Data = (byte*)handles[i].AddrOfPinnedObject(), Size = (ulong)c.Data.Length,
                };
            }
            return SpawnRaw(t, guid, name, p, yaw, options, native, (uint)comps.Count);
        }
        finally
        {
            foreach (var h in handles)
                if (h.IsAllocated) h.Free();
        }
    }

    private static EntityId SpawnRaw(Table* t, Guid guid, string? name, WorldPosition p, float yaw, SpawnOptions? options,
        NativeComponent* comps, uint compCount)
    {
        var req = new Request
        {
            // A v1 native without the appended fields reads only the first 104 bytes; send the short size then.
            Size = compCount > 0 ? (uint)sizeof(Request) : (uint)V1RequestSize,
            Components = comps,
            ComponentCount = compCount,
            Flags = (uint)(options?.Flags ?? SpawnFlags.None),
            Position = new Vec3(p),
            Yaw = yaw,
            Level = options?.Level ?? 0,
        };
        if (guid != System.Guid.Empty) WriteGuid(guid, &req.Template);
        uint id = 0;
        int rc;
        var nameBytes = Utf8(name ?? "");
        var tagBytes = Utf8(options?.Tag ?? "");
        var originBytes = Utf8(Origin);
        fixed (byte* n = nameBytes)
        fixed (byte* tag = tagBytes)
        fixed (byte* o = originBytes)
        {
            req.TemplateName = new NativeStr { Ptr = n, Len = (nuint)nameBytes.Length };
            req.Tag = new NativeStr { Ptr = tag, Len = (nuint)tagBytes.Length };
            req.Origin = new NativeStr { Ptr = o, Len = (nuint)originBytes.Length };
            rc = t->Spawn(&req, &id);
        }
        Check(rc, name ?? guid.ToString());
        if (Events.HasSubscribers<EntitySpawnedEvent>())
            Events.Post(new EntitySpawnedEvent(new EntityId(id), SpawnedKind.Entity, guid == System.Guid.Empty ? null : guid, name, p,
                0, 0, options?.Tag, Origin));
        return new EntityId(id);
    }

    // ---- destroy / inspect / find ----

    /// <summary>Destroys a non-player entity now (and drops its tracked record). False if it does not exist.</summary>
    public static bool Destroy(EntityId entity)
    {
        if (entity.IsNone) return false;
        if (entity.IsPlayer) throw new ArgumentException("players cannot be destroyed", nameof(entity));
        var watched = Events.HasSubscribers<EntityDestroyedEvent>();
        var info = watched ? Inspect(entity) : null;
        var rc = Require("Spawn.Destroy")->Destroy(entity.Value);
        if (rc == NotFound) return false;
        Check(rc, $"destroy {entity}");
        if (watched) Events.Post(new EntityDestroyedEvent(entity, info?.Template, info?.TemplateName, info?.Position, "destroy", Origin));
        return true;
    }

    /// <summary>Whether the entity exists right now.</summary>
    public static bool Exists(EntityId entity) => Inspect(entity) is not null;

    /// <summary>The entity's template and transform, or null when it does not exist.</summary>
    public static EntityInfo? Inspect(EntityId entity)
    {
        if (entity.IsNone) return null;
        var s = new NativeSnapshot { Size = (uint)sizeof(NativeSnapshot) };
        var rc = Require("Spawn.Inspect")->EntityInfo(entity.Value, &s);
        if (rc == NotFound) return null;
        Check(rc, $"inspect {entity}");
        Guid? tmpl = s.HasTemplate != 0 ? ReadGuid(&s.Template) : null;
        return new EntityInfo(entity, tmpl, tmpl is { } g ? Template(g)?.Name : null, s.Position.ToPosition(), s.Yaw,
            s.Tracked != 0, s.Serial, s.HasSpawnTime != 0 ? s.SpawnTime : null, new EntityId(s.Parent));
    }

    /// <summary>Entities of a template within <paramref name="radius"/> metres of <paramref name="near"/> (null: the
    /// whole world), nearest first.</summary>
    public static IReadOnlyList<EntityId> Find(Guid template, WorldPosition? near = null, double radius = 0, int max = 256) =>
        FindCore(template, null, near, radius, max);

    /// <summary>Number of entities in the server world right now.</summary>
    public static int EntityCount
    {
        get
        {
            FindCore(System.Guid.Empty, null, null, 0, 1, out var total);
            return total;
        }
    }

    /// <summary>Entities whose template name contains <paramref name="nameContains"/> (case-insensitive).</summary>
    public static IReadOnlyList<EntityId> FindByName(string nameContains, WorldPosition? near = null, double radius = 0, int max = 256)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameContains);
        return FindCore(System.Guid.Empty, nameContains, near, radius, max);
    }

    private static IReadOnlyList<EntityId> FindCore(Guid guid, string? filter, WorldPosition? near, double radius, int max) =>
        FindCore(guid, filter, near, radius, max, out _);

    private static IReadOnlyList<EntityId> FindCore(Guid guid, string? filter, WorldPosition? near, double radius, int max, out int total)
    {
        var t = Require("Spawn.Find");
        max = Math.Clamp(max, 1, 1 << 20);
        var buf = new uint[max];
        nuint count = 0;
        var g = default(GuidBytes);
        if (guid != System.Guid.Empty) WriteGuid(guid, &g);
        var fb = Utf8(filter ?? "");
        int rc;
        fixed (uint* o = buf)
        fixed (byte* f = fb)
        {
            rc = t->Find(guid != System.Guid.Empty ? &g : null, new NativeStr { Ptr = f, Len = (nuint)fb.Length },
                near is { } c ? new Vec3(c) : default, near is null ? 0 : radius, o, (nuint)max, &count);
        }
        Check(rc, "find");
        total = (int)count;
        var n = (int)Math.Min((ulong)count, (ulong)max);
        var list = new EntityId[n];
        for (var i = 0; i < n; i++) list[i] = new EntityId(buf[i]);
        return list;
    }

    // ---- templates (any thread) ----

    /// <summary>A template by name, display name or GUID text; null if unknown.</summary>
    public static TemplateInfo? Lookup(string name)
    {
        var t = T;
        if (t == null || string.IsNullOrEmpty(name)) return null;
        var info = new NativeTemplate { Size = (uint)sizeof(NativeTemplate) };
        var b = Utf8(name);
        int rc;
        fixed (byte* p = b) rc = t->TemplateFind(new NativeStr { Ptr = p, Len = (nuint)b.Length }, &info);
        return rc == Ok ? ToInfo(&info) : null;
    }

    /// <summary>A template by GUID; null if unknown.</summary>
    public static TemplateInfo? Template(Guid id)
    {
        var t = T;
        if (t == null) return null;
        var g = default(GuidBytes);
        WriteGuid(id, &g);
        var info = new NativeTemplate { Size = (uint)sizeof(NativeTemplate) };
        return t->TemplateGet(&g, &info) == Ok ? ToInfo(&info) : null;
    }

    /// <summary>Every known template (the game's loaded templates plus Kiln's catalog), sorted by name.</summary>
    public static IReadOnlyList<TemplateInfo> Templates
    {
        get
        {
            var t = T;
            if (t == null) return [];
            var n = (int)t->TemplateCount();
            var list = new List<TemplateInfo>(n);
            for (var i = 0; i < n; i++)
            {
                var info = new NativeTemplate { Size = (uint)sizeof(NativeTemplate) };
                if (t->TemplateAt((nuint)i, &info) == Ok) list.Add(ToInfo(&info));
            }
            return list;
        }
    }

    // ---- tracking ----

    /// <summary>What Kiln spawned in this world and has not removed yet (live or not found right now).</summary>
    public static IReadOnlyList<TrackedEntity> Tracked
    {
        get
        {
            var t = T;
            if (t == null) return [];
            var n = (int)t->TrackedCount();
            var list = new List<TrackedEntity>(n);
            for (var i = 0; i < n; i++)
            {
                var r = new NativeTracked { Size = (uint)sizeof(NativeTracked) };
                if (t->TrackedAt((nuint)i, &r) != Ok) continue;
                list.Add(new TrackedEntity(r.Serial, new EntityId(r.Entity), ReadGuid(&r.Template), Text(r.Name, 128),
                    r.Position.ToPosition(), r.SpawnPosition.ToPosition(), DateTimeOffset.FromUnixTimeSeconds(r.SpawnedUnix),
                    r.State == 0, (SpawnFlags)r.Flags, Text(r.Tag, 64), Text(r.Origin, 64)));
            }
            return list;
        }
    }

    /// <summary>Destroys tracked entities (all, or those with <paramref name="tag"/> / spawned by
    /// <paramref name="origin"/>) and drops records whose entity is gone.</summary>
    public static CleanupResult CleanupTracked(string? tag = null, string? origin = null)
    {
        var t = Require("Spawn.CleanupTracked");
        var watched = Events.HasSubscribers<EntityDestroyedEvent>()
            ? Tracked.Where(e => e.IsLive && (string.IsNullOrEmpty(tag) || e.Tag == tag) && (string.IsNullOrEmpty(origin) || e.Origin == origin)).ToList()
            : null;
        var r = new NativeCleanup { Size = (uint)sizeof(NativeCleanup) };
        var tb = Utf8(tag ?? "");
        var ob = Utf8(origin ?? "");
        int rc;
        fixed (byte* tp = tb)
        fixed (byte* op = ob)
        {
            rc = t->Cleanup(new NativeStr { Ptr = tp, Len = (nuint)tb.Length }, new NativeStr { Ptr = op, Len = (nuint)ob.Length }, &r);
        }
        Check(rc, "cleanup");
        if (watched is not null)
            foreach (var e in watched)
                if (!Exists(e.Entity))
                    Events.Post(new EntityDestroyedEvent(e.Entity, e.Template, e.TemplateName, e.Position, "cleanup", Origin));
        return new CleanupResult((int)r.Destroyed, (int)r.Missing, (int)r.Kept);
    }

    /// <summary>Re-checks the tracked list against the world now; returns how many are live.</summary>
    public static int Refresh()
    {
        uint live = 0;
        Check(Require("Spawn.Refresh")->Refresh(&live), "refresh");
        return (int)live;
    }

    /// <summary>True once the server world runs (its save is loaded). Spawning before that fails with
    /// <see cref="NotSupportedException"/>: the server steps its world while it still loads the save.</summary>
    public static bool WorldRunning => T != null && T->WorldState() == 2;

    /// <summary>The world the tracked list belongs to (the world GUID from the save), or null.</summary>
    public static string? WorldKey
    {
        get
        {
            var t = T;
            if (t == null) return null;
            var buf = stackalloc byte[128];
            nuint len = 0;
            return t->WorldKey(buf, 128, &len) == Ok ? Encoding.UTF8.GetString(buf, (int)len) : null;
        }
    }

    // ---- helpers ----

    private static string Origin => ModContext.CurrentMod?.Info.Id ?? "";

    private static void RequireTickThread(string api) => Services.RequireTickThread(api);

    private static Table* Require(string api)
    {
        var t = T;
        if (t == null) throw new NotSupportedException("kiln.spawn is unavailable on this server");
        Services.RequireTickThread(api);
        return t;
    }

    private static Task<EntityId> Run(Func<EntityId> f)
    {
        if (!Game.IsTickThread) throw new InvalidOperationException("Spawn must be called on the tick thread (use Scheduler.Post)");
        try
        {
            return Task.FromResult(f());
        }
        catch (Exception e) when (e is SpawnException or NotSupportedException)
        {
            return Task.FromException<EntityId>(e);
        }
    }

    private static void Check(int rc, string what)
    {
        if (rc == Ok) return;
        throw rc switch
        {
            Unavailable => new NotSupportedException($"kiln.spawn: {what}: unavailable (the world is not running yet, or not on this build)"),
            WrongThread => new InvalidOperationException($"kiln.spawn: {what}: not on the tick thread"),
            NotFound => new SpawnException($"kiln.spawn: {what}: unknown template or entity", rc),
            InvalidArgument => new SpawnException($"kiln.spawn: {what}: invalid argument", rc),
            _ => new SpawnException($"kiln.spawn: {what}: refused by the game (see kiln.log)", rc),
        };
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static void WriteGuid(Guid g, GuidBytes* dst) => g.TryWriteBytes(new Span<byte>(dst->B, 16));

    private static Guid ReadGuid(GuidBytes* src) => new(new ReadOnlySpan<byte>(src->B, 16));

    private static string Text(byte* p, int cap)
    {
        var n = 0;
        while (n < cap && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    private static TemplateInfo ToInfo(NativeTemplate* t) =>
        new(ReadGuid(&t->Guid), Text(t->Name, 128), Text(t->Display, 64), (TemplateKind)t->Kind, (t->Source & 1) != 0,
            (t->Source & 2) != 0, (t->Flags & 1) != 0 ? (t->Flags & 2) != 0 : null, (t->Flags & 4) != 0);
}
