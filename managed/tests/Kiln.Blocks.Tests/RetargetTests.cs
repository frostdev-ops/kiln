// SPDX-License-Identifier: MIT
// AnimationBlock.Retarget: its JSON shape and what Define checks before the cook pairs the joints.
using Xunit;

namespace Kiln.BlockTests;

public sealed class RetargetTests
{
    private static BlockContext Ctx() => new(new ModBlocks("retarget-test", 1), "animation moves");

    [Fact]
    public void Json_reads_the_cooks_shape()
    {
        var a = BlockJson.Parse<AnimationBlock>("""
            { "clips": "models/pack.glb", "skeleton": "golem", "idle": "Idle",
              "retarget": { "map": { "mixamorig:Hips": "pelvis" }, "byName": false, "translate": ["mixamorig:Hips"], "scale": 1.2,
                            "reference": { "animation": "TPose", "at": 0.5 } } }
            """);
        Assert.NotNull(a.Retarget);
        Assert.Equal("pelvis", a.Retarget!.Map["mixamorig:Hips"]);
        Assert.False(a.Retarget.ByName);
        Assert.Equal(["mixamorig:Hips"], a.Retarget.Translate!);
        Assert.Equal(1.2, a.Retarget.Scale);
        Assert.Equal(new RetargetPose("TPose", 0.5), a.Retarget.Reference);
    }

    [Fact]
    public void Without_retarget_nothing_is_checked()
    {
        Retargets.Check(Ctx(), new AnimationBlock { Clips = "models/a.glb" }, "");
    }

    [Fact]
    public void A_good_one_passes()
    {
        Retargets.Check(Ctx(), new AnimationBlock { Clips = "models/pack.glb", Skeleton = "golem", Retarget = new() { Map = { ["Hips"] = "pelvis" } } }, "");
    }

    [Theory]
    [InlineData("noskeleton", "say which skeleton")]
    [InlineData("noclips", "say clips")]
    [InlineData("scale", "not above 0")]
    [InlineData("twice", "paired twice")]
    [InlineData("nothing", "nothing pairs")]
    [InlineData("reference", "a time from 0")]
    public void Mistakes_name_the_field(string kind, string message)
    {
        var good = new AnimationBlock { Clips = "models/pack.glb", Skeleton = "golem", Retarget = new() { Map = { ["Hips"] = "pelvis" } } };
        var bad = kind switch
        {
            "noskeleton" => good with { Skeleton = null },
            "noclips" => good with { Clips = null },
            "scale" => good with { Retarget = good.Retarget! with { Scale = 0 } },
            "twice" => good with { Retarget = new() { Map = { ["Hips"] = "pelvis", ["Root"] = "pelvis" } } },
            "nothing" => good with { Retarget = new() { ByName = false } },
            _ => good with { Retarget = good.Retarget! with { Reference = new RetargetPose("TPose", -1) } },
        };
        var e = Assert.Throws<BlockError>(() => Retargets.Check(Ctx(), bad, "animation"));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
        Assert.Contains("animation.retarget", e.Message, StringComparison.Ordinal);
    }
}
