// SPDX-License-Identifier: MIT
// Kiln.Saves reads two other services through their native v1 tables (append-only ABI) instead of their managed
// assemblies, so it builds and runs independently of them: kiln.spawn (entity template + transform, find by template,
// template flags) for EntityData, kiln.players (Steam id of a player entity). Layouts: native/core/abi/services/
// kiln_spawn.h and kiln_players.h (sizes checked against the headers with clang).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class SpawnLink
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Snapshot  // KilnEntitySnapshot, 96 bytes
    {
        public uint Size, HasTemplate;
        public fixed byte Template[16];
        public Vec3 Position;
        public fixed float Rotation[4];
        public float Yaw;
        public uint Tracked;
        public ulong Serial;
        public long SpawnTime;
        public uint HasSpawnTime;
        public uint Parent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TemplateInfo  // KilnTemplateInfo, 224 bytes
    {
        public uint Size, Kind;
        public fixed byte Guid[16];
        public fixed byte Name[128];
        public fixed byte Display[64];
        public uint Source, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table  // the v1 prefix of KilnSpawnApi
    {
        public ServiceHeader Header;
        public nint Spawn, SpawnItem, Destroy;
        public delegate* unmanaged[Cdecl]<uint, Snapshot*, int> EntityInfo;
        public delegate* unmanaged[Cdecl]<byte*, NativeStr, Vec3, double, uint*, nuint, nuint*, int> Find;
        public nint TemplateFind;
        public delegate* unmanaged[Cdecl]<byte*, TemplateInfo*, int> TemplateGet;
        public nint TemplateCount, TemplateAt, TrackedCount, TrackedAt, Cleanup, Refresh, WorldKey;
        public delegate* unmanaged[Cdecl]<uint> WorldState;
    }

    private const uint KindCreature = 1, KindNpc = 4, FlagKnown = 1, FlagDoNotSave = 2, FlagTransform = 8;
    private static Table* _t;
    private static bool _looked;
    private static readonly Dictionary<Guid, (uint kind, bool neverSaved, string name, bool canPersist, bool known)> Templates = new();

    private static Table* T
    {
        get
        {
            if (!_looked)
            {
                _t = Services.Get<Table>("kiln.spawn", 1);
                _looked = true;
            }
            return _t;
        }
    }

    public static bool Available => T != null;

    /// <summary>Instance data needs all three identity operations, not a best-effort empty answer.</summary>
    internal static void RequireIdentity()
    {
        var t = T;
        if (t == null || t->EntityInfo == null || t->Find == null || t->TemplateGet == null)
            throw new KilnUnavailableException("kiln.spawn", "item holders need entity inspection, template metadata and holder search");
    }
    public static bool WorldRunning => T != null && T->WorldState() == 2;

    internal sealed record Ent(EntityId Entity, Guid? Template, string? Name, WorldPosition Position, float Yaw);

    /// <summary>Tick thread. Null when the entity does not exist; throws when kiln.spawn could not tell (wrong thread,
    /// world not there), so callers never mistake "unknown" for "destroyed".</summary>
    public static Ent? Inspect(EntityId e)
    {
        var t = T;
        if (t == null || e.IsNone) return null;
        var s = new Snapshot { Size = (uint)sizeof(Snapshot) };
        var rc = t->EntityInfo(e.Value, &s);
        if (rc == 2) return null;  // KILN_ERR_NOT_FOUND
        if (rc != 0) throw new InvalidOperationException($"kiln.spawn could not inspect entity {e} (rc {rc})");
        Guid? g = s.HasTemplate != 0 ? new Guid(new ReadOnlySpan<byte>(s.Template, 16)) : null;
        return new Ent(e, g, g is { } x ? Template(x).name : null, new WorldPosition(s.Position.X, s.Position.Y, s.Position.Z), s.Yaw);
    }

    /// <summary>Tick thread. Entities of the template within radius of the point, nearest first.</summary>
    public static IReadOnlyList<EntityId> Find(Guid template, WorldPosition near, double radius, int max, bool required = false)
    {
        if (required) RequireIdentity();
        var t = T;
        if (t == null) return [];
        var buf = new uint[max];
        nuint count = 0;
        var g = template.ToByteArray();
        int rc;
        fixed (byte* gp = g)
        fixed (uint* o = buf)
            rc = t->Find(gp, default, new Vec3 { X = near.X, Y = near.Y, Z = near.Z }, radius, o, (nuint)max, &count);
        if (rc != 0)
        {
            if (required) throw new KilnUnavailableException("kiln.spawn", $"holder search could not answer (rc {rc})");
            return [];
        }
        var n = (int)Math.Min((ulong)count, (ulong)max);
        var list = new EntityId[n];
        for (var i = 0; i < n; i++) list[i] = new EntityId(buf[i]);
        return list;
    }

    /// <summary>Kind, DoNotSave, name, positional persistence, and whether the template flags are known. Any thread.</summary>
    public static (uint kind, bool neverSaved, string name, bool canPersist, bool known) Template(Guid g, bool required = false)
    {
        if (required) RequireIdentity();
        lock (Templates)
            if (Templates.TryGetValue(g, out var hit)) return hit;
        var t = T;
        if (t == null) return (0, false, "", false, false);
        var info = new TemplateInfo { Size = (uint)sizeof(TemplateInfo) };
        var b = g.ToByteArray();
        int rc;
        fixed (byte* p = b) rc = t->TemplateGet(p, &info);
        if (rc != 0)
        {
            if (required) throw new KilnUnavailableException("kiln.spawn", $"holder template metadata could not answer (rc {rc})");
            return (0, false, "", false, false);
        }
        var name = Marshal.PtrToStringUTF8((nint)info.Name) ?? "";
        // Positional identity is safe only for a known, saved world template. Item entities have ItemState but no
        // CurrentTransform: Inspect reports the same default position for every sword, which is not an identity.
        var known = (info.Flags & FlagKnown) != 0;
        var r = (info.Kind, known && (info.Flags & FlagDoNotSave) != 0, name,
            known && (info.Flags & (FlagDoNotSave | FlagTransform)) == FlagTransform, known);
        // Before the engine's template registry is ready, flags are unknown. Do not cache that temporary answer.
        if (known) lock (Templates) Templates[g] = r;
        return r;
    }

    public static bool IsMobile(Guid g) => Template(g).kind is KindCreature or KindNpc;
}

internal static unsafe class PlayersLink
{
    [StructLayout(LayoutKind.Explicit, Size = 224)]
    private struct Player  // KilnPlayer: only the fields Kiln.Saves reads
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(16)] public uint Flags;
        [FieldOffset(48)] public ulong SteamId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table  // the v1 prefix of KilnPlayersApi
    {
        public ServiceHeader Header;
        public nint Status, List;
        public delegate* unmanaged[Cdecl]<uint, Player*, int> Get;
    }

    private static Table* _t;
    private static bool _looked;

    private static Table* T
    {
        get
        {
            if (!_looked)
            {
                _t = Services.Get<Table>("kiln.players", 1);
                _looked = true;
            }
            return _t;
        }
    }

    /// <summary>SteamID64 of the player who has this entity (0: none / unknown / kiln.players unavailable).</summary>
    public static ulong SteamId(EntityId e)
    {
        var t = T;
        if (t == null) return 0;
        var p = new Player { Size = 224 };
        return t->Get(e.Value, &p) == 0 ? p.SteamId : 0;
    }
}
