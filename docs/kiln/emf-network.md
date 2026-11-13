# EMF 0.6 mod messages on Kiln

`kiln-emf` supplies EMF 0.6.8's API v40 table. Mod messages are its five v27 functions: `net_channel`,
`net_to_server`, `net_to_player`, `net_to_players` and `net_peer`. EMF's unmodified Lua host loads on this table,
including `emf.net`.

The wire protocol is EMF's `#!emf1` protocol in the game's text chat. A peer can run EMF itself or Kiln. There is no
extra port and a peer does not need Kiln's network protocol. The server uses `kiln.chat`; the client hooks the game's
chat outbox and receive function. Marked lines are consumed before chat commands or the chat display. Hello/welcome
advertise `0.6.8-kiln` as the implementation and API 40.

The greeting (EMF 0.6.8's timing and text): a server with a mod channel greets each new connection 10 s after it
appears and once more 35 s later, with a chat line a game without EMF or Kiln shows ("This server runs EMF mods. Get
EMF to see their windows; you can play without it."). A greeting that reaches a game still loading, before it knows
its own account, is held and answered once it does (EMF 0.6.2; Kiln dropped it before), unless it was typed by
another player.

When a Kiln game takes marked lines (the greeting among them):
- from the game's start, so a greeting that comes while the first joined game still loads is taken and held;
- from each client world's start on. The world's start keeps a held greeting: it is the same connection;
- never between a world's stop and the next client world's start. What a stopped connection still sends is never
  taken for the next one (the adapter has no identity of a connection from the game to tell them apart). On a join
  after the first one in a game run, a greeting that comes before the client world starts therefore stays an ordinary
  chat line and is not answered; the server's second greeting, 35 s later, is answered when the world has started by then.

## The mod check on join (EMF 0.6.3)

EMF's core channel `emf/mods`, with EMF's own code (`native/plugins/emf/src/net/mods_check*.h`):

- **A Kiln game that joined a server** sends the list of its running EMF mods when the server has the channel, one
  exchange per session (while no answer comes it is sent again every 5 s: six sends at most, the first and five more): `e emf 0.6.8-kiln`, then one line per mod, `kind id version`, the kind from the mod's `mod.json`
  `side` (`b` both, `s` server, `l` client, `u` none). The server answers with its list. When the game lacks a mod
  the server's players must have (the server's `c` content and `b` mods, by id and version), the player gets one
  note per session ("Missing mods for this server ...: ... Install the same versions in the Kiln launcher.") and
  `kiln.log` has the difference (`EMF mods check: against the server's mods: ...`).
- **An EMF server set to refuse** (`[net] mods_check=refuse` in its `emf_core.ini`) kicks a game that sends no list
  within 60 s. A Kiln game now answers, so it is treated as an EMF game is: refused only when it really lacks a
  required mod.
- **A Kiln server** logs each game's list with the differences (`EMF mods check: player ...`) and answers with its
  own, so an EMF game can tell its player what is missing.

Limits:
- Kiln's list has no content entries (`c`: the EML content mods a game's data was built with; on Kiln that is
  kiln-eml's work). A server whose list has content mods sees them as missing from a Kiln game, although the game may
  have the same content: the player gets the note, and a server set to refuse refuses the join.
- A Kiln server greets only when one of its mods has a channel: the core's channel alone does not make it greet
  (EMF greets then when it has mods players must match). It never refuses a join; `mods_check`, `mods_required` and
  `mods_info` are not read.
- Checked by host tests against EMF's own unmodified wire code as the other end (both directions, the held
  greeting, a forged greeting, a new session). Not seen with a real joined game: see the client checklist in
  [emf.md](emf.md).

## Channels

Register a channel once from a mod's load function:

```cpp
EmfNetChannel c{};
c.size = sizeof c;
c.name = "my_mod/pick";
c.version = 1;
c.flags = EMF_NET_TO_SERVER;
c.on_server = on_pick;
api->net_channel(&c);
```

Only that mod can send on its channel. The Lua host can register its scripts' channels, as in EMF. A peer must first
advertise the same channel version. The receiver learns the player's entity and account from the server's connection
records; data in a message cannot set the sender or admin flag. Server sends require a server-world callback.
`net_peer` is a snapshot query and may run from any thread.

The default message limit is 4096 bytes (at most 16384) and the default channel rate is 100 messages per ten seconds.
The unchanged line format supports text and binary, at most 24 fragments of 1024 bytes. The upstream link handles
discovery, sequence numbers, duplicate rejection, channel versions and line/byte rates. Kiln also expires partial
messages, queued callbacks and server output after 30 seconds. Its callback queue holds at most 1024 entries; the
server output holds at most 256 lines per player and 4096 total. A whole message is accepted or refused together.
The server holds output while that player's chat reader has more than 16 KiB pending; other players still receive
their output. The service remembers at most 128 distinct rejection diagnostics per world session.

Callbacks run after the destination world's step, under the mod fault guard. A fault disables that callback. World
teardown drops queued messages, welcomes, fragments and peer state. A queued message from a disconnected player does
not follow that account to a new connection. Plugin shutdown waits for entered callbacks and client engine calls,
unsubscribes the chat receiver, and revokes the server observer. The client hooks are kiln.hooks links that stay in the
chain but pass calls through; Kiln never unhooks the function, so a foreign hook layered above it keeps working.

`kiln.log` reports whether each chat endpoint is on. A missing service, unresolved signature or unknown client layout
leaves that endpoint off; host loopback still works. Client engine access requires an exact reviewed executable hash
(svn 1273043, 1285601 or 1287369), in addition to the resolved signatures. In `emf/emf_core.ini` (or its Kiln config
copy), `[net] enabled=0` disables the remote endpoints. `[net] greet=0` disables server beacons. The beacon can be
visible to a player without EMF, as in EMF itself.

Source contract: [EMF 0.6.8 reference](https://github.com/matenm/emf-enshrouded/blob/v0.6.8/docs/modding/reference.md),
[public header](https://github.com/matenm/emf-enshrouded/blob/v0.6.8/sdk/include/emf/emf_api.h), and
[MIT protocol implementation](https://github.com/matenm/emf-enshrouded/tree/v0.6.8/src/core/net).
The copied implementation is attributed in `native/plugins/emf/src/net/`; its license is `licenses/EMF-LICENSE.txt`.

`native/plugins/emf/tests/run.sh` checks the frozen ABI, production adapters, upstream wire peer, binary fragments,
spoofed senders, direction/ownership/rate limits, backpressure, world teardown and shutdown, and the mod check
with EMF's own client link and server service as the other end. Its client-hook fixture
uses the production detours with stand-in engine calls. A dedicated-server probe is built with
`native/build.sh emf-net-probe`. These checks do not demonstrate an actual joined client: the live client/server
handshake, hidden chat, Beastmaster taming, stable interaction and per-player save/rejoin remain client checks.
