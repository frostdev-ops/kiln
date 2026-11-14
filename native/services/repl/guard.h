// SPDX-License-Identifier: MIT
// kiln.repl's fault rule for its raw detours (the dossier's chapter 11 §4.1). kiln.hooks runs structured detours under an
// SEH guard but not raw ones (a raw link's entry is the handler itself), and the replication hooks are raw: they sit on
// the game's own job threads, per (machine, channel), in parallel. So every observer detour of this service has one
// shape, `detour()` below:
//   - the original (the rest of the hook chain, then the game) runs exactly once, unconditionally, outside any guard;
//   - the service's own observer code runs inside a local __try (/EHa) before and/or after it; a fault there sets the
//     hook's `disabled` flag, counts the fault, notes its address and returns normally, so the original's result is
//     returned untouched;
//   - a disabled hook stays installed and only passes through until its owner revokes it;
//   - one log line per hook per session, written on the tick thread (flush_faults), never from a job thread:
//     "repl: observer <name> faulted at <where>; disabled".
// Observers never allocate and call no game function but the pure StateIndex (under the same guard). No C++ object with
// a destructor lives in a guarded frame.
#pragma once
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <type_traits>
#include <utility>

#include <windows.h>

namespace repl {

struct Guard {
    const char* name;
    std::atomic<bool> disabled{false};
    std::atomic<uint64_t> faults{0};
    std::atomic<uintptr_t> where{0};  // the first fault's address
    std::atomic<bool> logged{false};
    explicit Guard(const char* n) : name(n) {}
    Guard(const Guard&) = delete;
    Guard& operator=(const Guard&) = delete;
};

using ObserveFn = void (*)(void* ctx);

namespace guard_detail {
inline int note(EXCEPTION_POINTERS* e, uintptr_t* where) {
    *where = reinterpret_cast<uintptr_t>(e->ExceptionRecord->ExceptionAddress);
    return EXCEPTION_EXECUTE_HANDLER;
}
// fn(ctx) inside the guard: 0 when it returned, else the faulting address (1 when unknown).
inline uintptr_t run(ObserveFn fn, void* ctx) {
    uintptr_t where = 0;
    __try {
        fn(ctx);
    } __except (note(GetExceptionInformation(), &where)) {
        if (!where) where = 1;
    }
    return where;
}
}  // namespace guard_detail

// Runs fn(ctx) unless `g` is disabled; a fault in it disables `g` for the session. True when fn ran to its end.
inline bool observe(Guard& g, ObserveFn fn, void* ctx) {
    if (!fn || g.disabled.load(std::memory_order_acquire)) return false;
    const uintptr_t where = guard_detail::run(fn, ctx);
    if (!where) return true;
    uintptr_t none = 0;
    g.where.compare_exchange_strong(none, where, std::memory_order_relaxed);
    g.faults.fetch_add(1, std::memory_order_relaxed);
    g.disabled.store(true, std::memory_order_release);
    return false;
}

// The one shape of an observer detour: `before` and `after` (either may be null) run inside the guard; `original` runs
// exactly once between them, outside it, whatever they did. Its result is stored in ctx->result (when Ctx has one)
// before `after` runs, and returned untouched.
template <class Ctx, class R, class... P, class... A>
inline R detour(Guard& g, Ctx* ctx, ObserveFn before, ObserveFn after, R (*original)(P...), A&&... args) {
    if (before) observe(g, before, ctx);
    if constexpr (std::is_void_v<R>) {
        original(std::forward<A>(args)...);
        if (after) observe(g, after, ctx);
    } else {
        R r = original(std::forward<A>(args)...);
        if constexpr (requires { ctx->result = r; }) ctx->result = r;
        if (after) observe(g, after, ctx);
        return r;
    }
}

// Tick thread: one line for each guard that faulted and has not said so yet. `describe` turns an address into words
// ("enshrouded_server.exe+0x64bd12"); `log` writes a line. Returns the number of lines written.
template <class Describe, class Log>
inline int flush_faults(Guard* const* guards, size_t n, Describe&& describe, Log&& log) {
    int lines = 0;
    for (size_t i = 0; i < n; ++i) {
        Guard& g = *guards[i];
        if (!g.disabled.load(std::memory_order_acquire) || g.logged.exchange(true)) continue;
        char where[160];
        describe(g.where.load(std::memory_order_relaxed), where, sizeof where);
        char line[320];
        std::snprintf(line, sizeof line, "repl: observer %s faulted at %s; disabled", g.name, where);
        log(line);
        ++lines;
    }
    return lines;
}

}  // namespace repl
