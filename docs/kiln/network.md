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
   - a different protocol;
   - since 0.2.0-dev.29, a server boot mod with side `both` that the client has in the same version, whose boot content
     differs ([Boot content at the join](#boot-content-at-the-join)): another mod for this client, like a version difference.

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

## Boot content at the join
Since 0.2.0-dev.29. `Mod.OnBoot` runs once per process, before the connection ([modding/onboot.md](modding/onboot.md)).
A setting it reads, such as the value of a resource patch ([modding/resource-patches.md](modding/resource-patches.md)),
gives each process its own game data, and config sync ([sync.md](sync.md)) comes too late to change it. Same mod, same
version, other settings: the server's values decide, while the player's game shows and predicts its own (More Flame
Altars on a server set to 8 altars, a player set to 4). So each side fingerprints what its boot generation committed,
and the server compares.

- **The fingerprint** of a boot mod with side `both`: SHA-256 over every document its boot generation committed for it
  (what its `OnBoot` staged, as lowered) and the `resourcePatches` of its `blocks/*.json`, made once when the generation
  is published. kiln.log: `boot: more-flame-altars: boot content 3f9a0c4be1d27756 (the join compares it with the other
  side's)`. It counts values, so these do not change it: object key order, directory enumeration order, spacing and line endings between JSON tokens, how a
  number is written (`1500`, `1500.0`), the mod's folder inside a path (an icon). Not in it: the mod's other files (its
  version stands for them) and the roles it ran for. String values retain their line breaks. Patches retain native application order
  (files by name, then patch keys within each file): reordering writes to the same field can change the result. A mod that is not a boot mod, one with side `client` or `server`,
  and a process without a boot generation have none.
- **On the wire:** the handshake property `kiln.boot` in HELLO and WELCOME, `<id>=<first 16 hex digits>` per mod, or
  `<id>=out` for a boot mod left out of this process's generation (its `OnBoot` failed, a dependency was out, a collector
  refused it, its content faulted later), comma-separated in id order. At most 2048 bytes: a mod that does not fit is not
  sent and not compared (kiln.log says so once). A process without a boot generation sends no such property, so its
  frames are what they were. A peer of 0.2.0-dev.28 or older neither sends nor reads it: it keeps a property it does not
  know in `NetPeer.Properties` and decodes the frames unchanged. The mod list stays `[id, version, side]`; an older
  decoder reads each entry by that layout, so a field there would break it, and properties are what it skips.
- **The decision** (server, in the hello's mod check, step 2 above): a mod both have in the same version, where both
  sent a fingerprint and they differ, is *missing* with the detail `<id> <version> (boot content differs: its settings or
  files differ from the server's; use the server's settings and restart the game)`. `modSync` then handles it exactly as
  a version difference of that mod:

  | `modSync` | Outcome |
  |---|---|
  | `refuse` | REJECT. The player's game shows: "The boot content of More Flame Altars on your game differs from this server's: its settings (or its files) differ. Use this server's settings of More Flame Altars and restart the game." The server logs `refusing <player> (...): more-flame-altars 1.0.0 (boot content differs: ...)` |
  | `warn`, or not set | joins; the WELCOME warning (the Kiln menu's Network page, the client's kiln.log) is "boot content of More Flame Altars (more-flame-altars 1.0.0) differs from the server's: its settings or files differ on this game. Use the server's settings of More Flame Altars and restart the game"; the server's `joined` line lists it |
  | `off` | no check |

  `NetClientHandshakeEvent.Missing` has the detail, so a handler can decide otherwise.
- **Not compared:** a side that sends no fingerprint for the mod (an older Kiln, not a boot mod there, `out`), a mod in
  `optionalMods`, a mod with side `client` or `server`, and another version that `versionMatch` accepts (the version
  rule alone decides: the operator lets other versions in, whose boot content may differ for that reason).
- **Left out on one side, committed on the other:** the side that left it out has not loaded the mod
  (`RestartRequiredBoot`), so the rules above decide (missing on the client, or extra on it). The server's line for the
  join says it, for example `(boot: more-flame-altars is left out of the client's boot generation and committed here)`.
- **What the player does:** take the server's settings of the mod (its file in `kiln/config`, from the server's
  operator or modpack) and restart the game. The boot content is made at the start, so a config sync or a reload of the
  mod cannot change it.

### Registry boot values and order

Registry edits have a separate additive property, `kiln.registry.boot`, in HELLO and WELCOME:
`1:<64 lower-case SHA-256 hex digits of values>:<64 lower-case SHA-256 hex digits of order>`.
It does **not** change the existing `kiln.boot` hashes. A new peer compares this part only when both peers send a
valid version-1 property. An absent property (older managed or native Kiln), malformed property or unknown version
compares nothing; old peers ignore the new property and join exactly as before.

The native stage fingerprints successfully resolved, boot-routed registry fields whose mapping says a client needs
its own copy. It uses Kiln registry/entry/field names and the values actually written, not archive bytes, resource
addresses, game executable hashes, runtime live layers or report truncation. Server-only fields do not contribute.
The order hash covers participating mods and their resolved field contributions in registry load order; mods with no
participating boot edits do not contribute. A field that a mod with side `server` or `client` writes (by any path) is
left out, because the other side never runs that mod; `kiln.boot` leaves such mods out too. The immutable start snapshot remains available as `bootSync` in
`kiln/state/registry.json` after world load and hot reload too. A `reload <id>` (or an `unload` and a `load`) whose new
`kiln.mod.json` changes the mod's `side` is refused (`RestartRequired: ...`, the running version stays; 0.2.0-dev.31.18), so
the side-`both` lists the join compares keep the sides this snapshot was made with.

Each side hashes its own fields, so a field that one side leaves out and the other keeps would make every join differ,
with nothing the player can change. A start therefore sends no `kiln.registry.boot` (and registry.json has no
`bootSync`) when it could not apply the client-held boot edits of its side-`both` mods as asked: such an edit was
refused (by this game build's mapping, by composition or by the write) or no write reached it, a refusal names no
field (a stage callback that faulted, a registry section that could not be read), the stage could not read the game's
base, or a side `server`/`client` mod writes a field that a side-`both` mod edits too. kiln.log says which (`registry:
this start's registry boot values are not compared at joins (...)`). Nothing is compared with that process then: the
mod-list rules and `kiln.boot` decide alone, as for an older peer. A side `server`/`client` mod on fields no
side-`both` mod edits does not stop the part.

The server compares the part only when both peers run the same side-`both` mods in the same versions. When the mod
lists differ, the mod-list rules above decide alone, as before: a missing required mod, a version difference, an
`optionalMods` entry the player lacks, another version that `versionMatch` accepts, or an extra client mod. One hash
over all mods cannot honour those rules per mod, so it does not try. Client- and server-side mods in the lists do not stop
the comparison.

A values mismatch reports **registry boot values differ** with instructions to use the server's modpack/settings and
restart. It uses the ordinary handshake missing-details/report line: REJECT under `modSync: refuse`, WELCOME warning
under `warn` (including the default), and no player warning under `off` (the line stays in
`NetClientHandshakeEvent.Missing`). The values cover the resolved value of every compared field, so when they match the
player's game runs what the server runs. An order or contribution difference alone (another load order, or a setting
that a later mod overrides) is then only named in the server's log line for that player (**registry boot edits or load
order differ**, with the same resulting values); it is never refused, warned about or put in `Missing`. This detects
data-only registry packs too, without requiring an `OnBoot` override. It does not transfer boot values or rebuild them
after joining.

## NPC bodies at the join
Since 0.2.0-dev.31. An NPC block with a body of its own ([modding/npc-blocks.md](modding/npc-blocks.md)) is built by each
side's game at its start: model, skeleton, graph and motion sequences. A player whose game built it differently, or not at
all, would see another body or none, so the server compares what each side's start made **Ready**, not the mod list.

| Property (HELLO and WELCOME) | Value |
|---|---|
| `kiln.npcbodies` | SHA-256, lower-case hex, over the sorted lines `<mod>:<npc block>:<closure hash>` of this side's Ready bodies, joined with `\n`; empty when there are none |
| `kiln.npcbodies.count` | how many Ready bodies |
| `kiln.npcbodies.list` | the lines themselves, comma-separated, as many whole lines as fit 2048 bytes (at most 64), so a refusal can name the first body that differs; the hash and the count still cover every body |
| `kiln.npcbodies.pending` | only from a game with bodies waiting for its restart (`PendingRestart`; this version sends none, since a body defined in C# is refused): those `mod:block` keys, cut the same way |

The closure hash is the boot build's per-body hash (the canonical body and motion spec, the owned files' hashes, the
chassis layout); the rows come from kiln.knowledge's `native_manifest` (`"bodies"`). A peer that sends no
`kiln.npcbodies` (an older Kiln) counts as having no bodies: it joins a server without any and is refused by one with
them. The decision, server side, after the player-animation check:

- the same hash: nothing to say;
- another hash: refused with "This server's NPC bodies don't match your game's: the server has the NPC body
  `<mod>:<npc>` and your game doesn't" (or "your game has ... and this server doesn't", or "... is built differently on
  your game"), then "Join with the same mods, and restart if you just changed them.";
- a `kiln.npcbodies.pending` key: refused with a restart-required reason, whatever the hash;
- `modSync: off`: joins, and the server's log says `joins with other NPC bodies (modSync off)`. Such a player's game
  may crash on the first NPC whose body it lacks: the server sends that NPC's clone entity, and the game crashes on an
  entity of a template it doesn't have, as [Clients without Kiln](#clients-without-kiln) says for a vanilla client;
- a mod's `NetClientHandshakeEvent` handler cannot let such a client in: only `modSync: off` waives this check.

Not verified in a real game yet: the refusal reaching a real player's screen, and two real starts producing the same
hash for the same mod files. The decision is covered offline by `Kiln.Network.Tests` (`NpcBodyGateTests`).

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

**Seen with a client without Kiln: the plain join.** A real game client without Kiln joined a Kiln dedicated server
normally, with Kiln-loaded native mods that all say `"clients": "none"` (MicDrop7, 2026-11-17, Kiln 0.2.0-dev.29.15:
Smart Workshop's test build and Creative Mode). **Not proven yet with a client without Kiln:** what this section
describes for a server with Kiln-only templates (the hold, its refusal, the let-in without those entities, and the two
hooks: the gate on the server and the guard on a client's create path) has run in labs only, not with a real game
client. A **Kiln** client's refusal has been seen in a real game: Kiln's toast, then the
game's login failure, back to the main menu and a rejoin afterwards (client build 1285601; see "Lab tests" below, last
paragraph, and docs/re/network.md "Runtime"). Lab results and the client checks still to run are in docs/re/network.md,
"Vanilla clients on a Kiln server".

## Admission
Since 0.2.0-dev.28 one internal class, `NetAdmission` (`managed/services/Kiln.Network/Admission.cs`), is the only code
that sets kiln.net's gate, lets a held peer into the world or refuses a joining one. Kiln's own services register
**requirements** with it; there is no mod API (only the per-peer counters below are public). With no requirement
registered, the gate behaves exactly as before: the three built-in rules of [Clients without Kiln](#clients-without-kiln)
(the mod list not known yet; a vanilla client refused; a vanilla client held for proof) run unchanged on it.

| Requirement | Registered by | What it does |
|---|---|---|
| mandatory protocol (`SetMandatoryProtocol(name, version, …)`) | a service whose clients, Kiln or not, must speak a channel | a client without that channel at that version is refused in its hello, before Welcome, under every `modSync`; a Ready client that announces its channels again without it is refused; a vanilla client is refused. Enabling it answers `Ok=false` (not installed) while a connected peer lacks it (`PeersLacking`) |
| join hold (`HoldJoins`) | a service that must finish something before anyone enters (18's policy change) | no peer is let in while it lives; at its deadline it ends with an error line |
| peer hold (`HoldPeer`, from an `OnJoin` decider) | a service that needs one client's acknowledgement first (18's late join) | that peer stays out until the hold ends; at its deadline the peer is refused with the holder's reason |
| `OnJoin` decider `kiln.stationmenus` (since 0.2.0-dev.31) | Kiln.Content while a station menu exists or the mods' menus are not declared yet ([sync.md](sync.md#station-menus)) | a Kiln client without `kiln:station-menu` v1 is refused after its hello, before the world, under every `modSync` ("station-menu protocol1 required"), and one in the world that announces its channels again without it; until the mods' OnLoad and blocks pass are done each joining Kiln client is held (25 s at most). A client without Kiln is not concerned (not a mandatory protocol: it never gets a mod's own station) |
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

## Steam peer messages (`SteamMessages`)
Since 0.2.0-dev.30.3 (mod API 0.2.13). A mod's own Steam networking messages between game processes, by Steam id:
separate from the game connection and from the channels above. It works on a game client (the player's Steam user) and on
a dedicated server (the GameServer interfaces); the game pumps Steam's callbacks, Kiln never initialises or pumps Steam.
Native side and every rule: [services/net-steam-messages.md](services/net-steam-messages.md). Prefer channels when the
players are on the same server: Steam messages reach anyone by Steam id, outside the game's admission, so treat every
message as untrusted.

```csharp
// kiln.mod.json: "steamMessages": true (Kiln.Sdk: <KilnSteamMessages>true</KilnSteamMessages>)
var open = SteamMessages.Open(4100, 4103, answerSessions: true);   // a lease on Steam channels 4100..4103
if (!open.Ok) { Log.Info(open.Reason!); return; }                   // Taken, NotPermitted, ClosedForExit, Fault
using var lease = open.Lease!;                                       // closes at Dispose or the mod's unload
lease.SetAcceptPolicy(SteamAcceptMode.Listed, [friendSteamId]);     // who may start a session with this process
lease.Send(friendSteamId, 4100, Encoding.UTF8.GetBytes("hello"));    // SteamSendFlags.Reliable by default
var buffer = new byte[SteamMessages.MaxMessageBytes];
for (var r = lease.Receive(4100, buffer); r.Ok; r = lease.Receive(4100, buffer))
    Log.Info($"{r.Message.Sender}: {Encoding.UTF8.GetString(buffer, 0, r.Message.Length)}");
```
- **Opt-in.** Only a mod whose kiln.mod.json says `"steamMessages": true` (`ModInfo.SteamMessages`) opens a lease;
  `Open` throws `InvalidOperationException` for any other mod, outside mod code and in `OnBoot` (open leases from
  `OnLoad` on), before anything is called.
- **A lease is the mod's own.** It belongs to the mod that opened it: kiln.net checks the mod's owner on every lease call,
  closes the mod's leases when it unloads, and `SteamMessageLease` never shows its handle. A closed lease answers
  `LeaseClosed` and never calls Steam; `Dispose` is idempotent, any thread.
- **Channels.** Leases own inclusive ranges of Steam message channels (0..65535), never overlap (across every mod and
  Kiln's own runtimes), and at most 16 are open in the process (`Taken` otherwise; an overlap with another mod's lease
  names it).
- **Sessions.** Steam accepts a session with a peer this process sent to first. To hear from a peer that writes first,
  open one lease with `answerSessions` and set its accept policy (`None`, `Any`, `Listed` with up to 256 Steam ids), or
  accept a pending request with `lease.Accept(steamId)`. One session-answering lease at most; closing it sets `None`.
- **Sizes.** Send 0..524,288 bytes (`MaxMessageBytes`; more throws). `Receive` takes at most one message per call into
  your buffer; one bigger than the buffer is taken and `Dropped`, so a `MaxMessageBytes` buffer never drops for size.
  Nothing is buffered by Kiln: a message waits in Steam's queue until a `Receive` on its channel takes it.
- **Results.** `SteamLeaseResult`, `SteamSendResult` (with Steam's EResult), `SteamReceiveResult`, `SteamAcceptResult`,
  `SteamPolicyResult`, each with `Ok`, `Reason` and `Refusal` (`SteamMessagesRefusal`); the per-message results are
  structs, so sending and receiving allocate nothing. Bad arguments throw `ArgumentException` before any call.
- **Availability.** Capability `net.steam_messages`: `pending` until a call first finds Steam ready, then `available`.
  `SteamMessages.Available` says whether it works now; `SteamMessages.Status` never throws. `Open` throws
  `KilnUnavailableException` only when the row is unavailable (safe idle, kiln-net.dll not started, steam_api64.dll or an
  export missing) or this Kiln has no Steam messages; while it is pending a lease opens and answers `SteamNotReady`.
- **Threads.** Every member: any thread, never waits.
- Tested: `managed/tests/Kiln.Network.Tests/SteamMessagesTests.cs` over a fake kiln.net; the native side's host tests and
  a two-process test over a stand-in Steam library. Not run with real Steam yet.

## Limits
- Game frames are zero-padded to 4 bytes; Kiln trims the padding in fragments and JSON messages.
- Frames up to kiln.net's `max_frame` (8000 bytes, under the game's 8192 per message on channel 12). Bigger messages
  are split into FRAGMENT frames and reassembled.
- `maxMessageBytes` (default 256 KiB) is the largest message in either direction. A channel can set a smaller limit.
- At most 16 messages may be half-received per peer; an incomplete message is dropped after 15 s.
- Per client, server side: token buckets of `messagesPerSecond` (default 200) and `bytesPerSecond` (default 512 KiB),
  each with a 2 s burst. Excess is dropped. `floodKickDrops` (default 2000) drops within 10 s kick the client.
  kiln.net adds its own per-peer cap underneath (2000 frames/s, 4 MiB/s) and bounded queues.
- A frame the game refuses for now (its bandwidth budget) waits in kiln.net's retry queue, in order, for at most 2 s;
  at most 8192 frames wait in all. Then `Send` reports it as not sent.
- Kiln pings Ready peers every 10 s (`NetPeer.RoundTrip`).
- Shutdown (since 0.2.0-dev.31.13): once the game's shutdown begins (its `Trigger gameflow shutdown` line: Ctrl+C,
  the console's quit, a game client quitting), kiln.net sends nothing more, to any peer, and drops what waits; from the
  server world's stop until the next world starts, the server role sends nothing. Channel 12 is still read and freed
  then (the game never holds a Kiln frame), but nothing is delivered. `Send` returns false. kiln.log logs each
  peer's Kiln traffic when it goes and when the server world stops (`Kiln frames in N (B bytes), out N (B bytes),
  N dropped, N refused by the game`).

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
- `missing[]` otherwise: required ids this client lacks, has switched off or has with another hash, from its loaded
  mods, `state/native.json` and the other engines' mods in its game folder (the folders and ids the server reads its own
  from: ShroudForge, Flame, Green Eye, SET ...). Each kind is compared by the hash the server advertises: a Kiln mod by
  its identity (its cook outputs, `assets/cooked`, do not count, on either side), another engine's mod by what stays the
  same while it runs (the Library entry of Kiln's copy, else its files without logs and settings). Only a mod that runs
  in this game counts, as the server needs it running: a ShroudForge copy switched off (`enabled: false` in its
  `extended.mod.json`, which Kiln writes for a mod switched off in the profile) or whose `targets` leave out `client`
  is missing, as a Kiln mod switched off in the profile is. Not yet matched: mods a Kiln client runs from the Library
  rather than an engine folder (EML mods, EMF's DLL mods and Shroudtopia mods without that engine's own loader), so
  they are still listed. Up to 0.2.0-dev.31.17 only Kiln and native mods were looked at, so every other engine's mod
  was listed.
- `hosted: true`, `missing[]` empty: the server is this game's own world (the server role runs in this process and
  advertised this game's server id). kiln.log says `welcome from your hosted world (<id>, this game)`; another server
  is `server <id> "<name>"`. The launcher offers no modpack for it. `hosted: false` is final, also for a server that
  advertises this game's id while no world runs here (a copied `serverId`). Only an older file without `hosted` makes
  the launcher compare the server id with the game folder's own (network.json `serverId` or
  `kiln/data/kiln-network/server-id`).

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
- kiln.content's added-buff check at the join (`content_join`, needs `--lab` and a buff room that holds records: a server
  with mods/tests/statusblock-join-test, or its placeholders after it was removed): a client whose HELLO has
  `kiln.content.held-answer` is held by kiln.content, gets the content state while held and is let in by its deferral
  (ack 4); an older client is not held, and its `program_refresh` WRONG_THREAD error is taken as applied; a refusal while
  held refuses it with its reason; silence refuses it at the 25 s deadline with the timeout text (docs/kiln/sync.md);
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

With the real client (Kiln on both sides): handshake in a hosted world and on the lab server, NetSample ping/pong (26-29 ms),
the refuse path for a Kiln client that lacks a required mod (toast + login failure, rejoin afterwards), and the Kiln menu's Network tab (server mods, clients,
round trip). Details: `docs/re/network.md` "Runtime". Server modpacks with the real client: the refusal
carried the server's properties into `last-server.json`, and after the launcher installed the modpack the rejoin got
WELCOME (docs/kiln/launcher/modpacks.md "Checks done").
