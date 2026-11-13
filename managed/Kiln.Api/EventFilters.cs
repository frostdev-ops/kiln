// SPDX-License-Identifier: MIT
// Event filters (since 0.2.0-dev.27): Events.On<T>(handler, filter) receives only the events that pass the filter, and a
// service that holds work for handlers (kiln.combat's hit hold) asks the bus which entities its handlers watch.
using System.Collections.Frozen;

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
    private readonly FrozenSet<EntityId>? _entities;

    /// <summary>Only events about one of these entities (the event type must be an <see cref="IEntityEvent"/>). Null:
    /// any entity. The set is copied when the filter is made: adding to or removing from the set you passed changes
    /// nothing afterwards (subscribe again with a new filter instead).</summary>
    public IReadOnlySet<EntityId>? Entities
    {
        get => _entities;
        init => _entities = value?.ToFrozenSet();
    }

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

/// <summary>Keeps what a service holds for the handlers of some event types in step with the bus: the entities their
/// filters name, pushed to the service whenever they change (kiln.combat's hit hold: only hits on watched entities are
/// held for the handlers). Every subscription that is added leads to a push before <c>Events.On</c> returns, on the
/// subscribing thread, also when the entities stay the same: from then on the service holds that subscriber's events
/// too, and the push is where a service passes on what else the new handler needs (kiln.combat: the kinds of event it
/// wants). A removed one only narrows the set, which waits for the next <see cref="Refresh()"/> (the service calls it
/// once a step, and nothing is pushed then unless the entities changed).</summary>
internal sealed class EntityWatch : IDisposable
{
    private readonly Type[] _kinds;
    private readonly Func<uint[], bool> _push;
    private readonly object _lock = new();
    private int _version = -1;
    private uint[]? _pushed;

    /// <param name="kinds">The event types whose handlers are watched.</param>
    /// <param name="push">Gives the service the entity ids, ascending; empty means "everything" (a handler without an
    /// entity filter, or nobody with one). Any thread. False: it did not take them (tried again at the next refresh).</param>
    public EntityWatch(Type[] kinds, Func<uint[], bool> push)
    {
        _kinds = kinds;
        _push = push;
        Events.SubscriptionAdded += Added;
    }

    public void Dispose() => Events.SubscriptionAdded -= Added;

    private void Added() => Refresh(force: true);

    /// <summary>The watched ids from each kind's union (<see cref="Events.EntityFilterUnion"/>): empty as soon as one
    /// kind has a handler that takes every event, or when nobody filters at all. Pure.</summary>
    internal static uint[] Compute(IEnumerable<IReadOnlySet<EntityId>?> unions)
    {
        HashSet<uint>? all = null;
        foreach (var u in unions)
        {
            if (u is null) return [];
            (all ??= []).UnionWith(u.Select(e => e.Value));
        }
        return all is { Count: > 0 } ? all.Order().ToArray() : [];
    }

    /// <summary>Pushes the set when subscriptions changed since the last push and the entities are other ones. Any thread.</summary>
    public void Refresh() => Refresh(force: false);

    private void Refresh(bool force)
    {
        lock (_lock)
        {
            var version = Events.Version;
            if (!force && version == _version) return;
            var ids = Compute(_kinds.Select(Events.EntityFilterUnion));
            if (force || _pushed is null || !ids.AsSpan().SequenceEqual(_pushed))
            {
                if (!_push(ids)) return;
                _pushed = ids;
            }
            _version = version;
        }
    }
}
