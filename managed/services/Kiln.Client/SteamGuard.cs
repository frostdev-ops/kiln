// SPDX-License-Identifier: MIT
// Kiln.SteamGuard: the achievement guard of the game client. Native side: kiln.steamguard
// (native/core/abi/services/kiln_steamguard.h, native/services/steamguard).
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>One read of the achievement guard's state (<see cref="SteamGuard.Status"/>).</summary>
/// <param name="Available">The kiln.steamguard service is there and answered: a game client with hooks enabled. False
/// on a dedicated server, on an older Kiln, and when the read failed.</param>
/// <param name="HookInstalled">kiln.hooks reports the guard's link in place on the game's achievement call.</param>
/// <param name="TargetVerified">The game's achievement call was found once by its signature, its bytes are the ones the
/// signature names, and a second sign that it is that call holds, now as at boot.</param>
/// <param name="Active">A <see cref="SteamGuard.Latch"/> or at least one <see cref="SteamGuard.TryHold"/> is on.</param>
/// <param name="Dropped">Achievement reports dropped while active, since the game started.</param>
/// <param name="Passed">Achievement reports passed on to Steam while installed and not active, since the game
/// started.</param>
/// <param name="Reason">Why the guard is not protecting, or what keeps it on. Never null.</param>
/// <remarks>Experimental (<c>KILNX002</c>): the achievement guard has not yet run in a real game client. Do not rely on
/// it to protect a player's Steam account until the release notes say the guard-proof check has passed.</remarks>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX002")]
public readonly record struct SteamGuardStatus(bool Available, bool HookInstalled, bool TargetVerified,
                                               bool Active, long Dropped, long Passed, string Reason);

/// <summary>
/// The achievement guard: while it is on, the game client's own call that reports an achievement to Steam is dropped,
/// so a character or world changed by a test or an admin command cannot unlock achievements on the player's Steam
/// account. An unlocked achievement cannot be taken back.
/// </summary>
/// <remarks>
/// <para>Experimental (<c>KILNX002</c>): the guard has not yet run in a real game client. Do not rely on it to protect
/// a player's Steam account until the release notes say the guard-proof check has passed. A project that calls it
/// opts in with <c>NoWarn</c> for <c>KILNX002</c>.</para>
/// <para>Game client only. On a dedicated server there is no Steam user and the guard is never available.</para>
/// <para>Treat the guard as protecting only when <see cref="Latch"/> or <see cref="TryHold"/> returned true and
/// <see cref="Protecting"/> is true. Every doubt (no service, an older table, a target that failed its checks, a hook
/// that did not go in, another hook on the same call, a read that failed) reads as "not protecting".</para>
/// <para>There is no call that turns the guard off. A latch lasts until the game exits; a hold lasts until it is
/// disposed (two holds need two disposals). Another mod cannot end yours.</para>
/// <para>What it guards: the game's one call that sends achievement unlocks and stat changes to Steam (it also carries
/// the game's progress stats). While the guard is on, that call is dropped and counted in <see cref="SteamGuardStatus.Dropped"/>.</para>
/// <para>What it does not cover. It does not undo an achievement already reported. It does not cover the game's own
/// reset of all stats (which removes, never unlocks), other calls a mod or another loader makes into Steam itself, a
/// game build whose call does not match the signature (then it is not available), nor a character or world loaded later
/// in a game where the guard is off: the game reports achievements from saved state, so a changed character can unlock
/// them when it is loaded again. The guard runs in this process only: a joined player's account is protected by that
/// player's own client, and a client without Kiln cannot be protected at all. See native/services/steamguard/README.md
/// for what was read from the game's code and what is inferred.</para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX002")]
[ClientOnly]
[AnyThread]
public static unsafe class SteamGuard
{
    /// <summary>Capability and sigdb feature name.</summary>
    public const string Capability = "steam.achievement_guard";

    /// <summary>The capability row that says whether the guard protects in this session: pending until the first
    /// latch or hold, available while protecting.</summary>
    public const string ProtectingCapability = "steamguard.protecting";

    private const uint FlagTargetVerified = 1u << 0;
    private const uint FlagHookInstalled = 1u << 1;
    private const uint FlagActive = 1u << 2;
    private const int ReasonMax = 192;
    private const int ResultOk = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        public uint Size, Flags, Holds, Reserved;
        public ulong Dropped, Passed;
        public fixed byte Reason[ReasonMax];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStr
    {
        public byte* Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeStatus*, int> Status;
        public delegate* unmanaged[Cdecl]<NativeStr, int> Latch;
        public delegate* unmanaged[Cdecl]<NativeStr, ulong*, int> Hold;
        public delegate* unmanaged[Cdecl]<ulong, int> Release;
    }

    private static Table* _table;

    private static Table* T
    {
        get
        {
            if (_table != null) return _table;
            try
            {
                return _table = Services.Get<Table>("kiln.steamguard", 1);
            }
            catch
            {
                return null;  // no backend (a test host without Kiln): not available
            }
        }
    }

    private static readonly SteamGuardStatus Missing =
        new(false, false, false, false, 0, 0, "kiln.steamguard is not there (a dedicated server, an older Kiln, or hooks off)");

    /// <summary>The guard's state, read now. Any thread; never throws.</summary>
    public static SteamGuardStatus Status
    {
        get
        {
            try
            {
                var t = T;
                if (t == null) return Missing;
                var s = new NativeStatus { Size = (uint)sizeof(NativeStatus) };
                var rc = t->Status(&s);
                if (rc != ResultOk) return Missing with { Reason = $"kiln.steamguard status failed ({rc})" };
                if (s.Size < (uint)sizeof(NativeStatus))
                    return Missing with { Reason = $"kiln.steamguard filled {s.Size} of {sizeof(NativeStatus)} status bytes" };
                var len = 0;
                while (len < ReasonMax && s.Reason[len] != 0) len++;
                var reason = Encoding.UTF8.GetString(s.Reason, len);
                return new SteamGuardStatus(true, (s.Flags & FlagHookInstalled) != 0, (s.Flags & FlagTargetVerified) != 0,
                    (s.Flags & FlagActive) != 0, Clamp(s.Dropped), Clamp(s.Passed), reason);
            }
            catch (Exception e)
            {
                return Missing with { Reason = $"reading kiln.steamguard failed: {e.GetType().Name}" };
            }
        }
    }

    /// <summary>True only when the guard is available, its hook is in, its target is verified and it is on, all from one
    /// read. Any thread; never throws.</summary>
    public static bool Protecting
    {
        get
        {
            var s = Status;
            return s is { Available: true, HookInstalled: true, TargetVerified: true, Active: true };
        }
    }

    /// <summary>Turns the guard on while <paramref name="hold"/> is not disposed. The first latch or hold puts the hook
    /// in. Any thread.</summary>
    /// <param name="reason">What needs the guard, for the log (not empty).</param>
    /// <param name="hold">On true, dispose it to end this hold (other holds and a latch stay on); null on false.</param>
    /// <returns>True when the guard protects now; false when it cannot (see <see cref="Status"/>'s reason). On false
    /// nothing is held.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or empty.</exception>
    public static bool TryHold(string reason, out IDisposable? hold)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        hold = null;
        try
        {
            var t = T;
            if (t == null) return false;
            ulong token = 0;
            int rc;
            var bytes = Encoding.UTF8.GetBytes(reason);
            fixed (byte* p = bytes) rc = t->Hold(new NativeStr { Ptr = p, Len = (nuint)bytes.Length }, &token);
            if (rc != ResultOk || token == 0) return false;
            if (!Protecting)
            {
                t->Release(token);
                return false;
            }
            hold = new HoldToken(token);
            return true;
        }
        catch
        {
            hold = null;
            return false;
        }
    }

    /// <summary>Turns the guard on until the game exits. The first latch or hold puts the hook in. Any thread.</summary>
    /// <param name="reason">What needs the guard, for the log (not empty).</param>
    /// <returns>True when the guard protects now; false when it cannot (see <see cref="Status"/>'s reason).</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or empty.</exception>
    public static bool Latch(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        try
        {
            var t = T;
            if (t == null) return false;
            int rc;
            var bytes = Encoding.UTF8.GetBytes(reason);
            fixed (byte* p = bytes) rc = t->Latch(new NativeStr { Ptr = p, Len = (nuint)bytes.Length });
            return rc == ResultOk && Protecting;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Turns the guard on while the returned hold is not disposed, like <see cref="TryHold"/>, and throws
    /// when it cannot protect. Any thread.</summary>
    /// <param name="reason">What needs the guard, for the log (not empty).</param>
    /// <returns>Dispose it to end this hold (other holds and a latch stay on).</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or empty.</exception>
    /// <exception cref="KilnUnavailableException">The guard cannot protect: <see cref="Capability"/> when the service
    /// is not there or its target is not verified, else <see cref="ProtectingCapability"/> (the hook did not go in, or
    /// another hook is on the game's call). Nothing is held.</exception>
    public static IDisposable Hold(string reason)
    {
        if (TryHold(reason, out var hold) && hold is not null) return hold;
        var s = Status;
        throw new KilnUnavailableException(s is { Available: true, TargetVerified: true } ? ProtectingCapability : Capability, s.Reason);
    }

    private static long Clamp(ulong v) => v > long.MaxValue ? long.MaxValue : (long)v;

    private sealed class HoldToken(ulong token) : IDisposable
    {
        private ulong _token = token;

        public void Dispose()
        {
            var token = Interlocked.Exchange(ref _token, 0);
            if (token == 0) return;
            try
            {
                var t = T;
                if (t != null) t->Release(token);
            }
            catch
            {
                // a release that fails leaves the guard on: the safe side
            }
        }
    }
}
