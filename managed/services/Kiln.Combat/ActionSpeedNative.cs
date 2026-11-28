// SPDX-License-Identifier: MIT
// The native kiln.combat.speed table (native/core/abi/services/kiln_action_speed.h). Field order and types must match.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class SpeedNative
{
    public const string Name = "kiln.combat.speed";
    public const uint Version = 1;
    public const int Kinds = 3;
    public const int Ok = 0, ErrInvalidArgument = 1, ErrUnavailable = 3, ErrWrongThread = 4;

    // KILN_SPEED_*
    public const uint Sequence = 1, Graph = 2, Bow = 4, Sampling = 8, Prediction = 0x10, Presentation = 0x20, Wraps = 0x40,
        Items = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rate
    {
        public uint Entity;
        public fixed float Rates[Kinds];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ItemKind
    {
        public uint Item;
        public byte Kind, R0, R1, R2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public uint Parts;
        public byte Active, Scaling, R0, R1;
        public uint Wraps;
        public fixed float Defaults[Kinds];
        public uint Players;
        public uint Items;
        public ulong SequenceScaled, GraphScaled, BowScaled, Unmarked;
        public fixed byte Detail[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<float*, Rate*, uint, int> SetRates;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ItemKind*, uint, uint*, int> ItemKinds;
        public delegate* unmanaged[Cdecl]<ItemKind*, uint, int> SetItemKinds;
    }

    private static Table* _t;

    /// <summary>The table, or null when kiln.combat.speed is not there (an older kiln-combat.dll, or none).</summary>
    public static Table* T => _t != null ? _t : _t = Services.Get<Table>(Name, Version);

    /// <summary>The native status, or null without the table.</summary>
    public static Status? Read()
    {
        var t = T;
        if (t == null || t->GetStatus == null) return null;
        var s = new Status { Size = (uint)sizeof(Status) };
        return t->GetStatus(&s) == Ok ? s : null;
    }

    public static string Detail(Status s)
    {
        var p = s.Detail;
        var n = 0;
        while (n < 256 && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n).Trim().TrimEnd(';').Trim();
    }
}
