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
        // dev.32 (experimental): creatures, arsenals and perks. A creature edit is global to its kind (every Vukah warrior);
        // the level and other values a creature copies as it spawns reach those that spawn after it, its sight at once.
        registry.Alter(view =>
        {
            var vukah = view.Creatures["game:enemy_vukah_melee"];
            vukah.Sight.Range = vukah.Sight.Range * 1.5;
            vukah.Resist.Fire = 2.0;  // takes double fire damage
            view.Arsenals["game:enemy_vukah_melee"].Attacks[0].Chance = 0.5;
            view.Perks["game:perk_max_adder_health_t7"].Value = 150;  // a whole-number value: a fraction would be refused
        });
        // A Final helper is explicit. Ordinary assignments target Alter and are ignored in Add/Final.
        // Do not retain context, entries or lists: their operations expire when this callback returns.
    }
}
