# Talents, progression, quests, NPCs, sounds

Wiki: [talents](https://frostdev.io/kiln/wiki/talents), [progression](https://frostdev.io/kiln/wiki/progression),
[quests](https://frostdev.io/kiln/wiki/quests), [NPCs](https://frostdev.io/kiln/wiki/npcs),
[sounds](https://frostdev.io/kiln/wiki/sounds). `kiln lint` checks every format here.

## Client evidence

The dated [status page](https://frostdev.io/kiln/wiki/status) is the source for what a player has seen. Do not infer a new
block form, party scope, joined-player behaviour, UI or sound check from a server pass. Report the specific unobserved
result as "needs a client check".

## Progression (`Player.Progression`, service Players; works, seen with vanilla clients)
`Read()`, `AddExperience(n, source)`, `SetLevel(n)` (cap 50), `GiveSkillPoints(n)` (up to 60), `Unlock(node, force)`,
`Lock(node, force)`, `Respec()`. They return `Task<ProgressionResult>`: refusals are `Ok = false`, only bad arguments throw.

## Talents (`skills/*.json`, service Skills)
Both ways need `"side": "both"`, `"clients": "mod"`. Comments allowed, trailing commas not. Template: `kiln new talents`.
```json
{ "format": 1,
  "gameTree": { "nodes": [
    { "guid": "<fixed>", "name": "mymod_ward", "class": "Mage", "like": "0xcfa5985b", "from": ["0xcfa5985b"],
      "offset": [170, -40], "cost": 1, "configs": { "0x0a0c87e7": 2 },
      "texts": { "en": { "name": "Ward", "description": "+2 Intelligence." } } } ] },
  "trees": [ {
    "id": "mymod:ember", "name": { "en": "Ember Arts" }, "points": { "start": 1, "perLevel": 1 },
    "menu": { "en": "Ember Arts" }, "key": "K", "respec": true,
    "nodes": [ { "id": "kindle", "name": { "en": "Kindle" }, "col": 1, "row": 0, "cost": 1, "maxLevel": 3,
                 "icon": "icons/kindle.png", "effects": [ { "attribute": "Intelligence", "amount": 1 } ] } ] } ] }
```
- `gameTree`: drawn by the game, paid with its points. Put nodes in the file: on a dedicated server `GameTree.Define` counts from the
  next start. `maxLevel` 0 to 7. Nodes past the game's caps are kept by Kiln (up to 4096), outside the character save.
- `trees`: Kiln's own screen. Points: `{"start", "perLevel"}`, `{"share": 0.5}`, `TalentPoints.Custom`. `col`/`row` 0 to 31, levels to
  99. `requires`, `requireAll`, `pointsSpent`, `level`.
- Effects: `{"attribute", "amount"}`, `{"gameNode": "0x…"}`, `{"program", "configs"}`, `{"buff", "refresh"}`, `{"ability"}`,
  `TalentEffect.Callback(apply, remove)`.
- C#: `Skills.Register(new TalentTree {...})`, `Skills.HolderOf(player)`, `Skills.Unlock/Lock/Respec/Grant/Points/State/Has`,
  `TalentUnlockingEvent` (cancel with a `Reason`), `TalentChangedEvent`. Chat: `/talents`.

## Quests (`quests/*.json`, service Quests)
Comments and trailing commas allowed.
```jsonc
{ "format": 1,
  "npcs": { "ilse": { "template": "NPC_Workshop_Blacksmith01", "dialogue": "ilse", "names": { "en": "Ilse" } } },
  "quests": { "crates": {
    "name": { "en": "The Lost Crates" }, "giver": "ilse", "requires": [ { "level": 8 } ],
    "steps": [ { "id": "hunt", "text": { "en": "Drive off the scavengers" },
                 "objective": { "kill": { "tag": "crates", "count": 6 } },
                 "spawn": { "template": "Enemy_Scavenger_Melee01", "count": 6, "tag": "crates", "near": "ilse", "distance": [60, 120] },
                 "marker": { "name": { "en": "The Old Road" } } } ],
    "rewards": [ { "xp": 1500 } ] } },
  "dialogues": { "ilse": { "start": "hello", "nodes": { "hello": {
    "text": { "en": "Scavengers took our crates." },
    "options": [ { "text": { "en": "I'll find them." }, "if": { "quest": "crates", "is": "available" },
                   "do": [ { "start": "crates" } ], "goto": "end" } ] } } } } }
```
- Scope: `player` (default), `party` (`partyRadius`), `world`.
- Objectives: `kill`, `collect`, `deliver`, `reach`, `talk`, `craft`, `build`, `knowledge`, `custom`.
- Conditions: `level`, `quest` + `is`, `step` + `is`, `item`, `knowledge`, `predicate`, `all`, `any`, `not`.
- Actions: `xp`, `item`, `take`, `start`, `complete`, `fail`, `reset`, `advance`, `spawn`, `despawn`, `teleport`, `chat`, `toast`, `waypoint`,
  `talk`, `knowledge`, `revoke`, `signal`, `call`.
- Clients: quests and dialogue are in the game's UI by default and need `mod` + `both`. For a mod vanilla clients join: `"journal": "chat"` and `"native": false`, with `none`.
- Places: step `site`, `spawn`, `reach.site`, `npcs.*.place` with `near` and `distance`: real ground, outside bases. Don't guess positions.
- C#: `Quests.Define`, `Quests.Start`, `Quests.Signal`, `Quests.Advance`, `Quests.BindNpc`, `Dialogues.Open`, `QuestHolder.Of(player)`.
  Events: `QuestStartedEvent`, `QuestStepEvent`, `QuestCompletedEvent`, `QuestFailedEvent` (cancelable), `QuestStateChangedEvent`.
- Test without a player: `QuestHolder.Synthetic("name")`; `/questadmin reload | state | start | step | advance`. `QuestInput` is a
  lab helper: dev.25+ marks it `[Experimental("KILNX001")]` and hides it (a lab-only project adds `KILNX001` to `NoWarn`).

## Quest and dialogue blocks (dev.20+)
Use `QuestBlock` / `DialogueBlock` or `quests` / `dialogues` inside `blocks/*.json`; `kiln new quest MyQuest` scaffolds one.
`NpcBlock.Talk` names a dialogue block. Item, NPC, status and loot-table fields can name blocks of this mod. Typed pieces:
`StepBlock`, `Goal.Kill(...)`, `Req.Level(8)`, `Do.Start("errand")`, `RewardBlock`.

```jsonc
{ "format": 1,
  "quests": { "errand": { "name": "Wood for Ilse", "giver": "ilse",
    "steps": [ { "id": "wood", "text": "Bring three logs to Ilse",
      "objective": { "deliver": { "item": "Material_T1_Wood_Logs", "count": 3, "to": "ilse" } } } ],
    "rewards": { "xp": 100 } } },
  "dialogues": { "ilse-talk": { "start": "hello", "nodes": { "hello": { "options": [
    { "text": "Do you need wood?", "if": { "quest": "errand", "is": "available" },
      "do": [ { "start": "errand" } ], "reply": "Three logs, please." } ] } } } },
  "npcs": { "ilse": { "name": "Ilse", "body": "villager", "talk": "ilse-talk" } } }
```

- `Blocks.Define` returns `QuestHandle`; use `.FullId` with `Quests.Start` / `StatusOf`. Keep block names and step ids fixed.
- Rewards are one bundle: `xp`, `items`, `status`, `knowledge`, `lootTable`; step rewards run before `onDone`. Inventory
  overflow lands at the player's feet. A world quest pays every player present from one loot roll.
- Blocks use the game's UI: `side: both`, `clients: mod`. For vanilla clients, keep `quests/*.json` with chat/non-native flags.
- JSON blocks enter native journal data at the first start. C# definitions are written to the native sidecar for the next
  start; ship the generated `quests/generated/<mod>.json` for first-start UI. Restart server and clients after changes.
- The block form has no top-level `knowledge`, option `set`, `toast` or `waypoint` action. Unknown keys fail. Native appearance,
  party scope and dialogue interaction still need the client checks listed on the wiki; server lowering is not visual proof.

## Friendly NPCs and native quests (same files)
`clone`, `roster`, and quests and dialogue in the game's UI need `"clients": "mod"`, `"side": "both"` (not `kiln`: the client
builds them from the mod's files at boot, so changes need a restart).
- `npcs.<id>.place`: `{near, distance}` or `{at}`, with `yaw`, `once`, `unlock`. Real ground outside bases (`inBase`, `exact` opt out). A game NPC's unlock is granted for that world and stays.
- `clone`: `{guid, name}`, fixed. `roster`: `{unlock, roamingRange, summonItem}`. Limit 64 roster NPCs, 35 are the game's.
- `vendor.recipes`: shared with the base's workshop.
- Dialogue: any `if` on any node (one knowledge check, the rest kept by the engine). `reply`, `once`, `close`, `hint`,
  `greetings`, `farewell`. Add options at the end of a node: positions feed the ids.
- `knowledge`: `{"name": {"scope": "world"}}`, resolved as `<mod>:<name>`.
- Villagers: `Villagers.Find/Of/List`, `.Teleport`, `.GoTo`, `.Stay`, `.Follow`, `VillagerStateEvent`.

## Sounds (`assets/cook.json`, service Audio)
`"side": "both"` or `"client"`, `"clients": "mod"`. Ship your audio and the recipe. Plain JSON: no comments. Template: `kiln new sound`.
```json
{ "format": 1, "tool": "soundcook", "out": "cooked", "guidNamespace": "<a uuid4, once>",
  "steps": [
    { "op": "sound", "name": "click", "src": "../audio/click.wav", "channels": "mono", "cue": { "scene": "Ui" } },
    { "op": "sound", "name": "boom_1", "src": "../audio/boom_1.ogg", "channels": "mono", "normalize": -23 },
    { "op": "cue", "name": "boom", "sounds": ["boom_1"], "positional": { "min": 3, "max": 60 } },
    { "op": "voiceover", "name": "hello", "fallback": "en", "languages": { "en": ["../audio/en/hello.wav"] },
      "subtitles": { "en": "Hello." } } ] }
```
- Sources: WAV, FLAC, Ogg, MP3, any rate. 3D sounds mono. 1 s or less starts at once.
- Overrides: `inputs` + `"guid": "@input"`, `"mode": "override"`. Voice slots: en, de, fr, es, zh; others use English; empty is silent.
- C#: `SoundId.Named(Context, "boom")`, `Sound.Play2D/PlayAt/PlayOn/PlayMusic/Stop/SetVolume`,
  `Voiceover.Play(VoiceoverId.Named(Context, "hello"), speaker)`, `Voiceover.Narrate`. Any thread. Server calls relay to Kiln clients.
- `Sound.Replace` isn't any-thread: call it from `Client.Post` or mod code.
