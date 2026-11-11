// SPDX-License-Identifier: MIT
// compathost.exe: a stand-in for the game and kiln-core for the compatibility plugins (kiln-greeneye, kiln-flame,
// kiln-shroudtopia). run.py copies it into a scratch game folder as enshrouded.exe or enshrouded_server.exe and runs
// it under plain Wine:
//   compathost.exe <plugin.dll> <wait ms> [--claim-greeneye] [--load <dll>] [--hooks-full|--hooks-refuse] [--at <ms> --copy <src> <dst>]
// It serves a minimal KilnApi (log, paths, side, build info, kiln.profile from kiln/profile.json, a kiln.hooks v2 stand-in
// that places raw_detour links with safetyhook, register_service / service), loads the plugin, runs kiln_plugin_main and on_ready, waits, then calls the test targets below and prints
// RESULT lines that run.py checks together with kiln/logs/kiln.log and kiln/state/*.json.
#include <windows.h>

#include <cstddef>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_hooks.h"
#include "services/kiln_profile.h"
#include "../../flame/include/kiln_flame.h"
#include "../compat.h"
#include "safetyhook.hpp"

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
static safetyhook::InlineHook g_prehook;
static int prehook_target(int a, int b) { return g_prehook.call<int>(a, b) + 7; }
extern "C" __declspec(dllexport) int gt_prehook_delta() { return g_prehook ? 7 : 0; }

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

// ---- kiln.hooks v2 stand-in: raw_detour over safetyhook (the real registry has its own tests: native/core/tests) ----
// Like the registry it accepts only targets inside the exe image (the others get KILN_ERR_INVALID_ARGUMENT), writes the
// continuation to *original before the hook is live, and never removes a link. Counts links and refusals for RESULT lines.
std::vector<std::unique_ptr<safetyhook::InlineHook>> g_links;
std::mutex g_links_mu;
int g_raw_links = 0, g_raw_outside = 0, g_raw_refused = 0, g_raw_unordered = 0;
// --hooks-full: every target slot is taken (an address in the exe is refused with KILN_ERR_UNAVAILABLE and stats() says
// the table is full); --hooks-refuse: the same refusal without a full table (the detour could not be placed).
enum { kHooksNormal = 0, kHooksFull = 1, kHooksRefuse = 2 } g_hooks_mode = kHooksNormal;
constexpr uint32_t kTargetCap = 512;
KilnResult KILN_CALL hk_raw_detour(KilnOwner, uintptr_t target, const KilnHookOrder* order, void* fn, void** original,
                                   KilnHandle* out) {
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
    std::lock_guard lock(g_links_mu);
    if (!fn || target < base || target >= base + nt->OptionalHeader.SizeOfImage) {
        ++g_raw_outside;
        return KILN_ERR_INVALID_ARGUMENT;
    }
    // what mod links must ask for: one priority above Kiln's own, newest first (kiln-emf asks the same)
    if (order && order->size >= offsetof(KilnHookOrder, flags) + sizeof(order->flags) &&
        (order->priority != 1 || !(order->flags & KILN_HOOK_NEWEST_FIRST)))
        ++g_raw_unordered;
    if (g_hooks_mode != kHooksNormal) {
        ++g_raw_refused;
        return KILN_ERR_UNAVAILABLE;
    }
    auto hook = safetyhook::InlineHook::create(reinterpret_cast<void*>(target), fn, safetyhook::InlineHook::StartDisabled);
    if (!hook) return KILN_ERR_UNAVAILABLE;
    auto h = std::make_unique<safetyhook::InlineHook>(std::move(*hook));
    if (original) *original = h->original<void*>();  // before the link can be reached
    if (!h->enable()) return KILN_ERR_UNAVAILABLE;
    g_links.push_back(std::move(h));
    ++g_raw_links;
    if (out) *out = 0x4B484F4F00000000ull + static_cast<uint64_t>(g_links.size());
    return KILN_OK;
}
uintptr_t KILN_CALL hk_caller(KilnHandle) { return 0; }
KilnResult KILN_CALL hk_revoke(KilnHandle) { return KILN_OK; }
KilnResult KILN_CALL hk_stats(KilnHooksStats* out) {
    if (!out || out->size < offsetof(KilnHooksStats, targets_cap) + sizeof(out->targets_cap)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_links_mu);
    out->targets = g_hooks_mode == kHooksFull ? kTargetCap : static_cast<uint32_t>(g_raw_links);
    out->targets_cap = kTargetCap;
    return KILN_OK;
}
KilnHooksApi make_hooks() {
    KilnHooksApi t{};
    t.header = {sizeof(KilnHooksApi), KILN_HOOKS_VERSION};
    t.stats = &hk_stats;
    t.raw_detour = &hk_raw_detour;
    t.caller = &hk_caller;
    t.revoke = &hk_revoke;
    return t;
}
const KilnHooksApi kHooks = make_hooks();

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
// The emf list of kiln/profile.json as the core serves it (absolute paths in the test profiles); other kinds: none.
struct HostEntry { std::string id, version, path; bool enabled = true; };
std::vector<HostEntry> g_emf_entries;
uint32_t g_dropin_bits = 0;
void parse_profile_lists() {
    const auto doc = nlohmann::json::parse(g_profile_text, nullptr, false);
    if (!doc.is_object()) return;
    if (doc.contains("dropins")) {
        const auto& d = doc["dropins"];
        if (d.is_boolean() && d.get<bool>()) g_dropin_bits = 0xFu;
        if (d.is_array())
            for (const auto& k : d)
                if (k.is_string() && k.get<std::string>() == "emf") g_dropin_bits |= 1u << KILN_PROFILE_KIND_EMF;
    }
    if (!doc.contains("mods") || !doc["mods"].is_object() || !doc["mods"].contains("emf") || !doc["mods"]["emf"].is_array()) return;
    for (const auto& m : doc["mods"]["emf"]) {
        if (!m.is_object()) continue;
        HostEntry e;
        e.id = m.value("id", std::string());
        e.version = m.value("version", std::string());
        e.path = m.value("path", std::string());
        e.enabled = m.value("enabled", true);
        g_emf_entries.push_back(std::move(e));
    }
}
uint32_t KILN_CALL p_count(uint32_t kind) {
    return kind == KILN_PROFILE_KIND_EMF && g_profile_mode == KILN_PROFILE_ACTIVE ? static_cast<uint32_t>(g_emf_entries.size()) : 0;
}
KilnResult KILN_CALL p_get(uint32_t kind, uint32_t index, KilnProfileMod* out) {
    if (!out || kind != KILN_PROFILE_KIND_EMF || index >= p_count(kind)) return KILN_ERR_NOT_FOUND;
    const auto& e = g_emf_entries[index];
    out->kind = kind;
    out->id = ks(e.id);
    out->version = ks(e.version);
    out->path = ks(e.path);
    out->enabled = e.enabled ? 1u : 0u;
    return KILN_OK;
}
uint32_t KILN_CALL p_dropins() { return g_profile_mode == KILN_PROFILE_LEGACY ? 0xFu : g_dropin_bits; }
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

void shared_checks() {
    namespace fs = std::filesystem;
    auto check = [](const char* name, bool ok) { line(std::string("[compat] CHECK ") + name + (ok ? " ok" : " fail")); };
    const fs::path root = fs::path(g_game) / L"kiln";
    const fs::path file = root / L"export-probe.dll";
    KilnServiceHeader short_table{sizeof(KilnServiceHeader), 1};
    g_services["short.service"] = &short_table;
    kcompat::Host host{&kApi, 1, "compat"};
    check("short_service_refused", host.service<KilnProfileApi>("short.service", 1) == nullptr);
    g_services.erase("short.service");
    struct WriteJob { fs::path file; std::string data; bool ok = true; } jobs[] = {
        {root / L"atomic-probe.txt", std::string(1024, 'A')}, {root / L"atomic-probe.txt", std::string(1024, 'B')}};
    HANDLE writers[2]{};
    for (size_t i = 0; i < 2; ++i)
        writers[i] = CreateThread(nullptr, 0, [](void* arg) -> DWORD {
            auto& job = *static_cast<WriteJob*>(arg);
            for (int j = 0; j < 16; ++j) job.ok &= kcompat::write_file_atomic(job.file, job.data);
            return 0;
        }, &jobs[i], 0, nullptr);
    for (HANDLE t : writers) if (t) WaitForSingleObject(t, INFINITE), CloseHandle(t);
    std::string final_text;
    check("concurrent_atomic_writes", writers[0] && writers[1] && jobs[0].ok && jobs[1].ok);
    check("atomic_file_complete", kcompat::read_file(jobs[0].file, &final_text) &&
                                     (final_text == jobs[0].data || final_text == jobs[1].data));
    DeleteFileW(jobs[0].file.c_str());
    std::string image(0x600, '\0');
    auto w16 = [&](size_t o, uint16_t n) { image[o] = char(n); image[o + 1] = char(n >> 8); };
    auto w32 = [&](size_t o, uint32_t n) { w16(o, uint16_t(n)); w16(o + 2, uint16_t(n >> 16)); };
    image[0] = 'M'; image[1] = 'Z'; w32(0x3c, 0x80); w32(0x80, 0x4550);
    w16(0x84, IMAGE_FILE_MACHINE_AMD64); w16(0x86, 1); w16(0x94, 240); w16(0x96, IMAGE_FILE_DLL);
    w16(0x98, 0x20b); w32(0x98 + 108, 16); w32(0x98 + 112, 0x1000); w32(0x98 + 116, 0x100);
    w32(0x188 + 8, 0x400); w32(0x188 + 12, 0x1000); w32(0x188 + 16, 0x400); w32(0x188 + 20, 0x200);
    w32(0x188 + 36, IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ);
    w32(0x200 + 20, 1); w32(0x200 + 24, 1); w32(0x200 + 28, 0x1060);
    w32(0x200 + 32, 0x1050); w32(0x200 + 36, 0x1058); w32(0x250, 0x1080);
    w32(0x260, 0x1200); std::memcpy(image.data() + 0x280, "CreateModInstance", 18); image[0x400] = char(0xc3);
    const std::string valid = image;
    auto exported = [&] { kcompat::write_file_atomic(file, image); return kcompat::pe_exports_name(file, "CreateModInstance"); };
    check("callable_export", exported());
    w32(0x3c, 0xfffffff0); check("pe_offset_overflow", !exported()); image = valid;
    w16(0x94, 2); check("short_optional_header", !exported()); image = valid;
    w32(0x200 + 24, 0xffffffff); check("export_count_bounds", !exported()); image = valid;
    w16(0x258, 1); check("export_ordinal_bounds", !exported()); image = valid;
    w32(0x260, 0); check("null_export", !exported()); image = valid;
    w32(0x260, 0x1080); check("forwarded_export", !exported()); image = valid;
    w32(0x188 + 36, IMAGE_SCN_MEM_READ); check("data_export", !exported()); image = valid;
    w32(0x188 + 8, 0x800); w32(0x260, 0x1500); check("virtual_tail_export", !exported()); image = valid;
    w32(0x188 + 20, 0xfffffff0); check("raw_offset_bounds", !exported()); image = valid;
    w32(0x250, 0x13ff); image[0x5ff] = 'X'; check("unterminated_export", !exported()); image = valid;
    w32(0x200 + 20, 2); w32(0x200 + 24, 2); w32(0x254, 0x1090); w16(0x25a, 1); w32(0x264, 0x1200);
    std::memcpy(image.data() + 0x280, "Zzz\0", 4); std::memcpy(image.data() + 0x290, "CreateModInstance", 18);
    check("unsorted_export_names", !exported());
    std::memcpy(image.data() + 0x280, "CreateModInstance", 18); w32(0x260, 0x1080);
    check("duplicate_forwarded_export", !exported()); image = valid;
    exported();
    {
        kcompat::CheckedDll checked;
        check("checked_dll", checked.open(root, file, "CreateModInstance"));
        HANDLE writer = CreateFileW(file.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        check("dll_write_locked", writer == INVALID_HANDLE_VALUE);
        if (writer != INVALID_HANDLE_VALUE) CloseHandle(writer);
        check("dll_rename_locked", MoveFileExW(file.c_str(), (root / L"moved.dll").c_str(), MOVEFILE_REPLACE_EXISTING) == 0);
    }
    {
        kcompat::CheckedDll checked;
        check("root_trailing_slash", checked.open(fs::path(root.native() + L"\\"), file, "CreateModInstance"));
    }
    {
        kcompat::CheckedDll checked;
        check("outside_dll", !checked.open(root / L"logs", file, "CreateModInstance"));
    }
    DeleteFileW(file.c_str());
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
    int free_ms = -1, load_ms = -1;
    bool prehook = false;
    std::wstring load_later;
    std::wstring copy_src, copy_dst;
    HANDLE claim = nullptr;
    for (int i = 3; i < argc; ++i) {
        if (!wcscmp(argv[i], L"--claim-greeneye")) {
            wchar_t name[96];
            swprintf_s(name, L"Local\\Greeneye-Loader-%lu", GetCurrentProcessId());
            claim = CreateMutexW(nullptr, FALSE, name);
            line("[host] holding Greeneye Loader's claim");
        } else if (!wcscmp(argv[i], L"--prehook")) {
            prehook = true;
        } else if (!wcscmp(argv[i], L"--hooks-full")) {
            g_hooks_mode = kHooksFull;
        } else if (!wcscmp(argv[i], L"--hooks-refuse")) {
            g_hooks_mode = kHooksRefuse;
        } else if (!wcscmp(argv[i], L"--load") && i + 1 < argc) {
            HMODULE h = LoadLibraryW((g_game + L"\\" + argv[++i]).c_str());
            line("[host] preloaded " + u8(argv[i]) + (h ? " ok" : " FAILED"));
        } else if (!wcscmp(argv[i], L"--free-at") && i + 1 < argc) {
            free_ms = _wtoi(argv[++i]);
        } else if (!wcscmp(argv[i], L"--load-at") && i + 2 < argc) {
            load_ms = _wtoi(argv[++i]);
            load_later = argv[++i];
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
        parse_profile_lists();
    }
    g_services[KILN_PROFILE_SERVICE] = &kProfile;
    g_services[KILN_HOOKS_SERVICE] = &kHooks;
    line(std::string("[host] side ") + (g_side == KILN_SIDE_SERVER ? "server" : "client") + ", profile mode " +
         std::to_string(g_profile_mode));
    shared_checks();
    if (prehook) {
        g_prehook = safetyhook::create_inline(reinterpret_cast<void*>(&gt_target), reinterpret_cast<void*>(&prehook_target));
        line(std::string("[host] RESULT prehook=") + (g_prehook ? "ok" : "FAILED"));
    }

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
    bool copied = false, freed = false, late_loaded = false;
    while (static_cast<int>(GetTickCount() - t0) < wait_ms) {
        if (at_ms >= 0 && !copied && static_cast<int>(GetTickCount() - t0) >= at_ms) {
            copied = CopyFileW(copy_src.c_str(), copy_dst.c_str(), FALSE) != 0;
            line(std::string("[host] copied at ") + std::to_string(GetTickCount() - t0) + " ms: " + (copied ? "ok" : "FAILED"));
        }
        if (load_ms >= 0 && !late_loaded && static_cast<int>(GetTickCount() - t0) >= load_ms) {
            HMODULE late = LoadLibraryW((g_game + L"\\" + load_later).c_str());
            line(std::string("[host] RESULT late_loader=") + (late ? "ok" : "FAILED"));
            late_loaded = true;
        }
        if (free_ms >= 0 && !freed && static_cast<int>(GetTickCount() - t0) >= free_ms) {
            if (info.on_unload) info.on_unload(info.user);
            FreeLibrary(h);
            line(std::string("[host] RESULT plugin_pinned=") + (GetModuleHandleW(plugin.c_str()) ? "yes" : "no"));
            if (HMODULE mod = GetModuleHandleW((g_game + L"\\mods\\test.dll").c_str())) {
                if (auto restored = reinterpret_cast<bool (*)()>(GetProcAddress(mod, "kiln_shroudtopia_test_patch_restored")))
                    line(std::string("[host] RESULT patch_restored=") + (restored() ? "yes" : "no"));
            }
            freed = true;
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
        if (flame->header.size >= offsetof(KilnFlameApi, symbols) + sizeof(void*) && flame->symbols) {
            KilnStr file{};
            const uint32_t st = flame->symbols(KilnStr{"ftest", 5}, &file);
            line("[host] RESULT flame_symbols_ftest=" + std::to_string(st) + " file=" + sv(file));
        }
    }
    if (!freed && info.on_unload) info.on_unload(info.user);
    {
        std::lock_guard lock(g_links_mu);
        line("[host] RESULT hooks_raw_links=" + std::to_string(g_raw_links));
        line("[host] RESULT hooks_outside_image=" + std::to_string(g_raw_outside));
        line("[host] RESULT hooks_refused=" + std::to_string(g_raw_refused));
        line("[host] RESULT hooks_unordered=" + std::to_string(g_raw_unordered));
    }
    line("[host] RESULT done");
    std::fflush(stdout);
    if (g_log) std::fclose(g_log);
    // Plugins leave threads behind (Shroudtopia's loop); end the process the way the game does.
    TerminateProcess(GetCurrentProcess(), 0);
    return 0;
}
