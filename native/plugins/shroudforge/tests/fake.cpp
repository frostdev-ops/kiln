// SPDX-License-Identifier: MIT
#include <windows.h>
#include <cstdio>
#include <cmath>
#include "services/kiln_eml.h"
static void mark(const char* what) {
    wchar_t file[32768]{};
    GetEnvironmentVariableW(L"KILN_SF_TEST_LOG", file, 32768);
    if (FILE* f = _wfopen(file, L"ab")) { std::fprintf(f, "%s\n", what); std::fclose(f); }
}
#if TEST_runtime
extern "C" __declspec(dllexport) bool shroudforge_prepare_startup(const wchar_t* root) { mark(root && *root ? "prepare" : "bad-root"); return root && *root; }
extern "C" __declspec(dllexport) void* shroudforge_create(const wchar_t*) { mark("create"); return reinterpret_cast<void*>(1); }
extern "C" __declspec(dllexport) bool shroudforge_update(void* h, double dt) { mark("update"); if (GetEnvironmentVariableW(L"KILN_SF_FAULT_UPDATE", nullptr, 0)) RaiseException(0xc0000005, 0, 0, nullptr); return h && std::isfinite(dt) && dt >= 0; }
extern "C" __declspec(dllexport) void shroudforge_destroy(void*) { mark("destroy"); }
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
extern "C" __declspec(dllexport) bool KfcRuntimeInitialize() { mark("init"); return true; }
extern "C" __declspec(dllexport) void KfcRuntimeTick() { mark("tick"); }
extern "C" __declspec(dllexport) void KfcRuntimeShutdown() { mark("shutdown"); }
extern "C" __declspec(dllexport) void KfcRuntimeStatus(char* p, size_t n) { if (n) { p[0] = 'x'; if (n > 1) p[1] = 0; } }
#endif
