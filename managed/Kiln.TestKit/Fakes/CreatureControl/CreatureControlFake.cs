// SPDX-License-Identifier: MIT
// The kit's fake of kiln.creature_control (19d §4.3, §5.2): the broker's rules on the fake world's entities, its steps on
// the harness's steps, so Kiln.CreatureControl's own code (and a mod's lease logic) runs offline.
namespace Kiln.TestKit;

/// <summary>Plugs the fake kiln.creature_control into the kit (<see cref="IKitFake"/>).</summary>
internal sealed class CreatureControlFake : IKitFake
{
    public string Service => CreatureControlAbi.Service;

    public void Register()
    {
        KitBackend.Instance.RegisterTable(CreatureControlAbi.Service, CreatureControlTable.Create());
        FakeTables.Features([CreatureControlAbi.Operational]);
    }

    public void Start(KilnHarness harness) => ModelOf(harness).Start(harness);

    public void BeforeStep(KilnHarness harness, ulong step) => ModelOf(harness).Step();

    public void End(KilnHarness harness) => ModelOf(harness).End();

    public void AddCoverage(List<CoverageRow> rows) => rows.Add(new CoverageRow("Creature control", CoverageKind.RecordingFake,
        [CreatureControlAbi.Service, CreatureControlAbi.Operational],
        "Kiln.CreatureControl's own code on the kit's kiln.creature_control: bodies are the fake world's entities (counter-range " +
        "ids, no players); the roles' priority (Ridden > Vehicle > PetCombat > PetFollow/PetStay/EngineGuard), one base lease per " +
        "body (the same owner's next one replaces it, Released/Canceled \"Replaced\"; another owner's is Busy), one lease per other " +
        "role, lower leases Preempted and resumed with a new transition sequence; an input lease is Active two steps after it " +
        "takes the body (the broker's removal step and its check; at once when the body is held already), an AI-on lease four " +
        "steps after it (three zeroed steps and the restore's check; at once when nothing was removed), and waits while the body " +
        "is busy (KilnHarness.CreatureControl.SetBusy; 5 s fails it Quiescence); a release ends drive permission at once, the last " +
        "one is Released four steps later (also for a Pending lease whose removal was queued; otherwise Canceled the next step); a Vehicle that was Active ends only " +
        "with its body (body_retired); quarantine revokes at once, denies the root (root_policy) and ends the leases Retired once " +
        "the body is gone; a destroyed body fails its leases BodyGone; terminal leases stay queryable 10 minutes; notices per " +
        "owner; KilnHarness.CreatureControl.Calls records each call. Offline: the game's components (nothing is removed or " +
        "zeroed), another owner's AI layer (AiInhibited), the entity counter's identity check, failure injection"));

    public static CreatureControlModel ModelOf(KilnHarness harness) => KitFakes.State<CreatureControlModel>(harness);
}

/// <summary>One call a mod made to the fake kiln.creature_control.</summary>
/// <param name="Step">The harness step.</param>
/// <param name="Function">The table function (<c>bind_body</c>, <c>acquire_control</c>, <c>release_control</c>,
/// <c>release_body</c>, <c>next_request</c>, <c>quarantine</c>, <c>body_retired</c>).</param>
/// <param name="Owner">The caller's native owner.</param>
/// <param name="Entity">The body's entity (0 when none was named).</param>
/// <param name="Lease">The lease (0 when none).</param>
/// <param name="Result">The KilnResult it answered.</param>
/// <param name="Detail">The lease state or reason it answered, in words.</param>
public sealed record FakeControlCall(ulong Step, string Function, ulong Owner, EntityId Entity, ulong Lease, int Result, string Detail);

/// <summary>The broker of one harness (tick thread, except Query, CanDrive, Quarantine and RootPolicy under the lock).</summary>
internal sealed unsafe class CreatureControlModel
{
    private const ulong StepsPerSecond = 25;  // a dedicated server's step rate, as the native service counts its bounds
    private const ulong QuiescenceSteps = 5 * StepsPerSecond, RetainSteps = 600 * StepsPerSecond;
    private const int ReleaseSteps = 4;

    private sealed class Body
    {
        public CreatureControlAbi.BodyKey Key;
        public uint Kind;
        public readonly List<ulong> Refs = [];
        public readonly List<ulong> Leases = [];  // nonterminal
        public ulong Winner, Seq;
        public ulong Target, TargetSince, TargetNeeds, QuiesceSince;
        public int RestoreLeft;
        public bool Held;       // the broker's input configuration is on the body (SteeringAgent removed, AI layer)
        public bool Gone, BehaviorRemoved;
        public uint Latch;      // quarantine cause
    }

    private sealed class Lease
    {
        public ulong Handle, Owner, RequestId, TerminalStep, Order;
        public uint Role, State = CreatureControlAbi.Pending, Result;
        public uint Entity;
        public CreatureControlAbi.BodyKey Key;
        public ulong Seq, NoticeSeq;
        public bool Releasing, WasActive;
        public string Reason = "Pending: waiting for the body's transition";
    }

    private sealed class Owner
    {
        public ulong Next, High, NoticeSeq;
        public bool Resync;
        public readonly Dictionary<ulong, ulong> Requests = [];
        public readonly Queue<CreatureControlAbi.State> Notices = new();
    }

    private readonly object _lock = new();
    private KilnHarness? _harness;
    private static long _nextHandle;  // native handles are process-unique, including across world/harness changes
    private ulong _step, _generation, _order;
    private uint _queued;
    private readonly Dictionary<uint, Body> _bodies = [];
    private readonly Dictionary<ulong, Lease> _leases = [];
    private readonly Dictionary<ulong, Owner> _owners = [];
    private readonly HashSet<uint> _busy = [];
    private readonly HashSet<uint> _denied = [];
    private readonly List<FakeControlCall> _calls = [];

    public const ulong Epoch = 1;

    public void Start(KilnHarness harness) => _harness = harness;

    public void End()
    {
        lock (_lock) _bodies.Clear();
    }

    public IReadOnlyList<FakeControlCall> Calls
    {
        get
        {
            lock (_lock) return [.. _calls];
        }
    }

    public void SetBusy(uint entity, bool busy)
    {
        lock (_lock)
            if (busy) _busy.Add(entity);
            else _busy.Remove(entity);
    }

    public bool Controlled(uint entity)
    {
        lock (_lock) return _bodies.TryGetValue(entity, out var b) && !b.Gone && b.Held;
    }

    private bool Exists(uint entity) => _harness?.EntitiesFake?.Exists(entity) == true && entity >= 1024 && entity <= 0x7fffffff;

    private bool Operational => !FakeTables.Off(CreatureControlAbi.Operational);

    private void Record(string function, ulong owner, uint entity, ulong lease, int result, string detail) =>
        _calls.Add(new FakeControlCall(_step, function, owner, new EntityId(entity), lease, result, detail));

    private static int Priority(uint role) => role switch
    {
        CreatureControlAbi.Quarantine => 5,
        CreatureControlAbi.Ridden => 4,
        CreatureControlAbi.Vehicle => 3,
        CreatureControlAbi.PetCombat => 2,
        CreatureControlAbi.PetFollow or CreatureControlAbi.PetStay or CreatureControlAbi.EngineGuard => 1,
        _ => 0,
    };

    private static bool BaseRole(uint role) => role is CreatureControlAbi.PetFollow or CreatureControlAbi.PetStay or CreatureControlAbi.EngineGuard;

    private static bool AiRole(uint role) => role is CreatureControlAbi.PetCombat or CreatureControlAbi.EngineGuard;

    private static string RoleName(uint role) => role switch
    {
        CreatureControlAbi.PetFollow => "PetFollow",
        CreatureControlAbi.PetCombat => "PetCombat",
        CreatureControlAbi.Ridden => "Ridden",
        CreatureControlAbi.Vehicle => "Vehicle",
        CreatureControlAbi.PetStay => "PetStay",
        CreatureControlAbi.EngineGuard => "EngineGuard",
        _ => "None",
    };

    // ---- table functions ----

    public int Bind(ulong owner, uint entity, uint kind, uint retire, CreatureControlAbi.BodyKey* out_)
    {
        lock (_lock)
        {
            int rc;
            if (out_ == null || owner == 0 || entity == 0 || retire != 1 || kind > 1) rc = FakeTables.InvalidArgument;
            else if (!Operational) rc = FakeTables.Unavailable;
            else if (entity < 1024 || entity > 0x7fffffff) rc = FakeTables.InvalidArgument;
            else if (_bodies.TryGetValue(entity, out var have) && !have.Gone)
            {
                if (have.Kind != kind) rc = FakeTables.InvalidArgument;
                else
                {
                    if (!have.Refs.Contains(owner)) have.Refs.Add(owner);
                    *out_ = have.Key;
                    rc = FakeTables.Ok;
                }
            }
            else if (!Exists(entity)) rc = FakeTables.NotFound;
            else if (_bodies.Count >= 256 || _denied.Count + _bodies.Count(b => !_denied.Contains(b.Key)) >= 65536)
                rc = FakeTables.BufferTooSmall;
            else
            {
                var b = new Body { Kind = kind, Key = new CreatureControlAbi.BodyKey { WorldEpoch = Epoch, BodyGeneration = ++_generation, Entity = entity } };
                b.Refs.Add(owner);
                _bodies[entity] = b;
                *out_ = b.Key;
                rc = FakeTables.Ok;
            }
            Record("bind_body", owner, entity, 0, rc, rc == FakeTables.Ok ? $"generation {out_->BodyGeneration}" : "refused");
            return rc;
        }
    }

    public int NextRequest(ulong owner, ulong* out_)
    {
        if (owner == 0 || out_ == null) return FakeTables.InvalidArgument;
        lock (_lock)
        {
            var o = OwnerRec(owner);
            var last = Math.Max(o.Next, o.High);
            if (last == ulong.MaxValue) return FakeTables.BufferTooSmall;
            o.Next = last + 1;
            *out_ = o.Next;
            Record("next_request", owner, 0, 0, FakeTables.Ok, $"id {o.Next}");
            return FakeTables.Ok;
        }
    }

    public int Acquire(ulong owner, CreatureControlAbi.Request* q, CreatureControlAbi.State* out_)
    {
        if (out_ == null || out_->Size < (uint)sizeof(CreatureControlAbi.State)) return FakeTables.InvalidArgument;
        lock (_lock)
        {
            var rc = AcquireLocked(owner, q, out_, out var reason);
            Record("acquire_control", owner, q != null ? q->Body.Entity : 0, out_->Lease, rc, reason);
            return rc;
        }
    }

    private int AcquireLocked(ulong owner, CreatureControlAbi.Request* q, CreatureControlAbi.State* out_, out string reason)
    {
        int Refuse(uint result, string why, int rc = FakeTables.Ok)
        {
            *out_ = default;
            out_->Size = (uint)sizeof(CreatureControlAbi.State);
            out_->LeaseState = CreatureControlAbi.Failed;
            out_->Result = result;
            if (q != null && q->Size >= (uint)sizeof(CreatureControlAbi.Request))
            {
                out_->RequestedRole = q->Role;
                out_->Body = q->Body;
                out_->RequestId = q->RequestId;
            }
            CreatureControlAbi.SetReason(out_, why);
            return rc;
        }
        reason = "";
        if (owner == 0 || q == null || q->Size < (uint)sizeof(CreatureControlAbi.Request) || q->RequestId == 0 || q->Flags != 0 ||
            q->Reserved != 0 || q->Body.Reserved != 0 || q->Role is CreatureControlAbi.RoleNone or CreatureControlAbi.Quarantine or > 7)
            return Refuse(CreatureControlAbi.ResultNone, reason = "InvalidArgument", FakeTables.InvalidArgument);
        var o = OwnerRec(owner);
        if (o.Requests.TryGetValue(q->RequestId, out var known) && _leases.TryGetValue(known, out var same))
        {
            if (same.Role == q->Role && Same(same.Key, q->Body))
            {
                Write(same, out_);
                reason = "the same lease";
                return FakeTables.Ok;
            }
            return Refuse(CreatureControlAbi.ResultNone, reason = "RequestConflict: this request id was used for another request",
                FakeTables.InvalidArgument);
        }
        if (q->RequestId <= o.High)
            return Refuse(CreatureControlAbi.ExpiredHandle, reason = "ExpiredRequest: the request id is not above the owner's last admitted one");
        if (!Operational) return Refuse(CreatureControlAbi.CapabilityOff, reason = "CapabilityOff: creature_control is off");
        if (q->Body.WorldEpoch != Epoch) return Refuse(CreatureControlAbi.WorldEnded, reason = "WorldEnded: the key is of another server world");
        if (!_bodies.TryGetValue(q->Body.Entity, out var body) || body.Gone || body.Key.BodyGeneration != q->Body.BodyGeneration)
            return Refuse(CreatureControlAbi.BodyGone, reason = "BodyGone: the body is not bound under this key (gone, or stale)");
        if (!body.Refs.Contains(owner))
            return Refuse(CreatureControlAbi.ResultNone, reason = "NotBound: the owner holds no binding of this body (bind_body first)",
                FakeTables.InvalidArgument);
        if (body.Latch != 0) return Refuse(CreatureControlAbi.Busy, reason = "Quarantined: the body is quarantined and will be retired");
        ulong replace = 0;
        foreach (var h in body.Leases)
        {
            var l = _leases[h];
            if (l.Releasing) continue;
            if (BaseRole(q->Role) && BaseRole(l.Role))
            {
                if (l.Owner != owner) return Refuse(CreatureControlAbi.Busy, reason = "Busy: another owner holds the body's base lease");
                replace = h;
            }
            else if (l.Role == q->Role) return Refuse(CreatureControlAbi.Busy, reason = $"Busy: the body has a {RoleName(q->Role)} lease already");
        }
        if (replace == 0 && _leases.Values.Count(l => !Terminal(l)) >= 1024)
            return Refuse(CreatureControlAbi.Busy, reason = "Capacity: 1024 leases are open");
        if (_queued >= 1024)
            return Refuse(CreatureControlAbi.Busy, reason = "QueueFull: the request queue is full this step; try again");
        if (_leases.Count >= 2048)
            return Refuse(CreatureControlAbi.Busy, reason = "Capacity: no lease slot is free");
        if (replace != 0) End(_leases[replace], CreatureControlAbi.Released, CreatureControlAbi.Canceled, "Replaced: the owner asked for another base role");
        var top = body.Leases.Select(h => _leases[h]).Where(l => !l.Releasing).OrderByDescending(l => Priority(l.Role)).FirstOrDefault();
        var lease = new Lease
        {
            Handle = (ulong)Interlocked.Increment(ref _nextHandle), Owner = owner, RequestId = q->RequestId, Role = q->Role, Entity = q->Body.Entity, Key = body.Key,
            Order = ++_order,
        };
        if (top is not null && Priority(top.Role) >= Priority(q->Role))
        {
            lease.State = CreatureControlAbi.Preempted;
            lease.Result = CreatureControlAbi.ResultPreempted;
            lease.Reason = $"Preempted: a {RoleName(top.Role)} lease holds the body";
        }
        _leases[lease.Handle] = lease;
        body.Leases.Add(lease.Handle);
        o.High = q->RequestId;
        o.Next = Math.Max(o.Next, o.High);
        o.Requests[q->RequestId] = lease.Handle;
        ++_queued;
        Notify(lease);
        Write(lease, out_);
        reason = lease.State == CreatureControlAbi.Preempted ? "Preempted" : "Pending";
        return FakeTables.Ok;
    }

    public int Query(ulong handle, CreatureControlAbi.State* out_)
    {
        if (handle == 0 || out_ == null || out_->Size < (uint)sizeof(CreatureControlAbi.State)) return FakeTables.InvalidArgument;
        lock (_lock)
        {
            if (_leases.TryGetValue(handle, out var l))
            {
                Write(l, out_);
                return FakeTables.Ok;
            }
            *out_ = default;
            out_->Size = (uint)sizeof(CreatureControlAbi.State);
            out_->LeaseState = CreatureControlAbi.Failed;
            out_->Result = CreatureControlAbi.ExpiredHandle;
            out_->Lease = handle;
            CreatureControlAbi.SetReason(out_, "ExpiredHandle: no such lease (unknown, or terminal for more than 10 minutes)");
            return FakeTables.Ok;
        }
    }

    public bool CanDrive(ulong owner, ulong handle, CreatureControlAbi.BodyKey key, ulong seq)
    {
        lock (_lock)
            return seq != 0 && _leases.TryGetValue(handle, out var l) && l.Owner == owner && l.State == CreatureControlAbi.Active &&
                   !l.Releasing && l.Seq == seq && Same(l.Key, key) && _bodies.TryGetValue(key.Entity, out var b) && !b.Gone &&
                   b.Latch == 0 && b.Winner == handle;
    }

    public int Release(ulong owner, ulong handle)
    {
        lock (_lock)
        {
            int rc;
            if (owner == 0 || handle == 0) rc = FakeTables.InvalidArgument;
            else if (!_leases.TryGetValue(handle, out var l) || l.Owner != owner) rc = FakeTables.NotFound;
            else if (l.Role == CreatureControlAbi.Vehicle && l.Seq != 0 && !Terminal(l) && _bodies.TryGetValue(l.Entity, out var vb) && !vb.Gone)
                rc = FakeTables.InvalidArgument;  // RetireRequired: a Vehicle that held the body ends with it
            else
            {
                if (!Terminal(l) && !l.Releasing)
                {
                    l.Releasing = true;
                    l.WasActive = l.State == CreatureControlAbi.Active;
                    l.Reason = "Releasing: drive permission ended; the body is being restored";
                    if (_bodies.TryGetValue(l.Entity, out var b))
                    {
                        if (b.Winner == handle) b.Winner = 0;  // can_drive is false from this call on
                        if (b.Target == handle) b.Target = 0;
                        b.RestoreLeft = b.Held ? ReleaseSteps : 1;
                    }
                    Notify(l);
                }
                rc = FakeTables.Ok;
            }
            Record("release_control", owner, 0, handle, rc, rc == FakeTables.Ok ? "releasing" : "refused");
            return rc;
        }
    }

    public int Notices(ulong owner, CreatureControlAbi.State* out_, uint capacity, uint* written, uint* resync)
    {
        if (owner == 0 || written == null || (capacity > 0 && out_ == null)) return FakeTables.InvalidArgument;
        lock (_lock)
        {
            *written = 0;
            if (resync != null) *resync = 0;
            if (!_owners.TryGetValue(owner, out var o)) return FakeTables.Ok;
            uint n = 0;
            while (n < capacity && o.Notices.Count > 0) out_[n++] = o.Notices.Dequeue();
            *written = n;
            if (resync != null) *resync = o.Resync ? 1u : 0u;
            o.Resync = false;
            return FakeTables.Ok;
        }
    }

    public int ReleaseBody(ulong owner, CreatureControlAbi.BodyKey key)
    {
        lock (_lock)
        {
            int rc;
            if (owner == 0 || key.Reserved != 0) rc = FakeTables.InvalidArgument;
            else if (!_bodies.TryGetValue(key.Entity, out var b) || !Same(b.Key, key) || !b.Refs.Contains(owner)) rc = FakeTables.NotFound;
            else if (b.Leases.Any(h => _leases[h].Owner == owner) || (b.Latch != 0 && !b.Gone)) rc = FakeTables.InvalidArgument;
            else
            {
                b.Refs.Remove(owner);
                if (b.Refs.Count == 0 && b.Leases.Count == 0 && (b.Gone || !b.Held)) _bodies.Remove(key.Entity);
                rc = FakeTables.Ok;
            }
            Record("release_body", owner, key.Entity, 0, rc, rc == FakeTables.Ok ? "released" : "refused");
            return rc;
        }
    }

    public int Quarantine(CreatureControlAbi.BodyKey key, uint cause)
    {
        if (cause is < 1 or > 9 || key.Reserved != 0 || key.Entity == 0 || key.BodyGeneration == 0)
            return FakeTables.InvalidArgument;
        lock (_lock)
        {
            if (key.WorldEpoch != Epoch || !_bodies.TryGetValue(key.Entity, out var b) || b.Gone || !Same(b.Key, key))
                return FakeTables.NotFound;
            if (b.Latch == 0 || (b.Latch >= 5 && cause < 5)) b.Latch = cause;  // a semantic down overrides a teardown cause
            _denied.Add(key.Entity);
            b.Winner = 0;
            foreach (var h in b.Leases) Notify(_leases[h]);
            Record("quarantine", 0, key.Entity, 0, FakeTables.Ok, $"cause {cause}");
            return FakeTables.Ok;
        }
    }

    public int BodyRetired(ulong owner, CreatureControlAbi.BodyKey key)
    {
        lock (_lock)
        {
            int rc;
            if (owner == 0 || key.Reserved != 0) rc = FakeTables.InvalidArgument;
            else if (!_bodies.TryGetValue(key.Entity, out var b) || !Same(b.Key, key) || !b.Refs.Contains(owner)) rc = FakeTables.NotFound;
            else if (Exists(key.Entity)) rc = FakeTables.InvalidArgument;  // a live body is never retired by word
            else
            {
                Retire(key.Entity, b, "Retired: the owner destroyed the body");
                rc = FakeTables.Ok;
            }
            Record("body_retired", owner, key.Entity, 0, rc, rc == FakeTables.Ok ? "retired" : "refused");
            return rc;
        }
    }

    public uint RootPolicy(ulong epoch, uint root)
    {
        lock (_lock) return epoch == Epoch && _denied.Contains(root) ? 1u : 0u;
    }

    // ---- the broker's Pre ----

    public void Step()
    {
        lock (_lock)
        {
            _step++;
            _queued = 0;
            foreach (var (entity, b) in _bodies.ToList())
            {
                if (b.Gone) continue;
                if (b.Latch != 0)
                {
                    if (!Exists(entity)) Retire(entity, b, "Retired: the quarantined body is gone");
                    continue;  // no transition, no restore: retirement only
                }
                if (!Exists(entity))
                {
                    foreach (var h in b.Leases.ToList()) End(_leases[h], CreatureControlAbi.Failed, CreatureControlAbi.BodyGone, "BodyGone: the body no longer exists");
                    b.Gone = true;
                    b.Winner = 0;
                    if (b.Refs.Count == 0) _bodies.Remove(entity);
                    continue;
                }
                var want = b.Leases.Select(h => _leases[h]).Where(l => !l.Releasing)
                    .OrderByDescending(l => Priority(l.Role)).ThenBy(l => l.Order).FirstOrDefault();
                if (b.Winner != 0 && b.Winner != want?.Handle)
                {
                    var old = _leases[b.Winner];
                    b.Winner = 0;
                    if (!old.Releasing && want is not null) Preempt(old, want);
                }
                if (want is not null)
                    foreach (var l in b.Leases.Select(h => _leases[h]).Where(l => l != want && !l.Releasing && l.State == CreatureControlAbi.Pending).ToList())
                        Preempt(l, want);
                if (want is null)
                {
                    if (b.RestoreLeft > 0 && --b.RestoreLeft > 0) continue;
                    b.Held = false;
                    SettleReleasing(b);
                    if (b.Refs.Count == 0) _bodies.Remove(entity);
                    continue;
                }
                if (b.Winner == want.Handle) continue;
                if (b.Target != want.Handle)
                {
                    b.Target = want.Handle;
                    b.TargetSince = _step;
                    b.QuiesceSince = _step;
                    // steps from taking the body to the grant, as the native broker's Pres count them
                    b.TargetNeeds = AiRole(want.Role) ? (b.Held ? 3ul : 0ul) : (b.Held ? 0ul : 1ul);
                    if (want.State == CreatureControlAbi.Preempted)
                    {
                        want.State = CreatureControlAbi.Pending;
                        want.Result = CreatureControlAbi.ResultNone;
                        want.Reason = "Resuming: waiting for the body's transition";
                        Notify(want);
                    }
                }
                if (!AiRole(want.Role) && _busy.Contains(entity))
                {
                    if (_step - b.QuiesceSince >= QuiescenceSteps)
                    {
                        b.Target = 0;
                        End(want, CreatureControlAbi.Failed, CreatureControlAbi.TimedOut, "Quiescence: the body's current action did not end within 5 s");
                    }
                    else b.TargetSince = _step + 1;  // the removal is queued at the first quiet Pre, checked at the next
                    continue;
                }
                if (_step - b.TargetSince < b.TargetNeeds)
                {
                    // The first quiet input step applies removal, before the next Pre verifies and grants it.
                    if (!AiRole(want.Role)) b.Held = true;
                    continue;
                }
                b.Held = !AiRole(want.Role);
                if (want.Role == CreatureControlAbi.Vehicle) b.BehaviorRemoved = true;
                want.Seq = ++b.Seq;
                want.State = CreatureControlAbi.Active;
                want.Result = CreatureControlAbi.Granted;
                want.Reason = "Granted";
                b.Winner = want.Handle;
                b.Target = 0;
                Notify(want);
                SettleReleasing(b);
            }
            foreach (var l in _leases.Values.Where(l => Terminal(l) && _step - l.TerminalStep >= RetainSteps).ToList())
            {
                _leases.Remove(l.Handle);
                if (_owners.TryGetValue(l.Owner, out var o)) o.Requests.Remove(l.RequestId);
            }
        }
    }

    // ---- helpers (under the lock) ----

    private void Preempt(Lease l, Lease by)
    {
        l.State = CreatureControlAbi.Preempted;
        l.Result = CreatureControlAbi.ResultPreempted;
        l.Reason = $"Preempted: a {RoleName(by.Role)} lease holds the body";
        Notify(l);
    }

    private void SettleReleasing(Body b)
    {
        foreach (var h in b.Leases.ToList())
        {
            var l = _leases[h];
            if (l.Releasing)
                End(l, CreatureControlAbi.Released, l.WasActive ? CreatureControlAbi.ResultReleased : CreatureControlAbi.Canceled,
                    l.WasActive ? "Released" : "Canceled: released before it was Active");
        }
    }

    private void Retire(uint entity, Body b, string why)
    {
        _denied.Add(entity);
        foreach (var h in b.Leases.ToList()) End(_leases[h], CreatureControlAbi.Failed, CreatureControlAbi.Retired, why);
        _bodies.Remove(entity);
    }

    private Owner OwnerRec(ulong owner)
    {
        if (!_owners.TryGetValue(owner, out var o)) _owners[owner] = o = new Owner();
        return o;
    }

    private static bool Terminal(Lease l) => l.State is CreatureControlAbi.Released or CreatureControlAbi.Failed;

    private static bool Same(in CreatureControlAbi.BodyKey a, in CreatureControlAbi.BodyKey b) =>
        a.WorldEpoch == b.WorldEpoch && a.BodyGeneration == b.BodyGeneration && a.Entity == b.Entity && a.Reserved == b.Reserved;

    private void End(Lease l, uint state, uint result, string reason)
    {
        if (Terminal(l)) return;
        l.State = state;
        l.Result = result;
        l.Reason = reason;
        l.Releasing = false;
        l.TerminalStep = _step;
        if (_bodies.TryGetValue(l.Entity, out var b))
        {
            b.Leases.Remove(l.Handle);
            if (b.Winner == l.Handle) b.Winner = 0;
            if (b.Target == l.Handle) b.Target = 0;
        }
        Notify(l);
    }

    private void Notify(Lease l)
    {
        var o = OwnerRec(l.Owner);
        l.NoticeSeq = ++o.NoticeSeq;
        CreatureControlAbi.State s = default;
        Write(l, &s);
        if (o.Notices.Count < 1024) o.Notices.Enqueue(s);
        else o.Resync = true;
    }

    private void Write(Lease l, CreatureControlAbi.State* s)
    {
        *s = default;
        s->Size = (uint)sizeof(CreatureControlAbi.State);
        s->LeaseState = l.State;
        s->Result = l.Result;
        s->RequestedRole = l.Role;
        s->Body = l.Key;
        s->Lease = l.Handle;
        s->RequestId = l.RequestId;
        s->TransitionSequence = l.Seq;
        s->NoticeSequence = l.NoticeSeq;
        var reason = l.Reason;
        if (!Terminal(l) && _bodies.TryGetValue(l.Entity, out var b) && !b.Gone)
        {
            if (b.Latch != 0)
            {
                s->LeaseState = CreatureControlAbi.Preempted;  // the broker's own policy: lease 0, role Quarantine
                s->Result = CreatureControlAbi.ResultPreempted;
                s->EffectiveRole = CreatureControlAbi.Quarantine;
                reason = $"Quarantined: cause {b.Latch}; the body will be retired";
            }
            else if (b.Winner != 0 && _leases.TryGetValue(b.Winner, out var w))
            {
                s->WinningLease = w.Handle;
                s->EffectiveRole = w.Role;
            }
            if (b.Held) s->ChangedMask = CreatureControlAbi.ChangedSteering | CreatureControlAbi.ChangedAiLayer;
            if (b.BehaviorRemoved) s->ChangedMask |= CreatureControlAbi.ChangedVehicleBehavior;
        }
        CreatureControlAbi.SetReason(s, reason);
    }
}
