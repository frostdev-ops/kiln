// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

namespace Kiln.TestKit;

/// <summary>Where a harness's server is in its life.</summary>
public enum HarnessState
{
    /// <summary>No step yet: mods given to <c>Load</c> wait in <c>kiln/mods</c> for the first step.</summary>
    Created = 0,
    /// <summary>Mods are loaded; the server waits for the world to run (<see cref="KilnHarnessOptions.WaitForWorldRunning"/>).</summary>
    Starting = 1,
    /// <summary><see cref="ServerStartedEvent"/> fired.</summary>
    Running = 2,
    /// <summary><see cref="ServerStoppingEvent"/> fired and every mod unloaded; steps run nothing any more.</summary>
    Stopped = 3,
    /// <summary>Disposed.</summary>
    Disposed = 4,
    /// <summary>The tick thread was given up on (a test blocked it): every call throws <see cref="HarnessStoppedException"/>.</summary>
    Abandoned = 5,
}

public sealed partial class KilnHarness
{
    // Kiln.Host/Entry.cs: the lines its lifecycle watches for, the fallback, and the client's menu steps.
    internal const string WorldRunningLine = "Switching state from LoadEcsScene to Run";
    internal const string ShutdownLine = "Trigger gameflow shutdown";
    private const int StartedFallbackSteps = 2450;
    private const ulong MenuStepBase = 1UL << 40;
    private static ulong _bootTicks;

    private ulong _step, _loadStep;
    private long _virtualMs;  // steps x WallDeltaMs, as whole ms (the fault policy's clock)
    private double _clock;
    private bool _modsLoaded, _started, _runSeen, _stopping;
    private string? _stopRequest;
    private readonly Queue<(LogLevel, string)> _gameLines = new();
    private readonly List<(LogLevel, string)> _lineBatch = [];

    /// <summary>The last step that ran (0 before the first). Steps count from 1, as on a server.</summary>
    public ulong CurrentStep => Volatile.Read(ref _step);

    /// <summary>Where the server is.</summary>
    public HarnessState State =>
        Volatile.Read(ref _disposed) != 0 ? HarnessState.Disposed
        : _tick.AbandonReason is not null ? HarnessState.Abandoned
        : _stopping ? HarnessState.Stopped
        : _started ? HarnessState.Running
        : _modsLoaded ? HarnessState.Starting
        : HarnessState.Created;

    /// <summary>
    /// Runs <paramref name="steps"/> server steps, each in the order of Kiln.Host's Pre: the first one loads the mods
    /// waiting in <c>kiln/mods</c> (each <c>OnLoad</c> in dependency order, then <see cref="ModsLoadedEvent"/> and
    /// <see cref="ServerStartingEvent"/>) and, unless <see cref="KilnHarnessOptions.WaitForWorldRunning"/>, fires
    /// <see cref="ServerStartedEvent"/>; then every step runs the mods' queued requests (<c>Mods.ReloadAsync</c> and the
    /// like), Kiln's service tick, each mod's scheduled work, inter-mod messages, game-log lines and <c>Tick.Pre</c>, then
    /// <c>Tick.Post</c> when a mod listens there.
    /// </summary>
    /// <remarks>Time is virtual: steps never sleep. <see cref="TickInfo"/> advances like a dedicated server's (24.5 steps a
    /// second: <see cref="KilnHarnessOptions.WallDeltaMs"/> each, the 60 Hz game clock by 2 or 3); the fault policy's
    /// window counts the same time. A mod that reads <see cref="Environment.TickCount64"/>, <see cref="DateTime.UtcNow"/> or
    /// a <see cref="System.Diagnostics.Stopwatch"/> sees real time, which barely moves across many steps; file watchers and
    /// timers run in real time as well (<see cref="StepUntil"/> can pace steps for them).</remarks>
    /// <exception cref="ModLoadException">At the first step, a waiting mod did not load.</exception>
    public void Step(int steps = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        Call(() =>
        {
            for (var i = 0; i < steps; i++) StepOnce();
        });
    }

    /// <summary>Loads the waiting mods and starts the server (the first step) when that has not happened yet.</summary>
    public void Boot()
    {
        Call(() =>
        {
            if (!_modsLoaded) StepOnce();
        });
    }

    /// <summary>Steps until <paramref name="condition"/> (checked on the tick thread before each step) is true; returns
    /// the steps it took (0 when it already was).</summary>
    /// <param name="condition">What the test waits for.</param>
    /// <param name="maxSteps">Then it throws <see cref="TimeoutException"/>.</param>
    /// <param name="pace">Real time to wait after each step, for what runs in real time (a file watcher, a timer, a
    /// thread of the mod's own). Default none.</param>
    public int StepUntil(Func<bool> condition, int maxSteps = 1000, TimeSpan pace = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSteps);
        return Call(() =>
        {
            for (var i = 0; ; i++)
            {
                if (condition()) return i;
                if (i >= maxSteps) throw new TimeoutException($"the condition was still false after {maxSteps} steps (now at step {_step})");
                StepOnce();
                if (pace > TimeSpan.Zero) Thread.Sleep(pace);
            }
        });
    }

    /// <summary>A line of the game's own log, delivered at the next step: to mods' <see cref="ModContext.GameLog"/>
    /// handlers, and to Kiln's lifecycle, which watches for the world-running and shutdown lines. The game writes no
    /// lines here; only the test does.</summary>
    public void GameLog(string text, LogLevel level = LogLevel.Info)
    {
        ArgumentNullException.ThrowIfNull(text);
        ThrowIfDisposed();
        lock (_gameLines) _gameLines.Enqueue((level, text));
    }

    /// <summary>The game says its world runs (the line Kiln's lifecycle waits for): with
    /// <see cref="KilnHarnessOptions.WaitForWorldRunning"/>, <see cref="ServerStartedEvent"/> fires at the next step.</summary>
    public void ReportWorldRunning() => GameLog($"[gameflow] {WorldRunningLine}");

    /// <summary>Stops the server at once, as a stop signal does: <see cref="ServerStoppingEvent"/> with
    /// <paramref name="reason"/>, then every mod's <c>OnUnload</c> in reverse load order. <see cref="Dispose"/> does this
    /// when the test has not. Steps after it run nothing.</summary>
    /// <param name="reason">The event's <see cref="ServerStoppingEvent.Reason"/>: <c>SIGINT</c> (default, a console
    /// Ctrl-C), <c>SIGTERM</c>, <c>SIGHUP</c>, <c>SIGQUIT</c>; <c>game-log</c> is what the game's own shutdown line gives.</param>
    public void Stop(string reason = "SIGINT")
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        Call(() =>
        {
            if (_stopping) return;
            if (!_modsLoaded)
            {
                _stopping = true;  // nothing was loaded: there is nothing to tell
                return;
            }
            _stopRequest ??= reason;
            StepOnce();
        });
    }

    // Disposal, on the tick thread: stop if the server ran, then say which unloaded contexts are still alive.
    private void ShutDown()
    {
        if (_modsLoaded && !_stopping)
        {
            _stopRequest ??= "SIGINT";
            StepOnce();
        }
        if (_mods.CollectAndCountLeaks(out var leaked) > 0)
            _host.Log(LogLevel.Warn, $"testkit: the load context of {string.Join(", ", leaked)} was still alive when the harness ended");
    }

    // Kiln.Api's service tick ignores a step equal to the last it ran: a server that ended at step 1 would cost this one
    // its first service tick. The client runs service ticks in its menu at steps from 1 << 40; one of those here.
    private void BootServiceTick()
    {
        if (ServiceTick.Step != 1) return;
        ServiceTick.Run(HostAccess.Tick(MenuStepBase + ++_bootTicks, 0, 0, 0, 0));
    }

    /// <summary>One server step, as Kiln.Host's <c>HostRuntime.Pre</c> (dedicated server) and <c>Post</c> run it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StepOnce()
    {
        var before = _clock;
        _clock += _options.WallDeltaMs * 60 / 1000;
        var delta = (uint)(Math.Floor(_clock) - Math.Floor(before));
        var step = _step + 1;
        Volatile.Write(ref _step, step);
        Volatile.Write(ref _virtualMs, (long)(step * _options.WallDeltaMs));
        var tick = HostAccess.Tick(step, (uint)Math.Floor(_clock), delta, 0, _options.WallDeltaMs);

        _lineBatch.Clear();
        lock (_gameLines)
            while (_lineBatch.Count < 1024 && _gameLines.TryDequeue(out var line))
            {
                _lineBatch.Add(line);
                WatchLine(line.Item2);
            }
        if (!_modsLoaded)
        {
            _modsLoaded = true;
            _loadStep = step;
            _lineBatch.Clear();  // boot lines are for the lifecycle only
            LoadMods(step);
        }
        if (_stopRequest is { } reason && !_stopping) BeginStop(reason, step);
        if (_stopping) return;
        if (!_started && (_runSeen || !_options.WaitForWorldRunning || step - _loadStep >= StartedFallbackSteps))
            ServerStarted(step, _runSeen ? "game-log" : !_options.WaitForWorldRunning ? "first-step" : "timeout");
        _mods.ProcessRequests();
        ServiceTick.Run(in tick);
        _mods.Pre(in tick, _lineBatch);
        if (_mods.AnyPost) _mods.Post(in tick);
        if (_loadStep == step) ThrowIfStagedFailed();
    }

    private void WatchLine(string text)
    {
        if (!_runSeen && text.Contains(WorldRunningLine, StringComparison.Ordinal))
        {
            _runSeen = true;
            _host.Log(LogLevel.Info, $"lifecycle: the game reports the world running (step {_step})");
        }
        else if (text.Contains(ShutdownLine, StringComparison.Ordinal))
        {
            _host.Log(LogLevel.Info, $"lifecycle: the game began its shutdown ('{text.Trim()}')");
            _stopRequest ??= "game-log";
        }
    }

    private void LoadMods(ulong step)
    {
        Lifecycle.Phase = ServerPhase.Booting;
        _mods.LoadAll();
        Lifecycle.ModsLoaded = true;
        var all = Mods.All;
        _mods.PostEvent(new ModsLoadedEvent(all.Where(e => e.IsLoaded).ToArray(), all.Where(e => !e.IsLoaded).ToArray()));
        Lifecycle.Phase = ServerPhase.Starting;
        _mods.PostEvent(new ServerStartingEvent(step));
        _host.Log(LogLevel.Info, $"lifecycle: mods loaded, server starting (step {step})");
    }

    private void ServerStarted(ulong step, string source)
    {
        _started = true;
        Lifecycle.Phase = ServerPhase.Running;
        Lifecycle.StartedAtStep = step;
        if (source == "timeout") _host.Log(LogLevel.Warn, $"lifecycle: '{WorldRunningLine}' not seen in the game log; ServerStarted fires anyway");
        _mods.PostEvent(new ServerStartedEvent(step, source));
        _host.Log(LogLevel.Info, $"lifecycle: server started (step {step}, {source})");
        if (!_mods.SuppressAutoTests) _mods.QueueAutoTests();  // a KilnTestSession runs every test itself
    }

    private void BeginStop(string reason, ulong step)
    {
        _stopping = true;
        Lifecycle.Phase = ServerPhase.Stopping;
        var count = _mods.Mods.Count;
        _mods.PostEvent(new ServerStoppingEvent(step, reason));
        _mods.UnloadAll("server stopping");
        _host.Log(LogLevel.Info, $"lifecycle: server stopping ({reason}, step {step}): {count} mod(s) notified and unloaded");
    }
}
