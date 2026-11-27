// SPDX-License-Identifier: MIT
// kiln.content: the registry's live back-end (registry P1 slice S9). What it does and why: registry_live.h.
#include "registry_live.h"
#include "registry/saves.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <optional>
#include <string_view>
#include <tuple>
#include <unordered_map>

#include "registry/drift.h"
#include "registry/formulas.h"
#include "registry/stage.h"
#include "registry/unmapped.h"
#include "services/kiln_gamedata.h"
#include "services/kiln_registry_live.h"

namespace kc {

uint32_t (*g_registry_type_size)(const char* qname) = &type_size;
uint64_t steady_ms() {
    return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
uint64_t (*g_registry_now_ms)() = &steady_ms;
bool (*g_registry_stack_guard)(std::string* why) = [](std::string* why) { return guard_set(true, why); };

namespace {

using registry::ChainRow;
using registry::FieldSpec;
using registry::FieldType;
using registry::Value;
namespace rmap = registry::map;
constexpr char kNotExact[] = KILN_REGISTRY_LIVE_REASON_NOT_EXACT ": ";
bool not_exact_reason(const std::string& reason) { return reason.rfind(kNotExact, 0) == 0; }

using SlotBytes = std::array<uint8_t, kSlotBytes>;
struct LiveSlot {
    uintptr_t addr;
    uint8_t size;
};
// One bound slot as the last report saw it (the later-writes look, late_check): its game bytes and store layers, as one number.
struct LateSlot {
    uintptr_t addr;
    uint8_t size;
    uint64_t sig;
};
struct LiveField {
    FieldSpec spec;
    std::vector<LiveSlot> slots;
    std::function<std::optional<Value>(const std::vector<SlotBytes>&, std::vector<double>*)> decode;
};

struct LiveState {
    std::shared_ptr<RegistryLive> in;
    std::map<std::string, uint64_t> hashes;  // rel -> section hash, last application
    std::map<std::string, std::pair<std::string, std::string>> section_of;  // rel -> (mod, source), last application
    bool applied = false;                     // a first application happened since the install
    std::unordered_map<std::string, std::string> kiln_of;  // "<registry>|<32-bit id>" -> Kiln id (from nums)
    std::unordered_map<std::string, std::string> kiln_by_guid;  // "<registry>|<guid, lower case>" -> Kiln id (from ids)
    std::vector<registry::Refusal> refused;        // this application's live refusals
    std::set<std::string> refused_keys;
    struct Pending {
        registry::FieldRef field;
        std::string guid;
    };
    std::vector<Pending> pending_tables;  // drop-table fields whose table the world has not loaded yet
    // drop tables bound: the fields and the stack slots their layers are on, by the table's GUID (lower case), so a table
    // newcontent repoints (selector stacks copied to near memory) is bound again over the stacks the game reads now
    std::map<std::string, std::vector<registry::FieldRef>> table_fields;
    std::map<std::string, std::vector<uintptr_t>> table_addrs;
    std::map<registry::FieldRef, LiveField> fields;  // bound world fields and their report readers
    bool tables_changed = false;
    registry::Result pending;  // the last application's report, waiting for registry_live_publish
    bool pending_set = false;
    bool pending_application = false;
    bool pending_change = false;
    size_t skipped_logged = 0;  // store_skipped rows already in kiln.log
    // P32: the Kiln place ("items[game:x].maxStack") of every slot a bound field had since the install, so a skipped
    // write or revert is named in Kiln words even after its field was unbound (a removed pack, an ended world)
    std::unordered_map<uintptr_t, std::string> slot_places;
    std::vector<std::string> suspects;  // the drift probe's suspects in this world (drift.h), read at world begin
    std::set<registry::EntryRef> boot_written;  // entries an old boot document wrote (RegistryLive::old's OldPatch rows), per report
    // Writes made after the last report (registry-followups 1): what that report saw, so registry_live_publish can tell, cheaply
    // and never per tick, when a bound field's slot changed since (late_check): the store's generation, a signature of each
    // bound slot (the game's bytes there and its store layers), when it last looked and how long the next look waits. A look
    // goes over late_slots a batch a tick (late_next: the next one; late_slots.size(): no look going on).
    uint64_t late_gen = 0, late_at = 0, late_wait = 0;
    std::vector<LateSlot> late_slots;
    size_t late_next = 0;
    size_t late_rebuilds = 0;  // in this install (kiln.log names the first one, the others go to the debug log)
    std::map<KilnOwner, std::string> owner_names;  // the core's id of an owner a code writer's row names (name_code_writer)
    registry::Result reported;
    bool have_report = false;
    uint32_t ordinal = 0;
    KilnOwner owner = 0;
    struct Change {
        KilnOwner owner = 0;
        std::string mod;
        uint32_t point = 0;
        uint64_t world = 0, order = 0;
        size_t bytes = 0;
        std::vector<registry::Batch> batches;
        std::vector<registry::Refusal> refused;
        bool restart = false, entry_restart = false;
        // Rejected at an application of this world (binding, evaluation or write failure): not composed until the world
        // changes; the handle stays valid and a process change is tried again in the next world (review4 NS2).
        bool inactive = false;
        uint64_t inactive_world = 0;
        // review4 NS6: an entry-point change (MOD_LOAD, WORLD_LOAD, HOT_RELOAD) refuses per entry. Entries refused at
        // binding or evaluation in this world are left out of its composition until the world changes, with their rows.
        std::map<std::pair<std::string, uint32_t>, std::set<std::string>> world_except;  // (source, n) -> Kiln ids
        std::vector<registry::Refusal> world_rows;
        uint64_t except_world = 0;
    };
    std::map<uint64_t, Change> changes;
    std::set<KilnOwner> dead;
    uint64_t world = 0, next_order = 0;
    uint32_t world_kind = KILN_WORLD_KINDS;
    registry::Result stage_result;
    std::map<registry::EntryRef, registry::EntryDefinition> entry_catalog;
    std::map<registry::EntryRef, std::string> entry_failures, removal_failures;
    std::shared_ptr<const registry::PreparedBase> prepared;
    bool reading = false;
    std::string read_reason;
    bool read_absent = false;  // the read's entry has none of the field (a creature without the component, a perk without a value)
    std::map<registry::FieldRef, LiveField> read_fields;
    bool read_capture = false;                   // a pass read: put() keeps each slot's evaluation (review4 NS5)
    std::map<uintptr_t, LayerEval> read_evals;
    const Entry* initializing_entry = nullptr; // unpublished, private clone; store_mutex protects this scoped override
    const Schema* initializing_schema = nullptr;
    registry::EntryRef initializing_ref;
    std::map<uintptr_t, std::pair<uint8_t, LayerEval>> initialize_evals;
};
LiveState g;  // under store_mutex
bool inactive_now(const LiveState::Change& c) { return c.inactive && c.inactive_world == g.world; }
// Entry points refuse per entry like GameStart; a MANUAL transaction is all-or-nothing for binding and evaluation (review4 NS6).
bool per_entry(const LiveState::Change& c) { return c.point != KILN_REGISTRY_LIVE_MANUAL; }
uint64_t g_next_change = 0, g_next_world = 0; // never reused, including after an install
std::mutex g_retiring_mu;
std::set<KilnOwner> g_retiring;
thread_local bool g_transaction_active = false;
struct TransactionScope {
    TransactionScope() { g_transaction_active = true; }
    ~TransactionScope() { g_transaction_active = false; }
};
// All callers hold store_mutex, so the token, kind and admission decision are one snapshot.
bool on_live_thread() {
    return !g.world || (g.in && g.in->on_world_thread && g.in->on_world_thread(g.world_kind));
}
// All callers hold store_mutex; the leaf lock covers only incoming retirement notifications.
bool retired(KilnOwner owner) {
    std::lock_guard lock(g_retiring_mu);
    return g.dead.count(owner) || g_retiring.count(owner);
}
auto& fields_target() { return g.reading ? g.read_fields : g.fields; }

uint32_t kind_of(const std::string& registry) {
    if (registry == "items") return KILN_CONTENT_ITEMS;
    if (registry == "recipes") return KILN_CONTENT_RECIPES;
    if (registry == "loot") return KILN_CONTENT_LOOT;
    if (registry == "drops") return KILN_CONTENT_DROPS;
    return 0;
}

// dev.32: registries whose entries are kiln.gamedata's (creatures: the templates; arsenals; perks; skills: the skill tree's
// nodes and buffs: the buff collection's records, both by their 32-bit id; abilities: sequence resources): bound through gamedata's value slots (gd_value_slot), their store kind GD_KIND_BASE + the section,
// so a data pack's section layers lie below the registry's and the content sync sends the registry's. 0: not one of them.
uint32_t gd_section_of(const std::string& registry) {
    if (registry == "creatures") return KILN_GD_TEMPLATES;
    if (registry == "arsenals") return KILN_GD_ARSENALS;
    if (registry == "perks") return KILN_GD_PERKS;
    if (registry == "skills") return KILN_GD_SKILLS;
    if (registry == "buffs") return KILN_GD_BUFFS;
    if (registry == "abilities") return KILN_GD_SEQUENCES;  // a sequence resource (its first part is the section's entry)
    return 0;
}
const char* entry_word(const std::string& registry) {
    if (registry == "creatures") return "creature";
    if (registry == "arsenals") return "arsenal";
    if (registry == "perks") return "perk";
    if (registry == "skills") return "skill node";
    if (registry == "buffs") return "buff";
    if (registry == "abilities") return "ability";
    return "entry";
}

std::string num_key(const std::string& registry, uint32_t num) { return registry + "|" + std::to_string(num); }

std::vector<const ChainRow*> registry_rows(const registry::FieldOutcome& fo) {
    std::vector<const ChainRow*> out;
    for (const ChainRow& r : fo.chain)
        if (r.kind == registry::WriterKind::Registry || r.kind == registry::WriterKind::Check) out.push_back(&r);
    return out;
}

// One refusal per (field, reason): the edit that could not be written live.
void refuse_rows(const registry::FieldRef& fr, const std::string& mod, const std::string& source, registry::Stage stage, uint32_t n, const std::string& why) {
    if (g.reading) { g.read_reason = why; return; }
    const std::string key = fr.text() + "|" + why;
    if (!g.refused_keys.insert(key).second) return;
    registry::Refusal r;
    r.mod = mod, r.source = source, r.stage = stage, r.n = n;
    r.registry = fr.registry, r.id = fr.id, r.raw = registry::is_raw_field(fr.field);
    r.field = r.raw ? fr.field.substr(4) : fr.field;
    const bool said = why.size() >= 11 && why.compare(why.size() - 11, 11, "not applied") == 0;
    r.reason = not_exact_reason(why) ? why : fr.text() + ": " + why + (said ? "" : "; this edit was not applied");
    g.refused.push_back(std::move(r));
}
void refuse(const registry::FieldRef& fr, const std::vector<const ChainRow*>& rows, const std::string& why) {
    if (rows.empty()) return refuse_rows(fr, "", "", registry::Stage::Alter, 0, why);
    refuse_rows(fr, rows.back()->mod, rows.back()->source, rows.back()->stage, rows.back()->n, why);
}

// The game entry of a Kiln id in this world.
const Entry* find_entry(const std::string& registry, const std::string& id, std::string* guid_out) {
    const uint32_t kind = kind_of(registry);
    if (!kind) return nullptr;
    if (g.initializing_entry && g.initializing_ref == registry::EntryRef{registry, id}) return g.initializing_entry;
    const Registry& r = reg(kind);
    const std::string key = registry + "|" + id;
    std::string guid;
    if (auto it = g.in->ids.find(key); it != g.in->ids.end()) guid = it->second;
    const bool added = std::find(g.in->result.added.begin(), g.in->result.added.end(), registry::EntryRef{registry, id}) != g.in->result.added.end();
    if (guid.empty() && added && g.in->added_guid) guid = g.in->added_guid(registry, id);
    if (guid_out) *guid_out = guid;
    if (kind == KILN_CONTENT_DROPS) return guid.empty() ? nullptr : r.lookup(guid);
    if (auto it = g.in->nums.find(key); it != g.in->nums.end()) return r.get(it->second);
    if (guid.empty()) return nullptr;
    for (const Entry& e : r.entries)
        if (lower(guid_text(e.guid)) == lower(guid)) return &e;
    return nullptr;
}

// ---- values: Kiln <-> the game's ----
// The game's enum member name for a Kiln name (the row's values), and back.
std::string game_name(const rmap::Row* row, const std::string& kiln) {
    if (row)
        if (auto it = row->values.find(kiln); it != row->values.end()) return it->second;
    return kiln;
}
std::string kiln_name(const rmap::Row* row, const std::string& game) {
    if (row)
        for (auto& [k, v] : row->values)
            if (v == game || lower(v) == lower(game)) return k;
    return game;
}

Value to_kiln(const Field& f, const rmap::Row* row, const FieldSpec& spec, const uint8_t* bytes) {
    const KilnContentValue kv = decode(f, bytes);
    switch (spec.type) {
        case FieldType::Int:
            return Value::integer(kv.type == KILN_CV_FLOAT || kv.type == KILN_CV_TIME ? std::llround(as_double(kv)) : kv.i);
        case FieldType::Float: return Value::number(as_double(kv));
        case FieldType::Bool: return Value::boolean(kv.i != 0);
        case FieldType::Enum:
            if (f.en)
                for (auto& [n, x] : f.en->values)
                    if (x == kv.i) return Value::text(kiln_name(row, n));
            return Value::text(std::to_string(kv.i));
        default: return Value{};
    }
}

// A live refusal names Kiln's field and entry, never the game's member (owner's principle: mods and players see Kiln names;
// registry-fixes item 5). encode (refl.cpp) names the member it writes: its refusals here say "the game's storage of this
// field" instead, and kiln.log keeps the member at debug level for maintainers. `name`: the field as mods name it.
bool encode_kiln(const Field& f, const KilnContentValue& kv, uint8_t* out, std::string* why, const std::string& name) {
    if (encode(f, kv, out, why)) return true;
    if (!why || f.path.empty()) return false;
    LOGD("registry (live): %s: %s", name.c_str(), why->c_str());
    const std::string& m = f.path;
    if (why->rfind(m + " is read-only", 0) == 0) *why = "this field is read-only on this game build";
    else if (why->rfind(m + " cannot be written", 0) == 0) *why = "this field cannot be written on this game build";
    else if (const size_t at = why->find(" does not fit " + m); at != std::string::npos)
        *why = why->substr(0, at) + " does not fit the game's storage of this field" + why->substr(at + 14 + m.size());
    return false;
}
// The field as mods name it: "<registry>.<field>"; a raw member (the Advanced tier) is the member path the mod named.
std::string kiln_field_name(const registry::FieldRef& fr) {
    return registry::is_raw_field(fr.field) ? fr.field.substr(4) : fr.registry + "." + fr.field;
}

bool to_game(const Field& f, const rmap::Row* row, const FieldSpec& spec, const Value& v, uint8_t* out, std::string* why,
             const std::string& name) {
    KilnContentValue kv{};
    switch (spec.type) {
        case FieldType::Int:
            kv.type = KILN_CV_INT, kv.i = v.kind == Value::Int ? v.i : std::llround(v.as_double()), kv.f = static_cast<double>(kv.i);
            break;
        case FieldType::Float:
            kv.type = f.vt == KILN_CV_TIME ? KILN_CV_TIME : KILN_CV_FLOAT, kv.f = v.as_double();
            if (kv.type == KILN_CV_TIME && kv.f == 0) kv.type = KILN_CV_INT, kv.i = 0;  // 0 s (encode reads a TIME of f 0 from i)
            break;
        case FieldType::Bool: kv.type = KILN_CV_BOOL, kv.i = v.kind == Value::Bool ? v.b : v.as_double() != 0; break;
        case FieldType::Enum: {
            // review-registry-r3a (P45): the game's name of the value is looked up, the refusal names the Kiln field (`name`)
            const std::string game = game_name(row, v.t);
            bool found = false;
            if (f.en)
                for (auto& [n, x] : f.en->values)
                    if (!found && (n == game || lower(n) == lower(game))) kv.type = KILN_CV_INT, kv.i = x, found = true;
            if (!found) return *why = "\"" + v.t + "\" is not a value of " + name + " on this game build", false;
            break;
        }
        default: return *why = "this kind of field is not written live", false;
    }
    return encode_kiln(f, kv, out, why, name);
}

bool in_range(const FieldSpec& spec, const Value& v, std::string* why) {
    if (!v.numeric()) return true;
    const double d = v.as_double();
    if ((spec.min && d < *spec.min) || (spec.max && d > *spec.max) || !std::isfinite(d))
        return *why = "the result " + v.text_of() + " over the value below is outside the field's range", false;
    return true;
}

gear::Constants constants() {
    std::map<std::string, double> balance;
    if (auto it = g.in->base.registries.find("balance"); it != g.in->base.registries.end())
        for (const auto& entry : it->second)
            for (const auto& [field, value] : entry.fields)
                if (field.rfind("gear.", 0) == 0 && value.numeric()) balance[field] = value.as_double();
    for (const auto& [fr, fo] : g.in->result.fields)
        if (fr.registry == "balance" && fr.field.rfind("gear.", 0) == 0 && fo.result.numeric()) balance[fr.field] = fo.result.as_double();
    return registry::formulas::constants_of(balance);
}

// One field's binding: what every evaluated layer of it shares.
struct Binding {
    registry::FieldRef ref;
    std::vector<ChainRow> rows;  // the registry rows, in chain order
    FieldSpec spec;
    std::optional<rmap::Row> row;  // empty: a raw member
    uint32_t kind = 0;
    Entry entry;
};

bool fail(const std::shared_ptr<Binding>& b, const std::string& why) {
    const ChainRow& r = b->rows.back();
    refuse_rows(b->ref, r.mod, r.source, r.stage, r.n, why);
    return false;
}

std::string source_of(const Binding& b) {
    const ChainRow& r = b.rows.back();
    return (r.mod.empty() ? r.who : r.mod) + ": " + r.source + " (registry " + b.ref.registry + "[" + b.ref.id + "]." + b.ref.field + ")";
}

bool put(const std::shared_ptr<Binding>& b, int32_t field_index, int32_t sub, uintptr_t addr, uint8_t n, LayerEval ev) {
    if (g.reading) {
        if (g.initializing_entry) g.initialize_evals.insert_or_assign(addr, std::make_pair(n, std::move(ev)));
        else if (g.read_capture) g.read_evals.insert_or_assign(addr, std::move(ev));
        return true;
    }
    LayerModScope scope(b->rows.back().mod);
    return store_put_eval(registry_owner(), registry_seq(++g.ordinal), b->kind, b->entry.id, field_index, sub, addr, n, std::move(ev), source_of(*b));
}

bool put_at(const std::shared_ptr<Binding>& b, uint32_t kind, uint32_t id, int32_t field_index, int32_t sub, uintptr_t addr, uint8_t n, LayerEval ev) {
    if (g.reading) {
        if (g.initializing_entry) g.initialize_evals.insert_or_assign(addr, std::make_pair(n, std::move(ev)));
        else if (g.read_capture) g.read_evals.insert_or_assign(addr, std::move(ev));
        return true;
    }
    LayerModScope scope(b->rows.back().mod);
    return store_put_eval(registry_owner(), registry_seq(++g.ordinal), kind, id, field_index, sub, addr, n, std::move(ev), source_of(*b));
}

const Schema& binding_schema(uint32_t kind) { return g.initializing_schema ? *g.initializing_schema : reg(kind).schema; }
int32_t index_of(uint32_t kind, const Field* f) { return static_cast<int32_t>(f - binding_schema(kind).fields.data()); }

void track_scalar(const std::shared_ptr<Binding>& b, uintptr_t addr, const Field& f) {
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, {{addr, f.bytes}},
        [b, f](const std::vector<SlotBytes>& bytes, std::vector<double>*) {
            return to_kiln(f, b->row ? &*b->row : nullptr, b->spec, bytes[0].data());
        }});
}

// ---- saves (registry/saves.h): a write that would change what existing saves hold ----
// Refuse: the layer is not written (the value under it stays), the edit refused for this entry with why. Guard: the stack
// guard goes on before the lowered max stack is written; where it cannot, the edit is refused for this entry. A read (no
// write) only evaluates.
bool save_check(const std::shared_ptr<Binding>& b, const Value& under, const Value& v, std::string* why) {
    if (!b->row) return true;  // a raw member (Advanced tier): composed with its field or refused at composition
    // review: a new entry's first values (its add's "fields", over the bytes of the entry it is like) are not an edit of
    // anything a save holds; a refusal here would refuse the whole entry. Its later edits are checked like any other.
    if (g.initializing_entry) return true;
    const auto verdict = registry::saves::judge(b->ref.registry, b->ref.field, under, v);
    if (!verdict) return true;
    if (verdict->decision == registry::saves::Decision::Refuse) return *why = "saves: " + b->ref.text() + " " + verdict->text, false;
    if (verdict->decision != registry::saves::Decision::Guard || g.reading) return true;
    std::string w;
    if (g_registry_stack_guard && g_registry_stack_guard(&w)) return true;
    return *why = "saves: " + b->ref.text() + " " + verdict->unguarded + (w.empty() ? "" : " (" + w + ")"), false;
}

// ---- scalars: int, float, bool, time, enum; raw members ----
void bind_scalar(const std::shared_ptr<Binding>& b, const Field* f) {
    const Field fc = *f;
    LayerEval ev = [b, fc](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const rmap::Row* row = b->row ? &*b->row : nullptr;
        const Value under = to_kiln(fc, row, b->spec, below);
        const Value v = registry::evaluate(b->rows, under, b->spec);
        if (!in_range(b->spec, v, why) || !save_check(b, under, v, why) || !to_game(fc, row, b->spec, v, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    if (!put(b, index_of(b->kind, f), -1, b->entry.addr + f->offset, f->bytes, std::move(ev))) fail(b, "the field is not readable");
    else track_scalar(b, b->entry.addr + f->offset, *f);
}

// ---- flags: a bitmask member as a set of Kiln names (the row's values), held in the schema's order ----
// The game's bit of a Kiln flag name on this build, or -1 when the game has no such value.
int flag_bit(const Field& f, const rmap::Row* row, const std::string& kiln) {
    const std::string name = game_name(row, kiln);
    if (f.en)
        for (auto& [n, x] : f.en->values)
            if ((n == name || lower(n) == lower(name)) && x >= 0 && x < 64) return static_cast<int>(x);
    return -1;
}
Value flags_kiln(const Field& f, const rmap::Row* row, const FieldSpec& spec, const uint8_t* bytes) {
    const uint64_t mask = static_cast<uint64_t>(decode(f, bytes).i);
    std::vector<Value> out;
    for (const std::string& n : spec.enum_values)
        if (const int bit = flag_bit(f, row, n); bit >= 0 && ((mask >> bit) & 1)) out.push_back(Value::text(n));
    return Value::of_list(std::move(out));
}
// The bits of the names in v over the value below; bits Kiln has no name for stay as the game has them.
bool flags_game(const Field& f, const rmap::Row* row, const FieldSpec& spec, const Value& v, const uint8_t* below, uint8_t* out, std::string* why,
                const std::string& name) {
    if (v.kind != Value::List) return *why = "a flags value is a list of names", false;
    uint64_t named = 0, set = 0;
    for (const std::string& n : spec.enum_values)
        if (const int bit = flag_bit(f, row, n); bit >= 0) named |= 1ull << bit;
    for (const Value& x : v.list) {
        const int bit = x.kind == Value::Text ? flag_bit(f, row, x.t) : -1;
        if (bit < 0) return *why = "\"" + (x.kind == Value::Text ? x.t : x.text_of()) + "\" is not a value of " + name + " on this game build", false;
        set |= 1ull << bit;
    }
    KilnContentValue kv{};
    kv.type = KILN_CV_INT;
    kv.i = static_cast<int64_t>((static_cast<uint64_t>(decode(f, below).i) & ~named) | set);
    kv.f = static_cast<double>(kv.i);
    return encode_kiln(f, kv, out, why, name);
}

void bind_flags(const std::shared_ptr<Binding>& b, const Field* f) {
    const Field fc = *f;
    LayerEval ev = [b, fc](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const rmap::Row* row = b->row ? &*b->row : nullptr;
        const Value v = registry::evaluate(b->rows, flags_kiln(fc, row, b->spec, below), b->spec);
        if (!flags_game(fc, row, b->spec, v, below, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    const uintptr_t at = b->entry.addr + f->offset;
    if (!put(b, index_of(b->kind, f), -1, at, f->bytes, std::move(ev))) return (void)fail(b, "the field is not readable");
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, {{at, f->bytes}},
        [b, fc](const std::vector<SlotBytes>& bytes, std::vector<double>*) -> std::optional<Value> {
            return flags_kiln(fc, b->row ? &*b->row : nullptr, b->spec, bytes[0].data());
        }});
}

// ---- optionalEnum: [the switch (bool), the enum], one Kiln name; "none" is the switch off, the enum then left as it is ----
Value optional_kiln(const Field& sw, const Field& en, const rmap::Row* row, const uint8_t* swb, const uint8_t* enb) {
    if (decode(sw, swb).i == 0) return Value::text("none");
    const int64_t x = decode(en, enb).i;
    if (en.en)
        for (auto& [n, v] : en.en->values)
            if (v == x) return Value::text(kiln_name(row, n));
    return Value::text(std::to_string(x));
}
// The game's value of a Kiln name other than "none", or nullopt when this build's enum has none.
std::optional<int64_t> optional_game(const Field& en, const rmap::Row* row, const std::string& kiln) {
    const std::string name = game_name(row, kiln);
    if (en.en && !name.empty())
        for (auto& [n, x] : en.en->values)
            if (n == name || lower(n) == lower(name)) return x;
    return std::nullopt;
}

void bind_optional_enum(const std::shared_ptr<Binding>& b, const Field* swf, const Field* enf) {
    const Field sw = *swf, en = *enf;
    const uintptr_t base = b->entry.addr;
    // the field's value over the values below the registry, `mine` (0: the switch, 1: the enum) taken from the slot evaluated
    auto value = [b, sw, en, base](int mine, const uint8_t* below, std::string* why) -> std::optional<Value> {
        uint8_t swb[kSlotBytes] = {}, enb[kSlotBytes] = {};
        if (mine == 0) std::memcpy(swb, below, sw.bytes); else store_below_registry(base + sw.offset, sw.bytes, swb);
        if (mine == 1) std::memcpy(enb, below, en.bytes); else store_below_registry(base + en.offset, en.bytes, enb);
        const rmap::Row* row = b->row ? &*b->row : nullptr;
        const Value v = registry::evaluate(b->rows, optional_kiln(sw, en, row, swb, enb), b->spec);
        if (v.kind != Value::Text) return *why = "an optional enum's value is one name", std::nullopt;
        if (v.t != "none" && !optional_game(en, row, v.t)) return *why = "\"" + v.t + "\" is not a value of " + kiln_field_name(b->ref) + " on this game build", std::nullopt;
        return v;
    };
    LayerEval sw_ev = [b, sw, value](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const auto v = value(0, below, why);
        if (!v) return fail(b, *why);
        KilnContentValue kv{};
        kv.type = KILN_CV_BOOL, kv.i = v->t != "none", kv.f = static_cast<double>(kv.i);
        if (!encode_kiln(sw, kv, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    LayerEval en_ev = [b, en, value](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const auto v = value(1, below, why);
        if (!v) return fail(b, *why);
        if (v->t == "none") {  // the switch is off: the enum below stays
            std::memcpy(out, below, en.bytes);
            return true;
        }
        KilnContentValue kv{};
        kv.type = KILN_CV_INT, kv.i = *optional_game(en, b->row ? &*b->row : nullptr, v->t), kv.f = static_cast<double>(kv.i);
        if (!encode_kiln(en, kv, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    if (!put(b, index_of(b->kind, swf), -1, base + sw.offset, sw.bytes, std::move(sw_ev)) ||
        !put(b, index_of(b->kind, enf), -1, base + en.offset, en.bytes, std::move(en_ev)))
        return (void)fail(b, "the field is not readable");
    // a roll reads the switch, then the enum: the switch turns on after the enum lands and off before it changes
    if (!g.reading) store_set_gate(base + sw.offset);
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, {{base + sw.offset, sw.bytes}, {base + en.offset, en.bytes}},
        [b, sw, en](const std::vector<SlotBytes>& bytes, std::vector<double>*) -> std::optional<Value> {
            return optional_kiln(sw, en, b->row ? &*b->row : nullptr, bytes[0].data(), bytes[1].data());
        }});
}

// ---- formulas: power, armor ----
void bind_formula(const std::shared_ptr<Binding>& b, const std::vector<const Field*>& inputs) {
    std::vector<Field> fs;
    for (const Field* f : inputs) fs.push_back(*f);
    const uintptr_t base = b->entry.addr;
    LayerEval ev = [b, fs, base](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        std::vector<double> in;
        in.push_back(as_double(decode(fs[0], below)));
        for (size_t k = 1; k < fs.size(); ++k) {
            uint8_t x[kSlotBytes] = {};
            if (!store_effective(base + fs[k].offset, fs[k].bytes, x)) {
                LOGD("registry (live): %s: the formula's input %s is not readable", b->ref.text().c_str(), fs[k].path.c_str());
                return fail(b, *why = "a value this field's formula reads is not readable");
            }
            in.push_back(as_double(decode(fs[k], x)));
        }
        const gear::Constants c = constants();
        double kiln = 0, game = 0;
        if (!registry::formulas::read(b->row->formula, in, c, &kiln)) return fail(b, *why = "the formula " + b->row->formula + " has no value for this entry");
        const Value v = registry::evaluate(b->rows, Value::integer(std::llround(kiln)), b->spec);
        if (!in_range(b->spec, v, why)) return fail(b, *why);
        if (!registry::formulas::write(b->row->formula, v.as_double(), in, c, &game))
            return fail(b, *why = "the formula " + b->row->formula + " gives no game value for " + v.text_of());
        KilnContentValue kv{};
        kv.type = KILN_CV_FLOAT, kv.f = game;
        if (!encode_kiln(fs[0], kv, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    if (!put(b, index_of(b->kind, inputs[0]), -1, base + fs[0].offset, fs[0].bytes, std::move(ev)))
        return (void)fail(b, "the field is not readable");
    std::vector<LiveSlot> slots;
    for (const Field& f : fs) slots.push_back({base + f.offset, f.bytes});
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, std::move(slots),
        [fs, formula = b->row->formula, c = constants()](const std::vector<SlotBytes>& bytes, std::vector<double>* effective) -> std::optional<Value> {
            std::vector<double> in;
            for (size_t i = 0; i < fs.size(); ++i) in.push_back(as_double(decode(fs[i], bytes[i].data())));
            double result = 0;
            if (!registry::formulas::read(formula, in, c, &result)) return std::nullopt;
            if (effective) *effective = std::move(in);
            return Value::integer(std::llround(result));
        }});
}

// ---- recipe lists: inputs, outputs ----
Value make_struct(std::vector<std::pair<std::string, Value>> kv) {
    std::sort(kv.begin(), kv.end(), [](auto& a, auto& b) { return a.first < b.first; });
    Value x;
    x.kind = Value::Struct;
    for (auto& [k, v] : kv) x.keys.push_back(k), x.list.push_back(std::move(v));
    return x;
}
// A game id as a Kiln id (an id the stage's table does not have is kept as "#<id>", so it goes back unchanged).
Value id_value(const std::string& registry, uint32_t num) {
    auto it = g.kiln_of.find(num_key(registry, num));
    return Value::text(it != g.kiln_of.end() ? it->second : "#" + std::to_string(num));
}
bool id_num(const std::string& registry, const Value& v, uint32_t* num) {
    if (v.kind != Value::Text || v.t.empty()) return false;
    if (v.t[0] == '#') return (*num = static_cast<uint32_t>(std::strtoul(v.t.c_str() + 1, nullptr, 10))) != 0;
    if (auto it = g.in->nums.find(registry + "|" + v.t); it != g.in->nums.end()) return *num = it->second, true;
    const Entry* e = find_entry(registry, v.t, nullptr);  // an entry a mod added: the id kiln.content gave its GUID
    if (!e) return false;
    *num = e->id;
    return true;
}

bool read_list(uintptr_t header, const uint8_t* hb, uint32_t which, std::vector<KilnContentStack>* out) {
    int32_t off = 0;
    uint32_t n = 0;
    std::memcpy(&off, hb, 4);
    std::memcpy(&n, hb + 4, 4);
    if (n > 256) return false;
    out->clear();
    const uint32_t stride = which == KILN_CONTENT_INPUTS ? RL.in_stride : RL.out_stride;
    for (uint32_t q = 0; q < n; ++q) {
        const uintptr_t p = header + static_cast<intptr_t>(off) + static_cast<uintptr_t>(q) * stride;
        KilnContentStack x{};
        if (which == KILN_CONTENT_INPUTS) {
            uint32_t ccount = 0;
            if (!rdv(p + RL.in_item, x.item) || !rdv(p + RL.in_count, x.count) || !rdv(p + RL.in_cat, x.category) || !rdv(p + RL.in_cat_count, ccount))
                return false;
            if (!x.item) x.count = ccount;
        } else if (!rdv(p + RL.out_item, x.item) || !rdv(p + RL.out_count, x.count)) {
            return false;
        }
        out->push_back(x);
    }
    return true;
}

Value list_value(const std::vector<KilnContentStack>& st, bool inputs) {
    std::vector<Value> l;
    for (const KilnContentStack& s : st) {
        if (s.item || !inputs) l.push_back(make_struct({{"count", Value::integer(s.count)}, {"item", id_value("items", s.item)}}));
        else l.push_back(make_struct({{"category", id_value("inputCategories", s.category)}, {"count", Value::integer(s.count)}}));
    }
    return Value::of_list(std::move(l));
}

bool stacks_of(const Value& v, bool inputs, std::vector<KilnContentStack>* out, std::string* why) {
    if (v.kind != Value::List) return *why = "no list", false;
    out->clear();
    for (const Value& e : v.list) {
        KilnContentStack s{};
        s.chance = 1;
        const Value* item = e.leaf("item");
        const Value* cat = e.leaf("category");
        const Value* count = e.leaf("count");
        s.count = s.count_max = count && count->numeric() ? static_cast<uint32_t>(std::llround(count->as_double())) : 1;
        if (item && item->kind == Value::Text) {
            if (!id_num("items", *item, &s.item)) return *why = "the item " + item->t + " is not in this world", false;
        } else if (inputs && cat && cat->kind == Value::Text) {
            if (!id_num("inputCategories", *cat, &s.category)) return *why = "the input category " + cat->t + " is not known on this game build", false;
        } else {
            return *why = inputs ? "an input needs an item or a category" : "an output needs an item", false;
        }
        out->push_back(s);
    }
    return true;
}

bool same_stacks(const std::vector<KilnContentStack>& x, const std::vector<KilnContentStack>& y) {
    if (x.size() != y.size()) return false;
    for (size_t i = 0; i < x.size(); ++i)
        if (x[i].item != y[i].item || x[i].category != y[i].category || x[i].count != y[i].count) return false;
    return true;
}

void bind_recipe_list(const std::shared_ptr<Binding>& b, uint32_t which) {
    struct Cache {
        std::vector<KilnContentStack> stacks;
        uint8_t hb[8] = {};
        bool have = false;
    };
    auto cache = std::make_shared<Cache>();
    const uintptr_t header = b->entry.addr + (which == KILN_CONTENT_INPUTS ? RL.input : RL.output);
    LayerEval ev = [b, cache, header, which](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        std::vector<KilnContentStack> now;
        if (!read_list(header, below, which, &now)) return fail(b, *why = "the list below is not readable");
        const bool inputs = which == KILN_CONTENT_INPUTS;
        const Value v = registry::evaluate(b->rows, list_value(now, inputs), b->spec);
        std::vector<KilnContentStack> want;
        if (!stacks_of(v, inputs, &want, why)) return fail(b, *why);
        if (same_stacks(want, now)) {  // nothing changes over this value: the value below stays
            std::memcpy(out, below, 8);
            return true;
        }
        if (cache->have && same_stacks(want, cache->stacks)) {  // the list of last time: the same memory (near memory is never freed)
            std::memcpy(out, cache->hb, 8);
            return true;
        }
        if (recipe_list_header(b->entry, which, want, out, why) != KILN_OK) return fail(b, *why);
        cache->stacks = want, cache->have = true;
        std::memcpy(cache->hb, out, 8);
        return true;
    };
    if (!put(b, -1, static_cast<int32_t>(which), header, 8, std::move(ev)))
        return (void)fail(b, "the recipe is not readable");
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, {{header, 8}},
        [header, which](const std::vector<SlotBytes>& bytes, std::vector<double>*) -> std::optional<Value> {
            std::vector<KilnContentStack> stacks;
            if (!read_list(header, bytes[0].data(), which, &stacks)) return std::nullopt;
            return list_value(stacks, which == KILN_CONTENT_INPUTS);
        }});
}

// ---- drop tables: the leaves of each stack (entries[i].count.min, ...); a table keeps its stacks ----
struct DropLeaf {
    const char* leaf;   // the element's leaf name
    const char* field;  // kiln.content's drops field
};
constexpr DropLeaf kDropLeaves[] = {{"chance", "spawnProbability"}, {"count.max", "countMax"}, {"count.min", "countMin"}, {"weight", "weight"}};

template <class ReadLeaf>
Value drop_value(const std::vector<DropStackRef>& refs, const std::vector<Field>& fields, ReadLeaf read_leaf) {
    std::vector<Value> list;
    for (size_t i = 0; i < refs.size(); ++i) {
        uint32_t item = 0;
        rdv(refs[i].addr + DL.s_item, item);
        std::vector<std::pair<std::string, Value>> entry{{"item", id_value("items", item)}};
        for (size_t k = 0; k < fields.size(); ++k) {
            const Field& f = fields[k];
            const KilnContentValue cv = decode(f, read_leaf(i, k).data());
            entry.emplace_back(kDropLeaves[k].leaf, f.vt == KILN_CV_FLOAT ? Value::number(cv.f) : Value::integer(cv.i));
        }
        list.push_back(make_struct(std::move(entry)));
    }
    return Value::of_list(std::move(list));
}

void bind_drops(const std::shared_ptr<Binding>& b) {
    const Schema& sc = reg(KILN_CONTENT_DROPS).schema;
    std::vector<DropStackRef> refs;
    walk_drop_table(b->entry.addr, &refs);
    std::vector<const Field*> fields;
    for (const DropLeaf& l : kDropLeaves) {
        const Field* f = sc.find(l.field);
        if (!f) return (void)fail(b, std::string("the drop-table member ") + l.field + " is missing on this game build");
        fields.push_back(f);
    }
    std::vector<Field> fs;
    for (const Field* f : fields) fs.push_back(*f);
    // the whole list's value, the slot being evaluated taken from its own value below
    auto list_at = [refs, fs](size_t mine_stack, size_t mine_leaf, const uint8_t* below) {
        return drop_value(refs, fs, [&](size_t j, size_t k) {
            SlotBytes bytes{};
            if (j == mine_stack && k == mine_leaf) std::memcpy(bytes.data(), below, fs[k].bytes);
            else store_below_registry(refs[j].addr + fs[k].offset, fs[k].bytes, bytes.data());
            return bytes;
        });
    };
    const registry::RegistrySpec* drops = g.in->schema->registry("drops");
    for (size_t i = 0; i < refs.size(); ++i)
        for (size_t k = 0; k < fs.size(); ++k) {
            const FieldSpec* leaf_spec = drops ? drops->field(std::string("entries[].") + kDropLeaves[k].leaf) : nullptr;
            const FieldSpec lspec = leaf_spec ? *leaf_spec : FieldSpec{};
            const Field fk = fs[k];
            LayerEval ev = [b, list_at, i, k, fk, lspec](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
                const Value before = list_at(i, k, below);
                const Value v = registry::evaluate(b->rows, before, b->spec);
                if (v.kind != Value::List || v.list.size() != before.list.size())
                    return fail(b, *why = "a drop table keeps its stacks (only their counts, chances and weights change)");
                const Value* x = v.list[i].leaf(kDropLeaves[k].leaf);
                if (!x) return fail(b, *why = std::string("no ") + kDropLeaves[k].leaf);
                if (!in_range(lspec, *x, why)) return fail(b, *why);
                KilnContentValue kv{};
                if (fk.vt == KILN_CV_FLOAT) kv.type = KILN_CV_FLOAT, kv.f = x->as_double();
                else kv.type = KILN_CV_INT, kv.i = std::llround(x->as_double());
                if (!encode_kiln(fk, kv, out, why, kiln_field_name(b->ref) + "[]." + kDropLeaves[k].leaf)) return fail(b, *why);
                return true;
            };
            const uintptr_t at = refs[i].addr + fields[k]->offset;
            if (!put(b, index_of(KILN_CONTENT_DROPS, fields[k]), static_cast<int32_t>(i), at, fields[k]->bytes, std::move(ev)))
                fail(b, "a stack of the table is not readable");
            else
                if (!g.reading) g.table_addrs[lower(guid_text(b->entry.guid))].push_back(at);
        }
    const std::string tg = lower(guid_text(b->entry.guid));
    std::vector<registry::FieldRef> read_only_fields;
    auto& tf = g.reading ? read_only_fields : g.table_fields[tg];
    if (std::find(tf.begin(), tf.end(), b->ref) == tf.end()) tf.push_back(b->ref);
    std::vector<LiveSlot> slots;
    for (const DropStackRef& ref : refs)
        for (const Field& f : fs) slots.push_back({ref.addr + f.offset, f.bytes});
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, std::move(slots),
        [refs, fs](const std::vector<SlotBytes>& bytes, std::vector<double>*) {
            return drop_value(refs, fs, [&](size_t i, size_t k) -> const SlotBytes& { return bytes[i * fs.size() + k]; });
        }});
}

// ---- perks (refList): five slots of a reference and an id, kept in step ----
bool zero_bytes(const uint8_t* p, size_t n) {
    for (size_t i = 0; i < n; ++i)
        if (p[i]) return false;
    return true;
}
Value perk_value(const uint8_t bytes[16]) {
    const std::string guid = lower(guid_text(bytes));
    auto it = g.kiln_by_guid.find("perks|" + guid);
    return Value::text(it != g.kiln_by_guid.end() ? it->second : "#guid:" + guid);
}
bool perk_guid(const Value& v, uint8_t out[16]) {
    if (v.kind != Value::Text) return false;
    if (v.t.rfind("#guid:", 0) == 0) return guid_parse(v.t.substr(6), out);
    auto it = g.in->ids.find("perks|" + v.t);
    return it != g.in->ids.end() && guid_parse(it->second, out);
}

void bind_perks(const std::shared_ptr<Binding>& b) {
    const Schema& sc = binding_schema(KILN_CONTENT_ITEMS);
    std::vector<Field> refs, ids;
    for (int i = 0; i < 5; ++i) {
        const Field* r = sc.find("perkReferences[" + std::to_string(i) + "]");
        const Field* d = sc.find("perkIds[" + std::to_string(i) + "]");
        if (!r || !d || r->bytes != 16 || d->bytes != 4) {
            // Kiln words in the refusal, the game's members in kiln.log at debug level (registry-fixes item 5, P45)
            LOGD("registry (live): %s: perk slot %d: perkReferences[%d] / perkIds[%d] missing or not 16 / 4 bytes", b->ref.text().c_str(), i, i, i);
            return (void)fail(b, "this game build's items have no perk slots Kiln can write (Kiln's mapping of them needs an update)");
        }
        refs.push_back(*r), ids.push_back(*d);
    }
    const uintptr_t base = b->entry.addr;
    // the perks below the registry, slot i's reference given as below when it is the slot evaluated
    auto result = [b, refs, ids, base](int ref_slot, const uint8_t* below, std::vector<Value>* out, std::string* why) {
        std::vector<Value> l;
        for (int i = 0; i < 5; ++i) {
            uint8_t gb[16] = {};
            if (i == ref_slot) std::memcpy(gb, below, 16);
            else store_below_registry(base + refs[i].offset, 16, gb);
            if (!zero_bytes(gb, 16)) l.push_back(perk_value(gb));
        }
        const Value v = registry::evaluate(b->rows, Value::of_list(std::move(l)), b->spec);
        if (v.kind != Value::List || v.list.size() > 5) return *why = "an item has 5 perk slots", false;
        *out = v.list;
        return true;
    };
    for (int i = 0; i < 5; ++i) {
        LayerEval ref_ev = [b, result, i](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
            std::vector<Value> l;
            if (!result(i, below, &l, why)) return fail(b, *why);
            uint8_t gb[16] = {};
            if (static_cast<size_t>(i) < l.size() && !perk_guid(l[i], gb)) return fail(b, *why = "the perk " + l[i].t + " is not known on this game build");
            std::memcpy(out, gb, 16);
            return true;
        };
        const Field rf = refs[i];
        LayerEval id_ev = [b, result, i, rf, base](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
            uint8_t own[16] = {};
            store_below_registry(base + rf.offset, 16, own);
            std::vector<Value> l;
            if (!result(i, own, &l, why)) return fail(b, *why);
            uint32_t id = 0;
            if (static_cast<size_t>(i) < l.size()) {
                uint8_t gb[16] = {};
                if (!perk_guid(l[i], gb)) return fail(b, *why = "the perk " + l[i].t + " is not known on this game build");
                if (std::memcmp(gb, own, 16) == 0) std::memcpy(&id, below, 4);  // the same perk as below: its id as it is
                else if (auto it = g.in->nums.find("perks|" + l[i].t); it != g.in->nums.end() && it->second) id = it->second;
                else id = fnv1a(gb, 16);
            }
            std::memcpy(out, &id, 4);
            return true;
        };
        put(b, index_of(KILN_CONTENT_ITEMS, sc.find(refs[i].path)), -1, base + refs[i].offset, 16, std::move(ref_ev));
        put(b, index_of(KILN_CONTENT_ITEMS, sc.find(ids[i].path)), -1, base + ids[i].offset, 4, std::move(id_ev));
    }
    std::vector<LiveSlot> slots;
    for (const Field& f : refs) slots.push_back({base + f.offset, f.bytes});
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, std::move(slots),
        [](const std::vector<SlotBytes>& bytes, std::vector<double>*) {
            std::vector<Value> list;
            for (const SlotBytes& ref : bytes)
                if (!zero_bytes(ref.data(), 16)) list.push_back(perk_value(ref.data()));
            return Value::of_list(std::move(list));
        }});
}

// ---- impactConfig (staminaCost, manaCost): kiln.gamedata's item config value, its tooltip row following ----
std::string config_path(const std::string& config) {  // Kiln's config names as the game's config ids (as the model reads them)
    static const std::map<std::string, std::string> ids = {{"StaminaCost", "0x727d7f78"}, {"ManaCost", "0xaf236ece"}};
    const auto it = ids.find(config);
    return it == ids.end() ? config : it->second;
}

void bind_impact(const std::shared_ptr<Binding>& b, const std::string& path) {
    GdValueSlot s;
    std::string why;
    if (!gd_item_config_slot(b->entry, path, &s, &why)) return (void)fail(b, why);
    const Field vf = s.f;
    LayerEval ev = [b, vf](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const Value v = registry::evaluate(b->rows, to_kiln(vf, nullptr, b->spec, below), b->spec);
        if (vf.vt == KILN_CV_INT && v.numeric() && std::isfinite(v.as_double()) && std::trunc(v.as_double()) != v.as_double())
            return fail(b, *why = std::string(kNotExact) + b->ref.registry + "." + b->ref.field + " of " + b->ref.id +
                " is a whole number on this build; " + v.text_of() + " was not written");
        if (!in_range(b->spec, v, why) || !to_game(vf, nullptr, b->spec, v, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    if (!put_at(b, s.kind, s.id, s.field, -1, s.addr, vf.bytes, std::move(ev))) return (void)fail(b, "the config value is not readable");
    track_scalar(b, s.addr, vf);
    if (!s.tip) return;
    const uintptr_t at = s.addr;
    const uint8_t format = s.tip_format;
    LayerEval tip = [b, vf, at, format](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        uint8_t now[kSlotBytes] = {}, under[kSlotBytes] = {};
        store_effective(at, vf.bytes, now);
        store_below_registry(at, vf.bytes, under);
        if (std::memcmp(now, under, vf.bytes) == 0) {  // the registry changes nothing here: the row as it is below
            std::memcpy(out, below, 4);
            return true;
        }
        int64_t shown = 0;
        if (!gd_ui_scale(format, as_double(decode(vf, now)), &shown)) return *why = "the tooltip's format is unknown", false;
        const int32_t v32 = static_cast<int32_t>(std::clamp<int64_t>(shown, INT32_MIN, INT32_MAX));
        std::memcpy(out, &v32, 4);
        return true;
    };
    put_at(b, s.kind, s.id, -1, -1, s.tip, 4, std::move(tip));
}

// ---- unlock: "always" clears the recipe's knowledge requirement, "game" keeps the game's ----
void bind_unlock(const std::shared_ptr<Binding>& b) {
    const Schema& sc = reg(KILN_CONTENT_RECIPES).schema;
    const auto it = sc.special.find("knowledgeRequirement");
    const uint32_t size = g_registry_type_size("keen::KnowledgeQueryData");
    if (it == sc.special.end() || size == 0 || size > kSlotBytes) return (void)fail(b, "this game build's recipes have no knowledge requirement Kiln can write");
    const uintptr_t at = b->entry.addr + it->second;
    LayerEval ev = [b, at](const uint8_t* below, uint8_t n, uint8_t* out, std::string* why) {
        uint8_t orig[kSlotBytes] = {};
        if (!store_original(at, n, orig)) std::memcpy(orig, below, n);
        const Value v = registry::evaluate(b->rows, Value::text(zero_bytes(below, n) ? "always" : "game"), b->spec);
        if (v.kind != Value::Text || (v.t != "always" && v.t != "game")) return fail(b, *why = "unlock is game or always");
        if (v.t == "always") std::memset(out, 0, n);
        else std::memcpy(out, zero_bytes(below, n) ? orig : below, n);
        return true;
    };
    if (!put(b, -1, kSubUnlock, at, static_cast<uint8_t>(size), std::move(ev))) return (void)fail(b, "the recipe is not readable");
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, {{at, static_cast<uint8_t>(size)}},
        [size](const std::vector<SlotBytes>& bytes, std::vector<double>*) {
            return Value::text(zero_bytes(bytes[0].data(), size) ? "always" : "game");
        }});
}

// ---- one field ----
// A refusal of the mapping on this build: in Kiln words (the field and entry), the regmap path in kiln.log at debug level for
// maintainers, never in the refusal (owner's principle; registry-fixes item 5). The words keep the phrases the proof producer
// (RegistryProofTestMod.IsDisagreement) takes for a mapping that disagrees with the build: "is missing on this game build",
// "is not writable on this game build".
constexpr const char* kMappingUpdate = " (Kiln's mapping of it needs an update)";
void refuse_mapping(const registry::FieldRef& fr, const std::vector<const ChainRow*>& rows, const std::string& why, const std::string& member) {
    LOGD("registry (live): %s: %s (regmap path %s)", fr.text().c_str(), why.c_str(), member.c_str());
    refuse(fr, rows, why);
}

// ---- dev.32: gamedata-section registries (creatures, arsenals, perks, skills, buffs, abilities) ----
// A value of a gamedata entry: an evaluated layer on gamedata's slot of it (its store kind, so the content sync and the
// conflict report name it), compare-before-write and rollback as for every registry slot. A refusal names the Kiln field and
// entry; gamedata's own reason (game members) goes to kiln.log at debug level (P45).
bool section_slot(const std::shared_ptr<Binding>& b, uint32_t section, const std::vector<const ChainRow*>& rows, const std::string& path,
             GdValueSlot* s) {
    std::string why;
    if (gd_value_slot(section, b->entry, path, s, &why)) {
        if ((!g.reading || g.initializing_entry) && !s->f.writable) {
            refuse_mapping(b->ref, rows, "the game's value of this field is not writable on this game build", path);
            return false;
        }
        return true;
    }
    LOGD("registry (live): %s: %s (regmap path %s)", b->ref.text().c_str(), why.c_str(), path.c_str());
    if (s->absent) {  // this entry has none (a creature without a size of its own, a perk without a value): only it is refused
        if (g.reading) g.read_absent = true;  // a read of it is null, as the base reads it (registry_live_read)
        refuse(b->ref, rows, std::string("this ") + entry_word(b->ref.registry) + " has no value of this field (only some " + b->ref.registry + " have one)");
    } else
        refuse_mapping(b->ref, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, path);
    return false;
}

void bind_section_scalar(const std::shared_ptr<Binding>& b, const GdValueSlot& s) {
    const Field fc = s.f;
    LayerEval ev = [b, fc](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const rmap::Row* row = b->row ? &*b->row : nullptr;
        const Value under = to_kiln(fc, row, b->spec, below);
        const Value v = registry::evaluate(b->rows, under, b->spec);
        if (!in_range(b->spec, v, why) || !save_check(b, under, v, why) || !to_game(fc, row, b->spec, v, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    if (!put_at(b, s.kind, s.id, s.field, -1, s.addr, fc.bytes, std::move(ev))) return (void)fail(b, "the field is not readable");
    track_scalar(b, s.addr, fc);
}

// perks.value: the perk's one whole or fractional config (impactConfig "shown"), as bind_impact: a fraction on a whole-number
// config refused with not_exact, never rounded; its tooltip row follows the value the registry gives.
void bind_section_impact(const std::shared_ptr<Binding>& b, const GdValueSlot& s) {
    const Field vf = s.f;
    LayerEval ev = [b, vf](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        const Value v = registry::evaluate(b->rows, to_kiln(vf, nullptr, b->spec, below), b->spec);
        if (vf.vt == KILN_CV_INT && v.numeric() && std::isfinite(v.as_double()) && std::trunc(v.as_double()) != v.as_double())
            return fail(b, *why = std::string(kNotExact) + b->ref.registry + "." + b->ref.field + " of " + b->ref.id +
                " is a whole number on this build; " + v.text_of() + " was not written");
        if (!in_range(b->spec, v, why) || !to_game(vf, nullptr, b->spec, v, out, why, kiln_field_name(b->ref))) return fail(b, *why);
        return true;
    };
    if (!put_at(b, s.kind, s.id, s.field, -1, s.addr, vf.bytes, std::move(ev))) return (void)fail(b, "the value is not readable");
    track_scalar(b, s.addr, vf);
    if (!s.tip) return;
    const uintptr_t at = s.addr;
    const uint8_t format = s.tip_format;
    LayerEval tip = [vf, at, format](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
        uint8_t now[kSlotBytes] = {}, under[kSlotBytes] = {};
        store_effective(at, vf.bytes, now);
        store_below_registry(at, vf.bytes, under);
        if (std::memcmp(now, under, vf.bytes) == 0) {  // the registry changes nothing here: the row as it is below
            std::memcpy(out, below, 4);
            return true;
        }
        int64_t shown = 0;
        if (!gd_ui_scale(format, as_double(decode(vf, now)), &shown)) return *why = "the tooltip's format is unknown", false;
        const int32_t v32 = static_cast<int32_t>(std::clamp<int64_t>(shown, INT32_MIN, INT32_MAX));
        std::memcpy(out, &v32, 4);
        return true;
    };
    put_at(b, s.kind, s.id, -1, -1, s.tip, 4, std::move(tip));
}

// arsenals.attacks: a list of structs whose leaves are the element rows ("attacks[].chance"); the list keeps its elements
// (an arsenal's attacks are not added or removed live), each leaf a slot of its own.
void bind_section_list(const std::shared_ptr<Binding>& b, uint32_t section, const rmap::RegistryMap& rm, const std::vector<const ChainRow*>& rows) {
    const rmap::Row& row = *b->row;
    const std::string prefix = b->ref.field + "[].";
    struct Leaf {
        std::string name, path;
        FieldSpec spec;
    };
    std::vector<Leaf> leaves;
    const registry::RegistrySpec* rs = g.in->schema->registry(b->ref.registry);
    for (const auto& [fname, r] : rm.rows)
        if (fname.rfind(prefix, 0) == 0 && !r.paths.empty() && r.backend == rmap::Backend::Live && rs && rs->field(fname))
            leaves.push_back({fname.substr(prefix.size()), r.paths[0], *rs->field(fname)});
    uint32_t n = 0;
    std::string why;
    if (leaves.empty()) return refuse(b->ref, rows, "no field of its elements is mapped on this game build");
    if (!gd_list_size(section, b->entry, row.paths[0], &n, &why)) {
        LOGD("registry (live): %s: %s", b->ref.text().c_str(), why.c_str());
        return refuse_mapping(b->ref, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, row.paths[0]);
    }
    auto all = std::make_shared<std::vector<std::vector<GdValueSlot>>>(n, std::vector<GdValueSlot>(leaves.size()));
    for (uint32_t i = 0; i < n; ++i)
        for (size_t k = 0; k < leaves.size(); ++k)
            if (!section_slot(b, section, rows, row.paths[0] + "[" + std::to_string(i) + "]." + leaves[k].path, &(*all)[i][k])) return;
    const std::shared_ptr<const std::vector<std::vector<GdValueSlot>>> slots = all;  // shared by every slot's layer, never copied per slot
    const auto leaf_list = std::make_shared<const std::vector<Leaf>>(leaves);
    // the whole list's value, the slot being evaluated taken from its own value below
    auto list_at = [slots, leaf_list](size_t mine_i, size_t mine_k, const uint8_t* below) {
        std::vector<Value> list;
        for (size_t i = 0; i < slots->size(); ++i) {
            std::vector<std::pair<std::string, Value>> el;
            for (size_t k = 0; k < leaf_list->size(); ++k) {
                const GdValueSlot& sk = (*slots)[i][k];
                SlotBytes bytes{};
                if (i == mine_i && k == mine_k) std::memcpy(bytes.data(), below, sk.f.bytes);
                else store_below_registry(sk.addr, sk.f.bytes, bytes.data());
                el.emplace_back((*leaf_list)[k].name, to_kiln(sk.f, nullptr, (*leaf_list)[k].spec, bytes.data()));
            }
            list.push_back(make_struct(std::move(el)));
        }
        return Value::of_list(std::move(list));
    };
    for (size_t i = 0; i < slots->size(); ++i)
        for (size_t k = 0; k < leaves.size(); ++k) {
            const GdValueSlot& s = (*slots)[i][k];
            const Leaf lf = leaves[k];
            LayerEval ev = [b, list_at, i, k, s, lf](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
                const Value before = list_at(i, k, below);
                const Value v = registry::evaluate(b->rows, before, b->spec);
                if (v.kind != Value::List || v.list.size() != before.list.size())
                    return fail(b, *why = std::string("an ") + entry_word(b->ref.registry) + " keeps its " + b->ref.field + " (only their values change)");
                const Value* x = v.list[i].leaf(lf.name);
                if (!x) return fail(b, *why = "no " + lf.name);
                if (!in_range(lf.spec, *x, why) || !to_game(s.f, nullptr, lf.spec, *x, out, why, kiln_field_name(b->ref) + "[]." + lf.name)) return fail(b, *why);
                return true;
            };
            if (!put_at(b, s.kind, s.id, s.field, static_cast<int32_t>(i), s.addr, s.f.bytes, std::move(ev))) return (void)fail(b, "an element is not readable");
        }
    std::vector<LiveSlot> live;
    std::vector<Field> fs;
    for (const auto& el : *slots)
        for (const GdValueSlot& s : el) live.push_back({s.addr, s.f.bytes}), fs.push_back(s.f);
    std::vector<FieldSpec> specs;
    std::vector<std::string> names;
    for (const Leaf& l : leaves) specs.push_back(l.spec), names.push_back(l.name);
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, std::move(live),
        [fs, specs, names](const std::vector<SlotBytes>& bytes, std::vector<double>*) -> std::optional<Value> {
            std::vector<Value> list;
            for (size_t i = 0; names.size() && i < bytes.size() / names.size(); ++i) {
                std::vector<std::pair<std::string, Value>> el;
                for (size_t k = 0; k < names.size(); ++k) el.emplace_back(names[k], to_kiln(fs[i * names.size() + k], nullptr, specs[k], bytes[i * names.size() + k].data()));
                list.push_back(make_struct(std::move(el)));
            }
            return Value::of_list(std::move(list));
        }});
}

// eventTime (abilities.timing.*): the first event of a kind is the field's value; every event of that kind at the same time in the
// game's data takes the value the registry gives the first, evaluated over the first's value below the registry (one value for the
// moment, so a relative edit moves them together and an event another writer moved below the registry joins them again).
void bind_event_time(const std::shared_ptr<Binding>& b, const std::vector<GdValueSlot>& slots) {
    const Field fa = slots[0].f;
    const uintptr_t anchor = slots[0].addr;
    for (size_t i = 0; i < slots.size(); ++i) {
        const GdValueSlot s = slots[i];
        LayerEval ev = [b, fa, anchor, s, i](const uint8_t* below, uint8_t, uint8_t* out, std::string* why) {
            uint8_t under_b[kSlotBytes] = {};
            if (i == 0) std::memcpy(under_b, below, fa.bytes);
            else if (!store_below_registry(anchor, fa.bytes, under_b)) return fail(b, *why = "the ability's first such moment is not readable");
            const Value under = to_kiln(fa, nullptr, b->spec, under_b);
            const Value v = registry::evaluate(b->rows, under, b->spec);
            if (!in_range(b->spec, v, why) || !to_game(s.f, nullptr, b->spec, v, out, why, kiln_field_name(b->ref))) return fail(b, *why);
            return true;
        };
        if (!put_at(b, s.kind, s.id, s.field, -1, s.addr, s.f.bytes, std::move(ev))) return (void)fail(b, "the field is not readable");
    }
    // every event of the moment is the field's slot (the first is its value): a failed write or another writer on any of them is
    // this field's and its change's (P17, P44), named by the Kiln field (P32), and the later-writes look watches them all
    std::vector<LiveSlot> all;
    for (const GdValueSlot& s : slots) all.push_back({s.addr, s.f.bytes});
    fields_target().insert_or_assign(b->ref, LiveField{b->spec, std::move(all),
        [b, fa](const std::vector<SlotBytes>& bytes, std::vector<double>*) -> std::optional<Value> { return to_kiln(fa, nullptr, b->spec, bytes[0].data()); }});
}

// One field of a gamedata-section registry's entry: the entry by its GUID in this world, then the row's conversion.
void bind_section(const std::shared_ptr<Binding>& b, const registry::FieldRef& fr, const registry::FieldOutcome& fo,
             const std::vector<const ChainRow*>& rows, bool raw, uint32_t section) {
    // by its GUID; an element entry without one (a skill node of the skill tree's array) by its 32-bit id
    std::string guid;
    uint32_t num = 0;
    if (auto it = g.in->ids.find(fr.registry + "|" + fr.id); it != g.in->ids.end()) guid = lower(it->second);
    if (auto it = g.in->nums.find(fr.registry + "|" + fr.id); it != g.in->nums.end()) num = it->second;
    if ((guid.empty() && !num) || !gd_section_entry(section, guid, num, &b->entry)) return refuse(fr, rows, "the entry is not in this world");
    GdValueSlot s;
    if (raw) {  // the Advanced tier: a member path below the entry's root, as kiln.gamedata names it
        if (!section_slot(b, section, rows, fr.field.substr(4), &s)) return;
        if (!s.f.writable && (!g.reading || g.initializing_entry)) return refuse(fr, rows, "the member " + fo.raw + " is not a writable member on this game build");
        return bind_section_scalar(b, s);
    }
    std::string reason;
    if (g.in->regmap->mapping(fr.registry, fr.field, &reason) != rmap::Mapping::Mapped) return refuse(fr, rows, reason);
    const auto rm = g.in->regmap->registries.find(fr.registry);
    if (rm == g.in->regmap->registries.end()) return refuse(fr, rows, "no regmap rows for this registry");
    const auto row = rm->second.rows.find(fr.field);
    if (row == rm->second.rows.end()) return refuse(fr, rows, "no regmap row");
    b->row = row->second;
    const rmap::Row& rw = row->second;
    if (rw.backend != rmap::Backend::Live) return refuse(fr, rows, "this field has no live back-end on this game build");
    const std::string member0 = rw.paths.empty() ? std::string("?") : rw.paths[0];
    if (rw.paths.empty()) return refuse_mapping(fr, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, member0);
    if (rw.conv == "int" || rw.conv == "float" || rw.conv == "bool" || rw.conv == "time" || rw.conv == "enum") {
        if (!section_slot(b, section, rows, rw.paths[0], &s)) return;
        if (s.f.ref || s.f.vt == KILN_CV_GUID)  // a GUID or a reference is no plain value: a mapping this build broke
            return refuse_mapping(fr, rows, std::string("the game's value of this field is missing on this game build: it is not a plain value there") + kMappingUpdate, member0);
        if (rw.conv == "enum" && (s.f.enum_kind != KILN_CONTENT_ENUM || !s.f.en))
            return refuse_mapping(fr, rows, std::string("the game's value of this field is not an enum on this game build") + kMappingUpdate, member0);
        return bind_section_scalar(b, s);
    }
    if (rw.conv == "impactConfig" && rw.config == registry::model::kShownConfig) {
        std::string path, why;
        if (!gd_shown_config(section, b->entry, rw.paths[0], &path, &why)) {
            LOGD("registry (live): %s: %s (regmap path %s)", fr.text().c_str(), why.c_str(), member0.c_str());
            if (why == "none" && g.reading) g.read_absent = true;
            if (why == "none" || why == "several")
                return refuse(fr, rows, std::string("this ") + entry_word(fr.registry) + (why == "none" ? " has no value" : " has more than one value Kiln could mean"));
            return refuse_mapping(fr, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, member0);
        }
        if (!section_slot(b, section, rows, path, &s)) return;
        return bind_section_impact(b, s);
    }
    if (rw.conv == "list") return bind_section_list(b, section, rm->second, rows);
    if (rw.conv == "eventTime") {  // abilities.timing.*: the first event of a kind and every event of that kind at its moment
        std::vector<GdValueSlot> slots;
        std::string why;
        if (!gd_event_slots(section, b->entry, rw.paths[0], &slots, &why)) {
            LOGD("registry (live): %s: %s (regmap path %s)", fr.text().c_str(), why.c_str(), member0.c_str());
            if (why == "none") {  // this ability has no such event: it alone is refused, and a read of it is null as its base reads it
                if (g.reading) g.read_absent = true;
                return refuse(fr, rows, std::string("this ") + entry_word(fr.registry) + " has no such moment on this game build");
            }
            return refuse_mapping(fr, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, member0);
        }
        for (const GdValueSlot& s2 : slots)
            if ((!g.reading || g.initializing_entry) && !s2.f.writable)
                return refuse_mapping(fr, rows, "the game's value of this field is not writable on this game build", member0);
        return bind_event_time(b, slots);
    }
    refuse(fr, rows, "the live back-end does not write " + rw.conv + " fields yet");
}

void bind(const registry::FieldRef& fr, const registry::FieldOutcome& fo) {
    const std::vector<const ChainRow*> rows = registry_rows(fo);
    if (rows.empty()) return;  // only older writers: nothing of the registry's
    const registry::RegistrySpec* rs = g.in->schema->registry(fr.registry);
    const bool raw = registry::is_raw_field(fr.field);
    const FieldSpec* spec = raw ? (fo.raw_spec ? &*fo.raw_spec : nullptr) : rs ? rs->field(fr.field) : nullptr;
    if (!spec) return refuse(fr, rows, "not a field of the schema");
    if (spec->applies == registry::Applies::Restart) return;  // the boot back-end's (S10)
    auto b = std::make_shared<Binding>();
    b->ref = fr;
    for (const ChainRow* r : rows) b->rows.push_back(*r);
    b->spec = *spec;
    b->kind = kind_of(fr.registry);
    if (const uint32_t section = b->kind ? 0 : gd_section_of(fr.registry)) return bind_section(b, fr, fo, rows, raw, section);
    if (!b->kind) return refuse(fr, rows, "this registry has no live back-end");
    std::string guid;
    const Entry* e = find_entry(fr.registry, fr.id, &guid);
    if (!e) {
        if (b->kind == KILN_CONTENT_DROPS && !guid.empty()) {  // the game loads drop tables as it needs them
            if (g.reading) g.read_reason = "the drop table has not loaded in this world";
            else g.pending_tables.push_back({fr, guid});
            return;
        }
        return refuse(fr, rows, "the entry is not in this world");
    }
    b->entry = *e;
    const Schema& sc = binding_schema(b->kind);
    if (raw) {
        const Field* f = sc.find(fr.field.substr(4));
        if (!f || ((!g.reading || g.initializing_entry) && !f->writable)) return refuse(fr, rows, "the member " + fo.raw + " is not a writable member on this game build");
        return bind_scalar(b, f);
    }
    std::string reason;
    if (g.in->regmap->mapping(fr.registry, fr.field, &reason) != rmap::Mapping::Mapped) return refuse(fr, rows, reason);
    const auto rm = g.in->regmap->registries.find(fr.registry);
    if (rm == g.in->regmap->registries.end()) return refuse(fr, rows, "no regmap rows for this registry");
    const auto row = rm->second.rows.find(fr.field);
    if (row == rm->second.rows.end()) return refuse(fr, rows, "no regmap row");
    b->row = row->second;
    const rmap::Row& rw = row->second;
    if (rw.backend != rmap::Backend::Live) return refuse(fr, rows, "this field has no live back-end on this game build");
    const std::string& conv = rw.conv;
    const std::string member0 = rw.paths.empty() ? std::string("?") : rw.paths[0];
    if (conv == "int" || conv == "float" || conv == "bool" || conv == "time" || conv == "enum") {
        const Field* f = rw.paths.empty() ? nullptr : sc.find(rw.paths[0]);
        // refl.cpp exposes a one-scalar struct (GemSlotOverride {isSet}) at its parent path.
        constexpr std::string_view flag = ".isSet";
        if (!f && conv == "bool" && !rw.paths.empty() && rw.paths[0].ends_with(flag)) {
            const Field* flat = sc.find(rw.paths[0].substr(0, rw.paths[0].size() - flag.size()));
            if (flat && flat->vt == KILN_CV_BOOL) f = flat;
        }
        if (!f) return refuse_mapping(fr, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, member0);
        if ((!g.reading || g.initializing_entry) && !f->writable)
            return refuse_mapping(fr, rows, "the game's value of this field is not writable on this game build", member0);
        return bind_scalar(b, f);
    }
    const bool writing = !g.reading || g.initializing_entry;
    if (conv == "flags") {  // a bitmask of the game's enum (loot.rarities)
        const Field* f = rw.paths.size() == 1 ? sc.find(rw.paths[0]) : nullptr;
        if (!f) return refuse_mapping(fr, rows, std::string("the game's value of this field is missing on this game build") + kMappingUpdate, member0);
        if (f->enum_kind != KILN_CONTENT_FLAGS || !f->en)
            return refuse_mapping(fr, rows, std::string("the game's value of this field is not a bitmask on this game build") + kMappingUpdate, member0);
        if (writing && !f->writable) return refuse_mapping(fr, rows, "the game's value of this field is read-only on this game build", member0);
        return bind_flags(b, f);
    }
    if (conv == "optionalEnum") {  // [the switch, the enum] (loot.fixedRarity)
        const Field* sw = rw.paths.size() == 2 ? sc.find(rw.paths[0]) : nullptr;
        const Field* en = rw.paths.size() == 2 ? sc.find(rw.paths[1]) : nullptr;
        const std::string both = rw.paths.size() == 2 ? rw.paths[0] + " and " + rw.paths[1] : member0;
        if (!sw || !en) return refuse_mapping(fr, rows, std::string("a game value of this field is missing on this game build") + kMappingUpdate, both);
        if (sw->vt != KILN_CV_BOOL || en->enum_kind != KILN_CONTENT_ENUM || !en->en)
            return refuse_mapping(fr, rows, std::string("the game's values of this field are not a switch and an enum on this game build") + kMappingUpdate, both);
        if (writing && (!sw->writable || !en->writable))
            return refuse_mapping(fr, rows, "a game value of this field is read-only on this game build", both);
        return bind_optional_enum(b, sw, en);
    }
    if (conv == "formula") {
        std::vector<const Field*> in;
        for (const std::string& p : rw.paths) {
            const Field* f = sc.find(p);
            if (!f) return refuse_mapping(fr, rows, std::string("a game value this field's formula reads is missing on this game build") + kMappingUpdate, p);
            in.push_back(f);
        }
        if (in.empty() || !registry::formulas::known(rw.formula)) return refuse(fr, rows, "unknown formula " + rw.formula);
        return bind_formula(b, in);
    }
    if (conv == "list" && b->kind == KILN_CONTENT_RECIPES && (fr.field == "inputs" || fr.field == "outputs"))
        return bind_recipe_list(b, fr.field == "inputs" ? KILN_CONTENT_INPUTS : KILN_CONTENT_OUTPUTS);
    if (conv == "inventoryWalk" && b->kind == KILN_CONTENT_DROPS) return bind_drops(b);
    if (conv == "refList" && b->kind == KILN_CONTENT_ITEMS && fr.field == "perks") return bind_perks(b);
    if (conv == "impactConfig" && b->kind == KILN_CONTENT_ITEMS && !rw.paths.empty()) return bind_impact(b, rw.paths[0] + "." + config_path(rw.config));
    if (conv == "unlock" && b->kind == KILN_CONTENT_RECIPES) return bind_unlock(b);
    refuse(fr, rows, "the live back-end does not write " + conv + " fields yet");
}

void put_all() {
    store_drop_owner(registry_owner());
    g.pending_tables.clear();
    g.table_fields.clear(), g.table_addrs.clear();
    g.fields.clear();
    g.ordinal = 0;
    for (const auto& [fr, fo] : g.in->result.fields) bind(fr, fo);
}

bool absolute_row(const ChainRow& r) {
    return r.list == registry::ListOp::None ? r.op.set : r.list == registry::ListOp::Clear || r.list == registry::ListOp::Replace;
}

std::string layer_mod_of(const Layer& l) {
    if (!l.mod.empty()) return l.mod;
    const std::string label = owner_label(l.owner);
    for (const char* p : {"pack ", "mod "})
        if (label.rfind(p, 0) == 0) return label.substr(std::strlen(p));
    return {};
}

// registry-followups 1: a code writer's row in Kiln words. kiln.content's own calls (set, set_stacks, set_ref, kiln.gamedata's
// values) describe their layer as "<owner's label>: <registry or section> <the entry's game name>.<the game's member or list>"
// (the rehearsal's "owner 67: items Material_T1_Wood_Logs.maxStackSize", EMF's big_stacks); the store and kiln.log's conflict
// lines keep that. The report names "<who>: <Kiln place>" instead: the mod the owner's label gives, else the core's id of the
// owner ("emf:big_stacks"; a Kiln.Host owner's "mod:<id>" is that mod), else the label. A source the mod chose itself
// (Content.Apply's own label, a file and the selector it wrote) is the mod's wording and stays.
void name_code_writer(ChainRow& row, const Layer& layer, const Slot& slot, const registry::FieldRef& ref) {
    const std::string label = owner_label(layer.owner);
    const std::string head = label + ": ";
    if (layer.source.rfind(head, 0) != 0) return;
    // the entry: a kiln.content slot carries its registry's kind; a kiln.gamedata value slot (an item's config value, which
    // items.manaCost and items.staminaCost write) carries a gamedata kind and the item's own id (review: its row kept the game's
    // item and member names)
    const uint32_t kind = slot.kind >= KILN_CONTENT_ITEMS && slot.kind <= KILN_CONTENT_DROPS ? slot.kind : kind_of(ref.registry);
    const Entry* e = kind ? reg(kind).get(slot.id) : nullptr;
    // dev.32: a gamedata-section registry's entry (a creature's template, an arsenal, a perk), by the Kiln id's GUID
    Entry gd_entry;
    if (!e && g.in)
        if (const uint32_t section = gd_section_of(ref.registry))
            if (const auto it = g.in->ids.find(ref.registry + "|" + ref.id);
                gd_section_entry(section, it != g.in->ids.end() ? lower(it->second) : std::string(), slot.id, &gd_entry) && gd_entry.id == slot.id)
                e = &gd_entry;
    const std::string rest = layer.source.substr(head.size());
    const size_t space = rest.find(' ');
    if (!e || e->name.empty() || space == std::string::npos || space == 0 || rest.compare(space + 1, e->name.size(), e->name) != 0 ||
        rest.size() <= space + 1 + e->name.size() + 1 || rest[space + 1 + e->name.size()] != '.')
        return;  // not kiln.content's own wording of this entry
    std::string who = row.mod;
    if (who.empty() && g.in && g.in->owner_name) {
        auto it = g.owner_names.find(layer.owner);
        if (it == g.owner_names.end()) {
            std::string id = g.in->owner_name(layer.owner);
            if (!id.empty()) it = g.owner_names.emplace(layer.owner, std::move(id)).first;
        }
        if (it != g.owner_names.end()) {
            who = it->second;
            if (who.rfind("mod:", 0) == 0 && who.size() > 4) row.mod = who = who.substr(4);
        }
    }
    if (who.empty()) who = label;
    row.who = who + ": " + ref.text();
}

// Replay the store's writes as whole field values. A pack or code call can write several slots
// of the same list or formula, so apply all its slots before decoding that writer's row.
void live_row(registry::Result& r, const registry::FieldRef& ref, const LiveField& field) {
    // kiln_wrote: what Kiln last wrote into each slot (the store's current bytes; the game's bytes where the store holds none)
    std::vector<SlotBytes> bytes(field.slots.size()), effective(field.slots.size()), kiln_wrote(field.slots.size());
    struct Write {
        size_t slot;
        SlotBytes bytes;
    };
    struct Writer {
        ChainRow row;
        std::vector<Write> writes;
    };
    std::map<std::tuple<uint64_t, KilnOwner, std::string>, Writer> writers;
    std::vector<Write> applied;
    bool blocked = false;
    bool late = false;  // the game's bytes of a slot are not what Kiln last wrote there, and no flush has seen it yet
    for (size_t i = 0; i < field.slots.size(); ++i) {
        const auto [addr, size] = field.slots[i];
        if (!rd(addr, effective[i].data(), size)) return;
        Slot slot;
        if (!store_slot_copy(addr, &slot) || slot.n != size) {
            bytes[i] = effective[i];
            kiln_wrote[i] = effective[i];
            continue;
        }
        std::memcpy(bytes[i].data(), slot.original, size);
        std::memcpy(kiln_wrote[i].data(), slot.current, size);
        late = late || (!slot.foreign && std::memcmp(effective[i].data(), slot.current, size) != 0);
        for (const Layer& layer : slot.layers) {
            Write write{i, {}};
            std::memcpy(write.bytes.data(), layer.bytes, size);
            if (is_registry_seq(layer.seq)) {
                if (slot.foreign) blocked = true;
                else applied.push_back(write);
                continue;
            }
            auto [it, added] = writers.try_emplace({layer.seq, layer.owner, layer.source});
            Writer& writer = it->second;
            if (added) {
                writer.row.kind = layer.seq < kRegistrySeq ? registry::WriterKind::OldPack : registry::WriterKind::Code;
                writer.row.mod = layer_mod_of(layer);
                writer.row.who = layer.source;
                if (writer.row.kind == registry::WriterKind::Code) name_code_writer(writer.row, layer, slot, ref);
            }
            writer.writes.push_back(write);
        }
    }
    // what kiln.content holds as the original of each slot (the game's value before any Kiln layer): what a data-pack op of code
    // was computed on (registry-followups 3)
    const auto original = field.decode(bytes, nullptr);
    std::vector<double> formula_inputs;
    auto result = field.decode(effective, &formula_inputs);
    if (!result) return;
    auto record = [&](Writer& writer) {
        for (const Write& write : writer.writes) bytes[write.slot] = write.bytes;
        auto value = field.decode(bytes, nullptr);
        if (!value) return false;
        ChainRow& row = writer.row;
        row.after = std::move(*value);
        if (row.after.kind == Value::List) {
            row.list = registry::ListOp::Replace;
            row.list_values = row.after.list;
        } else {
            row.op.set = true;
            row.op.value = row.after;
        }
        return true;
    };
    registry::FieldOutcome& fo = r.fields.at(ref);
    std::vector<ChainRow> chain;
    for (const ChainRow& c : fo.chain)
        if (c.kind != registry::WriterKind::Registry && c.kind != registry::WriterKind::Check && c.kind != registry::WriterKind::OldPack && c.kind != registry::WriterKind::Code) chain.push_back(c);
    // The drift probe (drift.h): the game's value just before Kiln's first write in this world (the slots' originals, before
    // any pack) against what the stage read and the writers Kiln knows below the packs (archive packages, old boot patches).
    // A difference no known writer explains is a best-effort row, never a refusal. An entry a mod added is Kiln's own clone;
    // an entry an old boot document wrote holds Kiln's own patch, which many of its paths have no row to explain (a formula's
    // input, an element of a list of structs, "[*]" over a registry's elements): never another loader's change there.
    bool drift = false;
    std::string drift_was, drift_now;
    const registry::EntryRef entry{ref.registry, ref.id};
    const bool added = std::find(r.added.begin(), r.added.end(), entry) != r.added.end();
    if (!added && fo.base.kind != Value::None && !g.boot_written.count(entry)) {
        const Value& expected = chain.empty() ? fo.base : chain.back().after;
        if (auto observed = field.decode(bytes, nullptr); observed && !(*observed == expected)) {
            ChainRow row;
            row.kind = registry::WriterKind::Drift;
            row.who = registry::drift_who(g.suspects);
            if (observed->kind == Value::List) row.list = registry::ListOp::Replace, row.list_values = observed->list;
            else row.op.set = true, row.op.value = *observed;
            row.after = std::move(*observed);
            drift_was = registry::report_value_text(expected);
            drift_now = registry::report_value_text(row.after);
            drift = true;
            chain.push_back(std::move(row));
        }
    }
    auto next = writers.begin();
    const ChainRow* pack = nullptr;
    for (; next != writers.end() && next->second.row.kind == registry::WriterKind::OldPack; ++next) {
        if (record(next->second)) {
            pack = &next->second.row;
            chain.push_back(*pack);
        }
    }
    auto below = field.decode(bytes, nullptr);
    const bool skipped = blocked && applied.empty();
    const ChainRow* first_reg = nullptr;
    if (!skipped && below) {
        Value v = *below;
        for (const ChainRow& c : fo.chain)
            if (c.kind == registry::WriterKind::Registry || c.kind == registry::WriterKind::Check) {
                if (!first_reg) first_reg = &c;
                v = registry::evaluate({c}, v, field.spec);
                chain.push_back(c);
                chain.back().after = v;
            }
        for (const Write& write : applied) bytes[write.slot] = write.bytes;
        if (first_reg) {
            if (auto after = field.decode(bytes, nullptr)) chain.back().after = std::move(*after);
            if (pack && pack->mod != first_reg->mod && !(*below == fo.base)) {
                const std::string place = ref.text(), was = registry::report_value_text(*below), now = registry::report_value_text(chain.back().after);
                registry::Line line;
                line.field = ref, line.winner = first_reg->mod, line.loser = pack->who;
                if (absolute_row(*first_reg)) {
                    line.kind = registry::LineKind::Overridden;
                    line.text = place + ": " + first_reg->mod + " sets " + now + " over " + pack->who + "'s " + was + " (the later one wins)";
                } else {
                    line.kind = registry::LineKind::ComposedOn;
                    line.text = place + ": " + first_reg->mod + "'s change composed on " + pack->who + "'s value: " + was + " -> " + now;
                }
                r.lines.push_back(std::move(line));
            }
            if (drift) {
                const std::string place = ref.text(), now = registry::report_value_text(chain.back().after);
                registry::Line line;
                line.field = ref, line.winner = first_reg->mod, line.loser = registry::drift_who(g.suspects);
                line.certainty = registry::Certainty::BestEffort;
                line.kind = absolute_row(*first_reg) ? registry::LineKind::Overridden : registry::LineKind::ComposedOn;
                line.text = place + ": the game's value changed outside Kiln after the stage read it (" + drift_was + " -> " + drift_now +
                            " before Kiln's first write in this world) and no writer Kiln knows explains it; " + line.loser + ". " +
                            first_reg->mod + "'s edit applies over the game's value: " + now;
                r.lines.push_back(std::move(line));
            }
        }
    }
    if (skipped) fo.winner = pack && below && *result == *below ? pack->who : "";
    // the registry's value, before any code layer above it (registry-followups 3)
    std::optional<Value> reg_value;
    std::string reg_mod;
    if (first_reg && !skipped && !chain.empty()) {
        reg_value = chain.back().after;
        for (auto it = chain.rbegin(); it != chain.rend() && reg_mod.empty(); ++it)
            if (it->kind == registry::WriterKind::Registry) reg_mod = it->mod;
    }
    const ChainRow* code_top = nullptr;
    for (; next != writers.end(); ++next) {
        if (record(next->second)) {
            fo.winner = next->second.row.who;
            chain.push_back(std::move(next->second.row));
            code_top = &chain.back();
        }
    }
    // registry-followups 3: a Content.* change made by code sits above registry edits and wins (D1; services/registry.md), and
    // kiln.content computes a data-pack op it was given (mul, add, raiseOnly, ...) on the game's original value, never on what
    // the registry made of it (services/content.md). That is how BigStacks' x4 raiseOnly gave wood logs 1000 over a registry
    // pack's 2000; it stays so (what older mod sets get must not change). The report says it: one certain line.
    // Review: the value the code layer gives (its row's), never the game's value now: a write outside Kiln after it (the late
    // look's best-effort row below) is not the code's, and code that kept the registry's value replaced nothing.
    if (code_top && reg_value && !(code_top->after == *reg_value)) {
        registry::Line line;
        line.field = ref, line.winner = code_top->who, line.loser = reg_mod, line.kind = registry::LineKind::Overridden;
        line.text = ref.text() + ": " + code_top->who + " (a Content change made by code) sets " + registry::report_value_text(code_top->after) +
                    " over " + reg_mod + "'s " + registry::report_value_text(*reg_value) +
                    " (code wins over registry edits; a relative change made through Content is computed on " +
                    (added ? std::string("the entry's own value as its mod added it, ") : std::string("the game's value, ")) +
                    registry::report_value_text(original ? *original : fo.base) + ", not on the registry's)";
        r.lines.push_back(std::move(line));
    }
    // a slot another writer changed after Kiln's layer (the store's compare-before-write skipped Kiln's write): the same
    // best-effort row, last, with the value the game holds
    if (blocked) {
        ChainRow row;
        row.kind = registry::WriterKind::Drift;
        row.who = registry::drift_who(g.suspects);
        if (result->kind == Value::List) row.list = registry::ListOp::Replace, row.list_values = result->list;
        else row.op.set = true, row.op.value = *result;
        row.after = *result;
        if (fo.winner.empty() || skipped) fo.winner = row.who;
        chain.push_back(std::move(row));
    } else if (late) {
        // registry-followups 1: a write outside Kiln's store after Kiln's last write in this world (a DLL writing game memory),
        // seen by the later-writes look (late_check) before any flush of the slot has: the same best-effort row, last, and a
        // line. The game's value decides the result; nothing is refused or written back here.
        ChainRow row;
        row.kind = registry::WriterKind::Drift;
        row.who = registry::drift_who(g.suspects);
        if (result->kind == Value::List) row.list = registry::ListOp::Replace, row.list_values = result->list;
        else row.op.set = true, row.op.value = *result;
        row.after = *result;
        const auto wrote = field.decode(kiln_wrote, nullptr);
        registry::Line line;
        line.field = ref, line.winner = row.who, line.certainty = registry::Certainty::BestEffort, line.kind = registry::LineKind::Overridden;
        if (!chain.empty()) line.loser = chain.back().mod.empty() ? chain.back().who : chain.back().mod;
        line.text = ref.text() + ": the game's value changed outside Kiln after Kiln's last write in this world (" +
                    (wrote ? registry::report_value_text(*wrote) : std::string("?")) + " -> " + registry::report_value_text(*result) +
                    "), and no writer Kiln knows explains it; " + row.who;
        r.lines.push_back(std::move(line));
        fo.winner = row.who;
        chain.push_back(std::move(row));
    }
    fo.chain = std::move(chain);
    fo.result = std::move(*result);
    fo.formula_inputs = std::move(formula_inputs);
}

void late_seen();

// A write compare-before-write skipped, in Kiln words (P32): the store's own row names the game's debug and member
// names; the report names the Kiln place the slot's bound field had (slot_places, refreshed from g.fields per report).
std::string skipped_text(const StoreSkipped& s) {
    const auto it = g.slot_places.find(s.addr);
    const std::string place = it != g.slot_places.end() ? it->second : "a game memory slot Kiln wrote";
    return place + " was written by another writer after Kiln's layer: Kiln's " + (s.revert ? "revert" : "edit") + " not applied";
}
void report(bool application = true) {
    g.tables_changed = false;
    registry::Result r = g.in->result;
    g.boot_written.clear();
    for (const registry::OldWrite& w : g.in->old)
        if (w.kind == registry::WriterKind::OldPatch) g.boot_written.insert({w.field.registry, w.field.id});
    for (const auto& [ref, field] : g.fields) live_row(r, ref, field);
    if (g.world && g.in->record_entry) for (const auto& [entry, body] : g.in->entry_bodies) {
        auto fields = body.value("registryFields", registry::json::object());
        for (auto it = r.fields.lower_bound({entry.registry, entry.id, ""}); it != r.fields.end() && it->first.registry == entry.registry && it->first.id == entry.id; ++it)
            if (!registry::is_raw_field(it->first.field) && fields.contains(it->first.field)) fields[it->first.field] = it->second.result.to_json();
        g.in->record_entry(entry, fields);
    }
    const size_t first = r.refused.size();
    for (const registry::Refusal& x : g.refused) r.refused.push_back(x);
    const size_t live_end = r.refused.size();
    const std::vector<StoreSkipped> skipped = store_skipped();
    for (const auto& [ref, field] : g.fields)
        for (const LiveSlot& slot : field.slots) g.slot_places.insert_or_assign(slot.addr, ref.text());
    for (const StoreSkipped& s : skipped) {
        registry::Refusal x;
        x.mod = s.mod, x.source = s.source, x.reason = skipped_text(s);
        r.refused.push_back(std::move(x));
    }
    // this application's refusals, then the skipped writes found since the last report (each once per world)
    size_t logged = 0;
    const size_t from_skipped = live_end + std::min(g.skipped_logged, skipped.size());
    for (size_t i = first; i < r.refused.size() && logged < 50; ++i) {
        if (i >= live_end && i < from_skipped) continue;
        LOGW("registry (live): %s%s", r.refused[i].mod.empty() ? "" : (r.refused[i].mod + ": ").c_str(), r.refused[i].reason.c_str());
        ++logged;
    }
    const size_t to_log = (live_end - first) + (skipped.size() - std::min(g.skipped_logged, skipped.size()));
    if (to_log > 50) LOGW("registry (live): ... and %zu more (state/registry.json)", to_log - 50);
    g.skipped_logged = skipped.size();
    g.reported = r;
    g.have_report = true;
    g.pending = std::move(r);  // written by registry_live_publish, outside the store's lock (review finding 10)
    g.pending_set = true;
    g.pending_application |= application;
    late_seen();
}

void queue_change_report() {
    report(false);
    g.pending_change = true;
}

// registry-followups 1: writes made after the last report. registry.json's live rows are rebuilt at each application (world
// load, pack reload, a live change, a drop table bound later), so a write after the last one (another loader's code change
// through kiln.content, as EMF's set_item_max_stack makes; a DLL writing game memory) stayed out of it until the next. The
// tick's publish now looks again: once kiln.content's store generation moved (any Kiln-side write) and kLateWaitMs passed,
// and every kLateScanMs for a write outside Kiln's store. A look compares each bound slot (its bytes and the store's signature
// of it: no copy, nothing written) with what the last report saw, and rebuilds the live rows (report(false): registry.json
// and the Conflicts view, no new generation or ready event) only when one differs. Never per tick: a tick compares the
// generation and the clock. A field that keeps changing doubles the wait, up to kLateWaitMaxMs.
// Review: a look reads at most kLateBatch slots a tick and goes on at the next ticks (under the store's lock, which the game's
// job threads wait for too): reading every slot at once cost about 19 ms at 40,000 bound slots (a pack over every entry's
// stacks or drops binds that many), every 10 s.
constexpr uint64_t kLateWaitMs = 2000, kLateScanMs = 10000, kLateWaitMaxMs = 60000;
constexpr size_t kLateBatch = 1024;

uint64_t late_signature(uintptr_t addr, uint8_t size) {
    uint8_t b[kSlotBytes] = {};
    const uint8_t ok = rd(addr, b, size) ? 1 : 0;
    const uint64_t store = store_slot_signature(addr);
    uint64_t h = 1469598103934665603ull;
    auto mix = [&h](const void* p, size_t n) {
        for (size_t i = 0; i < n; ++i) h = (h ^ static_cast<const uint8_t*>(p)[i]) * 1099511628211ull;
    };
    mix(&ok, 1), mix(b, size), mix(&store, sizeof store);
    return h;
}

void late_seen() {  // what the report just built saw (report() calls it last); a look going on starts over at its next turn
    g.late_slots.clear();
    for (const auto& [ref, field] : g.fields)
        for (const LiveSlot& slot : field.slots) g.late_slots.push_back({slot.addr, slot.size, 0});
    // each slot once (formula inputs and list slots are shared by fields), in address order (the store's map is too)
    std::sort(g.late_slots.begin(), g.late_slots.end(), [](const LateSlot& a, const LateSlot& b) { return a.addr != b.addr ? a.addr < b.addr : a.size < b.size; });
    g.late_slots.erase(std::unique(g.late_slots.begin(), g.late_slots.end(), [](const LateSlot& a, const LateSlot& b) { return a.addr == b.addr && a.size == b.size; }),
                       g.late_slots.end());
    for (LateSlot& s : g.late_slots) s.sig = late_signature(s.addr, s.size);
    g.late_next = g.late_slots.size();
    g.late_gen = store_generation();
    g.late_at = g_registry_now_ms();
}

void late_check() {
    if (!g.in || !g.applied || !g.have_report || g.pending_set || g.late_slots.empty()) return;
    if (g.late_next >= g.late_slots.size()) {  // no look going on: one starts when it is due
        const uint64_t now = g_registry_now_ms();
        const bool moved = store_generation() != g.late_gen;
        const uint64_t wait = std::max(g.late_wait, kLateWaitMs);
        if (now - g.late_at < (moved ? wait : std::max(wait, kLateScanMs))) return;
        g.late_at = now;
        g.late_gen = store_generation();
        g.late_next = 0;
    }
    const size_t end = std::min(g.late_next + kLateBatch, g.late_slots.size());
    for (; g.late_next < end; ++g.late_next) {
        const LateSlot& s = g.late_slots[g.late_next];
        if (late_signature(s.addr, s.size) == s.sig) continue;
        const uint64_t wait = std::max(g.late_wait, kLateWaitMs);
        if (++g.late_rebuilds == 1)
            LOGI("registry (live): a field the registry writes changed after the last report (a later code change or another writer): "
                 "state/registry.json updated (later ones too, at most every %llu s while they keep coming)",
                 static_cast<unsigned long long>(kLateWaitMaxMs / 1000));
        else
            LOGD("registry (live): a later write changed a field the registry writes: state/registry.json updated (%zu)", g.late_rebuilds);
        report(false);  // late_seen: what this report saw, no look going on
        g.late_wait = std::min(wait * 2, kLateWaitMaxMs);
        return;
    }
    if (g.late_next >= g.late_slots.size()) g.late_wait = kLateWaitMs;  // a whole look found nothing changed
}

// Each committed transaction is a later composition turn. Preserve claims and chains rather than flattening values.
void rebuild_entry_stage() {
    auto mods = g.in->mods;
    std::vector<registry::Refusal> world_refused;
    for (auto& mod : mods) for (auto& batch : mod.batches) {
        std::erase_if(batch.edits, [&](auto& edit) {
            if (edit.entry == registry::EntryOp::Add) {
                const auto failed = g.entry_failures.find({edit.registry, edit.add_id});
                if (failed == g.entry_failures.end()) return false;
                mod.refused.push_back({mod.id, batch.source, batch.stage, edit.n, failed->second,
                    edit.registry + "|" + edit.add_id, edit.registry, edit.add_id, ""});
                world_refused.push_back(mod.refused.back()); return true;
            }
            if (edit.entry == registry::EntryOp::Remove) for (const auto& [ref, reason] : g.removal_failures) {
                if (ref.registry != edit.registry || !registry::glob_ci(edit.select.id, ref.id)) continue;
                edit.select.except.insert(ref.id);
                mod.refused.push_back({mod.id, batch.source, batch.stage, edit.n, reason, "", ref.registry, ref.id, ""});
                world_refused.push_back(mod.refused.back());
            }
            return false;
        });
    }
    auto result = registry::compose(*g.in->schema, g.in->base, g.in->old, mods);
    result.refused.insert(result.refused.end(), world_refused.begin(), world_refused.end());
    result.refused.insert(result.refused.begin(), g.in->pre_refused.begin(), g.in->pre_refused.end());
    for (const auto& [ref, field] : g.in->boot_fields) result.fields[ref] = field;
    g.stage_result = std::move(result);
    g.prepared = registry::prepare_base(g.in->base, &g.stage_result);
}

void compose_changes() {
    registry::Result result = g.stage_result;
    std::vector<const LiveState::Change*> ordered;
    for (const auto& [h, change] : g.changes)
        if (change.order && !retired(change.owner) && !inactive_now(change)) ordered.push_back(&change);
    std::sort(ordered.begin(), ordered.end(), [](const auto* a, const auto* b) { return a->order < b->order; });
    for (const auto* change : ordered) {
        registry::ModInput input;
        input.id = change->mod; input.batches = change->batches; input.refused = change->refused;
        if (!change->world_except.empty() && change->except_world == g.world)
            for (auto& batch : input.batches)
                for (auto& edit : batch.edits)
                    if (const auto it = change->world_except.find({batch.source, edit.n}); it != change->world_except.end())
                        edit.select.except.insert(it->second.begin(), it->second.end());
        result = registry::compose_after(*g.in->schema, g.in->base, *g.prepared, result, {input});
        result.refused.insert(result.refused.end(), change->refused.begin(), change->refused.end());
    }
    g.in->result = std::move(result);
}

bool refuse_removals(const std::map<registry::EntryRef, std::string>& failures) {
    bool changed = false;
    for (const auto& [ref, why] : failures) changed |= g.removal_failures.emplace(ref, why).second;
    if (changed) { rebuild_entry_stage(); compose_changes(); }
    return changed;
}
std::map<registry::EntryRef, std::string> failed_drop_exclusions(bool written) {
    std::map<registry::EntryRef, std::string> failed;
    for (const auto& [ref, outcome] : g.in->result.fields) {
        if (ref.registry != "drops" || ref.field != "entries") continue;
        if (std::any_of(g.pending_tables.begin(), g.pending_tables.end(), [&](const auto& pending) { return pending.field == ref; })) continue;
        const auto field = g.fields.find(ref);
        bool bound = field != g.fields.end();
        std::optional<Value> actual;
        if (written && bound) {
            std::vector<SlotBytes> bytes(field->second.slots.size());
            for (size_t i = 0; i < bytes.size(); ++i) {
                const auto slot = field->second.slots[i]; Slot state;
                if (!rd(slot.addr, bytes[i].data(), slot.size) || (store_slot_copy(slot.addr, &state) && state.foreign)) { bound = false; break; }
            }
            if (bound) actual = field->second.decode(bytes, nullptr);
        }
        for (const auto& row : outcome.chain) {
            if (row.kind != registry::WriterKind::Check || !row.source.starts_with("removal:") || !row.element.on) continue;
            const Value* value = actual && actual->kind == Value::List && row.element.index < actual->list.size()
                ? actual->list[row.element.index].leaf(row.element.leaf) : nullptr;
            if (!bound || (written && (!value || value->as_double() != 0))) {
                const registry::EntryRef item{"items", row.source.substr(8)};
                failed[item] = "drop exclusion for " + item.id + " could not be " + (written ? "written safely" : "bound") + "; item removal was not applied";
            }
        }
    }
    return failed;
}
void reconcile_registered_entries() {
    if (!g.in->entry_registered || !g.world) return;
    bool changed = false;
    for (const auto& [ref, definition] : g.entry_catalog) {
        if (g.entry_failures.count(ref)) continue;
        std::string why;
        if (!g.in->entry_registered(ref, &why)) {
            g.entry_failures[ref] = "entry " + ref.registry + "[" + ref.id + "] did not register in this world: " + why;
            changed = true;
        }
    }
    if (changed) rebuild_entry_stage();
}

bool pump_retiring() {
    std::set<KilnOwner> owners;
    { std::lock_guard lock(g_retiring_mu); owners.swap(g_retiring); }
    bool changed = false;
    for (KilnOwner owner : owners) {
        g.dead.insert(owner);
        for (auto it = g.changes.begin(); it != g.changes.end();) {
            if (it->second.owner != owner) { ++it; continue; }
            changed |= it->second.order != 0;
            it = g.changes.erase(it);
        }
    }
    return changed;
}

std::vector<uintptr_t> change_slots(uint64_t handle);
uint32_t foreign_slots(const std::vector<uintptr_t>& slots);

std::string binding_refusal(uint64_t handle) {
    const std::string prefix = "live:" + std::to_string(handle) + ":";
    const auto refusal = std::find_if(g.refused.begin(), g.refused.end(), [&](const auto& row) { return row.source.rfind(prefix, 0) == 0; });
    return refusal == g.refused.end() ? std::string() : refusal->reason;
}
void prepare_bindings() {
    g.refused.clear(); g.refused_keys.clear();
    put_all();
    // Evaluation can fail over this world's values even when the copied archive values admitted the edit.
    // Fold every bound slot before any write, including dependent slots read by formulas and lists.
    std::set<uintptr_t> seen;
    for (const auto& [ref, field] : g.fields)
        for (const auto& slot : field.slots)
            if (seen.insert(slot.addr).second) {
                uint8_t bytes[kSlotBytes] = {};
                store_effective(slot.addr, slot.size, bytes);
            }
}
// The Kiln places of the bound fields on these slots ("items[game:x].maxStack"), for reasons that never carry a process
// address (review4 nit): at most three, then a count.
std::string places_of(const std::set<uintptr_t>& slots) {
    std::string out;
    size_t named = 0, more = 0;
    for (const auto& [ref, field] : g.fields) {
        if (std::none_of(field.slots.begin(), field.slots.end(), [&](const LiveSlot& s) { return slots.count(s.addr) != 0; })) continue;
        if (named == 3) { ++more; continue; }
        out += (named++ ? ", " : "") + ref.text();
    }
    if (!named) return "a game memory slot";
    return more ? out + " and " + std::to_string(more) + " more" : out;
}
std::string write_failure(const std::set<uintptr_t>& slots) { return "the game memory write of " + places_of(slots) + " failed"; }
std::set<uintptr_t> slots_in(const std::vector<uintptr_t>& slots, const std::set<uintptr_t>& of) {
    std::set<uintptr_t> out;
    for (uintptr_t a : slots) if (of.count(a)) out.insert(a);
    return out;
}
// A retained change that cannot apply whole in this world is deactivated for this world only (review4 NS2): its handle
// stays valid (revert works), a process change is tried again in the next world, and its owner's mod is named in the report.
void reject_change(uint64_t handle, LiveState::Change& change, const std::string& why, std::vector<registry::Refusal>* rejected) {
    change.inactive = true, change.inactive_world = g.world;
    const std::string after = change.world ? "the change stays inactive for the rest of this world"
                                           : "the change is kept and tried again when the next world loads";
    const std::string reason = not_exact_reason(why)
        ? why + "; live change " + std::to_string(handle) + " not applied in this world; " + after
        : "live change " + std::to_string(handle) + " not applied in this world: " + why + "; " + after;
    rejected->push_back({change.mod, "live:" + std::to_string(handle) + ":", registry::Stage::Alter, 0, reason, ""});
}
// Binding and evaluation failures only. A slot another writer took after Kiln's layer stays with that writer for the world
// (ruling 2026-11-22 5:22 PM): the store leaves that one slot out and reports it, the change's other fields stay.
bool reject_unbound_changes(uint64_t candidate, std::vector<registry::Refusal>* rejected) {
    bool any = false;
    for (auto& [handle, change] : g.changes) {
        if (!change.order || handle == candidate || inactive_now(change)) continue; // the committing caller rolls its own candidate back
        const std::string why = binding_refusal(handle);
        if (why.empty()) continue;
        reject_change(handle, change, why, rejected);
        any = true;
    }
    return any;
}
// A failed write is charged per slot (review4 NM1): a retained change is rejected only when one of its own slots did not land,
// never for another change's slot, an unrelated pack edit's slot or the committing candidate's own failure.
bool reject_unwritten_changes(uint64_t candidate, const std::set<uintptr_t>& unwritten, std::vector<registry::Refusal>* rejected) {
    bool any = false;
    for (auto& [handle, change] : g.changes) {
        if (!change.order || handle == candidate || inactive_now(change)) continue;
        const std::set<uintptr_t> mine = slots_in(change_slots(handle), unwritten);
        if (mine.empty()) continue;
        reject_change(handle, change, write_failure(mine), rejected);
        any = true;
    }
    return any;
}
// review4 NS6: an entry-point change whose binding or evaluation refused some entries (an inexact whole-number cost, an entry
// missing in this world, a config the entry lacks) leaves those entries out for this world and applies the rest. True: an
// entry was newly left out (the caller recomposes and binds again). A row without an entry falls back to the whole change.
bool exclude_refused_entries() {
    bool any = false;
    for (auto& [handle, change] : g.changes) {
        if (!change.order || inactive_now(change) || !per_entry(change)) continue;
        const std::string prefix = "live:" + std::to_string(handle) + ":";
        std::vector<const registry::Refusal*> rows;
        for (const auto& r : g.refused)
            if (r.source.rfind(prefix, 0) == 0) rows.push_back(&r);
        if (rows.empty() || std::any_of(rows.begin(), rows.end(), [](const auto* r) { return r->id.empty(); })) continue;
        if (change.except_world != g.world) change.world_except.clear(), change.world_rows.clear(), change.except_world = g.world;
        for (const auto* r : rows)
            if (change.world_except[{r->source, r->n}].insert(r->id).second) {
                change.world_rows.push_back(*r);
                any = true;
            }
    }
    return any;
}
// A refusal row once: a pass that keeps the rows of an earlier pass binds again and would repeat the stage's own rows.
void keep_row(const registry::Refusal& r) {
    const bool seen = std::any_of(g.refused.begin(), g.refused.end(), [&](const registry::Refusal& x) {
        return x.mod == r.mod && x.source == r.source && x.n == r.n && x.reason == r.reason;
    });
    if (!seen) g.refused.push_back(r);
}
struct ApplyOutcome {
    std::string error;      // a write that did not land after every charge and retry (address-free); empty: all verified
    std::string candidate;  // the committing candidate's own write failure; empty: none
    std::string unbound;    // the candidate's binding or evaluation refusal (a MANUAL transaction refuses whole)
    std::vector<registry::Refusal> entries;  // the candidate's entries refused alone in this world (review4 NS6)
};
// Bind, flush and charge failures. charge=false (a rollback, a revert, an owner's cleanup): a failed write is reported to the
// caller as unverified and charged to no retained change. keep_refused: the rows found before this pass stay (a commit's
// rollback keeps the rows of the changes its first pass rejected, review4 NM1).
ApplyOutcome apply_changes(uint64_t candidate = 0, bool keep_refused = false, bool application = false, bool charge = true) {
    std::vector<registry::Refusal> prior;
    if (keep_refused) prior = std::move(g.refused); // changed pack sections can add their own restart diagnostics
    std::vector<registry::Refusal> rejected;
    ApplyOutcome out;
    std::set<uintptr_t> unwritten;  // the last flush's
    reconcile_registered_entries();
    // A committing candidate has already composed/pruned its copied IR. Only recompose again if
    // preflight removes another retained change; ordinary applications/reverts start from scratch.
    if (!candidate) compose_changes();
    for (;;) {
        if (g.world && g.in->prepare_visibility) {
            const auto failed = g.in->prepare_visibility(g.in->result, g.in->nums);
            if (refuse_removals(failed)) continue;
        }
        prepare_bindings();
        if (refuse_removals(failed_drop_exclusions(false))) continue;
        if (exclude_refused_entries()) { compose_changes(); continue; }
        if (reject_unbound_changes(candidate, &rejected)) { compose_changes(); continue; }
        if (candidate && !binding_refusal(candidate).empty()) { out.unbound = binding_refusal(candidate); break; } // fail before writing any part of this candidate
        std::vector<uintptr_t> missed;
        std::string ignored;  // store_flush's text names a process address; reasons name Kiln places instead
        store_flush_checked(&ignored, &missed);
        gd_section_written();  // kiln.gamedata's slots: events re-sorted, the buff mirror follows (dev.32 buffs, abilities)
        unwritten = {missed.begin(), missed.end()};
        if (refuse_removals(failed_drop_exclusions(true))) continue;
        const std::string visibility_error = g.in->verify_visibility ? g.in->verify_visibility(missed) : std::string();
        if (!visibility_error.empty()) {
            std::map<registry::EntryRef, std::string> failed;
            for (const auto& ref : g.in->result.removed)
                if (ref.registry == "items" || ref.registry == "recipes") failed[ref] = visibility_error;
            if (refuse_removals(failed)) continue; // remove every related hide and restore successful half-writes first
            out.error = visibility_error;
            break;
        }
        if (unwritten.empty()) { if (g.world && g.in->publish_visibility) g.in->publish_visibility(); break; }
        if (candidate) {
            const std::set<uintptr_t> mine = slots_in(change_slots(candidate), unwritten);
            if (!mine.empty()) { out.candidate = write_failure(mine); break; } // the caller rolls the candidate back alone
        }
        // A retained change whose own slot failed is removed whole and the remaining layers are flushed again, so no
        // successful half-change is left waiting for the next world.
        if (!charge || !reject_unwritten_changes(candidate, unwritten, &rejected)) break;
        compose_changes();
    }
    if (!unwritten.empty() && out.candidate.empty()) {
        out.error = write_failure(unwritten);
        for (auto& refusal : rejected) refusal.reason += "; rollback unverified: " + out.error;
        if (charge) // a slot of no live change (a pack's or the stage's edit): named for the edit that owns it
            for (const auto& [ref, field] : g.fields)
                if (std::any_of(field.slots.begin(), field.slots.end(), [&](const LiveSlot& s) { return unwritten.count(s.addr) != 0; }))
                    if (auto fo = g.in->result.fields.find(ref); fo != g.in->result.fields.end())
                        refuse(ref, registry_rows(fo->second), "the game memory write failed; this edit was not applied");
    }
    for (const auto& r : prior) keep_row(r);
    for (const auto& r : rejected) keep_row(r);
    for (const auto& [handle, change] : g.changes)  // entries left out for this world keep their rows in every report of it
        if (change.order && change.except_world == g.world && !inactive_now(change))
            for (const auto& r : change.world_rows) {
                keep_row(r);
                if (handle == candidate) out.entries.push_back(r);
            }
    if (application) report();
    else queue_change_report();
    return out;
}

KilnResult change_of(KilnOwner owner, uint64_t handle, LiveState::Change** out) {
    if (!owner) return KILN_ERR_INVALID_ARGUMENT;
    if (retired(owner)) return KILN_ERR_REVOKED;
    const auto it = g.changes.find(handle);
    if (it == g.changes.end()) return KILN_ERR_NOT_FOUND;
    if (it->second.owner != owner) return KILN_ERR_INVALID_ARGUMENT;
    *out = &it->second;
    return KILN_OK;
}

std::string edit_key(const registry::Edit& e) {
    if (e.entry != registry::EntryOp::None)  // by entry: a new entry op is new even when the mod had another one at the start
        return e.registry + "#entry:" + (e.entry == registry::EntryOp::Add ? "add:" + lower(e.add_id) : "remove:" + lower(e.select.id));
    if (!e.raw.empty()) return e.registry + "#raw";
    return e.registry + "." + (e.element.on ? e.element.list : registry::generic_path(e.field));
}

bool same_entry_intent(const registry::Edit& a, const registry::Edit& b) {
    return a.entry == b.entry && a.registry == b.registry && a.add_id == b.add_id && a.like == b.like &&
        a.add_fields == b.add_fields && a.select.id == b.select.id && a.select.where == b.select.where &&
        a.select.has == b.select.has && a.select.has_not == b.select.has_not && a.exclusive == b.exclusive && a.yield == b.yield;
}

// The data batches (source "content/...") of the kept inputs replaced by the sections now; an edit naming what the start
// never decoded, or an entry add / remove the start did not have, refused ("needs a restart").
void recompose(const std::vector<RegistrySection>& sections) {
    std::vector<registry::ModInput> mods = g.in->mods;
    auto is_data = [](const std::string& source) { return source.rfind("content/", 0) == 0; };
    std::vector<registry::Refusal> again;  // the changed sections' own refusals and the mapping check's: in registry.json as at the start
    // a section is changed when its file is new or its text is not the last application's; the stage's refusals of a changed
    // or removed file are replaced by what reading it again says, the others stay as the stage made them
    std::set<std::string> stale;  // "<mod>|<source>" and "<mod>|<rel>" of changed and removed files
    std::set<std::string> now_rels;
    for (const RegistrySection& s : sections) {
        now_rels.insert(s.rel);
        const auto h = g.hashes.find(s.rel);
        if (h == g.hashes.end() || h->second != s.hash) stale.insert(s.mod + "|" + s.source), stale.insert(s.mod + "|" + s.rel);
    }
    for (const auto& [rel, info] : g.section_of)
        if (!now_rels.count(rel)) stale.insert(info.first + "|" + info.second), stale.insert(info.first + "|" + rel);
    for (registry::ModInput& m : mods) {
        std::set<std::string> before;
        std::vector<registry::Batch> frozen;
        for (const registry::Batch& bt : m.batches) if (is_data(bt.source)) {
            auto entries = bt; entries.edits.clear();
            for (const registry::Edit& e : bt.edits) {
                before.insert(edit_key(e));
                if (e.entry != registry::EntryOp::None) entries.edits.push_back(e);
            }
            if (!entries.edits.empty()) frozen.push_back(std::move(entries));
        }
        auto entry_restart = [&](const registry::Batch& batch, const registry::Edit& edit) {
            registry::Refusal r{m.id, batch.source, batch.stage, edit.n,
                "RestartRequired: " + edit.registry + " entry intent changed after GameStart; the original entry operation is kept until restart", "",
                edit.registry, edit.entry == registry::EntryOp::Add ? edit.add_id : edit.select.id, ""};
            again.push_back(std::move(r));
        };
        std::set<const registry::Edit*> matched;
        std::erase_if(m.batches, [&](const registry::Batch& bt) { return is_data(bt.source); });
        std::erase_if(m.refused, [&](const registry::Refusal& r) { return is_data(r.source); });
        std::vector<registry::Batch> data;
        for (const RegistrySection& s : sections) {
            if (s.mod != m.id) continue;
            const bool changed = stale.count(s.mod + "|" + s.source) != 0;
            for (registry::Batch bt : s.batches) {
                std::erase_if(bt.edits, [&](const registry::Edit& edit) {
                    if (edit.entry == registry::EntryOp::None) return false;
                    for (const auto& batch : frozen) if (batch.source == bt.source && batch.stage == bt.stage)
                        for (const auto& old : batch.edits) if (!matched.count(&old) && same_entry_intent(old, edit)) {
                            matched.insert(&old); return true;
                        }
                    entry_restart(bt, edit);
                    return true; // only the frozen GameStart intent is ever replayed
                });
                if (g.in->check) {  // every batch: an unchanged section's refused edits were taken out at the start too
                    std::vector<registry::Refusal> r;
                    g.in->check(&bt, &r);
                    for (const registry::Refusal& x : r) {
                        m.refused.push_back(x);
                        if (changed) again.push_back(x);
                    }
                }
                std::erase_if(bt.edits, [&](const registry::Edit& e) {
                    const std::string k = edit_key(e);
                    if (before.count(k) || (k.find('#') == std::string::npos && g.in->decoded.count(k))) return false;
                    // the stable token first (registry.json, the reload reply, kilnctl status, the launcher)
                    registry::Refusal r;
                    r.mod = m.id, r.source = bt.source, r.stage = bt.stage, r.n = e.n;
                    r.registry = e.registry, r.id = e.select.id;
                    r.field = e.element.on ? e.element.list : e.raw.empty() ? registry::generic_path(e.field) : std::string();
                    r.reason = "RestartRequired: " + k + ": the base was read at start; this edit was not applied";
                    g.refused.push_back(r);
                    return true;
                });
                data.push_back(std::move(bt));
            }
            for (const registry::Refusal& r : s.refused) {
                m.refused.push_back(r);
                if (changed) again.push_back(r);
            }
            if (changed && !s.error.empty())
                again.push_back({s.mod, s.rel, registry::Stage::Alter, 0, s.rel + ": " + s.error + "; nothing of this file's registry section was applied", ""});
        }
        for (const auto& batch : frozen) for (const auto& edit : batch.edits)
            if (!matched.count(&edit)) entry_restart(batch, edit); // removed/changed operation or deleted file
        data.insert(data.begin(), frozen.begin(), frozen.end());
        // Match Runner::run: data precedes queued code and recorded callback turns, on every recompose too.
        m.batches.insert(m.batches.begin(), std::make_move_iterator(data.begin()), std::make_move_iterator(data.end()));
    }
    for (const RegistrySection& s : sections)  // a mod that had no registry content at the start
        if (std::none_of(mods.begin(), mods.end(), [&](const registry::ModInput& m) { return m.id == s.mod; })) {
            registry::Refusal r;
            r.mod = s.mod, r.source = s.source;
            r.reason = "RestartRequired: a registry section added after the start needs a restart; this edit was not applied";
            g.refused.push_back(r);
        }
    registry::Result next = registry::compose(*g.in->schema, g.in->base, g.in->old, mods, g.in->result);
    // restart fields are the boot back-end's as written at the start: a reload changes them only at the next start. Each one
    // whose value the reloaded sections change is a row of registry.json with the stable token (before,
    // only kiln.log said so), named for the changed section's last edit of it, so the next reload of that section replaces it.
    auto restart_writer = [&](const registry::FieldOutcome* fo) -> const registry::ChainRow* {
        if (!fo) return nullptr;
        for (auto row = fo->chain.rbegin(); row != fo->chain.rend(); ++row)
            if (row->kind == registry::WriterKind::Registry && stale.count(row->mod + "|" + row->source)) return &*row;
        return nullptr;
    };
    std::set<registry::FieldRef> restart_refs;
    for (const auto* fields : {&next.fields, &g.in->result.fields})
        for (const auto& [fr, fo] : *fields) {
            const registry::RegistrySpec* rs = g.in->schema->registry(fr.registry);
            const FieldSpec* sp = rs ? rs->field(fr.field) : nullptr;
            if (sp && sp->applies == registry::Applies::Restart) restart_refs.insert(fr);
        }
    for (const registry::FieldRef& fr : restart_refs) {
        const auto now = next.fields.find(fr);
        const auto old = g.in->result.fields.find(fr);
        const registry::FieldOutcome* now_fo = now == next.fields.end() ? nullptr : &now->second;
        const registry::FieldOutcome* old_fo = old == g.in->result.fields.end() ? nullptr : &old->second;
        if (now_fo && old_fo && now_fo->result == old_fo->result) continue;
        if (!now_fo && !old_fo) continue;
        LOGI("registry: %s changes at the next start (a restart field)", fr.text().c_str());
        const registry::ChainRow* by = restart_writer(now_fo);
        if (!by) by = restart_writer(old_fo);  // the reloaded section no longer edits it: named for the edit it had
        if (!by) continue;                     // not a reloaded section's (an old path's); kiln.log has it
        registry::Refusal r;
        r.mod = by->mod, r.source = by->source, r.stage = by->stage, r.n = by->n;
        r.registry = fr.registry, r.id = fr.id, r.field = fr.field, r.raw = registry::is_raw_field(fr.field);
        r.reason = "RestartRequired: " + fr.text() + " is read at game start; the reloaded value applies at the next start";
        if (std::none_of(again.begin(), again.end(), [&](const auto& x) { return x.mod == r.mod && x.source == r.source && x.n == r.n && x.reason == r.reason; }))
            again.push_back(std::move(r));
    }
    // what the stage refused outside compose stays (but the data sections' own: they were read and checked again above)
    std::vector<registry::Refusal> kept;
    for (const registry::Refusal& r : g.in->pre_refused)
        if (!stale.count(r.mod + "|" + r.source)) kept.push_back(r);
    for (const auto& row : again)
        if (std::none_of(kept.begin(), kept.end(), [&](const auto& old) { return old.mod == row.mod && old.source == row.source && old.n == row.n && old.reason == row.reason; })) kept.push_back(row);
    next.refused.insert(next.refused.begin(), kept.begin(), kept.end());
    std::erase_if(next.fields, [&](const auto& kv) {
        const registry::RegistrySpec* rs = g.in->schema->registry(kv.first.registry);
        const FieldSpec* sp = rs ? rs->field(kv.first.field) : nullptr;
        return sp && sp->applies == registry::Applies::Restart;
    });
    for (const auto& [fr, fo] : g.in->boot_fields) next.fields[fr] = fo;
    g.in->mods = std::move(mods);
    g.in->pre_refused = std::move(kept);
    g.stage_result = next;
    g.in->result = std::move(next);
    if (!g.entry_failures.empty() || !g.removal_failures.empty()) rebuild_entry_stage();
    else g.prepared = registry::prepare_base(g.in->base, &g.stage_result);
}


void index_base() {
    g.kiln_of.clear();
    g.kiln_by_guid.clear();
    for (const auto& [key, guid] : g.in->ids) {
        const size_t bar = key.find('|');
        if (bar != std::string::npos && !guid.empty()) g.kiln_by_guid.emplace(key.substr(0, bar) + "|" + lower(guid), key.substr(bar + 1));
    }
    for (const auto& [key, num] : g.in->nums) {
        const size_t bar = key.find('|');
        if (bar != std::string::npos) g.kiln_of.emplace(num_key(key.substr(0, bar), num), key.substr(bar + 1));
    }
}
// Under store_mutex: install a prepared copied base.
void install_live_base(registry::stage::LiveData&& data) {
    auto prepared = registry::prepare_base(data.base, &data.result);
    g.stage_result = data.result; g.in->result = std::move(data.result);
    g.in->base = std::move(data.base); g.in->old = std::move(data.old);
    g.in->ids = std::move(data.ids); g.in->nums = std::move(data.nums); g.in->decoded = std::move(data.decoded);
    g.prepared = std::move(prepared);
    index_base();
    g.in->prepare = {};
}
// Under store_mutex (a pack application's late recomposition, whose caller holds the lock): prepare and install at once.
bool prepare_live_base() {
    if (!g.in) return false;
    if (!g.in->prepare) return true;
    registry::stage::LiveData data;
    std::string why;
    if (!g.in->prepare(&data, &why)) {
        LOGW("registry (live): base unavailable: %s", why.c_str());
        return false;
    }
    install_live_base(std::move(data));
    return true;
}
// Without store_mutex held: the first-use decode reads the archive (seconds on a cold cache) outside the store's lock, so
// the world thread's other store users and the drop-table hooks are not stalled behind it (review4 NS1). The decode reads
// the game's bytes with Kiln's own store writes taken back out (registry/pristine.h); a reinstall meanwhile discards it.
bool ensure_live_base() {
    decltype(RegistryLive::prepare) prepare;
    std::shared_ptr<RegistryLive> in;
    {
        std::lock_guard lock(store_mutex());
        if (!g.in) return false;
        if (!g.in->prepare) return true;
        prepare = g.in->prepare, in = g.in;
    }
    registry::stage::LiveData data;
    std::string why;
    if (!prepare(&data, &why)) {
        LOGW("registry (live): base unavailable: %s", why.c_str());
        return false;
    }
    std::lock_guard lock(store_mutex());
    if (g.in != in) return g.in && !g.in->prepare;
    if (g.in->prepare) install_live_base(std::move(data));
    return true;
}

}  // namespace

KilnOwner registry_owner() {
    std::lock_guard lock(store_mutex());
    if (!g.owner) {
        static const char kLabel[] = "kiln:registry";
        g.owner = g_api ? g_api->owner_create(KilnStr{kLabel, sizeof kLabel - 1}) : static_cast<KilnOwner>(0x7E610001);
        set_owner_label(g.owner, "registry");
    }
    return g.owner;
}

bool registry_live_prepare_entry_identities(const registry::json& fields, std::string* why) {
    const auto perks = fields.find("perks");
    if (perks == fields.end() || !perks->is_array() || perks->empty()) return true;
    const bool locked = store_locked_here();
    {
        std::lock_guard lock(store_mutex());
        if (!g.in || !g.in->prepare) return true; // the initializer reports unknown ids after preparation
        bool missing = false;
        for (const auto& perk : *perks)
            if (perk.is_string() && !g.in->ids.count("perks|" + perk.get<std::string>())) missing = true;
        if (!missing) return true;
        if (!on_live_thread()) return *why = "stable entry identities need the world registration thread", false;
        if (locked) return *why = "stable entry identities need preparation outside the store lock", false;
    }
    if (!ensure_live_base()) return *why = "stable entry identities could not be prepared", false;
    std::lock_guard lock(store_mutex());
    if (!on_live_thread()) return *why = "the world changed during stable entry identity preparation", false;
    return true;
}

bool registry_live_initialize_entry(uint32_t kind, const Entry& entry, const std::string& id,
                                    const registry::json& fields, std::string* why, const Schema* native_schema) {
    if (!registry_live_prepare_entry_identities(fields, why)) return false;
    std::lock_guard lock(store_mutex());
    const std::string registry = kind == KILN_CONTENT_ITEMS ? "items" : kind == KILN_CONTENT_RECIPES ? "recipes" : "";
    if (!g.in || !g.in->schema || !g.in->regmap || registry.empty() || !fields.is_object())
        return *why = "the stable entry mapper is unavailable", false;
    if (g.reading || g.initializing_entry) return *why = "the stable entry mapper is busy", false;
    struct Restore {
        std::string reason = std::move(g.read_reason);
        std::map<registry::FieldRef, LiveField> fields = std::move(g.read_fields);
        ~Restore() {
            g.initializing_entry = nullptr; g.initializing_schema = nullptr; g.initializing_ref = {}; g.initialize_evals.clear();
            g.reading = false; g.read_reason = std::move(reason); g.read_fields = std::move(fields);
        }
    } restore;
    g.reading = true; g.initializing_entry = &entry; g.initializing_schema = native_schema; g.initializing_ref = {registry, id};
    const auto* rs = g.in->schema->registry(registry);
    if (!rs) return *why = "no stable registry " + registry, false;
    std::vector<std::pair<std::string, Value>> values;
    for (auto it = fields.begin(); it != fields.end(); ++it) {
        const auto* spec = rs->field(it.key());
        if (!spec || spec->read_only || spec->applies == registry::Applies::Restart)
            return *why = registry + "." + it.key() + " cannot initialize a content entry", false;
        std::string reason;
        if (g.in->regmap->mapping(registry, it.key(), &reason) != rmap::Mapping::Mapped)
            return *why = registry + "." + it.key() + ": " + reason, false;
        Value value;
        if (!registry::value_of(it.value(), *spec, false, &value, why)) return false;
        values.emplace_back(it.key(), std::move(value));
    }
    // Formula outputs depend on the final scalar inputs (level/speed). Other initializers run first.
    const auto& rows = g.in->regmap->registries.at(registry).rows;
    std::stable_sort(values.begin(), values.end(), [&](const auto& a, const auto& b) {
        return (rows.at(a.first).conv == "formula") < (rows.at(b.first).conv == "formula");
    });
    for (const auto& [field, value] : values) {
        registry::FieldOutcome fo; registry::ChainRow row;
        row.op.set = true; row.op.value = value; row.mod = id.substr(0, id.find(':')); row.source = "entry initialization";
        fo.chain.push_back(std::move(row));
        g.read_reason.clear(); g.initialize_evals.clear();
        bind({registry, id, field}, fo);
        if (!g.read_reason.empty()) return *why = g.read_reason, false;
        if (g.initialize_evals.empty()) return *why = registry + "." + field + " has no entry initializer", false;
        for (const auto& [address, eval] : g.initialize_evals) {
            SlotBytes before{}, after{};
            if (!rd(address, before.data(), eval.first)) return *why = registry + "." + field + " is unreadable", false;
            if (!eval.second(before.data(), eval.first, after.data(), why)) return false;
            std::memcpy(reinterpret_cast<void*>(address), after.data(), eval.first);
        }
    }
    return true;
}

void registry_live_install(RegistryLive in) {
    std::lock_guard lock(store_mutex());
    g.in = std::make_shared<RegistryLive>(std::move(in));
    g.stage_result = g.in->result;
    g.entry_catalog = g.stage_result.definitions;
    g.entry_failures.clear(); g.removal_failures.clear();
    g.prepared = registry::prepare_base(g.in->base, &g.stage_result);
    g.fields.clear(); g.pending_tables.clear(); g.table_fields.clear(); g.table_addrs.clear();
    g.changes.clear(); g.dead.clear(); g.world = 0; g.world_kind = KILN_WORLD_KINDS;
    { std::lock_guard retiring(g_retiring_mu); g_retiring.clear(); }

    if (!g.in->schema) g.in->schema = std::make_shared<const registry::Schema>(registry_schema());
    if (!g.in->regmap) g.in->regmap = std::make_shared<const rmap::RegMap>();
    index_base();
    g.hashes.clear();
    g.applied = false;
    g.have_report = false;
    g.pending_set = false;
    g.pending_application = false;
    g.pending_change = false;
    g.tables_changed = false;
    g.skipped_logged = 0;
    g.slot_places.clear();
    g.late_gen = g.late_at = g.late_wait = 0;
    g.late_slots.clear();
    g.late_next = 0;
    g.late_rebuilds = 0;
    g.owner_names.clear();
}

std::shared_ptr<const registry::Schema> registry_live_schema() {
    std::lock_guard lock(store_mutex());
    return g.in ? g.in->schema : nullptr;
}

void registry_live_apply(const std::vector<RegistrySection>& sections, const registry::Schema* schema) {
    if (!g.in || g.in->schema.get() != schema) return;
    const bool late = bool(g.in->prepare) && !sections.empty();
    if (late && !prepare_live_base()) return;
    std::map<std::string, uint64_t> hashes;
    for (const RegistrySection& s : sections) hashes[s.rel] = s.hash;
    g.refused.clear(), g.refused_keys.clear();
    if (!g.in->rollback.empty() && (late || (g.applied && hashes != g.hashes))) {
        // registry-rollback: this start runs on a rolled-back plan's edits; a section's change is not composed over them
        // (the first application of a late base: the stage already named the mods the plan leaves out, registry.json rollback.leftOut)
        for (const RegistrySection& s : sections) {
            const auto h = g.hashes.find(s.rel);
            if (!g.applied || (h != g.hashes.end() && h->second == s.hash)) continue;
            registry::Refusal r;
            r.mod = s.mod, r.source = s.source;
            r.reason = "RestartRequired: this start runs on the rolled-back registry plan " + g.in->rollback.substr(0, 12) +
                       "; a data pack's registry section change applies after the rollback is cleared (kilnctl registry rollback --clear) and a restart";
            g.refused.push_back(std::move(r));
        }
        LOGW("registry: a data pack's registry section changed, but this start runs on a rolled-back plan: not composed again");
    } else if (late || (g.applied && hashes != g.hashes)) {
        LOGI("registry: a data pack's registry section changed: composing the registry again");
        recompose(sections);
    }
    g.hashes = std::move(hashes);
    g.section_of.clear();
    for (const RegistrySection& s : sections) g.section_of[s.rel] = {s.mod, s.source};
    g.applied = true;
    pump_retiring();
    const ApplyOutcome applied = apply_changes(0, true, true);
    if (!applied.error.empty()) LOGW("registry (live): writing: %s", applied.error.c_str());
}

void registry_live_publish() {
    decltype(RegistryLive::report) fn;
    registry::Result r;
    bool application;
    {
        std::lock_guard lock(store_mutex());
        if (!on_live_thread()) return; // keep retirement and publication queued for the world's next tick
        if (g.in && pump_retiring()) {
            if (g.world) {
                const ApplyOutcome cleanup = apply_changes(0, false, false, false);
                if (!cleanup.error.empty()) LOGW("registry (live): owner cleanup: %s", cleanup.error.c_str());
            }
            else compose_changes();
        }
        application = g.pending_set && g.pending_application;
        if (g.tables_changed && g.have_report) {
            if (refuse_removals(failed_drop_exclusions(true))) {
                const bool changed = g.pending_change;
                const auto rollback = apply_changes(0, true, false, false);
                g.pending_change = changed; // a late table refresh never emits another world event/generation
                if (!rollback.error.empty()) LOGW("registry (live): drop exclusion rollback unverified: %s", rollback.error.c_str());
            } else report(false);
        }
        late_check();  // writes made after the last report (registry-followups 1): a look every 2-10 s, a batch of slots a tick
        if (!g.in || !g.pending_set) return;
        r = std::move(g.pending);
        const bool change = !application && g.pending_change && bool(g.in->changed);
        fn = change ? g.in->changed : g.in->report;
        if (change) application = g.world != 0; // changed() receives world presence, never a ready-event request
        g.pending_set = false;
        g.pending_application = false;
        g.pending_change = false;
    }
    if (fn) fn(std::move(r), application);  // registry.json: the stage runner's writer makes it, with no lock of kiln.content's held
}

void registry_live_table(const Entry& table) {
    std::lock_guard lock(store_mutex());
    if (!g.in) return;
    const std::string guid = lower(guid_text(table.guid));
    std::vector<registry::FieldRef> fields;
    std::erase_if(g.pending_tables, [&](const LiveState::Pending& p) {
        if (lower(p.guid) != guid) return false;
        fields.push_back(p.field);
        return true;
    });
    // bound before: its layers come off the stacks they are on (the game may read other stacks now) and go on again
    if (auto it = g.table_fields.find(guid); it != g.table_fields.end()) {
        for (const registry::FieldRef& f : it->second)
            if (std::find(fields.begin(), fields.end(), f) == fields.end()) fields.push_back(f);
        g.table_fields.erase(it);
    }
    if (auto it = g.table_addrs.find(guid); it != g.table_addrs.end()) {
        for (uintptr_t a : it->second) store_drop(registry_owner(), a);
        g.table_addrs.erase(it);
    }
    for (const registry::FieldRef& f : fields) {
        g.fields.erase(f);
        if (auto it = g.in->result.fields.find(f); it != g.in->result.fields.end()) bind(f, it->second);
    }
    g.tables_changed |= !fields.empty();
}

void drops_table_loaded(const Entry& table) {
    std::lock_guard lock(store_mutex());
    if (drops_have_rules()) drops_apply_rules(table);  // the old packs' drop rules (they rebind the registry's too)
    nc_drops_table(table);                             // new items' stacks; a repointed table gets the rules (and the registry) again
    registry_live_table(table);                        // the registry's layers over the stacks the game reads now, rules or not
}

std::optional<registry::Result> registry_live_result() {
    std::lock_guard lock(store_mutex());
    if (!g.have_report) return std::nullopt;
    return g.reported;  // a copy: the next application replaces it
}

KilnResult registry_live_begin(KilnOwner owner, const std::string& mod, uint32_t point, uint64_t* out) {
    if (!owner || !out || point < 1 || point > 4) return KILN_ERR_INVALID_ARGUMENT;
    std::string why;
    if (!registry::mod_id_ok(mod, &why)) return KILN_ERR_INVALID_ARGUMENT;
    {
        std::lock_guard lock(store_mutex());
        if (retired(owner)) return KILN_ERR_REVOKED;
    }
    if (!ensure_live_base()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(store_mutex());
    if (retired(owner)) return KILN_ERR_REVOKED;
    if (!g.in || g.in->prepare) return KILN_ERR_UNAVAILABLE;
    // review2 S4: per-owner caps (16 open drafts, 256 retained changes) below the process-wide ones (64, 1,024), so one mod
    // cannot starve the others. UNAVAILABLE either way; kiln.log names the cap once per owner.
    size_t drafts = 0, own_drafts = 0, own_changes = 0;
    for (const auto& [h, c] : g.changes) {
        drafts += !c.order;
        if (c.owner == owner) (c.order ? own_changes : own_drafts) += 1;
    }
    const char* cap = drafts >= 64 ? "the process-wide 64 open drafts" : g.changes.size() >= 1024 ? "the process-wide 1,024 retained changes"
                    : own_drafts >= 16 ? "its 16 open drafts" : own_changes >= 256 ? "its 256 retained changes" : nullptr;
    if (cap) {
        static std::set<std::pair<KilnOwner, std::string>> warned;  // under store_mutex
        if (warned.insert({owner, cap}).second) LOGW("registry (live): %s: begin refused (UNAVAILABLE): %s are in use", mod.c_str(), cap);
        return KILN_ERR_UNAVAILABLE;
    }
    LiveState::Change change;
    // A MOD_LOAD draft is no world's: begun in a world, it may still commit after that world ended (review4 nit).
    change.owner = owner; change.mod = mod; change.point = point; change.world = point == KILN_REGISTRY_LIVE_MOD_LOAD ? 0 : g.world;
    *out = ++g_next_change;
    g.changes.emplace(*out, std::move(change));
    return KILN_OK;
}

KilnResult registry_live_submit(KilnOwner owner, uint64_t handle, const std::string& ir, RegistryLiveReply* out, std::string* report_text) {
    std::lock_guard lock(store_mutex());
    LiveState::Change* change = nullptr;
    const KilnResult rc = change_of(owner, handle, &change);
    if (rc != KILN_OK) return rc;
    if (change->order) return KILN_ERR_UNAVAILABLE;
    size_t bytes = 0, edits = change->refused.size();
    for (const auto& [h, c] : g.changes) bytes += c.bytes;
    for (const auto& b : change->batches) edits += b.edits.size();
    if (ir.size() > 16u * 1024 * 1024 || bytes + ir.size() > 64u * 1024 * 1024) return KILN_ERR_INVALID_ARGUMENT;
    registry::Batch batch;
    std::vector<registry::Refusal> refused;
    std::string why;
    bool entry_restart = false;
    auto document = registry::json::parse(ir, nullptr, false, true);
    if (document.is_object() && document.contains("edits") && document["edits"].is_array()) {
        auto& edits = document["edits"].get_ref<registry::json::array_t&>();
        uint32_t ordinal = 0;
        for (auto& edit : edits) {
            if (edit.is_object() && !edit.contains("n")) edit["n"] = ordinal;
            ++ordinal;
        }
        std::erase_if(edits, [&](const auto& edit) {
            if (!edit.is_object() || !edit.contains("entry") || !edit["entry"].is_string()) return false;
            const auto operation = edit["entry"].template get<std::string>();
            if (operation != "add" && operation != "remove") return false;
            registry::Refusal row; row.mod = change->mod;
            row.registry = edit.contains("registry") && edit["registry"].is_string() ? edit["registry"].template get<std::string>() : "";
            row.n = edit.contains("n") && edit["n"].is_number_unsigned() && edit["n"].template get<uint64_t>() <= UINT32_MAX ? edit["n"].template get<uint32_t>() : 0;
            row.id = operation == "add" && edit.contains("id") && edit["id"].is_string() ? edit["id"].template get<std::string>() : "";
            if (operation == "add" && !row.id.empty()) row.entry = row.registry + "|" + row.id;
            row.reason = "RestartRequired: " + row.registry + " entry " + operation + " is GameStart-only; restart the game to change entries";
            refused.push_back(std::move(row)); entry_restart = true;
            return true;
        });
    }
    std::vector<registry::Refusal> parsed_refusals;
    if (!registry::parse_batch(document, *g.in->schema, &batch, &parsed_refusals, &why)) {
        if (report_text) *report_text = registry::json{{"error", why}}.dump();
        return KILN_ERR_INVALID_ARGUMENT;
    }
    refused.insert(refused.end(), std::make_move_iterator(parsed_refusals.begin()), std::make_move_iterator(parsed_refusals.end()));
    if (batch.mod != change->mod || edits + batch.edits.size() + refused.size() > 4096) return KILN_ERR_INVALID_ARGUMENT;
    if (entry_restart) change->restart = change->entry_restart = true;
    batch.source = "live:" + std::to_string(handle) + ":" + std::to_string(change->point) + ":" + std::to_string(change->batches.size()) + ":" + batch.source;
    for (auto& row : refused) row.source = batch.source;
    // review: what the schemas' id migrations renamed in a live change (the stage's registry.json "migrated" lists a start's)
    for (const std::string& note : batch.migrated) LOGI("registry (live): migrated %s %s %s", change->mod.c_str(), batch.source.c_str(), note.c_str());
    std::erase_if(batch.edits, [&](const registry::Edit& edit) {
        std::string reason;
        const auto* reg = g.in->schema->registry(edit.registry);
        const auto* field = reg ? reg->field(edit.element.on ? edit.element.list : edit.field) : nullptr;
        const bool left_out = !g.in->rollback.empty() && std::find(g.in->left_out.begin(), g.in->left_out.end(), change->mod) != g.in->left_out.end();
        if (left_out) {
            // review: a mod the rolled-back plan leaves out has none of its registry edits applied, live ones neither
            change->restart = true;
            reason = "RestartRequired: this start runs on the rolled-back registry plan " + g.in->rollback.substr(0, 12) + ", which leaves mod " + change->mod +
                     " out (registry.json rollback.leftOut); its registry edits apply after kilnctl registry rollback --clear and a restart";
        } else if (edit.entry != registry::EntryOp::None || !edit.raw.empty()) reason = "live changes use mapped Kiln fields, not entry add/remove or raw game paths";
        else if (field && field->applies == registry::Applies::Restart) { change->restart = true; reason = "RestartRequired: " + edit.registry + "." + field->name + " is read at game start"; }
        else if (field && !g.in->decoded.count(edit.registry + "." + field->name)) { change->restart = true; reason = "RestartRequired: the base did not read " + edit.registry + "." + field->name + " at start"; }
        if (reason.empty()) return false;
        // the place too: the reload reply, kilnctl status and the launcher name the field that waits
        refused.push_back({change->mod, batch.source, batch.stage, edit.n, reason, "", edit.registry, edit.select.id,
                           field ? field->name : std::string(), !edit.raw.empty()});
        return true;
    });
    for (auto& edit : batch.edits) edit.per_entry = per_entry(*change);  // review4 NS6: entry points refuse per entry
    if (g.in->check) g.in->check(&batch, &refused);
    else registry::refuse_unmapped_edits(*g.in->regmap, *g.in->schema, &batch, &refused);  // review2 N7: production's guard
    if (out) { *out = {}; out->accepted = static_cast<uint32_t>(batch.edits.size()); out->refused = static_cast<uint32_t>(refused.size()); out->world = g.world; }
    if (report_text) {
        registry::json rows = registry::json::array();
        for (const auto& r : refused) rows.push_back({{"n", r.n}, {"reason", r.reason}});
        *report_text = registry::json{{"accepted", batch.edits.size()}, {"refused", rows}}.dump();
    }
    change->bytes += ir.size(); change->batches.push_back(std::move(batch));
    change->refused.insert(change->refused.end(), refused.begin(), refused.end());
    return KILN_OK;
}

namespace {
std::vector<uintptr_t> change_slots(uint64_t handle) {
    std::vector<uintptr_t> slots;
    const std::string source = "live:" + std::to_string(handle) + ":";
    for (const auto& [ref, fo] : g.in->result.fields) {
        const bool mine = std::any_of(fo.chain.begin(), fo.chain.end(), [&](const auto& row) { return row.source.rfind(source, 0) == 0; });
        if (!mine) continue;
        if (const auto it = g.fields.find(ref); it != g.fields.end())
            for (const auto& slot : it->second.slots) slots.push_back(slot.addr);
    }
    return slots;
}
uint32_t foreign_slots(const std::vector<uintptr_t>& slots) {
    std::set<uintptr_t> seen;
    for (uintptr_t addr : slots) { Slot slot; if (store_slot_copy(addr, &slot) && slot.foreign) seen.insert(addr); }
    return static_cast<uint32_t>(seen.size());
}
}

namespace {
// review2 S2: the worldLoad fields a change writes ("items.maxStack"), at most three, then a count.
std::string world_load_fields(const LiveState::Change& change) {
    std::set<std::string> names;
    for (const auto& batch : change.batches)
        for (const auto& edit : batch.edits) {
            const auto* reg = g.in->schema->registry(edit.registry);
            const auto* field = reg ? reg->field(edit.element.on ? edit.element.list : edit.field) : nullptr;
            if (field && field->applies == registry::Applies::WorldLoad) names.insert(edit.registry + "." + field->name);
        }
    std::string out;
    size_t n = 0;
    for (const auto& name : names) {
        if (n == 3) return out + " and " + std::to_string(names.size() - 3) + " more";
        out += (n++ ? ", " : "") + name;
    }
    return out;
}
}  // namespace

KilnResult registry_live_commit(KilnOwner owner, uint64_t handle, RegistryLiveReply* out) {
    if (g_transaction_active) return KILN_ERR_UNAVAILABLE;
    TransactionScope transaction;
    RegistryLiveReply reply;
    {
        std::lock_guard lock(store_mutex());
        if (!on_live_thread()) return KILN_ERR_WRONG_THREAD;
        LiveState::Change* change = nullptr;
        const KilnResult rc = change_of(owner, handle, &change);
        if (rc != KILN_OK) return rc;
        if (change->order) return KILN_ERR_UNAVAILABLE;
        reply.world = g.world; reply.refused = static_cast<uint32_t>(change->refused.size());
        for (const auto& b : change->batches) reply.accepted += static_cast<uint32_t>(b.edits.size());
        if (change->entry_restart && !change->refused.empty()) {
            const auto entry = std::find_if(change->refused.begin(), change->refused.end(), [](const auto& r) { return r.reason.starts_with("RestartRequired:"); });
            if (entry != change->refused.end()) reply.reason = entry->reason;
        }
        if (change->entry_restart && !per_entry(*change)) {
            reply.outcome = RegistryLiveOutcome::RestartRequired; reply.accepted = 0;
            g.refused.insert(g.refused.end(), change->refused.begin(), change->refused.end());
            g.changes.erase(handle); queue_change_report();
        } else if (!reply.accepted) {
            reply.outcome = change->restart ? RegistryLiveOutcome::RestartRequired : RegistryLiveOutcome::Refused;
            reply.reason = change->refused.empty() ? "the transaction has no edits" : change->refused.front().reason;
            g.refused.insert(g.refused.end(), change->refused.begin(), change->refused.end());
            g.changes.erase(handle);
            queue_change_report();
        } else if ((change->world && change->world != g.world) || (!g.world && (change->point == 2 || change->point == 4))) {
            reply.outcome = RegistryLiveOutcome::WorldRequired; reply.reason = "a world is required, or the draft's world has ended";
        } else {
            change->world = change->point == 1 ? 0 : g.world;
            change->order = ++g.next_order;
            compose_changes();
            const std::string prefix = "live:" + std::to_string(handle) + ":";
            const bool entries = per_entry(*change);
            std::set<std::pair<std::string, uint32_t>> refused_edits, entry_edits;
            std::vector<registry::Refusal> refused_rows;
            std::string refusal;
            // Parse/mapping refusals were counted on submit and are not in accepted. Only composition refusals
            // can reduce the accepted edit count here (exclusive, dependency or selector failures).
            std::set<std::pair<std::string, uint32_t>> submitted;
            for (const auto& row : change->refused) submitted.emplace(row.source, row.n);
            for (const auto& row : g.in->result.refused)
                if (row.source.rfind(prefix, 0) == 0 && !submitted.count({row.source, row.n})) {
                    refused_rows.push_back(row);
                    if (refusal.empty()) refusal = row.reason;
                }
            uint32_t entry_refusals = 0;
            for (const auto& row : refused_rows) {
                if (entries && !row.id.empty()) {  // review2 S3: an entry point drops that selected entry alone
                    for (auto& batch : change->batches)
                        if (batch.source == row.source)
                            for (auto& edit : batch.edits)
                                if (edit.n == row.n) edit.select.except.insert(row.id);
                    entry_edits.emplace(row.source, row.n);
                    change->refused.push_back(row);
                    ++entry_refusals;
                } else {
                    refused_edits.emplace(row.source, row.n);
                }
            }
            uint32_t newly_refused = static_cast<uint32_t>(refused_edits.size());
            if (newly_refused || entry_refusals) {
                for (auto& batch : change->batches)
                    std::erase_if(batch.edits, [&](const auto& edit) { return refused_edits.count({batch.source, edit.n}) != 0; });
                for (const auto& row : refused_rows)
                    if (refused_edits.count({row.source, row.n})) change->refused.push_back(row);
                compose_changes(); // a refused edit cannot activate later just because another handle was reverted
            }
            if (entry_refusals) {  // an edit whose every selected entry was refused applies nowhere: refused as a whole
                std::set<std::pair<std::string, uint32_t>> present;
                for (const auto& [ref, fo] : g.in->result.fields)
                    for (const auto& row : fo.chain)
                        if (row.source.rfind(prefix, 0) == 0) present.emplace(row.source, row.n);
                uint32_t empty = 0;
                for (auto& batch : change->batches)
                    std::erase_if(batch.edits, [&](const auto& edit) {
                        const bool gone = entry_edits.count({batch.source, edit.n}) && !present.count({batch.source, edit.n});
                        empty += gone;
                        return gone;
                    });
                if (empty) compose_changes();
                newly_refused += empty;
            }
            reply.refused += static_cast<uint32_t>(refused_edits.size()) + entry_refusals;
            reply.accepted -= std::min(reply.accepted, newly_refused);
            if (entries && !refusal.empty()) reply.reason = refusal;
            if (!reply.accepted) {
                const std::string mod = change->mod;
                g.changes.erase(handle); compose_changes();
                reply.outcome = RegistryLiveOutcome::Refused; reply.reason = refusal;
                g.refused.push_back({mod, prefix, registry::Stage::Alter, 0, refusal, ""});
                queue_change_report();
            } else if (!g.world) {
                reply.outcome = RegistryLiveOutcome::PendingWorld;
                reply.reason = "the copied change will apply when a world loads";
                queue_change_report();
            } else {
                const ApplyOutcome applied = apply_changes(handle);
                const auto slots = change_slots(handle);
                reply.skipped = foreign_slots(slots);
                // review4 NS6: an entry point's entries refused at binding (an inexact whole-number cost, ...) are left out
                // alone and counted; the rest applies. Nothing left at all: refused like a whole transaction.
                reply.refused += static_cast<uint32_t>(applied.entries.size());
                if (!applied.entries.empty() && reply.reason.empty()) reply.reason = applied.entries.front().reason;
                const bool nothing = !applied.entries.empty() && std::none_of(g.in->result.fields.begin(), g.in->result.fields.end(), [&](const auto& kv) {
                    return std::any_of(kv.second.chain.begin(), kv.second.chain.end(), [&](const ChainRow& row) { return row.source.rfind(prefix, 0) == 0; });
                });
                if (!applied.candidate.empty() || !applied.unbound.empty() || reply.skipped || nothing) {
                    const std::string why = !applied.candidate.empty() ? applied.candidate : !applied.unbound.empty() ? applied.unbound
                        : nothing ? applied.entries.front().reason : "another writer owns a target slot";
                    const std::string mod = change->mod;
                    g.changes.erase(handle);
                    // The rollback keeps the rows the first pass found (other changes it rejected) and charges no failed
                    // write to a retained change: this is the candidate's own failure (review4 NM1).
                    const ApplyOutcome rollback = apply_changes(0, true, false, false);
                    reply.outcome = rollback.error.empty() ? (reply.skipped ? RegistryLiveOutcome::Skipped : RegistryLiveOutcome::Refused) : RegistryLiveOutcome::Unverified;
                    reply.accepted = 0; ++reply.refused;
                    reply.reason = not_exact_reason(why) ? why : "the live transaction was not committed: " + why;
                    if (!rollback.error.empty()) reply.reason += "; rollback unverified: " + rollback.error;
                    g.refused.push_back({mod, "live:" + std::to_string(handle), registry::Stage::Alter, 0, reply.reason, ""});
                    queue_change_report();
                } else {
                    // pending only for this change's own fields: a drop table another change or a GameStart item removal's
                    // disabled slots wait for leaves an unrelated change Applied (registry-proof, 31.x)
                    const bool own_pending = std::any_of(g.pending_tables.begin(), g.pending_tables.end(), [&](const LiveState::Pending& p) {
                        const auto it = g.in->result.fields.find(p.field);
                        return it != g.in->result.fields.end() && std::any_of(it->second.chain.begin(), it->second.chain.end(),
                            [&](const ChainRow& row) { return row.source.rfind(prefix, 0) == 0; });
                    });
                    reply.outcome = own_pending ? RegistryLiveOutcome::PendingResource : RegistryLiveOutcome::Applied;
                    // review2 S2: a worldLoad field is in place before a world's save is read. Written into a running world by
                    // any point but WORLD_LOAD, it is applied now and named: its full effect starts at the next world load.
                    if (change->point != KILN_REGISTRY_LIVE_WORLD_LOAD)
                        if (const std::string late = world_load_fields(*change); !late.empty()) {
                            const std::string note = late + " applies at world load: written now, in full effect from the next world load "
                                                            "(this world's save was already read)";
                            if (reply.reason.empty()) reply.reason = note;
                            LOGI("registry (live): %s: live change %llu: %s", change->mod.c_str(), static_cast<unsigned long long>(handle), note.c_str());
                        }
                }
            }
        }
    }
    if (out) *out = reply;
    return KILN_OK;
}

KilnResult registry_live_revert(KilnOwner owner, uint64_t handle, RegistryLiveReply* out) {
    if (g_transaction_active) return KILN_ERR_UNAVAILABLE;
    TransactionScope transaction;
    RegistryLiveReply reply;
    {
        std::lock_guard lock(store_mutex());
        if (!on_live_thread()) return KILN_ERR_WRONG_THREAD;
        LiveState::Change* change = nullptr;
        const KilnResult rc = change_of(owner, handle, &change);
        if (rc != KILN_OK) return rc;
        const bool active = change->order != 0;
        const auto slots = change_slots(handle);
        g.changes.erase(handle);
        reply.world = g.world; reply.outcome = RegistryLiveOutcome::Reverted;
        if (active && g.world) {
            const ApplyOutcome reverted = apply_changes(0, false, false, false);
            reply.skipped = foreign_slots(slots);
            if (!reverted.error.empty()) { reply.outcome = RegistryLiveOutcome::Unverified; reply.reason = reverted.error; }
            else if (reply.skipped) { reply.outcome = RegistryLiveOutcome::Skipped; reply.reason = "another writer's bytes were left unchanged during revert"; }
        } else if (active) { compose_changes(); queue_change_report(); }
    }
    if (out) *out = reply;
    return KILN_OK;
}

void registry_live_owner_gone(KilnOwner owner) {
    if (!owner) return;
    std::lock_guard lock(g_retiring_mu); g_retiring.insert(owner);
}
void registry_live_world_begin(uint32_t kind) {
    // the drift probe's suspects, once per world: read outside the store's lock (the hook map is another service's)
    std::function<std::vector<std::string>()> suspects;
    {
        std::lock_guard lock(store_mutex());
        if (g.in) suspects = g.in->suspects;
    }
    // never call out under the store's lock (it is recursive: a caller holding it keeps the last world's suspects)
    const bool ask = suspects && !store_locked_here();
    std::vector<std::string> named = ask ? suspects() : std::vector<std::string>{};
    std::lock_guard lock(store_mutex()); g.world = ++g_next_world; g.world_kind = kind;
    if (ask) g.suspects = std::move(named);
    if (g.in && (!g.entry_failures.empty() || !g.removal_failures.empty())) {
        g.entry_failures.clear(); g.removal_failures.clear(); rebuild_entry_stage();
    }
}
void registry_live_world_stop() {
    std::lock_guard lock(store_mutex());
    g.world = 0; g.world_kind = KILN_WORLD_KINDS;
    std::erase_if(g.changes, [](const auto& item) { return item.second.world != 0; });
    g.fields.clear(); g.pending_tables.clear(); g.table_addrs.clear(); g.table_fields.clear();
    if (g.in) {
        compose_changes();
        queue_change_report();  // review2 N4: registry.json drops the ended world's live rows at the next pump
    }
}
RegistryLiveStatus registry_live_status() {
    std::lock_guard lock(store_mutex());
    RegistryLiveStatus status; status.world = g.world; status.world_kind = g.world_kind;
    for (const auto& [h, c] : g.changes) { if (!c.order) ++status.drafts; else if (!inactive_now(c)) ++status.active; }
    return status;
}

KilnResult registry_live_read(const std::string& reg_name, const std::string& id, const std::string& field, std::string* out,
                              const std::string& exclude_mod) {
    if (!out) return KILN_ERR_UNAVAILABLE;
    {
        std::lock_guard lock(store_mutex());
        if (!on_live_thread()) return KILN_ERR_WRONG_THREAD;
    }
    if (!ensure_live_base()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(store_mutex());
    if (!on_live_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g.in || g.in->prepare) return KILN_ERR_UNAVAILABLE;
    const auto* reg_spec = g.in->schema->registry(reg_name);
    const auto* spec = reg_spec ? reg_spec->field(registry::generic_path(field)) : nullptr;
    const size_t bracket = field.find('[');
    if (bracket != std::string::npos) {
        const size_t close = field.find(']', bracket + 1);
        if (close == std::string::npos || field.find('[', bracket + 1) != std::string::npos) return KILN_ERR_INVALID_ARGUMENT;
        const std::string parent = field.substr(0, bracket), index = field.substr(bracket + 1, close - bracket - 1);
        const std::string suffix = field.substr(close + 1);
        if (!reg_spec || (!spec && !suffix.empty()) || (!suffix.empty() && (suffix[0] != '.' || suffix.size() == 1))) return KILN_ERR_INVALID_ARGUMENT;
        std::string mapping_reason;
        if (spec && g.in->regmap->mapping(reg_name, registry::generic_path(field), &mapping_reason) != rmap::Mapping::Mapped) return KILN_ERR_UNAVAILABLE;
        std::string text;
        const KilnResult rc = registry_live_read(reg_name, id, parent, &text, exclude_mod);
        if (rc != KILN_OK) return rc;
        const auto list = registry::json::parse(text, nullptr, false);
        if (!list.is_array()) return KILN_ERR_INVALID_ARGUMENT;
        auto leaf = [&](const registry::json& entry) -> std::optional<registry::json> {
            if (suffix.empty()) return entry;
            const std::string key = suffix.substr(1);
            if (entry.is_object() && entry.contains(key)) return entry[key]; // schema struct leaves may contain dots
            const registry::json* value = &entry;
            size_t at = 0;
            while (at < key.size()) {
                size_t end = key.find('.', at); if (end == std::string::npos) end = key.size();
                if (!value->is_object() || !value->contains(key.substr(at, end - at))) return std::nullopt;
                value = &(*value)[key.substr(at, end - at)]; at = end + 1;
            }
            return *value;
        };
        if (index.empty() || index == "*") {
            auto values = registry::json::array();
            for (const auto& entry : list) { auto value = leaf(entry); if (!value) return KILN_ERR_UNAVAILABLE; values.push_back(*value); }
            *out = values.dump(); return KILN_OK;
        }
        size_t n = 0;
        for (char ch : index) { if (ch < '0' || ch > '9' || n > 4096) return KILN_ERR_INVALID_ARGUMENT; n = n * 10 + static_cast<size_t>(ch - '0'); }
        if (n >= list.size()) return KILN_ERR_NOT_FOUND;
        auto value = leaf(list[n]); if (!value) return KILN_ERR_UNAVAILABLE;
        *out = value->dump(); return KILN_OK;
    }
    if (!spec) return KILN_ERR_INVALID_ARGUMENT;
    std::string why;
    if (g.in->regmap->mapping(reg_name, field, &why) != rmap::Mapping::Mapped) return KILN_ERR_UNAVAILABLE;
    const registry::FieldRef ref{reg_name, id, field};
    if (std::find(g.in->result.removed.begin(), g.in->result.removed.end(), registry::EntryRef{reg_name, id}) != g.in->result.removed.end()) return KILN_ERR_NOT_FOUND;
    // review4 NS5: a runtime pass reads the value without the reading mod's own registry rows (its stage edits and live
    // changes), so a read-derived assignment restates the mod's intent instead of stacking on its earlier layers.
    const registry::FieldOutcome* own = nullptr;
    if (!exclude_mod.empty())
        if (auto it = g.in->result.fields.find(ref); it != g.in->result.fields.end() &&
            std::any_of(it->second.chain.begin(), it->second.chain.end(), [&](const ChainRow& r) {
                return r.kind == registry::WriterKind::Registry && r.mod == exclude_mod; }))
            own = &it->second;
    auto others = [&](const ChainRow& r) { return !(r.kind == registry::WriterKind::Registry && r.mod == exclude_mod); };
    if (own && (!g.world || spec->applies == registry::Applies::Restart)) {
        Value v = own->base;
        for (const ChainRow& r : own->chain)
            if (others(r)) v = registry::evaluate({r}, v, *spec);
        *out = v.to_json().dump();
        return KILN_OK;
    }
    // a read-only field (items.hasDamage, creatures.kind and arsenal) has no live binding: in a world too it reads the base
    if (!g.world || spec->applies == registry::Applies::Restart || spec->read_only) {
        if (auto it = g.in->result.fields.find(ref); it != g.in->result.fields.end()) { *out = it->second.result.to_json().dump(); return KILN_OK; }
        if (const auto added = g.in->result.definitions.find({reg_name, id}); added != g.in->result.definitions.end()) {
            const auto value = added->second.values.find(field);
            if (value == added->second.values.end()) return KILN_ERR_UNAVAILABLE;
            *out = value->second.to_json().dump(); return KILN_OK;
        }
        if (auto reg = g.in->base.registries.find(reg_name); reg != g.in->base.registries.end())
            for (const auto& entry : reg->second) if (entry.id == id) {
                auto value = entry.fields.find(field);
                if (value == entry.fields.end()) return KILN_ERR_UNAVAILABLE;
                *out = value->second.to_json().dump(); return KILN_OK;
            }
        return KILN_ERR_NOT_FOUND;
    }
    struct ReadScope {
        ReadScope() { g.reading = true; g.read_absent = false; g.read_fields.clear(); g.read_reason.clear(); g.read_evals.clear(); }
        ~ReadScope() { g.reading = false; g.read_capture = false; g.read_fields.clear(); g.read_evals.clear(); }
    } scope;
    registry::FieldOutcome identity;
    if (own) {
        for (const ChainRow& r : own->chain)
            if (r.kind == registry::WriterKind::Registry && others(r)) identity.chain.push_back(r);
        g.read_capture = true;
    }
    if (identity.chain.empty()) identity.chain.push_back({});
    bind(ref, identity);
    const auto it = g.read_fields.find(ref);
    // review2 N6: an id this world does not have is NOT_FOUND, as before a world; UNAVAILABLE is an unreadable field
    // dev.32 (review registry-r2a): an entry that has none of the field (a creature without the component, a perk without a
    // value) reads null in a world as its base does before one, so a runtime pass's typed read is 0, never a failed author
    if (it == g.read_fields.end() && g.read_absent) return *out = "null", KILN_OK;
    if (it == g.read_fields.end()) return g.read_reason == "the entry is not in this world" ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
    std::vector<SlotBytes> bytes(it->second.slots.size());
    for (size_t i = 0; i < bytes.size(); ++i) {
        const LiveSlot& slot = it->second.slots[i];
        if (own) {  // the slot's layers with the other mods' rows in place of the registry's layer
            const auto ev = g.read_evals.find(slot.addr);
            if (!store_effective_without_registry(slot.addr, slot.size, ev != g.read_evals.end() ? &ev->second : nullptr, bytes[i].data()))
                return KILN_ERR_UNAVAILABLE;
        } else if (!rd(slot.addr, bytes[i].data(), slot.size)) {
            return KILN_ERR_UNAVAILABLE;
        }
    }
    auto value = it->second.decode(bytes, nullptr);
    if (!value || (value->numeric() && !std::isfinite(value->as_double()))) return KILN_ERR_UNAVAILABLE;
    *out = value->to_json().dump();
    return KILN_OK;
}

}  // namespace kc
