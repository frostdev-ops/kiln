# Wiki note: creative mode part 4 (Creative Mode coexistence, food and potions, character flight, backpack grid)

What changed for the public guide (Creative mode page; mod authors: Rules).

## Suggested text: "The creative menu" (additions)
> **Food, drinks, potions and bandages** are not used up in creative mode any more, like arrows and spell ammunition.
> Their effect (the food buff, the healing) still happens.
>
> **Items > Backpack** shows your backpack's slots. Pick an item, press **Place...**, then click an empty slot (or one
> with the same item) to put it exactly there.
>
> **World > Character flight** (when the server allows it): **Unlimited** or **Building areas only**. Jump to take off,
> hold jump to climb, sneak to sink, sprint to go faster; sink onto the ground to land. Opening the glider, water or
> leaving the building area (in that mode) ends the flight. A key for it can be set in Kiln menu > Keys
> ("Character flight on or off"). Server owners turn it on with `"allowCharacterFlight": true` in `kiln/config/rules.json`;
> it is off by default while it is new.
>
> **Creative Mode (Nexus 146) installed too?** Kiln runs it through its Flame plugin. While it runs, Kiln's own
> creative mode stays out of its way: F7 and the pause-menu entry are not there, `/creative on` says Creative Mode
> applies, and Kiln installs none of its creative hooks. Use Creative Mode's own menu.

## For mod authors
- `PlayerRules.CharacterFlight` (`CharacterFlightMode.Off`, `Unlimited`, `BuildingArea`; JSON `"characterFlight"`),
  `EffectiveRules.CharacterFlight`; rules.json `allowCharacterFlight` (default false); `Creative.SetFlight(player, mode)`;
  `Rules.Status()` has `CharacterFlight` and `KeptFoodAndPotions`.
- `CreativeTools.Give(player, item, count, level, rarity, upgrades, gemSlot, slot)` puts the item into a backpack slot
  first (`SlotId`).
- C: kiln.rules flags `KILN_RULES_CHARACTER_FLIGHT`, `KILN_RULES_FLIGHT_BASE_ONLY`, hook bits `KILN_RULES_HOOK_FLIGHT`,
  `KILN_RULES_HOOK_CONSUME_BUFF`; `KilnRulesCreative` grew `flight_takeoffs`, `flight_landings`, `flight_ends` (check
  `size`). kiln.rules' creative hooks are installed at the first world now (they wait for `kiln.flame`).
