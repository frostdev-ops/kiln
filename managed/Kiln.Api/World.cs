// SPDX-License-Identifier: MIT
using System.Numerics;

namespace Kiln;

/// <summary>A server-world entity id. Ids can be reused after an entity is destroyed. On a dedicated server player
/// entities are slot + 1 (1..16 with 16 slots); <see cref="IsPlayer"/> tests the engine's reserved range 1..1023.</summary>
public readonly record struct EntityId(uint Value)
{
    public static readonly EntityId None = default;
    public bool IsNone => Value == 0;
    public bool IsPlayer => Value is >= 1 and <= 1023;
    public override string ToString() => Value.ToString();
}

/// <summary>A world position in metres (x east, y up, z north). The engine stores 32.32 fixed point.</summary>
public readonly record struct WorldPosition(double X, double Y, double Z)
{
    public static WorldPosition From(Vector3 v) => new(v.X, v.Y, v.Z);
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    public double DistanceTo(WorldPosition o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
    public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
}
