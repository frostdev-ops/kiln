// SPDX-License-Identifier: MIT
// Opt-in in-game check of the compare-exchange writes from a task thread (for the owner's checks on a lab or test world;
// off by default). It runs only when <kiln>/data/kiln.world/ecs-write-check exists at boot AND names the running world:
//   world=<the world's GUID, 32 hex digits>   required; any other world: the check stays off. Without it (an empty
//                                             file) the check only logs the running world's GUID and does nothing else
//   component=<a component name, e.g. keen::ecs::Foo or Foo>             optional; the one component the check changes
//   offset=<byte>                                                         optional (default 0): the byte whose lowest
//                                                                         bit the check flips, then flips back
// Once the server world runs and a player is in it:
//   1. on the tick thread (server Pre) a compare-exchange from the tick must answer busy (in frame);
//   2. one kiln.tasks task lists the components the policy lets a task write (kiln.types' registry, ecs_write_policy)
//      and which of them the first player has; with a named component it does a no-op exchange, flips the bit,
//      flips it back and reads the bytes again, which must equal the original; without one it does the no-op only;
//   3. a few steps later the committed-write events are counted: two for the change and its restore.
// Results: "world ecs-write check: ..." lines in kiln.log and one PASS or FAIL line. The change lasts at most a few
// frames; pick a component whose value does not matter on the test world (the list in kiln.log helps).
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

#include "services/kiln_saves.h"
#include "services/kiln_tasks.h"
#include "services/kiln_types.h"
#include "transform_layout.h"
#include "world_internal.h"

namespace kw {
namespace {

enum class Phase { Off, Waiting, Running, Counting, Done };
std::atomic<Phase> g_phase{Phase::Off};
const KilnTasksApi* g_tasks = nullptr;
const KilnSavesApi* g_saves = nullptr;
const KilnTypesApi* g_types = nullptr;
std::string g_check_world;      // the GUID the check file names (lower-case hex)
std::string g_component;  // the component it may change ("" none)
uint32_t g_offset = 0;
std::atomic<int> g_events{0}, g_expected_events{0};
std::atomic<uint64_t> g_done_step{0};
std::atomic<bool> g_passed{true};
std::string g_why;  // the task's findings (written by the task before it sets Counting; read after)
KilnHandle g_sub = 0;
std::chrono::steady_clock::time_point g_next{};

const char* name_of(KilnResult rc) {
    switch (rc) {
        case KILN_OK: return "ok";
        case KILN_ERR_NOT_FOUND: return "absent";
        case KILN_ECS_ERR_STALE: return "stale";
        case KILN_ECS_ERR_UNREADABLE: return "unreadable";
        case KILN_ECS_ERR_BUSY: return "busy";
        case KILN_ECS_ERR_CONFLICT: return "conflict";
        case KILN_ECS_ERR_NOT_WRITABLE: return "not writable";
        case KILN_ECS_ERR_UNCERTAIN: return "uncertain";
        case KILN_ERR_UNAVAILABLE: return "unavailable";
        default: return "error";
    }
}
std::string hex(const uint8_t* p, size_t n) {
    std::string s;
    char b[3];
    for (size_t i = 0; i < n; ++i) { std::snprintf(b, sizeof b, "%02x", p[i]); s += b; }
    return s;
}
std::string type_name(uint32_t hash) {
    if (!g_types) return "?";
    const KilnType* t = g_types->type_by_hash(hash);
    KilnTypeInfo info{};
    info.size = sizeof info;
    if (!t || g_types->type_info(t, &info) != KILN_OK) return "?";
    return std::string(info.qualified_name.ptr, info.qualified_name.len);
}
void KILN_CALL on_event(const KilnEcsWriteEvent* e, void*) {
    if (e && e->owner == g_owner) g_events.fetch_add(1);  // this check's own writes only
}

// A compare-exchange retried while it is busy or unreadable (a frame, another writer), up to about a second.
KilnResult exchange(const KilnObjectRef& ref, uint32_t hash, const std::vector<uint8_t>& expected,
                    const std::vector<uint8_t>& desired, KilnMutationReply& reply, std::vector<uint8_t>& confirmed) {
    KilnResult rc = KILN_ERR_UNAVAILABLE;
    for (int i = 0; i < 400; ++i) {
        reply = KilnMutationReply{};
        reply.size = sizeof reply;
        confirmed.assign(expected.size(), 0);
        rc = t_ecs_compare_exchange(g_owner, &ref, hash, expected.data(), nullptr, desired.data(),
                                    static_cast<uint32_t>(expected.size()), confirmed.data(), &reply);
        if (rc != KILN_ECS_ERR_BUSY && rc != KILN_ECS_ERR_UNREADABLE) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return rc;
}
KilnResult snapshot(const KilnObjectRef& ref, uint32_t hash, std::vector<uint8_t>& out) {
    uint32_t size = 0;
    KilnResult rc = t_ecs_snapshot(g_owner, &ref, hash, nullptr, 0, &size);
    if (rc != KILN_ERR_BUFFER_TOO_SMALL || !size) return rc == KILN_OK ? KILN_ERR_NOT_FOUND : rc;
    out.assign(size, 0);
    for (int i = 0; i < 20; ++i) {
        rc = t_ecs_snapshot(g_owner, &ref, hash, out.data(), size, &size);
        if (rc != KILN_ECS_ERR_UNREADABLE && rc != KILN_WORLD_ERR_CHANGED) break;
    }
    return rc;
}

struct Job {
    KilnEntity player = 0;
};

void KILN_CALL check_body(const KilnTaskRun*, void* user) {
    const KilnEntity player = static_cast<Job*>(user)->player;
    std::string why;
    bool pass = true;
    // 1. What the policy lets a task write, from kiln.types' registry.
    std::vector<uint32_t> writable;
    if (g_types && g_types->header.size >= offsetof(KilnTypesApi, ecs_registry) + sizeof(void*) && g_types->ecs_registry) {
        KilnEcsRegistryInfo info{};
        info.size = sizeof info;
        uint32_t required = 0;
        g_types->ecs_registry(KILN_WORLD_SERVER, &info, nullptr, 0, &required);
        std::vector<KilnEcsRegistryRow> rows(required);
        if (required && g_types->ecs_registry(KILN_WORLD_SERVER, &info, rows.data(), required, &required) == KILN_OK) {
            for (const auto& row : rows) {
                KilnEcsWritePolicy p{};
                p.size = sizeof p;
                if (t_ecs_write_policy(KILN_WORLD_SERVER, row.component, &p) == KILN_OK && p.status == KILN_ECS_OK)
                    writable.push_back(row.component);
            }
            KW_I("world ecs-write check: %zu of %u registered components are writable from a task", writable.size(), required);
        }
    }
    if (writable.empty()) { pass = false; why += "no component is writable here; "; }
    // 2. Which of them the player has.
    KilnObjectRef ref{};
    KilnResult rc = t_entity_open(g_owner, g_owner, KILN_WORLD_SERVER, 0, player, &ref);
    if (rc != KILN_OK) { pass = false; why += std::string("opening player ") + std::to_string(player) + ": " + name_of(rc) + "; "; }
    std::string list;
    uint32_t first = 0;
    for (uint32_t hash : writable) {
        std::vector<uint8_t> b;
        if (rc != KILN_OK || snapshot(ref, hash, b) != KILN_OK) continue;
        if (!first) first = hash;
        list += type_name(hash) + " (" + std::to_string(b.size()) + "), ";
        if (list.size() > 900) {
            KW_I("world ecs-write check: writable on player %u: %s", player, list.c_str());
            list.clear();
        }
    }
    if (!list.empty()) KW_I("world ecs-write check: writable on player %u: %s", player, list.c_str());
    const uint32_t target = g_component.empty() ? first : component_hash(g_component);
    std::vector<uint8_t> original, confirmed;
    KilnMutationReply reply{};
    if (pass && target && snapshot(ref, target, original) == KILN_OK) {
        // The no-op: the same bytes in and out.
        rc = exchange(ref, target, original, original, reply, confirmed);
        KW_I("world ecs-write check: no-op on %s of player %u: %s (flags 0x%x)", type_name(target).c_str(), player,
             name_of(rc), reply.flags);
        if (rc != KILN_OK || !(reply.flags & KILN_MUTATION_NO_OP)) { pass = false; why += "the no-op did not answer ok, no-op; "; }
        if (!g_component.empty()) {
            if (g_offset >= original.size()) {
                pass = false;
                why += "the offset is past the component; ";
            } else {
                // Change one bit, then put it back, then read the bytes again.
                std::vector<uint8_t> changed = original;
                changed[g_offset] ^= 1;
                std::vector<uint8_t> current = original;
                for (int attempt = 0; attempt < 3; ++attempt) {
                    rc = exchange(ref, target, current, changed, reply, confirmed);
                    if (rc != KILN_ECS_ERR_CONFLICT) break;
                    current = confirmed;  // the game changed it first: change from its value
                    original = confirmed;
                    changed = confirmed;
                    changed[g_offset] ^= 1;
                }
                const uint64_t hold1 = reply.hold_ns;
                KW_I("world ecs-write check: changed byte %u of %s: %s (flags 0x%x, held %.1f us) %s -> %s", g_offset,
                     g_component.c_str(), name_of(rc), reply.flags, hold1 / 1000.0, hex(original.data(), std::min<size_t>(original.size(), 32)).c_str(),
                     hex(changed.data(), std::min<size_t>(changed.size(), 32)).c_str());
                if (rc != KILN_OK || !(reply.flags & KILN_MUTATION_WRITTEN)) {
                    pass = false;
                    why += "the change was not written; ";
                } else {
                    g_expected_events.fetch_add(1);
                    rc = exchange(ref, target, changed, original, reply, confirmed);
                    KW_I("world ecs-write check: restored %s: %s (flags 0x%x, held %.1f us)", g_component.c_str(), name_of(rc),
                         reply.flags, reply.hold_ns / 1000.0);
                    if (rc == KILN_OK) g_expected_events.fetch_add(1);
                    if (rc == KILN_ECS_ERR_CONFLICT)
                        why += "the game changed the component between the change and the restore (not forced back); ";
                    std::vector<uint8_t> after;
                    const KilnResult arc = snapshot(ref, target, after);
                    const bool same = arc == KILN_OK && after == original;
                    KW_I("world ecs-write check: read again: %s, %s the original", name_of(arc), same ? "equal to" : "NOT equal to");
                    if (rc != KILN_OK || !same) { pass = false; why += "the restore was not verified; "; }
                }
            }
        }
    } else if (pass) {
        pass = false;
        why += g_component.empty() ? "the player has no writable component; " : "the player has no " + g_component + "; ";
    }
    if (!pass) g_passed.store(false);
    g_why += why;
}
void KILN_CALL check_done(KilnHandle task, uint32_t, void* user) {
    delete static_cast<Job*>(user);
    if (g_tasks) g_tasks->release(g_owner, task);
    g_phase.store(Phase::Counting);
}

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}
std::string trim(const std::string& s) {
    const auto b = s.find_first_not_of(" \t\r\n"), e = s.find_last_not_of(" \t\r\n");
    return b == std::string::npos ? "" : s.substr(b, e - b + 1);
}

}  // namespace

void ecs_write_check_init() {
    char buf[1024];
    size_t len = 0;
    if (!g_api || g_api->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK) return;
    std::error_code ec;
    const std::filesystem::path file =
        std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(buf), len)) / "kiln.world" / "ecs-write-check";
    if (!std::filesystem::exists(file, ec)) return;
    std::ifstream in(file);
    for (std::string line; std::getline(in, line);) {
        const auto eq = line.find('=');
        if (eq == std::string::npos) continue;
        const std::string key = lower(trim(line.substr(0, eq))), value = trim(line.substr(eq + 1));
        if (key == "world") g_check_world = lower(value);
        else if (key == "component") g_component = value;
        else if (key == "offset") g_offset = static_cast<uint32_t>(std::strtoul(value.c_str(), nullptr, 10));
    }
    const bool named = g_check_world.size() == 32 && g_check_world.find_first_not_of("0123456789abcdef") == std::string::npos;
    if (!named) {
        g_check_world.clear();
        KW_I("world ecs-write check: the check file names no world (world=<32 hex digits>); it will log the running "
             "world's GUID and do nothing else");
    }
    g_tasks = static_cast<const KilnTasksApi*>(
        g_api->service(KilnStr{KILN_TASKS_SERVICE, sizeof(KILN_TASKS_SERVICE) - 1}, KILN_TASKS_VERSION));
    g_saves = static_cast<const KilnSavesApi*>(
        g_api->service(KilnStr{KILN_SAVES_SERVICE, sizeof(KILN_SAVES_SERVICE) - 1}, KILN_SAVES_VERSION));
    g_types = static_cast<const KilnTypesApi*>(
        g_api->service(KilnStr{KILN_TYPES_SERVICE, sizeof(KILN_TYPES_SERVICE) - 1}, KILN_TYPES_VERSION));
    if (!g_tasks || g_tasks->header.size < sizeof(KilnTasksApi) || !g_saves || !g_saves->status) {
        KW_W("world ecs-write check: enabled, but kiln.tasks or kiln.saves is not here; the check cannot run");
        return;
    }
    if (t_ecs_write_subscribe(g_owner, &on_event, nullptr, &g_sub) != KILN_OK)
        KW_W("world ecs-write check: no event subscription (writes are off?); events are not counted");
    g_next = std::chrono::steady_clock::now() + std::chrono::seconds(20);
    g_phase.store(Phase::Waiting);
    KW_I("world ecs-write check enabled for world %s%s%s", g_check_world.c_str(),
         g_component.empty() ? " (no component named: list and no-op only)" : ", component ", g_component.c_str());
}

void ecs_write_check_pre(const KilnTickInfo* tick) {
    const Phase phase = g_phase.load();
    if (phase == Phase::Counting) {
        if (!g_done_step.load()) g_done_step.store(tick ? tick->step : 1);
        if (tick && tick->step < g_done_step.load() + 3) return;  // the drains of a few steps
        const int events = g_events.load(), expected = g_expected_events.load();
        if (events != expected) {
            g_passed.store(false);
            g_why += "events: " + std::to_string(events) + " delivered, " + std::to_string(expected) + " expected; ";
        }
        KW_I("world ecs-write check: %d committed-write event(s) delivered", events);
        KW_I("world ecs-write check: %s%s%s", g_passed.load() ? "PASS" : "FAIL", g_why.empty() ? "" : ": ", g_why.c_str());
        g_phase.store(Phase::Done);
        return;
    }
    if (phase != Phase::Waiting || !g_world_live.load()) return;
    const auto now = std::chrono::steady_clock::now();
    if (now < g_next) return;
    g_next = now + std::chrono::seconds(20);
    // Only on the world the check file names.
    KilnSavesStatus st{};
    st.size = sizeof st;
    if (g_saves->status(&st) != KILN_OK || !st.has_guid) return;  // not known yet: try again later
    if (hex(st.world_guid, 16) != g_check_world) {
        KW_I("world ecs-write check: this world is %s, the check file names %s; the check stays off",
             hex(st.world_guid, 16).c_str(), g_check_world.empty() ? "none" : g_check_world.c_str());
        g_phase.store(Phase::Done);
        return;
    }
    // A player: the first id 1..1023 with a CurrentTransform.
    static std::vector<KilnEntity> ids(1 << 18);
    uint32_t n = 0;
    const KilnResult frc = find(kCurrentTransformHash, ids.data(), static_cast<uint32_t>(ids.size()), &n);
    if (frc != KILN_OK && frc != KILN_ERR_BUFFER_TOO_SMALL) return;
    KilnEntity player = 0;
    for (uint32_t i = 0; i < std::min<uint32_t>(n, static_cast<uint32_t>(ids.size())) && !player; ++i)
        if (ids[i] && ids[i] <= 1023) player = ids[i];
    if (!player) return;  // no player yet
    // From the tick (the named component, whose policy lets a task write it): busy in frame, nothing written.
    if (!g_component.empty()) {
        KilnObjectRef ref{};
        std::vector<uint8_t> bytes;
        KilnMutationReply reply{};
        reply.size = sizeof reply;
        KilnResult rc = t_entity_open(g_owner, g_owner, KILN_WORLD_SERVER, 0, player, &ref);
        if (rc == KILN_OK) rc = snapshot(ref, component_hash(g_component), bytes);
        if (rc == KILN_OK)
            rc = t_ecs_compare_exchange(g_owner, &ref, component_hash(g_component), bytes.data(), nullptr, bytes.data(),
                                        static_cast<uint32_t>(bytes.size()), nullptr, &reply);
        const bool ok = rc == KILN_ECS_ERR_BUSY && reply.why_code == KILN_ECS_WRITE_WHY_IN_FRAME;
        KW_I("world ecs-write check: a compare-exchange of %s from the tick: %s (why %u; busy in frame expected)",
             g_component.c_str(), name_of(rc), reply.why_code);
        if (!ok) { g_passed.store(false); g_why += "a compare-exchange from the tick was not refused in frame; "; }
    }
    auto* job = new Job{player};
    KilnTaskInfo info{};
    info.size = sizeof info;
    info.name = KilnStr{"world ecs-write check", sizeof "world ecs-write check" - 1};
    info.flags = KILN_TASK_AFTER_STEP;
    KilnHandle task = 0;
    uint32_t refusal = 0;
    g_phase.store(Phase::Running);
    if (g_tasks->submit(g_owner, &info, &check_body, &check_done, job, &task, &refusal) != KILN_OK) {
        KW_W("world ecs-write check: kiln.tasks refused the task (refusal %u)", refusal);
        delete job;
        g_phase.store(Phase::Waiting);
    }
}

}  // namespace kw
