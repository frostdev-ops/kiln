// SPDX-License-Identifier: MIT
// kiln.content registry stage: composition and conflicts (compose.h).
#include "compose.h"

#include "archive_journal.h"

#include <algorithm>
#include <cassert>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <deque>
#include <initializer_list>
#include <string_view>
#include <memory>
#include <tuple>
#include <unordered_set>

namespace kc::registry {

std::string report_value_text(const Value& v) {
    if (v.kind != Value::List) return v.text_of();
    return "list (" + std::to_string(v.list.size()) + " entries)";
}

const char* writer_kind_name(WriterKind k) {
    switch (k) {
        case WriterKind::ArchiveWriter: return "archiveWriter";
        case WriterKind::Drift: return "drift";
        case WriterKind::OldPatch: return "oldPatch";
        case WriterKind::OldPack: return "oldPack";
        case WriterKind::Code: return "code";
        case WriterKind::Check: return "check";
        default: return "registry";
    }
}

const char* certainty_name(Certainty c) {
    switch (c) {
        case Certainty::Named: return "named";
        case Certainty::BestEffort: return "bestEffort";
        default: return "certain";
    }
}

const char* line_kind_name(LineKind k) {
    switch (k) {
        case LineKind::Overridden: return "overridden";
        case LineKind::Composed: return "composed";
        case LineKind::ComposedOn: return "composedOn";
        case LineKind::Exclusive: return "exclusive";
        case LineKind::Dropped: return "dropped";
        default: return "yielded";
    }
}

namespace {

// One spelling per value as operator== sees it (1 and 1.0 are one number), for a hashed remove.
std::string value_key(const Value& v) {
    if (v.numeric()) {
        const double d = v.as_double() == 0 ? 0.0 : v.as_double();
        char buf[40];
        std::snprintf(buf, sizeof buf, "n%.17g", d);
        return buf;
    }
    switch (v.kind) {
        case Value::Bool: return v.b ? "b1" : "b0";
        case Value::Text: return "t" + v.t;
        case Value::List: return "l" + v.to_json().dump();
        case Value::Struct: return "s" + v.to_json().dump();
        default: return "z";
    }
}

// A list op on a list value. Flags (f->type Flags) stay a set in the schema's order: append adds names not there yet.
Value list_step(const ListOp op, const std::vector<Value>& values, const Value& v, const FieldSpec* f = nullptr) {
    std::vector<Value> l = v.kind == Value::List ? v.list : std::vector<Value>{};
    if (f && f->type == FieldType::Flags) {
        std::set<std::string> on;
        if (op != ListOp::Clear && op != ListOp::Replace)
            for (const Value& x : l) on.insert(x.t);
        if (op == ListOp::Replace || op == ListOp::Append)
            for (const Value& x : values) on.insert(x.t);
        if (op == ListOp::Remove)
            for (const Value& x : values) on.erase(x.t);
        std::vector<Value> names;
        for (const std::string& n : f->enum_values)
            if (on.count(n)) names.push_back(Value::text(n));
        return Value::of_list(std::move(names));
    }
    if (op == ListOp::Clear) l.clear();
    else if (op == ListOp::Replace) l = values;
    else if (op == ListOp::Append) l.insert(l.end(), values.begin(), values.end());
    else {
        std::unordered_set<std::string> gone;
        for (const Value& x : values) gone.insert(value_key(x));
        std::erase_if(l, [&](const Value& x) { return gone.count(value_key(x)) != 0; });
    }
    return Value::of_list(std::move(l));
}

// An element edit on a list value ("inputs[2].count", "inputs[*].count", "perks[1]"): op on that leaf (or that whole element) of
// the element(s), typed by el (the leaf's spec, or the element's). false + *why: refused (no such element, no number there,
// a result out of range).
bool element_step(const ElementRef& r, const Op& op, const FieldSpec& el, const Value& v, Value* next, std::string* why) {
    if (v.kind != Value::List) return *why = r.list + " has no list here", false;
    std::vector<Value> l = v.list;
    auto one = [&](Value& x) {
        Value* at = &x;
        Value fresh;
        size_t k = 0;
        if (!r.leaf.empty()) {
            if (x.kind != Value::Struct) return *why = r.list + ": its elements have no fields here", false;
            while (k < x.keys.size() && x.keys[k] < r.leaf) ++k;
            at = k < x.keys.size() && x.keys[k] == r.leaf ? &x.list[k] : &fresh;
        }
        if (op.relative() && !at->numeric()) return *why = el.name + " has no number to change here", false;
        Value nv;
        if (!op_result(op, *at, el, &nv, why)) return false;
        if (nv.numeric() && ((el.min && nv.as_double() < *el.min) || (el.max && nv.as_double() > *el.max)))
            return *why = el.name + ": the result " + nv.text_of() + " is outside the field's range", false;
        if (at != &fresh) {
            *at = std::move(nv);
        } else {  // a leaf the element did not have: in its sorted place
            x.keys.insert(x.keys.begin() + static_cast<std::ptrdiff_t>(k), r.leaf);
            x.list.insert(x.list.begin() + static_cast<std::ptrdiff_t>(k), std::move(nv));
        }
        return true;
    };
    if (r.all) {
        for (Value& x : l)
            if (!one(x)) return false;
    } else {
        if (r.index >= l.size()) return *why = r.list + " has no element " + std::to_string(r.index) + " here (it has " + std::to_string(l.size()) + ")", false;
        if (!one(l[r.index])) return false;
    }
    *next = Value::of_list(std::move(l));
    return true;
}

}  // namespace

Value evaluate(const std::vector<ChainRow>& rows, const Value& below, const FieldSpec& f) {
    Value v = below;
    for (const ChainRow& r : rows) {
        if (r.element.on) {
            Value nv;
            std::string why;
            if (r.element_spec && element_step(r.element, r.op, *r.element_spec, v, &nv, &why)) v = std::move(nv);
            continue;
        }
        v = r.list == ListOp::None ? apply_op(r.op, v, f) : list_step(r.list, r.list_values, v, &f);
    }
    return v;
}

namespace {

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

struct EntryState {
    std::string id;
    std::set<std::string> traits;
    std::map<std::string, Value> base;
    bool removed = false;
    std::string removed_by, removed_in;
    bool added = false;  // a mod's entry: its raw members are its like entry's
    std::string like;
    bool retired = false;  // the ids ledger retired this id: never added again, never edited
    std::vector<std::string> archive;  // BaseModel::Entry::archive: the archive-writer packages that wrote it (a game entry only)
    std::map<std::string, std::set<std::string>> archive_fields;  // BaseModel::Entry::archive_fields: packages named per field
    // The packages named on `field`, in run order: each whole-entry package, and each per-field one whose fields hold it.
    std::vector<std::pair<std::string, bool>> archive_for(const std::string& field) const {
        std::vector<std::pair<std::string, bool>> out;
        for (const std::string& who : archive) {
            const auto it = archive_fields.find(who);
            if (it == archive_fields.end()) out.emplace_back(who, false);
            else if (it->second.count(field)) out.emplace_back(who, true);
        }
        return out;
    }
};

// An archive-writer package's row of a field: what the archive holds, which is the base (D1). Never "another writer" for
// exclusive or yield (note_row), but the row a registry edit's line names when it is the one just below.
ChainRow archive_row(const std::string& who, const Value& base, bool by_field) {
    ChainRow r;
    r.kind = WriterKind::ArchiveWriter;
    r.who = who;
    r.archive_field = by_field;
    r.op.set = true;
    r.op.value = base;
    r.after = base;
    return r;
}

// Entry maps looked up by (registry, id) views: no string is copied to ask.
struct EntryKey {
    std::string_view registry, id;
};
struct EntryLess {
    using is_transparent = void;
    static std::pair<std::string_view, std::string_view> k(const EntryRef& e) { return {e.registry, e.id}; }
    static std::pair<std::string_view, std::string_view> k(const EntryKey& e) { return {e.registry, e.id}; }
    template <class A, class B>
    bool operator()(const A& a, const B& b) const { return k(a) < k(b); }
};

// The stage's state. A final's own view is an overlay over the post-alter state (parent): reads fall through, writes stay in
// the overlay, so a mod's final costs what it touches, not a copy of everything.
struct State {
    const State* parent = nullptr;
    std::map<std::string, std::vector<EntryState>> regs;  // the root's entries, in each registry's order
    std::map<EntryRef, size_t, EntryLess> at;             // the root's index into regs
    std::map<EntryRef, EntryState, EntryLess> over;       // an overlay's changed entries
    std::map<FieldRef, FieldOutcome> fields;              // this level's fields (an overlay: the ones it touched)

    const State* root() const {
        const State* r = this;
        while (r->parent) r = r->parent;
        return r;
    }
    const EntryState* entry(const std::string& reg, const std::string& id) const {
        if (parent) {
            const auto it = over.find(EntryKey{reg, id});
            return it != over.end() ? &it->second : parent->entry(reg, id);
        }
        const auto it = at.find(EntryKey{reg, id});
        return it == at.end() ? nullptr : &regs.find(reg)->second[it->second];
    }
    EntryState* entry(const std::string& reg, const std::string& id) {
        if (parent) {
            const auto it = over.find(EntryKey{reg, id});
            if (it != over.end()) return &it->second;
            const EntryState* p = parent->entry(reg, id);
            return p ? &over.emplace(EntryRef{reg, id}, *p).first->second : nullptr;
        }
        const auto it = at.find(EntryKey{reg, id});
        return it == at.end() ? nullptr : &regs.find(reg)->second[it->second];
    }
    // every entry of a registry, in its order, as this level sees it
    void each_entry(const std::string& reg, const std::function<void(const EntryState&)>& fn) const {
        if (parent) {
            parent->each_entry(reg, [&](const EntryState& e) {
                const auto it = over.find(EntryKey{reg, e.id});
                fn(it != over.end() ? it->second : e);
            });
            return;
        }
        if (const auto it = regs.find(reg); it != regs.end())
            for (const EntryState& e : it->second) fn(e);
    }
    const FieldOutcome* find_field(const FieldRef& f) const {
        const auto it = fields.find(f);
        if (it != fields.end()) return &it->second;
        return parent ? parent->find_field(f) : nullptr;
    }
    Value base_of(const FieldRef& f) const {
        const EntryState* e = entry(f.registry, f.id);
        if (!e) return {};
        const auto b = e->base.find(f.field);
        return b == e->base.end() ? Value{} : b->second;
    }
    Value current(const FieldRef& f) const {
        const FieldOutcome* fo = find_field(f);
        return fo ? fo->result : base_of(f);
    }
    FieldOutcome& touch(const FieldRef& f) {
        auto [it, fresh] = fields.try_emplace(f);
        if (fresh) {
            if (const FieldOutcome* p = parent ? parent->find_field(f) : nullptr) it->second = *p;
            else {
                it->second.result = it->second.base = base_of(f);
                // the packages that wrote the entry (or, from format 2's member paths, this field) before Kiln, below every
                // Kiln writer (archive_journal.h). The journal names the write, not the value: no package is the field's
                // winner (with no Kiln writer the archive's value, the base, stands)
                const EntryState* e = entry(f.registry, f.id);
                if (e && !e->added && it->second.base.kind != Value::None)
                    for (const auto& [who, by_field] : e->archive_for(f.field)) it->second.chain.push_back(archive_row(who, it->second.base, by_field));
            }
        }
        return it->second;
    }
    void add_entry(const std::string& reg, EntryState e) {  // the root only: entries are added in stage add
        auto& list = regs[reg];
        at[{reg, e.id}] = list.size();
        list.push_back(std::move(e));
    }

    // The Advanced tier: the member at raw path key of an entry on this build, through the base model (once per place; an
    // added entry through its like entry). nullptr + *why: it does not resolve there.
    std::map<FieldRef, std::optional<RawMember>> raw_members;
    std::map<FieldRef, std::string> raw_why;
    bool cached_raw(const FieldRef& ref, const RawMember** m, std::string* why) const {
        if (const auto it = raw_members.find(ref); it != raw_members.end()) {
            *m = it->second ? &*it->second : nullptr;
            if (!it->second) *why = raw_why.at(ref);
            return true;
        }
        return parent && parent->cached_raw(ref, m, why);
    }
    const RawMember* raw_member(const BaseModel& b, const std::string& reg, const std::string& id, const std::string& key, std::string* why) {
        const FieldRef ref{reg, id, raw_field(key)};
        const RawMember* known = nullptr;
        if (cached_raw(ref, &known, why)) return known;
        std::optional<RawMember> m;
        std::string w;
        std::string game = id;  // the game entry whose data it is
        for (int hops = 0; hops < 64; ++hops) {
            const EntryState* e = entry(reg, game);
            if (!e || !e->added) break;
            if (e->like.empty()) {
                w = "an entry added without \"like\" has no game data for raw paths";
                game.clear();
                break;
            }
            game = e->like;
        }
        if (!game.empty()) {
            if (!b.raw) w = "this Kiln resolves no raw paths here";
            else if (!(m = b.raw(reg, game, key, &w)) && w.empty()) w = "no such member";
        }
        if (m) {
            if (EntryState* e = entry(reg, id)) e->base.try_emplace(ref.field, m->value);
        } else {
            raw_why[ref] = w;
            *why = w;
        }
        const auto& slot = raw_members[ref] = std::move(m);
        return slot ? &*slot : nullptr;
    }
};

class StateView final : public View {
public:
    explicit StateView(const State& s) : s_(s) {}
    bool exists(const std::string& reg, const std::string& id) const override {
        const EntryState* e = s_.entry(reg, id);
        return e && !e->removed;
    }
    std::optional<Value> get(const FieldRef& f) const override {
        if (!exists(f.registry, f.id)) return std::nullopt;
        Value v = s_.current(f);
        if (v.kind == Value::None) return std::nullopt;
        return v;
    }
    std::vector<std::string> ids(const std::string& reg) const override {
        std::vector<std::string> out;
        s_.each_entry(reg, [&](const EntryState& e) {
            if (!e.removed) out.push_back(e.id);
        });
        return out;
    }
    bool has_trait(const std::string& reg, const std::string& id, const std::string& t) const override {
        const EntryState* e = s_.entry(reg, id);
        return e && e->traits.count(t);
    }

private:
    const State& s_;
};

struct Ctx {
    const Schema& schema;
    Result& out;
    const BaseModel& base;
    // (registry, Kiln field) -> the raw keys behind it that Kiln converts (BaseModel::raw_cover): one place on two scales
    std::map<std::pair<std::string, std::string>, std::vector<std::string>> converted;
    // references: why an entry's add was refused (the cause a reference to it names), and who added each entry
    std::map<EntryRef, std::string> refused_add;
    struct Adder {
        std::string mod, source;
        uint32_t n = 0;
    };
    std::map<EntryRef, Adder> adders;
    // BaseModel::raw_cover with its keys as raw paths are keyed (cover_key)
    std::map<std::string, std::map<std::string, BaseModel::Cover>> cover;
    std::string failed_id;  // the entry an edit failed on, for its refusal's place (refuse() takes and clears it)
    bool preserve_removals = false; // later field-only turns cannot invalidate established drop-group exclusions
    std::set<std::string> protected_items;
};
std::map<EntryRef, std::string> unsafe_item_removals(const Ctx& c, const State& s, const std::string& table_id = {});

// A "where" condition on one value: ">=1", "<=2", "!=x", "==x", ">1", "<1", or a plain value (equal; names ignore case).
bool cond_ok(const Value& v, const json& c) {
    if (c.is_boolean()) return v.kind == Value::Bool && v.b == c.get<bool>();
    if (c.is_number()) return v.numeric() && v.as_double() == c.get<double>();
    if (!c.is_string()) return false;
    const std::string s = c.get<std::string>();
    static const char* const ops[] = {">=", "<=", "!=", "==", ">", "<"};
    for (const char* op : ops) {
        const size_t n = std::char_traits<char>::length(op);
        if (s.compare(0, n, op) != 0) continue;
        const std::string rest = s.substr(n);
        char* end = nullptr;
        const double x = std::strtod(rest.c_str(), &end);
        const bool num = !rest.empty() && end && *end == 0 && v.numeric();
        const double a = v.as_double();
        if (n == 2 && op[0] == '>') return num && a >= x;
        if (n == 2 && op[0] == '<') return num && a <= x;
        if (op[0] == '>') return num && a > x;
        if (op[0] == '<') return num && a < x;
        const bool eq = num ? a == x : (v.kind == Value::Text && lower(v.t) == lower(rest));
        return op[0] == '=' ? eq : !eq;
    }
    return v.kind == Value::Text && lower(v.t) == lower(s);
}

// The entries a selector names on view v. false: refused (*why). An exact id another edit removed: *dropped_by, true.
bool select(const View& v, const Schema& schema, const State& s, const std::string& reg, const Selector& sel, std::vector<std::string>* ids,
            std::string* why, std::string* dropped_by) {
    ids->clear();
    if (sel.where.is_object())
        for (const auto& [k, c] : sel.where.items()) {
            if (k.find('[') != std::string::npos) return *why = "where: \"" + k + "\" is a field of a list's elements; where takes the entry's own fields", false;
            if (!schema.registry(reg)->field(k)) return *why = "where: " + reg + " has no field \"" + k + "\"", false;
        }
    // D3: the old debug-name forms keep working: "Material_T1_Wood_Logs" names "game:material_t1_wood_logs"
    // Kiln ids are lower case: an exact id selects without case, as a glob does
    const bool upper = std::any_of(sel.id.begin(), sel.id.end(), [](char ch) { return ch >= 'A' && ch <= 'Z'; });
    const std::string id = sel.id.find(':') == std::string::npos && sel.id != "*" ? "game:" + lower(sel.id) : upper ? lower(sel.id) : sel.id;
    auto keep = [&](const std::string& e) {
        if (sel.except.count(e)) return false;
        if (sel.where.is_object())
            for (const auto& [k, c] : sel.where.items()) {
                const auto x = v.get({reg, e, k});
                if (!x || !cond_ok(*x, c)) return false;
            }
        for (const std::string& t : sel.has)
            if (!v.has_trait(reg, e, t)) return false;
        for (const std::string& t : sel.has_not)
            if (v.has_trait(reg, e, t)) return false;
        return true;
    };
    if (id.find_first_of("*?") == std::string::npos) {
        if (!v.exists(reg, id)) {
            const EntryState* e = s.entry(reg, id);
            if (e && !e->removed_in.empty()) return *why = id + " was removed by game build " + e->removed_in + "; this edit was not applied", false;
            if (e && e->retired) return *why = id + " is retired (ids are never reused); this edit was not applied", false;
            if (e && e->removed && !e->retired) return *dropped_by = e->removed_by, true;
            return *why = "no entry " + id + " in " + reg, false;
        }
        if (keep(id)) ids->push_back(id);
        return true;
    }
    // a glob: only the entries whose id starts with its literal part (ids are lower case), then in the registry's order
    const std::string prefix = id.substr(0, id.find_first_of("*?"));
    const State* root = s.root();
    std::vector<std::pair<size_t, const std::string*>> hits;
    for (auto it = root->at.lower_bound(EntryKey{reg, prefix}); it != root->at.end() && it->first.registry == reg && it->first.id.compare(0, prefix.size(), prefix) == 0; ++it)
        if (v.exists(reg, it->first.id) && glob_ci(id, it->first.id) && keep(it->first.id)) hits.emplace_back(it->second, &it->first.id);
    std::sort(hits.begin(), hits.end());
    ids->reserve(hits.size());
    for (const auto& [at, e] : hits) ids->push_back(*e);
    return true;
}

// The chain's summary (FieldOutcome::Summary) after its last row was pushed.
void note_row(FieldOutcome& fo) {
    const int k = static_cast<int>(fo.chain.size()) - 1;
    const ChainRow& r = fo.chain[static_cast<size_t>(k)];
    // its value is base (D1), or a replayed package's recorded value standing in for its archive write (D6): never "another
    // writer" for exclusive or yield
    if (r.kind == WriterKind::ArchiveWriter || r.stand_in) return;
    auto& sm = fo.summary;
    auto differs = [&](int at) {
        const ChainRow& l = fo.chain[static_cast<size_t>(at)];
        return l.mod.empty() || r.mod.empty() || l.mod != r.mod;
    };
    if (sm.last >= 0 && differs(sm.last)) sm.prev_other = sm.last;
    sm.last = k;
    if (r.exclusive) {
        if (sm.last_excl >= 0 && differs(sm.last_excl)) sm.prev_excl_other = sm.last_excl;
        sm.last_excl = k;
    }
}
void resummarize(FieldOutcome& fo) {
    std::vector<ChainRow> rows = std::move(fo.chain);
    fo.chain.clear();
    fo.summary = {};
    for (ChainRow& r : rows) {
        fo.chain.push_back(std::move(r));
        note_row(fo);
    }
}
// The latest row of another writer than mod (not the archive writer's; exclusive ones only, or all), or nullptr.
const ChainRow* other_row(const FieldOutcome* fo, const std::string& mod, bool exclusive) {
    if (!fo) return nullptr;
    const int i = exclusive ? fo->summary.last_excl : fo->summary.last, j = exclusive ? fo->summary.prev_excl_other : fo->summary.prev_other;
    if (i < 0) return nullptr;
    const ChainRow& r = fo->chain[static_cast<size_t>(i)];
    if (r.mod.empty() || r.mod != mod) return &r;
    return j < 0 ? nullptr : &fo->chain[static_cast<size_t>(j)];
}

// Text in one piece (conflict lines are many): no temporaries.
std::string cat(std::initializer_list<std::string_view> parts) {
    size_t n = 0;
    for (std::string_view p : parts) n += p.size();
    std::string s;
    s.reserve(n);
    for (std::string_view p : parts) s.append(p);
    return s;
}

// One write of an edit into one place: a Kiln field, or a raw member (the Advanced tier).
// The edit's op and values are shared by its targets (one per entry), never copied per entry.
struct Target {
    FieldRef ref;
    const FieldSpec* spec = nullptr;  // the schema's field, or the raw member's own spec on this build (both outlive the stage)
    const Op* edit_op = nullptr;      // the edit's op...
    Op own_op;                        // ...or a raw target's, typed for its member
    bool own = false;
    ListOp list = ListOp::None;
    const std::vector<Value>* list_values = nullptr;
    std::string raw;         // the raw path as written (an Advanced-tier write), else empty
    std::string conv_field;  // a raw member behind a Kiln field Kiln converts: that field (one place, two scales)
    const ElementRef* element = nullptr;  // an element edit of the list at ref ("inputs[2].count")
    ElementRef own_element;               // ...or a raw element of a covered list ("perkIds[2]" -> perks[2])
    bool own_el = false;
    std::string path;                     // ...as the edit names it
    std::shared_ptr<const FieldSpec> element_spec;  // ...its leaf's spec (or the plain element's)

    const Op& op() const { return own ? own_op : *edit_op; }
    const std::vector<Value>& values() const {
        static const std::vector<Value> none;
        return list_values ? *list_values : none;
    }
    const ElementRef* elem() const { return own_el ? &own_element : element; }
};

bool absolute(const Target& t) { return t.elem() ? false : t.list == ListOp::None ? t.op().set : (t.list == ListOp::Clear || t.list == ListOp::Replace); }
bool absolute(const ChainRow& r) { return r.element.on ? false : r.list == ListOp::None ? r.op.set : (r.list == ListOp::Clear || r.list == ListOp::Replace); }
Certainty certainty_of(WriterKind k) {
    return k == WriterKind::ArchiveWriter ? Certainty::Named : k == WriterKind::Drift ? Certainty::BestEffort : Certainty::Certain;
}

// The place a target names, for messages: "items[game:x].maxStack", "items[game:x].damageSetup.dmgMod (raw)".
std::string place_text(const Target& t) {
    if (t.elem()) return t.ref.registry + "[" + t.ref.id + "]." + t.path;
    return t.raw.empty() || !is_raw_field(t.ref.field) ? t.ref.text() : t.ref.registry + "[" + t.ref.id + "]." + t.raw + " (raw)";
}

// The value an edit makes of cur. false: refused (*why).
bool next_value(const Target& t, const Value& cur, Value* next, std::string* why) {
    const FieldSpec& f = *t.spec;
    const size_t cap = f.max_items ? f.max_items : kMaxListValues;
    auto list_cap = [&]() {
        if (next->kind != Value::List) return true;
        if (next->list.size() > cap)
            return *why = f.name + " would hold " + std::to_string(next->list.size()) + " values; it holds at most " + std::to_string(cap) +
                          (f.max_items ? "" : " (the stage's cap for a list)"), false;
        if (f.ops == FieldOps::Indexed && f.fixed_length && next->list.size() != f.fixed_length)
            return *why = f.name + " holds exactly " + std::to_string(f.fixed_length) + " values", false;
        return true;
    };
    if (t.elem()) return element_step(*t.elem(), t.op(), *t.element_spec, cur, next, why);
    if (t.list == ListOp::None) {
        if (t.op().relative() && !cur.numeric()) return *why = f.name + " has no number to change here", false;
        if (!op_result(t.op(), cur, f, next, why)) return false;
        if (!list_cap()) return false;
        if (next->numeric() && ((f.min && next->as_double() < *f.min) || (f.max && next->as_double() > *f.max)))
            return *why = f.name + ": the result " + next->text_of() + " is outside the field's range", false;
        return true;
    }
    *next = list_step(t.list, t.values(), cur, &f);
    return list_cap();
}

struct Who {
    std::string mod, source;
    Stage stage;
};

// One edit's writes over its entries: all or nothing. report: false while a mod's final runs on its own scratch copy.
void refuse(Ctx& c, const Who& w, const Edit& e, const std::string& why);
bool apply_targets(Ctx& c, State& s, const Who& w, const Edit& e, const std::vector<Target>& ts, bool report, std::string* why) {
    struct Step {
        const Target* t = nullptr;
        Value next;
        bool yielded = false, line = false;
        LineKind kind = LineKind::Overridden;
        std::string other;
        Certainty certainty = Certainty::Certain;
        bool by_field = false;  // the archive-writer package below is named on this field (format 2), not the entry
        std::optional<FieldRef> supersede;  // the other scale's chain this absolute value overrides
        std::string over_who, over_place;
        Certainty over_cert = Certainty::Certain;
    };
    auto refuse_line = [&](const Target& t, const std::string& winner, Certainty cert, const std::string& text) {
        if (report) c.out.lines.push_back({LineKind::Exclusive, t.ref, winner, w.mod, text, cert});
        *why = text;
        c.failed_id = t.ref.id;
        return false;
    };
    std::vector<Step> steps;
    size_t refused_targets = 0;
    for (const Target& t : ts) {
        const bool ok = [&]() -> bool {
        // one place on two scales (a raw member behind a field Kiln converts). D1 (work-bb's ruling): an absolute value wins over
        // the other scale's writers, reported (their chain is superseded); a relative change would have to be converted
        // through the field's mapping, which composition does not know here: refused
        const ChainRow* scale = nullptr;
        std::string scale_place;
        FieldRef scale_ref;
        if (!t.conv_field.empty()) {
            scale_ref = {t.ref.registry, t.ref.id, t.conv_field};
            const FieldOutcome* o = s.find_field(scale_ref);
            if (o && !o->chain.empty()) scale = &o->chain.back(), scale_place = t.ref.registry + "." + t.conv_field;
        } else if (t.raw.empty()) {
            const auto conv = c.converted.find({t.ref.registry, t.ref.field});
            if (conv != c.converted.end())
                for (const std::string& key : conv->second) {
                    scale_ref = {t.ref.registry, t.ref.id, raw_field(key)};
                    const FieldOutcome* o = s.find_field(scale_ref);
                    if (!o || o->chain.empty()) continue;
                    scale = &o->chain.back(), scale_place = "raw path " + (o->raw.empty() ? key : o->raw);
                    break;
                }
        }
        Step st;
        st.t = &t;
        if (scale) {
            if (!absolute(t))
                return refuse_line(t, scale->who, certainty_of(scale->kind),
                                   "Conflict (one place, two scales): " + place_text(t) + " and " + scale_place + " are one place in the game, which Kiln converts " +
                                       "between them; " + scale->who + " writes " + scale_place + " there and no conversion of a relative change is known here: " +
                                       w.mod + "'s relative edit is refused (a value would win)");
            st.supersede = scale_ref;
            st.over_who = scale->who;
            st.over_place = scale_place;
            st.over_cert = certainty_of(scale->kind);
        }
        const FieldOutcome* fo = s.find_field(t.ref);
        const Value cur = fo ? fo->result : s.base_of(t.ref);
        // another writer: another mod's registry edit, an old path of another mod, an archive-writer package (the mod's own
        // older path is no conflict with itself)
        auto other = [&](const ChainRow& r) { return r.mod != w.mod || r.mod.empty(); };
        const ChainRow* prev = fo && !fo->chain.empty() ? &fo->chain.back() : nullptr;  // the row just before this edit
        ChainRow seed;  // a field not touched yet: its archive rows come with the touch, so the last package is the row below
        if (!fo)
            if (const EntryState* es = s.entry(t.ref.registry, t.ref.id); es && !es->added && cur.kind != Value::None)
                if (const auto named = es->archive_for(t.ref.field); !named.empty())
                    seed = archive_row(named.back().first, cur, named.back().second), prev = &seed;
        const ChainRow* last_other = other_row(fo, w.mod, false);  // the latest other writer that is not the archive writer (base, D1)
        const ChainRow* excl = other_row(fo, w.mod, true);         // the latest other writer whose edit is exclusive
        const bool earlier = last_other != nullptr;                // an earlier writer for yield: not the archive writer, not the mod itself
        if (e.yield && earlier) {
            st.yielded = true;
            steps.push_back(std::move(st));
            return true;
        }
        if (!next_value(t, cur, &st.next, why)) return c.failed_id = t.ref.id, *why = t.ref.id + ": " + *why, false;
        if (last_other && (e.exclusive || excl)) {
            const bool agreed = absolute(t) && absolute(*last_other) && st.next == cur;
            const ChainRow& named = e.exclusive ? *last_other : *excl;  // the conflict is with the exclusive edit
            if (!agreed)
                return refuse_line(t, named.who, certainty_of(named.kind),
                                   "Conflict (exclusive): " + named.who + (e.exclusive ? " also writes " : " wrote ") + place_text(t) +
                                       (e.exclusive ? "; the later edit is refused" : " exclusively; the later edit is refused"));
        }
        if (c.preserve_removals && t.ref.registry == "drops" && t.ref.field == "entries") {
            State candidate; candidate.parent = &s;
            candidate.touch(t.ref).result = st.next;
            const auto bad = unsafe_item_removals(c, candidate, t.ref.id);
            if (!bad.empty()) {
                c.failed_id = t.ref.id;
                *why = "drops " + t.ref.id + ": this field target would invalidate a safe drop group alongside removal of " +
                       bad.begin()->first.id + "; the field target was not applied and the established removal is retained";
                return false;
            }
        }
        if (prev && other(*prev)) {  // a line when this edit follows another writer's (not after the mod's own)
            if (absolute(t) || (t.elem() && t.list == ListOp::None && t.op().set)) {  // an element's value set is a value too (D1)
                st.line = !(st.next == cur);
                st.kind = LineKind::Overridden;
            } else {
                st.line = true;
                st.kind = absolute(*prev) ? LineKind::ComposedOn : LineKind::Composed;
            }
            st.other = prev->who;
            st.certainty = certainty_of(prev->kind);
            st.by_field = prev->archive_field;
        }
        steps.push_back(std::move(st));
        return true;
        }();
        if (ok) continue;
        if (!e.per_entry) return false;
        // review4 NS6: this entry alone is refused (named), the edit goes on with the others
        refuse(c, w, e, *why);
        ++refused_targets;
    }
    if (e.per_entry && refused_targets && steps.empty()) return true;  // every entry was refused, each named already
    for (Step& st : steps) {
        const Target& t = *st.t;
        if (st.yielded) {
            if (report)
                c.out.lines.push_back({LineKind::Yielded, t.ref, "", w.mod, "yielded: " + place_text(t) + " was set by an earlier writer; " + w.mod + "'s edit is not applied there"});
            continue;
        }
        if (st.supersede) {  // the other scale's writers are overridden: their chain goes, the line says it
            if (report)
                c.out.lines.push_back({LineKind::Overridden, t.ref, w.mod, st.over_who,
                                       cat({place_text(t), ": ", w.mod, " sets ", report_value_text(st.next), " over ", st.over_who, "'s ", st.over_place,
                                            " (one place in the game on two scales; the later value wins)"}),
                                       st.over_cert});
            s.fields.erase(*st.supersede);
        }
        FieldOutcome& fo = s.touch(t.ref);
        if (is_raw_field(t.ref.field)) {
            if (fo.raw.empty()) fo.raw = t.raw;
            if (!fo.raw_spec) fo.raw_spec = std::make_shared<const FieldSpec>(*t.spec);
        }
        const Value before = fo.result;
        ChainRow r;
        r.kind = WriterKind::Registry;
        r.raw = t.raw;
        r.mod = r.who = w.mod;
        r.stage = w.stage;
        r.source = w.source;
        r.n = e.n;
        r.op = t.op();
        r.list = t.list;
        r.list_values = t.values();
        if (t.elem()) r.element = *t.elem(), r.element_spec = t.element_spec;
        r.exclusive = e.exclusive;
        r.stand_in = e.stand_in;
        r.after = st.next;
        fo.chain.push_back(std::move(r));
        note_row(fo);
        fo.result = st.next;
        fo.winner = w.mod;
        if (report && st.line) {
            const std::string place = place_text(t), was = report_value_text(before), now = report_value_text(st.next);
            // an archive-writer package below: the journal names the entry's resource (format 1) or this field (format 2's
            // member paths), never the value it wrote (archive_journal.h)
            std::string text = st.certainty == Certainty::Named
                                   ? cat({place, ": ", archive_writer_label(st.other), st.by_field ? " wrote this field" : " wrote this entry",
                                          " in the game's archive before Kiln; ", w.mod, "'s edit wins (D1): ", was, " -> ", now})
                               : st.kind == LineKind::Overridden ? cat({place, ": ", w.mod, " sets ", now, " over ", st.other, "'s ", was, " (the later one wins)"})
                               : st.kind == LineKind::ComposedOn ? cat({place, ": ", w.mod, "'s change composed on ", st.other, "'s value: ", was, " -> ", now})
                                                                 : cat({place, ": ", w.mod, "'s change stacks on ", st.other, "'s: ", was, " -> ", now});
            c.out.lines.push_back({st.kind, t.ref, w.mod, st.other, std::move(text), st.certainty});
        }
    }
    return true;
}

// A Kiln field edit's writes over its entries.
std::vector<Target> field_targets(const FieldSpec& f, const Edit& e, const std::vector<std::string>& ids) {
    std::vector<Target> ts;
    std::shared_ptr<FieldSpec> el;  // an element edit: f is the list; the leaf's spec, or the plain element's (one for all)
    if (e.element.on) {
        if (!e.element.leaf.empty()) {
            if (const FieldSpec* leaf = f.element_field(e.element.leaf)) el = std::make_shared<FieldSpec>(*leaf);
        } else {
            el = std::make_shared<FieldSpec>(f);
            el->type = f.element;
            el->element_fields.clear();
        }
        if (el) el->name = e.field;
    }
    ts.reserve(ids.size());
    for (const std::string& id : ids) {
        Target t;
        t.ref = {e.registry, id, e.element.on ? e.element.list : e.field};
        t.spec = &f;
        if (e.element.on) t.element = &e.element, t.path = e.field, t.element_spec = el;
        t.edit_op = &e.op;
        t.list = e.list;
        t.list_values = &e.list_values;
        ts.push_back(std::move(t));
    }
    return ts;
}

// A covered member as a cover key (BaseModel::raw_cover): the regmap may spell it as the game does ("maxStackSize"); the
// stage keys it like a raw path ("maxstacksize").
std::string cover_key(const std::string& k) {
    std::string key, err;
    return raw_key(k, &key, &err) ? key : lower(k);
}

// One raw path of one entry as a write: a covered member is its Kiln field's place (an element of a covered list is an
// element edit on that list's chain); any other member is resolved on this build and typed by its own spec. false + *why:
// refused.
bool raw_target(Ctx& c, State& s, const std::string& reg, const std::string& id, const RawWrite& rw, Target* t, std::string* why) {
    const auto covers = c.cover.find(reg);
    auto covered = [&](const std::string& key) -> const BaseModel::Cover* {
        if (covers == c.cover.end()) return nullptr;
        const auto k = covers->second.find(key);
        return k == covers->second.end() ? nullptr : &k->second;
    };
    const BaseModel::Cover* cover = covered(rw.key);
    t->raw = rw.path;
    std::string err;
    if (!cover)  // inside a covered member: "perkids[2]" in perkids, "list[1].x" in list
        for (size_t at = rw.key.find('['); at != std::string::npos; at = rw.key.find('[', at + 1)) {
            const BaseModel::Cover* outer = covered(rw.key.substr(0, at));
            if (!outer) continue;
            const FieldSpec* lf = c.schema.registry(reg)->field(outer->field);
            const size_t close = rw.key.find(']', at);
            const std::string rest = close == std::string::npos ? "" : rw.key.substr(close + 1);
            if (outer->converted || !lf || lf->type != FieldType::List || !rest.empty() || close == std::string::npos)
                return *why = id + ": raw path \"" + rw.path + "\" is inside Kiln field " + reg + "." + outer->field +
                              (outer->converted ? ", which Kiln converts" : "") + ": edit " + outer->field + " (an element: " + outer->field + "[n]) instead", false;
            if (lf->read_only || lf->ops == FieldOps::None) return *why = id + ": raw path \"" + rw.path + "\" is in " + reg + "." + outer->field + ", which is read-only", false;
            t->own_el = true;
            t->own_element.on = true;
            t->own_element.list = outer->field;
            t->own_element.index = static_cast<uint32_t>(std::stoul(rw.key.substr(at + 1, close - at - 1)));  // raw_key: at most 9 digits
            t->path = outer->field + "[" + std::to_string(t->own_element.index) + "]";
            auto el = std::make_shared<FieldSpec>(*lf);
            el->type = lf->element;
            el->element_fields.clear();
            el->name = t->path;
            if (el->type == FieldType::Struct) return *why = id + ": raw path \"" + rw.path + "\" is a whole element of " + reg + "." + outer->field + ": edit its fields instead", false;
            t->element_spec = el;
            t->ref = {reg, id, outer->field};
            t->spec = lf;
            t->own = true;
            if (!op_of(rw.op, *el, &t->own_op, &err)) return *why = id + ": raw path \"" + rw.path + "\" (" + t->path + "): " + err, false;
            return true;
        }
    if (cover && !cover->converted) {
        const FieldSpec* f = c.schema.registry(reg)->field(cover->field);
        if (!f) return *why = id + ": raw path \"" + rw.path + "\" maps to " + reg + "." + cover->field + ", which the schema does not have", false;
        if (f->read_only || f->ops == FieldOps::None) return *why = id + ": raw path \"" + rw.path + "\" is the member of " + reg + "." + cover->field + ", which is read-only", false;
        if (f->type == FieldType::List) return *why = id + ": raw path \"" + rw.path + "\" is the list " + reg + "." + cover->field + ": change it with list ops on that field", false;
        t->ref = {reg, id, cover->field};
        t->spec = f;
        t->own = true;
        if (!op_of(rw.op, *f, &t->own_op, &err)) return *why = id + ": raw path \"" + rw.path + "\" (" + reg + "." + cover->field + "): " + err, false;
        if (f->ops == FieldOps::Set && t->own_op.relative()) return *why = id + ": raw path \"" + rw.path + "\" (" + reg + "." + cover->field + ") takes a value only", false;
        return true;
    }
    const RawMember* m = s.raw_member(c.base, reg, id, rw.key, &err);
    if (!m)
        return *why = id + ": raw path \"" + rw.path + "\" does not resolve on this game build (" + err +
                      "); raw paths are the Advanced tier, checked per entry: this edit was not applied", false;
    if (m->spec.read_only) return *why = id + ": raw path \"" + rw.path + "\" is read-only on this game build", false;
    t->ref = {reg, id, raw_field(rw.key)};
    t->spec = &m->spec;
    if (cover) t->conv_field = cover->field;
    t->own = true;
    if (!op_of(rw.op, m->spec, &t->own_op, &err)) return *why = id + ": raw path \"" + rw.path + "\": " + err, false;
    return true;
}

// The place a refusal names: the registry, the entry (the one that failed, else an exact selector's, else an add's id), the
// Kiln field or raw path.
void refuse(Ctx& c, const Who& w, const Edit& e, const std::string& why) {
    Refusal r{w.mod, w.source, w.stage, e.n, why, "", e.registry, "", "", false};
    r.raw = !e.element.on && e.field.empty() && !e.raw.empty();
    r.field = e.element.on || !e.field.empty() ? e.field : r.raw ? e.raw.front().path : "";
    if (!c.failed_id.empty()) r.id = c.failed_id;
    else if (e.entry == EntryOp::Add) r.id = e.add_id;
    else if (!e.select.id.empty() && e.select.id.find_first_of("*?") == std::string::npos)
        r.id = e.select.id.find(':') == std::string::npos ? "game:" + lower(e.select.id) : lower(e.select.id);
    c.failed_id.clear();
    c.out.refused.push_back(std::move(r));
}

// A value a field can take (code batches bypass the IR reader's checks): its type, its range, a list's size.
bool value_fits(const Value& v, const FieldSpec& f, std::string* why, bool element = false) {
    const FieldType t = element ? f.element : f.type;
    auto range = [&](double d) {
        if (!std::isfinite(d)) return *why = f.name + ": not a finite number", false;
        if ((f.min && d < *f.min) || (f.max && d > *f.max)) return *why = f.name + ": " + v.text_of() + " is outside the field's range", false;
        return true;
    };
    switch (t) {
        case FieldType::Int:
            if (v.kind != Value::Int) return *why = f.name + ": a whole number is expected", false;
            return range(static_cast<double>(v.i));
        case FieldType::Float:
            if (!v.numeric()) return *why = f.name + ": a number is expected", false;
            return range(v.as_double());
        case FieldType::Bool:
            return v.kind == Value::Bool || (*why = f.name + ": true or false is expected", false);
        case FieldType::Enum:
            if (v.kind == Value::Text && std::find(f.enum_values.begin(), f.enum_values.end(), v.t) != f.enum_values.end()) return true;
            return *why = f.name + ": one of the schema's names is expected", false;
        case FieldType::Id:
            if (v.kind == Value::None && f.optional) return true;  // an optional reference left empty
            return (v.kind == Value::Text && v.t.find(':') != std::string::npos) || (*why = f.name + ": an id is expected", false);
        case FieldType::Flags:
            if (v.kind != Value::List) return *why = f.name + ": a list of names is expected", false;
            for (const Value& x : v.list)
                if (x.kind != Value::Text || std::find(f.enum_values.begin(), f.enum_values.end(), x.t) == f.enum_values.end())
                    return *why = f.name + ": one of the schema's names is expected", false;
            return true;
        case FieldType::Struct:
            if (v.kind != Value::Struct) return *why = f.name + ": an element with fields is expected", false;
            for (size_t k = 0; k < v.keys.size(); ++k) {
                const FieldSpec* leaf = f.element_field(v.keys[k]);
                if (!leaf) return *why = f.name + ": an element has no field \"" + v.keys[k] + "\"", false;
                if (!value_fits(v.list[k], *leaf, why)) return false;
            }
            return true;
        default:
            if (element || v.kind != Value::List) return *why = f.name + ": a list is expected", false;
            if (f.max_items && v.list.size() > f.max_items) return *why = f.name + ": at most " + std::to_string(f.max_items) + " values", false;
            for (const Value& x : v.list)
                if (!value_fits(x, f, why, true)) return false;
            return true;
    }
}

// The field an edit's report lines name: its Kiln field, or its first raw path's chain.
std::string edit_field(const Edit& e) { return e.raw.empty() ? e.field : raw_field(e.raw.front().key); }

// One edit at its mod's turn on state s. resolved: the entries a final edit named on its mod's own copy (else resolved here).
void run_edit(Ctx& c, State& s, const Who& w, const Edit& e, bool report, const std::vector<std::string>* resolved) {
    const RegistrySpec* reg = c.schema.registry(e.registry);
    if (!reg) return refuse(c, w, e, "no registry \"" + e.registry + "\"");
    if (e.entry == EntryOp::Add) {  // all or nothing: the entry and every field it is given, or none of it
        if (w.stage != Stage::Add) return refuse(c, w, e, "an entry is added in stage add");
        if (e.add_id.rfind(w.mod + ":", 0) != 0) return refuse(c, w, e, "an added entry's id is \"" + w.mod + ":<name>\"");
        if (lower(e.add_id) != e.add_id) return refuse(c, w, e, "an added entry's id is lower case (Kiln ids are), not \"" + e.add_id + "\"");
        const std::string name = e.add_id.substr(w.mod.size() + 1);
        if (name.empty() || e.add_id.size() > 256 || !std::all_of(name.begin(), name.end(), [](unsigned char ch) {
                return (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '_' || ch == '-';
            })) return refuse(c, w, e, "an added entry's id is <mod>:<name>, at most 256 characters; name uses a-z, 0-9, '.', '_' or '-'");
        if (s.entry(e.registry, e.add_id)) return refuse(c, w, e, "the id " + e.add_id + " is taken (ids are never reused)");
        auto refuse_add = [&](const std::string& why) {  // a reference to this id names why it is not there
            c.refused_add.try_emplace({e.registry, e.add_id}, why);
            refuse(c, w, e, why);
        };
        std::vector<std::pair<const FieldSpec*, Value>> given;
        for (const auto& [k, v] : e.add_fields) {
            const FieldSpec* f = reg->field(k);
            std::string why;
            if (!f) return refuse_add(e.registry + " has no field \"" + k + "\"; the entry is not added");
            if (f->read_only) return refuse_add(e.registry + "." + k + " is read-only; the entry is not added");
            if (!value_fits(v, *f, &why)) return refuse_add(why + "; the entry is not added");
            given.emplace_back(f, v);
        }
        EntryState n;
        n.id = e.add_id;
        n.added = true;
        n.like = lower(e.like);
        if (!n.like.empty()) {
            const EntryState* l = s.entry(e.registry, n.like);
            if (!l || l->removed) return refuse_add("like: no entry " + e.like + " in " + e.registry);
            n.traits = l->traits;
            n.base = l->base;
            for (auto it = s.fields.lower_bound({e.registry, n.like, ""}); it != s.fields.end() && it->first.registry == e.registry && it->first.id == n.like; ++it)
                n.base[it->first.field] = it->second.result;
        }
        s.add_entry(e.registry, std::move(n));
        if (report) c.out.added.push_back({e.registry, e.add_id});
        c.adders[{e.registry, e.add_id}] = {w.mod, w.source, e.n};
        for (const auto& [f, v] : given) {  // a fresh entry: no other writer, nothing to refuse
            Edit set;
            set.n = e.n;
            set.registry = e.registry;
            set.field = f->name;
            set.op.set = true;
            set.op.value = v;
            std::string why;
            apply_targets(c, s, w, set, field_targets(*f, set, {e.add_id}), report, &why);
        }
        return;
    }
    if (w.stage == Stage::Add) return refuse(c, w, e, "stage add adds entries; field edits belong in alter or final");
    if (e.entry != EntryOp::Remove && e.raw.empty() && !reg->field(e.element.on ? e.element.list : e.field)) return refuse(c, w, e, e.registry + " has no field \"" + e.field + "\"");
    if (e.entry == EntryOp::None && e.raw.empty()) {
        std::string why;
        if (!ops_allow(*reg->field(e.element.on ? e.element.list : e.field), e, &why)) return refuse(c, w, e, why);
    }
    if (e.element.on && !reg->field(e.element.list)->element_field(e.element.leaf) && !e.element.leaf.empty())
        return refuse(c, w, e, e.registry + "." + e.element.list + "'s elements have no field \"" + e.element.leaf + "\"");
    std::vector<std::string> ids;
    if (resolved) {  // a final edit: entries another mod's final removed since are dropped
        for (const std::string& id : *resolved) {
            const EntryState* x = s.entry(e.registry, id);
            if (x && !x->removed) ids.push_back(id);
            else if (x && report)
                c.out.lines.push_back({LineKind::Dropped, {e.registry, id, edit_field(e)}, x->removed_by, w.mod,
                                       "dropped: " + id + " was removed by " + x->removed_by + "; " + w.mod + "'s edit into it is not applied"});
        }
    } else {
        std::string why, dropped_by;
        if (!select(StateView(s), c.schema, s, e.registry, e.select, &ids, &why, &dropped_by)) return refuse(c, w, e, why);
        if (!dropped_by.empty()) {
            if (report)
                c.out.lines.push_back({LineKind::Dropped, {e.registry, e.select.id, edit_field(e)}, dropped_by, w.mod,
                                       "dropped: " + e.select.id + " was removed by " + dropped_by + "; " + w.mod + "'s edit into it is not applied"});
            return;
        }
    }
    if (e.entry == EntryOp::Remove) {
        for (const std::string& id : ids) {
            EntryState* x = s.entry(e.registry, id);
            if (!x || x->removed) continue;
            x->removed = true;
            x->removed_by = w.mod;
            if (report) {
                c.out.removed.push_back({e.registry, id});
                c.out.removals[{e.registry, id}] = {w.mod, w.source, w.stage, e.n, "hidden by " + w.mod};
            }
        }
        return;
    }
    std::vector<Target> ts;
    if (!e.raw.empty()) {  // the Advanced tier: every path of every entry resolves, or the edit is refused whole
        for (const std::string& id : ids)
            for (const RawWrite& rw : e.raw) {
                Target t;
                std::string why;
                if (!raw_target(c, s, e.registry, id, rw, &t, &why)) return c.failed_id = id, refuse(c, w, e, why);
                ts.push_back(std::move(t));
            }
    } else {
        ts = field_targets(*reg->field(e.element.on ? e.element.list : e.field), e, ids);
    }
    std::string why;
    if (!apply_targets(c, s, w, e, ts, report, &why)) refuse(c, w, e, why);
}

// One mod's turn in a stage. Its code reads seen: the resolved view as of the end of the previous stage (never this stage's
// pending edits, its own included), so every mod's callback of one stage reads the same values, whatever the order.
void run_mod(Ctx& c, State& s, const ModInput& m, Stage stage, const State& seen) {
    std::string bad_id;
    if (!mod_id_ok(m.id, &bad_id)) {  // "game", "kiln", or no Kiln mod id: it mints no ids and edits nothing
        auto refuse_all = [&](const Batch& b) {
            for (const Edit& e : b.edits) refuse(c, {m.id, b.source, stage}, e, "mod id " + bad_id);
        };
        for (const Batch& b : m.batches)
            if (b.stage == stage) refuse_all(b);
        if (m.code) {
            Batch cb;
            cb.source = "code";
            m.code(stage, StateView(seen), &cb);
            refuse_all(cb);
        }
        return;
    }
    for (const Batch& b : m.batches) {
        if (b.stage != stage) continue;
        const Who w{m.id, b.source, stage};
        if (b.mod != m.id) {
            for (const Edit& e : b.edits) refuse(c, w, e, "a batch of mod " + b.mod + " in " + m.id + "'s place");
            continue;
        }
        for (const Edit& e : b.edits) run_edit(c, s, w, e, true, nullptr);
    }
    if (m.code) {
        Batch cb;
        cb.mod = m.id;
        cb.stage = stage;
        cb.source = "code";
        m.code(stage, StateView(seen), &cb);
        const Who w{m.id, cb.source, stage};
        for (const Edit& e : cb.edits) run_edit(c, s, w, e, true, nullptr);
    }
}

}  // namespace

namespace {

// ---- references (mod-dev feedback adopted for P1) ----
// An entry whose reference (an id field, a list of ids, an id leaf of a list's elements, into a registry of this schema)
// names a refused, removed, retired or missing entry is refused too, per entry, with the cause chain. Runs after the stage;
// a refusal can make another entry's reference bad, so it runs until nothing changes.

using Ref = std::pair<std::string, std::string>;  // the place in the value ("inputs[0].item"), the id as written

void refs_of(const Schema& schema, const FieldSpec& f, const Value& v, const std::string& path, std::vector<Ref>* out) {
    auto known = [&](const std::string& reg) { return !reg.empty() && schema.registry(reg); };
    if (f.type == FieldType::Id) {
        if (v.kind == Value::Text && known(f.ref)) out->emplace_back(path, v.t);
        return;
    }
    if (f.type != FieldType::List || v.kind != Value::List) return;
    for (size_t i = 0; i < v.list.size(); ++i) {
        const Value& x = v.list[i];
        const std::string at = path + "[" + std::to_string(i) + "]";
        if (f.element == FieldType::Id && x.kind == Value::Text && known(f.ref)) out->emplace_back(at, x.t);
        if (f.element == FieldType::Struct && x.kind == Value::Struct)
            for (size_t k = 0; k < x.keys.size(); ++k)
                if (const FieldSpec* leaf = f.element_field(x.keys[k]); leaf && leaf->type == FieldType::Id && x.list[k].kind == Value::Text && known(leaf->ref))
                    out->emplace_back(at + "." + x.keys[k], x.list[k].t);
    }
}

bool has_refs(const Schema& schema, const FieldSpec& f) {
    auto known = [&](const std::string& reg) { return !reg.empty() && schema.registry(reg); };
    if (f.type == FieldType::Id || (f.type == FieldType::List && f.element == FieldType::Id)) return known(f.ref);
    if (f.type == FieldType::List && f.element == FieldType::Struct)
        for (const FieldSpec& leaf : f.element_fields)
            if (leaf.type == FieldType::Id && known(leaf.ref)) return true;
    return false;
}

// The registry a reference at path points into (the field's, or its element leaf's).
std::string ref_registry(const FieldSpec& f, const std::string& path) {
    if (f.type != FieldType::List || f.element != FieldType::Struct) return f.ref;
    const size_t dot = path.rfind('.');
    const FieldSpec* leaf = dot == std::string::npos ? nullptr : f.element_field(path.substr(dot + 1));
    return leaf ? leaf->ref : std::string();
}

// "" when the entry is there (or cannot be judged here); else why not, as a reference to it says it. A game id this base
// has not got is S5's to judge (its model holds the game's data; this one may be partial), so only a mod's id counts as
// missing here.
std::string why_gone(const Ctx& c, const State& s, const std::string& reg, const std::string& id) {
    const std::string key = lower(id);
    const EntryState* e = s.entry(reg, key);
    const auto refused = c.refused_add.find({reg, key});
    if (!e) return refused != c.refused_add.end() ? "refused: " + refused->second : key.rfind("game:", 0) == 0 ? "" : "does not exist";
    if (!e->removed) return "";
    if (e->retired) return "is retired";
    if (!e->removed_in.empty()) return "was removed by game build " + e->removed_in;
    if (refused != c.refused_add.end()) return "refused: " + refused->second;
    return "was removed by " + e->removed_by;
}

// One chain row over value v (its field's spec f). false + *why: it no longer applies there.
bool apply_row(const ChainRow& r, const Value& v, const FieldSpec& f, Value* out, std::string* why) {
    if (r.element.on) return r.element_spec && element_step(r.element, r.op, *r.element_spec, v, out, why);
    if (r.list != ListOp::None) return *out = list_step(r.list, r.list_values, v, &f), true;
    if (!op_result(r.op, v, f, out, why)) return false;
    return true;
}

// A cause text cut to fit (at most about 1 KiB with its prefix), keeping its first links and its root cause.
std::string bounded(const std::string& text, const std::string& root) {
    if (text.size() <= 700) return text;
    size_t cut = 480;
    while (cut > 0 && (static_cast<unsigned char>(text[cut]) & 0xC0) == 0x80) --cut;
    std::string r = root.size() > 300 ? root.substr(0, 300) : root;
    size_t rc = r.size();
    while (rc > 0 && rc < root.size() && (static_cast<unsigned char>(r[rc]) & 0xC0) == 0x80) --rc;
    return text.substr(0, cut) + " ... (root: " + r.substr(0, rc) + ")";
}

// An item removal is indivisible: do not hide its loot/recipes while leaving unsafe drop groups behind.
std::map<EntryRef, std::string> unsafe_item_removals(const Ctx& c, const State& s, const std::string& table_id) {
    std::set<std::string> hidden = c.protected_items;
    for (const auto& ref : c.out.removed) if (ref.registry == "items") hidden.insert(ref.id);
    std::map<EntryRef, std::string> bad;
    if (hidden.empty() || !c.schema.registry("drops")) return bad;
    if (!c.base.registries.count("drops")) {
        for (const auto& id : hidden) bad[{"items", id}] = "drop tables were not read for removal safety on this build";
        return bad;
    }
    for (const auto& table : c.base.registries.at("drops")) {
        if (!table_id.empty() && table.id != table_id) continue;
        const Value value = s.current({"drops", table.id, "entries"});
        if (value.kind != Value::List) continue;
        auto hidden_id = [&](const Value& entry) -> std::string {
            const auto* item = entry.leaf("item");
            return item && item->kind == Value::Text && hidden.count(item->t) ? item->t : "";
        };
        bool touched = std::any_of(value.list.begin(), value.list.end(), [&](const auto& entry) { return !hidden_id(entry).empty(); });
        if (!touched) continue;
        size_t covered = 0; bool valid = true;
        for (const auto& [first, count] : table.drop_groups) {
            if (first != covered || first > value.list.size() || count > value.list.size() - first) { valid = false; break; }
            covered += count;
        }
        if (!valid || table.drop_groups.empty() || covered != value.list.size()) {
            for (const auto& entry : value.list) if (const auto id = hidden_id(entry); !id.empty())
                bad[{"items", id}] = "drop grouping of " + table.id + " is not proven on this build";
            continue;
        }
        for (const auto& [first, count] : table.drop_groups) {
            std::set<std::string> removed;
            bool viable = false;
            for (size_t i = first; i < first + count; ++i) {
                const auto& entry = value.list[i];
                const auto id = hidden_id(entry);
                if (!id.empty()) { removed.insert(id); continue; }
                const auto* chance = entry.leaf("chance"); const auto* weight = entry.leaf("weight"); const auto* max = entry.leaf("count.max");
                viable = viable || (chance && weight && max && chance->as_double() > 0 && weight->as_double() > 0 && max->as_double() > 0);
            }
            if (!viable) for (const auto& id : removed)
                bad[{"items", id}] = "drop group of " + table.id + " would have nothing that can spawn with a positive weight; item removal was not applied";
        }
    }
    return bad;
}

void refuse_unsafe_item_removals(Ctx& c, State& s) {
    for (const auto& [ref, reason] : unsafe_item_removals(c, s)) {
        const auto removal = c.out.removals.find(ref);
        if (removal == c.out.removals.end()) continue;
        c.out.refused.push_back({removal->second.mod, removal->second.source, removal->second.stage, removal->second.n,
                                reason, "", ref.registry, ref.id, ""});
        if (auto* entry = s.entry(ref.registry, ref.id)) { entry->removed = false; entry->removed_by.clear(); }
        std::erase(c.out.removed, ref); c.out.removals.erase(removal);
    }
}

void check_references(Ctx& c, State& s, bool fields_only = false) {
    std::map<std::string, std::vector<const FieldSpec*>> fields;  // registry -> its fields with references (lists, not leaf rows)
    for (const auto& [name, reg] : c.schema.registries)
        for (const auto& [fname, f] : reg.fields)
            if (fname.find("[]") == std::string::npos && has_refs(c.schema, f)) fields[name].push_back(&f);
    std::map<EntryRef, std::string> roots;  // a refused add's root cause
    // the bad references of a value: (place, id, why), and the ids alone
    auto bad_of = [&](const FieldSpec& f, const Value& v, std::vector<std::tuple<std::string, std::string, std::string>>* out) {
        std::vector<Ref> refs;
        refs_of(c.schema, f, v, f.name, &refs);
        for (const auto& [path, id] : refs) {
            const std::string why = why_gone(c, s, ref_registry(f, path), id);
            if (!why.empty()) out->emplace_back(path, id, why);
        }
    };
    auto bad_ids = [&](const FieldSpec& f, const Value& v) {
        std::vector<std::tuple<std::string, std::string, std::string>> b;
        bad_of(f, v, &b);
        std::set<std::string> ids;
        for (const auto& x : b) ids.insert(lower(std::get<1>(x)));
        return ids;
    };
    auto cause = [&](const FieldSpec& f, const std::tuple<std::string, std::string, std::string>& b, std::string* root) {
        const auto& [path, id, why] = b;
        const auto r = roots.find({ref_registry(f, path), lower(id)});
        *root = r != roots.end() ? r->second : id + " " + why;
        return bounded(path + " " + id + " " + why, *root);
    };
    // every entry with references once; then the entries naming one this check refused
    std::map<EntryRef, std::vector<EntryRef>> referrers;
    std::deque<EntryRef> work;
    std::set<EntryRef> queued;
    if (fields_only) {
        // Field-only turns cannot remove an entry or invalidate an untouched reference. Inspect their
        // accumulated touched reference fields, not every reference of every entry in the immutable base.
        for (const auto& [fr, outcome] : s.fields) {
            const auto* reg = c.schema.registry(fr.registry);
            const auto* field = reg ? reg->field(fr.field) : nullptr;
            const EntryRef entry{fr.registry, fr.id};
            if (field && has_refs(c.schema, *field) && queued.insert(entry).second) work.push_back(entry);
        }
    } else {
        for (const auto& [reg, spec] : c.schema.registries)
            s.each_entry(reg, [&](const EntryState& e) {
                if (e.removed) return;
                bool any = false;
                if (!e.like.empty()) {
                    referrers[{reg, e.like}].push_back({reg, e.id});
                    any = true;
                }
                if (reg == "loot" && c.schema.registry("items")) {
                    referrers[{"items", e.id}].push_back({reg, e.id});
                    any = true;
                }
                for (const FieldSpec* f : fields[reg]) {
                    std::vector<Ref> refs;
                    refs_of(c.schema, *f, s.current({reg, e.id, f->name}), f->name, &refs);
                    for (const auto& [path, id] : refs) referrers[{ref_registry(*f, path), lower(id)}].push_back({reg, e.id});
                    any = any || !refs.empty();
                }
                if (any && queued.insert({reg, e.id}).second) work.push_back({reg, e.id});
            });
    }
    auto enqueue_referrers = [&](const EntryRef& er) {
        if (const auto rs = referrers.find(er); rs != referrers.end())
            for (const EntryRef& by : rs->second)
                if (queued.insert(by).second) work.push_back(by);
    };
    auto refuse_added = [&](const EntryRef& er, EntryState& e, const Ctx::Adder& adder,
                            const std::string& why, const std::string& root, const std::string& field) {
        c.refused_add[er] = why;
        roots[er] = root;
        e.removed = true;
        e.removed_by = "refused";
        c.out.refused.push_back({adder.mod, adder.source, Stage::Add, adder.n, er.registry + " " + er.id + " refused: " + why,
                                 er.registry + "|" + er.id, er.registry, er.id, field});
        std::erase_if(c.out.added, [&](const EntryRef& a) { return a == er; });
        for (auto x = s.fields.lower_bound({er.registry, er.id, ""}); x != s.fields.end() && x->first.registry == er.registry && x->first.id == er.id;) x = s.fields.erase(x);
        std::erase_if(c.out.lines, [&](const Line& l) { return l.field.registry == er.registry && l.field.id == er.id; });
        enqueue_referrers(er);
    };
    while (!work.empty()) {
        const EntryRef er = work.front();
        work.pop_front();
        queued.erase(er);
        EntryState* e = s.entry(er.registry, er.id);
        if (!e || e->removed) continue;
        const auto adder = c.adders.find(er);
        // Clone bases and an item's loot row are identity dependencies, not editable schema fields.
        const EntryRef dependency = !e->like.empty() ? EntryRef{er.registry, e->like} : EntryRef{"items", er.id};
        if (!e->like.empty() || (er.registry == "loot" && c.schema.registry("items"))) {
            const std::string gone = why_gone(c, s, dependency.registry, dependency.id);
            if (!gone.empty()) {
                const auto root = roots.find(dependency);
                const std::string root_text = root == roots.end() ? dependency.id + " " + gone : root->second;
                const std::string why = bounded((e->like.empty() ? "item " : "like ") + dependency.id + " " + gone, root_text);
                if (adder != c.adders.end()) refuse_added(er, *e, adder->second, why, root_text, "");
                else {
                    e->removed = true; e->removed_by = "the reference check";
                    c.out.removed.push_back(er);
                    c.out.removals[er] = {"", "reference check", Stage::Alter, 0, why};
                    c.out.lines.push_back({LineKind::Dropped, {er.registry, er.id, ""}, "", "", er.registry + " " + er.id + " disabled: " + why});
                    enqueue_referrers(er);
                }
                continue;
            }
        }
        for (const FieldSpec* f : fields[er.registry]) {
            const FieldRef fr{er.registry, er.id, f->name};
            if (bad_ids(*f, s.current(fr)).empty()) continue;
            // 1: the registry edits that brought a bad reference in are taken out, here only (the entry's own add stands)
            if (auto it = s.fields.find(fr); it != s.fields.end()) {
                FieldOutcome& fo = it->second;
                std::vector<ChainRow> kept;
                std::set<std::string> dropped_mods;
                Value v = fo.base;
                for (ChainRow& r : fo.chain) {
                    const bool own_add = adder != c.adders.end() && r.kind == WriterKind::Registry && r.stage == Stage::Add && r.mod == adder->second.mod;
                    Value next;
                    std::string why;
                    if (r.kind != WriterKind::Registry || own_add) {
                        if (apply_row(r, v, *f, &next, &why)) v = next;
                        r.after = v;
                        kept.push_back(std::move(r));
                        continue;
                    }
                    const std::set<std::string> before = bad_ids(*f, v);
                    std::vector<std::tuple<std::string, std::string, std::string>> now;
                    const bool applies = apply_row(r, v, *f, &next, &why);
                    if (applies) {
                        bad_of(*f, next, &now);
                        std::erase_if(now, [&](const auto& x) { return before.count(lower(std::get<1>(x))) != 0; });
                    }
                    if (!applies || !now.empty()) {
                        std::string root;
                        c.out.refused.push_back({r.mod, r.source, r.stage, r.n,
                                                 er.registry + " " + er.id + ": " + (applies ? cause(*f, now.front(), &root) : why) + "; this edit was not applied there", "",
                                                 er.registry, er.id, f->name});
                        dropped_mods.insert(r.mod);
                        continue;
                    }
                    v = next;
                    r.after = v;
                    kept.push_back(std::move(r));
                }
                fo.chain = std::move(kept); // every row was moved, even when no newly introduced reference was refused
                if (!dropped_mods.empty()) {
                    resummarize(fo);
                    fo.result = v;
                    // the winner is the last Kiln writer left; archive-writer packages (named per entry) never are one, and
                    // a field only they are left on is no touched field any more
                    const auto last = std::find_if(fo.chain.rbegin(), fo.chain.rend(), [](const ChainRow& r) { return r.kind != WriterKind::ArchiveWriter; });
                    fo.winner = last == fo.chain.rend() ? std::string() : last->who;
                    // a conflict line naming an edit taken out no longer holds
                    std::erase_if(c.out.lines, [&](const Line& l) { return l.field == fr && (dropped_mods.count(l.winner) || dropped_mods.count(l.loser)); });
                    if (last == fo.chain.rend()) s.fields.erase(it);
                }
            }
            // 2: a game entry acts only on what the stage took away (an entry a mod removed, a retired id, one the game build
            // removed): in a registry whose entries are removed (hidden) it goes with it; in one whose entries are not, the
            // list elements naming it are taken out. A reference this base cannot judge (a game id it has not got) changes
            // nothing, and a refused mod entry never takes a game entry with it (only its own edits there are taken out, 1).
            if (adder == c.adders.end()) {
                std::vector<std::tuple<std::string, std::string, std::string>> gone;
                bad_of(*f, s.current(fr), &gone);
                std::erase_if(gone, [](const auto& x) { return std::get<2>(x).rfind("does not exist", 0) == 0 || std::get<2>(x).rfind("refused", 0) == 0; });
                if (gone.empty()) continue;
                std::string root;
                const std::string why = cause(*f, gone.front(), &root);
                if (er.registry == "drops" && f->name == "entries") {
                    // Physical slots never move. Check rows are last, so a later chance/weight edit cannot reactivate one.
                    auto& fo = s.touch(fr);
                    const Value current = fo.result;
                    const auto rows = fo.chain;
                    for (size_t i = 0; i < current.list.size(); ++i) {
                        const auto* item = current.list[i].leaf("item");
                        if (!item || item->kind != Value::Text || why_gone(c, s, "items", item->t).empty()) continue;
                        const auto removal = c.out.removals.find({"items", item->t});
                        const std::string remover = removal == c.out.removals.end() ? "the reference check" : removal->second.mod;
                        for (const char* leaf : {"chance", "weight"}) {
                            const FieldSpec* leaf_spec = f->element_field(leaf);
                            if (!leaf_spec) continue;
                            for (const auto& earlier : rows) {
                                if (earlier.kind != WriterKind::Registry) continue;
                                const bool targets = !earlier.element.on || ((earlier.element.all || earlier.element.index == i) && (earlier.element.leaf.empty() || earlier.element.leaf == leaf));
                                const auto* wanted = earlier.after.kind == Value::List && i < earlier.after.list.size() ? earlier.after.list[i].leaf(leaf) : nullptr;
                                if (!targets || !wanted || wanted->as_double() == 0) continue;
                                const std::string reason = er.registry + " " + er.id + " entries[" + std::to_string(i) + "]." + leaf + " conflicts with removal of " + item->t + " by " + remover + "; removal wins";
                                const bool seen = std::any_of(c.out.refused.begin(), c.out.refused.end(), [&](const auto& row) { return row.source == earlier.source && row.n == earlier.n && row.reason == reason; });
                                if (!seen) c.out.refused.push_back({earlier.mod, earlier.source, earlier.stage, earlier.n, reason, "", er.registry, er.id, f->name});
                            }
                            ChainRow row; row.kind = WriterKind::Check; row.mod = remover; row.who = "removal of " + item->t;
                            row.source = "removal:" + item->t; row.element = {true, "entries", false, static_cast<uint32_t>(i), leaf};
                            row.element_spec = std::make_shared<const FieldSpec>(*leaf_spec); row.op.set = true; row.op.value = Value::number(0);
                            Value next; std::string error;
                            if (apply_row(row, fo.result, *f, &next, &error)) {
                                fo.result = row.after = next; fo.winner = row.who; fo.chain.push_back(std::move(row)); note_row(fo);
                            }
                        }
                    }
                    c.out.lines.push_back({LineKind::Dropped, fr, "", "", er.registry + " " + er.id + ": stacks naming " + why + " disabled (physical slots retained)"});
                    continue;
                }
                if (c.schema.registry(er.registry)->remove) {
                    e->removed = true;
                    e->removed_by = "the reference check";
                    c.out.removed.push_back(er);
                    c.out.removals[er] = {"", "reference check", Stage::Alter, 0, why};
                    c.out.lines.push_back({LineKind::Dropped, fr, "", "", cat({er.registry, " ", er.id, " removed: ", why})});
                    if (const auto rs = referrers.find(er); rs != referrers.end())
                        for (const EntryRef& by : rs->second)
                            if (queued.insert(by).second) work.push_back(by);
                    break;
                }
                const Value cur = s.current(fr);
                if (f->type != FieldType::List || cur.kind != Value::List) {
                    c.out.lines.push_back({LineKind::Dropped, fr, "", "", cat({er.registry, " ", er.id, " still names ", why, " (its entries are not removed; it is left as it is)"})});
                    continue;
                }
                std::set<size_t> drop;
                for (const auto& g : gone) {  // the element index in "inputs[3].item" / "perks[3]"
                    const std::string& path = std::get<0>(g);
                    const size_t open = path.find('[', f->name.size());
                    if (open != std::string::npos) drop.insert(std::stoul(path.substr(open + 1)));
                }
                ChainRow r;
                r.kind = WriterKind::Check;
                r.who = "the reference check";
                r.list = ListOp::Remove;
                std::vector<Value> kept;
                for (size_t i = 0; i < cur.list.size(); ++i) (drop.count(i) ? r.list_values : kept).push_back(cur.list[i]);
                r.after = Value::of_list(std::move(kept));
                FieldOutcome& fo = s.touch(fr);
                fo.result = r.after;
                fo.winner = r.who;
                fo.chain.push_back(std::move(r));
                note_row(fo);
                c.out.lines.push_back({LineKind::Dropped, fr, "", "",
                                       cat({er.registry, " ", er.id, ": ", why, ": ", std::to_string(drop.size()), drop.size() == 1 ? " element" : " elements",
                                            " naming it taken out (its entries are not removed)"})});
                continue;
            }
            // 3: a mod's added entry still naming one is refused whole, and the entries naming it are looked at again
            std::vector<std::tuple<std::string, std::string, std::string>> still;
            bad_of(*f, s.current(fr), &still);
            if (still.empty()) continue;
            std::string root;
            const std::string why = cause(*f, still.front(), &root);
            refuse_added(er, *e, adder->second, why, root, f->name);
            break;
        }
    }
}

}  // namespace

// Immutable lookup/value state shared by later field-only turns; overlays copy only touched entries.
struct PreparedBase { State state; };

static State base_state(const BaseModel& base) {
    State s;
    for (const auto& [reg, entries] : base.registries)
        for (const BaseModel::Entry& e : entries) {
            // Kiln ids are lower case (D3); select()'s prefix walk relies on it, and S5's model hands them so
            assert(std::none_of(e.id.begin(), e.id.end(), [](char ch) { return ch >= 'A' && ch <= 'Z'; }) && "a base id with upper case");
            EntryState x;
            x.id = e.id;
            x.traits = e.traits;
            x.base = e.fields;
            x.removed_in = e.removed_in;
            x.removed = !e.removed_in.empty();
            x.removed_by = e.removed_in.empty() ? "" : "game build " + e.removed_in;
            x.archive = e.archive;
            x.archive_fields = e.archive_fields;
            s.add_entry(reg, std::move(x));
        }
    // retired ids ("<registry>|<id>") hold their place, removed: an add of one is refused as taken, an edit as missing
    for (const std::string& rid : base.retired) {
        const size_t bar = rid.find('|');
        if (bar == std::string::npos) continue;
        const std::string reg = rid.substr(0, bar), id = rid.substr(bar + 1);
        if (s.entry(reg, id)) continue;
        EntryState x;
        x.id = id;
        x.removed = x.retired = true;
        x.removed_by = "the ids ledger (retired)";
        s.add_entry(reg, std::move(x));
    }
    return s;
}

std::shared_ptr<const PreparedBase> prepare_base(const BaseModel& base, const Result* entries) {
    auto prepared = std::make_shared<PreparedBase>();
    prepared->state = base_state(base);
    if (entries) {
        for (const auto& [ref, definition] : entries->definitions) {
            if (prepared->state.entry(ref.registry, ref.id)) continue;
            EntryState entry; entry.id = ref.id; entry.added = true; entry.like = definition.like;
            entry.base = definition.constructor_values; entry.traits = definition.traits;
            prepared->state.add_entry(ref.registry, std::move(entry));
        }
        for (const auto& ref : entries->removed) if (auto* entry = prepared->state.entry(ref.registry, ref.id)) {
            entry->removed = true;
            const auto removal = entries->removals.find(ref);
            entry->removed_by = removal == entries->removals.end() ? "the reference check" : removal->second.mod;
        }
    }
    return prepared;
}

static Result compose_impl(const Schema& schema, const BaseModel& base, const std::vector<OldWrite>& old,
                           const std::vector<ModInput>& mods, const Result* previous, const State* prepared = nullptr,
                           const Result* established = nullptr) {
    Result out = previous ? *previous : Result{};
    Ctx c{schema, out, base, {}, {}, {}, {}, {}};
    const Result* protected_entries = previous ? previous : established;
    if (protected_entries) for (const auto& ref : protected_entries->removed)
        if (ref.registry == "items") c.protected_items.insert(ref.id);
    c.preserve_removals = !c.protected_items.empty();
    for (const auto& [reg, covers] : base.raw_cover)
        for (const auto& [key, cover] : covers) {
            const std::string k = cover_key(key);
            c.cover[reg][k] = cover;
            if (cover.converted) c.converted[{reg, cover.field}].push_back(k);
        }
    for (const ModInput& m : mods)  // refused before the stage: a reference to such an add names this as its cause
        for (const Refusal& x : m.refused)
            if (const size_t bar = x.entry.find('|'); bar != std::string::npos)
                c.refused_add.try_emplace({x.entry.substr(0, bar), lower(x.entry.substr(bar + 1))}, x.reason);
    State s;
    if (prepared) s.parent = prepared;
    else s = base_state(base);
    // D1: Kiln's older paths and the archive writer's packages lie below every registry edit, in the order they apply
    // (a raw member's chain too: "raw:<key>", when the member resolves on this build). Old data packs (and Content.* code)
    // are applied by the game's kiln.content as today: each one's relative op over the value below the packs (what the game
    // loaded: the archive writer's and old boot patches' result), the later one winning (packs.cpp parse_value); so two
    // packs' mul 2 and mul 3 give x3, not x6. Registry edits above them compose on the result.
    std::map<FieldRef, Value> below_packs;
    for (const OldWrite& given : old) {
        OldWrite w = given;
        const RegistrySpec* reg = schema.registry(w.field.registry);
        if (!reg || !s.entry(w.field.registry, w.field.id)) continue;
        ElementRef el;  // an old write into an element of a covered list
        if (is_raw_field(w.field.field)) {  // a covered member is its Kiln field's place
            const std::string key = cover_key(w.field.field.substr(4));
            const auto covers = c.cover.find(w.field.registry);
            if (covers != c.cover.end()) {
                if (const auto k = covers->second.find(key); k != covers->second.end() && !k->second.converted) {
                    w.field.field = k->second.field;
                } else if (const size_t at = key.find('['); at != std::string::npos && key.back() == ']' && key.find('[', at + 1) == std::string::npos) {
                    const auto o = covers->second.find(key.substr(0, at));
                    if (o != covers->second.end() && !o->second.converted) {
                        el.on = true, el.list = o->second.field, el.index = static_cast<uint32_t>(std::stoul(key.substr(at + 1, key.size() - at - 2)));
                        w.field.field = o->second.field;
                    }
                }
            }
        }
        const FieldSpec* f = reg->field(w.field.field);
        const RawMember* m = nullptr;
        if (!f && is_raw_field(w.field.field)) {
            std::string why;
            if ((m = s.raw_member(base, w.field.registry, w.field.id, w.field.field.substr(4), &why))) f = &m->spec;
        }
        if (!f) continue;
        FieldOutcome& fo = s.touch(w.field);
        if (m) {
            if (fo.raw.empty()) fo.raw = m->spec.name;
            if (!fo.raw_spec) fo.raw_spec = std::make_shared<const FieldSpec>(m->spec);
        }
        Value from = fo.result;
        if (w.kind == WriterKind::OldPack || w.kind == WriterKind::Code) from = below_packs.try_emplace(w.field, fo.result).first->second;
        ChainRow r;
        r.kind = w.kind;
        r.mod = w.mod;
        r.who = w.who;
        r.op = w.op;
        r.list = w.list;
        r.list_values = w.list_values;
        if (el.on) {  // an element row; one that does not apply there (no such element, a value out of range) is left out
            if (f->type != FieldType::List || f->element == FieldType::Struct) continue;
            FieldSpec es = *f;
            es.type = f->element, es.element_fields.clear(), es.name = el.list + "[" + std::to_string(el.index) + "]";
            Value next;
            std::string why;
            if (!element_step(el, w.op, es, from, &next, &why)) continue;
            r.element = el, r.element_spec = std::make_shared<const FieldSpec>(es), r.after = next;
        } else {
            r.after = w.list == ListOp::None ? apply_op(w.op, from, *f) : list_step(w.list, w.list_values, from, f);
        }
        fo.result = r.after;
        fo.winner = w.who;
        fo.chain.push_back(std::move(r));
        note_row(fo);
    }
    if (previous) {
        s.fields = previous->fields; // summaries travel with the chains: exclusive/yield stay exact
        for (const auto& [ref, definition] : previous->definitions) {
            if (!s.entry(ref.registry, ref.id) && !prepared) {
                EntryState entry; entry.id = ref.id; entry.added = true; entry.like = definition.like;
                entry.base = definition.constructor_values; entry.traits = definition.traits; s.add_entry(ref.registry, std::move(entry));
            }
            c.adders[ref] = {definition.mod, definition.source, definition.n};
        }
        for (const auto& ref : previous->removed) if (auto* entry = s.entry(ref.registry, ref.id)) {
            entry->removed = true;
            const auto removal = previous->removals.find(ref);
            entry->removed_by = removal == previous->removals.end() ? "the reference check" : removal->second.mod;
        }
    }
    std::map<EntryRef, std::map<std::string, Value>> constructors;
    const bool any_code = std::any_of(mods.begin(), mods.end(), [](const ModInput& m) { return bool(m.code); });
    for (const Stage stage : {Stage::Add, Stage::Alter}) {
        std::optional<State> seen;  // what code reads during this stage: the state as the previous stage left it
        if (any_code) seen = s;
        for (const ModInput& m : mods) run_mod(c, s, m, stage, seen ? *seen : s);
        if (!previous && stage == Stage::Add) for (const auto& ref : out.added) {
            const auto* entry = s.entry(ref.registry, ref.id);
            if (!entry) continue;
            auto& values = constructors[ref]; values = entry->base;
            for (auto f = s.fields.lower_bound({ref.registry, ref.id, ""}); f != s.fields.end() && f->first.registry == ref.registry && f->first.id == ref.id; ++f)
                if (!is_raw_field(f->first.field)) values[f->first.field] = f->second.result;
        }
        if (!previous && stage == Stage::Alter) refuse_unsafe_item_removals(c, s);
    }
    // final: each mod on its own overlay over the post-alter state (its data files' selectors see add+alter and its own final
    // edits, never another mod's; its code reads the post-alter view; the post-alter state is not changed until every final
    // ran); the entries it named are composed afterwards, in load order, with alter's rules
    struct Held {
        Who who;
        Edit edit;
        std::vector<std::string> ids;
        std::string dropped_by;
    };
    std::vector<Held> held;
    for (const ModInput& m : mods) {
        const bool any = m.code || std::any_of(m.batches.begin(), m.batches.end(), [](const Batch& b) { return b.stage == Stage::Final; });
        if (!any) continue;
        std::string bad_id;
        if (!mod_id_ok(m.id, &bad_id)) {
            State scratch;
            scratch.parent = &s;
            run_mod(c, scratch, m, Stage::Final, s);  // refuses each of its edits
            continue;
        }
        State scratch;
        scratch.parent = &s;
        Result ignored;
        ignored.removed = out.removed;
        Ctx sc{schema, ignored, base, c.converted, {}, {}, c.cover, {}};
        sc.preserve_removals = c.preserve_removals;
        sc.protected_items = c.protected_items;
        auto take = [&](const Who& w, const Edit& e, const std::string& batch_mod) {
            if (batch_mod != m.id) return refuse(c, w, e, "a batch of mod " + batch_mod + " in " + m.id + "'s place");
            if (e.entry == EntryOp::Add) return refuse(c, w, e, "an entry is added in stage add");
            const RegistrySpec* reg = schema.registry(e.registry);
            if (!reg) return refuse(c, w, e, "no registry \"" + e.registry + "\"");
            if (e.entry != EntryOp::Remove && e.raw.empty() && !reg->field(e.element.on ? e.element.list : e.field)) return refuse(c, w, e, e.registry + " has no field \"" + e.field + "\"");
            std::vector<std::string> ids;
            std::string why, dropped_by;
            if (!select(StateView(scratch), schema, scratch, e.registry, e.select, &ids, &why, &dropped_by)) return refuse(c, w, e, why);
            held.push_back({w, e, ids, dropped_by});
            if (dropped_by.empty()) run_edit(sc, scratch, w, e, false, &ids);  // its own later reads see it
        };
        for (const Batch& b : m.batches)
            if (b.stage == Stage::Final)
                for (const Edit& e : b.edits) take({m.id, b.source, Stage::Final}, e, b.mod);
        if (m.code) {
            Batch cb;
            cb.mod = m.id;
            cb.stage = Stage::Final;
            cb.source = "code";
            m.code(Stage::Final, StateView(s), &cb);  // the view as alter left it (never this stage's pending edits)
            for (const Edit& e : cb.edits) take({m.id, cb.source, Stage::Final}, e, cb.mod);
        }
    }
    for (const Held& h : held) {
        if (!h.dropped_by.empty()) {
            out.lines.push_back({LineKind::Dropped, {h.edit.registry, h.edit.select.id, edit_field(h.edit)}, h.dropped_by, h.who.mod,
                                 "dropped: " + h.edit.select.id + " was removed by " + h.dropped_by + "; " + h.who.mod + "'s edit into it is not applied"});
            continue;
        }
        run_edit(c, s, h.who, h.edit, true, &h.ids);
    }
    if (!previous) refuse_unsafe_item_removals(c, s);
    check_references(c, s, prepared != nullptr);
    if (!previous) {
        for (const EntryRef& ref : out.added) {
            const EntryState* e = s.entry(ref.registry, ref.id);
            const auto adder = c.adders.find(ref);
            if (!e || adder == c.adders.end()) continue;
            EntryDefinition d;
            d.like = e->like; d.base = e->like;
            for (size_t hops = 0; hops < 64; ++hops) {
                const auto* parent = s.entry(ref.registry, d.base);
                if (!parent || !parent->added) break;
                d.base = parent->like;
            }
            d.mod = adder->second.mod; d.source = adder->second.source; d.n = adder->second.n;
            d.traits = e->traits;
            d.constructor_values = constructors.at(ref);
            d.values = e->base;
            for (auto f = s.fields.lower_bound({ref.registry, ref.id, ""}); f != s.fields.end() && f->first.registry == ref.registry && f->first.id == ref.id; ++f)
                if (!is_raw_field(f->first.field)) d.values[f->first.field] = f->second.result;
            out.definitions[ref] = std::move(d);
        }
    }
    out.fields = std::move(s.fields);
    return out;
}

Result compose(const Schema& schema, const BaseModel& base, const std::vector<OldWrite>& old, const std::vector<ModInput>& mods) {
    return compose_impl(schema, base, old, mods, nullptr);
}
Result compose(const Schema& schema, const BaseModel& base, const std::vector<OldWrite>& old,
               const std::vector<ModInput>& mods, const Result& established) {
    return compose_impl(schema, base, old, mods, nullptr, nullptr, &established);
}
Result compose_after(const Schema& schema, const BaseModel& base, const Result& previous, const std::vector<ModInput>& mods) {
    return compose_impl(schema, base, {}, mods, &previous);
}

Result compose_after(const Schema& schema, const BaseModel& base, const PreparedBase& prepared,
                     const Result& previous, const std::vector<ModInput>& mods) {
    return compose_impl(schema, base, {}, mods, &previous, &prepared.state);
}

}  // namespace kc::registry
