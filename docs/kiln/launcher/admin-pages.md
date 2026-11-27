# Admin pages

The Kiln Launcher's server administration: live tools as launcher pages, on Kiln.Admin's op table over
KAP/1 ([admin-protocol.md](admin-protocol.md)). Code: `launcher/src/admin/**`,
`launcher/src/api/{admin,adminTauri,adminWs,shared}.ts`, `launcher/vite.admin.config.ts`,
`tools/launcher/{mock-admin.py,admin-shots.mjs}`. The same pages are the package `@kiln/admin-ui` for web pages (Kiln
Web Admin): see "The @kiln/admin-ui package" below.

## Mounting
`<AdminRoutes/>` (`src/admin/index.tsx`) reads `:id` from the route (or takes `serverId`), opens the server's shared
admin session (`<AdminProvider>`) and renders the pages. App.tsx mounts it at `servers/:id/admin/*` inside the server
layout, whose grouped list links the pages and whose live sentence shows the connection (reconnect and the transport
are in its `···` menu, or in the banner pages show while disconnected). Configs, Mods and the console are merged into
the server's own pages (`/servers/:id/configs`, `mods`, `overview`; the old admin routes redirect there, in the router
itself: `ADMIN_MERGED` in `shell/serverNav.ts`, so an old link does not wait for the admin pages' bundle). Each page's
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
Both forms work: without a server id the hooks use the nearest `<AdminProvider>`. The hub reaches Kiln.Admin through
an `AdminTransport` (`configureAdmin({transport, host})`): in the launcher `src/api/adminTauri.ts` (the Tauri commands
`admin_*` and the `kiln://admin` event, installed by `src/main.tsx`), in a web page `src/api/adminWs.ts`.

| Hook | Returns |
|---|---|
| `<AdminProvider serverId>` | shares the server's session with everything below; the last user closes it 30 s later |
| `useAdminState([serverId])` | `AdminHubState`: `status` (idle, connecting, ready, reconnecting, error), `session` (AdminSession: transport, ops, topics, kiln, server), `state` (the latest `state` push), `stateSeq`, `stateAt`, `error`, `retryAt`, `gaps`, `ops` (Set), plus the contract's `transport`, `connecting` |
| `useHasOp(op)` / `useHasOp(serverId, op)` | true when `ready.ops` has the op; pages gate with `<NeedsOp ops>` ("needs a newer Kiln.Admin") |
| `useAdminCaller([serverId])` | `(op, args) => Promise<data>`; rejects with KilnError (`op_failed` with the op's message; `unsupported` for an op the server lacks) |
| `useAdminCall(op, {invalidates?, ...mutation options})` | a TanStack mutation: `mutate(args)` |
| `useAdminQuery(op, args?, options?)` | a TanStack query keyed by server, op, args and session (a reconnect refetches); disabled until the op is available |
| `useKap([serverId])`, `kapOps(call)` | typed calls of the newer ops: `adminInfo`, `serverRestart`, `serverRestartCancel`, `loginMint`, `loginRedeem`, `updateList`, `updateStage`, `updateCancel`, `updateRollback` (each a KAP request; check with `useHasOp`) |
| `callAdmin`, `invalidateAdmin`, `reconnectAdmin(serverId, kind?)`, `onAdminState`, `onAdminTopic` | the same outside React |
| `configureAdmin({transport, host})`, `adminTransport()`, `adminHost()` | the transport seam and the app around the pages (below) |

The hub follows the Rust AdminHub, and every transport behaves like it: `connect` (`admin_connect`) returns the
server's one session (stable across its internal reconnects); the event `{kind:'closed', retry:true}` shows
"Reconnecting…" until `{kind:'ready', transport, ops, topics}`; `retry:false` is final (the Reconnect button); without
`retry` (an older backend) the hub reconnects itself after 10 s. A failed connect (server stopped, no endpoint) is
retried with backoff 1..30 s while a page is open. A call's `not_found` (session forgotten) reconnects;
`admin_unavailable` shows reconnecting. Switching the transport kind (auto, tcp, file, ssh: the kinds the transport
offers; a web page has none to pick) closes the session first. The kind is kept per server in localStorage.

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
live overlays on the plain world plane; Spawn falls back to the enemy types the server has seen. A host words that banner
and the Markers tab's line itself through `useMapNoArt` (the host seam, below).

The Map page fills the height its parent gives it when that parent is a flex column (the launcher's server page,
Kilnwarden's frame), and the map fills the page whatever the side panel holds: Markers, Find & filter and Live scroll
inside the map's height, and the search results end 12 px above the map's bottom edge and scroll there. In a parent of
no set height the map is the window's height under the page, less 18 px. `tests/admin-map-layout.spec.ts` checks the
launcher's flex column at 1920×1080, 1360×860, 1280×800 (the Deck) and 1024×640, and the parent of no set height (the
web harness) at 1280×800.

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

## The @kiln/admin-ui package
The admin pages are also a workspace package for web pages: Kilnwarden (the web panel mod, mod id `kilnwarden`,
formerly Kiln Web Admin; its DESIGN.md section 4) serves them from inside the server, so both panels get every page.
Nothing is published.

| | |
|---|---|
| Sources | `launcher/src/admin/**` and what its entry reaches: `src/api/{admin,adminWs,shared}.ts`, the design system's Tauri-free modules (`src/ui/*` without TaskProgress, DropZone, dialogFolder), `src/i18n`, `src/theme` |
| Entry | `src/admin/web.ts`: the pages (`AdminRoutes`, `ADMIN_PAGES`, each page), the hub and hooks, `configureAdmin`, `createWebSocketTransport`, `kapOps`/`useKap`, the KAP and op types, `ConfirmHost`, `Toaster`, the languages |
| Package | `launcher/packages/admin-ui/package.json` (`@kiln/admin-ui`, a Bun workspace of `launcher/`; `launcher/bunfig.toml` keeps the install hoisted): `exports` `.` = `dist/admin-ui.js` + `dist/types/admin/web.d.ts`, `./style.css` = `dist/admin-ui.css` |
| Build | `bun run build:admin-ui` in `launcher/` (`vite.admin-ui.config.ts`, `tsconfig.admin-ui.json`, `scripts/admin-ui-dist.ts`): an ES module with React, React Router, TanStack Query (peer dependencies), zustand, leaflet, lucide-react and react-virtual as imports; the stylesheet with the theme, fonts and leaflet's CSS. The last step fails when the bundle (every chunk, and every module its source maps name) or its declarations reach Tauri, the command layer, the launcher's own pages, components or shell, the mock, or a package the package.json does not declare |
| Not in it | the launcher's own server pages that use the admin hooks (Overview, the console `pages/servers/ServerConsole.tsx`, Mods `pages/servers/ServerModsPage.tsx`; Configs is in it as `AdminConfigsPage`), the browser mock (`src/admin/mock`, launcher dev only) |

A web page sets the transport and renders the pages in its own providers:
```tsx
import { AdminRoutes, ConfirmHost, Toaster, configureAdmin, createWebSocketTransport } from "@kiln/admin-ui";
import "@kiln/admin-ui/style.css";
configureAdmin({ transport: createWebSocketTransport({ url: (serverId) => `wss://${location.host}/kap/${serverId}` }) });
// <QueryClientProvider><HashRouter><Route path="servers/:id/admin/*" element={<AdminRoutes />} /> ... <ConfirmHost /><Toaster />
```
`createWebSocketTransport({url, WebSocket?, backoffMs?, handshakeMs?, callTimeoutMs?})`: `url` may be a function, called
at every (re)connect so the host can hand out a fresh ticket; one that throws `{code: "auth_failed"}` ends the session.
The protocol on the socket: [admin-protocol.md](admin-protocol.md) section 12.

The host seam (`configureAdmin`'s `host`, all optional): `assetsRoot()` (where the map art is: the launcher's
`kiln-asset` protocol in a release build; default `/assets-local/`), `useArtStatus()` (a hook: the launcher's Game
art state, so the map probes again), `useMapNoArt(art)` (a hook, called on every render of the map with
`useArtStatus()`'s state: what the Map page says while it has no map art, `{title, body?, action?: {label, onClick,
busy?}, markers?}` (`AdminMapNoArt`): `title` (null: no banner over the map, the host has its own notice), `body` (text
or the host's elements; taller than the map, the banner scrolls inside it) and `action` replace the banner's "Preparing
map art…" / "Map art needs the Enshrouded client" (the banner's box has the class `ka-map-noart`), `markers` (default:
the title; with neither, the launcher's line) the Markers tab's "No map data yet: the launcher prepares it…";
undefined: the launcher's words; `tests/admin-map-noart.spec.ts`), `crashReportsFromDisk(serverId)` (the launcher reads a stopped server's crash
reports), `restartManagedServer(serverId)` (the launcher restarts a server it manages: its Tauri command
`server_restart`). Without them the pages leave those parts out: no folder crash list, no "Restart now" for a staged
restore. Kiln.Admin's op `server_restart` is something else, a KAP request (`kapOps(call).serverRestart`), and the
names never meet: the WebSocket transport has no way to reach a launcher command
(`unit-tests/admin-{tauri,ws}-transport.test.ts`).

Checks: `unit-tests/admin-ui-package.test.ts` (what the entry reaches, by value and by type),
`scripts/admin-ui-dist.test.ts` (the build's last step on fake builds), `admin-hub.test.ts` (the
hub over a fake transport), `admin-tauri-transport.test.ts` (the launcher's transport at Tauri's invoke),
`admin-ws-transport.test.ts` (a fake KAP-over-WebSocket server); `tests/admin-ui-web.spec.ts` runs the pages in a plain
page (`src/admin/harness/index.html?kap=<ws url>`, served by the launcher's dev server, not part of a build) against
the browser mock's world behind a fake socket, with a Tauri stand-in that must never be called.

Dialogs over the map: Leaflet's panes, its canvas and controls and the map's own overlays use z-indexes of 400 to 1200.
`.ka-map-root` is a stacking context of its own (`isolation: isolate` in `map/map.css`), so they stay below what a host
puts on the body: the design system's dialogs (`.k-modal-backdrop`, z-index 50: `ConfirmHost`'s confirmations, a host's
own dialogs) and toasts (60). In the launcher the sheet (`.k-sheet`, z-index 3) already held them; a host with nothing
around the pages had dialogs over the map that took no clicks (Kilnwarden 0.1.3, where Leaflet's canvas took them).
Check: `tests/admin-map-dialogs.spec.ts` opens the map's Delete waypoint confirmation over the map, in the launcher and
in the harness, and clicks across it.

### A strict Content-Security-Policy (0.2.0-dev.31.13)
The pages run under Kilnwarden's header (`default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self';
connect-src 'self'; img-src 'self' data: blob:; ...`, no `'unsafe-inline'`, no `'unsafe-eval'`). What that refuses, and
so what the package never does: a `style="..."` in HTML a module writes (an `innerHTML`, a Leaflet `divIcon`'s or
tooltip's `html`) or sets with `setAttribute("style")`, a `<style>` element, inline script, `eval`. What it allows and the
pages use: React's `style={{...}}` and `element.style` / `style.setProperty` (React writes through the CSSOM, which CSP
does not govern), classes, SVG presentation attributes. The map's player labels set their health bar's `--hp` through
the CSSOM once Leaflet has built the icon (`PlayerIcon` in `map/engine.ts`), as the label rows set `--row`.
Every page has one `h1`: Items and Map, which show no header, have a visually hidden one (`k-sr-only`) with the page's
name, so the launcher looks the same.

The package's CSS carries its fonts as `data:` URLs (a Vite library build inlines assets): a host under `font-src 'self'`
turns them into files when it builds (Kilnwarden's `inlineFonts` plugin does).

Checks: `tests/admin-ui-csp.spec.ts` builds the harness for production (the dev server injects inline scripts and
styles itself), serves it from a fake origin with that exact header, and opens every page the mod vendors (the 13 admin
pages and Configs) over the fake socket: it fails on any CSP violation (the `securitypolicyviolation` events, the
console's CSP messages, page errors) and on a page without exactly one `h1`, and checks the map's health bars have their
width; a second test proves the probe sees an inline style attribute. `unit-tests/admin-ui-package.test.ts` also scans
every module the entry reaches for a style attribute in an HTML string, `setAttribute("style")`, `<style>`, raw HTML
and `eval`.
