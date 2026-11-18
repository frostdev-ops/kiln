// SPDX-License-Identifier: MIT
// A fake of Kiln's task pool for the C# task API's tests: the test kit's pool (Kiln.TestKit/Shared/KitTaskPool.cs, the
// managed host's lease table over real worker threads, with the native rules) under a tick thread of its own. Misuse the
// native table answers with INTERNAL is recorded in Errors (and thrown); a dispatch that leaves its thread's scope, mod,
// context or flowed identity changed is recorded in Leaks. Both must stay empty.
using System.Collections.Concurrent;
using Xunit;

namespace Kiln.Api.Tests;

internal sealed class FakeTaskBackend : Kiln.TestKit.KitTaskPool
{
    private readonly Thread _tickThread, _mainThread;
    private readonly BlockingCollection<Action> _tickJobs = new(), _mainJobs = new();

    public FakeTaskBackend(int workers = 2) : this(workers, new Thread[2]) { }

    // the base asks which thread is the tick thread and which the game's main thread; both are made before the base
    // starts its workers. The main thread steps the client world (#82) when a test starts a client session.
    private FakeTaskBackend(int workers, Thread[] threads)
        : base(workers, () => Thread.CurrentThread == threads[0], "fake-task-", notifyEnded: false, () => Thread.CurrentThread == threads[1])
    {
        _tickThread = threads[0] = new Thread(() => Loop(_tickJobs)) { IsBackground = true, Name = "fake-tick" };
        _mainThread = threads[1] = new Thread(() => Loop(_mainJobs)) { IsBackground = true, Name = "fake-main" };
        _tickThread.Start();
        _mainThread.Start();
    }

    public int TickThreadId => _tickThread.ManagedThreadId;
    public bool IsTickThread => Thread.CurrentThread == _tickThread;
    public int MainThreadId => _mainThread.ManagedThreadId;
    public bool IsMainThread => Thread.CurrentThread == _mainThread;

    /// <summary>One client-world step on the game's main thread: the client hops queued before it, the step's own work
    /// (<paramref name="pre"/>), then the client world's Post edge.</summary>
    public void ClientStep(Action? pre = null) => OnMain(() =>
    {
        RunHops(WorldKind.Client);
        pre?.Invoke();
        PostEdge(WorldKind.Client);
    });

    /// <summary>Runs <paramref name="job"/> on the game's main thread and waits for it.</summary>
    public void OnMain(Action job) => Run(_mainJobs, job, "main thread");

    /// <summary>One server step on the tick thread: the hops queued before it (each with its lease's cancellation), the
    /// step's own tick work (<paramref name="pre"/>), then the Post edge.</summary>
    public void Step(Action? pre = null) => OnTick(() =>
    {
        RunHops();
        pre?.Invoke();
        PostEdge();
    });

    /// <summary>Runs <paramref name="job"/> on the tick thread and waits for it.</summary>
    public void OnTick(Action job) => Run(_tickJobs, job, "tick");

    private static void Run(BlockingCollection<Action> jobs, Action job, string where)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.Add(() =>
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
        if (!done.Task.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException($"the fake {where} did not finish its job");
    }

    public override void Dispose()
    {
        base.Dispose();
        _tickJobs.CompleteAdding();
        _mainJobs.CompleteAdding();
        _tickThread.Join(TimeSpan.FromSeconds(5));
        _mainThread.Join(TimeSpan.FromSeconds(5));
    }

    private static void Loop(BlockingCollection<Action> jobs)
    {
        foreach (var job in jobs.GetConsumingEnumerable()) job();
    }
}

/// <summary>Kiln.Api's backend for these tests: the kit's recording backend, except that only the fake's tick thread is
/// the tick thread.</summary>
internal sealed class TickAwareBackend(Kiln.TestKit.RecordingBackend inner, FakeTaskBackend pool) : IKilnBackend
{
    public string Version => inner.Version;
    public bool IsTickThread => pool.IsTickThread;
    public bool HasFeature(string name) => inner.HasFeature(name);
    public void Log(ulong owner, LogLevel level, string text) => inner.Log(owner, level, text);
    public nint Service(string name, uint minVersion) => inner.Service(name, minVersion);
    public ulong OwnerCreate(string id) => inner.OwnerCreate(id);
}

/// <summary>Fresh Kiln state plus a fake pool with a live server session, bound as Kiln's task backend.</summary>
public abstract class TaskTest : KilnTest
{
    internal FakeTaskBackend Pool { get; private set; }

    protected TaskTest()
    {
        Pool = NewPool(2);
    }

    internal FakeTaskBackend NewPool(int workers)
    {
        var pool = new FakeTaskBackend(workers);
        pool.StartSession();
        TaskBackend.Install(pool);
        Kiln.Backend.Current = new TickAwareBackend(Backend, pool);
        return pool;
    }

    /// <summary>Replaces the pool (e.g. with one worker).</summary>
    internal void UsePool(int workers)
    {
        var old = Pool;
        Pool = NewPool(workers);
        old.Dispose();
    }

    public override void Dispose()
    {
        try
        {
            TaskRun.TestHook = null;
            TaskBackend.Install(null);
            Pool.Dispose();
            Kiln.Backend.Current = Backend;
            Assert.Empty(Pool.Errors);  // the workers and the tick thread have stopped
            Assert.Empty(Pool.Leaks);
        }
        finally
        {
            base.Dispose();
        }
    }

    /// <summary>Waits until <paramref name="task"/> has ended (never on a game thread: this is the test's thread).</summary>
    internal static void Ended(KilnTask task, int timeoutMs = 20000) =>
        Assert.True(task.WaitEnded(TimeSpan.FromMilliseconds(timeoutMs)), $"the task '{task.Name}' did not end ({task.State})");

    /// <summary>Tick code of <paramref name="mod"/>, as the host runs it in Pre: its scheduled work first, with the mod's
    /// tick context and CurrentMod installed.</summary>
    internal void TickAs(ModContext mod, Action? body = null) => Pool.Step(() =>
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
        HostAccess.Current = mod;
        try
        {
            while (true)
            {
                Action? next;
                lock (mod.PendingLock)
                    if (!mod.Pending.TryDequeue(out next)) break;
                next();
            }
            body?.Invoke();
        }
        finally
        {
            HostAccess.Current = null;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });
}
