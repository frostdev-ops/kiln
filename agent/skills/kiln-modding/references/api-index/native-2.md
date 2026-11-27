# Native ABI rows (mod API 0.2.26): part 2 of 3

One line per row: header, `Table.row`, @offset, mod API, comment (native.md says more).

## services/kiln_hooks.h — service `kiln.hooks`
### KilnHooksApi — 328 bytes
- kiln_hooks.h `KilnHooksApi.image_base` @8
- kiln_hooks.h `KilnHooksApi.image_size` @16
- kiln_hooks.h `KilnHooksApi.symbol` @24
- kiln_hooks.h `KilnHooksApi.constant` @32
- kiln_hooks.h `KilnHooksApi.scan` @40 — Scans the image's .text with sigdb pattern syntax (holes act as wildcards).
- kiln_hooks.h `KilnHooksApi.scan_hole` @48 — Reads a named hole from a pattern matched at 'match' (rel32 holes give the absolute target).
- kiln_hooks.h `KilnHooksApi.load_symbols` @56 — mod-shipped symbol fragments ---- Loads a JSON sigdb file (same format as sigdb/server.json) as fragment...
- kiln_hooks.h `KilnHooksApi.unload_symbols` @64
- kiln_hooks.h `KilnHooksApi.read` @72 — memory (SEH-guarded) ----
- kiln_hooks.h `KilnHooksApi.write` @80
- kiln_hooks.h `KilnHooksApi.patch` @88 — Overwrites n bytes at addr, remembering the originals; restored on unpatch, revoke or revoke_owner.
- kiln_hooks.h `KilnHooksApi.unpatch` @96
- kiln_hooks.h `KilnHooksApi.detour` @104 — detour: install (or join) an inline detour on 'target'; higher priority runs first. mid: a mid-function...
- kiln_hooks.h `KilnHooksApi.mid` @112
- kiln_hooks.h `KilnHooksApi.call_original` @120
- kiln_hooks.h `KilnHooksApi.revoke` @128
- kiln_hooks.h `KilnHooksApi.revoke_owner` @136
- kiln_hooks.h `KilnHooksApi.stats` @144
- kiln_hooks.h `KilnHooksApi.raw_detour` @152 `0.2.0` — ======== v2 (KILN_HOOKS_VERSION 2): check header.size covers these before calling ======== The registry...
- kiln_hooks.h `KilnHooksApi.caller` @160 `0.2.0`
- kiln_hooks.h `KilnHooksApi.detour2` @168 `0.2.0`
- kiln_hooks.h `KilnHooksApi.call_original2` @176 `0.2.0`
- kiln_hooks.h `KilnHooksApi.mid2` @184 `0.2.0`
- kiln_hooks.h `KilnHooksApi.map_json` @192 `0.2.0`
- kiln_hooks.h `KilnHooksApi.detour_typed` @200 `0.2.3` — ======== typed links (since 0.2.0-dev.28): check header.size covers these before calling ========...
- kiln_hooks.h `KilnHooksApi.call_original_frame` @208 `0.2.3`
- kiln_hooks.h `KilnHooksApi.call_with_frame` @216 `0.2.3`
- kiln_hooks.h `KilnHooksApi.fingerprint` @224 `0.2.3`
- kiln_hooks.h `KilnHooksApi.typed_refusal` @232 `0.2.3`
- kiln_hooks.h `KilnHooksApi.subscribe_owner_retiring` @240 `0.2.3` — ======== owner cleanup (since 0.2.0-dev.28): check header.size covers these before calling ======== A...
- kiln_hooks.h `KilnHooksApi.unsubscribe_owner_retiring` @248 `0.2.3`
- kiln_hooks.h `KilnHooksApi.mid2_checked` @256 `0.2.4` — ======== checked mid installation (since 0.2.0-dev.29, package 07): check header.size covers it ========...
- kiln_hooks.h `KilnHooksApi.validate_typed` @264 `0.2.4` — ======== typed safety extension (since 0.2.0-dev.29): check header.size covers each member before reading...
- kiln_hooks.h `KilnHooksApi.sig_status` @272 `0.2.4`
- kiln_hooks.h `KilnHooksApi.typed_install_ready` @280 `0.2.4`
- kiln_hooks.h `KilnHooksApi.detour_typed_checked` @288 `0.2.4` — ======== checked typed installation (since 0.2.0-dev.29): check header.size covers it before calling...
- kiln_hooks.h `KilnHooksApi.read_original` @296 `0.2.11` — ======== original code (since 0.2.0-dev.29.19): check header.size covers each member before reading it...
- kiln_hooks.h `KilnHooksApi.code_state` @304 `0.2.11`
- kiln_hooks.h `KilnHooksApi.open_module` @312 `0.2.11` — ======== hooks inside other modules (since 0.2.0-dev.29.19): check header.size covers each member ========...
- kiln_hooks.h `KilnHooksApi.module_scan` @320 `0.2.11`

## services/kiln_item_costs.h — service `kiln.item_costs`
### KilnItemCostsApi — 40 bytes
- kiln_item_costs.h `KilnItemCostsApi.submit` @8 `0.2.25` — Queues one cost; *ticket = its ticket (never 0).
- kiln_item_costs.h `KilnItemCostsApi.peek` @16 `0.2.25` — The receipt now (QUEUED until it ran).
- kiln_item_costs.h `KilnItemCostsApi.acknowledge` @24 `0.2.25` — The owner has the receipt: it is let go (its request id stays closed).
- kiln_item_costs.h `KilnItemCostsApi.status` @32 `0.2.25` — What can be submitted now, and the counters.

## services/kiln_items.h — service `kiln.items`
### KilnItemsApi — 392 bytes
- kiln_items.h `KilnItemsApi.give` @8 — Queues a give / take. *out_ticket (optional) = the request's ticket (never 0).
- kiln_items.h `KilnItemsApi.take` @16
- kiln_items.h `KilnItemsApi.cancel` @24 — Drops a request's callback (the transaction may still run if it already started).
- kiln_items.h `KilnItemsApi.cancel_owner` @32 — Drops the callbacks of every request of 'owner' and ends its fuel_keep entries and its transfer rules...
- kiln_items.h `KilnItemsApi.read` @40 — The inventory of 'entity': *info (optional) and up to 'capacity' slots (every slot of every linked 8-slot...
- kiln_items.h `KilnItemsApi.find_inventories` @48 — Entities with an initialized InventorySetup matching 'flags' (KILN_ITEMS_FIND_*; 0 = all), in no...
- kiln_items.h `KilnItemsApi.item_count` @56 — Catalog (the server world's item registry; empty until it has loaded, a few steps after the world starts).
- kiln_items.h `KilnItemsApi.item_at` @64 — index \< item_count(), sorted by name
- kiln_items.h `KilnItemsApi.item_info` @72
- kiln_items.h `KilnItemsApi.find_item` @80 — debug name: exact, else case-insensitive
- kiln_items.h `KilnItemsApi.item_id_of_guid` @88 — FNV-1a-32, as the game
- kiln_items.h `KilnItemsApi.status` @96
- kiln_items.h `KilnItemsApi.fuel` @104 `0.2.0` — Queues a Fuel write; it runs in the next sim step inside the program, 'done' (optional) in the Pre after.
- kiln_items.h `KilnItemsApi.fuel_read` @112 `0.2.0` — An entity's water: *current and *max in units (optional).
- kiln_items.h `KilnItemsApi.fuel_keep` @120 `0.2.0` — A refill policy: every sim step, inside the program after the game's fill and pour, the containers of...
- kiln_items.h `KilnItemsApi.fuel_keep_end` @128 `0.2.0` — KILN_ERR_NOT_FOUND: no such keep
- kiln_items.h `KilnItemsApi.move` @136 `0.2.0` — Queues a move (see "moves"). *out_ticket (optional) = its ticket.
- kiln_items.h `KilnItemsApi.container_name` @144 `0.2.0` — The custom name a player gave an object with the Rename button (a chest, a station...), as UTF-8...
- kiln_items.h `KilnItemsApi.transfer_rule` @152 `0.2.0` — Transfer rules, handlers and reports (see "transfer events and rules").
- kiln_items.h `KilnItemsApi.transfer_listen` @160 `0.2.0`
- kiln_items.h `KilnItemsApi.transfer_report` @168 `0.2.0`
- kiln_items.h `KilnItemsApi.transfer_end` @176 `0.2.0` — Ends a rule, handler or report listener; the hooks stay installed (they are never removed, so another...
- kiln_items.h `KilnItemsApi.status2` @184 `0.2.0`
- kiln_items.h `KilnItemsApi.peek_result` @192 `0.2.0` — Appended v3: copies a committed give/take result before its callback is delivered, or while its callback...
- kiln_items.h `KilnItemsApi.read_instances` @200 `0.2.1` — Reads 'count' item entities: items[i].entity in, every other field out (zeroed first).
- kiln_items.h `KilnItemsApi.read_holder` @208 `0.2.1` — Every item entity in 'holder''s inventories (Virtual links expanded), as read_instances reads it, in...
- kiln_items.h `KilnItemsApi.instances_status` @216 `0.2.1` — The stamps' state.
- kiln_items.h `KilnItemsApi.equip` @224 `0.2.2` — Queues an equip or unequip (see "equipment"). *out_ticket (optional) = its ticket.
- kiln_items.h `KilnItemsApi.equip_status` @232 `0.2.2` — any thread
- kiln_items.h `KilnItemsApi.set_capture` @240 `0.2.4` — Captures a snapshot of 'player''s inventory for 'owner' (see "equipment sets"); out->size in.
- kiln_items.h `KilnItemsApi.set_release_snapshot` @248 `0.2.4` — Releases a snapshot 'owner' captured.
- kiln_items.h `KilnItemsApi.set_status` @256 `0.2.4` — The state of equipment sets.
- kiln_items.h `KilnItemsApi.set_plan` @264 `0.2.4` — Plans 'count' entries (KilnSetOpV1, kiln_decisions.h; omitted slots stay as they are) against the snapshot...
- kiln_items.h `KilnItemsApi.equip_set` @272 `0.2.4` — Queues a set (see "Applying"). *ticket (optional) = its ticket, from the service's one ticket counter.
- kiln_items.h `KilnItemsApi.set_cancel` @280 `0.2.4` — Ends a queued set (not started) Dropped at the next Pre; its callback is kept and runs once.
- kiln_items.h `KilnItemsApi.socket_roll` @288 `0.2.5` — Sets 'owner''s socket rolls (see "gem sockets"); NULL or every chance 0 ends them.
- kiln_items.h `KilnItemsApi.gear_write` @296 `0.2.12` — Queues a gear write (see "gear"). *out_ticket (optional) = its ticket.
- kiln_items.h `KilnItemsApi.gear_read` @304 `0.2.12` — An item entity's gear now, through GetComponent (no write): *out zeroed, then filled. stamp 0 = any...
- kiln_items.h `KilnItemsApi.gear_status` @312 `0.2.12` — any thread
- kiln_items.h `KilnItemsApi.rarity_rolls` @320 `0.2.12` — Replaces 'owner''s rules (see "rarity rolls"): 'count' entries of 'roll_size' bytes...
- kiln_items.h `KilnItemsApi.roll_status` @328 `0.2.12` — any thread
- kiln_items.h `KilnItemsApi.gear_upgrade` @336 `0.2.12` — Queues an upgrade. *out_ticket (optional) = its ticket.
- kiln_items.h `KilnItemsApi.gear_scale` @344 `0.2.12` — Sets an item entity's power and/or armour scale (s->flags names which; 1 = the item's own value, which...
- kiln_items.h `KilnItemsApi.gear_scale_read` @352 `0.2.12` — An item entity's scales: *out zeroed, then filled (size, entity, stamp, flags = the scales other than 1...
- kiln_items.h `KilnItemsApi.scale_status` @360 `0.2.12` — any thread
- kiln_items.h `KilnItemsApi.container_rename` @368 `0.2.13` — Checks the request now and queues the rename for the next Pre, where it runs through the game's own rename.
- kiln_items.h `KilnItemsApi.rename_status` @376 `0.2.13` — any thread
- kiln_items.h `KilnItemsApi.give_equip` @384 `0.2.15` — Since 0.2.0-dev.30.6; appended at 384, table end 392.

## services/kiln_items_inventory_kinds.h — service `kiln.items.inventory_kinds`
### KilnItemsInventoryKindsApi — 16 bytes
- kiln_items_inventory_kinds.h `KilnItemsInventoryKindsApi.find` @8 `0.2.17`

## services/kiln_items_storage.h — service `kiln.items.storage`
### KilnItemsStorageApi — 128 bytes
- kiln_items_storage.h `KilnItemsStorageApi.register_storage` @8 `0.2.16` — Registers one storage for every player.
- kiln_items_storage.h `KilnItemsStorageApi.holder` @16 `0.2.16` — The player's holder of a registration.
- kiln_items_storage.h `KilnItemsStorageApi.state` @24 `0.2.16` — The player's state of a registration (any status).
- kiln_items_storage.h `KilnItemsStorageApi.set_ref` @32 `0.2.16` — Points the player's reference slot equip_slot (keen::EquipmentSlot 12, 14, 15 or 16) at the storage's cell...
- kiln_items_storage.h `KilnItemsStorageApi.status` @40 `0.2.16` — The service's state.
- kiln_items_storage.h `KilnItemsStorageApi.list` @48 `0.2.16` — Every key of the player: its registrations (any status) and its parked keys, sorted by key.
- kiln_items_storage.h `KilnItemsStorageApi.next_event` @56 `0.2.16` — The oldest event with seq > after_seq (the queue keeps the newest 256).
- kiln_items_storage.h `KilnItemsStorageApi.return_parked` @64 `0.2.16` — Rebuilds every parked record of 'key' (any reason) into 'target', in the next sim step: the player itself...
- kiln_items_storage.h `KilnItemsStorageApi.persist_bind` @72 `0.2.16` — Claims the persist rows for 'owner' and sets its stage function (fn = NULL keeps the claim without one...
- kiln_items_storage.h `KilnItemsStorageApi.take_frozen` @80 `0.2.16` — Copies batch 'tag': its header, captures, entries and records.
- kiln_items_storage.h `KilnItemsStorageApi.release_frozen` @88 `0.2.16` — Drops batch 'tag': Kiln.Saves has it, committed or staged on its own.
- kiln_items_storage.h `KilnItemsStorageApi.witness_of` @96 `0.2.16` — The current join of the game's player slot 'slot': W_g and the character the game read.
- kiln_items_storage.h `KilnItemsStorageApi.restore` @104 `0.2.16` — Restores the chosen document for join 'join' of slot 'slot' (once per join, at any time after the game...
- kiln_items_storage.h `KilnItemsStorageApi.park` @112 `0.2.16` — Parks the player's storage 'key' in the next sim step: its holder's contents are frozen into a PARKED...
- kiln_items_storage.h `KilnItemsStorageApi.unpark` @120 `0.2.16` — Puts the player's parked records of 'key' (any reason) back into its holder in the next sim step: each...

## services/kiln_labbot.h — service `kiln.labbot`
### KilnLabBotApi — 56 bytes
- kiln_labbot.h `KilnLabBotApi.status` @8 `0.2.25` — Any thread.
- kiln_labbot.h `KilnLabBotApi.join` @16 `0.2.25` — Starts a bot's join; *out_id names it.
- kiln_labbot.h `KilnLabBotApi.leave` @24 `0.2.25` — Ends the bot's game (KILN_LABBOT_LEAVE_*): a logout unless DROP.
- kiln_labbot.h `KilnLabBotApi.info` @32 `0.2.25` — Any thread.
- kiln_labbot.h `KilnLabBotApi.list` @40 `0.2.25` — Every remembered bot (live ones and the last LEFT/FAILED of each slot), newest first; *count = how many...
- kiln_labbot.h `KilnLabBotApi.input` @48 `0.2.25` — since dev.32 labbot-2: check header.size ---- Sets parts of the bot's client input (KILN_LABBOT_INPUT_*)...

## services/kiln_launch.h — service `kiln.launch`
### KilnLaunchApi — 24 bytes
- kiln_launch.h `KilnLaunchApi.status` @8 `0.2.12` — The frozen record.
- kiln_launch.h `KilnLaunchApi.notice_copy` @16 `0.2.12` — The notice for the player about this start's selection, for a loader that has to show it itself when...

## services/kiln_log.h — service `kiln.log`
### KilnLogApi — 16 bytes
- kiln_log.h `KilnLogApi.line_time` @8 `0.2.4` — In a KilnLogLineFn callback, on Kiln's log thread: when the game logged the line being delivered (see above).

## services/kiln_markers.h — service `kiln.markers`, `kiln.markers.bridge`
### KilnMarkersApi — 144 bytes
- kiln_markers.h `KilnMarkersApi.bind_owner` @8 `0.2.17` — bind_owner: KILN_OK (also again with the same namespace); KILN_ERR_INVALID_ARGUMENT: owner 0, a namespace...
- kiln_markers.h `KilnMarkersApi.release_owner` @16 `0.2.17`
- kiln_markers.h `KilnMarkersApi.catalog_count` @24 `0.2.17` — The icon catalog: 0 and 0 while it warms up. catalog_at: KILN_ERR_NOT_FOUND past the end. catalog_resolve...
- kiln_markers.h `KilnMarkersApi.catalog_at` @32 `0.2.17`
- kiln_markers.h `KilnMarkersApi.catalog_resolve` @40 `0.2.17`
- kiln_markers.h `KilnMarkersApi.world_epoch` @48 `0.2.17` — world_epoch: the service's epoch of that world kind's current world, 0 when there is none.
- kiln_markers.h `KilnMarkersApi.capture_anchor` @56 `0.2.17`
- kiln_markers.h `KilnMarkersApi.release_anchor` @64 `0.2.17`
- kiln_markers.h `KilnMarkersApi.set_local` @72 `0.2.17` — The local store (client): validate, reserve, intern and commit under a short lock; no game call, no callback.
- kiln_markers.h `KilnMarkersApi.remove_local` @80 `0.2.17`
- kiln_markers.h `KilnMarkersApi.clear_local` @88 `0.2.17`
- kiln_markers.h `KilnMarkersApi.send` @96 `0.2.17` — Server layers (recipient 0 = the broadcast layer).
- kiln_markers.h `KilnMarkersApi.unsend` @104 `0.2.17`
- kiln_markers.h `KilnMarkersApi.clear_sent` @112 `0.2.17`
- kiln_markers.h `KilnMarkersApi.suppress_sent` @120 `0.2.17`
- kiln_markers.h `KilnMarkersApi.subscribe` @128 `0.2.17` — subscribe: every change of every publisher, as immutable copies valid for the call.
- kiln_markers.h `KilnMarkersApi.unsubscribe` @136 `0.2.17`
### KilnMarkersBridgeApi — 48 bytes
- kiln_markers.h `KilnMarkersBridgeApi.attach` @8 `0.2.17`
- kiln_markers.h `KilnMarkersBridgeApi.peer_up` @16 `0.2.17`
- kiln_markers.h `KilnMarkersBridgeApi.peer_down` @24 `0.2.17`
- kiln_markers.h `KilnMarkersBridgeApi.receive` @32 `0.2.17`
- kiln_markers.h `KilnMarkersBridgeApi.pump` @40 `0.2.17`

## services/kiln_markers_lab.h — service `kiln.markers.lab`
### KilnMarkersLabApi — 32 bytes
- kiln_markers_lab.h `KilnMarkersLabApi.follow_now` @8 `0.2.17` — The follow read of the owner's lease now, as a world's Post does it for a stored record (the lease's world...
- kiln_markers_lab.h `KilnMarkersLabApi.stats` @16 `0.2.17` — Counters (any thread).
- kiln_markers_lab.h `KilnMarkersLabApi.create_raw` @24 `0.2.17` — The game's immediate create on the server world with requested_id (0: the engine picks one), the template...

## services/kiln_mod_values.h — service `kiln.mod-values`
### KilnModValuesApi — 72 bytes
- kiln_mod_values.h `KilnModValuesApi.owner_namespace` @8 `0.2.17` — The owner's namespace as UTF-8 with a NUL. *required (not NULL) gets the bytes it needs, NUL included (at...
- kiln_mod_values.h `KilnModValuesApi.describe` @16 `0.2.17` — Makes local_id known in the owner's namespace without activating it (labels, formats, kind for UI before...
- kiln_mod_values.h `KilnModValuesApi.define` @24 `0.2.17` — Describes and activates local_id (stats.own, server tick thread).
- kiln_mod_values.h `KilnModValuesApi.read` @32 `0.2.17` — A character's value, also of a character that is offline (stats.own, server tick thread).
- kiln_mod_values.h `KilnModValuesApi.change` @40 `0.2.17` — Changes a value of the owner's own namespace for a character in game (stats.own, server tick thread)....
- kiln_mod_values.h `KilnModValuesApi.read_synced` @48 `0.2.17` — Client, any thread (stats.sync): this player's copy of a value as the server last sent it, read with the...
- kiln_mod_values.h `KilnModValuesApi.subscribe` @56 `0.2.17` — fn(change, user) for each event of event_scope whose key passes qualified_filter: "namespace:id" (one...
- kiln_mod_values.h `KilnModValuesApi.unsubscribe` @64 `0.2.17` — Any thread.

## services/kiln_net.h — service `kiln.net`
### KilnNetApi — 184 bytes
- kiln_net.h `KilnNetApi.status` @8
- kiln_net.h `KilnNetApi.subscribe` @16 — Every frame and peer event (either callback may be NULL).
- kiln_net.h `KilnNetApi.unsubscribe` @24
- kiln_net.h `KilnNetApi.send` @32 — One frame (1..max_frame bytes).
- kiln_net.h `KilnNetApi.peers` @40 — Peers up right now; 'count' gets the total even when it exceeds 'capacity'.
- kiln_net.h `KilnNetApi.peer_info` @48
- kiln_net.h `KilnNetApi.kick` @56 — Server side: refuse a game peer with the game's own login-failure message (reason =...
- kiln_net.h `KilnNetApi.peer_of_entity` @64 — The peer of a connected player's entity (server side).
- kiln_net.h `KilnNetApi.gate_set` @72 — KILN_NET_GATE_OFF lets held peers go on.
- kiln_net.h `KilnNetApi.gate_info` @80
- kiln_net.h `KilnNetApi.gate_release` @88 — Lets a held peer go on: its Gameplay Ready is processed in the next receive step.
- kiln_net.h `KilnNetApi.entity_withheld` @96 — 1 when this server-world entity is withheld from peers without proof, 0 when not, -1 when it can't be read.
- kiln_net.h `KilnNetApi.template_kiln_only` @104 — 1 when this template GUID (game byte order) is one only Kiln has, 0 when not, -1 without kiln.assets.
- kiln_net.h `KilnNetApi.withhold_set` @112 — Whether Kiln-only entities are kept from this peer ('on': a default for a new peer).
- kiln_net.h `KilnNetApi.lab_selftest` @120 — Lab transport only (else KILN_ERR_UNAVAILABLE), tick thread: drives the game's own code on the server...
- kiln_net.h `KilnNetApi.steam_status` @128 `0.2.12` — Steam peer-to-peer messages (see KilnSteamMessagesStatus); added v1 in 0.2.0-dev.30.3 (appended), check...
- kiln_net.h `KilnNetApi.steam_open` @136 `0.2.12`
- kiln_net.h `KilnNetApi.steam_close` @144 `0.2.12`
- kiln_net.h `KilnNetApi.steam_maintain` @152 `0.2.12`
- kiln_net.h `KilnNetApi.steam_send` @160 `0.2.12`
- kiln_net.h `KilnNetApi.steam_receive` @168 `0.2.12`
- kiln_net.h `KilnNetApi.steam_accept` @176 `0.2.12`

## services/kiln_npcai.h — service `kiln.npcai`
### KilnNpcAiApi — 104 bytes
- kiln_npcai.h `KilnNpcAiApi.owner_open` @8 `0.2.0` — An owner for a mod's settings: the same name gives the same owner.
- kiln_npcai.h `KilnNpcAiApi.owner_release` @16 `0.2.0` — Drops everything the owner set.
- kiln_npcai.h `KilnNpcAiApi.relation_set` @24 `0.2.0` — Faction a toward faction b (0..255).
- kiln_npcai.h `KilnNpcAiApi.relation_get` @32 `0.2.0` — The table's relation of a toward b (KILN_REL_GAME when none).
- kiln_npcai.h `KilnNpcAiApi.entity_row` @40 `0.2.0` — Entity e toward every faction: 256 relations (KILN_REL_*), or NULL to clear.
- kiln_npcai.h `KilnNpcAiApi.pair_set` @48 `0.2.0` — Entity e toward entity other, for 'seconds' (0..86400, finite; 0: until cleared).
- kiln_npcai.h `KilnNpcAiApi.pair_clear` @56 `0.2.0` — Clears every pair of e this owner set.
- kiln_npcai.h `KilnNpcAiApi.query` @64 `0.2.0` — What the game's checks answer for entity a about entity b, as the hooks answer them (Kiln's relation, else...
- kiln_npcai.h `KilnNpcAiApi.query_factions` @72 `0.2.0` — The same by faction value, the table, else HostileValue(a, b) and FriendValue(a, b).
- kiln_npcai.h `KilnNpcAiApi.status` @80 `0.2.0` — Thread-safe; entity counts and mapped count describe the last Pre.
- kiln_npcai.h `KilnNpcAiApi.entity_lifetime` @88 `0.2.0` — Added in v1; check header.size.
- kiln_npcai.h `KilnNpcAiApi.tick_step` @96 `0.2.0` — Added in v1; check header.size.

## services/kiln_npcspeech.h — service `kiln.npcspeech`
### KilnNpcSpeechApi — 96 bytes
- kiln_npcspeech.h `KilnNpcSpeechApi.set` @8 `0.2.23` — The effective override of one NPC (the caller resolves owners and priorities). mode KILN_SPEECH_*; tags/n...
- kiln_npcspeech.h `KilnNpcSpeechApi.say` @16 `0.2.23` — One tag in the NPC's bubble for ms (1..KILN_SPEECH_MAX_SAY_MS); tag 0 or ms 0 ends a running Say.
- kiln_npcspeech.h `KilnNpcSpeechApi.text` @24 `0.2.23` — The texts of a loca tag: "text" or {"en": "...", "de": "..."} ("*" = every language), UTF-8 JSON.
- kiln_npcspeech.h `KilnNpcSpeechApi.clear_all` @32 `0.2.23` — Ends every override and Say (a world's end, a reset).
- kiln_npcspeech.h `KilnNpcSpeechApi.status` @40 `0.2.23` — Any thread.
- kiln_npcspeech.h `KilnNpcSpeechApi.set_lines` @48 `0.2.25` — since 0.2.0-dev.32 (appended; check header.size >= offsetof(KilnNpcSpeechApi, dialog_status) +...
- kiln_npcspeech.h `KilnNpcSpeechApi.next` @56 `0.2.25` — PICK_HOST lines: the index (0..n-1) the next open of that kind shows, or KILN_SPEECH_LINE_GAME; it stays...
- kiln_npcspeech.h `KilnNpcSpeechApi.events` @64 `0.2.25` — Up to max events since the last call, oldest first (*n of them).
- kiln_npcspeech.h `KilnNpcSpeechApi.dialog_status` @72 `0.2.25` — Any thread.
- kiln_npcspeech.h `KilnNpcSpeechApi.menu_say` @80 `0.2.25` — One text (a tag) in the speech box of the NPC's dialogue menu on this game: now if it is open, else when...
- kiln_npcspeech.h `KilnNpcSpeechApi.topics` @88 `0.2.25` — The NPC's Kiln topics (0..KILN_SPEECH_MAX_TOPICS; replaces the last call's, n 0 clears): rows after the...

## services/kiln_pacing.h — service `kiln.pacing`
### KilnPacingApi — 48 bytes
- kiln_pacing.h `KilnPacingApi.sites` @8 `0.2.0` — The places this build has (KILN_PACING_*).
- kiln_pacing.h `KilnPacingApi.set_receiver` @16 `0.2.0` — The one receiver of loop and server waits (the managed half; either may be NULL).
- kiln_pacing.h `KilnPacingApi.arm` @24 `0.2.0` — Which places deliver now (KILN_PACING_LOOP_WAIT | MESSAGE_WAIT | SERVER_WAIT).
- kiln_pacing.h `KilnPacingApi.cap_task_workers` @32 `0.2.0` — Caps the worker threads each task group starts with (0: leave a group).
- kiln_pacing.h `KilnPacingApi.status` @40 `0.2.0`

## services/kiln_pets.h — service `kiln.pets`
### KilnPetsApi — 96 bytes
- kiln_pets.h `KilnPetsApi.bind` @8 `0.2.25` — A guard row for a body the owner bound in kiln.creature_control.
- kiln_pets.h `KilnPetsApi.activate` @16 `0.2.25` — A staging row becomes a pet's guarded body (its final lease is Active).
- kiln_pets.h `KilnPetsApi.inspect_body` @24 `0.2.25` — The row now.
- kiln_pets.h `KilnPetsApi.collect_dirty` @32 `0.2.25` — Up to 'capacity' rows with DownPending or ProtectionLost, oldest latch first; *needs_rescan = 1 when more...
- kiln_pets.h `KilnPetsApi.retire_binding` @40 `0.2.25` — The owner destroyed the body: its row goes (the broker keeps its root denied for the world epoch).
- kiln_pets.h `KilnPetsApi.death_policy` @48 `0.2.25` — kiln.decisions' native death guard: KILN_DEATH_GUARD_SKIP (1) for a guarded body (latching DownPending...
- kiln_pets.h `KilnPetsApi.hit_policy` @56 `0.2.25` — The pet target rule for one hit: KILN_PET_HIT_DENY (1) denies the whole hit before ApplyHit (no health...
- kiln_pets.h `KilnPetsApi.involved` @64 `0.2.25` — 1 when the entity is a guarded pet body of this world epoch (kiln.combat asks before it reads anything).
- kiln_pets.h `KilnPetsApi.set_owner_entity` @72 `0.2.25` — The owner's player entity changed (a login): owner heals and hostile pairs follow it.
- kiln_pets.h `KilnPetsApi.set_hostile` @80 `0.2.25` — Declares (on 1) or ends (on 0) a consensual hostility between two player entities, for pets of the...
- kiln_pets.h `KilnPetsApi.withdraw` @88 `0.2.25` — Two-phase protection loss, phase 1: every row is marked ProtectionLost at once (and stays guarded); new...

## services/kiln_playeranim.h — service `kiln.playeranim`
### KilnPlayerAnimApi — 104 bytes
- kiln_playeranim.h `KilnPlayerAnimApi.state` @8 `0.2.1` — KILN_PA_*
- kiln_playeranim.h `KilnPlayerAnimApi.graph_hash` @16 `0.2.1` — 16 hex digits: the first 8 bytes of SHA-256 over the served graph's bytes.
- kiln_playeranim.h `KilnPlayerAnimApi.game_state` @24 `0.2.1` — name: "State", "Machine/State", or "0x" plus eight hex digits (entry id).
- kiln_playeranim.h `KilnPlayerAnimApi.game_state_ids` @32 `0.2.1` — Every id that enters that state (out_count is set even when capacity is too small).
- kiln_playeranim.h `KilnPlayerAnimApi.mod_state` @40 `0.2.1` — KILN_ERR_NOT_FOUND: the mod defines no such state.
- kiln_playeranim.h `KilnPlayerAnimApi.state_count` @48 `0.2.1` — Every state mods define, by mod id, then name (UTF-8 bytes).
- kiln_playeranim.h `KilnPlayerAnimApi.state_at` @56 `0.2.1`
- kiln_playeranim.h `KilnPlayerAnimApi.define_json` @64 `0.2.1` — A definition from code: {"format": 1, "mod": "\<id>", "dir": "\<folder>", "states": {...}}, the states as in...
- kiln_playeranim.h `KilnPlayerAnimApi.prune_pending` @72 `0.2.1` — A mod's code ran: states it kept from code for names other than these (a JSON array) are dropped from the...
- kiln_playeranim.h `KilnPlayerAnimApi.status_json` @80 `0.2.1` — JSON: state, hash, the states with status and reason, the machines touched, bytes before and after, the...
- kiln_playeranim.h `KilnPlayerAnimApi.game_state_at` @88 `0.2.1` — The index-th state with that name, in the graph's order (index \< the *out_matches game_state gives). name...
- kiln_playeranim.h `KilnPlayerAnimApi.game_state_ids_at` @96 `0.2.1` — Every id that enters the index-th state with that name (out_count is set even when capacity is too small).

## services/kiln_players.h — service `kiln.players`
### KilnPlayersApi — 112 bytes
- kiln_players.h `KilnPlayersApi.status` @8
- kiln_players.h `KilnPlayersApi.list` @16 — Players of the last scan, in slot order. *out_count = how many there are; when that is more than capacity...
- kiln_players.h `KilnPlayersApi.get` @24 — By player entity id (any state).
- kiln_players.h `KilnPlayersApi.find_name` @32 — By name: exact match first, then ASCII case-insensitive; an in-game player wins over one joining/leaving.
- kiln_players.h `KilnPlayersApi.find_steam_id` @40
- kiln_players.h `KilnPlayersApi.position` @48 — Position in metres.
- kiln_players.h `KilnPlayersApi.events` @56 — Events with seq > after_seq, oldest first, from a ring of the last 64. *out_count = how many were written...
- kiln_players.h `KilnPlayersApi.last_seq` @64
- kiln_players.h `KilnPlayersApi.subscribe` @72
- kiln_players.h `KilnPlayersApi.unsubscribe` @80
- kiln_players.h `KilnPlayersApi.raw_slot` @88 — Tick thread: one record + BasicPlayerInfos entry, raw.
- kiln_players.h `KilnPlayersApi.replay_log` @96 — Testing: runs a fresh log tracker (the same code the service uses on the live log) over 'lines' (one game...
- kiln_players.h `KilnPlayersApi.probe_position` @104 — Diagnostics, tick thread: the position path used for players (GetEntityTransform on the captured server...

## services/kiln_playerstate.h — service `kiln.playerstate`
### KilnPlayerStateApi — 176 bytes
- kiln_playerstate.h `KilnPlayerStateApi.status` @8 `0.2.9`
- kiln_playerstate.h `KilnPlayerStateApi.source_info` @16 `0.2.9` — The sources (index 1..KILN_PLAYERSTATE_SOURCES-1) and the names of their values, as this build has them.
- kiln_playerstate.h `KilnPlayerStateApi.enum_entry` @24 `0.2.9`
- kiln_playerstate.h `KilnPlayerStateApi.scan` @32 `0.2.9` — Reads 'count' players into 'out' (each with out[i].size set by the caller).
- kiln_playerstate.h `KilnPlayerStateApi.equipment` @40 `0.2.9`
- kiln_playerstate.h `KilnPlayerStateApi.equipment_slot_name` @48 `0.2.9`
- kiln_playerstate.h `KilnPlayerStateApi.buffs` @56 `0.2.9`
- kiln_playerstate.h `KilnPlayerStateApi.slot_item` @64 `0.2.9` — An item stack in a slot (InventorySlotId as in KilnPlayerAction.used_slot).
- kiln_playerstate.h `KilnPlayerStateApi.item_info` @72 `0.2.9`
- kiln_playerstate.h `KilnPlayerStateApi.sequence_name` @80 `0.2.9` — The running sub-sequence's name in the game's data: "UseItem_Food_Buff", "Movement_Tool_HangGlider_Start".
- kiln_playerstate.h `KilnPlayerStateApi.cancel_action` @88 `0.2.9` — Stops the sequence the actor runs, the way the game ends one whose keep-alive failed, if it is still this...
- kiln_playerstate.h `KilnPlayerStateApi.lab_use_item` @96 `0.2.9` — Test labs only (environment KILN_PLAYERSTATE_LAB=1, else KILN_ERR_UNAVAILABLE): starts a sequence with a...
- kiln_playerstate.h `KilnPlayerStateApi.local_player` @104 `0.2.9` — A joined client's or a host's own player; 0 on a dedicated server, in the menu.
- kiln_playerstate.h `KilnPlayerStateApi.client_action_info` @112 `0.2.9` — The versioned client actions of this build, 0..n-1; KILN_ERR_NOT_FOUND past the end.
- kiln_playerstate.h `KilnPlayerStateApi.client_action_bytes` @120 `0.2.9` — The action's struct as PlayerInput holds it now.
- kiln_playerstate.h `KilnPlayerStateApi.client_action_cancel` @128 `0.2.9` — Cancels the held action (entity, index): client_action_decide with cancel 1 for its version.
- kiln_playerstate.h `KilnPlayerStateApi.client_action_hold` @136 `0.2.9` — Holding client actions, so a listener can cancel them.
- kiln_playerstate.h `KilnPlayerStateApi.client_action_held` @144 `0.2.9` — The held actions not listed before (each out[i].size set by the caller).
- kiln_playerstate.h `KilnPlayerStateApi.client_action_decide` @152 `0.2.9` — cancel 0: let it run; 1: cancel it.
- kiln_playerstate.h `KilnPlayerStateApi.aim` @160 `0.2.9` — since 0.2.0-dev.27: check header.size ---- The player's camera (KilnPlayerAim).
- kiln_playerstate.h `KilnPlayerStateApi.client_action_interest` @168 `0.2.9` — since 0.2.0-dev.29: check header.size ---- A selective hold (kiln.decisions' typed equipment verdict)...

## services/kiln_profile.h — service `kiln.profile`
### KilnProfileApi — 88 bytes
- kiln_profile.h `KilnProfileApi.mode` @8 — KILN_PROFILE_*
- kiln_profile.h `KilnProfileApi.id` @16 — profile "id" (launcher profile id), empty for LEGACY / NO_MODS / INVALID
- kiln_profile.h `KilnProfileApi.name` @24 — profile "name" for logs and the launcher, empty when none
- kiln_profile.h `KilnProfileApi.generation` @32
- kiln_profile.h `KilnProfileApi.count` @40
- kiln_profile.h `KilnProfileApi.get` @48 — KILN_ERR_NOT_FOUND past the end or for an unknown kind; KILN_ERR_INVALID_ARGUMENT when out is NULL or its...
- kiln_profile.h `KilnProfileApi.dropins` @56 — Bit (1u \<\< KILN_PROFILE_KIND_*) set: also load what sits in that kind's drop-in folder (kiln/mods...
- kiln_profile.h `KilnProfileApi.config_dir` @64 — mod configs: profile "configDir", else \<game>/kiln/config
- kiln_profile.h `KilnProfileApi.data_dir` @72 — per-mod data root: profile "dataDir", else \<game>/kiln/data
- kiln_profile.h `KilnProfileApi.json` @80 — the profile file's text as read (INVALID: the error), empty when none

## services/kiln_progress.h — service `kiln.progress`
### KilnProgressApi — 48 bytes
- kiln_progress.h `KilnProgressApi.begin` @8 `0.2.0` — Starts a task with a title (the window's heading: "Preparing the game data for EML mods").
- kiln_progress.h `KilnProgressApi.update` @16 `0.2.0` — The line under the title and the bar: done of total (any unit; total 0 = no total known, the bar moves on...
- kiln_progress.h `KilnProgressApi.end` @24 `0.2.0` — The task is over.
- kiln_progress.h `KilnProgressApi.step` @32 `0.2.2` — v2, since 0.2.0-dev.27: what the start is doing, \<game>/kiln/state/progress.json ---- Check header.size >=...
- kiln_progress.h `KilnProgressApi.mark` @40 `0.2.2`

## services/kiln_queries.h — service `kiln.queries`
### KilnQueriesApi — 56 bytes
- kiln_queries.h `KilnQueriesApi.ray_voxels` @8 `0.2.3`
- kiln_queries.h `KilnQueriesApi.entities` @16 `0.2.3`
- kiln_queries.h `KilnQueriesApi.path_submit` @24 `0.2.3`
- kiln_queries.h `KilnQueriesApi.path_cancel` @32 `0.2.3`
- kiln_queries.h `KilnQueriesApi.path_poll` @40 `0.2.3`
- kiln_queries.h `KilnQueriesApi.path_retire` @48 `0.2.3`

## services/kiln_registry.h — service `kiln.registry`
### KilnRegistryApi — 88 bytes
- kiln_registry.h `KilnRegistryApi.status` @8 `0.2.17` — Where the registry stands, as one snapshot.
- kiln_registry.h `KilnRegistryApi.schema_json` @16 `0.2.17` — The registry schemas (schema/registry/\<registry>.json: registries, fields with type, range, unit, applies...
- kiln_registry.h `KilnRegistryApi.field_info` @24 `0.2.17` — One field: "\<registry>.\<field>" ("items.maxStack", "items.damage.fire").
- kiln_registry.h `KilnRegistryApi.subscribe_stage` @32 `0.2.17` — A mod's turn in each stage (the native form of C#'s Mod.OnRegistry; Kiln.Host uses it for every Kiln mod...
- kiln_registry.h `KilnRegistryApi.submit` @40 `0.2.17` — One IR batch (schema/registry-ir.schema.json) of owner's.
- kiln_registry.h `KilnRegistryApi.read` @48 `0.2.17` — One field of one entry in a view (KILN_REGISTRY_VIEW_*), as JSON (a number, true/false, a name, an id, a...
- kiln_registry.h `KilnRegistryApi.select` @56 `0.2.17` — The ids a selector names in a view, as a JSON array in the registry's order. selector_json: an IR...
- kiln_registry.h `KilnRegistryApi.report_json` @64 `0.2.17` — The body of state/registry.json for the current generation (schema/state-registry.schema.json): every...
- kiln_registry.h `KilnRegistryApi.subscribe_ready` @72 `0.2.17` — fn runs at START, on the stage thread right after the stage, and at each WORLDLOAD (each application of...
- kiln_registry.h `KilnRegistryApi.unsubscribe` @80 `0.2.17` — Removes a subscription of subscribe_stage or subscribe_ready.

## services/kiln_registry_live.h — service `kiln.registry.live`
### KilnRegistryLiveApi — 56 bytes
- kiln_registry_live.h `KilnRegistryLiveApi.begin` @8 `0.2.17`
- kiln_registry_live.h `KilnRegistryLiveApi.submit` @16 `0.2.17`
- kiln_registry_live.h `KilnRegistryLiveApi.commit` @24 `0.2.17`
- kiln_registry_live.h `KilnRegistryLiveApi.revert` @32 `0.2.17`
- kiln_registry_live.h `KilnRegistryLiveApi.read` @40 `0.2.17`
- kiln_registry_live.h `KilnRegistryLiveApi.status` @48 `0.2.17`

## services/kiln_registry_view.h — service `kiln.registry.view`
### KilnRegistryViewApi — 48 bytes
- kiln_registry_view.h `KilnRegistryViewApi.views_json` @8 `0.2.25` — Every view: {"format": 1, "views": [{"name", "tier": "stable" | "advanced", "fields": n, "registry"?...
- kiln_registry_view.h `KilnRegistryViewApi.fields_json` @16 `0.2.25` — One view's fields: {"view", "tier", "fields": [{"path", "type": "int" | "float" | "bool" | "enum" |...
- kiln_registry_view.h `KilnRegistryViewApi.read` @24 `0.2.25` — One field of one entry, as JSON.
- kiln_registry_view.h `KilnRegistryViewApi.select` @32 `0.2.25` — The ids a selector names, as a JSON array in the view's order: an id, a case-insensitive glob over ids...
- kiln_registry_view.h `KilnRegistryViewApi.entry_json` @40 `0.2.25` — Every field of one entry: {"view", "id", "tier", "fields": {"\<path>": value}, "refused": {"\<path>"...

## services/kiln_render.h — service `kiln.render`
### KilnRenderApi — 152 bytes
- kiln_render.h `KilnRenderApi.status` @8 `0.2.0`
- kiln_render.h `KilnRenderApi.acquire_frames` @16 `0.2.0`
- kiln_render.h `KilnRenderApi.release` @24 `0.2.0`
- kiln_render.h `KilnRenderApi.camera` @32 `0.2.0` — camera: KILN_ERR_UNAVAILABLE when feature render.camera is off (out->available = 0, the rest zero)....
- kiln_render.h `KilnRenderApi.world_to_screen` @40 `0.2.0`
- kiln_render.h `KilnRenderApi.line` @48 `0.2.0`
- kiln_render.h `KilnRenderApi.rect` @56 `0.2.0`
- kiln_render.h `KilnRenderApi.circle` @64 `0.2.0` — segments 0 = automatic
- kiln_render.h `KilnRenderApi.triangle` @72 `0.2.0`
- kiln_render.h `KilnRenderApi.polyline` @80 `0.2.0` — xy = count pairs; filled draws a convex polygon
- kiln_render.h `KilnRenderApi.text` @88 `0.2.0` — size 0 = the default font size; flags KILN_RENDER_TEXT_*
- kiln_render.h `KilnRenderApi.text_size` @96 `0.2.0`
- kiln_render.h `KilnRenderApi.image` @104 `0.2.0` — an image from kiln.ui image_load, stretched over the rectangle; uv 0..1; tint 0xFFFFFFFF = as is
- kiln_render.h `KilnRenderApi.outline` @112 `0.2.13` — outlines (v2, since 0.2.0-dev.30.3; check header.size, see "Outlines" above; any thread) ---- outline: a...
- kiln_render.h `KilnRenderApi.outline_info` @120 `0.2.13`
- kiln_render.h `KilnRenderApi.outline_remove` @128 `0.2.13`
- kiln_render.h `KilnRenderApi.outline_clear` @136 `0.2.13`
- kiln_render.h `KilnRenderApi.outline_status` @144 `0.2.13`

## services/kiln_repl.h — service `kiln.repl`, `kiln.repl.lab`, `kiln.repl.net`
### KilnReplApi — 104 bytes
- kiln_repl.h `KilnReplApi.capabilities` @8 `0.2.3`
- kiln_repl.h `KilnReplApi.stats_enable` @16 `0.2.3` — Reference-counted per owner (an owner holds one reference; enabling twice is once): the first enable of...
- kiln_repl.h `KilnReplApi.peer_stats` @24 `0.2.3` — One row per machine and channel with anything counted; *count = the rows (also with...
- kiln_repl.h `KilnReplApi.watch` @32 `0.2.3` — What 'peer_or_all' (KILN_REPL_ALL_MACHINES, or a peer handle as peer_stats names it, a fake's too) is sent...
- kiln_repl.h `KilnReplApi.remove` @40 `0.2.3` — Any handle this service returned; any thread.
- kiln_repl.h `KilnReplApi.component_info` @48 `0.2.3` — The registry's facts about a component, from the live world (cached at its first Pre).
- kiln_repl.h `KilnReplApi.capture` @56 `0.2.3` — Since 0.2.0-dev.31: writes everything 'peer' is sent for 'seconds' (1..600) as JSON lines...
- kiln_repl.h `KilnReplApi.lab_add_fakes` @64 `0.2.3` — lab: the loopback harness; since 0.2.0-dev.28; check header.size ---- Adds 'count' (1..16) fakes on the...
- kiln_repl.h `KilnReplApi.lab_force_relevant` @72 `0.2.3` — Makes 'entity' relevant to fake 'machine' on both channels, from the next frame on (at most 64 per fake).
- kiln_repl.h `KilnReplApi.lab_remove_fakes` @80 `0.2.3` — Stops the fakes and lets the game free their replication objects (the world's slot bits go).
- kiln_repl.h `KilnReplApi.lab_stats` @88 `0.2.3`
- kiln_repl.h `KilnReplApi.lab_decoded` @96 `0.2.3` — The newest state the harness decoded for 'entity' on fake 'machine''s client channel: the bytes of...
### KilnReplLabApi — 24 bytes
- kiln_repl.h `KilnReplLabApi.payload` @8 `0.2.17` — The payload (the bytes after the message header) of the newest message of 'kind' (KILN_REPL_KIND_CREATE or...
- kiln_repl.h `KilnReplLabApi.hook_cost` @16 `0.2.17` — What kiln.repl's own hook code costs (measured on lab servers only), since the last call with reset =...
### KilnReplNetApi — 144 bytes
- kiln_repl.h `KilnReplNetApi.capabilities` @8 `0.2.17`
- kiln_repl.h `KilnReplNetApi.remove` @16 `0.2.17` — Any handle this table returned (track, filter, message hook); any thread; takes effect before the next...
- kiln_repl.h `KilnReplNetApi.track` @24 `0.2.17` — Tracking.
- kiln_repl.h `KilnReplNetApi.peer_entities` @32 `0.2.17` — The entities in the peer's client-channel state map (relevant to it now). *count = how many (also with...
- kiln_repl.h `KilnReplNetApi.filter_add` @40 `0.2.17` — Filters: fn edits component component_hash of every snapshot sent to peers (peer_count 0 = every peer) on...
- kiln_repl.h `KilnReplNetApi.filter_set_peers` @48 `0.2.17` — Another peer set for a filter; touches the entities whose output changes.
- kiln_repl.h `KilnReplNetApi.touch` @56 `0.2.17` — Marks e for re-sending on the client channel without changing a byte (the touch program runs in the next...
- kiln_repl.h `KilnReplNetApi.remap_set` @64 `0.2.17` — The item remap.
- kiln_repl.h `KilnReplNetApi.remap_peers` @72 `0.2.17` — The peers the remap applies to (vanilla peers, Kiln peers without the content); touches what changes.
- kiln_repl.h `KilnReplNetApi.remap_stats` @80 `0.2.17`
- kiln_repl.h `KilnReplNetApi.message_hook` @88 `0.2.17` — Message hooks on the game's channels (not 4, 5, 12, 14, 17): directions KILN_REPL_SEND | KILN_REPL_RECEIVE.
- kiln_repl.h `KilnReplNetApi.message_inject` @96 `0.2.17` — Sends data to peer on channel as a game message: the schema channels a peer's machine socket carries (1...
- kiln_repl.h `KilnReplNetApi.schema_find` @104 `0.2.17` — The game's message codec. schema_find: a message schema by its name (*out_size its struct's bytes).
- kiln_repl.h `KilnReplNetApi.schema_fields` @112 `0.2.17`
- kiln_repl.h `KilnReplNetApi.schema_decode` @120 `0.2.17` — Decodes length bytes into out (zeroed first; out_size at least the schema's size).
- kiln_repl.h `KilnReplNetApi.schema_encode` @128 `0.2.17`
- kiln_repl.h `KilnReplNetApi.peers` @136 `0.2.17` — Each machine's peer as the last Pre saw it (kiln_repl.h "Machines and peers"): out_peers[m] the peer...

## services/kiln_routines.h — service `kiln.routines`
### KilnRoutinesApi — 88 bytes
- kiln_routines.h `KilnRoutinesApi.assign` @8 `0.2.17` — Queues "bind this plan to the villager".
- kiln_routines.h `KilnRoutinesApi.remove` @16 `0.2.17` — Queues "take the routine off the villager".
- kiln_routines.h `KilnRoutinesApi.resume` @24 `0.2.17` — Queues "end the manual suspension".
- kiln_routines.h `KilnRoutinesApi.inspect` @32 `0.2.17` — The villager's snapshot as JSON (not NUL-terminated).
- kiln_routines.h `KilnRoutinesApi.revoke_owner` @40 `0.2.17` — The owner is going (its mod unloads): at the next Pre its queued changes are dropped without a call and...
- kiln_routines.h `KilnRoutinesApi.subscribe_events` @48 `0.2.17` — Framework only (Kiln.Blocks): the events above.
- kiln_routines.h `KilnRoutinesApi.unsubscribe_events` @56 `0.2.17`
- kiln_routines.h `KilnRoutinesApi.restore` @64 `0.2.17` — Framework only (Kiln.Blocks, when a world is ready after a restart): queues "bind this saved assignment...
- kiln_routines.h `KilnRoutinesApi.set_revision_floor` @72 `0.2.17` — Framework only: the next revision the world gives is above floor (the highest revision a saved world ever...
- kiln_routines.h `KilnRoutinesApi.claim_mod` @80 `0.2.17` — Framework only: 'owner' is the owner mod 'mod_id''s calls come with (see Owners above).

## services/kiln_rules.h — service `kiln.rules`
### KilnRulesApi — 72 bytes
- kiln_rules.h `KilnRulesApi.set` @8 `0.2.0` — Server world: the rules of player entity 'entity' (a player 1..1023, or up to 16 other player characters...
- kiln_rules.h `KilnRulesApi.clear` @16 `0.2.0` — Clears every player's rules (a server world closing does this too).
- kiln_rules.h `KilnRulesApi.get` @24 `0.2.0` — The rules last set for 'entity' (defaults when none).
- kiln_rules.h `KilnRulesApi.set_local` @32 `0.2.0` — Client: the local player's rules (prediction world and the client's cursor).
- kiln_rules.h `KilnRulesApi.status` @40 `0.2.0`
- kiln_rules.h `KilnRulesApi.map_reveal` @48 `0.2.0` — since 0.2.0-dev.16 (check header.size): client only.
- kiln_rules.h `KilnRulesApi.creative` @56 `0.2.0`
- kiln_rules.h `KilnRulesApi.noclip` @64 `0.2.2` — since 0.2.0-dev.27 (check header.size): what the NoClip hook did for 'entity' (a player or one of the...

## services/kiln_saves.h — service `kiln.saves`
### KilnSavesApi — 64 bytes
- kiln_saves.h `KilnSavesApi.status` @8 — any thread
- kiln_saves.h `KilnSavesApi.request` @16 — any thread.
- kiln_saves.h `KilnSavesApi.next_event` @24 — any thread.
- kiln_saves.h `KilnSavesApi.set_pump` @32 — One pump (the managed Kiln.Saves). fn = NULL removes it.
- kiln_saves.h `KilnSavesApi.path` @40 — any thread.
- kiln_saves.h `KilnSavesApi.set_pump_interval` @48 — Also call the pump every 'steps' server-world steps in Pre, with kind 0 (0 = only for events).
- kiln_saves.h `KilnSavesApi.stage_progress` @56 `0.2.4` — Appended v1 (0.2.0-dev.29.3; check header.size, and KILN_SAVES_CAN_STAGE_BARRIER in status.features).

## services/kiln_screen.h — service `kiln.screen`
### KilnScreenApi — 56 bytes
- kiln_screen.h `KilnScreenApi.status` @8
- kiln_screen.h `KilnScreenApi.play` @16 — 0: refused (off, or KILN_SCREEN_MAX_EFFECTS already playing).
- kiln_screen.h `KilnScreenApi.stop` @24 — immediate: gone from the next frame.
- kiln_screen.h `KilnScreenApi.set_strength` @32 — Changes 'strength' while it plays: for what follows a value (health, depth).
- kiln_screen.h `KilnScreenApi.is_playing` @40 — 1 while it plays (fading out counts), else 0.
- kiln_screen.h `KilnScreenApi.stop_owner` @48 — Stops all of an owner's effects, fading unless immediate.

## services/kiln_settings.h — service `kiln.settings`
### KilnSettingsApi — 96 bytes
- kiln_settings.h `KilnSettingsApi.status` @8
- kiln_settings.h `KilnSettingsApi.count` @16 — Number of settings (0 until ready).
- kiln_settings.h `KilnSettingsApi.info` @24 — Everything about one setting, with its current value.
- kiln_settings.h `KilnSettingsApi.find` @32 — Index of a setting by its enshrouded_server.json name (case-insensitive).
- kiln_settings.h `KilnSettingsApi.option` @40 — ENUM options: name and value of option 'ordinal' (0-based, in the game's order).
- kiln_settings.h `KilnSettingsApi.get` @48
- kiln_settings.h `KilnSettingsApi.set` @56 — Writes the value into the live block and (unless SET_NO_RESEND) asks the game to send the block to every...
- kiln_settings.h `KilnSettingsApi.reset` @64 — Back to the world's own value ('initial'); index UINT32_MAX = every setting.
- kiln_settings.h `KilnSettingsApi.resend` @72 — Asks the game to send the settings block to every player with the next server-info message.
- kiln_settings.h `KilnSettingsApi.save` @80 — Writes the live values into enshrouded_server.json (status.config_path) with "gameSettingsPreset"...
- kiln_settings.h `KilnSettingsApi.last_error` @88 — The calling thread's last refusal or error text of set/reset/save ("" if none).

## services/kiln_skills_client.h — service `kiln.skills.client`
### KilnSkillsClientApi — 32 bytes
- kiln_skills_client.h `KilnSkillsClientApi.state` @8 `0.2.22`
- kiln_skills_client.h `KilnSkillsClientApi.nodes` @16 `0.2.22`
- kiln_skills_client.h `KilnSkillsClientApi.links` @24 `0.2.22`

## services/kiln_spawn.h — service `kiln.spawn`
### KilnSpawnApi — 136 bytes
- kiln_spawn.h `KilnSpawnApi.spawn` @8 — tick.
- kiln_spawn.h `KilnSpawnApi.spawn_item` @16 — tick.
- kiln_spawn.h `KilnSpawnApi.destroy` @24 — tick.
- kiln_spawn.h `KilnSpawnApi.entity_info` @32 — tick.
- kiln_spawn.h `KilnSpawnApi.find` @40 — tick.
- kiln_spawn.h `KilnSpawnApi.template_find` @48 — templates (thread-safe) ----
- kiln_spawn.h `KilnSpawnApi.template_get` @56
- kiln_spawn.h `KilnSpawnApi.template_count` @64
- kiln_spawn.h `KilnSpawnApi.template_at` @72 — 0..count-1, sorted by name
- kiln_spawn.h `KilnSpawnApi.tracked_count` @80 — thread-safe
- kiln_spawn.h `KilnSpawnApi.tracked_at` @88 — thread-safe
- kiln_spawn.h `KilnSpawnApi.cleanup` @96 — tick.
- kiln_spawn.h `KilnSpawnApi.refresh` @104 — tick.
- kiln_spawn.h `KilnSpawnApi.world_key` @112 — thread-safe.
- kiln_spawn.h `KilnSpawnApi.world_state` @120 — thread-safe.
- kiln_spawn.h `KilnSpawnApi.capabilities` @128 — thread-safe.

## services/kiln_station_menus.h — service `kiln.stationmenus`
### KilnStationMenusApi — 72 bytes
- kiln_station_menus.h `KilnStationMenusApi.info` @8 `0.2.17`
- kiln_station_menus.h `KilnStationMenusApi.define` @16 `0.2.17` — Stores 'record' as owner's layer for its definition id. *out: the layer's handle (the same for a...
- kiln_station_menus.h `KilnStationMenusApi.remove` @24 `0.2.17` — Removes a layer.
- kiln_station_menus.h `KilnStationMenusApi.export_layouts` @32 `0.2.17` — The effective local layouts: {"format":1,"generation":N,"menus":[records sorted by workshopId]}. *out_len...
- kiln_station_menus.h `KilnStationMenusApi.remote_replace` @40 `0.2.17` — Replaces the whole remote collection with 'snapshot' ({"format":1,"generation":N,"menus":[records]})...
- kiln_station_menus.h `KilnStationMenusApi.remote_clear` @48 `0.2.17` — No remote collection any more (left the server): local layers show again.
- kiln_station_menus.h `KilnStationMenusApi.set_pending` @56 `0.2.17` — on 1: a mod's own workshops list nothing (waiting for the server's menus); 0: as their layouts say.
- kiln_station_menus.h `KilnStationMenusApi.drain_applied` @64 `0.2.17` — What the menu hook built or refused since the last call, oldest first: a JSON array of {"definition"...
