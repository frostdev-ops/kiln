// SPDX-License-Identifier: MIT
// Compile the real api.cpp unload block and boot.cpp exit trigger against deterministic host stubs.
#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>
#include "kiln_api.h"
#define WINAPI
#define __try try
#define __except(x) catch (...)
using DWORD = unsigned long;
using LONG = long;
using LONGLONG = long long;
using HANDLE = void*;
constexpr bool TRUE = true, FALSE = false;
constexpr DWORD WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 258;
constexpr int GENERIC_READ = 1, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
constexpr int OPEN_EXISTING = 1, FILE_ATTRIBUTE_NORMAL = 1, FILE_BEGIN = 0;
#define INVALID_HANDLE_VALUE reinterpret_cast<HANDLE>(-1)
struct LARGE_INTEGER { LONGLONG QuadPart = 0; };
struct RTL_CRITICAL_SECTION { void* OwningThread = nullptr; };
struct Event { bool set = false; };
Event events[32];
size_t next_event = 0;
double clock_now = 1000;
DWORD thread_id = 1;
std::vector<DWORD> secondary_waits;
HANDLE sequence_event = nullptr;
bool throw_log = false;
bool in_secondary = false;
int logs = 0, file_reads = 0, real_exits = 0, flushes = 0;
DWORD flush_wait = 0;
bool stall_flush = false;
DWORD GetCurrentThreadId() { return thread_id; }
DWORD GetEnvironmentVariableW(const wchar_t*, wchar_t*, DWORD) { return 0; }
HANDLE CreateEventW(void*, bool, bool, void*) { return &events[next_event++]; }
bool SetEvent(HANDLE h) { if (h) static_cast<Event*>(h)->set = true; return true; }
LONG InterlockedCompareExchange(volatile LONG* p, LONG value, LONG expected) {
    LONG old = *p;
    if (old == expected) *p = value;
    return old;
}
LONG InterlockedExchange(volatile LONG* p, LONG value) { LONG old = *p; *p = value; return old; }
uintptr_t __readgsqword(int) { alignas(void*) static uint8_t peb[0x118]{}; return reinterpret_cast<uintptr_t>(peb); }
HANDLE CreateFileW(const std::filesystem::path::value_type*, int, int, void*, int, int, void*) {
    ++file_reads;
    return INVALID_HANDLE_VALUE;
}
bool GetFileSizeEx(HANDLE, LARGE_INTEGER*) { return true; }
bool SetFilePointerEx(HANDLE, LARGE_INTEGER, void*, int) { return true; }
bool ReadFile(HANDLE, void*, DWORD, DWORD*, void*) { return true; }
bool CloseHandle(HANDLE) { return true; }
bool GetExitCodeThread(HANDLE, DWORD* code) { *code = 0; return true; }
void Sleep(DWORD n) { clock_now += n; }
DWORD GetLastError() { return 0; }
// Threads execute inline for deterministic handler scheduling. A flush can instead remain unsignalled, modelling
// a stuck file write; the exit thread's wait must still end. Nothing copies the sequence's control flow in the test.
HANDLE CreateThread(void*, size_t, DWORD (*fn)(void*), void* arg, DWORD, void*) {
    if (stall_flush && arg == nullptr) return &events[31];
    const auto old = thread_id;
    thread_id = 2;
    fn(arg);
    thread_id = old;
    return &events[30];
}
DWORD WaitForSingleObject(HANDLE h, DWORD ms) {
    if (in_secondary && h == sequence_event) {
        secondary_waits.push_back(ms);
        return WAIT_OBJECT_0;  // allow the outer test to continue and verify its remaining work
    }
    if (h == &events[31]) { flush_wait = ms; clock_now += ms; return WAIT_TIMEOUT; }
    return WAIT_OBJECT_0;
}
namespace kiln {
double now_ms() { return clock_now; }
struct Paths { std::filesystem::path logs; };
const Paths& paths() { static Paths p; return p; }
struct Config { int log_level = KILN_LOG_TRACE; };
const Config& config() { static Config c; return c; }
namespace crash {
struct Capture {};
void report(const Capture&, const char*, const void*) {}
}
namespace log {
void write(int, const char*, std::string_view) { ++logs; if (throw_log) throw std::runtime_error("log fault"); }
}
namespace caps { bool flush() { ++flushes; return true; } }
#define KLOG_I(...) log::write(KILN_LOG_INFO, "kiln", "test log")
#define KLOG_W(...) log::write(KILN_LOG_WARN, "kiln", "test log")
#define KLOG_E(...) log::write(KILN_LOG_ERROR, "kiln", "test log")
struct LoadedModule { std::string name; KilnPluginInfo info{}; };
std::vector<LoadedModule> g_modules;
#include "unload_sequence.inc"
#include "unload_trigger.inc"
}
int failures = 0, checks = 0;
void check(bool ok, const char* name) { ++checks; std::printf("%s %s\n", ok ? "PASS" : "FAIL", name); if (!ok) ++failures; }
void fake_exit(uint32_t) { ++real_exits; }
void KILN_CALL reexit(void*) {
    clock_now += 250;
    in_secondary = true;
    kiln::unload_run(0, "test");
    in_secondary = false;
}
void prepare(void (KILN_CALL* handler)(void*)) {
    using namespace kiln;
    g_modules = {{"test-plugin", {}}};
    g_modules[0].info.size = sizeof(KilnPluginInfo);
    g_modules[0].info.on_unload = handler;
    g_unload_jobs = nullptr;
    g_unload_count = 0;
    g_unload_state = 0;
    g_unload_started_ms = -1;
    next_event = 0;
    for (auto& e : events) e.set = false;
    clock_now = 1000;
    secondary_waits.clear();
    throw_log = false;
    logs = file_reads = real_exits = flushes = 0;
    stall_flush = false;
    flush_wait = 0;
    unload_prepare();
    sequence_event = g_unload_over;
    g_exit_next = reinterpret_cast<void*>(&fake_exit);
}
void cleanup() { delete[] kiln::g_unload_jobs; kiln::g_unload_jobs = nullptr; }
void KILN_CALL marker(void*) {}
int main() {
    prepare(&reexit);
    kiln::exit_call(0);
    check(secondary_waits.size() == 1 && secondary_waits[0] == 13250,
          "second exit waits remaining handler + 2000 ms flush + 500 ms log + 1000 ms margin from sequence start");
    check(kiln::g_unload_state == 2 && static_cast<Event*>(sequence_event)->set && real_exits == 1 && flushes == 1,
          "reentrant exit leaves the whole sequence completed");
    cleanup();
    prepare(&marker);
    throw_log = true;
    kiln::exit_call(0);
    check(kiln::g_unload_state == 2 && static_cast<Event*>(sequence_event)->set && real_exits == 1,
          "fault in the sequence marks done and signals before continuing the real exit");
    throw_log = false;
    in_secondary = true;
    kiln::unload_run(0, "test");
    in_secondary = false;
    check(secondary_waits.empty(), "exit after a sequence fault does not wait for an abandoned sequence");
    cleanup();
    prepare(&marker);
    stall_flush = true;
    kiln::exit_call(0);
    check(flush_wait == 2000 && kiln::g_unload_state == 2 && real_exits == 1,
          "a stuck last file write is abandoned after 2000 ms so the second caller bound is real");
    cleanup();
    std::printf("%d checks, %d failed\n", checks, failures);
    return failures ? 1 : 0;
}
