# Kiln.Network: mod channels, the join handshake, mod sync

Forge's SimpleChannel + the FML handshake for Enshrouded. Transport and RE: `docs/re/network.md` (game channel 12
of the game's own connection: no extra port). Native: `kiln.net` (`native/core/abi/services/kiln_net.h`); managed:
`managed/services/Kiln.Network`. Tests: `mods/tests/net-test` + `tools/nettest/nettest.py`. Sample:
`mods/samples/NetSample`.

## Channels
```csharp
public sealed record Ping(long SentMs, string Text) : INetMessage<Ping>
{
    public void Encode(NetWriter w) => w.Int64(SentMs).String(Text);
    public static Ping Decode(ref NetReader r) => new(r.Int64(), r.String(512));
}

var ch = Network.Channel("mymod:main", version: "1")            // same name + version on both sides
    .Register<Ping>((m, ctx) => ctx.Reply(new Pong(...)), NetDirection.ToServer)   // ids in registration order
    .Register<Pong>((m, ctx) => Hud.Toast("pong"), NetDirection.ToClient)
    .RegisterJson(MyJson.Default.Settings, (s, ctx) => ...);    // JSON (source-generated) for convenience

ch.SendToServer(new Ping(...));             // client
ch.SendTo(player, msg); ch.SendTo(peer, msg); ch.SendToAll(msg, except: peer); ch.SendNear(pos, 30, msg);  // server
```
- Message ids: `Register<T>()` assigns 0, 1, 2… in call order; register in the same order on both sides, or pass ids.
- Codecs: `INetMessage<T>` (binary, `NetWriter`/`NetReader`: little-endian, varints, UTF-8 strings, bounds-checked),
  explicit `Register<T>(id, encode, decode, handler)`, `RegisterJson<T>(JsonTypeInfo<T>)` (source-generated) or
  `RegisterJson<T>()` (reflection: keeps an unloaded mod in memory for a second or two after a hot reload, see the
  remark on that method), `OnRaw((ctx, id, bytes) => …)` for unregistered ids.
- `required: true` (default): a client without the channel (or with another version) counts as missing it in mod
  sync. A peer that lacks a channel never receives its messages; sends to it return false.
- Threads: server side, handlers run on the tick thread and sends must be made there; client side, handlers run on the
  main thread (client world tick) and `SendToServer` works from any thread. Handlers run as the channel's mod.
- A channel belongs to the mod that opened it and closes when the mod unloads (clients get the new channel table).

## Peers and events
- `Network.Peers` (server): every connected machine, Kiln or not. `NetPeer`: `Name`, `Player` (entity), `AccountId`,
  `State` (Connecting / Ready / Vanilla / Rejected / Closed), `KilnVersion`, `Mods`, `Properties`, `Warnings`,
  `RoundTrip`, counters. `Network.PeerOf(entity)`. `Network.Kick(peer, reason)` (dev.27): refuses a connection, the
  same way the gate refuses a client without Kiln; for players, `Players.Kick` and Kiln's ban list
  ([services/players.md](services/players.md#kick-and-ban-dev27)), checked on the server tick with `PlayerLoginEvent`.
- `Network.Server` (client): the server connection; `Network.ServerHasKiln`.
- Server events (event bus, tick thread):
  - `NetClientHandshakeEvent` (cancelable): a Kiln client's HELLO arrived. Fields: `Missing`, `Extra`, and `Refuse`,
    which starts as the policy's decision and may be changed, plus `Reason`. Canceling also refuses.
  - `NetPeerReadyEvent`: the handshake is done.
  - `NetVanillaClientEvent`: a client gave no proof of Kiln within `vanillaHoldSeconds` of finishing loading (on a
    build without the gate: it was in game for `handshakeTimeoutSeconds` without a HELLO). `Kick` may be changed.
  - `NetPeerLeftEvent`: the peer is gone.
- Client events (C# events, main thread): `Network.ServerReady`, `Network.Rejected(reason, details)`,
  `Network.ServerVanilla`, `Network.Disconnected`.
- `Network.SetHandshakeProperty(key, () => value)`: extra key/value pairs sent in HELLO and WELCOME. This is the seam
  for content hashes and config sync; the other side reads them in `NetPeer.Properties`.

## Join handshake and mod sync
1. The client's transport comes up (its GameClient has a socket for channel 12 toward the server). One second later
   Kiln sends **HELLO**: protocol, Kiln version, its loaded mods with side `both` or `client` (id, version, side), its
   channels (name, version, required) and its handshake properties. If nothing answers, HELLO is resent up to 3 times
   (3 s apart). A vanilla server logs one warning per HELLO (`invalid machine message on channel`) and nothing else
   happens. After the last try plus 4 s the client marks the server `Vanilla`.
2. The server compares. *Missing* means:
   - a loaded server mod with side `both` (not listed in `optionalMods`) that the client lacks or has in a version
     that fails `versionMatch`;
   - a required server channel the client lacks, or has in another version;
   - a different protocol.

   *Extra* means a client mod with side `both` that the server does not have.
3. Policy (`modSync`):
   - not set (the default): as `warn`, except that a client without a mod that **states** `clients: "mod"` is refused, the same way as under
     `refuse`, with a message naming those mods. A value that only comes from `side` doesn't count, nor does `clients: "kiln"` (the server
     sends what's needed), nor a mod in `optionalMods`. Only `side: both` Kiln mods can be checked: the handshake doesn't list others.
   - `off`: no check.
   - `warn`, set in `network.json`: accept, with the differences as warnings in WELCOME.
   - `refuse`: if anything is missing, send **REJECT** (reason and list), then about 1.5 s later the game's own login
     failure (`kickReason`, default 1 = "Version"; the game shows that as "An update of the game is available…").
     Kiln on the client (Kiln.Ui) shows the server's name and the mods the REJECT names as a toast as soon as it
     arrives, over that dialog, and again when the game is back at its main menu: that is what tells the player which
     mods are missing. The same goes for every refusal, whatever the policy.
   - Handlers of `NetClientHandshakeEvent` can override.
4. **WELCOME**: the server's Kiln version, policy, its `both` mods, the channel table `{id, name, version, required}`,
   the message limit, warnings and properties. Both sides then use the server's channel ids for the channels they
   share (same name and version). When channels open or close later, the server sends a **CHANNELS** update and the
   client re-sends HELLO.
5. Vanilla clients: see "Clients without Kiln" below.

## Clients without Kiln
A vanilla client crashes on the first entity of a template it doesn't have, about 2 s into its loading screen, so a
refusal that waits for a timer never arrives. Kiln therefore decides before the game sends it any world data.
Evidence: docs/re/network.md "Vanilla clients".

**What happens to a client that isn't proven to have Kiln:**
| The server has | `modSync` | Outcome |
|---|---|---|
| no mod stating `clients`, no Kiln-only template | any but `refuse` with required mods | joins as fast as without Kiln, nothing held |
| a mod stating `clients: "kiln"` or `"mod"` | `warn`, `refuse` | **refused**: held at most `vanillaHoldSeconds`, then the game's login error |
| anything required | `refuse` | refused the same way |
| anything | `requireKiln: true` | refused the same way |
| Kiln-only templates, nothing that refuses (`modSync: off`, or a mod that doesn't state `clients`) | | **let in** after the hold, without those entities |
| Kiln-only templates, and this build can't withhold them | any | refused, with a warning in kiln.log: letting it in would crash it |

**How, in order:**
1. A Kiln client's `kiln.net` sends a small proof frame (`KILN_NET_KNOCK`) on channel 12: 1 s and 3 s after the connection
   comes up, and once more in the same call that sends the game's Gameplay Ready. Loading time doesn't matter: Gameplay
   Ready can't go out before that last frame. Vanilla code never writes channel 12, so any frame from a peer proves it,
   including a HELLO. A vanilla server logs one warning per frame and ignores it.
2. Gameplay Ready is what starts the game's replication to a machine. The server reads channel 12 before the game's
   channels. A Gameplay Ready without proof is held when anything is at stake: no player entity, no world data.
3. The hold ends one of these ways:
   - proof arrives (the two frames are on separate reliable channels of one connection, so a lost packet can put the
     proof a little behind): the client goes on, never judged;
   - `vanillaHoldSeconds` (default 5, 1..20) pass: it counts as a vanilla client, `NetVanillaClientEvent` fires, and it's
     refused or let in;
   - it disconnects;
   - Kiln.Network never decides (it died): kiln.net refuses it after 30 s, or lets it in if this build can't refuse.
4. A refused client gets a REJECT frame, then 1.5 s later the game's login error (`kickReason`). A vanilla game frees the
   frame unread and shows the game's text for that error; there's no way to send it our own. A Kiln client shows our text.
5. A let-in client never gets entities of templates only Kiln has, or their children. It sees the world without them. If it
   turns out to have Kiln after all, what was held goes out at its content ack.

**An older Kiln client** (a HELLO but no proof frame). Its HELLO goes out after its client world's first step, and that
step follows the server's first tick (read from the logs, see docs/re/network.md). While it's held it gets no ticks, so
no HELLO comes. Where vanilla clients are refused it's refused too and shows: "This server's mods need Kiln on your game.
Join with the Kiln launcher, with an up-to-date Kiln." Where they're let in it joins after the hold; its HELLO then
works and it's treated like the Kiln client below. Not run on a real older client.

**A Kiln client that lacks a template.** The same crash would hit it, in two ways. Both are covered:
1. *A `clients: "kiln"` clone already live when it joins.* Its definition comes over the content sync, after replication
   has started. So Kiln-only entities are held back from every Kiln client too, until it acknowledges its content state
   (or `definitionsWaitSeconds`, default 5, runs out). Then what was held goes out, and the entity arrives after its template.
   Why this and not asking again afterwards: nothing reaches the client early, so there's nothing to undo. Asking again would need
   the server to forget, for one entity, that it had already sent it, and the game has no such step.

   *A template added during play* (a mod loaded or reloaded while the client is in). A client's clear covers the Kiln-only
   templates there are at that moment. An entity of a template added later, or a child of one, is kept from it, even one
   spawned in the first step the template exists. Entities of the templates it already had keep going out. The server builds a content state with the
   new template within a second. Only an ack of the state it serves now clears the client again; an ack of an older state, from
   before the template, doesn't. Then the kept entities go out. Without the content channel (an older Kiln) there's nothing to
   wait for, and they go out at Kiln.Network's next tick. A client that doesn't ack gets them after `definitionsWaitSeconds`.
   kiln.log: `net: peer 0x.. (slot N): entity E of the Kiln template <guid>, added after its last content ack, waits for its next
   one`, then `net: peer 0x.. cleared again: ... 1 kept-back entity goes out`.
2. *A template it won't ever have*: a `clients: "mod"` mod it lacks, let in by a `modSync` set to `warn` or `off`. The client's guard drops the create
   and any children, along the game's own failure path. kiln.log gets one line per template:
   `net: this game has no template <guid>, which the server uses: its entities aren't created here (first: <entity>). A server mod you don't have adds it.`
   **What the player sees:** that NPC, marker or creature isn't there, with no message at that moment. An entity of a Kiln-only template the client does have
   is logged once per template: `net: this game creates entity N of the Kiln template <guid> (TemplateInfo ...)`. The Kiln menu's Network tab already
   lists `server mod not on this client: <mod>`. Later updates of the entity are discarded without a log line.

If the guard's hook doesn't resolve on a build, the client behaves as before (this crash) and kiln.log says
`net: no guard on the game's create path on this build`.

**What is not covered, plainly:** only entities are withheld. A let-in vanilla client can still crash on a Kiln id inside
other data:
- Kiln items on its own character, equipped: the `apply_armor` crash (docs/re/content.md section 9);
- Kiln items in another player's hands, in a chest it opens, in a dropped pickup: 19 of the game's item lookups don't test
  the result. Not run.

So a server that lets vanilla clients in (`modSync: off`, or a mod that adds items and doesn't state `clients`) can still
crash them that way. A mod that adds items should state `clients`, and then the default policy refuses.

**Hot reload:**
- A template added while a vanilla client is in: withheld from its first entity; every check reads kiln.assets' current
  snapshot.
- A template's resource removed: still withheld. It stays registered in the world, so kiln.assets remembers it for the
  process (`resource_is_new` stays 1).
- A mod that makes the server refuse, loaded while a vanilla client is in: that client stays until it leaves, with
  templates withheld; the next join is refused.

**When a piece is missing on a build:** Kiln clients don't need either piece and are unaffected.
- No withholding (`net.withhold` off) and the server has Kiln-only templates: vanilla clients are refused under every policy, with a warning in kiln.log. kiln.assets
  not loaded means no Kiln-only template can exist, so nothing changes.
- No gate (`net.gate` off): nothing is held. A vanilla client gets the world at once, without Kiln-only entities, and a refusal only after `handshakeTimeoutSeconds`; until
  then it can crash on Kiln items, and kiln.log says so.

**Not proven yet.** The real-client hold, refusal, let-in and the two hooks have not run: client launches are on hold.
Lab results and "Client checks" are in docs/re/network.md.

## Admission
Since 0.2.0-dev.28 one internal class, `NetAdmission` (`managed/services/Kiln.Network/Admission.cs`), is the only code
that sets kiln.net's gate, lets a held peer into the world or refuses a joining one. Kiln's own services register
**requirements** with it; there is no mod API (only the per-peer counters below are public). With no requirement
registered, the gate behaves exactly as before: the three built-in rules of [Clients without Kiln](#clients-without-kiln)
(the mod list not known yet; a vanilla client refused; a vanilla client held for proof) run unchanged on it.

| Requirement | Registered by | What it does |
|---|---|---|
| mandatory protocol (`SetMandatoryProtocol(name, version, …)`) | a service whose clients must speak a channel (16a's `kiln:station-menu`) | a client without that channel at that version is refused in its hello, before Welcome, under every `modSync`; a Ready client that announces its channels again without it is refused; a vanilla client is refused. Enabling it answers `Ok=false` (not installed) while a connected peer lacks it (`PeersLacking`) |
| join hold (`HoldJoins`) | a service that must finish something before anyone enters (18's policy change, 16a's menu latch) | no peer is let in while it lives; at its deadline it ends with an error line |
| peer hold (`HoldPeer`, from an `OnJoin` decider) | a service that needs one client's acknowledgement first (18's late join) | that peer stays out until the hold ends; at its deadline the peer is refused with the holder's reason |
| vanilla condition (`Require`) | a service that keeps vanilla clients safe only while something holds (15's item remap) | while it answers false, vanilla clients are refused |

**Order for a joining peer:** hello → built-in checks (mods, channels, world tables, player graph) → mandatory protocols →
Welcome → every `OnJoin` decider → holds end → let in. A refusal at any step sends REJECT with the reason (a Kiln client
shows it), then the game's login error 1.5 s later. Each hold has a deadline of at most 25 s, under kiln.net's own 30 s
limit for a held peer; it is never extended.

**Hold all.** While a requirement must decide Kiln clients (a hold, a decider or a mandatory protocol), kiln.net's gate is
in mode `KILN_NET_GATE_HOLD_ALL` (`kiln_net.h`, kiln.net since 0.2.0-dev.28): every Gameplay Ready is held, a proved
one's too, and only Kiln.Network's release (or a kick, a lower mode, the 30 s limit) ends it. The controller looks at the
held peers every step while a requirement lives and lets a peer in as soon as no hold is left on it: a compatible client
waits at most one step for that. When the last requirement goes, the gate returns to today's mode and kiln.net lets the
proved peers it holds go by itself. A kiln.net without the mode (`KILN_NET_GATE_CAN_HOLD_ALL` missing) makes `HoldJoins`,
`HoldPeer` and `OnJoin` throw `KilnUnavailableException("net.gate.hold_all")`; a build without the gate hook makes them
throw for `net.gate`, and `SetMandatoryProtocol` answers `Ok=false`. Capability rows: `net.admission`, `net.gate.hold_all`.

**When the login error cannot reach a refused peer** (kiln.net since 0.2.0-dev.28): kiln.net refuses the peer for good
first (`KILN_NET_PEER_REFUSED`: until it leaves, its Gameplay Ready is never processed, whatever the gate's mode). If the
game's login failure is not on the build or faults, kiln.net ends the peer with the game's own session kick instead
(sigdb feature `net.disconnect`, dedicated server only): what an admin's kick does. The game sends the machine its kick
message and closes its connection about a second later, the way it drops a peer that timed out; the peer then leaves
like a kicked one. If it is still connected 10 s later, kiln.net sends the session kick once more. Without
`net.disconnect` (a hosted world, or a build where it did not resolve), or when that faults too, the peer stays connected
but **blocked** (`KILN_NET_PEER_BLOCKED`): no world data until it leaves. kiln.log says so once
(`admission: … is refused, but this server cannot end its connection …`), the Network page lists it (`blocked: …`),
Kiln.Network does not try the kick again, and 30 s after kiln.net reported it blocked (kiln.net's own hold limit) it no
longer keeps the gate in "hold all": kiln.net's refusal keeps it out on its own. A peer the session kick ends is listed
as `ending: …` until it is gone. What a real client sees when the session kick ends it (expected: the game's own "kicked"
message) is not verified; the lab peers of `nettest` are UDP fakes with no game connection.

**While held**, a Kiln client keeps its channel-12 frames in both directions (a Welcome, a mod's message and its answer):
that is how an acknowledgement reaches the server before the client enters. It has no player entity yet
(`NetPeer.Player` is `None`). Measured on the lab transport; what a real client shows while held (its loading screen) is
the guided check `15a-hold-kiln`.

**Per-peer counters** (public): `NetPeer.FramesIn` and `NetPeer.FramesOut` count the channel-12 frames from and to a peer
(server side; on a client the server's link). A client without Kiln has `FramesIn` 0 and `FramesOut` 0, or 1 for the
REJECT its game discards. `NetPeer.BytesIn` (data message bytes received) and `BytesOut` (frame bytes sent) existed
before.

**Status:** the Kiln menu's Network page lists every requirement (holder, reason, deadline left) and each held peer with
the holder that keeps it; kiln.log has a line for each requirement that comes or goes (`admission: …`) and for each held
peer and its holder (`admission: proved peer lab-5 (0x80000005) held by …`). The content definitions wait (withholding
Kiln-only entities until a client's content ack) is not admission: the page lists the peers that wait for it, and it
works as described above.

## The contract (dev.27)

Kiln.Network follows [the API contract](modding/contract.md).

- **Capabilities.** `kiln.net` (the core's row) and its sigdb features (`net.client_guard`, `net.disconnect` (dev.28),
  `net.gate`, `net.kick`, `net.players`, `net.transport`, `net.withhold`), lowered at Kiln.Network's start (the first service tick) when
  kiln.net is missing; `players.kick` and `players.bans` (managed, PlayerGate, at the same start).
- **Errors.** Nothing throws for a missing transport (channels open, sends return false, peer lists are empty);
  `Network.Kick` throws `KilnUnavailableException` naming `players.kick` (dev.27).
- **Sides and threads.** `[ServerOnly]`: `Peers`, `PeerOf`, `Kick`, `ServerId`, `ServerName`, `Advertised`,
  `RefreshRequired`, `NetChannel.SendTo(EntityId, ...)`, `SendToAll`, `SendNear`. `[ClientOnly]`: `Server`,
  `ServerHasKiln`, `ServerAdvert`, the four client events, `NetChannel.SendToServer` (also `[AnyThread]`, as the
  remarks say). `[TickThreadOnly]` where the code checks it: `NetChannel.SendNear`, `ConfigSync.CheckRoundTrip`.
  **Not marked:** the server's other sends, which the remarks ask for on the tick thread but which do not check it: a
  mark would give code that works today a new analyzer warning.
- **Results and events.** No result record here (`KickResult` is Kiln.Players'); Kiln.Network writes no world state,
  so it has no change event of its own.
- Tested offline: `managed/tests/Kiln.Contract.Tests/NetworkContractTests.cs` and the conformance rules.

## Limits
- Game frames are zero-padded to 4 bytes; Kiln trims the padding in fragments and JSON messages.
- Frames up to kiln.net's `max_frame` (8000 bytes, under the game's 8192 per message on channel 12). Bigger messages
  are split into FRAGMENT frames and reassembled.
- `maxMessageBytes` (default 256 KiB) is the largest message in either direction. A channel can set a smaller limit.
- At most 16 messages may be half-received per peer; an incomplete message is dropped after 15 s.
- Per client, server side: token buckets of `messagesPerSecond` (default 200) and `bytesPerSecond` (default 512 KiB),
  each with a 2 s burst. Excess is dropped. `floodKickDrops` (default 2000) drops within 10 s kick the client.
  kiln.net adds its own per-peer cap underneath (2000 frames/s, 4 MiB/s) and bounded queues.
- Kiln pings Ready peers every 10 s (`NetPeer.RoundTrip`).

## `kiln/config/network.json` (server)
```json
{
  "modSync": "warn",          // off | warn | refuse; leave it out for the default (see Policy above)
  "versionMatch": "exact",    // exact | major (semver major; 0.x: same minor) | any
  "requireKiln": false,
  "optionalMods": [],
  "kickReason": 1,            // the game's login error shown to refused players (1 Version, 5 Generic)
  "handshakeTimeoutSeconds": 20,  // in game without a HELLO, only on a build without the gate
  "vanillaHoldSeconds": 5,        // finished loading without proof of Kiln: how long it waits to be judged (1..20)
  "definitionsWaitSeconds": 5,    // Kiln-only entities wait this long for a Kiln client's content ack (1..30)
  "maxMessageBytes": 262144,
  "messagesPerSecond": 200,
  "bytesPerSecond": 524288,
  "floodKickDrops": 2000,
  "labPort": 0,               // LAB ONLY: UDP 127.0.0.1:<port> for tools/nettest's fake clients
  "serverId": null,           // default: kiln/data/kiln-network/server-id (generated once, "srv-" + 12 hex)
  "serverName": null,         // default: enshrouded_server.json name
  "modpack": null,            // {url, sha256, name?, version?, size?}: where players get the mods (launcher publish)
  "advertise": true           // send server id/name, modpack and required mods in REJECT/WELCOME
}
```
The file is `network.json` in the config folder (`KilnPaths.Config`: `kiln/config`, or the active profile's
configDir on a client). `Network.ReloadConfig()` re-reads it (admin op `network_reload` does that and rehashes).

## Server modpacks
A server tells joining players what they need and where to get it; the launcher turns that into a consent modal
(docs/kiln/launcher/modpacks.md). Nothing here downloads anything.

**What players need** (`Network.Advertised.Required`): every mod in `Mods.All` with a folder whose manifest says
`clients: "mod"` (default from `side`: `server` -> none, else mod), not switched off with `.disabled`, not failed; plus
`kiln/native/<id>/native.json` folders with `clients: "mod"` (default from side: client -> mod). Each one with the kdh1
of its folder (`DirHash`, the C# port of `schema/fixtures/dirhash/dirhash.py`; NFC through the OS's
`NormalizeString` because the host runs with invariant globalization). A Kiln mod is advertised by its identity
(`DirHash.ModIdentity`, launcher/contracts.md 5.11): its kdh1 without what a cook left in `assets/cooked`, so a mod
cooked on the server (no models, a report that names the server's files) has the hash of the same mod cooked in a
player's game. Hashes are computed on the thread pool, cached
by each folder's tree signature, refreshed when the mod list changes and every 60 s; `Pending` until the first pass.
The launcher's `kiln_manage::modpack::required` computes the same list offline (lab parity test).

**Handshake properties** (both REJECT and WELCOME, when `advertise` is on; values are text):

| Key | Value |
|---|---|
| `kiln.server.id`, `kiln.server.name`, `kiln.server.kiln` | the server's id, name and Kiln version |
| `kiln.modpack.url`, `.sha256`, `.name`, `.version`, `.size` | network.json `modpack` |
| `kiln.required`, `kiln.required.1`, `kiln.required.2` ... | JSON arrays of `{id, version, hash, kind?}` (`kind` omitted for kiln), each chunk <= 4000 bytes |
| `kiln.required.count` | how many mods players need in total |
| `kiln.required.more` | how many did not fit (only when truncated) |
| `kiln.required.pending` | `"1"` while the hashes are not computed yet |

The chunks are cut so the whole frame stays <= 7 KB (kiln.net's frame is 8000 bytes; WELCOME is not fragmented):
about 50 mods fit, the rest are counted in `kiln.required.more`. Every value stays within the 0.1 decoder's limits
(key <= 128 bytes, value <= 4096 bytes, <= 256 properties), so old clients decode the frames unchanged.

**Client side** (`Network.ServerAdvert`): the server's properties parsed back. On every REJECT, on the first WELCOME
of a connection and when a server never answers, Kiln.Network writes `kiln/state/last-server.json`
(`schema/state-last-server.schema.json`): outcome (`rejected` | `welcome` | `vanilla`: no Kiln, or a WELCOME without
`kiln.server.*`), reason, server `{id, name, address: null, kiln}`, modpack, required[], truncated, missing[], and the
previous entries as history (newest first, at most 20). Written on the thread pool, atomically (tmp + rename).
- `missing[]` on a refusal: the mods the REJECT's details name (`<id> <version> (missing)` or `(client has …)`), which
  is what the server refused for. Under the default policy that's only mods that state `clients: "mod"`; required[]
  can hold more (a `side: both` mod whose `clients` only defaults to `"mod"` is in the modpack, but no reason to refuse).
  A REJECT without details falls back to the rule below.
- `missing[]` otherwise: required ids this client lacks or has with another hash, from its loaded mods and
  `state/native.json`. A Kiln mod is compared by its identity, as the server advertises it: its cook outputs
  (`assets/cooked`) do not count, on either side.

## Wire format (one kiln.net frame each)
`'K' 'N' 0x01 type`, then:

| Type | Payload |
|---|---|
| 1 HELLO | u16 protocol, str kilnVersion, u32 nonce, mods `[str id, str version, u8 side]`, channels `[u16 0, str name, str version, bool required]`, properties `[str, str]` |
| 2 WELCOME | u16 protocol, str kilnVersion, u32 nonce, str policy, u32 maxMessage, mods, channels `[u16 id, …]`, warnings `[str]`, properties |
| 3 REJECT | str reason, details `[str]`, then (Kiln 0.2+, optional) properties `[str, str]` |
| 4 DATA | u16 channel, u16 message, body |
| 5 FRAGMENT | u16 channel, u32 seq, u16 index, u16 count, (index 0: u16 message, u32 total), chunk |
| 6 PING / 7 PONG | u32 nonce, u64 sender ms |
| 8 BYE | str reason |
| 9 CHANNELS | channels `[u16 id, str name, str version, bool required]` |

Lists are a varint count, strings are a varint byte length plus UTF-8, and integers are little-endian.

## Lab tests (no client needed)
```sh
echo '{ "labPort": 25881 }' > ~/srv/enshrouded/labs/<lab>/kiln/config/network.json
KILN_OUT=… tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any --samples   # with mods/tests/net-test built into $KILN_OUT/mods/net-test
tools/lab/lab.sh start <lab>
tools/nettest/nettest.py --port 25881 --lab ~/srv/enshrouded/labs/<lab>
```
Each UDP source address is one fake client (peer `0x8000000n`, "lab-n"). The suite covers:
- handshake and re-handshake;
- echo (200 messages, in order);
- JSON;
- fragments both ways, up to 250 KB;
- oversize and over-limit messages;
- 62 malformed frames;
- broadcast to 2 clients;
- channel version mismatch;
- ping both ways;
- the rate limit and recovery;
- policy refuse/warn/versionMatch/protocol;
- hot reload of the test mod while clients are connected (channel table update, then a leak-free `reload-test`);
- NetSample's ping and announce;
- clients without Kiln (`vanilla`, needs `--lab`), with simulated peers: no wait when nothing is at stake; hold, REJECT and kick at the
  deadline; proof first; late proof; HELLO without proof; kiln.net's own time limit; let in;
- the admission controller (`admission`, needs `--lab`; net-test registers one requirement of each kind through its
  `gate-admit-*` controls): a join hold puts the gate in "hold all"; a proved client is held by that holder and keeps its
  frames both ways (a Welcome, a message and its answer); a second holder keeps it held; its end lets it in within a step;
  a deadline refuses it with the holder's reason and it is never let in; an OnJoin decider holds a joining client; a
  mandatory protocol refuses a client without the channel in its hello, and a client without Kiln is held, then refused;
  a client without Kiln let in under `modSync: off` has `FramesOut` 0;
- a refused peer the login error cannot reach (`admission_fallback`, needs `--lab`; net-test's `gate-admit-kickfail` sets
  kiln.net's lab switch): the game's session kick runs on the server's own machine index and the session refuses it in
  the game's log (the host, then a handle no machine has); with the login failure failing, a refused Kiln client and a
  refused client without Kiln are ended by the session kick and never let in (lab peers have no game connection: theirs
  is being forgotten); with the session kick failing too, both stay blocked, flagged, named on the Network page, logged
  once, and never let in, also after Kiln.Network stops holding the gate for them;
- what is withheld (`withheld`, add `--game-templates <n>` from a lab without mods): every template in the world against what
  kiln.assets reports, a real entity of each Kiln-only template, their children, a template resource added and removed at run time;
- the client guard and the held-back resend (part of `withheld`), on the game's own create reader and relevance step, which the server exe
  has too; a free player slot is used;
- the wait for definitions (`definitions`, needs Kiln-only templates): released by an ack of the state it was sent, not by an ack of
  another state, by its deadline, no wait without the channel;
- a template added during play (`hotload`, needs `kiln/staged/creaturetweaks`): CreatureTweaks loaded while three fake clients are
  in, each standing for the player of a free slot, and an Elite Vukah spawned in the step its template appears. On each slot, the
  game's relevance step shows the elite kept back while a game entity and an entity of a known Kiln template go in. An ack of the
  old state keeps it back, and the ack of the state that defines it sends it. A client without the content channel gets it
  within a second. One that never acks gets it at the deadline. The server's log order is checked: registered, spawned, kept
  back, state sent, acked, cleared;
- server modpack properties (`advert`): WELCOME/REJECT props, the 0.1 REJECT decoder on new frames, the advertised
  hash (a Kiln mod's identity) against the Python reference over the lab's mod folders, the dirhash fixtures hashed by
  the C# port under Wine (controls `dirhash` and `modhash`: kdh1 and a mod's identity), modpack in network.json,
  `advertise: false`.

The net-test mod also checks at load (`advert.*`, `lastserver.history` PASS lines in kiln.log): REJECT with properties
through the 0.1 decoder, zero padding, a 300-mod required list truncated under 7 KB, and the last-server.json history.

Last run (svn 1287369): `nettest: 111 PASS, 0 FAIL` with net-test and NetSample. With npcsample and quests-sample too
(`modSync: warn`): `handshake,policy,missing_mod,vanilla,withheld,definitions,hotload` gave 101 PASS, 0 FAIL.

With the real client: handshake in a hosted world and on the lab server, NetSample ping/pong (26-29 ms),
the refuse path (toast + login failure, rejoin afterwards), and the Kiln menu's Network tab (server mods, clients,
round trip). Details: `docs/re/network.md` "Runtime". Server modpacks with the real client: the refusal
carried the server's properties into `last-server.json`, and after the launcher installed the modpack the rejoin got
WELCOME (docs/kiln/launcher/modpacks.md "Checks done").
