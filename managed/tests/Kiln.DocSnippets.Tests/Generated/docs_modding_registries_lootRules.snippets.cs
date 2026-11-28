// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/lootRules.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_lootRules_1
{
    public sealed class Settings { public int Level { get; set; } = 1; }
#line 51 "docs/kiln/modding/registries/lootRules.md"
[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
public sealed class Leveler : Mod
{
    public override void OnLoad() => Config.Register<Settings>().Changed += _ =>
    {
        var r = Kiln.Registry.Registries.Reauthor();
        if (!r.Ok) Log.Warn($"not authored again: {r.Reason}");
    };
    public override void OnRegistry(RegistryContext context)
    {
        if (context.Phase != RegistryPhase.Alter) return;
        var w = Kiln.Registry.Registries.For(context).LootRules.RarityWeights;   // the values below this mod's own edit
        w.Legendary = w.Legendary * 4;                                          // never stacks: each pass reads below its own last edit
    }
}
#line default
}
