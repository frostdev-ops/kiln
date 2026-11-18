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

        // every component a test added is registered with its size, untracked and plain, unless the test says otherwise
        public override EcsWriteFacts WriteFacts(uint hash)
        {
            lock (Entities)
            {
                if (Policies.TryGetValue(hash, out var p)) return p;
                foreach (var e in Entities.Values)
                    if (e.Components.TryGetValue(hash, out var b)) return new(true, b.Length, true, false, false, true, b.Length, null);
            }
            return new(false, 0, false, false, false, false, 0, null);
        }

        public override bool WriteComponent(uint kind, uint id, uint hash, ReadOnlySpan<byte> bytes)
        {
            lock (Entities)
            {
                if (!Entities.TryGetValue((kind, id), out var e) || !e.Components.TryGetValue(hash, out var b) || b.Length != bytes.Length) return false;
                e.Components[hash] = bytes.ToArray();
                return true;
            }
        }

        public override ulong Step => FakeEcsReads.Step;

        public override bool TypesAvailable => FakeEcsReads.TypesAvailable;
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
    public static readonly Dictionary<uint, EcsWriteFacts> Policies = [];  // a component's policy facts, when a test sets them
    public static ulong Step;
    public static bool TypesAvailable = true;  // kiln.types is here
    public static int Subscribe(ulong owner, nint fn, nint user, out ulong handle)
    {
        ulong h = 0;
        var rc = EcsWritesLogic.Subscribe(M, owner, fn, user, &h);
        handle = h;
        return rc;
    }
    public static int Unsubscribe(ulong handle) => EcsWritesLogic.Unsubscribe(M, handle);
    public static bool WritesCapable { get => M.WritesCapable; set { lock (M.Gate) M.WritesCapable = value; } }
    public static int GateBusyNext { get => M.GateBusyNext; set { lock (M.Gate) M.GateBusyNext = value; } }
    public static int UncertainNext { get => M.UncertainNext; set { lock (M.Gate) M.UncertainNext = value; } }
    public static int RolledBackNext { get => M.RolledBackNext; set { lock (M.Gate) M.RolledBackNext = value; } }
    public static int EventCapacity { get => M.EventCapacity; set { lock (M.Gate) M.EventCapacity = value; } }
    public static int Writes => M.Writes;
    public static int Subscriptions { get { lock (M.Gate) return M.Subscriptions.Count; } }
    public static void Retire(ulong owner) => EcsWritesLogic.Retire(M, owner);
    public static int Drain() => EcsWritesLogic.Drain(M);
    public static int SubscribeRefusedNext { get => M.SubscribeRefusedNext; set { lock (M.Gate) M.SubscribeRefusedNext = value; } }

    /// <summary>A native service's compare-exchange (no managed mod behind its owner): the ref opened under the owner as
    /// its epoch, as kiln.world's rule wants.</summary>
    public static int NativeWrite(ulong owner, uint entity, string component, long expected, long desired)
    {
        EcsObjectRefNative r;
        if (EcsReadsLogic.EntityOpen(M, owner, owner, 0, 0, entity, &r) != 0) return -1;
        MutationReplyNative reply = default;
        reply.Size = (uint)sizeof(MutationReplyNative);
        return EcsWritesLogic.CompareExchange(M, owner, &r, World.ComponentHash(component), (byte*)&expected, null, (byte*)&desired, 8, null, &reply);
    }

    private static readonly EcsWritesTable* Writes4 = (EcsWritesTable*)NativeMemory.AllocZeroed((nuint)sizeof(EcsWritesTable));
    private static EcsReadsTable* Native => &Writes4->Reads;

    public static nint Table => (nint)Native;

    /// <summary>A fresh model: server session 1, client session 7, no prediction world, capable, exact profile, with the
    /// writes (a <paramref name="tableSize"/> below 616 is a kiln.world without them).</summary>
    public static void Reset(uint tableSize = 0)
    {
        lock (Entities)
        {
            Entities.Clear();
            Policies.Clear();
        }
        M.Reset();
        Step = 1;
        TypesAvailable = true;
        *Writes4 = default;
        Sessions[0] = 1;
        Sessions[1] = 0;
        Sessions[2] = 7;
        *Native = default;
        Native->V3.V1.Header = new ServiceHeader { Size = tableSize == 0 ? (uint)sizeof(EcsWritesTable) : tableSize, Version = 4 };
        Native->V3.Capabilities = &Capabilities;
        Native->V3.WriteStatus = &WriteStatus;  // Kiln.World's first use reports world.tracked_writes through it
        Native->EntityOpen = &EntityOpen;
        Native->EntityValidate = &EntityValidate;
        Native->ContextSnapshot = &ContextSnapshot;
        Native->Query = &Query;
        Native->Snapshot = &Snapshot;
        Native->SnapshotMany = &SnapshotMany;
        Native->TemplateUuid = &TemplateUuid;
        Writes4->WritePolicy = &WritePolicy;
        Writes4->CompareExchange = &CompareExchange;
        Writes4->WriteSubscribe = &WriteSubscribe;
        Writes4->WriteUnsubscribe = &WriteUnsubscribe;
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
    private static uint Capabilities() => EcsWritesLogic.Capabilities(M, EcsReadsLogic.Capabilities(M, 0x7));

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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WritePolicy(uint kind, uint component, EcsWritePolicyNative* o) => EcsWritesLogic.WritePolicy(M, kind, component, o);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CompareExchange(ulong owner, EcsObjectRefNative* r, uint component, void* expected, void* mask, void* desired, uint bytes,
        void* confirmed, MutationReplyNative* reply) =>
        EcsWritesLogic.CompareExchange(M, owner, r, component, (byte*)expected, (byte*)mask, (byte*)desired, bytes, (byte*)confirmed, reply);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteSubscribe(ulong owner, delegate* unmanaged[Cdecl]<EcsWriteEventNative*, void*, void> fn, void* user, ulong* handle) =>
        EcsWritesLogic.Subscribe(M, owner, (nint)fn, (nint)user, handle);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteUnsubscribe(ulong handle) => EcsWritesLogic.Unsubscribe(M, handle);
}
