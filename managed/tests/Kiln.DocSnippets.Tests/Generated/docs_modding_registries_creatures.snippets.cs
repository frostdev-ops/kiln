// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/creatures.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_creatures_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 107 "docs/kiln/modding/registries/creatures.md"
registry.Alter(stage =>
{
    var vukah = stage.Creatures["game:enemy_vukah_melee"];
    vukah.Sight.Range = vukah.Sight.Range * 1.5;
    vukah.Resist.Fire = 2.0;                               // takes double fire damage; 0 would make it immune
    foreach (var wolf in stage.Creatures.Select("game:enemy_wildbeast_wolf_*"))
        if (wolf.Kind == CreatureKind.Enemy) wolf.Level = 12;
    var dog = stage.Creatures["game:animal_wildlife_dog_05_white_ag2"];
    if (dog.Tameable) dog.TamingSteps = 3;
});
#line default
        }
    }
}
