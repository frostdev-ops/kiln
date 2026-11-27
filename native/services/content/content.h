// SPDX-License-Identifier: MIT
// kiln.content: internal declarations shared by the service's files.
//   game.cpp       guarded memory access, page-protection-aware writes, near allocation (nearmem.h / nearwin.h), the game's hash maps
//   refl.cpp       the game's reflection (TypeByHash) -> flattened field schemas
//   registries.cpp items / recipes / random-loot items / drop tables
//   store.cpp      per-owner layers over the game's original bytes; conflicts
//   packs.cpp      the data-pack JSON format (apply, dump, discovery)
//   content.cpp    boot, tick, the C table
// Evidence and layouts: docs/re/content.md.
#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_content.h"

namespace kc {

extern const KilnApi* g_api;
extern KilnOwner g_owner;
void logf(int level, const char* fmt, ...);
#define LOGI(...) ::kc::logf(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) ::kc::logf(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) ::kc::logf(KILN_LOG_ERROR, __VA_ARGS__)
#define LOGD(...) ::kc::logf(KILN_LOG_DEBUG, __VA_ARGS__)

uint32_t fnv1a(const void* data, size_t n);
inline uint32_t fnv1a(const std::string& s) { return fnv1a(s.data(), s.size()); }
std::string guid_text(const uint8_t g[16]);       // the game's GUID byte order -> "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
std::string ref_text(const uint8_t g[16]);        // guid_text, or "none" for all zero: what a rule writes for no reference, so it can be applied again
bool guid_parse(const std::string& s, uint8_t out[16]);
std::string lower(std::string s);

// ============================================================================================= resolved layout ====
struct Engine {
    using TypeByHashFn = uintptr_t(__fastcall*)(uint32_t hash);
    TypeByHashFn type_by_hash = nullptr;
    uintptr_t default_inventory_acquire = 0;  // hook target
    uintptr_t stack_clamp = 0;                // the `jbe` of validateItemStack's clamp
    int64_t type_hash = 0x50, type_qname = 0x20, type_inner = 0x38, type_size = 0x40, type_count = 0x48,
            type_kind = 0x4C, type_fields = 0x58, type_enums = 0x60, field_stride = 0x30, field_type = 0x10,
            field_offset = 0x18, enum_stride = 0x28, enum_value = 0x10;
    int64_t items_map = 0, items_loading = 0, items_started = 0;
    int64_t recipes_map = 0, recipes_resource = 0;
    int64_t lootgen_lootreg = 0, lootreg_map = 0;
    int64_t hm_bits = 8, hm_cap = 0x10, hm_keys = 0x28, hm_values = 0x40, hm_count = 0x54;
    bool reflection = false, items = false, recipes = false, loot = false, drops = false, guard = false;
};
extern Engine E;

// ============================================================================================= game.cpp =========
bool rd(uintptr_t address, void* out, size_t n);  // guarded copy out of game memory
template <class T>
bool rdv(uintptr_t a, T& v) {
    return rd(a, &v, sizeof v);
}
std::string read_text(uintptr_t ptr, uint64_t len);   // guarded; "" when unreadable or longer than 512
std::string read_blob_string(uintptr_t field);         // keen::BlobString {i32 self-relative offset; u32 length}
uintptr_t type_by_hash(uint32_t hash);                  // guarded call of the game's TypeByHash

// A hash map of the game (keys u32, values u64): every occupied slot. false: unreadable / implausible.
bool map_read(uintptr_t map, std::vector<uint32_t>* keys, std::vector<uint64_t>* values);
uint32_t map_count(uintptr_t map);  // 0 when unreadable

struct Write {
    uintptr_t addr;
    uint8_t n;
    uint8_t bytes[16];
};
// Copies every write into game memory. Read-only private pages (the game's resource memory: item, recipe and loot
// data) are made writable for the copy and set back. Image, mapped and no-access memory is refused. Returns how many
// writes landed; *failed (optional) = the first failure's address.
size_t write_batch(std::vector<Write>& writes, uintptr_t* failed);
struct WriteStats {
    uint64_t direct = 0, unprotected = 0, protect_calls = 0, refused = 0;
};
WriteStats write_stats();  // totals since boot
// One byte of code (the stack guard): PAGE_EXECUTE_READ made writable for the write, instruction cache flushed.
bool patch_code_byte(uintptr_t address, uint8_t expect, uint8_t value);
// Memory within 2 GB (less 16 MB) of `target` (for self-relative BlobArray offsets into the game's data). Never freed. The policy,
// and why it reserves at boot, is nearmem.h; a failure is logged there with what the scan saw.
void near_alloc_boot();  // at boot, before the game fills the address space near its resources
void* near_alloc(uintptr_t target, size_t n);
void near_alloc_add(uintptr_t base, size_t size);  // memory of Kiln's own that near_alloc may hand out (a clone's spare room)
std::string near_alloc_report();  // the chunks in use, for a log line

// ============================================================================================= refl.cpp =========
struct EnumInfo {
    std::string type;
    std::vector<std::pair<std::string, int64_t>> values;
};
struct Field {
    std::string path;
    std::string type_name;
    uint32_t offset = 0;
    uint8_t bytes = 0;
    uint8_t vt = KILN_CV_NONE;  // KILN_CV_*
    bool is_signed = false;
    bool f64 = false;
    bool writable = true;
    bool ref = false;  // a 16-byte GUID / ObjectReference field: not writable as a value, settable as a reference (refs.cpp)
    uint8_t enum_kind = KILN_CONTENT_PLAIN;
    std::shared_ptr<EnumInfo> en;
};
struct Schema {
    std::string type;
    uint32_t size = 0;
    std::vector<Field> fields;
    std::unordered_map<std::string, uint32_t> by_path, by_lower;
    std::unordered_map<std::string, uint32_t> special;  // top-level non-scalar fields: name -> offset (BlobArray, ...)
    const Field* find(const std::string& path) const;
};
// Flattens `qname`'s scalar fields. `readonly`: paths (top level) that may not be written.
bool build_schema(const char* qname, const std::vector<std::string>& readonly, Schema* out, std::string* err);
// Offset of a direct field of a reflected struct; -1 when missing.
int64_t field_offset(const char* qname, const char* field);
uint32_t type_size(const char* qname);
// A reflected struct that has `base` among its base types (keen::DefaultLootSettingsResource of keen::LootSettingsResource): the
// base's members are at the struct's start. field_offset and build_schema see a struct's own members only.
bool derives_from(const char* qname, const char* base);
// The values of a reflected enum (name, value); false when the type is not an enum of the game's reflection.
bool enum_values(const char* qname, std::vector<std::pair<std::string, int64_t>>* out);
// Offsets of every BlobArray / BlobString in the inline part of a struct (nested structs, static arrays): what must be
// rebased when the struct is copied (new content). false + *err for kinds it does not understand.
bool self_relative_fields(const char* qname, std::vector<uint32_t>* out, std::string* err);

// ============================================================================================= values =========
// Decode / encode a field's bytes. encode: false + *why when the value does not fit the field.
KilnContentValue decode(const Field& f, const uint8_t* bytes);
bool encode(const Field& f, const KilnContentValue& v, uint8_t* bytes, std::string* why);
double as_double(const KilnContentValue& v);
std::string show(const Field& f, const KilnContentValue& v);  // for logs and reports

// ============================================================================================= registries =====
struct Entry {
    uint32_t id = 0;
    std::string name;
    uintptr_t addr = 0;
    uint8_t guid[16] = {};
};
struct Registry {
    uint32_t kind = 0;
    const char* label = "";
    bool available = false;
    Schema schema;
    std::vector<Entry> entries;
    std::unordered_map<uint32_t, uint32_t> by_id;
    std::unordered_map<std::string, uint32_t> by_name, by_lower;
    std::unordered_map<uintptr_t, uint32_t> by_addr;
    void add(Entry e);
    const Entry* get(uint32_t id) const;
    const Entry* lookup(const std::string& key) const;  // name, case-insensitive name, 0x id, decimal id, GUID (drops)
};
Registry& reg(uint32_t kind);  // 1..6 (KILN_CONTENT_ITEMS .. KILN_CONTENT_RANDOM_LOOT)
bool valid_kind(uint32_t kind);

// Recipe layout (from reflection; filled by registries.cpp).
struct RecipeLayout {
    uint32_t input = 0, output = 0;       // BlobArray fields in RecipeInfo
    uint32_t in_stride = 48, out_stride = 24;
    uint32_t in_item_ref = 0, in_item = 0x10, in_count = 0x14;      // RecipeInputResource.itemStack
    uint32_t in_cat_ref = 0x18, in_cat = 0x28, in_cat_count = 0x2C; // .inputItemCategory
    uint32_t out_item_ref = 0, out_item = 0x10, out_count = 0x14;
    uint32_t debug_name = 0x78, guid = 0;
};
extern RecipeLayout RL;
struct DropLayout {
    uint32_t group_size = 36, group_stacks = 4, group_groups = 0xC;
    uint32_t stack_size = 40, s_item = 0x10, s_min = 0x14, s_max = 0x18, s_chance = 0x1C, s_weight = 0x20;
};
extern DropLayout DL;
extern std::unordered_map<uint32_t, std::vector<uint8_t>> g_category_guid;  // input category id -> GUID bytes

// Snapshot of the four registries (tick thread). false + *why when nothing usable.
bool build_registries(uintptr_t world, std::string* why);
// The loot rules (one entry) and the random loot tables, from resident resources (kiln.assets) and, for the rules, the loot
// registry `lreg` (0: none). Called by build_registries; off in a joined client's world.
void build_loot_tables(uintptr_t lreg);
// Stacks of a recipe list / a drop table as they are in memory now.
bool read_recipe_stacks(const Entry& e, uint32_t which, std::vector<KilnContentStack>* out);
struct DropStackRef {
    uintptr_t addr;   // the ItemStackInfoResource
    uint32_t group;
};
bool walk_drop_table(uintptr_t resource, std::vector<DropStackRef>* out);
void read_drop_stack(uintptr_t addr, uint32_t group, KilnContentStack* out);

// ============================================================================================= store ==========
constexpr uint64_t kCodeSeq = 1ull << 63;  // code layers sort after every data-pack layer
// Registry layers (registry_live.cpp, registry P1 S9): a band above every data-pack layer and below code, so code still wins
// over packs and the registry, and the registry over packs (owner decision D1).
constexpr uint64_t kRegistrySeq = 1ull << 62;
inline uint64_t registry_seq(uint32_t ordinal) { return kRegistrySeq | ordinal; }
inline bool is_registry_seq(uint64_t seq) { return (seq & (kCodeSeq | kRegistrySeq)) == kRegistrySeq; }
// An evaluated layer's value, computed from the value below it (the layers under it, else the original) whenever the slot
// is flushed: a relative registry edit over whatever a pack or the game gives. false (+ *why): no value, the layer holds
// the value below unchanged.
using LayerEval = std::function<bool(const uint8_t* below, uint8_t n, uint8_t* out, std::string* why)>;
constexpr uint8_t kSlotBytes = 16;  // the widest slot: a GUID reference
constexpr int32_t kSubBlob = 100;    // Slot.sub of an item list header (kitbash: per-body / pickup models)
struct Layer {
    KilnOwner owner;
    uint64_t seq;
    uint8_t bytes[kSlotBytes];
    std::string source;
    std::string mod;  // the mod a shared owner put it for (a gamedata definition's layer); empty: the owner's label says ("mod <id>", "pack <id>")
    std::shared_ptr<const LayerEval> eval;  // an evaluated layer (store_put_eval): bytes hold its last value
};
struct Slot {
    uint32_t kind = 0, id = 0;
    int32_t field = -1;           // index into the registry's schema; -1 = a BlobArray header (recipe inputs/outputs)
    int32_t sub = -1;             // drops: stack index; recipes: KILN_CONTENT_INPUTS/OUTPUTS for headers
    uint8_t n = 0;
    uint8_t original[kSlotBytes] = {};
    uint8_t current[kSlotBytes] = {};
    bool dirty = false;
    bool registry = false;  // a registry layer was on it at the last flush or since (its revert is compared too)
    bool foreign = false;   // another writer wrote it after Kiln's registry layer: registry layers left out for this world
    bool old_changed = false;  // a layer of an older path (not the registry) was put or dropped since the last flush
    bool gate = false;      // store_set_gate: written after a flush's other writes when it opens, before them when it closes
    std::vector<Layer> layers;
};
struct Conflict {
    std::string what;     // "items Material_T1_Wood_Logs.maxStackSize"
    std::string winner, loser, winner_value, loser_value;
};
// The one lock of the store, the registries, new content and kiln.gamedata (recursive). It also keeps how long it is held at a time,
// for the load-time report (timing_report).
class StoreMutex {
public:
    void lock();
    void unlock();
    bool try_lock();

private:
    std::recursive_mutex m_;
};
StoreMutex& store_mutex();
bool store_locked_here(); // internal admission for first-use work that must run outside the recursive lock

// Where load time goes: inclusive time per phase (nested phases count in both), and the store lock's longest holds.
enum Phase : uint8_t {
    PH_DEFINE, PH_DEFINE_LOCKED, PH_GD_DEFINE, PH_GD_REGISTER, PH_GD_FIELDS, PH_GD_AFTER, PH_GD_READY, PH_SYNC, PH_SYNC_ITEMS,
    PH_BUILD_CLONE, PH_KITBASH, PH_WORLD_ITEMS, PH_RESOLVE_ITEM, PH_MAP_PUT, PH_KC_ENTRY, PH_SYNC_RECIPES, PH_BUILD_RECIPE,
    PH_WS_ADD, PH_CRAFTED, PH_LOOT, PH_DROPS, PH_FLUSH, PH_WRITE_BATCH, PH_NEAR_ALLOC, PH_APPLY_JSON, PH_APPLY_PACKS,
    PH_BUILD_REGISTRIES, PH_DROP_TABLE, PH_STORE_SCAN, PH_IS_NEW, PH_OWN_LISTS, PH_ASSET_PUT, PH_GAME_REGISTER, PH_BUILD_BYTES, PH_STATUS, PH_LISTING, PH_EXPORT, PH_TEXTS, PH_SEQ_RES, PH_CONFLICTS, PH_PACK_FILE, PH_COUNT
};
class PhaseTimer {
public:
    explicit PhaseTimer(Phase p);
    ~PhaseTimer();
    PhaseTimer(const PhaseTimer&) = delete;
    PhaseTimer& operator=(const PhaseTimer&) = delete;

private:
    Phase p_, outer_;
    int64_t t0_;
};
// "phase total ms / calls (max longest ms), ..." and the store lock's holds and waits. reset: start counting again.
std::string timing_report(bool reset);
std::string lock_report(bool reset);
void timing_mark_job_thread(bool on);  // the calling thread is a game job thread (its waits for the lock are counted apart)

// While one lives on this thread, the layers store_put puts are `mod`'s: a shared owner (kiln.gamedata's definitions) acting
// for one mod. store_conflicts does not count such a layer against the same mod's other layers.
class LayerModScope {
public:
    explicit LayerModScope(const std::string& mod);
    ~LayerModScope();
    LayerModScope(const LayerModScope&) = delete;
    LayerModScope& operator=(const LayerModScope&) = delete;

private:
    std::string prev_;
};
// Adds or replaces `owner`'s layer of the slot at addr (the slot remembers the original bytes on first touch).
bool store_put(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
               uint8_t n, const uint8_t* bytes, const std::string& source);
// The same with an evaluated layer: its value is eval(value below) at every flush of the slot.
bool store_put_eval(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
                    uint8_t n, LayerEval eval, const std::string& source);
// The bytes the slot's layers give now (evaluated layers computed, registry layers of a foreign slot left out), before any
// flush; memory when there is no slot. false: unreadable, or a slot of another size.
bool store_effective(uintptr_t addr, uint8_t n, uint8_t* out);
// The same, with only the layers below the registry band (packs, the original): the value registry layers are computed over.
bool store_below_registry(uintptr_t addr, uint8_t n, uint8_t* out);
// The slot's value with its registry-band layers replaced by one evaluated layer (none: nullptr), layers below and above
// it as they are: what a registry reader sees without some registry rows (review4 NS5). Memory when there is no slot or
// another writer owns it for this world.
bool store_effective_without_registry(uintptr_t addr, uint8_t n, const LayerEval* registry, uint8_t* out);
// The slot at addr (one that exists: after a put) is a gate, a switch the game reads before the slots it gates (a fixed
// rarity's useUiRarity before uiRarity): a flush writes it last when it opens (any byte not zero) and first when it closes
// (all zero), and does not open it when another write of its entry did not land, so no reader sees it on over stale values.
void store_set_gate(uintptr_t addr);
uint32_t store_mark_evals();  // every slot holding an evaluated layer is flushed (evaluated) again; returns how many
bool store_slot_copy(uintptr_t addr, Slot* out);  // a snapshot of the slot and its last evaluated layer bytes; false: none
// What the slot holds, as one number that changes when it does (FNV-1a 64 over the bytes Kiln last wrote there, the foreign
// mark and each layer's owner, band, bytes, source and mod, in order; a layer's seq itself is left out, so a mod setting the
// same value again keeps it); 0: no slot. Cheap: no copy (the registry's later-writes look, registry_live.cpp).
uint64_t store_slot_signature(uintptr_t addr);
// Compare-before-write (sfi R3, owner decision D1): a registry write, or a registry layer's revert, on a slot whose memory is
// not what Kiln last wrote there is not made: another writer (a ShroudForge run-time write in kiln mode, or any other) wrote
// it since. Each such slot once per world (store_reset forgets them).
struct StoreSkipped {
    uintptr_t addr = 0;
    uint32_t kind = 0, id = 0;
    std::string what;  // "items Material_T1_Wood_Logs.maxStackSize": game names, diagnostics only, never in a report (P32)
    std::string mod, source;  // the registry layer's
    bool revert = false;      // the revert of a dropped registry layer
    std::string text;         // without a place ("a slot Kiln wrote ..."); registry_live names the Kiln field
};
std::vector<StoreSkipped> store_skipped();
bool store_drop(KilnOwner owner, uintptr_t addr);  // removes one layer
uint32_t store_drop_owner(KilnOwner owner);         // removes every layer of owner; returns slots affected
bool store_original(uintptr_t addr, uint8_t n, uint8_t* out);  // original bytes if the slot exists
void store_originals(uintptr_t addr, size_t size, uint8_t* out);  // out holds a copy of this memory: what rules changed in it goes back to the original
// The same without the store's lock (a leaf lock only, safe under another subsystem's lock): bytes holds a copy of
// [addr, addr + size); every slot fully inside it gets its original bytes back (review4 NS1, the registry's lazy base).
void store_pristine(uintptr_t addr, size_t size, uint8_t* bytes);
bool store_has(uintptr_t addr);
// Writes every dirty slot's effective bytes (newest layer, else the original). Returns slots written.
size_t store_flush(std::string* error);
// The same; unwritten (optional) receives the address of every slot whose write did not land (memory differs from it after the
// batch), so a caller can charge a failure to the owner of that slot alone.
size_t store_flush_checked(std::string* error, std::vector<uintptr_t>* unwritten);
std::vector<Conflict> store_conflicts();
uint32_t store_changed();  // slots whose current bytes differ from the original
uint64_t store_generation();  // changes with every change of a slot (what store_changed and store_conflicts count follows it)
void store_for_each(const std::function<void(uintptr_t, const Slot&)>& fn);
std::string owner_label(KilnOwner owner);
void set_owner_label(KilnOwner owner, const std::string& label);
uint64_t next_code_seq();
bool owner_has_layers(KilnOwner owner);
std::vector<KilnOwner> store_owners();
// The world is going away: takes every layer off (revert: writes the originals back first) and forgets every slot.
// Returns the slots that had layers.
uint32_t store_reset(bool revert, std::string* error);
// Content transactions (store.cpp): begin sets every layer of `owner` aside (false: the owner has one open, or 64 are
// open); commit forgets it; rollback puts the owner's layers back exactly as they were at begin, writes them, runs
// `settle` (kiln.gamedata's after_changes), then reads every touched field back. store_drop_owner and store_reset end
// an owner's open transaction (Cancelled: its layers went with it). One open transaction per owner.
enum class TxnOutcome { Restored, Unverified, Cancelled };
bool store_txn_begin(KilnOwner owner, uint64_t* id);
bool store_txn_commit(uint64_t id);
TxnOutcome store_txn_rollback(uint64_t id, const std::function<void()>& settle, KilnOwner* owner, uint32_t* fields, std::string* why);
bool store_txn_open(KilnOwner owner);

// Value-level API used by the C table and the packs (tick thread; take store_mutex).
KilnResult set_field(KilnOwner owner, uint64_t seq, uint32_t kind, const Entry& e, const Field& f,
                     const KilnContentValue& v, const std::string& source, std::string* why, int32_t sub = -1);
KilnResult set_recipe_stacks(KilnOwner owner, uint64_t seq, const Entry& e, uint32_t which,
                             const std::vector<KilnContentStack>& stacks, const std::string& source, std::string* why);
// The 8 bytes of a recipe list header pointing at a new list holding stacks (near memory, never freed): what set_recipe_stacks puts.
KilnResult recipe_list_header(const Entry& e, uint32_t which, const std::vector<KilnContentStack>& stacks, uint8_t* hb,
                              std::string* why);
KilnResult set_drop_stacks(KilnOwner owner, uint64_t seq, const Entry& e, const std::vector<KilnContentStack>& stacks,
                           const std::string& source, std::string* why);
KilnContentValue original_value(const Entry& e, const Field& f);
KilnContentValue current_value(const Entry& e, const Field& f);

// ============================================================================================= registry hooks ===
// The registry's live back-end (registry_live.cpp) and kiln.gamedata (gamedata_packs.cpp): one item config value
// ("impactValues.0x727d7f78", StaminaCost) as a store slot, and the tooltip row that shows it when the item has one. false
// + *why: the item has no such config (the live back-end never adds one) or the path is no value.
struct GdValueSlot {
    uintptr_t addr = 0;
    Field f;
    uint32_t kind = 0, id = 0;
    int32_t field = -1;
    uintptr_t tip = 0;  // the tooltip row's value (sint32), 0: none
    uint8_t tip_format = 0;
    bool absent = false;  // false return: this entry lacks the component / config the path starts at (only some entries have one)
};
bool gd_item_config_slot(const Entry& e, const std::string& path, GdValueSlot* out, std::string* why);
// dev.32 registries over kiln.gamedata's sections (creatures: KILN_GD_TEMPLATES, arsenals, perks, skills, buffs, abilities:
// KILN_GD_SEQUENCES), the same shape: the section's entry with this GUID (lower-case text) in this world, else (no GUID: an
// element entry such as a skill node or a buff record) with this 32-bit id, as kiln.content's Entry (id, name, address, GUID; an
// ability: its sequence resource, found through its first part; a clone never: the registry names the game's entries);
bool gd_section_entry(uint32_t section, const std::string& guid, uint32_t num, Entry* out);
// one value below its root (a template's path starts at a component: "Level.dataStorage[0]"; an ability's at its sequence
// resource: "subSequences[0]...") as a store slot of the section,
// with its tooltip row when the path names a config's value; false + *why (out->absent: this entry has none of it);
bool gd_value_slot(uint32_t section, const Entry& e, const std::string& path, GdValueSlot* out, std::string* why);
// the path of its one whole or fractional config below `configs` ("impactValues.0x0a0c87e7"); false + *why "none" / "several";
bool gd_shown_config(uint32_t section, const Entry& e, const std::string& configs, std::string* path, std::string* why);
// how many elements the list at path has;
bool gd_list_size(uint32_t section, const Entry& e, const std::string& path, uint32_t* n, std::string* why);
// eventTime (abilities.timing.*): `path` names the first event of a kind, "<list>[<Type>#0].time", found in the game's own order
// (Kiln's sorted copy is ignored); out holds it first, then every event of that kind at the same time in the game's data, each
// a slot named as kiln.gamedata names its own ("events[<Type>#<k>].time" of the sequence part it lists), and kiln.gamedata keeps
// the list sorted after their writes; false + *why ("none": this entry has no event of the kind, only some have one);
bool gd_event_slots(uint32_t section, const Entry& e, const std::string& path, std::vector<GdValueSlot>* out, std::string* why);
// after every flush of the registry's slots (store_mutex held): the events lists the registry moved re-sorted, the buff mirror
// following the game's records once there are buff clones (kiln.gamedata's after_changes, without its pack bookkeeping).
void gd_section_written();
bool gd_ui_scale(uint8_t format, double v, int64_t* out);  // a config value as its tooltip row shows it
constexpr int32_t kSubUnlock = 101;  // Slot.sub of a recipe's knowledgeRequirement (registry recipes.unlock)

// ============================================================================================= stack guard =====
bool guard_on();
bool guard_set(bool on, std::string* why);
void guard_maybe(const Field& f, uint32_t kind, const uint8_t* orig, const uint8_t* now);

// ============================================================================================= packs ==========
struct ApplyResult {
    uint32_t applied = 0;  // field / list changes
    std::vector<std::string> errors, warnings;
};
// Parses and applies one pack document as owner's layers (seq_base: data packs give their load order, code
// passes 0 = code layers). Drop-table rules are kept and applied when tables appear.
bool apply_pack(KilnOwner owner, uint64_t seq_base, const std::string& text, const std::string& source,
                ApplyResult* out);
// Drop-table rules: (re)apply every rule to one table (hook / reload).
void drops_apply_rules(const Entry& table);
void drops_drop_owner(KilnOwner owner);
// content.cpp's drop-table acquire path (registry_live.cpp; store_mutex held): the old packs' rules when there are any,
// newcontent's stacks, then the registry's layers over the stacks the table has now, whether or not a pack has rules.
void drops_table_loaded(const Entry& table);
bool drops_have_rules();
void drops_clear_rules();  // every owner's drop rules (world reset)
// Data packs on disk.
struct PackFile {
    std::string mod, path, rel, dir;  // dir: the mod's folder
    std::string version;               // kiln.mod.json's (the registry stage names it)
    std::string side;                  // kiln.mod.json's "side" (default "server"): the registry stage skips another side's mods
    uint64_t size = 0, mtime = 0;
};
std::vector<PackFile> discover_packs(std::string* signature);  // kiln.profile's mods when there is one (A6), else kiln/mods
const std::string& current_pack_dir();  // while a pack file applies: its mod's folder
// Keeps the files of the mods in `order` (load order) and sorts them by it; *skipped = mods with packs left out.
std::vector<PackFile> order_packs(const std::vector<PackFile>& files, const std::vector<std::string>& order,
                                  std::vector<std::string>* skipped);
struct PacksReport {
    uint64_t generation = 0;
    std::vector<std::string> skipped;  // mods whose packs did not apply (not loaded)
    std::vector<std::pair<std::string, ApplyResult>> files;  // rel path -> result
    std::vector<Conflict> conflicts;
    // mods set apart in this application: a pack of theirs threw while it applied, what their packs did came off, and their
    // packs from before apply again whole (previous) or none of them do. The report's "setApart" (Kiln.Host's reload reply).
    struct SetApart {
        std::string mod, file, reason;
        bool previous = false;
    };
    std::vector<SetApart> set_apart;
};
// Reverts every pack owner, applies every pack file in order, flushes. Returns the report. A mod's packs apply whole from
// one version: a mod with a file that cannot be read or is no pack keeps the packs of its last application in which every
// file read (when this world had one), whole; else its readable files apply, as at a world load.
PacksReport apply_all_packs(const std::vector<PackFile>& files);
void packs_forget_kept();  // world reset: the next world applies its files from scratch
std::string report_json(const PacksReport& r);
std::string apply_result_json(const ApplyResult& r);
bool dump_all(const std::string& path, std::string* error);
// export_changes (v2): the effective changes of the registries in `kinds` (bit mask of 1 << KILN_CONTENT_*) as a pack
// document with absolute values. *fields = how many field / list changes it holds. Tick thread, store_mutex inside.
std::string export_changes_json(uint32_t kinds, uint32_t* fields);
std::string mods_dir();
std::string data_dir();
// Whether a file or folder exists at `utf8_path` (a UTF-8 path such as data_dir()'s): converted as UTF-8, never read in
// the ANSI code page, which lacks the letters of a Cyrillic or accented Windows user name.
bool path_exists(const std::string& utf8_path);

// The gamedata worker's sections (gamedata.h, gamedata*.cpp): its store slots use kinds >= GD_KIND_BASE.
#if defined(KILN_CONTENT_GAMEDATA) && __has_include("gamedata.h")
#define KC_GAMEDATA 1
#else
#define KC_GAMEDATA 0
#endif

#if KC_GAMEDATA
void gd_item_registered(uint32_t id, const std::string& name, const uint8_t guid[16], uintptr_t addr);  // an item that went in after kiln.gamedata built its list
bool gd_own_lists(uintptr_t item, bool game_base, std::string* err);  // an item clone gets its own copy of every list; of a game item, as the game has it (gamedata_packs.cpp)
bool gd_template_defined(const uint8_t guid[16]);  // a template clone kiln.gamedata defines and has not refused (gamedata_clones.cpp)
uint64_t gd_template_generation();                 // changes whenever a template clone goes into a world's table
#endif

// ============================================================================================= new content =====
// newcontent.cpp: new items and recipes (Forge's DeferredRegister). Definitions come from code (define) or from the
// "add" lists of data packs; they are cloned from a base entry and inserted into the registries of every world that
// runs here (server, and a client's own world), at that world's load, before the save is read. Ids are FNV-1a(GUID).
// docs/kiln/services/content.md "New items and recipes", evidence docs/re/content.md section 7.
void nc_boot();                                   // kiln_plugin_main: symbols, client hooks, world ticks
namespace registry { struct Visibility; }
bool nc_registry_supports(const std::string& registry, bool add, std::string* why);
void nc_registry_visibility(std::shared_ptr<const registry::Visibility> visibility);
bool nc_registry_apply_visibility(std::string* why); // store held, active world's thread
std::string nc_registry_verify_visibility(const std::vector<uintptr_t>& unwritten);
void nc_registry_publish_visibility(); // publish the immutable view only after verified store writes
void nc_registry_record(uint32_t kind, const std::string& id, const std::string& fields_json); // copied effective placeholder/sync values
bool nc_registry_registered(uint32_t kind, const std::string& id, std::string* why);
void nc_world_ready(uintptr_t world, uint32_t kind);  // try_ready: the active world's registries just loaded
void nc_world_stopping(uintptr_t world);          // world_reset, before the store is reset (world intact)
void nc_packs_begin();                            // apply_packs, before apply_all_packs
void nc_packs_end();                              // ... after: pack definitions not seen again are orphaned
// One mod's pack application (apply_all_packs, store_mutex held), so that a pack that throws partway leaves nothing behind:
// nc_pack_mark(owner) before the mod's first pack; nc_pack_rollback(owner) puts every definition the
// owner's packs added or changed since the mark back as it was then (one it added is orphaned, as a removed pack's is:
// its id stays registered until a restart) and returns how many; the journal starts again from there (a rollback, then
// the packs it had before, then a rollback again leaves the mark's state). nc_pack_unmark() ends it. Other owners'
// definitions, and definitions made outside the mark, are never touched.
void nc_pack_mark(KilnOwner owner);
uint32_t nc_pack_rollback(KilnOwner owner);
void nc_pack_unmark();
size_t nc_pack_journal_copies();  // definitions the journal copied since the mark (only at a change of a definition's text)
// A data pack's "add" list (items / recipes): defines every entry as `owner`'s. Called by apply_pack (store_mutex
// held, on the active world's thread).
void nc_pack_add(KilnOwner owner, uint32_t kind, const std::string& list_json, const std::string& source,
                 ApplyResult* out);
KilnResult nc_define(KilnOwner owner, uint32_t kind, const std::string& json, bool pack, const std::string& source,
                     std::string* report);
std::string nc_definitions_json();
bool nc_is_new(uint32_t kind, uint32_t id);
void nc_drops_table(const Entry& table);           // a drop table seen for the first time (store_mutex held)        // a definition's entry (kitbash refs allowed without opt-in)
bool nc_icon_alias(const uint8_t from[16], const uint8_t to[16]);  // false without the UI texture hook
uint32_t nc_undefine_owner(KilnOwner owner);       // every definition of owner set aside (content sync: leave)
uint32_t nc_forget_unbound(KilnOwner owner);       // a boot generation not published: owner's definitions taken out (no world yet)
// A boot generation's definition ("boot:<mod>" owner) that did not register: {mod, name, why}. store_mutex held by the caller.
struct BootFailed {
    std::string mod, key, why;
};
void nc_boot_failed(std::vector<BootFailed>* out);
// Labs only (kiln.json boot.testFault, policy any): every world refuses the definition `guid` at registration with `why`.
bool nc_lab_refuse(const std::string& guid, const std::string& why);
// content.cpp: a required boot mod's definitions that did not register in a world stop the start (kiln.boot activation_fault),
// each mod once. Called after a registration pass, with no lock of kiln.content held.
void boot_late_check();
std::string nc_info_json(const std::string& refs_json);  // v4 info(): languages, placeholders, distribution
// The game's own texts (client; text() / text_info(), since 0.2.0-dev.29.18): newcontent.cpp, next to the loca hooks.
KilnResult nc_text(uint32_t loca_id, std::string* out);  // the game client's main thread (else KILN_ERR_WRONG_THREAD)
void nc_text_info(KilnContentTextInfo* out);            // any thread
void nc_text_boot();                                    // kiln_plugin_main, after nc_boot: reports the row content.text
KilnResult KILN_CALL nc_text_api(uint32_t loca_id, char* buffer, size_t cap, size_t* out_len);  // the table's text()
KilnResult KILN_CALL nc_text_info_api(KilnContentTextInfo* out);                              // the table's text_info()
// kiln.assets (any side; client for icons), looked up lazily: it may register after kiln.content. nullptr = none yet.
const KilnAssetsApi* assets_api();
uintptr_t active_world();  // content.cpp: the world whose registries kiln.content serves (0 = none / not ready)
uint32_t active_kind();

}  // namespace kc
