// SPDX-License-Identifier: MIT
// A fake of the core's task tables for the host bridge's tests: kiln.internal.tasks.host (native/core/src/tasks_host.h)
// and the two kiln.tasks members the host uses, as real native tables of function pointers over real worker threads
// and a real tick thread. It keeps the native rules: segments of one lease never overlap, one armed registration at a
// time, finish only inside a running segment, a queued lease cancelled without a dispatch, the end notification once
// per accepted lease on a worker after its last hop, tick hops only in a live server session with the per-owner and
// per-step budgets, after-step resumptions tied to their session. A call the native table answers with INTERNAL is
// recorded in Errors: the host must never make one.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Kiln.Host;

namespace Kiln.Host.Tests;

internal sealed unsafe class FakeNativeTasks : IDisposable
{
    internal sealed class Lease
    {
        public ulong Id, Owner;
        public string Name = "";
        public uint Flags, Priority;
        public nint HostState;
        public uint State = Queued;
        public bool SegmentRunning, Armed, ResumePending, AfterPending, Waiting, Finished, Cancel, EndQueued, Dispatched;
        public ulong Registration, Registrations;
        public (uint Session, ulong Edge) AfterAt, WaitAt;
        public uint Outcome;
        public int HopsPending;
        public bool Terminal => State >= Completed;
    }

    public const uint None = 0, Queued = 1, Running = 2, Completed = 3, Faulted = 4, Cancelled = 5;
    public const string Stamp = "0.2.0-test+20260101.abcdef0";
    public const int HopsPerOwner = 64;
    public const long HopOwnerNs = 2_000_000, HopStepNs = 4_000_000;

    private static FakeNativeTasks? s_current;

    private readonly object _lock = new();
    private readonly List<Thread> _workers = [];
    private readonly Thread _tickThread;
    private readonly BlockingCollection<Action> _tickJobs = new();
    private readonly LinkedList<Lease> _ready = new();
    private readonly Queue<Lease> _ends = new();
    private readonly Dictionary<ulong, Lease> _leases = [];
    private readonly List<(ulong Owner, Queue<Lease> Hops)> _hops = [];
    private readonly HashSet<ulong> _sealed = [], _retired = [];
    private readonly KilnTasksHostApi* _host;
    private readonly KilnTasksApi* _tasks;
    private readonly byte* _stamp;
    private delegate* unmanaged[Cdecl]<ulong, uint, uint, void*, void> _dispatch;
    private delegate* unmanaged[Cdecl]<ulong, uint, void*, void> _ended;
    private ulong _nextLease = 0x1000;
    private uint _session, _sessions;
    private ulong _edge;
    private int _running, _hopTurn;
    private bool _disposed;
    [ThreadStatic] private static FakeNativeTasks? t_worker;

    public readonly List<string> Errors = [];
    public readonly List<string> Leaks = [];
    /// <summary>Ended leases by final state, and the fault ring the diagnostics members serve.</summary>
    public readonly Dictionary<ulong, (ulong Completed, ulong Faulted, ulong Cancelled)> OwnerTotals = [];
    public readonly List<(ulong Sequence, Lease Lease, uint State)> Faults = [];
    public readonly List<(ulong Owner, string Name, uint Flags, uint Priority)> Admitted = [];
    public readonly List<(ulong Lease, uint State, bool OnWorker)> Ends = [];
    public readonly List<(ulong Owner, ulong Lease, bool Cancelled)> HopsRun = [];
    public int OwnerCapacity = 1024;
    public long HopCostNs;       // what each dispatched hop "takes" on the fake hop clock
    public int Binds, EndedBinds;
    public int AfterStepCalls, AfterStepInCalls;  // which after-step member the host used

    public FakeNativeTasks(int workers = 2, string stamp = Stamp)
    {
        _host = (KilnTasksHostApi*)NativeMemory.AllocZeroed((nuint)sizeof(KilnTasksHostApi));
        _tasks = (KilnTasksApi*)NativeMemory.AllocZeroed((nuint)sizeof(KilnTasksApi));
        _stamp = (byte*)Marshal.StringToCoTaskMemUTF8(stamp);
        *_host = new KilnTasksHostApi
        {
            Size = (uint)sizeof(KilnTasksHostApi),
            Version = 1,
            BuildStamp = &HBuildStamp,
            Bind = &HBind,
            Admit = &HAdmit,
            Arm = &HArm,
            Resume = &HResume,
            ResumeAfterStep = &HResumeAfterStep,
            TickHop = &HTickHop,
            Finish = &HFinish,
            IsCancelled = &HIsCancelled,
            SealOwner = &HSealOwner,
            Snapshot = &HSnapshot,
            BindEnded = &HBindEnded,
            ResumeAfterStepIn = &HResumeAfterStepIn,
            OnPoolThread = &HOnPoolThread,
            ServerSession = &HServerSession,
            StatsOwnerCount = &HStatsOwnerCount,
            StatsOwnerRow = &HStatsOwnerRow,
            StatsRunningRow = &HStatsRunningRow,
            StatsFaultRow = &HStatsFaultRow,
            StatsTotals = &HStatsTotals,
        };
        *_tasks = new KilnTasksApi { Size = (uint)sizeof(KilnTasksApi), Version = 1, Cancel = &TCancel, Status = &TStatus };
        s_current = this;
        for (var i = 0; i < workers; i++)
        {
            var t = new Thread(WorkerLoop) { IsBackground = true, Name = "fake-task-" + i };
            _workers.Add(t);
            t.Start();
        }
        _tickThread = new Thread(TickLoop) { IsBackground = true, Name = "fake-tick" };
        _tickThread.Start();
    }

    /// <summary>The tables, as <c>KilnApi.service</c> hands them out.</summary>
    public nint HostTable => (nint)_host;
    public nint TasksTable => (nint)_tasks;

    /// <summary>What a core of another build would offer: another size or version in the table's header.</summary>
    public void SetHeader(uint? size = null, uint? version = null)
    {
        if (size is { } s) _host->Size = s;
        if (version is { } v) _host->Version = v;
    }

    public void ClearTickHop() => _host->TickHop = null;

    public nint Service(string name, uint minVersion) => name switch
    {
        KilnTasksHostApi.Name => HostTable,
        KilnTasksApi.Name => TasksTable,
        _ => 0,
    };

    public int TickThreadId => _tickThread.ManagedThreadId;
    public bool IsTickThread => Thread.CurrentThread == _tickThread;
    public bool IsWorker(int managedThreadId) => _workers.Any(w => w.ManagedThreadId == managedThreadId);

    // ---- kiln.internal.tasks.host ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* HBuildStamp() => s_current!._stamp;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HBind(delegate* unmanaged[Cdecl]<ulong, uint, uint, void*, void> dispatch)
    {
        var f = s_current!;
        lock (f._lock)
        {
            if (dispatch == null) return KilnAbi.ErrInvalidArgument;
            if (f._dispatch != null && f._dispatch != dispatch) return KilnAbi.ErrInvalidArgument;
            f._dispatch = dispatch;
            f.Binds++;
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HBindEnded(delegate* unmanaged[Cdecl]<ulong, uint, void*, void> ended)
    {
        var f = s_current!;
        lock (f._lock)
        {
            if (ended == null) return KilnAbi.ErrInvalidArgument;
            if (f._ended != null && f._ended != ended) return KilnAbi.ErrInvalidArgument;
            f._ended = ended;
            f.EndedBinds++;
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HAdmit(ulong owner, KilnTaskInfo* info, void* hostState, ulong* lease, uint* refusal)
    {
        var f = s_current!;
        lock (f._lock)
        {
            *lease = 0;
            int Refuse(int rc, uint why)
            {
                if (refusal != null) *refusal = why;
                return rc;
            }
            if (f._dispatch == null) return Refuse(KilnAbi.ErrUnavailable, 4);
            if (owner == 0 || info == null || info->Size < (uint)sizeof(KilnTaskInfo) || (info->Flags & ~3u) != 0 || info->Priority > 2 ||
                info->Reserved != 0 || info->Name.Len is 0 or > 128)
                return Refuse(KilnAbi.ErrInvalidArgument, 5);
            if (f._retired.Contains(owner)) return Refuse(KilnAbi.ErrRevoked, 2);
            if (f._sealed.Contains(owner)) return Refuse(KilnAbi.ErrUnavailable, 2);
            if (f._leases.Values.Count(l => l.Owner == owner) >= f.OwnerCapacity) return Refuse(KilnAbi.ErrUnavailable, 1);
            var l = new Lease
            {
                Id = ++f._nextLease, Owner = owner, Name = info->Name.ToString(), Flags = info->Flags, Priority = info->Priority,
                HostState = (nint)hostState,
            };
            f._leases[l.Id] = l;
            f.Admitted.Add((owner, l.Name, l.Flags, l.Priority));
            f.Ready(l);
            *lease = l.Id;
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HArm(ulong lease, ulong* registration)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var l = f.Find(lease);
            if (l is null || !l.SegmentRunning || l.Armed || l.ResumePending || l.AfterPending || l.Finished) return f.Misuse("arm", l);
            l.Armed = true;
            l.Registration = ++l.Registrations;
            *registration = l.Registration;
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HResume(ulong lease, ulong registration)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var l = f.Find(lease);
            if (l is null || !l.Armed || l.Registration != registration) return f.Misuse("resume", l);
            l.Armed = false;
            l.Registration = 0;
            if (l.SegmentRunning) l.ResumePending = true;  // segments never overlap
            else f.Ready(l);
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HResumeAfterStep(ulong lease, ulong registration, uint* refusal)
    {
        var f = s_current!;
        lock (f._lock)
        {
            f.AfterStepCalls++;
            return f.ResumeAfter(lease, registration, f._session, refusal);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HResumeAfterStepIn(ulong lease, ulong registration, uint session, uint* refusal)
    {
        var f = s_current!;
        lock (f._lock)
        {
            f.AfterStepInCalls++;
            if (session == 0 || session != f._session)
            {
                if (refusal != null) *refusal = 7;
                return KilnAbi.ErrUnavailable;  // still armed
            }
            return f.ResumeAfter(lease, registration, session, refusal);
        }
    }

    private int ResumeAfter(ulong lease, ulong registration, uint session, uint* refusal)
    {
        if (session == 0)
        {
            if (refusal != null) *refusal = 7;
            return KilnAbi.ErrUnavailable;
        }
        var l = Find(lease);
        if (l is null || !l.Armed || l.Registration != registration) return Misuse("resume_after_step", l);
        l.Armed = false;
        l.Registration = 0;
        if (l.SegmentRunning)
        {
            l.AfterPending = true;
            l.AfterAt = (session, _edge);
        }
        else
        {
            PlaceAfter(l, (session, _edge));
        }
        return KilnAbi.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HTickHop(ulong lease)
    {
        var f = s_current!;
        lock (f._lock)
        {
            if (f._session == 0) return KilnAbi.ErrUnavailable;
            var l = f.Find(lease);
            if (l is null || l.EndQueued) return f.Misuse("tick_hop", l);
            l.HopsPending++;
            var queue = f._hops.FirstOrDefault(q => q.Owner == l.Owner).Hops;
            if (queue is null)
            {
                queue = new Queue<Lease>();
                f._hops.Add((l.Owner, queue));
            }
            queue.Enqueue(l);
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HFinish(ulong lease, uint outcome)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var l = f.Find(lease);
            if (outcome > 2 || l is null || !l.SegmentRunning || l.Finished || l.Armed || l.ResumePending || l.AfterPending)
                return f.Misuse("finish", l);
            l.Finished = true;
            l.Outcome = outcome;
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint HIsCancelled(ulong lease)
    {
        var f = s_current!;
        lock (f._lock) return f.Find(lease) is { Cancel: true } ? 1u : 0u;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HSealOwner(ulong owner, uint* outstanding)
    {
        var f = s_current!;
        lock (f._lock)
        {
            f._sealed.Add(owner);
            *outstanding = f.SignalOwner(owner);
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HSnapshot(KilnLeaseSnapshot* rows, uint capacity, uint* count)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var live = f._leases.Values.Where(l => l.State == Running).ToList();
            *count = (uint)live.Count;
            if (live.Count > capacity) return KilnAbi.ErrBufferTooSmall;
            for (var i = 0; i < live.Count; i++)
            {
                rows[i] = default;
                rows[i].Size = (uint)sizeof(KilnLeaseSnapshot);
                rows[i].State = Running;
                rows[i].Owner = live[i].Owner;
                rows[i].Lease = live[i].Id;
                rows[i].RunningNs = live[i].SegmentRunning ? 1_000_000u : 0u;
                var name = Encoding.UTF8.GetBytes(live[i].Name);
                for (var b = 0; b < name.Length && b < 128; b++) rows[i].Name[b] = name[b];
            }
            return KilnAbi.Ok;
        }
    }

    // ---- the appended diagnostics members (issue #49) ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint HStatsOwnerCount()
    {
        var f = s_current!;
        lock (f._lock) return (uint)f._leases.Values.Select(l => l.Owner).Union(f.OwnerTotals.Keys).Distinct().Count();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HStatsOwnerRow(uint index, KilnTaskStatsOwnerRow* row)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var owners = f._leases.Values.Select(l => l.Owner).Union(f.OwnerTotals.Keys).Distinct().Order().ToList();
            if (index >= owners.Count) return KilnAbi.ErrNotFound;
            var owner = owners[(int)index];
            var mine = f._leases.Values.Where(l => l.Owner == owner).ToList();
            var totals = f.OwnerTotals.GetValueOrDefault(owner);
            *row = default;
            row->Size = (uint)sizeof(KilnTaskStatsOwnerRow);
            row->Owner = owner;
            row->Instance = 1;
            row->QueuedCurrent = (ulong)mine.Count(l => l.State == Queued);
            row->RunningCurrent = (ulong)mine.Count(l => l.State == Running);
            row->Completed = totals.Completed;
            row->Faulted = totals.Faulted;
            row->Cancelled = totals.Cancelled;
            WriteName(row->OwnerName, owner.ToString());
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HStatsRunningRow(uint index, KilnTaskStatsRunningRow* row)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var live = f._leases.Values.Where(l => l.State == Running).OrderBy(l => l.Id).ToList();
            if (index >= live.Count) return KilnAbi.ErrNotFound;
            var l = live[(int)index];
            *row = default;
            row->Size = (uint)sizeof(KilnTaskStatsRunningRow);
            row->Owner = l.Owner;
            row->Lease = l.Id;
            row->State = l.SegmentRunning ? 1u : 2u;
            row->ElapsedNs = l.SegmentRunning ? 1_000_000ul : 0ul;
            WriteName(row->OwnerName, l.Owner.ToString());
            WriteName(row->TaskName, l.Name);
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HStatsFaultRow(uint index, KilnTaskStatsFaultRow* row)
    {
        var f = s_current!;
        lock (f._lock)
        {
            if (index >= f.Faults.Count) return KilnAbi.ErrNotFound;
            var (sequence, l, state) = f.Faults[(int)index];
            *row = default;
            row->Size = (uint)sizeof(KilnTaskStatsFaultRow);
            row->Sequence = sequence;
            row->Owner = l.Owner;
            row->Handle = l.Id;
            row->State = state;
            WriteName(row->OwnerName, l.Owner.ToString());
            WriteName(row->TaskName, l.Name);
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HStatsTotals(KilnTaskStatsTotals* totals)
    {
        var f = s_current!;
        lock (f._lock)
        {
            *totals = default;
            totals->Size = (uint)sizeof(KilnTaskStatsTotals);
            totals->Owners = (uint)f._leases.Values.Select(l => l.Owner).Union(f.OwnerTotals.Keys).Distinct().Count();
            totals->Workers = (uint)f._workers.Count;
            totals->Queued = (ulong)f._leases.Values.Count(l => l.State == Queued);
            totals->Running = (ulong)f._leases.Values.Count(l => l.State == Running);
            totals->RunningTasks = totals->Running;
            totals->Completed = (ulong)f.OwnerTotals.Values.Sum(t => (long)t.Completed);
            totals->Faulted = (ulong)f.OwnerTotals.Values.Sum(t => (long)t.Faulted);
            totals->Cancelled = (ulong)f.OwnerTotals.Values.Sum(t => (long)t.Cancelled);
            totals->Faults = (ulong)f.Faults.Count;
            return KilnAbi.Ok;
        }
    }

    private static void WriteName(byte* dst, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        for (var i = 0; i < bytes.Length && i < 128; i++) dst[i] = bytes[i];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint HOnPoolThread() => t_worker is not null && t_worker == s_current ? 1u : 0u;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint HServerSession()
    {
        var f = s_current!;
        lock (f._lock) return f._session;
    }

    // ---- kiln.tasks (the members the host uses on a lease's handle) ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TCancel(ulong owner, ulong task, uint* state)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var l = f.Find(task);
            if (l is null || l.Owner != owner)
            {
                if (state != null) *state = None;
                return KilnAbi.ErrNotFound;
            }
            f.CancelLocked(l);
            if (state != null) *state = l.State;
            return KilnAbi.Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TStatus(ulong owner, ulong task, uint* state)
    {
        var f = s_current!;
        lock (f._lock)
        {
            var l = f.Find(task);
            *state = l is null || l.Owner != owner ? None : l.State;
            return *state == None ? KilnAbi.ErrNotFound : KilnAbi.Ok;
        }
    }

    // ---- the test's side: owners and the server world ----

    /// <summary>The registry revokes <paramref name="owner"/>: no new leases, queued ones cancelled, running ones
    /// signalled. Never waits.</summary>
    public void RetireOwner(ulong owner)
    {
        lock (_lock)
        {
            _retired.Add(owner);
            SignalOwner(owner);
        }
    }

    public uint Session
    {
        get
        {
            lock (_lock) return _session;
        }
    }

    public void StartSession()
    {
        List<Lease> old;
        lock (_lock)
        {
            old = TakeHops();  // a world that stopped without a stop event: never carried into the new one
            _session = ++_sessions;
            _edge = 0;
        }
        foreach (var h in old) DispatchHop(h, cancelled: true);
    }

    /// <summary>The live session's STOPPING, on the calling thread (the one that ends the world): every pending hop is
    /// dispatched cancelled, every after-step resumption of the session is released with its cancellation set.</summary>
    public void StopSession()
    {
        List<Lease> hops;
        lock (_lock)
        {
            var ended = _session;
            _session = 0;
            hops = TakeHops();
            foreach (var l in _leases.Values.Where(l => l.Waiting && l.WaitAt.Session == ended).ToList())
            {
                l.Waiting = false;
                l.Cancel = true;
                Ready(l);
            }
        }
        foreach (var h in hops) DispatchHop(h, cancelled: true);
    }

    /// <summary>One server step on the tick thread: the queued hops within the budgets (owners in turn, FIFO per
    /// owner), the step's own tick work (<paramref name="pre"/>), then the Post edge. Returns the hops dispatched.</summary>
    public int Step(Action? pre = null)
    {
        var ran = 0;
        OnTick(() =>
        {
            ran = RunHops();
            pre?.Invoke();
            lock (_lock)
            {
                if (_session == 0) return;
                _edge++;
                foreach (var l in _leases.Values.Where(l => l.Waiting && l.WaitAt.Session == _session && l.WaitAt.Edge < _edge).ToList())
                {
                    l.Waiting = false;
                    Ready(l);
                }
            }
        });
        return ran;
    }

    private int RunHops()
    {
        long clock = 0;
        int ran = 0, owners;
        lock (_lock) owners = _hops.Count;
        for (var turn = 0; turn < owners; turn++)
        {
            if (clock >= HopStepNs) break;
            ulong owner;
            lock (_lock)
            {
                if (_hops.Count == 0) break;
                _hopTurn %= _hops.Count;
                owner = _hops[_hopTurn].Owner;
                _hopTurn = (_hopTurn + 1) % _hops.Count;
            }
            var ownerStart = clock;
            for (var n = 0; n < HopsPerOwner; n++)
            {
                if (clock - ownerStart >= HopOwnerNs || clock >= HopStepNs) break;
                Lease hop;
                bool cancel;
                lock (_lock)
                {
                    var queue = _hops.FirstOrDefault(q => q.Owner == owner).Hops;
                    if (queue is null || queue.Count == 0) break;
                    hop = queue.Dequeue();
                    cancel = hop.Cancel;
                }
                DispatchHop(hop, cancel);
                clock += HopCostNs;
                ran++;
            }
        }
        lock (_lock)
        {
            var next = _hops.Count == 0 ? 0 : _hops[_hopTurn % _hops.Count].Owner;
            _hops.RemoveAll(q => q.Hops.Count == 0);
            _hopTurn = Math.Max(0, _hops.FindIndex(q => q.Owner == next));
        }
        return ran;
    }

    /// <summary>Runs <paramref name="job"/> on the tick thread and waits for it.</summary>
    public void OnTick(Action job)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tickJobs.Add(() =>
        {
            try
            {
                job();
                done.SetResult();
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        if (!done.Task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("the fake tick did not finish its job");
    }

    public int PendingHops
    {
        get
        {
            lock (_lock) return _hops.Sum(q => q.Hops.Count);
        }
    }

    public int Records
    {
        get
        {
            lock (_lock) return _leases.Count;
        }
    }

    public Lease? LeaseNamed(string name)
    {
        lock (_lock) return _leases.Values.LastOrDefault(l => l.Name == name);
    }

    /// <summary>Waits until no segment runs, none is ready and no end notification is due: everything left is suspended,
    /// waiting for a hop, an edge or the test.</summary>
    public void WaitIdle(int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_lock)
        {
            while (_ready.Count > 0 || _ends.Count > 0 || _running > 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0 || !Monitor.Wait(_lock, TimeSpan.FromMilliseconds(left)))
                    throw new TimeoutException($"the fake pool did not go idle ({_ready.Count} ready, {_ends.Count} ends, {_running} running)");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            Monitor.PulseAll(_lock);
        }
        _tickJobs.CompleteAdding();
        foreach (var w in _workers) w.Join(TimeSpan.FromSeconds(5));
        _tickThread.Join(TimeSpan.FromSeconds(5));
        // the tables are left allocated: a late call from a stray thread must not touch freed memory
    }

    // ---- internals (under _lock) ----

    private Lease? Find(ulong lease) => _leases.GetValueOrDefault(lease);

    private int Misuse(string member, Lease? l)
    {
        Errors.Add($"{member} misused on lease {(l is null ? "?" : l.Name)} (the native table's INTERNAL)");
        return KilnAbi.ErrInternal;
    }

    private void Ready(Lease l)
    {
        _ready.AddLast(l);
        Monitor.PulseAll(_lock);
    }

    private void CancelLocked(Lease l)
    {
        if (l.State == Queued)
        {
            _ready.Remove(l);
            l.State = Cancelled;  // never dispatched
            QueueEnd(l);
        }
        else if (l.State == Running)
        {
            l.Cancel = true;
            if (l.AfterPending)
            {
                l.AfterPending = false;
                l.ResumePending = true;
            }
            else if (l.Waiting)
            {
                l.Waiting = false;
                Ready(l);
            }
        }
    }

    private uint SignalOwner(ulong owner)
    {
        var mine = _leases.Values.Where(l => l.Owner == owner).ToList();
        foreach (var l in mine) CancelLocked(l);
        return (uint)mine.Count;  // not quiescent yet: its end notification has not returned
    }

    private List<Lease> TakeHops()
    {
        var all = _hops.SelectMany(q => q.Hops).ToList();
        _hops.Clear();
        _hopTurn = 0;
        return all;
    }

    private void PlaceAfter(Lease l, (uint Session, ulong Edge) at)
    {
        if (at.Session != _session)
        {
            l.Cancel = true;  // its session is over: released cancelled, never carried into another
            Ready(l);
        }
        else if (l.Cancel)
        {
            Ready(l);
        }
        else
        {
            l.Waiting = true;
            l.WaitAt = at;
        }
    }

    // The done of a terminal lease: held back while a hop of it is pending, then delivered by a worker.
    private void QueueEnd(Lease l)
    {
        if (!l.Terminal || l.HopsPending > 0 || l.EndQueued) return;
        l.EndQueued = true;
        _ends.Enqueue(l);
        Monitor.PulseAll(_lock);
    }

    private void SegmentEnded(Lease l)
    {
        l.SegmentRunning = false;
        if (l.Finished)
        {
            l.State = l.Outcome switch
            {
                0 => Completed,
                2 when l.Cancel => Cancelled,
                _ => Faulted,
            };
            QueueEnd(l);
            return;
        }
        if (l.ResumePending)
        {
            l.ResumePending = false;
            Ready(l);
            return;
        }
        if (l.AfterPending)
        {
            l.AfterPending = false;
            PlaceAfter(l, l.AfterAt);
            return;
        }
        if (l.Armed) return;
        Errors.Add($"lease {l.Name}: a segment returned neither armed nor finished");
        l.State = Faulted;
        QueueEnd(l);
    }

    // What a dispatch must leave as it found it on its thread.
    private static (TaskRun?, ModContext?, SynchronizationContext?) Observed() =>
        (TaskExecutionScope.Current, ModContext.CurrentMod, SynchronizationContext.Current);

    private void CheckLeak(string where, (TaskRun?, ModContext?, SynchronizationContext?) before)
    {
        var after = Observed();
        if (after != before) lock (_lock) Leaks.Add($"{where} left {after} (was {before})");
    }

    private void DispatchHop(Lease h, bool cancelled)
    {
        lock (_lock) HopsRun.Add((h.Owner, h.Id, cancelled));
        var before = Observed();
        _dispatch(h.Id, 2, cancelled ? 1u : 0u, (void*)h.HostState);
        CheckLeak("a tick hop", before);
        lock (_lock)
        {
            h.HopsPending--;
            QueueEnd(h);
        }
    }

    private void WorkerLoop()
    {
        t_worker = this;
        while (true)
        {
            Lease? l = null, end = null;
            var cancel = false;
            lock (_lock)
            {
                while (!_disposed && _ready.Count == 0 && _ends.Count == 0) Monitor.Wait(_lock);
                if (_disposed) return;
                if (_ends.Count > 0)
                {
                    end = _ends.Dequeue();
                }
                else
                {
                    l = _ready.First!.Value;
                    _ready.RemoveFirst();
                    l.SegmentRunning = true;
                    l.State = Running;
                    l.Dispatched = true;
                    cancel = l.Cancel;
                }
                _running++;
            }
            if (end is not null)
            {
                var found = Observed();
                if (_ended != null) _ended(end.Id, end.State, (void*)end.HostState);
                CheckLeak("an end notification", found);
                lock (_lock)
                {
                    Ends.Add((end.Id, end.State, true));
                    var totals = OwnerTotals.GetValueOrDefault(end.Owner);
                    OwnerTotals[end.Owner] = end.State switch
                    {
                        Completed => (totals.Completed + 1, totals.Faulted, totals.Cancelled),
                        Faulted => (totals.Completed, totals.Faulted + 1, totals.Cancelled),
                        Cancelled => (totals.Completed, totals.Faulted, totals.Cancelled + 1),
                        _ => totals,
                    };
                    if (end.State == Faulted) Faults.Add(((ulong)Faults.Count + 1, end, end.State));
                    if (_ended != null) _leases.Remove(end.Id);  // the core releases the record after the notification
                    _running--;
                    Monitor.PulseAll(_lock);
                }
                continue;
            }
            var before = Observed();
            _dispatch(l!.Id, 1, cancel ? 1u : 0u, (void*)l.HostState);
            CheckLeak("a segment", before);
            lock (_lock)
            {
                SegmentEnded(l);
                _running--;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void TickLoop()
    {
        foreach (var job in _tickJobs.GetConsumingEnumerable()) job();
    }
}
