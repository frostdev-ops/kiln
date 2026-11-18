// SPDX-License-Identifier: MIT
// kiln.world's seven ECS-read members (kiln_world.h "ECS reads") answered the way native/services/world/ecs_reads.cpp
// does, over a world a fake provides: per-owner reload epochs, a ref that is stale after its id is recycled or its layout,
// session or epoch changed, an absent component confirmed against the ref, frame-quiet reads that are busy while a frame
// runs and unsupported off the server world, a short buffer that reports the size and copies nothing, and queries that are
// complete or nothing. One copy of these rules for every fake: the harness's kiln.world (World/WorldTable.cs, over the fake
// world's entities) and Kiln's own unit tests (Kiln.Api.Tests/FakeEcsReads.cs, over a dictionary).
namespace Kiln.TestKit;

/// <summary>The world the ECS reads answer from, and the knobs a test turns. The logic calls the world's members on the
/// thread it runs on; <see cref="Gate"/> guards the epochs, the counters and the knobs.</summary>
internal abstract class EcsReadsModel
{
    public readonly object Gate = new();
    public readonly Dictionary<ulong, ulong> Epochs = [];
    public readonly Dictionary<string, int> Calls = [];
    public readonly List<ulong> Owners = [];  // the owner of every entity call, in order

    /// <summary>The build has the reads and their capability bit (KILN_WORLD_CAP_ECS_READ).</summary>
    public bool Capable = true;

    /// <summary>Kiln has an exact engine profile for the build: off, every entity read is unavailable.</summary>
    public bool ExactProfile = true;

    /// <summary>A server frame runs now: every frame-quiet read is busy.</summary>
    public bool FrameRunning;

    /// <summary>The next N stable copies are unreadable, the next N queries changed, the next N frame-quiet reads busy.</summary>
    public int UnreadableNext, ChangedNext, BusyNext;

    public abstract ulong Session(uint kind);

    public abstract bool TryEntity(uint kind, uint id, out uint generation, out ulong layout);

    /// <summary>A copy of the component's bytes; null when the entity has none.</summary>
    public abstract byte[]? Component(uint kind, uint id, uint hash);

    public abstract bool Has(uint kind, uint id, uint hash);

    public abstract Guid? Template(uint kind, uint id);

    public abstract WorldPosition? Position(uint kind, uint id);

    public abstract IEnumerable<uint> Ids(uint kind);

    public void Reset()
    {
        lock (Gate)
        {
            Epochs.Clear();
            Calls.Clear();
            Owners.Clear();
            Capable = ExactProfile = true;
            FrameRunning = false;
            UnreadableNext = ChangedNext = BusyNext = 0;
        }
    }

    public void Count(string member, ulong owner = 0)
    {
        lock (Gate)
        {
            Calls[member] = Calls.GetValueOrDefault(member) + 1;
            if (owner != 0) Owners.Add(owner);
        }
    }

    public int CallsOf(string member)
    {
        lock (Gate) return Calls.GetValueOrDefault(member);
    }

    /// <summary>Calls of the members that read entities (not capabilities or the context).</summary>
    public int EntityCalls
    {
        get
        {
            lock (Gate) return Calls.Where(c => c.Key is not ("Capabilities" or "ContextSnapshot")).Sum(c => c.Value);
        }
    }

    internal bool Take(ref int counter)
    {
        lock (Gate)
        {
            if (counter <= 0) return false;
            counter--;
            return true;
        }
    }

    internal bool Busy()
    {
        lock (Gate)
        {
            if (FrameRunning) return true;
            if (BusyNext <= 0) return false;
            BusyNext--;
            return true;
        }
    }

    internal ulong EpochOf(ulong owner)
    {
        lock (Gate) return Epochs.GetValueOrDefault(owner);
    }
}

internal static unsafe class EcsReadsLogic
{
    public const int OkRc = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;
    public const int ErrChanged = 100, ErrStale = 101, ErrUnreadable = 102, ErrBusy = 103, ErrUnsupported = 104;

    public static int ResultOf(uint status) => status switch
    {
        EcsReadConstants.Ok => OkRc,
        EcsReadConstants.Absent => NotFound,
        EcsReadConstants.Stale => ErrStale,
        EcsReadConstants.Unreadable => ErrUnreadable,
        EcsReadConstants.Busy => ErrBusy,
        EcsReadConstants.Unsupported => ErrUnsupported,
        _ => Unavailable,
    };

    public static uint Capabilities(EcsReadsModel m, uint others)
    {
        m.Count(nameof(Capabilities));
        bool capable;
        lock (m.Gate) capable = m.Capable;
        return capable ? others | EcsReadConstants.CapEcsRead : others;
    }

    // the world of `kind` as the owner sees it: OK, or why not
    private static uint Context(EcsReadsModel m, uint kind)
    {
        bool exact;
        lock (m.Gate) exact = m.ExactProfile;
        return !exact || kind == (uint)WorldKind.Prediction || kind > 2 || m.Session(kind) == 0 ? EcsReadConstants.Unavailable : EcsReadConstants.Ok;
    }

    private static uint Validate(EcsReadsModel m, ulong owner, EcsObjectRefNative* r)
    {
        if (Context(m, r->WorldKind) is var c && c != EcsReadConstants.Ok) return c;
        if (r->Session != m.Session(r->WorldKind)) return EcsReadConstants.Stale;
        if (r->ReloadEpoch != m.EpochOf(owner)) return EcsReadConstants.Stale;
        if (!m.TryEntity(r->WorldKind, r->Entity, out var generation, out var layout)) return EcsReadConstants.Absent;
        if (generation != r->Generation || layout != r->LayoutStamp) return EcsReadConstants.Stale;
        return EcsReadConstants.Ok;
    }

    private static bool BadRef(EcsObjectRefNative* r) => r == null || r->Size != (uint)sizeof(EcsObjectRefNative) || r->Entity == 0;

    public static int EntityOpen(EcsReadsModel m, ulong owner, ulong epoch, uint kind, ulong expectedSession, uint entity, EcsObjectRefNative* o)
    {
        m.Count(nameof(EntityOpen), owner);
        if (owner == 0 || o == null || entity == 0 || kind > 2) return InvalidArgument;
        lock (m.Gate)
        {
            var current = m.Epochs.GetValueOrDefault(owner);
            if (epoch > current) m.Epochs[owner] = epoch;
            else if (epoch < current) return ErrStale;
        }
        if (Context(m, kind) is var c && c != EcsReadConstants.Ok) return ResultOf(c);
        var session = m.Session(kind);
        if (expectedSession != 0 && expectedSession != session) return ErrStale;
        if (!m.TryEntity(kind, entity, out var generation, out var layout)) return NotFound;
        *o = new EcsObjectRefNative
        {
            Size = (uint)sizeof(EcsObjectRefNative), Entity = entity, Generation = generation, WorldKind = kind,
            Session = session, LayoutStamp = layout, ReloadEpoch = epoch,
        };
        return OkRc;
    }

    public static int EntityValidate(EcsReadsModel m, ulong owner, EcsObjectRefNative* r, EcsObjectInfoNative* info)
    {
        m.Count(nameof(EntityValidate), owner);
        if (owner == 0 || BadRef(r) || info == null || info->Size < (uint)sizeof(EcsObjectInfoNative)) return InvalidArgument;
        var s = Validate(m, owner, r);
        var alive = m.TryEntity(r->WorldKind, r->Entity, out var generation, out _);
        info->Status = s;
        info->Entity = r->Entity;
        info->Generation = alive ? generation : 0;
        info->WorldKind = r->WorldKind;
        info->Flags = alive && s == EcsReadConstants.Ok ? EcsReadConstants.ObjectAlive : 0;  // fill_info: alive only for a ref that matches
        return ResultOf(s);
    }

    public static int ContextSnapshot(EcsReadsModel m, EcsContextInfoNative* o)
    {
        m.Count(nameof(ContextSnapshot));
        if (o == null || o->Size < (uint)sizeof(EcsContextInfoNative)) return InvalidArgument;
        o->PresentMask = 0;
        for (var k = 0u; k < 3; k++)
        {
            var session = m.Session(k);
            o->Sessions[k] = session;
            if (session != 0) o->PresentMask |= 1u << (int)k;
        }
        lock (m.Gate) o->ExactProfile = m.ExactProfile ? 1u : 0u;
        o->Status = EcsReadConstants.Ok;
        return OkRc;
    }

    public static int Query(EcsReadsModel m, ulong owner, EcsQueryRequestNative* q, EcsObjectRefNative* o, uint capacity, uint* required, uint* state)
    {
        m.Count(nameof(Query), owner);
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
        var quiet = (q->Flags & EcsReadConstants.QueryFrameQuiet) != 0;
        if (Context(m, q->WorldKind) is var c && c != EcsReadConstants.Ok) return ResultOf(Refuse(c));
        if (quiet && q->WorldKind != 0) return ResultOf(Refuse(EcsReadConstants.Unsupported));
        if (quiet && m.Busy()) return ResultOf(Refuse(EcsReadConstants.Busy));
        if (m.Take(ref m.ChangedNext))
        {
            Refuse(EcsReadConstants.Unreadable);
            return ErrChanged;
        }
        var kind = q->WorldKind;
        var hashes = q->ComponentCount == 0 ? [] : new ReadOnlySpan<uint>((void*)q->Components, (int)q->ComponentCount).ToArray();
        var matches = new List<(uint Id, uint Generation, ulong Layout)>();
        foreach (var id in m.Ids(kind).Order())
        {
            if (!hashes.All(h => m.Has(kind, id, h))) continue;
            if (bounds && (m.Position(kind, id) is not { } p ||
                           Dist(p, new WorldPosition(q->Center.X, q->Center.Y, q->Center.Z)) > q->Radius)) continue;
            if (m.TryEntity(kind, id, out var generation, out var layout)) matches.Add((id, generation, layout));
        }
        if (required != null) *required = (uint)matches.Count;
        if (capacity < matches.Count)
        {
            Refuse(EcsReadConstants.Ok);
            return BufferTooSmall;
        }
        var epoch = m.EpochOf(owner);
        var session = m.Session(kind);
        for (var i = 0; i < matches.Count; i++)
            o[i] = new EcsObjectRefNative
            {
                Size = (uint)sizeof(EcsObjectRefNative), Entity = matches[i].Id, Generation = matches[i].Generation,
                WorldKind = kind, Session = session, LayoutStamp = matches[i].Layout, ReloadEpoch = epoch,
            };
        Refuse(EcsReadConstants.Ok);
        return OkRc;
    }

    private static double Dist(WorldPosition a, WorldPosition b)
    {
        double x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    public static int Snapshot(EcsReadsModel m, ulong owner, EcsObjectRefNative* r, uint component, void* o, uint capacity, uint* required)
    {
        m.Count(nameof(Snapshot), owner);
        if (required != null) *required = 0;
        if (owner == 0 || BadRef(r) || component == 0 || (capacity != 0 && o == null)) return InvalidArgument;
        if (Validate(m, owner, r) is var s && s != EcsReadConstants.Ok) return ResultOf(s);
        if (m.Component(r->WorldKind, r->Entity, component) is not { } bytes) return NotFound;
        if (required != null) *required = (uint)bytes.Length;
        if (capacity < bytes.Length) return BufferTooSmall;
        if (m.Take(ref m.UnreadableNext)) return ErrUnreadable;
        bytes.CopyTo(new Span<byte>(o, bytes.Length));
        return OkRc;
    }

    public static int SnapshotMany(EcsReadsModel m, ulong owner, EcsObjectRefNative* r, EcsSnapshotSpecNative* specs, uint count, void* o,
        uint capacity, uint* required, uint* presence)
    {
        m.Count(nameof(SnapshotMany), owner);
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
        if (Validate(m, owner, r) is var v && v != EcsReadConstants.Ok) return RefuseAll(v);
        if (quiet && r->WorldKind != 0) return RefuseAll(EcsReadConstants.Unsupported);
        var each = new byte[]?[count];
        var total = 0;
        for (var i = 0; i < count; i++)
        {
            each[i] = m.Component(r->WorldKind, r->Entity, specs[i].Component);
            total += each[i]?.Length ?? 0;
        }
        if (required != null) *required = (uint)total;
        if (capacity < total)
        {
            if (presence != null)
                for (var i = 0; i < count; i++) presence[i] = each[i] is not null ? EcsReadConstants.Ok : EcsReadConstants.Absent;
            return BufferTooSmall;
        }
        // the frame-quiet window opens only now, after the sizing: a short buffer is TooSmall even while a frame runs
        if (quiet && m.Busy())
        {
            if (required != null) *required = 0;
            return RefuseAll(EcsReadConstants.Busy);
        }
        if (m.Take(ref m.UnreadableNext))
        {
            if (required != null) *required = 0;
            return RefuseAll(EcsReadConstants.Unreadable);
        }
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            if (each[i] is { } b)
            {
                b.CopyTo(new Span<byte>((byte*)o + at, b.Length));
                at += b.Length;
                if (presence != null) presence[i] = EcsReadConstants.Ok;
            }
            else if (presence != null) presence[i] = EcsReadConstants.Absent;
        }
        return OkRc;
    }

    public static int TemplateUuid(EcsReadsModel m, ulong owner, EcsObjectRefNative* r, byte* guid)
    {
        m.Count(nameof(TemplateUuid), owner);
        if (owner == 0 || BadRef(r) || guid == null) return InvalidArgument;
        if (Validate(m, owner, r) is var s && s != EcsReadConstants.Ok) return ResultOf(s);
        if (m.Template(r->WorldKind, r->Entity) is not { } t) return NotFound;
        t.TryWriteBytes(new Span<byte>(guid, 16));
        return OkRc;
    }
}
