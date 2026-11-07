# Wiki note: creative mode part 3 (map reveal, item options, free crafting, kept ammunition)

What changed for the public guide (Creative mode page; mod authors: Items, Rules).

## Suggested text: "The creative menu" (additions)
> **Items**: weapons and armour have two more options. **Upgrades** unlocks upgrades as the workbench does, each one
> level higher (5 = all the item has; its rarity decides how many). **Gem slot** gives a weapon an empty gem slot when
> it did not roll one. With **Only what I have found** on, gear's final level, including upgrades, is at most your
> character's level. Gear that cannot fit under that cap is refused; the character's level must be readable.
>
> **World > Map reveal** uncovers the map around you as you move: the game's own range, 200 m, 400 m, or the whole map
> in a sweep over several frames. It lasts while you are in the world with creative mode on. What it uncovers stays uncovered for that
> character, as the game saves it, so use it on a creative character.
>
> **Free crafting**: with creative mode on, crafting takes nothing and needs nothing. The game's crafting menu lets you
> craft without the ingredients (they still show in red), and what you have stays in your bags.
>
> **Ammunition**: fired arrows, thrown bombs and a staff's spell ammunition are not used up. Food, drinks, potions and
> bandages still are.

## For mod authors
- `Items.Give(..., new Gear(level, rarity) { Unlocked = 5, GemSlot = true })`, `Items.GearOptionsAvailable`
  (C: `KILN_ITEMS_UNLOCK` with `KilnItemsRequest.unlock`, `KILN_ITEMS_GEM_SLOT`).
- `CreativeTools.Give(player, item, count, level, rarity, upgrades, gemSlot)`.
- `PlayerRules.FreeCrafting` and `KeepConsumables` work again, as native hooks inside the game's own transactions
  (no item is minted or refunded). `Rules.Status()` has `FreeCrafting`, `KeptConsumables`, `MapReveal`, `CraftButtons`,
  `CraftsFree`, `ConsumablesKept`.
- C: kiln.rules `map_reveal(mode, radius)` and `creative(KilnRulesCreative*)` (table size grew; check `header.size`), flags
  `KILN_RULES_FREE_CRAFTING`, `KILN_RULES_KEEP_CONSUMABLES`.

## Not in it yet
Character flight (the character itself flying), food and potions that are not used up, drag and drop from the menu into
the backpack.
