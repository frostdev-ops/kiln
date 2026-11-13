// SPDX-License-Identifier: MIT
// kiln.time: time of day on the server world, a weather re-roll, and (v2) choosing the weather.
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
#ifndef KILN_TIME_HOST_TEST
#include <windows.h>
#include <intrin.h>
#endif

#include <atomic>
#include <climits>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <utility>
#include <vector>

#include "kiln_api.h"
#ifndef KILN_TIME_HOST_TEST
#include "kiln_hook.hpp"
#endif
#include "services/kiln_time.h"

namespace {

// Session state for the bound server clock; negative means the game decides, including sleep acceleration.
std::atomic<float> g_scale_hold{-1.0f};
std::atomic<uint64_t> g_scale_refused{0};
std::atomic<bool> g_scale_logged_refusal{false};

bool valid_scale(float scale) { return std::isfinite(scale) && scale <= 1000.0f; }

void reset_scale_state() {
    g_scale_hold.store(-1.0f);
    g_scale_refused.store(0);
    g_scale_logged_refusal.store(false);
}

void scale_world_event(uint32_t event, uint64_t session, bool closed) {
    if (event == KILN_WORLD_STOPPING || (event == KILN_WORLD_STARTED && session > 1 && !closed)) reset_scale_state();
}

// Publish a hold only after the game accepted the change. Failed changes/releases preserve the previous state.
template <typename Apply>
bool change_scale(float scale, Apply apply) {
    if (!valid_scale(scale)) return false;
    if (!(scale < 0.0f && g_scale_hold.load() < 0.0f) && !apply(scale < 0.0f ? 1.0f : scale)) return false;
    g_scale_hold.store(scale < 0.0f ? -1.0f : scale);
    g_scale_logged_refusal.store(false);
    g_scale_refused.store(0);
    return true;
}

// Sleep may ask for a different speed on every step. Do not re-anchor/bump the sync counter while already held.
bool scale_request(bool server, float current, float* requested) {
    const float hold = g_scale_hold.load();
    if (!server || hold < 0.0f) return true;
    *requested = hold;
    return current != hold;
}

// ---- choosing the weather (v2, since 0.2.0-dev.27; docs/re/time.md "Choosing the weather"): what needs no game call --

// Where the game's WeatherSystemResource keeps its tables (captured from the start of a phenomenon: sigdb
// @time.weather_table) and the layer state's current-phenomenon field.
struct WeatherTableOffsets {
    int64_t res_layers = -1, res_phenomena = -1, layer_stride = -1, phenomenon_stride = -1, layer_first = -1,
            layer_count = -1, layer_nothing = -1, layer_phenomenon = -1;
};
// Fields of the two records that no code Kiln captures reads (the reflected layouts of WeatherSystemPhenomenonResource,
// 176 bytes, and WeatherSystemLayerResource, 200 bytes; kfc `types --show`). Used only while the captured strides are
// those sizes.
constexpr int64_t kPhenGuid = 0x00, kPhenName = 0x10, kPhenProbability = 0x28, kPhenFlags = 0x2c, kPhenDurMin = 0x30,
                  kPhenDurMax = 0x38, kPhenDespawnFade = 0x60, kPhenEffect = 0xa8, kLayerGuid = 0x00, kLayerRestrict = 0x10,
                  kLayerExclude = 0x11;
constexpr int16_t kNoHold = INT16_MIN;
constexpr uint32_t kMaxWeatherLayers = 64;

bool weather_layout_ok(const WeatherTableOffsets& o) {
    return o.phenomenon_stride == 0xb0 && o.layer_stride == 0xc8 && o.layer_nothing == 0x18 && o.layer_first >= 0x10 &&
           o.layer_first < 0x18 && o.layer_count == o.layer_first + 1 && o.res_layers > 0 && o.res_layers < 0x100 &&
           o.res_phenomena > 0 && o.res_phenomena < 0x100 && o.layer_phenomenon > 0 && o.layer_phenomenon < 0x18;
}

// A BlobArray / BlobString field {i32 offset from the field, u32 count}: its first element, or null when empty.
const uint8_t* blob_at(const uint8_t* field, uint32_t* count) {
    int32_t rel = 0;
    std::memcpy(&rel, field, 4);
    std::memcpy(count, field + 4, 4);
    return rel && *count ? field + rel : nullptr;
}

// The game's choice byte for a request: a table index inside the layer's range, or -1 for the layer's "nothing" (the
// choice equal to the range's count). False for anything else.
bool weather_choice(int32_t phenomenon, uint8_t first, uint8_t count, uint8_t* choice) {
    if (phenomenon == -1) {
        *choice = count;
        return true;
    }
    if (phenomenon < first || phenomenon >= first + count) return false;
    *choice = static_cast<uint8_t>(phenomenon - first);
    return true;
}

// What a layer runs from its current-phenomenon field (the choice, stored as a qword): a table index, -1 for nothing,
// -2 when no choice is stored (after a reset, before the roll).
int32_t weather_current(uint64_t raw, uint8_t first, uint8_t count) {
    if (raw < count) return first + static_cast<int32_t>(raw);
    if (raw == count) return -1;
    return -2;
}

// The phenomenon table of a resource into `out` (`cap` records, `stride` bytes apart). Returns the table's size.
uint32_t weather_fill_table(const uint8_t* res, const WeatherTableOffsets& o, uint8_t* out, uint32_t cap, uint32_t stride) {
    uint32_t nl = 0, np = 0;
    const uint8_t* layers = blob_at(res + o.res_layers, &nl);
    const uint8_t* phen = blob_at(res + o.res_phenomena, &np);
    if (!phen) return 0;
    for (uint32_t i = 0; i < np && i < cap; ++i) {
        const uint8_t* p = phen + static_cast<uint64_t>(i) * o.phenomenon_stride;
        KilnTimeWeatherPhenomenon r{};
        r.size = sizeof r;
        r.index = static_cast<uint16_t>(i);
        r.layer = 0xFF;
        for (uint32_t l = 0; layers && l < nl && l < kMaxWeatherLayers; ++l) {
            const uint8_t* L = layers + static_cast<uint64_t>(l) * o.layer_stride;
            if (i >= L[o.layer_first] && i < static_cast<uint32_t>(L[o.layer_first]) + L[o.layer_count]) {
                r.layer = static_cast<uint8_t>(l);
                break;
            }
        }
        std::memcpy(r.guid, p + kPhenGuid, 16);
        std::memcpy(&r.probability, p + kPhenProbability, 4);
        std::memcpy(&r.flags, p + kPhenFlags, 4);
        std::memcpy(&r.duration_min_ns, p + kPhenDurMin, 8);
        std::memcpy(&r.duration_max_ns, p + kPhenDurMax, 8);
        r.effect = p[kPhenEffect];
        uint32_t len = 0;
        if (const uint8_t* name = blob_at(p + kPhenName, &len)) {
            const uint32_t n = len < sizeof r.name - 1 ? len : static_cast<uint32_t>(sizeof r.name - 1);
            std::memcpy(r.name, name, n);
        }
        std::memcpy(out + static_cast<uint64_t>(i) * stride, &r, sizeof r);
    }
    return np;
}

// The table's part of a layer's record (range, masks, guid, the weight of nothing). False: no such layer.
bool weather_fill_layer(const uint8_t* res, const WeatherTableOffsets& o, uint32_t i, KilnTimeWeatherLayer* l) {
    uint32_t nl = 0;
    const uint8_t* layers = blob_at(res + o.res_layers, &nl);
    if (!layers || i >= nl) return false;
    const uint8_t* L = layers + static_cast<uint64_t>(i) * o.layer_stride;
    l->index = static_cast<uint8_t>(i);
    l->first = L[o.layer_first];
    l->count = L[o.layer_count];
    l->restrict_mask = L[kLayerRestrict];
    l->exclude_mask = L[kLayerExclude];
    std::memcpy(l->guid, L + kLayerGuid, 16);
    std::memcpy(&l->nothing_probability, L + o.layer_nothing + kPhenProbability, 4);
    return true;
}

// The despawn fade time of the phenomenon a choice starts (the layer's nothing for choice == count). The game rolls the
// layer's next phenomenon that long before the end of a run (next = now + duration - fade), so Kiln adds it to a mod's
// duration: the layer is rolled again `duration` after the start, and the fade-out follows.
int64_t weather_fade(const uint8_t* res, const WeatherTableOffsets& o, uint32_t layer, uint8_t choice) {
    uint32_t nl = 0, np = 0;
    const uint8_t* layers = blob_at(res + o.res_layers, &nl);
    const uint8_t* phen = blob_at(res + o.res_phenomena, &np);
    if (!layers || layer >= nl) return 0;
    const uint8_t* L = layers + static_cast<uint64_t>(layer) * o.layer_stride;
    const uint8_t* rec = nullptr;
    if (choice == L[o.layer_count]) rec = L + o.layer_nothing;
    else if (phen && static_cast<uint32_t>(L[o.layer_first]) + choice < np)
        rec = phen + static_cast<uint64_t>(L[o.layer_first] + choice) * o.phenomenon_stride;
    int64_t fade = 0;
    if (rec) std::memcpy(&fade, rec + kPhenDespawnFade, 8);
    return fade > 0 && fade < 3'600'000'000'000ll ? fade : 0;
}

bool weather_request_flags_ok(uint32_t flags, int64_t duration) {
    if (flags & ~(KILN_TIME_WEATHER_HOLD | KILN_TIME_WEATHER_RELEASE | KILN_TIME_WEATHER_NOW)) return false;
    if (duration < 0) return false;
    if (flags & KILN_TIME_WEATHER_RELEASE) return !(flags & KILN_TIME_WEATHER_HOLD);
    return !(flags & KILN_TIME_WEATHER_NOW);
}

constexpr uint32_t kWeatherQueueMax = 256;

// The ordered changes of what each layer runs (KilnTimeWeatherChange, WeatherChangedEvent). The weather update looks at
// the layers before, between and after Kiln's requests and the game's roll; each difference from what it saw last is
// one record, so A then B then A in one update is two changes, and a restart (the same phenomenon, another instance)
// is one. Any thread (one lock; the update and the tick thread never hold it while calling the game).
class WeatherChanges {
public:
    static constexpr int32_t kUnseen = INT32_MIN;
    static constexpr size_t kKeep = 512;

    // A layer as it is now. The first look at a layer (or after forget) only remembers it. True: a change was recorded.
    bool note(uint32_t layer, int32_t phen, uint32_t instance, bool by_kiln, bool held, int64_t now) {
        if (layer >= kMaxWeatherLayers) return false;
        std::lock_guard lock(mu_);
        Seen& was = seen_[layer];
        const bool first = was.phen == kUnseen;
        const Seen before = was;
        was = {phen, instance};
        if (first || (before.phen == phen && before.instance == instance)) return false;
        KilnTimeWeatherChange c{};
        c.size = sizeof c;
        c.layer = static_cast<uint8_t>(layer);
        c.by_kiln = by_kiln ? 1 : 0;
        c.held = held ? 1 : 0;
        c.seq = ++seq_;
        c.before = before.phen;
        c.after = phen;
        c.instance_before = before.instance;
        c.instance_after = instance;
        c.now_ns = now;
        ring_.push_back(c);
        if (ring_.size() > kKeep) ring_.erase(ring_.begin(), ring_.begin() + static_cast<std::ptrdiff_t>(ring_.size() - kKeep));
        return true;
    }

    // Takes the layer's state as seen without a record (Kiln emptied it and the game rolls it next: that roll is the change).
    void resync(uint32_t layer, int32_t phen, uint32_t instance) {
        if (layer >= kMaxWeatherLayers) return;
        std::lock_guard lock(mu_);
        if (seen_[layer].phen != kUnseen) seen_[layer] = {phen, instance};
    }

    // The world closed: the next world's first look only remembers.
    void forget() {
        std::lock_guard lock(mu_);
        for (Seen& x : seen_) x = {};
    }

    uint64_t last_seq() {
        std::lock_guard lock(mu_);
        return seq_;
    }

    KilnResult read(uint64_t after, KilnTimeWeatherChange* out, uint32_t cap, uint32_t item_size, uint32_t* count) {
        if (count) *count = 0;
        if (!count || (cap && !out) || item_size < sizeof(KilnTimeWeatherChange)) return KILN_ERR_INVALID_ARGUMENT;
        std::lock_guard lock(mu_);
        uint32_t n = 0;
        for (const KilnTimeWeatherChange& c : ring_) {
            if (c.seq <= after) continue;
            if (n < cap) std::memcpy(reinterpret_cast<uint8_t*>(out) + static_cast<size_t>(n) * item_size, &c, sizeof c);
            ++n;
        }
        *count = n;
        return n > cap ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
    }

private:
    struct Seen {
        int32_t phen = kUnseen;
        uint32_t instance = 0;
    };
    std::mutex mu_;
    Seen seen_[kMaxWeatherLayers];
    std::vector<KilnTimeWeatherChange> ring_;
    uint64_t seq_ = 0;
};

// Per layer, what Kiln's requests leave for the layer's next recorded change. resync: Kiln emptied the layer (a release with
// now, a reset) and the game rolls it next: take the emptied state as seen, with no record of its own. kiln_next: that
// roll is Kiln's doing (WeatherChangedEvent.ByKiln). A request that sets the layer supersedes both: its own change is
// recorded at once, and the game's next roll of it is the game's own. A release without now leaves them as they are.
class WeatherMarks {
public:
    void after_request(uint32_t layer, uint32_t flags) {
        if (layer >= kMaxWeatherLayers) return;
        if (!(flags & KILN_TIME_WEATHER_RELEASE)) {
            resync_[layer].store(false);
            kiln_next_[layer].store(false);
        } else if (flags & KILN_TIME_WEATHER_NOW) {
            resync_[layer].store(true);
            kiln_next_[layer].store(true);
        }
    }
    void after_reset(uint32_t layers) {
        for (uint32_t i = 0; i < layers && i < kMaxWeatherLayers; ++i) {
            resync_[i].store(true);
            kiln_next_[i].store(true);
        }
    }
    bool take_resync(uint32_t layer) { return layer < kMaxWeatherLayers && resync_[layer].exchange(false); }
    bool kiln_next(uint32_t layer) const { return layer < kMaxWeatherLayers && kiln_next_[layer].load(); }
    void roll_seen(uint32_t layer) {
        if (layer < kMaxWeatherLayers) kiln_next_[layer].store(false);
    }
    void clear() {
        for (auto& x : resync_) x.store(false);
        for (auto& x : kiln_next_) x.store(false);
    }

private:
    std::atomic<bool> resync_[kMaxWeatherLayers]{};
    std::atomic<bool> kiln_next_[kMaxWeatherLayers]{};
};

// The queue of weather_set requests: at most kWeatherQueueMax; a full queue takes nothing (a refusal for now).
template <class Op>
class WeatherQueue {
public:
    KilnResult push(const Op& op) {
        std::lock_guard lock(mu_);
        if (ops_.size() >= kWeatherQueueMax) return KILN_ERR_BUFFER_TOO_SMALL;
        ops_.push_back(op);
        return KILN_OK;
    }
    uint32_t room() {
        std::lock_guard lock(mu_);
        return static_cast<uint32_t>(kWeatherQueueMax - ops_.size());
    }
    std::vector<Op> take() {
        std::vector<Op> v;
        std::lock_guard lock(mu_);
        v.swap(ops_);
        return v;
    }
    // Pre: the ones waiting longer than `older` end (out); the rest stay.
    template <class F>
    void expire(F&& too_old, std::vector<Op>* out) {
        std::lock_guard lock(mu_);
        for (auto it = ops_.begin(); it != ops_.end();) {
            if (!too_old(*it)) {
                ++it;
                continue;
            }
            out->push_back(*it);
            it = ops_.erase(it);
        }
    }

private:
    std::mutex mu_;
    std::vector<Op> ops_;
};

#ifndef KILN_TIME_HOST_TEST

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

kiln::RawHook g_set_hook, g_weather_hook, g_info_hook, g_scale_hook;
bool g_can_set = false, g_can_weather = false, g_can_probe = false, g_can_scale = false;
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
    const uintptr_t ret = g_set_hook.caller();  // the game's call site, read before the original
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
bool read_scale(uint8_t* daytime, float* scale) {
    __try { *scale = at<float>(daytime, D.scale); return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

void __fastcall hk_set_scale(uint8_t* daytime, float scale, int64_t ecs_now) {
    float current = 0;
    // Only the bound server singleton is authoritative. Before binding, a hosting client may have captured its
    // prediction world's Daytime; never hold that clock. An unreadable object follows the original game path.
    if (daytime && daytime == g_server_daytime.load() && read_scale(daytime, &current)) {
        if (!scale_request(true, current, &scale)) {
            g_scale_refused.fetch_add(1);
            if (!g_scale_logged_refusal.exchange(true))
                logf(KILN_LOG_INFO, "time: the game asked for a different clock speed; Kiln holds %.2f", current);
            return;
        }
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

// ---- choosing the weather (v2): the start of a phenomenon, called where the game rolls its layers ----
// WeatherUpdateLayers(ws, resource, now, params) runs in the GameServer frame after the sim step, on the tick thread.
// Kiln hooks it to know the resource and the weather clock, and to run what mods asked for right before the game's own
// roll: a choice ends the layer's instances and starts the chosen phenomenon the way the game's SetPhenomenon state
// does; a held layer whose time is up is rolled with Kiln's choice the way the game's roll does (the old instance
// fades as the previous one), so the game's roll then finds nothing to do on it.
WeatherTableOffsets WT;
struct OptTime {
    int64_t value;
    uint8_t has;
    uint8_t pad[7];
};
using StartPhenomenonFn = void(__fastcall*)(void* ws, void* res, int64_t now, uint64_t layer, uint8_t choice,
                                            const OptTime* duration);
StartPhenomenonFn g_start = nullptr;
kiln::RawHook g_layers_hook;
bool g_can_choose = false;
std::atomic<bool> g_choose_broken{false};
std::atomic<uint8_t*> g_res{nullptr};
std::atomic<int64_t> g_weather_now{0};
std::atomic<uint64_t> g_step{0};
std::atomic<int16_t> g_hold[kMaxWeatherLayers];
std::atomic<uint32_t> g_kiln_changes[kMaxWeatherLayers];
std::atomic<uint64_t> g_next_ticket{1};
std::atomic<uint64_t> g_held_rolls{0};
constexpr uint64_t kWeatherTimeoutSteps = 250;  // ~10 s at 24.5 Hz

struct WeatherOp {
    uint64_t ticket;
    KilnOwner owner;
    KilnTimeWeatherRequest r;
    uint8_t choice;
    uint64_t submitted;
    KilnTimeWeatherDoneFn done;
    void* user;
};
std::mutex g_wmu;  // the finished list; never held while calling the game or a callback
WeatherQueue<WeatherOp> g_wq;
std::vector<std::pair<KilnTimeWeatherResult, WeatherOp>> g_wdone;
WeatherChanges g_changes;
WeatherMarks g_marks;

// Each layer's phenomenon and instance now (the weather update, or the tick thread outside it). Plain data only (SEH).
bool read_layer_states(uint8_t* ws, uint8_t* res, int32_t* phen, uint32_t* inst, uint32_t* count) {
    __try {
        const uint64_t layers_p = at<uint64_t>(ws, W.layers), n = at<uint64_t>(ws, W.count);
        *count = 0;
        for (uint32_t i = 0; layers_p && i < n && i < kMaxWeatherLayers; ++i) {
            KilnTimeWeatherLayer tl{};
            if (!weather_fill_layer(res, WT, i, &tl)) break;
            uint8_t* L = reinterpret_cast<uint8_t*>(layers_p) + static_cast<uint64_t>(i) * W.stride;
            phen[i] = weather_current(at<uint64_t>(L, WT.layer_phenomenon), tl.first, tl.count);
            inst[i] = at<uint32_t>(L, W.cur);
            *count = i + 1;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Records what changed since the last look. only < 0: every layer; else that one. kiln: Kiln's change; with
// kiln_next the layers whose next roll is Kiln's doing count as Kiln's (the game's roll after a release now or a reset).
void note_layers(uint8_t* ws, uint8_t* res, int64_t now, int only, bool kiln, bool kiln_next) {
    if (!g_can_choose) return;
    int32_t phen[kMaxWeatherLayers];
    uint32_t inst[kMaxWeatherLayers], n = 0;
    if (!read_layer_states(ws, res, phen, inst, &n)) return;
    for (uint32_t i = 0; i < n; ++i) {
        if (only >= 0 && static_cast<uint32_t>(only) != i) continue;
        if (g_marks.take_resync(i)) {
            g_changes.resync(i, phen[i], inst[i]);
            continue;
        }
        const bool by_kiln = kiln || (kiln_next && g_marks.kiln_next(i));
        if (g_changes.note(i, phen[i], inst[i], by_kiln, g_hold[i].load() != kNoHold, now) && kiln_next) g_marks.roll_seen(i);
    }
}

void clear_holds() {
    for (auto& h : g_hold) h.store(kNoHold);
}

KilnTimeWeatherResult weather_result_of(const WeatherOp& op, int32_t status) {
    KilnTimeWeatherResult r{};
    r.size = sizeof r;
    r.status = status;
    r.ticket = op.ticket;
    r.layer = op.r.layer;
    r.flags = op.r.flags;
    r.current = -2;
    return r;
}

// The phenomenon's name for kiln.log (tick thread; the resource is the game's, read under SEH by the callers).
void phen_name(const uint8_t* res, int32_t index, char* out, size_t cap) {
    std::snprintf(out, cap, "%s", index == -1 ? "nothing" : index == -2 ? "none" : "?");
    if (index < 0) return;
    uint32_t np = 0, len = 0;
    const uint8_t* phen = blob_at(res + WT.res_phenomena, &np);
    if (!phen || static_cast<uint32_t>(index) >= np) return;
    if (const uint8_t* name = blob_at(phen + static_cast<uint64_t>(index) * WT.phenomenon_stride + kPhenName, &len))
        std::snprintf(out, cap, "%.*s", static_cast<int>(len < cap ? len : cap - 1), reinterpret_cast<const char*>(name));
}

// One queued request, inside the weather update. Plain data only (SEH).
bool run_weather_op(uint8_t* ws, uint8_t* res, int64_t now, const WeatherOp& op, KilnTimeWeatherResult* r, char* log, size_t logcap) {
    __try {
        const uint64_t layers_p = at<uint64_t>(ws, W.layers), n = at<uint64_t>(ws, W.count);
        KilnTimeWeatherLayer tl{};
        if (!layers_p || op.r.layer >= n || op.r.layer >= kMaxWeatherLayers || !weather_fill_layer(res, WT, op.r.layer, &tl)) {
            r->status = KILN_TIME_WEATHER_DROPPED;
            std::snprintf(r->message, sizeof r->message, "layer %u is not in the running weather", op.r.layer);
            return true;
        }
        uint8_t* L = reinterpret_cast<uint8_t*>(layers_p) + static_cast<uint64_t>(op.r.layer) * W.stride;
        const uint32_t prev = at<uint32_t>(L, W.prev), cur = at<uint32_t>(L, W.cur);
        if (op.r.flags & KILN_TIME_WEATHER_RELEASE) {
            g_hold[op.r.layer].store(kNoHold);
            if (op.r.flags & KILN_TIME_WEATHER_NOW) {  // what reset_weather does for one layer: the game rolls it now
                if (prev) g_remove_instance(ws, prev);
                if (cur) g_remove_instance(ws, cur);
                at<uint32_t>(L, W.prev) = 0;
                at<uint32_t>(L, W.cur) = 0;
                at<int64_t>(L, 0) = 0;
            }
            std::snprintf(log, logcap, "released%s", (op.r.flags & KILN_TIME_WEATHER_NOW) ? ", the game rolls it now" : "");
        } else {
            // the game's SetPhenomenon (0xa22f50 for a state configuration): both instances end, the layer is cleared,
            // then the start of a phenomenon with the choice and the optional override duration
            if (prev) g_remove_instance(ws, prev);
            if (cur) g_remove_instance(ws, cur);
            at<uint32_t>(L, W.prev) = 0;
            at<uint32_t>(L, W.cur) = 0;
            at<int64_t>(L, 0) = INT64_MIN;
            at<uint64_t>(L, WT.layer_phenomenon) = ~0ull;
            const int64_t asked = op.r.duration_ns > 0 ? op.r.duration_ns + weather_fade(res, WT, op.r.layer, op.choice) : 0;
            const OptTime duration{asked, static_cast<uint8_t>(asked > 0 ? 1 : 0), {}};
            g_start(ws, res, now, op.r.layer, op.choice, &duration);
            g_hold[op.r.layer].store((op.r.flags & KILN_TIME_WEATHER_HOLD) ? op.choice : kNoHold);
            char name[64];
            phen_name(res, op.r.phenomenon, name, sizeof name);
            std::snprintf(log, logcap, "started %s (table %d) %s%s", name, op.r.phenomenon,
                          op.r.duration_ns > 0 ? "for the given time" : "for its own time",
                          (op.r.flags & KILN_TIME_WEATHER_HOLD) ? ", held until released" : "");
        }
        g_kiln_changes[op.r.layer].fetch_add(1);
        r->current = weather_current(at<uint64_t>(L, WT.layer_phenomenon), tl.first, tl.count);
        r->instance = at<uint32_t>(L, W.cur);
        r->next_ns = at<int64_t>(L, 0);
        r->now_ns = now;
        r->status = KILN_TIME_WEATHER_DONE;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Held layers whose time is up: the game's roll, with Kiln's choice. Returns the layers rolled; -1 on a fault.
int roll_held(uint8_t* ws, uint8_t* res, int64_t now) {
    __try {
        const uint64_t layers_p = at<uint64_t>(ws, W.layers), n = at<uint64_t>(ws, W.count);
        int rolled = 0;
        for (uint64_t i = 0; layers_p && i < n && i < kMaxWeatherLayers; ++i) {
            const int16_t h = g_hold[i].load();
            if (h == kNoHold) continue;
            uint8_t* L = reinterpret_cast<uint8_t*>(layers_p) + i * static_cast<uint64_t>(W.stride);
            if (at<int64_t>(L, 0) > now) continue;
            // as WeatherUpdateLayers when a layer's time is up: the previous instance ends, the current one becomes the
            // previous (it fades out by its own despawn time), then the start of the next phenomenon
            if (const uint32_t prev = at<uint32_t>(L, W.prev)) g_remove_instance(ws, prev);
            at<uint32_t>(L, W.prev) = at<uint32_t>(L, W.cur);
            at<uint32_t>(L, W.cur) = 0;
            const OptTime none{0, 0, {}};
            g_start(ws, res, now, i, static_cast<uint8_t>(h), &none);
            g_kiln_changes[i].fetch_add(1);
            ++rolled;
        }
        return rolled;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return -1;
    }
}

void weather_before_roll(uint8_t* ws, uint8_t* res, int64_t now) {
    note_layers(ws, res, now, -1, false, false);  // what changed outside the update (the game's weather states)
    std::vector<WeatherOp> ops = g_wq.take();
    for (const WeatherOp& op : ops) {
        KilnTimeWeatherResult r = weather_result_of(op, KILN_TIME_WEATHER_DONE);
        char log[160] = "";
        if (g_choose_broken.load()) {
            r.status = KILN_TIME_WEATHER_FAILED;
            std::snprintf(r.message, sizeof r.message, "choosing the weather was switched off (see kiln.log)");
        } else if (!run_weather_op(ws, res, now, op, &r, log, sizeof log)) {
            r.status = KILN_TIME_WEATHER_FAILED;
            std::snprintf(r.message, sizeof r.message, "the game's start of a phenomenon faulted");
            g_choose_broken.store(true);
            logf(KILN_LOG_ERROR, "time: the game's weather start faulted on layer %u; choosing the weather is off until a restart",
                 op.r.layer);
        } else if (r.status == KILN_TIME_WEATHER_DONE) {
            logf(KILN_LOG_INFO, "time: weather layer %u: %s; now runs %d (instance %u), next roll at game time %lld ns "
                 "(clock %lld ns)", op.r.layer, log, r.current, r.instance, static_cast<long long>(r.next_ns),
                 static_cast<long long>(now));
            g_marks.after_request(op.r.layer, op.r.flags);
            note_layers(ws, res, now, static_cast<int>(op.r.layer), true, false);
        }
        std::lock_guard lock(g_wmu);
        g_wdone.emplace_back(r, op);
    }
    if (g_choose_broken.load()) return;
    const int rolled = roll_held(ws, res, now);
    if (rolled < 0) {
        g_choose_broken.store(true);
        clear_holds();
        logf(KILN_LOG_ERROR, "time: a held weather roll faulted; holds dropped, choosing the weather is off until a restart");
    } else if (rolled > 0) {
        note_layers(ws, res, now, -1, true, false);
        g_held_rolls.fetch_add(static_cast<uint64_t>(rolled));
        logf(KILN_LOG_INFO, "time: %d held weather layer(s) rolled with Kiln's choice at game time %lld ns", rolled,
             static_cast<long long>(now));
    }
}

void __fastcall hk_weather_layers(uint8_t* ws, uint8_t* res, int64_t now, void* params) {
    if (ws && res && ws == g_ws.load(std::memory_order_relaxed)) {
        g_res.store(res, std::memory_order_relaxed);
        g_weather_now.store(now, std::memory_order_relaxed);
        weather_before_roll(ws, res, now);
        g_layers_hook.call<void>(ws, res, now, params);
        note_layers(ws, res, now, -1, false, true);  // the game's roll (Kiln's when a release now or a reset asked for it)
        return;
    }
    g_layers_hook.call<void>(ws, res, now, params);
}

// Tick thread (no weather update running): the game's records, read under SEH.
bool read_table(uint8_t* res, uint8_t* out, uint32_t cap, uint32_t stride, uint32_t* total) {
    __try {
        *total = weather_fill_table(res, WT, out, cap, stride);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_layers(uint8_t* ws, uint8_t* res, uint8_t* out, uint32_t cap, uint32_t stride, uint32_t* total) {
    __try {
        const uint64_t layers_p = at<uint64_t>(ws, W.layers), n = at<uint64_t>(ws, W.count);
        *total = static_cast<uint32_t>(n < kMaxWeatherLayers ? n : kMaxWeatherLayers);
        const int64_t now = g_weather_now.load();
        for (uint32_t i = 0; layers_p && i < *total && i < cap; ++i) {
            KilnTimeWeatherLayer l{};
            l.size = sizeof l;
            weather_fill_layer(res, WT, i, &l);
            uint8_t* L = reinterpret_cast<uint8_t*>(layers_p) + static_cast<uint64_t>(i) * W.stride;
            l.current = weather_current(at<uint64_t>(L, WT.layer_phenomenon), l.first, l.count);
            const int16_t h = g_hold[i].load();
            l.held = h != kNoHold;
            l.held_phenomenon = h == kNoHold ? -2 : (h == l.count ? -1 : l.first + h);
            l.instance = at<uint32_t>(L, W.cur);
            l.kiln_changes = g_kiln_changes[i].load();
            l.next_ns = at<int64_t>(L, 0);
            l.now_ns = now;
            std::memcpy(out + static_cast<uint64_t>(i) * stride, &l, sizeof l);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool layer_of(uint8_t* res, uint32_t layer, KilnTimeWeatherLayer* l) {
    __try {
        return weather_fill_layer(res, WT, layer, l);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool weather_ready(uint8_t** ws, uint8_t** res) {
    *ws = g_ws.load();
    *res = g_res.load();
    return g_can_choose && !g_choose_broken.load() && *ws && *res;
}

KilnResult KILN_CALL weather_table(KilnTimeWeatherPhenomenon* out, uint32_t capacity, uint32_t item_size, uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if ((capacity && !out) || item_size < sizeof(KilnTimeWeatherPhenomenon) || !out_count) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t *ws = nullptr, *res = nullptr;
    if (!weather_ready(&ws, &res)) return KILN_ERR_UNAVAILABLE;
    uint32_t total = 0;
    if (!read_table(res, reinterpret_cast<uint8_t*>(out), capacity, item_size, &total)) return KILN_ERR_INTERNAL;
    *out_count = total;
    return total > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL weather_layers(KilnTimeWeatherLayer* out, uint32_t capacity, uint32_t item_size, uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if ((capacity && !out) || item_size < sizeof(KilnTimeWeatherLayer) || !out_count) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t *ws = nullptr, *res = nullptr;
    if (!weather_ready(&ws, &res)) return KILN_ERR_UNAVAILABLE;
    uint32_t total = 0;
    if (!read_layers(ws, res, reinterpret_cast<uint8_t*>(out), capacity, item_size, &total)) return KILN_ERR_INTERNAL;
    *out_count = total;
    return total > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

KilnResult KILN_CALL weather_set(KilnOwner owner, const KilnTimeWeatherRequest* r, KilnTimeWeatherDoneFn done, void* user,
                                 uint64_t* out_ticket) {
    if (out_ticket) *out_ticket = 0;
    if (!r || r->size < sizeof(KilnTimeWeatherRequest) || !weather_request_flags_ok(r->flags, r->duration_ns))
        return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t *ws = nullptr, *res = nullptr;
    if (!weather_ready(&ws, &res)) return KILN_ERR_UNAVAILABLE;
    KilnTimeWeatherLayer l{};
    if (r->layer >= kMaxWeatherLayers || !layer_of(res, r->layer, &l)) return KILN_ERR_INVALID_ARGUMENT;
    WeatherOp op{};
    if (!(r->flags & KILN_TIME_WEATHER_RELEASE) && !weather_choice(r->phenomenon, l.first, l.count, &op.choice))
        return KILN_ERR_INVALID_ARGUMENT;
    op.ticket = g_next_ticket.fetch_add(1);
    op.owner = owner;
    op.r = *r;
    op.r.size = sizeof(KilnTimeWeatherRequest);
    op.submitted = g_step.load();
    op.done = done;
    op.user = user;
    if (g_wq.push(op) != KILN_OK) return KILN_ERR_BUFFER_TOO_SMALL;  // a refusal for now: nothing was queued
    if (out_ticket) *out_ticket = op.ticket;
    return KILN_OK;
}

uint32_t KILN_CALL weather_room() { return g_wq.room(); }

uint64_t KILN_CALL weather_change_seq() { return g_changes.last_seq(); }

KilnResult KILN_CALL weather_changes(uint64_t after, KilnTimeWeatherChange* out, uint32_t capacity, uint32_t item_size,
                                     uint32_t* out_count) {
    return g_changes.read(after, out, capacity, item_size, out_count);
}

bool guarded_weather_done(KilnTimeWeatherDoneFn fn, const KilnTimeWeatherResult* r, void* user) {
    __try {
        fn(r, user);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Pre: requests the weather never ran (no update for 10 s) end as dropped; finished ones go to their callbacks.
void weather_pre(uint64_t step) {
    g_step.store(step);
    std::vector<std::pair<KilnTimeWeatherResult, WeatherOp>> done;
    std::vector<WeatherOp> old;
    g_wq.expire([step](const WeatherOp& op) { return step > op.submitted + kWeatherTimeoutSteps; }, &old);
    {
        std::lock_guard lock(g_wmu);
        for (const WeatherOp& op : old) {
            KilnTimeWeatherResult r = weather_result_of(op, KILN_TIME_WEATHER_DROPPED);
            std::snprintf(r.message, sizeof r.message, "the server's weather did not update for 10 s");
            g_wdone.emplace_back(r, op);
        }
        done.swap(g_wdone);
    }
    for (const auto& [r, op] : done)
        if (op.done && !guarded_weather_done(op.done, &r, op.user))
            logf(KILN_LOG_ERROR, "time: a weather result callback (owner %llu) faulted; ignored",
                 static_cast<unsigned long long>(op.owner));
}

// The server world closed: its weather objects are gone, holds end, waiting requests are dropped.
void weather_world_closed() {
    g_res.store(nullptr);
    clear_holds();
    g_changes.forget();
    g_marks.clear();
    const std::vector<WeatherOp> left = g_wq.take();
    std::lock_guard lock(g_wmu);
    for (const WeatherOp& op : left) {
        KilnTimeWeatherResult r = weather_result_of(op, KILN_TIME_WEATHER_DROPPED);
        std::snprintf(r.message, sizeof r.message, "the server world closed");
        g_wdone.emplace_back(r, op);
    }
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
    s.weather_choice_ready = (g_can_choose && !g_choose_broken.load() && g_ws.load() && g_res.load()) ? 1 : 0;
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
    __try {
        if (at<float>(d, D.scale) == hold) return true;
        if (!g_call_set_scale(d, hold, ecs_now())) return false;
        g_pending_since.store(static_cast<int64_t>(GetTickCount64()));
        g_pending_sync.store(at<uint32_t>(d, D.counter));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

KilnResult KILN_CALL set_time_scale(float scale) {
    if (!valid_scale(scale)) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_api->on_tick_thread()) return KILN_ERR_WRONG_THREAD;
    uint8_t* d = g_server_daytime.load();
    if (!g_can_scale || !d) return KILN_ERR_UNAVAILABLE;
    return change_scale(scale, [d](float v) { return apply_scale_hold(d, v); }) ? KILN_OK : KILN_ERR_INTERNAL;
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
        if (i < kMaxWeatherLayers) g_kiln_changes[i].fetch_add(1);
        logf(KILN_LOG_DEBUG, "time: weather layer %llu: next %lld, current 0x%x, previous 0x%x -> re-roll",
             static_cast<unsigned long long>(i), static_cast<long long>(next), cur, prev);
    }
    // emptied here, outside the update: not a change of its own; the game's roll in the next update is (by Kiln)
    g_marks.after_reset(static_cast<uint32_t>(n));
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
    scale_world_event(ev->event, ev->session, g_closed);
    if (ev->event == KILN_WORLD_STARTED) {
        const bool closed = g_closed;
        g_closed = false;
        if (ev->session > 1 && !closed) {  // no STOPPING was seen (no world-destroy hook): drop the old world's objects
            weather_world_closed();
            g_synced.store(UINT32_MAX);
            g_pending_sync.store(-1);
            g_logged_state = g_logged_owner = g_ignored_logged = false;
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
    weather_world_closed();
    g_daytime.store(nullptr);
    g_ws.store(nullptr);
    g_gs.store(nullptr);
    g_server_daytime.store(nullptr);
    g_server_ws.store(nullptr);
    g_synced.store(UINT32_MAX);
    g_pending_sync.store(-1);
    g_reset_pending.store(false);
    g_logged_state = g_logged_owner = g_ignored_logged = false;
    logf(KILN_LOG_INFO, "time: server world closed; Daytime and WeatherSystem released");
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    if (g_need_bind.load(std::memory_order_relaxed)) bind_server_world();
    weather_pre(tick->step);
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
        if (uint8_t* d = g_server_daytime.load(); g_can_scale && d) apply_scale_hold(d, hold);
    }
    const int64_t want = g_pending_sync.load();
    if (want >= 0 && g_can_probe && GetTickCount64() - static_cast<uint64_t>(g_pending_since.load()) > 30000) {
        g_pending_sync.store(-1);
        logf(KILN_LOG_INFO, "time: no ServerInfo carried change %lld within 30 s (last sent %u; the server sends it "
             "when clients are connected)", static_cast<long long>(want), g_synced.load());
    }
}

const KilnTimeApi kTable = {{sizeof(KilnTimeApi), KILN_TIME_VERSION}, &get, &set_time_of_day, &reset_weather,
                            &set_time_scale, &time_scale_hold, &weather_table, &weather_layers, &weather_set,
                            &weather_room, &weather_change_seq, &weather_changes};

bool sym(const char* name, uintptr_t* out) {
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}
bool cst(const char* name, int64_t* out) {
    return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

template <typename F>
bool hook(kiln::RawHook& h, uintptr_t target, F* detour, const char* what, const char* label) {
    h.install(g_api, g_owner, target, reinterpret_cast<void*>(detour), label);
    if (!h) {
        logf(KILN_LOG_ERROR, "time: hooking %s at rva 0x%llx failed", what,
             static_cast<unsigned long long>(target - g_base));
        return false;
    }
    return true;
}

#endif  // !KILN_TIME_HOST_TEST
}  // namespace

#ifndef KILN_TIME_HOST_TEST
extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    g_base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    info->id = KilnStr{KILN_TIME_SERVICE, sizeof(KILN_TIME_SERVICE) - 1};
    info->version = KilnStr{"2", 1};
    clear_holds();

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

    if (!hook(g_set_hook, set_fn, &hk_set_time_of_day, "setTimeOfDay", "time/SetTimeOfDay")) return KILN_ERR_UNAVAILABLE;

    uintptr_t weather = 0, remove = 0;
    if (feat("time.weather") && sym("time/WeatherUpdate", &weather) && sym("time/WeatherRemoveInstance", &remove) &&
        cst("time/weather.mode", &W.mode) && cst("time/weather.layers", &W.layers) &&
        cst("time/weather.layer_count", &W.count) && cst("time/weather.layer_stride", &W.stride) &&
        cst("time/weather.layer_cur", &W.cur) && cst("time/weather.layer_prev", &W.prev)) {
        g_remove_instance = reinterpret_cast<RemoveInstanceFn>(remove);
        g_can_weather = hook(g_weather_hook, weather, &hk_weather_update, "WeatherSystem_update", "time/WeatherUpdate");
    }
    // v2: choosing the weather (docs/re/time.md "Choosing the weather"): the layer update is hooked, the game's start of
    // a phenomenon called; the resource's layout must be the reflected one
    uintptr_t layers_fn = 0, start_fn = 0;
    if (g_can_weather && feat("time.weather_choose") && sym("time/WeatherUpdateLayers", &layers_fn) &&
        sym("time/WeatherStartPhenomenon", &start_fn) && cst("time/weather.res_layers", &WT.res_layers) &&
        cst("time/weather.res_phenomena", &WT.res_phenomena) && cst("time/weather.res_layer_stride", &WT.layer_stride) &&
        cst("time/weather.res_phenomenon_stride", &WT.phenomenon_stride) &&
        cst("time/weather.res_layer_first", &WT.layer_first) && cst("time/weather.res_layer_count", &WT.layer_count) &&
        cst("time/weather.res_layer_nothing", &WT.layer_nothing) &&
        cst("time/weather.layer_phenomenon", &WT.layer_phenomenon)) {
        if (!weather_layout_ok(WT)) {
            logf(KILN_LOG_WARN, "time: the weather table's layout differs from the reflected one (layer %lld B, phenomenon "
                 "%lld B, nothing at +0x%llx); choosing the weather is off", static_cast<long long>(WT.layer_stride),
                 static_cast<long long>(WT.phenomenon_stride), static_cast<unsigned long long>(WT.layer_nothing));
        } else {
            g_start = reinterpret_cast<StartPhenomenonFn>(start_fn);
            g_can_choose = hook(g_layers_hook, layers_fn, &hk_weather_layers, "WeatherUpdateLayers", "time/WeatherUpdateLayers");
        }
    }
    uintptr_t set_scale = 0;
    if (feat("time.scale") && sym("time/SetTimeScale", &set_scale))
        g_can_scale = hook(g_scale_hook, set_scale, &hk_set_scale, "the daytime speed setter", "time/SetTimeScale");
    uintptr_t send_info = 0;
    if (feat("time.sync_probe") && sym("time/SendServerInfo", &send_info) && cst("time/gs.daytime_synced", &g_gs_synced) &&
        cst("time/gs.server_game", &g_gs_server_game) && cst("time/sg.daytime", &g_sg_daytime))
        g_can_probe = hook(g_info_hook, send_info, &hk_send_server_info, "SendServerInfo", "time/SendServerInfo");

    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    logf(KILN_LOG_INFO, "time: hooks on setTimeOfDay rva 0x%llx; set %s, weather %s, weather choice %s, sync probe %s, speed %s",
         static_cast<unsigned long long>(set_fn - g_base), g_can_set ? "on" : "off", g_can_weather ? "on" : "off",
         g_can_choose ? "on" : "off", g_can_probe ? "on" : "off", g_can_scale ? "on" : "off");
    return api->register_service(owner, KilnStr{KILN_TIME_SERVICE, sizeof(KILN_TIME_SERVICE) - 1}, &kTable);
}

#endif  // !KILN_TIME_HOST_TEST
