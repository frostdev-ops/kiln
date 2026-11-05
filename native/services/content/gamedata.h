// SPDX-License-Identifier: MIT
// kiln.content, gamedata part: the game's ability / creature data as data-pack sections and the kiln.gamedata table.
//
//   gamedata_types.cpp  the game's reflection as a type graph (struct bases, blob arrays, variants, enums)
//   gamedata_walk.cpp   paths over live game memory: resolve, enumerate, read, parse values, config aliases
//   gamedata_res.cpp    the resource service (key -> data) and the section registries (templates, sequences, ...)
//   gamedata_packs.cpp  pack sections, item config paths, layers in kiln.content's store, tooltips, event order,
//                       config adds, export (content sync), external sections (other services' pack sections)
//   gamedata_api.cpp    boot, world hooks, the kiln.gamedata C table, dump
//
// Every change is a layer in kiln.content's store (store_put / store_drop_owner / store_flush), so revert on unload,
// pack reload, owner probing and world reset work as for items. Store kinds: GD_KIND_BASE + section.
// Docs: docs/kiln/services/gamedata.md (modder guide), docs/re/gamedata.md (evidence).
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include "content.h"
#include "nlohmann/json.hpp"
#include "services/kiln_gamedata.h"

namespace kc {

using gjson = nlohmann::ordered_json;

constexpr uint32_t GD_KIND_BASE = 16;  // store slot kinds >= this are gamedata's (kind = GD_KIND_BASE + section)

// ---- hooks called by kiln.content (content.cpp, packs.cpp, store.cpp) ------------------------------------------
void gd_boot();
void gd_world_ready(uintptr_t world, uint32_t kind);
void gd_world_reset();
void gd_tick(uint64_t step);
void gd_packs_begin();
void gd_packs_end();
void gd_owner_gone(KilnOwner owner);
void gd_pack_sections(KilnOwner owner, uint64_t seq_base, const gjson& doc, const std::string& source, ApplyResult* out);
// The "add" lists of the sections, before kiln.content's own sections (items may point at clones of the same pack).
void gd_pack_adds(KilnOwner owner, uint64_t seq_base, const gjson& doc, const std::string& source, ApplyResult* out);
bool gd_is_section(const std::string& k);
int gd_item_path(KilnOwner owner, uint64_t seq, const Entry& e, const std::string& path, const gjson& val,
                 const std::string& source, std::string* err);
uint32_t gd_export(gjson& doc, uint32_t kinds);
std::string gd_slot_what(const Slot& s, std::string* field_out);
std::string gd_layer_value(const Slot& s, const uint8_t* bytes);

namespace gd {

// ============================================================================================= types =============
enum : uint32_t {
    K_BOOL = 1, K_U8, K_S8, K_U16, K_S16, K_U32, K_S32, K_U64, K_S64, K_F32, K_F64,
    K_ENUM = 0x0C, K_BM8 = 0x0D, K_BM64 = 0x10, K_TYPEDEF = 0x11, K_STRUCT = 0x12, K_ARRAY = 0x13,
    K_BLOBARRAY = 0x18, K_BLOBSTRING = 0x19, K_BLOBOPT = 0x1A, K_BLOBVAR = 0x1B, K_REF = 0x1C, K_GUID = 0x1D,
};

struct TType;
struct TField {
    std::string name;
    uintptr_t type_rec = 0;
    uint32_t offset = 0;
    const TType* type() const;
};
struct TType {
    uintptr_t rec = 0;
    std::string name;   // qualified ("keen::ecs::LifeTime")
    std::string brief;  // without namespaces ("LifeTime")
    uint32_t hash = 0, size = 0, count = 0, kind = 0;
    uintptr_t inner_rec = 0;
    std::vector<TField> fields;     // structs: the base chain's fields first
    std::shared_ptr<EnumInfo> en;   // enums; bitmasks: their flag enum
    const TType* inner() const;     // typedef target, array / blob element, enum underlying, struct base
    const TType* resolved() const;  // through typedefs
    const TField* field(const std::string& n) const;  // exact, then ignoring case
};
const TType* type_at(uintptr_t rec);
const TType* type_hash(uint32_t hash);
const TType* type_named(const std::string& qname);

// ============================================================================================= walking ===========
struct Node {
    const TType* t = nullptr;  // resolved (never a typedef)
    uintptr_t addr = 0;
};
// A leaf the store can hold (<= 16 bytes): bool, integers, floats, enums, flag sets, keen::Time, single-scalar
// structs (keen::HashKey32), GUIDs and references. false: not a leaf.
bool leaf_of(const TType* t, Field* f);
// Reads a BlobArray / BlobVariant / BlobOptional / BlobString at addr.
bool blob_array(uintptr_t addr, uintptr_t* data, uint32_t* count);
bool blob_variant(uintptr_t addr, uint32_t* hash, uintptr_t* data, uint32_t* size);
std::string blob_string(uintptr_t addr);

enum class Root : uint8_t { Plain, Template, };
struct Resolved {
    Node node;                    // the leaf (or a struct node)
    std::string path;             // canonical path ("ImpactConfiguration.configValues.0xa19409b2")
    uintptr_t uivalues_owner = 0; // the struct that holds both the configurations and a uiValues array (tooltips)
    const TType* uivalues_type = nullptr;
    uintptr_t config = 0;         // the config entry (keen::impact::*Config) when the path named one
    const TType* config_type = nullptr;
    // config absent (an "add"): the Configurations node, the config id; node.t == nullptr
    uintptr_t add_configs = 0;
    uint32_t add_id = 0;
    std::string add_rest;         // the path below the config ("scaleFactor"), empty = its value
};
// Resolves a path below `root` (a struct node). `[*]` fans out (several results). false + *err: no such path.
// *first_missing: the path's first segment did not exist at all (items: not ours).
bool identity_field(const std::string& name);  // ids, GUIDs of things: read-only
bool resolve(Root kind, Node root, const std::string& path, std::vector<Resolved>* out, std::string* err,
             bool* first_missing = nullptr);
// Every leaf below a node, as (path, node) pairs, depth- and count-limited; `prefix` is prepended.
void enumerate(Root kind, Node root, const std::string& prefix, size_t limit,
               const std::function<void(const std::string&, const Node&, const Field&)>& fn);
// A node as JSON (structs as objects, arrays as lists, variants as {"$type", ...}); depth-limited.
gjson node_json(const Node& n, int depth = 8);
gjson leaf_json(const Field& f, const uint8_t* bytes, bool raw = false);  // raw: enums/flags as numbers (export)

// ---- config vocabulary (aliases) --------------------------------------------------------------------------------
std::vector<uint32_t> config_ids(const std::string& alias);  // alias / 0x id -> ids (several: ambiguous)
std::string config_name(uint32_t id);                         // the best alias, else "0x%08x"
gjson configs_json();
void configs_build();                                         // program variables + the C++-read table (once)
// An existing entry with this config id somewhere in the game data (for adds): variant hash, size, bytes.
struct Exemplar {
    uint32_t hash = 0, size = 0;
    bool scaled = false;  // found in a `scaled` array
    std::vector<uint8_t> bytes;
};
bool config_exemplar(uint32_t id, Exemplar* out, std::string* err);
void configs_reset_exemplars();
// Walks every config list of the game data once per world (the exemplar scan); also notes the GUIDs of buffs that
// the game data references (BuffType configs, ApplyBuffEvent.buffTypeOverride, BuffType.onTimeout.followUp).
void configs_scan();

// ---- values -----------------------------------------------------------------------------------------------------
// Parses a JSON value for a leaf (numbers, bools, enum / flag names, GUID text, names of templates / sequences for
// references, {"mul","add","min","max","raiseOnly","lowerOnly","set"} ops on the original). *reset: {"reset": true}.
bool parse_leaf(const Field& f, const gjson& v, const uint8_t* original, uint8_t* out, bool* reset, std::string* err);

// ============================================================================================= resources ========
bool res_ready();
const char* res_via();
uintptr_t res_data(const uint8_t guid[16], uint32_t type, uint64_t* size = nullptr);
bool res_list(uint32_t type, std::vector<std::array<uint8_t, 16>>* guids);

// ============================================================================================= registries =======
struct GEntry {
    uint32_t id = 0;
    std::string name;
    uintptr_t addr = 0;
    uint8_t guid[16] = {};
    int32_t sub = -1;       // sequences: sub-sequence index
    bool clone = false;
};
struct GRegistry {
    uint32_t section = 0;
    const char* label = "";
    const char* qtype = "";  // the root struct
    const TType* type = nullptr;
    Root root = Root::Plain;
    bool available = false;
    std::vector<GEntry> entries;
    std::unordered_map<uint32_t, uint32_t> by_id;
    std::unordered_map<std::string, uint32_t> by_name, by_lower;
    void clear();
    void add(GEntry e);
    const GEntry* get(uint32_t id) const;
    const GEntry* lookup(const std::string& key) const;          // name, ci name, 0x id, GUID ("guid#k" for subs)
    std::vector<const GEntry*> select(const std::string& sel) const;  // + globs; a GUID selects every sub of it
};
GRegistry& greg(uint32_t section);  // KILN_GD_TEMPLATES .. KILN_GD_ITEMS
bool valid_section(uint32_t section);
const char* section_name(uint32_t section);
uint32_t section_of(const std::string& name);  // 0 = not a gamedata section
bool build_sections(uintptr_t world, std::string* why);
// items come from kiln.content's item registry (refreshed at world ready)
void refresh_items();
std::string entry_key(uint32_t section, const GEntry& e);  // export key: GUID ("guid#k"), else "0x%08x"
Node root_of(uint32_t section, const GEntry& e);
// Arsenal entries by template ("template:<name>"), skill nodes by unlocked item ("item:<name>").
const GEntry* lookup_alias(uint32_t section, const std::string& key);

// ============================================================================================= changes ==========
// Applies one value (any JSON for a path) as owner's layer; fans out over [*] and struct objects. Returns the number
// of leaves changed; *err collects problems.
void forget_added(KilnOwner owner, uintptr_t header);  // a config list goes back to what the game has: this owner's added elements are not kept for it
uint32_t apply_value(KilnOwner owner, uint64_t seq, uint32_t section, const GEntry& e, const std::string& path,
                     const gjson& value, const std::string& source, std::vector<std::string>* errs);
bool where_ok(uint32_t section, const GEntry& e, const gjson& where, std::string* err);
void gd_pack_add(KilnOwner owner, uint32_t section, const gjson& list, const std::string& source, ApplyResult* out);
void ext_world_reset();
void changes_world_reset();
void changes_tick();
void changes_boot();
bool reset_path(KilnOwner owner, uint32_t section, const GEntry& e, const std::string& path);
void after_changes();  // flush, re-sort touched sequences, flush again (store_mutex held)
uint32_t changed_entries(uint32_t section, std::vector<uint32_t>* ids);
bool entry_modified(uint32_t section, uint32_t id);
std::string slot_path(int32_t field);
KilnOwner internal_owner();

// external sections (other services' pack sections, e.g. "creatures")
struct External {
    std::string name;
    KilnOwner owner = 0;
    KilnGdSectionFn apply = nullptr;
    KilnGdRevertFn revert = nullptr;
    void* user = nullptr;
};
KilnResult register_external(const External& x);
KilnResult unregister_external(const std::string& name);
std::string externals_json();
std::string waiting_json();  // sections kept for services that have not registered yet

// clones (gamedata_clones.cpp): "add" lists / define(). Templates, sequences, perks, buffs.
//  - perks: a copy of the base Perk resource served by kiln.assets (NEW) + one insert into the world's PerkRegistry
//    id map (FNV-1a(GUID) -> Perk*), the map PerkRegistryFind reads;
//  - buffs: the game finds a BuffType through BuffRegistry+0x20 (the collection: a static hash map id -> index and
//    the buffs array, getBuffType server 0x933700), with no runtime map. Kiln serves the collection from its own
//    copy: the game's records mirrored (kept in step with the game's records, where every layer stays: buffs_sync),
//    then the clones, and a rebuilt map; the registries' pointer is swapped to it.
void clones_boot();
void clones_world_ready(uintptr_t world, uint32_t kind);
void clones_world_reset();
void clones_tick(uint64_t step);
KilnResult clones_define(KilnOwner owner, uint32_t section, const std::string& json, bool pack, const std::string& source,
                         std::string* report);
std::string clones_json();
gjson clones_status();  // {"buffCopy": {...}, "perkMaps": n, "texts": bool} for status_json
gjson clones_export(uint32_t section);  // registered definitions (the content sync's "add" list)
void clones_packs_begin();
void clones_packs_end();
// The buff mirror follows the game's buff records (their layers); store_mutex held. Cheap when nothing changed.
void buffs_sync();
// A buff's GUID (buffTypeId = FNV-1a of it): a clone's, or one the game data references. false: not known.
bool buff_guid(uint32_t id, uint8_t out[16]);
void buff_guid_note(const uint8_t g[16]);  // a BuffTypeReference seen in the game data (configs_scan)

// ---- status / misc ----------------------------------------------------------------------------------------------
std::string status_json();
bool dump_section(uint32_t section, const std::string& key, const std::string& path, std::string* written,
                  std::string* err);
uint64_t generation();

}  // namespace gd
}  // namespace kc
