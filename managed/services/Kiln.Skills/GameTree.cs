// SPDX-License-Identifier: MIT
// Kiln.Skills, part 1: nodes in the game's own skill tree (native/services/skills, kiln.skills v1). Usage:
// docs/kiln/services/skills.md.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static unsafe class SkillsNative
{
    public const string Name = "kiln.skills";
    public const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte TreeLoad, LoginGuard, Functions, Texts;
        public uint VanillaNodes, VanillaLinks, VanillaLevelMax, Nodes, Links, AddedNodes, AddedLinks, FreeNodes, FreeLevels;
        public uint Defined, Refused, Pending, Worlds, Builds;
        public ulong Stripped, StripSeq;
        public uint ShadowNodes;
        public byte Shadow, R0, R1, R2;
        public fixed byte Reason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Strip
    {
        public ulong Seq, Step;
        public uint Entity, Count;
        public fixed uint Ids[16];
        public fixed byte Levels[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SimStep
    {
        public uint Op, Node;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SimResult
    {
        public uint Op, Node;
        public byte Ok, Reachable, Rebuild, Connected, Levels, Level, Known, Reserved;
        public int Spent;
        public uint Nodes, Stripped;
        public int Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public uint Size, TableVersion;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ulong, NativeText, byte*, nuint, nuint*, int> Define;
        public delegate* unmanaged[Cdecl]<ulong, uint> UndefineOwner;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Definitions;
        public delegate* unmanaged[Cdecl]<uint, byte*, nuint, nuint*, int> Tree;
        public delegate* unmanaged[Cdecl]<SimStep*, uint, SimResult*, int> Simulate;
        public delegate* unmanaged[Cdecl]<ulong, Strip*, uint, uint*, int> Strips;
        public delegate* unmanaged[Cdecl]<uint, void*, nuint, nuint*, byte*, nuint, int> Resource;
        public delegate* unmanaged[Cdecl]<uint> ShadowOn;
        public delegate* unmanaged[Cdecl]<uint*, uint, uint*, int> ShadowNodes;
        public delegate* unmanaged[Cdecl]<uint, uint, ShadowEntry*, uint, uint*, int> ShadowGet;
        public delegate* unmanaged[Cdecl]<uint, uint, ShadowEntry*, uint, int> ShadowSet;
        public delegate* unmanaged[Cdecl]<ulong, ShadowChange*, uint, uint*, int> ShadowChanges;
        public delegate* unmanaged[Cdecl]<uint, int*, int> Spent;
        public delegate* unmanaged[Cdecl]<uint, uint, int*, int*, int> IsUnlocked;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, int> GameRequest;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowEntry
    {
        public uint Node;
        public byte Level, R0, R1, R2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowChange
    {
        public ulong Seq;
        public uint World, Entity, Node;
        public byte OldLevel, NewLevel, Reason, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeText
    {
        public byte* Ptr;
        public nuint Len;
    }

    private static Table* _t;
    private static bool _looked;

    /// <summary>Tests: a fake kiln.skills table (null: none, as offline). Kiln never calls this.</summary>
    internal static void UseForTests(Table* table)
    {
        _t = table;
        _looked = true;
    }

    public static Table* T
    {
        get
        {
            if (_t == null && !_looked)
            {
                _looked = true;
                var p = (Table*)SkillsInternals.Service(Name, Version);
                if (p != null && p->Size >= (uint)sizeof(Table)) _t = p;
            }
            return _t;
        }
    }

    public const int Ok = 0, BufferTooSmall = 5, WrongThread = 4, Unavailable = 3, NotFound = 2;

    /// <summary>Calls a "fill a text buffer" function until the buffer is big enough.</summary>
    public static string Text(Func<IntPtr, nuint, IntPtr, int> call, out int rc)
    {
        var cap = 64 * 1024;
        for (var attempt = 0; attempt < 6; ++attempt)
        {
            var buf = new byte[cap];
            nuint len = 0;
            fixed (byte* b = buf)
                rc = call((IntPtr)b, (nuint)cap, (IntPtr)(&len));
            if (rc == BufferTooSmall || (int)len >= cap) { cap = Math.Max(cap * 2, (int)len + 1); continue; }
            return Encoding.UTF8.GetString(buf, 0, (int)Math.Min(len, (nuint)cap));
        }
        rc = BufferTooSmall;
        return "";
    }

    public static string Describe(int rc) => rc switch
    {
        0 => "ok",
        1 => "invalid argument",
        2 => "not found",
        3 => "unavailable on this build",
        4 => "wrong thread (tick thread only)",
        5 => "buffer too small",
        _ => $"error {rc}",
    };
}

/// <summary>What kiln.skills does with the game's own tree right now.</summary>
public sealed record GameTreeStatus(bool TreeExpansion, bool LoginGuard, bool GameFunctions, bool NodeTexts, int GameNodes,
    int GameLinks, int GameLevelMax, int Nodes, int Links, int AddedNodes, int AddedLinks, int FreeNodes, int FreeLevelSlots,
    int Defined, int Refused, int Pending, int WorldsServed, int Builds, ulong Refunded, string Reason, int ShadowNodes = 0,
    int ShadowLevel = 0)
{
    public override string ToString() =>
        $"game tree: {Nodes} nodes ({GameNodes} + {AddedNodes} Kiln, {ShadowNodes} shadow), {Links} links, {FreeNodes} native node slots and {FreeLevelSlots} " +
        $"level slots free; {Defined} defined, {Refused} refused, {Pending} waiting for the next world; expansion " +
        $"{(TreeExpansion ? "on" : "OFF")}, login guard {(LoginGuard ? "on" : "OFF")}, shadow table {(ShadowLevel == 2 ? "on (+screen)" : ShadowLevel == 1 ? "on" : "OFF")}{(Reason.Length > 0 ? $" ({Reason})" : "")}";
}

/// <summary>A node for the game's own skill tree (drawn by the game's skill-tree screen, unlocked with the game's skill
/// points, effects run by the game). Ids are FNV-1a of <see cref="Guid"/>, so every process with the same definition
/// agrees; never change a released GUID. Room: the game keeps 256 nodes (13 more than its own) and 100 level slots (9
/// more): for more, use a Kiln tree (<see cref="TalentTree"/>).</summary>
public sealed class GameNodeDefinition
{
    public required Guid Guid { get; init; }
    /// <summary>A debug name (also how other Kiln nodes refer to this one).</summary>
    public required string Name { get; init; }
    /// <summary>"PassiveSkill" (default: the like node's), "Attribute" or "ActiveSkill".</summary>
    public string? Type { get; init; }
    /// <summary>"Warrior", "Mage", "Ranger" or "Neutral" (default: the like node's).</summary>
    public string? Class { get; init; }
    /// <summary>Skill points (per level for nodes with levels).</summary>
    public int Cost { get; init; } = 1;
    /// <summary>0 = one unlock; 1..7 = levels (default: the like node's).</summary>
    public int? MaxLevel { get; init; }
    /// <summary>Absolute position in the game's tree units (the game's tree spans about 1700..6500); null = near
    /// <see cref="Near"/> (default: the first <see cref="From"/> node) plus <see cref="Offset"/>.</summary>
    public (float X, float Y)? Position { get; init; }
    public string? Near { get; init; }
    public (float X, float Y)? Offset { get; init; }
    /// <summary>Nodes that lead to this one ("0x&lt;id&gt;" of a game node, a node GUID, or a Kiln node's
    /// <see cref="Name"/>). The game unlocks along links in both directions.</summary>
    public IReadOnlyList<string> From { get; init; } = [];
    public IReadOnlyList<string> To { get; init; } = [];
    /// <summary>A node whose effect (program, config values, tooltip values), icon and texts to copy.</summary>
    public string? Like { get; init; }
    /// <summary>An ImpactProgram to run instead of the like node's (tools/impact, a Kiln program).</summary>
    public Guid? Program { get; init; }
    /// <summary>Number configs of the like node to change: config id (0x…) -> value. Tooltip values follow.</summary>
    public IReadOnlyDictionary<uint, double>? Configs { get; init; }
    /// <summary>Scale factors of the like node's scaled configs.</summary>
    public IReadOnlyDictionary<uint, double>? Scales { get; init; }
    /// <summary>A UiTexture GUID (e.g. an icon kiln.assets serves, from assets/assets.json) or null.</summary>
    public Guid? Icon { get; init; }
    /// <summary>A PNG for the icon (absolute, or relative to the kiln folder); kiln.assets serves it on clients.</summary>
    public string? IconFile { get; init; }
    /// <summary>language ("en", "de", "*") -> (name, description, perLevel).</summary>
    public IReadOnlyDictionary<string, (string Name, string Description, string? PerLevel)>? Texts { get; init; }
    /// <summary>The Ability_* item the node unlocks (its activeSkillUnlockRef).</summary>
    public Guid? UnlocksAbility { get; init; }

    /// <summary>This node's id in the game (FNV-1a of the GUID's bytes).</summary>
    public uint Id => Fnv(Guid.ToByteArray());

    internal static uint Fnv(ReadOnlySpan<byte> b)
    {
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x1000193u;
        return h;
    }

    internal JsonObject ToJson()
    {
        var j = new JsonObject { ["guid"] = Guid.ToString(), ["name"] = Name, ["cost"] = Cost };
        if (Type is not null) j["type"] = Type;
        if (Class is not null) j["class"] = Class;
        if (MaxLevel is { } ml) j["maxLevel"] = ml;
        if (Position is { } p) j["position"] = new JsonArray(p.X, p.Y);
        if (Near is not null) j["near"] = Near;
        if (Offset is { } o) j["offset"] = new JsonArray(o.X, o.Y);
        if (From.Count > 0) j["from"] = new JsonArray(From.Select(s => (JsonNode)s).ToArray());
        if (To.Count > 0) j["to"] = new JsonArray(To.Select(s => (JsonNode)s).ToArray());
        if (Like is not null) j["like"] = Like;
        if (Program is { } pg) j["program"] = pg.ToString();
        if (Configs is { Count: > 0 }) j["configs"] = new JsonObject(Configs.Select(kv => KeyValuePair.Create($"0x{kv.Key:x8}", (JsonNode?)kv.Value)));
        if (Scales is { Count: > 0 }) j["scales"] = new JsonObject(Scales.Select(kv => KeyValuePair.Create($"0x{kv.Key:x8}", (JsonNode?)kv.Value)));
        if (Icon is { } ic) j["icon"] = ic.ToString();
        if (IconFile is not null) j["iconFile"] = IconFile;
        if (Texts is { Count: > 0 })
        {
            var t = new JsonObject();
            foreach (var (lang, (name, desc, per)) in Texts)
            {
                var e = new JsonObject { ["name"] = name, ["description"] = desc };
                if (per is not null) e["perLevel"] = per;
                t[lang] = e;
            }
            j["texts"] = t;
        }
        if (UnlocksAbility is { } ab) j["unlocksAbility"] = ab.ToString();
        return j;
    }
}

/// <summary>A definition as kiln.skills holds it.</summary>
public sealed record GameNodeInfo(Guid Guid, uint Id, string Name, string Owner, string Source, string State, int Index,
    int LevelIndex, string? Error);

/// <summary>One step of <see cref="GameTree.Simulate"/>.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public enum GameTreeSimOp : uint { Unlock = 1, UnlockForce = 2, Lock = 3, Check = 4, AddRaw = 5, Strip = 6 }

/// <summary>The game's own skill functions' answer after one simulated step.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed record GameTreeSimResult(GameTreeSimOp Op, uint Node, bool Ok, bool Reachable, bool Rebuild, bool Connected,
    bool LevelsValid, int Level, bool Known, int Spent, int UnlockedNodes, int Stripped, int Index)
{
    public override string ToString() =>
        $"{Op} 0x{Node:x8}: ok {Ok}, known {Known} (index {Index}), reachable before {Reachable}, rebuild {Rebuild}, " +
        $"connected {Connected}, levels valid {LevelsValid}, level {Level}, spent {Spent}, nodes {UnlockedNodes}" +
        (Op == GameTreeSimOp.Strip ? $", stripped {Stripped}" : "");
}

/// <summary>A player joined with game-tree nodes the tree does not know (a mod that added them is gone): kiln.skills
/// removed them before the game's own check (which would reset the whole tree). Their points are free again; the
/// rest of the tree stays. Posted on the tick thread (Pre); observe only.</summary>
public sealed class GameNodesRefundedEvent : KilnEvent
{
    internal GameNodesRefundedEvent(EntityId player, IReadOnlyList<(uint Id, int Level)> nodes, ulong step)
    {
        Player = player;
        Nodes = nodes;
        Step = step;
    }

    /// <summary>The player (None when Kiln could not tell which player it was).</summary>
    public EntityId Player { get; }
    /// <summary>The removed node ids and their levels.</summary>
    public IReadOnlyList<(uint Id, int Level)> Nodes { get; }
    public ulong Step { get; }
    public override string ToString() => $"{Nodes.Count} unknown game-tree node(s) of entity {Player} refunded";
}

/// <summary>Nodes in the game's own skill tree. Definitions take effect when a world next reads its tree: on a client,
/// the next world it enters (define them in OnLoad: mods load in the main menu, before any world); on a dedicated
/// server the world reads its tree before managed mods load, so put the nodes into the mod folder's
/// <c>skills/*.json</c> (<c>"gameTree": {"nodes": [...]}</c>, read natively at boot) — a code definition is cached and in
/// from the next start, unless the mod is built with Kiln.Sdk, whose build bakes what the code defines into
/// <c>skills/&lt;mod&gt;.generated.json</c> (docs/kiln/modding/baked.md).</summary>
public static unsafe partial class GameTree
{
    public static bool Available => SkillsNative.T != null || Baking;

    private static SkillsNative.Table* Need() =>
        SkillsNative.T is var t && t != null ? t : throw new NotSupportedException("kiln.skills is unavailable");

    public static GameTreeStatus? Status
    {
        get
        {
            var t = SkillsNative.T;
            if (t == null) return null;
            SkillsNative.Status s;
            s.Size = (uint)sizeof(SkillsNative.Status);
            if (t->GetStatus(&s) != SkillsNative.Ok) return null;
            var reason = Encoding.UTF8.GetString(s.Reason, 160).TrimEnd('\0');
            return new GameTreeStatus(s.TreeLoad != 0, s.LoginGuard != 0, s.Functions != 0, s.Texts != 0, (int)s.VanillaNodes,
                (int)s.VanillaLinks, (int)s.VanillaLevelMax, (int)s.Nodes, (int)s.Links, (int)s.AddedNodes, (int)s.AddedLinks,
                (int)s.FreeNodes, (int)s.FreeLevels, (int)s.Defined, (int)s.Refused, (int)s.Pending, (int)s.Worlds,
                (int)s.Builds, s.Stripped, reason, (int)s.ShadowNodes, s.Shadow);
        }
    }

    /// <summary>Defines (or redefines) nodes as the calling mod's. Returns kiln.skills' report per node (state
    /// "defined" / "redefined" / "pending" / "error"). Any thread.</summary>
    public static IReadOnlyList<GameNodeInfo> Define(params GameNodeDefinition[] nodes)
    {
        var mod = SkillsInternals.CurrentMod;
        var doc = new JsonObject { ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)n.ToJson()).ToArray()) };
        if (mod is not null) doc["mod"] = mod.Info.Id;
        return DefineJson(doc.ToJsonString(), mod);
    }

    /// <summary>Defines nodes from JSON (one node object, an array, or {"nodes": [...]}), as in skills/*.json.</summary>
    public static IReadOnlyList<GameNodeInfo> DefineJson(string json) => DefineJson(json, SkillsInternals.CurrentMod);

    private static IReadOnlyList<GameNodeInfo> DefineJson(string json, ModContext? mod)
    {
        if (SkillsNative.T == null && Baking) return Record(json, mod);
        var t = Need();
        if (mod is not null && !json.Contains("\"mod\"", StringComparison.Ordinal) && JsonNode.Parse(json) is JsonObject o && o.ContainsKey("nodes"))
        {
            o["mod"] = mod.Info.Id;
            json = o.ToJsonString();
        }
        var kept = new List<GameNodeInfo>();
        json = Unbaked(json, mod, kept);
        if (kept.Count > 0 && NodesOf(json).Nodes.Count == 0) return kept;
        var owner = mod is null ? 0UL : OwnerOf(mod);
        var bytes = Encoding.UTF8.GetBytes(json);
        string report;
        int rc;
        fixed (byte* p = bytes)
        {
            var text = new SkillsNative.NativeText { Ptr = p, Len = (nuint)bytes.Length };
            report = SkillsNative.Text((b, c, l) => t->Define(owner, text, (byte*)b, c, (nuint*)l), out rc);
        }
        if (mod is not null && Watched.Add(mod)) SkillsInternals.OnRetiring(mod, () => { Watched.Remove(mod); t->UndefineOwner(OwnerOf(mod)); });
        var list = new List<GameNodeInfo>();
        if (JsonNode.Parse(report) is JsonObject r && r["nodes"] is JsonArray a)
            foreach (var n in a)
            {
                var err = n?["error"]?.GetValue<string>();
                var guid = Guid.TryParse(n?["guid"]?.GetValue<string>(), out var g) ? g : Guid.Empty;
                list.Add(new GameNodeInfo(guid, ParseHex(n?["id"]?.GetValue<string>()), n?["name"]?.GetValue<string>() ?? "",
                    mod?.Info.Id ?? "", "code", n?["state"]?.GetValue<string>() ?? "error", -1, -1, err));
                if (err is not null) SkillsInternals.Warn($"game-tree node {n?["name"]}: {err}");
            }
        list.AddRange(kept);
        return list;
    }

    private static readonly HashSet<ModContext> Watched = new();

    private static ulong OwnerOf(ModContext mod) => SkillsInternals.OwnerOf(mod);

    internal static uint ParseHex(string? s) =>
        s is not null && s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;

    /// <summary>Every definition (all mods, files and code) with its state and tree index.</summary>
    public static IReadOnlyList<GameNodeInfo> Definitions
    {
        get
        {
            var t = SkillsNative.T;
            if (t == null) return [];
            var s = SkillsNative.Text((b, c, l) => t->Definitions((byte*)b, c, (nuint*)l), out _);
            var list = new List<GameNodeInfo>();
            if (JsonNode.Parse(s) is JsonArray a)
                foreach (var n in a)
                    list.Add(new GameNodeInfo(Guid.TryParse(n?["guid"]?.GetValue<string>(), out var g) ? g : Guid.Empty,
                        ParseHex(n?["id"]?.GetValue<string>()), n?["name"]?.GetValue<string>() ?? "", n?["owner"]?.GetValue<string>() ?? "",
                        n?["source"]?.GetValue<string>() ?? "", n?["state"]?.GetValue<string>() ?? "",
                        n?["index"]?.GetValue<int>() ?? -1, n?["levelIndex"]?.GetValue<int>() ?? -1, n?["error"]?.GetValue<string>()));
            return list;
        }
    }

    /// <summary>The server world's tree as the game holds it (JSON: nodes by index through the game's own lookup, map
    /// counts). <paramref name="addedOnly"/>: Kiln's nodes only. Tick thread.</summary>
    public static string Dump(bool addedOnly = false)
    {
        Game.AssertTickThread();
        var t = Need();
        var s = SkillsNative.Text((b, c, l) => t->Tree(addedOnly ? 1u : 0u, (byte*)b, c, (nuint*)l), out var rc);
        if (rc != SkillsNative.Ok) throw new InvalidOperationException($"kiln.skills tree: {SkillsNative.Describe(rc)}");
        return s;
    }

    /// <summary>Runs steps with the game's own skill functions on a scratch node list (no player). Tick thread.</summary>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static IReadOnlyList<GameTreeSimResult> Simulate(params (GameTreeSimOp Op, uint Node)[] steps)
    {
        Game.AssertTickThread();
        var t = Need();
        var ins = steps.Select(s => new SkillsNative.SimStep { Op = (uint)s.Op, Node = s.Node }).ToArray();
        var outs = new SkillsNative.SimResult[ins.Length];
        int rc;
        fixed (SkillsNative.SimStep* i = ins)
        fixed (SkillsNative.SimResult* o = outs)
            rc = t->Simulate(i, (uint)ins.Length, o);
        if (rc != SkillsNative.Ok) throw new InvalidOperationException($"kiln.skills simulate: {SkillsNative.Describe(rc)}");
        return outs.Select(r => new GameTreeSimResult((GameTreeSimOp)r.Op, r.Node, r.Ok != 0, r.Reachable != 0, r.Rebuild != 0,
            r.Connected != 0, r.Levels != 0, r.Level, r.Known != 0, r.Spent, (int)r.Nodes, (int)r.Stripped, r.Index)).ToArray();
    }

    /// <summary>The resource kiln.skills serves (or would serve now): <paramref name="which"/> 0 = expanded, 1 = the
    /// game's own, 2 = the copy path without additions (round-trip test). <paramref name="check"/> = the round-trip
    /// report (every game node, link and sub-array compared with the game's).</summary>
    public static byte[] Resource(int which, out string check)
    {
        var t = Need();
        nuint len = 0;
        var rep = new byte[16384];
        int rc;
        fixed (byte* r = rep)
            rc = t->Resource((uint)which, null, 0, &len, r, (nuint)rep.Length);
        if (rc != SkillsNative.Ok) throw new InvalidOperationException($"kiln.skills resource: {SkillsNative.Describe(rc)}");
        var bytes = new byte[(int)len];
        fixed (byte* b = bytes)
        fixed (byte* r = rep)
            rc = t->Resource((uint)which, b, len, &len, r, (nuint)rep.Length);
        check = Encoding.UTF8.GetString(rep).TrimEnd('\0');
        return bytes;
    }

    private static ulong _stripSeq;

    /// <summary>Login-guard records since the last call (also posted as <see cref="GameNodesRefundedEvent"/>).</summary>
    internal static List<GameNodesRefundedEvent> TakeStrips()
    {
        var list = new List<GameNodesRefundedEvent>();
        var t = SkillsNative.T;
        if (t == null) return list;
        var buf = new SkillsNative.Strip[32];
        uint n;
        fixed (SkillsNative.Strip* b = buf) t->Strips(_stripSeq, b, (uint)buf.Length, &n);
        for (var i = 0; i < Math.Min(n, (uint)buf.Length); ++i)
        {
            ref var s = ref buf[i];
            var nodes = new List<(uint, int)>();
            for (var k = 0; k < Math.Min(16, (int)s.Count); ++k) nodes.Add((s.Ids[k], s.Levels[k] >> 5));
            list.Add(new GameNodesRefundedEvent(new EntityId(s.Entity), nodes, s.Step));
            _stripSeq = Math.Max(_stripSeq, s.Seq);
        }
        return list;
    }
}
