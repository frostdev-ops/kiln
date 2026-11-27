// SPDX-License-Identifier: MIT
// kiln.items: the game side. Function types and layouts of the inventory transaction (svn 1285601; see
// docs/re/items.md), resolved from sigdb/symbols/server.d/items.json. Everything here touches game memory and runs
// under a structured-exception guard (engine.cpp): the functions return false on a fault instead of taking the
// server down. No C++ objects with destructors live in these functions.
#pragma once
#include <cstddef>
#include <cstdint>

#include "kiln_api.h"
#include "moves.h"
#include "services/kiln_component_changes.h"
#include "services/kiln_items.h"

namespace kitems {

// ---- engine value types (MS x64; returned through an explicit out pointer) ----
struct PtrSize {
    void* ptr;
    uint64_t size;
};
struct Result12 {  // {u8 error; pad; u32 zero; u32 value}
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    uint32_t value;
};
struct OptU32 {
    uint32_t value;
    uint8_t has;
    uint8_t pad[3];
};
struct OptU8 {
    uint8_t value;
    uint8_t has;
};
struct OptF32 {
    float value;
    uint8_t has;
    uint8_t pad[3];
};
static_assert(sizeof(Result12) == 12 && sizeof(OptU32) == 8 && sizeof(OptU8) == 2 && sizeof(OptF32) == 8);

using TxCtorFn = void*(__fastcall*)(void* tx);
using TxInitFn = void(__fastcall*)(void* tx, const void* setup);
using TxFn = void(__fastcall*)(void* tx);
using AddItemsFn = Result12*(__fastcall*)(Result12* out, void* tx, uint32_t owner, uint64_t slot, uint32_t item,
                                          uint32_t count, OptU32* level, OptU8* rarity, OptF32* fuel);
using RemoveItemsFn = Result12*(__fastcall*)(Result12* out, void* tx, uint32_t owner, uint32_t item, uint64_t slot,
                                             uint32_t count);
// The output half of applyRecipe: 0 ok, 2 no room, 4 requirements not met, 5 props missing (docs/re/items.md section 10).
using RecipeApplyFn = uint32_t(__fastcall*)(void* tx, const void* recipe, const void* inputs, uint64_t props,
                                            const void* station, const uint32_t* workshop, uint32_t owner, int32_t count);
using RecipeFindFn = void*(__fastcall*)(uint32_t id, void* registry);
using ReadOtherFn = PtrSize*(__fastcall*)(PtrSize* out, void* storage, uint64_t accessor, uint32_t entity);
using JobStorageFn = void*(__fastcall*)(void* job);
using ItemFindFn = void*(__fastcall*)(uint32_t id, void* registry);
using GuidToIdFn = uint32_t(__fastcall*)(const void* guid);
using ComponentMapFindFn = uint16_t*(__fastcall*)(void* map, const uint32_t* hash);
using GetComponentFn = PtrSize*(__fastcall*)(PtrSize* out, uintptr_t world, uint32_t hash, uint32_t entity);
using RegisterProgramFn = uint8_t(__fastcall*)(uintptr_t world, void* desc, uint8_t disabled);
using RegisterAllFn = void(__fastcall*)(uintptr_t world);
using IterBeginFn = void(__fastcall*)(void* ctx, uint64_t* iter, uint32_t size);
// Tracked writes (docs/re/items.md section 12): WriteOther returns another entity's component like ReadOther and marks
// it written in the job's tracker (replication and the save); the job's tracker and the flag come from two getters.
using WriteOtherFn = PtrSize*(__fastcall*)(PtrSize* out, void* storage, void* tracker, uint64_t accessor,
                                           uint32_t entity, uint8_t flag);
using JobTrackerFn = void*(__fastcall*)(void* job);
using JobWriteFlagFn = uint8_t(__fastcall*)(void* ctx);
struct AttrRef {  // {u8 error (0 = found); u32 0; const AttributeDesc*}
    uint8_t error;
    uint8_t pad[3];
    uint32_t zero;
    const uint8_t* desc;  // +0 u32 attribute group (= the component's first u32), +0xC u16 index into its values
};
using AttrFindFn = AttrRef*(__fastcall*)(AttrRef* out, uint32_t attribute, void* registry);
using AttrChangedFn = void(__fastcall*)(void* component, void* registry);  // re-runs the attribute's formulas (clamps)
// The game's slot transfer (EMF "moveStack", shroudlink `transfer`; docs/re/items.md section 13): moves a stack
// (mode 0), half of it (1) or one item (2) from slot `src` to slot `dst` (InventorySlotId packed into a register: entity
// | index << 32) inside the transaction, item entity and all; Result12.value = items moved.
using SlotTransferFn = Result12*(__fastcall*)(Result12* out, void* tx, uint64_t dst, uint64_t src, int32_t mode);
// Quick stack (a player's R key; once per chest for a Quick Stack Station) and move all: shroudlink's `quick_stack` /
// `move_all` hook points. Both run on the game's transaction `tx`.
using QuickStackFn = Result12*(__fastcall*)(Result12* out, void* tx, uint32_t to, uint32_t from, void* feedback);
using MoveAllFn = Result12*(__fastcall*)(Result12* out, void* tx, uint32_t from, uint32_t to, uint32_t mode, uint8_t flag);
// The game's equip and unequip (v5, docs/re/items.md section 16): (out, tx, InventorySlotId, keen::EquipmentSlot). The
// player is the running program's current entity (ProgramContext + 8), not an argument.
using EquipFn = Result12*(__fastcall*)(Result12* out, void* tx, uint64_t slot, uint8_t equipment_slot);

// The selection half of EquipItem, queues op7; it never moves the stack or commits (docs/re/items.md section 21).
using SelectEquipmentFn = Result12*(__fastcall*)(Result12*, void* tx, uint8_t remove, uint32_t player,
                                                 const void* selection, uint8_t slot, uint64_t cell, const void* info, uint32_t pide);
// EquipItem's first decision (docs/re/items.md section 22): the inventory link category an item of this ItemInfo's
// EquipmentSlotType is equipped in. 4 (Generic) = selected in place where it lies, 2 (Equipment) = moved to an
// equipment page, 0 = not equippable. A pure leaf: it reads ItemInfo+0x104 and a jump table.
using EquipLocationKindFn = uint8_t(__fastcall*)(const void* info);

// ---- what the service resolved at boot ----
struct Engine {
    TxCtorFn tx_ctor = nullptr;
    TxInitFn tx_init = nullptr;
    AddItemsFn add_items = nullptr;
    RemoveItemsFn remove_items = nullptr;
    RecipeApplyFn recipe_apply = nullptr;  // optional: feature items.recipe
    RecipeFindFn recipe_find = nullptr;
    TxFn commit = nullptr, rollback = nullptr, free_arrays = nullptr;
    ReadOtherFn read_other = nullptr;
    JobStorageFn job_storage = nullptr;
    ItemFindFn item_find = nullptr;
    GuidToIdFn guid_to_id = nullptr;
    ComponentMapFindFn component_map_find = nullptr;
    GetComponentFn get_component = nullptr;
    RegisterProgramFn register_program = nullptr;
    RegisterAllFn register_all = nullptr;
    IterBeginFn iter_begin = nullptr;
    WriteOtherFn write_other = nullptr;      // feature items.fuel
    JobTrackerFn job_tracker = nullptr;
    JobWriteFlagFn job_write_flag = nullptr;
    AttrFindFn attr_find = nullptr;
    bool wear_ok = false;  // wear.h's facts resolved on an approved build (items.tool_wear_debit)
    AttrChangedFn attr_changed = nullptr;
    SlotTransferFn slot_transfer = nullptr;  // feature items.move
    QuickStackFn quick_stack = nullptr;      // features items.move (quick stack by a mod), items.transfer (hook)
    MoveAllFn move_all = nullptr;
    uintptr_t area_deposit = 0;        // feature items.transfer: only checked, never called or hooked
    uintptr_t set_string = 0;          // feature items.names
    uintptr_t add_component = 0;       // features items.names (the lab's self-test) and items.rename: the rename handler's AddComponent
    uintptr_t write_component_tick = 0;  // feature items.rename: the rename handler's tracked write (WriteOther, the world's tracker)
    EquipFn equip_item = nullptr;      // feature items.equip (v5)
    EquipFn unequip_item = nullptr;
    SelectEquipmentFn select_equipment = nullptr;
    bool give_equip_ok = false;
    // feature items.give_equip.instance_ammo (#139): instance-backed Arrow/Spell/FishingBait (eternal spells) given in
    // place. Needs give_equip_ok, the game's EquipLocationKind and the in-place equip/unequip/commit probes.
    EquipLocationKindFn equip_location_kind = nullptr;
    bool give_equip_instance_ammo_ok = false;
    // feature items.equip_sets (v6): the commit's own rollback conditions, captured from InventoryCommit's first bytes
    // (sigdb @items/CommitGuards): flags byte (bit 0x40 = roll back), the InventoryEmptyState accessor's slot, the touched
    // inventories' count. A set refuses before the commit when either would make the commit roll back by itself.
    int64_t tx_guard_flags = 0, tx_empty_state = 0, tx_touched_count = 0;

    int64_t world_storage = 0;      // world -> entity storage
    int64_t tx_size = 0;            // InventoryTx bytes (result flags + 8, rounded to 16)
    int64_t tx_ops = 0;             // tx: op array {ptr, count} (0x34-byte records)
    int64_t tx_operator = 0;        // tx: operator entity (from setup + setup_operator)
    int64_t setup_operator = 0;     // setup block: operator entity
    int64_t item_flags = 0;         // ItemInfo: ItemFlag bits (bit 1 HasPIDE)
    int64_t item_max_stack = 0;     // ItemInfo: u16 maxStackSize
    int64_t setup_initialized = 0;  // InventorySetup: u8 isInitialized
    int64_t registry_items = 0;     // ItemRegistry: HashMap<ItemId, ItemInfo*>
    int64_t registry_components = 0;// ECS registry: HashMap<qualifiedHash, u16 type index>
    int64_t storage_entities = 0;   // entity storage: HashMap<EntityId, record*>
    int64_t hmap_bits = 0, hmap_capacity = 0, hmap_keys = 0, hmap_values = 0, hmap_count = 0;
    int64_t program_count = 0;      // world: u16 registered programs
    int64_t strings_bitmap = 0;     // CustomPlayerStringsSystem: its HashMap's bitmap pointer (the map starts hmap_bits before it)
    // feature items.rename (v9): the string table's record count, the insert's fixed limit, its record pool's capacity
    int64_t strings_used = 0, strings_limit = 0, strings_pool_cap = 0;
    int64_t storage_next_id = 0;    // feature items.instances: entity storage: the u32 counter new entity ids come from
    // feature items.instances: the attribute ids the save record builder reads (durability, fuel, level)
    uint32_t attr_durability = 0, attr_durability_max = 0, attr_fuel = 0, attr_fuel_max = 0, attr_level = 0;

    bool tx_ok = false;       // give/take symbols and constants
    bool program_ok = false;  // own program registration (fallback when kiln.world is absent)
    bool read_ok = false;     // GetComponent
    bool catalog_ok = false;
    bool scan_ok = false;     // own entity-map scan
    bool fuel_ok = false;     // tracked Fuel writes (needs tx_ok's accessors too)
    bool move_ok = false;     // feature items.move: moves by the game's slot transfer, inside the program
    bool transfer_ok = false; // feature items.transfer: the quick-stack / move-all hooks can be installed
    bool names_ok = false;    // feature items.names: container names
    bool rename_ok = false;   // feature items.rename (v9): renaming a container through the game's rename
    bool instances_ok = false;  // feature items.instances: stamps, locations, records (v4)
    bool equip_ok = false;      // feature items.equip: the game's equip / unequip resolved (v5; also needs kiln.world)
    bool sets_ok = false;       // feature items.equip_sets: equip plus the commit's guards (v6; also needs kiln.decisions)
};
extern Engine E;
// After resolve(): blank the game functions a feature that is off must never call. items.move off drops the slot transfer
// only; quick stack and move all stay while items.transfer can still hook them, and go only when both features are off.
void gate_features();

// ---- fixed layouts (types.json + disassembly of this build; see docs/re/items.md) ----
constexpr uint32_t kSetupBlock = 0xF0;  // InventoryTx setup block (InventoryTx_init copies it)
constexpr uint32_t kOpStride = 0x34;    // commit op record
constexpr uint32_t kInventorySize = 96; // 8 x ItemStack {u32 id; u32 count; u32 pide}
constexpr uint32_t kSetupSize = 84;     // InventorySetup
constexpr uint32_t kSetupFlags = 0x50;  // InventorySetup: u8 flags (bit 0 a player's, bit 1 an NPC's inventory)
constexpr uint32_t kItemStateSize = 20; // ItemState (on a pide)
constexpr uint32_t kMaxPlayer = 1023;
constexpr uint32_t kRecipeWorkshopId = 0x24;  // RecipeInfo.workshopId (the recipe function compares it with its argument)

// Component hashes: FNV-1a-32 of "keen::ecs::<name>".
constexpr uint32_t kHashInventory = 0xb19528a9u;
constexpr uint32_t kHashInventorySetup = 0xc24df82au;
constexpr uint32_t kHashItemState = 0x3a2fac75u;
constexpr uint32_t kHashFuel = 0x04182abbu;
// Attribute ids: FNV-1a of the attribute's GUID (docs/re/items.md section 11).
constexpr uint32_t kAttrFuel = 0x05b0bf75u;
constexpr uint32_t kAttrFuelMax = 0xa00e2601u;

// The program's other-entity accessors, in the order of this enum (not the iterator's order: the service matches
// accessors to names by their component type index).
enum Acc : int { A_FUEL, A_INVENTORY, A_EMPTY, A_SETUP, A_ITEMSTATE, A_OWNER, A_SLOTSEL, A_COUNT };
extern const char* const kAccNames[A_COUNT];
extern const uint32_t kAccHashes[A_COUNT];

// What one program update hands the transaction code.
struct Access {
    void* ctx = nullptr;      // ProgramContext*
    void* job = nullptr;      // *ctx
    void* storage = nullptr;  // job -> entity storage
    void* balancing = nullptr;
    void* items = nullptr;    // g38_items::ItemRegistry
    void* attributes = nullptr;
    void* knowledge = nullptr;  // KnowledgeTriggerQueue (optional)
    void* recipes = nullptr;    // g38_items::RecipeRegistry (only for KILN_ITEMS_RECIPE)
    uint64_t acc[A_COUNT] = {};
    void* ev_pickup = nullptr;
    void* ev_full = nullptr;
};

// One queued request as the program sees it.
struct Job {
    bool take = false;
    KilnItemsRequest r{};
    uint32_t dye = 0;          // v7 KILN_ITEMS_DYE (from KilnItemsRequestEx)
    float durability = 1.0f;   // v7 KILN_ITEMS_DURABILITY
};

// A Fuel request (set / add / fill) or one keep-full entry, as the program sees it.
struct FuelJob {
    KilnItemsFuelRequest r{};
};

// ---- guarded game calls (engine.cpp). Each returns false on a fault (code = exception code). ----

// Checks the iterator of one program update: singletons, accessor type indices against the world's component map,
// event-sender slots. `acc_slots` are the iterator indices of the 7 other-entity accessors (any order).
// Fills `a` on success; on a failed check writes the reason to why[160] and returns ok=false.
bool verify_access(const uint64_t* it, const int* singleton_slots, const int* acc_slots, const int* event_slots,
                   void* ctx, Access* a, bool* ok, char* why, unsigned long* code);

// With the equipment program's current player entity; owns fault cleanup and never rolls back after commit starts.
bool run_give_equip(const Access& a, const KilnItemsGiveEquipRequest& r, uint8_t* tx, KilnItemsEquipResult* result, unsigned long* code);

// Runs one give / take inside the program update. tx: E.tx_size bytes, 16-aligned.
bool run_job(const Access& a, const Job& job, uint8_t* tx, KilnItemsResult* res, unsigned long* code);
// After a fault inside run_job: best-effort rollback + free (guarded).
void abandon_tx(uint8_t* tx);

// Runs one Fuel request inside the program update: tracked writes (WriteOther + AttrChanged), only where the value
// changes. res->changes lists what changed (up to KILN_ITEMS_MAX_FUEL_CHANGES).
bool run_fuel(const Access& a, const FuelJob& job, KilnItemsFuelResult* res, unsigned long* code);

// Tick thread: an entity's Fuel through GetComponent (no write). found=false: it has none.
bool read_fuel(uintptr_t world, void* attributes, KilnEntity entity, int32_t* current, int32_t* max, bool* found,
               unsigned long* code);

// Tick thread, no world job running: reads through GetComponent.
struct ReadOut {
    KilnItemsInventory info;
    KilnItemsSlot* slots;
    uint32_t capacity;
    uint32_t written;
    bool found;
    // set by the caller: leave the stamp table alone (slot.reserved stays 0). kiln.stations.client reads the client world,
    // whose item entities the server-world stamp table must not learn (review 31.13).
    bool no_stamp = false;
};
bool read_inventory(uintptr_t world, void* registry, KilnEntity entity, ReadOut* out, unsigned long* code);
// Entity-map scan. ids: capacity entries; *total = matches.
bool scan_inventories(uintptr_t world, uint32_t flags, KilnEntity* ids, uint32_t capacity, uint32_t* total,
                      unsigned long* code);
// Inventory filter for one entity (used with kiln.world's find).
bool inventory_matches(uintptr_t world, KilnEntity e, uint32_t flags, bool* match, unsigned long* code);
// InventorySetup.inventoryFlags of one entity (kiln.items.inventory_kinds); false: no setup, or a fault.
bool inventory_setup_flags(uintptr_t world, KilnEntity e, uint8_t* flags);

// Catalog rows copied out of the item registry.
struct RawItem {
    KilnItemId id;
    uint8_t guid[16];
    uint16_t max_stack;
    uint8_t category, rarity, equip_slot, gear;
    uint32_t item_flags, level_min, level_max;
    uint32_t guid_id;  // GuidToItemId(guid): must equal id
    uint16_t name_len;
    char name[118];
};
// Count of the registry's items (0 when it looks wrong).
bool registry_count(void* registry, uint32_t* count, unsigned long* code);
bool copy_registry(void* registry, RawItem* rows, uint32_t capacity, uint32_t* written, unsigned long* code);

// Own program registration (no kiln.world): validates the component names, then RegisterProgram.
bool register_program(uintptr_t world, void* desc, int* outcome, char* why, unsigned long* code);

// ---- moves (v3) ----
constexpr uint32_t kHashSlotSelection = 0x34bc3e2fu;
constexpr uint32_t kHashCustomPlayerString = 0x0e699564u;  // keen::ecs::CustomPlayerString: {u64 string id}, 0 = no name

// The 8-slot inventories of an entity with their stacks, in link order (Virtual links expanded), read through the
// accessors of `a` (the program's, or a game transaction's: access_from_tx).
struct PageSet {
    Page p[KILN_ITEMS_MAX_INVENTORIES];
    uint32_t n = 0;
    uint8_t cat[KILN_ITEMS_MAX_INVENTORIES] = {};  // each page's InventoryCategory (its link's; Virtual links expanded)
    uint8_t flags = 0;                             // the root InventorySetup's flags (bit 0 a player's, bit 1 an NPC's)
};
bool read_pages(const Access& a, KilnEntity e, PageSet* out, bool* initialized);
// Upgrades (v8, package P3, gear.cpp): opens a transaction of ours on `tx` (E.tx_size bytes, 16-aligned) with the
// accessors of `a` and `entity` as its operator, no flags (the caller commits or rolls back, then frees its arrays);
// and whether every op queued on it takes a plain stack (op 1 without an item entity). Not guarded: call them inside
// the caller's structured-exception guard.
void tx_begin(const Access& a, KilnEntity entity, uint8_t* tx);
bool tx_takes_plain_only(const uint8_t* tx);
// InventorySetup's flags byte (kSetupFlags) through the accessors of `a`. False: no setup (or a fault).
bool setup_flags(const Access& a, KilnEntity e, uint8_t* flags);

// A player's own stacks, from the entity's SlotSelection: whether it is a player at all (an action-bar page is set), the
// two action-bar pages, and the equipped slots plus the item in hand (each a slot id).
struct PlayerGuard {
    bool is_player = false;
    uint32_t actionbar[2] = {0, 0};
    uint64_t protect[24];
    uint32_t protect_n = 0;
    bool protects(uint64_t slot) const {
        for (uint32_t i = 0; i < protect_n; ++i)
            if (protect[i] == slot) return true;
        return false;
    }
};
void player_guard(const Access& a, KilnEntity e, PlayerGuard* g);

// Moves `count` items (0 = all) of the stack in `slot` of `from` into `to` through the game's slot transfer in the
// transaction `tx` (the caller owns it: program side, a transaction of ours; hook side, the game's own). Never takes
// a player's equipped or held stack unless `allow_equipped`, never fills a player's action bar unless `actionbar`.
// Into a person (a player or an NPC) it lands only where choose_destination (moves.h) allows: the Generic pages, the
// worn backpack's included, or a named `to_slot` the item may enter (issue #109).
// Returns the items moved; *status is KILN_ITEMS_DONE, PARTIAL, NO_ROOM, NOT_ENOUGH, SLOT_CHANGED or REFUSED, *error the
// game's error code of the last failing transfer, why a reason in words.
struct MoveOutcome {
    int32_t status = KILN_ITEMS_DONE;
    uint32_t error = 0;
    char why[96] = "";
    uint64_t named = 0;  // the caller's to_slot when choose_destination accepted it and something went there
};

// The destination rules for a stack of the item `info` (an ItemInfo, or null) moving into `to`, whose pages are `tp`
// (moves.h DestRules): a person or not, the action bars, SlotSelection.equipment. Not guarded.
void dest_rules_for(const Access& a, KilnEntity to, const PageSet& tp, bool actionbar, const uint8_t* info, DestRules* r);
uint32_t move_stack_in(const Access& a, void* tx, KilnEntity from, uint64_t slot, KilnEntity to, uint32_t count,
                       uint64_t to_slot, bool allow_equipped, bool actionbar, MoveOutcome* out);

// The slots of an entity's inventories as KilnItemsSlot rows (what a transfer handler's `read` returns).
uint32_t read_slots(const Access& a, KilnEntity e, KilnItemsSlot* out, uint32_t capacity);

// Builds the program-style Access from a transaction the game built (the quick-stack / move-all hooks run inside the
// game's own): the accessors are words of the transaction (docs/re/items.md section 3). Checks them once against the
// world's component map; false when they do not hold (the hooks then do nothing but pass through).
bool access_from_tx(void* tx, Access* a);
// GitHub #147: the reads of a server-world transaction whose program declares no Fuel (interaction_attempt's lock
// payment): ctx, items and the InventorySetup / Inventory accessors, checked against the registry, the server world only.
// The other accessors are copied as the transaction holds them (0 when it has none). For read_pages / setup_flags only.
bool access_from_tx_reads(void* tx, Access* a);

// Runs a move request inside the program update: validates, builds a transaction, moves, commits (or rolls back for
// a dry run), and fills the result including the ledger. Guarded: false on a fault.
struct MoveJob {
    KilnItemsMoveRequest r{};
    KilnItemsSlotWitness source{}, destination{};
};
bool run_move(const Access& a, const MoveJob& job, uint8_t* tx, KilnItemsMoveResult* res, unsigned long* code);

// ---- kiln.component_changes (since 0.2.0-dev.29, chapter 07 S7) ----
// A job of this service may change Inventory (give, take, recipes, moves, equipment, sets), Fuel (water) and
// SlotSelection of the entities it names and of their inventory trees. While anything is watched
// (kiln_component_watching), NoteLease::begin walks those trees (link ids only, no copy) and asks the service's filter
// (kiln_component_watched) for each entity and component; only those it may watch are copied, into a note lent from a
// small pool (no allocation once the pool has its notes; none at all when nothing the job touches is watched).
// NoteLease::end compares them after the job; each one that changed is told to kiln.component_changes as kiln.items'
// write. Guarded (a fault copies or tells nothing). An accessor the program does not have is skipped.
struct WatchNote {
    struct Item {
        KilnEntity entity;
        uint32_t index, size;  // component index (kiln_component_changes.h), bytes
        uint8_t bytes[256];
    };
    static constexpr uint32_t kMax = 600;  // two holders' trees: SlotSelection, Fuel, 32 x (Inventory + 8 item entities)
    uint32_t count = 0;
    Item items[kMax];
};
// on_ready: the table (null: kiln-decisions.dll is not loaded) and the service's owner.
void note_setup(const KilnComponentChangesApiV1* table, KilnOwner owner);
// Whether a job should be noted now (anything watched). Any thread, lock-free.
bool note_wanted();
class NoteLease {
public:
    NoteLease() = default;
    NoteLease(const NoteLease&) = delete;
    NoteLease& operator=(const NoteLease&) = delete;
    ~NoteLease();
    // Before the job: the watched components of the holders' trees (none copied when none is watched).
    void begin(const Access& a, const KilnEntity* holders, uint32_t n);
    // After the job: what changed is told.
    void end(const Access& a);
    explicit operator bool() const { return note_ != nullptr && note_->count != 0; }
    // test and lab: how many components begin copied
    uint32_t copied() const { return note_ ? note_->count : 0; }

private:
    friend void note_add(const Access& a, uint32_t index, KilnEntity e, NoteLease* lease);
    WatchNote* note_ = nullptr;
    int slot_ = -1;  // the pool's slot, -1 for a note of its own (the pool was busy)
};
// The pool's notes made so far and the notes made outside it (lab counters).
void note_pool_stats(uint32_t* pooled, uint64_t* extra);

// ---- equipment sets (v6, setexec.cpp) ----
namespace sets {
struct SetJob;
struct SetOutcome;
}  // namespace sets
// Runs one set inside kiln_items_equip's update of `player` (the program's accessors in `a`, its transaction buffer
// `tx`): the snapshot checked again, the plan, one transaction and its terminal state (setexec.h). Every game call is
// guarded; a fault ends it with out->engine_fault set.
void set_run(const Access& a, KilnEntity player, uint8_t* tx, const sets::SetJob& job, sets::SetOutcome* out);

}  // namespace kitems
