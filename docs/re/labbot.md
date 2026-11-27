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
   and on leave the final request plus what `ServerReceive` does after it.

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
(`0xa033b0`, result byte 0 = queued). The session refuses one (result 2) while the socket's backlog is over its budget
(`0xa03402..0xa03613`: the socket's `+0x24` counts the bytes queued and never acknowledged, `+0x28` the messages, 512 at
most). A bot never acknowledges anything, so every byte the server sends a bot in game stays counted on the sockets of its
machine index. And SyncSockets (`0x7bd3c0`) keeps a machine entry's sockets until **another handle takes that index**
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
Not fixed: the backlog itself. A bot in game keeps adding to its sockets' counters (channel 11 about 0.2 MB a second in
p1); they are 32-bit, so a bot in game for some hours could wrap them (not seen; the review's 5-minute stays were fine).
Draining it would mean answering the session's reliable channel as a client does (acknowledgements), not built.
