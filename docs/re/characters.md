# Characters: how the game tells them apart and keeps their progress (svn 1287369)

Research for `docs/kiln/services/character-data.md`. Builds: dedicated server Steam 26430253 (`f0d824be…`, image base
0x140000000, RVAs below), client 26430625 (`5d9ef2f1…`, RVAs in brackets), both svn 1287369. Sources:

- **Saves**: read-only copies of the LOCAL set (`character0.ksd`, `character1.ksd`, `world0..2.ksd`, `global.ksd`, the rolling
  `backup/` copies), the character files of the Steam set (only their identity blocks), the lab's `world0.ksd` (a dedicated
  server's). Container format from the MIT save editor (`launcher/src/editor/shared/sv2c-web.mjs`, `knowledge.mjs`,
  `character.mjs`). Reader in scratch only.
- **Code**: Ghidra 12.1.2 headless (no analysis, functions created on demand) and capstone over read-only copies of both exes;
  reflection through `tools/kfc types`.
- **Data**: `tools/kfc decode` of `JournalRegistryResource` and `GameKnowledgeResource`.
- **Runtime**: lab `character-data` (UDP 26390, fake peers). No client was run: everything about a real join is **[dis]**,
  with a manual test in the service doc.

Tags: **[refl]** the game's reflection, **[save]** decoded save files, **[data]** decoded game data, **[dis]** read in the code
at the RVA given, **[rt]** seen in the lab, **[hyp]** hypothesis.

## Short answers

| Question | Answer | Tag |
|---|---|---|
| Does a character have an identity? | Yes: `keen::CharacterId`, a GUID. It is in the SV2C header of `character<N>.ksd`, in its `character_data` block, in the world's `known_users`, and in the join message. | refl, save, dis |
| What does the server see? | `GameMessageLoginRequest.characterId` at login, kept in the player record at **+0x50**. The account key is at +0x48. | dis |
| How stable is it? | One GUID for a character's whole life: a Steam `character0` keeps its id over revisions 1..2619 across 13 days and a rename. A copy made with the game's copy function gets a **new** GUID (the LOCAL copies of both characters differ from the Steam originals). | save |
| Where do a character's progress and knowledge live? | In the **client's character file**: `player_character` (level, XP, skill nodes, inventory...), `player_knowledge` (id → state), `fogofwar`, `voiceover`. The client uploads it at join; the server sends it back at each world save and finally when the player leaves. | save, dis |
| Does the world save hold PlayerKnowledge records per slot? | **No.** `docs/re/quests.md` section 4 is wrong on this: no world file has such a block (4 files, a dedicated server's among them). "[server] Failed to save PlayerKnowledge" is the step that copies each player's knowledge into that player's outgoing **client save**. | save, dis |
| What does a world keep per character? | Only `known_users`: account → up to 10 `KnownCharacter {characterId, baseId, lastRespawnPoint, lastVisitTimeStamp}`. | refl, save, dis |
| What about the world's quest progress? | `world_knowledge` in the world file, one map for all characters. In the default shared mode a quest step reads the world's object when one exists (section 5). | save, data, dis |
| What happens when a second character of an account enters a world? | It logs in with its own id and its own save. The known-user lookup finds the account but not the character, so it gets the default spawn; at its first spawn-info change it takes a free slot of the 10 (else the oldest is evicted). Its own player knowledge came with it; world knowledge is whatever the world has. | dis, save |

What matters: **a quest's world progress is per world and server-side**, in the world's own
knowledge. What a character brings is the player side: its skills and its player knowledge, including each quest's "rewarded" flag.

## 1. The character file **[save]**

SV2C container: `"SV2C"`, `+0x0C` a GUID, `+0x1C` revision, `+0x20` UTC seconds, then a block directory. For a character file the
header GUID **is** the CharacterId (all 35 files and backups read agree).

| Block | Type id (S2BH +8) | Payload (two characters) | What |
|---|---|---|---|
| `character_data` | 0x0a | 132 / 71 B | a flag byte, a hash, name (u32 length + UTF-8), **CharacterId (16 B)**, zeros, time; the slot's list entry |
| `player_character` | 0x09 | 876 / 201 B | appearance, `+0x3a` level, `+0x3e` XP, `+0x42` skill node count, `(u32 id, u8 level)` nodes, equipment, inventory (save editor `character.mjs`) |
| `voiceover` | 0x14 | 388 / absent | played voice lines |
| `player_knowledge` | 0x0f | 7224 / 392 B | `u32 state, u32 n, n × u32 id, n × u32 value`: 902 / 48 entries |
| `fogofwar` | 0x10 | 1,638,413 B raw | the map |

Block types form a fixed enum with names in a table (`0x12edbb0..`: `character_data`, `player_character`, `fogofwar`,
`player_knowledge`, `known_users`...), so an unknown block has no type the game would write back **[dis]**. Comparison of the
a LOCAL copy with its Steam original (revision 2619): 902 vs 901 knowledge entries, same 36 quest ids: a copy has its own id and
the same progress.

`global.ksd` block `known_players` = `ClientKnownPlayerEntry {playerId, lastPlayed, ...}`: the client's mute list. Its `playerId`
values are the same numbers as `known_users` account keys **[refl] [save]**. The key is not the plain
SteamID64, nor FNV-1a, xxh64, xxh3, MD5, SHA-1 or SHA-256 of the obvious encodings of it (tried offline); the reflected name is
`accountIdHash`.

## 2. The world's `known_users` **[refl] [save]**

```
KnownUsers { version; users: BlobArray<KnownUserData> }
KnownUserData 504 B { knownCharacters: StaticArray<KnownCharacter,10> +0; accountIdHash u64 +0x1e0; groupId +0x1e8; lastVisitTimeStamp +0x1f0 }
KnownCharacter 48 B { characterId +0; baseId +0x10; lastRespawnPoint UniqueSceneId +0x20; lastVisitTimeStamp +0x28 }
```
On disk (block type 0x0d, no compression): `u32 count`, then per user `u64 account, u32 group, u64 time, u32 10`, then 10 ×
`GUID, u32 respawn, GUID base, u64 time` (44 B). Decoded, names kept to the id prefix:

```
world1.ksd (hosted, LOCAL) <world>, revision 2135
  acct <A> group <g1> last <t>
     char <c1> respawn <r1> base <b> last <t>     character 1, Steam original
     char <c2> respawn 00000000 base <b> last <t>     character 2, Steam original
     char <c3> respawn 00000000 base <b> last <t>     a LOCAL copy of character 1, since replaced
     char <c4> respawn 00000000 base <b> last <t>     the LOCAL copy now (character0.ksd)
  acct <B> group <g2> last <t>
     char <c5> respawn <r1> base <b> last <t>
  acct 0000000000000000 group <g2> last <t>
     char <c5> respawn 00000000 base <b> last <t>
labworld0.ksd (dedicated server) <world2>, revision 3006
  acct <A>: char <c1>   acct <B>: char <c5>   acct 0: char <c5>
```
**Four characters of one account on one world, each with its own record**. The same
character can sit under two accounts: the world was hosted on another machine, where the host's account read 0 at the time. Kiln's
key follows the game (account + character), so such a character starts over on its side, as the game's spawn data does.

## 3. The join and the identity on the server **[dis]**

`GameMessageLoginRequest` (0x140 B) is described by a table the code reads generically (descriptor `.data` `0x14ad030`, fields at
`0x14aceb0`, reader `0x9a77e0`; the handler's error is "[server] Invalid login message"):

| Field | Offset | Present flag | Notes |
|---|---|---|---|
| `type` | +0 | | `GameMessageLoginType`: Login, SystemsReady, GameplayReady, Logout |
| `username` | +8 | length +4, max 0x100 | what the record shows as the name: the platform name, not the character's |
| `characterId` | +0x10c | +0x108 | |
| `inputToggleConfig` | +0x128 | +0x120 | record +0x138 |
| `buildVersion` | +0x134 | +0x130 | checked against GameServer +0x17c8 |
| `isNewCharacter` | +0x139 | +0x138 | record +0x140 |

The client fills it in [`0x84d880`] from the selected character slot (`0xde3ab0`'s result): `characterId` = slot `+0x120` (16 B),
`isNewCharacter` = slot `+0x150` bit 0, every present flag 1, `buildVersion` from the client object's `+0x174`, then the generic writer `0x11f6420` with the same
descriptor.

- Dispatcher `0x7bdb20`, machine message type 0 → AddPlayer `0x7af1c0(server, machine, msg)` → `0x7abbe0` → record setup `0x7ac690(server, handle,
  machine, &username, &msg.characterId)`:
  ```
  7ac709  mov rax,[rsp+0x80]        ; &characterId
  7ac711  movups xmm0,[rax]
  7ac714  mov [rsi+0x18],r15d       ; entity = 0
  7ac718  movups [rsi+0x50],xmm0    ; record +0x50 = characterId
  ```
  The same bytes on the servers 1273043, 1285601, 1287369 and the client. `sigdb/symbols/server.d/characters.json` captures the
  offset (`characters/rec.character`). AddPlayer sets the account (`rec+0x48`, from the session's player info).
- `0x7aded0` (the spawn point of a player): `0x7b0720(knownUsers, &out, &rec.account)` finds or creates the account's entry and
  stamps its visit time; it then walks the 10 characters comparing 16 bytes with `rec+0x50`. A match copies the base id and respawn
  point to `rec+0x60`/`+0x70`. No match, or `isNewCharacter`: the default spawn.
- `0x7aedc0` (consumes `ecs::PlayerSpawnInfoChangeEvent {playerCharacterId +8, baseId +0x18, sceneInstanceId +0x28}` **[refl]**): for a
  record whose `+0x50` equals the event's id, updates the record and the world's entry: the character's slot, else the first free
  one, else it clears the oldest (smallest `lastVisitTimeStamp`) and takes that **[dis]**. The eviction loop is `0x7ba690` too.

**[rt]** (on an empty lab) `characters: CharacterId at record +0x50, account at +0x48, 16 records of 0x148 bytes`; kiln.characters reads
all 16 records with `Known = 0`. A real id in a record needs a client: manual test.

## 4. The character's way in and back **[dis]**

Save2System holds, per player slot (stride 0x5620, pointer table at `+0x56cf0`), a replication object `R` and a save buffer (`0x702430` =
`slot × 0x5620 + 0x5b70`). `R`: `+8` the handle, `+0xc` state (0 none, 1 waits for the client's save, 2 idle, 3 sending, 4 waits for ack, 5 final
ack received, 6 cancelled), `+0x18` entity blob, `+0x98` knowledge, `+0x100` hash, `+0x110` receive buffer, `+0x140` flags.

1. **Login** `0x7ac690` → `0x70cfd0(save2, handle)`: `R.state = 1`.
2. **Upload**: the client sends the character (string "CharacterSaveGameMessage" [`0x15215e8`]; "SyncCharacterSaveGame" [state in `0x849ff0`];
   "[client] Send Character Failed", "[save2] Sending savegame to server timed out."). Server: player channel, dispatcher case → `0x7144a0`
   → `0x713f60`: message byte 1 = a chunk `{u32 hash, u32 total, u32 offset, u16 len, bytes}`, reassembled; FNV-1a-32 over the whole must equal `hash`.
3. **Parse** `0x714540`: `u8 compressed`, then `u8`, `u32` size + the entity blob (into `R+0x18`), `u32` size (≤ 0x157c4 = 88,004) + a sub-stream: `u32
   n` (< 0x2af9 = 11,001), `n` ids, `n` values (`R+0xa0`, `+0xc8`). Validation `0x73d760`: "too many entities" (0x40000), "too many family pages" (0x6a5), "too many knowledge
   pages" (0x2800), "too many inventory pages" (0x10000).
4. **Knowledge loads** into the slot's block; `0x9a1960` drops every id that the static player map lacks, once (`0x99fdb0`; `quests.md` section 4). That is what
   rules knowledge out as a carrier for a mod's data: a server without the mod's object deletes the id, and its next save sends the character back without it.
5. **At each world save** `0x7c3aa0`: for each of the 16 slots `0x9a2fb0(out, 0x702430(save2, slot), knowledge, slot)` ("[server] Failed to save PlayerKnowledge for %k") writes
   the slot's knowledge into the slot's save buffer; state 5 of the save machine calls `0x73b750`, which for each slot with a save `setPlayerSave`s it into `R`. So
   the character goes back about every 30 s. The player's entity data comes from the ECS snapshot (`player_character`).
6. **Back**: `0x73ad80`: state 3 sends chunks of 700 B `{u8 0, u32 version, u32 hash, bool final, u32 total, u32 offset, u16 len, bytes}`; state 4 waits for the ack (message
   byte 0, flags byte: 1 resend, 2 final request, 4 + u32 version the ack) with a 5 s timeout.
7. **Leaving**: the client's message with flag 2 → `0x7bdb20`: record state 3 → 4, and `requestServerSave`. "[save2] Server sent final client save for %k." then "[save2] Received
   final savegame ack from %k". The client waits for it: "[client] Server did not send an updated Character Save Game upon Exit".

Those are the points a Kiln transport mirrors: an offer at join, updates at each world save (including the one a leaving player causes, while the client still waits).

**The server holds no character between sessions.** Nothing about a character but `known_users` reaches the world file. The blocks of a world, at most these 12:
`world_metadata`, `game_settings`, `base00`, `entity_chunk00`, `snapping`, `miscellaneous`, `world_knowledge`, `custom_strings`, `weather`, `logic`,
`chunk_persistent_data`, `known_users` **[save]** (world1; the lab's dedicated world has no `custom_strings`). (Third-party server tools keep copies by hooking `setPlayerSave`: the save editor's "guardian" mode.)

## 5. Quests: what is per world, what is per character **[data] [save]**

188 quests with their steps' `knowledgeRequirement` and `completionRequirement`. Of the requirements that name an object directly (the rest, 556, are `Extern` named queries):

| Object the step names | Requirements | Read in shared mode from |
|---|---|---|
| exists as world + player object | 163 | the **world's** map (rule below) |
| player object only | 93 | the character's `player_knowledge` |

The evaluator's rule (`quests.md` section 11 R1): in shared mode a Simple query reads the world object unless `isExplicitPlayerKnowledgeQuery` (96 of the requirements), and a
player-only object falls through to the player's map. Personal progression (`PlayerQuestProgressionMode`) reads the player's twins.

| | World quests (80) | Auto (76) | Player quests (32) |
|---|---|---|---|
| Mostly names | world + player objects (65 of 80) | mixed | player objects (29 of 32) |

Each quest's own `entryId` ("rewarded") is a **player** Bool, 188 of 188, never a world object.

What the files show:
```
world1.ksd world_knowledge: 527 ids, all static world objects; 0 quest entry ids
char 1   player_knowledge: 902 ids, all static player objects; 36 quest entry ids; 38 of the 163 step-visibility ids
char 2   player_knowledge:  48 ids; 7 quest entry ids; 0 step-visibility ids; 47 of the 48 also in char 1's
                            world1 has 27 of those 163 visibility ids set
```
So, per world and shared by every character: the world knowledge a quest step is usually read from (`world_knowledge`). With the character across worlds: its player knowledge, the
"rewarded" flags among it, its `activeQuestId`. A character taking a done quest to another world keeps its rewarded flag and sees that world's own progress for the steps.

**Consequence for Kiln.** Kiln's QuestBook mirrors the world part and is keyed per character as well (two characters on one world keep separate quests): it
stays on the world, keyed as `known_users` keys a character. `journal: native` quests mirror the book into knowledge, and their player-scope ids come along in the character's
`player_knowledge`, so a join on another world must clear what that world's book doesn't hold (`Kiln.Quests/EngineCharacters.cs`).

## 6. Skills **[save] [dis]**

Native skill nodes are in `player_character`, so they follow the character; the world holds none (`progression.md`). Kiln's overflow ("shadow") nodes stay out of that block on purpose
(`services/skills.md`: the character must stay valid on vanilla servers), and Kiln talent trees have no game-side storage. Both are character progress in the game's model, so they travel with
the character. Which path can carry them?

| Path | Verdict |
|---|---|
| `player_knowledge` entries | Round-trips, but a server without the object deletes the id at load (section 4 step 4), one u32 per id, and objects must be in the static map at boot. A character passing through a server without the mod would lose the data. Rejected. |
| A new block in `character<N>.ksd` | The block types are a fixed enum; the client's writer would drop it, and it never reaches a server. Rejected. |
| `player_character` nodes | Valid only up to the game's 256 nodes / 100 level slots, and the login check resets the whole tree for an unknown id on a vanilla server. Rejected (that is why shadow nodes exist). |
| A Kiln channel and a client-side file | Works with what Kiln has, mirrors sections 3 and 4. Taken. Vanilla clients stay server-side, per character. |

## 7. Limits of the identity

- **A copy is a new character.** The game's own copy to another slot or storage tab gives a new id, so a copy starts without the original's mod data. Seen on both characters.
- **Account 0**: a record with account 0 exists on old hosted data; on current hosted saves the account is the real one, Offline too. Kiln's key includes the account, so a character whose account
  key changed starts over on that world (same as the game's spawn data); its carried data comes from its sidecar.
- **The client chooses the id it sends.** The server trusts it (`0x7ac690` has no check). A client could claim another character's id, so Kiln scopes by account + id; the id alone gets it nothing on
  another account. Also, `known_users` holds 10 characters per account with LRU eviction; Kiln's world data has no such cap.
- **Game log**: a player known only from the game log (records unavailable on an unknown build) has no id: Kiln falls back to an account-only key.

## 8. Open questions
1. The client's write of `CharacterSaveGameMessage` was read only as far as its strings and the states [`0x849ff0`]; the wire format above is the server's parser.
2. `accountIdHash` from the platform id: not derived.
3. The second `u8` of the save stream (`bStackX_20`, returned as "final"?) in `0x714540`.
