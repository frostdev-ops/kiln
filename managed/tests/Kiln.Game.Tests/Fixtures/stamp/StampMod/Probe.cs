// SPDX-License-Identifier: MIT
// Reads a Lamp component out of a buffer through the generated struct, the way a mod reads one: by field name, bound
// when this code is compiled at run time against whichever Kiln.Game is loaded. Names Lamp.glow, wick, mode, flags and
// core, and LampCore.heat and ticks (a nested-struct field chain); never Lamp.spare, Torch or Glimmer_Hue.
using System.Globalization;
using System.Runtime.InteropServices;
using Kiln;

namespace StampMod;

public static class Probe
{
    public static string Read(byte[] buffer)
    {
        var lamp = MemoryMarshal.Read<Ecs.Lamp>(buffer);
        return string.Create(CultureInfo.InvariantCulture,
            $"glow={lamp.glow} wick={lamp.wick} mode={lamp.mode} flags={(byte)lamp.flags} heat={lamp.core.heat} ticks={lamp.core.ticks} bright={IsBright(lamp)}");
    }

    static bool IsBright(Ecs.Lamp lamp) => lamp.mode == Ecs.Glimmer_Mode.Bright;
}
