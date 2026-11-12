// SPDX-License-Identifier: MIT
namespace Kiln.Admin;

internal static partial class Ops
{
    /// <summary>Kiln.Admin's scheduler, set at load (AdminMod). The game writes after a guard request reach the tick
    /// thread through it.</summary>
    internal static Scheduler? TickScheduler { get; set; }

    /// <summary><c>await Ops.TickThread()</c> continues on the tick thread at the next step, through Kiln.Admin's
    /// scheduler, whatever thread the code was on and whatever synchronization context it had: the scheduler calls the
    /// continuation itself. Used right after <c>await SteamGuardRelay.LatchFor(...)</c>, whose answer may come back on a
    /// thread-pool thread (the relay's own wait), so the game writes and the reply that follow run on the tick thread by
    /// construction, one step later.</summary>
    internal static Scheduler.NextTickAwaitable TickThread() =>
        (TickScheduler ?? throw new InvalidOperationException("Kiln.Admin is not loaded")).NextTick();
}
