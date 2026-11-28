// Made by tools/docs-site/snippets.py from docs/kiln/modding/item-costs.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_item_costs_1
{
    using static DocSnippets.docs_modding_item_costs_1.Stubs;
    internal static class Stubs
    {
        public static void GiveTheBlessing(EntityId player) { }
    }
#line 17 "docs/kiln/modding/item-costs.md"
public sealed class BlessingMod : Mod
{
    [Command("bless", Description = "pay 3 twigs for a blessing")]
    private async Task Bless(CommandContext c)
    {
        if (!ItemCosts.StacksAvailable) throw new CommandException($"exact costs are off: {Capabilities.Why(ItemCosts.StacksCapability)}");
        var owner = EntityRef.Of(c.Source.Player);
        var twigs = Items.Lookup("Material_T1_Twigs")!.Id;

        var slot = ItemCosts.Choose(owner, twigs, 3)                              // one slot, as it is now: the first plain stack holding 3
                   ?? throw new CommandException("you need 3 twigs in one stack");
        var receipt = await ItemCosts.Take(slot, 3, Guid.NewGuid());              // exactly 3 from that slot, all or nothing
        if (!receipt.Committed) throw new CommandException($"not paid: {receipt.Refusal}: {receipt.Reason}");

        try { GiveTheBlessing(c.Source.Player); }                                  // your own effect
        catch
        {
            await ItemCosts.Refund(receipt, Guid.NewGuid());                       // one refund per paid receipt
            throw;
        }
        c.Reply("You are blessed.");
    }
}
#line default
}
