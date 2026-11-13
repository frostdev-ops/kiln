// How many of an item the editor lets a slot hold.
//
// The game's stack size of an item (items.json `maxStack`) is the vanilla one. A mod can raise it (stack-size mods of
// any loader change the game's item data, or the item in memory), and the editor cannot know: so it does not refuse an
// amount above the stack size, it says so, as it does for a level above an item's range. What it does refuse is what
// the save cannot hold: an amount is 16 bits there (shared/character.mjs).
export const MAX_AMOUNT = 65535;

/** The largest amount offered for an item: what the save holds for an item that stacks; an item that does not stack
 *  keeps the amount it has (1, or what a save came with). */
export function amountLimit(maxStack, current = 1) {
  return maxStack > 1 ? MAX_AMOUNT : Math.max(1, current || 1);
}

/** More than the game's own stack of this item: kept by a world whose mods raise stack sizes. */
export function aboveStack(maxStack, amount) {
  return maxStack > 1 && amount > maxStack;
}
