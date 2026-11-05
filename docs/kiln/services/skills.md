# kiln.skills: the game's skill tree for mods, and Kiln talent trees

Two ways for a mod to add talents:

- **A. Nodes in the game's own skill tree.** The game's skill-tree screen draws them, the game's skill points pay for
  them, and the game runs their effects. New nodes and links go into the game's `SkillTreeResource`, with texts per
  language, icons, effects (the program and config values of an existing node, Kiln programs, buffs and perks through
  kiln.gamedata) and ability unlocks. The game caps its tree at **256 nodes and 100 level slots**. Nodes past that are
  **shadow nodes**: Kiln keeps their unlock state and hooks the game's skill functions to read it.
- **B. Kiln talent trees.** A whole new tree that doesn't touch the game's: nodes on a grid, links, costs, levels,
  icons, texts and effects (attributes, buffs, game-node effects, programs, abilities, code). Its points are its own
  or a share of the game's. Kiln keeps each character's unlocks in its carried data (they follow the character between
  worlds, like the game's own skill nodes; [character-data.md](character-data.md)) and draws the tree as a screen in the
  game's look, opened from the pause menu or a key.

Evidence and layouts: `docs/re/abilities.md` §4 "Expanding the skill tree".

| Layer | Where |
|---|---|
| Symbols | `sigdb/symbols/server.d/skills.json` (shared by the client), client overlay `client.d/skills.json` (the screen) |
| C table | `native/services/skills/kiln_skills.h` (`kiln.skills` v1) |
| Native | `native/services/skills/` → `kiln/services/kiln-skills.dll` (`skills.cpp` service and hooks, `tree.cpp` the resource builder, `shadow.cpp` the shadow table) |
| Managed | `managed/services/Kiln.Skills/` → `kiln/host/Kiln.Skills.dll` (`GameTree`, `Skills`, `TalentTree`, ...; started by Kiln: `Kiln.SkillsStartup`) |
| Test | `mods/tests/skills-test/` (PASS / FAIL lines; fresh boot 55/0 with `shadownet.py`, the fake client for the client sync) |
| Sample | `mods/samples/SkillTreeSample/` (two nodes in the game's tree, the Kiln tree "Ember Arts") |

## A. The game's own tree

### Data: `skills/*.json` in the mod folder
```json
{ "format": 1,
  "gameTree": { "nodes": [
    { "guid": "3e2f6a1b-7c48-4d95-a1e3-5b9c0d7e8f21", "name": "mymod_ember_ward",
      "type": "Attribute", "class": "Mage", "like": "0xcfa5985b", "from": ["0xcfa5985b"], "offset": [170, -40],
      "cost": 1, "configs": { "0x0a0c87e7": 2 },
      "texts": { "en": { "name": "Ember Ward", "description": "+2 Intelligence." }, "de": { "name": "Glutwehr" } } },
    { "guid": "4f307b2c-8d59-4ea6-b2f4-6cad1e8f9032", "name": "mymod_emberheart", "class": "Mage",
      "like": "0xa598792e", "maxLevel": 3, "from": ["mymod_ember_ward"], "offset": [150, 0], "cost": 1 } ] } }
```
kiln-skills.dll reads these files natively at boot, for every mod in the load order (the profile's mods, or the
`kiln/mods` drop-ins), before any world exists.

| Field | Meaning |
|---|---|
| `guid` | fixed; the node id is FNV-1a-32 of its 16 bytes (the game's own `SkillNodeId` rule). Never change a released one: saves keep the id |
| `name` | debug name; other Kiln nodes refer to the node by it |
| `type` | `PassiveSkill`, `Attribute`, `ActiveSkill` (no new roots); default: the like node's |
| `class` | `Warrior`, `Mage`, `Ranger`, `Neutral` (the screen's colour); default: the like node's |
| `position` / `near` + `offset` | absolute tree units (the game's tree spans about 1700..6500), or relative to a node (default: the first `from` node, offset `[140, 0]`) |
| `from` / `to` | links into / out of the node. A node is a game node's `"0x<id>"`, a node GUID, or a Kiln node's name. The game unlocks along links in both directions |
| `bidirectional` | the link flag the game stores (all 312 game links have it false) |
| `cost`, `maxLevel` | skill points (per level); `maxLevel` 0 = one unlock, 1..7 = levels (the game keeps a level in 3 bits). Default: the like node's |
| `like` | a node whose effect to copy: its `impactReference` program, config values and tooltip values, icons and (without `texts`) texts. Write your `texts` for what that node does: `0xcfa5985b` sits among the Mage nodes but its AttributeReference is Intelligence (`2609852d-…`), so a node copied from it raises Intelligence |
| `program` | another ImpactProgram (a Kiln program from tools/impact, or a game one) |
| `configs` / `scales` | `{"0x<configId>": number}`: number configs (Float, Sint32, Uint32, Bool) of the like node, or scale factors of its scaled configs. The tooltip value paired with a config follows |
| `texts` | per language `{name, description, perLevel}`; also `displayName` / `description` for every language. Kiln hands them to the game's loca through kiln.content (`kc_nc_set_texts`); without it, the like node's texts show |
| `icon` / `iconFile` | a UiTexture GUID (e.g. an icon your `assets/assets.json` registers), or a PNG relative to the mod folder (kiln.assets serves it on clients). An icon that the UI cache doesn't serve when the tree is built falls back to the like node's: the cache stops the game on an unknown key |
| `unlocksAbility` | the `Ability_*` item GUID the node unlocks (`activeSkillUnlockRef`; its ItemId follows) |

Kiln's code way is `GameTree.Define(new GameNodeDefinition { ... })` (same fields), or `GameTree.DefineJson(json)`.

New config values that the like node doesn't have (e.g. a `BuffType` pointing at a kiln.gamedata buff clone) go
through kiln.gamedata's `skills` section on the new node's `0x` id. Its registry reads the expanded resource.

### When it takes effect
- Each world's `g38_skilltree::SkillTree` reads the resource **once**, in its loader. Kiln builds the expanded
  resource right before that loader runs and serves it as an override of the game's resource (kiln.assets). The game
  then builds its node map, link map, index map and id list from it itself. Lab: "the server world's skill tree
  loaded 261 nodes and 330 links (expected 261 / 330, Kiln's resource): node map 261 of 1024, link map 330 of 1024,
  index map 261 of 1024, id list 261 of 512".
- **Dedicated server:** managed mods load after the world has read its tree, so put game-tree nodes in
  `skills/*.json`. A definition from code is cached (`kiln/data/skills/defined/<mod>.json`) and is in from the next
  start, as long as the mod is in the load order.
- **Client:** mods load in the main menu, so code definitions are in time for every world. One build serves every
  world of the process (the hosted server world and the client world must agree). Definitions that change while a
  world holds the tree wait for the next world (`status.pending`).
- Nodes stay in a running world until it ends. A definition that goes away (mod unloaded) is left out of the next
  world, and the login guard refunds it there.

### Order and the caps
- `PlayerSkillsUi` (the replicated component that every "is X unlocked" check and the screen read) keeps **256 mask
  bits by tree index** and **100 level bytes by levelIndex**. Svn 1287369 uses 243 nodes and levelIndex 0..90. That
  leaves **13 native slots, 9 of them for nodes with levels**.
- **Who gets a native slot** is a pure function of the definitions: client and server have to agree without talking, whatever
  order they load their mods in. Mods with few nodes go first, then by mod id, then by node id, and a node gets a slot only once
  it hangs on a game node or a native node. So a mod stays whole; adding a mod displaces only nodes of mods with more nodes than it has,
  at most as many as it brings; no slot stays free while a linked node waits for one. Nothing is remembered between runs: a remembered
  order would differ between client and server. New nodes go behind the game's 243 in that order, with the shadow nodes behind them by id.
- **Shadow nodes:** every node past that (up to 4096 nodes in all) is a shadow node; see below. A Kiln node linked only to shadow
  nodes becomes one too: the game's own walkers would never reach it.
- Without the shadow hooks (an unknown build), nodes past the caps are refused (`definitions`: `error`), and the rest
  of the tree works.

### When the mod list moves a node (a character keeps its unlock and level)
A node can still be native in one mod list and a shadow node in another. Native unlocks live in the character's save, shadow unlocks in its
carried data, so a move crosses between them. Both directions keep the unlock and the level.

| Direction | What happens | kiln.log |
|---|---|---|
| Native to shadow | The login guard finds the id in the save, takes it out of the list and moves it into the table. Kiln.Skills keeps what came from the save for the session, so neither the table's first load nor a client's older carried copy, taken a moment later, can overwrite it, and writes it to the carried data, now its only record. A refund or respec afterwards counts | `shadow node 0x... of entity N: level 0 -> 2 (moved from the save)`, `shadow table: <player> has N shadow node(s), K of them moved from the character's save at this join` |
| Shadow to native | The carried entry of a node that is native in this tree goes to the game's own list once the game has set the tree up, one level at a time, through the game's rules: linked and affordable, with the points the table no longer counts. A node hanging on another waits for it. Then the carried entry goes | `... K that are native nodes in this world's tree (their levels go to the game's own list)`, `node 0x... is a native node in this tree: level N is in the character's list now` |
| Can't be kept | Shadow to native when the game's rules refuse it: its anchor node isn't unlocked, or the points went elsewhere. The unlock is lost in this world, its points are free, and the entry goes. Plainly: a warning, never silence | `[W] node 0x... (level N) is a native node in this tree, and it can't be taken back: <reason>. Its unlock is lost here and its points are free again` |
| Over budget | Unchanged: shadow nodes that cost more than the character has are reset, the native tree stays | `the tree costs N of M points with its K shadow node(s)` |
| A native node whose unlocked path ran through a node that became shadow | Only when it has another link, to a game node the character hasn't unlocked: it no longer connects in the game's check, which follows native lists only, and the game resets the whole tree, free. The ordering keeps a mod's chain together, so this takes a mod big enough to split, in that shape. Not seen; not prevented | `the game's join check rejected a character's tree (N nodes): some nodes are not linked to a root` |

### The shadow table (nodes past the caps)
A shadow node is in the game's tree resource: the game's NodeById finds it, the screen lists it, its links are
drawn. Its unlock state never enters `UnlockedSkillNodes`, `PlayerSkillsUi` or the character save, so the character
stays valid on vanilla servers. Kiln keeps it per player and hooks the game's own skill functions:

| Game function (server 1287369 / client 26430625) | Call sites (server / client) | What the hook does |
|---|---|---|
| IsUnlocked `0x94c480` / `0xea5230` (id → index → mask bit) | 50 in 23 functions / 53 in 25: every IsSkillUnlocked native, item-sequence `requiredSkill` checks (`0x53b30`, `0x53c70`: 7 each), buffs, durability, mining, camera, `server_progress_scaling`, ... | a shadow id: the table's level > 0 |
| NodeSet `0x91fe90` / `0xe78d20` (unlock / unlearn in UnlockedSkillNodes) | 3 (unlock handler, unlearn ×2) + kiln.progression | a shadow id: the level moves in the table (only for a real player's list), never into the list |
| Reachable `0x91f990` / `0xe78960` | 1 (the unlock handler) | a shadow id: its links (game or shadow neighbours) against the masks and the table |
| PointsSpent `0x9222f0` / `0xe7b170` | 3 (unlock cost check, login budget check, respec cost) / 4 (+ the screen's points) | adds the table's costs, except inside the game's join check, which judges the native tree alone |
| Reset `0xb9a00` / `0x2ce370` (respec, and the free reset when the join check fails) | 2 (both in player_skilltree) | clears the table when the game's list was reset. kiln.log: `(respec)` for a paid one, `(the game's join check reset the tree)` for the free one |
| LoginCheck `0xe00b0` / `0x2f4110` (already the login guard) | 1 | a shadow id found in a save moves into the table; PointsSpent counts native nodes only while it runs; when it rejects the tree, kiln.log says why |
| skilltree_ui `0x318540` (client) | the program | after the game's update: the shadow nodes' records (level `+0x41`, unlocked `+0x43`) in G38UiPlayData's node map |

**Count: 7 hooks on the client, 6 on the server, plus the tree loader. No byte patches.** The rest needs no hook:
- inline readers by design:
  - SkillUiRebuild and the level check only see UnlockedSkillNodes, which never holds a shadow id;
  - the connectivity BFS (`0x9160a0`) follows only native nodes' link lists. A link that touches a shadow node is
    only in that node's own lists and in the global link list (which the screen draws).
- inline readers that skip shadow nodes:
  - the per-class spent sums `0x922690` (one caller, 0x218780);
  - the camera's SkillModifier test `0x91afd0` (only reached when camera data names a node: don't point camera
    settings at a shadow node).
- the save and replication: untouched (vanilla format).

The game's own rules apply to shadow nodes, through its own handler:
- cost check (with shadow costs counted);
- links, levels (more levels only while below `maxLevel`);
- unlearning one node charges its cost in respec items, and refuses (undoes) without them;
- an unlearn may not cut another node off: a shadow node another shadow node hangs on stays. (A native node a shadow node hangs on can still be unlearned: the game's connectivity check follows native lists only, and a native neighbour's state isn't known inside NodeSet. The shadow node keeps working until the next respec.)
- the respec cost counts shadow nodes;
- when the join check rejects the native tree, the game's free reset clears the shadow nodes too: they hang on it.

Kiln.Skills (managed):
- keeps each character's table in its carried character data (`"gameShadow"`; it goes with the character:
  [character-data.md](character-data.md));
- loads it into the native table when the player is in game, without the entries of nodes this tree doesn't have (not
  applied, their points are free here; they stay in the data and apply again on a world that has the nodes). An entry of a node that
  is native in this tree goes to the game's list, and what the login guard moved out of the save stays ("When the mod list moves a node");
- saves what the game's unlock path changed, and loads the table again when the character's copy comes in from its client;
- checks the shadow nodes' budget itself, once the game has set the tree up: a tree that costs more than the character has
  with them loses the shadow nodes, free, and keeps its native nodes (kiln.log "the tree costs N of M points with its K shadow
  node(s)"). The game's own check can't do this: it would reset the native tree too;
- keeps the player's client up to date over channel `kiln:skills` (their screen and their client-side queries read the levels).
  This is level-triggered: each step, a player whose current peer hasn't got the table's current state gets it, as soon as the
  peer is ready. A player is in game before their client's Kiln handshake is done, so anything sent at load goes nowhere, and
  a reload that changes nothing sends nothing (see "Client findings"). The client keeps a message until its local player is known
  and clears its table when the connection ends: the same entity id on the next server must not show another session's state;
- runs the shadow nodes' effects. The game's `handle_skill_impacts` only walks UnlockedSkillNodes, so Kiln spawns
  each node's program with the components the game gives its own skill impacts:
  - `SkillIdComponent` (the program reads the node's config values through it);
  - `ImpactHierarchy` = the player;
  - `DespawnLink` to the player;
  - for nodes with levels, `Level {storage = {level, 99}}`.

Fail soft: the hooks are all or nothing. Without every server hook, or (on a client) without the screen hook,
shadow nodes are left out of the build. They are hidden and inert, the first 256 indices stay the same on both sides,
and the game's tree works.

Clients: a mod whose nodes end up as shadow nodes, or that adds game-tree nodes at all, declares `"clients": "mod"`
(the client must build the same tree from the same files). Kiln.Network refuses players without it.

### Saved unlocks of nodes that are gone (login guard)
- The game's character save stores unlocked nodes by id and level (the `player_character` block: count, then
  `(u32 id, u8 level)`). The character lives in the player's own `character<N>.ksd`; a dedicated server sends it back
  at saves.
- At each join the game checks the tree (`initialized` is not saved): rebuild, every node linked to a root (native lists), levels
  within `maxLevel`, native costs within the total. It waits for the skill-point knowledge first. Any failure means a free reset
  of the **whole tree**, and kiln.log says which: "the game's join check rejected a character's tree (N nodes): some nodes are
  not linked to a root (an admin force unlock?)", "a node id the tree does not know", "a level above a node's maxLevel" or "it costs
  more than the character's skill points".
- If an id is unknown to the tree (a mod that is gone), the rebuild would fail.
- kiln.skills hooks that check: it removes only the unknown ids. Their points come back (points are derived, never
  stored) and the rest of the tree stays. Records: `strips()`, `GameNodesRefundedEvent`, kiln.log "player entity N
  joined with K skill node(s) the tree does not know ...: removed".
- On a vanilla server the game's own reset happens. Shadow nodes never reach the character file, so that only
  concerns native Kiln nodes.
- A tree the game accepted stays. An admin's `/skill <player> unlock <node> force` can make one it won't accept: kiln.progression
  says so in the result ("forced: this tree is not linked to a root, so the game resets it at this character's next join"). That
  reset is correct: the tree is invalid by the game's rules, and vanilla would do the same. A forced path has to include
  every link (one such tree left out the double jump node `0xffd5f940`, and `0x4475e8dd` hangs on it).

### What vanilla clients see
They draw from their own 243-node tree. The screen ignores mask bits past its own count (its loop is bounded by
the id-list count), but its points sum would read past its node array. So mods that add game-tree nodes declare
`clients: "mod"` and servers refuse vanilla clients (the rule for content only Kiln clients can show).

## B. Kiln talent trees

### Data (the same `skills/*.json`, key `"trees"`)
```json
{ "format": 1, "trees": [ {
  "id": "mymod:ember", "name": { "en": "Ember Arts", "de": "Glutkünste" }, "description": { "en": "..." },
  "points": { "start": 1, "perLevel": 1 },          // or { "share": 0.5 }: half the game's skill-point total
  "menu": { "en": "Ember Arts" }, "key": "K", "respec": true,
  "nodes": [
    { "id": "kindle", "name": { "en": "Kindle" }, "description": { "en": "+1 Intelligence per level." },
      "col": 1, "row": 0, "cost": 1, "maxLevel": 3, "icon": "icons/kindle.png",
      "effects": [ { "attribute": "Intelligence", "amount": 1 } ] },
    { "id": "skin", "col": 2, "row": 1, "cost": 2, "requires": ["kindle"], "effects": [ { "gameNode": "0xa598792e" } ] },
    { "id": "step", "col": 1, "row": 2, "requires": ["hearth", "skin"], "pointsSpent": 3, "level": 10,
      "effects": [ { "buff": "MyMod_Warmth", "refresh": 30 }, { "ability": "mymod:flame-step" } ] } ] } ] }
```
Kiln loads the trees of every loaded mod at its first service tick (`Kiln.SkillsStartup`); a data-only mod needs no
code. Code may also call `Skills.LoadTrees()` (the calling mod's folder) or `Skills.Register(tree)`.

| Node field | Meaning |
|---|---|
| `id`, `name`, `description`, `icon` | the save key (never change), texts per language, a PNG relative to the mod folder |
| `col`, `row` | grid cell (0..31), drawn top-left first |
| `cost`, `maxLevel` | points per level; levels 1..99 |
| `requires`, `requireAll` | nodes of this tree: any one of them (all, with `requireAll`) taken first; drawn as links |
| `pointsSpent`, `level` | tiers: points spent in this tree; the character level |

| Effect | What runs (server, while the node is taken; re-applied at joins and level changes) |
|---|---|
| `{"attribute": "Strength", "amount": n}` | the game's attribute-node program (`937e39f4`, the "+1 Strength" kind) with n × level on the player. Names: Strength, Constitution, Intelligence, Spirit, Dexterity, Endurance (`TalentAttributes`; matched in the lab per attribute component). Lab: "Strength 1 -> 3 (+2 from the talent)", back to 1 at lock |
| `{"gameNode": "0x<id>"}` | a game node's effect for this player: its program with its own config values (`SkillIdComponent`), as the game runs it for its own unlocks; the node is not unlocked in the game's tree |
| `{"program": "<guid>", "configs": {...}}` | any Impact program kept running on the player (root = the player) |
| `{"buff": "<GUID or clone name>", "refresh": s}` | a game buff or a kiln.gamedata buff clone (`buffs.add`), applied for refresh + 5 s and renewed every refresh. It ends within that time after a lock (Kiln can't take a game buff off early) |
| `{"ability": "<id>"}` | a Kiln.Abilities ability only players with this node may cast (`AbilityCastEvent` canceled with "needs the talent ...") |
| C#: `TalentEffect.Callback(apply, remove)` | code, as the tree's mod, on the tick thread |

Perks: a perk is an item's, so a talent gives one through a buff, a program or code. A perk clone's own effect is a
buff (`impactValues.BuffType`), and its program can run through `program`.

### C#
```csharp
Skills.Register(new TalentTree
{
    Id = "mymod:talents", Name = UiText.Of(("en", "Talents"), ("de", "Talente")),
    Points = TalentPoints.Own(start: 1, perLevel: 1),            // .ShareOfGame(0.5), .Custom(holder => n)
    MenuEntry = "Talents", Key = "K",
    Nodes =
    [
        new TalentNode { Id = "a", Name = "Strong", Column = 0, Row = 0, Effects = [TalentEffect.Attribute(TalentAttributes.Strength, 2)] },
        new TalentNode { Id = "b", Name = "Lucky", Column = 1, Row = 1, Requires = ["a"], MaxLevel = 2,
                         Effects = [TalentEffect.Callback(ctx => Give(ctx.Player), ctx => Take(ctx.Player))] },
    ],
    OnChanged = c => Log.Info($"{c.Holder} {c.Kind} {c.Node}"),
});
// server, tick thread
var h = Skills.HolderOf(player);                                  // TalentHolder(SteamId, Entity) { Character }; HolderOf(characterKey) offline
Skills.Unlock("mymod:talents", "a", h);                           // TalentResult(Ok, Reason, Level, Points)
Skills.Lock / Respec / Grant(tree, h, points) / Points(tree, h) / State(tree, h) / LevelOf / Has(player, tree, node)
[Subscribe] void OnUnlocking(TalentUnlockingEvent e) { if (...) { e.Reason = "not yet"; e.Cancel(); } }
[Subscribe] void OnChanged(TalentChangedEvent e) { ... }
```

### Rules
- **State:** carried character data `"talents:<tree id>"` of Kiln.Skills' data owner (`kiln-skills`): `Levels`,
  `Granted`, `LastLevel`, `LastGameTotal`, `Revision`. Every tree's state lives there, so a mod reload or a tree that
  moves to another mod keeps every character's unlocks. Per character (the game's CharacterId), and it **follows the
  character** to other worlds and servers with a Kiln client ([character-data.md](character-data.md)); a vanilla client's stays on that
  server, per character. The player need not be online: `Skills.HolderOf(characterKey)`. On a build without the game's character id
  it falls back to the account's data (Steam id, else account id, else the host key 1 for the host of a local game). With none of these
  the state lasts the session only, and the shadow table warns "has no character, Steam or account id: their shadow nodes are kept for this session only".
- **From before characters:** state filed under a Steam id, account id or host key moves once, to the first character that loads it.
- **A client's copy is client-side and editable**, like the game's character save. When one comes in, Kiln.Skills checks it as the game checks a
  tree at login: levels cut to `MaxLevel`, nodes without their requirements removed, a tree that costs more than the character has reset for free.
  Trees and nodes this server doesn't have stay in the data, not applied. Servers can refuse: `importProgress` in `kiln/config/characters.json`.
- **Unlock:** level < max, requirements, tier (`pointsSpent`), character level, points, then `TalentUnlockingEvent`.
  **Lock:** one level back. It is refused when another taken node needs this one, or a tier would fall short. Respec
  is free unless `AllowRespec` is false.
- **Points:**
  - `Own(start, perLevel)`: start + perLevel × (level − 1) + granted;
  - `ShareOfGame(share)`: floor(share × the game's skill-point total) + granted. kiln.progression gives the total. The
    game's tree keeps all of its own points; this is a budget derived from them. The game's total is capped: 2 per
    level above 1 plus at most 78 skill-point knowledge flags, of which `GiveSkillPoints` can add 60;
  - `Custom(f)`;
  - `Grant` gives points of the tree's own on top.
- **Effects** start at unlock and when a taken player is seen in game (joins, restarts), end at lock, respec, leave
  and unload. Every ~2 s Kiln renews buffs and restarts effect entities that are gone.
- **Screen** (client): `UiStyle.Game`. The nodes sit on the grid: icon buttons, gold = taken, light = available,
  dim = not yet. Dotted links, level "n/max", a side panel with Unlock / Refund, Respec and Close. It opens from the
  tree's pause-menu entry or its key. State comes from the server (channel `kiln:skills` v1, not required). A hosting
  client calls the server part directly: a mod's tree in that mod's scheduler, a data-only tree (it has no owner
  scheduler) in the "skills" service tick.
- **Its key** opens it during play only: a letter key doesn't fire on the game's own screens, because the game's text fields don't
  tell Kiln they have the keyboard (ui.md "Key bindings"; K used to fire inside chat).
- **The pause menu of a game hosted offline stops the worlds.** Nothing runs on the tick thread until the player resumes, so a change
  can't apply there: the character's data, the effects and the attributes all belong to that thread. The screen shows the result
  at once, with "The game is paused: this applies when you resume.", and the queued request applies at the first step after the
  pause. The attribute on the character screen moves then. In a joined game the pause menu stops nothing and it applies at once.
- **Chat** (vanilla players too): `/talents` (trees and points), `/talents <tree>` (nodes), `/talents <tree> unlock|lock
  <node>`, `/talents <tree> respec`. Admins: `/talents <tree> grant <points> [player]`.
- `side: "both"`, `clients: "mod"` (the client draws the tree from the same definition and icons).

## C (native plugins)
`kiln_skills.h`:
- `status`, `define(owner, json)`, `undefine_owner`, `definitions`;
- `tree(flags)`: the server world's tree through the game's lookups;
- `simulate`: the game's skill functions on a scratch list;
- `strips`: login-guard records;
- `resource(which)`: expanded / game / copy path, with the round-trip check;
- the shadow table: `shadow_on`, `shadow_nodes`, `shadow_get`, `shadow_set`, `shadow_changes`, and the diagnostics
  `spent`, `is_unlocked`, `game_request`. `game_request` sends a player entity's unlock input the way its client would; its test-only operations (3 and up) are refused unless `<kiln>/data/skills/lab-ops` exists.

## Test (lab, no player)
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/skills-test/SkillsTest.csproj -c Release -o $KILN_OUT/mods/skills-test --artifacts-path $KILN_OUT/dotnet
tools/lab/lab.sh new skilltree 26290 --save <savegame dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/skilltree --policy any
tools/lab/lab.sh start skilltree      # "skills-test done: N PASS, 0 FAIL"
```
Delete `kiln/data/skills-test/first-boot-done` for a fresh run. The checks count the sample's nodes when it is deployed. In a hosted
game the mod loads but only logs "the automatic suite runs on a dedicated (lab) server".

The client sync's own run plays the join order with a fake client on kiln.net's lab transport:
```sh
echo '{ "labPort": 26451 }' > ~/srv/enshrouded/labs/<lab>/kiln/config/network.json
tools/lab/lab.sh start <lab>          # skills-test logs "shadow client sync: waiting for shadownet.py"
python3 mods/tests/skills-test/shadownet.py --port 26451
```

Which node gets a native slot, offline on the client's own tree: `python3 -m unittest discover -s tools/kfc/tests -p test_skilltree_slots.py`.

Latest regression: see "More client findings".

What the lab shows (kiln.log, server svn 1287369):
- the tree:
  - "first world: skill tree built: 261 nodes (243 game + 18 Kiln, 6 of them shadow nodes past the game's caps), 330
    links";
  - kiln.progression's own check of every node against the game's lookup: "skills on (261 nodes, ...)";
  - "the server world's tree has every Kiln node through the game's own NodeById (indices 243..260)";
  - "round trip: the served resource (120576 bytes) parses back, every game node/link/sub-array equals the game's";
  - offline, `tools/kfc`'s reflection decoder reads the expanded bytes: 261 nodes, 330 links, the game's nodes
    unchanged except the parents that gained a link.
- the game's own functions on Kiln nodes and the login guard:
  - "the game's Reachable/NodeSet/SkillUiRebuild/connectivity check take the Kiln node skilltest_bulk_0 (index
    252)";
  - "an unknown id in the list makes the game's SkillUiRebuild fail (the game would reset the whole tree at login)";
  - "the login guard's strip removes only it".
- the shadow path through the game's own input, on a player-template entity:
  - "the game's unlock handler put the shadow node in Kiln's table (level 1), not into UnlockedSkillNodes (the
    character save)";
  - "IsUnlocked(skilltest_deep_a) is true now, and skilltest_deep_b (linked from it) is reachable";
  - "the game's cost check refuses skilltest_deep_b (3 points, the entity has none)";
  - "the game's PointsSpent adds the shadow table: 0 -> 6";
  - "the game's unlearn of a shadow node asks for its unlearn cost like for its own nodes: no respec item, so it put
    the level back (2 -> 1 -> 2)";
  - "the game's respec (reset action) clears the shadow table too".
- the Kiln tree:
  - "Strength 0 -> 2 (+2 from the talent)", "lock: the effect entity is gone, Strength 2 -> 0";
  - "code tree: callbacks ran";
  - after a restart: "the offline character's Kiln tree state came back from Kiln.Saves (a 1, b 1, granted 2)".
- the sample:
  - "tree skilltreesample:ember registered (5 nodes, points own (1 + 1/level), owner kiln)" (the data-only path);
  - "net: advertising 1 mod(s) players need: skilltreesample".

## Client findings
What a real client showed: the root cause, the fix, what the lab shows.

| Seen | Root cause | Fix | Lab |
|---|---|---|---|
| Fresh process: 5 shadow nodes on the server, Ember Ward Locked on the screen, Space does nothing, `the game's unlock refused it` | The server sent the table when it loaded it. A player is in game before their client's Kiln handshake is done (00:39:43.644 table loaded, .725 peer ready), so the message went nowhere. The reload after the carried import changed nothing, so nothing was sent again. The client's table stayed empty and its screen read Locked; the server refused the unlock because the node was already taken. Same-process rejoins hid this: the old session's table was still there | Level-triggered sync (above); a message waits for the local player; the table ends with the session | `shadownet.py`: "the client gets the table once its peer can take it, with no change on the server" |
| Login logs `(respec)`, tree gone on the server, nodes still lit on the screen | Two things. **The reset was correct:** the forced relearn left out `0xffd5f940`, so `0x4475e8dd` hung on nothing and the game's connectivity check failed at each join. **The screen was stale:** the message about the reset was lost the same way, and the old session's client table stayed | The sync above; kiln.log says why a join check failed; a force unlock warns | Simulate: "the forced tree ... is not connected in the game's own check", "with the double jump node ... a valid tree passes" |
| A valid native tree could be wiped by shadow costs | PointsSpent counted the table inside the join check, so a restored character save with newer carried data went over budget and lost everything | The join check judges native nodes alone; Kiln checks the shadow budget itself and drops only those | Not reachable in a lab (a player-template entity has no total: the check waits for knowledge) |
| `/skillpoints` 8 unspent, screen 4; `/skill lock` "not unlocked"; `/skill respec` left them | kiln.progression's sums run on scratch masks the hooks can't attribute, and it looked for nodes in UnlockedSkillNodes | `shadow_scope`, `shadow_level`, `shadow_reset`, `shadow_linked`: its points, node count, lists, unlock, lock and respec see them (progression.md) | "its points and node count include them: spent 0 -> 3, nodes 0 -> 2", "/skill lock sees shadow nodes", "/skill respec clears the shadow table", "the unlock works again" |
| Ember Ward says "+2 Spirit", Intelligence rises | Its `like` node is an Intelligence node | The texts say Intelligence (English, German); the effect stays | "Intelligence 0 -> 2, Spirit 0 -> 0" |
| K in chat opened the tree and took the rest of the line | Key bindings fire on any screen; the game's text fields don't tell Kiln. **Real typing too**, not only synthetic input: capture on/off at Play/43, once per k | Text keys fire during play only (ui.md) | Client only |
| Unlock in the pause menu of a hosted game applies after resuming | That pause stops the worlds | The screen shows the result, marked (Rules above). It still applies at resume, and has to | Client only |
| A window opened with K during play: no mouse | **Only an absolute pointer** (a test driver): the game's pointer lock leaves relative motion only. A real mouse moved and clicked in a window opened by a key during play (`input while capturing`, Play/1, 23:52:40-47: 1014 moves, 2251 raw, 10 clicks) | One visible system cursor at capture start, which ends the lock (ui.md) | Client only; the test driver should use Esc or relative motion if it stays |

### More client findings
| Seen | Root cause | Fix | Lab |
|---|---|---|---|
| Adding skills-test: Ember Ward and Emberheart 2/3 Locked on join, 3 points back, no line in any log | Two things. **Slots:** by node id across all mods, so a big mod took the sample's slots; and a chain could split, leaving 2 slots unused after demoting nodes. **The unlock:** the login guard moved them into the table (client log: `2 shadow node(s) from the server`, then 76 ms later `0`), but the character's carried copy came in right after and the table loaded again from it, and it knew nothing of them. They had already left the save | Slots: "Order and the caps". Unlock: `GameShadow.FromSave`: reads the changes before loading, merges into every load, writes to the carried data | `test_skilltree_slots.py` 10 OK; skills-test: "the sample's nodes stay native next to this bigger mod", "they moved to the table with their levels (1, 2)", "the client's older copy coming in doesn't undo them", "a level the player gives back stays gone after a load", "a respec clears what came from the save" |
| The reverse direction (not seen in a client; found reading the code) | An entry of a node that is native in this tree counted as "a mod this world doesn't have": locked, points free, the wrong message, and back only if the node moved back | Goes to the game's list by the game's rules; refused, it says so | skills-test: "carried as a shadow node, it goes into the game's own list" (with force: the lab's entity has no points to read), "refused by the game's rules: no unlock, the entry goes, and kiln.log says so" |
| `status` said 0 native slots free with any shadow node | It counted shadow nodes | Native nodes only | "0 of 256 native node slots" with 7 shadow nodes, every slot used |
| Ember Arts in German: frame in English | Literals in `TreeScreen.cs` | `TalentUi.Frame` (en, de: what Kiln's other strings have), the game's own German attribute names. The pause note wraps | Compiled; client only. What the server answers stays in English: a server doesn't know its players' languages |

Regression: skills-test alone 69 PASS, 0 FAIL with shadownet.py "all passed"; with the sample 67 PASS, 0 FAIL; with everything else 67, 0. `test_skilltree_slots.py` 10 OK. world-switch-test with only the sample in the tree: "a tree with no shadow nodes: a character's carried Ember Ward and Emberheart 2/3 are in its list, with their levels". Lab entities have no points to read, so the move to native ran with force: its rule checks are the game's, through kiln.progression.

## What a client shows
A hosted LOCAL world, offline.

kiln.log:
- "skills: ready: tree expansion on, login guard on, game functions on, node texts on, shadow table on (+ screen); 21
  game-tree definition(s) at boot";
- "first world: skill tree built: 263 nodes (243 game + 20 Kiln, 9 of them shadow nodes past the game's caps), 332
  links (312 + 20), 2 of 256 native node slots and 9 of 100 level slots free, 121092 bytes";
- "the new world's skill tree loaded 263 nodes and 332 links (expected 263 / 332, Kiln's resource)", twice: the hosted
  server world and the client world;
- kiln.progression: "skills on (263 nodes ...)".

The game's SKILLS screen (H): skills-test's 14 bulk nodes above the central start, in two rows linked from one point, and
deep_a / deep_b ("0/2") above them. Those nodes have no texts of their own, so their tooltips show the `like` node's texts.
Space unlocks them. Shadow unlocks are logged: "skills: shadow node 0x7cb432ed of entity 1: level 0 -> 1 (the game's skill
tree)". Strength went 2 -> 8 over 6 nodes, 3 of them shadow nodes, whose effects Kiln runs. The pause menu has "Ember Arts",
K toggles the screen in the world, and the screen draws 5 talents with links.

The game's UI has no free respec: unlearning and respec cost respec items. A test character keeps the nodes it took until
its save is restored.

Saves: the game rewrites the Steam set's `global.ksd` and `user_settings.json` even when a LOCAL game is played. A restore
must cover the whole LOCAL folder, `backup/` included: `backup/character0.ksd.backup-*` keeps earlier revisions.

## Manual test
Use a LOCAL copy of a character, not your Steam save: stop at once if kiln.log shows a world slot 0x01xx. Nodes at tree
index 243+ must not reach a real save.

### Setup (game closed)
```sh
export KILN_OUT=<build dir>     # export, not a command prefix: the --mod arguments below use it
dotnet build mods/samples/SkillTreeSample/SkillTreeSample.csproj -c Release -o $KILN_OUT/mods/skilltreesample --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/skills-test/SkillsTest.csproj -c Release -o $KILN_OUT/mods/skills-test --artifacts-path $KILN_OUT/dotnet
tools/client/client.sh backup-saves
tools/client/client.sh deploy --real --policy any --mod $KILN_OUT/mods/skilltreesample --mod $KILN_OUT/mods/skills-test
```
Client log: `tools/client/client.sh logs 3000 | grep -E 'skills:|shadow table|saves: world loaded'`.
Lab log: `tr -d '\r' < ~/srv/enshrouded/labs/skilltree/kiln/logs/kiln.log | grep -E 'skills:|shadow table|joined with Kiln'`.

### A. Hosted game
Launch from Steam ("Processing Vulkan shaders": click Skip). PLAY > START A SESSION > **LOCAL** tab > a character copy and
its world > Start > Offline.
1. kiln.log: "saves: world loaded: slot 0x0002 (world1.ksd)"; "skill tree built: 263 nodes (243 game + 20 Kiln, 9 of them
   shadow nodes ...)"; "the new world's skill tree loaded 263 nodes and 332 links" twice.
2. SKILLS (H), zoomed in. Above the central start: 14 small test nodes in two rows, linked from one point, and above them
   a small node and a "0/2" node. Hover shows a game tooltip, Space unlocks at cost 0, and the attribute on the right
   rises by 1 per node. kiln.log for the shadow ones: "shadow node 0x... of entity 1: level 0 -> 1 (the game's skill
   tree)".
3. Sample nodes, which need a free skill point (use a character that has one; `/level`, `/xp` and `/skillpoints` on a real
   client can fire real Steam achievements). At the right edge, after the Mage Intelligence node `0xcfa5985b`: "Ember Ward"
   ("+2 Intelligence."; German game language "Glutwehr" / "+2 Intelligenz.") and, linked from it, "Emberheart" (0/3). Ember
   Ward costs 1 point, Intelligence rises by 2, Spirit stays, and kiln.log logs it like step 2 (a shadow node, index 259).
   Emberheart levels 1-3 cost 1 point each.
4. Esc > "Ember Arts". The header reads "Points: N available (0 of N spent)", N = the character level, with no red "the
   server does not have Kiln talent trees".
   - Click Kindle: the side panel shows level 0 / 3 and "Unlock (1)". Unlock: the border turns gold, "1/3" shows,
     Intelligence rises by 1. "Next level (1)" adds another +1.
   - kiln.log: "skills: skilltreesample:ember: entity 1 (steam K) Unlock kindle 0 -> 1 (N point(s) left)". K must not be
     0: the Steam or account id, or 1.
   - Hearth and Ember Skin turn available (light border) after Kindle. "Refund" takes one level back. "Respec (free)"
     clears all and gives the points back.
   - Close. K in the world opens and closes the same screen. The dotted links run between the node centres.
   - In this pause menu the world is stopped (Offline): Unlock shows at once with "The game is paused: this applies when you
     resume.", and Intelligence moves after Esc. K during play: the world runs, and it applies at once.
   - Open chat, type a line with a k: it stays in chat, no window.
5. Persistence. Unlock deep_a (the small shadow node above the test rows) and Kindle 1, then Esc > Return to Main Menu and
   start the same world again.
   - SKILLS: deep_a is still unlocked. kiln.log: "shadow table: <player> has N shadow node(s)", and no "has no Steam /
     account id" warning. Ember Arts: Kindle 1/3, Intelligence still +1.
   - The native test nodes from step 2 are still unlocked (the game's character save; the login guard knows their ids).
6. Quit to Desktop normally.

### B. Joined dedicated server
A lab with skills-test, the sample and Kiln.Admin, text chat on: `tools/lab/lab.sh start skilltree` (UDP 26290). Its log ends
with "skills-test done: N PASS, 0 FAIL". Admin password:
`jq -r '.userGroups[] | select(.name=="Admin").password' ~/srv/enshrouded/labs/skilltree/enshrouded_server.json`.
1. Join "Kiln lab skilltree" from the server list with that password, as the LOCAL copy. Lab kiln.log: "<name> (0x...)
   joined with Kiln ...". SKILLS looks the same as in A2 (263 nodes on both sides).
2. A character with a free skill point (not raised with admin commands, see A3).
3. A2 and A3 work the same. The lab's kiln.log has the shadow unlock ("... (the game's skill tree)"), and the client's
   screen shows it.
4. Ember Arts over the channel `kiln:skills` v1: as in A4, with no red message. The lab's kiln.log has the Unlock line,
   with the Steam id as K.
5. Leave and rejoin, also after quitting to desktop (a fresh process): shadow nodes and Ember Arts levels come back (carried character
   data), lit on the screen, and the next level of Emberheart still takes Space. Lab kiln.log: "shadow table: sent N shadow node(s) to the client of
   entity ... (peer 0x...)" after "joined with Kiln"; client kiln.log: "shadow table: local player ... has N shadow node(s) from the
   server". Between worlds: [character-data.md](character-data.md), "Manual test".
6. Optional:
   - A vanilla client (no Kiln) is refused, because the sample needs clients "mod".
   - Remove skills-test from the lab, restart it, and rejoin with a character that has a test node. Lab kiln.log: "player
     entity N joined with M skill node(s) the tree does not know (a mod that is gone?) ... removed before the game's
     check". Only those nodes are gone; the rest of the tree stays.

### Afterwards (game closed)
Stop the lab, restore the save backup (the whole LOCAL folder, `backup/` included), and `tools/client/client.sh undeploy --real`.
