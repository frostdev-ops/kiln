// SPDX-License-Identifier: MIT
// WorldConcurrent's compare-exchange writes: kiln.world's ecs_write_policy and ecs_compare_exchange (kiln_world.h
// "Compare-exchange writes", native/services/world/ecs_writes.cpp) for Kiln tasks, and ComponentExchangedEvent from its
// committed-write events. Outcomes are the native KILN_ECS_* statuses; an ordinary refusal never throws.
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>The outcome of a <see cref="WorldConcurrent"/> compare-exchange, and the status of a
/// <see cref="ComponentWritePolicy"/>. The values are kiln.world's own statuses.</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
public enum ExchangeStatus
{
    /// <summary>Written and read back (<see cref="ExchangeFlags.Written"/>), or nothing to change
    /// (<see cref="ExchangeFlags.NoOp"/>). For a policy: writable.</summary>
    Ok = 0,

    /// <summary>The entity is gone, or has no such component.</summary>
    Absent = 1,

    /// <summary>The ref's id names another entity now, its world session, component layout or the mod's load changed, or
    /// the ref is another mod's. Nothing was written; open the entity again.</summary>
    Stale = 2,

    /// <summary>A read faulted before any write, or a write that did not read back was undone and the undo proven
    /// (<see cref="ExchangeFlags.RolledBack"/>): the bytes are as they were. Try again.</summary>
    Unreadable = 3,

    /// <summary>The writes do not work here: no exact engine profile or write gate on this game build, an older Kiln, not
    /// the server world, no server world, or it is stopping. Use the tick (<c>World.Write</c>).</summary>
    Unavailable = 4,

    /// <summary>The engine's gate is taken (a server frame runs, or another writer holds it), the mod already met a busy
    /// gate in this server step, the call came from the server tick, or the event queue is full. Nothing was written.
    /// Retry after <c>await ctx.NextGateWindow()</c>.</summary>
    Busy = 5,

    /// <summary>The component's bytes are not the expected ones; <c>Current</c> holds them. Nothing was written. Retry from
    /// the new value.</summary>
    Conflict = 7,

    /// <summary>A task may not write this component (<see cref="WriteReason"/> says why: tracked, not plain bytes, the
    /// wrong size ...). Write it on the tick.</summary>
    NotWritable = 8,

    /// <summary>A write was attempted and neither its readback nor an undo could prove the bytes; or an earlier one to this
    /// target was (<see cref="ExchangeFlags.TargetLatched"/>). The target answers this for the rest of the world's session.
    /// Do not retry.</summary>
    Uncertain = 9,
}

/// <summary>What else a compare-exchange reports (kiln.world's KILN_MUTATION_* flags).</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
[Flags]
public enum ExchangeFlags
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>The bytes were written and read back.</summary>
    Written = 1 << 0,
    /// <summary>The new value equals the current one: nothing was written, no event.</summary>
    NoOp = 1 << 1,
    /// <summary><c>Current</c> holds the component's bytes as the write saw them (the written ones on
    /// <see cref="ExchangeStatus.Ok"/>, the current ones on <see cref="ExchangeStatus.Conflict"/>).</summary>
    Confirmed = 1 << 2,
    /// <summary>A write that did not read back was undone and the undo read back.</summary>
    RolledBack = 1 << 3,
    /// <summary>The target is latched uncertain for the world's session.</summary>
    TargetLatched = 1 << 4,
    /// <summary>A <see cref="ComponentExchangedEvent"/> follows on the tick.</summary>
    EventQueued = 1 << 5,
}

/// <summary>The first rule that decided a compare-exchange or a policy (kiln.world's KILN_ECS_WRITE_WHY_* codes).</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
public enum WriteReason
{
    /// <summary>Nothing refused.</summary>
    None = 0,
    /// <summary>Only the server world is written from tasks.</summary>
    NotServerWorld = 1,
    /// <summary>No exact engine profile for this build, its write gate is off, or kiln.types (the policy's reflection) is
    /// not here.</summary>
    NoProfile = 2,
    /// <summary>No server world runs now.</summary>
    NoWorld = 3,
    /// <summary>No component is registered under the name.</summary>
    Unregistered = 4,
    /// <summary>The game tracks changes to this component (for the save or replication): write it on the tick.</summary>
    Tracked = 5,
    /// <summary>The component's tracking bits could not be read.</summary>
    TrackingUnknown = 6,
    /// <summary>The component's type holds more than plain values (arrays, strings, references ...), or Kiln cannot
    /// describe it.</summary>
    NotPlain = 7,
    /// <summary>The value's size is not the component's.</summary>
    Size = 8,
    /// <summary>The component is larger than the engine's write budget.</summary>
    OverBudget = 9,
    /// <summary>Called on the server tick: the frame holds the gate there.</summary>
    InFrame = 10,
    /// <summary>The engine's gate refused or stopped validating.</summary>
    Gate = 11,
    /// <summary>The event queue is full for now.</summary>
    EventsFull = 12,
    /// <summary>An earlier write to this target was uncertain in this session.</summary>
    TargetLatched = 13,
    /// <summary>The mod is unloading or unloaded.</summary>
    OwnerRetired = 14,
    /// <summary>The ref no longer names the entity.</summary>
    Ref = 15,
    /// <summary>The current bytes are not the expected ones.</summary>
    Compare = 16,
    /// <summary>A guarded read or write faulted.</summary>
    Fault = 17,
    /// <summary>The bytes read back were not the bytes written.</summary>
    Readback = 18,
    /// <summary>The ref was opened by another mod.</summary>
    NotOwnersRef = 19,
    /// <summary>A mod unloaded during the write; nothing was written. Try again.</summary>
    RetireRace = 20,
}

/// <summary>The outcome of <see cref="WorldConcurrent.TryCompareExchange{T}"/>.</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
public readonly struct ExchangeResult<T> where T : unmanaged
{
    internal ExchangeResult(ExchangeStatus status, ExchangeFlags flags, WriteReason reason, T current, TimeSpan hold)
    {
        Status = status;
        Flags = flags;
        Reason = reason;
        Current = current;
        HoldTime = hold;
    }

    /// <summary>What happened.</summary>
    public ExchangeStatus Status { get; }

    /// <summary>The details.</summary>
    public ExchangeFlags Flags { get; }

    /// <summary>Why, when it was refused.</summary>
    public WriteReason Reason { get; }

    /// <summary>The value the write saw (<see cref="ExchangeFlags.Confirmed"/>): the written one on
    /// <see cref="ExchangeStatus.Ok"/>, the current one on <see cref="ExchangeStatus.Conflict"/>; else <c>default</c>.</summary>
    public T Current { get; }

    /// <summary>How long Kiln held the engine's gate (zero when it never took it).</summary>
    public TimeSpan HoldTime { get; }

    /// <summary><see cref="Status"/> is <see cref="ExchangeStatus.Ok"/>.</summary>
    public bool IsOk => Status == ExchangeStatus.Ok;

    /// <summary>The bytes were written (not a no-op).</summary>
    public bool Written => (Flags & ExchangeFlags.Written) != 0;

    /// <inheritdoc/>
    public override string ToString() => Reason == WriteReason.None ? $"{Status} {Flags}" : $"{Status} ({Reason}) {Flags}";
}

/// <summary>The outcome of <see cref="WorldConcurrent.TryCompareExchangeBytes"/>.</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
public readonly struct ExchangeBytesResult
{
    internal ExchangeBytesResult(ExchangeStatus status, ExchangeFlags flags, WriteReason reason, TimeSpan hold)
    {
        Status = status;
        Flags = flags;
        Reason = reason;
        HoldTime = hold;
    }

    /// <summary>What happened.</summary>
    public ExchangeStatus Status { get; }

    /// <summary>The details (<see cref="ExchangeFlags.Confirmed"/>: the caller's <c>current</c> buffer was filled).</summary>
    public ExchangeFlags Flags { get; }

    /// <summary>Why, when it was refused.</summary>
    public WriteReason Reason { get; }

    /// <summary>How long Kiln held the engine's gate (zero when it never took it).</summary>
    public TimeSpan HoldTime { get; }

    /// <summary><see cref="Status"/> is <see cref="ExchangeStatus.Ok"/>.</summary>
    public bool IsOk => Status == ExchangeStatus.Ok;

    /// <summary>The bytes were written (not a no-op).</summary>
    public bool Written => (Flags & ExchangeFlags.Written) != 0;

    /// <inheritdoc/>
    public override string ToString() => Reason == WriteReason.None ? $"{Status} {Flags}" : $"{Status} ({Reason}) {Flags}";
}

/// <summary>Whether a task may write a component, and why not (<see cref="WorldConcurrent.WritePolicy"/>).</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
public readonly struct ComponentWritePolicy
{
    internal ComponentWritePolicy(string component, ExchangeStatus status, WriteReason reason, uint flags, int size, int budget, string text)
    {
        Component = component;
        Status = status;
        Reason = reason;
        _flags = flags;
        Size = size;
        Budget = budget;
        Text = text;
    }

    private readonly uint _flags;

    /// <summary>The component asked about.</summary>
    public string Component { get; }

    /// <summary><see cref="ExchangeStatus.Ok"/> (writable), <see cref="ExchangeStatus.NotWritable"/>,
    /// <see cref="ExchangeStatus.Unavailable"/> or <see cref="ExchangeStatus.Unreadable"/> (ask again).</summary>
    public ExchangeStatus Status { get; }

    /// <summary>The first rule that refused it (<see cref="WriteReason.None"/> when writable).</summary>
    public WriteReason Reason { get; }

    /// <summary>A task may write it with <see cref="WorldConcurrent.TryCompareExchange{T}"/>.</summary>
    public bool IsWritable => Status == ExchangeStatus.Ok && (_flags & EcsWriteConstants.PolicyWritable) != 0;

    /// <summary>The component's registered size: the only size a value may have (0 when unknown).</summary>
    public int Size { get; }

    /// <summary>The engine's per-component write budget in bytes (0 without a gate).</summary>
    public int Budget { get; }

    /// <summary>The engine's change-tracking bits were read.</summary>
    public bool TrackingKnown => (_flags & EcsWriteConstants.PolicyTrackingKnown) != 0;

    /// <summary>The engine tracks changes to it (either category).</summary>
    public bool IsTracked => (_flags & (EcsWriteConstants.PolicyTrackedA | EcsWriteConstants.PolicyTrackedB)) != 0;

    /// <summary>Its type holds plain values only.</summary>
    public bool IsPlain => (_flags & EcsWriteConstants.PolicyPlain) != 0;

    /// <summary>The same in words ("" when writable).</summary>
    public string Text { get; }

    /// <inheritdoc/>
    public override string ToString() => IsWritable ? $"{Component}: writable ({Size} bytes)" : $"{Component}: {Status} ({Reason}): {Text}";
}

/// <summary>A task changed a component by compare-exchange (<see cref="WorldConcurrent.TryCompareExchange{T}"/>, or a
/// native service's). Posted on the server tick after the write's gate was released, once per committed write, in the
/// order of the writes. The bytes are not in the event: read the component if you need them.</summary>
[Experimental("KILNX001", UrlFormat = WorldConcurrent.WritesUrl)]
[ServerOnly, TickThreadOnly]
public sealed class ComponentExchangedEvent : KilnEvent, IEntityEvent
{
    static ComponentExchangedEvent() => WorldExchanges.Ensure();

    /// <summary>Makes the event (Kiln posts it; a mod or a test may make one to test its handler).</summary>
    public ComponentExchangedEvent(EntityId entity, uint generation, uint componentHash, ulong session, ulong step, string? mod)
    {
        Entity = entity;
        Generation = generation;
        ComponentHash = componentHash;
        Session = session;
        Step = step;
        Mod = mod;
    }

    /// <summary>The entity whose component was written.</summary>
    public EntityId Entity { get; }

    /// <summary>Its generation at the write.</summary>
    public uint Generation { get; }

    /// <summary>The component's hash (compare with <see cref="World.ComponentHash"/>).</summary>
    public uint ComponentHash { get; }

    /// <summary>The world: always <see cref="WorldKind.Server"/>.</summary>
    public WorldKind World => WorldKind.Server;

    /// <summary>The server world's session.</summary>
    public ulong Session { get; }

    /// <summary>The server step the write was made after.</summary>
    public ulong Step { get; }

    /// <summary>The id of the mod that wrote it, or null when it was not a mod (a native service).</summary>
    public string? Mod { get; }

    /// <summary>The component is <paramref name="component"/> (a short or qualified name).</summary>
    public bool Is(string component) => global::Kiln.World.ComponentHash(component) == ComponentHash;

    /// <inheritdoc/>
    public bool Involves(EntityId entity) => entity == Entity;
}

public static unsafe partial class WorldConcurrent
{
    internal const string WritesUrl = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/world.md#world-writes-from-task-threads";

    /// <summary>The capability row kiln.world reports for the writes; its text says why they are off.</summary>
    public const string WriteCapability = "world.ecs_write";

    /// <summary>True when the writes work on this game build: kiln.world has them and says so. Any thread.</summary>
    public static bool CanWrite => WriteMembers() != null;

    /// <summary>Whether a task may write <paramref name="component"/> of <paramref name="world"/>, and why not. Any
    /// thread; the answer holds for the world's session (the tracking bits are read again at every write).</summary>
    /// <exception cref="ArgumentException">No component name.</exception>
    public static ComponentWritePolicy WritePolicy(string component, WorldKind world = WorldKind.Server)
    {
        var hash = Hash(component);
        if ((uint)world > 2) throw new ArgumentOutOfRangeException(nameof(world), world, "not a world kind");
        var t = WriteTable();
        if (t == null) return new(component, ExchangeStatus.Unavailable, WriteReason.NoProfile, 0, 0, 0, "kiln.world has no compare-exchange writes here");
        EcsWritePolicyNative p = default;
        p.Size = (uint)sizeof(EcsWritePolicyNative);
        var rc = t->WritePolicy((uint)world, hash, &p);
        if (rc == InvalidArgument) throw Bug("WorldConcurrent.WritePolicy");
        var text = Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p.Why));
        return new(component, (ExchangeStatus)p.Status, (WriteReason)p.WhyCode, p.Flags, (int)p.ComponentSize, (int)p.Budget, text);
    }

    /// <summary>Compares the entity's whole <paramref name="component"/> with <paramref name="expected"/> and, only when they
    /// are equal, writes <paramref name="desired"/>, under the engine's write gate, between server frames. <c>T</c> must be
    /// the whole component (<c>sizeof(T)</c> its registered size: else <see cref="ExchangeStatus.NotWritable"/> with
    /// <see cref="WriteReason.Size"/>). Never waits: a gate that is taken answers <see cref="ExchangeStatus.Busy"/>.
    /// <c>var r = WorldConcurrent.TryCompareExchange(e, "Foo", old, old with { X = 1 });</c></summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>, or no component name.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ExchangeResult<T> TryCompareExchange<T>(ConcurrentEntity entity, string component, in T expected, in T desired) where T : unmanaged
    {
        var (owner, mod) = Writer("WorldConcurrent.TryCompareExchange");
        var r = RefOf(entity);
        var hash = Hash(component);
        T e = expected, d = desired, current = default;
        var (status, flags, reason, hold) = Exchange(owner, mod, &r, hash, (byte*)&e, null, (byte*)&d, (uint)sizeof(T), (byte*)&current,
            "WorldConcurrent.TryCompareExchange");
        return new(status, flags, reason, (flags & ExchangeFlags.Confirmed) != 0 ? current : default, hold);
    }

    /// <summary>The same with bytes: <paramref name="desired"/> merged under <paramref name="mask"/> (a nonzero mask byte
    /// takes the byte from <paramref name="desired"/>; an empty mask takes every byte). <paramref name="current"/>, when
    /// given, gets the bytes the write saw (<see cref="ExchangeFlags.Confirmed"/>). All buffers are the whole component.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is <c>default</c>, no component name, an empty value, or
    /// buffers of different lengths.</exception>
    /// <exception cref="InvalidOperationException">Not inside a Kiln task or on the tick.</exception>
    public static ExchangeBytesResult TryCompareExchangeBytes(ConcurrentEntity entity, string component, ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> desired, ReadOnlySpan<byte> mask = default, Span<byte> current = default)
    {
        var (owner, mod) = Writer("WorldConcurrent.TryCompareExchangeBytes");
        var r = RefOf(entity);
        var hash = Hash(component);
        if (expected.IsEmpty) throw new ArgumentException("an empty value", nameof(expected));
        if (desired.Length != expected.Length) throw new ArgumentException("desired is not as long as expected", nameof(desired));
        if (!mask.IsEmpty && mask.Length != expected.Length) throw new ArgumentException("mask is not as long as expected", nameof(mask));
        if (!current.IsEmpty && current.Length != expected.Length) throw new ArgumentException("current is not as long as expected", nameof(current));
        fixed (byte* pe = expected)
        fixed (byte* pd = desired)
        fixed (byte* pm = mask)
        fixed (byte* pc = current)
        {
            var (status, flags, reason, hold) = Exchange(owner, mod, &r, hash, pe, mask.IsEmpty ? null : pm, pd, (uint)expected.Length,
                current.IsEmpty ? null : pc, "WorldConcurrent.TryCompareExchangeBytes");
            return new(status, flags, reason, hold);
        }
    }

    // ---- internals ----

    private const int ErrConflict = EcsWriteConstants.ErrConflict, ErrNotWritable = EcsWriteConstants.ErrNotWritable,
        ErrUncertain = EcsWriteConstants.ErrUncertain;

    private static EcsWritesTable* _writeMembers;
    private static int _writeState;  // 0: not looked yet, 1: here and on, 2: not

    internal static EcsWritesTable* WriteTable()
    {
        var t = WorldNative.T;
        return t != null && t->Header.Version >= 4 && t->Header.Size >= (uint)sizeof(EcsWritesTable) ? (EcsWritesTable*)t : null;
    }

    // The write members, or null when this kiln.world has none or says they do not work on this build (fixed at boot).
    private static EcsWritesTable* WriteMembers()
    {
        switch (Volatile.Read(ref _writeState))
        {
            case 1: return _writeMembers;
            case 2: return null;
        }
        var t = WriteTable();
        var on = t != null && t->Reads.V3.Capabilities != null && (t->Reads.V3.Capabilities() & EcsWriteConstants.CapEcsWrite) != 0;
        _writeMembers = on ? t : null;
        Volatile.Write(ref _writeState, on ? 1 : 2);
        return _writeMembers;
    }

    internal static void ResetWrites()
    {
        _writeMembers = null;
        Volatile.Write(ref _writeState, 0);
    }

    // The owner the write is for, and its mod's id (for ComponentExchangedEvent.Mod).
    private static (ulong Owner, string? Mod) Writer(string api)
    {
        var owner = Caller(api);
        var mod = TaskExecutionScope.Current is { } run ? run.Mod : ModContext.CurrentMod;
        return (owner, mod?.Info.Id);
    }

    private static (ExchangeStatus, ExchangeFlags, WriteReason, TimeSpan) Exchange(ulong owner, string? mod, EcsObjectRefNative* r, uint hash,
        byte* expected, byte* mask, byte* desired, uint bytes, byte* current, string api)
    {
        var t = WriteMembers();
        if (t == null) return (ExchangeStatus.Unavailable, ExchangeFlags.None, WriteReason.NoProfile, TimeSpan.Zero);
        // The server tick runs inside the frame, which holds the gate: always busy there, and no engine read.
        if (OnServerTick) return (ExchangeStatus.Busy, ExchangeFlags.None, WriteReason.InFrame, TimeSpan.Zero);
        if (mod is not null) WorldExchanges.Writers[owner] = mod;
        WorldExchanges.Rebind();
        MutationReplyNative reply = default;
        reply.Size = (uint)sizeof(MutationReplyNative);
        var rc = t->CompareExchange(owner, r, hash, expected, mask, desired, bytes, current, &reply);
        if (rc == InvalidArgument) throw Bug(api);
        _ = rc;  // the reply says the same, in more detail
        var status = reply.Status switch
        {
            <= EcsReadConstants.Busy or EcsWriteConstants.Conflict or EcsWriteConstants.NotWritable or EcsWriteConstants.Uncertain => (ExchangeStatus)reply.Status,
            _ => throw Bug(api, rc),
        };
        return (status, (ExchangeFlags)reply.Flags, (WriteReason)reply.WhyCode, TimeSpan.FromTicks((long)(reply.HoldNs / 100)));
    }
}

#pragma warning disable KILNX001  // the producer of the experimental event, itself experimental with it
/// <summary>kiln.world's committed-write events as <see cref="ComponentExchangedEvent"/>: one native subscription per
/// process, made when the event type is first used; the native callback (the server tick, kiln.world's Pre) only queues
/// a copy, and a service tick posts the events.</summary>
internal static unsafe class WorldExchanges
{
    internal static readonly ConcurrentDictionary<ulong, string> Writers = new();  // owner -> mod id
    private static readonly ConcurrentQueue<EcsWriteEventNative> Queue = new();
    private static readonly object Gate = new();
    private static object? _backend;
    private static int _registered;
    private static IDisposable? _tick;
    private static ulong _handle;
    private static EcsWritesTable* _table;

    internal static void Ensure()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
            EventProducers.Register(typeof(ComponentExchangedEvent), WorldConcurrent.WriteCapability, Off);
        Bind();
    }

    private static string? Off() => WorldConcurrent.WriteTable() == null ? "kiln.world has no compare-exchange writes here" : null;

    // Once the event type is in use, a new backend (a test kit's next harness) gets its own subscription.
    internal static void Rebind()
    {
        if (Volatile.Read(ref _registered) == 0 || (ReferenceEquals(Backend.Current, Volatile.Read(ref _backend)) && Volatile.Read(ref _handle) != 0)) return;
        Bind();
    }

    // Subscribe once for the current backend (a test kit's harness is a backend of its own).
    internal static void Bind()
    {
        var backend = Backend.Current;
        lock (Gate)
        {
            if (ReferenceEquals(backend, _backend) && _handle != 0) return;
            if (!ReferenceEquals(backend, _backend))
            {
                if (_handle != 0 && _table != null) _table->WriteUnsubscribe(_handle);
                _handle = 0;
                _table = null;
                _tick?.Dispose();
                _tick = null;
                Writers.Clear();
                Queue.Clear();
                _backend = backend;
            }
            var t = WorldConcurrent.WriteTable();
            if (t == null || t->WriteSubscribe == null) return;
            var owner = backend.OwnerCreate("kiln.world.exchanges");
            if (owner == 0) return;
            ulong h = 0;
            if (t->WriteSubscribe(owner, &OnEvent, null, &h) != WorldNative.Ok || h == 0) return;
            _handle = h;
            _table = t;
            _tick = ServiceTick.Add("kiln world exchanges", static (in TickInfo _) => Pump());
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(EcsWriteEventNative* e, void* user)
    {
        try
        {
            if (e != null) Queue.Enqueue(*e);
        }
        catch
        {
            // never let an exception cross back into native code
        }
    }

    // The service tick: the events queued since the last one, in their order.
    internal static void Pump()
    {
        while (Queue.TryDequeue(out var e))
            Events.Post(new ComponentExchangedEvent(new EntityId(e.Entity), e.Generation, e.Component, e.Session, e.Step,
                Writers.TryGetValue(e.Owner, out var mod) ? mod : null));
    }
}
#pragma warning restore KILNX001
