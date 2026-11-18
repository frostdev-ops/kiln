// SPDX-License-Identifier: MIT
// A fake kiln.world v4 table with the seven ECS-read members (kiln_world.h "ECS reads"), answering the way
// native/services/world/ecs_reads.cpp does: per-owner reload epochs, a ref that is stale after its id is recycled or its
// layout, session or epoch changed, an absent component confirmed against the ref, frame-quiet reads that are busy while a
// frame runs and unsupported off the server world, a short buffer that reports the size and copies nothing, and queries
// that are complete or nothing. Each member counts its calls, so a test can see that a read never reached the engine.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.Api.Tests;

internal static unsafe class FakeEcsReads
{
    internal sealed class Entity
    {
        public uint Generation = 1;
        public ulong Layout = 0x1000;
        public readonly Dictionary<uint, byte[]> Components = [];
        public Guid? Template;
        public WorldPosition? Position;
    }

    public const int OkRc = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;
    public const int ErrChanged = 100, ErrStale = 101, ErrUnreadable = 102, ErrBusy = 103, ErrUnsupported = 104;

    public static readonly Dictionary<(uint Kind, uint Id), Entity> Entities = [];
    public static readonly Dictionary<ulong, ulong> Epochs = [];
    public static readonly ulong[] Sessions = new ulong[3];
    public static bool Capable = true, ExactProfile = true, FrameRunning;
    public static int UnreadableNext, ChangedNext;   // the next N copies (queries) fail unreadable (changed)
    public static readonly Dictionary<string, int> Calls = [];
    public static readonly List<ulong> Owners = [];  // the owner of every entity call, in order

    private static readonly EcsReadsTable* Native = (EcsReadsTable*)NativeMemory.AllocZeroed((nuint)sizeof(EcsReadsTable));

    public static nint Table => (nint)Native;

    /// <summary>A fresh model: server session 1, client session 7, no prediction world, capable, exact profile.</summary>
    public static void Reset(uint tableSize = 0)
    {
        lock (Entities)
        {
            Entities.Clear();
            Epochs.Clear();
            Calls.Clear();
            Owners.Clear();
        }
        Sessions[0] = 1;
        Sessions[1] = 0;
        Sessions[2] = 7;
        Capable = ExactProfile = true;
        FrameRunning = false;
        UnreadableNext = ChangedNext = 0;
        *Native = default;
        Native->V3.V1.Header = new ServiceHeader { Size = tableSize == 0 ? (uint)sizeof(EcsReadsTable) : tableSize, Version = 4 };
        Native->V3.Capabilities = &Capabilities;
        Native->V3.WriteStatus = &WriteStatus;  // Kiln.World's first use reports world.tracked_writes through it
        Native->EntityOpen = &EntityOpen;
        Native->EntityValidate = &EntityValidate;
        Native->ContextSnapshot = &ContextSnapshot;
        Native->Query = &Query;
        Native->Snapshot = &Snapshot;
        Native->SnapshotMany = &SnapshotMany;
        Native->TemplateUuid = &TemplateUuid;
    }

    public static Entity Add(uint id, WorldKind kind = WorldKind.Server, params (string Name, byte[] Bytes)[] components)
    {
        var e = new Entity();
        foreach (var (name, bytes) in components) e.Components[World.ComponentHash(name)] = bytes;
        lock (Entities) Entities[((uint)kind, id)] = e;
        return e;
    }

    public static int CallsOf(string member)
    {
        lock (Entities) return Calls.GetValueOrDefault(member);
    }

    public static int EntityCalls
    {
        get
        {
            lock (Entities) return Calls.Where(c => c.Key != nameof(Capabilities) && c.Key != nameof(ContextSnapshot)).Sum(c => c.Value);
        }
    }

    private static void Count(string member, ulong owner = 0)
    {
        lock (Entities)
        {
            Calls[member] = Calls.GetValueOrDefault(member) + 1;
            if (owner != 0) Owners.Add(owner);
        }
    }

    private static int ResultOf(uint status) => status switch
    {
        EcsReadConstants.Ok => OkRc,
        EcsReadConstants.Absent => NotFound,
        EcsReadConstants.Stale => ErrStale,
        EcsReadConstants.Unreadable => ErrUnreadable,
        EcsReadConstants.Busy => ErrBusy,
        EcsReadConstants.Unsupported => ErrUnsupported,
        _ => Unavailable,
    };

    // the world of `kind` as the owner sees it: OK, or why not
    private static uint Context(uint kind) =>
        !ExactProfile || kind == (uint)WorldKind.Prediction || kind > 2 || Sessions[kind] == 0 ? EcsReadConstants.Unavailable : EcsReadConstants.Ok;

    private static uint Validate(ulong owner, EcsObjectRefNative* r, out Entity? e)
    {
        e = null;
        if (Context(r->WorldKind) is var c && c != EcsReadConstants.Ok) return c;
        if (r->Session != Sessions[r->WorldKind]) return EcsReadConstants.Stale;
        lock (Entities)
        {
            if (r->ReloadEpoch != Epochs.GetValueOrDefault(owner)) return EcsReadConstants.Stale;
            if (!Entities.TryGetValue((r->WorldKind, r->Entity), out e)) return EcsReadConstants.Absent;
        }
        if (e.Generation != r->Generation || e.Layout != r->LayoutStamp) return EcsReadConstants.Stale;
        return EcsReadConstants.Ok;
    }

    private static bool BadRef(EcsObjectRefNative* r) => r == null || r->Size != (uint)sizeof(EcsObjectRefNative) || r->Entity == 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Capabilities()
    {
        Count(nameof(Capabilities));
        return Capable ? EcsReadConstants.CapEcsRead | 0x7 : 0x7;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteStatus(WriteStatusNative* status) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityOpen(ulong owner, ulong epoch, uint kind, ulong expectedSession, uint entity, EcsObjectRefNative* o)
    {
        Count(nameof(EntityOpen), owner);
        if (owner == 0 || o == null || entity == 0 || kind > 2) return InvalidArgument;
        lock (Entities)
        {
            var current = Epochs.GetValueOrDefault(owner);
            if (epoch > current) Epochs[owner] = epoch;
            else if (epoch < current) return ErrStale;
        }
        if (Context(kind) is var c && c != EcsReadConstants.Ok) return ResultOf(c);
        if (expectedSession != 0 && expectedSession != Sessions[kind]) return ErrStale;
        Entity? e;
        lock (Entities)
            if (!Entities.TryGetValue((kind, entity), out e)) return NotFound;
        *o = new EcsObjectRefNative
        {
            Size = (uint)sizeof(EcsObjectRefNative), Entity = entity, Generation = e.Generation, WorldKind = kind,
            Session = Sessions[kind], LayoutStamp = e.Layout, ReloadEpoch = epoch,
        };
        return OkRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityValidate(ulong owner, EcsObjectRefNative* r, EcsObjectInfoNative* info)
    {
        Count(nameof(EntityValidate), owner);
        if (owner == 0 || BadRef(r) || info == null || info->Size < (uint)sizeof(EcsObjectInfoNative)) return InvalidArgument;
        var s = Validate(owner, r, out var e);
        info->Status = s;
        info->Entity = r->Entity;
        info->Generation = e?.Generation ?? 0;
        info->WorldKind = r->WorldKind;
        info->Flags = e is not null && s == EcsReadConstants.Ok ? EcsReadConstants.ObjectAlive : 0;  // fill_info: alive only for a ref that matches
        return ResultOf(s);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ContextSnapshot(EcsContextInfoNative* o)
    {
        Count(nameof(ContextSnapshot));
        if (o == null || o->Size < (uint)sizeof(EcsContextInfoNative)) return InvalidArgument;
        o->PresentMask = 0;
        for (var k = 0; k < 3; k++)
        {
            o->Sessions[k] = Sessions[k];
            if (Sessions[k] != 0) o->PresentMask |= 1u << k;
        }
        o->ExactProfile = ExactProfile ? 1u : 0u;
        o->Status = EcsReadConstants.Ok;
        return OkRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Query(ulong owner, EcsQueryRequestNative* q, EcsObjectRefNative* o, uint capacity, uint* required, uint* state)
    {
        Count(nameof(Query), owner);
        if (required != null) *required = 0;
        var bounds = q != null && (q->Flags & EcsReadConstants.QueryBounds) != 0;
        var badBounds = bounds && (!double.IsFinite(q->Radius) || q->Radius <= 0 || !double.IsFinite(q->Center.X) ||
            !double.IsFinite(q->Center.Y) || !double.IsFinite(q->Center.Z) || !double.IsFinite(q->Radius * q->Radius));
        if (owner == 0 || q == null || q->Size < (uint)sizeof(EcsQueryRequestNative) || q->WorldKind > 2 ||
            (q->ComponentCount != 0 && q->Components == 0) ||
            (q->Flags & ~(EcsReadConstants.QueryBounds | EcsReadConstants.QueryFrameQuiet)) != 0 || badBounds ||
            (capacity != 0 && o == null))
            return InvalidArgument;
        uint Refuse(uint s)
        {
            if (state != null) *state = s;
            return s;
        }
        if (Context(q->WorldKind) is var c && c != EcsReadConstants.Ok) return ResultOf(Refuse(c));
        if ((q->Flags & EcsReadConstants.QueryFrameQuiet) != 0 && q->WorldKind != 0) return ResultOf(Refuse(EcsReadConstants.Unsupported));
        if ((q->Flags & EcsReadConstants.QueryFrameQuiet) != 0 && FrameRunning) return ResultOf(Refuse(EcsReadConstants.Busy));
        if (ChangedNext > 0)
        {
            ChangedNext--;
            Refuse(EcsReadConstants.Unreadable);
            return ErrChanged;
        }
        var hashes = q->ComponentCount == 0 ? [] : new ReadOnlySpan<uint>((void*)q->Components, (int)q->ComponentCount).ToArray();
        List<(uint Id, Entity E)> matches;
        lock (Entities)
            matches = [.. Entities.Where(p => p.Key.Kind == q->WorldKind && hashes.All(h => p.Value.Components.ContainsKey(h)))
                .Select(p => (p.Key.Id, p.Value)).OrderBy(p => p.Id)];
        if ((q->Flags & EcsReadConstants.QueryBounds) != 0)
        {
            var center = new WorldPosition(q->Center.X, q->Center.Y, q->Center.Z);
            var r = q->Radius;
            matches = [.. matches.Where(m => m.E.Position is { } p && Dist(p, center) <= r)];
        }
        if (required != null) *required = (uint)matches.Count;
        if (capacity < matches.Count)
        {
            Refuse(EcsReadConstants.Ok);
            return BufferTooSmall;
        }
        ulong epoch;
        lock (Entities) epoch = Epochs.GetValueOrDefault(owner);
        for (var i = 0; i < matches.Count; i++)
            o[i] = new EcsObjectRefNative
            {
                Size = (uint)sizeof(EcsObjectRefNative), Entity = matches[i].Id, Generation = matches[i].E.Generation,
                WorldKind = q->WorldKind, Session = Sessions[q->WorldKind], LayoutStamp = matches[i].E.Layout, ReloadEpoch = epoch,
            };
        Refuse(EcsReadConstants.Ok);
        return OkRc;
    }

    private static double Dist(WorldPosition a, WorldPosition b)
    {
        double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Snapshot(ulong owner, EcsObjectRefNative* r, uint component, void* o, uint capacity, uint* required)
    {
        Count(nameof(Snapshot), owner);
        if (required != null) *required = 0;
        if (owner == 0 || BadRef(r) || component == 0 || (capacity != 0 && o == null)) return InvalidArgument;
        if (Validate(owner, r, out var e) is var s && s != EcsReadConstants.Ok) return ResultOf(s);
        if (!e!.Components.TryGetValue(component, out var bytes)) return NotFound;
        if (required != null) *required = (uint)bytes.Length;
        if (capacity < bytes.Length) return BufferTooSmall;
        if (UnreadableNext > 0)
        {
            UnreadableNext--;
            return ErrUnreadable;
        }
        bytes.CopyTo(new Span<byte>(o, bytes.Length));
        return OkRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SnapshotMany(ulong owner, EcsObjectRefNative* r, EcsSnapshotSpecNative* specs, uint count, void* o, uint capacity,
        uint* required, uint* presence)
    {
        Count(nameof(SnapshotMany), owner);
        if (required != null) *required = 0;
        if (owner == 0 || BadRef(r) || count == 0 || specs == null || (capacity != 0 && o == null)) return InvalidArgument;
        var quiet = false;
        for (var i = 0; i < count; i++)
        {
            if (specs[i].Component == 0 || (specs[i].Flags & ~EcsReadConstants.ReadFrameQuiet) != 0) return InvalidArgument;
            quiet |= specs[i].Flags != 0;
        }
        int RefuseAll(uint s)
        {
            if (presence != null) for (var i = 0; i < count; i++) presence[i] = s;
            return ResultOf(s);
        }
        if (Validate(owner, r, out var e) is var v && v != EcsReadConstants.Ok) return RefuseAll(v);
        if (quiet && r->WorldKind != 0) return RefuseAll(EcsReadConstants.Unsupported);
        if (quiet && FrameRunning) return RefuseAll(EcsReadConstants.Busy);
        var total = 0;
        for (var i = 0; i < count; i++)
            if (e!.Components.TryGetValue(specs[i].Component, out var b)) total += b.Length;
        if (required != null) *required = (uint)total;
        if (capacity < total)
        {
            if (presence != null)
                for (var i = 0; i < count; i++) presence[i] = e!.Components.ContainsKey(specs[i].Component) ? EcsReadConstants.Ok : EcsReadConstants.Absent;
            return BufferTooSmall;
        }
        if (UnreadableNext > 0)
        {
            UnreadableNext--;
            if (required != null) *required = 0;
            return RefuseAll(EcsReadConstants.Unreadable);
        }
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            if (e!.Components.TryGetValue(specs[i].Component, out var b))
            {
                b.CopyTo(new Span<byte>((byte*)o + at, b.Length));
                at += b.Length;
                if (presence != null) presence[i] = EcsReadConstants.Ok;
            }
            else if (presence != null) presence[i] = EcsReadConstants.Absent;
        }
        return OkRc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateUuid(ulong owner, EcsObjectRefNative* r, byte* guid)
    {
        Count(nameof(TemplateUuid), owner);
        if (owner == 0 || BadRef(r) || guid == null) return InvalidArgument;
        if (Validate(owner, r, out var e) is var s && s != EcsReadConstants.Ok) return ResultOf(s);
        if (e!.Template is not { } t) return NotFound;
        t.TryWriteBytes(new Span<byte>(guid, 16));
        return OkRc;
    }
}
