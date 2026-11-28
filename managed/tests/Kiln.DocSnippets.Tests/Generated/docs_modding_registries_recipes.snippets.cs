// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/recipes.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_recipes_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 67 "docs/kiln/modding/registries/recipes.md"
registry.Alter(stage =>
{
    var forge = new RegistryId("game:prop_production_t1_forge");
    foreach (var recipe in stage.Recipes.Select())
        if (recipe.Station == forge) recipe.Duration /= 2;          // Duration is a TimeSpan
    var arrow = stage.Recipes["game:ammo_arrow_t3_bronze"];
    arrow.Inputs[0].Count = 2;                                      // an element of the input list, by its index
    var twig = arrow.Inputs.AppendNew();                            // a new input: then set its fields
    twig.Item = new RegistryId("game:material_t1_twigs");
    twig.Count = 1;
});
#line default
        }
    }
}
