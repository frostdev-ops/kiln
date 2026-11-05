// SPDX-License-Identifier: MIT
// kiln.settings: read and change the server's live game settings (keen::ecs::GameSettings, the "gameSettings" block
// of enshrouded_server.json) and write them back to that file on request.
//
// How it finds them (docs/re/settings.md):
//  - The GameServer instance holds the pointer to the live 144-byte block (+0x1758 on svn 1285601) and a byte flag
//    (+0x18E0) that makes SendServerInfo put the block into the next server-info message for every player and clear
//    the flag. kiln.settings hooks SendServerInfo(instance) to learn the instance; that is all the hook does, apart
//    from counting how often the game sent the block after kiln.settings asked it to.
//  - The field layout and enum names come from the game's own reflection: TypeByHash(fnv1a("keen::ecs::GameSettings")).
//  - The block is cross-checked against the world's named singleton "ecs::GameSettings" (the object every game system
//    is bound to; the core's api->singleton). ShroudForge (bonsaibauer, MIT) validates its GameSettings service the
//    same way (ShroudForge, modules/game-runtime/native/named-services/resolve_named_service.cpp).
//  - The day/night clock (g38_daytime::Daytime) is read for status: it takes new durations from the block every step,
//    which is the lab-observable proof that a live change took effect.
// Writes happen on the tick thread in Pre/Post, when no world job runs, exactly as the game's admin_player program
// writes the same block when an in-game admin changes the settings.
#include <windows.h>

#include <atomic>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "catalog.h"
#include "config.h"
#include "game.h"
#include "kiln_api.h"
#include "safetyhook.hpp"
#include "services/kiln_settings.h"

namespace {

using namespace kset;

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Layout L;
TypeByHashFn g_type_by_hash = nullptr;
SafetyHookInline g_hook;
uintptr_t g_image = 0;

constexpr const char* kTypeName = "keen::ecs::GameSettings";
constexpr const char* kSingletonName = "ecs::GameSettings";
constexpr const char* kDaytimeName = "g38_daytime::Daytime";

// ---- shared with the hook (GameServer thread = tick thread) and with any-thread readers ----
std::atomic<uintptr_t> g_instance{0};
std::atomic<bool> g_ready{false};
std::atomic<uint32_t> g_count{0};  // catalog published (release) once, never changed afterwards
std::atomic<uint64_t> g_revision{0}, g_sets{0}, g_resends_requested{0}, g_resends_sent{0};
std::atomic<bool> g_pending_resend{false};
std::atomic<bool> g_verified{false};
std::atomic<int32_t> g_preset{-1};
std::atomic<bool> g_hook_thread_warned{false};

struct DaytimeCache {
    bool valid = false;
    int64_t day = 0, night = 0, tod = 0;
    uint32_t changes = 0;
};
std::mutex g_daytime_mutex;
DaytimeCache g_daytime;

std::string g_config_path;   // UTF-8; empty on a game client (no server config)
std::wstring g_config_wpath;
bool g_client = false;       // a game client hosting its own world: no enshrouded_server.json

// ---- tick-thread state ----
struct Setting {
    std::string name, group, effect;
    uint32_t kind = 0, flags = 0, offset = 0, width = 0;
    bool is_signed = false;
    double min = 0, max = 0;
    KilnSettingValue def{};
    std::vector<Option> options;
};
std::vector<Setting> g_settings;  // built once
uint32_t g_block_size = 0;
uintptr_t g_live_instance = 0, g_block = 0;
std::vector<uint8_t> g_initial, g_last;
uint64_t g_step = 0, g_next_try = 0;
std::string g_last_init_error;

thread_local std::string t_error;

// ------------------------------------------------------------------------------------------------ helpers
void logf(int32_t level, const char* fmt, ...) {
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n <= 0) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof buf) ? n : sizeof buf - 1)});
}

KilnResult fail(KilnResult rc, std::string why) {
    t_error = std::move(why);
    return rc;
}

KilnResult copy_out(const std::string& s, char* buf, size_t cap, size_t* out_len) {
    if (out_len) *out_len = s.size();
    if (!buf || cap == 0) return KILN_OK;
    const size_t n = s.size() < cap - 1 ? s.size() : cap - 1;
    std::memcpy(buf, s.data(), n);
    buf[n] = '\0';
    return n == s.size() ? KILN_OK : KILN_ERR_BUFFER_TOO_SMALL;
}

std::string str(KilnStr s) { return s.ptr ? std::string(s.ptr, s.len) : std::string(); }
KilnStr ks(const std::string& s) { return KilnStr{s.data(), s.size()}; }

bool ieq(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x += 32;
        if (y >= 'A' && y <= 'Z') y += 32;
        if (x != y) return false;
    }
    return true;
}

std::string num(double v) {
    char b[64];
    std::snprintf(b, sizeof b, "%.6g", v);
    return b;
}

const Option* option_of(const Setting& s, int64_t value) {
    for (const Option& o : s.options)
        if (o.value == value) return &o;
    return nullptr;
}

// ---- value <-> bytes ----
KilnSettingValue decode(const Setting& s, const uint8_t* p) {
    KilnSettingValue v{};
    v.kind = s.kind;
    switch (s.kind) {
        case KILN_SETTING_BOOL: v.integer = p[0] != 0; break;
        case KILN_SETTING_FLOAT: {
            float f;
            std::memcpy(&f, p, 4);
            v.number = f;
            v.integer = std::isfinite(f) ? static_cast<int64_t>(f) : 0;
            return v;
        }
        case KILN_SETTING_DURATION: std::memcpy(&v.integer, p, 8); break;
        default: {  // INT / ENUM
            uint64_t u = 0;
            std::memcpy(&u, p, s.width);
            if (s.is_signed && s.width < 8) {
                const uint64_t sign = uint64_t{1} << (s.width * 8 - 1);
                if (u & sign) u |= ~((sign << 1) - 1);
            }
            v.integer = static_cast<int64_t>(u);
        }
    }
    v.number = static_cast<double>(v.integer);
    return v;
}

void encode(const Setting& s, const KilnSettingValue& v, uint8_t* p) {
    switch (s.kind) {
        case KILN_SETTING_BOOL: p[0] = v.integer ? 1 : 0; break;
        case KILN_SETTING_FLOAT: {
            const float f = static_cast<float>(v.number);
            std::memcpy(p, &f, 4);
            break;
        }
        case KILN_SETTING_DURATION: std::memcpy(p, &v.integer, 8); break;
        default: std::memcpy(p, &v.integer, s.width);
    }
}

std::string show(const Setting& s, const KilnSettingValue& v) {
    switch (s.kind) {
        case KILN_SETTING_BOOL: return v.integer ? "true" : "false";
        case KILN_SETTING_FLOAT: return num(v.number);
        case KILN_SETTING_DURATION: return num(static_cast<double>(v.integer) / 60e9) + " min";
        case KILN_SETTING_ENUM: {
            const Option* o = option_of(s, v.integer);
            return o ? o->name : std::to_string(v.integer) + " (?)";
        }
        default: return std::to_string(v.integer);
    }
}

// Normalises an incoming value to the setting's kind and checks it. false + t_error when refused.
bool accept(const Setting& s, const KilnSettingValue& in, uint32_t flags, KilnSettingValue& out) {
    out = KilnSettingValue{};
    out.kind = s.kind;
    const bool given_float = in.kind == KILN_SETTING_FLOAT || (in.kind == 0 && s.kind == KILN_SETTING_FLOAT);
    if (in.kind > KILN_SETTING_DURATION) return t_error = s.name + ": unknown value kind", false;
    if (given_float && !std::isfinite(in.number)) return t_error = s.name + ": not a finite number", false;
    const bool any = (flags & KILN_SETTINGS_SET_ANY_VALUE) != 0;
    if ((s.flags & KILN_SETTING_F_UNKNOWN) && !any)
        return t_error = s.name + ": not a setting Kiln knows on this game build (needs SET_ANY_VALUE)", false;
    if (s.kind == KILN_SETTING_FLOAT) {
        const double x = given_float ? in.number : static_cast<double>(in.integer);
        const double lo = any ? 0.0 : s.min, hi = any ? 100.0 : s.max;
        if (x < lo - 1e-6 || x > hi + 1e-6) return t_error = s.name + ": allowed " + num(lo) + " to " + num(hi), false;
        out.number = x;
        out.integer = static_cast<int64_t>(x);
        return true;
    }
    int64_t i = in.integer;
    if (given_float) {
        if (in.number != std::floor(in.number) || std::fabs(in.number) > 9e15)
            return t_error = s.name + ": needs a whole number", false;
        i = static_cast<int64_t>(in.number);
    }
    out.integer = i;
    out.number = static_cast<double>(i);
    switch (s.kind) {
        case KILN_SETTING_BOOL:
            if (i != 0 && i != 1) return t_error = s.name + ": on or off (1 or 0)", false;
            return true;
        case KILN_SETTING_ENUM: {
            if (option_of(s, i)) return true;
            std::string all;
            for (const Option& o : s.options) all += (all.empty() ? "" : ", ") + o.name + " (" + std::to_string(o.value) + ")";
            return t_error = s.name + ": one of " + all, false;
        }
        case KILN_SETTING_DURATION: {
            const double lo = any ? 1e9 : s.min, hi = any ? 86400e9 : s.max;
            if (static_cast<double>(i) < lo || static_cast<double>(i) > hi)
                return t_error = s.name + ": allowed " + num(lo / 60e9) + " to " + num(hi / 60e9) + " minutes (" +
                                 std::to_string(static_cast<int64_t>(lo)) + " to " +
                                 std::to_string(static_cast<int64_t>(hi)) + " ns)",
                       false;
            return true;
        }
        default: {  // INT
            double lo = s.min, hi = s.max;
            if (any) {
                lo = s.is_signed ? -std::ldexp(1.0, static_cast<int>(s.width * 8 - 1)) : 0.0;
                hi = s.is_signed ? std::ldexp(1.0, static_cast<int>(s.width * 8 - 1)) - 1
                                 : std::ldexp(1.0, static_cast<int>(s.width * 8)) - 1;
            }
            if (static_cast<double>(i) < lo || static_cast<double>(i) > hi)
                return t_error = s.name + ": allowed " + num(lo) + " to " + num(hi), false;
            return true;
        }
    }
}

// A value the block may plausibly hold (layout sanity check before kiln.settings writes anything).
bool plausible(const Setting& s, const KilnSettingValue& v) {
    switch (s.kind) {
        case KILN_SETTING_BOOL: return v.integer == 0 || v.integer == 1;
        case KILN_SETTING_FLOAT: return std::isfinite(v.number) && v.number >= 0 && v.number <= 1000;
        case KILN_SETTING_ENUM: return option_of(s, v.integer) != nullptr;
        case KILN_SETTING_DURATION: return v.integer > 0 && v.integer <= 864000000000000;  // 10 days
        default: return true;
    }
}

bool on_tick() { return g_api->on_tick_thread(); }

// The server world (the instance's own pointer, which the core's server_world() must agree with when it knows one) and
// a singleton in it (core lookup; 0 when the core's feature "singletons" is off).
uintptr_t world_of(uintptr_t instance) {
    uintptr_t world = 0;
    if (L.inst_world < 0 || !rd(instance + L.inst_world, world) || !world) return 0;
    const uintptr_t core = g_api->server_world();
    return core && core != world ? 0 : world;
}

uintptr_t singleton(uintptr_t world, const char* name) {
    return world ? g_api->singleton(world, KilnStr{name, std::strlen(name)}) : 0;
}

// ------------------------------------------------------------------------------------------------ catalog
void build_catalog(const Reflected& R) {
    for (const Field& f : R.fields) {
        Setting s;
        s.name = f.name;
        s.kind = f.kind;
        s.offset = f.offset;
        s.width = f.width;
        s.is_signed = f.is_signed;
        s.options = f.options;
        const Known* k = known(f.name.c_str());
        if (!f.kind) {
            logf(KILN_LOG_WARN, "settings: field %s (%s, +0x%x) has a type kiln.settings cannot read; left out",
                 f.name.c_str(), f.type_name.c_str(), f.offset);
            continue;
        }
        if (k && k->kind == f.kind) {
            s.group = k->group;
            s.effect = k->effect;
            s.flags = k->flags;
            if (f.kind == KILN_SETTING_FLOAT || f.kind == KILN_SETTING_INT || f.kind == KILN_SETTING_DURATION) {
                s.flags |= KILN_SETTING_F_RANGE;
                s.min = k->min, s.max = k->max;
            }
            s.def.kind = f.kind;
            if (f.kind == KILN_SETTING_ENUM) {
                for (const Option& o : f.options)
                    if (k->def_option && o.name == k->def_option) s.def.integer = o.value, s.def.number = double(o.value);
            } else {
                s.def.number = k->def;
                s.def.integer = static_cast<int64_t>(k->def);
            }
        } else {
            if (k) logf(KILN_LOG_WARN, "settings: %s has kind %u in this game build, Kiln expected %u: treated as unknown",
                        f.name.c_str(), f.kind, k->kind);
            else logf(KILN_LOG_INFO, "settings: %s (%s, +0x%x) is new to Kiln: readable, writable only with SET_ANY_VALUE",
                      f.name.c_str(), f.type_name.c_str(), f.offset);
            s.group = "Other";
            s.effect = "unknown: not documented for this game build";
            s.flags = KILN_SETTING_F_UNKNOWN;
        }
        if (f.kind == KILN_SETTING_BOOL) s.min = 0, s.max = 1;
        if (f.kind == KILN_SETTING_ENUM && !f.options.empty()) {
            s.min = s.max = static_cast<double>(f.options[0].value);
            for (const Option& o : f.options) {
                s.min = std::fmin(s.min, static_cast<double>(o.value));
                s.max = std::fmax(s.max, static_cast<double>(o.value));
            }
        }
        g_settings.push_back(std::move(s));
    }
    for (uint32_t i = 0; i < known_count(); ++i) {
        const Known* k = known_at(i);
        bool found = false;
        for (const Setting& s : g_settings) found = found || s.name == k->name;
        if (!found) logf(KILN_LOG_WARN, "settings: %s is not in this game build's GameSettings", k->name);
    }
}

// ------------------------------------------------------------------------------------------------ live block
bool read_block(std::vector<uint8_t>& out) {
    out.resize(g_block_size);
    return g_block && read(g_block, out.data(), out.size());
}

// The day/night clock, read every step (four fields); its address is looked up again every 25 steps.
uintptr_t g_daytime_obj = 0;
uint64_t g_daytime_lookup = 0;

void probe_daytime(uintptr_t instance) {
    DaytimeCache c;
    if (L.daytime) {
        if (!g_daytime_obj || g_step >= g_daytime_lookup) {
            g_daytime_obj = singleton(world_of(instance), kDaytimeName);
            g_daytime_lookup = g_step + 25;
        }
        const uintptr_t dt = g_daytime_obj;
        c.valid = dt && rd(dt + L.dt_day, c.day) && rd(dt + L.dt_night, c.night) && rd(dt + L.dt_tod, c.tod) &&
                  rd(dt + L.dt_changes, c.changes);
        if (!c.valid) g_daytime_obj = 0;
    }
    std::lock_guard lock(g_daytime_mutex);
    g_daytime = c;
}

void unready(const char* why) {
    if (g_ready.exchange(false)) logf(KILN_LOG_WARN, "settings: live block lost (%s); kiln.settings waits for it", why);
    g_block = 0;
    g_next_try = g_step + 25;
}

void try_init(uintptr_t instance) {
    g_next_try = g_step + 25;  // about once a second while it fails
    auto give_up = [&](std::string why) {
        if (why != g_last_init_error) logf(KILN_LOG_WARN, "settings: not ready: %s", why.c_str());
        g_last_init_error = std::move(why);
    };
    uintptr_t block = 0;
    if (!rd(instance + L.inst_settings, block) || !block) return give_up("the GameServer instance has no settings block yet");
    if (g_count.load(std::memory_order_acquire) == 0) {
        const uintptr_t type = type_by_hash(g_type_by_hash, fnv1a(kTypeName));
        Reflected R;
        std::string err;
        if (!reflect(L, type, kTypeName, R, err)) return give_up(std::string("reflection of ") + kTypeName + ": " + err);
        g_block_size = R.size;
        build_catalog(R);
        if (g_settings.empty()) return give_up("no readable field in GameSettings");
        g_count.store(static_cast<uint32_t>(g_settings.size()), std::memory_order_release);
        logf(KILN_LOG_INFO, "settings: reflection: %s is %u bytes, %zu fields, %zu usable", kTypeName, R.size,
             R.fields.size(), g_settings.size());
    }
    g_block = block;
    std::vector<uint8_t> snap;
    if (!read_block(snap)) {
        g_block = 0;
        return give_up("settings block unreadable");
    }
    for (const Setting& s : g_settings) {
        const KilnSettingValue v = decode(s, snap.data() + s.offset);
        if (!plausible(s, v)) {
            g_block = 0;
            return give_up("the block does not look like GameSettings (" + s.name + " = " + show(s, v) +
                           "): changes stay off on this build");
        }
    }
    // Cross-check: the block must be the world's registered ecs::GameSettings, the object game systems are bound to.
    const uintptr_t world = world_of(instance);
    const char* check = "not checked (world or singleton map unavailable)";
    g_verified.store(false);
    if (world) {
        const uintptr_t obj = singleton(world, kSingletonName);
        if (obj && obj != block) {
            g_block = 0;
            return give_up("the instance's block differs from the world singleton ecs::GameSettings; changes stay off");
        }
        if (obj == block) g_verified.store(true), check = "same block";
        else check = "not found in the world map";
    }
    uint8_t preset = 0xFF;
    if (L.inst_preset >= 0 && rd(instance + L.inst_preset, preset) && preset <= 4) g_preset.store(preset);
    if (g_live_instance == instance && !g_initial.empty()) {
        // the same world came back (block moved): keep the world's own values from the first read
    } else {
        g_initial = snap;
    }
    g_last = snap;
    g_live_instance = instance;
    g_last_init_error.clear();
    probe_daytime(instance);
    g_ready.store(true, std::memory_order_release);
    static const char* kPresets[] = {"Default", "Relaxed", "Hard", "Survival", "Custom"};
    const int32_t p = g_preset.load();
    logf(KILN_LOG_INFO,
         "settings: ready: %zu settings in the %u-byte block at %p (instance %p, world %p); world singleton %s: %s; "
         "preset %s; resend %s",
         g_settings.size(), g_block_size, reinterpret_cast<void*>(block), reinterpret_cast<void*>(instance),
         reinterpret_cast<void*>(world), kSingletonName, check, p >= 0 ? kPresets[p] : "?",
         L.inst_resend >= 0 ? "on" : "off (players keep old values until they reconnect)");
    std::string line;
    for (const Setting& s : g_settings) {
        line += (line.empty() ? "" : ", ") + s.name + "=" + show(s, decode(s, snap.data() + s.offset));
        if (line.size() > 1500) {
            logf(KILN_LOG_INFO, "settings: values: %s", line.c_str());
            line.clear();
        }
    }
    if (!line.empty()) logf(KILN_LOG_INFO, "settings: values: %s", line.c_str());
}

// Changes made by someone else (the game's admin menu, another tool): log them and bump the revision.
void watch() {
    std::vector<uint8_t> cur;
    if (!read_block(cur)) return unready("block unreadable");
    if (std::memcmp(cur.data(), g_last.data(), cur.size()) == 0) return;
    for (const Setting& s : g_settings) {
        if (std::memcmp(cur.data() + s.offset, g_last.data() + s.offset, s.width) == 0) continue;
        logf(KILN_LOG_INFO, "settings: %s changed outside kiln.settings (in-game admin menu?): %s -> %s", s.name.c_str(),
             show(s, decode(s, g_last.data() + s.offset)).c_str(), show(s, decode(s, cur.data() + s.offset)).c_str());
    }
    g_last = cur;
    g_revision.fetch_add(1);
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) {
    ++g_step;
    const uintptr_t instance = g_instance.load(std::memory_order_acquire);
    if (!instance) return;
    if (!g_ready.load(std::memory_order_relaxed) || instance != g_live_instance) {
        if (g_ready.load() && instance != g_live_instance) unready("the GameServer instance changed");
        if (g_step >= g_next_try) try_init(instance);
        return;
    }
    uintptr_t block = 0;
    if (!rd(instance + L.inst_settings, block) || block != g_block) return unready("the instance points elsewhere");
    watch();
    probe_daytime(instance);
}

// The game closes the server world (a client leaving its hosted game; the next one may reuse the addresses): the
// instance, the block and the world's original values belong to that world.
void drop_world(const char* why) {
    g_instance.store(0, std::memory_order_release);
    unready(why);
    g_live_instance = 0;
    g_initial.clear();
    g_last.clear();
    g_daytime_obj = 0;
    g_pending_resend.store(false);
    g_verified.store(false);
    {
        std::lock_guard lock(g_daytime_mutex);
        g_daytime = {};
    }
    logf(KILN_LOG_INFO, "settings: server world closed (%s); waiting for the next GameServer", why);
}

bool g_closed = false;  // tick thread

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER) return;
    if (ev->event == KILN_WORLD_STOPPING) {
        drop_world("the game is closing it");
        g_closed = true;
    } else if (ev->event == KILN_WORLD_STARTED) {
        if (ev->session > 1 && !g_closed) drop_world("a new server world started");
        g_closed = false;
    }
}

bool request_resend() {
    if (L.inst_resend < 0 || !g_live_instance) return false;
    const uint8_t one = 1;
    if (!write(g_live_instance + L.inst_resend, &one, 1)) return false;
    g_resends_requested.fetch_add(1);
    g_pending_resend.store(true);
    return true;
}

// SendServerInfo(GameServer instance): runs on the GameServer thread after the world stepped. kiln.settings only
// learns the instance here and counts the sends it asked for; the original runs unchanged.
uintptr_t __fastcall hk_send_server_info(uintptr_t instance, uintptr_t a2, uintptr_t a3, uintptr_t a4) {
    if (instance && instance != g_instance.load(std::memory_order_relaxed)) {
        g_instance.store(instance, std::memory_order_release);
        logf(KILN_LOG_INFO, "settings: GameServer instance %p (SendServerInfo)", reinterpret_cast<void*>(instance));
        if (!g_api->on_tick_thread() && !g_hook_thread_warned.exchange(true))
            logf(KILN_LOG_WARN, "settings: SendServerInfo runs on thread %lu, not on the tick thread", GetCurrentThreadId());
    }
    uint8_t before = 0;
    const bool have = instance && L.inst_resend >= 0 && rd(instance + L.inst_resend, before);
    const uintptr_t result = g_hook.call<uintptr_t>(instance, a2, a3, a4);
    if (have && before) {
        uint8_t after = 1;
        if (rd(instance + L.inst_resend, after) && !after) {
            const uint64_t n = g_resends_sent.fetch_add(1) + 1;
            if (g_pending_resend.exchange(false))
                logf(KILN_LOG_INFO, "settings: the game sent the settings block to the players (send #%llu)",
                     static_cast<unsigned long long>(n));
        }
    }
    return result;
}

// ------------------------------------------------------------------------------------------------ table
KilnResult guard_ready(bool need_tick = true) {
    if (need_tick && !on_tick()) return fail(KILN_ERR_WRONG_THREAD, "kiln.settings: call on the tick thread");
    if (!g_ready.load(std::memory_order_acquire) || !g_block)
        return fail(KILN_ERR_UNAVAILABLE, g_last_init_error.empty() ? "kiln.settings: the live settings are not found yet"
                                                                    : "kiln.settings: " + g_last_init_error);
    return KILN_OK;
}

bool live_value(const Setting& s, KilnSettingValue& out) {
    uint8_t buf[8] = {};
    if (!read(g_block + s.offset, buf, s.width)) return false;
    out = decode(s, buf);
    return true;
}

KilnResult KILN_CALL api_status(KilnSettingsStatus* out) {
    if (!out || out->size < sizeof(KilnSettingsStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->ready = g_ready.load() ? 1 : 0;
    out->resend = L.inst_resend >= 0 ? 1 : 0;
    out->verified = g_verified.load() ? 1 : 0;
    out->count = g_count.load(std::memory_order_acquire);
    out->revision = g_revision.load();
    out->sets = g_sets.load();
    out->resends_requested = g_resends_requested.load();
    out->resends_sent = g_resends_sent.load();
    out->preset = g_preset.load();
    {
        std::lock_guard lock(g_daytime_mutex);
        out->daytime = g_daytime.valid ? 1 : 0;
        out->day_duration_ns = g_daytime.day;
        out->night_duration_ns = g_daytime.night;
        out->time_of_day_ns = g_daytime.tod;
        out->daytime_changes = g_daytime.changes;
    }
    out->config_path = ks(g_config_path);
    return KILN_OK;
}

uint32_t KILN_CALL api_count() { return g_count.load(std::memory_order_acquire); }

KilnResult KILN_CALL api_info(uint32_t index, KilnSettingInfo* out) {
    if (!out || out->size < sizeof(KilnSettingInfo)) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = guard_ready(); rc != KILN_OK) return rc;
    if (index >= g_settings.size()) return KILN_ERR_NOT_FOUND;
    const Setting& s = g_settings[index];
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->index = index;
    out->name = ks(s.name);
    out->group = ks(s.group);
    out->effect = ks(s.effect);
    out->kind = s.kind;
    out->offset = s.offset;
    out->option_count = static_cast<uint32_t>(s.options.size());
    out->min = s.min;
    out->max = s.max;
    if (!live_value(s, out->value)) return fail(KILN_ERR_INTERNAL, "settings block unreadable");
    out->initial = decode(s, g_initial.data() + s.offset);
    out->default_value = s.def;
    out->flags = s.flags;
    uint8_t now[8] = {};
    encode(s, out->value, now);
    if (std::memcmp(now, g_initial.data() + s.offset, s.width) != 0) out->flags |= KILN_SETTING_F_CHANGED;
    return KILN_OK;
}

KilnResult KILN_CALL api_find(KilnStr name, uint32_t* out_index) {
    const uint32_t n = g_count.load(std::memory_order_acquire);
    const std::string want = str(name);
    for (uint32_t i = 0; i < n; ++i)
        if (ieq(g_settings[i].name, want)) {
            if (out_index) *out_index = i;
            return KILN_OK;
        }
    return n ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
}

KilnResult KILN_CALL api_option(uint32_t index, uint32_t ordinal, KilnStr* out_name, int64_t* out_value) {
    const uint32_t n = g_count.load(std::memory_order_acquire);
    if (index >= n || ordinal >= g_settings[index].options.size()) return n ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
    const Option& o = g_settings[index].options[ordinal];
    if (out_name) *out_name = ks(o.name);
    if (out_value) *out_value = o.value;
    return KILN_OK;
}

KilnResult KILN_CALL api_get(uint32_t index, KilnSettingValue* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    if (KilnResult rc = guard_ready(); rc != KILN_OK) return rc;
    if (index >= g_settings.size()) return KILN_ERR_NOT_FOUND;
    return live_value(g_settings[index], *out) ? KILN_OK : fail(KILN_ERR_INTERNAL, "settings block unreadable");
}

// Writes one field (already validated). Updates the watcher's snapshot so the change is not reported as the game's.
KilnResult write_field(const Setting& s, const KilnSettingValue& v, KilnSettingValue* before, bool& changed) {
    uint8_t old_bytes[8] = {}, new_bytes[8] = {}, check[8] = {};
    if (!read(g_block + s.offset, old_bytes, s.width)) return fail(KILN_ERR_INTERNAL, s.name + ": block unreadable");
    if (before) *before = decode(s, old_bytes);
    encode(s, v, new_bytes);
    changed = std::memcmp(old_bytes, new_bytes, s.width) != 0;
    if (!changed) return KILN_OK;
    if (!write(g_block + s.offset, new_bytes, s.width) || !read(g_block + s.offset, check, s.width) ||
        std::memcmp(check, new_bytes, s.width) != 0) {
        write(g_block + s.offset, old_bytes, s.width);
        return fail(KILN_ERR_INTERNAL, s.name + ": the write did not stick; old value restored");
    }
    std::memcpy(g_last.data() + s.offset, new_bytes, s.width);
    g_sets.fetch_add(1);
    g_revision.fetch_add(1);
    return KILN_OK;
}

KilnResult KILN_CALL api_set(KilnOwner owner, uint32_t index, const KilnSettingValue* value, uint32_t flags,
                             KilnSettingValue* out_before) {
    t_error.clear();
    if (!value) return fail(KILN_ERR_INVALID_ARGUMENT, "no value");
    if (flags & ~(KILN_SETTINGS_SET_NO_RESEND | KILN_SETTINGS_SET_ANY_VALUE)) return fail(KILN_ERR_INVALID_ARGUMENT, "unknown flags");
    if (KilnResult rc = guard_ready(); rc != KILN_OK) return rc;
    if (index >= g_settings.size()) return fail(KILN_ERR_NOT_FOUND, "no setting with index " + std::to_string(index));
    const Setting& s = g_settings[index];
    KilnSettingValue v;
    if (!accept(s, *value, flags, v)) {
        logf(KILN_LOG_INFO, "settings: refused: %s", t_error.c_str());
        return KILN_ERR_INVALID_ARGUMENT;
    }
    KilnSettingValue before{};
    bool changed = false;
    if (KilnResult rc = write_field(s, v, &before, changed); rc != KILN_OK) {
        logf(KILN_LOG_ERROR, "settings: %s", t_error.c_str());
        return rc;
    }
    if (out_before) *out_before = before;
    if (!changed) return KILN_OK;
    const bool resend = !(flags & KILN_SETTINGS_SET_NO_RESEND) && request_resend();
    logf(KILN_LOG_INFO, "settings: %s %s -> %s (owner %llu)%s", s.name.c_str(), show(s, before).c_str(),
         show(s, v).c_str(), static_cast<unsigned long long>(owner),
         resend ? "; resend to players requested" : (flags & KILN_SETTINGS_SET_NO_RESEND) ? "; no resend yet" : "");
    return KILN_OK;
}

KilnResult KILN_CALL api_reset(KilnOwner owner, uint32_t index, uint32_t flags) {
    t_error.clear();
    if (KilnResult rc = guard_ready(); rc != KILN_OK) return rc;
    if (index != UINT32_MAX && index >= g_settings.size()) return fail(KILN_ERR_NOT_FOUND, "no such setting");
    size_t n = 0;
    for (uint32_t i = 0; i < g_settings.size(); ++i) {
        if (index != UINT32_MAX && i != index) continue;
        const Setting& s = g_settings[i];
        KilnSettingValue before{};
        bool changed = false;
        if (KilnResult rc = write_field(s, decode(s, g_initial.data() + s.offset), &before, changed); rc != KILN_OK)
            return rc;
        if (changed) {
            ++n;
            logf(KILN_LOG_INFO, "settings: %s %s -> %s (reset to the world's own value, owner %llu)", s.name.c_str(),
                 show(s, before).c_str(), show(s, decode(s, g_initial.data() + s.offset)).c_str(),
                 static_cast<unsigned long long>(owner));
        }
    }
    if (n && !(flags & KILN_SETTINGS_SET_NO_RESEND)) request_resend();
    return KILN_OK;
}

KilnResult KILN_CALL api_resend() {
    if (KilnResult rc = guard_ready(); rc != KILN_OK) return rc;
    if (!request_resend()) return fail(KILN_ERR_UNAVAILABLE, "the game's send flag is not known on this build");
    logf(KILN_LOG_INFO, "settings: resend to players requested");
    return KILN_OK;
}

KilnResult KILN_CALL api_save(uint32_t flags, char* message, size_t capacity, size_t* out_len) {
    t_error.clear();
    if (flags) return fail(KILN_ERR_INVALID_ARGUMENT, "unknown flags");
    if (KilnResult rc = guard_ready(); rc != KILN_OK) return rc;
    if (g_client)
        return fail(KILN_ERR_UNAVAILABLE,
                    "client-hosted world: there is no enshrouded_server.json; the world's settings are part of its save "
                    "(the game's world settings); live changes still apply to the running world");
    if (g_config_wpath.empty()) return fail(KILN_ERR_UNAVAILABLE, "the server folder is unknown");
    std::vector<std::pair<std::string, Json>> values;
    for (const Setting& s : g_settings) {
        KilnSettingValue v;
        if (!live_value(s, v)) return fail(KILN_ERR_INTERNAL, "settings block unreadable");
        Json j;
        switch (s.kind) {
            case KILN_SETTING_BOOL: j = v.integer != 0; break;
            case KILN_SETTING_FLOAT: {
                const double r = std::round(v.number * 1e6) / 1e6;  // float32 -> the decimal it was set from
                if (r == std::floor(r) && std::fabs(r) < 1e15) j = static_cast<int64_t>(r);
                else j = r;
                break;
            }
            case KILN_SETTING_ENUM: {
                const Option* o = option_of(s, v.integer);
                if (o) j = o->name;
                else j = v.integer;
                break;
            }
            default: j = v.integer;
        }
        values.emplace_back(s.name, std::move(j));
    }
    std::string msg;
    const bool ok = save_config(g_config_wpath, values, msg);
    logf(ok ? KILN_LOG_INFO : KILN_LOG_ERROR, "settings: save: %s", msg.c_str());
    copy_out(msg, message, capacity, out_len);  // a truncated message does not undo the save
    if (!ok) return fail(KILN_ERR_INTERNAL, msg);
    return KILN_OK;
}

KilnResult KILN_CALL api_last_error(char* buffer, size_t capacity, size_t* out_len) {
    return copy_out(t_error, buffer, capacity, out_len);
}

const KilnSettingsApi kTable = {
    {sizeof(KilnSettingsApi), KILN_SETTINGS_VERSION},
    &api_status, &api_count, &api_info, &api_find, &api_option, &api_get, &api_set, &api_reset, &api_resend,
    &api_save, &api_last_error,
};

// ------------------------------------------------------------------------------------------------ boot
bool constant(const char* name, int64_t& out) {
    return g_api->constant(KilnStr{name, std::strlen(name)}, &out) == KILN_OK;
}
bool symbol(const char* name, uintptr_t& out) { return g_api->symbol(KilnStr{name, std::strlen(name)}, &out) == KILN_OK; }
bool feature(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

bool load_layout() {
    bool ok = constant("settings/instance.settings", L.inst_settings);
    const char* names[] = {"settings/type.hash", "settings/type.qname", "settings/type.inner", "settings/type.size",
                           "settings/type.count", "settings/type.kind", "settings/type.fields",
                           "settings/type.enum_values", "settings/field.stride", "settings/field.type",
                           "settings/field.offset", "settings/enum.stride", "settings/enum.value"};
    int64_t* slots[] = {&L.type_hash, &L.type_qname, &L.type_inner, &L.type_size, &L.type_count, &L.type_kind,
                        &L.type_fields, &L.type_enum_values, &L.field_stride, &L.field_type, &L.field_offset,
                        &L.enum_stride, &L.enum_value};
    for (size_t i = 0; i < sizeof names / sizeof names[0]; ++i) ok = constant(names[i], *slots[i]) && ok;
    if (!ok) return false;
    if (!feature("settings.resend") || !constant("settings/instance.resend", L.inst_resend)) L.inst_resend = -1;
    if (!constant("settings/instance.preset", L.inst_preset)) L.inst_preset = -1;
    if (!feature("settings.world") || !constant("settings/instance.world", L.inst_world)) L.inst_world = -1;
    L.daytime = feature("settings.daytime") && constant("settings/daytime.day", L.dt_day) &&
                constant("settings/daytime.night", L.dt_night) && constant("settings/daytime.changes", L.dt_changes) &&
                constant("settings/daytime.tod", L.dt_tod);
    return true;
}

void init_paths() {
    if (g_client) return;  // a client-hosted world has no server config file
    char buf[1024];
    size_t n = 0;
    if (g_api->path(KILN_PATH_GAME, buf, sizeof buf, &n) != KILN_OK || n == 0 || n >= sizeof buf) return;
    g_config_path.assign(buf, n);
    if (g_config_path.back() != '\\' && g_config_path.back() != '/') g_config_path += '\\';
    g_config_path += "enshrouded_server.json";
    const int w = MultiByteToWideChar(CP_UTF8, 0, g_config_path.data(), static_cast<int>(g_config_path.size()), nullptr, 0);
    g_config_wpath.assign(static_cast<size_t>(w), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, g_config_path.data(), static_cast<int>(g_config_path.size()), g_config_wpath.data(), w);
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{KILN_SETTINGS_SERVICE, sizeof(KILN_SETTINGS_SERVICE) - 1};
    info->version = KilnStr{"1", 1};
    g_image = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    uintptr_t send = 0, type_by_hash_fn = 0;
    if (!feature("settings.live") || !symbol("settings/SendServerInfo", send) ||
        !symbol("settings/TypeByHash", type_by_hash_fn) || !load_layout()) {
        logf(KILN_LOG_WARN, "settings: unavailable on this game build (sigdb feature settings.live is off)");
        return KILN_ERR_UNAVAILABLE;
    }
    g_type_by_hash = reinterpret_cast<TypeByHashFn>(type_by_hash_fn);
    g_client = api->side() == KILN_SIDE_CLIENT;
    init_paths();
    g_hook = safetyhook::create_inline(reinterpret_cast<void*>(send), reinterpret_cast<void*>(&hk_send_server_info));
    if (!g_hook) {
        logf(KILN_LOG_ERROR, "settings: hooking SendServerInfo at %p failed; unavailable", reinterpret_cast<void*>(send));
        return KILN_ERR_UNAVAILABLE;
    }
    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    char flag[32] = "unknown";
    if (L.inst_resend >= 0) std::snprintf(flag, sizeof flag, "instance+0x%llx", static_cast<unsigned long long>(L.inst_resend));
    logf(KILN_LOG_INFO,
         "settings: SendServerInfo hooked (rva 0x%llx); block at instance+0x%llx, send flag %s, world check %s, "
         "day/night probe %s; config %s",
         static_cast<unsigned long long>(send - g_image), static_cast<unsigned long long>(L.inst_settings), flag,
         L.inst_world >= 0 && feature("singletons") ? "on" : "off", L.daytime ? "on" : "off",
         g_client ? "none (client-hosted world: the settings live in the world save)"
                  : (g_config_path.empty() ? "?" : g_config_path.c_str()));
    return api->register_service(owner, KilnStr{KILN_SETTINGS_SERVICE, sizeof(KILN_SETTINGS_SERVICE) - 1}, &kTable);
}
