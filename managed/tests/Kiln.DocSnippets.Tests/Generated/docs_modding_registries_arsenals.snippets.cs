// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/arsenals.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_arsenals_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 48 "docs/kiln/modding/registries/arsenals.md"
registry.Alter(stage =>
{
    var arsenal = stage.Arsenals["game:enemy_vukah_melee"];
    arsenal.Attacks[0].Chance = 0.5;
    foreach (var attack in arsenal.Attacks) attack.MaxRange = attack.MaxRange * 1.25;
});
#line default
        }
    }
}
