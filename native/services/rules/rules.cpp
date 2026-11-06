// SPDX-License-Identifier: MIT
// kiln.rules: per-player rules applied where the game decides them (native/core/abi/services/kiln_rules.h,
// docs/re/rules.md). The managed Kiln.Rules works out each player's rules from every mod's rule sets and pushes them
// here; the hooks below only read them.
//
//  - flight gate: a mid hook where player_control_action_sequence has decided whether the building fly mode may run
//    (bl); a player with FLY_ANYWHERE gets "may" unless dead or gliding. Server world and the client's prediction world.
//  - cursor: after the function that computes the build cursor's refusal flags (both cursor programs call it), the
//    flags the player's rules waive are cleared.
//  - costs: ApplyMaterials (take or give a materials list for a building action); called from the four building
//    programs it is given a scaled copy of the list.
//  - mining: TerraformImpact notes the acting player for its thread; the per-material damage function scales its result.
//
// Every hook body runs under a structured-exception guard; a fault counts, and the game's own value stands.
#include <windows.h>

#include <atomic>
#include <bit>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include "kiln_api.h"
#include "safetyhook.hpp"
#include "services/kiln_rules.h"

static_assert(sizeof(KilnRulesPlayer) == 24);
static_assert(sizeof(KilnRulesStatus) == 224 && offsetof(KilnRulesStatus, detail) == 64);

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
constexpr const char* kName = "kiln.rules";

void logf(int level, const char* fmt, ...) {
    char b[512];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    if (g_api && n > 0) g_api->log(g_owner, level, KilnStr{b, static_cast<size_t>(std::min(n, int(sizeof b) - 1))});
}
#define LOGI(...) logf(KILN_LOG_INFO, __VA_ARGS__)
#define LOGW(...) logf(KILN_LOG_WARN, __VA_ARGS__)
#define LOGE(...) logf(KILN_LOG_ERROR, __VA_ARGS__)

// ============================================================================================= rules ===========

// One player's rules, readable from any thread without a lock. Floats are stored as their bits.
struct Slot {
    std::atomic<uint32_t> flags{0};
    std::atomic<uint32_t> cost{std::bit_cast<uint32_t>(1.0f)};
    std::atomic<uint32_t> refund{std::bit_cast<uint32_t>(1.0f)};
    std::atomic<uint32_t> dig{std::bit_cast<uint32_t>(1.0f)};
    std::atomic<bool> any{false};
};
constexpr uint32_t kMaxPlayer = 1023;
Slot g_players[kMaxPlayer + 1];
Slot g_local;
// Other entities with rules: player characters outside the 1..1023 player slots (a lab's spawned characters). Few.
constexpr int kExtra = 16;
std::atomic<uint32_t> g_extra_ids[kExtra];
Slot g_extra[kExtra];
std::mutex g_extra_mu;  // set/clear of extra entries; readers only load atomics

const Slot* slot_of(uint32_t entity) {
    if (entity >= 1 && entity <= kMaxPlayer) return &g_players[entity];
    if (entity == 0) return nullptr;
    for (int i = 0; i < kExtra; ++i)
        if (g_extra_ids[i].load(std::memory_order_acquire) == entity) return &g_extra[i];
    return nullptr;
}

float rd(const std::atomic<uint32_t>& a) { return std::bit_cast<float>(a.load(std::memory_order_relaxed)); }

bool is_default(const KilnRulesPlayer& r) {
    return r.flags == 0 && r.build_cost == 1.0f && r.build_refund == 1.0f && r.dig_damage == 1.0f;
}

void store(Slot& s, const KilnRulesPlayer* r) {
    KilnRulesPlayer d{sizeof d, 0, 1.0f, 1.0f, 1.0f, 0};
    if (r) d = *r;
    auto clean = [](float v) { return std::isfinite(v) && v >= 0.0f ? v : 1.0f; };
    s.flags.store(d.flags, std::memory_order_relaxed);
    s.cost.store(std::bit_cast<uint32_t>(clean(d.build_cost)), std::memory_order_relaxed);
    s.refund.store(std::bit_cast<uint32_t>(clean(d.build_refund)), std::memory_order_relaxed);
    s.dig.store(std::bit_cast<uint32_t>(clean(d.dig_damage)), std::memory_order_relaxed);
    s.any.store(!is_default(d), std::memory_order_release);
}

void load(const Slot& s, KilnRulesPlayer* out) {
    out->size = sizeof *out;
    out->flags = s.flags.load(std::memory_order_relaxed);
    out->build_cost = rd(s.cost);
    out->build_refund = rd(s.refund);
    out->dig_damage = rd(s.dig);
    out->reserved = 0;
}

struct Stats {
    std::atomic<uint64_t> fly{0}, cursor{0}, costs{0}, refunds{0}, digs{0}, faults{0};
} S;

// ============================================================================================= symbols =========

using CurrentEntityIdFn = uint32_t*(__fastcall*)(void* ctx, uint32_t* out);
using JobStorageFn = void*(__fastcall*)(void* job);
using ActorStateTestFn = bool(__fastcall*)(void* actor, uint8_t flag);
using CursorFlagsFn = void(__fastcall*)(uint32_t* flags, void* cctx, void* query, int64_t* xf, uint8_t a, uint8_t b,
                                        uint32_t c);
using ApplyMaterialsFn = bool(__fastcall*)(uint8_t* cost, int64_t* list, uint8_t take);
using DamageFn = uint8_t*(__fastcall*)(uint8_t* out, void* settings, void* setup, uint8_t hardness, uint8_t cls);
using TerraformFn = uint8_t*(__fastcall*)(uint8_t* out, void** args, int64_t* ctx);

struct Fn {
    CurrentEntityIdFn current_entity = nullptr;
    JobStorageFn job_storage = nullptr;
    ActorStateTestFn actor_state = nullptr;
    uintptr_t fly_site = 0;
    CursorFlagsFn cursor = nullptr;
    ApplyMaterialsFn apply = nullptr;
    DamageFn damage = nullptr;
    TerraformFn terraform = nullptr;
} fn;

struct Layout {
    int64_t storage_kind = -1;
    int64_t fly_entity = 0, fly_actor = 0;  // rbp displacements at the gate
    int64_t tx_operator = 0;                // InventoryTx: the acting entity
    int64_t arg_offset = 0, arg_base = 0, arg_stride = 0;
    int64_t programs_table = 0, programs_stride = 0, programs_count = 0;
} L;

SafetyHookMid h_fly;
SafetyHookInline h_cursor, h_apply, h_damage, h_terraform;
uint32_t g_hooks = 0;
std::mutex g_detail_mu;
std::string g_detail = "starting";

// Actor state flags (keen::actor::StateFlag).
constexpr uint8_t kDead = 7, kHangGliding = 37;

// Cursor flags (keen::ecs::CursorFlag) each rule waives.
constexpr uint32_t kOutsideZone = 1u << 1, kInFog = 1u << 2, kLenientInhibitor = 1u << 4, kStrictInhibitor = 1u << 5,
                   kEnemies = 1u << 9, kMissingMaterials = 1u << 12, kNoPermission = 1u << 14;

// ---- which world a program call belongs to ----
// 0: the server world (also a client's hosted world); anything else: a client's own worlds (prediction, client).
int world_kind_of(void* ctx) {
    void* job = *static_cast<void**>(ctx);
    if (!job || !fn.job_storage || L.storage_kind < 0) return -1;
    const auto* storage = static_cast<const uint8_t*>(fn.job_storage(job));
    return storage ? storage[L.storage_kind] : -1;
}

// The rules for one program call: by entity in the server world, the local player's elsewhere (a client's prediction
// world and cursor act only for the local player).
const Slot* rules_for(int kind, uint32_t entity) {
    if (kind == 0) return slot_of(entity);
    if (kind > 0) return &g_local;
    return nullptr;
}

// ============================================================================================= flight gate =====

void fly_body(safetyhook::Context& c) {
    if ((c.rbx & 0xFF) != 0) return;  // the game already allows it
    void* ctx = reinterpret_cast<void*>(c.rdi);
    const int kind = world_kind_of(ctx);
    const uint32_t entity = *reinterpret_cast<const uint32_t*>(c.rbp + L.fly_entity);
    const Slot* s = rules_for(kind, entity);
    if (!s || !s->any.load(std::memory_order_acquire) || !(s->flags.load(std::memory_order_relaxed) & KILN_RULES_FLY_ANYWHERE))
        return;
    void* actor = *reinterpret_cast<void**>(c.rbp + L.fly_actor);
    if (!actor || fn.actor_state(actor, kDead) || fn.actor_state(actor, kHangGliding)) return;
    c.rbx = (c.rbx & ~uintptr_t{0xFF}) | 1;
    S.fly.fetch_add(1, std::memory_order_relaxed);
}

void fly_guarded(safetyhook::Context& c, unsigned long* code) {
    __try {
        fly_body(c);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

void hk_fly(safetyhook::Context& c) {
    unsigned long code = 0;
    fly_guarded(c, &code);
}

// ============================================================================================= cursor ==========

uint32_t waived(const Slot& s) {
    const uint32_t f = s.flags.load(std::memory_order_relaxed);
    uint32_t m = 0;
    if (f & KILN_RULES_BUILD_OUTSIDE_BASES) m |= kOutsideZone;
    if (f & KILN_RULES_IGNORE_NO_BUILD_ZONES) m |= kLenientInhibitor | kStrictInhibitor;
    if (f & KILN_RULES_IGNORE_ENEMIES) m |= kEnemies;
    if (f & KILN_RULES_IGNORE_SHROUD) m |= kInFog;
    if (f & KILN_RULES_IGNORE_BASE_PERMISSIONS) m |= kNoPermission;
    if (rd(s.cost) == 0.0f) m |= kMissingMaterials;
    return m;
}

void cursor_body(uint32_t* flags, void* cctx) {
    void* ctx = *static_cast<void**>(cctx);  // the cursor programs keep their ProgramContext first
    if (!ctx) return;
    const int kind = world_kind_of(ctx);
    uint32_t entity = 0;
    if (kind == 0) fn.current_entity(ctx, &entity);
    const Slot* s = rules_for(kind, entity);
    if (!s || !s->any.load(std::memory_order_acquire)) return;
    const uint32_t m = waived(*s);
    if (*flags & m) {
        *flags &= ~m;
        S.cursor.fetch_add(1, std::memory_order_relaxed);
    }
}

bool cursor_guarded(uint32_t* flags, void* cctx, unsigned long* code) {
    __try {
        cursor_body(flags, cctx);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void __fastcall hk_cursor(uint32_t* flags, void* cctx, void* query, int64_t* xf, uint8_t a, uint8_t b, uint32_t c) {
    h_cursor.call<void>(flags, cctx, query, xf, a, b, c);
    unsigned long code = 0;
    if (flags && cctx && !cursor_guarded(flags, cctx, &code)) S.faults.fetch_add(1, std::memory_order_relaxed);
}

// ============================================================================================= costs ===========

// The update functions of the building programs, by name from the server world's program table (names are the
// programs' identity; their code moves between builds).
std::atomic<uintptr_t> g_build_fns[4];
std::atomic<bool> g_build_fns_ready{false};
constexpr const char* kBuildPrograms[4] = {"player_building_blueprints", "player_building_place_prop",
                                           "player_building_undo", "player_building_dismantle"};

// The function a return address belongs to: its runtime-function entry, following chained unwind info to the primary
// (MSVC splits large functions into chunks).
uintptr_t function_of(uintptr_t ret) {
    DWORD64 base = 0;
    auto* rf = RtlLookupFunctionEntry(ret, &base, nullptr);
    for (int i = 0; rf && i < 8; ++i) {
        const auto* ui = reinterpret_cast<const uint8_t*>(base + rf->UnwindData);
        if (!((ui[0] >> 3) & UNW_FLAG_CHAININFO)) return base + rf->BeginAddress;
        const uint8_t codes = ui[2];
        rf = reinterpret_cast<PRUNTIME_FUNCTION>(const_cast<uint8_t*>(ui) + 4 + ((codes + 1) & ~1) * 2);
    }
    return 0;
}

// Return address -> is it one of the building programs (cached: a handful of call sites).
std::mutex g_site_mu;
std::unordered_map<uintptr_t, bool> g_sites;

bool from_building(uintptr_t ret) {
    if (!g_build_fns_ready.load(std::memory_order_acquire)) return false;
    {
        std::lock_guard lock(g_site_mu);
        if (auto it = g_sites.find(ret); it != g_sites.end()) return it->second;
    }
    const uintptr_t f = function_of(ret);
    bool hit = false;
    for (auto& b : g_build_fns) hit = hit || (f && f == b.load(std::memory_order_relaxed));
    std::lock_guard lock(g_site_mu);
    g_sites.emplace(ret, hit);
    return hit;
}

struct Entry {  // one materials-list entry (0x18 bytes); the item at +0x10, the count at +0x14
    uint8_t head[0x10];
    uint32_t item;
    uint32_t count;
};
static_assert(sizeof(Entry) == 0x18);
constexpr uint64_t kMaxEntries = 64;

// Scales the list into `copy`; false when the call is not ours to change.
bool scale(uint8_t* cost, int64_t* list, uint8_t take, uintptr_t ret, Entry* copy, int64_t* out_list, bool* empty) {
    if (!cost || !list || cost[0] != 0) return false;  // mode 0: the server's inventory transaction
    const auto* tx = *reinterpret_cast<uint8_t* const*>(cost + 8);
    if (!tx) return false;
    const uint32_t player = *reinterpret_cast<const uint32_t*>(tx + L.tx_operator);
    const Slot* sp = slot_of(player);
    if (!sp) return false;
    const Slot& s = *sp;
    if (!s.any.load(std::memory_order_acquire)) return false;
    const float m = take ? rd(s.cost) : rd(s.refund);
    if (m == 1.0f || !from_building(ret)) return false;
    const auto* src = reinterpret_cast<const Entry*>(list[0]);
    const uint64_t n = static_cast<uint64_t>(list[1]);
    if (!src || n > kMaxEntries) return false;
    uint64_t k = 0;
    for (uint64_t i = 0; i < n; ++i) {
        const double v = std::floor(static_cast<double>(src[i].count) * m + 0.5);
        if (v < 1.0) continue;
        copy[k] = src[i];
        copy[k].count = v > 4.0e9 ? 4000000000u : static_cast<uint32_t>(v);
        ++k;
    }
    out_list[0] = reinterpret_cast<int64_t>(copy);
    out_list[1] = static_cast<int64_t>(k);
    *empty = k == 0;
    (take ? S.costs : S.refunds).fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool scale_guarded(uint8_t* cost, int64_t* list, uint8_t take, uintptr_t ret, Entry* copy, int64_t* out_list,
                   bool* empty, unsigned long* code) {
    __try {
        return scale(cost, list, take, ret, copy, out_list, empty);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool __fastcall hk_apply(uint8_t* cost, int64_t* list, uint8_t take) {
    const auto ret = reinterpret_cast<uintptr_t>(_ReturnAddress());
    Entry copy[kMaxEntries];
    int64_t scaled[2] = {0, 0};
    bool empty = false;
    unsigned long code = 0;
    if (!scale_guarded(cost, list, take, ret, copy, scaled, &empty, &code)) {
        if (code) S.faults.fetch_add(1, std::memory_order_relaxed);
        return h_apply.call<bool>(cost, list, take);
    }
    if (empty) return true;  // nothing left to take or give
    return h_apply.call<bool>(cost, scaled, take);
}

// ============================================================================================= mining ==========

thread_local uint32_t t_digger = 0;  // the player whose terraforming runs on this thread

uint32_t terraform_player(void** args, int64_t* ctx) {
    const uint32_t* handles = *reinterpret_cast<uint32_t* const*>(args);
    const auto table = static_cast<uintptr_t>(ctx[0]);
    const auto base = *reinterpret_cast<const uintptr_t*>(reinterpret_cast<const uint8_t*>(ctx) + L.arg_base);
    const uint32_t h = handles[0];
    if (!h) return 0;
    const uint16_t off =
        *reinterpret_cast<const uint16_t*>(table + static_cast<uintptr_t>(L.arg_offset) + (h & 0xFFFFu) * L.arg_stride);
    uint32_t player = 0;
    std::memcpy(&player, reinterpret_cast<const void*>(base + off), 4);
    return player;
}

uint32_t terraform_player_guarded(void** args, int64_t* ctx, unsigned long* code) {
    __try {
        return terraform_player(args, ctx);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

uint8_t* __fastcall hk_terraform(uint8_t* out, void** args, int64_t* ctx) {
    unsigned long code = 0;
    const uint32_t p = args && ctx ? terraform_player_guarded(args, ctx, &code) : 0;
    if (code) S.faults.fetch_add(1, std::memory_order_relaxed);
    const uint32_t outer = t_digger;
    t_digger = p;
    uint8_t* r = h_terraform.call<uint8_t*>(out, args, ctx);
    t_digger = outer;
    return r;
}

uint8_t* __fastcall hk_damage(uint8_t* out, void* settings, void* setup, uint8_t hardness, uint8_t cls) {
    uint8_t* r = h_damage.call<uint8_t*>(out, settings, setup, hardness, cls);
    const uint32_t p = t_digger;
    if (!p || !r) return r;
    const Slot* sp = slot_of(p);
    if (!sp) return r;
    const Slot& s = *sp;
    if (!s.any.load(std::memory_order_acquire)) return r;
    const float m = rd(s.dig);
    // {u8 error; u32; float damage}: only a computed, non-zero damage changes (zero damage and "unbreakable" stay so)
    if (m != 1.0f && r[0] == 0) {
        float d;
        std::memcpy(&d, r + 8, 4);
        if (d > 0.0f) {
            d *= m;
            std::memcpy(r + 8, &d, 4);
            S.digs.fetch_add(1, std::memory_order_relaxed);
        }
    }
    return r;
}

// ============================================================================================= world ===========

bool read_program_table(uintptr_t world, uintptr_t out[4], unsigned long* code) {
    __try {
        const uint16_t count = *reinterpret_cast<const uint16_t*>(world + L.programs_count);
        for (uint32_t i = 0; i < count && i < 4096; ++i) {
            const uintptr_t e = world + static_cast<uintptr_t>(L.programs_table) + i * static_cast<uintptr_t>(L.programs_stride);
            const char* name = *reinterpret_cast<const char* const*>(e + 0x10);
            const uint64_t len = *reinterpret_cast<const uint64_t*>(e + 0x18);
            if (!name || len == 0 || len > 128) continue;
            for (int k = 0; k < 4; ++k)
                if (len == std::strlen(kBuildPrograms[k]) && std::memcmp(name, kBuildPrograms[k], len) == 0)
                    out[k] = *reinterpret_cast<const uintptr_t*>(e + 0x28);
        }
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void resolve_build_programs() {
    if (g_build_fns_ready.load() || !(g_hooks & KILN_RULES_HOOK_COSTS)) return;
    const uintptr_t world = g_api->server_world();
    if (!world) return;
    uintptr_t f[4] = {};
    unsigned long code = 0;
    if (!read_program_table(world, f, &code)) {
        LOGE("reading the program table faulted (0x%08lx); building costs stay the game's", code);
        g_hooks &= ~KILN_RULES_HOOK_COSTS;
        return;
    }
    int found = 0;
    for (int k = 0; k < 4; ++k) {
        g_build_fns[k].store(f[k]);
        found += f[k] != 0;
    }
    if (found == 0) return;  // the table is not filled yet
    g_build_fns_ready.store(true, std::memory_order_release);
    HMODULE exe = GetModuleHandleW(nullptr);
    const auto base = reinterpret_cast<uintptr_t>(exe);
    LOGI("building programs: blueprints +0x%llx, place_prop +0x%llx, undo +0x%llx, dismantle +0x%llx",
         static_cast<unsigned long long>(f[0] ? f[0] - base : 0), static_cast<unsigned long long>(f[1] ? f[1] - base : 0),
         static_cast<unsigned long long>(f[2] ? f[2] - base : 0), static_cast<unsigned long long>(f[3] ? f[3] - base : 0));
}

void KILN_CALL on_pre(const KilnTickInfo*, void*) { resolve_build_programs(); }

void clear_all() {
    for (auto& s : g_players) store(s, nullptr);
    std::lock_guard lock(g_extra_mu);
    for (int i = 0; i < kExtra; ++i) {
        g_extra_ids[i].store(0, std::memory_order_release);
        store(g_extra[i], nullptr);
    }
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind != KILN_WORLD_SERVER || ev->event != KILN_WORLD_STOPPING) return;
    clear_all();
    LOGI("server world closing: every player's rules cleared");
}

// ============================================================================================= table ===========

KilnResult KILN_CALL t_set(KilnEntity entity, const KilnRulesPlayer* r) {
    if (entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (r && (r->size < sizeof(KilnRulesPlayer) || r->reserved != 0)) return KILN_ERR_INVALID_ARGUMENT;
    if (entity <= kMaxPlayer) {
        store(g_players[entity], r);
        return KILN_OK;
    }
    std::lock_guard lock(g_extra_mu);
    int at = -1, free_at = -1;
    for (int i = 0; i < kExtra; ++i) {
        const uint32_t id = g_extra_ids[i].load(std::memory_order_relaxed);
        if (id == entity) at = i;
        else if (id == 0 && free_at < 0) free_at = i;
    }
    const bool clearing = !r || is_default(*r);
    if (at < 0) {
        if (clearing) return KILN_OK;
        if (free_at < 0) return KILN_ERR_UNAVAILABLE;  // 16 other entities already have rules
        at = free_at;
    }
    store(g_extra[at], clearing ? nullptr : r);
    g_extra_ids[at].store(clearing ? 0 : entity, std::memory_order_release);
    return KILN_OK;
}

void KILN_CALL t_clear() { clear_all(); }

KilnResult KILN_CALL t_get(KilnEntity entity, KilnRulesPlayer* out) {
    if (!out || entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    if (const Slot* s = slot_of(entity)) load(*s, out);
    else load(Slot{}, out);
    return KILN_OK;
}

KilnResult KILN_CALL t_set_local(const KilnRulesPlayer* r) {
    if (r && (r->size < sizeof(KilnRulesPlayer) || r->reserved != 0)) return KILN_ERR_INVALID_ARGUMENT;
    store(g_local, r);
    return KILN_OK;
}

KilnResult KILN_CALL t_status(KilnRulesStatus* out) {
    if (!out || out->size < sizeof(KilnRulesStatus)) return KILN_ERR_INVALID_ARGUMENT;
    std::memset(out, 0, sizeof *out);
    out->size = sizeof *out;
    out->hooks = g_hooks;
    uint32_t n = 0;
    for (auto& s : g_players) n += s.any.load(std::memory_order_relaxed);
    for (auto& s : g_extra) n += s.any.load(std::memory_order_relaxed);
    out->players = n;
    out->local_flags = g_local.flags.load();
    out->fly_allowed = S.fly.load();
    out->cursor_cleared = S.cursor.load();
    out->costs_scaled = S.costs.load();
    out->refunds_scaled = S.refunds.load();
    out->digs_scaled = S.digs.load();
    out->faults = S.faults.load();
    std::lock_guard lock(g_detail_mu);
    std::snprintf(out->detail, sizeof out->detail, "%s", g_detail.c_str());
    return KILN_OK;
}

const KilnRulesApi kTable = {
    {sizeof(KilnRulesApi), KILN_RULES_VERSION}, &t_set, &t_clear, &t_get, &t_set_local, &t_status,
};

// ============================================================================================= boot ============

template <typename F>
bool sym(const char* name, F* out) {
    uintptr_t a = 0;
    if (g_api->symbol(KilnStr{name, std::strlen(name)}, &a) != KILN_OK || !a) return false;
    *out = reinterpret_cast<F>(a);
    return true;
}
bool cst(const char* name, int64_t* out) { return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK; }
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

template <typename H>
bool inline_hook(H& h, void* target, void* detour, const char* what) {
    h = safetyhook::create_inline(target, detour);
    if (!h) LOGE("hooking %s failed", what);
    return static_cast<bool>(h);
}

void install() {
    std::string off;
    auto note = [&off](const char* s) { off += off.empty() ? s : std::string(", ") + s; };
    const bool common = sym("rules/CurrentEntityId", &fn.current_entity) && sym("items/JobStorage", &fn.job_storage) &&
                        cst("storage.kind", &L.storage_kind);
    // flight
    if (common && feat("rules.fly") && sym("rules/FlyGate", &fn.fly_site) && sym("rules/ActorStateTest", &fn.actor_state) &&
        cst("rules/fly.entity", &L.fly_entity) && cst("rules/fly.actor", &L.fly_actor)) {
        h_fly = safetyhook::create_mid(reinterpret_cast<void*>(fn.fly_site), &hk_fly);
        if (h_fly) g_hooks |= KILN_RULES_HOOK_FLY;
        else LOGE("hooking the fly-mode gate failed");
    } else {
        note("flight (rules.fly)");
    }
    // cursor
    if (common && feat("rules.cursor") && sym("rules/CursorFlags", &fn.cursor) &&
        inline_hook(h_cursor, reinterpret_cast<void*>(fn.cursor), reinterpret_cast<void*>(&hk_cursor), "the cursor flags"))
        g_hooks |= KILN_RULES_HOOK_CURSOR;
    else note("placement (rules.cursor)");
    // costs: server worlds only (a dedicated server, or a client hosting one)
    if (feat("rules.costs") && sym("rules/ApplyMaterials", &fn.apply) && cst("items/tx.operator", &L.tx_operator) &&
        cst("world/programs.table", &L.programs_table) && cst("world/programs.stride", &L.programs_stride) &&
        cst("world/programs.count", &L.programs_count) &&
        inline_hook(h_apply, reinterpret_cast<void*>(fn.apply), reinterpret_cast<void*>(&hk_apply), "ApplyMaterials"))
        g_hooks |= KILN_RULES_HOOK_COSTS;
    else note("costs (rules.costs)");
    // mining
    if (feat("rules.dig") && sym("rules/TerrainDamage", &fn.damage) && sym("gameplay/TerraformImpact", &fn.terraform) &&
        cst("gameplay/impact.arg_offset", &L.arg_offset) && cst("gameplay/impact.arg_base", &L.arg_base) &&
        cst("gameplay/impact.arg_stride", &L.arg_stride) &&
        inline_hook(h_damage, reinterpret_cast<void*>(fn.damage), reinterpret_cast<void*>(&hk_damage), "the terrain damage") &&
        inline_hook(h_terraform, reinterpret_cast<void*>(fn.terraform), reinterpret_cast<void*>(&hk_terraform), "TerraformImpact"))
        g_hooks |= KILN_RULES_HOOK_DIG;
    else note("mining (rules.dig)");
    {
        std::lock_guard lock(g_detail_mu);
        g_detail = off.empty() ? "every hook installed" : "off on this build: " + off;
    }
    LOGI("hooks: flight %s, placement %s, costs %s, mining %s", (g_hooks & KILN_RULES_HOOK_FLY) ? "on" : "OFF",
         (g_hooks & KILN_RULES_HOOK_CURSOR) ? "on" : "OFF", (g_hooks & KILN_RULES_HOOK_COSTS) ? "on" : "OFF",
         (g_hooks & KILN_RULES_HOOK_DIG) ? "on" : "OFF");
}

}  // namespace

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = KilnStr{kName, std::strlen(kName)};
    info->version = KilnStr{"1", 1};
    install();
    KilnHandle h = 0;
    api->subscribe_world(owner, &on_world, nullptr, &h);
    api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h);
    // Published even with every hook off: set/get work, status() says what is off.
    return api->register_service(owner, KilnStr{KILN_RULES_SERVICE, std::strlen(KILN_RULES_SERVICE)}, &kTable);
}
