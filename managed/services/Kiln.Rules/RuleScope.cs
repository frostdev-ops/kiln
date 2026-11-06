// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// Who a rule set applies to: one player, everyone, or the players inside an area (optionally only one of them). An
/// area is checked against the players' positions four times a second.
/// </summary>
public sealed record RuleScope
{
    private enum Kind { Player, Everyone, Sphere, Box, Cylinder }

    private Kind _kind;
    private WorldPosition _a, _b;
    private double _radius;

    private RuleScope() { }

    /// <summary>Only this player entity (a player's entity id goes to the next player who joins after they leave: a
    /// player rule set ends when its player leaves).</summary>
    public EntityId? Player { get; private init; }

    public bool IsArea => _kind is Kind.Sphere or Kind.Box or Kind.Cylinder;

    public static RuleScope ForPlayer(EntityId player) =>
        player.IsNone ? throw new ArgumentException("no player", nameof(player)) : new() { _kind = Kind.Player, Player = player };

    /// <summary>Every player in the world.</summary>
    public static RuleScope Everyone { get; } = new() { _kind = Kind.Everyone };

    public static RuleScope Sphere(WorldPosition center, double radius) => new() { _kind = Kind.Sphere, _a = center, _radius = Math.Max(radius, 0) };

    public static RuleScope Box(WorldPosition min, WorldPosition max) =>
        new()
        {
            _kind = Kind.Box,
            _a = new WorldPosition(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
            _b = new WorldPosition(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)),
        };

    /// <summary>An upright cylinder: <paramref name="radius"/> around <paramref name="center"/> (x, z), from
    /// <paramref name="minY"/> to <paramref name="maxY"/>.</summary>
    public static RuleScope Cylinder(WorldPosition center, double radius, double minY, double maxY) =>
        new() { _kind = Kind.Cylinder, _a = center with { Y = Math.Min(minY, maxY) }, _b = center with { Y = Math.Max(minY, maxY) }, _radius = Math.Max(radius, 0) };

    /// <summary>This area, for one player only.</summary>
    public RuleScope Only(EntityId player) => this with { Player = player };

    /// <summary>Whether the scope covers <paramref name="player"/> at <paramref name="at"/> (an area needs a position).</summary>
    public bool Covers(EntityId player, WorldPosition? at)
    {
        if (Player is { } only && only != player) return false;
        switch (_kind)
        {
            case Kind.Player:
            case Kind.Everyone:
                return true;
            case Kind.Sphere:
                return at is { } p && Sq(p.X - _a.X) + Sq(p.Y - _a.Y) + Sq(p.Z - _a.Z) <= _radius * _radius;
            case Kind.Box:
                return at is { } q && q.X >= _a.X && q.X <= _b.X && q.Y >= _a.Y && q.Y <= _b.Y && q.Z >= _a.Z && q.Z <= _b.Z;
            default:
                return at is { } c && c.Y >= _a.Y && c.Y <= _b.Y && Sq(c.X - _a.X) + Sq(c.Z - _a.Z) <= _radius * _radius;
        }
    }

    private static double Sq(double v) => v * v;

    public override string ToString() => _kind switch
    {
        Kind.Player => $"player {Player}",
        Kind.Everyone => "everyone",
        Kind.Sphere => $"sphere {_radius} m around {_a}" + Only(),
        Kind.Box => $"box {_a}..{_b}" + Only(),
        _ => $"cylinder {_radius} m around {_a.X:0.#}, {_a.Z:0.#} (y {_a.Y:0.#}..{_b.Y:0.#})" + Only(),
    };

    private string Only() => Player is { } p ? $" for player {p}" : "";
}
