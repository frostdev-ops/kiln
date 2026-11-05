# Spawning, destroying and templates (server svn 1285601)

Evidence for `kiln.spawn` (native/services/spawn). Build: dedicated server svn 1285601 (Steam build 26404334, exe sha256
`c5c9fe50…`), image base `0x140000000`; addresses are RVAs. Static analysis: capstone + Ghidra 12.1.2 headless
decompilation of the functions named below. Runtime: lab `spawn` (a copied save, world GUID
`11111111-2222-4333-8444-555555555555`), no player connected, Kiln test mod `mods/tests/spawn-test`. Starting points:
EMF 0.3.3 docs (`ENGINE_MAP.md` 1.3/1.4, `engine_parts/re_time_spawn_weather.md` section 2, `re_templates.md`,
`re_inventory_api.md` section 4; MIT), with addresses for builds 1269579/1273043.

## Functions

| Name | RVA | Signature / role | Source |
|---|---|---|---|
| SimStep | `0x6c8f10` | `(storage, bool ticked)`; core symbol | core sigdb |
| SpawnWrapper | `0x620d00` | `u32 (world, const Guid*, const Slice<SpawnComponent>*)`: builds a SpawnRequest {parent 0, guid, slice, mask 0, kind 0} and calls createEntity(world+`world.storage`, id = `*(u32*)0x1301130` = 0, &req, 0). Core symbol | disasm |
| createEntity | `0x623ce0` | `u32 (storage, u32 id /*0 = allocate*/, SpawnRequest*, mask*)`. Immediate (not queued): locks the template resource, looks the template info up, allocates the id (`++*(u32*)(storage+0x498)`), builds the archetype, copies spawn components, writes the entity record. Returns the id or 0 | Ghidra |
| destroyEntity | `0x63e8a0` | `bool (storage, u32 id, u8 mode, u8 skip_culled)`. Immediate: removes the record and the grid entry, queues the client update ("Entity Update Queue is full for destroy"). Kiln calls `(storage, id, 1, 0)` like the game's own loops (`0x60af80`, `0x60b2c0`, `0x6c917f`) and EMF; the command executor (`0x6bf546`) passes `(0, 0)`; mode 2 changes only the replication kind, 4 skips a cleanup step | Ghidra |
| destroy core | `0x657090` | `(storage, record*, mode)` | Ghidra |
| GetEntityTransform | `0x64fd40` | `WorldTransform* (world, u32 id)`: entity map lookup, then `0x5ea2a0` (CurrentTransform column via the template map at registry+0x30). NULL if no entity/transform. Core symbol | disasm |
| GetComponent | `0x64ed50` | `{void*, u64}* (out, world, u32 hash, u32 id)` via the dynamic component map at registry+0x88. Core symbol | disasm |
| entity map find | `0x650c50` | `u64 slot (map*, u32* id)`: hash `x=((k>>16)^k)*0x45d9f3b; x=((x>>16)^x)*0x45d9f3b; h=(x>>16)^x`, linear probing | disasm |
| template map find | `0x6510b0` | `u64 slot (map*, Guid*)`: index = first u32 of the GUID & (cap-1), linear probing, 16-byte key compare | Ghidra |
| component map find | `0x64dc40` | `u16* (map*, u32* hash)`: identity hash, values = u16 dynamic type index | disasm |
| register template info | `0x6671d0` | `(storage, TemplateResource*, Guid*, bool)`: fills one 0xA8-byte template info, interns its name, sets its component bitset | Ghidra |
| template infos init | `0x667c10` | loops the template list, logs `[ecs%c] Initialized %,u template infos` (12,269 on this build) | disasm |
| lock template resource | `0x5f3aa0` | `(out, resourceSystem = *(registry+0xE17040), Guid*)` (old builds: registry+0xE15440, +0x1C00 like `world.storage`) | disasm |
| spawnPickupItem | `0x166760` | the game's loot drop: spawns template GUID bytes at `0xbe50f0` (`a56d2777-daf1-4164-90d4-b7b12749c723`, Base_LootPickup_Generic_Gravity) with CurrentTransform + PickupItem + Velocity + ModelDynamic through the queued create `0x5fb340` | Ghidra |

Sigdb (`sigdb/symbols/server.d/spawn.json`) resolves all of it on svn 1285601 and on 1273043
without edits: DestroyEntity/CreateEntity by EMF's patterns (CreateEntity cross-checked as SpawnWrapper's `call`
target), the map offsets as captured holes in DestroyEntity, CreateEntity, GetEntityTransform and GetComponent.

## Layouts

Storage `S = world + world.storage` (0xE179C0); registry `R = *(u64*)S` (= world+0x928).

- **Engine hash map** (every map below): `+0x08` occupancy bitset (u64 words), `+0x10` capacity (power of two), `+0x28`
  keys, `+0x40` values, `+0x54` count.
- **Entity map** `S+0x148` (cap `S+0x158`, records `S+0x188`): key u32 entity id, value `EntityRecord*`. Capacity 2^20
  on the lab server. Kiln walks it word-wise (empty words skipped): 0.30 ms for a full scan (lab log
  `entity map at step 250 ... full scan 0.30 ms`).
- **EntityRecord**: `+0x10` u32 id, `+0x14` u32 parent id, `+0x18` archetype, `+0x20` chunk, `+0x28` TemplateInfo*,
  `+0x30` u32 index in chunk, `+0x34` u8 flags (createEntity writes these; destroyEntity reads `+0x28`). Kiln checks
  `record+0x10 == key` for every slot it reads: 0 mismatches over 2,818 entities.
- **Template map** `R+0x745810` (cap `+0x745820`, values `+0x745850`): key = 16-byte GUID, value `TemplateInfo*`.
- **TemplateInfo** (0xA8, array at `R+0x280`, count `R+0x288`): `+0x00` GUID, `+0x10` `const char*` name (interned,
  NUL-terminated), `+0x18` u64 name length, `+0x20` u8 family, `+0x28` bitset of the dynamic component type indexes
  the template's entities get, `+0x98` u16 index, `+0x9C` u32 live entity count, `+0xA0` u32 peak count, `+0xA4..A7`
  flags. Kiln checks `key == TemplateInfo.guid` for every slot.
- **Dynamic component map** `R+0x88`: key = component qualifiedHash, value = u16 type index (bit index into
  TemplateInfo+0x28 and into an archetype's bitset).
- Component hashes are FNV-1a-32 of `"keen::ecs::<Name>"` (checked: CurrentTransform `0x027ac564`, PickupItem
  `0x08775a90`, SpawnTime `0x583963a3`, DoNotSave `0x41e678ea`, OverrideLevel `0xa21e7ee7`).
- **SpawnRequest** (0x30): `+0x00` parent, `+0x04` GUID, `+0x18` SpawnComponent*, `+0x20` count (max 128), `+0x2A` player
  mask, `+0x2C` kind. **SpawnComponent** (0x18): `{u32 hash; u32 pad; const void* data; u64 size}`; the size must equal
  the archetype column stride, else `[ecs%c] Component %u size of %,u does not match to spawn size of %,u` and no
  entity; an unknown hash logs `Could not find component %08x` before anything is created.
- **WorldTransform** (CurrentTransform, 56 B): `i64 pos[3]` 32.32 fixed point metres, `f32 rot[4]` quaternion x,y,z,w
  at +0x18, `f32 scale[3]` at +0x28, pad. Kiln writes yaw as `(0, sin(yaw/2), 0, cos(yaw/2))`, scale 1; it reads back
  exactly (`creature yaw reads back as 90.0 deg`, positions within 0.05 m: `is at (3805.8, 833.0, 1433.8) (asked ...)`).
- **PickupItem** (28 B, from spawnPickupItem): `u32 itemId; u8 itemRef[16] (0); u32 level; u8 rarity; u8 pad;
  u16 amount`.
- **World save header** (`<saveDirectory>/world0.ksd`): `"SV2C"`, `+0x0C` 16-byte world GUID (same in all ten
  backups), `+0x1C` u32 revision (+1 per write), `+0x20` u32 unix time. Kiln keys its sidecar by this GUID.

## Template identity

- Runtime: the template map holds every template the server loaded (12,269 on svn 1285601, the same count as the
  game's `[ecss] Initialized 12,269 template infos`), each with its name. So GUID → name and name → GUID need no
  offline table. Kiln reads it once at the first server-world step (`12269 engine templates loaded`).
- GUID text = Windows layout, as .NET `Guid.ToByteArray()` (first three groups little-endian):
  `Lookup(Enemy_Vukah_Melee) = d5d05217-52dc-467a-9192-26b4f661d8d9`, the same key the display-name catalog uses.
- Kinds by name (Kiln's heuristic): 1,007 creatures, 476 pickups, 4,058 props, 93 NPC-like, 6,635 other.
- Display names ("Vukah") come only from the shipped catalog (`creature-templates.json`, 269 entries, plus
  the generic pickup): the template info has no localized name.

## Thread rules (EntityGrid race)

SpawnWrapper and destroyEntity write chunk storage, the entity map and the EntityGrid directly. The server's SimStep
returns with the EntityGrid update and range-query jobs still running (EMF ENGINE_MAP "Jobs still running after
SimStep"), so both are only called from the tick thread in Pre (before the step; no world job runs) or Post after
`WaitWorldJobs` — Kiln's two tick phases. kiln.spawn refuses other threads (`spawn off the tick thread is refused
(InvalidOperationException)`). The entity exists as soon as createEntity returns (`creature 2542 exists after spawn`
in the same callback); game programs, including init-only ones, see it from the next step.

The world pointer comes from the core (`KilnApi.server_world`, recorded at the first server-world step, before Pre:
`kiln.spawn: ready (world from the core; ...)`). With an older core that lacks it, kiln.spawn chains a capture-only
safetyhook inline hook in front of the core's SimStep hook instead (it recorded the same storage pointer in the
earlier lab runs).

## World load states

The dedicated server steps its world (SimStep, kind 0) while it is still loading the save: steps start at
`[game_server] Switching state from Initialize to Load`, the save is deserialized ~11 s later, and
`Switching state from LoadEcsScene to Run` follows. Entity map: 0 entities at step 1, 1 at step 250, 1,723 at the Run
line, 2,818 at step 2,500 (more stream in without any player). Spawning during Load could collide with the loader's
own createEntity calls, so kiln.spawn refuses spawns until it sees the Run line in the game log (fallback: 4 min of
steps).

## AI, physics and culling (no player connected)

- A spawned `Enemy_Vukah_Melee` stayed exactly where it was spawned for 12 s (`moved 0.00 m`); an item pickup spawned
  2 m in the air did not fall (`y 834.50 -> 834.50`). Without a player nearby the server does not simulate the area
  (no AI/physics). Neither was culled in that time. EMF saw spawned hostiles attack a player in a hosted game; on the
  dedicated server this needs a client (client checklist).
- A creature spawn brings child entities parented to it (record `+0x14`): for the Vukah `Enemy_Weakspot_Vukah_Melee_Head`,
  `Hookshot_Pull_Entity_To_Player`, `VFX_SubEntity_Toggle_Signal_Link_Enemy_Vukah_Slinger` (entity count 2828 -> 2831).
  Destroying the creature removes them too (count 2825 after 25 steps, none left).

## Persistence

- The world autosaves every 30 s (revision +1 at 30 s intervals) and on SIGINT stop (revision 3015 -> 3016).
- Whether an entity reaches the save is decided by its **template**: templates whose component set includes DoNotSave
  are never saved. Kiln reads that bit from TemplateInfo+0x28: 3,654 of 12,269 templates carry it, including 839 of
  1,007 creatures (`Enemy_Vukah_Melee: NeverSaved=True`) and 66 of 476 pickups (the generic pickup:
  `NeverSaved=True`); `Prop_Deco_Steppeprops_Barrel_01` and `Home_Base_FlameAltar` do not.
- Observed after SIGINT stop + start (twice, fresh lab copies): the Vukah and the item pickup were gone; the barrel came
  back with a new id (2544 -> 1311) at the exact spawn position; a barrel spawned with an added DoNotSave component
  (`KILN_SPAWN_TRANSIENT`) did not come back. Matches the prediction from the template bits (and EMF's "a hostile
  spawned with spawn was not in the world after a reload"; EMF's spawned villagers were saved).
- **SpawnTime is not restored**: Kiln stamps a marker SpawnTime (ECS now + 1..997 ns; the game's own values are
  multiples of the 16,666,667 ns tick period) and it reads back in the session (`SpawnTime component 14500000292`),
  but the loaded barrel has no SpawnTime at all. The save keeps template + transform + a fixed set of components
  (EMF: the save2 loader passes CurrentTransform, OverrideLevel, CustomPlayerString, BaseIdComponent), so a loaded
  entity can only be matched by template and position. Kiln matches static templates at the exact saved position
  (0.3 m) and creatures/NPCs within 8 m of the last known position.
- After a cleanup and the next save, a restart shows nothing of Kiln's left (`after cleanup + save + restart: no ...`).

## Open questions

- Ground placement: entities are placed exactly where asked; there is no ground snap (EMF's admin spawn runs the
  transform through `0x14095a4f0`/`0x14095a760` on old builds, not traced). Spawning above ground with no player near
  leaves the entity floating until the area is simulated (assumed to fall then; client checklist).
- Aggro of spawned creatures toward a joined player on the dedicated server; whether server culling despawns a spawned
  creature once players leave (spawner logic mentions "despawned due to server culling").
- Which non-DoNotSave creature templates (168) are saved, e.g. villagers and tamed animals.
- Template `family` byte meaning (`TemplateInfo+0x20`; destroyEntity calls a resource callback when it is non-zero).
