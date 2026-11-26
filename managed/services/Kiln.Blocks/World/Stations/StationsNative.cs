// SPDX-License-Identifier: MIT
// The native table of kiln.stations (native/core/abi/services/kiln_stations.h, in kiln-items.dll). Blittable; field order
// is the ABI (sizes asserted in native/services/items/tests/stations_test.cpp and in StationsTests).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class StationsNative
{
    public const string Name = "kiln.stations";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;
    // result statuses (KilnStationResult.status) the managed side looks at
    public const int StLocked = 9, StUnavailable = 11, StDropped = 12, StTimedOut = 13, StCanceled = 15;
    public const int MaxBases = 16, MaxAltars = 16, MaxInputs = 32, MaxOutputs = 8, MaxChanges = 32, MaxKeep = 64, MaxChests = 64;

    // actions
    public const uint Fill = 1, Stop = 2, Resume = 3, Select = 4, CollectAction = 5;
    public const uint CollectOutputs = 0x10000, CollectLeftovers = 0x20000, CollectToBase = 0x40000;  // native collect's own bits
    // request flags
    public const uint FillAdd = 0x1, FillExact = 0x2, DryRun = 0x4, NoWater = 0x8, AfterCraft = 0x10, KeepStopped = 0x20,
        AllowLocked = 0x40, FillNoOutputs = 0x80, FillLinked = 0x100,  // FillLinked: 0.2.0-dev.31.12 (GitHub #132)
        FillItem = 0x200;  // 0.2.0-dev.31.16 (GitHub #132): the station screen's per-ingredient transfer, only where FillFlags names it
    // event kinds
    public const uint EvState = 1, EvPlaced = 2, EvRemoved = 3, EvBases = 4;
    // KilnStation.flags
    public const uint FlagStopPending = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct BaseId
    {
        public fixed byte Guid[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Choice
    {
        public uint Category;
        public byte Index, R0, R1, R2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stack
    {
        public uint Item, Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Base
    {
        public uint Size, Index;
        public BaseId Id;
        public uint AltarCount;
        public fixed uint Altars[16];
        public fixed byte AltarIds[16 * 16];
        public uint StorageCount, WaterCount, ZoneCount, StationCount, Version;
        public Vec3 Min, Max;
        public fixed uint Reserved[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Station
    {
        public uint Size, Entity, Workshop;
        public int BaseIndex;
        public BaseId Base;
        public Vec3 Position;
        public byte State, StoppedByPlayer, UseAllOptions, Needs;
        public uint RunningRecipe, SelectedRecipe;
        public Choice R0, R1, R2, R3, S0, S1, S2, S3;
        public uint CraftsLoaded;
        public long RecipeStartNs, PauseNs, NowNs, DurationNs;
        public float Progress;
        public int Water, WaterMax;
        public uint Flags;
        public fixed uint Reserved[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Item, Count, Category, CategoryCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Recipe
    {
        public uint Size, Id, Workshop, InputCount, OutputCount, WaterCharges;
        public long DurationNs;
        public uint KnowledgeId, KnowledgeValue;
        public byte KnowledgeCompare, KnowledgeType, KnowledgePlayerOnly, Reserved0;
        public uint Reserved1;
        public fixed uint Inputs[MaxInputs * 4];   // Input x 32
        public fixed uint Outputs[MaxOutputs * 2]; // Stack x 8
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Request
    {
        public uint Size, Action, Flags, Station, Crafts, RecipeId;
        public Choice C0, C1, C2, C3;
        public uint Player, KeepCount;
        public Stack* Keep;
        public uint Item;  // FillItem's item (0.2.0-dev.31.16; reserved[0] before, so 0 everywhere else)
        public fixed uint Reserved[3];
        public uint ChestCount, ChestReserved;
        public uint* Chests;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MoveChange
    {
        public ulong FromSlot, ToSlot;
        public uint Item, Amount, Pide, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Result
    {
        public uint Size;
        public int Status;
        public uint Action, Flags, Station, RecipeId, CraftsRequested, CraftsBefore, CraftsAfter, CraftsAdded;
        public int GameError;
        public uint ChangeCount;
        public byte StateBefore, StateAfter, StoppedBefore, StoppedAfter;
        public byte LimitedBy, R0, R1, R2;
        public uint ShortItem, FromOutputs;  // FromOutputs: 0.2.0-dev.29.17 (was reserved, 0)
        public ulong Ticket, Step;
        public fixed byte Changes[MaxChanges * 32];  // MoveChange x 32
        public fixed byte Message[128];
        public uint FromChests, ReservedTail;
        public uint FromLinked, ReservedLinked;  // 0.2.0-dev.31.12: read only when Size covers them (OnDone copies Size bytes)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Plan
    {
        public uint Size;
        public int Status;
        public uint Station, RecipeId;
        public int BaseIndex;
        public uint CraftsLoaded, CraftsAffordable, CraftsPlanned, InputCount, ShortItem;
        public byte LimitedBy, UseAllOptions, R0, R1;
        public uint WaterPerCraft, WaterHave, WaterStock;
        public fixed uint Inputs[MaxInputs * 2];  // Stack x 32
        public fixed uint Have[MaxInputs];
        public fixed uint Stock[MaxInputs];
        public fixed byte Message[128];
        public fixed uint Outputs[MaxInputs];  // 0.2.0-dev.29.17: an older kiln.stations answers INVALID_ARGUMENT for a plan smaller than it knows
        public fixed uint Chests[MaxInputs];   // ordinary chests named on the fill; 0 when the plan's size stops before this
        public fixed uint Linked[MaxInputs];   // 0.2.0-dev.31.12: the player's fill-opted links; 0 from an older kiln.stations
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size, Kind;
        public ulong Seq, Step;
        public uint Station, Workshop;
        public byte StateBefore, StateAfter, StoppedBefore, StoppedAfter;
        public uint SelectedBefore, SelectedAfter, RunningBefore, RunningAfter;
        public int BaseIndex;
        public uint Reserved0;
        public ulong By;
        public Vec3 Position;
        public BaseId Base;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Read, Fill, Control, Events, Program, Watching, Knowledge, Scanned;
        public uint Bases, Stations, Pending, Done;
        public fixed byte Detail[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint*, int> BaseCount;
        public delegate* unmanaged[Cdecl]<uint, Base*, int> BaseAt;
        public delegate* unmanaged[Cdecl]<uint, Base*, int> BaseOf;
        public delegate* unmanaged[Cdecl]<Vec3*, Base*, int> BaseAtPosition;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> BaseStorage;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> BaseWater;
        public delegate* unmanaged[Cdecl]<uint, Stack*, uint, uint*, int> BaseStock;
        public delegate* unmanaged[Cdecl]<int, uint*, uint, uint*, int> Stations;
        public delegate* unmanaged[Cdecl]<uint, Station*, int> StationRead;
        public delegate* unmanaged[Cdecl]<uint, Recipe*, int> RecipeInfo;
        public delegate* unmanaged[Cdecl]<uint, Choice*, Stack*, uint, uint*, int> RecipeInputs;
        public delegate* unmanaged[Cdecl]<uint, uint*, float*, uint, uint*, int> Category;
        public delegate* unmanaged[Cdecl]<uint, uint*, int> CategoryAt;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> WorkshopRecipes;
        public delegate* unmanaged[Cdecl]<Request*, Plan*, int> MakePlan;
        public delegate* unmanaged[Cdecl]<ulong, Request*, delegate* unmanaged[Cdecl]<Result*, void*, void>, void*, ulong*, int> Submit;
        public delegate* unmanaged[Cdecl]<ulong, int> Cancel;
        public delegate* unmanaged[Cdecl]<uint, int> Watch;
        public delegate* unmanaged[Cdecl]<ulong, Event*, uint, uint*, int> Events;
        public delegate* unmanaged[Cdecl]<ulong> LastSeq;
        // ---- 0.2.0-dev.29.17 (Header.Size tells whether they are here) ----
        public delegate* unmanaged[Cdecl]<uint, uint, int> SetOutputSource;
        public delegate* unmanaged[Cdecl]<uint*, uint, uint*, int> OutputSources;
        public delegate* unmanaged[Cdecl]<uint> MaxChests;
        public delegate* unmanaged[Cdecl]<ulong, Collect*, delegate* unmanaged[Cdecl]<Result*, void*, void>, void*, ulong*, int> Collect;
        public delegate* unmanaged[Cdecl]<uint, Stack*, uint, uint*, int> BaseStockWithStations;
        // ---- the crafting menu's snapshot for Kiln clients (GitHub #68 step 2). Header.Size tells whether they are here. ----
        public delegate* unmanaged[Cdecl]<Stack*, uint, int> SetCraftStock;
        public delegate* unmanaged[Cdecl]<uint*, uint, uint*, uint, uint*, int> CraftItems;
        // ---- the station screen's fill buttons for Kiln clients (GitHub #68, 0.2.0-dev.29.19). Header.Size tells. ----
        public delegate* unmanaged[Cdecl]<FillAnswer*, uint, int> SetFillAnswers;
        public delegate* unmanaged[Cdecl]<FillKey*, uint, uint*, int> FillAsks;
        public delegate* unmanaged[Cdecl]<ulong, SourceRequest*, ulong*, int> AddSource;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> RemoveSource;
        public delegate* unmanaged[Cdecl]<ulong, void> CancelSources;
        public delegate* unmanaged[Cdecl]<uint, uint, Stack*, uint, uint*, int> SourceStock;
        public delegate* unmanaged[Cdecl]<uint, Stack*, uint, int> SetDyeStock;
        // ---- locks from linked sources (GitHub #147, 0.2.0-dev.31.12). Header.Size tells whether they are here. ----
        public delegate* unmanaged[Cdecl]<uint, Stack*, uint, int> SetLockStock;
        public delegate* unmanaged[Cdecl]<uint*, uint, uint*, int> LockAsks;
        // ---- block placements from linked sources (GitHub #132, 0.2.0-dev.31.12). Header.Size tells whether it is here. ----
        public delegate* unmanaged[Cdecl]<uint, Stack*, uint, int> SetBuildStock;
        // ---- the FILL flags this kiln.stations takes (GitHub #132, 0.2.0-dev.31.16). Header.Size tells whether it is here. ----
        public delegate* unmanaged[Cdecl]<uint> FillFlags;
        // ---- the skill tree's respec from linked sources (GitHub #150, 0.2.0-dev.31.18). Header.Size tells whether it is here. ----
        public delegate* unmanaged[Cdecl]<uint, Stack*, uint, int> SetRespecStock;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SourceRequest
    {
        public uint Size, Player, Container, Order, Filter, ItemCount;
        public uint* Items;
        // since 0.2.0-dev.31.12 (GitHub #147): an older kiln.stations reads the first 32 bytes only
        public uint Flags, Reserved;
    }

    /// <summary>KilnCraftSourceRequest.flags: KILN_CRAFT_SOURCE_LOCKS (GitHub #147), _FILLS and _BUILDING (GitHub #132),
    /// _RESPEC (GitHub #150).</summary>
    public const uint SourceLocks = 1, SourceFills = 2, SourceBuilding = 4, SourceRespec = 8;
    /// <summary>source_stock's flags: KILN_CRAFT_SOURCE_STOCK_ALL, _LOCKS (GitHub #147), _FILLS and _BUILDING (GitHub #132),
    /// _RESPEC (GitHub #150: one row, the respec item).</summary>
    public const uint StockAll = 1, StockLocks = 2, StockFills = 4, StockBuilding = 8, StockRespec = 16;
    /// <summary>KILN_STATIONS_MAX_RESPEC_STOCK (GitHub #150).</summary>
    public const int MaxRespecStock = 16;
    /// <summary>KILN_STATIONS_MAX_LOCK_STOCK / KILN_STATIONS_MAX_LOCK_ASKS.</summary>
    public const int MaxLockStock = 256, MaxLockAsks = 16;

    /// <summary>KilnStationFillKey: a fill the station screen asks the game about (40 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FillKey
    {
        public uint Station, Recipe;
        public Choice C0, C1, C2, C3;
    }

    /// <summary>KilnStationFillAnswer: the server's answer for one key (48 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FillAnswer
    {
        public FillKey Key;
        public uint Crafts, Reserved;
    }

    /// <summary>KILN_STATIONS_MAX_FILL_KEYS: fills a Kiln client asks about, and answers it keeps.</summary>
    public const int MaxFillKeys = 16;

    [StructLayout(LayoutKind.Sequential)]
    public struct Collect
    {
        public uint Size, Station, To, Item, Flags, Reserved;
    }

    /// <summary>The rows of one crafting-stock snapshot (KILN_STATIONS_MAX_CRAFT_STOCK).</summary>
    public const int MaxCraftStock = 1024;

    /// <summary>The table size up to <see cref="Table.LastSeq"/>: kiln.stations before 0.2.0-dev.29.17.</summary>
    public static readonly int TableV1 = 8 + 21 * sizeof(nint);
    /// <summary>The table size through <see cref="Table.OutputSources"/> (station outputs, before ordinary chests).</summary>
    public static readonly int TableOutputs = TableV1 + 2 * sizeof(nint);
    /// <summary>The table size through <see cref="Table.MaxChests"/>.</summary>
    public static readonly int TableChests = TableOutputs + sizeof(nint);
    /// <summary>The table size through <see cref="Table.Collect"/>.</summary>
    public static readonly int TableCollect = TableChests + sizeof(nint);
    /// <summary>The table size through <see cref="Table.BaseStockWithStations"/>.</summary>
    public static readonly int TableStock = TableCollect + sizeof(nint);
    /// <summary>The table size through <see cref="Table.CraftItems"/>: the crafting menu's snapshot (GitHub #68 step 2).</summary>
    public static readonly int TableCraftStock = TableStock + 2 * sizeof(nint);
    /// <summary>The table size through <see cref="Table.FillAsks"/>: the station screen's fill buttons (0.2.0-dev.29.19).</summary>
    public static readonly int TableFillUi = TableCraftStock + 2 * sizeof(nint);
    /// <summary>The table size through <see cref="Table.SetDyeStock"/> (crafting sources and dyes, 0.2.0-dev.30.6).</summary>
    public static readonly int TableDyes = TableFillUi + 5 * sizeof(nint);
    /// <summary>The table size through <see cref="Table.LockAsks"/>: locks from linked sources (0.2.0-dev.31.12).</summary>
    public static readonly int TableLocks = TableDyes + 2 * sizeof(nint);
    /// <summary>The table size through <see cref="Table.SetBuildStock"/>: fills and building from linked sources (0.2.0-dev.31.12).</summary>
    public static readonly int TableBuild = TableLocks + sizeof(nint);
    /// <summary>The table size through <see cref="Table.FillFlags"/>: item fills (the per-ingredient transfer, 0.2.0-dev.31.16).</summary>
    public static readonly int TableItemFills = TableBuild + sizeof(nint);
    /// <summary>The table size through <see cref="Table.SetRespecStock"/>: the skill respec from linked sources (GitHub #150).</summary>
    public static readonly int TableRespec = TableItemFills + sizeof(nint);
    /// <summary>The plan's size before 0.2.0-dev.29.17 (no <see cref="Plan.Outputs"/>).</summary>
    public static readonly int PlanV1 = sizeof(Plan) - 3 * MaxInputs * sizeof(uint);
    /// <summary>The plan's size with outputs and without <see cref="Plan.Chests"/>.</summary>
    public static readonly int PlanChests = sizeof(Plan) - 2 * MaxInputs * sizeof(uint);
    /// <summary>The plan's size with chests and without <see cref="Plan.Linked"/> (before 0.2.0-dev.31.12).</summary>
    public static readonly int PlanLinked = sizeof(Plan) - MaxInputs * sizeof(uint);

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    public static Guid GuidOf(in BaseId id)
    {
        fixed (byte* g = id.Guid) return new Guid(new ReadOnlySpan<byte>(g, 16));
    }

    public static Guid AltarIdOf(in Base b, int i)
    {
        fixed (byte* g = b.AltarIds) return new Guid(new ReadOnlySpan<byte>(g + 16 * i, 16));
    }

    public static BaseId IdOf(Guid g)
    {
        BaseId id = default;
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        for (var i = 0; i < 16; i++) id.Guid[i] = b[i];
        return id;
    }
}
