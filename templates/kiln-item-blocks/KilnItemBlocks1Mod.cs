using Kiln;

namespace KilnItemBlocks1;

/// <summary>Items from building blocks: a material, food, a sword, armor, a wand, a loot table and a recipe at the game's workbench.
/// blocks/items.json makes the same kinds as data. Items are named as text here; with a catalog (`kiln catalog build`) the GameItems constants
/// complete them: GameItems.Material_T1_Twigs.</summary>
public sealed class KilnItemBlocks1Mod : Mod
{
    public override void OnLoad()
    {
        // a material: it stacks, it burns, and twigs make it
        var shard = Blocks.Define("glow-shard", new ItemBlock
        {
            Name = "Glow Shard", Description = "Faintly warm.", Stack = 100, Fuel = 60,
            Recipe = new RecipeBlock { Makes = 5, Needs = { ["Material_T1_Twigs"] = 2 } },
        });

        // food: eating it gives a buff for 10 minutes
        var sated = Blocks.Define("sated", new StatusBlock { Name = "Sated", Kind = StatusKind.Buff, Seconds = 600, Modifies = { [Stat.MoveSpeed] = 0.05 } });
        Blocks.Define("glow-stew", new ItemBlock
        {
            Name = "Glow Stew", Feeds = sated,
            Recipe = new RecipeBlock { Needs = { [shard] = 1, ["Material_T1_Twigs"] = 2 } },
        });

        // a sword: level, damage and rarity as the tooltip shows them
        Blocks.Define("glow-sword", new WeaponBlock
        {
            Name = "Glow Sword", Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Uncommon },
            OnHit = new OnHitBlock { Effects = [new EffectBlock { Damage = Damage.Fire(5), Flinch = Flinch.None }] },
            Recipe = new RecipeBlock { Needs = { [shard] = 4 } },
        });

        // armor: a helm of the warrior set
        Blocks.Define("glow-helm", new ArmorBlock
        {
            Name = "Glow Helm", Slot = ArmorSlot.Head, Set = ArmorSet.Warrior,
            Stats = new ArmorStats { Level = 5, Physical = 16, Magical = 8, Rarity = Rarity.Uncommon },
            Recipe = new RecipeBlock { Needs = { [shard] = 3 } },
        });

        // a wand: its attack is a spell of its own, no ammo and no charges
        Blocks.Define("glow-wand", new WandBlock
        {
            Name = "Glow Wand",
            Stats = new WeaponStats { Level = 5, Rarity = Rarity.Uncommon },
            Primary = new WandAttack
            {
                Cast = new CastBlock { ManaCost = 6 },
                Delivery = new ProjectileBlock { Speed = 40, Look = ProjectileLook.Fireball, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [new EffectBlock { Damage = Damage.Fire(20) }] } }] },
            },
            Recipe = new RecipeBlock { Needs = { [shard] = 5 } },
        });

        // a loot table: code rolls it (cache.Roll(), cache.Drop(at)), and a creature block can drop it (Drops = cache)
        Blocks.Define("glow-cache", new LootTableBlock
        {
            Entries =
            [
                new LootEntry { Item = shard, CountMin = 2, CountMax = 4 },
                new LootEntry { Item = "Material_T1_Metal_Scraps", Chance = 0.5 },
            ],
        });

        // a recipe that is not on an item: a game item, made at the game's workbench from your shards
        Blocks.Define("shard-resin", new RecipeBlock
        {
            Output = "Material_T1_Resin", Makes = 2, Station = StationKind.Workbench.Workshop,
            Needs = { [shard] = 1 },
        });
    }
}
