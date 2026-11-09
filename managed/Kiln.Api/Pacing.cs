// SPDX-License-Identifier: MIT
// Pacing: where the engine waits, and how many worker threads it starts (Enshwick's `pacing`: loop_wait, server_wait,
// task_workers). A mod uses them to make the game use less CPU: sleep through a wait the game would poll, stretch the
// server loop's wait, or cap the task pools the engine sizes from the CPUs it sees.
//
// This file is the managed half: the registry that merges what mods ask for, in the order Enshwick's loader does (handlers
// in load order, each seeing the last one's answer; the lowest worker cap wins). The native half (service kiln.pacing,
// native/services/pacing; the bridge is Services/PacingNative.cs) finds the places in the running build, puts a hook in
// each place a mod uses and calls the Dispatch* members below. Usage: docs/kiln/modding/lifecycle.md.
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>Which of the game's waits this build lets mods pace.</summary>
[Flags]
public enum PacingSites
{
    None = 0,
    /// <summary>The event loop's wait on its handles (server and client): <see cref="Pacing.OnLoopWait"/>.</summary>
    LoopWait = 1,
    /// <summary>The client's wait on handles and window messages (also <see cref="Pacing.OnLoopWait"/>).</summary>
    MessageWait = 2,
    /// <summary>The server loop's wait between two passes: <see cref="Pacing.OnServerWait"/>.</summary>
    ServerWait = 4,
    /// <summary>The task system's worker caps as it starts (dedicated server only): <see cref="Pacing.CapTaskWorkers"/>.</summary>
    TaskWorkers = 8,
}

/// <summary>Which wait of the event loop.</summary>
public enum LoopWaitKind
{
    /// <summary>A wait on handles (server and client).</summary>
    Handles = 0,
    /// <summary>The client's wait on handles and window messages.</summary>
    Messages = 1,
}

/// <summary>A wait of the engine's event loop, about to happen (<see cref="Pacing.OnLoopWait"/>).</summary>
/// <param name="Milliseconds">What the game will wait: the time left, truncated to whole milliseconds. With less than a
/// millisecond left that is 0, again and again, until the deadline passes.</param>
/// <param name="LeftNanoseconds">The time left before the loop's deadline.</param>
/// <param name="Kind">Which wait.</param>
public readonly record struct LoopWait(int Milliseconds, long LeftNanoseconds, LoopWaitKind Kind = LoopWaitKind.Handles);

/// <summary>The most worker threads the task system starts each group with; 0 leaves a group as the game sizes it.</summary>
/// <param name="Default">The default group, which also sizes the ECS task queue.</param>
/// <param name="Background">The background group: streaming, saves and other slow work.</param>
public readonly record struct TaskWorkerCaps(int Default, int Background)
{
    /// <summary>The same cap for both groups.</summary>
    public static TaskWorkerCaps AtMost(int workers) => new(workers, workers);

    /// <summary>Both groups are left as the game sizes them.</summary>
    public bool IsEmpty => Default <= 0 && Background <= 0;

    /// <summary>The lowest cap of each group wins; a 0 does not count.</summary>
    internal static TaskWorkerCaps Merge(TaskWorkerCaps a, TaskWorkerCaps b) => new(Lowest(a.Default, b.Default), Lowest(a.Background, b.Background));

    private static int Lowest(int a, int b) => a <= 0 ? Math.Max(b, 0) : b <= 0 ? a : Math.Min(a, b);
}

/// <summary>Runs on the thread that waits (the server loop, once a pass): <paramref name="waitNanoseconds"/> is what the
/// loop picked; set it to change it. An event still ends the wait early.</summary>
public delegate void ServerWaitHandler(ref long waitNanoseconds);

/// <summary>Runs on the thread that waits, just before the event loop waits.</summary>
public delegate void LoopWaitHandler(in LoopWait wait);

/// <summary>A pacing handler a mod registered. Dispose removes it (a mod's handlers also go when it unloads).</summary>
public sealed class PacingHandler : IDisposable
{
    internal PacingHandler(PacingSites site, Delegate handler, ModContext? owner, long order)
    {
        Site = site;
        Handler = handler;
        Owner = owner;
        Order = order;
    }

    internal PacingSites Site { get; }
    internal Delegate Handler { get; }
    internal ModContext? Owner { get; }
    internal long Order { get; }
    internal int Faults;
    internal volatile bool Removed;

    /// <summary>This build has the wait the handler is for (it will be called). False on a build that lacks it, or whose
    /// code would not take the hook. Any thread.</summary>
    public bool Found => (Pacing.Sites & Site) != 0;

    /// <summary>The handler threw too often and was removed (see <see cref="Pacing"/>).</summary>
    public bool Faulted { get; internal set; }

    public void Dispose() => Pacing.Remove(this);
}

/// <summary>
/// Where the engine waits and how many worker threads it starts. Handlers run <b>inside the game's loops</b>, on the thread that
/// waits and as often as it waits: keep them short, take no locks and call no game API (read what the mod keeps in
/// fields, written from the tick thread).
/// </summary>
/// <remarks>
/// <para>Kiln finds the game's waits in the running build (service <c>kiln.pacing</c>, byte patterns in its symbol database) and
/// puts a hook in a place only when a mod registers a handler for it, so a server with no pacing handler runs the game's own code
/// untouched. A hook, once in, stays; while no handler is registered it costs one flag check. On a dedicated server
/// <see cref="Sites"/> is <c>LoopWait | ServerWait | TaskWorkers</c> (run in a lab server on Steam build 26430253; the patterns also
/// match 26404334). On a client it also has <see cref="PacingSites.MessageWait"/> and no worker caps; the client's places are matched on the two
/// known client builds but have not run in a client yet. A build whose code Kiln does not know has none of them: <see cref="Sites"/>
/// is <see cref="PacingSites.None"/>, <see cref="Available"/> is false and handlers are never called.</para>
/// <para>Handlers run in the mods' load order, each on the last one's answer. A handler that throws three times in a row is
/// removed (logged); a call that works resets the count. They run on the thread that waits, which for a loop wait is several
/// threads at once, and the game's main loop polls with 0 ms waits some 100,000 times a second: keep a loop-wait handler to a few
/// field reads and writes.</para>
/// <para><b>Worker caps start with the game.</b> The task system sizes its pools before the first server step, and a mod loads at
/// that step, so <see cref="CapTaskWorkers"/> from a mod always returns false on a Kiln server (it also does on a client, once the
/// pools have started and on a build without the place); it logs the reason once. What caps the pools on a Kiln server is
/// <c>taskWorkers</c> in <c>kiln/config/kiln-pacing.json</c> (lab-verified, see docs/kiln/modding/lifecycle.md). The lowest cap of
/// each group wins, whoever sets it.</para>
/// <para>Whether a place exists is a fact about the game build: <see cref="Sites"/> says which Kiln found. A build that lacks one
/// is not an error, the handler is simply never called.</para>
/// </remarks>
public static class Pacing
{
    /// <summary>The server loop's wait with nobody online (nanoseconds).</summary>
    public const long ServerIdleWaitNs = 40_000_000;

    /// <summary>The server loop's wait with players online.</summary>
    public const long ServerOnlineWaitNs = 1_000_000;

    /// <summary>The server loop's wait after a pass longer than 14 ms.</summary>
    public const long ServerSlowWaitNs = 100_000;

    /// <summary>The longest wait a handler can set for the server loop: what handlers leave is clamped into
    /// <see cref="ServerMinWaitNs"/> to this (the loop still wakes for its own timers, and a wild value is never what anyone
    /// meant). A negative value is ignored: the game's own pick stays.</summary>
    public const long ServerMaxWaitNs = 1_000_000_000;

    /// <summary>The shortest wait a handler can set for the server loop: the game's own shortest
    /// (<see cref="ServerSlowWaitNs"/>). A 0 would turn the loop into a busy spin, one step after another at 100 % CPU.</summary>
    public const long ServerMinWaitNs = ServerSlowWaitNs;

    private const int FaultLimit = 3;

    private static readonly object Lock = new();
    private static volatile PacingHandler[] _handlers = [];
    private static readonly List<(TaskWorkerCaps Caps, ModContext? Owner)> Caps = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static volatile int _sites;
    private static volatile bool _tasksStarted;
    private static long _sequence;

    /// <summary>Which waits this build lets mods pace (what the game's code lets Kiln hook). Any thread.</summary>
    public static PacingSites Sites => (PacingSites)_sites;

    /// <summary>At least one wait or cap can be used on this build: Kiln found the place in the game's code.</summary>
    public static bool Available => _sites != 0;

    /// <summary>The task system has started its worker pools: caps come too late. On a dedicated server that is so before
    /// any mod loads (the pools start with the game, the mods at its first step).</summary>
    public static bool TaskWorkersStarted => _tasksStarted || TasksStartedProbe?.Invoke() == true;

    /// <summary>The cap each task group starts with, as the mods asked for them (0: left to the game).</summary>
    public static TaskWorkerCaps EffectiveTaskWorkerCaps
    {
        get
        {
            lock (Lock) return Merged();
        }
    }

    /// <summary>Handlers registered now, per site (diagnostics).</summary>
    public static int HandlerCount(PacingSites site) => _handlers.Count(h => (h.Site & site) != 0 && !h.Removed);

    /// <summary>Runs <paramref name="handler"/> before every wait of the engine's event loop on its handles, on the thread
    /// that waits: the main loop, the server's game loop and its session, and on a client the message wait too. A handler
    /// that sleeps must never sleep past <see cref="LoopWait.LeftNanoseconds"/> and must leave the waiting to the game, so the
    /// game still sees every event. <see cref="LoopWait.Milliseconds"/> is <see cref="int.MaxValue"/> and
    /// <see cref="LoopWait.LeftNanoseconds"/> is <see cref="long.MaxValue"/> when the loop has no deadline (the game's
    /// INFINITE). Tick thread; owned by the calling mod. Never called where <see cref="Sites"/> lacks the place.</summary>
    [TickThreadOnly]
    public static PacingHandler OnLoopWait(LoopWaitHandler handler) => Add(PacingSites.LoopWait | PacingSites.MessageWait, handler);

    /// <summary>Runs <paramref name="handler"/> where the server's game loop has picked its wait between two passes
    /// (<see cref="ServerIdleWaitNs"/>, <see cref="ServerOnlineWaitNs"/> or <see cref="ServerSlowWaitNs"/>); it may change it.
    /// A longer wait with players online means fewer passes between the simulation steps and up to that much more latency for
    /// what arrives between them. The loop still wakes for its own timers, and what the handlers leave is clamped into
    /// <see cref="ServerMinWaitNs"/> to <see cref="ServerMaxWaitNs"/> (a negative value leaves the game's pick). On the server loop's thread, once a pass. Tick thread; owned by the calling mod. Never
    /// called where <see cref="Sites"/> lacks the place.</summary>
    [TickThreadOnly]
    public static PacingHandler OnServerWait(ServerWaitHandler handler) => Add(PacingSites.ServerWait, handler);

    /// <summary>Caps the worker threads each task group starts with, for the calling mod. The engine sizes the pools from
    /// the logical CPUs it sees (under Wine, the host's, whatever a container allows). Returns whether the cap will apply:
    /// false for caps of 0, on a client, once the pools have started and on a build without the place. On a Kiln server the
    /// pools have started before any mod loads, so this is false there: cap them with <c>taskWorkers</c> in
    /// <c>kiln/config/kiln-pacing.json</c>. A mod's cap is withdrawn when it unloads (the pools it limited stay as they
    /// started). Tick thread.</summary>
    [TickThreadOnly]
    public static bool CapTaskWorkers(TaskWorkerCaps caps)
    {
        Game.AssertTickThread();
        if (caps.IsEmpty) return false;
        var owner = ModContext.CurrentMod;
        if (owner is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        if (TaskWorkersStarted)
        {
            LogLateCaps(owner);
            return false;
        }
        lock (Lock)
        {
            if (_tasksStarted || (Sites & PacingSites.TaskWorkers) == 0) return false;
            Caps.RemoveAll(c => ReferenceEquals(c.Owner, owner) && owner is not null);
            Caps.Add((caps, owner));
            Watch(owner);
        }
        Changed?.Invoke();
        return true;
    }

    private static bool _loggedLateCaps;

    // Once per run: a mod learns why its cap did not apply and what does (a mod loads after the pools started).
    private static void LogLateCaps(ModContext? owner)
    {
        if (_loggedLateCaps) return;
        _loggedLateCaps = true;
        const string text = "Pacing.CapTaskWorkers: the task system has started its pools already (it starts with the game, before any mod loads); " +
                            "set \"taskWorkers\" in kiln/config/kiln-pacing.json to cap them from the next start";
        if (owner is not null) owner.Log.Info(text);
        else Backend.Current.Log(0, LogLevel.Info, "kiln: " + text);
    }

    // ---------------------------------------------------------------------------------------------- internals ----

    private static PacingHandler Add(PacingSites site, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Game.AssertTickThread();
        var owner = ModContext.CurrentMod;
        if (owner is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        var h = new PacingHandler(site, handler, owner, Interlocked.Increment(ref _sequence));
        lock (Lock)
        {
            _handlers = [.. _handlers, h];
            Sort();
            Watch(owner);
        }
        Changed?.Invoke();
        return h;
    }

    internal static void Remove(PacingHandler h)
    {
        lock (Lock)
        {
            h.Removed = true;
            _handlers = _handlers.Where(x => !ReferenceEquals(x, h)).ToArray();
        }
        Changed?.Invoke();
    }

    // Handlers run in load order; one mod's handlers in the order it registered them.
    private static void Sort()
    {
        _handlers = _handlers.OrderBy(h => LoadIndex(h.Owner)).ThenBy(h => h.Order).ToArray();
    }

    private static int LoadIndex(ModContext? mod)
    {
        if (mod is null) return -1;  // Kiln's own services first
        try
        {
            return Mods.Get(mod.Info.Id) is { LoadIndex: >= 0 } e ? e.LoadIndex : int.MaxValue;
        }
        catch (Exception)
        {
            return int.MaxValue;
        }
    }

    private static void Watch(ModContext? mod)
    {
        if (mod is null || !Watched.Add(mod)) return;
        mod.Retiring += () =>
        {
            lock (Lock)
            {
                Watched.Remove(mod);
                foreach (var h in _handlers.Where(h => ReferenceEquals(h.Owner, mod))) h.Removed = true;
                _handlers = _handlers.Where(h => !ReferenceEquals(h.Owner, mod)).ToArray();
                Caps.RemoveAll(c => ReferenceEquals(c.Owner, mod));
            }
            Changed?.Invoke();
        };
    }

    private static TaskWorkerCaps Merged()
    {
        var merged = default(TaskWorkerCaps);
        foreach (var (caps, _) in Caps) merged = TaskWorkerCaps.Merge(merged, caps);
        return merged;
    }

    // ---- the native half (Kiln.Host or a service) ----

    /// <summary>Handlers or caps were added or removed: the native half arms its hooks only for what mods use. May run on
    /// any thread (a handler that fails is removed on the thread that called it).</summary>
    internal static event Action? Changed;

    /// <summary>The places the registered handlers are for (all of them, whether or not this build has them).</summary>
    internal static PacingSites Wanted
    {
        get
        {
            var wanted = PacingSites.None;
            foreach (var h in _handlers) wanted |= h.Site;
            return wanted;
        }
    }

    /// <summary>Asks the native half whether its task system has started (set by the native bridge).</summary>
    internal static Func<bool>? TasksStartedProbe;

    /// <summary>The native half could not put the hook for these places in: they are not available after all.</summary>
    internal static void Narrow(PacingSites lost)
    {
        lock (Lock) _sites &= ~(int)lost;
    }

    /// <summary>The native half reports which places it found and hooked.</summary>
    internal static void Attach(PacingSites found)
    {
        _sites = (int)found;
        Changed?.Invoke();
    }

    /// <summary>The server loop picked <paramref name="waitNanoseconds"/>: every server-wait handler may change it. On the
    /// server loop's thread; allocates nothing and does nothing without handlers.</summary>
    internal static void DispatchServerWait(ref long waitNanoseconds)
    {
        var all = _handlers;
        if (all.Length == 0) return;
        var before = waitNanoseconds;
        foreach (var h in all)
        {
            if (h.Site != PacingSites.ServerWait || h.Removed) continue;
            var wait = waitNanoseconds;
            if (Call(h, RunServerWait, ref wait, default)) waitNanoseconds = wait;
        }
        if (waitNanoseconds != before)
            waitNanoseconds = waitNanoseconds < 0 ? before : Math.Clamp(waitNanoseconds, ServerMinWaitNs, ServerMaxWaitNs);
    }

    /// <summary>The event loop is about to wait: every loop-wait handler sees it. On the waiting thread.</summary>
    internal static void DispatchLoopWait(in LoopWait wait)
    {
        var all = _handlers;
        if (all.Length == 0) return;
        foreach (var h in all)
        {
            if ((h.Site & PacingSites.LoopWait) == 0 || h.Removed) continue;
            long unused = 0;
            Call(h, RunLoopWait, ref unused, wait);
        }
    }

    /// <summary>The task system is starting its pools: the caps to apply (0: leave a group). Later caps are refused.</summary>
    internal static TaskWorkerCaps TakeTaskWorkerCaps()
    {
        lock (Lock)
        {
            _tasksStarted = true;
            return Merged();
        }
    }

    private delegate void Invoker(Delegate handler, ref long wait, in LoopWait loop);

    private static void RunServerWait(Delegate handler, ref long wait, in LoopWait loop) => ((ServerWaitHandler)handler)(ref wait);

    private static void RunLoopWait(Delegate handler, ref long wait, in LoopWait loop) => ((LoopWaitHandler)handler)(in loop);

    // A handler runs as its mod (so its logger and Mods.Current work) and may fail: a call that throws is logged, and the
    // third failure in a row removes the handler; a game's wait must never be a place a bug can spin.
    private static bool Call(PacingHandler h, Invoker invoke, ref long wait, in LoopWait loop)
    {
        var mod = h.Owner;
        var previous = ModContext.CurrentMod;
        if (mod is not null)
        {
            if (!mod.Alive) return false;
            ModContext.CurrentMod = mod;
        }
        try
        {
            invoke(h.Handler, ref wait, in loop);
            Interlocked.Exchange(ref h.Faults, 0);
            return true;
        }
        catch (Exception e)
        {
            Fault(h, e);
            return false;
        }
        finally
        {
            if (mod is not null) ModContext.CurrentMod = previous;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Fault(PacingHandler h, Exception e)
    {
        var n = Interlocked.Increment(ref h.Faults);
        var what = $"pacing handler ({h.Site}) threw ({n} of {FaultLimit})";
        if (h.Owner is not null) h.Owner.Log.Error(e, what);
        else Backend.Current.Log(0, LogLevel.Error, $"kiln: {what}: {e}");
        if (n < FaultLimit) return;
        h.Faulted = true;
        Remove(h);
        var text = $"pacing handler ({h.Site}) removed after {FaultLimit} failures in a row";
        if (h.Owner is not null) h.Owner.Log.Error(text);
        else Backend.Current.Log(0, LogLevel.Error, "kiln: " + text);
    }

    /// <summary>Test hook: forget every handler and cap, as if no native half had attached.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _handlers = [];
            Caps.Clear();
            Watched.Clear();
        }
        _sites = 0;
        _tasksStarted = false;
        _loggedLateCaps = false;
        TasksStartedProbe = null;
        Changed = null;
    }
}
