// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Kiln.TestKit;

/// <summary>The harness's tick thread: the one thread where <see cref="Game.IsTickThread"/> is true. Work from other
/// threads is queued and the caller waits; work from the thread itself runs inline. A background thread, so a test that
/// blocks it never keeps the test process alive. <see cref="Abandon"/> (the watchdog) gives up on it: every waiting and
/// later call throws <see cref="HarnessStoppedException"/>, and the thread is left to whatever blocks it.</summary>
internal sealed class TickThread
{
    private readonly object _lock = new();
    private readonly Queue<WorkItem> _queue = new();
    private readonly ManualResetEventSlim _abandoned = new(false);
    private readonly Thread _thread;
    private readonly int _threadId;
    private bool _closing;
    private volatile string? _abandonReason;

    public TickThread(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
        _threadId = _thread.ManagedThreadId;
    }

    /// <summary>The thread itself (for the watchdog: its name, its stack).</summary>
    public Thread Thread => _thread;

    public bool IsCurrent => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Why the thread was given up on; null while it serves.</summary>
    public string? AbandonReason => _abandonReason;

    /// <summary>Runs <paramref name="work"/> on the tick thread and returns its result; its exception comes back with its
    /// original stack.</summary>
    public T Invoke<T>(Func<T> work)
    {
        if (IsCurrent) return work();
        if (_abandonReason is { } why) throw new HarnessStoppedException(why);
        var item = new WorkItem<T>(work);
        lock (_lock)
        {
            if (_closing) throw new ObjectDisposedException(nameof(KilnHarness), "the harness was disposed");
            _queue.Enqueue(item);
            Monitor.Pulse(_lock);
        }
        WaitHandle.WaitAny([item.Done.WaitHandle, _abandoned.WaitHandle]);
        if (!item.Done.IsSet) throw new HarnessStoppedException(_abandonReason ?? "the tick thread was given up on");
        return item.Result();
    }

    /// <summary>The watchdog's way out: the thread stays blocked, the harness stops serving.</summary>
    public void Abandon(string reason)
    {
        _abandonReason ??= reason;
        _abandoned.Set();
        lock (_lock)
        {
            _closing = true;
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Lets the thread finish the queued work and end; waits for it up to <paramref name="timeout"/>.</summary>
    public bool Close(TimeSpan timeout)
    {
        lock (_lock)
        {
            _closing = true;
            Monitor.Pulse(_lock);
        }
        return IsCurrent || _thread.Join(timeout);
    }

    private void Loop()
    {
        while (true)
        {
            WorkItem? item;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_closing) Monitor.Wait(_lock);
                if (_queue.Count == 0 || _abandonReason is not null) return;
                item = _queue.Dequeue();
            }
            RunOne(item);
            item = null;  // the last item must not outlive its call: it may reach into a mod's load context
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOne(WorkItem item)
    {
        try
        {
            item.Execute();
        }
        finally
        {
            // what a call left behind on this thread must not pin a mod or leak into the next call
            HostAccess.Current = null;
            SynchronizationContext.SetSynchronizationContext(null);
            item.Done.Set();
        }
    }

    private abstract class WorkItem
    {
        public readonly ManualResetEventSlim Done = new(false);
        public abstract void Execute();
    }

    private sealed class WorkItem<T>(Func<T> work) : WorkItem
    {
        private Func<T>? _work = work;
        private T? _result;
        private ExceptionDispatchInfo? _error;

        public override void Execute()
        {
            try
            {
                _result = _work!();
            }
            catch (Exception e)
            {
                _error = ExceptionDispatchInfo.Capture(e);
            }
            _work = null;
        }

        public T Result()
        {
            _error?.Throw();
            var r = _result!;
            _result = default;
            return r;
        }
    }
}
