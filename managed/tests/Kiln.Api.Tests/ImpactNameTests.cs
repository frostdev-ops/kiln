// SPDX-License-Identifier: MIT
// GameType.ImpactName, GameTypes.ByImpactName and FindAllByImpactName over a fake kiln.types whose metadata gives each
// type a short, a qualified and an Impact name (kiln_types.h type_impact_name, since 0.2.0-dev.28): the three forms
// differ, a keen::ds:: mirror shares its runtime type's name, one entry is empty, one unreadable, one longer than the
// first buffer; the capability off, an older table and a slot that answers "unavailable" all throw naming the row.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.Api.Tests;

public sealed unsafe class ImpactNameTests : IDisposable
{
    const string Cap = "types.impact_names";
    const int Ok = 0, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;
    static readonly string LongName = "impact." + new string('x', 300);

    // (short, qualified, Impact name); null Impact = an unreadable entry
    static readonly (string Short, string Qualified, string? Impact)[] Rows =
    [
        ("Health", "keen::ecs::Health", "ecs.Health"),
        ("float32", "keen::float32", "float32"),
        ("Health", "keen::ds::ecs::Health", "ecs.Health"),
        ("Empty", "keen::Empty", ""),
        ("Broken", "keen::Broken", null),
        ("Long", "keen::Long", LongName),
        ("EntityId", "keen::ecs::EntityId", "ecs.EntityId"),
    ];

    sealed class Backend : IKilnBackend
    {
        public bool ImpactNames = true;
        public nint Types;
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => name == Cap && ImpactNames;
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) => name == "kiln.types" ? Types : 0;
    }

    static readonly List<nint> Allocations = [];
    static nint[] _handles = [];
    static (nint Ptr, nuint Len)[] _short = [], _qualified = [];
    static int _slotAnswer = -1;  // -1: answer from the rows; else this code
    readonly Backend _backend = new();
    readonly IKilnBackend _previous = Kiln.Backend.Current;

    public ImpactNameTests()
    {
        _handles = Rows.Select(_ => Alloc(1)).ToArray();
        _short = Rows.Select(r => Utf8(r.Short)).ToArray();
        _qualified = Rows.Select(r => Utf8(r.Qualified)).ToArray();
        _slotAnswer = -1;
        Publish(withImpactSlot: true);
        Kiln.Backend.Current = _backend;
    }

    public void Dispose()
    {
        var cache = (Dictionary<nint, GameType>)typeof(GameTypes).GetField("Cache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        lock (cache) foreach (var h in _handles) cache.Remove(h);
        GameTypes.ResetImpactIndex();
        Kiln.Backend.Current = _previous;
        foreach (var a in Allocations) Marshal.FreeHGlobal(a);
        Allocations.Clear();
    }

    static nint Alloc(int bytes)
    {
        var p = Marshal.AllocHGlobal(bytes);
        new Span<byte>((void*)p, bytes).Clear();
        Allocations.Add(p);
        return p;
    }

    static (nint, nuint) Utf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var p = Alloc(Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return (p, (nuint)bytes.Length);
    }

    void Publish(bool withImpactSlot)
    {
        var size = withImpactSlot ? sizeof(TypesNative.ImpactTable) : sizeof(TypesNative.Table);
        var t = (TypesNative.ImpactTable*)Alloc(sizeof(TypesNative.ImpactTable));
        t->Base.Header.Size = (uint)size;
        t->Base.Header.Version = 1;
        t->Base.TypeCount = &TypeCount;
        t->Base.TypeAt = &TypeAt;
        t->Base.TypeInfo = &TypeInfo;
        if (withImpactSlot) t->TypeImpactName = &TypeImpactName;
        GameTypes.ResetImpactIndex();
        _backend.Types = (nint)t;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static uint TypeCount() => (uint)_handles.Length;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void* TypeAt(uint i) => i < _handles.Length ? (void*)_handles[i] : null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int TypeInfo(void* handle, TypesNative.TypeInfo* info)
    {
        var i = Array.IndexOf(_handles, (nint)handle);
        if (i < 0) return NotFound;
        info->Index = (uint)i;
        info->Name = new NativeStr { Ptr = (byte*)_short[i].Ptr, Len = _short[i].Len };
        info->QualifiedName = new NativeStr { Ptr = (byte*)_qualified[i].Ptr, Len = _qualified[i].Len };
        info->TypeSize = 4;
        info->Alignment = 4;
        info->Kind = (byte)GameTypeKind.Struct;
        info->Hash = (uint)i + 1;
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int TypeImpactName(void* handle, byte* output, nuint cap, nuint* required)
    {
        if (required == null || (cap > 0 && output == null)) return 1;
        *required = 0;
        if (_slotAnswer >= 0) return _slotAnswer;
        var i = Array.IndexOf(_handles, (nint)handle);
        if (i < 0 || Rows[i].Impact is not { } name) return NotFound;
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        *required = (nuint)bytes.Length;
        if (cap < (nuint)bytes.Length) return BufferTooSmall;
        bytes.CopyTo(new Span<byte>(output, bytes.Length));
        return Ok;
    }

    static GameType Type(string qualified) => GameTypes.All.Single(t => t.QualifiedName == qualified);

    [Fact]
    public void The_Impact_name_is_neither_the_short_nor_the_qualified_name_and_round_trips()
    {
        var id = Type("keen::ecs::EntityId");
        Assert.Equal("ecs.EntityId", id.ImpactName);
        Assert.NotEqual(id.Name, id.ImpactName);
        Assert.NotEqual(id.QualifiedName, id.ImpactName);
        Assert.Same(id, GameTypes.ByImpactName("ecs.EntityId"));
        Assert.Equal([id], GameTypes.FindAllByImpactName(id.ImpactName!));
        // a name equal to the short name is still found by its Impact name only
        Assert.Same(Type("keen::float32"), GameTypes.ByImpactName("float32"));
    }

    [Fact]
    public void Short_and_qualified_names_never_alias_and_the_lookup_is_ordinal()
    {
        Assert.Null(GameTypes.ByImpactName("EntityId"));
        Assert.Null(GameTypes.ByImpactName("keen::ecs::EntityId"));
        Assert.Null(GameTypes.ByImpactName("ECS.EntityId"));
        Assert.Null(GameTypes.ByImpactName("ecs.EntityId "));
        Assert.Empty(GameTypes.FindAllByImpactName("no.such.Name"));
    }

    [Fact]
    public void A_shared_name_is_ambiguous_for_the_single_lookup_and_FindAll_lists_both_in_the_game_s_order()
    {
        Assert.Null(GameTypes.ByImpactName("ecs.Health"));
        Assert.Equal(["keen::ecs::Health", "keen::ds::ecs::Health"], GameTypes.FindAllByImpactName("ecs.Health").Select(t => t.QualifiedName));
    }

    [Fact]
    public void An_empty_or_unreadable_entry_has_no_Impact_name_and_a_long_one_reads_whole()
    {
        Assert.Null(Type("keen::Empty").ImpactName);
        Assert.Null(Type("keen::Broken").ImpactName);
        Assert.Equal(LongName, Type("keen::Long").ImpactName);
        Assert.Same(Type("keen::Long"), GameTypes.ByImpactName(LongName));
    }

    [Fact]
    public void A_null_or_empty_name_is_a_bad_argument()
    {
        Assert.Throws<ArgumentNullException>(() => GameTypes.ByImpactName(null!));
        Assert.Throws<ArgumentException>(() => GameTypes.ByImpactName(""));
        Assert.Throws<ArgumentException>(() => GameTypes.FindAllByImpactName(""));
    }

    [Fact]
    public void With_the_capability_off_every_member_throws_naming_it()
    {
        var health = Type("keen::ecs::Health");
        _backend.ImpactNames = false;
        Assert.Equal(Cap, Assert.Throws<KilnUnavailableException>(() => health.ImpactName).Capability);
        Assert.Equal(Cap, Assert.Throws<KilnUnavailableException>(() => GameTypes.ByImpactName("ecs.Health")).Capability);
        Assert.Equal(Cap, Assert.Throws<KilnUnavailableException>(() => GameTypes.FindAllByImpactName("ecs.Health")).Capability);
    }

    [Fact]
    public void An_older_kiln_types_without_the_slot_turns_the_names_off_and_leaves_the_rest_working()
    {
        Publish(withImpactSlot: false);
        var e = Assert.Throws<KilnUnavailableException>(() => GameTypes.ByImpactName("ecs.Health"));
        Assert.Equal(Cap, e.Capability);
        Assert.Contains("older than 0.2.0-dev.28", e.Message, StringComparison.Ordinal);
        Assert.Equal(Rows.Length, GameTypes.Count);
        Assert.Equal(CapabilityState.Available, Capabilities.Get("kiln.types").State);
    }

    [Fact]
    public void A_slot_that_answers_unavailable_throws_naming_the_row()
    {
        _slotAnswer = Unavailable;
        Assert.Equal(Cap, Assert.Throws<KilnUnavailableException>(() => GameTypes.ByImpactName("ecs.Health")).Capability);
    }
}
