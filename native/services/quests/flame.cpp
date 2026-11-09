// SPDX-License-Identifier: MIT
// kiln.knowledge: the Mother Flame's level (flame_status, flame_set, flame_result in kiln_knowledge.h).
//
// Evidence: docs/re/world-status.md section 11. The authoritative store is the `Level` attribute component
// (keen::ecs::Level: UInt32Attribute {rootId, storageOffset, flags, storageCount} + dataStorage[2] = {level, max}) of the
// world's progress entity (template 3_MotherFlame: Crafting, Level, NetworkServerProgress, ServerProgress). The game's
// own writer is the admin command AdminSetFlameLevel in admin_player: it takes the Level through the program's write
// accessor (which marks it written, for saving and replication), stores the byte at the attribute's slot and re-runs the
// attribute formula (AttrRecompute, which clamps the level to the second value, the game's flameLevelMax). The game
// copies Level into NetworkServerProgress.level (what clients get) and into ServerProgressSystem.progressLevel (what the
// altar and water-source limits and the fog rules read) every step.
//
// Kiln does exactly that, from a program of its own (kiln_flame, kiln.world, each-mode over the entities that have Level
// and NetworkServerProgress: the one progress entity): MarkWritten on the write record, the slot found with the game's
// AttrFind, the store, the game's AttrRecompute. The program also samples the level twice a second (for flame_status)
// and, while a change is being mirrored, every step. Cost with nothing to do: one entity, a counter and a modulo.
//
// Fail soft: an unknown build (no AttrFind / AttrRecompute / Level id, or a program kiln.world refuses) turns the flame
// off with a reason in flame_status(); nothing here can stop the server. Every read of game memory is SEH-guarded.
#include <windows.h>

#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>

#include "knowledge_internal.h"
#include "services/kiln_world.h"

namespace kiln::knowledge {
namespace {

#define FI(...) ::kiln::knowledge::logf(KILN_LOG_INFO, "flame: " __VA_ARGS__)
#define FW(...) ::kiln::knowledge::logf(KILN_LOG_WARN, "flame: " __VA_ARGS__)

static_assert(sizeof(KilnFlameStatus) == 160 && sizeof(KilnFlameResult) == 152);

using AttrFindFn = void* (*)(void* out16, uint32_t id, void* registry);
using AttrRecomputeFn = void (*)(void* component, void* registry);

const KilnWorldApi* W = nullptr;
AttrFindFn g_find = nullptr;
AttrRecomputeFn g_recompute = nullptr;
int64_t g_attr_level = 0;  // the Level attribute's id (0x724bf568)
KilnHandle g_prog = 0;
int32_t s_nsp = -1, s_level = -1, s_reg = -1;
std::atomic<bool> g_ok{false};
std::string g_reason;  // g_mu

constexpr uint32_t kRequestMax = KILN_FLAME_QUEUE_MAX, kNoValue = KILN_FLAME_NO_VALUE;
constexpr uint32_t kSampleEvery = 15;   // steps between samples while idle
constexpr uint32_t kMirrorSteps = KILN_FLAME_MIRROR_UPDATES;
constexpr uint64_t kSeenMs = 3000;      // "found": the program saw the progress entity this recently

struct Pending {
    uint64_t request;
    uint32_t level;
};

struct Await {
    bool active = false;
    KilnFlameResult res{};
    uint64_t applied_update = 0;
    uint32_t target = 0;
};

std::mutex g_mu;
std::deque<Pending> g_pending;  // g_mu
std::array<KilnFlameResult, 16> g_results{};  // ring by request id (g_mu)
uint64_t g_next = 1;                           // g_mu
Await g_await;                                 // program thread only (its result is published under g_mu)
std::atomic<uint32_t> g_pending_n{0};
std::atomic<bool> g_awaiting{false};
std::atomic<uint64_t> g_updates{0};
std::atomic<uint64_t> g_submitted{0}, g_applied{0}, g_failed{0};

struct Snap {
    bool seen = false;
    uint64_t seen_ms = 0;
    KilnEntity entity = 0;
    bool tracked = false;
    uint32_t level = 0, cap = 0, network = 0, count = 0;
    uint64_t step = 0;
} g_snap;  // g_mu

// ServerProgressSystem (a world singleton; progressEntity +0x10, progressLevel +0x14), found lazily on the tick thread.
uintptr_t g_sps = 0, g_sps_world = 0;
const char* g_sps_name = nullptr;

void set_reason(const char* fmt, ...) {
    char buf[160];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    std::lock_guard lock(g_mu);
    g_reason = buf;
}

void copy_text(char* dst, size_t cap, const char* s) {
    std::snprintf(dst, cap, "%s", s ? s : "");
}

// ---- guarded access to game memory (no C++ objects with destructors in these frames) --------------------------------
bool read_u32(const void* p, uint32_t* out) {
    if (!p) return false;
    __try {
        *out = *static_cast<const volatile uint32_t*>(p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_u16(const void* p, uint32_t* out) {
    if (!p) return false;
    __try {
        *out = *static_cast<const volatile uint16_t*>(p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool write_u32(void* p, uint32_t v) {
    if (!p) return false;
    __try {
        *static_cast<volatile uint32_t*>(p) = v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The u32 storage word of attribute `id` in an attribute component: the game's AttrFind says which slot, the
// component's own header (rootId, storageOffset, flags, storageCount) says where; both must agree.
uint32_t* attr_slot(void* comp, uint32_t id, void* reg, uint32_t* count) {
    if (!comp || !reg || !g_find) return nullptr;
    __try {
        alignas(8) uint8_t out[16] = {};
        g_find(out, id, reg);
        if (out[0] != 0) return nullptr;
        const uint8_t* info = *reinterpret_cast<uint8_t* const*>(out + 8);
        auto* c = static_cast<uint8_t*>(comp);
        if (!info || *reinterpret_cast<const uint32_t*>(info) != *reinterpret_cast<const uint32_t*>(c)) return nullptr;
        if (!(c[6] & 1)) return nullptr;  // not initialised
        const uint16_t slot = *reinterpret_cast<const uint16_t*>(info + 0xc);
        if (slot >= c[7]) return nullptr;  // storageCount
        if (count) *count = c[7];
        return reinterpret_cast<uint32_t*>(c + *reinterpret_cast<const uint16_t*>(c + 4) + slot * 4u);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

bool run_recompute(void* comp, void* reg) {
    if (!g_recompute) return false;
    __try {
        g_recompute(comp, reg);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool mark(KilnProgramCtx* ctx, const uint64_t* iter, int32_t slot) {
    if (slot < 0 || !iter[slot] || !iter[slot + 5]) return false;  // no change tracker: nothing to mark
    __try {
        W->mark_written(ctx, &iter[slot]);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

uint64_t now_ms() { return GetTickCount64(); }

void publish(const KilnFlameResult& r) {
    std::lock_guard lock(g_mu);
    g_results[r.request % g_results.size()] = r;
}

// ---- the program ----------------------------------------------------------------------------------------------------
void finish_await(bool mirrored, uint32_t steps, const char* reason) {
    KilnFlameResult r = g_await.res;
    r.mirrored = mirrored ? 1 : 0;
    r.steps_to_mirror = steps;
    if (reason) copy_text(r.reason, sizeof r.reason, reason);
    publish(r);
    g_await.active = false;
    g_awaiting.store(false, std::memory_order_release);
}

using WriteWord = bool (*)(void*, uint32_t);
using ReadWord = bool (*)(const void*, uint32_t*);

// The formula can fault after partially updating Level. Put back both storage words and verify them before reporting
// a normal failure. A failure to restore is explicit: the already-marked component may still contain changed state.
void fail_apply(KilnFlameResult r, uint32_t* slot, uint32_t before, uint32_t cap, const char* why,
                WriteWord write = &write_u32, ReadWord read = &read_u32) {
    const bool level_written = write(slot, before);
    const bool cap_written = write(slot + 1, cap);
    uint32_t actual = 0, actual_cap = 0;
    const bool level_read = read(slot, &actual);
    const bool cap_read = read(slot + 1, &actual_cap);
    const bool restored = level_written && cap_written && level_read && cap_read && actual == before && actual_cap == cap;
    r.status = KILN_ERR_INTERNAL;
    r.after = level_read ? actual : kNoValue;
    std::snprintf(r.reason, sizeof r.reason, "%s; %s", why,
                  restored ? "previous Level/cap restored" : "ROLLBACK FAILED: Level/cap may be changed");
    g_failed.fetch_add(1);
    publish(r);
}

void apply_one(KilnProgramCtx* ctx, uint64_t* iter, void* level, void* reg, const Pending& p, uint64_t update) {
    KilnFlameResult r{};
    r.size = sizeof r;
    r.request = p.request;
    r.requested = p.level;
    r.step = update;
    uint32_t count = 0;
    uint32_t* slot = attr_slot(level, static_cast<uint32_t>(g_attr_level), reg, &count);
    uint32_t before = 0;
    if (!slot || !read_u32(slot, &before)) {
        r.status = KILN_ERR_INTERNAL;
        copy_text(r.reason, sizeof r.reason, "the progress entity's Level is not an initialised attribute on this build");
        g_failed.fetch_add(1);
        publish(r);
        return;
    }
    uint32_t cap = 0;
    if (count < 2 || !read_u32(slot + 1, &cap)) {
        r.status = KILN_ERR_INTERNAL;
        copy_text(r.reason, sizeof r.reason, "the progress entity's Level maximum could not be read; no write made");
        g_failed.fetch_add(1);
        publish(r);
        return;
    }
    r.before = before;
    r.cap = cap;
    r.marked = mark(ctx, iter, s_level) ? 1 : 0;
    if (!r.marked) {
        r.status = KILN_ERR_UNAVAILABLE;
        copy_text(r.reason, sizeof r.reason, "Level has no change tracker: no persistent write was made");
        g_failed.fetch_add(1);
        publish(r);
        return;
    }
    if (!write_u32(slot, p.level) || !run_recompute(level, reg)) {
        fail_apply(r, slot, before, cap, "the attribute write faulted");
        return;
    }
    uint32_t after = 0;
    if (!read_u32(slot, &after)) {
        fail_apply(r, slot, before, cap, "Level readback faulted");
        return;
    }
    r.after = after;
    r.clamped = after != p.level ? 1 : 0;
    r.status = KILN_OK;
    r.system_level = kNoValue;
    g_applied.fetch_add(1);
    if (r.clamped) std::snprintf(r.reason, sizeof r.reason, "the game's formula limited %u to %u (maximum %u)", p.level, after, cap);
    g_await.active = true;
    g_await.res = r;
    g_await.applied_update = update;
    g_await.target = after;
    g_awaiting.store(true, std::memory_order_release);
}

void KILN_CALL flame_each(KilnProgramCtx* ctx, uint64_t* iter, KilnEntity entity, void*) {
    if (!g_ok.load(std::memory_order_relaxed) || s_level < 0 || s_nsp < 0 || s_reg < 0) return;
    const uint64_t update = g_updates.fetch_add(1, std::memory_order_relaxed) + 1;
    const bool busy = g_pending_n.load(std::memory_order_acquire) != 0 || g_awaiting.load(std::memory_order_acquire);
    if (!busy && (update % kSampleEvery) != 1) return;
    void* level = reinterpret_cast<void*>(iter[s_level]);
    const void* nsp = reinterpret_cast<const void*>(iter[s_nsp]);
    void* reg = reinterpret_cast<void*>(iter[s_reg]);
    if (!level || !nsp || !reg) return;

    uint32_t network = 0;
    if (!read_u16(nsp, &network)) return;

    // apply what is queued: one request per step, in order
    Pending p{};
    bool have = false;
    {
        std::lock_guard lock(g_mu);
        if (!g_pending.empty() && !g_await.active) {
            p = g_pending.front();
            g_pending.pop_front();
            g_pending_n.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
            have = true;
        }
    }
    if (have) apply_one(ctx, iter, level, reg, p, update);

    // the mirror of the last change
    if (g_await.active) {
        if (network == g_await.target) {
            finish_await(true, static_cast<uint32_t>(update - g_await.applied_update), nullptr);
        } else if (update - g_await.applied_update > kMirrorSteps) {
            finish_await(false, 0, "NetworkServerProgress.level did not follow within 180 updates");
        }
    }

    // the sample flame_status() answers from
    uint32_t cur = 0, count = 0, cap = 0;
    uint32_t* slot = attr_slot(level, static_cast<uint32_t>(g_attr_level), reg, &count);
    const bool read = slot && read_u32(slot, &cur);
    if (read && count >= 2) read_u32(reinterpret_cast<uint8_t*>(slot) + 4, &cap);
    std::lock_guard lock(g_mu);
    g_snap.seen = read;
    g_snap.seen_ms = now_ms();
    g_snap.entity = entity;
    g_snap.tracked = iter[s_level + 5] != 0;
    g_snap.level = cur;
    g_snap.cap = cap;
    g_snap.network = network;
    g_snap.count = count;
    g_snap.step = update;
}

// ---- the service ----------------------------------------------------------------------------------------------------
uintptr_t system_object(uintptr_t world) {
    static const char* const kNames[] = {"ServerProgressSystem", "keen::ecs::ServerProgressSystem", "ecs::ServerProgressSystem"};
    if (world != g_sps_world) {
        g_sps_world = world;
        g_sps = 0;
        g_sps_name = nullptr;
    }
    if (g_sps || !world) return g_sps;
    for (const char* n : kNames) {
        const uintptr_t p = g_api->singleton(world, kstr(n));
        if (p) {
            g_sps = p;
            g_sps_name = n;
            FI("ServerProgressSystem found as the singleton \"%s\"", n);
            break;
        }
    }
    return g_sps;
}

// progressLevel when the object's progressEntity is the entity the program found (the layout check), else kNoValue.
uint32_t system_level(KilnEntity entity) {
    if (!g_api->on_tick_thread()) return kNoValue;
    const uintptr_t obj = system_object(g_api->server_world());
    uint32_t e = 0, level = 0;
    if (!obj || !read_u32(reinterpret_cast<const void*>(obj + 0x10), &e) || e != entity ||
        !read_u32(reinterpret_cast<const void*>(obj + 0x14), &level))
        return kNoValue;
    return level;
}

bool seen_recently(const Snap& s) { return s.seen && now_ms() - s.seen_ms < kSeenMs; }

KilnResult KILN_CALL t_flame_status(KilnFlameStatus* out) {
    if (!out || out->size < sizeof(KilnFlameStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size > sizeof(KilnFlameStatus) ? static_cast<uint32_t>(sizeof(KilnFlameStatus)) : size;
    out->system_level = kNoValue;
    KilnProgramInfo info{};
    info.size = sizeof info;
    const bool active = g_prog && W && W->program_info(g_prog, &info) == KILN_OK && info.state == KILN_PROGRAM_ACTIVE;
    out->available = g_ok.load() && active ? 1 : 0;
    Snap s;
    std::string reason;
    {
        std::lock_guard lock(g_mu);
        s = g_snap;
        reason = g_reason;
        out->pending = static_cast<uint32_t>(g_pending.size());
    }
    out->found = seen_recently(s) ? 1 : 0;
    out->tracked = s.tracked ? 1 : 0;
    if (out->found) {
        out->entity = s.entity;
        out->level = s.level;
        out->cap = s.cap;
        out->network_level = s.network;
        out->system_level = system_level(s.entity);
        out->step = s.step;
    }
    out->submitted = g_submitted.load();
    out->applied = g_applied.load();
    out->failed = g_failed.load();
    if (!out->available && reason.empty() && g_ok.load()) reason = "the program kiln_flame is not active (no server world yet, or kiln.world refused it)";
    copy_text(out->reason, sizeof out->reason, reason.c_str());
    return KILN_OK;
}

KilnResult KILN_CALL t_flame_set(KilnOwner, uint32_t level, uint64_t* out_request) {
    if (!out_request) return KILN_ERR_INVALID_ARGUMENT;
    *out_request = 0;
    if (level < 1 || level > 255) return KILN_ERR_INVALID_ARGUMENT;
    if (!g_ok.load()) return KILN_ERR_UNAVAILABLE;
    KilnProgramInfo info{};
    info.size = sizeof info;
    if (!g_prog || !W || W->program_info(g_prog, &info) != KILN_OK || info.state != KILN_PROGRAM_ACTIVE) return KILN_ERR_UNAVAILABLE;
    std::lock_guard lock(g_mu);
    if (!seen_recently(g_snap)) return KILN_ERR_UNAVAILABLE;  // no world, or no Mother Flame in it
    if (g_pending.size() >= kRequestMax) return KILN_ERR_BUFFER_TOO_SMALL;
    const uint64_t id = g_next++;
    g_pending.push_back({id, level});
    g_pending_n.store(static_cast<uint32_t>(g_pending.size()), std::memory_order_release);
    g_submitted.fetch_add(1);
    *out_request = id;
    return KILN_OK;
}

KilnResult KILN_CALL t_flame_result(uint64_t request, KilnFlameResult* out) {
    if (!out || out->size < sizeof(KilnFlameResult) || request == 0) return KILN_ERR_INVALID_ARGUMENT;
    std::lock_guard lock(g_mu);
    const KilnFlameResult& r = g_results[request % g_results.size()];
    if (r.request != request) return KILN_ERR_NOT_FOUND;  // still waiting, or forgotten (the last 16 are kept)
    *out = r;
    return KILN_OK;
}

void KILN_CALL flame_world(const KilnWorldEvent* e, void*) {
    if (!e || e->kind != KILN_WORLD_SERVER) return;
    // a new or ended server world: what was seen belongs to the old one
    std::lock_guard lock(g_mu);
    g_snap = Snap{};
    for (const Pending& p : g_pending) {
        KilnFlameResult r{};
        r.size = sizeof r;
        r.request = p.request;
        r.requested = p.level;
        r.status = KILN_ERR_UNAVAILABLE;
        r.system_level = kNoValue;
        copy_text(r.reason, sizeof r.reason, "the world ended before the change ran");
        g_results[r.request % g_results.size()] = r;
        g_failed.fetch_add(1);
    }
    g_pending.clear();
    g_pending_n.store(0, std::memory_order_release);
    // World notifications bracket the sim steps: no ECS callback runs during this reset. Do not let the next world's
    // coincidentally equal level confirm a write from the old world.
    if (g_await.active) {
        auto r = g_await.res;
        r.status = KILN_ERR_UNAVAILABLE;
        r.mirrored = 0;
        copy_text(r.reason, sizeof r.reason, "the world ended before the change was mirrored");
        g_results[r.request % g_results.size()] = r;
        g_await = Await{};
        g_awaiting.store(false, std::memory_order_release);
        g_failed.fetch_add(1);
    }
    g_sps = 0;
    g_sps_world = 0;
}

}  // namespace

KilnResult KILN_CALL flame_status(KilnFlameStatus* out) { return t_flame_status(out); }
KilnResult KILN_CALL flame_set(KilnOwner owner, uint32_t level, uint64_t* out_request) {
    return t_flame_set(owner, level, out_request);
}
KilnResult KILN_CALL flame_result(uint64_t request, KilnFlameResult* out) { return t_flame_result(request, out); }

// Called from kiln.knowledge's on_ready (the same moment its own program registers): kiln.world accepts programs now.
void flame_ready() {
    int64_t level_id = 0;
    uintptr_t find = 0, recompute = 0;
    if (!g_api->feature(kstr("players.progression")) || g_api->symbol(kstr("players/AttrFind"), &find) != KILN_OK || !find ||
        g_api->symbol(kstr("players/AttrRecompute"), &recompute) != KILN_OK || !recompute ||
        g_api->constant(kstr("players/attr.level"), &level_id) != KILN_OK || level_id == 0) {
        set_reason("the game's attribute functions or the Level attribute id are not resolved on this build");
        FW("off: %s", g_reason.c_str());
        return;
    }
    W = static_cast<const KilnWorldApi*>(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
    if (!W || W->header.size < sizeof(KilnWorldApi)) {
        W = nullptr;
        set_reason("kiln.world is unavailable");
        FW("off: %s", g_reason.c_str());
        return;
    }
    g_find = reinterpret_cast<AttrFindFn>(find);
    g_recompute = reinterpret_cast<AttrRecomputeFn>(recompute);
    g_attr_level = level_id;
    static const KilnStr kRead[] = {{"NetworkServerProgress", 21}};
    static const KilnStr kWrite[] = {{"Level", 5}};
    static const KilnStr kSing[] = {{"impact::AttributeRegistry", 25}};
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = KILN_PROGRAM_HOST_ONLY;
    d.name = kstr("kiln_flame");
    d.each = &flame_each;
    d.read = KilnNames{kRead, 1, 0};
    d.write = KilnNames{kWrite, 1, 0};
    d.singletons_read = KilnNames{kSing, 1, 0};
    if (W->register_program(g_owner, &d, &g_prog) != KILN_OK) {
        g_prog = 0;
        set_reason("kiln.world refused the program kiln_flame");
        FW("off: %s", g_reason.c_str());
        return;
    }
    s_nsp = W->program_slot(g_prog, KILN_SLOT_READ, kRead[0]);
    s_level = W->program_slot(g_prog, KILN_SLOT_WRITE, kWrite[0]);
    s_reg = W->program_slot(g_prog, KILN_SLOT_SINGLETON, kSing[0]);
    KilnHandle h = 0;
    g_api->subscribe_world(g_owner, &flame_world, nullptr, &h);
    g_ok = s_nsp >= 0 && s_level >= 0 && s_reg >= 0;
    if (!g_ok) {
        set_reason("the program's slots did not resolve");
        FW("off: %s", g_reason.c_str());
        return;
    }
    FI("program kiln_flame accepted (Level write slot %d, NetworkServerProgress read slot %d, attribute registry slot %d)",
       s_level, s_nsp, s_reg);
}

}  // namespace kiln::knowledge
