// SPDX-License-Identifier: MIT
// Readers for the game data world progress is built from: the knowledge query db (named queries and their actions), the
// world scene's entity chunks (placements, their knowledge triggers and queries, loot container ids) and templates
// (spawners, boss health bars, map markers, kill triggers). Pure functions over a resource's bytes, so they run on any
// thread and in unit tests. Layouts are the game's reflection (docs/re/world-status.md); every read is bounds-checked and
// a malformed resource throws InvalidDataException.
using System.Buffers.Binary;
using System.Text;

namespace Kiln;

/// <summary>FNV-1a-32, the hash the game uses for type names, component names and GUID-based ids.</summary>
internal static class Fnv
{
    public static uint Hash(ReadOnlySpan<byte> data)
    {
        var h = 0x811c9dc5u;
        foreach (var b in data) h = (h ^ b) * 0x01000193u;
        return h;
    }

    public static uint Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    /// <summary>A component's hash: FNV-1a-32 of "keen::ecs::&lt;name&gt;".</summary>
    public static uint Component(string name) => Hash("keen::ecs::" + name);
}

/// <summary>One knowledge operation of a trigger (the game's KnowledgeTriggerOperation).</summary>
internal readonly record struct TriggerOp(byte Context, uint Id, byte Type, uint Value)
{
    public const byte Invalid = 0, Interact = 1, Kill = 2, Toggle = 3, Range = 4, MiningNode = 5;
    public bool IsBool => Type == 0;
}

/// <summary>A requirement as stored (the game's KnowledgeQueryData).</summary>
internal readonly record struct QueryData(uint Id, uint CompareValue, byte Compare, byte Type, bool PlayerOnly)
{
    public KnowledgeQuery ToQuery() => new(Id, (KnowledgeQueryType)Type, (KnowledgeCompare)Compare, CompareValue, PlayerOnly);
}

/// <summary>An action of a named query (KnowledgeQueryActionInfo): its own name labels the knowledge it reads.</summary>
internal sealed record QueryAction(byte Type, string Name, QueryData Query)
{
    public const byte Knowledge = 0;
    public bool IsSimple => Type == Knowledge && Query.Type is 1 or 2 or 3;
}

internal sealed record QueryRecord(uint Id, string Name, byte Type, bool Player, IReadOnlyList<QueryAction> Actions);

/// <summary>One placed entity of the world scene, with the spawn components world progress reads.</summary>
internal sealed record ScenePlacement(int Chunk, int Index, Guid Template, WorldPosition Position, IReadOnlyList<TriggerOp> Triggers,
    QueryData? Query, uint LootId);

/// <summary>What a template says about world progress.</summary>
internal sealed record TemplateData(Guid Guid, string Name, IReadOnlyCollection<uint> Components, IReadOnlyList<Guid> Spawns,
    uint BossBarTag, uint MarkerTag, TriggerOp? KillOp, bool KillUsesScene)
{
    public bool Has(string component) => Components.Contains(Fnv.Component(component));
    /// <summary>A boss the game gives a health bar (its name: <see cref="BossBarTag"/>). Templates with only the Boss tag
    /// are elites without a bar.</summary>
    public bool IsBoss => Components.Contains(GameLayout.BossHealthBar);
    public bool IsChest => Has("RandomLootContainer") && Has("InventoryEmptyState");
}

/// <summary>Bounds-checked little-endian reads of a blob (a resource's root struct at 0, its data after it).</summary>
internal readonly ref struct Blob
{
    private readonly ReadOnlySpan<byte> _b;

    public Blob(ReadOnlySpan<byte> bytes) => _b = bytes;

    public int Length => _b.Length;

    private void Need(int at, int size)
    {
        if (at < 0 || size < 0 || at > _b.Length - size)
            throw new InvalidDataException($"read of {size} byte(s) at 0x{at:x} outside the {_b.Length}-byte resource");
    }

    public byte U8(int at)
    {
        Need(at, 1);
        return _b[at];
    }

    public ushort U16(int at)
    {
        Need(at, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(_b[at..]);
    }

    public uint U32(int at)
    {
        Need(at, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(_b[at..]);
    }

    public int I32(int at)
    {
        Need(at, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(_b[at..]);
    }

    public Guid Guid(int at)
    {
        Need(at, 16);
        return new Guid(_b.Slice(at, 16));
    }

    public uint GuidHash(int at)
    {
        Need(at, 16);
        var g = _b.Slice(at, 16);
        foreach (var x in g)
            if (x != 0) return Fnv.Hash(g);
        return 0;
    }

    /// <summary>A BlobArray {i32 offset from the field, u32 count}: the first element's offset and the count.</summary>
    public (int At, int Count) Array(int at, int stride, int max = 1 << 22)
    {
        var rel = I32(at);
        var n = U32(at + 4);
        if (n == 0) return (0, 0);
        if (n > (uint)max) throw new InvalidDataException($"array at 0x{at:x} has {n} elements");
        var start = (long)at + rel;
        if (start < 0 || start + (long)n * stride > _b.Length)
            throw new InvalidDataException($"array at 0x{at:x} ({n} x {stride} B) is outside the resource");
        return ((int)start, (int)n);
    }

    /// <summary>A BlobString {i32 offset from the field, u32 length}.</summary>
    public string String(int at)
    {
        var rel = I32(at);
        var n = U32(at + 4);
        if (n == 0) return "";
        var start = (long)at + rel;
        if (n > 4096 || start < 0 || start + n > _b.Length) throw new InvalidDataException($"string at 0x{at:x} is outside the resource");
        return Encoding.UTF8.GetString(_b.Slice((int)start, (int)n)).TrimEnd('\0');
    }

    /// <summary>A BlobVariant {u32 type hash, i32 offset from +4, u32 size}: the hash, the data offset and the size.</summary>
    public (uint Hash, int At, int Size) Variant(int at)
    {
        var h = U32(at);
        var rel = I32(at + 4);
        var size = U32(at + 8);
        var start = (long)at + 4 + rel;
        if (start < 0 || size > (uint)_b.Length || start + size > _b.Length)
            throw new InvalidDataException($"variant at 0x{at:x} is outside the resource");
        return (h, (int)start, (int)size);
    }
}

/// <summary>Layouts on game data 1287369 (reflection; docs/re/world-status.md). A component of another size is skipped.</summary>
internal static class GameLayout
{
    public const int QueryEntry = 48, ActionEntry = 36;
    public const int ChunkEntity = 20, ChunkEntities = 0x18, ChunkTemplates = 0x00, ChunkSpawnData = 0x34;
    public const int SpawnEntries = 0x00, SpawnComponents = 0x08, SpawnIndices = 0x50;
    public const int TemplateName = 0x00, TemplateComponents = 0x10;
    public const int SceneKnowledgeTriggerSize = 132, KnowledgeQuerySize = 124, SceneLootNodeIdSize = 4;
    public const int TriggerSpawnerSize = 56, RandomSpawnerSize = 168, SpawnerEntry = 36, BossHealthBarSize = 20, MapMarkerSize = 40;
    public const int KnowledgeTriggerOnKillSize = 104;

    public static readonly uint SceneKnowledgeTrigger = Fnv.Component("SceneKnowledgeTrigger");
    public static readonly uint KnowledgeQuery = Fnv.Component("KnowledgeQuery");
    public static readonly uint SceneLootNodeId = Fnv.Component("SceneLootNodeId");
    public static readonly uint TriggerSpawner = Fnv.Component("TriggerSpawner");
    public static readonly uint RandomSpawner = Fnv.Component("RandomSpawner");
    public static readonly uint BossHealthBar = Fnv.Component("BossHealthBar");
    public static readonly uint MapMarker = Fnv.Component("MapMarker");
    public static readonly uint KnowledgeTriggerOnKill = Fnv.Component("KnowledgeTriggerOnKill");

    public static readonly uint QueryDbType = Fnv.Hash("keen::GameKnowledgeQueryResourceDb");
    public static readonly uint SceneChunkType = Fnv.Hash("keen::SceneEntityChunkResource");
    public static readonly uint TemplateType = Fnv.Hash("keen::ecs::TemplateResource");
}

internal static class GameDataReader
{
    internal static QueryData ReadQuery(in Blob b, int at) =>
        new(b.U32(at), b.U32(at + 4), b.U8(at + 8), b.U8(at + 9), b.U8(at + 10) != 0);

    internal static TriggerOp ReadOp(in Blob b, int at) => new(b.U8(at), b.U32(at + 4), b.U8(at + 8), b.U32(at + 12));

    /// <summary>GameKnowledgeQueryResourceDb: every named query with its actions.</summary>
    public static List<QueryRecord> ReadQueryDb(ReadOnlySpan<byte> bytes)
    {
        var b = new Blob(bytes);
        var (at, n) = b.Array(0x10, GameLayout.QueryEntry, 100_000);
        var list = new List<QueryRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var e = at + i * GameLayout.QueryEntry;
            var (aat, an) = b.Array(e + 0x18, GameLayout.ActionEntry, 4096);
            var actions = new QueryAction[an];
            for (var k = 0; k < an; k++)
            {
                var a = aat + k * GameLayout.ActionEntry;
                actions[k] = new QueryAction(b.U8(a), b.String(a + 4), ReadQuery(b, a + 0xc));
            }
            list.Add(new QueryRecord(b.U32(e + 8), b.String(e), b.U8(e + 0x10), b.U8(e + 0x11) != 0, actions));
        }
        return list;
    }

    /// <summary>A SceneEntityChunkResource: its template GUIDs and the placements that have a knowledge trigger or query (or
    /// every placement when <paramref name="all"/>, those whose template <paramref name="want"/> accepts, and with
    /// <paramref name="withLoot"/> those with a loot container id).</summary>
    public static (Guid[] Templates, List<ScenePlacement> Placements) ReadChunk(ReadOnlySpan<byte> bytes, int chunk, bool all = false,
        Func<Guid, bool>? want = null, bool withLoot = false)
    {
        var b = new Blob(bytes);
        var (tat, tn) = b.Array(GameLayout.ChunkTemplates, 16, 1 << 16);
        var templates = new Guid[tn];
        for (var i = 0; i < tn; i++) templates[i] = b.Guid(tat + 16 * i);
        var (eat, en) = b.Array(GameLayout.ChunkEntities, GameLayout.ChunkEntity);
        var sd = GameLayout.ChunkSpawnData;
        var (nat, nn) = b.Array(sd + GameLayout.SpawnEntries, 4);
        var (cat, cn) = b.Array(sd + GameLayout.SpawnComponents, 12);
        var (iat, inn) = b.Array(sd + GameLayout.SpawnIndices, 1);
        var list = new List<ScenePlacement>();
        for (var i = 0; i < en; i++)
        {
            var e = eat + i * GameLayout.ChunkEntity;
            int ti = b.U16(e);
            var tmpl = ti < tn ? templates[ti] : System.Guid.Empty;
            var wanted = all || (want is not null && want(tmpl));
            List<TriggerOp>? ops = null;
            QueryData? query = null;
            uint loot = 0;
            if (i < nn)
            {
                int start = b.U16(nat + 4 * i + 2);
                if (start < inn)
                {
                    int count = b.U8(iat + start);
                    if (count > 0 && start + 3 <= inn)
                    {
                        int first = b.U8(iat + start + 1) | (b.U8(iat + start + 2) << 8);
                        for (var k = first; k < first + count && k < cn; k++)
                        {
                            var (h, at, size) = b.Variant(cat + 12 * k);
                            if (h == GameLayout.SceneKnowledgeTrigger && size == GameLayout.SceneKnowledgeTriggerSize)
                            {
                                for (var s = 0; s < 8; s++)
                                {
                                    var op = ReadOp(b, at + 16 * s);
                                    if (op.Id != 0) (ops ??= []).Add(op);
                                }
                            }
                            else if (h == GameLayout.KnowledgeQuery && size == GameLayout.KnowledgeQuerySize)
                            {
                                var q = ReadQuery(b, at);
                                if (q.Id != 0) query = q;
                            }
                            else if (h == GameLayout.SceneLootNodeId && size == GameLayout.SceneLootNodeIdSize)
                            {
                                loot = b.U32(at);
                            }
                        }
                    }
                }
            }
            if (!wanted && ops is null && query is null && !(withLoot && loot != 0)) continue;
            var tr = e + 2;
            var x = (b.U16(tr + 2) << 16 | b.U16(tr)) / 65536.0;
            var y = (b.U16(tr + 6) << 16 | b.U16(tr + 4)) / 65536.0;
            var z = (b.U16(tr + 10) << 16 | b.U16(tr + 8)) / 65536.0;
            list.Add(new ScenePlacement(chunk, i, tmpl, new WorldPosition(x, y, z), ops is null ? [] : ops, query, loot));
        }
        return (templates, list);
    }

    /// <summary>An ecs::TemplateResource: name, component hashes and what world progress reads from it.</summary>
    public static TemplateData ReadTemplate(Guid guid, ReadOnlySpan<byte> bytes)
    {
        var b = new Blob(bytes);
        var name = b.String(GameLayout.TemplateName);
        var (cat, cn) = b.Array(GameLayout.TemplateComponents, 12, 4096);
        var comps = new HashSet<uint>();
        var spawns = new List<Guid>();
        uint bar = 0, marker = 0;
        TriggerOp? kill = null;
        var killScene = false;
        for (var i = 0; i < cn; i++)
        {
            var (h, at, size) = b.Variant(cat + 12 * i);
            comps.Add(h);
            if (h == GameLayout.TriggerSpawner && size == GameLayout.TriggerSpawnerSize)
            {
                var g = b.Guid(at);
                if (g != System.Guid.Empty) spawns.Add(g);
            }
            else if (h == GameLayout.RandomSpawner && size == GameLayout.RandomSpawnerSize)
            {
                var (eat, en) = b.Array(at, GameLayout.SpawnerEntry, 1024);
                for (var k = 0; k < en; k++)
                {
                    var (gat, gn) = b.Array(eat + k * GameLayout.SpawnerEntry, 16, 1024);
                    for (var j = 0; j < gn; j++)
                    {
                        var g = b.Guid(gat + 16 * j);
                        if (g != System.Guid.Empty) spawns.Add(g);
                    }
                }
            }
            else if (h == GameLayout.BossHealthBar && size == GameLayout.BossHealthBarSize)
            {
                bar = b.GuidHash(at);
            }
            else if (h == GameLayout.MapMarker && size == GameLayout.MapMarkerSize)
            {
                marker = b.GuidHash(at);
            }
            else if (h == GameLayout.KnowledgeTriggerOnKill && size == GameLayout.KnowledgeTriggerOnKillSize)
            {
                killScene = b.U8(at) != 0;
                var op = new TriggerOp(TriggerOp.Kill, b.U32(at + 0x5c), b.U8(at + 0x60), b.U32(at + 0x64));
                if (op.Id != 0) kill = op;
            }
        }
        return new TemplateData(guid, name, comps, spawns, bar, marker, kill, killScene);
    }
}
