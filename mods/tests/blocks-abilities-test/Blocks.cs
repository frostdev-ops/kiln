// SPDX-License-Identifier: MIT
using Kiln;

namespace BlocksAbilitiesTest;

public sealed partial class BlocksAbilitiesTestMod
{
    private void Define()
    {
        var mark = Blocks.Define("mark", new StatusBlock { Name = "Mark", Seconds = 30, Kind = StatusKind.Buff });
        var burst = Blocks.Define("burst", new EffectBlock { Damage = Damage.Ice(11), Flinch = Flinch.None });
        // code naming a block of this mod's data files, during OnLoad: it compiles when named
        Blocks.Define("code-names-data", new StatusBlock { Name = "Code Names Data", Seconds = 3, OnApply = ["burst-json", new EffectBlock { Applies = "mark-json" }] });
        Blocks.Define("glide-buff", new StatusBlock { Name = "Lift", Kind = StatusKind.Buff, While = PlayerStateId.Gliding, Modifies = { [Stat.MoveSpeed] = 0.25 } });
        Blocks.Define("sit-buff", new StatusBlock { Name = "Rest", Kind = StatusKind.Buff, While = PlayerStateId.Sitting, Modifies = { [Stat.MoveSpeed] = 0.25 } });

        // ---- perks
        var hit = Blocks.Define("perk-hit", new PerkBlock { Name = "Frost Edge", Description = "Hits bite with cold.", Value = "5%", OnHit = [burst, new EffectBlock { Applies = mark }] });
        var might = Blocks.Define("perk-might", new PerkBlock { Name = "Might", Modifies = { [Stat.Strength] = 5 } });
        Blocks.Define("perk-swift", new PerkBlock { Name = "Swift", Modifies = { [Stat.MoveSpeed] = 0.1 } });
        Blocks.Define("perk-chance", new PerkBlock { Name = "Chance", OnHit = [new EffectBlock { Chance = 0.3, Damage = Damage.Ice(1) }] });
        Blocks.Define("perk-quiet", new PerkBlock { Name = "Quiet", OnKill = [new EffectBlock { Heal = 1 }, new EffectBlock { Heal = 2 }] });
        Blocks.Define("perk-icon", new PerkBlock { Name = "Icon", Icon = "Perk_Damage_Ice", OnKill = [new EffectBlock { Heal = 1 }] });
        Blocks.Define("perk-sword", new WeaponBlock { Name = "Perk Sword", Class = WeaponClass.Sword, Stats = new WeaponStats { Level = 5, Power = 20, Rarity = Rarity.Uncommon }, Perks = [hit, might] });

        // ---- spells with what is new
        Blocks.Define("stamina-spell", new SpellBlock { Name = "Stamina Spell", Cast = new CastBlock { ManaCost = 5, StaminaCost = 7, Charges = 3 }, Delivery = new ProjectileBlock { OnHit = [new EffectBlock { Damage = Damage.Ice(3) }] } });
        // its tooltip rows are put in another order than its starter's below (RowsBySync); a wand's rows (mana, radius) are not its starter's either
        Blocks.Define("rows-wand", new WandBlock { Name = "Rows Wand", Stats = new WeaponStats { Level = 5, Rarity = Rarity.Uncommon }, Primary = new WandAttack { Cast = new CastBlock { ManaCost = 6 }, Delivery = new ProjectileBlock { Look = ProjectileLook.Fireball, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 2, Effects = [new EffectBlock { Damage = Damage.Fire(25) }] } }] } } });
        Blocks.Define("rows-reordered", new SpellBlock { Name = "Rows Reordered", Cast = new CastBlock { ManaCost = 5, StaminaCost = 7 }, Delivery = new ProjectileBlock { Look = ProjectileLook.Fireball, OnHit = [new EffectBlock { Damage = Damage.Fire(3) }] } });
        Blocks.Define("free-spell", new SpellBlock { Name = "Free Spell", Cast = new CastBlock { ManaCost = 5, Charges = 0 }, Delivery = new ProjectileBlock { OnHit = [new EffectBlock { Damage = Damage.Ice(3) }] } });
        Blocks.Define("cone-spell", new SpellBlock { Name = "Cone Spell", Cast = new CastBlock { ManaCost = 5 }, Delivery = new ConeBlock { Range = 8, Angle = 60, Effects = [new EffectBlock { Damage = Damage.Fire(41), Flinch = Flinch.None }] } });
        Blocks.Define("homing-spell", new SpellBlock { Name = "Homing Spell", Cast = new CastBlock { ManaCost = 5 }, Delivery = new ProjectileBlock { Homing = new HomingBlock { TurnSpeed = 40, SeekRange = 15 }, OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = Damage.Ice(3) }] } }] } });

        // ---- abilities
        EffectBlock Fire(float n) => new() { Damage = Damage.Fire(n), Flinch = Flinch.None };
        Blocks.Define("mend", new AbilityBlock { Name = "Mend", Cast = new CastBlock { ManaCost = 10, StaminaCost = 5 }, Cooldown = 5, OnCast = [new EffectBlock { Heal = 3 }], Command = "mend" });
        Blocks.Define("nova", new AbilityBlock { Name = "Nova", Delivery = new AroundBlock { Radius = 6, Effects = [Fire(21)] } });
        Blocks.Define("cone", new AbilityBlock { Name = "Cone", Delivery = new ConeBlock { Range = 8, Angle = 60, Effects = [Fire(22)] } });
        Blocks.Define("beam", new AbilityBlock { Name = "Beam", Delivery = new BeamBlock { Range = 20, Width = 2, Effects = [Fire(23)] } });
        Blocks.Define("zone", new AbilityBlock { Name = "Zone", Delivery = new ZoneBlock { Radius = 4, Seconds = 2, TickEvery = 0.5, Ahead = 6, Effects = [Fire(24)] } });
        Blocks.Define("chain", new AbilityBlock { Name = "Chain", Delivery = new ChainBlock { Range = 15, Jumps = 2, JumpRange = 6, Effects = [Fire(25)] } });
        Blocks.Define("summon", new AbilityBlock { Name = "Summon", Delivery = new SummonBlock { Template = GameZone, Count = 2, Seconds = 1 } });
        Blocks.Define("bolt", new AbilityBlock { Name = "Bolt", Delivery = new ProjectileBlock { Speed = 20, OnHit = [new EffectBlock { Damage = Damage.Fire(26) }], Homing = new HomingBlock { TurnSpeed = 40, SeekRange = 15 } } });
        Blocks.Define("long-beam", new AbilityBlock { Name = "Long Beam", Delivery = new BeamBlock { Range = 20, Width = 2, Seconds = 1.5, TickEvery = 0.5, Effects = [Fire(27)] } });
        Blocks.Define("self-buff", new AbilityBlock { Name = "Self Buff", Delivery = new SelfBlock { Effects = [new EffectBlock { Applies = mark }] }, OnCast = [new EffectBlock { Heal = 1 }] });
        Blocks.Define("hunt", new AbilityBlock { Name = "Hunt", Aim = AbilityTargeting.NearestEnemy, Range = 30, Delivery = new ProjectileBlock { Speed = 20, OnHit = [new EffectBlock { Damage = Damage.Fire(28) }], Homing = new HomingBlock { TurnSpeed = 80 } } });
        Blocks.Define("hunt-plain", new AbilityBlock { Name = "Hunt Plain", Aim = AbilityTargeting.NearestEnemy, Range = 30, Delivery = new ProjectileBlock { Speed = 20, OnHit = [new EffectBlock { Damage = Damage.Fire(28) }] } });
        Blocks.Define("staff-bolt", new AbilityBlock { Name = "Staff Bolt", Cast = new CastBlock { Animation = CastAnimation.FastBolt, ReleaseAt = 0.5 }, Delivery = new ProjectileBlock { Speed = 20, OnHit = [new EffectBlock { Damage = Damage.Fire(29) }] } });
        var gated = Blocks.Define("gated", new AbilityBlock { Name = "Gated", OnCast = [new EffectBlock { Heal = 1 }] });
        Blocks.Define("gated-game", new AbilityBlock { Name = "Gated By The Game", OnCast = [new EffectBlock { Heal = 1 }] });
        Blocks.Define("scroll", new AbilityBlock { Name = "Scroll", Cast = new CastBlock { ManaCost = 20, Charges = 2 }, Cooldown = 30, Item = new ItemBlock { Name = "Scroll Of Mending", Stack = 20 }, OnCast = [new EffectBlock { Heal = 7 }] });
        Blocks.Define("charm", new AbilityBlock { Name = "Charm", Cast = new CastBlock { Charges = 0 }, Item = new ItemBlock { Name = "Charm Of Mending" }, OnCast = [new EffectBlock { Heal = 8 }] });
        Blocks.Define("casts-on-hit", new PerkBlock { Name = "Echo", OnHit = [new EffectBlock { On = EffectOn.Caster, Casts = "self-buff" }] });

        // ---- talents
        Blocks.Define("arts", new TalentTreeBlock
        {
            Name = "Arts",
            Points = new TalentPointsBlock { Start = 1, PerLevel = 1 },
            Menu = "Arts",
            Key = "J",
            Nodes =
            {
                ["kindle"] = new TalentBlock { Name = "Kindle", MaxLevel = 3, Modifies = { [Stat.Strength] = 2, [Stat.MoveSpeed] = 0.1 } },
                ["hearth"] = new TalentBlock { Name = "Hearth", Description = "Hits burn.", Requires = ["kindle"], OnHit = [new EffectBlock { Damage = Damage.Fire(31), Flinch = Flinch.None }] },
                ["gate"] = new TalentBlock { Name = "Gate", Requires = ["hearth"], Cost = 2, Keeps = mark },
            },
        });
        Blocks.Define("key-tree", new TalentTreeBlock
        {
            Name = "Key Tree",
            Points = new TalentPointsBlock { Start = 5 },
            Nodes = { ["unlock"] = new TalentBlock { Name = "Unlock", Unlocks = gated, OnStart = [new EffectBlock { Heal = 1 }], Texts = new Dictionary<string, ItemTexts> { ["de"] = new() { Name = "Freischalten" } } } },
        });
        var ward = Blocks.Define("ward", new TalentBlock { Name = "Ward", MaxLevel = 3, Cost = 0, Modifies = { [Stat.Strength] = 1 }, Game = new GameTreePlace { From = [Anchor], Offset = [170, -40] } });
        Blocks.Define("ward-share", new TalentBlock { Name = "Ward Share", MaxLevel = 3, Cost = 0, Requires = [ward.Name], Modifies = { [Stat.MoveSpeed] = 0.1 }, Game = new GameTreePlace { Class = "Mage", Offset = [150, 0] } });
        Blocks.Define("ward-gate", new TalentBlock { Name = "Ward Gate", Cost = 0, Requires = ["ward"], Unlocks = "gated-game", OnHit = [Fire(32)], Game = new GameTreePlace { Offset = [150, 100] } });

        // ---- what players do: any state, any action, any item
        Blocks.Define("on-sit", new TriggerBlock { On = PlayerStateId.Sitting, Do = [new EffectBlock { Restores = Restore.Mana(3) }], OnEnd = [new EffectBlock { Restores = Restore.Stamina(2) }], Cooldown = 1 });
        Blocks.Define("on-greet", new TriggerBlock { On = PlayerStateId.ActionExternalAbortAndOverride, When = [PlayerStateId.Hungry], Do = [new EffectBlock { Applies = mark }] });
        Blocks.Define("on-glide-raw", new TriggerBlock { On = "LocomotionHangGliding", Do = [new EffectBlock { Restores = Restore.Mana(1) }] });
        Blocks.Define("on-derived", new TriggerBlock { On = "UsingItem", Unless = [PlayerStateId.Fed], Do = [new EffectBlock { Restores = Restore.Mana(1) }] });
        Blocks.Define("eaten-mushroom", new TriggerBlock { On = PlayerStateRef.Eaten, Item = Mushroom, Do = [new EffectBlock { Restores = Restore.Mana(5) }] });
        Blocks.Define("started-mushroom", new TriggerBlock { On = PlayerStateRef.Eating, Item = Mushroom, Do = [new EffectBlock { Restores = Restore.Stamina(4) }] });
        Blocks.Define("stop-mushroom", new TriggerBlock { On = PlayerStateRef.Eating, Item = Mushroom, When = [PlayerStateId.Fed], Stop = true });
        Blocks.Define("stop-meteor", new TriggerBlock { On = PlayerStateRef.Eating, Item = Meteor, Stop = true });
        Blocks.Define("cake", new ItemBlock { Name = "Cake", Kind = ItemKind.Consumable, OnUse = [new EffectBlock { Heal = 1 }], OnEat = [new EffectBlock { Restores = Restore.Mana(4) }], CanEat = new EatRule { Unless = [PlayerStateId.Sleeping] } });
        Blocks.Define("ration", new ItemBlock { Name = "Ration", Kind = ItemKind.Consumable, OnUse = [new EffectBlock { Heal = 1 }], OnEat = [new EffectBlock { Restores = Restore.Mana(4) }], CanEat = new EatRule { When = [PlayerStateId.Sitting], OnRefused = [new EffectBlock { Applies = mark }] } });
    }
}
