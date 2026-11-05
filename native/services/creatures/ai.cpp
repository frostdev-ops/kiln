// SPDX-License-Identifier: MIT
// kiln.creatures: enemy AI observation (state and target events), the lab readiness switch.
//
// State changes: enemy01's HandleCondition(ctx, condition) is the only caller of SetState(ctx, state) on svn 1285601
// (one call site, docs/re/creatures.md 2.2). Both get a mid hook at their entry (worker threads, inside the sim step):
// HandleCondition remembers (ctx, condition) per thread; SetState reads the old state from the dynamic state
// (ctx + ctx.dyn), the entity from the program context (ctx + 0 -> CurrentEntityId) and queues an event. The hooks do
// nothing unless the listener asked for state events (one atomic load).
//
// Targets: SelectedTargets.targets[0] of creatures whose AI is active (a state event in the last 2 minutes) is
// compared every other Pre; a change is a target event.
#include <cstdio>
#include <unordered_set>

#include "internal.h"
#include "safetyhook.hpp"

namespace kcr {

AiStats AS;

namespace {

safetyhook::MidHook g_cond_hook, g_set_hook, g_behavior_hook;
std::atomic<uint32_t> g_interest{0};
std::atomic<bool> g_diag{false};
bool g_installed = false;

thread_local uintptr_t t_ctx = 0;
thread_local uint8_t t_cond = 255;

std::mutex g_ev_mu;
std::vector<KilnCreatureEvent> g_ev;  // filled by worker threads, drained in Pre
constexpr size_t kMaxEvents = 4096;

KilnCreatureListenerFn g_listener = nullptr;
void* g_listener_user = nullptr;

struct Active {
    uint8_t state = 0;
    KilnEntity target = 0;
    uint64_t last = 0;
};
std::unordered_map<KilnEntity, Active> g_active;
int64_t g_off_target = -1, g_off_target_root = -1;
bool g_targets_resolved = false;

std::unordered_map<KilnEntity, std::vector<KilnOwner>> g_forced;
int g_lab_switch = -1;  // -1 unknown

void hk_condition(safetyhook::Context& c) {
    if (!(g_interest.load(std::memory_order_relaxed) & KILN_CREATURE_MASK(KILN_CREATURE_EV_STATE))) return;
    t_ctx = c.rcx;
    t_cond = static_cast<uint8_t>(c.rdx & 0xFF);
}

void hk_set_state(safetyhook::Context& c) {
    if (!(g_interest.load(std::memory_order_relaxed) & KILN_CREATURE_MASK(KILN_CREATURE_EV_STATE))) return;
    AS.state_changes.fetch_add(1, std::memory_order_relaxed);
    const uintptr_t ctx = c.rcx;
    KilnCreatureEvent ev{};
    ev.size = sizeof ev;
    ev.kind = KILN_CREATURE_EV_STATE;
    ev.to = static_cast<uint8_t>(c.rdx & 0xFF);
    ev.condition = t_ctx == ctx ? t_cond : 255;
    t_ctx = 0;
    uintptr_t dyn = 0, pctx = 0;
    if (!seh_read(reinterpret_cast<void*>(ctx + L.ctx_dyn), &dyn, 8) || !dyn ||
        !seh_read(reinterpret_cast<void*>(dyn), &ev.from, 1))
        return;
    if (!seh_read(reinterpret_cast<void*>(ctx + L.ctx_program), &pctx, 8) || !seh_current_entity(reinterpret_cast<void*>(pctx), &ev.entity) || !ev.entity)
        return;
    ev.step = g_step;
    std::lock_guard lock(g_ev_mu);
    if (g_ev.size() >= kMaxEvents) {
        AS.dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    g_ev.push_back(ev);
}

void hk_behavior(safetyhook::Context& c) {
    if (!g_diag.load(std::memory_order_relaxed)) return;
    uintptr_t tpl = 0;
    if (seh_read(reinterpret_cast<void*>(c.rcx + L.ctx_tpl), &tpl, 8) && tpl && is_private_copy(reinterpret_cast<void*>(tpl)))
        AS.copy_reads.fetch_add(1, std::memory_order_relaxed);
}

bool install_mid(safetyhook::MidHook& h, uintptr_t at, safetyhook::MidHookFn f, const char* what) {
    if (!at) return false;
    auto m = safetyhook::MidHook::create(reinterpret_cast<void*>(at), f);
    if (!m) {
        KC_W("mid hook on %s (0x%llx) failed", what, static_cast<unsigned long long>(at));
        return false;
    }
    h = std::move(*m);
    return true;
}

std::string data_file(const char* name) {
    char buf[1024];
    size_t n = 0;
    if (g_api->path(KILN_PATH_DATA, buf, sizeof buf, &n) != KILN_OK) return {};
    return std::string(buf, n) + "\\kiln.creatures\\" + name;
}

bool file_exists(const std::string& p) {
    if (p.empty()) return false;
    FILE* f = std::fopen(p.c_str(), "rb");
    if (!f) return false;
    std::fclose(f);
    return true;
}

void resolve_targets() {
    if (g_targets_resolved) return;
    g_targets_resolved = true;
    TypeInfo t;
    std::string err;
    FieldRef f;
    if (!type_info("keen::ecs::SelectedTargets", &t, &err)) return;
    if (resolve_path(t, "targets[0].targetEntityId", &f, &err)) g_off_target = f.offset;
    if (resolve_path(t, "targets[0].targetRootEntityId", &f, &err)) g_off_target_root = f.offset;
}

KilnEntity read_target(KilnEntity e) {
    resolve_targets();
    uint32_t n = 0;
    auto* c = static_cast<uint8_t*>(component(e, H::Targets, &n));
    if (!c) return 0;
    uint32_t root = 0, ent = 0;
    if (g_off_target_root >= 0 && static_cast<uint32_t>(g_off_target_root) + 4 <= n) seh_read(c + g_off_target_root, &root, 4);
    if (g_off_target >= 0 && static_cast<uint32_t>(g_off_target) + 4 <= n) seh_read(c + g_off_target, &ent, 4);
    return root ? root : ent;
}

}  // namespace

bool lab_ready_switch() {
    if (g_lab_switch < 0) g_lab_switch = file_exists(data_file("force-ready")) ? 1 : 0;
    return g_lab_switch == 1;
}

bool ai_hooks_on() { return g_installed; }

void ai_install() {
    if (!F.ai) return;
    bool ok = install_mid(g_cond_hook, fn.handle_condition, &hk_condition, "HandleCondition") &&
              install_mid(g_set_hook, fn.set_state, &hk_set_state, "SetState");
    if (!ok) {
        g_cond_hook = {};
        g_set_hook = {};
        return;
    }
    g_installed = true;
    if (file_exists(data_file("diag")) && install_mid(g_behavior_hook, fn.behavior_update, &hk_behavior, "BehaviorUpdate")) {
        g_diag = true;
        KC_I("diagnostics on (kiln/data/kiln.creatures/diag): counting behaviour updates that read a private copy");
    }
}

KilnResult ai_set_listener(KilnCreatureListenerFn f, void* user) {
    g_listener = f;
    g_listener_user = user;
    if (!f) g_interest.store(0);
    else g_interest.store(KILN_CREATURE_MASK(KILN_CREATURE_EV_STATE) | KILN_CREATURE_MASK(KILN_CREATURE_EV_TARGET));
    return KILN_OK;
}

void ai_pre() {
    // readiness switch
    if (!g_forced.empty() && F.ready) {
        for (auto it = g_forced.begin(); it != g_forced.end();) {
            uint32_t n = 0;
            auto* a = static_cast<uint8_t*>(component(it->first, H::Actor, &n));
            if (!a) {
                it = g_forced.erase(it);
                continue;
            }
            uint64_t st = 0;
            if (static_cast<uint32_t>(L.actor_state) + 8 <= n && seh_read(a + L.actor_state, &st, 8)) {
                st |= 1ull << L.ready_bit;
                seh_write(a + L.actor_state, &st, 8);
            }
            ++it;
        }
    }
    std::vector<KilnCreatureEvent> evs;
    {
        std::lock_guard lock(g_ev_mu);
        evs.swap(g_ev);
    }
    const uint32_t interest = g_interest.load();
    for (auto& e : evs) {
        Active& a = g_active[e.entity];
        a.state = e.to;
        a.last = g_step;
    }
    // targets: active creatures, every other step
    if ((interest & KILN_CREATURE_MASK(KILN_CREATURE_EV_TARGET)) && (g_step & 1) == 0) {
        for (auto it = g_active.begin(); it != g_active.end();) {
            if (g_step - it->second.last > 3000 || !entity_exists(it->first)) {
                it = g_active.erase(it);
                continue;
            }
            const KilnEntity t = read_target(it->first);
            if (t != it->second.target) {
                KilnCreatureEvent ev{};
                ev.size = sizeof ev;
                ev.kind = KILN_CREATURE_EV_TARGET;
                ev.entity = it->first;
                ev.from = ev.to = it->second.state;
                ev.condition = 255;
                ev.target = t;
                ev.previous = it->second.target;
                ev.step = g_step;
                evs.push_back(ev);
                it->second.target = t;
                it->second.last = g_step;
            }
            ++it;
        }
    } else if (!interest) {
        g_active.clear();
    }
    // events, or every 10 steps an interest poll with no events (the listener tells which kinds it wants next)
    if (!g_listener || (evs.empty() && g_step % 10 != 0)) return;
    uint32_t want = 0;
    unsigned long code = 0;
    if (!seh_listener(g_listener, evs.data(), static_cast<uint32_t>(evs.size()), g_listener_user, &want, &code)) {
        KC_E("the creature event listener faulted (0x%08lx) and was removed", code);
        ai_set_listener(nullptr, nullptr);
        return;
    }
    AS.delivered.fetch_add(evs.size(), std::memory_order_relaxed);
    g_interest.store(want);
}

KilnResult ai_set_ready(KilnOwner owner, KilnEntity e, uint32_t mode) {
    if (!F.ready) return KILN_ERR_UNAVAILABLE;
    if (!lab_ready_switch()) return KILN_ERR_UNAVAILABLE;
    if (mode > KILN_CREATURE_READY_FORCE) return KILN_ERR_INVALID_ARGUMENT;
    if (!component(e, H::Actor)) return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    auto& owners = g_forced[e];
    std::erase(owners, owner);
    if (mode == KILN_CREATURE_READY_FORCE) owners.push_back(owner);
    if (owners.empty()) g_forced.erase(e);
    return KILN_OK;
}

void ai_release_owner(KilnOwner owner) {
    for (auto it = g_forced.begin(); it != g_forced.end();) {
        std::erase(it->second, owner);
        it = it->second.empty() ? g_forced.erase(it) : std::next(it);
    }
}

bool ai_forced(KilnEntity e) { return g_forced.count(e) != 0; }

}  // namespace kcr
