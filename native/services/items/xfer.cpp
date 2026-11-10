// SPDX-License-Identifier: MIT
// kiln.items: the hooks on the game's quick stack and move all, and transfer rules, handlers and reports
// (docs/re/items.md section 13).
//
// Behaviour ported from shroudlink's inventory events (MIT, bjck/shroudlink v0.8.0, native/plugins/shroudlink/upstream/
// server/src/events.rs and game_api.rs): the two functions are hooked, the mods may keep stacks and move stacks first, then
// the game's own transfer runs with the kept stacks hidden from it (their count reads 0 for the call). Differences: the
// game's own caller is never changed (no player stacks are hidden unless a rule or handler says so), a cancel skips the
// game's transfer, rules are data (no code runs), and a report goes to the tick thread afterwards.
//
// Threads: the hooks run inside the game's inventory program, on a game worker thread. The registry is a snapshot
// replaced as a whole (a registration copies it), so the hook never takes a lock. Handlers run there; reports are
// queued and delivered in Pre.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <vector>

#include "engine.h"
#include "names.h"
#include "safetyhook.hpp"
#include "xfer.h"

namespace kitems {

// ---- the mark a kiln.items move leaves for the hook ----
namespace {
thread_local bool t_by_mod = false;
thread_local bool t_silent = false;
thread_local bool t_canceled = false;
thread_local bool t_dry_run = false;
}  // namespace

void xfer_mod_begin(bool silent, bool dry_run) {
    t_by_mod = true;
    t_silent = silent;
    t_canceled = false;
    t_dry_run = dry_run;
}

bool xfer_mod_end() {
    const bool c = t_canceled;
    t_by_mod = t_silent = t_canceled = t_dry_run = false;
    return c;
}

namespace {

constexpr uint32_t kMaxPerKind = 256;
constexpr size_t kMaxReports = 256;

// Shared by every registry snapshot containing the callback. A lease cannot start after stop(), and an external
// cancellation waits for every lease that started before it. Reentrant cancellation only invalidates: waiting from
// inside a callback could deadlock itself or another callback that is waiting on it.
struct CallbackLife {
    static constexpr uint64_t Stopped = 1ull << 63;
    std::atomic<uint64_t> state{0};
    bool enter() {
        auto n = state.load(std::memory_order_acquire);
        while (!(n & Stopped))
            if (state.compare_exchange_weak(n, n + 1, std::memory_order_acq_rel)) return true;
        return false;
    }
    void leave() {
        state.fetch_sub(1, std::memory_order_release);
        state.notify_all();
    }
    void stop() { state.fetch_or(Stopped, std::memory_order_acq_rel); }
    bool idle() const { return (state.load(std::memory_order_acquire) & ~Stopped) == 0; }
    void drain() {
        auto n = state.load(std::memory_order_acquire);
        while (n & ~Stopped) {
            state.wait(n, std::memory_order_acquire);
            n = state.load(std::memory_order_acquire);
        }
    }
};
thread_local uint32_t t_callback_depth = 0;

struct RuleRec {
    uint64_t handle;
    KilnOwner owner;
    KilnItemsTransferRule r;
};
struct HandlerRec {
    uint64_t handle;
    KilnOwner owner;
    KilnItemsTransferFn fn;
    void* user;
    std::shared_ptr<CallbackLife> life;
};
struct ReporterRec {
    uint64_t handle;
    KilnOwner owner;
    KilnItemsTransferReportFn fn;
    void* user;
    std::shared_ptr<CallbackLife> life;
};
struct Registry {
    std::vector<RuleRec> rules;
    std::vector<RuleView> views;  // the rules, as the logic module reads them
    std::vector<HandlerRec> handlers;
    std::vector<ReporterRec> reporters;
};

std::mutex g_mu;  // writers (registrations); the hooks only read the snapshot
struct RetiredCallback { uint64_t handle; KilnOwner owner; std::shared_ptr<CallbackLife> life; };
std::vector<RetiredCallback> g_retired;  // g_mu: cancellation also drains callbacks another thread already removed

void prune_retired() {  // g_mu held; stopped callbacks can never become active again
    std::erase_if(g_retired, [](const auto& r) { return r.life->idle(); });
}
void finish_retirement(const std::vector<std::shared_ptr<CallbackLife>>& lives) {
    if (t_callback_depth != 0 || lives.empty()) return;
    for (const auto& life : lives) life->drain();
    std::lock_guard lock(g_mu);
    prune_retired();
}
std::atomic<std::shared_ptr<const Registry>> g_reg{std::make_shared<const Registry>()};
std::atomic<uint32_t> g_active{0};  // rules + handlers + reporters: 0 = the hooks pass straight through
std::atomic<uint64_t> g_next{1ull << 40};
std::atomic<uint64_t> g_seen{0}, g_canceled{0};
std::atomic<uint64_t> g_step{0};
std::atomic<bool> g_installed{false};
bool g_install_failed = false;  // g_mu
std::atomic<bool> g_broken{false};  // the game's transaction did not match what the hook expects: pass through
char g_detail[160] = "";         // g_mu

std::atomic<SafetyHookInline*> h_qs{nullptr};  // never destroyed: another loader may hook on top
std::atomic<SafetyHookInline*> h_ma{nullptr};

std::deque<KilnItemsTransferReport> g_reports;  // g_mu
std::atomic<uint32_t> g_reporters{0};

std::shared_ptr<const Registry> snapshot() { return g_reg.load(); }

void rebuild(std::shared_ptr<Registry> r) {  // g_mu held
    r->views.clear();
    for (const RuleRec& x : r->rules)
        r->views.push_back(RuleView{x.r.type, x.r.kinds, x.r.entity, x.r.item, x.r.slot});
    g_active.store(static_cast<uint32_t>(r->rules.size() + r->handlers.size() + r->reporters.size()), std::memory_order_release);
    g_reporters.store(static_cast<uint32_t>(r->reporters.size()), std::memory_order_release);
    g_reg.store(std::shared_ptr<const Registry>(std::move(r)));
}

// ---- what one hooked call knows ----
struct Hidden {
    uint8_t* stack[kMaxKeeps];
    uint32_t count[kMaxKeeps];
    uint32_t n = 0;
};

struct Call {
    void* tx = nullptr;
    Access a;
    uint32_t kind = 0, from = 0, to = 0, player = 0, flags = 0;
    PageSet from_pages;
    Verdict verdict;
    uint32_t extra_moved = 0;
    Hidden hidden;
};

// ---- guarded game access (plain data in, plain data out) ----
bool prepare(Call* c) {
    __try {
        if (!access_from_tx(c->tx, &c->a)) return false;
        bool init = false;
        if (!read_pages(c->a, c->from, &c->from_pages, &init)) c->from_pages.n = 0;
        PlayerGuard gf, gt;
        player_guard(c->a, c->from, &gf);
        player_guard(c->a, c->to, &gt);
        c->player = gf.is_player ? c->from : gt.is_player ? c->to : 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Items the source lost since prepare() read it (handlers' own moves and the game's transfer together): the game's own
// result is not an item count for every function (move all reports something else).
uint32_t lost_items(Call* c) {
    __try {
        PageSet after;
        bool init = false;
        if (!read_pages(c->a, c->from, &after, &init) || after.n != c->from_pages.n) return 0;
        uint64_t before = 0, now = 0;
        for (uint32_t i = 0; i < after.n; ++i)
            for (uint32_t k = 0; k < kSlotsPerPage; ++k) {
                before += is_empty(c->from_pages.p[i].s[k]) ? 0 : c->from_pages.p[i].s[k].count;
                now += is_empty(after.p[i].s[k]) ? 0 : after.p[i].s[k].count;
            }
        return before > now ? static_cast<uint32_t>(before - now) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

uint8_t* live_stack_ptr(const Access& a, uint64_t slot) {
    if ((slot >> 40) != 0 || KILN_SLOT_INDEX(slot) > 7) return nullptr;
    PtrSize ps{};
    E.read_other(&ps, a.storage, a.acc[A_INVENTORY], KILN_SLOT_INVENTORY(slot));
    if (!ps.ptr || ps.size < kInventorySize) return nullptr;
    return static_cast<uint8_t*>(ps.ptr) + 12 * KILN_SLOT_INDEX(slot);
}

bool source_slot(const Call& c, uint64_t slot) {
    if ((slot >> 40) != 0 || KILN_SLOT_INDEX(slot) >= kSlotsPerPage) return false;
    for (uint32_t p = 0; p < c.from_pages.n; ++p)
        if (c.from_pages.p[p].entity == KILN_SLOT_INVENTORY(slot)) return true;
    return false;
}

void hide(Call* c) {
    c->hidden.n = 0;
    for (uint32_t i = 0; i < c->verdict.keep_count && c->hidden.n < kMaxKeeps; ++i) {
        if (!source_slot(*c, c->verdict.keep[i])) continue;
        uint8_t* st = live_stack_ptr(c->a, c->verdict.keep[i]);
        if (!st) continue;
        uint32_t cnt;
        std::memcpy(&cnt, st + 4, 4);
        if (cnt == 0) continue;
        const uint32_t zero = 0;
        std::memcpy(st + 4, &zero, 4);
        // A failed write changed nothing: only successfully hidden stacks need restoring.
        c->hidden.stack[c->hidden.n] = st;
        c->hidden.count[c->hidden.n++] = cnt;
    }
}

void restore(Call* c) {
    for (uint32_t i = 0; i < c->hidden.n; ++i) std::memcpy(c->hidden.stack[i] + 4, &c->hidden.count[i], 4);
    c->hidden.n = 0;
}

using Vanilla = Result12* (*)(void*);

// The game's call with the kept stacks hidden; they come back even when the game's code faults.
Result12* call_hidden(Call* c, Vanilla vanilla, void* arg) {
    Result12* r = nullptr;
    __try {
        __try {
            hide(c);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            // hide can fault after it already zeroed earlier stacks. Put those back before passing through.
            restore(c);
        }
        r = vanilla(arg);
    } __finally {
        restore(c);
    }
    return r;
}

// ---- what a handler may call ----
Call* as_call(KilnItemsTransferCtx* ctx) { return reinterpret_cast<Call*>(ctx); }

uint32_t KILN_CALL host_read(KilnItemsTransferCtx* ctx, KilnEntity entity, KilnItemsSlot* out, uint32_t capacity) {
    Call* c = as_call(ctx);
    if (!c || !out || !capacity) return 0;
    return read_slots(c->a, entity, out, capacity);
}
void KILN_CALL host_keep(KilnItemsTransferCtx* ctx, KilnSlotId slot) {
    if (Call* c = as_call(ctx); c && source_slot(*c, slot)) keep_slot(&c->verdict, slot);
}
void KILN_CALL host_cancel(KilnItemsTransferCtx* ctx) {
    if (Call* c = as_call(ctx)) c->verdict.deny = true;
}
uint32_t KILN_CALL host_move(KilnItemsTransferCtx* ctx, KilnSlotId slot, KilnEntity to, uint32_t count) {
    Call* c = as_call(ctx);
    if (!c || to == 0) return 0;
    MoveOutcome mo;
    const uint32_t moved = move_stack_in(c->a, c->tx, c->from, slot, to, count, 0, false, false, &mo);
    c->extra_moved += moved;
    return moved;
}
uint32_t KILN_CALL host_name(KilnItemsTransferCtx* ctx, KilnEntity entity, char* buf, uint32_t capacity) {
    Call* c = as_call(ctx);
    if (!c || !buf || !capacity) return 0;
    const uintptr_t world = reinterpret_cast<uintptr_t>(c->a.storage) - static_cast<uintptr_t>(E.world_storage);
    uint32_t len = 0;
    const KilnResult rc = name_of(world, entity, buf, capacity, &len);
    return (rc == KILN_OK || rc == KILN_ERR_BUFFER_TOO_SMALL) ? len : 0;
}

const KilnItemsTransferHost g_host = {sizeof(KilnItemsTransferHost), &host_read, &host_keep, &host_cancel, &host_move,
                                      &host_name};

std::atomic<bool> g_handler_fault_warned{false};

bool call_reporter(const ReporterRec& l, const KilnItemsTransferReport* r, unsigned long* code) {
    __try {
        l.fn(r, l.user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool call_handler(const HandlerRec& h, const KilnItemsTransferEvent* ev, unsigned long* code) {
    __try {
        h.fn(ev, &g_host, h.user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// ---- the hook proper ----
struct QsArgs {
    Result12* out;
    void* tx;
    uint32_t to, from;
    void* feedback;
};
struct MaArgs {
    Result12* out;
    void* tx;
    uint32_t from, to, mode;
    uint8_t flag;
};

Result12* vanilla_qs(void* p) {
    auto* a = static_cast<QsArgs*>(p);
    return h_qs.load(std::memory_order_acquire)->call<Result12*>(a->out, a->tx, a->to, a->from, a->feedback);
}
Result12* vanilla_ma(void* p) {
    auto* a = static_cast<MaArgs*>(p);
    return h_ma.load(std::memory_order_acquire)->call<Result12*>(a->out, a->tx, a->from, a->to, a->mode, a->flag);
}

void queue_report(const Call& c, uint32_t flags, uint32_t moved) {
    KilnItemsTransferReport r{};
    r.size = sizeof r;
    r.kind = c.kind;
    r.from = c.from;
    r.to = c.to;
    r.player = c.player;
    r.flags = flags;
    r.moved = moved;
    r.kept = c.verdict.keep_count;
    r.step = g_step.load(std::memory_order_relaxed);
    std::lock_guard lock(g_mu);
    if (g_reports.size() >= kMaxReports) g_reports.pop_front();
    g_reports.push_back(r);
}

Result12* around(uint32_t kind, void* tx, uint32_t from, uint32_t to, Vanilla vanilla, void* arg, Result12* out) {
    g_seen.fetch_add(1, std::memory_order_relaxed);
    if (g_broken.load(std::memory_order_relaxed)) return vanilla(arg);
    const bool by_mod = t_by_mod;
    const bool report = !(by_mod && t_dry_run);
    if (by_mod && t_silent) return vanilla(arg);
    const auto reg = snapshot();
    if (!reg || (reg->rules.empty() && reg->handlers.empty() && reg->reporters.empty())) return vanilla(arg);

    Call c;
    c.tx = tx;
    c.kind = kind;
    c.from = from;
    c.to = to;
    c.flags = by_mod ? (KILN_TRANSFER_BY_MOD | (t_dry_run ? KILN_TRANSFER_DRY_RUN : 0u)) : 0u;
    if (!prepare(&c)) {
        if (!g_broken.exchange(true))
            items_log(KILN_LOG_WARN, "transfer hooks: the game's transaction does not match the layout kiln.items knows; "
                      "quick stack and move all pass straight through");
        return vanilla(arg);
    }
    apply_rules(reg->views.data(), reg->views.size(), kind, from, to, c.from_pages.p, c.from_pages.n, &c.verdict);
    for (const HandlerRec& h : reg->handlers) {
        if (!h.life->enter()) continue;
        KilnItemsTransferEvent ev{};
        ev.size = sizeof ev;
        ev.kind = kind;
        ev.from = from;
        ev.to = to;
        ev.player = c.player;
        ev.flags = c.flags;
        ev.ctx = reinterpret_cast<KilnItemsTransferCtx*>(&c);
        unsigned long code = 0;
        ++t_callback_depth;
        const bool ok = call_handler(h, &ev, &code);
        --t_callback_depth;
        h.life->leave();
        if (!ok && !g_handler_fault_warned.exchange(true))
            items_log(KILN_LOG_ERROR, "a transfer handler (owner %llu) faulted (0x%08lx); it is skipped for this transfer",
                      static_cast<unsigned long long>(h.owner), code);
    }
    uint32_t moved = 0;
    uint32_t flags = c.flags;
    if (c.verdict.deny) {
        g_canceled.fetch_add(1, std::memory_order_relaxed);
        if (by_mod) t_canceled = true;
        flags |= KILN_TRANSFER_CANCELED;
        std::memset(out, 0, sizeof(Result12));  // ok, nothing moved by the game
        out->value = c.extra_moved;
        moved = lost_items(&c);
    } else {
        Result12* r = call_hidden(&c, vanilla, arg);
        if (r && r->error == 0) {
            r->value += c.extra_moved;
            moved = reg->reporters.empty() ? r->value : lost_items(&c);
        }
        out = r;
    }
    if (report && !reg->reporters.empty()) queue_report(c, flags, moved);
    return out;
}

Result12* __fastcall hk_quick_stack(Result12* out, void* tx, uint32_t to, uint32_t from, void* feedback) {
    QsArgs a{out, tx, to, from, feedback};
    if (g_active.load(std::memory_order_acquire) == 0) return vanilla_qs(&a);
    return around(KILN_TRANSFER_QUICK_STACK, tx, from, to, &vanilla_qs, &a, out);
}

Result12* __fastcall hk_move_all(Result12* out, void* tx, uint32_t from, uint32_t to, uint32_t mode, uint8_t flag) {
    MaArgs a{out, tx, from, to, mode, flag};
    if (g_active.load(std::memory_order_acquire) == 0) return vanilla_ma(&a);
    return around(KILN_TRANSFER_MOVE_ALL, tx, from, to, &vanilla_ma, &a, out);
}

// The first registration installs the hooks (g_mu held). Chains with any hook already on the functions: safetyhook
// copies a jump at the entry into its trampoline, so the other loader's hook still runs, after ours.
using HookFactory = SafetyHookInline* (*)(void*, void*);
SafetyHookInline* make_hook(void* target, void* replacement) {
    return new SafetyHookInline(safetyhook::create_inline(target, replacement, SafetyHookInline::StartDisabled));
}
bool install_locked(HookFactory create = &make_hook) {
    if (g_installed.load()) return true;
    if (g_install_failed) return false;
    if (!E.transfer_ok || !E.quick_stack || !E.move_all) {
        g_install_failed = true;
        std::snprintf(g_detail, sizeof g_detail, "the quick-stack / move-all hook points are not available on this build");
        return false;
    }
    auto* qs = create(reinterpret_cast<void*>(E.quick_stack), reinterpret_cast<void*>(&hk_quick_stack));
    auto* ma = create(reinterpret_cast<void*>(E.move_all), reinterpret_cast<void*>(&hk_move_all));
    // Publish both trampolines before enabling either hook. An enable failure may leave one active: it must still
    // pass through safely, even though registration fails and g_active remains zero.
    h_qs.store(qs, std::memory_order_release);
    h_ma.store(ma, std::memory_order_release);
    if (!qs || !ma || !*qs || !*ma || !qs->enable() || !ma->enable()) {
        // kept on purpose: a half-installed pair is never taken apart
        g_install_failed = true;
        std::snprintf(g_detail, sizeof g_detail, "hooking quick stack / move all failed");
        items_log(KILN_LOG_ERROR, "transfer hooks: hooking the game's quick stack / move all failed; transfer rules, handlers and "
                  "reports are off");
        return false;
    }
    g_installed.store(true);
    items_log(KILN_LOG_INFO, "transfer hooks: quick stack %p and move all %p hooked (chained with any hook already there; never "
              "removed)", reinterpret_cast<void*>(E.quick_stack), reinterpret_cast<void*>(E.move_all));
    return true;
}

bool rule_ok(const KilnItemsTransferRule* r) {
    if (!r || r->size < sizeof(KilnItemsTransferRule) || r->reserved != 0) return false;
    if (r->kinds & ~static_cast<uint32_t>(KILN_TRANSFER_ANY)) return false;
    switch (r->type) {
        case KILN_TRANSFER_KEEP_SLOT:
            return r->slot != 0 && (r->slot >> 40) == 0 && KILN_SLOT_INDEX(r->slot) <= 7 && KILN_SLOT_INVENTORY(r->slot) != 0;
        case KILN_TRANSFER_KEEP_ITEM:
            return r->item != 0;
        case KILN_TRANSFER_DENY:
            return r->entity != 0;
        default:
            return false;
    }
}

}  // namespace

// Lab switch: KILN_ITEMS_HOOKS=early installs the hooks at load, before another loader's hook (kiln-shroudlink starts
// later), so a lab can run the hook chain in both orders. Normally the first registration installs them.
void xfer_init() {
    char v[16] = "";
    if (GetEnvironmentVariableA("KILN_ITEMS_HOOKS", v, sizeof v) == 0 || std::strcmp(v, "early") != 0) return;
    std::lock_guard lock(g_mu);
    if (install_locked()) items_log(KILN_LOG_INFO, "transfer hooks: installed early (KILN_ITEMS_HOOKS=early, a lab switch)");
}

void xfer_set_step(uint64_t step) { g_step.store(step, std::memory_order_relaxed); }

KilnResult xfer_add_rule(KilnOwner owner, const KilnItemsTransferRule* rule, uint64_t* handle) {
    if (!rule_ok(rule) || !handle) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (!E.transfer_ok) return KILN_ERR_UNAVAILABLE;
    auto cur = snapshot();
    if (cur->rules.size() >= kMaxPerKind) return KILN_ERR_UNAVAILABLE;
    if (!install_locked()) return KILN_ERR_UNAVAILABLE;
    auto next = std::make_shared<Registry>(*cur);
    RuleRec rec{g_next.fetch_add(1), owner, *rule};
    rec.r.size = sizeof(KilnItemsTransferRule);
    next->rules.push_back(rec);
    *handle = rec.handle;
    rebuild(std::move(next));
    return KILN_OK;
}

KilnResult xfer_add_handler(KilnOwner owner, KilnItemsTransferFn fn, void* user, uint64_t* handle) {
    if (!fn || !handle) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (!E.transfer_ok) return KILN_ERR_UNAVAILABLE;
    auto cur = snapshot();
    if (cur->handlers.size() >= kMaxPerKind) return KILN_ERR_UNAVAILABLE;
    if (!install_locked()) return KILN_ERR_UNAVAILABLE;
    auto next = std::make_shared<Registry>(*cur);
    prune_retired();
    HandlerRec rec{g_next.fetch_add(1), owner, fn, user, std::make_shared<CallbackLife>()};
    next->handlers.push_back(rec);
    *handle = rec.handle;
    rebuild(std::move(next));
    return KILN_OK;
}

KilnResult xfer_add_reporter(KilnOwner owner, KilnItemsTransferReportFn fn, void* user, uint64_t* handle) {
    if (!fn || !handle) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    if (!E.transfer_ok) return KILN_ERR_UNAVAILABLE;
    auto cur = snapshot();
    if (cur->reporters.size() >= kMaxPerKind) return KILN_ERR_UNAVAILABLE;
    if (!install_locked()) return KILN_ERR_UNAVAILABLE;
    auto next = std::make_shared<Registry>(*cur);
    prune_retired();
    ReporterRec rec{g_next.fetch_add(1), owner, fn, user, std::make_shared<CallbackLife>()};
    next->reporters.push_back(rec);
    *handle = rec.handle;
    rebuild(std::move(next));
    return KILN_OK;
}

KilnResult xfer_end(uint64_t handle) {
    std::vector<std::shared_ptr<CallbackLife>> drain;
    bool found = false;
    {
        std::lock_guard lock(g_mu);
        prune_retired();
        auto next = std::make_shared<Registry>(*snapshot());
        auto drop = [&](auto& v) {
            for (auto it = v.begin(); it != v.end(); ++it)
                if (it->handle == handle) {
                    if constexpr (requires { it->life; }) {
                        it->life->stop();
                        g_retired.push_back({it->handle, it->owner, it->life});
                    }
                    v.erase(it);
                    found = true;
                    return;
                }
        };
        drop(next->rules);
        if (!found) drop(next->handlers);
        if (!found) drop(next->reporters);
        if (found) rebuild(std::move(next));
        // Another thread may already have removed this handle but still be draining its callback.
        for (const auto& r : g_retired) if (r.handle == handle) drain.push_back(r.life);
    }
    finish_retirement(drain);  // never wait while holding the registry mutex
    return found ? KILN_OK : KILN_ERR_NOT_FOUND;
}

void xfer_end_owner(KilnOwner owner) {
    std::vector<std::shared_ptr<CallbackLife>> drain;
    {
        std::lock_guard lock(g_mu);
        prune_retired();
        auto next = std::make_shared<Registry>(*snapshot());
        const size_t before = next->rules.size() + next->handlers.size() + next->reporters.size();
        auto gone = [owner](const auto& r) { return r.owner == owner; };
        std::erase_if(next->rules, gone);
        auto retire = [owner](const auto& r) {
            if (r.owner != owner) return false;
            r.life->stop();
            g_retired.push_back({r.handle, r.owner, r.life});
            return true;
        };
        std::erase_if(next->handlers, retire);
        std::erase_if(next->reporters, retire);
        if (next->rules.size() + next->handlers.size() + next->reporters.size() != before) rebuild(std::move(next));
        for (const auto& r : g_retired) if (r.owner == owner) drain.push_back(r.life);
    }
    finish_retirement(drain);
}

void xfer_deliver() {
    if (g_reporters.load(std::memory_order_acquire) == 0) {
        std::lock_guard lock(g_mu);
        g_reports.clear();
        return;
    }
    std::deque<KilnItemsTransferReport> out;
    {
        std::lock_guard lock(g_mu);
        if (g_reports.empty()) return;
        out.swap(g_reports);
    }
    const auto reg = snapshot();
    for (const KilnItemsTransferReport& r : out) {
        for (const ReporterRec& l : reg->reporters) {
            if (!l.life->enter()) continue;
            unsigned long code = 0;
            ++t_callback_depth;
            const bool ok = call_reporter(l, &r, &code);
            --t_callback_depth;
            l.life->leave();
            if (!ok)
                items_log(KILN_LOG_ERROR, "a transfer report callback (owner %llu) faulted (0x%08lx); ignored",
                          static_cast<unsigned long long>(l.owner), code);
        }
    }
}

void xfer_status(KilnItemsStatus2* st) {
    const auto reg = snapshot();
    st->transfer_hooks = E.transfer_ok ? 1 : 0;
    st->hooks_installed = g_installed.load() ? 1 : 0;
    st->rules = static_cast<uint32_t>(reg->rules.size());
    st->handlers = static_cast<uint32_t>(reg->handlers.size());
    st->reporters = static_cast<uint32_t>(reg->reporters.size());
    st->transfers_seen = g_seen.load();
    st->transfers_canceled = g_canceled.load();
    if (g_broken.load()) std::snprintf(st->detail, sizeof st->detail, "the transfer hooks pass through: the game's transaction layout did not check out");
    else {
        std::lock_guard lock(g_mu);
        if (g_detail[0]) std::snprintf(st->detail, sizeof st->detail, "%s", g_detail);
    }
}

}  // namespace kitems
