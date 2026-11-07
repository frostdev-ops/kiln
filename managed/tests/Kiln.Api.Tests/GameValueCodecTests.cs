// SPDX-License-Identifier: MIT
// GameValueCodec: the checked scalar reads and writes behind Reflected components, GameEvents.Post and kiln-set; and
// the fail-soft face of GameTypes / GameSystems without kiln.types.
using Kiln;
using Xunit;
using System.Runtime.InteropServices;

public class GameValueCodecTests
{
    private static readonly (string, long)[] Rarity = [("Common", 0), ("Uncommon", 1), ("Rare", 2), ("Epic", 3), ("Legendary", 4)];

    [Fact]
    public void ReadsSignedAndUnsignedWidths()
    {
        Assert.Equal(-1L, GameValueCodec.Read([0xff], GameTypeKind.S8, 1));
        Assert.Equal(255L, GameValueCodec.Read([0xff], GameTypeKind.U8, 1));
        Assert.Equal(-2L, GameValueCodec.Read([0xfe, 0xff, 0xff, 0xff], GameTypeKind.S32, 4));
        Assert.Equal(ulong.MaxValue, GameValueCodec.Read([0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff], GameTypeKind.U64, 8));
        Assert.Equal(true, GameValueCodec.Read([1], GameTypeKind.Bool, 1));
        Assert.Equal(1.5, (double)GameValueCodec.Read(BitConverter.GetBytes(1.5f), GameTypeKind.F32, 4), 6);
    }

    [Fact]
    public void WritesWithinRange()
    {
        var b = new byte[4];
        GameValueCodec.Write(b, GameTypeKind.S32, 4, -20);
        Assert.Equal(-20L, GameValueCodec.Read(b, GameTypeKind.S32, 4));
        GameValueCodec.Write(b, GameTypeKind.U16, 2, 65535);
        Assert.Equal(65535L, GameValueCodec.Read(b, GameTypeKind.U16, 2));
        GameValueCodec.Write(b, GameTypeKind.F32, 4, 2.25);
        Assert.Equal(2.25, (double)GameValueCodec.Read(b, GameTypeKind.F32, 4), 6);
        GameValueCodec.Write(b, GameTypeKind.S32, 4, 7.0);  // a whole double is a whole number
        Assert.Equal(7L, GameValueCodec.Read(b, GameTypeKind.S32, 4));
    }

    [Fact]
    public void RefusesWhatDoesNotFitAndWritesNothing()
    {
        var b = new byte[] { 9, 9, 9, 9 };
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.S32, 4, 1L << 40, field: "hp"));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.U8, 1, -1));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.U8, 1, 256));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.S32, 4, 1.5));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.Bool, 1, 2));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.F32, 4, 1e300));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.S32, 4, "many"));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, b);
    }

    [Fact]
    public void EnumsTakeTheirOwnValuesByNameOrNumber()
    {
        var b = new byte[1];
        GameValueCodec.Write(b, GameTypeKind.Enum, 1, "legendary", Rarity);
        Assert.Equal(4L, GameValueCodec.Read(b, GameTypeKind.Enum, 1));
        GameValueCodec.Write(b, GameTypeKind.Enum, 1, 2, Rarity);
        Assert.Equal("Rare (2)", GameValueCodec.Format(GameValueCodec.Read(b, GameTypeKind.Enum, 1), GameTypeKind.Enum, Rarity));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.Enum, 1, 9, Rarity));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(b, GameTypeKind.Enum, 1, "Mythic", Rarity));
        GameValueCodec.Write(b, GameTypeKind.Enum, 1, 9, []);  // an enum that lists no values takes any that fits
    }

    [Fact]
    public void DeclaredSignedAndUnsignedEnumsRoundTripTheirOwnValues()
    {
        var bytes = new byte[4];
        var signed = new (string, long)[] { ("Missing", -1), ("Present", 1) };
        GameValueCodec.Write(bytes, GameTypeKind.Enum, 4, "Missing", signed);
        var negative = GameValueCodec.Read(bytes, GameTypeKind.Enum, 4, signed);
        Assert.Equal(-1L, negative);
        Assert.Equal("Missing (-1)", GameValueCodec.Format(negative, GameTypeKind.Enum, signed));
        var unsigned = new (string, long)[] { ("Maximum", uint.MaxValue) };
        GameValueCodec.Write(bytes, GameTypeKind.Enum, 4, uint.MaxValue, unsigned);
        Assert.Equal((long)uint.MaxValue, GameValueCodec.Read(bytes, GameTypeKind.Enum, 4, unsigned));
        Assert.Equal((long)uint.MaxValue, GameValueCodec.Read(bytes, GameTypeKind.Enum, 4));
        var small = new (string, long)[] { ("Missing", -1) };
        GameValueCodec.Write(bytes, GameTypeKind.Enum, 1, -1, small);
        Assert.Equal(-1L, GameValueCodec.Read(bytes, GameTypeKind.Enum, 1, small));
    }

    [Fact]
    public void FormatsBitmasksInHex()
    {
        Assert.Equal("0x81", GameValueCodec.Format(0x81L, GameTypeKind.Bitmask8));
        Assert.Equal("false", GameValueCodec.Format(false, GameTypeKind.Bool));
    }

    [Fact]
    public void WithoutKilnTypesEverythingIsOffNotBroken()
    {
        Assert.False(GameTypes.Available);
        Assert.Null(GameTypes.Find("Health"));
        Assert.Empty(GameTypes.All);
        Assert.Empty(GameTypes.Components);
        Assert.False(GameSystems.Available);
        Assert.Empty(GameSystems.All);
        Assert.Null(GameSystems.Find("durability_loss"));
        Assert.Throws<ArgumentException>(() => GameSystems.SetEnabled("durability_loss", false));
        Assert.Throws<NotSupportedException>(() => GameSystems.Profile(true));
        Assert.Equal("kiln.types is not running", GameTypes.Status);
    }
}

public sealed class ReflectedSafetyTests : IDisposable
{
    private readonly Kiln.IKilnBackend _previous = Kiln.Backend.Current;

    public void Dispose() => Kiln.Backend.Current = _previous;

    [Theory]
    [InlineData(GameTypeKind.Bool, 0)]
    [InlineData(GameTypeKind.Bool, 8)]
    [InlineData(GameTypeKind.F32, 8)]
    [InlineData(GameTypeKind.U64, 1)]
    [InlineData(GameTypeKind.S8, 8)]
    [InlineData(GameTypeKind.Enum, 3)]
    public void InvalidScalarWidthsCannotReadOrWriteAdjacentBytes(GameTypeKind kind, int size)
    {
        var bytes = Enumerable.Repeat((byte)0x55, 16).ToArray();
        Assert.Throws<ArgumentException>(() => GameValueCodec.Read(bytes, kind, size));
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(bytes, kind, size, 1));
        Assert.All(bytes, b => Assert.Equal((byte)0x55, b));
        Assert.False(GameValueCodec.Fits(1, kind, size));
    }

    [Fact]
    public void NonFiniteAndRoundedIntegerOverflowsAreRefusedWithoutWrites()
    {
        var bytes = Enumerable.Repeat((byte)0x55, 8).ToArray();
        foreach (var value in new object[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, float.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => GameValueCodec.Write(bytes, GameTypeKind.F32, 4, value));
            Assert.Throws<ArgumentException>(() => GameValueCodec.Write(bytes, GameTypeKind.F64, 8, value));
            Assert.Throws<ArgumentException>(() => GameValueCodec.Write(bytes, GameTypeKind.S64, 8, value));
        }
        foreach (var value in new object[] { 9223372036854775808.0, float.MaxValue, -float.MaxValue })
            Assert.Throws<ArgumentException>(() => GameValueCodec.Write(bytes, GameTypeKind.S64, 8, value));
        Assert.All(bytes, b => Assert.Equal((byte)0x55, b));
    }

    [Fact]
    public void FullUnsignedRangeAndFitsRespectTheKind()
    {
        var bytes = new byte[8];
        GameValueCodec.Write(bytes, GameTypeKind.U64, 8, ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ulong.MaxValue, GameValueCodec.Read(bytes, GameTypeKind.U64, 8));
        Assert.False(GameValueCodec.Fits(2, GameTypeKind.Bool, 1));
        Assert.True(GameValueCodec.Fits(-20, GameTypeKind.F32, 4));
    }

    [Fact]
    public void EnumNumericStringsAreCheckedLikeNumbers()
    {
        var bytes = new byte[1];
        GameValueCodec.Write(bytes, GameTypeKind.Enum, 1, "2", [("Two", 2)]);
        Assert.Equal((byte)2, bytes[0]);
        Assert.Throws<ArgumentException>(() => GameValueCodec.Write(bytes, GameTypeKind.Enum, 1, "3", [("Two", 2)]));
    }

    [Fact]
    public void MissingServicesAndNullInputsFailSoft()
    {
        Kiln.Backend.Current = new ReflectionBackend();
        Assert.Null(GameTypes.Find(null!));
        Assert.Empty(GameTypes.Search(null!));
        Assert.Null(GameSystems.Find(null!));
        Assert.Empty(GameSystems.Search(null!));
        Assert.Null(Reflected.Component(new EntityId(1), "Health"));
        Assert.Empty(Reflected.All("Health"));
        Assert.Equal(-1, GameEvents.Each("MissingEvent", _ => { }));
        Assert.Throws<ArgumentNullException>(() => GameEvents.Each("MissingEvent", null!));
        Assert.Throws<NotSupportedException>(() => GameEvents.Post("MissingEvent", new Dictionary<string, object?>()));
        Assert.Throws<ArgumentNullException>(() => GameEvents.Post("MissingEvent", null!));
    }

    [Fact]
    public void LiveApisCheckTickThreadBeforeLookingUpServices()
    {
        Kiln.Backend.Current = new ReflectionBackend { TickThread = false };
        Assert.Throws<InvalidOperationException>(() => Reflected.Component(new EntityId(1), "Health"));
        Assert.Throws<InvalidOperationException>(() => Reflected.All("Health"));
        Assert.Throws<InvalidOperationException>(() => GameEvents.Each("MissingEvent", _ => { }));
        Assert.Throws<InvalidOperationException>(() => GameEvents.Post("MissingEvent", new Dictionary<string, object?>()));
        Assert.Throws<InvalidOperationException>(() => GameSystems.SetEnabled("missing", false));
        Assert.Throws<InvalidOperationException>(() => GameSystems.TurnAllOn());
        Assert.Throws<InvalidOperationException>(() => GameSystems.Profile(true));
    }

    [Fact]
    public void LiveApisAdvertiseAnalyzerThreadRequirements()
    {
        foreach (var name in new[] { nameof(GameSystems.SetEnabled), nameof(GameSystems.TurnAllOn), nameof(GameSystems.Profile) })
            Assert.NotNull(Attribute.GetCustomAttribute(typeof(GameSystems).GetMethod(name)!, typeof(TickThreadOnlyAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(Reflected), typeof(TickThreadOnlyAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(GameEvents), typeof(TickThreadOnlyAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(ReflectedValue).GetProperty("Item")!, typeof(TickThreadOnlyAttribute)));
    }

    [Fact]
    public void DefaultBytesAreOwnedAndRemainValidAfterTheNativeServiceUnloads()
    {
        GameType type;
        using (var native = new NativeReflection())
        {
            var handle = native.Add("Defaults", GameTypeKind.Struct, 4);
            var pointer = native.SetDefaults(handle, [1, 2, 3, 4]);
            type = GameTypes.Find("Defaults")!;
            Marshal.WriteInt32(pointer, 0);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, type.Defaults);
            type.Defaults![0] = 99;
        }
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, type.Defaults);
    }

    [Fact]
    public void ComponentUsesActualByteLengthBeforeReadsAndWrites()
    {
        using var native = new NativeReflection();
        var scalar = native.Add("i64", GameTypeKind.S64, 8);
        var type = native.Add("Health", GameTypeKind.Struct, 8, fields: [("value", scalar, 0)]);
        native.ComponentBytes = 4;
        var value = ReflectedValue.OfComponent(1, GameTypes.Find("Health")!);
        Assert.Throws<InvalidOperationException>(() => value.Bytes);
        Assert.Throws<InvalidOperationException>(() => value["value"]);
        Assert.Throws<InvalidOperationException>(() => value["value"] = 7);
        Assert.Throws<InvalidOperationException>(() => Reflected.Component(new EntityId(1), "Health"));
        Assert.Equal(0x55555555, Marshal.ReadInt32(native.ComponentMemory));
        native.ComponentBytes = 8;
        value["value"] = 7;
        Assert.Equal(7L, value["value"]);
    }

    [Fact]
    public void MetadataCyclesAndOutOfParentFieldsCannotEscapeTheirObject()
    {
        using var native = new NativeReflection();
        var i32 = native.Add("i32", GameTypeKind.S32, 4);
        var nested = native.Add("Nested", GameTypeKind.Struct, 4, fields: [("outside", i32, 4)]);
        var outer = native.Add("Outer", GameTypeKind.Struct, 8, fields: [("nested", nested, 0)]);
        var value = ReflectedValue.Owned(GameTypes.Find("Outer")!, new byte[8]);
        Assert.Throws<ArgumentException>(() => value["nested.outside"] = 1);
        Assert.All(value.Bytes, b => Assert.Equal((byte)0, b));
        var cycle = native.Add("Cycle", GameTypeKind.Struct, 8);
        native.Types[cycle].Inner = cycle;
        Assert.Throws<NotSupportedException>(() => GameTypes.Find("Cycle")!.AllFields.ToArray());
        Assert.Throws<NotSupportedException>(() => GameTypes.Find("Cycle")!.Field("missing"));
        var alias = native.Add("Alias", GameTypeKind.Typedef, 4);
        native.Types[alias].Inner = alias;
        Assert.Throws<NotSupportedException>(() => GameTypes.Find("Alias")!.Resolved);
    }

    [Fact]
    public void ArrayIndexesAndNestedOffsetsAreCheckedBeforeWriting()
    {
        using var native = new NativeReflection();
        var i32 = native.Add("i32", GameTypeKind.S32, 4);
        var array = native.Add("Array", GameTypeKind.StaticArray, 8, inner: i32);
        native.Add("Holder", GameTypeKind.Struct, 8, fields: [("items", array, 0)]);
        var value = ReflectedValue.Owned(GameTypes.Find("Holder")!, new byte[8]);
        value["items[1]"] = 7;
        Assert.Equal(7L, value["items[1]"]);
        foreach (var path in new[] { "items[2]", "items[-1]", "items[9999999999999999999]", "items[0][0]" })
            Assert.ThrowsAny<ArgumentException>(() => value[path] = 99);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 7, 0, 0, 0 }, value.Bytes);
    }

    [Fact]
    public void EventsRequireRegistryMembershipAndValidateBeforePosting()
    {
        using var native = new NativeReflection();
        var i32 = native.Add("i32", GameTypeKind.S32, 4);
        var baseEvent = native.Add("keen::ecs::Event", GameTypeKind.Struct, 0);
        native.Add("UnlistedEvent", GameTypeKind.Struct, 4, inner: baseEvent);
        native.Add("KnownEvent", GameTypeKind.Struct, 4, inner: baseEvent, fields: [("data", i32, 0)], isEvent: true);
        Assert.Throws<ArgumentException>(() => GameEvents.Post("UnlistedEvent", new Dictionary<string, object?>()));
        Assert.Throws<ArgumentException>(() => GameEvents.Post("KnownEvent", new Dictionary<string, object?> { ["data"] = 1L << 40 }));
        Assert.Equal(0, native.Posts);
        Assert.True(GameEvents.Post("KnownEvent", new Dictionary<string, object?> { ["data"] = 7 }));
        Assert.Equal(1, native.Posts);
        Assert.Equal(4u, native.PostSize);
        Assert.Equal(7, native.PostValue);
    }

    [Fact]
    public void EventCallbacksCopyOnlyTheReflectedSizeAndContainMalformedDataErrors()
    {
        using var native = new NativeReflection();
        var i32 = native.Add("i32", GameTypeKind.S32, 4);
        native.Add("KnownEvent", GameTypeKind.Struct, 4, fields: [("data", i32, 0)], isEvent: true);
        native.EventBytes = 8; // Native streams may pad a four-byte type to eight bytes.
        Assert.Equal(1, GameEvents.Each("KnownEvent", e =>
        {
            Assert.Equal(4, e.Bytes.Length);
            Assert.Equal(7L, e["data"]);
        }));
        native.EventBytes = 3;
        Assert.Throws<InvalidOperationException>(() => GameEvents.Each("KnownEvent", _ => Assert.Fail("short payload delivered")));
    }

    [Fact]
    public void SystemOwnershipIsReleasedWhenTheCallingModRetires()
    {
        using var native = new NativeReflection();
        var mod = Kiln.HostAccess.CreateContext(new ModInfo("types-owner", "types-owner", "1", "/tmp"), 456, "/tmp");
        Kiln.HostAccess.Current = mod;
        try { GameSystems.SetEnabled("stamina_depletion_sprint", false); }
        finally { Kiln.HostAccess.Current = null; }
        Assert.Equal(456ul, native.LastSystemOwner);
        mod.Retire();
        Assert.Equal(456ul, native.ReleasedOwner);
        GameSystems.SetEnabled("stamina_depletion_sprint", false);
        Assert.Equal(321ul, native.LastSystemOwner); // Shared developer tools have a real revocable native owner too.
    }

    private sealed class NativeReflection : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CountFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint AtFn(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NameFn(NativeStr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InfoFn(nint type, nint output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FieldFn(nint type, uint index, nint output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ComponentFn(uint entity, uint hash, nint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint WorldFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EventCallbackFn(nint data, uint size, nint user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EachFn(nuint world, uint hash, nint callback, nint user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PostFn(nuint world, uint hash, nint data, uint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindSystemFn(NativeStr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SystemInfoFn(uint index, nint output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SystemSetFn(ulong owner, uint index, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReleaseFn(ulong owner);
        public sealed class Metadata(string name, GameTypeKind kind, int size, nint inner, (string, nint, int)[] fields, bool isEvent)
        {
            public readonly string Name = name;
            public readonly GameTypeKind Kind = kind;
            public readonly int Size = size;
            public nint Inner = inner, Defaults;
            public readonly (string Name, nint Type, int Offset)[] Fields = fields;
            public readonly bool IsEvent = isEvent;
        }
        public readonly Dictionary<nint, Metadata> Types = new();
        private readonly List<nint> _allocations = [];
        private readonly List<Delegate> _callbacks = [];
        private readonly ReflectionBackend _backend = new();
        public readonly nint ComponentMemory;
        public uint ComponentBytes = 8, EventBytes = 8;
        public int Posts, PostValue;
        public uint PostSize;
        public ulong LastSystemOwner, ReleasedOwner;

        private nint Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(Math.Max(1, size));
            _allocations.Add(p);
            Marshal.Copy(new byte[Math.Max(1, size)], 0, p, Math.Max(1, size));
            return p;
        }
        private static int Off<T>(string name) => (int)Marshal.OffsetOf<T>(name);
        private void Bind<T>(nint table, string member, Delegate callback)
        {
            _callbacks.Add(callback);
            Marshal.WriteIntPtr(table, Off<T>(member), Marshal.GetFunctionPointerForDelegate(callback));
        }
        private void NameAt(nint record, int offset, string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var p = Alloc(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            Marshal.WriteIntPtr(record, offset, p);
            Marshal.WriteIntPtr(record, offset + IntPtr.Size, bytes.Length);
        }
        public nint Add(string name, GameTypeKind kind, int size, nint inner = 0, (string, nint, int)[]? fields = null, bool isEvent = false)
        {
            var handle = Alloc(1);
            Types[handle] = new(name, kind, size, inner, fields ?? [], isEvent);
            return handle;
        }
        public nint SetDefaults(nint type, byte[] bytes)
        {
            var pointer = Alloc(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Types[type].Defaults = pointer;
            return pointer;
        }
        private int Info(nint type, nint output)
        {
            if (!Types.TryGetValue(type, out var t)) return 2;
            NameAt(output, Off<TypesNative.TypeInfo>("Name"), t.Name);
            NameAt(output, Off<TypesNative.TypeInfo>("QualifiedName"), t.Name);
            Marshal.WriteInt32(output, Off<TypesNative.TypeInfo>("TypeSize"), t.Size);
            Marshal.WriteByte(output, Off<TypesNative.TypeInfo>("Kind"), (byte)t.Kind);
            Marshal.WriteInt32(output, Off<TypesNative.TypeInfo>("Hash"), (int)Types.Keys.ToList().IndexOf(type) + 1);
            Marshal.WriteInt32(output, Off<TypesNative.TypeInfo>("FieldCount"), t.Fields.Length);
            Marshal.WriteIntPtr(output, Off<TypesNative.TypeInfo>("Inner"), t.Inner);
            Marshal.WriteIntPtr(output, Off<TypesNative.TypeInfo>("Defaults"), t.Defaults);
            return 0;
        }
        private int Field(nint type, uint index, nint output)
        {
            if (!Types.TryGetValue(type, out var t) || index >= t.Fields.Length) return 2;
            var f = t.Fields[index];
            NameAt(output, Off<TypesNative.TypeField>("Name"), f.Name);
            Marshal.WriteIntPtr(output, Off<TypesNative.TypeField>("Type"), f.Type);
            Marshal.WriteInt64(output, Off<TypesNative.TypeField>("Offset"), f.Offset);
            return 0;
        }
        public NativeReflection()
        {
            var types = Alloc(Marshal.SizeOf<TypesNative.Table>());
            Marshal.WriteInt32(types, Marshal.SizeOf<TypesNative.Table>());
            _backend.Tables["kiln.types"] = types;
            Bind<TypesNative.Table>(types, "TypeByName", new NameFn(n => Types.FirstOrDefault(t => t.Value.Name == n.ToString()).Key));
            Bind<TypesNative.Table>(types, "TypeInfo", new InfoFn(Info));
            Bind<TypesNative.Table>(types, "TypeField", new FieldFn(Field));
            Bind<TypesNative.Table>(types, "EventTypeCount", new CountFn(() => (uint)Types.Count(t => t.Value.IsEvent)));
            Bind<TypesNative.Table>(types, "EventTypeAt", new AtFn(i => Types.Where(t => t.Value.IsEvent).ElementAt((int)i).Key));
            Bind<TypesNative.Table>(types, "EventEach", new EachFn((w, h, callback, user) =>
            {
                var data = Alloc(8);
                Marshal.WriteInt32(data, 7);
                Marshal.GetDelegateForFunctionPointer<EventCallbackFn>(callback)(data, EventBytes, user);
                return 0;
            }));
            Bind<TypesNative.Table>(types, "EventPost", new PostFn((w, h, p, n) => { Posts++; PostSize = n; PostValue = Marshal.ReadInt32(p); return 0; }));
            Bind<TypesNative.Table>(types, "SystemFind", new FindSystemFn(_ => 0));
            Bind<TypesNative.Table>(types, "SystemInfo", new SystemInfoFn((i, p) => { NameAt(p, Off<TypesNative.SystemInfo>("Name"), "stamina_depletion_sprint"); return 0; }));
            Bind<TypesNative.Table>(types, "SystemSetEnabled", new SystemSetFn((o, i, e) => { LastSystemOwner = o; return 0; }));
            Bind<TypesNative.Table>(types, "SystemReleaseOwner", new ReleaseFn(o => { ReleasedOwner = o; return 0; }));
            var world = Alloc(Marshal.SizeOf<WorldPrefix>());
            Marshal.WriteInt32(world, Marshal.SizeOf<WorldPrefix>());
            _backend.Tables["kiln.world"] = world;
            ComponentMemory = Alloc(8);
            Marshal.Copy(Enumerable.Repeat((byte)0x55, 8).ToArray(), 0, ComponentMemory, 8);
            Bind<WorldPrefix>(world, "Component", new ComponentFn((e, h, n) => { Marshal.WriteInt32(n, (int)ComponentBytes); return ComponentMemory; }));
            Bind<WorldPrefix>(world, "ServerWorld", new WorldFn(() => 1));
            Kiln.Backend.Current = _backend;
        }
        public void Dispose()
        {
            var cache = (Dictionary<nint, GameType>)typeof(GameTypes).GetField("Cache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            foreach (var key in Types.Keys) cache.Remove(key);
            foreach (var allocation in _allocations) Marshal.FreeHGlobal(allocation);
        }
    }

    internal sealed class ReflectionBackend : Kiln.IKilnBackend
    {
        public bool TickThread = true;
        public string Version => "test";
        public bool IsTickThread => TickThread;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { }
        public readonly Dictionary<string, nint> Tables = new();
        public nint Service(string name, uint minVersion) => Tables.GetValueOrDefault(name);
        public ulong OwnerCreate(string id) => 321;
    }
}
