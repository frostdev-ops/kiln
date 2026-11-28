// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/difficulty.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_difficulty_1
{
#line 128 "docs/kiln/modding/registries/difficulty.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class WiderBounds : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            var bounds = stage.Difficulty.EnemyDamageFactor;
            bounds.Max = Math.Max(bounds.Max, 10);            // never lower another mod's higher bound
        });
    }
}
#line default
}
