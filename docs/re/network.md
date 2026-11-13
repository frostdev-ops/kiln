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
  message's template GUID up in the template map (`R+0x745810`, registry `R=*(W+0xe179c0)`; dev.28 layout
  clarification). A miss leaves `r14` = 0. It then calls
  `0x987910` without a test, which reads `[r14+0x28]`, `TemplateInfo`'s component bitset: `ACCESS_VIOLATION` reading 0x28.
- **The message** (corrected 2026-11, dev.28 RE [static] [lab]): `u32 (serverTick << 3)|kind`, **`u32 seq`**,
  `u32 entity`; create then has `u32 parent`, `GUID[16]`, `u8 0`, `u16 length`, payload. Kind 2=create, 3=update.
  The first word is **tick, not sequence**; the following u32 is the acked message sequence. Written at `0x62c886`
  in `0x62c2e0`; see the current replication section below for updates, baselines and destroy lists.
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
- Corrected 2026-11 (dev.28 RE [static] [lab]): objects are indexed by **machine index `peer & 0x7f`**, not
  player slot: `obj=*(world+0x15fd2a8)+machine*0x125850`. Object `+4` is player slot; **client context `obj+8`**
  starts with that slot, and prediction context is `obj+0x92c18`. `0x67d3e1` compares the context's slot with
  `entity-1` to give the player's own entity priority 0x20d. See current layouts below.
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
- Templates added after a clear **[lab]**: a clear lists kiln.assets' Kiln-only templates (`resource_list`, filtered by `resource_is_new`) and keeps that set and
  `resource_new_count` for the slot. While the count is unchanged, a cleared slot costs the hook a few loads. Once it grows, the hook walks the entity's
  templates as for a withheld slot and keeps back those outside the set, until the next clear. The count is read at most once per step: the step number moves
  in every server Pre and in the receive hook. Kiln adds templates on the tick thread, when no world job runs, so the first enqueue after one reads the new
  count. Lab, CreatureTweaks hot-loaded with three fake clients bound to free slots: the elite was spawned 57 ms after its template was registered, in the
  first step it could be. It stayed out of the slots' heaps, and went in with the next entity after the ack of the state that defines it. A game entity and an entity of an
  older Kiln template went in meanwhile.

### The guard on a Kiln client **[dis] [lab]**
- `net/CreateTemplateCall`, a mid hook on the reader's `call 0x987910` (`0x961d07`): `r14` is the `TemplateInfo`, 0 on a miss, the GUID is in `xmm6`, the entity and parent at
  `rbp+0x3cb0`, `rbp+0x3cc0` (captured in `@net.create_fields`).
- Dropping resumes at `0x961d18`, the branch the game takes when that call returns nothing: it frees what the argument set up, returns an empty result, and `0x9c70ee` discards the
  message. Hooking the miss branch itself (`0x961cc9`) would have cost nothing on a hit, but `0x961ccc` is a jump target inside the 5 bytes the hook overwrites.
- Cost: the hook's stub plus one test per create, and a kiln.assets `resource_is_new` lookup for a found template (the first entity of each Kiln-only template is logged: `net: this game creates entity N of the Kiln template <guid> (TemplateInfo ...)`); a second test, and a set lookup for a create with a parent, only after something was dropped on this connection.
- `0x987910`'s other callers: `0x990645` is the hosted server's writer, with a template it read from a live entity. `0x96256d` is an update that carries a GUID, which is only reached once the
  reader has found the entity (`0x96235e` returns early otherwise), so its template is known. Not guarded.

| What follows a dropped create | What happens |
|---|---|
| Later updates | the update reader `0x962260` looks the entity up first and returns empty on a miss (`0x96235e`). Discarded before `processMessage`, so its `Entity %,u not found` line doesn't fill the log |
| Children | dropped too, by parent id, grandchildren included. A child that arrives before its parent is dropped stays, without a parent; the game's priority heap doesn't promise parents first, so it already has to allow that. Not run |
| A destroy | `0x9a2a10` looks the id up first and returns on a miss |
| Acks | Corrected dev.28 [static] [lab]: acks name **message sequence numbers**, separate from the header tick. The server records per-peer state/baselines; a dropped create does not request the missing template. Hold late templates on the server rather than relying on this guard to retry |
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
    Lab: `net: peer 0x.. (slot N): entity E of the Kiln template 6b1e0c52-..., added after its last content ack, waits for its next one`,
    then `<player>: content state 623e3255 applied`, then `net: peer 0x.. cleared again: ... 1 kept-back entity goes out`. Client: the
    `template Kiln_Elite_Vukah ... registered in the client world` line comes before any line about that entity, and there's no
    `this game has no template 6b1e0c52` line.
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


## Replication pipeline (dev.28; server 26430253 / client 26430625)

S `f0d824be…`, C `5d9ef2f1…`; C send-side addresses are the hosted server copies. Source: dev.28 dossier chapters
11/15, replication REPORT/VERIFY: 24 confirmed, six confirmed with corrections, no refuted claims. Corrections to
maxMsg width, Merge, StateIndex, state trailers and GUID hashing are included here. `[seen]` is coordinator-run
sessions 2 (hosted) and 4 (joined); no new game run is implied.

### Frame, jobs and ownership [static] [lab] [seen]

Server frame: ServerReceive → simulation → **WaitWorldJobs** → apply queued acks/reset empty machines →
RelevanceDriver (wait) → SendStep/Prepare (wait for per-player/channel writer jobs) → RunBatches (wait for send
callbacks). QueueReader, EntityWriter, Snapshot, DeltaWriter and SendCallback run on world job threads, after sim
jobs finish, with the tick thread waiting. Different players/channels are concurrent. Hooks may read the quiet world
and modify only their own per-(machine,channel) buffers/snapshot; no tick-only API or shared mutable pool without
synchronization. Client receive/store/decode/process/merge/apply all ran on the client's main thread; hosted server
world had its separate GameServer thread.

| Signature name / Win64 contract | Server | Client | Evidence |
|---|---|---|---|
| `repl/SendStep`: `void(GameServer*)` | `0x7bb770` | `0xe5f330` | [static] [lab] |
| `repl/Prepare`: `void(Batches*,World*,const Items*,u8 alsoPrediction)` | `0x62b390` | `0x98f500` | [lab] |
| `repl/PrepareInner`: same, Items by-value pair | `0x62bc80` | `0x98fdf0` | [static] |
| `repl/QueueReader`: `void(World*,Ctx*,u8 machine,u64 maxMsg,u8 pred)` | `0x62b3c0` | `0x98f530` | [static], width corrected |
| `repl/EntityWriter`: `bool(WriterOut*,const WriterArgs*)` | `0x62c2e0` | `0x990450` | [lab] |
| `repl/Snapshot`: `StateRef*(out,SnapArgs*)` | `0x622d50` | `0x986ec0` | [static] [lab] |
| `repl/SnapshotReturn`: post-call site, RAX=&StateRef | `0x62c530` | `0x9906a0` | [static] |
| `repl/TemplateState`: `StateRef*(out,TArgs*)` | `0x6237a0` | `0x987910` | [lab] |
| `repl/StateIndex`: `void(u8 index[0x7d0],const State*,Registry*)` | `0x64bbd0` | `0x9afd40` | [lab]; **zero output first** |
| `repl/DeltaWriter`: `bool(BitWriter*,Registry*,snap,base,u8 templateFlag,const State** baseRef,u8 pred)` | `0x6214a0` | `0x985610` | [lab] |
| `repl/GenericDelta`: `bool(u8*out,u64*outLen,u64 cap,const u8*snap,const u8*base,u64 size)` | `0x549530` | `0x889a40` | [static] [lab via decode] |
| `repl/Decode`: `StateRef*(out,Alloc*,const Slice*,base,u8 pred,Registry*,u32 tick,void* zero)` | `0x6002e0` | `0x9646d0` | [lab] |
| `repl/RunBatches`: `void(World*,Batches*,void(*cb)(CbPair*),void* user)` | `0x64dad0` | `0x9b1c40` | [static] |
| `repl/SendCallback`: `void(CbPair* {GameServer*,Batch*})` | `0x7adb70` | `0xe51e90` | [lab] |
| `repl/RecordSent`: `void(World*,const MsgEntry*,u64 machine,u8 pred)` | `0x64d620` | `0x9b1790` | [lab] |
| `repl/AckReceive`: `void(World*,u8 machine,const Slice*,u8 pred)` | `0x6620f0` | `0x9c6140` | [lab] |
| `repl/AckProcess`: `void(World*,u8 machine)` | `0x677080` | `0x9db190` | [lab] |
| `repl/AckContext`: `void(Ctx*,const U32List*)` | `0x677500` | `0x9db610` | [static] [lab] |
| `repl/EcsReset`: `void(World*,u64 machine,const Slice* ids,u8 pred)` | `0x689920` | `0x9ed780` | [lab] [seen] |
| `repl/ResetMachine`: `void(World*,u8 machine)` | `0x686af0` | `0x9eab50` | [lab] |
| `repl/RelevanceDriver` / `RelevanceStep`: `void(World*)` / `void(Ctx*,World*)` | `0x6c8c10` / `0x676e40` | `0xa2c360` / `0x9daf50` | [lab] |
| `repl/MarkReplication` | `0x66f2c0` | `0x9d33d0` | [static] [lab via tracked writes] |

Items = `{Item12* ptr,u64 count}`; Item12 = `{u8 machine,u8 slot,u16 pad,u32 max4,u32 max5}`. The maxima from
S `0xa01ba0` are **message-size limits**, not bandwidth. Zero-extend the u32 into QueueReader's u64 argument.
Batch 24 B: MsgEntry* `+0`, count `+8`, machine `+0x10`, pred `+0x11`, sent u32 `+0x14`. MsgEntry 32 B: type byte,
seq u32, data pointer, size u64, trailer pointer. Entry type 4 means entity payload; 5 destroy list; 0/2 frame/handshake.
Do not confuse these with header kinds.

World W: Storage `W+0xe179c0`, registry `R=*Storage=W+0x928`, server tick u32 `W+0xe17e9c`, replicated player-slot
mask u16 `W+0x15d3c90` (Storage `+0x7bc2d0`). Objects pointer/count `W+0x15fd2a8/+0x15fd2b0`: **17 objects,
stride 0x125850, indexed by machine**, object active `+0`, slot `+4`, client context `+8`, prediction `+0x92c18`.
This slot mask is not the scheduler live-player mask `storage+0x7ddc0a`. [static] [lab]

Context: slot `+0`, handshake-acked `+1`, handshake seq u32 `+4`, message list `+0x10`, to-write ring `+0x48`,
entity state map `+0x858`, priority heap `+0x8f0`, sent-message map `+0x70918`, state pool `+0x8da20`, unchanged
counter `+0x8db78`, next seq `+0x8db9c`, newly relevant entries `+0x8dba0` (stride 0x14, count u16 `+0x92ba0`).
PerPlayerState 0x20 B: created/acked `+0`, flags `+1` (8=in heap), enqueue tick `+4`, versions `+8/+0xc`, baseline
A/B pointers `+0x10/+0x18`. Before handshake ack only the type-2 handshake is emitted every 60 ticks. [static] [lab]

Entity Record: id `+0x10`, parent `+0x14`, archetype `+0x18`, chunk `+0x20`, TemplateInfo `+0x28`, chunk index
`+0x30`, whole-set row u16 `+0x36`, **network row** u16 `+0x38`. Network-row table Storage `+0xb0`, stride 80 B.
Registry `R+0x745810` template map uses GUID's first u32 **as hash input**, masks by cap-1 then linear probes with
full-GUID compares; it is not the final slot. Registry replication-bit mask `+0x190` had 98 components, versioned
mask `+0x1e0` 112; codec slots `+0x118` had five custom-coded components. These are lab counts, not array widths.

### Writer state and borrowed memory [static] [lab]

WriterArgs is **0x48 B**, callback-local: stream** `+0`, World* `+8`, Storage* `+0x10`, WriterEntry*
`+0x18` (`{u32 entity,u8 kind}`), Ctx* `+0x20`, zero u64 `+0x28`, maxMsg u64 `+0x30` (0=unlimited), tick u32
`+0x38`, seq u32 `+0x3c`, player slot byte `+0x40`, pred byte `+0x41`. Kind 0=create/template baseline;
1=acked A; 2=newest B/A/template. QueueReader chooses kind 1 when `(tick+entity)&3==0`, else 2 for updates.
WriterOut `{Record*,State*,u8 skipped}`: false=entity absent; true/skipped=1 means unchanged or too large (only
unchanged increments `ctx+0x8db78`); true/skipped=0 appends a message. Call original once, then inspect output.
The resulting snapshot becomes a baseline only after ack. Scratch `temp=1` snapshots are sent but not retained.

SnapArgs: `{Alloc* playerPool=ctx+0x8da20,Holder* temp,Registry*,Storage*,u32 tick,Record*,u8 pred}`;
TArgs `{Holder*,Registry*,TemplateInfo*,u8 pred}`. StateRef `{State*,u8 temp}`. For direct TemplateState/Decode,
use a caller-owned Alloc object: vtable `+8 alloc(self,u64 size,u64 align,u64 flags,u64 tag)`, `+0x10 free(self,p,0)`.
Do not take a player's pool outside its jobs. Holder `{Alloc*,blocks[4],u8 n,pad[7]}` is zeroed/one-call-only;
a fifth allocation is fatal. Free nonreturned tracked blocks and later the returned state through that Alloc.
Decode owns its new state, borrows baseline, returns null on failure; empty payload copies baseline. StateIndex
**must be zeroed before each call**: sparse decoding ORs mask bits and leaves absent entries unchanged.

### Channel sets, tracked writes and wire format [static] [lab]

Snapshot **client channel**: entity network row `Record+0x38`, filtered by `usage&7`; **prediction**: archetype mask
minus registry `+0x140`, filtered by `usage&0x38`. Template defaults use a **different** rule: client `usage&0x10 &&
usage&1`, prediction `!no_prediction`. The can sent ItemState, NetworkFuel, **UsedItem** on client channel;
prediction also sent Fuel, FuelSetup, OwnerRelationship. The 98 replication-bit components are not the whole wire set.

WriteOther → WriteMark S `0x66f170` → MarkReplication marks an entity when slot mask!=0 and type has its replication
bit. The **client channel** re-snapshots only when the entity version moves; a sent component without that bit can
be stale. Prediction compares the **current world tick** every tick, so this staleness warning is client-channel-only.
Tracked Fuel writes yielded derived NetworkFuel updates. A byte-neutral write_other touch through a valid declared
replication-bit carrier produced **10 touches → 40 re-snapshots on four fake players**, zero without. It changes no
component bytes. Four carriers covered all 2819 entities in that lab, not necessarily every future archetype.

Entity header: `u32 (tick<<3)|kind; u32 seq; u32 entity`. Create kind 2: parent u32, GUID[16], byte 0; update kind 3:
baseline u32 (bit 31=acked A), GUID only when baseline=0. Then u16 length/payload. Tick is server tick; seq is what
acks name. Client stale rejection uses last tick for `entity&0x1ff`. [lab] 30/30 captured payloads parsed exactly.

Payload LSB-first: three present flags; removed/added/changed counts 6/6/8 bits; 10-bit component indices,
added/changed version-present bit plus optional u8 version; changed adds u16 `(size<<1)|isDelta`. Byte-align, then
raw/custom/generic-XOR-delta blobs. Generic delta encodes unchanged and changed run counts (`0xff` extensions),
XOR bytes; if not smaller than raw, sends raw.

State blob: `{u16 size,u16 dataComponentCount,u32 tick}`, list of u16 indices (optional u8 version), then **whole
component set** (not just fields carrying bytes): fewer than 40 uses `u8 0,u8 count,u16[count]`; 40..640 uses
`u8 1,80-byte mask`; then per-type-aligned data. StateIndex zeroed 0x7d0 B: u16 data offsets `+0x280` from state+8,
whole-set mask `+0x780`. The older small-list-only description was incomplete (VERIFY claim 13).

Destroy header kind 4: tick/kind, seq, `u8 nA,u8 nB`, then u32 ids. A=destroyed, B=left relevance; client reasons
0/2 respectively. Writer S `0x61e9d0` / C `0x982b40` reads context destroy set `+0x92bb0`, flags `+0x92be8`,
up to 127 ids/message and ten messages/frame. [lab] Chest take-out produced lists on both channels; [seen] joined
session counted 473 reason-0 / 5508 reason-2 destroys, relevance bursts confirming B. Ack format:
`u8 0,u32 firstSeq`, then delta byte 1..254, `0xff n` consecutive, or `0x00 u32` explicit. EcsReset makes the next
message a create. Full message/codec listings stay in chapter 11.

### Client apply paths: channel 4 is not Merge [static] [seen]

| Function / contract | S copy | C |
|---|---|---|
| ClientStore: `void(World*,const Slice*,u8 pred)` | `0x662960` | `0x9c69b0` |
| CreateReader / UpdateReader | `0x5fd6e0` / `0x5fddb0` | `0x961b90` / `0x962260` |
| StoreState: `bool(Alloc*,StateTable*,u32 entity,State*)` | `0x5fcd90` | `0x961240` |
| ProcessMessages: `void(World*)`, **both loops** | `0x678db0` | `0x9dcec0` |
| ProcessClientMessage: `void(World*,ClientRecord* {u32* msg,u64})` | `0x6776e0` | `0x9db7f0` |
| ApplyClientState: `bool(Storage*,Record*,const State*)` | `0x6c17f0` | `0xa24f40` |
| ProcessPredictionMessage: `void(PredStorage*,ClientRecord*)` | `0x6785c0` | `0x9dc6d0` |
| Merge: `void(Alloc*,Registry*,MergedState** retained,u32 tick,const State* incoming)` | `0x61a2c0` | `0x97e430` |
| ApplyMap / ApplyEntity | `0x6c4790` / `0x62f260` | `0xa27ee0` / `0x9933d0` |
| StateAt | `0x64f470` | `0x9b35e0` |
| createEntityWithId | — | `0x987e50` |
| DestroyEntity: `bool(Storage*,u32 entity,u8 mode,u8 skipReplication)`: 0/1 in AL only, upper RAX unspecified; mode 2 = unload (no removed bit, event kind 1, players' destroy-list B; compares S `0x63e95d`/`0x63ea82`/`0x63eb1a`/`0x63ec6f`), other = real destroy (removed bit, event kind 2, list A); skipReplication non-zero skips queuing the id into every player's destroy sets (S `0x63ea90` `test r15b,r15b`, C `0x9a2c00`) (corrected from u64: `mov al,1` S `0x63ede8` / C `0x9a2f58`, `xor al,al` S `0x63edec` / C `0x9a2f5c`, no later RAX write) [static] | `0x63e8a0` | `0x9a2a10` |

ClientStore decodes immediately using template/prior baseline and StoreState retains up to **32 states/entity**
(oldest freed via Alloc). Bucket map `W+0x15f5788`, stride 0x58, client records `+8`, prediction `+0x30`.
First packet initializes client tick to **received tick+10**, not ten ticks behind. VERIFY corrected that earlier
inference. [seen] Joined session measured client tick 1–6 ahead of newest received server tick (median 3), with
join/respawn spikes: client messages applied in the first reached step after arrival, not a ten-tick playout buffer.

- Channel 4: ProcessMessages → **ProcessClientMessage** → createEntityWithId/StateIndex for creates,
  StateAt/**ApplyClientState** for updates, DestroyEntity for destroy lists. Session 4: 12288 creates + 219714 updates
  = 232002 channel-4 decodes; ApplyClientState also ran for some creates. No channel-4 Merge path.
- Channel 5: ProcessPredictionMessage → **Merge** into prediction map `+0x7df588` (0x28 B entries) → ApplyMap →
  ApplyEntity. ApplyMap forwards its input **+0x140**; Merge's third arg is a retained-state **pointer-to-pointer**,
  not a state value. Session 2 Merge counts tracked channel-5 decodes. Interpolation was not decoded in this research.

Ids are u32 server ids, preserved by client creation. Bit-31 ids are **world-group**, not a client-local range:
`group=(id>>20)&0x7ff`, `index=id&0x3ffff`. Two appeared on the real prediction channel; pure client-created id
allocation is still open. Prediction carried a small control/touch-related set; observations do not identify every
member or establish a distance-only policy.

[seen] The host's own client **does use the session replication pipeline**, as machine 0: hosted SendCallback and
ClientStore totals matched across shutdown boundaries (136137 sent / 136133 received channel-4 messages). It does
not bypass replication by directly reading server-world components. Dedicated machine 0 has no player and receives
none. Snapshot filters/baselines therefore also matter to the host; channel-12 routing is a separate transport choice.

### Schema and receive sites (chapter 15) [static] [lab] [seen]

`repl/SchemaEncode` S `0x9a8180` / C `0x11f6420`: `void(BitCursor*,const void* msg,const Schema*)`;
SchemaDecode S `0x9a77e0` / C `0x11f5be0`: `bool(void* out,BitCursor*,const Schema*)`.
BitCursor 0x30 B: parent u64 `+0=0`, data pointer `+8`, capacityBits `+0x10`, positionBits `+0x18`, zero u64 `+0x20`,
error byte `+0x28`; size rounds `(positionBits+31)/32*4`. Schema fields are **0x40 B**, not 0x20: name `+0`, type
u32 `+8`, element parameter `+0xc`, shape `+0x10`, data offset `+0x18`, present flag/array count offset `+0x20`,
fixed count u64 `+0x28`, nested schema `+0x38`. BanListUpdate has bans/changeCounter/totalBanCount; its fixed struct
is 0x4c10 B with inline arrays, no decode heap. Field types 9..0xb remain unconfirmed; no fabricated full schema codec.

Dispatch/free sites (machine, player respectively):
- S dispatch `0x7bdc54/0x7be252`, free `0x7be0e4/0x7be74d`;
- hosted C dispatch `0xe61757/0xe61cd6`, free `0xe61827/0xe621a5`;
- client C ClientReceive `0x874ed0`, dispatch `0x874f7e/0x875704`, free `0x875641/0x875acd`.
At dispatch RAX=channel. Server read result `rbp-0x80` (data `rbp-0x68`, size `rbp-0x60`), client `rbp-0x30`
(data `rbp-0x18`, size `rbp-0x10`). Server machine peer handle `[rdi]`, player handle EDI. Free-site jump is the
natural drop path, but **drop/rewrite was not exercised**; only observation hooks were run. [static] [lab] [seen]

Current loop channel membership agrees with the earlier tables; 17 is read elsewhere, 14 has no socket. Session 4
saw all 53510 ch4/5 ack messages on server dispatch and **no ch12** dispatch on either side: Kiln drained it first.
Item-id coverage is **67 sites in 26 components [refl] [lab registry]**, not the earlier 16-site subset; include
UsedItem and prediction-channel components (full census in chapter 15, not copied here). Vanilla tolerance of
remapped ids remains untested.

Scratch signatures: SnapshotReturn is an **ordinary address symbol** with scoped pattern/offset, not
`@repl.snapshot_call` (capture-only probes cannot yield hook addresses). Updated checks: server 67/67, client 72/72;
old server `d7baa768…` 65/67 (ProcessMessages/ApplyEntity moved). Full proposals, loopback evidence (10004 NetworkFuel
byte comparisons, 0 mismatches; identical creates with/without observe hooks), hook contracts and sidecar designs
stay in chapters 11/15. No new sidecar/filter/API implementation is implied by these notes.
