// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/world.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_world_1
{
#line 67 "docs/kiln/modding/registries/world.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class BuildAnywhere : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        if (!Kiln.Registry.Registries.Available) return;                      // kiln.registry: the host's registry transport
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            stage.World.NoBuildZones.Clear();
            stage.World.PlayableAreas.Clear();
        });
    }
}
#line default
}
