// SPDX-License-Identifier: MIT
// kiln.toolwear's table (kiln_toolwear.h): the owners' rules, the change records and the status. The hooks
// (toolwear.cpp) read one number per item, lock-free; everything here is above that, on the tick thread:
//   - a rule is a layer of one owner for one item. The latest defined live layer is in effect; removing it (or its
//     owner going) reveals the one before, or the game's own debit;
//   - the effective cost is written to the hooks' table whenever it changes, and the generation grows by one;
//   - status() answers from a copy under a mutex, for any thread.
#include "toolwear.h"

#include <windows.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "engine.h"
#include "kiln_api.h"
#include "services/kiln_hooks.h"
#include "services/kiln_items.h"
#include "services/kiln_toolwear.h"
#include "xfer.h"

namespace kitems {
KilnResult items_item_info(uint32_t id, KilnItemInfo* out);
}

namespace kitems::toolwear {
namespace {

struct Layer {
    KilnHandle handle;
    KilnOwner owner;
    std::string mod;
    uint32_t item;
    int32_t cost;
    uint64_t order;  // the latest defined wins
};

const KilnApi* g_api = nullptr;
std::mutex g_mu;  // the layers and the generation: tick thread writes, status() reads
std::vector<Layer> g_layers;
uint64_t g_generation = 0, g_next_order = 1;
KilnHandle g_next_handle = 1;

constexpr uint8_t kCategoryTools = 3, kCategoryBuildTools = 6;  // the game files its pickaxes under build tools (with an item entity)
constexpr uint32_t kFlagHasPide = 0x2;

const Layer* winner_locked(uint32_t item) {
    const Layer* best = nullptr;
    for (const Layer& l : g_layers)
        if (l.item == item && (!best || l.order > best->order)) best = &l;
    return best;
}

// After a change of `item`'s layers: the hooks' number follows the winner. False: the hooks' table is full.
bool publish_locked(uint32_t item, int32_t before) {
    const Layer* w = winner_locked(item);
    const int32_t now = w ? w->cost : -1;
    if (!set_rule(item, now)) return false;
    if (now != before) ++g_generation;
    return true;
}

bool ascii_id(KilnStr s) {
    if (!s.ptr || !s.len || s.len > 127) return false;
    for (size_t i = 0; i < s.len; ++i)
        if (static_cast<unsigned char>(s.ptr[i]) < 0x20 || static_cast<unsigned char>(s.ptr[i]) > 0x7e) return false;
    return true;
}

// 0: a rule can be set for the item; else the result code, with the reason in `why`.
KilnResult supported(uint32_t item, char* why, size_t cap) {
    KilnItemInfo info{};
    info.size = sizeof info;
    const KilnResult rc = items_item_info(item, &info);
    if (rc == KILN_ERR_UNAVAILABLE) {
        std::snprintf(why, cap, "PendingContent");
        return KILN_TOOLWEAR_PENDING;
    }
    if (rc != KILN_OK) {
        std::snprintf(why, cap, "UnknownItem: the game has no item 0x%08x", item);
        return KILN_ERR_NOT_FOUND;
    }
    // a tool of either category that has an item entity of its own (HasPIDE) and is gear (durability): axes, pickaxes. A
    // build tool without an item entity (a rake) and a torch (no item entity; it burns down by another path) are refused.
    if ((info.category != kCategoryTools && info.category != kCategoryBuildTools) || !(info.item_flags & kFlagHasPide) || !info.gear) {
        std::snprintf(why, cap, "UnsupportedTool: %.*s is not a tool with an item entity and durability of its own (a torch burns down by another "
                                "path; build tools without one and other categories are not debited per use)", static_cast<int>(info.name.len), info.name.ptr);
        return KILN_TOOLWEAR_UNSUPPORTED_TOOL;
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_define(KilnOwner owner, KilnStr mod, uint32_t item, uint32_t per_debit, KilnHandle* out) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!out || !item || per_debit > static_cast<uint32_t>(kMaxCost) || !ascii_id(mod)) return KILN_ERR_INVALID_ARGUMENT;
    *out = 0;
    if (!status().on) return KILN_ERR_UNAVAILABLE;
    if (!g_api->server_world()) return KILN_TOOLWEAR_NO_AUTHORITATIVE_WORLD;
    char why[192];
    if (const KilnResult rc = supported(item, why, sizeof why); rc != KILN_OK) return rc;
    std::lock_guard lock(g_mu);
    const Layer* was = winner_locked(item);
    const int32_t before = was ? was->cost : -1;
    for (Layer& l : g_layers)
        if (l.owner == owner && l.item == item) {
            // the same owner again: its layer, updated in place and moved to the top (its handle stays)
            const Layer old = l;
            l.cost = static_cast<int32_t>(per_debit);
            l.mod.assign(mod.ptr, mod.len);
            l.order = g_next_order++;
            if (!publish_locked(item, before)) {
                l = old;
                return KILN_TOOLWEAR_TOO_MANY_RULES;
            }
            if (before == l.cost && was && was->handle != l.handle) ++g_generation;  // the same cost, another mod's now
            *out = l.handle;
            return KILN_OK;
        }
    g_layers.push_back(Layer{g_next_handle, owner, std::string(mod.ptr, mod.len), item, static_cast<int32_t>(per_debit), g_next_order++});
    if (!publish_locked(item, before)) {
        g_layers.pop_back();
        return KILN_TOOLWEAR_TOO_MANY_RULES;
    }
    if (before == static_cast<int32_t>(per_debit)) ++g_generation;  // an equal cost, but another owner's rule is in effect now
    *out = g_next_handle++;
    return KILN_OK;
}

// Takes layers out by a predicate and publishes each item they touched.
template <typename Pred>
size_t remove_locked(Pred&& gone) {
    std::vector<std::pair<uint32_t, std::pair<int32_t, KilnHandle>>> touched;  // item -> (effective cost, winner) before
    for (const Layer& l : g_layers)
        if (gone(l) && std::none_of(touched.begin(), touched.end(), [&](const auto& t) { return t.first == l.item; })) {
            const Layer* w = winner_locked(l.item);
            touched.push_back({l.item, {w ? w->cost : -1, w ? w->handle : 0}});
        }
    const size_t before = g_layers.size();
    g_layers.erase(std::remove_if(g_layers.begin(), g_layers.end(), gone), g_layers.end());
    for (const auto& [item, was] : touched) {
        publish_locked(item, was.first);  // taking a rule away always fits the hooks' table
        const Layer* w = winner_locked(item);
        if (w && w->cost == was.first && w->handle != was.second) ++g_generation;  // the same cost, another mod's now
    }
    return before - g_layers.size();
}

KilnResult KILN_CALL t_remove(KilnOwner caller, KilnHandle rule) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!rule) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    for (const Layer& l : g_layers)
        if (l.handle == rule) {
            if (l.owner != caller) return KILN_TOOLWEAR_NOT_OWNER;
            remove_locked([rule](const Layer& x) { return x.handle == rule; });
            return KILN_OK;
        }
    return KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_changes(KilnToolWearChange* out, uint32_t capacity, uint32_t stride, uint32_t* count) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!count || (capacity && (!out || stride < offsetof(KilnToolWearChange, sequence_id)))) return KILN_ERR_INVALID_ARGUMENT;
    *count = 0;
    KilnSessionInfo session{};
    session.size = sizeof session;
    const bool have = g_api->session(&session) == KILN_OK;
    Change batch[64];
    const size_t record = std::min<size_t>(stride, sizeof(KilnToolWearChange));
    while (*count < capacity) {
        const size_t n = drain(batch, std::min<size_t>(64, capacity - *count));
        if (!n) break;
        for (size_t i = 0; i < n; ++i) {
            const Change& c = batch[i];
            KilnToolWearChange r{};
            r.size = static_cast<uint32_t>(record);
            r.source = c.source;
            r.world_session = have ? session.sessions[KILN_WORLD_SERVER] : 0;
            r.step = c.step;
            r.actor = c.actor;
            r.item_pide = c.item_entity;
            r.item_id = c.item_id;
            r.instance_stamp = c.stamp;
            r.before = c.before;
            r.after = c.after;
            r.maximum = c.maximum;
            r.original_delta = c.original_delta;
            r.requested_delta = c.requested_delta;
            r.sequence_id = c.sequence_id;
            r.call_index = c.call_index;
            r.trigger_time = c.trigger_time;
            r.trigger_type = c.trigger_type;
            std::memcpy(reinterpret_cast<uint8_t*>(out) + static_cast<size_t>(*count) * stride, &r, record);
            ++*count;
        }
    }
    return KILN_OK;
}

KilnResult KILN_CALL t_status(uint32_t item, KilnToolWearState* out) {
    if (!out || out->size < offsetof(KilnToolWearState, owner_mod_id)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = std::min<uint32_t>(out->size, sizeof(KilnToolWearState));
    KilnToolWearState s{};
    s.size = size;
    const Status st = status();
    s.dropped_events = st.dropped;
    s.hook_threads = st.threads;  // the tail: copied below only as far as the caller's size reaches
    s.hit_seen = st.hit_seen;
    s.hit_applied = st.hit_applied;
    s.spawn_seen = st.spawn_seen;
    s.spawn_applied = st.spawn_applied;
    if (!st.on) {
        std::snprintf(s.reason, sizeof s.reason, "%s", st.why[0] ? st.why : "items.tool_wear_debit is off");
    } else if (item) {
        const KilnResult rc = supported(item, s.reason, sizeof s.reason);
        s.pending = rc == KILN_TOOLWEAR_PENDING;
        s.ok = rc == KILN_OK;
    } else {
        s.ok = 1;
    }
    {
        std::lock_guard lock(g_mu);
        s.generation = g_generation;
        if (const Layer* w = item ? winner_locked(item) : nullptr) {
            s.has_rule = 1;
            s.per_debit = static_cast<uint32_t>(w->cost);
            std::snprintf(s.owner_mod_id, sizeof s.owner_mod_id, "%s", w->mod.c_str());
        }
    }
    std::memcpy(out, &s, size);
    return KILN_OK;
}

// An owner went (a mod unloaded): its rules go with it, each item falling back to the rule before or the game's own.
void KILN_CALL on_owner_retiring(KilnOwner retiring, void*) {
    std::lock_guard lock(g_mu);
    remove_locked([retiring](const Layer& l) { return l.owner == retiring; });
}

const KilnToolWearApi kTable = {{sizeof(KilnToolWearApi), KILN_TOOLWEAR_VERSION}, &t_define, &t_remove, &t_changes, &t_status};

}  // namespace

bool publish(const KilnApi* api, uint64_t owner) {
    g_api = api;
    // rules of an owner that is revoked go by themselves where kiln.hooks can say so (0.2.0-dev.28); the managed side
    // removes a retiring mod's rules itself as well
    const auto* hooks = static_cast<const KilnHooksApi*>(api->service(KilnStr{KILN_HOOKS_SERVICE, std::strlen(KILN_HOOKS_SERVICE)}, 2));
    if (hooks && hooks->header.size >= offsetof(KilnHooksApi, subscribe_owner_retiring) + sizeof(void*) && hooks->subscribe_owner_retiring) {
        KilnHandle h = 0;
        hooks->subscribe_owner_retiring(owner, &on_owner_retiring, nullptr, &h);
    }
    return api->register_service(owner, KilnStr{KILN_TOOLWEAR_SERVICE, std::strlen(KILN_TOOLWEAR_SERVICE)}, &kTable) == KILN_OK;
}

}  // namespace kitems::toolwear
