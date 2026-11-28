// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/drops.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_drops_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 58 "docs/kiln/modding/registries/drops.md"
registry.Alter(stage =>
{
    var table = stage.Drops["game:lootcontainer_enemy_skeleton_elite"];
    foreach (var entry in table.Entries) entry.Chance = entry.Chance * 0.5;
    table.Entries[0].Count.Max = 5;                      // one stack's most
    var bonus = table.Entries.AppendNew();               // a new stack: what you set on it goes into the append
    bonus.Item = new RegistryId("game:material_t1_wood_logs");
    bonus.Count.Min = 1; bonus.Count.Max = 3; bonus.Chance = 0.25;
});
#line default
        }
    }
}
