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
    AttrChangedFn attr_changed = nullptr;
    SlotTransferFn slot_transfer = nullptr;  // feature items.move
    QuickStackFn quick_stack = nullptr;      // features items.move (quick stack by a mod), items.transfer (hook)
    MoveAllFn move_all = nullptr;
    uintptr_t area_deposit = 0;        // feature items.transfer: only checked, never called or hooked
    uintptr_t set_string = 0;          // feature items.names
    uintptr_t add_component = 0;       // feature items.names (lab only: the rename handler's AddComponent)

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

    bool tx_ok = false;       // give/take symbols and constants
    bool program_ok = false;  // own program registration (fallback when kiln.world is absent)
    bool read_ok = false;     // GetComponent
    bool catalog_ok = false;
    bool scan_ok = false;     // own entity-map scan
    bool fuel_ok = false;     // tracked Fuel writes (needs tx_ok's accessors too)
    bool move_ok = false;     // feature items.move: moves by the game's slot transfer, inside the program
    bool transfer_ok = false; // feature items.transfer: the quick-stack / move-all hooks can be installed
    bool names_ok = false;    // feature items.names: container names
};
extern Engine E;

// ---- fixed layouts (types.json + disassembly of this build; see docs/re/items.md) ----
constexpr uint32_t kSetupBlock = 0xF0;  // InventoryTx setup block (InventoryTx_init copies it)
constexpr uint32_t kOpStride = 0x34;    // commit op record
constexpr uint32_t kInventorySize = 96; // 8 x ItemStack {u32 id; u32 count; u32 pide}
constexpr uint32_t kSetupSize = 84;     // InventorySetup
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
};
bool read_inventory(uintptr_t world, void* registry, KilnEntity entity, ReadOut* out, unsigned long* code);
// Entity-map scan. ids: capacity entries; *total = matches.
bool scan_inventories(uintptr_t world, uint32_t flags, KilnEntity* ids, uint32_t capacity, uint32_t* total,
                      unsigned long* code);
// Inventory filter for one entity (used with kiln.world's find).
bool inventory_matches(uintptr_t world, KilnEntity e, uint32_t flags, bool* match, unsigned long* code);

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
};
bool read_pages(const Access& a, KilnEntity e, PageSet* out, bool* initialized);

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
// Returns the items moved; *status is KILN_ITEMS_DONE, PARTIAL, NO_ROOM, NOT_ENOUGH, SLOT_CHANGED or REFUSED, *error the
// game's error code of the last failing transfer, why a reason in words.
struct MoveOutcome {
    int32_t status = KILN_ITEMS_DONE;
    uint32_t error = 0;
    char why[96] = "";
};
uint32_t move_stack_in(const Access& a, void* tx, KilnEntity from, uint64_t slot, KilnEntity to, uint32_t count,
                       uint64_t to_slot, bool allow_equipped, bool actionbar, MoveOutcome* out);

// The slots of an entity's inventories as KilnItemsSlot rows (what a transfer handler's `read` returns).
uint32_t read_slots(const Access& a, KilnEntity e, KilnItemsSlot* out, uint32_t capacity);

// Builds the program-style Access from a transaction the game built (the quick-stack / move-all hooks run inside the
// game's own): the accessors are words of the transaction (docs/re/items.md section 3). Checks them once against the
// world's component map; false when they do not hold (the hooks then do nothing but pass through).
bool access_from_tx(void* tx, Access* a);

// Runs a move request inside the program update: validates, builds a transaction, moves, commits (or rolls back for
// a dry run), and fills the result including the ledger. Guarded: false on a fault.
struct MoveJob {
    KilnItemsMoveRequest r{};
};
bool run_move(const Access& a, const MoveJob& job, uint8_t* tx, KilnItemsMoveResult* res, unsigned long* code);

}  // namespace kitems
