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
| Item customization: upgrades unlocked, gem slot added | missing | missing | M | needs RE of the upgrade attributes; server only |
| Drag and drop into a backpack slot | missing | missing (Take puts items where the game puts them) | L | game inventory UI hooks, client only |
| Picker filter "without equipment" | missing | **done** | S | low |
| Journey picker mode (only discovered items, level cap) | missing | **done**: "Only what I have found" (server item-knowledge reads); no level cap | M | knowledge reads need a joined player (not lab-testable) |
| Catalogue hiding debug/unused items | missing | **done** (Kiln's own name rules: 33 of 4,213 left out on svn 1287369) | S | heuristic |
| Character flight (the character flies) | partial (the game's fly mode anywhere, body carried) | partial | L | locomotion on server and prediction; high risk; glider work in progress elsewhere |
| Flight only in building areas | has (the game's own fly mode is base-only) | has | - | - |
| No fall damage | has in `/creative` (its invulnerability cancels falls) | **done**: a standalone `NoFallDamage` rule for other presets | S | server only (Kiln.Combat fall event) |
| No building costs + missing-materials preview | has (`BuildCost` 0 lifts both) | has | - | - |
| Free storage-chest upgrade | missing (the upgrade pays outside `ApplyMaterials`) | **done**: `rules.storage` waives the upgrade's own payment with `BuildCost` 0 | S | server hook on the inventory payment, matched by return address |
| No crafting costs | missing | **done** server side: `FreeCrafting` (craft event canceled, recipe output given); `/creative` turns it on | M | the game's crafting menu still wants the inputs before it sends (client hook not done) |
| Consumables not used up | missing | **done** for food, drinks, potions, bandages: `KeepConsumables` (the used one is given back); arrows not | M | arrows need the ammo removal site (RE) |
| Time of day jump | has (`Time.Set`) | **done** in the menu and `/creative time` | S | low |
| Day/night cycle stopped / fast | missing | **done** in kiln.time (`Time.SetCycle`), menu and `/creative cycle` | M | server hook on the daytime speed setter (docs/re/time.md); clients follow the game's own daytime sync |
| Map reveal radius / whole map | missing | missing | M | client fog-of-war RE; client only, persistent per character |
| Configurable keys | has (`KeyBindings`, Kiln menu Keys tab) | has | - | - |
| HUD status line | has (`Hud.Show`) | **done** (creative status line) | S | - |
| Item stats fix for given items | n/a | n/a | - | Kiln gives through the game's own spawn path |
| Fail soft on unknown builds | has (sigdb features) | has | - | - |
| Server detection, version check | has (Kiln.Network handshake, channels) | has (`kiln:creative` channel) | - | - |
| Admin ops for remote tools | partial (`creative`, `rules`, `time_set`) | **done** (`creative_give`, `creative_cycle`) | S | - |

What "done" covers, how it was tested and what is left: `docs/kiln/services/rules.md` ("Creative menu"),
`docs/kiln/commands.md` (`/creative`).
