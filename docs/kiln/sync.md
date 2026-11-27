# Server-to-client sync: content, configs, config screens

Forge's login sync for Enshrouded. A Kiln server sends Kiln clients what they need to show its game correctly:
- its **content changes** (registries: items and recipes, from data packs and from mods' code);
- the **`[Synced]` entries** of its server and common configs.

The client applies both while connected and takes them off on leave. The Kiln menu also gets a **config screen**
for every mod config (Forge's config GUI). Code:
- `managed/services/Kiln.Content/ContentSync.cs`: content;
- `managed/services/Kiln.Network/ConfigSync.cs`: configs and `SyncSettings`;
- `managed/Kiln.Api/Config.cs`: server values in `ConfigFile`;
- `managed/services/Kiln.Ui/ConfigScreens.cs`: the screens;
- native: kiln.content v2 (`export_changes`, client world).

Tests: `mods/tests/sync-test/` (mod + `synctest.py`).

## What a mod author does
Nothing, for content. A mod changes items or recipes on the server, through a data pack (`content/*.json`) or code
(`Content.Items.Get(..).MaxStack = 1000`, `Content.Apply(...)`). Every Kiln client in the game then sees the same
values: max stacks, recipe ingredients and results, crafting times, item stats in tooltips. The mod does not have to
be installed on the clients. BigStacks (`side: server`) is enough on the server.

For configs, mark what clients must know:
```csharp
public sealed class MyRules
{
    [Synced, Range(1, 50)] public int MaxHomes { get; set; } = 3;     // sent to clients
    public string Motd { get; set; } = "hi";                          // stays on the server
    [Synced] public PvpSection Pvp { get; set; } = new();             // a synced section: all of it
}
_rules = Config.Register<MyRules>(ConfigKind.Server);                 // or Common
```
On a connected client, `_rules.Value.MaxHomes` is the server's value. The mod gets its usual `ConfigChangedEvent`
(`Source == ConfigChangeSource.Server`) and a `ConfigSyncedEvent`. `_rules.IsFromServer("maxHomes")` is true and
`_rules.ServerValues` lists the paths. These entries are read-only meanwhile (`TrySet` refuses). The client's own
file keeps the player's values, and they come back on leave.
A mod that registers its config while connected (a hot reload) gets the server's values before `Register` returns,
`[RequiresRestart]` entries included.

Events (event bus, tick thread; on a joined client that is the main thread):
| Event | When | Fields |
|---|---|---|
| `ContentSyncedEvent` | client: the server's content was applied (`Restored` false), or taken off after leaving (true) | `Applied`, `Reset` (own changes put back to match the server), `Errors`, `StateId` |
| `ConfigSyncedEvent` | client: server values went into a config (`Restored` false), or the local values came back (true) | `Config`, `Changed`, `Errors` (values the local schema refused), `Restored` |
| `ConfigChangedEvent` | as always; `Source = Server` for both of the above on configs | |

Status for displays: `ContentSync.Status` (client: state, fields, bytes), `ContentSync.Serving` (server),
`ConfigSync.LastReceived`, `ConfigSync.ServerConfigs`, `ConfigSync.Serving`. The Kiln menu's Network tab shows them.

## Content sync
- **Server**: every second the server checks whether any Ready Kiln client has the `kiln:content` channel. If one
  does, it builds the **content state**:
  - kiln.content's `export_changes` lists every item and recipe field whose value differs from the game's original,
    plus the whole current list of every changed recipe input or output list. That covers data packs and code layers
    of every mod, after conflicts are resolved.
  - The result is a pack document with absolute values, keyed by id (`"0x2b0cf1d5": {"@name": "Material_T1_Wood_Logs",
    "maxStackSize": 1000}`).
  - The state is rebuilt when the content generation (a pack reload) or the number of changed fields changes, and
    every 10 s otherwise. A change of value only is therefore sent within 10 s, a new field within 1 s.
  - The state id is the FNV-1a hash of the document. The document is deflated and sent to every client that does not
    have that id yet: at join, and after every change.
  - Random loot and drop tables are rolled on the server, so they are not sent. The client world has no loot
    registry anyway.
- **Client** (joined game): it reassembles the parts, inflates them and checks the length and the hash. Once its own
  registries are ready:
  1. It takes its previous sync layer off.
  2. Optional, `matchServer` (default on): what this game changed on its own (its local packs and mods) and the server
     did not goes back to the game's original (`{"mul": 1}`). The registries then equal the server's exactly.
  3. It applies the document as one owner layer, **"server sync"**. It is a code layer, so it sits on top of local
     packs.
  4. It logs the result, posts `ContentSyncedEvent` and acknowledges to the server. The server logs the ack:
     `<player>: content state … applied: N change(s), … in X ms`. That ack also releases the Kiln-only entities the server was
     holding back from this client, so an entity never arrives before its template (docs/kiln/network.md, "A Kiln client that
     lacks a template"; `definitionsWaitSeconds` limits the wait). Only an ack of the state the server serves now counts. A
     Kiln-only template added during play (a hot load) makes a new state due at once; its entities wait for that state's ack.

  When local packs are re-applied while connected (a local mod reloads), the layer goes on top again.
- **The added-buff check at the join** (a server whose buff room holds records: added buffs of loaded mods, or the
  placeholders of a removed mod's buffs). The server holds each joining Kiln client at its Gameplay Ready until the
  client answers its content state. A joining game cannot apply the state then: its registries load in its client
  world's steps, and a held game gets no world data, so its world does not step (and no Kiln tick runs in it). Since
  0.2.0-dev.30 it answers at once instead, from the frame that completes the state (ack result 4, "deferred", sent
  only to a server whose WELCOME has the handshake property `kiln.content.held-answer`, which a client's HELLO has
  too): refused there when its own buff room is already known (a game that joined before) and too small, with the
  numbers; otherwise let in, and the ack of its apply once its world runs decides (a failure then refuses it with the
  reason). What needs no registries is answered at once too, to any Kiln server: `acceptContent` off, kiln.content not
  available. Each connection answers its state again (a rejoin with the same content, or a join after one that ended
  before its world ran). A client whose handshake does not have that property (an older Kiln) is not held for this
  check: it can only answer once its world runs. A held client that does not answer within 25 s is refused with "Your
  game did not answer this server's content and added-buff check in time". 0.2.0-dev.29 to 0.2.0-dev.29.22 servers (and
  29.23, a launcher-only release) held every Kiln client there and refused it after 25 s with "Waiting for this game's
  content and added-buff capacity check": no Kiln player could join such a server, and a dev.30 client cannot either
  until the server's Kiln is updated.
  The programs the state carries (`@programs`, e.g. a StatusBlock's) are served through kiln.assets as before; a
  joined client no longer reports kiln.abilities' `program_refresh` answer WRONG_THREAD (4) as an error (it registers
  programs on the server world's thread only, and a joined client runs none): 0.2.0-dev.29 to .22 refused such a
  player with "kiln.abilities did not register the programs (4)". A 0.2.0-dev.30 server takes an older client's ack
  whose only error is that one as applied (its apply was otherwise whole), so older clients can play there.
- **Leave**: on `Network.Disconnected`, or a new server's handshake, the layer comes off on the client's main
  thread. If the client world stops first, kiln.content itself puts every change back while the world is still
  intact (`world_reset`, see below). Either order is safe.
- **Hosting** (single player or co-op host): the server is this process, and its item and recipe data is the same
  memory as the client world's (two `ItemRegistry` objects, shared `ItemInfo` data). Nothing is sent to the host's
  own client, and a host that gets a state anyway acknowledges "hosting" and ignores it. Remote co-op players get the
  state as from a dedicated server. The shared memory is the newcontent worker's finding. The host path itself (skip,
  "hosting" ack) and co-op guests of a host are not runtime-tested yet.
- **Vanilla clients** never get anything: Kiln sends only to peers that completed the handshake with the channel.
  Older Kiln clients without the channel join normally: the channels are not `required`.
  So what the sync carries can't reach a vanilla client, and neither can entities of new templates: where it isn't
  refused, the server withholds those (docs/kiln/network.md "Clients without Kiln"). What still reaches it is ids in
  other data, such as a new item in a chest; those can crash it (docs/re/content.md section 9).
- **Limits**: `maxContentKiB` (default 8192 KiB of uncompressed JSON) on both sides. A server over its limit sends a
  "not synced" notice, and the client logs the reason (`Failed`).
  - Parts: each part is cut to **one kiln.net frame** (the server's max frame minus 64 bytes, about 7.9 KB), so the
    state never relies on Kiln.Network's fragmentation. A client accepts parts up to 96 KiB, at most 4096 of them.
  - Pacing: the server sends at most 48 parts per peer per server tick (about 380 KB/s).
  - A half-received state is dropped after 60 s.
  - With BigStacks on (2257 changed fields): 178 KiB of JSON, 37 KiB deflated, 5 parts. The export takes 9-12 ms on
    the server tick. The real client applied it in 27 ms on its main thread (765 VirtualProtect pairs).

### kiln.content in a joined client (native, v2)
kiln.content used to work only on the server world, so in a joined game (no server world) it never became ready.
Now:
- The registries are the **server world's** when there is one (dedicated, hosting), else the **client world's own**.
  ClientWorldSetup registers `g38_items::ItemRegistry` (`[r15+0x3e20]`, client exe 0x3a7b4e) and `RecipeRegistry`
  (`[r15+0x3e28]`, 0x3a7c4d). LootGenerationSystem is server-only.
- The table works on that world's thread (the tick thread or the main thread), and the client world's Pre drives the
  service while no server world runs.
- On `KILN_WORLD_STOPPING` of that world, every layer is taken off while the world is intact (`store_reset`), the
  snapshot is dropped, and the next world (a new hosted session, a join) is read and patched from scratch. Mods'
  `OnReady` handlers run again then.
- `export_changes(kinds, buf, cap, len)` (v2) gives the document above. A recipe list that is back to the game's
  amounts, but lives in Kiln's memory, does not count as a change.
- In a joined client the readiness log reads `registries ready at step 12 (172 ms after the client world's first
  step): 4213 items, 2542 recipes … 0 random-loot items` (real client). The loot registry lives on the server, so a
  joined client logs `random-loot items off: this client joined a server, which rolls the loot` at Info; a server
  world (dedicated, or a client hosting) without it warns `random-loot items off: the loot registry is not readable`.
- `ContentSync.CheckRoundTrip()` checks the format on this process's own registries:
  1. The exported state, applied on top, leaves the state unchanged.
  2. A vanilla server's state with `matchServer` puts every change back.
  3. Taking the layer off restores the state.

  In the lab: `ok: 2255 change(s) exported, 2255 re-applied unchanged, 2255 put back for a vanilla server, 92 ms`.

## Config sync
- **Server**: the state lists every registered config of kind Server or Common that has `[Synced]` entries, with
  their running values as `SyncedValuesJson()`. Client configs and Kiln's own `sync` config are never sent. It is
  rebuilt after any config change (`Config.AnyChanged`), registration or unregistration, and every 250 steps (to
  catch values changed in code without `Save()`). It is sent to clients that lack its id.
- **Client**: each received config is applied to the local config with the same id (`ConfigFile.ApplyServerValues`):
  - Values are validated by the local schema. Out of range or of another type: refused, the local value stays, and
    the refusal is listed in `ConfigSyncedEvent.Errors` and in the ack.
  - `[RequiresRestart]` entries do not change a running mod: they are reported as pending. They apply at the mod's
    next load while connected.
  - A config the server no longer sends gets its local values back.
  - Editing the file while connected changes only the local value. The server's stays in force.
  - `Save()` writes the local value of server-held entries, never the server's.
  - Leaving calls `ClearServerValues` on every config.

## `kiln/config/sync.json` (Kiln's own config "sync", both sides)
| Key | Default | |
|---|---|---|
| `sendContent` | true | server: send content states |
| `sendConfigs` | true | server: send config states |
| `acceptContent` | true | client: apply a server's content |
| `acceptConfigs` | true | client: use a server's `[Synced]` values |
| `matchServer` | true | client: also put back local-only changes while connected |
| `maxContentKiB` | 8192 | largest content document either side handles (16..65536) |

It is a normal Kiln config: it reloads while running, and the Kiln menu shows it under "Kiln settings".

## Config screens (Kiln menu > Mods > Config)
Every mod with a registered config gets a **Config** button. The page is generated from the schema:
- bool: a checkbox;
- whole number or float with a `[Range]`: a slider (whole numbers with a span over 100000 get a text field); with
  `[Range(min, max, step)]` the slider moves from min in steps of step (docs/kiln/config.md), the number field does not;
  a soft side (`SoftMin` / `SoftMax = true`) ends the slider only: the number field takes a value past it, and the
  slider sits at its end for such a value;
- enum: `<` / `>` with the options in the tooltip;
- string, duration (`30s`, `5m`), list (`[1, 2]`): a text field.

The tooltip shows the comment, type, default, range, "synced" and "requires a restart". Edits are staged (a `*`
marks them) and saved with **Apply**: `ConfigFile.TrySet` validates and writes the file on the tick thread, and a
refused value stays on the page with the reason. **Undo** drops the staged edits. **default** stages one entry's
default and **All to defaults** stages every one.
- Client and common configs are editable. Entries that hold the server's value are read-only and marked
  `[server]`; the tooltip shows the player's own value.
- A **server config** is read-only while connected to a server (remote). It is editable when hosting or in the menu:
  it applies when you host.
- Saving needs a running world (the tick thread). In the main menu, Apply waits, shown as "(saved when a world
  runs)".
- Mod pages added with `Ui.AddConfigPage` still show below the generated one.

## Wire format
Channels `kiln:content` v1 and `kiln:config` v1, opened by Kiln itself (owner "kiln"), `required: false`. Message
ids are explicit.

| Channel / id | Direction | Payload |
|---|---|---|
| content 0 PART | server → client | u16 format (1), u32 state id, u64 generation, u32 raw length, u32 fields, u16 part, u16 parts (0 = not synced: data is the reason), u8 codec (1 = raw deflate), bytes data |
| content 1 ACK | client → server | u32 state id, u8 result (0 applied, 1 failed, 2 hosting, 3 refused), varint applied, reset, errors, ms, str detail |
| config 0 STATE | server → client | u16 format (1), u32 state id, configs `[str id, str mod, u8 kind, str values JSON]` (at most 512) |
| config 1 ACK | client → server | u32 state id, varint applied, varint refused, str detail |
| station-menu 0 PART | server → client | u16 format (1), str state id (64 hex), str session (36), u64 generation, u32 raw length, u16 part (from 0), u16 parts (1..4096), u8 codec (1 = raw deflate), bytes data (at most 96 KiB) |
| station-menu 1 ACK | client → server | str state id, str session, u64 generation, u8 stage (0 stored, 1 applied, 2 refused), str reason (at most 4096 bytes) |

## Station menus
Channel `kiln:station-menu` v1 (since 0.2.0-dev.31), opened by Kiln.Content at its start, `required: false`: on a server
always, on a game client only while it can draw menus (`stationmenus.render`), so a client never claims what it cannot
follow. What authors write: [modding/world-blocks.md](modding/world-blocks.md#menus-with-tabs-of-their-own-since-020-dev31).

- **Server.** On each join of a Kiln client that declared the channel, and after every change of its menus, it sends one
  full snapshot: `{"contentStateId": <kiln:content state>, "format": 1, "generation": <u64>, "menus": [records], "session":
  "<guid>"}` (canonical: keys sorted, compact). `menus: []` is the snapshot with no menu (it takes the previous ones
  off). The session is new for every world the server starts. The menu export and the content state come from one
  content generation (read before and after; tried again at the next tick on a change). State id: SHA-256 of the exact
  bytes sent, 64 lower-case hex. Limits: 64 MiB of JSON, 65 MiB deflated, 4096 parts. The hosting player's own game gets
  nothing (it draws from the same process).
- **Admission.** While a menu exists, a Kiln client that did not declare `kiln:station-menu` at exactly "1" is refused
  after its hello, before it enters the world, under every `modSync`: "station-menu protocol1 required". One in the
  world that announces its channels again without it is refused the same way. Until the mods' OnLoad pass and the
  blocks pass are done, joining Kiln clients are held (at most 25 s) and then decided. Clients without Kiln are not
  refused for menus: they never get a mod's own station (Kiln-only templates, [network.md](network.md#clients-without-kiln)).
  A first menu defined while a Kiln client without the channel is in the world is refused (`PeersLackStationMenus`).
- **Client.** It keeps the parts of one snapshot (another state, session or generation starts over; 30 s to get them
  all), checks the hash and the envelope (no definition or workshop twice), acks `stored`, waits until its content sync
  applied the named content state (at most 120 s), then swaps the server's collection in one call and acks `applied`.
  While the client waits, a mod's own workshops list nothing. If no snapshot part arrives within 120 s of the Kiln
  handshake, the client stops waiting without sending an ack. It also stops waiting if it refuses the snapshot or one
  of its records, if the remaining parts do not arrive within 30 s of the first, or if the named content state does
  not arrive within 120 s of staging the whole snapshot. In those cases it acks `refused` with the reason. When the
  client stops waiting, its workshops show this game's own menus (its local layouts, else the menus their recipes are
  placed in), and kiln.log says so once. The server sends a refused snapshot once and sends a new one on the next menu
  or content change. A late snapshot or a snapshot whose content state arrives late can still apply. Leaving the
  server takes the server's menus off. The same snapshot again changes nothing.

## Tests
Lab, without a client (`lab.sh new sync 25900 --save …`, `kiln/config/network.json` `{"labPort": 25901}`,
`kiln/config/sync-test-server.json` `{"maxHomes": 7}`, mods `bigstacks` + `sync-test`):
```sh
export KILN_OUT=<build dir>
dotnet build mods/samples/BigStacks/BigStacks.csproj -c Release -o $KILN_OUT/mods/bigstacks --artifacts-path $KILN_OUT/dotnet
dotnet build mods/tests/sync-test/SyncTest.csproj -c Release -o $KILN_OUT/mods/sync-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/sync --policy any --samples && tools/lab/lab.sh start sync
mods/tests/sync-test/synctest.py --port 25901 --lab ~/srv/enshrouded/labs/sync      # synctest: 35 PASS, 0 FAIL
```
The suite checks:
- the handshake: both channels in the table, not required;
- the content state at join:
  - integrity (length, hash, format);
  - field count;
  - wood logs and resin as the server reads them (BigStacks 1000, sync-test 777);
  - a whole recipe list (torch: Twigs x3 + Wood Logs x2 -> x2), a recipe field (rod 2 s);
  - no loot or drops;
- the ack in the server log;
- `ContentSync.CheckRoundTrip()` and `ConfigSync.CheckRoundTrip()` (the client-side config path on the server's own
  config: values, read-only, file untouched, restore);
- the config state at join: synced server entries and the synced section; unsynced, client and `sync` configs absent;
- the config ack;
- a synced config change pushed (0.1 s); an unsynced change not pushed;
- content pushes: a new field (0.2 s), a value-only change (5-10 s);
- a late joiner gets the current state; a peer without the channels gets nothing;
- `maxContentKiB` 16 → "not synced" notice → back to 8192 → the full state again;
- reverting the test's code layers pushed;
- padded frames and fragments (as kiln.net's game transport sends them), 101 B to 50 KB;
- malformed acks.

Also: `tools/nettest/nettest.py` 42/0 with these channels open, content-test 34/0 on the v2 service,
`reload-test sync-test 5`: 0 leaked contexts.

Client (one session, see the report): BigStacks on the server only; sync-test on both. sync-test logs `PASS client.…`
lines for content (resin 777, torch recipe, rod 2 s), configs (from server, read-only, file keeps local) and both
restores, and shows a HUD line `sync-test: wood max … | resin max … | homes … (server) | content Applied (…)`. With
`giveOnJoin` in the server config it gives the joined player one stack of that many wood logs after 15 s.

## Runtime (real client)
2026-11-04: a lab (bigstacks + sync-test, `maxHomes` 7, `giveOnJoin` 1000) on the same PC. Client build 26404966 with
sync-test only (no BigStacks), joined through Join > Dedicated server > Search by IPv4 (`127.0.0.1:25900`). Later joined
runs on the same kind of lab: [client.md](client.md), "Mods and sides". Not run: a joined session on a server on
another machine or over the internet.
- **Config sync**: `config state 234b7726 from the server: 2 config(s)`. On the client, `maxHomes` was 7 (the
  server's) with 3 in its file, read-only. Four `PASS client.config.*` lines.
  - A server file edit while connected (`maxHomes` 9) reached the client within 0.03 s of the server's reload.
  - Leaving gave `local values back (maxHomes)` and `PASS client.config.restored: maxHomes 3`, both times.
- **Content sync, first join: found a transport bug.** The 37 KB state (then one fragmented message) was dropped:
  `peer server: dropped frame (fragment overflow)`. kiln.net pads every game-transport frame to a multiple of 4 bytes,
  so the last fragment was up to 3 bytes too long; the lab transport does not pad.
  - Fixed in `Kiln.Network/Engine.cs`: a last fragment's zero padding is trimmed. `RegisterJson` decoders trim
    trailing NULs too.
  - The content sync now cuts parts to one frame.
  - `synctest.py` checks padded frames and fragments (`fragments.padded-*`).
- **Without the content state** (the vanilla-registry client, first join), the inventory still showed a stack of 1000
  wood logs. Split (500/500) and merge back to 1000 worked: the server decides, the client only displays.
- **Content sync, second join**: `content state 37028d93 from the server: 2257 change(s), 177.7 KiB (37.0 KiB
  compressed, 5 part(s))`, then `server sync: 2257 change(s), 0 error(s)`, then `applied … in 27 ms`, and the stack
  guard switched on.
  - `PASS client.content.applied / resin / torch / rod-duration`.
  - HUD: `wood max 1000 | resin max 777 | homes 7 (server) | content Applied (2257)`.
  - Inventory: a stack of 1000 was split to 499/501 and merged back to 1000. A drop onto a full stack of 1000 was
    refused.
  - Crafting UI (server-only recipe changes): **Campfire: Wood Logs 5 + Twigs 3** (vanilla: Wood Logs 2) and
    **Wooden Arrow x25** (vanilla x10). Crafting the arrows gave 25 arrows and took 2 twigs.
  - Changing BigStacks' multiplier to 2 on the server while connected: a new state reached the client in about 4 s,
    with wood max 500.
- **Leave**: `client world … is stopping: 2257 changed field(s) put back` (kiln.content, while the world was intact),
  then the connection closed (`content sync: left the server: 0 field(s)`: already back), then the configs came back.
  The game quit normally.
- **Kiln menu**:
  - Mods > sync-test > Config showed the common config (`feature [server]`, read-only), the client config (editable)
    and the server config (read-only, synced entries `[server]`).
  - Changing the client config's `anchor` (`<` / `>`), then **Apply**: toast `sync-test-client: saved`, the file was
    written, and the HUD line moved.
  - The Network tab showed `content: 2257 change(s) from the server (178 KiB)` and `configs: 2 synced config(s) from
    the server, 2 in use here`.

## Limits and open points
- This sync covers what the server can send. A mod that states `clients: "mod"` needs its own files on the client: a Kiln client without
  them is refused unless `modSync` is set to `warn` or `off` (docs/kiln/network.md, "Join handshake and mod sync").
- **Player animation graph.** This is compared, not synced: a server and every player's game merge the same mods'
  player states into the same player animation graph ([services/playeranim.md](services/playeranim.md)), and the join
  compares the two (`kiln.playeranim` in HELLO and WELCOME: 16 hex digits, zeros for the game's own graph or a side
  without the service; `PlayerAnimStates.GraphHash`). A client whose graph differs from the server's is refused with
  the difference in words (how many mod states each side has, and which), under `modSync: warn` too: it would play
  other states than the server evaluates, and the server's poses decide hits. `modSync: off` lets it in and logs one
  warning. Code: `managed/services/Kiln.Network/PlayerGraphGate.cs`. Works on a test server with a fake client; not
  run with a real client.
- A client without Kiln gets none of this. Refused or let in, see docs/kiln/network.md "Clients without Kiln".
  Real-client runs of that are still open (launches on hold).
- New items and recipes of mods with `"clients": "kiln"` (and placeholders) travel as `@definitions`; the client
  defines them as the "server sync" owner and sets them aside on leave (kiln.content A5). Their names and texts travel
  with the definitions; PNG icons do not (the mod must be on the client for those).
- The Impact programs a mod made at load and its clones name (a buff's `impact`, a template's `Impact.program`, a sequence's
  `SpawnImpact`) travel as `@programs` (`{guid, data}`, base64): only programs the server serves as new, never the game's.
  The client serves them through kiln.assets as the "server sync" owner before it applies the rest, so the clones'
  references resolve; one its game or its own mods already have stays as it is. They come off on leave
  (`SyncPrograms.cs`). item-blocks' 12 add about 40 KiB of JSON, under 8 KiB deflated.
- Code that writes game memory without kiln.content (a native plugin poking ItemInfo) is invisible to the export.
- Value-only changes reach clients within 10 s, not at once. The managed Content API could signal edits; see the
  report.
- `matchServer` cannot put back a local change of a field the server's state also holds. The server's value wins
  there anyway.
