# KAP/1: the Kiln Admin Protocol

Kiln.Admin (`mods/Kiln.Admin`) serves server administration over two transports that share one op table
(`Bridge.Execute`):
- the **file bridge** (for external admin tools): `requests/<id>.json` -> `results/<id>.json`, `state.json`;
- **KAP/1** over TCP on 127.0.0.1, described here. The launcher's `kiln_manage::admin` client speaks it directly, or
  through an SSH tunnel for remote servers, and falls back to the file bridge.

Rust types: `kiln_manage::admin::protocol`
(`Frame`, `KapErrorCode`, `Endpoint`, `Limits`). Test vector: `schema/fixtures/kap/handshake.json`.

## 1. Endpoint and token
- The listener binds **127.0.0.1 only**, on `kiln/data/kiln-admin/config.json` `"tcp": {"enabled": true, "port": 0}`
  (the default when the key is missing). Port 0: the server's query port number on TCP, then query port + 1000, + 2000
  ... + 9000 (10 tries), then any free port; a fixed port (useful for SSH tunnels) falls back to a free port with a
  warning when it is taken. `"tcp": {"enabled": false}` turns KAP off. endpoint.json says which port it got.
- When the listener is up, Kiln.Admin writes `kiln/data/kiln-admin/endpoint.json` atomically
  (`schema/admin-endpoint.schema.json`): `{format: 1, protocol: "kap/1", host: "127.0.0.1", port, pid, started, kiln,
  admin, server{id, name}, token: "admin-token", bridge}`. It deletes the file on unload. `pid` is the Windows pid (under
  Wine not a host pid), so a client tells a stale file (a crashed server) by the listener: connect refused = stale.
- `kiln/data/kiln-admin/admin-token`: 64 lower-case hex characters (an optional trailing newline) = 32 random bytes
  from the OS CSPRNG, created on first start with mode 0600 on Linux (Wine: the host file's mode; the folder is the
  server owner's). The HMAC key is the 32 decoded bytes. The op `admin_token_rotate` replaces it (section 6).
- Anyone who can read the token file can administer the server; that is the trust boundary (same as the file bridge
  folder today).

## 2. Framing
- A frame is `u32` little-endian payload length, then the payload: one UTF-8 JSON object with a string field `"t"`
  (the frame type). No BOM, no trailing data.
- Limits: a client frame larger than 1 MiB is a protocol error (`too_large`, then close); a length of 0 is `bad_frame`
  (fatal). Server frames are at most 16 MiB (`ready.limits.maxFrame`; clients read it from there): a result that would
  be larger is answered `res{ok: false, code: "too_large"}` instead.
- Unknown fields are ignored. An unknown `"t"` gets an `error{code: "bad_frame"}` and the connection stays open.
- Ids (`id`) are unsigned integers chosen by the sender of the request (`req`, `sub`, `unsub`, `ping`), unique per
  connection while in flight; the answer carries the same id.

## 3. Handshake
```
server -> hello  {"t":"hello","proto":"kap/1","nonce":"<64 hex>","kiln":"0.2.0-dev","admin":"0.2.0",
                  "server":{"id":"srv-4b8e2c","name":"Ember Isles"},"auth":["hmac-sha256"]}
client -> auth   {"t":"auth","proto":"kap/1","cnonce":"<64 hex>","mac":"<64 hex>","client":"kiln-launcher/0.2.0-dev"}
server -> ready  {"t":"ready","session":"<id>","mac":"<64 hex>","ops":[...],"topics":["state","log"],
                  "limits":{"maxFrame":8388608,"maxClientFrame":1048576,"inflight":64,"pingS":15,"idleS":45}}
```
- `nonce` (server) and `cnonce` (client) are 32 random bytes each, sent as 64 lower-case hex characters.
- `mac = HMAC-SHA256(token, "kap1-auth\n" + nonce + "\n" + cnonce)`, lower-case hex; the nonces enter the message as
  their hex text (ASCII).
- The server checks `proto` (else `error{code: "version"}`, close) and the mac in constant time (else, after a 250 ms
  delay, `error{code: "auth_failed"}` and close).
- `ready.mac = HMAC-SHA256(token, "kap1-ready\n" + nonce + "\n" + cnonce)` proves the server knows the token too; the
  client closes when it does not match.
- Before `ready`, any frame other than `auth` gets `error{code: "auth_required"}` and close. The handshake must finish
  within 5 s of the connection.

Test vector (`schema/fixtures/kap/handshake.json`):

| | value |
|---|---|
| token file | `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f` |
| nonce | `202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f` |
| cnonce | `404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f` |
| auth message | `"kap1-auth\n" + nonce + "\n" + cnonce` |
| mac | `ef7614078851373ad50d58a39bd104753602237a30159343d9423520226e9a1f` |
| ready mac | `091f206355c6b66354a7fe484eef85df7919a5f105220f4877a9d6d21eeaf4cc` |
| hello frame | `c4000000` + the 196-byte payload in the file (`helloFrameHex`) |

## 4. Requests
```
client -> {"t":"req","id":7,"op":"teleport","args":{"entity":5,"x":100,"y":20,"z":-40}}
server -> {"t":"res","id":7,"ok":true,"op":"teleport","ms":38,"data":{...}}
server -> {"t":"res","id":8,"ok":false,"op":"heal","ms":0,"error":"entity must be an entity id","code":"op_failed"}
```
- `args` is the file bridge's request object without `"op"`; the op receives `args + {"op": op}`, so every op answers
  exactly as over the file bridge. `res` is the bridge's result object (`ok`, `op`, `ms`, `data` | `error`) plus `id`
  and, on failure, `code`: `op_failed` (the op threw / AdminError), `unknown_op` (error `"unknown op <name>"`, as the
  bridge says), `busy`, `timeout`, `shutting_down`, `too_large`.
- Requests drain on the tick thread in Pre, at most 32 per step for all connections, oldest first. Async ops
  (teleport, give_item, mod_reload, ...) answer when they finish; responses may come out of order.
- At most 64 requests in flight per connection; more get `res{ok: false, code: "busy"}` at once. A second `req` with an
  id still in flight gets `error{code: "bad_frame", id}` (non-fatal).
- A request not answered within its op's budget (default 30 s; `mod_test` 180 s, `backup_create` 90 s, `save_now`
  60 s, `mod_*` 45 s) gets `res{ok: false, code: "timeout"}`; a later result is dropped.

## 5. Subscriptions, pings, bye
```
client -> {"t":"sub","id":9,"topic":"state"}             server -> {"t":"res","id":9,"ok":true,"op":"sub"}
server -> {"t":"push","topic":"state","seq":1,"data":{...state.json...}}     (about every 1 s)
client -> {"t":"unsub","id":10,"topic":"state"}          server -> {"t":"res","id":10,"ok":true,"op":"unsub"}
either -> {"t":"ping","id":11,"ts":1762257600123}        other  -> {"t":"pong","id":11,"ts":1762257600123}
either -> {"t":"bye","reason":"unload"}                  then close
```
- Topics: `state` (the same object as the bridge's `state.json`: clock, kiln, ready, players, deaths, spawned, combat,
  mods; about every 1 s, i.e. every 25 steps), `log` (new `kiln.log` lines: `{"lines": [...]}`, batched per step, at
  most 256 lines per push). `ready.topics` lists what this server offers; others get `res{ok:false,
  code:"unknown_topic"}`. `seq` counts pushes per topic from 1.
- A new `state` subscription gets the latest state at once (right after the `res`), then about every 1 s. `sub` again
  on a subscribed topic is ok (no duplicate pushes). A slow client whose send queue exceeds 4 MiB loses pushes (the
  next push has a gap in `seq`), never responses.
- Either side sends `ping` after 15 s without sending anything; a peer silent for 45 s gets `bye{reason: "idle"}` and is
  closed. Kiln.Admin also pings a peer that has been silent for 15 s, even while it is busy pushing, so answering its
  pings is enough to stay connected. The launcher pings from its own thread (not from the page), and closes and
  reconnects when the server has sent nothing for 45 s. Kiln.Admin sends `bye{reason: "unload"|"shutdown"}`
  when it unloads (mod reload, server stop) and closes every connection; a client's `bye` closes its connection.
- Connections: at most 4 at once; the 5th gets `error{code: "busy"}` and close.
- Reload safety: the listener runs on a background thread and stops in `OnUnload`; `reload-test kiln-admin 20` must
  report 0 leaks.

## 6. Errors
`{"t":"error","code":"…","message":"…","id":7?}`. Fatal ones (closed after): `bad_frame` for an unparsable payload,
`too_large`, `auth_required`, `auth_failed`, `version`, `busy` (connection limit), `shutting_down`, `internal`.
Non-fatal: `bad_frame` for an unknown `t`. Op-level failures are `res{ok:false, code}`, never `error`.

| Code | Meaning |
|---|---|
| `bad_frame` | not JSON, no `t`, unknown `t`, zero length |
| `too_large` | over the frame limit |
| `auth_required` | a frame before `ready` that is not `auth` |
| `auth_failed` | wrong mac |
| `version` | `proto` is not `kap/1` |
| `unknown_op` | no such op on this server (res) |
| `op_failed` | the op failed; `error` is its message (res) |
| `busy` | in-flight or connection limit |
| `unknown_topic` | no such topic (res) |
| `timeout` | the op did not answer in time (res) |
| `shutting_down` | Kiln.Admin is unloading |
| `internal` | a bug |

## 7. Versioning
- `proto` is `kap/1`. Within kap/1 only additions: new optional fields, new frame types (old peers answer
  `bad_frame` and go on), new ops and topics (discovered from `ready.ops`/`topics`), new error codes. A breaking change
  is `kap/2`; a server may then offer both (`hello.proto` = the newest, `auth.proto` picks).
- Clients must check `ready.ops` before calling an op (`useHasOp`).

## 8. Ops
The op table is the file bridge's (`mods/Kiln.Admin/Ops*.cs`). Changes ops make (creature layers, content values) belong to Kiln.Admin and revert when it unloads,
like any mod's. Arguments are the request's fields; `entity` is a player's entity id unless noted. Source of
truth for each op's arguments and result: its `Ops.*.cs` file.

| Area (file) | Op | Arguments |
|---|---|---|
| core (AdminMod.cs) | `ping` | - |
| | `status` | - (kiln, features, ops) |
| players (Ops.Players.cs) | `players` | - |
| world (Ops.World.cs) | `enemies` | near?, radius? |
| | `teleport` (async) | entity, x, y, z, safe? |
| | `teleport_to_player` (async) | entity, target |
| items (Ops.Items.cs) | `inventory` | entity |
| | `give_item` (async) | entity, item, count?, level?, rarity? |
| | `item_search` | q |
| spawn (Ops.Spawn.cs) | `spawn` (async) | guid, count?, x?, y?, z?, spread? |
| | `spawn_item` (async) | item, amount?, x, y, z |
| | `despawn_spawned` | - |
| settings (Ops.Settings.cs) | `settings_get` | paths? |
| | `settings_set` | path, value |
| | `settings_save` | - |
| chat, time (Ops.ChatTime.cs) | `broadcast` | text |
| | `tell` | entity, text |
| | `command` | line |
| | `command_run` | line, steamId? |
| | `commands` | - |
| | `time_get` | - |
| | `time_set` | hours? / clock? / mode? |
| | `weather_reset` | - |
| combat (Ops.Combat.cs) | `health` | entity (any entity) |
| | `heal` | entity, amount? |
| | `god` | entity, on? |
| | `kill` (async) | entity (any entity) |
| | `damage` (async) | entity, amount, attacker? |
| rules (Ops.Rules.cs) | `creative` | entity, on? (toggles without it); refused for a player whose game has no Kiln with the rules channel |
| | `rules` | entity? (the hooks, presets, active rule sets, creative players; with entity that player's rules) |
| saves (Ops.Saves.cs) | `save_now` (async) | - |
| | `save_status` | - |
| | `backup_list` | - |
| | `backup_create` (async) | name? |
| | `backup_restore` | name (staged for the next start) |
| | `backup_cancel_restore` | - |
| gameplay (Ops.Gameplay.cs) | `gameplay_status` | - |
| | `zones_list` | - |
| | `zones_add` | name, x, y, z, radius, deny?, owners?, allowAdmins? |
| | `zones_remove` | name |
| content (Ops.Content.cs) | `content_status` | - (also definitions, placeholders, missing[]) |
| | `content_get` | kind, entry, field |
| | `content_set` | kind, entry, field, value |
| | `content_reset` | kind, entry, field |
| | `content_fields` | kind |
| | `content_search` | kind, q, limit? |
| | `content_entry` | kind, entry |
| | `content_recipe` | entry |
| | `content_definitions` | - |
| | `content_report` | - |
| | `content_dump` | - |
| | `content_reload` | - |
| | `sync_status` | - |
| config, permissions, commands (Ops.Config.cs) | `config_list` | - |
| | `config_get` | id |
| | `config_set` | id, path, value |
| | `config_reload` | id |
| | `permissions_get` | - |
| | `permissions_check` | node, steamId \| entity |
| | `permissions_player_node` | steamId, node, allow (true/false/null), name? |
| | `permissions_player_group` | steamId, group, add?, name? |
| | `permissions_group` | group, admin?, inherits?, node?, allow? |
| | `permissions_group_remove` | group |
| | `permissions_map_user_group` | userGroup, groups |
| | `permissions_reload` | - |
| | `commands_describe` | - |
| | `command_complete` | text, steamId? |
| | `events_stats` | - |
| mods (Ops.Mods.cs) | `mods` | - |
| | `mod_load` (async) | folder (a folder name under kiln/mods) |
| | `mod_reload` (async) | id |
| | `mod_unload` (async) | id |
| | `mod_test` (async) | id, filter? |
| | `crash_reports` | name? (one report's text) |
| creatures (Ops.Creatures.cs; when Kiln.Creatures is loaded) | `creature_status` | - |
| | `creature_catalog` | filter?, kind? (enemy, wildlife, animal, villager) |
| | `creature_near` | x, y, z, radius (<= 2000) |
| | `creature_inspect` | entity (any entity) |
| | `creature_set` | entity, field (level, faction, scale, xp, speed, damage, home, loot-labels, loot-slots, loot-off, feature-on, feature-off, arsenal, resist:&lt;kind&gt;), value (number, text, true/false, {x,y,z}, [min,max]) |
| | `creature_ai` | entity, path (component.field), value? (sets) |
| | `creature_reset` | entity |
| | `creature_spawn` (async) | what (variant id or template), x, y, z, count? (1-50); tag "admin", kept when Kiln.Admin unloads |
| | `creature_variants` | - |
| | `creature_command` | entity, action (attack, guard, stopguard, stay, heal, moveto), target? \| x, y, z |
| game data (Ops.GameData.cs; when kiln.gamedata is loaded) | `gamedata_status`, `gamedata_configs` | - |
| | `gamedata_definitions` | - (the clone entries of every section) |
| | `gamedata_search` | section, q?, limit? (default 200, max 2000) |
| | `template_get`, `arsenal_get`, `sequence_get` | entry (name, GUID or 0x id; arsenals also `template:<name>`), path? (none: the whole entry) |
| | `template_set`, `arsenal_set`, `sequence_set` | entry, path, value (pack JSON) -> `{applied, value}` |
| | `template_reset`, `arsenal_reset`, `sequence_reset` | entry, path -> `{reset, value}` |
| | `template_fields` | entry, prefix? (at most 2000 fields) |
| | `gamedata_get`, `gamedata_set`, `gamedata_reset` | section (templates, arsenals, sequences, buffs, perks, skills, items), entry, path, value? |
| | `gamedata_dump` | section, entry? |
| world editing (Ops.Build.cs; when kiln.build is loaded) | `build_status` | - -> Build.Status counters + `grid` {cellSize, origin, cellsX, cellsZ, tileCells, level} |
| | `build_bases` | - -> [{altar, center, half, abandoned}] |
| | `build_regions` | - -> {keptChunks} |
| | `build_release` | box {x, y, z, sx, sy, sz} in cells (or those fields at the top level) -> {released} |
| | `build_read` (async) | x, y, z, sx, sy, sz in cells (a cell = world metres / grid.cellSize), at most 65536 cells -> {box, corner (world), cells, solid, data}: base64 of u16 LE cells (low byte material, high byte fill), index (z*sy + y)*sx + x |
| | `build_shape` (async) | **edit** (dig, add, flatten, smooth, paint, replace; `op` names the request, so the spec's `op` is `edit` here), shape (box, sphere, cylinder; default box), x, y, z, hx (radius), hy?, hz? (0-64 m), material? (add, paint, flatten), from?, to? (replace), level? (flatten), strength?, passes? (smooth), flags? (list of InBases, RespectZones, AllowAltars, DryRun, Transient) -> {cellsChanged, blocksWritten, verified, kept, box, ms, refusal (None, Zone, Base, NotLoaded, DryRun), zone}; inside a player base without InBases: refusal Base |
| | `build_props` | min {x,y,z}, max {x,y,z}, filter? (placed, saved, all, static) -> at most 1000 [{entity, template, templateName, pos, item, kind, flags}] |
| | `build_prop_place` | template (name or GUID), x, y, z, yaw? (degrees), item? -> the prop |
| | `build_prop_remove` | entity -> {removed} |
| | `build_schematics` | - -> the `.kschem` files in kiln/data/kiln.build/schematics: [{name, bytes, size [x,y,z], props, hash}] |
| | `build_capture` (async) | name (A-Z a-z 0-9 . _ -, at most 64), min {x,y,z}, max {x,y,z} -> saves `<name>.kschem`; {name, size, props, hash, bytes} |
| | `build_paste` (async) | name, x, y, z, rotation? (0, 90, 180, 270), mode? (replace, merge), flags? -> {box, cells (as build_shape), propsPlaced, propsFailed, propsCleared, journalId, ok} |
| | `build_undo` (async) | owner? (`admin` = Kiln.Admin's own edits, the default; `player:<entity>`; a mod id) -> {id, label, cellsRestored, cellsKept, propsRemoved, propsRestored, complete}; fails when there is nothing to undo |
| | `build_history` | owner? (none: every owner) -> [{id, owner, label, kind, at, cells, propsPlaced, propsRemoved}] (this session's journal; Kiln.Admin's owner is `kiln-admin`) |
| | `build_placeonce_list`, `build_placeonce_forget` | mod? (only `kiln-admin`: PlaceOnce records are per mod), key (forget) -> [{key, placedAt, at, rotation, schematic, hash, cells, props}] / {forgotten} |

More ops (both transports serve them):

| Op | Arguments | Result |
|---|---|---|
| `admin_info` | - | `{admin, kiln, api, protocol: "kap/1", transports: ["file","tcp"], endpoint: {host, port} \| null, server: {id, name}, uptimeS, connections, ops, topics}` |
| `network_status` | - | Kiln.Network: `{config: <network.json values>, configFile, configError, serverId, serverName, kiln, modpack, advertise, pending, required: [{id, version, hash, kind, name}], clients: [{entity, name, state, kiln, version, lab, mods: [{id, version, side}], missing: [...], warnings, roundTripMs}], transport: {...}}` (`pending`: the required mods' kdh1 are still being computed) |
| `network_reload` | - | re-reads `kiln/config/network.json`, hashes the required mods again; returns `network_status` |
| `native_status` | - | the content of `state/native.json` (with `present: false` when there is none) plus `chain` (`state/boot.json` `chain[]`) and `boot` (its kiln, pid, written, policy, hooks, safeIdle, native, profile) |
| `log_tail` | lines? (default 200, max 5000), file? (`kiln` = kiln/logs/kiln.log, the default; `game` = the game's enshrouded_server.log; or a file name under kiln/logs, no paths) | `{file, lines: [...], size}` |
| `world_status` | detail? | Kiln.Quests' world progress: `{index, flame, difficulty, spires, bosses, survivors, counts, players}`; `detail` adds `shrines`, `shroudRoots`, `obelisks` (docs/kiln/services/world-progress.md) |
| `world_status_player` | target (entity or name) | one player's progress: counts, every spire, shrine, root, obelisk and tracked boss with that player's value, started quests |
| `world_chests` | x, y, z, radius? (default 8, max 64) | `{chests: [{lootId, template, position, loaded, entity, opened, looted}]}` |
| `admin_token_rotate` | - | writes a new `admin-token`, returns `{rotated: true}`; every other connection gets `bye{reason: "token"}`, the calling one stays open |

## 9. Remote servers: the SSH tunnel recipe
The launcher never stores SSH keys or passwords: it uses the user's ssh config and agent, non-interactively.
```sh
ssh -o BatchMode=yes myhost cat /srv/enshrouded/ember/kiln/data/kiln-admin/endpoint.json    # port, pid
ssh -o BatchMode=yes myhost cat /srv/enshrouded/ember/kiln/data/kiln-admin/admin-token      # the key
ssh -N -o BatchMode=yes -o ExitOnForwardFailure=yes -L 127.0.0.1:<local>:127.0.0.1:<port> myhost
# then connect to 127.0.0.1:<local> and speak KAP/1 as usual
```
`kiln_manage::admin::tunnel::SshTunnel` runs that `ssh -N` child (a free local port, `ServerAliveInterval=15`), waits
until the forward accepts connections, and kills the child on close or drop. `AdminHub` reconnects a dropped session
by running the whole recipe again (endpoint, token, a new tunnel), so a restarted server with a new port is found. The
token is held in memory only. `$KILN_SSH` overrides the ssh binary. Remote machines are Linux (POSIX shell, `cat`).
A remote server without KAP (an old Kiln.Admin) is not reachable from the launcher (no remote file bridge).

## 10. Fallback: the file bridge
`mods/Kiln.Admin/Bridge.cs`: the folder `<server>/shroudforge/exports/emberwatch` (or
`kiln/data/kiln-admin/config.json` `"bridge"`), `requests/<id>.json` (`{"op": ..., ...args}`, id `[A-Za-z0-9-]{1,64}`)
handled once on the tick thread and deleted, `results/<id>.json` = `{ok, op, ms, data | error}`, `state.json` about
once a second, all written through a temp file + rename. No push: the client polls `state.json`. External
tools keep using it while the launcher uses TCP.
