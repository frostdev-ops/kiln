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
            registry.Add(view => view.Items.AddLike(new("registry-edits:stout_logs"), new("game:material_t1_wood_logs"),
                item => item.MaxStack = 100));
            // This hides only random-loot candidates, not already-owned logs or their recipes/direct drops.
            registry.Alter(view => view.Loot.Remove("game:material_t1_wood_logs"));
        }
        registry.Alter(view => view.Items["game:material_t1_wood_logs"].MaxStack = settings.WoodStack);
        // Since 0.2.0-dev.32: the world-wide rarity weights and every random loot table (read at each roll, server world).
        // Reads leave this mod's own layers out, so x LegendaryBoost restates the intent at every point and never stacks.
        registry.Alter(view =>
        {
            var weights = view.LootRules.RarityWeights;
            weights.Legendary = weights.Legendary * settings.LegendaryBoost;
            foreach (var table in view.RandomLoot.Select("game:labels_*"))  // enemies and generic containers
                if (table.Rarity.Legendary.Mode == RandomLootMode.Set)     // the elite, boss and chest tables with their own odds
                    table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * settings.LegendaryBoost;
        });
        // A Final helper is explicit. Ordinary assignments target Alter and are ignored in Add/Final.
        // Do not retain context, entries or lists: their operations expire when this callback returns.
    }
}
