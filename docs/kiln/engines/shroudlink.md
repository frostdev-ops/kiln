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
  of mod DLLs (a release zip's `shroudlink_mods` folder, for example).
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
`shroudlink_mods/` as usual. Profile entries are not passed to the real core.
As a backstop, both copies use shroudlink's per-process claim (`chestsort_server_<pid>`): whichever claims first runs
and the other stays idle.

To move a server from shroudlink's proxy to Kiln: stop it, delete `steam_api64.dll` and rename `steam_api64_o.dll`
back to `steam_api64.dll` (or verify the files in Steam), keep `shroudlink_mods/`, install Kiln.

The **client addon** (`dinput8.dll`, MODS tab, chest search, slot locks) is a client mod. Kiln's installer chains it
from `kiln/chain/` like any other `dinput8.dll` mod, and it talks to the server over the game's own connection, so it
works the same with a Kiln-hosted core. In singleplayer the addon loads `shroudlink_core.dll` from the game folder.
With kiln-shroudlink in the client and shroudlink mods present, Kiln's copy usually claims first and the addon's copy
stays idle. That case is on the client checklist.

## Install a mod

Unpack its zip into the server folder (`shroudlink_mods/<name>.dll`), or add it to a Kiln profile:

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
(`native/plugins/shroudlink/build.sh --test`, 10 tests under Wine, among them upstream's
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
| `chest_name(game, entity)` | upstream (custom string index) | **gap** (no container-name read in Kiln) | full; lab "quick stack sorts by category: ... chest named Ores" |
| `max_stack(game, item)` | upstream | `Items.Get(id).MaxStack` | full; lab "iron bar ... (max stack 250)" |
| `move_stack(game, slot, to)` | upstream | **gap** (`Items.TakeSlot` + `Give` loses item state) | full; lab quick-stack tests |
| `players()` | upstream (login events) | `Players.Connected` | full; host |
| `position(entity)` | upstream | `World.Position` | full; lab "entities_near" |
| `teleport(entity, x, y, z)` | upstream | `Teleport.To` (Kiln.World) | full (no player in lab: untested) |
| `player_of(client)` | upstream | `Network.PeerOf` / `Players.Find` | full; host `full_session` |
| `read_component(entity, hash)` | upstream | `World.ReadBytes(entity, name)` | full; lab "component read: CurrentTransform of the chest, 56 bytes" |
| `write_component(entity, hash, data)` | upstream | **gap** (typed writes only: `Combat.SetHealth`, `Transform`...) | full; untested in lab |
| `inventories_near(entity, radius)` | upstream | **new** `Nearby.Within(Items.FindInventories(), entity, radius)` | full; lab "world inventories: 21" |
| `inventory(entity)` | upstream | `Items.Read(entity)` | full; lab "world inventory: 24 slot(s)" |
| `move_stack_between(from, slot, to)` | upstream | **gap** | full; lab "world move_stack: 4 iron bars moved, chest 7 -> 11" |
| `apply_buff(entity, guid, seconds)` | upstream impact hooks | `Buffs.ApplyBuff(target, guid, duration)` (Kiln.Abilities) | full; lab "buffs: feature on" (no player to buff) |
| `player_name(entity)` | upstream | `Players.Find(entity)?.Name` | full; untested (no player) |
| `entities_near(entity, radius, type)` | upstream | **new** `Nearby.Entities(entity, radius, component)` | full; lab "world entities_near: 1 inventory entity(s) within 0.5 m" |
| `move_items(from, slot, to, count)` | upstream | **gap** | full; lab "world move_items: 3 of 9 iron bars moved" |
| `give_items(entity, item, count)` | upstream | `Items.Give` | full; lab "give_items: 5 plant fibre created" |
| `remove_items(entity, slot, count)` | upstream | `Items.TakeSlot` / `Take` | full; lab "remove_items: 2 removed" |
| `world_chest_name(entity)` | upstream | **gap** | full; lab "world chest_name: the test chest is \"Ores\"" |

### What a mod exports (`ModDecl` v1-v8)

| shroudlink API | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `shroudlink_mod_v2` / `shroudlink_mod_v1` exports | upstream loader (v1, v2, v3+ layouts) | `Mod` subclass + `kiln.mod.json` | full; host `loads_a_real_mod_from_a_profile_root`, lab 6 mods loaded |
| `id` | upstream | `ModInfo.Id` | full; lab |
| `on_load` | upstream | `Mod.OnLoad` | full; lab |
| `on_join` / `on_leave` (addon session) | upstream link host | `PlayerJoinedEvent` / `PlayerLeftEvent` | full; host `full_session`, lab addon session |
| `on_message` | upstream | `Network` channel handlers | full; lab "assistants window opens", "item search" |
| `keep` (slots a transfer must not move) | upstream inventory hooks (quick stack, area deposit, move all) | **gap** (no inventory-transfer event) | full; lab "quick stack keeps locked slot", "move all keeps locked slot" |
| `on_transfer` (mod moves stacks first) | upstream | **gap** | full; lab "quick stack sorts by category" |
| `game_events` / `on_game_event` | upstream dispatcher hook, chained onto Kiln.Gameplay's | Kiln's typed events (`[Subscribe]`), no by-name subscription | full; lab "hook points: 14 found, features: ... game_events" (no events without players) |
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

Gaps left (see "What's left" in the wave report): container names, slot moves between inventories, inventory
transfer events with keep/take-over, raw component writes, game events by type name, slot marks/keys.

## Tests

- `native/plugins/shroudlink/build.sh --test` (with `SHROUDLINK_SRC=<clone of v0.8.0>` to build the example mod):
  upstream's host tests plus Kiln's (profile rules, roots, EML detection, loading a real mod from a profile root).
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
2. The player menu shows the **MODS** tab with chestsort, assistants, storage, meter, gemforge. kiln.log on the
   server: `[shroudlink] mod 'chestsort' registered` for each.
3. Rename a chest "Ores", press quick stack: iron bars go in. Press `L` on a backpack slot: gold rim, and quick stack
   leaves it.
4. MODS -> Storage lists the chests within 40 m; click an item: it comes from every chest.
5. Hit a creature: MODS -> meter shows the fight.
6. Singleplayer with Kiln in the client and the mods in `<game>/shroudlink_mods/` (+ `shroudlink_core.dll`): the
   client kiln.log says either `shroudlink core running` or `steps aside`; exactly one copy runs; the MODS tab works.
