// SPDX-License-Identifier: MIT
// Why a taming call was refused, as a value (TamingResult.Refusal): the first word of its reason, so a mod decides by the
// value and never matches the text. The same meaning has the same word as Kiln.Pets' PetRefusal (Busy, Capacity, NotOwner,
// OwnerOffline, RequestConflict, OutOfRange, RefusedByMod, UncertainCommit, ...). A word a later Kiln adds is a member more
// (additive); until a mod is built against it, it reads as Other.
namespace Kiln;

/// <summary>Why a <see cref="Taming"/> call (<see cref="TamingResult.Refusal"/>, <see cref="TamingChoicesResult.Refusal"/>)
/// or a <see cref="NativeTaming.PetStep(EntityId)"/> (<see cref="NativeStepResult.Refusal"/>) was refused: the first word of its
/// <c>Reason</c>, as a value. The reason's text says more, for a log. Since 0.2.0-dev.32.</summary>
public enum TamingRefusal
{
    /// <summary>Not refused (<c>Ok</c>).</summary>
    None = 0,
    /// <summary>A reason this Kiln has no member for (a later Kiln's word): read <c>Reason</c>.</summary>
    Other = 1,
    /// <summary>The attempt is another player's.</summary>
    NotOwner = 2,
    /// <summary>The player is not in game.</summary>
    OwnerOffline = 3,
    /// <summary>The attempt's owner left the world.</summary>
    OwnerLeft = 4,
    /// <summary>The player's character is not known (an account-only player cannot own the pet a tame makes).</summary>
    NoCharacter = 5,
    /// <summary>Another session or world than the attempt's.</summary>
    StaleSession = 6,
    /// <summary>Another taming of the target runs, or a step's cost is being paid.</summary>
    Busy = 7,
    /// <summary>The request's token is not the attempt's current step (the attempt moved on): read it again.</summary>
    StaleStep = 8,
    /// <summary>The request id was used for another action.</summary>
    RequestConflict = 9,
    /// <summary>Too many requests: 10 a second per player, 256 per attempt.</summary>
    TooManyRequests = 10,
    /// <summary>The selection is not the owner's slot of the step's item (or holds too few).</summary>
    StaleSelection = 11,
    /// <summary>The player is too far from the target.</summary>
    OutOfRange = 12,
    /// <summary>The target's health is above the weaken threshold.</summary>
    Threshold = 13,
    /// <summary>The target is not there (or dead).</summary>
    BodyGone = 14,
    /// <summary>The target is not a creature the definition tames.</summary>
    TemplateMismatch = 15,
    /// <summary>A limit is reached: 512 attempts in a world, the closed attempts kept, the pet roster's room
    /// (<see cref="PetLimits"/>, 128 records per character).</summary>
    Capacity = 16,
    /// <summary>No definition of that id is registered.</summary>
    DefinitionUnknown = 17,
    /// <summary>The definition cannot be replaced now (attempts of it run, or the id is another mod's).</summary>
    DefinitionInUse = 18,
    /// <summary>The definition's mod unloaded.</summary>
    DefinitionGone = 19,
    /// <summary>The attempt is not in a state that takes this call (every step paid: retry; a step running).</summary>
    WrongState = 20,
    /// <summary>The game has not counted the native step(s) yet.</summary>
    NativePending = 21,
    /// <summary>Every step is paid; the tame waits for <see cref="Taming.Retry"/> (the final condition was not met).</summary>
    CommitDeferred = 22,
    /// <summary>The attempt is over (or unknown).</summary>
    AttemptClosed = 23,
    /// <summary>The attempt ran out of time.</summary>
    Expired = 24,
    /// <summary>The attempt was canceled (by its player or a failure meanwhile).</summary>
    Canceled = 25,
    /// <summary>A mod's handler of a before-event refused it (<see cref="TamingBeginningEvent"/>,
    /// <see cref="TamingAdvancingEvent"/>, <see cref="TamingCommittingEvent"/>); nothing more was spent and the attempt
    /// goes on.</summary>
    RefusedByMod = 26,
    /// <summary>The cost adapter, the binder or the game refused it; the reason says which.</summary>
    Refused = 27,
    /// <summary>A cost may have been taken (a fault inside the game's change): the result carries its receipt.</summary>
    UncertainCommit = 28,
    /// <summary>The game took only part of a stack: spent as the receipt says; the attempt ended.</summary>
    PartialCostCommitted = 29,
    /// <summary>The world ended.</summary>
    WorldEnded = 30,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: no wild animal.</summary>
    NotWild = 31,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: its template does not allow taming.</summary>
    NotTameable = 32,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: it is a farm animal already.</summary>
    Tamed = 33,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: it has its steps; the game tames it in its next step.</summary>
    Complete = 34,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: a step counts only while it eats its bait.</summary>
    NotEating = 35,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: its pet interaction is not the game's standard one Kiln knows.</summary>
    OtherPetSequence = 36,
    /// <summary>A <see cref="NativeTaming.PetStep(EntityId)"/>: the game did not start the pet sequence.</summary>
    SequenceRefused = 37,
}
