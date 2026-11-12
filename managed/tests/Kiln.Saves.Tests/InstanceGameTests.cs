// SPDX-License-Identifier: MIT
// The adapter to the game (InstanceGame): Kiln.Items' item records and locations mapped to the store's fields and places,
// and holder identities from kiln.spawn (the TestWorld stand-in) and the players' characters. What needs the game itself
// (reads, following, the tracker's events) is the lab's.
using Xunit;

namespace Kiln;

[Collection("entity identity")]
public sealed class InstanceGameTests : IDisposable
{
    private static readonly Guid Chest = Guid.Parse("00000003-0000-0000-0000-000000000003");
    private static readonly Guid Wolf = Guid.Parse("00000004-0000-0000-0000-000000000004");
    private static readonly Guid Pickup = Guid.Parse("00000005-0000-0000-0000-000000000005");
    private static readonly CharacterKey Hero = new(0x1122334455667788, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-000000000001"));
    private static readonly ItemInstanceRef Sword = new(new EntityRef(new EntityId(2043), (1UL << 60) | 7), new ItemId(0x5a0d0001));

    public InstanceGameTests()
    {
        TestWorld.Reset();
        TestWorld.Templates[Chest] = new(3, 9, "PropChest");        // known, saved, with a position
        TestWorld.Templates[Wolf] = new(1, 9, "Wolf");              // a creature: found within 16 m
        TestWorld.Templates[Pickup] = new(3, 11, "LootPickup");     // DoNotSave
    }

    public void Dispose() => TestWorld.Reset();

    private static ItemRecord Record(uint dye = 0, uint gem = 0) =>
        new(new ItemId(0x5a0d0001), 1, 23, 80, 100, 3200, 6400, new ItemId(dye), 2, 1, new ItemId(gem), 3);

    [Fact]
    public void ARecordMapsFieldForField()
    {
        var f = InstanceGame.FieldsOf(Record(dye: 0xd7e00001, gem: 0x6e000002));
        Assert.Equal(new InstanceFields(new ItemId(0x5a0d0001), 1, 23, 80, 100, 3200, 6400, new ItemId(0xd7e00001), 2, 1, new ItemId(0x6e000002), 3), f);
        var plain = InstanceGame.FieldsOf(Record());
        Assert.True(plain.Dye.IsNone);
        Assert.True(plain.Gem.IsNone);
        Assert.Equal(plain.Stable, InstanceGame.FieldsOf(Record()).Stable);
    }

    [Fact]
    public void APlacedItemKeepsItsHolderAndFlatSlotAsKilnItemsNumbersThem()
    {
        var holder = new EntityId(2042);
        var saved = new ItemLocation(holder, SlotId.Of(new EntityId(2050), 5), 1, 13);
        var item = InstanceGame.Map(new ItemInstanceInfo(Sword, saved, Record()));
        Assert.Equal((Sword, new InstancePlace(holder, 13)), (item.Ref, item.Place));
        // equipment, currency, customization, nested links: 256 and up, an opaque number unique in the holder
        var equipped = InstanceGame.PlaceOf(new ItemLocation(new EntityId(3), SlotId.Of(new EntityId(2060), 2), 4, 256 + 3 * 8 + 2));
        Assert.Equal(new InstancePlace(new EntityId(3), 282), equipped);
    }

    [Fact]
    public void AnItemInNoHoldersSlotHasNoPlaceAndNoHolder()
    {
        var nowhere = new ItemLocation(EntityId.None, SlotId.Of(new EntityId(2070), 0), -1, -1);
        Assert.False(nowhere.IsPlaced);
        var place = InstanceGame.PlaceOf(nowhere);
        Assert.Equal(new InstancePlace(EntityId.None, -1), place);
        Assert.Equal(HolderKind.None, InstanceGame.IdentityOf(place.Holder, _ => Hero).Kind);   // its data is for the session only
    }

    [Fact]
    public void APlayerIsFoundByItsCharacter()
    {
        var id = InstanceGame.IdentityOf(new EntityId(5), e => e.Value == 5 ? Hero : default);
        Assert.Equal((HolderKind.Player, Hero), (id.Kind, id.Character));
        var unknown = InstanceGame.IdentityOf(new EntityId(6), _ => default);
        Assert.Equal(HolderKind.Player, unknown.Kind);                 // the store keeps such a player's data for the session only
        Assert.True(unknown.Character.IsNone);
    }

    [Fact]
    public void AWorldHolderIsItsSavedTemplateAndPosition()
    {
        TestWorld.World[2042] = new SpawnLink.Ent(new EntityId(2042), Chest, null, new WorldPosition(10, 2, 30), 0);
        TestWorld.World[2044] = new SpawnLink.Ent(new EntityId(2044), Wolf, null, new WorldPosition(1, 0, 1), 0);
        TestWorld.World[2046] = new SpawnLink.Ent(new EntityId(2046), Pickup, null, new WorldPosition(5, 0, 5), 0);
        var chest = InstanceGame.IdentityOf(new EntityId(2042), _ => Hero);
        Assert.Equal((HolderKind.World, Chest, new WorldPosition(10, 2, 30), false), (chest.Kind, chest.Template, chest.Position, chest.Mobile));
        var wolf = InstanceGame.IdentityOf(new EntityId(2044), _ => Hero);
        Assert.Equal((HolderKind.World, true), (wolf.Kind, wolf.Mobile));
        Assert.Equal(HolderKind.None, InstanceGame.IdentityOf(new EntityId(2046), _ => Hero).Kind);  // never saved
        Assert.Equal(HolderKind.None, InstanceGame.IdentityOf(new EntityId(2048), _ => Hero).Kind);  // no such entity
        TestWorld.Available = false;
        Assert.Throws<KilnUnavailableException>(() => InstanceGame.IdentityOf(new EntityId(2042), _ => Hero));
    }

    [Fact]
    public void SpawnErrorsAreUnavailableNotAbsent()
    {
        var game = new InstanceGame();
        TestWorld.InspectResult = 3;
        Assert.Throws<KilnUnavailableException>(() => InstanceGame.IdentityOf(new EntityId(2042), _ => Hero));
        TestWorld.FindResult = 3;
        Assert.Throws<KilnUnavailableException>(() => game.FindHolders(Chest, default, 1));
        TestWorld.Available = false;
        Assert.Throws<KilnUnavailableException>(() => game.FindHolders(Chest, default, 1));
    }

    [Fact]
    public void HoldersAreFoundThroughKilnSpawn()
    {
        TestWorld.World[2042] = new SpawnLink.Ent(new EntityId(2042), Chest, null, new WorldPosition(10, 0, 10), 0);
        TestWorld.World[2043] = new SpawnLink.Ent(new EntityId(2043), Chest, null, new WorldPosition(30, 0, 10), 0);
        var game = new InstanceGame();
        Assert.Equal([new EntityId(2042)], game.FindHolders(Chest, new WorldPosition(10.2, 0, 10), 0.5));
        Assert.Equal(2, game.FindHolders(Chest, new WorldPosition(20, 0, 10), 16).Count);
    }
}
