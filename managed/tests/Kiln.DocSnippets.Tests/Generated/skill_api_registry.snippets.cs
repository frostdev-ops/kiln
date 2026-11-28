// Made by tools/docs-site/snippets.py from agent/skills/kiln-modding/references/api-registry.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.skill_api_registry_1
{
    using Kiln;
    using Kiln.Registry;
#line 81 "agent/skills/kiln-modding/references/api-registry.md"



[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Tweaks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        if (!Capabilities.Is("kiln.registry")) return;                            // the transport; fields have their own rows
        var registry = Kiln.Registry.Registries.For(context);
        registry.Alter(stage =>
        {
            stage.Items["game:material_t1_wood_logs"].MaxStack = 500;
            var vukah = stage.Creatures["game:enemy_vukah_melee"];
            vukah.Sight.Range = vukah.Sight.Range * 1.5;
            stage.Arsenals["game:enemy_vukah_melee"].Attacks[0].Chance = 0.5;
            stage.Skills["game:skill_4475e8dd"].Cost = 0;
            stage.LootRules.RarityWeights.Legendary = stage.LootRules.RarityWeights.Legendary * 2;
            foreach (var table in stage.RandomLoot.Select("game:labels_*"))        // enemies and generic containers
                if (table.Rarity.Legendary.Mode == RandomLootMode.Set)             // the elite, boss and chest tables with their own odds
                    table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * 2;
        });
        if (context.EntryPoint == RegistryEntryPoint.GameStart)                    // world rules are restart fields: GameStart only
            registry.Alter(stage => stage.World.NoBuildZones.Clear());
    }
}
#line default
}

namespace DocSnippets.skill_api_registry_2
{
    public sealed class Settings { public int Level { get; set; } = 1; }
#line 166 "agent/skills/kiln-modding/references/api-registry.md"
[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
public sealed class Leveler : Mod
{
    public override void OnLoad() => Config.Register<Settings>().Changed += _ =>
    {
        if (!Kiln.Registry.Registries.Available) return;                            // the host's registry back-end
        var r = Kiln.Registry.Registries.Reauthor();
        if (!r.Ok) Log.Warn($"not authored again: {r.Reason}");
    };
    public override void OnRegistry(RegistryContext context)
    {
        if (context.Phase != RegistryPhase.Alter || !Capabilities.Is("kiln.registry")) return;
        var w = Kiln.Registry.Registries.For(context).LootRules.RarityWeights;   // the values below this mod: the game's, or other mods' edits
        w.Legendary = w.Legendary * 4;                                          // never stacks: each pass reads below its own last edit
    }
}
#line default
}

namespace DocSnippets.skill_api_registry_3
{
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 220 "agent/skills/kiln-modding/references/api-registry.md"
if (Capabilities.Is("kiln.registry.view") && RegistryViews.Find("quest") is { } quests)
{
    foreach (var id in quests.Select("game:quest_*"))
        if (quests[id].TryGet<int>("xpReward", out var xp, out var why)) Log.Info($"{id.Value}: {xp} xp");
        else Log.Info(why);                                             // this entry has no value of it, in Kiln words
}
var maxStack = RegistryViews.Find("items")!["game:material_t1_wood_logs"].Get<int>("maxStack");
#line default
        }
    }
}
