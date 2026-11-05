// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_gameplay.h (v1).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class GameplayNative
{
    public const uint Tick = 0, Craft = 1, Crafted = 2, Interact = 3, Interacted = 4, Loot = 5, Pickup = 6, Build = 7,
        Teleport = 8, Teleported = 9, Spawn = 10, EntityJoined = 11, EntityLeft = 12,
        Discard = 13;

    public const uint FCancelable = 0x01, FCanceled = 0x02, FDenied = 0x04, FRetarget = 0x08, FInjected = 0x10, FReplaced = 0x20;
    public const uint Keep = 0, Cancel = 1, Allow = 2;

    public static uint Mask(uint kind) => 1u << (int)kind;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
        public WorldPosition ToPosition() => new(X, Y, Z);
        public static Vec3 From(WorldPosition p) => new() { X = p.X, Y = p.Y, Z = p.Z };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Item
    {
        public uint Id, Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size, Kind, Flags, Zone;
        public ulong Step;
        public uint Actor, Target, Id, Id2, Amount;
        public int Result;
        public uint Op, Extra;
        public Vec3 Position, TargetPosition;
        public fixed byte Guid[16];
        public uint OutputCount, Phase;
        public fixed uint Outputs[16]; // 8 x {item, count}
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Zone
    {
        public uint Size, Deny, Shape, Flags;
        public Vec3 Center;
        public double Radius;
        public Vec3 Min, Max;
        public ulong* Allowed;
        public uint AllowedCount, Priority;
        public NativeStr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Inject
    {
        public uint Size, Kind, Actor, Target, Id, Amount, Flags, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Intercept, Emit, CraftHook, BuildHooks, TerraformHook, SpawnHook, Teleport, World;
        public uint Zones, Subscribers;
        public ulong Steps, EventsSeen, Dispatched, Canceled, Denied, HookRecords;
        public byte EntityHooks, R0, R1, R2;
        public uint EntityCategories;
        public ulong EntitiesJoined, EntitiesLeft, EntitiesFiltered;
        public byte DiscardOn, R3, R4, R5, R6, R7, R8, R9;
        public ulong DiscardsSeen, DiscardsCanceled;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Recipe
    {
        public uint Size, Id, Workshop, InputCount, OutputCount, Reserved;
        public long DurationNs;
        public ulong RequiredProps;
        public fixed uint Inputs[16];
        public fixed uint Outputs[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, uint, delegate* unmanaged[Cdecl]<Event*, void*, uint>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<ulong, Zone*, uint*, int> ZoneAdd;
        public delegate* unmanaged[Cdecl]<uint, Zone*, int> ZoneUpdate;
        public delegate* unmanaged[Cdecl]<uint, int> ZoneRemove;
        public delegate* unmanaged[Cdecl]<uint, Vec3, uint, uint*, byte> ZoneDenies;
        public delegate* unmanaged[Cdecl]<Inject*, int> Inject;
        public delegate* unmanaged[Cdecl]<uint, byte, int> Watch;
        public delegate* unmanaged[Cdecl]<Status*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, Recipe*, int> Recipe;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> WorkshopRecipes;
        // entity_filter follows (added later in v1): read it with EntityFilter(t) after checking Header.Size, so an
        // older kiln-gameplay.dll still matches this table.
    }

    /// <summary>entity_filter of the native table, or null when the DLL predates it.</summary>
    public static delegate* unmanaged[Cdecl]<uint, int> EntityFilter(Table* t) =>
        t != null && t->Header.Size >= (uint)(sizeof(Table) + sizeof(nint))
            ? *(delegate* unmanaged[Cdecl]<uint, int>*)((byte*)t + sizeof(Table))
            : null;

    /// <summary>spawn_replace / spawn_replace_clear (appended after entity_filter), or null when the DLL predates them.</summary>
    public static delegate* unmanaged[Cdecl]<ulong, byte*, byte*, float, int> SpawnReplace(Table* t) =>
        t != null && t->Header.Size >= (uint)(sizeof(Table) + 3 * sizeof(nint))
            ? *(delegate* unmanaged[Cdecl]<ulong, byte*, byte*, float, int>*)((byte*)t + sizeof(Table) + sizeof(nint))
            : null;

    public static delegate* unmanaged[Cdecl]<ulong, uint*, int> SpawnReplaceClear(Table* t) =>
        t != null && t->Header.Size >= (uint)(sizeof(Table) + 3 * sizeof(nint))
            ? *(delegate* unmanaged[Cdecl]<ulong, uint*, int>*)((byte*)t + sizeof(Table) + 2 * sizeof(nint))
            : null;

    private static Table* _table;
    public static Table* T => _table != null ? _table : (_table = Services.Get<Table>("kiln.gameplay", 1));

    static GameplayNative()
    {
        if (sizeof(Event) != 0xC0) throw new InvalidOperationException($"Kiln.Gameplay: event struct is {sizeof(Event)} bytes, expected 192");
        if (sizeof(Zone) != 0x80) throw new InvalidOperationException($"Kiln.Gameplay: zone struct is {sizeof(Zone)} bytes, expected 128");
    }
}
