// SPDX-License-Identifier: MIT
// The fake kiln.queries (native/core/abi/services/kiln_queries.h, v1) under the real Kiln.Queries: voxel rays answer from
// the test's script, entity origins from the harness's fake world, the ray budget counts per step as the service's does.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_queries.h</c>. The sizes are pinned by tests to Kiln.Queries' private mirrors.</summary>
internal static unsafe class QueriesAbi
{
    public const string Service = "kiln.queries";
    public const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3 { public double X, Y, Z; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RayRequest { public uint Size, Realm; public Vec3 From, To; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RayResult
    {
        public uint Size, Ok;
        public NativeStr Reason;
        public uint Hit, Material;
        public ushort Voxel, Reserved;
        public Vec3 Position;
        public double Distance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EntityRequest
    {
        public uint Size, Shape;
        public Vec3 Center, HalfExtents;
        public double Radius;
        public uint Exclude, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EntityResult
    {
        public uint Size, Ok;
        public NativeStr Reason;
        public uint Written, Total;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<RayRequest*, RayResult*, int> RayVoxels;
        public delegate* unmanaged[Cdecl]<EntityRequest*, uint*, uint, EntityResult*, int> Entities;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, int> PathSubmit;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> PathCancel;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, uint, uint*, int> PathPoll;
        public delegate* unmanaged[Cdecl]<ulong, int> PathRetire;
    }
}

/// <summary>One harness's kiln.queries: the script, the record and the step's ray count.</summary>
internal sealed class QueriesFakeState
{
    public readonly object Lock = new();
    public Func<FakeVoxelRay, FakeVoxelAnswer>? Voxels;
    public int MaxRaysPerStep = 64;
    public readonly List<FakeVoxelRay> Rays = [];
    public ulong BucketStep;
    public int Calls;
}

internal sealed unsafe class QueriesFake : IKitFake
{
    public const string RayFeature = "queries.voxel_ray";

    public string Service => QueriesAbi.Service;

    private static QueriesAbi.Table* _table;
    private static readonly Dictionary<string, NativeStr> Reasons = new(StringComparer.Ordinal);

    public void Register()
    {
        if (_table == null)
        {
            var t = (QueriesAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(QueriesAbi.Table));  // process lifetime, never freed
            t->Header = new ServiceHeader { Size = (uint)sizeof(QueriesAbi.Table), Version = QueriesAbi.Version };
            t->RayVoxels = &RayVoxels;
            t->Entities = &Entities;
            t->PathSubmit = &PathSubmit;
            t->PathCancel = &PathCancel;
            t->PathPoll = &PathPoll;
            t->PathRetire = &PathRetire;
            _table = t;
        }
        KitBackend.Instance.RegisterTable(QueriesAbi.Service, (nint)_table);
        // The query fake uses the world fake's implemented origin/Find operations, so their feature rows are truthful too.
        FakeTables.Features(["world.entities", "world.find"]);
        ReportRay(false);
    }

    internal static void ReportRay(bool scripted)
    {
        if (scripted) FakeTables.Features([RayFeature]);
        else FakeTables.Offline(RayFeature, "voxel rays need a KilnHarness.Queries.Voxels script; there are no voxels offline");
    }

    public void Start(KilnHarness harness)
    {
        var state = KitFakes.State<QueriesFakeState>(harness);
        lock (state.Lock) ReportRay(state.Voxels != null);
    }
    public void BeforeStep(KilnHarness harness, ulong step) { }
    public void End(KilnHarness harness) => ReportRay(false);

    public void AddCoverage(List<CoverageRow> rows) =>
        rows.Add(new CoverageRow("Queries", CoverageKind.RecordingFake, [QueriesAbi.Service, RayFeature, "queries.ground", "queries.line_of_sight", "queries.overlap_origins"],
            "Kiln.Queries' own code on the kit's kiln.queries. A voxel ray answers what the test scripts (KilnHarness.Queries.Voxels; without a script every ray is " +
            "unavailable): there are no voxels offline, so no test here says what the game's ray would hit. The rules around the ray are the service's: bad numbers " +
            "throw, the budget (KilnHarness.Queries.MaxRaysPerStep, 64) refuses with BudgetExceeded and starts again each step, refusals keep their reasons. " +
            "EntitiesInSphere and EntitiesInBox read the harness's fake world: entity origins, closed shapes, nearest first. Paths answer unavailable."));

    private static NativeStr Str(string text)
    {
        lock (Reasons)
        {
            if (Reasons.TryGetValue(text, out var have)) return have;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var p = (byte*)NativeMemory.Alloc((nuint)Math.Max(bytes.Length, 1));  // process lifetime, as the service's reason strings
            bytes.CopyTo(new Span<byte>(p, bytes.Length));
            return Reasons[text] = new NativeStr { Ptr = p, Len = (nuint)bytes.Length };
        }
    }

    private static bool Finite(QueriesAbi.Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    // the service's number rule (native/services/queries/ray.h): a length above the game's minimum and at most 256 m, as doubles and as floats
    private static bool IsRay(QueriesAbi.Vec3 a, QueriesAbi.Vec3 b)
    {
        if (!Finite(a) || !Finite(b)) return false;
        var length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        if (!(length > 0) || !(length <= 256)) return false;
        float x = (float)b.X - (float)a.X, y = (float)b.Y - (float)a.Y, z = (float)b.Z - (float)a.Z;
        var narrow = MathF.Sqrt(x * x + y * y + z * z);
        return float.IsFinite(narrow) && narrow > 1e-20f && narrow <= 256f;
    }

    private static int Refuse(QueriesAbi.RayResult* out_, string reason)
    {
        out_->Ok = 0;
        out_->Reason = Str(reason);
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RayVoxels(QueriesAbi.RayRequest* req, QueriesAbi.RayResult* out_)
    {
        try
        {
            if (req == null || out_ == null || req->Size < (uint)sizeof(QueriesAbi.RayRequest) || out_->Size < (uint)sizeof(QueriesAbi.RayResult)) return FakeTables.InvalidArgument;
            *out_ = new QueriesAbi.RayResult { Size = out_->Size };
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            if (!Finite(req->From) || !Finite(req->To) || req->Realm > 255) return FakeTables.InvalidArgument;
            if (FakeTables.Active(QueriesAbi.Service) is not { } harness || FakeTables.Off(RayFeature)) return FakeTables.Unavailable;
            if (harness.EntitiesFake is not { Started: true }) return Refuse(out_, "WorldNotReady");
            if (!IsRay(req->From, req->To)) return FakeTables.InvalidArgument;
            var state = KitFakes.State<QueriesFakeState>(harness);
            var ray = new FakeVoxelRay(new(req->From.X, req->From.Y, req->From.Z), new(req->To.X, req->To.Y, req->To.Z), (byte)req->Realm);
            Func<FakeVoxelRay, FakeVoxelAnswer>? script;
            lock (state.Lock)
            {
                script = state.Voxels;
                if (script == null) return FakeTables.Unavailable;
                var step = harness.CurrentStep;
                if (state.BucketStep != step)
                {
                    state.BucketStep = step;
                    state.Calls = 0;
                }
                if (state.Calls >= state.MaxRaysPerStep) return Refuse(out_, "BudgetExceeded");
                state.Calls++;
                state.Rays.Add(ray);
            }
            var answer = script(ray);
            if (answer.Refusal is { } reason) return Refuse(out_, reason);
            out_->Ok = 1;
            if (answer.HitAt is not { } at) return FakeTables.Ok;
            out_->Hit = 1;
            out_->Material = answer.Material;
            out_->Voxel = answer.Voxel;
            out_->Position = new QueriesAbi.Vec3 { X = at.X, Y = at.Y, Z = at.Z };
            out_->Distance = ray.From.DistanceTo(at);
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Entities(QueriesAbi.EntityRequest* req, uint* ids, uint capacity, QueriesAbi.EntityResult* out_)
    {
        try
        {
            if (req == null || out_ == null || req->Size < (uint)sizeof(QueriesAbi.EntityRequest) || out_->Size < (uint)sizeof(QueriesAbi.EntityResult) || (capacity != 0 && ids == null))
                return FakeTables.InvalidArgument;
            *out_ = new QueriesAbi.EntityResult { Size = out_->Size };
            if (!FakeTables.OnTickThread) return FakeTables.WrongThread;
            var box = req->Shape == 1;
            if (req->Shape > 1 || !Finite(req->Center)) return FakeTables.InvalidArgument;
            if (box ? !Finite(req->HalfExtents) || req->HalfExtents.X < 0 || req->HalfExtents.Y < 0 || req->HalfExtents.Z < 0 : !double.IsFinite(req->Radius) || req->Radius < 0)
                return FakeTables.InvalidArgument;
            if (FakeTables.Active(QueriesAbi.Service) is not { } harness) return FakeTables.Unavailable;
            if (harness.EntitiesFake is not { Started: true } world)
            {
                out_->Reason = Str("WorldNotReady");
                return FakeTables.Ok;
            }
            var found = new List<(double D2, uint Id)>();
            foreach (var id in world.Entities)
            {
                if (id == req->Exclude || world.PositionOf(id) is not { } p) continue;
                double dx = p.X - req->Center.X, dy = p.Y - req->Center.Y, dz = p.Z - req->Center.Z;
                var d2 = dx * dx + dy * dy + dz * dz;
                var inside = box
                    ? Math.Abs(dx) <= req->HalfExtents.X && Math.Abs(dy) <= req->HalfExtents.Y && Math.Abs(dz) <= req->HalfExtents.Z
                    : d2 <= req->Radius * req->Radius;
                if (inside) found.Add((d2, id));
            }
            found.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Id.CompareTo(b.Id));
            out_->Ok = 1;
            out_->Total = (uint)found.Count;
            out_->Written = Math.Min(out_->Total, capacity);
            for (var i = 0; i < out_->Written; i++) ids[i] = found[i].Id;
            return FakeTables.Ok;
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    // Paths are not modelled.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PathSubmit(ulong owner, void* request, void* admission) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PathCancel(ulong owner, ulong request) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PathPoll(ulong owner, void* update, void* points, uint capacity, uint* needed) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PathRetire(ulong owner) => FakeTables.Unavailable;
}
