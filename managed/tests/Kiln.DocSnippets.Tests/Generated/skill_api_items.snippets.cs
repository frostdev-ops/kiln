// Made by tools/docs-site/snippets.py from agent/skills/kiln-modding/references/api-items.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.skill_api_items_5
{
    using static DocSnippets.skill_api_items_5.Stubs;
    internal static class Stubs
    {
        public static EntityId player => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 181 "agent/skills/kiln-modding/references/api-items.md"
if (!Capabilities.Is("items.costs.exact")) return;
var twigs = Items.Lookup("Material_T1_Twigs")!.Id;
var sel = ItemCosts.Choose(EntityRef.Of(player), twigs, 3);                              // the first plain stack that can pay; null: none
if (sel is not { } slot) return;
CostReceipt r = await ItemCosts.Take(slot, 3, Guid.NewGuid());                          // all or nothing, from that slot only
if (r.Committed) Log.Info($"took {r.Taken}");                                           // r.State: Committed, Refused or Uncertain
else if (r.Uncertain) Log.Warn($"unknown whether paid: {r.Reason}");                     // never submit it again as if nothing happened
else Log.Info($"refused: {r.Refusal}");                                                  // StaleSelection, NotAStack, ...
CostReceipt back = await ItemCosts.Refund(r, Guid.NewGuid());                            // a committed take, once
#line default
        }
    }
}
