// SPDX-License-Identifier: MIT
// Kiln.Pets' events (19d §4.2, §5.3). Every change event carries a PetChange (before, after, the operation, the step).
// After-events of one logical change share its StateRevision; they are posted on the server tick thread, after the
// change settled and outside every lock, so a handler may call Pets (its request runs at the next step).
namespace Kiln;

/// <summary>The base of every pet change event: subscribe to it for all of them. Server tick thread. Since 0.2.0-dev.32.</summary>
public abstract class PetChangeEvent : KilnEvent
{
    private protected PetChangeEvent(PetChange change) => Change = change;
    /// <summary>The change: before, after, the request, the step.</summary>
    public PetChange Change { get; }
    /// <summary>The pet.</summary>
    public PetId Pet => Change.After.Id;
    /// <summary>Its owner.</summary>
    public CharacterKey Owner => Change.After.Owner;
}

/// <summary>A pet entered or left a transition (Spawning, Downing, Dismissing, Reviving, Releasing). Since 0.2.0-dev.32.</summary>
public sealed class PetTransitionChangedEvent : PetChangeEvent { internal PetTransitionChangedEvent(PetChange c) : base(c) { } }
/// <summary>A character was given a new pet (<see cref="Pets.Grant"/>). Since 0.2.0-dev.32.</summary>
public sealed class PetGrantedEvent : PetChangeEvent { internal PetGrantedEvent(PetChange c) : base(c) { } }
/// <summary>A pet was summoned: a body (Ready) or its recovery anchor next to its owner (Downed). Since 0.2.0-dev.32.</summary>
public sealed class PetSummonedEvent : PetChangeEvent { internal PetSummonedEvent(PetChange c) : base(c) { } }
/// <summary>A pet was dismissed. Since 0.2.0-dev.32.</summary>
public sealed class PetDismissedEvent : PetChangeEvent { internal PetDismissedEvent(PetChange c) : base(c) { } }
/// <summary>A pet's order or target changed. Since 0.2.0-dev.32.</summary>
public sealed class PetCommandChangedEvent : PetChangeEvent { internal PetCommandChangedEvent(PetChange c) : base(c) { } }
/// <summary>A downed pet's recovery anchor appeared, moved or was hidden. Since 0.2.0-dev.32.</summary>
public sealed class PetRecoveryAnchorChangedEvent : PetChangeEvent { internal PetRecoveryAnchorChangedEvent(PetChange c) : base(c) { } }
/// <summary>A pet's body was removed from the world (dismiss, logout, release, down). Since 0.2.0-dev.32.</summary>
public sealed class PetBodyRetiredEvent : PetChangeEvent { internal PetBodyRetiredEvent(PetChange c) : base(c) { } }
/// <summary>A pet's body was gone without Kiln retiring it; the pet is down (never rebuilt by itself). Since 0.2.0-dev.32.</summary>
public sealed class PetBodyLostEvent : PetChangeEvent { internal PetBodyLostEvent(PetChange c) : base(c) { } }
/// <summary>A pet went down: its combat body is gone (the event comes after that), what it became is the definition's
/// (or <see cref="PetDowningEvent"/>'s) choice. Since 0.2.0-dev.32.</summary>
public sealed class PetDownedEvent : PetChangeEvent { internal PetDownedEvent(PetChange c) : base(c) { } }
/// <summary>A revive channel started. Since 0.2.0-dev.32.</summary>
public sealed class PetReviveStartedEvent : PetChangeEvent { internal PetReviveStartedEvent(PetChange c) : base(c) { } }
/// <summary>A revive channel was canceled (nothing was taken unless its receipt says so). Since 0.2.0-dev.32.</summary>
public sealed class PetReviveCanceledEvent : PetChangeEvent { internal PetReviveCanceledEvent(PetChange c) : base(c) { } }
/// <summary>A pet was revived: a new protected body. Since 0.2.0-dev.32.</summary>
public sealed class PetRevivedEvent : PetChangeEvent { internal PetRevivedEvent(PetChange c) : base(c) { } }
/// <summary>A pet was released: its record is a tombstone. Since 0.2.0-dev.32.</summary>
public sealed class PetReleasedEvent : PetChangeEvent { internal PetReleasedEvent(PetChange c) : base(c) { } }
/// <summary>A pet's level, health or maximum health changed. Since 0.2.0-dev.32.</summary>
public sealed class PetStatsChangedEvent : PetChangeEvent { internal PetStatsChangedEvent(PetChange c) : base(c) { } }
/// <summary>Kiln could no longer protect a pet's body (a capability went away) and retired it. Since 0.2.0-dev.32.</summary>
public sealed class PetProtectionLostEvent : PetChangeEvent { internal PetProtectionLostEvent(PetChange c) : base(c) { } }

/// <summary>A pet is going down: its combat body is about to be retired. <see cref="Outcome"/> starts as its definition's
/// choice; a handler may choose another for this pet. The down itself cannot be refused. Server tick thread.
/// Since 0.2.0-dev.32.</summary>
public sealed class PetDowningEvent : KilnEvent
{
    internal PetDowningEvent(PetSnapshot pet, PetChangeCause cause, PetDownedOutcome outcome) => (Pet, Cause, Outcome) = (pet, cause, outcome);
    /// <summary>The pet before it goes down.</summary>
    public PetSnapshot Pet { get; }
    /// <summary>Why it goes down (a lethal hit, a lost body, lost protection).</summary>
    public PetChangeCause Cause { get; }
    /// <summary>What it becomes: a recovery anchor where it fell, absent, or released.</summary>
    public PetDownedOutcome Outcome { get; set; }
}

/// <summary>A save committed a pet's state: its <see cref="PetSnapshot.DurableRevision"/> moved to the revision that save
/// holds. Server tick thread. Since 0.2.0-dev.32.</summary>
public sealed class PetRecordCommittedEvent : KilnEvent
{
    internal PetRecordCommittedEvent(PetId pet, CharacterKey owner, ulong captured, ulong previous, PetCommit commit) =>
        (Pet, Owner, CapturedStateRevision, PreviousDurableRevision, Commit) = (pet, owner, captured, previous, commit);
    /// <summary>The pet.</summary>
    public PetId Pet { get; }
    /// <summary>Its owner.</summary>
    public CharacterKey Owner { get; }
    /// <summary>The revision the committed bytes hold.</summary>
    public ulong CapturedStateRevision { get; }
    /// <summary>The durable revision before.</summary>
    public ulong PreviousDurableRevision { get; }
    /// <summary>The save.</summary>
    public PetCommit Commit { get; }
}

/// <summary>One of a pet's leases on its body changed (the broker's states before and after). Standalone callers watch
/// ControlChangedEvent; this one names the pet. Server tick thread. Since 0.2.0-dev.32.</summary>
public sealed class PetControlChangedEvent : KilnEvent
{
    internal PetControlChangedEvent(PetId pet, CharacterKey owner, ControlLeaseSnapshot? before, ControlLeaseSnapshot after) =>
        (Pet, Owner, Before, After) = (pet, owner, before, after);
    /// <summary>The pet.</summary>
    public PetId Pet { get; }
    /// <summary>Its owner.</summary>
    public CharacterKey Owner { get; }
    /// <summary>The lease before (null: first seen).</summary>
    public ControlLeaseSnapshot? Before { get; }
    /// <summary>The lease now.</summary>
    public ControlLeaseSnapshot After { get; }
}

/// <summary>A revive is about to start: its cost was quoted and nothing has moved. A handler may cancel it (nothing is taken
/// and no PetReviveStartedEvent follows): the mod's own revive rules (a cooldown, a zone). The completion checks the quote
/// again; no handler runs between the debit and the activation. Server tick thread. Since 0.2.0-dev.32.</summary>
public sealed class PetRevivingEvent : CancelableEvent
{
    internal PetRevivingEvent(CharacterKey owner, PetSnapshot pet, Guid request, PetCostDefinition? cost) =>
        (Owner, Pet, RequestId, Cost) = (owner, pet, request, cost);
    /// <summary>The owner.</summary>
    public CharacterKey Owner { get; }
    /// <summary>The downed pet before the channel.</summary>
    public PetSnapshot Pet { get; }
    /// <summary>The revive's request id (<see cref="Pets.CancelRevive"/> names it).</summary>
    public Guid RequestId { get; }
    /// <summary>The quoted cost; null: free.</summary>
    public PetCostDefinition? Cost { get; }
}
