// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>An entity's world transform: position in metres, rotation, scale.</summary>
public readonly record struct EntityTransform(WorldPosition Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>Heading about +Y (up) in degrees, 0..360.</summary>
    public double YawDegrees
    {
        get
        {
            var q = Rotation;
            var yaw = Math.Atan2(2.0 * (q.W * q.Y + q.X * q.Z), 1.0 - 2.0 * (q.Y * q.Y + q.X * q.X)) * 180.0 / Math.PI;
            return yaw < 0 ? yaw + 360.0 : yaw;
        }
    }
}

/// <summary>What kiln.world can do on this server build right now.</summary>
public readonly record struct WorldStatus(bool Programs, bool WorldLive, bool Teleport, bool Find, int KilnPrograms,
    int EnginePrograms, int EngineMax, int TeleportsPending, ulong WorldSteps);

/// <summary>
/// The server world's entities (kiln.world). Every call that reads game state must run on the tick thread (mod
/// callbacks, <c>Scheduler.Post</c>, after <c>await NextTick()</c>); data comes straight from the engine at call time.
/// </summary>
/// <remarks>Component names are the engine's short names without <c>keen::ecs::</c> ("CurrentTransform",
/// "TeleportCount", "PlayerInput"). Entity ids are not stable across server restarts.</remarks>
public static unsafe partial class World
{
    private static readonly Dictionary<string, uint> Hashes = new(StringComparer.Ordinal);
    private static uint[] _findBuffer = new uint[4096];

    /// <summary>True when kiln.world is loaded on this server build.</summary>
    public static bool Available => WorldNative.T != null;

    /// <summary>What works now (programs, a live server world, teleport, find).</summary>
    public static WorldStatus Status
    {
        get
        {
            var t = WorldNative.T;
            if (t == null) return default;
            WorldNative.Status s;
            s.Size = (uint)sizeof(WorldNative.Status);
            if (t->GetStatus(&s) != WorldNative.Ok) return default;
            return new WorldStatus(s.Programs != 0, s.World != 0, s.Teleport != 0, s.Find != 0, (int)s.KilnPrograms,
                (int)s.EnginePrograms, (int)s.EngineMax, (int)s.TeleportsPending, s.WorldSteps);
        }
    }

    /// <summary>FNV-1a-32 of <c>"keen::ecs::" + name</c> (the name as given when it already contains "::"): the
    /// engine's component hash (types.json qualifiedHash). Works without the native service.</summary>
    public static uint ComponentHash(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (Hashes)
        {
            if (Hashes.TryGetValue(name, out var h)) return h;
            h = 0x811c9dc5u;
            if (!name.Contains("::", StringComparison.Ordinal))
                foreach (var b in "keen::ecs::"u8) h = (h ^ b) * 0x01000193u;
            foreach (var b in Encoding.UTF8.GetBytes(name)) h = (h ^ b) * 0x01000193u;
            Hashes[name] = h;
            return h;
        }
    }

    /// <summary>True if the entity exists (has a world transform).</summary>
    public static bool Exists(EntityId entity)
    {
        var t = Tick("World.Exists");
        return !entity.IsNone && t->Exists(entity.Value) != 0;
    }

    /// <summary>The entity's position in metres, or null when it does not exist or has no transform.</summary>
    public static WorldPosition? Position(EntityId entity) => TryGetPosition(entity, out var p) ? p : null;

    public static bool TryGetPosition(EntityId entity, out WorldPosition position)
    {
        var t = Tick("World.Position");
        WorldNative.Vec3 v;
        if (entity.IsNone || t->GetPosition(entity.Value, &v) != WorldNative.Ok)
        {
            position = default;
            return false;
        }
        position = new WorldPosition(v.X, v.Y, v.Z);
        return true;
    }

    /// <summary>The entity's CurrentTransform, or null.</summary>
    public static EntityTransform? Transform(EntityId entity)
    {
        var t = Tick("World.Transform");
        WorldNative.Transform x;
        if (entity.IsNone || t->GetTransform(entity.Value, &x) != WorldNative.Ok) return null;
        return new EntityTransform(new WorldPosition(x.Position.X, x.Position.Y, x.Position.Z),
            new Quaternion(x.Rotation[0], x.Rotation[1], x.Rotation[2], x.Rotation[3]),
            new Vector3(x.Scale[0], x.Scale[1], x.Scale[2]));
    }

    /// <summary>True if the entity has the per-entity component <paramref name="component"/>.</summary>
    public static bool Has(EntityId entity, string component)
    {
        var t = Tick("World.Has");
        uint size;
        return !entity.IsNone && t->Component(entity.Value, ComponentHash(component), &size) != null;
    }

    /// <summary>Size in bytes of the entity's component, or 0 when it has none.</summary>
    public static int ComponentSize(EntityId entity, string component)
    {
        var t = Tick("World.ComponentSize");
        uint size = 0;
        return !entity.IsNone && t->Component(entity.Value, ComponentHash(component), &size) != null ? (int)size : 0;
    }

    /// <summary>Copies the first <c>sizeof(T)</c> bytes of the entity's component into <paramref name="value"/>.
    /// False when the entity has no such component or it is smaller than T. Layouts depend on the game build.</summary>
    public static bool TryRead<T>(EntityId entity, string component, out T value) where T : unmanaged
    {
        var t = Tick("World.TryRead");
        uint size = 0;
        var p = entity.IsNone ? null : t->Component(entity.Value, ComponentHash(component), &size);
        if (p == null || size < (uint)sizeof(T))
        {
            value = default;
            return false;
        }
        value = *(T*)p;
        return true;
    }

    /// <summary>Copies <paramref name="value"/> into the entity's component at <paramref name="offset"/>, as a raw store:
    /// the game is not told. False when the entity has no such component or it doesn't fit. It keeps working as before;
    /// what replaces it depends on what the value is: see <see cref="WriteRaw{T}"/>.</summary>
    [Obsolete("Use World.Write, World.Update, World.WriteField or World.WriteAt: this raw store skips the game's write tracking, so the save and players may never see the change. For an input the game neither saves nor sends, use World.WriteRaw.")]
    public static bool TryWrite<T>(EntityId entity, string component, int offset, T value) where T : unmanaged =>
        WriteRaw(entity, component, offset, value);

    /// <summary>Copies <paramref name="value"/> into the entity's component at <paramref name="offset"/> at once, as a raw
    /// store: the game's write tracking is not told, so the save skips the change on an entity it has already written
    /// and players may never be sent it. Right only for a value the game reads in the coming step and neither saves nor
    /// sends (an input such as <c>PlayerInput</c> or <c>ActorInput</c> set before the step); for everything else use
    /// <see cref="Write{T}"/>, <see cref="Update{T}"/>, <see cref="WriteField"/> or <see cref="WriteAt{T}"/>. False when
    /// the entity has no such component or the value doesn't fit. Layouts depend on the game build. Since 0.2.0-dev.27
    /// (the same store as <c>TryWrite</c> before it).</summary>
    [TickThreadOnly]
    public static bool WriteRaw<T>(EntityId entity, string component, int offset, T value) where T : unmanaged
    {
        var t = Tick("World.WriteRaw");
        uint size = 0;
        var p = entity.IsNone ? null : t->Component(entity.Value, ComponentHash(component), &size);
        if (p == null || offset < 0 || (uint)offset + (uint)sizeof(T) > size) return false;
        *(T*)((byte*)p + offset) = value;
        return true;
    }

    /// <summary>A copy of the entity's component bytes, or null.</summary>
    public static byte[]? ReadBytes(EntityId entity, string component)
    {
        var t = Tick("World.ReadBytes");
        uint size = 0;
        var p = entity.IsNone ? null : t->Component(entity.Value, ComponentHash(component), &size);
        return p == null ? null : new ReadOnlySpan<byte>(p, checked((int)size)).ToArray();
    }

    /// <summary>Every entity that has the per-entity component <paramref name="component"/> (one pass over the entity
    /// map). Empty when the component type does not exist in this world.</summary>
    public static IReadOnlyList<EntityId> Find(string component)
    {
        var t = Tick("World.Find");
        var hash = ComponentHash(component);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            int rc;
            fixed (uint* buf = _findBuffer) rc = t->Find(hash, buf, (uint)_findBuffer.Length, &count);
            if (rc == WorldNative.BufferTooSmall)
            {
                _findBuffer = new uint[Math.Max(count + 1024, (uint)_findBuffer.Length * 2)];
                continue;
            }
            if (rc == WorldNative.NotFound) return [];
            if (rc != WorldNative.Ok) throw new InvalidOperationException($"World.Find({component}): {WorldNative.Describe(rc)}");
            var list = new EntityId[count];
            for (var i = 0; i < count; i++) list[i] = new EntityId(_findBuffer[i]);
            return list;
        }
        throw new InvalidOperationException($"World.Find({component}): the entity count kept growing");
    }

    /// <summary>Entities in the server world.</summary>
    public static int EntityCount => (int)Tick("World.EntityCount")->EntityCount();

    private static WorldNative.Table* Tick(string api)
    {
        var t = WorldNative.Require(api);
        Services.RequireTickThread(api);
        return t;
    }
}
