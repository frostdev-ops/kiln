// SPDX-License-Identifier: MIT
// Pending ECS events. Programs send events into per-job buffers (`EmitEvent`: record {u32 hash; u16 program; u8 size;
// u8 0; data[align8(size)]}); the next SimStep starts by delivering them to the readers' queues (`DeliverEvents`, called
// from SimStep before any program runs) and resetting the buffers. Kiln's Pre runs before that SimStep, so every
// event sent in the previous step is here, readable and still changeable. See docs/re/gameplay.md.
#include <algorithm>
#include <unordered_map>

#include "internal.h"

namespace kg {
namespace {

inline uint32_t rd32(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
inline void wr32(uint8_t* p, uint32_t v) { std::memcpy(p, &v, 4); }

void fill_base(KilnGameplayEvent* ev, uint32_t kind, uint32_t flags) {
    std::memset(ev, 0, sizeof *ev);
    ev->size = sizeof *ev;
    ev->kind = kind;
    ev->flags = flags;
    ev->step = g_step;
    ev->phase = KILN_GAMEPLAY_PHASE_PRE;
}

// Deny check for a Pre event whose subject is the host's position.
void apply_zone(KilnGameplayEvent* ev, uint32_t action) {
    const uint32_t z = zone_check(ev->actor, ev->position.x, ev->position.y, ev->position.z, action);
    if (z) {
        ev->zone = z;
        ev->flags |= KILN_GAMEPLAY_F_DENIED | KILN_GAMEPLAY_F_CANCELED;
    }
}

// Turns the record into the game's own DeclinedInteraction {guest, host, offerId}: what interaction_attempt sends when it
// refuses, and what ends the guest's interaction sequence (player_control_not_predicted). The queue of
// DeclinedInteraction copies 12 bytes; the record keeps its size byte, so the buffer walk is unchanged.
struct Declined {
    uint32_t guest, host, offer;
};
std::vector<Declined> g_declined;  // Kiln's declines of this Pre (delivery check in Post)

void decline(uint8_t* rec, uint32_t guest, uint32_t host, uint32_t offer) {
    if (g_declined.size() < 64) g_declined.push_back({guest, host, offer});
    uint8_t* d = rec + 8;
    wr32(rec, H.declined);
    wr32(d + 0, guest);
    wr32(d + 4, host);
    wr32(d + 8, offer);
}

struct WalkStats {
    uint32_t records = 0, handled = 0, canceled = 0, bad_size = 0;
};
WalkStats g_walk;
bool g_logged_first = false;
uint64_t g_hist_step = 0;
std::unordered_map<uint32_t, uint32_t> g_hist;  // diagnostics: hash -> count (first minutes)

void on_record(uint8_t* rec, uint32_t hash, uint32_t size) {
    ++g_walk.records;
    uint8_t* d = rec + 8;
    uint16_t pid;
    std::memcpy(&pid, rec + 4, 2);
    const uint32_t inj = pid && pid == program_pid() ? KILN_GAMEPLAY_F_INJECTED : 0u;
    KilnGameplayEvent ev;
    if (hash == H.craft_request) {
        if (size != kCraftSize) { ++g_walk.bad_size; return; }
        if (!has_subscribers(KILN_GAMEPLAY_CRAFT) && !zones_any(KILN_GAMEPLAY_DENY_CRAFT)) return;
        fill_base(&ev, KILN_GAMEPLAY_CRAFT, KILN_GAMEPLAY_F_CANCELABLE | inj);
        ev.actor = rd32(d + 0x8);
        ev.target = rd32(d + 0xC);
        ev.id = rd32(d + 0x10);
        uint16_t amount;
        std::memcpy(&amount, d + 0x14, 2);
        ev.amount = amount;
        if (ev.target == 0) return;  // already neutralized (or never valid)
        entity_position(ev.target, &ev.position);
        apply_zone(&ev, KILN_GAMEPLAY_DENY_CRAFT);
        ++g_walk.handled;
        if (dispatch(&ev)) {
            wr32(d + 0xC, 0);  // station 0: handle_crafting_events finds no station for it and skips it silently
            ++g_walk.canceled;
            KG_D("craft canceled: operator %u station %u recipe %08x x%u%s", ev.actor, ev.target, ev.id, ev.amount,
                 ev.zone ? " (zone)" : "");
        }
    } else if (hash == H.accept_offer) {
        if (size != kAcceptSize) { ++g_walk.bad_size; return; }
        if (!has_subscribers(KILN_GAMEPLAY_INTERACT) && !zones_any(KILN_GAMEPLAY_DENY_INTERACT)) return;
        fill_base(&ev, KILN_GAMEPLAY_INTERACT, KILN_GAMEPLAY_F_CANCELABLE | inj);
        ev.actor = rd32(d + 0x8);
        ev.target = rd32(d + 0xC);
        ev.id = rd32(d + 0x10);
        entity_position(ev.target, &ev.position);
        apply_zone(&ev, KILN_GAMEPLAY_DENY_INTERACT);
        ++g_walk.handled;
        if (dispatch(&ev)) {
            decline(rec, ev.actor, ev.target, ev.id);
            ++g_walk.canceled;
            KG_D("interaction declined: guest %u host %u offer %u%s", ev.actor, ev.target, ev.id, ev.zone ? " (zone)" : "");
        }
    } else if (hash == H.loot) {
        if (size != kLootSize) { ++g_walk.bad_size; return; }
        if (!has_subscribers(KILN_GAMEPLAY_LOOT) && !zones_any(KILN_GAMEPLAY_DENY_LOOT)) return;
        fill_base(&ev, KILN_GAMEPLAY_LOOT, KILN_GAMEPLAY_F_CANCELABLE | inj);
        ev.actor = rd32(d + 0x8);
        ev.target = rd32(d + 0xC);
        ev.id = rd32(d + 0x10);
        ev.op = (d[0x14] ? 1u : 0u) | (d[0x78] ? 2u : 0u);
        entity_position(ev.target, &ev.position);
        apply_zone(&ev, KILN_GAMEPLAY_DENY_LOOT);
        ++g_walk.handled;
        if (dispatch(&ev)) {
            decline(rec, ev.actor, ev.target, ev.id);
            ++g_walk.canceled;
            KG_D("loot declined: guest %u host %u offer %u%s", ev.actor, ev.target, ev.id, ev.zone ? " (zone)" : "");
        }
    } else if (hash == H.pickup) {
        if (size != kPickupSize) { ++g_walk.bad_size; return; }
        if (!has_subscribers(KILN_GAMEPLAY_PICKUP)) return;
        fill_base(&ev, KILN_GAMEPLAY_PICKUP, 0);
        float pos[3];
        std::memcpy(pos, d + 0x8, 12);
        ev.position = KilnVec3{pos[0], pos[1], pos[2]};
        ev.id = rd32(d + 0x14);
        ev.amount = rd32(d + 0x18);
        ev.actor = rd32(d + 0x1C);
        ev.target = rd32(d + 0x20);
        ev.extra = rd32(d + 0x24);
        ev.id2 = d[0x28];
        ev.op = d[0x2C] ? 1u : 0u;
        ++g_walk.handled;
        dispatch(&ev);
    } else if (hash == H.accepted || hash == H.declined) {
        const bool acc = hash == H.accepted;
        if (size != (acc ? kAcceptedSize : kDeclinedSize)) {
            // a record Kiln declined keeps its original size byte; it was reported as INTERACT/LOOT already
            if (!acc && (size == kAcceptSize || size == kLootSize)) return;
            ++g_walk.bad_size;
            return;
        }
        if (!has_subscribers(KILN_GAMEPLAY_INTERACTED)) return;
        fill_base(&ev, KILN_GAMEPLAY_INTERACTED, 0);
        const uint8_t* p = acc ? d + 8 : d;
        ev.actor = rd32(p + 0);
        ev.target = rd32(p + 4);
        ev.id = rd32(p + 8);
        ev.op = acc ? 1u : 0u;
        ++g_walk.handled;
        dispatch(&ev);
    }
}

struct WalkCtx {
    uintptr_t storage;
    bool histogram;
};

void walk(void* p) {
    auto* c = static_cast<WalkCtx*>(p);
    const uintptr_t arr = *reinterpret_cast<const uintptr_t*>(c->storage + L.pending);
    const uint64_t n = *reinterpret_cast<const uint64_t*>(c->storage + L.pending_count);
    if (!arr || n > 65536) return;
    for (uint64_t i = 0; i < n; ++i) {
        const uintptr_t buf = arr + i * static_cast<uintptr_t>(L.buffer_stride);
        auto* data = *reinterpret_cast<uint8_t* const*>(buf);
        const uint64_t cap = *reinterpret_cast<const uint64_t*>(buf + 8);
        const uint32_t used = *reinterpret_cast<const uint32_t*>(buf + L.buffer_used);
        if (!data || used == 0 || used > cap) continue;
        uint32_t off = 0;
        while (off + 8 <= used) {
            uint8_t* rec = data + off;
            const uint32_t hash = rd32(rec);
            const uint32_t size = rec[L.record_size];
            const uint32_t adv = 8 + ((size + 7u) & ~7u);
            if (off + adv > used) break;
            if (c->histogram) ++g_hist[hash];
            on_record(rec, hash, size);
            off += adv;
        }
    }
}

bool g_intercept_broken = false;

// ---- delivery check: the DeclinedInteraction queue after the step that delivered Kiln's declines ----
struct QueueCtx {
    uintptr_t storage;
    uint32_t hash;
    uint32_t count, stride, matched;
    bool found;
};

void read_queue(void* p) {
    auto* c = static_cast<QueueCtx*>(p);
    c->found = false;
    const uintptr_t reg = *reinterpret_cast<const uintptr_t*>(c->storage);
    const uint64_t* bits = *reinterpret_cast<uint64_t* const*>(reg + L.ev_bits);
    const uint64_t cap = *reinterpret_cast<const uint64_t*>(reg + L.ev_cap);
    const uint32_t* keys = *reinterpret_cast<uint32_t* const*>(reg + L.ev_keys);
    const uint16_t* values = *reinterpret_cast<uint16_t* const*>(reg + L.ev_values);
    if (!bits || !keys || !values || !cap || (cap & (cap - 1))) return;
    uint64_t i = c->hash & (cap - 1);
    uint16_t idx = 0xFFFF;
    for (uint64_t n = 0; n < cap && ((bits[i >> 6] >> (i & 63)) & 1); ++n, i = (i + 1) & (cap - 1))
        if (keys[i] == c->hash) {
            idx = values[i];
            break;
        }
    if (idx == 0xFFFF) return;
    const uintptr_t q = *reinterpret_cast<const uintptr_t*>(c->storage + L.queues) + idx * 0x48ull;
    c->count = *reinterpret_cast<const uint32_t*>(q + 0x18);
    c->stride = *reinterpret_cast<const uint32_t*>(q + 0x14);
    const uintptr_t base = *reinterpret_cast<const uintptr_t*>(q + 0x30);
    uint16_t page = *reinterpret_cast<const uint16_t*>(q + 0x40);
    c->found = true;
    c->matched = 0;
    if (!c->stride || c->stride > 0x7F8) return;
    const uint8_t* pg = page ? reinterpret_cast<const uint8_t*>(base + (page - 1) * 0x800ull) : nullptr;
    uint32_t off = 0;
    for (uint32_t k = 0; k < c->count && pg; ++k) {
        if (off + c->stride > 0x7F8) {
            page = *reinterpret_cast<const uint16_t*>(pg + 2);
            pg = page ? reinterpret_cast<const uint8_t*>(base + (page - 1) * 0x800ull) : nullptr;
            off = 0;
            if (!pg) break;
        }
        const uint8_t* e = pg + 8 + off;
        off += c->stride;
        for (auto& d : g_declined)
            if (rd32(e) == d.guest && rd32(e + 4) == d.host && rd32(e + 8) == d.offer) ++c->matched;
    }
}

uint32_t g_queue_checks = 0;

}  // namespace

void declined_check(const char* when) {
    if (g_declined.empty() || !F.queues || g_queue_checks >= 20) return;
    const uintptr_t w = world();
    if (!w) return;
    QueueCtx c{storage_of(w), H.declined};
    unsigned long code = 0;
    if (!seh_call(&read_queue, &c, &code)) {
        KG_W("delivery check (%s): reading the DeclinedInteraction queue faulted (0x%08lx)", when, code);
        g_queue_checks = 20;
        return;
    }
    ++g_queue_checks;
    if (!c.found) KG_W("delivery check (%s): DeclinedInteraction is not in the event map", when);
    else
        KG_I("delivery check (%s): DeclinedInteraction queue holds %u event(s) of %u bytes, %u of them the %zu Kiln "
             "declined", when, c.count, c.stride, c.matched, g_declined.size());
    if (c.matched) g_declined.clear();
}

void declined_reset() { g_declined.clear(); }

bool intercept_wanted() {
    return !g_intercept_broken &&
           (has_subscribers(KILN_GAMEPLAY_CRAFT) || has_subscribers(KILN_GAMEPLAY_INTERACT) ||
            has_subscribers(KILN_GAMEPLAY_LOOT) || has_subscribers(KILN_GAMEPLAY_PICKUP) ||
            has_subscribers(KILN_GAMEPLAY_INTERACTED) ||
            zones_any(KILN_GAMEPLAY_DENY_CRAFT | KILN_GAMEPLAY_DENY_INTERACT | KILN_GAMEPLAY_DENY_LOOT));
}

void intercept_pre() {
    const uintptr_t w = world();
    if (!w) return;
    WalkCtx c{storage_of(w), g_hist_step < 6000};
    g_walk = WalkStats{};
    unsigned long code = 0;
    if (!seh_call(&walk, &c, &code)) {
        g_intercept_broken = true;
        KG_E("pending-event walk faulted (0x%08lx): intercepting is off from now on", code);
        return;
    }
    ++S.steps;
    S.events_seen += g_walk.records;
    S.canceled += g_walk.canceled;
    if (!g_logged_first && g_walk.records) {
        g_logged_first = true;
        KG_I("pending events readable in Pre: %u record(s) in step %llu (%u handled)", g_walk.records,
             static_cast<unsigned long long>(g_step), g_walk.handled);
    }
    if (g_walk.bad_size) KG_W("%u pending record(s) of a known event had an unexpected size (ignored)", g_walk.bad_size);
    if (c.histogram && ++g_hist_step % 1500 == 0) {
        std::vector<std::pair<uint32_t, uint32_t>> v(g_hist.begin(), g_hist.end());
        std::sort(v.begin(), v.end(), [](auto& a, auto& b) { return a.second > b.second; });
        std::string s;
        char tmp[48];
        for (size_t i = 0; i < v.size() && i < 16; ++i) {
            std::snprintf(tmp, sizeof tmp, "%s%08x x%u", i ? ", " : "", v[i].first, v[i].second);
            s += tmp;
        }
        KG_I("pending events over %llu walks: %zu type(s): %s", static_cast<unsigned long long>(g_hist_step), v.size(),
             s.c_str());
    }
}

// ------------------------------------------------------------------------------------------------- teleports ----
namespace {

struct Watched {
    KilnEntity e = 0;
    bool explicit_ = false;  // added with watch()
    bool pending_reported = false;
    bool have_count = false;
    uint8_t count = 0;
    KilnVec3 last{};
    bool have_last = false;
};
std::vector<Watched> g_watch;

Watched* find_watch(KilnEntity e) {
    for (auto& w : g_watch)
        if (w.e == e) return &w;
    return nullptr;
}

void sync_players() {
    // Players 1..16 are watched while they exist; entries added with watch() stay until removed.
    for (KilnEntity e = 1; e <= 16; ++e) {
        uint32_t n = 0;
        const bool exists = component(e, H.teleport_count, &n) != nullptr;
        Watched* w = find_watch(e);
        if (exists && !w) g_watch.push_back(Watched{e});
        if (!exists && w && !w->explicit_)
            g_watch.erase(std::remove_if(g_watch.begin(), g_watch.end(), [&](const Watched& x) { return x.e == e; }),
                          g_watch.end());
    }
}

}  // namespace

KilnResult watch(KilnEntity e, bool on) {
    if (!e) return KILN_ERR_INVALID_ARGUMENT;
    Watched* w = find_watch(e);
    if (on) {
        if (!w) g_watch.push_back(Watched{e, true});
        else w->explicit_ = true;
    } else if (w) {
        g_watch.erase(std::remove_if(g_watch.begin(), g_watch.end(), [&](const Watched& x) { return x.e == e; }),
                      g_watch.end());
    }
    return KILN_OK;
}

void teleport_pre() {
    if (!F.components) return;
    const bool want = has_subscribers(KILN_GAMEPLAY_TELEPORT) || has_subscribers(KILN_GAMEPLAY_TELEPORTED);
    if (!want) return;
    if (g_step % 25 == 1) sync_players();
    for (auto& w : g_watch) {
        uint32_t n = 0;
        auto* tp = static_cast<uint8_t*>(component(w.e, H.teleport, &n));
        if (!tp || n < static_cast<uint32_t>(L.tp_applied + 2)) {
            w.pending_reported = false;
            continue;
        }
        if (tp[L.tp_applied] != 0 || w.pending_reported) continue;
        w.pending_reported = true;
        if (!has_subscribers(KILN_GAMEPLAY_TELEPORT)) continue;
        KilnGameplayEvent ev;
        fill_base(&ev, KILN_GAMEPLAY_TELEPORT, KILN_GAMEPLAY_F_CANCELABLE);
        ev.actor = w.e;
        std::memcpy(&ev.target, tp + L.tp_target_entity, 4);
        int64_t pos[3];
        std::memcpy(pos, tp, sizeof pos);
        ev.target_position = KilnVec3{fixed_to_m(pos[0]), fixed_to_m(pos[1]), fixed_to_m(pos[2])};
        ev.op = tp[L.tp_find_ground] ? 1u : 0u;
        entity_position(w.e, &ev.position);
        const KilnVec3 before = ev.target_position;
        if (dispatch(&ev)) {
            tp[L.tp_applied] = 1;  // the game's teleport program removes an applied Teleport without moving
            ++S.canceled;
            KG_I("teleport of %u to (%.1f, %.1f, %.1f) canceled", w.e, before.x, before.y, before.z);
        } else if (ev.flags & KILN_GAMEPLAY_F_RETARGET) {
            const KilnVec3 t = ev.target_position;
            if (std::isfinite(t.x) && std::isfinite(t.y) && std::isfinite(t.z) && t.x > -1e5 && t.x < 1e5 &&
                t.y > -1e5 && t.y < 1e5 && t.z > -1e5 && t.z < 1e5) {
                const int64_t np[3] = {m_to_fixed(t.x), m_to_fixed(t.y), m_to_fixed(t.z)};
                std::memcpy(tp, np, sizeof np);
                KG_I("teleport of %u redirected to (%.1f, %.1f, %.1f)", w.e, t.x, t.y, t.z);
            }
        }
    }
}

void teleport_post() {
    if (!F.components || !has_subscribers(KILN_GAMEPLAY_TELEPORTED)) return;
    for (auto& w : g_watch) {
        uint32_t n = 0;
        auto* tc = static_cast<const uint8_t*>(component(w.e, H.teleport_count, &n));
        KilnVec3 now{};
        const bool have_pos = entity_position(w.e, &now);
        if (!tc || n == 0) {
            w.have_count = false;
            continue;
        }
        if (w.have_count && tc[0] != w.count) {
            KilnGameplayEvent ev;
            fill_base(&ev, KILN_GAMEPLAY_TELEPORTED, 0);
            ev.phase = KILN_GAMEPLAY_PHASE_POST;
            ev.actor = w.e;
            ev.amount = tc[0];
            ev.position = w.have_last ? w.last : now;
            ev.target_position = now;
            dispatch(&ev);
        }
        w.count = tc[0];
        w.have_count = true;
        if (have_pos) {
            w.last = now;
            w.have_last = true;
        }
    }
}


// ------------------------------------------------------------------------------------------------- discards ----
// Enshrouded has no world drop; a player gets rid of a stack with the inventory's Destroy (InventoryTransferAction,
// InventoryActionType::Destroy = 4) or the item menu's Trash (ItemAction, ItemActionType::Trash = 2). Both arrive as
// versioned client input in PlayerInput; inventory_actions runs an action when its version differs from the one it
// stored in ServerConsumedPlayerInput (docs/re/gameplay.md section 10). In Pre the new input is already there; marking
// it consumed makes the game skip it.
namespace {

uint64_t g_discards = 0, g_discards_canceled = 0;
constexpr uint8_t kTransferDestroy = 4, kItemTrash = 2;

template <typename T>
T rd(const uint8_t* p, int64_t off) {
    T v;
    std::memcpy(&v, p + off, sizeof v);
    return v;
}

void discard_event(KilnEntity player, uint32_t op, const uint8_t* slot, uint8_t* consumed, int64_t consumed_off,
                   uint32_t version) {
    KilnGameplayEvent ev;
    fill_base(&ev, KILN_GAMEPLAY_DISCARD, KILN_GAMEPLAY_F_CANCELABLE);
    ev.actor = player;
    ev.op = op;
    ev.extra = rd<uint32_t>(slot, 0);  // InventorySlotId {u32 inventory entity; u8 index}
    ev.id2 = slot[4];
    entity_position(player, &ev.position);
    uint32_t n = 0;
    if (const auto* inv = static_cast<const uint8_t*>(component(ev.extra, ecs_hash("Inventory"), &n));
        inv && ev.id2 < 8 && n >= (ev.id2 + 1) * 12u) {  // 8 x {u32 item; u32 count; u32 item entity}
        ev.id = rd<uint32_t>(inv, ev.id2 * 12);
        ev.amount = rd<uint32_t>(inv, ev.id2 * 12 + 4);
        ev.target = rd<uint32_t>(inv, ev.id2 * 12 + 8);
    }
    ++g_discards;
    if (dispatch(&ev)) {
        std::memcpy(consumed + consumed_off, &version, 4);  // as if inventory_actions had run it
        ++g_discards_canceled;
        ++S.canceled;
        KG_I("discard by player %u canceled: item %08x x%u in slot %u:%u (%s)", player, ev.id, ev.amount, ev.extra,
             ev.id2, op == KILN_GAMEPLAY_DISCARD_TRASH ? "trash" : "destroy");
    }
}

}  // namespace

void discard_status(uint64_t* seen, uint64_t* canceled) {
    *seen = g_discards;
    *canceled = g_discards_canceled;
}

void discard_pre() {
    if (!F.discard || !has_subscribers(KILN_GAMEPLAY_DISCARD)) return;
    static const uint32_t kPi = ecs_hash("PlayerInput"), kScpi = ecs_hash("ServerConsumedPlayerInput");
    for (KilnEntity e = 1; e <= 16; ++e) {
        uint32_t npi = 0, nsc = 0;
        auto* pi = static_cast<uint8_t*>(component(e, kPi, &npi));
        if (!pi) continue;
        auto* sc = static_cast<uint8_t*>(component(e, kScpi, &nsc));
        if (!sc) continue;
        if (npi >= static_cast<uint32_t>(L.pi_tr_type + 1) && nsc >= static_cast<uint32_t>(L.scpi_tr + 4)) {
            const uint32_t v = rd<uint32_t>(pi, L.pi_tr_version);
            if (v != rd<uint32_t>(sc, L.scpi_tr) && pi[L.pi_tr_type] == kTransferDestroy)
                discard_event(e, KILN_GAMEPLAY_DISCARD_DESTROY, pi + L.pi_tr_slot, sc, L.scpi_tr, v);
        }
        if (npi >= static_cast<uint32_t>(L.pi_item_type + 1) && nsc >= static_cast<uint32_t>(L.scpi_item + 4)) {
            const uint32_t v = rd<uint32_t>(pi, L.pi_item_version);
            if (v != rd<uint32_t>(sc, L.scpi_item) && pi[L.pi_item_type] == kItemTrash)
                discard_event(e, KILN_GAMEPLAY_DISCARD_TRASH, pi + L.pi_item_slot, sc, L.scpi_item, v);
        }
    }
}

}  // namespace kg
