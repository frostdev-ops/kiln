// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/buffs.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_buffs_1
{
#line 48 "docs/kiln/modding/registries/buffs.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
public sealed class Poison : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        if (!Kiln.Registry.Registries.Available) return;                      // kiln.registry: the host's registry transport
        Kiln.Registry.Registries.For(context).Alter(stage =>
        {
            var poisoned = stage.Buffs["game:buff_5f425faf"];
            poisoned.Duration = TimeSpan.FromSeconds(20);
            Log.Info($"Poisoned: {poisoned.Duration.TotalSeconds:0} s, {poisoned.Kind}, applied again: {poisoned.Stacking}");
        });
    }
}
#line default
}
