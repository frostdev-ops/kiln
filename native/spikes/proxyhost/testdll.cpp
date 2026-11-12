// SPDX-License-Identifier: MIT
// A chained test DLL (see CMakeLists.txt). Logs "dll <name> attach" and "probe <name> ..." from DllMain.
//   TEST_ENTRY_PATCH  also overwrites the exe's entry point with a 14-byte jump to its own gate, which logs
//                     "gate <name>", puts the saved bytes back and calls the entry: what a loader that holds the
//                     game's start does (EMF on a server). With two of these and Kiln, the gates must run in reverse
//                     order of patching: Kiln, then the last patcher, then the first, then the real entry.
//   TEST_LOADS_EMF_X  also starts <game>\mods\emf-x.dll from its DllMain: what EMF's loader does with the DLLs of
//                     dinput8-chain.ini, so such a DLL is in the process before kiln-core's late phase can load it
//   TEST_FAIL         DllMain returns FALSE (LoadLibrary fails with ERROR_DLL_INIT_FAILED, 1114)
//   TEST_SHROUDFORGE  carries ShroudForge's marker text: the proxy must refuse it without loading it
//   TEST_APPENDS_ADDON_LOG  appends a line to <game>\shroudlink_client.log from DllMain, as shroudlink's client addon
//                     does once it loaded: the proxy measures that log before the chain (KilnBootInfo.addon_log_size)
//   TEST_EMF05        shaped like EMF 0.5's loader: exports emf_loader_identity (EMF's marker text) and
//                     ShroudforgeSetDinput8Provider; the proxy must load it as EMF, not refuse it as ShroudForge
#include <cstring>

#include "harness.h"

#ifndef TEST_NAME
#define TEST_NAME L"test"
#endif

namespace {
char g_name[64];

#ifdef TEST_SHROUDFORGE
extern "C" __declspec(dllexport) const char proxyhost_marker[] = "shroudforge/shroudforge-runtime.dll";
#endif

#ifdef TEST_EMF05
extern "C" __declspec(dllexport) const char emf_loader_identity[] = "EMF-LOADER-7d3c9f";
extern "C" __declspec(dllexport) void ShroudforgeSetDinput8Provider(void*) {}
#endif

#ifdef TEST_EMF_EXPORTS
extern "C" __declspec(dllexport) void emf_loader_play_once() {}
extern "C" __declspec(dllexport) void ShroudforgeSetDinput8Provider(void*) {}
#endif
#ifdef TEST_KILN_EXPORTS
extern "C" __declspec(dllexport) void KilnProxyInfo() {}
#endif
#ifdef TEST_EMF_WIDE
extern "C" __declspec(dllexport) const wchar_t emf_identity_wide[] = L"eMf-LoAdEr-7d3c9f";
extern "C" __declspec(dllexport) void ShroudforgeSetDinput8Provider(void*) {}
#endif

#ifdef TEST_LOADS_EMF_X
void load_listed() {
    wchar_t path[1024];
    const DWORD n = GetModuleFileNameW(nullptr, path, 1000);
    if (n == 0 || n >= 1000) return;
    wchar_t* slash = path;
    for (wchar_t* p = path; *p; ++p)
        if (*p == L'\\') slash = p;
    lstrcpyW(slash, L"\\mods\\emf-x.dll");
    HMODULE h = LoadLibraryW(path);
    ph_log("loader %s started mods\\emf-x.dll: %s", g_name, h ? "yes" : "no");
}
#endif

#ifdef TEST_APPENDS_ADDON_LOG
void append_addon_log() {
    wchar_t path[1024];
    const DWORD n = GetModuleFileNameW(nullptr, path, 1000);
    if (n == 0 || n >= 1000) return;
    wchar_t* slash = path;
    for (wchar_t* p = path; *p; ++p)
        if (*p == L'\\') slash = p;
    lstrcpyW(slash, L"\\shroudlink_client.log");
    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    const char line[] = "[1] chained DLL line\n";
    DWORD wrote = 0;
    WriteFile(h, line, sizeof line - 1, &wrote, nullptr);
    CloseHandle(h);
    ph_log("dll %s appended %lu bytes to shroudlink_client.log", g_name, wrote);
}
#endif

#ifdef TEST_ENTRY_PATCH
unsigned char* g_entry = nullptr;
unsigned char g_saved[14];

bool write_entry(const unsigned char* bytes) {
    DWORD protection = 0;
    if (!VirtualProtect(g_entry, 14, PAGE_EXECUTE_READWRITE, &protection)) return false;
    memcpy(g_entry, bytes, 14);
    DWORD ignored = 0;
    VirtualProtect(g_entry, 14, protection, &ignored);
    return FlushInstructionCache(GetCurrentProcess(), g_entry, 14) != 0;
}

int __cdecl gate() {
    ph_log("gate %s (entry bytes restored)", g_name);
    if (!write_entry(g_saved)) ExitProcess(91);
    return reinterpret_cast<int(__cdecl*)()>(g_entry)();
}

void patch() {
    g_entry = ph_entry();
    memcpy(g_saved, g_entry, 14);
    unsigned char jump[14]{0xff, 0x25, 0, 0, 0, 0};
    auto target = &gate;
    memcpy(jump + 6, &target, sizeof target);
    const bool ok = write_entry(jump);
    ph_log("patch %s: entry %s (saved %02x %02x)", g_name, ok ? "patched" : "NOT patched", g_saved[0], g_saved[1]);
}
#endif
}  // namespace

extern "C" __declspec(dllexport) const wchar_t* proxyhost_test_name() { return TEST_NAME; }

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, void*) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(inst);
    WideCharToMultiByte(CP_UTF8, 0, TEST_NAME, -1, g_name, sizeof g_name, nullptr, nullptr);
    ph_log("dll %s attach", g_name);
    // What is already in the process when this DLL loads: a chain DLL loaded by the proxy sees no core; one the core loads
    // late (chain.ini [late]) sees the core and the native mods that load before it ("boot" phase).
    ph_log("probe %s kiln-core=%d native-test-boot=%d", g_name, GetModuleHandleW(L"kiln-core.dll") != nullptr ? 1 : 0,
           GetModuleHandleW(L"native-test-boot.dll") != nullptr ? 1 : 0);
#ifdef TEST_ENTRY_PATCH
    patch();
#endif
#ifdef TEST_LOADS_EMF_X
    load_listed();
#endif
#ifdef TEST_APPENDS_ADDON_LOG
    append_addon_log();
#endif
#ifdef TEST_SHROUDFORGE
    ph_log("dll %s: ShroudForge marker %s", g_name, proxyhost_marker);
#endif
#ifdef TEST_FAIL
    return FALSE;
#else
    return TRUE;
#endif
}
