// SPDX-License-Identifier: MIT
// kiln.saves: world save events, "save now" through the game's own request, and the world's identity.
// Engine evidence: docs/re/saves.md. Usage: docs/kiln/services/saves.md. Table: native/core/abi/services/kiln_saves.h.
//
// Three observe-only hooks (each calls the original first or last and never changes its arguments or result):
//  - requestServerSave(inst)        server thread: who asked (return address), whether the game accepted it
//  - save2WriteWorld(save2, user)   Save2Thread: the world file is complete (result byte, slot, GUID, revision)
//  - loadSavegameStep(out, inst)    server thread: the world finished loading (once, on the first result 0)
// The managed Kiln.Saves registers a pump; it is called synchronously for SAVING (inside the game's request, before
// the game snapshots the world in its next instance tick) and at the start of a step for everything else.
#include <windows.h>
#include <intrin.h>
#include <shlobj.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "nlohmann/json.hpp"
#include "safetyhook.hpp"
#include "services/kiln_saves.h"

// The managed mirror (managed/services/Kiln.Saves/Native.cs) relies on these sizes.
static_assert(sizeof(KilnSaveEvent) == 88 && sizeof(KilnSavesStatus) == 128);

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uintptr_t g_base = 0;

// ---- layout (sigdb saves.json) ----
struct Offsets {
    int64_t inst_save_block = 0, inst_save_sys = 0, inst_last_request = 0, inst_slot_valid = 0, inst_save2 = 0;
    int64_t ss_state = 0, ss_flags = 0;
    int64_t s2_result = 0, s2_guid = 0, s2_rev_rel = 0, s2_slot = 0;
} O;
bool g_can_request = false, g_can_write = false, g_can_load = false, g_can_reason = false;
uintptr_t g_ret_autosave = 0, g_ret_shutdown = 0, g_ret_player_left = 0;
int64_t g_autosave_ns = 0;

using RequestFn = void(__fastcall*)(uintptr_t inst);
using WaitJobsFn = void(__cdecl*)(uintptr_t world);
RequestFn g_request_fn = nullptr;
WaitJobsFn g_wait_jobs = nullptr;
SafetyHookInline g_h_request, g_h_write, g_h_load;

// ---- logging ----
void logv(int level, const char* fmt, va_list ap) {
    char buf[1024];
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    if (n < 0) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(std::min(n, static_cast<int>(sizeof buf) - 1))});
}
void logf(int level, const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    logv(level, fmt, ap);
    va_end(ap);
}

int64_t unix_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}
double mono_ms() {
    using namespace std::chrono;
    return duration<double, std::milli>(steady_clock::now().time_since_epoch()).count();
}

const char* reason_name(uint32_t r) {
    switch (r) {
        case KILN_SAVE_REASON_AUTOSAVE: return "autosave";
        case KILN_SAVE_REASON_SHUTDOWN: return "shutdown";
        case KILN_SAVE_REASON_PLAYER_LEFT: return "player left";
        case KILN_SAVE_REASON_REQUEST: return "request";
        case KILN_SAVE_REASON_BACKUP: return "backup";
        default: return "unknown";
    }
}

std::string guid_text(const uint8_t g[16]) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1],
                  g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}
bool zero16(const uint8_t g[16]) {
    for (int i = 0; i < 16; ++i)
        if (g[i]) return false;
    return true;
}

// ---- game memory (guarded reads) ----
bool read_mem(uintptr_t addr, void* out, size_t n) {
    if (addr < 0x10000) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(addr), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
template <class T>
bool rd(uintptr_t addr, T* out) {
    return read_mem(addr, out, sizeof(T));
}

// ---- paths ----
std::filesystem::path u8p(const std::string& s) {
    return std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(s.data()), s.size()));
}
std::string u8s(const std::filesystem::path& p) {
    const std::u8string s = p.u8string();
    return std::string(reinterpret_cast<const char*>(s.data()), s.size());
}
std::string core_path(int kind) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(kind, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::string(buf, n);
}

std::filesystem::path g_game, g_save_dir, g_data_root, g_backups;

std::filesystem::path find_save_dir() {
    std::filesystem::path dir = g_game / "savegame";
    try {
        std::ifstream cf(g_game / "enshrouded_server.json", std::ios::binary);
        if (cf) {
            std::stringstream ss;
            ss << cf.rdbuf();
            const auto cfg = nlohmann::json::parse(ss.str(), nullptr, false);
            if (cfg.is_object() && cfg.contains("saveDirectory") && cfg["saveDirectory"].is_string()) {
                std::filesystem::path d = u8p(cfg["saveDirectory"].get<std::string>());
                dir = d.is_absolute() ? d : g_game / d;
            }
        }
    } catch (...) {
    }
    return dir.lexically_normal();
}

struct Header {
    uint8_t guid[16]{};
    uint32_t revision = 0, unix_s = 0;
};
// "SV2C" world file header: +0x0C GUID, +0x1C revision, +0x20 UTC seconds (docs/re/saves.md).
bool read_header(const std::filesystem::path& f, Header* h) {
    std::ifstream in(f, std::ios::binary);
    uint8_t b[0x24]{};
    if (!in || !in.read(reinterpret_cast<char*>(b), sizeof b) || std::memcmp(b, "SV2C", 4) != 0) return false;
    std::memcpy(h->guid, b + 0x0C, 16);
    std::memcpy(&h->revision, b + 0x1C, 4);
    std::memcpy(&h->unix_s, b + 0x20, 4);
    return true;
}

// ---- a game client's save folders (docs/re/client.md; EMF docs/engine_parts/re_saves.md) ----
// A client keeps worlds in two backends; a slot id is (storage << 8) | (index + 1), storage 0 Local, 1 Steam:
//   Steam: <Steam>\userdata\<account id>\1203620\remote\world<index>.ksd (ISteamRemoteStorage)
//   Local: <Saved Games>\keen games\Enshrouded\default\world<index>.ksd
// Kiln only reads these files. Under Proton the Steam folder is the Linux one (STEAM_COMPAT_CLIENT_INSTALL_PATH, seen
// through Wine's Z: drive); on Windows it is HKCU\Software\Valve\Steam\SteamPath.
bool g_client = false;
std::filesystem::path g_local_dir;              // Local backend folder
std::vector<std::filesystem::path> g_steam_dirs;  // <Steam>\userdata\*\1203620\remote that exist

std::filesystem::path local_save_dir() {
    PWSTR p = nullptr;
    std::filesystem::path dir;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_SavedGames, 0, nullptr, &p)) && p)
        dir = std::filesystem::path(p) / L"keen games" / L"Enshrouded" / L"default";
    CoTaskMemFree(p);
    return dir;
}

std::vector<std::filesystem::path> steam_remote_dirs() {
    std::vector<std::filesystem::path> roots, out;
    wchar_t buf[2048];
    DWORD n = GetEnvironmentVariableW(L"STEAM_COMPAT_CLIENT_INSTALL_PATH", buf, 2048);
    if (n > 0 && n < 2048) {
        std::wstring linux_path(buf, n);
        if (!linux_path.empty() && linux_path[0] == L'/') {
            for (wchar_t& c : linux_path) c = c == L'/' ? L'\\' : c;
            roots.emplace_back(L"Z:" + linux_path);
        } else {
            roots.emplace_back(linux_path);
        }
    }
    DWORD size = sizeof buf;
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath", RRF_RT_REG_SZ, nullptr, buf, &size) ==
        ERROR_SUCCESS)
        roots.emplace_back(std::wstring(buf));
    std::error_code ec;
    for (const auto& r : roots)
        for (auto& e : std::filesystem::directory_iterator(r / L"userdata", ec))
            if (std::filesystem::is_directory(e.path() / L"1203620" / L"remote", ec))
                out.push_back((e.path() / L"1203620" / L"remote").lexically_normal());
    std::sort(out.begin(), out.end());
    out.erase(std::unique(out.begin(), out.end()), out.end());
    return out;
}

// Of several Steam accounts' folders the one whose world<index>.ksd changed last (the account that plays now).
std::filesystem::path steam_dir_for(unsigned index) {
    std::error_code ec;
    std::filesystem::path best;
    std::filesystem::file_time_type best_t{};
    for (const auto& d : g_steam_dirs) {
        const auto f = d / (L"world" + std::to_wstring(index) + L".ksd");
        const auto t = std::filesystem::last_write_time(f, ec);
        if (ec) continue;
        if (best.empty() || t > best_t) best = d, best_t = t;
    }
    return best.empty() && !g_steam_dirs.empty() ? g_steam_dirs.front() : best;
}

std::filesystem::path world_file_for(uint16_t slot) {
    const unsigned lo = slot & 0xFFu;
    if (lo < 1 || lo > 10) return {};
    const std::wstring name = L"world" + std::to_wstring(lo - 1) + L".ksd";
    if (!g_client) return g_save_dir / name;
    const unsigned storage = slot >> 8;
    const std::filesystem::path dir = storage == 1 ? steam_dir_for(lo - 1) : (storage == 0 ? g_local_dir : std::filesystem::path{});
    if (dir.empty()) return {};
    return dir / name;
}
// Newest world*.ksd in the save directory (before the game told us its slot).
std::filesystem::path newest_world_file() {
    std::error_code ec;
    std::filesystem::path best;
    std::filesystem::file_time_type best_t{};
    for (auto& e : std::filesystem::directory_iterator(g_save_dir, ec)) {
        const std::wstring n = e.path().filename().wstring();
        if (n.rfind(L"world", 0) != 0 || e.path().extension() != L".ksd") continue;
        const auto t = e.last_write_time(ec);
        if (best.empty() || t > best_t) best = e.path(), best_t = t;
    }
    return best;
}

// ---- shared state ----
std::mutex g_mu;  // guards everything below that is not atomic
std::deque<KilnSaveEvent> g_events;
uint64_t g_seq = 0;
KilnSavesStatus g_st{};
std::filesystem::path g_world_file;
uint64_t g_current_save_seq = 0;  // SAVING seq of the save in progress
double g_accepted_mono = 0;
uint32_t g_current_reason = 0;
uint64_t g_autosaves_logged = 0;

std::atomic<uintptr_t> g_inst{0};
std::atomic<uintptr_t> g_save2{0};
std::atomic<uint64_t> g_step{0};
std::atomic<uint32_t> g_pending_reason{0};
std::atomic<int64_t> g_pending_since_ms{0};
std::atomic<bool> g_loaded{false};

std::atomic<KilnSavesPumpFn> g_pump{nullptr};
std::atomic<uint32_t> g_pump_every{0};
std::atomic<void*> g_pump_user{nullptr};
uint64_t g_pumped_seq = 0;  // tick thread
bool g_in_pump = false;     // tick thread
thread_local uint32_t tl_own_reason = 0;
thread_local bool tl_in_pre = false;

uint64_t push_event(KilnSaveEvent ev) {  // g_mu held
    ev.size = sizeof ev;
    ev.seq = ++g_seq;
    ev.step = g_step.load();
    ev.unix_ms = unix_ms();
    g_events.push_back(ev);
    while (g_events.size() > 256) g_events.pop_front();
    g_st.last_seq = ev.seq;
    return ev.seq;
}

// Reads GUID / revision / slot from the game's Save2System (any thread; plain loads of game memory).
bool read_save2(uintptr_t save2, uint8_t guid[16], uint32_t* rev, uint16_t* slot) {
    if (!save2 || !g_can_write) return false;
    bool ok = read_mem(save2 + static_cast<uintptr_t>(O.s2_guid), guid, 16) &&
              rd(save2 + static_cast<uintptr_t>(O.s2_guid + O.s2_rev_rel), rev);
    if (slot) ok = ok && rd(save2 + static_cast<uintptr_t>(O.s2_slot), slot);
    return ok;
}

uintptr_t save2_of(uintptr_t inst) {
    uintptr_t s2 = 0;
    if (inst && g_can_load && rd(inst + static_cast<uintptr_t>(O.inst_save2), &s2)) return s2;
    return g_save2.load();
}

// ---- pump ----
// SEH wrappers: no C++ objects in these frames.
bool invoke_pump(KilnSavesPumpFn fn, uint32_t kind, void* user, DWORD* code) {
    __try {
        fn(kind, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
void invoke_wait(WaitJobsFn fn, uintptr_t world) {
    __try {
        fn(world);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

void call_pump(uint32_t kind) {  // tick thread
    KilnSavesPumpFn fn = g_pump.load();
    if (!fn || g_in_pump) return;
    {
        std::lock_guard lk(g_mu);
        g_pumped_seq = g_seq;
    }
    g_in_pump = true;
    DWORD code = 0;
    const bool ok = invoke_pump(fn, kind, g_pump_user.load(), &code);
    g_in_pump = false;
    if (!ok) {
        g_pump.store(nullptr);
        logf(KILN_LOG_ERROR, "saves: the pump faulted (0x%08lx) and was removed; save events stop reaching managed code",
             code);
    }
}

void wait_world_jobs() {
    const uintptr_t world = g_api->server_world ? g_api->server_world() : 0;
    if (world && g_wait_jobs) invoke_wait(g_wait_jobs, world);
}

// ---- hooks ----
uint32_t classify(void* ret) {
    if (tl_own_reason) return tl_own_reason;
    const auto r = reinterpret_cast<uintptr_t>(ret);
    if (g_can_reason) {
        if (r == g_ret_autosave) return KILN_SAVE_REASON_AUTOSAVE;
        if (r == g_ret_shutdown) return KILN_SAVE_REASON_SHUTDOWN;
        if (r == g_ret_player_left) return KILN_SAVE_REASON_PLAYER_LEFT;
    }
    return KILN_SAVE_REASON_UNKNOWN;
}

void __fastcall hk_request(uintptr_t inst) {
    void* ret = _ReturnAddress();
    if (inst) g_inst.store(inst);
    uintptr_t ss = 0;
    uint8_t before = 0xFF, after = 0xFF;
    const bool have_ss = inst && rd(inst + static_cast<uintptr_t>(O.inst_save_sys), &ss) && ss &&
                         rd(ss + static_cast<uintptr_t>(O.ss_state), &before);
    g_h_request.call(inst);
    if (have_ss) rd(ss + static_cast<uintptr_t>(O.ss_state), &after);
    const uint32_t reason = classify(ret);
    const bool accepted = have_ss && before == 0 && after == 1;
    if (!accepted) {
        std::lock_guard lk(g_mu);
        ++g_st.refused;
        return;
    }
    const bool tick = g_api->on_tick_thread();
    const bool sync = tick && g_pump.load() && !g_in_pump;
    uint8_t guid[16]{};
    uint32_t rev = 0;
    uint16_t slot = 0;
    const bool have = read_save2(save2_of(inst), guid, &rev, &slot);
    uint64_t seq;
    uint64_t n_auto;
    {
        std::lock_guard lk(g_mu);
        KilnSaveEvent ev{};
        ev.kind = KILN_SAVE_EVENT_SAVING;
        ev.reason = reason;
        ev.flags = (sync ? KILN_SAVE_EVENT_SYNC : 0u) | (have && !zero16(guid) ? KILN_SAVE_EVENT_HAS_GUID : 0u);
        ev.revision = have ? rev + 1 : (g_st.revision ? g_st.revision + 1 : 0);
        ev.slot = have ? slot : g_st.slot;
        if (have) std::memcpy(ev.world_guid, guid, 16);
        seq = push_event(ev);
        g_current_save_seq = seq;
        g_current_reason = reason;
        g_accepted_mono = mono_ms();
        g_st.state = KILN_SAVES_STATE_SAVING;
        g_st.last_reason = reason;
        g_st.last_accepted_unix_ms = unix_ms();
        ++g_st.accepted;
        if (have && !zero16(guid)) {
            std::memcpy(g_st.world_guid, guid, 16);
            g_st.has_guid = 1;
            g_st.slot = slot;
        }
        n_auto = reason == KILN_SAVE_REASON_AUTOSAVE ? ++g_autosaves_logged : 0;
    }
    if (reason != KILN_SAVE_REASON_AUTOSAVE || n_auto <= 5 || n_auto % 20 == 0)
        logf(KILN_LOG_INFO, "saves: save #%llu accepted (%s, revision %u next, step %llu, %s)",
             static_cast<unsigned long long>(seq), reason_name(reason), have ? rev + 1 : 0u,
             static_cast<unsigned long long>(g_step.load()),
             sync ? "handlers run now, before the game's snapshot" : (tick ? "no pump" : "off the tick thread: queued"));
    if (sync) {
        if (!tl_in_pre) wait_world_jobs();  // the game calls this after its sim step; mods may read game state
        call_pump(KILN_SAVE_EVENT_SAVING);
    }
}

void __fastcall hk_write(uintptr_t save2, uintptr_t user) {
    g_h_write.call(save2, user);
    g_save2.store(save2);
    uint8_t result = 0xFF;
    rd(save2 + static_cast<uintptr_t>(O.s2_result), &result);
    uint8_t guid[16]{};
    uint32_t rev = 0;
    uint16_t slot = 0;
    const bool have = read_save2(save2, guid, &rev, &slot);
    std::filesystem::path file = world_file_for(slot);
    Header h;
    const bool hdr = !file.empty() && read_header(file, &h);
    double dur;
    uint64_t seq, save_seq;
    uint32_t reason;
    uint64_t n_auto;
    {
        std::lock_guard lk(g_mu);
        if (!file.empty()) g_world_file = file;
        if (g_client && !file.empty()) g_save_dir = file.parent_path();  // SAVE_DIR: the hosted world's folder
        KilnSaveEvent ev{};
        ev.kind = result == 0 ? KILN_SAVE_EVENT_WRITTEN : KILN_SAVE_EVENT_FAILED;
        ev.reason = reason = g_current_reason;
        ev.result = result;
        ev.revision = have ? rev : 0;
        ev.slot = slot;
        ev.world_unix = hdr && h.revision == rev ? h.unix_s : 0;
        if (have && !zero16(guid)) {
            ev.flags |= KILN_SAVE_EVENT_HAS_GUID;
            std::memcpy(ev.world_guid, guid, 16);
        }
        dur = g_accepted_mono > 0 ? mono_ms() - g_accepted_mono : 0;
        ev.duration_ms = dur;
        ev.save_seq = save_seq = g_current_save_seq;
        seq = push_event(ev);
        if (result == 0) {
            ++g_st.written;
            g_st.revision = rev;
            g_st.world_unix = ev.world_unix;
            g_st.last_written_unix_ms = unix_ms();
            g_st.last_duration_ms = dur;
            if (have && !zero16(guid)) {
                std::memcpy(g_st.world_guid, guid, 16);
                g_st.has_guid = 1;
                g_st.slot = slot;
            }
            g_st.state = KILN_SAVES_STATE_RUNNING;
        } else {
            ++g_st.failed;
        }
        n_auto = reason == KILN_SAVE_REASON_AUTOSAVE ? g_autosaves_logged : 0;
    }
    if (result != 0)
        logf(KILN_LOG_WARN, "saves: world write #%llu FAILED (result %u, %s save #%llu); the game retries up to 4 times",
             static_cast<unsigned long long>(seq), result, reason_name(reason), static_cast<unsigned long long>(save_seq));
    else if (reason != KILN_SAVE_REASON_AUTOSAVE || n_auto <= 5 || n_auto % 20 == 0)
        logf(KILN_LOG_INFO, "saves: world written: revision %u, %s, header %s (rev %u, %u), %.1f ms after the request "
             "(%s save #%llu, Save2Thread %lu)",
             rev, u8s(file.filename()).c_str(), hdr ? "ok" : "unreadable", h.revision, h.unix_s, dur,
             reason_name(reason), static_cast<unsigned long long>(save_seq), GetCurrentThreadId());
}

uintptr_t __fastcall hk_load(uintptr_t out, uintptr_t inst) {
    const uintptr_t r = g_h_load.call<uintptr_t>(out, inst);
    uint8_t code = 0xFF;
    if (!rd(out, &code) || code != 0 || g_loaded.exchange(true)) return r;
    g_inst.store(inst);
    uint8_t valid = 0;
    uint16_t slot = 0;
    if (rd(inst + static_cast<uintptr_t>(O.inst_slot_valid), &valid) && valid)
        rd(inst + static_cast<uintptr_t>(O.inst_slot_valid - 2), &slot);
    const uintptr_t save2 = save2_of(inst);
    if (save2) g_save2.store(save2);
    uint8_t guid[16]{};
    uint32_t rev = 0;
    uint16_t s2slot = 0;
    const bool have = read_save2(save2, guid, &rev, &s2slot);
    if (!slot) slot = s2slot;
    std::filesystem::path file = world_file_for(slot);
    Header h;
    const bool hdr = !file.empty() && read_header(file, &h);
    {
        std::lock_guard lk(g_mu);
        if (!file.empty()) g_world_file = file;
        if (g_client && !file.empty()) g_save_dir = file.parent_path();  // SAVE_DIR: the hosted world's folder
        KilnSaveEvent ev{};
        ev.kind = KILN_SAVE_EVENT_LOADED;
        ev.slot = slot;
        ev.revision = have ? rev : (hdr ? h.revision : 0);
        ev.world_unix = hdr ? h.unix_s : 0;
        if (!hdr) ev.flags |= KILN_SAVE_EVENT_NEW_WORLD;
        if (have && !zero16(guid)) {
            ev.flags |= KILN_SAVE_EVENT_HAS_GUID;
            std::memcpy(ev.world_guid, guid, 16);
        } else if (hdr) {
            ev.flags |= KILN_SAVE_EVENT_HAS_GUID;
            std::memcpy(ev.world_guid, h.guid, 16);
        }
        push_event(ev);
        if (ev.flags & KILN_SAVE_EVENT_HAS_GUID) {
            std::memcpy(g_st.world_guid, ev.world_guid, 16);
            g_st.has_guid = 1;
        }
        g_st.slot = slot;
        g_st.revision = ev.revision;
        g_st.world_unix = ev.world_unix;
        g_st.state = KILN_SAVES_STATE_RUNNING;
    }
    logf(KILN_LOG_INFO, "saves: world loaded: slot 0x%04x (%s), %s, revision %u (memory %s, header %s rev %u), "
         "step %llu, server thread %lu%s",
         slot, file.empty() ? "?" : u8s(file.filename()).c_str(), have ? guid_text(guid).c_str() : "guid ?", rev,
         have ? "ok" : "unreadable", hdr ? "ok" : "missing", h.revision, static_cast<unsigned long long>(g_step.load()),
         GetCurrentThreadId(), g_api->on_tick_thread() ? " (tick thread)" : " (NOT the tick thread)");
    return r;
}

// ---- staged restore (Saves.RestoreOnNextStart -> kiln/backups/restore.json) ----
// Runs in kiln_plugin_main, before the game's entry point: the game has not opened its save yet. Copies the backup's
// world file(s) into the save directory (the files it replaces go to kiln/backups/_before-restore-<stamp>/ first) and
// its sidecar generation into kiln/data/worlds/<id>/ (content-addressed files: existing ones are kept).
std::string stamp_now() {
    SYSTEMTIME t;
    GetSystemTime(&t);
    char b[32];
    std::snprintf(b, sizeof b, "%04u%02u%02u-%02u%02u%02u", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
    return b;
}

bool copy_atomic(const std::filesystem::path& from, const std::filesystem::path& to, std::string& err) {
    std::error_code ec;
    std::filesystem::create_directories(to.parent_path(), ec);
    const std::filesystem::path tmp = to.wstring() + L".kiln-restore.tmp";
    if (!std::filesystem::copy_file(from, tmp, std::filesystem::copy_options::overwrite_existing, ec)) {
        err = "copy " + u8s(from) + ": " + ec.message();
        return false;
    }
    if (!MoveFileExW(tmp.c_str(), to.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        err = "replace " + u8s(to) + ": error " + std::to_string(GetLastError());
        std::filesystem::remove(tmp, ec);
        return false;
    }
    return true;
}

void apply_staged_restore() {
    const std::filesystem::path req = g_backups / "restore.json";
    std::error_code ec;
    if (!std::filesystem::exists(req, ec)) return;
    if (g_client) {  // never write into a client's Steam cloud / local save folders
        const std::filesystem::path refused = g_backups / ("restore.refused-" + stamp_now() + ".json");
        std::filesystem::rename(req, refused, ec);
        logf(KILN_LOG_WARN, "saves: a staged restore is for dedicated servers; refused on a game client (kept as %s)",
             u8s(refused.filename()).c_str());
        return;
    }
    nlohmann::json result = {{"requested", req.filename().string()}, {"ok", false}};
    std::string err;
    std::vector<std::string> done;
    auto finish = [&](bool ok) {
        result["ok"] = ok;
        result["error"] = err;
        result["files"] = done;
        result["finished"] = stamp_now();
        const std::filesystem::path out = g_backups / ("restore.result-" + stamp_now() + ".json");
        std::ofstream(out, std::ios::binary) << result.dump(2);
        std::filesystem::remove(req, ec);
        if (ok)
            logf(KILN_LOG_INFO, "saves: restore applied before the game started: %s (%zu files)",
                 result.value("backup", std::string("?")).c_str(), done.size());
        else
            logf(KILN_LOG_ERROR, "saves: staged restore NOT applied: %s (the save was left as it was)", err.c_str());
    };
    try {
        std::ifstream rf(req, std::ios::binary);
        std::stringstream rs;
        rs << rf.rdbuf();
        rf.close();
        const auto r = nlohmann::json::parse(rs.str(), nullptr, false);
        if (!r.is_object() || !r.contains("backup") || !r["backup"].is_string()) return err = "bad restore.json", finish(false);
        const std::string name = r["backup"].get<std::string>();
        result["backup"] = name;
        if (name.empty() || name.find_first_of("/\\") != std::string::npos || name.rfind("..", 0) == 0)
            return err = "bad backup name", finish(false);
        const std::filesystem::path dir = g_backups / u8p(name);
        std::ifstream bf(dir / "backup.json", std::ios::binary);
        std::stringstream bs;
        bs << bf.rdbuf();
        const auto b = nlohmann::json::parse(bs.str(), nullptr, false);
        if (!b.is_object() || !b.contains("worldId") || !b.contains("revision") || !b.contains("saveFiles"))
            return err = "backup.json missing or incomplete in " + u8s(dir), finish(false);
        const std::string world = b["worldId"].get<std::string>();
        const uint32_t rev = b["revision"].get<uint32_t>();
        // Check every world file first: header GUID + revision as recorded.
        for (const auto& f : b["saveFiles"]) {
            const std::filesystem::path src = dir / "savegame" / u8p(f.get<std::string>());
            Header h;
            if (!read_header(src, &h)) return err = "unreadable world file " + u8s(src), finish(false);
            if (guid_text(h.guid) != world || h.revision != rev)
                return err = "world file " + u8s(src) + " is " + guid_text(h.guid) + " rev " + std::to_string(h.revision) +
                             ", backup.json says " + world + " rev " + std::to_string(rev),
                       finish(false);
        }
        const std::filesystem::path safety = g_backups / ("_before-restore-" + stamp_now()) / "savegame";
        for (const auto& f : b["saveFiles"]) {
            const std::filesystem::path name_f = u8p(f.get<std::string>());
            const std::filesystem::path cur = g_save_dir / name_f;
            if (std::filesystem::exists(cur, ec)) {
                std::filesystem::create_directories(safety, ec);
                std::filesystem::copy_file(cur, safety / name_f, std::filesystem::copy_options::overwrite_existing, ec);
                if (ec) return err = "safety copy of " + u8s(cur) + ": " + ec.message(), finish(false);
            }
        }
        for (const auto& f : b["saveFiles"]) {
            const std::filesystem::path name_f = u8p(f.get<std::string>());
            if (!copy_atomic(dir / "savegame" / name_f, g_save_dir / name_f, err)) return finish(false);
            done.push_back("savegame/" + f.get<std::string>());
        }
        const std::filesystem::path wsrc = dir / "worlds" / u8p(world);
        if (std::filesystem::exists(wsrc, ec)) {
            for (auto& e : std::filesystem::recursive_directory_iterator(wsrc, ec)) {
                if (!e.is_regular_file()) continue;
                const auto rel = std::filesystem::relative(e.path(), wsrc, ec);
                const auto dst = g_data_root / u8p(world) / rel;
                if (std::filesystem::exists(dst, ec) && std::filesystem::file_size(dst, ec) == e.file_size()) continue;
                if (!copy_atomic(e.path(), dst, err)) return finish(false);
                done.push_back("worlds/" + world + "/" + u8s(rel.generic_wstring()));
            }
        }
        result["worldId"] = world;
        result["revision"] = rev;
        result["safetyCopy"] = u8s(safety.parent_path());
        finish(true);
    } catch (const std::exception& e) {
        err = e.what();
        finish(false);
    }
}

// ---- tick ----
void issue_pending() {  // tick thread, Pre
    const uint32_t reason = g_pending_reason.load();
    if (!reason) return;
    const uintptr_t inst = g_inst.load();
    const int64_t since = g_pending_since_ms.load();
    if (unix_ms() - since > 60000) {
        g_pending_reason.store(0);
        std::lock_guard lk(g_mu);
        KilnSaveEvent ev{};
        ev.kind = KILN_SAVE_EVENT_FAILED;
        ev.reason = reason;
        ev.flags = KILN_SAVE_EVENT_TIMEOUT;
        ev.result = 0xFF;
        push_event(ev);
        ++g_st.failed;
        g_st.pending_request = 0;
        logf(KILN_LOG_WARN, "saves: %s request not accepted by the game within 60 s; dropped", reason_name(reason));
        return;
    }
    if (!inst) return;
    uintptr_t ss = 0;
    uint8_t state = 0xFF, flags = 0;
    if (!rd(inst + static_cast<uintptr_t>(O.inst_save_sys), &ss) || !ss ||
        !rd(ss + static_cast<uintptr_t>(O.ss_state), &state) || !rd(ss + static_cast<uintptr_t>(O.ss_flags), &flags))
        return;
    if (state != 0 || !(flags & 4) || (flags & 1)) return;  // a save is running / pending, or the world is not running
    const uint64_t accepted_before = [] {
        std::lock_guard lk(g_mu);
        return g_st.accepted;
    }();
    tl_own_reason = reason;
    tl_in_pre = true;
    g_request_fn(inst);  // through the hook: it records the save and runs the pump
    tl_in_pre = false;
    tl_own_reason = 0;
    std::lock_guard lk(g_mu);
    if (g_st.accepted > accepted_before) {
        g_pending_reason.store(0);
        g_st.pending_request = 0;
    }
}

void KILN_CALL on_pre(const KilnTickInfo* t, void*) {
    g_step.store(t->step);
    {
        std::lock_guard lk(g_mu);
        g_st.step = t->step;
        if (g_st.state == KILN_SAVES_STATE_NONE) g_st.state = KILN_SAVES_STATE_LOADING;
    }
    if (g_can_request) issue_pending();
    uint32_t kind = 0;
    {
        std::lock_guard lk(g_mu);
        if (g_seq > g_pumped_seq && !g_events.empty()) kind = g_events.back().kind;
    }
    const uint32_t every = g_pump_every.load();
    if (kind) call_pump(kind);
    else if (every && t->step % every == 0) call_pump(0);
}

// ---- world sessions ----
// A game client hosts one world after another (back to the main menu, host again; another world or the same one).
// When the game closes the server world (after its shutdown save) the world's identity and instance go; the next
// world's load reports LOADED again. A dedicated server has one world for its run: nothing here runs there.
void drop_world(const char* why) {
    const uint32_t pending = g_pending_reason.exchange(0);
    {
        std::lock_guard lk(g_mu);
        if (pending) {
            KilnSaveEvent ev{};
            ev.kind = KILN_SAVE_EVENT_FAILED;
            ev.reason = pending;
            ev.result = 0xFF;
            push_event(ev);
            ++g_st.failed;
        }
        g_st.state = KILN_SAVES_STATE_NONE;
        g_st.has_guid = 0;
        std::memset(g_st.world_guid, 0, sizeof g_st.world_guid);
        g_st.slot = 0;
        g_st.revision = 0;
        g_st.world_unix = 0;
        g_st.pending_request = 0;
        g_world_file.clear();
        g_current_save_seq = 0;
        g_current_reason = 0;
    }
    g_loaded.store(false);
    g_inst.store(0);
    g_save2.store(0);
    logf(KILN_LOG_INFO, "saves: server world closed (%s)%s; the next world's load is reported again", why,
         pending ? "; a pending save request was dropped" : "");
}

bool g_closed = false;  // tick thread

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER) return;
    if (ev->event == KILN_WORLD_STOPPING) {
        drop_world("the game is closing it");
        g_closed = true;
    } else if (ev->event == KILN_WORLD_STARTED) {
        if (ev->session > 1 && !g_closed) drop_world("a new server world started");
        g_closed = false;
    }
}

// ---- table ----
KilnResult KILN_CALL t_status(KilnSavesStatus* out) {
    if (!out || out->size < sizeof(KilnSavesStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    *out = g_st;
    out->size = sizeof(KilnSavesStatus);
    out->features = (g_can_request ? KILN_SAVES_CAN_REQUEST : 0u) | (g_can_write ? KILN_SAVES_CAN_WRITE : 0u) |
                    (g_can_load ? KILN_SAVES_CAN_LOAD : 0u) | (g_can_reason ? KILN_SAVES_CAN_REASON : 0u) |
                    (g_client ? KILN_SAVES_CLIENT_HOSTED : 0u);
    out->autosave_interval_ns = g_autosave_ns;
    out->pending_request = g_pending_reason.load() ? 1 : 0;
    return KILN_OK;
}

KilnResult KILN_CALL t_request(uint32_t reason) {
    if (reason != KILN_SAVE_REASON_REQUEST && reason != KILN_SAVE_REASON_BACKUP) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_can_request) return KILN_ERR_UNAVAILABLE;  // before the world loaded the request waits (60 s at most)
    uint32_t expected = 0;
    if (g_pending_reason.compare_exchange_strong(expected, reason)) g_pending_since_ms.store(unix_ms());
    std::lock_guard lk(g_mu);
    g_st.pending_request = 1;
    return KILN_OK;
}

KilnResult KILN_CALL t_next_event(uint64_t after, KilnSaveEvent* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    for (const auto& e : g_events)
        if (e.seq > after) return *out = e, KILN_OK;
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_set_pump(KilnOwner, KilnSavesPumpFn fn, void* user, uint64_t* last_seq) {
    g_pump_user.store(user);
    g_pump.store(fn);
    std::lock_guard lk(g_mu);
    g_pumped_seq = g_seq;
    if (last_seq) *last_seq = g_seq;
    return KILN_OK;
}

KilnResult KILN_CALL t_path(int32_t kind, char* buf, size_t cap, size_t* out_len) {
    std::string s;
    switch (kind) {
        case KILN_SAVES_PATH_SAVE_DIR: {
            std::lock_guard lk(g_mu);
            s = u8s(g_save_dir);
            break;
        }
        case KILN_SAVES_PATH_WORLD_FILE: {
            std::lock_guard lk(g_mu);
            if (g_world_file.empty()) return KILN_ERR_NOT_FOUND;
            s = u8s(g_world_file);
            break;
        }
        case KILN_SAVES_PATH_DATA: s = u8s(g_data_root); break;
        case KILN_SAVES_PATH_BACKUPS: s = u8s(g_backups); break;
        default: return KILN_ERR_INVALID_ARGUMENT;
    }
    if (out_len) *out_len = s.size();
    if (!buf || cap < s.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, s.data(), s.size());
    return KILN_OK;
}

KilnResult KILN_CALL t_set_pump_interval(uint32_t steps) {
    g_pump_every.store(steps);
    return KILN_OK;
}

const KilnSavesApi kTable = {{sizeof(KilnSavesApi), KILN_SAVES_VERSION}, &t_status, &t_request, &t_next_event,
                             &t_set_pump, &t_path, &t_set_pump_interval};

bool sym(const char* name, uintptr_t* out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

template <typename F>
bool hook(SafetyHookInline& h, uintptr_t target, F* detour, const char* what) {
    h = safetyhook::create_inline(reinterpret_cast<void*>(target), reinterpret_cast<void*>(detour));
    if (!h) {
        logf(KILN_LOG_ERROR, "saves: hooking %s at rva 0x%llx failed", what,
             static_cast<unsigned long long>(target - g_base));
        return false;
    }
    return true;
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    info->id = KilnStr{KILN_SAVES_SERVICE, sizeof(KILN_SAVES_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    g_st.size = sizeof g_st;

    g_game = u8p(core_path(KILN_PATH_GAME));
    g_client = api->side() == KILN_SIDE_CLIENT;
    if (g_client) {
        g_local_dir = local_save_dir();
        g_steam_dirs = steam_remote_dirs();
        g_save_dir = !g_steam_dirs.empty() ? g_steam_dirs.front() : g_local_dir;
    } else {
        g_save_dir = find_save_dir();
    }
    g_data_root = (u8p(core_path(KILN_PATH_DATA)) / "worlds").lexically_normal();
    g_backups = (u8p(core_path(KILN_PATH_KILN)) / "backups").lexically_normal();
    apply_staged_restore();
    // Identity before the game says anything: the newest world file's header (the dedicated server has one world; a
    // client has many and says which one it hosts when it loads it).
    if (auto f = g_client ? std::filesystem::path{} : newest_world_file(); !f.empty()) {
        Header h;
        if (read_header(f, &h) && !zero16(h.guid)) {
            g_world_file = f;
            std::memcpy(g_st.world_guid, h.guid, 16);
            g_st.has_guid = 1;
            g_st.revision = h.revision;
            g_st.world_unix = h.unix_s;
        }
    }

    uintptr_t req = 0, write = 0, load = 0;
    g_can_write = feat("saves.write") && sym("saves/Save2WriteWorld", &write) && cst("saves/s2.result", &O.s2_result) &&
                  cst("saves/s2.guid", &O.s2_guid) && cst("saves/s2.rev_rel", &O.s2_rev_rel) &&
                  cst("saves/s2.slot", &O.s2_slot);
    g_can_load = feat("saves.load") && sym("saves/LoadSavegameStep", &load) &&
                 cst("saves/inst.slot_valid", &O.inst_slot_valid) && cst("saves/inst.save2", &O.inst_save2);
    g_can_request = feat("saves.request") && sym("saves/RequestServerSave", &req) &&
                    cst("saves/inst.save_block", &O.inst_save_block) && cst("saves/inst.save_sys", &O.inst_save_sys) &&
                    cst("saves/ss.state", &O.ss_state) && cst("saves/ss.flags", &O.ss_flags) &&
                    cst("saves/inst.last_request", &O.inst_last_request);
    int64_t lo = 0, hi = 0;
    g_can_reason = g_can_request && feat("saves.reasons") && sym("saves/RetAutosave", &g_ret_autosave) &&
                   sym("saves/RetShutdown", &g_ret_shutdown) && sym("saves/RetPlayerLeave", &g_ret_player_left) &&
                   cst("saves/autosave.lo", &lo) && cst("saves/autosave.hi", &hi);
    if (g_can_reason) g_autosave_ns = (hi << 32) | (lo & 0xFFFFFFFFll);
    uintptr_t wait = 0;
    if (feat("tick.wait_jobs") && sym("WaitWorldJobs", &wait)) g_wait_jobs = reinterpret_cast<WaitJobsFn>(wait);

    if (!g_can_request && !g_can_write) {
        logf(KILN_LOG_WARN, "saves: features saves.request and saves.write are unavailable on this build; kiln.saves is off");
        return KILN_ERR_UNAVAILABLE;
    }
    if (g_can_write && !hook(g_h_write, write, &hk_write, "save2WriteWorld")) g_can_write = false;
    if (g_can_load && !hook(g_h_load, load, &hk_load, "loadSavegameStep")) g_can_load = false;
    if (g_can_request) {
        if (hook(g_h_request, req, &hk_request, "requestServerSave")) {
            g_request_fn = reinterpret_cast<RequestFn>(req);
        } else {
            g_can_request = g_can_reason = false;
        }
    }
    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    if (g_client) {
        std::string dirs;
        for (const auto& d : g_steam_dirs) dirs += (dirs.empty() ? "" : ", ") + u8s(d);
        logf(KILN_LOG_INFO, "saves: game client: world files in the Steam cloud folder(s) %s and the local folder %s "
             "(read only; staged restores are refused)", dirs.empty() ? "(none found)" : dirs.c_str(),
             g_local_dir.empty() ? "(unknown)" : u8s(g_local_dir).c_str());
    }
    logf(KILN_LOG_INFO,
         "saves: hooks request %s (rva 0x%llx), write %s (rva 0x%llx), load %s (rva 0x%llx), reasons %s, autosave every "
         "%.1f s; save dir %s; world %s rev %u (from %s)",
         g_can_request ? "on" : "off", static_cast<unsigned long long>(req ? req - g_base : 0),
         g_can_write ? "on" : "off", static_cast<unsigned long long>(write ? write - g_base : 0),
         g_can_load ? "on" : "off", static_cast<unsigned long long>(load ? load - g_base : 0),
         g_can_reason ? "on" : "off", static_cast<double>(g_autosave_ns) / 1e9, u8s(g_save_dir).c_str(),
         g_st.has_guid ? guid_text(g_st.world_guid).c_str() : "(no world file yet)", g_st.revision,
         g_world_file.empty() ? "-" : u8s(g_world_file.filename()).c_str());
    return api->register_service(owner, KilnStr{KILN_SAVES_SERVICE, sizeof(KILN_SAVES_SERVICE) - 1}, &kTable);
}
