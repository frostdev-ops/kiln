// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln;

[CollectionDefinition("entity identity", DisableParallelization = true)]
public sealed class EntityIdentityCollection;

[Collection("entity identity")]
public sealed class EntityIdentityTests
{
    private static readonly Guid Sword = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid Chest = Guid.Parse("00000002-0000-0000-0000-000000000002");
    private static readonly Dictionary<string, JsonSerializerOptions> Options = [];

    public EntityIdentityTests()
    {
        SpawnLink.Reset();
        SpawnLink.Templates[Sword] = (5, false, "WeaponSwordPide", false);
        SpawnLink.Templates[Chest] = (3, false, "PropChest", true);
    }

    private static EntityId Place(uint id, Guid template, double x = 0)
    {
        var entity = new EntityId(id);
        SpawnLink.World[id] = new SpawnLink.Ent(entity, template, null, new WorldPosition(x, 0, 0), 0);
        return entity;
    }

    private static void Tag(EntityTable t, EntityId e, string tag) => t.Attach(e).ByMod["test"] = new()
    {
        ["engraving"] = new WorldStore.Slot { Json = JsonSerializer.Serialize(tag) },
    };

    private static EntityTable Reload(EntityTable t)
    {
        var reloaded = new EntityTable();
        foreach (var (mod, data) in t.Stage(Options)) reloaded.Absorb(mod, JsonNode.Parse(data)!.AsObject());
        reloaded.StartMatching();
        return reloaded;
    }

    [Fact]
    public void TwoColocatedSwordsKeepSeparateSessionDataAndNeverCrossAssignAfterRestart()
    {
        var t = new EntityTable();
        var a = Place(2001, Sword);
        var b = Place(2002, Sword);
        Tag(t, a, "level3");
        Tag(t, b, "level5");
        Assert.Equal("\"level3\"", t.Find(a)!.ByMod["test"]["engraving"].Json);
        Assert.Equal("\"level5\"", t.Find(b)!.ByMod["test"]["engraving"].Json);
        Assert.Empty(t.Stage(Options));
        var loaded = Reload(t);
        SpawnLink.World.Clear();
        a = Place(3001, Sword);
        b = Place(3002, Sword);
        loaded.Tick();
        Assert.Null(loaded.FindOrMatch(a));
        Assert.Null(loaded.FindOrMatch(b));
        Assert.Equal(0, loaded.LiveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyItemSidecarCannotAttachToAnArbitrarySword(bool periodic)
    {
        // Old versions wrote both swords with the same unsafe positional identity.
        var loaded = new EntityTable();
        var json = $$$"""{"entities":[{"template":"{{{Sword}}}","pos":[0,0,0],"keys":{"engraving":"level3"}},{"template":"{{{Sword}}}","pos":[0,0,0],"keys":{"engraving":"level5"}}]}""";
        loaded.Absorb("test", JsonNode.Parse(json)!.AsObject());
        var a = Place(3001, Sword);
        var b = Place(3002, Sword);
        loaded.StartMatching();
        if (periodic) loaded.Tick();
        Assert.Null(loaded.FindOrMatch(a));
        Assert.Null(loaded.FindOrMatch(b));
        Tag(loaded, a, "new-session");
        Assert.Equal("\"new-session\"", loaded.Find(a)!.ByMod["test"]["engraving"].Json);
        Assert.Null(loaded.FindOrMatch(b));
    }

    [Fact]
    public void SavedWorldEntityStillMatchesAcrossRenumbering()
    {
        var t = new EntityTable();
        var old = Place(2001, Chest, 42);
        Tag(t, old, "chest-data");
        var loaded = Reload(t);
        SpawnLink.World.Clear();
        var current = Place(3001, Chest, 42);
        loaded.Tick();
        Assert.Equal("\"chest-data\"", loaded.FindOrMatch(current)!.ByMod["test"]["engraving"].Json);
        Assert.Equal(1, loaded.Matched);
    }
}
