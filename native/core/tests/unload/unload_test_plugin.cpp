// SPDX-License-Identifier: MIT
// Test plugins for the on_unload lab check (native/core/tests/test-unload-lab.sh). One source, one DLL per mode
// (UNLOAD_TEST_MODE, set by tests/unload/CMakeLists.txt):
//   1 marker     its on_unload appends a line to <logs>/<id>.txt (one line per call) and to
//                <logs>/unload-order.txt (shared by every test plugin: the line order is the call order)
//   2 nohandler  the same marker code, but kiln_plugin_main sets no on_unload: its marker must never appear
//   3 fault      on_unload notes the call in unload-order.txt, then writes through a null pointer
//   4 block      on_unload notes the call in unload-order.txt, then never returns
//   5 reexit     on_unload notes the call, then (first call only) calls ExitProcess(0) itself: a second exit call while
//                the sequence runs (the core must not start it again, and must still call the handlers after this one)
// Every line carries the thread, whether it is the thread kiln_plugin_main ran on, whether that thread holds the
// loader lock, and a QueryPerformanceCounter stamp. Files are written with WriteFile + FlushFileBuffers: nothing is
// left in a buffer when the process ends right after the call.
#include <windows.h>
#include <intrin.h>

#include <cstdio>
#include <cstring>

#include "kiln_api.h"

#ifndef UNLOAD_TEST_MODE
#error "UNLOAD_TEST_MODE must be 1 (marker), 2 (nohandler), 3 (fault), 4 (block) or 5 (reexit)"
#endif
#ifndef UNLOAD_TEST_ID
#error "UNLOAD_TEST_ID must name the plugin (\"unload-test-1-marker\")"
#endif

namespace {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
DWORD g_main_thread = 0;
char g_logs[1024] = {};
size_t g_logs_len = 0;
volatile LONG g_calls = 0;

DWORD loader_lock_owner() {
    // PEB.LoaderLock (x64 offset 0x110, the same on Windows and Wine): the RTL_CRITICAL_SECTION the loader holds.
    const auto* peb = reinterpret_cast<const uint8_t*>(__readgsqword(0x60));
    const auto* cs = *reinterpret_cast<RTL_CRITICAL_SECTION* const*>(peb + 0x110);
    return cs ? static_cast<DWORD>(reinterpret_cast<uintptr_t>(cs->OwningThread)) : 0;
}

void append(const char* file, const char* text) {
    char path[1100];
    std::snprintf(path, sizeof path, "%.*s\\%s", static_cast<int>(g_logs_len), g_logs, file);
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, text, static_cast<DWORD>(std::strlen(text)), &written, nullptr);
    FlushFileBuffers(h);
    CloseHandle(h);
}

LONG note_call(const char* what) {
    const LONG n = InterlockedIncrement(&g_calls);
    LARGE_INTEGER qpc{};
    QueryPerformanceCounter(&qpc);
    const DWORD tid = GetCurrentThreadId();
    char text[400];
    std::snprintf(text, sizeof text, "%s %s call=%ld thread=%lu main=%s loaderlock=%s qpc=%lld\n", UNLOAD_TEST_ID,
                  what, n, tid, tid == g_main_thread ? "yes" : "no", loader_lock_owner() == tid ? "held" : "free",
                  static_cast<long long>(qpc.QuadPart));
    append("unload-order.txt", text);
    char own[200];
    std::snprintf(own, sizeof own, "%s.txt", UNLOAD_TEST_ID);
    append(own, text);
    return n;
}

void KILN_CALL on_unload(void*) {
#if UNLOAD_TEST_MODE == 1 || UNLOAD_TEST_MODE == 2
    note_call("on_unload");
#elif UNLOAD_TEST_MODE == 3
    note_call("on_unload-faulting");
    *static_cast<volatile int*>(nullptr) = 1;  // the fault the core's guard must catch
#elif UNLOAD_TEST_MODE == 4
    note_call("on_unload-blocking");
    for (;;) Sleep(INFINITE);  // never returns: the core's time limit must abandon it
#elif UNLOAD_TEST_MODE == 5
    // The core's exit trigger again, from this handler's thread. Only on the first call: a core that started the
    // sequence again would call this handler a second time, which must then return (and the markers show 2 calls).
    if (note_call("on_unload-exiting") == 1) ExitProcess(0);
#endif
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (!api || !info || api->abi_version != KILN_ABI_VERSION || !api->path || !api->log) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_main_thread = GetCurrentThreadId();
    if (api->path(KILN_PATH_LOGS, g_logs, sizeof g_logs, &g_logs_len) != KILN_OK) return KILN_ERR_UNAVAILABLE;
    info->id = KilnStr{UNLOAD_TEST_ID, sizeof(UNLOAD_TEST_ID) - 1};
    info->version = KilnStr{"1", 1};
#if UNLOAD_TEST_MODE != 2
    info->on_unload = &on_unload;
#else
    (void)&on_unload;  // compiled, never handed to the core
#endif
    char line[160];
    const int n = std::snprintf(line, sizeof line, "%s: loaded on thread %lu (on_unload %s)", UNLOAD_TEST_ID,
                                g_main_thread, UNLOAD_TEST_MODE == 2 ? "not set" : "set");
    api->log(owner, KILN_LOG_INFO, KilnStr{line, static_cast<size_t>(n)});
    return KILN_OK;
}
