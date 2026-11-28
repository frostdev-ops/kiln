// Made by tools/docs-site/snippets.py from docs/kiln/modding/inventory.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_inventory_2
{
    using static DocSnippets.docs_modding_inventory_2.Stubs;
    internal static class Stubs
    {
        public static EntityId player => default!;
        public static EntityId chest => default!;
        public static ItemId flint => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 86 "docs/kiln/modding/inventory.md"
if (!Items.Available || !Items.CheckedMovesAvailable) return;    // kiln.items, and checked moves on this Kiln
var seen = Items.Read(player)!.Slots.First(s => s.Item == flint);   // what the player dragged
var spot = Items.Read(chest)!.Slots.First(s => s.IsEmpty);          // where they dropped it
MoveResult r = await Items.MoveChecked(player, seen, chest, 3, spot);   // exactly 3, exactly into that slot
if (r.Status == ItemsStatus.SlotChanged) { /* the view was old: send the player a fresh one */ }
#line default
        }
    }
}
