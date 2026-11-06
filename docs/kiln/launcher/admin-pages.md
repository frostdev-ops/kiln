# Admin pages

The Kiln Launcher's server administration: live tools as launcher pages, on Kiln.Admin's op table over
KAP/1 ([admin-protocol.md](admin-protocol.md)). Code: `launcher/src/admin/**`,
`launcher/src/api/admin.ts`, `launcher/vite.admin.config.ts`, `tools/launcher/{mock-admin.py,admin-shots.mjs}`.

## Mounting
`<AdminRoutes/>` (`src/admin/index.tsx`) reads `:id` from the route (or takes `serverId`), opens the server's shared
admin session (`<AdminProvider>`) and renders the pages. App.tsx mounts it at `servers/:id/admin/*` inside the server
layout, whose grouped list links the pages and whose live sentence shows the connection (reconnect and the transport
are in its `···` menu, or in the banner pages show while disconnected). Configs, Mods and the console are merged into
the server's own pages (`/servers/:id/configs`, `mods`, `overview`; the old admin routes redirect there). Each page's
default export also works on its own route (`withAdmin` adds a provider from `useParams().id`).

| Route | Page | Ops (Kiln.Admin) |
|---|---|---|
| `players` | who is in the world (live) as a table and the selected player's form: SteamIDs, rights, health; teleport (player, waypoint, grave, coords), give item (ItemPicker on `item_search`; gear with level/rarity), heal, god, kill (typed confirmation), inventory, tell; everyone seen | players (state), teleport, teleport_to_player, give_item, item_search, heal, god, kill, inventory, tell, permissions_get |
| `map` | Leaflet map: tiles + WebGL relief, markers, place names, find & filter, search, players gliding with the pushes, follow, waypoints, graves, zones drawn and editable, enemies near players, routes, context menus (teleport, waypoint, protect an area) | zones_list, zones_add, zones_remove, teleport, teleport_to_player, enemies |
| `zones` | protection zones: list, add/edit (centre from a player, the map or numbers; refused actions; owners; admins), remove, the build's hooks | zones_list, zones_add, zones_remove, gameplay_status |
| `items` | the server's item list; the selected item's form: give to a player, drop into the world | item_search, give_item, spawn_item |
| `spawn` | creature picker (local catalog + types the server saw + GUID), 1..20 next to a player or at coords, spread, labels, remove spawned | spawn, enemies, despawn_spawned |
| `world` | time of day (presets, slider, HH:MM, game-clock reset), weather reset, broadcast, live world settings (the game's ranges), make permanent | time_get, time_set, weather_reset, broadcast, settings_get, settings_set, settings_save |
| `combat` | players' health, heal/god/kill; any entity by id (health, damage, kill); creatures near a player or coords; deaths with the killer | health, heal, god, kill, damage, enemies, state.combatDeaths |
| `creatures` | Kiln.Creatures: catalog search with spawn (at a player or coords, 1-50), creatures nearby, inspect (state, level, values, home, loot) with set/reset and commands (stay, heal, moveto, attack, guard, stopguard); changes are Kiln.Admin's layers  | creature_status, creature_catalog, creature_near, creature_inspect, creature_set, creature_reset, creature_spawn, creature_command |
| `gamedata` | kiln.gamedata: search a section (templates, arsenals, sequences, buffs, perks, skills, items), an entry's fields (templates) or JSON, set a path to a pack value, reset it; changes are Kiln.Admin's layers  | gamedata_status, gamedata_search, gamedata_get, gamedata_set, gamedata_reset, template_fields |
| `permissions` | groups (admin, inherits, rules), server user groups → Kiln groups, players' groups and rules by SteamID, check (explained), nodes | permissions_* |
| `content` | status, look up (search, entry, fields: enum/flags/bool/number/text), set/reset, single-field fallback, data packs + conflicts + re-apply, mods' new definitions, sync status | content_* , sync_status |
| `configs` (merged: `/servers/:id/configs`) | config list, SchemaForm on the live schema (Kiln validates every change), pending-restart values, re-read | config_list, config_get, config_set, config_reload |
| `mods` (merged: `/servers/:id/mods`) | one table with the files: state, tick cost, faults, errors, deps; run tests, reload, unload (confirm), load; on/off for the next start (`server_mods` / `server_mod_enable`) | mods, mod_test, mod_reload, mod_unload, mod_load |
| `console` (merged: the server's Overview) | command line with completion (Tab), history (Up, Down), run as a player, the command list with usage | command_run, command_complete, commands_describe |
| `saves` | save status, save now, Kiln backups (name rules), restore staged for the next start (typed `restore`), cancel, restart | save_status, save_now, backup_list, backup_create, backup_restore, backup_cancel_restore |
| `crashes` | Kiln's crash reports, newest first, the text with a filter; from the folder (`logs_crash_reports`) while the server is stopped | crash_reports |

Input is validated before anything reaches the game (SteamID64s, nodes, group names,
backup names, field paths, amounts 1..10000, spawn counts 1..20, radius 1..2000 m, coordinates ±1e6, waypoint names
1..40, config paths and list sizes); Kiln.Admin validates again and its message is shown as is.

## Hooks (`src/api/admin.ts`)
Both forms work: without a server id the hooks use the nearest `<AdminProvider>`.

| Hook | Returns |
|---|---|
| `<AdminProvider serverId>` | shares the server's session with everything below; the last user closes it 30 s later |
| `useAdminState([serverId])` | `AdminHubState`: `status` (idle, connecting, ready, reconnecting, error), `session` (AdminSession: transport, ops, topics, kiln, server), `state` (the latest `state` push), `stateSeq`, `stateAt`, `error`, `retryAt`, `gaps`, `ops` (Set), plus the contract's `transport`, `connecting` |
| `useHasOp(op)` / `useHasOp(serverId, op)` | true when `ready.ops` has the op; pages gate with `<NeedsOp ops>` ("needs a newer Kiln.Admin") |
| `useAdminCaller([serverId])` | `(op, args) => Promise<data>`; rejects with KilnError (`op_failed` with the op's message; `unsupported` for an op the server lacks) |
| `useAdminCall(op, {invalidates?, ...mutation options})` | a TanStack mutation: `mutate(args)` |
| `useAdminQuery(op, args?, options?)` | a TanStack query keyed by server, op, args and session (a reconnect refetches); disabled until the op is available |
| `callAdmin`, `invalidateAdmin`, `reconnectAdmin(serverId, transport?)`, `onAdminState`, `onAdminTopic` | the same outside React |

The hub follows the Rust AdminHub: `admin_connect` returns the server's one session (stable across its
internal reconnects); `kiln://admin` `{kind:'closed', retry:true}` shows "Reconnecting…" until `{kind:'ready',
transport, ops, topics}`; `retry:false` is final (the Reconnect button); without `retry` (an older backend) the hub
reconnects itself after 10 s. A failed `admin_connect` (server stopped, no endpoint) is retried with backoff 1..30 s
while a page is open. `admin_call` `not_found` (session forgotten) reconnects; `admin_unavailable` shows reconnecting.
Switching transport closes the session first (`admin_disconnect`, then `admin_connect`). The transport choice is kept
per server in localStorage.

## What the launcher remembers per server
`src/admin/history.ts` (localStorage `kiln.admin.history.v1.<serverId>`), fed by every state push of every connected
server: SteamIDs and names of players seen, deaths with killers from
`state.combatDeaths` (last 300), graves from `state.deaths` (older Kiln.Admin), waypoints (1..40
characters) and enemy labels. The map's look and filters:
`kiln.admin.map.v1`; the console's lines and history: `kiln.admin.console.v1.<serverId>`.

## Map assets (never committed, never bundled)
They are Keen Games' game data: the launcher makes most of them from the user's installed game
([gameart.md](gameart.md)). Layout: `map/{tiles,gpu,data,i,fonts}`, `data/{creature-templates,items}.json` (31 MB). Every page
reads them through one helper, `assetsRoot()` (`src/admin/catalog.ts`):

| Where | Base URL | Filled by |
|---|---|---|
| dev (vite, `tauri dev`, a browser) | `/assets-local/` (vite serves `launcher/assets-local/`, gitignored) | a link to `<data>/admin-assets` |
| release launcher | `kiln-asset://localhost/admin/` (Windows `http://kiln-asset.localhost/admin/`): `src-tauri/src/assets.rs` serves `$KILN_ADMIN_ASSETS`, then `<data>/admin-assets/` (debug builds also `launcher/assets-local/`) | in the app: Settings → Game art (`app_assets_import("admin", from)`, `app_assets_status`) |
| override | `VITE_KILN_ASSETS` | - |

Dev uses the vite path because WebKitGTK refuses custom-scheme images and CSS from the `http://localhost:1420` page
under `tauri dev`. Without the assets the map shows a banner (with the import button in the launcher) and draws the
live overlays on the plain world plane; Spawn falls back to the enemy types the server has seen.

## Development and testing
- Browser mock (no backend): `bun x vite` (1420) or `KILN_ADMIN_MOCK=1 bun x vite --config vite.admin.config.ts`
  (1421). `src/admin/mock/` answers the `admin_*` commands from `fixtures/synthetic.json` (the shapes of a real
  Kiln.Admin's results; Kiln's own results kept, game content invented: "Test Sword T2", "Creature A", synthetic
  content fields; made by `mock-admin.py synthesize`) plus a small simulated world (two players walking, time, zones,
  permissions, configs, content edits, backups); the server id `offline` refuses to connect. A real capture
  (`mock-admin.py capture --server <dir>`) holds game names and stays local in
  `launcher/assets-local/fixtures/kiln-admin.capture.json` (gitignored); both mocks prefer it when present.
- Dev KAP bridge (dev server only, not in builds): `KILN_ADMIN_SERVER=<server dir>` or
  `KILN_ADMIN_SERVERS="lab=<dir>,mock=<dir>|host:port@tokenfile" bun x vite --config vite.admin.config.ts`: the browser's
  `admin_*` commands go to `/__kap/*`, which speaks KAP/1 (HMAC handshake, `maxFrame` from `ready.limits`, pings,
  reconnect with `closed{retry}`/`ready` like the Rust hub) to a real Kiln.Admin or the mock server.
- `tools/launcher/mock-admin.py`: KAP/1 mock server (`serve [--port] [--dir DIR] [--fixture-hello] [--fixture F]`; DIR
  gets `kiln/data/kiln-admin/{endpoint.json,admin-token}`; the default token is the handshake test vector), a client
  (`call`, `watch`), `capture --server <dir>` (a local capture, paths made anonymous), `synthesize [--from capture]`
  (the committed synthetic fixture) and `selftest`.
- Screenshots: `node tools/launcher/admin-shots.mjs [--server lab] [--prefix mock-]` → `.out/launcher/admin-shots/`.
