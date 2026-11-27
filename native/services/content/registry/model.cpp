// SPDX-License-Identifier: MIT
// kiln.content registry stage: the model (model.h).
#include "model.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <unordered_map>

#include "formulas.h"
#include "model_bytes.h"

namespace kc {  // content.h's, declared here: content.h cannot share a unit with the registry's headers
std::string guid_text(const uint8_t g[16]);
uint32_t fnv1a(const void* data, size_t n);
}  // namespace kc

namespace kc::registry::model {

namespace {

constexpr size_t kMaxResource = 64u << 20;  // bytes of one resource the model reads
constexpr size_t kMaxWalk = 4096;           // a drop table's stacks

std::string lc(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

std::string key_hex(const KilnAssetKey& k) {
    uint8_t raw[24];
    std::memcpy(raw, k.guid, 16);
    for (int i = 0; i < 4; ++i) raw[16 + i] = static_cast<uint8_t>(k.type >> (8 * i));
    for (int i = 0; i < 4; ++i) raw[20 + i] = static_cast<uint8_t>(k.part >> (8 * i));
    return kiln::boot_view::hex_bytes(raw, sizeof raw);
}

const json* member_ci(const json& o, const std::string& name) {
    if (!o.is_object()) return nullptr;
    if (const auto it = o.find(name); it != o.end()) return &*it;
    const std::string f = lc(name);
    for (auto it = o.begin(); it != o.end(); ++it)
        if (lc(it.key()) == f) return &it.value();
    return nullptr;
}

// A path ("a.b[3].c") over a decoded value. tmpl: the first segment may name a template's component by its type ("Gravity",
// "Gravity#1"), as gd::resolve's Root::Template does.
bool jwalk(const json& root, const std::string& path, bool tmpl, json* out, std::string* why) {
    const json* cur = &root;
    size_t i = 0;
    bool first = true;
    while (i < path.size()) {
        if (path[i] == '.') {
            ++i;
            continue;
        }
        if (path[i] == '[') {
            const size_t e = path.find(']', i);
            if (e == std::string::npos) return *why = "\"[\" without \"]\" in " + path, false;
            const std::string in = path.substr(i + 1, e - i - 1);
            if (!cur->is_array()) return *why = path.substr(0, i) + " is not a list", false;
            if (!in.empty() && !std::isdigit(static_cast<unsigned char>(in[0]))) {
                // [<Type>#<k>] / [<Type>]: the k-th element of that variant type (a tree gives a variant as {"$type": <its name>,
                // ...}; the type is named qualified or by its last part, kiln.gamedata's grammar)
                const size_t h = in.find('#');
                std::string want = in.substr(0, h);
                if (const size_t ns = want.rfind("::"); ns != std::string::npos) want = want.substr(ns + 2);
                char* nend = nullptr;
                const unsigned long nth = h == std::string::npos ? 0 : std::strtoul(in.c_str() + h + 1, &nend, 10);
                if (h != std::string::npos && (h + 1 == in.size() || !nend || *nend)) return *why = "\"[" + in + "]\": <Type>#<index>", false;
                const json* found = nullptr;
                unsigned long seen = 0;
                for (const auto& el : *cur) {
                    if (!el.is_object() || !el.contains("$type") || !el["$type"].is_string()) continue;
                    std::string t = el["$type"].get<std::string>();
                    if (const size_t ns = t.rfind("::"); ns != std::string::npos) t = t.substr(ns + 2);
                    if (lc(t) != lc(want) || seen++ != nth) continue;
                    found = &el;
                    break;
                }
                if (!found) return *why = path.substr(0, i) + " has no " + in, false;
                cur = found;
                i = e + 1;
                first = false;
                continue;
            }
            char* end = nullptr;
            const unsigned long n = std::strtoul(in.c_str(), &end, 10);
            if (in.empty() || !end || *end) return *why = "\"[" + in + "]\": an index", false;
            if (n >= cur->size()) return *why = path.substr(0, i) + " has " + std::to_string(cur->size()) + " element(s), not " + in, false;
            cur = &(*cur)[n];
            i = e + 1;
            first = false;
            continue;
        }
        size_t e = i;
        while (e < path.size() && path[e] != '.' && path[e] != '[') ++e;
        const std::string name = path.substr(i, e - i);
        const json* next = member_ci(*cur, name);
        if (!next && first && tmpl && cur->is_object()) {
            const size_t h = name.find('#');
            const std::string want = lc(name.substr(0, h));
            const unsigned long nth = h == std::string::npos ? 0 : std::strtoul(name.c_str() + h + 1, nullptr, 10);
            const json* comps = member_ci(*cur, "components");
            unsigned long seen = 0;
            if (comps && comps->is_array())
                for (const auto& c : *comps)
                    if (c.is_object() && c.contains("$type") && c["$type"].is_string() && lc(c["$type"].get<std::string>()) == want && seen++ == nth) {
                        next = &c;
                        break;
                    }
        }
        if (!next) return *why = (path.substr(0, i).empty() ? std::string("the entry") : path.substr(0, i)) + " has no member " + name, false;
        cur = next;
        i = e;
        first = false;
    }
    *out = *cur;
    return true;
}

bool is_none_ref(const json& j) {
    if (!j.is_string()) return true;
    const std::string s = j.get<std::string>();
    return s.empty() || s == "none" || s == "00000000-0000-0000-0000-000000000000";
}

bool number_of(const json& j, double* out) {
    if (j.is_number()) return *out = j.get<double>(), true;
    if (j.is_boolean()) return *out = j.get<bool>() ? 1.0 : 0.0, true;
    return false;
}

// ---- the live bytes ----
class LiveSource final : public Source {
   public:
    LiveSource(kiln::boot_view::BaseView& view, Lister list) : view_(view), list_(std::move(list)) {}
    const char* name() const override { return view_.source(); }

    bool list(const std::string& type, std::vector<KilnAssetKey>* out, std::string* why) override {
        out->clear();
        if (released_) return *why = "the model's reads are over", false;
        if (!list_ || !list_(kiln::boot_gd::fnv1a32(type), out)) return *why = "the game's resources of " + type + " cannot be listed", false;
        return true;
    }
    bool count(const Place& p, size_t* n, std::string* why) override {
        rbytes::Node root;
        const std::vector<rbytes::Node>* els = nullptr;
        if (!root_of(p, &root, &els, why)) return false;
        *n = els ? els->size() : 0;
        return true;
    }
    bool member(const Place& p, const std::string& path, json* out, std::string* why) override {
        rbytes::Node root;
        const std::vector<rbytes::Node>* els = nullptr;
        if (!root_of(p, &root, &els, why)) return false;
        if (p.element >= 0) {
            if (!els || static_cast<size_t>(p.element) >= els->size()) return *why = p.array + "[" + std::to_string(p.element) + "] is not there", false;
            root = (*els)[static_cast<size_t>(p.element)];
        }
        return rbytes::member(root, path, out, why);
    }
    std::string digest() override { return released_ ? digest_ : view_.digest(); }
    void release() override {
        if (released_) return;
        digest_ = view_.digest();
        released_ = true;
        view_.release();
        arrays_.clear();
        bytes_.clear();
    }
    std::vector<std::pair<KilnAssetKey, std::string>> keys_read() const override { return read_; }
    bool touch(const KilnAssetKey& key, const std::string& type, std::string* why) override {
        const std::string* b = nullptr;
        return load(key, type, &b, why);
    }

   private:
    bool load(const KilnAssetKey& key, const std::string& type, const std::string** b, std::string* why) {
        if (released_) return *why = "the model's reads are over", false;
        const std::string k = key_hex(key);
        if (const auto it = bytes_.find(k); it != bytes_.end()) return *b = &it->second, true;
        std::string data, w;
        const KilnResult rc = view_.bytes(key, kMaxResource, &data, &w);
        if (rc != KILN_OK) return *why = w.empty() ? "the resource cannot be read" : w, false;
        *b = &bytes_.emplace(k, std::move(data)).first->second;
        read_.emplace_back(key, type);
        return true;
    }
    bool root_of(const Place& p, rbytes::Node* root, const std::vector<rbytes::Node>** els, std::string* why) {
        const std::string* b = nullptr;
        if (!load(p.key, p.type, &b, why)) return false;
        if (!rbytes::root(b->data(), b->size(), p.type, root, why)) return false;
        *els = nullptr;
        if (p.array.empty()) return true;
        const std::string ak = key_hex(p.key) + "|" + p.array;
        auto it = arrays_.find(ak);
        if (it == arrays_.end()) {
            std::vector<rbytes::Node> nodes;
            if (!rbytes::elements(*root, p.array, &nodes, why)) return false;
            it = arrays_.emplace(ak, std::move(nodes)).first;
        }
        *els = &it->second;
        return true;
    }

    kiln::boot_view::BaseView& view_;
    Lister list_;
    bool released_ = false;
    std::string digest_;
    std::map<std::string, std::string> bytes_;                 // key -> the bytes (stable: never changed after insert)
    std::map<std::string, std::vector<rbytes::Node>> arrays_;  // key|array -> its elements
    std::vector<std::pair<KilnAssetKey, std::string>> read_;   // every key read, in order, with its type
};

// ---- decoded trees ----
class TreeSource final : public Source {
   public:
    TreeSource(kiln::boot_view::BaseView& view, TreeLister list) : view_(view), list_(std::move(list)) {}
    const char* name() const override { return view_.source(); }
    bool list(const std::string& type, std::vector<KilnAssetKey>* out, std::string* why) override {
        out->clear();
        if (released_) return *why = "the model's reads are over", false;
        return list_ && list_(type, out, why);
    }
    bool count(const Place& p, size_t* n, std::string* why) override {
        const json* v = nullptr;
        if (!value_of(p, &v, why)) return false;
        const json* a = member_ci(*v, p.array);
        if (!a || !a->is_array()) return *why = p.array + " is not a list here", false;
        *n = a->size();
        return true;
    }
    bool member(const Place& p, const std::string& path, json* out, std::string* why) override {
        const json* v = nullptr;
        if (!value_of(p, &v, why)) return false;
        bool tmpl = p.type == "keen::ecs::TemplateResource";
        if (p.element >= 0) {
            const json* a = member_ci(*v, p.array);
            if (!a || !a->is_array() || static_cast<size_t>(p.element) >= a->size()) return *why = p.array + "[" + std::to_string(p.element) + "] is not there", false;
            v = &(*a)[static_cast<size_t>(p.element)];
            tmpl = false;
        }
        if (path.empty()) return *out = *v, true;
        return jwalk(*v, path, tmpl, out, why);
    }
    std::string digest() override { return released_ ? digest_ : view_.digest(); }
    void release() override {
        if (released_) return;
        digest_ = view_.digest();
        released_ = true;
        view_.release();
        values_.clear();
    }
    std::vector<std::pair<KilnAssetKey, std::string>> keys_read() const override { return read_; }
    bool touch(const KilnAssetKey& key, const std::string& type, std::string* why) override {
        const json* v = nullptr;
        return value_of(Place{key, type, "", -1}, &v, why);
    }

   private:
    bool value_of(const Place& p, const json** v, std::string* why) {
        if (released_) return *why = "the model's reads are over", false;
        const std::string k = key_hex(p.key);
        if (const auto it = values_.find(k); it != values_.end()) return *v = &it->second, true;
        const std::string q = bootview_query(p.key);
        std::string text, w;
        size_t cap = 1u << 16;
        for (int tries = 0; tries < 4; ++tries) {
            size_t required = 0, value_bytes = 0;
            const KilnResult rc = view_.tree(q, cap, kMaxResource, UINT64_MAX, &text, &required, &value_bytes, &w);
            if (rc == KILN_ERR_BUFFER_TOO_SMALL && required > cap && required <= kMaxResource + 1) {
                cap = required;
                continue;
            }
            if (rc == KILN_ERR_NOT_FOUND) return *why = "not in the game data", false;
            if (rc != KILN_OK) return *why = w.empty() ? "the view cannot read it" : w, false;
            json j = json::parse(text, nullptr, false);
            if (!j.is_object() || !j.contains("value")) return *why = "the view's answer holds no value", false;
            *v = &values_.emplace(k, std::move(j["value"])).first->second;
            read_.emplace_back(p.key, p.type);
            return true;
        }
        return *why = "the resource's value kept growing while it was read", false;
    }
    static std::string bootview_query(const KilnAssetKey& k) {
        char hash[9];
        std::snprintf(hash, sizeof hash, "%08x", static_cast<unsigned>(k.type));
        return "resource:" + guid_text(k.guid) + "/" + hash + "/" + std::to_string(k.part);
    }

    kiln::boot_view::BaseView& view_;
    TreeLister list_;
    bool released_ = false;
    std::string digest_;
    std::map<std::string, json> values_;
    std::vector<std::pair<KilnAssetKey, std::string>> read_;
};

// The entry sources of the registries outside the schema that R1's references name (P1-PLAN 9.2: "templates", "perks",
// "inputCategories"); their ids follow the same rules. Stations have their own schema and regmap since dev.32.
const map::EntrySource* builtin(const std::string& r) {
    static const map::EntrySource templates = [] {
        map::EntrySource s;
        s.kind = map::EntryKind::Resource, s.resource_type = "keen::ecs::TemplateResource", s.name = "name", s.unnamed = "template_%.8s";
        return s;
    }();
    static const map::EntrySource perks = [] {
        map::EntrySource s;
        s.kind = map::EntryKind::Resource, s.resource_type = "keen::Perk", s.name = "debugName", s.unnamed = "perk_%.8s";
        return s;
    }();
    static const map::EntrySource categories = [] {
        map::EntrySource s;
        s.kind = map::EntryKind::Array, s.resource_type = "keen::RecipeRegistryResource", s.array = "inputCategories", s.name = "debugName",
        s.id = "inputCategoryId", s.unnamed = "category_%08x";
        return s;
    }();
    if (r == "templates") return &templates;
    if (r == "perks") return &perks;
    if (r == "inputCategories") return &categories;
    return nullptr;
}

// An impactConfig row's config "shown" (perks.value): the entry's one configuration of a whole-number or a fractional type,
// the value its tooltip row shows (the config's id differs per entry). Types by their name without namespaces.
bool shown_config_type(const std::string& brief) { return brief == "Sint32ImpactConfig" || brief == "FloatImpactConfig"; }

// Kiln's names of the impact configurations the regmap's impactConfig rows name, as the game's config ids (identifiers of
// Kiln's translation layer, docs/kiln/services/gamedata.md).
std::string config_path(const std::string& config) {
    static const std::map<std::string, uint32_t> ids = {{"StaminaCost", 0x727d7f78u}, {"ManaCost", 0xaf236eceu}};
    const auto it = ids.find(config);
    if (it == ids.end()) return config;
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", static_cast<unsigned>(it->second));
    return b;
}

std::optional<std::string> kiln_enum(const map::Row& row, const json& j) {
    if (!j.is_string()) return std::nullopt;
    const std::string g = lc(j.get<std::string>());
    for (const auto& [k, v] : row.values)
        if (!v.empty() && lc(v) == g) return k;
    return std::nullopt;
}

std::optional<Value> scalar(const FieldSpec& f, FieldType type, const json& j, std::string* why) {
    double d = 0;
    switch (type) {
        case FieldType::Int:
            if (!number_of(j, &d)) return *why = "not a number: " + j.dump(), std::nullopt;
            return Value::integer(static_cast<int64_t>(std::llround(d)));
        case FieldType::Float:
            if (!number_of(j, &d)) return *why = "not a number: " + j.dump(), std::nullopt;
            return Value::number(d);
        case FieldType::Bool:
            if (!number_of(j, &d)) return *why = "not a bool: " + j.dump(), std::nullopt;
            return Value::boolean(d != 0);
        default: break;
    }
    (void)f;
    return *why = "not a plain value", std::nullopt;
}

FieldSpec element_spec(const FieldSpec& f) {
    FieldSpec e = f;
    e.type = f.element;
    return e;
}

}  // namespace

std::unique_ptr<Source> live_source(kiln::boot_view::BaseView& view, Lister list) { return std::make_unique<LiveSource>(view, std::move(list)); }
std::unique_ptr<Source> tree_source(kiln::boot_view::BaseView& view, TreeLister list) { return std::make_unique<TreeSource>(view, std::move(list)); }

// ---- the decoder ----
std::optional<Value> decode(const map::RegistryMap& reg, const map::Row& row, const FieldSpec& f, const MemberFn& get, const RefFn& refs,
                            const std::map<std::string, double>& balance, std::string* why) {
    auto at = [&](size_t i, json* out) {
        if (i >= row.paths.size()) return *why = "the row has no path " + std::to_string(i), false;
        return get(row.paths[i], out, why);
    };
    auto ref_of = [&](const std::string& registry, const json& num, const json& guid) -> Value {
        double n = 0;
        const bool has_num = number_of(num, &n) && n != 0;
        const std::string g = is_none_ref(guid) ? std::string() : lc(guid.get<std::string>());
        if (!has_num && g.empty()) return Value{};
        if (refs)
            if (auto id = refs(registry, g, has_num ? static_cast<uint32_t>(n) : 0)) return Value::text(*id);
        return Value{};  // names no entry this model knows: null, never a dangling id
    };
    const std::string& c = row.conv;
    json a, b;
    // eventTime: the first event of a kind ("<list>[<Type>#0].time"), read as a time; the live back-end moves every event of
    // that kind at the same time with it (regmap/README.md)
    if (c == "int" || c == "float" || c == "bool" || c == "time" || c == "eventTime") {
        if (!at(0, &a)) return std::nullopt;
        return scalar(f, f.type == FieldType::List ? f.element : f.type, a, why);
    }
    if (c == "position") {
        // a world position's axis: 32.32 fixed-point metres (keen::WorldPosition), exact as a double up to 2^53 / 2^32 m
        if (!at(0, &a)) return std::nullopt;
        double d = 0;
        if (!number_of(a, &d)) return *why = "not a number: " + a.dump(), std::nullopt;
        return Value::number(d / kPositionUnit);
    }
    if (c == "enum") {
        if (!at(0, &a)) return std::nullopt;
        if (auto k = kiln_enum(row, a)) return Value::text(*k);
        return *why = "the game's value " + a.dump() + " has no Kiln name", std::nullopt;
    }
    if (c == "optionalEnum") {
        if (!at(0, &a)) return std::nullopt;
        double on = 0;
        if (!number_of(a, &on)) return *why = "not a bool: " + a.dump(), std::nullopt;
        if (on == 0) return Value::text("none");
        if (!at(1, &b)) return std::nullopt;
        if (auto k = kiln_enum(row, b)) return Value::text(*k);
        return *why = "the game's value " + b.dump() + " has no Kiln name", std::nullopt;
    }
    if (c == "flags") {
        if (!at(0, &a)) return std::nullopt;
        std::set<std::string> on;
        for (const auto& x : a.is_array() ? a : json::array())
            if (auto k = kiln_enum(row, x)) on.insert(*k);
        std::vector<Value> out;
        for (const std::string& n : f.enum_values)
            if (on.count(n)) out.push_back(Value::text(n));
        return Value::of_list(std::move(out));
    }
    if (c == "ref") {
        if (!at(0, &a)) return std::nullopt;
        if (row.paths.size() == 1) return ref_of(f.ref, json(), a);  // one path: the reference alone (a creature's arsenal)
        if (!get(row.paths[1], &b, why)) b = nullptr;
        return ref_of(f.ref, a, b);
    }
    if (c == "component") {  // the first of the row's component types the entry holds (creatures.kind)
        for (const std::string& p : row.paths) {
            std::string w;
            if (!get(p, &a, &w)) continue;
            for (const auto& [k, v] : row.values)
                if (v == p) return Value::text(k);
        }
        return *why = "the entry holds none of the row's components", std::nullopt;
    }
    if (c == "refList") {
        if (!at(0, &a)) return std::nullopt;
        if (row.paths.size() > 1 && !get(row.paths[1], &b, why)) b = json::array();
        std::vector<Value> out;
        const size_t n = std::max(a.is_array() ? a.size() : 0, b.is_array() ? b.size() : 0);
        for (size_t i = 0; i < n; ++i) {
            const json g = a.is_array() && i < a.size() ? a[i] : json();
            const json k = b.is_array() && i < b.size() ? b[i] : json();
            const Value v = ref_of(f.ref, k, g);
            if (v.kind == Value::Text) out.push_back(v);
        }
        return Value::of_list(std::move(out));
    }
    if (c == "formula") {
        std::vector<double> in;
        for (size_t i = 0; i < row.paths.size(); ++i) {
            double d = 0;
            if (!at(i, &a) || !number_of(a, &d)) return *why = why->empty() ? "a formula input is not a number" : *why, std::nullopt;
            in.push_back(d);
        }
        double v = 0;
        if (!formulas::read(row.formula, in, formulas::constants_of(balance), &v)) return *why = "the formula " + row.formula + " gives no value here", std::nullopt;
        return f.type == FieldType::Int ? Value::integer(static_cast<int64_t>(std::llround(v))) : Value::number(v);
    }
    if (c == "impactConfig" && row.config == kShownConfig) {  // perks.value: the entry's one whole or fractional configuration
        if (row.paths.empty()) return *why = "the row has no path", std::nullopt;
        if (!get(row.paths[0], &a, why)) return std::nullopt;
        const json* only = nullptr;
        size_t n = 0;
        for (auto it = a.begin(); a.is_object() && it != a.end(); ++it)
            if (const json* t = it.value().is_object() ? member_ci(it.value(), "$config") : nullptr; t && t->is_string() &&
                shown_config_type(t->get<std::string>().substr(t->get<std::string>().rfind(':') == std::string::npos ? 0 : t->get<std::string>().rfind(':') + 1)))
                ++n, only = &it.value();
        if (n == 0) return Value{};  // a perk without a value (the simple armour and damage perks): null
        if (n != 1 || !only->contains("value")) return *why = "the entry holds more than one value", std::nullopt;
        return scalar(f, f.type, (*only)["value"], why);
    }
    if (c == "impactConfig") {
        if (row.paths.empty()) return *why = "the row has no path", std::nullopt;
        if (!get(row.paths[0] + "." + config_path(row.config), &a, why)) return std::nullopt;
        if (a.is_object() && a.contains("value")) a = a["value"];
        return scalar(f, f.type, a, why);
    }
    if (c == "unlock") {
        if (!at(0, &a)) return std::nullopt;
        bool set = false;
        if (a.is_object())
            for (auto it = a.begin(); it != a.end(); ++it) {
                double d = 0;
                if (number_of(it.value(), &d) && d != 0) set = true;
                if (it.value().is_string() && !is_none_ref(it.value())) set = true;
            }
        return Value::text(set ? "game" : "always");
    }
    // the elements of a list: a scalar each, or a struct of the element rows ("<list>[].<leaf>") over the element
    auto element_value = [&](const json& el, Value* out) {
        if (f.element != FieldType::Struct) {
            if (auto v = scalar(f, f.element, el, why)) return *out = *v, true;
            return false;
        }
        std::vector<std::pair<std::string, Value>> leaves;
        for (const FieldSpec& leaf : f.element_fields) {
            const auto lr = reg.rows.find(f.name + "[]." + leaf.name);
            if (lr == reg.rows.end()) continue;
            MemberFn in = [&](const std::string& path, json* o, std::string* w) { return jwalk(el, path, false, o, w); };
            std::string w;
            auto v = decode(reg, lr->second, leaf, in, refs, balance, &w);
            leaves.emplace_back(leaf.name, v ? *v : Value{});
        }
        std::sort(leaves.begin(), leaves.end(), [](const auto& x, const auto& y) { return x.first < y.first; });
        Value s;
        s.kind = Value::Struct;
        for (auto& [k, v] : leaves) s.keys.push_back(k), s.list.push_back(std::move(v));
        return *out = std::move(s), true;
    };
    if (c == "list") {
        if (!at(0, &a)) return std::nullopt;
        if (!a.is_array()) return *why = "not a list: " + a.dump().substr(0, 80), std::nullopt;
        std::vector<Value> out;
        for (const auto& el : a) {
            Value v;
            if (!element_value(el, &v)) return std::nullopt;
            out.push_back(std::move(v));
        }
        return Value::of_list(std::move(out));
    }
    if (c == "stackCount") {
        // the element is an item stack or an input category: the count of the one that is set
        if (row.paths.size() < 2) return *why = "the row needs two paths", std::nullopt;
        const std::string parent = row.paths[0].substr(0, row.paths[0].rfind('.'));
        json id, ref;
        std::string w;
        const bool item = (get(parent + ".item", &id, &w) && id.is_number() && id.get<double>() != 0) ||
                          (get(parent + ".itemRef", &ref, &w) && !is_none_ref(ref));
        if (!get(row.paths[item ? 0 : 1], &a, why)) return std::nullopt;
        return scalar(f, FieldType::Int, a, why);
    }
    if (c == "inventoryWalk") {
        if (!at(0, &a)) return std::nullopt;
        std::vector<Value> out;
        std::function<bool(const json&, int)> walk = [&](const json& g, int depth) {
            if (depth > 8 || !g.is_object()) return true;
            if (const json* st = member_ci(g, "stacks"); st && st->is_array())
                for (const auto& s : *st) {
                    if (out.size() >= kMaxWalk) return false;
                    Value v;
                    if (!element_value(s, &v)) return false;
                    out.push_back(std::move(v));
                }
            if (const json* gs = member_ci(g, "groups"); gs && gs->is_array())
                for (const auto& x : *gs)
                    if (!walk(x, depth + 1)) return false;
            return true;
        };
        if (!walk(a, 0)) return *why = why->empty() ? "the table has more than 4096 stacks" : *why, std::nullopt;
        return Value::of_list(std::move(out));
    }
    return *why = "conversion " + c + " is not decoded", std::nullopt;
}

bool encode_leaf(const FieldSpec& spec, const map::Row& r, const Value& x, json* out, std::string* why) {
    if (r.conv == "enum") {
        const auto it = x.kind == Value::Text ? r.values.find(x.t) : r.values.end();
        if (it == r.values.end()) return *why = "no game value for " + x.text_of(), false;
        return *out = it->second, true;
    }
    if (r.conv == "position") {
        if (!x.numeric() || !std::isfinite(x.as_double())) return *why = "not a number: " + x.text_of(), false;
        const double fixed = std::round(x.as_double() * kPositionUnit);
        if (!(fixed >= -0x1p63 && fixed < 0x1p63)) return *why = x.text_of() + " m does not fit the game's storage of this field", false;
        return *out = static_cast<int64_t>(fixed), true;
    }
    if (r.conv != "int" && r.conv != "float" && r.conv != "bool" && r.conv != "time")
        return *why = "the boot back-end does not write " + r.conv + " values", false;
    if (x.kind == Value::Bool) return *out = x.b, true;
    if (!x.numeric()) return *why = "not a number: " + x.text_of(), false;
    *out = spec.type == FieldType::Int || (spec.type == FieldType::List && spec.element == FieldType::Int) ? json(x.i) : json(x.as_double());
    if (x.kind == Value::Num && spec.type == FieldType::Int) *out = static_cast<int64_t>(std::llround(x.d));
    return true;
}

bool encode(const map::RegistryMap& reg, const map::Row& row, const FieldSpec& f, const Value& v, const Value& below,
            std::vector<std::pair<std::string, json>>* writes, std::string* why) {
    auto scalar_json = [&](const FieldSpec& spec, const map::Row& r, const Value& x, json* out) { return encode_leaf(spec, r, x, out, why); };
    if (row.paths.empty()) return *why = "the row has no path", false;
    if (row.conv != "list") {
        if (v == below) return true;
        json j;
        if (!scalar_json(f, row, v, &j)) return false;
        writes->emplace_back(row.paths[0], std::move(j));
        return true;
    }
    if (v.kind != Value::List || below.kind != Value::List) return *why = "not a list", false;
    if (v.list.size() != below.list.size())
        return *why = "the boot back-end writes " + f.name + "'s elements in place: its length (" + std::to_string(below.list.size()) + ") cannot change", false;
    for (size_t i = 0; i < v.list.size(); ++i) {
        if (v.list[i] == below.list[i]) continue;
        const std::string at = row.paths[0] + "[" + std::to_string(i) + "]";
        if (f.element != FieldType::Struct) {
            map::Row er = row;
            er.conv = f.element == FieldType::Float ? "float" : f.element == FieldType::Bool ? "bool" : "int";
            json j;
            if (!scalar_json(element_spec(f), er, v.list[i], &j)) return false;
            writes->emplace_back(at, std::move(j));
            continue;
        }
        const Value& now = v.list[i];
        const Value& was = below.list[i];
        for (size_t k = 0; k < now.keys.size(); ++k) {
            const Value* old = was.leaf(now.keys[k]);
            if (old && *old == now.list[k]) continue;
            const auto lr = reg.rows.find(f.name + "[]." + now.keys[k]);
            const FieldSpec* leaf = f.element_field(now.keys[k]);
            if (lr == reg.rows.end() || !leaf || lr->second.paths.empty()) return *why = f.name + "[]." + now.keys[k] + " has no row", false;
            json j;
            if (!scalar_json(*leaf, lr->second, now.list[k], &j)) return false;
            writes->emplace_back(at + "." + lr->second.paths[0], std::move(j));
        }
    }
    return true;
}

// ---- the hooks registries' values ----
namespace {
std::mutex g_hooks_mu;
HookValueFn g_hooks;
}  // namespace
void set_hook_values(HookValueFn fn) {
    std::lock_guard lk(g_hooks_mu);
    g_hooks = std::move(fn);
}

// ---- the model ----
struct Model::Impl {
    const map::RegMap& rm;
    const Schema& schema;
    struct Reg {
        bool built = false, failed = false;
        std::string why;
        const map::EntrySource* src = nullptr;
        const map::RegistryMap* map = nullptr;
        std::vector<Entry> entries;
        std::unordered_map<std::string, size_t> by_id;
        std::map<std::string, size_t> by_guid;
        std::map<uint32_t, size_t> by_num;
    };
    std::map<std::string, Reg> regs;
    std::map<std::tuple<std::string, size_t, std::string>, std::optional<Value>> values;
    std::map<std::tuple<std::string, size_t, std::string>, std::string> failed;
    std::map<std::tuple<std::string, size_t, std::string>, std::optional<RawMember>> raws;
    std::vector<std::string> notes;
    std::set<std::string> noted;
    std::set<std::string> decoded;
    std::optional<std::map<std::string, double>> constants;
    std::vector<std::string> details;  // take_details()
    void note(const std::string& n) {
        if (noted.insert(n).second) notes.push_back(n);
    }
};

Model::Model(Source& src, const map::RegMap& rm, const Schema& schema) : d_(std::make_unique<Impl>(Impl{rm, schema, {}, {}, {}, {}, {}, {}, {}, {}, {}})), src_(src) {}
Model::~Model() = default;

const std::vector<Entry>* Model::entries(const std::string& registry, std::string* why) {
    auto& R = d_->regs[registry];
    if (R.built) return R.failed ? (*why = R.why, nullptr) : &R.entries;
    R.built = true;
    if (const auto it = d_->rm.registries.find(registry); it != d_->rm.registries.end()) R.map = &it->second, R.src = &it->second.entries;
    else R.src = builtin(registry);
    auto fail = [&](const std::string& w) {
        R.failed = true;
        R.why = registry + ": " + w;
        *why = R.why;
        return nullptr;
    };
    if (!R.src) return fail("Kiln has no entry source for it on this build");
    const map::EntrySource& s = *R.src;
    std::vector<KilnAssetKey> keys;
    std::string w;
    if (s.kind != map::EntryKind::Hooks && !src_.list(s.resource_type, &keys, &w)) return fail(w);
    std::vector<Place> places;
    if (s.kind == map::EntryKind::Hooks) {
        places.push_back({KilnAssetKey{}, kHooksType, "", -1});  // one entry, no resource: its values are Kiln's hooks
    } else if (s.kind == map::EntryKind::Resource) {
        for (const KilnAssetKey& k : keys) {
            const Place p{k, s.resource_type, "", -1};
            // creatures: only the templates that hold one of the source's components (an enemy's, an animal's behaviour)
            bool holds = s.with_component.empty();
            for (size_t c = 0; !holds && c < s.with_component.size(); ++c) {
                json j;
                std::string w;
                holds = src_.member(p, s.with_component[c], &j, &w);
            }
            if (holds) places.push_back(p);
        }
    } else if (s.kind == map::EntryKind::Single) {
        for (const KilnAssetKey& k : keys)
            if (lc(guid_text(k.guid)) == lc(s.resource_guid)) places.push_back({k, s.resource_type, "", -1});
        if (places.empty()) return fail("the game data has no " + s.resource_type + " " + s.resource_guid);
    } else {
        if (keys.empty()) return fail("the game data has no " + s.resource_type);
        if (keys.size() > 1) d_->note(registry + ": " + std::to_string(keys.size()) + " resources of " + s.resource_type + "; the first is read");
        size_t n = 0;
        const Place whole{keys[0], s.resource_type, s.array, -1};
        if (!src_.count(whole, &n, &w)) return fail(w);
        for (size_t i = 0; i < n; ++i) places.push_back({keys[0], s.resource_type, s.array, static_cast<int32_t>(i)});
    }
    // drop tables: the templates that use them name them
    std::map<std::string, std::vector<std::string>> users;
    if (!s.name_from.empty()) {
        std::string tw;
        const std::vector<Entry>* templates = entries("templates", &tw);
        if (!templates) d_->note(registry + ": drop tables are named by their GUIDs (" + tw + ")");
        const size_t dot = s.name_from.find('.');
        std::string comp = dot == std::string::npos ? s.name_from : s.name_from.substr(0, dot);
        if (const size_t ns = comp.rfind("::"); ns != std::string::npos) comp = comp.substr(ns + 2);
        const std::string path = comp + (dot == std::string::npos ? "" : s.name_from.substr(dot));
        for (const Entry& t : templates ? *templates : std::vector<Entry>()) {
            json ref;
            std::string e;
            if (!src_.member(t.place, path, &ref, &e) || is_none_ref(ref)) continue;
            users[lc(ref.get<std::string>())].push_back(t.id.substr(t.id.find(':') + 1));
        }
    }
    std::map<uint32_t, std::string> by_item;
    std::map<std::string, std::string> by_key_guid;
    if (!s.key.empty()) {  // loot: its item's id (by 32-bit id); creatures: its template's id (by GUID)
        std::string iw;
        const std::vector<Entry>* keyed = entries(s.key, &iw);
        if (!keyed) return fail("its entries are named by " + s.key + ": " + iw);
        for (const Entry& e : *keyed) {
            by_item.emplace(e.num, e.id);
            if (!e.guid.empty()) by_key_guid.emplace(e.guid, e.id);
        }
    }
    std::vector<map::GameEntry> game;
    for (const Place& p : places) {
        Entry e;
        e.place = p;
        json j;
        std::string e1;
        if (!s.name.empty() && src_.member(p, s.name, &j, &e1) && j.is_string()) e.name = j.get<std::string>();
        if (s.kind == map::EntryKind::Hooks) {
            e.guid = lc(added_guid(registry, "game:" + s.unnamed));  // the id table's key: fixed, as an added entry's
        } else if (!s.guid.empty()) {
            if (src_.member(p, s.guid, &j, &e1) && !is_none_ref(j)) e.guid = lc(j.get<std::string>());
        } else if (p.array.empty()) {
            e.guid = lc(guid_text(p.key.guid));
        }
        double n = 0;
        if (!s.id.empty() && src_.member(p, s.id, &j, &e1) && number_of(j, &n)) e.num = static_cast<uint32_t>(n);
        else if (s.id.empty() && p.array.empty()) e.num = fnv1a(p.key.guid, 16);
        if (!s.name_from.empty()) e.named_by = users[e.guid];
        uint32_t key = 0;  // stations: the item that builds the entry names it (keyMember)
        if (!s.key_member.empty() && src_.member(p, s.key_member, &j, &e1) && number_of(j, &n)) key = static_cast<uint32_t>(n);
        game.push_back({e.guid, e.name, e.num, e.named_by, key});
        R.entries.push_back(std::move(e));
    }
    std::vector<std::string> notes;
    const std::vector<std::string> ids = map::assign_ids(registry, s, d_->rm.build.aliases, game, s.key == "items" ? &by_item : nullptr, &notes,
                                                         s.key.empty() || s.key == "items" ? nullptr : &by_key_guid);
    for (const std::string& n : notes) d_->note(n);
    for (size_t i = 0; i < R.entries.size() && i < ids.size(); ++i) {
        // every Kiln id is lower case (the id grammar; kiln.registry's select walks ids as stored): a pinned id that is not is
        // folded and named
        std::string id = ids[i];
        if (lc(id) != id || !map::is_kiln_id(lc(id))) {
            d_->note(registry + ": the id " + id + " is not a lower-case Kiln id: " + lc(id) + " is used");
            id = lc(id);
        }
        R.entries[i].id = id;
        R.by_id.emplace(id, i);
        if (!R.entries[i].guid.empty()) R.by_guid.emplace(R.entries[i].guid, i);
        if (R.entries[i].num) R.by_num.emplace(R.entries[i].num, i);
    }
    return &R.entries;
}

const Entry* Model::find(const std::string& registry, const std::string& kiln_id) {
    std::string why;
    if (!entries(registry, &why)) return nullptr;
    const auto& R = d_->regs[registry];
    const auto it = R.by_id.find(lc(kiln_id));
    return it == R.by_id.end() ? nullptr : &R.entries[it->second];
}

std::map<std::string, double> Model::balance_constants() {
    if (d_->constants) return *d_->constants;
    d_->constants.emplace();
    std::string why;
    const std::vector<Entry>* b = entries("balance", &why);
    if (b && !b->empty())
        for (const char* k : {"gear.seed", "gear.creep", "gear.armorOffset", "gear.armorGradient"})
            if (auto v = value("balance", 0, k, &why); v && v->numeric()) (*d_->constants)[k] = v->as_double();
    return *d_->constants;
}

std::optional<Value> Model::value(const std::string& registry, size_t i, const std::string& field, std::string* why) {
    const auto key = std::make_tuple(registry, i, field);
    if (const auto it = d_->values.find(key); it != d_->values.end()) {
        if (!it->second) *why = d_->failed[key];
        return it->second;
    }
    auto done = [&](std::optional<Value> v, const std::string& w) {
        if (!v) d_->failed[key] = w, *why = w;
        d_->decoded.insert(registry + "." + field);
        return d_->values.emplace(key, std::move(v)).first->second;
    };
    const std::vector<Entry>* es = entries(registry, why);
    if (!es || i >= es->size()) return done(std::nullopt, es ? "no such entry" : *why);
    const auto& R = d_->regs[registry];
    const RegistrySpec* rs = d_->schema.registry(registry);
    const FieldSpec* f = rs ? rs->field(field) : nullptr;
    if (!f || !R.map) return done(std::nullopt, registry + "." + field + " is not in the schema");
    const auto row = R.map->rows.find(field);
    if (row == R.map->rows.end()) return done(std::nullopt, registry + "." + field + " has no regmap row");
    const Place place = (*es)[i].place;
    MemberFn get = [&](const std::string& path, json* out, std::string* w) { return src_.member(place, path, out, w); };
    if (R.src && R.src->kind == map::EntryKind::Hooks)
        get = [](const std::string& path, json* out, std::string* w) {
            std::lock_guard lk(g_hooks_mu);
            if (!g_hooks) return *w = "Kiln's hooks of the game's code are not here", false;
            return g_hooks(path, out, w);
        };
    // A creature without the component a field lives in has no value of it: null, never an error or a note (most creatures
    // have no size of their own, only enemies have an arsenal); a live edit of it is refused for that entry.
    if (R.src && R.src->resource_type == "keen::ecs::TemplateResource" && row->second.conv != "component" && !row->second.paths.empty()) {
        const std::string& p0 = row->second.paths[0];
        json j;
        std::string w;
        if (!get(p0.substr(0, p0.find_first_of(".[")), &j, &w)) return done(Value{}, "");
    }
    RefFn refs = [this](const std::string& reg, const std::string& guid, uint32_t num) -> std::optional<std::string> {
        std::string w;
        if (!entries(reg, &w)) return std::nullopt;
        const auto& T = d_->regs[reg];
        if (!guid.empty())
            if (const auto it = T.by_guid.find(guid); it != T.by_guid.end()) return T.entries[it->second].id;
        if (num)
            if (const auto it = T.by_num.find(num); it != T.by_num.end()) return T.entries[it->second].id;
        d_->note(reg + ": a reference names no entry (" + (guid.empty() ? std::to_string(num) : guid) + "): read as none");
        return std::nullopt;
    };
    const auto constants = registry == "balance" && field.rfind("gear.", 0) == 0 ? std::map<std::string, double>() : balance_constants();
    std::string w;
    auto v = decode(*R.map, row->second, *f, get, refs, constants, &w);
    if (!v) *why = registry + "." + field + " of " + (*es)[i].id + ": " + w;
    return done(std::move(v), *why);
}

std::optional<RawMember> Model::raw(const std::string& registry, size_t i, const std::string& key, std::string* why) {
    const auto k = std::make_tuple(registry, i, key);
    if (const auto it = d_->raws.find(k); it != d_->raws.end()) {
        if (!it->second) *why = d_->failed[k];
        return it->second;
    }
    auto done = [&](std::optional<RawMember> m, const std::string& w) {
        if (!m) d_->failed[k] = w;
        return d_->raws.emplace(k, std::move(m)).first->second;
    };
    const std::vector<Entry>* es = entries(registry, why);
    if (!es || i >= es->size()) return done(std::nullopt, es ? "no such entry" : *why);
    json j;
    std::string w;
    if (!src_.member((*es)[i].place, key, &j, &w)) return done(std::nullopt, (*why = w));
    RawMember m;
    m.spec.name = key;
    const auto& R = d_->regs[registry];
    bool boot = false;
    if (R.map)
        for (const auto& [n, r] : R.map->rows) boot = boot || r.backend == map::Backend::Boot;
    m.spec.applies = boot ? Applies::Restart : Applies::Now;
    const std::string last = lc(key.substr(key.find_last_of('.') == std::string::npos ? 0 : key.find_last_of('.') + 1));
    m.spec.read_only = rbytes::identity(last) || last == "itemid" || last == "objectid";
    if (j.is_boolean()) m.spec.type = FieldType::Bool, m.value = Value::boolean(j.get<bool>());
    else if (j.is_number_integer() || j.is_number_unsigned()) m.spec.type = FieldType::Int, m.value = Value::integer(j.get<int64_t>());
    else if (j.is_number()) m.spec.type = FieldType::Float, m.value = Value::number(j.get<double>());
    else if (j.is_string() && !is_none_ref(j) && j.get<std::string>().find('-') == std::string::npos) {
        m.spec.type = FieldType::Enum, m.value = Value::text(j.get<std::string>());
        m.spec.enum_values = {j.get<std::string>()};  // the member's own names are the game's; a write is checked when it is written
    } else {
        return done(std::nullopt, (*why = key + " is not a number, a bool or an enum on this build"));
    }
    return done(std::move(m), "");
}

std::set<std::string> Model::decoded() const { return d_->decoded; }
const map::RegMap& Model::regmap() const { return d_->rm; }
const Schema& Model::schema() const { return d_->schema; }

std::vector<std::string> Model::take_notes() {
    std::vector<std::string> out;
    out.swap(d_->notes);
    return out;
}

std::vector<std::string> Model::take_details() {
    std::vector<std::string> out;
    out.swap(d_->details);
    return out;
}

void Model::release() { src_.release(); }

void Model::fill(const Need& need, BaseModel* out) {
    std::set<std::string> regs = need.registries;
    if (need.all)
        for (const auto& [n, r] : d_->schema.registries) regs.insert(n);
    for (const auto& [n, f] : need.fields) regs.insert(n);
    for (const std::string& reg : regs) {
        const RegistrySpec* rs = d_->schema.registry(reg);
        if (!rs) continue;
        std::string why;
        const std::vector<Entry>* es = entries(reg, &why);
        if (!es) {
            d_->note("registry " + why + ": its entries are not read in this start; edits naming them are refused");
            continue;
        }
        std::set<std::string> fields;
        if (need.all)
            for (const auto& [fn, f] : rs->fields)
                if (fn.find("[]") == std::string::npos) fields.insert(fn);
        if (const auto it = need.fields.find(reg); it != need.fields.end()) fields.insert(it->second.begin(), it->second.end());
        const auto whole = need.whole.find(reg);
        const bool traits = need.all || need.traits.count(reg);
        auto& dst = out->registries[reg];
        // fields an entry has no value of (an item without a cost or an armor value): one note per field with the count and the
        // first entry's reason, never one per entry (31.18 logged 34,207 such lines at a start with registry content); out of
        // the entries the field was read for: every entry for `fields`, else only the entries read whole
        std::map<std::string, std::pair<size_t, std::string>> missing;
        size_t read_whole = 0;
        for (size_t i = 0; i < es->size(); ++i) {
            BaseModel::Entry e;
            e.id = (*es)[i].id;
            std::set<std::string> want = fields;
            if (whole != need.whole.end() && whole->second.count(e.id)) {
                ++read_whole;
                for (const auto& [fn, f] : rs->fields)
                    if (fn.find("[]") == std::string::npos) want.insert(fn);
            }
            for (const std::string& fn : want) {
                if (!rs->field(fn) || rs->field(fn)->name.find("[]") != std::string::npos) continue;
                std::string w;
                if (auto v = value(reg, i, fn, &w)) {
                    e.fields[fn] = std::move(*v);
                    continue;
                }
                auto& m = missing[fn];
                if (m.first++ == 0) m.second = w;
                else if (w == m.second) continue;  // the field's own reason (no regmap row), the same for every entry: the note's
                d_->details.push_back(std::move(w));  // the entry's: kiln.log at debug level
            }
            if (reg == "drops" && e.fields.count("entries")) {
                // Safety metadata only: preserve grouping while the schema's list remains a flat walk.
                // An empty/missing span vector makes an item removal fail closed, never assume an old cache had it.
                const auto mapping = d_->rm.registries.find(reg);
                if (mapping != d_->rm.registries.end()) {
                    const auto row = mapping->second.rows.find("entries");
                    json root; std::string error; size_t cursor = 0;
                    std::function<bool(const json&, size_t)> groups = [&](const json& node, size_t depth) {
                        if (!node.is_object() || depth > 8) return false;
                        const auto* stacks = member_ci(node, "stacks");
                        const auto* children = member_ci(node, "groups");
                        if ((stacks && !stacks->is_array()) || (children && !children->is_array())) return false;
                        const size_t n = stacks ? stacks->size() : 0;
                        if (cursor + n > kMaxWalk) return false;
                        e.drop_groups.emplace_back(cursor, n); cursor += n;
                        if (children) for (const auto& child : *children) if (!groups(child, depth + 1)) return false;
                        return true;
                    };
                    if (row == mapping->second.rows.end() || row->second.paths.empty() ||
                        !src_.member((*es)[i].place, row->second.paths.front(), &root, &error) || !groups(root, 0) ||
                        e.fields.at("entries").list.size() != cursor) e.drop_groups.clear();
                }
            }
            if (traits) {
                std::string w;
                auto has = [&](const char* f) {
                    auto v = value(reg, i, f, &w);
                    return v && ((v->kind == Value::Bool && v->b) || (v->numeric() && v->as_double() > 1));
                };
                if (reg == "items") {
                    if (has("hasDamage")) e.traits.insert("weapon");
                    if (has("hasArmor")) e.traits.insert("armor");
                    if (has("maxStack")) e.traits.insert("stackable");
                } else if (reg == "recipes") {
                    json j;
                    if (src_.member((*es)[i].place, "isCookingRecipe", &j, &w) && j.is_boolean() && j.get<bool>()) e.traits.insert("cooking");
                    if (src_.member((*es)[i].place, "isUpgrade", &j, &w) && j.is_boolean() && j.get<bool>()) e.traits.insert("upgrade");
                } else if (reg == "creatures") {  // its kind is its trait: "@has": ["enemy"]
                    if (auto v = value(reg, i, "kind", &w); v && v->kind == Value::Text) e.traits.insert(v->t);
                } else if (reg == "skills") {
                    if (auto v = value(reg, i, "kind", &w); v && v->kind == Value::Text && v->t == "active") e.traits.insert("active");
                    if (auto v = value(reg, i, "maxLevel", &w); v && v->numeric() && v->as_double() > 0) e.traits.insert("leveled");
                } else if (reg == "stations") {
                    if (auto v = value(reg, i, "npc", &w); v && v->kind == Value::Bool && v->b) e.traits.insert("npc");
                }
            }
            dst.push_back(std::move(e));
        }
        for (const auto& [fn, m] : missing)
            d_->note(reg + "." + fn + " has no value in " + std::to_string(m.first) + " of " + std::to_string(fields.count(fn) ? es->size() : read_whole) +
                     " entries on this build (the first: " + m.second + "); an edit of those composes over no value (each is in kiln.log with "
                     "\"log_level\": \"debug\")");
        // ids the ledger says a game build removed: every edit naming one is refused "removed by game build ..."
        for (const auto& [rid, build] : d_->rm.build.aliases.removed) {
            const size_t bar = rid.find('|');
            if (bar == std::string::npos || rid.substr(0, bar) != reg) continue;
            BaseModel::Entry e;
            e.id = rid.substr(bar + 1);
            e.removed_in = build;
            if (!d_->regs[reg].by_id.count(e.id)) dst.push_back(std::move(e));
        }
        // the Advanced tier's raw members, resolved now (the source is released before compose asks)
        if (const auto rk = need.raw.find(reg); rk != need.raw.end())
            for (size_t i = 0; i < es->size(); ++i)
                for (const std::string& k : rk->second) {
                    std::string w;
                    raw(reg, i, k, &w);
                }
        // raw paths that are a Kiln field's member on this build
        if (const auto* m = d_->regs[reg].map)
            for (const auto& [fn, r] : m->rows) {
                if (r.paths.empty() || r.backend == map::Backend::None || fn.find("[]") != std::string::npos) continue;
                std::string key, err;
                if (!raw_key(r.paths[0], &key, &err)) continue;
                const bool converted = r.conv == "formula" || r.conv == "enum" || r.conv == "optionalEnum" || r.conv == "flags" || r.conv == "impactConfig" ||
                                       r.conv == "ref" || r.conv == "refList" || r.conv == "unlock" || r.conv == "stackCount" || r.conv == "list" ||
                                       r.conv == "inventoryWalk" || r.conv == "eventTime" || r.conv == "position";
                out->raw_cover[reg][key] = {fn, converted};
            }
    }
    Impl* d = d_.get();
    out->raw = [d](const std::string& registry, const std::string& id, const std::string& key, std::string* why) -> std::optional<RawMember> {
        const auto r = d->regs.find(registry);
        if (r == d->regs.end()) return *why = "no game data was read for " + registry, std::nullopt;
        const auto e = r->second.by_id.find(id);
        if (e == r->second.by_id.end()) return *why = "no game entry " + id, std::nullopt;
        const auto k = std::make_tuple(registry, e->second, key);
        const auto it = d->raws.find(k);
        if (it == d->raws.end()) return *why = "not read in this start", std::nullopt;
        if (!it->second) *why = d->failed[k];
        return it->second;
    };
}

void Model::id_table(std::map<std::string, std::string>* ids, std::map<std::string, uint32_t>* nums) const {
    for (const auto& [reg, R] : d_->regs) {
        if (R.failed) continue;
        for (const Entry& e : R.entries) {
            (*ids)[reg + "|" + e.id] = e.guid;
            (*nums)[reg + "|" + e.id] = e.num;
        }
    }
}

void Model::list_all() {
    std::string why;
    for (const auto& [n, r] : d_->schema.registries) entries(n, &why);
    for (const char* builtin_table : {"templates", "perks", "inputCategories"}) entries(builtin_table, &why);
}

json Model::id_table_json() const {
    json regs = json::object();
    for (const auto& [reg, R] : d_->regs) {
        if (R.failed || R.entries.empty()) continue;
        json t = json::object();
        for (const Entry& e : R.entries) t[e.guid.empty() ? "#" + std::to_string(e.num) : e.guid] = e.id;
        regs[reg] = std::move(t);
    }
    return {{"format", 1}, {"side", d_->rm.side}, {"exeSha256", d_->rm.exe_sha256}, {"decoder", kDecoderVersion}, {"registries", std::move(regs)}};
}

// ---- what the stage needs ----
Need needs_of(const Schema& schema, const std::vector<ModInput>& mods) {
    Need n;
    bool refs = false;
    for (const ModInput& m : mods) {
        if (m.code) n.all = true;
        for (const Batch& b : m.batches)
            for (const Edit& e : b.edits) {
                n.registries.insert(e.registry);
                auto& f = n.fields[e.registry];
                if (e.entry == EntryOp::Add || e.entry == EntryOp::Remove) refs = true;
                if (e.entry == EntryOp::Add) {
                    if (!e.like.empty()) n.whole[e.registry].insert(lc(e.like));
                    for (const auto& [k, v] : e.add_fields) f.insert(k);
                }
                if (e.element.on) f.insert(e.element.list);
                else if (!e.field.empty()) f.insert(e.field);
                for (const RawWrite& r : e.raw) n.raw[e.registry].insert(r.key);
                if (e.select.where.is_object())
                    for (auto it = e.select.where.begin(); it != e.select.where.end(); ++it) f.insert(it.key());
                if (!e.select.has.empty() || !e.select.has_not.empty()) n.traits.insert(e.registry);
            }
    }
    if (refs || n.all)
        for (const auto& [name, reg] : schema.registries)
            for (const auto& [fname, f] : reg.fields) {
                if (fname.find("[]") != std::string::npos) continue;
                bool r = (f.type == FieldType::Id || (f.type == FieldType::List && f.element == FieldType::Id)) && schema.registry(f.ref);
                if (f.type == FieldType::List && f.element == FieldType::Struct)
                    for (const FieldSpec& leaf : f.element_fields) r = r || (leaf.type == FieldType::Id && schema.registry(leaf.ref));
                if (r) n.fields[name].insert(fname), n.registries.insert(name);
            }
    for (auto& [reg, fields] : n.fields) {  // keep only the schema's fields (a where key may be a raw path or a typo: compose says so)
        const RegistrySpec* rs = schema.registry(reg);
        for (auto it = fields.begin(); it != fields.end();) it = rs && rs->field(*it) ? std::next(it) : fields.erase(it);
    }
    return n;
}

bool bytes_member(const void* data, size_t len, const std::string& type, const std::string& path, json* out, std::string* why) {
    rbytes::Node root;
    return rbytes::root(data, len, type, &root, why) && rbytes::member(root, path, out, why);
}

// ---- the GUID of a mod's entry ----
namespace {
struct Sha1 {
    uint32_t h[5] = {0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476, 0xC3D2E1F0};
    std::vector<uint8_t> buf;
    uint64_t len = 0;
    static uint32_t rol(uint32_t x, int n) { return (x << n) | (x >> (32 - n)); }
    void block(const uint8_t* p) {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i) w[i] = uint32_t(p[i * 4]) << 24 | uint32_t(p[i * 4 + 1]) << 16 | uint32_t(p[i * 4 + 2]) << 8 | p[i * 4 + 3];
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i) {
            uint32_t f, k;
            if (i < 20) f = (b & c) | (~b & d), k = 0x5A827999;
            else if (i < 40) f = b ^ c ^ d, k = 0x6ED9EBA1;
            else if (i < 60) f = (b & c) | (b & d) | (c & d), k = 0x8F1BBCDC;
            else f = b ^ c ^ d, k = 0xCA62C1D6;
            const uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d, d = c, c = rol(b, 30), b = a, a = t;
        }
        h[0] += a, h[1] += b, h[2] += c, h[3] += d, h[4] += e;
    }
    std::array<uint8_t, 20> digest(const std::vector<uint8_t>& msg) {
        std::vector<uint8_t> m = msg;
        const uint64_t bits = static_cast<uint64_t>(msg.size()) * 8;
        m.push_back(0x80);
        while (m.size() % 64 != 56) m.push_back(0);
        for (int i = 7; i >= 0; --i) m.push_back(static_cast<uint8_t>(bits >> (i * 8)));
        for (size_t i = 0; i < m.size(); i += 64) block(&m[i]);
        std::array<uint8_t, 20> out{};
        for (int i = 0; i < 5; ++i)
            for (int j = 0; j < 4; ++j) out[static_cast<size_t>(i * 4 + j)] = static_cast<uint8_t>(h[i] >> (24 - j * 8));
        return out;
    }
};
}  // namespace

std::string added_guid(const std::string& registry, const std::string& kiln_id) {
    static const uint8_t ns[16] = {0x6f, 0x6d, 0x2a, 0x44, 0x53, 0xc1, 0x5c, 0x52, 0x9d, 0x51, 0x0a, 0x3c, 0x6e, 0x1f, 0x2b, 0x77};
    std::vector<uint8_t> msg(ns, ns + 16);
    const std::string name = "registry:" + registry + ":" + kiln_id;
    msg.insert(msg.end(), name.begin(), name.end());
    auto d = Sha1{}.digest(msg);
    d[6] = static_cast<uint8_t>((d[6] & 0x0f) | 0x50);
    d[8] = static_cast<uint8_t>((d[8] & 0x3f) | 0x80);
    static constexpr char hx[] = "0123456789abcdef";
    std::string s;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) s += '-';
        s += hx[d[static_cast<size_t>(i)] >> 4];
        s += hx[d[static_cast<size_t>(i)] & 15];
    }
    return s;
}

}  // namespace kc::registry::model
