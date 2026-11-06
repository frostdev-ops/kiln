// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_rules.h (kiln.rules v1). Blittable only; the native side static_asserts the sizes.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class RulesNative
{
    public const string Service = "kiln.rules";
    public const uint Version = 1;

    // KilnRulesPlayer.flags
    public const uint FlyAnywhere = 0x01, BuildOutsideBases = 0x02, IgnoreNoBuildZones = 0x04, IgnoreEnemies = 0x08,
        IgnoreShroud = 0x10, IgnoreBasePermissions = 0x20;

    // KilnRulesStatus.hooks
    public const uint HookFly = 0x01, HookCursor = 0x02, HookCosts = 0x04, HookDig = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    public struct Player
    {
        public uint Size;
        public uint Flags;
        public float BuildCost;
        public float BuildRefund;
        public float DigDamage;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public uint Hooks;
        public uint Players;
        public uint LocalFlags;
        public ulong FlyAllowed;
        public ulong CursorCleared;
        public ulong CostsScaled;
        public ulong RefundsScaled;
        public ulong DigsScaled;
        public ulong Faults;
        public fixed byte Detail[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint, Player*, int> Set;
        public delegate* unmanaged[Cdecl]<void> Clear;
        public delegate* unmanaged[Cdecl]<uint, Player*, int> Get;
        public delegate* unmanaged[Cdecl]<Player*, int> SetLocal;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }

    private static Table* _t;
    public static Table* T => _t != null ? _t : (_t = Services.Get<Table>(Service, Version));

    public static Player Of(in RulesNativePart p) => new()
    {
        Size = (uint)sizeof(Player), Flags = p.Flags, BuildCost = p.BuildCost, BuildRefund = p.BuildRefund, DigDamage = p.DigDamage,
    };

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }
}

/// <summary>The part of a player's rules the native hooks apply (and a Kiln client gets).</summary>
internal readonly record struct RulesNativePart(uint Flags, float BuildCost, float BuildRefund, float DigDamage)
{
    public static readonly RulesNativePart Default = new(0, 1f, 1f, 1f);
    public bool IsDefault => this == Default;
}
