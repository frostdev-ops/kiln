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
        // the consumers built before v3: Kiln.Api's program table (v2) and Kiln.World's own (v1)
        SameLayout(abi, "KilnWorldApi", typeof(EcsWorldTable), (int)abi["v2Entries"]!);
        SameLayout(abi, "KilnWorldApi", typeof(WorldNative.Table), 24);
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
