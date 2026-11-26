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
        // Since 0.2.0-dev.32: a skill node's cost (game:skill_<node id>; Airborne Architect here) and recipes by their station
        // (a stations id: the id of the item that builds the station). Stations and recipes.station are read-only.
        registry.Alter(view => view.Skills["game:skill_4475e8dd"].Cost = 0);
        var forge = new RegistryId("game:prop_production_t1_forge");
        registry.Alter(view =>
        {
            foreach (var recipe in view.Recipes.Select())
                if (recipe.Station == forge) recipe.Duration /= 2;
        });
        // A Final helper is explicit. Ordinary assignments target Alter and are ignored in Add/Final.
        // Do not retain context, entries or lists: their operations expire when this callback returns.
    }
}
