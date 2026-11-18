// SPDX-License-Identifier: MIT
// A fake kiln.world v4 table with the seven ECS-read members (kiln_world.h "ECS reads") over a dictionary of entities. The
// rules are the test kit's (Kiln.TestKit/Shared/EcsReadsLogic.cs, as native/services/world/ecs_reads.cpp answers), the
// same the harness's fake kiln.world uses; this file is the world they read and the table's entry points. Each member
// counts its calls, so a test can see that a read never reached the engine.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln.TestKit;

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

    private sealed class Dict : EcsReadsModel
    {
        public override ulong Session(uint kind) => kind < 3 ? Sessions[kind] : 0;

        public override bool TryEntity(uint kind, uint id, out uint generation, out ulong layout)
        {
            lock (Entities)
            {
                if (Entities.TryGetValue((kind, id), out var e))
                {
                    (generation, layout) = (e.Generation, e.Layout);
                    return true;
                }
            }
            (generation, layout) = (0, 0);
            return false;
        }

        public override byte[]? Component(uint kind, uint id, uint hash)
        {
            lock (Entities) return Entities.TryGetValue((kind, id), out var e) && e.Components.TryGetValue(hash, out var b) ? b : null;
        }

        public override bool Has(uint kind, uint id, uint hash)
        {
            lock (Entities) return Entities.TryGetValue((kind, id), out var e) && e.Components.ContainsKey(hash);
        }

        public override Guid? Template(uint kind, uint id)
        {
            lock (Entities) return Entities.TryGetValue((kind, id), out var e) ? e.Template : null;
        }

        public override WorldPosition? Position(uint kind, uint id)
        {
            lock (Entities) return Entities.TryGetValue((kind, id), out var e) ? e.Position : null;
        }

        public override IEnumerable<uint> Ids(uint kind)
        {
            lock (Entities) return [.. Entities.Keys.Where(k => k.Kind == kind).Select(k => k.Id)];
        }
    }

    private static readonly Dict M = new();

    public static readonly Dictionary<(uint Kind, uint Id), Entity> Entities = [];
    public static readonly ulong[] Sessions = new ulong[3];
    public static Dictionary<ulong, ulong> Epochs => M.Epochs;
    public static bool Capable { get => M.Capable; set { lock (M.Gate) M.Capable = value; } }
    public static bool ExactProfile { get => M.ExactProfile; set { lock (M.Gate) M.ExactProfile = value; } }
    public static bool FrameRunning { get => M.FrameRunning; set { lock (M.Gate) M.FrameRunning = value; } }
    public static int UnreadableNext { get => M.UnreadableNext; set { lock (M.Gate) M.UnreadableNext = value; } }  // the next N copies fail unreadable
    public static int ChangedNext { get => M.ChangedNext; set { lock (M.Gate) M.ChangedNext = value; } }        // the next N queries change
    public static List<ulong> Owners => M.Owners;  // the owner of every entity call, in order

    private static readonly EcsReadsTable* Native = (EcsReadsTable*)NativeMemory.AllocZeroed((nuint)sizeof(EcsReadsTable));

    public static nint Table => (nint)Native;

    /// <summary>A fresh model: server session 1, client session 7, no prediction world, capable, exact profile.</summary>
    public static void Reset(uint tableSize = 0)
    {
        lock (Entities) Entities.Clear();
        M.Reset();
        Sessions[0] = 1;
        Sessions[1] = 0;
        Sessions[2] = 7;
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

    public static int CallsOf(string member) => M.CallsOf(member);

    public static int EntityCalls => M.EntityCalls;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Capabilities() => EcsReadsLogic.Capabilities(M, 0x7);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteStatus(WriteStatusNative* status) => EcsReadsLogic.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityOpen(ulong owner, ulong epoch, uint kind, ulong expectedSession, uint entity, EcsObjectRefNative* o) =>
        EcsReadsLogic.EntityOpen(M, owner, epoch, kind, expectedSession, entity, o);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityValidate(ulong owner, EcsObjectRefNative* r, EcsObjectInfoNative* info) =>
        EcsReadsLogic.EntityValidate(M, owner, r, info);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ContextSnapshot(EcsContextInfoNative* o) => EcsReadsLogic.ContextSnapshot(M, o);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Query(ulong owner, EcsQueryRequestNative* q, EcsObjectRefNative* o, uint capacity, uint* required, uint* state) =>
        EcsReadsLogic.Query(M, owner, q, o, capacity, required, state);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Snapshot(ulong owner, EcsObjectRefNative* r, uint component, void* o, uint capacity, uint* required) =>
        EcsReadsLogic.Snapshot(M, owner, r, component, o, capacity, required);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SnapshotMany(ulong owner, EcsObjectRefNative* r, EcsSnapshotSpecNative* specs, uint count, void* o, uint capacity,
        uint* required, uint* presence) =>
        EcsReadsLogic.SnapshotMany(M, owner, r, specs, count, o, capacity, required, presence);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateUuid(ulong owner, EcsObjectRefNative* r, byte* guid) => EcsReadsLogic.TemplateUuid(M, owner, r, guid);
}
