// SPDX-License-Identifier: MIT
using System.Diagnostics;

namespace Kiln.Host;

/// <summary>
/// The frame a task's tick hop runs in on the server tick: <c>ToTick</c>, <c>OnTick</c> and <c>PostToTick</c> work, and
/// tick continuations of a task. The core queues the hops and dispatches them before the Pre subscribers, in order per
/// mod, mods in turn, within its budgets (64 hops and 2 ms per mod, 4 ms in all per step; the rest waits a step). This
/// dispatcher takes each dispatch for its mod: the hop runs as that mod's tick work and its time is booked to the mod.
/// It is separate from <c>Scheduler.Post</c> / <c>NextTick</c>: that queue, its drain in the mod's Pre and its limit
/// are untouched, and a hop never goes through it.
/// </summary>
/// <remarks>Tick thread only (the server world's; client-world hops run through it on the client world's step, see
/// <see cref="RunClient"/>). A hop the core flushes cancelled from another thread (a world's end)
/// runs no mod code on the tick and never comes here. The task's own scope (current mod, synchronization context)
/// is installed and restored by the task around the hop; a fault in the hop faults that task and is never a handler
/// fault of the mod.</remarks>
internal sealed class TaskTickDispatcher(ModManager mods)
{
    /// <summary>Hops dispatched through here since boot (diagnostics, tests).</summary>
    public long Hops { get; private set; }

    public void Run(ulong owner, Action hop)
    {
        Hops++;
        var start = Stopwatch.GetTimestamp();
        try
        {
            hop();  // never throws: the task catches its work's exceptions
        }
        finally
        {
            mods.AccountTaskHop(owner, Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>Client-world hops run while a hosted server world drives the mods (main-thread work, not their tick).</summary>
    public long ClientHops { get; private set; }

    /// <summary>A hosting client's client-world hop (#82): main-thread work beside the mods' tick on the server thread. Its
    /// time is not the mods' Pre (that runs on another thread); it is counted here only. On a joined client the client
    /// world drives the mods and its hops go through <see cref="Run"/>.</summary>
    public void RunClient(ulong owner, Action hop)
    {
        _ = owner;
        ClientHops++;
        hop();  // never throws: the task catches its work's exceptions
    }
}

/// <summary>The frame a client-world hop runs in on a client (#82), apart from <see cref="HostRuntime"/> so its one rule is
/// testable: with no hosted server world the client world drives the mods, and the hop is their tick work, under the drive
/// lock with the thread marked as the driver (<c>Game.IsTickThread</c> true, as in a joined client's Context.Tick). While a
/// hosted server world drives them on its own thread, the hop is main-thread work like a Client.Tick handler and never
/// takes the drive lock: that would make the game's main thread wait for the server thread's whole step.</summary>
internal static class ClientHopFrame
{
    internal static void Run(bool serverWorldLive, object drive, ref int driver, Action asMods, Action asClient)
    {
        if (serverWorldLive)
        {
            asClient();
            return;
        }
        lock (drive)
        {
            var previous = Volatile.Read(ref driver);
            Volatile.Write(ref driver, Environment.CurrentManagedThreadId);
            try
            {
                asMods();
            }
            finally
            {
                Volatile.Write(ref driver, previous);
            }
        }
    }
}
