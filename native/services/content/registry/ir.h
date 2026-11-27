// SPDX-License-Identifier: MIT
// kiln.content registry stage: the edit IR (registry P1 slice S3, plan: registry-p05 P1-PLAN.md section 4).
//
// One IR document is one batch of edits of one mod in one stage, the same from a data pack's "registry" section and from
// C# (Kiln.Registry serialises its calls into it):
//   {"ir": 1, "mod": "<id>", "modVersion": "<v>", "stage": "add|alter|final", "source": "<file or code:...>",
//    "edits": [ {"n": 0, "registry": "items", "select": {"id": "game:material_*", "where": {...}, "has": [...], "hasNot": [...]},
//                "field": "maxStack", "op": {"mul": 4, "max": 9999} | <plain value>, "exclusive": false, "yield": false},
//               {"registry": "recipes", "select": {...}, "field": "inputs", "list": {"append": [...]} },
//               {"registry": "items", "entry": "remove", "select": {"id": "game:x"}},
//               {"registry": "items", "entry": "add", "id": "<mod>:<name>", "like": "game:y", "fields": {"maxStack": 500}} ]}
// The op vocabulary and its evaluation are today's data packs' (packs.cpp parse_value): r = clamp(v * mul + add, min, max),
// then raiseOnly / lowerOnly against v, integer fields rounded. Validation is per edit and all or nothing: a malformed edit is
// refused with a reason, the rest of the batch stands. The schema descriptor below is what S1's generator emits
// (schema/registry/*.json); here it is filled by hand by the tests. The document's JSON Schema: schema/registry-ir.schema.json.
//
// The Advanced tier (owner decision D5): {"registry": "items", "select": {...}, "raw": {"damageSetup.dmgMod": 1.5}} names the
// game's own member paths of an entry, in a registry whose schema allows it ("raw": "allowed"). It works while the game's names
// and layout hold: each path is resolved per entry on this game build (S5's model) and an edit naming one that does not resolve
// there is refused, never written blind. The IR checks only the shape here (a member path, a plain value or an op); the type and
// range checks are the member's own, at composition (compose.h).
// Nothing here touches the game, a service table or a lock: plain data in, plain data out.
#pragma once
#include <cstdint>
#include <map>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace kc::registry {

using json = nlohmann::json;

// ---- the schema descriptor (generated from schema/registry/*.json by S1) ----
enum class Applies : uint8_t { Now = 1, WorldLoad = 2, Restart = 3 };  // KILN_REGISTRY_APPLIES_*
// Flags: a set of the schema's names (enum_values), held as a list in the schema's order, each name at most once.
// Struct: only as a list's element (FieldSpec::element): an element with fields of its own (recipes.inputs {item, count},
// drops.entries), its leaves in element_fields; the schema names each leaf "<list>[].<leaf>" ("inputs[].count").
enum class FieldType : uint8_t { Int, Float, Bool, Enum, Id, List, Flags, Struct };

// The schema's "ops" for a field: which edits it takes. Any: a hand-filled descriptor that says nothing (every edit its type
// allows). None: read-only. Set: a value only. Number: a value and the relative ops. List: clear, append, remove, replace (and
// a whole list through op). Indexed: a list of fixed length, edited one element at a time ("xpPerLevel[2]").
enum class FieldOps : uint8_t { Any, None, Set, Number, List, Indexed };

struct FieldSpec {
    std::string name;  // the Kiln field path, as edits name it ("maxStack", "damage.fire", "inputs", "inputs[].count")
    FieldType type = FieldType::Int;
    FieldType element = FieldType::Int;  // a list's elements
    std::optional<double> min, max;      // the range of a number (of a list's numbers)
    size_t max_items = 0;                // a list: at most this many (0 = no limit)
    std::vector<std::string> enum_values;
    bool read_only = false;
    Applies applies = Applies::Restart;
    std::string ref;  // an id (or a list of ids): the registry its ids name (the schema's "ref"); a reference the stage checks
    FieldOps ops = FieldOps::Any;
    size_t fixed_length = 0;   // ops Indexed: exactly this many elements (0: as long as length_field says, or not fixed)
    std::string length_field;  // ops Indexed: as long as this read-only whole-number field of the entry, plus length_offset
    int64_t length_offset = 0;
    std::vector<FieldSpec> element_fields;  // element Struct: its leaves by their own name ("item", "count"); link_elements()
    bool optional = false;   // an id that may be absent: null clears it (the schema's "optional")
    const FieldSpec* element_field(const std::string& leaf) const {
        for (const FieldSpec& f : element_fields)
            if (f.name == leaf) return &f;
        return nullptr;
    }
};

// "inputs[3].count" and "inputs[*].count" as the schema names them: "inputs[].count".
std::string generic_path(const std::string& path);

// One breaking step of a registry schema's versions and its id migration (schema/registry/migrations/<registry>-<from>-<to>.json,
// generated into schema.g.h): Kiln ids and field paths, old -> new.
struct Migration {
    std::string from, to;
    std::map<std::string, std::string> ids, fields;
};

struct RegistrySpec {
    std::string name;
    std::string version;  // the registry schema's "version" ("1.0.0"): registry.json "schema.registries"
    std::map<std::string, FieldSpec> fields;
    // The id migrations of its breaking steps, oldest first. parse_batch reads every edit through them before it is matched:
    // a field path this schema does not have, and a Kiln id (selected, a "like", a reference value), that a step renames is read
    // as its new name, through every later step too. Old names are never reused (ids.lock.json), so this is right whatever
    // schema version the edit was written for; a name the schema has is never renamed.
    std::vector<Migration> migrations;
    bool add = true, remove = true;
    bool raw = false;  // "raw": "allowed": edits may name the game's member paths (the Advanced tier, D5)
    uint32_t edits_per_call = 4096;  // edits of this registry one call takes (schema editsPerCall): refuse_over_cap
    // A field by its path; an element's index ("[3]", "[*]") reads as "[]": "inputs[2].count" is "inputs[].count".
    const FieldSpec* field(const std::string& path) const {
        auto it = fields.find(path);
        if (it == fields.end() && path.find('[') != std::string::npos) it = fields.find(generic_path(path));
        return it == fields.end() ? nullptr : &it->second;
    }
};

struct Schema {
    int format = 1;  // "registrySchema": the schema format (registry.json "schema.registrySchema")
    std::map<std::string, RegistrySpec> registries;
    const RegistrySpec* registry(const std::string& name) const {
        const auto it = registries.find(name);
        return it == registries.end() ? nullptr : &it->second;
    }
};

// After a descriptor is filled: every leaf row "<list>[].<leaf>" joins its list's element_fields (the list's element becomes
// Struct). schema_ir.h (generated_schema) calls it; so do hand-filled descriptors with element leaves.
void link_elements(Schema* schema);

// ---- values ----
struct Value {
    enum Kind : uint8_t { None, Int, Num, Bool, Text, List, Struct } kind = None;
    int64_t i = 0;
    double d = 0;
    bool b = false;
    std::string t;  // an enum member's name, an id
    std::vector<Value> list;        // a List's values; a Struct's values, in keys' order
    std::vector<std::string> keys;  // a Struct's leaf names, sorted
    const Value* leaf(const std::string& k) const {
        for (size_t n = 0; n < keys.size(); ++n)
            if (keys[n] == k) return &list[n];
        return nullptr;
    }

    static Value integer(int64_t v) { Value x; x.kind = Int, x.i = v, x.d = static_cast<double>(v); return x; }
    static Value number(double v) { Value x; x.kind = Num, x.d = v; return x; }
    static Value boolean(bool v) { Value x; x.kind = Bool, x.b = v; return x; }
    static Value text(std::string v) { Value x; x.kind = Text, x.t = std::move(v); return x; }
    static Value of_list(std::vector<Value> v) { Value x; x.kind = List, x.list = std::move(v); return x; }
    bool numeric() const { return kind == Int || kind == Num; }
    double as_double() const { return kind == Int ? static_cast<double>(i) : kind == Num ? d : kind == Bool ? (b ? 1.0 : 0.0) : 0.0; }
    json to_json() const;
    std::string text_of() const;  // for messages: a scalar as JSON writes it, a list or element as JSON
    friend bool operator==(const Value& a, const Value& b);
};

// A value in JSON for a field (or a list's element): checked against its type and range. false + *err: why not.
bool value_of(const json& j, const FieldSpec& f, bool element, Value* out, std::string* err);

// ---- edits ----
enum class Stage : uint8_t { Add = 0, Alter = 1, Final = 2 };
const char* stage_name(Stage s);
bool stage_of(const std::string& s, Stage* out);

struct Op {
    bool set = false;  // an absolute value (a plain value, or {"set": v})
    Value value;
    std::optional<double> mul, add, min, max;
    bool raise_only = false, lower_only = false;
    bool relative() const { return !set; }
    json to_json() const;
};

enum class ListOp : uint8_t { None, Clear, Append, Remove, Replace };
enum class EntryOp : uint8_t { None, Add, Remove };

struct Selector {
    std::string id;  // an id or a case-insensitive glob over ids ("game:material_*"); "*" = every entry
    json where;      // {"<field>": "<cond>" | <value>}: every condition holds (on the view the stage gives)
    std::vector<std::string> has, has_not;  // traits
    // Kiln-internal, never read from IR: entries this selector leaves out (lower-case Kiln ids). The live back-end sets it
    // for an entry-point change whose edit was refused for that entry alone (review4 NS6, review2 S3).
    std::set<std::string> except;
    bool exact() const { return id.find_first_of("*?") == std::string::npos; }
};

// One raw member path of an Advanced-tier edit (D5).
struct RawWrite {
    std::string path;  // as written ("damageSetup.dmgMod"), for messages and the report
    std::string key;   // raw_key(path): one spelling per place
    json op;           // a plain value or {set | mul | add | min | max | raiseOnly | lowerOnly}: typed per entry against the member
};

// A field path into one element of a list: "inputs[2].count" (a leaf of element 2), "inputs[*].count" (that leaf of every
// element), "inputs[3]" / "perks[1]" (the whole element). The edit changes it inside the list's own chain.
struct ElementRef {
    bool on = false;
    std::string list;  // "inputs"
    bool all = false;  // [*]
    uint32_t index = 0;
    std::string leaf;  // "count"; empty: the whole element
};

struct Edit {
    uint32_t n = 0;  // ordinal in its batch
    std::string registry;
    Selector select;
    std::string field;  // as the schema names a place: "maxStack", "inputs", "inputs[2].count" (indices as plain numbers)
    ElementRef element;  // field names an element of a list
    Op op;
    ListOp list = ListOp::None;
    std::vector<Value> list_values;
    EntryOp entry = EntryOp::None;
    std::string add_id, like;
    std::map<std::string, Value> add_fields;
    std::vector<RawWrite> raw;  // not empty: an Advanced-tier edit of raw member paths (then field, op and list are unset)
    bool exclusive = false, yield = false;
    // Kiln-internal, never read from IR: a target this edit cannot take (a conflict, a value that does not fit) refuses that
    // entry alone, named, and the edit applies to the others (entry-point live changes, review4 NS6). Off: the whole edit.
    bool per_entry = false;
    // Kiln-internal, never read from IR: a replayed archive-writer package's recorded value (effect replay, D6; replay.h),
    // standing in for the write its Lua no longer makes: like an archive row it is never "an earlier writer" for another
    // edit's exclusive or yield, so a replay never refuses or holds back a mod's edit.
    bool stand_in = false;
};

struct Batch {
    std::string mod, mod_version, source;
    Stage stage = Stage::Alter;
    std::vector<Edit> edits;
    // What the schemas' id migrations changed in this batch's edits ("#2: items.stackSize -> items.maxStack (items schema
    // 0.1.0 -> 0.2.0)"): for kiln.log and registry.json "migrated". Not IR.
    std::vector<std::string> migrated;
};

struct Refusal {
    std::string mod, source;
    Stage stage = Stage::Alter;
    uint32_t n = 0;
    std::string reason;
    std::string entry;  // a refused add: the "<registry>|<id>" it would have made (a reference to it names this refusal as its cause)
    std::string registry, id, field;  // where it was refused, when known: the registry, the entry, the Kiln field or raw path
    bool raw = false;                  // field is a raw path (the Advanced tier), not a Kiln field: readers hide it
};

// One IR document. false: the document itself is unusable (*err: why; nothing of it is taken): not JSON, nested deeper than
// 32 levels, larger than 16 MiB, no usable ir / mod / stage / edits. true: *out holds every edit that passed validation; each
// refused edit is one row in *refused, named by its mod, source and ordinal ("n", else its position; an "n" that is not
// 0..4294967295 or is used twice refuses that edit). No exception leaves either form.
bool parse_batch(const std::string& text, const Schema& schema, Batch* out, std::vector<Refusal>* refused, std::string* err);
// One call's edits per registry against each registry's edits-per-call cap (the schema's editsPerCall; 4096 where it declares
// none): the batch's edits and its refused rows, plus `prior` (a live change's earlier batches, by registry). Empty when every
// registry fits. Otherwise the whole batch is refused: one row per registry the batch names, the over-cap registries' with
// the count and the cap, the others' naming the registry that made the call too big (owner 2026-11-26: the cap is per
// registry; a call over it is refused loudly and whole, never thrown in the mod's code, never cut). Code batches only (a
// stage submit, a live change); data sections from files are not calls.
std::vector<Refusal> refuse_over_cap(const Batch& b, const std::vector<Refusal>& refused, const Schema& schema,
                                     const std::map<std::string, size_t>& prior = {});
bool parse_batch(const json& doc, const Schema& schema, Batch* out, std::vector<Refusal>* refused, std::string* err);

// A data pack's "registry" section (schema/registry-section.schema.json): {"add": [edits], "alter": [edits], "final":
// [edits]}, each list an IR batch of that stage for mod, named source ("content/<file>.json"). false: the section itself is
// unusable (*err; nothing of it is taken). true: *out holds one batch per stage present (add, alter, final), each edit
// checked as parse_batch checks it (refusals in *refused).
bool lower_section(const json& section, const std::string& mod, const std::string& mod_version, const std::string& source, const Schema& schema,
                   std::vector<Batch>* out, std::vector<Refusal>* refused, std::string* err);

// The IR document of a batch, as parse_batch reads it back: the same edits, ordinals, selectors, values and flags (the
// Kiln-internal Selector::except and Edit::per_entry are not IR and are left out). The stage keeps a start's lowered edits in
// its plan with it (the plan a rollback starts on, stage.h).
json batch_json(const Batch& b);

// Today's numeric operation on a value (packs.cpp parse_value's order): the base for a relative op. Integer fields round.
// false + *why: the result is not finite, or past what a whole-number field holds (checked before any rounding).
bool op_result(const Op& op, const Value& base, const FieldSpec& f, Value* out, std::string* why);
// The same, total: a result op_result refuses leaves the value as it was (for back-ends re-evaluating a chain over another
// value; the stage itself refuses such an edit through op_result).
Value apply_op(const Op& op, const Value& base, const FieldSpec& f);

// Whether a field's schema ops take this edit (op / list / element path). false + *err: why not. The IR reader asks it for
// every field edit; composition asks again for edits made by code.
bool ops_allow(const FieldSpec& f, const Edit& e, std::string* err);

// Values in one list op, one list value, and in any list the stage composes (a list field without maxItems included).
constexpr size_t kMaxListValues = 4096;

// A Kiln mod id ("^[a-z0-9][a-z0-9._-]{0,63}$", kiln.mod.schema.json) that is not reserved ("game", "kiln", ...): a batch of
// any other "mod" is refused whole, so no mod mints game: or kiln: ids. false + *err: why not.
bool mod_id_ok(const std::string& id, std::string* err);

// An edit's "op" for field f: a plain value (an absolute set), {"set": v}, or relative numbers {mul, add, min, max, raiseOnly,
// lowerOnly} (number fields only). false + *err: why not.
bool op_of(const json& j, const FieldSpec& f, Op* op, std::string* err);

// A raw member path ("damageSetup.dmgMod", "perkIds[2]", "a.b[03].c": member names [A-Za-z_][A-Za-z0-9_]*, indices [n]) into
// its key, one spelling per place: names lower-case (the game's reflection names are matched without case, as packs do),
// indices as plain numbers ("a.b[3].c"). No "*": a raw edit names exact places. false + *err: not such a path.
bool raw_key(const std::string& path, std::string* key, std::string* err);
// The field name a raw path's chain is kept under (compose.h FieldRef::field): "raw:" + its key. Schema fields have no ':'.
inline std::string raw_field(const std::string& key) { return "raw:" + key; }
inline bool is_raw_field(const std::string& field) { return field.rfind("raw:", 0) == 0; }

// Case-insensitive glob ('*' any run, '?' one character), as packs' selectors.
bool glob_ci(const std::string& pattern, const std::string& text);

}  // namespace kc::registry
