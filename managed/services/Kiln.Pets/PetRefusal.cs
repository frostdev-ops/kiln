// SPDX-License-Identifier: MIT
// Why a pet request or definition was refused, as a value (PetResult.Refusal): the first word of its reason, so a mod
// decides by the value and never matches the text. Kiln.Taming's TamingRefusal names the same meaning with the same word
// (Busy, Capacity, NotOwner, OwnerOffline, RequestConflict, RefusedByMod, ...). A word a later Kiln adds is a member more
// (additive); until a mod is built against it, it reads as Other.
namespace Kiln;

/// <summary>Why a <see cref="Pets"/> request (<see cref="PetResult.Refusal"/>) or a definition
/// (<see cref="PetDefinitionResult.Refusal"/>) was refused: the first word of its <c>Reason</c>, as a value. The reason's
/// text says more, for a log. Since 0.2.0-dev.32.</summary>
public enum PetRefusal
{
    /// <summary>Not refused (<c>Ok</c>).</summary>
    None = 0,
    /// <summary>A reason this Kiln has no member for (a later Kiln's word): read <c>Reason</c>.</summary>
    Other = 1,
    /// <summary>Another request of the pet runs (a transition, a revive): try again when it settled.</summary>
    Busy = 2,
    /// <summary>The pet's <see cref="PetSnapshot.StateRevision"/> moved on from the request's
    /// <see cref="PetRequest.ExpectedStateRevision"/>: nothing changed.</summary>
    StaleRevision = 3,
    /// <summary>The request id was used for another request.</summary>
    RequestConflict = 4,
    /// <summary>The pet is not this owner's.</summary>
    NotOwner = 5,
    /// <summary>The owner is not in game.</summary>
    OwnerOffline = 6,
    /// <summary>The owner left the world while the request ran.</summary>
    OwnerLeft = 7,
    /// <summary>No player was named.</summary>
    NoOwner = 8,
    /// <summary>The owner has no character id here (an account-only player cannot own pets).</summary>
    NoCharacter = 9,
    /// <summary>No such pet.</summary>
    NotFound = 10,
    /// <summary>The pet is released.</summary>
    AlreadyReleased = 11,
    /// <summary>The revive a cancel named had completed already.</summary>
    AlreadyCompleted = 12,
    /// <summary>The revive a cancel named had ended already.</summary>
    AlreadyEnded = 13,
    /// <summary>A limit is reached: the definition's <see cref="PetLimits"/>, 128 records per character, 256 bodies in a
    /// world.</summary>
    Capacity = 14,
    /// <summary>The level the definition asks for cannot be had (the owner's level is not known yet).</summary>
    LevelUnavailable = 15,
    /// <summary>No definition of that id is registered.</summary>
    DefinitionUnknown = 16,
    /// <summary>The mod that defined the pet unloaded.</summary>
    DefinitionUnloaded = 17,
    /// <summary>Another definition under the id while its pets have bodies or revives (<see cref="Pets.Define"/>).</summary>
    DefinitionInUse = 18,
    /// <summary>The creature is not one Kiln knows (no creatures registry id or creature block of that name).</summary>
    CreatureUnknown = 19,
    /// <summary>The creature cannot be a pet's body (a boss, a flying creature, ...).</summary>
    CreatureUnsupported = 20,
    /// <summary>The creature's name is two templates': name one by its creatures registry id.</summary>
    CreatureAmbiguous = 21,
    /// <summary>kiln.creatures is not loaded here.</summary>
    CreaturesUnavailable = 22,
    /// <summary>No world records here (no server world).</summary>
    RecordsUnavailable = 23,
    /// <summary>The owner's records could not be read (kept as they are, never overwritten).</summary>
    RecordsUnreadable = 24,
    /// <summary>No server world runs here.</summary>
    WorldNotRunning = 25,
    /// <summary>The world ended while the request ran.</summary>
    WorldEnded = 26,
    /// <summary>kiln.spawn cannot make bodies here.</summary>
    SpawnUnavailable = 27,
    /// <summary>The game refused the body.</summary>
    SpawnRefused = 28,
    /// <summary>No safe ground near the owner for the body.</summary>
    NoSafeGround = 29,
    /// <summary>The body was not ready in time (5 s); it was removed.</summary>
    BodyNotReady = 30,
    /// <summary>Kiln refused to make the body (no loaded mod defines the pet, its faction could not be set).</summary>
    BodyRefused = 31,
    /// <summary>The body went down before the answer: the result carries the downed pet.</summary>
    BodyDowned = 32,
    /// <summary>The body is gone.</summary>
    BodyGone = 33,
    /// <summary>The control broker (creature_control) is not there.</summary>
    ControlUnavailable = 34,
    /// <summary>The control broker refused the pet's lease.</summary>
    ControlRefused = 35,
    /// <summary>The pet's lease ended without its asking.</summary>
    ControlLost = 36,
    /// <summary>The pet's lease failed.</summary>
    ControlFailed = 37,
    /// <summary>Kiln cannot protect a body here (the native guard is not there).</summary>
    ProtectionUnavailable = 38,
    /// <summary>The native guard refused the body's row.</summary>
    ProtectionRefused = 39,
    /// <summary>The body's protection went away.</summary>
    ProtectionLost = 40,
    /// <summary>The body could not be removed; it stays protected and the record is kept.</summary>
    RetireFailed = 41,
    /// <summary>The pet has no body to order.</summary>
    NotSummoned = 42,
    /// <summary>The pet's target rule does not allow the target (<see cref="PetTargetPolicy"/>).</summary>
    TargetProtected = 43,
    /// <summary>The target is not there.</summary>
    TargetGone = 44,
    /// <summary>Only a downed pet is revived.</summary>
    NotDowned = 45,
    /// <summary>A revive needs the pet's recovery anchor summoned next to its owner.</summary>
    RecoveryNotSummoned = 46,
    /// <summary>The owner is too far.</summary>
    OutOfRange = 47,
    /// <summary>The definition does not allow Kiln's revive (<see cref="PetRevivePolicy.Allowed"/>).</summary>
    ReviveNotAllowed = 48,
    /// <summary>A mod's handler refused it (a cancelable event such as <see cref="PetRevivingEvent"/>); nothing was
    /// taken.</summary>
    RefusedByMod = 49,
    /// <summary>The cost cannot be paid from the owner's slots (or its item is not known).</summary>
    CostUnavailable = 50,
    /// <summary>The revive was canceled before its cost was taken: nothing was spent.</summary>
    CanceledNoCost = 51,
    /// <summary>The revive was canceled after its cost was taken: the result carries the receipts.</summary>
    CanceledCostCommitted = 52,
    /// <summary>The revive failed after its cost was taken: the result carries the receipts; the pet stays downed.</summary>
    ReviveFailedCostCommitted = 53,
    /// <summary>A cost may have been taken (a fault inside the game's change): the result carries its receipt.</summary>
    UncertainCommit = 54,
    /// <summary>No such revive runs.</summary>
    NoChannel = 55,
    /// <summary>The request is not one this pet takes.</summary>
    Unsupported = 56,
}

/// <summary>A reason's first word as a refusal value (Kiln.Pets and Kiln.Taming share it).</summary>
internal static class RefusalWords
{
    /// <summary><paramref name="none"/> when <paramref name="ok"/>; else the member of <typeparamref name="T"/> named by the
    /// letters <paramref name="reason"/> starts with, or <paramref name="other"/> for a word it has no member for.</summary>
    internal static T Of<T>(bool ok, string? reason, T none, T other) where T : struct, Enum
    {
        if (ok) return none;
        var text = reason.AsSpan();
        var n = 0;
        while (n < text.Length && char.IsAsciiLetter(text[n])) n++;
        return n > 0 && Enum.TryParse<T>(text[..n], ignoreCase: false, out var value) && Enum.IsDefined(value)
               && !EqualityComparer<T>.Default.Equals(value, none) && !EqualityComparer<T>.Default.Equals(value, other)
            ? value
            : other;
    }
}
