// SPDX-License-Identifier: MIT
// The Windows kiln.tasks platform: real OS threads at below-normal priority, SetThreadDescription for the
// idle name and "<mod>/<task>", GetModuleHandleExW FROM_ADDRESS references for callback modules, the SEH fault boundary
// (tasks_seh.cpp) and a QueryPerformanceCounter clock. The pool (tasks.cpp) is platform-independent; this is the only
// file that calls Windows thread/loader APIs. Host suites inject their own Platform instead.
#include <windows.h>

#include <string>

#include "tasks_platform.h"

namespace kiln::tasks {

void seh_prepare_thread();
void guarded_call_seh(GuardedFn fn, void* context, FaultInfo* info);

namespace {

std::wstring widen(const char* s) {
    if (!s || !*s) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n > 0 ? n - 1 : 0, L'\0');
    if (n > 0) MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

struct ThreadStart {
    ThreadFn fn;
    void* context;
    std::wstring name;
};

DWORD WINAPI thread_main(void* raw) {
    auto* start = static_cast<ThreadStart*>(raw);
    const ThreadFn fn = start->fn;
    void* context = start->context;
    if (!start->name.empty()) SetThreadDescription(GetCurrentThread(), start->name.c_str());
    seh_prepare_thread();
    delete start;
    fn(context);
    return 0;
}

class WindowsPlatform final : public Platform {
public:
    ThreadHandle start_thread(const char* name, ThreadFn fn, void* context) override {
        auto* start = new ThreadStart{fn, context, widen(name)};
        const HANDLE h = CreateThread(nullptr, 0, &thread_main, start, 0, nullptr);
        if (!h) {
            delete start;
            return {0};
        }
        return {reinterpret_cast<uint64_t>(h)};
    }
    void join_thread(ThreadHandle thread) override {
        if (!thread.id) return;
        const HANDLE h = reinterpret_cast<HANDLE>(thread.id);
        WaitForSingleObject(h, INFINITE);
        CloseHandle(h);
    }
    void detach_thread(ThreadHandle thread) override {
        if (thread.id) CloseHandle(reinterpret_cast<HANDLE>(thread.id));  // the thread keeps running; the OS reclaims it
    }
    uint64_t current_thread_id() override { return GetCurrentThreadId(); }

    void set_description(const char* utf8) override { SetThreadDescription(GetCurrentThread(), widen(utf8).c_str()); }
    void lower_current_priority() override { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL); }

    ModuleRef acquire_module(uintptr_t address) override {
        HMODULE module = nullptr;
        // No UNCHANGED_REFCOUNT: this increments the module's load count, so it stays mapped until release_module.
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(address), &module))
            return {0, false};  // no module: a JIT thunk; the pool relies on the caller's lifetime (the managed host's lease)
        return {reinterpret_cast<uint64_t>(module), true};
    }
    void release_module(ModuleRef ref) override {
        if (ref.present && ref.token) FreeLibrary(reinterpret_cast<HMODULE>(ref.token));
    }

    void guarded_call(GuardedFn fn, void* context, FaultInfo* info) override { guarded_call_seh(fn, context, info); }

    uint64_t now_ns() override {
        LARGE_INTEGER count, freq;
        QueryPerformanceCounter(&count);
        QueryPerformanceFrequency(&freq);
        return freq.QuadPart ? static_cast<uint64_t>(count.QuadPart * 1000000000.0 / freq.QuadPart) : 0;
    }

    // GetThreadTimes (kernel + user, 100 ns units): exact units, but the values only advance when
    // the scheduler accounts the thread, at the ~15.6 ms quantum, on native Windows and under Wine
    // alike. QueryThreadCycleTime would advance continuously, but its cycles have no documented
    // frequency to convert to nanoseconds with, so the quantum-granular clock wins (issue #49).
    uint64_t thread_cpu_ns() override {
        FILETIME created, exited, kernel, user;
        if (!GetThreadTimes(GetCurrentThread(), &created, &exited, &kernel, &user)) return 0;
        const auto ticks = [](const FILETIME& f) {
            return (static_cast<uint64_t>(f.dwHighDateTime) << 32) | f.dwLowDateTime;
        };
        return (ticks(kernel) + ticks(user)) * 100ull;
    }
};

}  // namespace

// Immortal: never destroyed at static destruction, so a worker detached at shutdown that is still running at process
// exit never calls into a destroyed platform (the pool Core it co-owns refers to this object).
Platform& default_platform() {
    // Keep this a heap object that is never deleted (not a function-local static, no delete): a worker detached at
    // shutdown may still call into it. The host tests define their own default_platform(), so nothing here goes red
    // if this is reverted; tasks_pool_test's stuck_worker_survives_pool shows the call.
    static auto* platform = new WindowsPlatform;
    return *platform;
}

}  // namespace kiln::tasks
