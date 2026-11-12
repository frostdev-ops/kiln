// SPDX-License-Identifier: MIT
// A look's animated model is for what is held: a placed thing (a prop, a station, a piece of furniture) refuses it by block and field.
using Xunit;

namespace Kiln.BlockTests;

public sealed class AnimatedPlacedTests
{
    private static readonly AnimatedModel Moving = new() { Model = "bell_anim", Graph = "bell" };

    private static BlockError Refused<T>(T b) where T : class, IBlock =>
        Assert.Throws<BlockError>(() => Compiler.Lower(new ModBlocks("placed-test", 1) { ManifestClients = "mod" }, "thing", b));

    [Fact]
    public void A_placed_thing_refuses_an_animated_look()
    {
        var prop = Refused(new PropBlock { Name = "Bell", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01", Animated = Moving } });
        Assert.Equal("visual.animated", prop.Field);
        Assert.Contains("Bell: an animated model is for what is held", prop.Message);
        Assert.Equal("visual.animated", Refused(new StationBlock { Name = "Forge", Visual = new VisualBlock { Animated = Moving } }).Field);
        Assert.Equal("visual.animated", Refused(new ContainerBlock { Name = "Chest", Visual = new VisualBlock { Animated = Moving } }).Field);
        // the same look without it is a prop as before
        Compiler.Lower(new ModBlocks("placed-test", 1) { ManifestClients = "mod" }, "thing", new PropBlock { Name = "Bell", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" } });
    }
}
