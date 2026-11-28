// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/loot.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_loot_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 54 "docs/kiln/modding/registries/loot.md"
registry.Alter(stage =>
{
    foreach (var entry in stage.Loot.Select("game:material_t1_*")) entry.Stack.Max += 1;
    stage.Loot["game:material_t1_wood_logs"].Rarities = RegistryRarities.Epic | RegistryRarities.Legendary;
});
registry.Alter(stage => stage.Loot.Remove("game:material_t1_twigs"));     // GameStart only
#line default
        }
    }
}
