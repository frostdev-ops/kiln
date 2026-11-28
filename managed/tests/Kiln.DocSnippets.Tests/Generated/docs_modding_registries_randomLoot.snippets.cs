// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/randomLoot.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_randomLoot_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 78 "docs/kiln/modding/registries/randomLoot.md"
registry.Alter(stage =>
{
    foreach (var table in stage.RandomLoot.Select("game:labels_*"))             // enemies and generic containers
        if (table.Rarity.Legendary.Mode == RandomLootMode.Set)                  // the elite, boss and chest tables with their own odds
            table.Rarity.Legendary.Weight = table.Rarity.Legendary.Weight * 2;
    foreach (var table in stage.RandomLoot.Select("game:labels_*"))
    {
        table.LevelOffset.Mode = RandomLootMode.Add;
        table.LevelOffset.Value = 2;
    }
});
#line default
        }
    }
}
