// SPDX-License-Identifier: MIT
// kiln.item_costs' table (kiln_item_costs.h): submit checks the cost's shape, the world and the holder's session, then
// queues it for kiln_items_gear; peek and acknowledge read and let go of receipts; status says what can be submitted.
// The receipt table itself is costs_logic.h's, behind one mutex that is never held while the game or a callback runs.
#include "costs.h"

#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "services/kiln_players.h"

namespace kitems {
void items_log(int level, const char* fmt, ...);
uint64_t current_step();
}  // namespace kitems

namespace kitems::costs {
namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
std::mutex g_mu;  // g_table and g_logged
Table g_table;
std::atomic<bool> g_queued{false};
std::vector<KilnCostResult> g_to_log;  // settled in the program, logged in the next Pre (g_mu)

KilnStr str(const char* s) { return KilnStr{s, std::strlen(s)}; }
uint64_t now_ms() { return GetTickCount64(); }

// The running server world's session number, 0 without one.
uint64_t world_epoch_now() {
    KilnSessionInfo s{};
    s.size = sizeof s;
    if (!g_api || g_api->session(&s) != KILN_OK || !s.worlds[KILN_WORLD_SERVER]) return 0;
    return s.sessions[KILN_WORLD_SERVER];
}

const KilnPlayersApi* players() {
    static const KilnPlayersApi* p = nullptr;
    if (!p && g_api) {
        p = static_cast<const KilnPlayersApi*>(g_api->service(str(KILN_PLAYERS_SERVICE), KILN_PLAYERS_VERSION));
        if (p && p->header.size < sizeof(KilnPlayersApi)) p = nullptr;
    }
    return p;
}

// The holder's session check: a player holder must be in game with this session; any other holder passes 0.
KilnResult check_holder(const KilnSelectedCost& c) {
    const KilnPlayersApi* p = players();
    KilnPlayer pl{};
    pl.size = sizeof pl;
    const bool is_player = p && p->get(c.holder, &pl) == KILN_OK;
    if (!is_player) {
        if (c.character_session == 0) return KILN_OK;
        return p ? KILN_ITEM_COSTS_STALE_SESSION : KILN_ERR_UNAVAILABLE;  // without kiln.players a session cannot be checked
    }
    if (pl.state != KILN_PLAYER_IN_GAME || c.character_session != character_session(pl.handle, pl.joined_step))
        return KILN_ITEM_COSTS_STALE_SESSION;
    return KILN_OK;
}

void update_queued_locked() { g_queued.store(g_table.queued(), std::memory_order_release); }

KilnResult KILN_CALL t_submit(KilnOwner owner, const KilnSelectedCost* cost, uint64_t* ticket) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!cost || !ticket) return KILN_ERR_INVALID_ARGUMENT;
    *ticket = 0;
    KilnSelectedCost c{};
    std::memcpy(&c, cost, cost->size < sizeof c ? cost->size : sizeof c);
    if (const char* bad = check_shape(c)) {
        items_log(KILN_LOG_DEBUG, "item cost refused: %s", bad);
        return KILN_ERR_INVALID_ARGUMENT;
    }
    char why[160] = "";
    if (is_stack(c.kind) ? !gear_costs_stacks(why) : !gear_costs_wear(why)) return KILN_ERR_UNAVAILABLE;
    const uint64_t epoch = world_epoch_now();
    if (!epoch || c.world_epoch != epoch) return KILN_ITEM_COSTS_WRONG_WORLD;
    if (KilnResult rc = check_holder(c); rc != KILN_OK) return rc;
    std::lock_guard lock(g_mu);
    const KilnResult rc = g_table.submit(owner, c, current_step(), ticket);
    update_queued_locked();
    return rc;
}

KilnResult KILN_CALL t_peek(KilnOwner owner, uint64_t ticket, KilnCostResult* out) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!out || out->size < sizeof(KilnCostResult)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    return g_table.peek(owner, ticket, out);
}

KilnResult KILN_CALL t_acknowledge(KilnOwner owner, uint64_t ticket) {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    std::lock_guard lock(g_mu);
    return g_table.acknowledge(owner, ticket);
}

KilnResult KILN_CALL t_status(KilnItemCostsStatus* out) {
    if (!out || out->size < sizeof(KilnItemCostsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->stacks = gear_costs_stacks(out->stacks_reason) ? 1 : 0;
    out->wear = gear_costs_wear(out->wear_reason) ? 1 : 0;
    out->world_epoch = world_epoch_now();
    std::lock_guard lock(g_mu);
    out->held = g_table.held();
    out->committed = g_table.committed();
    out->refused = g_table.refused_count();
    out->uncertain = g_table.uncertain();
    return KILN_OK;
}

const KilnItemCostsApi kTable = {
    {sizeof(KilnItemCostsApi), KILN_ITEM_COSTS_VERSION}, &t_submit, &t_peek, &t_acknowledge, &t_status,
};

const char* state_name(uint32_t s) {
    switch (s) {
        case KILN_COST_COMMITTED: return "committed";
        case KILN_COST_REFUSED: return "refused";
        case KILN_COST_UNCERTAIN: return "UNCERTAIN";
        default: return "queued";
    }
}

}  // namespace

void publish(const KilnApi* api, KilnOwner owner) {
    g_api = api;
    g_owner = owner;
    api->register_service(owner, str(KILN_ITEM_COSTS_SERVICE), &kTable);
}

bool pending() { return g_queued.load(std::memory_order_acquire); }

std::vector<Job> take(size_t max) {
    std::lock_guard lock(g_mu);
    std::vector<Job> jobs = g_table.take(max);
    update_queued_locked();
    return jobs;
}

void settle(const KilnCostResult& result) {
    std::lock_guard lock(g_mu);
    g_table.settle(result, now_ms());
    if (g_to_log.size() < 4096) g_to_log.push_back(result);
}

void pre(uint64_t step) {
    std::vector<KilnCostResult> log;
    {
        char why[160] = "";
        const bool off = gear_switched_off(why);
        std::lock_guard lock(g_mu);
        g_table.end_queued(step, gear_last_update_step(), world_epoch_now(), off, why, now_ms());
        g_table.expire(now_ms());
        update_queued_locked();
        log.swap(g_to_log);
    }
    for (const KilnCostResult& r : log)
        items_log(r.state == KILN_COST_COMMITTED ? KILN_LOG_INFO : KILN_LOG_WARN,
                  "item cost ticket %llu (kind %u, item 0x%08x, slot %llx, entity %u): %s, disposition %u, taken %u, wear %u -> %d "
                  "(durability %d -> %d), refusal %u%s%s",
                  static_cast<unsigned long long>(r.ticket), r.kind, r.item, static_cast<unsigned long long>(r.slot), r.item_entity,
                  state_name(r.state), r.disposition, r.quantity_taken, r.wear_requested, r.wear_applied, r.durability_before,
                  r.durability_after, r.refusal, r.reason[0] ? ": " : "", r.reason);
}

}  // namespace kitems::costs
