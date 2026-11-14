// SPDX-License-Identifier: MIT
// Kiln.CreatureControl: the managed face of kiln.creature_control (19d §5.2). A mod that drives a creature body by its
// input (an NPC brain, a pet's follower) asks the broker for a lease, waits until it is Active, writes only while the
// lease can drive, and releases it; the broker owns the body's AI-off layer and its SteeringAgent, and restores the body
// when the last lease ends. The calling mod is the lease's owner. The body is bound implicitly (as a game creature with
// its AI); bodies without AI and vehicle leases belong to the native mount service, not to this face.
// Rows: kiln.creature_control (the table: queries, waits and releases) and creature_control (new acquisitions).
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>
/// Leases on creature bodies, from the one broker that owns a controlled body's movement (service
/// <c>kiln.creature_control</c>). Acquire is admission only; <see cref="WaitActive"/> tells when the body is yours
/// (its own AI is off and its SteeringAgent removed, so the input you write moves it); <see cref="Release"/> gives it back
/// and the broker restores what it changed. Every lease change is a <see cref="ControlChangedEvent"/>. A lease belongs to
/// the mod that acquired it and is released when that mod unloads. Since 0.2.0-dev.28.
/// </summary>
[ServerOnly]
public static unsafe partial class CreatureControl
{
    /// <summary>The capability names.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row): queries, waits and releases work.</summary>
        public const string Service = "kiln.creature_control";

        /// <summary>The broker can take new leases (reported by kiln.creature_control: the layout guard matched, its world
        /// program and the AI switch are there).</summary>
        public const string Operational = "creature_control";

        internal static readonly ServiceCapRows Rows = new(Service, Report);

        private static void Report()
        {
            // a missing table: the operational row says so too (the native service, which reports it, is not here)
            if (CreatureControlNative.T == null && Capabilities.Get(Operational).Reason == CapabilityReason.Unknown)
                ServiceCaps.Static(Operational, false, CapabilityReason.ServiceMissing, "kiln.creature_control is not loaded", Service);
        }
    }

    static CreatureControl() => Caps.Rows.Ensure();

    private const double MinWait = 0.01, MaxWait = 10;

    /// <summary>True when kiln.creature_control is loaded (row <c>kiln.creature_control</c>). New leases also need
    /// <c>creature_control</c> (<see cref="Capabilities.Is"/>).</summary>
    [AnyThread]
    public static bool Available => CreatureControlNative.T != null;

    /// <summary>A new request id for the calling mod (unique, increasing). Take it right before <see cref="Acquire"/>:
    /// an id at or below one already admitted is refused as expired.</summary>
    /// <exception cref="KilnUnavailableException"><c>kiln.creature_control</c> is not loaded.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the ids are used up.</exception>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static ControlRequestId NewRequestId()
    {
        var t = Table("CreatureControl.NewRequestId");
        ulong id = 0;
        var rc = t->NextRequest(OwnerOfCaller(), &id);
        if (rc == CreatureControlNative.BufferTooSmall) throw new InvalidOperationException("request ids are used up for this mod");
        Check(rc, "CreatureControl.NewRequestId");
        return new ControlRequestId(id);
    }

    /// <summary>Asks for a lease on <paramref name="body"/> in <paramref name="role"/>. Admission only: an admitted lease
    /// is Pending and grants nothing until <see cref="WaitActive"/> says it is Active. The body is bound as a game creature
    /// with its own AI; the binding goes when the mod's last lease on it ends.</summary>
    /// <returns>Ok with the Pending lease; or Ok false with the broker's reason (the body is gone or taken, a ceiling, an
    /// expired request id, the body cannot be controlled).</returns>
    /// <exception cref="ArgumentException">No body, a player or reserved id, role None, Quarantine or Vehicle (vehicles
    /// belong to the native mount service), request id 0, or a request id used for another request.</exception>
    /// <exception cref="KilnUnavailableException"><c>creature_control</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(Caps.Operational)]
    public static ControlRequestResult Acquire(EntityRef body, ControlRole role, ControlRequestId requestId)
    {
        if (body.IsNone) throw new ArgumentException("no body", nameof(body));
        if (body.Id.Value < 1024 || body.Id.Value > 0x7fffffff)
            throw new ArgumentException($"entity {body.Id} is a player or a reserved id, not a body Kiln can control", nameof(body));
        if (role is ControlRole.None or ControlRole.Quarantine or ControlRole.Vehicle || !Enum.IsDefined(role))
            throw new ArgumentException(role == ControlRole.Vehicle
                ? "Vehicle leases are held by the native mount service, not through Kiln.CreatureControl"
                : $"role {role} cannot be requested", nameof(role));
        if (requestId.Value == 0) throw new ArgumentException("request id 0", nameof(requestId));
        var t = Table("CreatureControl.Acquire");
        if (!Capabilities.Is(Caps.Operational)) throw Off(Caps.Operational, "the creature control broker cannot take new leases");
        if (body.HasSerial && !StillThere(body)) return Refused($"BodyGone: entity {body} is not the body it was");
        var owner = OwnerOfCaller();
        CreatureControlNative.BodyKey key;
        var rc = t->BindBody(owner, body.Id.Value, CreatureControlNative.BaselineEnemy, 1, &key);
        switch (rc)
        {
            case CreatureControlNative.Ok: break;
            case CreatureControlNative.NotFound: return Refused($"BodyGone: entity {body} does not exist");
            case CreatureControlNative.InvalidArgument:
                return Refused($"the body {body} is not a game creature Kiln can lease (a player, or bound as a body without AI)");
            case CreatureControlNative.Unavailable:
                return Capabilities.Is(Caps.Operational)
                    ? Refused($"the body {body} cannot be controlled: no Actor or ActorInput, or its SteeringAgent is already gone " +
                              "(ExternalController: another mod or loader moves it); kiln.log says which")
                    : throw Off(Caps.Operational, "the creature control broker cannot take new leases");
            case CreatureControlNative.BufferTooSmall: return Refused("Capacity: 256 bodies are bound");
            default: Check(rc, "CreatureControl.Acquire"); break;
        }
        var request = new CreatureControlNative.Request
        {
            Size = (uint)sizeof(CreatureControlNative.Request), Role = (uint)role, Body = key, RequestId = requestId.Value,
        };
        CreatureControlNative.State s = default;
        s.Size = (uint)sizeof(CreatureControlNative.State);
        rc = t->AcquireControl(owner, &request, &s);
        var snapshot = rc is CreatureControlNative.Ok or CreatureControlNative.InvalidArgument ? Convert(in s) : null;
        if (rc == CreatureControlNative.InvalidArgument)
        {
            DropBindingIfUnused(owner, key);
            throw new ArgumentException(snapshot?.Reason ?? "the request was refused as invalid", nameof(requestId));
        }
        Check(rc, "CreatureControl.Acquire");
        if (s.Lease == 0)
        {
            DropBindingIfUnused(owner, key);
            return new ControlRequestResult(false, snapshot!.Reason, default, snapshot);
        }
        Track(s.Lease, owner, key, ModContext.CurrentMod, mine: true, snapshot!);
        return new ControlRequestResult(true, null, new ControlLeaseId(s.Lease), snapshot);
    }

    /// <summary>Waits until <paramref name="lease"/> is Active (the body is yours), at most
    /// <paramref name="timeoutSimSeconds"/> of simulation time. A timeout answers Ok false and keeps the lease: release it
    /// yourself if you give up. A lease that ended answers Ok false with its reason.</summary>
    /// <exception cref="ArgumentException">Lease 0, or a timeout outside 0.01..10 s.</exception>
    /// <exception cref="KilnUnavailableException"><c>kiln.creature_control</c> is not loaded.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static Task<ControlLeaseResult> WaitActive(ControlLeaseId lease, double timeoutSimSeconds = 0.5)
    {
        if (lease.Value == 0) throw new ArgumentException("lease 0", nameof(lease));
        if (!double.IsFinite(timeoutSimSeconds) || timeoutSimSeconds < MinWait || timeoutSimSeconds > MaxWait)
            throw new ArgumentOutOfRangeException(nameof(timeoutSimSeconds), timeoutSimSeconds, "a wait is 0.01 to 10 simulation seconds");
        var t = Table("CreatureControl.WaitActive");
        var now = Query(t, lease.Value);
        if (now is null) return Task.FromResult(new ControlLeaseResult(false, "ExpiredHandle: no such lease", null));
        if (now.CanDrive) return Task.FromResult(new ControlLeaseResult(true, null, now));
        if (now.IsTerminal) return Task.FromResult(new ControlLeaseResult(false, now.Reason, now));
        var wait = new Waiter(WaitKind.Active, ModContext.CurrentMod, SimSeconds + timeoutSimSeconds);
        lock (Gate)
        {
            var tracked = TrackLocked(lease.Value, 0, now.Body, null, mine: false, now);
            tracked.Waiters.Add(wait);
        }
        EnsureTick();
        return wait.Source.Task;
    }

    /// <summary>The lease as the broker has it now; null when it has no such lease (unknown, or ended more than 10
    /// minutes of simulation time ago). Any lease, also one a native service holds.</summary>
    /// <exception cref="ArgumentException">Lease 0.</exception>
    /// <exception cref="KilnUnavailableException"><c>kiln.creature_control</c> is not loaded.</exception>
    [AnyThread]
    [RequiresCapability(Caps.Service)]
    public static ControlLeaseSnapshot? Inspect(ControlLeaseId lease)
    {
        if (lease.Value == 0) throw new ArgumentException("lease 0", nameof(lease));
        var t = CreatureControlNative.T;
        if (t == null) throw Off(Caps.Service, "kiln.creature_control is not loaded");
        return Query(t, lease.Value);
    }

    /// <summary>Releases the calling mod's lease. Drive permission ends at once; the task completes when the broker has
    /// restored the body (or handed it to the next lease) and the lease is Released: Ok. Releasing a lease that already
    /// ended Released is Ok again (its reason kept); one that ended by a failure (the body or the world gone, a restore
    /// that failed) answers Ok false with that reason.</summary>
    /// <exception cref="ArgumentException">Lease 0.</exception>
    /// <exception cref="KilnUnavailableException"><c>kiln.creature_control</c> is not loaded.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static Task<ControlLeaseResult> Release(ControlLeaseId lease)
    {
        if (lease.Value == 0) throw new ArgumentException("lease 0", nameof(lease));
        var t = Table("CreatureControl.Release");
        ulong owner;
        lock (Gate) owner = Tracked.TryGetValue(lease.Value, out var known) && known.Owner != 0 ? known.Owner : OwnerOfCaller();
        var rc = t->ReleaseControl(owner, lease.Value);
        var now = Query(t, lease.Value);
        if (rc == CreatureControlNative.NotFound)
            return Task.FromResult(now is null
                ? new ControlLeaseResult(false, "ExpiredHandle: no such lease", null)
                : new ControlLeaseResult(false, "the lease belongs to another owner", now));
        Check(rc, "CreatureControl.Release");
        if (now is null) return Task.FromResult(new ControlLeaseResult(false, "ExpiredHandle: no such lease", null));
        if (now.IsTerminal) return Task.FromResult(Ended(now));
        var wait = new Waiter(WaitKind.Released, ModContext.CurrentMod, double.PositiveInfinity);
        lock (Gate)
        {
            var tracked = TrackLocked(lease.Value, owner, now.Body, null, mine: false, now);
            tracked.Waiters.Add(wait);
        }
        EnsureTick();
        return wait.Source.Task;
    }

    // ---- the tracked leases ----

    private enum WaitKind { Active, Released }

    private sealed class Waiter(WaitKind kind, ModContext? mod, double deadline)
    {
        public readonly WaitKind Kind = kind;
        public readonly ModContext? Mod = mod;
        public readonly double Deadline = deadline;
        public readonly TaskCompletionSource<ControlLeaseResult> Source = new();
    }

    private sealed class Lease(ulong handle, ulong owner, CreatureControlNative.BodyKey key, ModContext? mod, bool mine, ControlLeaseSnapshot last)
    {
        public readonly ulong Handle = handle;
        public ulong Owner = owner;
        public readonly CreatureControlNative.BodyKey Key = key;
        public ModContext? Mod = mod;
        public bool Mine = mine;  // acquired here: its binding reference is dropped when it ends
        public ControlLeaseSnapshot Last = last;
        public readonly List<Waiter> Waiters = [];
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Lease> Tracked = [];
    private static readonly Dictionary<(ulong Owner, ulong Epoch, ulong Generation, uint Entity), int> Bindings = [];
    private static readonly HashSet<ModContext> Watched = [];
    private static IDisposable? _tick;
    private static int _tickGeneration = -1;
    private static double _simSeconds;
    private static ulong _facadeOwner;

    // simulation seconds counted by the service tick (the 60 Hz game clock)
    private static double SimSeconds => Volatile.Read(ref _simSeconds);

    private static void Track(ulong handle, ulong owner, CreatureControlNative.BodyKey key, ModContext? mod, bool mine, ControlLeaseSnapshot first)
    {
        lock (Gate) TrackLocked(handle, owner, first.Body, key, mine, first, mod);
        EnsureTick();
    }

    private static Lease TrackLocked(ulong handle, ulong owner, ControlBodyKey body, CreatureControlNative.BodyKey? native, bool mine,
        ControlLeaseSnapshot last, ModContext? mod = null)
    {
        if (Tracked.TryGetValue(handle, out var have))
        {
            if (mine && !have.Mine)
            {
                have.Mine = true;
                have.Owner = owner;
                have.Mod = mod;
                Bind(owner, have.Key, +1);
            }
            return have;
        }
        var key = native ?? new CreatureControlNative.BodyKey
        {
            WorldEpoch = body.WorldEpoch, BodyGeneration = body.BodyGeneration, Entity = body.Entity.Value,
        };
        var lease = new Lease(handle, owner, key, mod, mine, last);
        Tracked[handle] = lease;
        if (mine) Bind(owner, key, +1);
        if (mine && mod is not null && Watched.Add(mod)) mod.Retiring += () => Retire(mod);
        return lease;
    }

    private static int Bind(ulong owner, CreatureControlNative.BodyKey key, int delta)
    {
        var k = (owner, key.WorldEpoch, key.BodyGeneration, key.Entity);
        Bindings.TryGetValue(k, out var n);
        n += delta;
        if (n <= 0) Bindings.Remove(k);
        else Bindings[k] = n;
        return n;
    }

    private static void DropBindingIfUnused(ulong owner, CreatureControlNative.BodyKey key)
    {
        bool unused;
        lock (Gate) unused = !Bindings.ContainsKey((owner, key.WorldEpoch, key.BodyGeneration, key.Entity));
        var t = CreatureControlNative.T;
        if (unused && t != null) t->ReleaseBody(owner, key);
    }

    private static void EnsureTick()
    {
        lock (Gate)
        {
            if (_tick is not null && _tickGeneration == ServiceTick.Generation) return;
            _tickGeneration = ServiceTick.Generation;
            _tick = ServiceTick.Add("kiln.creature_control leases", Poll);
        }
    }

    // The start of a step (tick thread, no mod context): what the broker did since. A change posts ControlChangedEvent,
    // completes the waits it answers, and a lease of this face that ended gives its binding back.
    private static void Poll(in TickInfo tick)
    {
        Volatile.Write(ref _simSeconds, _simSeconds + tick.ClockDelta / 60.0);
        var t = CreatureControlNative.T;
        if (t == null) return;
        Lease[] leases;
        lock (Gate)
        {
            if (Tracked.Count == 0) return;
            leases = [.. Tracked.Values];
        }
        var now = SimSeconds;
        foreach (var lease in leases)
        {
            var after = Query(t, lease.Handle) ?? Expired(lease.Last);
            ControlLeaseSnapshot? before;
            lock (Gate)
            {
                before = lease.Last;
                lease.Last = after;
            }
            if (!Same(before, after) && Events.HasSubscribers<ControlChangedEvent>())
                Events.Post(new ControlChangedEvent(before, after));
            Answer(lease, after, now);
        }
    }

    private static void Answer(Lease lease, ControlLeaseSnapshot after, double now)
    {
        List<(Waiter, ControlLeaseResult)> done = [];
        bool drop;
        lock (Gate)
        {
            foreach (var w in lease.Waiters)
            {
                if (w.Kind == WaitKind.Active)
                {
                    if (after.CanDrive) done.Add((w, new ControlLeaseResult(true, null, after)));
                    else if (after.IsTerminal) done.Add((w, new ControlLeaseResult(false, after.Reason, after)));
                    else if (now >= w.Deadline)
                        done.Add((w, new ControlLeaseResult(false, $"timed out after the wait; the lease is kept ({after.State}: {after.Reason})", after)));
                }
                else if (after.IsTerminal)
                {
                    done.Add((w, Ended(after)));
                }
            }
            foreach (var (w, _) in done) lease.Waiters.Remove(w);
            drop = after.IsTerminal && lease.Waiters.Count == 0;
            if (drop) Tracked.Remove(lease.Handle);
        }
        foreach (var (w, r) in done) w.Source.TrySetResult(r);
        if (drop && lease.Mine)
        {
            int left;
            lock (Gate) left = Bind(lease.Owner, lease.Key, -1);
            var t = CreatureControlNative.T;
            if (left == 0 && t != null) t->ReleaseBody(lease.Owner, lease.Key);
        }
    }

    // The mod unloads: its leases are released (the broker restores their bodies), its waits end, and the bookkeeping
    // goes on without it until the leases have ended and their bindings are given back.
    private static void Retire(ModContext mod)
    {
        var t = CreatureControlNative.T;
        List<Waiter> ended = [];
        List<Lease> release = [];
        lock (Gate)
        {
            Watched.Remove(mod);
            foreach (var lease in Tracked.Values)
            {
                foreach (var w in lease.Waiters.Where(w => ReferenceEquals(w.Mod, mod)).ToList())
                {
                    lease.Waiters.Remove(w);
                    ended.Add(w);
                }
                if (lease.Mine && ReferenceEquals(lease.Mod, mod))
                {
                    lease.Mod = null;
                    release.Add(lease);
                }
            }
        }
        foreach (var lease in release)
            if (t != null && Game.IsTickThread) t->ReleaseControl(lease.Owner, lease.Handle);
        foreach (var w in ended) w.Source.TrySetCanceled();  // the mod's continuations do not run on its unload
    }

    // ---- helpers ----

    private static CreatureControlNative.Table* Table(string api)
    {
        var t = CreatureControlNative.T;
        if (t == null) throw Off(Caps.Service, "kiln.creature_control is not loaded");
        Services.RequireTickThread(api);
        return t;
    }

    private static KilnUnavailableException Off(string capability, string detail)
    {
        Caps.Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }

    private static void Check(int rc, string api)
    {
        switch (rc)
        {
            case CreatureControlNative.Ok: return;
            case CreatureControlNative.WrongThread: throw new InvalidOperationException($"{api} must be called on the tick thread");
            case CreatureControlNative.Unavailable: throw Off(Caps.Operational, $"{api}: kiln.creature_control answered unavailable");
            case CreatureControlNative.InvalidArgument: throw new ArgumentException($"{api}: the broker refused an argument");
            default: throw new InvalidOperationException($"{api}: kiln.creature_control answered {rc}");
        }
    }

    private static ulong OwnerOfCaller()
    {
        if (ModContext.CurrentMod is { Owner: not 0 } mod) return mod.Owner;
        if (_facadeOwner == 0) _facadeOwner = Backend.Current.OwnerCreate("kiln.creature_control.managed");
        return _facadeOwner != 0 ? _facadeOwner : 1;
    }

    private static bool StillThere(EntityRef body)
    {
        try
        {
            return body.IsAlive;
        }
        catch (KilnUnavailableException)
        {
            return true;  // no entity source to tell: the broker checks the id
        }
    }

    private static ControlRequestResult Refused(string reason) => new(false, reason, default, null);

    private static ControlLeaseResult Ended(ControlLeaseSnapshot s) =>
        s.State == ControlLeaseState.Released ? new ControlLeaseResult(true, null, s) : new ControlLeaseResult(false, s.Reason, s);

    private static ControlLeaseSnapshot? Query(CreatureControlNative.Table* t, ulong lease)
    {
        CreatureControlNative.State s = default;
        s.Size = (uint)sizeof(CreatureControlNative.State);
        if (t->QueryControl(0, lease, &s) != CreatureControlNative.Ok) return null;
        return s.Result == (uint)ControlResultCode.ExpiredHandle && s.WinningLease == 0 && s.TransitionSequence == 0 &&
               s.LeaseState == (uint)ControlLeaseState.Failed && s.RequestId == 0
            ? null
            : Convert(in s);
    }

    private static ControlLeaseSnapshot Expired(ControlLeaseSnapshot last) =>
        last with { State = ControlLeaseState.Failed, Result = ControlResultCode.ExpiredHandle, WinningLease = default, Reason = "ExpiredHandle: no such lease" };

    private static bool Same(ControlLeaseSnapshot? a, ControlLeaseSnapshot b) =>
        a is not null && a.State == b.State && a.Result == b.Result && a.EffectiveRole == b.EffectiveRole &&
        a.WinningLease == b.WinningLease && a.TransitionSequence == b.TransitionSequence && a.ChangedMask == b.ChangedMask &&
        a.Reason == b.Reason;

    internal static ControlLeaseSnapshot Convert(in CreatureControlNative.State s) => new(
        new ControlBodyKey(s.Body.WorldEpoch, s.Body.BodyGeneration, new EntityId(s.Body.Entity)),
        new ControlLeaseId(s.Lease), new ControlLeaseId(s.WinningLease), new ControlRequestId(s.RequestId),
        (ControlRole)s.RequestedRole, (ControlRole)s.EffectiveRole, (ControlLeaseState)s.LeaseState, (ControlResultCode)s.Result,
        s.TransitionSequence, s.NoticeSequence, s.ChangedMask, CreatureControlNative.ReasonOf(in s));
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the rows kiln.creature_control and
/// creature_control say why they are off before a mod asks (with no native service: "not loaded").</summary>
internal static class CreatureControlStartup
{
    static CreatureControlStartup() => CreatureControl.Caps.Rows.Ensure();
}
