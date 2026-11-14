// SPDX-License-Identifier: MIT
// Kiln.Queries' own code on the test kit's kiln.queries (chapter 01 section 6, the offline list): the three states of a ray
// stay apart, a refusal has its reason and Ok never has one, bad numbers throw, a row that is off throws by its name.
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace QueriesTests;

public sealed class QueriesTests
{
    private static readonly WorldPosition A = new(10, 50, 10), B = new(10, 20, 10);

    private static KilnHarness Started()
    {
        var kiln = new KilnHarness();
        kiln.Queries.Voxels = _ => FakeVoxelAnswer.Clear;
        kiln.Step(2);
        return kiln;
    }

    [Fact]
    public void An_unscripted_ray_is_unavailable_and_costs_no_allowance()
    {
        using var kiln = new KilnHarness();
        kiln.Step(2);
        Assert.Equal("queries.voxel_ray", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.RaycastVoxels(A, B))).Capability);
        Assert.Equal("queries.ground", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.GroundBelow(A))).Capability);
        Assert.Empty(kiln.Queries.Rays);
        kiln.Queries.MaxRaysPerStep = 1;
        kiln.Queries.Voxels = _ => FakeVoxelAnswer.Clear;
        Assert.True(kiln.Run(() => Queries.GroundBelow(A)).Ok);
        Assert.Single(kiln.Queries.Rays);
        kiln.Queries.Voxels = null;
        Assert.Equal("queries.ground", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.GroundBelow(A))).Capability);
        Assert.True(kiln.Run(() => Queries.EntitiesInSphere(A, 1)).Ok);
    }

    [Fact]
    public unsafe void Unmodelled_paths_answer_unavailable()
    {
        using var kiln = Started();
        kiln.Run(() =>
        {
            var t = QueriesNative.T;
            Assert.Equal(QueriesNative.Unavailable, t->PathSubmit(1, null, null));
            Assert.Equal(QueriesNative.Unavailable, t->PathCancel(1, 1));
            Assert.Equal(QueriesNative.Unavailable, t->PathPoll(1, null, null, 0, null));
            Assert.Equal(QueriesNative.Unavailable, t->PathRetire(1));
        });
    }

    [Theory]
    [InlineData("kiln.world")]
    [InlineData("world.entities")]
    [InlineData("world.find")]
    public void A_dependency_lost_between_refreshes_refuses_by_the_queries_members_row(string dependency)
    {
        using var kiln = Started();
        var a = kiln.Entities.Create(new WorldPosition(0, 10, 0));
        var b = kiln.Entities.Create(new WorldPosition(20, 10, 0));
        Assert.True(kiln.Run(() => Queries.VoxelLineOfSight(a, b)).Ok);
        Assert.True(kiln.Run(() => Queries.EntitiesInSphere(A, 1)).Ok);
        kiln.Run(() => Capabilities.Report(dependency, CapabilityState.Unavailable, CapabilityReason.Faulted, "dependency lost between refreshes"));
        try
        {
            if (dependency != "world.find")
                Assert.Equal("queries.line_of_sight", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.VoxelLineOfSight(a, b))).Capability);
            Assert.Equal("queries.overlap_origins", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.EntitiesInSphere(A, 1))).Capability);
            // The ray itself does not use kiln.world's entity functions.
            Assert.True(kiln.Run(() => Queries.GroundBelow(A)).Ok);
        }
        finally { kiln.Run(() => Capabilities.Report(dependency, CapabilityState.Available)); }
        Assert.True(kiln.Run(() => Queries.VoxelLineOfSight(a, b)).Ok);
        Assert.True(kiln.Run(() => Queries.EntitiesInSphere(A, 1)).Ok);
    }

    [Fact]
    public void A_hit_a_loaded_miss_and_a_refusal_are_three_answers()
    {
        using var kiln = Started();
        var clear = kiln.Run(() => Queries.RaycastVoxels(A, B));
        Assert.Equal((true, null, null), (clear.Ok, clear.Reason, clear.Hit));

        kiln.Queries.Voxels = _ => FakeVoxelAnswer.Hit(new WorldPosition(10, 30, 10), material: 0x2f2607aa, voxel: 0x6c01);
        var hit = kiln.Run(() => Queries.RaycastVoxels(A, B));
        Assert.True(hit.Ok);
        Assert.Null(hit.Reason);
        Assert.Equal(new VoxelRayHit(new WorldPosition(10, 30, 10), 20, 0x2f2607aa, 0x6c01), hit.Hit);

        foreach (var reason in new[] { "NotLoaded", "OutOfGrid" })
        {
            kiln.Queries.Voxels = _ => FakeVoxelAnswer.Refused(reason);
            var refused = kiln.Run(() => Queries.RaycastVoxels(A, B));
            Assert.Equal((false, reason, null), (refused.Ok, refused.Reason, refused.Hit));
        }
        Assert.Equal(4, kiln.Queries.Rays.Count);
        Assert.Equal(new FakeVoxelRay(A, B, 0), kiln.Queries.Rays[0]);
    }

    [Fact]
    public void Ground_is_a_downward_ray_of_the_length_asked_for()
    {
        using var kiln = Started();
        kiln.Queries.Voxels = r => FakeVoxelAnswer.Hit(new WorldPosition(r.From.X, 42, r.From.Z));
        var ground = kiln.Run(() => Queries.GroundBelow(A, maxDown: 25, realm: 3));
        Assert.Equal(new FakeVoxelRay(A, new WorldPosition(10, 25, 10), 3), Assert.Single(kiln.Queries.Rays));
        Assert.Equal(8, ground.Hit!.Distance);
        foreach (var bad in new[] { 0, -1, 256.5, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.GroundBelow(A, bad)));
        Assert.True(kiln.Run(() => Queries.GroundBelow(A, 256)).Ok);
        Assert.Equal(2, kiln.Queries.Rays.Count);   // the refused numbers never reached the service
    }

    [Fact]
    public void Numbers_that_are_no_ray_throw_and_use_no_budget()
    {
        using var kiln = Started();
        var nan = new WorldPosition(double.NaN, 0, 0);
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RaycastVoxels(A, A)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RaycastVoxels(A, nan)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RaycastVoxels(new WorldPosition(0, 0, 0), new WorldPosition(256.01, 0, 0))));
        // two doubles that are one point as the game's floats
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RaycastVoxels(new WorldPosition(1000, 0, 0), new WorldPosition(1000.000001, 0, 0))));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RaycastVoxels(new WorldPosition(1e300, 0, 0), new WorldPosition(1e300, 1, 0))));
        Assert.Empty(kiln.Queries.Rays);
        Assert.True(kiln.Run(() => Queries.RaycastVoxels(new WorldPosition(0, 0, 0), new WorldPosition(256, 0, 0))).Ok);
    }

    [Fact]
    public void The_number_rule_names_what_is_wrong()
    {
        Assert.Equal(RayNumbers.Ok, RayRules.Check(A, B));
        Assert.Equal(RayNumbers.Ok, RayRules.Check(B, A));
        Assert.Equal(RayNumbers.Degenerate, RayRules.Check(A, A));
        Assert.Equal(RayNumbers.Degenerate, RayRules.Check(new WorldPosition(1000, 0, 0), new WorldPosition(1000.000001, 0, 0)));
        Assert.Equal(RayNumbers.TooLong, RayRules.Check(new WorldPosition(0, 0, 0), new WorldPosition(0, 300, 0)));
        Assert.Equal(RayNumbers.NotFinite, RayRules.Check(A, new WorldPosition(0, double.PositiveInfinity, 0)));
        Assert.Equal(RayNumbers.NotFinite, RayRules.Check(new WorldPosition(1e300, 0, 0), new WorldPosition(1e300, 1, 0)));
        Assert.Equal(1e-20f, RayRules.MinLength);
    }

    [Fact]
    public void The_budget_refuses_and_starts_again_with_the_step()
    {
        using var kiln = Started();
        kiln.Queries.MaxRaysPerStep = 3;
        var answers = kiln.Run(() => Enumerable.Range(0, 5).Select(_ => Queries.RaycastVoxels(A, B)).ToList());
        Assert.Equal([true, true, true, false, false], answers.Select(a => a.Ok));
        Assert.All(answers.Skip(3), a => Assert.Equal("BudgetExceeded", a.Reason));
        // ground and sight share it
        Assert.Equal("BudgetExceeded", kiln.Run(() => Queries.GroundBelow(A)).Reason);
        kiln.Step();
        Assert.True(kiln.Run(() => Queries.GroundBelow(A)).Ok);
        Assert.Equal(4, kiln.Queries.Rays.Count);
    }

    [Fact]
    public void Sight_is_a_ray_between_offset_positions_and_a_gone_entity_is_not_hidden()
    {
        using var kiln = Started();
        var a = kiln.Entities.Create(new WorldPosition(0, 10, 0));
        var b = kiln.Entities.Create(new WorldPosition(20, 10, 0));
        var open = kiln.Run(() => Queries.VoxelLineOfSight(a, b));
        Assert.Equal((true, null, true, null), (open.Ok, open.Reason, open.Visible, open.Obstruction));
        Assert.Equal(new FakeVoxelRay(new WorldPosition(0, 11.5, 0), new WorldPosition(20, 11.5, 0), 0), kiln.Queries.Rays[0]);

        kiln.Queries.Voxels = _ => FakeVoxelAnswer.Hit(new WorldPosition(5, 11.5, 0));
        var wall = kiln.Run(() => Queries.VoxelLineOfSight(a, b, new VoxelSightOptions { FromOffset = new(0, 0.5, 0), ToOffset = new(0, 2, 0), Realm = 1 }));
        Assert.True(wall.Ok);
        Assert.False(wall.Visible);
        Assert.Equal(new WorldPosition(5, 11.5, 0), wall.Obstruction!.Position);
        Assert.Equal(new FakeVoxelRay(new WorldPosition(0, 10.5, 0), new WorldPosition(20, 12, 0), 1), kiln.Queries.Rays[1]);

        kiln.Queries.Voxels = _ => FakeVoxelAnswer.Refused("NotLoaded");
        var unknown = kiln.Run(() => Queries.VoxelLineOfSight(a, b));
        Assert.Equal((false, "NotLoaded", false), (unknown.Ok, unknown.Reason, unknown.Visible));

        kiln.Entities.Destroy(b);
        var gone = kiln.Run(() => Queries.VoxelLineOfSight(a, b));
        Assert.Equal((false, "EntityGone", false), (gone.Ok, gone.Reason, gone.Visible));
        Assert.Equal(3, kiln.Queries.Rays.Count);   // no ray for an entity that is not there
        // one entity is no segment
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.VoxelLineOfSight(a, a)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.VoxelLineOfSight(a, a, new VoxelSightOptions { FromOffset = new(double.NaN, 0, 0) })));
    }

    [Fact]
    public void Overlaps_take_origins_on_the_closed_shape_nearest_first()
    {
        using var kiln = Started();
        var centre = new WorldPosition(-100, 5, -100);
        var near = kiln.Entities.Create(new WorldPosition(-100, 5, -99));
        var edge = kiln.Entities.Create(new WorldPosition(-103, 5, -100));      // exactly 3 m
        var tie = kiln.Entities.Create(new WorldPosition(-100, 8, -100));       // exactly 3 m too: a higher id, so after
        var outside = kiln.Entities.Create(new WorldPosition(-103.001, 5, -100));
        var sphere = kiln.Run(() => Queries.EntitiesInSphere(centre, 3));
        Assert.True(sphere.Ok);
        Assert.Null(sphere.Reason);
        Assert.Equal([near, edge, tie], sphere.Entities);
        Assert.Equal((3, false), (sphere.Total, sphere.Truncated));
        Assert.DoesNotContain(outside, sphere.Entities);

        Assert.Equal([edge, tie], kiln.Run(() => Queries.EntitiesInSphere(centre, 3, exclude: near)).Entities);
        var cut = kiln.Run(() => Queries.EntitiesInSphere(centre, 3, max: 2));
        Assert.Equal([near, edge], cut.Entities);
        Assert.Equal((3, true), (cut.Total, cut.Truncated));

        var box = kiln.Run(() => Queries.EntitiesInBox(centre, new WorldPosition(3, 1, 1)));
        Assert.Equal([near, edge], box.Entities);   // tie is 3 m up: outside a box 1 m high
        kiln.Entities.Destroy(edge);
        Assert.Equal([near], kiln.Run(() => Queries.EntitiesInBox(centre, new WorldPosition(3, 1, 1))).Entities);

        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.EntitiesInSphere(centre, -1)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.EntitiesInSphere(centre, double.NaN)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.EntitiesInSphere(centre, 3, max: 0)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.EntitiesInSphere(centre, 3, max: 4097)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.EntitiesInBox(centre, new WorldPosition(1, -1, 1))));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.EntitiesInBox(new WorldPosition(0, double.NaN, 0), new WorldPosition(1, 1, 1))));
    }

    [Fact]
    public void A_row_that_is_off_throws_by_its_own_name()
    {
        using var kiln = Started();
        var a = kiln.Entities.Create(new WorldPosition(0, 10, 0));
        var b = kiln.Entities.Create(new WorldPosition(20, 10, 0));
        Assert.True(kiln.Run(() => Capabilities.Is("queries.ground")));
        kiln.Capabilities.Override("queries.voxel_ray", false, "switched off by the test");
        kiln.Step(30);   // the rows built on the ray follow at the service's refresh
        Assert.Equal("queries.voxel_ray", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.RaycastVoxels(A, B))).Capability);
        Assert.Equal("queries.ground", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.GroundBelow(A))).Capability);
        Assert.Equal("queries.line_of_sight", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.VoxelLineOfSight(a, b))).Capability);
        Assert.Empty(kiln.Queries.Rays);
        // origins need no ray
        Assert.True(kiln.Run(() => Queries.EntitiesInSphere(new WorldPosition(0, 10, 0), 1)).Ok);
        // bad numbers are still an argument error, whatever is off
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RaycastVoxels(A, A)));
        // the service itself says no (its table is switched off): the member's own row is named
        kiln.Capabilities.Override("kiln.queries", false, "switched off by the test");
        Assert.Equal("queries.overlap_origins", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.EntitiesInSphere(A, 1))).Capability);
    }

    [Fact]
    public void A_composed_row_reported_before_the_rays_row_was_there_does_not_outlive_it()
    {
        // What a gate run hit: Kiln.Queries' rows were reported under a harness's new registry before the kit's
        // queries.voxel_ray row was visible, so ground, sight and origins read "kiln.queries is not loaded" for the next
        // 25 steps although everything was there. The rows are planted so here; the members decide from what is true now.
        using var kiln = Started();
        var a = kiln.Entities.Create(new WorldPosition(0, 10, 0));
        var b = kiln.Entities.Create(new WorldPosition(20, 10, 0));
        kiln.Run(() =>
        {
            foreach (var row in new[] { "queries.ground", "queries.line_of_sight", "queries.overlap_origins" })
                ServiceCaps.Static(row, false, CapabilityReason.ServiceMissing, "kiln.queries is not loaded");
            Assert.False(Capabilities.Is("queries.ground"));
        });
        Assert.True(kiln.Run(() => Queries.GroundBelow(A)).Ok);
        kiln.Run(() => ServiceCaps.Static("queries.line_of_sight", false, CapabilityReason.ServiceMissing, "kiln.queries is not loaded"));
        Assert.True(kiln.Run(() => Queries.VoxelLineOfSight(a, b)).Ok);
        kiln.Run(() => ServiceCaps.Static("queries.overlap_origins", false, CapabilityReason.ServiceMissing, "kiln.queries is not loaded"));
        Assert.True(kiln.Run(() => Queries.EntitiesInSphere(A, 1)).Ok);
        Assert.True(kiln.Run(() => Capabilities.Is("queries.ground") && Capabilities.Is("queries.line_of_sight") && Capabilities.Is("queries.overlap_origins")));
        // and a row that is really off still refuses: the ray's row switched off by the test
        kiln.Capabilities.Override("queries.voxel_ray", false, "switched off by the test");
        Assert.Equal("queries.ground", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.GroundBelow(A))).Capability);
    }

    [Fact]
    public void Off_the_tick_thread_is_refused()
    {
        using var kiln = Started();
        Assert.Throws<InvalidOperationException>(() => Queries.RaycastVoxels(A, B));
        Assert.Throws<InvalidOperationException>(() => Queries.EntitiesInSphere(A, 1));
    }

    [Fact]
    public unsafe void The_mirrors_are_the_headers_sizes()
    {
        Assert.Equal(56, sizeof(QueriesNative.Table));
        Assert.Equal(56, sizeof(QueriesNative.RayRequest));
        Assert.Equal(72, sizeof(QueriesNative.RayResult));
        Assert.Equal(72, sizeof(QueriesNative.EntityRequest));
        Assert.Equal(32, sizeof(QueriesNative.EntityResult));
    }
}
