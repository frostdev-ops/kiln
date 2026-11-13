# EMF API parity (EmfApi v40)

kiln-emf (`native/plugins/emf`) hands every EMF mod an `EmfApi` table built on Kiln's services. This page lists
every function of EMF 0.6.8's C API (v40, 135 functions, `sdk/include/emf/emf_api.h`) and every function of its Lua
API (`docs/modding/lua.md`), with the Kiln service behind it, the nearest Kiln C# API, and the status.

- **full**: behaves as EMF documents. **partial**: the difference is in the row. **not yet**: Kiln does not carry
  the function; it logs `emf: <mod> called <fn>, not supported (not available on Kiln yet)` once per mod and returns
  EMF's documented failure (false / 0 / an empty buffer / a reason in the request), as EMF's own core does on a game
  build where the feature is off. A callback handed to such a function is never called.
- **tested**: `probe` = the conformance probe (`mods/tests/emf-test/probe`, lab server), `host` = host test
  (`native/plugins/emf/tests/run.sh`), `client` = only a joined player can show it (client checklist in
  [emf.md](emf.md)), `lab-mod` = a shipped EMF mod uses it in the lab check (`tools/emf/lab-check.py`).
- Counts: **76 full, 17 partial, 42 not yet out of 135.** The v27 prefix has 90 slots (75 full, 15 partial). v28 to
  v40 append 45: one full (`world_to_screen`), two partial (`chat_tell_ex`, `chat_broadcast_ex`) and 42 not yet
  ([the table below](#v28-to-v40-emf-063-to-068)). Counts are C table slots, not grouped rows or attribute constants.
  The two attribute slots remain partial because the XP/level ids use the queued progression path below. The four
  chat line slots are partial because a plain line (no `EMF_CHAT_SYSTEM`) shows without a speaker's name on Kiln and
  under the player's own name on EMF (counted as full until 0.2.0-dev.26; the behaviour did not change). Full means
  the implemented contract matches EMF; the evidence column separately names host, server and pending client checks.

API 27 messages use EMF's official `#!emf1` chat wire. Kiln peers can speak to EMF peers without Kiln's own network
protocol. Host checks use the upstream wire peer; the dedicated-server API probe checks the five functions. Two mixed
server boots (dev.20) loaded unmodified Lua 1.19, Beastmaster 1.13 and ShroudForge once each. Those boots do not verify
a joined player's handshake, hidden chat, taming, portraits, UI or multiplayer behaviour. See
[emf-network.md](emf-network.md), which also has the mod check on join (EMF 0.6.3).

## Version negotiation

One table, `version = 40`, `size = sizeof(EmfApi) = 1088`. Slots are only appended (v22 at
byte 592, v23 616, v24 624, v25 648, v26 672, v27 688, v28 728, v29 736, v30 752, v31 776, v32 808, v33 864, v34 920,
v35 952, v36 992, v37 1000, v38 1016, v39 1040, v40 1064). Every earlier prefix is unchanged. A mod's `mod.json`
`"emf": N` above 40 is refused ("needs EMF API vN"). Mods built with EMF's C++ SDK also check
`api->size >= sizeof(EmfApi)` of their own header and do not start on a smaller table, whatever they then call: this
is why Kiln serves the whole v40 table although it does not carry every function yet. A mod built for EMF 0.6.3 or
later, EMF's Lua host among them, did not start on Kiln's earlier 728-byte table. The table is a shared, immutable
prefix-compatible view; it is not shrunk for an older mod.
Structs passed in are read up to their `size` field (EMF's `api_sizes.h` rules): an `EmfUiWidget` array is walked by
`widget_size` (44..4096 bytes, at most 256 widgets), and a v24/v25 mod's 56-byte widgets read `icon`, `fps` and
`height` as 0. A v36 to v40 request (`EmfRemoveBuilding`, `EmfEnemyOrder`, `EmfMoveOrder`) whose `size` is smaller
than EMF's minimum is refused untouched, as in EMF.

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
| `hook_program` (1) | `kiln.types` runner pre/post callbacks | `GameSystems.Hook(name, before, after)` | full | host; server probe |
| `register_program`, `register_program_def` (2/16) | `kiln.world` programs, server world | `Kiln.Api` ECS programs | partial: server world only | probe |
| `spawn`, `destroy` (2) | `kiln.spawn` | `Kiln.Spawn` | full | probe |
| `spawn_ex` (12) | `kiln.spawn` components copied into the spawn request | `SpawnOptions.Components`, `SpawnComponent.Of` | full | host; server probe |
| `on_save`, `on_load` (2) | `kiln.saves` events | `Kiln.Saves` events | full | probe |
| `emit_event` (3) | the game's emitter (as `kiln.gameplay`) | `Kiln.Gameplay` | full | probe |
| `read_events` (3) | `kiln.world` programs v2, multiple event inputs | `Kiln.Api` ECS programs with multiple event inputs | full | host; server probe |
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
| `wheel_open` (10) | `kiln.ui` radial wheel; server refusal as EMF | `Ui.OpenWheel`, `UiWheel` | full | host; client drawing, mouse/keyboard/gamepad selection pending |
| `notify` (10), `hud_set` (12) | `kiln.ui` overlay | `HudLine` (`Kiln.Ui`) | partial: a server keeps HUD lines and logs notes | client |
| `setting_declare/_int/_float/_text/_set`, `on_setting_changed` (13) | kiln-emf (`<configDir>/emf/<id>.ini`) | `ModConfig` | full | probe |
| `mod_info`, `mod_info_at` (13) | kiln-emf | `ModEntry` | full | probe |
| `find_pattern`, `hook_pattern`, `hook_address` (14) | on-disk scan; the detour is a kiln.hooks v2 raw link owned by `emf:<id>` (newest first, also against Shroudtopia's detours, and before Kiln's own links; outside the exe, or when kiln.hooks has no free target slot: plain safetyhook) | `Kiln.Hooks` | full | probe, lab-mod (loot_scale) |
| `game_build` (14) | Kiln's verified build records | `Diagnostics` | full | probe |
| `command_sender` (15), `register_command_ex` (17) | `kiln.chat` | `Kiln.Chat` | full | probe |
| `program_write_other` (16) | `kiln.world` programs v2, declared other-entity writes | `EcsProgramContext.TryWriteOther<T>` | full | host; server probe |
| `attribute_get`, `attribute_add` (18) | `kiln.combat` health, stamina and mana; v22 ids through `kiln.progression` | `Attributes.Get/Set/Add`, `PlayerProgression` | partial: health/stamina/mana supported; XP/level timing and limits below | probe; host |
| `damage` (18) | `kiln.combat` hit | `Kiln.Combat` | full | probe |
| `chat_tell`, `chat_broadcast` (19) | `kiln.chat` server lines | `Chat.Tell`, `Chat.Broadcast` | partial: the line shows as its text alone, without a speaker's name (on EMF under the player's own name, `Ember: Welcome!`) | probe, lab-mod (herald); client (the line on screen) |
| `player_name`, `on_chat` (19) | `kiln.chat` | `Kiln.Chat` | full | probe, lab-mod (herald) |
| `request_save` (19) | `kiln.saves` | `Kiln.Saves` | full | probe |
| `teleport` (20) | `kiln.world` teleport | `Kiln.World` teleport | full | probe |
| `world_setting_get/_set/_reset/_info`, `on_world_setting_changed` (21) | `kiln.settings` | `Kiln.Settings` | full | probe, lab-mod (server-admin) |
| `player_xp_required` (22) | `kiln.progression` `xp_for_level` (cached per session, any thread) | `PlayerProgression` | full | probe |
| `player_level_cap` (22) | `kiln.progression` | `PlayerProgression` | full | probe |
| `player_level_set` (22) | `kiln.progression` SET_LEVEL + SET_EXPERIENCE | `PlayerProgression` | partial: level queued for the next server step, even in a world callback (EMF: at once there); nonzero XP is a second request after the level result (several steps later), suppressed if superseded or the session/level changed | probe (refusals), host |
| `EMF_ATTR_XP_PENDING` / `_XP` / `_XP_REQUIRED` / `_LEVEL` (22) | `kiln.progression` get / ADD_EXPERIENCE | `PlayerProgression` | partial: pending XP is queued, at most 100,000,000 per call (`kiln.progression`); reads require the server thread | probe (refusals), host |
| `change_component` (23) | kiln-emf's `emf_components` program on `kiln.world` (`queue_add` / `queue_remove`), EMF's list and queue | `Kiln.Api` ECS program `QueueAdd` / `QueueRemove` (low level) | full | probe, host |
| `window_open`, `window_update`, `window_close` (24) | `kiln.ui` ImGui window, capture, keys | `UiWindow` | partial: plain ImGui look; left/right clicks implemented; refused on a dedicated server (as EMF) | probe (server refusal), host, client |
| `spawn_offer` (25) | `kiln.spawn` + the offer below | `Offers.SpawnWithOffer` | partial: the prompt appears a step or two after the spawn | probe |
| `offer`, `offer_remove` (25) | the three offer components through `emf_components`; `InteractionAcceptedEvent` listener | `Offers.Add/TryAdd/Remove` | full | probe (components), host, client (the prompt, the accept) |
| `image_load` (26) | `kiln.ui` `image_load` (PNG frames), EMF's limits and paths; encoded files also limited to 64 MB | `UiImage` | partial: encoded-file limit in addition to EMF's decoded-pixel limit | host, client |
| `offer_active` (25/26) | kiln-emf's offer registry | `Offers.IsActive/Status` | full | probe, host |
| `net_channel` (27) | upstream EMF channel registry, caller ownership and version checks | `Network.Channel` (Kiln wire; not the EMF protocol) | full | host wire/ownership checks; server probe |
| `net_to_server` (27) | EMF wire on host loopback or the game's client chat outbox | `NetChannel.SendToServer` (Kiln wire) | full | host + production client-detour fixture; joined-client check pending |
| `net_to_player` (27) | EMF wire through `kiln.chat`, checked peer/channel | `NetChannel.SendTo(player, message)` (Kiln wire) | full | host upstream-peer interop; server probe; joined-client check pending |
| `net_to_players` (27) | EMF wire to every compatible peer | `NetChannel.SendToAll` (Kiln wire) | full | host; server probe; joined-client check pending |
| `net_peer` (27) | EMF peer/channel-version snapshot | `Network.PeerOf`, `NetPeer.HasChannel` (Kiln peers; presence only) | full | host; server probe |

## v28 to v40 (EMF 0.6.3 to 0.6.8)

What a mod gets from each function EMF added after 0.6.0. "Answer" is the value a mod sees from a function Kiln does
not carry yet. Host tests check every answer (`tests/glue_test.cpp`, `test_v40`); the probe checks them on a
dedicated server.

| EMF function (since) | On Kiln | Status | Tested |
|---|---|---|---|
| `chat_tell_ex`, `chat_broadcast_ex` (30) | `kiln.chat` server lines, which a player's game shows without a speaker's name: with `EMF_CHAT_SYSTEM` as EMF documents. Other flags are refused. | partial: flags 0 sends the same line without a name (on EMF a plain line shows under the player's own name) | probe; client (the line on screen) |
| `world_to_screen` (33) | `kiln.render`'s camera: display pixels, false behind the camera or before a frame. False on a dedicated server. | full | host; client |
| `on_player` (28) | Answer: `false`. No join, leave or death callback comes. | not yet | host, probe |
| `item_name`, `game_language` (29) | Answer: 0 and an empty buffer ("use the debug name"). | not yet | host, probe |
| `player_character_name` (30) | Answer: `false` and an empty buffer. | not yet | host, probe |
| `on_hit`, `on_hit_before`, `hit_rule_add`, `hit_rule_remove` (31) | Answer: `false` / 0. Hits are not observed or changed; fall, shroud, cold and drowning damage are not reported. | not yet | host, probe |
| `buff_types`, `buff_type`, `buff_text`, `buffs_of`, `buff_apply`, `buff_remove`, `on_buff` (32) | Answer: 0 / `false`; `done` is not called. | not yet | host, probe |
| `marker_set`, `marker_remove`, `marker_clear`, `marker_types`, `marker_send`, `marker_unsend` (33) | Answer: `false` / 0. No marker is drawn or sent. | not yet | host, probe |
| `map_cursor`, `on_map_click`, `map_action`, `ground_at` (34) | Answer: `false`; no callback. | not yet | host, probe |
| `player_view`, `fog_at`, `map_markers`, `canvas_set`, `canvas_draw` (35) | Answer: `false` / 0. No canvas is drawn. | not yet | host, probe |
| `remove_building` (36) | Answer: `false`, `refunded` 0, `why` "not available on Kiln yet". Nothing is removed. | not yet | host, probe |
| `path_find`, `path_cancel` (37) | Answer: 0 / `false`; no callback. | not yet | host, probe |
| `door_state`, `door_set`, `door_lock` (38) | Answer: `EMF_DOOR_NONE` / `false`. | not yet | host, probe |
| `enemy_order`, `enemy_order_cancel`, `enemy_target` (39) | Answer: 0 with `why` in the order / `false` / 0. | not yet | host, probe |
| `enemy_move`, `enemy_move_cancel`, `enemy_moving` (40) | Answer: 0 with `why` in the order / `false` / 0; no callback. | not yet | host, probe |

A mod that registers a callback through a function that is not carried and ignores the `false` runs without that
feature and without an error of its own: the one log line and `state/emf.json` `unsupported` are where it shows.

## Lua API (`emf.*`, run by EMF's `lua` mod on the C API)

EMF's `lua` mod is an EMF DLL mod built on the public C API only, so every Lua function works on Kiln exactly as far
as the C function under it does. Lua mods need EMF's `lua` mod in `emf/mods/lua/`. Unmodified Lua 1.19 (EMF 0.6.0,
EMF's own DLL, dev.20) and Lua 1.33 (EMF 0.6.8, built from EMF's unchanged source for a lab) load on the v40 table and
run EMF's Lua mods; their actual player-facing features still need the client checklist.
Lua 1.33 looks at the table's version and size before it uses a newer function, then at the function's answer: on
Kiln the functions of the last rows below return what EMF's Lua guide documents for a feature that is off (`nil`,
`false`, or `nil` and a reason).

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
| `emf.spawn`, `emf.destroy` | `spawn_ex`, `destroy` | full; spawn-time components supported |
| `emf.kill`, `emf.health`, `emf.attribute`, `emf.attribute_add`, `emf.damage` | v18 attributes, `damage` | partial: health, stamina and mana supported; XP/level timing and limits as C |
| `emf.xp_required`, `emf.level_cap`, `emf.set_level` | v22 | full / full / partial (queued level, then XP) |
| `emf.add_component`, `emf.remove_component` | `change_component` | full |
| `emf.tell`, `emf.broadcast` | `chat_tell`, `chat_broadcast` | partial (as C: no speaker's name) |
| `emf.on_chat`, `emf.request_save` | v19 | full |
| `emf.teleport` | `teleport` | full |
| `emf.world_settings.get/set/reset/list`, `emf.on_world_setting_changed` | v21 | full |
| `emf.wheel`, `emf.wheel_section`, `emf.wheel_group` | `wheel_open` | full; client input/drawing pending |
| `emf.window`, `emf.window_update`, `emf.window_close`, `emf.image` | v24, v26 | partial (as C) |
| `emf.offer`, `emf.spawn_offer`, `emf.offer_remove`, `emf.offered` | v25, `offer_active` | full / partial / full / full |
| `emf.net.channel(name, def)`, `.send(name, data)`, `.send_to(world, player, name, data)`, `.send_all(world, name, data)`, `.peer(world, player, name)` | v27 `net_channel`, `net_to_server`, `net_to_player`, `net_to_players`, `net_peer` | full implementation; host interop and server probe; joined-client checks pending |
| `world:is_server`, `:is_client`, `:tick`, `:entities`, `:position`, `:get`, `:template`, `:name`, `:account` | worlds, components, `player_name` | full |
| `emf.tell`, `emf.broadcast` with options (`system`) (1.22) | `chat_tell_ex`, `chat_broadcast_ex` | full with `system`; without it partial, as `emf.tell` |
| `emf.world_to_screen` | `world_to_screen` | full (client) |
| `emf.player_data`, `emf.save_player_data` | the Lua host's own files under `emf/config` | full |
| `emf.on_player_joined`, `emf.on_player_left`, `emf.on_player_died` (1.20) | `on_player` | not yet |
| `emf.item_name`, `emf.language` (1.21) | `item_name`, `game_language` | not yet (`emf.item_name` gives the debug name and `false`, `emf.language` `nil`, as on a dedicated server) |
| `emf.on_hit`, `emf.hit_rule`, `emf.hit_rule_remove` | v31 | not yet |
| `emf.buff_types`, `emf.buff_type`, `emf.buffs`, `emf.apply_buff`, `emf.remove_buff`, `emf.on_buff` | v32 | not yet |
| `emf.marker`, `emf.remove_marker`, `emf.clear_markers`, `emf.marker_types`, `emf.send_marker`, `emf.unsend_marker` | v33 | not yet |
| `emf.map_cursor`, `emf.on_map_click`, `emf.map_action`, `emf.remove_map_action`, `emf.ground_at` | v34 | not yet |
| `emf.player_view`, `emf.fog_at`, `emf.map_markers`, `emf.canvas`, `emf.canvas_draw` | v35 | not yet |
| `emf.remove_building` | v36 | not yet |
| `emf.path_find`, `emf.path_cancel` | v37 | not yet |
| `emf.door`, `emf.door_open`, `emf.door_close`, `emf.door_lock` | v38 | not yet |
| `emf.enemy_order`, `emf.enemy_order_cancel`, `emf.enemy_target`, `emf.enemy_move`, `emf.enemy_move_cancel`, `emf.enemy_moving` | v39, v40 | not yet |

## Lifetime and host checks

World teardown closes windows exactly once with `BY_CORE`, `NO_WORLD` and a null world, drops queued clicks and accepts,
and fails pending component changes with a null world. Image handles are cached for the process as EMF specifies;
failed multi-frame loads release already decoded frames, and plugin shutdown releases the cache. Offers only remove
support components they added. Native EMF DLLs remain loaded until process exit; there is no per-mod unload/reload
entry point in kiln-emf.

`native/plugins/emf/tests/run.sh` checks the frozen x64 ABI slot map, model policy/queues and the production window,
progression, offer, event and API 27 network adapters with fake Kiln services (ASan/UBSan). Network checks include
upstream-wire interoperability, fragments, ownership, sender identity, limits, teardown and production client detours
with stand-in engine calls. The world-program review also ran eight unload/reload cycles with no failed checks or
leaked program owners on its server lab. These checks do not demonstrate client rendering or player interaction.

EMF manifest portraits are prepared by an offline CPU renderer in the separate GPL `kiln-eml` component, started
when EMF DLL mods load (on a worker thread; only the thread loading the mods waits in `image_load` for a picture being drawn). The MIT adapter only plans requests and calls that export. Archive/cache checks rendered textured
wolf PNGs and reused them through the Windows DLL ABI. Automatic preparation in a real client, custom client
portraits and their appearance in Beastmaster remain unchecked. See [eml.md](eml.md#emf-creature-portraits).

## What is not in kiln-emf

- **EML content** (`emf_content.dll`, GPL-3.0 in EMF): EMF's loader runs it before the game starts. It is not part of
  the C API above. Kiln's GPL plugin `kiln-eml` covers EML content; kiln-emf stays MIT and does not load it.
- EMF's own console UI, debug bridge, save backups and profile manager are EMF's tooling, not API.
