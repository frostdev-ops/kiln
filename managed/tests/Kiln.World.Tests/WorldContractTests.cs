// SPDX-License-Identifier: MIT
// What kiln.world v3 promises the code built against it: the managed mirrors match the header field for field
// (native/services/world/tests/abi.json, which the native host test checks the header against), a consumer built for
// v2 (Kiln.Api's EcsPrograms table, Kiln.World's v1 table) keeps working on the v3 table and the v3 parts stay off on a
// v2 one, and every member added in dev.27 throws KilnUnavailableException naming its capability when kiln.world is
// not there.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.WorldTests;

/// <summary>The tests that put a backend in place (Backend.Current is the process's): never in parallel with each other.</summary>
[CollectionDefinition("kiln.world backend", DisableParallelization = true)]
public sealed class WorldBackendCollection;

[Collection("kiln.world backend")]
public sealed unsafe class WorldContractTests
{
    // ---- field for field ----

    private static JsonNode Abi([CallerFilePath] string self = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "native", "services", "world"))) { repo = dir; break; }
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "native", "services", "world", "tests", "abi.json")))!;
    }

    // The instance fields of a mirror in declaration order with their offsets; the table prefixes Kiln's mirrors embed
    // (one field standing for many native entries) are opened up.
    internal static List<(string Name, int Offset)> Flat(Type type, int at = 0)
    {
        var list = new List<(string, int)>();
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.MetadataToken))
        {
            var offset = at + (int)Marshal.OffsetOf(type, f.Name);
            if (f.FieldType == typeof(WorldNative.Table) || f.FieldType == typeof(WorldPrefix)) list.AddRange(Flat(f.FieldType, offset));
            else list.Add((f.Name, offset));
        }
        return list;
    }

    internal static void SameLayout(JsonNode abi, string native, Type mirror, int entries = -1)
    {
        var fields = abi[native]!["fields"]!.AsArray().Select(f => ((string)f![0]!, (int)f[1]!)).ToList();
        var mine = Flat(mirror);
        if (entries < 0)
        {
            Assert.Equal((int)abi[native]!["size"]!, Marshal.SizeOf(mirror));
            entries = fields.Count;
        }
        else Assert.Equal(fields[entries].Item2, Marshal.SizeOf(mirror));   // a prefix ends where the next native entry starts
        Assert.Equal(entries, mine.Count);
        for (var i = 0; i < entries; i++)
            Assert.True(fields[i].Item2 == mine[i].Offset, $"{mirror.Name}.{mine[i].Name} is at {mine[i].Offset}, {native}.{fields[i].Item1} at {fields[i].Item2}");
    }

    [Fact]
    public void The_managed_mirrors_match_the_header_field_for_field()
    {
        var abi = Abi();
        SameLayout(abi, "KilnWorldApi", typeof(WorldWritesTable));
        SameLayout(abi, "KilnWorldWriteRequest", typeof(WriteRequestNative));
        SameLayout(abi, "KilnWorldWriteResult", typeof(WriteResultNative));
        SameLayout(abi, "KilnWorldWriteInfo", typeof(WriteInfoNative));
        SameLayout(abi, "KilnWorldWriteStatus", typeof(WriteStatusNative));
        SameLayout(abi, "KilnObjectRef", typeof(EcsObjectRefNative));
        SameLayout(abi, "KilnObjectInfo", typeof(EcsObjectInfoNative));
        SameLayout(abi, "KilnEcsContextInfo", typeof(EcsContextInfoNative));
        SameLayout(abi, "KilnEcsQueryRequest", typeof(EcsQueryRequestNative));
        SameLayout(abi, "KilnEcsSnapshotSpec", typeof(EcsSnapshotSpecNative));
        // the consumers built before v3: Kiln.Api's program table (v2) and Kiln.World's own (v1)
        SameLayout(abi, "KilnWorldApi", typeof(EcsWorldTable), (int)abi["v2Entries"]!);
        SameLayout(abi, "KilnWorldApi", typeof(WorldNative.Table), 24);
    }

    [Fact]
    public void WorldConcurrents_table_mirror_puts_the_read_members_and_capabilities_where_the_header_does()
    {
        var abi = Abi();
        Assert.Equal((int)abi["KilnWorldApiEcsReads"]!["size"]!, sizeof(EcsReadsTable));
        var members = abi["KilnWorldApiEcsReads"]!["fields"]!.AsArray().Select(f => ((string)f![0]!, (int)f[1]!)).ToList();
        string[] mine = [nameof(EcsReadsTable.EntityOpen), nameof(EcsReadsTable.EntityValidate), nameof(EcsReadsTable.ContextSnapshot),
            nameof(EcsReadsTable.Query), nameof(EcsReadsTable.Snapshot), nameof(EcsReadsTable.SnapshotMany), nameof(EcsReadsTable.TemplateUuid)];
        Assert.Equal(members.Count, mine.Length);
        for (var i = 0; i < mine.Length; i++)
            Assert.True(members[i].Item2 == (int)Marshal.OffsetOf<EcsReadsTable>(mine[i]), $"{mine[i]} is not at {members[i].Item1}'s {members[i].Item2}");
        var capabilities = abi["KilnWorldApi"]!["fields"]!.AsArray().Single(f => (string)f![0]! == "capabilities")![1]!;
        Assert.Equal((int)capabilities, (int)Marshal.OffsetOf<EcsReadsTable>(nameof(EcsReadsTable.V3)) +
            (int)Marshal.OffsetOf<WorldWritesTable>(nameof(WorldWritesTable.Capabilities)));
    }

    [Fact]
    public void The_write_mirrors_put_the_members_and_records_where_the_header_does()
    {
        var abi = Abi();
        Assert.Equal((int)abi["KilnWorldApiEcsWrites"]!["size"]!, sizeof(EcsWritesTable));
        Assert.Equal((int)abi["KilnWorldApiEcsReads"]!["size"]!, (int)Marshal.OffsetOf<EcsWritesTable>(nameof(EcsWritesTable.WritePolicy)));
        var members = abi["KilnWorldApiEcsWrites"]!["fields"]!.AsArray().Select(f => ((string)f![0]!, (int)f[1]!)).ToList();
        string[] mine = [nameof(EcsWritesTable.WritePolicy), nameof(EcsWritesTable.CompareExchange), nameof(EcsWritesTable.WriteSubscribe),
            nameof(EcsWritesTable.WriteUnsubscribe)];
        Assert.Equal(members.Count, mine.Length);
        for (var i = 0; i < mine.Length; i++)
            Assert.True(members[i].Item2 == (int)Marshal.OffsetOf<EcsWritesTable>(mine[i]), $"{mine[i]} is not at {members[i].Item1}'s {members[i].Item2}");
        SameLayout(abi, "KilnEcsWritePolicy", typeof(EcsWritePolicyNative));
        SameLayout(abi, "KilnMutationReply", typeof(MutationReplyNative));
        SameLayout(abi, "KilnEcsWriteEvent", typeof(EcsWriteEventNative));
    }

#pragma warning disable KILNX001  // the public enums mirror the header's values
    [Fact]
    public void The_ecs_write_constants_match_the_header()
    {
        var mine = new Dictionary<string, long>
        {
            ["KILN_ECS_CONFLICT"] = EcsWriteConstants.Conflict, ["KILN_ECS_NOT_WRITABLE"] = EcsWriteConstants.NotWritable,
            ["KILN_ECS_UNCERTAIN"] = EcsWriteConstants.Uncertain, ["KILN_ECS_ERR_CONFLICT"] = EcsWriteConstants.ErrConflict,
            ["KILN_ECS_ERR_NOT_WRITABLE"] = EcsWriteConstants.ErrNotWritable, ["KILN_ECS_ERR_UNCERTAIN"] = EcsWriteConstants.ErrUncertain,
            ["KILN_WORLD_CAP_ECS_WRITE"] = EcsWriteConstants.CapEcsWrite,
            ["KILN_ECS_WRITE_POLICY_WRITABLE"] = EcsWriteConstants.PolicyWritable,
            ["KILN_ECS_WRITE_POLICY_TRACKING_KNOWN"] = EcsWriteConstants.PolicyTrackingKnown,
            ["KILN_ECS_WRITE_POLICY_TRACKED_A"] = EcsWriteConstants.PolicyTrackedA,
            ["KILN_ECS_WRITE_POLICY_TRACKED_B"] = EcsWriteConstants.PolicyTrackedB,
            ["KILN_ECS_WRITE_POLICY_PLAIN"] = EcsWriteConstants.PolicyPlain,
            ["KILN_ECS_WRITE_POLICY_OVER_BUDGET"] = EcsWriteConstants.PolicyOverBudget,
        };
        foreach (ExchangeFlags f in Enum.GetValues<ExchangeFlags>())
            if (f != ExchangeFlags.None) mine["KILN_MUTATION_" + Snake(f.ToString())] = (long)f;
        foreach (WriteReason r in Enum.GetValues<WriteReason>()) mine["KILN_ECS_WRITE_WHY_" + Snake(r.ToString())] = (long)r;
        var listed = Abi()["KilnEcsWriteConstants"]!["values"]!.AsArray().ToDictionary(v => (string)v![0]!, v => (long)v![1]!);
        Assert.Equal(listed.OrderBy(p => p.Key), mine.OrderBy(p => p.Key));
        // ExchangeStatus is the native statuses a write can give
        Assert.Equal([0L, 1, 2, 3, 4, 5, 7, 8, 9], Enum.GetValues<ExchangeStatus>().Select(v => (long)v).Order());
        Assert.Equal(EcsWriteConstants.Conflict, (uint)ExchangeStatus.Conflict);
        Assert.Equal(EcsWriteConstants.NotWritable, (uint)ExchangeStatus.NotWritable);
        Assert.Equal(EcsWriteConstants.Uncertain, (uint)ExchangeStatus.Uncertain);
    }
#pragma warning restore KILNX001

    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToUpperInvariant();

    [Fact]
    public void The_ecs_read_constants_match_the_header()
    {
        var mine = new Dictionary<string, long>
        {
            ["KILN_ECS_OK"] = EcsReadConstants.Ok, ["KILN_ECS_ABSENT"] = EcsReadConstants.Absent,
            ["KILN_ECS_STALE"] = EcsReadConstants.Stale, ["KILN_ECS_UNREADABLE"] = EcsReadConstants.Unreadable,
            ["KILN_ECS_UNAVAILABLE"] = EcsReadConstants.Unavailable, ["KILN_ECS_BUSY"] = EcsReadConstants.Busy,
            ["KILN_ECS_UNSUPPORTED"] = EcsReadConstants.Unsupported, ["KILN_WORLD_ERR_CHANGED"] = EcsReadConstants.ErrChanged,
            ["KILN_ECS_ERR_STALE"] = EcsReadConstants.ErrStale, ["KILN_ECS_ERR_UNREADABLE"] = EcsReadConstants.ErrUnreadable,
            ["KILN_ECS_ERR_BUSY"] = EcsReadConstants.ErrBusy, ["KILN_ECS_ERR_UNSUPPORTED"] = EcsReadConstants.ErrUnsupported,
            ["KILN_ECS_QUERY_BOUNDS"] = EcsReadConstants.QueryBounds,
            ["KILN_ECS_QUERY_FRAME_QUIET"] = EcsReadConstants.QueryFrameQuiet,
            ["KILN_ECS_READ_FRAME_QUIET"] = EcsReadConstants.ReadFrameQuiet,
            ["KILN_OBJECT_ALIVE"] = EcsReadConstants.ObjectAlive, ["KILN_OBJECT_HAS_TEMPLATE"] = EcsReadConstants.ObjectHasTemplate,
            ["KILN_WORLD_CAP_ECS_READ"] = EcsReadConstants.CapEcsRead,
        };
        var listed = Abi()["KilnEcsReadConstants"]!["values"]!.AsArray().ToDictionary(v => (string)v![0]!, v => (long)v![1]!);
        Assert.Equal(listed.OrderBy(p => p.Key), mine.OrderBy(p => p.Key));
    }

    // ---- a v2 consumer on the v3 table ----

    private sealed class TableBackend(nint world) : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) =>
            name == WorldNative.Name && world != 0 && ((ServiceHeader*)world)->Version >= minVersion ? world : 0;
    }

    private static void ResetWorld()
    {
        WorldNative.Reset();
        WorldWrites.Reset();
        WorldCapabilities.Reset();
    }

    private static T With<T>(nint table, Func<T> body)
    {
        var old = Backend.Current;
        Backend.Current = new TableBackend(table);
        ResetWorld();
        try { return body(); }
        finally
        {
            Backend.Current = old;
            ResetWorld();
        }
    }

    private static int _statusCalls, _capabilityCalls;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint CapabilitiesStub()
    {
        _capabilityCalls++;
        return 0x7 | WorldWritesAbi.CapTrackedWrites;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StatusStub(WorldNative.Status* status)
    {
        _statusCalls++;
        return WorldNative.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteStatusStub(WriteStatusNative* s)
    {
        s->Program = WorldWritesAbi.ProgramActive;
        s->Declared = 1;
        return WorldNative.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SubmitWrongThread(ulong owner, WriteRequestNative* request, ulong* ticket) => WorldNative.WrongThread;

    [Fact]
    public void A_write_asked_for_inside_a_program_s_update_is_a_thread_error()
    {
        var v3 = Table((uint)sizeof(WorldWritesTable), 3);
        try
        {
            ((WorldWritesTable*)v3)->WriteSubmit = &SubmitWrongThread;
            With(v3, () =>
            {
                Assert.True(World.SupportsTrackedWrites);
                var e = Assert.Throws<InvalidOperationException>(() => { _ = World.WriteAt(new EntityId(5), "Fuel", 8, 1); });
                Assert.Contains("between two steps", e.Message);
                return 0;
            });
        }
        finally { NativeMemory.Free((void*)v3); }
    }

    // A v3 table as kiln.world publishes it, with the entries these tests call; `size`/`version` make it look older.
    private static nint Table(uint size, uint version)
    {
        var t = (WorldWritesTable*)NativeMemory.AllocZeroed((nuint)sizeof(WorldWritesTable));
        t->V1.Header = new ServiceHeader { Size = size, Version = version };
        t->V1.GetStatus = &StatusStub;
        t->Capabilities = &CapabilitiesStub;
        t->WriteStatus = &WriteStatusStub;
        return (nint)t;
    }

    [Fact]
    public void A_consumer_built_for_v2_works_on_the_v3_table()
    {
        var v3 = Table((uint)sizeof(WorldWritesTable), 3);
        try
        {
            With(v3, () =>
            {
                // Kiln.Api's EcsPrograms asks for v2 and checks the size of its own (v2) struct: the v3 table passes
                var programs = Services.Get<EcsWorldTable>(WorldNative.Name, 2);
                Assert.True(programs != null);
                _capabilityCalls = 0;
                Assert.Equal(0xFu, programs->Capabilities());   // its capabilities entry is v3's
                Assert.Equal(1, _capabilityCalls);
                // Kiln.World's v1 view: the status entry is where it was
                Assert.True(WorldNative.T != null);
                _statusCalls = 0;
                WorldNative.Status s;
                s.Size = (uint)sizeof(WorldNative.Status);
                Assert.Equal(WorldNative.Ok, WorldNative.T->GetStatus(&s));
                Assert.Equal(1, _statusCalls);
                // and the v3 part is there
                Assert.True(WorldWrites.T != null);
                Assert.True(World.SupportsTrackedWrites);
                return 0;
            });
        }
        finally { NativeMemory.Free((void*)v3); }
    }

    [Fact]
    public void On_a_v2_table_the_v2_consumers_work_and_tracked_writes_say_the_service_is_older()
    {
        var v2 = Table((uint)sizeof(EcsWorldTable), 2);
        try
        {
            With(v2, () =>
            {
                Assert.True(Services.Get<EcsWorldTable>(WorldNative.Name, 2) != null);
                Assert.True(WorldNative.T != null);
                Assert.True(WorldWrites.T == null);   // never reads past the v2 table's end
                Assert.False(World.SupportsTrackedWrites);
                Assert.Contains("kiln.world v2 has no tracked writes", Capabilities.Why(World.TrackedWritesCapability));
                var e = Assert.Throws<KilnUnavailableException>(() => { _ = World.WriteAt(new EntityId(5), "Fuel", 8, 1); });
                Assert.Equal(World.TrackedWritesCapability, e.Capability);
                Assert.Equal(WorldWriteState.Off, World.WriteInfo("Fuel").State);
                return 0;
            });
        }
        finally { NativeMemory.Free((void*)v2); }
    }

    // ---- the exception contract of what dev.27 added ----

    [Fact]
    public void Without_kiln_world_the_new_members_throw_KilnUnavailableException_naming_their_capability()
    {
        With<int>(0, () =>
        {
            var e = new EntityId(42);
            Assert.Equal("world.find", Assert.Throws<KilnUnavailableException>(() => World.Query("Fuel")).Capability);
            Assert.Equal("world.find", Assert.Throws<KilnUnavailableException>(() => World.Query(["Fuel"], ["Health"])).Capability);
            Assert.Equal("world.find", Assert.Throws<KilnUnavailableException>(() => World.Query<Ecs.Fuel>()).Capability);
            Assert.Equal("world.find", Assert.Throws<KilnUnavailableException>(() => World.Query<Ecs.Fuel, Ecs.ScaleComponent>()).Capability);
            foreach (Func<object> call in (Func<object>[])[
                () => World.Write(e, new Ecs.ScaleComponent()),
                () => World.Update<Ecs.Fuel>(e, (ref Ecs.Fuel f) => { }),
                () => World.WriteField(e, "Fuel", "dataStorage[0]", 1),
                () => World.WriteAt(e, "Fuel", 8, 1),
            ])
                Assert.Equal(World.TrackedWritesCapability, Assert.Throws<KilnUnavailableException>(call).Capability);
            // a bad argument is an ArgumentException whatever is available; the old Find keeps its own exception
            Assert.Throws<ArgumentException>(() => World.Query());
            Assert.Throws<ArgumentException>(() => World.Query([""], null));
            Assert.Throws<ArgumentException>(() => { _ = World.WriteAt(EntityId.None, "Fuel", 8, 1); });
            Assert.Throws<ArgumentException>(() => { _ = World.WriteAt(e, "Fuel", -1, 1); });
            Assert.IsNotType<KilnUnavailableException>(Assert.ThrowsAny<NotSupportedException>(() => World.Find("Fuel")));
            // reads that answer instead of throwing
            Assert.False(World.SupportsTrackedWrites);
            Assert.Equal(WorldWriteState.Off, World.WriteInfo("Fuel").State);
            Assert.Equal(WorldWriteState.Off, World.WriteInfo<Ecs.Fuel>().State);
            return 0;
        });
    }
}
