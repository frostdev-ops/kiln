# EMF API parity (EmfApi v26)

kiln-emf (`native/plugins/emf`) hands every EMF mod an `EmfApi` table built on Kiln's services. This page lists
every function of EMF 0.5.1's C API (v26, 85 functions, `sdk/include/emf/emf_api.h`) and every function of its Lua
API (`docs/modding/lua.md`), with the Kiln service behind it, the nearest Kiln C# API, and the status.

- **full**: behaves as EMF documents. **partial**: the difference is in the row. **no**: logs
  `emf: <mod> called <fn>, not supported` once and returns EMF's documented failure (false / 0 / NULL).
- **tested**: `probe` = the conformance probe (`mods/tests/emf-test/probe`, lab server), `host` = host test
  (`native/plugins/emf/tests/run.sh`), `client` = only a joined player can show it (client checklist in
  [emf.md](emf.md)), `lab-mod` = a shipped EMF mod uses it in the lab check (`tools/emf/lab-check.py`).
- Counts: v21 had 61 full, 9 partial, 3 no out of 73; v22-v26's 12 functions were missing and every 0.5 mod was
  refused (EMF's SDK refuses a table smaller than its own `sizeof(EmfApi)`). Now: **67 full, 15 partial, 3 no
  out of 85.**

## Version negotiation

As EMF 0.5.1 does it: one table, `version = 26`, `size = sizeof(EmfApi) = 688`. Slots are only appended (v22 at
byte 592, v23 616, v24 624, v25 648, v26 672). A mod's `mod.json` `"emf": N` above 26 is refused ("needs EMF API vN").
Mods built with EMF's C++ SDK also check `api->size >= sizeof(EmfApi)` of their own header, so a v26 mod needs the
whole v26 table. The table is a shared, immutable prefix-compatible view; it is not shrunk for an older mod.
Structs passed in are read up to their `size` field (EMF's `api_sizes.h` rules): an `EmfUiWidget` array is walked by
`widget_size` (44..4096 bytes, at most 256 widgets), and a v24/v25 mod's 56-byte widgets read `icon`, `fps` and
`height` as 0.

## C API

| EMF function (since) | Kiln implementation | Kiln C# equivalent | Status | Tested |
|---|---|---|---|---|
| `log` (1) | `kiln.log` under `[emf:<mod>]` | `Logger` | full | probe |
| `server_world`, `client_world`, `world_kind` (1) | core worlds | `Kiln.World`, `Kiln.Client` | full | probe |
| `world_tick`, `ecs_time_ns` (1) | core tick info | `TickEvents` | full | probe |
| `singleton` (1) | core singleton map | `Kiln.Api` game types (singletons) | full | probe |
| `local_player` (1) | `kiln.client` | `Kiln.Client` | full | probe |
| `get_component` (1) | `kiln.world` component / the game's GetComponent | `World.TryRead<T>` | full | probe |
| `for_each_entity` (1) | the storage's entity map | `World.Find` | full | probe |
| `on_tick`, `run_on_world` (1) | Post of each world's step | `TickEvents` / `Scheduler` | full | probe |
| `resolve`, `hook` (1) | EMF signature name → Kiln sigdb (`sigdb/symbols/*.d/emf.json`) | `Kiln.Hooks` | full | probe |
| `hook_program` (1) | — | — | no | probe (refusal) |
| `register_program`, `register_program_def` (2/16) | `kiln.world` programs, server world | `Kiln.Api` ECS programs | partial: server world only | probe |
| `spawn`, `destroy` (2) | `kiln.spawn` | `Kiln.Spawn` | full | probe |
| `spawn_ex` (12) | `kiln.spawn` | `Kiln.Spawn` | partial: no spawn-time components | probe |
| `on_save`, `on_load` (2) | `kiln.saves` events | `Kiln.Saves` events | full | probe |
| `emit_event` (3) | the game's emitter (as `kiln.gameplay`) | `Kiln.Gameplay` | full | probe |
| `read_events` (3) | one `events_in` per program | `Kiln.Api` ECS programs | partial: one event type per program | probe |
| `on_event` (4) | one `kiln.world` program per event type | `Events.On<T>` (service events) | partial: server world only | probe |
| `for_each_item`, `find_item`, `item_info` (5) | `kiln.items` catalog | `Kiln.Items` | full | probe |
| `give_item` (5) | `kiln.spawn` pickup | `Kiln.Items` | full | probe |
| `register_command`, `command_reply` (6) | players' chat (`kiln.chat`) + `emf/commands.txt` | `Kiln.Chat` commands | full | probe, lab-mod |
| `get_template_component` (7) | the game's GetTemplateComponent | `Kiln.World` | full | probe |
| `item_data`, `set_item_max_stack` (7) | `kiln.content` | `Kiln.Content` | full | probe, lab-mod (big_stacks) |
| `write_memory` (7) | private data pages only | `Kiln.Hooks` | full | probe |
| `program_singleton`, `program_read_other` (8) | `kiln.world` iterator slots | `Kiln.Api` ECS programs | full | probe |
| `put_items` (8), `put_items_ex`, `take_items` (11) | `kiln.items` transactions | `Kiln.Items` | full | probe |
| `server_session` (9) | core session | `Kiln.World` | full | probe |
| `wheel_open` (10) | — | — | no: no radial wheel yet | probe (refusal) |
| `notify` (10), `hud_set` (12) | `kiln.ui` overlay | `HudLine` (`Kiln.Ui`) | partial: a server keeps HUD lines and logs notes | client |
| `setting_declare/_int/_float/_text/_set`, `on_setting_changed` (13) | kiln-emf (`<configDir>/emf/<id>.ini`) | `ModConfig` | full | probe |
| `mod_info`, `mod_info_at` (13) | kiln-emf | `ModEntry` | full | probe |
| `find_pattern`, `hook_pattern`, `hook_address` (14) | on-disk scan, safetyhook | `Kiln.Hooks` | full | probe, lab-mod (loot_scale) |
| `game_build` (14) | Kiln's verified build records | `Diagnostics` | full | probe |
| `command_sender` (15), `register_command_ex` (17) | `kiln.chat` | `Kiln.Chat` | full | probe |
| `program_write_other` (16) | — | — | no: `kiln.world` has no other-entity writes | probe (refusal) |
| `attribute_get`, `attribute_add` (18) | `kiln.combat` health; v22 ids through `kiln.progression` | `Kiln.Combat`, `PlayerProgression` | partial: stamina and mana not supported | probe |
| `damage` (18) | `kiln.combat` hit | `Kiln.Combat` | full | probe |
| `chat_tell`, `chat_broadcast`, `player_name`, `on_chat` (19) | `kiln.chat` | `Kiln.Chat` | full | probe, lab-mod (herald) |
| `request_save` (19) | `kiln.saves` | `Kiln.Saves` | full | probe |
| `teleport` (20) | `kiln.world` teleport | `Kiln.World` teleport | full | probe |
| `world_setting_get/_set/_reset/_info`, `on_world_setting_changed` (21) | `kiln.settings` | `Kiln.Settings` | full | probe, lab-mod (server-admin) |
| `player_xp_required` (22) | `kiln.progression` `xp_for_level` (cached per session, any thread) | `PlayerProgression` | full | probe |
| `player_level_cap` (22) | `kiln.progression` | `PlayerProgression` | full | probe |
| `player_level_set` (22) | `kiln.progression` SET_LEVEL + SET_EXPERIENCE | `PlayerProgression` | partial: level queued for the next server step, even in a world callback (EMF: at once there); nonzero XP is a second request after the level result (several steps later), suppressed if superseded or the session/level changed | probe (refusals), host |
| `EMF_ATTR_XP_PENDING` / `_XP` / `_XP_REQUIRED` / `_LEVEL` (22) | `kiln.progression` get / ADD_EXPERIENCE | `PlayerProgression` | partial: pending XP is queued, at most 100,000,000 per call (`kiln.progression`); reads require the server thread | probe (refusals), host |
| `change_component` (23) | kiln-emf's `emf_components` program on `kiln.world` (`queue_add` / `queue_remove`), EMF's list and queue | `Kiln.Api` ECS program `QueueAdd` / `QueueRemove` (low level) | full | probe, host |
| `window_open`, `window_update`, `window_close` (24) | `kiln.ui` ImGui window, capture, keys | `UiWindow` | partial: no right clicks; plain ImGui look; refused on a dedicated server (as EMF) | probe (server refusal), host, client |
| `spawn_offer` (25) | `kiln.spawn` + the offer below | — | partial: the prompt appears a step or two after the spawn | probe |
| `offer`, `offer_remove` (25) | the three offer components through `emf_components`; `InteractionAcceptedEvent` listener | — | full | probe (components), host, client (the prompt, the accept) |
| `image_load` (26) | `kiln.ui` `image_load` (PNG frames), EMF's limits and paths; encoded files also limited to 64 MB | `UiImage` | partial: encoded-file limit in addition to EMF's decoded-pixel limit | host, client |
| `offer_active` (25/26) | kiln-emf's offer registry | — | full | probe, host |

## Lua API (`emf.*`, run by EMF's `lua` mod on the C API)

EMF's `lua` mod is an EMF DLL mod built on the public C API only, so every Lua function works on Kiln exactly as far
as the C function under it does. Lua mods need EMF's `lua` mod in `emf/mods/lua/` (0.5.1's build needs the v26 table).

| Lua | C under it | Status |
|---|---|---|
| `emf.id`, `emf.dir`, `emf.log`, `print` | `log`, mod info | full |
| `emf.notify`, `emf.hud` | `notify`, `hud_set` | partial (as C) |
| `emf.on_tick` | `on_tick` | full |
| `emf.bus.subscribe/publish/unsubscribe` | the Lua host itself (`run_on_world`) | full |
| `emf.on_event`, `emf.emits`, `emf.emit` | `on_event`, `register_program_def`, `emit_event` | partial: server world only |
| `emf.command`, `emf.reply`, `emf.sender` | `register_command_ex`, `command_reply`, `command_sender` | full |
| `emf.local_player`, `emf.session`, `emf.session_token`, `emf.time_of_day` | worlds, session, singleton | full |
| `emf.on_world_data`, `emf.save_world_data` | `on_save` / `on_load` | full |
| `emf.setting`, `emf.set_setting`, `emf.on_setting_changed`, `emf.on_key`, `emf.key_down` | settings | full (`on_key`: client) |
| `emf.item`, `emf.item_info`, `emf.give`, `emf.put`, `emf.take`, `emf.move` | items | full |
| `emf.spawn`, `emf.destroy` | `spawn_ex`, `destroy` | partial: no spawn-time components |
| `emf.kill`, `emf.health`, `emf.attribute`, `emf.attribute_add`, `emf.damage` | v18 attributes, `damage` | partial: health and XP/level; no stamina, mana |
| `emf.xp_required`, `emf.level_cap`, `emf.set_level` | v22 | full / full / partial (queued level, then XP) |
| `emf.add_component`, `emf.remove_component` | `change_component` | full |
| `emf.tell`, `emf.broadcast`, `emf.on_chat`, `emf.request_save` | v19 | full |
| `emf.teleport` | `teleport` | full |
| `emf.world_settings.get/set/reset/list`, `emf.on_world_setting_changed` | v21 | full |
| `emf.wheel`, `emf.wheel_section`, `emf.wheel_group` | `wheel_open` | no |
| `emf.window`, `emf.window_update`, `emf.window_close`, `emf.image` | v24, v26 | partial (as C) |
| `emf.offer`, `emf.spawn_offer`, `emf.offer_remove`, `emf.offered` | v25, `offer_active` | full / partial / full / full |
| `world:is_server`, `:is_client`, `:tick`, `:entities`, `:position`, `:get`, `:template`, `:name`, `:account` | worlds, components, `player_name` | full |

## Lifetime and host checks

World teardown closes windows exactly once with `BY_CORE`, `NO_WORLD` and a null world, drops queued clicks and accepts,
and fails pending component changes with a null world. Image handles are cached for the process as EMF specifies;
failed multi-frame loads release already decoded frames, and plugin shutdown releases the cache. Offers only remove
support components they added. Native EMF DLLs remain loaded until process exit; there is no per-mod unload/reload
entry point in kiln-emf.

`native/plugins/emf/tests/run.sh` checks the frozen x64 ABI slot map, model policy/queues and the production window,
progression, offer and event adapters with fake Kiln services (ASan/UBSan). These checks do not demonstrate rendering,
replication or interaction in a running game.

## What is not in kiln-emf

- **EML content** (`emf_content.dll`, GPL-3.0 in EMF): EMF's loader runs it before the game starts. It is not part of
  the C API above. Kiln's GPL plugin `kiln-eml` covers EML content; kiln-emf stays MIT and does not load it.
- EMF's own console UI, debug bridge, save backups and profile manager are EMF's tooling, not API.
