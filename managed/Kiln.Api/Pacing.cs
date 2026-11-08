// SPDX-License-Identifier: MIT
// Pacing: where the engine waits, and how many worker threads it starts (Enshwick's `pacing`: loop_wait, server_wait,
// task_workers). A mod uses them to make the game use less CPU: sleep through a wait the game would poll, stretch the
// server loop's wait, or cap the task pools the engine sizes from the CPUs it sees.
//
// This file is the managed half: the registry that merges what mods ask for, in the order Enshwick's loader does (handlers
// in load order, each seeing the last one's answer; the lowest worker cap wins). The native half finds the places in the
// running build, hooks them and calls the Dispatch*/Take* members below; until it does, Sites says None and nothing runs.
// Usage: docs/kiln/modding/lifecycle.md.
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

    /// <summary>This build has the wait the handler is for (it will be called). False until the game's wait is hooked, or
    /// for good on a build that lacks it. Any thread.</summary>
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
/// <para>Handlers run in the mods' load order, each on the last one's answer. A handler that throws three times in a row is
/// removed (logged); a call that works resets the count. The worker caps apply when the task system starts, which only a
/// dedicated server does after mods load: <see cref="CapTaskWorkers"/> returns false on a client, once the pools have
/// started, and when the build lacks the place. The lowest cap of each group wins.</para>
/// <para>Whether a place exists is a fact about the game build: <see cref="Sites"/> says which Kiln found and hooked. A build
/// that lacks one is not an error, the handler is simply never called.</para>
/// </remarks>
public static class Pacing
{
    /// <summary>The server loop's wait with nobody online (nanoseconds).</summary>
    public const long ServerIdleWaitNs = 40_000_000;

    /// <summary>The server loop's wait with players online.</summary>
    public const long ServerOnlineWaitNs = 1_000_000;

    /// <summary>The server loop's wait after a pass longer than 14 ms.</summary>
    public const long ServerSlowWaitNs = 100_000;

    private const int FaultLimit = 3;

    private static readonly object Lock = new();
    private static PacingHandler[] _handlers = [];
    private static readonly List<(TaskWorkerCaps Caps, ModContext? Owner)> Caps = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static volatile int _sites;
    private static volatile bool _tasksStarted;
    private static long _sequence;

    /// <summary>Which waits this build lets mods pace (what the game's code lets Kiln hook). Any thread.</summary>
    public static PacingSites Sites => (PacingSites)_sites;

    /// <summary>At least one wait can be paced on this build.</summary>
    public static bool Available => _sites != 0;

    /// <summary>The task system has started its worker pools: caps come too late.</summary>
    public static bool TaskWorkersStarted => _tasksStarted;

    /// <summary>The cap each task group starts with, as the mods asked for them (0: left to the game).</summary>
    public static TaskWorkerCaps EffectiveTaskWorkerCaps
    {
        get
        {
            lock (Lock) return Merged();
        }
    }

    /// <summary>Handlers registered now, per site (diagnostics).</summary>
    public static int HandlerCount(PacingSites site) => _handlers.Count(h => h.Site == site);

    /// <summary>Runs <paramref name="handler"/> before every wait of the engine's event loop on its handles, on the thread
    /// that waits: the main loop, the server's game loop and its session, and on a client the message wait too. A handler
    /// that sleeps must never sleep past <see cref="LoopWait.LeftNanoseconds"/> and must leave the waiting to the game, so the
    /// game still sees every event. Tick thread; owned by the calling mod.</summary>
    [TickThreadOnly]
    public static PacingHandler OnLoopWait(LoopWaitHandler handler) => Add(PacingSites.LoopWait | PacingSites.MessageWait, handler);

    /// <summary>Runs <paramref name="handler"/> where the server's game loop has picked its wait between two passes
    /// (<see cref="ServerIdleWaitNs"/>, <see cref="ServerOnlineWaitNs"/> or <see cref="ServerSlowWaitNs"/>); it may change it.
    /// A longer wait with players online means fewer passes between the simulation steps and up to that much more latency for
    /// what arrives between them. On the server loop's thread, once a pass. Tick thread; owned by the calling mod.</summary>
    [TickThreadOnly]
    public static PacingHandler OnServerWait(ServerWaitHandler handler) => Add(PacingSites.ServerWait, handler);

    /// <summary>Caps the worker threads each task group starts with, for the calling mod. The engine sizes the pools from
    /// the logical CPUs it sees (under Wine, the host's, whatever a container allows). Returns whether the cap will apply:
    /// false for caps of 0, on a client, once the pools have started and on a build without the place. A mod's cap is
    /// withdrawn when it unloads (the pools it limited stay as they started). Tick thread.</summary>
    [TickThreadOnly]
    public static bool CapTaskWorkers(TaskWorkerCaps caps)
    {
        Game.AssertTickThread();
        if (caps.IsEmpty) return false;
        var owner = ModContext.CurrentMod;
        if (owner is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        lock (Lock)
        {
            if (_tasksStarted || (Sites & PacingSites.TaskWorkers) == 0) return false;
            Caps.RemoveAll(c => ReferenceEquals(c.Owner, owner) && owner is not null);
            Caps.Add((caps, owner));
            Watch(owner);
        }
        return true;
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

    /// <summary>Handlers or caps were added or removed: the native half arms its hooks only for what mods use.</summary>
    internal static event Action? Changed;

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
        foreach (var h in all)
        {
            if (h.Site != PacingSites.ServerWait || h.Removed) continue;
            var wait = waitNanoseconds;
            if (Call(h, RunServerWait, ref wait, default)) waitNanoseconds = wait;
        }
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
            h.Faults = 0;
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
        var n = ++h.Faults;
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
        Changed = null;
    }
}
