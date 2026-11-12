# Character data: per-character mod data, and what travels with a character

Mods keep data per character, the way the game does: a world keeps its own record of each character, and a character brings its
own progress with it. Evidence for everything below: [`docs/re/characters.md`](../../re/characters.md).

| Layer | File |
|---|---|
| Symbols | `sigdb/symbols/server.d/characters.json` (feature `characters.id`, constant `characters/rec.character`) |
| Native | `native/services/characters/` → `kiln/services/kiln-characters.dll` (`kiln.characters` v1, `kiln_characters.h`) |
| Managed identity | `managed/services/Kiln.Players/PlayerCharacter.cs`: `Player.CharacterId`, `PlayerCharacters` |
| Managed data | `managed/services/Kiln.Saves/Characters.cs`: `CharacterKey`, `CharacterData`, `CharacterData.Carried`, events, `CharacterSettings` |
| Transport | `managed/services/Kiln.Saves/CharacterSync.cs`: channel `kiln:character` v1, the sidecar |
| Users | `Kiln.Quests` (`EngineCharacters.cs`), `Kiln.Skills` (`TreeRuntime.cs`, `GameShadow.cs`) |
| Test | `mods/tests/character-test/` (mod + `charnet.py`) |

## The game's model, and Kiln's
| | The game | Kiln |
|---|---|---|
| Identity | `CharacterId`, a GUID in the character file, sent in the login message, kept at player record +0x50 | `Player.CharacterId`, `CharacterKey(account, id)` |
| A world's record of a character | `known_users`: account → up to 10 characters → base, respawn point | `CharacterData` |
| Shared world progress | `world_knowledge` | `WorldData`, and the game's world knowledge |
| A character's own progress | `player_character`, `player_knowledge` in the client's file; goes up at join, comes back at each world save | `CharacterData.Carried`, sidecar in the client's Kiln folder; offered at join, comes back at each world save |

The world save holds **no** per-player progress (`docs/re/quests.md` section 4 said it did; `docs/re/characters.md` section 4 has the correction).

## What travels and what stays

| Data | Where it lives | Travels? |
|---|---|---|
| Kiln quests (`QuestBook`), player and party scope | this world, this character (`CharacterData`, "book") | **no**: quests belong to the world |
| Kiln quests, world scope | this world (`WorldData`) | no |
| The game's world knowledge | the world save | no (the game's) |
| The game's player knowledge, native quests' player mirror | the character's save | yes, by the game. A join clears a native quest's mirror when this world's book has no such quest |
| Native skill nodes (up to the game's caps) | the character's save | yes, by the game |
| Overflow ("shadow") nodes | `CharacterData.Carried`, "gameShadow" | **yes** |
| Kiln talent trees | `CharacterData.Carried`, "talents:&lt;tree&gt;" | **yes** |
| Your mod's data | your choice: `CharacterData` or `CharacterData.Carried` | your choice |
| Your mod's data on one item in the character's inventory (`InstanceData`, `Persist.World`) | this world's record of the character (`character-instances/<key>/<mod>`) | **no**: the item's data stays with this world |
| The same with `Persist.Carried` | (would be `CharacterData.Carried`) | not available in this release |
| `WorldData`, `PlayerData`, `EntityData` | this world | no. `EntityData` on a player entity is `PlayerData`: per Steam id, not per character |

So one character on two worlds has separate quests and the same talents, and two characters on one world have separate everything.

## C#
```csharp
using Kiln;

var who = CharacterData.KeyOf(player.Entity);                 // CharacterKey: None for an unknown player
var world = CharacterData.Get<Standing>(who, "standing");     // this world's record of this character
var bag = CharacterData.Carried.Get<Perks>(who, "perks");     // goes with the character
bag.Unlocked.Add("ember");                                    // a live object: change it, Kiln saves it

[Subscribe] void OnImported(CharacterImportedEvent e)          // a client's copy came in: check it
{
    if (CharacterData.Carried.TryGet<Perks>(e.Character, "perks", out var p)) p.Unlocked.RemoveWhere(x => !Known(x));
}

// data your mod kept per Steam id before characters: moves once, to the first character that asks
CharacterData.MigrateFromPlayerData(who, "standing", player.SteamId, player.AccountId);
```

| Member | Threads | Notes |
|---|---|---|
| `Player.CharacterId` | any | `Guid.Empty` when unknown. Stays readable after the player left, until the slot changes hands |
| `PlayerCharacters.Available`, `Status`, `IdOf`, `AccountOf` | any | the native service |
| `CharacterKey(account, id)` | | `.IsNone`, `.IsAccountOnly`, `.Text`, `TryParse` |
| `CharacterData.KeyOf(entity)`, `LastOf(playerId)` | any / tick | `LastOf`: the character a Steam or account id last played in this world |
| `CharacterData.Get<T>(key or entity, k)`, `TryGet`, `Set`, `Remove`, `Contains`, `Keys`, `Characters`, `MigrateFromPlayerData` | tick, mod code | stays on this world. Same rules as `WorldData`: keys per calling mod, JSON, live objects, saved with the world |
| `CharacterData.Carried.*` the same, and `Revision(key)` | tick, mod code | goes with the character. Client-side and editable: see below |
| `CharacterData.Status` | any | `Sessions`, `Imports`, `Refused`, `Kept`, `Dropped`, `Updates`, `SidecarWrites` |
| `Skills.HolderOf(characterKey, legacySteamId?)`, `TalentHolder.Character`; `QuestHolder.OfCharacter(key, legacyAccount?)`, `QuestHolder.Character` | tick | the services' holders. A holder with only a Steam id or account means the character it last played here, else the data from before characters |

| Event | When | Thread | Cancel |
|---|---|---|---|
| `CharacterImportingEvent {Character, Player, Revision, ServerRevision, Mods, Reason}` | a client offered newer carried data, before anything is applied. Not when the setting refuses | tick | refuses the whole offer; the server keeps its copy, the client keeps its file |
| `CharacterImportedEvent {Character, Player, Revision, Applied, Kept}` | the data is in place. Check it here | tick | no |

## A character's items
Data a mod keeps on one item (`sword.Get(Kills)`, [saves.md "Instance data"](saves.md#instance-data-values-that-stay-with-one-item))
follows the item, and an item in a player's inventory belongs to the character:

- **In this world's record** (`Persist.World`, the default): at each world save the items with data in a player's
  inventory are written into the scope `character-instances/<character key>/<mod>` of the world's generation, with the
  inventory's whole list of items (the witness). `CharacterData.Keys` and `CharacterData.Carried.Keys` do not list it.
- **At a join** the records are matched once the character's inventory is initialized: a character that comes back
  exactly as it left gets every record back in its own slot, twins included; a character that comes back changed (played
  elsewhere, its file edited) gets back only what is certain (the one item that fits), and the rest is set aside and
  reported, never guessed.
- **At a leave** the item entities are destroyed with the character going back to its client; the records wait with the
  character at their last slots (`InstanceDataParkedEvent`, `PlayerLeft`), with no limit in sessions while the character
  stays away. An item trashed while the player is still in game is dropped (`Destroyed`).
- **In `Carried`** (data that travels to other worlds with the character): not available yet.
  `InstanceData.Key(name, new() { Persist = Persist.Carried })` throws `KilnUnavailableException("saves.instance_data.carried")`:
  `Persist.World` keeps the value on this world's save, the item in a player's inventory included.
- A character without an id on this build (account-only key) keeps its items' data for the session only.

What a real client must still confirm (the guided client checks in `mods/tests/instances-test/ClientChecks.cs`; none
has run): that a character's inventory comes back at a join with the same flat slots and record fields, and the order
of the leave save, the items' destruction and the leave event. A lab with simulated players shows only that a
character's records wait across a leave, a rejoin in another slot and a restart, and attach to nothing (a simulated
player has no inventory).

## When the mod list moves a skill node
Native nodes live in the character's save, shadow nodes in `Carried`. A mod list that moves a node across keeps its unlock and level both ways: [skills.md](skills.md) "When the mod list moves a node". What the join check moved out of the save stays through a client's older carried copy coming in.

## How a character travels
Channel `kiln:character` v1 (not required: a client without it just doesn't take part). JSON; ids in this order: 0 `CharHello`, 1 `CharOffer`, 2 `CharResult`, 3 `CharUpdate`,
4 `CharAck`, 5 and 6 lab peers only.

1. The player is in game (about a second later). The server sends `CharHello {Character, World, Imports, MaxBytes, Revision}`.
2. The client answers with `CharOffer {Character, Revision, Mods}` from `kiln/data/characters/<id>.json`. One offer per session; an offer for another character is refused.
3. **The newest revision wins.** Client newer: its copy replaces the server's, after the checks below. Equal or older: the server's copy stays, and the client gets what it lacks.
4. At each world save (autosave every 30 s, a player leaving, `Saves.Request`, shutdown), the server diffs the character's carried data against what the client has. A change makes a new
   revision and a `CharUpdate`. The client merges it into the file (keys the update doesn't name stay), keeps the previous file as `.prev`, and answers `CharAck`. What isn't acknowledged goes again at the next save.
5. Leaving: the game saves when a player leaves and its client waits for its character (`docs/re/characters.md` section 4), so the update arrives in that window. A last push runs when the session closes.

- **In a hosted game** this machine's player has no messages: the same code reads and writes the sidecar directly. Other players who joined that game use the channel.
- **Vanilla clients** never get a hello. Their data stays on the server, per character, at revision 0. If they later come with a Kiln client that has a newer file, the file wins.
- **Why not the game's path**: `player_knowledge` drops ids the server's static map lacks at each load, so a character passing through a server without the mod would lose them; the character file's block types are a fixed enum and never reach a server; skill nodes past the caps break vanilla servers. Details in `docs/re/characters.md` section 6.
- **Why the file isn't next to the character save**: nothing may be written into the game's save folders (the game's migration scan moves unknown files, Steam cloud owns the other set). The name carries the CharacterId, so it still finds its character.

## What a server checks
A client's copy is treated as the game treats a character: client-side and editable. So don't put anything in `Carried` that a server must trust.

| Check | Effect |
|---|---|
| Setting `importProgress: false` | nothing taken, nothing sent; the client's file stays as it is; this server keeps its own copy per character |
| Over `maxImportBytes`, `maxKeys`, or `maxCharactersPerAccount` new characters | the whole offer is refused; nothing changes on either side |
| A value over `maxValueBytes`, a broken key, a mod id with other characters than letters, digits, `.`, `_`, `-`, `:` | that entry is dropped from what the server takes |
| A mod not loaded here | its data is **kept, not applied**: stored, passed on, read by nobody |
| A value that doesn't read as its type (`Get` throws `InvalidDataException`) | your mod decides. Kiln.Skills removes it, and the removal reaches the client's file |
| Talent trees (Kiln.Skills, at `CharacterImportedEvent`) | levels cut to `MaxLevel`, nodes whose requirements don't hold removed, a tree that costs more than the character has reset for free (as the game does at login). Nodes and trees this server doesn't have stay, not applied |
| Shadow nodes | levels of nodes this tree doesn't have aren't loaded and stay in the data. A set that costs more than the character has is reset for free, and only those nodes: the game's login check judges the native tree alone (skills.md). `Granted` points are taken as they are: use `importProgress: false` where that matters |

`kiln/config/characters.json` (server, read at each join; a file that doesn't parse refuses imports until it is fixed):
```json
{
  "importProgress": true,            // false: refuse imported progress, and leave clients' copies alone
  "maxImportBytes": 204800,
  "maxValueBytes": 65536,
  "maxKeys": 256,
  "maxCharactersPerAccount": 32
}
```

## The move from account-keyed data
Kiln.Quests and Kiln.Skills kept `book`, `gameShadow` and `talents:<tree>` per Steam id, else account id, else host key 1.

- The first character of the account that loads a key takes it: the entry **moves** (`Kiln.Saves: ... the account-wide data of <id> now belongs to character <guid> (migrated once ...)`). Later characters start without it.
- Moved, never copied, and in the same world store: a crash before the next save gives the old generation, with the data where it was. Nothing can end up on two characters.
- Per world: another world's old data moves when a character first loads it there. If the character arrives with a carried `talents:<tree>` from its file, the file's copy stands, and that world's old entry stays for the next character without one.
- An offline `QuestHolder.OfAccount(id)` or `new TalentHolder(steamId, EntityId.None)` follows `CharacterData.LastOf`: the character that id last played here.

## Limits
- **A copy is a new character.** The game's copy to another slot or storage tab gives a new id, so it starts without the original's mod data, on every world. To keep it, copy `kiln/data/characters/<old id>.json` to `<new id>.json` with the game closed.
- **An unknown build** (the signature doesn't match): `CharacterId` is empty, keys are account-only (`CharacterKey.IsAccountOnly`), characters of one account share their data as before, nothing travels. kiln.log says so once. This is the most stable derivation without the game's id.
- **The account is in the key**, as in `known_users`: a character whose account key changed starts over on that world, except what its file brings.
- **The file and the character save aren't one transaction.** Restoring an old character save leaves the file at its newer revision. The launcher's save backups don't include `kiln/data/characters/`.
- **Restoring an old world** brings back its quests and its copy of the carried data; a character with a newer file overrides that copy at its next join.
- **Revisions count changes, not history.** A character played as a vanilla client on a server, then joining there with a Kiln client whose file is newer, loses that server-only progress (the world store's older generations still hold it).
- `maxImportBytes` also caps what the server sends. Changes over it stay on the server (error in kiln.log).
- A server you join can write your file, like it writes your character save; only `.prev` keeps the step before.

## C (`kiln_characters.h`)
```c
#include "kiln_characters.h"   /* native/services/characters/ */
const KilnCharactersApi* C = api->service((KilnStr){KILN_CHARACTERS_SERVICE, 15}, 1);   /* in on_ready */
KilnCharacterInfo i = {sizeof i};
if (C->by_handle(player.handle, &i) == KILN_OK) { /* i.id (16 bytes, the save's byte order), i.account */ }
```
`status`, `by_handle`, `by_slot`: any thread, from a snapshot taken each server-world step. `simulate(slot, account, id)`: labs only, `KILN_CHARACTERS_SIMULATE=1`.

## Testing
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/character-test/CharacterTest.csproj -c Release -o $KILN_OUT/mods/character-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any
echo '{"labPort": <P>}' > ~/srv/enshrouded/labs/<lab>/kiln/config/network.json
tools/lab/lab.sh start <lab>            # boot 1: writes account-keyed data the old way
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>   # boot 2: the migration, two characters on one account
tools/lab/lab.sh stop <lab>; tools/lab/lab.sh start <lab>   # boot 3: everything came back
mods/tests/character-test/charnet.py --port <P> --lab <lab dir> --sidecar <scratch file> --phase first      # the channel, checks, the setting
# restart the lab: --phase restart. Move kiln/data/worlds aside (another server) and restart: --phase other. Put it back and restart: --phase back
# as a hosted game's local player: --phase hosted1; delete kiln/data/worlds, restart: --phase hosted2
KILN_PLAYERS_SIMULATE='kiln\mods\character-test\fixtures\two-characters.log' KILN_CHARACTERS_SIMULATE=1 tools/lab/lab.sh start <lab>   # on a clean store
```
`kiln/data/character-test/boot.txt` counts the boots (delete it and `kiln/data/worlds` to start over; write 0 for a boot 1 or 2 for a boot 3 when you swap stores). The simulated
run replays one Steam account as two players (slots 0, 1) playing two characters, through the real join and leave events.

## Lab results (no client)
```
characters: CharacterId at record +0x50, account at +0x48, 16 records of 0x148 bytes
character-test: PASS kiln.characters found the CharacterId at record +0x50 (... Resolved = True, Records = True, Known = 0, RecordOffset = 80 })
Kiln.Saves: kiln-quests key 'book': the account-wide data of 8502796096475496449 now belongs to character aaaaaaaa-… (migrated once; ...)
character-test: PASS migration: the first character got the account's quest where it stood (character-test:errand: Active at two)
character-test: PASS migration: the second character of the account has no such quest
character-test: PASS two characters: separate progress
characters: lab:lab-1 character aaaaaaaa-…a: took its carried data, revision 2 -> 3: applied [character-test, kiln-skills], kept unread [], dropped []
```
| Run | Result |
|---|---|
| boots 1, 2, 3 | 11, 29, 13 PASS, 0 FAIL |
| simulated players | 14 PASS, 0 FAIL |
| `charnet.py` first, restart, other, back, hosted1, hosted2 | 37, 8, 10, 4, 8, 6 PASS, 0 FAIL |
| regressions | skills-test 38 then 32; quest-engine-test 143 then 8; questnet.py 30; players-test 30; quest-native-test 56 then 39 (its other 6 checks need the sample mod deployed); saves-test 27 then 7 alone, its kill -9 phases not run (with other test mods in the lab its "first run: no mod data yet" check failed, most likely because their saves ran first: `Commit` sets the match to Exact); 0 FAIL otherwise |

## Manual test
Everything below is what the lab cannot show: a real record, a real client, the screens.

### Setup (game closed)
```sh
export KILN_OUT=<build dir>
tools/client/client.sh backup-saves
dotnet build mods/tests/character-test/CharacterTest.csproj -c Release -o $KILN_OUT/mods/character-test --artifacts-path $KILN_OUT/dotnet
dotnet build mods/samples/SkillTreeSample/SkillTreeSample.csproj -c Release -o $KILN_OUT/mods/skilltreesample --artifacts-path $KILN_OUT/dotnet
dotnet build mods/samples/quests-sample/QuestsSample.csproj -c Release -o $KILN_OUT/mods/quests-sample --artifacts-path $KILN_OUT/dotnet
tools/client/client.sh deploy --real --policy any --mod $KILN_OUT/mods/character-test --mod $KILN_OUT/mods/skilltreesample --mod $KILN_OUT/mods/quests-sample
```
LOCAL tab only; stop if kiln.log shows a world slot `0x01xx`. Client log: `tools/client/client.sh logs 3000 | grep -E 'characters:|account-wide|saves: world loaded'`.

### A. Two characters on one hosted world: separate quests and talents
Play → **LOCAL** tab → slot 1 (a character copy) → world1 → Start → Offline.

| # | Do | See | kiln.log |
|---|---|---|---|
| A1 | Spawn in | | `saves: world loaded: slot 0x0002 (world1.ksd)`; `characters: slot 0 handle 0x…: character <id A>, account <account>`; `characters: <name> character <id A> in game (this machine's player)` |
| A2 | First time with old data | your old quests and talents are still there | `the account-wide data of <id> now belongs to character <id A>`, once per key |
| A3 | F6 → the sample's quest; advance it a step (Q2..Q5 of `services/quests.md`) | the quest moves on | `character:<account>.<idA> quests-sample:ember_errand: step meet done` |
| A4 | Esc → Ember Arts → Unlock Kindle | Kindle 1/3, Intelligence +1 | `skills: skilltreesample:ember: … character <id A> Unlock kindle 0 -> 1` |
| A5 | Wait 30 s or quit to menu | | the game folder has `kiln/data/characters/<idA>.json`: `"kiln-skills"` with `talents:skilltreesample:ember`, no `kiln-quests` |
| A6 | Quit to menu. LOCAL slot 2 (a second character) → the **same** world1 → Start | The game's journal: the errand from the start, not at the first character's step; Ember Arts: Kindle 0/3, Intelligence as the second character's own | `character <id B>`; no second "account-wide" line |
| A7 | Advance the errand one step; unlock Kindle | only the second character's state moves | `character:<account>.<idB> quests-sample:ember_errand: step meet done`, `… character <id B> Unlock kindle 0 -> 1` |
| A8 | Quit. LOCAL slot 1, world1 | the first character's quest and Kindle as they were left | `its sidecar (revision N): the server's copy is as new or newer` |
| A9 | World quest (the sample's World Watch) | one state for both characters: same count on both | `world quests-sample:world_watch` |

### B. One character from a hosted world to a lab: same talents, separate quests
```sh
tools/lab/lab.sh start character-data     # UDP 26390; it has the same mods after: tools/deploy-server.sh ~/srv/enshrouded/labs/character-data --policy any
jq -r '.userGroups[] | select(.name=="Admin").password' ~/srv/enshrouded/labs/character-data/enshrouded_server.json
```
| # | Do | See | Lab kiln.log | Client kiln.log |
|---|---|---|---|---|
| B1 | Join "Kiln lab character-data" as the first LOCAL character with that password | | `characters: slot N handle …: character <id A>`, `in game; imports allowed`, then `took its carried data, revision 0 -> N: applied [kiln-skills]` | `characters: joined as character <id A>: offering revision N` |
| B2 | Esc → Ember Arts | Kindle 1/3, Intelligence +1: the talents came along | | |
| B3 | The game's journal | the errand from the start: this world's quests are its own | no `quests-sample:ember_errand` from before | |
| B4 | Unlock Kindle's second level; advance the errand one step; wait 30 s | | `update revision N+1`, debug level | the file's `revision` is N+1 |
| B5 | Leave the lab. Host world1 again with that character | Kindle 2/3 (from the lab); the errand where **world1** left it (A3), not the lab's | `its sidecar (revision N+1): taken` | |
| B6 | Lab: put `{"importProgress": false}` in `kiln/config/characters.json`, rejoin | Kindle as the lab last had it, your file unchanged (compare `revision`) | `imports refused here` | `this server keeps its own copy` |
| B7 | Optional: rejoin without Kiln (play-once vanilla), with `importProgress` back on | talents per `/talents`, kept per character on the lab | `in game` with no `took` | |

### C. Native quests across worlds (`"journal": "native"`)
With `mods/samples` native quest data on both sides: start a native quest on world1, then join the lab with that character: the game's journal must not show it there. Lab kiln.log: `… knowledge id(s) from another world cleared: no record of this quest in this world`. Back on world1 it is shown again (the mirror runs from the book).

### Afterwards (game closed)
`tools/lab/lab.sh stop character-data`; `tools/client/client.sh undeploy --real`, or redeploy your usual set. The test's sidecars are in the client's `kiln/data/characters/`; delete them to start over.
