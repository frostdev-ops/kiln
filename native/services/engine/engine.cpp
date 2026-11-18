// SPDX-License-Identifier: MIT
// kiln.engine: exact-build engine profile selection (public kiln.engine v1, data only) and the Kiln-internal
// primitive table kiln.internal.engine. Portable: the Windows facts of the loaded image come from facts_win.cpp.
#include <atomic>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>

#include "engine_state.h"
#include "gate.h"
#include "identity.h"
#include "include/kiln_engine_native.h"
#include "memory.h"
#include "profiles.g.h"
#include "resolve.h"
#include "services/kiln_caps.h"

namespace kiln::engine {

Stats& stats() {
    static Stats s;
    return s;
}

namespace {
struct State {
    const KilnApi* api = nullptr;
    KilnOwner owner = 0;
    ImageFacts facts;
    Selection selection;
};
State* g_state = nullptr;  // process lifetime: other services keep pointers into the selected profile

KilnStr str(const char* s) { return {s, std::strlen(s)}; }

void log(int32_t level, const std::string& text) {
    if (g_state && g_state->api && g_state->api->log) g_state->api->log(g_state->owner, level, {text.data(), text.size()});
}

void report(const KilnCapsApi* caps, const char* name, bool available, const std::string& text) {
    if (!caps || !caps->report) return;
    caps->report(g_state->owner, str(name), available ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE,
                 available ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_SYMBOL_MISSING, {text.data(), text.size()});
}

KilnResult KILN_CALL identity(KilnEngineIdentity* out) {
    if (!out || out->size < sizeof(KilnEngineIdentity)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_state) return KILN_ERR_UNAVAILABLE;
    describe(g_state->facts, g_state->selection, *out);
    return KILN_OK;
}

KilnResult KILN_CALL gate_stats(KilnEngineGateStats* out) {
    if (!out || out->size < sizeof(KilnEngineGateStats)) return KILN_ERR_INVALID_ARGUMENT;
    KilnEngineGateStats s{};
    s.size = sizeof s;
    const Stats& c = stats();
    s.attempts = c.attempts.load(std::memory_order_relaxed);
    s.acquired = c.acquired.load(std::memory_order_relaxed);
    s.busy_registry = c.busy_registry.load(std::memory_order_relaxed);
    s.busy_scene = c.busy_scene.load(std::memory_order_relaxed);
    s.refused_in_frame = c.refused_in_frame.load(std::memory_order_relaxed);
    s.refused_world_thread = c.refused_world_thread.load(std::memory_order_relaxed);
    s.refused_other = c.refused_other.load(std::memory_order_relaxed);
    s.hold_ns_total = c.hold_ns_total.load(std::memory_order_relaxed);
    s.hold_ns_max = c.hold_ns_max.load(std::memory_order_relaxed);
    s.views = c.views.load(std::memory_order_relaxed);
    s.view_failures = c.view_failures.load(std::memory_order_relaxed);
    *out = s;
    return KILN_OK;
}

const Layout* KILN_CALL layout() {
    return g_state && g_state->selection.profile ? &g_state->selection.profile->layout : nullptr;
}

const Profile* KILN_CALL profile() { return g_state ? g_state->selection.profile : nullptr; }

// Read primitives (kiln_engine_native.h): any thread, no allocation, no host lock, no wait.
KilnResult KILN_CALL copy(std::uintptr_t address, void* out, std::size_t bytes) {
    if (!bytes) return KILN_OK;
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    return mem::read_span(address, out, bytes) ? KILN_OK : KILN_ERR_UNAVAILABLE;
}

KilnResult KILN_CALL view(std::uint32_t kind, EcsView* out, std::uint32_t* reason) {
    if (!out || !reason || kind > static_cast<std::uint32_t>(ViewKind::Client)) return KILN_ERR_INVALID_ARGUMENT;
    stats().views.fetch_add(1, std::memory_order_relaxed);
    EcsView v{};
    const Reason r = ecs_view(layout(), static_cast<ViewKind>(kind), v, mem::image_base());
    *reason = static_cast<std::uint32_t>(r);
    if (r != Reason::None) {
        stats().view_failures.fetch_add(1, std::memory_order_relaxed);
        return KILN_ERR_UNAVAILABLE;
    }
    *out = v;
    return KILN_OK;
}

std::int32_t KILN_CALL write_supported(const EcsView* expected, std::uint16_t component) {
    return expected && ecs_write_supported(layout(), *expected, component, mem::image_base()) ? 1 : 0;
}

// The engine's original write gate. The caller's thread comes from the core before any word is touched; a core
// without on_world_thread cannot prove the caller is off the world threads, so the gate refuses (fail closed).
CallerThread caller_thread() {
    const KilnApi* api = g_state ? g_state->api : nullptr;
    if (!api || api->size < offsetof(KilnApi, on_world_thread) + sizeof api->on_world_thread || !api->on_world_thread)
        return CallerThread::Unknown;
    if (api->on_world_thread(KILN_WORLD_SERVER)) return CallerThread::ServerWorld;
    if (api->on_world_thread(KILN_WORLD_CLIENT) || api->on_world_thread(KILN_WORLD_PREDICTION)) return CallerThread::ClientWorld;
    return CallerThread::Other;
}
bool gate_record(const EcsGate* gate) { return gate && gate->size == sizeof(EcsGate); }

KilnResult KILN_CALL gate_acquire_member(const EcsView* expected, EcsGate* gate, std::uint32_t* reason) {
    if (!expected || !gate_record(gate) || !reason || !gate->owner || !gate->step ||
        gate->admission > static_cast<std::uint32_t>(GateAdmission::RetryKilnHolder))
        return KILN_ERR_INVALID_ARGUMENT;
    const Reason r = gate_acquire(layout(), *expected, *gate, caller_thread(), mem::image_base());
    *reason = static_cast<std::uint32_t>(r);
    return r == Reason::None ? KILN_OK : KILN_ERR_UNAVAILABLE;
}
std::int32_t KILN_CALL gate_validate_member(const EcsGate* gate) {
    return gate_record(gate) && gate_validate(layout(), *gate, mem::image_base()) ? 1 : 0;
}
std::int32_t KILN_CALL gate_component_member(const EcsGate* gate, std::uint16_t component) {
    return gate_record(gate) && gate_component_supported(*gate, component) ? 1 : 0;
}
std::int32_t KILN_CALL gate_track_member(const EcsGate* gate, std::uintptr_t entity, std::uint16_t component) {
    return gate_record(gate) && gate_track_change(*gate, entity, component) ? 1 : 0;
}
KilnResult KILN_CALL gate_write_member(const EcsGate* gate, std::uintptr_t address, const void* data, std::size_t bytes) {
    if (!gate_record(gate) || !gate->held || (!data && bytes)) return KILN_ERR_INVALID_ARGUMENT;
    switch (gate_write(*gate, address, data, bytes)) {
        case GateWrite::Ok: return KILN_OK;
        case GateWrite::TooLarge: return KILN_ERR_BUFFER_TOO_SMALL;  // over gate.maximum_bytes, no mutation
        default: return KILN_ERR_UNAVAILABLE;                        // faulted: a partial write may have happened
    }
}
void KILN_CALL gate_release_member(EcsGate* gate) {
    if (gate_record(gate)) gate_release(*gate);
}

// The gate closes while the server world is stopping (a later acquire would only ever see a dying world) and opens at
// the next server world's STARTED; at process exit it closes for good. Both then wait a few microseconds for a Kiln
// holder already in flight and say so when one is left. The core dispatches STOPPING from the world-destroy hook on the
// destroying thread; that no Kiln holder can be inside then rests on the engine holding the Scene word Shared through
// teardown, which the bounded look below would show if it is wrong.
constexpr std::uint64_t kExitHolderWaitNs = 20'000;
void KILN_CALL world_event(const KilnWorldEvent* event, void*) {
    if (!event || event->kind != KILN_WORLD_SERVER) return;
    if (event->event == KILN_WORLD_STOPPING && !gate_close_session(true, kExitHolderWaitNs))
        log(KILN_LOG_WARN, "kiln.engine: a Kiln thread held the engine's write gate at a stopping server world "
                           "(gave up waiting after 20 us)");
    if (event->event == KILN_WORLD_STARTED) gate_close_session(false);
}
// The live server step for the busy latch (until the next server step edge), from the server Pre.
void KILN_CALL server_pre(const KilnTickInfo* tick, void*) {
    if (tick) gate_set_server_step(tick->step);
}
void KILN_CALL on_exit(void*) {
    if (!gate_close_for_exit(kExitHolderWaitNs))
        log(KILN_LOG_WARN, "kiln.engine: a Kiln thread still held the engine's write gate at exit (gave up after 20 us)");
}

// The measurement switch for the write gate's priority raise: <kiln data>/kiln.engine/gate-priority-raise. Read once,
// at boot, before any service can hold a gate. Only the content "off" (surrounding whitespace and a UTF-8 byte-order
// mark trimmed) turns the raise off, for the whole process. No file leaves it on and says nothing; any other content, or
// a file that cannot be read, leaves it on with a warning. For measuring what the raise buys (docs/kiln/priority-kit.md),
// not for play.
constexpr const char* kRaiseSwitch = "kiln/data/kiln.engine/gate-priority-raise";
enum class RaiseSwitch { Absent, Off, Other, Unreadable };
RaiseSwitch read_raise_switch(const KilnApi* api) {
    if (!api || api->size < offsetof(KilnApi, path) + sizeof api->path || !api->path) return RaiseSwitch::Absent;
    char buf[1024];
    std::size_t len = 0;
    if (api->path(KILN_PATH_DATA, buf, sizeof buf, &len) != KILN_OK || len > sizeof buf) return RaiseSwitch::Absent;
    try {
        const std::filesystem::path file = std::filesystem::path(std::u8string(reinterpret_cast<const char8_t*>(buf), len)) /
                                           "kiln.engine" / "gate-priority-raise";
        std::error_code ec;
        const auto status = std::filesystem::status(file, ec);
        if (status.type() == std::filesystem::file_type::not_found) return RaiseSwitch::Absent;
        if (ec || !std::filesystem::is_regular_file(status)) return RaiseSwitch::Unreadable;
        std::ifstream in(file, std::ios::binary);
        if (!in) return RaiseSwitch::Unreadable;
        char text[64];
        in.read(text, sizeof text);
        if (in.bad()) return RaiseSwitch::Unreadable;
        std::string content(text, static_cast<std::size_t>(in.gcount()));
        if (content.size() == sizeof text) return RaiseSwitch::Other;  // longer than any "off" with whitespace around it
        if (content.rfind("\xEF\xBB\xBF", 0) == 0) content.erase(0, 3);
        const auto first = content.find_first_not_of(" \t\r\n"), last = content.find_last_not_of(" \t\r\n");
        content = first == std::string::npos ? std::string() : content.substr(first, last - first + 1);
        return content == "off" ? RaiseSwitch::Off : RaiseSwitch::Other;
    } catch (...) {
        return RaiseSwitch::Unreadable;
    }
}
void apply_raise_switch(const KilnApi* api, const KilnCapsApi* caps) {
    const RaiseSwitch sw = read_raise_switch(api);
    gate_set_priority_raise(sw != RaiseSwitch::Off);
    std::string text;
    if (sw == RaiseSwitch::Off) {
        text = std::string("off for this run by the measurement switch ") + kRaiseSwitch +
               " (for measurements only, not for play; delete the file to turn it back on)";
        log(KILN_LOG_WARN, "kiln.engine: the write gate's priority raise is " + text);
    } else if (sw == RaiseSwitch::Other) {
        text = std::string("on (") + kRaiseSwitch + " holds something other than \"off\" and was ignored)";
        log(KILN_LOG_WARN, std::string("kiln.engine: ") + kRaiseSwitch +
                               " holds something other than \"off\"; the write gate's priority raise stays on");
    } else if (sw == RaiseSwitch::Unreadable) {
        text = std::string("on (") + kRaiseSwitch + " could not be read and was ignored)";
        log(KILN_LOG_WARN, std::string("kiln.engine: ") + kRaiseSwitch +
                               " could not be read; the write gate's priority raise stays on");
    }
    if (caps)
        caps->report(g_state->owner, str("engine.gate_priority_raise"),
                     sw == RaiseSwitch::Off ? KILN_CAP_UNAVAILABLE : KILN_CAP_AVAILABLE,
                     sw == RaiseSwitch::Off ? KILN_CAP_REASON_POLICY : KILN_CAP_REASON_NONE, {text.data(), text.size()});
}

const KilnEngineApi g_table = {{sizeof(KilnEngineApi), KILN_ENGINE_VERSION}, &identity, &gate_stats};
const KilnEngineNativeApi g_native = {{sizeof(KilnEngineNativeApi), KILN_ENGINE_NATIVE_VERSION},
                                      sizeof(Layout),
                                      sizeof(Profile),
                                      &layout,
                                      &copy,
                                      &view,
                                      &write_supported,
                                      &profile,
                                      &gate_acquire_member,
                                      &gate_validate_member,
                                      &gate_component_member,
                                      &gate_track_member,
                                      &gate_write_member,
                                      &gate_release_member};
}  // namespace

const Layout* selected_layout() { return layout(); }

void reset_for_tests() {
    delete g_state;
    g_state = nullptr;
    gate_reset_for_tests();
}

}  // namespace kiln::engine

using namespace kiln::engine;

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    try {
        if (!api || !info || info->size < sizeof(KilnPluginInfo) || api->abi_version != KILN_ABI_VERSION ||
            api->size < offsetof(KilnApi, register_service) + sizeof(api->register_service) || !api->service ||
            !api->register_service || !api->log || !api->build_info)
            return KILN_ERR_UNAVAILABLE;
        if (g_state) return KILN_ERR_INVALID_ARGUMENT;
        g_state = new State;
        g_state->api = api;
        g_state->owner = owner;
        g_state->facts = gather_facts(api);
        g_state->selection = select(g_state->facts, generated::kProfiles,
                                    sizeof generated::kProfiles / sizeof generated::kProfiles[0]);
        const Profile* p = g_state->selection.profile;
        char line[512];
        if (p) {
            std::snprintf(line, sizeof line,
                          "kiln.engine: exact engine profile %s for %s (sha256 %s, TimeDateStamp %u, SizeOfImage %u, KFC4)",
                          p->id, p->target, p->sha256, p->timestamp, p->size_of_image);
            log(KILN_LOG_INFO, line);
        } else {
            std::snprintf(line, sizeof line,
                          "kiln.engine: %s for %s (sha256 %s, TimeDateStamp %u, SizeOfImage %u, KFC%u): engine-reading members "
                          "are unavailable on this build",
                          g_state->selection.why, g_state->facts.target.c_str(),
                          g_state->facts.sha256.empty() ? "-" : g_state->facts.sha256.c_str(), g_state->facts.timestamp,
                          g_state->facts.size_of_image, g_state->facts.archive_format);
            log(KILN_LOG_WARN, line);
        }
        const auto* caps = static_cast<const KilnCapsApi*>(api->service(str(KILN_CAPS_SERVICE), 1));
        if (caps && (caps->header.size < offsetof(KilnCapsApi, report) + sizeof caps->report || !caps->report)) caps = nullptr;
        const std::string unknown = p ? std::string() : std::string(g_state->selection.why);
        const bool read = p && (p->sections & KILN_ENGINE_SECTION_OWNERS) && (p->sections & KILN_ENGINE_SECTION_ECS_READ);
        const bool write = read && (p->sections & KILN_ENGINE_SECTION_ECS_WRITE);
        report(caps, "engine.profile", p != nullptr, p ? p->id : unknown);
        report(caps, "engine.ecs_read", read, read ? "" : p ? "the profile has no validated ECS read layout" : unknown);
        report(caps, "engine.ecs_write", write,
               write ? "" : p ? "the profile has no enabled native-thread Scene write gate" : unknown);
        apply_raise_switch(api, caps);
        info->id = str(KILN_ENGINE_SERVICE);
        info->version = str("1");
        info->on_unload = &on_exit;
        KilnHandle events{};
        if (api->size < offsetof(KilnApi, subscribe_world) + sizeof api->subscribe_world || !api->subscribe_world ||
            api->subscribe_world(owner, &world_event, nullptr, &events) != KILN_OK)
            log(KILN_LOG_INFO, "kiln.engine: no world lifecycle events here; the write gate is not closed at a stopping world "
                               "(a later acquire still refuses a dying world's view)");
        KilnHandle tick{};
        if (!api->subscribe_tick || api->subscribe_tick(owner, KILN_TICK_PRE, &server_pre, nullptr, &tick) != KILN_OK)
            log(KILN_LOG_INFO, "kiln.engine: no server step here; the write gate's busy latch keys on the caller's step");
        const KilnResult rc = api->register_service(owner, str(KILN_ENGINE_SERVICE), &g_table);
        if (rc != KILN_OK) return rc;
        if (api->register_service(owner, str(KILN_ENGINE_NATIVE_SERVICE), &g_native) != KILN_OK)
            log(KILN_LOG_ERROR,
                "kiln.engine: the core refused kiln.internal.engine (kiln-engine.dll must be loaded from kiln/services); "
                "Kiln services that read engine memory report themselves unavailable");
        return KILN_OK;
    } catch (...) {
        return KILN_ERR_INTERNAL;
    }
}
