// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using Kiln;

namespace NpcSample;

// The second half of the sample: a creature with a brain of its own that walks a route (docs/kiln/modding/npc-blocks.md,
// docs/kiln/services/queries.md "Paths"). NpcControl.MoveTo walks straight at a point; the game's path search knows the
// way around a wall. The brain asks Queries.FindPath for a route, keeps the copied points and walks them one after
// another with MoveTo. In game: /npcsample walker (one appears 12 m away and walks to where you stand),
// /npcsample walkto (the walkers come to where you stand now).
public sealed partial class NpcSampleMod
{
    /// <summary>Where each walker is asked to go. Commands write it, the brains read it; both on the tick thread.</summary>
    internal static readonly ConcurrentDictionary<EntityId, WorldPosition> Goals = new();

    public override void OnLoad() =>
        Blocks.Define("path-walker", new CreatureBlock
        {
            Name = "Path walker",
            Body = Bodies.Vukah,
            // no attacks, and its own mind: the game's AI is off for it
            Behaviour = Behaviours.None with { Brain = () => new PathWalkerBrain() },
        });

    [Command("npcsample walker", Description = "a creature 12 m from you that walks to where you stand, on a route from the game's path search",
        Permission = CommandPermission.Admin)]
    private async Task Walker(CommandContext c)
    {
        var player = PlayerOf(c);
        if (player.IsNone || World.Position(player) is not { } at) throw new CommandException("no player in game");
        var e = await Blocks.Spawn("path-walker", new WorldPosition(at.X + 12, at.Y, at.Z), 270);
        Goals[e] = at;
        c.Reply(Capabilities.Is("queries.path")
            ? $"walker {e} walks to ({at.X:F0}, {at.Y:F0}, {at.Z:F0})"
            : $"walker {e} stands: this server has no path search ({Capabilities.Why("queries.path")})");
    }

    [Command("npcsample walkto", Description = "the walkers come to where you stand now", Permission = CommandPermission.Admin)]
    private string WalkTo(CommandContext c)
    {
        var player = PlayerOf(c);
        if (player.IsNone || World.Position(player) is not { } at) throw new CommandException("no player in game");
        foreach (var walker in Goals.Keys) Goals[walker] = at;
        return $"{Goals.Count} walker(s) walk to ({at.X:F0}, {at.Y:F0}, {at.Z:F0})";
    }
}

/// <summary>
/// Walks to its goal on a route it asked the game's path search for. The route is a copy from the step the game
/// answered, so the brain treats it as out of date when the goal moved, when it is old, or when the creature is no
/// longer near it, and asks again. Without a route (the search is off, refused the question or found no way) it
/// stands still and asks again a little later: it never walks straight at the goal instead.
/// </summary>
internal sealed class PathWalkerBrain : NpcBrain
{
    private const double Reach = 0.7;        // metres from a point at which the next one is taken
    private const double Arrive = 1.5;       // metres from the goal at which it is there
    private const double GoalMoved = 2;      // the goal moved this far: the route leads somewhere else
    private const double Strayed = 4;        // this far from the leg it walks: something pushed it off the route
    private const double MaxAge = 10;        // seconds a route is trusted
    private const double RetryAfter = 3;     // seconds to stand after a refusal before asking again

    private Task<QueryPathResult>? _asking;  // the question that is out, if any
    private WorldPosition _askedFor;         // the goal it was asked for
    private IReadOnlyList<QueryPathPoint> _route = [];
    private int _next;                       // the point of _route it walks to
    private WorldPosition _routeGoal;
    private double _routeAt = double.NegativeInfinity, _askAgainAt;

    public override void Tick(NpcControl npc)
    {
        if (!NpcSampleMod.Goals.TryGetValue(npc.Entity, out var goal) || npc.Position is not { } here)
        {
            npc.Stop();
            return;
        }
        TakeAnswer(npc);
        if (Flat(here, goal) < Arrive)
        {
            _route = [];
            npc.Stop();
            return;
        }
        // a route that no longer fits is dropped before another step is taken on it
        var have = _next < _route.Count;
        if (have && (Flat(_routeGoal, goal) > GoalMoved || npc.Time - _routeAt > MaxAge || OffLeg(here) > Strayed))
        {
            _route = [];
            have = false;
        }
        if (!have)
        {
            npc.Stop();
            Ask(npc, here, goal);
            return;
        }
        if (Flat(here, _route[_next].Position) < Reach && ++_next >= _route.Count)
        {
            // the end of the route and not at the goal (a partial route, or the goal is off the walkable ground): ask again later
            _askAgainAt = npc.Time + RetryAfter;
            npc.Stop();
            return;
        }
        npc.MoveTo(_route[_next].Position);
    }

    public override void Stop(NpcControl npc) => NpcSampleMod.Goals.TryRemove(npc.Entity, out _);

    private void Ask(NpcControl npc, WorldPosition here, WorldPosition goal)
    {
        if (_asking != null || npc.Time < _askAgainAt) return;
        // the row first: FindPath throws KilnUnavailableException where the path search is off
        if (!Capabilities.Is("queries.path"))
        {
            _askAgainAt = npc.Time + RetryAfter;
            return;
        }
        // the creature is its own anchor: Blocks.Spawn made it, so it carries the spawn marker the search asks for.
        // The search does not move it.
        _askedFor = goal;
        _asking = Queries.FindPath(new QueryPathRequest(npc.Entity, here, goal) { ClosestFallback = true });
    }

    private void TakeAnswer(NpcControl npc)
    {
        if (_asking is not { IsCompleted: true } done) return;
        _asking = null;
        // Ok = false is an answer too (NoPath, QueueFull, TimedOut, NotLoadedOrUnknown, ...): stand, and ask again later.
        // Only walking moves are given with Ok; a route with a jump or a door in it comes back as UnsupportedTraversal.
        if (!done.IsCompletedSuccessfully || done.Result is not { Ok: true, Points.Count: > 0 } result)
        {
            _askAgainAt = npc.Time + RetryAfter;
            return;
        }
        _route = result.Points;
        _next = 0;
        _routeGoal = _askedFor;
        _routeAt = npc.Time;
    }

    // How far it is from the leg it walks: the line from the point before to the one it walks to. Points of a route can
    // lie metres apart, so the distance to the next point alone says nothing about leaving the route.
    private double OffLeg(WorldPosition here)
    {
        var to = _route[_next].Position;
        var from = _next > 0 ? _route[_next - 1].Position : to;
        double dx = to.X - from.X, dz = to.Z - from.Z, length2 = dx * dx + dz * dz;
        var t = length2 > 0 ? Math.Clamp(((here.X - from.X) * dx + (here.Z - from.Z) * dz) / length2, 0, 1) : 0;
        return Flat(here, new WorldPosition(from.X + t * dx, here.Y, from.Z + t * dz));
    }

    private static double Flat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
