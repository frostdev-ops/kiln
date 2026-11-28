// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/balance.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_balance_1
{
#line 98 "docs/kiln/modding/registries/balance.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart)]
public sealed class Levels : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registries.For(context);
        registry.Alter(stage =>
        {
            var balance = stage.Balance;                    // the one entry, no id needed
            balance.LevelCap = 60;
            balance.XpPerLevel[2] = 3000;                  // a fixed list: set an element
            balance.SocketChance.Epic = 0.75;
            balance.Durability.Growth = balance.Durability.Growth * 2;
        });
    }
}
#line default
}
