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
     lacks a template"; `definitionsWaitSeconds` limits the wait).

  When local packs are re-applied while connected (a local mod reloads), the layer goes on top again.
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
  step): 4213 items, 2542 recipes … 0 random-loot items` (real client).
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
- whole number or float with a `[Range]`: a slider (whole numbers with a span over 100000 get a text field);
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
A lab (bigstacks + sync-test, `maxHomes` 7, `giveOnJoin` 1000). Client build 26404966 with sync-test only (no
BigStacks), joined through Join > Dedicated server > Search by IPv4 (`127.0.0.1:25900`).
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
