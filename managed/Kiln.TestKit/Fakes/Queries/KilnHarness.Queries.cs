// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessQueries? _queries;

    /// <summary>The fake kiln.queries of this harness: what a voxel ray answers, and the rays mods asked for.</summary>
    public HarnessQueries Queries => _queries ??= new HarnessQueries(this);
}

/// <summary>A voxel ray a mod asked for (<c>Queries.RaycastVoxels</c>, <c>GroundBelow</c>, <c>VoxelLineOfSight</c>, a brain's sight).</summary>
public readonly record struct FakeVoxelRay(WorldPosition From, WorldPosition To, byte Realm);

/// <summary>What the fake answers a voxel ray: <see cref="Clear"/>, <see cref="Hit"/>, or <see cref="Refused"/> with one of
/// the service's reasons (<c>NotLoaded</c>, <c>OutOfGrid</c>).</summary>
public readonly record struct FakeVoxelAnswer
{
    internal WorldPosition? HitAt { get; private init; }
    internal uint Material { get; private init; }
    internal ushort Voxel { get; private init; }
    internal string? Refusal { get; private init; }

    /// <summary>Loaded, and nothing on the segment.</summary>
    public static FakeVoxelAnswer Clear => default;

    /// <summary>The first voxel surface is at <paramref name="at"/>.</summary>
    public static FakeVoxelAnswer Hit(WorldPosition at, uint material = 1, ushort voxel = 0xff01) => new() { HitAt = at, Material = material, Voxel = voxel };

    /// <summary>Nothing is known, for <paramref name="reason"/>: <c>NotLoaded</c>, <c>OutOfGrid</c>.</summary>
    /// <exception cref="ArgumentException">An empty reason.</exception>
    public static FakeVoxelAnswer Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new() { Refusal = reason };
    }
}

/// <summary>
/// The fake kiln.queries of a harness (<see cref="KilnHarness.Queries"/>): Kiln.Queries' own code runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> The rules around a ray: its numbers, the tick thread, the capability rows, the shared budget
/// per step (<see cref="MaxRaysPerStep"/>), refusals with their reasons. Entity origins come from the harness's fake world.</para>
/// <para><b>Not modelled.</b> Voxels: a ray answers what <see cref="Voxels"/> says, and is loaded and clear without a
/// script. The time part of the budget, realms, and paths.</para>
/// </remarks>
public sealed class HarnessQueries
{
    private readonly KilnHarness _harness;

    internal HarnessQueries(KilnHarness harness) => _harness = harness;

    private QueriesFakeState State => KitFakes.State<QueriesFakeState>(_harness);

    /// <summary>What a voxel ray answers. Null: every ray is loaded and clear. Runs on the tick thread, inside the mod's call.</summary>
    public Func<FakeVoxelRay, FakeVoxelAnswer>? Voxels
    {
        get { lock (State.Lock) return State.Voxels; }
        set { lock (State.Lock) State.Voxels = value; }
    }

    /// <summary>How many rays the fake admits per step before it refuses with <c>BudgetExceeded</c> (the service's default: 64).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not 0 to 4096.</exception>
    public int MaxRaysPerStep
    {
        get { lock (State.Lock) return State.MaxRaysPerStep; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 4096);
            lock (State.Lock) State.MaxRaysPerStep = value;
        }
    }

    /// <summary>Every ray that was admitted, in order.</summary>
    public IReadOnlyList<FakeVoxelRay> Rays { get { lock (State.Lock) return [.. State.Rays]; } }
}
