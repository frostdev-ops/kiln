// SPDX-License-Identifier: MIT
// kiln.playeranim (native/core/abi/services/kiln_playeranim.h): every enabled mod's player states merged into the player's
// animation graph once, when the game's resource service comes up, and served through kiln.assets as an override for the
// life of the process. The merge is merge.cpp (portable, compared with the Python reference by tests/run.sh); this file
// reads the mods, the game's type layouts (the table kiln.types found, records read from the image) and resources (kiln.assets),
// puts the result, logs it, and answers the lookup.
//
// The graph is put once and never again: an override of the player's graph put while a player's graph instance is live
// kills the game. What it names stays too: a merged state's clip is a game clip or one a mod's assets.json serves, which
// kiln.assets registers at boot under an owner of its own per mod folder that nothing revokes (a mod's unload reverts only
// what its code registered, and code runs after this merge, so the merge never sees such a clip).
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "../types/scan.h"
#include "kiln_api.h"
#include "merge.h"
#include "services/kiln_assets.h"
#include "services/kiln_caps.h"
#include "services/kiln_playeranim.h"
#include "services/kiln_profile.h"
#include "services/kiln_types.h"

namespace kpa {
namespace {

namespace fs = std::filesystem;
using ojson = nlohmann::ordered_json;
using Clock = std::chrono::steady_clock;

constexpr const char* kZeroHash = "0000000000000000";

const KilnApi* g_api = nullptr;
KilnOwner g_self = 0;
const KilnAssetsApi* g_assets = nullptr;
const KilnTypesApi* g_types = nullptr;

std::atomic<int32_t> g_state{KILN_PA_OFF};
std::mutex g_mu;  // everything below

struct Config {
    bool enabled = true;
    std::string test_missing_field;  // lab only: the merge takes this field name as missing from the game's types
};
Config g_cfg;
std::string g_off_reason;
uint32_t g_off_code = KILN_CAP_REASON_FAULTED;  // the row's reason when off: policy, service-missing or faulted
std::string g_row_text;                        // the row's text when merged or untouched
const KilnCapsApi* g_caps = nullptr;           // kiln.caps (in-core, there from kiln_plugin_main on); null on an older core

// What the mods say.
std::vector<std::string> g_folders;            // every enabled mod folder, in the load order
std::vector<ModStates> g_mods;                 // the definitions read
std::map<std::string, std::string> g_dir_of;   // mod id -> folder, for the mods with definitions
std::vector<std::string> g_errors;             // definition files that could not be read
bool g_started = false;

// What came of it.
ojson g_report;
std::vector<State> g_states;  // by mod id, then name
std::string g_hash = kZeroHash;
const char* g_hash_kept = kZeroHash;
struct Times {
    double types = 0, facts = 0, merge = 0, put = 0, total = 0;
    MergeTimes split;
};
Times g_times;
std::vector<KilnPaState> g_kept;  // the states as the lookup gives them; their strings are never freed
std::vector<std::string> g_changes;  // mods with states that went or changed after the boot merge

// The game's graph, for the lookup (decoded on the first question).
Bytes g_game_raw;
std::unique_ptr<GameStates> g_game;
std::string g_game_why;  // why the game's graph cannot be read (permanent: a decode that failed)

// The game's type layouts: kiln.types' table, records read from the image as the host tests read them.
ktypes::Image g_im;
std::vector<ktypes::Range> g_readable;
std::unique_ptr<TypeDb> g_db;

// ---- small helpers ---------------------------------------------------------------------------------------------------

const char* keep(const std::string& s) {  // strings a caller keeps: never freed
    auto* p = new char[s.size() + 1];
    std::memcpy(p, s.c_str(), s.size() + 1);
    return p;
}
KilnStr keep_str(const std::string& s) { return KilnStr{keep(s), s.size()}; }

void logf(int level, const char* fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    if (n <= 0 || !g_api) return;
    g_api->log(g_self, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof(buf)) ? n : sizeof(buf) - 1)});
}
#define PA_I(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define PA_W(...) logf(KILN_LOG_WARN, __VA_ARGS__)
#define PA_E(...) logf(KILN_LOG_ERROR, __VA_ARGS__)

fs::path to_path(const std::string& utf8) { return fs::path(std::u8string(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size())); }
std::string to_utf8(const fs::path& p) {
    const std::u8string s = p.u8string();
    return std::string(reinterpret_cast<const char*>(s.data()), s.size());
}
std::string api_path(int32_t kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK || n >= sizeof buf) return "";
    return std::string(buf, n);
}
bool read_file(const fs::path& p, std::string* out) {
    std::ifstream f(p, std::ios::binary);
    if (!f) return false;
    out->assign(std::istreambuf_iterator<char>(f), {});
    return true;
}
double ms_between(Clock::time_point a, Clock::time_point b) { return std::chrono::duration<double, std::milli>(b - a).count(); }

bool write_atomic(const fs::path& path, const std::string& text) {
    std::error_code ec;
    fs::create_directories(path.parent_path(), ec);
    const fs::path tmp = fs::path(path).concat(L".tmp");
    {
        std::ofstream f(tmp, std::ios::binary);
        if (!f) return false;
        f.write(text.data(), static_cast<std::streamsize>(text.size()));
        if (!f) return false;
    }
    if (!MoveFileExW(tmp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        fs::remove(tmp, ec);
        return false;
    }
    return true;
}

// Every mod folder in the load order: the profile's, and kiln/mods when no profile says otherwise (kiln.assets' rule).
std::vector<std::string> mod_folders() {
    std::vector<std::string> out;
    const auto* prof = static_cast<const KilnProfileApi*>(g_api->service(KilnStr{"kiln.profile", 12}, 1));
    bool scan_dropins = true;
    if (prof && prof->header.size >= sizeof(KilnProfileApi)) {
        const uint32_t n = prof->count(KILN_PROFILE_KIND_KILN);
        for (uint32_t i = 0; i < n; ++i) {
            KilnProfileMod m{};
            m.size = sizeof m;
            if (prof->get(KILN_PROFILE_KIND_KILN, i, &m) == KILN_OK && m.enabled) out.emplace_back(m.path.ptr, m.path.len);
        }
        if (prof->mode() == KILN_PROFILE_ACTIVE || prof->mode() == KILN_PROFILE_ONCE) scan_dropins = (prof->dropins() & (1u << KILN_PROFILE_KIND_KILN)) != 0;
    }
    if (scan_dropins) {
        std::error_code ec;
        std::vector<std::string> found;
        for (auto it = fs::directory_iterator(to_path(api_path(KILN_PATH_MODS)), ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
            std::error_code e2;
            if (it->is_directory(e2) && !fs::exists(it->path() / L".disabled", e2)) found.push_back(to_utf8(it->path()));
        }
        std::sort(found.begin(), found.end());
        for (std::string& f : found)
            if (std::find(out.begin(), out.end(), f) == out.end()) out.push_back(std::move(f));
    }
    return out;
}

// ---- the game's image and type layouts ---------------------------------------------------------------------------------

bool live_readable(uint64_t va, uint64_t n) {
    auto it = std::upper_bound(g_readable.begin(), g_readable.end(), va, [](uint64_t a, const ktypes::Range& r) { return a < r.lo; });
    return it != g_readable.begin() && std::prev(it)->has(va, n);
}

// The exe as mapped: its committed, readable pages (a record pointing elsewhere reads as no record, never a fault).
bool live_image(std::string* why) {
    const auto base = reinterpret_cast<uint64_t>(GetModuleHandleW(nullptr));
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + static_cast<uint64_t>(dos->e_lfanew));
    const uint64_t size = nt->OptionalHeader.SizeOfImage;
    g_readable.clear();
    constexpr DWORD kRead = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    for (uint64_t at = base; at < base + size;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<const void*>(at), &mbi, sizeof mbi)) break;
        const uint64_t end = std::min(base + size, reinterpret_cast<uint64_t>(mbi.BaseAddress) + mbi.RegionSize);
        if (end <= at) break;
        if (mbi.State == MEM_COMMIT && (mbi.Protect & kRead) && !(mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) {
            if (!g_readable.empty() && g_readable.back().hi == at) g_readable.back().hi = end;
            else g_readable.push_back({at, end});
        }
        at = end;
    }
    g_im.mem = reinterpret_cast<const uint8_t*>(base);
    g_im.base = base;
    g_im.all = {base, base + size};
    g_im.readable = &live_readable;
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec) {
        const ktypes::Range r{base + sec->VirtualAddress, base + sec->VirtualAddress + sec->Misc.VirtualSize};
        if (!std::memcmp(sec->Name, ".text\0\0\0", 8)) g_im.text = r;
        if (!std::memcmp(sec->Name, ".rdata\0\0", 8)) g_im.rdata = r;
    }
    if (!g_im.valid()) {
        *why = "the game's exe has no readable .text and .rdata";
        return false;
    }
    return true;
}

// The type layouts: the table kiln.types found at boot (no second scan), each record read as the host tests read the exe.
// kiln.types missing turns the merge off: there is no other, untested way to the types.
bool build_types(std::string* why, uint32_t* code = nullptr) {
    if (g_db) return true;
    if (code) *code = KILN_CAP_REASON_FAULTED;
    if (!g_types || g_types->header.size < offsetof(KilnTypesApi, type_at) + sizeof(void*)) {
        *why = "kiln.types isn't there, so the game's type layouts are unknown";
        if (code) *code = KILN_CAP_REASON_SERVICE_MISSING;
        return false;
    }
    const uint32_t n = g_types->type_count();
    if (n == 0) {
        *why = "kiln.types found no type table on this game build";
        return false;
    }
    std::vector<uint64_t> entries(n);
    for (uint32_t i = 0; i < n; ++i) entries[i] = reinterpret_cast<uint64_t>(g_types->type_at(i));
    if (!live_image(why)) return false;
    auto db = std::make_unique<TypeDb>();
    if (!TypeDb::from_table(g_im, entries, db.get(), why)) return false;
    g_db = std::move(db);
    return true;
}

// ---- resources through kiln.assets --------------------------------------------------------------------------------------

KilnAssetKey key_of(const Guid& g, uint32_t type) {
    KilnAssetKey k{};
    std::memcpy(k.guid, g.b.data(), 16);
    k.type = type;
    k.part = 0;
    return k;
}
// An override if any, else the game's own. false: there is none (or the resource service is not up).
bool read_resource(const Guid& g, uint32_t type, Bytes* out) {
    const KilnAssetKey k = key_of(g, type);
    size_t n = 0;
    KilnResult rc = g_assets->resource_read(&k, nullptr, 0, &n);
    if (rc == KILN_OK) {
        out->clear();
        return true;
    }
    if (rc != KILN_ERR_BUFFER_TOO_SMALL) return false;
    out->resize(n);
    rc = g_assets->resource_read(&k, out->data(), out->size(), &n);
    return rc == KILN_OK && n == out->size();
}
bool has_resource(const Guid& g, uint32_t type) {
    const KilnAssetKey k = key_of(g, type);
    size_t n = 0;
    const KilnResult rc = g_assets->resource_read(&k, nullptr, 0, &n);
    return rc == KILN_OK || rc == KILN_ERR_BUFFER_TOO_SMALL;
}
Guid player_graph_guid() {
    Guid g;
    Guid::parse(kPlayerGraph, &g);
    return g;
}

// ---- status ---------------------------------------------------------------------------------------------------------------

const char* state_name(int32_t s) {
    switch (s) {
        case KILN_PA_WAITING: return "waiting";
        case KILN_PA_UNTOUCHED: return "untouched";
        case KILN_PA_MERGED: return "merged";
        default: return "off";
    }
}

// The row of capability anim.player_states, from the service's state: the one place that turns the state into the row.
// It is a static row (a manifest may require it), so it is never pending: while the service waits, nothing is decided and
// nothing is reported; the merge decides it before the mods load.
struct CapabilityRow {
    bool decided = false;
    uint32_t state = KILN_CAP_UNAVAILABLE, reason = KILN_CAP_REASON_FAULTED;
    std::string text;
};
CapabilityRow capability_row() {
    switch (g_state.load()) {
        case KILN_PA_MERGED:
        case KILN_PA_UNTOUCHED: return {true, KILN_CAP_AVAILABLE, KILN_CAP_REASON_NONE, g_row_text};
        case KILN_PA_WAITING: return {false, KILN_CAP_UNAVAILABLE, KILN_CAP_REASON_SESSION, "not decided yet: the game's resource service isn't up"};
        default: return {true, KILN_CAP_UNAVAILABLE, g_off_code, g_off_reason.empty() ? std::string("off") : g_off_reason};
    }
}
const char* reason_name(uint32_t r) {
    switch (r) {
        case KILN_CAP_REASON_NONE: return "none";
        case KILN_CAP_REASON_SERVICE_MISSING: return "service-missing";
        case KILN_CAP_REASON_POLICY: return "policy";
        case KILN_CAP_REASON_SESSION: return "session";
        default: return "faulted";
    }
}
// Beside each place the state is decided (kiln_plugin_main, on_ready, publish): the row to kiln.caps, once decided.
void report_row() {
    const CapabilityRow row = capability_row();
    if (!row.decided || !g_caps) return;
    const char* name = "anim.player_states";
    const KilnResult rc = g_caps->report(g_self, KilnStr{name, std::strlen(name)}, row.state, row.reason, KilnStr{row.text.c_str(), row.text.size()});
    if (rc != KILN_OK) PA_W("playeranim: kiln.caps refused the row %s (%d)", name, rc);
}

std::string status_text() {
    ojson o = ojson::object();
    o["state"] = state_name(g_state.load());
    if (!g_off_reason.empty()) o["reason"] = g_off_reason;
    o["hash"] = g_hash;
    const CapabilityRow cap = capability_row();
    o["capability"] = {{"name", "anim.player_states"},
                       {"state", !cap.decided ? "pending" : cap.state == KILN_CAP_AVAILABLE ? "available" : "unavailable"},
                       {"reason", reason_name(cap.reason)},
                       {"text", cap.text},
                       {"reported", cap.decided && g_caps != nullptr}};
    // the merge's lines, with the status the lookup gives (unavailable: taken by the merge being off)
    ojson states = g_report.contains("states") ? g_report["states"] : ojson::array();
    for (size_t i = 0; i < states.size() && i < g_kept.size(); ++i)
        states[i]["status"] = g_kept[i].status == KILN_PA_LIVE ? "live" : g_kept[i].status == KILN_PA_UNAVAILABLE ? "unavailable" : "refused";
    o["states"] = std::move(states);
    o["errors"] = g_errors;
    o["machines"] = g_report.contains("machines") ? g_report["machines"] : ojson::array();
    o["bytes"] = g_report.contains("bytes") ? g_report["bytes"] : ojson({{"before", nullptr}, {"after", nullptr}});
    o["ms"] = {{"total", g_times.total}, {"types", g_times.types}, {"clipFacts", g_times.facts}, {"merge", g_times.merge}, {"put", g_times.put},
               {"read", g_times.split.read}, {"accept", g_times.split.accept}, {"build", g_times.split.build}, {"validate", g_times.split.validate},
               {"encode", g_times.split.encode}};
    o["changed"] = g_changes;
    return o.dump(1, ' ', false, ojson::error_handler_t::replace);
}
void write_status() {
    if (!write_atomic(to_path(api_path(KILN_PATH_DATA)) / L"playeranim" / L"status.json", status_text())) PA_W("playeranim: can't write status.json");
}

// The lookup's view of the states, with strings that stay.
void keep_states() {
    g_kept.clear();
    for (const State& st : g_states) {
        KilnPaState s{};
        s.size = sizeof s;
        const bool live = st.status && *st.status == "live";
        // a state the merge being off took (the Python's "the merge is off: ..."), or one refused for its own reason
        const bool unavailable = !live && st.reason && st.reason->find(": the merge is off: ") != std::string::npos;
        s.status = live ? KILN_PA_LIVE : unavailable ? KILN_PA_UNAVAILABLE : KILN_PA_REFUSED;
        s.kind = st.kind && *st.kind == "overlay" ? KILN_PA_KIND_OVERLAY : KILN_PA_KIND_STATE;
        s.id = st.id;
        Guid g;
        if (Guid::parse(st.guid, &g)) std::memcpy(s.guid, g.b.data(), 16);
        s.mod = keep_str(st.mod);
        s.name = keep_str(st.name);
        s.like = keep_str(st.like ? *st.like : "");
        s.machine = keep_str(st.machine ? *st.machine : "");
        s.reason = keep_str(live || !st.reason ? std::string() : *st.reason);
        g_kept.push_back(s);
    }
}

// Turns the result off after the merge (kiln.assets refused the graph): every state the merge took is unavailable.
void take_back(const std::string& why) {
    g_off_reason = why;
    g_off_code = KILN_CAP_REASON_FAULTED;
    for (State& st : g_states)
        if (st.status && *st.status == "live") st.status = "refused", st.reason = "mod " + st.mod + ", state " + st.name + ": the merge is off: " + why;
    g_report["graph"] = "off";
    g_report["reason"] = why;
    g_report["graphHash"] = kZeroHash;
    ojson states = ojson::array();
    for (const State& st : g_states) states.push_back(st.entry());
    g_report["states"] = std::move(states);
}

// Sets the state from the report, logs one line (and one per refused state), writes the status.
void publish() {
    const std::string graph = g_report.value("graph", "off");
    if (graph == "merged") g_state.store(KILN_PA_MERGED);
    else if (graph == "untouched") g_state.store(KILN_PA_UNTOUCHED);
    else g_state.store(KILN_PA_OFF);
    if (graph == "off" && g_off_reason.empty()) g_off_reason = g_report["reason"].is_string() ? g_report["reason"].get<std::string>() : "off";
    g_hash = g_report.value("graphHash", std::string(kZeroHash));
    g_hash_kept = keep(g_hash);
    keep_states();
    for (const std::string& e : g_errors) PA_W("playeranim: %s", e.c_str());
    size_t live = 0, refused = 0;
    std::set<std::string> mods_live;
    for (const State& st : g_states) {
        if (st.status && *st.status == "live") {
            ++live;
            mods_live.insert(st.mod);
        } else if (graph != "off") {
            ++refused;
            PA_W("playeranim: %s", st.reason ? st.reason->c_str() : "refused");
        }
    }
    if (graph == "merged")
        g_row_text = std::to_string(live) + " player state(s) of " + std::to_string(mods_live.size()) + " mod(s) are in the player's graph";
    else if (graph == "untouched")
        g_row_text = "every player state was refused (" + std::to_string(refused) + "): the game's player graph is served; the refusals are in kiln.log";
    report_row();
    if (graph == "merged") {
        const ojson& b = g_report["bytes"];
        PA_I("playeranim: %zu state(s) of %zu mod(s) merged into the player's graph: %zu machine(s), %llu -> %llu bytes, %.0f ms (types %.1f, clip facts %.1f, merge %.1f, put "
             "%.1f), hash %s%s",
             live, mods_live.size(), g_report["machines"].size(), b["before"].get<unsigned long long>(), b["after"].get<unsigned long long>(), g_times.total,
             g_times.types, g_times.facts, g_times.merge, g_times.put, g_hash.c_str(), refused ? (", " + std::to_string(refused) + " refused").c_str() : "");
    } else if (graph == "untouched") {
        PA_I("playeranim: no player state could be merged (%zu refused): the game's graph is untouched", refused);
    } else {
        PA_E("playeranim: off: %s: the game's graph is untouched, %zu state(s) unavailable", g_off_reason.c_str(), g_states.size());
    }
    write_status();
}

void go_off(const std::string& why, uint32_t code = KILN_CAP_REASON_FAULTED) {
    g_off_reason = why;
    g_off_code = code;
    g_report = ojson::object();
    g_report["graph"] = "off";
    g_report["reason"] = why;
    g_report["graphHash"] = kZeroHash;
    g_states.clear();
    for (const ModStates& m : g_mods)
        for (State st : m.states) {
            if (!st.status) st.status = "refused", st.reason = "mod " + st.mod + ", state " + st.name + ": the merge is off: " + why;
            g_states.push_back(std::move(st));
        }
    std::stable_sort(g_states.begin(), g_states.end(), [](const State& a, const State& b) { return a.mod != b.mod ? a.mod < b.mod : a.name < b.name; });
    ojson states = ojson::array();
    for (const State& st : g_states) states.push_back(st.entry());
    g_report["states"] = std::move(states);
}

// ---- the merge, once ------------------------------------------------------------------------------------------------------

void merge_locked() {
    const Clock::time_point t0 = Clock::now();
    std::string why;
    uint32_t code = KILN_CAP_REASON_FAULTED;
    if (!build_types(&why, &code)) return go_off(why, code);
    const Clock::time_point t1 = Clock::now();
    g_times.types = ms_between(t0, t1);
    std::vector<ClipFactsProvider::Mod> fm;
    for (const std::string& dir : g_folders) {
        ClipFactsProvider::Mod m;
        m.assets_dir = dir + "\\assets";
        m.has_json = read_file(to_path(m.assets_dir) / L"assets.json", &m.assets_json);
        fm.push_back(std::move(m));
    }
    ClipFactsProvider::Game game;
    game.read = [](const Guid& g, uint32_t type, Bytes* out) { return read_resource(g, type, out); };
    game.has = [](const Guid& g, uint32_t type) { return has_resource(g, type); };
    ClipFactsProvider provider(*g_db, fm, [](const std::string& path, Bytes* out) {
        std::string s;
        if (!read_file(to_path(path), &s)) return false;
        out->assign(s.begin(), s.end());
        return true;
    }, game);
    for (const std::string& w : provider.warnings) PA_W("playeranim: %s: skipped for clip facts", w.c_str());
    double facts_ms = 0;
    const ClipFactsFn facts = [&](const std::string& guid) {
        const Clock::time_point a = Clock::now();
        std::optional<ClipFacts> f = provider(guid);
        facts_ms += ms_between(a, Clock::now());
        return f;
    };
    GraphSource src;
    src.read = [] {
        GraphRead r;
        if (read_resource(player_graph_guid(), fnv1a(kGraphType), &r.bytes)) {
            r.kind = GraphRead::Ok;
            g_game_raw = r.bytes;
        } else {
            r.kind = GraphRead::Error;
            r.error = std::string("kiln.assets has no ") + kGraphType + " " + kPlayerGraph;
        }
        return r;
    };
    MergeHooks hooks;
    hooks.missing_field = g_cfg.test_missing_field;
    MergeResult res = merge(src, *g_db, g_mods, facts, &hooks);
    const Clock::time_point t2 = Clock::now();
    g_times.facts = facts_ms;
    g_times.merge = ms_between(t1, t2) - facts_ms;
    g_times.split = res.times;
    g_report = std::move(res.report);
    g_states = std::move(res.states);
    if (res.graph) {
        const KilnAssetKey k = key_of(player_graph_guid(), fnv1a(kGraphType));
        const KilnResult rc = g_assets->resource_put(g_self, &k, res.graph->data(), res.graph->size(), KILN_ASSET_PUT_OVERRIDE);
        if (rc != KILN_OK) take_back("kiln.assets refused the merged graph (resource_put " + std::to_string(rc) + ")");
    }
    g_times.put = ms_between(t2, Clock::now());
    g_times.total = ms_between(t0, Clock::now());
}

void KILN_CALL on_resources_ready(void*) {
    std::lock_guard lock(g_mu);
    if (g_started) return;
    g_started = true;
    try {
        merge_locked();
    } catch (const std::exception& e) {
        go_off(std::string("the merge failed: ") + e.what());
    } catch (...) {
        go_off("the merge faulted");
    }
    publish();
}

// ---- mods that go or change after the merge ---------------------------------------------------------------------------

// kiln/state/mods.json (Kiln.Host rewrites it after every load, unload and reload): a mod with player states that goes,
// comes back or is reloaded gets one line (states.h: mods_changes). Its states stay in the graph until the next start. Looked
// at from a tick at most once a second; never touches the graph.
std::mutex g_watch_mu;                 // the look below; one tick thread at a time
std::atomic<int64_t> g_next_look{0};   // steady clock, ms
std::set<std::string> g_watched;       // mods with player states (set in on_ready, then read only)
fs::path g_mods_file;
fs::file_time_type g_mods_time{};
ModsLook g_mods_last;

void look_at_mods() {
    const int64_t now = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count();
    int64_t due = g_next_look.load();
    if (now < due || !g_next_look.compare_exchange_strong(due, now + 1000)) return;
    std::unique_lock look(g_watch_mu, std::try_to_lock);
    if (!look) return;
    std::error_code ec;
    const fs::file_time_type t = fs::last_write_time(g_mods_file, ec);
    if (ec || t == g_mods_time) return;
    g_mods_time = t;
    std::string text;
    if (!read_file(g_mods_file, &text)) return;
    ModsLook seen = read_mods_state(text, GetCurrentProcessId());
    if (!seen.ok) return;  // broken, or another process's: wait for the next write
    const std::vector<std::string> lines = mods_changes(g_mods_last, seen, g_watched);
    g_mods_last = std::move(seen);
    if (lines.empty()) return;
    std::lock_guard lock(g_mu);
    for (const std::string& line : lines) {
        PA_I("playeranim: %s", line.c_str());
        g_changes.push_back(line);
    }
    write_status();
}
void KILN_CALL on_tick(const KilnTickInfo*, void*) { look_at_mods(); }

// The server world's ticks, and on a client its own world's (a joined client has no server world).
void watch_mods() {
    g_mods_file = to_path(api_path(KILN_PATH_STATE)) / L"mods.json";
    for (const auto& [id, dir] : g_dir_of) g_watched.insert(id);
    KilnHandle h = 0;
    if (g_api->subscribe_tick(g_self, KILN_TICK_PRE, &on_tick, nullptr, &h) != KILN_OK) PA_W("playeranim: no server tick: mods that go or change are not reported");
    if (g_api->side() == KILN_SIDE_CLIENT && g_api->size >= offsetof(KilnApi, subscribe_world_tick) + sizeof(void*) &&
        g_api->subscribe_world_tick(g_self, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_tick, nullptr, &h) != KILN_OK)
        PA_W("playeranim: no client world tick: mods that go or change are not reported on this client");
}

// ---- the table -------------------------------------------------------------------------------------------------------------

int32_t KILN_CALL t_state() { return g_state.load(); }
KilnStr KILN_CALL t_hash() {
    std::lock_guard lock(g_mu);
    return KilnStr{g_hash_kept, std::strlen(g_hash_kept)};
}

// The game's own graph, decoded once: after the boot merge read it, or now from kiln.assets (none merged: the game's own).
// false: not before the resource service is up, or this build's graph or types cannot be read.
bool ensure_game(std::string* why) {
    if (g_game) return true;
    if (!g_game_why.empty()) {
        *why = g_game_why;
        return false;
    }
    if (!g_assets) {
        *why = "kiln.assets isn't there";
        return false;
    }
    if (!build_types(why)) return false;
    if (g_game_raw.empty() && !read_resource(player_graph_guid(), fnv1a(kGraphType), &g_game_raw)) {
        g_game_raw.clear();
        *why = "the game's resource service isn't up yet";
        return false;
    }
    try {
        g_game = std::make_unique<GameStates>(*g_db, g_game_raw);
    } catch (const PyError& e) {
        g_game_why = "the game's player graph does not decode: " + e.type + ": " + e.text;
        *why = g_game_why;
        return false;
    } catch (...) {
        g_game_why = "the game's player graph does not decode";
        *why = g_game_why;
        return false;
    }
    return true;
}

void fill(const GameState& s, KilnPaGameState* out) {
    out->kind = s.kind == "overlay" ? KILN_PA_KIND_OVERLAY : KILN_PA_KIND_STATE;
    out->id = s.ids.empty() ? 0 : s.ids[0];
    out->id_count = static_cast<uint32_t>(s.ids.size());
    out->machine = KilnStr{s.machine.c_str(), s.machine.size()};  // g_game lives for the process
    out->name = KilnStr{s.name.c_str(), s.name.size()};
}

// The index-th state with that name (under g_mu): KILN_OK, KILN_ERR_NOT_FOUND (none, or index past the end) or
// KILN_ERR_UNAVAILABLE; *matches: how many states have the name.
KilnResult find_game(KilnStr name, uint32_t index, const GameState** out, uint32_t* matches) {
    std::string why;
    if (!ensure_game(&why)) return KILN_ERR_UNAVAILABLE;
    const auto found = g_game->find(std::string_view(name.ptr, name.len));
    if (matches) *matches = static_cast<uint32_t>(found.size());
    if (index >= found.size()) return KILN_ERR_NOT_FOUND;
    *out = found[index];
    return KILN_OK;
}

KilnResult ids_of(const GameState& s, uint32_t* out, size_t capacity, size_t* out_count) {
    if (out_count) *out_count = s.ids.size();
    if (capacity < s.ids.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::copy(s.ids.begin(), s.ids.end(), out);
    return KILN_OK;
}

KilnResult KILN_CALL t_game_state(KilnStr name, KilnPaGameState* out, uint32_t* out_matches) {
    if (!name.ptr || !out || out->size < sizeof(KilnPaGameState)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const GameState* s = nullptr;
    const KilnResult rc = find_game(name, 0, &s, out_matches);
    if (rc == KILN_OK) fill(*s, out);
    return rc;
}

KilnResult KILN_CALL t_game_state_ids(KilnStr name, uint32_t* out, size_t capacity, size_t* out_count) {
    if (!name.ptr || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const GameState* s = nullptr;
    const KilnResult rc = find_game(name, 0, &s, nullptr);
    if (rc != KILN_OK) {
        if (out_count) *out_count = 0;
        return rc;
    }
    return ids_of(*s, out, capacity, out_count);
}

KilnResult KILN_CALL t_game_state_at(KilnStr name, uint32_t index, KilnPaGameState* out) {
    if (!name.ptr || !out || out->size < sizeof(KilnPaGameState)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const GameState* s = nullptr;
    const KilnResult rc = find_game(name, index, &s, nullptr);
    if (rc == KILN_OK) fill(*s, out);
    return rc;
}

KilnResult KILN_CALL t_game_state_ids_at(KilnStr name, uint32_t index, uint32_t* out, size_t capacity, size_t* out_count) {
    if (!name.ptr || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const GameState* s = nullptr;
    const KilnResult rc = find_game(name, index, &s, nullptr);
    if (rc != KILN_OK) {
        if (out_count) *out_count = 0;
        return rc;
    }
    return ids_of(*s, out, capacity, out_count);
}

KilnResult KILN_CALL t_mod_state(KilnStr mod, KilnStr name, KilnPaState* out) {
    if (!mod.ptr || !name.ptr || !out || out->size < sizeof(KilnPaState)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const std::string_view m(mod.ptr, mod.len), n(name.ptr, name.len);
    for (const KilnPaState& s : g_kept)
        if (std::string_view(s.mod.ptr, s.mod.len) == m && std::string_view(s.name.ptr, s.name.len) == n) {
            const uint32_t size = out->size;
            *out = s;
            out->size = size;
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

uint32_t KILN_CALL t_state_count() {
    std::lock_guard lock(g_mu);
    return static_cast<uint32_t>(g_kept.size());
}

KilnResult KILN_CALL t_state_at(uint32_t index, KilnPaState* out) {
    if (!out || out->size < sizeof(KilnPaState)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (index >= g_kept.size()) return KILN_ERR_NOT_FOUND;
    const uint32_t size = out->size;
    *out = g_kept[index];
    out->size = size;
    return KILN_OK;
}

// Definitions from code: not in this version (they would be kept for the next start).
KilnResult KILN_CALL t_define(KilnStr, char*, size_t, size_t* out_len) {
    if (out_len) *out_len = 0;
    return KILN_ERR_UNAVAILABLE;
}
KilnResult KILN_CALL t_prune(KilnStr, KilnStr) { return KILN_ERR_UNAVAILABLE; }

KilnResult KILN_CALL t_status(char* buf, size_t cap, size_t* out_len) {
    std::string text;
    {
        std::lock_guard lock(g_mu);
        text = status_text();
    }
    if (out_len) *out_len = text.size();
    if (!buf || cap < text.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, text.c_str(), text.size() + 1);
    return KILN_OK;
}

const KilnPlayerAnimApi kTable = {
    {sizeof(KilnPlayerAnimApi), KILN_PLAYERANIM_VERSION},
    &t_state,
    &t_hash,
    &t_game_state,
    &t_game_state_ids,
    &t_mod_state,
    &t_state_count,
    &t_state_at,
    &t_define,
    &t_prune,
    &t_status,
    &t_game_state_at,
    &t_game_state_ids_at,
};

// kiln/config/playeranim.json. Missing or broken: the defaults.
Config read_config() {
    Config c;
    const std::string dir = api_path(KILN_PATH_CONFIG);
    if (dir.empty()) return c;
    std::string text;
    const fs::path p = to_path(dir) / L"playeranim.json";
    if (!read_file(p, &text)) return c;
    const nlohmann::json j = nlohmann::json::parse(text, nullptr, false, true);
    if (j.is_discarded() || !j.is_object()) {
        PA_W("playeranim: %s isn't valid JSON: using the defaults", to_utf8(p).c_str());
        return c;
    }
    if (j.contains("enabled") && j["enabled"].is_boolean()) c.enabled = j["enabled"].get<bool>();
    if (j.contains("testMissingField") && j["testMissingField"].is_string()) c.test_missing_field = j["testMissingField"].get<std::string>();
    if (!c.test_missing_field.empty()) PA_W("playeranim: lab switch testMissingField: the merge takes field '%s' as missing", c.test_missing_field.c_str());
    return c;
}

// After every service's kiln_plugin_main: kiln.assets and kiln.types are registered, the resource service isn't up yet.
void KILN_CALL on_ready(void*) {
    std::lock_guard lock(g_mu);
    g_assets = static_cast<const KilnAssetsApi*>(g_api->service(KilnStr{KILN_ASSETS_SERVICE, sizeof(KILN_ASSETS_SERVICE) - 1}, KILN_ASSETS_VERSION));
    g_types = static_cast<const KilnTypesApi*>(g_api->service(KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, KILN_TYPES_VERSION));
    if (!g_assets || g_assets->header.size < offsetof(KilnAssetsApi, when_resources_ready) + sizeof(void*)) {
        g_assets = nullptr;
        go_off("kiln.assets isn't there, or has no when_resources_ready", KILN_CAP_REASON_SERVICE_MISSING);
        publish();
        return;
    }
    size_t files = 0;
    try {
        for (const std::string& dir : mod_folders()) {
            g_folders.push_back(dir);
            const fs::path base = to_path(dir), file = base / L"assets" / L"player-states.json";
            std::error_code ec;
            if (!fs::exists(file, ec)) continue;
            ++files;
            std::string man, text;
            if (!read_file(base / L"kiln.mod.json", &man)) {
                g_errors.push_back(to_utf8(file) + ": no kiln.mod.json above it: player states need the mod's id");
                continue;
            }
            try {
                const ManifestFields mf = read_manifest_text(man, to_utf8(base / L"kiln.mod.json"));
                if (!read_file(file, &text)) throw DefinitionError("mod " + *mf.id + ": " + to_utf8(file) + ": cannot be read");
                g_mods.push_back(load_bytes(text, *mf.id, mf.side, mf.clients, kFileName));
                g_dir_of[*mf.id] = dir;
            } catch (const DefinitionError& e) {
                g_errors.push_back(e.what());
            }
        }
    } catch (const std::exception& e) {
        g_errors.push_back(std::string("reading the mods' player states failed: ") + e.what());
    }
    size_t states = 0;
    for (const ModStates& m : g_mods) states += m.states.size();
    if (states == 0) {
        // nothing to merge: nothing is read from the game, nothing is put
        g_state.store(KILN_PA_UNTOUCHED);
        for (const std::string& e : g_errors) PA_W("playeranim: %s", e.c_str());
        g_row_text = files == 0 ? std::string("no mod defines a player state: the merge was not run; the game's player graph is served")
                                : std::to_string(files) + " player-states.json file(s) with no state in them: the merge was not run; the game's player graph is served";
        report_row();
        if (files == 0) PA_I("playeranim: no mod defines a player state: the game's graph is untouched");
        else PA_I("playeranim: %zu player-states.json file(s), no state in them: the game's graph is untouched", files);
        write_status();
        return;
    }
    g_state.store(KILN_PA_WAITING);
    PA_I("playeranim: %zu player state(s) of %zu mod(s) to merge when the game's resources come up", states, g_mods.size());
    watch_mods();
    if (g_assets->when_resources_ready(&on_resources_ready, nullptr) != KILN_OK) {
        go_off("kiln.assets has no resource service on this build", KILN_CAP_REASON_SERVICE_MISSING);
        publish();
    }
}

}  // namespace
}  // namespace kpa

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kpa;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_self = owner;
    info->id = KilnStr{KILN_PLAYERANIM_SERVICE, sizeof(KILN_PLAYERANIM_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    const KilnResult rc = api->register_service(owner, KilnStr{KILN_PLAYERANIM_SERVICE, sizeof(KILN_PLAYERANIM_SERVICE) - 1}, &kTable);
    if (rc != KILN_OK) return rc;
    // kiln.caps is in the core and set up before any service DLL loads: here already, so the policy cases report at once
    g_caps = static_cast<const KilnCapsApi*>(api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION));
    if (g_caps && g_caps->header.size < offsetof(KilnCapsApi, report) + sizeof(void*)) g_caps = nullptr;
    g_cfg = read_config();
    KilnBuildInfo bi{};
    bi.size = sizeof bi;
    api->build_info(&bi);
    if (!g_cfg.enabled || !bi.hooks_enabled) {
        std::lock_guard lock(g_mu);
        g_off_reason = !g_cfg.enabled ? "off by kiln/config/playeranim.json" : "hooks disabled (safe idle)";
        g_off_code = KILN_CAP_REASON_POLICY;
        g_state.store(KILN_PA_OFF);
        report_row();
        PA_I("playeranim: %s: the game's graph is untouched", g_off_reason.c_str());
        write_status();
        return KILN_OK;
    }
    info->on_ready = &on_ready;
    return KILN_OK;
}
