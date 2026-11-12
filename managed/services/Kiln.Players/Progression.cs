// SPDX-License-Identifier: MIT
// Managed face of kiln.progression (native/services/players/kiln_progression.h): level, experience, skill tree and
// skill points of the players in game. Usage: docs/kiln/services/progression.md.
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>Where experience came from (the game's ExperienceSourceType; the client's "+XP" line may show it).</summary>
public enum ExperienceSource : byte
{
    None = 0,
    Exploration = 1,
    Lore = 2,
    Quest = 3,
    Combat = 4,
    Mining = 5,
    ItemSetCompleted = 6,
}

/// <summary>The game's skill node types.</summary>
public enum SkillNodeType : byte
{
    Root = 0,
    Attribute = 1,
    ActiveSkill = 2,
    PassiveSkill = 3,
}

/// <summary>What a progression request does.</summary>
public enum ProgressionOp
{
    SetLevel = 1,
    AddExperience = 2,
    SetExperience = 3,
    GiveSkillPoints = 4,
    Unlock = 5,
    Lock = 6,
    Respec = 7,
}

/// <summary>A player's progression as the game holds it, read at <see cref="Step"/>.</summary>
/// <param name="Experience">Experience into the current level.</param>
/// <param name="ExperienceNeeded">What the current level needs for the next one (0: the game has not set it yet).</param>
/// <param name="ExperiencePending">Granted experience the game's player_level counts in the next step.</param>
/// <param name="SkillPoints">Unspent skill points: <see cref="SkillPointsTotal"/> - <see cref="SkillPointsSpent"/>
/// (negative: the tree costs more than the player has; the game resets it at their next login).</param>
/// <param name="SkillPointsFromLevel">SkillPointsPerLevel x (Level - 1).</param>
/// <param name="SkillPointsFromKnowledge">From the player's skill-point knowledge (wells, "additional skill point"
/// rewards).</param>
/// <param name="GivableSkillPoints">What <see cref="PlayerProgression.GiveSkillPoints"/> can still add.</param>
public sealed record ProgressionSnapshot(
    EntityId Entity, int Level, int LevelCap, uint Experience, uint ExperienceNeeded, uint ExperiencePending,
    int SkillPoints, int SkillPointsTotal, int SkillPointsSpent, int SkillPointsFromLevel, int SkillPointsFromKnowledge,
    int SkillPointsPerLevel, int UnlockedNodes, int GivableSkillPoints, bool HasLevel, bool HasSkills, bool HasPoints,
    bool HasKnowledge, bool AtLevelCap, bool OverBudget, bool IsSnapshot, ulong Step)
{
    public override string ToString() =>
        $"level {Level}/{LevelCap}, experience {Experience}/{ExperienceNeeded}{(ExperiencePending != 0 ? $" (+{ExperiencePending})" : "")}, " +
        $"skill points {SkillPoints} of {SkillPointsTotal} ({UnlockedNodes} nodes)";
}

/// <summary>One node of the game's skill tree.</summary>
/// <param name="Cost">Skill points (per level for nodes with levels).</param>
/// <param name="MaxLevel">0: one unlock; else levels 1..MaxLevel.</param>
/// <param name="ActiveSkillItem">ItemId of the ability item the node unlocks (0: none).</param>
public readonly record struct SkillNode(
    uint Id, SkillNodeType Type, int Cost, int MaxLevel, int LevelIndex, int ClassAlignment, uint ActiveSkillItem,
    int Incoming, int Outgoing, int Index, float UiX, float UiY)
{
    public override string ToString() => $"0x{Id:x8} {Type} cost {Cost}{(MaxLevel > 0 ? $" x{MaxLevel}" : "")}";
}

/// <summary>A node a player has unlocked. <see cref="Cost"/>: what it costs now (level x cost for nodes with
/// levels).</summary>
public readonly record struct UnlockedSkillNode(uint Id, int Level, int MaxLevel, int Cost);

/// <summary>What a progression request did. <see cref="After"/> is read two steps after the change (the game's
/// player_level and player_skilltree ran by then; a grant over several levels levels up one level per step).</summary>
/// <param name="Applied">SetLevel: the level set. AddExperience: experience granted. SetExperience: the value.
/// GiveSkillPoints: points given. Unlock/Lock: 1. Respec: nodes reset.</param>
public sealed record ProgressionResult(
    ulong Request, ProgressionOp Op, EntityId Entity, bool Ok, string Status, int Applied, int Steps,
    ProgressionSnapshot? Before, ProgressionSnapshot? After, string Reason)
{
    public override string ToString() => Ok ? $"{Op} ok ({Applied}){(Reason.Length > 0 ? ": " + Reason : "")}" : $"{Op} failed: {Reason}";
}

/// <summary>What kiln.progression can do on this build.</summary>
public sealed record ProgressionStatus(
    bool Levels, bool Skills, bool SkillKnowledge, bool ExperienceEvents, bool Program, bool World, int TreeNodes,
    int KnowledgeEntries, int KnowledgePlusOne, int LevelCap, int SkillPointsPerLevel, int Pending, ulong Submitted,
    ulong Applied, ulong Failed, ulong ProgramUpdates, string Reason);

/// <summary>A player's progression (<see cref="Player.Progression"/>): reads and the changes an admin needs.</summary>
/// <remarks>Reads: on the tick thread live, elsewhere the last snapshot (every 8 steps). Changes are applied in the next
/// server-world step by the game's own code paths; the task completes on the tick thread two steps later with what
/// the game made of it. A change never throws for game reasons (offline player, cap, unknown node): read
/// <see cref="ProgressionResult.Ok"/> and <see cref="ProgressionResult.Reason"/>.</remarks>
public sealed class PlayerProgression
{
    internal PlayerProgression(EntityId entity) => Entity = entity;

    public EntityId Entity { get; }

    /// <summary>The progression now, or null when the entity is not a player in game.</summary>
    public ProgressionSnapshot? Read() => Progression.Get(Entity);

    public int Level => Read()?.Level ?? 0;
    public uint Experience => Read()?.Experience ?? 0;
    public uint ExperienceNeeded => Read()?.ExperienceNeeded ?? 0;
    /// <summary>Unspent skill points.</summary>
    public int SkillPoints => Read()?.SkillPoints ?? 0;
    public IReadOnlyList<UnlockedSkillNode> UnlockedNodes => Progression.UnlockedNodes(Entity);

    /// <summary>Sets the level (1..cap) like the game's admin command: experience into the level 0.</summary>
    public Task<ProgressionResult> SetLevel(int level) => Progression.SetLevel(Entity, level);
    /// <summary>Grants experience like a quest reward: the game levels the player up (one level per step).</summary>
    public Task<ProgressionResult> AddExperience(uint amount, ExperienceSource source = ExperienceSource.None) =>
        Progression.AddExperience(Entity, amount, source);
    public Task<ProgressionResult> SetExperience(uint experience) => Progression.SetExperience(Entity, experience);
    /// <summary>Gives unspent skill points as the game's +1 skill-point knowledge (at most
    /// <see cref="ProgressionSnapshot.GivableSkillPoints"/>; for more, raise the level).</summary>
    public Task<ProgressionResult> GiveSkillPoints(int points) => Progression.GiveSkillPoints(Entity, points);
    /// <summary>Unlocks a node (one more level for a node with levels). Without <paramref name="force"/> it must be linked
    /// to an unlocked node and affordable, as in the game's own skill tree.</summary>
    public Task<ProgressionResult> Unlock(uint node, bool force = false) => Progression.Unlock(Entity, node, force);
    /// <summary>Locks a node (one level less). Without <paramref name="force"/> the rest must stay connected.</summary>
    public Task<ProgressionResult> Lock(uint node, bool force = false) => Progression.Lock(Entity, node, force);
    /// <summary>Gives every spent point back (free, like the game's own reset).</summary>
    public Task<ProgressionResult> Respec() => Progression.Respec(Entity);

    public override string ToString() => Read()?.ToString() ?? $"entity {Entity}: no progression";
}

public sealed partial record Player
{
    /// <summary>Level, experience, skill tree and skill points of this player (kiln.progression).</summary>
    public PlayerProgression Progression => new(Entity);
}

/// <summary>Level, experience, skill tree and skill points of the players in game (kiln.progression).</summary>
public static unsafe class Progression
{
    // ---- native mirror (kiln_progression.h) ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeProgression
    {
        public uint Size, Entity, Flags, Level, LevelCap, Experience, ExperienceNeeded, ExperiencePending, PointsTotal,
            PointsFromLevel, PointsFromKnowledge, PointsSpent;
        public int PointsAvailable;
        public uint PointsPerLevel, Nodes, PointsGivable;
        public ulong Step;
        public fixed uint Reserved[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeNode
    {
        public uint Id;
        public ushort Cost;
        public byte Type, ClassAlignment, MaxLevel, LevelIndex, Incoming, Outgoing;
        public uint ActiveSkillUnlock, Index;
        public float UiX, UiY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeNodeState
    {
        public uint Id;
        public byte Level, MaxLevel;
        public ushort Cost;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRequest
    {
        public uint Size, Op, Entity, Value, Node, Flags, Source, Reserved;
        public nint Callback, User;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeResult
    {
        public uint Size, Op;
        public ulong Request;
        public uint Entity;
        public int Status;
        public uint Applied, Steps;
        public NativeProgression Before, After;
        public fixed byte Reason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeEvent
    {
        public ulong Seq;
        public uint Kind, Entity;
        public ulong Step;
        public uint OldValue, NewValue;
        public ulong Request;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeStatus
    {
        public uint Size;
        public byte Level, Skills, Knowledge, Events, Program, World, R1, R2;
        public uint TreeNodes, KnowledgeEntries, KnowledgePlus1, LevelCap, PointsPerLevel, Pending;
        public ulong Submitted, Applied, Failed, ProgramUpdates, LastSeq;
        public fixed byte Reason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSimStep
    {
        public uint Op, Node;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSimResult
    {
        public uint Op, Node;
        public byte Ok, Reachable, Connected, Level;
        public uint Spent, Nodes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeAttrProbe
    {
        public uint Size, Component;
        public fixed uint Storage[4];
        public uint SetId, SetValue, Root;
        public fixed int Slots[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeStatus*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, NativeProgression*, int> Get;
        public delegate* unmanaged[Cdecl]<uint, NativeNodeState*, uint, uint*, int> Nodes;
        public delegate* unmanaged[Cdecl]<NativeNode*, uint, uint*, int> Tree;
        public delegate* unmanaged[Cdecl]<ulong, NativeRequest*, ulong*, int> Submit;
        public delegate* unmanaged[Cdecl]<ulong, NativeResult*, int> Result;
        public delegate* unmanaged[Cdecl]<ulong, NativeEvent*, uint, uint*, int> Events;
        public delegate* unmanaged[Cdecl]<ulong> LastSeq;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint*, int> XpForLevel;
        public delegate* unmanaged[Cdecl]<NativeSimStep*, uint, NativeSimResult*, int> Simulate;
        public delegate* unmanaged[Cdecl]<NativeAttrProbe*, int> AttrProbe;
        public delegate* unmanaged[Cdecl]<uint, int> LabPlayer;
    }

    private const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;
    private const uint HasLevel = 0x01, HasSkills = 0x02, HasPoints = 0x04, HasKnowledge = 0x08, OverBudget = 0x10,
        AtCap = 0x20, Snapshot = 0x40;
    private const uint FlagForce = 0x01, FlagNoEvent = 0x02;

    private static Table* _table;
    private static bool _looked;

    private static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _looked = true;
            if (sizeof(NativeProgression) != 88 || sizeof(NativeNode) != 28 || sizeof(NativeNodeState) != 8 ||
                sizeof(NativeRequest) != 48 || sizeof(NativeResult) != 368 || sizeof(NativeEvent) != 40 ||
                sizeof(NativeStatus) != 240 || sizeof(NativeSimResult) != 20 || sizeof(NativeAttrProbe) != 52)
                return null; // layout drift between this assembly and the header: refuse rather than misread
            _table = Services.Get<Table>("kiln.progression", 1);
            return _table;
        }
    }

    private static Table* Require(string api) =>
        T != null ? T : throw new NotSupportedException($"Progression.{api}: kiln.progression is unavailable on this server");

    /// <summary>True when any part of kiln.progression works on this build (see <see cref="Status"/>).</summary>
    public static bool Available => T != null;

    /// <summary>What works and the counters; null when kiln.progression is not loaded.</summary>
    public static ProgressionStatus? Status
    {
        get
        {
            if (T == null) return null;
            NativeStatus s = default;
            s.Size = (uint)sizeof(NativeStatus);
            if (T->Status(&s) != Ok) return null;
            return new ProgressionStatus(s.Level != 0, s.Skills != 0, s.Knowledge != 0, s.Events != 0, s.Program != 0,
                s.World != 0, (int)s.TreeNodes, (int)s.KnowledgeEntries, (int)s.KnowledgePlus1, (int)s.LevelCap,
                (int)s.PointsPerLevel, (int)s.Pending, s.Submitted, s.Applied, s.Failed, s.ProgramUpdates, Text(s.Reason, 160));
        }
    }

    /// <summary>A player's progression, or null when the entity is not a player in game. Tick thread: live; other
    /// threads: the last snapshot.</summary>
    public static ProgressionSnapshot? Get(EntityId entity)
    {
        var t = Require(nameof(Get));
        NativeProgression p = default;
        p.Size = (uint)sizeof(NativeProgression);
        var rc = t->Get(entity.Value, &p);
        if (rc == NotFound) return null;
        Check(rc, nameof(Get));
        return Convert(&p);
    }

    /// <summary>The nodes a player has unlocked (tick thread). Empty when the entity is not a player in game.</summary>
    public static IReadOnlyList<UnlockedSkillNode> UnlockedNodes(EntityId entity)
    {
        var t = Require(nameof(UnlockedNodes));
        Services.RequireTickThread("Progression.UnlockedNodes");
        var buf = new NativeNodeState[256];
        uint n = 0;
        int rc;
        fixed (NativeNodeState* p = buf) rc = t->Nodes(entity.Value, p, (uint)buf.Length, &n);
        if (rc == NotFound) return [];
        Check(rc == BufferTooSmall ? Ok : rc, nameof(UnlockedNodes));
        var list = new List<UnlockedSkillNode>((int)Math.Min(n, (uint)buf.Length));
        for (var i = 0; i < Math.Min(n, (uint)buf.Length); i++)
            list.Add(new UnlockedSkillNode(buf[i].Id, buf[i].Level, buf[i].MaxLevel, buf[i].Cost));
        return list;
    }

    private static IReadOnlyList<SkillNode>? _tree;

    /// <summary>The game's skill tree (empty before the server world's first step).</summary>
    public static IReadOnlyList<SkillNode> Tree
    {
        get
        {
            if (_tree is { Count: > 0 }) return _tree;
            var t = Require(nameof(Tree));
            var buf = new NativeNode[1024];
            uint n = 0;
            int rc;
            fixed (NativeNode* p = buf) rc = t->Tree(p, (uint)buf.Length, &n);
            if (rc == NotFound) return [];
            Check(rc == BufferTooSmall ? Ok : rc, nameof(Tree));
            var list = new List<SkillNode>((int)Math.Min(n, (uint)buf.Length));
            for (var i = 0; i < Math.Min(n, (uint)buf.Length); i++)
            {
                var x = buf[i];
                list.Add(new SkillNode(x.Id, (SkillNodeType)x.Type, x.Cost, x.MaxLevel, x.LevelIndex, x.ClassAlignment,
                    x.ActiveSkillUnlock, x.Incoming, x.Outgoing, (int)x.Index, x.UiX, x.UiY));
            }
            return _tree = list;
        }
    }

    /// <summary>The node with this id, or null.</summary>
    public static SkillNode? FindNode(uint id)
    {
        foreach (var n in Tree)
            if (n.Id == id) return n;
        return null;
    }

    /// <summary>Parses a node given as "0x1234abcd", hex digits ("ffd5f940") or a decimal id; null when it is not in
    /// the tree.</summary>
    public static SkillNode? ParseNode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = text.Trim();
        const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), Hex, null, out var h0) ? FindNode(h0) : null;
        if (uint.TryParse(s, out var d) && FindNode(d) is { } byDecimal) return byDecimal;
        return uint.TryParse(s, Hex, null, out var h) ? FindNode(h) : null;
    }

    public static Task<ProgressionResult> SetLevel(EntityId entity, int level) =>
        Submit(ProgressionOp.SetLevel, entity, level < 1 ? 0u : (uint)level, 0, 0, 0);

    public static Task<ProgressionResult> AddExperience(EntityId entity, uint amount, ExperienceSource source = ExperienceSource.None,
        bool sendEvent = true) =>
        Submit(ProgressionOp.AddExperience, entity, amount, 0, sendEvent ? 0 : FlagNoEvent, (uint)source);

    public static Task<ProgressionResult> SetExperience(EntityId entity, uint experience) =>
        Submit(ProgressionOp.SetExperience, entity, experience, 0, 0, 0);

    public static Task<ProgressionResult> GiveSkillPoints(EntityId entity, int points) =>
        Submit(ProgressionOp.GiveSkillPoints, entity, points < 1 ? 0u : (uint)points, 0, 0, 0);

    public static Task<ProgressionResult> Unlock(EntityId entity, uint node, bool force = false) =>
        Submit(ProgressionOp.Unlock, entity, 0, node, force ? FlagForce : 0, 0);

    public static Task<ProgressionResult> Lock(EntityId entity, uint node, bool force = false) =>
        Submit(ProgressionOp.Lock, entity, 0, node, force ? FlagForce : 0, 0);

    public static Task<ProgressionResult> Respec(EntityId entity) => Submit(ProgressionOp.Respec, entity, 0, 0, 0, 0);

    // ---- requests ----
    private static readonly object Lock_ = new();
    private static readonly Dictionary<ulong, (TaskCompletionSource<ProgressionResult> Tcs, ProgressionOp Op, EntityId Entity)> Waiting = new();
    private static IDisposable? _pump;

    /// <summary>Queues a request. Argument errors throw (<see cref="ArgumentException"/>); a player who is not in game
    /// or a part that does not work on this build completes the task with <c>Ok = false</c>.</summary>
    private static Task<ProgressionResult> Submit(ProgressionOp op, EntityId entity, uint value, uint node, uint flags, uint source)
    {
        var t = Require(op.ToString());
        var req = new NativeRequest
        {
            Size = (uint)sizeof(NativeRequest), Op = (uint)op, Entity = entity.Value, Value = value, Node = node,
            Flags = flags, Source = source,
        };
        ulong id = 0;
        var rc = t->Submit(Services.CallerOwner, &req, &id);
        if (rc == InvalidArgument) throw new ArgumentException($"Progression.{op}: invalid arguments (value {value}, node 0x{node:x8})");
        if (rc != Ok)
        {
            var why = rc switch
            {
                NotFound => "that player is not in the world",
                Unavailable => "this does not work on this server build (see Progression.Status)",
                BufferTooSmall => "too many changes are waiting; try again in a moment",
                _ => $"kiln.progression returned {rc}",
            };
            return Task.FromResult(new ProgressionResult(0, op, entity, false, Code(rc), 0, 0, null, null, why));
        }
        var tcs = new TaskCompletionSource<ProgressionResult>(TaskCreationOptions.None);  // continuations: tick thread
        lock (Lock_)
        {
            Waiting[id] = (tcs, op, entity);
            _pump ??= ServiceTick.Add("kiln.progression results", Pump);
        }
        return tcs.Task;
    }

    private static void Pump(in TickInfo tick)
    {
        var t = T;
        if (t == null) return;
        List<(ulong, TaskCompletionSource<ProgressionResult>, ProgressionOp, EntityId)> due;
        lock (Lock_)
        {
            if (Waiting.Count == 0) return;
            due = [.. Waiting.Select(kv => (kv.Key, kv.Value.Tcs, kv.Value.Op, kv.Value.Entity))];
        }
        foreach (var (id, tcs, op, entity) in due)
        {
            NativeResult r = default;
            r.Size = (uint)sizeof(NativeResult);
            if (t->Result(id, &r) != Ok) continue;
            lock (Lock_) Waiting.Remove(id);
            tcs.TrySetResult(new ProgressionResult(id, op, entity, r.Status == Ok, Code(r.Status), (int)r.Applied,
                (int)r.Steps, r.Before.Size != 0 ? Convert(&r.Before) : null, r.After.Size != 0 ? Convert(&r.After) : null,
                Text(r.Reason, 160)));
        }
    }

    // ---- events (ProgressionEvents.cs) ----
    internal static ulong LastSequence => T != null ? T->LastSeq() : 0;

    internal static List<NativeEvent> EventsAfter(ulong cursor, int max)
    {
        var list = new List<NativeEvent>();
        var t = T;
        if (t == null) return list;
        var buf = stackalloc NativeEvent[16];
        while (list.Count < max)
        {
            uint n = 0;
            if (t->Events(cursor, buf, 16, &n) != Ok || n == 0) break;
            for (var i = 0; i < n; i++)
            {
                if (buf[i].Seq <= cursor) continue;
                cursor = buf[i].Seq;
                list.Add(buf[i]);
            }
            if (n < 16) break;
        }
        return list;
    }

    /// <summary>Diagnostics and tests.</summary>
    public static class Diagnostics
    {
        /// <summary>Lab: this entity (a player template, no client) counts as a player in game for <see cref="Get"/>,
        /// <see cref="UnlockedNodes"/> and changes. <see cref="EntityId.None"/> ends it.</summary>
        [System.Diagnostics.CodeAnalysis.Experimental("KILNX001")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void LabPlayer(EntityId entity)
        {
            var t = Require(nameof(LabPlayer));
            t->LabPlayer(entity.Value);
        }

        /// <summary>The game's own experience table: what <paramref name="level"/> needs for the next level, and the
        /// level cap. Tick thread.</summary>
        public static (uint Needed, int Cap) ExperienceForLevel(int level)
        {
            var t = Require(nameof(ExperienceForLevel));
            Services.RequireTickThread("Progression.Diagnostics.ExperienceForLevel");
            uint need = 0, cap = 0;
            Check(t->XpForLevel((uint)Math.Max(level, 0), &need, &cap), nameof(ExperienceForLevel));
            return (need, (int)cap);
        }

        /// <summary>The game's attribute ids of the progression attributes (sigdb players/attr.*).</summary>
        public const uint AttrLevel = 0x724bf568, AttrExperience = 0x8dcddaf4, AttrGranted = 0x1d7dd102, AttrNeeded = 0x04723f62;

        /// <summary>What <see cref="ProbeAttributes"/> found: the root id, the storage after the write, and the storage slot
        /// of level / experience / granted / needed (-1: not in this component).</summary>
        public readonly record struct AttrProbe(uint Root, uint[] Storage, int[] Slots);

        /// <summary>The game's attribute lookup and formula re-run on a scratch Experience (<paramref name="level"/> false,
        /// 3 values) or Level (true, 2 values) component laid out as the game initialises one: writes
        /// <paramref name="setValue"/> into attribute <paramref name="setId"/> (0: none) and re-runs the formulas. Tick
        /// thread.</summary>
        public static AttrProbe ProbeAttributes(bool level, uint[] storage, uint setId = 0, uint setValue = 0)
        {
            ArgumentNullException.ThrowIfNull(storage);
            var t = Require(nameof(ProbeAttributes));
            Services.RequireTickThread("Progression.Diagnostics.ProbeAttributes");
            NativeAttrProbe p = default;
            p.Size = (uint)sizeof(NativeAttrProbe);
            p.Component = level ? 1u : 0u;
            for (var i = 0; i < Math.Min(storage.Length, 4); i++) p.Storage[i] = storage[i];
            p.SetId = setId;
            p.SetValue = setValue;
            Check(t->AttrProbe(&p), nameof(ProbeAttributes));
            var n = level ? 2 : 3;
            var st = new uint[n];
            for (var i = 0; i < n; i++) st[i] = p.Storage[i];
            var slots = new int[4];
            for (var i = 0; i < 4; i++) slots[i] = p.Slots[i];
            return new AttrProbe(p.Root, st, slots);
        }

        /// <summary>One step of <see cref="Simulate"/>.</summary>
        [System.Diagnostics.CodeAnalysis.Experimental("KILNX001")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public enum SimOp : uint { Unlock = 1, Lock = 2, Respec = 3, Check = 4 }

        /// <summary>What a simulated step gave (the game's own functions on a scratch node list).</summary>
        [System.Diagnostics.CodeAnalysis.Experimental("KILNX001")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public readonly record struct SimResult(SimOp Op, uint Node, bool Ok, bool Reachable, bool Connected, int Level, int Spent, int Nodes);

        /// <summary>Runs the game's skill functions (link check, unlock/unlearn, tree rebuild, connectivity, points spent)
        /// on a scratch UnlockedSkillNodes that starts empty: no player needed. Tick thread.</summary>
        [System.Diagnostics.CodeAnalysis.Experimental("KILNX001")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static IReadOnlyList<SimResult> Simulate(IReadOnlyList<(SimOp Op, uint Node)> steps)
        {
            ArgumentNullException.ThrowIfNull(steps);
            var t = Require(nameof(Simulate));
            Services.RequireTickThread("Progression.Diagnostics.Simulate");
            var s = new NativeSimStep[steps.Count];
            for (var i = 0; i < s.Length; i++) s[i] = new NativeSimStep { Op = (uint)steps[i].Op, Node = steps[i].Node };
            var r = new NativeSimResult[steps.Count];
            fixed (NativeSimStep* ps = s)
            fixed (NativeSimResult* pr = r)
                Check(t->Simulate(ps, (uint)s.Length, pr), nameof(Simulate));
            return [.. r.Select(x => new SimResult((SimOp)x.Op, x.Node, x.Ok != 0, x.Reachable != 0, x.Connected != 0, x.Level, (int)x.Spent, (int)x.Nodes))];
        }
    }

    // ---- helpers ----
    internal static ProgressionSnapshot Convert(NativeProgression* p) => new(
        new EntityId(p->Entity), (int)p->Level, (int)p->LevelCap, p->Experience, p->ExperienceNeeded, p->ExperiencePending,
        p->PointsAvailable, (int)p->PointsTotal, (int)p->PointsSpent, (int)p->PointsFromLevel, (int)p->PointsFromKnowledge,
        (int)p->PointsPerLevel, (int)p->Nodes, (int)p->PointsGivable, (p->Flags & HasLevel) != 0, (p->Flags & HasSkills) != 0,
        (p->Flags & HasPoints) != 0, (p->Flags & HasKnowledge) != 0, (p->Flags & AtCap) != 0, (p->Flags & OverBudget) != 0,
        (p->Flags & Snapshot) != 0, p->Step);

    private static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }

    private static string Code(int rc) => rc switch
    {
        Ok => "ok",
        InvalidArgument => "invalid",
        NotFound => "not_found",
        Unavailable => "unavailable",
        WrongThread => "wrong_thread",
        BufferTooSmall => "busy",
        _ => "internal",
    };

    private static void Check(int rc, string api)
    {
        switch (rc)
        {
            case Ok: return;
            case Unavailable: throw new NotSupportedException($"Progression.{api}: unavailable on this build");
            case WrongThread: throw new InvalidOperationException($"Progression.{api}: call it on the tick thread");
            default: throw new InvalidOperationException($"Progression.{api}: kiln.progression returned {rc}");
        }
    }
}
