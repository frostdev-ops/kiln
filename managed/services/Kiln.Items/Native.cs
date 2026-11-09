// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_items.h (kiln.items v3; the v2 functions are in TableV2, the v3 ones in TableV3). Blittable only; field order is the ABI (the native
// side static_asserts the same sizes and offsets).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class ItemsNative
{
    public const string Service = "kiln.items";
    public const uint Version = 1;

    // KilnItemsRequest.flags
    public const uint Level = 0x01, Rarity = 0x02, ExactSlot = 0x04, AllOrNothing = 0x08, Notify = 0x10, NoMerge = 0x20,
        DryRun = 0x40, Recipe = 0x80, Unlock = 0x100, GemSlot = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    public struct Request
    {
        public uint Size;
        public uint Flags;
        public uint Entity;
        public uint Item;
        public uint Count;
        public uint Level;
        public byte Rarity;
        public byte Unlock;
        public byte R1, R2;
        public uint Pide;
        public ulong Slot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Change
    {
        public ulong Slot;
        public uint Item;
        public uint Amount;
        public uint Pide;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Result
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Entity;
        public uint Item;
        public uint Requested;
        public uint Done;
        public uint GameError;
        public uint ChangeCount;
        public uint Flags;
        public uint Steps;
        public fixed byte Changes[16 * 24];
        public fixed byte Message[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Slot
    {
        public ulong Id;
        public uint Item;
        public uint Count;
        public uint Pide;
        public ushort Level;
        public byte Rarity;
        public byte Flags;
        public byte Link;
        public byte Category;
        public ushort State;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Inventory
    {
        public uint Size;
        public uint Entity;
        public byte Initialized;
        public byte SetupFlags;
        public byte GenericSlots;
        public byte AvailableSlots;
        public uint InventoryCount;
        public uint SlotTotal;
        public uint UsedSlots;
        public fixed uint Inventories[32];
        public fixed byte Categories[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ItemInfo
    {
        public uint Size;
        public uint Id;
        public fixed byte Guid[16];
        public ushort MaxStack;
        public byte Category;
        public byte Rarity;
        public uint ItemFlags;
        public uint LevelMin;
        public uint LevelMax;
        public byte EquipSlot;
        public byte Gear;
        public byte R0, R1;
        public NativeStr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Transactions;
        public byte Read;
        public byte Catalog;
        public byte Program;
        public uint Pending;
        public uint Items;
        public ulong Committed;
        public ulong RolledBack;
        public ulong ProgramUpdates;
        public fixed byte Detail[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, Request*, delegate* unmanaged[Cdecl]<Result*, void*, void>, void*, ulong*, int> Give;
        public delegate* unmanaged[Cdecl]<ulong, Request*, delegate* unmanaged[Cdecl]<Result*, void*, void>, void*, ulong*, int> Take;
        public delegate* unmanaged[Cdecl]<ulong, int> Cancel;
        public delegate* unmanaged[Cdecl]<ulong, void> CancelOwner;
        public delegate* unmanaged[Cdecl]<uint, Inventory*, Slot*, uint, uint*, int> Read;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> FindInventories;
        public delegate* unmanaged[Cdecl]<uint> ItemCount;
        public delegate* unmanaged[Cdecl]<uint, ItemInfo*, int> ItemAt;
        public delegate* unmanaged[Cdecl]<uint, ItemInfo*, int> ItemInfoOf;
        public delegate* unmanaged[Cdecl]<NativeStr, uint> FindItem;
        public delegate* unmanaged[Cdecl]<byte*, uint> IdOfGuid;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }

    // ---- v2: water (KilnItemsFuelRequest.flags) ----
    public const uint FuelSet = 0x00, FuelFill = 0x01, FuelAdd = 0x02, FuelInventories = 0x10, FuelDryRun = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    public struct FuelRequest
    {
        public uint Size;
        public uint Flags;
        public uint Entity;
        public uint Item;
        public int Units;
        public int Limit;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FuelChange
    {
        public uint Container;
        public uint Item;
        public ulong Slot;
        public int Before;
        public int After;
        public int Max;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FuelResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Entity;
        public uint Item;
        public uint Flags;
        public uint Matched;
        public uint Changed;
        public uint Steps;
        public uint ChangeCount;
        public uint Reserved;
        public fixed byte Changes[16 * 32];
        public fixed byte Message[160];
    }

    /// <summary>The whole v2 table: v1 followed by the water functions. Use it only when the header's size says the
    /// native service has them (an older runtime publishes v1).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV2
    {
        public Table V1;
        public delegate* unmanaged[Cdecl]<ulong, FuelRequest*, delegate* unmanaged[Cdecl]<FuelResult*, void*, void>, void*, ulong*, int> Fuel;
        public delegate* unmanaged[Cdecl]<uint, int*, int*, int> FuelRead;
        public delegate* unmanaged[Cdecl]<ulong, FuelRequest*, delegate* unmanaged[Cdecl]<ulong, FuelChange*, uint, void*, void>, void*, ulong*, int> FuelKeep;
        public delegate* unmanaged[Cdecl]<ulong, int> FuelKeepEnd;
    }

    // ---- v3: moves, container names, transfer rules and reports ----
    public const uint MoveStack = 0, MoveQuickStack = 1, MoveAllKind = 2;
    public const uint MoveDryRun = 0x01, MoveEquipped = 0x02, MoveActionBar = 0x04, MoveSilent = 0x08;
    public const uint TransferQuickStack = 1, TransferMoveAll = 2, TransferAny = 3;
    public const uint TransferByMod = 1, TransferCanceled = 2;
    public const uint RuleKeepSlot = 1, RuleKeepItem = 2, RuleDeny = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct MoveRequest
    {
        public uint Size;
        public uint Flags;
        public uint Kind;
        public uint From;
        public uint To;
        public uint Count;
        public ulong Slot;
        public ulong ToSlot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MoveChange
    {
        public ulong FromSlot;
        public ulong ToSlot;
        public uint Item;
        public uint Amount;
        public uint Pide;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MoveResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint From;
        public uint To;
        public uint Kind;
        public uint Flags;
        public uint Requested;
        public uint Done;
        public uint Stacks;
        public uint GameError;
        public uint ChangeCount;
        public uint Canceled;
        public uint Steps;
        public uint Reserved;
        public fixed byte Changes[16 * 32];
        public fixed byte Message[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TransferRule
    {
        public uint Size;
        public uint Type;
        public uint Kinds;
        public uint Entity;
        public uint Item;
        public uint Reserved;
        public ulong Slot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TransferReport
    {
        public uint Size;
        public uint Kind;
        public uint From;
        public uint To;
        public uint Player;
        public uint Flags;
        public uint Moved;
        public uint Kept;
        public ulong Step;
        public uint R0, R1;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status2
    {
        public uint Size;
        public byte Moves;
        public byte Names;
        public byte TransferHooks;
        public byte HooksInstalled;
        public uint Rules;
        public uint Handlers;
        public uint Reporters;
        public uint Pad;
        public ulong TransfersSeen;
        public ulong TransfersCanceled;
        public ulong MovesDone;
        public uint NamesFound;
        public uint Reserved;
        public fixed byte Detail[160];
    }

    /// <summary>The whole v3 table: v2 followed by the move, name and transfer functions. Use it only when the header's
    /// size says the native service has them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV3
    {
        public TableV2 V2;
        public delegate* unmanaged[Cdecl]<ulong, MoveRequest*, delegate* unmanaged[Cdecl]<MoveResult*, void*, void>, void*, ulong*, int> Move;
        public delegate* unmanaged[Cdecl]<uint, byte*, uint, uint*, int> ContainerName;
        public delegate* unmanaged[Cdecl]<ulong, TransferRule*, ulong*, int> TransferRuleAdd;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, ulong*, int> TransferListen;  // native handlers only
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<TransferReport*, void*, void>, void*, ulong*, int> TransferReportAdd;
        public delegate* unmanaged[Cdecl]<ulong, int> TransferEnd;
        public delegate* unmanaged[Cdecl]<Status2*, int> GetStatus2;
    }

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    // KilnResult
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;
}
