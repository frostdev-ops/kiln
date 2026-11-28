// Made by tools/docs-site/snippets.py from docs/kiln/modding/registries/views.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_registries_views_1
{
    using Kiln.Registry;
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 17 "docs/kiln/modding/registries/views.md"


if (RegistryViews.Available && RegistryViews.Find("quest") is { } quests)
{
    foreach (var id in quests.Select("game:quest_*"))
    {
        var quest = quests[id];
        if (quest.TryGet<int>("xpReward", out var xp, out var why)) Log.Info($"{id.Value}: {xp} xp");
        else Log.Info(why);                                         // this entry has no value of it, in Kiln words
    }
    var reward = quests["game:quest_main"].Get<RegistryId>("rewardItems[0].item");   // another view's entry
}
var maxStack = RegistryViews.Find("items")!["game:material_t1_wood_logs"].Get<int>("maxStack");   // the registry's value
#line default
        }
    }
}
