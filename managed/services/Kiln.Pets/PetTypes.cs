// SPDX-License-Identifier: MIT
// The shapes of a pet record (19d §4.1, §4.2, §4.7, §5.3): two axes (life, presence) and a transition, the requested
// order, revisions and the save receipt; requests and results; the change every event carries.
namespace Kiln;

/// <summary>A pet's id: random, kept in its owner's record for ever, never an entity id. Since 0.2.0-dev.32.</summary>
/// <param name="Value">The id; <see cref="Guid.Empty"/> is none.</param>
public readonly record struct PetId(Guid Value)
{
    /// <summary>No pet.</summary>
    public static PetId None => default;
    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Value == Guid.Empty;
    /// <summary>The id as 32 hex digits.</summary>
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

/// <summary>The save that holds a pet's state: what was serialized and committed. Since 0.2.0-dev.32.</summary>
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

/// <summary>A pet as it is now. Since 0.2.0-dev.32.</summary>
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
    /// <summary>The request id of the revive channel running (the one <see cref="Pets.CancelRevive"/> names); null when
    /// none.</summary>
    public Guid? ReviveRequest { get; init; }
    /// <summary>How much of the channel is done (simulation time).</summary>
    public TimeSpan ReviveElapsed { get; init; }
    /// <summary>How long the channel takes.</summary>
    public TimeSpan ReviveDuration { get; init; }
}

/// <summary>A request that changes a pet, in the one request shape of Kiln.Pets and Kiln.Taming (<see cref="TamingRequest"/>):
/// its request id makes a retry the same request (its first answer is given back for 10 minutes), and what it expects of
/// the view it was made on refuses it when that moved on. Since 0.2.0-dev.32.</summary>
/// <param name="RequestId">New for every intended request, the same for a network retry of it; the same id with another
/// request is <c>RequestConflict</c>. The answer (<see cref="PetResult.RequestId"/>) and the change it makes
/// (<see cref="PetChange.RequestId"/>) carry it.</param>
/// <param name="ExpectedStateRevision">0: any; otherwise the pet's <see cref="PetSnapshot.StateRevision"/> the request was
/// made on (<c>StaleRevision</c> when it moved on).</param>
public sealed record PetRequest(Guid RequestId, ulong ExpectedStateRevision = 0)
{
    /// <summary>A new request with a fresh request id.</summary>
    public static PetRequest New(ulong expectedStateRevision = 0) => new(Guid.NewGuid(), expectedStateRevision);
}

/// <summary>The answer to a request. Since 0.2.0-dev.32.</summary>
/// <param name="Ok">True when the request did what it asked (or found it done: an idempotent repeat).</param>
/// <param name="Reason">Why not, in words for a log: its first word is <see cref="Refusal"/> (<c>Busy</c>,
/// <c>StaleRevision</c>, <c>RequestConflict</c>, <c>NotOwner</c>, <c>AlreadyReleased</c>, <c>Capacity</c>,
/// <c>NoSafeGround</c>, <c>BodyNotReady</c>, <c>TargetProtected</c>, <c>RecoveryNotSummoned</c>,
/// <c>ReviveFailedCostCommitted</c>, ...). Decide by <see cref="Refusal"/>, never by the text.</param>
/// <param name="Pet">The pet after it (also after a refusal, when Kiln knows it).</param>
/// <param name="RequestId">The request's id (<see cref="PetRequest.RequestId"/>).</param>
/// <param name="Costs">The receipts (Kiln.Items' <see cref="CostReceipt"/>) of costs that were taken or may have been
/// (committed or uncertain), also on a refusal that came after a cost was taken.</param>
public sealed record PetResult(bool Ok, string? Reason, PetSnapshot? Pet, Guid RequestId, IReadOnlyList<CostReceipt> Costs) : IKilnResult
{
    /// <summary>Why not, as a value: <see cref="PetRefusal.None"/> when <see cref="Ok"/>, else the first word of
    /// <see cref="Reason"/> (<see cref="PetRefusal.Other"/> for a word this Kiln has no member for).</summary>
    public PetRefusal Refusal => RefusalWords.Of(Ok, Reason, PetRefusal.None, PetRefusal.Other);
}

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

/// <summary>One change of a pet: before and after, the request behind it and when it happened. Since 0.2.0-dev.32.</summary>
public sealed record PetChange
{
    /// <summary>Unique per change.</summary>
    public Guid EventId { get; init; }
    /// <summary>The id of the request behind it (<see cref="PetRequest.RequestId"/>); empty when no request caused it.</summary>
    public Guid RequestId { get; init; }
    /// <summary>The server step it was observed at (a later delivery keeps it).</summary>
    public ulong ObservedStep { get; init; }
    /// <summary>The pet before.</summary>
    public PetSnapshot Before { get; init; } = new();
    /// <summary>The pet after.</summary>
    public PetSnapshot After { get; init; } = new();
    /// <summary>Why.</summary>
    public PetChangeCause Cause { get; init; }
}
