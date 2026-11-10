// SPDX-License-Identifier: MIT
// Native mod loader (native_mods.h).
#include "native_mods.h"

#include <algorithm>
#include <atomic>
#include <deque>
#include <functional>
#include <map>
#include <mutex>
#include <regex>
#include <set>

#include "api.h"
#include "chain_status.h"
#include "config.h"
#include "crash.h"
#include "image.h"
#include "kiln_native.h"
#include "log.h"
#include "native_mods_semver.h"
#include "nlohmann/json.hpp"
#include "profile.h"
#include "registry.h"
#include "tick.h"

#include <psapi.h>  // after windows.h (common.h)

namespace kiln::native_mods {
namespace {

using nlohmann::json;
namespace fs = std::filesystem;

enum class St { Pending, Loaded, Failed, Disabled, Skipped, Policy, Unsupported };

const char* st_name(St s) {
    switch (s) {
        case St::Loaded: return "loaded";
        case St::Failed: return "failed";
        case St::Disabled: return "disabled";
        case St::Skipped: return "skipped";
        case St::Policy: return "policy";
        case St::Unsupported: return "unsupported";
        default: return "pending";
    }
}

const char* const kPhases[] = {"dllmain", "early", "boot", "world"};

struct Dll {
    std::string file;  // as in native.json (relative, '/')
    std::string status = "pending";
    int32_t error = 0;
    double ms = -1;
    HMODULE module = nullptr;
    std::wstring late_canon;  // phase dllmain, deferred to the late phase: the DLL's lower-case final path
};

struct Mod {
    std::string key;  // id, or folder:<name> when native.json is unusable
    std::string id, name, version, folder, folder_name, source;
    semver::Version parsed;
    uint32_t phase = KILN_NATIVE_PHASE_EARLY;
    std::string side = "client";
    std::string init;
    std::vector<std::string> after, before, needs;
    std::vector<std::pair<std::string, semver::Range>> incompatible;
    std::vector<Dll> dlls;
    St state = St::Pending;
    std::string reason;
    std::vector<std::string> notes;
    int load_index = -1;
    // phase dllmain, chain.ini [profile]: the proxy loaded at least one of its DLLs before kiln-core started
    bool proxy_loaded = false;
    // phase dllmain with a DLL chain.ini [late] deferred: why that DLL must not be loaded (the mod was not loadable when
    // the proxy's results were matched), else empty
    std::string late_block;
    // init call
    bool init_called = false, init_faulted = false;
    int init_result = 0;
    unsigned long init_fault = 0;
    double init_ms = 0;
    // KilnNativeContext and its strings (stable: g_mods is a deque)
    KilnOwner owner = 0;
    std::string cfg_dir, data_dir;
    KilnNativeContext ctx{};
};

std::deque<Mod> g_mods;
std::vector<Mod*> g_order[4];  // per phase, load order
std::recursive_mutex g_mutex;  // state writes (the world phase runs on a world thread)
bool g_verified = false;
bool g_hooks = false;
int g_next_index = 0;
const char* g_last_phase = nullptr;
std::atomic<bool> g_world_ran{false};
std::set<std::wstring> g_loaded_paths;  // canonical, lower-case: DLLs loaded by native mods

// chain.ini [late]: the proxy's KILN_CHAIN_LATE entries, in chain order (collected by discover()).
struct LateDll {
    LateRow row;
    std::wstring canon;  // lower-case final path (matches Dll::late_canon)
    HMODULE module = nullptr;
    const Mod* owner = nullptr;  // the dllmain native mod whose DLL this is (set by resolve()), else null
};
std::vector<LateDll> g_late;
bool g_late_collected = false;
bool g_late_ran = false;

void fail(Mod& m, St st, std::string reason) {
    m.state = st;
    m.reason = std::move(reason);
}

std::string side_name() { return image().side == KILN_SIDE_CLIENT ? "client" : "server"; }

std::wstring lower(std::wstring s) {
    CharLowerBuffW(s.data(), static_cast<DWORD>(s.size()));
    return s;
}

// Final path of a file (symlinks, case, drive mappings resolved), lower-case; the input when it cannot be opened.
std::wstring final_path(const fs::path& p) {
    HANDLE h = CreateFileW(p.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                           FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) return lower(p.wstring());
    std::wstring buf(4096, L'\0');
    const DWORD n = GetFinalPathNameByHandleW(h, buf.data(), static_cast<DWORD>(buf.size()),
                                              FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    CloseHandle(h);
    if (n == 0 || n >= buf.size()) return lower(p.wstring());
    buf.resize(n);
    if (buf.rfind(L"\\\\?\\UNC\\", 0) == 0) buf = L"\\\\" + buf.substr(8);
    else if (buf.rfind(L"\\\\?\\", 0) == 0) buf = buf.substr(4);
    return lower(buf);
}

// ---- SEH guards: no objects with destructors in these frames ----

HMODULE guarded_load(const wchar_t* path, DWORD* error) {
    crash::Capture cap{};
    unsigned long code = 0;
    __try {
        HMODULE h = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        *error = h ? 0 : GetLastError();
        return h;
    } __except (crash::filter(GetExceptionInformation(), &cap, &code)) {
        crash::report(cap, "native mod DLL load", nullptr);
        *error = code;
        return nullptr;
    }
}

bool guarded_init(KilnNativeInitFn fn, const KilnNativeContext* ctx, int* result, unsigned long* code) {
    crash::Capture cap{};
    __try {
        *result = fn(ctx);
        return true;
    } __except (crash::filter(GetExceptionInformation(), &cap, code)) {
        crash::report(cap, "native mod init", reinterpret_cast<const void*>(fn));
        return false;
    }
}

// ---- native.json ----

struct Bad {
    std::string what;
};

std::vector<std::string> id_list(const json& o, const char* key) {
    std::vector<std::string> out;
    if (!o.contains(key) || o[key].is_null()) return out;
    if (!o[key].is_array()) throw Bad{std::string("\"") + key + "\" must be a list of mod ids"};
    static const std::regex kModId("^[a-z0-9][a-z0-9._-]{0,63}$");
    for (const auto& v : o[key]) {
        if (!v.is_string() || !std::regex_match(v.get<std::string>(), kModId))
            throw Bad{std::string("\"") + key + "\" holds " + v.dump() + ", not a mod id"};
        out.push_back(v.get<std::string>());
    }
    return out;
}

// Reads <folder>/native.json into m (id, version, dlls, ...). Throws Bad.
void read_manifest(Mod& m, const fs::path& folder) {
    std::string text;
    if (!read_file(folder / L"native.json", &text)) throw Bad{"native.json cannot be read"};
    json doc;
    try {
        doc = json::parse(jsonc_to_json(text));
    } catch (const std::exception& e) {
        throw Bad{std::string("native.json is not valid JSON: ") + e.what()};
    }
    if (!doc.is_object()) throw Bad{"native.json must hold one { ... } object"};
    if (doc.contains("format") && !(doc["format"].is_number_integer() && doc["format"].get<int64_t>() == 1))
        throw Bad{"\"format\" must be 1"};
    static const std::regex kModId("^[a-z0-9][a-z0-9._-]{0,63}$");
    if (!doc.contains("id") || !doc["id"].is_string() || !std::regex_match(doc["id"].get<std::string>(), kModId))
        throw Bad{"\"id\" must be a mod id ([a-z0-9][a-z0-9._-]{0,63})"};
    m.id = doc["id"].get<std::string>();
    m.key = m.id;
    if (!doc.contains("version") || !doc["version"].is_string() || !semver::parse(doc["version"].get<std::string>(), &m.parsed))
        throw Bad{"\"version\" must be a semantic version"};
    m.version = doc["version"].get<std::string>();
    m.name = doc.contains("name") && doc["name"].is_string() ? doc["name"].get<std::string>() : m.id;
    if (doc.contains("side")) {
        const std::string s = doc["side"].is_string() ? doc["side"].get<std::string>() : "";
        if (s != "server" && s != "client" && s != "both") throw Bad{"\"side\" must be server, client or both"};
        m.side = s;
    }
    if (!doc.contains("dlls") || !doc["dlls"].is_array() || doc["dlls"].empty())
        throw Bad{"\"dlls\" must list at least one DLL"};
    std::set<std::string> seen;
    for (const auto& d : doc["dlls"]) {
        const std::string f = d.is_string() ? d.get<std::string>() : "";
        const bool rel = !f.empty() && f.find_first_of("\\:*?\"<>|") == std::string::npos && f[0] != '/';
        bool dotdot = false;
        for (size_t s = 0, e; s <= f.size(); s = e + 1) {
            e = f.find('/', s);
            if (e == std::string::npos) e = f.size();
            dotdot |= f.compare(s, e - s, "..") == 0;
        }
        std::string lf = f;
        for (char& c : lf) c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
        if (!rel || dotdot || lf.size() < 5 || lf.compare(lf.size() - 4, 4, ".dll") != 0)
            throw Bad{"\"dlls\" entry " + d.dump() + " must be a relative path ('/' separators, no '..') to a .dll"};
        if (!seen.insert(lf).second) throw Bad{"\"dlls\" lists " + f + " twice"};
        m.dlls.push_back(Dll{f});
    }
    if (doc.contains("phase")) {
        const std::string p = doc["phase"].is_string() ? doc["phase"].get<std::string>() : "";
        uint32_t phase = 4;
        for (uint32_t i = 0; i < 4; ++i)
            if (p == kPhases[i]) phase = i;
        if (phase == 4) throw Bad{"\"phase\" must be early, boot, world or dllmain"};
        m.phase = phase;
    }
    if (doc.contains("init") && !doc["init"].is_null()) {
        static const std::regex kExport("^[A-Za-z_][A-Za-z0-9_@?$]*$");
        if (!doc["init"].is_string() || !std::regex_match(doc["init"].get<std::string>(), kExport))
            throw Bad{"\"init\" must be the name of an export"};
        m.init = doc["init"].get<std::string>();
        if (m.phase == KILN_NATIVE_PHASE_DLLMAIN) throw Bad{"\"init\" is not allowed with phase dllmain"};
    }
    m.after = id_list(doc, "after");
    m.before = id_list(doc, "before");
    m.needs = id_list(doc, "requires");
    if (doc.contains("incompatible") && !doc["incompatible"].is_null()) {
        const json& x = doc["incompatible"];
        auto add = [&](const std::string& id, const std::string& range) {
            semver::Range r;
            std::string error;
            if (!semver::Range::parse(range, &r, &error)) throw Bad{"\"incompatible\" " + id + ": " + error};
            m.incompatible.emplace_back(id, std::move(r));
        };
        if (x.is_array()) {
            for (const auto& id : id_list(doc, "incompatible")) add(id, "*");
        } else if (x.is_object()) {
            for (const auto& [id, range] : x.items()) add(id, range.is_string() ? range.get<std::string>() : "*");
        } else {
            throw Bad{"\"incompatible\" must be a list of ids or an object of id: range"};
        }
    }
    if (doc.contains("kiln") && doc["kiln"].is_string()) {
        semver::Range r;
        std::string error;
        semver::Version kiln;
        semver::parse(std::string(kVersion), &kiln);
        if (!semver::Range::parse(doc["kiln"].get<std::string>(), &r, &error)) throw Bad{"\"kiln\": " + error};
        if (!r.satisfies(kiln) && !r.satisfies(kiln.release()))  // a dev build counts as its release
            fail(m, St::Failed, "needs Kiln " + r.text() + "; this is Kiln " + std::string(kVersion));
    }
    if (doc.contains("builds") && doc["builds"].is_array() && !doc["builds"].empty()) {
        bool listed = false;
        for (const auto& b : doc["builds"]) listed |= b.is_string() && b.get<std::string>() == image().sha256;
        if (!listed) m.notes.push_back("untested build (the exe's sha256 is not in \"builds\")");
    }
    if (doc.contains("enabled") && doc["enabled"].is_boolean() && !doc["enabled"].get<bool>()) m.notes.push_back("#off");
    static const std::set<std::string> kKnown = {"$schema", "format", "id", "name", "version", "description", "authors",
                                                 "license", "url", "side", "clients", "dlls", "phase", "init", "after",
                                                 "before", "requires", "incompatible", "enabled", "kiln", "builds"};
    for (const auto& [k, v] : doc.items())
        if (!kKnown.count(k)) KLOG_W("native mod %s: unknown key '%s' in native.json (kept, ignored)", m.id.c_str(), k.c_str());
}

// <configDir>/native.json: {"format": 1, "mods": {"<id>": {"enabled": bool}}}
std::map<std::string, bool> read_user_state() {
    std::map<std::string, bool> out;
    const fs::path file = paths().config / L"native.json";
    std::string text;
    std::error_code ec;
    if (!fs::exists(file, ec)) return out;
    if (!read_file(file, &text)) {
        KLOG_W("native: %s cannot be read; native.json defaults apply", u8(file).c_str());
        return out;
    }
    try {
        const json doc = json::parse(jsonc_to_json(text));
        if (doc.contains("mods") && doc["mods"].is_object())
            for (const auto& [id, v] : doc["mods"].items())
                if (v.is_object() && v.contains("enabled") && v["enabled"].is_boolean()) out[id] = v["enabled"].get<bool>();
    } catch (const std::exception& e) {
        KLOG_W("native: %s is not valid JSON (%s); native.json defaults apply", u8(file).c_str(), e.what());
    }
    return out;
}

void add_candidate(const fs::path& folder, const std::string& source, const profile::Entry* entry,
                   const std::map<std::string, bool>& user) {
    Mod& m = g_mods.emplace_back();
    m.folder = u8(folder);
    m.source = source;
    m.folder_name = entry ? entry->id : u8(folder.filename());
    m.key = "folder:" + m.folder_name;
    try {
        read_manifest(m, folder);
    } catch (const Bad& b) {
        if (!m.id.empty()) m.key = m.id;
        fail(m, St::Failed, b.what);
        return;
    }
    const bool off_by_default = std::find(m.notes.begin(), m.notes.end(), "#off") != m.notes.end();
    m.notes.erase(std::remove(m.notes.begin(), m.notes.end(), "#off"), m.notes.end());
    if (m.state != St::Pending) return;
    if (entry && entry->id != m.id) {
        fail(m, St::Failed, "the profile lists it as '" + entry->id + "' but its native.json says id '" + m.id + "'");
        return;
    }
    std::error_code ec;
    const auto u = user.find(m.id);
    if (entry && !entry->enabled)
        fail(m, St::Disabled, entry->disabled_by.empty() ? "disabled in the profile" : entry->disabled_by);
    else if (u != user.end() && !u->second) fail(m, St::Disabled, "switched off in " + u8(paths().config / L"native.json"));
    else if (u == user.end() && off_by_default) fail(m, St::Disabled, "off by default (native.json \"enabled\": false)");
    else if (!entry && fs::exists(folder / L".disabled", ec)) fail(m, St::Disabled, "disabled by a .disabled file");
}

// ---- resolve: per-mod checks, requires/incompatible fixpoint, per-phase order (Kiln.Host Resolver.cs rules) ----

Mod* active_by_id(const std::string& id) {
    for (Mod& m : g_mods)
        if (m.id == id && m.state == St::Pending) return &m;
    return nullptr;
}

Mod* any_by_id(const std::string& id) {
    for (Mod& m : g_mods)
        if (m.id == id) return &m;
    return nullptr;
}

std::string check_deps(const Mod& m) {
    for (const auto& r : m.needs) {
        const Mod* p = active_by_id(r);
        if (!p) {
            if (const Mod* x = any_by_id(r); x && x->state != St::Pending && x->state != St::Loaded)
                return "required native mod " + r + " did not load (" + st_name(x->state) + ": " + x->reason + ")";
            if (const Mod* x = any_by_id(r); x && x->state == St::Loaded) continue;  // dllmain, already loaded
            return "missing required native mod " + r;
        }
        if (p->phase > m.phase)
            return "requires " + r + ", which loads in a later phase (" + kPhases[p->phase] + " after " + kPhases[m.phase] + ")";
        // A dllmain mod that is still pending here has a DLL kiln/chain.ini [late] defers: it loads after the early and
        // boot phases ran (run_late() is the last step of kiln_boot). Only a world-phase mod comes after it.
        if (p->phase == KILN_NATIVE_PHASE_DLLMAIN && (m.phase == KILN_NATIVE_PHASE_EARLY || m.phase == KILN_NATIVE_PHASE_BOOT))
            return "requires " + r + ", whose DLL kiln/chain.ini [late] loads after the plugins, later than this mod's " +
                   kPhases[m.phase] + " phase (take " + r + " out of [late], or make this mod phase world)";
    }
    for (const auto& [id, range] : m.incompatible) {
        const Mod* o = any_by_id(id);
        if (o && (o->state == St::Pending || o->state == St::Loaded) && range.satisfies(o->parsed))
            return "incompatible with " + o->id + " " + o->version + " (declared in its incompatible list)";
    }
    return {};
}

// Ties by id (contracts.md 5.4), then folder name (only duplicates share an id, and they never both load).
bool tie_less(const Mod* a, const Mod* b) {
    return a->id != b->id ? a->id < b->id : a->folder_name < b->folder_name;
}

// Kahn over one phase: before -> after edges from requires, after, before (same phase only). Returns leftovers.
std::vector<Mod*> order_phase(uint32_t phase, std::vector<Mod*>* out,
                              std::map<Mod*, std::set<Mod*>>* edges_out) {
    std::vector<Mod*> nodes;
    for (Mod& m : g_mods)
        if (m.state == St::Pending && m.phase == phase) nodes.push_back(&m);
    std::map<Mod*, std::set<Mod*>> edges;
    auto find = [&](const std::string& id) -> Mod* {
        for (Mod* n : nodes)
            if (n->id == id) return n;
        return nullptr;
    };
    for (Mod* n : nodes) edges[n];
    for (Mod* n : nodes) {
        for (const auto& r : n->needs)
            if (Mod* p = find(r)) edges[p].insert(n);
        for (const auto& a : n->after)
            if (Mod* p = find(a)) edges[p].insert(n);
        for (const auto& b : n->before)
            if (Mod* p = find(b)) edges[n].insert(p);
    }
    std::map<Mod*, int> indegree;
    for (Mod* n : nodes) indegree[n] = 0;
    for (auto& [from, to] : edges)
        for (Mod* t : to) ++indegree[t];
    std::vector<Mod*> ready;
    for (Mod* n : nodes)
        if (indegree[n] == 0) ready.push_back(n);
    out->clear();
    while (!ready.empty()) {
        auto it = std::min_element(ready.begin(), ready.end(), tie_less);
        Mod* next = *it;
        ready.erase(it);
        out->push_back(next);
        for (Mod* t : edges[next])
            if (--indegree[t] == 0) ready.push_back(t);
    }
    std::vector<Mod*> left;
    for (Mod* n : nodes)
        if (indegree[n] > 0) left.push_back(n);
    *edges_out = std::move(edges);
    return left;
}

// Cycles among the leftovers: strongly connected components with a cycle, each as a path in "must load after" order.
std::vector<std::vector<Mod*>> cycles(const std::vector<Mod*>& left, const std::map<Mod*, std::set<Mod*>>& edges) {
    std::set<Mod*> in(left.begin(), left.end());
    std::map<Mod*, std::vector<Mod*>> depends;  // depends[a] = mods that must load before a
    for (Mod* n : left) depends[n];
    for (const auto& [from, to] : edges)
        if (in.count(from))
            for (Mod* t : to)
                if (in.count(t)) depends[t].push_back(from);
    for (auto& [n, d] : depends) std::sort(d.begin(), d.end(), tie_less);
    // Tarjan
    std::map<Mod*, int> index, low;
    std::set<Mod*> on;
    std::vector<Mod*> stack;
    std::vector<std::vector<Mod*>> sccs;
    int counter = 0;
    std::function<void(Mod*)> visit = [&](Mod* v) {
        index[v] = low[v] = counter++;
        stack.push_back(v);
        on.insert(v);
        for (Mod* w : depends[v]) {
            if (!index.count(w)) {
                visit(w);
                low[v] = std::min(low[v], low[w]);
            } else if (on.count(w)) {
                low[v] = std::min(low[v], index[w]);
            }
        }
        if (low[v] != index[v]) return;
        std::vector<Mod*> scc;
        Mod* x;
        do {
            x = stack.back();
            stack.pop_back();
            on.erase(x);
            scc.push_back(x);
        } while (x != v);
        sccs.push_back(std::move(scc));
    };
    std::vector<Mod*> sorted = left;
    std::sort(sorted.begin(), sorted.end(), tie_less);
    for (Mod* v : sorted)
        if (!index.count(v)) visit(v);
    std::vector<std::vector<Mod*>> out;
    for (auto& scc : sccs) {
        std::sort(scc.begin(), scc.end(), tie_less);
        Mod* start = scc[0];
        const auto& d0 = depends[start];
        if (scc.size() == 1 && std::find(d0.begin(), d0.end(), start) == d0.end()) continue;
        // shortest cycle from start through the component (BFS)
        std::set<Mod*> within(scc.begin(), scc.end());
        std::map<Mod*, Mod*> parent;
        std::deque<Mod*> q;
        std::vector<Mod*> path;
        for (Mod* n : depends[start]) {
            if (!within.count(n)) continue;
            if (n == start) {
                path = {start};
                break;
            }
            if (parent.emplace(n, start).second) q.push_back(n);
        }
        while (path.empty() && !q.empty()) {
            Mod* cur = q.front();
            q.pop_front();
            for (Mod* n : depends[cur]) {
                if (!within.count(n)) continue;
                if (n == start) {
                    path.push_back(cur);
                    for (Mod* p = cur; parent[p] != start; p = parent[p]) path.push_back(parent[p]);
                    path.push_back(start);
                    std::reverse(path.begin(), path.end());
                    break;
                }
                if (parent.emplace(n, cur).second) q.push_back(n);
            }
        }
        out.push_back(path.empty() ? scc : path);
    }
    return out;
}

void resolve() {
    // 1. duplicates: the first (profile before drop-ins) keeps the id
    for (size_t i = 0; i < g_mods.size(); ++i) {
        Mod& m = g_mods[i];
        if (m.id.empty()) continue;
        for (size_t j = 0; j < i; ++j)
            if (g_mods[j].id == m.id) {
                if (g_mods[j].source == "profile" && m.source == "dropin")
                    fail(m, St::Skipped, "the profile lists " + m.id + " too (the profile wins over the drop-in folder)");
                else
                    fail(m, St::Failed, "duplicate native mod id '" + m.id + "' (also in " + g_mods[j].folder + "); only the first loads");
                m.key = m.id + "@" + m.folder_name;
                break;
            }
    }
    // 2. side and policy
    const std::string side = side_name();
    for (Mod& m : g_mods) {
        if (m.state != St::Pending) continue;
        const bool policy_refuses = config().native_policy == NativePolicy::Never ||
                                    (config().native_policy == NativePolicy::VerifiedOnly && !g_verified);
        if (m.side != "both" && m.side != side) fail(m, St::Skipped, "side '" + m.side + "': not loaded on a " + side);
        else if (m.phase == KILN_NATIVE_PHASE_DLLMAIN && policy_refuses)  // the proxy loaded it before kiln.json is read
            m.notes.push_back("phase dllmain: loaded by the proxy, before kiln.json native.policy (" +
                              std::string(native_policy_name(config().native_policy)) + ") applies");
        else if (config().native_policy == NativePolicy::Never)
            fail(m, St::Policy, "kiln.json native.policy is \"never\"");
        else if (config().native_policy == NativePolicy::VerifiedOnly && !g_verified)
            fail(m, St::Policy, "kiln.json native.policy is \"verified-only\" and this game build is not verified");
    }
    // 3. dllmain: what the proxy did (kiln/chain.ini [profile]). Every dllmain mod is matched, loadable or not: a DLL the
    // proxy loaded is running whatever the profile says, and a DLL it deferred (chain.ini [late]) must not be loaded
    // by run_late() when its mod is disabled, on the wrong side or refused.
    const KilnBootInfo& bi = boot_info();
    const bool policy_refuses = config().native_policy == NativePolicy::Never ||
                                (config().native_policy == NativePolicy::VerifiedOnly && !g_verified);
    for (Mod& m : g_mods) {
        if (m.phase != KILN_NATIVE_PHASE_DLLMAIN) continue;
        const bool pending = m.state == St::Pending;
        int found = 0, loaded = 0, late = 0;
        std::string bad;
        for (Dll& d : m.dlls) {
            std::string f = d.file;
            std::replace(f.begin(), f.end(), '/', '\\');
            const std::wstring c = final_path(fs::path(to_wide(m.folder)) / to_wide(f));
            for (uint32_t i = 0; i < bi.chain_count; ++i) {
                const KilnChainEntry& e = bi.chain[i];
                if (e.source != KILN_CHAIN_SOURCE_PROFILE || lower(to_wide(sv(e.path))) != c) continue;
                if (e.status == KILN_CHAIN_LATE) {  // chain.ini [late]: run_late() loads it and finishes the mod
                    ++found;
                    ++late;
                    d.late_canon = c;
                    for (LateDll& l : g_late)
                        if (l.row.chain_index == i) l.owner = &m;
                    break;
                }
                d.status = chain_status_name(e.status);
                d.error = e.error;
                d.module = static_cast<HMODULE>(e.module);
                ++found;
                if (e.status == KILN_CHAIN_LOADED || e.status == KILN_CHAIN_DUPLICATE) ++loaded;
                else if (bad.empty()) bad = d.file + ": " + d.status + (e.error ? " (error " + std::to_string(e.error) + ")" : "");
                break;
            }
        }
        m.proxy_loaded = loaded > 0;
        if (!pending) {
            if (late > 0) m.late_block = "native mod " + m.key + " is " + st_name(m.state) + (m.reason.empty() ? "" : " (" + m.reason + ")");
            if (m.state == St::Disabled && m.proxy_loaded) {
                // chain.ini was written before the profile changed: the proxy started the DLL, so the mod runs
                m.state = St::Loaded;
                m.reason.clear();
                m.load_index = g_next_index++;
                m.notes.push_back("the profile says it is disabled, but the proxy loaded its DLL before kiln-core started; "
                                  "activate the profile again in the launcher to keep it out");
            }
            continue;
        }
        if (found == 0)
            fail(m, St::Skipped, "phase dllmain: not in kiln/chain.ini [profile] (the launcher writes it when it activates the profile)");
        else if (!bad.empty()) fail(m, St::Failed, "the proxy did not load it: " + bad);
        else if (late > 0 && policy_refuses) {
            // [late] kept the proxy from loading it, so kiln.json can still decide
            m.notes.erase(std::remove_if(m.notes.begin(), m.notes.end(),
                                         [](const std::string& n) { return n.rfind("phase dllmain: loaded by the proxy", 0) == 0; }),
                          m.notes.end());
            fail(m, St::Policy, std::string(config().native_policy == NativePolicy::Never
                                                ? "kiln.json native.policy is \"never\""
                                                : "kiln.json native.policy is \"verified-only\" and this game build is not verified") +
                                    " (chain.ini [late] deferred its DLL, so the policy applies to it)");
            m.late_block = "native mod " + m.key + " is policy (" + m.reason + ")";
        } else if (late > 0 && loaded + late == static_cast<int>(m.dlls.size())) {
            m.reason = "loads late: chain.ini [late] puts its DLL after the plugins";  // stays Pending until run_late()
            m.notes.push_back("phase dllmain, deferred by chain.ini [late]");
        } else if (loaded < static_cast<int>(m.dlls.size())) fail(m, St::Failed, "the proxy loaded only some of its DLLs");
        else {
            m.state = St::Loaded;
            m.load_index = g_next_index++;
        }
    }
    // 4. requires / incompatible to a fixpoint, then order per phase; cycles fail together, then again
    for (;;) {
        for (bool changed = true; changed;) {
            changed = false;
            std::vector<Mod*> active;
            for (Mod& m : g_mods)
                if (m.state == St::Pending) active.push_back(&m);
            std::sort(active.begin(), active.end(), [](const Mod* a, const Mod* b) { return a->id < b->id; });
            for (Mod* m : active) {
                std::string why = check_deps(*m);
                if (why.empty()) continue;
                fail(*m, St::Failed, why);
                changed = true;
            }
        }
        bool any_left = false;
        for (uint32_t phase = KILN_NATIVE_PHASE_EARLY; phase <= KILN_NATIVE_PHASE_WORLD; ++phase) {
            std::map<Mod*, std::set<Mod*>> edges;
            const auto left = order_phase(phase, &g_order[phase], &edges);
            if (left.empty()) continue;
            any_left = true;
            auto found = cycles(left, edges);
            if (found.empty()) found = {left};
            for (const auto& c : found) {
                std::string text;
                for (const Mod* x : c) text += x->id + " -> ";
                text += c[0]->id;
                for (Mod* x : c)
                    if (x->state == St::Pending) fail(*x, St::Failed, "load-order cycle: " + text + " (each must load after the next)");
            }
        }
        if (!any_left) break;
    }
}

// ---- state/native.json ----

void write_state() {
    std::lock_guard lock(g_mutex);
    json mods = json::array();
    for (const Mod& m : g_mods) {
        json dlls = json::array();
        for (const Dll& d : m.dlls) {
            if (d.status == "pending") continue;  // not tried (yet)
            json o = {{"file", d.file}, {"status", d.status}, {"error", d.error}};
            if (d.ms >= 0) o["ms"] = d.ms;
            dlls.push_back(o);
        }
        json init = nullptr;
        if (!m.init.empty() && m.init_called) {
            init = {{"export", m.init}, {"result", m.init_faulted ? json(nullptr) : json(m.init_result)}, {"ms", m.init_ms}};
            char code[16];
            std::snprintf(code, sizeof code, "0x%08lx", m.init_fault);
            init["fault"] = m.init_faulted ? json(code) : json(nullptr);
        }
        mods.push_back({{"id", m.id.empty() ? m.key : m.id},
                        {"version", m.version.empty() ? json(nullptr) : json(m.version)},
                        {"name", m.name.empty() ? json(nullptr) : json(m.name)},
                        {"folder", m.folder},
                        {"source", m.source},
                        {"phase", kPhases[m.phase]},
                        {"state", st_name(m.state)},
                        {"reason", m.reason.empty() ? json(nullptr) : json(m.reason)},
                        {"notes", m.notes},
                        {"loadIndex", m.load_index >= 0 ? json(m.load_index) : json(nullptr)},
                        {"dlls", dlls},
                        {"init", init}});
    }
    json doc = {{"format", 1},
                {"pid", GetCurrentProcessId()},
                {"written", utc_now()},
                {"kiln", std::string(kVersion)},
                {"side", side_name()},
                {"policy", native_policy_name(config().native_policy)},
                {"hooks", g_hooks},
                {"mods", mods}};
    if (g_last_phase) doc["phase"] = g_last_phase;
    if (!g_late.empty()) {  // chain.ini [late]: what the core did with the DLLs the proxy left to it
        json late = json::array();
        for (const LateDll& l : g_late) {
            json o = {{"path", l.row.path}, {"source", l.row.source}, {"status", l.row.status}, {"error", l.row.error},
                      {"order", l.row.order}};
            if (l.row.ms >= 0) o["ms"] = l.row.ms;
            if (!l.row.note.empty()) o["note"] = l.row.note;
            late.push_back(std::move(o));
        }
        doc["chain"] = {{"late", std::move(late)}};
    }
    if (!write_file_atomic(paths().state / L"native.json", doc.dump(2) + "\n"))
        KLOG_W("native: cannot write %s", u8(paths().state / L"native.json").c_str());
}

// ---- loading ----

void load_mod(Mod& m, uint32_t phase) {
    for (const auto& r : m.needs) {
        const Mod* p = any_by_id(r);
        if (!p || p->state != St::Loaded) {
            fail(m, St::Failed, "required native mod " + r + " failed to load" + (p && !p->reason.empty() ? " (" + p->reason + ")" : ""));
            KLOG_E("native mod %s %s not loaded: %s", m.id.c_str(), m.version.c_str(), m.reason.c_str());
            return;
        }
    }
    std::error_code ec;
    const fs::path folder = to_wide(m.folder);
    const fs::path cfg = paths().config / L"native" / to_wide(m.id);
    const fs::path data = fs::path(to_wide(profile::state().data_dir)) / to_wide(m.id);
    fs::create_directories(cfg, ec);
    fs::create_directories(data, ec);
    m.cfg_dir = u8(cfg);
    m.data_dir = u8(data);
    const double t_mod = now_ms();
    for (size_t i = 0; i < m.dlls.size(); ++i) {
        Dll& d = m.dlls[i];
        std::string f = d.file;
        std::replace(f.begin(), f.end(), '/', '\\');
        const fs::path path = folder / to_wide(f);
        if (!fs::is_regular_file(path, ec)) {
            d.status = "missing";
            fail(m, St::Failed, d.file + " is missing");
            break;
        }
        const std::wstring canon = final_path(path);
        const bool again = g_loaded_paths.count(canon) != 0;
        const double t0 = now_ms();
        DWORD error = 0;
        HMODULE h = guarded_load(path.c_str(), &error);
        d.ms = now_ms() - t0;
        if (!h) {
            d.status = "failed";
            d.error = static_cast<int32_t>(error);
            fail(m, St::Failed, d.file + " failed to load (error " + std::to_string(error) +
                                    (error == ERROR_DLL_INIT_FAILED ? ": its DllMain failed" : "") + ")");
            break;
        }
        d.module = h;
        d.status = again ? "duplicate" : "loaded";
        g_loaded_paths.insert(canon);
        if (i != 0 || m.init.empty()) continue;
        // init: right after dlls[0] loaded
        auto fn = reinterpret_cast<KilnNativeInitFn>(GetProcAddress(h, m.init.c_str()));
        if (!fn) {
            fail(m, St::Failed, d.file + " has no export " + m.init + " (native.json \"init\")");
            break;
        }
        m.owner = reg::owner_create("native:" + m.id);
        KilnNativeContext& c = m.ctx;
        c.size = sizeof(KilnNativeContext);
        c.abi_version = KILN_NATIVE_ABI_VERSION;
        c.phase = phase;
        c.side = image().side;
        c.api = api();
        c.owner = m.owner;
        c.module = h;
        c.id = kstr(m.id);
        c.version = kstr(m.version);
        c.directory = kstr(m.folder);
        c.config_dir = kstr(m.cfg_dir);
        c.data_dir = kstr(m.data_dir);
        c.hooks_enabled = hooks_enabled() ? 1u : 0u;
        c.play_once = boot_info().play_once;
        const double ti = now_ms();
        m.init_called = true;
        const bool ok = guarded_init(fn, &c, &m.init_result, &m.init_fault);
        m.init_ms = now_ms() - ti;
        if (!ok) {
            m.init_faulted = true;
            reg::owner_revoke(m.owner);
            char code[16];
            std::snprintf(code, sizeof code, "0x%08lx", m.init_fault);
            fail(m, St::Failed, m.init + " faulted (" + code + "); its registrations were revoked");
            break;
        }
        if (m.init_result == KILN_NATIVE_INIT_UNSUPPORTED) {
            reg::owner_revoke(m.owner);
            fail(m, St::Unsupported, m.init + " says this game build or side is not supported");
            break;
        }
        if (m.init_result != KILN_NATIVE_INIT_OK) {
            reg::owner_revoke(m.owner);
            fail(m, St::Failed, m.init + " returned " + std::to_string(m.init_result) + "; its registrations were revoked");
            break;
        }
    }
    if (m.state == St::Pending) {
        m.state = St::Loaded;
        m.load_index = g_next_index++;
        KLOG_I("native mod %s %s loaded (%s, %zu DLL(s)%s, %.1f ms)%s", m.id.c_str(), m.version.c_str(), kPhases[phase],
               m.dlls.size(), m.init.empty() ? "" : (", " + m.init + " ok").c_str(), now_ms() - t_mod,
               m.notes.empty() ? "" : ("; " + m.notes[0]).c_str());
    } else if (m.state == St::Unsupported) {
        KLOG_W("native mod %s %s: %s", m.id.c_str(), m.version.c_str(), m.reason.c_str());
    } else {
        KLOG_E("native mod %s %s failed: %s", m.id.c_str(), m.version.c_str(), m.reason.c_str());
    }
}

void run_phase(uint32_t phase) {
    std::lock_guard lock(g_mutex);
    g_last_phase = kPhases[phase];
    int n = 0;
    for (Mod* m : g_order[phase]) {
        if (m->state != St::Pending) continue;
        load_mod(*m, phase);
        ++n;
    }
    if (n) KLOG_I("native: phase %s: %d mod(s) handled", kPhases[phase], n);
    write_state();
}

const char* const kChainSources[] = {"chain", "profile", "emf"};

// The module of the DLL `canon` (lower-case final path) when it is in the process already: loaded by another loader
// (EMF's loader starts the DLLs of dinput8-chain.ini itself), or by anything else before the late phase.
HMODULE module_in_process(const fs::path& path, const std::wstring& canon) {
    if (HMODULE h = GetModuleHandleW(path.c_str())) return h;
    // The same file under another spelling (a link, 8.3 names, another drive mapping): modules of that file name only.
    const size_t cut = canon.find_last_of(L'\\');
    const std::wstring base = cut == std::wstring::npos ? canon : canon.substr(cut + 1);
    HMODULE mods[2048];
    DWORD needed = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof mods, &needed)) return nullptr;
    const size_t n = std::min<size_t>(needed / sizeof(HMODULE), 2048);
    for (size_t i = 0; i < n; ++i) {
        wchar_t name[2048];
        const DWORD len = GetModuleFileNameW(mods[i], name, 2048);
        if (len == 0 || len >= 2048) continue;
        const std::wstring full(name, len);
        const size_t slash = full.find_last_of(L"\\/");
        if (lower(slash == std::wstring::npos ? full : full.substr(slash + 1)) != base) continue;
        if (final_path(fs::path(full)) == canon) return mods[i];
    }
    return nullptr;
}

// Why a late DLL of this native mod must not be loaded; empty when the mod is loadable.
std::string unloadable_why(const Mod& m) {
    if (!m.late_block.empty()) return m.late_block;
    if (m.state != St::Pending) return "native mod " + m.key + " is " + st_name(m.state) + (m.reason.empty() ? "" : " (" + m.reason + ")");
    return {};
}

// The proxy's KILN_CHAIN_LATE entries, in its list order.
void collect_late() {
    g_late_collected = true;
    g_late.clear();
    const KilnBootInfo& bi = boot_info();
    for (uint32_t i = 0; i < bi.chain_count; ++i) {
        const KilnChainEntry& e = bi.chain[i];
        if (e.status != KILN_CHAIN_LATE) continue;
        LateDll l;
        l.row.chain_index = i;
        l.row.path = std::string(sv(e.path));
        l.row.source = e.source < 3 ? kChainSources[e.source] : "chain";
        l.row.order = static_cast<int>(g_late.size()) + 1;
        l.canon = final_path(fs::path(to_wide(l.row.path)));
        g_late.push_back(std::move(l));
    }
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    const uint32_t kind = image().side == KILN_SIDE_CLIENT ? KILN_WORLD_CLIENT : KILN_WORLD_SERVER;
    if (ev->event != KILN_WORLD_STARTED || ev->kind != kind) return;
    if (g_world_ran.exchange(true)) return;
    run_phase(KILN_NATIVE_PHASE_WORLD);
}

}  // namespace

void discover(bool verified) {
    std::lock_guard lock(g_mutex);
    g_verified = verified;
    collect_late();
    const auto& prof = profile::state();
    const auto user = read_user_state();
    for (const auto& e : prof.mods[KILN_PROFILE_KIND_NATIVE]) add_candidate(to_wide(e.path), "profile", &e, user);
    if (profile::dropins(KILN_PROFILE_KIND_NATIVE)) {
        std::error_code ec;
        std::vector<fs::path> dirs;
        const fs::path root = paths().kiln / L"native";
        if (fs::is_directory(root, ec))
            for (auto& d : fs::directory_iterator(root, ec))
                if (d.is_directory(ec) && fs::exists(d.path() / L"native.json", ec)) dirs.push_back(d.path());
        std::sort(dirs.begin(), dirs.end());
        for (const auto& d : dirs) add_candidate(d, "dropin", nullptr, user);
    }
    resolve();
    int pending[4] = {};
    for (const Mod& m : g_mods) {
        if (m.state == St::Pending) ++pending[m.phase];
        else if (m.state == St::Failed) KLOG_E("native mod %s not loaded: %s", m.key.c_str(), m.reason.c_str());
        else if (m.state == St::Loaded) KLOG_I("native mod %s %s: loaded by the proxy (phase dllmain)", m.id.c_str(), m.version.c_str());
        else KLOG_I("native mod %s %s: %s", m.key.c_str(), st_name(m.state), m.reason.c_str());
    }
    if (!g_mods.empty())
        KLOG_I("native: %zu mod(s) found (policy %s): %d early, %d boot, %d world to load", g_mods.size(),
               native_policy_name(config().native_policy), pending[1], pending[2], pending[3]);
    g_last_phase = "dllmain";
    write_state();
}

void run_early() { run_phase(KILN_NATIVE_PHASE_EARLY); }

void run_late() {
    std::lock_guard lock(g_mutex);
    if (g_late_ran) return;
    g_late_ran = true;
    if (!g_late_collected) collect_late();  // safe idle before discover() (the game image cannot be parsed)
    if (g_late.empty()) return;
    KLOG_I("native: loading %zu chain DLL(s) named in kiln/chain.ini [late], after the plugins", g_late.size());
    for (LateDll& l : g_late) {
        const fs::path path = to_wide(l.row.path);
        std::error_code ec;
        // Its native mod is disabled, on the wrong side, refused by the policy or failed a check: the DLL stays out.
        if (const std::string why = l.owner ? unloadable_why(*l.owner) : std::string(); !why.empty()) {
            l.row.status = "skipped";
            l.row.note = why + "; chain.ini [late] does not load its DLL";
            KLOG_W("late: %s is not loaded: %s", l.row.path.c_str(), l.row.note.c_str());
            continue;
        }
        if (!fs::is_regular_file(path, ec)) {
            l.row.status = "missing";
            KLOG_W("late: %s is gone since the proxy checked it; not loaded", l.row.path.c_str());
            continue;
        }
        if (g_loaded_paths.count(l.canon)) {
            l.row.status = "duplicate";
            KLOG_I("late: %s was loaded already (as a native mod DLL)", l.row.path.c_str());
            continue;
        }
        if (HMODULE have = module_in_process(path, l.canon)) {
            // dinput8-chain.ini: EMF's loader (when chained) starts those DLLs itself, before the gate. [late] moved nothing.
            l.module = have;
            l.row.status = kLateAlreadyLoaded;
            l.row.note = "another loader started it before the late phase; chain.ini [late] had no effect";
            KLOG_W("late: %s was already loaded when the late phase ran: %s", l.row.path.c_str(), l.row.note.c_str());
            continue;
        }
        const double t0 = now_ms();
        DWORD error = 0;
        HMODULE h = guarded_load(path.c_str(), &error);
        l.row.ms = now_ms() - t0;
        if (!h) {
            l.row.status = "failed";
            l.row.error = static_cast<int32_t>(error);
            KLOG_W("late: %s failed to load (error %lu%s)", l.row.path.c_str(), error,
                   error == ERROR_DLL_INIT_FAILED ? ": its DllMain failed" : "");
            continue;
        }
        l.module = h;
        l.row.status = "loaded";
        g_loaded_paths.insert(l.canon);
        KLOG_I("late: %s loaded at %p (%.1f ms)", l.row.path.c_str(), static_cast<void*>(h), l.row.ms);
    }
    // A dllmain native mod whose DLL was late finishes here.
    for (Mod& m : g_mods) {
        if (m.state != St::Pending || m.phase != KILN_NATIVE_PHASE_DLLMAIN) continue;
        bool any = false;
        std::string bad;
        for (Dll& d : m.dlls) {
            if (d.late_canon.empty()) continue;
            any = true;
            const auto it = std::find_if(g_late.begin(), g_late.end(), [&](const LateDll& l) { return l.canon == d.late_canon; });
            if (it == g_late.end()) {
                d.status = "failed";
                if (bad.empty()) bad = d.file + ": not handed to the late phase";
                continue;
            }
            const bool already = it->row.status == kLateAlreadyLoaded;
            d.status = already ? "duplicate" : it->row.status;
            d.error = it->row.error;
            d.ms = it->row.ms;
            d.module = it->module;
            if (already) m.notes.push_back("chain.ini [late] had no effect on " + d.file + ": another loader started its DLL first");
            if (it->row.status != "loaded" && it->row.status != "duplicate" && !already && bad.empty())
                bad = d.file + ": " + it->row.status + (it->row.error ? " (error " + std::to_string(it->row.error) + ")" : "");
        }
        if (!any) continue;
        m.reason.clear();
        if (!bad.empty()) {
            fail(m, St::Failed, "the late phase did not load it: " + bad);
            KLOG_E("native mod %s %s failed: %s", m.id.c_str(), m.version.c_str(), m.reason.c_str());
        } else {
            m.state = St::Loaded;
            m.load_index = g_next_index++;
            KLOG_I("native mod %s %s loaded (phase dllmain, deferred to the late phase by chain.ini [late])", m.id.c_str(),
                   m.version.c_str());
        }
    }
    write_state();
}

bool loaded_by_proxy(const std::string& mod_id) {
    std::lock_guard lock(g_mutex);
    for (const Mod& m : g_mods)
        if (m.id == mod_id && m.proxy_loaded) return true;
    return false;
}

std::vector<LateRow> late_rows() {
    std::lock_guard lock(g_mutex);
    std::vector<LateRow> out;
    for (const LateDll& l : g_late) out.push_back(l.row);
    return out;
}

void run_boot() {
    g_hooks = hooks_enabled();
    run_phase(KILN_NATIVE_PHASE_BOOT);
}

void arm_world(bool hooks) {
    std::lock_guard lock(g_mutex);
    g_hooks = hooks;
    bool any = false;
    for (Mod* m : g_order[KILN_NATIVE_PHASE_WORLD]) any |= m->state == St::Pending;
    if (!any) return;
    if (!hooks) {
        for (Mod* m : g_order[KILN_NATIVE_PHASE_WORLD])
            if (m->state == St::Pending) fail(*m, St::Skipped, "phase world needs hooks (this start is in safe idle)");
        write_state();
        return;
    }
    KilnHandle h = 0;
    const KilnOwner owner = reg::owner_create("native-loader");
    if (tick::subscribe_world(owner, &on_world, nullptr, &h) != KILN_OK) {
        for (Mod* m : g_order[KILN_NATIVE_PHASE_WORLD])
            if (m->state == St::Pending) fail(*m, St::Skipped, "no world events on this build (phase world)");
        write_state();
        return;
    }
    KLOG_I("native: phase world armed (first %s world start)", image().side == KILN_SIDE_CLIENT ? "client" : "server");
}

Summary summary() {
    std::lock_guard lock(g_mutex);
    Summary s;
    for (const Mod& m : g_mods) {
        if (m.state == St::Loaded) ++s.loaded;
        else if (m.state == St::Failed) ++s.failed;
        else if (m.state == St::Pending) ++s.pending;
    }
    return s;
}

}  // namespace kiln::native_mods
