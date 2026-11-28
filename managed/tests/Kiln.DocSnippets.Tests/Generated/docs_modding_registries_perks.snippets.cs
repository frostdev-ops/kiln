// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/perks.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_perks_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 74 "docs/kiln/modding/registries/perks.md"
registry.Alter(stage =>
{
    stage.Perks["game:perk_damage_ice"].Value = stage.Perks["game:perk_damage_ice"].Value * 2;
    stage.Perks["game:perk_max_adder_health_t7"].Value = 150;     // a whole-number value: a fraction would be refused
});
#line default
        }
    }
}
