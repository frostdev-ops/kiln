# Kiln.Quests: the quest engine behind the game's own quest UI

Quest chains from mods, run by the server for players, parties and the world, and shown by the game itself: its journal and tracked-quest
HUD, its notifications, its NPC menu with its voice playback, its map markers. Kiln draws no window of its own for a quest. The modder's
guide is [modding/quests.md](../modding/quests.md); this page is the API and how it works. What the game's resources get is
[npcs.md](npcs.md); the game's knowledge underneath is [knowledge.md](knowledge.md).

| Layer | File |
|---|---|
| Definitions | `managed/services/Kiln.Quests/EngineDefs.cs`, `EngineEvents.cs` |
| Engine | `Engine.cs` (registry, state machine, `QuestsStartup`), `Objectives.cs` (+ `QuestInput`), `Actions.cs` (conditions, actions, waypoint), `QuestTick.cs` (the tick thread) |
| The world | `Places.cs`: places on real ground outside bases, the creatures of a step, their checks |
| The game's UI | `Native/NativeQuests.cs` (the mirror, computed conditions, NPCs, markers), `Native/NativeExport.cs` (quests from C#), the boot build `native/services/quests/` |
| JSON | `Json.cs`, `schema/kiln-quests.schema.json`, `kiln lint` kind `quests` |
| Text fallback | `EngineConversations.cs`, `Commands.cs`: chat lines, `/quests`, `/talk`, only for `"journal": "chat"` |
| Commands | `Commands.cs` (`/questadmin`) |
| Native | `native/services/questmark/` (`kiln.questmark` v1: the `PlayerWaypoint` write program) |
| Admin | `mods/Kiln.Admin/Ops.Quests.cs` |
| Test | `mods/tests/quest-engine-test/`, `mods/tests/quest-native-test/`, `tools/kfc/tests/test_questbuild.py` |
| Sample | `mods/samples/quests-sample/` |

## What the game gives, and what Kiln adds
The game has no quest objects at run time: every vanilla quest is knowledge plus client data (`docs/re/quests.md`). A mod's quest gets
both at boot (the journal entry, its knowledge objects, its lines, its markers), and the engine runs it:
- **State** lives in Kiln's data: `QuestBook` per character and for the world, in the world's sidecar under the data owner
  `kiln-quests` (`CharacterData`, `WorldData`), written with every world save. **What the player sees follows from it**: the mirror writes
  the book into the quest's knowledge objects, and the game's journal, notifications, markers and line filters read those.
- **Holders**: a player is keyed as the world's `known_users` keys a character: the game's account key + CharacterId
  ([character-data.md](character-data.md)). Two characters of one account on one world have separate books, and **a quest never
  travels**: the same character on another world starts that world's quests from nothing, as the game's world knowledge does. The mirror does
  travel in the character's save (it is the game's knowledge), so a join clears it when this world's book has no such quest (`EngineCharacters.cs`).
  What a quest grants as player knowledge stays with the character.
- **From before characters**: a book filed under a Steam id, account id or the host key 1 moves once, to the first character
  that loads it. On a build without the game's character id, holders stay per account (`QuestHolder.Character` is none).
- **A revoke undoes that id only.** The game may have set related ids itself because of it (knowledge.md "Lab": the Bard's
  unlock also sets `0x08055951`), so don't treat a revoke as a rollback.
- **XP** goes through kiln.progression (`AddExperience(..., ExperienceSource.Quest)`), the game's own quest-reward path.

### What a player sees, and where it comes from
| Seen in the game | From |
|---|---|
| The quest in the journal, its portrait, its steps' short lines and paragraphs | `JournalRegistryResource`: `name`, `source`, a step's `title` and `text` |
| "New quest", "quest updated" notifications | the game's journal, when a step's knowledge is newly set by the game's apply (`KnowledgeGained` JournalEntry, JournalCompletion). What a join restores stays quiet: "The mirror" below |
| The tracked quest on the HUD | the game's own: the journal's "set active", kept in the character's save |
| A step's counter, 1/2 | a Counter object (`s.<quest>.<step>.count`) and `progressStepsRequirement`: what kill, collect, craft, build and custom objectives add up to |
| Map markers | a marker entity per step with a place, visible while the step is active; `mapMarkerReference` links the journal's step to it |
| The map and compass pin | `PlayerWaypoint`, the game's own (`marker.waypoint`, default on) |
| Topics, replies, greetings, voice | `NpcDialog` lines ([npcs.md](npcs.md)) |

### The tick thread
Everything here runs on the tick thread. A service runs without a synchronization context, and other services' tasks complete on the thread
pool, so a plain `await` loses the thread and the next `Events.Post` throws, half way through. A native option's hand-in once finished the quest and paid the
rewards this way, and never mirrored it: the journal stayed open and the line stayed on offer. Every await in this service is
`await task.OnTick()` (`QuestTick.cs`), which comes back on the tick thread whoever called. A mod's `call` action runs as that mod, so its own `await` comes
back through its scheduler.

## C#
```csharp
using Kiln;

Quests.EnsureStarted();                                   // Kiln does this at its first service tick
var q = Quests.Define(new QuestDefinition { Id = "x", Steps = [...] });   // q.FullId == "<your mod>:x"
var who = QuestHolder.Of(player);                         // QuestHolder.World, .Synthetic("name"), .OfCharacter(key), .OfAccount(id)
QuestResult r = Quests.Start(q.FullId, who);              // r.Ok, r.Reason, r.Status: a refusal is not an exception
QuestStatus s = Quests.StatusOf(q.FullId, who);
QuestProgress? p = Quests.Progress(q.FullId, who);        // p.StepId, p.Counter(0), p.Completions
Quests.Signal("mymod.bell", who, 1);                      // custom objectives
await Quests.Advance(q.FullId, who, "turnin");            // finishes the step; a deliver objective takes its items now
```

| Member | Threads | Notes |
|---|---|---|
| `Quests.Define`, `DefineNpc`, `Dialogues.Define` | any, normally `OnLoad` | as the calling mod's; go away when it unloads; invalid definitions throw `ArgumentException` |
| `Quests.Definitions`, `NpcDefinitions`, `Find(id)` | any | by full id, or by own id when only one mod has it |
| `Quests.Reload()` | tick | reads `quests/*.json` of every loaded mod again; keeps what code defined and all states |
| `Quests.Start`, `Complete`, `Fail`, `Abandon`, `Reset`, `SetStep` | tick | `QuestResult`; `force` on `Start` skips the requirements |
| `Quests.Advance(quest, holder, step?, force?)` | tick; completes on it | talk and custom objectives may be finished this way; kill, collect and reach only with `force`; deliver takes the items or refuses |
| `Quests.StatusOf`, `CanStart`, `Progress`, `ProgressOf` | tick | `ProgressOf` includes world quests |
| `Quests.PlaceOf(quest, step)`, `SpawnedBy(quest, step, holder)`, `SpawnGaveUp(quest, step, holder)` | tick | a step's place, its creatures, whether the game kept removing them |
| `NativeQuests.ShownByGame(q)`, `Wanted(quest, holder)`, `Holds(computed, holder)`, `Resync(holder)` | tick | what the mirror should hold and writes: [npcs.md](npcs.md) "API" |
| `Quests.Signal(name, holder?, amount)` | tick | no holder: every player in game and the world |
| `Quests.RegisterAction`, `RegisterPredicate` | any | JSON's `call` and `predicate`, as the calling mod's |
| `Quests.BindNpc(fullNpcId, entity)` | tick | this entity is that NPC (it wins over the search by template) |
| `Quests.LineResolver` | | `talk.line` values the knowledge names do not know (Native sets it) |
| `Quests.Evaluate(condition, context)` | tick | a condition as the engine reads it |
| `Quests.Status`, `RecentActions`, `WaypointCounters` | any | counters, load errors; the last 128 action outcomes |
| `Dialogues.ApplyOption(player, npc, dialogue, node, option)` | tick; completes on it | a line chosen in the game's menu runs its `if` and `do`, not its `goto` |
| `Dialogues.Open`, `Talk(holder, npc?)`, `Choose(holder, id, option)`, `Close`, `View(holder)` | tick | chat conversations only (`"native": false`): one open per holder; `Choose` checks the option's `if` again |
| `QuestInput.Kill`, `SetInventory`, `Reach`, `Talk`, `Craft`, `Build`, `SetLevel`, `Clear` | tick | the same paths real events take, for holders without a player |

## Events (`Kiln` namespace, posted on the tick thread, built only with subscribers)
| Event | When | Cancel / fields |
|---|---|---|
| `QuestStartedEvent {Quest, Holder, Player}` | before a holder starts a quest; one per party member | Cancel: not started for this holder |
| `QuestStepEvent {.., Step, StepId, Next}` | before a step finishes | Cancel: the step stays with its counters full; asked again at the next counter event or poll |
| `QuestCompletedEvent` | before completion and rewards | Cancel: stays active at its last step |
| `QuestFailedEvent {.., Reason}` | before a failure | Cancel: stays active |
| `QuestStateChangedEvent {Quest, Holder, OldStatus, Status, StepId, StepIndex}` | after any status or step change | not cancelable; the hook to mirror state |
| `QuestObjectiveProgressEvent {Quest, Holder, Step, Index, Objective, OldValue, Value, Target}` | after a counter changed | not cancelable |
| `ConversationOptionEvent {Holder, Dialogue, Node, Option, Npc, RefuseMessage}` | a chosen option passed its `if`, before its `do` | Cancel: the node is shown again with `RefuseMessage` |

No handler may change a definition's fields from these events. Actions run after the state changed.

## How it works
- **Start-up**: `Kiln.QuestsStartup` (`ServiceTick.AutoStart`) reads `quests/*.json` of every loaded mod; side `server` mods that load
  later with a hosted world are found within ~5 s. A mod that is no longer loaded loses its definitions; states stay.
- **Which side**: the engine runs where `Lifecycle.IsServerRunning` holds: a dedicated server, or a client hosting. A joined client shows
  what the game replicates to it.
- **Objective sources** are subscribed only when a definition uses them: `EntityDeathEvent` + `EntitySpawnedEvent`, `EntityDestroyedEvent`,
  `ItemCraftedEvent`, `BuildEvent`, `DialogueLineEvent`, `PlayerJoinedEvent` / `PlayerLeftEvent`. Polls: positions every 12 steps,
  inventories, knowledge and C# progress every 25. Without quests the service costs one tick callback.
- **Credit**: a kill goes to the killer's quests, party members within `partyRadius` of them, and world quests. Collect and
  reach are each player's own; on a world quest collect is everyone's total, reach anyone's.
- **A moment on screen**: a step in the game's journal that a player already fulfils (collect with the items in hand, reach standing there)
  stays open 3 s, with its counter full, so the journal and its notification get to show it.
- **The mirror** is a reconciliation (`NativeQuests`): what the knowledge should hold follows from the book. It runs at every state change and
  counter change, at a join, after a reset, and every 2 s for every player in game, and writes only what differs: a Bool through the
  game's own apply, so its notifications follow; a counter, or anything going back to 0, as the exact value.
  **What a join finds missing is progress coming back, not news**, so for 5 s after a join the reconciliation writes it as the exact value too. The game's journal notifies only for a
  state without the save's mark (bits 32..39 of the state, `isSaveGameStates` of its message; client `0xf04d90`, `0x2fc8e0`): a direct write keeps the mark, the game's apply clears it.
  So done quests no longer notify again after a world switch, when a visit to another world had cleared their mirror. A quest's own state changing is still the game's apply: an auto quest that starts at the join notifies.
- **Places** (`Places.cs`): a step's site, its spawn's place, a fixed `at` of a marker, reach or kill. Column by column around the middle of the wanted
  distance, one column per step: outside every base zone with a margin (`Build.Bases`; default 20 m), the uppermost solid voxel with 3 m of air above it,
  not a building block, at most 2.5 m of difference over 4 m, within 12 m of the height asked for; only then any height. It widens to 450 m by
  itself. Kept per world in `WorldData` under `quest-sites`. Base zones appear a few steps into a world's run, so the first search waits for them, up to 8 s.
- **A step's creatures** (`spawn`): never saved. Every second: one that is gone without a death is logged as removed by the game, and what is owed spawns again, further
  out and further from the bases, with the marker and waypoint following. After `retries` (default 5) it stops with an error. Owed is the count less what the kill
  objective has counted, so a restart or a rejoin spawns the rest. When the step ends, what is left is removed.
- **Givers** (`place`): the same search, then the game's rules: verified 3 s later and placed again up to 5 times, with a warning each time and an error at the end. Placement waits for the world's data and entities ([npcs.md](npcs.md) "Running it").
- **Waypoint**: a step's place also writes `PlayerWaypoint {enabled +0, waypointPosition +8}` through the program `kiln_questmark` (HOST_ONLY, one entity update
  per player, `mark_written` before the write). It overrides a waypoint the player set; only the quest that set it clears it. `"waypoint": false` on the marker turns this off.
- **Quests defined in C#**: the journal is built at boot, before any managed mod runs. `NativeExport` writes what code defines to
  `kiln/data/kiln-quests/native/<mod>.json` in the file format, and the boot build reads it at the next start; until then that quest runs with chat lines and a warning.
  Shipped as `quests/generated/<anything>.json` in the mod, the first start has it too. The objectives, conditions and rewards keep running from the C# objects.
- **The text fallback**: a quest with `"journal": "chat"`, and a native quest whose data failed to build (kiln.log says why, and `/questadmin list` says `NOT BUILT`), tells its progress in chat. A dialogue with
  `"native": false` is a chat conversation: `/talk`, numbered answers, checked again when chosen, closed after 5 idle minutes or 50 m away.

## kiln.questmark (C)
```c
#include "kiln_questmark.h"   /* native/services/questmark/ */
const KilnQuestmarkApi* M = api->service((KilnStr){KILN_QUESTMARK_SERVICE, 14}, 1);
M->set(player, 1, x, y, z);   /* tick thread; applied in the next step */
```
`set(player, enabled, x, y, z)`: `KILN_ERR_NOT_FOUND` when the entity has no `PlayerWaypoint`, `KILN_ERR_UNAVAILABLE` when the
program is not running or the component's size is not 32 B. `counters(&requests, &written)`, `active()`.

## Commands
| Command | Who | |
|---|---|---|
| `/quests` | everyone | your chat quests with status and current step |
| `/quest <id>`, `/quest abandon <id>`, `/quest start <id>` | everyone | one quest; a quest with a giver starts through its NPC |
| `/talk`, `/talk <n>`, `/talk end` | everyone | a chat conversation with the NPC near you: answer, leave. An NPC in the game's menu answers "talk to them in the game" |
| `/questadmin list\|status\|reload\|state <who> [quest]\|start\|complete\|fail\|reset <who> <quest>\|step <who> <quest> <step>\|advance <who> <quest>\|signal <name> [who] [amount]` | admins, console | who: `world`, a player, `synthetic:<name>` |

The first three are the text fallback: the game's UI cannot reach a vanilla client, because a client builds it from the mod's files, so only a `"journal": "chat"` mod has players who need them. A native
quest's mod says `clients: "mod"`, and servers refuse players without it. `/journal` is the vanilla journal's admin command (Kiln.Admin).

## Kiln.Admin
| Op | Args | Answer |
|---|---|---|
| `modquests_status` | | counts, counters, `loadErrors`, `recentActions`, waypoint counters; `channel` stays false, `tracked` reads an old save's value |
| `modquests_list` | | `[{id, mod, name, scope, journal, main, auto, repeatable, giver, steps[{id, text, objectives[{kind, count}]}]}]` |
| `modquest_state` | `target`, `quest?` | one quest's `{status, canStart, progress}`, or every record `{tracked, quests[]}` |
| `modquest_set` | `target`, `quest`, `status` (`start`, `complete`, `fail`, `reset`, `advance`) or `step` | `{ok, reason, status}` |
| `modquest_signal` | `signal`, `target?`, `amount?` | `{signal, amount}` |
| `modquests_reload` | | `{fromFiles, total, loadErrors}` |

`quests_list` and `quest_state` are the vanilla journal's (knowledge.md).

## Testing
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/quest-engine-test/quest-engine-test.csproj -c Release -o $KILN_OUT/mods/quest-engine-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any; tools/lab/lab.sh start <lab>
grep -a quest-engine-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log      # boot 1: every objective, chat conversations, world quest, actions after a wait
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>                      # boot 2: the sidecar kept it; puts the world back
```
Delete `kiln/data/quest-engine-test/state.json` and copy the save back to start over. Run it without quest-native-test in the same lab: both use `QuestInput`.
The game's UI, places and creatures: [npcs.md](npcs.md) "To repeat".

## Lab results (no player, a clean copy of the lab save)
| Run | Result |
|---|---|
| `quest-engine-test`, boots 1 and 2 | 151 and 7 PASS, 0 FAIL |
| `quest-native-test`, 4 boots: first, restart, without the mod, mod back | 175, 59, 7, 5 PASS, 0 FAIL; one `[E` line, the deliberate "gave up" |
| `test_questbuild.py`, offline on the client's data | 16 tests OK |
| `character-test`, boots 1, 2, 3 (its quests are chat quests now) | 11, 29, 13 PASS, 0 FAIL |
| `kiln-manage` lint tests | 11 OK |

```
quest-engine-test: PASS actions after a wait run on the tick thread: True,True
quests: quest-native-test:lab_hunt step kill: at (3718.8, 843.0, 1445.3) (the voxel surface, 21 m from the nearest base zone)
quests: world quest-native-test:lab_hunt step kill: spawned Enemy_Vukah_Melee as 2594 at (3718.5, 843.0, 1442.8)
quest-native-test: PASS both are still there after 8 s: verified
quests: world quest-native-test:lab_hunt step kill: the game removed Enemy_Vukah_Melee 2594 at (3718.5, 843.0, 1442.8), 22 m from the nearest base zone
quests: quest-native-test:lab_hunt step kill, retry 1: at (3806.0, 778.5, 1546.8) (the voxel surface, 53 m from the nearest base zone)
quests: NPC quest-native-test:lab_placed: (3800.0, 832.0, 1434.0) is in or next to the base at (3800.0, 832.5, 1434.0): moved to (3718.2, 842.5, 1452.9)
quest-native-test: PASS round 2: the game's evaluator: the line's query went false
```
A test's "the game removed" is the test destroying them. Whether the game itself removes creatures at these places, and whether a joined client sees all of it, is what the checklist below is for.

### Why the first sample's mobs could not be found
It spawned them 18 m east of a player standing in their base: (3844.8, 841.5, 1435.5), inside the base zone (center 3800,832,1434, half-size 60), at a height from
the player's own position plus one, with no marker on them and no check that they existed. The game pacifies enemies in a base and may remove them. The step said "nearby", and nothing on the map
pointed at them. That form of spawn is gone from the sample, and a `spawn` action now settles on the ground outside bases too, unless it says `"exact": true`.

## Manual test
Everything below is what the lab cannot show. Tobin's menu, voice and journal entry were seen in a real client (joined to a lab), so these rows build on that.

```sh
export KILN_OUT=<build dir>
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any        # or only the client, to host; server and client must be the same build
dotnet build mods/samples/quests-sample/QuestsSample.csproj -c Release -o $KILN_OUT/mods/quests-sample --artifacts-path $KILN_OUT/dotnet
tools/client/client.sh backup-saves
tools/client/client.sh deploy --real --policy any --mod $KILN_OUT/mods/quests-sample
C=~/.local/share/Steam/steamapps/common/Enshrouded/kiln/control/console     # console commands: echo '<command>' > $C/x.cmd
tools/client/client.sh logs 3000 | grep -aE 'quests:|native:'                # the log rows below, when hosting
```
Play → **LOCAL** tab → slot 1 (a character copy) → host offline; kiln.log must show `saves: world loaded: slot 0x0002 (world1.ksd)`. Joined to a lab instead: the mod is needed on both, and the rows' log lines are in the lab's kiln.log.
Stand in your base when you spawn in: that is the case that failed. Offline hosting has no chat, so these rows use the game's UI and the console folder only.

| # | Do | You should see or hear | kiln.log |
|---|---|---|---|
| Q1 | Boot to the menu | nothing of Kiln's | `knowledge: native: 1 mod(s): … 4 quest(s), 1 NPC(s) in the roster, … <more than 0> text(s)`; no `defined in code` warning; no `native: quests-sample` error |
| Q2 | Spawn in | The game's new-quest notification, twice. No Kiln toast, no tracker of Kiln's on the right. F6 and F7 do nothing; the pause menu has no Quests entry | `ember_errand: started`, `world_watch: started`, `NPC quests-sample:warden: at (…) (the voxel surface, N m from the nearest base zone)`, `native: NPC quests-sample:warden placed`, 3 s later `verified` |
| Q3 | Open the journal's quest log | "The Warden's Errand" with its step "Find the Warden" and the paragraph; "The World Watch" with "Together, kill two Vukah" and a counter 0/2 | |
| Q4 | Set the Errand as the active quest in the journal | The game's tracked-quest line on the HUD says "Find the Warden" | |
| Q5 | Open the map, look at the compass | Marker "The Warden" at him, outside your base, investigation icon; plus the game's waypoint pin there. Note which of the two you see: a marker entity far from a client may not reach it, and that decides whether the pin stays on by default | `native: marker Kiln_Marker_quests-sample_ember_errand_meet placed`, `waypoint of …` |
| Q6 | Walk there, press the game's talk key at him | The game's NPC menu, title "The Warden"; his greeting with a game voice line; topics "Tell me an old story." and none of level 50. The step finishes: quest-updated notification, journal shows it done and "Kill the two Vukah" 0/2 with its marker. If only a topic finishes it, the client doesn't send the greeting: report it | `native: 1 hears the greeting of quests-sample:warden`, or `chose` on the first topic; `step meet done` |
| Q7 | Open the map again, go to "Vukah on the Ridge" | The marker is 50 to 90 m from the Warden, on open ground. Two Vukah stand there and fight you normally: not pacified, not in a wall, not in the air | `ember_errand step hunt: at (…) (the voxel surface, N m from the nearest base zone)`; twice `spawned Enemy_Vukah_Melee as …`; no `the game removed` |
| Q8 | Kill them | Journal and HUD count 1/2, 2/2; World Watch completes with the game's notification. Then +3 logs with the game's item popup; "Take the Warden's logs" shows 3/3 for about 3 s before it passes | `.hunt[0] Kill: 1 -> 2/2`, `world_watch: complete`, `give 3 x Material_T1_Wood_Logs` |
| Q9 | Put the logs in a chest, talk to him | "Give the Warden three logs" is active and its marker is on him, but **no "Here are your logs." topic** | |
| Q10 | Take the logs back, wait 2 s, talk again and pick it | The topic is back; "Good timber…"; logs -3; quest-complete notification; XP +500; +5 logs. Talk again: the topic is gone, and the journal shows it complete | `chose quests-sample:warden hello[1]`, `ember_errand: complete (1x)`, `rewards: xp 500`. **No** `must run on the tick thread` and no `FAILED` |
| Q11 | Pick "Any more work?" | New quest "One More Walk", marker "The Warden's Mark" 30 to 45 m from him; a new topic "I will not make that walk." | `second_errand: started` |
| Q12 | Pick "I will not make that walk." | The quest leaves the journal; "Any more work?" is back. Pick it again, walk to the mark | `second_errand: reset`, then `started`, `complete`, `rewards: xp 100` |
| Q13 | Pick "Tell me an old story." | The game's lore voice line (English, about 9 s) with the text; the topic "And what came after?" joins the list. Pick it, talk again: it is gone | `chose … hello[5]`, `chose … story[0]` |
| Q14 | Pick "Shall I count the logs at camp?", then pick up logs | "The Warden's Tally", from C#, in the same journal, its counter following what you carry; at 10/10 it completes, XP +50 | `warden_tally: started`, `complete` |
| Q15 | `echo 'questadmin step <character> quests-sample:ember_errand hunt' > $C/x.cmd`, then quit to menu and host the same slot | The errand is at the hunt; quests as you left them otherwise; one Warden, at the same place; two Vukah at the mark again; no notification for the quests already done. Talk to him: it works | In every hosted world of a process: `NPC quests-sample:warden is in the world`, after `saves: world loaded`. Never `was placed once in this world and is gone`. `spawned Enemy_Vukah_Melee`; no second `complete` |
| Q16 | LOCAL slot 2 (a second character), same world | The errand from the start in the journal, one Warden, the World Watch as the first character left it. Text language German: journal, marker names and his lines in German | `character e4bbc8ca-…`; `ember_errand: started` for that character |

To play it again: `echo 'questadmin reset <character> quests-sample:ember_errand' > $C/x.cmd`: the journal entry and its markers go, the auto quest starts again within a second.

A player without the mod: refused when joining a server that runs it (`clients: "mod"`). A `"journal": "chat"` mod, optional: join without Kiln: `[Quest] ...` chat lines, `/quests`, `/talk`, `/talk <n>`, `/talk end`, no journal.
