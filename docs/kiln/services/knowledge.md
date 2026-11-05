# kiln.knowledge: the game's knowledge, the vanilla journal, dialogue events, the client veto

| Layer | File |
|---|---|
| C table | `native/services/quests/kiln_knowledge.h` (`kiln.knowledge` v1, published by kiln-quests.dll) |
| Native | `native/services/quests/knowledge.cpp` (reads, changes, events, holds, settings), `catalog.cpp` (names, NPC roster, journal, query db) |
| Symbols | `sigdb/symbols/server.d/quests.json` (features `quests.knowledge`, `quests.changes`, `quests.journal`, `quests.npc`, `quests.dialogue`, `quests.client_input`), `sigdb/symbols/client.d/quests.json` (overlay) |
| Managed | `managed/services/Kiln.Quests/Knowledge*.cs` (`Kiln.Knowledge`, `Kiln.Journal`, events, `KnowledgeSettings`) |
| Admin | `mods/Kiln.Admin/Ops.Knowledge.cs` (ops + `/knowledge`, `/quests`) |
| Settings | `kiln/config/knowledge.json` (Kiln's config "knowledge"; security checks, both off) |
| Test mod | `mods/tests/quests-test/` |
| Evidence | `docs/re/quests.md` (Q§), `docs/re/creatures.md` §7 (dialogue) |

This is the base of the quest engine (Q1): every vanilla quest step, NPC rescue, dialogue line "heard" and unlock is a
knowledge id, so a mod that can grant, read, revoke and watch knowledge can drive vanilla quests and NPCs with vanilla
clients, and its own quest engine can react to dialogue.

## What the game has, in short
- **Knowledge** is a map id -> value: one **world map** (shared progress) and one **player map** per player slot
  (personal), held by the server (`G38Knowledge` -> its registry), saved in the world save, replicated to every
  client. Values: Bool 0/1, Counter n, Flags bit mask. Only ids listed in `GameKnowledgeResource` exist (5,825 world
  objects, 10,744 player objects on 1287369); 1,690 world objects have a **player twin** with the same id.
- Programs grant knowledge by pushing `{id, type, value, player context}` into `KnowledgeTriggerQueue`;
  `update_knowledge_system_server` (a barrier program) applies it once per step: the world object if the id has one,
  then the player object of the context's player; XP for player objects with `experienceGainId`; query triggers;
  replication. The queue **only raises** values.
- The **journal** (188 quests, 406 steps) is a view over knowledge: a step shows while its visibility query holds and
  is done when its completion query holds; the game pays the quest's XP when the last step is done and sets the
  quest's own player Bool (`entryId`, "rewarded").
- **NPCs**: 28 of the 35 `NpcCollection` entries have an unlock requirement, a world Bool (18 workshop NPCs, 10 villagers;
  the 6 `test_*` and the deprecated one have none and always read unlocked); the NPC registry follows the knowledge
  (`npc_unlock_state_server`).
- **Dialogue**: the client builds and filters the menu; the server gets `{line id, NPC}` and runs that line's
  knowledge without checking anything (C§7.3). `TriggerKnowledgeAction` (player input) pushes any operation a client
  sends.

## How Kiln does it
- **Reads** (tick thread): the maps with the game's own lookup (`KnowledgeIndex`), the static objects with the game's
  `StaticWorldFind` / `StaticPlayerFind`, queries with the game's evaluator (`QueryEval`, as `trigger_quest_rewards`).
- **Changes**: a detour on the game's queue apply (`KnowledgeApplyQueue` 0x9a0830, called once per step inside
  `update_knowledge_system_server`). After the game's own queue, Kiln's requests run in submit order:
  - **grant** = the game's per-operation apply `KnowledgeApplyOp` (0x9a0670) on `{id, type, value, context}`, exactly
    what the queue does: XP, analytics, change list, version, query triggers and replication are the game's;
  - **revoke / set** = a direct write of the map entry, the change-list entry (`ChangeListAppend` 0x99fa50) and the
    version, the three things the apply writes (0x9a0670 / 0x9a4a80). A client erases an id it receives with value 0
    (Q§4); the save writes non-zero values only, so a revoked id is gone after a restart. New entries go through the
    game's store (`BlockStore` 0x9a08a0, the open hash map's find-or-insert).
  - Without the detour (feature `quests.changes` off) grants fall back to a queue push from Kiln's program and
    revoke / set answer unavailable.
- **Change events**: the change lists of the step are read in Post and diffed against Kiln's shadow of each map, so
  changes made after the queue apply in the same step (query triggers call `KnowledgeApplyOp` directly, 0xa2ed6e) count
  too. Loads are not events: the world's shadow is filled at the world's first knowledge update (the save is parsed
  by then), a player's when the block goes valid (empty) and again at the game's mark of a character's record. `0x9a1110`
  writes the record straight into the map (no change list, no version bump) and raises a flag at `block.valid + 1`. The
  game's knowledge update takes the flag down in its step (`0x9a48e0`): it clears the change list and leaves one entry,
  byte 12 = 2, with a version bump, so the whole map goes to the client. That entry is the baseline: the flag is already
  down when Kiln's Post runs (the first version of this read the flag, and every real join reported the record as gains:
  978 `changed` lines for one character).
- **While the world loads**: `update_knowledge_system_server` does not run until the world runs and the maps read empty.
  Changes submitted before that wait for its first run (up to ~10 minutes) instead of failing after 50 steps;
  `Status.Applying` tells. Read after `ServerStartedEvent`.
- **Program `kiln_knowledge`** (kiln.world, SYSTEM, host only, events_in `ServerSelectDialogOptionEvent`, singletons
  read `G38Knowledge`, write `KnowledgeTriggerQueue`): the dialogue select / stop events; the fallback grant push.
- **Holds** (security, listeners): two mid hooks, both only active when needed:
  - before the `KnowledgeQueuePush` of `handle_knowledge_trigger_input` (0x1bc216): the client's operation is
    recorded; with the veto it is neutralised in place (id 0: the game drops unknown ids and still consumes the input);
  - after `FindDialogLine` in `unlock_knowledge_from_dialog_option_input` (0x1fb478): the line's operations and its
    "heard" id are recorded and the game's push is skipped (rax = 0).
  A held entry is decided in Post: by a listener (`decide`) or its default; allowed operations are pushed into the
  game's queue (applied in the next step, with the player's context, as the game would have).
- **Names**: built from the game's data at the world's first steps: `NpcCollection` (via `NpcRegistry`),
  `JournalRegistryResource` (via `g38_journal::JournalRegistry`), `GameKnowledgeQueryResourceDb` (via kiln.assets),
  dialogue lines (via kiln.gamedata, on demand). Texts are loca tags (client data): the server has no quest names.
- **Fail soft**: six sigdb features; a missing part answers `KILN_ERR_UNAVAILABLE`; with `quests.knowledge` off the
  service is not published. Every call into the game and every read of game memory is SEH-guarded.

## Names
| Name | Means |
|---|---|
| `0x1234abcd`, `305441741` | that knowledge id |
| `npc:<debugName>`, `npc:<debugName>.unlock` | the NPC's unlock requirement (`npc:NPC_Workshop_Bard01.unlock` = world Bool `0xff6000e2`) |
| `quest:0x<entryId>` | the quest's "rewarded" player Bool (`quest:0x8755c335`, "Alchemist Needs Shelter") |
| `quest:0x<entryId>.step<n>.visible` / `.done` | step n's visibility / completion requirement (knowledge if Simple*, else a query) |
| `query:<name>` | a named query of the query db (5,296; `query:NPC_Alchemist_Hint01_HAS_Shelter_Journal`) |
| `line:<template>/<option\|init\|transition\|farewell><k>` | dialogue line k of that list in the template's `NpcDialog` (its id is its "heard" knowledge) |
Names are case-insensitive. A query name resolves to a query (`IsQuery`): it can be evaluated, not granted.
`Journal.Quests()` and `Knowledge.Catalog(...)` list the catalog.

## C#
Reference Kiln.Quests (and Kiln.Players for `Player`) as other services:
`<ProjectReference Include=".../managed/services/Kiln.Quests/Kiln.Quests.csproj" Private="false" ExcludeAssets="runtime" />`.

```csharp
using Kiln;

// unlock a vanilla NPC for everyone (world knowledge), read it back, take it away again
var r = await Knowledge.World.Grant("npc:NPC_Workshop_Bard01.unlock");   // r.Before 0, r.After 1
bool unlocked = Knowledge.World.Has("npc:NPC_Workshop_Bard01");          // the NPC's unlock requirement
await Knowledge.World.Revoke("npc:NPC_Workshop_Bard01.unlock");

// a player's knowledge (while they are on the server)
await Knowledge.Grant(player, "quest:0x8755c335.step1.visible");          // shows the step in their journal
var s = Knowledge.For(player).Get(0x88b160c1);                            // KnowledgeState, null if not a player object
foreach (var q in Journal.States(player.Entity)) if (q.Started) Log.Info(q.ToString());

[Subscribe] void OnChanged(KnowledgeChangedEvent e) => Log.Info(e.ToString());     // "world 0xff6000e2 (npc:…): 0 -> 1"
[Subscribe] void OnLine(DialogueLineEvent e) { if (e.Action == DialogueAction.Start) Log.Info($"{e.Player?.Name} picked 0x{e.LineId:x8}"); }
[Subscribe] void OnLineKnowledge(DialogueKnowledgeEvent e) { if (e.LineId == 0x1234abcd) e.Cancel(); }  // keep a vanilla quest from starting
```

| Member | Threads | Notes |
|---|---|---|
| `Knowledge.Available`, `Status` | any | `KnowledgeStatus`: the six parts, the program, sizes, catalog counts, counters, `QueueMax`, `Reason`, `Applying` (the game's knowledge update has run in this world: the save's knowledge is in the maps) |
| `Knowledge.World`, `Knowledge.For(player)` | any | `KnowledgeScope` of the world map / a player's map |
| `scope.Get(id\|name)` | tick | `KnowledgeState {Target, Id, Value, Type, Present, NewMask, Step}`; null when the id is not an object of that kind of target; `NotSupportedException` when the player is not in game |
| `scope.Has(id\|name)` | tick | value != 0; a name with a requirement (NPC unlock, quest step, `query:`) is evaluated with the game's evaluator |
| `scope.List()` | tick | every set id of the map |
| `scope.Grant(id\|name, value = 1)` | any (names: tick) | the game's apply: Bool set, Counter + value, Flags flag number `value`. Player grant of a twin also sets the world object (game rule). XP: the game's |
| `scope.Revoke(id\|name, flag = -1)` | any (names: tick) | value 0 (Flags: one flag, -1 all). Direct write; clients erase it; not saved. That id only: what the game already did because of it stays (see "Lab") |
| `scope.Set(id\|name, value)` | any (names: tick) | exact value, direct write (no XP, no trigger event). The state keeps the save's mark, so the game's journal does not notify for it: docs/re/quests.md "Notifications" |
| `Knowledge.Grant/Revoke/Has/Get(player, name)` | as above | shortcuts |
| `Knowledge.Info(id)` | any | `KnowledgeInfo`: world / player / twin, types, expected flags, XP id, skill points |
| `Knowledge.Query(target, query, mode)`, `TryQuery` | tick | the game's evaluator; `target` 0 = world (player queries throw `ArgumentException`); `KnowledgeMode.Shared/Player/Personal` |
| `Knowledge.Resolve(name)`, `TryResolve`, `NameOf(id, query)`, `TryParseId` | tick (raw ids: any) | names above; `KnowledgeName {Text, IsQuery, Id, Requirement}` |
| `Knowledge.Catalog(KnowledgeCatalog.Npcs\|Quests\|Queries)` | tick | the catalog's names (all 35 NPCs; the 7 without an unlock have `Id` 0 and do not resolve) |
| `Knowledge.Npcs`, `FindNpc(name)` | tick | `KnowledgeNpc {DebugName, NpcId, Unlock, Unlocked (NpcRegistry), Workshop, SummonItem}` |
| `Knowledge.Submit(op, target, id, value)` | any | the raw request |
| `Knowledge.Security.Current / Apply(settings, save) / Allow(ids)` | any (save: tick) | the checks below |
| `Journal.Quests()`, `Find(name\|id)` | tick | `JournalQuest {EntryId, NameTag, Source, Type, IsMain, IsTutorial, Steps, RewardExperience, RewardItems}`, `JournalStep {Visible, Done, MapMarkerTag, ...}` |
| `Journal.State(target, quest)`, `States(target)` | tick | `JournalQuestState {Steps (Visible?, Done?), Started, Completed?, Rewarded?}`; target 0 = world (player queries: null) |

Changes return `Task<KnowledgeResult>` (`Ok`, `Status`, `Before`, `After`, `Changed`, `Steps`, `Reason`, `Request`):
applied in the next server-world step inside the game's knowledge update, completed on the tick thread right after
(~40-80 ms; while the world loads, at its first knowledge update). Game reasons (player left, already set: `Changed` false) come back in the result; unknown ids, missing
flags, query names and bad arguments throw.

### Events (posted on the tick thread, one step after the game's step; built only with subscribers)
| Event | When | Cancel / result |
|---|---|---|
| `KnowledgeChangedEvent {Target, IsWorld, Player, Id, Name, OldValue, NewValue, Type, Request, ByKiln, Gained, Lost, Step}` | a value changed in the world map or a player map (game or Kiln; `Request` = the Kiln request) | not cancelable (revoke to undo) |
| `DialogueLineEvent {PlayerEntity, Player, Npc, LineId, Action Start\|Stop, IsFarewell (0), IsMissingRequirement (1), Step}` | the game's `ServerSelectDialogOptionEvent` (a line picked / stopped) | not cancelable |
| `DialogueKnowledgeEvent {PlayerEntity, Npc, LineId, Operations, RequirementFailed, TooFar, CheckError, Distance, ChecksOn, DefaultApplies}` | a line's knowledge is held: while someone subscribes (that is the opt-in; Kiln holds every line's knowledge for up to 8 steps, normally 1) or with `checkDialogue` | Cancel / `Deny()` drops it; `Allow()` runs it even if a check failed; default: runs unless `checkDialogue` and a check failed |
| `KnowledgeRequestEvent {PlayerEntity, Player, Id, Name, Type, Value, Held, Step}` | a client sent `TriggerKnowledgeAction` | only with `vetoClientKnowledge` (`Held`): `Allow()` applies it, Deny / Cancel / default drop it. Without the veto it already went through (report only) |

## C
```c
#include "kiln_knowledge.h"   /* native/services/quests/ */
const KilnKnowledgeApi* K = api->service((KilnStr){KILN_KNOWLEDGE_SERVICE, 14}, 1);
KilnKnowledgeName n = {sizeof n};
if (K && K->resolve((KilnStr){"npc:NPC_Workshop_Bard01.unlock", 30}, &n) == KILN_OK) {   /* tick thread */
    uint64_t req; K->grant(owner, 0 /* world */, n.id, 1, &req);           /* or submit() with a callback */
}
KilnKnowledgeState s = {sizeof s};
K->get(0, 0xff6000e2, &s);                                                  /* s.value, s.type, s.present */
uint8_t holds; KilnKnowledgeQuery q = n.query; K->query(player, &q, KILN_KNOW_MODE_PLAYER, &holds);
```
| Function | Threads | Notes |
|---|---|---|
| `status` | any | `KilnKnowledgeStatus` (296 B) |
| `info(id)` | any | `KilnKnowledgeInfo`; NOT_FOUND: unknown id |
| `get(target, id)`, `list(target, out, cap, &count)` | tick | NOT_FOUND: not an object of that target; UNAVAILABLE: no world / player not loaded |
| `submit(owner, &request, &id)`, `grant`, `revoke`, `result(id)` | any | `KilnKnowledgeRequest {op GRANT/REVOKE/SET, target, id, value, callback}`; the callback runs on the tick thread (Post of the apply step); the last 256 results are kept |
| `query(target, &q, mode, &out)` | tick | the game's evaluator; INVALID_ARGUMENT: a player query for the world |
| `resolve(name)`, `name_of(id, kind)`, `catalog_count/at`, `journal_quest/step`, `npc` | tick | catalog |
| `events(after, out, cap, &n)`, `last_seq` | any | ring of 4,096 `KilnKnowledgeEvent` (64 B): CHANGED, DIALOGUE, CLIENT, DIALOGUE_HELD, RESOLVED |
| `held_ops(held)`, `decide(held, ALLOW/DENY/DEFAULT)`, `listen(kinds)` | any | holds (Kiln.Quests' pump uses them) |
| `settings`, `configure`, `allow(ids, n)` | any | security settings (configure lasts until the file changes) |

## Security (opt-in; `kiln/config/knowledge.json`)
```jsonc
{
  "vetoClientKnowledge": false,   // hold and drop knowledge the clients send (TriggerKnowledgeAction) unless allowed
  "allowClientKnowledge": [],     // ids or names that pass the veto
  "checkDialogue": false,         // run a dialogue line's knowledge only if its requirement holds and the player is near
  "dialogueRange": 12.0,          // metres (0: no distance check)
  "logClientKnowledge": true      // log each distinct client-sent id once
}
```
The native service reads the file at boot and within ~4 s of a change (no managed mod needed); Kiln.Quests registers
it as Kiln's config "knowledge" (comments, launcher schema). `Knowledge.Security.Apply` changes it at run time.
- **The gap** (C§7.3, Q§2.5): a modified client can unlock any knowledge (quests, NPCs, recipes, achievements' inputs)
  by sending `TriggerKnowledgeAction`, and can run any dialogue line's knowledge of any NPC from anywhere by sending
  its id. The vanilla game trusts both.
- **vetoClientKnowledge risk**: the **vanilla client sends some knowledge itself** (tutorial and UI progress is the
  expected case; which ids is client checklist K4). With the veto on and those ids not allowed, that progress is
  dropped: tutorial hints may repeat or tutorial journal entries never complete. Turn on `logClientKnowledge`, play
  the normal paths once, put every logged id into `allowClientKnowledge`, then enable the veto.
- **checkDialogue risk**: a line whose requirement the server evaluates differently from the client (personal
  progression edge cases, runtime queries the server sees later) loses its knowledge; the knowledge arrives one step
  later than without the check. The NPC's answer (animation, texts) is the client's and is not affected.

## New knowledge ids for mods
Built: a mod declares them in `quests/*.json` (`"knowledge"`) and the boot build rebuilds `GameKnowledgeResource`; see
[npcs.md](npcs.md). Way 1 of the old design held: every lookup reads `*(*(registry + 8) + 0x10)`, which is the resource service's copy,
and a kiln.assets override registered right after the service starts reaches it. Names: `<mod>:<name>` (`Knowledge.Resolve`), ids:
`NativeIds.Knowledge(mod, name)`. A server-only mod can use them with vanilla clients, which store ids they do not know and use none of them (Q§4).

## Kiln.Admin
| Op | Args | Answer |
|---|---|---|
| `knowledge_status` | | parts, sizes, catalog counts, counters |
| `knowledge_get` | `target` ("world", entity or name), `id`/`name` | `{state {value, set, type, present}, info, holds (requirement)}` |
| `knowledge_list` | `target` | `{count, entries [{id, value, name}]}` |
| `knowledge_grant` / `knowledge_revoke` / `knowledge_set` | `target`, `id`/`name`, `value` / `flag` / `value` | result `{ok, status, before, after, changed, steps, reason}` |
| `knowledge_resolve` | `name` | `{id, query, requirement, catalogName}` |
| `knowledge_npcs` | | `[{name, npcId, unlock, unlocked, worldKnowledge}]` |
| `knowledge_security` / `knowledge_security_set` | `vetoClientKnowledge?`, `checkDialogue?`, `dialogueRange?`, `logClientKnowledge?`, `save?` | the settings |
| `quests_list` | | `[{name, entryId, source, type, main, steps}]` |
| `quest_state` | `target`, `quest` | `{started, completed, rewarded, steps [{visible, done, ...}]}` |

Chat (Admin): `/knowledge <world|player> get|grant|revoke|count [id or name] [value|flag]`, `/journal <world|player> [quest:0x…]`.

## Lab
Server 26430253 (`f0d824be…`), three runs from a clean copy of the lab save (`world0.ksd`
revision 3006), restarted with `lab.sh stop` / `start`; the shutdown save wrote revisions 3007, 3008, 3009. No player
connected: everything on a player's map is client checklist K1-K4.

Start-up and catalog:
```
knowledge: layout: knowledge on, in-step apply on (revoke/set, ordered grants, change events), journal on, NPC registry on, dialogue on, client input on
knowledge: program kiln_knowledge accepted (dialogue events slot 3, queue slot 2)
knowledge: GameKnowledgeResource 0000000064253AA0 (registry data 00000000897FFF70 + 0x10) is the resource service's copy of cb1eeb8b (0000000064253AA0, 917240 bytes)
knowledge: catalog: 35 NPCs (28 with an unlock requirement; NpcRegistry: 0 unlocked, version 0), 188 quests (406 steps), 5296 named queries, 4477 names
knowledge: first knowledge update of this world at step 272: world map 566 entries (the save's knowledge, the change events' baseline)
quests-test: PASS the save's load fired no change events (0 game changes so far, 567 entries in the map)
quests-test: PASS a grant submitted while the world loads (step 41) waited 231 steps for the first knowledge update
```
Persistence (`NPC_Workshop_Bard01`, world Bool `0xff6000e2`):
```
run 1  quests-test: PASS run 1: NPC_Workshop_Bard01's unlock 0xff6000e2 is not set in this world
       quests-test: changed: world 0xff6000e2 (npc:NPC_Workshop_Bard01.unlock): 0 -> 1 (Kiln request 8)
       quests-test: PASS run 1: NpcRegistry reports NPC_Workshop_Bard01 unlocked (npc_unlock_state_server followed the knowledge)
       quests-test: done: 53 PASS, 0 FAIL
run 2  knowledge: first knowledge update of this world at step 272: world map 568 entries (the save's knowledge, the change events' baseline)
       quests-test: PASS run 2: 0xff6000e2 (NPC_Workshop_Bard01) is still set after the restart (the world save kept it)
       quests-test: PASS run 2: NpcRegistry reports NPC_Workshop_Bard01 unlocked after the restart
       quests-test: changed: world 0xff6000e2 (npc:NPC_Workshop_Bard01.unlock): 1 -> 0 (Kiln request 8)
       quests-test: PASS run 2: NpcRegistry reports NPC_Workshop_Bard01 locked again
       quests-test: done: 54 PASS, 0 FAIL
run 3  knowledge: first knowledge update of this world at step 272: world map 567 entries (the save's knowledge, the change events' baseline)
       quests-test: PASS run 3: 0xff6000e2 (NPC_Workshop_Bard01) is absent after the restart (the save left it out)
       quests-test: PASS run 3: NpcRegistry reports NPC_Workshop_Bard01 locked after the restart
       quests-test: done: 51 PASS, 0 FAIL
```
Also measured:
- **A revoke undoes that id only.** The game answers the Bard's unlock by setting world Bool `0x08055951` itself (53 ms
  and 558 ms later in two runs, `changed: world 0x08055951: 0 -> 1`, no Kiln request; it stayed unset for 30 s on a world
  without the grant). It
  stays set after the revoke and after the restart, which is why run 3's map has 567 entries, not 566. This is also
  the lab's evidence that the game's own changes reach `KnowledgeChangedEvent`.
- **Settings file**: after a run-time configure, editing `knowledge.json` applied within ~4 s (`knowledge: settings:
  vetoClientKnowledge ON, ... 2 allowed ids`, one a name, one an id); `knowledge_security_set` without `save` left the file
  alone, with `save` wrote it.
- **Kiln.Admin**: `tools/nettest/kaptest.py`: 37 PASS, 0 FAIL (144 of 145 ops equal over TCP and the file bridge,
  `admin_token_rotate` skipped by design). By hand: status, resolve, get, list (`knowledge_list` 568), `knowledge_npcs`
  35, `quests_list` 188, `quest_state`, grant and revoke by id and by name; an unknown id and an unknown player refuse.

Fixed because of these runs (the first boot ended 36 PASS, 13 FAIL):
- the test ran before the world did (11 FAILs); 28 NPCs have an unlock, not 29, an error in this document and the test (2);
- the save's load fired every entry as a change (`changesSeen: 568` then; 8 now: 7 Kiln changes and the game's one);
- changes submitted during the load failed after 50 steps (they now wait);
- a run-time configure stopped the file from being re-read for the rest of the session, which contradicted this document;
- from reading `0x9a1110`: a joining player's record did not reach the shadow (a later revoke of a loaded id would have
  read 0 -> 0 and fired nothing). **Not run yet: needs a client (K1, K2).**

Not verified here: all player maps, journal display, dialogue events and holds, the client veto, replication of revoked
ids to a client. The dedicated server is the only side run: the client overlay `client.d/quests.json` resolves
(`sigdb_check.py`) but has not run in a hosted world.

## Testing
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/quests-test -c Release -o $KILN_OUT/mods/quests-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any; tools/lab/lab.sh start <lab>
grep quests-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log    # offline checks, then persistence run 1
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>            # run 2 (still set, then revoked); again: run 3
touch ~/srv/enshrouded/labs/<lab>/kiln/control/quests-test.reset    # start the persistence runs over
echo grant > ~/srv/enshrouded/labs/<lab>/kiln/control/quests-test.live   # with a player: K1 (revoke: K2)
```
The offline part plays a joining client's record on a free player block (`Knowledge.LabRecord`: the login's activation, then the map and the record flag; the game's own update does the rest): "a character's record is the baseline: no change events", then one real change for a revoke and one for a new id. Before the fix this run reported every entry of the record as gained. Without a player the live file logs `FAIL live: no player in game` (expected). The persistence runs change the lab's
world: copy the save back and touch the reset file to repeat them from clean.

## Manual test (K1-K4)
A lab `knowledge` (Admin password in its `enshrouded_server.json`), a LOCAL character copy. Setup:
`KILN_OUT=<build dir> tools/deploy-server.sh ~/srv/enshrouded/labs/knowledge --policy any --samples`, then
`tools/lab/lab.sh start knowledge`. On join the log has `knowledge: player N: knowledge map 0 set entries`, about 3.5 s later `knowledge: player N: knowledge map M set entries from the character's record`, and no flood of changes.
- **K1 grant:** `echo grant > ~/srv/enshrouded/labs/knowledge/kiln/control/quests-test.live`: a new quest in the journal's Quests
  tab within a second; log `PASS live: grant`, `PASS live: the server's journal state follows`. If the log passes and the journal
  shows nothing, try `echo "grant quest:0xc2fbdab1" > …/quests-test.live` (the first may be hidden).
- **K2 revoke:** `echo revoke > …/quests-test.live`: the quest disappears; `changed: … 1 -> 0`. Grant, leave, rejoin: no flood, still
  there. Revoke: `changed: … 1 -> 0` must fire (untested fix). Leave and rejoin: stays gone.
- **K3 dialogue:** talk to Balthazar, pick 2 options, leave: the NPC answers normally; log `quests-test: dialogue: player N Start line …`
  per pick, `dialogue knowledge: … default applies`.
- **K4 veto:** play normal UI paths, note `client knowledge from player N: 0x…` ids. Set `vetoClientKnowledge` true in
  `kiln/config/knowledge.json`, repeat, note anything that stops working. Set `checkDialogue` true, talk nearby: `default applies`,
  no `too far`. Set both back to false.
