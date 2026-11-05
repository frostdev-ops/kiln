// SPDX-License-Identifier: MIT
// EMF mod settings (API v13). Each mod's values live in <configDir>/emf/<id>.ini, section [<id>] (configDir =
// KILN_PATH_CONFIG: the active profile's configDir, else <game>/kiln/config). The same section is mirrored into
// <game>/emf/config/<id>.ini, the file EMF uses, because shipped mods read their switch and values there directly
// (GetPrivateProfileIntW: big_stacks, sleep, herald, clock). A first start imports an existing EMF ini. Declarations
// come from mod.json "settings" (before emf_mod_load) and setting_declare. Value rules follow EMF's reference.md
// ("Settings"): out of range from a call is refused, from the ini clamped and written back; a number between steps
// moves to the nearest step; choice = index, select = option value, color = #rrggbb.
// A schema snapshot (no values) goes to <configDir>/.schema/emf/<id>.json for the launcher (contracts.md 5.5).
#include <intrin.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <ctime>
#include <cstdio>
#include <map>

#include "emf_internal.h"
#include "nlohmann/json.hpp"

namespace kemf {

namespace {

using json = nlohmann::json;

struct SettingDef {
    std::string key, type;  // bool int float slider choice select keybind color text
    std::string label, help, group;
    std::string def;        // default, stored form
    bool has_min = false, has_max = false, has_step = false;
    double min = 0, max = 0, step = 0;
    std::vector<std::string> choices;                          // choice: labels (stored as the index)
    std::vector<std::pair<std::string, std::string>> options;  // select: value, label
    uint32_t max_length = 256;
    bool live = false;
    json enabled_when;
    bool whole() const {  // stored as a whole number
        if (type == "int") return true;
        if (type == "slider") return has_step && step == std::floor(step) && min == std::floor(min);
        return false;
    }
};

struct Store {
    std::string mod;
    std::vector<SettingDef> defs;
    std::map<std::string, std::string> values;  // every key of the section (declared or not), stored form
    std::filesystem::path ini, mirror;
};

std::mutex g_mutex;
std::map<std::string, Store> g_stores;  // by mod id

struct Watch {
    Mod* mod;
    std::string filter;  // "" = every mod
    EmfSettingCallback cb;
    void* user;
    std::atomic<bool> dead{false};
};
std::vector<std::unique_ptr<Watch>> g_watches;
std::vector<std::pair<std::string, std::string>> g_changes;  // (mod, key) waiting for the next step

// ---------------------------------------------------------------------------------------------- ini files ------
using Section = std::vector<std::pair<std::string, std::string>>;
struct IniDoc {
    std::vector<std::pair<std::string, Section>> sections;  // in file order; "" = keys before the first section
    Section* find(std::string_view name) {
        for (auto& [n, s] : sections)
            if (iequals(n, name)) return &s;
        return nullptr;
    }
};

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t' || s.front() == '\r')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t' || s.back() == '\r')) s.remove_suffix(1);
    return s;
}

IniDoc parse_ini(const std::string& text) {
    IniDoc d;
    d.sections.push_back({"", {}});
    size_t i = 0;
    std::string_view t = text;
    if (t.size() >= 3 && t.substr(0, 3) == "\xEF\xBB\xBF") i = 3;
    while (i < t.size()) {
        size_t j = t.find('\n', i);
        if (j == std::string_view::npos) j = t.size();
        const std::string_view line = trim(t.substr(i, j - i));
        i = j + 1;
        if (line.empty() || line[0] == ';' || line[0] == '#') continue;
        if (line[0] == '[') {
            const size_t e = line.find(']');
            d.sections.push_back({std::string(trim(line.substr(1, e == std::string_view::npos ? line.size() - 1 : e - 1))), {}});
            continue;
        }
        const size_t eq = line.find('=');
        if (eq == std::string_view::npos) continue;
        d.sections.back().second.push_back({std::string(trim(line.substr(0, eq))), std::string(trim(line.substr(eq + 1)))});
    }
    return d;
}

std::string render_ini(const IniDoc& d) {
    std::string out;
    for (const auto& [name, sec] : d.sections) {
        if (name.empty() && sec.empty()) continue;
        if (!name.empty()) out += (out.empty() ? "" : "\n") + std::string("[") + name + "]\n";
        for (const auto& [k, v] : sec) out += k + "=" + v + "\n";
    }
    return out;
}

void set_in(Section& sec, const std::string& k, const std::string& v) {
    for (auto& [kk, vv] : sec)
        if (iequals(kk, k)) {
            vv = v;
            return;
        }
    sec.push_back({k, v});
}

// Writes the store's section into `file`, keeping other sections and the section's other keys.
void write_section(const Store& st, const std::filesystem::path& file, const char* header) {
    std::string text;
    IniDoc d = read_file(file, &text) ? parse_ini(text) : IniDoc{};
    if (d.sections.empty()) d.sections.push_back({"", {}});
    Section* sec = d.find(st.mod);
    if (!sec) {
        d.sections.push_back({st.mod, {}});
        sec = &d.sections.back().second;
    }
    // order: enabled, then the declared settings, then the rest
    Section ordered;
    if (auto it = st.values.find("enabled"); it != st.values.end()) ordered.push_back({"enabled", it->second});
    for (const auto& def : st.defs)
        if (auto it = st.values.find(def.key); it != st.values.end()) ordered.push_back({def.key, it->second});
    for (const auto& [k, v] : st.values) {
        bool have = false;
        for (const auto& [kk, vv] : ordered) have |= kk == k;
        if (!have) ordered.push_back({k, v});
    }
    for (const auto& [k, v] : *sec) {
        bool have = false;
        for (const auto& [kk, vv] : ordered) have |= iequals(kk, k);
        if (!have) ordered.push_back({k, v});
    }
    *sec = ordered;
    std::string out = render_ini(d);
    if (header && text.empty()) out = std::string(header) + out;
    std::string old;
    if (read_file(file, &old) && old == out) return;
    if (!write_file_atomic(file, out)) logf(KILN_LOG_WARN, "settings: cannot write %s", path_utf8(file).c_str());
}

const char* kHeader =
    "; EMF mod settings, kept by Kiln (kiln-emf). Section [<mod>]: enabled=0/1 switches the mod at the next start;\n"
    "; the other keys are the mod's settings (mod.json). Kiln mirrors this section to <game>/emf/config/<mod>.ini.\n";

void persist(const Store& st) {
    write_section(st, st.ini, kHeader);
    write_section(st, st.mirror, nullptr);
}

// ---------------------------------------------------------------------------------------------- values --------
std::string num_text(double v, bool whole) {
    if (whole) return std::to_string(static_cast<long long>(std::llround(v)));
    char b[64];
    std::snprintf(b, sizeof b, "%.10g", v);
    return b;
}

bool parse_num(std::string_view s, double* out) {
    const std::string t(trim(s));
    if (t.empty()) return false;
    char* end = nullptr;
    const double v = std::strtod(t.c_str(), &end);
    if (!end || *end != '\0' || !std::isfinite(v)) return false;
    *out = v;
    return true;
}

bool parse_bool(std::string_view s, bool* out) {
    const std::string t = lower(trim(s));
    if (t == "1" || t == "true" || t == "yes" || t == "on") return *out = true, true;
    if (t == "0" || t == "false" || t == "no" || t == "off") return *out = false, true;
    return false;
}

// Checks / normalizes `in` for `d`. clamp: an ini value (clamped, not refused). False + why when refused.
bool normalize(const SettingDef& d, std::string_view in, bool clamp, std::string* out, std::string* why) {
    const std::string t(trim(in));
    if (d.type == "bool") {
        bool b;
        if (!parse_bool(t, &b)) return *why = "a switch takes 1/0 (on/off)", false;
        *out = b ? "1" : "0";
        return true;
    }
    if (d.type == "int" || d.type == "float" || d.type == "slider") {
        double v;
        if (!parse_num(t, &v)) return *why = "not a number", false;
        if (d.type == "int" && v != std::floor(v)) {
            if (!clamp) return *why = "a whole number is needed", false;
            v = std::round(v);
        }
        if (d.type == "slider" && d.has_step && d.step > 0) v = d.min + std::round((v - d.min) / d.step) * d.step;
        if (d.has_min && v < d.min) {
            if (!clamp) return *why = format("below the minimum %s", num_text(d.min, d.whole()).c_str()), false;
            v = d.min;
        }
        if (d.has_max && v > d.max) {
            if (!clamp) return *why = format("above the maximum %s", num_text(d.max, d.whole()).c_str()), false;
            v = d.max;
        }
        if (d.type == "int" && std::fabs(v) > 999999999999999.0) return *why = "out of range", false;
        *out = num_text(v, d.whole());
        return true;
    }
    if (d.type == "choice") {
        double v;
        if (parse_num(t, &v) && v == std::floor(v) && v >= 0 && v < static_cast<double>(d.choices.size())) {
            *out = std::to_string(static_cast<long long>(v));
            return true;
        }
        for (size_t i = 0; i < d.choices.size(); ++i)
            if (iequals(d.choices[i], t)) return *out = std::to_string(i), true;
        return *why = "not one of its choices", false;
    }
    if (d.type == "select") {
        for (const auto& [v, l] : d.options)
            if (v == t) return *out = v, true;
        for (const auto& [v, l] : d.options)
            if (iequals(v, t) || iequals(l, t)) return *out = v, true;
        return *why = "not one of its options", false;
    }
    if (d.type == "color") {
        std::string c = lower(t);
        if (c.size() == 4 && c[0] == '#') c = std::string("#") + c[1] + c[1] + c[2] + c[2] + c[3] + c[3];
        if (c.size() != 7 || c[0] != '#' || c.find_first_not_of("0123456789abcdef", 1) != std::string::npos)
            return *why = "a colour is #rrggbb", false;
        *out = c;
        return true;
    }
    // text, keybind / key
    if (t.find('\n') != std::string::npos) return *why = "no line breaks", false;
    if (t.size() > 256 || (d.type == "text" && t.size() > d.max_length * 4)) return *why = "too long", false;
    *out = t;
    return true;
}

bool parse_def(const json& j, SettingDef* d, std::string* why) {
    if (!j.is_object() || !j.contains("key") || !j["key"].is_string()) return *why = "a setting without \"key\"", false;
    d->key = j["key"].get<std::string>();
    if (d->key.empty() || d->key.size() > 48 || d->key == "enabled" ||
        d->key.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-") != std::string::npos)
        return *why = "setting key \"" + d->key + "\" is not 1-48 of A-Z a-z 0-9 _ . - (\"enabled\" is reserved)", false;
    d->type = j.contains("type") && j["type"].is_string() ? lower(j["type"].get<std::string>()) : "text";
    if (d->type == "key") d->type = "keybind";
    if (j.contains("control") && j["control"] == "slider" && d->type == "int") d->type = "int";
    static const char* kTypes[] = {"bool", "int", "float", "slider", "choice", "select", "keybind", "color", "text"};
    if (std::find_if(std::begin(kTypes), std::end(kTypes), [&](const char* t) { return d->type == t; }) == std::end(kTypes))
        return *why = "setting " + d->key + ": unknown type \"" + d->type + "\"", false;
    auto str = [&](const char* k) {
        return j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : std::string();
    };
    d->label = str("label").empty() ? d->key : str("label");
    d->help = str("help");
    d->group = str("group");
    d->live = j.contains("live") && j["live"].is_boolean() && j["live"].get<bool>();
    if (j.contains("min") && j["min"].is_number()) d->has_min = true, d->min = j["min"].get<double>();
    if (j.contains("max") && j["max"].is_number()) d->has_max = true, d->max = j["max"].get<double>();
    if (j.contains("step") && j["step"].is_number()) d->has_step = true, d->step = j["step"].get<double>();
    if (d->type == "slider" && (!d->has_min || !d->has_max)) return *why = "slider " + d->key + " needs min and max", false;
    if (j.contains("maxLength") && j["maxLength"].is_number_integer())
        d->max_length = static_cast<uint32_t>(std::clamp<int64_t>(j["maxLength"].get<int64_t>(), 1, 256));
    if (j.contains("choices") && j["choices"].is_array())
        for (const auto& c : j["choices"])
            if (c.is_string()) d->choices.push_back(c.get<std::string>());
    if (j.contains("options") && j["options"].is_array())
        for (const auto& o : j["options"]) {
            if (o.is_string()) d->options.push_back({o.get<std::string>(), o.get<std::string>()});
            else if (o.is_object() && o.contains("value"))
                d->options.push_back({o["value"].is_string() ? o["value"].get<std::string>() : o["value"].dump(),
                                      o.contains("label") && o["label"].is_string() ? o["label"].get<std::string>()
                                                                                    : o["value"].dump()});
        }
    if (d->type == "choice" && d->choices.empty()) return *why = "choice " + d->key + " has no choices", false;
    if (d->type == "select" && d->options.empty()) return *why = "select " + d->key + " has no options", false;
    if (j.contains("enabledWhen")) d->enabled_when = j["enabledWhen"];
    // the default, in stored form
    std::string def;
    if (j.contains("default") && !j["default"].is_null()) {
        const json& v = j["default"];
        if (v.is_boolean()) def = v.get<bool>() ? "1" : "0";
        else if (v.is_number_integer()) def = std::to_string(v.get<int64_t>());
        else if (v.is_number()) def = num_text(v.get<double>(), false);
        else if (v.is_string()) def = v.get<std::string>();
    } else if (d->type == "bool") {
        def = "0";
    } else if (d->type == "int" || d->type == "float" || d->type == "slider") {
        def = num_text(d->has_min ? d->min : 0, d->whole());
    } else if (d->type == "choice") {
        def = "0";
    } else if (d->type == "select") {
        def = d->options.front().first;
    }
    std::string norm, w;
    if (!normalize(*d, def, true, &norm, &w)) {
        if (d->type == "keybind") return *why = "keybind " + d->key + ": its default is no key", false;
        return *why = "setting " + d->key + ": its default " + def + " is not valid (" + w + ")", false;
    }
    d->def = norm;
    return true;
}

SettingDef def_from_c(const EmfSettingDef* c) {
    SettingDef d;
    d.key = c->key ? c->key : "";
    static const char* kTypes[] = {"bool", "int", "float", "choice", "keybind", "text"};
    d.type = c->type < 6 ? kTypes[c->type] : "?";
    d.label = c->label ? c->label : d.key;
    d.help = c->help ? c->help : "";
    d.has_min = (c->flags & EMF_SETTING_HAS_MIN) != 0;
    d.has_max = (c->flags & EMF_SETTING_HAS_MAX) != 0;
    d.min = c->min;
    d.max = c->max;
    d.live = (c->flags & EMF_SETTING_LIVE) != 0;
    if (c->choices) {
        std::string_view s = c->choices;
        size_t i = 0;
        while (i <= s.size()) {
            size_t j = s.find('|', i);
            if (j == std::string_view::npos) j = s.size();
            d.choices.emplace_back(trim(s.substr(i, j - i)));
            i = j + 1;
        }
    }
    d.def = c->default_value ? c->default_value : "";
    return d;
}

Store* store_of(std::string_view mod) {
    auto it = g_stores.find(lower(mod));
    return it == g_stores.end() ? nullptr : &it->second;
}

const SettingDef* def_of(const Store* st, std::string_view key) {
    if (!st) return nullptr;
    for (const auto& d : st->defs)
        if (d.key == key) return &d;
    for (const auto& d : st->defs)
        if (iequals(d.key, key)) return &d;
    return nullptr;
}

Store& make_store(const std::string& id) {
    Store& st = g_stores[id];
    if (st.mod.empty()) {
        st.mod = id;
        st.ini = P.config / L"emf" / to_wide(id + ".ini");
        st.mirror = P.emf / L"config" / to_wide(id + ".ini");
        std::string text;
        std::error_code ec;
        const bool have = read_file(st.ini, &text);
        if (!have && read_file(st.mirror, &text))
            logf(KILN_LOG_INFO, "settings: %s: imported EMF's %s", id.c_str(), path_utf8(st.mirror).c_str());
        if (!text.empty()) {
            IniDoc d = parse_ini(text);
            if (Section* s = d.find(id))
                for (const auto& [k, v] : *s) st.values[k] = v;
        }
    }
    return st;
}

// Adds or replaces a declaration and fits the stored value to it (clamped; a value that does not fit -> default).
void declare(Store& st, SettingDef d, const Mod* m) {
    auto it = std::find_if(st.defs.begin(), st.defs.end(), [&](const SettingDef& x) { return x.key == d.key; });
    if (it != st.defs.end()) *it = d;
    else st.defs.push_back(d);
    auto v = st.values.find(d.key);
    if (v == st.values.end()) {
        st.values[d.key] = d.def;
        return;
    }
    std::string norm, why;
    if (!normalize(d, v->second, true, &norm, &why)) {
        mod_logf(m, KILN_LOG_WARN, "setting %s: the saved value \"%s\" is not valid (%s): the default %s is used", d.key.c_str(),
                 v->second.c_str(), why.c_str(), d.def.c_str());
        norm = d.def;
    } else if (norm != v->second) {
        mod_logf(m, KILN_LOG_INFO, "setting %s: the saved value %s was fitted to %s", d.key.c_str(), v->second.c_str(),
                 norm.c_str());
    }
    v->second = norm;
}

void queue_change(const std::string& mod, const std::string& key) {
    g_changes.push_back({mod, key});
}

// ---------------------------------------------------------------------------------------------- the C API -----
bool api_setting_declare(const char* mod, const EmfSettingDef* def) {
    Mod* caller = caller_mod(_ReturnAddress());
    if (!mod || !valid_mod_id(lower(mod)) || !def || def->size < offsetof(EmfSettingDef, choices) + sizeof(def->choices) ||
        !def->key) {
        refuse(caller, "setting_declare", "bad mod id or definition");
        return false;
    }
    SettingDef d = def_from_c(def);
    if (d.type == "?" || d.key.empty() || d.key == "enabled" || d.key.size() > 48) {
        refuse(caller, "setting_declare", "bad key or type");
        return false;
    }
    std::string norm, why;
    if (!normalize(d, d.def, true, &norm, &why)) {
        if (d.type == "bool") norm = "0";
        else if (d.type == "choice") norm = "0";
        else if (d.type == "int" || d.type == "float") norm = num_text(d.has_min ? d.min : 0, d.type == "int");
        else norm = "";
    }
    d.def = norm;
    std::lock_guard lock(g_mutex);
    Store& st = make_store(lower(mod));
    declare(st, d, find_mod(mod));
    persist(st);
    return true;
}

bool lookup(const char* mod, const char* key, std::string* value, const SettingDef** out_def) {
    if (!mod || !key) return false;
    Store* st = store_of(mod);
    if (!st) return false;
    if (std::string_view(key) == "enabled") {
        auto it = st->values.find("enabled");
        *value = it == st->values.end() ? "1" : it->second;
        if (out_def) *out_def = nullptr;
        return true;
    }
    const SettingDef* d = def_of(st, key);
    if (!d) return false;
    auto it = st->values.find(d->key);
    *value = it == st->values.end() ? d->def : it->second;
    if (out_def) *out_def = d;
    return true;
}

int64_t api_setting_int(const char* mod, const char* key, int64_t fallback) {
    std::lock_guard lock(g_mutex);
    std::string v;
    if (!lookup(mod, key, &v, nullptr)) return fallback;
    double d;
    if (!parse_num(v, &d)) return fallback;
    return static_cast<int64_t>(d);
}

double api_setting_float(const char* mod, const char* key, double fallback) {
    std::lock_guard lock(g_mutex);
    std::string v;
    if (!lookup(mod, key, &v, nullptr)) return fallback;
    double d;
    return parse_num(v, &d) ? d : fallback;
}

uint32_t api_setting_text(const char* mod, const char* key, char* buf, uint32_t size) {
    std::lock_guard lock(g_mutex);
    std::string v;
    if (!lookup(mod, key, &v, nullptr)) {
        if (buf && size) buf[0] = '\0';
        return 0;
    }
    if (buf && size) {
        copy_cstr(buf, size, v);
        if (v.empty() && size >= 2) buf[1] = '\0';  // a declared empty value (emf::setting_text tells them apart)
    }
    return static_cast<uint32_t>(v.size());
}

bool set_locked(const char* mod, const char* key, const char* value, std::string* why) {
    Store* st = store_of(mod ? mod : "");
    if (!st) return *why = "unknown mod", false;
    std::string norm;
    if (std::string_view(key) == "enabled") {
        bool b;
        if (!parse_bool(value, &b)) return *why = "enabled takes 1/0", false;
        norm = b ? "1" : "0";
        if (st->values["enabled"] == norm) return true;
        st->values["enabled"] = norm;
        persist(*st);
        queue_change(st->mod, "enabled");
        return true;
    }
    const SettingDef* d = def_of(st, key);
    if (!d) return *why = std::string("no setting ") + key, false;
    if (!normalize(*d, value ? value : "", false, &norm, why)) return false;
    if (st->values[d->key] == norm) return true;
    st->values[d->key] = norm;
    persist(*st);
    queue_change(st->mod, d->key);
    return true;
}

bool api_setting_set(const char* mod, const char* key, const char* value) {
    Mod* caller = caller_mod(_ReturnAddress());
    std::string why;
    bool ok;
    {
        std::lock_guard lock(g_mutex);
        ok = key && set_locked(mod, key, value ? value : "", &why);
    }
    if (!ok) refuse(caller, "setting_set", format("%s.%s = %s: %s", mod ? mod : "?", key ? key : "?", value ? value : "", why.c_str()).c_str());
    return ok;
}

void api_on_setting_changed(const char* mod, EmfSettingCallback cb, void* user) {
    if (!cb) return;
    auto w = std::make_unique<Watch>();
    w->mod = mod_of_address(reinterpret_cast<const void*>(cb));
    if (!w->mod) w->mod = t_ctx.mod;
    w->filter = mod ? lower(mod) : "";
    w->cb = cb;
    w->user = user;
    std::lock_guard lock(g_mutex);
    g_watches.push_back(std::move(w));
}

struct ChangeCall {
    EmfSettingCallback cb;
    EmfWorld* w;
    const char* mod;
    const char* key;
    void* user;
};
void change_thunk(void* p) {
    auto* c = static_cast<ChangeCall*>(p);
    c->cb(c->w, c->mod, c->key, c->user);
}

}  // namespace

// ---------------------------------------------------------------------------------------------- plugin side ----
void settings_load_mod(Mod* m) {
    std::lock_guard lock(g_mutex);
    Store& st = make_store(m->id);
    if (!m->settings_json.empty()) {
        try {
            const json arr = json::parse(m->settings_json);
            for (const auto& e : arr) {
                SettingDef d;
                std::string why;
                if (!parse_def(e, &d, &why)) {
                    mod_logf(m, KILN_LOG_WARN, "mod.json: %s (that setting is left out)", why.c_str());
                    continue;
                }
                declare(st, d, m);
            }
        } catch (...) {
        }
    }
    if (st.values.find("enabled") == st.values.end()) st.values["enabled"] = m->enabled_default ? "1" : "0";
    persist(st);
    settings_write_schema(m);
}

bool settings_enabled(const Mod* m, bool fallback) {
    std::lock_guard lock(g_mutex);
    Store* st = store_of(m->id);
    if (!st) return fallback;
    auto it = st->values.find("enabled");
    if (it == st->values.end()) return fallback;
    bool b;
    return parse_bool(it->second, &b) ? b : fallback;
}

// docs/kiln/launcher/contracts.md 5.5 (schema/config-schema.schema.json): <configDir>/.schema/emf/<id>.json, the
// schema of <configDir>/emf/<id>.ini without values. Paths are "<section>.<key>" (the section is the mod id). Kiln
// types: bool/int/float/enum/string; EMF's own type and its extras (choices, step, group, ...) ride along.
void settings_write_schema(const Mod* m) {
    // caller holds g_mutex
    const Store* st = store_of(m->id);
    if (!st) return;
    json entries = json::array();
    entries.push_back({{"path", m->id + ".enabled"},
                       {"type", "bool"},
                       {"comment", "Enabled: the mod's switch (applies at the next start)"},
                       {"default", m->enabled_default},
                       {"requiresRestart", true},
                       {"synced", false},
                       {"emfType", "bool"}});
    for (const auto& d : st->defs) {
        std::string kt = "string";
        if (d.type == "bool") kt = "bool";
        else if (d.type == "int" || d.type == "choice" || (d.type == "slider" && d.whole())) kt = "int";
        else if (d.type == "float" || d.type == "slider") kt = "float";
        else if (d.type == "select") kt = "enum";
        std::string comment = d.label;
        if (!d.help.empty()) comment += ": " + d.help;
        json e = {{"path", m->id + "." + d.key}, {"type", kt},   {"comment", comment}, {"requiresRestart", !d.live},
                  {"synced", false},             {"emfType", d.type}, {"label", d.label}};
        double num;
        if (d.type == "bool") e["default"] = d.def == "1";
        else if (kt == "int" && parse_num(d.def, &num)) e["default"] = static_cast<int64_t>(num);
        else if (kt == "float" && parse_num(d.def, &num)) e["default"] = num;
        else e["default"] = d.def;
        if (d.has_min) e["min"] = d.min;
        if (d.has_max) e["max"] = d.max;
        if (d.type == "choice") {  // stored as the index
            e["min"] = 0;
            e["max"] = static_cast<int64_t>(d.choices.size()) - 1;
            e["choices"] = d.choices;
        }
        if (d.has_step) e["step"] = d.step;
        if (!d.options.empty()) {
            json o = json::array(), labels = json::array();
            for (const auto& [v, l] : d.options) o.push_back(v), labels.push_back(l);
            e["options"] = o;
            e["optionLabels"] = labels;
        }
        if (d.type == "text") e["maxLength"] = d.max_length;
        if (!d.group.empty()) e["group"] = d.group;
        if (!d.help.empty()) e["help"] = d.help;
        if (!d.enabled_when.is_null()) e["enabledWhen"] = d.enabled_when;
        entries.push_back(e);
    }
    const auto now = std::chrono::system_clock::now();
    const std::time_t tt = std::chrono::system_clock::to_time_t(now);
    std::tm tm{};
    gmtime_s(&tm, &tt);
    char written[32];
    std::strftime(written, sizeof written, "%Y-%m-%dT%H:%M:%SZ", &tm);
    json doc = {{"format", 1},
                {"source", "emf"},
                {"file", "emf/" + m->id + ".ini"},
                {"valueFormat", "ini"},
                {"id", m->id},
                {"mod", m->id},
                {"modVersion", m->version},
                {"kind", m->side == "client" ? "client" : m->side == "server" ? "server" : "common"},
                {"name", m->name},
                {"sections", json::array({{{"path", m->id}, {"comment", m->name}, {"synced", false}}})},
                {"entries", entries}};
    const auto file = P.config / L".schema" / L"emf" / to_wide(m->id + ".json");
    std::string old;
    // "written" changes on every start: compare without it so an unchanged schema is not rewritten
    if (read_file(file, &old)) {
        try {
            json o = json::parse(old);
            o.erase("written");
            o.erase("kiln");
            if (o == doc) return;
        } catch (...) {
        }
    }
    doc["written"] = written;
    doc["kiln"] = kPluginId + std::string(" ") + kPluginVersion;
    if (!write_file_atomic(file, doc.dump(2) + "\n")) logf(KILN_LOG_WARN, "settings: cannot write %s", path_utf8(file).c_str());
}

std::string settings_summary(const Mod* m) {
    std::lock_guard lock(g_mutex);
    const Store* st = store_of(m->id);
    if (!st) return "no settings";
    std::string out;
    auto en = st->values.find("enabled");
    out += "enabled=" + (en == st->values.end() ? std::string("1") : en->second);
    for (const auto& d : st->defs) {
        auto v = st->values.find(d.key);
        out += ", " + d.key + "=" + (v == st->values.end() ? d.def : v->second);
    }
    return out;
}

bool settings_set_text(const char* mod, const char* key, const char* value, std::string* why) {
    std::lock_guard lock(g_mutex);
    return set_locked(mod, key, value, why);
}

void settings_after_step(EmfWorld* w, uint8_t) {
    std::vector<std::pair<std::string, std::string>> changes;
    std::vector<Watch*> watches;
    {
        std::lock_guard lock(g_mutex);
        if (g_changes.empty()) return;
        changes.swap(g_changes);
        for (const auto& up : g_watches)
            if (!up->dead.load()) watches.push_back(up.get());
    }
    for (const auto& [mod, key] : changes)
        for (Watch* wa : watches) {
            if (wa->dead.load() || (!wa->filter.empty() && wa->filter != mod)) continue;
            ChangeCall c{wa->cb, w, mod.c_str(), key.c_str(), wa->user};
            ContextScope scope(wa->mod, w, true);
            if (!guarded(wa->mod, "an on_setting_changed callback", &change_thunk, &c)) wa->dead = true;
        }
}

void fill_settings(EmfApi* t) {
    t->setting_declare = &api_setting_declare;
    t->setting_int = &api_setting_int;
    t->setting_float = &api_setting_float;
    t->setting_text = &api_setting_text;
    t->setting_set = &api_setting_set;
    t->on_setting_changed = &api_on_setting_changed;
}

}  // namespace kemf
