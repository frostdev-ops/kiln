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

    /// <summary>The first candidate with voxel ground within the definition's search height, as the floor + 0.05 m; null when
    /// none (or the ground cannot be read here).</summary>
    internal static WorldPosition? Find(WorldPosition near, double min, double max, PetDefinition def, PetCreatureInfo? creature)
    {
        if (!double.IsFinite(near.X) || !double.IsFinite(near.Y) || !double.IsFinite(near.Z)) return null;
        if (!Capabilities.Is("queries.ground")) return null;
        var search = def.Movement.GroundSearchMeters;
        foreach (var c in Candidates(near, min, max))
        {
            VoxelRayResult r;
            try { r = Queries.GroundBelow(new WorldPosition(c.X, c.Y + search, c.Z), search * 2); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
            if (r is { Ok: true, Hit: { } hit }) return new WorldPosition(hit.Position.X, hit.Position.Y + 0.05, hit.Position.Z);
        }
        return null;
    }
}
