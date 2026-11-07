// SPDX-License-Identifier: MIT
// VFX blocks, particle ABI 2: spawn shapes, velocity stretch and a mod's own particle module reach the builder in its JSON shape, and a Kiln VFX's
// per-play parameters are laid out here (a server has no VFX) for the data routes that carry a start block. The builder's side: tools/vfxcook/tests/test_vfx_abi2.py.
using System.Numerics;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class VfxAbi2Tests
{
    private static readonly ParticleBlock Spark = new() { Burst = 8, Lifetime = 0.5f };

    private static (ModBlocks Mod, BlockHandle Vfx) WithVfx(VfxBlock v)
    {
        var mod = new ModBlocks("vfx-abi2", 1);
        var (h, _, _) = Compiler.Lower(mod, "burst", v);
        mod.Handles["burst"] = h;
        return (mod, h);
    }

    [Fact]
    public void Shapes_align_stretch_and_shader_reach_the_builder_as_its_json()
    {
        var d = Compiler.VfxDefinition(new VfxBlock
        {
            Particles =
            [
                Spark with { Shape = SpawnShape.Ring(2), Spread = Shape.Outward, Align = ParticleAlign.Velocity, Stretch = 0.1f,
                             Shader = new ParticleShader("shaders/build/swirl.spv", [6f, 0.8f]) },
                Spark with { Shape = SpawnShape.Box(1, 2, 3) },
                Spark with { Shape = SpawnShape.Sphere(2, inner: 1) },
            ],
        });
        var p = d["particles"]!.AsArray();
        Assert.Equal("""{"ring":2}""", p[0]!["shape"]!.ToJsonString());
        Assert.Equal("outward", (string?)p[0]!["spread"]);
        Assert.Equal("velocity", (string?)p[0]!["align"]);
        Assert.Equal(0.1f, (float)p[0]!["stretch"]!);
        Assert.Equal("""{"file":"shaders/build/swirl.spv","constants":[6,0.8]}""", p[0]!["shader"]!.ToJsonString());
        Assert.Equal("""{"box":[1,2,3]}""", p[1]!["shape"]!.ToJsonString());
        Assert.Equal("""{"sphere":2,"inner":1}""", p[2]!["shape"]!.ToJsonString());
    }

    [Fact]
    public void The_json_shape_reads_back()
    {
        var v = System.Text.Json.JsonSerializer.Deserialize<ParticleBlock>("""{"burst":1,"shape":{"disc":1.5,"inner":0.5},"align":"velocity","stretch":0.2,"shader":"shaders/build/a.spv"}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) } })!;
        Assert.Equal(SpawnShape.Disc(1.5f, 0.5f), v.Shape);
        Assert.Equal(ParticleAlign.Velocity, v.Align);
        Assert.Equal("shaders/build/a.spv", v.Shader!.File);
    }

    [Theory]
    [InlineData("stretch")]
    [InlineData("both")]
    [InlineData("shader")]
    [InlineData("inner")]
    public void Mistakes_are_refused_by_field(string which)
    {
        var p = which switch
        {
            "stretch" => Spark with { Stretch = 1 },
            "both" => Spark with { SpawnRadius = 1, Shape = SpawnShape.Sphere(1) },
            "shader" => Spark with { Shader = "shaders/swirl.hlsl" },
            _ => Spark with { Shape = SpawnShape.Disc(1, inner: 2) },
        };
        var e = Assert.Throws<BlockError>(() => Compiler.Lower(new ModBlocks("vfx-abi2", 1), "bad", new VfxBlock { Particles = [p] }));
        Assert.Contains("particles[0]", e.Message);
    }

    [Fact]
    public void A_status_sets_a_kiln_vfxs_parameters_in_its_buff_row()
    {
        var (mod, _) = WithVfx(new VfxBlock { Particles = [Spark] });
        var low = Compiler.Lower(mod, "chilled", new StatusBlock
        {
            Name = "Chilled", Seconds = 8,
            Vfx = new VfxRef("burst") { Parameters = new() { ["color"] = "#4080ff", ["size"] = 2f, ["count"] = 0.5f } },
        }).Low.Json.ToJsonString();
        Assert.Contains("\"parameters.data[0]\":" + (0x40u | 0x80u << 8 | 0xffu << 16 | 0xffu << 24), low);
        Assert.Contains("\"parameters.data[1]\":" + BitConverter.SingleToUInt32Bits(2f), low);
        Assert.Contains("\"parameters.data[4]\":" + BitConverter.SingleToUInt32Bits(0.5f), low);
        Assert.Contains("\"parameters.data[15]\":" + 0x4e4c494bu, low);
    }

    [Fact]
    public void Unknown_names_and_wrong_types_are_refused()
    {
        var (mod, _) = WithVfx(new VfxBlock { Particles = [Spark] });
        foreach (var (name, value) in new (string, VfxValue)[] { ("radius", 2f), ("color", 2f), ("size", "#ffffff"), ("size", 99f), ("count", VfxValue.Int(2)) })
        {
            var e = Assert.Throws<BlockError>(() => Compiler.Lower(mod, "s", new StatusBlock { Name = "S", Seconds = 1, Vfx = new VfxRef("burst") { Parameters = new() { [name] = value } } }));
            Assert.Contains($"parameters.{name}", e.Message);
        }
    }

    [Fact]
    public void The_block_layout()
    {
        var w = VfxParameterBlock.Of(new Dictionary<string, VfxValue> { ["brightness"] = 3f, ["color"] = "#10203080" }, (f, why) => new Exception(f + why));
        Assert.Equal(16, w.Length);
        Assert.Equal(0x10u | 0x20u << 8 | 0x30u << 16 | 0x80u << 24, w[0]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(1f), w[1]);
        Assert.Equal(BitConverter.SingleToUInt32Bits(3f), w[3]);
        Assert.Equal(0x4e4c494bu, w[15]);
        Assert.Equal(["color", "size", "speed", "brightness", "count"], VfxParameterBlock.Names);
    }
}
