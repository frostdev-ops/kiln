# Kiln quest chains (quests/*.json), format 1

<!-- Generated from schema/kiln-quests.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

A mod's quests, dialogue and quest NPCs, read by Kiln.Quests (managed/services/Kiln.Quests/Json.cs, System.Text.Json: comments and trailing commas allowed) and by the boot build of the game's journal, NPC menu and map markers (native/services/quests/questpack.cpp). A quest and a dialogue are in the game's own UI unless they say otherwise. Beyond the schema, kiln lint checks references across the mod's files: step ids, nodes, goto targets, givers, quests named by conditions and actions, and that the mod says "clients": "mod". Docs: docs/kiln/modding/quests.md.

- Files: `**/quests/*.json`
- JSON: comments allowed, trailing commas allowed
- Schema: [`schema/kiln-quests.schema.json`](../../../../schema/kiln-quests.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `$schema` | string |  |  |
| `dialogues` | object |  |  |
| `format` | `1` | yes |  |
| `knowledge` | object |  | New knowledge objects of this mod: added to the game's GameKnowledgeResource at boot on the server and on clients with the mod (docs/kiln/services/npcs.md). Name them "<mod>:<name>" elsewhere, or by name inside this mod's native lines. |
| `npcs` | object |  |  |
| `quests` | object |  |  |

## action

One of xp, item, take, knowledge, revoke, start, complete, fail, advance, reset, spawn, despawn, teleport, chat, toast, waypoint, talk, signal, call.

| Key | Type | Required | Notes |
|---|---|---|---|
| `advance` | string |  | quest or quest.step: finishes the current step (a deliver objective takes its items now). min length 1 |
| `amount` | [count](#count) |  |  |
| `at` | [position](#position) |  |  |
| `bindNpc` | string |  | spawn: the entity becomes this NPC of npcs. min length 1 |
| `call` | string |  | A name registered with Quests.RegisterAction. min length 1 |
| `chat` | [text](#text) |  |  |
| `complete` | string |  | min length 1 |
| `count` | [count](#count) |  |  |
| `despawn` | string |  | Destroys the entities Kiln spawned with this tag. min length 1 |
| `exact` | boolean |  | spawn: exactly at the place given. Otherwise on real ground, outside every base. |
| `fail` | string |  | min length 1 |
| `flag` | integer |  | revoke: one flag of a Flags object (-1: all). min -1 max 31 |
| `force` | boolean |  |  |
| `item` | [item](#item) |  |  |
| `kind` | `"info"` \| `"success"` \| `"warning"` \| `"error"` |  |  |
| `knowledge` | string |  | Grants it: npc:....unlock, quest:..., 0x... . min length 1 |
| `node` | string |  | min length 1 |
| `offset` | [position](#position) |  | spawn, teleport, waypoint: from the player. |
| `reset` | string |  | min length 1 |
| `revoke` | string |  | Clears that id only: what the game already did because of it stays. min length 1 |
| `signal` | string |  | min length 1 |
| `spawn` | [template](#template) |  |  |
| `start` | string |  | min length 1 |
| `tag` | string |  | min length 1 |
| `take` | [item](#item) |  |  |
| `talk` | string |  | Opens this dialogue as a chat conversation. The game's own menu opens only when a player talks to the NPC. min length 1 |
| `target` | `"world"` \| `"player"` |  | knowledge, revoke: default the player's object if the id has one, else the world's. |
| `teleport` | [position](#position) |  |  |
| `toast` | [text](#text) |  | A chat line: Kiln has no toasts of its own any more. The game's notifications follow the journal. |
| `transient` | boolean |  | spawn: never written to the save. |
| `value` | integer |  | knowledge: a counter's amount or a flag's number. min 0 |
| `waypoint` | boolean or [position](#position) |  | [x, y, z] sets the player's waypoint, false clears it, true with offset is relative to the player. |
| `xp` | number |  | Experience through kiln.progression (as a quest reward). min 0 |
| `yaw` | number |  |  |

## condition

One of knowledge, level, quest, step, item, predicate, all, any, not. Checked on the server.

| Key | Type | Required | Notes |
|---|---|---|---|
| `all` | array of [condition](#condition) |  |  |
| `any` | array of [condition](#condition) |  |  |
| `count` | [count](#count) |  |  |
| `is` | `"locked"` \| `"available"` \| `"active"` \| `"done"` \| `"failed"` \| `"started"` \| `"pending"` \| `true` \| `false` |  | Quest: locked, available, active, done, failed, started. Step: pending, active, done. Default done. Knowledge: true (the default) or false. |
| `item` | [item](#item) |  |  |
| `knowledge` | string |  | min length 1 |
| `level` | integer |  | min 1 |
| `not` | [condition](#condition) |  |  |
| `predicate` | string |  | A name registered with Quests.RegisterPredicate. min length 1 |
| `quest` | string |  | min length 1 |
| `step` | string |  | a step is quest.step |

## count

min 1

Type: integer

## dialogue

| Key | Type | Required | Notes |
|---|---|---|---|
| `farewell` | array of [dialogueLineText](#dialoguelinetext) |  | Replaces the NPC's farewell lines. |
| `greet` | boolean |  | The start node's text is a greeting. Default: true on a clone, false on a game NPC (its own greetings stay). |
| `greetings` | array of object |  | More greetings; shown before the plain one while their condition holds. |
| `hint` | `"None"` \| `"Hint"` \| `"Attention"` \| `"MainQuest"` |  | The marker over the NPC's head on the greeting. |
| `native` | boolean |  | true (the default): lines in the game's own NPC menu, with the game's voice playback (needs "clients": "mod"): the start node's text is the greeting, each option a topic whose reply is the target node's text. false: a chat conversation through /talk, for a mod vanilla clients join. docs/kiln/services/npcs.md. |
| `nodes` | object | yes |  |
| `start` | string |  | The first node (default "start"). min length 1 |
| `transition` | array of [dialogueLineText](#dialoguelinetext) |  | Replaces the line between topics. |

## dialogueLineText

A text, or {"text": ..., "voice": ...}.

Type: [text](#text) or object

## distance

Metres from what it is near: one number, or [min, max]. It widens by itself until a good spot is found.

Type: number or array

## guid

a GUID: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx

Type: string

## id

an id: letters, digits, '_' and '-'

Type: string

## item

An item's name. min length 1 Names a **items** entry (checked against the catalog).

Type: string

## knowledgeObject

| Key | Type | Required | Notes |
|---|---|---|---|
| `expectedFlags` | integer |  | Flags: the mask of flags that exist; a grant of another flag is dropped by the game. min 0 |
| `scope` | `"world"` \| `"player"` \| `"both"` |  | world: shared progress. player (default): per character. both: a world object with a player twin (a player grant also sets the world's). |
| `type` | `"bool"` \| `"counter"` \| `"flags"` |  | bool (default), counter, flags. |

## near

What to search around: "player" (who gets there first), an id of npcs, or a place.

Type: string or [position](#position)

## node

| Key | Type | Required | Notes |
|---|---|---|---|
| `do` | array of [action](#action) |  |  |
| `goto` | string |  | A chat conversation's node without options: where it goes on; "end" or none ends it. min length 1 |
| `hint` | `"None"` \| `"Hint"` \| `"Attention"` \| `"MainQuest"` |  | The marker over the NPC's head on this node's greeting. |
| `options` | array of [option](#option) |  |  |
| `speaker` | [text](#text) |  |  |
| `text` | [text](#text) |  |  |
| `voice` | string |  | A voice-over line, played by the game's dialogue: a name in the mod's cooked audio or a GUID of the game's. min length 1 |

## npc

An NPC quests and dialogue refer to: a game NPC, or a new one (clone). In the world by itself, or put there by place.

| Key | Type | Required | Notes |
|---|---|---|---|
| `at` | [position](#position) |  |  |
| `clone` | boolean or object |  | A new NPC: a copy of template under its own GUID (NpcId = FNV-1a of the GUID), one per world. true: GUID and name derived from the mod and this id. Needs "clients": "mod". |
| `description` | [text](#text) |  | A roster NPC's line on the game's Survivors page: its summon tool's description. |
| `dialogue` | string |  | The conversation it opens (id of dialogues, or mod:id). |
| `name` | [text](#text) |  |  |
| `names` | [text](#text) |  |  |
| `place` | object |  | Where Kiln puts it, on real ground and outside every base. It checks that the game keeps it there, and says so in kiln.log when not. |
| `roster` | object |  | An entry in the game's NPC roster (NpcCollection): the NPC directory, the unlock rule, recruiting and beds. Needs clone. At most 64 roster NPCs in all (the game's 35 included). |
| `talkRange` | number |  | How close a player must be to talk (metres, default 8). |
| `template` | [template](#template) |  |  |
| `vendor` | object |  | Its stock: kiln.content recipes ("add" entries), with station = its workshop's 0x id. A clone shares its base's workshop, so the stock shows at the base's NPC too. |

## objective

One of kill, collect, deliver, reach, talk, craft, build, knowledge, custom.

| Key | Type | Required | Notes |
|---|---|---|---|
| `build` | object |  |  |
| `collect` | object |  |  |
| `count` | [count](#count) |  |  |
| `craft` | object |  | craft needs item or recipe |
| `custom` | string or object |  | A signal's name (Quests.Signal, the signal action). |
| `deliver` | object |  |  |
| `kill` | object |  | kill needs template, family or tag |
| `knowledge` | string or object |  | A knowledge name that must hold: npc:..., quest:..., query:..., line:..., 0x... . |
| `reach` | object |  | reach needs at, offset, marker or site |
| `talk` | object |  |  |
| `text` | [text](#text) |  |  |

## option

| Key | Type | Required | Notes |
|---|---|---|---|
| `close` | boolean |  | The NPC plays its outro after the reply. |
| `do` | array of [action](#action) |  |  |
| `goto` | string |  | The next node: its text is the reply, and its options join the NPC's topics from then on. "end" or none: nothing more. min length 1 |
| `hint` | `"None"` \| `"Hint"` \| `"Attention"` \| `"MainQuest"` |  | The topic's marker. |
| `if` | [condition](#condition) |  |  |
| `once` | boolean |  | The topic goes away once heard. |
| `reply` | [text](#text) |  | What the NPC answers; default the target node's text, else the option's own text. |
| `set` | array of string |  | Knowledge the game itself sets when the line runs (at most 8 operations with the node gate). |
| `text` | [text](#text) | yes |  |
| `voice` | string |  | The reply's voice-over; default the target node's. min length 1 |

## position

Metres: [x, y, z] or {"x": x, "y": y, "z": z}. x east, y up, z north.

Type: array or object

## quest

| Key | Type | Required | Notes |
|---|---|---|---|
| `abandonable` | boolean |  | Players may give it up (default true; never a world quest). |
| `auto` | boolean |  | Starts by itself as soon as it is available. |
| `description` | [text](#text) |  |  |
| `giver` | string |  | The NPC who offers it. A quest with a giver starts through that NPC, an action or code. |
| `hidden` | boolean |  | Not listed until it is active. |
| `journal` | `"native"` \| `"chat"` |  | native (the default): the game's own journal, notifications and map markers; the mod says "clients": "mod", because clients build them from the mod's files. chat: chat lines and /quests, for a mod vanilla clients join, whom the game's UI cannot reach. |
| `main` | boolean |  |  |
| `name` | [text](#text) |  |  |
| `onFail` | array of [action](#action) |  |  |
| `onStart` | array of [action](#action) |  |  |
| `partyRadius` | number |  |  |
| `priority` | integer |  | Its order in the journal (the game uses 40..100; default 50). min 0 |
| `repeatable` | boolean |  |  |
| `requires` | array of [condition](#condition) |  |  |
| `retryable` | boolean |  | Can start again after it failed or was given up (default true). |
| `rewards` | array of [action](#action) |  |  |
| `scope` | `"player"` \| `"party"` \| `"world"` |  | player (default), party (players within partyRadius share credit, each gets rewards), world (one shared state). |
| `source` | `"None"` \| `"Flame"` \| `"Blacksmith"` \| `"Alchemist"` \| `"Huntress"` \| `"Farmer"` \| `"Carpenter"` \| `"CryptKeeper"` \| `"Bard"` \| `"ChineseNewYearTrader"` \| `"Barber"` \| `"Fisher"` \| `"AncientResearcher"` \| `"Grassland"` \| `"Deepforest"` \| `"Steppes"` \| `"Desert"` \| `"ColdHeights"` \| `"Wetlands"` \| `"AncientLands"` |  | The journal's portrait (keen::QuestSourceType). Default None. |
| `steps` | array of [step](#step) | yes | min items 1 |

## site

A place found at run time: on real ground (the voxel surface), outside every base, not too steep; one per world.

| Key | Type | Required | Notes |
|---|---|---|---|
| `at` | [position](#position) |  |  |
| `distance` | [distance](#distance) |  |  |
| `exact` | boolean |  | Exactly at: no ground, no base check. |
| `inBase` | boolean |  | A base is fine (never for enemies: the game pacifies them there and may remove them). |
| `margin` | number |  | Metres to keep from a base's build zone (default 20). min 0 |
| `near` | [near](#near) |  |  |

## step

| Key | Type | Required | Notes |
|---|---|---|---|
| `id` | [id](#id) | yes |  |
| `marker` | boolean or object |  | The step's map marker. A step with a place (site, spawn, a reach or kill place, an NPC to talk to or deliver to) has one by itself; this names it or moves it. false: none. |
| `objective` | [objective](#objective) |  |  |
| `objectives` | array of [objective](#objective) |  |  |
| `onDone` | array of [action](#action) |  |  |
| `onEnter` | array of [action](#action) |  |  |
| `recommendedLevel` | integer |  | min 0 max 255 |
| `site` | [site](#site) |  |  |
| `spawn` | [stepSpawn](#stepspawn) or array of [stepSpawn](#stepspawn) |  | Creatures the step brings into the world: one or a list. |
| `text` | [text](#text) |  |  |
| `title` | [text](#text) |  | The step's short name in the journal; default its text. |
| `voice` | string |  | A voice-over line of the step in the journal. min length 1 |

## stepSpawn

Creatures the step brings into the world: on real ground outside every base, never saved, checked every second, spawned again when the game removes them, and after a restart or rejoin as many as are still owed.

| Key | Type | Required | Notes |
|---|---|---|---|
| `at` | [position](#position) |  |  |
| `count` | [count](#count) |  |  |
| `distance` | [distance](#distance) |  |  |
| `level` | integer |  | The level to spawn at (0: the template's). min 0 |
| `margin` | number |  | min 0 |
| `near` | [near](#near) |  |  |
| `retries` | integer |  | How many times they come back when the game removes them (default 5). Then kiln.log has an error. min 0 |
| `spread` | number |  | Metres between them (default 4). min 0 |
| `tag` | string |  | The Kiln.Spawn tag: a kill objective can name it. min length 1 |
| `template` | [template](#template) | yes |  |

## template

A template's name. min length 1 Names a **templates** entry (checked against the catalog).

Type: string

## text

A string, or a text per language ({"en": "...", "de": "..."}; the game's language keys).

Type: string or object

