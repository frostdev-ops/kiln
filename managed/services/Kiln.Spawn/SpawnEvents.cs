// SPDX-License-Identifier: MIT
// Kiln.Spawn on the event bus: EntitySpawnedEvent, EntityDestroyedEvent (docs/kiln/events.md).
namespace Kiln;

public enum SpawnedKind
{
    /// <summary>A template (creature, prop, NPC): <see cref="Spawn.Entity(string, WorldPosition, float, SpawnOptions?)"/>.</summary>
    Entity = 0,
    /// <summary>A loose item pickup: <see cref="Spawn.Item"/>.</summary>
    Item = 1,
}

/// <summary>Kiln.Spawn created an entity (for any mod). Posted on the tick thread right after the spawn, before the
/// caller gets the id. Not cancelable. Entities the game spawns itself do not raise it.</summary>
public sealed class EntitySpawnedEvent : KilnEvent
{
    internal EntitySpawnedEvent(EntityId entity, SpawnedKind kind, Guid? template, string? templateName, WorldPosition position,
        uint itemId, int amount, string? tag, string origin)
    {
        Entity = entity;
        Kind = kind;
        Template = template;
        TemplateName = templateName;
        Position = position;
        ItemId = itemId;
        Amount = amount;
        Tag = tag;
        Origin = origin.Length > 0 ? origin : "kiln";
    }

    public EntityId Entity { get; }
    public SpawnedKind Kind { get; }

    /// <summary>The template GUID when spawned by GUID; null when spawned by name (see <see cref="TemplateName"/>).</summary>
    public Guid? Template { get; }

    /// <summary>The name given to the spawn call, when spawned by name.</summary>
    public string? TemplateName { get; }

    public WorldPosition Position { get; }

    /// <summary>Item pickups: the game's item id and the amount.</summary>
    public uint ItemId { get; }

    public int Amount { get; }
    public string? Tag { get; }

    /// <summary>Id of the mod that spawned it ("kiln" outside mod code).</summary>
    public string Origin { get; }

    public override string ToString() => $"spawned {Kind} {Entity} ({TemplateName ?? Template?.ToString() ?? $"item 0x{ItemId:x8} x{Amount}"}) at {Position} by {Origin}";
}

/// <summary>Kiln.Spawn destroyed an entity: <see cref="Spawn.Destroy"/> (<see cref="Reason"/> "destroy") or
/// <see cref="Spawn.CleanupTracked"/> ("cleanup"). Posted on the tick thread after it is gone. Not cancelable.</summary>
public sealed class EntityDestroyedEvent : KilnEvent
{
    internal EntityDestroyedEvent(EntityId entity, Guid? template, string? templateName, WorldPosition? position, string reason,
        string origin)
    {
        Entity = entity;
        Template = template;
        TemplateName = templateName;
        Position = position;
        Reason = reason;
        Origin = origin.Length > 0 ? origin : "kiln";
    }

    public EntityId Entity { get; }
    public Guid? Template { get; }
    public string? TemplateName { get; }

    /// <summary>Where it was just before (null when unknown).</summary>
    public WorldPosition? Position { get; }

    public string Reason { get; }

    /// <summary>Id of the mod that destroyed it.</summary>
    public string Origin { get; }

    public override string ToString() => $"destroyed {Entity} ({TemplateName ?? Template?.ToString() ?? "?"}) by {Origin} ({Reason})";
}
