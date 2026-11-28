// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/limits.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_limits_1
{
#line 44 "docs/kiln/modding/registries/limits.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class MoreAltars : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            stage.Limits.MaxAltars = 20;
            stage.Limits.GiftingRange = 500;
            for (var level = 0; level < stage.Balance.AltarsPerFlameLevel.Count; level++)
                stage.Balance.AltarsPerFlameLevel[level] = Math.Max(stage.Balance.AltarsPerFlameLevel[level], 20);
        });
    }
}
#line default
}
