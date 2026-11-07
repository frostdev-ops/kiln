// SPDX-License-Identifier: MIT
// compathost.exe: a stand-in for the game and kiln-core for the compatibility plugins (kiln-greeneye, kiln-flame,
// kiln-shroudtopia). run.py copies it into a scratch game folder as enshrouded.exe or enshrouded_server.exe and runs
// it under plain Wine:
//   compathost.exe <plugin.dll> <wait ms> [--claim-greeneye] [--load <dll>] [--at <ms> --copy <src> <dst>]
// It serves a minimal KilnApi (log, paths, side, build info, kiln.profile from kiln/profile.json, register_service /
// service), loads the plugin, runs kiln_plugin_main and on_ready, waits, then calls the test targets below and prints
// RESULT lines that run.py checks together with kiln/logs/kiln.log and kiln/state/*.json.
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_profile.h"
#include "../../flame/include/kiln_flame.h"

// ---- test targets in the exe's .text (Greeneye's find only scans the exe) ----
//   gt_target(a, b) = a + b + 0x5A17C0DE; its first 12 bytes are whole instructions (a Greeneye hook length).
//   gt_caller(a, b) = gt_target2(a, b) = a + 2b, through one "call rel32" right after a marker "mov eax, 0x7EA7C0DE".
asm(R"(
    .intel_syntax noprefix
    .text
    .globl gt_target
    .p2align 4
gt_target:
    lea eax, [rcx + rdx]
    add eax, 0x5A17C0DE
    nop
    nop
    nop
    nop
    ret
    .p2align 4
    .globl gt_target2
gt_target2:
    lea eax, [rcx + rdx*2]
    ret
    .p2align 4
    .globl gt_caller
gt_caller:
    sub rsp, 40
    mov eax, 0x7EA7C0DE
    call gt_target2
    add rsp, 40
    ret
    .att_syntax
)");;
extern "C" int gt_target(int a, int b);
#pragma comment(linker, "/EXPORT:gt_target")  // Shroudtopia's test mod finds it by name to check FindPattern
extern "C" int gt_caller(int a, int b);

namespace {

std::wstring g_game;  // exe folder, no trailing slash
std::mutex g_log_mu;
FILE* g_log = nullptr;
uint32_t g_side = KILN_SIDE_CLIENT;
std::map<std::string, const void*> g_services;
std::string g_profile_text;
uint32_t g_profile_mode = KILN_PROFILE_LEGACY;
std::string g_cfg_u8;
std::string g_data_u8;

std::string u8(const std::wstring& w) {
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}
std::wstring wide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n ? n - 1 : 0, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}
std::string sv(KilnStr s) { return s.ptr ? std::string(s.ptr, s.len) : std::string(); }
KilnStr ks(const std::string& s) { return KilnStr{s.data(), s.size()}; }

void line(const std::string& s) {
    std::lock_guard lock(g_log_mu);
    std::printf("%s\n", s.c_str());
    std::fflush(stdout);
    if (g_log) std::fprintf(g_log, "%s\n", s.c_str()), std::fflush(g_log);
}

// ---- KilnApi ----
KilnStr KILN_CALL f_version() { static const char v[] = "0.0.0-compathost"; return KilnStr{v, sizeof v - 1}; }
void KILN_CALL f_log(KilnOwner, int32_t level, KilnStr text) {
    static const char* lv[] = {"?", "E", "W", "I", "D", "T"};
    line(std::string("[") + (level >= 1 && level <= 5 ? lv[level] : "?") + "] " + sv(text));
}
KilnResult KILN_CALL f_build_info(KilnBuildInfo* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    out->side = g_side;
    std::strcpy(out->exe_sha256, "0000000000000000000000000000000000000000000000000000000000c0ffee");
    out->verified = 1;
    out->hooks_enabled = 1;
    out->kiln_version = f_version();
    return KILN_OK;
}
bool KILN_CALL f_feature(KilnStr) { return false; }
KilnResult KILN_CALL f_path(int32_t kind, char* buf, size_t cap, size_t* out_len) {
    std::wstring p;
    switch (kind) {
    case KILN_PATH_GAME: p = g_game; break;
    case KILN_PATH_KILN: p = g_game + L"\\kiln"; break;
    case KILN_PATH_MODS: p = g_game + L"\\kiln\\mods"; break;
    case KILN_PATH_LOGS: p = g_game + L"\\kiln\\logs"; break;
    case KILN_PATH_DATA: p = g_game + L"\\kiln\\data"; break;
    case KILN_PATH_CONTROL: p = g_game + L"\\kiln\\control"; break;
    case KILN_PATH_CONFIG: p = g_game + L"\\kiln\\config"; break;
    case KILN_PATH_STATE: p = g_game + L"\\kiln\\state"; break;
    default: return KILN_ERR_INVALID_ARGUMENT;
    }
    const std::string s = u8(p);
    if (out_len) *out_len = s.size();
    if (s.size() > cap) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, s.data(), s.size());
    return KILN_OK;
}
KilnOwner g_next_owner = 1;
KilnOwner KILN_CALL f_owner_create(KilnStr) { return ++g_next_owner; }
void KILN_CALL f_owner_revoke(KilnOwner) {}
KilnResult KILN_CALL f_subscribe_tick(KilnOwner, int32_t, KilnTickFn, void*, KilnHandle* out) { if (out) *out = 1; return KILN_OK; }
KilnResult KILN_CALL f_unsubscribe(KilnHandle) { return KILN_OK; }
KilnResult KILN_CALL f_post(KilnOwner, KilnTaskFn fn, void* user) { if (fn) fn(user); return KILN_OK; }
bool KILN_CALL f_on_tick_thread() { return true; }
KilnResult KILN_CALL f_tick_info(KilnTickInfo*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL f_subscribe_log(KilnOwner, KilnLogLineFn, void*, KilnHandle*) { return KILN_ERR_UNAVAILABLE; }
KilnResult KILN_CALL f_symbol(KilnStr, uintptr_t*) { return KILN_ERR_NOT_FOUND; }
KilnResult KILN_CALL f_constant(KilnStr, int64_t*) { return KILN_ERR_NOT_FOUND; }
const void* KILN_CALL f_service(KilnStr name, uint32_t) {
    auto it = g_services.find(sv(name));
    return it == g_services.end() ? nullptr : it->second;
}
KilnResult KILN_CALL f_register_service(KilnOwner, KilnStr name, const void* table) {
    if (!g_services.emplace(sv(name), table).second) return KILN_ERR_INVALID_ARGUMENT;
    line("[host] service registered: " + sv(name));
    return KILN_OK;
}
uintptr_t KILN_CALL f_server_world() { return 0; }
uintptr_t KILN_CALL f_singleton(uintptr_t, KilnStr) { return 0; }
uint32_t KILN_CALL f_side() { return g_side; }
uintptr_t KILN_CALL f_world(uint32_t) { return 0; }
bool KILN_CALL f_on_world_thread(uint32_t) { return true; }
KilnResult KILN_CALL f_subscribe_world_tick(KilnOwner, uint32_t, int32_t, KilnTickFn, void*, KilnHandle* out) { if (out) *out = 1; return KILN_OK; }
KilnResult KILN_CALL f_post_world(KilnOwner, uint32_t, KilnTaskFn, void*) { return KILN_OK; }
KilnResult KILN_CALL f_subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle* out) { if (out) *out = 1; return KILN_OK; }
KilnResult KILN_CALL f_session(KilnSessionInfo*) { return KILN_ERR_UNAVAILABLE; }

const KilnApi kApi = {sizeof(KilnApi), KILN_ABI_VERSION, &f_version, &f_log, &f_build_info, &f_feature, &f_path,
                      &f_owner_create, &f_owner_revoke, &f_subscribe_tick, &f_unsubscribe, &f_post, &f_on_tick_thread,
                      &f_tick_info, &f_subscribe_log, &f_symbol, &f_constant, &f_service, &f_register_service,
                      &f_server_world, &f_singleton, &f_side, &f_world, &f_on_world_thread, &f_subscribe_world_tick,
                      &f_post_world, &f_subscribe_world, &f_session};

// ---- kiln.profile (from kiln/profile.json; kiln/no-mods selects NO_MODS) ----
uint32_t KILN_CALL p_mode() { return g_profile_mode; }
KilnStr KILN_CALL p_id() { static std::string id = "testprofile"; return g_profile_mode == KILN_PROFILE_ACTIVE ? ks(id) : KilnStr{"", 0}; }
KilnStr KILN_CALL p_name() { return KilnStr{"", 0}; }
uint64_t KILN_CALL p_generation() { return 1; }
uint32_t KILN_CALL p_count(uint32_t) { return 0; }
KilnResult KILN_CALL p_get(uint32_t, uint32_t, KilnProfileMod*) { return KILN_ERR_NOT_FOUND; }
uint32_t KILN_CALL p_dropins() { return g_profile_mode == KILN_PROFILE_LEGACY ? 0xFu : 0u; }
KilnStr KILN_CALL p_config_dir() { return ks(g_cfg_u8); }
KilnStr KILN_CALL p_data_dir() { return ks(g_data_u8); }
KilnStr KILN_CALL p_json() { return ks(g_profile_text); }
const KilnProfileApi kProfile = {{sizeof(KilnProfileApi), KILN_PROFILE_VERSION}, &p_mode, &p_id, &p_name, &p_generation,
                                 &p_count, &p_get, &p_dropins, &p_config_dir, &p_data_dir, &p_json};

bool read_text(const std::wstring& p, std::string* out) {
    FILE* f = _wfopen(p.c_str(), L"rb");
    if (!f) return false;
    char buf[4096];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof buf, f)) > 0) out->append(buf, n);
    std::fclose(f);
    return true;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring e(exe);
    g_game = e.substr(0, e.find_last_of(L'\\'));
    const std::wstring exe_name = e.substr(e.find_last_of(L'\\') + 1);
    g_side = _wcsicmp(exe_name.c_str(), L"enshrouded_server.exe") == 0 ? KILN_SIDE_SERVER : KILN_SIDE_CLIENT;
    CreateDirectoryW((g_game + L"\\kiln").c_str(), nullptr);
    CreateDirectoryW((g_game + L"\\kiln\\logs").c_str(), nullptr);
    CreateDirectoryW((g_game + L"\\kiln\\state").c_str(), nullptr);
    g_log = _wfopen((g_game + L"\\kiln\\logs\\kiln.log").c_str(), L"w");
    if (argc < 3) {
        line("usage: compathost <plugin.dll> <wait ms> [--claim-greeneye] [--load <dll>] [--at <ms> --copy <src> <dst>]");
        return 2;
    }
    const std::wstring plugin = argv[1];
    const int wait_ms = _wtoi(argv[2]);
    int at_ms = -1;
    std::wstring copy_src, copy_dst;
    HANDLE claim = nullptr;
    for (int i = 3; i < argc; ++i) {
        if (!wcscmp(argv[i], L"--claim-greeneye")) {
            wchar_t name[96];
            swprintf_s(name, L"Local\\Greeneye-Loader-%lu", GetCurrentProcessId());
            claim = CreateMutexW(nullptr, FALSE, name);
            line("[host] holding Greeneye Loader's claim");
        } else if (!wcscmp(argv[i], L"--load") && i + 1 < argc) {
            HMODULE h = LoadLibraryW((g_game + L"\\" + argv[++i]).c_str());
            line("[host] preloaded " + u8(argv[i]) + (h ? " ok" : " FAILED"));
        } else if (!wcscmp(argv[i], L"--at") && i + 4 < argc && !wcscmp(argv[i + 2], L"--copy")) {
            at_ms = _wtoi(argv[i + 1]);
            copy_src = g_game + L"\\" + argv[i + 3];
            copy_dst = g_game + L"\\" + argv[i + 4];
            i += 4;
        }
    }
    (void)claim;
    if (GetFileAttributesW((g_game + L"\\kiln\\no-mods").c_str()) != INVALID_FILE_ATTRIBUTES) {
        g_profile_mode = KILN_PROFILE_NO_MODS;
    } else if (read_text(g_game + L"\\kiln\\profile.json", &g_profile_text)) {
        g_profile_mode = KILN_PROFILE_ACTIVE;
        g_cfg_u8 = u8(g_game + L"\\kiln\\config");
        g_data_u8 = u8(g_game + L"\\kiln\\data");
    }
    g_services[KILN_PROFILE_SERVICE] = &kProfile;
    line(std::string("[host] side ") + (g_side == KILN_SIDE_SERVER ? "server" : "client") + ", profile mode " +
         std::to_string(g_profile_mode));

    HMODULE h = LoadLibraryW((g_game + L"\\" + plugin).c_str());
    if (!h) {
        line("[host] RESULT plugin_load=FAILED error " + std::to_string(GetLastError()));
        return 1;
    }
    auto main_fn = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, KILN_PLUGIN_MAIN));
    KilnPluginInfo info{};
    info.size = sizeof info;
    const KilnResult rc = main_fn ? main_fn(&kApi, 1, &info) : KILN_ERR_NOT_FOUND;
    line("[host] RESULT plugin_main=" + std::to_string(rc) + " id=" + sv(info.id) + " version=" + sv(info.version));
    if (rc != KILN_OK) return 1;
    if (info.on_ready) info.on_ready(info.user);
    line("[host] on_ready returned");

    const DWORD t0 = GetTickCount();
    bool copied = false;
    while (static_cast<int>(GetTickCount() - t0) < wait_ms) {
        if (at_ms >= 0 && !copied && static_cast<int>(GetTickCount() - t0) >= at_ms) {
            copied = CopyFileW(copy_src.c_str(), copy_dst.c_str(), FALSE) != 0;
            line(std::string("[host] copied at ") + std::to_string(GetTickCount() - t0) + " ms: " + (copied ? "ok" : "FAILED"));
        }
        Sleep(20);
    }

    // ---- checks ----
    line("[host] RESULT gt_target=" + std::to_string(gt_target(2, 3) - 0x5A17C0DE));
    line("[host] RESULT gt_caller=" + std::to_string(gt_caller(2, 3)));
    using FindFn = uint8_t* (*)(const char*);
    using ReadFn = void (*)(const void*, void*, size_t);
    if (auto find = reinterpret_cast<FindFn>(GetProcAddress(h, "kiln_greeneye_test_find"))) {
        auto read = reinterpret_cast<ReadFn>(GetProcAddress(h, "kiln_greeneye_test_read"));
        uint8_t* p = find("8D 04 11 05 DE C0 17 5A 90 90 90 90 C3");
        line(std::string("[host] RESULT find_target=") + (p == reinterpret_cast<uint8_t*>(&gt_target) ? "ok" : "wrong"));
        line(std::string("[host] RESULT find_wildcard=") +
             (find("8D 04 11 05 ?? C0 17 5A 90") == reinterpret_cast<uint8_t*>(&gt_target) ? "ok" : "wrong"));
        line(std::string("[host] RESULT find_ambiguous=") + (find("90 90") == nullptr ? "null" : "found"));
        uint8_t orig[13] = {}, live[13] = {};
        read(reinterpret_cast<const void*>(&gt_target), orig, sizeof orig);
        std::memcpy(live, reinterpret_cast<const void*>(&gt_target), sizeof live);
        const uint8_t want[13] = {0x8D, 0x04, 0x11, 0x05, 0xDE, 0xC0, 0x17, 0x5A, 0x90, 0x90, 0x90, 0x90, 0xC3};
        line(std::string("[host] RESULT read_original=") + (std::memcmp(orig, want, 13) == 0 ? "ok" : "wrong") +
             " live_patched=" + (std::memcmp(live, want, 12) != 0 ? "yes" : "no"));
    }
    if (auto* flame = static_cast<const KilnFlameApi*>(f_service(KilnStr{KILN_FLAME_SERVICE, sizeof(KILN_FLAME_SERVICE) - 1}, 1))) {
        for (uint32_t i = 0; i < flame->count(); ++i) {
            KilnFlameMod m{};
            m.size = sizeof m;
            if (flame->get(i, &m) == KILN_OK)
                line("[host] RESULT flame_mod " + sv(m.id) + " status=" + std::to_string(m.status));
        }
        line(std::string("[host] RESULT flame_running_ftest=") + (flame->running(KilnStr{"ftest", 5}) ? "yes" : "no"));
        line(std::string("[host] RESULT flame_stepped_aside=") + (flame->stepped_aside() ? "yes" : "no"));
    }
    if (info.on_unload) info.on_unload(info.user);
    line("[host] RESULT done");
    std::fflush(stdout);
    if (g_log) std::fclose(g_log);
    // Plugins leave threads behind (Shroudtopia's loop); end the process the way the game does.
    TerminateProcess(GetCurrentProcess(), 0);
    return 0;
}
