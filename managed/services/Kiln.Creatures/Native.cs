// SPDX-License-Identifier: MIT
// The native kiln.creatures table (native/core/abi/services/kiln_creatures.h). Field order and types must match the C
// header.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class CreaturesNative
{
    public const string Name = "kiln.creatures";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;

    // fields (KILN_CF_*)
    public const uint Level = 1, Faction = 2, Scale = 3, Xp = 4, LootLabels = 5, LootSlots = 6, LootDisabled = 7,
        Resist = 8, Speed = 9, Damage = 10, FeaturesOn = 11, FeaturesOff = 12, Home = 13;

    public const uint EvState = 1, EvTarget = 2;
    public static uint Mask(uint kind) => 1u << (int)kind;

    public const uint CmdAttack = 1, CmdGuard = 2, CmdStopGuard = 3, CmdHeal = 4, CmdControl = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct GuidBytes
    {
        public fixed byte B[16];

        public Guid ToGuid()
        {
            fixed (byte* p = B) return new Guid(new ReadOnlySpan<byte>(p, 16));
        }

        public static GuidBytes From(Guid g)
        {
            var r = new GuidBytes();
            g.TryWriteBytes(new Span<byte>(r.B, 16));
            return r;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Template
    {
        public uint Size;
        public uint Kind;
        public GuidBytes Guid;
        public fixed byte Name[128];
        public fixed byte Display[64];
        public uint Flags;
        public byte Faction, EnemyClass, LootSlotsMin, LootSlotsMax;
        public uint Level, LevelMax;
        public float XpMod, Scale;
        public GuidBytes Arsenal, LootLabels, Model, AnimationGraph;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Value
    {
        public double Number;
        public GuidBytes Guid;
        public Vec3 Vec;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Size;
        public uint Kind;
        public GuidBytes Template;
        public uint Flags;
        public byte State, AwarenessState, Faction, LootSlotsMin, LootSlotsMax, LootDisabled, P0, P1;
        public float Awareness;
        public uint Level, LevelMax;
        public int Health, HealthMax;
        public float Scale, XpMod, SpeedFactor, DamageMultiplier;
        public uint FeaturesTemplate, FeaturesEnabled, FeaturesDisabled;
        public uint Target, Guard;
        public Vec3 Home;
        public GuidBytes LootLabels;
        public uint Layers, PrivateCopies;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size;
        public uint Kind;
        public uint Entity;
        public byte From, To, Condition, Pad;
        public uint Target, Previous;
        public uint Reserved;
        public ulong Step;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Command
    {
        public uint Size;
        public uint Kind;
        public uint Entity, Target;
        public float Value;
        public uint Pool;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NpcCommand
    {
        public uint Size;
        public uint Type;
        public uint NpcId;
        public uint Reserved;
        public double X, Y, Z;
        public float Rx, Ry, Rz, Rw;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Slots, Ai, Ready, Arsenals, Reflection, Commands, Marks, LabReady;
        public uint Catalog, CatalogLoaded, EngineCreatures, ArsenalCount, Entities, Layers, PrivateCopies, PendingFree;
        public ulong StateChanges, EventsDelivered, EventsDropped, CommandsSent, MarksWritten, CopyReads;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint> TemplateCount;
        public delegate* unmanaged[Cdecl]<uint, Template*, int> TemplateAt;
        public delegate* unmanaged[Cdecl]<GuidBytes*, Template*, int> TemplateGet;
        public delegate* unmanaged[Cdecl]<NativeStr, Template*, int> TemplateFind;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> Inspect;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, Value*, int> Set;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, Value*, int> Get;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, uint*, int> Reset;
        public delegate* unmanaged[Cdecl]<ulong, uint, NativeStr, NativeStr, NativeStr, int> AiSet;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, NativeStr, byte*, nuint, nuint*, uint*, int> AiGet;
        public delegate* unmanaged[Cdecl]<ulong, uint, NativeStr, NativeStr, uint*, int> AiReset;
        public delegate* unmanaged[Cdecl]<uint, uint*, int> AiRefresh;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, nuint*, int> AiFields;
        public delegate* unmanaged[Cdecl]<ulong, uint> ReleaseOwner;
        public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<Event*, uint, void*, uint>, void*, int> SetListener;
        public delegate* unmanaged[Cdecl]<Command*, int> SendCommand;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, int> SetReady;
        public delegate* unmanaged[Cdecl]<uint, GuidBytes*, uint*, int> ArsenalOf;
        public delegate* unmanaged[Cdecl]<NpcCommand*, int> SendNpcCommand;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, byte*, nuint, nuint*, int> ArsenalDefine;
        public delegate* unmanaged[Cdecl]<ulong, GuidBytes*, int> ArsenalRemove;
        public delegate* unmanaged[Cdecl]<GuidBytes*, byte*, nuint, nuint*, int> ArsenalInfo;
        public delegate* unmanaged[Cdecl]<uint*, int> ArsenalRefresh;
        public delegate* unmanaged[Cdecl]<uint*, uint*, int> ArsenalSelftest;
        public delegate* unmanaged[Cdecl]<RuleWork*, uint, uint> RulesDrain;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, int> RulesConditionSet;
        public delegate* unmanaged[Cdecl]<uint, int> RulesConditionClear;
        public delegate* unmanaged[Cdecl]<uint, ulong*, ulong*, int> RulesConditionStats;
        public delegate* unmanaged[Cdecl]<uint, void> RulesPump;
        public delegate* unmanaged[Cdecl]<uint, uint, int> RulesPhaseSet;
        public delegate* unmanaged[Cdecl]<uint, uint*, int> RulesPhaseGet;
        public delegate* unmanaged[Cdecl]<RulesStatus*, int> GetRulesStatus;
        public delegate* unmanaged[Cdecl]<ulong, uint, void*, void*, int> RulesJobHandler;
        public delegate* unmanaged[Cdecl]<ulong, uint, AttackOverride*, int> SetAttackOverride;
        public delegate* unmanaged[Cdecl]<ulong, GuidBytes*, uint, int> AttackPickArsenal;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, int> AiSwitch;
        public delegate* unmanaged[Cdecl]<uint, uint*, int> AiSwitchGet;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, float, int> TargetPrefer;
    }

    public const uint WorkAction = 1, WorkCondition = 2, WorkPick = 3, WorkGone = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RuleWork
    {
        public uint Kind;
        public uint Entity;
        public uint Id;
        public uint Target;
        public ulong Step;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AttackOverride
    {
        public uint Size;
        public int Forced;
        public uint Allow;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RulesStatus
    {
        public uint Size;
        public byte Writer, Dispatcher, Attacks, RuleHooks, AttackHooks, AiSwitch, R0, R1;
        public uint Arsenals, Tracked, Overrides, AiOff, Handlers, MapCapacity, MapCount, R2;
        public ulong Constraints, ConstraintsTrue, Actions, ActionsDropped, Selects, SelectsForced, SelectsFiltered,
            PicksWaiting, FollowupsRefused, Starts, StartsOffOverride, SequencesRefused, PreNs, PreCalls, JobTicks, JobCalls, JobTickHz;
    }

    private static Table* _t;

    /// <summary>The table, or null when kiln.creatures is not loaded (looked up again until it is found).</summary>
    public static Table* T => _t != null ? _t : _t = Services.Get<Table>(Name, Version);

    public static Table* Require(string api)
    {
        var t = T;
        if (t != null) return t;
        CreaturesCaps.Rows.Ensure();
        throw ServiceCaps.Unavailable(CreaturesCaps.Service, $"{api}: kiln.creatures is not available on this server build");
    }

    public static string Describe(int rc) => rc switch
    {
        InvalidArgument => "invalid argument or value",
        NotFound => "no such entity (or name)",
        Unavailable => "unavailable (the entity lacks that component, or this build/world does not support it)",
        WrongThread => "not on the tick thread",
        BufferTooSmall => "too many pending requests",
        6 => "revoked",
        _ => $"error {rc}",
    };

    /// <summary>UTF-8 view of a string for one native call.</summary>
    public readonly ref struct Utf8
    {
        private readonly byte[] _bytes;
        public Utf8(string? s) => _bytes = System.Text.Encoding.UTF8.GetBytes(s ?? "");
        public NativeStr Str(byte* p) => new() { Ptr = p, Len = (nuint)_bytes.Length };
        public byte[] Bytes => _bytes;
    }

    public static string Text(byte* p, int cap)
    {
        var n = 0;
        while (n < cap && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }
}
