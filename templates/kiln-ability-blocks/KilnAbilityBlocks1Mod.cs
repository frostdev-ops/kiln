using Kiln;

namespace KilnAbilityBlocks1;

/// <summary>Player powers from building blocks: an enhancement (perk) on a sword, an ability on a key, a talent tree that unlocks it,
/// and a trigger on what a player does. blocks/abilities.json makes the same kinds as data.</summary>
public sealed class KilnAbilityBlocks1Mod : Mod
{
    public override void OnLoad()
    {
        var singe = Blocks.Define("singe", new EffectBlock { Damage = Damage.Fire(10), Flinch = Flinch.None });

        // a perk: it works once the sword is upgraded to unlock it, as the game's own enhancements do
        var flare = Blocks.Define("flare-edge", new PerkBlock { Name = "Flare Edge", Description = "Hits may singe.", OnHit = [new EffectBlock { Chance = 0.25, Then = [singe] }] });
        Blocks.Define("flare-sword", new WeaponBlock
        {
            Name = "Flare Sword", Class = WeaponClass.Sword,
            Stats = new WeaponStats { Level = 5, Power = 25, Rarity = Rarity.Uncommon },
            Perks = [flare],
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 3 } },
        });

        // an ability on G: a cone of fire, for those who have the talent below
        var burst = Blocks.Define("flare-burst", new AbilityBlock
        {
            Name = "Flare Burst", Key = "G", Command = "flareburst",
            Cast = new CastBlock { ManaCost = 20 }, Cooldown = 6,
            Delivery = new ConeBlock { Range = 8, Angle = 70, Effects = [singe] },
        });

        // a talent tree with its own menu (K): points from levels, nodes that need others
        Blocks.Define("flare-path", new TalentTreeBlock
        {
            Name = "Flare Path", Menu = "Flare Path", Key = "K",
            Nodes =
            {
                ["spark"] = new TalentBlock { Name = "Spark", MaxLevel = 3, Modifies = { [Stat.Intelligence] = 1 } },
                ["burst"] = new TalentBlock { Name = "Flare Burst", Description = "G sends fire ahead of you.", Requires = ["spark"], Cost = 2, Unlocks = burst },
            },
        });

        // a trigger: sitting down gives back mana, at most every 10 s
        Blocks.Define("fireside", new TriggerBlock { On = PlayerStateId.Sitting, Do = [new EffectBlock { Restores = Restore.Mana(15) }], Cooldown = 10 });
    }
}
