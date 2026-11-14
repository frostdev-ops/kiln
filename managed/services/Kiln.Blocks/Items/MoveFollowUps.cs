// SPDX-License-Identifier: MIT
// Where a move's follow-up windows lead: the author's shape (MoveBlock.FollowUps) and what Blocks.MovesStatus and
// WeaponMovesApplicationChangedEvent say about a weapon whose moves apply as one generation. A sequence of the game has
// timed windows ("on Primary, between here and there, go on with that sequence"); a route points a window the motion
// already has at another move of the same weapon. No window is made and none is taken away. The resolver is
// MoveRoutes.cs, the application MoveApplications.cs; docs/kiln/modding/item-blocks.md, "Follow-ups".
namespace Kiln;

/// <summary>An attack slot of a weapon, as <see cref="WeaponMoves"/> names them: where a <see cref="MoveFollowUpBlock"/> leads.</summary>
public enum WeaponMoveSlot
{
    /// <summary><see cref="WeaponMoves.Primary"/>.</summary>
    Primary,
    /// <summary><see cref="WeaponMoves.Heavy"/>.</summary>
    Heavy,
    /// <summary><see cref="WeaponMoves.HeavyAir"/>.</summary>
    HeavyAir,
    /// <summary><see cref="WeaponMoves.DodgeAttack"/>.</summary>
    DodgeAttack,
    /// <summary><see cref="WeaponMoves.JumpAttack"/>.</summary>
    JumpAttack,
    /// <summary><see cref="WeaponMoves.PlungeAttack"/>.</summary>
    PlungeAttack,
    /// <summary><see cref="WeaponMoves.Block"/>.</summary>
    Block,
    /// <summary><see cref="WeaponMoves.Parry"/>.</summary>
    Parry,
    /// <summary><see cref="WeaponMoves.Special"/>.</summary>
    Special,
    /// <summary><see cref="WeaponMoves.Execution"/>.</summary>
    Execution,
    /// <summary><see cref="WeaponMoves.SneakAttack"/>.</summary>
    SneakAttack,
}

/// <summary>What a follow-up window of a motion waits for.</summary>
public enum MoveFollowUpTrigger
{
    /// <summary>The light attack's input.</summary>
    Primary,
    /// <summary>The heavy attack's input.</summary>
    Heavy,
    /// <summary>The block's input.</summary>
    Block,
    /// <summary>The outcome of a block that parried a blow. Not the parry's input: a window that waits for that input is not routed.</summary>
    Parried,
}

/// <summary>One route: the follow-up windows of this move that wait for <see cref="Trigger"/> lead to another move of the same weapon.
/// Only a window the motion already has, with a follow-up of its own, is routed; its time, its skill and tag conditions and its cost stay.
/// A motion often has several windows for one input (one per skill a player may have): they are counted in the motion's own order, by time,
/// from 0. Two routes of one move that select the same window are refused, also when they say the same.</summary>
public sealed record MoveFollowUpBlock
{
    /// <summary>The input or outcome the windows wait for.</summary>
    public required MoveFollowUpTrigger Trigger { get; init; }
    /// <summary>Which sub-sequence of the motion, from 0. Most motions have one.</summary>
    public int SubSequence { get; init; }
    /// <summary>Which of the windows for <see cref="Trigger"/> in that sub-sequence, from 0 in the motion's own order (by time; windows without a
    /// follow-up are not counted). Null: all of them.</summary>
    public int? Occurrence { get; init; }
    /// <summary>The slot of this weapon the windows lead to. The weapon's moves must say what that slot plays.</summary>
    public required WeaponMoveSlot To { get; init; }
    /// <summary>Which link of that slot's chain, from 1.</summary>
    public int Link { get; init; } = 1;
}

/// <summary>Where a weapon's moves with follow-ups are.</summary>
public enum MoveApplicationState
{
    /// <summary>Waiting for the game's data: nothing is applied yet.</summary>
    PendingContent,
    /// <summary>The weapon's slots play the moves of <see cref="MoveApplicationResult.AppliedGeneration"/>.</summary>
    Applied,
    /// <summary>The last attempt was refused whole. The slots play what they played before it.</summary>
    Refused,
    /// <summary>The mod unloaded: the slots are the class's again.</summary>
    Removed,
}

/// <summary>What <see cref="Blocks.MovesStatus"/> answers: a snapshot that never changes. A weapon with follow-ups applies all its moves, slots
/// and routes at once (a generation) or none of them: an attempt that is refused leaves the last applied generation playing.</summary>
public sealed record MoveApplicationResult : IKilnResult
{
    /// <summary>True only when <see cref="State"/> is <see cref="MoveApplicationState.Applied"/>.</summary>
    public bool Ok { get; init; }
    /// <summary>Empty when applied; <c>"PendingContent"</c>, <c>"Removed"</c>, or why the last attempt was refused (the block's field and what is wrong with it).</summary>
    public string Reason { get; init; } = "";
    /// <summary>Where the weapon's moves are.</summary>
    public MoveApplicationState State { get; init; }
    /// <summary>The number of the last attempt, counted from 1 for this weapon while its mod is loaded.</summary>
    public ulong RequestedGeneration { get; init; }
    /// <summary>The number of the attempt whose slots are playing: the last one that went through. 0: none has.</summary>
    public ulong AppliedGeneration { get; init; }
    /// <summary>The content generation (<c>Content.Status().Generation</c>) the last attempt read the game's data in.</summary>
    public ulong ContentGeneration { get; init; }
    /// <summary>The sequences the applied generation plays, by <c>slot/link</c> (<c>"primary/1"</c>, <c>"block/1"</c>). Empty while none is applied.
    /// For looking at; the GUIDs change when the moves or the game's data do.</summary>
    public IReadOnlyDictionary<string, Guid> LiveClones { get; init; } = System.Collections.Frozen.FrozenDictionary<string, Guid>.Empty;
}

/// <summary>A weapon's moves with follow-ups went somewhere: applied, refused, or removed with its mod. Raised on the tick, once per change;
/// an attempt that changes nothing (the same moves again) raises none. <see cref="After"/> says <see cref="MoveApplicationState.Applied"/>
/// only once the weapon's slots have been switched and read back.</summary>
public sealed class WeaponMovesApplicationChangedEvent : KilnEvent
{
    internal WeaponMovesApplicationChangedEvent(string modId, string blockName, MoveApplicationResult before, MoveApplicationResult after)
    {
        ModId = modId;
        BlockName = blockName;
        Before = before;
        After = after;
    }

    /// <summary>The mod whose weapon it is.</summary>
    public string ModId { get; }
    /// <summary>The weapon block's name in that mod.</summary>
    public string BlockName { get; }
    /// <summary>What <see cref="Blocks.MovesStatus"/> answered before.</summary>
    public MoveApplicationResult Before { get; }
    /// <summary>What it answers now.</summary>
    public MoveApplicationResult After { get; }
}
