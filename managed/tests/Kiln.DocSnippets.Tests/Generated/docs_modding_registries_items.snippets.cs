// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/items.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_items_1
{
#line 106 "docs/kiln/modding/registries/items.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Stacks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        if (!Kiln.Registry.Registries.Available) return;                      // kiln.registry: the host's registry transport
        var registry = Kiln.Registry.Registries.For(context);
        registry.Alter(stage =>
        {
            stage.Items["game:material_t1_wood_logs"].MaxStack = 500;
            foreach (var weapon in stage.Items.Select("game:weapon_t1_*"))
                if (weapon.HasDamage) weapon.Power = weapon.Power * 2;
        });
        if (context.EntryPoint == RegistryEntryPoint.GameStart && Capabilities.Is("registry.items.add"))   // adding has its own row
            registry.Add(stage => stage.Items.AddLike(new("my-mod:stout_logs"), new("game:material_t1_wood_logs"), item => item.MaxStack = 100));
    }
}
#line default
}
