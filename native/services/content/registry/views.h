// SPDX-License-Identifier: MIT
// kiln.content registry views (dev.32 J1, BRIEF R4 / P5): one typed view over every registry and every other resource type
// the game has, under Kiln names.
//
// A view is a set of entries (Kiln ids) with Kiln fields. Two kinds:
//  - stable:   a registry (schema/registry/<registry>.json, generated into schema.g.h): its schema fields, written and proven
//              as the registry is (kiln.registry, kiln.registry.live). The view only reads through it.
//  - advanced: any other resource type of the game, derived from the game's reflection on this build: the view's name from
//              the type's name ("keen::NpcDialogResource" -> "npcDialog"), each field's name from its member's ("rewardItems"
//              -> "rewardItems"), lower camel case. The names start as the game's on the first build Kiln maps and are Kiln's
//              from then on: regmap/views.json (the alias table, Kiln's part like the rest of regmap/) keeps a view's or a
//              field's Kiln name when a game update renames the type or the member, and marks a member a build removed. Read
//              only in this release; "verified per game build" (D5 Advanced): a field is there when its member resolves on the
//              running build's reflection, and a field that does not (no such member, a kind the view cannot read) is refused
//              per entry with a reason in Kiln words.
// The game's type and member names never leave this layer: the public surface (kiln.registry.view, Kiln.Registry's views)
// names views, ids and fields only. A field a registry row maps one to one names that row (coveredBy), so a mod can move to
// the stable tier.
// Plain data in and out: the reflection comes in as TypeDesc (rbytes::describe over the game's types, or a test's), values as
// the JSON rbytes::member gives. No game memory, no service table, no lock.
#pragma once
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "ir.h"
#include "nlohmann/json.hpp"
#include "regmap.h"

namespace kc::registry::views {

using json = nlohmann::json;

// ---- the game's reflection, as plain data ----

enum class TypeKind : uint8_t { Bool, Int, Float, Enum, Flags, Time, Guid, Ref, Text, Struct, List, Variant, Optional, Configs, Other };

// One type, depth-limited (a struct that reaches itself through a list stops at the limit as Other). Leaves are what gd::leaf_of
// calls leaves: a struct of one scalar (keen::HashKey32, ids) is that scalar.
struct TypeDesc {
    std::string name;  // qualified ("keen::QuestResource"): Kiln's part
    TypeKind kind = TypeKind::Other;
    uint32_t hash = 0;
    std::vector<std::pair<std::string, std::shared_ptr<const TypeDesc>>> fields;  // Struct: members, base chain first
    std::shared_ptr<const TypeDesc> element;                                      // List: the element
    uint32_t fixed = 0;                     // List: a static array's length (0: a blob array)
    std::vector<std::string> values;        // Enum, Flags: the game's value names, in order
    std::string target;                     // Ref: the referenced type when the reflection names it ("keen::ItemInfo")
};

// ---- the alias table: regmap/views.json ----

// {"views": 1,
//  "types":   {"<kiln view>": "<qualified type>"},                    a view whose type the game renamed keeps its name
//  "fields":  {"<kiln view>": {"<kiln field>": "<member path>"}},     a field whose member the game renamed or moved
//  "removed": {"<kiln view>": {"<kiln field>": "<game build>"}},      a member a game build removed: refused, never reused
//  "ids":     {"pinned": {"<view>|<guid>": "<kiln id>"}, "removed": {"<view>|<kiln id>": "<game build>"}}}
// Deltas only (identifiers, never a list of the game's names or values; ruling 2026-11-20): an empty table is a valid one, and
// every name without a row is the one derive() gives.
struct Aliases {
    std::map<std::string, std::string> types;
    std::map<std::string, std::map<std::string, std::string>> fields, removed;
    map::Aliases ids;
};
bool load_aliases(const json& doc, Aliases* out, std::vector<std::string>* errs);

// ---- names ----

// A Kiln name segment from a game name: lower camel case over [A-Za-z0-9] (other characters break words), a leading "m_" left
// out, a leading digit after "n" ("2d" -> "n2d"); "" for a name with no letter or digit. Matches registrygen's NAME rule.
std::string kiln_name(const std::string& game);
// A type's view name: its last namespace part without a "Resource" suffix, as kiln_name ("keen::NpcDialogResource" ->
// "npcDialog", "keen::ecs::TemplateResource" -> "template"). The namespaced form ("ecsTemplate") is what a clash falls back to.
std::string view_name(const std::string& qualified, bool namespaced = false);

// ---- views ----

enum class FieldKind : uint8_t { Int, Float, Bool, Enum, Flags, Time, Id, Text, List, Struct, Unread };
const char* kind_name(FieldKind k);

struct ViewField {
    std::string path;      // Kiln path ("rewardItems[].count"); a list's elements are "<list>[]" rows of their own
    std::string game;      // the member path ("rewardItems[].count"): Kiln's part, never shown
    FieldKind kind = FieldKind::Unread;
    FieldKind element = FieldKind::Unread;  // List: its elements' kind (Struct: their fields are rows "<list>[].<leaf>")
    std::vector<std::string> values;        // Enum, Flags: Kiln names, in the game's order
    std::vector<std::string> game_values;   // ... and the game's names they stand for (Kiln's part)
    std::string ref;       // Id: the view its ids name (catalog(); "" when no view has the target type)
    std::string ref_type;  // Id: the referenced type, when the reflection says (Kiln's part)
    bool read_only = true; // advanced fields: always (this release); stable fields: the schema's
    bool removed = false;  // the alias table says a game build removed its member: refused per entry
    std::string reason;    // why it is not readable (Unread, removed), in Kiln words
    std::string covered_by;  // "items.maxStack": a registry field over the same member (stable tier); of several, the one-to-one
                             // one, else the one whose own member it is (never a formula that only reads it)
    bool exact = false;      // ... the same number or true/false (false: a formula, an enum's own names, another conversion)
};

struct View {
    std::string name;
    bool stable = false;   // a registry
    std::string type;      // advanced: the root type (Kiln's part)
    uint32_t type_hash = 0;
    std::string name_member;  // advanced: the member that names entries ("debugName", "name"); "" = unnamed (GUID form)
    std::vector<ViewField> fields;
    bool truncated = false;   // more fields than kMaxFields: the rest are left out (named in the catalog)
    const ViewField* field(const std::string& kiln_path) const;  // "a[3].b" reads as "a[].b"
};

constexpr size_t kMaxFields = 4096;  // fields of one advanced view (a template-sized type has a few hundred)
constexpr int kMaxDepth = 8;          // nested structs and lists below the root

// The fields of one advanced view of a resource type: every member below the root, named, with the alias table's renames and
// removals, the registries' rows that cover them (rm may be null), in member order.
View derive(const TypeDesc& root, const std::string& name, const Aliases& aliases, const map::RegMap* rm);

// The catalog: every registry of the schema (stable) and every resource type given (advanced), named; a type whose derived name
// is a registry's, or another type's taken earlier (types in name order), gets the namespaced form, then the derived name and
// "_<type hash, 8 hex>" (the one place a view name has a "_"). The alias table's "types" rows name their types first. An id
// field's ref becomes the view of its target type.
struct Catalog {
    std::vector<View> views;  // stable first (schema order), then advanced by name
    std::map<std::string, size_t> by_name;
    std::map<std::string, std::string> by_type;  // qualified type -> view name (advanced views and the registries' entry types)
    const View* find(const std::string& name) const;
};
Catalog catalog(const Schema& schema, const std::vector<TypeDesc>& types, const Aliases& aliases, const map::RegMap* rm);

// A field's value as the view gives it, from the member's JSON (rbytes::member: numbers, true/false, the game's enum names,
// GUID text or "none", seconds for a time, lists, objects by member name): enums and flags by Kiln name, a list of structs as
// objects with flat dotted Kiln keys ("count", "item.min"). refs: an id field's target id from its GUID (may be empty: the
// GUID text stays). nullopt + *why (a value the field cannot hold).
using RefFn = std::function<std::optional<std::string>(const std::string& view, const std::string& guid)>;
std::optional<json> kiln_value(const View& v, const ViewField& f, const json& game, const RefFn& refs, std::string* why);
// The member path of a Kiln path with indices ("rewardItems[2].count" -> its member path with [2]); nullopt + *why.
std::optional<std::string> game_path(const View& v, const std::string& kiln_path, const ViewField** field, std::string* why);

// The entries' Kiln ids, as a registry's (regmap.h assign_ids: game:<debug name in lower case>, clash suffixes, pins), the
// unnamed form "<view in lower case>_<first 8 hex of the GUID>".
std::vector<std::string> ids(const View& v, const Aliases& aliases, const std::vector<map::GameEntry>& entries,
                             std::vector<std::string>* notes);
// A selector of one entry: its id exactly, a case-insensitive glob over ids ("game:quest_*", "*"), or its debug name (the old
// selector form, case-insensitive).
bool selects(const std::string& selector, const std::string& id, const std::string& name);

// The catalog as kiln.registry.view's views_json gives it, and one view's fields (fields_json). Kiln names only.
json views_json(const Catalog& c, const std::map<std::string, size_t>& entry_counts);
json fields_json(const View& v);

}  // namespace kc::registry::views
