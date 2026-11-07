# EMF mods on Kiln (kiln-emf)

Kiln loads unmodified mods for **EMF** (Enshrouded Modding Framework, MIT; mods built for EMF 0.3.3 up to 0.5.1)
through the plugin `kiln/plugins/kiln-emf.dll`. It hands every EMF mod an `EmfApi` table of version 26, the one EMF
0.5.1 ships, built on Kiln's services. Every function and its status: [emf-parity.md](emf-parity.md). You keep your EMF DLL mods and EMF's Lua mods; you do not need EMF's own `dinput8.dll` or
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
- **API version:** `"emf": N` above 26 is refused ("needs EMF API vN"). Mods built for older versions keep working.
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
- **EMF 0.5.x runs on current game builds; EMF 0.3.3 does not.** 0.3.3 goes idle there (`a required signature is
  missing: EMF stays idle`). kiln-emf notices this about 2 s in and logs how to hand the mods back. Put `;` in front of EMF's line in `kiln/chain.ini` (or remove it from `dinput8-chain.ini`), then
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
Tested in a lab dedicated server on svn 1285601, with a copy of a real world and no players
(`tools/emf/lab-check.py`). "Loads" means `emf_mod_load` returned true. "Works" means the
player-free check from its source passed. What only a joined player can show is in the client checklist below.

| Mod | Loads | Works (player-free evidence) | Missing on Kiln |
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
| loot_scale | yes | `hook_pattern` on the game's loot factor is installed and chained. | - |
| loot_magnet, bulk_craft, companion, villager_jobs | yes | Ready, and their mod programs (`emit_event`) registered. They act on a player's input. | wheel (client) |
| emf_probe (Kiln's test mod) | yes | 72 checks over the whole table, 0 failures. | - |

## Compatibility by function
The full table for EmfApi v26 (85 functions, and EMF's Lua API) is [emf-parity.md](emf-parity.md). v22-v26 in short:
player level and XP through `kiln.progression`; `change_component` and E-prompt offers through kiln-emf's own
`kiln.world` program; mod windows and pictures through `kiln.ui` in the game client (refused on a dedicated server, as
in EMF). The v21 part:

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
| `read_events` | partial | one `events_in` per program (all shipped mods) |
| `program_singleton`, `program_read_other` | full | `kiln.world` iterator slots |
| `program_write_other` | no | `kiln.world` v1 has no other-entity writes |
| `hook_program` | no | no program-update hook in Kiln yet |
| `resolve`, `hook` | full | EMF signature name → Kiln sigdb symbol (42 of 51 resolve on the server, 47 on the client; 4 `Post*` client names are open) |
| `find_pattern`, `hook_pattern`, `hook_address` | full | on-disk scan, safetyhook detours that chain over Kiln's own hooks |
| `write_memory` | full | private data pages only, never code |
| `game_build` | full | `known` = Kiln verified this exe; `number` = its svn |
| `for_each_item`, `find_item`, `item_info` | full | `kiln.items` catalog, live max stack from `kiln.content` |
| `item_data` | full | `kiln.content` entry (2088 bytes) |
| `set_item_max_stack` | full | `kiln.content` layer of the mod (stack guard on) |
| `put_items`, `put_items_ex`, `take_items` | full | `kiln.items` transactions |
| `give_item` | full | `kiln.spawn` pickup 1 m above the player |
| `spawn`, `destroy` | full | `kiln.spawn` (not saved, except villagers) |
| `spawn_ex` | partial | without extra components it is `spawn`; with components it is refused |
| `attribute_get`, `attribute_add` | partial | Current and maximum health (`kiln.combat`). Stamina and mana are not supported. |
| `damage` | full | `kiln.combat` hit (light reaction, kill credit) |
| `teleport` | full | `kiln.world` teleport. `NOTIFY_READY` for players: READY 1 s after MOVED. |
| `chat_tell`, `chat_broadcast`, `player_name`, `on_chat` | full | `kiln.chat` (30 lines / 10 s per mod) |
| `register_command`, `register_command_ex`, `command_reply`, `command_sender` | full | players' chat via `kiln.chat` + `emf/commands.txt` |
| `on_save`, `on_load`, `request_save` | full | `kiln.saves` events / request |
| `world_setting_get/set/reset/info`, `on_world_setting_changed` | full | `kiln.settings` (short names, minutes; `persist` per world slot) |
| `setting_*`, `on_setting_changed`, `mod_info`, `mod_info_at` | full | kept by kiln-emf (`<configDir>/emf/<id>.ini`) |
| `hud_set`, `notify` | partial | `kiln.ui` overlay on the client (`hud_set` verified in game with clock; `notify` not yet seen); a server keeps HUD lines and logs notes |
| `wheel_open` | no | no radial wheel yet (client) |

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
- loot_magnet, bulk_craft, quick_stack and villager_jobs while a player plays. Their wheels are not supported.
- v24 windows (client, a world you host): mark a prop as a stable with beastmaster's `beastmaster.stable mark`, look
  at it and press E: the Beasts window opens with its rows; a click on a row reaches the mod; Esc and the X close it;
  the mouse returns to the game.
- v26 pictures: a mod window with a picture (`image_load` of a PNG in the mod's folder) draws it; a folder of frames
  with `fps` turns.
- v25 E prompts: a marked stable (beastmaster) shows the game's "E <verb>" prompt within 32 m; pressing E runs the mod's callback (log line), for the host and a joined player.
- v22: blessings' level reward (`set_level`) queues the level for the next step; nonzero XP towards that level follows
  after the result, several steps later (see [parity limits](emf-parity.md)). Given XP
  levels the player up with the game's own level-up message.
