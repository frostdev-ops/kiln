// SPDX-License-Identifier: MIT
// A fake Kiln for gate-priority-test, the same on the host (tests/kit_test.cpp, the mod's code linked in) and on Windows
// under Wine (tests/host_win.cpp, the mod's DLL loaded with LoadLibrary). kiln.world is the real thing where it
// matters: its ECS reads and compare-exchange writes (ecs_engine, ecs_lookup, ecs_reads, ecs_writes, ecs_write_hold,
// ecs_write_policy) over kiln.engine's real write gate (gate.cpp, resolve.cpp) and memory primitives (memory_host.cpp on
// the host, memory_win.cpp's SEH copies and Interlocked words on Windows), against the fake engine of the world tests
// laid out from the dedicated server's exact profile. kiln.types serves sigdb's reflection of that build (offsets,
// alignments). A simulated GameServer thread runs frames that hold the Scene word Shared, calls the mod's Pre and Post
// inside them, and now and then changes a real field of the mod's target, as the game would.
//
// One scenario per process (the mod's state lives for the process, as in the game): <exe> <scenario> <sigdb/types dir>
// [<mod dll>]. Prints PASS/FAIL lines and exits 1 on a failure.
#pragma once
#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#endif

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "ecs_engine.h"
#include "ecs_epochs.h"
#include "ecs_fake_world.h"
#include "ecs_writes.h"
#include "engine_state.h"
#include "gate.h"
#include "include/kiln_engine_native.h"
#include "kiln_native.h"
#include "memory.h"
#include "nlohmann/json.hpp"
#include "profiles.g.h"
#include "resolve.h"
#include "services/kiln_caps.h"
#include "services/kiln_engine.h"
#include "services/kiln_players.h"
#include "services/kiln_saves.h"
#include "services/kiln_tasks.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

#ifndef _WIN32
namespace kiln::engine::mem {
extern std::atomic<std::uintptr_t> test_image;
extern thread_local int test_priority;
}  // namespace kiln::engine::mem
#endif

// kiln.engine's counters (engine.cpp is not linked here). Defined here: one translation unit per program includes this.
namespace kiln::engine {
Stats& stats() {
    static Stats s;
    return s;
}
}  // namespace kiln::engine

namespace harness {
namespace fs = std::filesystem;
using namespace kw::ecs;

inline int checks = 0, failures = 0;
inline void check(bool ok, const std::string& what) {
    ++checks;
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what.c_str());
    if (!ok) ++failures;
}

// ---- kiln.types over sigdb's reflection (offsets and alignments included) ----
struct Type {
    std::string name;
    uint8_t kind = 0;
    uint32_t size = 0, align = 0, hash = 0;
    std::string inner_name;
    std::vector<std::tuple<std::string, std::string, uint64_t>> field_names;  // name, type, offset
    const Type* inner = nullptr;
    std::vector<KilnTypeField> fields;
};
struct Types {
    std::deque<Type> all;
    std::map<std::string, Type*> by_name;
    std::map<uint32_t, Type*> by_hash;
    std::vector<std::string> components;
    std::vector<uint32_t> registry;  // component hashes in registration order (the fake world's)
    std::map<uint32_t, uint32_t> registered_size;
    Type& add(const std::string& name, uint8_t kind, uint32_t size, uint32_t align, const std::string& inner = "",
              std::vector<std::tuple<std::string, std::string, uint64_t>> fields = {}) {
        all.push_back(Type{name, kind, size, align, fakeworld::fnv1a(name), inner, std::move(fields), nullptr, {}});
        by_name[name] = &all.back();
        by_hash[all.back().hash] = &all.back();
        return all.back();
    }
    void load(const std::string& path) {
        std::ifstream in(path);
        const auto doc = nlohmann::json::parse(in);
        for (const auto& t : doc.at("types")) {
            std::vector<std::tuple<std::string, std::string, uint64_t>> fields;
            if (t.contains("fields"))
                for (const auto& f : t.at("fields"))
                    fields.emplace_back(f.at("name").get<std::string>(), f.at("type").get<std::string>(), f.at("offset").get<uint64_t>());
            add(t.at("qualifiedName").get<std::string>(), static_cast<uint8_t>(t.at("kind").get<int>()), t.at("size").get<uint32_t>(),
                t.value("align", 0u), t.contains("inner") ? t.at("inner").get<std::string>() : "", std::move(fields));
        }
        for (const auto& c : doc.at("components")) components.push_back(c.get<std::string>());
    }
    void link() {
        for (Type& t : all) {
            const auto it = by_name.find(t.inner_name);
            t.inner = t.inner_name.empty() || it == by_name.end() ? nullptr : it->second;
            t.fields.clear();
            for (const auto& [fname, ftype, off] : t.field_names) {
                const auto f = by_name.find(ftype);
                KilnTypeField kf{};
                kf.name = KilnStr{fname.data(), fname.size()};
                kf.type = f == by_name.end() ? nullptr : reinterpret_cast<const KilnType*>(f->second);
                kf.offset = off;
                t.fields.push_back(kf);
            }
        }
    }
};
inline Types* g_types = nullptr;
inline const KilnType* KILN_CALL t_by_hash(uint32_t h) {
    const auto it = g_types->by_hash.find(h);
    return it == g_types->by_hash.end() ? nullptr : reinterpret_cast<const KilnType*>(it->second);
}
inline const KilnType* KILN_CALL t_by_name(KilnStr n) {
    const auto it = g_types->by_name.find(std::string(n.ptr, n.len));
    return it == g_types->by_name.end() ? nullptr : reinterpret_cast<const KilnType*>(it->second);
}
inline KilnResult KILN_CALL t_info(const KilnType* type, KilnTypeInfo* out) {
    if (!type || !out) return KILN_ERR_INVALID_ARGUMENT;
    const Type& t = *reinterpret_cast<const Type*>(type);
    const uint32_t size = out->size;
    *out = KilnTypeInfo{};
    out->size = size;
    const auto colon = t.name.rfind("::");
    out->name = colon == std::string::npos ? KilnStr{t.name.data(), t.name.size()}
                                           : KilnStr{t.name.data() + colon + 2, t.name.size() - colon - 2};
    out->qualified_name = KilnStr{t.name.data(), t.name.size()};
    out->type_size = t.size;
    out->alignment = static_cast<uint16_t>(t.align);
    out->kind = t.kind;
    out->hash = t.hash;
    out->field_count = static_cast<uint32_t>(t.fields.size());
    out->inner = reinterpret_cast<const KilnType*>(t.inner);
    return KILN_OK;
}
inline KilnResult KILN_CALL t_field(const KilnType* type, uint32_t index, KilnTypeField* out) {
    if (!type || !out) return KILN_ERR_INVALID_ARGUMENT;
    const Type& t = *reinterpret_cast<const Type*>(type);
    if (index >= t.fields.size()) return KILN_ERR_NOT_FOUND;
    *out = t.fields[index];
    return KILN_OK;
}
inline KilnResult KILN_CALL t_registry(uint32_t kind, KilnEcsRegistryInfo* info, KilnEcsRegistryRow* rows, uint32_t cap,
                                       uint32_t* required) {
    if (kind != KILN_WORLD_SERVER || !info || !required) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t n = static_cast<uint32_t>(g_types->registry.size());
    *required = n;
    info->status = 0;
    info->world_kind = kind;
    info->count = n;
    if (cap < n) return KILN_ERR_BUFFER_TOO_SMALL;
    for (uint32_t i = 0; i < n; ++i) {
        rows[i] = KilnEcsRegistryRow{};
        rows[i].size = sizeof rows[i];
        rows[i].index = i;
        rows[i].component = g_types->registry[i];
        rows[i].type_size = g_types->registered_size[g_types->registry[i]];
    }
    return KILN_OK;
}
inline KilnTypesApi types_table() {
    KilnTypesApi t{};
    t.header = {sizeof(KilnTypesApi), KILN_TYPES_VERSION};
    t.type_by_hash = &t_by_hash;
    t.type_by_name = &t_by_name;
    t.type_info = &t_info;
    t.type_field = &t_field;
    t.ecs_registry = &t_registry;
    return t;
}

// ---- the engine and kiln.world ----
inline std::atomic<std::uintptr_t> g_image{0};
inline const kiln::engine::Layout* g_layout = nullptr;
inline thread_local bool t_server_thread = false;
inline std::atomic<uint64_t> g_holds{0}, g_boosted_holds{0};

inline int thread_priority_now() {
#ifdef _WIN32
    return GetThreadPriority(GetCurrentThread());
#else
    return kiln::engine::mem::test_priority;
#endif
}
inline const kiln::engine::Layout* KILN_CALL e_layout() { return g_layout; }
inline KilnResult KILN_CALL e_copy(std::uintptr_t address, void* out, std::size_t bytes) {
    if (!bytes) return KILN_OK;
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    return kiln::engine::mem::read_span(address, out, bytes) ? KILN_OK : KILN_ERR_UNAVAILABLE;
}
inline KilnResult KILN_CALL e_view(std::uint32_t kind, kiln::engine::EcsView* out, std::uint32_t* reason) {
    kiln::engine::EcsView v{};
    const auto r = kiln::engine::ecs_view(g_layout, static_cast<kiln::engine::ViewKind>(kind), v, g_image.load());
    *reason = static_cast<std::uint32_t>(r);
    if (r != kiln::engine::Reason::None) return KILN_ERR_UNAVAILABLE;
    *out = v;
    return KILN_OK;
}
inline KilnResult KILN_CALL e_acquire(const kiln::engine::EcsView* expected, kiln::engine::EcsGate* gate, std::uint32_t* reason) {
    const auto caller = t_server_thread ? kiln::engine::CallerThread::ServerWorld : kiln::engine::CallerThread::Other;
    const auto r = kiln::engine::gate_acquire(g_layout, *expected, *gate, caller, g_image.load());
    *reason = static_cast<std::uint32_t>(r);
    if (r != kiln::engine::Reason::None) return KILN_ERR_UNAVAILABLE;
    g_holds.fetch_add(1);
    // the holder's priority right after the gate was taken: raised (above normal) or its own
#ifdef _WIN32
    if (thread_priority_now() >= THREAD_PRIORITY_ABOVE_NORMAL) g_boosted_holds.fetch_add(1);
#else
    if (thread_priority_now() >= 1) g_boosted_holds.fetch_add(1);
#endif
    return KILN_OK;
}
// Scenario.uncertain_write: the gate check right after that write (1-based) fails, so kiln.world cannot prove the write
// it just made and answers UNCERTAIN (the bytes are written; the target is latched for the session).
inline std::atomic<int> g_uncertain_write{0}, g_gate_writes{0};
inline std::atomic<bool> g_fail_next_validate{false};
inline std::int32_t KILN_CALL e_validate(const kiln::engine::EcsGate* gate) {
    if (g_fail_next_validate.exchange(false)) return 0;
    return kiln::engine::gate_validate(g_layout, *gate, g_image.load()) ? 1 : 0;
}
inline std::int32_t KILN_CALL e_component(const kiln::engine::EcsGate* gate, std::uint16_t c) {
    return kiln::engine::gate_component_supported(*gate, c) ? 1 : 0;
}
inline KilnResult KILN_CALL e_write(const kiln::engine::EcsGate* gate, std::uintptr_t address, const void* data, std::size_t bytes) {
    const auto written = kiln::engine::gate_write(*gate, address, data, bytes);
    if (written == kiln::engine::GateWrite::Ok && g_uncertain_write.load() && ++g_gate_writes == g_uncertain_write.load())
        g_fail_next_validate.store(true);
    switch (written) {
        case kiln::engine::GateWrite::Ok: return KILN_OK;
        case kiln::engine::GateWrite::TooLarge: return KILN_ERR_BUFFER_TOO_SMALL;
        case kiln::engine::GateWrite::NotHeld: return KILN_ERR_INVALID_ARGUMENT;
        default: return KILN_ERR_UNAVAILABLE;
    }
}
inline void KILN_CALL e_release(kiln::engine::EcsGate* gate) { kiln::engine::gate_release(*gate); }

struct Worlds {
    std::atomic<uintptr_t> world{0};
    std::atomic<uint64_t> session{41}, steps{1};
    OwnerEpochs epochs;
};
inline Worlds* g_worlds = nullptr;
inline uint32_t w_fact(void*, uint32_t kind, engine::WorldFact* out) {
    out->world = kind == KILN_WORLD_SERVER ? g_worlds->world.load() : 0;
    out->session = kind == KILN_WORLD_SERVER ? g_worlds->session.load() : 0;
    return KILN_ECS_OK;
}
inline uint64_t w_epoch(void*, KilnOwner o) { return g_worlds->epochs.current(o); }
inline uint64_t w_open_epoch(void*, KilnOwner o, uint64_t e) { return g_worlds->epochs.open(o, e); }
inline bool w_steps(void*, uint32_t, uint64_t* out) {
    *out = g_worlds->steps.load();
    return true;
}

inline engine::Reader* g_reader = nullptr;
inline Backend* g_backend = nullptr;
inline writes::Writer* g_writer = nullptr;
inline bool guarded_call(void (*fn)(void*), void* ctx, unsigned long* code) {
#ifdef _WIN32
    __try {
        fn(ctx);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#else
    try {
        fn(ctx);
        return true;
    } catch (...) {
        *code = 0xE06D7363;
        return false;
    }
#endif
}

inline KilnResult KILN_CALL w_entity_open(KilnOwner o, uint64_t e, uint32_t k, uint64_t s, KilnEntity id, KilnObjectRef* out) {
    return entity_open(*g_backend, o, e, k, s, id, out);
}
inline KilnResult KILN_CALL w_entity_validate(KilnOwner o, const KilnObjectRef* r, KilnObjectInfo* out) {
    return entity_validate(*g_backend, o, r, out);
}
inline KilnResult KILN_CALL w_context(KilnEcsContextInfo* out) { return ecs_context_snapshot(*g_backend, out); }
inline KilnResult KILN_CALL w_query(KilnOwner o, const KilnEcsQueryRequest* q, KilnObjectRef* out, uint32_t cap, uint32_t* req,
                                    uint32_t* state) {
    return ecs_query(*g_backend, o, q, out, cap, req, state);
}
inline KilnResult KILN_CALL w_snapshot(KilnOwner o, const KilnObjectRef* r, uint32_t c, void* out, uint32_t cap, uint32_t* req) {
    return ecs_snapshot(*g_backend, o, r, c, out, cap, req);
}
inline KilnResult KILN_CALL w_snapshot_many(KilnOwner o, const KilnObjectRef* r, const KilnEcsSnapshotSpec* s, uint32_t n,
                                            void* out, uint32_t cap, uint32_t* req, uint32_t* presence) {
    return ecs_snapshot_many(*g_backend, o, r, s, n, out, cap, req, presence);
}
inline KilnResult KILN_CALL w_template(KilnOwner o, const KilnObjectRef* r, uint8_t guid[16]) {
    return ecs_template_uuid(*g_backend, o, r, guid);
}
inline KilnResult KILN_CALL w_policy(uint32_t k, uint32_t c, KilnEcsWritePolicy* out) { return g_writer->policy(k, c, out); }
inline KilnResult KILN_CALL w_cx(KilnOwner o, const KilnObjectRef* r, uint32_t c, const void* e, const void* m, const void* d,
                                 uint32_t b, void* conf, KilnMutationReply* reply) {
    return g_writer->compare_exchange(o, r, c, e, m, d, b, conf, reply);
}
inline KilnResult KILN_CALL w_subscribe(KilnOwner o, KilnEcsWriteEventFn fn, void* u, KilnHandle* out) {
    return g_writer->subscribe(o, fn, u, out);
}
inline KilnResult KILN_CALL w_unsubscribe(KilnHandle h) { return g_writer->unsubscribe(h); }
inline uint32_t KILN_CALL w_caps() { return KILN_WORLD_CAP_ECS_READ | KILN_WORLD_CAP_ECS_WRITE; }

// ---- kiln.tasks: a thread per task (the pool's rules that matter here: below-normal threads, done after the body) ----
struct Tasks {
    std::mutex mu;
    std::vector<std::thread> threads;
    std::atomic<bool> closing{false};
    std::atomic<uint64_t> next{1}, submitted{0};
};
inline Tasks* g_tasks = nullptr;
inline uint32_t KILN_CALL never_cancelled(const KilnTaskRun*) { return g_tasks->closing.load() ? 1u : 0u; }
inline KilnResult KILN_CALL k_submit(KilnOwner owner, const KilnTaskInfo* info, KilnTaskBodyFn body, KilnTaskDoneFn done, void* user,
                                     KilnHandle* task, uint32_t* refusal) {
    if (!info || !body || !done || !task) {
        if (refusal) *refusal = KILN_TASK_REFUSAL_ARGUMENT;
        return KILN_ERR_INVALID_ARGUMENT;
    }
    if (g_tasks->closing.load()) {
        *task = 0;
        if (refusal) *refusal = KILN_TASK_REFUSAL_SHUTDOWN;
        return KILN_ERR_UNAVAILABLE;
    }
    const uint64_t h = g_tasks->next.fetch_add(1);
    *task = h;
    g_tasks->submitted.fetch_add(1);
    std::lock_guard lock(g_tasks->mu);
    g_tasks->threads.emplace_back([=] {
#ifdef _WIN32
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
#else
        kiln::engine::mem::test_priority = -1;
#endif
        KilnTaskRun run{};
        run.size = sizeof run;
        run.task = h;
        run.owner = owner;
        run.cancelled = &never_cancelled;
        body(&run, user);
        done(h, KILN_TASK_COMPLETED, user);
    });
    return KILN_OK;
}
inline KilnResult KILN_CALL k_cancel(KilnOwner, KilnHandle, uint32_t* state) {
    if (state) *state = KILN_TASK_RUNNING;
    return KILN_OK;
}
inline KilnResult KILN_CALL k_status(KilnOwner, KilnHandle, uint32_t* state) {
    if (state) *state = KILN_TASK_RUNNING;
    return KILN_OK;
}
inline KilnResult KILN_CALL k_release(KilnOwner, KilnHandle) { return KILN_OK; }
inline KilnResult KILN_CALL k_drain(KilnOwner, uint32_t, uint32_t* running) {
    if (running) *running = 0;
    return KILN_OK;
}
inline void join_tasks() {
    g_tasks->closing.store(true);
    for (;;) {
        std::vector<std::thread> take;
        {
            std::lock_guard lock(g_tasks->mu);
            take.swap(g_tasks->threads);
        }
        if (take.empty()) break;
        for (auto& t : take) t.join();
    }
}

// ---- the rest of the fake Kiln ----
inline uint8_t g_guid[16] = {0x6b, 0x69, 0x6c, 0x6e, 0x2d, 0x74, 0x65, 0x73, 0x74, 0x2d, 0x77, 0x6f, 0x72, 0x6c, 0x64, 0x01};
inline std::string guid_hex() {
    std::string s;
    char b[3];
    for (uint8_t x : g_guid) {
        std::snprintf(b, sizeof b, "%02x", x);
        s += b;
    }
    return s;
}
inline KilnResult KILN_CALL s_status(KilnSavesStatus* out) {
    if (!out || out->size < sizeof(KilnSavesStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    *out = KilnSavesStatus{};
    out->size = size;
    std::memcpy(out->world_guid, g_guid, 16);
    out->has_guid = 1;
    return KILN_OK;
}
inline std::atomic<KilnEntity> g_player_entity{1};
inline KilnResult KILN_CALL p_list(uint32_t, KilnPlayer* out, uint32_t cap, uint32_t* count) {
    *count = 1;
    if (cap < 1) return KILN_ERR_BUFFER_TOO_SMALL;
    out[0] = KilnPlayer{};
    out[0].size = sizeof(KilnPlayer);
    out[0].entity = g_player_entity.load();
    out[0].state = KILN_PLAYER_IN_GAME;
    return KILN_OK;
}
inline std::atomic<bool> g_raise_on{true};
inline KilnResult KILN_CALL c_find(KilnStr name, KilnCapRow* out) {
    if (std::string(name.ptr, name.len) != "engine.gate_priority_raise") return KILN_ERR_NOT_FOUND;
    static const char off[] = "off for this run by the measurement switch kiln/data/kiln.engine/gate-priority-raise";
    const uint32_t size = out->size;
    *out = KilnCapRow{};
    out->size = size;
    out->state = g_raise_on.load() ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE;
    out->reason = g_raise_on.load() ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_POLICY;
    out->name = name;
    out->text = g_raise_on.load() ? KilnStr{"", 0} : KilnStr{off, sizeof off - 1};
    return KILN_OK;
}
inline KilnResult KILN_CALL g_identity(KilnEngineIdentity* out) {
    const uint32_t size = out->size;
    *out = KilnEngineIdentity{};
    out->size = size;
    out->status = KILN_ENGINE_EXACT;
    std::snprintf(out->profile_id, sizeof out->profile_id, "%s", "test-profile");
    return KILN_OK;
}
inline KilnResult KILN_CALL g_gate_stats(KilnEngineGateStats* out) {
    const auto& c = kiln::engine::stats();
    *out = KilnEngineGateStats{};
    out->size = sizeof *out;
    out->attempts = c.attempts.load();
    out->acquired = c.acquired.load();
    out->busy_scene = c.busy_scene.load();
    out->hold_ns_total = c.hold_ns_total.load();
    out->hold_ns_max = c.hold_ns_max.load();
    return KILN_OK;
}

struct Host {
    std::mutex log_mu;
    std::vector<std::string> logs;
    std::map<std::string, const void*> services;
    std::vector<std::pair<KilnTickFn, void*>> pre, post;
    std::vector<std::pair<KilnWorldEventFn, void*>> world_events;
    std::string data_dir, logs_dir;
};
inline Host* g_host = nullptr;
inline KilnStr KILN_CALL h_version() { return KilnStr{"0.2.0-test", 10}; }
inline void KILN_CALL h_log(KilnOwner, int32_t level, KilnStr text) {
    std::lock_guard lock(g_host->log_mu);
    g_host->logs.emplace_back(std::string(level == KILN_LOG_WARN ? "W " : level == KILN_LOG_ERROR ? "E " : "I ") +
                              std::string(text.ptr, text.len));
    std::printf("  log: %s\n", g_host->logs.back().c_str());
}
inline KilnResult KILN_CALL h_path(int32_t kind, char* buf, size_t cap, size_t* len) {
    const std::string* p = kind == KILN_PATH_DATA ? &g_host->data_dir : kind == KILN_PATH_LOGS ? &g_host->logs_dir : nullptr;
    if (!p) return KILN_ERR_NOT_FOUND;
    *len = p->size();
    if (cap < p->size()) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, p->data(), p->size());
    return KILN_OK;
}
inline const void* KILN_CALL h_service(KilnStr name, uint32_t) {
    const auto it = g_host->services.find(std::string(name.ptr, name.len));
    return it == g_host->services.end() ? nullptr : it->second;
}
inline KilnResult KILN_CALL h_subscribe_tick(KilnOwner, int32_t phase, KilnTickFn fn, void* user, KilnHandle* out) {
    (phase == KILN_TICK_PRE ? g_host->pre : g_host->post).emplace_back(fn, user);
    *out = 1;
    return KILN_OK;
}
inline KilnResult KILN_CALL h_subscribe_world(KilnOwner, KilnWorldEventFn fn, void* user, KilnHandle* out) {
    g_host->world_events.emplace_back(fn, user);
    *out = 2;
    return KILN_OK;
}
inline uint32_t KILN_CALL h_side() { return KILN_SIDE_SERVER; }
inline bool KILN_CALL h_on_world_thread(uint32_t kind) { return kind == KILN_WORLD_SERVER && t_server_thread; }
inline size_t logs_with(const std::string& part) {
    std::lock_guard lock(g_host->log_mu);
    return static_cast<size_t>(std::count_if(g_host->logs.begin(), g_host->logs.end(),
                                             [&](const std::string& l) { return l.find(part) != std::string::npos; }));
}

// ---- one scenario ----
struct Scenario {
    std::string name;
    std::string run_file;            // contents; "<none>" = no file
    bool raise = true;
    std::vector<std::string> comps;  // real components registered and put on the player
    double seconds = 3.0;            // how long the server runs
    int stop_after_ms = -1;          // the server world stops after this long (-1: at the end)
    int game_writes_every = 0;       // every Nth frame the game changes byte 0 of `game_target`
    std::string game_target;
    int uncertain_write = 0;         // this write (1-based) of the gate cannot be proven: UNCERTAIN (0: none)
};

struct Rig {
    const kiln::engine::Profile* profile = nullptr;
    std::unique_ptr<fake::Engine> engine;
    std::unique_ptr<fakeworld::World> world;
    Types types;
    KilnTypesApi types_api{};
    KilnEngineNativeApi native{};
    Worlds worlds;
    std::unique_ptr<engine::Reader> reader;
    Backend backend{};
    std::unique_ptr<writes::Writer> writer;
    KilnWorldApi world_api{};
    KilnTasksApi tasks_api{};
    KilnSavesApi saves_api{};
    KilnPlayersApi players_api{};
    KilnEngineApi engine_api{};
    KilnCapsApi caps_api{};
    Tasks tasks;
    Host host;
    KilnApi api{};
    uintptr_t layout = 0;  // the player's archetype
    std::map<std::string, std::vector<uint8_t>> initial;  // the player's components at the start
    std::atomic<int> game_changes{0};
    fs::path root;
};

inline uintptr_t component_at(Rig& r, const std::string& name) { return r.world->row_address(r.layout, 0, name); }
inline std::vector<uint8_t> bytes_of(Rig& r, const std::string& name) {
    const auto& c = r.world->comp(name);
    std::vector<uint8_t> v(c.size);
    std::memcpy(v.data(), reinterpret_cast<const void*>(component_at(r, name)), c.size);
    return v;
}

inline void set_tracked(Rig& r, const std::string& name) {
    const auto& L = r.profile->layout;
    const uint16_t index = r.world->comp(name).index;
    const uintptr_t word = r.engine->registry_owner() + L.write.tracking_registry_a + (index / 64) * 8;
    fake::put<uint64_t>(word, fake::get<uint64_t>(word) | (uint64_t{1} << (index % 64)));
}

// Builds the fake engine, the world (the player with `comps`), kiln.world and the fake Kiln around them.
inline bool build(Rig& r, const Scenario& sc, const std::string& sigdb) {
    for (const auto& p : kiln::engine::generated::kProfiles)
        if (p.layout.dedicated_server) r.profile = &p;
    if (!r.profile) return false;
    g_layout = &r.profile->layout;
    r.engine = std::make_unique<fake::Engine>(r.profile->layout);
    g_image = r.engine->image.at();
#ifndef _WIN32
    kiln::engine::mem::test_image = r.engine->image.at();
#endif
    r.types.load(sigdb + "/server/" + r.profile->sha256 + ".json");
    // synthetic components whose names sort first, so a broken rule would pick one of them: two fields that overlap
    // (as in a union), a gap that alignment does not explain, nothing reflected at all
    r.types.add("keen::ecs::AaKitUnionLike", KILN_TYPE_STRUCT, 8, 4, "", {{"a", "keen::uint32", 0}, {"b", "keen::uint8", 2}});
    r.types.add("keen::ecs::AbKitGap", KILN_TYPE_STRUCT, 8, 4, "", {{"a", "keen::uint32", 0}, {"b", "keen::uint8", 6}});
    r.types.add("keen::ecs::AcKitEmpty", KILN_TYPE_STRUCT, 1, 1, "keen::ecs::Component");
    r.types.link();
    g_types = &r.types;
    r.types_api = types_table();
    r.world = std::make_unique<fakeworld::World>(r.profile->layout, r.engine->image, r.engine->registry_owner(), r.engine->manager());
    for (const auto& name : sc.comps) {
        const Type* t = r.types.by_name.at(name);
        r.world->reg(name.substr(std::strlen("keen::ecs::")), static_cast<uint16_t>(t->size));
        r.types.registry.push_back(fakeworld::fnv1a(name));
        r.types.registered_size[fakeworld::fnv1a(name)] = t->size;
    }
    r.layout = r.world->archetype(sc.comps);
    r.world->add(1, 1, r.layout, 0, fakeworld::kUuidPlayer);
    for (const auto& name : sc.comps) {
        std::vector<uint8_t> b(r.world->comp(name).size);
        for (size_t i = 0; i < b.size(); ++i) b[i] = static_cast<uint8_t>(0x10 + i);  // padding included: not all zero
        r.world->bytes(1, name, b);
        r.initial[name] = b;
    }
    if (std::find(sc.comps.begin(), sc.comps.end(), "keen::ecs::CurrentTransform") != sc.comps.end())
        set_tracked(r, "keen::ecs::CurrentTransform");  // the game tracks it (saved and replicated)

    r.native.header = {sizeof r.native, KILN_ENGINE_NATIVE_VERSION};
    r.native.layout_size = sizeof(kiln::engine::Layout);
    r.native.profile_size = sizeof(kiln::engine::Profile);
    r.native.layout = &e_layout;
    r.native.copy = &e_copy;
    r.native.ecs_view = &e_view;
    r.native.ecs_gate_acquire = &e_acquire;
    r.native.ecs_gate_validate = &e_validate;
    r.native.ecs_gate_component_supported = &e_component;
    r.native.ecs_gate_write = &e_write;
    r.native.ecs_gate_release = &e_release;
    g_worlds = &r.worlds;
    r.worlds.world = r.engine->exec.at();
    engine::Source s;
    s.engine = &r.native;
    s.layout = g_layout;
    s.exact_profile = true;
    s.reads = engine::layout_refusal(g_layout) == nullptr;
    s.world = &w_fact;
    s.epoch = &w_epoch;
    s.open_epoch = &w_open_epoch;
    s.steps = &w_steps;
    r.reader = std::make_unique<engine::Reader>(s);
    r.backend = r.reader->backend();
    g_reader = r.reader.get();
    g_backend = &r.backend;
    writes::WriterSource ws;
    ws.reader = [](void*) { return g_reader; };
    ws.engine = &r.native;
    ws.types = &r.types_api;
    ws.exact_profile = true;
    ws.server_step = [](void*) -> uint64_t { return g_worlds->steps.load(); };
    ws.guarded = &guarded_call;
    ws.log = [](void*, int, const char* text) { h_log(0, KILN_LOG_INFO, KilnStr{text, std::strlen(text)}); };
    r.writer = std::make_unique<writes::Writer>(ws);
    g_writer = r.writer.get();
    kiln::engine::gate_set_priority_raise(sc.raise);
    g_raise_on = sc.raise;
    g_uncertain_write = sc.uncertain_write;

    r.world_api.header = {sizeof(KilnWorldApi), KILN_WORLD_VERSION};
    r.world_api.capabilities = &w_caps;
    r.world_api.entity_open = &w_entity_open;
    r.world_api.entity_validate = &w_entity_validate;
    r.world_api.ecs_context_snapshot = &w_context;
    r.world_api.ecs_query = &w_query;
    r.world_api.ecs_snapshot = &w_snapshot;
    r.world_api.ecs_snapshot_many = &w_snapshot_many;
    r.world_api.ecs_template_uuid = &w_template;
    r.world_api.ecs_write_policy = &w_policy;
    r.world_api.ecs_compare_exchange = &w_cx;
    r.world_api.ecs_write_subscribe = &w_subscribe;
    r.world_api.ecs_write_unsubscribe = &w_unsubscribe;
    g_tasks = &r.tasks;
    r.tasks_api.header = {sizeof r.tasks_api, KILN_TASKS_VERSION};
    r.tasks_api.submit = &k_submit;
    r.tasks_api.cancel = &k_cancel;
    r.tasks_api.status = &k_status;
    r.tasks_api.release = &k_release;
    r.tasks_api.drain_owner = &k_drain;
    r.saves_api.header = {sizeof r.saves_api, KILN_SAVES_VERSION};
    r.saves_api.status = &s_status;
    r.players_api.header = {sizeof r.players_api, KILN_PLAYERS_VERSION};
    r.players_api.list = &p_list;
    r.engine_api.header = {sizeof r.engine_api, KILN_ENGINE_VERSION};
    r.engine_api.identity = &g_identity;
    r.engine_api.gate_stats = &g_gate_stats;
    r.caps_api.header = {sizeof r.caps_api, KILN_CAPS_VERSION};
    r.caps_api.find = &c_find;

    g_host = &r.host;
    r.root = fs::temp_directory_path() / ("gate-priority-" + sc.name + "-" + std::to_string(
#ifdef _WIN32
                                                                                 GetCurrentProcessId()
#else
                                                                                 ::getpid()
#endif
                                                                                     ));
    fs::remove_all(r.root);
    fs::create_directories(r.root / "data" / "gate-priority-test");
    fs::create_directories(r.root / "logs");
    const auto u8 = [](const fs::path& p) {
        const std::u8string s = p.u8string();
        return std::string(s.begin(), s.end());
    };
    r.host.data_dir = u8(r.root / "data");
    r.host.logs_dir = u8(r.root / "logs");
    if (sc.run_file != "<none>")
        std::ofstream(r.root / "data" / "gate-priority-test" / "run.txt", std::ios::binary) << sc.run_file;
    r.host.services[KILN_WORLD_SERVICE] = &r.world_api;
    r.host.services[KILN_TASKS_SERVICE] = &r.tasks_api;
    r.host.services[KILN_SAVES_SERVICE] = &r.saves_api;
    r.host.services[KILN_PLAYERS_SERVICE] = &r.players_api;
    r.host.services[KILN_TYPES_SERVICE] = &r.types_api;
    r.host.services[KILN_ENGINE_SERVICE] = &r.engine_api;
    r.host.services[KILN_CAPS_SERVICE] = &r.caps_api;
    r.api.size = sizeof r.api;
    r.api.abi_version = KILN_ABI_VERSION;
    r.api.version = &h_version;
    r.api.log = &h_log;
    r.api.path = &h_path;
    r.api.service = &h_service;
    r.api.subscribe_tick = &h_subscribe_tick;
    r.api.subscribe_world = &h_subscribe_world;
    r.api.side = &h_side;
    r.api.on_world_thread = &h_on_world_thread;
    return true;
}

inline void world_event(Rig& r, uint32_t event) {
    KilnWorldEvent ev{};
    ev.size = sizeof ev;
    ev.event = event;
    ev.kind = KILN_WORLD_SERVER;
    ev.session = 1;
    ev.world = r.worlds.world.load();
    for (auto& [fn, user] : r.host.world_events) fn(&ev, user);
}

inline void scene_shared(Rig& r, int delta) {
#ifdef _WIN32
    if (delta > 0) InterlockedIncrement(reinterpret_cast<volatile LONG*>(r.engine->scene_word()));
    else InterlockedDecrement(reinterpret_cast<volatile LONG*>(r.engine->scene_word()));
#else
    __atomic_fetch_add(fake::word(r.engine->scene_word()), delta, __ATOMIC_SEQ_CST);
#endif
}
// The engine's update takes the Scene word Shared; a Kiln holder has it exclusively between frames (the update waits).
inline void frame_begin(Rig& r) {
    for (;;) {
        int32_t v = __atomic_load_n(fake::word(r.engine->scene_word()), __ATOMIC_SEQ_CST);
        if (v >= 0 && __atomic_compare_exchange_n(fake::word(r.engine->scene_word()), &v, v + 1, false, __ATOMIC_SEQ_CST,
                                                  __ATOMIC_SEQ_CST))
            return;
        std::this_thread::yield();  // the GameServer thread spins on the word while a Kiln holder has it
    }
}

// Runs the server: STARTED, frames of about 1 ms with ~3 ms between them for sc.seconds, then STOPPING.
inline void serve(Rig& r, const Scenario& sc) {
    world_event(r, KILN_WORLD_STARTED);
    const auto begin = std::chrono::steady_clock::now();
    const auto stop_at = sc.stop_after_ms >= 0 ? begin + std::chrono::milliseconds(sc.stop_after_ms)
                                               : begin + std::chrono::milliseconds(static_cast<int>(sc.seconds * 1000));
    std::thread server([&] {
        t_server_thread = true;
        uint64_t frame = 0;
        while (std::chrono::steady_clock::now() < stop_at) {
            frame_begin(r);
            const uint64_t step = r.worlds.steps.fetch_add(1) + 1;
            KilnTickInfo info{};
            info.size = sizeof info;
            info.world_kind = KILN_WORLD_SERVER;
            info.step = step;
            kiln::engine::gate_set_server_step(step);
            r.writer->drain();  // kiln.world's own Pre: the committed-write events
            for (auto& [fn, user] : r.host.pre) fn(&info, user);
            if (sc.game_writes_every && ++frame % sc.game_writes_every == 0) {
                // the game changes a real field of the target inside its frame (byte 0: a field, never padding)
                const uintptr_t at = component_at(r, sc.game_target);
                fake::put<uint8_t>(at, static_cast<uint8_t>(fake::get<uint8_t>(at) + 1));
                r.game_changes.fetch_add(1);
            }
            std::this_thread::sleep_for(std::chrono::microseconds(800));
            for (auto& [fn, user] : r.host.post) fn(&info, user);
            scene_shared(r, -1);
            std::this_thread::sleep_for(std::chrono::microseconds(3000));
        }
        // the world stops inside a frame, as the game's does: the gate closes, then the services hear STOPPING
        frame_begin(r);
        kiln::engine::gate_close_session(true, 20'000);
        world_event(r, KILN_WORLD_STOPPING);
        scene_shared(r, -1);
        t_server_thread = false;
    });
    server.join();
}

// ---- reading what the mod wrote ----
inline fs::path run_dir(Rig& r) {
    std::error_code ec;
    const fs::path base = r.root / "logs" / "gate-priority-test";
    if (!fs::exists(base, ec)) return {};
    for (const auto& e : fs::directory_iterator(base, ec))
        if (e.is_directory() && e.path().filename().string().rfind("run-", 0) == 0) return e.path();
    return {};
}
inline std::vector<std::vector<std::string>> csv(const fs::path& file, std::string* header) {
    std::ifstream in(file);
    std::vector<std::vector<std::string>> rows;
    std::string line;
    bool columns = false;
    while (std::getline(in, line)) {
        if (!line.empty() && line[0] == '#') {
            if (header) *header = line;
            continue;
        }
        if (!columns) {
            columns = true;
            continue;
        }
        std::vector<std::string> cells;
        std::stringstream ss(line);
        std::string cell;
        while (std::getline(ss, cell, ',')) cells.push_back(cell);
        rows.push_back(cells);
    }
    return rows;
}

}  // namespace harness
