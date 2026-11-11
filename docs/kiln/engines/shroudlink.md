# shroudlink mods on Kiln (kiln-shroudlink)

Kiln runs unmodified mods for **shroudlink** 0.8.0 (MIT, [bjck/shroudlink](https://github.com/bjck/shroudlink),
Nexus 278) on a dedicated server: chestsort, assistants, storage, meter, gemforge (Nexus 279-283), and any mod built
on its Rust SDK (`shroudlink-sdk`, mod ABI v1-v8). You do not need shroudlink's `steam_api64.dll` proxy, so a Steam
file check or game update no longer removes the framework.

## How it works

`kiln/plugins/kiln-shroudlink.dll` **is shroudlink's own server core**, built from its source (vendored in
`native/plugins/shroudlink/upstream/`, changes marked `kiln:` and listed in `upstream/KILN-CHANGES.md`) with a small
Kiln entry point (`upstream/server/src/kiln.rs`). Everything a mod calls (the 23 host functions, inventory events,
game events, the addon channel and its UI messages, hot reload) is shroudlink's code, so mods behave exactly as under
shroudlink. Kiln adds:

- **where mods come from**: the profile's `mods.shroudlink` list, then `<server>/shroudlink_mods/` (the rule every
  engine plugin uses, [README.md](README.md#where-mods-are-found)). A profile entry's `path` is a mod DLL or a folder
  of mod DLLs (a release zip's `shroudlink_mods` folder, for example). In `shroudlink_mods/` Kiln reads the DLLs
  there and in its direct subfolders (`shroudlink_mods/<id>/`, where the launcher's server sync puts each mod).
- **EML data mods** (shroudlink's "bigger stacks x10" / "chests x2"): any folder among those roots, or its direct
  subfolders, with `mod.json` + `src/mod.lua` is handed to kiln-eml, Kiln's single writer of game data, in the early
  phase (`kiln_eml.h`, `add_mod(path, "shroudlink")`). shroudlink's own `UPDATE-SERVER.bat` repatch step is not
  needed: kiln-eml re-patches after a game update before the world loads.
- **logs**: every shroudlink line also goes to `kiln/logs/kiln.log` as `[shroudlink] ...`; `shroudlink.log` and
  `shroudlink_hooks.txt` in the server folder are kept as shroudlink's bug reports expect.
- **status**: `kiln/state/shroudlink.json` (each mod DLL with `loaded`/`skipped` and the reason, the features that are
  on, and why any is off, the EML hand-over).
- **chaining onto other hooks**: shroudlink finds its 14 game functions by byte pattern. When Kiln (or another mod)
  already hooked one, the pattern is matched against the code in the exe file and shroudlink's detour chains onto the
  existing one. On build 26430253 this is the game-event dispatcher, which Kiln.Gameplay hooks
  (`kiln: found in the exe file (already hooked in memory): +0x6c2300`).
- **no mods, no hooks**: without any shroudlink mod DLL the core does not start.

The core starts from `on_ready`, on its own thread, as its `DllMain` does when loaded as `steam_api64.dll`.

## When shroudlink's own core is installed too

The plugin **steps aside**. If shroudlink's core is loaded in the process as `steam_api64.dll`, `crashhandler64.dll`,
`dinput8.dll` (also when Kiln's installer chained it from `kiln/chain/`) or `shroudlink_core.dll`, it is recognised
by its export pair `CrashHandlerFactory` + `DirectInput8Create` or its file name, together with shroudlink's own core
strings in the file. Unrelated proxies exporting the same names do not make Kiln step aside. kiln.log then says
`kiln-shroudlink steps aside: shroudlink's own core runs in this process (...)`, and that core loads
`shroudlink_mods/` as usual. Profile entries are not passed to the real core: `shroudlink.json` lists them as
`not-loaded` (a warning in kiln.log), except an entry that is a DLL directly in `shroudlink_mods/` (`own-loader`).
As a backstop, both copies use shroudlink's per-process claim (`chestsort_server_<pid>`): whichever claims first runs
and the other stays idle.

To move a server from shroudlink's proxy to Kiln: stop it, delete `steam_api64.dll` and rename `steam_api64_o.dll`
back to `steam_api64.dll` (or verify the files in Steam), keep `shroudlink_mods/`, install Kiln.

The **client addon** (`dinput8.dll`, MODS tab, chest search, slot locks) is a client mod. Kiln's installer chains it
from `kiln/chain/` like any other `dinput8.dll` mod, and it talks to the server over the game's own connection, so it
works the same with a Kiln-hosted core. Dropped on the launcher (alone, or in shroudlink's singleplayer package) it is
imported as a native mod and chained from the profile (dev.22; before, the Library took it for shroudlink's loader and
left it out, so the MODS tab never appeared). The Library tells it from the core by the core's `CrashHandlerFactory`
export and its Steam proxy text (`engines::is_shroudlink_addon`). The addon alone is not counted as shroudlink's own
loader; with `shroudlink_core.dll` in the game folder it is. In singleplayer the addon loads `shroudlink_core.dll` from the game folder.
With kiln-shroudlink in the client and shroudlink mods present, Kiln's copy usually claims first and the addon's copy
stays idle. That case is on the client checklist.

### Client startup and input

The addon scans the **live** executable on its own initialization thread. If Kiln hooks the item-registry lookup
before that scan, shroudlink 0.8.0 reports `native UI disabled: pattern not found: 89 4C 24 08 ...` even though its
host-link hooks initialized. Kiln now waits at the game entry point, outside the loader lock, after early native
mods load and before serving its game hooks. This covers the normally imported `dllmain` addon and early native
mods, not an addon explicitly configured to load after Kiln's hooks. For the same reason the profile page never
offers its one-click "load late" for a hook overlap of this addon: it hooks before Kiln on purpose.

The gate recognizes the addon's module-owned `shroudlink_shutdown` and `DirectInput8Create` exports. It proceeds
when that copy logs a complete startup result of this start (only what the addon appended to `shroudlink_client.log`
after the proxy measured it, before the chain loaded, counts) or when its first UI hook demonstrably jumps into that
copy. Stale/partial logs, unrelated hooks and forwarded exports are not readiness. The whole boot has one 10-second
wait budget; an unrecognized or failed addon cannot block Kiln forever. No addon code or signature semantics are
changed. `kiln/logs/kiln.log` records the outcome and `kiln/state/boot.json` includes `waitedFor` entries (`ready`,
`hooked`, `switched-off` or `timeout`, elapsed milliseconds and an available diagnostic line).

Kiln also leaves an addon's later window subclass above its own instead of reinstalling itself and forming a
procedure loop. The headless real-DLL test exercises both subclass orders, keys, shutdown and hot reload. These
checks do **not** establish that every missing MODS tab has the scan-order cause, nor verify rendered game menus.

`native UI ready` means the addon installed its hooks, not that a later player-menu frame emitted the tab. The
0.8.0 addon recognizes a row of exactly seven game tabs, remembers its last key/template, then appends Mods when
that key is drawn. Its native-tab path returns without appending if the template's input action is `0x8b` (none).
The actual-DLL harness reproduces both conditional suppression paths after successful initialization; it does not
establish that any particular controller mode, game build or player report supplies those inputs. Server handshake
and received menu entries do not gate the tab itself. Missing tab, empty shroudlink page, and Kiln's separate Mods
page are different symptoms.

Support reports include the bounded, redacted `shroudlink_client.log` when present. For a missing tab, reproduce
once after closing Kiln's own menu, try the addon's configured MODS key (default `K`) in the player menu, then save
a fresh report. A screenshot of the whole player tab row and whether keyboard/mouse differs from controller input
help distinguish a tab-recognition issue from startup or input delivery; do not infer the cause from slot locking alone.

### Optional cooperative client-menu bridge (source-only)

Kiln can also show a cooperating addon's **actual received menu entries and windows** inside its own Mods page:
choose **shroudlink menus**, then Storage, Assistants, or another entry the host actually sent. This access does not
require a local catalog row for the server's mod. It is **not available with the unchanged stock addon**, which has
no production snapshot/event export. Kiln does not load, install or replace an addon to enable it. Without the
versioned export the button is absent and the stock addon path is unchanged.

`native/plugins/shroudlink/client-bridge/prepare.py` is an offline, opt-in compatibility recipe for the MIT 0.8.0
source (upstream revision `f2fc8ddc741db517fcaec8d44bd756d72356c8d0`); it validates the patched inputs and unchanged
native-tab source, preserves upstream's MIT notice, and makes a **new private source copy**. No third-party client
source or DLL is included in this recipe. The separate count-independent native-tab experiment is not applied.
Run from outside the source, with build outputs in private scratch directories:

```sh
python -I native/plugins/shroudlink/client-bridge/prepare.py <pristine-source> <new-private-source>
CL=/FIstring.h CARGO_BUILD_JOBS=2 CMAKE_BUILD_PARALLEL_LEVEL=2 cargo xwin build --offline --locked --release \
  --manifest-path <new-private-source>/mod/Cargo.toml -p shroudlink-client --target x86_64-pc-windows-msvc
```

Set `CARGO_TARGET_DIR` and `CARGO_BUILD_BUILD_DIR` first. The forced string header works around upstream's
`libudis86-sys` clang-cl include flag; it does not alter addon behavior. The output is `shroudlink_client.dll` for
private harness testing, **not an installer or a redistributed client addon**.

The cooperating copy exports C ABI `shroudlink_client_bridge_v1(request, len, out, capacity, written) -> u32`.
Pointers are caller-owned valid buffers; lengths are bytes excluding NUL, `written` is a `size_t*`. Requests and
replies are UTF-8 JSON. Maximum request/reply sizes are 8192 / 262144 bytes. Status 0 succeeds; 1 means short buffer
(`written` gives the required size), 2 invalid input, 3 inactive copy, 4 stale ownership/state, 5 busy and 6 unavailable.
Short-buffer probes never acquire/renew/release ownership or enqueue events. Successful replies carry `"abi":1`.
The operations are:

- `{"op":"read"}`: opaque module/session epochs, revision, readiness, actual owner-addressed Entries/windows;
  read does not disclose another presentation's lease token.
- `{"op":"acquire","module":"...","session":"...","client":"..."}`: obtain/renew a two-second exclusive
  presentation lease. The reply includes its token and current state. The token is a fresh 128-bit random value that
  no other reply contains, so it cannot be derived from module, session or a counter. The lease coordinates
  cooperating presenters; it is not a guard against other code in the game process, which can call the addon or
  change its memory however it likes. Native menu-window presentation is suppressed
  only while a live lease is held. The addon's window procedure then forwards input unchanged down its saved chain
  instead of recording native text, hotkeys or wheel actions as well. Outside the lease, inventory/input features
  and the stock tab-recognition code remain unchanged.
- `{"op":"release","module":"...","session":"...","lease":"..."}`: release and discard queued events.
- `{"op":"event","module":"...","session":"...","lease":"...","revision":3,"owner":"storage",
  "event":{"window":"...","node":"...","event":{"kind":"click"}}}`: queue a validated real UI event.
  Menu uses an advertised entry ID and empty window; Closed uses an existing window and empty node. Other event
  types, values and indices must match the current owner/window/node. The queue holds at most 32 events, each at
  most 2048 serialized bytes. They are revalidated on the real update thread and under the actual link session lock,
  encoded with the addon's real nonce/message counter, and sent only as a whole message within that tick's budget.
  They never enter a persistent bridge packet outbox.

Readiness requires an accepted real beacon, UI-protocol-2 welcome, recognized world through the existing native UI
hook, and a recent actual client update. A wholly failed native UI installation therefore cannot use this bridge;
a replacement copy also needs a fresh real beacon/rejoin, not restored environment state. A UI frame outside the
world (screen 0) or more than two seconds without a client update (a loading stall, a title-bar drag) means not
ready while it lasts and ends the lease and its queued events; what the session received stays and shows again
once the world and the updates are back, as upstream treats the same two-second threshold as a live value. A new
beacon, a client-pointer change, module replacement and shutdown start a new session and drop everything received.
Like the addon's own menu, the bridge keeps the last host's entries if a later world has no shroudlink host and the
game reuses the same client object; events then go out as the native menu's would. Kiln releases its
lease when the menu closes, another section is selected, or its menu frame fails; expiry handles stopped rendering.
Discovery only examines already-loaded modules and never loads a DLL or resolves a forwarder. It runs on a worker
thread, never on the render thread, at most once a second while Kiln's menu is drawn. Export tables are read through
kernel-checked copies, bounded by the image size in the PE header, without taking a module reference; a reference
is taken only for a module whose table names the bridge export, which is then checked again under it, and
non-owned/non-executable exports are rejected. Each scan compares the loaded-module list with the last one; export
tables are read again only after a module loaded or unloaded, and until then only a matching copy that declined
is asked again. There are no
transport taps, unknown game layouts, receive-injection API or test TCP listener.

All ten real window node kinds are rendered. Text inputs send Change and have an explicit Apply/Submit button;
item grids show the actual numeric item IDs/counts, not guessed names/icons. HUDs, slot marks and backpack sections
remain with the native addon. Bounded synthetic actual-DLL wire tests do not prove in-game layout, input focus,
controller operation, or the cause of an individual missing-tab report.

## Install a mod

Unpack its zip into the server folder (`shroudlink_mods/<name>.dll`, or `shroudlink_mods/<name>/`), import it in the
launcher's Library and sync it to the server, or add it to a Kiln profile:

```json
"mods": { "shroudlink": [
  { "id": "chestsort", "path": "Z:\\srv\\library\\chestsort\\shroudlink_mods" },
  { "id": "meter", "path": "Z:\\srv\\library\\meter\\meter.dll", "enabled": false } ] }
```

A profile that lists `shroudlink` stops reading `shroudlink_mods/` unless it has `"dropins": true`. Mods keep their
files where shroudlink keeps them (`chestsort_categories.json` and the mods' own data next to the server exe). Hot
reload works as upstream: overwrite a mod DLL and it reloads within 2 s (shadow copies in
`kiln/data/shroudlink/loaded/`).

Discovery and each hot reload validate paths from the authorized mod root down, keeping them open through copying;
ancestors above the root may be links. Traversal, aliases and reparse points within the root are refused, with final
handle-path containment checked too. Wine's invisible Unix file-symlink limitation is the same as in the shared
[path policy](README.md). Shadow files are created exclusively. Native faults during initialization are reported
as a skipped mod; modules are retained because initialization may already have started threads or hooks. Native
mods still run trusted code in the game process; this is not process isolation or recovery from `abort`.

## API coverage

shroudlink's mod API is the `shroudlink-sdk` crate: the `HostApi` table, the `ModDecl` a mod exports, the JSON UI
protocol to the addon (`shroudlink::ui`), and SDK helpers. Because the plugin *is* shroudlink's host, every item is
implemented by shroudlink's own code. **Coverage: 23/23 host functions, 10/10 mod callbacks, 2/2 entry exports, 9/9
server-to-addon messages, 10/10 UI node kinds: all implemented.** "Tested" names the evidence: `host` = host tests
(`native/plugins/shroudlink/build.sh --test`, tests under Wine, among them upstream's
`plugins::tests::example_plugin_round_trip` and `link_host::tests::full_session`); `lab` = shroudlink's own in-server
self-test (`shroudlink_selftest` marker) on a Kiln lab server, build 26430253, 2026-11-07, `RESULT ok`.

The "Kiln C# equivalent" column is what a Kiln C# mod uses for the same thing. **New** marks what this work added.

### Host functions (`HostApi`, ABI 8)

| shroudlink API | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `send(ctx, client, json)` | shroudlink's link host (`link_host.rs`) over the game's string channel | `Network.Channel(...).Send` (Kiln's own wire) | full; host `full_session`, lab "addon session: welcome lists [...6 mods]" |
| `log(text)` | shroudlink.log + kiln.log `[shroudlink]` | `ModContext.Log` | full; lab (mod lines in kiln.log, e.g. `assistants: 0 open order(s)`) |
| `slots(game, entity)` | upstream `game_api` | `Items.Read(entity)` | full; lab "L stores the lock", quick-stack tests |
| `is_player(game, entity)` | upstream | `Players.IsPlayer`, `EntityId.IsPlayer` | full; lab "give_items refuses players" |
| `chest_name(game, entity)` | upstream (custom string index) | `Items.ContainerName(entity)` (tick thread) | full; lab "quick stack sorts by category: ... chest named Ores" |
| `max_stack(game, item)` | upstream | `Items.Get(id).MaxStack` | full; lab "iron bar ... (max stack 250)" |
| `move_stack(game, slot, to)` | upstream | `Items.Move(from, slot, to)` (keeps the item entity) | full; lab quick-stack tests |
| `players()` | upstream (login events) | `Players.Connected` | full; host |
| `position(entity)` | upstream | `World.Position` | full; lab "entities_near" |
| `teleport(entity, x, y, z)` | upstream | `Teleport.To` (Kiln.World) | full (no player in lab: untested) |
| `player_of(client)` | upstream | `Network.PeerOf` / `Players.Find` | full; host `full_session` |
| `read_component(entity, hash)` | upstream | `World.ReadBytes(entity, name)` | full; lab "component read: CurrentTransform of the chest, 56 bytes" |
| `write_component(entity, hash, data)` | upstream | `Reflected.Component(entity, name)[path] = value` (checked field writes) | full; untested in lab |
| `inventories_near(entity, radius)` | upstream | **new** `Nearby.Within(Items.FindInventories(), entity, radius)` | full; lab "world inventories: 21" |
| `inventory(entity)` | upstream | `Items.Read(entity)` | full; lab "world inventory: 24 slot(s)" |
| `move_stack_between(from, slot, to)` | upstream | `Items.Move(from, slot, to)` | full; lab "world move_stack: 4 iron bars moved, chest 7 -> 11" |
| `apply_buff(entity, guid, seconds)` | upstream impact hooks | `Buffs.ApplyBuff(target, guid, duration)` (Kiln.Abilities) | full; lab "buffs: feature on" (no player to buff) |
| `player_name(entity)` | upstream | `Players.Find(entity)?.Name` | full; untested (no player) |
| `entities_near(entity, radius, type)` | upstream | **new** `Nearby.Entities(entity, radius, component)` | full; lab "world entities_near: 1 inventory entity(s) within 0.5 m" |
| `move_items(from, slot, to, count)` | upstream | `Items.Move(from, slot, to, count)` | full; lab "world move_items: 3 of 9 iron bars moved" |
| `give_items(entity, item, count)` | upstream | `Items.Give` | full; lab "give_items: 5 plant fibre created" |
| `remove_items(entity, slot, count)` | upstream | `Items.TakeSlot` / `Take` | full; lab "remove_items: 2 removed" |
| `world_chest_name(entity)` | upstream | `Items.ContainerName(entity)` | full; lab "world chest_name: the test chest is \"Ores\"" |

### What a mod exports (`ModDecl` v1-v8)

| shroudlink API | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `shroudlink_mod_v2` / `shroudlink_mod_v1` exports | upstream loader (v1, v2, v3+ layouts) | `Mod` subclass + `kiln.mod.json` | full; host `loads_a_real_mod_from_a_profile_root`, lab 6 mods loaded |
| `id` | upstream | `ModInfo.Id` | full; lab |
| `on_load` | upstream | `Mod.OnLoad` | full; lab |
| `on_join` / `on_leave` (addon session) | upstream link host | `PlayerJoinedEvent` / `PlayerLeftEvent` | full; host `full_session`, lab addon session |
| `on_message` | upstream | `Network` channel handlers | full; lab "assistants window opens", "item search" |
| `keep` (slots a transfer must not move) | upstream inventory hooks (quick stack, area deposit, move all) | `Items.Transfers.Keep/KeepItem/Deny` | full; lab "quick stack keeps locked slot", "move all keeps locked slot" |
| `on_transfer` (mod moves stacks first) | upstream | partial: `InventoryTransferEvent` reports after the transfer; rules or a held client-action cancel shape it; synchronous handlers use the native `transfer_listen` API | full; lab "quick stack sorts by category" |
| `game_events` / `on_game_event` | upstream dispatcher hook, chained onto Kiln.Gameplay's | `GameEvents.Each(type, handler)` (tick thread), or typed `[Subscribe]` events | full; lab "hook points: 14 found, features: ... game_events" (no events without players) |
| `on_tick` (about 1/s, v5+) | upstream | `Tick` / `[Subscribe] TickEvent` | full; lab "mod tick: 6 v5 mod(s) got on_tick" |
| hot reload (changed DLL) | upstream, over Kiln's roots | Kiln's mod reload | full; lab "hot reload: chestsort reloaded and greeted the session again" |

### Addon UI building blocks (`shroudlink::ui`)

Server-to-addon messages `Open`, `Close`, `Entries` (MODS menu), `SlotKeys`, `Marks` (rim/dim on slots), `Store`,
`Section` (backpack row), `Notify`, `Hud`, and node kinds `Text`, `Heading`, `Separator`, `Row`, `Button`, `TextInput`,
`Toggle`, `ItemGrid`, `List`, `Tabs`; addon-to-server `SlotKey`, `Stored`, `Layout`, `ui_event` (`Click`, `Change`,
`Submit`, `Menu`, `Closed`). All carried unchanged by shroudlink's link host; drawn by shroudlink's addon. Lab: "MODS
menu entry", "backpack section offered", "notification: Wave answers with a HUD message", "chest categories window".
Kiln C# equivalents: `Ui.AddWindow` (Kiln.Ui, needs Kiln in the client), `Menus.Add`, `Ui.AddHud`, `Ui.AddToast`;
slot marks and slot keys have no Kiln equivalent (gap).

### SDK helpers (compiled into each mod)

`components::hash_of`, `world_position`, `events::hash_of`/`name_of`/`Known`, `guid_bytes`, `guid_hash`,
`server_dir`, `testing::MockHost`: mod-side code, nothing for a host to provide. Kiln equivalents: `World.ComponentHash`,
`ItemId.FromGuidBytes`, `ModInfo.Directory`, `Kiln.Testing`.

## Kiln C# additions

`managed/services/Kiln.World/Nearby.cs` (ported from shroudlink's `entities_near`/`inventories_near`):
- `Nearby.Entities(EntityId center, double radius, string component)` / `(WorldPosition at, ...)`: entities with a
  component within a radius, nearest first.
- `Nearby.Within(IEnumerable<EntityId> candidates, EntityId center | WorldPosition at, double radius)`: filter any
  list, e.g. `Nearby.Within(Items.FindInventories(), player, 40)` for chests near a player. Tick thread.

Kiln C# also exposes `Items.ContainerName`, `Items.Move/QuickStack/MoveAll`, transfer rules and
`InventoryTransferEvent`, checked component writes through `Reflected.Component`, and event reads by type name
through `GameEvents.Each`. See [inventory.md](../modding/inventory.md). Moves keep the item entity rather than
replacing gear with a take/give pair. Player-initiated quick stack, addon drawing and slot input still need client
checks. Kiln's inventory lab reported 95 PASS and 15 PASS after restart, with 12 PASS in each shroudlink hook
order. These server results do not prove addon input or drawing. Slot marks and slot keys remain
without a Kiln C# equivalent.

## Tests

- `native/plugins/shroudlink/build.sh --test` (with `SHROUDLINK_SRC=<clone of v0.8.0>` to build the example mod):
  upstream's host tests plus Kiln's (profile rules, roots, EML detection, loading a real mod from a profile root).
  The example-DLL tests can silently skip if `hello_mod.dll` is absent beside the test executable's `deps` folder;
  check `--nocapture` output for `skipped: build hello_mod first`, especially with a separate Cargo build directory.
- `KILN_OUT=<build-output> native/core/tests/test-foreign-scan.sh`: ASan/UBSan host tests for readiness parsing,
  bounded hook attribution, success, failure, stale/partial logs and timeout behavior.
- `KILN_OUT=<build-output> SHROUDLINK_ADDON=<existing-client-dinput8.dll> native/services/ui/tests/test-addon-wine.sh`:
  optional Wine integration using the unmodified operator-supplied addon and production Kiln input/startup code.
  Eight compatibility scenarios plus a stock no-export negative check cover the scan-order failure and fix,
  both subclass orders, shutdown/reload, shared-budget timeout,
  forwarded-export rejection, and actual addon tab-list/widget calls (six/seven/eight tabs, action-none suppression,
  restored action, native K-label override and Kiln capture flags). The tab checks run without server/menu entries.
  Synthetic source-backed game functions and message-only window discovery adapters need no game or display.
  This is not a real-game render, layout/controller test, server connection or Storage/Assistants feature test.
- For a privately prepared cooperating addon, add `SHROUDLINK_BRIDGE_TEST=1` to that command. This replaces the
  no-export negative check with both subclass orders exercising real store/update-hook beacon/welcome,
  Entries/Open/all-node snapshots, validated game-thread event wire roundtrips, ABI probes,
  lease/rejoin/replacement invalidation and leased-hotkey checks.
  It writes the synthetic actual-addon reply to `<evidence>/bridge/bridge-snapshot.json`.
- `cargo test --offline --manifest-path native/plugins/shroudlink/Cargo.toml -p kiln-shroudlink-client-bridge`:
  bounded state, ownership, lifecycle, event validation and real frame codec tests.
- `python -I native/plugins/shroudlink/client-bridge/test_prepare.py <pristine-source> <scratch>`: source recipe guards.
- `dotnet test managed/tests/Kiln.Ui.Tests --artifacts-path <scratch>`: renderer, client and PE export-table tests.
  Set `KILN_SHROUDLINK_BRIDGE_SNAPSHOT=<captured-snapshot>` and `KILN_SHROUDLINK_BRIDGE_ADDON=<private-dll>` to also
  exercise the actual private addon's snapshot and PE export; these two checks are explicitly skipped otherwise.
- `KILN_OUT=<build-output> native/services/ui/tests/run.sh`: Kiln's regular UI/input host regressions.
- Lab: deploy Kiln (`tools/deploy-server.sh` builds `kiln-shroudlink` like every plugin), put mod DLLs built from
  shroudlink's repo (`cargo build --release` in its `mod/`) into `shroudlink_mods/`, create the file
  `shroudlink_selftest` next to the server exe and start it. `shroudlink_selftest.txt` ends with `RESULT ok`. The test
  moves items in the copied world.

Lab results (2026-11-07, `wv-rustsdk-sl`, build 26430253):
- legacy layout, 6 mods: `shroudlink core running: features [channel, inventory, move_all, game_events,
  world_context, buffs, items]`; self-test `RESULT ok` (all PASS lines above);
- profile with 4 entries (folder, single DLL, disabled, EML folder): `profile 'sl-test': 4 listed`, `storage (...):
  switched off in the profile`, 3 mods loaded from the profile paths, the 6 in `shroudlink_mods/` not read;
- upstream core installed as `steam_api64.dll`: `kiln-shroudlink steps aside: shroudlink's own core runs in this
  process (...\steam_api64.dll)`, and the real core registered its 6 mods.

The EML hand-over requests the v1 prefix of `kiln_eml.h`; compatible larger tables are accepted, and missing,
short, version-zero or null-slot tables are unavailable. kiln-eml independently authorizes paths: its current
default roots do not include `shroudlink_mods/`. Put an EML folder in an authorized root or explicitly list its
path in the active profile. A refusal is logged and recorded in state; it is never reported as a successful hand-over.

Review regressions: `build.sh --test` includes API/info/service size and version checks, proxy identity, checked
paths, exclusive shadow copies, native initialization faults and detour chaining over an existing `E9` jump.
The bounded exe-file parser also runs without Wine:
`rustc --edition 2021 --test native/plugins/shroudlink/upstream/common/src/file_scan.rs -o /tmp/sl-scan-test`.
Nearby's thread/bounds harness is `native/plugins/shroudlink/tests/nearby/Nearby.Tests.csproj`; use
`dotnet run --project <that path> --artifacts-path /tmp/kiln-nearby`.

## Client checklist (user)

1. Server: lab or your server with Kiln + the shroudlink mods (no `steam_api64.dll` proxy). Client: shroudlink's
   addon `dinput8.dll` installed through Kiln (it lands in `kiln/chain/`). Join.
2. The player menu shows the **Mods** tab with Chest categories, Assistants, Storage and Damage meter for the
   bundled mods (gemforge has no menu entry). kiln.log on the server lists each of the five mod IDs as registered.
3. Rename a chest "Ores", press quick stack: iron bars go in. Press `L` on a backpack slot: gold rim, and quick stack
   leaves it.
4. MODS -> Storage lists the chests within 40 m; click an item: it comes from every chest.
5. Hit a creature: MODS -> meter shows the fight.
6. Singleplayer with Kiln in the client and the mods in `<game>/shroudlink_mods/` (+ `shroudlink_core.dll`): the
   client kiln.log says either `shroudlink core running` or `steps aside`; exactly one copy runs; the MODS tab works.
