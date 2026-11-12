// SPDX-License-Identifier: MIT
// Event filters (since 0.2.0-dev.27): Events.On<T>(handler, filter) receives only the events that pass the filter, and a
// service that holds work for handlers (kiln.combat's hit hold) asks the bus which entities its handlers watch.
namespace Kiln;

/// <summary>An event about entities (a hit's target and attacker, a death's victim). A subscription with an entity filter
/// (<see cref="EventFilter.Entities"/>) receives it only when <see cref="Involves"/> is true for one of the filter's
/// entities. Since 0.2.0-dev.27.</summary>
public interface IEntityEvent
{
    /// <summary>Whether the event is about <paramref name="entity"/>: any entity it names as taking part (for a hit: the
    /// target, the part hit, the attacker or the source).</summary>
    bool Involves(EntityId entity);
}

/// <summary>What a subscription receives (<see cref="Events.On{T}(Action{T}, EventFilter, EventPriority, bool)"/>). A field
/// left null matches every event. Since 0.2.0-dev.27.</summary>
public sealed record EventFilter
{
    /// <summary>Only events about one of these entities (the event type must be an <see cref="IEntityEvent"/>). Null:
    /// any entity.</summary>
    public IReadOnlySet<EntityId>? Entities { get; init; }

    /// <summary>Events about <paramref name="entity"/> only.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none.</exception>
    public static EventFilter ForEntity(EntityId entity) => ForEntities([entity]);

    /// <summary>Events about any of <paramref name="entities"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="entities"/> is null.</exception>
    /// <exception cref="ArgumentException">No entity, or a none entity among them.</exception>
    public static EventFilter ForEntities(IEnumerable<EntityId> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var set = new HashSet<EntityId>(entities);
        if (set.Count == 0) throw new ArgumentException("no entity", nameof(entities));
        if (set.Contains(EntityId.None)) throw new ArgumentException("EntityId.None names no entity", nameof(entities));
        return new EventFilter { Entities = set };
    }

    /// <summary>Whether <paramref name="ev"/> passes this filter.</summary>
    public bool Passes(KilnEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (Entities is not { } set) return true;
        if (ev is not IEntityEvent e) return false;
        foreach (var id in set)
            if (e.Involves(id)) return true;
        return false;
    }
}
