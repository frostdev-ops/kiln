// SPDX-License-Identifier: MIT
// C# tasks on Kiln's one task pool: the real TaskBackend over the core's lease table (kiln.internal.tasks.host,
// native/core/src/tasks_host.h) and the two kiln.tasks members that work on a lease's handle. The table is
// Kiln-internal and belongs to one build: nothing of it is called before its size, version and build stamp match.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;

namespace Kiln.Host;

/// <summary>KilnTaskInfo (abi/services/kiln_tasks.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KilnTaskInfo
{
    public uint Size;
    public uint Flags;
    public KilnStr Name;
    public uint Priority;
    public uint Reserved;

    public const uint LongRunning = 1;  // KILN_TASK_LONG_RUNNING
}

/// <summary>kiln.tasks v1 (abi/services/kiln_tasks.h). The host uses cancel and status on a lease's handle; leases are
/// admitted through <see cref="KilnTasksHostApi"/>, never through submit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnTasksApi
{
    public uint Size;
    public uint Version;
    public void* Submit;
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint*, int> Cancel;
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint*, int> Status;
    public delegate* unmanaged[Cdecl]<ulong, ulong, int> Release;
    public delegate* unmanaged[Cdecl]<ulong, uint, uint*, int> DrainOwner;

    public const string Name = "kiln.tasks";
}

/// <summary>KilnLeaseSnapshot (tasks_host.h), 168 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnLeaseSnapshot
{
    public uint Size;
    public uint State;
    public ulong Owner;
    public ulong Lease;
    public ulong RunningNs;
    public fixed byte Name[129];
    public fixed byte Reserved[7];
}

/// <summary>kiln.internal.tasks.host v1 (native/core/src/tasks_host.h), 128 bytes: field order is the table's.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnTasksHostApi
{
    public uint Size;
    public uint Version;
    public delegate* unmanaged[Cdecl]<byte*> BuildStamp;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, uint, uint, void*, void>, int> Bind;
    public delegate* unmanaged[Cdecl]<ulong, KilnTaskInfo*, void*, ulong*, uint*, int> Admit;
    public delegate* unmanaged[Cdecl]<ulong, ulong*, int> Arm;
    public delegate* unmanaged[Cdecl]<ulong, ulong, int> Resume;
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint*, int> ResumeAfterStep;
    public delegate* unmanaged[Cdecl]<ulong, int> TickHop;
    public delegate* unmanaged[Cdecl]<ulong, uint, int> Finish;
    public delegate* unmanaged[Cdecl]<ulong, uint> IsCancelled;
    public delegate* unmanaged[Cdecl]<ulong, uint*, int> SealOwner;
    public delegate* unmanaged[Cdecl]<KilnLeaseSnapshot*, uint, uint*, int> Snapshot;
    public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<ulong, uint, void*, void>, int> BindEnded;
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint, uint*, int> ResumeAfterStepIn;
    public delegate* unmanaged[Cdecl]<uint> OnPoolThread;
    public delegate* unmanaged[Cdecl]<uint> ServerSession;

    public const string Name = "kiln.internal.tasks.host";
    public const uint Version1 = 1;
}

/// <summary>What the bridge needs from its host besides the tables: the server tick's identity, the log, and the frame
/// a tick hop runs in.</summary>
internal sealed class HostTasksEnvironment
{
    /// <summary><c>KilnApi.service</c> (the managed host's private view also resolves the lease table).</summary>
    public required Func<string, uint, nint> Service { get; init; }

    /// <summary>True on the server world's tick thread (<c>KilnApi.on_world_thread(SERVER)</c>).</summary>
    public required Func<bool> OnServerTick { get; init; }

    public required Action<LogLevel, string> Log { get; init; }

    /// <summary>The build stamp Kiln.Api was compiled with (<see cref="KilnBuild"/>); null without one.</summary>
    public required string? BuildStamp { get; init; }

    /// <summary>Runs one tick hop of <c>owner</c> on the server tick (<see cref="TaskTickDispatcher"/>); null runs it
    /// bare.</summary>
    public Action<ulong, Action>? TickHop { get; init; }
}

/// <summary>
/// The pool C# tasks run on: <see cref="TaskBackend"/> over the core's lease table.
/// </summary>
/// <remarks>
/// <para>Pins: every admitted lease gets one normal GCHandle, passed to the core as its host state. Its target holds the
/// task's state and the load context its code lives in, so neither can be collected while a segment, a tick hop or a late
/// continuation of the task can still run. The handle is freed exactly once: at a refused admit, or in the core's end
/// notification, which comes once per accepted lease after everything of it has run (also for a lease cancelled before
/// its first segment, which never reaches the dispatcher). The core releases the lease's record right after that
/// notification; the host never releases one.</para>
/// <para>Entry points: the dispatcher and the end notification are <c>UnmanagedCallersOnly</c> statics of Kiln.Host
/// (not collectible), bound once per process. They run on pool workers, on the server tick and, at a world's end, on the
/// thread that ends it, possibly at once; they catch everything, so no managed exception crosses back into the
/// core.</para>
/// </remarks>
internal sealed unsafe class HostTasks : TaskBackend
{
    /// <summary>One lease's pin: the GCHandle's target.</summary>
    private sealed class Pin(HostTasks backend, ulong owner, object state, AssemblyLoadContext? context)
    {
        public readonly HostTasks Backend = backend;
        public readonly ulong Owner = owner;
        public readonly object State = state;      // the task's state: what the dispatcher hands back
        public readonly AssemblyLoadContext? Context = context;  // the work's collectible load context, held while the lease lives
        public GCHandle Handle;
    }

    private static HostTasks? s_bound;  // the backend the entry points serve: one per process (tests rebind)

    private readonly KilnTasksHostApi* _host;
    private readonly KilnTasksApi* _tasks;
    private readonly HostTasksEnvironment _env;
    private readonly ConcurrentDictionary<nint, Pin> _pins = new();
    private LeaseDispatch? _dispatch;
    private string? _unavailable;

    private HostTasks(HostTasksEnvironment env, KilnTasksHostApi* host, KilnTasksApi* tasks, string? unavailable)
    {
        _env = env;
        _host = host;
        _tasks = tasks;
        _unavailable = unavailable;
    }

    /// <summary>The bound backend; null before <see cref="Start"/>.</summary>
    internal static HostTasks? Bound => Volatile.Read(ref s_bound);

    /// <summary>Resolves and checks the core's tables and installs the backend. A table that is missing, short, of another
    /// version or of another build leaves a backend that only says why tasks.pool is off: nothing of the table is called
    /// (beyond its build stamp).</summary>
    internal static HostTasks Start(HostTasksEnvironment env)
    {
        var backend = Create(env);
        Volatile.Write(ref s_bound, backend);
        TaskBackend.Install(backend);  // binds the dispatcher (Bind below)
        if (backend._unavailable is { } why) env.Log(LogLevel.Warn, $"tasks: C# tasks are off: {why}");
        else env.Log(LogLevel.Info, "tasks: C# tasks run on Kiln's task pool");
        return backend;
    }

    private static HostTasks Create(HostTasksEnvironment env)
    {
        HostTasks Off(string why) => new(env, null, null, why);
        var host = (KilnTasksHostApi*)env.Service(KilnTasksHostApi.Name, KilnTasksHostApi.Version1);
        if (host == null) return Off("this Kiln core offers no task pool to C# mods (no kiln.internal.tasks.host table)");
        if (host->Size < (uint)sizeof(KilnTasksHostApi))
            return Off($"the core's task table is shorter than this Kiln.Host expects ({host->Size} of {sizeof(KilnTasksHostApi)} bytes): the core and Kiln.Host are different builds");
        if (host->Version != KilnTasksHostApi.Version1)
            return Off($"the core's task table is version {host->Version}, this Kiln.Host needs {KilnTasksHostApi.Version1}: the core and Kiln.Host are different builds");
        if (host->BuildStamp == null) return Off("the core's task table has no build stamp");
        var stamp = Marshal.PtrToStringUTF8((nint)host->BuildStamp()) ?? "";
        if (stamp.Length == 0) return Off("the core was built without a build stamp, so Kiln.Host cannot tell that it is the same build");
        if (env.BuildStamp is not { Length: > 0 } own)
            return Off("Kiln.Api was built without a build stamp, so Kiln.Host cannot tell that the core is the same build");
        if (!string.Equals(stamp, own, StringComparison.Ordinal))
            return Off($"the core is build {stamp}, Kiln.Host is build {own}: the task table is only used within one build");
        for (var i = 1; i < sizeof(KilnTasksHostApi) / sizeof(nint); i++)
            if (((nint*)host)[i] == 0) return Off("the core's task table has an empty member");
        var tasks = (KilnTasksApi*)env.Service(KilnTasksApi.Name, 1);
        if (tasks == null || tasks->Size < (uint)sizeof(KilnTasksApi) || tasks->Cancel == null || tasks->Status == null || tasks->DrainOwner == null)
            return Off("the core's kiln.tasks table is missing or short");
        return new HostTasks(env, host, tasks, null);
    }

    /// <summary>Leases whose pins are held: admitted and not yet reported ended by the core.</summary>
    internal int LivePins => _pins.Count;

    /// <summary>The load contexts pinned for <paramref name="owner"/>'s live leases (tests, diagnostics).</summary>
    internal IReadOnlyList<AssemblyLoadContext?> PinnedContexts(ulong owner) =>
        [.. _pins.Values.Where(p => p.Owner == owner).Select(p => p.Context)];

    /// <summary>Leases of <paramref name="owner"/> whose end the core has not reported yet: while any is left, a
    /// segment, a tick hop or a continuation of it can still run, so its load context is not quiescent. The pin
    /// itself holds that context until then; a mod's unload joins this off the tick, never on it.</summary>
    internal int OwnerPins(ulong owner) => _pins.Values.Count(p => p.Owner == owner);

    /// <summary>The live tasks of <paramref name="owner"/> (name and state), for the log line that names what an
    /// unload still waits for. Tasks that have ended are not listed, even while their pin is still draining.</summary>
    internal IReadOnlyList<(string Name, KilnTaskState State)> LiveTasks(ulong owner) =>
        [.. _pins.Values.Where(p => p.Owner == owner)
            .Select(p => TaskRun.InfoOf(p.State))
            .Where(i => i is not null)
            .Select(i => i!.Value)];

    // The load context the task's code lives in, when it is a collectible one (a mod's): held by the pin so it cannot
    // finish unloading while the task can still run. Kiln's own code (the default context) needs no pin.
    private static AssemblyLoadContext? LoadContextOf(object hostState) =>
        TaskRun.WorkAssemblyOf(hostState) is { } assembly && AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: true } context
            ? context
            : null;

    /// <summary>The core's <c>drain_owner</c>: seals <paramref name="owner"/> (again; it is already sealed by the
    /// unload) and then waits up to <paramref name="budget"/> for its leases to be quiescent, returning how many
    /// are not. This is the off-tick join behind a mod's load-context teardown, so it may only run on a thread
    /// that is neither the tick, a world thread nor a pool worker (the core refuses those with WRONG_THREAD);
    /// -1 when the core refused or no pool is bound, in which case the caller falls back to looking at the pins.
    /// A revoked owner whose tasks are still running also reports them here (ERR_REVOKED with the count set).</summary>
    internal int DrainOwner(ulong owner, TimeSpan budget)
    {
        if (_unavailable is not null || _tasks is null) return -1;
        uint running = 0;
        var ms = budget <= TimeSpan.Zero ? 0u : (uint)Math.Min(uint.MaxValue, Math.Max(1, Math.Ceiling(budget.TotalMilliseconds)));
        var rc = _tasks->DrainOwner(owner, ms, &running);
        return rc is KilnAbi.Ok or KilnAbi.ErrRevoked ? (int)running : -1;
    }

    /// <summary>The owner is going away (unload, disable): its tasks' cancellation is signalled, so work that waits on its
    /// token ends, and the core takes no new task of it. Never waits. Cancellation and finalization continuations of
    /// its tasks still run afterwards.</summary>
    internal int RetireOwner(ulong owner)
    {
        if (_unavailable is not null) return 0;
        foreach (var pin in _pins.Values)
            if (pin.Owner == owner) TaskRun.CancelFromHost(pin.State);
        return SealOwner(owner);
    }

    // ---- the entry points the core calls ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDispatch(ulong lease, uint what, uint cancelled, void* hostState)
    {
        var backend = Volatile.Read(ref s_bound);
        try
        {
            if (GCHandle.FromIntPtr((nint)hostState).Target is not Pin pin) return;
            backend = pin.Backend;
            backend.Dispatch(lease, what, cancelled != 0, pin);
        }
        catch (Exception e)
        {
            Report(backend, $"tasks: Kiln's task dispatcher failed on lease 0x{lease:x} (a bug in Kiln, not in a mod): {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEnded(ulong lease, uint state, void* hostState)
    {
        var backend = Volatile.Read(ref s_bound);
        try
        {
            var handle = GCHandle.FromIntPtr((nint)hostState);
            if (handle.Target is not Pin pin) return;
            backend = pin.Backend;
            try
            {
                TaskRun.LeaseEnded(pin.State, (KilnTaskState)state);
            }
            finally
            {
                // nothing of the lease runs any more: its pins go, exactly once
                if (backend._pins.TryRemove((nint)hostState, out _)) handle.Free();
            }
        }
        catch (Exception e)
        {
            Report(backend, $"tasks: Kiln's task end notification failed on lease 0x{lease:x} (a bug in Kiln, not in a mod): {e}");
        }
    }

    private static void Report(HostTasks? backend, string text)
    {
        try
        {
            backend?._env.Log(LogLevel.Error, text);
        }
        catch
        {
            // nothing left to report to; never back into the core
        }
    }

    private void Dispatch(ulong lease, uint what, bool cancelled, Pin pin)
    {
        var dispatch = _dispatch;
        if (dispatch is null) return;
        if (what == (uint)LeaseWork.TickHop && _env.TickHop is { } frame && _env.OnServerTick())
            frame(pin.Owner, () => dispatch(lease, LeaseWork.TickHop, cancelled, pin.State));  // on the server tick, before Pre
        else
            dispatch(lease, (LeaseWork)what, cancelled, pin.State);  // a segment; or a world's end flushing its hops
    }

    // ---- TaskBackend ----

    public override string? UnavailableReason => _unavailable;

    public override bool OnPoolThread => _unavailable is null && _host->OnPoolThread() != 0;

    public override bool OnServerTick => _unavailable is null && _env.OnServerTick();

    public override ulong ServerSession => _unavailable is null ? _host->ServerSession() : 0;

    public override void Bind(LeaseDispatch dispatch)
    {
        _dispatch = dispatch;
        if (_unavailable is not null) return;
        // the end notification first: no lease is admitted before the core can report its end
        var rc = _host->BindEnded(&OnEnded);
        if (rc == KilnAbi.Ok) rc = _host->Bind(&OnDispatch);
        if (rc != KilnAbi.Ok) _unavailable = $"the core refused Kiln.Host's task dispatcher (rc {rc})";
    }

    public override TaskRefusal Admit(ulong owner, string name, KilnTaskOptions options, object hostState, out ulong lease)
    {
        lease = 0;
        if (_unavailable is not null) return TaskRefusal.Capability;
        var pin = new Pin(this, owner, hostState, LoadContextOf(hostState));
        pin.Handle = GCHandle.Alloc(pin);
        var key = GCHandle.ToIntPtr(pin.Handle);
        _pins[key] = pin;  // before the admit: its end can come before admit returns
        var bytes = Encoding.UTF8.GetBytes(name);
        ulong admitted = 0;
        uint refusal = 0;
        int rc;
        fixed (byte* p = bytes)
        {
            var info = new KilnTaskInfo
            {
                Size = (uint)sizeof(KilnTaskInfo),
                Flags = options.LongRunning ? KilnTaskInfo.LongRunning : 0,
                Name = new KilnStr { Ptr = p, Len = (nuint)bytes.Length },
                Priority = (uint)options.Priority,
            };
            rc = _host->Admit(owner, &info, (void*)key, &admitted, &refusal);
        }
        if (rc == KilnAbi.Ok)
        {
            lease = admitted;
            return TaskRefusal.None;
        }
        // refused: nothing was transferred, the core never calls back for it
        if (_pins.TryRemove(key, out _)) pin.Handle.Free();
        return refusal is > 0 and <= (uint)TaskRefusal.NoServerWorld ? (TaskRefusal)refusal : TaskRefusal.Capability;
    }

    public override ulong Arm(ulong lease)
    {
        ulong registration = 0;
        Check(_host->Arm(lease, &registration), "arm", lease);
        return registration;
    }

    public override void Resume(ulong lease, ulong registration) => Check(_host->Resume(lease, registration), "resume", lease);

    public override TaskRefusal ResumeAfterStep(ulong lease, ulong registration, ulong session)
    {
        if (session is 0 or > uint.MaxValue) return TaskRefusal.NoServerWorld;
        uint refusal = 0;
        // the core compares the session with the live one and arms in one step: never moved into another world
        var rc = _host->ResumeAfterStepIn(lease, registration, (uint)session, &refusal);
        if (rc == KilnAbi.ErrUnavailable) return TaskRefusal.NoServerWorld;  // still armed
        Check(rc, "resume_after_step", lease);
        return TaskRefusal.None;
    }

    public override TaskRefusal TickHop(ulong lease)
    {
        var rc = _host->TickHop(lease);
        if (rc == KilnAbi.ErrUnavailable) return TaskRefusal.NoServerWorld;
        Check(rc, "tick_hop", lease);
        return TaskRefusal.None;
    }

    public override void Finish(ulong lease, LeaseOutcome outcome) => Check(_host->Finish(lease, (uint)outcome), "finish", lease);

    public override bool IsCancelled(ulong lease) => _host->IsCancelled(lease) != 0;

    public override KilnTaskState Cancel(ulong owner, ulong lease)
    {
        uint state = 0;
        _tasks->Cancel(owner, lease, &state);  // NOT_FOUND leaves NONE: its record is gone
        return (KilnTaskState)state;
    }

    public override KilnTaskState Status(ulong owner, ulong lease)
    {
        uint state = 0;
        _tasks->Status(owner, lease, &state);
        return (KilnTaskState)state;
    }

    public override int SealOwner(ulong owner)
    {
        if (_unavailable is not null) return 0;
        uint outstanding = 0;
        _host->SealOwner(owner, &outstanding);
        return (int)outstanding;
    }

    public override IReadOnlyList<LeaseRow> Snapshot()
    {
        if (_unavailable is not null) return [];
        uint count = 0;
        var capacity = 64u;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var rows = new KilnLeaseSnapshot[capacity];
            int rc;
            fixed (KilnLeaseSnapshot* p = rows) rc = _host->Snapshot(p, capacity, &count);
            if (rc == KilnAbi.ErrBufferTooSmall)
            {
                capacity = Math.Max(count, capacity * 2);
                continue;
            }
            if (rc != KilnAbi.Ok) return [];
            var result = new LeaseRow[Math.Min(count, capacity)];
            for (var i = 0; i < result.Length; i++)
            {
                string name;
                fixed (byte* n = rows[i].Name) name = Marshal.PtrToStringUTF8((nint)n) ?? "";
                result[i] = new LeaseRow(rows[i].Owner, rows[i].Lease, (KilnTaskState)rows[i].State, name,
                    TimeSpan.FromTicks((long)(rows[i].RunningNs / 100)));
            }
            return result;
        }
        return [];
    }

    // The table's INTERNAL (and anything else unexpected) is a bug in Kiln, as the contract says.
    private static void Check(int rc, string member, ulong lease)
    {
        if (rc != KilnAbi.Ok) throw new InvalidOperationException($"Kiln's task pool refused {member} on lease 0x{lease:x} (rc {rc})");
    }
}
