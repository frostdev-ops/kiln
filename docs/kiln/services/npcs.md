# Friendly NPCs and quests in the game's own UI

A mod describes in JSON what the game itself then shows, and this is the default for every quest and dialogue: new knowledge ids, quests in
the game's journal with step counters and notifications, dialogue lines in the game's NPC menu with per-language text and voice-over,
quest markers on the map, and new NPCs with a place in the game's roster (one per world, unlock rule, summon tool, beds, vendor stock).
Evidence: [docs/re/quests.md](../../re/quests.md) sections 11 and 12 and
[docs/re/creatures.md](../../re/creatures.md) section 7.8. The quest logic itself (objectives, rewards) is the
engine's, [docs/kiln/modding/quests.md](../modding/quests.md).

| Layer | File |
|---|---|
| Boot build | `native/services/quests/native_pack.cpp` (finds the files, serves the result through kiln.assets) |
| Builders | `native/services/quests/questpack.cpp` (JSON to definitions), `questbuild.cpp` (the game's resources); portable, also compiled by the tests |
| Run time | `managed/services/Kiln.Quests/Native/` (`NativeQuests`: the mirror, computed conditions, dialogue options, NPCs, markers; `NativeIds`; `NativeExport`: quests from C#), `Places.cs` (where things go) |
| Villagers | `managed/services/Kiln.Creatures/Villagers.cs` + `kiln.creatures.npc_command` |
| Format | `schema/kiln-quests.schema.json`, lint rules KL210, KL312 |
| Samples | `mods/samples/quests-sample/` (the Warden's chain), `mods/samples/NpcSample/` (Tobin: voice-over, roster, a trade, a summon tool) |
| Tests | `tools/kfc/tests/test_questbuild.py` (offline, on the client's own data), `mods/tests/quest-native-test/` (lab) |

## Status

- **Lab and offline: verified on the dedicated server** (svn 1287369): see "Verification".
- **Seen in a real client** (joined to a lab): Tobin's topics in the game's NPC menu, his greeting's voice-over, his quest in the game's journal.
- **Not seen yet**: map markers, notifications, the tracked-quest HUD, step counters, the NPC directory, the summon tool and beds. See "Manual test".

## What needs what

| You use | Vanilla clients | `clients` | `side` |
|---|---|---|---|
| A quest, a dialogue, `clone`, `roster`: the default | refused | **`mod`** | `both` |
| `"journal": "chat"`, `"native": false` | follow in chat, `/quests`, `/talk` | `none` | `server` is enough |
| New knowledge ids only (`knowledge`), read and granted by the server | join, and store ids they do not use | `none` | `server` is enough |
| A game NPC placed as a giver (`npcs.*.place`, nothing else) | see it | `none` | `server` is enough |

**`clients` must be `"mod"`, not `"kiln"`.** A client builds these resources from the mod's own
`quests/*.json` at boot, before any world or server connection exists, so a server cannot send them. `kiln lint` reports
KL210 as an error otherwise.

## The format

`quests/*.json`, read by two readers: this boot build and the quest engine. No key asks for the game's UI: only `"journal": "chat"`
and `"native": false` stay out of it. Field reference: [schemas/kiln-quests.md](../modding/schemas/kiln-quests.md).

```jsonc
{
  "format": 1,
  "knowledge": {
    "tobin_met": { "scope": "world" },               // world | player (default) | both; bool (default) | counter | flags
    "pages": { "type": "flags", "expectedFlags": 7 }
  },
  "npcs": {
    "tobin": {
      "template": "NPC_Workshop_Fisher_Trader",      // the base: its look, behaviour, workshop
      "clone": { "guid": "299d68c5-…", "name": "Kiln_NpcSample_Tobin" },   // fixed forever; true derives both
      "names": { "en": "Tobin the Wanderer", "de": "Tobin der Wanderer" },
      "description": { "en": "Tobin the Wanderer trades a little…" },  // a roster NPC's line on the game's Survivors page
      "dialogue": "tobin",
      "place": { "near": "player", "distance": [25, 45], "yaw": 90, "once": true, "unlock": true },   // or "at": [x, y, z]
      "roster": {                                    // an entry in the game's NPC roster; needs clone
        "unlock": "tobin_met",                       // a knowledge name, 0x…, or npc:<debugName>.unlock; none = always
        "roamingRange": 10,
        "workshop": "0xc4acc104",                    // optional: default the base's
        "summonItem": {                              // a GUID, or kiln.content's item definition
          "guid": "83ff4ce1-…", "name": "Kiln_NpcSample_TobinTool",
          "base": "NPC_Townsfolk_Male_Villager_08_fisher_Trader",   // its name and description are the NPC's: see "Recruiting and beds"
          "texts": { "en": { "caption": "Tobin's Whistle" } },      // what the tool is
          "refs": { "equipment.placedEntity": "299d68c5-…" }   // the clone's GUID: what the tool places
        }
      },
      "vendor": { "recipes": [ { "guid": "7797ab3a-…", "name": "…", "station": "0xc4acc104",
                                 "inputs": [ { "item": "Material_T1_Twigs", "count": 2 } ],
                                 "outputs": [ { "item": "Material_T1_Plant_Fibre", "count": 1 } ], "unlock": "always" } ] }
    }
  },
  "quests": {
    "tobin_kindling": {
      "name": { "en": "Kindling for Tobin" }, "giver": "tobin", "scope": "player",
      "source": "Fisher",                            // source: the journal's portrait (keen::QuestSourceType)
      "steps": [
        { "id": "gather", "title": { "en": "Gather 3 Twigs" },   // the short line: the journal's list, the tracked quest
          "text": { "en": "Tobin's fire is dying." },            // the journal's paragraph
          "objective": { "collect": { "item": "Material_T1_Twigs", "count": 3 } },   // the journal counts 0/3
          "site": { "near": "tobin", "distance": [30, 50] },     // a place on the ground, outside every base
          "marker": { "name": { "en": "Tobin's Kindling" } } }   // the marker comes by itself; this names it
      ],
      "rewards": [ { "xp": 100 } ]
    }
  },
  "dialogues": {
    "tobin": {
      "start": "hello",
      "greetings": [ { "if": { "quest": "tobin_kindling", "is": "active" }, "text": { "en": "Back so soon?" } } ],
      "nodes": {
        "hello": {
          "text": { "en": "Well met, Flameborn." }, "voice": "tobin_hello",  // a voiceover step of assets/cook.json, or a GUID
          "options": [
            { "text": { "en": "I can find kindling." }, "if": { "quest": "tobin_kindling", "is": "available" },
              "do": [ { "start": "tobin_kindling" } ], "reply": { "en": "Bless you." }, "voice": "tobin_thanks",
              "hint": "Attention", "goto": "end" },
            { "text": { "en": "Who are you?" }, "reply": { "en": "A wanderer." }, "once": true }
          ]
        }
      },
      "farewell": [ { "text": { "en": "Keep warm." } } ],
      "transition": [ { "text": { "en": "Anything else?" } } ]
    }
  }
}
```

### From a graph to the game's menu

The game's dialogue is one flat list of topics, and each line has exactly one knowledge query. So:

- The **start node's text** is a greeting (an init line). `greetings` adds lines that win over it while their `if` holds.
- Every **option** of every node is one topic. Its reply is `reply`, else the target node's text, else its own text; its
  voice is `voice`, else the target node's. `do` runs on the server through the engine (`Dialogues.ApplyOption`), which
  checks `if` again there.
- A **later node's** options need a gate: Kiln adds a knowledge object that the option leading there sets. It is per
  player and stays set, so those topics stay listed afterwards.
- **`if`** can be anything the engine can evaluate, on any node. The game checks **one knowledge query per line**, so:
  `{quest, is: available | active | done | started}` of a quest of this mod, `{step, is: active | done}` and `{knowledge, is}` with a name the build knows
  are that one query, and the game evaluates them at once. Everything else (`all`, `any`, `not`, `level`, `item`, `predicate`, `available` on a quest with `requires`, a gate or
  `once` together with an `if`) gets a knowledge object of its own (`cond.…`, "computed" in the manifest) that the engine keeps true or false for each
  player, once a second. The menu is still the game's. The engine checks `if` again when the line is picked.
- **A hand-in** (an option whose `do` advances a step with a `deliver` objective, with `if` the step being active or no `if`) is on offer only while the player holds the items: its one query is
  `s.<quest>.<step>.ready`, which the engine keeps. With the items gone between the menu and the pick, `Advance` refuses, the step stays, and the player gets the reason in chat.
- **An NPC without a dialogue** that is a quest's `giver` or a `deliver`'s `to` gets a topic for each by itself: the quest's name, and the step's text.
- `once` hides a topic after it was heard; `close` plays the NPC's outro after the reply; `hint` (`Hint`, `Attention`,
  `MainQuest`) is the marker over the NPC's head.
- A dialogue on a **game NPC** adds topics, and its lines stay. Its start node needs no text then, unless `"greet": true` makes it one
  more greeting. Several mods can add to one NPC; they share one override, in load order. On a **clone** the dialogue replaces the base's
  greetings and topics; the base's `farewell` and `transition` lines stay, with the base's voice, unless you give your own.
- `kiln lint` reports what the build refuses as KL312. The build logs the same things; that mod then has no native data, and its quests run with chat lines.

## Ids

Every id is FNV-1a-32 of a UUIDv5 of `<mod>:<kind>:<name>`, the same in C++ and in C# (`NativeIds`). Ids therefore do not
change between runs and saves keep them, but **renaming a mod, quest, step, dialogue, node or moving an option changes
ids**: what the save holds for the old ones is dropped at the next load. An option's id includes its index, so add
new options at the end of a node.

| Object | Knowledge name | Kind |
|---|---|---|
| your own | `<name>`, from elsewhere `<mod>:<name>` (`Knowledge.Resolve`) | as declared |
| a quest's "rewarded" Bool (its journal `entryId`) | `q.<quest>` | player |
| quest state | `q.<quest>.started`, `.active`, `.done` | player, or world for `"scope": "world"` |
| step state | `s.<quest>.<step>.visible`, `.active`, `.done` | as the quest |
| a step's counter, when its counted objectives add up to more than 1 | `s.<quest>.<step>.count` | counter, as the quest |
| a hand-in's "holds the items" | `s.<quest>.<step>.ready` | player |
| a line (its "heard" knowledge) | `line.<dialogue>.<node>.<option>` | player |
| a node's gate | `gate.<dialogue>.<node>` | player |
| a condition one query cannot say | `cond.<dialogue>.<node>.<option>`, `cond.<dialogue>.greeting<n>`, `cond.auto.<quest>.available` | player |
| the always-true requirement | `kiln.always` | world counter, never set |

## Running it

- **At boot** (kiln-quests.dll, before anything can read a resource): reads `quests/*.json`, `quests/generated/*.json`, what code defined at an earlier start
  (`kiln/data/kiln-quests/native/<mod>.json`), `assets/cooked/audio.json` and
  `assets/cook.json` of every enabled mod of the load order, compiles, and hands kiln.assets the rebuilt
  `GameKnowledgeResource`, `JournalRegistryResource` and `NpcCollection`, and the edited or new `TemplateResource`s. Definitions of
  summon tools and vendor recipes go to kiln.content. On a client, the texts go to kiln.content's loca hooks.
  ```
  knowledge: native: 2 mod(s): 48 new knowledge object(s), 3 quest(s), 2 NPC(s) in the roster, 6 template(s), 0 text(s); GameKnowledgeResource 1509084 bytes served by kiln.assets
  ```
  An error in one mod leaves out that mod's native data and logs why (`knowledge: native: <mod>: …`); the others build.
- **Changes need a restart**: resources are read once per process. `Quests.Reload()` reloads the engine's quests, not these.
- **At run time** (`NativeQuests`, started by the engine, server side):
  - mirrors the engine's state into the knowledge above: at every change, at a join, after a reset, and every 2 s whatever differs;
  - keeps the computed conditions and hand-ins for each player, once a second;
  - runs a native option's `do` on `DialogueLineEvent`, on the tick thread;
  - places NPCs with `place` once their world's data and entities are there. A dedicated server has both when it starts; a game client's hosted
    world starts about 10 s before the game loads it, and has its entities when its player arrives, so there it waits for the first player. A new
    hosted world starts with no NPC bound and nothing waiting from the last one;
  - moves a step's marker to the step's place when someone enters the step.
- **Removing a mod**: the next start has no objects for its ids, so the world's and players' records of them are dropped at
  load, and saved entities of its clones are gone (lab, below). Its quest sidecar stays in the world's data. Knowledge the game set on its
  own account stays: a game NPC's unlock granted for `place` is one of the game's ids.

### Placing NPCs

The game enforces two rules on any entity with an NPC template (`unique_npc_spawns`):
one entity per NpcId, and a **locked roster NPC is destroyed** (about a second after it spawns).

| `place` of | Unlock not set | Default of `"unlock"` |
|---|---|---|
| a game NPC | Kiln grants it for this world, waits for the roster to report it, then spawns | `true` |
| a clone with `roster.unlock` | Kiln waits for your knowledge, then spawns | `false` |
| a clone without `roster` | no such rule | – |

- **Where**: `"at": [x, y, z]`, or `"near": "player" | npc id | [x, y, z]` with `"distance": [min, max]`. On the voxel surface, outside every base zone (`"inBase": true` allows one, `"exact": true` takes `at` as it is). A wanted
  place in a base moves out, with a warning. Kept per world: one place from then on.
- **Checked**: 3 s after the spawn. Gone: a warning and another try, up to 5, then an error. `"once"` counts only an NPC that stayed.
- `"once": true` remembers the placement in the world's data: an NPC that later dies or is removed is not placed again. Either way, an
  entity already in the world is used and nothing spawns.
- Granting a game NPC's unlock is a real change to that world: it shows in the NPC directory and stays.
- The engine is told the entity (`Quests.BindNpc`), so `talk` and `deliver` objectives find it.

### Vendors

A vendor's stock is the recipes of its workshop. A clone shares its base's workshop, so `vendor.recipes` appear at the base NPC
too (Tobin's trade also shows at the fisher trader). A workshop of its own would need a new id in `WorkshopRegistryResource` **and**
in the crafting menu's configuration (`cfg+0xf098`: 48 workshops, client UI data); this was not built. `roster.workshop` can only name an
existing workshop.

### Recruiting and beds

Unchanged game paths, driven by the roster entry:

- **the game's Survivors list draws a roster NPC from its tool**: the row's title is the tool's item name, the page's text is the
  tool's item description, and only the page's title is the roster entry's own name (client 26430625: the row `0xc046c0` reads
  `ItemInfo.name` of `NpcResource.itemId`, the page `0xc46f1f` reads `.description`). So every tool of the game is named after its NPC
  ("Captain Melville Fontane", "… is a fisher and entrepreneur who offers fishing gear."). The boot build does the same for a tool
  defined inline: the NPC's `names` and `description`, per language. A name or description of the tool's own is replaced, with a warning
  (lint KL313); say what the tool is in its `caption` or `lore`. Without `description` the page shows the NPC's name: the base tool's text
  would be about another NPC. The game shows one item either way, so the tool in the inventory carries the NPC's name too;
- the tool's `equipment.placedEntity` is the clone's GUID. Using it places the NPC, or, when the NPC is already in the world, moves it
  (`NpcCommandEvent` Reset);
- every vanilla tool has `placementConsume` set, so expect a clone of one to be used up by placing;
- the tool's Assign sequence (aiming at a bed) sets `NpcCommon.bedId` and `NpcBed.associatedNpc`, finding the NPC by the tool's item in the roster.

## Limits

What the game's UI cannot do, and how a mod works within it instead of around it:

| It cannot | Why | Do this |
|---|---|---|
| More than one knowledge check on a line | the data format | nothing to do: anything more becomes a computed object, about a second behind |
| Nested menus: a node's options replacing the list | one flat topic list | `goto` adds the node's topics to the list from then on. `once` removes a topic, `if` keeps it for its moment, `close` ends the talk |
| Open the NPC menu from the server | the client opens it when the player talks | a marker on the NPC, a `hint` over its head, a greeting that wins while a step is active. `{"talk": …}` stays a chat conversation |
| A free-text notification | its notifications come from journal knowledge | a step's text, an NPC's reply. `{"toast": …}` is a chat line now, and lint warns |
| A give-up button | the journal has none | a topic: `"if": {"quest": "q", "is": "active"}, "do": [{"reset": "q"}]` |
| Show rewards in the journal | filling `QuestRewardResource` makes the game pay them too, and its item claim path was not traced | the engine pays at completion: the game's XP path and item popup. Say it in the text |
| Different places per player | a marker is one entity with one place per step | a site per world: everyone goes to one place. A `reach` `offset` still counts per player, but the marker shows the last player's |
| A refusal in the menu | the game has its own rule (id 1, missing requirement) only for its own check | conditions hide what cannot be picked; a pick that still fails tells the player in chat |

The numbers:

| Limit | Why | What happens |
|---|---|---|
| 64 roster NPCs in all, 35 are the game's | the unlock state is one u64 (`0xa2ef90`) | build error for the mod group |
| 32 texts per line | the client stops the game above it (`0x460b40`) | Kiln writes one per line |
| 8 knowledge operations per line | the server pushes at most 9, with "heard" | build error |
| 6,050 world and 11,000 player objects (5,825 and 10,744 are the game's) | what the game reserves its run-time maps for (`0x99ead0`) | build warning; beyond it is untested. Those numbers bound entries set at run time, so this is likely softer than it looks. A quest costs 4 player objects + 3 to 5 per step + 1 per line; 256 are left before the warning. quests-sample (4 quests, 11 lines) takes 50, NpcSample 17 |
| new Extern (named) queries | not built: step and line requirements are single objects | – |
| template table 13,000 (12,269 used) | inline in ecs storage (`docs/re/gamedata.md`) | shared with kiln.gamedata clones; one per step with a place |
| water | the voxel surface does not say where water is | a site can be a lake bed: name a place, or check the map |

## API

```csharp
using Kiln;

// what the boot build made (any thread)
NativeModInfo? m = NativeQuests.Mod("npcsample");              // Ok, Errors, Knowledge, Quests, Lines, Npcs, Markers, Computed
uint? met = NativeQuests.KnowledgeId("npcsample", "tobin_met");
uint same = NativeIds.Knowledge("npcsample", "tobin_met");     // the rule itself, no build needed
await Knowledge.World.Grant(met!.Value);                       // or Knowledge.World.Grant("npcsample:tobin_met")
await Knowledge.GrantUnchecked(EntityId.None, 0x12345678);     // through the game's apply even if unknown: it is dropped

// the mirror (tick thread); needs no player, so a test can ask it
bool shown = NativeQuests.ShownByGame(def);                    // says native, and the boot build made it
IReadOnlyList<NativeWanted> w = NativeQuests.Wanted(fullId, holder);   // what the knowledge should hold now: ("done", id, 1), ("gather.count", id, 2), ...
bool holds = NativeQuests.Holds(m.Computed[0], holder);        // a computed condition or hand-in, as the engine sees it
NativeQuests.Resync(holder);                                   // write it again now; the engine does at every change, join and reset, and every 2 s

// villagers (tick thread)
Villager? v = Villagers.Find(templateGuid);   // or Villagers.Of(entity), Villagers.List()
v.GoTo(pos);                                  // the game's GoToPosition: walks there (path grid: a player nearby; ignored in state Start)
v.Teleport(pos, yawDegrees: 90);              // the game's Reset: moves it, and that becomes its home
v.Stay();                                     // Reset where it stands
v.Follow(player, distance: 4, catchUp: 40);   // repeated GoTo; Reset beyond catchUp; ends with Stay, GoTo, despawn, mod unload
VillagerState s = v.State;                    // keen::active_npc::State
Villagers.Watch(v.Entity);                    // VillagerStateEvent about 4x a second (all villagers: 1x a second while subscribed)
[Subscribe] void OnState(VillagerStateEvent e) { /* e.Entity, e.NpcId, e.From, e.To */ }
```

C: `kiln.creatures.npc_command(const KilnNpcCommand*)` (appended; any thread), `kiln.knowledge.native_manifest(buffer, cap, &len)`
(appended), `KILN_KNOW_REQ_UNCHECKED` in `KilnKnowledgeRequest.flags`.

## Verification

### Offline (`python3 -m unittest discover -s tools/kfc/tests -p test_questbuild.py`, 16 tests)
The same C++ as kiln-quests.dll, compiled for Linux, on resources tools/kfc reads from the installed client. The result is decoded with the
game's reflection. Checked: every vanilla and every new knowledge id found by the game's bucket rule with the right type and
scope; the game's arrays unchanged in the rebuilt resources; moved quests and roster entries decode to the same values; the
journal's requirements name the manifest's objects; a clone's NpcId and lines; the lines' voice-over GUIDs against the cook's
UUIDv5; Balthazar's vanilla lines intact and nothing else in his template moved; a marker for every step with a place, its requirements, tag and type; the journal's counter in the shape the
game's own step uses (`SimpleCount GreaterThan n-1`); one query per line, with hand-ins and computed conditions; the Warden's chain with no keys and the C# quest from its shipped file; `"journal": "chat"` and `"native": false` building nothing; topics for a giver
without a dialogue; bad input reported and nothing built; every constant of the builders against the game's reflection.

### Lab (no player; `mods/tests/quest-native-test`)
The whole sequence on one build, from a clean save: 175 PASS, then 59 after a restart, 7 after a restart without the mod, 5 with the mod back; 0 FAIL, one `[E` line, the deliberate "gave up". Log
excerpts: [services/quests.md](quests.md) and [docs/re/quests.md](../../re/quests.md) section 11. In short:

| Row | Check | Result |
|---|---|---|
| Q3 | negative: a new id without the rebuilt map | Kiln refuses it; the game's own apply drops it |
| Q3 | with the map: world bool, counter, flags accepted | PASS |
| Q3 | the world save keeps them across a restart | PASS |
| Q3 | a restart without the mod drops them | PASS |
| Q3 | the mod back: its knowledge stays lost, the ids work again, a world quest's state returns from the engine's sidecar | PASS |
| Q3 | engine state mirrored, the game's evaluator sees the step visible, then done; survives a restart | PASS, world-scope quest |
| Default | a quest with no journal key is in the game's journal | PASS |
| Mirror | complete through a line of the menu, after an action that ended off the tick thread: the knowledge ids, the game's evaluator on the line's own query going false, a second pick refused, a reset clearing it, all of it twice | PASS, world-scope quest |
| Counter | the journal's counter object at 0, 2, 3 of 3, and back to 0 after a reset | PASS |
| A moment on screen | a step already fulfilled: its counter full at once, still open after 343 ms, passed at 3437 ms | PASS |
| Hand-in | not on offer without the items or with too few, on offer with them, refused and staying on the step without, what the mirror wants after it; a reset, then again | PASS, synthetic holder |
| Conditions | all/level/not/item, available with a requirement, a gate with an `if`: one object each, true and false at the right moments | PASS, synthetic holder |
| Places | an NPC and two Vukah asked for the middle of the base: on the voxel surface, at least 20 m from every base zone; the marker there, named by the journal's step | PASS |
| Creatures | verified after 8 s; one destroyed: logged, a new one elsewhere, the marker follows; destroyed again: gives up as an error; a reset ends it | PASS |
| N | a game NPC placed once, survives restart, a second spawn is destroyed | PASS |
| N | a clone in the roster: locked is destroyed; unlocked lives; one per world; registry follows | PASS |
| N | a clone without a roster entry | PASS |
| N | the entity's template slot reads the rebuilt data (override and clone) | PASS |
| N | `NpcCommandEvent` Reset moves it and sets its home | PASS |
| N | `GoTo`, `Follow`'s walking, `VillagerStateEvent` | **not seen in the lab**: the game ignores GoToPosition while an NPC is in state Start, and without a player NPCs stay in Start, so no state ever changed. Manual test |

A player's own knowledge cannot be written without a player, so the player-scope rows assert what the mirror wants (`NativeQuests.Wanted`, `Holds`) and the writing is shown by world quests, through the same code. Player maps, the client's reads and everything drawn are not covered without a player.

### To repeat
```sh
export KILN_OUT=<build dir>
mods/samples/NpcSample/build-assets.sh
for m in tests/quest-native-test/quest-native-test.csproj:quest-native-test samples/NpcSample/NpcSample.csproj:npcsample samples/quests-sample/QuestsSample.csproj:quests-sample; do
  dotnet build mods/${m%%:*} -c Release -o $KILN_OUT/mods/${m##*:} --artifacts-path $KILN_OUT/dotnet; done
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any
bash -c 'ulimit -e 1; exec nice -n 19 ionice -c 3 tools/lab/lab.sh start <lab>'; grep -a 'quest-native-test' ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log   # run 1
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>                                                     # run 2: the save kept it
# without the mod:
mv <lab>/kiln/mods/quest-native-test/quests{,.off}; rm <lab>/kiln/data/kiln-quests/native/quest-native-test.json   # what it defines in code
touch <lab>/kiln/mods/{npcsample,quests-sample}/.disabled                                                     # the negative test, and the drop
```
The test changes the lab's world: after the whole sequence it only says so. Copy the save back and delete `kiln/data/quest-native-test/state.txt`, `kiln/data/worlds` and `kiln/data/kiln-quests` to repeat it. Without quest-engine-test in the same lab: both use `QuestInput`.

## Manual test
```sh
export KILN_OUT=<build dir>
mods/samples/NpcSample/build-assets.sh
dotnet build mods/samples/NpcSample/NpcSample.csproj -c Release -o $KILN_OUT/mods/npcsample --artifacts-path $KILN_OUT/dotnet
tools/client/client.sh backup-saves
tools/client/client.sh deploy --real --policy any --mod $KILN_OUT/mods/npcsample
```
Launch; click Skip on "Processing Vulkan shaders". Play → **LOCAL** tab → slot 1 (a character copy), host offline. Never the STEAM tab.
`kiln.log` must show `saves: world loaded: slot 0x0002 (world1.ksd)`. Commands go through the console folder:
```sh
C=~/.local/share/Steam/steamapps/common/Enshrouded/kiln/control/console
echo 'npcsample here' > $C/here.cmd
```

| # | Do | See or hear | `kiln.log` |
|---|---|---|---|
| N1 | Boot | | `knowledge: native: 1 mod(s): 17 new knowledge object(s), 1 quest(s), 1 NPC(s) in the roster, 3 template(s), <more than 0> text(s)` |
| N2 | In game, `npcsample here` | Tobin, with the fisher trader's look, about 3 m east of you | `npcsample: Tobin is at (…)` |
| N3 | Talk to him | Title "Tobin the Wanderer"; greeting "Well met, Flameborn. The night is cold and my fire is dying."; two 523 Hz beeps (three 784 Hz with German voice) | |
| N4 | Read the topics | "I can find you some kindling." with an attention marker, "Who are you?". No "Here are your twigs." | |
| N5 | Pick "Who are you?" | "Just a wanderer. I trade a little…", no sound | `quests: native: … chose npcsample:tobin hello[2]` |
| N6 | Pick the kindling topic | "Three twigs would do. Bless you." and a 997 Hz chime, 0.5 s; the game's new-quest notification | `chose … hello[0]`, then the quest started |
| N7 | Open the journal | "Kindling for Tobin", fisher portrait, step "Gather 3 Twigs for Tobin" with a counter 0/3 (holding twigs: it shows them for about 3 s, then passes) | |
| N8 | Open the map | Marker "Tobin's Kindling" 30 to 50 m from Tobin, outside any base; the game's waypoint pin there too | `tobin_kindling step gather: at (…) (the voxel surface, …)` |
| N9 | Talk to him with no twigs at the return step: `echo 'questadmin step <character> npcsample:tobin_kindling return' > $C/x.cmd` | "Bring the Twigs to Tobin" is active and its marker is on him, but **no "Here are your twigs." topic** | |
| N10 | `npcsample twigs` or gather 3, wait 2 s, talk, pick "Here are your twigs." | Thanks line and chime; 3 twigs taken; quest complete in the journal; about 100 XP; his summon tool in your inventory, named "Tobin the Wanderer", with the line "Tobin's Whistle" under the name. Talk again: the hand-in and the kindling topic are gone | `chose … hello[1]`, quest complete; **no** `must run on the tick thread`, no `FAILED` |
| N11 | Open his crafting menu, then the fisher trader's | "Tobin's Trade": 2 Twigs → 1 Plant Fibre, at both | |
| N12 | Journal > Survivors, before and after you hold his tool | The row reads "Tobin the Wanderer" / Fisher, with his portrait. The page: title "Tobin the Wanderer", text "Tobin the Wanderer trades a little and keeps a fire going…" (German: "Tobin der Wanderer", "… handelt ein wenig…"). Nowhere "Tobin's Whistle" as his name or "Calls Tobin to where you point" as his text | no `native:` warning about `summonItem` |
| N13 | In a base, use the whistle on a spot. Run `npcsample whistle` if it was used up, then aim at a bed | He moves there, then takes the bed | `npcsample status` shows him |
| N14 | `npcsample follow`, walk off, then `npcsample stay` | He walks after you, then stays there | `npcsample status` shows the state |
| N15 | Talk to Balthazar, if he's in this world | His usual topics plus "Have you met Tobin?", with chime | `chose npcsample:balthazar_tobin topic[0]` |
| N16 | Set text and voice language to German, talk to Tobin again | German text; three 784 Hz beeps on the greeting | |
| N17 | Quit to menu, host the same slot again | Quest still complete; one Tobin; the hand-in stays gone; no notification for the done quest | he isn't placed by the service: `npcsample here` spawned him, so no `native: NPC npcsample:tobin` line. Never `was placed once in this world and is gone` while he stands there |
| N17b | `echo 'questadmin reset <character> npcsample:tobin_kindling' > $C/x.cmd`, talk to him | The journal entry is gone; "I can find you some kindling." is back; the quest can be done again the same way | `tobin_kindling: reset` |
| N18 | Remove the mod | Game starts without him; the journal entry is gone | |

A topic missing at N4, or no `chose` line at N5, points at the client's option path.
