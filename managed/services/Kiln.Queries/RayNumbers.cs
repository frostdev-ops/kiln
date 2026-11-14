// SPDX-License-Identifier: MIT
// The one rule for a voxel ray's numbers, as kiln.queries applies it (native/services/queries/ray.h): finite, longer than
// nothing and at most 256 m, also once the endpoints are the floats the game gets. Two distinct doubles can be one point
// as floats, and the game answers "loaded, no hit" for a segment without length: such a ray is refused, never asked.
// Kiln.Blocks' brain perception uses the same rule, so it cannot drift from the service's.
namespace Kiln;

internal enum RayNumbers
{
    Ok,
    /// <summary>A coordinate is not a finite number, as a double or as a float.</summary>
    NotFinite,
    /// <summary>Longer than <see cref="RayRules.MaxLength"/>.</summary>
    TooLong,
    /// <summary>No length: the same point, or the same point once narrowed to floats.</summary>
    Degenerate,
}

internal static class RayRules
{
    public const double MaxLength = 256;
    /// <summary>The game's own bound below which it looks at no segment (1e-20f).</summary>
    public static readonly float MinLength = BitConverter.UInt32BitsToSingle(0x1e3ce508);

    public static RayNumbers Check(WorldPosition from, WorldPosition to)
    {
        if (!Finite(from) || !Finite(to)) return RayNumbers.NotFinite;
        double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 0)) return RayNumbers.Degenerate;
        if (!(length <= MaxLength)) return double.IsFinite(length) ? RayNumbers.TooLong : RayNumbers.NotFinite;
        float fx = (float)from.X, fy = (float)from.Y, fz = (float)from.Z, tx = (float)to.X, ty = (float)to.Y, tz = (float)to.Z;
        if (!float.IsFinite(fx) || !float.IsFinite(fy) || !float.IsFinite(fz) || !float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz)) return RayNumbers.NotFinite;
        // the game's order: float multiply, add, square root
        float x = tx - fx, y = ty - fy, z = tz - fz;
        var narrow = MathF.Sqrt(x * x + y * y + z * z);
        if (!float.IsFinite(narrow)) return RayNumbers.NotFinite;
        if (!(narrow > MinLength)) return RayNumbers.Degenerate;
        return narrow <= (float)MaxLength ? RayNumbers.Ok : RayNumbers.TooLong;
    }

    public static bool Finite(WorldPosition p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    /// <summary>Throws <see cref="ArgumentException"/> for a ray the service does not take.</summary>
    public static void Require(WorldPosition from, WorldPosition to, string api)
    {
        switch (Check(from, to))
        {
            case RayNumbers.NotFinite: throw new ArgumentException($"{api}: {from} to {to} has a coordinate that is not a finite number");
            case RayNumbers.TooLong: throw new ArgumentException($"{api}: {from} to {to} is longer than {MaxLength} m");
            case RayNumbers.Degenerate: throw new ArgumentException($"{api}: {from} to {to} has no length (the same point, or the same point as the game's floats)");
        }
    }
}
