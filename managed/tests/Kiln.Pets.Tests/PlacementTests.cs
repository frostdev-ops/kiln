// SPDX-License-Identifier: MIT
// 19d §4.3a placement as the game adapters ask for it: the owner stands at the middle of every ring, so its own entity
// never counts as one in the way (nor does the pet caught up); any other entity origin within 2.5 m still does. Review of
// dev.32 pets: GamePetWorld and GamePetMover asked without the owner, so a downed pet's anchor (0..3 m) never found a spot
// in a game and a revive close to its anchor failed at completion.
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

internal sealed class FlatProbe : IPlacementProbe
{
    public readonly Dictionary<EntityId, WorldPosition> At = [];
    public bool Ground => true;
    public bool Origins => true;
    public double? Floor(WorldPosition from, double down) => 0;
    public bool Clear(WorldPosition from, WorldPosition to) => true;

    public IReadOnlyList<EntityId>? Near(WorldPosition center, double radius, EntityId exclude) =>
        At.Where(kv => kv.Key != exclude && kv.Value.DistanceTo(center) <= radius).Select(kv => kv.Key).ToList();
}

public sealed class PlacementTests : IDisposable
{
    private readonly FlatProbe _probe = new();
    private readonly PetDefinition _def = new() { Id = "mod-a:wolf", Creature = "game:enemy_wildbeast_wolf" };
    private readonly EntityId _owner = new(1);
    private readonly WorldPosition _at = new(10, 0, 10);

    public PlacementTests()
    {
        PetPlacement.Probe = _probe;
        _probe.At[_owner] = _at;
    }

    public void Dispose() => PetPlacement.Probe = new PetPlacement.QueriesProbe();

    [Fact]
    public void The_owner_standing_there_never_blocks_its_pets_anchor_body_or_revive()
    {
        var world = new GamePetWorld();
        // a downed pet's anchor next to its owner (0..3 m): every candidate is within 2.5 m of the owner
        var anchor = world.SafeGround(_at, 0, 3, _def, null, _owner);
        Assert.NotNull(anchor);
        Assert.True(anchor.Value.DistanceTo(_at) <= 3);
        // a body 2..4 m away takes the first ring, 2 m, not the 3 m one past the owner's 2.5 m
        var body = world.SafeGround(_at, 2, 4, _def, null, _owner);
        Assert.NotNull(body);
        Assert.Equal(2, Math.Round(Math.Sqrt((body.Value.X - _at.X) * (body.Value.X - _at.X) + (body.Value.Z - _at.Z) * (body.Value.Z - _at.Z)), 3));
        // a revive's preview at the anchor, the owner standing 1 m from it
        var revive = world.SafeGround(new WorldPosition(_at.X + 1, 0, _at.Z), 0, 2, _def, null, _owner);
        Assert.NotNull(revive);
    }

    [Fact]
    public void Another_entity_there_still_takes_the_spot()
    {
        var world = new GamePetWorld();
        _probe.At[new EntityId(5000)] = _at;   // another player, a chest, a creature at the owner's feet
        Assert.Null(world.SafeGround(_at, 0, 3, _def, null, _owner));
    }

    [Fact]
    public void A_catch_up_spot_never_counts_the_owner_or_the_pet_itself()
    {
        var pet = new EntityId(3000);
        _probe.At[pet] = new WorldPosition(_at.X, 0, _at.Z + 2);   // the pet already next to the first spot
        var spot = new GamePetMover().SafeGround(_at, 2, 4, _def, _owner, pet);
        Assert.NotNull(spot);
        Assert.Equal(_at.Z + 2, spot.Value.Z, 3);   // the front spot of the first ring
    }
}
