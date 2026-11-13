// SPDX-License-Identifier: MIT
// The kit's recording fake of kiln.items: Kiln.Items' own code, an inventory per fake player and per container.
using System.Runtime.CompilerServices;
using TkFakes;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class ItemsFakeTests
{
    private const ulong AdaSteam = 76561198000000701;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static (KilnHarness Kiln, FakePlayer Ada) Start(int playerInventories = 5)
    {
        var kiln = new KilnHarness();
        kiln.Load<TkFakesMod>();
        kiln.Items.AddItem("Material_T1_Twigs", 50);
        kiln.Items.AddItem("Weapon_Sword", 1, ItemCategory.Weapons, gear: true, levelMin: 1, levelMax: 10);
        kiln.Items.PlayerInventories = playerInventories;
        var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(0, 0, 0));
        kiln.Step(2);
        return (kiln, ada);
    }

    private static string Last(KilnHarness kiln, string prefix) => kiln.Lines("tk-fakes", prefix).Last();

    [Fact]
    public void GiveAndTakeRunAtTheNextStepWithStacksGearAndEvents()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        var twigs = kiln.Items.IdOf("Material_T1_Twigs");
        kiln.RunCommand(ada, "tkf-give me Material_T1_Twigs 70");
        Assert.Equal(0, ada.Inventory.Count(twigs));  // the transaction runs at the start of the next step
        kiln.Step();
        Assert.Equal("give done: Done 70/70 changes 2 steps 0 dry False: ", Last(kiln, "give done:"));
        Assert.Equal("event given: 70 x Material_T1_Twigs to 1 by tk-fakes", Last(kiln, "event given:"));
        Assert.Equal([50, 20], ada.Inventory.Slots.Where(s => !s.IsEmpty).Select(s => s.Count));
        kiln.RunCommand(ada, "tkf-give me Material_T1_Twigs 40");  // stacks are topped up first
        kiln.Step();
        Assert.Equal([50, 50, 10], ada.Inventory.Slots.Where(s => !s.IsEmpty).Select(s => s.Count));
        Assert.Equal("read: 40 slots: Material_T1_Twigs x50; Material_T1_Twigs x50; Material_T1_Twigs x10", Said(kiln.RunCommand(ada, "tkf-read me")));

        kiln.RunCommand(ada, "tkf-take me Material_T1_Twigs 105");
        kiln.Step();
        Assert.Equal("take done: Done 105/105 changes 3 steps 0 dry False: ", Last(kiln, "take done:"));
        Assert.Equal(5, ada.Inventory.Count("Material_T1_Twigs"));
        kiln.RunCommand(ada, "tkf-take me Material_T1_Twigs 6");  // all or nothing
        kiln.Step();
        Assert.Equal("take done: NotEnough 0/6 changes 0 steps 0 dry False: only 5 of 6 are there; nothing was taken", Last(kiln, "take done:"));
        Assert.Equal(5, ada.Inventory.Count(twigs));

        kiln.RunCommand(ada, "tkf-give me Weapon_Sword 2 gear");  // gear: one per slot, an item entity each
        kiln.Step();
        var swords = ada.Inventory.Slots.Where(s => s.Item == kiln.Items.IdOf("Weapon_Sword")).ToList();
        Assert.Equal(2, swords.Count);
        Assert.All(swords, s => Assert.Equal((1, 7, ItemRarity.Rare), (s.Count, s.Level, s.Rarity)));
        Assert.All(swords, s => Assert.True(kiln.Entities.Exists(s.ItemEntity)));
        Assert.Contains("Weapon_Sword x1 gear L7 Rare", Said(kiln.RunCommand(ada, "tkf-read me")));
        kiln.RunCommand(ada, "tkf-take me Weapon_Sword 1");
        kiln.Step();
        Assert.False(kiln.Entities.Exists(swords[0].ItemEntity));  // the game destroys gear taken out
        Assert.True(kiln.Entities.Exists(swords[1].ItemEntity));
        Assert.Equal([false, false, true, true, false, true], kiln.Items.Calls.Select(c => c.IsTake));
        Assert.All(kiln.Items.Calls, c => Assert.Equal("tk-fakes", c.ModId));
        Assert.Equal(ItemsStatus.NotEnough, kiln.Items.Calls[3].Status);
    }

    [Fact]
    public void AFullInventoryRefusesAsTheGameDoes()
    {
        var (kiln, ada) = Start(playerInventories: 1);
        using var _ = kiln;
        kiln.RunCommand(ada, "tkf-give me Material_T1_Twigs 500");
        kiln.Step();
        Assert.StartsWith("give done: Partial 400/500 changes 8 steps 0 dry False: only 400 of 500 fit", Last(kiln, "give done:"));
        kiln.RunCommand(ada, "tkf-give me Material_T1_Twigs 1");
        kiln.Step();
        Assert.StartsWith("give done: NoRoom 0/1 changes 0 steps 0 dry False: no room", Last(kiln, "give done:"));
        Assert.Equal(400, ada.Inventory.Count("Material_T1_Twigs"));

        var chest = kiln.Entities.Create(new WorldPosition(2, 0, 0));
        kiln.Items.AddContainer(chest);
        kiln.RunCommand(ada, $"tkf-give {chest.Value} Weapon_Sword 9 all");  // 8 slots: all or nothing
        kiln.RunCommand(ada, $"tkf-give {chest.Value} Weapon_Sword 3 dry");
        kiln.Step();
        var lines = kiln.Lines("tk-fakes", "give done:");
        Assert.StartsWith("give done: NoRoom 0/9", lines[^2]);
        Assert.Equal("give done: Done 3/3 changes 3 steps 0 dry True: ", lines[^1]);
        Assert.Equal(0, kiln.Items.Of(chest)!.Count("Weapon_Sword"));
        Assert.True(kiln.Items.Calls[^1].DryRun);

        // unknown item, no inventory: results, never exceptions
        var unknown = kiln.Run(() => Items.Give(chest, new ItemId(0x1234), 1));
        var nowhere = kiln.Run(() => Items.Give(new EntityId(4000), kiln.Items.IdOf("Material_T1_Twigs"), 1));
        kiln.Step();
        Assert.Equal(ItemsStatus.UnknownItem, unknown.Result.Status);
        Assert.Equal(ItemsStatus.NoInventory, nowhere.Result.Status);
        Assert.Equal(ItemsStatus.UnknownItem, kiln.Run(() => Items.Give(chest, "Nope", 1)).Result.Status);  // by name: Kiln.Items' own lookup
    }

    [Fact]
    public void ContainersAreFoundReadAndFilledByTheTest()
    {
        var (kiln, ada) = Start();
        using var _ = kiln;
        var chest = kiln.Entities.Create(new WorldPosition(3, 0, 4));
        var inv = kiln.Items.AddContainer(chest, inventories: 3);
        Assert.Equal(60, inv.Put(kiln.Items.IdOf("Material_T1_Twigs"), 60));
        Assert.Equal($"chests: {chest.Value}; players: 1", Said(kiln.RunCommand("tkf-chests")));
        Assert.Equal("read: 24 slots: Material_T1_Twigs x50; Material_T1_Twigs x10", Said(kiln.RunCommand($"tkf-read {chest.Value}")));
        var read = kiln.Run(() => Items.Read(chest))!;
        Assert.Equal(3, read.Inventories.Count);
        Assert.Equal(chest, read.Inventories[0].Item1);  // a chest's first inventory is the chest itself
        Assert.Equal(["Material_T1_Twigs", "Weapon_Sword"], kiln.Run(() => Items.All.Select(i => i.Name).ToArray()));
        Assert.True(kiln.Run(() => Items.Lookup("weapon_sword"))!.IsGear);
        Assert.Equal("read: none", Said(kiln.RunCommand("tkf-read 4000")));
        inv.Clear();
        Assert.Empty(inv.Slots.Where(s => !s.IsEmpty));
        kiln.Entities.Destroy(chest);
        Assert.Null(kiln.Items.Of(chest));  // gone with its entity
    }

    [Fact]
    public void ATestCanSwitchItOffAndTheNextHarnessHasItsOwnItems()
    {
        var (kiln, ada) = Start();
        using (kiln)
        {
            kiln.Capabilities.Override("items.transaction", false);
            var off = kiln.Run(() => Items.Give(ada.Entity, kiln.Items.IdOf("Material_T1_Twigs"), 1));
            Assert.Equal(ItemsStatus.Unavailable, off.Result.Status);
            kiln.Capabilities.ClearOverride("items.transaction");
            kiln.Capabilities.Override("kiln.items", false);
            Assert.Throws<NotSupportedException>(() => kiln.Run(() => Items.Read(ada.Entity)));
            kiln.Capabilities.ClearOverride("kiln.items");
            Assert.False(kiln.Capabilities.Is("items.equip"));
            Assert.Contains("offline", kiln.Capabilities.Why("items.move"));
        }
        using var next = new KilnHarness();
        next.Step();
        Assert.Empty(next.Run(() => Items.All));
        Assert.Null(next.Run(() => Items.Lookup("Material_T1_Twigs")));
        next.Items.AddItem("Food_Bread", 20, ItemCategory.Consumables);
        Assert.Equal(["Food_Bread"], next.Run(() => Items.All.Select(i => i.Name).ToArray()));
        Assert.Empty(next.Items.Calls);
    }

    [Fact]
    public void TheTableMirrorsTheHeaderAsTheServiceReadsIt()
    {
        Assert.Equal((40, 24, 592, 32, 184, 64, 200, 104), (Unsafe.SizeOf<ItemsAbi.Request>(), Unsafe.SizeOf<ItemsAbi.Change>(), Unsafe.SizeOf<ItemsAbi.Result>(),
            Unsafe.SizeOf<ItemsAbi.Slot>(), Unsafe.SizeOf<ItemsAbi.Inventory>(), Unsafe.SizeOf<ItemsAbi.ItemInfo>(), Unsafe.SizeOf<ItemsAbi.Status>(),
            Unsafe.SizeOf<ItemsAbi.Table>()));
        var native = typeof(Items).Assembly.GetType("Kiln.ItemsNative", true)!;
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Request>(), Mirror.Size(native, "Request"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Change>(), Mirror.Size(native, "Change"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Result>(), Mirror.Size(native, "Result"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Slot>(), Mirror.Size(native, "Slot"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Inventory>(), Mirror.Size(native, "Inventory"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.ItemInfo>(), Mirror.Size(native, "ItemInfo"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Status>(), Mirror.Size(native, "Status"));
        Assert.Equal(Unsafe.SizeOf<ItemsAbi.Table>(), Mirror.Size(native, "Table"));
    }
}
