// SPDX-License-Identifier: MIT
// kiln.world's four compare-exchange members (kiln_world.h "Compare-exchange writes") answered the way
// native/services/world/ecs_writes.cpp does, over the world an EcsReadsModel provides: the policy's rules (the server world,
// an exact profile with a write gate, a registered component, its tracking bits, plain bytes, its size within the budget),
// owner scoping (a retired owner, another owner's ref), the uncertain latch for the session, the event queue's capacity,
// the gate's busy refusal and its latch per owner and step, the compare, the masked merge, the no-op, and one event per
// committed write, delivered at the next drain. One copy of these rules for every fake: the harness's kiln.world
// (World/EcsReadsFake.cs) and Kiln's own unit tests (Kiln.Api.Tests/FakeEcsReads.cs). The fake has no engine: a test
// makes the gate busy (GateBusyNext), the next write uncertain or rolled back (UncertainNext, RolledBackNext).
using System.Text;

namespace Kiln.TestKit;

/// <summary>What the policy knows of one component (kiln.types' reflection and the engine's registry in the real service).</summary>
internal readonly record struct EcsWriteFacts(bool Registered, int Size, bool TrackingKnown, bool TrackedA, bool TrackedB, bool Plain,
    int ReflectedSize, string? PlainWhy);

internal abstract partial class EcsReadsModel
{
    /// <summary>The build has the writes and their capability bit (KILN_WORLD_CAP_ECS_WRITE).</summary>
    public bool WritesCapable = true;

    /// <summary>The engine's per-component write budget.</summary>
    public int Budget = 65536;

    /// <summary>The next N writes meet a gate the engine holds (busy, and the owner latched for the step).</summary>
    public int GateBusyNext;

    /// <summary>The next N writes are attempted and cannot be proven (uncertain; the target latched for the session).</summary>
    public int UncertainNext;

    /// <summary>The next N writes do not read back and are rolled back (unreadable, nothing changed).</summary>
    public int RolledBackNext;

    /// <summary>The event queue's capacity (4096 in kiln.world).</summary>
    public int EventCapacity = 4096;

    public readonly HashSet<ulong> Retired = [];
    public readonly HashSet<(ulong Session, uint Kind, uint Entity, uint Generation, uint Component)> Latched = [];
    public readonly Dictionary<ulong, ulong> BusyLatch = [];  // owner -> the step of its last busy refusal
    public readonly Queue<EcsWriteEventNative> Pending = [];
    public readonly List<(ulong Handle, ulong Owner, nint Fn, nint User)> Subscriptions = [];
    public ulong NextHandle;
    public int Writes;  // committed writes, for tests

    /// <summary>The policy's facts of a component of the server world.</summary>
    public abstract EcsWriteFacts WriteFacts(uint hash);

    /// <summary>Replaces the entity's component bytes (the same length). False when it has none.</summary>
    public abstract bool WriteComponent(uint kind, uint id, uint hash, ReadOnlySpan<byte> bytes);

    /// <summary>The server step now (the busy latch is per owner and step).</summary>
    public virtual ulong Step => 0;

    /// <summary>kiln.types (the policy's reflection) is here: without it every write is unavailable.</summary>
    public virtual bool TypesAvailable => true;

    // the subscription whose callback runs now, and the draining thread (unsubscribe from another thread waits for it)
    public ulong RunningHandle;
    public int DrainThread;

    private void ResetWrites()
    {
        WritesCapable = true;
        Budget = 65536;
        GateBusyNext = UncertainNext = RolledBackNext = 0;
        EventCapacity = 4096;
        Retired.Clear();
        Latched.Clear();
        BusyLatch.Clear();
        Pending.Clear();
        Subscriptions.Clear();
        Writes = 0;  // NextHandle goes on: a handle is never reused, as in kiln.world (a stale one finds nothing)
        RunningHandle = 0;
        DrainThread = 0;
    }
}

internal static unsafe class EcsWritesLogic
{
    private const int OkRc = EcsReadsLogic.OkRc, InvalidArgument = EcsReadsLogic.InvalidArgument, NotFound = EcsReadsLogic.NotFound,
        Unavailable = EcsReadsLogic.Unavailable;

    // kiln.world's why codes (WriteReason's values)
    private const uint WhyNone = 0, WhyNotServerWorld = 1, WhyNoProfile = 2, WhyNoWorld = 3, WhyUnregistered = 4, WhyTracked = 5,
        WhyTrackingUnknown = 6, WhyNotPlain = 7, WhySize = 8, WhyOverBudget = 9, WhyGate = 11, WhyEventsFull = 12,
        WhyTargetLatched = 13, WhyOwnerRetired = 14, WhyRef = 15, WhyCompare = 16, WhyReadback = 18, WhyNotOwnersRef = 19;

    public static int ResultOf(uint status) => status switch
    {
        EcsWriteConstants.Conflict => EcsWriteConstants.ErrConflict,
        EcsWriteConstants.NotWritable => EcsWriteConstants.ErrNotWritable,
        EcsWriteConstants.Uncertain => EcsWriteConstants.ErrUncertain,
        _ => EcsReadsLogic.ResultOf(status),
    };

    public static uint Capabilities(EcsReadsModel m, uint others)
    {
        bool on;
        lock (m.Gate) on = m.WritesCapable && m.ExactProfile;
        return on && m.TypesAvailable ? others | EcsWriteConstants.CapEcsWrite : others;
    }

    private static void Why(EcsWritePolicyNative* o, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var n = Math.Min(bytes.Length, 95);
        bytes.AsSpan(0, n).CopyTo(new Span<byte>(o->Why, 95));
        o->Why[n] = 0;
    }

    // ecs_write_policy.cpp's decide over the model's facts
    private static int Decide(EcsReadsModel m, uint kind, uint hash, EcsWritePolicyNative* o)
    {
        var size = o->Size;
        *o = default;
        o->Size = size;
        o->WorldKind = kind;
        o->Component = hash;
        bool writes;
        int budget;
        lock (m.Gate) (writes, budget) = (m.WritesCapable && m.ExactProfile, m.Budget);
        writes = writes && m.TypesAvailable;
        o->Budget = writes ? (uint)budget : 0;
        int Refuse(uint status, uint why, string text)
        {
            o->Status = status;
            o->WhyCode = why;
            Why(o, text);
            return status == EcsReadConstants.Unavailable ? Unavailable : OkRc;
        }
        if (kind != (uint)WorldKind.Server)
            return Refuse(EcsReadConstants.Unavailable, WhyNotServerWorld, "only the server world is written from tasks");
        if (!writes)
            return Refuse(EcsReadConstants.Unavailable, WhyNoProfile, m.TypesAvailable
                ? "no exact engine profile for this build"
                : "kiln.types is not here (the policy needs its reflection)");
        if (m.Session(kind) == 0) return Refuse(EcsReadConstants.Unavailable, WhyNoWorld, "no server world runs now");
        var f = m.WriteFacts(hash);
        if (!f.Registered) return Refuse(EcsWriteConstants.NotWritable, WhyUnregistered, "no component is registered under this hash");
        o->ComponentSize = (uint)f.Size;
        if (f.TrackingKnown)
        {
            o->Flags |= EcsWriteConstants.PolicyTrackingKnown;
            if (f.TrackedA) o->Flags |= EcsWriteConstants.PolicyTrackedA;
            if (f.TrackedB) o->Flags |= EcsWriteConstants.PolicyTrackedB;
        }
        if (f.Plain) o->Flags |= EcsWriteConstants.PolicyPlain;
        if (f.Size > budget) o->Flags |= EcsWriteConstants.PolicyOverBudget;
        if (!f.TrackingKnown) return Refuse(EcsWriteConstants.NotWritable, WhyTrackingUnknown, "the component's change-tracking bits could not be read");
        if (f.TrackedA || f.TrackedB)
            return Refuse(EcsWriteConstants.NotWritable, WhyTracked, "the engine tracks changes to this component (write it on the tick)");
        if (!f.Plain) return Refuse(EcsWriteConstants.NotWritable, WhyNotPlain, f.PlainWhy ?? "kiln.types cannot describe the component's type");
        if (f.ReflectedSize != f.Size || f.Size == 0)
            return Refuse(EcsWriteConstants.NotWritable, WhySize, "the reflected size is not the registered size");
        if (f.Size > budget) return Refuse(EcsWriteConstants.NotWritable, WhyOverBudget, "the component is larger than the engine's write budget");
        o->Status = EcsReadConstants.Ok;
        o->Flags |= EcsWriteConstants.PolicyWritable;
        return OkRc;
    }

    public static int WritePolicy(EcsReadsModel m, uint kind, uint hash, EcsWritePolicyNative* o)
    {
        m.Count(nameof(WritePolicy));
        if (o == null || o->Size < (uint)sizeof(EcsWritePolicyNative) || kind > 2) return InvalidArgument;
        return Decide(m, kind, hash, o);
    }

    public static int CompareExchange(EcsReadsModel m, ulong owner, EcsObjectRefNative* r, uint hash, byte* expected, byte* mask, byte* desired,
        uint bytes, byte* confirmed, MutationReplyNative* reply)
    {
        m.Count(nameof(CompareExchange), owner);
        if (owner == 0 || EcsReadsLogic.BadRef(r) || r->WorldKind > 2 || expected == null || desired == null || bytes == 0 || reply == null ||
            reply->Size < (uint)sizeof(MutationReplyNative))
            return InvalidArgument;
        var size = reply->Size;
        *reply = default;
        reply->Size = size;
        reply->Generation = r->Generation;
        reply->Session = r->Session;
        int Finish(uint status, uint why, uint flags = 0)
        {
            reply->Status = status;
            reply->WhyCode = why;
            reply->Flags |= flags;
            return ResultOf(status);
        }
        lock (m.Gate)
            if (m.Retired.Contains(owner)) return Finish(EcsReadConstants.Stale, WhyOwnerRetired);
        if (r->ReloadEpoch != owner) return Finish(EcsReadConstants.Stale, WhyNotOwnersRef);
        if (r->WorldKind != (uint)WorldKind.Server) return Finish(EcsReadConstants.Unavailable, WhyNotServerWorld);
        bool writes;
        lock (m.Gate) writes = m.WritesCapable && m.ExactProfile;
        if (!writes || !m.TypesAvailable) return Finish(EcsReadConstants.Unavailable, WhyNoProfile);
        if (m.Session(r->WorldKind) == 0) return Finish(EcsReadConstants.Unavailable, WhyNoWorld);
        var step = m.Step;
        reply->Step = step;
        EcsWritePolicyNative policy = default;
        policy.Size = (uint)sizeof(EcsWritePolicyNative);
        Decide(m, r->WorldKind, hash, &policy);
        if (policy.Status != EcsReadConstants.Ok) return Finish(policy.Status, policy.WhyCode);
        if (bytes != policy.ComponentSize) return Finish(EcsWriteConstants.NotWritable, WhySize);
        var target = (r->Session, r->WorldKind, r->Entity, r->Generation, hash);
        lock (m.Gate)
            if (m.Latched.Contains(target)) return Finish(EcsWriteConstants.Uncertain, WhyTargetLatched, EcsWriteConstants.MutationTargetLatched);
        if (EcsReadsLogic.Validate(m, owner, r) is var v && v != EcsReadConstants.Ok) return Finish(v, WhyRef);
        lock (m.Gate)
        {
            if (m.Pending.Count >= m.EventCapacity) return Finish(EcsReadConstants.Busy, WhyEventsFull);
            // the gate: the engine's busy refusal latches the owner for the step; the latch refuses before the gate
            if (m.BusyLatch.TryGetValue(owner, out var at) && at == step) return Finish(EcsReadConstants.Busy, WhyGate);
            if (m.FrameRunning || m.GateBusyNext > 0)
            {
                if (!m.FrameRunning) m.GateBusyNext--;
                m.BusyLatch[owner] = step;
                return Finish(EcsReadConstants.Busy, WhyGate);
            }
        }
        reply->HoldNs = 1000;  // the fake holds no gate; a write that reached it reports a hold
        if (m.Component(r->WorldKind, r->Entity, hash) is not { } before) return Finish(EcsReadConstants.Absent, WhyRef);
        if (before.Length != bytes) return Finish(EcsReadConstants.Unreadable, WhyRef);
        if (!before.AsSpan().SequenceEqual(new ReadOnlySpan<byte>(expected, (int)bytes)))
        {
            if (confirmed != null) before.CopyTo(new Span<byte>(confirmed, (int)bytes));
            return Finish(EcsWriteConstants.Conflict, WhyCompare, confirmed != null ? EcsWriteConstants.MutationConfirmed : 0);
        }
        var merged = new byte[bytes];
        for (var i = 0; i < bytes; i++) merged[i] = mask == null || mask[i] != 0 ? desired[i] : before[i];
        if (merged.AsSpan().SequenceEqual(before))
        {
            if (confirmed != null) before.CopyTo(new Span<byte>(confirmed, (int)bytes));
            return Finish(EcsReadConstants.Ok, WhyNone, EcsWriteConstants.MutationNoOp | (confirmed != null ? EcsWriteConstants.MutationConfirmed : 0));
        }
        if (m.Take(ref m.RolledBackNext)) return Finish(EcsReadConstants.Unreadable, WhyReadback, EcsWriteConstants.MutationRolledBack);
        if (m.Take(ref m.UncertainNext))
        {
            m.WriteComponent(r->WorldKind, r->Entity, hash, merged);  // whatever the bytes are now, nothing proves them
            lock (m.Gate) m.Latched.Add(target);
            return Finish(EcsWriteConstants.Uncertain, WhyReadback, EcsWriteConstants.MutationTargetLatched);
        }
        if (!m.WriteComponent(r->WorldKind, r->Entity, hash, merged)) return Finish(EcsReadConstants.Absent, WhyRef);
        if (confirmed != null) merged.CopyTo(new Span<byte>(confirmed, (int)bytes));
        lock (m.Gate)
        {
            m.Writes++;
            m.Pending.Enqueue(new EcsWriteEventNative
            {
                Size = (uint)sizeof(EcsWriteEventNative), WorldKind = r->WorldKind, Entity = r->Entity, Generation = r->Generation,
                Component = hash, Owner = owner, Session = r->Session, Step = step,
            });
        }
        return Finish(EcsReadConstants.Ok, WhyNone,
            EcsWriteConstants.MutationWritten | EcsWriteConstants.MutationEventQueued | (confirmed != null ? EcsWriteConstants.MutationConfirmed : 0));
    }

    public static int Subscribe(EcsReadsModel m, ulong owner, nint fn, nint user, ulong* handle)
    {
        m.Count(nameof(Subscribe));  // not an entity call: its owner is the subscriber's, not a reader's
        if (owner == 0 || fn == 0 || handle == null) return InvalidArgument;
        *handle = 0;
        lock (m.Gate)
        {
            if (m.Retired.Contains(owner)) return Unavailable;
            var h = ++m.NextHandle;
            m.Subscriptions.Add((h, owner, fn, user));
            *handle = h;
        }
        return OkRc;
    }

    public static int Unsubscribe(EcsReadsModel m, ulong handle)
    {
        m.Count(nameof(Unsubscribe));
        lock (m.Gate)
        {
            if (m.Subscriptions.RemoveAll(s => s.Handle == handle) == 0) return NotFound;
            // from another thread: wait for its callback that runs now to return (from inside a callback: at once)
            while (m.RunningHandle == handle && m.DrainThread != Environment.CurrentManagedThreadId) Monitor.Wait(m.Gate);
            return OkRc;
        }
    }

    /// <summary>The owner unloads or reloads: its calls answer stale and its subscriptions end.</summary>
    public static void Retire(EcsReadsModel m, ulong owner)
    {
        lock (m.Gate)
        {
            m.Retired.Add(owner);
            m.Subscriptions.RemoveAll(s => s.Owner == owner);
        }
    }

    /// <summary>kiln.world's drain in its server Pre: every queued event to the subscriptions alive at each call, in order.</summary>
    public static int Drain(EcsReadsModel m)
    {
        var n = 0;
        while (true)
        {
            EcsWriteEventNative e;
            lock (m.Gate)
            {
                if (!m.Pending.TryDequeue(out e)) return n;
            }
            n++;
            (ulong Handle, ulong Owner, nint Fn, nint User)[] subs;
            lock (m.Gate) subs = [.. m.Subscriptions];
            foreach (var s in subs)
            {
                lock (m.Gate)
                {
                    if (!m.Subscriptions.Exists(x => x.Handle == s.Handle)) continue;  // ended during this drain
                    m.RunningHandle = s.Handle;
                    m.DrainThread = Environment.CurrentManagedThreadId;
                }
                try
                {
                    ((delegate* unmanaged[Cdecl]<EcsWriteEventNative*, void*, void>)s.Fn)(&e, (void*)s.User);
                }
                finally
                {
                    lock (m.Gate)
                    {
                        m.RunningHandle = 0;
                        Monitor.PulseAll(m.Gate);
                    }
                }
            }
        }
    }
}
