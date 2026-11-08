// SPDX-License-Identifier: MIT
// The native kiln.combat table (native/core/abi/services/kiln_combat.h). Field order and types must match the C header.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class CombatNative
{
    public const string Name = "kiln.combat";
    public const uint Version = 1;
    public const int Ok = 0, ErrInvalidArgument = 1, ErrNotFound = 2, ErrUnavailable = 3, ErrWrongThread = 4;

    public const uint Hurt = 1, Heal = 2, Damaged = 3, Died = 4, Respawned = 5, Drops = 6, Knockback = 7, Fall = 8;
    public const byte HealHit = 0, HealKiln = 1, HealRegeneration = 2, HealAttribute = 3;
    public static uint Mask(uint kind) => 1u << (int)kind;

    public const uint DamageForceInterrupt = 0x01, DamageSilent = 0x02, DamageAsGameHit = 0x04, DamageKill = 0x08, DamageNoCredit = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Health
    {
        public uint Size;
        public int Current, Max;
        public byte Dead, HasActor, Invulnerable, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DamageRequest
    {
        public uint Size;
        public uint Target, Attacker, Source;
        public int Amount;
        public ushort Reaction;
        public byte PushBack, Reserved0;
        public uint Flags;
        public float Aggro;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HitStatus
    {
        public uint Size;
        public uint State;
        public int HealthBefore, HealthAfter;
        public byte KillingBlow, R0, R1, R2;
        public uint Steps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size;
        public uint Kind;
        public ulong Id;
        public uint Target, Part, Attacker, Source;
        public int Amount;
        public int HealthBefore, HealthAfter, HealthMax;
        public uint HitFlags;
        public ushort DamageTypes, Reaction;
        public byte AttackCategory, WeaponCategory, Origin, Lethal;
        public byte Canceled, IsPlayer, R0, R1;
        public uint ImpactId;
        public Vec3 Position;
        public ulong Step;
        // added later in v1
        public Vec3 Direction;
        public float FallDistance, FallFraction;
        public byte PushBack, Cause, R2, R3;
        public uint R4;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Health, Hits, Deaths, Holding;
        public uint Interest;
        public ulong HitsSeen, HitsHeld, HitsReleased, HitsCanceled, HitsModified, KilnHits, GodBlocked, GodRestored,
            DeathsSeen, EventsDelivered;
        public uint Invulnerable, Reserved;
        public ulong GameDiedEvents, HitsDeferred, DropsSeen, DropsCanceled, RepeatHits;
        // added later in v1
        public byte Falls, Regen, AttrHeals, R0, R1, R2, R3, R4;
        public ulong FallsSeen, FallsCanceled, FallsModified, RegenSeen, RegenCanceled, AttrHealsSeen, AttrHealsCorrected,
            KnockbacksSeen, KnockbacksModified;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint, Health*, int> GetHealth;
        public delegate* unmanaged[Cdecl]<uint, int, int*, int> SetHealth;
        public delegate* unmanaged[Cdecl]<uint, int, int*, int> Heal;
        public delegate* unmanaged[Cdecl]<ulong, DamageRequest*, ulong*, int> Damage;
        public delegate* unmanaged[Cdecl]<ulong, HitStatus*, int> HitStatus;
        public delegate* unmanaged[Cdecl]<ulong, uint, byte, int> SetInvulnerable;
        public delegate* unmanaged[Cdecl]<uint, byte> Invulnerable;
        public delegate* unmanaged[Cdecl]<ulong, int> ReleaseOwner;
        public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<Event*, uint, void*, uint>, void*, int> SetListener;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }

    /// <summary>Attribute kinds (KILN_ATTR_*), also the values of <see cref="GameAttribute"/>.</summary>
    public const uint AttrHealth = 1, AttrStamina = 2, AttrMana = 3, AttrBodyHeat = 4, AttrOxygen = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeIds
    {
        public uint Size, Kind, CurrentId, MinId, MaxId, Component;
        public byte GameClamp, R0, R1, R2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeValue
    {
        public uint Size, Id, Kind, CurrentId, MaxId;
        public int Value, Current, Min, Max;
        public byte HasMin, HasMax, IsCurrent, R0;
    }

    /// <summary>The whole v2 table: v1 followed by the attribute functions. Use it only when the header's size says the
    /// native service has them (an older runtime publishes v1).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV2
    {
        public Table V1;
        public delegate* unmanaged[Cdecl]<uint, AttributeIds*, int> AttributeIdsFn;
        public delegate* unmanaged[Cdecl]<uint, uint, AttributeValue*, int> AttributeGet;
        public delegate* unmanaged[Cdecl]<uint, uint, int, AttributeValue*, int> AttributeSet;
        public delegate* unmanaged[Cdecl]<uint, uint, int, AttributeValue*, int> AttributeAdd;
    }

    private static Table* _t;

    /// <summary>The table, or null when kiln.combat is not loaded (looked up again until it is found).</summary>
    public static Table* T => _t != null ? _t : _t = Services.Get<Table>(Name, Version);

    /// <summary>The v2 table (attributes), or null when kiln.combat is missing or an older runtime's v1.</summary>
    public static TableV2* V2
    {
        get
        {
            var t = T;
            if (t == null || t->Header.Size < (uint)sizeof(TableV2)) return null;
            return (TableV2*)t;
        }
    }

    public static TableV2* RequireV2(string api)
    {
        var t = V2;
        if (t != null) return t;
        throw new NotSupportedException(T == null
            ? $"{api}: kiln.combat is not available on this server build"
            : $"{api}: this Kiln runtime's kiln.combat has no attribute functions (update Kiln)");
    }

    public static string DescribeAttribute(int rc) => rc switch
    {
        1 => "not an attribute id of health, stamina, mana, body heat or oxygen, or not a current value (the maximum and computed values are the game's to change)",
        2 => "no such entity, or it has no such attribute (a creature has no mana; players have all five)",
        3 => "unavailable: the game's attribute resource was not found on this build, or no world is running",
        4 => "not on the tick thread",
        _ => $"error {rc}",
    };

    public static Table* Require(string api) =>
        T != null ? T : throw new NotSupportedException($"{api}: kiln.combat is not available on this server build");

    public static string Describe(int rc) => rc switch
    {
        1 => "invalid argument",
        2 => "no such entity (or it has no health)",
        3 => "unavailable on this build or the world is not running",
        4 => "not on the tick thread",
        _ => $"error {rc}",
    };
}
