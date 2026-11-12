// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using Kiln;

namespace EnumMetadata;

public static class Probe
{
    public static Ecs.Glimmer_Hue Field;

    public static byte Local(byte raw)
    {
        Ecs.Glimmer_Mode mode = (Ecs.Glimmer_Mode)raw;
        // Taking the address preserves a typed local even in a Release build.
        return Number(ref mode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static byte Number(ref Ecs.Glimmer_Mode mode) => (byte)mode;

    public static Type Token() => typeof(Ecs.Glimmer_ModeMask);
}
