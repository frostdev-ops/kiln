// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/skills.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_skills_1
{
    [RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad)]
    public sealed partial class Snippet : Mod
    {
        public override void OnRegistry(RegistryContext context)
        {
            if (!Kiln.Registry.Registries.Available) return;
            var registry = Kiln.Registry.Registries.For(context);
#line 57 "docs/kiln/modding/registries/skills.md"
registry.Alter(stage =>
{
    stage.Skills["game:skill_4475e8dd"].Cost = 0;                       // Airborne Architect is free
    foreach (var node in stage.Skills.Select())
        if (node.Kind == SkillNodeKind.Active) node.Cost = Math.Max(0, node.Cost - 1);
});
#line default
        }
    }
}
