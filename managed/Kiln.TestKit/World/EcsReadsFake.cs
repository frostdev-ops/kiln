// SPDX-License-Identifier: MIT
// kiln.world's ECS reads from task threads (kiln_world.h "ECS reads", the seven members at 528..576) in the harness's fake
// kiln.world: WorldConcurrent runs on the fake world's entities. The rules are the shared ones (Shared/EcsReadsLogic.cs,
// as native/services/world/ecs_reads.cpp answers); this file is the world they read. A read from a task's worker runs on
// that worker while the harness's tick thread is between work items (TickThread.WorldGate): a step or a call never
// changes the world under it.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.TestKit;

internal static unsafe class EcsReadsFake
{
    /// <summary>The capability row of the reads: a fake feature row (switch it off with
    /// <c>kiln.Capabilities.Override("world.ecs_read", false)</c>: then the build has no exact profile).</summary>
    public const string Row = "world.ecs_read";

    /// <summary>The fake world as the ECS reads see it: the server world's entities, its session from the fake of the
    /// game's programs (game_world_session), no client or prediction world, no template names.</summary>
    internal sealed class Model(EntitiesFake world, KilnHarness harness) : EcsReadsModel
    {
        public override ulong Session(uint kind) => kind == (uint)WorldKind.Server ? KitFakes.State<SystemsState>(harness).Session : 0;

        public override bool TryEntity(uint kind, uint id, out uint generation, out ulong layout)
        {
            if (kind == (uint)WorldKind.Server) return world.Facts(id, out generation, out layout);
            (generation, layout) = (0, 0);
            return false;
        }

        public override byte[]? Component(uint kind, uint id, uint hash) =>
            kind == (uint)WorldKind.Server && world.Block(id, hash) is { } slot ? new ReadOnlySpan<byte>((void*)slot.Data, slot.Size).ToArray() : null;

        public override bool Has(uint kind, uint id, uint hash) => kind == (uint)WorldKind.Server && world.Block(id, hash) is not null;

        public override Guid? Template(uint kind, uint id) => null;

        // kiln.world's write-event subscriptions outlive a harness's world, as the service's do the server's: Kiln
        // subscribes once a process (WorldExchanges), so every harness after the first gets the events too.
        public override EcsWriteSubscriptions Subs => ProcessSubscriptions;

        public override WorldPosition? Position(uint kind, uint id) => kind == (uint)WorldKind.Server ? world.PositionOf(id) : null;

        public override IEnumerable<uint> Ids(uint kind) => kind == (uint)WorldKind.Server ? world.Entities : [];

        // The compare-exchange policy's facts: the size from Kiln.Game's types (or the bytes the test set), the tracking
        // only as a test gave it with FakeWorld.Track (unknown otherwise, so not writable: the kit reads no engine), plain
        // bytes from Kiln.Game's types only, as kiln.types answers.
        public override EcsWriteFacts WriteFacts(uint hash)
        {
            var (size, known, save, replication) = world.WriteFactsOf(hash);
            if (size is not { } s) return new(false, 0, false, false, false, false, 0, null);
            var plain = TypesFake.Plain(hash, out var reflected, out var why);
            return new(true, s, known, save, replication, plain == true, reflected, why);
        }

        public override bool WriteComponent(uint kind, uint id, uint hash, ReadOnlySpan<byte> bytes) =>
            kind == (uint)WorldKind.Server && world.Overwrite(id, hash, bytes);

        public override ulong Step => world.Steps;

        public override bool TypesAvailable => TypesFake.Available;
    }

    private static readonly EcsWriteSubscriptions ProcessSubscriptions = new();

    /// <summary>The capability row of the compare-exchange writes: a fake feature row (off: a build without them).</summary>
    public const string WriteRow = "world.ecs_write";

    public static void Fill(WorldAbi.Table* t)
    {
        t->EntityOpen = &EntityOpen;
        t->EntityValidate = &EntityValidate;
        t->EcsContextSnapshot = &ContextSnapshot;
        t->EcsQuery = &Query;
        t->EcsSnapshot = &Snapshot;
        t->EcsSnapshotMany = &SnapshotMany;
        t->EcsTemplateUuid = &TemplateUuid;
        t->EcsWritePolicy = &WritePolicy;
        t->EcsCompareExchange = &CompareExchange;
        t->EcsWriteSubscribe = &WriteSubscribe;
        t->EcsWriteUnsubscribe = &WriteUnsubscribe;
    }

    /// <summary>The capability word with the reads' bit while the row is on.</summary>
    public static uint Capabilities(EntitiesFake fake, uint others)
    {
        Sync(fake.Reads);
        return EcsWritesLogic.Capabilities(fake.Reads, EcsReadsLogic.Capabilities(fake.Reads, others));
    }

    /// <summary>kiln.world's drain in its server Pre: the committed-write events to their subscribers (tick thread).</summary>
    public static void Drain(EntitiesFake fake) => EcsWritesLogic.Drain(fake.Reads);

    // the test's switch: the row off is a build with no exact profile (and no capability bit)
    private static void Sync(Model m)
    {
        var on = !FakeTables.Off(Row);
        var writes = !FakeTables.Off(WriteRow);
        lock (m.Gate)
        {
            m.Capable = m.ExactProfile = on;
            m.WritesCapable = writes;
        }
    }

    // One read, between the tick thread's work items (or inline on it). No harness, a stopped one, or a world gate not free
    // within the call timeout: unavailable, as the service is when it has no world.
    private static int Read(Func<Model, int> read)
    {
        var h = FakeTables.Active(WorldAbi.Service);
        if (h?.EntitiesFake is not { } fake || h.TickThread.AbandonReason is not null) return EcsReadsLogic.Unavailable;
        var gate = h.TickThread.WorldGate;
        var taken = false;
        try
        {
            Monitor.TryEnter(gate, h.Options.CallTimeout, ref taken);
            if (!taken) return EcsReadsLogic.Unavailable;
            Sync(fake.Reads);
            return read(fake.Reads);
        }
        catch (Exception)
        {
            return EcsReadsLogic.Unavailable;  // nothing crosses back into a native caller
        }
        finally
        {
            if (taken) Monitor.Exit(gate);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityOpen(ulong owner, ulong epoch, uint kind, ulong expectedSession, uint entity, EcsObjectRefNative* o)
    {
        var p = (nint)o;
        return Read(m => EcsReadsLogic.EntityOpen(m, owner, epoch, kind, expectedSession, entity, (EcsObjectRefNative*)p));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EntityValidate(ulong owner, EcsObjectRefNative* r, EcsObjectInfoNative* info)
    {
        var (pr, pi) = ((nint)r, (nint)info);
        return Read(m => EcsReadsLogic.EntityValidate(m, owner, (EcsObjectRefNative*)pr, (EcsObjectInfoNative*)pi));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ContextSnapshot(EcsContextInfoNative* o)
    {
        var p = (nint)o;
        return Read(m => EcsReadsLogic.ContextSnapshot(m, (EcsContextInfoNative*)p));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Query(ulong owner, EcsQueryRequestNative* q, EcsObjectRefNative* o, uint capacity, uint* required, uint* state)
    {
        var (pq, po, preq, pst) = ((nint)q, (nint)o, (nint)required, (nint)state);
        return Read(m => EcsReadsLogic.Query(m, owner, (EcsQueryRequestNative*)pq, (EcsObjectRefNative*)po, capacity, (uint*)preq, (uint*)pst));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Snapshot(ulong owner, EcsObjectRefNative* r, uint component, void* o, uint capacity, uint* required)
    {
        var (pr, po, preq) = ((nint)r, (nint)o, (nint)required);
        return Read(m => EcsReadsLogic.Snapshot(m, owner, (EcsObjectRefNative*)pr, component, (void*)po, capacity, (uint*)preq));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SnapshotMany(ulong owner, EcsObjectRefNative* r, EcsSnapshotSpecNative* specs, uint count, void* o, uint capacity,
        uint* required, uint* presence)
    {
        var (pr, ps, po, preq, ppr) = ((nint)r, (nint)specs, (nint)o, (nint)required, (nint)presence);
        return Read(m => EcsReadsLogic.SnapshotMany(m, owner, (EcsObjectRefNative*)pr, (EcsSnapshotSpecNative*)ps, count, (void*)po, capacity,
            (uint*)preq, (uint*)ppr));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TemplateUuid(ulong owner, EcsObjectRefNative* r, byte* guid)
    {
        var (pr, pg) = ((nint)r, (nint)guid);
        return Read(m => EcsReadsLogic.TemplateUuid(m, owner, (EcsObjectRefNative*)pr, (byte*)pg));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WritePolicy(uint kind, uint component, EcsWritePolicyNative* o)
    {
        var p = (nint)o;
        return Read(m => EcsWritesLogic.WritePolicy(m, kind, component, (EcsWritePolicyNative*)p));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CompareExchange(ulong owner, EcsObjectRefNative* r, uint component, void* expected, void* mask, void* desired, uint bytes,
        void* confirmed, MutationReplyNative* reply)
    {
        var (pr, pe, pm, pd, pc, prep) = ((nint)r, (nint)expected, (nint)mask, (nint)desired, (nint)confirmed, (nint)reply);
        return Read(m => EcsWritesLogic.CompareExchange(m, owner, (EcsObjectRefNative*)pr, component, (byte*)pe, (byte*)pm, (byte*)pd, bytes,
            (byte*)pc, (MutationReplyNative*)prep));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteSubscribe(ulong owner, delegate* unmanaged[Cdecl]<EcsWriteEventNative*, void*, void> fn, void* user, ulong* handle)
    {
        var (pf, pu, ph) = ((nint)fn, (nint)user, (nint)handle);
        return Read(m => EcsWritesLogic.Subscribe(m, owner, pf, pu, (ulong*)ph));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteUnsubscribe(ulong handle) => Read(m => EcsWritesLogic.Unsubscribe(m, handle));
}
