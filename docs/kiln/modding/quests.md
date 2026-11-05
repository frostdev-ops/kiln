# Quests, dialogue and quest NPCs

A mod can add quest chains: objectives, rewards, a giver, dialogue with choices. You write them once, and the game shows them with its own UI: the journal and the
tracked-quest line, its notifications, step counters, map markers, the NPC menu with its voice playback. The server runs them, per character on each world. There is no flag to ask for
any of that, and Kiln draws no window of its own.

This page is the guide. The API and how it works are in [services/quests.md](../services/quests.md), what the game's data gets in [services/npcs.md](../services/npcs.md); every key is in
the [generated schema reference](schemas/kiln-quests.md). `mods/samples/quests-sample` is a complete chain.

## A first quest
`mymod/quests/errand.json` (any file name; comments and trailing commas are fine):
```jsonc
{ "format": 1,
  "npcs": {
    "ilse": { "template": "NPC_Workshop_Blacksmith01", "dialogue": "ilse", "names": { "en": "Caravan Master" } }
  },
  "quests": {
    "lost_crates": {
      "name": { "en": "The Lost Crates", "de": "Die verlorenen Kisten" },
      "giver": "ilse",
      "requires": [ { "level": 8 } ],
      "steps": [
        { "id": "bandits",
          "title": { "en": "Drive off the scavengers" },              // the short line: the journal's list, the tracked quest
          "text": { "en": "They took the crates on the old road." },  // the journal's paragraph
          "objective": { "kill": { "tag": "crates", "count": 6 } },   // the journal counts 0/6
          "spawn": { "template": "Enemy_Scavenger_Melee01", "count": 6, "tag": "crates", "near": "ilse", "distance": [60, 120] },
          "marker": { "name": { "en": "The Old Road" } } },           // the map marker is there by itself; this names it
        { "id": "return", "text": { "en": "Bring three logs back" },  // a marker on Ilse by itself
          "objective": { "deliver": { "item": "Material_T1_Wood_Logs", "count": 3, "to": "ilse" } } }
      ],
      "rewards": [ { "xp": 1500 }, { "item": "Weapon_T1_1H_SwordEpic_01" } ]
    }
  },
  "dialogues": {
    "ilse": {
      "start": "hello",
      "nodes": {
        "hello": { "options": [
          { "text": { "en": "I'll find them." }, "if": { "quest": "lost_crates", "is": "available" },
            "do": [ { "start": "lost_crates" } ], "reply": { "en": "Scavengers took our crates on the old road." }, "hint": "Attention" },
          { "text": { "en": "Here are your logs." }, "if": { "step": "lost_crates.return", "is": "active" },
            "do": [ { "advance": "lost_crates.return" } ], "reply": { "en": "The road is safe again. Take this." }, "hint": "MainQuest" } ] } } } } }
```
That file and a manifest with `"clients": "mod"`, `"side": "both"` ([manifest.md](manifest.md)) are the whole mod: Kiln reads `quests/*.json` of every loaded mod by itself, on the server and on every client. `kiln lint` checks it: the schema,
item and template names (with a catalog), and every step, node, NPC and quest the file names.

An NPC with no `dialogue` at all works too: it offers the quests it gives and takes the deliveries it is owed, each as a topic of its own in the game's menu.

## What players need
A client builds the game's journal, its NPC lines and new NPCs from your mod's files at boot, and no server sends them. So:

| Your mod has | Manifest | Who can join |
|---|---|---|
| Quests and dialogue: the default | `"clients": "mod"`, `"side": "both"` | Players with the mod. Servers refuse the others (`modSync`) |
| Every quest `"journal": "chat"`, every dialogue `"native": false` | `"clients": "none"` | Anyone. A vanilla client cannot show the game's UI for a new quest, so this is chat lines, `/quests` and `/talk <n>` |

`kiln lint` reports the wrong one as [KL210](lint.md#kl210-clients-quests). Changes to these files need a restart on both sides: the game reads its resources once. A native mod's data that fails to build leaves that mod's quests running with chat lines, and kiln.log says why.

## Scope
- **`"scope": "player"`** (default): each character on its own. Two characters of one player have separate quests.
- **`"party"`**: each character has its own state; players within `partyRadius` metres (default 60) share kills, crafts,
  builds and signals, and each gets the rewards. A start also starts the quest for players nearby who can start it.
- **`"world"`**: one state for the world. Anyone's kill, craft or signal counts; item and XP rewards go to every player
  in game when it completes. It cannot be given up.

Quest state always belongs to the world: a character that visits another world starts that world's quests from nothing and
finds its progress here when it comes back. Rewards stay with it (items, XP), and so does knowledge a quest grants to the player
(the game keeps player knowledge in the character). Talents travel with the character: [services/character-data.md](../services/character-data.md).

## Objectives
A step has `"objective"` or a list `"objectives"`; it is done when all are.

| Objective | Keys | Counts |
|---|---|---|
| `kill` | `template` (exact), `family` (part of the name), `tag` (a Kiln.Spawn tag), `count`, `near` + `radius`, `countDestroyed` | a death credited to the player (a world quest: any death); `countDestroyed`: Kiln.Spawn destroying a match too |
| `collect` | `item`, `count` | what the inventory holds now: it goes down when the items do. Takes nothing |
| `deliver` | `item`, `count`, `to` | finished by an `advance` action or the NPC's automatic hand-over; takes the items then, refused without them |
| `reach` | `site` or `at` or `offset` or `marker`, `radius` | being there, checked twice a second. `site`: the step's place. `offset`: from where the player stood when the step started. `marker`: a template's name, the nearest entity of it |
| `talk` | `npc`, `line`, `node` | talking to that NPC; `line`: one dialogue line (`line:…`, `0x…`, `<dialogue>.<node>.<option>`); `node`: reaching a node of a chat conversation |
| `craft` | `item` or `recipe`, `count` | a successful craft by the player |
| `build` | `prop`, `count` | placing it |
| `knowledge` | a knowledge name | the game's knowledge holds it (`npc:…`, `quest:…`, `query:…`, `0x…`; [services/knowledge.md](../services/knowledge.md#names)) |
| `custom` | a signal name, or `signal` + `count` | `Quests.Signal`, the `signal` action, or a C# `Progress` function |

The journal shows a step's `kill`, `collect`, `craft`, `build` and `custom` objectives as one counter, their sum. A step the player already fulfils when it starts, such as `collect` with the items in hand, stays open about 3 s, so it gets seen.

## Places
A step with a place shows it: a map marker, visible while the step is active, that the journal's step points to, and the game's waypoint pin on the map and compass. Any of these is a place: `site`, `spawn`, a `reach` place,
a `kill`'s `near`, a `marker`'s `at`, an NPC of your mod to `talk` to or `deliver` to. `"marker": { "name": … }` names it, `"marker": { "waypoint": false }` leaves the player's own pin alone, `"marker": false` shows nothing.

Don't guess where the player is, or how high the ground is. Kiln finds a place:
```jsonc
"site": { "near": "ilse", "distance": [30, 45] },        // "near": "player" (who gets there first), an NPC of npcs, or [x, y, z]
"objective": { "reach": { "site": true, "radius": 8 } }
```
A place is on real ground (the voxel surface), under the open sky, not too steep, on no one's building, and at least `margin` metres (default 20) from every base zone. If `distance` has nothing good, the search widens by itself. It is kept per world, so everyone doing the step goes to one place. A fixed `at` gets the same treatment: it moves out of a base and onto the ground, with a line in the log; `"exact": true` takes it as it is.

### Creatures
```jsonc
"spawn": { "template": "Enemy_Vukah_Melee", "count": 2, "tag": "hunt", "near": "ilse", "distance": [50, 90] }
```
The game pacifies enemies in a base and may remove them, so a step's `spawn` never goes in one. Kiln checks every second that they exist. One that is gone without dying is logged (`the game removed …`) and spawned again, further out, with the marker following; after `retries` (default 5) kiln.log has an error. They are never saved: after a restart or a rejoin, as many as the `kill` objective still needs come back at the place. When the step ends, what is left goes away.

A `spawn` **action** is a one-off for what no objective needs. It also settles on the ground outside bases unless `"exact": true`, and only logs when the game removes it.

### Givers
```jsonc
"place": { "near": "player", "distance": [25, 45], "once": true, "unlock": true }     // or "at": [x, y, z]; "inBase": true for an NPC that belongs in one
```
The game keeps **one entity per named NPC**, and it destroys a named NPC this world has not unlocked. A spawned Hugo Vanderhall was gone within 3 s in the lab; a second Balthazar goes the same way. `place` handles this: it grants a game NPC's unlock for that world first (it stays unlocked, also after the mod is removed), unless `"unlock": false`; a clone in the roster waits for its own unlock unless `"unlock": true`. Kiln checks 3 s later that the NPC is still there, tries again up to 5 times, and says so. New NPCs of your own are clones: `npcs.<id>.clone` and `roster`, [services/npcs.md](../services/npcs.md).

## Conditions
`requires` (a quest) and `if` (an option) are checked on the server, also when an option is chosen: a client cannot pick
what the server would not offer.

| Condition | Meaning |
|---|---|
| `{ "knowledge": "npc:…unlock" }` | the game's knowledge |
| `{ "level": 8 }` | character level at least 8 |
| `{ "quest": "id", "is": "done" }` | `locked`, `available` (can start now), `active`, `done`, `failed`, `started` |
| `{ "step": "quest.step", "is": "active" }` | `pending`, `active`, `done` |
| `{ "item": "name", "count": 3 }` | in the inventory |
| `{ "predicate": "name" }` | a C# function (`Quests.RegisterPredicate`) |
| `{ "all": […] }`, `{ "any": […] }`, `{ "not": {…} }` | combinations |

## Actions
`rewards`, `onStart`, `onFail`, a step's `onEnter` and `onDone`, a node's and an option's `do`.

| Action | Does |
|---|---|
| `{ "xp": 1500 }` | experience, as a quest reward (kiln.progression) |
| `{ "item": "name", "count": 2 }`, `{ "take": "name", "count": 2 }` | gives (what does not fit lands at the player's feet), takes |
| `{ "knowledge": "npc:….unlock" }`, `{ "revoke": "…" }` | the game's knowledge; `target`: `world` or `player`. A revoke clears **that id only**: what the game already did because of it stays |
| `{ "start": "q" }`, `{ "complete": "q" }`, `{ "fail": "q" }`, `{ "reset": "q" }`, `{ "advance": "q.step" }` | other quests. `advance` refuses unfinished kill, collect and reach objectives (`"force": true` skips that) |
| `{ "spawn": "template", "offset": [5,0,0], "tag": "x", "bindNpc": "id", "transient": true, "exact": false }`, `{ "despawn": "x" }` | a one-off near the player or at `at`, on the ground outside bases. For creatures a step needs, use the step's `spawn` |
| `{ "teleport": [x,y,z] }` | moves the player |
| `{ "chat": {…} }` | a chat line for the player. `toast` is the same now: the game has no free-text notification, so put it in a step's text or an NPC's reply |
| `{ "waypoint": [x,y,z] }`, `{ "waypoint": true, "offset": […] }`, `{ "waypoint": false }` | the player's map and compass pin |
| `{ "talk": "dialogue", "node": "n" }` | opens a chat conversation. The game's own menu opens only when a player talks to the NPC |
| `{ "signal": "name", "amount": 1 }` | feeds `custom` objectives |
| `{ "call": "name" }` | a C# function (`Quests.RegisterAction`) |

In an option's `do`, the first failing action stops the rest, and the player gets the reason in chat. In rewards
and hooks each failure is logged and the rest still runs.

## Dialogue
A dialogue goes into the game's own NPC menu. A node has `text` and `options`; an option has `text`, `if`, `do`, and what the NPC answers: `reply`, else the text of the node it goes to with `goto`. The start node's `text` is a greeting (on a game NPC only with `"greet": true`: its own greetings stay), and `greetings` win over it while their `if` holds.

The game's menu is **one flat list of topics**, so this is how a graph maps to it:
- A `goto` to a node with options adds those options to the list from then on, and they stay.
- `once` removes a topic after it was heard, `if` keeps it for its moment, `close` ends the talk with the NPC's outro, `hint` (`Hint`, `Attention`, `MainQuest`) is the marker over its head.
- A dialogue on a game NPC adds topics to its own, and several mods can add to one NPC. On a clone it replaces the base's greetings and topics.
- **A hand-in is on offer only with the items**: an option whose `do` advances a `deliver` step is listed while the player holds what it takes.
- `if` can be any condition. The game checks one knowledge query per line: `quest`, `step` and `knowledge` are that query and act at once; anything else gets a knowledge object of its own that the engine keeps for each player, about a second behind.
- The game's journal has no give-up button. A topic can be one: `"if": { "quest": "q", "is": "active" }, "do": [ { "reset": "q" } ]`.

Voice-over: `"voice": "name"` plays a line of your cooked audio ([sounds.md](sounds.md)), `"voice": "<guid>"` any line of the game, through the game's dialogue. An option's `voice` goes with its reply; default the target node's.

Option positions and node names feed the lines' ids: add new options at the end of a node.

**What the game's menu cannot do**: open from the server (it opens when a player talks; use a marker and a hint), show a refusal, or replace the list with a node's options. [services/npcs.md](../services/npcs.md) "Limits" has the whole list.

A dialogue with `"native": false` is a chat conversation instead, for a mod vanilla clients join: `/talk` within `talkRange` of the NPC (default 8 m), or when an NPC's game dialogue starts, or a `talk` action; numbered options, `/talk <n>`, `/talk end`.

## NPCs
`npcs` says how to find an NPC: `template`, or `Quests.BindNpc` from code, or a `spawn` action with `bindNpc`, or `place` to put it in the world ("Places" above). A dialogue in the game's menu needs `template`.

## Code
```csharp
using Kiln;

public sealed class MyMod : Mod
{
    public override void OnLoad()
    {
        Quests.Define(new QuestDefinition
        {
            Id = "from_code",
            Name = UiText.Of(("en", "From Code")),
            Requires = [QuestCondition.Level(5)],
            Steps =
            [
                new QuestStep { Id = "ring", Text = "Ring the bell", Objectives = [QuestObjective.Custom("mymod.bell", 3)] },
                new QuestStep { Id = "tally", Text = "Count your gold",
                                Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Custom, Progress = c => GoldOf(c.Player), Count = 100 }] },
            ],
            Rewards = [QuestAction.Experience(500), QuestAction.Code(ctx => Pay(ctx))],
        });
        Quests.RegisterPredicate("mymod.isNight", c => Time.IsNight);
    }

    [Subscribe] private void OnBell(BellRungEvent e) => Quests.Signal("mymod.bell", QuestHolder.Of(e.Player));
    [Subscribe] private void OnDone(QuestCompletedEvent e) => Log.Info($"{e.Holder} finished {e.Quest.FullId}");
}
```
Reference Kiln.Quests like any service: `<ProjectReference Include=".../managed/services/Kiln.Quests/Kiln.Quests.csproj"
Private="false" ExcludeAssets="runtime" />`. Definitions belong to the mod that made them and go away when it unloads;
players' progress stays.

A quest from code gets the game's UI with no flag, like one from a file: the mod says `"clients": "mod"`, and the journal, counters, markers and topics follow. Only one thing differs. The game's journal is built at boot, before a C# mod runs, so Kiln writes what your code defines to `kiln/data/kiln-quests/native/<mod>.json` and builds it at the next start; until then the quest runs with chat lines, and kiln.log says so. To have it at the first start, on every client too, ship that file as `quests/generated/<mod>.json` in the mod (the sample does) and write it again when the quest's steps or texts change. Only what the game shows goes into it: objectives, conditions and rewards keep running from your C# objects, including `Progress` functions and `QuestCondition.When`.

## Saving, and changing a quest later
Progress is stored in Kiln's sidecar of the world (`kiln/data/worlds/<id>/`), per player and world, written with every
world save. It is keyed by the quest's and the step's ids, so:
- renaming a quest's id or a step's id loses players' place: keep them;
- adding, reordering or removing other steps is fine; a player on a removed step continues at the step of the same
  position in the list;
- removing a mod keeps the records, and they count again if it comes back.

The game's knowledge you grant is the game's: it is in the world save, and a revoke undoes only that id.

`/questadmin reload` (or the admin op `modquests_reload`) reads the engine's side of the files again without a restart. What the game shows (texts, steps, topics, markers) needs a restart on the server and on clients. Renaming a quest, step, dialogue or node changes the game's ids too: what the character's save holds for the old ones is dropped at the next load.

## Testing your quest
`/questadmin state <player|world> [quest]`, `start`, `step`, `advance`, `complete`, `fail`, `reset`, `signal`. `/questadmin list` says `NOT BUILT` for a quest the game's journal does not have. Without a
player, the C# class `QuestInput` feeds a holder from `QuestHolder.Synthetic("name")`, `NativeQuests.Wanted` says what the game's knowledge should hold for it, and `Quests.RecentActions` shows what
rewards did: `mods/tests/quest-engine-test` runs every objective this way, and `mods/tests/quest-native-test` the game's UI.
