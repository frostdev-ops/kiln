// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace ItemBlocks;

/// <summary>
/// One of everything item blocks make, said once. The look, the stats, the class and the on-hit are parts: blocks of their own, reused below.
/// blocks/twins.json says the same in data.
/// </summary>
public sealed class ItemBlocksMod : Mod
{
    public override void OnLoad()
    {
        // what happens, from the core blocks' effects
        var burn = Blocks.Define("burn", new StatusBlock
        {
            Name = "Burn", Description = "It keeps burning.", Seconds = 6, TickEvery = 2, Look = StatusLook.Fire,
            OnTick = [new EffectBlock { Damage = Damage.Fire(2), Flinch = Flinch.None }],
        });
        var sear = Blocks.Define("sear", new EffectBlock { Damage = Damage.Fire(12), Flinch = Flinch.Light, Applies = burn });

        // the parts: write one, use it everywhere
        var emberLook = Blocks.Define("ember-look", new VisualBlock { Like = GameItems.Weapon_T4_1H_SwordEpic_02 });
        var veteran = Blocks.Define("veteran", new WeaponStats { Level = 10, Power = 60, Rarity = Rarity.Rare });
        var searing = Blocks.Define("searing", new OnHitBlock { Effects = [sear] });
        var emberBlade = Blocks.Define("ember-blade", WeaponClass.Sword with { Visual = emberLook, Stats = veteran });

        // things that go in a bag
        var shard = Blocks.Define("ember-shard", new ItemBlock
        {
            Name = "Ember Shard", Description = "Still warm.", Stack = 100, Fuel = 120,
            Recipe = new RecipeBlock { Makes = 5, Needs = { [GameItems.Material_T1_Twigs] = 1 } },
        });
        Blocks.Define("healing-draught", new ItemBlock
        {
            Name = "Healing Draught", Heals = 150, Stack = 10,
            Recipe = new RecipeBlock { Makes = 2, Needs = { [shard] = 1 } },
        });
        Blocks.Define("ember-tonic", new ItemBlock
        {
            Name = "Ember Tonic", OnUse = [new EffectBlock { Heal = 40 }],
            Recipe = new RecipeBlock { Needs = { [shard] = 2 } },
        });
        var warmth = Blocks.Define("warmth", new StatusBlock { Name = "Warmth", Kind = StatusKind.Buff, Seconds = 600, Modifies = { [Stat.MoveSpeed] = 0.05 } });
        Blocks.Define("ember-stew", new ItemBlock
        {
            Name = "Ember Stew", Feeds = warmth,
            Recipe = new RecipeBlock { Needs = { [shard] = 1, [GameItems.Material_T1_Twigs] = 2 } },
        });
        Blocks.Define("ember-arrow", new ItemBlock
        {
            Name = "Ember Arrow", Level = 5, Stack = 200,
            Ammo = new AmmoBlock { Damage = Damage.Pierce(8) + Damage.Fire(4) },
            Recipe = new RecipeBlock { Makes = 20, Needs = { [shard] = 1, [GameItems.Material_T1_Twigs] = 5 } },
        });
        Blocks.Define("ember-axe", new ItemBlock
        {
            Name = "Ember Axe", Level = 10, Rarity = Rarity.Uncommon,
            Tool = new ToolBlock { Harvests = Harvest.Wood, Power = 30, StaminaCost = 2 },
            Recipe = new RecipeBlock { Needs = { [shard] = 3 } },
        });
        Blocks.Define("ember-pick", new ItemBlock
        {
            Name = "Ember Pick", Level = 10,
            Tool = new ToolBlock { Harvests = Harvest.Stone, Power = 18 },
            Recipe = new RecipeBlock { Needs = { [shard] = 3 } },
        });
        // the same pick, worn 7 by each of the game's debits instead of 1 (the game's own ember pick above keeps 1). Per debit,
        // not per swing: a swing whose hit the game debits twice spends 14. Set where the world runs; on a game client it waits
        // for its guided check (16b-g).
        Blocks.Define("brittle-ember-pick", new ItemBlock
        {
            Name = "Brittle Ember Pick", Level = 10,
            Tool = new ToolBlock { Harvests = Harvest.Stone, Power = 18, WearPerDebit = 7 },
            Recipe = new RecipeBlock { Needs = { [shard] = 2 } },
        });

        // a weapon of every class. Stats are the tooltip's: level and damage, made exactly so
        foreach (var (name, display, cls) in new[]
        {
            ("sword", "Ember Sword", WeaponClass.Sword), ("waraxe", "Ember War Axe", WeaponClass.Axe), ("mace", "Ember Mace", WeaponClass.Mace), ("greatsword", "Ember Greatsword", WeaponClass.Greatsword),
            ("greataxe", "Ember Greataxe", WeaponClass.Greataxe), ("hammer", "Ember Hammer", WeaponClass.Hammer), ("daggers", "Ember Daggers", WeaponClass.Daggers), ("bow", "Ember Bow", WeaponClass.Bow),
            ("staff", "Ember Staff", WeaponClass.Staff),
        })
            Blocks.Define(name, new WeaponBlock
            {
                Name = display, Class = cls,
                Stats = new WeaponStats { Level = 5, Power = 30, Rarity = Rarity.Rare },
                Recipe = new RecipeBlock { Needs = { [shard] = 4 } },
            });
        Blocks.Define("ember-guard", new WeaponBlock
        {
            Name = "Ember Guard", Class = WeaponClass.Shield,
            Stats = new WeaponStats { Level = 5, Block = 0.3, Rarity = Rarity.Uncommon },
            Recipe = new RecipeBlock { Needs = { [shard] = 4 } },
        });

        // the parts at work: the class brings its look and stats; hits work in hand, kills too
        Blocks.Define("ember-edge", new WeaponBlock
        {
            Name = "Ember Edge", Description = "Every blow sears.", Class = emberBlade, OnHit = searing, OnKill = [new EffectBlock { Area = new AreaBlock { Radius = 4, Effects = [sear] } }],
            Perks = [GamePerks.Perk_Damage_Fire],
            Recipe = new RecipeBlock { Needs = { [shard] = 6 } },
        });

        // a wand's spells are its own: no ammo item, no charges. The primary's main damage is the wand's power
        Blocks.Define("ember-wand", new WandBlock
        {
            Name = "Ember Wand",
            Stats = new WeaponStats { Level = 5, Rarity = Rarity.Uncommon },
            Primary = new WandAttack
            {
                Cast = new CastBlock { ManaCost = 6 },
                Delivery = new ProjectileBlock { Speed = 40, Look = ProjectileLook.Fireball, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [new EffectBlock { Damage = Damage.Fire(25) }] } }] },
            },
            Heavy = new WandAttack
            {
                Cast = new CastBlock { ManaCost = 20 },
                Delivery = new ProjectileBlock { Speed = 25, Gravity = 0.5f, Look = ProjectileLook.Fireball, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 4, Effects = [new EffectBlock { Damage = Damage.Fire(70) }, sear] } }] },
            },
            Special = new WandAttack
            {
                Cast = new CastBlock { ManaCost = 0, ReleaseAt = 0.4 },
                Delivery = new ProjectileBlock { Speed = 60, Gravity = 0, Look = ProjectileLook.Fireball, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = Damage.Fire(40), Knockback = Knockback.Big }] } }] },
            },
            Recipe = new RecipeBlock { Needs = { [shard] = 5 } },
        });

        // worn things
        var embersteel = Blocks.Define("embersteel", new ArmorStats { Level = 5, Physical = 16, Magical = 8, Rarity = Rarity.Uncommon });
        Blocks.Define("ember-helm", new ArmorBlock { Name = "Ember Helm", Slot = ArmorSlot.Head, Set = ArmorSet.Warrior, Stats = embersteel, Recipe = new RecipeBlock { Needs = { [shard] = 3 } } });
        Blocks.Define("ember-mail", new ArmorBlock
        {
            Name = "Ember Mail", Slot = ArmorSlot.Chest, Set = ArmorSet.Warrior,
            Stats = new ArmorStats { Level = 5, Physical = 30, Magical = 12, Resist = new ArmorResist { Fire = 6 }, Bonus = new Dictionary<EquipBonus, double> { [EquipBonus.Health] = 60 }, Rarity = Rarity.Rare },
            OnHitTaken = [new EffectBlock { Damage = Damage.Fire(5), Flinch = Flinch.None }],
            Recipe = new RecipeBlock { Needs = { [shard] = 6 } },
        });
        Blocks.Define("ember-band", new ArmorBlock
        {
            Name = "Ember Band", Slot = ArmorSlot.Ring,
            Stats = new ArmorStats { Level = 5, Bonus = new Dictionary<EquipBonus, double> { [EquipBonus.Mana] = 30, [EquipBonus.MagicDamage] = 0.05 } },
            Recipe = new RecipeBlock { Needs = { [shard] = 2 } },
        });

        // gear that scales with where it is found has no recipe: a roll gives it a level and a rarity of its own
        Blocks.Define("ember-relic", new WeaponBlock
        {
            Name = "Ember Relic", Class = WeaponClass.Sword,
            Loot = new LootBlock { Rolled = true, Like = GameItems.Weapon_T1_1H_Sword_01 },
        });
        // a rename says what it was, and ids and saves don't notice
        Blocks.Define("ember-charm", new ItemBlock { Name = "Ember Charm", Comfort = 2, Was = "ember-token" });

        // somewhere to make things, and somewhere to find them
        var bench = Blocks.Define("ember-bench", new StationBlock
        {
            Name = "Ember Bench", Description = "A workbench that never cools.",
            Recipe = new RecipeBlock { Needs = { [shard] = 10 } },
        });
        Blocks.Define("ember-gem", new ItemBlock
        {
            Name = "Ember Gem", Source = "Pried from cooled embers", Rarity = Rarity.Rare, Stack = 20,
            Texts = new Dictionary<string, ItemTexts> { ["de"] = new() { Name = "Glutstein", Description = "Noch warm." } },
            Visual = new VisualBlock { Icon = "item:" + GameItems.Material_T1_Metal_Scraps, Dropped = GameItems.Material_T1_Metal_Scraps, Tint = "#ff6600" },
            Recipe = new RecipeBlock { Station = bench, Needs = { [shard] = 5 }, Seconds = 4 },
            Loot = new LootBlock
            {
                Like = GameItems.Material_T1_Metal_Scraps, StackMin = 1, StackMax = 2,
                Tables = [new DropPlace { DropsLike = GameItems.Material_T1_Metal_Scraps, CountMin = 1, CountMax = 2, Chance = 0.25 }],
            },
        });
    }
}
