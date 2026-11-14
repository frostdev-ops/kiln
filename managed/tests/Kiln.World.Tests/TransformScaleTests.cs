// SPDX-License-Identifier: MIT
// World.SetTransformScale (0.2.0-dev.28) without the game: where CurrentTransform keeps its scale (the two accepted shapes
// and every refused one, and the server build's own type dump), Scale3's layout and JSON, the argument policy, the
// answer decoded from the tracked write's, and the row off without kiln.types.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Node = Kiln.TransformScale.Node;

namespace Kiln.WorldTests;

[Collection("kiln.world backend")]  // SetTransformScale reads the process's backend (GameTypes, the capability rows)
public sealed class TransformScaleTests
{
    private static readonly Node F32 = new("keen::float32", GameTypeKind.F32, 4, []);
    private static readonly Node F64 = new("keen::float64", GameTypeKind.F64, 8, []);
    private static readonly Node U8 = new("keen::uint8", GameTypeKind.U8, 1, []);

    private static Node Struct(string name, int size, params (string Name, int Offset, Node Type)[] fields) => new(name, GameTypeKind.Struct, size, fields);

    private static Node Float3(params int[] offsets) =>
        Struct("keen::float3", 12, [.. offsets.Select((o, i) => ("xyz"[i].ToString(), o, F32))]);

    private static Node Current(Node scale, int scaleAt = 40, int transformSize = 56, int componentSize = 56, int transformAt = 0) =>
        Struct("keen::ecs::CurrentTransform", componentSize, ("transform", transformAt,
            Struct("keen::WorldTransform", transformSize, ("position", 0, Struct("keen::WorldPosition", 24)), ("orientation", 24, Struct("keen::Quaternion", 16)),
                ("scale", scaleAt, scale))));

    [Fact]
    public void A_struct_of_three_float32_is_accepted_at_its_reflected_place()
    {
        var (layout, why) = TransformScale.Resolve(Current(Float3(0, 4, 8)));
        Assert.Equal(40, layout!.Offset);
        Assert.Contains("a struct of three float32", why);
        // the place comes from the metadata, not from a constant
        Assert.Equal(28, TransformScale.Resolve(Current(Float3(0, 4, 8), scaleAt: 20, transformAt: 8, componentSize: 64))!.Layout!.Offset);
    }

    [Fact]
    public void A_static_array_of_three_float32_and_a_typedef_are_accepted()
    {
        var array = new Node("keen::float32[3]", GameTypeKind.StaticArray, 12, [], F32, 3);
        Assert.Contains("an array of three float32", TransformScale.Resolve(Current(array)).Why);
        var typedef = new Node("keen::Scale", GameTypeKind.Typedef, 12, [], Float3(0, 4, 8));
        Assert.Equal(40, TransformScale.Resolve(Current(typedef)).Layout!.Offset);
        var f32Alias = new Node("keen::real", GameTypeKind.Typedef, 4, [], F32);
        Assert.NotNull(TransformScale.Resolve(Current(Struct("keen::float3", 12, ("x", 0, f32Alias), ("y", 4, f32Alias), ("z", 8, f32Alias)))).Layout);
    }

    private static readonly Dictionary<string, (Node? Component, string Expected)> RefusedCases = new()
    {
        ["no component"] = (null, "has no keen::ecs::CurrentTransform"),
        ["a raw byte array transform"] = (Struct("keen::ecs::CurrentTransform", 56, ("transform", 0, new Node("u8[56]", GameTypeKind.StaticArray, 56, [], U8, 56))), "not a struct with a scale"),
        ["no transform field"] = (Struct("keen::ecs::CurrentTransform", 56, ("pose", 0, Struct("keen::WorldTransform", 56))), "no field 'transform'"),
        ["no scale field"] = (Struct("keen::ecs::CurrentTransform", 56, ("transform", 0, Struct("keen::WorldTransform", 56, ("position", 0, Struct("p", 24))))), "no field 'scale'"),
        ["three float64"] = (Current(Struct("keen::double3", 24, ("x", 0, F64), ("y", 8, F64), ("z", 16, F64)), transformSize: 64, componentSize: 64), "not three contiguous float32"),
        ["four floats"] = (Current(Struct("keen::float4", 16, ("x", 0, F32), ("y", 4, F32), ("z", 8, F32), ("w", 12, F32)), transformSize: 56), "not three contiguous float32"),
        ["fields out of order"] = (Current(Float3(0, 8, 4)), "not three contiguous float32"),
        ["an array of two"] = (Current(new Node("keen::float32[3]", GameTypeKind.StaticArray, 12, [], F32, 2)), "not three contiguous float32"),
        ["outside the component"] = (Current(Float3(0, 4, 8), scaleAt: 48), "does not lie inside"),
        ["not 4-aligned"] = (Current(Float3(0, 4, 8), scaleAt: 41, transformSize: 60, componentSize: 60), "not 4-aligned"),
    };

    public static TheoryData<string> Refused() => [.. RefusedCases.Keys];

    [Theory]
    [MemberData(nameof(Refused))]
    public void Every_other_shape_is_refused_with_what_was_found(string what)
    {
        var (component, expected) = RefusedCases[what];
        var (layout, why) = TransformScale.Resolve(component);
        Assert.Null(layout);
        Assert.Contains(expected, why, StringComparison.Ordinal);
    }

    // The server build's type dump (sigdb/types/server, structure only): CurrentTransform -> transform -> scale is a float3
    // at +40 there, as the reflection the row reads at run time gives it.
    [Fact]
    public void The_server_builds_own_type_dump_resolves_to_a_float3_at_40()
    {
        var dir = Path.Combine(Repo(), "sigdb", "types", "server");
        var file = Path.Combine(dir, "f0d824be7fcd48ebfe2458ff5c28da26060c2074e3ec1fcac587e5a1d508aeb2.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var types = doc.RootElement.GetProperty("types").EnumerateArray().ToDictionary(t => t.GetProperty("qualifiedName").GetString()!);
        Node NodeOf(string q, int depth)
        {
            var t = types[q];
            var kind = (GameTypeKind)t.GetProperty("kind").GetInt32();
            var fields = depth > 6 || !t.TryGetProperty("fields", out var fs) ? [] :
                fs.EnumerateArray().Select(f => (f.GetProperty("name").GetString()!, f.GetProperty("offset").GetInt32(), NodeOf(f.GetProperty("type").GetString()!, depth + 1))).ToList();
            var inner = kind is GameTypeKind.Typedef or GameTypeKind.StaticArray && t.TryGetProperty("inner", out var i) ? NodeOf(i.GetString()!, depth + 1) : null;
            var size = t.GetProperty("size").GetInt32();
            return new Node(q, kind, size, fields, inner, kind == GameTypeKind.StaticArray && inner is { Size: > 0 } e ? size / e.Size : 0);
        }
        var (layout, why) = TransformScale.Resolve(NodeOf("keen::ecs::CurrentTransform", 0));
        Assert.Equal(40, layout?.Offset);
        Assert.Equal("keen::ecs::CurrentTransform.transform.scale at +40 (keen::float3: a struct of three float32)", why);
    }

    [Fact]
    public void Transform_scale_is_held_on_both_sides_until_the_player_check()
    {
        using var gates = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "native", "core", "gates", "dev28-13b-shroudforge.json")));
        var row = Assert.Single(gates.RootElement.GetProperty("gates").EnumerateArray(),
            g => g.GetProperty("name").GetString() == World.TransformScaleCapability);
        Assert.False(row.TryGetProperty("side", out _));
        Assert.Equal(["13-world.transform-scale"], row.GetProperty("checks").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public void Scale3_is_three_sequential_floats_and_its_JSON_is_lower_case()
    {
        Assert.Equal(12, Unsafe.SizeOf<Scale3>());
        Assert.Equal(12, Marshal.SizeOf<Scale3>());
        var s = new Scale3(1.25f, 0.75f, 1.5f);
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<Scale3>(in s)).ToArray();
        Assert.Equal(1.25f, BitConverter.ToSingle(bytes, 0));
        Assert.Equal(0.75f, BitConverter.ToSingle(bytes, 4));
        Assert.Equal(1.5f, BitConverter.ToSingle(bytes, 8));
        Assert.Equal("""{"x":1.25,"y":0.75,"z":1.5}""", JsonSerializer.Serialize(s));
        Assert.Equal(s, JsonSerializer.Deserialize<Scale3>("""{"x":1.25,"y":0.75,"z":1.5}"""));
        Assert.Equal("(1.25, 0.75, 1.5)", s.ToString());
        Assert.Equal(Scale3.Uniform(1f), Scale3.One);
        Assert.True(s.IsValid);
    }

    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(-1f, 1f, 1f)]
    [InlineData(1f, float.NaN, 1f)]
    [InlineData(1f, 1f, float.PositiveInfinity)]
    [InlineData(1f, float.NegativeInfinity, 1f)]
    public void A_factor_that_is_not_finite_and_positive_is_a_bad_argument(float x, float y, float z)
    {
        Assert.False(new Scale3(x, y, z).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = World.SetTransformScale(new EntityId(7), new Scale3(x, y, z)); });
    }

    [Fact]
    public void No_entity_is_a_bad_argument_and_without_kiln_types_the_row_is_off()
    {
        Assert.Throws<ArgumentException>(() => { _ = World.SetTransformScale(EntityId.None, Scale3.One); });
        var e = Assert.Throws<KilnUnavailableException>(() => { _ = World.SetTransformScale(new EntityId(7), Scale3.One); });
        Assert.Equal(World.TransformScaleCapability, e.Capability);
        Assert.Equal("world.transform_scale", World.TransformScaleCapability);
    }

    [Fact]
    public void The_answer_decodes_the_twelve_bytes_before_and_after()
    {
        static byte[] B(Scale3 s) => MemoryMarshal.AsBytes(new ReadOnlySpan<Scale3>(in s)).ToArray();
        var asked = new Scale3(1.25f, 0.75f, 1.5f);
        var written = new WorldWriteResult
        {
            Status = WorldWriteStatus.Written, Entity = new EntityId(9), Component = "CurrentTransform", Offset = 40, ComponentSize = 56,
            Before = B(Scale3.One), After = B(asked), SaveTracked = true, ReplicationTracked = false,
        };
        var r = TransformScale.Of(new EntityId(9), asked, written);
        Assert.Equal((true, true, Scale3.One, asked, asked), (r.Ok, r.Changed, r.Before!.Value, r.Now!.Value, r.Requested));
        Assert.Equal((true, false, (bool?)null), (r.SaveTracked!.Value, r.ReplicationTracked!.Value, r.Kept));
        var same = TransformScale.Of(new EntityId(9), asked, written with { Status = WorldWriteStatus.Unchanged, Before = B(asked) });
        Assert.True(same.Ok);
        Assert.False(same.Changed);
        var refused = TransformScale.Of(new EntityId(9), asked, new WorldWriteResult
        {
            Status = WorldWriteStatus.NotDeclared, Entity = new EntityId(9), Component = "CurrentTransform", Reason = "add \"writes\": [\"CurrentTransform\"]",
        });
        Assert.Equal((false, false, (Scale3?)null, (Scale3?)null), (refused.Ok, refused.Changed, refused.Before, refused.Now));
        Assert.Equal(WorldWriteStatus.NotDeclared, refused.WorldWriteStatus);
        Assert.Contains("writes", refused.Reason);
    }

    private static string Repo([CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
}
