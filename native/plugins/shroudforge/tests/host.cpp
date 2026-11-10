// SPDX-License-Identifier: MIT
// Fake runtime host only: never launches or loads Enshrouded.
#include <windows.h>
#include <filesystem>
#include <string>
#include <cstdio>
#include <cstring>
#include <vector>
#include "kiln_api.h"
#include "services/kiln_hooks.h"
#include "services/kiln_profile.h"
static std::filesystem::path game;
static KilnResult path(int32_t kind, char* out, size_t cap, size_t* len) {
    auto p = game;
    if (kind == KILN_PATH_KILN) p /= L"kiln";
    if (kind == KILN_PATH_STATE) p /= L"kiln/state";
    const auto u = p.u8string(); *len = u.size();
    if (cap < u.size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(out, u.data(), u.size()); return KILN_OK;
}
static void log(KilnOwner, int32_t, KilnStr text) { std::printf("%.*s\n", int(text.len), text.ptr); }
// A fake kiln.hooks v2 (KILN_SF_FAKE_HOOKS=1): one "game" place at 0x1000 whose file bytes are AA BB CC DD EE FF 00 11 22 33
// 44 55; "11 22 33 44 55 66" is ambiguous; mid2 links are kept so main() can fire them with made-up registers.
struct Link { KilnMidFn fn; void* user; std::string label; uintptr_t target; };
static std::vector<Link> links;
static KilnHooksApi fake{};
static std::string text(KilnStr s) { return std::string(s.ptr, s.len); }
static const unsigned char file_bytes[16] = {0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0, 0, 0, 0};
static KilnResult f_scan(KilnStr pattern, uintptr_t* out, uint32_t cap, uint32_t* count) {
    const std::string p = text(pattern);
    if (p == "AA BB CC DD EE FF") { out[0] = 0x1000; *count = 1; return KILN_OK; }
    if (p == "11 22 33 44 55 66") { out[0] = 0x2000; out[1] = 0x3000; *count = 2; return KILN_OK; }
    *count = 0;
    return KILN_ERR_NOT_FOUND;
}
static KilnResult f_scan_hole(KilnStr pattern, uintptr_t match, KilnStr hole, int64_t* out) {
    const std::string h = text(hole);
    if (match != 0x1000 || h.size() < 2 || h[0] != 'h') return KILN_ERR_NOT_FOUND;
    const size_t i = std::stoul(h.substr(1));
    if (i * 4 + 4 > sizeof file_bytes) return KILN_ERR_NOT_FOUND;
    uint32_t v;
    std::memcpy(&v, file_bytes + i * 4, 4);
    *out = v;
    (void)pattern;
    return KILN_OK;
}
static KilnResult f_mid2(KilnOwner owner, uintptr_t target, const KilnHookOrder* order, KilnMidFn fn, void* user, KilnHandle* out) {
    if (target != 0x1000) return KILN_ERR_INVALID_ARGUMENT;
    links.push_back({fn, user, text(order->label), target});
    *out = 0x100 + links.size();
    std::printf("[fake kiln.hooks] mid2 owner=%llu label=%s target=0x%llx\n", (unsigned long long)owner, links.back().label.c_str(), (unsigned long long)target);
    return KILN_OK;
}
static KilnResult f_revoke(KilnHandle h) { std::printf("[fake kiln.hooks] revoke %llu\n", (unsigned long long)h); return KILN_OK; }
static uintptr_t f_image_base() { return 0; }
// A fake kiln.profile (KILN_SF_PROFILE=<file>): an active profile whose text is that file.
static std::string profile_text;
static uint32_t p_mode() { return KILN_PROFILE_ACTIVE; }
static KilnStr p_json() { return KilnStr{profile_text.data(), profile_text.size()}; }
static KilnProfileApi fake_profile{};
static const void* service(KilnStr name, uint32_t min) {
    if (text(name) == KILN_PROFILE_SERVICE) {
        wchar_t file[1024];
        const DWORD n = GetEnvironmentVariableW(L"KILN_SF_PROFILE", file, 1024);
        if (!n || n >= 1024 || min > 1) return nullptr;
        if (FILE* f = _wfopen(file, L"rb")) {
            char buf[4096];
            size_t got;
            profile_text.clear();
            while ((got = std::fread(buf, 1, sizeof buf, f)) > 0) profile_text.append(buf, got);
            std::fclose(f);
        }
        fake_profile.header = {sizeof fake_profile, KILN_PROFILE_VERSION};
        fake_profile.mode = p_mode;
        fake_profile.json = p_json;
        return &fake_profile;
    }
    if (!GetEnvironmentVariableW(L"KILN_SF_FAKE_HOOKS", nullptr, 0) || text(name) != KILN_HOOKS_SERVICE || min > 2) return nullptr;
    fake.header = {sizeof fake, 2};
    fake.image_base = f_image_base;
    fake.scan = f_scan;
    fake.scan_hole = f_scan_hole;
    fake.mid2 = f_mid2;
    fake.revoke = f_revoke;
    return &fake;
}
static uint32_t side() { return KILN_SIDE_SERVER; }
int main(int argc, char** argv) {
    if (argc < 3) return 2;
    game = std::filesystem::absolute(argv[1]);
    if (GetEnvironmentVariableW(L"KILN_SF_BUSY_LOCK", nullptr, 0)) {
        HANDLE lock = CreateFileW((game / L".kiln-eml.lock").c_str(), GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        OVERLAPPED overlapped{};
        if (lock == INVALID_HANDLE_VALUE || !LockFileEx(lock, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY,
                                                       0, MAXDWORD, MAXDWORD, &overlapped)) return 7;
        // Deliberately retained while the adapter tries a different handle, simulating another process's lock.
    }
    auto dll = std::filesystem::absolute(argv[2]);
    if (argc > 3 && std::string(argv[3]) == "early") LoadLibraryW((game / L"kiln/plugins/kiln-eml.dll").c_str());
    HMODULE h = LoadLibraryW(dll.c_str()); if (!h) return 3;
    auto fn = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, argc > 3 ? "kiln_plugin_early" : "kiln_plugin_main"));
    if (!fn) return 4;
    KilnApi api{}; api.size = sizeof api; api.abi_version = KILN_ABI_VERSION;
    api.path = path; api.log = log; api.service = service; api.side = side;
    KilnPluginInfo info{}; info.size = sizeof info;
    if (fn(&api, 1, &info) != KILN_OK) return 5;
    if (info.on_ready) info.on_ready(info.user);
    Sleep(120);
    // What the game would do: run each hook with registers of its own (r15 0x15, rbp 0xbb, rcx 0xc7, r14 0x14, r13 0x13).
    for (const Link& l : links) {
        KilnRegisters regs{};
        regs.r15 = 0x15, regs.rbp = 0xbb, regs.rcx = 0xc7, regs.r14 = 0x14, regs.r13 = 0x13;
        l.fn(&regs, l.user);
    }
    // The core may call normal main again after the early pass; it must not prepare or start a second worker.
    if (argc > 3) {
        auto normal = reinterpret_cast<KilnPluginMainFn>(GetProcAddress(h, "kiln_plugin_main"));
        info = {}; info.size = sizeof info;
        if (normal(&api, 2, &info) != KILN_OK) return 6;
        if (info.on_ready) info.on_ready(info.user);
    }
    if (info.on_unload) info.on_unload(info.user);
    Sleep(150); // allow signalled, serial worker cleanup before the fake host exits
    return 0;
}
