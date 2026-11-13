// SPDX-License-Identifier: MIT
// Entity sets (since 0.2.0-dev.27): the entities that carry every component of one list and none of another, read
// with kiln.world's find (one pass over the entity map per listed component).
using System.Collections;

namespace Kiln;

/// <summary>The entities of the server world that have every component of <see cref="With"/> and none of
/// <see cref="Without"/>, as of the last <see cref="Refresh"/>. A set is a snapshot: entities come and go with every
/// step, so refresh it when you need it current (it costs one pass over the world's entities for the rarest of the
/// <see cref="With"/> components, then a lookup per candidate and other component). Since 0.2.0-dev.27.</summary>
public sealed unsafe class EntitySet : IReadOnlyCollection<EntityId>
{
    /// <summary>The capability entity sets need: <c>world.find</c> (kiln.world's pass over the entity map).</summary>
    public const string Capability = "world.find";

    private EntityId[] _entities = [];
    private HashSet<EntityId>? _lookup;

    internal EntitySet(IReadOnlyList<string> with, IReadOnlyList<string> without)
    {
        With = with;
        Without = without;
    }

    /// <summary>The components every entity of the set has (short names).</summary>
    public IReadOnlyList<string> With { get; }

    /// <summary>The components no entity of the set has.</summary>
    public IReadOnlyList<string> Without { get; }

    /// <summary>The entities, in no particular order.</summary>
    public IReadOnlyList<EntityId> Entities => _entities;

    /// <inheritdoc/>
    public int Count => _entities.Length;

    /// <summary>Whether <paramref name="entity"/> was in the set at the last <see cref="Refresh"/>.</summary>
    public bool Contains(EntityId entity) => (_lookup ??= [.. _entities]).Contains(entity);

    /// <summary>Reads the set again from the world. Tick thread.</summary>
    /// <returns>This set.</returns>
    /// <exception cref="KilnUnavailableException"><c>world.find</c> is off: kiln.world is not loaded, or it cannot walk the
    /// entity map on this game build.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public EntitySet Refresh()
    {
        var t = Table();
        // The rarest component gives the candidates; a component type the world does not have gives none.
        IReadOnlyList<EntityId>? candidates = null;
        var first = -1;
        for (var i = 0; i < With.Count; i++)
        {
            var found = World.FindCore(t, With[i], sets: true);
            if (candidates is not null && found.Count >= candidates.Count) continue;
            candidates = found;
            first = i;
            if (found.Count == 0) break;
        }
        var kept = new List<EntityId>(candidates!.Count);
        foreach (var e in candidates)
        {
            var ok = true;
            for (var i = 0; ok && i < With.Count; i++)
                if (i != first && !World.Has(e, With[i])) ok = false;
            for (var i = 0; ok && i < Without.Count; i++)
                if (World.Has(e, Without[i])) ok = false;
            if (ok) kept.Add(e);
        }
        _entities = [.. kept];
        _lookup = null;
        return this;
    }

    // The capability first (the one error rule), then the thread.
    internal static WorldNative.Table* Table()
    {
        var t = WorldNative.T;
        if (t == null) throw new KilnUnavailableException(Capability, "kiln.world is not loaded on this server");
        Services.RequireTickThread("World.Query");
        return t;
    }

    /// <inheritdoc/>
    public IEnumerator<EntityId> GetEnumerator() => ((IEnumerable<EntityId>)_entities).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entities.GetEnumerator();
}

public static unsafe partial class World
{
    /// <summary>The entities that have every one of <paramref name="with"/> (component short names), read now. Keep the
    /// set and call <see cref="EntitySet.Refresh"/> to read it again. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentException">No component, or an empty name.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.find</c> is off (kiln.world is not loaded, or cannot walk the
    /// entity map on this game build).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(EntitySet.Capability)]
    public static EntitySet Query(params string[] with) => Query(with, null);

    /// <summary>The entities that have every one of <paramref name="with"/> and none of <paramref name="without"/>, read
    /// now. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="with"/> is null.</exception>
    /// <exception cref="ArgumentException">No component in <paramref name="with"/>, or an empty name in either list.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.find</c> is off (kiln.world is not loaded, or cannot walk the
    /// entity map on this game build).</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(EntitySet.Capability)]
    public static EntitySet Query(IEnumerable<string> with, IEnumerable<string>? without)
    {
        ArgumentNullException.ThrowIfNull(with);
        string[] all = [.. with.Distinct(StringComparer.Ordinal)], none = [.. (without ?? []).Distinct(StringComparer.Ordinal)];
        if (all.Length == 0) throw new ArgumentException("a set needs at least one component its entities have", nameof(with));
        foreach (var name in all.Concat(none)) ArgumentException.ThrowIfNullOrEmpty(name, nameof(with));
        return new EntitySet(all, none).Refresh();
    }

    /// <summary>The entities that have component <typeparamref name="T"/>, read now.</summary>
    /// <exception cref="KilnUnavailableException"><c>world.find</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(EntitySet.Capability)]
    public static EntitySet Query<T>() where T : unmanaged, IGameComponent<T> => Query([T.Name], null);

    /// <summary>The entities that have both <typeparamref name="T1"/> and <typeparamref name="T2"/>, read now.</summary>
    /// <exception cref="KilnUnavailableException"><c>world.find</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [TickThreadOnly]
    [RequiresCapability(EntitySet.Capability)]
    public static EntitySet Query<T1, T2>() where T1 : unmanaged, IGameComponent<T1> where T2 : unmanaged, IGameComponent<T2> =>
        Query([T1.Name, T2.Name], null);
}
