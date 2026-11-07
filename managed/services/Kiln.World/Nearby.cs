// SPDX-License-Identifier: MIT
// Kiln.World: entities within a radius. Ported from shroudlink's host API (bjck/shroudlink, MIT: HostApi
// `entities_near` and `inventories_near`, docs/kiln/engines/shroudlink.md) onto Kiln's own World.Find/Position, so
// Kiln mods get what shroudlink mods have.
namespace Kiln;

/// <summary>Entities near a point or another entity. Tick thread, as <see cref="World"/>.</summary>
public static class Nearby
{
    /// <summary>Entities with the per-entity component <paramref name="component"/> within <paramref name="radius"/>
    /// metres of <paramref name="center"/>, nearest first; <paramref name="center"/> itself is left out. Empty when
    /// the center has no position. shroudlink: <c>World::entities_near(entity, radius, type)</c>.</summary>
    public static IReadOnlyList<EntityId> Entities(EntityId center, double radius, string component)
    {
        var at = World.Position(center);
        return at is null ? [] : Filter(World.Find(component), at.Value, radius, center);
    }

    /// <summary>Entities with <paramref name="component"/> within <paramref name="radius"/> metres of a point,
    /// nearest first.</summary>
    public static IReadOnlyList<EntityId> Entities(WorldPosition at, double radius, string component) =>
        Filter(World.Find(component), at, radius, default);

    /// <summary>The entities of <paramref name="candidates"/> within <paramref name="radius"/> metres of
    /// <paramref name="center"/>, nearest first (center left out). Inventories near a player, as shroudlink's
    /// <c>World::inventories_near(player, 40.0)</c>: <c>Nearby.Within(Items.FindInventories(), player, 40)</c>.</summary>
    public static IReadOnlyList<EntityId> Within(IEnumerable<EntityId> candidates, EntityId center, double radius)
    {
        var at = World.Position(center);
        return at is null ? [] : Filter(candidates, at.Value, radius, center);
    }

    /// <summary>The entities of <paramref name="candidates"/> within <paramref name="radius"/> metres of a point,
    /// nearest first.</summary>
    public static IReadOnlyList<EntityId> Within(IEnumerable<EntityId> candidates, WorldPosition at, double radius) =>
        Filter(candidates, at, radius, default);

    static IReadOnlyList<EntityId> Filter(IEnumerable<EntityId> candidates, WorldPosition at, double radius, EntityId skip)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!(radius >= 0)) throw new ArgumentOutOfRangeException(nameof(radius), radius, "radius must be 0 or more");
        var hits = new List<(EntityId Id, double D)>();
        foreach (var e in candidates)
        {
            if (e == skip || e.IsNone || !World.TryGetPosition(e, out var p)) continue;
            var d = p.DistanceTo(at);
            if (d <= radius) hits.Add((e, d));
        }
        hits.Sort((a, b) => a.D.CompareTo(b.D));
        return hits.ConvertAll(h => h.Id);
    }
}
