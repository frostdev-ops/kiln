// SPDX-License-Identifier: MIT
#include <windows.h>
#include <cstdio>
#include <cmath>
#include <cstdint>
#include <string>
#include "services/kiln_eml.h"
// One line per call. Several threads write (the mods worker, kiln.ui's window thread): an append-only handle and one
// WriteFile per line, so no line overwrites another (the CRT's "a" mode seeks to the end, then writes).
static void mark(const char* what) {
    wchar_t file[32768]{};
    GetEnvironmentVariableW(L"KILN_SF_TEST_LOG", file, 32768);
    HANDLE f = CreateFileW(file, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    const std::string line = std::string(what) + "\n";
    DWORD written = 0;
    WriteFile(f, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    CloseHandle(f);
}
static void timing(const char* operation, double dt = 0) {
    if (!GetEnvironmentVariableW(L"KILN_SF_SLOW_HEARTBEAT", nullptr, 0)) return;
    char text[160];
    std::snprintf(text, sizeof text, "timing %s %llu %lu %.9f", operation, GetTickCount64(), GetCurrentThreadId(), dt);
    mark(text);
}
#if TEST_bootstrap
extern "C" __declspec(dllexport) void ShroudforgeStop() {}
#elif TEST_runtime || TEST_runtime_feed || TEST_runtime_feed_partial
#if TEST_runtime_feed || TEST_runtime_feed_partial
// The host key feed (the fork's kiln/abi/input_host.h): every call is written down. A call before begin, after end or
// after shroudforge_destroy is written as "feed-out-of-turn <what>". runtime_feed_partial lacks one of the seven.
// KILN_SF_FEED_VERSION, KILN_SF_FEED_BEGIN: what version() and begin() answer (default 1). KILN_SF_FEED_SLOW: each call
// takes 2 ms (teardown races).
#include <atomic>
#include <cstring>
static std::atomic<int> feed_state{0};  // 0 before begin, 1 fed, 2 ended
static std::atomic<bool> destroyed{false};
static uint32_t env_number(const wchar_t* name, uint32_t fallback) {
    wchar_t v[16] = L"";
    return GetEnvironmentVariableW(name, v, 16) ? static_cast<uint32_t>(_wtoi(v)) : fallback;
}
static void feed_mark(const char* what, bool needs_feed = true) {
    if (GetEnvironmentVariableW(L"KILN_SF_FEED_SLOW", nullptr, 0)) Sleep(2);
    if (needs_feed && (feed_state.load() != 1 || destroyed.load())) {
        char text[160];
        std::snprintf(text, sizeof text, "feed-out-of-turn %s", what);
        mark(text);
        return;
    }
    mark(what);
    char fault[16]{};
    const DWORD n = GetEnvironmentVariableA("KILN_SF_FEED_FAULT", fault, sizeof fault);
    if (n && n < sizeof fault && std::strncmp(what + 5, fault, n) == 0 &&
        (what[5 + n] == '\0' || what[5 + n] == ' '))
        RaiseException(0xC0000005, 0, 0, nullptr);
}
extern "C" __declspec(dllexport) uint32_t shroudforge_input_host_version() {
    feed_mark("feed-version", false);
    return env_number(L"KILN_SF_FEED_VERSION", 1);
}
extern "C" __declspec(dllexport) uint32_t shroudforge_input_host_begin() {
    feed_mark("feed-begin", false);
    const uint32_t took = env_number(L"KILN_SF_FEED_BEGIN", 1);
    if (took == 1) feed_state = 1;
    return took;
}
extern "C" __declspec(dllexport) void shroudforge_input_host_raw_key(uint32_t make_code, uint32_t flags, uint32_t vkey) {
    char text[64];
    std::snprintf(text, sizeof text, "feed-raw %x %x %x", make_code, flags, vkey);
    feed_mark(text);
}
extern "C" __declspec(dllexport) void shroudforge_input_host_window_key(uintptr_t wparam, intptr_t lparam) {
    char text[64];
    std::snprintf(text, sizeof text, "feed-window %llx %llx", (unsigned long long)wparam, (unsigned long long)lparam);
    feed_mark(text);
}
#if !TEST_runtime_feed_partial
extern "C" __declspec(dllexport) void shroudforge_input_host_mouse_button(uint32_t button, uint32_t down) {
    char text[64];
    std::snprintf(text, sizeof text, "feed-mouse %u %u", button, down);
    feed_mark(text);
}
#endif
extern "C" __declspec(dllexport) void shroudforge_input_host_reset() { feed_mark("feed-reset"); }
extern "C" __declspec(dllexport) void shroudforge_input_host_end() {
    feed_mark("feed-end");
    feed_state = 2;
}
#endif
extern "C" __declspec(dllexport) bool shroudforge_prepare_startup(const wchar_t* root) { mark(root && *root ? "prepare" : "bad-root"); return root && *root; }
extern "C" __declspec(dllexport) void* shroudforge_create(const wchar_t* root) {
    mark("create");
    // A new line in ShroudForge's log after the adapter's start, so its [sf] mirror has something to read in the loop.
    if (root && GetEnvironmentVariableW(L"KILN_SF_SLOW_HEARTBEAT", nullptr, 0)) {
        const std::wstring logs = std::wstring(root) + L"\\shroudforge\\logs";
        CreateDirectoryW((std::wstring(root) + L"\\shroudforge").c_str(), nullptr);
        CreateDirectoryW(logs.c_str(), nullptr);
        if (FILE* f = _wfopen((logs + L"\\shroudforge-server.log").c_str(), L"ab")) {
            std::fprintf(f, "[I] fake runtime created\n");
            std::fclose(f);
        }
    }
    return reinterpret_cast<void*>(1);
}
extern "C" __declspec(dllexport) bool shroudforge_update(void* h, double dt) {
    timing("update", dt);
    mark("update");
    if (GetEnvironmentVariableW(L"KILN_SF_STALL_UPDATE", nullptr, 0)) Sleep(4400);  // past the writer's second turn (host.cpp)
    if (GetEnvironmentVariableW(L"KILN_SF_FAULT_UPDATE", nullptr, 0)) RaiseException(0xc0000005, 0, 0, nullptr);
    return h && std::isfinite(dt) && dt >= 0;
}
extern "C" __declspec(dllexport) void shroudforge_destroy(void*) {
    timing("destroy");
    mark("destroy");
#if TEST_runtime_feed || TEST_runtime_feed_partial
    destroyed = true;
#endif
}
#elif TEST_eml
static KilnResult reserve(KilnStr reason) {
    mark(reason.ptr && reason.len ? "reserve" : "bad-reason");
    return GetEnvironmentVariableW(L"KILN_SF_DENY_RESERVATION", nullptr, 0) ? KILN_ERR_UNAVAILABLE : KILN_OK;
}
static KilnEmlApi table{{sizeof(KilnEmlApi), 2}, nullptr, nullptr, nullptr, reserve};
extern "C" __declspec(dllexport) const KilnEmlApi* kiln_eml_service(uint32_t version) {
    if (GetEnvironmentVariableW(L"KILN_SF_SHORT_EML_TABLE", nullptr, 0)) table.header.size = offsetof(KilnEmlApi, stand_aside);
    return version <= 2 ? &table : nullptr;
}
#else
extern "C" __declspec(dllexport) uint32_t KfcRuntimeAbi() { 
#if TEST_bad_kfc
return 11;
#else
return 12;
#endif
 }
#if TEST_good_kfc
// The host interface (docs/kiln/shroudforge-compat.md): what the bundled runtime does with the table the adapter offers.
struct Host {
    uint32_t size, version;
    uint32_t(__cdecl* find_signature)(const char*, const unsigned char*, size_t, uintptr_t*);
    uint64_t(__cdecl* install_hook)(const char*, uintptr_t, uint32_t, void*, uintptr_t);
    void(__cdecl* remove_hook)(uint64_t);
};
static Host host{};
static uint64_t handles[3];
static void logf2(const char* fmt, unsigned long long a, unsigned long long b, unsigned long long c) {
    char text[128];
    std::snprintf(text, sizeof text, fmt, a, b, c);
    mark(text);
}
static void __cdecl cb_plain(void* a, void* b) { logf2("cb-plain %llx %llx %llx", (unsigned long long)a, (unsigned long long)b, 0); }
static void __cdecl cb_context(void* a, void* b) { logf2("cb-context %llx %llx %llx", (unsigned long long)a, (unsigned long long)b, 0); }
static void __cdecl cb_cursor(void* a, void* b, void* c) { logf2("cb-cursor %llx %llx %llx", (unsigned long long)a, (unsigned long long)b, (unsigned long long)c); }
extern "C" __declspec(dllexport) bool __cdecl KfcRuntimeSetHost(const Host* offered) {
    if (GetEnvironmentVariableW(L"KILN_SF_REFUSE_HOST", nullptr, 0)) { mark("set-host-refused"); return false; }
    if (!offered || offered->size < sizeof(Host) || offered->version != 1) return false;
    host = *offered;
    mark("set-host");
    return true;
}
#endif
extern "C" __declspec(dllexport) bool KfcRuntimeInitialize() {
    mark("init");
#if TEST_good_kfc
    if (host.find_signature) {
        const unsigned char expected[4] = {0xAA, 0xBB, 0xCC, 0xDD}, wrong[4] = {0xAA, 0xBB, 0xCC, 0xDE};
        uintptr_t at = 0;
        const unsigned long long found = host.find_signature("AA BB CC DD EE FF", expected, 4, &at);
        logf2("find %llx %llx %llx", found, at, 0);
        logf2("find-mismatch %llx %llx %llx", host.find_signature("AA BB CC DD EE FF", wrong, 4, &at), 0, 0);
        logf2("find-ambiguous %llx %llx %llx", host.find_signature("11 22 33 44 55 66", nullptr, 0, &at), 0, 0);
        logf2("find-none %llx %llx %llx", host.find_signature("01 02 03", nullptr, 0, &at), 0, 0);
        handles[0] = host.install_hook("game_thread", at, 0, reinterpret_cast<void*>(&cb_plain), 0);
        handles[1] = host.install_hook("world_building_dispatch", at, 1, reinterpret_cast<void*>(&cb_context), 0);
        handles[2] = host.install_hook("world_cursor", at, 2, reinterpret_cast<void*>(&cb_cursor), 0x270);
        logf2("installed %llx %llx %llx", handles[0] != 0, handles[1] != 0, handles[2] != 0);
    }
#endif
    return true;
}
extern "C" __declspec(dllexport) void KfcRuntimeTick() { timing("tick"); mark("tick"); }
extern "C" __declspec(dllexport) void KfcRuntimeShutdown() {
    timing("shutdown");
    mark("shutdown");
#if TEST_good_kfc
    if (host.remove_hook)
        for (int i = 2; i >= 0; --i) if (handles[i]) { host.remove_hook(handles[i]); mark("removed"); }
#endif
}
extern "C" __declspec(dllexport) void KfcRuntimeStatus(char* p, size_t n) { timing("status"); if (n) { p[0] = 'x'; if (n > 1) p[1] = 0; } }
#endif
