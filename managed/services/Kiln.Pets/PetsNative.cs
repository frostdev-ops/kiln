// SPDX-License-Identifier: MIT
// The native table of kiln.pets v1 (native/core/abi/services/kiln_pets.h), mirrored field by field. Sizes: binding 96,
// state 80, hit query 56 bytes (pinned by Kiln.Pets.Tests).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class PetsNative
{
    public const string Service = "kiln.pets";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;

    public const uint Guarded = 1, DownPending = 2, Retiring = 4, Retired = 8, ProtectionLost = 16, Staging = 32;
    public const uint BindStaging = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Id128 { public ulong Lo, Hi; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CharacterKey128 { public ulong Account; public Id128 Character; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Binding
    {
        public uint Size, Flags;
        public CreatureControlNativeKey Body;
        public Id128 Pet;
        public CharacterKey128 Owner;
        public ulong CharacterSession;
        public uint OwnerEntity, HostileFactions, Pvp, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint Size, Flags;
        public CreatureControlNativeKey Body;
        public Id128 Pet;
        public ulong DirtySequence, ObservedStep;
        public uint Cause, Reserved;
        public ulong DeniedHits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HitQuery
    {
        public uint Size, HitFlags;
        public ulong WorldEpoch;
        public uint RootSource, Source, Target;
        public int HealthChange, RootHealth, TargetHealth;
        public ushort Reaction;
        public byte Pushback, HitKind;
        public float Aggro;
        public byte RootFaction, TargetFaction, RootClass, TargetClass;
        public uint Reserved;
    }

    /// <summary>kiln.creature_control's body key (24 bytes), as kiln.pets takes it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CreatureControlNativeKey
    {
        public ulong WorldEpoch, BodyGeneration;
        public uint Entity, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, Binding*, int> Bind;
        public delegate* unmanaged[Cdecl]<ulong, CreatureControlNativeKey, int> Activate;
        public delegate* unmanaged[Cdecl]<CreatureControlNativeKey, State*, int> InspectBody;
        public delegate* unmanaged[Cdecl]<ulong, State*, uint, uint*, uint*, int> CollectDirty;
        public delegate* unmanaged[Cdecl]<ulong, CreatureControlNativeKey, int> RetireBinding;
        public delegate* unmanaged[Cdecl]<ulong, uint, int, uint> DeathPolicy;
        public delegate* unmanaged[Cdecl]<HitQuery*, uint> HitPolicy;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint> Involved;
        public delegate* unmanaged[Cdecl]<ulong, CreatureControlNativeKey, uint, int> SetOwnerEntity;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, int> SetHostile;
        public delegate* unmanaged[Cdecl]<ulong, int> Withdraw;
    }

    private static Table* _table;

    /// <summary>The table, or null when kiln.pets' native module is not here (asked for again at the next use).</summary>
    public static Table* T => _table != null ? _table : (_table = Services.Get<Table>(Service, Version));

    public static CreatureControlNativeKey Key(ControlBodyKey k) => new() { WorldEpoch = k.WorldEpoch, BodyGeneration = k.BodyGeneration, Entity = k.Entity.Value };

    public static Id128 Id(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        return new Id128 { Lo = BitConverter.ToUInt64(b[..8]), Hi = BitConverter.ToUInt64(b[8..]) };
    }
}
