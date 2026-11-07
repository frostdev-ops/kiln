// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_rules.h (kiln.rules v1, the 48-byte player struct). Blittable only; the native side static_asserts the sizes.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class RulesNative
{
    public const string Service = "kiln.rules";
    public const uint Version = 1;

    // KilnRulesPlayer.flags
    public const uint FlyAnywhere = 0x01, BuildOutsideBases = 0x02, IgnoreNoBuildZones = 0x04, IgnoreEnemies = 0x08,
        IgnoreShroud = 0x10, IgnoreBasePermissions = 0x20, ShroudFreeze = 0x40, GlideUnlimited = 0x80, GlideBoost = 0x100;

    // KilnRulesStatus.hooks
    public const uint HookFly = 0x01, HookCursor = 0x02, HookCosts = 0x04, HookDig = 0x08, HookZone = 0x10, HookShroud = 0x20,
        HookGlide = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    public struct Player
    {
        public uint Size;
        public uint Flags;
        public float BuildCost;
        public float BuildRefund;
        public float DigDamage;
        public uint Reserved;
        public float ShroudFloor;
        public float GlideSink;
        public float GlideThrust;
        public float GlideMaxSpeed;
        public float GlideLift;
        public float GlideCoast;
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
        public ulong ZoneForced;
        public ulong ShroudHeld;
        public ulong GlideChanged;
        public ulong Reserved;
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
        ShroudFloor = p.ShroudFloor, GlideSink = p.GlideSink, GlideThrust = p.GlideThrust, GlideMaxSpeed = p.GlideMaxSpeed,
        GlideLift = p.GlideLift, GlideCoast = p.GlideCoast,
    };

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }
}

/// <summary>The part of a player's rules the native hooks apply (and a Kiln client gets).</summary>
internal readonly record struct RulesNativePart(uint Flags, float BuildCost, float BuildRefund, float DigDamage,
    float ShroudFloor = 0f, float GlideSink = 1f, float GlideThrust = 0f, float GlideMaxSpeed = 0f, float GlideLift = 0f,
    float GlideCoast = 0f)
{
    public static readonly RulesNativePart Default = new(0, 1f, 1f, 1f);
    public bool IsDefault => this == Default;

    private const uint GlideFlags = RulesNative.GlideUnlimited | RulesNative.GlideBoost;

    /// <summary>Changes the glide (which the player's game predicts).</summary>
    public bool HasGlide => (Flags & GlideFlags) != 0 || GlideSink != 1f;

    public RulesNativePart WithoutGlide => this with
    {
        Flags = Flags & ~GlideFlags, GlideSink = 1f, GlideThrust = 0f, GlideMaxSpeed = 0f, GlideLift = 0f, GlideCoast = 0f,
    };
}
