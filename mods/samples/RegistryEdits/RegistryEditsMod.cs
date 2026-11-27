// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registry;

namespace RegistryEdits;

// Since API 0.2.17. Omit the attribute to author only at GameStart.
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
public sealed class RegistryEditsMod : Mod
{
    public sealed class Settings
    {
        [Range(1, 10000)] public int WoodStack { get; set; } = 500;
        [Range(1, 10)] public double LegendaryBoost { get; set; } = 2;
    }
    public override void OnRegistry(RegistryContext context)
    {
        // The host supplies a read-only input snapshot when no live config is registered yet.
        // Observed settings, code, point, pass and world identity contribute to context.Identity.
        var settings = Config.Register<Settings>().Value;
        var registry = DataRegistry.For(context);
        if (context.EntryPoint == RegistryEntryPoint.GameStart)
        {
            // New identities and visibility are process decisions: never restate them at later entry points.
            registry.Add(stage => stage.Items.AddLike(new("registry-edits:stout_logs"), new("game:material_t1_wood_logs"),
                item => item.MaxStack = 100));
            // This hides only random-loot candidates, not already-owned logs or their recipes/direct drops.
            registry.Alter(stage => stage.Loot.Remove("game:material_t1_wood_logs"));
            // World rules (dev.32) are restart fields: GameStart only. Build anywhere, and let worlds set enemy damage up to 10x.
            registry.Alter(stage => stage.World.NoBuildZones.Clear());
            registry.Alter(stage => stage.Difficulty.EnemyDamageFactor.Max = Math.Max(stage.Difficulty.EnemyDamageFactor.Max, 10));
        }
        registry.Alter(stage => stage.Items["game:material_t1_wood_logs"].MaxStack = settings.WoodStack);
        // dev.32 (experimental): creatures, arsenals and perks. A creature edit is global to its kind (every Vukah warrior);
        // the level and other values a creature copies as it spawns reach those that spawn after it, its sight at once.
        registry.Alter(stage =>
        {
            var vukah = stage.Creatures["game:enemy_vukah_melee"];
            vukah.Sight.Range = vukah.Sight.Range * 1.5;
            vukah.Resist.Fire = 2.0;  // takes double fire damage
            stage.Arsenals["game:enemy_vukah_melee"].Attacks[0].Chance = 0.5;
            stage.Perks["game:perk_max_adder_health_t7"].Value = 150;  // a whole-number value: a fraction would be refused
            // a wild animal's taming (read at once by every animal of the entry): the white dog takes 3 pettings, not 2;
            // whether it is tameable and its favourite bait are read only
            var dog = stage.Creatures["game:animal_wildlife_dog_05_white_ag2"];
            if (dog.Tameable) dog.TamingSteps = 3;
        });
        // Since 0.2.0-dev.32: a skill node's cost (game:skill_<node id>; Airborne Architect here) and recipes by their station
        // (a stations id: the id of the item that builds the station). Stations and recipes.station are read-only.
        registry.Alter(stage => stage.Skills["game:skill_4475e8dd"].Cost = 0);
        var forge = new RegistryId("game:prop_production_t1_forge");
        registry.Alter(stage =>
        {
            foreach (var recipe in stage.Recipes.Select())
                if (recipe.Station == forge) recipe.Duration /= 2;
        });
        // Since 0.2.0-dev.32: the world-wide rarity weights and every random loot table (read at each roll, server world).
        // Reads leave this mod's own layers out, so x LegendaryBoost restates the intent at every point and never stacks.
        registry.Alter(stage =>
        {
            var weights = stage.LootRules.RarityWeights;
            weights.Legendary = weights.Legendary * settings.LegendaryBoost;
            foreach (var table in stage.RandomLoot.Select("game:labels_*"))  // enemies and generic containers
                if (table.Rarity.Legendary.Mode == RandomLootMode.Set)     // the elite, boss and chest tables with their own odds
                    table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * settings.LegendaryBoost;
        });
        // A Final helper is explicit. Ordinary assignments target Alter and are ignored in Add/Final.
        // Do not retain context, entries or lists: their operations expire when this callback returns.
    }
}
