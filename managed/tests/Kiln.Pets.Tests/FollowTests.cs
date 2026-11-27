// SPDX-License-Identifier: MIT
// Package 4 (19d §4.3 modes, §4.3a): follow walks walk-only routes and stops inside its distance, runs past RunDistance,
// never writes without the broker's authority; no measured clearance means catch-up only; a far or stuck pet is put next to
// its owner on validated ground (or stops when its definition says so); stay holds with zero input; EngineGuard hands the
// walk to the game's guard; attack hands the body to the AI and returns to follow when the target ends. Placement and route
// acceptance rules are checked on their own.
using System.Numerics;
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

internal sealed class FakeMover : IPetMover
{
    public bool Drive = true, Fight;
    public readonly Dictionary<uint, WorldPosition> At = [];
    public readonly List<(Vector3 Move, uint Speed)> Writes = [];
    public readonly List<(WorldPosition From, WorldPosition To)> Requests = [];
    public readonly List<WorldPosition> Teleports = [];
    public readonly List<EntityId> Guards = [], Attacks = [];
    public Func<WorldPosition, WorldPosition, PetRoute?> Router = (a, b) => new PetRoute([new WorldPosition((a.X + b.X) / 2, a.Y, (a.Z + b.Z) / 2), b]);
    public bool Ground = true, TargetAlive = true, TeleportMoves = true;
    public int Pending;   // steps a path request stays pending

    private sealed class Req(WorldPosition from, WorldPosition to, int pending)
    {
        public readonly WorldPosition From = from, To = to;
        public int Left = pending;
    }

    public bool CanDrive(PetBody body) => Drive;
    public bool Fighting(PetBody body) => Fight;
    public WorldPosition? Position(PetBody body) => At.TryGetValue(body.Entity.Id.Value, out var p) ? p : null;
    public void Write(PetBody body, Vector3 move, Vector3 look, uint speed) => Writes.Add((move, speed));

    public object? RequestPath(PetBody body, WorldPosition from, WorldPosition to, PetClearanceProfile clearance)
    {
        Requests.Add((from, to));
        return new Req(from, to, Pending);
    }

    public (PetRoute? Route, string? Refusal)? PollPath(object request)
    {
        var r = (Req)request;
        if (r.Left-- > 0) return null;
        var route = Router(r.From, r.To);
        return route is null ? (null, "NoRoute: none") : (route, null);
    }

    public void CancelPath(object request) { }

    public bool Teleport(PetBody body, WorldPosition to)
    {
        Teleports.Add(to);
        if (TeleportMoves) At[body.Entity.Id.Value] = to;
        return true;
    }

    public WorldPosition? SafeGround(WorldPosition near, double min, double max, PetDefinition def, EntityId owner, EntityId pet) => Ground ? new WorldPosition(near.X + 2, near.Y, near.Z) : null;
    public void Guard(PetBody body, EntityId owner) => Guards.Add(owner);
    public void Attack(PetBody body, EntityId target) => Attacks.Add(target);
    bool IPetMover.TargetAlive(EntityRef target) => TargetAlive;
}

public sealed class FollowTests : IDisposable
{
    private readonly FakeMover _m = new();
    private readonly PetBody _body = new(new EntityRef(new EntityId(3000), 1), 1, 1);
    private readonly PetState _pet;
    private readonly PetFollower _f = new();
    private PetDefinition _def = new() { Id = "mod-a:wolf", Creature = "game:enemy_wildbeast_wolf" };
    private PetOwner _owner = new(new CharacterKey(1, Guid.NewGuid()), new EntityRef(new EntityId(1), 1), new WorldPosition(20, 0, 0), 10, 1);
    private double _now;

    public FollowTests()
    {
        PetClearance.ResetForTests();
        PetFollower.ResetForTests();
        PetClearance.SetForTests("game:enemy_wildbeast_wolf", 1, new PetClearanceProfile(1, 2));
        _pet = new PetState { Id = new PetId(Guid.NewGuid()), Body = _body, Presence = PetPresence.Live, Life = PetLife.Ready };
        _m.At[3000] = new WorldPosition(0, 0, 0);
    }

    public void Dispose()
    {
        PetClearance.ResetForTests();
        PetFollower.ResetForTests();
    }

    private void Step(int n = 1)
    {
        for (var i = 0; i < n; i++)
        {
            _f.Step(_pet, _def, _owner, _m, _now);
            _now += 1 / 60.0;
        }
    }

    [Fact]
    public void Follow_walks_a_walk_only_route_toward_its_owner_and_stops_inside_its_distance()
    {
        Step(2);
        Assert.Single(_m.Requests);
        var (move, speed) = _m.Writes[^1];
        Assert.Equal(PetFollower.Run, speed);   // 20 m > RunDistance 10
        Assert.True(move.X > 0.9f);
        _m.At[3000] = new WorldPosition(14, 0, 0);
        _m.Writes.Clear();
        Step();
        Assert.Equal(PetFollower.Walk, _m.Writes[^1].Speed);   // 6 m: walking
        _m.At[3000] = new WorldPosition(18, 0, 0);
        Step();
        Assert.Equal((Vector3.Zero, PetFollower.Stop), _m.Writes[^1]);   // inside FollowDistance 3: stopped
    }

    [Fact]
    public void Nothing_is_written_without_the_brokers_authority()
    {
        _m.Drive = false;   // Pending, or Preempted by a rider
        Step(10);
        Assert.Empty(_m.Writes);
        Assert.Empty(_m.Teleports);
        _m.Drive = true;
        Step(2);
        Assert.NotEmpty(_m.Writes);
    }

    [Fact]
    public void Without_a_measured_clearance_the_pet_never_walks_a_route_and_catches_up_instead()
    {
        PetClearance.SetForTests("game:enemy_wildbeast_wolf", 1, null);
        Step(5);
        Assert.Empty(_m.Requests);
        Assert.StartsWith("ClearanceMappingUnverified", _f.Limited);
        Assert.All(_m.Writes, w => Assert.Equal(PetFollower.Stop, w.Speed));
        _owner = _owner with { Position = new WorldPosition(100, 0, 0) };
        Step();
        Assert.Single(_m.Teleports);
        Assert.Equal(new WorldPosition(102, 0, 0), _m.Teleports[0]);
    }

    [Fact]
    public void A_far_pet_is_put_next_to_its_owner_and_its_spot_is_checked_after_it_settles()
    {
        _owner = _owner with { Position = new WorldPosition(100, 0, 0) };
        Step();
        Assert.Single(_m.Teleports);
        Assert.Equal(1, _f.CatchUps);
        Step(40);   // settled within 1 m after 0.5 s: following again
        Assert.Null(_f.Limited);
    }

    [Fact]
    public void A_pet_that_does_not_settle_where_it_was_put_stops_and_never_jumps_again_unchecked()
    {
        _m.TeleportMoves = false;
        _owner = _owner with { Position = new WorldPosition(100, 0, 0) };
        Step(400);
        Assert.Single(_m.Teleports);
        Assert.StartsWith("CatchUpUnsettled", _f.Limited);
    }

    [Fact]
    public void Catch_up_is_the_mods_choice()
    {
        _def = _def with { Movement = new PetMovementPolicy { AllowCatchUp = false } };
        _owner = _owner with { Position = new WorldPosition(100, 0, 0) };
        Step(10);
        Assert.Empty(_m.Teleports);
    }

    [Fact]
    public void No_safe_ground_means_no_catch_up()
    {
        _m.Ground = false;
        _owner = _owner with { Position = new WorldPosition(100, 0, 0) };
        Step();
        Assert.Empty(_m.Teleports);
        Assert.StartsWith("NoSafeGround", _f.Limited);
    }

    [Fact]
    public void A_stuck_pet_catches_up_after_its_stuck_seconds()
    {
        Step(2);   // the route comes, it starts walking; the body never moves
        Step(60 * 5 + 5);
        Assert.Single(_m.Teleports);
    }

    [Fact]
    public void No_route_for_its_stuck_seconds_catches_up()
    {
        _m.Router = (_, _) => null;
        Step(60 * 5 + 10);
        Assert.Single(_m.Teleports);
    }

    [Fact]
    public void A_moving_owner_gets_a_new_route_past_two_metres()
    {
        Step(2);
        Assert.Single(_m.Requests);
        _owner = _owner with { Position = new WorldPosition(20, 0, 1) };
        Step();
        Assert.Single(_m.Requests);   // 1 m: the route stands (until RepathSeconds)
        _owner = _owner with { Position = new WorldPosition(20, 0, 5) };
        Step();
        Assert.Equal(2, _m.Requests.Count);
    }

    [Fact]
    public void Stay_holds_with_zero_input_and_asks_for_no_route()
    {
        _pet.Order = PetOrder.Stay;
        Step(5);
        Assert.Empty(_m.Requests);
        Assert.All(_m.Writes, w => Assert.Equal((Vector3.Zero, PetFollower.Stop), w));
    }

    [Fact]
    public void Engine_guard_hands_the_walk_to_the_games_guard()
    {
        _def = _def with { Movement = new PetMovementPolicy { Mode = PetMovementMode.EngineGuard } };
        Step(10);
        Assert.Equal([new EntityId(1)], _m.Guards);
        Assert.Empty(_m.Writes);
        Assert.Empty(_m.Requests);
    }

    [Fact]
    public void Attack_hands_the_body_to_the_AI_once_combat_holds_it_and_ends_with_its_target()
    {
        _pet.Order = PetOrder.Attack;
        _pet.Target = new EntityRef(new EntityId(4000), 9);
        Step(3);
        Assert.Empty(_m.Attacks);   // the PetCombat lease is not the winner yet
        _m.Fight = true;
        Step(3);
        Assert.Equal([new EntityId(4000)], _m.Attacks);
        Assert.Empty(_m.Writes);
        _m.TargetAlive = false;
        Step();
        Assert.True(_f.TargetEnded);
    }

    [Fact]
    public void At_most_four_route_requests_are_in_flight()
    {
        _m.Pending = 1000;
        var followers = Enumerable.Range(0, 6).Select(_ => new PetFollower()).ToList();
        foreach (var f in followers) f.Step(_pet, _def, _owner, _m, 0);
        Assert.Equal(PetFollower.MaxInFlight, _m.Requests.Count);
    }

    [Fact]
    public void Only_a_found_walk_only_route_that_reaches_its_target_is_walked()
    {
        QueryPathResult R(QueryPathStatus s, bool reaches, params byte[] moves) =>
            new(s is QueryPathStatus.Found or QueryPathStatus.Partial, null, 1, 1, s, 0, reaches, moves.Select(m => new QueryPathPoint(new WorldPosition(0, 0, 0), m)).ToList(), 1, 1);
        Assert.NotNull(GamePetMover.Accept(R(QueryPathStatus.Found, true, 0, 0)).Route);
        Assert.Null(GamePetMover.Accept(R(QueryPathStatus.Found, true)).Route);              // code 0 with no points is no route
        Assert.Null(GamePetMover.Accept(R(QueryPathStatus.Partial, false, 0, 0)).Route);     // never a partial route
        Assert.Null(GamePetMover.Accept(R(QueryPathStatus.Found, false, 0, 0)).Route);
        Assert.Null(GamePetMover.Accept(R(QueryPathStatus.Found, true, 0, 2)).Route);        // a jump
        Assert.Null(GamePetMover.Accept(R(QueryPathStatus.Found, true, 0, 5)).Route);        // a door
        Assert.Null(GamePetMover.Accept(R(QueryPathStatus.Found, true, 0, 1)).Route);        // flying
    }

    [Fact]
    public void A_spot_needs_ground_everywhere_under_it_a_flat_floor_a_clear_column_and_no_one_on_it()
    {
        PetPlacement.Samples S(double? c, double?[] foot, bool clear = true, int n = 0) => new(c, foot, clear, n);
        Assert.True(PetPlacement.Accept(S(1, [1, 1.2, 0.9, 1.4])));
        Assert.False(PetPlacement.Accept(S(null, [1, 1, 1, 1])));
        Assert.False(PetPlacement.Accept(S(1, [1, null, 1, 1])));
        Assert.False(PetPlacement.Accept(S(1, [1, 1, 1, 1.6])));   // a step of 0.6 m
        Assert.False(PetPlacement.Accept(S(1, [1, 1, 1, 1], clear: false)));
        Assert.False(PetPlacement.Accept(S(1, [1, 1, 1, 1], n: 1)));
    }

    [Fact]
    public void Candidates_go_front_first_and_are_at_most_twenty_four()
    {
        var c = PetPlacement.Candidates(new WorldPosition(0, 0, 0), 2, 4).ToList();
        Assert.Equal(24, c.Count);
        Assert.Equal(new WorldPosition(0, 0, 2), c[0]);
        Assert.All(c, p => Assert.InRange(Math.Sqrt(p.X * p.X + p.Z * p.Z), 1.99, 4.01));
        var anchor = PetPlacement.Candidates(new WorldPosition(5, 0, 5), 0, 3).First();
        Assert.Equal(new WorldPosition(5, 0, 5), anchor);
    }
}

public sealed class RosterFollowTests : IDisposable
{
    private readonly RosterTests _t = new();
    private readonly FakeMover _m = new();

    public RosterFollowTests()
    {
        PetClearance.ResetForTests();
        PetFollower.ResetForTests();
        PetClearance.SetForTests("game:enemy_wildbeast_wolf", 1, new PetClearanceProfile(1, 2));
        _t.Roster.Mover = _m;
        _t.Roster.TargetCheck = (_, _) => null;
    }

    public void Dispose()
    {
        PetClearance.ResetForTests();
        _t.Dispose();
    }

    [Fact]
    public void A_live_pets_follower_runs_each_step_and_an_ended_target_puts_it_back_on_follow()
    {
        var p = _t.Summoned(_t.A);
        foreach (var (id, b) in _t.World.Bodies) _m.At[id] = b.Position;
        _t.Step(3);
        Assert.NotEmpty(_m.Writes);
        var target = new EntityRef(new EntityId(4321), 5);
        var r = _t.Run(_t.Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Command, OperationId = Guid.NewGuid(), OwnerKey = _t.A, Who = _t.Who(_t.A), Pet = p.Id, Order = PetOrder.Attack, Target = target, Payload = "attack" }));
        Assert.True(r.Ok, r.Reason);
        _t.Names();
        _m.Fight = true;
        _t.Step(2);
        Assert.Contains(new EntityId(4321), _m.Attacks);
        _m.TargetAlive = false;
        _t.Step();
        Assert.Equal(PetOrder.Follow, _t.Roster.Get(p.Id)!.RequestedOrder);
        Assert.Equal(["CommandChanged"], _t.Names());
        Assert.Contains(_t.World.Orders, o => o.Order == PetOrder.Follow);
    }
}
