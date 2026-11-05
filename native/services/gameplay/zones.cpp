// SPDX-License-Identifier: MIT
// Protection zones: checked synchronously inside the game's building / terraforming / spawning code (worker threads)
// and in Pre for the intercepted events. Zone sets are immutable snapshots published with an atomic pointer; the old
// snapshot is freed in Post of the next step, after WaitWorldJobs, when no worker can still be reading it.
#include <algorithm>

#include "internal.h"
#include "services/kiln_players.h"

namespace kg {
namespace {

struct ZoneSet {
    std::vector<Zone> zones;
    uint32_t deny_union = 0;
};

std::atomic<const ZoneSet*> g_current{nullptr};
std::vector<const ZoneSet*> g_retired;  // tick thread
std::vector<Zone> g_zones;              // master copy, tick thread
uint32_t g_next_id = 1;
const KilnPlayersApi* g_players = nullptr;

struct PlayerBits {
    KilnEntity entity;
    uint64_t steam;
    bool admin;
};
std::vector<PlayerBits> g_last_players;
bool g_dirty = true;

uint64_t mask_for(const Zone& z, const std::vector<PlayerBits>& players) {
    uint64_t m = 0;
    for (auto& p : players) {
        if (p.entity < 1 || p.entity > 64) continue;
        const bool allowed = ((z.flags & KILN_GAMEPLAY_ZONE_ALLOW_ADMINS) && p.admin) ||
                             (p.steam && std::find(z.allowed.begin(), z.allowed.end(), p.steam) != z.allowed.end());
        if (allowed) m |= 1ull << (p.entity - 1);
    }
    return m;
}

void publish() {
    auto* s = new ZoneSet;
    s->zones = g_zones;
    for (auto& z : s->zones) {
        z.allowed_mask = mask_for(z, g_last_players);
        s->deny_union |= z.deny;
    }
    const ZoneSet* old = g_current.exchange(s, std::memory_order_acq_rel);
    if (old) g_retired.push_back(old);
    g_dirty = false;
}

bool contains(const Zone& z, double x, double y, double z_) {
    switch (z.shape) {
    case KILN_GAMEPLAY_ZONE_SPHERE: {
        const double dx = x - z.center.x, dy = y - z.center.y, dz = z_ - z.center.z;
        return dx * dx + dy * dy + dz * dz <= z.radius * z.radius;
    }
    case KILN_GAMEPLAY_ZONE_CYLINDER: {
        const double dx = x - z.center.x, dz = z_ - z.center.z;
        return dx * dx + dz * dz <= z.radius * z.radius && y >= z.min.y && y <= z.max.y;
    }
    case KILN_GAMEPLAY_ZONE_BOX:
        return x >= z.min.x && x <= z.max.x && y >= z.min.y && y <= z.max.y && z_ >= z.min.z && z_ <= z.max.z;
    default:
        return false;
    }
}

KilnResult copy_in(const KilnGameplayZone* in, Zone* z) {
    if (!in || in->size < sizeof(KilnGameplayZone)) return KILN_ERR_INVALID_ARGUMENT;
    if (in->shape > KILN_GAMEPLAY_ZONE_BOX || (in->deny & ~KILN_GAMEPLAY_DENY_ALL) || in->deny == 0)
        return KILN_ERR_INVALID_ARGUMENT;
    if (in->shape != KILN_GAMEPLAY_ZONE_BOX && !(in->radius > 0 && in->radius < 1e6)) return KILN_ERR_INVALID_ARGUMENT;
    if (in->shape != KILN_GAMEPLAY_ZONE_SPHERE && !(in->max.y >= in->min.y)) return KILN_ERR_INVALID_ARGUMENT;
    if (in->shape == KILN_GAMEPLAY_ZONE_BOX && !(in->max.x >= in->min.x && in->max.z >= in->min.z))
        return KILN_ERR_INVALID_ARGUMENT;
    if (in->allowed_count > 4096 || (in->allowed_count && !in->allowed_steam_ids)) return KILN_ERR_INVALID_ARGUMENT;
    z->deny = in->deny;
    z->shape = in->shape;
    z->flags = in->flags;
    z->center = in->center;
    z->radius = in->radius;
    z->min = in->min;
    z->max = in->max;
    z->allowed.assign(in->allowed_steam_ids, in->allowed_steam_ids + in->allowed_count);
    z->name.assign(sv(in->name).substr(0, 128));
    return KILN_OK;
}

}  // namespace

void zones_set_players_service(const void* p) { g_players = static_cast<const KilnPlayersApi*>(p); }

uint32_t zone_check(KilnEntity player, double x, double y, double z, uint32_t action) {
    const ZoneSet* s = g_current.load(std::memory_order_acquire);
    if (!s || !(s->deny_union & action)) return 0;
    const bool is_player = player >= 1 && player <= 1023;
    if (action != KILN_GAMEPLAY_DENY_SPAWN && !is_player) return 0;  // zones restrict players only
    for (auto& zn : s->zones) {
        if (!(zn.deny & action)) continue;
        if (!contains(zn, x, y, z)) continue;
        if (action != KILN_GAMEPLAY_DENY_SPAWN && player <= 64 && (zn.allowed_mask & (1ull << (player - 1)))) continue;
        return zn.id;
    }
    return 0;
}

size_t zone_count() { return g_zones.size(); }

bool zones_any(uint32_t action_mask) {
    const ZoneSet* s = g_current.load(std::memory_order_acquire);
    return s && (s->deny_union & action_mask);
}

void zones_refresh() {
    if (g_zones.empty()) {
        if (g_dirty) publish();
        return;
    }
    std::vector<PlayerBits> now;
    if (g_players) {
        KilnPlayer buf[64];
        uint32_t n = 0;
        const KilnResult rc = g_players->list(0, buf, 64, &n);
        if (rc == KILN_OK || rc == KILN_ERR_BUFFER_TOO_SMALL) {
            n = std::min<uint32_t>(n, 64);
            for (uint32_t i = 0; i < n; ++i)
                now.push_back({buf[i].entity, buf[i].steam_id, buf[i].is_admin != 0});
        }
    }
    bool same = now.size() == g_last_players.size();
    for (size_t i = 0; same && i < now.size(); ++i)
        same = now[i].entity == g_last_players[i].entity && now[i].steam == g_last_players[i].steam &&
               now[i].admin == g_last_players[i].admin;
    if (!same) {
        g_last_players = std::move(now);
        g_dirty = true;
    }
    if (g_dirty) publish();
}

void zones_retire() {
    // Post: WaitWorldJobs returned, so no worker reads a snapshot published before this step's Pre.
    for (auto* s : g_retired) delete s;
    g_retired.clear();
}

KilnResult zone_add(KilnOwner owner, const KilnGameplayZone* in, uint32_t* out) {
    Zone z;
    if (KilnResult rc = copy_in(in, &z); rc != KILN_OK) return rc;
    if (g_zones.size() >= 1024) return KILN_ERR_BUFFER_TOO_SMALL;
    z.id = g_next_id++;
    z.owner = owner;
    g_zones.push_back(std::move(z));
    g_dirty = true;
    publish();
    if (out) *out = g_zones.back().id;
    const Zone& b = g_zones.back();
    KG_I("zone %u '%s' added: deny 0x%x, shape %u, center (%.1f, %.1f, %.1f) r %.1f, %zu allowed player(s)%s", b.id,
         b.name.c_str(), b.deny, b.shape, b.center.x, b.center.y, b.center.z, b.radius, b.allowed.size(),
         (b.flags & KILN_GAMEPLAY_ZONE_ALLOW_ADMINS) ? " + admins" : "");
    return KILN_OK;
}

KilnResult zone_update(uint32_t id, const KilnGameplayZone* in) {
    for (auto& z : g_zones) {
        if (z.id != id) continue;
        Zone n = z;
        if (KilnResult rc = copy_in(in, &n); rc != KILN_OK) return rc;
        z = std::move(n);
        g_dirty = true;
        publish();
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}

KilnResult zone_remove(uint32_t id) {
    auto it = std::find_if(g_zones.begin(), g_zones.end(), [&](const Zone& z) { return z.id == id; });
    if (it == g_zones.end()) return KILN_ERR_NOT_FOUND;
    KG_I("zone %u '%s' removed", it->id, it->name.c_str());
    g_zones.erase(it);
    g_dirty = true;
    publish();
    return KILN_OK;
}

bool zone_denies(KilnEntity player, KilnVec3 p, uint32_t action, uint32_t* out_zone) {
    const uint32_t z = zone_check(player, p.x, p.y, p.z, action);
    if (out_zone) *out_zone = z;
    return z != 0;
}

}  // namespace kg
