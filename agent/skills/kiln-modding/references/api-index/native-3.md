# Native ABI rows (mod API 0.2.28): part 3 of 3

One line per row: header, `Table.row`, @offset, mod API, comment (native.md says more).

## services/kiln_stations.h — service `kiln.stations`
### KilnStationsApi — 328 bytes
- kiln_stations.h `KilnStationsApi.status` @8 `0.2.6`
- kiln_stations.h `KilnStationsApi.base_count` @16 `0.2.6` — The game's bases. base_at: index 0..count-1. base_of: the base the entity stands in (KILN_ERR_NOT_FOUND...
- kiln_stations.h `KilnStationsApi.base_at` @24 `0.2.6`
- kiln_stations.h `KilnStationsApi.base_of` @32 `0.2.6`
- kiln_stations.h `KilnStationsApi.base_at_position` @40 `0.2.6`
- kiln_stations.h `KilnStationsApi.base_storage` @48 `0.2.6` — A base's crafting-stock containers / water sources in the game's order; its stock summed per item (stock...
- kiln_stations.h `KilnStationsApi.base_water` @56 `0.2.6`
- kiln_stations.h `KilnStationsApi.base_stock` @64 `0.2.6`
- kiln_stations.h `KilnStationsApi.stations` @72 `0.2.6` — Production stations: base_index -1 = all, else those in that base.
- kiln_stations.h `KilnStationsApi.station_read` @80 `0.2.6` — KILN_ERR_NOT_FOUND: the entity has no FactoryStation.
- kiln_stations.h `KilnStationsApi.recipe` @88 `0.2.6` — Recipes: KILN_ERR_NOT_FOUND for an unknown id. recipe_inputs: the game's own resolution with choices (NULL...
- kiln_stations.h `KilnStationsApi.recipe_inputs` @96 `0.2.6`
- kiln_stations.h `KilnStationsApi.category` @104 `0.2.6` — Ingredient categories (22 in the game's data): the options of one (item, scaling); category_at walks them.
- kiln_stations.h `KilnStationsApi.category_at` @112 `0.2.6`
- kiln_stations.h `KilnStationsApi.workshop_recipes` @120 `0.2.6` — The recipes of a workshop (a station's Crafting.workshopId).
- kiln_stations.h `KilnStationsApi.plan` @128 `0.2.6` — What a FILL request would do now (no game function runs).
- kiln_stations.h `KilnStationsApi.submit` @136 `0.2.6` — Queues an action.
- kiln_stations.h `KilnStationsApi.cancel` @144 `0.2.6` — Drops an action's callback (the action may still run).
- kiln_stations.h `KilnStationsApi.watch` @152 `0.2.6` — Events are collected while any watch is on (each on() needs an off()); they cost a scan every 32 steps and...
- kiln_stations.h `KilnStationsApi.events` @160 `0.2.6` — Events with seq > after, oldest first (the last 1024 are kept).
- kiln_stations.h `KilnStationsApi.last_seq` @168 `0.2.6`
- kiln_stations.h `KilnStationsApi.set_output_source` @176 `0.2.9` — since 0.2.0-dev.29.17 (header.size tells whether these are here) ---- Station outputs as crafting stock...
- kiln_stations.h `KilnStationsApi.output_sources` @184 `0.2.9` — The opted-in stations, in the order a fill uses them (entity order).
- kiln_stations.h `KilnStationsApi.max_chests` @192 `0.2.9` — KILN_STATIONS_MAX_CHESTS.
- kiln_stations.h `KilnStationsApi.collect` @200 `0.2.9` — Moves a station's finished stacks, each through kiln.items' move (the game's slot transfer...
- kiln_stations.h `KilnStationsApi.base_stock_with_stations` @208 `0.2.9` — base_stock plus the finished outputs still in this base's production stations (Bases.StockWithStations).
- kiln_stations.h `KilnStationsApi.set_craft_stock` @216 `0.2.9` — A Kiln client's snapshot of the crafting stock its crafting menu may count: per item, the player's bag...
- kiln_stations.h `KilnStationsApi.craft_items` @224 `0.2.9` — The items a Kiln client must have counted to raise a recipe's count with station outputs: every input (an...
- kiln_stations.h `KilnStationsApi.set_fill_answers` @232 `0.2.11` — the station screen's fill buttons for Kiln clients, GitHub #68 (since 0.2.0-dev.29.19; header.size tells)...
- kiln_stations.h `KilnStationsApi.fill_asks` @240 `0.2.11` — The fills the station screen asked the game about while the snapshot was on, that the game refused for...
- kiln_stations.h `KilnStationsApi.add_source` @248 `0.2.15` — player crafting sources, since 0.2.0-dev.30.6; capability crafting.sources ---- Tick thread, server world.
- kiln_stations.h `KilnStationsApi.remove_source` @256 `0.2.15` — Any thread.
- kiln_stations.h `KilnStationsApi.cancel_sources` @264 `0.2.15` — Any thread: remove this owner's registrations (also called by kiln.items.cancel_owner).
- kiln_stations.h `KilnStationsApi.source_stock` @272 `0.2.15` — Tick thread: the registered sources' filtered extra counts, beyond the bag, current base stock and...
- kiln_stations.h `KilnStationsApi.set_dye_stock` @280 `0.2.15` — Since 0.2.0-dev.30.6: a Kiln server's supported dye-source announcement for the local player.
- kiln_stations.h `KilnStationsApi.set_lock_stock` @288 `0.2.21` — locks from linked sources, since 0.2.0-dev.31.12 (GitHub #147; capability items.lock_sources; header.size...
- kiln_stations.h `KilnStationsApi.lock_asks` @296 `0.2.21` — A Kiln client: the lock items its lock checks (the press and the locked prompt) met while the snapshot was...
- kiln_stations.h `KilnStationsApi.set_build_stock` @304 `0.2.21` — block placements from linked sources, since 0.2.0-dev.31.12 (GitHub #132; capability...
- kiln_stations.h `KilnStationsApi.fill_flags` @312 `0.2.23` — since 0.2.0-dev.31.16 (GitHub #132; header.size tells whether this is here) ---- The KILN_STATION_FILL_*...
- kiln_stations.h `KilnStationsApi.set_respec_stock` @320 `0.2.25` — the skill tree's respec from linked sources, since 0.2.0-dev.31.18 (GitHub #150; capability...

## services/kiln_stations_client.h — service `kiln.stations.client`
### KilnStationsClientApi — 32 bytes
- kiln_stations_client.h `KilnStationsClientApi.status` @8 `0.2.22`
- kiln_stations_client.h `KilnStationsClientApi.stations` @16 `0.2.22`
- kiln_stations_client.h `KilnStationsClientApi.station` @24 `0.2.22`

## services/kiln_steamguard.h — service `kiln.steamguard`
### KilnSteamGuardApi — 40 bytes
- kiln_steamguard.h `KilnSteamGuardApi.status` @8 `0.2.1` — The guard's state.
- kiln_steamguard.h `KilnSteamGuardApi.latch` @16 `0.2.1` — Turns the guard on until the game exits (the first call puts the detour in).
- kiln_steamguard.h `KilnSteamGuardApi.hold` @24 `0.2.1` — Turns the guard on while the returned token is held (the first call puts the detour in).
- kiln_steamguard.h `KilnSteamGuardApi.release` @32 `0.2.1` — Releases one hold.

## services/kiln_surfaces.h — service `kiln.surfaces`
### KilnSurfacesApi — 24 bytes
- kiln_surfaces.h `KilnSurfacesApi.state` @8
- kiln_surfaces.h `KilnSurfacesApi.status_json` @16 — JSON: state, each surface with its slot and status, errors, warnings, the streamed-texture count.

## services/kiln_survivors.h — service `kiln.survivors`
### KilnSurvivorsApi — 96 bytes
- kiln_survivors.h `KilnSurvivorsApi.status` @8 `0.2.17` — any thread
- kiln_survivors.h `KilnSurvivorsApi.count` @16 `0.2.17` — Roster entries (count(): 0 before the world's catalog is built).
- kiln_survivors.h `KilnSurvivorsApi.at` @24 `0.2.17`
- kiln_survivors.h `KilnSurvivorsApi.get` @32 `0.2.17` — By NpcId.
- kiln_survivors.h `KilnSurvivorsApi.of_entity` @40 `0.2.17` — The NpcId of a live NPC entity.
- kiln_survivors.h `KilnSurvivorsApi.bed_occupant` @48 `0.2.17` — The NpcId a bed is assigned to (the bed's side), 0 when free.
- kiln_survivors.h `KilnSurvivorsApi.base_at` @56 `0.2.17` — The base whose build zone holds 'position': its id (for SUMMON) and its altar; *abandoned 1 when abandoned.
- kiln_survivors.h `KilnSurvivorsApi.submit` @64 `0.2.17` — KILN_ERR_UNAVAILABLE: the operation's capability is off (status() says why).
- kiln_survivors.h `KilnSurvivorsApi.result` @72 `0.2.17` — KILN_ERR_NOT_FOUND while it runs, and after it was forgotten (10 minutes, the last 256).
- kiln_survivors.h `KilnSurvivorsApi.progress` @80 `0.2.17`
- kiln_survivors.h `KilnSurvivorsApi.events` @88 `0.2.17` — Events with sequence > after, oldest first; *out_count written.

## services/kiln_tasks.h — service `kiln.tasks`
### KilnTasksApi — 56 bytes
- kiln_tasks.h `KilnTasksApi.submit` @8 `0.2.10` — Accept a task for 'owner': body and done both required.
- kiln_tasks.h `KilnTasksApi.cancel` @16 `0.2.10` — Queued (or AFTER_STEP waiting): CANCELLED now, its done delivered on a worker (never inline on this thread).
- kiln_tasks.h `KilnTasksApi.status` @24 `0.2.10` — *state = KILN_TASK_*; NOT_FOUND (state NONE) for an unknown, foreign or released handle.
- kiln_tasks.h `KilnTasksApi.release` @32 `0.2.10` — Terminal only: KILN_OK and the record (and its capacity slot) is gone; KILN_OK again for a handle this...
- kiln_tasks.h `KilnTasksApi.drain_owner` @40 `0.2.10` — Seal the owner FOREVER (no new submissions), cancel its queued tasks (each still gets its done), signal...
- kiln_tasks.h `KilnTasksApi.submit_in` @48 `0.2.10` — submit, bound to 'world''s 'session' (see "World-bound submission" above).

## services/kiln_time.h — service `kiln.time`
### KilnTimeApi — 96 bytes
- kiln_time.h `KilnTimeApi.get` @8 — Current state.
- kiln_time.h `KilnTimeApi.set_time_of_day` @16 — Sets the time of day (hours, wrapped into 0..24).
- kiln_time.h `KilnTimeApi.reset_weather` @24 — Ends every weather phenomenon and makes each weather layer pick a new one on the server's next weather...
- kiln_time.h `KilnTimeApi.set_time_scale` @32 `0.2.0` — Later additions: check header.size before calling.
- kiln_time.h `KilnTimeApi.time_scale_hold` @40 `0.2.0` — The speed Kiln holds; -1 when the game decides; -2 when this build cannot hold it.
- kiln_time.h `KilnTimeApi.weather_table` @48 `0.2.2` — The game's phenomenon table, 'capacity' records of 'item_size' bytes (at least the v2 size; the stride)....
- kiln_time.h `KilnTimeApi.weather_layers` @56 `0.2.2` — Every layer's state, as weather_table writes records.
- kiln_time.h `KilnTimeApi.weather_set` @64 `0.2.2` — Queues a choice or a release for one layer; it runs in the server's next weather update (the same frame...
- kiln_time.h `KilnTimeApi.weather_room` @72 `0.2.2` — How many more requests weather_set takes now (0..256).
- kiln_time.h `KilnTimeApi.weather_change_seq` @80 `0.2.2` — The seq of the newest KilnTimeWeatherChange (0: none yet).
- kiln_time.h `KilnTimeApi.weather_changes` @88 `0.2.2` — The changes after 'after_seq', oldest first, 'capacity' records of 'item_size' bytes (at least...

## services/kiln_tools.h — service `kiln.tools`
### KilnToolsApi — 64 bytes
- kiln_tools.h `KilnToolsApi.status` @8 `0.2.14` — Any thread.
- kiln_tools.h `KilnToolsApi.register_action` @16 `0.2.14` — Reserved ABI slot.
- kiln_tools.h `KilnToolsApi.unregister_action` @24 `0.2.14` — Reserved ABI slot.
- kiln_tools.h `KilnToolsApi.define_family` @32 `0.2.14` — Tick thread/before first tick.
- kiln_tools.h `KilnToolsApi.remove_family` @40 `0.2.14` — Tick thread/before first tick.
- kiln_tools.h `KilnToolsApi.material_snapshot` @48 `0.2.14` — Any thread; observation only, never payment/edit authority.
- kiln_tools.h `KilnToolsApi.presentation_status` @56 `0.2.14` — Any thread.

## services/kiln_toolwear.h — service `kiln.toolwear`
### KilnToolWearApi — 48 bytes
- kiln_toolwear.h `KilnToolWearApi.define` @8 `0.2.3` — A rule of 'owner' for 'item': every qualifying debit costs 'per_debit' (0..32767).
- kiln_toolwear.h `KilnToolWearApi.remove` @16 `0.2.3` — Removes 'caller''s rule.
- kiln_toolwear.h `KilnToolWearApi.changes` @24 `0.2.3` — Drains up to 'capacity' change records of 'stride' bytes each into 'out' (the smaller of stride and the...
- kiln_toolwear.h `KilnToolWearApi.status` @32 `0.2.3` — The state for 'item' (0: only the capability's and the counters).
- kiln_toolwear.h `KilnToolWearApi.rule_changes` @40 `0.2.3` — appended in 0.2.0-dev.28 (check header.size before calling) ---- The rule changes after generation...

## services/kiln_types.h — service `kiln.types`
### KilnTypesApi — 216 bytes
- kiln_types.h `KilnTypesApi.status` @8 `0.2.0`
- kiln_types.h `KilnTypesApi.type_count` @16 `0.2.0`
- kiln_types.h `KilnTypesApi.type_at` @24 `0.2.0`
- kiln_types.h `KilnTypesApi.type_by_hash` @32 `0.2.0`
- kiln_types.h `KilnTypesApi.type_by_name` @40 `0.2.0` — "keen::ecs::Health" exactly; a name without "::" is tried as "keen::ecs::\<name>", then as the only type...
- kiln_types.h `KilnTypesApi.type_info` @48 `0.2.0`
- kiln_types.h `KilnTypesApi.type_field` @56 `0.2.0` — Own field i (0-based, \< field_count) of a struct.
- kiln_types.h `KilnTypesApi.type_enum` @64 `0.2.0` — Value i of an enum or bitmask.
- kiln_types.h `KilnTypesApi.component_type_count` @72 `0.2.0` — The game's lists of ECS component types and event types (687 / 132 on svn 1287369).
- kiln_types.h `KilnTypesApi.component_type_at` @80 `0.2.0`
- kiln_types.h `KilnTypesApi.event_type_count` @88 `0.2.0`
- kiln_types.h `KilnTypesApi.event_type_at` @96 `0.2.0`
- kiln_types.h `KilnTypesApi.system_count` @104 `0.2.0`
- kiln_types.h `KilnTypesApi.system_info` @112 `0.2.0`
- kiln_types.h `KilnTypesApi.system_find` @120 `0.2.0` — index, or -1
- kiln_types.h `KilnTypesApi.system_set_enabled` @128 `0.2.0` — Only stamina_depletion_sprint and fall_damage_infliction may be disabled (unique update functions only)....
- kiln_types.h `KilnTypesApi.profile` @136 `0.2.0` — On: starts timing from zero.
- kiln_types.h `KilnTypesApi.event_each` @144 `0.2.0` — Every event of type 'hash' the world produced in the step that just ended.
- kiln_types.h `KilnTypesApi.event_post` @152 `0.2.0` — Queues exactly the registered type's bytes; inline scalars/structs/static arrays/GUIDs/references only...
- kiln_types.h `KilnTypesApi.system_release_owner` @160 `0.2.0` — Appended in v1: release all of this owner's disable requests (tick thread).
- kiln_types.h `KilnTypesApi.system_hook` @168 `0.2.0` — Appended in v1: before/after the named system's update (one pair per slice).
- kiln_types.h `KilnTypesApi.system_unhook` @176 `0.2.0` — Any thread outside a system-hook callback.
- kiln_types.h `KilnTypesApi.external_runner` @184 `0.2.3` — Appended in 0.2.0-dev.28: whether another runtime owns the program runner, so that a program can no longer...
- kiln_types.h `KilnTypesApi.type_impact_name` @192 `0.2.3` — Since 0.2.0-dev.28 (check header.size): the name Impact scripts use for 'type' ("ecs.Health" for...
- kiln_types.h `KilnTypesApi.in_system_callback` @200 `0.2.3` — Since 0.2.0-dev.28 (check header.size): 1 while the calling thread runs a system_hook callback (before or...
- kiln_types.h `KilnTypesApi.ecs_registry` @208 `0.2.10` — Appended to v1 (check header.size): the engine's component registration catalog for 'world_kind'...

## services/kiln_ui.h — service `kiln.ui`, `kiln.ui.tick`, `kiln.craftmenu`
### KilnUiApi — 1096 bytes
- kiln_ui.h `KilnUiApi.status` @8
- kiln_ui.h `KilnUiApi.subscribe_frame` @16
- kiln_ui.h `KilnUiApi.subscribe_key` @24
- kiln_ui.h `KilnUiApi.unsubscribe` @32
- kiln_ui.h `KilnUiApi.set_demand` @40 — Things to draw (HUD lines, windows, toasts).
- kiln_ui.h `KilnUiApi.set_capture` @48 — Interactive Kiln windows open: > 0 = Kiln takes mouse and keyboard (as when the Kiln menu is open).
- kiln_ui.h `KilnUiApi.set_menu` @56 — The Kiln menu: -1 toggle, 0 close, 1 open.
- kiln_ui.h `KilnUiApi.set_menu_key` @64
- kiln_ui.h `KilnUiApi.key_name` @72 — Name of a virtual key ("F8", "Insert", "A", "Mouse4", "MouseMiddle"; "WheelUp" for KILN_UI_VK_WHEEL_UP...
- kiln_ui.h `KilnUiApi.begin` @80
- kiln_ui.h `KilnUiApi.end` @88
- kiln_ui.h `KilnUiApi.text` @96
- kiln_ui.h `KilnUiApi.text_colored` @104
- kiln_ui.h `KilnUiApi.text_wrapped` @112
- kiln_ui.h `KilnUiApi.text_disabled` @120
- kiln_ui.h `KilnUiApi.button` @128
- kiln_ui.h `KilnUiApi.small_button` @136
- kiln_ui.h `KilnUiApi.checkbox` @144
- kiln_ui.h `KilnUiApi.slider_float` @152
- kiln_ui.h `KilnUiApi.slider_int` @160
- kiln_ui.h `KilnUiApi.input_text` @168 — 'buffer' holds NUL-terminated UTF-8 and is edited in place.
- kiln_ui.h `KilnUiApi.collapsing_header` @176
- kiln_ui.h `KilnUiApi.tree_node` @184
- kiln_ui.h `KilnUiApi.tree_pop` @192
- kiln_ui.h `KilnUiApi.selectable` @200
- kiln_ui.h `KilnUiApi.separator` @208
- kiln_ui.h `KilnUiApi.separator_text` @216
- kiln_ui.h `KilnUiApi.same_line` @224
- kiln_ui.h `KilnUiApi.spacing` @232
- kiln_ui.h `KilnUiApi.push_id` @240
- kiln_ui.h `KilnUiApi.pop_id` @248
- kiln_ui.h `KilnUiApi.set_next_window_pos` @256
- kiln_ui.h `KilnUiApi.set_next_window_size` @264
- kiln_ui.h `KilnUiApi.set_next_window_bg_alpha` @272
- kiln_ui.h `KilnUiApi.progress_bar` @280
- kiln_ui.h `KilnUiApi.begin_table` @288
- kiln_ui.h `KilnUiApi.table_setup_column` @296
- kiln_ui.h `KilnUiApi.table_headers_row` @304
- kiln_ui.h `KilnUiApi.table_next_row` @312
- kiln_ui.h `KilnUiApi.table_next_column` @320
- kiln_ui.h `KilnUiApi.end_table` @328
- kiln_ui.h `KilnUiApi.begin_tab_bar` @336
- kiln_ui.h `KilnUiApi.begin_tab_item` @344
- kiln_ui.h `KilnUiApi.end_tab_item` @352
- kiln_ui.h `KilnUiApi.end_tab_bar` @360
- kiln_ui.h `KilnUiApi.begin_child` @368
- kiln_ui.h `KilnUiApi.end_child` @376
- kiln_ui.h `KilnUiApi.is_item_hovered` @384
- kiln_ui.h `KilnUiApi.set_tooltip` @392
- kiln_ui.h `KilnUiApi.draw_text` @400 — Foreground (HUD) drawing in pixels; size 0 = the default font size.
- kiln_ui.h `KilnUiApi.draw_rect` @408
- kiln_ui.h `KilnUiApi.text_size` @416
- kiln_ui.h `KilnUiApi.take_pressed_key` @424 — The next key pressed (for "press a key" remapping): returns its vk once, 0 while none.
- kiln_ui.h `KilnUiApi.screen` @432 — screen: the state the game's UI built in its last frame (KilnUiScreen; set out->size first).
- kiln_ui.h `KilnUiApi.subscribe_screen` @440
- kiln_ui.h `KilnUiApi.subscribe_ui_tick` @448
- kiln_ui.h `KilnUiApi.menu_add` @456 — menu_add: an entry drawn by the game's own menu-entry renderer (its look, sounds, focus and...
- kiln_ui.h `KilnUiApi.menu_set` @464
- kiln_ui.h `KilnUiApi.push_style` @472 — push_style: KILN_UI_STYLE_GAME (the game's colours and, once loaded, the game's own UI font read from its...
- kiln_ui.h `KilnUiApi.pop_style` @480
- kiln_ui.h `KilnUiApi.image_load` @488 — image_load: decodes a PNG (bytes, copied) into a texture Kiln uploads at the next frame.
- kiln_ui.h `KilnUiApi.image_size` @496
- kiln_ui.h `KilnUiApi.image` @504
- kiln_ui.h `KilnUiApi.image_button` @512
- kiln_ui.h `KilnUiApi.image_free` @520
- kiln_ui.h `KilnUiApi.set_next_window_focus` @528 — set_next_window_focus: focus the next window begun (a screen that opens takes the keyboard). combo: a...
- kiln_ui.h `KilnUiApi.combo` @536
- kiln_ui.h `KilnUiApi.input_multiline` @544
- kiln_ui.h `KilnUiApi.dummy` @552
- kiln_ui.h `KilnUiApi.window_rect` @560
- kiln_ui.h `KilnUiApi.set_hover_capture` @568 — set_hover_capture: Kiln panels that sit next to a game screen (e.g. a crafting info panel) take the mouse...
- kiln_ui.h `KilnUiApi.subscribe_pre_overlay` @576 — full-screen work at present, under the overlay ---- subscribe_pre_overlay: fn runs at each present of the...
- kiln_ui.h `KilnUiApi.set_pre_overlay_demand` @584
- kiln_ui.h `KilnUiApi.add_swapchain_usage` @592
- kiln_ui.h `KilnUiApi.is_item_clicked` @600 `0.2.0` — is_item_clicked: true on the frame the mouse 'button' (KILN_UI_MOUSE_*) went down over the item drawn last...
- kiln_ui.h `KilnUiApi.wheel_open` @608 `0.2.0` — One wheel at a time, drawn in the middle of the screen over everything (windows below it get no input).
- kiln_ui.h `KilnUiApi.wheel_close` @616 `0.2.0`
- kiln_ui.h `KilnUiApi.wheel_active` @624 `0.2.0`
- kiln_ui.h `KilnUiApi.push_font_size` @632 `0.2.0` — push_font_size: text drawn until pop_font_size uses this size, in pixels at UI scale 1 (the UI scale is...
- kiln_ui.h `KilnUiApi.pop_font_size` @640 `0.2.0`
- kiln_ui.h `KilnUiApi.begin_child_ex` @648 `0.2.0` — begin_child_ex: a child region of width x height (0 = the rest, \< 0 = the rest minus that much) with...
- kiln_ui.h `KilnUiApi.set_next_item_width` @656 `0.2.0` — set_next_item_width: the width of the next widget (> 0 pixels; \< 0 = the rest of the line minus that much).
- kiln_ui.h `KilnUiApi.cursor` @664 `0.2.0` — cursor: where the next widget goes and the space left (KilnUiCursor; set out->size first). set_cursor_pos...
- kiln_ui.h `KilnUiApi.set_cursor_pos` @672 `0.2.0`
- kiln_ui.h `KilnUiApi.item` @680 `0.2.0` — item: the rectangle and state of the widget drawn last (KilnUiItem; set out->size first).
- kiln_ui.h `KilnUiApi.selectable_ex` @688 `0.2.0` — selectable_ex: a selectable row of width x height (0 = the rest of the line / one text line) with...
- kiln_ui.h `KilnUiApi.set_keyboard_focus_here` @696 `0.2.0` — set_keyboard_focus_here: the keyboard goes to the next widget (offset 0) or the one after (1)... use it on...
- kiln_ui.h `KilnUiApi.set_item_default_focus` @704 `0.2.0`
- kiln_ui.h `KilnUiApi.draw_rect_in` @712 `0.2.0` — draw_rect_in / draw_text_in: like draw_rect / draw_text on a KILN_UI_LAYER_*: WINDOW = the current...
- kiln_ui.h `KilnUiApi.draw_text_in` @720 `0.2.0`
- kiln_ui.h `KilnUiApi.text_size_ex` @728 `0.2.0` — text_size_ex: like text_size, wrapped at wrap_width when > 0.
- kiln_ui.h `KilnUiApi.push_color` @736 `0.2.0` — push_color: a colour of the current look (KILN_UI_COLOR_*) until pop_color(count) takes 'count' pushes...
- kiln_ui.h `KilnUiApi.pop_color` @744 `0.2.0`
- kiln_ui.h `KilnUiApi.push_var` @752 `0.2.0`
- kiln_ui.h `KilnUiApi.pop_var` @760 `0.2.0`
- kiln_ui.h `KilnUiApi.key_pressed` @768 `0.2.0` — key_pressed: KILN_UI_KEY_* went down this frame (keyboard or gamepad; only while Kiln has the input).
- kiln_ui.h `KilnUiApi.key_mods` @776 `0.2.0`
- kiln_ui.h `KilnUiApi.set_scroll_here_y` @784 `0.2.0` — set_scroll_here_y: scrolls the current window so the cursor sits at ratio (0 top, 0.5 middle, 1 bottom).
- kiln_ui.h `KilnUiApi.menu_page` @792 `0.2.0` — menu_page: the main-menu page's state (KilnUiMenuPage; set out->size first).
- kiln_ui.h `KilnUiApi.menu_page_drawn` @800 `0.2.0`
- kiln_ui.h `KilnUiApi.menu_page_leave` @808 `0.2.0`
- kiln_ui.h `KilnUiApi.menu_page_host` @816 `0.2.0`
- kiln_ui.h `KilnUiApi.push_font` @824 `0.2.0` — push_font: text drawn until pop_font uses a KILN_UI_FONT_* font at the current size.
- kiln_ui.h `KilnUiApi.pop_font` @832 `0.2.0`
- kiln_ui.h `KilnUiApi.subscribe_window_input` @840 `0.2.0` — subscribe_window_input: fn(record) for the game window's own input records, for a native plugin that keeps...
- kiln_ui.h `KilnUiApi.menu_shown` @848 `0.2.2` — menu_shown: KILN_UI_MENU_SHOW_* (Kiln.Ui sets it from Kiln's setting "modEntries" in...
- kiln_ui.h `KilnUiApi.menu_entries` @856 `0.2.2`
- kiln_ui.h `KilnUiApi.menu_activate` @864 `0.2.2`
- kiln_ui.h `KilnUiApi.menu_scroll` @872 `0.2.2` — menu_scroll: KILN_UI_SCROLL_* (any thread).
- kiln_ui.h `KilnUiApi.subscribe_key_ex` @880 `0.2.2` — v7 (0.2.0-dev.27; check header.size >= offsetof(KilnUiApi, subscribe_key_ex) + sizeof(void*)) ----...
- kiln_ui.h `KilnUiApi.set_next_window_size_constraints` @888 `0.2.4` — Additive size-guarded tail: inside a frame callback, constrain the next window in screen pixels.
- kiln_ui.h `KilnUiApi.menu_footer` @896 `0.2.5` — Additive size-guarded tail (0.2.0-dev.29.11; check header.size through this member and its non-null...
- kiln_ui.h `KilnUiApi.image_load_rgba` @904 `0.2.7` — Additive size-guarded tail (0.2.0-dev.29.15; check header.size >= offsetof(KilnUiApi, image_load_rgba) +...
- kiln_ui.h `KilnUiApi.screen_target` @912 `0.2.7` — Additive size-guarded tail (0.2.0-dev.29.15; check header.size >= offsetof(KilnUiApi, screen_target) +...
- kiln_ui.h `KilnUiApi.image_state` @920 `0.2.7` — Additive size-guarded tail (0.2.0-dev.29.15; check header.size >= offsetof(KilnUiApi, image_state) +...
- kiln_ui.h `KilnUiApi.theme_create` @928 `0.2.8` — Additive size-guarded tail (0.2.0-dev.29.16; check header.size >= offsetof(KilnUiApi, x) + sizeof(void*)...
- kiln_ui.h `KilnUiApi.theme_free` @936 `0.2.8`
- kiln_ui.h `KilnUiApi.push_theme` @944 `0.2.8`
- kiln_ui.h `KilnUiApi.set_look` @952 `0.2.8`
- kiln_ui.h `KilnUiApi.look_info` @960 `0.2.8`
- kiln_ui.h `KilnUiApi.font_load` @968 `0.2.8`
- kiln_ui.h `KilnUiApi.font_free` @976 `0.2.8`
- kiln_ui.h `KilnUiApi.push_font_handle` @984 `0.2.8`
- kiln_ui.h `KilnUiApi.image_ex` @992 `0.2.8` — Additive size-guarded tail (0.2.0-dev.29.16; check header.size >= offsetof(KilnUiApi, x) + sizeof(void*)...
- kiln_ui.h `KilnUiApi.image_opaque_rect` @1000 `0.2.8`
- kiln_ui.h `KilnUiApi.set_texts` @1008 `0.2.8` — Additive size-guarded tail (0.2.0-dev.29.16; check header.size >= offsetof(KilnUiApi, set_texts) +...
- kiln_ui.h `KilnUiApi.graphics_set` @1016 `0.2.10` — Additive size-guarded tail (0.2.0-dev.29.18; check header.size >= offsetof(KilnUiApi, x) + sizeof(void*)...
- kiln_ui.h `KilnUiApi.graphics_status` @1024 `0.2.10`
- kiln_ui.h `KilnUiApi.chat_completion` @1032 `0.2.11` — Appended internal-only slot (issue #44).
- kiln_ui.h `KilnUiApi.subscribe_pointer_press` @1040 `0.2.12` — Additive size-guarded tail (0.2.0-dev.30; check header.size >= offsetof(KilnUiApi...
- kiln_ui.h `KilnUiApi.chat_search` @1048 `0.2.13` — Phase 1c: append-only internal Kiln.Ui slot.
- kiln_ui.h `KilnUiApi.pointer` @1056 `0.2.13` — Additive size-guarded tail (0.2.0-dev.30.3; check header.size >= offsetof(KilnUiApi, x) + sizeof(void*)...
- kiln_ui.h `KilnUiApi.drag_source` @1064 `0.2.13`
- kiln_ui.h `KilnUiApi.drop_target` @1072 `0.2.13`
- kiln_ui.h `KilnUiApi.popups` @1080 `0.2.19` — Additive size-guarded tail (0.2.0-dev.31.9; check header.size >= offsetof(KilnUiApi, popups) +...
- kiln_ui.h `KilnUiApi.window_state` @1088 `0.2.23` — Additive size-guarded tail (0.2.0-dev.31.16, GitHub issue 149; check header.size >= offsetof(KilnUiApi...
### KilnUiTickApi — 32 bytes
- kiln_ui.h `KilnUiTickApi.subscribe` @8
- kiln_ui.h `KilnUiTickApi.unsubscribe` @16
- kiln_ui.h `KilnUiTickApi.in_menu` @24 — true while no world steps (boot, main menu, loading) and the UI frames still run
### KilnCraftMenuApi — 48 bytes
- kiln_ui.h `KilnCraftMenuApi.subscribe` @8
- kiln_ui.h `KilnCraftMenuApi.set_add` @16
- kiln_ui.h `KilnCraftMenuApi.tab_add` @24
- kiln_ui.h `KilnCraftMenuApi.remove` @32 — removes a set / tab / subscription
- kiln_ui.h `KilnCraftMenuApi.describe` @40 — JSON: per workshop the tabs and subcategories (labels as tag ids, recipe counts) and Kiln's additions

## services/kiln_ui_ammo.h — service `kiln.ui.ammo`
### KilnUiAmmoApi — 32 bytes
- kiln_ui_ammo.h `KilnUiAmmoApi.publish` @8 `0.2.15` — Client main/UI thread.
- kiln_ui_ammo.h `KilnUiAmmoApi.context` @16 `0.2.15` — Any thread, owned copy, set out->size.
- kiln_ui_ammo.h `KilnUiAmmoApi.take_picks` @24 `0.2.15` — Client main/UI thread.

## services/kiln_ui_blocks.h — service `kiln.ui.blocks`
### KilnUiBlocksApi — 40 bytes
- kiln_ui_blocks.h `KilnUiBlocksApi.begin_tab_item_ex` @8 `0.2.17`
- kiln_ui_blocks.h `KilnUiBlocksApi.set_next_item_open` @16 `0.2.17`
- kiln_ui_blocks.h `KilnUiBlocksApi.item_state` @24 `0.2.17`
- kiln_ui_blocks.h `KilnUiBlocksApi.input_text_ex` @32 `0.2.17`

## services/kiln_ui_chest.h — service `kiln.ui.chest`
### KilnUiChestApi — 24 bytes
- kiln_ui_chest.h `KilnUiChestApi.subscribe` @8 `0.2.17` — OK+out.ok=0: budget (REGISTRATION_BUDGET/TEXT_BUDGET) or duplicate caption (REJECTED).
- kiln_ui_chest.h `KilnUiChestApi.unsubscribe` @16 `0.2.17`

## services/kiln_ui_input.h — service `kiln.ui.input`
### KilnUiInputApi — 48 bytes
- kiln_ui_input.h `KilnUiInputApi.subscribe_owned` @8 `0.2.22` — fn(input) for every new press of 'sources' (KILN_UI_OWN_*), and the repeats, release and end of the...
- kiln_ui_input.h `KilnUiInputApi.unsubscribe` @16 `0.2.22` — Ends a subscription (KILN_OK: a call in flight has ended; from inside a kiln.ui callback it may answer...
- kiln_ui_input.h `KilnUiInputApi.disown` @24 `0.2.22` — Gives up the subscription's press of 'vk' on 'controller' (vk 0: every press it owns) without a callback...
- kiln_ui_input.h `KilnUiInputApi.owns` @32 `0.2.22` — The subscription owns the press of 'vk' on 'controller' now.
- kiln_ui_input.h `KilnUiInputApi.pad_state` @40 `0.2.22` — Controller 0..3 as the game's last poll read it (above; set out->size first).

## services/kiln_ui_inventory.h — service `kiln.ui.inventory`
### KilnUiInventoryApi — 48 bytes
- kiln_ui_inventory.h `KilnUiInventoryApi.subscribe_clicks` @8 `0.2.12` — fn(click) for presses matching filter.
- kiln_ui_inventory.h `KilnUiInventoryApi.slot_at` @16 `0.2.12` — The slot at (x, y) in a target of target_width x target_height pixels (the window's client area or the...
- kiln_ui_inventory.h `KilnUiInventoryApi.mapping` @24 `0.2.12` — The latest frame's units-to-pixels mapping for a target of that size.
- kiln_ui_inventory.h `KilnUiInventoryApi.unsubscribe` @32 `0.2.12` — Removes a click subscription (any kiln.ui.values handle).
- kiln_ui_inventory.h `KilnUiInventoryApi.screen_view` @40 `0.2.12` — v2 (size-guarded): the inventory screen of the latest frame for a target of that size (the overlay's...

## services/kiln_ui_item_detail.h — service `kiln.ui.item_detail`
### KilnUiItemDetailApi — 16 bytes
- kiln_ui_item_detail.h `KilnUiItemDetailApi.copy` @8 `0.2.14` — Any thread.

## services/kiln_ui_slots.h — service `kiln.ui.slots`
### KilnUiSlotsApi — 16 bytes
- kiln_ui_slots.h `KilnUiSlotsApi.subscribe` @8 `0.2.14` — record_size must be sizeof(KilnUiVisibleSlotV2) = 120.

## services/kiln_ui_snapshot.h — service `kiln.ui.snapshot`
### KilnUiSnapshotApi — 48 bytes
- kiln_ui_snapshot.h `KilnUiSnapshotApi.demand_add` @8 `0.2.17`
- kiln_ui_snapshot.h `KilnUiSnapshotApi.demand_remove` @16 `0.2.17`
- kiln_ui_snapshot.h `KilnUiSnapshotApi.acquire` @24 `0.2.17`
- kiln_ui_snapshot.h `KilnUiSnapshotApi.copy` @32 `0.2.17`
- kiln_ui_snapshot.h `KilnUiSnapshotApi.release` @40 `0.2.17`

## services/kiln_ui_tints.h — service `kiln.ui.tints`
### KilnUiTintsApi — 24 bytes
- kiln_ui_tints.h `KilnUiTintsApi.subscribe` @8 `0.2.17` — item_filter=0: all slots.
- kiln_ui_tints.h `KilnUiTintsApi.unsubscribe` @16 `0.2.17`

## services/kiln_ui_values.h — service `kiln.ui.values`
### KilnUiValuesApi — 152 bytes
- kiln_ui_values.h `KilnUiValuesApi.subscribe` @8 `0.2.3` — A value provider of 'kind' (KilnUiValueKind) for one item id or 0.
- kiln_ui_values.h `KilnUiValuesApi.tooltip_lines` @16 `0.2.3` — A tooltip-lines provider (Kiln's details window).
- kiln_ui_values.h `KilnUiValuesApi.unsubscribe` @24 `0.2.3` — Removes any registration of this service (KILN_ERR_NOT_FOUND when unknown or already gone).
- kiln_ui_values.h `KilnUiValuesApi.copy_slots` @32 `0.2.3` — One completed frame of drawn slots (ui.slots.inspect): (0, 0) = the latest, else exactly one of the last...
- kiln_ui_values.h `KilnUiValuesApi.subscribe_slots` @40 `0.2.3` — fn(frame, slots, count) once per completed UI frame, on the UI thread after the frame; borrowed for the call.
- kiln_ui_values.h `KilnUiValuesApi.subscribe_changes` @48 `0.2.3`
- kiln_ui_values.h `KilnUiValuesApi.open_tooltip_details` @56 `0.2.3` — Opens the shared details window on the last completed detail snapshot (frozen until refresh or close)...
- kiln_ui_values.h `KilnUiValuesApi.close_tooltip_details` @64 `0.2.3`
- kiln_ui_values.h `KilnUiValuesApi.tooltip_details` @72 `0.2.3` — tooltip_details: which 0 = the window's frozen snapshot (ok 0, KUI_NO_TOOLTIP while it is closed), 1 = the...
- kiln_ui_values.h `KilnUiValuesApi.refresh_tooltip_details` @80 `0.2.3`
- kiln_ui_values.h `KilnUiValuesApi.copy_gemforge_candidates` @88 `0.2.5` — Same two-frame retry convention as copy_slots.
- kiln_ui_values.h `KilnUiValuesApi.subscribe_gemforge_candidates` @96 `0.2.5` — UI thread, after each completed frame, including unavailable frames; borrowed only for the callback.
- kiln_ui_values.h `KilnUiValuesApi.gemforge_sockets` @104 `0.2.5` — Sockets on other gear in the Gem Forge: widens the WEAPONS list and the GEMS list's carrier rows (gear...
- kiln_ui_values.h `KilnUiValuesApi.gemforge_sources` @112 `0.2.7` — appended in 0.2.0-dev.29.15: ui.gemforge.sources, client only.
- kiln_ui_values.h `KilnUiValuesApi.gemforge_sources_state` @120 `0.2.7` — The feature's state for logs and the owner's managed side (which materials to ask the server about).
- kiln_ui_values.h `KilnUiValuesApi.tooltip_lines_native` @128 `0.2.12` — appended in 0.2.0-dev.29.19 (equipment wave): native tooltip lines and values.
- kiln_ui_values.h `KilnUiValuesApi.tooltip_values` @136 `0.2.12`
- kiln_ui_values.h `KilnUiValuesApi.tooltip_native_state` @144 `0.2.12`

## services/kiln_vfx.h — service `kiln.vfx`
### KilnVfxApi — 136 bytes
- kiln_vfx.h `KilnVfxApi.status` @8
- kiln_vfx.h `KilnVfxApi.play` @16 — 0: unavailable, bad arguments or too many live
- kiln_vfx.h `KilnVfxApi.stop` @24 — immediate 0: the game's stop.
- kiln_vfx.h `KilnVfxApi.set_param` @32 — A named parameter of the effect: name_hash = FNV-1a of the name, type_id = keen::DsTypeId, at most 16 B.
- kiln_vfx.h `KilnVfxApi.state` @40
- kiln_vfx.h `KilnVfxApi.info` @48
- kiln_vfx.h `KilnVfxApi.stop_owner` @56
- kiln_vfx.h `KilnVfxApi.is_registered` @64 — 1 registered, 0 not, -1 the VFX system is not known or not ready yet
- kiln_vfx.h `KilnVfxApi.preload` @72 — Holds a reference on the VFX, for the session, so its first real play does not start late.
- kiln_vfx.h `KilnVfxApi.status_json` @80 — status + recent handles
- kiln_vfx.h `KilnVfxApi.define_json` @88 — A VFX defined from code: {"format": 1, "mod": id, "dir": folder, "vfx": {...}}, the same blocks as a JSON...
- kiln_vfx.h `KilnVfxApi.build_status` @96 — JSON: what was built, the budget, the fingerprint
- kiln_vfx.h `KilnVfxApi.bind_guid` @104 — The GUID of a play made with KILN_VFX_WAIT_GUID.
- kiln_vfx.h `KilnVfxApi.resource_budget` @112 `0.2.4` — the resource budget: appended since 0.2.0-dev.29 (version stays 1).
- kiln_vfx.h `KilnVfxApi.define_vfx_result` @120 `0.2.4` — Defines one VFX block of a mod with an answer. name: the block's name (letters, digits, '-', '_', '.'; at...
- kiln_vfx.h `KilnVfxApi.vfx_registration` @128 `0.2.4` — The answer for registration_id now: what became of the definition since its call (registered once the...

## services/kiln_window_clock.h — service `kiln.window_clock`
### KilnWindowClockApiV1 — 16 bytes
- kiln_window_clock.h `KilnWindowClockApiV1.get` @8 `0.2.4` — Fills *out (out->size is set by the callee).

## services/kiln_world.h — service `kiln.world`
### KilnWorldApi — 656 bytes
- kiln_world.h `KilnWorldApi.register_program` @8 — Accepts a program for the server world. *out = its handle (for program_slot / program_info).
- kiln_world.h `KilnWorldApi.program_slot` @16 — Qword index into the iterator of 'name' in list 'kind' (KILN_SLOT_*), or -1 when it is not declared there.
- kiln_world.h `KilnWorldApi.program_info` @24
- kiln_world.h `KilnWorldApi.iter_begin` @32 — Raw mode only: fills 'iter' (size bytes; see program_info.iter_size).
- kiln_world.h `KilnWorldApi.iter_next` @40 — Raw mode only: advances to the next entity of the slice; false at the end.
- kiln_world.h `KilnWorldApi.current_entity` @48 — The entity iter_next stopped at (0 before the first iter_next and in SYSTEM programs).
- kiln_world.h `KilnWorldApi.command_buffer` @56 — The slice's command buffer (opaque; exclusive to this update while it runs).
- kiln_world.h `KilnWorldApi.queue_push` @64 — Reserves a zeroed record of 'size' bytes (16-byte header included: +0 u16 size, +2 u8 kind, +4 u16 program...
- kiln_world.h `KilnWorldApi.queue_add` @72 — Kind-3 record: adds component 'hash' with 'size' bytes of 'data' to 'entity' at the end of the step.
- kiln_world.h `KilnWorldApi.queue_remove` @80 — Kind-4 record: removes component 'hash' from 'entity' at the end of the step.
- kiln_world.h `KilnWorldApi.mark_written` @88 — Marks the component behind a write slot changed (replication to clients, saving) before you write through...
- kiln_world.h `KilnWorldApi.read_other` @96 — Another entity's component through a declared other-read accessor: iter[slot] with slot from...
- kiln_world.h `KilnWorldApi.component_hash` @104 — FNV-1a-32 of "keen::ecs::" + name (as given if it contains "::").
- kiln_world.h `KilnWorldApi.component` @112 — The entity's per-entity component data, or NULL (no such entity or component). *out_size = its size.
- kiln_world.h `KilnWorldApi.exists` @120
- kiln_world.h `KilnWorldApi.transform` @128 — CurrentTransform in metres.
- kiln_world.h `KilnWorldApi.position` @136
- kiln_world.h `KilnWorldApi.find` @144 — Every entity that has per-entity component 'hash', in no particular order. *out_count = how many there...
- kiln_world.h `KilnWorldApi.entity_count` @152
- kiln_world.h `KilnWorldApi.server_world` @160 — Advanced: the server world object (0 when there is none).
- kiln_world.h `KilnWorldApi.teleport` @168 — Queues a teleport. *out_request = its id (never 0).
- kiln_world.h `KilnWorldApi.teleport_cancel` @176 — Drops the request's callback (the move may still happen).
- kiln_world.h `KilnWorldApi.status` @184
- kiln_world.h `KilnWorldApi.write_other` @192 `0.2.0` — Appended in v1: tracked other-entity writes.
- kiln_world.h `KilnWorldApi.unregister_program` @200 `0.2.0` — Disable the callback permanently; engine descriptors remain allocated.
- kiln_world.h `KilnWorldApi.bind_program` @208 `0.2.0` — v2: bind the inert descriptor reserved from \<mod>/kiln.programs.json at boot.
- kiln_world.h `KilnWorldApi.capabilities` @216 `0.2.0` — KILN_WORLD_CAP_*; any thread.
- kiln_world.h `KilnWorldApi.program_events` @224 `0.2.0` — v2: all events of the declared input type, in queue order.
- kiln_world.h `KilnWorldApi.program_emit` @232 `0.2.0` — v2: copy an exact reflected payload into a declared output stream; GameEvent timestamps use program time.
- kiln_world.h `KilnWorldApi.write_submit` @240 `0.2.2` — tracked writes: appended in v3, since 0.2.0-dev.27.
- kiln_world.h `KilnWorldApi.write_cancel` @248 `0.2.2` — Drops the request's callbacks.
- kiln_world.h `KilnWorldApi.write_info` @256 `0.2.2` — What kiln.world knows about writing component 'component' for 'mod_id' (empty: any mod): its state, and...
- kiln_world.h `KilnWorldApi.write_status` @264 `0.2.2`
- kiln_world.h `KilnWorldApi.write_declare` @272 `0.2.2` — A native service's own list of component types to write (a service has no manifest), under the name 'who'...
- kiln_world.h `KilnWorldApi.game_program_find` @280 `0.2.3` — game programs: appended in v4, since 0.2.0-dev.28.
- kiln_world.h `KilnWorldApi.game_program_info` @288 `0.2.3` — One entry of that directory.
- kiln_world.h `KilnWorldApi.game_programs_status` @296 `0.2.3` — The directories, the threads report and the wrap counts.
- kiln_world.h `KilnWorldApi.lab_parallel` @304 `0.2.3` — Lab only, never a mod API (managed: Kiln.Testing.LabSystems).
- kiln_world.h `KilnWorldApi.game_program_wrap` @312 `0.2.3` — Wraps program 'index' of the live world of 'world_kind' (SERVER or CLIENT; PREDICTION since...
- kiln_world.h `KilnWorldApi.game_program_unwrap` @320 `0.2.3` — Unwraps: no call of fn starts after this returns; one that had started finishes (game_wrap_info.running...
- kiln_world.h `KilnWorldApi.game_wrap_info` @328 `0.2.3` — State and counters of a wrap.
- kiln_world.h `KilnWorldApi.game_wraps_release` @336 `0.2.3` — Unwraps every wrap of 'owner': no call of its stages starts after this returns; calls that had started...
- kiln_world.h `KilnWorldApi.game_slice_run_original` @344 `0.2.3` — Inside a REPLACE stage: runs the game's update for this slice, once.
- kiln_world.h `KilnWorldApi.wrap_original` @352 `0.2.3` — The game's update behind a table entry kiln.world swapped (entry = the program-table entry, as the...
- kiln_world.h `KilnWorldApi.in_program` @360 `0.2.3` — appended since 0.2.0-dev.28 (version stays 4).
- kiln_world.h `KilnWorldApi.game_program_list` @368 `0.2.4` — per entity inside a stage (since 0.2.0-dev.29, still v4: check header.size) ---- The names of list 'list'...
- kiln_world.h `KilnWorldApi.game_slice_iterate` @376 `0.2.4` — Inside a stage: runs the program's own iterator over the slice and calls each(iter, row, entity, user) per...
- kiln_world.h `KilnWorldApi.game_slice_slot` @384 `0.2.4` — Inside game_slice_iterate's callback: what the iterator holds for name 'i' of descriptor list 'list' for...
- kiln_world.h `KilnWorldApi.game_slice_mark` @392 `0.2.4` — Inside game_slice_iterate's callback, before writing through a WRITE / WRITE_OPT slot: marks the current...
- kiln_world.h `KilnWorldApi.game_slice_archetype` @400 `0.2.4` — Inside a stage: the slice's archetype key (all its entities share one archetype).
- kiln_world.h `KilnWorldApi.game_archetype_has` @408 `0.2.4` — Whether archetype 'layout' (game_slice_archetype) has component 'component_hash': 1 yes, 0 no, -1 not...
- kiln_world.h `KilnWorldApi.game_slice_attr_changed` @416 `0.2.4` — Inside game_slice_iterate's callback, after writing an attribute component (Fuel, Health, ...) through...
- kiln_world.h `KilnWorldApi.game_slice_attr_validate` @424 `0.2.4` — Since dev.29: inside iteration, validates reflected AttributeComponent ancestry and the block's bounds.
- kiln_world.h `KilnWorldApi.game_world_session` @432 `0.2.4` — Since dev.29: monotonically changing lifecycle generation for this world kind, advanced at STARTED/STOPPING.
- kiln_world.h `KilnWorldApi.game_program_contract` @440 `0.2.4` — Since dev.29: the contract of program 'index''s descriptor in that world's directory (chapter 06a 9.7; the...
- kiln_world.h `KilnWorldApi.foreign_layout` @448 `0.2.4` — foreign views: since 0.2.0-dev.29 (chapter 06c 9.1), still v4: check header.size ---- Code that runs...
- kiln_world.h `KilnWorldApi.foreign_slot` @456 `0.2.4` — The iterator qword of name 'name' of list 'list' (KILN_GAME_LIST_*) in the view, or -1.
- kiln_world.h `KilnWorldApi.foreign_check` @464 `0.2.4` — Inside the program's update, 'iter' its iterator: KILN_OK when the job's group counts and storage registry...
- kiln_world.h `KilnWorldApi.foreign_read` @472 `0.2.4` — Inside the update: component 'component' (short name: "Health") of entity 'entity' through the program's...
- kiln_world.h `KilnWorldApi.foreign_write` @480 `0.2.4` — The same through the program's other_write accessor with the job's tracker (WriteOther): the component...
- kiln_world.h `KilnWorldApi.foreign_attr_changed` @488 `0.2.4` — Immediately after foreign_write on the same thread, for its returned pointer and a reflected attribute...
- kiln_world.h `KilnWorldApi.register_program_world` @496 `0.2.4` — Accepts a program for the world of 'world_kind'.
- kiln_world.h `KilnWorldApi.program_world_state` @504 `0.2.4` — The program's state in the world of 'world_kind' now (KILN_PROGRAM_*), and why in words ('reason'...
- kiln_world.h `KilnWorldApi.bind_program_world` @512 `0.2.4` — bind_program for the world of 'world_kind': binds the declaration 'key' of mod 'mod_id' that kiln.world...
- kiln_world.h `KilnWorldApi.unbind_program_world` @520 `0.2.4` — Stops a callback: register_program_world's program handle (its client or prediction side) or a binding...
- kiln_world.h `KilnWorldApi.entity_open` @528 `0.2.10` — ECS reads from task threads: appended in v4 (the version stays 4), at offsets 528..576 (the table is 584...
- kiln_world.h `KilnWorldApi.entity_validate` @536 `0.2.10` — Re-checks a ref: alive, same generation, same session, same layout, same epoch. *out (size set) always...
- kiln_world.h `KilnWorldApi.ecs_context_snapshot` @544 `0.2.10` — Which worlds and sessions exist right now, and whether Kiln has an exact engine profile for them.
- kiln_world.h `KilnWorldApi.ecs_query` @552 `0.2.10` — Verified query: the entities of request->world_kind that have every listed component (and, with...
- kiln_world.h `KilnWorldApi.ecs_snapshot` @560 `0.2.10` — One component's bytes of the ref's entity, validated before and after the copy (always a stable copy; the...
- kiln_world.h `KilnWorldApi.ecs_snapshot_many` @568 `0.2.10` — Several components, packed in spec order (see KilnEcsSnapshotSpec). presence[i] (when non-NULL) is the...
- kiln_world.h `KilnWorldApi.ecs_template_uuid` @576 `0.2.10` — The template an entity was spawned from (its definition uuid), validated directly before and after the read.
- kiln_world.h `KilnWorldApi.ecs_write_policy` @584 `0.2.10` — compare-exchange writes from task threads: appended in v4 (the version stays 4) after the ECS reads, at...
- kiln_world.h `KilnWorldApi.ecs_compare_exchange` @592 `0.2.10` — Compare 'expected' (the whole component, 'bytes' long) with the entity's component and, when equal, write...
- kiln_world.h `KilnWorldApi.ecs_write_subscribe` @600 `0.2.10` — 'fn' is called once per committed write, on the server tick (kiln.world's Pre) or at the server world's...
- kiln_world.h `KilnWorldApi.ecs_write_unsubscribe` @608 `0.2.10` — No later event reaches the subscription once this returns.
- kiln_world.h `KilnWorldApi.named_service_open` @616 `0.2.13` — named services from task threads: appended since 0.2.0-dev.30.3 (the version stays 4) after the...
- kiln_world.h `KilnWorldApi.named_service_snapshot` @624 `0.2.13` — The service's whole bytes, copied under the gate between frames and found again before the release....
- kiln_world.h `KilnWorldApi.named_service_close` @632 `0.2.13` — Ends a view.
- kiln_world.h `KilnWorldApi.named_service_compare_exchange` @640 `0.2.13` — Compare 'expected' (the whole service, 'bytes' long) with the service and, when equal, write 'desired'...
- kiln_world.h `KilnWorldApi.ecs_registration` @648 `0.2.13` — since 0.2.0-dev.30.3 (check header.size): one registration of a world's component catalog, at 648 (the...

## services/kiln_world_budget.h — service `kiln.world.budget`
### KilnWorldBudgetApi — 24 bytes
- kiln_world_budget.h `KilnWorldBudgetApi.program_budget` @8 `0.2.4` — world_kind KILN_WORLD_SERVER or KILN_WORLD_CLIENT.
- kiln_world_budget.h `KilnWorldBudgetApi.program_reservation` @16 `0.2.4` — Since dev.29: immutable boot declaration for (mod id, key), independent of any live world or binding.

## services/kiln_worldtables.h — service `kiln.worldtables`
### KilnWorldTablesApi — 176 bytes
- kiln_worldtables.h `KilnWorldTablesApi.state` @8 — KILN_WT_*
- kiln_worldtables.h `KilnWorldTablesApi.table_hash` @16 — What client and server have to agree on: materials, crafting props and optional custom pools/bindings.
- kiln_worldtables.h `KilnWorldTablesApi.material_count` @24 — Every material in the tables: the building materials, then the terrain ones (cell_id \< 128), live ones...
- kiln_worldtables.h `KilnWorldTablesApi.material_at` @32
- kiln_worldtables.h `KilnWorldTablesApi.material_id` @40 — The cell id of a material's GUID, or -1: not in the tables.
- kiln_worldtables.h `KilnWorldTablesApi.first_new_id` @48 — The cell id of the first id after the game's own table entries, or -1 before the merge.
- kiln_worldtables.h `KilnWorldTablesApi.add_writer` @56 — Adds a table writer.
- kiln_worldtables.h `KilnWorldTablesApi.define_json` @64 — A definition from code: {"format": 1, "mod": "\<id>", "dir": "\<folder>", "materialTweaks": {...}...
- kiln_worldtables.h `KilnWorldTablesApi.prune_pending` @72 — A mod's code ran: blocks kept from code for names other than these (a JSON array) are dropped from the...
- kiln_worldtables.h `KilnWorldTablesApi.status_json` @80 — JSON: state, hash, ids, tweaks, what was refused and why.
- kiln_worldtables.h `KilnWorldTablesApi.forget` @88 — Stops keeping an absent mod's material in every world: from the next start, a world's cells of it are lost...
- kiln_worldtables.h `KilnWorldTablesApi.tweak_count` @96 — The tweaks of game materials whose material resolved, in load order (a later one wins a field both set).
- kiln_worldtables.h `KilnWorldTablesApi.tweak_at` @104
- kiln_worldtables.h `KilnWorldTablesApi.game_material_id` @112 — A game material's cell id from what a mod writes: a GameMaterials name, "Cell\<id>", or the material's...
- kiln_worldtables.h `KilnWorldTablesApi.crafting_prop_count` @120 `0.2.0` — The mods' crafting props in the game's table, in the order of their places: 0 before the merge and when off.
- kiln_worldtables.h `KilnWorldTablesApi.crafting_prop_at` @128 `0.2.0`
- kiln_worldtables.h `KilnWorldTablesApi.crafting_prop_index` @136 `0.2.0` — The place (bit) of a crafting prop's GUID, or -1: not in the table.
- kiln_worldtables.h `KilnWorldTablesApi.material_budget` @144 `0.2.4` — dev.29 appends.
- kiln_worldtables.h `KilnWorldTablesApi.pool_budget` @152 `0.2.4`
- kiln_worldtables.h `KilnWorldTablesApi.define_pool_result` @160 `0.2.4` — Same writer document as define_json, with one buildingMaterialPools entry named by name.
- kiln_worldtables.h `KilnWorldTablesApi.pool_registration` @168 `0.2.4` — owner=0 is a pure any-thread query.
