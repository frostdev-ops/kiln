// SPDX-License-Identifier: MIT
#include "registry.h"

#include <mutex>
#include <unordered_map>

#include "guard.h"
#include "log.h"

namespace kiln::hooks::seh {
// hooks_seh.cpp: the observer call under a structured-exception guard (false = it faulted).
bool call_owner_retiring(void(KILN_CALL* fn)(KilnOwner, void*), KilnOwner retiring, void* user, unsigned long* code);
}  // namespace kiln::hooks::seh

namespace kiln::reg {
namespace {

struct RetiringSub {
    KilnHandle handle;
    KilnOwner owner;  // the observer
    OwnerRetiringFn fn;
    void* user;
    std::atomic<bool> live{true};
};

struct Task {
    KilnOwner owner;
    KilnTaskFn fn;
    void* user;
};

std::mutex g_mutex;
std::unordered_map<KilnOwner, std::string> g_owners;  // live owners
KilnOwner g_next_owner = 1;
KilnHandle g_next_handle = 1;
std::vector<std::shared_ptr<TickSub>> g_tick[2];
std::vector<std::shared_ptr<LogSub>> g_log;
std::atomic<uint64_t> g_version{1};
std::atomic<size_t> g_log_count{0};
std::vector<std::shared_ptr<RetiringSub>> g_retiring;  // under g_mutex

std::mutex g_task_mutex;
std::vector<Task> g_tasks;

void bump() { g_version.fetch_add(1, std::memory_order_acq_rel); }

}  // namespace

KilnOwner owner_create(std::string_view id) {
    std::lock_guard lock(g_mutex);
    const KilnOwner o = g_next_owner++;
    g_owners.emplace(o, std::string(id));
    return o;
}

std::string owner_name(KilnOwner owner) {
    if (owner == 0) return "kiln";
    std::lock_guard lock(g_mutex);
    auto it = g_owners.find(owner);
    return it == g_owners.end() ? "(revoked)" : it->second;
}

bool owner_alive(KilnOwner owner) {
    if (owner == 0) return true;
    std::lock_guard lock(g_mutex);
    return g_owners.count(owner) != 0;
}

void owner_revoke(KilnOwner owner) {
    if (owner == 0) return;
    size_t removed = 0;
    std::vector<std::shared_ptr<RetiringSub>> observers;  // called below, outside the locks
    {
        std::lock_guard lock(g_mutex);
        const bool was_alive = g_owners.erase(owner) != 0;
        std::erase_if(g_retiring, [&](const std::shared_ptr<RetiringSub>& s) {
            if (s->owner != owner) return false;
            s->live.store(false, std::memory_order_release);
            ++removed;
            return true;
        });
        if (was_alive) observers = g_retiring;  // a second revoke of the same owner tells nobody again
        for (auto& list : g_tick) {
            std::erase_if(list, [&](const std::shared_ptr<TickSub>& s) {
                if (s->owner != owner) return false;
                s->live.store(false, std::memory_order_release);
                ++removed;
                return true;
            });
        }
        std::erase_if(g_log, [&](const std::shared_ptr<LogSub>& s) {
            if (s->owner != owner) return false;
            s->live.store(false, std::memory_order_release);
            ++removed;
            return true;
        });
        g_log_count.store(g_log.size());
    }
    {
        std::lock_guard lock(g_task_mutex);
        std::erase_if(g_tasks, [&](const Task& t) { return t.owner == owner; });
    }
    bump();
    KLOG_D("owner %llu revoked (%zu subscriptions)", static_cast<unsigned long long>(owner), removed);
    for (const auto& s : observers) {
        if (!s->live.load(std::memory_order_acquire)) continue;  // unsubscribed (perhaps by an earlier callback)
        unsigned long code = 0;
        if (hooks::seh::call_owner_retiring(s->fn, owner, s->user, &code)) continue;
        KLOG_E("an owner-retiring observer of owner %llu faulted (0x%08lx) and was removed",
               static_cast<unsigned long long>(s->owner), code);
        remove_owner_retiring(s->handle);
    }
}

KilnResult add_owner_retiring(KilnOwner observer, OwnerRetiringFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    if (observer != 0 && !g_owners.count(observer)) return KILN_ERR_REVOKED;
    auto s = std::make_shared<RetiringSub>();
    s->handle = g_next_handle++;
    s->owner = observer;
    s->fn = fn;
    s->user = user;
    g_retiring.push_back(s);
    if (out) *out = s->handle;
    return KILN_OK;
}

KilnResult remove_owner_retiring(KilnHandle handle) {
    std::lock_guard lock(g_mutex);
    const size_t n = std::erase_if(g_retiring, [&](const std::shared_ptr<RetiringSub>& s) {
        if (s->handle != handle) return false;
        s->live.store(false, std::memory_order_release);
        return true;
    });
    return n ? KILN_OK : KILN_ERR_NOT_FOUND;
}

size_t owner_retiring_count() {
    std::lock_guard lock(g_mutex);
    return g_retiring.size();
}

KilnResult add_tick(KilnOwner owner, int phase, KilnTickFn fn, void* user, KilnHandle* out) {
    if (!fn || (phase != KILN_TICK_PRE && phase != KILN_TICK_POST)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    if (owner != 0 && !g_owners.count(owner)) return KILN_ERR_REVOKED;
    auto s = std::make_shared<TickSub>();
    s->handle = g_next_handle++;
    s->owner = owner;
    s->phase = phase;
    s->fn = fn;
    s->user = user;
    g_tick[phase].push_back(s);
    if (out) *out = s->handle;
    bump();
    return KILN_OK;
}

KilnResult add_log(KilnOwner owner, KilnLogLineFn fn, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mutex);
    if (owner != 0 && !g_owners.count(owner)) return KILN_ERR_REVOKED;
    auto s = std::make_shared<LogSub>();
    s->handle = g_next_handle++;
    s->owner = owner;
    s->fn = fn;
    s->user = user;
    g_log.push_back(s);
    g_log_count.store(g_log.size());
    if (out) *out = s->handle;
    bump();
    return KILN_OK;
}

KilnResult remove(KilnHandle handle) {
    bool found = false;
    {
        std::lock_guard lock(g_mutex);
        for (auto& list : g_tick) {
            std::erase_if(list, [&](const std::shared_ptr<TickSub>& s) {
                if (s->handle != handle) return false;
                s->live.store(false, std::memory_order_release);
                found = true;
                return true;
            });
        }
        std::erase_if(g_log, [&](const std::shared_ptr<LogSub>& s) {
            if (s->handle != handle) return false;
            s->live.store(false, std::memory_order_release);
            found = true;
            return true;
        });
        g_log_count.store(g_log.size());
    }
    if (found) bump();
    return found ? KILN_OK : KILN_ERR_NOT_FOUND;
}

uint64_t version() { return g_version.load(std::memory_order_acquire); }

std::vector<std::shared_ptr<TickSub>> tick_subs(int phase) {
    std::lock_guard lock(g_mutex);
    return g_tick[phase];
}

std::vector<std::shared_ptr<LogSub>> log_subs() {
    std::lock_guard lock(g_mutex);
    return g_log;
}

bool log_subs_empty_hint() { return g_log_count.load(std::memory_order_relaxed) == 0; }

size_t tick_sub_count(int phase) {
    std::lock_guard lock(g_mutex);
    return g_tick[phase].size();
}

KilnResult post(KilnOwner owner, KilnTaskFn fn, void* user) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    if (!owner_alive(owner)) return KILN_ERR_REVOKED;
    std::lock_guard lock(g_task_mutex);
    g_tasks.push_back(Task{owner, fn, user});
    return KILN_OK;
}

size_t drain_posts() {
    std::vector<Task> tasks;
    {
        std::lock_guard lock(g_task_mutex);
        if (g_tasks.empty()) return 0;
        tasks.swap(g_tasks);
    }
    for (const Task& t : tasks) {
        if (!owner_alive(t.owner)) continue;
        unsigned long code = 0;
        if (!guard::task(t.fn, t.user, &code))
            KLOG_E("posted task of '%s' faulted (0x%08lx)", owner_name(t.owner).c_str(), code);
    }
    return tasks.size();
}

}  // namespace kiln::reg
