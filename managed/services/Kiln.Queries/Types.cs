// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Where a voxel ray met the first voxel surface: terrain or building voxels, nothing else.</summary>
/// <param name="Position">Where the segment meets the voxel.</param>
/// <param name="Distance">Metres from the ray's start to <paramref name="Position"/>.</param>
/// <param name="Material">The game's material id of the voxel.</param>
/// <param name="Voxel">The raw packed voxel.</param>
public sealed record VoxelRayHit(WorldPosition Position, double Distance, uint Material, ushort Voxel);

/// <summary>A voxel ray's answer. <see cref="Ok"/> with a <see cref="Hit"/>: the first voxel surface. <see cref="Ok"/>
/// without: the whole segment was read and is clear. Not <see cref="Ok"/>: nothing is known, and <see cref="Reason"/> says
/// why: <c>NotLoaded</c> (load the area with <c>Build.Load</c> and ask in a later step), <c>OutOfGrid</c>,
/// <c>BudgetExceeded</c> (ask next step), <c>WorldNotReady</c>, <c>ReentrantQuery</c>, <c>InvalidHit</c>.</summary>
public sealed record VoxelRayResult(bool Ok, string? Reason, VoxelRayHit? Hit) : IKilnResult;

/// <summary>Whether voxels stand between two entities. <see cref="Ok"/> and <see cref="Visible"/>: nothing does.
/// <see cref="Ok"/> and not visible: <see cref="Obstruction"/> is the first voxel surface in the way. Not <see cref="Ok"/>:
/// nothing is known (<see cref="Reason"/>: a ray's reasons, or <c>EntityGone</c>); <see cref="Visible"/> is false then,
/// and that does not mean "hidden".</summary>
public sealed record VoxelSightResult(bool Ok, string? Reason, bool Visible, VoxelRayHit? Obstruction) : IKilnResult;

/// <summary>What of an entity an overlap question looks at. <see cref="Origin"/>: its position, one point; not its collider.</summary>
public enum EntityQueryGeometry { Origin }

/// <summary>The entities whose origin is inside the shape, nearest first (then by id). <see cref="Total"/> is how many
/// matched; <see cref="Truncated"/> says the list was cut at the <c>max</c> asked for.</summary>
public sealed record EntityQueryResult(bool Ok, string? Reason, IReadOnlyList<EntityId> Entities, int Total, bool Truncated) : IKilnResult;

/// <summary>From where to where <see cref="Queries.VoxelLineOfSight"/> looks: offsets in metres along the world's axes
/// (Y up) added to each entity's position. The default, 1.5 m up, is a convention, not a creature's eye.</summary>
public sealed record VoxelSightOptions
{
    public WorldPosition FromOffset { get; init; } = new(0, 1.5, 0);
    public WorldPosition ToOffset { get; init; } = new(0, 1.5, 0);
    /// <summary>The realm both entities are in. 0: the overworld.</summary>
    public byte Realm { get; init; }
}
