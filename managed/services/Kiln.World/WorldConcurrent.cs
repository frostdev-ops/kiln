// SPDX-License-Identifier: MIT
// WorldConcurrent: kiln.world's verified-copy reads (kiln_world.h "ECS reads", native/services/world/ecs_reads.cpp) for
// Kiln tasks. Statuses are the native KILN_ECS_* ones plus the three a managed caller can meet on its own side (a query
// that changed, a short buffer, a component smaller than T); an ordinary miss never throws.
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>How a <see cref="WorldConcurrent"/> read copies component bytes.</summary>
public enum ReadMode
{
    /// <summary>The default: a verified copy at any time. Kiln copies the bytes twice between identity checks and refuses
    /// a value that changed during the read (<see cref="ReadStatus.Unreadable"/>). Its limit: a game writer descheduled
    /// half way through storing a value leaves a half-written value that stays put, and no read without an engine lock
    /// can tell it from a whole one.</summary>
    Stable = 0,

    /// <summary>The copy runs only while no server frame runs (the server world only). A frame that ran during the read
    /// gives <see cref="ReadStatus.Busy"/> (retry after <c>await ctx.NextGateWindow()</c>); a client or prediction world
    /// gives <see cref="ReadStatus.Unsupported"/>, never a stable copy instead. On the server tick (a tick handler or a
    /// task's tick hop) the frame is running, so a server-world read is always <see cref="ReadStatus.Busy"/> there.</summary>
    FrameQuiet = 1,
}

/// <summary>The outcome of a <see cref="WorldConcurrent"/> read. 0 to 6 are kiln.world's own statuses.</summary>
public enum ReadStatus
{
    /// <summary>A complete, verified result.</summary>
    Ok = 0,

    /// <summary>No such entity, or the entity has no such component (or names no template).</summary>
    Absent = 1,

    /// <summary>The ref's id names another entity now (same id, new generation), or its world session, its component
    /// layout or the mod's load changed since it was opened. Open the entity again.</summary>
    Stale = 2,

    /// <summary>The copy faulted, or the value changed while it was read. Nothing was returned; try again.</summary>
    Unreadable = 3,

    /// <summary>The reads do not work here: no exact engine profile for this game build, an older Kiln, or no such world
    /// right now (<see cref="WorldConcurrent.IsAvailable"/>, <see cref="WorldConcurrent.IsPresent"/>).</summary>
    Unavailable = 4,

    /// <summary><see cref="ReadMode.FrameQuiet"/> only: a server frame ran during the read; nothing was copied. Retry after
    /// <c>await ctx.NextGateWindow()</c>.</summary>
    Busy = 5,

    /// <summary><see cref="ReadMode.FrameQuiet"/> only: the world is a client or prediction world, which has no
    /// frame-quiet window.</summary>
    Unsupported = 6,

    /// <summary>A query's membership changed while it ran; nothing was copied. Try again.</summary>
    Changed = 7,

    /// <summary>The caller's buffer is too small: <c>Required</c> says how big it must be, and nothing was copied.</summary>
    TooSmall = 8,

    /// <summary><see cref="WorldConcurrent.Read{T}"/> or <see cref="ConcurrentSnapshot.TryGet{T}"/>: the component is
    /// smaller than <c>T</c> (another layout on this game build).</summary>
    SizeMismatch = 9,
}

/// <summary>An entity as <see cref="WorldConcurrent.Open"/> found it: its id, generation, world and session. A value, not
/// a handle: nothing is pinned, every read checks it again, and a read of an entity that is gone or replaced says
/// <see cref="ReadStatus.Stale"/> or <see cref="ReadStatus.Absent"/>. Refs opened before the mod reloads are stale after
/// it.</summary>
public readonly struct ConcurrentEntity : IEquatable<ConcurrentEntity>
{
    internal readonly EcsObjectRefNative Native;

    internal ConcurrentEntity(in EcsObjectRefNative native) => Native = native;

    /// <summary>The entity's id.</summary>
    public EntityId Id => new(Native.Entity);

    /// <summary>The world it belongs to.</summary>
    public WorldKind World => (WorldKind)Native.WorldKind;

    /// <summary>The id's generation when it was opened.</summary>
    public uint Generation => Native.Generation;

    /// <summary>The world's session when it was opened (a new world, a new session).</summary>
    public ulong Session => Native.Session;

    /// <summary>True for <c>default</c>: no entity.</summary>
    public bool IsNone => Native.Size == 0;

    /// <inheritdoc/>
    public bool Equals(ConcurrentEntity other) =>
        Native.Size == other.Native.Size && Native.Entity == other.Native.Entity && Native.Generation == other.Native.Generation &&
        Native.WorldKind == other.Native.WorldKind && Native.Session == other.Native.Session &&
        Native.LayoutStamp == other.Native.LayoutStamp && Native.ReloadEpoch == other.Native.ReloadEpoch;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ConcurrentEntity other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Native.Entity, Native.Generation, Native.WorldKind, Native.Session,
        Native.LayoutStamp, Native.ReloadEpoch);

    /// <summary>Same entity, generation, world, session, layout and load.</summary>
    public static bool operator ==(ConcurrentEntity left, ConcurrentEntity right) => left.Equals(right);

    /// <summary>Not the same ref.</summary>
    public static bool operator !=(ConcurrentEntity left, ConcurrentEntity right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => IsNone ? "ConcurrentEntity(none)" : $"ConcurrentEntity({Native.Entity}:{Native.Generation} {World})";
}

/// <summary>A value read by <see cref="WorldConcurrent"/>, with its status. <see cref="Value"/> is <c>default</c> unless
/// <see cref="Status"/> is <see cref="ReadStatus.Ok"/>.</summary>
public readonly struct ConcurrentRead<T>
{
    internal ConcurrentRead(ReadStatus status, T value)
    {
        Status = status;
        Value = value;
    }

    /// <summary>What happened.</summary>
    public ReadStatus Status { get; }

    /// <summary>The value when <see cref="Status"/> is <see cref="ReadStatus.Ok"/>, else <c>default</c>.</summary>
    public T Value { get; }

    /// <summary><see cref="Status"/> is <see cref="ReadStatus.Ok"/>.</summary>
    public bool IsOk => Status == ReadStatus.Ok;

    /// <inheritdoc/>
    public override string ToString() => IsOk ? $"Ok({Value})" : Status.ToString();
}

/// <summary>The outcome of <see cref="WorldConcurrent.ReadBytes"/>.</summary>
public readonly struct ConcurrentCopy
{
    internal ConcurrentCopy(ReadStatus status, int written, int required)
    {
        Status = status;
        Written = written;
        Required = required;
    }

    /// <summary>What happened.</summary>
    public ReadStatus Status { get; }

    /// <summary>Bytes copied into the buffer: the whole component on <see cref="ReadStatus.Ok"/>, else 0.</summary>
    public int Written { get; }

    /// <summary>The component's size, when it is known (<see cref="ReadStatus.Ok"/> and <see cref="ReadStatus.TooSmall"/>);
    /// else 0.</summary>
    public int Required { get; }
}

/// <summary>The outcome of <see cref="WorldConcurrent.Query(in EntityQuery, Span{ConcurrentEntity})"/>.</summary>
public readonly struct ConcurrentQueryResult
{
    internal ConcurrentQueryResult(ReadStatus status, int count, int required)
    {
        Status = status;
        Count = count;
        Required = required;
    }

    /// <summary>What happened.</summary>
    public ReadStatus Status { get; }

    /// <summary>Refs written to the start of the buffer (<see cref="ReadStatus.Ok"/>), else 0.</summary>
    public int Count { get; }

    /// <summary>The number of matches (<see cref="ReadStatus.Ok"/> and <see cref="ReadStatus.TooSmall"/>); else 0.</summary>
    public int Required { get; }
}

/// <summary>A query for <see cref="WorldConcurrent.Query(in EntityQuery, Span{ConcurrentEntity})"/>: the entities of one world that have every listed component,
/// optionally within <see cref="Radius"/> metres of <see cref="Center"/>.</summary>
public readonly struct EntityQuery
{
    private readonly string[]? _components;
    internal readonly uint[]? Hashes;

    /// <summary>The entities of <paramref name="world"/> that have all of <paramref name="components"/> (the engine's
    /// short names, as <see cref="World.ComponentHash"/> takes them).</summary>
    /// <exception cref="ArgumentException">No component, or an empty name.</exception>
    public EntityQuery(WorldKind world, params string[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length == 0) throw new ArgumentException("a query needs at least one component", nameof(components));
        World = world;
        _components = [.. components];
        Hashes = new uint[components.Length];
        for (var i = 0; i < components.Length; i++) Hashes[i] = Kiln.World.ComponentHash(components[i]);
    }

    /// <summary>The world queried.</summary>
    public WorldKind World { get; }

    /// <summary>The components every match has.</summary>
    public IReadOnlyList<string> Components => _components ?? [];

    /// <summary>With a value, only entities with a position within <see cref="Radius"/> of it match (an entity with no
    /// position never does).</summary>
    public WorldPosition? Center { get; init; }

    /// <summary>Metres, finite and above 0, when <see cref="Center"/> is set.</summary>
    public double Radius { get; init; }

    /// <summary><see cref="ReadMode.FrameQuiet"/> runs the query's walks only while no server frame runs.</summary>
    public ReadMode Mode { get; init; }
}

/// <summary>The components one <see cref="WorldConcurrent.ReadMany"/> reads, in order. Make it once and keep it.</summary>
public sealed class ComponentSet
{
    private readonly string[] _names;
    internal readonly uint[] Hashes;

    /// <summary>The components, by the engine's short names.</summary>
    /// <exception cref="ArgumentException">No component, or an empty name.</exception>
    public ComponentSet(params string[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Length == 0) throw new ArgumentException("a set needs at least one component", nameof(components));
        _names = [.. components];
        Hashes = new uint[components.Length];
        for (var i = 0; i < components.Length; i++) Hashes[i] = World.ComponentHash(components[i]);
    }

    /// <summary>How many components.</summary>
    public int Count => _names.Length;

    /// <summary>The name at <paramref name="index"/>.</summary>
    public string this[int index] => _names[index];
}

/// <summary>Several components of one entity, from <see cref="WorldConcurrent.ReadMany"/>. Each component is verified on
/// its own; the set is not one atomic read across components.</summary>
public sealed class ConcurrentSnapshot
{
    private readonly ReadStatus[] _status;
    private readonly byte[] _bytes;
    private readonly int[] _offset, _length;

    internal ConcurrentSnapshot(ReadStatus status, ReadStatus[] each, byte[] bytes, int[] offset, int[] length)
    {
        Status = status;
        _status = each;
        _bytes = bytes;
        _offset = offset;
        _length = length;
    }

    /// <summary>The call's outcome: <see cref="ReadStatus.Ok"/> when the entity was read (each component then has its own
    /// status, <see cref="ReadStatus.Absent"/> for one it does not have); otherwise every component has this status.</summary>
    public ReadStatus Status { get; }

    /// <summary>How many components were asked for.</summary>
    public int Count => _status.Length;

    /// <summary>The status of the component at <paramref name="index"/> of the set.</summary>
    public ReadStatus StatusOf(int index) => _status[index];

    /// <summary>The component's bytes when its status is <see cref="ReadStatus.Ok"/>, else empty.</summary>
    public ReadOnlySpan<byte> Bytes(int index) =>
        _status[index] == ReadStatus.Ok ? _bytes.AsSpan(_offset[index], _length[index]) : default;

    /// <summary>The first <c>sizeof(T)</c> bytes of the component at <paramref name="index"/>; false when its status is not
    /// <see cref="ReadStatus.Ok"/> or it is smaller than <c>T</c>.</summary>
    public unsafe bool TryGet<T>(int index, out T value) where T : unmanaged
    {
        var bytes = Bytes(index);
        if (_status[index] != ReadStatus.Ok || bytes.Length < sizeof(T))
        {
            value = default;
            return false;
        }
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }
}

/// <summary>
/// Entity and component reads from Kiln tasks (kiln.world's verified copies). Every read copies the bytes and checks
/// the entity again around the copy, so it returns a complete value or says why not (<see cref="ReadStatus"/>); an
/// ordinary miss never throws. Since 0.2.0-dev.30.7, mod API 0.2.16 (no longer experimental).
/// </summary>
/// <remarks>
/// <para><b>Where.</b> Inside a Kiln task (its work on a worker, or after <c>await ctx.ToTick()</c>) and on the tick.
/// Anywhere else (a <c>new Thread</c>, <c>Task.Run</c>, code after <c>ConfigureAwait(false)</c>) a call throws
/// <see cref="InvalidOperationException"/>: Kiln reads for a task it can attribute to a mod, and that code left it.</para>
/// <para><b>When.</b> On the game builds Kiln has an exact engine profile for (<see cref="IsAvailable"/>); everywhere else
/// every read is <see cref="ReadStatus.Unavailable"/>. <see cref="OrOnTick{T}"/> keeps the tick path one line.</para>
/// <para><b>Which world.</b> A dedicated server has the server world; a client has its client world, and the server world
/// too while it hosts. The prediction world is never readable.</para>
/// </remarks>
public static unsafe partial class WorldConcurrent
{
    /// <summary>The capability row kiln.world reports for these reads; its text says why they are off.</summary>
    public const string Capability = "world.ecs_read";

    /// <summary>True when the reads work on this game build: kiln.world has them and Kiln has an exact engine profile
    /// that checks out against the running game. Any thread.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (Members() == null) return false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var (rc, info) = Context();
                if (rc == ErrUnreadable) continue;  // the context moved between its two samples: ask again
                return info.ExactProfile != 0;
            }
            return false;
        }
    }

    /// <summary>True when a world of <paramref name="world"/> exists right now. Any thread.</summary>
    public static bool IsPresent(WorldKind world)
    {
        if ((uint)world > 2 || Table() == null) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (rc, info) = Context();
            if (rc == ErrUnreadable) continue;
            return (info.PresentMask & (1u << (int)world)) != 0;
        }
        return false;
    }

    /// <summary>The entity <paramref name="id"/> of <paramref name="world"/> as a ref for the other reads.
    /// <see cref="ReadStatus.Absent"/> when there is no such entity.</summary>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ConcurrentRead<ConcurrentEntity> Open(EntityId id, WorldKind world = WorldKind.Server)
    {
        var owner = Caller("WorldConcurrent.Open");
        if ((uint)world > 2) throw new ArgumentOutOfRangeException(nameof(world), world, "not a world kind");
        if (id.IsNone) return new(ReadStatus.Absent, default);
        var t = Members();
        if (t == null) return new(ReadStatus.Unavailable, default);
        EcsObjectRefNative r;
        var rc = t->EntityOpen(owner, owner, (uint)world, 0, id.Value, &r);  // the owner id is the mod load's epoch
        return rc == Ok ? new(ReadStatus.Ok, new ConcurrentEntity(r)) : new(StatusOf(rc, "WorldConcurrent.Open"), default);
    }

    /// <summary>Checks <paramref name="entity"/> again: <see cref="ReadStatus.Ok"/> while it is the same entity in the same
    /// world, session, layout and mod load.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ReadStatus Validate(ConcurrentEntity entity)
    {
        var owner = Caller("WorldConcurrent.Validate");
        var r = RefOf(entity);
        var t = Members();
        if (t == null) return ReadStatus.Unavailable;
        EcsObjectInfoNative info = default;
        info.Size = (uint)sizeof(EcsObjectInfoNative);
        var rc = t->EntityValidate(owner, &r, &info);
        if (rc == InvalidArgument) throw Bug("WorldConcurrent.Validate");
        return info.Status <= EcsReadConstants.Unsupported ? (ReadStatus)info.Status : StatusOf(rc, "WorldConcurrent.Validate");
    }

    /// <summary>The first <c>sizeof(T)</c> bytes of the entity's <paramref name="component"/> (the whole component is
    /// copied and verified). <see cref="ReadStatus.SizeMismatch"/> when the component is smaller than <c>T</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>, or no component name.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ConcurrentRead<T> Read<T>(ConcurrentEntity entity, string component, ReadMode mode = ReadMode.Stable) where T : unmanaged
    {
        var owner = Caller("WorldConcurrent.Read");
        var r = RefOf(entity);
        var hash = Hash(component);
        var quiet = Quiet(mode);
        var t = Members();
        if (t == null) return new(ReadStatus.Unavailable, default);
        if (quiet && OnServerTick) return new(QuietOnTick(r.WorldKind), default);
        var size = Sizes.TryGetValue((r.WorldKind, hash), out var known) ? known : 64;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, sizeof(T)));
            try
            {
                fixed (byte* p = buffer)
                {
                    var (status, required) = Copy(t, owner, &r, hash, p, (uint)buffer.Length, quiet);
                    if (status == ReadStatus.TooSmall)
                    {
                        size = required;
                        continue;
                    }
                    if (status != ReadStatus.Ok) return new(status, default);
                    if (required < sizeof(T)) return new(ReadStatus.SizeMismatch, default);
                    return new(ReadStatus.Ok, System.Runtime.CompilerServices.Unsafe.ReadUnaligned<T>(p));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        return new(ReadStatus.Unreadable, default);  // the size kept changing: not one value
    }

    /// <summary>The whole <paramref name="component"/> into <paramref name="into"/>. A buffer that is too small gives
    /// <see cref="ReadStatus.TooSmall"/> with the size in <see cref="ConcurrentCopy.Required"/>, and copies nothing.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>, or no component name.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ConcurrentCopy ReadBytes(ConcurrentEntity entity, string component, Span<byte> into, ReadMode mode = ReadMode.Stable)
    {
        var owner = Caller("WorldConcurrent.ReadBytes");
        var r = RefOf(entity);
        var hash = Hash(component);
        var quiet = Quiet(mode);
        var t = Members();
        if (t == null) return new(ReadStatus.Unavailable, 0, 0);
        if (quiet && OnServerTick) return new(QuietOnTick(r.WorldKind), 0, 0);
        fixed (byte* p = into)
        {
            var (status, required) = Copy(t, owner, &r, hash, p, (uint)into.Length, quiet);
            return status switch
            {
                ReadStatus.Ok => new(status, required, required),
                ReadStatus.TooSmall => new(status, 0, required),
                _ => new(status, 0, 0),
            };
        }
    }

    /// <summary>Several components of one entity in one call (one frame-quiet window for all of them with
    /// <see cref="ReadMode.FrameQuiet"/>).</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ConcurrentSnapshot ReadMany(ConcurrentEntity entity, ComponentSet components, ReadMode mode = ReadMode.Stable)
    {
        var owner = Caller("WorldConcurrent.ReadMany");
        var r = RefOf(entity);
        ArgumentNullException.ThrowIfNull(components);
        var quiet = Quiet(mode);
        var n = components.Count;
        var t = Members();
        if (t == null) return Refused(n, ReadStatus.Unavailable);
        if (quiet && OnServerTick) return Refused(n, QuietOnTick(r.WorldKind));
        var specs = n <= 32 ? stackalloc EcsSnapshotSpecNative[n] : new EcsSnapshotSpecNative[n];
        var presence = n <= 32 ? stackalloc uint[n] : new uint[n];
        var guess = 0;
        for (var i = 0; i < n; i++)
        {
            specs[i] = new EcsSnapshotSpecNative { Component = components.Hashes[i], Flags = quiet ? EcsReadConstants.ReadFrameQuiet : 0 };
            guess += Sizes.TryGetValue((r.WorldKind, components.Hashes[i]), out var s) ? s : 0;
        }
        var buffer = new byte[Math.Max(guess, 64)];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint required = 0;
            int rc;
            fixed (byte* p = buffer)
            fixed (EcsSnapshotSpecNative* sp = specs)
            fixed (uint* pp = presence)
                rc = t->SnapshotMany(owner, &r, sp, (uint)n, p, (uint)buffer.Length, &required, pp);
            if (rc == BufferTooSmall)
            {
                buffer = new byte[required];
                continue;
            }
            if (rc != Ok) return Refused(n, StatusOf(rc, "WorldConcurrent.ReadMany"));
            // packed in spec order; an absent component takes no room. The sizes are the component types' own.
            var each = new ReadStatus[n];
            var offset = new int[n];
            var length = new int[n];
            var at = 0;
            for (var i = 0; i < n; i++)
            {
                each[i] = presence[i] <= EcsReadConstants.Unsupported ? (ReadStatus)presence[i] : ReadStatus.Unreadable;
                if (each[i] != ReadStatus.Ok) continue;
                if (SizeOf(t, owner, &r, specs[i].Component) is not { } size) return Refused(n, ReadStatus.Stale);
                offset[i] = at;
                length[i] = size;
                at += size;
            }
            if (at != required) return Refused(n, ReadStatus.Unreadable);
            return new ConcurrentSnapshot(ReadStatus.Ok, each, buffer, offset, length);
        }
        return Refused(n, ReadStatus.Unreadable);
    }

    /// <summary>The entities that match <paramref name="query"/>, as refs in ascending id, into <paramref name="into"/>.
    /// Complete or nothing: a buffer that is too small gives <see cref="ReadStatus.TooSmall"/> with the match count in
    /// <see cref="ConcurrentQueryResult.Required"/>, and a membership change during the walk gives
    /// <see cref="ReadStatus.Changed"/>.</summary>
    /// <exception cref="ArgumentException">A <c>default</c> query, or a <see cref="EntityQuery.Center"/> that is not finite
    /// or has no finite radius above 0.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ConcurrentQueryResult Query(in EntityQuery query, Span<ConcurrentEntity> into) => Query(query, into, true);

    private static ConcurrentQueryResult Query(in EntityQuery query, Span<ConcurrentEntity> into, bool firstTry)
    {
        var owner = Caller("WorldConcurrent.Query");
        if (query.Hashes is not { Length: > 0 } hashes) throw new ArgumentException("a default EntityQuery: use new EntityQuery(world, components)", nameof(query));
        if ((uint)query.World > 2) throw new ArgumentException($"{query.World} is not a world kind", nameof(query));
        if (query.Center is { } centre && (!(query.Radius > 0) || !double.IsFinite(query.Radius * query.Radius) ||
                                            !double.IsFinite(centre.X) || !double.IsFinite(centre.Y) || !double.IsFinite(centre.Z)))
            throw new ArgumentException("a query with a Center needs a finite Center and a finite Radius above 0", nameof(query));
        var quiet = Quiet(query.Mode);
        var t = Members();
        if (t == null) return new(ReadStatus.Unavailable, 0, 0);
        if (quiet && OnServerTick) return new(QuietOnTick((uint)query.World), 0, 0);
        var refs = into.Length == 0 ? null : ArrayPool<EcsObjectRefNative>.Shared.Rent(into.Length);
        try
        {
            fixed (uint* h = hashes)
            fixed (EcsObjectRefNative* o = refs)
            {
                var request = new EcsQueryRequestNative
                {
                    Size = (uint)sizeof(EcsQueryRequestNative), WorldKind = (uint)query.World, ComponentCount = (uint)hashes.Length,
                    Components = (nint)h, Flags = quiet ? EcsReadConstants.QueryFrameQuiet : 0,
                };
                if (query.Center is { } c)
                {
                    request.Flags |= EcsReadConstants.QueryBounds;
                    request.Center = new WorldNative.Vec3 { X = c.X, Y = c.Y, Z = c.Z };
                    request.Radius = query.Radius;
                }
                uint required = 0, state = EcsReadConstants.Unavailable;
                var rc = t->Query(owner, &request, o, (uint)into.Length, &required, &state);
                if (rc == BufferTooSmall) return new(ReadStatus.TooSmall, 0, (int)required);
                if (rc == ErrChanged) return new(ReadStatus.Changed, 0, 0);
                if (rc != Ok) return new(StatusOf(rc, "WorldConcurrent.Query"), 0, 0);
                var count = (int)required;
                if (count > 0 && o[0].ReloadEpoch != owner && firstTry)
                {
                    // Query refs carry the owner's current epoch, which is 0 until it opens a ref under its own. Set
                    // it (an open does) and ask again: else these refs would go stale at the owner's first Open, and
                    // a query ref from before a reload would still hold after it.
                    EcsObjectRefNative scratch;
                    _ = t->EntityOpen(owner, owner, (uint)WorldKind.Prediction, 0, 1, &scratch);
                    return Query(query, into, false);
                }
                for (var i = 0; i < count; i++) into[i] = new ConcurrentEntity(o[i]);
                return new(ReadStatus.Ok, count, count);
            }
        }
        finally
        {
            if (refs != null) ArrayPool<EcsObjectRefNative>.Shared.Return(refs);
        }
    }

    /// <summary>The template the entity was spawned from. <see cref="ReadStatus.Absent"/> when it names none.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ConcurrentRead<Guid> Template(ConcurrentEntity entity)
    {
        var owner = Caller("WorldConcurrent.Template");
        var r = RefOf(entity);
        var t = Members();
        if (t == null) return new(ReadStatus.Unavailable, default);
        var guid = stackalloc byte[16];
        var rc = t->TemplateUuid(owner, &r, guid);
        return rc == Ok ? new(ReadStatus.Ok, new Guid(new ReadOnlySpan<byte>(guid, 16))) : new(StatusOf(rc, "WorldConcurrent.Template"), default);
    }

    /// <summary>The fallback in one line: when the reads work here (<see cref="IsAvailable"/>) <paramref name="offTick"/>
    /// runs at once, in the task; otherwise <paramref name="onTick"/> runs through
    /// <see cref="KilnTaskContext.OnTick{T}(Func{T})"/>, on the task's tick world (<see cref="KilnTaskContext.TickWorld"/>:
    /// the server tick for a task started in server code, the client world's step for one started on the game's main
    /// thread). Either way the result (or the exception) comes back through the Task.
    /// <c>var n = await WorldConcurrent.OrOnTick(ctx, () =&gt; CountFromTask(), () =&gt; CountOnTick());</c></summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Task<T> OrOnTick<T>(KilnTaskContext ctx, Func<T> offTick, Func<T> onTick)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(offTick);
        ArgumentNullException.ThrowIfNull(onTick);
        if (!IsAvailable) return ctx.OnTick(onTick);
        try
        {
            return Task.FromResult(offTick());
        }
        catch (OperationCanceledException e)
        {
            return Task.FromCanceled<T>(e.CancellationToken.IsCancellationRequested ? e.CancellationToken : new CancellationToken(true));
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }

    // ---- internals ----

    private const int Ok = WorldNative.Ok, InvalidArgument = WorldNative.InvalidArgument, NotFound = WorldNative.NotFound,
        Unavailable = WorldNative.Unavailable, BufferTooSmall = WorldNative.BufferTooSmall;
    private const int ErrChanged = EcsReadConstants.ErrChanged, ErrStale = EcsReadConstants.ErrStale,
        ErrUnreadable = EcsReadConstants.ErrUnreadable, ErrBusy = EcsReadConstants.ErrBusy, ErrUnsupported = EcsReadConstants.ErrUnsupported;

    // a component type's size never changes in a process (it is the game build's registration): learnt once per world kind
    private static readonly ConcurrentDictionary<(uint Kind, uint Hash), int> Sizes = new();
    private static EcsReadsTable* _members;
    private static int _state;  // 0: not looked yet, 1: the members and their capability are here, 2: they are not

    /// <summary>Tests: look for the table and its capability again.</summary>
    internal static void Reset()
    {
        _members = null;
        Volatile.Write(ref _state, 0);
        Sizes.Clear();
        ResetWrites();
    }

    private static EcsReadsTable* Table()
    {
        var t = WorldNative.T;
        return t != null && t->Header.Version >= 4 && t->Header.Size >= (uint)sizeof(EcsReadsTable) ? (EcsReadsTable*)t : null;
    }

    // The read members, or null when this kiln.world has none or says they do not work on this build (both fixed at boot).
    private static EcsReadsTable* Members()
    {
        switch (Volatile.Read(ref _state))
        {
            case 1: return _members;
            case 2: return null;
        }
        var t = Table();
        var on = t != null && t->V3.Capabilities != null && (t->V3.Capabilities() & EcsReadConstants.CapEcsRead) != 0;
        _members = on ? t : null;
        Volatile.Write(ref _state, on ? 1 : 2);
        return _members;
    }

    private static (int Rc, EcsContextInfoNative Info) Context()
    {
        var t = Table();
        EcsContextInfoNative info = default;
        if (t == null || t->ContextSnapshot == null) return (Unavailable, info);
        info.Size = (uint)sizeof(EcsContextInfoNative);
        var rc = t->ContextSnapshot(&info);
        return (rc, info);
    }

    private static bool Quiet(ReadMode mode) => mode switch
    {
        ReadMode.Stable => false,
        ReadMode.FrameQuiet => true,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a ReadMode"),
    };

    // The server tick runs inside the server frame: a frame-quiet read there would always meet it.
    // A task's server-world hop or the server tick itself: the frame holds the gate there. A client-world hop (#82) runs on
    // the game's main thread, beside the server world's frame: its calls meet the gate like any task's.
    private static bool OnServerTick =>
        (TaskExecutionScope.OnTick && TaskExecutionScope.TickWorld == WorldKind.Server) || TaskBackend.Current?.OnServerTick == true;

    // A frame-quiet read on the server tick: the server frame is running (busy; a worker can retry), and a client or
    // prediction world has no frame-quiet window at all (unsupported, as kiln.world says it). No engine read either way.
    private static ReadStatus QuietOnTick(uint worldKind) => worldKind == (uint)WorldKind.Server ? ReadStatus.Busy : ReadStatus.Unsupported;

    // The owner the read is for: the task's, or the mod's on the tick. Everything else is refused.
    private static ulong Caller(string api)
    {
        if (TaskExecutionScope.Current is { } run) return run.OwnerId;
        if (Backend.Current.IsTickThread)
        {
            if (ModContext.CurrentMod is { } mod) return mod.Owner;
            throw new InvalidOperationException($"{api} needs a mod: call it from a mod's code on the tick, or inside a Kiln task");
        }
        var from = TaskExecutionScope.FlowedIdentity;
        throw new InvalidOperationException(from is null
            ? $"{api} works inside a Kiln task or on the tick, and this thread is neither (a raw thread or Task.Run). Run the work with Context.Tasks.Run."
            : $"{api} works inside a Kiln task or on the tick, and this code left the task {from} (ConfigureAwait(false), Task.Run or a thread it started). Keep the awaits in the task: drop ConfigureAwait(false).");
    }

    private static EcsObjectRefNative RefOf(ConcurrentEntity entity, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(entity))] string? name = null)
    {
        if (entity.IsNone) throw new ArgumentException("a default ConcurrentEntity: use WorldConcurrent.Open or Query", name);
        return entity.Native;
    }

    private static uint Hash(string component)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        return World.ComponentHash(component);
    }

    private static ReadStatus StatusOf(int rc, string api) => rc switch
    {
        Ok => ReadStatus.Ok,
        NotFound => ReadStatus.Absent,
        ErrStale => ReadStatus.Stale,
        ErrUnreadable => ReadStatus.Unreadable,
        Unavailable => ReadStatus.Unavailable,
        ErrBusy => ReadStatus.Busy,
        ErrUnsupported => ReadStatus.Unsupported,
        ErrChanged => ReadStatus.Changed,
        BufferTooSmall => ReadStatus.TooSmall,
        _ => throw Bug(api, rc),
    };

    private static InvalidOperationException Bug(string api, int rc = InvalidArgument) =>
        new($"{api}: kiln.world refused the call ({WorldNative.Describe(rc)}); this is a bug in Kiln, please report it with kiln.log");

    // One component into p: the stable copy (ecs_snapshot) or a frame-quiet one (ecs_snapshot_many with one spec). The
    // size comes back with Ok and TooSmall, and is remembered for the component type.
    private static (ReadStatus Status, int Required) Copy(EcsReadsTable* t, ulong owner, EcsObjectRefNative* r, uint hash, byte* p,
        uint capacity, bool quiet)
    {
        uint required = 0;
        int rc;
        if (!quiet) rc = t->Snapshot(owner, r, hash, p, capacity, &required);
        else
        {
            var spec = new EcsSnapshotSpecNative { Component = hash, Flags = EcsReadConstants.ReadFrameQuiet };
            uint presence = 0;
            rc = t->SnapshotMany(owner, r, &spec, 1, p, capacity, &required, &presence);
            if (rc == Ok && presence != EcsReadConstants.Ok) return (presence == EcsReadConstants.Absent ? ReadStatus.Absent : (ReadStatus)Math.Min(presence, EcsReadConstants.Unsupported), 0);
        }
        if (rc is Ok or BufferTooSmall) Sizes[(r->WorldKind, hash)] = (int)required;
        if (rc == Ok) return (ReadStatus.Ok, (int)required);
        if (rc == BufferTooSmall) return (ReadStatus.TooSmall, (int)required);
        return (StatusOf(rc, "WorldConcurrent"), 0);
    }

    // A present component's size, for slicing a ReadMany: remembered, or asked of the entity (a zero-capacity snapshot copies
    // nothing and answers the size).
    private static int? SizeOf(EcsReadsTable* t, ulong owner, EcsObjectRefNative* r, uint hash)
    {
        if (Sizes.TryGetValue((r->WorldKind, hash), out var size)) return size;
        uint required = 0;
        var rc = t->Snapshot(owner, r, hash, null, 0, &required);
        if (rc == Ok) required = 0;  // an empty component: nothing to copy, so it fits
        else if (rc != BufferTooSmall || required == 0) return null;
        Sizes[(r->WorldKind, hash)] = (int)required;
        return (int)required;
    }

    private static ConcurrentSnapshot Refused(int count, ReadStatus status)
    {
        var each = new ReadStatus[count];
        Array.Fill(each, status);
        return new ConcurrentSnapshot(status, each, [], new int[count], new int[count]);
    }
}
