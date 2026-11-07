// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class BlockLoweringTests
{
    private static JsonObject Lower<T>(T b) where T : class, IBlock =>
        Compiler.Lower(new ModBlocks("lowering-test", 1) { ManifestClients = "mod" }, "thing", b).Low.Json;

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.MaxValue)]
    public void Tools_reject_values_that_cannot_be_written_to_game_fields(double n)
    {
        Assert.Equal("tool.durability", Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Torch", Tool = new() { Harvests = ToolClass.Torch, Durability = n } })).Field);
        Assert.Equal("tool.fishing.strength", Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Rod", Tool = new() { Harvests = ToolClass.FishingRod, Fishing = new() { Strength = n } } })).Field);
    }

    [Fact]
    public void Torch_duration_updates_both_burn_config_and_tooltip()
    {
        var j = Lower(new ItemBlock { Name = "Torch", Tool = new() { Harvests = ToolClass.Torch, Durability = 1200 } });
        Assert.Equal(1200, (double)j["itemRules"]!["impactValues.0x45ab43e7"]!);
        Assert.Equal(1200, (int)j["itemDataRules"]!["uiValues[1].value"]!);
    }

    private static JsonObject Prop(float size, float center = 0) => Lower(new PropBlock
    {
        Name = "Prop", Visual = new VisualBlock { Model = Guid.Empty.ToString() },
        Collider = new BoxCollider { Size = new Vector3(size), Center = new Vector3(center) },
    });

    private static double Hash(JsonObject j) => (double)j["templates"]![0]!["fields"]!["ColliderResourceComponent.colliders[0].joltObjects[0].hash"]!;

    [Fact]
    public void Physics_hash_tracks_geometry_and_matches_the_placement_shape()
    {
        var a = Prop(1);
        Assert.Equal(Hash(a), Hash(Prop(1)));
        Assert.NotEqual(Hash(a), Hash(Prop(2)));
        Assert.NotEqual(Hash(a), Hash(Prop(1, 1)));
        Assert.Equal(Hash(a), (double)a["itemDataRules"]!["equipment.placementColliders[0].joltObjects[0].hash"]!);
    }

    [Fact]
    public void Colliders_reject_non_finite_centers() =>
        Assert.Equal("collider.center", Assert.Throws<BlockError>(() => Prop(1, float.NaN)).Field);

    [Fact]
    public void Sound_handles_keep_their_identity_in_another_mod()
    {
        var h = Compiler.Lower(new ModBlocks("other-mod", 1) { ManifestClients = "mod" }, "hum", new SoundBlock { Clips = ["audio/a.wav"] }).Handle;
        SoundRef sound = (BlockHandle<SoundBlock>)h;
        Assert.Equal(h.Guid.ToString(), sound.Value);
    }

    [Fact]
    public void Rake_range_changes_the_cached_placement_shape()
    {
        JsonObject Rake(double range) => Lower(new ItemBlock { Name = "Rake", Tool = new() { Harvests = ToolClass.Rake, Range = range } });
        const string path = "equipment.placementColliders[0].joltObjects[0].hash";
        Assert.NotEqual((double)Rake(3)["itemDataRules"]![path]!, (double)Rake(5)["itemDataRules"]![path]!);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Sound_fields_must_be_finite(double n)
    {
        Assert.Equal("volume", Assert.Throws<BlockError>(() => Lower(new SoundBlock { Clips = ["audio/a.wav"], Volume = n })).Field);
        Assert.Equal("range", Assert.Throws<BlockError>(() => Lower(new SoundBlock { Clips = ["audio/a.wav"], Range = [0, n] })).Field);
    }
}
