// SPDX-License-Identifier: MIT
// kiln.content registry stage: the model (registry P1 slice S5; P1-PLAN.md sections 4.2, 6, 9.2, 9.3 and 9.4).
//
// The base of a start's registry stage as Kiln ids and Kiln fields, read once from a Source (the base view's backings):
//   live      kiln.assets' bytes at resources-ready (boot_archive_view.h LiveView: it keeps a hash of every byte string it
//             served, for the digest), decoded through the game's reflection (gd::resolve, gd::node_json);
//   tree      a view of decoded values (the boot snapshot, or the committed generation in a kiln-backend start).
// Both give each member as the same JSON (leaf_json / node_json's forms: numbers, true/false, the game's enum names, GUID
// text with "none" for none, seconds for keen::Time), so one decoder turns them into Kiln values through the regmap's rows
// (regmap/README.md: int .. inventoryWalk; formulas through formulas.h with the balance registry's gear constants).
//
// Lazy: a registry's entries (name, GUID, 32-bit id, place) are listed the first time something needs that registry (an
// edit, a reference into it, the id table), and a field is decoded the first time it is asked for. The ids are D3's
// (regmap.h assign_ids over the entries in the archive's order). What the stage needs is decoded before compose (needs_of);
// then release() lets the source go, so the stage does not hold a second copy of the game's data for the whole start.
// Plain data in and out: the game is reached only through the Source; no lock (one stage thread).
#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "../../../core/src/services/boot_archive_view.h"
#include "compose.h"
#include "regmap.h"

namespace kc::registry::model {

// Bumped whenever the model or its reading of the regmap could give other Kiln values for the same bytes: part of every
// cache key next to the base digest (P1-PLAN section 6: the digest names the data, not the decoder).
inline constexpr const char* kDecoderVersion = "kiln-model 1";

// Where an entry's data is: a resource, or one element of a resource's array member.
struct Place {
    KilnAssetKey key{};
    std::string type;      // the resource's root type ("keen::ItemInfo")
    std::string array;     // the member holding the entries (kind array), else empty
    int32_t element = -1;  // the element (kind array)
};

class Source {
   public:
    virtual ~Source() = default;
    virtual const char* name() const = 0;  // "live", "snapshot", "committed", a test's
    // Every resource of a type, in the archive's order (the game's, then new ones).
    virtual bool list(const std::string& type, std::vector<KilnAssetKey>* out, std::string* why) = 0;
    // How many elements the array member of a place's resource holds.
    virtual bool count(const Place& p, size_t* n, std::string* why) = 0;
    // The member at path below the place's root (the resource, or its element), as JSON. false + *why: no such member here.
    virtual bool member(const Place& p, const std::string& path, json* out, std::string* why) = 0;
    // The base view's digest of what was read (empty: none).
    virtual std::string digest() = 0;
    // Every resource read so far (with its type), and a read of one without decoding it (the cache's check: the digest
    // then covers the same bytes the cached base was decoded from).
    virtual std::vector<std::pair<KilnAssetKey, std::string>> keys_read() const = 0;
    virtual bool touch(const KilnAssetKey& key, const std::string& type, std::string* why) = 0;
    // The reads are over: what the source holds for them is let go (later reads fail).
    virtual void release() = 0;
};

// kiln.assets' bytes through a base view that serves bytes (LiveView), decoded through the game's reflection. list(type
// hash) lists a type's keys (kiln.assets resource_list).
using Lister = std::function<bool(uint32_t type_hash, std::vector<KilnAssetKey>* out)>;
std::unique_ptr<Source> live_source(kiln::boot_view::BaseView& view, Lister list);
// A base view that serves decoded trees (kiln.boot's query "resource:<guid>/<type hash>/<part>"); list_tree lists a type.
using TreeLister = std::function<bool(const std::string& type, std::vector<KilnAssetKey>* out, std::string* why)>;
std::unique_ptr<Source> tree_source(kiln::boot_view::BaseView& view, TreeLister list);

struct Entry {
    std::string id;    // the Kiln id
    std::string guid;  // GUID text, lower case ("" when it has none)
    std::string name;  // the debug name as the game has it
    uint32_t num = 0;  // the game's 32-bit id
    Place place;
    std::vector<std::string> named_by;  // drop tables: the stable names of the templates that use it
};

// What the stage needs decoded before compose.
struct Need {
    bool all = false;                                          // every field of every registry (a stage callback may read any)
    std::map<std::string, std::set<std::string>> fields;       // registry -> Kiln fields
    std::map<std::string, std::set<std::string>> whole;        // registry -> entries whose every field is needed ("like")
    std::map<std::string, std::set<std::string>> raw;          // registry -> raw member keys (the Advanced tier)
    std::set<std::string> traits;                              // registries whose traits a selector names
    std::set<std::string> registries;                          // registries named at all
};
// What these batches name: their fields (and an element's list), where keys, like entries, raw keys, traits; and, when an
// entry is removed or added (a reference to it can go bad), every reference field of the schema (compose's reference check).
Need needs_of(const Schema& schema, const std::vector<ModInput>& mods);

class Model {
   public:
    Model(Source& src, const map::RegMap& rm, const Schema& schema);
    ~Model();
    Model(const Model&) = delete;
    Model& operator=(const Model&) = delete;

    // A registry's game entries, in the archive's order (the registries it depends on are listed first). null + *why.
    const std::vector<Entry>* entries(const std::string& registry, std::string* why);
    const Entry* find(const std::string& registry, const std::string& kiln_id);
    // One Kiln field of entry #i, decoded once. nullopt + *why.
    std::optional<Value> value(const std::string& registry, size_t i, const std::string& field, std::string* why);
    // The Advanced tier: the member at raw key of entry #i (resolved once), typed by its value.
    std::optional<RawMember> raw(const std::string& registry, size_t i, const std::string& key, std::string* why);

    // The base for compose: every registry need names (with its removed-by-ledger ids), the fields need names decoded, the
    // raw members need names resolved (BaseModel::raw answers from them after release()), raw_cover from the regmap rows.
    void fill(const Need& need, BaseModel* out);

    // The id table of every registry listed so far: "<registry>|<kiln id>" -> GUID text / 32-bit id (lane D's live back-end).
    void id_table(std::map<std::string, std::string>* ids, std::map<std::string, uint32_t>* nums) const;
    // Lists every registry the per-build id table holds: the schema's and the reference tables R1's references name
    // (templates, perks, input categories), whatever was decoded so far. Entries only: no field is decoded.
    void list_all();
    // The same as kiln.content writes it at start (never into the repository): {"format":1, "side", "exeSha256", "decoder",
    // "registries": {"<registry>": {"<guid or #<num>>": "<kiln id>"}}}.
    json id_table_json() const;

    const map::RegMap& regmap() const;
    const Schema& schema() const;
    std::string digest() { return src_.digest(); }
    std::vector<std::pair<KilnAssetKey, std::string>> keys_read() const { return src_.keys_read(); }
    const char* source_name() const { return src_.name(); }
    std::set<std::string> decoded() const;  // "<registry>.<field>" decoded so far
    // Notes for kiln.log, each once: names outside the id grammar, references that name no entry, rows that do not decode.
    std::vector<std::string> take_notes();
    // The entries behind fill's per-field notes ("<registry>.<field> has no value in N of M entries"), for kiln.log at debug
    // level: one reason per entry it read the field of without a value. A reason of the field itself (no regmap row), the
    // same for every entry, is the note's alone.
    std::vector<std::string> take_details();
    // The reads are over: the source lets its bytes go; decoded values stay.
    void release();
    // The balance registry's gear constants (formulas.h constants_of), from the base.
    std::map<std::string, double> balance_constants();

   private:
    struct Impl;
    std::unique_ptr<Impl> d_;
    Source& src_;
};

// The GUID of an entry a mod adds (P1-PLAN 9.2): UUIDv5 of Kiln.Blocks' namespace 6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77 and
// "registry:<registry>:<kiln id>", as GUID text. Fixed forever (saves keep it).
std::string added_guid(const std::string& registry, const std::string& kiln_id);

// ---- the boot back-end's writes (balance: restart fields) ----
// A Kiln value of field f (its regmap row) as the game's member writes: (member path from the entry's root, JSON value for
// gd::parse_leaf), only where it differs from below (the value under the registry's rows). false + *why: it cannot be
// written there (a list whose length would change, a conversion the boot back-end does not write).
bool encode(const map::RegistryMap& reg, const map::Row& row, const FieldSpec& f, const Value& v, const Value& below,
            std::vector<std::pair<std::string, json>>* writes, std::string* why);
// One plain Kiln value (spec: its field, or a list's element field) as the JSON gd::parse_leaf takes for the row's member:
// enum names through the row's values, numbers, bools, a time's seconds, a position's metres as the game's 32.32 fixed point.
// false + *why: not a value the boot back-end writes (another conversion, not a number).
bool encode_leaf(const FieldSpec& spec, const map::Row& row, const Value& v, json* out, std::string* why);
// The values of a "hooks" registry (regmap entries kind hooks): the game's own value of each Kiln hook by the name a row's path
// gives ("altarCap"), as the model reads a member (limits.cpp sets it in kiln-content.dll from the game's code; host tests set
// their own). Unset: such a registry's fields have no value ("Kiln's hooks are not here").
using HookValueFn = std::function<bool(const std::string& name, json* out, std::string* why)>;
void set_hook_values(HookValueFn fn);
// The place of a hooks registry's one entry: no resource (its key is all zeros), this type name.
inline constexpr const char* kHooksType = "kiln:hooks";
// A position (conv "position"): the game keeps a world position's axis as a signed 64-bit 32.32 fixed-point number of metres.
constexpr double kPositionUnit = 4294967296.0;  // 2^32: one metre
// The decoder alone, over a member getter (paths from the root the row's paths start at): what the boot back-end reads back
// from the copy after the old documents. refs: a reference's Kiln id from (registry, GUID text, 32-bit id); may be empty.
using MemberFn = std::function<bool(const std::string& path, json* out, std::string* why)>;
// A member of a resource's bytes (data, len) of the root type named type, through the game's reflection, as the live source
// gives it: the boot back-end reads a copy back with it.
bool bytes_member(const void* data, size_t len, const std::string& type, const std::string& path, json* out, std::string* why);
using RefFn = std::function<std::optional<std::string>(const std::string& registry, const std::string& guid, uint32_t num)>;
std::optional<Value> decode(const map::RegistryMap& reg, const map::Row& row, const FieldSpec& f, const MemberFn& get, const RefFn& refs,
                            const std::map<std::string, double>& balance, std::string* why);

}  // namespace kc::registry::model
