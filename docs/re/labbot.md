# Lab bot players: how the dedicated server hosts a player of its own (kiln.labbot)

Engine facts behind `kiln.labbot` (native/services/labbot, native/core/abi/services/kiln_labbot.h). Server exe f0d824be
(svn 1287369); addresses are RVAs of `enshrouded_server.exe`. **[static]**: read in the code; **[lab]**: seen on a lab
server (dev32 LABBOT, 2026-11-27). Lab servers only; nothing here may run where real players join.

## The idea

A client's join reaches the GameServer in two ways: the **session** (the online layer) tells it which machines and
players exist, and the client's **messages** (login, character upload, ready, save acks) arrive on game channels and
reach handlers in `ServerReceive`. Authentication (Steam ticket, password) happens in the session before a machine is
ever published. So a bot needs neither: Kiln adds the bot's machine and player to what the session publishes, and calls
the same handlers the client's messages would reach. The game's own code does the rest (record, spawn search, player
entity from the player template, user-group rights, saves, logout, removal).

## The session's published state [static]

- The session keeps two state buffers of 0x2668 bytes at `session+0x48` (`labbot/session.buffers`, stride
  `labbot/session.stride`); `session+0x3c` (`labbot/session.current`) is the index of the current one, `session+0x40`
  a reference count per buffer (readers `lock inc` it while they read).
- A buffer: players at `+0x178`, 16 entries of 0x150 (`+0x0` player handle, `+0x4` nonzero = remote (SessionCreateSocket
  reads it as the socket's remote flag), `+0x8` machine handle, `+0xc` user-group id, `+0x20` account id, `+0x28` the
  name, `+0x148` a byte copied as is); machines at `+0x1678`, 17 entries of 0xf0 (`+0x0` machine handle, `+0x4` remote
  byte, `+0xc0` the machine's player handles: `{u32* data, u64 count, u64 capacity, ?, u64 allocator|flag, u32
  inline[1..2]}`, a small array whose one inline slot is at entry `+0xe8`).
- `SessionPublish(session, buffer)` `0x9ff430` (`labbot/SessionPublish`) makes a filled back buffer current (it writes
  the index; an older buffer than the current one is refused with a log line). Its two callers: `0xa0be10` copies the
  current buffer into a free one (`0x9f7630`, machines through `MachineInfoCopy` `0x9f7870`) and publishes it;
  `0xa12109` (in the session update) copies the session's own master state at `session+0x5258` into a free one and
  publishes it. So an entry added only in a published buffer is carried forward by the first path and dropped by the
  second: kiln.labbot adds the bot to every buffer at publish, and after the bot left takes it out again (each entry
  only while it still holds the bot's handle).
- `MachineInfoCopy` resets the destination's player count and grows its array with `ArrayReserve(array, capacity, 0)`
  `0x7b84a0` (`labbot/ArrayReserve`): for capacity 1 the array points at its inline slot, no allocation.
- Readers: `SessionPlayerInfo` / `SessionMachineInfo` (`0xa01f40` / `0xa017d0`, a copy of one entry of the current
  buffer), the player and machine lists `0xa020c0` / `0xa01950` that `SyncSockets` walks, and `SessionCreateSocket`
  `0x9fc690` (refuses an owner the current buffer does not list).

## The GameServer's side [static]

- `SyncSockets(gs+0x30)` `0x7bd3c0` (first step of `ServerReceive`): for each player and machine of the published
  lists without sockets, creates its sockets for game channels 0..24 except 14 through `SessionCreateSocket`.
- `AddPlayer(gs, machine, login)` `0x7af1c0` (`labbot/AddPlayer`; what channel 0's Login reaches): refuses with a login
  result when the server is full (2) or shutting down (3). Its first step `0x7abbe0(gs, machine, name, character)`
  **admits the machine**: it reads the machine's info from the published state (`0xa017d0`), sets the GameServer's
  machine slot (`gs+0x16a0 + (machine & 0x7f) * 8`, `{u32 handle; u8 flags}`) and makes the player record of the
  machine's first player with the login's name and character (`0x7ac690`). Then, for each of the machine's published
  players (at most one) whose record (`gs+0x1e0 + (handle & 0x3f) * 0x148`) carries that handle, it copies the account
  from the player info and sets record state 1 (loading), or 6 when the login names a build version other than the
  server's; the new-character flag goes to record `+0x140`, the input toggles to `+0x138`. `ServerReceive` calls it only
  when the login has a name, a character and input toggles (`labbot/recv.login_*`). **[lab]** s1: before the Login
  neither the slot nor the record exists, whatever the published state says (so the bot logs in once it is published
  and SyncSockets made its sockets).
- The GameServer's frame after `UpdatePlayers` (`0x7bc405`): for each machine index whose slot does not hold the
  published handle (`0xa01860`, the 17 handles of the current buffer), the slot is set only for the session's own
  machine (the header field the state copy `0xa024b0` returns at `+0x14`) and cleared otherwise. `UpdatePlayers`
  (`0x7bed90`) resets a record whose handle the published players no longer list (`0xa01fd0`). So a machine or
  player that leaves the published state is dropped as a lost connection.
- `MachineLogin(gs, machine, login)` `0x7af4e0` (`net/MachineLogin`, kiln.net's gate hooks it): SystemsReady sets the
  machine slot's bit 0, GameplayReady bit 1, then the game starts the machine's players.
- `Save2Receive(&final, save2, player, {data, size}*)` `0x7144a0` (`labbot/Save2Receive`; save2 = `*(gs+0x1c8)`): the
  per-player save state is `*(save2 + 0x56cf0 + slot * 8)`; with no handle there yet the message is dropped with a log
  line. Message 1 = a chunk of the character upload (u8 1, u32 FNV-1a of the whole body, u32 total, u32 offset, u16
  length, bytes); message 0 = control (u8 0, u8 flags: 2 asks for the final save, 4 acks save `u32 version`). After a
  final request of a record in game (state 3) `ServerReceive` sets state 4 and asks for a world save
  (`saves/RequestServerSave`).
- The character upload (parsed by `0x714540` once its chunks are in and the FNV-1a matches): u8 compressed (1 = the rest is
  a compressed stream), u8 **new character**, u32 size and that many bytes into the save state's buffer at `+0x18`
  (`{data, size, capacity, grow}`), then the knowledge sub-stream (u32 size, u32 entries, ...). The chunk receive stores
  `+0x14c = (new character byte == 0)`: "this section is a saved entity". **[lab]** s2-s4: with that byte 0 the player
  update (`0x7a93a0`) tried to deserialize the section as an entity and logged "[server] Could not spawn Player Entity"
  (record state 6).
- A new character (byte 1): the record's state-3 step reads the section as the character creator's choices,
  `0x6ff580`: u8, 9 x u32, u8 (38 bytes; the third u32 nonzero picks something more, `0x99df70`), and spawns the player
  entity from the player template `d666dbc3-a589-6c4a-a8e3-d9a1f6009812` with entity id slot + 1 (`0x628e20`). **[lab]**
  s5: 38 zero bytes give a player in the world ("Player 'Bot Alder' logged in with Permissions:"), entity 16 for slot 15.
- The record's states in the player update (`0x7bf3d4`, a jump table at `0x7c0540`): 1 searches a spawn position
  (`0x7aded0`, into record `+0x100`) and waits for the world around it to stream in, then 2; 2 keeps streaming there;
  3 with no entity spawns it (above); 4 removes the entity; 5 waits for the final save; 6 sends the login failure.
  The bot's SystemsReady and GameplayReady move a loaded record (2) to 3.

## What kiln.labbot does

1. join (tick thread): picks the highest player slot and machine index no record, machine slot, published entry or bot
   uses, the machine index also one no bot held before in this server session (below, "A join after a bot left"); the
   handles are `slot | 0x55 << 6` and `index | 0x55 << 7` (real ones count up from generation 1).
2. Every publish (session thread): the bot's entries go in (or come out after it left).
3. After each `ServerReceive` (GameServer thread): `Bot::step` (bot.cpp): once the current buffer lists the bot and
   SyncSockets made its machine's sockets (the GameServer's socket set, `net/gs.sockets` `+net/sockets.machines`), the
   Login (`AddPlayer`); once Save2's slot carries the bot's handle and waits, the character upload (`Save2Receive`);
   SystemsReady and GameplayReady (`MachineLogin`) when the record is loaded; acks of every save the server sends;
   and on leave the final request plus what `ServerReceive` does after it. While it is in game, the parts of its client
   input a script set (below, "Using an item").

## The lab sessions (dev32 LABBOT, 2026-11-27, lab dev32-labbot, server f0d824be)

| Run | What happened | What it showed |
|---|---|---|
| s1 | published, never logged in | the machine slot and record are made by AddPlayer itself; the bot waited for them (fixed: wait for the sockets) |
| s2-s4 | logged in, upload taken, "Could not spawn Player Entity" | the upload's second byte is "new character", not "has entity" (fixed) |
| s5 | in game in 0.27 s, 60 s in the world, 3 saves acked, logout | the whole join and leave work; see below |
| p1-p4 (lab dev32-labbot-pets, a temporary merge with dev32/review-pets) | the bot typed `/pet give hound` and `/pet summon 1` | owning a pet; the summon's refusal is Kiln.Pets' (below) |
| s6 | two bots, a drop, a logout, a new name, the first name again | all work but the last (below) |

**The four open points of the first notes, answered:**

1. *Messages to a machine the session does not really have* **[lab]**: the GameServer sends to the bot as to any client
   (the session's send allocator peaked at 980,304 bytes with the bot in game, 0 without); the session's per-machine
   table shows 0 bytes sent to every machine: nothing reaches the wire, nothing is logged, nothing piles up (the
   server's memory was flat for the last 45 s of the stay). The review's 5-minute stays (g1, g3; `bot.stay(300)`, RSS
   every 10 s, 10 saves acked each) after the first minute: 1152 to 1157 MB (min 1152, max 1158) and 1081 to 1089 MB,
   a drift of +1 to +2 MB a minute with plateaus and dips, against 1049 to 1068 MB in the 30 s before the join. Small,
   but five minutes cannot rule out a slow leak: hours were not measured.
2. *kiln.net's gate and the bot's Gameplay Ready* **[lab]** with the gate off (kiln.net's default): the bot's
   GameplayReady goes through kiln.net's hook on MachineLogin like a client's and the record reaches state 3. With a
   holding gate the bot is held like any client without Kiln (it never sends Kiln's proof);
   `KILN_LABBOT_JOIN_KILN_CLIENT` stays reserved for letting it through as a Kiln client, not built in v1.
3. *Player entry `+0x4` and `+0x148`* **[static]**: `+0x4` is the remote flag SessionCreateSocket passes to the socket
   (a machine's is its `+0x4` byte); with it set the bot's sockets are made (s5). `+0x148` is a byte the state copy
   carries; the bot leaves it 0 and nothing needed it.
4. *The character upload's size limit* **[lab]**: Save2's upload buffer held 131,072 bytes at the bot's login
   (`labbot/save.upload` `+0x10`); the bot refuses an upload that does not fit before sending it. A new character's
   upload is 52 bytes.

**Seen in s5:** `Players.All` lists "Bot Alder" (entity 16, admin), `CharacterData.KeyOf` gives its derived character
id, kiln.characters logs it in game, `Commands.Execute` as the bot runs `/help` (done, 2 lines), the world around the
spawn streams in (RSS +120 MB at the join), each world save reaches the bot (3 acked in 62 s), and the logout ends
with "[server] Remove Entity for Player 'Bot Alder'" and "Remove Player", the server's exit code 0.

**Seen in p1-p4 (PetsSample on Kiln.Pets):** the bot's own chat line `/pet give hound` ran PetsSample's admin command with
the bot as source; `Pets.Of(CharacterData.KeyOf(bot))` then held a Hound whose owner is the bot's derived character key,
and the records were still there in the next server session (the same name is the same character). `/pet summon 1`
came back to the bot as "refused: BodyRefused: its faction could not be set (Faction: call it from mod code (changes
belong to a mod))": Kiln.Pets' `GamePetWorld.CreateBody` sets `Creatures.Of(e).Faction` from Kiln.Pets' own step, where
Kiln.Creatures finds no mod owner (Creatures.cs `RequireOwner`). That is Kiln.Pets' to fix (lane PETS), not the bot's;
three more summons in the same step answered "NoSafeGround". Teleporting the bot (`Teleport.To`) moved it 6 m and
read back.

**Seen in s6:** two bots at once (slots 15 and 14, entities 16 and 15); the one in the group with the fewest rights got
rights 32 and the game's command check refused it `/kick` ("You may not use /kick."); a drop takes the player out of
`Players.All` within 3 s; a new name joins at once after another bot's logout and reuses its slot and machine.

**A join after a bot left (dev.32 review-labbot, then labbot-2).** A bot that joined after another bot left (a logout or
a drop) stayed in record state 1 until the 60 s load limit: the same name (lane p1, p2, s6) and, in the review's labs, a
new name too (g1 to g4: 0.1 s to 60 s after the leave, also with a new machine and player generation in a test build).
Only the lane's s6 had one spawn (Bot Cedar, after Bot Alder's 4 s in game). The review refused a name that played earlier
in the session (`labbot::Played`) and told scripts to restart the lab after any leave.

**The cause [static] [lab].** The record's state-1 step (jump table `0x7c0540`, state 1 at `0x7bffc2`) calls the spawn
position search `0x7aded0`; on a found position it stores it at record `+0x100` and goes to state 2 only when four checks
pass: `0xa45c10` (the area at that position, on `[gs+0x1770]`), `0x66c710` (`[[gs+0x1d0]] == 2`), `0x23a180` (on
`[gs+0x1d8]`) and `0x7b6460`, the **spawn send**: it looks the machine's socket entry up (`[gs+0x30]+0x28`, 32-byte entries
by machine index, `{u32 handle; ...; +0x10 sockets; +0x18 count}`) and asks the session for a send buffer on it
(`0xa033b0`, result byte 0 = queued). The session refuses one (result 2) when the socket's link has no credit left for it
(`0xa0354b..0xa035b9`: the link `[socket+0x10]` holds the credit at `+0x4` and the frame's reserves at `+0x10`; a request
whose minimum size neither the credit nor the socket's own reserve `+0x20` covers is refused and its minimum size added
to the socket's `+0x24`; a granted one counts in `+0x28`, 512 a frame at most, and only then is a buffer allocated,
`0xa036f0`). A bot never acknowledges anything, and as far as the labs show only a client's acknowledgements give credit
back: once what the server sent a bot in game used its link's credit up, every later send on that machine index is
refused, and `+0x24` adds up the refused bytes. And SyncSockets (`0x7bd3c0`) keeps a machine entry's sockets until **another handle takes that index**
(`0x7b5530` destroys them then; a machine that leaves the published state keeps them). So the next bot on that machine
index (kiln.labbot gave every bot the highest free one: index 16 again) found its spawn message refused every frame.

Lab p1 (dev32-labbot-2, a probe build that logged the four checks, the spawn send's results and the bot's sockets every
3 s; `out/labbot-2/runs/p1-rejoin`):

| Step | Machine / slot | Result | What the probe showed |
|---|---|---|---|
| Exp Alder joins, 20 s in game, logout | 16 / 15 | in game in 1.6 s | its machine sockets' `+0x24` grew while it was in game: channel 11 to 3.9 MB, 16 to 4.7 MB, 26 to 3,601 bytes; none ever went down |
| Exp Birch (a new name) | 16 / 15, the same handles | record state 1 after 15 s | the same socket objects (entry 16 still held 0x2a90); the search found a position (`+0x100` set), `[[gs+0x1d0]]` 2, `0x23a180` 1, the save state 0x6; **the spawn send refused 9,088 of 9,089 tries**, channel 10's `+0x24` grew to 148 MB with the refused tries |
| Fresh Cedar (a new name, an index no bot held) | 15 / 14 | in game in 0.5 s | |
| Fresh Cedar again (the same character) | 14 / 13 | in game in 0.6 s | the same character spawns again: the name was never the cause |

**The fix (dev.32 labbot-2).** kiln.labbot gives each bot a machine index no bot held before in this server session
(`labbot::SpentMachines`, `pick_index` in bot.cpp: the highest free one; real clients get the low ones from the session),
and the refusal of a name that played earlier is gone. A server session has 16 machine indices (1 to 16; 0 is the
server's own), so about 15 joins per server session, less one for each player connected; then `join` answers
`KILN_ERR_NOT_FOUND` ("no free machine index that no bot held in this server session"), and a restart gives them back.
Not fixed: the backlog itself. Draining it would mean answering the session's reliable channel as a client does
(acknowledgements), not built.

**The counters over hours (review-labbot-2) [static].** A bot in game keeps adding to its sockets' `+0x24` (channel 11
about 0.2 MB a second in p1), a u32 that would wrap after some hours. That changes nothing: no send decision reads it (the
request above reads the link's credit and reserves and the socket's `+0x20` and `+0x28`). Its one reader is the session's
frame end over the sockets (`0x9f85b3..0x9f8735`): for a socket that holds a reserve (`+0x20` nonzero) it logs "channel
%u reserved %u more bandwidth than it used" from `+0x20` and `+0x24` and then zeroes both (a qword store at `+0x20`);
a bot's sockets hold none, so theirs only grows. `+0x28` is reset there at every frame's end for every socket (after
"channel %u tried to send %u messages in one frame" at 512 or more), so it never wraps. Nor does memory grow with it: a
refused request allocates nothing (`0xa036f0` runs only for a granted one), so what the session holds for a bot is
bounded by the link's credit (its send allocator peaked at 0.98 MB in s5).

## Using an item (dev.32 labbot-2)

A client uses an item from its hotbar: the player programs turn the press into a trigger of the item's own sequence
(`ItemInfo.sequences[]` with `triggeredBy` Primary) with the slot as the trigger context's used item, and the sequence's
events do the rest on the server. Kiln already starts that sequence the same way (`Abilities.UseItem`, kiln.abilities
`item_use`: the helper player actions use, `ActorInput.triggerContext`), so a bot's use is that call on its slot. Two
parts of the bot's client input decide whether and where it works **[static] [lab]**:

- **Readiness.** `actor_is_ready_for_gameplay` sets `IsReadyForGameplay` only when the client's
  `PlayerInput.fromClientIsReadyForGameplay` (+0x5b8) is set and the terrain at the character is loaded
  (docs/re/player-state.md), and `Abilities.UseItem` refuses a player that is not ready (`NotReady`). A bot sends no input,
  so it was never ready: `LabBots.SetReady` makes its client say so.
- **The camera.** The dog bait `Food_T2_Dog_Bait` (`0xb59dfc2c`, category AnimalFood, flag `IsAnimalBait`) has one use
  sequence, `e638b71f-…` `UseItem_Drop_FoodBait` (0.3 s): at 0.15 s a `SpawnEntityEvent` with an empty template (so the
  used item's `equipment.placedEntity`, `FoodBait_T2_Dog` `21eb90be-…`), `spawnTransform` **Crosshair**,
  `useTargetingPosition`, `spawnOffset` local (0, 0, 0.5), `consume` on, and the item's `placedEntityLocalSpawnVelocity`
  (0, 5, 10) (`tools/kfc` decode of the server's data). The spawn transform's switch (`0x3f72a` in the command builder
  `0x3f6a0`, a jump table at `0x40558`) sends Crosshair (2) to `0x40580`, which reads the actor's PlayerInput (`ctx+0x88`)
  at `+0x28` (`fromClient.camera.position`), `+0x40` (`.orientation`) and `+0x50` (`.distance`): the spawn point is the
  camera's position moved `distance` along its view, turned as the camera. So the bait is thrown from the camera along
  the view, 5 m/s up and 10 m/s forward. A bot's camera was at the world's origin (kiln.playerstate's `aim` reads it as
  no camera); `LabBots.SetCamera` sets it.

kiln.labbot writes those parts (`input`, `KilnLabBotInput`) into the bot's PlayerInput after each GameServer receive while
the bot is in game. That is where a client's input lands: `ServerReceive`'s player channel 8 queues a client's input per
tick and `InputApply` (`0x600ff0`) copies the step's entry into PlayerInput; no entry ever arrives for a bot's slot, so
nothing overwrites what kiln.labbot wrote. The parts are found by the game's reflection (kiln.types: PlayerInput,
ClientPlayerInputData, ClientCamera), the component through kiln.world.

The labbot-test mod's `give` (Items.Give: the game's own inventory add) and `use` ops (ready, the camera at the bot's eyes
looking at a point, then `Abilities.UseItem` once the game marks the bot ready) are what lab scripts call
(`tools/labbot`: `bot.give`, `bot.use(item, at=...)`).

**The labs (dev32-labbot-2, 2026-11-27; `out/labbot-2/runs`)** **[lab]**:

| Run | What happened | What it showed |
|---|---|---|
| u3 | the bot said it is ready, a use answered Busy for 10 s | ready makes the game play a new character's first-time spawn intro, `Player_Spawn_Initial_FirstTime_PodWakeup` (17.7 s, trigger ActionRespawn, StateFlag Spawning); a bot uploads a new character at every join, so it plays at every join. labbot-test's `use` waits for it (30 s by default) |
| u4-u6, p2 | 3 dog baits and 3 berries given (Items.Give: Done 3), one berry eaten, one bait thrown | the berry: `UseItem` Started after the intro, the game took one (3 to 2) and put buff `0x4c958a05` (28.5 s) in a Food slot; the bait: Started, the game took one (3 to 2), and a `FoodBait_T2_Dog` entity (template 21eb90be) lay 1.85 m along the view (pitch 43.8 degrees down from 1.7 m: the flat-ground model of (0, 5, 10) says 4 m), carrying `UsedItem` = the bait item (the spawn path's; a bait spawned from its template has none) |
| p2 (a probe build that hooked the bait sensor `0x21e160` and its query `0x166ef0`, observing only) | a wild dog 3.5 m east of the bot, facing it, the bait 1.8 m in front of it | **the dog's own sensor took the thrown bait**: `DynamicWildlife.foodBaitEntityId` = the thrown bait's entity, `hasSensedFoodBait` 1, then FoodBaitPursue (state 14), alternating with Roaming (6) for 30 s; it never ate (state 15) with the bot 1.85 m from the bait. It sensed after it had been alerted (state 4 Alert, 7 RunAway) |
| u4, u5, u6 | the same throw; in u6 the bot backed off 9 m after it | not sensed: the dogs stayed in Idle (3) and Roaming (6) and were never alerted |
| controls (u4, u5, u6) | a bait spawned from its template (Spawn.Entity), u6 on the spot a thrown one lands | never sensed, but its dogs never left Idle and Roaming either (and u5's fell 7 m into a hole), so they do not show that a template spawn cannot be sensed |

The sensor, statically: `0x21e160`, called through `0x220640` from the behaviour update (`0x21eaad`) for a Tameable
template, every 32 frames per animal (`0x69d740(ctx, 4)`), after a 10 s cooldown from the last step (`0x21d750`, record
`+0x78`): an AABB query (`0x166ef0` over `0x67a830`, collision mask `0xfb877d0f`) of `foodBaitSensorRange` around the animal
for an entity whose template is the favourite food (`0x65bc10`), with an angle check beyond
`foodBaitAngleCheckMinDistance` (2.5 m for the dog). **Open:** which wildlife states reach that call (p2 suggests the
alerted ones), and why a dog that sensed the bait did not eat it (the bot next to it; `foodBaitEatAdaptFleeRadiusDistance`
6 m). Next lab steps for TAME: alert the dog first (or find that state), throw, back off, then the pet sequence.
