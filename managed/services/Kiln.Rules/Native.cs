// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_rules.h (kiln.rules v1, the 56-byte player struct). Blittable only; the native side static_asserts the sizes.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class RulesNative
{
    public const string Service = "kiln.rules";
    public const uint Version = 1;

    // KilnRulesPlayer.flags
    public const uint FlyAnywhere = 0x01, BuildOutsideBases = 0x02, IgnoreNoBuildZones = 0x04, IgnoreEnemies = 0x08,
        IgnoreShroud = 0x10, IgnoreBasePermissions = 0x20, ShroudFreeze = 0x40, GlideUnlimited = 0x80, GlideBoost = 0x100,
        FreeCrafting = 0x200, KeepConsumables = 0x400, CharacterFlight = 0x800, FlightBaseOnly = 0x1000, GlideSteer = 0x2000;

    // KilnRulesStatus.hooks
    public const uint HookFly = 0x01, HookCursor = 0x02, HookCosts = 0x04, HookDig = 0x08, HookZone = 0x10, HookShroud = 0x20,
        HookGlide = 0x40, HookMap = 0x80, HookCraft = 0x100, HookCraftUi = 0x200, HookConsume = 0x400, HookConsumeBuff = 0x800, HookFlight = 0x1000,
        HookGlideSteer = 0x2000;

    // map_reveal modes
    public const uint MapDefault = 0, MapRadius = 1, MapWhole = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Creative
    {
        public uint Size;
        public uint MapMode;
        public float MapRadius;
        public uint MapRevealed;
        public ulong MapCalls;
        public ulong CraftsFree;
        public ulong CraftUi;
        public ulong ConsumesKept;
        public ulong Faults;
        public ulong FlightTakeoffs;
        public ulong FlightLandings;
        public ulong FlightEnds;
    }

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
        public float GlideClimb;
        public float GlideSteerLimit;  // since 0.2.0-dev.24 (was reserved, 0): degrees
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
        public ulong GlideSteered;  // since 0.2.0-dev.24 (was reserved, 0)
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

    /// <summary>The same table with the functions added in 0.2.0-dev.16 (an older kiln.rules has no such table).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CreativeTable
    {
        public Table V1;
        public delegate* unmanaged[Cdecl]<uint, float, int> MapReveal;
        public delegate* unmanaged[Cdecl]<Creative*, int> GetCreative;
    }

    private static CreativeTable* _c;
    public static CreativeTable* C => _c != null ? _c : (_c = Services.Get<CreativeTable>(Service, Version));
    public static bool HasCreative => C != null;

    /// <summary>The installed hooks (0 without the service).</summary>
    public static uint Hooks
    {
        get
        {
            if (T == null) return 0;
            var st = new Status { Size = (uint)sizeof(Status) };
            return T->GetStatus(&st) == 0 ? st.Hooks : 0;
        }
    }

    public static bool CreativeStatus(out Creative c)
    {
        c = new Creative { Size = (uint)sizeof(Creative) };
        if (!HasCreative) return false;
        fixed (Creative* p = &c) return C->GetCreative(p) == 0;
    }

    private static Table* _t;
    public static Table* T => _t != null ? _t : (_t = Services.Get<Table>(Service, Version));

    public static Player Of(in RulesNativePart p) => new()
    {
        Size = (uint)sizeof(Player), Flags = p.Flags, BuildCost = p.BuildCost, BuildRefund = p.BuildRefund, DigDamage = p.DigDamage,
        ShroudFloor = p.ShroudFloor, GlideSink = p.GlideSink, GlideThrust = p.GlideThrust, GlideMaxSpeed = p.GlideMaxSpeed,
        GlideLift = p.GlideLift, GlideCoast = p.GlideCoast, GlideClimb = p.GlideClimb, GlideSteerLimit = p.GlideSteerLimit,
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
    float GlideCoast = 0f, float GlideClimb = 0f, float GlideSteerLimit = 0f)
{
    public static readonly RulesNativePart Default = new(0, 1f, 1f, 1f);
    public bool IsDefault => this == Default;

    // Every glide flag the player's game predicts: one missing here would reach a server-world hook while the game it
    // is held back from (no rules channel) glides without it.
    private const uint GlideFlags = RulesNative.GlideUnlimited | RulesNative.GlideBoost | RulesNative.GlideSteer;

    /// <summary>Changes the glide (which the player's game predicts).</summary>
    public bool HasGlide => (Flags & GlideFlags) != 0 || GlideSink != 1f;

    public RulesNativePart WithoutGlide => this with
    {
        Flags = Flags & ~GlideFlags, GlideSink = 1f, GlideThrust = 0f, GlideMaxSpeed = 0f, GlideLift = 0f, GlideCoast = 0f,
        GlideClimb = 0f, GlideSteerLimit = 0f,
    };

    /// <summary>Without glider steering: for a game that has not confirmed it predicts the steering hook, the glide
    /// (GlideBoost's plane path included) runs as before steering existed.</summary>
    public RulesNativePart WithoutSteer => this with { Flags = Flags & ~RulesNative.GlideSteer, GlideSteerLimit = 0f };

    private const uint FlightFlags = RulesNative.CharacterFlight | RulesNative.FlightBaseOnly;
    public RulesNativePart WithoutFlight => this with { Flags = Flags & ~FlightFlags };

    /// <summary>Rules the player's game predicts (the glide, character flight): held back from a game without Kiln's rules
    /// channel, which would fight the server every step.</summary>
    public bool HasPredicted => HasGlide || (Flags & FlightFlags) != 0;

    public RulesNativePart WithoutPredicted => WithoutGlide with { Flags = Flags & ~(GlideFlags | FlightFlags) };

    // The generated record formatter would recursively format the computed WithoutGlide property.
    public override string ToString() =>
        $"flags 0x{Flags:x}, build cost x{BuildCost}, refund x{BuildRefund}, dig x{DigDamage}, Shroud floor {ShroudFloor}, " +
        $"glide sink x{GlideSink}, thrust {GlideThrust}, max speed {GlideMaxSpeed}, lift {GlideLift}, coast {GlideCoast}, climb {GlideClimb}, " +
        $"steer limit {GlideSteerLimit}";
}
