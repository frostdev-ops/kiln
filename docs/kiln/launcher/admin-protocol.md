# KAP/1: the Kiln Admin Protocol

Kiln.Admin (`mods/Kiln.Admin`) serves server administration over two transports that share one op table
(`Bridge.Execute`):
- the **file bridge** (for external admin tools): `requests/<id>.json` -> `results/<id>.json`, `state.json`;
- **KAP/1** over TCP on 127.0.0.1, described here. The launcher's `kiln_manage::admin` client speaks it directly, or
  through an SSH tunnel for remote servers, and falls back to the file bridge.

A web page (Kilnwarden's panel; Kiln Web Admin before its rename) gets the same KAP/1 frames as JSON over a WebSocket
from its host, which holds the admin token and speaks TCP KAP/1 to Kiln.Admin (section 12).

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
server -> ready  {"t":"ready","session":"<id>","mac":"<64 hex>","ops":[...],"topics":["state","log","metrics"],
                  "limits":{"maxFrame":8388608,"maxClientFrame":1048576,"inflight":64,"pingS":15,"idleS":45},
                  "restart":{"supported":"supported","via":"kilnctl","code":75}}
```
- `nonce` (server) and `cnonce` (client) are 32 random bytes each, sent as 64 lower-case hex characters.
- `mac = HMAC-SHA256(token, "kap1-auth\n" + nonce + "\n" + cnonce)`, lower-case hex; the nonces enter the message as
  their hex text (ASCII).
- The server checks `proto` (else `error{code: "version"}`, close) and the mac in constant time (else, after a 250 ms
  delay, `error{code: "auth_failed"}` and close).
- `ready.mac = HMAC-SHA256(token, "kap1-ready\n" + nonce + "\n" + cnonce)` proves the server knows the token too; the
  client closes when it does not match.
- `ready.restart` (since 0.2.0-dev.30.9; absent before): whether a stop asked for with `server_restart` comes back by
  itself. `{supported: "supported", via: "kilnctl", code: 75}` under kilnctl's systemd unit, else
  `{supported: "unknown", via: null, code: null}` (section 11). Not in `hello`: nothing more is said before the
  handshake.
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
- A request not answered within its op's budget (default 30 s; `mod_test` and `update_stage` 180 s, `backup_create`
  90 s, `save_now` 60 s, `mod_*` 45 s) gets `res{ok: false, code: "timeout"}`; a later result is dropped.

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
  most 256 lines per push), `metrics` (since 0.2.0-dev.30.9, see below). `ready.topics` lists what this server offers;
  others get `res{ok:false, code:"unknown_topic"}`. `seq` counts pushes per topic from 1.
- `metrics`: every 2 s, to its subscribers only. While nobody is subscribed Kiln.Admin measures nothing and has no Post
  tick handler (a Post listener costs the server some parallelism). The first push comes about 2 s after `sub`.
  ```
  {"at": 1763913600000, "windowS": 2.003, "steps": 120, "stepsPerS": 59.91,
   "frameMs": {"avg": 16.7, "p95": 18.1, "max": 20.3}, "tickMs": {"avg": 0.71, "p95": 2.88, "max": 8.1},
   "memory": {"workingSet": 1060000000, "managedHeap": 21000000}, "cpu": {"percent": 41.2, "cores": 16},
   "gcPauseMs": 4.0, "players": 3}
  ```
  `at`: Unix milliseconds at the window's end. `frameMs`: from one server step's start to the next (25 steps/s idle,
  about 60 with players). `tickMs`: one step itself, Kiln's Pre to Post (the world's sim and jobs); null when no step
  finished in the window. `memory`: the process's working set and the managed heap Kiln and every mod share, in
  bytes. `cpu.percent`: process CPU over the window, 100 = one core (null on the first push). `gcPauseMs`: garbage
  collection pauses in the window (the tick thread waits for them). `players`: players in game (null when Kiln.Players
  is not there).
- A new `state` subscription gets the latest state at once (right after the `res`), then about every 1 s. `sub` again
  on a subscribed topic is ok (no duplicate pushes). A slow client whose send queue exceeds 4 MiB loses pushes (the
  next push has a gap in `seq`), never responses.
- Either side sends `ping` after 15 s without sending anything; a peer silent for 45 s gets `bye{reason: "idle"}` and is
  closed. Kiln.Admin also pings a peer that has been silent for 15 s, even while it is busy pushing, so answering its
  pings is enough to stay connected. The launcher pings from its own thread (not from the page), and closes and
  reconnects when the server has sent nothing for 45 s. Kiln.Admin sends `bye{reason: "unload"|"shutdown"|"restart"}`
  when it unloads (mod reload, server stop, a stop for `server_restart`) and closes every connection; a client's `bye`
  closes its connection.
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
| | `command_run` | line, steamId?, source?, level? (section 11); the result also has `source` |
| | `commands` | - |
| | `time_get` | - |
| | `time_set` | hours? / clock? / mode? |
| | `weather_reset` | - |
| combat (Ops.Combat.cs) | `health` | entity (any entity) |
| | `heal` | entity, amount? |
| | `god` | entity, on? |
| | `kill` (async) | entity (any entity) |
| | `damage` (async) | entity, amount, attacker? |
| rules (Ops.Rules.cs) | `creative` | entity, on? (toggles whole mode without it); or entity, effect (modular Creative effect name), on (explicit boolean required). Effect mutations require active Creative and preserve other owners/preset exclusions/server policy. Returns effects status; no new privilege or operation name. Refused for a player whose game has no Kiln with the rules channel |
| | `rules` | entity? (the hooks, presets, active rule sets, creative players; with entity that player's rules and modular effect status; remains read-only) |
| | `creative_give` | entity, item (name or id), count? (1..one stack, at most 9,999), level? (gear's range), rarity? (Common/Uncommon/Rare/Epic/Legendary string); async, refused unless the player is in creative mode; one pending take per player, 250 ms between admissions |
| | `creative_cycle` | mode: normal, stopped or fast (the day and night cycle for the whole world, this session) |
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
| | `commands_describe` | source?, level? (the commands that source may run, with each argument's form data; section 11) |
| | `command_complete` | text, steamId?, source?, level? |
| | `events_stats` | - |
| mods (Ops.Mods.cs) | `mods` | - |
| | `mod_load` (async) | folder (a folder name under kiln/mods) |
| | `mod_reload` (async) | id |
| | `mod_unload` (async) | id |
| | `mod_test` (async) | id, filter? |
| | `crash_reports` (async) | name? (one report's text) |
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
| replication (Ops.Repl.cs; when Kiln.Replication is loaded, [../services/replication.md](../services/replication.md)) | `repl_stats` | off? (true: Kiln's own counters off) -> the `ReplPeerStats` rows per peer and channel; the first call turns Kiln's counters on and returns no rows yet |
| | `repl_watch` (async) | entity, peer? (a name or peer id; none: every peer), seconds? (1..30, default 3) -> the `ReplSent` events of that time and the `ReplWatchEnded` reason when the watch ended before |
| | `repl_capture` | peer, seconds (1..600) -> the capture file's name under `kiln/logs` (`repl-<peer>-<utc>.jsonl`); one capture at a time |
| | `repl_component` | name -> the `ReplComponentInfo` (index, size, hash, replication bit, versioned, custom codec, channels) with `found: true`; `{found: false}` for an unknown name |

More ops (both transports serve them):

| Op | Arguments | Result |
|---|---|---|
| `admin_info` | - | `{admin, kiln, api, protocol: "kap/1", transports: ["file","tcp"], endpoint: {host, port} \| null, server: {id, name}, uptimeS, connections, ops, topics, restart: {supported, via, code, request}}` (`restart`: as `ready.restart`, plus `request`, the restart under way or the last one, as `server_restart` answers it, or null) |
| `server_restart` | seconds? (0 to 3600, default 60), message? (one line, at most 200 characters: added to the notices of a minute or more), by? (at most 120 characters; default the KAP client's name) | `{state: "counting", by, requested, secondsLeft, message, error: null, via: null, restart: {supported, via, code}}`. A countdown in chat, the world save, then the server's own stop (section 11). Fails while one is under way |
| `server_restart_cancel` | by? | `{canceled: true}`; only while counting down |
| `login_mint` | consumer (1 to 64 of `a-z 0-9 . _ -`), label? | KAP only. `{secret: <64 hex>, consumer, expiresAt, ttlS: 60}` (section 11) |
| `login_redeem` | consumer, secret | KAP only. `{role: "owner", consumer, label, mintedBy, mintedAt}`; a wrong, used or expired secret fails |
| `network_status` | - | Kiln.Network: `{config: <network.json values>, configFile, configError, serverId, serverName, kiln, modpack, advertise, pending, required: [{id, version, hash, kind, name}], clients: [{entity, name, state, kiln, version, lab, mods: [{id, version, side}], missing: [...], warnings, roundTripMs}], transport: {...}}` (`pending`: the required mods' kdh1 are still being computed) |
| `network_reload` | - | re-reads `kiln/config/network.json`, hashes the required mods again; returns `network_status` |
| `native_status` (async) | - | the content of `state/native.json` (with `present: false` when there is none) plus `chain` (`state/boot.json` `chain[]`) and `boot` (its kiln, pid, written, policy, hooks, safeIdle, native, profile) |
| `log_tail` (async) | lines? (default 200, max 5000), file? (`kiln` = kiln/logs/kiln.log, the default; `game` = the game's enshrouded_server.log; or a file name under kiln/logs, no paths) | `{file, lines: [...], size}` |
| `world_status` | detail? | Kiln.Quests' world progress: `{index, flame, difficulty, spires, bosses, survivors, counts, players}`; `detail` adds `shrines`, `shroudRoots`, `obelisks` (docs/kiln/services/world-progress.md) |
| `world_status_player` | target (entity or name) | one player's progress: counts, every spire, shrine, root, obelisk and tracked boss with that player's value, started quests |
| `world_chests` | x, y, z, radius? (default 8, max 64) | `{chests: [{lootId, template, position, loaded, entity, opened, looted}]}` |
| `admin_token_rotate` | - | writes a new `admin-token`, returns `{rotated: true}`; every other connection gets `bye{reason: "token"}`, the calling one stays open |

Staged updates (0.2.0-dev.30.9, `Ops.Updates.cs` and `Staging.cs`; [staged-updates.md](../staged-updates.md)). A mod or
Kiln update is staged while the server runs and applied at its next start, before anything loads it. The previous
version is kept for a rollback. Kiln.Admin only stages from files that are already on the server: downloading and
checking signatures is the caller's job (Kilnwarden). `unit` is `"kiln"` or `"mod.<folder>"` (a folder under
`kiln/mods`). `by` (optional, at most 120 characters, default `"kiln-admin"`) says who asked; it goes into the records
and `kiln.log`. Dedicated servers only: on a game client `update_stage` and `update_rollback` are refused.

| Op | Arguments | Result |
|---|---|---|
| `update_list` | - | `{units: [{unit, kind: "kiln"\|"mod", folder, id, current: {version} \| null, staged: {version, from, staged, by, label, files, bytes, remove} \| null, previous: {version, replacedBy, kept, reason} \| null, rollback: {requested, by} \| null, probation: {state, version, error} \| null, open}], results: [the newest 20 result records], incoming}` (`open`: a change waits for the next start to finish it; `incoming`: the folder sources must sit in) |
| `update_stage` (async) | kind (`mod` \| `kiln`), source (a folder under `kiln/staged/incoming`: a name there or a full path; copied, never moved), id? (mod: must match its `kiln.mod.json`), folder? (mod: default the `kiln/mods` folder that holds that id now, else the id), files? (`[{path, sha256}]`: the caller's list from the archive it checked; the source must match it exactly, and every file is checked again on the bytes it copies), label?, sha256? (kiln: the bundle zip's, for the receipt), by? | `{unit, staged: {version, from, staged, by, label, files, bytes, remove}, note}`. Refused: a source outside `kiln/staged/incoming`, a link or a bad path in it, an update or a rollback of that unit already waiting (cancel it first), a change still open, a mod folder Kiln's receipt lists (it updates with Kiln), a mod folder named by another folder's Windows short name, a Kiln bundle whose bytes are not its `kiln-runtime.json`'s or that would write outside what a Kiln update may write (never `kiln/data`), a `dinput8.dll` that is not Kiln's, a Kiln version that is installed already, too little free space (the copy must leave 2 GiB plus the world's savegame folder free on the volume of `kiln/staged`; the error says how much is needed and how much is free). A staging that outlasts its 180 s budget still finishes (the reply is a timeout): `update_list` says whether the unit is staged |
| `update_cancel` | unit | `{unit, canceled: ["update"?, "rollback"?]}`; fails when nothing waits |
| `update_rollback` | unit | `{unit, requested: true, to, from, note}` (`to` = the kept version; `""` when it was not installed before, so the rollback removes it); fails without a kept version, while an update of that unit is staged, or (Kiln) once kilnctl or the launcher installed Kiln since the change (a rollback would mix two versions) |

## 9. Remote servers: the SSH tunnel recipe
The launcher never stores SSH keys or passwords: it uses the user's ssh config and agent, non-interactively.
```sh
ssh -o BatchMode=yes myhost cat /srv/enshrouded/ember/kiln/state/boot.json                  # profile.dataDir (below)
ssh -o BatchMode=yes myhost cat /srv/enshrouded/ember/kiln/data/kiln-admin/endpoint.json    # port, pid
ssh -o BatchMode=yes myhost cat /srv/enshrouded/ember/kiln/data/kiln-admin/admin-token      # the key
ssh -o BatchMode=yes -W 127.0.0.1:<port> myhost     # one per connection: stdin/stdout are the KAP/1 stream
```
(0.2.0-dev.31.12) `kiln_manage::admin::tunnel::SshTunnel` binds 127.0.0.1:<local> itself and holds it for the tunnel's
life; each connection accepted there runs its own `ssh -W` child (`ServerAliveInterval=15`) and the bytes are copied
between the two. No port is looked up, released and handed to ssh any more (another program could take it in between),
and ssh opens no listener. Closing or dropping the tunnel stops accepting and kills every child; a connection that ssh
could not forward reports what ssh said (`ssh tunnel to <host>: ...`). The held port serves only the launcher's own
user's programs on Linux (`kiln_manage::admin::peer`, the socket table; a connection whose owner is not found is
refused) and at most 8 connections at once (`tunnel::MAX_CHILDREN`; one more is closed: every connection costs an ssh
sign-in). Since ssh connects and signs in before the first byte, the KAP hello through a tunnel may take
`tunnel::SSH_SETUP` (20 s) longer than the handshake. Kiln.Admin's folder follows the data folder the server booted
with: when `kiln/state/boot.json` names a `profile.dataDir` (a profile with `dataDir: profile`) that the launcher can
map (locally: `Z:` mapped to `/`, other drives through the server's Wine prefix, relative paths under `<server>/kiln/`;
remotely: `Z:` and absolute paths), the server's data root is that folder and Kiln.Admin's files are read from
`<dataDir>/kiln-admin/` only; otherwise from `kiln/data/kiln-admin/` (`kiln_manage::admin::datadir`). The root is read
once per connection, and endpoint.json and admin-token always come from the same folder. `AdminHub` reconnects a dropped session
by running the whole recipe again (endpoint, token, a new tunnel), so a restarted server with a new port is found. The
token is held in memory only. `$KILN_SSH` overrides the ssh binary. Remote machines are Linux (POSIX shell, `cat`).
A remote server without KAP (an old Kiln.Admin) is not reachable from the launcher (no remote file bridge).

## 10. Fallback: the file bridge
`mods/Kiln.Admin/Bridge.cs`: the folder `<server>/shroudforge/exports/emberwatch` (or
`kiln/data/kiln-admin/config.json` `"bridge"`), `requests/<id>.json` (`{"op": ..., ...args}`, id `[A-Za-z0-9-]{1,64}`)
handled once on the tick thread and deleted, `results/<id>.json` = `{ok, op, ms, data | error}`, `state.json` about
once a second, all written through a temp file + rename. No push: the client polls `state.json`. External
tools keep using it while the launcher uses TCP.

Since 0.2.0-dev.30.9 the folder is read and written by the bridge's own thread, never by the tick thread: a request
file is read and deleted there, runs on the tick thread at the next poll, and its result file is written there again.
When the disk is slow (a world save forcing its files to disk), `state.json` gets the newest state once the disk
answers and the states in between are skipped; `kiln.log` says `file bridge: writing state.json took N ms (the disk is
busy)` at most once a minute. The ops that only read files (`log_tail`, `native_status`, `crash_reports`) run on the
thread pool for the same reason. (Before, one Pre handler of Kiln.Admin once took 810 ms while Kiln.Saves waited 3.66 s
for the disk.)

One responder per folder. The Emberwatch Lua mod (a ShroudForge mod, `mods/emberwatch-admin`) answers the same folder
in the same way, and Kiln's ShroudForge support runs it next to Kiln.Admin when both are installed: each request is
then answered by whichever reads it first, and `state.json` alternates between the two forms (the Lua mod's has no
`kiln`, and its empty lists are `{}`). Kiln.Admin logs a warning at load when it finds such a state.json. Remove one of
the two, or give Kiln.Admin its own folder with `"bridge"`. The launcher's `auto` transport leaves the bridge for TCP
as soon as Kiln.Admin's listener answers, so it is not affected.

## 11. Restart, one-time login, commands as forms (0.2.0-dev.30.9)
Additions for Kilnwarden (then still Kiln Web Admin), all within kap/1 (new ops, a new topic, new optional fields).

### Restart
`server_restart` counts down in chat, saves the world and stops the server the way a Ctrl-C does. Whether the server
comes back depends on what runs it:
- **kilnctl's systemd unit** (`kiln@.service`): `kilnctl run` gives the server `KILN_RUN_ID` (an id for this run) and,
  when it is the unit's main process, `KILN_RESTART=kilnctl`. Kiln.Admin then reports `restart.supported:
  "supported"`, `via: "kilnctl"`, `code: 75`. After the server has exited, `kilnctl run` reads
  `kiln/data/kiln-admin/restart.json`, deletes it, and exits **75** (EX_TEMPFAIL) when the marker is this run's (same
  `runId`, written after the run started) and the server exited 0. The unit counts 75 as a clean exit
  (`SuccessExitStatus=75`: no failed state) and starts the server again after `RestartSec` (15 s) anyway
  (`RestartForceExitStatus=75`). Restarts count toward the unit's start limit (5 in 10 minutes).
- **Everything else** (the Kiln Launcher's own starts, `kilnctl start` without systemd, hosting panels, a plain
  Windows server): `supported: "unknown"`. The op then stops the server and nothing known brings it back; a panel
  should offer "Stop" with a hint instead of "Restart". The game process itself exits 0, as after any Ctrl-C.

The sequence, on the tick thread unless noted:
1. Notices in chat: at the request (`The server restarts in 5 minutes.` plus `message`), then at 10, 5 and 2 minutes, 1
   minute, 30 and 10 seconds and every second from 5 (only the marks below the delay). "stops" instead of "restarts"
   when it is not supported. `server_restart_cancel` ends it until the countdown is over.
2. At zero: `The server restarts now: saving the world.`, then Kiln.Saves' world save (`save_now`'s). A failed save
   stops nothing: `request.state` becomes `failed` with the reason, and the server keeps running.
3. The marker `kiln/data/kiln-admin/restart.json` (written off the tick thread, the only file the restart writes):
   `{format: 1, runId: <KILN_RUN_ID> | null, pid, at, by, message}`. Kiln.Admin deletes a leftover one when it loads.
4. The stop: `kernelbase!CtrlRoutine(CTRL_C_EVENT)` on a thread of its own (what Wine runs on SIGINT and Windows on a
   console's Ctrl-C), so Kiln.Host's handler unloads the mods and the game logs `Trigger gameflow shutdown, exit:
   Ctrl_C`, saves and exits 0. KAP clients get `bye{reason: "restart"}`. When the stop has not begun 10 s later:
   `GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)`; 10 s after that the restart fails (`request.state: "failed"`, the
   marker is deleted, chat is told). The process is never killed.

`request` (in `server_restart`'s answer and `admin_info.restart.request`): `{state: "counting" | "saving" |
"stopping" | "failed" | "canceled", by, requested, secondsLeft, message, error, via, restart}`; `via` is how the stop
was raised.

### One-time login
For "Open web panel" in the launcher: the launcher holds the admin token, so it is the owner. It asks for a secret for
a named consumer, and that consumer (the web panel mod, also connected with the admin token) exchanges it once for
"owner". (0.2.0-dev.31.14) The consumer is the id of the web panel mod whose `launcher-endpoint.json` the launcher read:
`kilnwarden` for Kilnwarden, `kiln-webadmin` for a Kiln Web Admin 0.1.0 test build (its old name). Launchers
0.2.0-dev.31.2 to 31.13 always mint for `kiln-webadmin`; Kilnwarden redeems those too (a refused redeem for `kilnwarden`
is tried once for `kiln-webadmin`). Kiln.Admin takes any consumer name, so nothing changed on its side.
- `login_mint {consumer, label?}` -> `{secret, consumer, expiresAt, ttlS: 60}`: 32 random bytes (64 hex characters),
  valid 60 s and for one `login_redeem`. At most 8 live secrets per consumer (minting a 9th drops the oldest).
- `login_redeem {consumer, secret}` -> `{role: "owner", consumer, label, mintedBy, mintedAt}`; `mintedBy` is the
  minting connection's `auth.client`. Every live secret of the consumer is compared in constant time; a wrong, used,
  expired or other consumer's secret fails with the same message.
- KAP only: over the file bridge both fail (a result file would keep the secret on disk). Only a SHA-256 of each secret
  is kept, in memory; secrets are never logged (kiln.log says who minted for whom, and who redeemed).
- (0.2.0-dev.31.12) The launcher never puts the secret on a command line: it opens a one-use bootstrap address of the
  server's web panel gateway (contracts.md, `admin_open_web_panel`) whose page holds the secret and sends the browser on
  to `https://<the server's own loopback origin>/launcher#secret=` with `location.replace`. On Linux the page is only
  served to a program of the launcher's own user. The secret is minted by the Kiln.Admin of the same data root the
  panel's `launcher-endpoint.json` was read from (one boot.json read per open), so it only ever goes to the panel
  beside that Kiln.Admin. The panel's page removes the fragment from the address bar at once;
  a browser's history may still list the used URL, and the secret in it is dead (one use, 60 s).
- (0.2.0-dev.31.19) When the panel's `launcher-endpoint.json` names its address on the internet (`public`, Kilnwarden
  0.1.4) and the launcher's check of it passes (the certificate the file names, the owner exists, the launcher's login
  is on; contracts.md, `admin_open_web_panel`), the bootstrap page sends the browser to `<public>launcher#secret=`
  instead, and the panel redeems it the same way through the relay. Kilnwarden refuses such a login through the relay
  while no owner exists (a setup grant works on the server's own computer only), and its threat model for the public
  address is in its docs/protocol-relay-broker.md §7.1.

### Commands as forms
`commands_describe` gives a panel what it needs to turn every command into a form. Per command (unchanged): `name`,
`aliases`, `usage`, `description`, `permission` (its level: everyone, admin, console), `node`, `owner` (the mod's id),
`typed`, `usageLines`, `tree`. Per tree node, new: `level` next to `permission` (a subcommand's own node), and for an
argument `optional` (true when the line may end before it) and `default` (a `[Command]` method's parameter default: a
number, true/false, text, an enum value in lower case, a duration in seconds). Per argument type, added to `type` and
`describe`:

| type | fields |
|---|---|
| `int`, `double` | `min`, `max` (when set) |
| `word`, `string`, `greedy` | `greedy`, `quotable` |
| `enum`, `choice` | `options` |
| `player`, `players` | `many`, `selectors` (`@s @p @r @a`; `@e[type=player]` too) |
| `entity`, `entities` | `many`, `selectors` (`@s @p @r @a @e`) |
| `position` | `relative` (`~` offsets) |
| `duration` | `unit: "s"`, `min`, `max` (seconds) |
| `time` | `unit: "h"`, `min: 0`, `max: 24` |

`command_run`, `command_complete` and `commands_describe` take an optional named source and a level cap:
- `source`: who runs it, e.g. `web:<user>` (one line, at most 80 characters). It is the command source's name in
  kiln.log (`command /kick by remote web:alice (up to admin): ok`) and in `CommandExecutingEvent` /
  `CommandExecutedEvent`. It needs `level`.
- `level`: `everyone`, `admin` or `console`, the highest command level the line may use. Commands, subcommands and
  permission nodes above it are refused (also a node granted to a player by name, and a handler's own `Has` check),
  and below `admin` the source is no admin. `commands_describe` with a level lists only what it may run.
- With `steamId` the line runs as that player, with the player's permissions, capped at `level` when given.
- Without both: as before, the remote "dashboard" with admin rights (the launcher's console).

## 12. KAP/1 over a WebSocket (web pages, 0.2.0-dev.30.9)
The admin pages also run in a browser, outside the launcher (the `@kiln/admin-ui` package, [admin-pages.md](admin-pages.md)).
There the page talks to its host over a WebSocket, and the host (Kilnwarden, inside the server) speaks TCP KAP/1 to
Kiln.Admin with the admin token. The browser never sees the token and never authenticates in KAP: signing in is the host
page's job (a session cookie, or a one-time ticket in the socket's URL), and the socket it hands to the page is already
signed in. The page side is `launcher/src/api/adminWs.ts` (`createWebSocketTransport({url})`); the host decides which
ops and topics a user may have.

- **Messages:** one frame per WebSocket message: the frame's JSON object (section 2's payload) as a text message, with no
  length prefix. A binary message holds the same UTF-8 JSON. Section 2's limits apply: the page never sends a request
  larger than `ready.limits.maxClientFrame` (it refuses it itself, `invalid_argument`). A limit that is missing or not a
  number of at least 0 keeps section 2's default.
- **Opening:** after the upgrade the host sends `hello` (optional; `proto`, `kiln`, `admin`, `server {id, name}`; no
  `nonce` or `auth` needed) and then `ready` (`ops`, `topics`, `limits`, `restart`; `session` is the host's id, `mac`
  is left out). Nothing comes from the page before `ready`, and the page never sends `auth`. A `hello` with another
  `proto` than `kap/1` is refused. An `error` frame before `ready` refuses the connection (`auth_failed`/`auth_required`
  as `auth_failed`, other codes as in section 6); the page waits at most 10 s for `ready`.
- **Then:** sections 4 to 6 unchanged: `req`/`res` by id, `sub`/`unsub`/`push`, `ping`/`pong` (the page pings after
  `limits.pingS` without sending, answers the host's pings, and drops a host silent for `limits.idleS`), `error`,
  `bye`. Ids are per socket. Unknown frame types are ignored.
- **Closing:** close codes 4401 (not signed in, or the sign-in expired) and 4403 (not allowed) are final: the page shows
  "not connected" and does not retry. Any other close, a `bye` or a silent host is retried with backoff (1, 2, 4 ... 30
  s); after a reconnect the page subscribes its topics again. A browser cannot read an HTTP status from a refused
  upgrade, so a host that refuses one should accept it and close with 4401 or 4403 instead.
- **Sessions:** as the launcher's hub: one per server, its id stable across reconnects; events `closed {retry}` and
  `ready` as in the launcher's `kiln://admin`.
- **Restart:** the op `server_restart` (section 11, Kiln.Admin stops the server it runs in) is a `req` like any other.
  The launcher's own restart of a server it manages is a launcher command, never a KAP op, and a web page cannot reach
  it.
