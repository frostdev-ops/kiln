// SPDX-License-Identifier: MIT
// kiln.content registry stage: the regmap (registry P1 slice S2; plan: registry-p05 P1-PLAN.md section 9.2).
//
// How each Kiln field of a registry maps onto the game: regmap/common/<registry>.json holds one row per field (the game's
// member paths from the entry's root, the conversion between the Kiln value and the game's, when the game reads it, which
// back-end writes it, whether a client needs the same value) and where the entries come from; regmap/<side>/<exe sha256>.json
// is the record of one game build: whether it is verified, each row's state on it, the root types' layout hashes and the
// alias deltas of D3 (renames, removals and clash suffixes, keyed by GUID: identifiers only, never names or content of
// the game's data). These files are Kiln's part: mods never read them and name only the schema's Kiln fields and ids.
//
// The routing rule (the schema's "applies" is the contract): a row's read time must not be later than its field's applies
// (now < worldLoad < restart); a boot back-end serves only restart fields and a live back-end only now and worldLoad ones;
// a row whose read time is not traced is unproven. A field without a verified record for the running build, or whose row
// is missing, changed or unproven, is refused per entry with the reason mapping() gives (BRIEF 6.2).
// Plain data in, plain data out (and load_dir reads files): no game memory, no service table.
#pragma once
#include <cstdint>
#include <map>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace kc::registry::map {

using json = nlohmann::json;

enum class Read : uint8_t { Now, WorldLoad, Restart, Unknown };
enum class Backend : uint8_t { Live, Boot, None };
enum class RowState : uint8_t { Mapped, Unproven, Unmapped };
enum class EntryKind : uint8_t { Resource, Array, Single };
// Unproven (kilnctl regproof): nothing is wrong that the proof can see, but the lab gave no evidence for the row on this build
// (the record's "reasons" says what is missing); kept off like Changed, with that reason instead of "a Kiln update is needed".
enum class BuildRow : uint8_t { Ok, Changed, Unmapped, Unproven };
enum class Mapping : uint8_t { Mapped = 0, Unmapped = 1, Unproven = 2, Removed = 3 };  // KILN_REGISTRY_MAPPED ... REMOVED

// The conversions between a Kiln value and the game's (regmap/README.md).
inline constexpr const char* kConvs[] = {"int",     "float",   "bool",    "time",         "enum",          "flags",
                                         "ref",     "refList", "formula", "impactConfig", "list",          "stackCount",
                                         "inventoryWalk", "unlock", "optionalEnum", "component", "eventTime"};

struct Row {
    std::string field;               // the Kiln field ("maxStack", "inputs[].count")
    std::vector<std::string> paths;  // the game's member paths from the entry's root (or the list element's)
    std::string conv;
    std::map<std::string, std::string> values;  // enum / flags: Kiln name -> the game's
    std::string formula, config, reason, evidence;
    Read read = Read::Unknown;
    Backend backend = Backend::None;
    bool client_needs = false;
    RowState state = RowState::Mapped;
};

struct EntrySource {
    EntryKind kind = EntryKind::Resource;
    std::string resource_type;  // the game's root type of the resource
    std::string array;          // kind Array: the member holding the entries
    std::string resource_guid;  // kind Single: the one resource
    std::string name, guid, id; // members: the debug name, the entry's GUID (empty: the resource's), the 32-bit id
    std::string unnamed;        // the stable name of an entry without one: printf over the id ("item_%08x") or the GUID text ("drops_%.8s")
    std::string name_from;      // drop tables: "<component type>.<member>" whose templates name the entry
    std::string key;            // loot: "items": the entry has the id of the items entry with the same 32-bit id; creatures:
                                // "templates": the id of the template with the same GUID
    std::string key_member;     // stations: with key "items", the member whose 32-bit item id names the entry (keyMember;
                                // empty: the entry's own id, as loot's)
    // creatures: the entries are the resources holding a component of one of these types (template roots), in order
    std::vector<std::string> with_component;
};

struct RegistryMap {
    std::string registry;
    EntrySource entries;
    std::map<std::string, Row> rows;
};

struct Aliases {
    std::map<std::string, std::string> pinned;   // "<registry>|<guid>" -> Kiln id (renames and clash suffixes)
    std::map<std::string, std::string> removed;  // "<registry>|<Kiln id>" -> the game build that removed it
    bool is_removed(const std::string& registry, const std::string& id, std::string* build) const;
};

struct BuildRecord {
    std::string side, exe_sha256, game_build, steam_build, proof;
    // the regmap/common the record was proven on (kilnctl regproof: sha256 over "<file name>\n<sha256 of its bytes>\n" per
    // common file in name order); a record whose digest is not the deployed common's enables no row (review2 S1)
    std::string regmap_digest;
    bool verified = false;
    // written by kilnctl regproof --mark into this server's own Kiln folder ("markedLocally"), not shipped by Kiln: when
    // regmap/common changes under it, the fix is a new proof on this server, not a Kiln update (review3 S4)
    bool marked_locally = false;
    std::map<std::string, BuildRow> rows;      // "<registry>.<field>"
    std::map<std::string, std::string> reasons; // "<registry>.<field>" -> why an unproven row is not proven (Kiln words)
    std::map<std::string, uint32_t> layouts;   // root type -> the reflection's layout hash on this build
    Aliases aliases;
};

struct RegMap {
    std::map<std::string, RegistryMap> registries;
    BuildRecord build;
    bool has_build = false;
    std::string side, exe_sha256;  // the running game's (load_dir), named when there is no record for it
    std::string common_digest;     // the deployed regmap/common's digest (load_dir), as the record's regmapDigest is made
    // files in regmap/common that are not a registry's own <registry>.json (a copy left beside one, a file that does not
    // read as a regmap): they change the digest, so the record enables nothing; the fix is removing them or a repair
    std::vector<std::string> stray_common;
    // the record of the running exe is verified AND was proven on the deployed regmap/common: only then can a row apply
    bool verified() const { return has_build && build.verified && build.regmap_digest == common_digest; }
    // The state of a field on this build and, unless Mapped, the reason an edit of it is refused (BRIEF 6.2: it names the field
// and the game build and ends "; this edit was not applied"). Without a verified record for the running exe every field is
// Unproven: Kiln never writes a field it has not proven on that exact build.
    Mapping mapping(const std::string& registry, const std::string& field, std::string* reason) const;
};

// One regmap/common/<registry>.json. Rows that are malformed are left out and named in *errs.
bool load_common(const json& doc, RegistryMap* out, std::vector<std::string>* errs);
// One regmap/<side>/<exe sha256>.json.
bool load_record(const json& doc, BuildRecord* out, std::vector<std::string>* errs);
// <dir>/common/*.json and <dir>/<side>/<exe sha256>.json (has_build false when there is no record for that exe). A row that
// breaks the routing rule (check_routing) is loaded as unproven, its reason the rule it breaks, and named in *errs.
bool load_dir(const std::string& dir, const std::string& side, const std::string& exe_sha256, RegMap* out, std::vector<std::string>* errs);

// The routing rule against the generated schema (schema.g.h): one line per row that breaks it, or names a field the schema
// does not have.
std::vector<std::string> check_routing(const RegistryMap& m);

// ---- ids (D3) ----
struct GameEntry {
    std::string guid;                     // its GUID as text (lower case)
    std::string name;                     // its debug name ("" when it has none)
    uint32_t id = 0;                      // its 32-bit id
    std::vector<std::string> named_by;    // drop tables: the stable names of the templates that use it
    uint32_t key = 0;                     // the 32-bit item id a "key": "items" source names it by (keyMember; else id)
};
// The Kiln ids of a registry's game entries, given in the archive's order: a pinned id first; else game:<stable name>,
// where the stable name is the debug name in lower case (an unnamed entry: the source's "unnamed" pattern; a drop table:
// the first of its templates' names in ordinal order). The entry that holds a name first keeps it; a later one with the
// same name (also in another case) gets "~<first 8 hex digits of its GUID>", more digits while that is taken.
// by_item_id: the items registry's ids by 32-bit item id (loot: the entry's own id; stations: its keyMember, the item that
// builds it; an entry whose item no items entry has, or none, falls back to the name rules); by_key_guid: the keyed registry's
// ids by GUID (lower case; creatures take their template's id). A registry whose source names a "key" takes the keyed id by
// GUID first, else by 32-bit id.
// A name outside the id grammar (spaces, apostrophes, non-ASCII bytes) has each such character as '_' and is named in
// *notes (the record pins it); so is a second loot entry for one item. Removed ids (aliases.removed) are never given again.
std::vector<std::string> assign_ids(const std::string& registry, const EntrySource& src, const Aliases& aliases,
                                    const std::vector<GameEntry>& entries, const std::map<uint32_t, std::string>* by_item_id,
                                    std::vector<std::string>* notes = nullptr, const std::map<std::string, std::string>* by_key_guid = nullptr);

// The one id grammar (tools/registrygen KILN_ID, C# RegistryId, the data section's schema): <namespace>:<name>, namespace
// [a-z0-9][a-z0-9_.-]*, name [a-z0-9_.-]+, then optionally a clash suffix "~" and 8 to 32 lower-case hex digits.
bool is_kiln_id(const std::string& id);

}  // namespace kc::registry::map
