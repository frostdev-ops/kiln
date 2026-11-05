// SPDX-License-Identifier: MIT
// kiln.content: internal declarations shared by the service's files.
//   game.cpp       guarded memory access, page-protection-aware writes, near allocation, the game's hash maps
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
// Memory within +-1.5 GB of `target` (for self-relative BlobArray offsets into the game's data). Never freed.
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
Registry& reg(uint32_t kind);  // 1..4
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
constexpr uint8_t kSlotBytes = 16;  // the widest slot: a GUID reference
constexpr int32_t kSubBlob = 100;    // Slot.sub of an item list header (kitbash: per-body / pickup models)
struct Layer {
    KilnOwner owner;
    uint64_t seq;
    uint8_t bytes[kSlotBytes];
    std::string source;
};
struct Slot {
    uint32_t kind = 0, id = 0;
    int32_t field = -1;           // index into the registry's schema; -1 = a BlobArray header (recipe inputs/outputs)
    int32_t sub = -1;             // drops: stack index; recipes: KILN_CONTENT_INPUTS/OUTPUTS for headers
    uint8_t n = 0;
    uint8_t original[kSlotBytes] = {};
    uint8_t current[kSlotBytes] = {};
    bool dirty = false;
    std::vector<Layer> layers;
};
struct Conflict {
    std::string what;     // "items Material_T1_Wood_Logs.maxStackSize"
    std::string winner, loser, winner_value, loser_value;
};
std::recursive_mutex& store_mutex();
// Adds or replaces `owner`'s layer of the slot at addr (the slot remembers the original bytes on first touch).
bool store_put(KilnOwner owner, uint64_t seq, uint32_t kind, uint32_t id, int32_t field, int32_t sub, uintptr_t addr,
               uint8_t n, const uint8_t* bytes, const std::string& source);
bool store_drop(KilnOwner owner, uintptr_t addr);  // removes one layer
uint32_t store_drop_owner(KilnOwner owner);         // removes every layer of owner; returns slots affected
bool store_original(uintptr_t addr, uint8_t n, uint8_t* out);  // original bytes if the slot exists
void store_originals(uintptr_t addr, size_t size, uint8_t* out);  // out holds a copy of this memory: what rules changed in it goes back to the original
bool store_has(uintptr_t addr);
// Writes every dirty slot's effective bytes (newest layer, else the original). Returns slots written.
size_t store_flush(std::string* error);
std::vector<Conflict> store_conflicts();
uint32_t store_changed();  // slots whose current bytes differ from the original
void store_for_each(const std::function<void(uintptr_t, const Slot&)>& fn);
std::string owner_label(KilnOwner owner);
void set_owner_label(KilnOwner owner, const std::string& label);
uint64_t next_code_seq();
bool owner_has_layers(KilnOwner owner);
std::vector<KilnOwner> store_owners();
// The world is going away: takes every layer off (revert: writes the originals back first) and forgets every slot.
// Returns the slots that had layers.
uint32_t store_reset(bool revert, std::string* error);

// Value-level API used by the C table and the packs (tick thread; take store_mutex).
KilnResult set_field(KilnOwner owner, uint64_t seq, uint32_t kind, const Entry& e, const Field& f,
                     const KilnContentValue& v, const std::string& source, std::string* why, int32_t sub = -1);
KilnResult set_recipe_stacks(KilnOwner owner, uint64_t seq, const Entry& e, uint32_t which,
                             const std::vector<KilnContentStack>& stacks, const std::string& source, std::string* why);
KilnResult set_drop_stacks(KilnOwner owner, uint64_t seq, const Entry& e, const std::vector<KilnContentStack>& stacks,
                           const std::string& source, std::string* why);
KilnContentValue original_value(const Entry& e, const Field& f);
KilnContentValue current_value(const Entry& e, const Field& f);

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
bool drops_have_rules();
void drops_clear_rules();  // every owner's drop rules (world reset)
// Data packs on disk.
struct PackFile {
    std::string mod, path, rel, dir;  // dir: the mod's folder
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
};
// Reverts every pack owner, applies every pack file in order, flushes. Returns the report.
PacksReport apply_all_packs(const std::vector<PackFile>& files);
std::string report_json(const PacksReport& r);
std::string apply_result_json(const ApplyResult& r);
bool dump_all(const std::string& path, std::string* error);
// export_changes (v2): the effective changes of the registries in `kinds` (bit mask of 1 << KILN_CONTENT_*) as a pack
// document with absolute values. *fields = how many field / list changes it holds. Tick thread, store_mutex inside.
std::string export_changes_json(uint32_t kinds, uint32_t* fields);
std::string mods_dir();
std::string data_dir();

// The gamedata worker's sections (gamedata.h, gamedata*.cpp): its store slots use kinds >= GD_KIND_BASE.
#if defined(KILN_CONTENT_GAMEDATA) && __has_include("gamedata.h")
#define KC_GAMEDATA 1
#else
#define KC_GAMEDATA 0
#endif

#if KC_GAMEDATA
void gd_item_registered(uint32_t id, const std::string& name, const uint8_t guid[16], uintptr_t addr);  // an item that went in after kiln.gamedata built its list
bool gd_own_lists(uintptr_t item, bool game_base, std::string* err);  // an item clone gets its own copy of every list; of a game item, as the game has it (gamedata_packs.cpp)
#endif

// ============================================================================================= new content =====
// newcontent.cpp: new items and recipes (Forge's DeferredRegister). Definitions come from code (define) or from the
// "add" lists of data packs; they are cloned from a base entry and inserted into the registries of every world that
// runs here (server, and a client's own world), at that world's load, before the save is read. Ids are FNV-1a(GUID).
// docs/kiln/services/content.md "New items and recipes", evidence docs/re/content.md section 7.
void nc_boot();                                   // kiln_plugin_main: symbols, client hooks, world ticks
void nc_world_ready(uintptr_t world, uint32_t kind);  // try_ready: the active world's registries just loaded
void nc_world_stopping(uintptr_t world);          // world_reset, before the store is reset (world intact)
void nc_packs_begin();                            // apply_packs, before apply_all_packs
void nc_packs_end();                              // ... after: pack definitions not seen again are orphaned
// A data pack's "add" list (items / recipes): defines every entry as `owner`'s. Called by apply_pack (store_mutex
// held, on the active world's thread).
void nc_pack_add(KilnOwner owner, uint32_t kind, const std::string& list_json, const std::string& source,
                 ApplyResult* out);
KilnResult nc_define(KilnOwner owner, uint32_t kind, const std::string& json, bool pack, const std::string& source,
                     std::string* report);
std::string nc_definitions_json();
bool nc_is_new(uint32_t kind, uint32_t id);
void nc_drops_table(const Entry& table);           // a drop table seen for the first time (store_mutex held)        // a definition's entry (kitbash refs allowed without opt-in)
uint32_t nc_undefine_owner(KilnOwner owner);       // every definition of owner set aside (content sync: leave)
std::string nc_info_json(const std::string& refs_json);  // v4 info(): languages, placeholders, distribution
// kiln.assets (any side; client for icons), looked up lazily: it may register after kiln.content. nullptr = none yet.
const KilnAssetsApi* assets_api();
uintptr_t active_world();  // content.cpp: the world whose registries kiln.content serves (0 = none / not ready)
uint32_t active_kind();

}  // namespace kc
