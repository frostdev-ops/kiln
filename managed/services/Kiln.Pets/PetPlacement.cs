// SPDX-License-Identifier: MIT
// Where a body or an anchor may go (19d §4.3a, PetPlacement.Find): radii around the owner in 8 directions, front first, at
// most 24 candidates; each must have voxel ground found by kiln.queries (a loaded, Ok hit), never an unchecked spot. A spot
// is voxel/settling validation, not proof that no prop collider stands there.
namespace Kiln;

internal static class PetPlacement
{
    internal const int MaxCandidates = 24;

    /// <summary>The candidate spots in order (no world reads): radii from <paramref name="min"/> to <paramref name="max"/>
    /// (whole metres, at least one ring), 8 directions each, +Z first and then alternating left and right.</summary>
    internal static IEnumerable<WorldPosition> Candidates(WorldPosition near, double min, double max)
    {
        var rings = new List<double>();
        for (var r = Math.Max(min, 0.5); r <= max + 1e-9 && rings.Count < 3; r += 1) rings.Add(r);
        if (rings.Count == 0) rings.Add(max);
        if (min <= 0) rings.Insert(0, 0);
        int[] order = [0, 1, 7, 2, 6, 3, 5, 4];
        var n = 0;
        foreach (var r in rings)
            foreach (var k in order)
            {
                if (n++ >= MaxCandidates) yield break;
                if (r == 0)
                {
                    yield return near;
                    break;
                }
                var a = k * Math.PI / 4;
                yield return new WorldPosition(near.X + r * Math.Sin(a), near.Y, near.Z + r * Math.Cos(a));
            }
    }

    internal const double FootRadius = 0.5, DefaultHeight = 2, MaxFloorSpread = 0.5, KeepAway = 2;

    /// <summary>A spot's samples: the floor under its centre and under 4 footprint points, whether the column above the floor
    /// is clear up to the body's height, and the origins of entities nearby (other than the owner and the pet itself).</summary>
    internal readonly record struct Samples(double? Centre, IReadOnlyList<double?> Foot, bool Clear, int Neighbours);

    /// <summary>19d §4.3a's checks on a spot: ground under the centre and every footprint point, a floor spread of at most
    /// 0.5 m, a clear column, no entity origin within the footprint + 2 m. Voxel/settling validation, not a collider proof.</summary>
    internal static bool Accept(Samples s) =>
        s.Centre is { } c && s.Foot.All(f => f is not null) && s.Foot.Select(f => Math.Abs(f!.Value - c)).DefaultIfEmpty(0).Max() <= MaxFloorSpread &&
        s.Clear && s.Neighbours == 0;

    /// <summary>The first candidate that passes <see cref="Accept"/>, as its floor + 0.05 m; null when none (or the ground cannot
    /// be read here: kiln.queries' ground ray is off, or its budget for this step is used up).</summary>
    internal static WorldPosition? Find(WorldPosition near, double min, double max, PetDefinition def, PetCreatureInfo? creature, EntityId owner = default, EntityId pet = default)
    {
        if (!double.IsFinite(near.X) || !double.IsFinite(near.Y) || !double.IsFinite(near.Z)) return null;
        if (!Capabilities.Is("queries.ground")) return null;
        var search = def.Movement.GroundSearchMeters;
        var height = DefaultHeight * def.Level.Scale;
        try
        {
            foreach (var c in Candidates(near, min, max))
            {
                double? Floor(WorldPosition at)
                {
                    var r = Queries.GroundBelow(new WorldPosition(at.X, at.Y + search, at.Z), search * 2);
                    return r is { Ok: true, Hit: { } hit } ? hit.Position.Y : null;
                }
                var centre = Floor(c);
                if (centre is null) continue;
                var foot = new List<double?>();
                for (var k = 0; k < 4; k++)
                {
                    var a = k * Math.PI / 2;
                    foot.Add(Floor(new WorldPosition(c.X + FootRadius * Math.Sin(a), centre.Value, c.Z + FootRadius * Math.Cos(a))));
                }
                var top = new WorldPosition(c.X, centre.Value + height, c.Z);
                var up = Queries.RaycastVoxels(new WorldPosition(c.X, centre.Value + 0.1, c.Z), top);
                var clear = up is { Ok: true, Hit: null };
                var neighbours = 0;
                if (Capabilities.Is("queries.overlap_origins"))
                {
                    var q = Queries.EntitiesInSphere(new WorldPosition(c.X, centre.Value, c.Z), FootRadius + KeepAway, owner);
                    neighbours = q.Ok ? q.Entities.Count(e => e != pet && !e.IsNone) : 1;
                }
                if (Accept(new Samples(centre, foot, clear, neighbours))) return new WorldPosition(c.X, centre.Value + 0.05, c.Z);
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        return null;
    }
}
