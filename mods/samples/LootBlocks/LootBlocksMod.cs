// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace LootBlocks;

/// <summary>
/// A loot table, creatures that drop one, and recipes that are not on an item. blocks/twins.json says the same in data.
/// A table is Kiln's: it is rolled on the server where a creature of the block dies, and by code (<c>cache.Roll()</c>, <c>cache.Drop(at)</c>, <c>cache.Give(chest)</c>).
/// </summary>
public sealed class LootBlocksMod : Mod
{
    public override void OnLoad()
    {
        var shard = Blocks.Define("ember-shard", new ItemBlock { Name = "Ember Shard", Description = "Still warm.", Stack = 100 });

        // what can come up: every entry tries its own chance (the default), and one roll is all the table does
        var cache = Blocks.Define("ember-cache", new LootTableBlock
        {
            Entries =
            [
                new LootEntry { Item = shard, CountMin = 2, CountMax = 4 },                                          // always, 2 to 4
                new LootEntry { Item = GameItems.Material_T1_Metal_Scraps, CountMin = 1, CountMax = 3, Chance = 0.5 }, // half the time
            ],
        });

        // three rolls, each picks one entry by weight: twigs three times as often as logs
        Blocks.Define("scrap-salvage", new LootTableBlock
        {
            Rolls = 3, Pick = LootPick.One,
            Entries = [new LootEntry { Item = GameItems.Material_T1_Twigs, CountMin = 2, Weight = 3 }, new LootEntry { Item = GameItems.Material_T1_Wood_Logs, Weight = 1 }],
        });

        // creatures that drop a table: by handle (or name), or one written in place
        Blocks.Define("ember-scavenger", new CreatureBlock { Name = "Ember Scavenger", Body = Bodies.Vukah, Stats = new CreatureStats { Level = 6 }, Drops = cache });
        Blocks.Define("log-carrier", new CreatureBlock
        {
            Name = "Log Carrier", Body = Bodies.Vukah,
            Drops = new LootTableBlock { Entries = [new LootEntry { Item = GameItems.Material_T1_Wood_Logs, CountMin = 5 }] },
        });

        // recipes that are not on an item: a game item at the game's workbench, and an item of yours at it
        Blocks.Define("ember-charcoal", new RecipeBlock
        {
            Output = GameItems.Material_T1_Charcoal, Makes = 2, Station = StationKind.Workbench.Workshop,
            Needs = { [shard] = 2, [GameItems.Material_T1_Wood_Logs] = 1 },
        });
        Blocks.Define("shard-from-twigs", new RecipeBlock { Output = shard, Station = StationKind.Workbench.Workshop, Needs = { [GameItems.Material_T1_Twigs] = 3 } });
    }
}
