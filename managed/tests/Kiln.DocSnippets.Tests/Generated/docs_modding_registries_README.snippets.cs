// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/README.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_README_1
{
#line 120 "docs/kiln/modding/registries/README.md"
[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
public sealed class Stacks : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registries.For(context);
        registry.Alter(stage => stage.Items["game:material_t1_wood_logs"].MaxStack = 500);
    }
}
#line default
}

namespace DocSnippets.docs_modding_registries_README_2
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 211 "docs/kiln/modding/registries/README.md"
if (context.EntryPoint == RegistryEntryPoint.GameStart)
{
    registry.Add(stage => stage.Items.AddLike(new("my-mod:logs"), new("game:material_t1_wood_logs"),
        item => item.MaxStack = 100));
    registry.Alter(stage => stage.Loot.Remove("game:material_t1_wood_logs"));
}
#line default
        }
    }
}

namespace DocSnippets.docs_modding_registries_README_3
{
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 272 "docs/kiln/modding/registries/README.md"
var change = Kiln.Registry.Registries.Change(stage =>
    stage.Items["game:material_t1_wood_logs"].MaxStack = 500);
if (!change.Result.Ok) Log.Warn(change.Result.Reason ?? "registry change not applied");
// Keep the owner-bound handle if needed. Revert recomposes the remaining layers, not an old memory snapshot.
var reverted = change.Revert();
if (!reverted.Ok) Log.Warn(reverted.Reason ?? "registry revert not applied");
#line default
        }
    }
}

namespace DocSnippets.docs_modding_registries_README_4
{
    public sealed class Settings { public int Level { get; set; } = 1; }
#line 359 "docs/kiln/modding/registries/README.md"
[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
public sealed class Leveler : Mod
{
    public override void OnLoad() => Config.Register<Settings>().Changed += _ => Kiln.Registry.Registries.Reauthor();
    public override void OnRegistry(RegistryContext context)
    {
        if (context.Phase != RegistryPhase.Alter) return;
        var r = Kiln.Registry.Registries.For(context);
        var w = r.LootRules.RarityWeights;      // the values below this mod: the game's, or other mods' edits
        w.Legendary = w.Legendary * 4;          // never stacks: each pass reads below its own last edit
    }
}
#line default
}
