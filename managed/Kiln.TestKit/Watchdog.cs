// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Kiln.TestKit;

/// <summary>How the watchdog ended a call that blocked the tick thread.</summary>
internal enum StopKind
{
    /// <summary><see cref="Thread.Interrupt"/>: the thread was blocked in a wait, a sleep, a join or a lock.</summary>
    Interrupted = 0,
    /// <summary><c>ControlledExecution</c>'s abort: the thread ran managed code that never returned (a loop).</summary>
    Aborted = 1,
    /// <summary>Neither reached it (native code, a <c>finally</c> that never ends, a runtime without
    /// <c>ControlledExecution</c>): the thread was given up on and may still run.</summary>
    NotStopped = 2,
}

/// <summary>What the watchdog did to one call. <see cref="Stack"/> is the tick thread's managed stack where the stop
/// reached it (taken on that thread in a first-chance handler), or null when nothing reached it.</summary>
internal sealed record StopReport(string Subject, TimeSpan Budget, StopKind Kind, string? Stack, bool ThreadEnded, string How);

/// <summary>
/// Stops a harness call that blocks the tick thread, for the test runner (<see cref="KilnHarness.RunTest"/>). While it
/// is attached to a <see cref="TickThread"/>, every call waits at most <see cref="Budget"/>; one that is overdue is
/// stopped:
/// <list type="number">
/// <item>while the thread is in a wait (<see cref="System.Threading.ThreadState.WaitSleepJoin"/>: <c>Task.Wait</c>,
/// <c>.Result</c>, a lock, <c>Thread.Sleep</c>, an event), <see cref="Thread.Interrupt"/>, up to three times;</item>
/// <item>then <c>ControlledExecution</c>'s abort, requested from a throwaway thread (the request does not return while the
/// target waits: measured on .NET 10, Linux), with more interrupts to wake a thread that swallows them, so the abort can
/// land;</item>
/// <item>then the thread is given up on.</item>
/// </list>
/// The stack is taken on the tick thread by a first-chance handler when the interrupt or the abort is thrown there, so it
/// shows the frames of the code that blocked. The harness is abandoned afterwards in every case.
/// </summary>
internal sealed class Watchdog
{
    private static int _supported = 1;  // ControlledExecution.Run works on this runtime (until it says otherwise)

    private readonly CancellationTokenSource _abort = new();
    private volatile Thread? _target;
    private volatile bool _stopping;
    private string? _stack;
    private int _executing;

    /// <summary>How long one call may take; <see cref="Timeout.InfiniteTimeSpan"/> when nothing is watched.</summary>
    public TimeSpan Budget { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>What the calls are made for (the test's name), for the report.</summary>
    public string Subject { get; set; } = "";

    /// <summary>Set once a call was stopped.</summary>
    public StopReport? Stopped { get; private set; }

    /// <summary>How long the stop tries, per stage. Tests shorten them.</summary>
    internal TimeSpan InterruptWait { get; init; } = TimeSpan.FromMilliseconds(400);

    internal TimeSpan AbortWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Whether this runtime can abort managed code (<c>System.Runtime.ControlledExecution</c>; CoreCLR has it,
    /// NativeAOT and Mono do not).</summary>
    public static bool CanAbort => Volatile.Read(ref _supported) != 0;

    /// <summary>On the tick thread: runs one work item where an abort can reach it.</summary>
    public void Execute(Action work)
    {
        _target = Thread.CurrentThread;
        if (!CanAbort || _abort.IsCancellationRequested)
        {
            work();
            return;
        }
        Interlocked.Exchange(ref _executing, 1);
        try
        {
#pragma warning disable SYSLIB0046  // ControlledExecution is unsafe in production; a test harness is the use it was kept for
            System.Runtime.ControlledExecution.Run(work, _abort.Token);
#pragma warning restore SYSLIB0046
        }
        catch (PlatformNotSupportedException)
        {
            // the work item never throws (it keeps its exception for the caller): this is the runtime saying no
            Volatile.Write(ref _supported, 0);
            work();
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            // aborted: the work item ended in the abort, which its caller sees as a stopped harness
        }
        finally
        {
            Interlocked.Exchange(ref _executing, 0);
        }
    }

    /// <summary>On the calling thread: waits for <paramref name="done"/> for at most <see cref="Budget"/>, then stops the
    /// tick thread and abandons it. Returns when the call ended or the tick thread was given up on.</summary>
    public void Await(TickThread tick, WaitHandle done, WaitHandle abandoned)
    {
        WaitHandle[] handles = [done, abandoned];
        var budget = Budget;
        if (budget == Timeout.InfiniteTimeSpan || Stopped is not null)
        {
            WaitHandle.WaitAny(handles);
            return;
        }
        if (WaitHandle.WaitAny(handles, budget) != WaitHandle.WaitTimeout) return;
        Stop(tick, done, budget);
    }

    private void Stop(TickThread tick, WaitHandle done, TimeSpan budget)
    {
        var thread = tick.Thread;
        _target = thread;
        _stopping = true;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        var kind = StopKind.NotStopped;
        var how = new List<string>();
        try
        {
            // 1. a wait: interrupt it (each interrupt throws ThreadInterruptedException at the wait)
            for (var i = 0; i < 3 && !done.WaitOne(0); i++)
            {
                if (!SeenWaiting(thread, done)) break;  // running, not waiting: the abort's case (no interrupt left pending)
                thread.Interrupt();
                how.Add("Thread.Interrupt");
                if (done.WaitOne(InterruptWait)) kind = StopKind.Interrupted;
            }
            // 2. running managed code (or a wait that swallows interrupts): abort it
            if (!done.WaitOne(0) && CanAbort && Volatile.Read(ref _executing) != 0)
            {
                how.Add("ControlledExecution abort");
                var canceller = new Thread(() =>
                {
                    try
                    {
                        _abort.Cancel();
                    }
                    catch (Exception)
                    {
                        // a token callback of the runtime's; nothing to do here
                    }
                }) { IsBackground = true, Name = "kiln-testkit watchdog abort" };
                canceller.Start();
                var until = Stopwatch.StartNew();
                while (!done.WaitOne(100) && until.Elapsed < AbortWait)
                    if (IsWaiting(thread))
                        thread.Interrupt();  // the abort request waits for the thread to run; a wake-up lets it land
                if (done.WaitOne(0)) kind = StopKind.Aborted;
            }
            if (!done.WaitOne(0)) kind = StopKind.NotStopped;
            else if (kind == StopKind.NotStopped) kind = how.Contains("ControlledExecution abort") ? StopKind.Aborted : StopKind.Interrupted;
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            _stopping = false;
        }
        var ended = done.WaitOne(0);
        var text = kind switch
        {
            StopKind.Interrupted => "stopped by Thread.Interrupt (it was blocked in a wait, a sleep or a lock)",
            StopKind.Aborted => "stopped by ControlledExecution's abort (it kept running managed code)",
            _ => CanAbort
                ? "could not be stopped (" + string.Join(", then ", how.DefaultIfEmpty("nothing reached it")) + " did not end the call): the thread was given up on and may still run"
                : "could not be stopped: this runtime has no ControlledExecution, and the thread was not in an interruptible wait; it was given up on and may still run",
        };
        Stopped = new StopReport(Subject, budget, kind, Volatile.Read(ref _stack), ended, text);
        tick.Abandon($"{Subject} blocked the tick thread for more than {budget.TotalSeconds:0.##} s and was {text}");
    }

    // A few samples 10 ms apart: a thread blocked in a wait shows WaitSleepJoin in each, a loop in none.
    private static bool SeenWaiting(Thread thread, WaitHandle done)
    {
        for (var i = 0; i < 5; i++)
        {
            if (IsWaiting(thread)) return true;
            if (done.WaitOne(10)) return false;
        }
        return false;
    }

    private static bool IsWaiting(Thread thread)
    {
        try
        {
            return (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0;
        }
        catch (ThreadStateException)
        {
            return false;
        }
    }

    // On the stopped thread, at the throw of the interrupt or the abort: the stack where it was blocked.
    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (!_stopping || !ReferenceEquals(Thread.CurrentThread, _target) || Volatile.Read(ref _stack) is not null) return;
        if (e.Exception is not (ThreadInterruptedException or ThreadAbortException or OperationCanceledException)) return;
        try
        {
            var frames = new StackTrace(1, fNeedFileInfo: true).ToString();
            Interlocked.CompareExchange(ref _stack, frames, null);
        }
        catch (Exception)
        {
            // a stack that cannot be read: the report says there is none
        }
    }
}
