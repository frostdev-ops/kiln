// SPDX-License-Identifier: MIT
// kiln.questmark v1 (kiln_questmark.h): a write program for PlayerWaypoint. Queue a position on the tick thread; the
// program kiln_questmark writes it on the player's entity inside the step, after mark_written, so it replicates and the
// game's own readers (player_waypoints_ui on clients) see it like a waypoint the player set.
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <vector>

#include "kiln_api.h"
#include "kiln_questmark.h"
#include "services/kiln_world.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
const KilnWorldApi* W = nullptr;
KilnHandle g_prog = 0;
int32_t s_wp = -1;
uint32_t g_hash = 0;

constexpr uint32_t kSize = 32;       // keen::ecs::PlayerWaypoint (reflection, server 26430253 and client 26430625)
constexpr size_t kOffEnabled = 0;    // keen::bool
constexpr size_t kOffPosition = 8;   // keen::WorldPosition: three int64, 32.32 fixed point metres

struct Pending {
    KilnEntity entity;
    uint8_t enabled;
    int64_t x, y, z;
};

std::mutex g_mu;
std::vector<Pending> g_pending;
std::atomic<uint32_t> g_count{0};
std::atomic<uint64_t> g_requests{0}, g_written{0};

constexpr KilnStr kstr(const char* s) {
    size_t n = 0;
    while (s[n]) ++n;
    return KilnStr{s, n};
}

void log_line(int level, const char* text) { g_api->log(g_owner, level, kstr(text)); }

int64_t fixed(double metres) { return static_cast<int64_t>(std::llround(metres * 4294967296.0)); }

void KILN_CALL prog_each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity entity, void*) {
    if (g_count.load(std::memory_order_acquire) == 0 || s_wp < 0) return;
    Pending p{};
    {
        std::lock_guard lock(g_mu);
        auto it = g_pending.begin();
        for (; it != g_pending.end(); ++it)
            if (it->entity == entity) break;
        if (it == g_pending.end()) return;
        p = *it;
        g_pending.erase(it);
        g_count.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
    }
    auto* data = reinterpret_cast<uint8_t*>(iter[s_wp]);
    if (!data) return;
    W->mark_written(ctx, &iter[s_wp]);
    data[kOffEnabled] = p.enabled;
    if (p.enabled) {
        const int64_t pos[3] = {p.x, p.y, p.z};
        std::memcpy(data + kOffPosition, pos, sizeof pos);
    }
    g_written.fetch_add(1, std::memory_order_relaxed);
}

uint32_t KILN_CALL t_active() {
    if (!W || !g_prog) return 0;
    KilnProgramInfo info{};
    info.size = sizeof info;
    return W->program_info(g_prog, &info) == KILN_OK && info.state == KILN_PROGRAM_ACTIVE ? 1u : 0u;
}

KilnResult KILN_CALL t_set(KilnEntity player, uint8_t enabled, double x, double y, double z) {
    if (!W || !g_prog || !g_api->on_tick_thread()) return KILN_ERR_UNAVAILABLE;
    if (!t_active()) return KILN_ERR_UNAVAILABLE;
    if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z)) return KILN_ERR_INVALID_ARGUMENT;
    uint32_t size = 0;
    if (!W->component(player, g_hash, &size)) return KILN_ERR_NOT_FOUND;
    if (size != kSize) return KILN_ERR_UNAVAILABLE;
    Pending p{player, enabled, fixed(x), fixed(y), fixed(z)};
    std::lock_guard lock(g_mu);
    for (auto& e : g_pending)
        if (e.entity == player) {
            e = p;
            g_requests.fetch_add(1, std::memory_order_relaxed);
            return KILN_OK;
        }
    if (g_pending.size() >= 64) return KILN_ERR_BUFFER_TOO_SMALL;
    g_pending.push_back(p);
    g_count.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
    g_requests.fetch_add(1, std::memory_order_relaxed);
    return KILN_OK;
}

KilnResult KILN_CALL t_counters(uint64_t* requests, uint64_t* written) {
    if (requests) *requests = g_requests.load(std::memory_order_relaxed);
    if (written) *written = g_written.load(std::memory_order_relaxed);
    return KILN_OK;
}

const KilnQuestmarkApi kTable = {{sizeof(KilnQuestmarkApi), KILN_QUESTMARK_VERSION}, &t_set, &t_counters, &t_active};

void KILN_CALL on_ready(void*) {
    W = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    if (!W || W->header.size < sizeof(KilnWorldApi)) {
        W = nullptr;
        log_line(KILN_LOG_WARN, "questmark: kiln.world is unavailable: quest steps set no waypoint");
        return;
    }
    static const KilnStr kWrite[] = {kstr("PlayerWaypoint")};
    g_hash = W->component_hash(kWrite[0]);
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("kiln_questmark");
    d.each = &prog_each;
    d.write = KilnNames{kWrite, 1, 0};
    if (W->register_program(g_owner, &d, &g_prog) != KILN_OK) {
        g_prog = 0;
        log_line(KILN_LOG_WARN, "questmark: kiln.world refused the program kiln_questmark: quest steps set no waypoint");
        return;
    }
    s_wp = W->program_slot(g_prog, KILN_SLOT_WRITE, kWrite[0]);
    char line[128];
    std::snprintf(line, sizeof line, "questmark: program kiln_questmark accepted (PlayerWaypoint slot %d)", s_wp);
    log_line(KILN_LOG_INFO, line);
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = kstr(KILN_QUESTMARK_SERVICE);
    info->version = kstr("1");
    info->on_ready = &on_ready;
    return api->register_service(owner, kstr(KILN_QUESTMARK_SERVICE), &kTable);
}
