// SPDX-License-Identifier: MIT
// kiln.vfx: the queue. Requests from any thread become a handle in PENDING. The game is only called from core_update, which
// runs inside the game's own FxController update: the code that makes the game's VFX calls, with its access rights. Nothing
// in the VFX path takes a lock, so a call from anywhere else could corrupt its free lists (docs/re/vfx.md 4.1).
//
// An entry's life: PENDING -> (Kiln's check) -> FxPlayAt or FxPlayAttached -> PLAYING -> STOPPED, or FAILED if it never started.
// Each update asks the FxController whether it still holds each live handle, applies stop and parameter requests, and ends an
// attached effect whose entity is gone (the game leaves that to its own owner of the handle, here Kiln).
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "core.h"

namespace kvx {

namespace {

constexpr uint32_t kMaxLive = 1024;
constexpr uint32_t kMaxStartsPerUpdate = 64;
constexpr uint64_t kPendingTimeoutMs = 10000;
constexpr uint64_t kKeepFinishedMs = 60000;
constexpr uint64_t kNoInstanceMs = 5000;
constexpr int64_t kOneShotMinNs = 1'000'000'000;
constexpr int64_t kOneShotMaxNs = 60'000'000'000;  // the game's own one-shot wrappers cap at 60 s (0x2a0b10, 0x2a0b80)
constexpr uint32_t kFloatType = 0x90b5085du;       // DsTypeId of a float parameter (radius, duration, speed; [data])
constexpr float kDefaultVerifyRadius = 10.f;
constexpr size_t kMaxParams = 16, kMaxParamBytes = 16;
constexpr int kLogFirst = 40;  // info lines for the first N plays, ends, failures; then debug
constexpr size_t kMaxRefusalLines = 256;

struct ParamOp {
    uint32_t hash, type;
    uint8_t size;
    uint8_t data[kMaxParamBytes];
};

struct Entry {
    KilnVfx id = 0;
    KilnOwner owner = 0;
    uint32_t flags = 0;
    uint32_t state = KILN_VFX_PENDING;
    uint32_t reason = KILN_VFX_END_NONE;
    uint32_t info_flags = 0;
    uint8_t guid[16] = {};
    KilnVec3 position{};
    float rotation[4] = {0, 0, 0, 1};
    float scale = 1.f;
    KilnEntity entity = 0;
    uint8_t slot = 0;
    float local[3] = {}, world[3] = {};
    float orot[4] = {0, 0, 0, 1};
    float verify_radius = kDefaultVerifyRadius;
    int64_t lifetime_ns = 0;  // 0 until start() settles the one-shot's length
    void* fxc = nullptr;
    uint32_t fx = 0;
    uint32_t instance = 0;
    uint32_t thread = 0;
    uint64_t queued_ms = 0, started_ms = 0, ended_ms = 0;
    bool stop_req = false, stop_immediate = false;
    std::vector<ParamOp> params;
};

std::mutex g_mu;  // everything below
std::map<KilnVfx, Entry> g_entries;  // by id: requests start in call order
std::set<std::string> g_preload_done;
std::vector<std::string> g_preload_queue;
KilnVfx g_next = 1;
uint32_t g_live = 0;
uint64_t g_played = 0, g_failed = 0;
int g_logged_plays = 0, g_logged_ends = 0, g_logged_fails = 0;
uint64_t g_last_sweep = 0;
bool g_said_no_game = false;

std::atomic<Game*> g_game{nullptr};
std::atomic<void*> g_fxc{nullptr};
std::atomic<uint32_t> g_thread{0};
std::atomic<uint64_t> g_updates{0};
std::atomic<bool> g_system_said{false};
std::atomic<void (*)()> g_build_update{nullptr};
std::atomic<bool (*)(const uint8_t*)> g_build_refuses{nullptr};
std::atomic<int64_t (*)(const uint8_t*)> g_build_one_shot{nullptr};
std::atomic<uint64_t> g_refused{0};

std::mutex g_refusal_mu;
std::set<std::string> g_refusals;

bool final_state(uint32_t s) { return s == KILN_VFX_STOPPED || s == KILN_VFX_FAILED; }

const char* reason_text(uint32_t r) {
    switch (r) {
        case KILN_VFX_END_FINISHED: return "finished";
        case KILN_VFX_END_STOPPED: return "stopped";
        case KILN_VFX_END_ENTITY_GONE: return "the entity is gone";
        case KILN_VFX_END_UNKNOWN_VFX: return "unknown VFX";
        case KILN_VFX_END_START_FAILED: return "the FxController returned no handle (pool full)";
        case KILN_VFX_END_TIMEOUT: return "timed out (no FxController update: no client world, or the VFX system not ready)";
        case KILN_VFX_END_WORLD_ENDED: return "the client world ended";
        case KILN_VFX_END_FAULT: return "a game call faulted";
        case KILN_VFX_END_REFUSED: return "a test flag on a GUID the game knows";
        case KILN_VFX_END_NO_INSTANCE: return "no instance after 5 s (this body has no such slot, or the instance pool is full)";
        default: return "-";
    }
}
const char* state_text(uint32_t s) {
    switch (s) {
        case KILN_VFX_PENDING: return "pending";
        case KILN_VFX_PLAYING: return "playing";
        case KILN_VFX_STOPPED: return "stopped";
        case KILN_VFX_FAILED: return "failed";
        default: return "none";
    }
}

void finish(Entry& e, uint32_t state, uint32_t reason) {
    if (final_state(e.state)) return;
    e.state = state;
    e.reason = reason;
    e.ended_ms = now_ms();
    e.params.clear();
    if (g_live) --g_live;
    if (state == KILN_VFX_FAILED) ++g_failed;
    const bool fail = state == KILN_VFX_FAILED;
    const bool info = fail ? g_logged_fails++ < kLogFirst : g_logged_ends++ < kLogFirst;
    char g[40];
    guid_text(e.guid, g);
    if (reason == KILN_VFX_END_UNKNOWN_VFX) {
        vlog(info ? KILN_LOG_WARN : KILN_LOG_DEBUG, "vfx: #%u unknown VFX %s ignored", e.id, g);
        return;
    }
    vlog(info ? (fail ? KILN_LOG_WARN : KILN_LOG_INFO) : KILN_LOG_DEBUG, "vfx: #%u %s %s: %s", e.id, g, fail ? "FAILED" : "ended",
         reason_text(reason));
}

void normalize(float q[4]) {
    const float n = std::sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
    if (!(n > 1e-6f) || !std::isfinite(n)) {
        q[0] = q[1] = q[2] = 0.f;
        q[3] = 1.f;
        return;
    }
    for (int i = 0; i < 4; ++i) q[i] /= n;
}

double dist(const KilnVec3& a, const KilnVec3& b) {
    const double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}

FxTiming timing_of(Game& game, void* fxc, Entry& e) {
    FxTiming d{};
    if (e.lifetime_ns < 0) return d;  // until stop
    if (e.lifetime_ns == 0) {
        const auto built = g_build_one_shot.load(std::memory_order_acquire);
        e.lifetime_ns = built ? built(e.guid) : 0;
        if (e.lifetime_ns <= 0) {
            VfxLengths v;
            e.lifetime_ns = game.vfx_lengths(fxc, e.guid, &v) ? one_shot_ns(v) : kOneShotFallbackNs;
        }
    }
    d.ns = e.lifetime_ns;
    d.has = 1;
    return d;
}

// The values the VFX describes go into the block it starts with, over its defaults, and leave the entry; the rest wait for an instance
// and VfxSetParameter, which ignores what the VFX doesn't have. Returns how many went into the block.
size_t start_params(Game& game, void* fxc, Entry& e, FxParams* block) {
    if (e.params.empty()) return 0;
    VfxParamLayout l;
    if (!game.vfx_params(fxc, e.guid, &l) || !l.count) return 0;
    std::memcpy(block->data, l.defaults, sizeof block->data);
    const size_t before = e.params.size();
    std::erase_if(e.params, [&](const ParamOp& p) { return put_start_param(l, p.hash, p.type, p.data, p.size, block); });
    const size_t taken = before - e.params.size();
    if (taken) block->has = 1;
    else *block = FxParams{};
    return taken;
}

// PENDING -> the game (update thread, g_mu held).
void start(Game& game, const Frame& f, Entry& e) {
    const bool test = (e.flags & (KILN_VFX_TEST_FX | KILN_VFX_TEST_CREATE)) != 0;
    const int known = core_build_refuses(e.guid) ? 0 : game.is_registered(f.fxc, e.guid);
    const uint64_t now = now_ms();
    if (test) {
        if (known == 1) {
            finish(e, KILN_VFX_FAILED, KILN_VFX_END_REFUSED);
            return;
        }
    } else if (known == 0) {
        finish(e, KILN_VFX_FAILED, KILN_VFX_END_UNKNOWN_VFX);
        return;
    } else if (known < 0) {  // the system is not there yet: wait
        if (now - e.queued_ms > kPendingTimeoutMs) finish(e, KILN_VFX_FAILED, KILN_VFX_END_TIMEOUT);
        return;
    }
    e.thread = thread_id();
    e.fxc = f.fxc;
    char g[40];
    guid_text(e.guid, g);

    if (e.flags & KILN_VFX_TEST_CREATE) {
        const uint32_t inst = game.create_direct(f, e.guid);
        vlog(KILN_LOG_INFO, "vfx: #%u test: RenderCreateVfx %s returned %u (0 = the guard on VfxCreateInstance caught it)", e.id, g, inst);
        finish(e, inst ? KILN_VFX_STOPPED : KILN_VFX_FAILED, inst ? KILN_VFX_END_FINISHED : KILN_VFX_END_UNKNOWN_VFX);
        return;
    }

    bool attach = (e.flags & KILN_VFX_ON) != 0;
    KilnVec3 entity_at{};
    if (attach && (e.flags & KILN_VFX_VERIFY)) {
        if (!game.entity_position(f, e.entity, &entity_at)) {
            attach = false;
            e.info_flags |= KILN_VFX_INFO_UNMATCHED;
            vlog(KILN_LOG_INFO, "vfx: #%u entity %u is not in the client world: playing at the given position", e.id, e.entity);
        } else if (dist(entity_at, e.position) > e.verify_radius) {
            attach = false;
            e.info_flags |= KILN_VFX_INFO_UNMATCHED;
            vlog(KILN_LOG_INFO, "vfx: #%u entity %u is %.1f m from the expected position (limit %.1f m): playing at the expected "
                 "position without attaching (ids differ on this client)", e.id, e.entity, dist(entity_at, e.position), e.verify_radius);
        }
    } else if (attach && !game.entity_position(f, e.entity, &entity_at)) {
        finish(e, KILN_VFX_FAILED, KILN_VFX_END_ENTITY_GONE);
        return;
    }
    FxParams params{};
    const size_t at_start = start_params(game, f.fxc, e, &params);
    const FxTiming timing = timing_of(game, f.fxc, e);
    uint32_t fx = 0;
    if (attach) {
        FxAttach a{};
        a.entity = e.entity;
        a.slot = e.slot;
        std::memcpy(a.local, e.local, sizeof a.local);
        std::memcpy(a.world, e.world, sizeof a.world);
        std::memcpy(a.rot, e.orot, sizeof a.rot);
        a.follow = 1;
        fx = game.play_on(f, e.guid, a, params, timing);
        e.position = entity_at;
    } else {
        WorldTransform t{};
        t.pos[0] = m_to_fixed(e.position.x);
        t.pos[1] = m_to_fixed(e.position.y);
        t.pos[2] = m_to_fixed(e.position.z);
        std::memcpy(t.rot, e.rotation, sizeof t.rot);
        t.scale[0] = t.scale[1] = t.scale[2] = e.scale;
        fx = game.play_at(f, e.guid, t, params, timing);
    }
    if (!game.can_play()) {
        finish(e, KILN_VFX_FAILED, KILN_VFX_END_FAULT);
        return;
    }
    if (!fx) {
        finish(e, KILN_VFX_FAILED, KILN_VFX_END_START_FAILED);
        return;
    }
    e.fx = fx;
    e.state = KILN_VFX_PLAYING;
    e.started_ms = now_ms();
    ++g_played;
    if (attach) e.info_flags |= KILN_VFX_INFO_ATTACHED;
    game.lookup(f, fx, &e.instance);
    const bool info = g_logged_plays++ < kLogFirst;
    char with[64] = "";
    if (at_start) std::snprintf(with, sizeof with, ", %zu parameter(s) in its start block", at_start);
    if (attach)
        vlog(info ? KILN_LOG_INFO : KILN_LOG_DEBUG, "vfx: #%u play %s on entity %u slot %u -> handle 0x%x, instance 0x%x, thread %u, %llu ms after the request%s",
             e.id, g, e.entity, e.slot, fx, e.instance, e.thread, static_cast<unsigned long long>(e.started_ms - e.queued_ms), with);
    else
        vlog(info ? KILN_LOG_INFO : KILN_LOG_DEBUG, "vfx: #%u play %s at %.2f,%.2f,%.2f -> handle 0x%x, instance 0x%x, thread %u, %llu ms after the request%s",
             e.id, g, e.position.x, e.position.y, e.position.z, fx, e.instance, e.thread, static_cast<unsigned long long>(e.started_ms - e.queued_ms), with);
}

// PLAYING (update thread, g_mu held).
//
// An entry without an instance needs the immediate stop: the FxController keeps retrying a start that hasn't produced an instance (a
// slot this body doesn't have, an empty instance pool) and ignores an end time while it does.
void update(Game& game, const Frame& f, Entry& e) {
    uint32_t instance = 0;
    const bool held = game.lookup(f, e.fx, &instance);
    if (e.stop_req) {
        if (held) game.stop(f, e.fx, e.stop_immediate || instance == 0);
        finish(e, KILN_VFX_STOPPED, KILN_VFX_END_STOPPED);
        return;
    }
    if (!held) {
        finish(e, KILN_VFX_STOPPED, KILN_VFX_END_FINISHED);
        return;
    }
    e.instance = instance;
    if (!instance && now_ms() - e.started_ms > kNoInstanceMs) {
        game.stop(f, e.fx, true);
        finish(e, KILN_VFX_STOPPED, KILN_VFX_END_NO_INSTANCE);
        return;
    }
    if (instance && !e.params.empty()) {
        for (const auto& p : e.params) game.set_param(f, instance, p.hash, p.type, p.data, p.size);
        e.params.clear();
    }
    if ((e.info_flags & KILN_VFX_INFO_ATTACHED) != 0) {
        KilnVec3 at{};
        if (!game.entity_position(f, e.entity, &at)) {
            game.stop(f, e.fx, instance == 0);
            finish(e, KILN_VFX_STOPPED, KILN_VFX_END_ENTITY_GONE);
            return;
        }
        e.position = at;
    }
}

// Timeouts and forgetting, also when no update runs (the main menu has no FxController). g_mu held.
void sweep(uint64_t now) {
    if (now - g_last_sweep < 1000) return;
    g_last_sweep = now;
    for (auto it = g_entries.begin(); it != g_entries.end();) {
        Entry& e = it->second;
        if (e.state == KILN_VFX_PENDING && now - e.queued_ms > kPendingTimeoutMs) finish(e, KILN_VFX_FAILED, KILN_VFX_END_TIMEOUT);
        if (final_state(e.state) && now - e.ended_ms > kKeepFinishedMs)
            it = g_entries.erase(it);
        else
            ++it;
    }
}

uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}

bool finite3(const KilnVec3& v) { return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z); }

}  // namespace

// ---- map and guard ----

bool zero16(const uint8_t g[16]) {
    for (int i = 0; i < 16; ++i)
        if (g[i]) return false;
    return true;
}

void guid_text(const uint8_t g[16], char out[40]) {
    std::snprintf(out, 40, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1], g[0], g[5], g[4], g[7],
                  g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
}

int64_t map_slot(const MapView& m, const uint8_t guid[16]) {
    if (!m.capacity || !m.count || !m.bits || !m.keys || zero16(guid)) return -1;
    uint64_t k0, k1;
    std::memcpy(&k0, guid, 8);
    std::memcpy(&k1, guid + 8, 8);
    const uint64_t mask = m.capacity - 1;
    uint64_t i = k0 & 0xffffffffu & mask;
    for (uint64_t probes = 0; probes < m.capacity; ++probes) {
        if (((m.bits[i >> 6] >> (i & 63)) & 1) == 0) return -1;
        if (m.keys[i * 2] == k0 && m.keys[i * 2 + 1] == k1) return static_cast<int64_t>(i);
        i = i + 1 < m.capacity ? i + 1 : 0;
    }
    return -1;
}

int map_find(const MapView& m, const uint8_t guid[16]) {
    if (!m.capacity || !m.count || !m.bits || !m.keys) return -1;
    return map_slot(m, guid) >= 0 ? 1 : 0;
}

void read_lengths(const uint8_t* res, const ResLayout& l, VfxLengths* out) {
    static const uint32_t kDuration = fnv1a("duration"), kLifetime = fnv1a("lifetime");
    std::memcpy(&out->kill_seconds, res + l.kill, 4);
    int32_t rel = 0;
    uint32_t count = 0;
    std::memcpy(&rel, res + l.params, 4);
    std::memcpy(&count, res + l.params + 4, 4);
    if (!rel) return;
    const uint8_t* p = res + l.params + rel;  // BlobArray<VfxUserEffectParameterDescription 12 B>
    for (uint32_t i = 0; i < count && i < 64; ++i, p += 12) {
        uint32_t hash, type;
        std::memcpy(&hash, p, 4);
        std::memcpy(&type, p + 4, 4);
        const uint8_t start = p[8];
        if (type != kFloatType || start > 60 || (hash != kDuration && hash != kLifetime)) continue;
        float v;
        std::memcpy(&v, res + l.defaults + start, 4);
        if (std::isfinite(v)) (hash == kDuration ? out->duration : out->lifetime) = v;
    }
}

void read_params(const uint8_t* res, const ResLayout& l, VfxParamLayout* out) {
    *out = VfxParamLayout{};
    std::memcpy(out->defaults, res + l.defaults, sizeof out->defaults);
    int32_t rel = 0;
    uint32_t count = 0;
    std::memcpy(&rel, res + l.params, 4);
    std::memcpy(&count, res + l.params + 4, 4);
    if (!rel) return;
    const uint8_t* p = res + l.params + rel;  // BlobArray<VfxUserEffectParameterDescription 12 B>: hash, type, Range8, joint collection
    for (uint32_t i = 0; i < count && i < 64 && out->count < std::size(out->slots); ++i, p += 12) {
        VfxParamSlot s;
        std::memcpy(&s.hash, p, 4);
        std::memcpy(&s.type, p + 4, 4);
        s.start = p[8];
        s.count = p[9];
        if (s.count && s.start + s.count <= sizeof out->defaults) out->slots[out->count++] = s;
    }
}

bool put_start_param(const VfxParamLayout& l, uint32_t hash, uint32_t type, const void* data, size_t size, FxParams* block) {
    for (uint32_t i = 0; i < l.count; ++i) {
        const VfxParamSlot& s = l.slots[i];
        if (s.hash != hash || s.type != type || s.count != size) continue;
        std::memcpy(reinterpret_cast<uint8_t*>(block->data) + s.start, data, size);
        return true;
    }
    return false;
}

int64_t one_shot_ns(const VfxLengths& v) {
    double s = v.kill_seconds;
    double exposed = 0;
    if (v.duration > 0) exposed += v.duration;
    if (v.lifetime > 0) exposed += v.lifetime;
    if (exposed > s) s = exposed;
    if (s <= 0) return kOneShotFallbackNs;
    return std::clamp(static_cast<int64_t>(s * 1e9), kOneShotMinNs, kOneShotMaxNs);
}

bool map_valid(const MapView& m) {
    if (!m.capacity || (m.capacity & (m.capacity - 1)) != 0 || m.capacity > (1u << 24) || m.count > m.capacity || !m.bits || !m.keys)
        return false;
    uint64_t n = 0;
    for (uint64_t w = 0; w < (m.capacity + 63) / 64; ++w) {
        uint64_t b = m.bits[w];
        while (b) {
            b &= b - 1;
            ++n;
        }
    }
    return n == m.count;
}

void guard_refused(const uint8_t guid[16], const char* where) {
    g_refused.fetch_add(1, std::memory_order_relaxed);
    char g[40];
    guid_text(guid, g);
    std::string key = std::string(g) + where;
    {
        std::lock_guard lk(g_refusal_mu);
        if (g_refusals.size() >= kMaxRefusalLines || !g_refusals.insert(std::move(key)).second) return;
    }
    KV_W("vfx: unknown VFX %s ignored (guard at %s; the game would have aborted)", g, where);
}

int64_t m_to_fixed(double m) { return static_cast<int64_t>(std::llround(m * 4294967296.0)); }
double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }

void core_set_game(Game* game) { g_game.store(game, std::memory_order_release); }

void core_set_build_hooks(void (*update)(), bool (*refuses)(const uint8_t guid[16]), int64_t (*one_shot)(const uint8_t guid[16])) {
    g_build_update.store(update, std::memory_order_release);
    g_build_refuses.store(refuses, std::memory_order_release);
    g_build_one_shot.store(one_shot, std::memory_order_release);
}
bool core_build_refuses(const uint8_t guid[16]) {
    const auto refuses = g_build_refuses.load(std::memory_order_acquire);
    return refuses && refuses(guid);
}
Game* core_game() { return g_game.load(std::memory_order_acquire); }

// ---- the update ----

void core_update(void* fxc, int64_t now) {
    Game* game = core_game();
    if (!game) return;
    if (const auto update = g_build_update.load(std::memory_order_acquire)) update();
    g_updates.fetch_add(1, std::memory_order_relaxed);
    const uint32_t tid = thread_id();
    const uint32_t prev_tid = g_thread.exchange(tid, std::memory_order_relaxed);
    void* prev = g_fxc.exchange(fxc, std::memory_order_acq_rel);
    if (prev != fxc)
        KV_I("vfx: FxController 0x%llx updates on thread %u, game time %lld ns", static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(fxc)), tid,
             static_cast<long long>(now));
    else if (prev_tid != tid && prev_tid != 0)
        KV_D("vfx: the FxController update moved from thread %u to %u", prev_tid, tid);
    if (!g_system_said.load(std::memory_order_relaxed)) {
        const MapView v = game->map(fxc);
        if (v.count && !g_system_said.exchange(true))
            KV_I("vfx: system ready: %llu VFX registered (map capacity %llu), guard %s", static_cast<unsigned long long>(v.count),
                 static_cast<unsigned long long>(v.capacity), game->has_guard() ? "on" : "OFF");
    }
    const Frame f{fxc, now};
    std::lock_guard lk(g_mu);
    if (!g_preload_queue.empty() && game->can_play()) {
        std::vector<std::string> later;
        for (const auto& s : g_preload_queue) {
            const uint8_t* guid = reinterpret_cast<const uint8_t*>(s.data());
            char g[40];
            guid_text(guid, g);
            const int known = core_build_refuses(guid) ? 0 : game->is_registered(fxc, guid);
            if (known < 0) {
                later.push_back(s);
            } else if (known == 0) {
                KV_W("vfx: preload: unknown VFX %s ignored", g);
            } else if (g_preload_done.insert(s).second) {
                game->preload(f, guid);
                KV_I("vfx: preload %s: reference held for the session", g);
            }
        }
        g_preload_queue.swap(later);
    }
    if (!g_entries.empty()) {
        uint32_t starts = 0;
        for (auto& [id, e] : g_entries) {
            if (final_state(e.state)) continue;
            if (!game->can_play()) {
                finish(e, e.state == KILN_VFX_PENDING ? KILN_VFX_FAILED : KILN_VFX_STOPPED, KILN_VFX_END_FAULT);
                continue;
            }
            if (e.state == KILN_VFX_PENDING) {
                if (e.stop_req)
                    finish(e, KILN_VFX_STOPPED, KILN_VFX_END_STOPPED);
                else if (e.flags & KILN_VFX_WAIT_GUID)
                    continue;  // bind_guid has not come yet; the sweep times it out
                else if (starts < kMaxStartsPerUpdate) {
                    ++starts;
                    start(*game, f, e);
                }
            } else if (e.fxc == fxc) {  // an entry of a controller whose world ended is closed by core_world_ended
                update(*game, f, e);
            }
        }
    }
    sweep(now_ms());
}

void core_world_ended() {
    std::lock_guard lk(g_mu);
    g_fxc.store(nullptr, std::memory_order_release);
    uint32_t n = 0;
    for (auto& [id, e] : g_entries) {
        if (final_state(e.state)) continue;
        finish(e, e.state == KILN_VFX_PENDING ? KILN_VFX_FAILED : KILN_VFX_STOPPED, KILN_VFX_END_WORLD_ENDED);
        ++n;
    }
    KV_I("vfx: client world stopping: %u of Kiln's VFX handles ended", n);
}

// ---- the C table ----

KilnResult core_status(KilnVfxStatus* out) {
    if (!out || out->size < sizeof(KilnVfxStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    Game* game = core_game();
    void* fxc = g_fxc.load(std::memory_order_acquire);
    if (game) {
        if (game->has_guard()) out->flags |= KILN_VFX_HAS_GUARD;
        if (game->can_play()) out->flags |= KILN_VFX_HAS_PLAY;
        if (game->is_sim()) out->flags |= KILN_VFX_SIM;
        const MapView v = game->map(fxc);
        if (v.count) out->flags |= KILN_VFX_SYSTEM;
        out->registered = static_cast<uint32_t>(v.count);
        out->map_capacity = static_cast<uint32_t>(v.capacity);
    }
    if (fxc) out->flags |= KILN_VFX_CONTROLLER;
    out->thread = g_thread.load(std::memory_order_relaxed);
    out->updates = g_updates.load(std::memory_order_relaxed);
    out->refused = g_refused.load(std::memory_order_relaxed);
    out->controller = reinterpret_cast<uintptr_t>(fxc);
    std::lock_guard lk(g_mu);
    sweep(now_ms());
    out->live = g_live;
    out->played = g_played;
    out->failed = g_failed;
    return KILN_OK;
}

KilnVfx core_play(KilnOwner owner, const KilnVfxPlay* p) {
    if (!p || p->size < sizeof(KilnVfxPlay) || (zero16(p->vfx) && !(p->flags & KILN_VFX_WAIT_GUID))) return 0;
    Game* game = core_game();
    if (!game || !game->can_play()) {
        std::lock_guard lk(g_mu);
        if (!g_said_no_game) {
            g_said_no_game = true;
            KV_W("vfx: play: kiln.vfx cannot reach the game on this build or after a fault; nothing plays");
        }
        return 0;
    }
    if ((p->flags & (KILN_VFX_AT | KILN_VFX_ON)) == 0 && (p->flags & KILN_VFX_TEST_CREATE) == 0) return 0;
    if ((p->flags & KILN_VFX_AT) && !finite3(p->position)) return 0;
    if ((p->flags & KILN_VFX_ON) && (!p->entity || p->slot >= KILN_VFX_SLOTS)) return 0;
    Entry e;
    e.owner = owner;
    e.flags = p->flags;
    std::memcpy(e.guid, p->vfx, 16);
    e.position = p->position;
    std::memcpy(e.rotation, p->rotation, sizeof e.rotation);
    normalize(e.rotation);
    e.scale = std::isfinite(p->scale) && p->scale > 0.f ? p->scale : 1.f;
    e.entity = p->entity;
    e.slot = static_cast<uint8_t>(p->slot);
    for (int i = 0; i < 3; ++i) {
        e.local[i] = std::isfinite(p->local_offset[i]) ? p->local_offset[i] : 0.f;
        e.world[i] = std::isfinite(p->world_offset[i]) ? p->world_offset[i] : 0.f;
    }
    std::memcpy(e.orot, p->offset_rotation, sizeof e.orot);
    normalize(e.orot);
    e.verify_radius = std::isfinite(p->verify_radius) && p->verify_radius > 0.f ? p->verify_radius : kDefaultVerifyRadius;
    e.lifetime_ns = p->lifetime_ns;
    const uint64_t now = now_ms();
    e.queued_ms = now;
    std::lock_guard lk(g_mu);
    sweep(now);
    if (g_live >= kMaxLive) {
        KV_W("vfx: play refused: %u VFX handles live", g_live);
        return 0;
    }
    const KilnVfx id = g_next;
    g_next = g_next + 1 >= 0x7fffffffu ? 1 : g_next + 1;
    e.id = id;
    g_entries.emplace(id, std::move(e));
    ++g_live;
    return id;
}

KilnResult core_stop(KilnVfx vfx, int immediate) {
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(vfx);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    if (!final_state(it->second.state)) {
        it->second.stop_req = true;
        it->second.stop_immediate = immediate != 0;
    }
    return KILN_OK;
}

KilnResult core_bind_guid(KilnVfx vfx, const uint8_t guid[16]) {
    if (!guid) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(vfx);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    Entry& e = it->second;
    if (!(e.flags & KILN_VFX_WAIT_GUID) || final_state(e.state)) return KILN_OK;
    e.flags &= ~static_cast<uint32_t>(KILN_VFX_WAIT_GUID);
    std::memcpy(e.guid, guid, 16);
    if (zero16(guid)) finish(e, KILN_VFX_FAILED, KILN_VFX_END_UNKNOWN_VFX);
    return KILN_OK;
}

KilnResult core_set_param(KilnVfx vfx, uint32_t hash, uint32_t type, const void* data, size_t size) {
    if (!data || !size || size > kMaxParamBytes) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    auto it = g_entries.find(vfx);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    Entry& e = it->second;
    if (final_state(e.state)) return KILN_OK;
    for (auto& p : e.params)  // the last value of a name wins
        if (p.hash == hash && p.type == type && p.size == size) {
            std::memcpy(p.data, data, size);
            return KILN_OK;
        }
    if (e.params.size() >= kMaxParams) return KILN_ERR_BUFFER_TOO_SMALL;
    ParamOp op{};
    op.hash = hash;
    op.type = type;
    op.size = static_cast<uint8_t>(size);
    std::memcpy(op.data, data, size);
    e.params.push_back(op);
    return KILN_OK;
}

uint32_t core_state(KilnVfx vfx) {
    std::lock_guard lk(g_mu);
    sweep(now_ms());
    auto it = g_entries.find(vfx);
    if (it == g_entries.end()) return KILN_VFX_NONE;
    const Entry& e = it->second;
    if (!final_state(e.state) && e.stop_req) return KILN_VFX_STOPPED;  // stop requested: reads stopped at once
    return e.state;
}

KilnResult core_info(KilnVfx vfx, KilnVfxInfo* out) {
    if (!out || out->size < sizeof(KilnVfxInfo)) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lk(g_mu);
    sweep(now_ms());
    auto it = g_entries.find(vfx);
    if (it == g_entries.end()) return KILN_ERR_NOT_FOUND;
    const Entry& e = it->second;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->state = (!final_state(e.state) && e.stop_req) ? static_cast<uint32_t>(KILN_VFX_STOPPED) : e.state;
    out->reason = e.reason;
    out->flags = e.info_flags;
    out->fx_handle = e.fx;
    out->instance = e.instance;
    out->thread = e.thread;
    out->entity = e.entity;
    out->queued_ms = e.queued_ms;
    out->started_ms = e.started_ms;
    out->ended_ms = e.ended_ms;
    out->position = e.position;
    out->lifetime_ns = e.lifetime_ns;
    return KILN_OK;
}

uint32_t core_stop_owner(KilnOwner owner, int immediate) {
    std::lock_guard lk(g_mu);
    uint32_t n = 0;
    for (auto& [id, e] : g_entries) {
        if (e.owner != owner || final_state(e.state) || e.stop_req) continue;
        e.stop_req = true;
        e.stop_immediate = immediate != 0;
        ++n;
    }
    return n;
}

int32_t core_is_registered(const uint8_t guid[16]) {
    if (!guid) return 0;
    Game* game = core_game();
    if (!game) return -1;
    if (core_build_refuses(guid)) return 0;
    return game->is_registered(g_fxc.load(std::memory_order_acquire), guid);
}

KilnResult core_preload(const uint8_t guid[16]) {
    if (!guid || zero16(guid)) return KILN_ERR_INVALID_ARGUMENT;
    Game* game = core_game();
    if (!game || !game->can_play()) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lk(g_mu);
    std::string s(reinterpret_cast<const char*>(guid), 16);
    if (!g_preload_done.count(s) && g_preload_queue.size() < 256) g_preload_queue.push_back(std::move(s));
    return KILN_OK;
}

KilnResult core_status_json(char* buf, size_t cap, size_t* out_len) {
    KilnVfxStatus s{};
    s.size = sizeof s;
    core_status(&s);
    char b[512];
    std::snprintf(b, sizeof b,
                  "{\"guard\":%s,\"play\":%s,\"controller\":%s,\"system\":%s,\"sim\":%s,\"registered\":%u,\"mapCapacity\":%u,\"thread\":%u,"
                  "\"live\":%u,\"played\":%llu,\"failed\":%llu,\"refused\":%llu,\"updates\":%llu,\"handles\":[",
                  (s.flags & KILN_VFX_HAS_GUARD) ? "true" : "false", (s.flags & KILN_VFX_HAS_PLAY) ? "true" : "false",
                  (s.flags & KILN_VFX_CONTROLLER) ? "true" : "false", (s.flags & KILN_VFX_SYSTEM) ? "true" : "false",
                  (s.flags & KILN_VFX_SIM) ? "true" : "false", s.registered, s.map_capacity, s.thread, s.live,
                  static_cast<unsigned long long>(s.played), static_cast<unsigned long long>(s.failed),
                  static_cast<unsigned long long>(s.refused), static_cast<unsigned long long>(s.updates));
    std::string j = b;
    {
        std::lock_guard lk(g_mu);
        int shown = 0;
        bool first = true;
        for (auto it = g_entries.rbegin(); it != g_entries.rend() && shown < 64; ++it, ++shown) {
            const Entry& e = it->second;
            char g[40];
            guid_text(e.guid, g);
            std::snprintf(b, sizeof b, "%s{\"id\":%u,\"vfx\":\"%s\",\"state\":\"%s\",\"reason\":\"%s\",\"handle\":%u,\"instance\":%u,\"entity\":%u,\"thread\":%u}",
                          first ? "" : ",", e.id, g, state_text(e.state), reason_text(e.reason), e.fx, e.instance, e.entity, e.thread);
            j += b;
            first = false;
        }
    }
    j += "]}";
    if (out_len) *out_len = j.size();
    if (!buf || cap < j.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, j.data(), j.size());
    buf[j.size()] = 0;
    return KILN_OK;
}

}  // namespace kvx
