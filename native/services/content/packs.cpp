// SPDX-License-Identifier: MIT
// kiln.content: the data-pack format (docs/kiln/services/content.md), pack discovery, reports and the dump.
//
//   { "items":   { "<selector>": { "@where": {...}, "<field path>": <value | op>, ... }, ... },
//     "recipes": { "<selector>": { "<field>": ..., "inputs": [...] | {op}, "outputs": [...] | {op} } },
//     "loot":    { "<item selector>": { "stackSizeMin": 5, ... } },
//     "drops":   { "<table GUID | glob>": { "count": {op}, "chance": {op}, "items": { "<item>": { "countMin": ... } } } },
//     "options": { "stackGuard": true } }
//
// Selectors: a debug name, "0x1234abcd" / decimal id, or a case-insensitive glob ("Material_*", "*"). Values: numbers,
// booleans, enum names, flag-name arrays, keen::Time in seconds, or an op relative to the game's original value:
// {"mul": 4, "add": 0, "min": 1, "max": 9999, "raiseOnly": true}.
#include <windows.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <sstream>

#include "content.h"
#include "nlohmann/json.hpp"
#include "refs.h"
#include "services/kiln_profile.h"
#if KC_GAMEDATA
#include "gamedata.h"
#endif

namespace kc {

using json = nlohmann::ordered_json;
namespace fs = std::filesystem;

namespace {

fs::path upath(const std::string& s) { return fs::path(std::u8string(s.begin(), s.end())); }

bool glob_ci(const char* p, const char* s) {
    // iterative '*' / '?' match, case-insensitive
    const char *star = nullptr, *ss = nullptr;
    auto eq = [](char a, char b) {
        auto lo = [](char c) { return (c >= 'A' && c <= 'Z') ? char(c - 'A' + 'a') : c; };
        return lo(a) == lo(b);
    };
    while (*s) {
        if (*p == '*') {
            star = p++;
            ss = s;
        } else if (*p == '?' || (*p && eq(*p, *s))) {
            ++p;
            ++s;
        } else if (star) {
            p = star + 1;
            s = ++ss;
        } else {
            return false;
        }
    }
    while (*p == '*') ++p;
    return *p == 0;
}

bool has_wild(const std::string& s) { return s.find_first_of("*?") != std::string::npos; }

std::vector<const Entry*> select(const Registry& r, const std::string& key) {
    std::vector<const Entry*> out;
    if (!has_wild(key)) {
        if (const Entry* e = r.lookup(key)) out.push_back(e);
        return out;
    }
    for (const Entry& e : r.entries)
        if (glob_ci(key.c_str(), e.name.c_str())) out.push_back(&e);
    return out;
}

// ---- values ----
bool enum_value(const Field& f, const std::string& name, int64_t* v) {
    if (!f.en) return false;
    for (auto& [n, x] : f.en->values)
        if (n == name || lower(n) == lower(name)) {
            *v = x;
            return true;
        }
    return false;
}

bool number_of(const json& j, double* d) {
    if (j.is_number()) {
        *d = j.get<double>();
        return true;
    }
    return false;
}

bool parse_value(const Field& f, const json& j, const KilnContentValue& orig, KilnContentValue* out, std::string* err) {
    *out = KilnContentValue{};
    out->type = f.vt;
    auto fail = [&](const std::string& s) {
        *err = f.path + ": " + s;
        return false;
    };
    if (j.is_boolean()) {
        out->type = KILN_CV_BOOL;
        out->i = j.get<bool>();
        out->f = static_cast<double>(out->i);
        return true;
    }
    if (j.is_number_integer() && f.vt != KILN_CV_FLOAT && f.vt != KILN_CV_TIME) {
        out->type = KILN_CV_INT;
        out->i = j.is_number_unsigned() ? static_cast<int64_t>(j.get<uint64_t>()) : j.get<int64_t>();
        out->f = static_cast<double>(out->i);
        return true;
    }
    if (j.is_number()) {
        out->type = f.vt == KILN_CV_TIME ? KILN_CV_TIME : KILN_CV_FLOAT;
        out->f = j.get<double>();
        return true;
    }
    if (j.is_string()) {
        const std::string s = j.get<std::string>();
        int64_t v = 0;
        if (f.enum_kind != KILN_CONTENT_PLAIN && enum_value(f, s, &v)) {
            out->type = KILN_CV_INT;
            out->i = f.enum_kind == KILN_CONTENT_FLAGS ? (1ll << v) : v;
            return true;
        }
        if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')) {
            out->type = KILN_CV_INT;
            out->i = static_cast<int64_t>(std::strtoull(s.c_str() + 2, nullptr, 16));
            return true;
        }
        return fail("unknown value \"" + s + "\"" + (f.en ? " (not a name of " + f.en->type + ")" : ""));
    }
    if (j.is_array() && f.enum_kind == KILN_CONTENT_FLAGS) {
        int64_t bits = 0;
        for (const json& x : j) {
            int64_t v = 0;
            if (x.is_string() && enum_value(f, x.get<std::string>(), &v)) bits |= 1ll << v;
            else if (x.is_number_integer()) bits |= 1ll << x.get<int64_t>();
            else return fail("flag names expected");
        }
        out->type = KILN_CV_INT;
        out->i = bits;
        return true;
    }
    if (j.is_object()) {
        if (j.contains("set")) return parse_value(f, j["set"], orig, out, err);
        double base = as_double(orig), mul = 1, add = 0, lo = -INFINITY, hi = INFINITY;
        if (j.contains("mul") && !number_of(j["mul"], &mul)) return fail("\"mul\" must be a number");
        if (j.contains("add") && !number_of(j["add"], &add)) return fail("\"add\" must be a number");
        if (j.contains("min") && !number_of(j["min"], &lo)) return fail("\"min\" must be a number");
        if (j.contains("max") && !number_of(j["max"], &hi)) return fail("\"max\" must be a number");
        double r = base * mul + add;
        r = std::clamp(r, lo, hi);
        if (j.value("raiseOnly", false)) r = std::max(r, base);
        if (j.value("lowerOnly", false)) r = std::min(r, base);
        if (f.vt == KILN_CV_INT || f.vt == KILN_CV_BOOL) {
            out->type = KILN_CV_INT;
            out->i = static_cast<int64_t>(std::llround(r));
            out->f = r;
        } else {
            out->type = f.vt == KILN_CV_TIME ? KILN_CV_TIME : KILN_CV_FLOAT;
            out->f = r;
        }
        return true;
    }
    return fail("unsupported value");
}

// ---- @where ----
bool compare(double a, const std::string& cond, bool* ok) {
    const char* ops[] = {">=", "<=", "!=", "==", ">", "<"};
    for (const char* op : ops) {
        const size_t n = std::strlen(op);
        if (cond.compare(0, n, op) == 0) {
            char* end = nullptr;
            const double b = std::strtod(cond.c_str() + n, &end);
            if (!end || end == cond.c_str() + n) return false;
            if (!std::strcmp(op, ">=")) *ok = a >= b;
            else if (!std::strcmp(op, "<=")) *ok = a <= b;
            else if (!std::strcmp(op, "!=")) *ok = a != b;
            else if (!std::strcmp(op, "==")) *ok = a == b;
            else if (!std::strcmp(op, ">")) *ok = a > b;
            else *ok = a < b;
            return true;
        }
    }
    return false;
}

bool cond_ok(const Field& f, const KilnContentValue& v, const json& c, std::string* err) {
    if (c.is_array()) {
        for (const json& x : c)
            if (cond_ok(f, v, x, err)) return true;
        return false;
    }
    const double a = as_double(v);
    if (c.is_boolean()) return (v.i != 0) == c.get<bool>();
    if (c.is_number()) return a == c.get<double>();
    if (c.is_string()) {
        std::string s = c.get<std::string>();
        bool ok = false;
        if (compare(a, s, &ok)) return ok;
        const bool neg = !s.empty() && s[0] == '!';
        if (neg) s = s.substr(1);
        int64_t ev = 0;
        if (f.enum_kind == KILN_CONTENT_ENUM && enum_value(f, s, &ev)) return (v.i == ev) != neg;
        if (f.enum_kind == KILN_CONTENT_FLAGS && enum_value(f, s, &ev)) return (((v.i >> ev) & 1) != 0) != neg;
        *err = "@where " + f.path + ": cannot use \"" + c.get<std::string>() + "\"";
        return false;
    }
    *err = "@where " + f.path + ": unsupported condition";
    return false;
}

bool any_stack(const Entry& e, uint32_t which, const std::string& sel) {
    std::vector<KilnContentStack> st;
    if (!read_recipe_stacks(e, which, &st)) return false;
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    for (const KilnContentStack& s : st) {
        const Entry* it = items.get(s.item);
        if (!it) continue;
        if (has_wild(sel) ? glob_ci(sel.c_str(), it->name.c_str()) : items.lookup(sel) == it) return true;
    }
    return false;
}

bool where_ok(const Registry& r, const Entry& e, const json& w, std::string* err) {
    if (!w.is_object()) {
        *err = "@where must be an object";
        return false;
    }
    for (auto& [k, c] : w.items()) {
        if (r.kind == KILN_CONTENT_RECIPES && (k == "output" || k == "input")) {
            if (!c.is_string() || !any_stack(e, k == "output" ? KILN_CONTENT_OUTPUTS : KILN_CONTENT_INPUTS, c.get<std::string>()))
                return false;
            continue;
        }
        if (k == "name") {
            if (!c.is_string() || !glob_ci(c.get<std::string>().c_str(), e.name.c_str())) return false;
            continue;
        }
        const Field* f = r.schema.find(k);
        if (!f) {
            *err = "@where: unknown field " + k;
            return false;
        }
        if (!cond_ok(*f, original_value(e, *f), c, err)) return false;
    }
    return true;
}

// ---- recipe lists ----
bool parse_stacks(const json& j, bool inputs, const std::vector<KilnContentStack>& orig, std::vector<KilnContentStack>* out,
                  std::string* err) {
    out->clear();
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    if (j.is_object()) {  // scale the original amounts
        double mul = 1, lo = 1, hi = 65535, add = 0;
        number_of(j.value("mul", json(1.0)), &mul);
        if (j.contains("add")) number_of(j["add"], &add);
        if (j.contains("min")) number_of(j["min"], &lo);
        if (j.contains("max")) number_of(j["max"], &hi);
        for (KilnContentStack s : orig) {
            s.count = static_cast<uint32_t>(std::clamp(std::llround(s.count * mul + add), static_cast<long long>(std::max(1.0, lo)),
                                                       static_cast<long long>(std::min(65535.0, hi))));
            s.count_max = s.count;
            out->push_back(s);
        }
        return true;
    }
    if (!j.is_array()) {
        *err = std::string(inputs ? "inputs" : "outputs") + ": a list [{\"item\": ..., \"count\": n}] or {\"mul\": x}";
        return false;
    }
    for (const json& x : j) {
        KilnContentStack s{};
        s.chance = 1;
        if (!x.is_object()) {
            *err = "list entries are objects";
            return false;
        }
        const json cnt = x.value("count", json(1));
        if (!cnt.is_number_integer() || cnt.get<int64_t>() < 1 || cnt.get<int64_t>() > 65535) {
            *err = "count must be 1..65535";
            return false;
        }
        s.count = s.count_max = static_cast<uint32_t>(cnt.get<int64_t>());
        if (x.contains("item") && x["item"].is_string()) {
            const Entry* it = items.lookup(x["item"].get<std::string>());
            if (!it) {
                *err = "unknown item " + x["item"].get<std::string>();
                return false;
            }
            s.item = it->id;
        } else if (inputs && x.contains("category")) {
            const json& c = x["category"];
            if (c.is_string()) s.category = static_cast<uint32_t>(std::strtoull(c.get<std::string>().c_str(), nullptr, 0));
            else if (c.is_number_integer()) s.category = c.get<uint32_t>();
            if (!s.category) {
                *err = "category must be an id (\"0x...\")";
                return false;
            }
        } else {
            *err = "entry needs \"item\"" + std::string(inputs ? " or \"category\"" : "");
            return false;
        }
        out->push_back(s);
    }
    return true;
}

// ---- drop rules ----
struct DropRule {
    KilnOwner owner;
    uint64_t seq;
    std::string selector, source;
    json body;
};
std::vector<DropRule> g_rules;  // under store_mutex

uint64_t g_seq_counter = 0;
uint64_t take_seq(uint64_t base) { return base ? base + (++g_seq_counter & 0xFFFFFFFFull) : next_code_seq(); }

void apply_rule(const DropRule& r, const Entry& table, std::vector<std::string>* errors) {
    const Registry& D = reg(KILN_CONTENT_DROPS);
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    const Field* fmin = D.schema.find("countMin");
    const Field* fmax = D.schema.find("countMax");
    const Field* fch = D.schema.find("spawnProbability");
    if (!fmin || !fmax || !fch) return;
    std::vector<DropStackRef> refs;
    walk_drop_table(table.addr, &refs);
    for (size_t i = 0; i < refs.size(); ++i) {
        Entry st{};
        st.id = table.id;
        st.addr = refs[i].addr;
        uint32_t item = 0;
        rdv(refs[i].addr + DL.s_item, item);
        auto put = [&](const Field* f, const json& op) {
            KilnContentValue v{};
            std::string err;
            if (!parse_value(*f, op, original_value(st, *f), &v, &err)) {
                if (errors) errors->push_back("drops " + r.selector + ": " + err);
                return;
            }
            if (f == fch) v.f = std::clamp(v.type == KILN_CV_INT ? static_cast<double>(v.i) : v.f, 0.0, 1.0), v.type = KILN_CV_FLOAT;
            uint8_t b[8];
            if (!encode(*f, v, b, &err)) {
                if (errors) errors->push_back("drops " + r.selector + ": " + err);
                return;
            }
            store_put(r.owner, r.seq, KILN_CONTENT_DROPS, table.id, static_cast<int32_t>(f - D.schema.fields.data()),
                      static_cast<int32_t>(i), refs[i].addr + f->offset, f->bytes, b, r.source);
        };
        if (r.body.contains("count")) {
            put(fmin, r.body["count"]);
            put(fmax, r.body["count"]);
        }
        if (r.body.contains("chance")) put(fch, r.body["chance"]);
        if (r.body.contains("items") && r.body["items"].is_object()) {
            const Entry* it = items.get(item);
            for (auto& [sel, fields] : r.body["items"].items()) {
                if (!it || !(has_wild(sel) ? glob_ci(sel.c_str(), it->name.c_str()) : items.lookup(sel) == it)) continue;
                if (!fields.is_object()) continue;
                for (auto& [path, val] : fields.items()) {
                    const Field* f = D.schema.find(path);
                    if (!f || !f->writable) {
                        if (errors) errors->push_back("drops " + r.selector + ": unknown or read-only field " + path);
                        continue;
                    }
                    put(f, val);
                }
            }
        }
    }
}

bool rule_matches(const DropRule& r, const Entry& table) {
    return r.selector == "*" || glob_ci(r.selector.c_str(), table.name.c_str());
}

std::string read_file(const fs::path& p, bool* ok) {
    std::ifstream f(p, std::ios::binary);
    *ok = static_cast<bool>(f);
    std::stringstream ss;
    ss << f.rdbuf();
    return ss.str();
}

std::string api_path(int kind) {
    char b[1024];
    size_t n = 0;
    if (g_api->path(kind, b, sizeof b, &n) != KILN_OK) return {};
    return std::string(b, n);
}

std::unordered_map<std::string, KilnOwner> g_pack_owners;  // mod id -> owner
uint64_t g_generation = 0;

}  // namespace

std::string mods_dir() { return api_path(KILN_PATH_MODS); }
std::string data_dir() { return api_path(KILN_PATH_DATA); }

bool path_exists(const std::string& utf8_path) {
    std::error_code ec;
    return fs::exists(upath(utf8_path), ec);
}

void drops_apply_rules(const Entry& table) {
    std::lock_guard lock(store_mutex());
    std::vector<const DropRule*> rs;
    for (const DropRule& r : g_rules)
        if (rule_matches(r, table)) rs.push_back(&r);
    std::sort(rs.begin(), rs.end(), [](const DropRule* a, const DropRule* b) { return a->seq < b->seq; });
    for (const DropRule* r : rs) apply_rule(*r, table, nullptr);
}

void drops_drop_owner(KilnOwner owner) {
    std::lock_guard lock(store_mutex());
    std::erase_if(g_rules, [&](const DropRule& r) { return r.owner == owner; });
}

bool drops_have_rules() {
    std::lock_guard lock(store_mutex());
    return !g_rules.empty();
}

void drops_clear_rules() {
    std::lock_guard lock(store_mutex());
    g_rules.clear();
}

// Every ItemInfo of the active registry (refs.cpp pools).
std::vector<uintptr_t> registry_items() {
    const Registry& I = reg(KILN_CONTENT_ITEMS);
    std::vector<uintptr_t> v;
    v.reserve(I.entries.size());
    for (const Entry& e : I.entries) v.push_back(e.addr);
    std::sort(v.begin(), v.end());
    return v;
}

KilnResult apply_kitbash(KilnOwner owner, uint64_t seq, const Entry& e, const std::string& key, const json& value,
                         bool allow_game, const std::string& source, std::vector<std::string>* problems) {
    const Registry& I = reg(KILN_CONTENT_ITEMS);
    // {"mul": 1} / {"reset": true}: this owner's layer holds the game's own value. It can write nothing else, so it needs no opt-in (the content sync's "match the server" sends these).
    const bool object = value.is_object() && key != "refs";
    const bool reset = object && ((value.contains("mul") && value["mul"] == 1) || (value.contains("reset") && value["reset"] == true));
    if (!reset && !nc_is_new(KILN_CONTENT_ITEMS, e.id) && !allow_game) {
        problems->push_back("items " + e.name + "." + key + ": a reference of a game item; changing it needs the opt-in "
                            "(\"@refs\": true in the rule, \"options\": {\"refs\": true}, or the API's allowGameItems)");
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (key == "icon") {
        problems->push_back("items " + e.name + ".icon: icons are part of a definition (\"icon\" in the item's \"add\" entry)");
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (object) {
        KitbashOut probe;
        const ItemFind self = [&](const std::string&) -> uintptr_t { return e.addr; };
        kitbash(I.schema, key, json("item:x"), self, registry_items(), &probe);
        for (const RefWrite& w : probe.writes) {
            uint8_t original[sizeof w.bytes];
            if (!store_original(e.addr + w.f->offset, w.f->bytes, original)) rd(e.addr + w.f->offset, original, w.f->bytes);  // no layer yet: memory holds the original
            const int32_t idx = static_cast<int32_t>(w.f - I.schema.fields.data());
            if (!store_put(owner, seq, KILN_CONTENT_ITEMS, e.id, idx, -1, e.addr + w.f->offset, w.f->bytes, original, source))
                problems->push_back("items " + e.name + "." + w.f->path + ": not writable");
        }
        return KILN_OK;
    }
    const ItemFind find = [&](const std::string& sel) -> uintptr_t {
        const Entry* x = I.lookup(sel);
        return x ? x->addr : 0;
    };
    KitbashOut ko;
    kitbash(I.schema, key, value, find, registry_items(), &ko, e.addr);
    for (const std::string& pr : ko.problems) problems->push_back("items " + e.name + ": " + pr);
    for (const RefWrite& w : ko.writes) {
        const int32_t idx = static_cast<int32_t>(w.f - I.schema.fields.data());
        if (!store_put(owner, seq, KILN_CONTENT_ITEMS, e.id, idx, -1, e.addr + w.f->offset, w.f->bytes, w.bytes, source))
            problems->push_back("items " + e.name + "." + w.f->path + ": not writable");
    }
    for (const BlobWrite& b : ko.blobs) {  // a list header (8 bytes) pointed at the other item's list
        const uintptr_t h = e.addr + b.offset;
        const int64_t rel = b.data ? static_cast<int64_t>(b.data) - static_cast<int64_t>(h) : 0;
        if (rel < INT32_MIN || rel > INT32_MAX) {
            problems->push_back("items " + e.name + "." + b.path + ": the other item's list is out of reach");
            continue;
        }
        uint8_t hb[8];
        const int32_t off = static_cast<int32_t>(rel);
        const uint32_t n = b.data ? b.count : 0;
        std::memcpy(hb, &off, 4);
        std::memcpy(hb + 4, &n, 4);
        store_put(owner, seq, KILN_CONTENT_ITEMS, e.id, -1, kSubBlob, h, 8, hb, source);
    }
    return ko.writes.empty() && ko.blobs.empty() && !ko.problems.empty() ? KILN_ERR_INVALID_ARGUMENT : KILN_OK;
}

bool apply_pack(KilnOwner owner, uint64_t seq_base, const std::string& text, const std::string& source, ApplyResult* out) {
    json doc = json::parse(text, nullptr, false, true);
    if (doc.is_discarded() || !doc.is_object()) {
        out->errors.push_back(source + ": not a JSON object");
        return false;
    }
    if (doc.contains("format") && (!doc["format"].is_number_integer() || doc["format"].get<int>() != 1)) {
        out->errors.push_back(source + ": format " + doc["format"].dump() + " is not supported (1)");
        return false;
    }
    std::lock_guard lock(store_mutex());
    g_seq_counter = 0;
    // "options": {"refs": true}: this document may change references of game items (kitbash opt-in)
    const bool doc_refs = doc.contains("options") && doc["options"].is_object() && doc["options"].value("refs", false) == true;
#if KC_GAMEDATA
    gd_pack_adds(owner, seq_base, doc, source, out);  // the gamedata sections' "add" lists first: item rules may name them
#endif
    auto section = [&](const char* name, uint32_t kind) {
        if (!doc.contains(name)) return;
        const json& sec = doc[name];
        const Registry& r = reg(kind);
        if (!sec.is_object()) {
            out->errors.push_back(std::string(name) + ": must be an object { \"<selector>\": { fields } }");
            return;
        }
        if (!r.available) {
            out->errors.push_back(std::string(name) + ": this registry is not available on this build");
            return;
        }
        for (auto& [sel, body] : sec.items()) {
            if (sel == "add" && body.is_array()) {  // new items / recipes (newcontent.cpp); later rules can use them
                nc_pack_add(owner, kind, body.dump(), source, out);
                continue;
            }
            if (!body.is_object()) {
                out->errors.push_back(std::string(name) + " " + sel + ": must be an object of fields");
                continue;
            }
            const std::vector<const Entry*> hits = select(r, sel);
            if (hits.empty()) {
                out->warnings.push_back(std::string(name) + " " + sel + ": matches nothing");
                continue;
            }
            const json* where = body.contains("@where") ? &body["@where"] : nullptr;
            uint32_t matched = 0;
            std::vector<std::string> field_errors;
            for (const Entry* e : hits) {
                std::string werr;
                if (where && !where_ok(r, *e, *where, &werr)) {
                    if (!werr.empty()) {
                        out->errors.push_back(std::string(name) + " " + sel + ": " + werr);
                        break;
                    }
                    continue;
                }
                ++matched;
                const std::string src = source + ": " + name + " " + sel;
                for (auto& [path, val] : body.items()) {
                    if (path.empty() || path[0] == '@') continue;
                    std::string err;
                    if (kind == KILN_CONTENT_RECIPES && (path == "inputs" || path == "outputs")) {
                        const uint32_t which = path == "inputs" ? KILN_CONTENT_INPUTS : KILN_CONTENT_OUTPUTS;
                        std::vector<KilnContentStack> orig, want;
                        // the original list: the slot's original header if changed, else memory
                        Entry o = *e;
                        uint8_t hb[8];
                        const uintptr_t h = e->addr + (which == KILN_CONTENT_INPUTS ? RL.input : RL.output);
                        read_recipe_stacks(*e, which, &orig);
                        if (store_original(h, 8, hb)) {
                            // decode the original list from the original header bytes
                            int32_t off;
                            uint32_t n;
                            std::memcpy(&off, hb, 4);
                            std::memcpy(&n, hb + 4, 4);
                            (void)o;
                            orig.clear();
                            const uint32_t stride = which == KILN_CONTENT_INPUTS ? RL.in_stride : RL.out_stride;
                            for (uint32_t q = 0; q < n && q < 256; ++q) {
                                const uintptr_t p = h + static_cast<intptr_t>(off) + static_cast<uintptr_t>(q) * stride;
                                KilnContentStack s{};
                                s.chance = 1;
                                if (which == KILN_CONTENT_INPUTS) {
                                    uint32_t ccount = 0;
                                    rdv(p + RL.in_item, s.item), rdv(p + RL.in_count, s.count), rdv(p + RL.in_cat, s.category),
                                        rdv(p + RL.in_cat_count, ccount);
                                    if (!s.item) s.count = ccount;
                                } else {
                                    rdv(p + RL.out_item, s.item), rdv(p + RL.out_count, s.count);
                                }
                                s.count_max = s.count;
                                orig.push_back(s);
                            }
                        }
                        if (!parse_stacks(val, which == KILN_CONTENT_INPUTS, orig, &want, &err) ||
                            set_recipe_stacks(owner, take_seq(seq_base), *e, which, want, src, &err) != KILN_OK) {
                            field_errors.push_back(std::string(name) + " " + e->name + "." + path + ": " + err);
                            continue;
                        }
                        ++out->applied;
                        continue;
                    }
                    if (kind == KILN_CONTENT_ITEMS && is_kitbash_key(r.schema, path)) {
                        const bool allow = doc_refs || (body.contains("@refs") && body["@refs"] == true);
                        if (apply_kitbash(owner, take_seq(seq_base), *e, path, val, allow, src, &field_errors) == KILN_OK) ++out->applied;
                        continue;
                    }
                    const Field* f = r.schema.find(path);
                    if (!f) {
#if KC_GAMEDATA
                        if (kind == KILN_CONTENT_ITEMS) {  // item config paths (impactValues.ManaCost, uiValues[2].value, ...)
                            std::string gerr;
                            const int g = gd_item_path(owner, take_seq(seq_base), *e, path, val, src, &gerr);
                            if (g > 0) {
                                ++out->applied;
                                continue;
                            }
                            if (g < 0) {
                                field_errors.push_back("items " + e->name + ": " + gerr);
                                continue;
                            }
                        }
#endif
                        field_errors.push_back(std::string(name) + " " + sel + ": unknown field " + path);
                        continue;
                    }
                    KilnContentValue v{};
                    if (!parse_value(*f, val, original_value(*e, *f), &v, &err) ||
                        set_field(owner, take_seq(seq_base), kind, *e, *f, v, src, &err) != KILN_OK) {
                        field_errors.push_back(std::string(name) + " " + e->name + ": " + err);
                        continue;
                    }
                    ++out->applied;
                }
            }
            // one line per distinct problem, not one per matched entry
            std::sort(field_errors.begin(), field_errors.end());
            field_errors.erase(std::unique(field_errors.begin(), field_errors.end()), field_errors.end());
            if (field_errors.size() > 20) {
                const size_t more = field_errors.size() - 20;
                field_errors.resize(20);
                field_errors.push_back("... and " + std::to_string(more) + " more");
            }
            for (auto& e : field_errors) out->errors.push_back(e);
            if (where && matched == 0) out->warnings.push_back(std::string(name) + " " + sel + ": @where matches nothing");
        }
    };
    section("items", KILN_CONTENT_ITEMS);
    section("recipes", KILN_CONTENT_RECIPES);
    section("loot", KILN_CONTENT_LOOT);
    if (doc.contains("drops")) {
        const json& d = doc["drops"];
        if (!d.is_object()) out->errors.push_back("drops: must be an object");
        else if (!reg(KILN_CONTENT_DROPS).available) out->errors.push_back("drops: not available on this build");
        else {
            for (auto& [sel, body] : d.items()) {
                if (!body.is_object()) {
                    out->errors.push_back("drops " + sel + ": must be an object");
                    continue;
                }
                DropRule r{owner, take_seq(seq_base), sel, source + ": drops " + sel, body};
                g_rules.push_back(r);
                ++out->applied;
                const Registry& D = reg(KILN_CONTENT_DROPS);
                for (const Entry& t : D.entries)
                    if (rule_matches(r, t)) apply_rule(r, t, &out->errors);
            }
        }
    }
#if KC_GAMEDATA
    gd_pack_sections(owner, seq_base, doc, source, out);  // templates, arsenals, sequences, buffs, perks, skills
#endif
    if (doc.contains("options") && doc["options"].is_object()) {
        const json& o = doc["options"];
        for (auto& [k, v] : o.items())
            if (k != "stackGuard" && k != "refs") out->warnings.push_back("options." + k + ": unknown option ignored");
        if (o.contains("stackGuard") && o["stackGuard"].is_boolean()) {
            std::string why;
            if (!guard_set(o["stackGuard"].get<bool>(), &why)) out->warnings.push_back("options.stackGuard: " + why);
        }
    }
    for (auto& [k, v] : doc.items())
        if (k != "items" && k != "recipes" && k != "loot" && k != "drops" && k != "options" && k != "format" &&
            k[0] != '@' && k != "comment"
#if KC_GAMEDATA
            && !gd_is_section(k)
#endif
        )
            out->warnings.push_back("unknown section \"" + k + "\" ignored");
    std::string ferr;
    store_flush(&ferr);
    if (!ferr.empty()) out->errors.push_back("writing: " + ferr);
    return true;
}

// ============================================================================================= discovery =======

namespace {
// The mod folders whose content/ is searched: kiln.profile's managed mods (A6: the launcher's profile, in its order,
// then kiln/mods when the profile keeps that drop-in folder), else (no profile service yet, or the legacy layout)
// every folder of kiln/mods in name order. A profile with no mods / an invalid one: none.
struct ModDir {
    fs::path dir;
    std::string folder;
};
std::vector<ModDir> mod_dirs(std::string* signature) {
    std::vector<ModDir> out;
    std::error_code ec;
    bool legacy = true, dropins = true;
    if (const auto* pr = static_cast<const KilnProfileApi*>(
            g_api->service(KilnStr{KILN_PROFILE_SERVICE, std::strlen(KILN_PROFILE_SERVICE)}, 1));
        pr && pr->mode && pr->count && pr->get && pr->dropins) {
        const uint32_t mode = pr->mode();
        if (mode == KILN_PROFILE_NO_MODS || mode == KILN_PROFILE_INVALID) {
            *signature += "P:none;";
            return out;
        }
        if (mode == KILN_PROFILE_ACTIVE || mode == KILN_PROFILE_ONCE) {
            legacy = false;
            dropins = (pr->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
            const uint32_t n = pr->count(KILN_PROFILE_KIND_KILN);
            for (uint32_t i = 0; i < n; ++i) {
                KilnProfileMod m{};
                m.size = sizeof m;
                if (pr->get(KILN_PROFILE_KIND_KILN, i, &m) != KILN_OK || !m.path.ptr || !m.path.len) continue;
                const std::string path(m.path.ptr, m.path.len);
                fs::path d = upath(path);
                std::string folder = reinterpret_cast<const char*>(d.filename().u8string().c_str());
                if (!m.enabled) {
                    *signature += "D:" + folder + ";";
                    continue;
                }
                out.push_back(ModDir{d, folder});
            }
            *signature += "P:" + std::to_string(n) + ";";
        }
    }
    (void)legacy;
    if (!dropins) return out;
    const fs::path root = upath(mods_dir());
    if (root.empty() || !fs::is_directory(root, ec)) return out;
    std::vector<fs::path> dirs;
    for (auto it = fs::directory_iterator(root, ec); !ec && it != fs::directory_iterator(); it.increment(ec))
        if (it->is_directory(ec)) dirs.push_back(it->path());
    std::sort(dirs.begin(), dirs.end(), [](const fs::path& a, const fs::path& b) {
        return a.filename().u8string() < b.filename().u8string();  // Kiln.Host: StringComparer.Ordinal
    });
    for (const fs::path& d : dirs) out.push_back(ModDir{d, reinterpret_cast<const char*>(d.filename().u8string().c_str())});
    return out;
}
thread_local std::string t_pack_dir;  // the mod folder of the pack being applied (relative icon files)
}  // namespace

const std::string& current_pack_dir() { return t_pack_dir; }

std::vector<PackFile> discover_packs(std::string* signature) {
    std::vector<PackFile> out;
    signature->clear();
    std::error_code ec;
    for (const ModDir& md : mod_dirs(signature)) {
        const fs::path& d = md.dir;
        const std::string& folder = md.folder;
        if (fs::exists(d / ".disabled", ec)) {
            *signature += "D:" + folder + ";";
            continue;
        }
        if (!fs::exists(d / "kiln.mod.json", ec)) continue;
        const fs::path cdir = d / "content";
        if (!fs::is_directory(cdir, ec)) continue;
        std::string id = folder;
        bool ok = false;
        const std::string mt = read_file(d / "kiln.mod.json", &ok);
        json m = json::parse(mt, nullptr, false, true);
        if (!m.is_discarded() && m.is_object() && m.contains("id") && m["id"].is_string()) id = m["id"].get<std::string>();
        if (std::any_of(out.begin(), out.end(), [&](const PackFile& f) { return f.mod == id && f.dir != reinterpret_cast<const char*>(d.u8string().c_str()); }))
            continue;  // the same mod id twice (profile + drop-in): the first wins, as in Kiln.Host
        std::vector<fs::path> files;
        for (auto it = fs::directory_iterator(cdir, ec); !ec && it != fs::directory_iterator(); it.increment(ec))
            if (it->is_regular_file(ec) && lower(reinterpret_cast<const char*>(it->path().extension().u8string().c_str())) == ".json")
                files.push_back(it->path());
        std::sort(files.begin(), files.end(), [](const fs::path& a, const fs::path& b) {
            return a.filename().u8string() < b.filename().u8string();
        });
        for (const fs::path& f : files) {
            PackFile p;
            p.mod = id;
            p.dir = reinterpret_cast<const char*>(d.u8string().c_str());
            p.path = reinterpret_cast<const char*>(f.u8string().c_str());
            p.rel = folder + "/content/" + reinterpret_cast<const char*>(f.filename().u8string().c_str());
            p.size = fs::file_size(f, ec);
            const auto t = fs::last_write_time(f, ec);
            p.mtime = static_cast<uint64_t>(t.time_since_epoch().count());
            *signature += p.rel + "|" + std::to_string(p.size) + "|" + std::to_string(p.mtime) + ";";
            out.push_back(std::move(p));
        }
    }
    return out;
}

std::vector<PackFile> order_packs(const std::vector<PackFile>& files, const std::vector<std::string>& order,
                                  std::vector<std::string>* skipped) {
    std::vector<PackFile> out;
    for (const std::string& id : order)
        for (const PackFile& f : files)
            if (f.mod == id) out.push_back(f);
    for (const PackFile& f : files)
        if (std::find(order.begin(), order.end(), f.mod) == order.end() &&
            std::find(skipped->begin(), skipped->end(), f.mod) == skipped->end())
            skipped->push_back(f.mod);
    return out;
}

PacksReport apply_all_packs(const std::vector<PackFile>& files) {
    std::lock_guard lock(store_mutex());
    PacksReport rep;
    rep.generation = ++g_generation;
    for (auto& [id, owner] : g_pack_owners) {
        store_drop_owner(owner);
        drops_drop_owner(owner);
    }
    std::vector<std::string> order;
    for (const PackFile& p : files)
        if (std::find(order.begin(), order.end(), p.mod) == order.end()) order.push_back(p.mod);
    uint32_t file_index = 0;
    for (const PackFile& p : files) {
        auto it = g_pack_owners.find(p.mod);
        if (it == g_pack_owners.end()) {
            const std::string label = "content:" + p.mod;
            const KilnOwner o = g_api->owner_create(KilnStr{label.data(), label.size()});
            it = g_pack_owners.emplace(p.mod, o).first;
            set_owner_label(o, "pack " + p.mod);
        }
        const uint64_t mod_index = static_cast<uint64_t>(std::find(order.begin(), order.end(), p.mod) - order.begin());
        const uint64_t base = ((mod_index + 1) << 44) | (static_cast<uint64_t>(++file_index & 0xFFF) << 32);
        ApplyResult r;
        bool ok = false;
        const std::string text = read_file(upath(p.path), &ok);
        t_pack_dir = p.dir;
        if (!ok) r.errors.push_back("cannot read the file");
        else {
            PhaseTimer ft(PH_PACK_FILE);
            apply_pack(it->second, base, text, p.rel, &r);
        }
        t_pack_dir.clear();
        rep.files.emplace_back(p.rel, std::move(r));
    }
    std::string ferr;
    store_flush(&ferr);
    rep.conflicts = store_conflicts();
    return rep;
}

std::string apply_result_json(const ApplyResult& r) {
    json j = {{"applied", r.applied}, {"errors", r.errors}, {"warnings", r.warnings}};
    json c = json::array();
    for (const Conflict& x : store_conflicts())
        c.push_back({{"field", x.what}, {"winner", x.winner}, {"winnerValue", x.winner_value}, {"loser", x.loser}, {"loserValue", x.loser_value}});
    j["conflicts"] = c;
    return j.dump();
}

std::string report_json(const PacksReport& rep) {
    json j;
    j["generation"] = rep.generation;
    json packs = json::array();
    for (auto& [rel, r] : rep.files)
        packs.push_back({{"file", rel}, {"applied", r.applied}, {"errors", r.errors}, {"warnings", r.warnings}});
    j["packs"] = packs;
    j["skipped"] = rep.skipped;
    json c = json::array();
    for (const Conflict& x : rep.conflicts)
        c.push_back({{"field", x.what}, {"winner", x.winner}, {"winnerValue", x.winner_value}, {"loser", x.loser}, {"loserValue", x.loser_value}});
    j["conflicts"] = c;
    return j.dump(1);
}

// ============================================================================================= dump ============

namespace {
json value_json(const Field& f, const KilnContentValue& v) {
    switch (f.vt) {
        case KILN_CV_BOOL: return v.i != 0;
        case KILN_CV_INT:
            if (f.enum_kind == KILN_CONTENT_ENUM && f.en)
                for (auto& [n, x] : f.en->values)
                    if (x == v.i) return n;
            if (f.enum_kind == KILN_CONTENT_FLAGS && f.en) {
                json a = json::array();
                uint64_t rest = static_cast<uint64_t>(v.i);
                for (auto& [n, x] : f.en->values)
                    if (x >= 0 && x < 64 && ((rest >> x) & 1)) {
                        a.push_back(n);
                        rest &= ~(1ull << x);
                    }
                if (rest) a.push_back(rest);
                return a;
            }
            if (!f.is_signed && f.bytes == 8) return static_cast<uint64_t>(v.i);
            return v.i;
        case KILN_CV_FLOAT: return v.f;
        case KILN_CV_TIME: return v.f;
        case KILN_CV_GUID: return guid_text(v.guid);
        default: return nullptr;
    }
}

bool zero(const Field& f, const KilnContentValue& v) {
    if (f.vt == KILN_CV_GUID) {
        for (uint8_t b : v.guid)
            if (b) return false;
        return true;
    }
    return f.vt == KILN_CV_FLOAT ? v.f == 0 : v.i == 0;
}

json stacks_json(const std::vector<KilnContentStack>& st, bool drops) {
    const Registry& items = reg(KILN_CONTENT_ITEMS);
    json a = json::array();
    for (const KilnContentStack& s : st) {
        json x;
        if (s.item) {
            const Entry* it = items.get(s.item);
            x["item"] = it ? it->name : std::to_string(s.item);
            char b[16];
            std::snprintf(b, sizeof b, "0x%08x", s.item);
            x["id"] = b;
        } else {
            char b[16];
            std::snprintf(b, sizeof b, "0x%08x", s.category);
            x["category"] = b;
        }
        if (drops) {
            x["countMin"] = s.count, x["countMax"] = s.count_max, x["spawnProbability"] = s.chance, x["weight"] = s.weight,
            x["group"] = s.group;
        } else {
            x["count"] = s.count;
        }
        a.push_back(x);
    }
    return a;
}

json registry_json(uint32_t kind) {
    const Registry& r = reg(kind);
    json out;
    out["available"] = r.available;
    out["type"] = r.schema.type;
    out["size"] = r.schema.size;
    json schema = json::array();
    for (const Field& f : r.schema.fields) {
        json fj = {{"path", f.path}, {"type", f.type_name}, {"offset", f.offset}, {"bytes", f.bytes}, {"writable", f.writable}};
        if (f.vt == KILN_CV_TIME) fj["unit"] = "seconds";
        if (f.en && !f.en->values.empty()) {
            json e = json::array();
            for (auto& [n, v] : f.en->values) e.push_back(n);
            fj[f.enum_kind == KILN_CONTENT_FLAGS ? "flags" : "enum"] = e;
        }
        schema.push_back(fj);
    }
    out["schema"] = schema;
    // changed fields: address -> slot
    std::unordered_map<uintptr_t, const Slot*> changed;
    store_for_each([&](uintptr_t a, const Slot& s) {
        if (s.kind == kind) changed.emplace(a, &s);
    });
    json entries = json::array();
    for (const Entry& e : r.entries) {
        json ej;
        char b[16];
        std::snprintf(b, sizeof b, "0x%08x", e.id);
        ej["id"] = b;
        ej["name"] = e.name;
        if (kind != KILN_CONTENT_DROPS) {
            bool any = false;
            for (uint8_t g : e.guid) any = any || g;
            if (any) ej["guid"] = guid_text(e.guid);
        }
        if (kind == KILN_CONTENT_DROPS) {
            std::vector<DropStackRef> refs;
            walk_drop_table(e.addr, &refs);
            std::vector<KilnContentStack> st(refs.size());
            for (size_t i = 0; i < refs.size(); ++i) read_drop_stack(refs[i].addr, refs[i].group, &st[i]);
            ej["stacks"] = stacks_json(st, true);
        } else {
            json fields = json::object();
            json diff = json::object();
            for (const Field& f : r.schema.fields) {
                const KilnContentValue v = current_value(e, f);
                if (!zero(f, v) && (f.vt != KILN_CV_GUID || f.ref)) fields[f.path] = value_json(f, v);  // references too (kitbash)
                auto c = changed.find(e.addr + f.offset);
                if (c != changed.end()) diff[f.path] = {{"original", value_json(f, decode(f, c->second->original))}, {"current", value_json(f, v)}};
            }
            ej["fields"] = fields;
            if (kind == KILN_CONTENT_RECIPES) {
                std::vector<KilnContentStack> in, outp;
                read_recipe_stacks(e, KILN_CONTENT_INPUTS, &in);
                read_recipe_stacks(e, KILN_CONTENT_OUTPUTS, &outp);
                ej["inputs"] = stacks_json(in, false);
                ej["outputs"] = stacks_json(outp, false);
                for (uint32_t w = 0; w < 2; ++w)
                    if (changed.count(e.addr + (w ? RL.output : RL.input))) diff[w ? "outputs" : "inputs"] = "changed";
            }
            if (!diff.empty()) ej["changed"] = diff;
        }
        entries.push_back(ej);
    }
    out["count"] = r.entries.size();
    out["entries"] = entries;
    return out;
}
}  // namespace

namespace {
// One field / entry per line: greppable ("grep Material_T1_Wood_Logs dump.json") and still valid JSON.
void write_registry(std::ostream& f, const char* key, uint32_t kind, bool last) {
    json r = registry_json(kind);
    f << " \"" << key << "\": {\"available\": " << r["available"].dump() << ", \"type\": " << r["type"].dump()
      << ", \"size\": " << r["size"].dump() << ", \"count\": " << r["count"].dump() << ",\n  \"schema\": [\n";
    const json& sc = r["schema"];
    for (size_t i = 0; i < sc.size(); ++i) f << "   " << sc[i].dump() << (i + 1 < sc.size() ? ",\n" : "\n");
    f << "  ],\n  \"entries\": [\n";
    const json& en = r["entries"];
    for (size_t i = 0; i < en.size(); ++i) f << "   " << en[i].dump() << (i + 1 < en.size() ? ",\n" : "\n");
    f << "  ]}" << (last ? "\n" : ",\n");
}
}  // namespace

bool dump_all(const std::string& path, std::string* error) {
    std::lock_guard lock(store_mutex());
    std::error_code ec;
    const fs::path p = upath(path);
    fs::create_directories(p.parent_path(), ec);
    std::ofstream f(p, std::ios::binary | std::ios::trunc);
    if (!f) {
        *error = "cannot write " + path;
        return false;
    }
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    f << "{\"kiln\": \"content-dump/1\"";
    if (g_api->build_info(&bi) == KILN_OK) f << ", \"exe_sha256\": " << json(std::string(bi.exe_sha256)).dump();
    json conflicts = json::array();
    for (const Conflict& x : store_conflicts())
        conflicts.push_back({{"field", x.what}, {"winner", x.winner}, {"winnerValue", x.winner_value}, {"loser", x.loser}, {"loserValue", x.loser_value}});
    f << ",\n \"conflicts\": " << conflicts.dump() << ",\n";
    write_registry(f, "items", KILN_CONTENT_ITEMS, false);
    write_registry(f, "recipes", KILN_CONTENT_RECIPES, false);
    write_registry(f, "loot", KILN_CONTENT_LOOT, false);
    write_registry(f, "drops", KILN_CONTENT_DROPS, true);
    f << "}\n";
    return static_cast<bool>(f);
}

// ============================================================================================= export (v2) ===
// The effective changes as a pack document (Kiln.Network's content sync): absolute values keyed by id, so that the
// other side's kiln.content applies exactly this state as one owner's layers.

namespace {
json raw_json(const Field& f, const KilnContentValue& v) {
    switch (f.vt) {
        case KILN_CV_BOOL: return v.i != 0;
        case KILN_CV_INT:
            if (!f.is_signed && f.bytes == 8) return static_cast<uint64_t>(v.i);
            return v.i;
        case KILN_CV_FLOAT:
        case KILN_CV_TIME: return v.f;
        default: return nullptr;
    }
}

std::string hex_id(uint32_t id) {
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", id);
    return b;
}

// The list a recipe header's bytes point to (the header lives at `header`; `hb` = its 8 bytes: i32 self-relative
// offset, u32 count).
bool list_at(uintptr_t header, const uint8_t* hb, uint32_t which, std::vector<KilnContentStack>* out) {
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
            if (!rdv(p + RL.in_item, x.item) || !rdv(p + RL.in_count, x.count) || !rdv(p + RL.in_cat, x.category) ||
                !rdv(p + RL.in_cat_count, ccount))
                return false;
            if (!x.item) x.count = ccount;
        } else if (!rdv(p + RL.out_item, x.item) || !rdv(p + RL.out_count, x.count)) {
            return false;
        }
        out->push_back(x);
    }
    return true;
}

bool same_list(const std::vector<KilnContentStack>& a, const std::vector<KilnContentStack>& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i)
        if (a[i].item != b[i].item || a[i].category != b[i].category || a[i].count != b[i].count) return false;
    return true;
}

json list_json(const std::vector<KilnContentStack>& st, bool inputs) {
    json a = json::array();
    for (const KilnContentStack& s : st) {
        json x = json::object();
        if (s.item) x["item"] = hex_id(s.item);
        else if (inputs && s.category) x["category"] = hex_id(s.category);
        else continue;
        x["count"] = s.count;
        a.push_back(x);
    }
    return a;
}
}  // namespace

std::string export_changes_json(uint32_t kinds, uint32_t* fields) {
    PhaseTimer pt(PH_EXPORT);
    if (!kinds) kinds = (1u << KILN_CONTENT_ITEMS) | (1u << KILN_CONTENT_RECIPES);
    std::lock_guard lock(store_mutex());
    std::map<std::pair<uint32_t, uint32_t>, json> bodies;  // (kind, id) -> fields; ordered: the same state gives the same text
    uint32_t n = 0;
    bool has_refs = false;
    store_for_each([&](uintptr_t addr, const Slot& s) {
        if (s.kind < KILN_CONTENT_ITEMS || s.kind > KILN_CONTENT_LOOT || !(kinds & (1u << s.kind))) return;
        if (std::memcmp(s.current, s.original, s.n) == 0) return;
        const Registry& r = reg(s.kind);
        const Entry* e = r.get(s.id);
        if (!e) return;
        std::string key;
        json value;
        if (s.field >= 0) {
            if (static_cast<size_t>(s.field) >= r.schema.fields.size()) return;
            const Field& f = r.schema.fields[static_cast<size_t>(s.field)];
            value = f.ref && f.bytes == 16 ? json(ref_text(s.current)) : raw_json(f, decode(f, s.current));
            if (value.is_null()) return;
            if (f.ref) has_refs = true;
            key = f.path;
        } else if (s.kind == KILN_CONTENT_RECIPES && (s.sub == KILN_CONTENT_INPUTS || s.sub == KILN_CONTENT_OUTPUTS)) {
            std::vector<KilnContentStack> st, orig;
            if (!read_recipe_stacks(*e, static_cast<uint32_t>(s.sub), &st) || st.empty()) return;
            // a list put back to the game's amounts lives in Kiln's memory: same contents = not a change
            if (list_at(addr, s.original, static_cast<uint32_t>(s.sub), &orig) && same_list(st, orig)) return;
            value = list_json(st, s.sub == KILN_CONTENT_INPUTS);
            key = s.sub == KILN_CONTENT_INPUTS ? "inputs" : "outputs";
        } else {
            return;
        }
        json& body = bodies[{s.kind, s.id}];
        if (body.is_null()) {
            body = json::object();
            body["@name"] = e->name;
        }
        body[key] = std::move(value);
        ++n;
    });
    json doc = json::object();
    doc["format"] = 1;
    doc["@fields"] = n;
    if (has_refs) doc["options"] = {{"refs", true}};  // references of game items: the receiver takes them as they are
    for (auto& [k, body] : bodies) {
        const char* section = k.first == KILN_CONTENT_ITEMS ? "items" : k.first == KILN_CONTENT_RECIPES ? "recipes" : "loot";
        doc[section][hex_id(k.second)] = std::move(body);
    }
#if KC_GAMEDATA
    n += gd_export(doc, kinds);  // its sections, and item config paths merged into doc["items"][id]
    doc["@fields"] = n;
#endif
    if (fields) *fields = n;
    return doc.dump(-1, ' ', false, json::error_handler_t::replace);
}

}  // namespace kc
