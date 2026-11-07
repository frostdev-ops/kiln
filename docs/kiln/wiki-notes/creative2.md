# Wiki note: creative menu, day and night cycle, no fall damage

What changed for the public guide (Creative mode page, Commands page, Keys).

## Suggested text: "The creative menu"
> Press **F7** in the game (or pick **Creative** in the pause menu) to open the creative menu. **Items** lists every item
> of the world, mods' items included: search, pick a category or leave out equipment, then **Take 1**, take a stack or
> take any count; weapons and armour get the level and rarity you choose. Taking items needs creative mode, which an
> admin turns on with `/creative on <player>`. **World** sets the time of day and the day and night cycle: Normal,
> Stopped (the sun stands still, even when players sleep) or Fast (a whole day and night in about a minute and a half).
> Only admins (and the host of a world you host yourself) change the time. A "Creative" line shows on the HUD while
> creative mode is on. Tick **Only what I have found** to list only items you have picked up, crafted or used (journey
> mode). On a dedicated server the menu needs Kiln on the server and in your game.

## Suggested text: commands
> `/creative give <player> <item> [count] [level]` — items for a player in creative mode.
> `/creative time <time>` — jump the clock forward (`7`, `07:30`, `7pm`, `dusk`).
> `/creative cycle normal|stopped|fast` — the day and night cycle until it is set back or the server restarts.

## For mod authors
- `CreativeTools.Give / SetTime / SetCycle / Catalog` (Kiln.Rules), `Time.SetCycle(TimeCycle)`, `Time.SetSpeed(double?)`,
  `Time.HeldSpeed`, `Time.CanSetSpeed` (Kiln.Time; C: `set_time_scale`, `time_scale_hold`).
- New rules `NoFallDamage`, `FreeCrafting`, `KeepConsumables` (server limits `allowNoFallDamage`, `allowFreeCrafting`,
  `allowKeepConsumables`). `/creative` now also gives free crafting and kept consumables (food, drinks, potions,
  bandages), and upgrading a storage chest is free with free building. `CreativeTools.Found(player)` for journey mode.
- Remote tools: Kiln.Admin ops `creative_give`, `creative_cycle`.

## Not in it yet
Character flight (the character itself flying), crafting without the inputs in the game's menu (the menu still asks for
them), arrows that are not used up, a map reveal, journey mode's level cap, drag and drop into the backpack, unlocked
upgrades on taken gear.
