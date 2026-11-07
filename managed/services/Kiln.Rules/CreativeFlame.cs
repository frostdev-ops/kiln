// SPDX-License-Identifier: MIT
// Creative Mode (Nexus mod 146, a Flame mod) and Kiln's own creative mode in one game: the third-party mod hooks the same
// game systems (crafting and building payments, consumables, the map's fog, time of day, movement), so Kiln's creative
// features go idle while it runs. kiln-flame (a Kiln plugin, docs/kiln/engines/flame.md) publishes kiln.flame with
// running("creative"); without that plugin there is no service and Kiln's creative mode works as usual.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class CreativeFlame
{
    public const string Service = "kiln.flame";
    public const uint Version = 1;
    public const string ModId = "creative";

    /// <summary>What a refused creative request and <c>/creative status</c> say.</summary>
    public const string Note = "Creative Mode (the Flame mod \"creative\") runs in this game, so Kiln's creative mode stays idle: " +
                               "use Creative Mode's own menu";

    // mirror of native/plugins/flame/include/kiln_flame.h (kiln.flame v1)
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint> Count;
        public delegate* unmanaged[Cdecl]<uint, void*, int> Get;
        public delegate* unmanaged[Cdecl]<NativeStr, byte> Running;  // C bool
        public delegate* unmanaged[Cdecl]<byte> SteppedAside;
    }

    private static int _state;  // 0 not asked or not running, 1 running (stays: a mod that entered keeps its hooks)

    /// <summary>Tests: replaces the service lookup.</summary>
    internal static Func<bool>? Probe;

    /// <summary>Creative Mode runs (or may have entered) in this process. Logged once.</summary>
    public static bool Running
    {
        get
        {
            if (Volatile.Read(ref _state) == 1) return true;
            if (!Ask()) return false;
            if (Interlocked.Exchange(ref _state, 1) != 1)
                RulesRuntime.Log(LogLevel.Info, $"creative: {Note}. Off while it runs: /creative on, the creative menu, taking items, " +
                                                "the time of day and the cycle, and kiln.rules' creative hooks.");
            return true;
        }
    }

    /// <summary>Tests: forget the sticky answer.</summary>
    internal static void Reset() => Volatile.Write(ref _state, 0);

    private static bool Ask()
    {
        if (Probe is { } p) return p();
        try
        {
            var t = Services.Get<Table>(Service, Version);
            if (t == null || t->Running == null) return false;
            var id = "creative"u8;
            fixed (byte* s = id) return t->Running(new NativeStr { Ptr = s, Len = (nuint)id.Length }) != 0;
        }
        catch (Exception)
        {
            return false;  // no host (unit tests) or no such service
        }
    }
}
