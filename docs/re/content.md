# Content registries on the dedicated server (svn 1285601): evidence for kiln.content

Build: dedicated server Steam 26404334 / svn 1285601, exe sha256 `c5c9fe50…`, image base `0x140000000`, RVAs below.
Cross-checked against the old server build 1273043 and the client `enshrouded.exe`
Steam 26404966 (sha256 `ce6fdba6…`, analysed from a scratch copy). Sources: EMF 0.3.3 docs (MIT:
`engine_parts/re_content.md`, `inventory_crafting.md`, `re_templates.md`, `ENGINE_MAP.md`), the EMF `big_stacks` and
`loot_scale` samples, `docs/re/items.md`, `docs/re/settings.md` (the reflection record layout), capstone over the PE
image (`tools/sigport/peimg.py`), and runtime checks in lab `content` (a copied world, no player). Nothing
from `kfc-parser/`. Evidence tags: **[dis]** disassembly, **[cap]** typed-hole capture in
`sigdb/symbols/server.d/content.json`, **[refl]** the game's own reflection, **[rt]** runtime (kiln.log quoted).

## Short answers
- **Layouts come from the game itself.** `TypeByHash(fnv1a(qualified name))` (`0x7e15d0`) returns the static
  reflection record of any `keen::` type: name, size, kind, fields `{name, type, offset}`, enum values. kiln.content
  flattens `keen::ItemInfo` (2088 B → 345 scalar paths), `keen::RecipeInfo` (168 B → 31), `keen::LootableItemInfo`
  (36 B → 13) and `keen::ecs::ItemStackInfoResource` (40 B → 7) at run time; no struct offset is hard-coded except
  the registries' hash maps. **[refl] [rt]** "reflection: keen::ItemInfo 2088 bytes, 345 fields; keen::RecipeInfo 168
  bytes, 31 fields (inputs +0x28 x48 B, outputs +0x30 x24 B); keen::LootableItemInfo 36 bytes, 13 fields;
  keen::ecs::ItemStackInfoResource 40 bytes, 7 fields"
- **Item, recipe and random-loot data live in one read-only allocation.** **[rt]** "memory: items data at
  …63763CB0: protect 0x2, MEM_PRIVATE, region …63763000 + 382.9 MB (allocation 000000005CC50000)", recipes and loot in
  the same allocation. Writes need `VirtualProtect` (EMF saw the same on the client: `ENGINE_MAP.md`).
- **The game reads these values live**: `maxStackSize` in addItems / validateItemStack (docs/re/items.md), recipe
  inputs/outputs in applyRecipe at every craft, `LootableItemInfo` stack sizes through `LootFind` at every random roll,
  drop-table stacks at every default-inventory application. A write takes effect at the next use; nothing is cached.
- **The registries are ready ~0.3 s after the server world's first step, ~11 s before the save is read and the world
  runs** (players cannot join before that). **[rt]** "registries ready at step 12 (334 ms after the server world's
  first step): 4213 items, 2542 recipes (22 input categories), 4213 random-loot items" … "lifecycle: the game reports
  the world running (step 272)"; game log "Savegame deserialization took 1.11 s" at 12.4 s.
- **The game cuts oversized stacks.** validateItemStack (`0x1b7f10`) clamps `count` to `ItemInfo.maxStackSize` and logs
  "[inventory] Deleted %k items from stack of %k." Lowering a raised max stack therefore deletes items at the next
  transaction that touches the stack. kiln.content's stack guard turns that `jbe` into a `jmp` (`0x1b80d3`, 76 → EB).

## 1. Functions and layout constants (MS x64)

| sigdb name | 1285601 | 1273043 | client 26404966 | what |
|---|---|---|---|---|
| `content/TypeByHash` | `0x7e15d0` | `0x7d7ed0` | `0x8de780` | `TypeRecord* (u32 hash)`: static reflection (same pattern as `settings/TypeByHash`) |
| `content/ItemRegistryFind` | `0x93b5c0` | `0x9315d0` | `0xe94390` | `ItemInfo* (u32 id, ItemRegistry*)`; map at `+0x38` **[cap items.map]** |
| `content/ItemRegistryUpdate` | `0x96abc0` | | resolves | per-frame loader: `cmp byte [rcx+0x320],0` (loading), `[rcx+0x321]` (started) **[cap]** |
| `content/RecipeFind` | `0x940a30` | `0x936a30` | `0xe99700` | `RecipeInfo* (u32 id, RecipeRegistry*)`: count `+0x9c`, cap `+0x58`, bits `+0x50`, keys `+0x70`, values `+0x88` **[cap]** |
| `content/RecipeFinishLoading` | `0x96df10` | `0x963ab0` | `0xec5f80` | resource `+0x20` **[cap]**, maps `+0x48` (id → RecipeInfo*) **[cap]**, `+0xA0` input categories, `+0xF8` workshop lists (`0x1008`-byte `SmallDynamicArray<RecipeInfo*,512>`, u16 count `+0x1000`) **[dis]** |
| `content/LootFind` | `0x92f950` | `0x925970` | `0xe886c0` | `LootableItemInfo* (LootRegistry*, u32 itemId)`: count `+0x94`, cap `+0x50`, bits `+0x48`, keys `+0x68`, values `+0x80` **[cap]** (captured as the call target of the roll at `0x16b6de`) |
| `@content/LootRoll.fields` | `0x16b6ca` | `0x16625a` | resolves | `mov rcx,[rcx+0x18]` before `call LootFind`: LootGenerationSystem `+0x18` = the loot registry **[cap]** |
| `content/DefaultInventoryAcquire` | `0x19d9b0` | `0x1981a0` | `0x3fc5f0` | `Handle* (Handle* out, ResourceSystem*, const Guid*)`, type `0x3eb1bc48` = fnv1a("keen::ecs::DefaultInventoryResource"); after the call `out+8` = the resource data (0 while loading), `out+0x10` its size **[dis]** |
| `content/StackClamp` | `0x1b80d3` | `0x1b28d3` | `0x417e33` | the `jbe` (76 7C) after `movzx ecx,word [r13+0x14]; mov eax,[r14+4]; cmp eax,ecx` in validateItemStack **[dis] [cap clamp.max_stack = 0x14]** |

The three hash maps are the same engine HashMap type (`bits +0x08, capacity +0x10, keys +0x28, values +0x40, count
+0x54` from the map): the service checks that every captured member of the recipe and loot maps sits at those
distances (`recipes.count - recipes.map == 0x54`, …) before it reads them. The same signatures resolve on 1273043 and
on the client exe (the offline `sigdb_check.py` reports `@content/LootFind.fields` as failed because it does not
resolve rel32-captured scope symbols before scoped searches; the core does, and the bytes at LootFind are identical
on all three builds).

### Where each registry comes from (ServerWorldSetup `0x233ee0`, singleton registrations at `0x235370..0x23649f`) **[dis]**
| object | systems offset | world singleton |
|---|---|---|
| ItemRegistry (`0x328` B, created `0x925700`) | `+0x1d8` | `g38_items::ItemRegistry` |
| RecipeRegistry | `+0x1e0` | `g38_items::RecipeRegistry` |
| loot registry (`0xF0` B, created `0x925980`, requests `LootableItemsResource` type `0x2bd83360`; maps `+0x40` id → LootableItemInfo*, `+0x98`) | `+0x210` | none: reached through `g38_loot::LootGenerationSystem` (`+0x220`, ctor `0x16a440` stores the registry at `+0x18`) |
| LootShufflebag (ctor `0x925af0` gets the loot registry) | `+0x218` | `g38_loot::LootShufflebag` |
| DefaultLootLabelRegistry | `+0x208` | `DefaultLootLabelRegistry` |

## 2. The reflection record **[refl] [rt]**
Layout as in docs/re/settings.md: `+0x20/+0x28` qualified name, `+0x38` inner type (typedef target, enum underlying
type, array / BlobArray element, bitmask enum), `+0x40` u32 size, `+0x48` u32 field / enum-value / array-element
count, `+0x4C` u32 kind (low byte; upper bits are flags, e.g. structs read `0x612`), `+0x50` hash, `+0x58` fields
(stride `0x30`: name, len, type `+0x10`, offset `+0x18`), `+0x60` enum values (stride `0x28`: name, len, value `+0x10`).
Kinds seen: 1 bool, 2 u8, 3 s8, 4 u16, 5 s16, 6 u32, 7 s32, 8 u64, 9 s64, 0xA f32, 0xB f64, 0xC enum, 0xD–0x10
Bitmask8..64 (inner = the flag enum), 0x11 typedef, 0x12 struct, 0x13 StaticArray (count `+0x48`), 0x18 BlobArray
`{i32 self-relative offset, u32 count}`, 0x19 BlobString, 0x1C ObjectReference (16-byte GUID), 0x1D Guid.
`keen::Time` is a struct `{s64 value}` in nanoseconds; `keen::HashKey32` a struct `{u32 value}`.

Layouts used for lists (all from reflection at run time):
- `RecipeInfo` +0x28 `input: BlobArray<RecipeInputResource>` (48 B: `itemStack` RecipeItemStackResource {itemRef
  GUID, item +0x10, count +0x14}, `inputItemCategory` +0x18 {categoryRef GUID, category +0x10, count +0x14}); +0x30
  `output: BlobArray<RecipeItemStackResource>` (24 B); +0x38 `craftingDuration` Time; +0x50 `knowledgeRequirement`
  KnowledgeQueryData (the unlock: id, compareValue, operator, type, isExplicitPlayerKnowledgeQuery); +0x78 debugName.
- `DefaultInventoryResource` = `rootGroup` DefaultInventoryGroupResource (36 B: isSelector, `stacks` +4
  BlobArray<ItemStackInfoResource>, `groups` +0xC BlobArray<group>, lootLabels); `ItemStackInfoResource` 40 B: itemRef,
  item +0x10, countMin +0x14, countMax +0x18, spawnProbability +0x1C, weight +0x20, lootLabelsId +0x24.
- `LootableItemInfo` 36 B: itemId, rarities (Bitmask8<ItemRarity>), useUiRarity, uiRarity, labelGroups, stackSizeMin
  +0x18, stackSizeMax +0x1A, stackSizeMaxScaled +0x1C, stackSizeScalable +0x1E, shufflebagType, …
- The roll (`0x16b5f0`) reads `+0x1E` (scalable) and then `+0x18/+0x1A` (min/max) and, when scalable, scales by
  `+0x1C` through `0x16db30` **[dis]**: stack sizes are read at every roll. Rarity masks and label groups feed the
  shufflebag / pools built at load, so kiln.content offers them as fields but a change may only reach rolls after the
  pools are rebuilt (not verified).

## 3. Writing
- Item, recipe and loot structs are in a `PAGE_READONLY` `MEM_PRIVATE` allocation of ~450 MB **[rt]**. kiln.content
  writes in sorted batches: per cluster of neighbouring pages (at most 16 pages, only pages that hold a write) it
  re-checks the region (`VirtualQuery`: committed, read-only, private), makes it `PAGE_READWRITE`, copies, restores.
  Image, mapped and other memory is refused. **[rt]** BigStacks: "2252 change(s) … (writes since boot: 0 writable,
  2257 read-only pages, 746 VirtualProtect pairs)", about 16 ms once at world load.
- Recipe lists of a different length: the BlobArray header (8 bytes in the RecipeInfo) is pointed at a copy in
  Kiln's memory, allocated within ±1.5 GB of the recipe so the self-relative i32 offset reaches it. The RecipeInfo
  keeps its address, so the registry map and the workshop lists need no change; revert = the original 8 bytes.
- Drop tables are loaded on demand: the DefaultInventoryAcquire hook (worker threads, under the store lock) registers
  a table the first time its data pointer appears and applies pending pack rules before the caller reads the stacks.
  **[rt]** a spawned `LootContainer_Enemy_Scavenger_Melee01` (no player): "drop tables 0 -> 1; inventory
  Material_T1_Metal_Scraps x24" and "table a20e8eae-7833-462e-9ea9-59d1e17a1b98 registered on first use, pack rule x2
  applied before the roll: Material_T1_Metal_Scraps 6..6 -> 12..12 p1" (24 = 12 × the lab world's own Loot Amount 2).

## 4. Lab results (lab `content`, a copied world, no player) **[rt]**
```
PASS dashboard catalog: 4206 known, debug name equal 4206, original max stack equal 4206, category equal 4206
PASS data pack: Material_T1_Twigs.maxStackSize 600 (original 250)
PASS data pack: Ammo_Arrow_T1_Flint outputs Ammo_T1_Arrow_Flint_lvl5 x100 (x2 of 50)
PASS code: Material_T1_Wood_Logs.MaxStack 2000 (original 250), entry marked modified
PASS give 1500 Material_T1_Wood_Logs (vanilla max 250) into 1042:3: Done 1500/1500 in 1 slot(s) [1042:3+1500]
PASS read back: 1042:3 holds 1500 x Material_T1_Wood_Logs as one stack
PASS still one stack of 1500 five steps later (no clamp)
PASS conflict reported: items Material_T1_Resin.maxStackSize: content-test/content/test-pack.json: items Material_T1_Resin sets 400, mod content-test: … sets 500
PASS recipe: Tool_basic_FishingRod_T0_PK inputs now Material_T1_Twigs x1, Material_T1_String x1, Material_T1_Wood_Logs x3, Material_T1_Resin x4, duration 5s
PASS recipe: charcoal kiln duration 120s -> 30s, inputs category 0xee5ae93f x12, category 0x4da53a1c x3
PASS loot: Material_T1_Wood_Logs random stacks 5..20 (originally 1..1)
content-test done: 34 PASS, 0 FAIL
```
Unload / reload / restart (content-observer logs every change):
```
[host] control: unload content-test
[service:kiln-content.dll] mod content-test reverted: 7 field(s) put back
OBSERVE items Material_T1_Wood_Logs.maxStackSize: 2000 -> 250
OBSERVE recipes Tool_basic_FishingRod_T0_PK.inputs: … Material_T1_Resin x4 -> Material_T1_Twigs x2, Material_T1_Metal_Scraps x2, Material_T1_String x1
OBSERVE recipes Material_T1_Factory_03_Charcoal.craftingDuration: 30s -> 120s
data packs of content-test skipped: the mod is not loaded
OBSERVE items Material_T1_Twigs.maxStackSize: 600 -> 250
OBSERVE status: … generation 2, packs 0, changed 0, conflicts 0 …
PASS after reload: Material_T1_Wood_Logs.maxStackSize back to 250 (the old instance's 2000 came off)
PASS after reload: the data pack stayed (the mod is loaded again): twigs 600
PASS after restart: the data pack was applied at world load (generation 1): twigs 600, Ammo_Arrow_T1_Flint x100; code values not yet (wood 250)
PASS after restart: code changes re-applied in OnReady (wood 2000, rod 5 s)
reload-test content-test 10: "10 reloads, 0 failures, avg 8.07 ms, max 16.85 ms, 0 leaked context(s)"
[mod:bigstacks] raised the max stack size of 2252 of 2252 stackable items (x4, at most 9999); wood logs 250 -> 1000; stack guard on
```
Tick cost: Kiln Pre p50 0.03 ms (the service polls readiness every 4 steps until ready, owners every 25, pack files
every 50); a dump stalls one step for ~0.5 s.

## 5. Multiplayer (EMF `re_content.md` §3, applied to each change)
Every process builds its own registries from its own data; the network carries item ids, counts and recipe ids,
not definitions; there is no data-hash handshake. The client exe has the same registries, reflection and code
(every content signature resolves on `enshrouded.exe` 26404966), so the same pack JSON can be applied there.

| change | server-only result | what an unmodded client shows | client needs it? |
|---|---|---|---|
| item `maxStackSize` | authoritative: stacks merge to the new size, saved as they are | counts display (u32 in the replicated Inventory); its own prediction / UI split and merge with the vanilla size: drag-merge may look refused or split until the server state arrives; its prediction world may run its own clamp (cosmetic) | yes, for a clean UI |
| recipe inputs / outputs | the server consumes / gives what the server's recipe says | the crafting UI lists the vanilla ingredients and result and checks the player's materials against them: a cheaper recipe may stay greyed out, a dearer one is offered and the server silently does less | yes |
| `craftingDuration` | factory stations finish on the server's time | the station's progress shows vanilla | yes (display) |
| unlock (`knowledgeRequirement`) | the server does not check knowledge (EMF) | the client's UI decides what is listed | yes (client-only) |
| item stats (damage, armor, fuel, comfort) | the server computes impacts | tooltips show vanilla | yes (display) |
| random loot, drop tables | rolled on the server | nothing to show before the loot appears | no |

## 6. Open questions
- Client side (needs a joined player): the UI and prediction effects in the table above.
- Rarity masks / label groups of `LootableItemInfo`: whether the loot pools pick up a change without a restart.
- The 1273043 `ItemRegistryUpdate` pattern was not checked (the `items.loading` flag only gates readiness; the
  service also waits for a stable item count).
- New items in random-loot pools and drop tables (section 7 covers registries, names, icons, crafting).

## 7. New items and recipes at run time (EMF §2.4, done) **[dis] [refl] [rt]**
Server 1285601 RVAs, client 26404966 in brackets. Code: `native/services/content/newcontent.cpp`; usage:
docs/kiln/services/content.md "New items and recipes".

### Item registry insert
- `ItemRegistryFind` → `HashMap::find` `0x92d900` [client: same shape]: `slot = key & (cap-1)`, test the bitset bit,
  compare `keys[slot]`, `slot+1` with wrap-around, stop at the first clear bit; no tombstones **[dis]**.
- ItemRegistryUpdate phase 1 (`0x96ac9d..0x96acfd`) inserts each loaded ItemInfo exactly so: slot finder `0x94fb10`,
  `bts` the bit, `inc [map+0x54]`, key = `ItemInfo+0` (itemId), value = the ItemInfo pointer (captured as
  `content/items.map2` = `items.map` = 0x38) **[dis] [cap]**. kiln.content writes value, key, then the bit
  (`lock or`) and the count, in Pre on the world's own thread, and keeps the game's load factor (refuses beyond
  cap/2: the game reserves 2 x next_pow2(n), 16384 slots for 4213 items).
- The other maps of the registry, filled from the resource (phase 0 `0x96ae3a..`): `+0xe8` load entries (GUID →
  resource handle, 0x30-byte entries at `+0x128`), `+0x90` (sized by itemRefs), `+0x1f0` / `+0x248` (sized by the
  resource's second / third arrays), and two side maps phase 1 fills only for voxel (`+0x140`, key = byte
  `ItemInfo+0x454`) and overgrowth items (`+0x198`, byte `+0x4e0`). New items go into `+0x38` only: lookups by id
  (`ItemRegistryFind`, 341 callers on the client per EMF) find them; the voxel / overgrowth item of a material stays
  the base item's; the `+0xe8` entries own resource handles and are left alone (EMF's advice).
- validateItemStack keeps a stack when `ItemRegistryFind(id)` is non-null, so stacks of a registered new item pass
  every transaction; the lab never logged "Removed invalid Item from Stack" across a save and restart **[rt]**.
- Without the definition (a restart without the mod) the stacks are already gone right after the world loads: lab,
  a sword and 7 ingots saved in container 1042 read back as empty slots before any transaction, no game log line
  **[rt]**. The game drops unknown ids while loading the save, so the insert has to happen before it (it does: 0.3 s
  after the first step, the save is read ~11 s later).

### Cloning an ItemInfo
- `self_relative_fields("keen::ItemInfo")` walks the reflected struct (nested structs and static arrays): its inline
  part holds 20 BlobArrays and 1 BlobString (debugName) and no other self-relative kind (kinds 0x12, 0x13, 0x18,
  0x19, 0x1c, 0x1d and scalars only; offline walk over the exe's static reflection records, client identical)
  **[refl]**. The clone is the 2088-byte struct copied into memory within ±1.5 GB of the base (each i32 offset
  rebased to keep pointing at the base's blob data), then `itemId`, `objectId`, the `name` / `description` / `lore`
  LocaTag references (new GUIDs derived from the item GUID; tag id = FNV-1a of the GUID), `debugName` (a BlobString
  into the clone's tail) and the definition's field values. **[rt]** "keen::ItemInfo 2088 bytes (21 blob fields
  rebased per clone)"; a Bronze Sword clone with dmgMod 1.75 / Rare was given as gear (level 5, item entity) and read
  back; flags (HasPIDE, IsSalvageable) and category come from the base.
- One clone per base ItemInfo pointer, shared by the worlds that load the same resource (EMF: both worlds share the
  item data); a new session whose base moved gets a new clone. Old clones are never freed.

### Recipe registry insert
- RecipeFinishLoading (`0x96df10`) builds `+0x48` id → RecipeInfo* (same HashMap insert) and `+0xF8` workshop id →
  `SmallDynamicArray<RecipeInfo*, 512>`: values of 0x1008 bytes (`imul rdx, r8, 0x1008`), u16 count at `+0x1000`,
  limit `cmp ax, 0x200` → "SmallDynamicArray is out of elements" (captured: `content/recipes.workshops` 0xF8,
  `ws_stride` 0x1008, `ws_count` 0x1000, `ws_max` 0x200; the workshop map is reserved with capacity 0x40)
  **[dis] [cap]**. A new RecipeInfo (168 B) lives in Kiln's memory with its input (48 B each), output (24 B), props
  arrays and debugName right behind it (self-contained BlobArrays), and is appended to its workshop's array. Taking a
  recipe off (owner gone) shifts the array down; its id stays in `+0x48`.
- Server craft (`handle_crafting_events` `0x8ad90`, "[crafting] Invalid recipe id '%k'" at `0x8af2a`) looks the
  recipe up with RecipeFind (`0x940a30`), so the id map entry is what the server needs.

### Client: crafting UI **[dis client]**
- `unlocked_recipes_ui` (descriptor `0x1e32f40`, update `0x31b650`, flags 0x5; sys-read G38Knowledge, NpcRegistry,
  ItemKnowledgeDatabase, RecipeRegistry, WorkshopRegistry; sys-write G38UiPlayData; events-out KnowledgeGained)
  iterates **the RecipeRegistryResource array** (`RecipeAll` `0xe9a0b0`: `{resource+8 data, count}`, stride 0xa8),
  not the maps: a recipe only in the maps is never listed. (EMF's data-patched recipe was in that array and still
  hidden; **[inf]** its copied requirement failed the knowledge check below, not verified.) Per recipe: knowledge requirement (`0x2fc800`;
  `knowledgeOrQueryId == 0` → unlocked, no error **[dec]**), then for requirement-less recipes "is any input item
  known" (`0xe76f30(itemId, ItemKnowledgeDatabase)`: `+0x98` is the ItemRegistry, so an unregistered item
  is not known; a registered item without a knowledge record, or whose record lacks the lock flag (`& 8`), is known,
  else the player's knowledge decides **[dec]**; corrected in 8.4), then
  workshop lookup (`0xe89bb0`); the recipe id goes into a per-workshop
  list in G38UiPlayData (`+0x60` / `+0xb8` / `+0x110`, 0x818-byte arrays of u32, 512 max).
- `RecipeAll` has two callers, both client UI and read-only (`0x31b6fb` this loop, `0x2c51cb` a cooking lookup). The
  hook returns a copy of the array (each copy's 4 self-relative fields rebased) plus the new RecipeInfos, cached
  until the source or the set changes.
- The loop runs only when G38UiPlayData's cached versions (`+0x3018` / `+0x301c`, captured `content/ui.version_a/b`)
  differ from the player's knowledge versions: after adding or removing recipes in the client world the service sets
  `+0x3018` to 0xFFFFFFFF (a u16 version can never match), so the lists are rebuilt at the next UI update.

### Client: the crafting menu **[dec client]**
- The menu (builder `0xb80050`) reads the unlocked list of the station's workshop from G38UiPlayData, but what it
  shows comes from its UI configuration: `0xd5ade0(lists, …, workshopId, …)` takes `cfg = *(*(0x283f4a8) + 0xc8)`,
  finds the workshop id in the u32 BlobArray at `cfg+0xf098` (48 workshops, id 0 = the player's own menu) and its
  category BlobArray in the parallel 8-byte array at `cfg+0xf0a0`; `0xd5abf0` walks the 0x38-byte categories,
  `0xd5ee20` their subcategories (BlobArray at category `+0x30`, 0x28 bytes each, name tag at `+0`, recipe-id
  BlobArray at `+0x20`) and keeps a recipe id when RecipeFind finds it and `0xd662d0` (unlocked) agrees. A recipe in
  no subcategory is never shown: this, not the knowledge check, is why EMF's data-patched recipe stayed hidden.
  `*(0x283f4a8)` is only valid while the UI builds (null in a client-world Pre), so the service patches the lists from
  a hook on `0xd5ade0` (captured as `content/CraftMenuCategories`, `content/UiApp`, `ui.app_config` 0xc8,
  `ui.menu_workshops` 0xf098; the strides are fixed constants). **[rt]** "crafting menu config …: 48 workshop(s), 48
  category list(s); workshops: 0 56d94f8 …" → "the crafting menu lists 3 new recipe(s) (1 of 1 subcategory list(s)
  rewritten)".

### Client: names and icons **[dis client]**
- LocaPool_lookup `0x12168e0` (EMF's pattern; absent on the dedicated server) `Result* (Result* out, LocaPool*, u32
  id)`: `Result = {u8 error; u32 @+4; const char* text @+8; u64 len @+0x10}`; id 0 → error 0x12, id == `pool+0x18`
  (0x69691905 = FNV-1a of the zero GUID) → a constant, then the three language slots (binary search over 24-byte
  `LocaTagResource`s, `0x12161b0`). The hook answers the definitions' tag ids first (an immutable map swapped
  atomically; strings never freed: the UI keeps the views).
- The UI's text getter (`0xaff910`) first asks `0x1216580` → **LocaRecord** `0x12165f0` `Result{u8 err; u32;
  const LocaTagResource* @+8}* (out, pool, id)` for the tag's record (24 B: id, text BlobString `+4`, arguments
  BlobArray `+0xc`, genericArguments `+0x14`) to check argument counts, and returns an empty string when there is no
  record, before it ever calls LocaPool_lookup. **[rt]** with only the text hook the item names stayed blank and the
  loca hook saw none of the new tags (2 M lookups, 0 answered); the item tooltip builder `0xb20020` did compute the
  new tag (`0xe94600` name, `0xe93540` description, `0xe94460` lore: FNV-1a of `ItemInfo+0x48/+0x68/+0x78` unless the
  item-knowledge record overrides). With a record hook (a synthesized 24-byte record + text per tag) the names show:
  "loca hook 2045566 lookup(s), 44972 answered with new texts".
- The UI texture cache get `0xa4e5f0` `(cache, key*, u8)`: key = `{guid[16], type u32 @+0x10, part u32 @+0x14}`
  (callers build 32 bytes with type 0xf9e86997 = UiTextureResource, part 0), FNV-style hash into a map at
  `cache+0x3550..0x3570`; a missing key is fatal: "corrupted game ui texture: %k" (`0xa4e93b`). Ten call sites in five
  UI functions. The hook copies the key and replaces a new item's GUID with its base item's before the lookup.

### Runtime (client 26404966) **[rt]**
```
new content on (workshop lists +0xf8, 512 x 4104 B); client parts: loca on, item icons on, crafting UI on (+ refresh)
new content: item Kiln_Sample_FrostEdge (0xdc853d25, …) added to the server world's registry (clone of Weapon_T1_1H_SwordEpic_01 at 000002001EA00840)
new content: item Kiln_Sample_FrostEdge (0xdc853d25, …) added to the client world's registry (clone of Weapon_T1_1H_SwordEpic_01 at 000002001EA00840)
new content: the crafting UI sees 2545 recipe(s) (2542 of the game's + 3 new)
NewItems: starter kit for … (entity 1): Done 1/1 x 0xdc853d25 @ 1; Done 3/3 x 0xfaa382cf @ 1; Done 6/6 x 0x6b356a28 @ 1
(joined the lab)  new content: client world: 3 item(s), 3 recipe(s) added, 3 recipe(s) listed at their station
(joined the lab)  registries ready at step 4200 (159 ms after the client world's first step): 4216 items, 2545 recipes
```
The server and client worlds of a hosted game got the same clone address: they share the base ItemInfo memory (EMF).
Screens: names, descriptions, icons and stats in the backpack; the three recipes under "Supplies — Utility";
crafting consumed the inputs and gave the outputs, hosted and on the dedicated lab server.

### Runtime (lab newcontent, server 1285601, no player)
```
new content on (workshop lists +0xf8, 512 x 4104 B); client parts: loca off, item icons off, crafting UI off
new content: keen::ItemInfo 2088 bytes (21 blob fields rebased per clone), keen::RecipeInfo 168 bytes (4), input 48 B, output 24 B
new content: item Kiln_Test_Emberblade (0x496358d0, 59a6d47d-0b55-4211-bfd0-16e9bb6e12c9) added to the server world's registry (clone of Weapon_T1_1H_SwordEpic_01 at 000000007CAF0000)
new content: server world: 3 item(s), 2 recipe(s) added, 2 recipe(s) listed at their station, 0 taken off (definition version 7)
registries ready at step 12 (335 ms after the server world's first step): 4216 items, 2544 recipes ...
PASS world load: recipe Kiln_Test_Emberblade_Recipe 0xd881b050 at the hand station: Material_T1_Wood_Logs x5, Kiln_Test_Ingot x2 -> Kiln_Test_Emberblade x1
PASS kiln.items catalog has Kiln_Test_Emberblade; items: 4216 in kiln.content, 4216 in kiln.items
PASS give 1 Kiln_Test_Emberblade (gear, level 5 Rare) into 1042: Done 1/1 x 0x496358d0 @ 1042 -> 1042:3 item entity 2542
PASS save requested: saved revision 3007 in 21 ms
(restart)
PASS after the restart: the new sword survived the save: 1042:3 item 0x496358d0 level 5 rarity Rare entity 1043
PASS after the restart: the 7 new ingots survived the save: 1050:0 x7
newcontent-test done: 24 PASS, 0 FAIL
```

## 8. References, languages, distribution, missing content

### 8.1 The loca language slot (client 26404966) **[dis]**
- `LocaPool_lookup` `0x12168e0` `(Result* out, LocaPool* pool, u32 id)`: id 0 → error 0x12; id == `pool+0x18`
  (0x69691905) → a constant; else it searches `pool+0xe0` (an override slot, when its blob `+0x20` is loaded), then
  `pool+0x40` (keenglish) **if `[pool+0xd8] == 0`**, then `pool+0x90` (the current language's slot), then `pool+0x40`
  as the final fallback (error 3 "Loading" when keenglish is not loaded, 0x1f "Unknown Tag Id" when no slot has it).
  `LocaRecord` `0x12165f0` has the same slot order (`+0xe0`, `+0x90`, `+0x40`).
- `pool+0xd8` is the current **keen::LanguageId**. `0x1216b60` `(pool, u32 lang)` sets it (`cmp [rcx+0xd8], edx; je;
  mov [rcx+0xd8], edx`) and reloads the language slot; its only caller `0x850d45` passes the user's choice
  (`[settings+0x1d8]`, 0 = the system language from `0x907be0`). The pool constructor `0x1215d20` writes it from its
  4th argument. The slot loader (`0x1216e00`, `mov r9d, [rdi+0xd8]` at `0x1216fc7`) walks the
  `LocaTagCollectionResource.languages` BlobArray (`keen::LocaTagCollectionResourceLanguage`, 0x28 B: language id
  `+0`, content reference `+8`) for the entry whose id equals `+0xd8`.
- Sigdb: `@content/LocaLookup.language` (scope LocaLookup, `48 8D 6E 40 39 BE {content/loca.language:d32} 75`),
  feature `content.loca_language`.
- `keen::LanguageId` from the game's reflection (server and client the same) **[refl] [rt]**: 0 none, 1 en-us, 2 en-gb,
  3 de-de, 4 nl-nl, 5 fr-fr, 6 it-it, 7 es-es, 8 es-mx, 9 ru-ru, 10 pl-pl, 11 ja-jp, 12 id-id, 13 ko-kr, 14 ms-my,
  15 pt-pt, 16 pt-br, 17 th-th, 18 tr-tr, 19 vi-vn, 20 zh-cn, 21 zh-tw, 22 fr-ca, 23 es-419, 24 bg-bg, 25 cs-cz,
  26 da-dk, 27 fi-fi, 28 el-gr, 29 hu-hu, 30 no-no, 31 nb-no, 32 ro-ro, 33 sv-se, 34 uk-ua (35 = count).
- The hooks read `[pool + loca.language]` on every lookup and answer the definition's text for that language (per
  tag a table indexed by LanguageId, built when the texts are set; fallback: primary language, `en`, `*`).

### 8.2 Reference fields of `keen::ItemInfo` **[refl] [rt]**
- 60 16-byte fields are references (reflection kind 0x1C ObjectReference or a typedef of one / 0x1D): 27 distinct
  types among the values the game data uses. The pools kiln.content builds from every loaded ItemInfo (lab, 4222
  items): `keen::ObjectReference<keen::Model>`, `keen::ecs::TemplateReference`, `keen::PerkReference`,
  `keen::WeaponCategoryReference`, `keen::ImpactProgramReference`, `keen::SoundContainerReference`, ... .
- Weapons and tools: `equipment.visualEntity` (template; Bronze Sword `fc562683…`) + `equipment.equipmentEntityModelSetup.model`
  (`9c38c7f0…`, `isSet` true, offsets), `iconModel` = the same model. Armour: `equipment.visualModel` (the worn
  model, e.g. Armor_T2_Warrior_Tank_Chest `35be9681…`), no visual entity, `isSet` false, `iconModel` = the same model.
- Perks: 6124 non-empty `perkReferences[i]` slots, every one with `perkIds[i] == FNV-1a(perkReferences[i])` **[rt]**
  ("perk slots 6124 (id = FNV-1a(GUID) in 6124)").
- Writing them into a clone before it is inserted: lab, `Kiln_Kitbash_Sword` (Bronze Sword base, model of
  Weapon_T4_1H_SwordEpic_02, perk 0 of Weapon_T5_1H_SwordEpic_01) "21 reference / visual field(s) from kitbash",
  registered, given as gear, read back.

### 8.3 Who reads the references (lazy or cached) **[dec] [dis]**
Server 1285601 / client 26404966 RVAs. Ghidra 12.1.2 decompiles + capstone displacement scans of every function;
offsets from the client's reflection of `keen::ItemInfo` (`+0x20 overridePickupModels` BlobArray, `+0xd0
weaponCategoryReference`, `+0x118 blendShapeMask`, `+0x11c hideMask`, `+0x128 impactEntity`, `+0x138 program`,
`+0x148 visualEntity`, `+0x158 visualModel`, `+0x4b8 equipment.visualModels` BlobArray of 20-byte `{u32 mask, Guid}`,
`+0x4f8 equipmentEntityModelSetup`, `+0x54c pidEntity`, `+0x56c iconImage`, `+0x57c iconModel`, `+0x58c iconScene`,
`+0x608 perkReferences[5]`, `+0x658 perkIds[5]`, `+0x74c collectionInfo{isOverrideModelSet, overrideModel +0x750}`).

| field | consumer | lazy / cached | after a write on an item in use |
|---|---|---|---|
| icon (the UI texture under `objectId`) | item-slot widget `0xd085c0` → `0xa4e0e0` → UiTextureGet `0xa4e5f0`; the cache's key set is filled once (`0xa560a0`, flag `cache+0x36c0`) from ListKeys(0xf9e86997): every UiTextureResource in the resource directory; records resolve on first use | per GUID | nothing (Kiln's alias table) |
| `iconImage`, `iconScene`, `equipmentEntityModelSetup.*` | no reader in either exe | - | - |
| `visualModel`, `visualModels[]`, `hideMask`, `blendShapeMask` | client `render_equipment_models_ag2` (`0x3891e0`, body `0x3899e0`): per frame `0x384340` resolves the slots → ItemRegistryFind; `0xe8fd20(out, ItemInfo, bodyFlags, slot)` = `visualModels[]` entry matching the body, else `visualModel`; compared by GUID value with the instance cached per slot (`RenderEquipment+0x60+slot*0x10`), re-created by GUID on change | per frame, by GUID | nothing |
| `visualEntity` (weapons: the held model) | server `equipment_visuals` (`0x147640`, helper `0x146080`; client copy `0x388520` when hosting): spawns the template per equipment slot (queued create `0x95f710`), caches the entity id in Equipment `+0x320...` | per slot, an entity | unequip / equip |
| `impactEntity`, `program` | server `equipment_impacts` (`0x147330` → `0x145a10`, 11 slots `Equipment+8+slot*0x48`): when the slot's item id changes, destroys and spawns `impactEntity`, binds `program` (`0x146430`); attacks copy `+0x110..+0x147` per request (`0x3f6a0`) | per slot by item id; attacks live | re-equip |
| `iconModel`, `overridePickupModels[]` | `pickup_item_model_setup_static` (client `0x341ff0`, server `0x101730`): a random override, else `iconModel`, as the dropped item's ModelDynamic; `render_collection_models` (client `0x3143f0`): `overrideModel` when `isOverrideModelSet`, else `visualModel`, else `iconModel` | per pickup entity / per collection slot | new pickups; re-place on the shelf |
| `perkIds[i]` | `rebalance_perks_and_level` (`0x1acba0`), the perk attribute sums (`0x192250`): `PerkRegistryFind(perkIds[i])` (`0x93f8f0`, map `+0x90`) | live | nothing |
| `perkReferences[i]` | createPide (`0x1b1920`): `perkReferences[rarity] != 0` grants `PerkContainerNew` (docs/re/items.md section 10) | per new item entity | new item entities |
| `weaponCategoryReference` | copied into spawn / impact requests (`0x3f6a0`), looked up per attack (`0x42390`) | live | nothing |
| `pidEntity` | createPide spawns it at commit (`0x1b1e55`) | per new item entity | new item entities |

- The PerkRegistry keys perks by `FNV-1a(perk GUID)` (preload `0x96ca40` stores `GuidToItemId(perkGuid)`), so a
  kitbashed slot writes the GUID and that id together.
- `keen::ObjectReference<keen::Model>` resolves to the **`keen::RenderModel` of the same GUID** (render system
  `0x101af0`: Find key `{guid, 0x177394c7, 0}`); templates are all loaded at world init (12,269); perks at PerkRegistry
  load; RenderModels and UI textures on first use from the resource directory. Loading an ItemInfo loads none of its
  references. So "a GUID some loaded item uses in a field of the same type" is a resource the directory has (the game
  data ships it); the validation pools rely on exactly that.
- Resource system: `ItemRegistry+8`; Find `0x829cd0` / client `0x1080a80` (slot `+0x7d8`), GetData `0x829810` /
  `0x10805c0`, ListKeys `0x82a9a0` / `0x1081750`, Release `0x82cf10` / `0x1083cc0` (not used by kiln.content yet: the
  pools need no call into the game; a mod's own resources come through kiln.assets' `resource_read`).
- Consequences for the model bundle (refs.cpp): weapons need `visualEntity` (the held model is that entity),
  armour `visualModel` **and** the `visualModels[]` list (per body type), a dropped item `iconModel` /
  `overridePickupModels[]`; the icon is only the UI texture alias.

### 8.4 Loot, drop tables, knowledge, traders **[dec] [dis] [rt]**
- **Random loot has no pre-built pools.** Every roll (container fill `0x16ccb0` from `random_loot_on_interaction`,
  single slot `0x16d020` from `random_pickup_loot_spawner`, from random drop-table stacks (`item == 0`) and the
  destroy path) reads the candidate list through the getter **LootItems** `0x93d3c0` (client `0xe960b0`, 1273043
  `0x9333d0`): `{data, count}(out, lootRegistry)` = `LootableItemsResource.items` (`lootRegistry+0x38` → data `+0x14`,
  BlobArray of 36-byte `LootableItemInfo`). Each entry with non-empty label groups that match the container's tags is a
  candidate (weight = tag × label weights), then rarity (`globalRarityDropRates`), a weighted pick, the shufflebag
  re-draw from the same list (by `shufflebagType`, scratch bitsets sized per roll), and finally `LootFind(itemId)`
  (map `+0x40`) for uiRarity and stack sizes. Nothing per item is cached; the `+0x98` map holds label weights.
- kiln.content (feature `content.loot_new`, sigdb `content/LootItems` with `content/lootreg.data` 0x38 and
  `content/loot.items` 0x14): a definition's copy of its `like` item's entry (label BlobArrays rebased) goes into the
  loot map, and the hooked getter returns an immutable copy of the array (headers rebased) plus those entries. Lab
  **[rt]**: "random loot rolls see 4214 item(s) (4213 of the game's + 1 new)"; 20 spawned `LootContainer_*` entities
  made 4 rolls through the getter, all 4 with the new entry; the self-check (getter called through the hook +
  LootFind) `{"count":4214,"newAtTheEnd":1,"lootFind":1}`.
- **Drop tables**: `applyDefaultInventory` (`0x19b830` / `0x19bc90`) acquires the table, walks it from the root group
  (`0x1a1cb0`, headers re-read at every visit), releases the handle (`h+0` resource system, `h+0x10` id; `h+8` data
  is not used for the release). Selector groups (`0x1a2140`) pick by weight among at most **64** entries (stacks'
  `weight`, child groups at 100). The stack applier `0x1a1d70` rolls `spawnProbability`, then resolves **`item` by id**
  (ItemRegistryFind; the `itemRef` GUID is never read; unknown id: skipped silently), count in `[countMin,
  countMax]` scaled by Loot Amount. Nothing cached between applications. Lab **[rt]**: the scavenger's table
  `a20e8eae…` with a child group of two new stacks rolled "Material_T1_Metal_Scraps x12, Kiln_Kitbash_Token x6,
  Kiln_Kitbash_Coin x4" (Loot Amount 2).
- **Item knowledge** (correction to section 7): in the known check `0xe76f30(itemId, ItemKnowledgeDatabase)`
  (server `0x91e050`), `db+0x98` is the **ItemRegistry** (its `+0x38` id map), not a knowledge map. An item that is
  registered and has no `ItemKnowledge` record in the resource (StaticHashMap, 8192 buckets, `ItemKnowledge` 56 B:
  `lockedKnowledgeMask` bit 3 = recipes) **is known**; with a record the player's flags (G38Knowledge, keyed by item
  id) decide. New items have no record: known, names shown, requirement-less recipes of only new items listed. There
  is no item codex (`journal_ui` is quests and lore; collections sort by `ItemInfo.collectionInfo`).
- **Traders**: `handle_crafting_events` (`0x8ad90`) checks no workshop (only workshops `0xb326e042` / `0x6f7d5d2f`
  need a permission bit): a recipe with the fisher trader's workshop id `0xc4acc104` is accepted like any recipe. Lab
  **[rt]**: `Kiln_Kitbash_Trade` at `0xc4acc104` registered and listed in that workshop's array. Whether the barter
  screen is the crafting menu config (workshop list at `cfg+0xf098`) is open (client).

## 9. A client world's first steps, and item ids the client does not know (client 26430625) **[dump] [dis] [rt]**
Evidence: minidumps, kiln.log and the game's log of the crashes.

### The crash: `enshrouded.exe+0x3834a4` on a join
- **Where**: program `apply_armor` (update `0x387c50`, descriptor `0x1dfd5b0`; query SlotSelection + ArmorPoints; singletons
  BalancingRegistry, PerkRegistry, `g38_items::ItemRegistry`, `impact::AttributeRegistry`; reads Inventory, ItemState, Level,
  PerkContainerNew). It calls `0x383350` per equipment slot: 2..6, 0 (the held weapon: inventory slot 0x19), 7, ...
- **The unchecked read** **[dis]**: `0x383350` gets the slot's stack, then the item's level through `0x3b7fb0`, which reads the
  item entity's `Level`, and only without an entity falls back to `0xe8f480`, a registry lookup that fails softly (error 0x1f:
  slot skipped). With a level it calls `ItemRegistryFind` (`0xe94470`), keeps the result in `rdi` unchecked, reads the entity's
  `PerkContainerNew`, and with at least one perk reads `[rdi+0x658]` (`perkIds`). So NULL only faults for an equipped item with
  an item entity, a level and a perk.
- **Crash 1 and 2** **[dump]**: `rdi` = 0x658, `rbp` = 4 perks, stack `{inventory 0x5185 slot 6, id 0x723a87fa (Glacier), item
  entity 0x5180, count 1}`, level 0x36. Registry `[ctx+0x50]` = 0x3004ee53778 in both. Stack through
  `0xa00de0` → `0xa283c0` → SimStep: the first prediction step; the log ends at `prediction world ... started`.
- **Not stale state**: entity 0x5180 and inventory 0x5185 fit the lab's numbering of that join (its `DefaultImpact` entities are
  20875.. = 0x518b.. at 00:34:43.716, the hosted session before it had 6589..).
- **Registries** **[dis]**: a client world creates its own ItemRegistry (`0x3a6224`, `0xe7e560`) and registers the singleton
  (`0x3a7c5d`); teardown destroys it (`0x3a99b0`). The prediction object has no registries: WorldCreate points its storage at the
  client world's tables (`0x983f6b..0x983fe7`), and its entity lookups (`storage+0x30` → `world+0x400`) read the client world's
  entities. There is a third, process-long ItemRegistry in `Game_ClientPermanent` (`+0x238`, vtable `0x1d68ea0`), updated
  at `0x851aa6`; no world reads it.
- **Order in a frame** **[dis]**: the client world's update `0xa1fae0`: when the tick changed, `0xa00de0` steps the prediction world
  first, then SimStep of the client world. Kiln's add used to run in the client world's Pre, so the first prediction step ran
  without the new items in every session.
- **Why not every join crashed: open.** Runs seen: joined as first world OK (one of the two runs had the item);
  joined after a join CRASH; joined after two hosted worlds CRASH; hosted as second world OK. So the first prediction step needs the
  item entity to be there, and that held only in those later joined worlds. Seen but not explained: first worlds go `Load -> Play/0`,
  later ones `Load -> Play/1` (hosted session 2 too, which did not crash); Play to first step was 65 ms in both crashes and
  47-53 ms in the runs that survived. Nothing Kiln keeps across worlds is read before the crash: Kiln's first client-world code
  (its Pre) runs after this step. The fix does not depend on the answer.

### Who else reads a missing entry **[dis]** (scan of the direct call sites, `0x...` on 26430625)
| lookup | sites | result |
|---|---|---|
| `ItemRegistryFind` `0xe94470` | 478 | 372 tested right away; 19 dereference at once (UI and simulation: `0x2251be`, `0x2e5737`, `0x3feeba`, `0xb1a4d2`, ...); 30 pass it on, 57 not classified; `apply_armor` is among the ones passed on (`0xe6e1a0` checks, its own later read does not). Not fixable per site |
| HashMap find `0xe86770` (the small getters) | 17 | all tested |
| `PerkRegistryFind` `0xe986a0` | 7 | all tested |
| `getBuffType` `0xe8c5c0` | 32 | 24 tested, 8 passed to `0x3b3540` / `0x3b6910`, which test it |

The scan is a linear look at the instructions after each call; it can misjudge a site, so these are counts, not proofs.

### An item the client lacks
The server sends ids, never definitions (section 5). The character is the client's file (docs/re/characters.md), its inventory goes
to the server at login, and the server validates stacks (`0x1af860`: validateItemStack, `0x1b7f10`, then "Invalid Item Stack"):
| case | server | client |
|---|---|---|
| the server lacks the mod, this client has it | **[dis]** the stack is dropped at validation, not run here | never sees the id |
| this client lacks the mod, the server has it, `"clients": "mod"` (policy warn lets it in) | keeps it | every lookup misses, for good. A Kiln client: kiln.content answers. A vanilla client: `apply_armor` faults on that item when it is equipped, **seen** (below); since then the server refuses it first unless `modSync` is off (docs/kiln/network.md "Clients without Kiln") |
| `"clients": "kiln"`, or a placeholder of the server | keeps it | misses until `@definitions` arrive after the handshake |
| both lack it, the world recorded it | placeholder `[Missing: mod:item]`, stacks kept | placeholder through `@definitions` |
| both lack it, no record | dropped when the save or character loads (section 7) | never sees the id |

Kiln's placeholders are server-side records, so they never covered the client's first steps. kiln.content answers those misses itself
(docs/kiln/services/content.md, "Client worlds").

### A client without Kiln **[dump] [log]**
Vanilla client (play-once), a character with the Epic Glacier equipped, a lab: `exe+3834a4` reading 0x658, `rdi` = 0x658, the stack through `0x387d42`, `0x9af2e3`, `0xa2c87f`, `0xa011be`,
id 0x723a87fa in `xmm0`. No kiln.content there to answer the miss, so the "not run" row above is now run. Its neighbours in that session crashed earlier, on a template the client lacks
(docs/re/network.md "Vanilla clients"), so both faults are possible on a vanilla join and which comes first depends on the data.

The server can't take this path away from a client it lets in: the item is on the client's own character and only a client with kiln.content answers the lookup.
- Refused clients: never reach it. The refusal happens before any world data ([dis], client check 2 in docs/re/network.md).
- Let-in vanilla clients (`modSync: off`, or a mod that adds items and doesn't state `clients`): can still crash here. That's a stated limit, not a bug to fix on the server.

### Checked without a client **[rt]**
Lab `join-crash`, server 26430253, `<kiln>/data/content/early-test` (the client path on the server world, on the same game functions with the same
prologues): the binding comes before the world's first step and names the right world, the insert runs at registry load, the lookup's self-check
goes through the game's own entry, and newcontent-test still passes:
```
new content: server world 00000000B2410080 is being set up (session 1 next): its item registry is 00000000F070BDC8; definitions go in when it finishes loading
tick: server world 0xb2410080 started (session 1, thread 388)
new content: unknown item ids are answered in this registry (00000000F070BDC8; self-check through the game's lookup: ok)
new content: server world 00000000B2410080: definitions registered 258 ms after its first step (...; at registry load): 2 item(s), 2 recipe(s)
newcontent-test done: 24 PASS, 0 FAIL
```
Not checkable there: a client world, a prediction step, a second world in one process, a held item.
