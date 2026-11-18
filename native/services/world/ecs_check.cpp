// SPDX-License-Identifier: MIT
// Opt-in in-game check of the ECS reads from a task thread (for the owner's checks and labs; off by default): enabled
// when <kiln>/data/kiln.world/ecs-read-check exists at boot. While a server world runs (a dedicated server, or a
// client that hosts), every 20 s, 6 rounds at most:
//   1. on the tick thread (server Pre) it takes the truth through the game's own GetComponent (kiln.world's
//      tick-thread path): the players (ids 1..1023 with a CurrentTransform), their positions and Inventory sizes, and
//      the nearest entity with an Inventory within 10 m of the first player that is not a player (a container);
//   2. one kiln.tasks task then reads the same entities through kiln.world's task-thread members (ecs_context_snapshot,
//      entity_open, entity_validate, ecs_snapshot, ecs_snapshot_many, ecs_template_uuid, ecs_query with bounds, a
//      frame-quiet read of the player's CurrentTransform, and on a client the client world's query, whose frame-quiet
//      form must refuse unsupported) and compares them with the truth.
// Results: "world ecs-read check: ..." lines in kiln.log with the values read, and one PASS or FAIL line per round.
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "services/kiln_tasks.h"
#include "transform_layout.h"
#include "world_internal.h"

namespace kw {
namespace {

constexpr int kRounds = 6;
constexpr auto kEvery = std::chrono::seconds(20);
constexpr double kNear = 10.0, kSamePlace = 0.5;  // metres: container search radius, tick vs task position tolerance

struct Seen {
    KilnEntity id = 0;
    KilnVec3 pos{};
    uint32_t inventory = 0;  // Inventory size through GetComponent (0: none)
};
struct Round {
    int number = 0;
    std::vector<Seen> players;
    Seen container;
};

bool g_enabled = false;
const KilnTasksApi* g_tasks = nullptr;
uint32_t g_inventory = 0, g_health = 0;
std::chrono::steady_clock::time_point g_next{};
int g_round = 0;
std::atomic<bool> g_busy{false};

double distance(const KilnVec3& a, const KilnVec3& b) {
    const double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}
const char* result_name(KilnResult rc) {
    switch (rc) {
        case KILN_OK: return "ok";
        case KILN_ERR_NOT_FOUND: return "absent";
        case KILN_ECS_ERR_STALE: return "stale";
        case KILN_ECS_ERR_UNREADABLE: return "unreadable";
        case KILN_WORLD_ERR_CHANGED: return "changed";
        case KILN_ERR_UNAVAILABLE: return "unavailable";
        case KILN_ERR_BUFFER_TOO_SMALL: return "buffer too small";
        default: return "error";
    }
}
std::string hex(const uint8_t* p, size_t n) {
    std::string s;
    char b[3];
    for (size_t i = 0; i < n; ++i) { std::snprintf(b, sizeof b, "%02x", p[i]); s += b; }
    return s;
}

// A read the world may be writing at that moment: unreadable or changed is retried a few times.
template <class F> KilnResult settle(F&& read) {
    KilnResult rc = KILN_ERR_UNAVAILABLE;
    for (int i = 0; i < 20; ++i) {
        rc = read();
        if (rc != KILN_ECS_ERR_UNREADABLE && rc != KILN_WORLD_ERR_CHANGED) break;
    }
    return rc;
}

// One entity read from the task and compared with the tick's truth. Returns false on a mismatch or a refusal.
bool read_entity(const char* what, const Seen& truth, std::string& why) {
    KilnObjectRef ref{};
    KilnResult rc = settle([&] { return t_entity_open(g_owner, 1, KILN_WORLD_SERVER, 0, truth.id, &ref); });
    if (rc != KILN_OK) {
        why += std::string(what) + " open " + result_name(rc) + "; ";
        return false;
    }
    KilnObjectInfo info{};
    info.size = sizeof info;
    rc = t_entity_validate(g_owner, &ref, &info);
    uint8_t t[56] = {};
    uint32_t req = 0;
    const KilnResult trc = settle([&] { return t_ecs_snapshot(g_owner, &ref, kCurrentTransformHash, t, sizeof t, &req); });
    KilnTransform tr{};
    const bool have_pos = trc == KILN_OK && transform_decode(t, sizeof t, &tr);
    uint32_t inv_size = 0;
    const KilnResult irc = t_ecs_snapshot(g_owner, &ref, g_inventory, nullptr, 0, &inv_size);
    uint8_t guid[16] = {};
    const KilnResult grc = settle([&] { return t_ecs_template_uuid(g_owner, &ref, guid); });
    const double off = have_pos ? distance(tr.position, truth.pos) : -1;
    KW_I("world ecs-read check: %s %u: generation %u, layout 0x%llx, validate %s (alive %d, template %d); position (%.2f, "
         "%.2f, %.2f) %s, the tick saw (%.2f, %.2f, %.2f), %.3f m apart; Inventory %u bytes (%s), the tick saw %u; "
         "template %s %s",
         what, truth.id, ref.generation, static_cast<unsigned long long>(ref.layout_stamp), result_name(rc),
         (info.flags & KILN_OBJECT_ALIVE) != 0, (info.flags & KILN_OBJECT_HAS_TEMPLATE) != 0, tr.position.x,
         tr.position.y, tr.position.z, result_name(trc), truth.pos.x, truth.pos.y, truth.pos.z, off, inv_size,
         result_name(irc), truth.inventory, grc == KILN_OK ? hex(guid, 16).c_str() : "-", result_name(grc));
    bool ok = rc == KILN_OK && have_pos && off <= kSamePlace;
    if (truth.inventory) ok = ok && irc == KILN_ERR_BUFFER_TOO_SMALL && inv_size == truth.inventory;
    if (!ok) why += std::string(what) + " differs from the tick; ";
    return ok;
}

void KILN_CALL check_body(const KilnTaskRun*, void* user) {
    auto* round = static_cast<Round*>(user);
    std::string why;
    bool pass = true;
    KilnEcsContextInfo ci{};
    ci.size = sizeof ci;
    const KilnResult crc = settle([&] { return t_ecs_context_snapshot(&ci); });
    KW_I("world ecs-read check: round %d of %d: context %s, worlds 0x%x, server session %llu, client session %llu, exact "
         "profile %u, reads %s%s%s",
         round->number, kRounds, result_name(crc), ci.present_mask, static_cast<unsigned long long>(ci.sessions[0]),
         static_cast<unsigned long long>(ci.sessions[KILN_WORLD_CLIENT]), ci.exact_profile,
         ecs_prod_available() ? "on" : "off", *ecs_prod_last_refusal() ? "; last refusal: " : "", ecs_prod_last_refusal());
    if (crc != KILN_OK || !ecs_prod_available()) { pass = false; why += "reads are off on this build; "; }
    for (size_t i = 0; pass && i < round->players.size() && i < 4; ++i) pass = read_entity("player", round->players[i], why) && pass;
    if (pass && round->container.id) pass = read_entity("container", round->container, why) && pass;
    if (pass && !round->players.empty()) {
        const Seen& p = round->players.front();
        // The containers near the player, through a verified query with bounds.
        const uint32_t comps[2] = {kCurrentTransformHash, g_inventory};
        KilnEcsQueryRequest q{};
        q.size = sizeof q;
        q.world_kind = KILN_WORLD_SERVER;
        q.flags = KILN_ECS_QUERY_BOUNDS;
        q.components = comps;
        q.component_count = 2;
        q.center = p.pos;
        q.radius = kNear;
        std::vector<KilnObjectRef> refs(256);
        uint32_t req = 0, state = 0;
        const KilnResult qrc = settle([&] {
            return t_ecs_query(g_owner, &q, refs.data(), static_cast<uint32_t>(refs.size()), &req, &state);
        });
        bool player_in = false, container_in = !round->container.id;
        for (uint32_t i = 0; qrc == KILN_OK && i < req && i < refs.size(); ++i) {
            player_in = player_in || refs[i].entity == p.id;
            container_in = container_in || refs[i].entity == round->container.id;
        }
        KW_I("world ecs-read check: query (CurrentTransform + Inventory within %.0f m of player %u): %s, %u entities, "
             "player %s, container %s",
             kNear, p.id, result_name(qrc), req, player_in ? "in" : "missing", container_in ? "in" : "missing");
        if (qrc != KILN_OK || !player_in || !container_in) { pass = false; why += "the bounded query missed an entity; "; }
        // Frame-quiet: the player's CurrentTransform copied only while no server frame runs (busy is retried; the
        // task starts right after a step, between frames).
        {
            KilnObjectRef ref{};
            const KilnEcsSnapshotSpec quiet[1] = {{kCurrentTransformHash, KILN_ECS_READ_FRAME_QUIET}};
            uint8_t t[56] = {};
            uint32_t presence[1] = {9}, busy = 0;
            KilnResult frc = t_entity_open(g_owner, 1, KILN_WORLD_SERVER, 0, p.id, &ref);
            for (int i = 0; frc == KILN_OK && i < 200; ++i) {
                const KilnResult r = t_ecs_snapshot_many(g_owner, &ref, quiet, 1, t, sizeof t, &req, presence);
                if (r != KILN_ECS_ERR_BUSY && r != KILN_ECS_ERR_UNREADABLE) { frc = r; break; }
                frc = r;
                ++busy;
                std::this_thread::sleep_for(std::chrono::milliseconds(2));
            }
            KilnTransform tr{};
            const bool got = frc == KILN_OK && transform_decode(t, sizeof t, &tr);
            const double off = got ? distance(tr.position, p.pos) : -1;
            KW_I("world ecs-read check: frame-quiet CurrentTransform of player %u: %s after %u busy look(s), position "
                 "(%.2f, %.2f, %.2f), %.3f m from the tick's",
                 p.id, result_name(frc), busy, tr.position.x, tr.position.y, tr.position.z, off);
            if (!got || off > kSamePlace) { pass = false; why += "the frame-quiet read failed; "; }
        }
        // Several components at once.
        KilnObjectRef ref{};
        if (t_entity_open(g_owner, 1, KILN_WORLD_SERVER, 0, p.id, &ref) == KILN_OK) {
            const KilnEcsSnapshotSpec specs[3] = {{kCurrentTransformHash, 0}, {g_inventory, 0}, {g_health, 0}};
            uint32_t presence[3] = {9, 9, 9};
            std::vector<uint8_t> bytes(65536);
            const KilnResult mrc = settle([&] {
                return t_ecs_snapshot_many(g_owner, &ref, specs, 3, bytes.data(), static_cast<uint32_t>(bytes.size()), &req, presence);
            });
            KW_I("world ecs-read check: snapshot_many of player %u (CurrentTransform, Inventory, Health): %s, %u bytes, "
                 "presence %u %u %u",
                 p.id, result_name(mrc), req, presence[0], presence[1], presence[2]);
            if (mrc != KILN_OK || presence[0] != KILN_ECS_OK) { pass = false; why += "snapshot_many failed; "; }
        }
        // A client also reads its own world: the entities with a CurrentTransform near the player's position.
        if (ci.present_mask & (1u << KILN_WORLD_CLIENT)) {
            q.world_kind = KILN_WORLD_CLIENT;
            q.components = comps;
            q.component_count = 1;
            q.radius = 5;
            const KilnResult cq = settle([&] {
                return t_ecs_query(g_owner, &q, refs.data(), static_cast<uint32_t>(refs.size()), &req, &state);
            });
            double x = 0, y = 0, z = 0;
            if (cq == KILN_OK && req) {
                uint8_t t[56] = {};
                uint32_t size = 0;
                KilnTransform tr{};
                if (t_ecs_snapshot(g_owner, &refs[0], kCurrentTransformHash, t, sizeof t, &size) == KILN_OK &&
                    transform_decode(t, sizeof t, &tr)) {
                    x = tr.position.x; y = tr.position.y; z = tr.position.z;
                }
            }
            KW_I("world ecs-read check: client world: %u entities with a CurrentTransform within 5 m (%s); the first, %u, "
                 "at (%.2f, %.2f, %.2f)",
                 req, result_name(cq), cq == KILN_OK && req ? refs[0].entity : 0u, x, y, z);
            if (cq != KILN_OK || !req) { pass = false; why += "the client world query found nothing near the player; "; }
            // Frame-quiet is the server world's only: on the client world it must refuse unsupported, never fall back.
            q.flags = KILN_ECS_QUERY_BOUNDS | KILN_ECS_QUERY_FRAME_QUIET;
            const KilnResult uq = t_ecs_query(g_owner, &q, refs.data(), static_cast<uint32_t>(refs.size()), &req, &state);
            KW_I("world ecs-read check: frame-quiet query of the client world: %s (state %u; unsupported expected)",
                 uq == KILN_ECS_ERR_UNSUPPORTED ? "unsupported" : result_name(uq), state);
            if (uq != KILN_ECS_ERR_UNSUPPORTED) { pass = false; why += "a frame-quiet client-world read was not refused unsupported; "; }
        }
    }
    if (round->players.empty()) { pass = false; why += "no player in the server world yet; "; }
    KW_I("world ecs-read check: %s round %d%s%s", pass ? "PASS" : "FAIL", round->number, why.empty() ? "" : ": ",
         why.c_str());
}
void KILN_CALL check_done(KilnHandle task, uint32_t, void* user) {
    delete static_cast<Round*>(user);
    if (g_tasks) g_tasks->release(g_owner, task);
    g_busy.store(false);
}

}  // namespace

void ecs_check_init() {
    char buf[1024];
    size_t len = 0;
    if (!g_api || g_api->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK) return;
    std::error_code ec;
    const std::u8string dir(reinterpret_cast<const char8_t*>(buf), len);
    if (!std::filesystem::exists(std::filesystem::path(dir) / "kiln.world" / "ecs-read-check", ec)) return;
    g_tasks = static_cast<const KilnTasksApi*>(
        g_api->service(KilnStr{KILN_TASKS_SERVICE, sizeof(KILN_TASKS_SERVICE) - 1}, KILN_TASKS_VERSION));
    if (!g_tasks || g_tasks->header.size < sizeof(KilnTasksApi)) {
        KW_W("world ecs-read check: enabled, but kiln.tasks is not here; the check cannot run");
        return;
    }
    g_enabled = true;
    g_inventory = component_hash("Inventory");
    g_health = component_hash("Health");
    g_next = std::chrono::steady_clock::now() + kEvery;
    KW_I("world ecs-read check enabled: %d rounds, one every %lld s while a server world runs", kRounds,
         static_cast<long long>(kEvery.count()));
}

void ecs_check_pre(const KilnTickInfo*) {
    if (!g_enabled || g_round >= kRounds || g_busy.load() || !g_world_live.load()) return;
    const auto now = std::chrono::steady_clock::now();
    if (now < g_next) return;
    g_next = now + kEvery;
    auto round = new Round;
    round->number = ++g_round;
    // The truth, on the tick thread, through the game's own GetComponent.
    static std::vector<KilnEntity> ids(1 << 18);
    const auto listed = [](uint32_t hash, uint32_t& n) {
        const KilnResult rc = find(hash, ids.data(), static_cast<uint32_t>(ids.size()), &n);
        if (n > ids.size()) n = static_cast<uint32_t>(ids.size());  // more than fit: the first ones are enough here
        return rc == KILN_OK || rc == KILN_ERR_BUFFER_TOO_SMALL;
    };
    uint32_t n = 0;
    if (listed(kCurrentTransformHash, n)) {
        for (uint32_t i = 0; i < n; ++i) {
            if (ids[i] == 0 || ids[i] > 1023) continue;
            Seen s;
            s.id = ids[i];
            if (position(s.id, &s.pos) != KILN_OK) continue;
            component(s.id, g_inventory, &s.inventory);
            round->players.push_back(s);
        }
    }
    if (!round->players.empty() && listed(g_inventory, n)) {
        double best = kNear;
        for (uint32_t i = 0; i < n; ++i) {
            if (ids[i] <= 1023) continue;
            Seen s;
            s.id = ids[i];
            if (position(s.id, &s.pos) != KILN_OK) continue;
            const double d = distance(s.pos, round->players.front().pos);
            if (d > best) continue;
            best = d;
            component(s.id, g_inventory, &s.inventory);
            round->container = s;
        }
    }
    KW_I("world ecs-read check: round %d: the tick sees %zu player(s)%s%u", round->number, round->players.size(),
         round->container.id ? ", the nearest container is " : ", no container within 10 m",
         round->container.id);
    KilnTaskInfo info{};
    info.size = sizeof info;
    info.name = KilnStr{"world ecs-read check", sizeof "world ecs-read check" - 1};
    info.flags = KILN_TASK_AFTER_STEP;  // starts right after a server step: between frames
    KilnHandle task = 0;
    uint32_t refusal = 0;
    g_busy.store(true);
    if (g_tasks->submit(g_owner, &info, &check_body, &check_done, round, &task, &refusal) != KILN_OK) {
        KW_W("world ecs-read check: kiln.tasks refused the task (refusal %u)", refusal);
        delete round;
        g_busy.store(false);
    }
}

}  // namespace kw
