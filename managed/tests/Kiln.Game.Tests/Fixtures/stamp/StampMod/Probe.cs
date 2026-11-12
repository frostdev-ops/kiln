// SPDX-License-Identifier: MIT
// Reads a Lamp and a Glow component out of a buffer through the generated structs, the way a mod reads them: by member
// name, bound when this code is compiled at run time against whichever Kiln.Game is loaded. Names Lamp.glow, wick, mode,
// flags and core, LampCore.heat and ticks (a nested-struct field chain), WickId's implicit conversion to uint (a
// generated method, not its field) and Glow's Values view (a generated method); never Lamp.spare or Torch.
// Glimmer_Hue appears only in a constant folded to a byte: no enum reference survives in the metadata.
using System.Globalization;
using System.Runtime.InteropServices;
using Kiln;

namespace StampMod;

public static class Probe
{
    public static string Read(byte[] buffer)
    {
        var lamp = MemoryMarshal.Read<Ecs.Lamp>(buffer);
        uint wick = lamp.wick;
        return string.Create(CultureInfo.InvariantCulture,
            $"glow={lamp.glow} wick={wick} mode={lamp.mode} flags={(byte)lamp.flags} heat={lamp.core.heat} ticks={lamp.core.ticks} bright={IsBright(lamp)}");
    }

    public static string ReadGlow(byte[] buffer)
    {
        var glow = MemoryMarshal.Read<Ecs.Glow>(buffer);
        var values = glow.Values;
        return string.Create(CultureInfo.InvariantCulture, $"values={values[0]},{values[1]}");
    }

    static bool IsBright(Ecs.Lamp lamp) => lamp.mode == Ecs.Glimmer_Mode.Bright;

    // Roslyn emits only the number 1, not a reference to Glimmer_Hue: intentionally not stampable from metadata.
    public static bool IsBlue(byte hue) => hue == (byte)Ecs.Glimmer_Hue.Blue;
}
