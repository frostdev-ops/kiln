// SPDX-License-Identifier: MIT
// Blocks.RegisterKind (16d S1): who may register what, what a duplicate answers, and that a kind that is gone leaves nothing behind.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Kiln.BlockTests;

[Collection("block kinds")]
public sealed class KindRegistryTests
{
    [Fact]
    public void A_kind_registers_under_its_id_section_and_record_and_says_so_once()
    {
        var events = KindKit.Fresh();
        var (entry, _, _) = KindKit.Lantern();
        Assert.Same(entry, KindRegistry.FindSection("provider:lanterns"));
        Assert.Same(entry, KindRegistry.Find(typeof(LanternBlock)));
        Assert.Equal(1, KindRegistry.Count);
        var ev = Assert.IsType<BlockKindRegisteredEvent>(Assert.Single(events));
        Assert.Equal(("provider:lantern", "provider", false), (ev.KindId, ev.ProviderModId, ev.Removed));
    }

    [Fact]
    public void The_descriptor_is_copied()
    {
        KindKit.Fresh();
        var requires = new List<string> { "items.give" };
        var d = KindKit.Descriptor() with { Requires = requires };
        Assert.True(KindRegistry.Register(KindKit.Owner("provider"), d, new LanternKind()).Ok);
        requires.Add("later");
        var kept = KindRegistry.FindId("provider:lantern")!.Descriptor;
        Assert.NotSame(d, kept);
        Assert.Equal(["items.give"], kept.Requires);
    }

    [Fact]
    public void A_duplicate_id_section_or_record_is_refused_and_replaces_nothing()
    {
        KindKit.Fresh();
        var (first, _, _) = KindKit.Lantern();
        var other = KindKit.Owner("provider");
        var id = KindRegistry.Register(other, KindKit.Descriptor(section: "provider:others"), new OtherKind());
        Assert.False(id.Ok);
        Assert.StartsWith("AlreadyRegistered", id.Reason);
        Assert.Null(id.Registration);
        var section = KindRegistry.Register(other, KindKit.Descriptor(id: "provider:other"), new OtherKind());
        Assert.False(section.Ok);
        Assert.StartsWith("AlreadyRegistered", section.Reason);
        var record = KindRegistry.Register(other, KindKit.Descriptor("provider:lantern2", "provider:lanterns2"), new LanternKind());
        Assert.False(record.Ok);
        Assert.StartsWith("AlreadyRegistered", record.Reason);
        Assert.Same(first, KindRegistry.FindId("provider:lantern"));
        Assert.Null(KindRegistry.Find(typeof(OtherBlock)));
        Assert.Equal(1, KindRegistry.Count);
    }

    [Theory]
    [InlineData("other:lantern", "provider:lanterns")]
    [InlineData("provider:lantern", "other:lanterns")]
    [InlineData("lantern", "provider:lanterns")]
    [InlineData("provider:", "provider:lanterns")]
    [InlineData("provider:lan tern", "provider:lanterns")]
    [InlineData("provider:lantern", "provider:a:b")]
    public void An_id_or_section_that_is_not_the_registering_mods_throws(string id, string section)
    {
        KindKit.Fresh();
        Assert.Throws<ArgumentException>(() => KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor(id, section), new LanternKind()));
        Assert.Equal(0, KindRegistry.Count);
    }

    [Fact]
    public void A_block_Kiln_compiles_itself_cannot_be_a_kind()
    {
        KindKit.Fresh();
        Assert.Throws<ArgumentException>(() => KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor(), new BuiltInKind()));
        // every section's record is of the Kiln.Blocks assembly: the rule covers kinds other packages add
        Assert.All(BlockJson.KindTypes(), t => Assert.True(KindRegistry.IsBuiltIn(t), t.Name));
        Assert.Equal(0, KindRegistry.Count);
    }

    [Theory]
    [InlineData("""{"$ref":"https://example.org/x.json"}""")]
    [InlineData("""{"properties":{"a":{"$ref":"other.json#/x"}}}""")]
    [InlineData("""[1]""")]
    [InlineData("""{ not json""")]
    [InlineData("")]
    public void A_schema_that_is_not_local_plain_JSON_throws(string schema)
    {
        KindKit.Fresh();
        Assert.Throws<ArgumentException>(() => KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor() with { SchemaJson = schema }, new LanternKind()));
    }

    [Fact]
    public void The_schema_is_bounded_and_a_local_reference_is_taken()
    {
        KindKit.Fresh();
        var big = "{\"description\":\"" + new string('x', KindRegistry.MaxSchemaBytes) + "\"}";
        Assert.Throws<ArgumentException>(() => KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor() with { SchemaJson = big }, new LanternKind()));
        Assert.Throws<ArgumentException>(() => KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor() with { Format = 2 }, new LanternKind()));
        Assert.True(KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor() with { SchemaJson = """{"$defs":{"a":{}},"properties":{"a":{"$ref":"#/$defs/a"}}}""" }, new LanternKind()).Ok);
    }

    [Fact]
    public void Only_the_registering_instance_disposes_and_again_does_nothing()
    {
        var events = KindKit.Fresh();
        var (entry, _, provider) = KindKit.Lantern();
        Assert.Throws<ArgumentException>(() => KindRegistry.Dispose(entry, KindKit.Owner("provider")));   // the same id, another instance
        Assert.Throws<ArgumentException>(() => KindRegistry.Dispose(entry, KindKit.Owner("consumer")));
        Assert.Throws<ArgumentException>(() => KindRegistry.Dispose(entry, null));
        Assert.False(entry.Retired);
        KindRegistry.Dispose(entry, provider);
        KindRegistry.Dispose(entry, provider);
        Assert.True(entry.Retired);
        Assert.Null(KindRegistry.FindId("provider:lantern"));
        Assert.Null(KindRegistry.FindSection("provider:lanterns"));
        Assert.Null(KindRegistry.Find(typeof(LanternBlock)));
        Assert.Equal([false, true], events.Cast<BlockKindRegisteredEvent>().Select(e => e.Removed));
        // the names are free again
        Assert.True(KindRegistry.Register(KindKit.Owner("provider"), KindKit.Descriptor(), new LanternKind()).Ok);
    }

    public sealed record Many<T> : IBlock;

    public sealed class ManyKind<T> : IBlockKind<Many<T>>
    {
        public BlockPlan Compile(BlockCompileContext context, Many<T> b) => context.Plan("e", [context.Effect("e", new EffectBlock { Heal = 1 })]);
    }

    private static BlockKindResult RegisterMany(KindOwner owner, Type inner, int i)
    {
        var compiler = Activator.CreateInstance(typeof(ManyKind<>).MakeGenericType(inner))!;
        var register = typeof(IBlock).Assembly.GetType("Kiln.KindRegistry", true)!.GetMethod("Register", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(typeof(Many<>).MakeGenericType(inner));
        return (BlockKindResult)register.Invoke(null, [owner, KindKit.Descriptor($"provider:k{i}", $"provider:k{i}s"), compiler])!;
    }

    [Fact]
    public void The_limit_of_kinds_is_a_refusal()
    {
        KindKit.Fresh();
        var owner = KindKit.Owner("provider");
        var inner = typeof(int);
        for (var i = 0; i < KindRegistry.MaxKinds; i++)
        {
            Assert.True(RegisterMany(owner, inner, i).Ok);
            inner = typeof(Many<>).MakeGenericType(inner);
        }
        var over = RegisterMany(owner, inner, KindRegistry.MaxKinds);
        Assert.False(over.Ok);
        Assert.StartsWith("TooManyKinds", over.Reason);
        Assert.Equal(KindRegistry.MaxKinds, KindRegistry.Count);
        KindRegistry.Reset();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Compiler, KindEntry Entry) RegisterAndDispose()
    {
        var provider = KindKit.Owner("provider");
        var kind = new LanternKind();
        var r = KindRegistry.Register(provider, KindKit.Descriptor(), kind);
        var entry = KindRegistry.FindId("provider:lantern")!;
        KindRegistry.Dispose(entry, provider);
        return (new WeakReference(kind), entry);
    }

    [Fact]
    public void A_disposed_kind_holds_nothing_of_its_provider()
    {
        KindKit.Fresh();
        var (compiler, entry) = RegisterAndDispose();
        for (var i = 0; i < 5 && compiler.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(compiler.IsAlive);
        Assert.Null(entry.RecordType);
        Assert.Null(entry.Options);
        Assert.Null(entry.Compile);
    }

    // ---- JSON ---------------------------------------------------------------------------------------------------

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void An_external_block_reads_strictly_with_its_format_taken_off()
    {
        KindKit.Fresh();
        var (entry, _, _) = KindKit.Lantern();
        var b = Assert.IsType<LanternBlock>(KindJson.ReadRecord(entry, "camp", J("""{"format":1,"name":"Camp","seconds":600}""")));
        Assert.Equal(("Camp", 600d), (b.Name, b.Seconds));
        Assert.Equal("format", Assert.Throws<BlockError>(() => KindJson.ReadRecord(entry, "camp", J("""{"name":"Camp"}"""))).Field);
        Assert.Equal("format", Assert.Throws<BlockError>(() => KindJson.ReadRecord(entry, "camp", J("""{"format":2,"name":"Camp"}"""))).Field);
        Assert.Equal("format", Assert.Throws<BlockError>(() => KindJson.ReadRecord(entry, "camp", J("""{"format":"1","name":"Camp"}"""))).Field);
        Assert.Throws<BlockError>(() => KindJson.ReadRecord(entry, "camp", J("""{"format":1,"name":"Camp","colour":"red"}""")));
        Assert.Throws<BlockError>(() => KindJson.ReadRecord(entry, "camp", J("""[1]""")));
    }

    [Fact]
    public void A_retired_kinds_section_reads_nothing()
    {
        KindKit.Fresh();
        var (entry, _, provider) = KindKit.Lantern();
        KindRegistry.Dispose(entry, provider);
        Assert.Throws<BlockError>(() => KindJson.ReadRecord(entry, "camp", J("""{"format":1,"name":"Camp"}""")));
    }

    [Fact]
    public void A_data_file_naming_an_unregistered_kind_says_whose_it_is()
    {
        KindKit.Fresh();
        var dir = Directory.CreateTempSubdirectory("kiln-kinds-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "blocks"));
            File.WriteAllText(Path.Combine(dir, "blocks", "a.json"), """{"format":1,"provider:lanterns":{"camp":{"format":1,"name":"Camp","seconds":600}},"nonsense":{}}""");
            var mod = KindKit.Mod("kinds-data-a");
            BlockJson.Read(mod, dir);
            Assert.Empty(mod.Pending);
            Assert.Contains(mod.Errors, e => e.Contains("\"provider:lanterns\" is a block kind of mod provider"));
            Assert.Contains(mod.Errors, e => e.Contains("\"nonsense\" is not a kind of block"));
            mod.Data?.Dispose();

            KindKit.Lantern();
            var again = KindKit.Mod("kinds-data-b");
            BlockJson.Read(again, dir);
            Assert.True(again.Pending.ContainsKey("camp"));
            Assert.Equal(1ul, BlockJson.CountEntries(dir));
            again.Data?.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

[Collection("block kinds")]
public sealed class KindCapabilityTests
{
    [Fact]
    public void The_rows_follow_whether_blocks_compile_here_and_say_why_when_they_do_not()
    {
        var on = KindCapabilities.RowFor(true);
        Assert.Equal((CapabilityState.Available, CapabilityReason.None), (on.State, on.Reason));
        var off = KindCapabilities.RowFor(false);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceMissing), (off.State, off.Reason));
        Assert.False(string.IsNullOrEmpty(off.Text));
    }

    [Fact]
    public void A_row_that_is_off_throws_by_its_name_and_reports_both_rows()
    {
        var reported = new List<(string Name, CapabilityState State)>();
        var (was, report) = (KindCapabilities.BlocksAvailable, BlockCapabilities.Report);
        try
        {
            BlockCapabilities.Report = (name, row) => reported.Add((name, row.State));
            KindCapabilities.BlocksAvailable = () => false;
            KindCapabilities.Rearm();
            Assert.Equal("blocks.compile", Assert.Throws<KilnUnavailableException>(() => KindCapabilities.Require(KindCapabilities.Compile)).Capability);
            Assert.Contains(("blocks.kinds", CapabilityState.Unavailable), reported);
            Assert.Contains(("blocks.compile", CapabilityState.Unavailable), reported);
            KindCapabilities.BlocksAvailable = () => true;
            KindCapabilities.Rearm();
            KindCapabilities.Require(KindCapabilities.Kinds);
            Assert.Contains(("blocks.kinds", CapabilityState.Available), reported);
        }
        finally
        {
            (KindCapabilities.BlocksAvailable, BlockCapabilities.Report) = (was, report);
            KindCapabilities.Rearm();
        }
    }
}
