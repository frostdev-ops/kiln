// SPDX-License-Identifier: MIT
// The native mirror of kiln.knowledge (native/services/quests/kiln_knowledge.h). Internal: the public surface is
// Knowledge.cs, KnowledgeJournal.cs and KnowledgeEvents.cs.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

internal static unsafe class KnowledgeNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Info
    {
        public uint Size, Id, Flags;
        public byte WorldType, PlayerType, SkillPoints, Reserved;
        public uint WorldFlags, PlayerFlags, ExperienceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct State
    {
        public uint Size, Target, Id, Value;
        public byte Type, NewMask, Present, Reserved;
        public uint Reserved2;
        public ulong Step;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Entry
    {
        public uint Id, Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        public uint Size, Op;
        public ulong Request;
        public uint Target, Id;
        public int Status;
        public uint Steps, Before, After;
        public byte Changed;
        public fixed byte Reserved[7];
        public ulong Step;
        public fixed byte Reason[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Request
    {
        public uint Size, Op, Target, Id, Value, Flags;
        public nint Callback, User;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Query
    {
        public uint Id, CompareValue;
        public byte Compare, Type, PlayerOnly, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Name
    {
        public uint Size, Kind, Id, Catalog, Index;
        public Query Query;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JQuest
    {
        public uint Size, EntryId, Name, Priority;
        public byte Source, Type, Main, Tutorial, AllPlayers, R1, R2, R3;
        public uint Steps, Rewards;
        public fixed uint RewardXp[4];
        public fixed uint RewardItem[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JStep
    {
        public uint Size, EntryId, Name, Text, MapMarker;
        public Query Visible, Done;
        public uint ProgressSteps, RecommendedLevel, ItemIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Npc
    {
        public uint Size, NpcId, Name, Workshop, Item;
        public Query Unlock;
        public byte Unlocked, CraftingAssistant, R1, R2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event
    {
        public ulong Seq, Step;
        public uint Kind, Target, Id, OldValue, NewValue, Npc;
        public ulong Request;
        public uint Flags, Extra;
        public float Distance;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Op
    {
        public uint Id;
        public byte Type, R1, R2, R3;
        public uint Value, Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Settings
    {
        public uint Size;
        public byte VetoClient, CheckDialogue, LogClient, Reserved;
        public float DialogueRange;
        public uint AllowCount, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public uint Size;
        public byte Knowledge, Changes, Journal, Npc, Dialogue, ClientInput, Program, World;
        public uint WorldObjects, PlayerObjects, WorldEntries, Players, Npcs, Quests, Queries, Pending;
        public ulong Submitted, Applied, Failed, ChangesSeen, ClientOps, ClientVetoed, DialogueEvents, DialogueHeld,
            DialogueDropped, LastSeq;
        public uint QueueMax, Applying;
        public fixed byte Reason[160];
    }

    // The Mother Flame (appended to the native table; KilnFlameStatus 160 bytes, KilnFlameResult 152 bytes)
    [StructLayout(LayoutKind.Sequential)]
    internal struct FlameState
    {
        public uint Size;
        public byte Available, Found, Tracked, Reserved;
        public uint Entity, Level, Cap, NetworkLevel, SystemLevel, Pending;
        public ulong Step, Submitted, Applied, Failed;
        public fixed byte Reason[96];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FlameOutcome
    {
        public uint Size;
        public int Status;
        public ulong Request;
        public uint Requested, Before, After, Cap;
        public byte Marked, Mirrored, Clamped, Reserved;
        public uint StepsToMirror, SystemLevel, Reserved2;
        public ulong Step;
        public fixed byte Reason[96];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> Info;
        public delegate* unmanaged[Cdecl]<uint, uint, State*, int> Get;
        public delegate* unmanaged[Cdecl]<uint, Entry*, uint, uint*, int> List;
        public delegate* unmanaged[Cdecl]<ulong, Request*, ulong*, int> Submit;
        public delegate* unmanaged[Cdecl]<ulong, Result*, int> Result;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, ulong*, int> Grant;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, ulong*, int> Revoke;
        public delegate* unmanaged[Cdecl]<uint, Query*, uint, byte*, int> Query;
        public delegate* unmanaged[Cdecl]<NativeStr, Name*, int> Resolve;
        public delegate* unmanaged[Cdecl]<uint, uint, byte*, nuint, nuint*, int> NameOf;
        public delegate* unmanaged[Cdecl]<uint, uint> CatalogCount;
        public delegate* unmanaged[Cdecl]<uint, uint, Name*, byte*, nuint, nuint*, int> CatalogAt;
        public delegate* unmanaged[Cdecl]<uint, JQuest*, int> JournalQuest;
        public delegate* unmanaged[Cdecl]<uint, uint, JStep*, int> JournalStep;
        public delegate* unmanaged[Cdecl]<uint, Npc*, byte*, nuint, nuint*, int> Npc;
        public delegate* unmanaged[Cdecl]<ulong, Event*, uint, uint*, int> Events;
        public delegate* unmanaged[Cdecl]<ulong> LastSeq;
        public delegate* unmanaged[Cdecl]<ulong, Op*, uint, uint*, int> HeldOps;
        public delegate* unmanaged[Cdecl]<ulong, int, int> Decide;
        public delegate* unmanaged[Cdecl]<uint, int> Listen;
        public delegate* unmanaged[Cdecl]<Settings*, int> GetSettings;
        public delegate* unmanaged[Cdecl]<Settings*, int> Configure;
        public delegate* unmanaged[Cdecl]<uint*, uint, int> Allow;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> NativeManifest;
        public delegate* unmanaged[Cdecl]<uint, uint, uint*, uint*, uint, int> LabRecord;
        // appended with the flame (managed and native ship together: Services.Get checks the whole table's size)
        public delegate* unmanaged[Cdecl]<FlameState*, int> FlameStatus;
        public delegate* unmanaged[Cdecl]<ulong, uint, ulong*, int> FlameSet;
        public delegate* unmanaged[Cdecl]<ulong, FlameOutcome*, int> FlameResult;
    }

    internal const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5,
        Internal = 7;

    private static Table* _table;
    private static bool _looked;

    /// <summary>Forgets a remembered miss, so the next use looks for the table again (ServiceCaps.WhenFound: the refresh
    /// found it after all).</summary>
    internal static void LookAgain()
    {
        if (_table == null) _looked = false;
    }

    internal static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _looked = true;
            if (sizeof(Info) != 28 || sizeof(State) != 32 || sizeof(Result) != 184 || sizeof(Request) != 40 ||
                sizeof(Query) != 12 || sizeof(Name) != 32 || sizeof(JQuest) != 64 || sizeof(JStep) != 56 ||
                sizeof(Npc) != 36 || sizeof(Event) != 64 || sizeof(Op) != 16 || sizeof(Settings) != 20 ||
                sizeof(Status) != 296 || sizeof(FlameState) != 160 || sizeof(FlameOutcome) != 152)
                return null;  // layout drift between this assembly and the header: refuse rather than misread
            var t = Services.Get<Table>("kiln.knowledge", 1);
            if (t != null && t->Header.Size < (uint)sizeof(Table)) t = null;
            _table = t;
            return _table;
        }
    }

    internal static Table* Require(string api)
    {
        var t = T;
        if (t != null) return t;
        QuestsCaps.Rows.Ensure();
        throw ServiceCaps.Unavailable(QuestsCaps.Service, $"Knowledge.{api}: kiln.knowledge is unavailable on this server");
    }

    internal static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    internal static string Code(int rc) => rc switch
    {
        Ok => "ok",
        InvalidArgument => "invalid",
        NotFound => "not_found",
        Unavailable => "unavailable",
        WrongThread => "wrong_thread",
        BufferTooSmall => "busy",
        _ => "internal",
    };

    internal static void Check(int rc, string api)
    {
        switch (rc)
        {
            case Ok: return;
            // not a capability: "no world" and "the player is not in game" are refusals about this call's target
            case Unavailable: throw new NotSupportedException($"Knowledge.{api}: unavailable (no world, the player is not in game, or the part is off on this build)");
            case WrongThread: throw new InvalidOperationException($"Knowledge.{api}: call it on the tick thread");
            case InvalidArgument: throw new ArgumentException($"Knowledge.{api}: invalid arguments");
            default: throw new InvalidOperationException($"Knowledge.{api}: kiln.knowledge returned {rc}");
        }
    }

    internal static KnowledgeQuery FromNative(in Query q) =>
        new(q.Id, (KnowledgeQueryType)q.Type, (KnowledgeCompare)q.Compare, q.CompareValue, q.PlayerOnly != 0);

    internal static Query ToNative(in KnowledgeQuery q) => new()
    {
        Id = q.Id, CompareValue = q.CompareValue, Compare = (byte)q.Compare, Type = (byte)q.Type, PlayerOnly = q.PlayerOnly ? (byte)1 : (byte)0,
    };
}
