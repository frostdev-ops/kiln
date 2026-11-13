# Creative Mode (Nexus mod 146, version 3.2.0): what it does, and what Kiln has

A feature inventory of the third-party "Creative Mode" mod for Enshrouded 1.0, written to bring the same features into
Kiln's own creative mode. The mod has no licence, so nothing from it is used in Kiln: no code, no signatures or
patterns, no data. This document records only *what* the player sees and *which* game systems are involved; Kiln finds
the game functions itself in the game's executables and writes its own symbols.

Method: static analysis only. The archive was unpacked into an empty scratch folder and never run or loaded. Sources:
the mod's manifest files, the names and descriptions in its symbol files (`symbols/*.json`), the strings and exports of
its DLL, and a few decompiled functions (Ghidra). Tags: **[str]** the DLL's own strings (UI text, log lines, config
keys), **[sym]** a symbol name or description, **[dec]** decompiled code. Anything not tagged is inference.

## What it is
- A native DLL loaded by its own `dinput8.dll` proxy from `mods/creative/` (exports one entry point; imports only
  KERNEL32, USER32 and one synch API set) **[dec]**. It carries its own runtime ("Flame", an SDK for such mods) with a
  symbol resolver that finds game functions per build and turns features off when a build is unknown **[str]**.
- It runs in the game client and in a dedicated server (the same DLL; per-side symbols marked `exe: client`) **[sym]**.
- Its item catalogue `items.json` is an export of game data (4,213 items in 24 groups; names, descriptions, tiers,
  recipes) made by a separate tool. Game data: not looked at beyond its shape, never copied.

## Features (what the player sees)

### 1. The creative menu (client)
- A full-screen menu in the game's own look, opened by a key (`menuKey`, configurable) or on a controller with RB +
  Start **[str]**. It is drawn with the game's own UI widgets: the mod hooks the HUD builder and shows its menu as one of
  the HUD's screen modes, and calls the game's window, tab, item-slot, slider, dropdown, text-field and hint functions
  **[sym]** (`hud.*`, `gameui.*`, `bundle.*` styles and sounds).
- Tabs **[str]**: the item catalogue (shelves of category tabs, a rail of groups, a grid of item slots, the game's item
  details on hover, a search over all items), and **Settings** (keys, rules, time of day, map reveal; a **Server** part
  for admins).
- A first-run introduction asks the player to check the two keys (click one, press the new key) **[str]**. A notice when
  the game build is newer than the mod knows, shown once per build **[str]**.

### 2. Item picker (client UI, server gives)
- **Take 1**, **Take N** (a stack), **Take with customization**: a dialog with **Level**, **Rarity** and **Upgrades**
  sliders, capped at the item's maximum **[str]**. Items can also be dragged from the menu onto a backpack slot (the mod
  hooks the inventory UI's drag and drop) **[str] [sym]**.
- The server spawns the item into the player's inventory through the game's inventory transaction, then sets its level
  attributes, unlocks its upgrades, and adds a gem slot when the game's roll did not ("ItemGiver") **[str] [sym]**.
- **Picker mode** **[str]**: *Creative* (every item in the game) or *Journey* (only items the character has picked up or
  crafted, from the game's item knowledge; gear levels capped at the character's level).
- **Gear** **[str]**: *Everything* or *Without equipment* (weapons, shields, armour, rings and gems left out; bombs,
  arrows, food, potions and tools stay).
- The catalogue hides abilities, build tools, skill-point tokens, one-time rewards, unused/debug/deprecated items, loot
  copies and Twitch-drop copies, and folds variants **[str]**.
- A client fix for the item details' stat list of items given this way ("ItemStatsFix") **[str] [sym]**.

### 3. Character flight (client and server)
- The character itself flies, unlike the game's building fly mode (a camera spark while the character waits) **[str]**.
  On/off on its own key (`flightKey`; controller: hold RB + L3). Jump to take off, Space climbs, C sinks, Left Shift
  flies fast, landing ends it **[str]**.
- Mode **[str]**: *Off*, *Unlimited*, or *Building area only* (inside a base's build zone; leaving it in the air lands the
  character safely). A HUD hint shows "exit character flight" while flying.
- Game systems **[sym]**: the locomotion state (a forced *Flying* state), the movement system's per-state mover
  dispatch, gravity's strength for the actor, the player input system (jump, sprint, sneak, glider, head-dive actions),
  the air-jump gate, the bases' build zones (setter and query). Runs where movement runs: the server and the player's
  prediction.

### 4. No fall damage (client and server)
"Land safely from any height" **[str]**. Hooks the fall damage system's "is falling" decision **[sym]**.

### 5. No building costs (server, plus a client preview)
Building and placing props take nothing and need nothing; upgrading or converting a storage chest in place is free
too **[str]**. Hooks the building payment, the build refund, the chest upgrade's payment, and on the client the build
preview's "missing materials" check **[str] [sym]**.

### 6. No crafting costs (server, plus the client's crafting UI)
Crafting takes nothing and needs nothing; arrows, bandages and other consumables are not used up **[str]**. Hooks the
recipe payment and the two places that remove a fired or used consumable **[sym]**.

### 7. Time of day (server)
- **Time of day**: jump forward to a chosen hour **[str]**.
- **Day and night cycle**: *Normal*, *Stopped* (the sun stands still, sleeping doesn't move it), *Fast* (a whole day
  and night in about a minute and a half) **[str]**. Hooks the server's daytime tick and the sleep system's wanted time
  scale **[sym]**.
- Only in a world the player hosts, or for an admin of a server that runs the mod **[str]**.

### 8. Map reveal (client)
- **Map reveal radius**: *Default*, *200 m*, *400 m*, *Whole map* **[str]**. The client's fog of war uncovers a larger
  circle around the player each frame, or every tile at once **[sym]** (`map.discover`, `map.paintCircle`, the fog
  tiles). What it uncovers stays uncovered for that character; kept per world and character in the client settings,
  with a one-time warning **[str]**.

### 9. HUD additions (client)
Rows in the HUD's building-area box and an entry in the HUD's status (buff) listing **[str] [sym]**.

### 10. Settings, server rules, permissions
- Client `settings.json`: `menuKey`, `flightKey`, the map reveal per world and character, the warning's "don't show
  again" **[str]**. Server `server.json`: `pickerMode`, gear, `flightMode`, `noFallDamage`, `noBuildingCost`,
  `noCraftingCost` (older: `noResourceCost`) **[str] [dec]**.
- On a server, the server's settings apply to every player. An **admin** (read from the server's player slots: groups
  and permissions **[sym]**) changes them from the menu, for everyone at once; others are refused and logged
  **[str]**. In a world the player hosts, the host's settings rule.
- Version check: the client asks the server whether it runs the mod; answers: runs it, doesn't, runs another version
  (then taking items and flight are off) **[str]**.

### 11. Multiplayer transport (how a client talks to the server)
No network layer of its own: a client request rides on the game's own **crafting request** (a craft queued with a
carrier recipe id and the request in spare fields); the server hooks its crafting action system and takes those
requests out **[str] [sym]**. The server answers through the game's **knowledge** replication (a counter knowledge
value it sets for the player) **[sym]**. A vanilla server ignores the requests.

## What Kiln had before this work (Kiln.Rules `/creative`)
Per-player rule sets; `/creative` (admins; a hosted world's host) gives: the game's building fly mode anywhere with the
character carried to where the flight ends, building anywhere (outside bases, no-build zones, near enemies, in the
Shroud), free building with no refunds, one-hit mining, invulnerability, health/stamina/mana kept full, water kept
full, a frozen Shroud timer, and a plane-like glider. Kiln also has `Items.Give` with level and rarity, `Time.Set`,
`Combat`'s fall events, the Kiln overlay (screens, key bindings, HUD lines), Kiln.Network channels, and permissions.

## Gap table
Size: S (hours), M (a day), L (several days). Risk: what breaks on a game update or in multiplayer.

| Creative Mode feature | Kiln before | Now | Size | Risk / notes |
|---|---|---|---|---|
| Admin-gated creative per player | has (`/creative`, node `kiln.command.creative`) | has | - | - |
| Server-wide rules for everyone | has (`RuleScope.Everyone`, presets, `rules.json` limits) | has | - | different model: Kiln applies a preset per player or scope |
| Creative menu window | missing | **done**: Kiln's Creative screen (`F7`, pause-menu entry) | M | overlay window, not game widgets: no game look-alike slots, no drag and drop |
| Item picker: take 1 / a stack / N | missing (API `Items.Give` only) | **done** (server-checked, any build with kiln.items) | M | low |
| Item customization: level, rarity | API only | **done** (level, rarity) | S | low |
| Item customization: upgrades unlocked, gem slot added | missing | **done** (part 3): menu Upgrades 0-5 and Gem slot; `Gear.Unlocked`, `Gear.GemSlot` (kiln.items createPide hook) | M | lab-verified; persistence across a restart not checked |
| Drag and drop into a backpack slot | missing | **partial** (part 4): Items > Backpack, a Kiln-drawn grid of the backpack's slots, pick-and-click into a chosen slot (server-checked); no drag gesture, no drop on the game's own inventory: Part 4 section 4 | M done; drag gesture S; game inventory L | game inventory UI hooks, client only |
| Picker filter "without equipment" | missing | **done** | S | low |
| Journey picker mode (only discovered items, level cap) | missing | **done**: "Only what I have found" (server item-knowledge reads); level capped at the character's level (part 3, server side) | M | knowledge and level reads need a joined player (not lab-testable) |
| Catalogue hiding debug/unused items | missing | **done** (Kiln's own name rules: 33 of 4,213 left out on svn 1287369) | S | heuristic |
| Character flight (the character flies) | partial (the game's fly mode anywhere, body carried) | **built, off by default** (part 4): `CharacterFlight` Off / Unlimited / BuildingArea, BuildingFlying state, gravity, vertical input; `rules.json` `allowCharacterFlight` | L | server-side lab-verified; prediction, animation and camera need the manual test |
| Flight only in building areas | has (the game's own fly mode is base-only) | has | - | - |
| No fall damage | has in `/creative` (its invulnerability cancels falls) | **done**: a standalone `NoFallDamage` rule for other presets | S | server only (Kiln.Combat fall event) |
| No building costs + missing-materials preview | has (`BuildCost` 0 lifts both) | has | - | - |
| Free storage-chest upgrade | missing (the upgrade pays outside `ApplyMaterials`) | **done**: `rules.storage` waives the upgrade's own payment with `BuildCost` 0 | S | server hook on the inventory payment, matched by return address |
| No crafting costs | missing | **done** (part 3): `FreeCrafting` as the recipe payment answering "paid" inside the game's transaction (both payers), and the crafting menu's craft buttons (client hook on the craftable amount) | M | server lab-verified; the buttons need the client checklist |
| Consumables not used up | missing | **done** (parts 3 and 4): `KeepConsumables` skips the three sequence-event removals: arrows, thrown items, health and mana potions, healing food (the spawn path, SpawnEntityEvent and SpawnImpact), staff ammunition, and food and drink buffs, bandages, lotions, flasks (ApplyBuffEvent) | M | needs a player using them (client checklist) |
| Time of day jump | has (`Time.Set`) | **done** in the menu and `/creative time` | S | low |
| Day/night cycle stopped / fast | missing | **done** in kiln.time (`Time.SetCycle`), menu and `/creative cycle` | M | server hook on the daytime speed setter (docs/re/time.md); clients follow the game's own daytime sync |
| Map reveal radius / whole map | missing | **done** (part 3): World > Map reveal (game's, 200 m, 400 m, whole map; session only, creative on) | M | client only: client checklist |
| Configurable keys | has (`KeyBindings`, Kiln menu Keys tab) | has | - | - |
| HUD status line | has (`Hud.Show`) | **done** (creative status line) | S | - |
| Item stats fix for given items | n/a | n/a | - | Kiln gives through the game's own spawn path |
| Fail soft on unknown builds | has (sigdb features) | has | - | - |
| Creative Mode itself running in the same game (kiln-flame) | n/a | **done** (part 4): Kiln's creative mode stays idle, its creative hooks are not installed | S | detection through `kiln.flame`, at the first world |
| Server detection, version check | has (Kiln.Network handshake, channels) | has (`kiln:creative` channel) | - | - |
| Admin ops for remote tools | partial (`creative`, `rules`, `time_set`) | **done** (`creative_give`, `creative_cycle`) | S | - |

What "done" covers, how it was tested and what is left: `docs/kiln/services/rules.md` ("Creative menu"),
`docs/kiln/commands.md` (`/creative`).

## Part 3 research (agent creative3, 2026-11-07): the game functions behind the remaining features

Static analysis of the game executables only (Kiln's own work, nothing taken from the mod): client 26430625
(`enshrouded.exe`, `5d9ef2f1…`), server 26430253 (`enshrouded_server.exe`, `f0d824be…`). RVAs are image-relative, client
in brackets. Tools: `tools/sigport/exeinfo.py` function tables, capstone, the client's reflection dump
(`tools/kfc/.out/vfx-build/types/5d9ef2f1….json`) for field names. Tags: **[dis]** disassembly, **[refl]** reflection
names and offsets, **[inf]** inference, not yet run. Component hashes below are FNV-1a-32 of the reflected type name
(`keen::ecs::<Name>`), so they hold across builds. Every server-side site below exists unchanged in the client exe too
(a hosted LOCAL world runs the server world inside the client), and every draft pattern was checked unique on both
exes unless it says "client only".

**Status (Phase B, 2026-11-07).** Built and lab-verified on the server: free crafting (both payers), gear upgrades and
gem slot. Built, hook installs, needs a player: kept arrows / thrown items / staff ammunition. Built, client only (client
checklist): map reveal, craft buttons. Journey level cap: server side, needs a joined player. Not built: character
flight (research below and a lab probe), food and potions not used up, drag and drop. Code: `native/services/rules/
creative.cpp`, `native/services/items/pide.cpp`; docs: `docs/kiln/services/rules.md`, `docs/kiln/services/items.md`.

**Public source check (2026-11-07).** Neither Creative Mode nor its "Flame" runtime (`flame.json`, `FlameStartRuntime`)
has public source: GitHub repository and code searches and a web search found nothing, and the archive carries no
licence or source link. The clean-room rule stands. The only related open project is Shroudtopia
(github.com/s0t7x/shroudtopia, MIT, commit `5e734dd`, cloned read-only to `/mnt/Dongus/kiln-wave/creative3/src-ref/`).
Its `flight_mod` is not character flight: it replaces one float load in the glider code (a 2024 build, svn 637515) to
change the glide sink. It is of no use for items 1-7 beyond confirming that the glider is the only flight the game
gives a player. The EML / kfc-parser project (Brabb3l) is a data patcher and exporter (GPL, never copied into Kiln).

### 1. Map reveal (client only)
- **The fog object** **[dis]**: `FogOfWar` (0x48 B) is created by `[0x3a1970]` (client world setup `[0x3a60b0]`, stored at
  `clientWorld+0x3dc0` and registered as ECS system data named `"FogOfWar"`): `+0x00 float2` map size in metres, `+0x08
  uint2` tile counts (`ceil(size × 0.125 / 32)`: one texel per 8 m, 32×32-texel tiles of 1 KiB), `+0x18` tile texels,
  `+0x20` tile count, `+0x28` a dirty-tile bitset, `+0x40` dirty count (the renderer `[0xd9b970]` uploads dirty tiles; the
  save is read by `[0x3a1da0]`, "[fog_of_war] …" log lines).
- **The system** **[dis] [refl]**: ECS program `fog_of_war`, descriptor at `[.rdata 0x1e2b2c0]`: reads
  `FogOfWarDiscovery` (`discoveryRange` f32 +0), `PlayerState`, `RenderTransform`, `LocalPlayerData`, system data
  `FogOfWar`; update `[0x30c670]`. Per entity: only the local player (`[0x3bb740]`), not while `PlayerState+0x34` bit
  `0x40000` is set; position = RenderTransform x/z (32.32 fixed point × 2⁻³²), then the one call below.
- **`FogDiscover(FogOfWar*, const float2* posXZ, float range)` `[0x3a2780]`** **[dis]**, its only caller is the system:
  radius in texels = `range × 0.125 × 0.5`, so **`discoveryRange` is a diameter** (a "200 m radius" reveal is
  `range = 400`). It clamps the circle's bounding box to the map (texel y = `size.y − z`), paints each touched tile
  with the SIMD circle painter `[0x1c540]` (ISPC-style dispatch), and marks changed tiles dirty. Tiles once painted stay
  painted; the client saves them with the character.
- **Plan**: a detour on `FogDiscover` (kiln-rules client side, like `rules/client.*`): Default = untouched; 200 m / 400 m
  = `range = max(range, 2 × R)`; Whole map = once per `FogOfWar*` (a new pointer after a world load reveals again) call
  the original with ranges that cover the map. A single call with a huge range would mark every tile dirty in one
  frame (1,600 tiles for a 10 km map; the upload `[0xd9b970]` allocates a per-frame upload buffer and logs
  "allocateDynamicUploadBuffer … failed" when it cannot), so sweep it: a grid of circles over a few dozen frames,
  map size from `+0x00`. Off on an unknown build (the pattern pins the function body). No server part.
- Draft sigdb (client only):
  ```json
  "rules/client.FogDiscover": { "comment": "FogDiscover(FogOfWar*, const float2* posXZ, float range): the fog_of_war system's per-frame discover call; range is a diameter in metres",
    "find": [ { "pattern": "40 53 56 57 41 56 41 57 48 81 EC 80 00 00 00 48 8B 79 {rules/client.fow.tiles:d8} 4C 8B F1 F3 0F 10 05 ?? ?? ?? ?? 44 8B CF 0F 29 7C 24 60" } ] },
  "@rules/client.FogDiscover.caller": { "comment": "the system's call: range from FogOfWarDiscovery in xmm2",
    "find": [ { "pattern": "F3 0F 59 C6 F3 0F 59 CE F3 0F 11 84 24 80 00 00 00 F3 0F 11 8C 24 84 00 00 00 F3 0F 10 10 E8 {rules/client.FogDiscover:rel32}" } ] }
  ```
  `rules/client.fow.tiles` = 8 (the map size is the float2 at +0, read with the tile counts as a sanity check).

### 2. Unlocked upgrades and a gem slot on taken gear (server)
- **createPide** `0x1b1920` `[0x411e20]` (sigdb has none yet; docs/re/items.md section 10) **[dis]** builds a list of
  component overrides `{u32 componentHash; void* data; u64 size}` (24 B entries; pointer `[rbp-0x68]`, count
  `[rbp-0x60]`, capacity `[rbp-0x58]` = 8 inline, grown through `[rbp-0x50]`) and spawns the item's pide template with
  it (`0x5fb340` `[0x95f7f0]`, call at `0x1b1e9d` `[0x41239d]`). Entries, by hash **[refl]**: `UsedItem 0x0b02361d` (4),
  `ItemState 0x3a2fac75` (20), `OwnerRelationship 0xd7361788` (4), `Level 0xe550da37` (16: dataStorage `u32[2]` at +8,
  current at index `[0x14c6148]` = 0, max at `[0x14b0de0]` = 1), `FuelPercentageInit 0x6d3a006d` (4),
  `PerkContainerNew 0xe288d50c` (2: `availablePerks`, `unlockedPerks` = 0), and for `ItemInfo+0xCC == 2` (Weapons)
  `WeaponGemSlot 0x84ba9b52` (12 B of zeros = an empty `ItemStack`) when the BalancingRegistry chance roll wins or
  `ItemInfo+0x798 gemSlotOverride` is set.
- **What an unlock is** **[dis]**: the free perk step of `inventory_actions` (`0x19fe00`) does `unlockedPerks += 1` (only
  while `< availablePerks`) and the Level attribute (`0x724bf568`) `+= 1` through `0x19d7b0` (AttrFind, write, `0xab970`
  recompute), then an `ItemPerkChanged`-style event. So a fully upgraded item is `unlocked = available` and
  `Level.current = base + available` (docs/re/items.md: "LEVEL 54/54 on an instance given at level 50 with 4 perks").
- **Implementation**: a mid hook on that spawn call (kiln.items runs every Give inside its own program). The
  thread-local options scope around `InventoryCommit` checks the `UsedItem` override against the give's item id and
  ends even when the commit faults; ordinary loot and crafting have no scope. For a Give with
  `Unlocked`: find the `PerkContainerNew` and `Level` entries in the list and set `unlocked = available`,
  `current += available`. With `GemSlot`, on a Weapon whose list has no `WeaponGemSlot` and `count < capacity`: append
  `{0x84ba9b52, &zero12, 12}` (a static zeroed buffer, alive until the spawn copies it). Everything else spawns as the
  game made it. `rebalance_perks_and_level` later re-clamps `Level − unlocked` into the item's range, which our base
  level already is. Lab-testable: give, then read `PerkContainerNew`, `Level`, `WeaponGemSlot` back with kiln.world.
- Draft sigdb (server and client):
  ```json
  "items/CreatePide": { "find": [ { "pattern": "48 89 5C 24 20 55 57 41 54 41 55 41 56 48 8D AC 24 50 FF FF FF 48 81 EC B0 01 00 00 45 33 E4 C6 85 F0 00 00 00 FF" } ] },
  "items/PideSpawnCall": { "comment": "createPide's spawn of the pide template; mid hook on the call: rbp-relative override list, rdi = ItemInfo",
    "find": [ { "scope": { "symbol": "items/CreatePide", "size": 1600 },
      "pattern": "48 8B 4D {items/pide.list:d8} 48 8D 97 4C 05 00 00 48 89 8D 80 00 00 00 45 8B CC 48 8B 4D {items/pide.count:d8} 44 8B C3 48 89 8D 88 00 00 00 0F B6 0D ?? ?? ?? ?? 88 4C 24 38 48 8D 8D 80 00 00 00 C6 44 24 30 04 48 89 4C 24 28 48 8B C8 48 89 54 24 20 48 8D 54 24 48 E8",
      "offset": 76 } ] },
  "@items/CreatePide.category": { "find": [ { "scope": { "symbol": "items/CreatePide", "size": 1600 }, "pattern": "80 BF {items/item.category:d32} 02 48 8B B5 ?? ?? 00 00 0F 85" } ] }
  ```
  `items/pide.list` = -0x68, `items/pide.count` = -0x60 (signed d8), the capacity is the qword after the count;
  `items/item.category` = 0xCC.

### 3. Crafting without the inputs in the game's crafting menu (client only)
- **Path of a craft click** **[dis]**: the crafting screen's update `[0xc79250]` (one caller, `[0xc75840]`) asks
  **`UiCraftableAmount(gameUi, u32 recipeId, u16 cap, const RecipeInputCategorySelections*)` `[0xb2ddd0]`** how many it
  can make (cap 1 for "craft", 10 for "craft 10"), stores it in the recipe entry and passes it to the button hints
  (`[0xd21120]`), which enable or disable the craft actions. The action handler `[0xd13120]` only queues a craft when an
  enabled action of type 0 (one) or 0x1b (ten) is pressed, through **`UiQueueCraftRecipe(ctx, recipeId, station,
  amount, selections*)` `[0xd6e460]`**, which writes UI command 0x37 (55). The UI dispatcher `[0x85c7f0]` (case 55) turns
  it into `UiCraftRecipeEvent` (56 B: operator, station, recipe, amount, selections) without checks, and
  `[0x3af670]` copies UI events into `ClientPlayerInput` for the server.
- `UiCraftableAmount` itself is a cache lookup (a map at `gameUi[0xd0]→[0]→+0x38 + 0x14f70` keyed by `{1, recipe, cap,
  selections}`, the count filled in asynchronously; 0 until then). Callers: the crafting screen, the flame altar
  `[0xbc3f70]`, and `[0xcee020]`, `[0xd90b90]`, `[0xd9a230]` (other recipe views).
- **Plan**: a client hook on `UiCraftableAmount` returning `max(result, cap)` while the local player's creative
  FreeCrafting is on (the server tells the client over the `kiln:creative` channel). The server already answers the
  craft with the output and takes nothing (FreeCrafting). Ingredient rows keep showing "missing" in red (their own
  per-ingredient counts); only the buttons change. **[inf]** until a client check: whether some view also needs the
  ingredient counts to enable the button.
- **The server half, inside the game's own transaction** (replacing the disabled "cancel and give" path): the recipe function
  `items/RecipeApply` `0x19a1d0` `[0x3f8f30]` pays the inputs with `rules/InventoryPay` `0x199c80` `[0x3f89e0]` (the
  payment kiln.rules already hooks for the storage upgrade) or, for recipes with flag `0x40`, with a second payer
  `0x19a9b0` `[0x3f9700]` (same arguments `(tx, costs, owner, multiplier, extra)`, its only caller); flag `0x10` is the
  game's own "free recipe". Return 0 = paid, then the output goes in through the same transaction **[dis]**. So
  FreeCrafting can be the payment hook answering 0 when the return address is the recipe function's call
  (`0x19a28c` `[0x3f8fec]`) and the transaction's operator (`items/tx.operator`) has FreeCrafting, plus a detour on the
  second payer with the same test. Stations (operator = the station) and everyone else pay as usual. Draft:
  `"rules/RecipePaid": { "find": [ { "scope": { "symbol": "items/RecipeApply", "size": 256 }, "pattern": "A8 40 75 07 E8 {rules/InventoryPay:rel32} EB 05 E8 {rules/RecipePayAlt:rel32} 85 C0 0F 85", "offset": 9 } ] }`
  (unique on both exes; `offset` 9 is the return address of the first call, the second payer is captured).
- Draft sigdb (client only): `"rules/client.CraftableAmount": { "find": [ { "pattern": "48 89 5C 24 10 48 89 74 24 18 57 48 83 EC 70 48 8B 81 ?? ?? 00 00 0F 57 C0 41 0F 10 49 10 48 8B F1 4C 8B 10 49 8B 7A 38 0F 11 44 24 40 48 81 C7 ?? ?? ?? ?? 66 44 89 44 24 44" } ] }`.

### 4. Arrows, spell ammunition and thrown items not used up (server)
- The two removal sites are **[dis]** the spawn path's `consume` (`0x42390`, SpawnEntityEvent with `consume`: bows,
  throwables) and the usage-cost payer `0x40d00` (`PayUsageCost.consume`: staffs' spell ammunition) (docs/re/abilities.md).
  Each builds its own inventory transaction and calls `items/InventoryRemoveItems` (`0x1b0d90` `[0x411360]`) with
  `owner = current entity` (the actor), the used item and count 1, then commits only when `!out.error && out.value ==
  0`. After the commit the cost payer re-equips ammunition when the stack ran out (`0x93a5b0`, `equipItem`).
- **Plan**: like the storage-upgrade payment (`rules.storage`): hook `InventoryRemoveItems` and answer
  `{error 0, value 0}` without touching the transaction when the return address is one of the two sites below and the
  owner's rules have `KeepConsumables`. The commit then has no op; nothing is removed and nothing re-equips. Validate
  each return address (it must follow `E8 rel32` to the hooked function), as `upgrade_call_valid` does. Lab: a spawned
  character with KeepConsumables and a bow cannot fire without a client; the hook can be driven by an ability test
  (`Abilities.PlaySequence` has no used item, so check `Kiln.Abilities` first) or verified by return-address resolution
  and a client checklist.
- Draft sigdb (server and client; the hook needs the return address = pattern start + 38):
  ```json
  "rules/AmmoConsumed": { "comment": "the spawn path's consume: removeItems(out, tx, actor, usedItem, slot, 1); returns here",
    "find": [ { "pattern": "C7 44 24 28 01 00 00 00 48 8D 95 ?? ?? 00 00 44 8B CF 4C 89 7C 24 20 48 8D 8D ?? ?? 00 00 44 8B 00 E8 {items/InventoryRemoveItems:rel32} 80 BD ?? ?? 00 00 00", "offset": 38 } ] },
  "rules/CostConsumed": { "comment": "PayUsageCost.consume: removeItems(out, tx, actor, costItem, slot, 1); returns here",
    "find": [ { "pattern": "45 8B 4C 24 08 48 8D 95 ?? ?? 00 00 C7 44 24 28 01 00 00 00 48 8D 4C 24 30 48 89 5C 24 20 44 8B 00 E8 {items/InventoryRemoveItems:rel32} 44 38 6C 24 30", "offset": 38 } ] }
  ```

- KeepConsumables uses these two call sites instead of slot-change notices, which cannot prove a use. Food, drinks and potions go
  through other removal sites; the other callers of `InventoryRemoveItems` are `0x75bc0` (a program),
  `0x14d750`, `0x1a1230` (from `inventory_actions` `0x1a5af0`, writes an event of type `0x103e50ea` after the removal)
  and the wrapper `0x1b0d60`. Which one is eating and drinking is not confirmed yet **[inf]**.

### 5. Journey level cap (no RE needed)
The mod caps gear levels at the character's level in Journey mode (feature 2 above). Kiln.Players' `Progression`
already reads the character's `Level`; `CreativeServer.GiveAsync` applies `CreativeTools.JourneyLevel` when the
request's Journey option is set. The base level leaves room for the requested upgrades (bounded by the requested
rarity's perk count, or five when rarity is unset). Unknown character levels and gear whose minimum cannot fit are
refused. This checks the final level on the server, including requests with no explicit base level.

### 6. Character flight (server and the player's prediction)
- **Locomotion states** **[refl]** `keen::ecs::LocomotionState`: Walking 0, Sliding 1, Falling 2, Flying 3, HangGliding 4,
  HookPulling 5, HookSwinging 6, ClimbingWall 7, ClimbingLadder 8, Swimming 9, Diving 10, BuildingFlying 11. The state
  is `DynamicLocomotion+0x59`.
- **The mover dispatch** in `locomotion_execution` (`0x1dcf90` `[0x43d6d0]`, jump tables at `0x1dd98c` / `0x1dd9bc`)
  **[dis]**: Walking `0x1d4370`/`0x1d4870`, Sliding `0x1d3260`, Falling `0x1d12a0`, HangGliding `0x1d2320` (the glide
  step), Swimming `0x1d3900`, Diving `0x1d0d70`, **BuildingFlying `0x1d09d0`**; Flying (3), the hook and climbing states
  have no case in that dispatch. **Corrected 2026-11 (dev.28 RE, S26430253 `f0d824be…`, C26430625
  `5d9ef2f1…`) [static] [lab]:** the following common step `0x1d16c0` / C `0x431e20` **is the Flying mover**
  for actors with Flying/DynamicFlying; lack of a switch case is not lack of movement. See [mounts.md](mounts.md).
- **The state decision** `LocoNextState` `0x1c5180` `[0x4259a0]`, called from the state program `0x1e0240` (`0x1e043a`
  `[0x440b7a]`; result written to `+0x59` at `0x1e047a`) **[dis]**: **if the state is BuildingFlying it stays
  BuildingFlying**, before any other test; then ladder 8, wall 7, hooks 5/6, an actor-state flag 0x27 → Flying 3, …
  So a player entity whose state is set to 11 keeps it, and moves with the building spark's own mover.
- **The BuildingFlying mover** `0x1d09d0` **[dis]**: steers `Velocity` toward the desired movement (iterator +0xf8) with
  an acceleration row picked by two ActorInput flags (`0x35` = Sprint 53, and `0x15`) from the movement config at
  iterator +0x10 (`0x1d0470` does the step). What feeds the vertical part for the spark (jump / crouch while building)
  is not traced yet: `player_control_building_fly_mode` copies the player's ActorInput into the spark's; the player's
  own control writes `desiredLocalMoveInput = (stick.x, 0, stick.y)` (docs/re/rules.md section 7).
- **Gravity** `gravity_apply_velocity_actor` `0xa0de0` `[0x2a3510]` does not look at the locomotion state: Velocity +=
  dir × strength × dt unless `Gravity+0x10` (enabled) is 0 or the actor is grounded. So a flying player also needs
  gravity off. The glider's mid hook sits on the strength selection (`rules/GlideGravity`, `0xa0f13`); a separate mid
  hook right after the StepDelta call (`0xa0f1f` `[0x2a364f]`, no shared bytes) can zero `xmm6` for flying entities
  without touching the glide code. Draft: `"rules/FlightGravity": { "find": [ { "pattern": "F3 0F 10 72 40 48 8D 55 ?? 48 8B CF E8 ?? ?? ?? ?? 48 8B 4D ?? 48 8B 18 48 8B 45 ?? F2 0F 10 00 8B 70 08", "offset": 17 } ] }`
  (unique on both exes; the actor comes from `[rbp + rules/grav.actor]` as for the glider).
- **Plan, behind its own feature flag (`rules.flight`, off by default)**: entering flight writes state 11 on the
  player entity (server, from the rule) and the same in the client's prediction (the local player's rule, as the glider
  hooks do); landing writes Falling (2), and "Building area only" lands the player when the build-zone test fails. A
  mid hook after `LocoNextState` returns (`0x1e043f`) can hold 11 for a flying entity and drop it otherwise, so the
  state never sticks without the rule. Open before it can work: the vertical input (Space / C) for a player that is
  not the spark, how `DynamicNetworkLocomotion.state` replicates 11 to the client and to other players (animation:
  the player graph has no BuildingFlying animation; `locomotion_animation` picks from a fixed table, docs/re/npc-internals.md),
  and air-jump / glider / dive inputs while flying. Size L, high risk, as the gap table says.
  Draft: `"rules/LocoNextState": { "find": [ { "pattern": "48 89 5C 24 10 48 89 6C 24 18 48 89 7C 24 20 41 56 48 83 EC 40 41 80 78 {rules/loco.state:d8} 0B 49 8B E9 49 8B F8 48 8B DA 4C 8B F1 75 ?? B0 0B" } ] }`,
  `"rules/LocoStateWrite": { "find": [ { "pattern": "E8 {rules/LocoNextState:rel32} 48 8B 55 ?? 0F B6 D8 38 5A {rules/loco.state:d8} 74", "offset": 5 } ] }` (`rules/loco.state` = 0x59).

- **Lab probe** (`mods/tests/creative-test` FlightProbe, INFO only, lab `wv-creative3-a`, 2026-11-07) **[rt]**: a raw write of
  `DynamicLocomotion+0x59 = 11` on a spawned player character holds: "step 1: state 11 … step 50: state 11" (2 s), and
  writing 2 (Falling) back lands it ("state 2, grounded True"). So the sticky state is real on a player entity. The
  character stood on the ground (the probe's 30 m teleport did not apply within 60 steps after the fall test), so whether
  gravity pulls a BuildingFlying player down is still open; the gravity code says it does.

### 7. Drag and drop from the creative menu into the backpack
Kiln's creative menu is an ImGui overlay window, not a game widget, so a drag from it cannot land on the game's
inventory slots: the game's drag and drop lives in its own UI (the HUD screens drawn by `gameui`), which Kiln does not
draw into. What it would take: either (a) a game-UI drop target, i.e. hooking the inventory screen's drop handler
and feeding it a synthetic "dragged stack" (client RE of the inventory UI's drag state, plus a server request to put
the item into the exact slot: kiln.items' `Give` already has a slot parameter), or (b) a
Kiln-drawn copy of the backpack grid inside the overlay that sends "give into slot N" (kiln.items has both slot
selection and inventory reads; the creative request would need to carry the slot). (b) is M, (a) is L with client-only RE. Not built.

## Part 4 (2026-11-07): Creative Mode yields, food and potions, gear persistence, character flight, the backpack grid

Same executables and tags as Part 3 (server 26430253 `f0d824be…`, client 26430625 `5d9ef2f1…`; **[rt]** seen in a lab).

### 1. Kiln steps aside for Creative Mode
kiln-flame publishes `kiln.flame` (`running("creative")`) from its `on_ready`, which runs after every service's
`on_ready` (services load before plugins). So kiln.rules installs its creative hooks and the inventory payment at the
first world instead of at boot, after asking `kiln.flame`; Kiln.Rules asks it from managed code (`CreativeFlame`). While
Creative Mode runs: no creative hook (free crafting, kept consumables, storage upgrade, map reveal, craft buttons,
character flight), `/creative on`, takes, `/creative time|cycle`, the menu and the map reveal are refused or idle, and
the state sent to Kiln clients carries `CreativeAccess.Idle`. kiln.rules' other hooks (fly gate, cursor, costs, glider,
mining, Shroud) are installed at boot as before: they act only for players with Kiln rules, and Kiln grants none of
creative's while Creative Mode runs; if Creative Mode inline-hooks one of the same functions it hooks over Kiln's jump.
kiln.time's clock-speed hook is not touched (Kiln refuses cycle changes, so it passes through). Lab: the dummy Flame mod
test (`native/plugins/compat` `flame_test.dll` as `mods/creative/`), see below.
Review follow-up: native pass-through guards also cover late detection; active Kiln creative sets and its held clock
speed are retired on the next server tick. Pending/entered reservations remain sticky across a failed start. Character
flight ownership uses entity plus locomotion-object identity, lasts until the flight ends, and resets on world changes;
no wall-clock timeout decides whether a sticky state falls.

### 2. Food, drinks, potions and bandages
The consume events in the game data (listed by `mods/tests/creative-test` from kiln.gamedata) **[rt]**: ApplyBuffEvent 11
(`UseItem_Bandage_buff`, `UseItem_Food_Liquid_Buff`, `UseItem_Food_Buff`, `UseItem_Flask_FogElixir`,
`UseItem_Potion_Ice_Protection_Lotion`, scrolls), SpawnImpact 15 (`UseItem_Potion_Health`, `UseItem_Food_Heal`,
`UseItem_Potion_Antidote_RemovePoison`, ...), SpawnEntityEvent 34 (`UseItem_Potion_Mana`, `UseItem_Potion_FogResistance`,
throwables), PayUsageCost 40, PlaceVoxelObjectEvent 1. `actor_spawn_entity` (`0x454f0`) runs SpawnImpact and
SpawnEntityEvent through the spawn path `0x42390`, whose one removal is Part 3's `rules/AmmoConsumed`: health potions and
healing food were already kept. The buff ones go through `actor_apply_buff` (`0x75bc0` `[0x262f40]`) **[dis]**: one
transaction, `InventoryRemoveItems(out, tx, current entity, used item, slot, 1)` (call `0x76184`, returns `0x76189`
`[0x263049]`), then on `{0, 0}` `applyBuffCore` (`0x15e130`) and commit, else rollback. `rules/BuffConsumed` (feature
`rules.consume_buff`) is that return address; KEEP_CONSUMABLES answers `{0, 0}` there too, so the buff applies and the
commit has nothing to remove. The other callers of the removal are not consumables: `0x14d750` (fishing bait,
`server_player_fishing`), `0xa4e80` (`interaction_attempt`), `0xb3560` (`player_cooking`), `0x144bb0`
(`equipment_actions`), `0x1a1230` (`inventory_actions`: an item applied to another item, event `0x103e50ea`), the
crafting payers, and `0x469c0` / `0x46bc0` through the wrapper `0x1b0d60` (`animal_food_consumption`, feeding troughs).

### 3. Upgraded gear across a save and a restart
Lab (below): three weapons given into a chest (plain Epic, all upgrades and a gem slot, two upgrades), the world
autosaved, the lab restarted, the chest read back.

### 4. The backpack grid (drag and drop, M) and what the rest takes
Kiln's overlay has no drag gesture: `UiDraw` exposes no ImGui drag-and-drop source/target or mouse state. Built: a
Kiln-drawn grid of the player's backpack (the player's Generic inventories, 8 slots each) in the creative menu; **Place...**
picks the selected catalogue item up and a click on an empty slot (or one with the same item) gives it into that slot
(`CreativeRequest.Slot`, a trailing field; `Items.Give(..., slot)`; the server refuses a slot outside the player's own
backpack, limited to initialized unlocked Generic slots, and rejects other items, occupied gear and full stacks). What is left:
- a real drag gesture inside the overlay: kiln.ui needs `BeginDragDropSource` / `SetDragDropPayload` /
  `BeginDragDropTarget` / `AcceptDragDropPayload` in its widget table and `UiDraw` (S, shared UI service, additive);
- a drop onto the game's own inventory screen (L, client RE): the HUD's inventory screen drop handler and its drag
  state (Part 3 section 7, option a), then the same slot give.

### 5. Character flight (built behind `rules.json` `allowCharacterFlight`, off by default)
Answers to Part 3's open questions **[dis]**:
- **Gravity.** `gravity_apply_velocity_actor` ignores the locomotion state. Kiln zeroes the strength at `rules/FlightGravity`
  (`0xa0f1f` `[0x2a364f]`, right after the StepDelta call; the glider's site `0xa0f13` covers 7 bytes before the call,
  no overlap) for a player who may fly; the Actor comes from the same function's grounded test (`[rbp-0x51]`).
- **The vertical input.** The BuildingFlying mover (`0x1d09d0` `[0x431130]`) steers `Velocity` toward
  `DynamicLocomotion.inputVelocity` (the iterator's DynamicLocomotion at `+0xf8`; the second float3 it writes is
  `velocitySpringAcceleration` `+0x40`); its acceleration row comes from the Actor's `Strafing` (53) and `InSneakMode`
  (21) flags (`0x3b7a0` is ActorStateTest). The movers share locomotion_execution's iterator (`lea rcx,[rsp+0x60]` for
  Falling `0x1d12a0`, HangGliding `0x1d2320` and BuildingFlying alike), so the glider's PlayerInput slot (`+0x50`) and
  input mask (`+0x3a8`) apply. Kiln sets `inputVelocity.y` before the mover from the held `Jump` (32) and `Sneak` (55)
  bits, `Sprint` (53) faster.
- **The state.** `LocoNextState` (`0x1c5180` `[0x4259a0]`) takes the Actor in `rdx` and tests its flags (Flying 39 →
  3, UnderWater 44, HangGliding 37 → 4, Grounded 0 → Walking/Sliding, else Falling 2); 11 stays 11 first of all. The mid
  hook `rules/LocoStateWrite` (`0x1e043f` `[0x440b7f]`): `al` the decision, `rsi` the ProgramContext through the loop,
  the Actor at `[rsp+0x58]`, DynamicLocomotion at `[rbp-0x70]`. Decision: `flight_math.h` (host-tested).
- **Animation.** `locomotion_animation` (`0x1d7b90`) switches on the state with `cmp sil, 0xa; ja default`, and the state
  name table (`0x1c5390`) covers 0..11, so state 11 on a player is bounded: it takes the default branch. What it looks
  like for other players is for the manual test.
- **Replication.** `DynamicNetworkLocomotion.state` (+0x24) carries the state to clients; the owner's prediction runs
  the same three hooks with its local rules (sent over `kiln:rules`, like the glider's), so both sides decide from the same input and server-authored flight permission. The v4 channel requires a per-connection
  acknowledgement of available client flight hooks. `BuildingArea` is resolved server-side from actual altar bounds
  every tick and sent as the same flight bit, rather than testing `IsNearBuildingZone` (build-anywhere forces that flag).
  Boundary latency and prediction are not proven on a real connection.
- **Not done:** the mod's air-jump and head-dive input changes (Kiln's flight starts from any airborne step instead of
  a jump in the air), a HUD hint while flying, and "lands the character safely" outside creative mode's invulnerability.

A lab (server 26430253, `mods/tests/creative-test`, a spawned player character, its PlayerInput held
bits written by the test): see docs/kiln/services/rules.md "Character flight" for the lines.


### Scoped review evidence (2026-11-07)
Read-only disassembly of server 26430253: `gravity_apply_velocity_actor` changes Velocity only; the separate
`movement_collider_velocity_actor` (`0xfeae0`) still runs the collider path, with no state-11 bypass. `addItems`
(`0x1b1f60`) calls `putStack` for the preferred slot; the latter rechecks the current item/gear/stack limit
(`0x1b2597..0x1b2619`), declining a nonempty mismatched item. The earlier managed slot check therefore also gives a
clean refusal, while the game's own execution-time check protects contents changed after submission. These are static
observations; no game or live server was launched for this review.
