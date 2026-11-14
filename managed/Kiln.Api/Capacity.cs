// SPDX-License-Identifier: MIT
// The shared shape of every capacity (budget) query: native/core/abi/services/kiln_capacity.h (since 0.2.0-dev.28).
// There is no capacity service: the services that own a quota answer with these types.
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>The world a per-world budget belongs to. The values are the core's world kinds; the prediction world
/// has no budget of its own.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum CapacityWorldKind : uint
{
    /// <summary>The server world (a dedicated server's, or a host's).</summary>
    Server = 0,
    /// <summary>The client world.</summary>
    Client = 2,
}

/// <summary>Names one world of this process while it lives: its kind and the session's counter of that kind. A
/// receipt for this process only: never save or send it; ask again after a restart or a new world.</summary>
/// <param name="Kind">Which world.</param>
/// <param name="Generation">The session's counter of that kind; never 0 for a real world.</param>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public readonly record struct CapacityWorldToken(CapacityWorldKind Kind, uint Generation);

/// <summary>Whether a budget is one for the process or one per world.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum CapacityScope : uint
{
    /// <summary>One budget for the whole process.</summary>
    Process = 0,
    /// <summary>The budget of one world (<see cref="CapacityInfo.World"/>).</summary>
    World = 1,
}

/// <summary>Whether a budget can be read now, and if not, why. The name of a value that is not
/// <see cref="Ready"/> is the <see cref="CapacityQueryResult.Reason"/> of the query.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public enum CapacityReadiness : uint
{
    /// <summary>The service has not counted yet (early in the start).</summary>
    NotReady = 0,
    /// <summary>The counts are valid.</summary>
    Ready = 1,
    /// <summary>A per-world budget was asked for and there is no such world now.</summary>
    NoWorld = 2,
    /// <summary>The world the token names has ended.</summary>
    WorldEnded = 3,
    /// <summary>Another loader uses the quota in a way Kiln cannot measure: no free room is promised.</summary>
    UnknownExternalUsage = 4,
    /// <summary>The counts do not add up (more used than there is, or the game's table could not be read).</summary>
    AccountingMismatch = 5,
    /// <summary>No budget of that name.</summary>
    NotFound = 6,
}

/// <summary>One budget: how much of a quota there is and who uses it. The three used counts never overlap and are in
/// the quota's own unit (resource records, clone records, roster entries, program slots, ...).</summary>
/// <param name="Capacity">The quota. 0 while not <see cref="Ready"/>.</param>
/// <param name="GameUsed">What the game itself uses and Kiln cannot have.</param>
/// <param name="KilnUsed">What is in active use through Kiln: mods, Kiln's own services, other loaders' mods that
/// register through Kiln.</param>
/// <param name="Reserved">Occupied for another reason: records kept for a mod that is gone, placeholders, room
/// Kiln's services reserved and do not use yet, and measured use by another loader.</param>
/// <param name="Readiness">Whether the counts are valid.</param>
/// <param name="Expanded">The larger quota is really installed (not merely asked for in the configuration).</param>
/// <param name="Scope">One budget for the process, or one per world.</param>
/// <param name="ExternalUsageUnknown">Another loader's use could not be measured.</param>
/// <param name="World">The world of a per-world budget; null for a process budget and when there is no world.</param>
/// <param name="Revision">The answering service's own counter: it rises whenever what it publishes changes, so two
/// reads with the same revision are the same.</param>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public sealed record CapacityInfo(uint Capacity, uint GameUsed, uint KilnUsed, uint Reserved, CapacityReadiness Readiness,
    bool Expanded, CapacityScope Scope, bool ExternalUsageUnknown, CapacityWorldToken? World, ulong Revision)
{
    /// <summary>True when the counts are valid.</summary>
    public bool Ready => Readiness == CapacityReadiness.Ready;

    /// <summary>The room that is left, or null when the budget is not ready or its counts do not add up.</summary>
    public uint? Available => Ready && (ulong)GameUsed + KilnUsed + Reserved <= Capacity
        ? Capacity - GameUsed - KilnUsed - Reserved
        : null;
}

/// <summary>The answer of a budget query. <see cref="Ok"/> is <see cref="CapacityInfo.Ready"/>: a budget that cannot
/// be read now (no world yet, another loader's use unknown) is a refusal with the readiness name as the reason, not
/// an exception and not a missing capability.</summary>
/// <param name="Ok">True when <paramref name="Budget"/> is ready.</param>
/// <param name="Reason">Null when <paramref name="Ok"/>; else the name of <see cref="CapacityInfo.Readiness"/>
/// (<c>NotReady</c>, <c>NoWorld</c>, <c>WorldEnded</c>, <c>UnknownExternalUsage</c>, <c>AccountingMismatch</c>,
/// <c>NotFound</c>).</param>
/// <param name="Budget">The budget; its counts are 0 when it is not ready.</param>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public sealed record CapacityQueryResult(bool Ok, string? Reason, CapacityInfo Budget) : IKilnResult;

/// <summary>A budget changed: its capacity, readiness or one of its counts. Posted on the bus by the service that
/// owns the quota, after the change is committed (at the owning world's next Post; at start, once mods had the
/// chance to subscribe). A change of <see cref="CapacityInfo.Revision"/> alone posts nothing.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX028")]
public sealed class CapacityChangedEvent : KilnEvent
{
    internal CapacityChangedEvent(string quota, CapacityInfo previous, CapacityInfo current)
    {
        Quota = quota;
        Previous = previous;
        Current = current;
    }

    /// <summary>The quota's name: the capability row of the query that reads it (for example
    /// <c>gamedata.buff_budget</c>).</summary>
    public string Quota { get; }

    /// <summary>The budget before.</summary>
    public CapacityInfo Previous { get; }

    /// <summary>The budget now.</summary>
    public CapacityInfo Current { get; }
}

/// <summary>kiln_capacity.h <c>KilnCapacityInfo</c> and its mapping; for Kiln's own service libraries and the test
/// kit.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KilnCapacityInfo
{
    public uint Size, Capacity, GameUsed, KilnUsed, Reserved, Flags;
    public uint Readiness, WorldKind, Generation, ReservedZero;
    public ulong Revision;

    public const uint FlagReady = 1, FlagProcess = 2, FlagWorld = 4, FlagExpanded = 8, FlagExternalUnknown = 16;
    public const uint NoWorldKind = 0xFFFFFFFF;

    /// <summary>A struct to hand to a native query (its size set).</summary>
    public static unsafe KilnCapacityInfo New() => new() { Size = (uint)sizeof(KilnCapacityInfo) };
}

/// <summary>Native reply to managed result, mechanically (chapter 17, I5).</summary>
internal static class CapacityMapping
{
    /// <summary>The result of a structurally valid native reply (the query returned KILN_OK). A reply that breaks
    /// the contract (READY without VALID, counts over the capacity, both or no scope flag, non-zero counts while not
    /// ready) is reported as <see cref="CapacityReadiness.AccountingMismatch"/> with zero counts, never as room.</summary>
    public static CapacityQueryResult ToResult(in KilnCapacityInfo n)
    {
        var readiness = n.Readiness <= (uint)CapacityReadiness.NotFound ? (CapacityReadiness)n.Readiness : CapacityReadiness.AccountingMismatch;
        var process = (n.Flags & KilnCapacityInfo.FlagProcess) != 0;
        var world = (n.Flags & KilnCapacityInfo.FlagWorld) != 0;
        var readyFlag = (n.Flags & KilnCapacityInfo.FlagReady) != 0;
        var sound = process != world
                    && readyFlag == (readiness == CapacityReadiness.Ready)
                    && (ulong)n.GameUsed + n.KilnUsed + n.Reserved <= n.Capacity
                    && (readiness == CapacityReadiness.Ready || (n.Capacity | n.GameUsed | n.KilnUsed | n.Reserved) == 0)
                    && (!world || n.WorldKind is (uint)CapacityWorldKind.Server or (uint)CapacityWorldKind.Client)
                    && (!process || (n.WorldKind == KilnCapacityInfo.NoWorldKind && n.Generation == 0))
                    && (readiness != CapacityReadiness.Ready || !world || n.Generation != 0);
        if (!sound) readiness = CapacityReadiness.AccountingMismatch;
        var ready = readiness == CapacityReadiness.Ready;
        CapacityWorldToken? token = world && n.Generation != 0 && n.WorldKind is (uint)CapacityWorldKind.Server or (uint)CapacityWorldKind.Client
            ? new CapacityWorldToken((CapacityWorldKind)n.WorldKind, n.Generation)
            : null;
        if (!world) token = null;
        var info = new CapacityInfo(
            ready ? n.Capacity : 0, ready ? n.GameUsed : 0, ready ? n.KilnUsed : 0, ready ? n.Reserved : 0, readiness,
            (n.Flags & KilnCapacityInfo.FlagExpanded) != 0, world && !process ? CapacityScope.World : CapacityScope.Process,
            (n.Flags & KilnCapacityInfo.FlagExternalUnknown) != 0 || readiness == CapacityReadiness.UnknownExternalUsage,
            token, n.Revision);
        return new CapacityQueryResult(ready, ready ? null : readiness.ToString(), info);
    }

    /// <summary>Checks a world token an author passed: a zero generation or an unknown kind is a bad argument.</summary>
    /// <exception cref="ArgumentException">The token is not one a query returned.</exception>
    public static void CheckToken(CapacityWorldToken token, string paramName)
    {
        if (token.Kind is not (CapacityWorldKind.Server or CapacityWorldKind.Client))
            throw new ArgumentException($"world kind {(uint)token.Kind} is not Server or Client", paramName);
        if (token.Generation == 0)
            throw new ArgumentException("a world token with generation 0 names no world; use the token a query returned", paramName);
    }

    /// <summary>The event for a budget that changed in more than its revision, or null.</summary>
    public static CapacityChangedEvent? Changed(string quota, CapacityInfo previous, CapacityInfo current) =>
        previous with { Revision = 0 } == current with { Revision = 0 } ? null : new CapacityChangedEvent(quota, previous, current);
}
