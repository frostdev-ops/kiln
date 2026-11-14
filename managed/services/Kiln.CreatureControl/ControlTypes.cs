// SPDX-License-Identifier: MIT
// The shapes of Kiln.CreatureControl (19d §5.2). The numbers of the enums are kiln.creature_control's, exactly.
namespace Kiln;

/// <summary>A request id for <see cref="CreatureControl.Acquire"/>: unique and increasing per owner (take one from
/// <see cref="CreatureControl.NewRequestId"/> right before you acquire). The same id with the same request gives the
/// same lease back. Since 0.2.0-dev.28.</summary>
/// <param name="Value">The id; 0 is none.</param>
public readonly record struct ControlRequestId(ulong Value);

/// <summary>A lease on a creature body. 0 is none. Since 0.2.0-dev.28.</summary>
/// <param name="Value">The broker's handle.</param>
public readonly record struct ControlLeaseId(ulong Value);

/// <summary>A controlled body as the broker names it: the server world's epoch, the body generation the broker gave it
/// at its first binding (never 0, never reused) and its entity. Since 0.2.0-dev.28.</summary>
/// <param name="WorldEpoch">The server world's session number.</param>
/// <param name="BodyGeneration">The broker's generation of the body.</param>
/// <param name="Entity">The body's entity.</param>
public readonly record struct ControlBodyKey(ulong WorldEpoch, ulong BodyGeneration, EntityId Entity);

/// <summary>What a lease is for. Priority: Quarantine, Ridden, Vehicle, PetCombat, then the base roles PetFollow,
/// PetStay and EngineGuard (one base lease per body). Since 0.2.0-dev.28.</summary>
public enum ControlRole : uint
{
    /// <summary>No role.</summary>
    None = 0,
    /// <summary>Base role: the body follows by input the holder writes (no SteeringAgent, the broker's AI-off layer).</summary>
    PetFollow = 1,
    /// <summary>The game's AI fights for the holder (SteeringAgent kept, the broker's AI-off layer released).</summary>
    PetCombat = 2,
    /// <summary>A rider drives the body by input (above every base role).</summary>
    Ridden = 3,
    /// <summary>The broker's own: the body is quarantined and will be retired. Never requested.</summary>
    Quarantine = 4,
    /// <summary>A permanent no-AI base role of a vehicle body (held by the native mount service).</summary>
    Vehicle = 5,
    /// <summary>Base role: the body stays where it is, by zero input.</summary>
    PetStay = 6,
    /// <summary>Base role: the game's own guard keeps the body (SteeringAgent kept, AI on).</summary>
    EngineGuard = 7,
}

/// <summary>Where a lease is. Only <see cref="Active"/> with <see cref="ControlLeaseSnapshot.WinningLease"/> equal to
/// the lease grants the right to write the body's input. Since 0.2.0-dev.28.</summary>
public enum ControlLeaseState : uint
{
    /// <summary>Admitted; the body is changing hands. Never permission.</summary>
    Pending = 0,
    /// <summary>Granted: the body's component transition is verified.</summary>
    Active = 1,
    /// <summary>Kept while a higher lease holds the body; it may come back (with a new transition sequence).</summary>
    Preempted = 2,
    /// <summary>Released by its holder (terminal).</summary>
    Released = 3,
    /// <summary>Ended by a failure, the body's or the world's end (terminal).</summary>
    Failed = 4,
}

/// <summary>Why a lease is where it is (<see cref="ControlLeaseSnapshot.Reason"/> says more). Since 0.2.0-dev.28.</summary>
public enum ControlResultCode : uint
{
    /// <summary>Nothing yet.</summary>
    None = 0,
    /// <summary>The lease is Active.</summary>
    Granted = 1,
    /// <summary>Released after it was Active.</summary>
    Released = 2,
    /// <summary>A higher lease holds the body.</summary>
    Preempted = 3,
    /// <summary>Released before it was Active.</summary>
    Canceled = 4,
    /// <summary>The body or the broker is taken (another owner's lease, a ceiling, a full queue).</summary>
    Busy = 5,
    /// <summary>The body's action did not end within 5 s, or a component change did not verify within 0.5 s.</summary>
    TimedOut = 6,
    /// <summary>The body is gone (or the key is stale).</summary>
    BodyGone = 7,
    /// <summary>The server world ended.</summary>
    WorldEnded = 8,
    /// <summary><c>creature_control</c> is off, or the role is not served.</summary>
    CapabilityOff = 9,
    /// <summary>The holder unloaded.</summary>
    OwnerUnloaded = 10,
    /// <summary>The body could not be restored.</summary>
    RestoreFailed = 11,
    /// <summary>The body was retired.</summary>
    Retired = 12,
    /// <summary>No such lease (unknown, or terminal for more than 10 minutes), or an expired request id.</summary>
    ExpiredHandle = 13,
    /// <summary>An AI-on role could not turn the AI on: another owner holds it off.</summary>
    AiInhibited = 14,
}

/// <summary>A lease as the broker published it. A snapshot is history: inspect again before you act on it. Since
/// 0.2.0-dev.28.</summary>
/// <param name="Body">The body.</param>
/// <param name="Lease">This lease.</param>
/// <param name="WinningLease">The body's Active lease now (none while it changes hands).</param>
/// <param name="Request">The request id that admitted it.</param>
/// <param name="RequestedRole">The role it asked for.</param>
/// <param name="EffectiveRole">The role that holds the body now (None while a change is not verified).</param>
/// <param name="State">Where the lease is.</param>
/// <param name="Result">Why.</param>
/// <param name="TransitionSequence">The body's sequence when this lease last became Active (0 before).</param>
/// <param name="NoticeSequence">The owner's notice counter when this was published.</param>
/// <param name="ChangedMask">What the broker changed on the body: 1 SteeringAgent removed, 2 its AI-off layer, 4 a
/// vehicle's behaviour removed.</param>
/// <param name="Reason">The reason in words (starts with a stable word: "Granted", "Busy", "Quiescence", ...).</param>
public sealed record ControlLeaseSnapshot(ControlBodyKey Body, ControlLeaseId Lease,
    ControlLeaseId WinningLease, ControlRequestId Request, ControlRole RequestedRole,
    ControlRole EffectiveRole, ControlLeaseState State, ControlResultCode Result,
    ulong TransitionSequence, ulong NoticeSequence, uint ChangedMask, string Reason)
{
    /// <summary>True while this lease is the body's Active winner: the holder may write the body's input now.</summary>
    public bool CanDrive => State == ControlLeaseState.Active && WinningLease == Lease && Lease.Value != 0;

    /// <summary>True for Released and Failed.</summary>
    public bool IsTerminal => State is ControlLeaseState.Released or ControlLeaseState.Failed;
}

/// <summary>The answer of <see cref="CreatureControl.Acquire"/>: an admission (Pending, never permission) or a
/// refusal with its reason. Since 0.2.0-dev.28.</summary>
/// <param name="Ok">True when the request was admitted.</param>
/// <param name="Reason">Why not; null when admitted.</param>
/// <param name="Lease">The lease (none for a refusal).</param>
/// <param name="Initial">The lease's first state, or the refusal as the broker stated it.</param>
public sealed record ControlRequestResult(bool Ok, string? Reason, ControlLeaseId Lease, ControlLeaseSnapshot? Initial) : IKilnResult;

/// <summary>The answer of <see cref="CreatureControl.WaitActive"/> and <see cref="CreatureControl.Release"/>. Since
/// 0.2.0-dev.28.</summary>
/// <param name="Ok">True when the lease became Active (WaitActive) or ended Released (Release).</param>
/// <param name="Reason">Why not; null when Ok.</param>
/// <param name="State">The lease when the answer was made; null when the broker has no such lease.</param>
public sealed record ControlLeaseResult(bool Ok, string? Reason, ControlLeaseSnapshot? State) : IKilnResult;

/// <summary>A lease Kiln.CreatureControl watches changed: one acquired through <see cref="CreatureControl.Acquire"/>,
/// or named to <see cref="CreatureControl.WaitActive"/>. Posted on the tick thread at the start of the step in which
/// Kiln saw the change (the broker moves at the server world's Pre), once per change, in order. Observe only: the
/// broker made the change, not a handler. Since 0.2.0-dev.28.</summary>
[ServerOnly]
public sealed class ControlChangedEvent : KilnEvent
{
    internal ControlChangedEvent(ControlLeaseSnapshot? before, ControlLeaseSnapshot after)
    {
        Before = before;
        After = after;
    }

    /// <summary>The lease.</summary>
    public ControlLeaseId Lease => After.Lease;

    /// <summary>What Kiln saw of the lease before (null: the first time it saw it).</summary>
    public ControlLeaseSnapshot? Before { get; }

    /// <summary>The lease now.</summary>
    public ControlLeaseSnapshot After { get; }

    /// <summary>Why it changed: <see cref="ControlLeaseSnapshot.Result"/> of <see cref="After"/>.</summary>
    public ControlResultCode Cause => After.Result;

    public override string ToString() =>
        $"lease {Lease.Value}: {Before?.State.ToString() ?? "-"} -> {After.State} ({Cause}) seq {After.TransitionSequence}: {After.Reason}";
}
