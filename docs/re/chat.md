# Text chat on the dedicated server (kiln.chat)

Current build: svn 1285601 (exe sha256 `c5c9fe50c6398d2a…`); every definition also resolves on 1273043. Sources:
EMF 0.3.3 (MIT) signatures 34 ChatReceive / 35 ChatLogAdd and `docs/engine_parts/re_chat.md` (its server section was
confirmed in game by EMF on 1273043), re-derived here with capstone; live checks in lab `chat` (no client).
Definitions: `sigdb/symbols/server.d/chat.json`. Service: `native/services/chat/chat.cpp`.

## Path of a line
```
client UI  queueChatMessage (client only; not in the server exe)
  -> GameClient outbox -> ChatMessageClientMessage {u32 type; string[0x400]; tag1; tag2}
server GameServer frame 0x7bbeb0 (the tick thread):
  0x7bc4d2  ChatReceive(GameServer)           receive loop for all client messages
              0x7be649  ChatLogAdd(&res, GameServer+0x60 sys, type, &text, tag1, tag2, connHandle, account, &{0, has=0})
  0x7bcb14  runSimStep -> SimStep (Kiln Pre / step / Post)
  0x7bd2ac  ChatSend(GameServer) 0x7ba850     for each of 17 slots at GameServer+0x16A0 {u32 peer; u8 flags}:
              reader = peer & 0x7F, gen = (peer >> 7) - 1
              ReaderNext(sys, &cursor, reader, gen) 0x930a00  -> next inbox line whose mask bit is clear
              ChatMessageServerMessage {type, text, tag1, tag2, playerHandle, accountHash} -> that connection
              MarkSent(sys, line, reader, size) 0x94eda0
client  message 0x13 -> chatLogAdd on the client's own chat system -> chat UI
```
The sender's own line only appears through this echo (the send loop includes the sender's reader).
There is no text-chat gate on the server's receive path (`0x9a77f0` at `0x7be4fb` is the message deserializer);
the client gates sending on `CommunicationSetup.isTextChatEnabled` from ServerInfo (EMF), i.e. `"enableTextChat"`.

## Functions (RVA current / 1273043)
| Name | 1285601 | 1273043 | Notes |
|---|---|---|---|
| `chat/ChatLogAdd(res, sys, type, text*, tag1, tag2, handle, account, time*)` | `0x915200` | `0x90b330` | text <= `0x400` bytes else `res.code = 0x12`; appends to the inbox, evicting the oldest when the pool is full |
| `chat/ServerReceive(GameServer)` (EMF ChatReceive) | `0x7bdb20` | `0x7b45b0` | hooked: GameServer pointer each frame |
| `chat/ReceiveAddLine` (the call in it) | `0x7be649` | `0x7b50d9` | its return address identifies a player's line in the ChatLogAdd hook |
| `chat/MarkSent(sys, line, reader, size)` | `0x94eda0` | `0x944d90` | `mask |= 1<<reader; reader.pending -= size` |
| ReaderNext (probe only) | `0x930a00` | `0x926a20` | a new generation in a slot marks every old line read |
| ChatSend (not hooked) | `0x7ba850` | | the send step above |

Other ChatLogAdd callers (passed through untouched): `0x7bff92` join notice (type 4, tag `0xec53702d` / `0x2fec0cdf`,
speaker = the joining player), `0x7ad9f3` leave notice (type 4, tag `0xc762ab66`), `0xcfd8b` an ECS program's type-1
line (loca tag from its data, speaker = a player record).

## ChatMessagesSystem (`*(GameServer+0x60)`)
- 32 readers at `+0x0C + i*0x0C`: `+0` u8 active, `+4` u32 generation, `+8` u32 pending bytes.
- Inbox: `+0x1C0` head, `+0x1C8` tail, `+0x1D0` count (pool `+0x190`).
- Line: `+0x00` u8 type, `+0x04` text offset (0x3C -> text at `+0x40`), `+0x08` u32 length, `+0x0C`/`+0x10` loca tags,
  `+0x14` speaker connection handle, `+0x18` speaker account hash, `+0x20` u32 reader mask (bit set = not to be sent),
  `+0x28`/`+0x30` prev/next, `+0x38` time. Size accounted per reader: `align4(len + 0x17)`.
- ChatLogAdd sets the mask bit of every inactive reader and adds the size to every active reader's pending count.

## Connection records (`ServerPlayers`, `GameServer+0x1E0`, 16 x 0x148 B)
Index = `handle & 0x3F`, record `+0x00` must equal the handle. `+0x04` peer handle (reader = `peer & 0x7F`),
`+0x0A` u8 state (2 -> 3 at "Machine '%u': Player '%k' logged in", later 6), `+0x10` u8 permission bits, `+0x18`
player entity (an ECS program compares it with CurrentEntityId at `0xcfd28`), `+0x20` name `{char*, u64 len}` (the
platform name the chat shows), `+0x48` account hash.
Permission bits (enum table at `.rdata 0x1115a60`: name, value): 0 CanKickBan, 1 CanAccessInventories, 2 CanEditBase,
3 CanEditWorld, 4 CanExtendBase, 5 CanReceiveEXP; the "logged in with Permissions" log tests them in this order
(`0x7bfbca`...).

## What Kiln does
- **Receive.** Hook ChatLogAdd; when the return address is `ReceiveAddLine + 5`, the type is 0 and someone subscribed,
  copy the line, report success without adding it, and deliver it in the next Pre (same frame: receive runs before the
  sim step). Subscribers may consume it; otherwise Kiln calls the original ChatLogAdd with the original arguments.
  Nothing changes when nobody subscribed. Live (injected lines through the same queue, connection 0):
  ```
  chat: hooks on ChatReceive rva 0x7bdb20, ChatLogAdd rva 0x915200 (receive call rva 0x7be649); receive on, send on, players on
  chat: GameServer at 00000000ACC7CEC8
  chat: chat system 00000000A686BC28 ready: inbox 0 lines, 0 active readers, 0 players connected
  PASS chat.receive.passed-on: inbox newest: 'chat-test hello 82011' (type 0, reader mask 0xffffffff); inbox 0 -> 1
  chat: line #2 from ? (entity 0, connection 0x0) consumed: chat-test drop-me 82011
  command /kt-echo by connection 0x0 (entity 0): ok
  ```
  (mask 0xffffffff: no reader active, nobody connected.)
- **Server lines.** A line with speaker handle 0 / account 0 is sent but not shown by clients (EMF, in game), so for
  each recipient Kiln adds a type-0 line spoken by that recipient (their handle and account) and calls the game's
  MarkSent for every other active reader whose bit is clear. Each recipient sees "<their name>: <text>"; Kiln.Chat puts
  "[Server] " in front. EMF's in-game test of the same scheme on 1273043 showed "<name>: [Server] ...".
- **Lines as a player** (`say`): spoken by a connected player, optionally narrowed to one reader.

## Unverified here (needs a client)
- Display of narrowed recipient-spoken lines on 1285601, of `say`, of the "Unknown command" reply.
- Whether a type-1/type-4 line with free text (no loca tag) would show as a system line without a speaker. Not used.
- Real player lines through the hook (only injected ones went through the queue; the hook's address check and the
  receive call site were matched statically on both builds).
