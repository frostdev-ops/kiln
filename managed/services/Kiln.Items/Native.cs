// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_items.h (kiln.items v8; the v2 functions are in TableV2, the v3 ones in TableV3,
// the v4 ones in TableV4, the v5 ones in TableV5, the v6 ones in TableV6, v7 in TableV7, the v8 gear functions in TableV8). Blittable only; field order is the ABI (the native side static_asserts the same sizes and offsets).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class ItemsNative
{
    public const string Service = "kiln.items";
    public const uint Version = 1;

    // KilnItemsRequest.flags
    public const uint Level = 0x01, Rarity = 0x02, ExactSlot = 0x04, AllOrNothing = 0x08, Notify = 0x10, NoMerge = 0x20,
        DryRun = 0x40, Recipe = 0x80, Unlock = 0x100, GemSlot = 0x200;
    // v7 (the gem wave): sockets on any gear, the re-creation of gear (Gems.AddSocket)
    public const uint GemSlotAny = 0x400, Replace = 0x800, Dye = 0x1000, DurabilityFlag = 0x2000;

    /// <summary>KilnItemsRequestEx (v7): the request, then the dye and durability of a give with Dye / DurabilityFlag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RequestEx
    {
        public Request Base;
        public uint DyeItem;
        public float Durability;
        public uint R0, R1;
    }

    /// <summary>KilnItemsSocketRoll (v7).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SocketRoll
    {
        public uint Size;
        public float Rings, Armour, Shields, Other;
        public uint Reserved;
    }

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
    public const uint TransferByMod = 1, TransferCanceled = 2, TransferDryRun = 4;  // DryRun: native handler events only, never in reports
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
        public SlotWitness Source, Destination;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SlotWitness
    {
        public uint Item, Count, Pide, Stamp;
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

    // Appended within v3: keep TableV3's existing size so its move/transfer rows remain usable on older v3 runtimes.
    [StructLayout(LayoutKind.Sequential)]
    public struct TableWithResultPeek
    {
        public TableV3 V3;
        public delegate* unmanaged[Cdecl]<ulong, Result*, int> PeekResult;
    }

    // ---- v4: item instances (KilnItemsInstance, KilnItemsInstancesStatus) ----
    public const uint ReadLocation = 0x01;
    public const uint InstFound = 0x0001, InstPlaced = 0x0002, InstSavedSlot = 0x0004, InstEquipped = 0x0008,
        InstLevel = 0x0010, InstDurability = 0x0020, InstFuel = 0x0040, InstDye = 0x0080, InstPerks = 0x0100,
        InstGem = 0x0200, InstGone = 0x0400;
    public const uint InstancesOn = 0x01, InstancesExhausted = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public uint Entity;
        public uint Stamp;
        public uint Flags;
        public uint Item;
        public ulong Slot;
        public uint Holder;
        public uint FlatSlot;
        public byte Link;
        public byte Category;
        public ushort State;
        public uint Count;
        public uint Level;
        public int Durability;
        public int DurabilityMax;
        public int Fuel;
        public int FuelMax;
        public uint Dye;
        public byte PerksAvailable;
        public byte PerksUnlocked;
        public byte GemLevel;
        public byte R0;
        public uint Gem;
        public fixed uint Reserved[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct InstancesStatus
    {
        public uint Size;
        public uint Flags;
        public ulong StampsIssued;
        public ulong Reads;
        public uint TableSize;
        public uint Resets;
        public uint NextEntityId;
        public uint Mismatches;
        public uint HolderRebuilds;
        public uint Reserved;
        public fixed byte Detail[160];
    }

    /// <summary>The whole v4 table: v3 (with the result peek) followed by the item-instance functions. Use it only when
    /// the header's size says the native service has them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV4
    {
        public TableWithResultPeek V3;
        public delegate* unmanaged[Cdecl]<Instance*, uint, uint, uint, int> ReadInstances;
        public delegate* unmanaged[Cdecl]<uint, Instance*, uint, uint, uint*, byte*, int> ReadHolder;
        public delegate* unmanaged[Cdecl]<InstancesStatus*, int> GetInstancesStatus;
    }

    // ---- v5 (0.2.0-dev.27): equipment (KilnItemsEquipRequest, KilnItemsEquipResult, KilnItemsEquipStatus) ----
    public const uint EquipUnequip = 0x01, EquipDryRun = 0x02;
    public const byte EquipAny = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct EquipRequest
    {
        public uint Size;
        public uint Flags;
        public uint Player;
        public byte EquipmentSlot;
        public byte R0, R1, R2;
        public ulong Slot;
        public uint Pide;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EquipResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Player;
        public uint Flags;
        public byte EquipmentSlot;
        public byte GameError;
        public ushort R0;
        public uint Steps;
        public uint Item;
        public uint Pide;
        public uint Stamp;
        public uint Replaced;
        public ulong From;
        public ulong To;
        public uint ReplacedPide;
        public uint ReplacedStamp;
        public ulong ReplacedTo;
        public fixed uint Reserved[4];
        public fixed byte Message[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EquipStatus
    {
        public uint Size;
        public byte Available;
        public byte Program;
        public ushort R0;
        public uint Pending;
        public uint R1;
        public ulong Done;
        public ulong Refused;
        public fixed byte Detail[160];
    }

    /// <summary>The whole v5 table: v4 followed by the equipment functions. Use it only when the header's size says the
    /// native service has them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV5
    {
        public TableV4 V4;
        public delegate* unmanaged[Cdecl]<ulong, EquipRequest*, delegate* unmanaged[Cdecl]<EquipResult*, void*, void>, void*, ulong*, int> Equip;
        public delegate* unmanaged[Cdecl]<EquipStatus*, int> GetEquipStatus;
    }

    // ---- v6 (0.2.0-dev.29): equipment sets (KilnSetSnapshotV1, KilnSetStatusV1; kiln_items.h "equipment sets") ----
    public const uint SetRefusalNone = 0, SetRefusalNoPlayer = 1, SetRefusalNoIdentity = 2, SetRefusalShape = 3, SetRefusalBudget = 4;

    /// <summary><c>KilnSetSnapshotV1</c>: the chapter's 88 bytes and the refusal tail, 256 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetSnapshot
    {
        public uint Size, Player;
        public ulong Token, WorldEpoch, CapturedStep;
        public fixed byte ProcessSession[16];
        public DecisionsNative.EntityKey Identity;
        public uint Refusal, Reserved;
        public fixed byte Reason[160];
    }

    /// <summary><c>KilnSetStatusV1</c>, 208 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetStatus
    {
        public uint Size;
        public byte Available, Broken;
        public ushort R0;
        public uint Snapshots, Pending;
        public ulong Captured, Done, Refused, InvariantFailures;
        public fixed byte Detail[160];
    }

    // KilnSetPlanV1.status, KilnSetStepV1.op
    public const uint SetPlanPlanned = 0, SetPlanNoChange = 1, SetPlanStale = 4, SetPlanRefused = 5;
    public const byte SetStepEquip = 1, SetStepUnequip = 2;
    public const int SetMaxSteps = 128;

    /// <summary><c>KilnSetStepV1</c>, 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetStep
    {
        public uint Index;
        public byte Op, EquipmentSlot, Entry, Kind;
        public uint ItemId, ItemEntity;
        public ulong InstanceStamp, SourceSlot;
        public uint Count, Reserved;
    }

    /// <summary><c>KilnSetPlanV1</c>, 5408 bytes (128 steps).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetPlan
    {
        public uint Size, Status, Refusal, FailedEntry, StepCount, RequiredFreeSlots;
        public ulong ValidThroughStep;
        public fixed byte Reason[256];
        public fixed byte Steps[SetMaxSteps * 40];
    }

    // KilnSetRequestV1, KilnSetSlotValueV1, KilnSetChangeV1, KilnSetResultV1
    public const uint SetRequestDryRun = 1;
    public const uint SetRequestLabFailStep = 0x100, SetRequestLabGuardBit = 0x200, SetRequestLabCommitAnyway = 0x400, SetRequestLabFakeMismatch = 0x800;
    public const uint SetResultDryRun = 1, SetResultCommitted = 2, SetResultMayHaveMutated = 4;

    /// <summary><c>KilnSetRequestV1</c>, 56 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetRequest
    {
        public uint Size, Player, Flags, Count;
        public EquipDecisions.SetOp* Entries;
        public ulong SnapshotToken;
        public ulong OriginOwner;
        public fixed byte ProcessSession[16];
    }

    /// <summary><c>KilnSetSlotValueV1</c>, 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetSlotValue
    {
        public uint ItemId, ItemEntity, Count, Reserved;
        public ulong InstanceStamp, InventorySlot;
    }

    /// <summary><c>KilnSetChangeV1</c>, 72 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetChange
    {
        public uint Size, EquipmentSlot;
        public SetSlotValue Before, After;
    }

    /// <summary><c>KilnSetResultV1</c>, 1760 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetResult
    {
        public uint Size, Status, GameError, FailedOperation;
        public ulong Ticket, DecisionId, WorldEpoch, AppliedStep;
        public uint Player, Flags, ChangeCount, Refusal;
        public fixed byte Reason[256];
        public fixed byte Changes[20 * 72];
    }

    /// <summary>kiln.decisions' <c>KilnSetDecisionRequestV1</c> (the kind-18 payload), 824 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SetDecisionPayload
    {
        public ulong ItemsTicket, SnapshotToken;
        public uint Count, Flags;
        public fixed byte Entries[20 * 40];
    }

    /// <summary>The v6 table: v5 followed by the equipment-set functions. Read a member only when the header's size covers
    /// it (<see cref="HasV6"/>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV6
    {
        public TableV5 V5;
        public delegate* unmanaged[Cdecl]<ulong, uint, SetSnapshot*, int> SetCapture;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> SetRelease;
        public delegate* unmanaged[Cdecl]<SetStatus*, int> GetSetStatus;
        public delegate* unmanaged[Cdecl]<ulong, ulong, EquipDecisions.SetOp*, uint, SetPlan*, int> SetPlanOf;
        public delegate* unmanaged[Cdecl]<ulong, SetRequest*, delegate* unmanaged[Cdecl]<SetResult*, void*, void>, void*, ulong*, int> EquipSet;
        public delegate* unmanaged[Cdecl]<ulong, int> SetCancel;
    }

    /// <summary>The v7 table (the gem wave): v6 followed by socket_roll.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV7
    {
        public TableV6 V6;
        public delegate* unmanaged[Cdecl]<ulong, SocketRoll*, int> SocketRollOf;
    }

    // KilnItemsRarityRoll.sources (v8, package P5)
    public const uint RollCraft = 0x1, RollLoot = 0x2, RollGive = 0x4;

    /// <summary>KilnItemsRarityRoll (v8, package P5): one item type's rarity rule at creation.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RarityRoll
    {
        public uint Item;
        public uint Sources;
        public fixed float Weights[5];
        public uint Reserved;
    }

    /// <summary>KilnItemsRollStatus (v8, package P5).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RollStatus
    {
        public uint Size;
        public byte Available;
        public byte R0, R1, R2;
        public uint Items, Owners, Conflicts, R3;
        public fixed ulong Rolled[5];
        public ulong Kept, NoSlot, Faults;
        public fixed byte Detail[160];
    }

    // ---- v8 (0.2.0-dev.29.19, the equipment wave): gear (KilnItemsGearRequest, KilnItemsGearState, KilnItemsGearResult,
    // KilnItemsGearStatus; kiln_items.h "gear") ----
    public const uint GearAvailable = 0x01, GearUnlocked = 0x02, GearLevel = 0x04, GearForceSlot = 0x08, GearDurabilityRefresh = 0x10,
        GearDurabilityRepair = 0x20, GearDryRun = 0x40, GearNotEquipped = 0x80;
    // package P7: a level outside the type's range, max durability (kiln_items.h)
    public const uint GearAnyLevel = 0x200, GearDurabilityMax = 0x400;
    public const int GearMaxDurability = 1000000;
    public const int GearDone = 0, GearStale = 1, GearNotGear = 2, GearNoSlot = 3, GearOutOfRange = 4, GearEquipped = 5, GearUnavailableStatus = 6,
        GearDropped = 7, GearRefused = 8;
    public const uint GearChangedPerks = 0x01, GearChangedLevel = 0x02, GearChangedDurability = 0x04, GearChangedState = 0x08,
        GearChangedDurabilityMax = 0x10;
    public const uint GearHasPerks = 0x01, GearHasLevel = 0x02, GearHasDurability = 0x04, GearEquippedNow = 0x08, GearDurabilityInit = 0x10;

    /// <summary><c>KilnItemsGearRequest</c>, 40 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GearRequest
    {
        public uint Size;
        public uint Flags;
        public uint Entity;
        public uint Stamp;
        public uint Item;
        public byte Available;
        public byte Unlocked;
        public ushort Reserved0;
        public uint Level;
        public int DurabilityMax;  // package P7 (was reserved)
        public uint R0, R1;
    }

    /// <summary><c>KilnItemsGearState</c>, 44 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GearState
    {
        public uint Flags;
        public uint Item;
        public byte Available;
        public byte Unlocked;
        public byte Rarity;
        public byte TypeRarity;
        public byte MaxAvailable;
        public byte RarityUi;
        public byte LevelUi;
        public byte Reserved0;
        public uint Level;
        public uint LevelMax;
        public uint TypeLevelMin;
        public uint TypeLevelMax;
        public int Durability;
        public int DurabilityMax;
        public ushort State;
        public ushort Reserved1;
    }

    /// <summary><c>KilnItemsGearResult</c>, 296 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GearResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Entity;
        public uint Stamp;
        public uint Flags;
        public uint Changed;
        public uint Steps;
        public uint Reserved;
        public ulong Step;
        public GearState Before;
        public GearState After;
        public fixed byte Message[160];
    }

    /// <summary><c>KilnItemsGearStatus</c>, 192 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GearStatus
    {
        public uint Size;
        public byte Available;
        public byte Program;
        public byte Replicated;
        public byte Saved;
        public uint Pending;
        public ulong Writes;
        public ulong Refused;
        public fixed byte Detail[160];
    }

    // ---- v8 (package P3): upgrades (KilnItemsUpgradeRequest, KilnItemsUpgradeResult; kiln_items.h "upgrades") ----
    public const uint UpgradeWrite = 0x01, UpgradeDryRun = 0x02;
    public const int UpgradeCantPay = 9, UpgradeBadPayer = 10, UpgradeChanged = 11;
    public const int UpgradeMaxCosts = 8, UpgradeMaxPayers = 8, UpgradeMaxCount = 100000;

    /// <summary><c>KilnItemsUpgradeRequest</c>, 160 bytes. Costs are (item, count) pairs: Costs[2 i], Costs[2 i + 1].</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UpgradeRequest
    {
        public uint Size;
        public uint Flags;
        public uint Player;
        public uint CostCount;
        public uint PayerCount;
        public uint ExpectAvailable;
        public GearRequest Gear;
        public fixed uint Costs[16];
        public fixed uint Payers[8];
    }

    /// <summary><c>KilnItemsUpgradeResult</c>, 392 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UpgradeResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Flags;
        public uint CostCount;
        public uint PaidFromPlayer;
        public uint Reserved;
        public fixed uint Paid[8];
        public fixed uint Have[8];
        public GearResult Gear;
    }

    /// <summary>The v8 table (the equipment wave): v7 followed by gear_write, gear_read, gear_status (package P1, typed by
    /// the managed gear API of package P2; 320 bytes), rarity_rolls, roll_status (package P5; 336 bytes) and gear_upgrade
    /// (package P3; 344 bytes). Read a member only when the header's size covers it.</summary>
    /// <summary>Where the v8 gear functions end (offsetof(KilnItemsApi, rarity_rolls)): a table this long has gear.</summary>
    public const uint V8GearEnd = 320;

    /// <summary>Where the rarity rolls end (offsetof(KilnItemsApi, gear_upgrade)): a table this long has rarity_rolls.</summary>
    public const uint V8RollsEnd = 336;

    /// <summary>Where gear_upgrade ends: a table this long has upgrades (package P3).</summary>
    public const uint V8UpgradeEnd = 344;

    /// <summary>Where the scales end (package P7): a table this long has gear_scale, gear_scale_read and scale_status.</summary>
    public const uint V8ScaleEnd = 368;

    // ---- v8 (package P7): power and armour scales (KilnItemsGearScale, KilnItemsScaleStatus) ----
    public const uint ScalePower = 0x01, ScaleArmor = 0x02;
    public const float ScaleMin = 0.01f, ScaleMax = 100f;
    public const int ScaleCapacity = 8192;

    /// <summary><c>KilnItemsGearScale</c>, 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GearScale
    {
        public uint Size;
        public uint Flags;
        public uint Entity;
        public uint Stamp;
        public float Power;
        public float Armor;
        public uint R0, R1;
    }

    /// <summary><c>KilnItemsScaleStatus</c>, 208 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ScaleStatus
    {
        public uint Size;
        public byte Power, Armor, PowerHooked, ArmorHooked;
        public uint Entries, Capacity, Reserved;
        public ulong Powered, Armored, Faults;
        public fixed byte Detail[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TableV8
    {
        public TableV7 V7;
        public delegate* unmanaged[Cdecl]<ulong, GearRequest*, delegate* unmanaged[Cdecl]<GearResult*, void*, void>, void*, ulong*, int> GearWrite;
        public delegate* unmanaged[Cdecl]<uint, uint, GearState*, int> GearRead;
        public delegate* unmanaged[Cdecl]<GearStatus*, int> GetGearStatus;
        public delegate* unmanaged[Cdecl]<ulong, RarityRoll*, uint, uint, int> RarityRolls;
        public delegate* unmanaged[Cdecl]<RollStatus*, int> RollStatusOf;
        public delegate* unmanaged[Cdecl]<ulong, UpgradeRequest*, delegate* unmanaged[Cdecl]<UpgradeResult*, void*, void>, void*, ulong*, int> GearUpgrade;
        // package P7 (368 bytes)
        public delegate* unmanaged[Cdecl]<GearScale*, int> GearScaleSet;
        public delegate* unmanaged[Cdecl]<uint, uint, GearScale*, int> GearScaleRead;
        public delegate* unmanaged[Cdecl]<ScaleStatus*, int> ScaleStatusOf;
    }

    // ---- v9 (0.2.0-dev.30.3, GitHub issue #112): renaming a container (KilnItemsRenameRequest, -Result, -Status) ----

    /// <summary>Where the renames end: a table this long has container_rename and rename_status.</summary>
    public const uint V9RenameEnd = 384;

    /// <summary>KILN_ITEMS_NAME_CAPACITY, KILN_ITEMS_NAME_MAX_BYTES, KILN_ITEMS_NAME_MAX_CHARS.</summary>
    public const int NameCapacity = 256, NameMaxBytes = 64, NameMaxChars = 48;

    // KilnItemsRenameResult.status
    public const int RenameDone = 0, RenameGone = 1, RenameNotContainer = 2, RenameTooLong = 3, RenameBadText = 4, RenameNotServer = 5,
        RenameFull = 6, RenameUnavailable = 7, RenameDropped = 8;

    /// <summary><c>KilnItemsRenameRequest</c>, 272 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenameRequest
    {
        public uint Size;
        public uint Entity;
        public uint Len;
        public uint Reserved;
        public fixed byte Text[NameCapacity];
    }

    /// <summary><c>KilnItemsRenameResult</c>, 192 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenameResult
    {
        public uint Size;
        public int Status;
        public ulong Ticket;
        public uint Entity;
        public uint GameError;
        public uint Steps;
        public byte Changed, Cleared;
        public ushort Reserved;
        public fixed byte Message[160];
    }

    /// <summary><c>KilnItemsRenameStatus</c>, 184 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenameStatus
    {
        public uint Size;
        public byte Available, Feature, Server, Reserved0;
        public uint Pending, Done, Refused, Reserved1;
        public fixed byte Detail[160];
    }

    /// <summary>The v9 table: v8 followed by container_rename and rename_status.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableV9
    {
        public TableV8 V8;
        public delegate* unmanaged[Cdecl]<ulong, RenameRequest*, delegate* unmanaged[Cdecl]<RenameResult*, void*, void>, void*, ulong*, RenameResult*, int> ContainerRename;
        public delegate* unmanaged[Cdecl]<RenameStatus*, int> RenameStatusOf;
    }

    /// <summary>True when a table of <paramref name="size"/> bytes has the v6 member that ends at <paramref name="end"/>.</summary>
    public static bool HasV6(uint size, int end) => size >= (uint)end;

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    // KilnResult
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5, InternalError = 7;
    [StructLayout(LayoutKind.Sequential)]
    public struct GiveEquipRequest
    {
        public uint Size, Player, Item, Count, Flags;
        public byte EquipmentSlot;
        public fixed byte Reserved[3];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TableWithGiveEquip
    {
        public TableV9 V9;
        public delegate* unmanaged[Cdecl]<ulong, GiveEquipRequest*, delegate* unmanaged[Cdecl]<EquipResult*, void*, void>, void*, ulong*, int> GiveEquip;
    }

}
