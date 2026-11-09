# EMF mods on Kiln (kiln-emf)

Kiln loads unmodified mods for **EMF** (Enshrouded Modding Framework, MIT; API versions through EMF 0.6.0 / v27)
through the plugin `kiln/plugins/kiln-emf.dll`. It supplies an `EmfApi` v27 table built on Kiln's services:
77 full, 13 partial and no missing C slots out of 90. API coverage is not a guarantee for every mod pairing or
player-facing feature. Every function and its evidence: [emf-parity.md](emf-parity.md). You keep your EMF DLL mods and EMF's Lua mods; you do not need EMF's own `dinput8.dll` or
`emf_core.dll`.

Evidence, design and the mapping of each function: [docs/re/emf-compat.md](../re/emf-compat.md).

## Install a mod

1. Install Kiln as usual. `tools/deploy-server.sh` copies `kiln-emf.dll` to `kiln/plugins/`.
2. Put the mod where EMF would put it: a folder `<game>/emf/mods/<id>/` with its `mod.json` and DLL, or a single
   `<game>/emf/mods/<name>.dll`. EMF's Lua mods also need the `lua` mod (`emf/mods/lua/`).
   With the Kiln Launcher, a profile's `emf` entries are loaded too (`kiln.profile`). The `emf/mods` folder still
   counts while the profile has EMF drop-ins on, which is the default.
3. Switch it on. Most shipped EMF mods start switched off. Edit `<configDir>/emf/<id>.ini`:
   ```ini
   [big_stacks]
   enabled=1
   multiplier=4
   max=9999
   ```
   `configDir` is `<game>/kiln/config`, or the active profile's `configDir`. kiln-emf creates the file at the first
   start, with every setting at its `mod.json` default. If EMF ran in this folder before, the first start takes the
   values from EMF's own `<game>/emf/config/<id>.ini`.
4. Start the server or the game. `kiln/logs/kiln.log` has one line per mod. Each mod logs under `[emf:<id>]`, for
   example `[emf:big_stacks] big_stacks 1.0.0 (big_stacks\, legacy): loaded in 1.4 ms`. Every mod's status is also in
   `kiln/state/emf.json`.

### Settings
- **Where values live:** `<configDir>/emf/<id>.ini` holds them, in section `[<id>]`. `enabled=0/1` is the mod's
  switch and applies at the next start. The other keys are the mod's settings.
- **The mirror:** kiln-emf copies the section to `<game>/emf/config/<id>.ini`, the file EMF uses. Shipped mods read
  their switch and values there directly (big_stacks, sleep, herald, clock). Edit the Kiln file, not the mirror: the
  mirror is rewritten at each start.
- **Validation:** values are checked as EMF checks them. Out of range is clamped when read from the file and refused
  when set by a command. A choice is stored as its index, a select as the option's value, and a colour as `#rrggbb`.
- **Schema for the launcher:** `<configDir>/.schema/emf/<id>.json` describes the settings (no values), as
  `docs/kiln/launcher/contracts.md` 5.5 defines.
- **Changing values while the game runs:** use the commands `emf.set <mod> <key> <value>` and
  `emf.settings <mod>` (below). A mod that marks a setting `live` sees the change at once.

### Which mods load
- **Switch:** `enabled` in the ini decides. Without a value, `mod.json`'s `"enabled"` decides (default on). A
  profile entry with `enabled: false` is always off.
- **API version:** `"emf": N` above 27 is refused ("needs EMF API vN"). Older API prefixes are accepted; individual mods remain subject to the compatibility limits below.
- **Side:** `"side": "client"` mods do nothing on a dedicated server, so the server skips them (clock). Everything
  else loads everywhere, as in EMF.
- **Dependencies:** `depends` / `dependencies` take EMF's version ranges (`^1.2`, `~1.2`, `>=1.0 <2`, `1.2.x`,
  `1.2 - 2`, `||`). A mod whose required dependency is missing, off, too old or failed does not load, and the log
  says why. Mods load in dependency order, then by folder name.
- **Script mods:** a `mod.json` with `"main"` instead of `"dll"` is a script mod. The `lua` mod runs it (status
  `script`).
- **Faults:** a mod whose `emf_mod_load` returns false or faults is marked `failed`. Its DLL stays loaded: Kiln never
  unloads an EMF mod.

### When EMF itself is installed
- **kiln-emf steps aside** when EMF's real core runs in the same process: `emf_core.dll` is loaded, or EMF's loader
  was chained by Kiln's proxy (`kiln/chain.ini [chain]`, `dinput8-chain.ini`). The log says
  `kiln-emf steps aside: ...`. Then EMF loads the EMF mods, never both.
- **The older EMF 0.3.3 core went idle on the tested game build.** It reports `a required signature is
  missing: EMF stays idle`. kiln-emf notices this about 2 s in and logs how to hand the mods back. Put `;` in front of EMF's line in `kiln/chain.ini` (or remove it from `dinput8-chain.ini`), then
  restart. EMF's files can stay in `emf/`; only its loader must not run.

### What a mod costs
`kiln/state/emf.json` lists every mod with its status and reason, the time its callbacks take per server step
(`tickMsPerStep`, `tickMsMax`), the commands it registered, and the functions it called that Kiln does not support.
In the lab, server-admin cost about 1.1 ms per step and the lua mod with 8 scripts about 0.8 ms. Every other shipped
mod cost 0.1 ms or less.

## Commands
EMF mods register commands (`tp`, `herald.say`, `raids.status`, ...). On Kiln they run from two places:
- **Players' chat:** `/<command> args`. The line is consumed, so nobody else sees it. Replies come back as chat lines
  only that player sees (`[EMF] ...`).
  - **Who may run them:** commands a mod marks for players (`EMF_COMMAND_PLAYERS`: `/home`, `/unstuck`,
    `/players`, `/sleep.status`, ...) are for everyone. Every other command is for admins only.
  - **Admins:** players with the game's admin flag, accounts listed in `[chat] admins=0x...` in
    `<configDir>/emf/emf_core.ini` (EMF's own `<game>/emf/emf_core.ini` is read when that file is missing), or
    everyone with `everyone=1`.
  - `[chat] deny=...` takes players' commands away from non-admins. Non-admins may send at most 5 commands in 10 s.
- **The console file:** `<game>/emf/commands.txt`, one command per line. It runs as the server owner, and replies go
  to `kiln.log` as `[cmd] ...`.

Built-ins: `emf.help [page]`, `emf.mods`, `emf.settings <mod>`, `emf.set <mod> <key> <value>`, `chat.players` (in
`commands.txt` also without the `emf.` prefix). `/help` stays Kiln's own command.

## Compatibility by mod
The following table preserves an earlier lab snapshot on svn 1285601, with a copy of a real world and no players
(`tools/emf/lab-check.py`). "Loads" means `emf_mod_load` returned true. "Works" means the
player-free check from its source passed. It is not the current API gap list; use [emf-parity.md](emf-parity.md).
The dev.20 mixed-server checks loaded Lua 1.19, Beastmaster 1.13 and ShroudForge in two boots, including the bear/beasts
asset packs. They do not establish taming, custom portrait display or joined multiplayer. Current messaging and
offline portrait evidence: [emf-network.md](emf-network.md), [emf-portraits.md](emf-portraits.md).
What only a joined player can show is in the client checklist below.

| Mod | Loads | Works (player-free evidence) | Limitation at that earlier check |
|---|---|---|---|
| big_stacks | yes | Raised 2252 items x4 (at most 9999). Kiln's own content registry agrees: wood logs 250 → 1000, 0 items off the formula. The stack guard is on. | - |
| server-admin | yes | Its file ops all work: `ping` (api 21), `items` (4213, wood max stack 1000), `settings.set` with read-back, `settings.reset`, an out-of-range refusal, `save` acknowledged through `on_save`. It publishes `state.json` with 38 world settings. | - |
| herald | yes | `players` answers, `herald.say` broadcasts, and `on_chat` is on. | - |
| sleep | yes | `sleep.status` answers, and `game_build` reports a known build, so it does not go idle. | - |
| teleports | yes | All 7 commands are registered (`/home`, `/unstuck` for players), and `tp.pos` answers. The teleport itself is verified on a villager by the probe. | - |
| clock | client: yes (server: skipped, side `client`) | In a client, offline: the time of day in the bottom-right corner through `kiln.ui`, following the game's clock (03:13 → 03:16 while Kiln's probe logged 3.18 h → 3.29 h). | - |
| lua | yes | Runs 8 Lua mods; `lua.status` lists them. | - |
| beastmaster, bounty, champion, grave, handover, kill_counter, life_skills, necromancer (Lua) | yes (script) | Running under `lua`. Their commands answer from the console ("could not tell which player you are"). | wheel (client) |
| quick_stack | yes | `qs.list` answers. Chest moves need a player. | wheel (client) |
| raids | yes | `raids.status` / `raids.world on` answer, and its program registered. At nightfall with raids on it logged `nightfall: a raid was due, but nobody is at a base` (time of day, kept per world). | - |
| loot_scale | yes | `hook_pattern` on the game's loot factor is installed as a link in Kiln's chain. | - |
| loot_magnet, bulk_craft, companion, villager_jobs | yes | Ready, and their mod programs (`emit_event`) registered. They act on a player's input. | wheel (client) |
| emf_probe (Kiln's test mod) | yes | 72 checks over the whole table, 0 failures. | - |

## Hook order
`hook`, `hook_pattern` and `hook_address` put each mod's detour into [kiln.hooks v2](services/hooks.md): one physical
detour per game function, shared with Kiln's own services, and your detour is a **link** in it, owned by `emf:<mod id>` and
labelled with the signature name (`state/hooks.json` lists it). `*original` is the link's continuation: it runs the rest of
the chain and then the game's function. As EMF documents, a later hook on a function runs before an earlier one; mods'
links also run before Kiln's own links on that function. Kiln's services read the game's call site with `caller()`, so
your hook in front of them cannot confuse them. The log says `hook <name> at exe+0x... (in Kiln's hook chain)`.

A target outside the game exe (another DLL's code, through `hook_address`) is not in kiln.hooks' reach: it still gets a
plain detour and the log says `outside the game exe`. `kiln-emf`'s own hooks (ChatLogAdd and ClientOutboxFlush for the
v27 client chat bridge) are links too, `ChatLogAdd` after `kiln.chat`'s.

## Compatibility by function
The full table for EmfApi v27 (90 functions, and EMF's Lua API) is [emf-parity.md](emf-parity.md). v22-v27 in short:
player level and XP through `kiln.progression`; `change_component` and E-prompt offers through kiln-emf's own
`kiln.world` program; mod windows and pictures through `kiln.ui` in the game client (refused on a dedicated server, as
in EMF). API v27 adds mod messaging on the official `#!emf1` wire; joined-client handshake and gameplay still need checks.
The v21 prefix, with current implementations:

EmfApi v21 has 73 functions. **Full** means it behaves as EMF documents. **Partial** means the differences are
listed. **No** means it logs `emf: <mod> called <fn>, not supported` once and returns a safe failure.

| Function | Status | On Kiln |
|---|---|---|
| `log` | full | `kiln.log` under `[emf:<mod>]` |
| `server_world`, `client_world`, `world_kind`, `world_tick`, `ecs_time_ns`, `server_session`, `local_player` | full | core worlds/session, `kiln.client` |
| `singleton` | full | core singleton map |
| `get_component` | full | `kiln.world` (server), the game's GetComponent (client worlds) |
| `get_template_component` | full | the game's GetTemplateComponent; the size comes from the game's reflection |
| `for_each_entity` | full | the storage's entity map (layout from the side's sigdb, as `kiln.world` derives it); client worlds not yet checked in game |
| `on_tick`, `run_on_world` | full | Post of each world's step (server: after `WaitWorldJobs`) |
| `on_event` | partial | One `kiln.world` program per event type. Server world only. EntityDiedEvent arrives only near a player. |
| `register_program`, `register_program_def` | partial | `kiln.world` system programs (server world). Programs on the client world are not supported. |
| `emit_event` | full | the game's emitter, as `kiln.gameplay` uses it |
| `read_events` | full | multiple declared event inputs through `kiln.world` programs |
| `program_singleton`, `program_read_other` | full | `kiln.world` iterator slots |
| `program_write_other` | full | declared other-entity writes through `kiln.world` programs |
| `hook_program` | full | program-update pre/post callbacks through `kiln.types` |
| `resolve`, `hook` | full | EMF signature name → Kiln sigdb symbol (42 of 51 resolve on the server, 47 on the client; 4 `Post*` client names are open) |
| `find_pattern`, `hook_pattern`, `hook_address` | full | on-disk scan; the detour is a [kiln.hooks v2](services/hooks.md) link owned by `emf:<mod id>` (see [Hook order](#hook-order)) |
| `write_memory` | full | private data pages only, never code |
| `game_build` | full | `known` = Kiln verified this exe; `number` = its svn |
| `for_each_item`, `find_item`, `item_info` | full | `kiln.items` catalog, live max stack from `kiln.content` |
| `item_data` | full | `kiln.content` entry (2088 bytes) |
| `set_item_max_stack` | full | `kiln.content` layer of the mod (stack guard on) |
| `put_items`, `put_items_ex`, `take_items` | full | `kiln.items` transactions |
| `give_item` | full | `kiln.spawn` pickup 1 m above the player |
| `spawn`, `destroy` | full | `kiln.spawn` (not saved, except villagers) |
| `spawn_ex` | full | spawn-time extra components through `kiln.spawn` |
| `attribute_get`, `attribute_add` | partial | health, stamina and mana through `kiln.combat`; XP/level keep queued progression timing and request limits |
| `damage` | full | `kiln.combat` hit (light reaction, kill credit) |
| `teleport` | full | `kiln.world` teleport. `NOTIFY_READY` for players: READY 1 s after MOVED. |
| `chat_tell`, `chat_broadcast`, `player_name`, `on_chat` | full | `kiln.chat` (30 lines / 10 s per mod) |
| `register_command`, `register_command_ex`, `command_reply`, `command_sender` | full | players' chat via `kiln.chat` + `emf/commands.txt` |
| `on_save`, `on_load`, `request_save` | full | `kiln.saves` events / request |
| `world_setting_get/set/reset/info`, `on_world_setting_changed` | full | `kiln.settings` (short names, minutes; `persist` per world slot) |
| `setting_*`, `on_setting_changed`, `mod_info`, `mod_info_at` | full | kept by kiln-emf (`<configDir>/emf/<id>.ini`) |
| `hud_set`, `notify` | partial | `kiln.ui` overlay on the client (`hud_set` verified in game with clock; `notify` not yet seen); a server keeps HUD lines and logs notes |
| `wheel_open` | full | radial wheel through `kiln.ui`; host checks pass, client drawing/input still need a player |

## Client checklist (needs a joined player)
- The clock shows the time of day in the bottom-right corner (`hud_set` through `kiln.ui`, client and
  hosted server world ticks, `singleton` on the client world).
- Notes (`notify`) appear at the top of the screen.
- Players' chat commands: `/home`, `/unstuck` (teleports), `/players` (herald). Replies arrive as `[EMF] ...` lines.
- Admin commands are refused for a non-admin account, with the account hash in the log.
- The herald MOTD and join/leave lines; `on_chat` sees players' lines.
- `EntityDiedEvent` mods: herald death lines, companion, server-admin graves, and a kill counted by kill_counter.
- `put_items` / `take_items` into a player inventory (server-admin `inventory.add` / `inventory.remove`), and
  `give_item` drops.
- Teleporting a player: server-admin `teleport`, `tp.pos`, and READY after the destination loads.
- loot_magnet, bulk_craft, quick_stack and villager_jobs while a player plays. Their wheels (`wheel_open`) use kiln.ui's radial
  wheel since dev.20: see the wheel items below.
- Radial wheel (client, a world you host): open a mod's wheel (a Lua `emf.wheel`, e.g. quick_stack's). It shows in the
  middle with its title; the pointer lights the sector it is in and the camera does not move; a left click on a lit entry
  picks it (the mod's callback runs, log line); Esc and a right click cancel (callback with -1). With a mod whose wheel
  has a hold key: holding the key, moving to an entry and letting go picks; letting go in the middle cancels; a quick tap
  leaves it open for a click. With more than 8 entries the mouse wheel turns the page. "Name (details)" labels show the
  details on a second line. While it is open a mod window under it gets no clicks, and `window_open` is refused.
- Window right clicks (v24, client): right-click a row or a button with a key in a mod window (beastmaster's Beasts window
  rows): the mod's callback gets `EMF_UI_RIGHT_CLICK` with the widget's key (log line); a left click still sends `EMF_UI_CLICK`.
- Stamina and mana of a real player: `attribute_get` / `attribute_add` of `EMF_ATTR_STAMINA` / `EMF_ATTR_MANA` on a joined
  player (a mod that spends mana, or `emf.attribute_add`): the bar follows (the lab shows stamina on a creature only).
- v24 windows (client, a world you host): mark a prop as a stable with beastmaster's `beastmaster.stable mark`, look
  at it and press E: the Beasts window opens with its rows; a click on a row reaches the mod; Esc and the X close it;
  the mouse returns to the game.
- v26 pictures: a mod window with a picture (`image_load` of a PNG in the mod's folder) draws it; a folder of frames
  with `fps` turns.
- v25 E prompts: a marked stable (beastmaster) shows the game's "E <verb>" prompt within 32 m; pressing E runs the mod's callback (log line), for the host and a joined player.
- v22: blessings' level reward (`set_level`) queues the level for the next step; nonzero XP towards that level follows
  after the result, several steps later (see [parity limits](emf-parity.md)). Given XP
  levels the player up with the game's own level-up message.
