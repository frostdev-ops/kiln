// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>What a <see cref="ContentChangedEvent"/> reports.</summary>
public enum ContentChange
{
    /// <summary>A field, a reference, a recipe's stacks or a drop table of one entry was set as a mod's layer.</summary>
    Set = 1,

    /// <summary>A mod's layer of one field was taken off (<see cref="ContentEntry.Reset"/>).</summary>
    Reset = 2,

    /// <summary>A data-pack document was applied (<see cref="Content.Apply"/>).</summary>
    Applied = 3,

    /// <summary>Every layer of a mod was taken off (<see cref="Content.Revert"/>).</summary>
    Reverted = 4,
}

/// <summary>
/// A mod changed game content through Kiln.Content from code: a value of an entry, a data-pack document applied, or its
/// layers taken off. Posted on the tick thread right after the change took. Observe only. Built only while someone
/// subscribes. Content that changes without a call (the data packs of mods' folders at world load and at
/// <see cref="Content.ReloadPacks"/>) is reported by <see cref="ContentReadyEvent"/>, not here. Since 0.2.0-dev.27.
/// </summary>
public sealed class ContentChangedEvent : KilnEvent
{
    internal ContentChangedEvent(ContentChange change, ContentKind? kind, uint id, string name, string path, string origin)
    {
        Change = change;
        Kind = kind;
        Id = id;
        Name = name;
        Path = path;
        Origin = origin;
    }

    /// <summary>What happened.</summary>
    public ContentChange Change { get; }

    /// <summary>The registry of the entry (<see cref="ContentChange.Set"/>, <see cref="ContentChange.Reset"/>); null for a
    /// document or a mod's layers as a whole.</summary>
    public ContentKind? Kind { get; }

    /// <summary>The entry's id; 0 for a document or a mod's layers as a whole.</summary>
    public uint Id { get; }

    /// <summary>The entry's name; "" for a document or a mod's layers as a whole.</summary>
    public string Name { get; }

    /// <summary>The field's path as the call gave it, a reference key (<c>model</c>), <c>inputs</c>, <c>outputs</c> or
    /// <c>stacks</c>; for <see cref="ContentChange.Applied"/> the document's source label; "" for
    /// <see cref="ContentChange.Reverted"/>.</summary>
    public string Path { get; }

    /// <summary>The id of the mod that made the change ("" when Kiln made it for a mod without code).</summary>
    public string Origin { get; }

    /// <summary>The change, what it touched and who did it, for a log line.</summary>
    public override string ToString() => Kind is { } k
        ? $"content {Change}: {k} {Name}.{Path} by {(Origin.Length > 0 ? Origin : "kiln")}"
        : $"content {Change}: {Path} by {(Origin.Length > 0 ? Origin : "kiln")}";
}
