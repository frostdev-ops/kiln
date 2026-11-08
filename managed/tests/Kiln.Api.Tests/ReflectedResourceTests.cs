// SPDX-License-Identifier: MIT
// Reflected resources, lookup by field, and the claims behind writes (Overrides): over a fake kiln.types (a small resource type
// with every blob kind) and a fake kiln.assets that keeps the game's resources and per-owner overrides the way the native
// service does (the last put under a key wins; removing an owner's item reveals the one below).
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Kiln;
using Xunit;

namespace Kiln.Api.Tests;

public class ReflectedResourceTests : KilnTest
{
    private readonly FakeGame _game;
    private static readonly Guid Sword = Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95");
    private static readonly Guid Shield = Guid.Parse("2d622bd9-8170-41b1-822e-d3984b386d95");
    private static readonly Guid Bare = Guid.Parse("3d622bd9-8170-41b1-822e-d3984b386d95");
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public ReflectedResourceTests()
    {
        Overrides.ResetForTests();
        _game = new FakeGame(Backend);
        _game.Resources[(Sword, FakeGame.ItemHash, 0)] = FakeGame.Item(id: 0x43108313, stack: 1, weight: 2.5f, rarity: 3, flag: true, reference: Target,
            name: "Sword", tags: [(7, 10), (8, 20)], extraPower: 99, partX: 5);
        _game.Resources[(Shield, FakeGame.ItemHash, 0)] = FakeGame.Item(id: 2, stack: 1, weight: 4f, rarity: 0, flag: false, reference: Guid.Empty,
            name: "Shield", tags: [(7, 30)], extraPower: 12, partX: 6);
        _game.Resources[(Bare, FakeGame.ItemHash, 0)] = FakeGame.Item(id: 3, stack: 50, weight: 0.5f, rarity: 2, flag: false, reference: Guid.Empty,
            name: "", tags: [], extraPower: null, partX: null);
    }

    public override void Dispose()
    {
        Overrides.ResetForTests();
        _game.Dispose();
        base.Dispose();
    }

    private static ReflectedResource Res(Guid guid) => ReflectedResources.Get(guid, "Test::Item")!;

    [Fact]
    public void ReadsEveryKindOfField()
    {
        var r = Res(Sword);
        Assert.Equal("Test::Item", r.Type.QualifiedName);
        Assert.Equal(Sword, r.Guid);
        Assert.Equal(0x43108313L, r["id"]);                              // a one-field struct reads as its value
        Assert.Equal(0x43108313L, r["id.value"]);                        // and its field is still there by name
        Assert.Equal(1L, r["stack"]);
        Assert.Equal(2.5, r["weight"]);
        Assert.Equal(3L, r["rarity"]);
        Assert.Equal(true, r["flag"]);
        Assert.Equal(Target, r["reference"]);
        Assert.Equal("Sword", r["name"]);
        Assert.Equal(2, r.Count("tags"));
        Assert.Equal(5, r.Count("name"));
        Assert.Equal(8L, r["tags[1].id"]);
        Assert.Equal(20L, r["tags[1].weight"]);
        Assert.True(r.IsSet("extra"));
        Assert.Equal(99L, r["extra.power"]);
        Assert.Equal("PartA", r.VariantType("part")!.Name);
        Assert.Equal(5L, r["part.x"]);
        Assert.Equal(1, r.Get<int>("stack"));
        Assert.Equal(2.5f, r.Get<float>("weight"));
        Assert.Equal(Target, r.Get<Guid>("reference"));
    }

    [Fact]
    public void ReadsWhatIsNotThere()
    {
        var r = Res(Bare);
        Assert.Equal("", r["name"]);
        Assert.Equal(0, r.Count("tags"));
        Assert.False(r.IsSet("extra"));
        Assert.False(r.IsSet("part"));
        Assert.Null(r.VariantType("part"));
        Assert.IsType<ResourcePathMiss>(Assert.ThrowsAny<ArgumentException>(() => r["extra.power"]));
        Assert.IsType<ResourcePathMiss>(Assert.ThrowsAny<ArgumentException>(() => r["tags[0].id"]));
        Assert.Throws<ArgumentException>(() => r["nothing"]);
        Assert.Throws<ArgumentException>(() => r["stack.deeper"]);
        Assert.Throws<ArgumentException>(() => r["stack[0]"]);
        Assert.Throws<ArgumentException>(() => r["tags"]);       // a list is read through its elements
        Assert.Throws<ArgumentException>(() => r["extra"]);
        Assert.Throws<ArgumentException>(() => r[""]);
        Assert.Null(ReflectedResources.Get(Guid.NewGuid(), "Test::Item"));
        Assert.Throws<ArgumentException>(() => ReflectedResources.Get(Sword, "Test::Missing"));
        Assert.Throws<ArgumentException>(() => ReflectedResources.Get(Sword, "Test::Rarity"));   // an enum is not a resource type
    }

    [Fact]
    public void DescribeListsEveryFieldWithItsPath()
    {
        var text = Res(Sword).Describe();
        foreach (var line in new[]
        {
            "id = 1125155603", "stack = 1", "weight = 2.5", "rarity = Epic (3)", "flag = true", $"reference = {Target}",
            "name = \"Sword\"", "tags = [2 elements]", "tags[0].id = 7", "tags[1].weight = 20", "extra.power = 99", "part = <PartA>", "part.x = 5",
        })
            Assert.Contains(line + "\n", text.Replace("\r", ""));
        var bare = Res(Bare).Describe();
        Assert.Contains("extra = <not set>", bare);
        Assert.Contains("part = <empty>", bare);
        Assert.Contains("tags = [0 elements]", bare);
        Assert.DoesNotContain("tags[0]", bare);
        Assert.DoesNotContain("tags[1]", Res(Sword).Describe(maxArray: 1));
    }

    [Fact]
    public void ListsEveryResourceOfAType()
    {
        var all = ReflectedResources.All("Test::Item").Select(r => r.Guid).ToHashSet();
        Assert.Equal(new[] { Sword, Shield, Bare }.ToHashSet(), all);
        Assert.Empty(ReflectedResources.All("Test::Inner"));
    }

    [Fact]
    public void FindsResourcesByTheValueOfAField()
    {
        Guid[] Find(string path, object? value) => ReflectedResources.FindByField("Test::Item", path, value).Select(r => r.Guid).OrderBy(g => g).ToArray();
        Assert.Equal([Sword], Find("id", 0x43108313));
        Assert.Equal([Sword], Find("id", "1125155603"));                 // a number as text, like a write
        Assert.Equal(new[] { Sword, Shield }.OrderBy(g => g).ToArray(), Find("stack", 1));
        Assert.Equal([Bare], Find("rarity", "Rare"));                      // an enum by name
        Assert.Equal([Bare], Find("rarity", 2));
        Assert.Equal([Shield], Find("name", "Shield"));                    // text
        Assert.Empty(Find("name", "shield"));                              // exact text
        Assert.Equal([Shield], Find("weight", 4.0));
        Assert.Equal([Sword], Find("flag", true));
        Assert.Equal([Sword], Find("reference", Target));                  // a GUID
        Assert.Equal([Sword], Find("reference", Target.ToString()));
        Assert.Equal(2, Find("tags[0].id", 7).Length);                     // Bare has no tags: not a match, not an error
        Assert.Equal([Shield], Find("tags[0].weight", 30));
        Assert.Equal([Sword], Find("extra.power", 99));                    // Bare has no extra
        Assert.Equal(new[] { Sword, Shield }.OrderBy(g => g).ToArray(), Find("part.x", 5).Concat(Find("part.x", 6)).OrderBy(g => g).ToArray());
        Assert.Empty(Find("stack", 12345));
        Assert.Throws<ArgumentException>(() => Find("nothing", 1));        // not a field: an error, not "no match"
        Assert.Throws<ArgumentException>(() => Find("rarity", "Mythic"));  // not a value of the enum
        Assert.Throws<ArgumentException>(() => Find("stack", 1L << 40));
        Assert.Throws<ArgumentException>(() => Find("tags", 1));           // a list has no value to compare
        Assert.Equal(ReflectedResources.FindByField(GameTypes.Find("Test::Item")!, "id", 2).Single().Guid, Shield);
    }

    [Fact]
    public void AWriteIsCopyOnWriteAndRestoreGivesTheOriginalBack()
    {
        var mod = NewMod("writer");
        var vanilla = _game.Resources[(Sword, FakeGame.ItemHash, 0)];
        As(mod, () =>
        {
            var r = Res(Sword);
            var result = r.Set("stack", 5);
            Assert.True(result.Ok, result.Message);
            Assert.Contains("stack of Test::Item", result.Message);
            Assert.Contains("= 5 (was 1)", result.Message);
            Assert.Equal(5L, r["stack"]);
            Assert.Equal(5L, Res(Sword)["stack"]);                          // a fresh view sees what the game gets
            Assert.Equal(1L, r.Original("stack"));
            Assert.Equal("writer", r.HeldBy("stack"));
            Assert.Null(r.HeldBy("weight"));
            Assert.Equal(vanilla, _game.Resources[(Sword, FakeGame.ItemHash, 0)]);   // the game's own bytes are never touched
            Assert.Single(_game.Overrides);
            Assert.Equal(_game.Owner, _game.Overrides[0].Owner);

            Assert.True(r.Restore("stack"));
            Assert.Equal(1L, r["stack"]);
            Assert.Null(r.HeldBy("stack"));
            Assert.Empty(_game.Overrides);                                 // the override is gone, not just reset
            Assert.False(r.Restore("stack"));                              // nothing held any more
        });
        Assert.Empty(Overrides.Active);
    }

    [Fact]
    public void WritesGoThroughListsOptionalsVariantsAndReferences()
    {
        var mod = NewMod("deep");
        As(mod, () =>
        {
            var r = Res(Sword);
            var size = r.Bytes.Length;
            r["tags[1].weight"] = 65535;
            r["extra.power"] = -4;
            r["part.x"] = 77;
            r["reference"] = "11111111-2222-3333-4444-555555555555";
            r["rarity"] = "Rare";
            r["flag"] = false;
            r["weight"] = 0.25;
            r["id"] = 0x1234;                                                // through a one-field struct
            Assert.Equal(65535L, r["tags[1].weight"]);
            Assert.Equal(10L, r["tags[0].weight"]);                          // the neighbour is untouched
            Assert.Equal(-4L, r["extra.power"]);
            Assert.Equal(77L, r["part.x"]);
            Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), r["reference"]);
            Assert.Equal(2L, r["rarity"]);
            Assert.Equal(false, r["flag"]);
            Assert.Equal(0.25, r["weight"]);
            Assert.Equal(0x1234L, r["id.value"]);
            Assert.Equal("Sword", r["name"]);                                // the text and the lists are as they were
            Assert.Equal(2, r.Count("tags"));
            Assert.Equal(size, r.Bytes.Length);
            Assert.Equal(8, r.RestoreAll());
            Assert.Equal(20L, r["tags[1].weight"]);
            Assert.Equal(99L, r["extra.power"]);
            Assert.Equal(Target, r["reference"]);
            Assert.Equal(_game.Resources[(Sword, FakeGame.ItemHash, 0)], r.Bytes);
        });
    }

    [Fact]
    public void AWriteThatDoesNotFitChangesNothing()
    {
        var mod = NewMod("careful");
        As(mod, () =>
        {
            var r = Res(Sword);
            Assert.Throws<ArgumentException>(() => r.Set("stack", 1L << 40));
            Assert.Throws<ArgumentException>(() => r.Set("rarity", "Mythic"));
            Assert.Throws<ArgumentException>(() => r.Set("reference", "not a guid"));
            Assert.Throws<ArgumentException>(() => r.Set("nothing", 1));
            Assert.Throws<NotSupportedException>(() => r.Set("name", "Axe"));      // text keeps its size
            Assert.Throws<NotSupportedException>(() => r.Set("tags[0]", 1));       // a struct is set field by field
            Assert.Throws<NotSupportedException>(() => r.Set("tags", 1));
            Assert.IsType<ResourcePathMiss>(Assert.ThrowsAny<ArgumentException>(() => Res(Bare).Set("extra.power", 1)));
            Assert.Empty(_game.Overrides);
            Assert.Empty(Overrides.Active);
        });
        Assert.Equal(0, _game.Puts);
    }

    [Fact]
    public void TwoModsChangeDifferentFieldsOfOneResourceAndEachTakesOutOnlyItsOwn()
    {
        var a = NewMod("mod-a");
        var b = NewMod("mod-b");
        As(a, () => Assert.True(Res(Sword).Set("stack", 5).Ok));
        As(b, () => Assert.True(Res(Sword).Set("weight", 9).Ok));
        As(b, () => Assert.True(Res(Sword).Set("tags[0].weight", 11).Ok));
        var r = Res(Sword);
        Assert.Equal(5L, r["stack"]);
        Assert.Equal(9.0, r["weight"]);
        Assert.Equal(11L, r["tags[0].weight"]);
        Assert.Equal(1L, r.Original("stack"));
        Assert.Single(_game.Overrides);                                // one composed override, not one per mod
        Assert.Equal(3, Overrides.Active.Count);
        Assert.Equal(new[] { "mod-a", "mod-b", "mod-b" }, Overrides.Active.Select(i => i.Mod).ToArray());

        a.Retire();                                                          // mod-a unloads: its field goes back, mod-b's stay
        r = Res(Sword);
        Assert.Equal(1L, r["stack"]);
        Assert.Equal(9.0, r["weight"]);
        Assert.Equal(11L, r["tags[0].weight"]);
        Assert.Single(_game.Overrides);
        Assert.True(Backend.Logged("gave back 1 change of mod-a (unloading)"));

        As(b, () => Assert.Equal(2, Res(Sword).RestoreAll()));               // mod-b takes both of its fields out at once
        Assert.Empty(_game.Overrides);
        Assert.Equal(_game.Resources[(Sword, FakeGame.ItemHash, 0)], r.Bytes);
    }

    [Fact]
    public void TwoModsClaimingTheSameFieldGetAClearLoggedResult()
    {
        var a = NewMod("mod-a");
        var b = NewMod("mod-b");
        As(a, () => Assert.True(Res(Sword).Set("stack", 5).Ok));
        As(b, () =>
        {
            var r = Res(Sword);
            var result = r.Set("stack", 9);
            Assert.Equal(OverrideStatus.Conflict, result.Status);
            Assert.False(result.Ok);
            Assert.Equal("mod-a", result.HeldBy);
            Assert.Contains("mod-a", result.Message);
            Assert.Contains("mod-b", result.Message);
            Assert.Contains("stack of Test::Item", result.Message);
            Assert.Contains("set to 5", result.Message);
            Assert.Equal(5L, r["stack"]);                                    // the first claim keeps the field
            Assert.Equal("mod-a", r.HeldBy("stack"));
            Assert.False(r.Restore("stack"));                                // mod-b has nothing to give back
            var thrown = Assert.Throws<InvalidOperationException>(() => r["stack"] = 9);
            Assert.Contains("mod-a", thrown.Message);
        });
        Assert.True(Backend.Logged("Warn: overrides: refused: stack of Test::Item"));
        As(a, () => Assert.True(Res(Sword).Set("stack", 6).Ok));              // the holder may change its own value
        Assert.Equal(6L, Res(Sword)["stack"]);
        As(a, () => Assert.True(Res(Sword).Restore("stack")));
        As(b, () => Assert.True(Res(Sword).Set("stack", 9).Ok));              // free again
        Assert.Equal(9L, Res(Sword)["stack"]);
        Assert.Equal(["mod-b"], Overrides.Active.Select(i => i.Mod).ToArray());
    }

    [Fact]
    public void UnloadingAModGivesEverythingBack()
    {
        var mod = NewMod("temp");
        As(mod, () =>
        {
            Res(Sword).Set("stack", 5);
            Res(Shield).Set("stack", 6);
            Overrides.Claim("temp:thing", () => { }, () => Backend.Lines.Add("restored thing"));
        });
        Assert.Equal(2, _game.Overrides.Count);
        mod.Retire();
        Assert.Empty(_game.Overrides);
        Assert.Empty(Overrides.Active);
        Assert.Equal(1L, Res(Sword)["stack"]);
        Assert.Equal(1L, Res(Shield)["stack"]);
        Assert.True(Backend.Logged("restored thing"));
        Assert.True(Backend.Logged("gave back 3 changes of temp (unloading)"));
    }

    [Fact]
    public void PutsAreBalancedWithRemoves()
    {
        var mod = NewMod("balance");
        As(mod, () =>
        {
            Res(Sword).Set("stack", 5);
            Res(Sword).Set("weight", 6);
            Res(Shield).Set("stack", 7);
        });
        mod.Retire();
        Assert.Equal(2, _game.Removes);                                        // one remove per resource, when its last claim went
        Assert.Empty(_game.Overrides);
    }

    [Fact]
    public void ClaimsOfAnyKind()
    {
        var a = NewMod("mod-a");
        var b = NewMod("mod-b");
        var value = 1;
        As(a, () =>
        {
            var r = Overrides.Claim("setting:speed", () => value = 5, () => value = 1);
            Assert.True(r.Ok);
            Assert.Equal(5, value);
            Assert.Equal("mod-a", Overrides.HolderOf("setting:speed"));
            var again = Overrides.Claim("setting:speed", () => value = 6, () => value = -1);   // updated, the first restore stays
            Assert.True(again.Ok);
            Assert.Equal(6, value);
        });
        As(b, () =>
        {
            var applied = false;
            var r = Overrides.Claim("setting:speed", () => applied = true, () => { });
            Assert.Equal(OverrideStatus.Conflict, r.Status);
            Assert.Equal("mod-a", r.HeldBy);
            Assert.False(applied);
            Assert.False(Overrides.Release("setting:speed"));
            Assert.Equal(0, Overrides.RestoreAll());
        });
        Assert.True(Backend.Logged("Warn: overrides: refused: 'setting:speed' is already changed by mod 'mod-a'"));
        As(a, () =>
        {
            Assert.True(Overrides.Release("setting:speed"));
            Assert.Equal(1, value);                                           // the first restore ran
            Assert.Null(Overrides.HolderOf("setting:speed"));
            Assert.False(Overrides.Release("setting:speed"));
        });
    }

    [Fact]
    public void RestoreAllGivesBackNewestFirstAndSurvivesAFailingRestore()
    {
        var mod = NewMod("order");
        var order = new List<string>();
        As(mod, () =>
        {
            Overrides.Claim("one", () => { }, () => order.Add("one"));
            Overrides.Claim("two", () => { }, () => throw new InvalidOperationException("boom"));
            Overrides.Claim("three", () => { }, () => order.Add("three"));
            Assert.Equal(3, Overrides.Active.Count);
            Assert.Equal(3, Overrides.RestoreAll());
        });
        Assert.Equal(["three", "one"], order);
        Assert.True(Backend.Logged("restoring 'two' failed: boom"));
        Assert.Empty(Overrides.Active);
    }

    [Fact]
    public void AClaimWhoseApplyThrowsIsDropped()
    {
        var mod = NewMod("thrower");
        var restored = false;
        As(mod, () =>
        {
            Assert.Throws<InvalidOperationException>(() => Overrides.Claim("x", () => throw new InvalidOperationException(), () => restored = true));
            Assert.Null(Overrides.HolderOf("x"));
            Assert.True(Overrides.Claim("x", () => { }, () => { }).Ok);        // free to claim again
        });
        Assert.False(restored);
    }

    [Fact]
    public void ResourceWritesFailSoftWhenTheGameWillNotServeThem()
    {
        _game.IgnorePuts = true;                                              // a resource the game does not take as an override
        var mod = NewMod("refused");
        As(mod, () =>
        {
            var r = Res(Sword).Set("stack", 5);
            Assert.Equal(OverrideStatus.Unavailable, r.Status);
            Assert.Contains("was not changed", r.Message);
            Assert.Equal(1L, Res(Sword)["stack"]);
        });
        Assert.Empty(Overrides.Active);
        Assert.Empty(_game.Overrides);
        Assert.True(Backend.Logged("Warn: overrides: stack of Test::Item"));
    }

    [Fact]
    public void WithoutTheServicesEverythingFindsNothing()
    {
        Backend.Tables.Clear();
        Assert.False(ReflectedResources.Available);
        Assert.Null(ReflectedResources.Get(Sword, _game.ItemType));
        Assert.Null(ReflectedResources.Get(Sword, "Test::Item"));                 // by name too: no types, so no error either
        Assert.Empty(ReflectedResources.All("Test::Item"));
        Assert.Empty(ReflectedResources.FindByField("Test::Item", "stack", 1));
        Assert.Empty(ReflectedResources.All(_game.ItemType));
        Assert.Empty(ReflectedResources.FindByField(_game.ItemType, "stack", 1));
    }

    [Fact]
    public void TypesOfAValue()
    {
        var r = Res(Sword);
        Assert.Same(r.Type, GameTypes.TypeOf(r));
        Assert.Equal("Test::Item", GameTypes.TypeOf(r)!.QualifiedName);
        var owned = ReflectedValue.Owned(r.Type, new byte[r.Type.Size]);
        Assert.Same(r.Type, GameTypes.TypeOf(owned));
        Assert.Equal("i32", GameTypes.TypeOf(r.Type.Field("stack")!.Value)!.Name);
        Assert.Same(r.Type, GameTypes.TypeOf(r.Type));
        Assert.Null(GameTypes.TypeOf("not a game value"));
        Assert.Null(GameTypes.TypeOf(null));
    }

    [Fact]
    public void ResourceViewsOfTheSameResourceAgree()
    {
        var r = Res(Sword);
        Assert.Equal(r.ToString(), Res(Sword).ToString());
        Assert.Contains("Test::Item", r.ToString());
        Assert.True(r.Exists);
        _game.Resources.Remove((Sword, FakeGame.ItemHash, 0));
        Assert.False(r.Exists);
        Assert.Throws<KeyNotFoundException>(() => r["stack"]);
    }

    // ---- the fake game -------------------------------------------------------------------------------------------

    private sealed class FakeGame : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint CountFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint AtFn(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ByHashFn(uint hash);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint NameFn(NativeStr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InfoFn(nint type, nint output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FieldFn(nint type, uint index, nint output);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn(nint type, uint index, nint name, nint value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFn(nint key, nint buffer, nuint capacity, nint outLength);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ListFn(uint type, nint keys, nuint capacity, nint outCount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PutFn(ulong owner, nint key, nint data, nuint length, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RemoveFn(ulong owner, nint key);

        private sealed record Meta(string Name, GameTypeKind Kind, int Size, nint Inner, (string Name, nint Type, int Offset)[] Fields,
                                   (string, long)[] Values, uint Hash);
        private readonly Dictionary<nint, Meta> _types = new();
        private readonly List<nint> _allocations = [];
        private readonly List<Delegate> _callbacks = [];
        private readonly Dictionary<string, nint> _byName = new();

        public readonly Dictionary<(Guid, uint, uint), byte[]> Resources = new();
        public readonly List<(ulong Owner, (Guid, uint, uint) Key, byte[] Bytes)> Overrides = [];
        public bool IgnorePuts;
        public int Puts, Removes;
        public ulong Owner => 777;
        public readonly GameType ItemType;
        private readonly FakeBackend _backend;
        public static readonly uint ItemHash = Fnv("Test::Item");

        public static uint Fnv(string s)
        {
            var h = 0x811c9dc5u;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 0x01000193u;
            return h;
        }

        private nint Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(Math.Max(1, size));
            _allocations.Add(p);
            Marshal.Copy(new byte[Math.Max(1, size)], 0, p, Math.Max(1, size));
            return p;
        }

        private nint Add(string name, GameTypeKind kind, int size, nint inner = 0, (string, nint, int)[]? fields = null, (string, long)[]? values = null)
        {
            var handle = Alloc(1);
            _types[handle] = new Meta(name, kind, size, inner, fields ?? [], values ?? [], Fnv(name));
            _byName[name] = handle;
            return handle;
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

        public FakeGame(FakeBackend backend)
        {
            _backend = backend;
            var u8 = Add("u8", GameTypeKind.U8, 1);
            var u16 = Add("u16", GameTypeKind.U16, 2);
            var i32 = Add("i32", GameTypeKind.S32, 4);
            var u32 = Add("u32", GameTypeKind.U32, 4);
            var hashKey = Add("Test::HashKey32", GameTypeKind.Struct, 4, fields: [("value", u32, 0)]);   // a one-field struct, like the game's ids
            var f32 = Add("f32", GameTypeKind.F32, 4);
            var boolean = Add("bool", GameTypeKind.Bool, 1);
            var guid = Add("Guid", GameTypeKind.Guid, 16);
            var rarity = Add("Test::Rarity", GameTypeKind.Enum, 1, values: [("Common", 0), ("Rare", 2), ("Epic", 3)]);
            var tag = Add("Test::Tag", GameTypeKind.Struct, 4, fields: [("id", u16, 0), ("weight", u16, 2)]);
            var inner = Add("Test::Inner", GameTypeKind.Struct, 4, fields: [("power", i32, 0)]);
            var partBase = Add("Test::Part", GameTypeKind.Struct, 4);
            Add("Test::PartA", GameTypeKind.Struct, 4, inner: partBase, fields: [("x", i32, 0)]);
            Add("Test::PartB", GameTypeKind.Struct, 4, inner: partBase, fields: [("y", f32, 0)]);
            var text = Add("BlobString", GameTypeKind.BlobString, 8);
            var tags = Add("BlobArray<Test::Tag>", GameTypeKind.BlobArray, 8, inner: tag);
            var optional = Add("BlobOptional<Test::Inner>", GameTypeKind.BlobOptional, 4, inner: inner);
            var variant = Add("BlobVariant<Test::Part>", GameTypeKind.BlobVariant, 12, inner: partBase);
            Add("Test::Item", GameTypeKind.Struct, 64, fields:
            [
                ("id", hashKey, 0), ("stack", i32, 4), ("weight", f32, 8), ("rarity", rarity, 12), ("flag", boolean, 13), ("reference", guid, 16),
                ("name", text, 32), ("tags", tags, 40), ("extra", optional, 48), ("part", variant, 52),
            ]);
            _ = u8;
            _ = u16;

            var types = Alloc(Marshal.SizeOf<TypesNative.Table>());
            Marshal.WriteInt32(types, Marshal.SizeOf<TypesNative.Table>());
            Bind<TypesNative.Table>(types, "TypeCount", new CountFn(() => (uint)_types.Count));
            Bind<TypesNative.Table>(types, "TypeAt", new AtFn(i => _types.Keys.ElementAt((int)i)));
            Bind<TypesNative.Table>(types, "TypeByHash", new ByHashFn(h => _types.FirstOrDefault(t => t.Value.Hash == h).Key));
            Bind<TypesNative.Table>(types, "TypeByName", new NameFn(n => _byName.GetValueOrDefault(n.ToString())));
            Bind<TypesNative.Table>(types, "TypeInfo", new InfoFn(Info));
            Bind<TypesNative.Table>(types, "TypeField", new FieldFn(Field));
            Bind<TypesNative.Table>(types, "TypeEnum", new EnumFn(EnumValue));
            Bind<TypesNative.Table>(types, "ComponentTypeCount", new CountFn(() => 0));
            backend.Tables["kiln.types"] = types;

            var assets = Alloc(Marshal.SizeOf<ResourceStore.Table>() + 64);   // longer than the prefix, like the real table
            Marshal.WriteInt32(assets, Marshal.SizeOf<ResourceStore.Table>() + 64);
            Bind<ResourceStore.Table>(assets, "ResourceRead", new ReadFn(Read));
            Bind<ResourceStore.Table>(assets, "ResourceList", new ListFn(List));
            Bind<ResourceStore.Table>(assets, "ResourcePut", new PutFn(Put));
            Bind<ResourceStore.Table>(assets, "ResourceRemove", new RemoveFn(Remove));
            backend.Tables["kiln.assets"] = assets;
            ItemType = GameTypes.Find("Test::Item")!;
        }

        private int Info(nint type, nint output)
        {
            if (!_types.TryGetValue(type, out var t)) return 2;
            NameAt(output, Off<TypesNative.TypeInfo>("Name"), t.Name.Split("::")[^1]);
            NameAt(output, Off<TypesNative.TypeInfo>("QualifiedName"), t.Name);
            Marshal.WriteInt32(output, Off<TypesNative.TypeInfo>("TypeSize"), t.Size);
            Marshal.WriteInt16(output, Off<TypesNative.TypeInfo>("Alignment"), 4);
            Marshal.WriteByte(output, Off<TypesNative.TypeInfo>("Kind"), (byte)t.Kind);
            Marshal.WriteInt32(output, Off<TypesNative.TypeInfo>("Hash"), (int)t.Hash);
            Marshal.WriteInt32(output, Off<TypesNative.TypeInfo>("FieldCount"), t.Kind == GameTypeKind.Enum ? t.Values.Length : t.Fields.Length);
            Marshal.WriteIntPtr(output, Off<TypesNative.TypeInfo>("Inner"), t.Inner);
            return 0;
        }

        private int Field(nint type, uint index, nint output)
        {
            if (!_types.TryGetValue(type, out var t) || index >= t.Fields.Length) return 2;
            var f = t.Fields[index];
            NameAt(output, Off<TypesNative.TypeField>("Name"), f.Name);
            Marshal.WriteIntPtr(output, Off<TypesNative.TypeField>("Type"), f.Type);
            Marshal.WriteInt64(output, Off<TypesNative.TypeField>("Offset"), f.Offset);
            return 0;
        }

        private int EnumValue(nint type, uint index, nint name, nint value)
        {
            if (!_types.TryGetValue(type, out var t) || index >= t.Values.Length) return 2;
            NameAt(name, 0, t.Values[index].Item1);
            Marshal.WriteInt64(value, t.Values[index].Item2);
            return 0;
        }

        private static (Guid, uint, uint) KeyAt(nint key)
        {
            var bytes = new byte[16];
            Marshal.Copy(key, bytes, 0, 16);
            return (new Guid(bytes), (uint)Marshal.ReadInt32(key, 16), (uint)Marshal.ReadInt32(key, 20));
        }

        private int Read(nint key, nint buffer, nuint capacity, nint outLength)
        {
            var k = KeyAt(key);
            var bytes = Overrides.LastOrDefault(o => o.Key == k).Bytes ?? Resources.GetValueOrDefault(k);
            if (bytes == null) return 2;
            Marshal.WriteIntPtr(outLength, bytes.Length);
            if (capacity < (nuint)bytes.Length) return 5;
            if (bytes.Length > 0) Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return 0;
        }

        private int List(uint type, nint keys, nuint capacity, nint outCount)
        {
            var list = Resources.Keys.Where(k => k.Item2 == type).ToList();
            Marshal.WriteIntPtr(outCount, list.Count);
            if (capacity < (nuint)list.Count) return 5;
            for (var i = 0; i < list.Count; i++)
            {
                Marshal.Copy(list[i].Item1.ToByteArray(), 0, keys + i * 24, 16);
                Marshal.WriteInt32(keys + i * 24, 16, (int)list[i].Item2);
                Marshal.WriteInt32(keys + i * 24, 20, (int)list[i].Item3);
            }
            return 0;
        }

        private int Put(ulong owner, nint key, nint data, nuint length, uint flags)
        {
            Puts++;
            var k = KeyAt(key);
            if (IgnorePuts || ((flags & 2) != 0 && !Resources.ContainsKey(k))) return 0;   // the native side skips it and still says ok
            var bytes = new byte[(int)length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            Overrides.RemoveAll(o => o.Owner == owner && o.Key == k);                       // an owner's put replaces its earlier one
            Overrides.Add((owner, k, bytes));
            return 0;
        }

        private int Remove(ulong owner, nint key)
        {
            Removes++;
            var k = KeyAt(key);
            Overrides.RemoveAll(o => o.Owner == owner && o.Key == k);
            return 0;
        }

        public void Dispose()
        {
            var cache = (Dictionary<nint, GameType>)typeof(GameTypes).GetField("Cache", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            lock (cache) foreach (var key in _types.Keys) cache.Remove(key);
            _backend.Tables.Clear();
            foreach (var allocation in _allocations) Marshal.FreeHGlobal(allocation);
        }

        // A Test::Item resource: 64 bytes, then the text, the tags, the optional's value and the variant's value.
        public static byte[] Item(uint id, int stack, float weight, byte rarity, bool flag, Guid reference, string name,
                                  (ushort Id, ushort Weight)[] tags, int? extraPower, int? partX)
        {
            var d = new List<byte>(new byte[64]);
            void Pad(int to) { while (d.Count < to) d.Add(0); }
            void Put32(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(CollectionsMarshal.AsSpan(d).Slice(at, 4), v);
            Put32(0, (int)id);
            Put32(4, stack);
            BinaryPrimitives.WriteSingleLittleEndian(CollectionsMarshal.AsSpan(d).Slice(8, 4), weight);
            d[12] = rarity;
            d[13] = (byte)(flag ? 1 : 0);
            reference.ToByteArray().CopyTo(CollectionsMarshal.AsSpan(d).Slice(16, 16));
            var text = System.Text.Encoding.UTF8.GetBytes(name);
            if (text.Length > 0)
            {
                Put32(32, d.Count - 32);
                Put32(36, text.Length);
                d.AddRange(text);
                Pad((d.Count + 3) & ~3);
            }
            if (tags.Length > 0)
            {
                Put32(40, d.Count - 40);
                Put32(44, tags.Length);
                foreach (var (tid, w) in tags)
                {
                    d.AddRange(BitConverter.GetBytes(tid));
                    d.AddRange(BitConverter.GetBytes(w));
                }
            }
            if (extraPower is { } p)
            {
                Put32(48, d.Count - 48);
                d.AddRange(BitConverter.GetBytes(p));
            }
            if (partX is { } x)
            {
                Put32(52, (int)Fnv("Test::PartA"));
                Put32(56, d.Count - 56);
                Put32(60, 4);
                d.AddRange(BitConverter.GetBytes(x));
            }
            return d.ToArray();
        }
    }
}
