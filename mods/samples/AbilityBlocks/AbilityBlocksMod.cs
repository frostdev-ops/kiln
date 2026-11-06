// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace AbilityBlocks;

/// <summary>
/// Ember Path: what a character casts, earns and does, said once. The same burn is a perk's, a talent's and an ability's.
/// </summary>
public sealed class AbilityBlocksMod : Mod
{
    public override void OnLoad()
    {
        var kindled = Blocks.Define("kindled", new StatusBlock
        {
            Name = "Kindled",
            Description = "Burning a little every second.",
            Seconds = 4,
            OnTick = [new EffectBlock { Damage = Damage.Fire(3), Flinch = Flinch.None }],
        });
        var burn = Blocks.Define("burn", new EffectBlock { Damage = Damage.Fire(10), Flinch = Flinch.None, Applies = kindled });

        // An enhancement: it works once the sword has been upgraded to unlock it, as the game's own.
        var emberEdge = Blocks.Define("ember-edge", new PerkBlock { Name = "Ember Edge", Description = "Hits may set the target alight.", OnHit = [new EffectBlock { Chance = 0.25, Then = [burn] }] });
        var might = Blocks.Define("ember-might", new PerkBlock { Name = "Ember Might", Modifies = { [Stat.Strength] = 3 } });
        Blocks.Define("ember-blade", new WeaponBlock
        {
            Name = "Ember Blade",
            Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 25, Rarity = Rarity.Uncommon },
            Perks = [emberEdge, might],
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 3 } },
        });

        // On G: fire in front, for those who have the talent below.
        var flameStep = Blocks.Define("flame-step", new AbilityBlock
        {
            Name = "Flame Step",
            Key = "G",
            Command = "flamestep",
            Cast = new CastBlock { ManaCost = 20, StaminaCost = 10 },
            Cooldown = 6,
            Delivery = new ConeBlock { Range = 8, Angle = 70, Effects = [burn, new EffectBlock { Damage = Damage.Fire(20), Knockback = Knockback.Small }] },
        });

        // A charm that never runs out: a bolt that turns toward the nearest enemy.
        Blocks.Define("ember-bolt", new AbilityBlock
        {
            Name = "Ember Bolt",
            Command = "emberbolt",
            Aim = AbilityTargeting.NearestEnemy,
            Range = 30,
            Cooldown = 3,
            Cast = new CastBlock { ManaCost = 10, Charges = 0 },
            Item = new ItemBlock { Name = "Ember Charm", Description = "Sends a bolt after the nearest enemy.", Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 2 } } },
            Delivery = new ProjectileBlock { Speed = 18, Look = ProjectileLook.Fireball, Homing = new HomingBlock { TurnSpeed = 60 }, OnHit = [burn] },
        });

        Blocks.Define("ember-path", new TalentTreeBlock
        {
            Name = "Ember Path",
            Menu = "Ember Path",
            Key = "K",
            Nodes =
            {
                ["kindle"] = new TalentBlock { Name = "Kindle", MaxLevel = 3, Modifies = { [Stat.Intelligence] = 1 } },
                ["hearth"] = new TalentBlock { Name = "Hearth", Description = "Your hits burn.", Requires = ["kindle"], OnHit = [new EffectBlock { Chance = 0.2, Then = [burn] }] },
                ["step"] = new TalentBlock { Name = "Flame Step", Description = "G sends fire ahead of you.", Requires = ["kindle"], Cost = 2, Unlocks = flameStep },
            },
        });

        // Two staff spells: a fan of fire in front, and a bolt that seeks. A cast takes two charges of the fan.
        Blocks.Define("ember-fan", new SpellBlock
        {
            Name = "Ember Fan",
            Cast = new CastBlock { ManaCost = 25, Charges = 2 },
            Delivery = new ConeBlock { Range = 7, Angle = 80, Look = ProjectileLook.Fireball, Effects = [new EffectBlock { Damage = Damage.Fire(30), Knockback = Knockback.Small }] },
            Recipe = new RecipeBlock { Makes = 20, Needs = { [GameItems.Material_T1_Twigs] = 1 } },
        });
        Blocks.Define("ember-seeker", new SpellBlock
        {
            Name = "Ember Seeker",
            Cast = new CastBlock { ManaCost = 15, StaminaCost = 5 },
            Delivery = new ProjectileBlock { Speed = 20, Look = ProjectileLook.Fireball, Homing = new HomingBlock { TurnSpeed = 60 }, OnHit = [burn] },
            Recipe = new RecipeBlock { Makes = 20, Needs = { [GameItems.Material_T1_Twigs] = 1 } },
        });

        // A node in the game's own tree, next to its central start.
        Blocks.Define("ember-ward", new TalentBlock
        {
            Name = "Ember Ward",
            Description = "+1 Strength per level.",
            MaxLevel = 3,
            Modifies = { [Stat.Strength] = 1 },
            Game = new GameTreePlace { From = ["0x8efcc472"], Offset = [170, -40] },
        });

        // What a player does.
        Blocks.Define("updraft", new StatusBlock { Name = "Updraft", Kind = StatusKind.Buff, While = PlayerStateId.Gliding, Modifies = { [Stat.StaminaUseGlide] = -0.5 } });
        Blocks.Define("hearthside", new TriggerBlock { On = PlayerStateId.Sitting, Do = [new EffectBlock { Restores = Restore.Mana(15) }], Cooldown = 10 });
        Blocks.Define("ember-bun", new ItemBlock
        {
            Name = "Ember Bun",
            Description = "Restores mana, but only away from a fight.",
            Kind = ItemKind.Consumable,
            OnUse = [new EffectBlock { Heal = 5 }],
            OnEat = [new EffectBlock { Restores = Restore.Mana(40) }],
            CanEat = new EatRule { Unless = [PlayerStateId.InCombat], OnRefused = [new EffectBlock { Applies = kindled }] },
            Recipe = new RecipeBlock { Makes = 5, Needs = { [GameItems.Material_T1_Twigs] = 1 } },
        });
    }
}
