// SPDX-License-Identifier: MIT
// kiln.time: time of day on the server world, plus a weather re-roll.
//
// Game objects (docs/re/time.md):
//  - g38_daytime::Daytime (0x70 B, one per world; the server's is authoritative). It stores an anchor {ecsTime,
//    gameTime, timeScale} and a change counter; the time of day is derived from game time once per sim step by the
//    server_update_daytime program. Kiln captures the object when the game calls setTimeOfDay on it, which it does
//    while creating the server game (07:00) and again when it loads the savegame (the saved time).
//  - The GameServer's ServerInfo sender copies the anchor into ServerInfoMessage.daytimeSync whenever the counter
//    differs from the value it sent last (GameServer+0x17C4): that is how a change reaches clients. Kiln hooks the
//    sender only to observe that.
//  - WeatherSystem (server, mode 0): layers {nextPhenomenonTime, currentPhenomenon, currentInstance, previousInstance}.
//    Kiln captures it from WeatherSystem_update, which the GameServer frame calls after the sim step.
// Everything that writes runs on the tick thread (Pre or Post): the Daytime readers and the weather update run in
// the same thread's frame, before (programs inside the step) or after (weather) Kiln's handlers, never concurrently.
#include <windows.h>
#include <intrin.h>

#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "kiln_api.h"
#include "safetyhook.hpp"
#include "services/kiln_time.h"

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uintptr_t g_base = 0;

constexpr double kNsPerHour = 3.6e12;
constexpr int64_t kDayNs = 86'400'000'000'000;

struct DaytimeOffsets {
    int64_t anchor_ecs = -1, anchor_game = -1, counter = -1, tod = -1, day_start = -1, night_start = -1,
            day_length = -1, night_length = -1, scale = -1, is_day = -1;
} D;
struct WeatherOffsets {
    int64_t mode = -1, layers = -1, count = -1, stride = -1, cur = -1, prev = -1;
} W;
int64_t g_gs_synced = -1, g_gs_server_game = -1, g_sg_daytime = -1;

using TodToGameFn = int64_t(__fastcall*)(void* daytime, int64_t tod);
using GetGameTimeFn = int64_t(__fastcall*)(void* daytime, int64_t ecs_now);
using GameToTodFn = int64_t(__fastcall*)(void* daytime, int64_t game_time);
using RemoveInstanceFn = void(__fastcall*)(void* ws, uint32_t handle);
using SetScaleFn = void(__fastcall*)(void* daytime, float scale, int64_t ecs_now);
TodToGameFn g_tod_to_game = nullptr;
GetGameTimeFn g_get_game_time = nullptr;
GameToTodFn g_game_to_tod = nullptr;
RemoveInstanceFn g_remove_instance = nullptr;

SafetyHookInline g_set_hook, g_weather_hook, g_info_hook, g_scale_hook;
bool g_can_set = false, g_can_weather = false, g_can_probe = false, g_can_scale = false;
// The clock speed Kiln holds (time/SetTimeScale, docs/re/time.md "The clock's speed"); negative: the game decides
// (1, or faster while players sleep). Session state, applied to the server world's Daytime only.
std::atomic<float> g_scale_hold{-1.0f};
std::atomic<uint64_t> g_scale_refused{0};  // the game's own speed changes refused while Kiln holds the speed
bool g_scale_logged_refusal = false;
std::atomic<uint8_t*> g_daytime{nullptr};
std::atomic<uint8_t*> g_ws{nullptr};
std::atomic<uint8_t*> g_gs{nullptr};
std::atomic<uint32_t> g_synced{UINT32_MAX};
std::atomic<int64_t> g_pending_sync{-1};  // change counter Kiln set and wants to see in a ServerInfo message
std::atomic<int64_t> g_pending_since{0};  // GetTickCount64 when it was set
std::atomic<bool> g_reset_pending{false};  // log what the next weather update rolled
bool g_logged_state = false, g_logged_owner = false;
// The server world's own objects (its singletons), once a server world runs. A game client also runs a client world
// with a Daytime and a WeatherSystem of its own: only the server world's are kiln.time's. Bound at the first Pre of
// each server-world session (KilnApi.world + singleton), cleared when the game closes the world.
std::atomic<uint8_t*> g_server_daytime{nullptr}, g_server_ws{nullptr};
std::atomic<bool> g_need_bind{true};
bool g_ignored_logged = false;

void logf(int32_t level, const char* fmt, ...) {
    char buf[768];
    va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (static_cast<size_t>(n) >= sizeof buf) n = sizeof buf - 1;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

template <typename T>
T& at(uint8_t* p, int64_t off) {
    return *reinterpret_cast<T*>(p + off);
}

// ---- guarded game calls (no C++ objects with destructors in these functions, as __try requires) ----
bool g_call_game_time(uint8_t* d, int64_t now, int64_t* out) {
    __try {
        *out = g_get_game_time(d, now);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_call_game_to_tod(uint8_t* d, int64_t game, int64_t* out) {
    __try {
        *out = g_game_to_tod(d, game);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_call_tod_to_game(uint8_t* d, int64_t tod, int64_t* out) {
    __try {
        *out = g_tod_to_game(d, tod);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_call_remove(uint8_t* ws, uint32_t handle) {
    __try {
        g_remove_instance(ws, handle);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_call_set_scale(uint8_t* d, float scale, int64_t now) {
    __try {
        g_scale_hook.call<void>(d, scale, now);  // the original, past Kiln's hook
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}
bool g_read_u64(const void* p, uint64_t* out) {
    __try {
        *out = *static_cast<const volatile uint64_t*>(p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

int64_t ecs_now() {
    KilnTickInfo t{};
    if (g_api->tick_info(&t) != KILN_OK || t.clock == 0) return 0;
    return t.clock_period * static_cast<int64_t>(t.clock);
}

double hours_of(int64_t ns) { return static_cast<double>(ns) / kNsPerHour; }

// ---- hooks ----
uintptr_t __fastcall hk_set_time_of_day(uint8_t* daytime, int64_t tod, int64_t ecs_time) {
    const uintptr_t ret = reinterpret_cast<uintptr_t>(_ReturnAddress());
    const uintptr_t r = g_set_hook.call<uintptr_t>(daytime, tod, ecs_time);
    uint8_t* server = g_server_daytime.load();
    if (server && daytime != server) {  // another world's Daytime (a client's own world): not kiln.time's
        if (!g_ignored_logged) {
            g_ignored_logged = true;
            logf(KILN_LOG_INFO, "time: setTimeOfDay(%.4f h) on Daytime %p from rva 0x%llx is not the server world's "
                 "(%p); ignored (a client's own world)", hours_of(tod), static_cast<void*>(daytime),
                 static_cast<unsigned long long>(ret - g_base), static_cast<void*>(server));
        }
        return r;
    }
    uint8_t* previous = g_daytime.exchange(daytime);
    logf(KILN_LOG_INFO, "time: game setTimeOfDay(%.4f h, ecs %lld) from rva 0x%llx; counter %u%s", hours_of(tod),
         static_cast<long long>(ecs_time), static_cast<unsigned long long>(ret - g_base),
         at<uint32_t>(daytime, D.counter), previous == daytime ? "" : " (Daytime object captured)");
    return r;
}

// The game's daytime speed setter (re-anchors at now, sets the scale, bumps the change counter). Its one caller is the
// server's sleep program, which asks for the speed the sleepers want whenever the clock's speed differs from it.
// While Kiln holds a speed, that request is refused (the clock already runs at Kiln's speed), so the change counter
// does not move every step.
void __fastcall hk_set_scale(uint8_t* daytime, float scale, int64_t ecs_now) {
    const float hold = g_scale_hold.load(std::memory_order_relaxed);
    if (hold >= 0.0f && daytime == g_daytime.load(std::memory_order_relaxed)) {
        if (at<float>(daytime, D.scale) == hold) {
            g_scale_refused.fetch_add(1, std::memory_order_relaxed);
            if (!g_scale_logged_refusal) {
                g_scale_logged_refusal = true;
                logf(KILN_LOG_INFO, "time: the game asked for clock speed %.2f; Kiln holds %.2f", scale, hold);
            }
            return;
        }
        scale = hold;
    }
    g_scale_hook.call<void>(daytime, scale, ecs_now);
}

uintptr_t __fastcall hk_weather_update(uint8_t* ws, uintptr_t a, uintptr_t b, uintptr_t c) {
    uint8_t* server = g_server_ws.load(std::memory_order_relaxed);
    if (g_ws.load(std::memory_order_relaxed) != ws && at<uint32_t>(ws, W.mode) == 0 && (!server || ws == server)) {
        g_ws.store(ws);
        uint64_t n = 0;
        g_read_u64(ws + W.count, &n);
        logf(KILN_LOG_INFO, "time: server WeatherSystem captured at %p (%llu layers)", static_cast<void*>(ws),
             static_cast<unsigned long long>(n));
    }
    const uintptr_t r = g_weather_hook.call<uintptr_t>(ws, a, b, c);
    if (g_reset_pending.load(std::memory_order_relaxed) && ws == g_ws.load(std::memory_order_relaxed)) {
        g_reset_pending.store(false);
        uint64_t layers_p = 0, n = 0;
        if (g_read_u64(ws + W.layers, &layers_p) && g_read_u64(ws + W.count, &n) && layers_p && n <= 64) {
            uint32_t rolled = 0, active = 0;
            int64_t soonest = INT64_MAX;
            for (uint64_t i = 0; i < n; ++i) {
                uint8_t* L = reinterpret_cast<uint8_t*>(layers_p) + i * static_cast<uint64_t>(W.stride);
                const int64_t next = at<int64_t>(L, 0);
                if (next != 0) ++rolled;
                if (at<uint32_t>(L, W.cur)) ++active;
                if (next > 0 && next < soonest) soonest = next;
            }
            logf(KILN_LOG_INFO, "time: weather update after the reset: %u of %llu layers rolled a new phenomenon "
                 "(%u with a live instance), next change at game time %lld ns", rolled,
                 static_cast<unsigned long long>(n), active, static_cast<long long>(soonest == INT64_MAX ? 0 : soonest));
        }
    }
    return r;
}

uintptr_t __fastcall hk_send_server_info(uint8_t* gs, uintptr_t a, uintptr_t b, uintptr_t c) {
    g_gs.store(gs, std::memory_order_relaxed);
    const uintptr_t r = g_info_hook.call<uintptr_t>(gs, a, b, c);
    const uint32_t sent = at<uint32_t>(gs, g_gs_synced);
    g_synced.store(sent, std::memory_order_relaxed);
    const int64_t want = g_pending_sync.load();
    if (want >= 0 && sent == static_cast<uint32_t>(want)) {
        g_pending_sync.store(-1);
        logf(KILN_LOG_INFO, "time: ServerInfo now carries daytime change %u (sent to clients %llu ms after the set)",
             sent, static_cast<unsigned long long>(GetTickCount64() - g_pending_since.load()));
    }
    if (!g_logged_owner) {
        g_logged_owner = true;
        uint64_t sg = 0, d = 0;
        if (g_read_u64(gs + g_gs_server_game, &sg) && sg && g_read_u64(reinterpret_cast<uint8_t*>(sg) + g_sg_daytime, &d))
            logf(KILN_LOG_INFO, "time: GameServer %p syncs Daytime %p (%s Kiln's), last sent counter %u",
                 static_cast<void*>(gs), reinterpret_cast<void*>(d),
                 reinterpret_cast<uint8_t*>(d) == g_daytime.load() ? "same as" : "DIFFERENT from", sent);
    }
    return r;
}

// ---- table ----
KilnResult KILN_CALL get(KilnTimeState* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    uint8_t* d = g_daytime.load();
    if (!d) return KILN_ERR_UNAVAILABLE;
    KilnTimeState s{};
    s.size = sizeof s;
    const int64_t now = ecs_now();
    int64_t game = 0, tod = 0;
    if (!g_call_game_time(d, now, &game) || !g_call_game_to_tod(d, game, &tod)) return KILN_ERR_INTERNAL;
    s.hours = hours_of(tod);
    s.hours_field = hours_of(at<int64_t>(d, D.tod));
    s.is_day = at<uint8_t>(d, D.is_day);
    s.day_start_hours = hours_of(at<int64_t>(d, D.day_start));
    s.night_start_hours = hours_of(at<int64_t>(d, D.night_start));
    s.day_length_s = static_cast<double>(at<int64_t>(d, D.day_length)) / 1e9;
    s.night_length_s = static_cast<double>(at<int64_t>(d, D.night_length)) / 1e9;
    s.time_scale = at<float>(d, D.scale);
    s.change_counter = at<uint32_t>(d, D.counter);
    s.ecs_time_ns = now;
    s.game_time_ns = game;
    s.anchor_ecs_ns = at<int64_t>(d, D.anchor_ecs);
    s.anchor_game_ns = at<int64_t>(d, D.anchor_game);
    s.synced_counter = g_synced.load();
    uint8_t* ws = g_ws.load();
    s.weather_available = (g_can_weather && ws) ? 1 : 0;
    uint64_t layers = 0;
    if (ws) g_read_u64(ws + W.count, &layers);
    s.weather_layers = static_cast<uint32_t>(layers);
    *out = s;
    return KILN_OK;
}

KilnResult KILN_CALL set_time_of_day(double hours, uint32_t mode) {
    if (!std::isfinite(hours) || mode > KILN_TIME_SET_GAME) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t* d = g_daytime.load();
    if (!d || !g_can_set) return KILN_ERR_UNAVAILABLE;
    double h = std::fmod(hours, 24.0);
    if (h < 0) h += 24.0;
    int64_t tod = std::llround(h * kNsPerHour);
    if (tod >= kDayNs || tod < 0) tod = 0;
    const int64_t now = ecs_now();
    int64_t before = 0;
    if (!g_call_game_time(d, now, &before)) return KILN_ERR_INTERNAL;
    const uint32_t counter_before = at<uint32_t>(d, D.counter);

    bool forward = mode == KILN_TIME_SET_FORWARD;
    const int64_t cycle = at<int64_t>(d, D.day_length) + at<int64_t>(d, D.night_length);
    int64_t target = 0;
    if (forward) {
        int64_t in_cycle = 0;
        if (cycle <= 0 || !g_call_tod_to_game(d, tod, &in_cycle) || in_cycle < 0 || in_cycle >= cycle) {
            logf(KILN_LOG_WARN, "time: forward set impossible (cycle %lld ns, tod->game %lld); using the game's setTimeOfDay",
                 static_cast<long long>(cycle), static_cast<long long>(in_cycle));
            forward = false;
        } else {
            int64_t phase = before % cycle;
            if (phase < 0) phase += cycle;
            target = before - phase + in_cycle;
            if (target < before) target += cycle;
        }
    }
    if (forward) {
        // What setTimeOfDay does, but keeping the game clock monotonic: re-anchor at (now, target) and bump the change
        // counter so the server re-sends the anchor to clients.
        at<int64_t>(d, D.anchor_ecs) = now;
        at<int64_t>(d, D.anchor_game) = target;
        ++at<uint32_t>(d, D.counter);
    } else {
        g_set_hook.call<uintptr_t>(d, tod, now);  // the original, without our logging hook
    }
    const uint32_t counter = at<uint32_t>(d, D.counter);
    g_pending_since.store(static_cast<int64_t>(GetTickCount64()));
    g_pending_sync.store(counter);
    int64_t after = 0, check = 0;
    g_call_game_time(d, now, &after);
    g_call_game_to_tod(d, after, &check);
    logf(KILN_LOG_INFO,
         "time: set %.4f h (%s): was %.4f h, now %.4f h; game time %lld -> %lld ns; counter %u -> %u; ecs %lld",
         h, forward ? "forward" : "game setTimeOfDay", hours_of(at<int64_t>(d, D.tod)), hours_of(check),
         static_cast<long long>(before), static_cast<long long>(after), counter_before, counter,
         static_cast<long long>(now));
    return KILN_OK;
}

// Tick thread: the server world's Daytime runs at the held speed (after a load, a set, or Kiln's own change).
bool apply_scale_hold(uint8_t* d, float hold) {
    if (at<float>(d, D.scale) == hold) return true;
    const float was = at<float>(d, D.scale);
    if (!g_call_set_scale(d, hold, ecs_now())) return false;
    g_pending_since.store(static_cast<int64_t>(GetTickCount64()));
    g_pending_sync.store(at<uint32_t>(d, D.counter));
    logf(KILN_LOG_INFO, "time: clock speed %.2f -> %.2f; counter %u", was, hold, at<uint32_t>(d, D.counter));
    return true;
}

KilnResult KILN_CALL set_time_scale(float scale) {
    if (std::isnan(scale) || scale > 1000.0f) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t* d = g_daytime.load();
    if (!g_can_scale || !d) return KILN_ERR_UNAVAILABLE;
    if (scale < 0.0f) {
        const float had = g_scale_hold.exchange(-1.0f);
        g_scale_logged_refusal = false;
        if (had >= 0.0f) {
            // back to the game's normal speed; the sleep program raises it again while players sleep
            apply_scale_hold(d, 1.0f);
            logf(KILN_LOG_INFO, "time: the game decides the clock speed again (Kiln held %.2f; refused %llu requests)", had,
                 static_cast<unsigned long long>(g_scale_refused.exchange(0)));
        }
        return KILN_OK;
    }
    g_scale_hold.store(scale);
    g_scale_logged_refusal = false;
    return apply_scale_hold(d, scale) ? KILN_OK : KILN_ERR_INTERNAL;
}

float KILN_CALL time_scale_hold() { return g_can_scale ? g_scale_hold.load() : -2.0f; }

KilnResult KILN_CALL reset_weather() {
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t* ws = g_ws.load();
    if (!g_can_weather || !ws) return KILN_ERR_UNAVAILABLE;
    if (at<uint32_t>(ws, W.mode) != 0) return KILN_ERR_UNAVAILABLE;
    uint64_t layers_p = 0, n = 0;
    if (!g_read_u64(ws + W.layers, &layers_p) || !g_read_u64(ws + W.count, &n) || !layers_p) return KILN_ERR_INTERNAL;
    if (n > 64) return KILN_ERR_INTERNAL;  // not a sane layer list
    auto* layers = reinterpret_cast<uint8_t*>(layers_p);
    uint32_t removed = 0;
    for (uint64_t i = 0; i < n; ++i) {
        uint8_t* L = layers + i * static_cast<uint64_t>(W.stride);
        const uint32_t prev = at<uint32_t>(L, W.prev), cur = at<uint32_t>(L, W.cur);
        const int64_t next = at<int64_t>(L, 0);
        // removeInstance frees the slot and queues WeatherSyncRemoveInstanceMessage for every client tracker; it does
        // not clear the layer's handles, so do that here. Handle 0 is a no-op in the game.
        if (prev && g_call_remove(ws, prev)) ++removed;
        at<uint32_t>(L, W.prev) = 0;
        if (cur && g_call_remove(ws, cur)) ++removed;
        at<uint32_t>(L, W.cur) = 0;
        at<int64_t>(L, 0) = 0;  // nextPhenomenonTime: the next weather update re-rolls this layer
        logf(KILN_LOG_DEBUG, "time: weather layer %llu: next %lld, current 0x%x, previous 0x%x -> re-roll",
             static_cast<unsigned long long>(i), static_cast<long long>(next), cur, prev);
    }
    g_reset_pending.store(true);
    logf(KILN_LOG_INFO, "time: weather reset: %llu layers re-roll, %u instances removed", static_cast<unsigned long long>(n),
         removed);
    return KILN_OK;
}

// First Pre of a server-world session: bind the world's own Daytime and WeatherSystem.
void bind_server_world() {
    g_need_bind.store(false);
    const uintptr_t world = g_api->world(KILN_WORLD_SERVER);
    if (!world) return;
    auto* dt = reinterpret_cast<uint8_t*>(g_api->singleton(world, KilnStr{"g38_daytime::Daytime", 20}));
    auto* ws = reinterpret_cast<uint8_t*>(g_api->singleton(world, KilnStr{"WeatherSystem", 13}));
    g_server_ws.store(ws);
    if (ws && g_ws.load() && g_ws.load() != ws) g_ws.store(nullptr);  // the update hook re-captures the right one
    uint8_t* seen = g_daytime.load();
    if (dt) {
        g_server_daytime.store(dt);
        g_daytime.store(dt);
    }
    logf(KILN_LOG_INFO, "time: server world %p: Daytime %p (setTimeOfDay saw %p: %s), WeatherSystem %p", 
         reinterpret_cast<void*>(world), static_cast<void*>(dt), static_cast<void*>(seen),
         !dt ? "singleton not found, keeping it" : (seen == dt ? "same" : (seen ? "DIFFERENT, using the singleton" : "-")),
         static_cast<void*>(ws));
}

// The game closes the server world (a client leaving its hosted game): nothing of it may be used any more.
bool g_closed = false;  // tick thread: the last server world was closed with a STOPPING

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER) return;
    if (ev->event == KILN_WORLD_STARTED) {
        const bool closed = g_closed;
        g_closed = false;
        if (ev->session > 1 && !closed) {  // no STOPPING was seen (no world-destroy hook): drop the old world's objects
            g_daytime.store(nullptr);
            g_ws.store(nullptr);
            g_gs.store(nullptr);
            g_server_daytime.store(nullptr);
            g_server_ws.store(nullptr);
        }
        g_need_bind.store(true);
        return;
    }
    if (ev->event != KILN_WORLD_STOPPING) return;
    g_closed = true;
    g_daytime.store(nullptr);
    g_ws.store(nullptr);
    g_gs.store(nullptr);
    g_server_daytime.store(nullptr);
    g_server_ws.store(nullptr);
    g_synced.store(UINT32_MAX);
    g_pending_sync.store(-1);
    g_reset_pending.store(false);
    g_logged_state = g_logged_owner = g_ignored_logged = false;
    if (g_scale_hold.exchange(-1.0f) >= 0.0f) logf(KILN_LOG_INFO, "time: the held clock speed ends with the world");
    logf(KILN_LOG_INFO, "time: server world closed; Daytime and WeatherSystem released");
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    if (g_need_bind.load(std::memory_order_relaxed)) bind_server_world();
    if (!g_logged_state) {
        KilnTimeState s{};
        if (get(&s) == KILN_OK) {
            g_logged_state = true;
            logf(KILN_LOG_INFO,
                 "time: Daytime %p: %.4f h (field %.4f h), %s, day %.2f-%.2f h, day %.0f s, night %.0f s, scale %.2f, "
                 "counter %u, game time %lld ns, ecs %lld ns",
                 static_cast<void*>(g_daytime.load()), s.hours, s.hours_field, s.is_day ? "day" : "night",
                 s.day_start_hours, s.night_start_hours, s.day_length_s, s.night_length_s, s.time_scale,
                 s.change_counter, static_cast<long long>(s.game_time_ns), static_cast<long long>(s.ecs_time_ns));
        }
    }
    const float hold = g_scale_hold.load(std::memory_order_relaxed);
    if (hold >= 0.0f) {
        if (uint8_t* d = g_daytime.load(); d && at<float>(d, D.scale) != hold) apply_scale_hold(d, hold);
    }
    const int64_t want = g_pending_sync.load();
    if (want >= 0 && g_can_probe && GetTickCount64() - static_cast<uint64_t>(g_pending_since.load()) > 30000) {
        g_pending_sync.store(-1);
        logf(KILN_LOG_INFO, "time: no ServerInfo carried change %lld within 30 s (last sent %u; the server sends it "
             "when clients are connected)", static_cast<long long>(want), g_synced.load());
    }
}

const KilnTimeApi kTable = {{sizeof(KilnTimeApi), KILN_TIME_VERSION}, &get, &set_time_of_day, &reset_weather,
                            &set_time_scale, &time_scale_hold};

bool sym(const char* name, uintptr_t* out) {
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}
bool cst(const char* name, int64_t* out) {
    return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

template <typename F>
bool hook(SafetyHookInline& h, uintptr_t target, F* detour, const char* what) {
    h = safetyhook::create_inline(reinterpret_cast<void*>(target), reinterpret_cast<void*>(detour));
    if (!h) {
        logf(KILN_LOG_ERROR, "time: hooking %s at rva 0x%llx failed", what,
             static_cast<unsigned long long>(target - g_base));
        return false;
    }
    return true;
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    info->id = KilnStr{KILN_TIME_SERVICE, sizeof(KILN_TIME_SERVICE) - 1};
    info->version = KilnStr{"1", 1};

    uintptr_t set_fn = 0, game_time = 0, game_to_tod = 0, tod_to_game = 0;
    if (!feat("time.read") || !sym("time/SetTimeOfDay", &set_fn) || !sym("time/GetGameTime", &game_time) ||
        !sym("time/GameTimeToTod", &game_to_tod) || !cst("time/daytime.anchor_ecs", &D.anchor_ecs) ||
        !cst("time/daytime.anchor_game", &D.anchor_game) || !cst("time/daytime.counter", &D.counter) ||
        !cst("time/daytime.tod", &D.tod) || !cst("time/daytime.day_start", &D.day_start) ||
        !cst("time/daytime.night_start", &D.night_start) || !cst("time/daytime.day_length", &D.day_length) ||
        !cst("time/daytime.night_length", &D.night_length) || !cst("time/daytime.scale", &D.scale) ||
        !cst("time/daytime.is_day", &D.is_day)) {
        logf(KILN_LOG_WARN, "time: feature time.read unavailable on this build; kiln.time is off");
        return KILN_ERR_UNAVAILABLE;
    }
    g_get_game_time = reinterpret_cast<GetGameTimeFn>(game_time);
    g_game_to_tod = reinterpret_cast<GameToTodFn>(game_to_tod);
    g_can_set = feat("time.set") && sym("time/TodToGameTime", &tod_to_game);
    g_tod_to_game = reinterpret_cast<TodToGameFn>(tod_to_game);

    if (!hook(g_set_hook, set_fn, &hk_set_time_of_day, "setTimeOfDay")) return KILN_ERR_UNAVAILABLE;

    uintptr_t weather = 0, remove = 0;
    if (feat("time.weather") && sym("time/WeatherUpdate", &weather) && sym("time/WeatherRemoveInstance", &remove) &&
        cst("time/weather.mode", &W.mode) && cst("time/weather.layers", &W.layers) &&
        cst("time/weather.layer_count", &W.count) && cst("time/weather.layer_stride", &W.stride) &&
        cst("time/weather.layer_cur", &W.cur) && cst("time/weather.layer_prev", &W.prev)) {
        g_remove_instance = reinterpret_cast<RemoveInstanceFn>(remove);
        g_can_weather = hook(g_weather_hook, weather, &hk_weather_update, "WeatherSystem_update");
    }
    uintptr_t set_scale = 0;
    if (feat("time.scale") && sym("time/SetTimeScale", &set_scale))
        g_can_scale = hook(g_scale_hook, set_scale, &hk_set_scale, "the daytime speed setter");
    uintptr_t send_info = 0;
    if (feat("time.sync_probe") && sym("time/SendServerInfo", &send_info) && cst("time/gs.daytime_synced", &g_gs_synced) &&
        cst("time/gs.server_game", &g_gs_server_game) && cst("time/sg.daytime", &g_sg_daytime))
        g_can_probe = hook(g_info_hook, send_info, &hk_send_server_info, "SendServerInfo");

    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    logf(KILN_LOG_INFO, "time: hooks on setTimeOfDay rva 0x%llx; set %s, weather %s, sync probe %s, speed %s",
         static_cast<unsigned long long>(set_fn - g_base), g_can_set ? "on" : "off", g_can_weather ? "on" : "off",
         g_can_probe ? "on" : "off", g_can_scale ? "on" : "off");
    return api->register_service(owner, KilnStr{KILN_TIME_SERVICE, sizeof(KILN_TIME_SERVICE) - 1}, &kTable);
}
