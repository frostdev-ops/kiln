// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/abilities.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_abilities_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
#line 61 "docs/kiln/modding/registries/abilities.md"
Kiln.Registry.Registries.For(context).Alter(stage =>
{
    var cast = stage.Abilities["game:attack_player_staff_fixedanim_fireball_fast"];
    cast.Timing.Release = TimeSpan.FromSeconds(0.45);      // the projectile leaves earlier...
    cast.Timing.Cost = TimeSpan.FromSeconds(0.45);         // ...and the mana is paid at the same moment
});
#line default
        }
    }
}
