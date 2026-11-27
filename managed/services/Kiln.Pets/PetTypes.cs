// SPDX-License-Identifier: MIT
// The shapes of a pet record (19d §4.1, §4.2, §4.7, §5.3): two axes (life, presence) and a transition, the requested
// order, revisions and the save receipt; requests and results; the change every event carries.
namespace Kiln;

/// <summary>A pet's id: random, kept in its owner's record for ever, never an entity id. A pet's body is a different thing
/// (<see cref="PetSnapshot.Body"/>): it comes and goes, the id stays. Since 0.2.0-dev.32.</summary>
/// <param name="Value">The id; <see cref="Guid.Empty"/> is none.</param>
public readonly record struct PetId(Guid Value)
{
    /// <summary>No pet.</summary>
    public static PetId None => default;
    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Value == Guid.Empty;
    /// <summary>The id as 32 hex digits (no dashes).</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>Whether a pet is alive, down or gone. Since 0.2.0-dev.32.</summary>
public enum PetLife
{
    /// <summary>It can be summoned and fights.</summary>
    Ready = 0,
    /// <summary>It went down: no body, no health; revive (or release) is the way back.</summary>
    Downed = 1,
    /// <summary>Released: the record is a tombstone until a save keeps that, then it goes.</summary>
    Released = 2,
}

/// <summary>Where a pet is in the world. Since 0.2.0-dev.32.</summary>
public enum PetPresence
{
    /// <summary>Nowhere: no body, no anchor.</summary>
    Absent = 0,
    /// <summary>A body in the world (<see cref="PetSnapshot.Body"/>).</summary>
    Live = 1,
    /// <summary>A downed pet's bodyless anchor: a spot its owner sees and revives it at. No entity, no collision, never a target.</summary>
    RecoveryAnchor = 2,
    /// <summary>Its body is being removed (short; protected).</summary>
    Retiring = 3,
}

/// <summary>What is happening to a pet right now. Requests that arrive meanwhile are <c>Busy</c>. Since 0.2.0-dev.32.</summary>
public enum PetTransition
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>A body is being made.</summary>
    Spawning = 1,
    /// <summary>It is going down (its body being retired).</summary>
    Downing = 2,
    /// <summary>Its body is being dismissed.</summary>
    Dismissing = 3,
    /// <summary>A revive channel runs, or its new body is being made.</summary>
    Reviving = 4,
    /// <summary>It is being released.</summary>
    Releasing = 5,
}

/// <summary>What a pet's owner asked of it. Since 0.2.0-dev.32.</summary>
public enum PetOrder
{
    /// <summary>Follow its owner (the default after every summon and revive).</summary>
    Follow = 0,
    /// <summary>Stay where it is.</summary>
    Stay = 1,
    /// <summary>Attack a target its rule allows.</summary>
    Attack = 2,
}

/// <summary>The save that holds a pet's state: what was serialized and committed. A pet's state is durable when a committed
/// save holds its <see cref="PetSnapshot.StateRevision"/> (<see cref="PetSnapshot.DurableRevision"/>); until then a crash
/// loses the change. Since 0.2.0-dev.32.</summary>
/// <seealso cref="PetRecordCommittedEvent"/>
public sealed record PetCommit
{
    /// <summary>The save session it belongs to.</summary>
    public ulong WorldEpoch { get; init; }
    /// <summary>The world file's revision (the mod data generation).</summary>
    public uint WorldRevision { get; init; }
    /// <summary>The world file's time (Unix seconds); 0 when unknown.</summary>
    public uint WorldUnix { get; init; }
    /// <summary>The pet's <see cref="PetSnapshot.StateRevision"/> in the committed bytes.</summary>
    public ulong StateRevision { get; init; }
    /// <summary>The sha256 of the committed record bundle (hex).</summary>
    public string BundleHash { get; init; } = "";
}

/// <summary>
/// A pet as it is now: an immutable copy, never updated in place. Read it again (<see cref="Pets.Get"/>, <see cref="Pets.Of"/>) or
/// take the one an answer or an event carries. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>A pet has three independent facts: <see cref="Life"/> (alive, down or gone), <see cref="Presence"/> (where it is: no
/// body, a live body, a recovery anchor) and <see cref="Transition"/> (what is happening to it right now). A request made
/// while a transition runs is <c>Busy</c>. <see cref="Body"/> is an entity only while the pet is <see cref="PetPresence.Live"/> or
/// <see cref="PetPresence.Retiring"/>; never keep it across steps, ask again. Server side only: a client reads
/// <see cref="PetViewEntry"/> through <see cref="PetsClient"/>.</remarks>
/// <seealso cref="Pets.Of"/>
/// <seealso cref="PetChange"/>
public sealed record PetSnapshot
{
    /// <summary>Its id.</summary>
    public PetId Id { get; init; }
    /// <summary>Its owner.</summary>
    public CharacterKey Owner { get; init; }
    /// <summary>Its definition's id.</summary>
    public string Definition { get; init; } = "";
    /// <summary>The name its owner reads.</summary>
    public string Label { get; init; } = "";
    /// <summary>The server world session its body belongs to (0: no body).</summary>
    public ulong WorldEpoch { get; init; }
    /// <summary>The body's generation (the broker's; 0: no body). A new body never has an old generation.</summary>
    public ulong BodyGeneration { get; init; }
    /// <summary>The body while <see cref="Presence"/> is Live or Retiring; none otherwise.</summary>
    public EntityRef Body { get; init; }
    /// <summary>Alive, down or released.</summary>
    public PetLife Life { get; init; }
    /// <summary>Where it is.</summary>
    public PetPresence Presence { get; init; }
    /// <summary>What is happening to it.</summary>
    public PetTransition Transition { get; init; }
    /// <summary>The owner's wish that it be out: set by Summon, cleared by Dismiss, kept across logout (the next login summons it).</summary>
    public bool WantsSummoned { get; init; }
    /// <summary>The order it was given.</summary>
    public PetOrder RequestedOrder { get; init; }
    /// <summary>The target of an attack order; none otherwise (never saved).</summary>
    public EntityRef RequestedTarget { get; init; }
    /// <summary>The recovery anchor's spot while it is shown (and the spot kept for it while Downed and Absent).</summary>
    public WorldPosition? RecoveryAnchor { get; init; }
    /// <summary>Its level.</summary>
    public int Level { get; init; }
    /// <summary>Its health (0 while down; while it has no body, its kept share of <see cref="HealthMax"/>).</summary>
    public int Health { get; init; }
    /// <summary>Its maximum health (the last body's).</summary>
    public int HealthMax { get; init; }
    /// <summary>Increases once per change of its state (not per event).</summary>
    public ulong StateRevision { get; init; }
    /// <summary>The highest <see cref="StateRevision"/> a committed save holds.</summary>
    public ulong DurableRevision { get; init; }
    /// <summary>The save that holds <see cref="DurableRevision"/>; null before the first.</summary>
    public PetCommit? LastCommit { get; init; }
    /// <summary>The revive channel running; null when none.</summary>
    public Guid? ReviveOperation { get; init; }
    /// <summary>Seconds of the channel done.</summary>
    public double ReviveElapsed { get; init; }
    /// <summary>Seconds the channel takes.</summary>
    public double ReviveDuration { get; init; }
}

/// <summary>A request that changes a pet: its operation id makes a retry the same request (its first answer is given back
/// for 10 minutes), and an expected revision refuses a request made on a stale view. Since 0.2.0-dev.32.</summary>
/// <remarks>Make a new request for every intended action (<see cref="New"/>). To retry after a lost answer, send the same request
/// again: nothing runs twice. Never reuse an operation id for another action: that is <c>RequestConflict</c>.</remarks>
/// <example><code>
/// PetRequest request = PetRequest.New();                   // a fresh operation id
/// PetRequest guarded = PetRequest.New(pet.StateRevision);  // refused (StaleRevision) if the pet changed since this snapshot
/// </code></example>
/// <seealso cref="Pets.Summon"/>
/// <param name="OperationId">Unique per request; the same id with another request is <c>RequestConflict</c>.</param>
/// <param name="ExpectedStateRevision">0: any; otherwise the pet's <see cref="PetSnapshot.StateRevision"/> the request was
/// made on (<c>StaleRevision</c> when it moved on).</param>
public sealed record PetRequest(Guid OperationId, ulong ExpectedStateRevision = 0)
{
    /// <summary>A new request with a fresh operation id.</summary>
    /// <param name="expectedStateRevision">0 to accept any state; otherwise the <see cref="PetSnapshot.StateRevision"/> the caller
    /// saw (the request is <c>StaleRevision</c> when the pet moved on).</param>
    /// <returns>A request with a new operation id.</returns>
    public static PetRequest New(ulong expectedStateRevision = 0) => new(Guid.NewGuid(), expectedStateRevision);
}

/// <summary>The answer to a request: the outcome and the pet after it. A refusal is a result, not an exception; check
/// <see cref="Ok"/> first. Since 0.2.0-dev.32.</summary>
/// <seealso cref="PetRequest"/>
/// <param name="Ok">True when the request did what it asked (or found it done: an idempotent repeat).</param>
/// <param name="Reason">Why not: <c>Busy</c>, <c>StaleRevision</c>, <c>RequestConflict</c>, <c>NotOwner</c>,
/// <c>AlreadyReleased</c>, <c>Capacity</c>, <c>NoSafeGround</c>, <c>BodyNotReady</c>, <c>TargetProtected</c>,
/// <c>RecoveryNotSummoned</c>, <c>ReviveFailedCostCommitted</c>, ...</param>
/// <param name="Pet">The pet after it (also after a refusal, when Kiln knows it).</param>
/// <param name="OperationId">The request's operation id.</param>
/// <param name="Costs">The receipts (Kiln.Items' <see cref="CostReceipt"/>) of costs that were taken or may have been
/// (committed or uncertain), also on a refusal that came after a cost was taken.</param>
public sealed record PetResult(bool Ok, string? Reason, PetSnapshot? Pet, Guid OperationId, IReadOnlyList<CostReceipt> Costs) : IKilnResult;

/// <summary>Why a pet changed. Since 0.2.0-dev.32.</summary>
public enum PetChangeCause
{
    /// <summary>A request (Summon, Dismiss, Command, Revive, Release, Grant).</summary>
    Request = 0,
    /// <summary>Its body was lost (destroyed, unloaded, dead by a path the guard does not cover).</summary>
    BodyLost = 1,
    /// <summary>A lethal hit, fall or health loss, caught by the native guard.</summary>
    NativeLethal = 2,
    /// <summary>Kiln could no longer protect it (a capability went away): its body was retired.</summary>
    ProtectionLost = 3,
    /// <summary>Its owner left the world.</summary>
    Logout = 4,
    /// <summary>The world ended or Kiln shut down.</summary>
    WorldEnd = 5,
    /// <summary>Its owner came back.</summary>
    Login = 6,
    /// <summary>Its body's stats changed (health, maximum).</summary>
    Stats = 7,
    /// <summary>The control broker ended or moved its lease.</summary>
    Control = 8,
    /// <summary>A revive channel's own course (completed, canceled by movement, range or damage).</summary>
    Channel = 9,
    /// <summary>The mod that defined the pet unloaded: its body was retired (the record and the owner's wish are kept).</summary>
    Unload = 10,
}

/// <summary>One change of a pet: before and after, the request behind it and when it happened. Every
/// <see cref="PetChangeEvent"/> carries one. Since 0.2.0-dev.32.</summary>
public sealed record PetChange
{
    /// <summary>Unique per change.</summary>
    public Guid EventId { get; init; }
    /// <summary>The request's operation id; empty when no request caused it.</summary>
    public Guid OperationId { get; init; }
    /// <summary>The server step it was observed at (a later delivery keeps it).</summary>
    public ulong ObservedStep { get; init; }
    /// <summary>The pet before.</summary>
    public PetSnapshot Before { get; init; } = new();
    /// <summary>The pet after.</summary>
    public PetSnapshot After { get; init; } = new();
    /// <summary>Why.</summary>
    public PetChangeCause Cause { get; init; }
}
