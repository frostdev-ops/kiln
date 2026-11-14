// SPDX-License-Identifier: MIT
// kiln.repl host tests (no game; built with clang-cl, run under plain Wine by tests/run.sh, so the guard's SEH is the
// real thing): the ABI layout of kiln_repl.h, and the fault rule of guard.h, the one shape of every observer detour
// (a forced access violation in an observer leaves the original's result intact, the original runs exactly once,
// the hook is disabled and says so once).
#include <windows.h>

#include <cstddef>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../guard.h"
#include "../layout.h"
#include "services/kiln_repl.h"

namespace {

int g_checks = 0, g_failed = 0;
void check(bool ok, const char* what) {
    ++g_checks;
    if (!ok) {
        ++g_failed;
        std::printf("FAIL %s\n", what);
    }
}
#define CHECK(x) check((x), #x)

// ---- ABI ----
void abi() {
    static_assert(sizeof(KilnReplPeerStats) == 16 + 13 * 8);
    static_assert(offsetof(KilnReplPeerStats, peer) == 4 && offsetof(KilnReplPeerStats, channel) == 8 &&
                  offsetof(KilnReplPeerStats, flags) == 9 && offsetof(KilnReplPeerStats, frames) == 16 &&
                  offsetof(KilnReplPeerStats, refused) == 40 && offsetof(KilnReplPeerStats, unchanged) == 80 &&
                  offsetof(KilnReplPeerStats, dropped) == 112);
    static_assert(sizeof(KilnReplComponent) == 24 && offsetof(KilnReplComponent, hash) == 4 &&
                  offsetof(KilnReplComponent, custom_codec) == 8 && offsetof(KilnReplComponent, data) == 16);
    static_assert(sizeof(KilnReplWatchEvent) == 48 && offsetof(KilnReplWatchEvent, direction) == 8 &&
                  offsetof(KilnReplWatchEvent, gap) == 11 && offsetof(KilnReplWatchEvent, entity) == 12 &&
                  offsetof(KilnReplWatchEvent, payload_bytes) == 28 && offsetof(KilnReplWatchEvent, count) == 32 &&
                  offsetof(KilnReplWatchEvent, components) == 40);
    static_assert(sizeof(KilnReplComponentInfo) == 16 && offsetof(KilnReplComponentInfo, index) == 8 &&
                  offsetof(KilnReplComponentInfo, data_size) == 10 && offsetof(KilnReplComponentInfo, flags) == 12);
    // v1's slots in order; a table only grows at its end
    static_assert(offsetof(KilnReplApi, capabilities) == 8 && offsetof(KilnReplApi, stats_enable) == 16 &&
                  offsetof(KilnReplApi, peer_stats) == 24 && offsetof(KilnReplApi, watch) == 32 &&
                  offsetof(KilnReplApi, remove) == 40 && offsetof(KilnReplApi, component_info) == 48 &&
                  offsetof(KilnReplApi, capture) == 56);
    static_assert(KILN_REPL_CAP_OBSERVE == 1 && KILN_REPL_CAP_CLIENT == 2 && KILN_REPL_CAP_LOOPBACK == 4 &&
                  KILN_REPL_CAP_FILTER == 8 && KILN_REPL_CAP_TOUCH == 16 && KILN_REPL_CAP_MESSAGES == 32 &&
                  KILN_REPL_CAP_SCHEMA == 64);
    // the game's structures the hooks read
    static_assert(sizeof(repl::Item12) == 12 && sizeof(repl::MsgEntry) == 32 && sizeof(repl::Batch) == 24 &&
                  sizeof(repl::Holder) == 48 && offsetof(repl::WriterOut, skipped) == 16);
    CHECK(sizeof(KilnReplApi) >= 64);
}

// ---- the fault rule ----
int g_original_calls = 0;
int g_before_runs = 0, g_after_runs = 0;
bool g_fault_before = false, g_fault_after = false;

// A stand-in for a game function: a value the caller must get back unchanged.
__declspec(noinline) int64_t original(int64_t a, int64_t b) {
    ++g_original_calls;
    return a * 1000 + b;
}
__declspec(noinline) void original_void(int64_t* out) {
    ++g_original_calls;
    *out += 1;
}

struct Call {
    int64_t a, b;
    int64_t result = 0;
    int64_t seen_result = -1;
};

void fault_now() {
    volatile int* p = nullptr;
    *p = 1;  // a real access violation, as an observer reading a stale pointer would raise
}
void before(void* p) {
    ++g_before_runs;
    if (g_fault_before) fault_now();
    (void)p;
}
void after(void* p) {
    ++g_after_runs;
    auto* c = static_cast<Call*>(p);
    c->seen_result = c->result;
    if (g_fault_after) fault_now();
}

repl::Guard g_guard{"test-hook"};
int64_t detoured(int64_t a, int64_t b) {
    Call c{a, b};
    return repl::detour(g_guard, &c, &before, &after, &original, a, b);
}

std::vector<std::string> g_lines;
int flush(repl::Guard& g) {
    repl::Guard* list[] = {&g};
    return repl::flush_faults(list, 1, [](uintptr_t w, char* out, size_t cap) { std::snprintf(out, cap, "%s", w > 1 ? "addr" : "?"); },
                              [](const char* line) { g_lines.emplace_back(line); });
}

void fault_rule() {
    // a call without a fault: both observers run, the original once, the result through
    CHECK(detoured(2, 3) == 2003);
    CHECK(g_original_calls == 1 && g_before_runs == 1 && g_after_runs == 1);
    CHECK(!g_guard.disabled.load() && g_guard.faults.load() == 0);
    CHECK(flush(g_guard) == 0 && g_lines.empty());

    // a fault in the observer before the original: the original still runs once, its result is returned untouched
    g_fault_before = true;
    CHECK(detoured(7, 9) == 7009);
    CHECK(g_original_calls == 2);
    CHECK(g_guard.disabled.load() && g_guard.faults.load() == 1 && g_guard.where.load() > 1);
    CHECK(g_after_runs == 1);  // disabled: the after observer does not run in the same call either

    // disabled: pass-through, no observer runs, the original once per call
    g_fault_before = false;
    CHECK(detoured(4, 5) == 4005);
    CHECK(g_original_calls == 3 && g_before_runs == 2 && g_after_runs == 1);

    // one log line per hook per session
    CHECK(flush(g_guard) == 1 && g_lines.size() == 1);
    CHECK(!g_lines.empty() && g_lines[0] == "repl: observer test-hook faulted at addr; disabled");
    CHECK(flush(g_guard) == 0 && g_lines.size() == 1);

    // a fault after the original: the result is the original's, the after observer saw it
    repl::Guard g2{"after-hook"};
    g_fault_after = true;
    Call c{11, 12};
    const int64_t r = repl::detour(g2, &c, nullptr, &after, &original, int64_t{11}, int64_t{12});
    CHECK(r == 11012 && c.result == 11012 && c.seen_result == 11012);
    CHECK(g_original_calls == 4 && g2.disabled.load() && g2.faults.load() == 1);
    g_fault_after = false;

    // a void original: runs once, faulting observer or not
    repl::Guard g3{"void-hook"};
    int64_t v = 0;
    g_fault_before = true;
    struct VoidCall {
    } vc;
    repl::detour(g3, &vc, &before, nullptr, &original_void, &v);
    repl::detour(g3, &vc, &before, nullptr, &original_void, &v);
    g_fault_before = false;
    CHECK(v == 2 && g_original_calls == 6 && g3.disabled.load() && g3.faults.load() == 1);

    // guards fault independently
    CHECK(!repl::Guard{"fresh"}.disabled.load());
}

}  // namespace

int main() {
    abi();
    fault_rule();
    std::printf("repl host tests: %d checks, %d failed\n", g_checks, g_failed);
    return g_failed ? 1 : 0;
}
