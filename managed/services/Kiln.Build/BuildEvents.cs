// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>What a Kiln edit is.</summary>
public enum BuildEditKind
{
    /// <summary>Exact cells (<see cref="Build.Write(CellBox, ReadOnlySpan{Voxel}, BuildOptions?)"/>).</summary>
    Write = 1,
    Dig,
    Add,
    Flatten,
    Smooth,
    Paint,
    Replace,
    /// <summary>A schematic paste (cells and props).</summary>
    Paste,
    /// <summary>One prop placed.</summary>
    PlaceProp,
    /// <summary>One prop removed.</summary>
    RemoveProp,
    /// <summary>A journal entry reverted.</summary>
    Undo,
}

/// <summary>Posted before a Kiln edit (managed API, schematics, commands) changes the world. Cancel it to stop the
/// edit: the call then returns a result with <see cref="BuildRefusal.Canceled"/>.</summary>
/// <remarks>Tick thread, inside the call that edits (before any cell or prop changes). Handlers may read
/// <see cref="Box"/> and <see cref="Actor"/>; changing them has no effect. Player edits made with the game's build
/// hammer are <c>BuildEvent</c>s of Kiln.Gameplay, not this.</remarks>
public sealed class BuildEditingEvent : CancelableEvent
{
    internal BuildEditingEvent(BuildEditKind kind, CellBox box, EntityId actor, string origin, string? label)
    {
        Kind = kind;
        Box = box;
        Actor = actor;
        Origin = origin;
        Label = label;
    }

    public BuildEditKind Kind { get; }

    /// <summary>The cells the edit may change (props: the cell of the prop's position).</summary>
    public CellBox Box { get; }

    /// <summary>The player the edit is for, or none (Kiln itself).</summary>
    public EntityId Actor { get; }

    /// <summary>The mod that asked for it.</summary>
    public string Origin { get; }

    public string? Label { get; }
}

/// <summary>Posted after a Kiln edit changed the world (or was refused by the world: zone, base, not loaded).</summary>
/// <remarks>Tick thread, right after the edit, before the call returns. Read-only.</remarks>
public sealed class BuildEditEvent : KilnEvent
{
    internal BuildEditEvent(BuildEditKind kind, CellBox box, EntityId actor, string origin, string? label, BuildResult? result,
        IReadOnlyList<EntityId> propsPlaced, IReadOnlyList<EntityId> propsRemoved, long journalId)
    {
        Kind = kind;
        Box = box;
        Actor = actor;
        Origin = origin;
        Label = label;
        Result = result;
        PropsPlaced = propsPlaced;
        PropsRemoved = propsRemoved;
        JournalId = journalId;
    }

    public BuildEditKind Kind { get; }
    public CellBox Box { get; }
    public EntityId Actor { get; }
    public string Origin { get; }
    public string? Label { get; }

    /// <summary>The voxel part (null for prop-only edits).</summary>
    public BuildResult? Result { get; }

    public IReadOnlyList<EntityId> PropsPlaced { get; }
    public IReadOnlyList<EntityId> PropsRemoved { get; }

    /// <summary>The journal entry (0 = not journaled).</summary>
    public long JournalId { get; }

    public bool Changed => (Result?.Changed ?? false) || PropsPlaced.Count > 0 || PropsRemoved.Count > 0;
}
