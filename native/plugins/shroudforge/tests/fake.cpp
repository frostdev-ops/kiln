// SPDX-License-Identifier: MIT
#include <windows.h>
#include <cstdio>
#include <cmath>
#include <cstdint>
#include "services/kiln_eml.h"
static void mark(const char* what) {
    wchar_t file[32768]{};
    GetEnvironmentVariableW(L"KILN_SF_TEST_LOG", file, 32768);
    if (FILE* f = _wfopen(file, L"ab")) { std::fprintf(f, "%s\n", what); std::fclose(f); }
}
static void timing(const char* operation, double dt = 0) {
    if (!GetEnvironmentVariableW(L"KILN_SF_SLOW_HEARTBEAT", nullptr, 0)) return;
    char text[160];
    std::snprintf(text, sizeof text, "timing %s %llu %lu %.9f", operation, GetTickCount64(), GetCurrentThreadId(), dt);
    mark(text);
}
#if TEST_bootstrap
extern "C" __declspec(dllexport) void ShroudforgeStop() {}
#elif TEST_runtime
extern "C" __declspec(dllexport) bool shroudforge_prepare_startup(const wchar_t* root) { mark(root && *root ? "prepare" : "bad-root"); return root && *root; }
extern "C" __declspec(dllexport) void* shroudforge_create(const wchar_t*) { mark("create"); return reinterpret_cast<void*>(1); }
extern "C" __declspec(dllexport) bool shroudforge_update(void* h, double dt) {
    timing("update", dt);
    mark("update");
    if (GetEnvironmentVariableW(L"KILN_SF_STALL_UPDATE", nullptr, 0)) Sleep(3200);
    if (GetEnvironmentVariableW(L"KILN_SF_FAULT_UPDATE", nullptr, 0)) RaiseException(0xc0000005, 0, 0, nullptr);
    return h && std::isfinite(dt) && dt >= 0;
}
extern "C" __declspec(dllexport) void shroudforge_destroy(void*) { timing("destroy"); mark("destroy"); }
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
