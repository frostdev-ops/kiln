// SPDX-License-Identifier: MIT
// The shared harness of kiln.world's engine-backend host tests (ecs_engine_test.cpp, ecs_writes_test.cpp): a fake core
// for the real kiln-engine entry (booted through kiln_plugin_main), kiln.internal.engine's copy behind a test hook,
// and Kiln's own world facts scripted per kind. Include once per test program (it defines kiln::engine::gather_facts).
#pragma once
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <functional>
#include <map>
#include <string>
#include <thread>
#include <vector>

#include "engine_state.h"
#include "fake_engine.h"
#include "include/kiln_engine_native.h"
#include "profiles.g.h"
#include "services/kiln_caps.h"

#include "../ecs_engine.h"
#include "../ecs_epochs.h"
#include "../transform_layout.h"


extern "C" KilnResult KILN_CALL kiln_plugin_main(const KilnApi*, KilnOwner, KilnPluginInfo*);
namespace kiln::engine::mem {
extern std::atomic<std::uintptr_t> test_image;
}

namespace {
using namespace kiln::engine;
using namespace kw::ecs;
using kw::ecs::engine::OwnResolution;
using kw::ecs::engine::Reader;
using kw::ecs::engine::Source;
using kw::ecs::engine::WorldFact;

int checks = 0, failures = 0;
std::string g_case;
void check(bool ok, const std::string& what) {
    ++checks;
    if (!ok) { ++failures; std::fprintf(stderr, "FAIL [%s] %s\n", g_case.c_str(), what.c_str()); }
}
[[maybe_unused]] bool all_eq(const std::vector<uint8_t>& v, uint8_t b) { for (auto x : v) if (x != b) return false; return true; }

// ---- a fake core for the real kiln-engine entry (as native/services/engine/tests/plugin_host_test.cpp) ----
// Calls into the fake core while the calling thread holds an engine write gate (ecs_writes_test.cpp sets g_in_hold).
thread_local bool g_in_hold = false;
std::atomic<long> g_core_calls_in_hold{0};
void core_call() { if (g_in_hold) g_core_calls_in_hold.fetch_add(1); }
ImageFacts g_facts;
std::map<std::string, const void*> g_tables;
KilnCapsApi g_caps{};
KilnResult KILN_CALL cap_report(KilnOwner, KilnStr, uint32_t, uint32_t, KilnStr) { return KILN_OK; }
const void* KILN_CALL lookup(KilnStr n, uint32_t v) {
    core_call();
    const std::string name(n.ptr, n.len);
    if (name == KILN_CAPS_SERVICE) return &g_caps;
    const auto at = g_tables.find(name);
    if (at == g_tables.end()) return nullptr;
    return static_cast<const KilnServiceHeader*>(at->second)->version >= v ? at->second : nullptr;
}
KilnResult KILN_CALL publish(KilnOwner, KilnStr n, const void* t) {
    core_call();
    g_tables[std::string(n.ptr, n.len)] = t;
    return KILN_OK;
}
void KILN_CALL logger(KilnOwner, int32_t, KilnStr) { core_call(); }
KilnResult KILN_CALL build(KilnBuildInfo* out) { core_call(); out->side = KILN_SIDE_CLIENT; return KILN_OK; }
// The calling thread runs the server world's frame (a tick handler): the gate refuses it before any engine word.
thread_local bool g_on_server_thread = false;
bool KILN_CALL on_world(uint32_t kind) { core_call(); return kind == KILN_WORLD_SERVER && g_on_server_thread; }
KilnResult KILN_CALL subscribe_world(KilnOwner, KilnWorldEventFn, void*, KilnHandle* out) { core_call(); *out = 0; return KILN_OK; }
KilnResult KILN_CALL subscribe_tick(KilnOwner, int32_t, KilnTickFn, void*, KilnHandle* out) { core_call(); *out = 0; return KILN_OK; }
KilnApi make_api() {
    KilnApi api{};
    api.size = sizeof api; api.abi_version = KILN_ABI_VERSION;
    api.service = &lookup; api.register_service = &publish; api.log = &logger; api.build_info = &build;
    api.on_world_thread = &on_world; api.subscribe_world = &subscribe_world; api.subscribe_tick = &subscribe_tick;
    return api;
}
const KilnEngineNativeApi* boot_engine(const Profile& p, KilnApi& api) {
    reset_for_tests();
    g_tables.clear();
    g_facts = {};
    g_facts.headers = true; g_facts.target = p.target; g_facts.sha256 = p.sha256; g_facts.timestamp = p.timestamp;
    g_facts.size_of_image = p.size_of_image; g_facts.archive_format = 4;
    g_facts.side = std::string(p.target) == "enshrouded.exe" ? KILN_SIDE_CLIENT : KILN_SIDE_SERVER;
    KilnPluginInfo info{}; info.size = sizeof info;
    if (kiln_plugin_main(&api, 7, &info) != KILN_OK) return nullptr;
    char why[160]{};
    return kiln_engine_native(&api, 7, sizeof(KilnEngineNativeApi), why, sizeof why);
}

}  // namespace
#include "ecs_fake_world.h"
namespace {
using namespace fakeworld;
// kiln.internal.engine's copy with a test hook in front: called with each address before the real copy reads it.
const KilnEngineNativeApi* g_real = nullptr;
std::function<void(uintptr_t)> g_copy_hook;
KilnResult KILN_CALL hooked_copy(std::uintptr_t address, void* out, std::size_t bytes) {
    if (g_copy_hook) g_copy_hook(address);
    return g_real->copy(address, out, bytes);
}

// Kiln's own world facts as ecs_prod.cpp samples them, scripted: the live world per kind and its session.
struct Worlds {
    std::atomic<uintptr_t> world[KILN_WORLD_KINDS]{};
    std::atomic<uint64_t> session[KILN_WORLD_KINDS]{};
    std::atomic<uint64_t> steps[KILN_WORLD_KINDS]{};
    std::atomic<bool> step_each_read{false};  // every look at the counter finds a step that ran since the last one
    std::atomic<int> release_after{-1};         // after this many more world samples, the Scene word is let go
    std::atomic<std::int32_t>* word = nullptr;
    OwnerEpochs epochs;
};
bool steps_of(void* u, uint32_t kind, uint64_t* out) {
    auto& w = *static_cast<Worlds*>(u);
    if (kind >= KILN_WORLD_KINDS) return false;
    *out = w.step_each_read.load() ? w.steps[kind].fetch_add(1) + 1 : w.steps[kind].load();
    return true;
}
uint32_t world_fact(void* u, uint32_t kind, WorldFact* out) {
    auto& w = *static_cast<Worlds*>(u);
    if (kind >= KILN_WORLD_KINDS) return KILN_ECS_UNAVAILABLE;
    if (w.release_after.load() >= 0 && w.release_after.fetch_sub(1) == 0 && w.word) w.word->store(0);
    out->world = w.world[kind].load();
    out->session = w.session[kind].load();
    return KILN_ECS_OK;
}
uint64_t epoch_of(void* u, KilnOwner o) { return static_cast<Worlds*>(u)->epochs.current(o); }
uint64_t open_epoch(void* u, KilnOwner o, uint64_t e) { return static_cast<Worlds*>(u)->epochs.open(o, e); }

Source source_of(const KilnEngineNativeApi* native, Worlds& w) {
    Source s;
    s.engine = native;
    s.layout = native ? native->layout() : nullptr;
    s.exact_profile = s.layout != nullptr;
    s.reads = s.layout && !kw::ecs::engine::layout_refusal(s.layout);
    s.user = &w;
    s.world = &world_fact;
    s.epoch = &epoch_of;
    s.open_epoch = &open_epoch;
    s.steps = &steps_of;
    return s;
}

}  // namespace

namespace kiln::engine {
ImageFacts gather_facts(const KilnApi*) { return g_facts; }
}  // namespace kiln::engine
