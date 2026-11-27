// SPDX-License-Identifier: MIT
// kiln.content registry views: Kiln names over the game's reflection (views.h).
#include "views.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <functional>

namespace kc::registry::views {

namespace {

std::string lower_text(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
bool alnum(char c) { return std::isalnum(static_cast<unsigned char>(c)) != 0; }
std::string hex8(uint32_t v) {
    char b[16];
    std::snprintf(b, sizeof b, "%08x", static_cast<unsigned>(v));
    return b;
}
bool is_name(const std::string& s) {
    if (s.empty() || !std::islower(static_cast<unsigned char>(s[0]))) return false;
    return std::all_of(s.begin(), s.end(), [](char c) { return alnum(c); });
}
// "a.b[].c" -> {"a", "b[]", "c"}
bool valid_path(const std::string& p) {
    if (p.empty()) return false;
    size_t start = 0;
    while (start <= p.size()) {
        size_t dot = p.find('.', start);
        std::string seg = p.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
        while (seg.size() >= 2 && seg.compare(seg.size() - 2, 2, "[]") == 0) seg.resize(seg.size() - 2);
        if (!is_name(seg)) return false;
        if (dot == std::string::npos) break;
        start = dot + 1;
    }
    return true;
}

const char* unread_reason(TypeKind k) {
    switch (k) {
        case TypeKind::Variant: return "a member that holds one of several kinds of value: the generic view does not read it in this release";
        case TypeKind::Optional: return "an optional member: the generic view does not read it in this release";
        case TypeKind::Configs:
            return "a configuration list: the generic view does not read it; a registry field (items.manaCost and the like) or a data "
                   "pack's config names do";
        default: return "a member of a kind the generic view cannot read";
    }
}

// Unique Kiln names for a list of game names (members of one struct, values of one enum): the first holder keeps a name, a later
// one gets the next free number ("speed", "speed2").
std::vector<std::string> unique_names(const std::vector<std::string>& game, const char* fallback) {
    std::vector<std::string> out;
    std::set<std::string> used;
    for (size_t i = 0; i < game.size(); ++i) {
        std::string n = kiln_name(game[i]);
        if (n.empty()) n = std::string(fallback) + std::to_string(i);
        std::string m = n;
        for (int k = 2; used.count(m); ++k) m = n + std::to_string(k);
        used.insert(m);
        out.push_back(m);
    }
    return out;
}

struct Walk {
    View* v;
    std::map<std::string, std::string> prefix;  // game path of every node -> its Kiln path (alias renames move whole subtrees)
    void add(ViewField f) {
        if (v->fields.size() >= kMaxFields) {
            v->truncated = true;
            return;
        }
        v->fields.push_back(std::move(f));
    }
    static FieldKind leaf_kind(TypeKind k) {
        switch (k) {
            case TypeKind::Bool: return FieldKind::Bool;
            case TypeKind::Int: return FieldKind::Int;
            case TypeKind::Float: return FieldKind::Float;
            case TypeKind::Enum: return FieldKind::Enum;
            case TypeKind::Flags: return FieldKind::Flags;
            case TypeKind::Time: return FieldKind::Time;
            case TypeKind::Guid:
            case TypeKind::Text: return FieldKind::Text;
            case TypeKind::Ref: return FieldKind::Id;
            default: return FieldKind::Unread;
        }
    }
    void values_of(const TypeDesc& t, ViewField* f) {
        f->game_values = t.values;
        f->values = unique_names(t.values, "value");
    }
    void node(const TypeDesc& t, const std::string& kiln, const std::string& game, int depth) {
        if (v->truncated) return;
        prefix[game] = kiln;
        if (depth > kMaxDepth) {
            ViewField f;
            f.path = kiln, f.game = game, f.reason = "nested too deep for the generic view (more than 8 levels)";
            add(std::move(f));
            return;
        }
        if (t.kind == TypeKind::Struct) {
            std::vector<std::string> names;
            for (const auto& [n, _] : t.fields) names.push_back(n);
            const std::vector<std::string> kn = unique_names(names, "member");
            for (size_t i = 0; i < t.fields.size(); ++i) {
                const auto& [n, ft] = t.fields[i];
                if (!ft) continue;
                node(*ft, kiln.empty() ? kn[i] : kiln + "." + kn[i], game.empty() ? n : game + "." + n, depth + 1);
            }
            return;
        }
        ViewField f;
        f.path = kiln, f.game = game;
        if (t.kind == TypeKind::List) {
            f.kind = FieldKind::List;
            const TypeDesc* el = t.element.get();
            if (!el) {
                f.element = FieldKind::Unread, f.reason = unread_reason(TypeKind::Other);
            } else if (el->kind == TypeKind::Struct) {
                f.element = FieldKind::Struct;
            } else if (el->kind == TypeKind::List) {
                f.element = FieldKind::Unread, f.reason = "a list of lists: the generic view does not read it in this release";
            } else {
                f.element = leaf_kind(el->kind);
                if (f.element == FieldKind::Unread) f.reason = unread_reason(el->kind);
                if (el->kind == TypeKind::Enum || el->kind == TypeKind::Flags) values_of(*el, &f);
                if (el->kind == TypeKind::Ref) f.ref_type = el->target;
            }
            add(std::move(f));
            if (el && el->kind == TypeKind::Struct) node(*el, kiln + "[]", game + "[]", depth + 1);
            return;
        }
        f.kind = leaf_kind(t.kind);
        if (f.kind == FieldKind::Unread) f.reason = unread_reason(t.kind);
        if (t.kind == TypeKind::Enum || t.kind == TypeKind::Flags) values_of(t, &f);
        if (t.kind == TypeKind::Ref) f.ref_type = t.target;
        add(std::move(f));
    }
};

// game path starts with prefix as a whole node ("a.b" covers "a.b", "a.b.c", "a.b[].c", not "a.bc")
bool under(const std::string& path, const std::string& prefix) {
    if (path.compare(0, prefix.size(), prefix) != 0) return false;
    return path.size() == prefix.size() || path[prefix.size()] == '.' || path[prefix.size()] == '[';
}

// Conversions whose Kiln value is the game's value: the same number or true/false (registrygen HINT_EXACT, KILN124). An enum's
// or flags' Kiln names are the registry's own, so those are covered but not the same value.
constexpr const char* kExactConvs[] = {"int", "float", "bool", "time"};

// A member several rows name: the row that maps it one to one first, then a row whose own member it is (its first path), and
// only then a row that reads it as one input of a formula (itemLevelRange.minLevel is items.levels.min; the armor and power
// formulas only read it). Ties: registry, then field, in name order.
void cover(View* v, const map::RegMap* rm) {
    if (!rm) return;
    std::vector<int> rank(v->fields.size(), 3);
    for (const auto& [reg, m] : rm->registries) {
        // where the registry's row paths start in this type: its root, or the element of the array member holding its entries;
        // an "arrays" registry (randomLoot) has one such array per source, each of its own resource type (review-int)
        std::string base;
        bool here = false;
        if (m.entries.kind == map::EntryKind::Arrays) {
            for (const map::EntrySource& src : m.entries.sources)
                if (src.resource_type == v->type && !src.array.empty()) base = src.array + "[].", here = true;
        } else if (m.entries.resource_type == v->type) {
            base = m.entries.kind == map::EntryKind::Array ? m.entries.array + "[]." : "", here = true;
        }
        if (!here) continue;
        for (const auto& [field, row] : m.rows) {
            if (field.find("[]") != std::string::npos) continue;  // element rows: their paths are from the list's element
            const bool exact = row.paths.size() == 1 && std::find(std::begin(kExactConvs), std::end(kExactConvs), row.conv) != std::end(kExactConvs);
            for (size_t k = 0; k < row.paths.size(); ++k) {
                const int r = exact ? 0 : k == 0 ? 1 : 2;
                for (size_t i = 0; i < v->fields.size(); ++i) {
                    ViewField& f = v->fields[i];
                    if (f.game != base + row.paths[k] || r >= rank[i]) continue;
                    f.covered_by = reg + "." + field, f.exact = exact, rank[i] = r;
                }
            }
        }
    }
}

FieldKind kind_of(FieldType t) {
    switch (t) {
        case FieldType::Int: return FieldKind::Int;
        case FieldType::Float: return FieldKind::Float;
        case FieldType::Bool: return FieldKind::Bool;
        case FieldType::Enum: return FieldKind::Enum;
        case FieldType::Id: return FieldKind::Id;
        case FieldType::List: return FieldKind::List;
        case FieldType::Flags: return FieldKind::Flags;
        case FieldType::Struct: return FieldKind::Struct;
    }
    return FieldKind::Unread;
}

View stable_view(const RegistrySpec& r) {
    View v;
    v.name = r.name;
    v.stable = true;
    for (const auto& [path, spec] : r.fields) {
        ViewField f;
        f.path = path;
        f.kind = kind_of(spec.type);
        f.element = spec.type == FieldType::List ? kind_of(spec.element) : f.kind;
        f.values = spec.enum_values;
        f.ref = spec.ref;
        f.read_only = spec.read_only;
        v.fields.push_back(std::move(f));
    }
    return v;
}

std::string id_name(const std::string& view) { return lower_text(view); }

}  // namespace

bool load_aliases(const json& doc, Aliases* out, std::vector<std::string>* errs) {
    *out = Aliases{};
    if (!doc.is_object() || !doc.contains("views") || doc["views"] != 1) {
        errs->push_back("regmap/views.json: \"views\" is not 1");
        return false;
    }
    bool ok = true;
    const auto rows = [&](const char* key, std::map<std::string, std::map<std::string, std::string>>* to) {
        if (!doc.contains(key)) return;
        if (!doc[key].is_object()) return errs->push_back(std::string("regmap/views.json: \"") + key + "\" is not an object"), void(ok = false);
        for (const auto& [view, fields] : doc[key].items()) {
            if (!fields.is_object()) {
                errs->push_back(std::string("regmap/views.json: ") + key + "." + view + " is not an object");
                ok = false;
                continue;
            }
            for (const auto& [field, value] : fields.items()) {
                if (!valid_path(field) || !value.is_string() || value.get<std::string>().empty()) {
                    errs->push_back(std::string("regmap/views.json: ") + key + "." + view + "." + field + " is not a Kiln field with a text value");
                    ok = false;
                    continue;
                }
                (*to)[view][field] = value.get<std::string>();
            }
        }
    };
    if (doc.contains("types")) {
        if (!doc["types"].is_object()) errs->push_back("regmap/views.json: \"types\" is not an object"), ok = false;
        else
            for (const auto& [view, type] : doc["types"].items()) {
                if (type.is_string() && !type.get<std::string>().empty()) out->types[view] = type.get<std::string>();
                else errs->push_back("regmap/views.json: types." + view + " is not a type"), ok = false;
            }
    }
    rows("fields", &out->fields);
    rows("removed", &out->removed);
    if (doc.contains("ids") && doc["ids"].is_object()) {
        const json& ids = doc["ids"];
        for (const char* key : {"pinned", "removed"})
            if (ids.contains(key) && ids[key].is_object())
                for (const auto& [k, v] : ids[key].items())
                    if (v.is_string()) (std::string(key) == "pinned" ? out->ids.pinned : out->ids.removed)[lower_text(k)] = v.get<std::string>();
    }
    return ok;
}

std::string kiln_name(const std::string& game) {
    std::string s = game;
    if (s.size() > 2 && (s[0] == 'm' || s[0] == 'M') && s[1] == '_') s = s.substr(2);
    std::string out;
    bool up = false;
    for (char c : s) {
        if (!alnum(c)) {
            up = !out.empty();
            continue;
        }
        if (out.empty()) out += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        else out += up ? static_cast<char>(std::toupper(static_cast<unsigned char>(c))) : c;
        up = false;
    }
    if (!out.empty() && std::isdigit(static_cast<unsigned char>(out[0]))) out = "n" + out;
    return out;
}

std::string view_name(const std::string& qualified, bool namespaced) {
    std::string q = qualified;
    const size_t lt = q.find('<');
    if (lt != std::string::npos) q = q.substr(0, lt);
    std::vector<std::string> parts;
    for (size_t start = 0;;) {
        const size_t c = q.find("::", start);
        parts.push_back(q.substr(start, c == std::string::npos ? std::string::npos : c - start));
        if (c == std::string::npos) break;
        start = c + 2;
    }
    std::string base = parts.back();
    if (base.size() > 8 && base.compare(base.size() - 8, 8, "Resource") == 0) base.resize(base.size() - 8);
    std::string joined;
    if (namespaced)
        for (size_t i = 0; i + 1 < parts.size(); ++i)
            if (parts[i] != "keen" && !parts[i].empty()) joined += parts[i] + "_";
    return kiln_name(joined + base);
}

const char* kind_name(FieldKind k) {
    switch (k) {
        case FieldKind::Int: return "int";
        case FieldKind::Float: return "float";
        case FieldKind::Bool: return "bool";
        case FieldKind::Enum: return "enum";
        case FieldKind::Flags: return "flags";
        case FieldKind::Time: return "time";
        case FieldKind::Id: return "id";
        case FieldKind::Text: return "text";
        case FieldKind::List: return "list";
        case FieldKind::Struct: return "struct";
        case FieldKind::Unread: return "unread";
    }
    return "unread";
}

const ViewField* View::field(const std::string& kiln_path) const {
    const std::string p = kiln_path.find('[') == std::string::npos ? kiln_path : generic_path(kiln_path);
    for (const ViewField& f : fields)
        if (f.path == p) return &f;
    return nullptr;
}

View derive(const TypeDesc& root, const std::string& name, const Aliases& aliases, const map::RegMap* rm) {
    View v;
    v.name = name;
    v.type = root.name;
    v.type_hash = root.hash;
    for (const auto& [n, t] : root.fields)
        if (t && t->kind == TypeKind::Text && (n == "debugName" || (n == "name" && v.name_member.empty()))) v.name_member = n;
    Walk w{&v, {}};
    w.node(root, "", "", 0);
    // the alias table: a pinned Kiln name follows its member (and moves the member's subtree), whatever the member is called now
    std::set<size_t> owned;  // fields whose name the alias table gives: they keep it
    const auto pins = aliases.fields.find(name);
    if (pins != aliases.fields.end()) {
        for (const auto& [kiln, game] : pins->second) {
            const auto from = w.prefix.find(game);
            if (from == w.prefix.end()) {
                ViewField f;
                f.path = kiln, f.game = game;
                f.reason = name + "." + kiln + " has no member on this game build (a Kiln update is needed)";
                owned.insert(v.fields.size());
                v.fields.push_back(std::move(f));
                continue;
            }
            const std::string old = from->second;
            for (size_t i = 0; i < v.fields.size(); ++i)
                if (!owned.count(i) && under(v.fields[i].game, game)) v.fields[i].path = kiln + v.fields[i].path.substr(old.size()), owned.insert(i);
        }
    }
    // a member a build removed: its Kiln name stays refused and is never given to another member
    const auto gone = aliases.removed.find(name);
    if (gone != aliases.removed.end())
        for (const auto& [kiln, build] : gone->second) {
            ViewField f;
            f.path = kiln, f.removed = true;
            f.reason = name + "." + kiln + " was removed by game build " + build;
            owned.insert(v.fields.size());
            v.fields.push_back(std::move(f));
        }
    // one name per field: an owned name holds; a derived field that has one already taken gets the next number
    std::set<std::string> used;
    for (size_t i : owned) used.insert(v.fields[i].path);
    for (size_t i = 0; i < v.fields.size(); ++i) {
        if (owned.count(i)) continue;
        ViewField& f = v.fields[i];
        std::string m = f.path;
        for (int k = 2; used.count(m); ++k) m = f.path + std::to_string(k);
        f.path = m;
        used.insert(m);
    }
    cover(&v, rm);
    return v;
}

const View* Catalog::find(const std::string& name) const {
    const auto it = by_name.find(name);
    return it == by_name.end() ? nullptr : &views[it->second];
}

Catalog catalog(const Schema& schema, const std::vector<TypeDesc>& types, const Aliases& aliases, const map::RegMap* rm) {
    Catalog c;
    std::set<std::string> taken;
    for (const auto& [name, r] : schema.registries) {
        c.by_name[name] = c.views.size();
        c.views.push_back(stable_view(r));
        taken.insert(name);
    }
    std::vector<const TypeDesc*> order;
    for (const TypeDesc& t : types) order.push_back(&t);
    std::sort(order.begin(), order.end(), [](const TypeDesc* a, const TypeDesc* b) { return a->name < b->name; });
    std::map<std::string, std::string> names;  // type -> view
    for (const auto& [view, type] : aliases.types)
        if (!taken.count(view) && std::any_of(order.begin(), order.end(), [&](const TypeDesc* t) { return t->name == type; }))
            names[type] = view, taken.insert(view);
    for (const TypeDesc* t : order) {
        if (names.count(t->name)) continue;
        std::string n = view_name(t->name);
        if (n.empty()) n = "type";
        if (taken.count(n)) {
            const std::string ns = view_name(t->name, true);
            n = !ns.empty() && ns != n && !taken.count(ns) ? ns : n + "_" + hex8(t->hash);
        }
        for (int k = 2; taken.count(n); ++k) n = view_name(t->name) + "_" + hex8(t->hash) + "_" + std::to_string(k);
        names[t->name] = n;
        taken.insert(n);
    }
    std::vector<std::pair<std::string, const TypeDesc*>> advanced;
    for (const TypeDesc* t : order) advanced.emplace_back(names[t->name], t);
    std::sort(advanced.begin(), advanced.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
    for (const auto& [n, t] : advanced) {
        c.by_name[n] = c.views.size();
        c.by_type[t->name] = n;
        c.views.push_back(derive(*t, n, aliases, rm));
    }
    for (View& v : c.views) {
        if (v.stable) continue;
        for (ViewField& f : v.fields)
            if (!f.ref_type.empty()) {
                const auto it = c.by_type.find(f.ref_type);
                f.ref = it == c.by_type.end() ? "" : it->second;
            }
    }
    return c;
}

namespace {

std::optional<json> scalar(const View& v, const ViewField& f, FieldKind k, const json& g, const RefFn& refs, std::string* why) {
    const std::string place = v.name + "." + f.path;
    switch (k) {
        case FieldKind::Int:
            if (g.is_number_integer() || g.is_number_unsigned()) return g;
            if (g.is_boolean()) return json(g.get<bool>() ? 1 : 0);
            break;
        case FieldKind::Float:
        case FieldKind::Time:
            if (g.is_number()) return json(g.get<double>());
            break;
        case FieldKind::Bool:
            if (g.is_boolean()) return g;
            if (g.is_number_integer() || g.is_number_unsigned()) return json(g.get<int64_t>() != 0);
            break;
        case FieldKind::Enum:
            if (g.is_string()) {
                for (size_t i = 0; i < f.game_values.size() && i < f.values.size(); ++i)
                    if (f.game_values[i] == g.get<std::string>()) return json(f.values[i]);
                return *why = place + ": the game holds a value Kiln has no name for on this build", std::nullopt;
            }
            if (g.is_number()) return *why = place + ": the game holds a value Kiln has no name for on this build", std::nullopt;
            break;
        case FieldKind::Flags: {
            json a = json::array();
            if (!g.is_array()) break;
            for (const json& x : g) {
                bool found = false;
                if (x.is_string())
                    for (size_t i = 0; i < f.game_values.size() && i < f.values.size(); ++i)
                        if (f.game_values[i] == x.get<std::string>()) a.push_back(f.values[i]), found = true;
                if (!found) return *why = place + ": the game holds a flag Kiln has no name for on this build", std::nullopt;
            }
            return a;
        }
        case FieldKind::Id:
            if (g.is_string()) {
                const std::string s = g.get<std::string>();
                if (s == "none" || s.empty()) return json(nullptr);
                if (refs && !f.ref.empty())
                    if (auto id = refs(f.ref, lower_text(s))) return json(*id);
                return json(lower_text(s));
            }
            if (g.is_null()) return json(nullptr);
            break;
        case FieldKind::Text:
            if (g.is_string()) return g;
            if (g.is_null()) return json(nullptr);
            break;
        default:
            return *why = place + ": " + (f.reason.empty() ? std::string("the generic view does not read it") : f.reason), std::nullopt;
    }
    return *why = place + ": the game's value is not a " + kind_name(k) + " on this build", std::nullopt;
}

// A struct element (game member names) as an object with flat dotted Kiln keys, through the view's rows below prefix.
std::optional<json> element(const View& v, const std::string& kiln_prefix, const std::string& game_prefix, const json& g,
                            const RefFn& refs, std::string* why) {
    json o = json::object();
    for (const ViewField& f : v.fields) {
        if (f.path.compare(0, kiln_prefix.size(), kiln_prefix) != 0 || f.game.compare(0, game_prefix.size(), game_prefix) != 0) continue;
        const std::string krest = f.path.substr(kiln_prefix.size()), grest = f.game.substr(game_prefix.size());
        if (krest.find("[]") != std::string::npos && !(f.kind == FieldKind::List && krest.find("[]") == std::string::npos)) continue;
        // walk the element's members
        const json* cur = &g;
        size_t start = 0;
        bool ok = true;
        while (ok) {
            const size_t dot = grest.find('.', start);
            const std::string seg = grest.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
            if (!cur->is_object() || !cur->contains(seg)) ok = false;
            else cur = &(*cur)[seg];
            if (dot == std::string::npos) break;
            start = dot + 1;
        }
        if (!ok || f.kind == FieldKind::Unread) continue;
        std::optional<json> val;
        if (f.kind == FieldKind::List) {
            if (!cur->is_array()) continue;
            json a = json::array();
            for (const json& x : *cur) {
                auto e = f.element == FieldKind::Struct ? element(v, f.path + "[].", f.game + "[].", x, refs, why) : scalar(v, f, f.element, x, refs, why);
                if (!e) return std::nullopt;
                a.push_back(*e);
            }
            val = std::move(a);
        } else {
            val = scalar(v, f, f.kind, *cur, refs, why);
        }
        if (!val) return std::nullopt;
        o[krest] = *val;
    }
    return o;
}

}  // namespace

std::optional<json> kiln_value(const View& v, const ViewField& f, const json& game, const RefFn& refs, std::string* why) {
    if (f.removed || f.kind == FieldKind::Unread) return *why = v.name + "." + f.path + ": " + f.reason, std::nullopt;
    if (f.kind != FieldKind::List) return scalar(v, f, f.kind, game, refs, why);
    if (f.element == FieldKind::Unread) return *why = v.name + "." + f.path + ": " + f.reason, std::nullopt;
    if (!game.is_array()) return *why = v.name + "." + f.path + ": the game's value is not a list on this build", std::nullopt;
    json a = json::array();
    for (const json& x : game) {
        auto e = f.element == FieldKind::Struct ? element(v, f.path + "[].", f.game + "[].", x, refs, why) : scalar(v, f, f.element, x, refs, why);
        if (!e) return std::nullopt;
        a.push_back(*e);
    }
    return a;
}

std::optional<std::string> game_path(const View& v, const std::string& kiln_path, const ViewField** field, std::string* why) {
    // the indices, in order, and the path with "[]" in their places
    std::vector<std::string> idx;
    std::string generic;
    for (size_t i = 0; i < kiln_path.size(); ++i) {
        if (kiln_path[i] != '[') {
            generic += kiln_path[i];
            continue;
        }
        const size_t close = kiln_path.find(']', i);
        const std::string n = close == std::string::npos ? "" : kiln_path.substr(i + 1, close - i - 1);
        if (n.empty() || n.size() > 9 || !std::all_of(n.begin(), n.end(), [](char c) { return std::isdigit(static_cast<unsigned char>(c)) != 0; }))
            return *why = v.name + "." + kiln_path + ": an index is a whole number in brackets (" + v.name + ".list[2].field)", std::nullopt;
        idx.push_back(n);
        generic += "[]";
        i = close;
    }
    const ViewField* f = nullptr;
    for (const ViewField& x : v.fields)
        if (x.path == generic) f = &x;
    bool element_of_list = false;
    if (!f && generic.size() > 2 && generic.compare(generic.size() - 2, 2, "[]") == 0) {
        for (const ViewField& x : v.fields)
            if (x.path == generic.substr(0, generic.size() - 2) && x.kind == FieldKind::List && x.element != FieldKind::Struct) f = &x;
        element_of_list = f != nullptr;
    }
    if (!f) return *why = v.name + " has no field " + generic + " on this game build", std::nullopt;
    *field = f;
    if (f->removed || (f->kind == FieldKind::Unread && f->game.empty())) return *why = v.name + "." + f->path + ": " + f->reason, std::nullopt;
    std::string out;
    size_t k = 0;
    const std::string g = element_of_list ? f->game + "[]" : f->game;
    for (size_t i = 0; i < g.size(); ++i) {
        if (g.compare(i, 2, "[]") == 0 && k < idx.size()) {
            out += "[" + idx[k++] + "]";
            ++i;
        } else {
            out += g[i];
        }
    }
    return out;
}

std::vector<std::string> ids(const View& v, const Aliases& aliases, const std::vector<map::GameEntry>& entries,
                             std::vector<std::string>* notes) {
    map::EntrySource src;
    src.kind = map::EntryKind::Resource;
    src.resource_type = v.type;
    src.name = v.name_member;
    src.unnamed = id_name(v.name) + "_%.8s";
    return map::assign_ids(v.name, src, aliases.ids, entries, nullptr, notes);
}

bool selects(const std::string& selector, const std::string& id, const std::string& name) {
    if (selector.empty()) return false;
    if (selector == "*") return true;
    if (selector.find_first_of("*?") != std::string::npos) return glob_ci(selector, id);
    return lower_text(selector) == id || (!name.empty() && lower_text(selector) == lower_text(name));
}

json views_json(const Catalog& c, const std::map<std::string, size_t>& entry_counts) {
    json a = json::array();
    for (const View& v : c.views) {
        json o = {{"name", v.name}, {"tier", v.stable ? "stable" : "advanced"}, {"fields", v.fields.size()}};
        if (v.stable) o["registry"] = v.name;
        if (v.truncated) o["truncated"] = true;
        const auto it = entry_counts.find(v.name);
        if (it != entry_counts.end()) o["entries"] = it->second;
        a.push_back(std::move(o));
    }
    return {{"format", 1}, {"views", std::move(a)}};
}

json fields_json(const View& v) {
    json a = json::array();
    for (const ViewField& f : v.fields) {
        json o = {{"path", f.path}, {"type", kind_name(f.kind)}, {"readOnly", f.read_only}};
        if (f.kind == FieldKind::List) o["element"] = kind_name(f.element);
        if (!f.values.empty()) o["values"] = f.values;
        if (!f.ref.empty()) o["ref"] = f.ref;
        o["state"] = f.removed ? "removed" : (f.kind == FieldKind::Unread || (f.kind == FieldKind::List && f.element == FieldKind::Unread)) ? "unread" : "readable";
        if (!f.reason.empty()) o["reason"] = f.reason;
        if (!f.covered_by.empty()) o["coveredBy"] = f.covered_by, o["exact"] = f.exact;
        a.push_back(std::move(o));
    }
    json out = {{"view", v.name}, {"tier", v.stable ? "stable" : "advanced"}, {"fields", std::move(a)}};
    if (v.truncated) out["truncated"] = true;
    return out;
}

}  // namespace kc::registry::views
