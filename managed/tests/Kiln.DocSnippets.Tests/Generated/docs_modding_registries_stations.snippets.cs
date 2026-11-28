// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/stations.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_stations_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 51 "docs/kiln/modding/registries/stations.md"
registry.Alter(stage =>
{
    var forge = new RegistryId("game:prop_production_t1_forge");
    foreach (var recipe in stage.Recipes.Select())
        if (recipe.Station == forge) recipe.Duration /= 2;           // every forge recipe takes half as long
});

// reading the stations themselves, outside a pass (on the tick thread, in your mod)
Kiln.Registry.Registries.Read(stage =>
{
    foreach (var station in stage.Stations.Select())
        Log.Info($"{station.Id.Value}: built by {station.Item}, villager's: {station.Npc}");
});
#line default
        }
    }
}
