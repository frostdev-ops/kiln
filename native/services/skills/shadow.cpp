// SPDX-License-Identifier: MIT
// kiln.skills shadow table (shadow.h). The game keeps a player's unlocks in UnlockedSkillNodes (256 id slots, saved
// with the character) and derives PlayerSkillsUi (256 mask bits by tree index, 100 level bytes by levelIndex). Nodes
// past those caps never enter either: their levels live here, per player, and these hooks make the game consult them:
//   IsUnlocked(id, masks, tree)        every "is node X unlocked" of the game (natives, requiredSkill, buffs, camera...)
//   NodeSet(id, unlocked, on, tree)    the unlock / unlearn path of player_skilltree and kiln.progression
//   Reachable(id, ui, tree)            the unlock handler's link check (a shadow node's links)
//   PointsSpent(masks, levels, tree)   every spent-points sum (unlock cost check, login budget check, respec, the screen)
//   Reset(iter, paid)                  the respec (and the login reset when the tree costs more than the points)
//   TreeUi(ctx) [client]               the skill-tree screen's per-frame records: shadow nodes' level and unlocked flag
// Which player a call is about comes from the component address it gets (masks / ui / unlocked): every Pre, the
// players' PlayerSkillsUi and UnlockedSkillNodes addresses are mapped to them (server world), and on a client the local
// player's (and the screen's mask copy). Readers are lock-free (immutable snapshots, retired 64 publishes later);
// writers take a mutex. Unknown addresses (scratch copies) see no shadow levels and cannot change them, unless the
// calling thread named their player with shadow_scope (kiln.progression's sums work on scratch copies).
// Inside the game's join check PointsSpent counts native nodes only: a shadow cost must never reset a native tree.
#include "shadow.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <unordered_set>

#include "safetyhook.hpp"
#include "services/kiln_client.h"
#include "services/kiln_world.h"

namespace kiln::skills::shadow {
namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* W = nullptr;
const KilnClientApi* C = nullptr;

void logf(int level, const char* fmt, ...) {
    if (!g_api) return;
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n > static_cast<int>(sizeof buf) - 1) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}
KilnStr kstr(const char* s) { return KilnStr{s, std::strlen(s)}; }

using IsUnlockedFn = bool (*)(uint32_t id, const uint8_t* masks, void* tree);
using NodeSetFn = bool (*)(uint32_t id, uint8_t* unlocked, bool unlock, void* tree);
using ReachableFn = bool (*)(uint32_t id, const uint8_t* ui, void* tree);
using PointsSpentFn = int32_t (*)(const uint8_t* masks, const uint8_t* levels, void* tree);
using ResetFn = void (*)(uint8_t* iter, char paid);
using TreeUiFn = void (*)(void* ctx);
struct Slice {
    const void* ptr;
    uint64_t size;
};
using GetComponentFn = void (*)(Slice* out, uintptr_t world, uint32_t hash, uint32_t entity);

uintptr_t a_is = 0, a_set = 0, a_reach = 0, a_spent = 0, a_reset = 0, a_ui = 0;
GetComponentFn g_get_component = nullptr;
int64_t L_unlocked = 0x80, L_records = 0, L_masks = 0, L_record = 0x44;
SafetyHookInline h_is, h_set, h_reach, h_spent, h_reset, h_ui;
std::atomic<uint32_t> g_level{0};
std::string g_why;

uint32_t fnv(const char* s, uint32_t h = 0x811c9dc5u) {
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x1000193u;
    return h;
}
const uint32_t H_UI = fnv("PlayerSkillsUi", fnv("keen::ecs::")), H_UNLOCKED = fnv("UnlockedSkillNodes", fnv("keen::ecs::"));

// ---- snapshots ----
struct NodeInfo {
    uint16_t cost = 0;
    uint8_t max = 0;
    std::vector<std::pair<uint32_t, bool>> from;
    std::vector<uint32_t> to;
};
struct NodesSnap {
    std::unordered_map<uint32_t, NodeInfo> m;
    std::vector<uint32_t> order;
};
struct OwnersSnap {
    std::unordered_map<uintptr_t, uint64_t> m;
};
using Levels = std::unordered_map<uint32_t, uint8_t>;
struct LevelsSnap {
    std::unordered_map<uint64_t, Levels> m;
};

template <class T>
struct Pub {
    std::atomic<const T*> cur{nullptr};
    std::deque<std::unique_ptr<T>> old;  // the publisher's mutex guards it
    void publish(std::unique_ptr<T> p) {
        cur.store(p.get(), std::memory_order_release);
        old.push_back(std::move(p));
        while (old.size() > 64) old.pop_front();
    }
    const T* get() const { return cur.load(std::memory_order_acquire); }
};

std::mutex g_mu;  // publishers, the address lists, the change ring
Pub<NodesSnap> g_nodes;
Pub<OwnersSnap> g_owners;
Pub<LevelsSnap> g_levels;
std::atomic<bool> g_any{false};  // the tree has shadow nodes
std::vector<std::pair<uintptr_t, uint64_t>> g_server_addrs, g_client_addrs;

std::vector<KilnSkillsShadowChange> g_ring;
uint64_t g_seq = 0;

thread_local uint64_t t_scope_key = 0;    // shadow_scope: this thread's scratch copies are this player's
thread_local bool t_native_only = false;  // inside the game's join check

uint64_t key_of(uint32_t world, KilnEntity e) { return (static_cast<uint64_t>(world) << 32) | e; }

uint64_t owner_of(const void* addr) {
    if (t_scope_key) return t_scope_key;
    const OwnersSnap* o = g_owners.get();
    if (!o) return 0;
    auto it = o->m.find(reinterpret_cast<uintptr_t>(addr));
    return it == o->m.end() ? 0 : it->second;
}

uint8_t level_of(uint64_t key, uint32_t id) {
    if (!key) return 0;
    const LevelsSnap* s = g_levels.get();
    if (!s) return 0;
    auto it = s->m.find(key);
    if (it == s->m.end()) return 0;
    auto j = it->second.find(id);
    return j == it->second.end() ? 0 : j->second;
}

void record_locked(uint64_t key, uint32_t node, uint8_t old_level, uint8_t new_level, uint8_t reason) {
    KilnSkillsShadowChange c{};
    c.seq = ++g_seq;
    c.world = static_cast<uint32_t>(key >> 32);
    c.entity = static_cast<KilnEntity>(key & 0xffffffffu);
    c.node = node;
    c.old_level = old_level;
    c.new_level = new_level;
    c.reason = reason;
    if (g_ring.size() < 256) g_ring.push_back(c);
    else g_ring[c.seq % 256] = c;
}

// Changes one player's levels (caller holds g_mu). f(levels&) returns whether anything changed.
template <class F>
bool edit_locked(uint64_t key, F&& f) {
    const LevelsSnap* cur = g_levels.get();
    auto next = std::make_unique<LevelsSnap>(cur ? *cur : LevelsSnap{});
    Levels& lv = next->m[key];
    if (!f(lv)) return false;
    if (lv.empty()) next->m.erase(key);
    g_levels.publish(std::move(next));
    return true;
}

// Would every taken shadow node stay linked with `without` gone? Links count through other taken shadow nodes, to roots,
// and to native nodes that `native` says are unlocked.
template <class NativeFn>
bool all_linked(const Levels& lv, const NodesSnap& ns, uint32_t without, NativeFn&& native) {
    std::vector<uint32_t> taken, work;
    for (const auto& [id, l] : lv)
        if (l && id != without) taken.push_back(id);
    std::unordered_set<uint32_t> linked;
    auto seed = [&](uint32_t id, const NodeInfo& n) {
        for (const auto& [src, root] : n.from)
            if (root || (!ns.m.count(src) && native(src))) return true;
        for (uint32_t dst : n.to)
            if (!ns.m.count(dst) && native(dst)) return true;
        (void)id;
        return false;
    };
    for (uint32_t id : taken) {
        auto it = ns.m.find(id);
        if (it != ns.m.end() && seed(id, it->second)) {
            linked.insert(id);
            work.push_back(id);
        }
    }
    auto neighbours = [&](const NodeInfo& a, uint32_t b) {
        for (const auto& [src, root] : a.from)
            if (src == b) return true;
        for (uint32_t dst : a.to)
            if (dst == b) return true;
        return false;
    };
    while (!work.empty()) {
        const uint32_t cur = work.back();
        work.pop_back();
        auto ci = ns.m.find(cur);
        for (uint32_t id : taken) {
            if (linked.count(id)) continue;
            auto it = ns.m.find(id);
            if (it == ns.m.end()) continue;
            if (neighbours(it->second, cur) || (ci != ns.m.end() && neighbours(ci->second, id))) {
                linked.insert(id);
                work.push_back(id);
            }
        }
    }
    return linked.size() == taken.size();
}

void publish_owners_locked() {
    auto o = std::make_unique<OwnersSnap>();
    for (auto& [a, k] : g_server_addrs) o->m[a] = k;
    for (auto& [a, k] : g_client_addrs) o->m.emplace(a, k);
    g_owners.publish(std::move(o));
}

// ---- hooks (job threads: no allocation on the vanilla path) ----
bool hk_is(uint32_t id, const uint8_t* masks, void* tree) {
    if (g_any.load(std::memory_order_relaxed)) {
        const NodesSnap* ns = g_nodes.get();
        if (ns && ns->m.count(id)) return level_of(owner_of(masks), id) > 0;
    }
    return h_is.call<bool>(id, masks, tree);
}

bool unlocked_any(uint32_t id, const uint8_t* ui, void* tree, const NodesSnap* ns, uint64_t key) {
    if (ns->m.count(id)) return level_of(key, id) > 0;
    return h_is.call<bool>(id, ui, tree);  // the game's own bit test (a native node: index < 256)
}

bool hk_reach(uint32_t id, const uint8_t* ui, void* tree) {
    if (g_any.load(std::memory_order_relaxed)) {
        const NodesSnap* ns = g_nodes.get();
        auto it = ns ? ns->m.find(id) : decltype(ns->m.end()){};
        if (ns && it != ns->m.end()) {
            const uint64_t key = owner_of(ui);
            const uint8_t lv = level_of(key, id);
            if (lv > 0) return it->second.max > 0 && lv < it->second.max;  // the game: more levels only
            for (auto& [src, root] : it->second.from)
                if (root || unlocked_any(src, ui, tree, ns, key)) return true;
            for (uint32_t dst : it->second.to)
                if (unlocked_any(dst, ui, tree, ns, key)) return true;
            return false;
        }
    }
    return h_reach.call<bool>(id, ui, tree);
}

bool hk_set(uint32_t id, uint8_t* unlocked, bool unlock, void* tree) {
    if (g_any.load(std::memory_order_relaxed)) {
        const NodesSnap* ns = g_nodes.get();
        auto it = ns ? ns->m.find(id) : decltype(ns->m.end()){};
        if (ns && it != ns->m.end()) {
            const uint64_t key = owner_of(unlocked);
            if (!key) return false;  // a scratch list: a shadow node cannot go into UnlockedSkillNodes
            const uint8_t max = it->second.max;
            bool ok = false;
            std::lock_guard lock(g_mu);
            edit_locked(key, [&](Levels& lv) {
                const uint8_t cur = lv.count(id) ? lv[id] : 0;
                uint8_t nl = cur;
                if (unlock) {
                    if (max == 0 ? cur >= 1 : cur >= max) return false;
                    nl = static_cast<uint8_t>(cur + 1);
                } else {
                    if (cur == 0) return false;
                    nl = static_cast<uint8_t>(cur - 1);
                    // the game's rule for its own nodes: an unlearn may not cut others off. A native neighbour's state
                    // isn't known here (no masks), so it counts as a link: this only refuses shadow-on-shadow chains.
                    if (nl == 0 && !all_linked(lv, *ns, id, [](uint32_t) { return true; })) return false;
                }
                if (nl) lv[id] = nl;
                else lv.erase(id);
                record_locked(key, id, cur, nl, KILN_SKILLS_SHADOW_GAME);
                ok = true;
                return true;
            });
            return ok;
        }
    }
    return h_set.call<bool>(id, unlocked, unlock, tree);
}

int32_t hk_spent(const uint8_t* masks, const uint8_t* levels, void* tree) {
    int32_t r = h_spent.call<int32_t>(masks, levels, tree);
    if (!g_any.load(std::memory_order_relaxed)) return r;
    if (t_native_only) return r;
    const uint64_t key = owner_of(masks);
    if (!key) return r;
    const NodesSnap* ns = g_nodes.get();
    const LevelsSnap* ls = g_levels.get();
    if (!ns || !ls) return r;
    auto it = ls->m.find(key);
    if (it == ls->m.end()) return r;
    for (auto& [id, lv] : it->second) {
        auto n = ns->m.find(id);
        if (n == ns->m.end()) continue;
        r += n->second.max ? static_cast<int32_t>(lv) * n->second.cost : n->second.cost;
    }
    return r;
}

// SEH part of the reset hook: the unlocked list before / after.
bool copy_list(uint8_t* iter, uint8_t* out, uint8_t** unlocked) {
    __try {
        *unlocked = *reinterpret_cast<uint8_t**>(iter + L_unlocked);
        if (!*unlocked) return false;
        std::memcpy(out, *unlocked + 4, 0x400);
        std::memcpy(out + 0x400, *unlocked + 0x804, 0x100);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void hk_reset(uint8_t* iter, char paid) {
    if (!g_any.load(std::memory_order_relaxed)) return h_reset.call<void>(iter, paid);
    alignas(8) uint8_t before[0x500], after[0x500];
    uint8_t* u = nullptr;
    const bool have = copy_list(iter, before, &u);
    h_reset.call<void>(iter, paid);
    uint8_t* u2 = nullptr;
    if (!have || !copy_list(iter, after, &u2) || u2 != u) return;
    // done: the list changed, or there was no game node unlocked (nothing to pay for, nothing to change)
    bool any = false;
    for (int i = 0; i < 256 && !any; ++i) {
        uint32_t id;
        std::memcpy(&id, before + i * 4, 4);
        any = id != 0 && (before[0x400 + i] >> 5) != 0;
    }
    const bool changed = std::memcmp(before, after, sizeof before) != 0;
    if (!changed && any) return;  // a paid respec the game refused
    const uint64_t key = owner_of(u);
    if (!key) return;
    std::lock_guard lock(g_mu);
    edit_locked(key, [&](Levels& lv) {
        if (lv.empty()) return false;
        const uint8_t reason = paid ? KILN_SKILLS_SHADOW_RESET : KILN_SKILLS_SHADOW_LOGIN;
        for (auto& [id, l] : lv) record_locked(key, id, l, 0, reason);
        lv.clear();
        return true;
    });
}

// ---- client: the screen's records ----
bool write_records(uintptr_t play, const uint32_t* order, size_t count, uint64_t key) {
    __try {
        const uintptr_t map = play + static_cast<uintptr_t>(L_records);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(map + 0x10);
        const uint64_t* bits = *reinterpret_cast<const uint64_t* const*>(map + 0x08);
        const uint32_t* keys = *reinterpret_cast<const uint32_t* const*>(map + 0x28);
        uint8_t* vals = *reinterpret_cast<uint8_t* const*>(map + 0x40);
        if (!cap || (cap & (cap - 1)) || !bits || !keys || !vals) return false;
        for (size_t i = 0; i < count; ++i) {
            const uint32_t id = order[i];
            uint64_t slot = id & (cap - 1);
            for (uint64_t n = 0; n < cap && ((bits[slot >> 6] >> (slot & 63)) & 1); ++n) {
                if (keys[slot] == id) {
                    uint8_t* rec = vals + slot * static_cast<uint64_t>(L_record);
                    const uint8_t lv = level_of(key, id);
                    rec[0x41] = lv;
                    rec[0x42] = 0;
                    rec[0x43] = lv > 0 ? 1 : 0;
                    break;
                }
                slot = (slot + 1) & (cap - 1);
            }
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::atomic<uint64_t> g_client_key{0};
std::atomic<bool> g_ui_fault{false};

void hk_ui(void* ctx) {
    h_ui.call<void>(ctx);
    if (!g_any.load(std::memory_order_relaxed) || g_ui_fault.load(std::memory_order_relaxed)) return;
    const uintptr_t w = g_api->world(KILN_WORLD_CLIENT);
    if (!w) return;
    const uintptr_t play = g_api->singleton(w, kstr("G38UiPlayData"));
    const NodesSnap* ns = g_nodes.get();
    if (!play || !ns) return;
    if (!write_records(play, ns->order.data(), ns->order.size(), g_client_key.load())) {
        g_ui_fault = true;
        logf(KILN_LOG_WARN, "skills: the skill-tree screen's records did not write: shadow nodes show as locked there");
    }
}

template <class F>
bool hook(SafetyHookInline& h, uintptr_t at, F* fn, const char* what) {
    h = safetyhook::create_inline(reinterpret_cast<void*>(at), reinterpret_cast<void*>(fn));
    if (!h) logf(KILN_LOG_WARN, "skills: the %s hook did not install", what);
    return static_cast<bool>(h);
}

bool sym(const char* n, uintptr_t* out) { return g_api->symbol(kstr(n), out) == KILN_OK && *out; }

}  // namespace

bool init(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    int64_t v = 0;
    if (api->constant(kstr("skills/login.unlocked"), &v) == KILN_OK) L_unlocked = v;
    const bool syms = api->feature(kstr("skills.shadow")) && sym("skills/IsUnlocked", &a_is) && sym("skills/NodeSet", &a_set) &&
                      sym("skills/Reachable", &a_reach) && sym("skills/PointsSpent", &a_spent) && sym("skills/Reset", &a_reset);
    if (!syms) {
        g_why = "feature skills.shadow is off on this build: nodes past the game's caps are refused";
        return false;
    }
    const bool ok = hook(h_is, a_is, &hk_is, "IsUnlocked") && hook(h_set, a_set, &hk_set, "NodeSet") &&
                    hook(h_reach, a_reach, &hk_reach, "Reachable") && hook(h_spent, a_spent, &hk_spent, "PointsSpent") &&
                    hook(h_reset, a_reset, &hk_reset, "Reset");
    if (!ok) {
        // all or nothing: a partial set would answer some queries and not others
        h_is = {}, h_set = {}, h_reach = {}, h_spent = {}, h_reset = {};
        g_why = "a shadow hook did not install: nodes past the game's caps are refused";
        return false;
    }
    g_level = 1;
    uintptr_t gc = 0;
    if (sym("GetComponent", &gc)) g_get_component = reinterpret_cast<GetComponentFn>(gc);
    if (api->side() == KILN_SIDE_CLIENT) {
        int64_t rec = 0, masks = 0, rs = 0;
        if (api->feature(kstr("skills.shadow_ui")) && sym("skills/TreeUi", &a_ui) &&
            api->constant(kstr("skills/ui.records"), &rec) == KILN_OK && api->constant(kstr("skills/ui.masks"), &masks) == KILN_OK &&
            g_get_component) {
            L_records = rec;
            L_masks = masks;
            if (api->constant(kstr("skills/ui.record_size"), &rs) == KILN_OK && rs > 0x43) L_record = rs;
            if (hook(h_ui, a_ui, &hk_ui, "skill-tree screen")) g_level = 2;
        }
        if (g_level < 2) {
            g_why = "the skill-tree screen hook (feature skills.shadow_ui) is off: nodes past the game's caps are hidden on this client";
            // a client without the screen part shows nothing past the caps (the first 256 indices stay the server's)
            h_is = {}, h_set = {}, h_reach = {}, h_spent = {}, h_reset = {};
            g_level = 0;
            return false;
        }
    }
    return true;
}

void ready() {
    W = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    C = static_cast<const KilnClientApi*>(g_api->service(kstr("kiln.client"), 1));
}

uint32_t level() { return g_level.load(); }
std::string why_off() { return g_why; }
void native_costs_only(bool on) { t_native_only = on; }

void set_tree(const std::vector<ShadowNode>& nodes) {
    auto ns = std::make_unique<NodesSnap>();
    for (const ShadowNode& s : nodes) {
        NodeInfo& n = ns->m[s.id];
        n.cost = s.cost;
        n.max = s.max_level;
        n.from = s.from;
        n.to = s.to;
        ns->order.push_back(s.id);
    }
    std::lock_guard lock(g_mu);
    g_any.store(!ns->m.empty());
    g_nodes.publish(std::move(ns));
}

bool is_shadow(uint32_t node) {
    if (!g_any.load(std::memory_order_relaxed)) return false;
    const NodesSnap* ns = g_nodes.get();
    return ns && ns->m.count(node);
}

void pre_server() {
    if (!g_level || !W) return;
    KilnEntity players[64];
    uint32_t n = 0;
    if (W->find(fnv("PlayerState", fnv("keen::ecs::")), players, 64, &n) != KILN_OK) n = std::min<uint32_t>(n, 64);
    std::vector<std::pair<uintptr_t, uint64_t>> addrs;
    for (uint32_t i = 0; i < n && i < 64; ++i) {
        uint32_t sz = 0;
        const uint64_t k = key_of(KILN_WORLD_SERVER, players[i]);
        if (const void* p = W->component(players[i], H_UI, &sz)) addrs.emplace_back(reinterpret_cast<uintptr_t>(p), k);
        if (const void* p = W->component(players[i], H_UNLOCKED, &sz)) addrs.emplace_back(reinterpret_cast<uintptr_t>(p), k);
    }
    std::sort(addrs.begin(), addrs.end());
    std::lock_guard lock(g_mu);
    if (addrs == g_server_addrs) return;
    g_server_addrs = std::move(addrs);
    publish_owners_locked();
}

namespace {
bool get_component(uintptr_t world, uint32_t hash, uint32_t e, const void** out) {
    __try {
        Slice s{};
        g_get_component(&s, world, hash, e);
        *out = s.ptr;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
}  // namespace

void pre_client() {
    if (!g_level || !C || !g_get_component) return;
    KilnClientState st{sizeof st};
    if (C->state(&st) != KILN_OK || !st.local_player) return;
    const uintptr_t w = g_api->world(KILN_WORLD_CLIENT);
    if (!w) return;
    // on a host the ids are shared with the server world: the same table entry
    const uint32_t kind = st.session == KILN_SESSION_HOST ? KILN_WORLD_SERVER : KILN_WORLD_CLIENT;
    const uint64_t k = key_of(kind, st.local_player);
    g_client_key = k;
    std::vector<std::pair<uintptr_t, uint64_t>> addrs;
    const void* p = nullptr;
    if (get_component(w, H_UI, st.local_player, &p) && p) addrs.emplace_back(reinterpret_cast<uintptr_t>(p), k);
    if (get_component(w, H_UNLOCKED, st.local_player, &p) && p) addrs.emplace_back(reinterpret_cast<uintptr_t>(p), k);
    if (const uintptr_t play = g_api->singleton(w, kstr("G38UiPlayData")); play && L_masks)
        addrs.emplace_back(play + static_cast<uintptr_t>(L_masks), k);
    std::sort(addrs.begin(), addrs.end());
    std::lock_guard lock(g_mu);
    if (addrs == g_client_addrs) return;
    g_client_addrs = std::move(addrs);
    publish_owners_locked();
}

bool migrate(const void* unlocked, uint32_t node, uint8_t level_byte) {
    const uint64_t key = owner_of(unlocked);
    if (!key) return false;
    const NodesSnap* ns = g_nodes.get();
    auto it = ns ? ns->m.find(node) : decltype(ns->m.end()){};
    if (!ns || it == ns->m.end()) return false;
    uint8_t lv = static_cast<uint8_t>(std::max(1, level_byte >> 5));
    if (it->second.max && lv > it->second.max) lv = it->second.max;
    if (!it->second.max) lv = 1;
    std::lock_guard lock(g_mu);
    return edit_locked(key, [&](Levels& m) {
        const uint8_t cur = m.count(node) ? m[node] : 0;
        if (cur >= lv) return false;
        m[node] = lv;
        record_locked(key, node, cur, lv, KILN_SKILLS_SHADOW_MIGRATE);
        return true;
    });
}

// ---- table ----
KilnResult t_nodes(uint32_t* out, uint32_t cap, uint32_t* n) {
    const NodesSnap* ns = g_nodes.get();
    const uint32_t c = ns ? static_cast<uint32_t>(ns->order.size()) : 0;
    if (n) *n = c;
    for (uint32_t i = 0; i < c && i < cap && out; ++i) out[i] = ns->order[i];
    return c > cap && out ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult t_get(uint32_t world, KilnEntity e, KilnSkillsShadowEntry* out, uint32_t cap, uint32_t* n) {
    const LevelsSnap* s = g_levels.get();
    uint32_t c = 0;
    if (s)
        if (auto it = s->m.find(key_of(world, e)); it != s->m.end()) {
            for (auto& [id, lv] : it->second) {
                if (out && c < cap) out[c] = KilnSkillsShadowEntry{id, lv, {}};
                ++c;
            }
        }
    if (n) *n = c;
    return out && c > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult t_set(uint32_t world, KilnEntity e, const KilnSkillsShadowEntry* in, uint32_t n) {
    if (!e || (n && !in)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_level) return KILN_ERR_UNAVAILABLE;
    const NodesSnap* ns = g_nodes.get();
    const uint64_t key = key_of(world, e);
    std::lock_guard lock(g_mu);
    edit_locked(key, [&](Levels& lv) {
        Levels next;
        for (uint32_t i = 0; i < n; ++i) {
            auto it = ns ? ns->m.find(in[i].node) : decltype(ns->m.end()){};
            if (!ns || it == ns->m.end() || !in[i].level) continue;
            next[in[i].node] = static_cast<uint8_t>(it->second.max ? std::min<uint8_t>(in[i].level, it->second.max) : 1);
        }
        if (next == lv) return false;
        for (auto& [id, l] : lv)
            if (!next.count(id)) record_locked(key, id, l, 0, KILN_SKILLS_SHADOW_SET);
        for (auto& [id, l] : next)
            if (!lv.count(id) || lv[id] != l) record_locked(key, id, lv.count(id) ? lv[id] : 0, l, KILN_SKILLS_SHADOW_SET);
        lv = std::move(next);
        return true;
    });
    return KILN_OK;
}

KilnResult t_scope(uint32_t world, KilnEntity e) {
    t_scope_key = e ? key_of(world, e) : 0;
    return KILN_OK;
}

KilnResult t_level(uint32_t world, KilnEntity e, uint32_t node, uint32_t* level, uint32_t* max_level) {
    const NodesSnap* ns = g_nodes.get();
    if (!ns || !g_any.load(std::memory_order_relaxed)) return KILN_ERR_NOT_FOUND;
    auto it = ns->m.find(node);
    if (it == ns->m.end()) return KILN_ERR_NOT_FOUND;
    if (level) *level = level_of(key_of(world, e), node);
    if (max_level) *max_level = it->second.max;
    return KILN_OK;
}

KilnResult t_reset(uint32_t world, KilnEntity e, uint32_t* levels) {
    if (!e) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_level) return KILN_ERR_UNAVAILABLE;
    const uint64_t key = key_of(world, e);
    uint32_t n = 0;
    std::lock_guard lock(g_mu);
    edit_locked(key, [&](Levels& lv) {
        if (lv.empty()) return false;
        for (auto& [id, l] : lv) {
            n += l;
            record_locked(key, id, l, 0, KILN_SKILLS_SHADOW_RESET);
        }
        lv.clear();
        return true;
    });
    if (levels) *levels = n;
    return KILN_OK;
}

KilnResult t_linked(uint32_t world, KilnEntity e, const void* ui, void* tree, uint32_t without, int32_t* ok) {
    if (!ok || !ui || !tree) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_level) return KILN_ERR_UNAVAILABLE;
    const NodesSnap* ns = g_nodes.get();
    const LevelsSnap* ls = g_levels.get();
    *ok = 1;
    if (!ns || !ls) return KILN_OK;
    auto it = ls->m.find(key_of(world, e));
    if (it == ls->m.end()) return KILN_OK;
    const auto* masks = static_cast<const uint8_t*>(ui);
    *ok = all_linked(it->second, *ns, without, [&](uint32_t id) { return h_is.call<bool>(id, masks, tree); }) ? 1 : 0;
    return KILN_OK;
}

KilnResult t_changes(uint64_t after, KilnSkillsShadowChange* out, uint32_t cap, uint32_t* n) {
    std::lock_guard lock(g_mu);
    std::vector<KilnSkillsShadowChange> v;
    for (auto& c : g_ring)
        if (c.seq > after) v.push_back(c);
    std::sort(v.begin(), v.end(), [](auto& a, auto& b) { return a.seq < b.seq; });
    if (n) *n = static_cast<uint32_t>(v.size());
    for (uint32_t i = 0; i < v.size() && i < cap && out; ++i) out[i] = v[i];
    return out && v.size() > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

namespace {
void* server_tree() {
    const uintptr_t w = g_api->server_world();
    return w ? reinterpret_cast<void*>(g_api->singleton(w, kstr("g38_skilltree::SkillTree"))) : nullptr;
}
bool call_spent(const uint8_t* ui, void* tree, int32_t* out) {
    __try {
        *out = reinterpret_cast<PointsSpentFn>(a_spent)(ui, ui + 0x20, tree);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool call_bool(uintptr_t fn, uint32_t id, const uint8_t* p, void* tree, int32_t* out) {
    __try {
        *out = reinterpret_cast<IsUnlockedFn>(fn)(id, p, tree) ? 1 : 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
}  // namespace

KilnResult t_spent(KilnEntity e, int32_t* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_level || !W) return KILN_ERR_UNAVAILABLE;
    void* tree = server_tree();
    uint32_t sz = 0;
    const auto* ui = static_cast<const uint8_t*>(W->component(e, H_UI, &sz));
    if (!tree || !ui || sz != 136) return KILN_ERR_NOT_FOUND;
    pre_server();  // the address map of this very step
    return call_spent(ui, tree, out) ? KILN_OK : KILN_ERR_INTERNAL;
}

KilnResult t_is_unlocked(KilnEntity e, uint32_t node, int32_t* unlocked, int32_t* reachable) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!g_level || !W) return KILN_ERR_UNAVAILABLE;
    void* tree = server_tree();
    uint32_t sz = 0;
    const auto* ui = static_cast<const uint8_t*>(W->component(e, H_UI, &sz));
    if (!tree || !ui || sz != 136) return KILN_ERR_NOT_FOUND;
    pre_server();
    int32_t u = 0, r = 0;
    if (!call_bool(a_is, node, ui, tree, &u) || !call_bool(a_reach, node, ui, tree, &r)) return KILN_ERR_INTERNAL;
    if (unlocked) *unlocked = u;
    if (reachable) *reachable = r;
    return KILN_OK;
}

}  // namespace kiln::skills::shadow
