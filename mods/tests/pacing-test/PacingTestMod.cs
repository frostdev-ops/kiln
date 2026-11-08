// SPDX-License-Identifier: MIT
using System.Diagnostics;
using Kiln;

namespace PacingTest;

/// <summary>
/// Lab check of the pacing API on a dedicated server with nobody online. It registers a loop-wait and a server-wait
/// handler and walks through five phases of a few seconds each, measuring the server's step rate in each:
/// A (handlers only look: the baseline, the game's own 40 ms idle wait), B (a handler turns the idle wait into 80 ms: about half
/// the steps), C (into 10 ms: two to five times the steps, since a pass is the wait plus the step itself), D (that handler is disposed: the baseline again), E (every handler
/// disposed: none is called any more). Prints "pacing-test: PASS|FAIL ..." lines, the loop wait's statistics and a summary.
/// </summary>
public sealed class PacingTestMod : Mod
{
    private const double PhaseSeconds = 9;   // the first second of each phase is not measured: a pass that started with the old wait is still running
    private const double SettleSeconds = 1;
    private int _pass, _fail;

    // What the loop-wait handler saw (any thread that waits).
    private long _loopCalls, _loopZeroMs, _loopNegLeft, _loopMessages, _loopNoDeadline, _loopInconsistent;
    private long _loopMaxMs, _loopMaxLeft, _loopMinLeft = long.MaxValue;
    private long _srvLoopCalls, _srvLoopMaxMs, _srvLoopMaxLeft;  // the waits made on the server loop's own thread
    private readonly int[] _msHistogram = new int[44];  // 0..42 ms, then more
    private readonly HashSet<int> _loopThreads = [];

    // What the server-wait handler saw.
    private long _serverCalls, _idle, _online, _slow, _otherWait;
    private long _override;  // ns that replaces the idle wait, 0 = leave it
    private readonly HashSet<int> _serverThreads = [];
    private bool _serverOnTickThread;
    private int _serverLoopThread;  // managed id of the thread the server-wait handler runs on

    private PacingHandler? _loopHandler, _countHandler, _overrideHandler;
    private int _phase = -1;
    private bool _finished;
    private long _phaseStartTs, _windowStartTs, _phaseSteps;
    private bool _windowOpen;
    private long _phaseLoopCalls, _phaseServerCalls, _phaseIdle;
    private double _rateA, _rateB, _rateC, _rateD;
    private long _afterDisposeLoop, _afterDisposeServer;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"pacing-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private static void Max(ref long target, long value)
    {
        long seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    private static void Min(ref long target, long value)
    {
        long seen;
        while (value < (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    public override void OnLoad()
    {
        Check(Pacing.Available, $"Pacing.Available (sites: {Pacing.Sites})");
        Check((Pacing.Sites & PacingSites.LoopWait) != 0 && (Pacing.Sites & PacingSites.ServerWait) != 0, "the loop wait and the server wait were found");
        Check((Pacing.Sites & PacingSites.TaskWorkers) != 0 && (Pacing.Sites & PacingSites.MessageWait) == 0, "a server has worker caps and no message wait");

        _loopHandler = Pacing.OnLoopWait(OnLoopWait);
        _countHandler = Pacing.OnServerWait(OnServerCount);
        Check(_loopHandler.Found && _countHandler.Found, "the handlers will be called (Found)");

        // The pools started before this mod loaded: the cap is refused, and the mod can tell why.
        var capped = Pacing.CapTaskWorkers(TaskWorkerCaps.AtMost(2));
        Check(!capped && Pacing.TaskWorkersStarted, $"CapTaskWorkers from a mod is too late on a server (returned {capped}, pools started {Pacing.TaskWorkersStarted})");

        Context.Tick.Pre += OnPre;
    }

    public override void OnUnload()
    {
        Context.Tick.Pre -= OnPre;
        _loopHandler?.Dispose();
        _countHandler?.Dispose();
        _overrideHandler?.Dispose();
    }

    // ---- the handlers: on the threads of the game's loops ----

    private void OnLoopWait(in LoopWait wait)
    {
        Interlocked.Increment(ref _loopCalls);
        if (wait.Milliseconds == 0) Interlocked.Increment(ref _loopZeroMs);
        if (wait.LeftNanoseconds < 0) Interlocked.Increment(ref _loopNegLeft);
        if (wait.Kind != LoopWaitKind.Handles) Interlocked.Increment(ref _loopMessages);
        // The game turns the time left into whole milliseconds, truncating, a negative time into 0, no deadline into its INFINITE.
        var noDeadline = wait.LeftNanoseconds == long.MaxValue;
        if (noDeadline) Interlocked.Increment(ref _loopNoDeadline);
        var expected = wait.LeftNanoseconds < 0 ? 0 : (int)Math.Min(wait.LeftNanoseconds / 1_000_000, int.MaxValue);
        if (wait.Milliseconds != expected) Interlocked.Increment(ref _loopInconsistent);
        if (!noDeadline)
        {
            Max(ref _loopMaxMs, wait.Milliseconds);
            Max(ref _loopMaxLeft, wait.LeftNanoseconds);
            Min(ref _loopMinLeft, wait.LeftNanoseconds);
        }
        if (_serverLoopThread == Environment.CurrentManagedThreadId)
        {
            Interlocked.Increment(ref _srvLoopCalls);
            Max(ref _srvLoopMaxMs, wait.Milliseconds);
            Max(ref _srvLoopMaxLeft, wait.LeftNanoseconds);
        }
        Interlocked.Increment(ref _msHistogram[Math.Clamp(wait.Milliseconds, 0, _msHistogram.Length - 1)]);
        lock (_loopThreads) _loopThreads.Add(Environment.CurrentManagedThreadId);
    }

    private void OnServerCount(ref long ns)
    {
        Interlocked.Increment(ref _serverCalls);
        if (ns == Pacing.ServerIdleWaitNs) Interlocked.Increment(ref _idle);
        else if (ns == Pacing.ServerOnlineWaitNs) Interlocked.Increment(ref _online);
        else if (ns == Pacing.ServerSlowWaitNs) Interlocked.Increment(ref _slow);
        else Interlocked.Increment(ref _otherWait);
        _serverLoopThread = Environment.CurrentManagedThreadId;
        lock (_serverThreads) _serverThreads.Add(Environment.CurrentManagedThreadId);
        _serverOnTickThread = Game.IsTickThread;
    }

    // ---- the phases: on the tick thread ----

    private void OnPre(in TickInfo tick)
    {
        if (_finished) return;
        var now = Stopwatch.GetTimestamp();
        if (_phase < 0)
        {
            StartPhase(0, now);
            return;
        }
        var elapsed = Stopwatch.GetElapsedTime(_phaseStartTs, now).TotalSeconds;
        if (!_windowOpen)
        {
            if (elapsed < SettleSeconds) return;
            OpenWindow(now);
            return;
        }
        _phaseSteps++;
        if (elapsed < PhaseSeconds) return;
        EndPhase(now);
    }

    // What a phase reports is measured from here, a second after its change.
    private void OpenWindow(long now)
    {
        _windowOpen = true;
        _windowStartTs = now;
        _phaseSteps = 0;
        _phaseLoopCalls = Interlocked.Read(ref _loopCalls);
        _phaseServerCalls = Interlocked.Read(ref _serverCalls);
        _phaseIdle = Interlocked.Read(ref _idle);
        Interlocked.Exchange(ref _srvLoopMaxMs, 0);
        Interlocked.Exchange(ref _srvLoopMaxLeft, 0);
    }

    private void StartPhase(int phase, long now)
    {
        _phase = phase;
        _phaseStartTs = now;
        _windowOpen = false;
        _phaseSteps = 0;
        switch (phase)
        {
            case 1:  // B: the idle wait doubles
                Interlocked.Exchange(ref _override, 80_000_000);
                _overrideHandler = Pacing.OnServerWait(OnServerOverride);
                break;
            case 2:  // C: and then a quarter
                Interlocked.Exchange(ref _override, 10_000_000);
                break;
            case 3:  // D: restored by taking the handler away
                _overrideHandler!.Dispose();
                _overrideHandler = null;
                break;
            case 4:  // E: every handler gone
                _loopHandler!.Dispose();
                _countHandler!.Dispose();
                _afterDisposeLoop = Interlocked.Read(ref _loopCalls);
                _afterDisposeServer = Interlocked.Read(ref _serverCalls);
                break;
        }
        var name = "ABCDE"[phase];
        Log.Info($"pacing-test: phase {name} starts (step {_phaseSteps})");
    }

    private void OnServerOverride(ref long ns)
    {
        var o = Volatile.Read(ref _override);
        if (o > 0 && ns == Pacing.ServerIdleWaitNs) ns = o;
    }

    private void EndPhase(long now)
    {
        var seconds = Stopwatch.GetElapsedTime(_windowStartTs, now).TotalSeconds;
        var rate = _phaseSteps / seconds;
        var serverLoopMs = Interlocked.Read(ref _srvLoopMaxMs);
        var loop = Interlocked.Read(ref _loopCalls) - _phaseLoopCalls;
        var server = Interlocked.Read(ref _serverCalls) - _phaseServerCalls;
        var idle = Interlocked.Read(ref _idle) - _phaseIdle;
        var name = "ABCDE"[_phase];
        Log.Info($"pacing-test: phase {name}: {_phaseSteps} steps in {seconds:F2} s = {rate:F2} Hz; loop-wait calls {loop} ({loop / seconds:F0}/s), server-wait calls {server} ({server / seconds:F1}/s, {idle} of them the 40 ms idle wait); " +
                 $"the server loop's own loop waits were at most {serverLoopMs} ms");
        switch (_phase)
        {
            case 0:
                _rateA = rate;
                Check(rate is > 15 and < 30, $"A: the baseline step rate is about 1 / (40 ms wait + the step), 24.5 Hz on an idle machine ({rate:F2})");
                Check(loop > 50 && server > 50, $"A: both handlers were called ({loop} loop waits, {server} server waits)");
                Check(idle >= server * 9 / 10, $"A: with nobody online the server loop picked its 40 ms idle wait ({idle} of {server})");
                ReportLoop();
                break;
            case 1:
                _rateB = rate;
                Check(serverLoopMs is >= 76 and <= 80, $"B: the game's loop now waits the 80 ms the handler set (its loop waits on the server thread were at most {serverLoopMs} ms)");
                Check(rate / _rateA < 0.75, $"B: an 80 ms idle wait gives fewer steps, about half on an idle machine ({rate:F2} Hz = {rate / _rateA:P0} of {_rateA:F2})");
                break;
            case 2:
                _rateC = rate;
                Check(serverLoopMs is >= 8 and <= 10, $"C: the game's loop now waits the 10 ms the handler set (its loop waits on the server thread were at most {serverLoopMs} ms)");
                Check(rate / _rateA > 1.2, $"C: a 10 ms idle wait gives more steps, 2.5x or more on an idle machine ({rate:F2} Hz = {rate / _rateA:F2}x of {_rateA:F2})");
                break;
            case 3:
                _rateD = rate;
                Check(serverLoopMs is >= 38 and <= 40, $"D: the game's loop is back to its own 40 ms (its loop waits on the server thread were at most {serverLoopMs} ms)");
                Check(rate / _rateA is > 0.65 and < 1.5, $"D: with the handler disposed the rate is back, within what a busy machine moves it ({rate:F2} Hz vs baseline {_rateA:F2})");
                Check(Pacing.HandlerCount(PacingSites.ServerWait) == 1, "D: only the counting handler is left");
                break;
            case 4:
                var loopAfter = Interlocked.Read(ref _loopCalls) - _afterDisposeLoop;
                var serverAfter = Interlocked.Read(ref _serverCalls) - _afterDisposeServer;
                Check(loopAfter == 0 && serverAfter == 0, $"E: disposed handlers are not called any more ({loopAfter} loop, {serverAfter} server in {seconds:F0} s)");
                Check(rate / _rateA is > 0.65 and < 1.5, $"E: the game paces itself as before ({rate:F2} Hz vs baseline {_rateA:F2})");
                Finish();
                return;
        }
        StartPhase(_phase + 1, now);
    }

    private void ReportLoop()
    {
        var hist = string.Join(" ", _msHistogram.Select((n, ms) => (n, ms)).Where(x => x.n > 0).Select(x => $"{(x.ms == _msHistogram.Length - 1 ? ">=" : "")}{x.ms}ms:{x.n}"));
        Log.Info($"pacing-test: loop wait: {_loopCalls} calls on {_loopThreads.Count} thread(s): {_loopZeroMs} with 0 ms, {_loopNoDeadline} with no deadline (left = long.MaxValue, ms = int.MaxValue), " +
                 $"{_loopNegLeft} with a passed deadline, {_loopMessages} message waits; the others waited up to {_loopMaxMs} ms with {_loopMinLeft}..{_loopMaxLeft} ns left; histogram {hist}");
        Log.Info($"pacing-test: loop wait on the server loop's own thread: {_srvLoopCalls} calls, at most {_srvLoopMaxMs} ms and {_srvLoopMaxLeft} ns left");
        Check(_loopCalls > 50 && _loopInconsistent == 0 && _loopMessages == 0,
            $"the loop wait's values are consistent: ms is always the time left in whole milliseconds, handles only ({_loopCalls} calls, {_loopInconsistent} inconsistent)");
        Check(_srvLoopCalls > 50 && _srvLoopMaxMs <= 40 && _srvLoopMaxLeft <= 40_000_000,
            $"on the server loop's thread the loop waits are at most the 40 ms idle wait ({_srvLoopMaxMs} ms, {_srvLoopMaxLeft} ns left)");
        Log.Info($"pacing-test: server wait ran on {_serverThreads.Count} thread(s), on the tick thread: {_serverOnTickThread}; waits seen: {_idle} idle, {_online} online, {_slow} slow, {_otherWait} other");
        Check(_otherWait == 0, "the server loop only ever picked its three waits");
    }

    private void Finish()
    {
        _finished = true;
        Log.Info($"pacing-test: rates A {_rateA:F2} B {_rateB:F2} C {_rateC:F2} D {_rateD:F2} Hz");
        Log.Info($"pacing-test: done: {_pass} PASS, {_fail} FAIL");
        Log.Info($"pacing-test: {(_fail == 0 ? "PASS" : "FAIL")}");
    }
}
