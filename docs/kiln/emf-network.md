# EMF 0.6 mod messages on Kiln

`kiln-emf` supplies EMF 0.6.0's API v27 table (728 bytes). The five new functions are `net_channel`,
`net_to_server`, `net_to_player`, `net_to_players` and `net_peer`. EMF's unmodified Lua 1.19 DLL loads on this table,
including `emf.net`. Beastmaster 1.13 runs through that Lua host.

The wire protocol is EMF's `#!emf1` protocol in the game's text chat. A peer can run EMF itself or Kiln. There is no
extra port and a peer does not need Kiln's network protocol. The server uses `kiln.chat`; the client hooks the game's
chat outbox and receive function. Marked lines are consumed before chat commands or the chat display. Hello/welcome
advertise `kiln-api27` as the implementation and API 27.

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
The server holds output while that player's chat reader has more than 16 KiB pending.

Callbacks run after the destination world's step, under the mod fault guard. A fault disables that callback. World
teardown drops queued messages, welcomes, fragments and peer state. A queued message from a disconnected player does
not follow that account to a new connection. Plugin shutdown waits for entered callbacks and client engine calls,
unsubscribes the chat receiver, and revokes the server observer. The client detours stay mapped but inactive, so a
foreign hook layered above them keeps its trampoline.

`kiln.log` reports whether each chat endpoint is on. A missing service, unresolved signature or unknown client layout
leaves that endpoint off; host loopback still works. Client engine access requires an exact reviewed executable hash
(svn 1273043, 1285601 or 1287369), in addition to the resolved signatures. In `emf/emf_core.ini` (or its Kiln config
copy), `[net] enabled=0` disables the remote endpoints. `[net] greet=0` disables server beacons. The beacon can be
visible to a player without EMF, as in EMF itself.

Source contract: [EMF 0.6.0 reference](https://github.com/matenm/emf-enshrouded/blob/v0.6.0/docs/modding/reference.md),
[public header](https://github.com/matenm/emf-enshrouded/blob/v0.6.0/sdk/include/emf/emf_api.h), and
[MIT protocol implementation](https://github.com/matenm/emf-enshrouded/tree/v0.6.0/src/core/net).
The copied implementation is attributed in `native/plugins/emf/src/net/`; its license is `licenses/EMF-LICENSE.txt`.

`native/plugins/emf/tests/run.sh` checks the frozen ABI, production adapters, upstream wire peer, binary fragments,
spoofed senders, direction/ownership/rate limits, backpressure, world teardown and shutdown. Its client-hook fixture
uses the production detours with stand-in engine calls. A dedicated-server probe is built with
`native/build.sh emf-net-probe`. These checks do not demonstrate an actual joined client: the live client/server
handshake, hidden chat, Beastmaster taming, stable interaction and per-player save/rejoin remain client checks.
