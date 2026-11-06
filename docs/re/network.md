# Networking: transport, session channels, and Kiln's mod channel

Builds: dedicated server svn 1285601 (`enshrouded_server.exe` sha256 `c5c9fe50…`, also checked on 1273043), client
Steam build 26404966 (`enshrouded.exe` sha256 `ce6fdba6…`). Static analysis only (python capstone + `.pdata` bounds,
Ghidra 12.1.2 headless decompilation without auto-analysis, reflection data in `.rdata`), then checked at runtime with the real client (build 1285601, GE-Proton) hosting a world and joining the lab dedicated server (see
"Runtime" at the end). RVAs: server / client. Definitions: `sigdb/symbols/server.d/net.json`,
`sigdb/symbols/client.d/net.json`. Service: `native/services/net` (`kiln.net`), managed `Kiln.Network`.

## Summary

| Question | Answer | How sure |
|---|---|---|
| Transport | Steam Networking Sockets (`SteamNetworkingSockets013`, connection lanes, poll groups) under the game's own **session layer** (machines, players, acks, reliability, fragmentation, congestion control, packet authentication). A plain UDP socket exists for the dedicated server's query/ping port. | High (imports, interface strings, log strings) |
| Message dispatch | Per connection ("machine") and per player the session has one **message socket per game channel**, 25 game channels (0..24). The GameServer and the GameClient read every channel's socket each frame and `switch` on the channel. | High (code, both exes) |
| Chat | player channel 19 client → server (`ChatMessageClientMessage`), machine channel 19 server → client (`ChatMessageServerMessage`) | High |
| ECS replication | machine channels 4/5/6 (EcsClient / EcsPrediction / EcsSnapping) | High for the channels, Medium for the names |
| A free channel | **game channel 12**: configured "reliable + large messages" (mode 3, max 8 KiB per message), sockets exist on both sides for every connection, and **no vanilla code reads it**: the server's machine handler warns "invalid machine message" and drops, the client's machine loop has no case for it (silently freed). | High (static; Kiln frames cross it both ways at runtime) |
| Chat while `enableTextChat: false` | The server's chat receive path has no gate (docs/re/chat.md); the client refuses to send typed lines (`GameClient+0x4672A2`). Irrelevant for Kiln: the mod channel does not use chat. | High |
| Kiln's channel | **In-band on game channel 12** (machine sockets, both directions), using the game's own session functions to read, allocate, commit and free messages. No extra port, authenticated and encrypted by the game session, reliable. | Design |

## Session layer (shared code in both exes)

Strings `[session] …` (`Packet from %k has failed authentication`, `Congestion detected for machine %k`, `Received
message for channel with no registered socket. ComposedChannelID: %i`, `Requested message size(%u) exceeds the absolute
maximum message size(%u) for game channel %u`, `channel %u tried to send %u messages in one frame`).

- Session sockets system = `session + 0x4dd0`. Socket create `0x9fc8e0` / client `0xa67f50` (via `0x9fc6a0`):
  `{u8 kind (1 player, 2 machine); s32 channel; u64; u32 a, b, c; u32}`; the composed channel is `game channel + 10`
  (channels 0..9 are the session's own).
- Channel configuration (`*(sys + 0x11b10)`, 0xE8 bytes per composed channel): byte 0 bit 0 → the socket may carry
  large (fragmented) messages. The game fills the 25 game-channel modes in `0x935560` / client `0xe8e330`:
  ```
  channel: 0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24
  mode:    2 2 2 2 1 1 0 0 0 2  2  2  3  0  0  2  1  3  2  2  2  2  1  1  2
  ```
  Mode 2 = reliable (login, voxel data, chat), 0 = unreliable (input, snapping), bit 0 = large. 12 and 17 are the only
  mode-3 channels; 17 is never read by either side's main loops (the save-game transfer reads it elsewhere).
- Limits (session setup `0x53b3e5`): large messages up to `0x2000` (8192) bytes, `0x4000` with the target's "big"
  flag; otherwise one packet (MTU minus headers). At most 512 messages per socket per frame. A socket may have a
  bandwidth budget: allocation then fails with code 2 until the next frame ("reserved more bandwidth than it used").
- Message API (the four functions Kiln calls; `res` layouts from the callers):
  | Function | Server | Client | Use |
  |---|---|---|---|
  | `SessionRead(res*0x28, session, socket)` | `0xa03230` | `0xa6f2a0` | next received message: `res+0` status (0 = got one, 0x12 = no socket, 0x1f = no machine), `res+8` handle, `res+0x18` data, `res+0x20` size |
  | `SessionFree(session, &res+8)` | `0x9fa460` | `0xa65c60` | release a read message |
  | `SessionAlloc(res*0x38, session, socket, spec*)` | `0xa033c0` | `0xa6f430` | `spec {u64 min, u64 max, u8 explicit}` (explicit 0 = the socket's maximum); `res+0` status, `res+8` handle, `res+0x20` u32 final size (set before commit), `res+0x28` buffer, `res+0x30` capacity |
  | `SessionCommit(session, &res+8)` / `SessionAbort` | `0x9fa4c0` / `0x9ff3d0` | `0xa65cc0` / `0xa6ae50` | send / drop an allocated message |
  The game's writers round the final size up to 4 bytes (bit writers flush 32-bit words); Kiln frames carry their own
  length.

## Server side (dedicated server)

- `GameServer+0x30` = the game's socket set: `[0]` session, `[2]` player table (0x20-byte entries, `handle & 0x3F`),
  `[5]` (+0x28) machine table (0x20-byte entries, `peer & 0x7F`: `+0` handle, `+0x10` `Socket*[25]`, `+0x18` count).
- `GameServer+0x16A0`: 17 machine slots `{u32 peer handle; u8 flags}` (flag bit 0 = logged in; ChatSend's loop).
- `ServerReceive(GameServer)` `0x7bdb20` (the GameServer frame's receive step, tick thread, before the sim step):
  1. `SyncSockets(*(gs+0x30))` `0x7bd3c0`: for each new machine and player, creates sockets for channels 0..24 except
     14.
  2. Machine loop: for each machine, channels 0..24 except 14 and 17: `SessionRead` until empty, `switch (channel)`,
     `SessionFree`. Handlers: 0 login, 4/5/6 ECS, 13, 15, 16, 20, 22. Every other channel (1-3, 7-12, 18, 19, 21, 23,
     24): `[server] Received invalid machine message on channel '%s'.` (warning) and drop.
  3. Player loop, same scheme: handlers 7, 8 (input), 9 (save game), 10 (knowledge), 11, 19 (chat → ChatLogAdd), 23;
     others warn "invalid player message".
- Kick primitive: `SendLoginResult(GameServer, peer, reason)` `0x7b6020` writes a failed `GameMessageLoginResult` on
  the machine's login channel (channel 0); the player update calls it for failed records. Reasons
  (`keen::GameMessageLoginErrors`): 0 Authentication, 1 Version, 2 Full, 3 Shutdown, 4 SaveDataIssue, 5 Generic.

## Client side

- `GameClient+0x180` = the client's socket set: `[0]` session, `[3]` (+0x18) `Socket*[25]` toward the server machine
  (created for every channel except 14 when the connection is set up, `0x84541b`), `[6]` (+0x30) the local player's
  sockets (`0x872d90`, called at the start of the receive step).
- `ClientReceive(GameClient)` `0x874df0` (main thread, GameClient update): machine loop over channels 0..24 except 14
  and 17: handlers 0 login result, 1 ServerInfo (copies `CommunicationSetup` to `+0x4672A0..A2`), 2/3 voxel, 4/5/6 ECS,
  15, 16, 18, 19 chat, 20, 22; **no case for 8-13, 21, 23, 24** (the message is freed, nothing logged). Player loop:
  handlers 7, 9, 10, 11, 21, 23, 24; others warn "invalid player message".
- Hosted worlds: the client exe contains the same GameServer code (`ServerReceive` `0xe61540`, `SyncSockets`
  `0xe60de0`, `SendLoginResult` `0xe5a030`), the GameServer register is r15 there.

## Kiln's mod channel (design from the evidence)

- **Channel**: game channel 12 on **machine** sockets in both directions. Channel 12 is the only game channel that
  (a) is reliable and large (mode 3), (b) has sockets for every connection on both sides, (c) has no handler on
  either side and at either level (machine and player). Unused by the game in this build.
- **Server**: a mid-function hook right after `SyncSockets` returns inside `ServerReceive` (`net/SyncReturn`; the
  GameServer is in r13) drains channel 12 of every machine in the 17 slots before the game's loop reads it, so the
  game never sees a Kiln frame; frames are delivered in the next Pre (same server frame). Sends: `SessionAlloc` with
  the socket's maximum, copy, set the size, `SessionCommit`, on the tick thread. Peers are machine handles; a peer's
  player comes from the record whose `+0x04` is that handle.
- **Client**: a hook on `ClientReceive` drains channel 12 of the server machine socket (`*(*(gc+0x180)+0x18))[12]`)
  before the game's loop and flushes Kiln's outgoing frames there (main thread, GameClient known to be alive).
- **Vanilla peers**: a Kiln client on a vanilla server: its HELLO costs one warning line on the server per attempt
  ("invalid machine message on channel"); Kiln stops after 3 attempts and treats the server as vanilla. A vanilla
  client on a Kiln server never sends a HELLO; Kiln never sends it anything on channel 12 (and if it did, the client
  would free it silently).
- **What a vanilla peer does with a channel-12 message** (static, from the code paths):
  - Vanilla **server** (machine loop of `ServerReceive`, case `0x7be062`): one warning per message,
    `[server] Received invalid machine message on channel '<name>'.`, then `SessionFree` and the next read
    (`0x7be0d6 call log; 0x7be0db jmp 0x7be0e4; 0x7be0ef call SessionFree`). No disconnect, no state change, no
    counter. So a Kiln client costs a vanilla server exactly one log line per HELLO attempt (Kiln sends at most 3,
    2 s apart, then treats the server as vanilla and sends nothing more).
  - Vanilla **client** (machine loop of `ClientReceive`): no `case 12` in the jump table, so the message is freed
    without a log line or any effect. A Kiln server never sends on channel 12 to a peer that has not sent a HELLO,
    so vanilla clients on a Kiln server see nothing at all. A refused one gets a REJECT there, which it frees.
  - Both vanilla cases were runtime-unverified when written; with Kiln on both sides the lab server logged no "invalid
    machine message" line at all, so the drain runs before the game's loop. A vanilla client has since joined a Kiln
    server (section "Vanilla clients on a Kiln server").
- **Why not chat**: the chat line is ≤ 0x400 bytes of text, lands in the shared inbox (evicting real lines, per-reader
  byte accounting), echoes to every reader unless narrowed, and the client gates sending on `enableTextChat`.
- **Why not a side channel**: an extra UDP/TCP port needs forwarding and its own authentication (Steam session
  tickets); the session channel is already authenticated per connection. Kiln keeps a **lab-only loopback UDP
  transport** (`KILN_NET_LAB_PORT`, policy `any` only) so the protocol above the transport can be tested without a
  client (`tools/nettest`).

## Runtime
Real client (Steam build 26404966 / game build 1285601) under GE-Proton, Kiln on both sides, `modSync: warn` unless
noted:
- **Hosted world** (client exe's own GameServer, register r15): the client's GameClient connects to it; HELLO →
  WELCOME on channel 12 and the NetSample ping/pong (29 ms) work.
- **Remote join** (lab dedicated server on 127.0.0.1:25880): `net: peer 0x81 up (machine slot 1)`, `peer 0x81 is
  player 'Alder' (slot 0, state 1)`; client `net: connected to a server`, `hello #1 ... rc 0`, `first frame from peer
  0xffffffff (336 bytes)`, `server speaks Kiln 0.1.0-dev (policy warn)`; server `Alder (0x81) joined with Kiln
  0.1.0-dev: 2 mod(s), 1 shared channel(s), 3 warning(s)`. NetSample ping/pong 26 ms (the in-menu round trip shows
  13-33 ms). Server log: **no** "invalid machine message" line.
- **Refusal** (`modSync: refuse`, the client lacks net-test): client toast `Refused: This server requires mods you do
  not have: net-test 0.1.0 (missing), channel net-test:echo v1 (missing)`; 1.6 s later the game's login failure
  (reason 1) shows the game's own dialog **"An update of the game is available. You need to download the latest
  version of the game in order to use online features."** — misleading on its own; the Kiln toast carries the real
  reason. Server: `refusing Alder`, `kick peer 0x101 reason 1: login failure sent`, `peer Alder (0x101) left
  (Rejected)`. The client returned to the main menu and could join again.

## Vanilla clients on a Kiln server (client 26430625, server 26430253, svn 1287369)
Marks: **[dump]** read from a crash dump, **[log]** real logs, **[dis]** read in the code, not run.

### The crash
- **Where** **[dump] [dis]**: the client's CreateEntity reader `0x961b90` (server copy `0x5fd6e0`, shared code) looks the
  message's template GUID up in the template map (`storage+0x745810`). A miss leaves `r14` = 0. It then calls
  `0x987910` without a test, which reads `[r14+0x28]`, `TemplateInfo`'s component bitset: `ACCESS_VIOLATION` reading 0x28.
- **The message** **[dis]**: `u32 (tick?)`, `u32 entity`, `u32 parent`, `GUID`, `u8`, `u16 length`, data; first word
  `(sequence << 3) | type`, type 2 = create, 3 = update. Written at one site only, `0x62c886` in `0x62c2e0`
  (a search for `shl eax,3 .. or eax,2` over `.text` has this one hit).
- **Which entity** **[dump] [log]**: `xmm6` still holds the GUID at the fault, the reader's frame holds the entity.
  | Dump | GUID | What it is | Entity | Lab log |
  |---|---|---|---|---|
  | a crash dump (a character) | `299d68c5-10b9-4af9-91e0-194b308ee461` | `Kiln_NpcSample_Tobin` (npcsample.json) | 0x48a = 1162 | `NPC npcsample:tobin is entity 1162` |
  | a crash dump (new character) | `5b58f8ff-edf9-50b5-9e06-2a93ac0b1b64` | uuid5(Kiln ns, `quests-sample:marker:ember_errand.meet`), `Kiln_Marker_quests-sample_ember_errand_meet` | 0x1c5a = 7258 | `spawned Kiln_Marker_quests-sample_ember_errand_meet as entity 7258` |

  So any live entity of a Kiln-added template that reaches a client without it crashes that client. Kiln gear isn't
  needed. Which one comes first depends on the world and the join.
- **Third dump**, `enshrouded_20261105_181614`: the `apply_armor` read, docs/re/content.md section 9.

### Why a timer can't refuse in time **[log] [dis]**
- `GameMessageLoginType` 1 = SystemReady, 2 = GameplayReady reach `0x7af4e0` (client's hosted copy `0xe53730`), called once from
  `ServerReceive` at `0x7bdd52` as `(GameServer, peer, &message)`; only the message's first byte is read. Type 2 sets the
  machine's flag bit 1 and moves its records from state 2 to 3. Replication starts right after: game log `Machine '1':
  Player '0(0)' logged in`, `Replication Checkpoint 1 Set for Player 0` 36 ms later.
- A Kiln client's HELLO comes after that. Six joins: client `Send Gameplay Ready`, then
  `[ecsc] ### set tick 0 -> 12,769` 52 ms later, then the client world's first step, then `handshake: hello #1` 0-28 ms after
  it (19 joins). The HELLO is sent from `Client.Tick`, so it needs the server's first tick. Consequences: Kiln's `in game` state can't
  be used to judge a peer, and a hold must not depend on the HELLO. Hence the proof frame.

### The gate **[dis]**
- Proof: any frame on channel 12 (no vanilla code writes it). The client sends it in a mid-hook on `0x86d9f0`, the function
  that sends Gameplay Ready (`net/ClientGameplayReady`), so it's committed first in the same call; and from `ClientReceive`
  1 s and 3 s after the connection comes up.
- The server drains channel 12 right after `SyncSockets`, before the game's channel loop, so a proof that arrives with or
  before Gameplay Ready is seen first. They are two reliable channels of one connection, not one ordered stream: after a
  lost packet the proof can land behind it, and the hold covers that.
- `net/MachineLogin` is an inline hook. A held Gameplay Ready isn't passed on; it's re-issued from the after-sync hook, inside
  `ServerReceive`, the point where it would have been processed. While held, record state stays 2: no entity, no replication.
  The local machine (`peer & 0x7f == 0`, hosted or the dedicated server's own) is never held.
- The record's state-2 case (`0x7c0108`) only preloads around the spawn point; no timeout found there.
- Channel 12 on a vanilla client: the machine loop's jump table (`0x876328`) sends 12 to the default case `0x875641`, which frees
  the message. So the REJECT sent before a refusal does nothing there.

### Withholding **[dis]**
- A player's replication object: `[world+0x15fd2a8] + slot*0x125850 + 8`. Its first byte is the slot: `0x67d3e1` compares it with
  `entity - 1` to give the player's own entity priority 0x20d.
- Entities go out through a heap at `+0x8f0` (entries 0x1c: entity, last tick, entered tick, u16 priority, position), read per tick
  by `0x62b3c0`, which queues candidates for the writer `0x62c2e0`.
- `0x6691a0(storage, player, state, entity, options)` is the only push (the other references to `+0x8f0`..`+0x910` are init,
  clear and reads). `net/ReplicationEnqueue` hooks it. Skipping it leaves the entity's state with no bit 8 ("in the heap") and
  sends nothing.
- Why here and not in the writer: a heap entry older than the player's Replication Checkpoint gets +1000 priority (`0x67d539`) and
  the checkpoint is reached only when the top entry is newer (`0x6c7239`). An entity left in the heap and refused by the writer
  would hold the checkpoint until its 900-tick timeout (`0x6c71c6`).
- `0x6690b0` re-queues an entity with bit 8 clear when it changes, so a withheld moving entity is checked again per change, and
  appears on its next change if the peer proves itself later. A static one may not appear until it's relevant anew.
- Kiln-only test: `TemplateInfo` GUID, then `kiln.assets` `resource_is_new` for type `0x39768775`. Children: `EntityRecord+0x14` is
  walked up to 8 parents. Every way Kiln adds a template ends in `resource_put`: `gamedata_clones.cpp` (template clones from data packs
  and code), `native_pack.cpp` (NPC clones, quest markers and sites), `assets.json` manifests and `Assets` calls.

- Sending later **[dis] [lab]**: the hook remembers what it kept back, per slot. Once the slot is cleared, the next call of the hook for that player sends those ids from inside
  the player's own job: the game's thread and moment for that queue. An id with no state left in the player's map (`player+0x858`) has gone out of relevance and needs nothing; the game queues it again if it returns.

### The guard on a Kiln client **[dis] [lab]**
- `net/CreateTemplateCall`, a mid hook on the reader's `call 0x987910` (`0x961d07`): `r14` is the `TemplateInfo`, 0 on a miss, the GUID is in `xmm6`, the entity and parent at
  `rbp+0x3cb0`, `rbp+0x3cc0` (captured in `@net.create_fields`).
- Dropping resumes at `0x961d18`, the branch the game takes when that call returns nothing: it frees what the argument set up, returns an empty result, and `0x9c70ee` discards the
  message. Hooking the miss branch itself (`0x961cc9`) would have cost nothing on a hit, but `0x961ccc` is a jump target inside the 5 bytes the hook overwrites.
- Cost: the hook's stub plus one test per create; a second test, and a set lookup for a create with a parent, only after something was dropped on this connection.
- `0x987910`'s other callers: `0x990645` is the hosted server's writer, with a template it read from a live entity. `0x96256d` is an update that carries a GUID, which is only reached once the
  reader has found the entity (`0x96235e` returns early otherwise), so its template is known. Not guarded.

| What follows a dropped create | What happens |
|---|---|
| Later updates | the update reader `0x962260` looks the entity up first and returns empty on a miss (`0x96235e`). Discarded before `processMessage`, so its `Entity %,u not found` line doesn't fill the log |
| Children | dropped too, by parent id, grandchildren included. A child that arrives before its parent is dropped stays, without a parent; the game's priority heap doesn't promise parents first, so it already has to allow that. Not run |
| A destroy | `0x9a2a10` looks the id up first and returns on a miss |
| Acks | per tick, not per entity. The server counts the entity as sent and doesn't resend. That's why a late template is held on the server rather than retried here |
| Ids in components | as for a withheld entity: the client already handles ids it doesn't have. Not audited one by one |

### What can still point at a withheld entity **[dis]**, not run
| Reference | What happens |
|---|---|
| A child entity | withheld with its parent, so the client never gets a missing parent |
| An update message for it | none: updates come from the same heap |
| A destroy for it | it can be sent: type-4 messages list ids from a per-player set (`0x61e9d0`), and whether a never-created entity gets in depends on flag bits this wasn't traced to the end. The reader calls `destroyEntity` for each id (`0x677d72`), which looks the id up first and returns on a miss (`0x63e8e5`..`0x63e90b`). So harmless either way |
| Ids inside components (a follow or guard target, an owner, the NPC directory) | the client already handles ids it doesn't have: entities outside a player's relevance are never sent. No component was audited one by one |
| Knowledge ids | docs/re/quests.md section 4: the client stores ids it doesn't know and uses none. Not run |

### Lab results (no client)
Simulated peers on the lab transport run the real managed decisions, native gate state and timers, and the real entity and template reads. The queue hook and the create guard also ran on the game's own functions, through `lab_selftest` (below). Nothing in a lab sends a real Gameplay Ready, so that hook has only been installed and resolved.
| Config | Result | What it shows |
|---|---|---|
| net-test only | nettest 93/0 | world 12269 templates, kiln.assets 0; gate off; `passed the gate` in 0.10 s under `modSync: off` and `warn`; never judged later |
| 9 mods adding content, net-test | nettest 120/0 (`--game-templates 12269`) | 12279 in the world - 12269 = 10 = `resource_new_count`, listed below; refuse at 5.6 s; let in at 5.7 s under `modSync: off`; a real entity of each is withheld, a Vukah isn't; a Kiln client without `npcsample` and `quests-sample` refused by default with both named and the modpack in the REJECT, let in by a set `warn` or `off` |
| the gate, queue and guard patterns broken in the lab's sigdb | nettest 28/0 (`handshake,echo,vanilla`) | `gate NOT available`, `withholding NOT available`, `no guard on the game's create path`; `vanilla clients are refused: the server has 10 template(s) only Kiln has`; refused under `modSync: off` too; Kiln peers join as before |
| sync-test, bigstacks, assets-test | synctest 35/0, assets-test 8/0 | nothing else moved |
| unit tests, `managed/tests/Kiln.Network.Tests` | 37/0 | policy, gate mode, deadlines, what counts as stating `modSync` |

Guard and resend on the game's own code (the server exe has the same reader and relevance step, so `lab_selftest` calls those on a free player slot, no client):
- reader with a missing GUID: dropped through the real failure branch, empty result, no fault; the same GUID again: dropped, no second log line; a known template whose parent or grandparent was dropped: dropped;
- reader with a known template and no parent: the guard leaves it alone and the reader goes on through the moved call, as far as its message store (rva `0x6747df`), an assert a server world hits because it has no such store; the test catches it. So the found path past the hook has run only up to there, not to a created entity: that's client check 3;
- relevance step: a game entity enters the heap (1), a Kiln entity stays out on a withheld slot (still 1), and after clearing, the next entity brings the kept one along (3).
- Not exercised: the byte that names a joined player's slot. A free slot's is 0xFF; a real player's is read in code only (`0x67d3e1`). Client check 6 shows it through the `withheld=` counter.

The 10: `Kiln_Elite_Vukah` (CreatureTweaks, data pack), `Kiln_Test_Elite_Vukah` (gamedata-test), `Kiln_NpcSample_Tobin`, `Kiln_QuestsSample_Warden` (NPC clones), six `Kiln_Marker_*`. FireNova, NewItems, SpellTweaks and EliteVukah add no template: EliteVukah's elites use the game's. Each elite has 3 children, withheld through the parent walk. Children of other Kiln entities have game templates (`Hookshot_Pull_Entity_To_Player`, `VFX_SubEntity_Toggle_Signal_Link_...`), so the walk is needed. Tobin's spawned copy was removed by the game before it could be read (`removed by the game`); his template is on the list.

### Client checks (launches are on hold: all unproven)
Lab with no `labPort`, real client through `play-once` mode vanilla unless noted, LOCAL characters only.
1. **Vanilla, new character, lab with quests-sample and npcsample** (refuse): loading screen for about 7 s, then the game's dialog, back to the
   menu, no crash, nothing in `crash_dumps`. Lab: `net: peer 0x.. sent Gameplay Ready with no proof of Kiln: held`, `has no Kiln (vanilla
   client): refusing`, `kick ... rc 0`, no `Replication Checkpoint` in the game log, no `Machine '..' logged in`.
2. **Same, a character with the Epic Glacier equipped**: same outcome. The `apply_armor` path needs world data, which never comes.
3. **Kiln client, same lab**: `net: peer 0x.. has Kiln` before `in game`, no `held` line, join and handshake as before, join time unchanged.
4. **Vanilla, `kickReason: 5`**: which text the dialog shows, against reason 1's "An update of the game is available". Keep the less misleading one.
5. **Vanilla, lab with no mods** (baseline): joins and plays, no `held` line, join time as on a server without Kiln.
6. **Vanilla, new character, lab with `modSync: off`, quests-sample and npcsample**: after the hold it joins and plays, no crash; no Tobin, Warden or quest marker in sight;
   `letting it in`; with net-test on the lab, its `gate` control's `withheld=` counter rises. Watch: the loading screen ends (Replication Checkpoint reached, no `Timed Out`); the journal; talking to vanilla NPCs.
7. **Vanilla, a character with the Glacier, same lab**: expected to crash in `apply_armor` (the known limit). Confirm it, and which item state avoids it.
8. **Vanilla and Kiln client together on that lab**: the Kiln client sees Tobin, the vanilla one doesn't, neither crashes. Hot-spawn `Kiln_Elite_Vukah` near the vanilla client: not sent, no crash.
9. **Hosted Kiln world, vanilla guest** if a second account is available: same as 1. The host's own client is never held.
10. **Old Kiln build** if one is kept: refused with Kiln's text, as network.md says.
11. **Kiln client without npcsample and quests-sample, `network.json` with `"modSync": "warn"`, lab with both**: no crash. kiln.log has one `this game has no template` line per template however long you stay, no Tobin or Warden in sight, and the Kiln menu's Network tab lists both mods. Walk past where they stand. Then leave and rejoin: same.
12. **Kiln client without CreatureTweaks, a `Kiln_Elite_Vukah` already alive near the spawn point** (`clients: "kiln"`): no crash, no `this game has no template` line, the elite is there and fights. Lab order: `has the server's definitions after N ms`, then the entity. Join a few times.
13. **Kiln client, all mods**: Tobin, the Warden and markers appear within about a second of the world; note `has the server's definitions after N ms` and whether anything visibly pops in. Quests still work (Q3, N2).
14. **Kiln client, spawn the elite right after a hot reload that adds it**: no crash, the elite appears after `content state ... applied`.
15. **The game's log after 11**: no flood of `processMessage UpdateEntity`, frame rate unchanged near the missing NPC.
16. **Same client, `modSync` not set (the default)**, also with net-test on the lab (no `clients`): Kiln's toast names the server and both mods (not net-test) before the game's dialog and stays over it, then shows again at the main menu; back to the menu, no crash. kiln.log: `ui: refusal shown: ...` and `ui: refusal shown again at the main menu`, `last-server.json: rejected ... missing npcsample, quests-sample`. Lab: `refusing ... quests-sample ...`, `kick`. `kiln/state/last-server.json`: `"outcome": "rejected"`, `missing` the two mods, `required` all three. The launcher's Play page shows the Refused banner without a modpack (Dismiss hides it); with a modpack published on the lab, the banner offers it.

## Not verified / open
1. The per-message maximum at runtime (expected 8192 or 16384 bytes): real-client frames were small (≤ 336 bytes);
   fragments were tested over the lab transport only. Kiln's protocol tolerates reordering (fragments carry indices)
   but expects reliability.
2. The "communication allowed mask" / "block mask" (`[session] Setting block mask of client %d to %08x:%08x`) look
   like per-player communication privacy (32 players, voice/text). Assumed not to gate game channels.
3. Vanilla peers (a Kiln client on a vanilla server, a vanilla client on a Kiln server), `requireKiln`, and the
   vanilla timeout; reason 5 ("Generic") as a less misleading `kickReason`.
