// SPDX-License-Identifier: MIT
// EMF mods: where they are (the legacy <game>/emf/mods folder and the active Kiln profile's "emf" entries), their
// mod.json, version ranges in "depends", the load order, and the load itself (LoadLibraryExW + emf_mod_load under a
// fault guard; never FreeLibrary). Rules follow EMF 0.3.3's docs/modding/reference.md ("Where mods live", "mod.json",
// "Version ranges in depends").
#include <algorithm>
#include <array>
#include <cstdio>
#include <map>
#include <set>

#include "emf_internal.h"
#include "v26_model.h"
#include "nlohmann/json.hpp"

namespace kemf {

std::vector<std::unique_ptr<Mod>> g_mods;
std::mutex g_mods_mutex;

namespace {

using json = nlohmann::json;

// ---------------------------------------------------------------------------------------------- versions -------
struct Version {
    std::array<int64_t, 3> v{0, 0, 0};
    int parts = 0;      // how many numbers were written (0..3)
    bool wild = false;  // an x / * took the place of the next part ("1.2.x", "1.*")
};

bool parse_version(std::string_view s, Version* out, bool allow_wild = false) {
    Version r;
    size_t i = 0;
    while (true) {
        size_t j = s.find('.', i);
        if (j == std::string_view::npos) j = s.size();
        const std::string_view part = s.substr(i, j - i);
        if (part.empty() || r.parts >= 3) return false;
        if (part == "x" || part == "X" || part == "*") {
            if (!allow_wild) return false;
            r.wild = true;
            break;  // whatever follows a wildcard is a wildcard too
        }
        int64_t n = 0;
        for (char c : part) {
            if (c < '0' || c > '9') return false;
            n = n * 10 + (c - '0');
            if (n > 1000000000) return false;
        }
        r.v[static_cast<size_t>(r.parts++)] = n;
        if (j == s.size()) break;
        i = j + 1;
    }
    if (r.parts == 0 && !r.wild) return false;
    *out = r;
    return true;
}

int cmp(const std::array<int64_t, 3>& a, const std::array<int64_t, 3>& b) {
    for (size_t i = 0; i < 3; ++i)
        if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
    return 0;
}

std::array<int64_t, 3> bump(const Version& v, int at) {  // the first version after the family of v's first `at` parts
    std::array<int64_t, 3> r{0, 0, 0};
    for (int i = 0; i < at && i < 3; ++i) r[static_cast<size_t>(i)] = v.v[static_cast<size_t>(i)];
    if (at <= 0) return {INT64_MAX, 0, 0};
    r[static_cast<size_t>(at - 1)] += 1;
    return r;
}

struct Interval {  // lo <= v < hi (lo_incl / hi_incl adjust)
    std::array<int64_t, 3> lo{0, 0, 0};
    bool lo_incl = true;
    std::array<int64_t, 3> hi{INT64_MAX, 0, 0};
    bool hi_incl = false;
    bool contains(const std::array<int64_t, 3>& v) const {
        const int a = cmp(v, lo), b = cmp(v, hi);
        return (a > 0 || (a == 0 && lo_incl)) && (b < 0 || (b == 0 && hi_incl));
    }
    void intersect(const Interval& o) {
        const int a = cmp(o.lo, lo);
        if (a > 0 || (a == 0 && !o.lo_incl)) {
            lo = o.lo;
            lo_incl = o.lo_incl;
        }
        const int b = cmp(o.hi, hi);
        if (b < 0 || (b == 0 && !o.hi_incl)) {
            hi = o.hi;
            hi_incl = o.hi_incl;
        }
    }
    bool empty() const {
        const int c = cmp(lo, hi);
        return c > 0 || (c == 0 && !(lo_incl && hi_incl));
    }
};

bool comparator(std::string_view t, Interval* out) {
    Interval r;
    if (t.empty() || t == "*" || t == "x" || t == "X") {
        *out = r;
        return true;
    }
    std::string_view op;
    for (std::string_view o : {">=", "<=", ">", "<", "=", "^", "~"})
        if (t.substr(0, o.size()) == o) {
            op = o;
            break;
        }
    Version v;
    if (!parse_version(t.substr(op.size()), &v, true)) return false;
    const int p = v.parts;  // numbers written before a wildcard / the end
    const bool full = p == 3 && !v.wild;
    if (op.empty() && (v.wild || v.parts == 0)) {  // x-range: "1.2.x" = 1.2.0 up to below 1.3.0
        r.lo = v.v;
        if (p > 0) r.hi = bump(v, p);
    } else if (op.empty() || op == "^") {  // a bare version is the same as ^ (EMF)
        r.lo = v.v;
        int at;
        if (v.v[0] > 0 || p == 1) at = 1;          // ^1.2.3 < 2.0.0, ^0 < 1.0.0
        else if (v.v[1] > 0 || p == 2) at = 2;     // ^0.2.3 < 0.3.0, ^0.0 < 0.1.0
        else at = 3;                               // ^0.0.3 < 0.0.4
        r.hi = bump(v, at);
    } else if (op == "~") {
        r.lo = v.v;
        r.hi = bump(v, p >= 2 ? 2 : 1);
    } else if (op == ">=") {
        r.lo = v.v;
    } else if (op == ">") {
        if (full) {
            r.lo = v.v;
            r.lo_incl = false;
        } else {
            r.lo = bump(v, p);
        }
    } else if (op == "<") {
        r.hi = v.v;
    } else if (op == "<=") {
        if (full) {
            r.hi = v.v;
            r.hi_incl = true;
        } else {
            r.hi = bump(v, p);
        }
    } else if (op == "=") {
        r.lo = v.v;
        r.hi = v.v;
        r.hi_incl = true;
    }
    *out = r;
    return true;
}

std::vector<std::string_view> split_any(std::string_view s, std::string_view seps) {
    std::vector<std::string_view> out;
    size_t i = 0;
    while (i < s.size()) {
        while (i < s.size() && seps.find(s[i]) != std::string_view::npos) ++i;
        size_t j = i;
        while (j < s.size() && seps.find(s[j]) == std::string_view::npos) ++j;
        if (j > i) out.push_back(s.substr(i, j - i));
        i = j;
    }
    return out;
}

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
    return s;
}

// A range as EMF reads it. False for a malformed range or one that matches no version.
bool parse_range(std::string_view range, std::vector<Interval>* out) {
    out->clear();
    range = trim(range);
    if (range.empty()) range = "*";
    size_t i = 0;
    while (true) {
        size_t j = range.find("||", i);
        const std::string_view alt = trim(range.substr(i, j == std::string_view::npos ? std::string_view::npos : j - i));
        Interval acc;
        const size_t dash = alt.find(" - ");
        if (dash != std::string_view::npos) {
            Version a, b;
            if (!parse_version(trim(alt.substr(0, dash)), &a, true) || !parse_version(trim(alt.substr(dash + 3)), &b, true))
                return false;
            acc.lo = a.v;
            if (b.parts == 3) {
                acc.hi = b.v;
                acc.hi_incl = true;
            } else {
                acc.hi = bump(b, b.parts);
            }
        } else {
            // ">= 1.0" with a space after the operator is accepted too
            std::string joined;
            for (std::string_view t : split_any(alt, ", ")) {
                if (!joined.empty() && (joined.back() == '=' || joined.back() == '>' || joined.back() == '<' ||
                                        joined.back() == '^' || joined.back() == '~'))
                    joined += std::string(t);
                else
                    joined += (joined.empty() ? "" : " ") + std::string(t);
            }
            for (std::string_view t : split_any(joined, " ")) {
                Interval c;
                if (!comparator(t, &c)) return false;
                acc.intersect(c);
            }
        }
        if (!acc.empty()) out->push_back(acc);
        if (j == std::string_view::npos) break;
        i = j + 2;
    }
    return !out->empty();
}

std::string vtext(const std::array<int64_t, 3>& v) {
    if (v[0] == INT64_MAX) return "any";
    return std::to_string(v[0]) + "." + std::to_string(v[1]) + "." + std::to_string(v[2]);
}

// "needs lua ^1.3 (1.3.0 or newer, below 2.0.0), found 1.2.0: too old (update lua)"
std::string version_problem(const Dependency& d, const Mod& dep) {
    std::vector<Interval> r;
    if (!parse_range(d.range, &r)) return "its range for " + d.id + " (\"" + d.range + "\") matches no version";
    Version v;
    if (!parse_version(dep.version, &v)) return d.id + " has no readable version";
    for (const auto& i : r)
        if (i.contains(v.v)) return {};
    const auto& first = r.front();
    const bool too_old = cmp(v.v, first.lo) < 0 || (cmp(v.v, first.lo) == 0 && !first.lo_incl);
    std::string what = "needs " + d.id + " " + d.range + " (" + vtext(first.lo) + (first.lo_incl ? " or newer" : " exclusive");
    if (first.hi[0] != INT64_MAX) what += std::string(first.hi_incl ? ", up to " : ", below ") + vtext(first.hi);
    what += "), found " + vtext(v.v) + (too_old ? ": too old (update " + d.id + ")" : ": too new (look for an update of this mod)");
    return what;
}

// ---------------------------------------------------------------------------------------------- manifests ------
std::string line_col(const std::string& text, size_t byte) {
    size_t line = 1, col = 1;
    for (size_t i = 0; i < byte && i < text.size(); ++i) {
        if (text[i] == '\n') {
            ++line;
            col = 1;
        } else {
            ++col;
        }
    }
    return "line " + std::to_string(line) + ", column " + std::to_string(col);
}

bool read_deps(const json& j, Mod* m, std::string* why) {
    auto add = [&](const std::string& id, const json& v) -> bool {
        Dependency d;
        d.id = lower(id);
        if (v.is_string()) {
            d.range = v.get<std::string>();
        } else if (v.is_object()) {
            if (v.contains("version") && v["version"].is_string()) d.range = v["version"].get<std::string>();
            if (v.contains("optional") && v["optional"].is_boolean()) d.optional = v["optional"].get<bool>();
        } else if (!v.is_null()) {
            *why = "\"depends\": the entry for " + id + " is neither a range nor an object";
            return false;
        }
        std::vector<Interval> r;
        if (!parse_range(d.range, &r)) {
            *why = "\"depends\": the range for " + id + " (\"" + d.range + "\") matches no version";
            return false;
        }
        m->deps.push_back(d);
        return true;
    };
    if (j.is_object()) {
        for (auto it = j.begin(); it != j.end(); ++it)
            if (!add(it.key(), it.value())) return false;
        return true;
    }
    if (j.is_array()) {
        for (const auto& e : j) {
            if (e.is_string()) {
                if (!add(e.get<std::string>(), json())) return false;
            } else if (e.is_object() && e.contains("id") && e["id"].is_string()) {
                if (!add(e["id"].get<std::string>(), e)) return false;
            } else {
                *why = "\"depends\": a list entry has no \"id\"";
                return false;
            }
        }
        return true;
    }
    *why = "\"depends\" is neither an object nor a list";
    return false;
}

// Fills m from <folder>/mod.json. False (m->reason set) for a broken manifest.
bool read_manifest(const std::filesystem::path& folder, Mod* m) {
    std::string text;
    const auto file = folder / L"mod.json";
    if (!read_file(file, &text)) {
        m->reason = "mod.json cannot be read";
        return false;
    }
    if (text.size() > 256 * 1024) {
        m->reason = "mod.json is larger than 256 KB";
        return false;
    }
    if (text.size() >= 2 && ((unsigned char)text[0] == 0xFF || (unsigned char)text[0] == 0xFE)) {
        m->reason = "mod.json is saved as UTF-16: save it as UTF-8";
        return false;
    }
    if (text.size() >= 3 && text.compare(0, 3, "\xEF\xBB\xBF") == 0) text.erase(0, 3);
    json j;
    try {
        j = json::parse(text, nullptr, true, true);
    } catch (const json::parse_error& e) {
        m->reason = "mod.json: " + line_col(text, e.byte ? e.byte - 1 : 0) + ": " + e.what();
        return false;
    }
    if (!j.is_object()) {
        m->reason = "mod.json is not an object";
        return false;
    }
    auto str = [&](const char* k) -> std::string {
        return j.contains(k) && j[k].is_string() ? j[k].get<std::string>() : std::string();
    };
    m->id = str("id");
    if (!valid_mod_id(m->id)) {
        m->reason = m->id.empty() ? "mod.json has no \"id\"" : "\"id\" \"" + m->id + "\" is not 1-48 of a-z 0-9 _ . -";
        if (m->id.empty()) m->id = lower(to_utf8(folder.filename().wstring()));
        return false;
    }
    m->version = str("version");
    Version v;
    if (!parse_version(m->version, &v)) {
        m->reason = "\"version\" must be \"1\", \"1.2\" or \"1.2.3\"";
        return false;
    }
    m->name = str("name").empty() ? m->id : str("name");
    m->description = str("description");
    m->side = lower(str("side"));
    if (j.contains("authors") && j["authors"].is_array())
        for (const auto& a : j["authors"])
            if (a.is_string()) m->authors.push_back(a.get<std::string>());
    if (j.contains("emf")) {
        if (!j["emf"].is_number_integer()) {
            m->reason = "\"emf\" must be a whole number (the lowest EMF API version the mod needs)";
            return false;
        }
        m->emf_min = v26::api_requirement(j["emf"].is_number_unsigned()
                                             ? j["emf"].get<uint64_t>()
                                             : static_cast<uint64_t>(std::max<int64_t>(1, j["emf"].get<int64_t>())));
    }
    if (j.contains("enabled") && j["enabled"].is_boolean()) m->enabled_default = j["enabled"].get<bool>();
    if (j.contains("depends") && j.contains("dependencies")) {
        m->reason = "mod.json has both \"depends\" and \"dependencies\"";
        return false;
    }
    std::string why;
    if (j.contains("depends") && !read_deps(j["depends"], m, &why)) {
        m->reason = why;
        return false;
    }
    if (j.contains("dependencies") && !read_deps(j["dependencies"], m, &why)) {
        m->reason = why;
        return false;
    }
    if (j.contains("settings") && !j["settings"].is_null()) {
        if (!j["settings"].is_array()) {
            m->reason = "\"settings\" is not a list";
            return false;
        }
        m->settings_json = j["settings"].dump();
    }
    m->main = str("main");
    const std::string dll = str("dll");
    if (m->main.empty()) {
        m->dll = folder / to_wide(dll.empty() ? m->id + ".dll" : dll);
        if (dll.find("..") != std::string::npos || dll.find(':') != std::string::npos) {
            m->reason = "\"dll\" must name a file inside the mod's folder";
            return false;
        }
    }
    return true;
}

void add_mod(std::unique_ptr<Mod> m) {
    for (const auto& o : g_mods)
        if (o->id == m->id) {
            logf(KILN_LOG_WARN, "emf mod %s: a second copy at %s is ignored (the first is %s)", m->id.c_str(),
                 path_utf8(m->folder).c_str(), path_utf8(o->folder).c_str());
            return;
        }
    g_mods.push_back(std::move(m));
}

void scan_folder_mod(const std::filesystem::path& folder, const char* origin, int profile_enabled) {
    auto m = std::make_unique<Mod>();
    m->folder = folder;
    m->origin = origin;
    m->source = to_utf8(folder.filename().wstring()) + "\\";
    const bool ok = read_manifest(folder, m.get());
    if (!ok) m->status = EMF_MOD_REJECTED;
    if (profile_enabled == 0) {
        m->enabled_default = false;
        m->enabled = false;
        if (ok) m->status = EMF_MOD_DISABLED;
        m->reason = m->reason.empty() ? "switched off in the profile" : m->reason;
    }
    add_mod(std::move(m));
}

void scan_legacy(const std::filesystem::path& dir) {
    std::error_code ec;
    if (!std::filesystem::is_directory(dir, ec)) return;
    std::vector<std::filesystem::path> entries;
    for (const auto& e : std::filesystem::directory_iterator(dir, ec)) entries.push_back(e.path());
    std::sort(entries.begin(), entries.end());
    for (const auto& p : entries) {
        if (std::filesystem::is_directory(p, ec)) {
            if (std::filesystem::exists(p / L"mod.json", ec)) scan_folder_mod(p, "legacy", -1);
            continue;
        }
        if (lower(to_utf8(p.extension().wstring())) != ".dll") continue;
        auto m = std::make_unique<Mod>();
        m->loose = true;
        m->folder = dir;
        m->dll = p;
        m->origin = "legacy";
        m->id = lower(to_utf8(p.stem().wstring()));
        m->name = to_utf8(p.stem().wstring());
        m->version = "0.0.0";
        m->source = to_utf8(p.filename().wstring());
        if (!valid_mod_id(m->id)) {
            m->status = EMF_MOD_REJECTED;
            m->reason = "the file name is not a valid mod id (1-48 of a-z 0-9 _ . -)";
        }
        add_mod(std::move(m));
    }
}

std::filesystem::path from_windows(std::string_view p) { return std::filesystem::path(to_wide(p)); }

}  // namespace

bool valid_mod_id(std::string_view id) {
    if (id.empty() || id.size() > 48) return false;
    for (char c : id)
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-')) return false;
    return true;
}

Mod* find_mod(std::string_view id) {
    for (const auto& m : g_mods)
        if (iequals(m->id, id)) return m.get();
    return nullptr;
}

Mod* mod_of_address(const void* address) {
    HMODULE h = nullptr;
    if (!address || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                        static_cast<LPCWSTR>(address), &h))
        return nullptr;
    for (const auto& m : g_mods)
        if (m->module == h) return m.get();
    return nullptr;
}

Mod* caller_mod(const void* return_address) {
    if (Mod* m = mod_of_address(return_address)) return m;
    return t_ctx.mod;
}

const char* mod_name(const Mod* m) { return m ? m->id.c_str() : "?"; }

void discover_mods() {
    std::lock_guard lock(g_mods_mutex);
    bool legacy = true;
    if (S.profile) {
        const uint32_t mode = S.profile->mode();
        const char* names[] = {"legacy", "active", "play-once", "no-mods", "invalid"};
        logf(KILN_LOG_INFO, "profile: mode %s, %u emf entr%s, drop-ins %s", mode < 5 ? names[mode] : "?",
             S.profile->count(KILN_PROFILE_KIND_EMF), S.profile->count(KILN_PROFILE_KIND_EMF) == 1 ? "y" : "ies",
             (S.profile->dropins() & (1u << KILN_PROFILE_KIND_EMF)) ? "on" : "off");
        if (mode == KILN_PROFILE_NO_MODS || mode == KILN_PROFILE_INVALID) {
            logf(KILN_LOG_INFO, "profile mode %s: no EMF mods load", names[mode]);
            return;
        }
        if (mode == KILN_PROFILE_ACTIVE || mode == KILN_PROFILE_ONCE) {
            const uint32_t n = S.profile->count(KILN_PROFILE_KIND_EMF);
            for (uint32_t i = 0; i < n; ++i) {
                KilnProfileMod e{};
                e.size = sizeof e;
                if (S.profile->get(KILN_PROFILE_KIND_EMF, i, &e) != KILN_OK) continue;
                const auto folder = from_windows(sv(e.path));
                std::error_code ec;
                if (!std::filesystem::exists(folder / L"mod.json", ec)) {
                    auto m = std::make_unique<Mod>();
                    m->id = lower(sv(e.id));
                    if (m->id.empty()) m->id = lower(to_utf8(folder.filename().wstring()));
                    m->folder = folder;
                    m->origin = "profile";
                    m->status = EMF_MOD_REJECTED;
                    m->reason = "the profile names " + std::string(sv(e.path)) + ", which has no mod.json";
                    add_mod(std::move(m));
                    continue;
                }
                scan_folder_mod(folder, "profile", e.enabled ? 1 : 0);
            }
            legacy = (S.profile->dropins() & (1u << KILN_PROFILE_KIND_EMF)) != 0;
        }
    }
    if (legacy) scan_legacy(P.emf / L"mods");
}

namespace {

// Decides every mod's status before loading (switches, API version, side, dependencies) and returns the load order.
std::vector<Mod*> resolve_order() {
    std::map<std::string, Mod*> by_id;
    for (const auto& m : g_mods) by_id[m->id] = m.get();
    for (const auto& up : g_mods) {
        Mod* m = up.get();
        if (m->status != EMF_MOD_PENDING) continue;
        if (!m->enabled) {
            m->status = EMF_MOD_DISABLED;
            if (m->reason.empty()) m->reason = "switched off (enabled=0)";
            continue;
        }
        if (m->emf_min > kEmfApiVersion) {
            m->status = EMF_MOD_REJECTED;
            m->reason = "needs EMF API v" + std::to_string(m->emf_min) + "; kiln-emf provides v" +
                        std::to_string(kEmfApiVersion);
            continue;
        }
        if (g_side == KILN_SIDE_SERVER && m->side == "client") {
            m->status = EMF_MOD_REJECTED;
            m->reason = "a client mod (side \"client\": HUD lines, keys, notes): it does nothing on a dedicated server";
            continue;
        }
        if (!m->main.empty()) {
            m->status = EMF_MOD_SCRIPT;
            continue;
        }
        std::error_code ec;
        if (!std::filesystem::exists(m->dll, ec)) {
            m->status = EMF_MOD_REJECTED;
            m->reason = "its DLL " + to_utf8(m->dll.filename().wstring()) + " is missing";
        }
    }
    // dependency checks until nothing changes (a rejection can reject the mods that need it)
    bool changed = true;
    while (changed) {
        changed = false;
        for (const auto& up : g_mods) {
            Mod* m = up.get();
            if (m->status != EMF_MOD_PENDING && m->status != EMF_MOD_SCRIPT) continue;
            for (const auto& d : m->deps) {
                auto it = by_id.find(d.id);
                Mod* dep = it == by_id.end() ? nullptr : it->second;
                std::string why;
                const bool usable =
                    dep && (dep->status == EMF_MOD_PENDING || dep->status == EMF_MOD_SCRIPT || dep->status == EMF_MOD_LOADED);
                if (!dep) {
                    if (!d.optional) why = "needs " + d.id + ", which is not installed";
                } else if (!usable) {
                    if (!d.optional)
                        why = "needs " + d.id + ", which is " +
                              (dep->status == EMF_MOD_DISABLED ? "switched off" : "not loading (" + dep->reason + ")");
                } else {
                    why = version_problem(d, *dep);
                }
                if (!why.empty()) {
                    m->status = EMF_MOD_REJECTED;
                    m->reason = why;
                    changed = true;
                    break;
                }
            }
        }
    }
    // topological order by required + usable optional dependencies, ties by source name
    std::vector<Mod*> pending;
    for (const auto& up : g_mods)
        if (up->status == EMF_MOD_PENDING) pending.push_back(up.get());
    std::sort(pending.begin(), pending.end(), [](Mod* a, Mod* b) { return a->source < b->source; });
    std::vector<Mod*> order;
    std::set<Mod*> placed;
    auto needs = [&](Mod* m, bool required_only) {
        std::vector<Mod*> out;
        for (const auto& d : m->deps) {
            auto it = by_id.find(d.id);
            if (it == by_id.end() || it->second->status != EMF_MOD_PENDING) continue;
            if (required_only && d.optional) continue;
            out.push_back(it->second);
        }
        return out;
    };
    while (placed.size() < pending.size()) {
        bool progressed = false;
        for (Mod* m : pending) {
            if (placed.count(m)) continue;
            bool ready = true;
            for (Mod* d : needs(m, false))
                if (!placed.count(d)) ready = false;
            if (!ready) continue;
            order.push_back(m);
            placed.insert(m);
            progressed = true;
            break;  // restart so the order stays by name among the ready ones
        }
        if (progressed) continue;
        // stuck: optional-only cycles break by name; a mod in a required cycle is rejected
        for (Mod* m : pending) {
            if (placed.count(m)) continue;
            bool ready = true;
            for (Mod* d : needs(m, true))
                if (!placed.count(d)) ready = false;
            if (ready) {
                logf(KILN_LOG_INFO, "emf mods: %s and a mod it uses optionally use each other; %s goes first (by name)",
                     m->id.c_str(), m->id.c_str());
                order.push_back(m);
                placed.insert(m);
                progressed = true;
                break;
            }
        }
        if (progressed) continue;
        for (Mod* m : pending)
            if (!placed.count(m)) {
                m->status = EMF_MOD_REJECTED;
                m->reason = "a cycle of required dependencies";
                placed.insert(m);
            }
    }
    return order;
}

struct LoadCall {
    EmfModLoadFn fn;
    const EmfApi* api;
    bool ok;
};
void load_thunk(void* p) {
    auto* c = static_cast<LoadCall*>(p);
    c->ok = c->fn(c->api);
}

}  // namespace

void load_mods(const EmfApi* table) {
    std::vector<Mod*> order;
    {
        std::lock_guard lock(g_mods_mutex);
        for (const auto& up : g_mods) {
            up->owner = g_api->owner_create(kstr("emf:" + up->id));
            if (up->status == EMF_MOD_PENDING || up->status == EMF_MOD_DISABLED) {
                settings_load_mod(up.get());  // reads the switch: enabled=0 in the ini disables it
                if (up->status == EMF_MOD_PENDING) up->enabled = settings_enabled(up.get(), up->enabled_default);
            }
        }
        order = resolve_order();
    }
    portraits_on_ready();  // selected manifests only; the early startup asset writer has already finished
    for (Mod* m : order) {
        // a dependency that failed while loading rejects its dependents
        std::string why;
        for (const auto& d : m->deps) {
            Mod* dep = find_mod(d.id);
            if (dep && !d.optional && dep->status != EMF_MOD_LOADED && dep->status != EMF_MOD_SCRIPT)
                why = "needs " + d.id + ", which did not load (" + dep->reason + ")";
        }
        if (!why.empty()) {
            m->status = EMF_MOD_REJECTED;
            m->reason = why;
            mod_logf(m, KILN_LOG_WARN, "not loaded: %s", why.c_str());
            continue;
        }
        LARGE_INTEGER f, t0, t1;
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&t0);
        HMODULE h = LoadLibraryExW(m->dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!h) {
            m->status = EMF_MOD_FAILED;
            m->reason = "its DLL did not load (Windows error " + std::to_string(GetLastError()) + ")";
            mod_logf(m, KILN_LOG_ERROR, "%s %s: %s", m->id.c_str(), m->version.c_str(), m->reason.c_str());
            continue;
        }
        m->module = h;  // never FreeLibrary: it may have hooks, threads or callbacks registered
        auto fn = reinterpret_cast<EmfModLoadFn>(GetProcAddress(h, "emf_mod_load"));
        if (!fn) {
            m->status = EMF_MOD_FAILED;
            m->reason = "its DLL has no emf_mod_load export";
            mod_logf(m, KILN_LOG_ERROR, "%s %s: %s", m->id.c_str(), m->version.c_str(), m->reason.c_str());
            continue;
        }
        LoadCall call{fn, table, false};
        bool ran;
        {
            ContextScope scope(m, nullptr, false);
            ran = guarded(m, "emf_mod_load", &load_thunk, &call);
        }
        QueryPerformanceCounter(&t1);
        m->load_ms = static_cast<double>(t1.QuadPart - t0.QuadPart) * 1000.0 / static_cast<double>(f.QuadPart);
        if (!ran) {
            m->status = EMF_MOD_FAILED;
            m->reason = "emf_mod_load faulted";
        } else if (!call.ok) {
            m->status = EMF_MOD_FAILED;
            m->reason = "emf_mod_load returned false";
        } else {
            m->status = EMF_MOD_LOADED;
        }
        mod_logf(m, m->status == EMF_MOD_LOADED ? KILN_LOG_INFO : KILN_LOG_WARN, "%s %s (%s, %s): %s in %.1f ms%s%s",
                 m->id.c_str(), m->version.c_str(), m->source.c_str(), m->origin.c_str(),
                 m->status == EMF_MOD_LOADED ? "loaded" : "FAILED", m->load_ms, m->reason.empty() ? "" : ": ",
                 m->reason.c_str());
    }
    for (const auto& up : g_mods) {
        Mod* m = up.get();
        if (m->status == EMF_MOD_DISABLED)
            mod_logf(m, KILN_LOG_INFO, "%s %s: switched off (%s)", m->id.c_str(), m->version.c_str(), m->reason.c_str());
        else if (m->status == EMF_MOD_REJECTED)
            mod_logf(m, KILN_LOG_WARN, "%s %s: not loaded: %s", m->id.c_str(), m->version.c_str(), m->reason.c_str());
        else if (m->status == EMF_MOD_SCRIPT)
            mod_logf(m, KILN_LOG_INFO, "%s %s: a script mod (%s), run by its script host", m->id.c_str(), m->version.c_str(),
                     m->main.c_str());
    }
}

}  // namespace kemf
