// SPDX-License-Identifier: MIT
// The OS the kiln.tasks pool (tasks.cpp) runs on, behind one interface so the pool logic is host-testable
// and the Windows specifics (SEH fault boundary, SetThreadDescription, GetModuleHandleExW FROM_ADDRESS, thread
// priority) live in tasks_win.cpp / tasks_seh.cpp. Host tests inject a fake Platform (tasks_platform_host.cpp /
// the test's own) to force overlaps, faults and clock values deterministically; Windows proves the real boundary
// under Wine. Nothing here calls into the model or holds any lock.
#pragma once
#include <cstdint>

namespace kiln::tasks {

// What a guarded invocation of a body/done callback returned.
struct FaultInfo {
    bool faulted = false;      // a structured fault (not an ordinary return)
    uint32_t code = 0;         // the exception code
    bool thread_tainted = true;  // the OS thread must be retired (true for a real fault; stays true unless proven safe)
};

// A reference that keeps the module owning a callback address loaded until released (Windows: GetModuleHandleExW with
// FROM_ADDRESS, no PIN; FreeLibrary to release). `present` is false for an address in no module (a JIT thunk): the
// pool then relies on the caller's own lifetime (the managed host's lease), never on a module pin.
struct ModuleRef {
    uint64_t token = 0;
    bool present = false;
};

// A started worker; join() blocks until its loop returned. The pool never reuses one: a retired worker is joined and a
// fresh one started.
struct ThreadHandle {
    uint64_t id = 0;
};

// A thread entry point: the pool packs everything it needs into `context`.
using ThreadFn = void (*)(void* context);
// A POD trampoline for guarded_call: no C++ object with a destructor is live across the fault boundary (clang-cl SEH).
using GuardedFn = void (*)(void* context);
// Every guarded_call context begins with this header, so a platform can see which user callback is about to run
// (the real Windows boundary ignores it; the host test platform uses `callback` to inject a fault for one address).
struct GuardedHeader {
    uintptr_t callback;
};

class Platform {
public:
    virtual ~Platform() = default;

    // Start `fn(context)` on a new OS thread named `idle_name`. A worker lowers its own priority at entry via
    // lower_current_priority(). Returns {0} on failure (the pool unwinds what it started).
    virtual ThreadHandle start_thread(const char* idle_name, ThreadFn fn, void* context) = 0;
    virtual void join_thread(ThreadHandle thread) = 0;
    // Give up on a thread that will not exit (an uncaught stack overflow leaves its worker spinning): release the
    // handle without waiting, so shutdown is bounded. The pool only detaches a thread it has already decided is stuck
    // and reports it; it never forcibly terminates one (TerminateThread corrupts the process).
    virtual void detach_thread(ThreadHandle thread) = 0;
    virtual uint64_t current_thread_id() = 0;

    // The running worker's description (SetThreadDescription): "<mod>/<task>" while a task runs, the idle name after.
    virtual void set_description(const char* utf8) = 0;
    virtual void lower_current_priority() = 0;

    // A module reference for a callback address; `present` says whether the address is in a module at all. release is
    // balanced with each successful acquire.
    virtual ModuleRef acquire_module(uintptr_t address) = 0;
    virtual void release_module(ModuleRef ref) = 0;

    // Run fn(context) inside the OS fault boundary. On an ordinary return *info = {faulted:false}. On a structured
    // fault *info describes it and thread_tainted says the thread must be retired. Never lets a fault cross the ABI.
    virtual void guarded_call(GuardedFn fn, void* context, FaultInfo* info) = 0;

    // A monotonic clock in nanoseconds for watchdog and CPU-time accounting (tests inject a fake).
    virtual uint64_t now_ns() = 0;
    // This thread's CPU time in nanoseconds (the executing thread only: time awaiting IO, where the
    // thread is not scheduled, does not advance it). 0 when the platform cannot say. The Windows
    // implementation uses GetThreadTimes, whose kernel+user times advance at the scheduler quantum
    // (~15.6 ms), so a segment's CPU is quantum-granular there: a segment shorter than a quantum
    // usually reads 0 and one that crosses a quantum boundary reads a whole quantum. That is a
    // documented property of the number, not a conversion error (tasks.md, Diagnostics).
    virtual uint64_t thread_cpu_ns() { return 0; }
};

// The production platform (tasks_win.cpp on Windows; a thin std::thread host build for the sanitizer suites). It is
// immortal (never destroyed, not even at static destruction): a worker detached at shutdown may still use it at exit.
// A platform passed to a Pool must likewise outlive every worker the pool detached.
Platform& default_platform();

}  // namespace kiln::tasks
