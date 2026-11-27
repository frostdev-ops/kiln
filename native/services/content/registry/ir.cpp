// SPDX-License-Identifier: MIT
// kiln.content registry stage: the edit IR's reader and checks (ir.h).
#include "ir.h"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <climits>
#include <cmath>
#include <set>

namespace kc::registry {

namespace {

constexpr int kMaxDepth = 32;              // nesting levels of an IR document (a real one has fewer than 8)
constexpr size_t kMaxIrBytes = 16u << 20;  // KILN_REGISTRY_MAX_IR_BYTES
// The whole numbers an int64 holds, as doubles: [-2^63, 2^63).
constexpr double kInt64Lo = -9223372036854775808.0, kInt64Hi = 9223372036854775808.0;

// An untrusted value for a message: a scalar as written (a text cut to 64 bytes on a character boundary), never the whole of
// a list or an object.
std::string brief(const json& j) {
    if (j.is_string()) {
        std::string s = j.get<std::string>();
        if (s.size() > 64) {
            size_t cut = 64;
            while (cut > 0 && (static_cast<unsigned char>(s[cut]) & 0xC0) == 0x80) --cut;
            s = s.substr(0, cut) + "...";
        }
        return json(s).dump(-1, ' ', false, json::error_handler_t::replace);
    }
    if (j.is_number() || j.is_boolean() || j.is_null()) return j.dump();
    return j.is_array() ? "a list" : j.is_object() ? "an object" : "a value";
}

bool too_deep(const json& doc) {  // the same limit for a document handed over as JSON; iterative
    std::vector<std::pair<const json*, int>> stack{{&doc, 0}};
    while (!stack.empty()) {
        const auto [j, d] = stack.back();
        stack.pop_back();
        if (!j->is_structured()) continue;
        if (d >= kMaxDepth) return true;
        for (const json& c : *j) stack.emplace_back(&c, d + 1);
    }
    return false;
}

const char* const kReservedMods[] = {"kiln", "kiln.api", "game", "enshrouded", "native", "permissions"};

}  // namespace

namespace {
// A value change of field (or leaf) f: relative or not. false + *err: its ops refuse it.
bool value_ops(const FieldSpec& f, bool relative, std::string* err) {
    if (f.ops == FieldOps::None || f.read_only) return *err = f.name + " is read-only", false;
    if (f.ops == FieldOps::Set && relative) return *err = f.name + " takes a value only (its ops are set), not a relative change", false;
    return true;
}
}  // namespace

bool ops_allow(const FieldSpec& f, const Edit& e, std::string* err) {
    const bool relative = e.list == ListOp::None && !e.op.set;
    if (e.element.on) {  // f is the list; its element's (or leaf's) own ops, then the list's
        if (f.ops == FieldOps::None || f.read_only) return *err = f.name + " is read-only", false;
        if (!e.element.leaf.empty()) {
            const FieldSpec* leaf = f.element_field(e.element.leaf);
            if (!leaf) return *err = f.name + "'s elements have no field \"" + e.element.leaf + "\"", false;
            FieldSpec named = *leaf;
            named.name = f.name + "[]." + leaf->name;
            return value_ops(named, relative, err);
        }
        return true;
    }
    switch (f.ops) {
        case FieldOps::Any: return true;
        case FieldOps::None: return *err = f.name + " is read-only", false;
        case FieldOps::Set:
        case FieldOps::Number:
            if (e.list != ListOp::None) return *err = f.name + " is not a list", false;
            return value_ops(f, relative, err);
        case FieldOps::List: return true;
        case FieldOps::Indexed:
            return *err = f.name + " is a list of fixed length: edit one element at a time (" + f.name + "[n])", false;
    }
    return true;
}

bool mod_id_ok(const std::string& id, std::string* err) {
    const bool shape = !id.empty() && id.size() <= 64 && (std::islower(static_cast<unsigned char>(id[0])) || std::isdigit(static_cast<unsigned char>(id[0]))) &&
                       std::all_of(id.begin(), id.end(), [](char c) {
                           return std::islower(static_cast<unsigned char>(c)) || std::isdigit(static_cast<unsigned char>(c)) || c == '.' || c == '_' || c == '-';
                       });
    if (!shape) return *err = "\"" + json(id).dump(-1, ' ', false, json::error_handler_t::replace).substr(1) + " is no Kiln mod id (a-z, 0-9, '.', '_', '-'; at most 64)", false;
    for (const char* r : kReservedMods)
        if (id == r) return *err = "\"" + id + "\" is reserved (game: and kiln ids are Kiln's own), not a mod's id", false;
    return true;
}

json Value::to_json() const {
    switch (kind) {
        case Int: return i;
        case Num: return d;
        case Bool: return b;
        case Text: return t;
        case List: {
            json a = json::array();
            for (const Value& v : list) a.push_back(v.to_json());
            return a;
        }
        case Struct: {
            json o = json::object();
            for (size_t n = 0; n < keys.size() && n < list.size(); ++n) o[keys[n]] = list[n].to_json();
            return o;
        }
        default: return nullptr;
    }
}

std::string Value::text_of() const {
    switch (kind) {
        case Int: return std::to_string(i);
        case Bool: return b ? "true" : "false";
        case Num: {  // the shortest text that reads back as d, written as JSON writes a float ("2.0", "1.5", "1e+20")
            if (!std::isfinite(d)) return "null";
            char buf[64];
            const auto r = std::to_chars(buf, buf + sizeof buf, d);
            std::string s(buf, r.ptr);
            if (s.find_first_of(".e") == std::string::npos) s += ".0";
            return s;
        }
        default: return to_json().dump(-1, ' ', false, json::error_handler_t::replace);
    }
}

bool operator==(const Value& a, const Value& b) {
    if (a.numeric() && b.numeric()) return a.as_double() == b.as_double();
    if (a.kind != b.kind) return false;
    switch (a.kind) {
        case Value::Bool: return a.b == b.b;
        case Value::Text: return a.t == b.t;
        case Value::List: return a.list == b.list;
        case Value::Struct: return a.keys == b.keys && a.list == b.list;
        default: return true;
    }
}

std::string generic_path(const std::string& path) {
    std::string g;
    for (size_t i = 0; i < path.size(); ++i) {
        g += path[i];
        if (path[i] != '[') continue;
        const size_t e = path.find(']', i);
        if (e == std::string::npos) return path;
        i = e - 1;  // the loop's ++i lands on ']', which is copied
    }
    return g;
}

void link_elements(Schema* schema) {
    for (auto& [name, reg] : schema->registries)
        for (const auto& [path, leaf] : reg.fields) {
            const size_t at = path.find("[].");
            if (at == std::string::npos) continue;
            const auto list = reg.fields.find(path.substr(0, at));
            if (list == reg.fields.end() || list->second.type != FieldType::List) continue;
            FieldSpec& l = list->second;
            l.element = FieldType::Struct;
            FieldSpec x = leaf;
            x.name = path.substr(at + 3);
            if (!l.element_field(x.name)) l.element_fields.push_back(std::move(x));
        }
}

const char* stage_name(Stage s) { return s == Stage::Add ? "add" : s == Stage::Alter ? "alter" : "final"; }
bool stage_of(const std::string& s, Stage* out) {
    if (s == "add") return *out = Stage::Add, true;
    if (s == "alter") return *out = Stage::Alter, true;
    if (s == "final") return *out = Stage::Final, true;
    return false;
}

json Op::to_json() const {
    if (set) return value.to_json();
    json o = json::object();
    if (mul) o["mul"] = *mul;
    if (add) o["add"] = *add;
    if (min) o["min"] = *min;
    if (max) o["max"] = *max;
    if (raise_only) o["raiseOnly"] = true;
    if (lower_only) o["lowerOnly"] = true;
    return o;
}

bool glob_ci(const std::string& p, const std::string& t) {
    size_t i = 0, j = 0, star = std::string::npos, back = 0;
    auto eq = [](char a, char b) { return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b)); };
    while (j < t.size()) {
        if (i < p.size() && (p[i] == '?' || (p[i] != '*' && eq(p[i], t[j])))) {
            ++i, ++j;
        } else if (i < p.size() && p[i] == '*') {
            star = i++, back = j;
        } else if (star != std::string::npos) {
            i = star + 1, j = ++back;
        } else {
            return false;
        }
    }
    while (i < p.size() && p[i] == '*') ++i;
    return i == p.size();
}

namespace {

bool in_range(double v, const FieldSpec& f, std::string* err) {
    if ((f.min && v < *f.min) || (f.max && v > *f.max)) {
        json lo = f.min ? json(*f.min) : json("-"), hi = f.max ? json(*f.max) : json("-");
        return *err = f.name + ": " + json(v).dump() + " is outside " + lo.dump() + ".." + hi.dump(), false;
    }
    return true;
}

bool scalar_of(const json& j, FieldType type, const FieldSpec& f, Value* out, std::string* err) {
    switch (type) {
        case FieldType::Int: {
            int64_t v = 0;
            if (j.is_number_unsigned()) {
                const uint64_t u = j.get<uint64_t>();
                if (u > static_cast<uint64_t>(INT64_MAX)) return *err = f.name + ": " + j.dump() + " is too large for a whole-number field", false;
                v = static_cast<int64_t>(u);
            } else if (j.is_number_integer()) {
                v = j.get<int64_t>();
            } else if (j.is_number_float()) {
                const double d = j.get<double>();
                if (!std::isfinite(d) || std::nearbyint(d) != d) return *err = f.name + ": a whole number is expected, not " + brief(j), false;
                if (d < kInt64Lo || d >= kInt64Hi) return *err = f.name + ": " + brief(j) + " is too large for a whole-number field", false;
                v = static_cast<int64_t>(d);
            } else {
                return *err = f.name + ": a whole number is expected, not " + brief(j), false;
            }
            if (!in_range(static_cast<double>(v), f, err)) return false;
            *out = Value::integer(v);
            return true;
        }
        case FieldType::Float:
            if (!j.is_number()) return *err = f.name + ": a number is expected, not " + brief(j), false;
            if (!std::isfinite(j.get<double>())) return *err = f.name + ": a finite number is expected", false;
            if (!in_range(j.get<double>(), f, err)) return false;
            *out = Value::number(j.get<double>());
            return true;
        case FieldType::Bool:
            if (!j.is_boolean()) return *err = f.name + ": true or false is expected, not " + brief(j), false;
            *out = Value::boolean(j.get<bool>());
            return true;
        case FieldType::Enum: {
            if (!j.is_string()) return *err = f.name + ": one of the names is expected, not " + brief(j), false;
            const std::string s = j.get<std::string>();
            const auto it = std::find_if(f.enum_values.begin(), f.enum_values.end(), [&](const std::string& e) { return glob_ci(e, s) && e.size() == s.size(); });
            if (it == f.enum_values.end()) {
                std::string names;
                for (const std::string& e : f.enum_values) names += (names.empty() ? "" : ", ") + e;
                return *err = f.name + ": " + brief(j) + " is not one of " + names, false;
            }
            *out = Value::text(*it);
            return true;
        }
        case FieldType::Id:
            if (j.is_null()) {  // clearing a reference: only where the schema says it may be absent
                if (f.optional) return *out = Value{}, true;
                return *err = f.name + ": a reference that is not optional cannot be cleared (null)", false;
            }
            // An id named as a value (a perk, an item of a recipe) is only checked for its shape here. Composition refuses a
            // mod id that names no entry (compose.cpp check_references), and the back-end refuses a game id this build or
            // world lacks when it writes (registry_live.cpp id_num), per edit.
            if (!j.is_string() || j.get<std::string>().find(':') == std::string::npos)
                return *err = f.name + ": an id (\"game:<name>\" or \"<mod>:<name>\") is expected, not " + brief(j), false;
            *out = Value::text(j.get<std::string>());
            return true;
        default:
            return *err = f.name + ": a list holds plain values", false;
    }
}

// A flags value: names of the schema (any case), each once, held in the schema's order.
bool flags_of(const json& j, const FieldSpec& f, Value* out, std::string* err) {
    if (!j.is_array()) return *err = f.name + ": a list of names is expected, not " + brief(j), false;
    std::vector<bool> on(f.enum_values.size(), false);
    for (const json& x : j) {
        Value v;
        if (!scalar_of(x, FieldType::Enum, f, &v, err)) return false;
        const size_t k = static_cast<size_t>(std::find(f.enum_values.begin(), f.enum_values.end(), v.t) - f.enum_values.begin());
        if (on[k]) return *err = f.name + ": \"" + v.t + "\" is named twice", false;
        on[k] = true;
    }
    std::vector<Value> names;
    for (size_t k = 0; k < on.size(); ++k)
        if (on[k]) names.push_back(Value::text(f.enum_values[k]));
    *out = Value::of_list(std::move(names));
    return true;
}

// One element of a list with fields of its own ({"item": "game:x", "count": 2}): its leaves, each typed by its spec.
bool struct_of(const json& j, const FieldSpec& list, Value* out, std::string* err) {
    if (!j.is_object()) return *err = list.name + ": an element is {\"<field>\": <value>, ...}, not " + brief(j), false;
    Value v;
    v.kind = Value::Struct;
    for (const auto& [k, x] : j.items()) {  // nlohmann's object is ordered: keys come sorted
        const FieldSpec* leaf = list.element_field(k);
        if (!leaf) return *err = list.name + ": an element has no field \"" + k + "\"", false;
        // a read-only leaf (an element's identity: a drop entry's item) is given when the element is made, never edited after
        Value y;
        if (!(leaf->type == FieldType::Flags ? flags_of(x, *leaf, &y, err) : scalar_of(x, leaf->type, *leaf, &y, err))) return false;
        v.keys.push_back(k);
        v.list.push_back(std::move(y));
    }
    *out = std::move(v);
    return true;
}

bool element_value(const json& j, const FieldSpec& f, Value* out, std::string* err) {
    return f.element == FieldType::Struct ? struct_of(j, f, out, err) : scalar_of(j, f.element, f, out, err);
}

// "inputs[2].count", "inputs[*].count", "inputs[3]": the list, the element, the leaf. One level of list.
bool element_of(const std::string& path, ElementRef* r, std::string* err) {
    *r = ElementRef{};
    const size_t open = path.find('['), close = path.find(']');
    if (open == 0 || open == std::string::npos || close == std::string::npos || close < open)
        return *err = "\"" + path + "\": an element is <list>[n] or <list>[*]", false;
    const std::string idx = path.substr(open + 1, close - open - 1);
    r->list = path.substr(0, open);
    if (idx == "*") {
        r->all = true;
    } else {
        if (idx.empty() || idx.size() > 9 || !std::all_of(idx.begin(), idx.end(), [](char c) { return c >= '0' && c <= '9'; }))
            return *err = "\"" + path + "\": an index is a whole number or *", false;
        r->index = static_cast<uint32_t>(std::stoul(idx));
    }
    const std::string rest = path.substr(close + 1);
    if (!rest.empty()) {
        if (rest[0] != '.' || rest.size() == 1) return *err = "\"" + path + "\": after an element comes .<field>", false;
        r->leaf = rest.substr(1);
        if (r->leaf.find_first_of("[]") != std::string::npos) return *err = "\"" + path + "\": one level of list (no list in an element)", false;
    }
    r->on = true;
    return true;
}

constexpr size_t kMaxRawPaths = 32;

bool scalar(const json& j) { return j.is_number() || j.is_boolean() || j.is_string(); }

// A raw path's value: its shape only (the member's type and range are checked per entry at composition).
bool raw_op_shape(const json& v, const std::string& path, std::string* err) {
    if (scalar(v)) return true;
    const std::string what = "raw path \"" + path + "\"";
    if (!v.is_object()) return *err = what + ": a plain value (a number, true or false, a name) or an op is expected, not " + brief(v), false;
    if (v.contains("set")) {
        if (v.size() != 1) return *err = what + ": \"set\" stands alone", false;
        if (!scalar(v["set"])) return *err = what + ": \"set\" takes a plain value (a number, true or false, a name), not " + brief(v["set"]), false;
        return true;
    }
    FieldSpec any;  // relative ops: numbers whatever the member's type; the member says at composition whether it is a number
    any.name = what, any.type = FieldType::Float;
    Op op;
    return op_of(v, any, &op, err);
}

bool selector_of(const json& j, Selector* s, std::string* err) {
    *s = Selector{};
    if (j.is_string()) return s->id = j.get<std::string>(), !s->id.empty() || (*err = "an empty selector", false);
    if (!j.is_object() || !j.contains("id") || !j["id"].is_string() || j["id"].get<std::string>().empty())
        return *err = "\"select\" is {\"id\": \"<id or glob>\", \"where\": {...}, \"has\": [...], \"hasNot\": [...]}", false;
    for (const auto& [k, v] : j.items())
        if (k != "id" && k != "where" && k != "has" && k != "hasNot") return *err = "\"select\": unknown member \"" + k + "\"", false;
    s->id = j["id"].get<std::string>();
    if (j.contains("where")) {
        if (!j["where"].is_object()) return *err = "\"where\" is {\"<field>\": <condition>}", false;
        for (const auto& [k, c] : j["where"].items())
            if (!scalar(c)) return *err = "where \"" + k + "\": a condition is a plain value (\">=1\", 5, true, a name), not " + brief(c), false;
        s->where = j["where"];
    }
    for (const char* k : {"has", "hasNot"}) {
        if (!j.contains(k)) continue;
        if (!j[k].is_array()) return *err = std::string("\"") + k + "\" is a list of trait names", false;
        for (const json& t : j[k]) {
            if (!t.is_string()) return *err = std::string("\"") + k + "\" is a list of trait names", false;
            (k[3] == 'N' ? s->has_not : s->has).push_back(t.get<std::string>());
        }
    }
    return true;
}

bool edit_of(const json& e, const Batch& b, const Schema& schema, Edit* out, std::string* err) {
    if (!e.is_object()) return *err = "an edit is an object", false;
    static const char* const known[] = {"n", "registry", "select", "field", "op", "list", "entry", "id", "like", "fields", "exclusive", "yield", "raw"};
    for (const auto& [k, v] : e.items())
        if (std::none_of(std::begin(known), std::end(known), [&](const char* x) { return k == x; })) return *err = "unknown member \"" + k + "\"", false;
    if (!e.contains("registry") || !e["registry"].is_string()) return *err = "\"registry\" names the registry", false;
    out->registry = e["registry"].get<std::string>();
    const RegistrySpec* reg = schema.registry(out->registry);
    if (!reg) return *err = "no registry \"" + out->registry + "\"", false;
    for (const char* k : {"exclusive", "yield"})
        if (e.contains(k) && !e[k].is_boolean()) return *err = std::string("\"") + k + "\" is true or false", false;
    out->exclusive = e.value("exclusive", false);
    out->yield = e.value("yield", false);
    if (e.contains("raw")) {  // the Advanced tier (D5): raw game member paths
        for (const char* k : {"field", "op", "list", "entry", "id", "like", "fields"})
            if (e.contains(k)) return *err = std::string("\"raw\" (game member paths) and \"") + k + "\" in one edit: an edit has one or the other", false;
        if (!reg->raw)
            return *err = out->registry + ": raw game paths (the Advanced tier) are not allowed in this registry: its schema does not allow them; use its Kiln fields", false;
        if (b.stage == Stage::Add) return *err = "stage add adds entries; field edits, raw ones too, belong in alter or final", false;
        if (!e.contains("select")) return *err = "\"select\" names the entries a raw edit applies to", false;
        if (!selector_of(e["select"], &out->select, err)) return false;
        const json& r = e["raw"];
        if (!r.is_object()) return *err = "\"raw\" is {\"<game member path>\": <value or op>}", false;
        if (r.empty()) return *err = "\"raw\" names at least one game member path", false;
        if (r.size() > kMaxRawPaths) return *err = "\"raw\" names at most " + std::to_string(kMaxRawPaths) + " game member paths in one edit", false;
        for (const auto& [p, v] : r.items()) {
            RawWrite w;
            w.path = p;
            if (!raw_key(p, &w.key, err)) return false;
            for (const RawWrite& o : out->raw)
                if (o.key == w.key) return *err = "raw paths \"" + o.path + "\" and \"" + p + "\" name one place", false;
            if (!raw_op_shape(v, p, err)) return false;
            w.op = v;
            out->raw.push_back(std::move(w));
        }
        return true;
    }
    if (e.contains("entry")) {
        const std::string what = e["entry"].is_string() ? e["entry"].get<std::string>() : "";
        if (what == "add") {
            out->entry = EntryOp::Add;
            if (!reg->add) return *err = out->registry + ": entries are not added to this registry", false;
            if (b.stage != Stage::Add) return *err = "an entry is added in stage add", false;
            if (!e.contains("id") || !e["id"].is_string()) return *err = "an added entry has an \"id\" (\"" + b.mod + ":<name>\")", false;
            out->add_id = e["id"].get<std::string>();
            const std::string prefix = b.mod + ":";
            if (out->add_id.size() <= prefix.size() || out->add_id.compare(0, prefix.size(), prefix) != 0 ||
                out->add_id.find_first_of("*?", prefix.size()) != std::string::npos)
                return *err = "an added entry's id is \"" + prefix + "<name>\", not " + brief(e["id"]), false;
            if (std::any_of(out->add_id.begin(), out->add_id.end(), [](char c) { return c >= 'A' && c <= 'Z'; }))
                return *err = "an added entry's id is lower case (Kiln ids are), not " + brief(e["id"]), false;
            const auto name = out->add_id.substr(prefix.size());
            if (out->add_id.size() > 256 || !std::all_of(name.begin(), name.end(), [](unsigned char c) {
                    return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                })) return *err = "an added entry's id is <mod>:<name>, at most 256 characters; name uses a-z, 0-9, '.', '_' or '-'", false;
            if (e.contains("like")) {
                if (!e["like"].is_string()) return *err = "\"like\" is an id", false;
                out->like = e["like"].get<std::string>();
            }
            if (e.contains("fields")) {
                if (!e["fields"].is_object()) return *err = "\"fields\" is {\"<field>\": <value>}", false;
                for (const auto& [k, v] : e["fields"].items()) {
                    const FieldSpec* f = reg->field(k);
                    if (!f) return *err = out->registry + " has no field \"" + k + "\"", false;
                    if (f->read_only) return *err = out->registry + "." + k + " is read-only", false;
                    Value x;
                    if (!value_of(v, *f, false, &x, err)) return false;
                    out->add_fields[k] = x;
                }
            }
            return true;
        }
        if (what != "remove") return *err = "\"entry\" is \"add\" or \"remove\"", false;
        out->entry = EntryOp::Remove;
        if (!reg->remove) return *err = out->registry + ": entries are not removed from this registry", false;
        if (b.stage == Stage::Add) return *err = "an entry is removed in stage alter or final", false;
        return e.contains("select") ? selector_of(e["select"], &out->select, err) : (*err = "\"select\" names what is removed", false);
    }
    if (b.stage == Stage::Add) return *err = "stage add adds entries (\"entry\": \"add\"); field edits belong in alter or final", false;
    if (!e.contains("select") || !selector_of(e["select"], &out->select, err)) return err->empty() ? (*err = "\"select\" names the entries", false) : false;
    if (!e.contains("field") || !e["field"].is_string()) return *err = "\"field\" names the field", false;
    out->field = e["field"].get<std::string>();
    if (e.contains("op") == e.contains("list")) return *err = "an edit has \"op\" or \"list\"", false;
    if (out->field.find('[') != std::string::npos) {  // an element of a list: "inputs[2].count", "inputs[*].count", "perks[1]"
        ElementRef& r = out->element;
        if (!element_of(out->field, &r, err)) return false;
        const FieldSpec* lf = reg->field(r.list);
        if (!lf || lf->type != FieldType::List) return *err = out->registry + " has no list \"" + r.list + "\"", false;
        if (lf->read_only) return *err = out->registry + "." + r.list + " is read-only", false;
        if (lf->ops == FieldOps::Indexed && lf->fixed_length && !r.all && r.index >= lf->fixed_length)
            return *err = out->registry + "." + r.list + " has a fixed length of " + std::to_string(lf->fixed_length) + " elements: no element " +
                          std::to_string(r.index), false;
        out->field = r.list + "[" + (r.all ? std::string("*") : std::to_string(r.index)) + "]" + (r.leaf.empty() ? "" : "." + r.leaf);
        if (e.contains("list")) return *err = "a list op names the list itself (\"" + r.list + "\"), not an element", false;
        if (!r.leaf.empty()) {
            if (lf->element != FieldType::Struct) return *err = out->registry + "." + r.list + "'s elements have no fields", false;
            const FieldSpec* leaf = lf->element_field(r.leaf);
            if (!leaf) return *err = out->registry + "." + r.list + "'s elements have no field \"" + r.leaf + "\"", false;
            FieldSpec named = *leaf;  // messages name the place as the edit does ("outputs[0].item"), not the bare leaf
            named.name = out->field;
            if (!op_of(e["op"], named, &out->op, err)) return false;
            return ops_allow(*lf, *out, err);
        }
        if (lf->element == FieldType::Struct) {  // a whole element with fields: {"set": {...}} (a plain object would read as an op)
            const json& o = e["op"];
            if (!o.is_object() || o.size() != 1 || !o.contains("set")) return *err = out->field + ": a whole element is set with {\"set\": {<its fields>}}", false;
            out->op.set = true;
            if (!struct_of(o["set"], *lf, &out->op.value, err)) return false;
            return ops_allow(*lf, *out, err);
        }
        FieldSpec el = *lf;  // a plain element: a value or a relative change, with the list's range for its elements
        el.type = lf->element;
        el.name = out->field;
        if (!op_of(e["op"], el, &out->op, err)) return false;
        return ops_allow(*lf, *out, err);
    }
    const FieldSpec* f = reg->field(out->field);
    if (!f) return *err = out->registry + " has no field \"" + out->field + "\"", false;
    if (f->read_only) return *err = out->registry + "." + out->field + " is read-only", false;
    const bool listish = f->type == FieldType::List || f->type == FieldType::Flags;
    if (e.contains("op")) {
        if (listish && !e["op"].is_array() && !(e["op"].is_object() && e["op"].contains("set")))
            return *err = out->registry + "." + out->field + " is a list: \"op\" sets the whole list ([...]); \"list\" changes it (clear, append, remove, replace)", false;
        if (!op_of(e["op"], *f, &out->op, err)) return false;
        return ops_allow(*f, *out, err);
    }
    if (!listish) return *err = out->registry + "." + out->field + " is not a list", false;
    if (f->ops == FieldOps::Indexed || f->ops == FieldOps::None || f->ops == FieldOps::Set || f->ops == FieldOps::Number) {
        out->list = ListOp::Clear;  // only to ask: a list op of any kind
        const bool ok = ops_allow(*f, *out, err);
        out->list = ListOp::None;
        if (!ok) return false;
    }
    const json& l = e["list"];
    if (l == "clear") return out->list = ListOp::Clear, true;  // the section's short form
    if (!l.is_object() || l.size() != 1) return *err = "\"list\" is one of {\"clear\": true}, {\"append\": [...]}, {\"remove\": [...]}, {\"replace\": [...]}", false;
    const std::string k = l.begin().key();
    const json& v = l.begin().value();
    if (k == "clear") {
        if (v != true) return *err = "{\"clear\": true}", false;
        out->list = ListOp::Clear;
        return true;
    }
    out->list = k == "append" ? ListOp::Append : k == "remove" ? ListOp::Remove : k == "replace" ? ListOp::Replace : ListOp::None;
    if (out->list == ListOp::None) return *err = "unknown list operation \"" + k + "\" (clear, append, remove, replace)", false;
    if (!v.is_array() || (v.empty() && out->list != ListOp::Replace)) return *err = "\"" + k + "\" takes a list of values", false;
    if (v.size() > kMaxListValues) return *err = "\"" + k + "\" takes at most " + std::to_string(kMaxListValues) + " values", false;
    for (const json& x : v) {
        Value y;
        if (!(f->type == FieldType::Flags ? scalar_of(x, FieldType::Enum, *f, &y, err) : value_of(x, *f, true, &y, err))) return false;
        out->list_values.push_back(y);
    }
    if (out->list == ListOp::Replace && f->max_items && out->list_values.size() > f->max_items)
        return *err = out->registry + "." + out->field + " holds at most " + std::to_string(f->max_items) + " values", false;
    return true;
}

}  // namespace

bool value_of(const json& j, const FieldSpec& f, bool element, Value* out, std::string* err) {
    if (f.type == FieldType::Flags && !element) return flags_of(j, f, out, err);
    if (element && f.type == FieldType::List) return element_value(j, f, out, err);
    if (f.type == FieldType::List && !element) {
        if (!j.is_array()) return *err = f.name + ": a list is expected, not " + brief(j), false;
        if (f.max_items && j.size() > f.max_items) return *err = f.name + ": at most " + std::to_string(f.max_items) + " values", false;
        if (j.size() > kMaxListValues) return *err = f.name + ": at most " + std::to_string(kMaxListValues) + " values in one list", false;
        std::vector<Value> v;
        for (const json& x : j) {
            Value y;
            if (!element_value(x, f, &y, err)) return false;
            v.push_back(std::move(y));
        }
        *out = Value::of_list(std::move(v));
        return true;
    }
    return scalar_of(j, element ? f.element : f.type, f, out, err);
}

bool op_of(const json& j, const FieldSpec& f, Op* op, std::string* err) {
    *op = Op{};
    if (!j.is_object()) {
        op->set = true;
        return value_of(j, f, false, &op->value, err);
    }
    if (j.contains("set")) {
        if (j.size() != 1) return *err = f.name + ": \"set\" stands alone", false;
        op->set = true;
        return value_of(j["set"], f, false, &op->value, err);
    }
    if (f.type != FieldType::Int && f.type != FieldType::Float)
        return *err = f.name + ": {mul, add, min, max} works on numbers; this field takes a plain value", false;
    if (j.empty()) return *err = f.name + ": an empty operation", false;
    for (const auto& [k, v] : j.items()) {
        if (k == "mul" || k == "add" || k == "min" || k == "max") {
            if (!v.is_number()) return *err = f.name + ": \"" + k + "\" takes a number", false;
            if (!std::isfinite(v.get<double>())) return *err = f.name + ": \"" + k + "\" takes a finite number", false;
            (k == "mul" ? op->mul : k == "add" ? op->add : k == "min" ? op->min : op->max) = v.get<double>();
        } else if (k == "raiseOnly" || k == "lowerOnly") {
            if (!v.is_boolean()) return *err = f.name + ": \"" + k + "\" takes true or false", false;
            (k == "raiseOnly" ? op->raise_only : op->lower_only) = v.get<bool>();
        } else {
            return *err = f.name + ": unknown operation \"" + k + "\" (set, mul, add, min, max, raiseOnly, lowerOnly)", false;
        }
    }
    if (op->min && op->max && *op->min > *op->max) return *err = f.name + ": min must not be greater than max", false;
    if (op->raise_only && op->lower_only) return *err = f.name + ": raiseOnly and lowerOnly together change nothing", false;
    return true;
}

bool raw_key(const std::string& path, std::string* key, std::string* err) {
    auto bad = [&](const std::string& why) { return *err = "raw path \"" + path + "\": " + why, false; };
    if (path.empty()) return bad("empty");
    if (path.size() > 256) return bad("longer than 256 characters");
    if (path.find('*') != std::string::npos) return bad("'*' is not allowed: a raw edit names exact places");
    auto letter = [](char c) { return std::isalpha(static_cast<unsigned char>(c)) || c == '_'; };
    auto word = [](char c) { return std::isalnum(static_cast<unsigned char>(c)) || c == '_'; };
    std::string k;
    size_t i = 0;
    for (;;) {
        if (i >= path.size() || !letter(path[i])) return bad("a member name starts with a letter or '_' (members are a.b, indices [n])");
        while (i < path.size() && word(path[i])) k += static_cast<char>(std::tolower(static_cast<unsigned char>(path[i++])));
        while (i < path.size() && path[i] == '[') {
            const size_t j = ++i;
            while (i < path.size() && std::isdigit(static_cast<unsigned char>(path[i]))) ++i;
            if (i == j || i >= path.size() || path[i] != ']') return bad("an index is [n], n a whole number");
            if (i - j > 9) return bad("an index has at most 9 digits");
            const size_t z = path.find_first_not_of('0', j);
            k += "[" + (z >= i ? std::string("0") : path.substr(z, i - z)) + "]";
            ++i;
        }
        if (i == path.size()) break;
        if (path[i] != '.') return bad("members are separated by '.'");
        k += '.';
        ++i;
    }
    *key = std::move(k);
    return true;
}

bool op_result(const Op& op, const Value& base, const FieldSpec& f, Value* out, std::string* why) {
    if (op.set) return *out = op.value, true;
    const double v = base.as_double();
    double r = v * op.mul.value_or(1.0) + op.add.value_or(0.0);
    if (op.min && r < *op.min) r = *op.min;  // std::clamp's order without its precondition on NaN
    if (op.max && r > *op.max) r = *op.max;
    if (op.raise_only) r = std::max(r, v);
    if (op.lower_only) r = std::min(r, v);
    if (!std::isfinite(r)) return *why = f.name + ": the result is not a finite number", false;
    if (f.type != FieldType::Int) return *out = Value::number(r), true;
    if (r < kInt64Lo || r >= kInt64Hi) return *why = f.name + ": the result " + json(r).dump() + " is more than a whole-number field holds", false;
    *out = Value::integer(static_cast<int64_t>(std::llround(r)));  // in range: llround is defined
    return true;
}

Value apply_op(const Op& op, const Value& base, const FieldSpec& f) {
    Value x;
    std::string why;
    return op_result(op, base, f, &x, &why) ? x : base;
}

namespace {
// ---- id migrations (RegistrySpec::migrations): an edit written for an older schema version, read as this one ----
struct Renamer {
    const Schema& schema;
    std::vector<std::string> notes;
    // name through every step of registry reg, oldest first; empty: no step renames it
    std::string chain(const RegistrySpec& reg, const std::string& name, bool field, std::string* steps) const {
        std::string cur = name, first, last;
        for (const Migration& m : reg.migrations) {
            const auto& map = field ? m.fields : m.ids;
            const auto it = map.find(cur);
            if (it == map.end()) continue;
            if (first.empty()) first = m.from;
            last = m.to;
            cur = it->second;
        }
        if (first.empty() || cur == name) return "";
        *steps = reg.name + " schema " + first + " -> " + last;
        return cur;
    }
    void note(const RegistrySpec& reg, const std::string& from, const std::string& to, const std::string& steps, bool field) {
        notes.push_back((field ? reg.name + "." + from + " -> " + reg.name + "." + to : from + " -> " + to) + " (" + steps + ")");
    }
    // A field path ("stackSize", "inputs[2].count", "inputs[*].item"): renamed only where this schema does not have it.
    void field(const RegistrySpec& reg, json* f) {
        if (!f->is_string()) return;
        const std::string path = f->get<std::string>();
        std::string steps;
        const size_t open = path.find('[');
        if (open == std::string::npos) {
            if (reg.field(path)) return;
            const std::string to = chain(reg, path, true, &steps);
            if (!to.empty()) note(reg, path, to, steps, true), *f = to;
            return;
        }
        const size_t close = path.find(']', open);
        if (close == std::string::npos) return;
        std::string list = path.substr(0, open), leaf = close + 1 < path.size() && path[close + 1] == '.' ? path.substr(close + 2) : "";
        const std::string index = path.substr(open, close - open + 1);
        if (!leaf.empty() && !reg.field(list + "[]." + leaf)) {
            const std::string to = chain(reg, list + "[]." + leaf, true, &steps);
            const size_t at = to.find("[].");
            if (at != std::string::npos) {
                note(reg, path, to.substr(0, at) + index + "." + to.substr(at + 3), steps, true);
                *f = to.substr(0, at) + index + "." + to.substr(at + 3);
                return;
            }
        }
        if (!reg.field(list)) {
            const std::string to = chain(reg, list, true, &steps);
            if (!to.empty()) {
                const std::string now = to + index + (leaf.empty() ? "" : "." + leaf);
                note(reg, path, now, steps, true), *f = now;
            }
        }
    }
    // A Kiln id of registry `of` (an exact selector, a "like", a reference value): renamed by that registry's steps.
    void id(const std::string& of, json* v) {
        const RegistrySpec* reg = schema.registry(of);
        if (!reg || reg->migrations.empty() || !v->is_string()) return;
        const std::string from = v->get<std::string>();
        if (from.find_first_of("*?") != std::string::npos) return;  // a glob names no one entry
        std::string steps;
        const std::string to = chain(*reg, from, false, &steps);
        if (!to.empty()) note(*reg, from, to, steps, false), *v = to;
    }
    // Not empty: the edit names one field twice, by an old name and by the name a step gave it (review of registry-rollback):
    // refused, since which of the two values it meant is unknown.
    std::string error;
    // An object keyed by field names (an add's "fields", a "where", an element's leaves): each key renamed by rename(key) (empty:
    // kept), and a key that a rename makes equal to another refuses the edit.
    template <class Rename, class Each>
    void keys(json* o, const std::string& what, Rename rename, Each each) {
        if (!o->is_object()) return;
        json renamed = json::object();
        std::map<std::string, std::string> written;  // the new key -> the key as the edit wrote it
        for (auto it = o->begin(); it != o->end(); ++it) {
            std::string k = rename(it.key());
            if (k.empty()) k = it.key();
            if (const auto seen = written.find(k); seen != written.end()) {
                if (error.empty())
                    error = "names " + what + k + " twice (" + (seen->second == k ? it.key() : seen->second) + " is its name before a schema migration): " +
                            "use " + k + " only";
                return;
            }
            written[k] = it.key();
            json v = it.value();
            each(k, it.key(), &v);
            renamed[k] = std::move(v);
        }
        *o = std::move(renamed);
    }
    // Every name a list of reg has had (its current name first): an element's leaf is renamed by a step keyed by any of them.
    std::vector<std::string> list_names(const RegistrySpec& reg, const std::string& list, const std::string& written) const {
        std::vector<std::string> out{list};
        if (written != list) out.push_back(written);
        for (const Migration& m : reg.migrations)
            for (const auto& [from, to] : m.fields) {
                std::string steps;
                if (from.find('[') == std::string::npos && chain(reg, from, true, &steps) == list &&
                    std::find(out.begin(), out.end(), from) == out.end())
                    out.push_back(from);
            }
        return out;
    }
    // An element's leaf ("amount" of the list written as `written`, now `list`): its name now, or empty when no step renames it.
    std::string leaf_name(const RegistrySpec& reg, const FieldSpec& list, const std::string& written, const std::string& leaf) {
        if (list.element_field(leaf)) return "";
        for (const std::string& name : list_names(reg, list.name, written)) {
            std::string steps;
            const std::string to = chain(reg, name + "[]." + leaf, true, &steps);
            if (to.rfind(list.name + "[].", 0) == 0) {
                note(reg, name + "[]." + leaf, to, steps, true);
                return to.substr(list.name.size() + 3);
            }
        }
        return "";
    }
    // Reference values in a value of field f (an id, a list of ids, elements with reference leaves), and the leaf names of its
    // elements. written: the list's path as the edit wrote it (an old name's leaves are keyed by it).
    void values(const RegistrySpec& reg, const FieldSpec& f, const std::string& written, json* v) {
        if (v->is_object() && v->contains("set") && v->size() == 1) return values(reg, f, written, &(*v)["set"]);
        if (!f.ref.empty() && f.type == FieldType::Id) return id(f.ref, v);
        if (f.type != FieldType::List) return;
        auto element = [&](json* x) {
            if (f.element == FieldType::Struct && x->is_object()) {
                keys(x, reg.name + "." + f.name + "[].", [&](const std::string& k) { return leaf_name(reg, f, written, k); },
                     [&](const std::string& k, const std::string&, json* lv) {
                         if (const FieldSpec* leaf = f.element_field(k); leaf && !leaf->ref.empty()) id(leaf->ref, lv);
                     });
            } else if (!f.ref.empty()) {
                id(f.ref, x);
            }
        };
        if (v->is_array()) for (json& x : *v) element(&x);
        else element(v);
    }
    // A field name of reg as an object key ("fields", "where"): renamed where this schema does not have it.
    std::string key_name(const RegistrySpec& reg, const std::string& k) {
        json j = k;
        field(reg, &j);
        return j.get<std::string>() == k ? std::string() : j.get<std::string>();
    }
    void edit(const RegistrySpec& reg, json* e) {
        const std::string written = e->contains("field") && (*e)["field"].is_string() ? (*e)["field"].get<std::string>() : "";
        if (e->contains("field")) field(reg, &(*e)["field"]);
        if (e->contains("select")) {
            json& s = (*e)["select"];
            if (s.is_string()) id(reg.name, &s);
            else if (s.is_object() && s.contains("id")) id(reg.name, &s["id"]);
            // a where condition names an entry's own fields; a plain value of a reference field is an id
            if (s.is_object() && s.contains("where"))
                keys(&s["where"], reg.name + ".", [&](const std::string& k) { return key_name(reg, k); }, [&](const std::string& k, const std::string&, json* c) {
                    if (const FieldSpec* f = reg.field(k); f && f->type == FieldType::Id && !f->ref.empty()) id(f->ref, c);
                });
        }
        if (e->contains("like")) id(reg.name, &(*e)["like"]);
        if (e->contains("fields"))
            keys(&(*e)["fields"], reg.name + ".", [&](const std::string& k) { return key_name(reg, k); }, [&](const std::string& k, const std::string& old, json* v) {
                if (const FieldSpec* f = reg.field(k)) values(reg, *f, old, v);
            });
        if (!error.empty() || !e->contains("field") || !(*e)["field"].is_string()) return;
        const std::string path = (*e)["field"].get<std::string>();
        // an element edit's op is that element's (or leaf's) value; a whole field's op or list op its own. review: the list's
        // spec for an element path ("inputs[0]" names no field of its own, so its whole-element op was skipped)
        const bool element = path.find('[') != std::string::npos;
        const FieldSpec* f = reg.field(element ? path.substr(0, path.find('[')) : path);
        if (!f) return;
        const std::string written_list = written.substr(0, written.find('['));
        if (e->contains("op")) {
            json& op = (*e)["op"];
            if (!element) values(reg, *f, written_list, &op);
            else if (f->type == FieldType::List) {
                const size_t dot = path.find("].");
                if (dot != std::string::npos) {
                    if (const FieldSpec* leaf = f->element_field(path.substr(dot + 2)); leaf && !leaf->ref.empty()) id(leaf->ref, op.is_object() && op.contains("set") ? &op["set"] : &op);
                } else {
                    values(reg, *f, written_list, op.is_object() && op.contains("set") ? &op["set"] : &op);
                }
            }
        }
        if (e->contains("list") && (*e)["list"].is_object())
            for (auto it = (*e)["list"].begin(); it != (*e)["list"].end(); ++it) values(reg, *f, written_list, &it.value());
    }
};
}  // namespace

std::vector<Refusal> refuse_over_cap(const Batch& b, const std::vector<Refusal>& refused, const Schema& schema,
                                     const std::map<std::string, size_t>& prior) {
    std::map<std::string, size_t> count = prior;
    std::map<std::string, uint32_t> first;  // registry -> the n of its first edit in this batch
    std::map<std::string, uint32_t> past;   // registry -> the n of its first edit past its cap
    const auto cap_of = [&](const std::string& r) -> size_t {
        const RegistrySpec* spec = schema.registry(r);
        return spec ? spec->edits_per_call : 4096u;
    };
    const auto count_one = [&](const std::string& r, uint32_t n) {
        first.emplace(r, n);
        if (++count[r] > cap_of(r)) past.emplace(r, n);
    };
    for (const Edit& e : b.edits) count_one(e.registry, e.n);
    for (const Refusal& x : refused) count_one(x.registry, x.n);
    if (past.empty()) return {};
    std::string over;
    for (const auto& [r, n] : past) over += (over.empty() ? "" : ", ") + r;
    std::vector<Refusal> rows;  // the registries over their cap first (a commit's reason is its first row), then the others
    std::vector<std::pair<std::string, uint32_t>> order;
    for (const auto& [r, n] : first)
        if (past.count(r)) order.emplace_back(r, n);
    for (const auto& [r, n] : first)
        if (!past.count(r)) order.emplace_back(r, n);
    for (const auto& [r, n] : order) {
        Refusal row;
        row.mod = b.mod, row.source = b.source, row.stage = b.stage, row.registry = r;
        const auto p = past.find(r);
        row.n = p != past.end() ? p->second : n;
        if (p != past.end())
            row.reason = r + ": " + std::to_string(count[r]) + " edits in one call, over this registry's cap of " + std::to_string(cap_of(r)) +
                         " edits per call; the call is refused whole and nothing of it was applied (split the edits over the entry point's "
                         "phases or over several calls)";
        else
            row.reason = r + ": refused with its call, which has more edits of " + over + " than the cap allows; nothing of the call was applied";
        rows.push_back(std::move(row));
    }
    return rows;
}

bool parse_batch(const std::string& text, const Schema& schema, Batch* out, std::vector<Refusal>* refused, std::string* err) {
    *out = Batch{};
    if (text.size() > kMaxIrBytes) return *err = "the IR document is larger than 16 MiB", false;
    // nlohmann's parser does not recurse and its values are destroyed without recursion; the document is at most 16 MiB.
    // The nesting limit is then the JSON form's, one rule for both (too_deep).
    const json doc = json::parse(text, nullptr, false, true);
    if (doc.is_discarded()) return *err = "the IR document is not JSON", false;
    return parse_batch(doc, schema, out, refused, err);
}

bool parse_batch(const json& doc, const Schema& schema, Batch* out, std::vector<Refusal>* refused, std::string* err) {
    *out = Batch{};
    // nothing thrown inside leaves: a document that makes the reader throw is refused whole, an edit that does is refused alone
    try {
        if (too_deep(doc)) return *err = "the IR document nests deeper than " + std::to_string(kMaxDepth) + " levels", false;
        if (!doc.is_object()) return *err = "the IR document is an object", false;
        if (!doc.contains("ir") || doc["ir"] != 1) return *err = "\"ir\": 1 is the only IR format this Kiln reads", false;
        if (!doc.contains("mod") || !doc["mod"].is_string() || doc["mod"].get<std::string>().empty()) return *err = "\"mod\" names the mod", false;
        if (!mod_id_ok(doc["mod"].get<std::string>(), err)) return *err = "\"mod\": " + *err, false;
        if (!doc.contains("stage") || !doc["stage"].is_string() || !stage_of(doc["stage"].get<std::string>(), &out->stage))
            return *err = "\"stage\" is add, alter or final", false;
        if (!doc.contains("edits") || !doc["edits"].is_array()) return *err = "\"edits\" is a list", false;
        for (const char* k : {"modVersion", "source"})
            if (doc.contains(k) && !doc[k].is_string()) return *err = std::string("\"") + k + "\" is a text", false;
        out->mod = doc["mod"].get<std::string>();
        out->mod_version = doc.value("modVersion", "");
        out->source = doc.value("source", "");
    } catch (const std::exception& e) {
        *out = Batch{};
        return *err = std::string("the IR document could not be read: ") + e.what(), false;
    }
    std::set<uint32_t> used;
    uint32_t pos = 0;
    for (const json& e : doc["edits"]) {
        Edit x;
        const uint32_t at = pos++;
        x.n = at;
        std::string why;
        bool ok = false;
        try {
            if (e.is_object() && e.contains("n")) {
                const json& n = e["n"];
                if (!n.is_number_unsigned() || n.get<uint64_t>() > UINT32_MAX) {
                    refused->push_back({out->mod, out->source, out->stage, at, "\"n\" is a whole number 0..4294967295, not " + brief(n), "", "", "", ""});
                    continue;
                }
                x.n = static_cast<uint32_t>(n.get<uint64_t>());
            }
            if (!used.insert(x.n).second) {
                refused->push_back({out->mod, out->source, out->stage, x.n, "the edit number " + std::to_string(x.n) + " is used twice in this batch", "", "", "", ""});
                continue;
            }
            // id migrations first: an old field or id is read as the running schema's before the edit is checked and matched
            const RegistrySpec* reg = e.is_object() && e.contains("registry") && e["registry"].is_string() ? schema.registry(e["registry"].get<std::string>()) : nullptr;
            bool any_steps = false;
            if (reg) any_steps = !reg->migrations.empty();
            for (const auto& [n, r] : schema.registries) any_steps = any_steps || (!r.migrations.empty() && reg);
            if (any_steps) {
                json m = e;
                Renamer rn{schema, {}, {}};
                rn.edit(*reg, &m);
                if (!rn.error.empty()) {
                    ok = false;
                    why = "the edit " + rn.error;
                } else if (!rn.notes.empty()) {
                    for (const std::string& note : rn.notes) out->migrated.push_back("#" + std::to_string(x.n) + ": " + note);
                    ok = edit_of(m, *out, schema, &x, &why);
                } else {
                    ok = edit_of(e, *out, schema, &x, &why);
                }
            } else {
                ok = edit_of(e, *out, schema, &x, &why);
            }
        } catch (const std::exception& ex) {
            ok = false;
            why = std::string("this edit could not be read: ") + ex.what();
        }
        if (ok) {
            out->edits.push_back(std::move(x));
            continue;
        }
        Refusal r{out->mod, out->source, out->stage, x.n, why, "", "", "", "", false};
        if (e.is_object()) {  // where, when the document says it
            if (e.contains("registry") && e["registry"].is_string()) r.registry = e["registry"].get<std::string>();
            if (e.contains("field") && e["field"].is_string()) r.field = e["field"].get<std::string>();
            else if (e.contains("raw") && e["raw"].is_object() && !e["raw"].empty()) r.field = e["raw"].begin().key(), r.raw = true;
            if (e.contains("id") && e["id"].is_string()) r.id = e["id"].get<std::string>();
            else if (e.contains("select") && e["select"].is_string()) r.id = e["select"].get<std::string>();
            else if (e.contains("select") && e["select"].is_object() && e["select"].contains("id") && e["select"]["id"].is_string()) r.id = e["select"]["id"].get<std::string>();
            if (r.id.find_first_of("*?") != std::string::npos) r.id.clear();  // a glob names no one entry
            if (e.value("entry", json()) == "add" && !r.registry.empty() && !r.id.empty()) r.entry = r.registry + "|" + r.id;  // a reference to it names this as its cause
        }
        refused->push_back(std::move(r));
    }
    return true;
}

namespace {
json selector_json(const Selector& s) {
    json o = {{"id", s.id}};
    if (s.where.is_object() && !s.where.empty()) o["where"] = s.where;
    if (!s.has.empty()) o["has"] = s.has;
    if (!s.has_not.empty()) o["hasNot"] = s.has_not;
    return o;
}
// An op as edit_of reads it: a whole element with fields only as {"set": {...}} (a plain object reads as relative numbers).
json op_json(const Op& op) {
    if (op.set && op.value.kind == Value::Struct) return {{"set", op.value.to_json()}};
    return op.to_json();
}
json values_json(const std::vector<Value>& vs) {
    json a = json::array();
    for (const Value& v : vs) a.push_back(v.to_json());
    return a;
}
}  // namespace

json batch_json(const Batch& b) {
    json edits = json::array();
    for (const Edit& e : b.edits) {
        json o = {{"n", e.n}, {"registry", e.registry}};
        if (e.exclusive) o["exclusive"] = true;
        if (e.yield) o["yield"] = true;
        if (!e.raw.empty()) {
            o["select"] = selector_json(e.select);
            json raw = json::object();
            for (const RawWrite& w : e.raw) raw[w.path] = w.op;
            o["raw"] = std::move(raw);
        } else if (e.entry == EntryOp::Add) {
            o["entry"] = "add";
            o["id"] = e.add_id;
            if (!e.like.empty()) o["like"] = e.like;
            if (!e.add_fields.empty()) {
                json f = json::object();
                for (const auto& [k, v] : e.add_fields) f[k] = v.to_json();
                o["fields"] = std::move(f);
            }
        } else if (e.entry == EntryOp::Remove) {
            o["entry"] = "remove";
            o["select"] = selector_json(e.select);
        } else {
            o["select"] = selector_json(e.select);
            o["field"] = e.field;
            switch (e.list) {
                case ListOp::None: o["op"] = op_json(e.op); break;
                case ListOp::Clear: o["list"] = {{"clear", true}}; break;
                case ListOp::Append: o["list"] = {{"append", values_json(e.list_values)}}; break;
                case ListOp::Remove: o["list"] = {{"remove", values_json(e.list_values)}}; break;
                case ListOp::Replace: o["list"] = {{"replace", values_json(e.list_values)}}; break;
            }
        }
        edits.push_back(std::move(o));
    }
    json doc = {{"ir", 1}, {"mod", b.mod}, {"stage", stage_name(b.stage)}, {"edits", std::move(edits)}};
    if (!b.mod_version.empty()) doc["modVersion"] = b.mod_version;
    if (!b.source.empty()) doc["source"] = b.source;
    return doc;
}

bool lower_section(const json& section, const std::string& mod, const std::string& mod_version, const std::string& source, const Schema& schema,
                   std::vector<Batch>* out, std::vector<Refusal>* refused, std::string* err) {
    out->clear();
    try {
        if (too_deep(section)) return *err = "the \"registry\" section nests deeper than " + std::to_string(kMaxDepth) + " levels", false;
        if (!section.is_object()) return *err = "the \"registry\" section is {\"add\": [...], \"alter\": [...], \"final\": [...]}", false;
        for (const auto& [k, v] : section.items()) {
            Stage st;
            if (!stage_of(k, &st)) return *err = "the \"registry\" section: unknown member \"" + k + "\" (add, alter, final)", false;
            if (!v.is_array()) return *err = "the \"registry\" section: \"" + k + "\" is a list of edits", false;
        }
    } catch (const std::exception& e) {
        return *err = std::string("the \"registry\" section could not be read: ") + e.what(), false;
    }
    for (const char* stage : {"add", "alter", "final"}) {  // in stage order, whatever the file's order
        if (!section.contains(stage)) continue;
        json doc = {{"ir", 1}, {"mod", mod}, {"stage", stage}, {"source", source}, {"edits", section[stage]}};
        if (!mod_version.empty()) doc["modVersion"] = mod_version;
        Batch b;
        if (!parse_batch(doc, schema, &b, refused, err)) return out->clear(), false;
        out->push_back(std::move(b));
    }
    return true;
}

}  // namespace kc::registry
