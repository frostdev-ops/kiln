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
//   6 caps       kiln.caps rows of its own: from the server-world tick, 4 s after the first step, ten reports of
//                "unloadtest.step" 200 ms apart, available and unavailable in turn; in on_unload "unloadtest.step"
//                once more ("at exit"), a wait until capabilities.json was written again (its last-write time, read
//                without opening the file: a reader holding it open can make the core's replace fail), then
//                "unloadtest.exit" (available).
//                The wait puts the core's writer thread inside its one-second gap, so only the exit path's last write
//                can get the exit row into the file in time. UNLOAD_TEST_CAPS_EXIT=single skips the first report and
//                the wait.
//                Its lines go to <logs>/caps-test.txt (wall-clock milliseconds, the registry's generation), not to
//                unload-order.txt.
// Every line carries the thread, whether it is the thread kiln_plugin_main ran on, whether that thread holds the
// loader lock, and a QueryPerformanceCounter stamp. Files are written with WriteFile + FlushFileBuffers: nothing is
// left in a buffer when the process ends right after the call.
#include <windows.h>
#include <intrin.h>

#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "kiln_api.h"
#include "services/kiln_caps.h"

#ifndef UNLOAD_TEST_MODE
#error "UNLOAD_TEST_MODE must be 1 (marker), 2 (nohandler), 3 (fault), 4 (block), 5 (reexit) or 6 (caps)"
#endif
#ifndef UNLOAD_TEST_ID
#error "UNLOAD_TEST_ID must name the plugin (\"unload-test-1-marker\")"
#endif

namespace {
[[maybe_unused]] const KilnApi* g_api = nullptr;  // used by the caps mode
[[maybe_unused]] KilnOwner g_owner = 0;
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

[[maybe_unused]] LONG note_call(const char* what) {
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

#if UNLOAD_TEST_MODE == 6
const KilnCapsApi* g_caps = nullptr;
char g_state[1024] = {};
size_t g_state_len = 0;
uint64_t g_first_tick = 0, g_next_report = 0;
int g_reports = 0;
constexpr int kReports = 10;

uint64_t unix_ms() {
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    ULARGE_INTEGER u;
    u.LowPart = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    return (u.QuadPart - 116444736000000000ull) / 10000;
}

void caps_line(const char* fmt, ...) {
    char text[400];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(text, sizeof text, fmt, ap);
    va_end(ap);
    append("caps-test.txt", text);
}

KilnResult report(const char* name, bool on, const char* text) {
    return g_caps->report(g_owner, KilnStr{name, std::strlen(name)}, on ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                          on ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_POLICY, KilnStr{text, std::strlen(text)});
}

void KILN_CALL on_tick(const KilnTickInfo*, void*) {
    if (g_reports >= kReports) return;
    const uint64_t now = GetTickCount64();
    if (!g_first_tick) {
        g_first_tick = now;
        return;
    }
    if (now - g_first_tick < 4000 || now < g_next_report) return;
    char text[64];
    std::snprintf(text, sizeof text, "report %d of %d", g_reports + 1, kReports);
    const bool on = g_reports % 2 == 0;
    const KilnResult rc = report("unloadtest.step", on, text);
    ++g_reports;
    g_next_report = now + 200;
    caps_line("report %d state=%s rc=%d unix_ms=%llu generation=%llu\n", g_reports, on ? "available" : "unavailable",
              rc, static_cast<unsigned long long>(unix_ms()), static_cast<unsigned long long>(g_caps->generation()));
}

// capabilities.json's last-write time (0 when it is not there), without opening the file.
uint64_t caps_file_time() {
    char path[1100];
    std::snprintf(path, sizeof path, "%.*s\\capabilities.json", static_cast<int>(g_state_len), g_state);
    WIN32_FILE_ATTRIBUTE_DATA a{};
    if (!GetFileAttributesExA(path, GetFileExInfoStandard, &a)) return 0;
    return (static_cast<uint64_t>(a.ftLastWriteTime.dwHighDateTime) << 32) | a.ftLastWriteTime.dwLowDateTime;
}

void KILN_CALL on_ready(void*) {
    g_caps = static_cast<const KilnCapsApi*>(
        g_api->service(KilnStr{KILN_CAPS_SERVICE, sizeof(KILN_CAPS_SERVICE) - 1}, KILN_CAPS_VERSION));
    if (g_caps && g_caps->header.size < sizeof(KilnCapsApi)) g_caps = nullptr;
    KilnHandle h = 0;
    const KilnResult rc =
        g_caps ? g_api->subscribe_tick(g_owner, KILN_TICK_PRE, &on_tick, nullptr, &h) : KILN_ERR_UNAVAILABLE;
    caps_line("ready caps=%s tick=%d unix_ms=%llu\n", g_caps ? "yes" : "no", rc,
              static_cast<unsigned long long>(unix_ms()));
}
#endif

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
#elif UNLOAD_TEST_MODE == 6
    const LONG n = InterlockedIncrement(&g_calls);
    caps_line("on_unload call=%ld thread=%lu main=%s unix_ms=%llu\n", n, GetCurrentThreadId(),
              GetCurrentThreadId() == g_main_thread ? "yes" : "no", static_cast<unsigned long long>(unix_ms()));
    if (!g_caps) return;
    char mode[16] = {};
    const bool single =
        GetEnvironmentVariableA("UNLOAD_TEST_CAPS_EXIT", mode, sizeof mode) && !std::strcmp(mode, "single");
    if (!single) {
        const uint64_t before = caps_file_time();
        const KilnResult rc = report("unloadtest.step", true, "at exit");
        const uint64_t t0 = unix_ms();
        bool found = false;
        while (!(found = caps_file_time() != before) && unix_ms() - t0 < 1500) Sleep(10);
        caps_line("step at exit rc=%d in the file after %llu ms (%s)\n", rc,
                  static_cast<unsigned long long>(unix_ms() - t0), found ? "found" : "not found");
    }
    const KilnResult rc = report("unloadtest.exit", true, "reported in on_unload");
    caps_line("exit reported rc=%d unix_ms=%llu generation=%llu mode=%s\n", rc,
              static_cast<unsigned long long>(unix_ms()), static_cast<unsigned long long>(g_caps->generation()),
              single ? "single" : "gap");
#endif
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (!api || !info || api->abi_version != KILN_ABI_VERSION || !api->path || !api->log) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_main_thread = GetCurrentThreadId();
    if (api->path(KILN_PATH_LOGS, g_logs, sizeof g_logs, &g_logs_len) != KILN_OK) return KILN_ERR_UNAVAILABLE;
#if UNLOAD_TEST_MODE == 6
    if (api->path(KILN_PATH_STATE, g_state, sizeof g_state, &g_state_len) != KILN_OK) return KILN_ERR_UNAVAILABLE;
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(void*)) info->on_ready = &on_ready;
#endif
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
