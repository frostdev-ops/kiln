# Native ABI rows (mod API 0.2.28): part 1 of 3

One line per row: header, `Table.row`, @offset, mod API, comment (native.md says more).

## kiln_api.h
### KilnApi — 216 bytes
- kiln_api.h `KilnApi.version` @8
- kiln_api.h `KilnApi.log` @16
- kiln_api.h `KilnApi.build_info` @24
- kiln_api.h `KilnApi.feature` @32
- kiln_api.h `KilnApi.path` @40 — The folder of 'kind' (KILN_PATH_*) as an absolute Windows-form path in UTF-8 (not NUL-terminated).
- kiln_api.h `KilnApi.owner_create` @48
- kiln_api.h `KilnApi.owner_revoke` @56 — call on the tick thread, or before the first tick
- kiln_api.h `KilnApi.subscribe_tick` @64
- kiln_api.h `KilnApi.unsubscribe` @72
- kiln_api.h `KilnApi.post` @80
- kiln_api.h `KilnApi.on_tick_thread` @88
- kiln_api.h `KilnApi.tick_info` @96
- kiln_api.h `KilnApi.subscribe_log` @104
- kiln_api.h `KilnApi.symbol` @112 — absolute address
- kiln_api.h `KilnApi.constant` @120 — layout constant, e.g. "world.storage"
- kiln_api.h `KilnApi.service` @128 — Returns the function table of a named service (e.g. "kiln.teleport") whose version >= min_version, or NULL...
- kiln_api.h `KilnApi.register_service` @136 — Publishes a service table under 'name' (call from kiln_plugin_main).
- kiln_api.h `KilnApi.server_world` @144 — server_world: the server world pointer, 0 before its first sim step. singleton: the object registered...
- kiln_api.h `KilnApi.singleton` @152
- kiln_api.h `KilnApi.side` @160 — side: KILN_SIDE_* of this process (from the exe's identity), also when hooks are off. world: the live...
- kiln_api.h `KilnApi.world` @168
- kiln_api.h `KilnApi.on_world_thread` @176
- kiln_api.h `KilnApi.subscribe_world_tick` @184
- kiln_api.h `KilnApi.post_world` @192
- kiln_api.h `KilnApi.subscribe_world` @200
- kiln_api.h `KilnApi.session` @208

## services/kiln_abilities.h — service `kiln.abilities`
### KilnAbilitiesApi — 320 bytes
- kiln_abilities.h `KilnAbilitiesApi.status` @8 — any thread
- kiln_abilities.h `KilnAbilitiesApi.set_listener` @16 — NULL removes it
- kiln_abilities.h `KilnAbilitiesApi.hold_filter` @24 — Narrows HOLD to instances of these programs (count 0 = hold every new instance while HOLD interest is set).
- kiln_abilities.h `KilnAbilitiesApi.instance_vars` @32 — Variables of a live instance (count = all; writes up to cap).
- kiln_abilities.h `KilnAbilitiesApi.instance_read` @40
- kiln_abilities.h `KilnAbilitiesApi.instance_write` @48
- kiln_abilities.h `KilnAbilitiesApi.instance_info` @56 — The instance's self entity, program and flags (Initialized 1, Executed 2, Halt 8, Error 0x10, Destroy 0x40).
- kiln_abilities.h `KilnAbilitiesApi.entity_overrides` @64 — Config overrides for the next instance created on 'entity' (e.g. one Kiln just spawned). count 0 clears.
- kiln_abilities.h `KilnAbilitiesApi.program_count` @72 — tick
- kiln_abilities.h `KilnAbilitiesApi.program_info` @80 — tick
- kiln_abilities.h `KilnAbilitiesApi.program_vars` @88 — tick
- kiln_abilities.h `KilnAbilitiesApi.program_register` @96 — tick.
- kiln_abilities.h `KilnAbilitiesApi.program_refresh` @104 — tick.
- kiln_abilities.h `KilnAbilitiesApi.native_count` @112 — any thread
- kiln_abilities.h `KilnAbilitiesApi.native_at` @120 — any thread
- kiln_abilities.h `KilnAbilitiesApi.native_find` @128
- kiln_abilities.h `KilnAbilitiesApi.native_declare` @136 — any thread.
- kiln_abilities.h `KilnAbilitiesApi.native_register` @144 — tick.
- kiln_abilities.h `KilnAbilitiesApi.native_unregister` @152 — tick.
- kiln_abilities.h `KilnAbilitiesApi.native_complete` @160 — tick (listener).
- kiln_abilities.h `KilnAbilitiesApi.native_wrap` @168 — any thread.
- kiln_abilities.h `KilnAbilitiesApi.native_observe` @176
- kiln_abilities.h `KilnAbilitiesApi.set_wrap_dispatcher` @184
- kiln_abilities.h `KilnAbilitiesApi.call_original` @192 — job thread, from the dispatcher only: runs the game's native with the current args; sets out_error/out_value.
- kiln_abilities.h `KilnAbilitiesApi.release_owner` @200 — tick, outside a native call.
- kiln_abilities.h `KilnAbilitiesApi.attr_get` @208 — The current value of attribute 'attr' (e.g. Mana 0x6aa2574e) in component 'component' (FNV-1a of...
- kiln_abilities.h `KilnAbilitiesApi.attr_add` @216
- kiln_abilities.h `KilnAbilitiesApi.sequence_state` @224 — The actor's current sequence (Actor.sequenceRuntimeId): sequence id (FNV-1a of the ActorSequenceResource...
- kiln_abilities.h `KilnAbilitiesApi.sequence_play` @232 — tick.
- kiln_abilities.h `KilnAbilitiesApi.native_register_sync` @240 `0.2.4` — since 0.2.0-dev.29: synchronous natives (check header.size before use) ---- tick, and not from inside a...
- kiln_abilities.h `KilnAbilitiesApi.native_counters` @248 `0.2.4` — any thread.
- kiln_abilities.h `KilnAbilitiesApi.native_attach` @256 `0.2.4` — since 0.2.0-dev.29: attachments (check header.size before use) ---- tick, not from inside a native call.
- kiln_abilities.h `KilnAbilitiesApi.native_detach` @264 `0.2.4` — tick, not from inside a native call.
- kiln_abilities.h `KilnAbilitiesApi.native_declare_typed` @272 `0.2.4` — since 0.2.0-dev.29: typed declarations (check header.size before use) ---- any thread.
- kiln_abilities.h `KilnAbilitiesApi.native_state` @280 `0.2.4` — since 0.2.0-dev.29: states (check header.size before use) ---- any thread.
- kiln_abilities.h `KilnAbilitiesApi.native_attach_state` @288 `0.2.4`
- kiln_abilities.h `KilnAbilitiesApi.native_ecs_read` @296 `0.2.4` — since 0.2.0-dev.29: the ECS bridge (chapter 06c 9.1), check header.size ---- Inside a sync native's or an...
- kiln_abilities.h `KilnAbilitiesApi.native_ecs_write` @304 `0.2.4` — The same through the other_write accessor with the job's tracker (saved and replicated like the game's own...
- kiln_abilities.h `KilnAbilitiesApi.item_use` @312 `0.2.13` — since 0.2.0-dev.30.3 (mod API 0.2.13): an item used from a player's slot, check header.size (a table...

## services/kiln_assets.h — service `kiln.assets`
### KilnAssetsApi — 256 bytes
- kiln_assets.h `KilnAssetsApi.icon_register_png` @8 — icon_register_png: 'png' = the bytes of a PNG file (8-bit RGBA/RGB/grey, any size up to 1024x1024; drawn...
- kiln_assets.h `KilnAssetsApi.icon_register_file` @16
- kiln_assets.h `KilnAssetsApi.icon_remove` @24
- kiln_assets.h `KilnAssetsApi.icon_state` @32
- kiln_assets.h `KilnAssetsApi.resource_put` @40 — resource_put: serve 'data' (copied) for 'key': a new resource, or the replacement of a game resource (flags).
- kiln_assets.h `KilnAssetsApi.resource_remove` @48
- kiln_assets.h `KilnAssetsApi.resource_read` @56
- kiln_assets.h `KilnAssetsApi.resource_list` @64
- kiln_assets.h `KilnAssetsApi.blob_add` @72 — blob_add: a new blob; out_hash = the hash to put into a resource's ResourceContentReference (derived from...
- kiln_assets.h `KilnAssetsApi.blob_override` @80
- kiln_assets.h `KilnAssetsApi.blob_remove` @88
- kiln_assets.h `KilnAssetsApi.blob_read` @96
- kiln_assets.h `KilnAssetsApi.texture_override_png` @104 — texture_override_png: replace the pixels of the texture resource 'key' (keen::UiTextureResource or...
- kiln_assets.h `KilnAssetsApi.dump` @112 — dump: write resources / blobs to \<game>/kiln/data/assets/dump/. spec: "\<guid>[:\<type>[:\<part>]]"...
- kiln_assets.h `KilnAssetsApi.status` @120
- kiln_assets.h `KilnAssetsApi.revert_owner` @128
- kiln_assets.h `KilnAssetsApi.load_manifest` @136
- kiln_assets.h `KilnAssetsApi.resource_data` @144 — resource_data: a zero-copy, read-only view of a resource's live loaded bytes, as the game sees them (a...
- kiln_assets.h `KilnAssetsApi.resource_is_new` @152 — resource_is_new: 1 when 'key' was served in this process as a resource the game itself does not have, so a...
- kiln_assets.h `KilnAssetsApi.resource_new_count` @160
- kiln_assets.h `KilnAssetsApi.when_resources_ready` @168 — when_resources_ready: calls fn(user) once the game's resource service is up, on the game's thread and...
- kiln_assets.h `KilnAssetsApi.blob_add_ex` @176 `0.2.3` — since 0.2.0-dev.28 (check header.size before each) ---- blob_add_ex / blob_override_ex: blob_add /...
- kiln_assets.h `KilnAssetsApi.blob_override_ex` @184 `0.2.3`
- kiln_assets.h `KilnAssetsApi.blob_catalog_open` @192 `0.2.3` — The content blob catalog (above, before KilnAssetsApi).
- kiln_assets.h `KilnAssetsApi.blob_catalog_read` @200 `0.2.3`
- kiln_assets.h `KilnAssetsApi.blob_catalog_close` @208 `0.2.3`
- kiln_assets.h `KilnAssetsApi.texture_rgba` @216 `0.2.7` — since 0.2.0-dev.29.15 (check header.size >= offsetof(KilnAssetsApi, texture_rgba) + sizeof(void*)) ----...
- kiln_assets.h `KilnAssetsApi.blob_read_resident` @224 `0.2.7` — blob_read_resident (0.2.0-dev.29.15, check header.size >= offsetof(KilnAssetsApi, blob_read_resident) +...
- kiln_assets.h `KilnAssetsApi.loaded_snapshot` @232 `0.2.13` — since 0.2.0-dev.30.3 (check header.size >= offsetof(KilnAssetsApi, loaded_list) + sizeof(void*)) ----...
- kiln_assets.h `KilnAssetsApi.loaded_list` @240 `0.2.13`
- kiln_assets.h `KilnAssetsApi.loaded_scalar_compare_exchange` @248 `0.2.13` — since 0.2.0-dev.30.3 (check header.size >= offsetof(KilnAssetsApi, loaded_scalar_compare_exchange) +...

## services/kiln_assets_vfx.h — service `kiln.assets.vfx`
### KilnAssetsVfxApi — 32 bytes
- kiln_assets_vfx.h `KilnAssetsVfxApi.budget` @8 `0.2.4` — size must be >= sizeof(KilnAssetsVfxBudget).
- kiln_assets_vfx.h `KilnAssetsVfxApi.set_capacity` @16 `0.2.4` — Records an installed capacity (2048 or 4096), boot only, before resources-ready starts or S exists.
- kiln_assets_vfx.h `KilnAssetsVfxApi.resource_put` @24 `0.2.4` — Cooker registration: retains mod with the resource under the store lock, for late omission reports.

## services/kiln_audio.h — service `kiln.audio`
### KilnAudioApi — 104 bytes
- kiln_audio.h `KilnAudioApi.status` @8
- kiln_audio.h `KilnAudioApi.play` @16 — A cue in a scene: 2D (no AT/FOLLOW), at a position, or following an entity.
- kiln_audio.h `KilnAudioApi.stop` @24 — fade_ns 0 = the game's default (immediate); typical 20..100 ms.
- kiln_audio.h `KilnAudioApi.set_volume` @32
- kiln_audio.h `KilnAudioApi.set_position` @40 — Moves the sound (and ends FOLLOW).
- kiln_audio.h `KilnAudioApi.state` @48
- kiln_audio.h `KilnAudioApi.info` @56
- kiln_audio.h `KilnAudioApi.stop_owner` @64 — Stops every sound and voice-over line 'owner' started; returns how many.
- kiln_audio.h `KilnAudioApi.voiceover_play` @72 — voice-over (in game; features audio.voiceover / audio.narration) ---- voiceover_play: a VoiceoverResource...
- kiln_audio.h `KilnAudioApi.voiceover_stop` @80
- kiln_audio.h `KilnAudioApi.narrate` @88
- kiln_audio.h `KilnAudioApi.status_json` @96 — Diagnostics as JSON (status, live handles, the game's SoundStart threads). out_len gets the full length.

## services/kiln_base_rules.h — service `kiln.base_rules`
### KilnBaseRulesApi — 16 bytes
- kiln_base_rules.h `KilnBaseRulesApi.state` @8 `0.2.17`

## services/kiln_boot.h — service `kiln.boot`
### KilnBootApiV1 — 104 bytes
- kiln_boot.h `KilnBootApiV1.status` @8 `0.2.3` — The boot phase.
- kiln_boot.h `KilnBootApiV1.begin_owner` @16 `0.2.3` — Opens 'mod_id''s author frame (Manifest.IdPattern, ^[a-z0-9][a-z0-9._-]{0,63}$) for the transient author...
- kiln_boot.h `KilnBootApiV1.stage` @24 `0.2.3` — Stages one definition: kind is one of the boot kinds (statuses, effects, items, spells, talents...
- kiln_boot.h `KilnBootApiV1.finish_owner` @32 `0.2.3` — Closes an author frame: accept is KILN_BOOT_FINISH_*.
- kiln_boot.h `KilnBootApiV1.read_resource` @40 `0.2.3` — The bounded offline game-data view (one query, see Kiln.IReadOnlyGameData.Read) for an open author frame...
- kiln_boot.h `KilnBootApiV1.commit` @48 `0.2.3` — Prepares and publishes the generation (see "The phase" and "Failure policy").
- kiln_boot.h `KilnBootApiV1.owner_state` @56 `0.2.3` — One mod's state (any thread).
- kiln_boot.h `KilnBootApiV1.issues` @64 `0.2.3` — The issues of 'generation' (any thread), oldest first. stride >= sizeof(KilnBootIssueV1); each record's...
- kiln_boot.h `KilnBootApiV1.collector` @72 `0.2.3` — the native collectors (assets, worldtables, content, skills, knowledge, vfx, patches) register once during...
- kiln_boot.h `KilnBootApiV1.active_plan` @80 `0.2.3` — consumer hooks read through the same atomic committed generation; borrowed until process exit.
- kiln_boot.h `KilnBootApiV1.activation_fault` @88 `0.2.3` — Appended (0.2.0-dev.28, resource patches); check header.size before calling.
- kiln_boot.h `KilnBootApiV1.stop_start` @96 `0.2.4` — Appended (0.2.0-dev.29); check header.size before calling.

## services/kiln_build.h — service `kiln.build`
### KilnBuildApi — 184 bytes
- kiln_build.h `KilnBuildApi.status` @8 — any thread
- kiln_build.h `KilnBuildApi.grid` @16 — Grid geometry.
- kiln_build.h `KilnBuildApi.ready` @24 — KILN_OK when every streaming tile under the box is loaded.
- kiln_build.h `KilnBuildApi.read` @32 — Reads cells (capacity in cells).
- kiln_build.h `KilnBuildApi.write` @40 — Writes cells through the engine's writer; *result reports what changed and whether it read back equal.
- kiln_build.h `KilnBuildApi.shape_box` @48 — The cell box a shape edit touches (for journals: read it before calling shape).
- kiln_build.h `KilnBuildApi.shape` @56 — Runs a shape edit (read box, compute, write).
- kiln_build.h `KilnBuildApi.prop_place` @64
- kiln_build.h `KilnBuildApi.prop_remove` @72 — Destroys a prop (any non-player entity that is not a creature or villager).
- kiln_build.h `KilnBuildApi.props_in_box` @80 — Entities whose position lies in [min, max] (metres), filtered (KILN_BUILD_PROPS_*), nearest to the box...
- kiln_build.h `KilnBuildApi.prop_info` @88
- kiln_build.h `KilnBuildApi.bases` @96
- kiln_build.h `KilnBuildApi.item_material` @104 — items (thread-safe once the world ran) ---- The voxel material an item places (building material items...
- kiln_build.h `KilnBuildApi.item_template` @112 — The template an item places (prop items); NOT_FOUND when it places none.
- kiln_build.h `KilnBuildApi.keep` @120 — The game saves voxel edits only inside player-base build zones; everything else reverts at the next restart.
- kiln_build.h `KilnBuildApi.release` @128
- kiln_build.h `KilnBuildApi.terrain_budget_reserve` @136 `0.2.4` — Appended dev.29, v1: shared Terrain/recovery allowance on the tick thread.
- kiln_build.h `KilnBuildApi.terrain_budget_charge` @144 `0.2.4`
- kiln_build.h `KilnBuildApi.grid_for_session` @152 `0.2.13` — voxels from task threads: appended since 0.2.0-dev.30.3 at offset 152 (the table is 160 bytes with it).
- kiln_build.h `KilnBuildApi.block_at` @160 `0.2.15` — Since 0.2.0-dev.30.6: raw info succeeds with unknown optional metadata/provenance (-1). find_wall...
- kiln_build.h `KilnBuildApi.find_wall` @168 `0.2.15`
- kiln_build.h `KilnBuildApi.break_box` @176 `0.2.15`

## services/kiln_camera.h — service `kiln.camera`
### KilnCameraApi — 88 bytes
- kiln_camera.h `KilnCameraApi.capabilities` @8 `0.2.17` — Which KILN_CAMERA_CAP_* work for 'endpoint' (KILN_CAMERA_SERVER_MODE, ...).
- kiln_camera.h `KilnCameraApi.apply` @16 `0.2.17` — A new lease.
- kiln_camera.h `KilnCameraApi.update` @24 `0.2.17` — Replaces the lease's whole request (not a merge).
- kiln_camera.h `KilnCameraApi.end` @32 `0.2.17` — Ends a lease of 'owner'.
- kiln_camera.h `KilnCameraApi.subscribe` @40 `0.2.17` — Every change of every lease.
- kiln_camera.h `KilnCameraApi.unsubscribe` @48 `0.2.17`
- kiln_camera.h `KilnCameraApi.get_visual` @56 `0.2.17` — The camera of the newest frame.
- kiln_camera.h `KilnCameraApi.bind_transport` @64 `0.2.17` — Framework-private: one bridge at a time; the table is copied. unbind after the bridge drained its work.
- kiln_camera.h `KilnCameraApi.unbind_transport` @72 `0.2.17`
- kiln_camera.h `KilnCameraApi.transport_ended` @80 `0.2.17` — The client ended a remote lease by itself.

## services/kiln_caps.h — service `kiln.caps`
### KilnCapsApi — 64 bytes
- kiln_caps.h `KilnCapsApi.count` @8 `0.2.1` — Rows, sorted by name (byte order). count() and at() are not one transaction: read generation() before and...
- kiln_caps.h `KilnCapsApi.at` @16 `0.2.1`
- kiln_caps.h `KilnCapsApi.find` @24 `0.2.1` — The row of 'name'.
- kiln_caps.h `KilnCapsApi.report` @32 `0.2.1` — Sets 'owner''s report for 'name' (replacing its earlier one). state: KILN_CAP_*; reason...
- kiln_caps.h `KilnCapsApi.subscribe` @40 `0.2.1` — Calls 'fn' for every change of a row a reader sees, from now on (no call for the rows that exist already).
- kiln_caps.h `KilnCapsApi.unsubscribe` @48 `0.2.1`
- kiln_caps.h `KilnCapsApi.generation` @56 `0.2.1` — Counts the changes a reader can see, from 1.

## services/kiln_chat.h — service `kiln.chat`
### KilnChatApi — 80 bytes
- kiln_chat.h `KilnChatApi.subscribe` @8 — Players' typed lines.
- kiln_chat.h `KilnChatApi.unsubscribe` @16
- kiln_chat.h `KilnChatApi.send` @24 — Server line to one player (to = entity) or to every connected player (to = 0): a notice, shown as its text...
- kiln_chat.h `KilnChatApi.say` @32 — A line spoken by player 'speaker', shown to everyone (to = 0) or to one player.
- kiln_chat.h `KilnChatApi.players` @40 — Connected players.
- kiln_chat.h `KilnChatApi.find_player` @48
- kiln_chat.h `KilnChatApi.status` @56
- kiln_chat.h `KilnChatApi.inject` @64 — Tests and tools: queue 'text' as if the player with connection handle 'connection' (0 = nobody) had sent it.
- kiln_chat.h `KilnChatApi.last_line` @72 — Newest inbox line: info and a copy of its text (NUL-terminated, cut to 'capacity').

## services/kiln_client.h — service `kiln.client`
### KilnClientApi — 80 bytes
- kiln_client.h `KilnClientApi.state` @8
- kiln_client.h `KilnClientApi.subscribe_tick` @16 — Pre/Post of the client world's steps, on the main thread (KILN_TICK_PRE / KILN_TICK_POST).
- kiln_client.h `KilnClientApi.unsubscribe` @24
- kiln_client.h `KilnClientApi.post` @32 — Runs 'fn' on the main thread before the client world's next Pre (dropped with the owner).
- kiln_client.h `KilnClientApi.on_client_thread` @40
- kiln_client.h `KilnClientApi.client_world` @48 — The client world, 0 when none.
- kiln_client.h `KilnClientApi.building_input_submit` @56 `0.2.12` — Building input (see KilnBuildingInput), since 0.2.0-dev.30.3: appended at 56, check header.size >= 80...
- kiln_client.h `KilnClientApi.building_input_status` @64 `0.2.12`
- kiln_client.h `KilnClientApi.building_input_cancel` @72 `0.2.12`

## services/kiln_client_crosshair.h — service `kiln.client.crosshair`
### KilnClientCrosshairApi — 16 bytes
- kiln_client_crosshair.h `KilnClientCrosshairApi.get` @8 `0.2.22` — The newest sample, copied into *out (out->size >= sizeof(KilnCrosshairHit)); *out_reason (may be NULL)...

## services/kiln_combat.h — service `kiln.combat`
### KilnCombatApi — 128 bytes
- kiln_combat.h `KilnCombatApi.health` @8 — NOT_FOUND: no such entity or it has no Health component.
- kiln_combat.h `KilnCombatApi.set_health` @16 — Sets current health (clamped to 0..max) with the attribute system's own add (formulas re-evaluated).
- kiln_combat.h `KilnCombatApi.heal` @24 — Adds 'amount' (> 0) up to max, silently (no HitEvent). *out_healed = health actually added.
- kiln_combat.h `KilnCombatApi.damage` @32 — Queues a hit; it applies in the next impact_hit_conclusion (this step when called in Pre).
- kiln_combat.h `KilnCombatApi.hit_status` @40
- kiln_combat.h `KilnCombatApi.set_invulnerable` @48 — God mode on/off for one entity (players too): damage hits on it are dropped before they apply, and a...
- kiln_combat.h `KilnCombatApi.invulnerable` @56
- kiln_combat.h `KilnCombatApi.release_owner` @64 — Drops everything 'owner' holds: its god-mode entries and its queued hits (call when the owner goes away).
- kiln_combat.h `KilnCombatApi.set_listener` @72 — One listener (the managed Kiln.Combat); a second call replaces it, fn NULL removes it.
- kiln_combat.h `KilnCombatApi.status` @80 — any thread
- kiln_combat.h `KilnCombatApi.attribute_ids` @88 `0.2.0` — Server world, tick thread (Pre or Post) except attribute_ids (any thread).
- kiln_combat.h `KilnCombatApi.attribute_get` @96 `0.2.0` — Reads attribute 'attribute_id' of 'entity', with its pair's current value and bounds.
- kiln_combat.h `KilnCombatApi.attribute_set` @104 `0.2.0` — Sets a pair's current value, clamped to [min, max] (min: the pair's minimum attribute, else 0), through...
- kiln_combat.h `KilnCombatApi.attribute_add` @112 `0.2.0` — Adds 'delta' (negative to spend) to a pair's current value, clamped like attribute_set.
- kiln_combat.h `KilnCombatApi.hold_filter` @120 `0.2.2` — since 0.2.0-dev.27 (appended; check header.size >= offsetof(KilnCombatApi, hold_filter) + sizeof(void*))...

## services/kiln_component_changes.h — service `kiln.component_changes`
### KilnComponentChangesApiV1 — 144 bytes
- kiln_component_changes.h `KilnComponentChangesApiV1.attach` @8 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.detach` @16 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.watch` @24 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.resume` @32 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.status` @40 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.remove` @48 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.release_owner` @56 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.begin_events` @64 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.read_events` @72 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.ack_events` @80 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.record_write` @88 `0.2.4` — native service owners only; data valid during call, copied; any legal component writer thread
- kiln_component_changes.h `KilnComponentChangesApiV1.window_begin` @96 `0.2.4` — core-internal only: after all Pre / after WaitWorldJobs before ordinary Post
- kiln_component_changes.h `KilnComponentChangesApiV1.window_end` @104 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.window_wanted` @112 `0.2.4` — ======== appended (0.2.0-dev.29); check header.size covers a member before calling it ========...
- kiln_component_changes.h `KilnComponentChangesApiV1.window_faulted` @120 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.counters` @128 `0.2.4`
- kiln_component_changes.h `KilnComponentChangesApiV1.watched` @136 `0.2.4`

## services/kiln_content.h — service `kiln.content`
### KilnContentApi — 288 bytes
- kiln_content.h `KilnContentApi.status` @8 — any thread
- kiln_content.h `KilnContentApi.count` @16 — Registries. count() is 0 until ready. find: debug name (exact, then case-insensitive), "0x1234abcd" /...
- kiln_content.h `KilnContentApi.entry_at` @24
- kiln_content.h `KilnContentApi.find` @32
- kiln_content.h `KilnContentApi.field_count` @40 — Field schema of a registry (from the game's reflection).
- kiln_content.h `KilnContentApi.field_at` @48
- kiln_content.h `KilnContentApi.field_find` @56
- kiln_content.h `KilnContentApi.enum_name` @64
- kiln_content.h `KilnContentApi.get` @72 — Values. get: current (and, optional, the game's original). set: a layer of 'owner'; INT/FLOAT/TIME/BOOL...
- kiln_content.h `KilnContentApi.set` @80
- kiln_content.h `KilnContentApi.reset` @88
- kiln_content.h `KilnContentApi.stacks` @96 — Recipe inputs / outputs, drop-table stacks. *out_count = the list's length (more than cap: the first cap...
- kiln_content.h `KilnContentApi.set_stacks` @104
- kiln_content.h `KilnContentApi.apply_json` @112 — A data-pack document (the content/\<file>.json format) applied as 'owner''s layers.
- kiln_content.h `KilnContentApi.revert_owner` @120 — Removes every layer of 'owner' (fields, stacks, drop rules); returns how many fields were put back.
- kiln_content.h `KilnContentApi.dump` @128 — Writes every registry (schema + entries with their non-zero fields, stacks, originals of changed fields)...
- kiln_content.h `KilnContentApi.reload_packs` @136 — Re-reads every mod folder's content/\<file>.json now (also happens when a file changes).
- kiln_content.h `KilnContentApi.report` @144 — JSON report of the last data-pack application: generation, packs, errors, conflicts, skipped, and setApart...
- kiln_content.h `KilnContentApi.subscribe_ready` @152 — fn runs after every application (world load, pack reload), on the tick thread (Pre), after the data packs...
- kiln_content.h `KilnContentApi.unsubscribe_ready` @160
- kiln_content.h `KilnContentApi.stack_guard` @168 — The stack guard: while on, the game no longer cuts a stack down to its item's max stack size (its...
- kiln_content.h `KilnContentApi.label_owner` @176 — Names 'owner' in reports and conflict lines (e.g. "mod bigstacks").
- kiln_content.h `KilnContentApi.pack_mods` @184 — Which mods' data packs apply, in which order: a JSON array of mod ids in load order (Kiln.Content passes...
- kiln_content.h `KilnContentApi.export_changes` @192 — The effective changes as a data-pack document with absolute values (Kiln.Network's content sync sends it...
- kiln_content.h `KilnContentApi.define` @200 — Defines a new item (kind KILN_CONTENT_ITEMS) or recipe (KILN_CONTENT_RECIPES) as 'owner''s, from a JSON...
- kiln_content.h `KilnContentApi.definitions` @208 — Every definition as a JSON array: {kind, guid, id, name, base, owner, source, state: pending | registered...
- kiln_content.h `KilnContentApi.set_ref` @216 — Points reference fields of a registered item at resources the game already loaded ("kitbash"), as...
- kiln_content.h `KilnContentApi.undefine_owner` @224 — Sets every definition of 'owner' aside as if the owner had gone away (items stay registered until the...
- kiln_content.h `KilnContentApi.info` @232 — JSON: {"languages": [game language keys by keen::LanguageId], "textLanguage": bool (the hooks answer per...
- kiln_content.h `KilnContentApi.icon_alias` @240 `0.2.0` — icon_alias: the UI looks the texture key 'from' up as 'to' for as long as kiln.assets does not serve an...
- kiln_content.h `KilnContentApi.txn_begin` @248 `0.2.3` — txn_begin sets every layer of 'owner' aside (its fields, list headers and the ids that follow a...
- kiln_content.h `KilnContentApi.txn_end` @256 `0.2.3`
- kiln_content.h `KilnContentApi.text` @264 `0.2.10` — text: the game's text of a loca tag id in the language the game shows its texts in now, the way the game's...
- kiln_content.h `KilnContentApi.text_info` @272 `0.2.10` — text_info: whether text() works here (the capability row content.text) and the game's text language now.
- kiln_content.h `KilnContentApi.pack_mod_reloaded` @280 `0.2.22` — A mod of the pack_mods list was loaded again while the list stayed the same (a reload, or an unload and a...

## services/kiln_creature_control.h — service `kiln.creature_control`
### KilnCreatureControlApi — 96 bytes
- kiln_creature_control.h `KilnCreatureControlApi.bind_body` @8 `0.2.3` — One binding reference of 'owner' to the body (idempotent per owner; other owners may share the key).
- kiln_creature_control.h `KilnCreatureControlApi.acquire_control` @16 `0.2.3` — Admission.
- kiln_creature_control.h `KilnCreatureControlApi.query_control` @24 `0.2.3` — The complete state of a lease (any owner may read).
- kiln_creature_control.h `KilnCreatureControlApi.can_drive` @32 `0.2.3` — *out = 1 only while 'lease' is the body's Active winner, belongs to 'owner', and key and...
- kiln_creature_control.h `KilnCreatureControlApi.release_control` @40 `0.2.3` — Releases the owner's lease: drive permission ends at once, then the body is restored when no lease is...
- kiln_creature_control.h `KilnCreatureControlApi.notices` @48 `0.2.3` — Takes up to 'capacity' notices of the owner's leases, oldest first (each exactly a KilnControlState...
- kiln_creature_control.h `KilnCreatureControlApi.quarantine` @56 `0.2.3` — Latches the body's quarantine (cause KILN_CONTROL_CAUSE_*): every lease loses authority at once and its...
- kiln_creature_control.h `KilnCreatureControlApi.body_retired` @64 `0.2.3` — The owner destroyed the body: the broker checks it is gone at a job barrier, keeps its root denied for the...
- kiln_creature_control.h `KilnCreatureControlApi.root_policy` @72 `0.2.3` — 1: deny every outgoing effect of hits whose root is this entity (a quarantined or retired body of this...
- kiln_creature_control.h `KilnCreatureControlApi.next_request` @80 `0.2.3` — A new request id for the owner (unique, increasing; allocate just before acquire_control).
- kiln_creature_control.h `KilnCreatureControlApi.release_body` @88 `0.2.3` — Drops the owner's binding reference.

## services/kiln_creatures.h — service `kiln.creatures`
### KilnCreaturesApi — 320 bytes
- kiln_creatures.h `KilnCreaturesApi.status` @8 — any thread
- kiln_creatures.h `KilnCreaturesApi.template_count` @16 — catalog (thread-safe) ----
- kiln_creatures.h `KilnCreaturesApi.template_at` @24 — sorted by name
- kiln_creatures.h `KilnCreaturesApi.template_get` @32
- kiln_creatures.h `KilnCreaturesApi.template_find` @40 — name, display name, GUID text
- kiln_creatures.h `KilnCreaturesApi.inspect` @48 — NOT_FOUND: no such entity.
- kiln_creatures.h `KilnCreaturesApi.set` @56 — Sets a value as 'owner''s layer (owner 0 = a direct write without layer: not reverted by anyone).
- kiln_creatures.h `KilnCreaturesApi.get` @64 — The current value.
- kiln_creatures.h `KilnCreaturesApi.reset` @72 — Drops owner's layer(s): field 0 = all fields of that entity; entity 0 = all entities.
- kiln_creatures.h `KilnCreaturesApi.ai_set` @80 — component: template component short name ("Enemy01BehaviorState"); path: reflection path inside it...
- kiln_creatures.h `KilnCreaturesApi.ai_get` @88 — Text of the field as this entity reads it now (its copy, else the shared template). *is_private = 1 when...
- kiln_creatures.h `KilnCreaturesApi.ai_reset` @96 — Drops owner's field overrides (component/path empty = all).
- kiln_creatures.h `KilnCreaturesApi.ai_refresh` @104 — Rebuilds the private copies of one entity (0 = all) from the shared template's current bytes and...
- kiln_creatures.h `KilnCreaturesApi.ai_fields` @112 — Field list of a template component as JSON ([{"path","type","offset","bytes"}...]); thread-safe once the...
- kiln_creatures.h `KilnCreaturesApi.release_owner` @120 — Drops everything 'owner' set (values, copies, readiness): a mod unloading.
- kiln_creatures.h `KilnCreaturesApi.set_listener` @128 — One listener (the managed Kiln.Creatures).
- kiln_creatures.h `KilnCreaturesApi.command` @136 — Queues a command (tick).
- kiln_creatures.h `KilnCreaturesApi.set_ready` @144 — Lab readiness switch (tick).
- kiln_creatures.h `KilnCreaturesApi.arsenal_of` @152 — The arsenal GUID an entity's enemy AI reads now (its private EnemyArsenalReference copy or the...
- kiln_creatures.h `KilnCreaturesApi.npc_command` @160 — Appended: sends the game's NpcCommandEvent in the next sim step (any thread).
- kiln_creatures.h `KilnCreaturesApi.arsenal_define` @168 — What these are and how a mod uses them: docs/kiln/services/creatures.md "Rules and arsenals".
- kiln_creatures.h `KilnCreaturesApi.arsenal_remove` @176 — Drops owner's additions (guid NULL: all of them).
- kiln_creatures.h `KilnCreaturesApi.arsenal_info` @184 — The arsenal as the AI reads it now, as JSON: { "guid", "kiln", "attacks": [{ "index", "guid", "sequence"...
- kiln_creatures.h `KilnCreaturesApi.arsenal_refresh` @192 — Rebuilds Kiln's arsenals from the game's current data (after a kiln.content change).
- kiln_creatures.h `KilnCreaturesApi.arsenal_selftest` @200 — Reads every arsenal of the registry and writes it back through the writer in memory; counts those not...
- kiln_creatures.h `KilnCreaturesApi.rules_drain` @208 — Work for the tick side: actions the job threads queued, conditions a creature asked for.
- kiln_creatures.h `KilnCreaturesApi.rules_condition_set` @216 — Publishes a condition's value for one creature (slot 0..255).
- kiln_creatures.h `KilnCreaturesApi.rules_condition_clear` @224 — Forgets a condition slot on every creature, and its counters.
- kiln_creatures.h `KilnCreaturesApi.rules_condition_stats` @232 — Times a slot was evaluated on job threads, and came out true.
- kiln_creatures.h `KilnCreaturesApi.rules_pump` @240 — flags bit 0: run the listener every Pre (conditions and picks due on a schedule); bit 1: time the...
- kiln_creatures.h `KilnCreaturesApi.rules_phase_set` @248 — The creature's Kiln phase: FNV-1a of a phase GUID, 0 = none.
- kiln_creatures.h `KilnCreaturesApi.rules_phase_get` @256
- kiln_creatures.h `KilnCreaturesApi.rules_status` @264 — Counters and what resolved.
- kiln_creatures.h `KilnCreaturesApi.rules_job_handler` @272 — Handler for KILN_RC_JOB / KILN_RA_JOB kinds, id 1..63, on the game's job threads inside the sim step: read...
- kiln_creatures.h `KilnCreaturesApi.attack_override` @280 — Attack override of 'owner' for one creature; the newest owner's wins. override NULL drops owner's.
- kiln_creatures.h `KilnCreaturesApi.attack_pick_arsenal` @288 — Marks an arsenal as "picked by code": until a creature using it has an override (even an empty one), its...
- kiln_creatures.h `KilnCreaturesApi.ai_switch` @296 — The game's AI switch for one creature (DynamicEnemy01BehaviorState.enabled), a layer of 'owner': off while...
- kiln_creatures.h `KilnCreaturesApi.ai_switch_get` @304 — 1 when the AI runs, 0 when it is off.
- kiln_creatures.h `KilnCreaturesApi.target_prefer` @312 — Holds 'target' as the creature's combat target for 'seconds': the game keeps a target while its next...

## services/kiln_decisions.h — service `kiln.decisions`
### KilnDecisionsApiV1 — 184 bytes
- kiln_decisions.h `KilnDecisionsApiV1.supported_kinds` @8 `0.2.4` — Bit n is set when kind n has a producer that is on: for a hooked kind (1, 2) its symbols resolved, and its...
- kiln_decisions.h `KilnDecisionsApiV1.attach` @16 `0.2.4` — Makes 'dispatcher' the one consumer.
- kiln_decisions.h `KilnDecisionsApiV1.resolve_entity` @24 `0.2.4` — Refreshes and returns the metadata row of one entity without a discovery scan. position may be null.
- kiln_decisions.h `KilnDecisionsApiV1.interest` @32 `0.2.4` — Replaces the consumer's interest in one kind: all entities, or a copied list (at most...
- kiln_decisions.h `KilnDecisionsApiV1.enqueue_prepared` @40 `0.2.4` — Pre adapters only, kinds 16, 17, 18: copies the request and assigns request_id, world_epoch, capture_step...
- kiln_decisions.h `KilnDecisionsApiV1.begin_requests` @48 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.read_requests` @56 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.ack_requests` @64 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.answer` @72 `0.2.4` — Answers one request. receipt->accepted is 1 when this call decided it, 0 when it was terminal already or...
- kiln_decisions.h `KilnDecisionsApiV1.begin_outcomes` @80 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.read_outcomes` @88 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.ack_outcomes` @96 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.reevaluate_pickup` @104 `0.2.4` — Forgets what the walk-over producer remembers about (drop, receiver) so the next exposure asks again.
- kiln_decisions.h `KilnDecisionsApiV1.detach` @112 `0.2.4` — Releases every pending hold and the consumer's interests.
- kiln_decisions.h `KilnDecisionsApiV1.window_begin` @120 `0.2.4` — Core only: after all Pre subscribers / after WaitWorldJobs, before the Post subscribers.
- kiln_decisions.h `KilnDecisionsApiV1.window_end` @128 `0.2.4`
- kiln_decisions.h `KilnDecisionsApiV1.set_death_guard` @136 `0.2.4` — ======== appended: the native guard seam (19d-pets).
- kiln_decisions.h `KilnDecisionsApiV1.enqueue_native` @144 `0.2.4` — ======== appended: the verdict route of native Pre adapters (kind 18, kiln.items' equipment sets).
- kiln_decisions.h `KilnDecisionsApiV1.prepare_kind` @152 `0.2.4` — ======== appended since 0.2.0-dev.29: producers that start when a mod listens, and the core's window calls.
- kiln_decisions.h `KilnDecisionsApiV1.window_wanted` @160 `0.2.4` — Core only, after the last Pre subscriber of a step: nonzero when the step needs window_begin and...
- kiln_decisions.h `KilnDecisionsApiV1.window_faulted` @168 `0.2.4` — Core only: a window call of this service faulted ('code' is the exception code); the core makes no further...
- kiln_decisions.h `KilnDecisionsApiV1.is_stopped` @176 `0.2.4` — Any thread, lock-free.

## services/kiln_eml.h — service `kiln.eml`
### KilnEmlApi — 40 bytes
- kiln_eml.h `KilnEmlApi.add_mod` @8 `0.2.0` — Adds one EML mod folder (with mod.json) or .zip to this start's run, from an engine plugin that owns the...
- kiln_eml.h `KilnEmlApi.phase` @16 `0.2.0` — KILN_EML_*
- kiln_eml.h `KilnEmlApi.state_json` @24 `0.2.0` — The run's result as JSON, the same text as \<game>/kiln/state/eml.json (format 1: mods[] with id, version...
- kiln_eml.h `KilnEmlApi.stand_aside` @32 `0.2.0` — v2: reserve the startup data writer for a runtime that runs EML itself.

## services/kiln_eml_archive.h — service `kiln.eml.archive`
### KilnEmlArchiveApiV1 — 152 bytes
- kiln_eml_archive.h `KilnEmlArchiveApiV1.acquire` @8 `0.2.12` — Four-slot OFFSETS remain 8/16/24/32 (40-byte extent), NOT old call ABI.
- kiln_eml_archive.h `KilnEmlArchiveApiV1.identity` @16 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.resource_json` @24 `0.2.12` — kiln.boot query grammar (boot_service.cpp:210-251): resource:\<lowercase canonical GUID>/\<8 hex qualified...
- kiln_eml_archive.h `KilnEmlArchiveApiV1.release` @32 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.registry_metadata` @40 `0.2.12` — since sfi -- immutable read subset. limit=0: header only (types=[]). first/limit are ordered registry...
- kiln_eml_archive.h `KilnEmlArchiveApiV1.find_resource` @48 `0.2.12` — since sfi -- find/read include finalized staged entries in this generation; list is reference-catalog...
- kiln_eml_archive.h `KilnEmlArchiveApiV1.list_resources` @56 `0.2.12` — capacity/required in ELEMENTS; fixed sizeof(KilnAssetKey) stride.
- kiln_eml_archive.h `KilnEmlArchiveApiV1.read_resource` @64 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.content_read_range` @72 `0.2.12` — Game-reference contents only, never new staged content or arbitrary files.
- kiln_eml_archive.h `KilnEmlArchiveApiV1.list_contents` @80 `0.2.12` — Entire reference-catalog suffix from first, capacity/required in ELEMENTS, fixed sizeof(KilnContentHash)...
- kiln_eml_archive.h `KilnEmlArchiveApiV1.value_decode` @88 `0.2.12` — since sfi -- stateless neutral-tree codecs; registry is an issued view.
- kiln_eml_archive.h `KilnEmlArchiveApiV1.value_encode` @96 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.loaded_field` @104 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.attribute_catalog` @112 `0.2.12` — since sfi -- immutable metadata/calculation over caller-owned scalar bits. selector_json has its OWN...
- kiln_eml_archive.h `KilnEmlArchiveApiV1.attribute_resolve` @120 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.attribute_evaluate` @128 `0.2.12`
- kiln_eml_archive.h `KilnEmlArchiveApiV1.error_copy` @136 `0.2.12` — since sfi -- pure immutable error transport, never IO/codec replay.
- kiln_eml_archive.h `KilnEmlArchiveApiV1.error_release` @144 `0.2.12`

## services/kiln_eml_startup.h
### KilnEmlStartupApiV1 — 56 bytes
- kiln_eml_startup.h `KilnEmlStartupApiV1.attach` @8 `0.2.12`
- kiln_eml_startup.h `KilnEmlStartupApiV1.witness_wait` @16 `0.2.12`
- kiln_eml_startup.h `KilnEmlStartupApiV1.witness_recheck` @24 `0.2.12`
- kiln_eml_startup.h `KilnEmlStartupApiV1.native_terminal` @32 `0.2.12`
- kiln_eml_startup.h `KilnEmlStartupApiV1.outcome` @40 `0.2.12`
- kiln_eml_startup.h `KilnEmlStartupApiV1.published_identity` @48 `0.2.12`

## services/kiln_eml_values.h — service `kiln.eml.values`
### KilnEmlValuesApiV1 — 144 bytes
- kiln_eml_values.h `KilnEmlValuesApiV1.arena_create` @8 `0.2.12` — Returns a creator-host CONTROL lease, not the public identity.arena ID.
- kiln_eml_values.h `KilnEmlValuesApiV1.arena_close` @16 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.arena_collect` @24 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_open` @32 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_info` @40 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_read` @48 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_child` @56 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_set` @64 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_clone` @72 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_export` @80 `0.2.12` — selected_type empty: convert this already-validated graph's exact type.
- kiln_eml_values.h `KilnEmlValuesApiV1.value_close` @88 `0.2.12` — Closes ANY lease kind: value, iterator, immutable result/payload/error.
- kiln_eml_values.h `KilnEmlValuesApiV1.value_result_copy` @96 `0.2.12` — Immutable retained result only: no second lookup/stringify/pop/next.
- kiln_eml_values.h `KilnEmlValuesApiV1.value_array_push` @104 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_array_insert` @112 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_array_pop` @120 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_array_remove` @128 `0.2.12`
- kiln_eml_values.h `KilnEmlValuesApiV1.value_array_clear` @136 `0.2.12`

## services/kiln_engine.h — service `kiln.engine`
### KilnEngineApi — 24 bytes
- kiln_engine.h `KilnEngineApi.identity` @8 `0.2.10` — The image and the selected profile.
- kiln_engine.h `KilnEngineApi.gate_stats` @16 `0.2.10` — A copy of the counters.

## services/kiln_example.h — service `kiln.example`
### KilnExampleApi — 16 bytes
- kiln_example.h `KilnExampleApi.steps` @8

## services/kiln_gamedata.h — service `kiln.gamedata`
### KilnGamedataApi — 160 bytes
- kiln_gamedata.h `KilnGamedataApi.status` @8 — JSON {ready, sections: {name: {available, count, changed}}, configs, externals, definitions, detail}.
- kiln_gamedata.h `KilnGamedataApi.count` @16
- kiln_gamedata.h `KilnGamedataApi.entry_at` @24
- kiln_gamedata.h `KilnGamedataApi.find` @32 — key: name (exact, then ignoring case), "0x" id, GUID ("\<guid>#k" sub-sequences), "template:\<name>"...
- kiln_gamedata.h `KilnGamedataApi.get` @40 — A leaf's current value and the game's original.
- kiln_gamedata.h `KilnGamedataApi.get_json` @48 — Any node as JSON: {"path", "type", "value", "original" (leaves), "writable", "address", "config"}.
- kiln_gamedata.h `KilnGamedataApi.set` @56
- kiln_gamedata.h `KilnGamedataApi.set_json` @64 — value_json: any JSON value for the path (fans out over [*] and objects). report: {"applied", "errors"}.
- kiln_gamedata.h `KilnGamedataApi.reset` @72 — Takes owner's layers of that path (and below) off.
- kiln_gamedata.h `KilnGamedataApi.fields` @80 — Every leaf below 'prefix' (empty: the whole entry) as a JSON array [{"path", "type", "value"...
- kiln_gamedata.h `KilnGamedataApi.dump` @88 — Writes entries as JSON (key empty: the whole section, one entry per line) to path (empty...
- kiln_gamedata.h `KilnGamedataApi.configs` @96 — The config vocabulary: [{"id": "0xaf236ece", "names": ["ManaCost"], "programs": n, "type": "..."}].
- kiln_gamedata.h `KilnGamedataApi.register_section` @104
- kiln_gamedata.h `KilnGamedataApi.unregister_section` @112
- kiln_gamedata.h `KilnGamedataApi.define` @120 — Clones (data-pack "add" lists): a JSON object {"guid", "name", "base", "fields": {...}}; report as...
- kiln_gamedata.h `KilnGamedataApi.definitions` @128
- kiln_gamedata.h `KilnGamedataApi.buff_budget` @136 `0.2.4` — buff clones that answer: appended since 0.2.0-dev.29 (version stays 1).
- kiln_gamedata.h `KilnGamedataApi.define_buff_result` @144 `0.2.4` — Defines a buff clone (clone_json as define(): {"guid", "name", "base", "fields", "displayName"...
- kiln_gamedata.h `KilnGamedataApi.buff_registration` @152 `0.2.4` — The answer for registration_id now (what became of the definition since: registered, superseded by a later...

## services/kiln_gameplay.h — service `kiln.gameplay`
### KilnGameplayApi — 144 bytes
- kiln_gameplay.h `KilnGameplayApi.subscribe` @8 — kinds_mask: bit (1 \<\< KILN_GAMEPLAY_*).
- kiln_gameplay.h `KilnGameplayApi.unsubscribe` @16
- kiln_gameplay.h `KilnGameplayApi.zone_add` @24 — Zones. *out_id never 0.
- kiln_gameplay.h `KilnGameplayApi.zone_update` @32
- kiln_gameplay.h `KilnGameplayApi.zone_remove` @40
- kiln_gameplay.h `KilnGameplayApi.zone_denies` @48 — Would 'action' (one KILN_GAMEPLAY_DENY_* bit) by 'player' at 'position' be denied? *out_zone = the zone.
- kiln_gameplay.h `KilnGameplayApi.inject` @56 — Queues one request event; Kiln's program sends it in the next step, so it is seen (and may be canceled) in...
- kiln_gameplay.h `KilnGameplayApi.watch` @64 — Adds / removes an entity to the teleport watch list (players are always watched).
- kiln_gameplay.h `KilnGameplayApi.status` @72
- kiln_gameplay.h `KilnGameplayApi.recipe` @80 — Recipes.
- kiln_gameplay.h `KilnGameplayApi.workshop_recipes` @88 — The recipe ids a workshop offers; *out_count = how many (BUFFER_TOO_SMALL when more than capacity).
- kiln_gameplay.h `KilnGameplayApi.entity_filter` @96 — Which KILN_GAMEPLAY_ENTITY_* categories ENTITY_JOINED / ENTITY_LEFT report (0 = none: the hooks only test...
- kiln_gameplay.h `KilnGameplayApi.spawn_replace` @104 — Natural-spawn template replacement, decided inside random_spawner's QueueSpawn call (worker thread, from...
- kiln_gameplay.h `KilnGameplayApi.spawn_replace_clear` @112 — Removes every replacement rule of 'owner' (a mod unloading).
- kiln_gameplay.h `KilnGameplayApi.component_change` @120 `0.2.0` — Queues one change of an interaction-offer component on 'entity' (never a player).
- kiln_gameplay.h `KilnGameplayApi.request_build` @128 `0.2.4` — First-use build interest, AnyThread.
- kiln_gameplay.h `KilnGameplayApi.request_hooks` @136 `0.2.4` — AnyThread.

## services/kiln_gemcraft.h — service `kiln.gemcraft`
### KilnGemcraftApi — 120 bytes
- kiln_gemcraft.h `KilnGemcraftApi.status` @8 `0.2.5`
- kiln_gemcraft.h `KilnGemcraftApi.submit` @16 `0.2.5` — Queues a code action.
- kiln_gemcraft.h `KilnGemcraftApi.cancel` @24 `0.2.5` — Drops a code action's callback (the action may still run).
- kiln_gemcraft.h `KilnGemcraftApi.hold` @32 `0.2.5` — Holds players' new forge actions for the next 'steps' steps (1..250; 0 stops at once: what is held runs).
- kiln_gemcraft.h `KilnGemcraftApi.held` @40 `0.2.5` — The actions held since the last call (each once). *out_count = how many were written...
- kiln_gemcraft.h `KilnGemcraftApi.decide` @48 `0.2.5` — The answer for a held action (player, version): cancel 1 = refuse it, 0 = let it run.
- kiln_gemcraft.h `KilnGemcraftApi.watch` @56 `0.2.5` — 1: players' actions that ran are reported by crafted() (code actions always are).
- kiln_gemcraft.h `KilnGemcraftApi.crafted` @64 `0.2.5` — The actions that ran since the last call, players' (while watched) and code's (DONE only), and every...
- kiln_gemcraft.h `KilnGemcraftApi.extras` @72 `0.2.7` — appended in 0.2.0-dev.29.15 (header.size covers them) ---- Which appended parts run here...
- kiln_gemcraft.h `KilnGemcraftApi.offer` @80 `0.2.7` — Offers gems to players (any number of players and holders in one call): each gets a new token and its expiry.
- kiln_gemcraft.h `KilnGemcraftApi.withdraw` @88 `0.2.7` — Withdraws the owner's offers (owner 0: every owner's) to 'player' (0: every player) in 'holder' (0: every...
- kiln_gemcraft.h `KilnGemcraftApi.sources` @96 `0.2.7` — The live offers to 'player' (every owner's), in offer order.
- kiln_gemcraft.h `KilnGemcraftApi.payers` @104 `0.2.7` — Who would pay for 'player' with pay_from / pay_holder now, in order (what a request resolves in the step...
- kiln_gemcraft.h `KilnGemcraftApi.forge_material` @112 `0.2.7` — The forge's own upgrade material (ShroudForge.upgradeMaterial), what payers other than the player pay in.
