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
Both ways need `"side": "both"`, `"clients": "mod"`. Comments allowed, trailing commas not. Template: `kiln new talents`. dev.28+: talents can be staged in `OnBoot`
([boot.md](boot.md)); dev.29+ a game-tree node's `Icon` must be a file inside the mod's folder.
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
  next start (dev.27+: Kiln.Sdk bakes them into the mod at build, so they count from the first). `maxLevel` 0 to 7. Nodes past the game's caps are kept by Kiln (up to 4096), outside the character save.
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
  lab helper: dev.26+ marks it `[Experimental("KILNX001")]` and hides it (a lab-only project adds `KILNX001` to `NoWarn`).

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
  start; dev.27+ Kiln.Sdk bakes them into `quests/generated/<mod>.json` at build (`docs/kiln/modding/baked.md`), and that
  file wins at start until you rebuild. Restart server and clients after changes.
- The block form has no top-level `knowledge`, option `set`, `toast` or `waypoint` action. Unknown keys fail. Native appearance,
  party scope and dialogue interaction still need the client checks listed on the wiki; server lowering is not visual proof.

## Friendly NPCs and native quests (same files)
`clone`, `roster`, and quests and dialogue in the game's UI need `"clients": "mod"`, `"side": "both"` (not `kiln`: the client
builds them from the mod's files at boot, so changes need a restart).
- `npcs.<id>.place`: `{near, distance}` or `{at}`, with `yaw`, `once`, `unlock`. Real ground outside bases (`inBase`, `exact` opt out). A game NPC's unlock is granted for that world and stays.
- `clone`: `{guid, name}`, fixed. `roster`: `{unlock, roamingRange, summonItem}`. Roster limit: 128 entries, 35 of them
  the game's, so 93 for all mods together (dev.29+; 64 before). A roster this game cannot hold stops the start (exit code
  0x4B02). Past 64 entries is verified on dedicated servers only. Update Kiln on the server and every player's game before
  adding a mod with more than 29 roster NPCs: an older Kiln loses their progress when it saves. A mod's NPCs are ordered by
  key name. `NativeQuests.GetUnlock`, `GetRosterBudget` read unlock states and room (any thread).
- `vendor.recipes`: shared with the base's workshop.
- Dialogue: any `if` on any node (one knowledge check, the rest kept by the engine). `reply`, `once`, `close`, `hint`,
  `greetings`, `farewell`. Add options at the end of a node: positions feed the ids.
- `knowledge`: `{"name": {"scope": "world"}}`, resolved as `<mod>:<name>`.
- Villagers: `Villagers.Find/Of/List`, `.Teleport`, `.GoTo`, `.Stay`, `.Follow`, `VillagerStateEvent`.

## NPC speech bubbles (Kiln.Npcs, dev.31.16+, mod API 0.2.23)
Your text in the game's own speech bubble over a villager, craftsperson or assistant; the host decides, Kiln clients
show it, vanilla clients keep the game's lines. `"side": "server"`, `"clients": "none"` is enough (players need Kiln, not the mod).
```csharp
Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(6));          // once, now, no voice; 0.5-60 s; UiText for languages
var s = Npcs.Speech(npc);                                           // this mod's override of that NPC
s.Replace("No food, no work.", "My stomach...");                    // greets with these (1-32 lines, <= 200 chars), no voice
s.Suppress();                                                       // or: no greeting bubble
s.Clear();                                                          // the game's lines again (also on unload / world end)
```
- Several mods: highest `Priority`, then the most recent. A Say shows over any override; a newer Say replaces it
  (`SayOptions(Interrupt: false)` waits for another mod's). `Npcs.SpeechOf`, `IsSaying`, `CanSpeak` (tick), `Available`.
- A mod on both sides decides on the host (`Lifecycle.IsServerRunning`); a joined client's own calls change only its screen.
- Never guard the calls with `Npcs.Available` / `Capabilities.Is("npcspeech.bubble")`: both are false on a dedicated
  server (no bubbles of its own), which still sends; guarded, no player sees anything. The calls never throw for it.
- Not changed: the talk gesture when a player presses E. Tests: `KilnHarness.NpcSpeech` (`ModeOf`, `LinesOf`,
  `Saying`). Sample `HungryCrafter`. `services/npc-speech.md`.

### NPC dialogue (dev.32: picks, farewells, the menu's line, talk pages)
```csharp
s.Pick = SpeechPick.InOrder;                                        // Random (default: new line each greeting), InOrder, Seeded(n), By(ctx => i)
Npcs.Farewells(npc).Replace("Come back with food.");                // the menu's farewell (no Suppress)
var m = Npcs.MenuLine(npc);                                         // the NPC's own line when its menu opens
m.Pick = SpeechPick.By(ctx => Fed(ctx.Player) ? -1 : 0);            // per player on the host, one open ahead; -1 = none
m.Replace("I can't work like this."); m.Refresh();                  // Refresh: ask again for every player now
Npcs.MenuSay(npc, player, "Thanks!");                               // one line in that player's menu
Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.") // rows after the NPC's own (Ramone gets a page)
    { Reply = "Bless you.", Hint = NpcTopicHint.Attention, OnPick = ctx => { Feed(ctx.Npc); ctx.Reply("..."); } });
```
- `By` and `OnPick` run on the host's tick thread as your mod; Random/InOrder/Seeded are picked by each player's game.
- Kiln 0.2.0-dev.32 on the player's game (channel `kiln:npcdialog`); 31.16-31.21 clients keep the v1 greetings only.
- 16 topics per NPC (all mods); a topic's `Close` needs a `Reply`. Tests: `PickOf`, `FarewellsOf`, `MenuLinesOf`,
  `TopicsOf`, `RepliesTo`, `PickTopic(npc, topicId, player)`.

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
