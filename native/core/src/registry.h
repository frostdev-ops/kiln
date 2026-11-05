// SPDX-License-Identifier: MIT
// Owners and everything they register: tick subscriptions, game-log subscriptions, queued tasks.
#pragma once
#include <atomic>
#include <memory>
#include <string>
#include <vector>

#include "common.h"

namespace kiln::reg {

struct TickSub {
    KilnHandle handle;
    KilnOwner owner;
    int phase;
    KilnTickFn fn;
    void* user;
    std::atomic<bool> live{true};
    uint32_t faults = 0;
};

struct LogSub {
    KilnHandle handle;
    KilnOwner owner;
    KilnLogLineFn fn;
    void* user;
    std::atomic<bool> live{true};
};

KilnOwner owner_create(std::string_view id);
std::string owner_name(KilnOwner owner);
bool owner_alive(KilnOwner owner);
void owner_revoke(KilnOwner owner);

KilnResult add_tick(KilnOwner owner, int phase, KilnTickFn fn, void* user, KilnHandle* out);
KilnResult add_log(KilnOwner owner, KilnLogLineFn fn, void* user, KilnHandle* out);
KilnResult remove(KilnHandle handle);

// Snapshot of live subscribers; rebuilt only when `version()` changed since the caller's last snapshot.
uint64_t version();
std::vector<std::shared_ptr<TickSub>> tick_subs(int phase);
std::vector<std::shared_ptr<LogSub>> log_subs();
bool log_subs_empty_hint();  // lock-free; may be briefly stale
size_t tick_sub_count(int phase);

// Cross-thread task queue drained on the tick thread at Pre.
KilnResult post(KilnOwner owner, KilnTaskFn fn, void* user);
size_t drain_posts();  // returns how many ran

}  // namespace kiln::reg
