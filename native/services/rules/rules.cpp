// SPDX-License-Identifier: MIT
// kiln.rules: per-player rules applied where the game decides them (native/core/abi/services/kiln_rules.h,
// docs/re/rules.md). The managed Kiln.Rules works out each player's rules from every mod's rule sets and pushes them
// here; the hooks below only read them.
//
//  - flight gate: a mid hook where player_control_action_sequence has decided whether the building fly mode may run
//    (bl); a player with FLY_ANYWHERE gets "may" unless dead or gliding. Server world and the client's prediction world.
//  - building zone: where server_cursor_no_prediction asks BuildZoneSystem whether the player's cursor is near a
//    building zone (ServerCursor.serverFlags IsNearBuildingZone); a player with BUILD_OUTSIDE_BASES gets "yes". The game
//    copies that flag into the actor state and the replicated NetworkCursor, so the hammer's menu ("Needs a Flame Altar
//    nearby"), the fly-mode gate and the cursor all treat the player as inside a base. Server world only.
//  - cursor: after the function that computes the build cursor's refusal flags (both cursor programs call it), the
//    flags the player's rules waive are cleared.
//  - costs: ApplyMaterials (take or give a materials list for a building action); called from the four building
//    programs it is given a scaled copy of the list.
//  - mining: TerraformImpact notes the acting player for its thread; the per-material damage function scales its result.
//  - gliding: locomotion_execution's glide step is wrapped (the boost along the heading while sprint is held, the coast
//    after it, holding altitude at sink 0); the gravity program's strength is scaled for a gliding actor (the sink); the
//    glider's stamina drain is zeroed (unlimited). Server world and the client's prediction world alike: the result
//    depends only on the game's own state, the input and the rules, so the server and the player's game agree.
//  - Shroud: where fog_resistance_update dispatches on the player's fog type (bl), a player with SHROUD_FREEZE (or a
//    shroud_floor) gets the "no drain" branch while in the Shroud, or the recovery branch while below the floor. Server
//    world only; the HUD shows the server's replicated value.
//
//  - client guards (the game client only; client.d/rules.json): fill_ui_healthbar's line of sight from the camera to
//    each health bar is made zero-length when it is longer than 1 km or not finite (a broken camera 2^31 m off made
//    every ray walk ~1.5e9 voxel cells and the game froze), and a read-only check of the camera ClientPlayerInput
//    gets each frame logs once when it is outside the world or far from the player, with the camera's own fields.
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
#include "services/kiln_client.h"
#include "services/kiln_rules.h"
#include "services/kiln_world.h"

#include "glide_math.h"

static_assert(sizeof(KilnRulesPlayer) == 56);
static_assert(sizeof(KilnRulesStatus) == 256 && offsetof(KilnRulesStatus, detail) == 64 &&
              offsetof(KilnRulesStatus, zone_forced) == 224);

namespace {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
constexpr const char* kName = "kiln.rules";

void logf(int level, const char* fmt, ...) {
    char b[1024];  // the client camera check's lines are long
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
    std::atomic<uint32_t> shroud{0};
    std::atomic<uint32_t> sink{std::bit_cast<uint32_t>(1.0f)};
    std::atomic<uint32_t> thrust{0};
    std::atomic<uint32_t> max_speed{0};
    std::atomic<uint32_t> lift{0};
    std::atomic<uint32_t> coast{0};
    std::atomic<uint32_t> climb{0};
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

// Hard bounds on what the glider hooks may do (the managed rules allow less: boost speed 120 m/s at most). Kiln never
// writes a non-finite velocity, nor one faster than kMaxKilnSpeed (unless the game's own was faster: then it is left).
constexpr float kMaxGlideSink = 10.0f;     // x the glider's sink
constexpr float kMaxGlideAccel = 100.0f;   // thrust, lift, coast, m/s^2
constexpr float kMaxGlideSpeed = 120.0f;   // the boost's cap, m/s
constexpr float kMaxKilnSpeed = 150.0f;    // any velocity a glider hook writes, m/s

constexpr KilnRulesPlayer kDefault{sizeof(KilnRulesPlayer), 0, 1.0f, 1.0f, 1.0f, 0, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0};

bool is_default(const KilnRulesPlayer& r) {
    return r.flags == 0 && r.build_cost == 1.0f && r.build_refund == 1.0f && r.dig_damage == 1.0f && r.shroud_floor == 0.0f &&
           r.glide_sink == 1.0f;
}

// A caller's struct, any accepted size, as the current one (the fields an older caller does not know keep defaults).
bool read_in(const KilnRulesPlayer* r, KilnRulesPlayer* out) {
    *out = kDefault;
    if (!r) return true;
    if (r->size < KILN_RULES_PLAYER_V1_SIZE || r->reserved != 0) return false;
    std::memcpy(out, r, std::min<size_t>(r->size, sizeof *out));
    out->size = sizeof *out;
    out->reserved2 = 0;
    return true;
}

void store(Slot& s, const KilnRulesPlayer* r) {
    KilnRulesPlayer d = kDefault;
    if (r) d = *r;
    auto clean = [](float v, float fallback) { return std::isfinite(v) && v >= 0.0f ? v : fallback; };
    d.build_cost = clean(d.build_cost, 1.0f);
    d.build_refund = clean(d.build_refund, 1.0f);
    d.dig_damage = clean(d.dig_damage, 1.0f);
    d.shroud_floor = std::min(clean(d.shroud_floor, 0.0f), 1.0f);
    // the glider's values are bounded here too, whoever calls: a hook never makes a speed the game cannot handle
    d.glide_sink = std::min(clean(d.glide_sink, 1.0f), kMaxGlideSink);
    d.glide_thrust = std::min(clean(d.glide_thrust, 0.0f), kMaxGlideAccel);
    d.glide_max_speed = std::min(clean(d.glide_max_speed, 0.0f), kMaxGlideSpeed);
    d.glide_lift = std::min(clean(d.glide_lift, 0.0f), kMaxGlideAccel);
    d.glide_coast = std::min(clean(d.glide_coast, 0.0f), kMaxGlideAccel);
    d.glide_climb = std::min(clean(d.glide_climb, 0.0f), kMaxGlideSpeed);
    s.flags.store(d.flags, std::memory_order_relaxed);
    s.cost.store(std::bit_cast<uint32_t>(d.build_cost), std::memory_order_relaxed);
    s.refund.store(std::bit_cast<uint32_t>(d.build_refund), std::memory_order_relaxed);
    s.dig.store(std::bit_cast<uint32_t>(d.dig_damage), std::memory_order_relaxed);
    s.shroud.store(std::bit_cast<uint32_t>(d.shroud_floor), std::memory_order_relaxed);
    s.sink.store(std::bit_cast<uint32_t>(d.glide_sink), std::memory_order_relaxed);
    s.thrust.store(std::bit_cast<uint32_t>(d.glide_thrust), std::memory_order_relaxed);
    s.max_speed.store(std::bit_cast<uint32_t>(d.glide_max_speed), std::memory_order_relaxed);
    s.lift.store(std::bit_cast<uint32_t>(d.glide_lift), std::memory_order_relaxed);
    s.coast.store(std::bit_cast<uint32_t>(d.glide_coast), std::memory_order_relaxed);
    s.climb.store(std::bit_cast<uint32_t>(d.glide_climb), std::memory_order_relaxed);
    s.any.store(!is_default(d), std::memory_order_release);
}

void load(const Slot& s, KilnRulesPlayer* out) {
    *out = kDefault;
    out->flags = s.flags.load(std::memory_order_relaxed);
    out->build_cost = rd(s.cost);
    out->build_refund = rd(s.refund);
    out->dig_damage = rd(s.dig);
    out->shroud_floor = rd(s.shroud);
    out->glide_sink = rd(s.sink);
    out->glide_thrust = rd(s.thrust);
    out->glide_max_speed = rd(s.max_speed);
    out->glide_lift = rd(s.lift);
    out->glide_coast = rd(s.coast);
    out->glide_climb = rd(s.climb);
}

struct Stats {
    std::atomic<uint64_t> fly{0}, zone{0}, cursor{0}, costs{0}, refunds{0}, digs{0}, shroud{0}, glide{0}, faults{0};
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
using GlideStepFn = void(__fastcall*)(uint8_t* it);
using QuatRotateFn = float*(__fastcall*)(float* out, const float* quat, const float* v);
using StepDeltaFn = int64_t*(__fastcall*)(void* ctx, int64_t* out);

struct Fn {
    CurrentEntityIdFn current_entity = nullptr;
    JobStorageFn job_storage = nullptr;
    ActorStateTestFn actor_state = nullptr;
    uintptr_t fly_site = 0;
    uintptr_t zone_site = 0;
    uintptr_t shroud_site = 0;
    CursorFlagsFn cursor = nullptr;
    ApplyMaterialsFn apply = nullptr;
    DamageFn damage = nullptr;
    TerraformFn terraform = nullptr;
    GlideStepFn glide = nullptr;
    QuatRotateFn quat_rotate = nullptr;
    StepDeltaFn step_delta = nullptr;
    uintptr_t grav_site = 0, stam_site = 0;
    uintptr_t ray_site = 0, cam_site = 0;  // client guards
} fn;

struct Layout {
    int64_t storage_kind = -1;
    int64_t fly_entity = 0, fly_actor = 0;  // rbp displacements at the gate
    int64_t fog_attr = 0, fog_actor = 0, fog_state = 0;  // fog_resistance_update: rsp slots; the actor's state word
    // locomotion_execution's iterator: ActorInput, CurrentTransform, Velocity, PlayerInput slots; transform rotation;
    // ActorInput's trigger; PlayerInput's digital-input mask (the held bits follow it)
    int64_t glide_input = 0, glide_xform = 0, glide_velocity = 0, glide_player_input = 0, xform_rotation = 0,
            input_trigger = 0, input_mask = 0;
    int64_t grav_actor = 0;  // gravity_apply_velocity_actor: the Actor slot, rbp + (int8)
    int64_t input_camera_rot = -1;  // PlayerInput: the camera's orientation (fromClient.camera), for the plane's pitch
    int64_t tx_operator = 0;                // InventoryTx: the acting entity
    int64_t arg_offset = 0, arg_base = 0, arg_stride = 0;
    int64_t programs_table = 0, programs_stride = 0, programs_count = 0;
    // client guards: fill_ui_healthbar's player z slot (rbp); the client game's camera object (rbp), ClientPlayerInput's
    // camera (r14), the camera object's pivot (its float fields follow)
    int64_t hb_player_z = 0, cam_obj = 0, cpi_camera = 0, cam_pivot = 0;
} L;

SafetyHookMid h_fly, h_zone, h_shroud, h_grav, h_stam, h_ray, h_cam;
SafetyHookInline h_cursor, h_apply, h_damage, h_terraform, h_glide;
const KilnClientApi* g_client = nullptr;
uint32_t g_hooks = 0;
std::mutex g_detail_mu;
std::string g_detail = "starting";

// Actor state flags (keen::actor::StateFlag).
constexpr uint8_t kDead = 7, kHangGliding = 37;
constexpr uint64_t kInFogBit = 1ull << 13;  // keen::actor::StateFlag InFog, in the actor's state word

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

// ============================================================================================= building zone ===

// Right after `call IsInBuildZone(BuildZoneSystem, cursor position)` in server_cursor_no_prediction: al is the answer,
// r12 the ProgramContext (the program reads the current entity through it right below).
void zone_body(safetyhook::Context& c) {
    if ((c.rax & 0xFF) != 0) return;  // already near a building zone
    void* ctx = reinterpret_cast<void*>(c.r12);
    if (!ctx) return;
    const int kind = world_kind_of(ctx);
    if (kind != 0) return;  // the server cursor runs in the server world (also a hosting game's)
    uint32_t entity = 0;
    fn.current_entity(ctx, &entity);
    const Slot* s = slot_of(entity);
    if (!s || !s->any.load(std::memory_order_acquire) ||
        !(s->flags.load(std::memory_order_relaxed) & KILN_RULES_BUILD_OUTSIDE_BASES))
        return;
    c.rax = (c.rax & ~uintptr_t{0xFF}) | 1;
    S.zone.fetch_add(1, std::memory_order_relaxed);
}

void zone_guarded(safetyhook::Context& c, unsigned long* code) {
    __try {
        zone_body(c);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

void hk_zone(safetyhook::Context& c) {
    unsigned long code = 0;
    zone_guarded(c, &code);
}

// ============================================================================================= gliding =========

constexpr int kSprint = 53;           // PlayerInputType.Sprint: a held bit of PlayerInput's digital input
constexpr uint16_t kAbortTrigger = 24;  // ActorInput trigger ExternalAbortAndOverride (the glide was aborted)

uint32_t local_player() {
    if (!g_client) return 0;
    KilnClientState st{};
    st.size = sizeof st;
    return g_client->state(&st) == KILN_OK ? st.local_player : 0;
}

// The rules for a glider. The glider's programs run as parallel each-programs whose context holds a per-chunk job copy
// without the storage pointer, so the world is not told apart (world_kind_of does not work there); it need not be:
// the per-entity rules exist only where a server world runs (a dedicated server, a hosting game: there the host's
// prediction sees the same values as its server world), and a joined client has only the local player's.
const Slot* glide_rules(void* ctx) {
    uint32_t entity = 0;
    fn.current_entity(ctx, &entity);
    if (entity == 0) return nullptr;
    const Slot* s = slot_of(entity);
    if (s && s->any.load(std::memory_order_acquire)) return s;
    if (!g_local.any.load(std::memory_order_acquire)) return nullptr;
    const uint32_t local = local_player();
    if (entity == local) return &g_local;
    static std::atomic<bool> told{false};
    if (local != 0 && !told.exchange(true))
        LOGI("glider: entity %u in this game is not the local player %u (its rules are the server's)", entity, local);
    return nullptr;
}

struct GlidePre {
    const Slot* s = nullptr;
    void* ctx = nullptr;
    const uint8_t* input = nullptr;
    float v0[3] = {};
    uint16_t trigger = 0;
};

bool glide_pre(uint8_t* it, GlidePre* g) {
    void* ctx = *reinterpret_cast<void**>(it);
    const auto* input = *reinterpret_cast<const uint8_t* const*>(it + L.glide_player_input);
    if (!ctx || !input) return false;  // players only
    const Slot* s = glide_rules(ctx);
    if (!s) return false;
    const uint32_t f = s->flags.load(std::memory_order_relaxed);
    if (!(f & KILN_RULES_GLIDE_BOOST) && rd(s->sink) != 0.0f) return false;
    const auto* v = *reinterpret_cast<const float* const*>(it + L.glide_velocity);
    if (!v) return false;
    std::memcpy(g->v0, v, sizeof g->v0);
    const auto* ai = *reinterpret_cast<const uint8_t* const*>(it + L.glide_input);
    g->trigger = ai ? *reinterpret_cast<const uint16_t*>(ai + L.input_trigger) : 0;
    g->s = s;
    g->ctx = ctx;
    g->input = input;
    return true;
}

bool finite3(const float* v) { return std::isfinite(v[0]) && std::isfinite(v[1]) && std::isfinite(v[2]); }
float length3(const float* v) { return std::sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); }

// The guard on every velocity a glider hook writes: finite, and no faster than kMaxKilnSpeed (or the game's own speed
// in this step, when that was faster: a dive keeps its speed, Kiln only never adds to it). A result that fails is
// scaled down; one that cannot be (non-finite) is dropped and the game's velocity stands.
bool safe_velocity(float* v, const float* game) {
    if (!finite3(v)) return false;
    const float cap = std::max(kMaxKilnSpeed, finite3(game) ? length3(game) : 0.0f);
    const float n = length3(v);
    if (!std::isfinite(n)) return false;
    if (n > cap) {
        const float k = cap / n;
        for (int i = 0; i < 3; ++i) v[i] *= k;
    }
    return finite3(v);
}

void glide_post(uint8_t* it, const GlidePre& g) {
    const auto* ai = *reinterpret_cast<const uint8_t* const*>(it + L.glide_input);
    if (ai && *reinterpret_cast<const uint16_t*>(ai + L.input_trigger) == kAbortTrigger && g.trigger != kAbortTrigger)
        return;  // the game ended the glide in this step
    auto* out = *reinterpret_cast<float* const*>(it + L.glide_velocity);
    const auto* xf = *reinterpret_cast<const uint8_t* const*>(it + L.glide_xform);
    if (!out || !xf) return;
    // work on a copy: the game's velocity is written back only as a finite, bounded result
    float v[3] = {out[0], out[1], out[2]};
    if (!finite3(v) || !finite3(g.v0)) return;  // not ours to repair; never build on it
    int64_t ns_out = 0;
    const int64_t* ns = fn.step_delta(g.ctx, &ns_out);
    const double dt = ns ? std::min(static_cast<double>(*ns) * 1e-9, 0.25) : 0.0;
    if (!(dt > 0.0)) return;
    const float fwd[3] = {0.0f, 0.0f, 1.0f};
    float F[3] = {};
    fn.quat_rotate(F, reinterpret_cast<const float*>(xf + L.xform_rotation), fwd);
    const float len = std::sqrt(F[0] * F[0] + F[1] * F[1] + F[2] * F[2]);
    if (!(len > 0.5f && len < 2.0f)) return;
    for (float& x : F) x /= len;
    if (!finite3(F)) return;

    const Slot& s = *g.s;
    const uint32_t flags = s.flags.load(std::memory_order_relaxed);
    const bool hold = rd(s.sink) == 0.0f;
    float next[3] = {v[0], v[1], v[2]};
    if (flags & KILN_RULES_GLIDE_BOOST) {
        // like a plane (glide_math.h): the path's pitch from the camera (PlayerInput's copy of it: the same in the
        // server world and in the player's prediction), the energy trade, the thrust while sprint is held
        kiln_glide::Step st;
        std::memcpy(st.v0, g.v0, sizeof st.v0);
        std::memcpy(st.v, v, sizeof st.v);
        std::memcpy(st.heading, F, sizeof st.heading);
        if (L.input_camera_rot >= 0) {
            fn.quat_rotate(st.view, reinterpret_cast<const float*>(g.input + L.input_camera_rot), fwd);
            if (!finite3(st.view)) st.view[0] = st.view[1] = st.view[2] = 0.0f;
        }
        st.dt = static_cast<float>(dt);
        const uint64_t held = *reinterpret_cast<const uint64_t*>(g.input + L.input_mask + 8);
        st.boost = (held >> kSprint) & 1;
        kiln_glide::Params pr;
        pr.thrust = rd(s.thrust);
        pr.max_speed = rd(s.max_speed);
        pr.lift = rd(s.lift);
        pr.coast = rd(s.coast);
        pr.climb = rd(s.climb);
        pr.hold_altitude = hold;
        kiln_glide::plane_step(st, pr, next);
    } else if (hold && F[1] > -0.2f && next[1] < 0.0f) {
        // sink 0: hold altitude unless the glider points down (looking down still descends)
        next[1] = 0.0f;
    }
    if (next[0] == v[0] && next[1] == v[1] && next[2] == v[2]) return;
    if (!safe_velocity(next, out)) return;
    out[0] = next[0];
    out[1] = next[1];
    out[2] = next[2];
    S.glide.fetch_add(1, std::memory_order_relaxed);
}

bool glide_pre_guarded(uint8_t* it, GlidePre* g, unsigned long* code) {
    __try {
        return glide_pre(it, g);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void glide_post_guarded(uint8_t* it, const GlidePre& g, unsigned long* code) {
    __try {
        glide_post(it, g);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
    }
}

void __fastcall hk_glide(uint8_t* it) {
    GlidePre g;
    unsigned long code = 0;
    const bool ours = it && glide_pre_guarded(it, &g, &code);
    h_glide.call<void>(it);
    if (ours) glide_post_guarded(it, g, &code);
    if (code) S.faults.fetch_add(1, std::memory_order_relaxed);
}

// gravity_apply_velocity_actor, before it reads the step's time: xmm6 = the gravity strength for this actor, rdi the
// ProgramContext, [rbp + (int8)grav.actor] the Actor. Under the game's quadratic drag the steady sink goes with the root
// of gravity, so "x sink" is x sink^2 gravity.
void grav_body(safetyhook::Context& c) {
    void* actor = *reinterpret_cast<void**>(c.rbp + static_cast<int8_t>(L.grav_actor));
    if (!actor || !fn.actor_state(actor, kHangGliding)) return;
    const Slot* s = glide_rules(reinterpret_cast<void*>(c.rdi));
    if (!s) return;
    const float m = std::min(rd(s->sink), kMaxGlideSink);
    if (!(m >= 0.0f) || m == 1.0f) return;  // NaN and the game's own sink: unchanged
    const float g = c.xmm6.f32[0] * m * m;
    if (!std::isfinite(g) || !std::isfinite(c.xmm6.f32[0])) return;
    c.xmm6.f32[0] = g;
    S.glide.fetch_add(1, std::memory_order_relaxed);
}

// stamina_depletion_glider, before this step's drain is added: xmm6 = the drain, rdi the ProgramContext.
void stam_body(safetyhook::Context& c) {
    if (c.xmm6.f32[0] == 0.0f) return;
    const Slot* s = glide_rules(reinterpret_cast<void*>(c.rdi));
    if (!s || !(s->flags.load(std::memory_order_relaxed) & KILN_RULES_GLIDE_UNLIMITED)) return;
    c.xmm6.f32[0] = 0.0f;
    S.glide.fetch_add(1, std::memory_order_relaxed);
}

template <void (*Body)(safetyhook::Context&)>
void mid_guarded(safetyhook::Context& c) {
    __try {
        Body(c);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

void hk_grav(safetyhook::Context& c) { mid_guarded<grav_body>(c); }
void hk_stam(safetyhook::Context& c) { mid_guarded<stam_body>(c); }

// ============================================================================================= Shroud ==========

// fog_resistance_update, at `movzx ecx, bl` (the fog-type dispatch): bl = EnvironmentSensor.fogType after the safe-zone
// override (0 none, 1 none-temporary, 2 dangerous: -1/s, 3 deadly: -100/s; >= 2 also runs the death check when the
// timer is below 1 after the drain), r15 the ProgramContext, r13w / r12w the value slots of FogResistance's current and
// max (whole seconds), [rsp+fog.attr] the FogResistance component, [rsp+fog.actor] the Actor. Both were marked written
// earlier in the same iteration, so what changes here replicates and saves like the game's own.
//  bl = 1: the recovery branch (+25/s toward max, no death check)   bl = 4: no drain (the death check cannot fire)
void shroud_body(safetyhook::Context& c) {
    const uint8_t fog = static_cast<uint8_t>(c.rbx & 0xFF);
    if (fog < 2) return;  // not in the Shroud
    // fog_resistance_update runs as a parallel each-program: its context's job is a per-chunk copy without the
    // storage pointer (world_kind_of does not work here). The rules are the server's per-entity ones; a joined client
    // has none (it only has the local player's), so nothing changes there and the HUD shows the server's value.
    void* ctx = reinterpret_cast<void*>(c.r15);
    if (!ctx) return;
    uint32_t entity = 0;
    fn.current_entity(ctx, &entity);
    const Slot* s = slot_of(entity);
    if (!s || !s->any.load(std::memory_order_acquire)) return;
    const bool freeze = s->flags.load(std::memory_order_relaxed) & KILN_RULES_SHROUD_FREEZE;
    const float floor_frac = rd(s->shroud);
    if (!freeze && !(floor_frac > 0.0f)) return;
    const auto* comp = *reinterpret_cast<const uint8_t* const*>(c.rsp + L.fog_attr);
    if (!comp || !(comp[6] & 1)) return;
    const auto* values = comp + *reinterpret_cast<const uint16_t*>(comp + 4);
    const uint32_t count = comp[7];
    const uint32_t i_cur = static_cast<uint32_t>(c.r13 & 0xFFFF), i_max = static_cast<uint32_t>(c.r12 & 0xFFFF);
    if (i_cur >= count || i_max >= count) return;
    const int32_t cur = *reinterpret_cast<const int32_t*>(values + i_cur * 4);
    const int32_t max = *reinterpret_cast<const int32_t*>(values + i_max * 4);
    int32_t floor_units = floor_frac > 0.0f ? static_cast<int32_t>(std::ceil(std::min(floor_frac, 1.0f) * static_cast<float>(max))) : 0;
    if (freeze || floor_units > 0) floor_units = std::max(floor_units, 1);
    uint8_t to = fog;
    if (cur < floor_units) {
        to = 1;  // below the floor (or out of time): recover, never the death check
    } else if (freeze) {
        to = 4;
    } else {
        const int32_t margin = fog == 2 ? 2 : 12;  // more than one step's drain (1/s, 100/s) on a slow step
        if (cur - margin < floor_units) to = 4;
    }
    if (to == fog) return;
    c.rbx = (c.rbx & ~uintptr_t{0xFF}) | to;
    if (to == 4) {
        // the drain branch would have marked the player "in the Shroud"; keep that (HUD, music, Shroud effects)
        auto* actor = *reinterpret_cast<uint8_t* const*>(c.rsp + L.fog_actor);
        if (actor) *reinterpret_cast<uint64_t*>(actor + L.fog_state) |= kInFogBit;
    }
    S.shroud.fetch_add(1, std::memory_order_relaxed);
}

void shroud_guarded(safetyhook::Context& c, unsigned long* code) {
    __try {
        shroud_body(c);
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        S.faults.fetch_add(1, std::memory_order_relaxed);
    }
}

void hk_shroud(safetyhook::Context& c) {
    unsigned long code = 0;
    shroud_guarded(c, &code);
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

// ============================================================================================= client guards ===
// The game client only (docs/re/client.md, "The camera, health-bar line of sight, and a frozen client").
//  - rules.healthbar_guard: fill_ui_healthbar casts a voxel line of sight from ClientPlayerInput's camera to every
//    health bar within 150 m of the local player, with no bound on its length. A camera 2^31 m below the world (a
//    float the game turned into fixed point out of range) made each ray walk ~1.5e9 cells: 18 s per step. A ray longer
//    than kMaxRay, or a non-finite one, is made zero-length (the game's test returns at once: "nothing in the way").
//  - rules.camera_check: read only. Where the client writes ClientPlayerInput.camera from its camera object, a camera
//    outside the world (or not finite, or far from the player for a while) is logged once per session with the camera
//    object's fields and the recent frame times, so the next occurrence names the source.

constexpr float kMaxRay = 1000.0f;            // m; the program's own range is 150 m around the player
constexpr double kFarFromPlayer = 1000.0;     // m
constexpr double kWorldSize = 10240.0, kWorldHeight = 2560.0, kWorldMargin = 1000.0;  // docs/re/map.md
constexpr int kFarFrames = 120;               // a camera far from the player (inside the world) must stay so this long
constexpr uint64_t kPlayerFreshMs = 3000;      // a player position older than this is not compared
constexpr uint64_t kGuardStatsMs = 30000;      // at most one "more rays" line per this long
constexpr double kFixed = 1.0 / 4294967296.0;  // 32.32 fixed point -> m

const KilnWorldApi* g_world_api = nullptr;

// The local player's position as last seen: fill_ui_healthbar's own copy (client side) or the server world's (a host).
std::atomic<uint64_t> g_player_bits[3];
std::atomic<uint64_t> g_player_ms{0};
std::atomic<int> g_player_src{0};  // 1 the health-bar program, 2 the server world
constexpr const char* kPlayerSrc[] = {"unknown", "the health-bar program", "the server world"};

// Which client guards are installed (not in KilnRulesStatus.hooks: the rules ABI is unchanged).
constexpr uint32_t kGuardRays = 0x1u, kGuardCamera = 0x2u;
uint32_t g_client_guards = 0;

std::atomic<uint64_t> g_rays_guarded{0}, g_rays_reported{0}, g_rays_report_ms{0};  // a camera outside the world
std::atomic<uint64_t> g_rays_long{0};  // inside the world, over kMaxRay (a flight far from the character)
std::atomic<bool> g_ray_long_told{false};
// once per session each: a camera outside the world (or not finite), and one far from the player (which a slow camera
// after a respawn could also be: kept apart so it never hides the first)
std::atomic<bool> g_ray_told{false}, g_cam_told{false}, g_cam_far_told{false};
std::atomic<uint32_t> g_client_gen{0};  // client sessions: the camera check resets its own state when this changes

bool in_world(const double* p) {
    return std::isfinite(p[0]) && std::isfinite(p[1]) && std::isfinite(p[2]) && p[0] >= -kWorldMargin &&
           p[0] <= kWorldSize + kWorldMargin && p[2] >= -kWorldMargin && p[2] <= kWorldSize + kWorldMargin &&
           p[1] >= -kWorldMargin && p[1] <= kWorldHeight + kWorldMargin;
}

void note_player(const double* p, int src) {
    if (!in_world(p)) return;
    for (int i = 0; i < 3; ++i) g_player_bits[i].store(std::bit_cast<uint64_t>(p[i]), std::memory_order_relaxed);
    g_player_src.store(src, std::memory_order_relaxed);
    g_player_ms.store(GetTickCount64(), std::memory_order_release);
}

// The last player position, its source and age; false when there is none.
bool last_player(double* p, int* src, uint64_t* age_ms) {
    const uint64_t at = g_player_ms.load(std::memory_order_acquire);
    if (!at) return false;
    for (int i = 0; i < 3; ++i) p[i] = std::bit_cast<double>(g_player_bits[i].load(std::memory_order_relaxed));
    *src = g_player_src.load(std::memory_order_relaxed);
    *age_ms = GetTickCount64() - at;
    return true;
}

double dist3(const double* a, const double* b) {
    const double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
    return std::sqrt(dx * dx + dy * dy + dz * dz);
}

// A new client session starts with the guards' "log once" re-armed.
void client_session_reset() {
    g_rays_guarded.store(0);
    g_rays_reported.store(0);
    g_rays_report_ms.store(0);
    g_rays_long.store(0);
    g_ray_long_told.store(false);
    g_ray_told.store(false);
    g_cam_told.store(false);
    g_cam_far_told.store(false);
    g_player_ms.store(0);
    g_client_gen.fetch_add(1);
}

// fill_ui_healthbar, at `call LineOfSight(result, start, end, query)`: rdx = the start (the camera, float3), r8 = the
// end (the entity + 1.5 m); the local player's position in xmm14 (x), xmm15 (y) and [rbp + hb.player_z] (z).
void ray_body(safetyhook::Context& c) {
    auto* from = reinterpret_cast<float*>(c.rdx);
    auto* to = reinterpret_cast<float*>(c.r8);
    if (!from || !to) return;
    const double player[3] = {c.xmm14.f32[0], c.xmm15.f32[0], *reinterpret_cast<const float*>(c.rbp + L.hb_player_z)};
    note_player(player, 1);
    const bool finite = finite3(from) && finite3(to);
    const float dx = to[0] - from[0], dy = to[1] - from[1], dz = to[2] - from[2];
    const float len2 = dx * dx + dy * dy + dz * dz;
    if (finite && std::isfinite(len2) && len2 <= kMaxRay * kMaxRay) return;
    // zero length on a finite point: the end if it is finite, else the start, else the player
    const float before[6] = {from[0], from[1], from[2], to[0], to[1], to[2]};
    float at[3] = {to[0], to[1], to[2]};
    if (!finite3(at)) {
        at[0] = from[0], at[1] = from[1], at[2] = from[2];
        if (!finite3(at)) at[0] = static_cast<float>(player[0]), at[1] = static_cast<float>(player[1]), at[2] = static_cast<float>(player[2]);
    }
    for (int i = 0; i < 3; ++i) from[i] = to[i] = at[i];
    // A camera inside the world but over 1 km away is a building flight far from the character (FlyAnywhere leaves
    // the character behind): its rays are cut too (nothing to see that far), but that is no broken camera.
    const double cam[3] = {before[0], before[1], before[2]};
    const double end[3] = {before[3], before[4], before[5]};
    if (in_world(cam) && in_world(end)) {
        g_rays_long.fetch_add(1, std::memory_order_relaxed);
        if (!g_ray_long_told.exchange(true))
            LOGI("client guard: a health bar's line of sight from the camera (%.0f, %.0f, %.0f) is %.0f m long (a flight far "
                 "from the character at (%.0f, %.0f, %.0f)?); rays over %.0f m are made zero-length",
                 cam[0], cam[1], cam[2], std::sqrt(static_cast<double>(len2)), player[0], player[1], player[2], kMaxRay);
        return;
    }
    const uint64_t n = g_rays_guarded.fetch_add(1, std::memory_order_relaxed) + 1;
    if (!g_ray_told.exchange(true)) {
        LOGW("client guard: the health bars' line of sight ran from the camera (%.2f, %.2f, %.2f) to (%.2f, %.2f, %.2f), "
             "%.0f m, outside the world or not finite; made it zero-length so the game keeps running. The local player is "
             "at (%.2f, %.2f, %.2f): the game's camera is broken (save and leave the world; docs/re/client.md). Further "
             "rays are counted.",
             before[0], before[1], before[2], before[3], before[4], before[5], std::sqrt(static_cast<double>(len2)),
             player[0], player[1], player[2]);
        g_rays_reported.store(n);
        g_rays_report_ms.store(GetTickCount64());
        return;
    }
    const uint64_t now = GetTickCount64();
    uint64_t last = g_rays_report_ms.load(std::memory_order_relaxed);
    if (now - last >= kGuardStatsMs && g_rays_report_ms.compare_exchange_strong(last, now)) {
        const uint64_t was = g_rays_reported.exchange(n);
        LOGW("client guard: %llu more health-bar ray(s) made zero-length (%llu this session); the camera is at (%.2f, %.2f, %.2f)",
             static_cast<unsigned long long>(n - was), static_cast<unsigned long long>(n), before[0], before[1], before[2]);
    }
}

void hk_ray(safetyhook::Context& c) { mid_guarded<ray_body>(c); }

// Frame times between two camera writes (the client's frame), newest last; one thread (the client's main loop).
constexpr int kFrames = 64;
double g_frame_ms[kFrames];
uint64_t g_frames = 0;
LARGE_INTEGER g_qpf{}, g_last_frame{};
double g_session_max_ms = 0;
uint64_t g_session_max_at = 0;  // GetTickCount64
int g_far_frames = 0;
bool g_cam_armed = false;       // the camera was near the player once this session (a loading camera is not compared)
uint32_t g_cam_gen = 0;

void frame_tick() {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (g_last_frame.QuadPart && g_qpf.QuadPart) {
        const double ms = static_cast<double>(now.QuadPart - g_last_frame.QuadPart) * 1000.0 / static_cast<double>(g_qpf.QuadPart);
        g_frame_ms[g_frames++ % kFrames] = ms;
        if (ms > g_session_max_ms) {
            g_session_max_ms = ms;
            g_session_max_at = GetTickCount64();
        }
    }
    g_last_frame = now;
}

double fixed_m(int64_t v) { return static_cast<double>(v) * kFixed; }

// After the client wrote ClientPlayerInput.camera: r14 = the local player's ClientPlayerInput, rbp = the client game
// (its camera object at [rbp + cam.obj]). Read only.
void camera_body(safetyhook::Context& c) {
    if (const uint32_t gen = g_client_gen.load(std::memory_order_relaxed); gen != g_cam_gen) {
        g_cam_gen = gen;
        g_frames = 0;
        g_last_frame.QuadPart = 0;
        g_session_max_ms = 0;
        g_session_max_at = 0;
        g_far_frames = 0;
        g_cam_armed = false;
    }
    frame_tick();
    if (g_cam_told.load(std::memory_order_relaxed) && g_cam_far_told.load(std::memory_order_relaxed)) return;
    const auto* cam = *reinterpret_cast<const uint8_t* const*>(c.rbp + L.cam_obj);
    const auto* cpi = reinterpret_cast<const uint8_t*>(c.r14);
    if (!cam || !cpi) return;  // no camera object: the game wrote nothing this frame
    const auto* camera = cpi + L.cpi_camera;  // keen::ecs::ClientCamera: WorldPosition, Quaternion, distance, fovY, aspect, near, far
    const int64_t* raw = reinterpret_cast<const int64_t*>(camera);
    const double pos[3] = {fixed_m(raw[0]), fixed_m(raw[1]), fixed_m(raw[2])};
    const float* f = reinterpret_cast<const float*>(camera + 24);  // orientation x y z w, distance, fovY, aspect, near, far
    bool floats_ok = true;
    for (int i = 0; i < 9; ++i) floats_ok = floats_ok && std::isfinite(f[i]);
    double player[3] = {};
    int src = 0;
    uint64_t age = 0;
    const bool have_player = last_player(player, &src, &age) && age <= kPlayerFreshMs;
    const double away = have_player ? dist3(pos, player) : 0.0;  // ("far" is a windows.h macro)
    const char* why = nullptr;
    char because[96];
    if (have_player && away < 200.0) g_cam_armed = true;
    if (!in_world(pos) || !floats_ok) {
        if (g_cam_told.exchange(true)) return;
        why = !in_world(pos) ? "outside the world" : "its orientation or lens is not finite";
    } else if (g_cam_armed && have_player && away > kFarFromPlayer) {
        if (++g_far_frames < kFarFrames) return;  // far for a moment (a teleport): only a camera that stays there
        if (g_cam_far_told.exchange(true)) return;
        std::snprintf(because, sizeof because, "%.0f m from the player for %d frames", away, g_far_frames);
        why = because;
    } else {
        g_far_frames = 0;
        return;
    }
    // recent frame times, newest first
    char frames[200];
    int k = 0;
    double max64 = 0;
    const uint64_t have = std::min<uint64_t>(g_frames, kFrames);
    for (uint64_t i = 0; i < have; ++i) max64 = std::max(max64, g_frame_ms[(g_frames - 1 - i) % kFrames]);
    for (uint64_t i = 0; i < std::min<uint64_t>(have, 10) && k < static_cast<int>(sizeof frames) - 12; ++i)
        k += std::snprintf(frames + k, sizeof frames - k, "%s%.1f", i ? " " : "", g_frame_ms[(g_frames - 1 - i) % kFrames]);
    if (k == 0) std::snprintf(frames, sizeof frames, "none yet");
    // outside the world or not finite: the broken camera (W); far from the player inside the world: a building flight
    // explains that too (I), with the same details
    const int level = why == because ? KILN_LOG_INFO : KILN_LOG_WARN;
    logf(level, "client camera check: ClientPlayerInput's camera is at (%.3f, %.3f, %.3f) m [y raw 0x%016llx], %s; player %s "
         "(%.2f, %.2f, %.2f) from %s, %llu ms ago; orientation (%g, %g, %g, %g), distance %g, fovY %g, aspect %g, near %g, "
         "far %g; frame ms newest first: %s; max %.1f over the last %llu frames, session max %.1f (%llu s ago)",
         pos[0], pos[1], pos[2], static_cast<unsigned long long>(raw[1]), why, have_player ? "at" : "last seen at",
         player[0], player[1], player[2], kPlayerSrc[src < 0 || src > 2 ? 0 : src], static_cast<unsigned long long>(age),
         f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], frames, max64, static_cast<unsigned long long>(have),
         g_session_max_ms, static_cast<unsigned long long>(g_session_max_at ? (GetTickCount64() - g_session_max_at) / 1000 : 0));
    // the camera object's fields (offsets of client 26430625; read only)
    const int64_t* target = reinterpret_cast<const int64_t*>(cam + 0x50);
    const float* tf = reinterpret_cast<const float*>(cam + 0x68);
    const int64_t* pivot = reinterpret_cast<const int64_t*>(cam + L.cam_pivot);
    logf(level, "client camera check: camera object %p: +0x50 target (%.3f, %.3f, %.3f) m [raw 0x%016llx 0x%016llx 0x%016llx], "
         "+0x68 %g, +0x6c %g; +0x%llx pivot (%.3f, %.3f, %.3f) m [raw 0x%016llx 0x%016llx 0x%016llx]; +0x217c %g",
         static_cast<const void*>(cam), fixed_m(target[0]), fixed_m(target[1]), fixed_m(target[2]),
         static_cast<unsigned long long>(target[0]), static_cast<unsigned long long>(target[1]),
         static_cast<unsigned long long>(target[2]), tf[0], tf[1], static_cast<unsigned long long>(L.cam_pivot),
         fixed_m(pivot[0]), fixed_m(pivot[1]), fixed_m(pivot[2]), static_cast<unsigned long long>(pivot[0]),
         static_cast<unsigned long long>(pivot[1]), static_cast<unsigned long long>(pivot[2]),
         *reinterpret_cast<const float*>(cam + 0x217c));
    // +pivot+0x18 .. +pivot+0x74 as floats: offset (+0x148c), +0x1498.., distance (+0x14a4), quaternions (+0x14b0/c0/d0)
    char fl[400];
    k = 0;
    for (int i = 0; i < 24 && k < static_cast<int>(sizeof fl) - 16; ++i)
        k += std::snprintf(fl + k, sizeof fl - k, "%s%g", i ? " " : "",
                           *reinterpret_cast<const float*>(cam + L.cam_pivot + 0x18 + 4 * i));
    logf(level, "client camera check: camera object +0x%llx..+0x%llx (floats): %s",
         static_cast<unsigned long long>(L.cam_pivot + 0x18), static_cast<unsigned long long>(L.cam_pivot + 0x74), fl);
}

void hk_cam(safetyhook::Context& c) { mid_guarded<camera_body>(c); }

// A host: the server world's position of the local player, from the server tick (kiln.world reads there only).
void refresh_player_from_server() {
    if (!g_client || !g_client_guards) return;
    if (!g_world_api) {
        const auto* w = static_cast<const KilnWorldApi*>(
            g_api->service(KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, KILN_WORLD_VERSION));
        if (!w || w->header.size < offsetof(KilnWorldApi, position) + sizeof(void*)) return;
        g_world_api = w;
    }
    const uint32_t local = local_player();
    if (!local) return;
    KilnVec3 p{};
    if (g_world_api->position(local, &p) != KILN_OK) return;
    const double v[3] = {p.x, p.y, p.z};
    note_player(v, 2);
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

void KILN_CALL on_pre(const KilnTickInfo* t, void*) {
    resolve_build_programs();
    if (g_client_guards && t && t->step % 30 == 0) refresh_player_from_server();
}

void clear_all() {
    for (auto& s : g_players) store(s, nullptr);
    std::lock_guard lock(g_extra_mu);
    for (int i = 0; i < kExtra; ++i) {
        g_extra_ids[i].store(0, std::memory_order_release);
        store(g_extra[i], nullptr);
    }
}

void KILN_CALL on_world(const KilnWorldEvent* ev, void*) {
    if (ev->kind == KILN_WORLD_CLIENT && ev->event == KILN_WORLD_STARTED) {
        if (g_client_guards && (g_rays_guarded.load() || g_rays_long.load())) {
            LOGI("client guard: last session: %llu health-bar ray(s) from a camera outside the world and %llu over %.0f m "
                 "made zero-length", static_cast<unsigned long long>(g_rays_guarded.load()),
                 static_cast<unsigned long long>(g_rays_long.load()), kMaxRay);
        }
        client_session_reset();
        return;
    }
    if (ev->kind != KILN_WORLD_SERVER || ev->event != KILN_WORLD_STOPPING) return;
    clear_all();
    LOGI("server world closing: every player's rules cleared");
}

// ============================================================================================= table ===========

KilnResult KILN_CALL t_set(KilnEntity entity, const KilnRulesPlayer* in) {
    if (entity == 0) return KILN_ERR_INVALID_ARGUMENT;
    KilnRulesPlayer v;
    if (!read_in(in, &v)) return KILN_ERR_INVALID_ARGUMENT;
    const KilnRulesPlayer* r = in ? &v : nullptr;
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
    if (!out || entity == 0 || out->size < KILN_RULES_PLAYER_V1_SIZE) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t n = std::min<uint32_t>(out->size, sizeof(KilnRulesPlayer));
    KilnRulesPlayer v;
    if (const Slot* s = slot_of(entity)) load(*s, &v);
    else v = kDefault;
    v.size = n;
    std::memcpy(out, &v, n);
    return KILN_OK;
}

KilnResult KILN_CALL t_set_local(const KilnRulesPlayer* in) {
    KilnRulesPlayer v;
    if (!read_in(in, &v)) return KILN_ERR_INVALID_ARGUMENT;
    store(g_local, in ? &v : nullptr);
    return KILN_OK;
}

void fill_status(KilnRulesStatus* out);

KilnResult KILN_CALL t_status(KilnRulesStatus* out) {
    constexpr uint32_t kV1 = offsetof(KilnRulesStatus, zone_forced);
    if (!out || out->size < kV1) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t n = std::min<uint32_t>(out->size, sizeof(KilnRulesStatus));
    KilnRulesStatus full;
    fill_status(&full);
    full.size = n;
    std::memcpy(out, &full, n);
    return KILN_OK;
}

void fill_status(KilnRulesStatus* out) {
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
    out->zone_forced = S.zone.load();
    out->shroud_held = S.shroud.load();
    out->glide_changed = S.glide.load();
    std::lock_guard lock(g_detail_mu);
    std::snprintf(out->detail, sizeof out->detail, "%s", g_detail.c_str());
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

// The game client's guards (client.d/rules.json): each needs its pattern pinned to its function and its layout
// constants as found on 26430625, so a game update turns it off instead of hooking the wrong place.
void install_client_guards() {
    if (feat("rules.healthbar_guard") && sym("rules/client.HealthbarRay", &fn.ray_site) &&
        cst("rules/client.hb.player_z", &L.hb_player_z)) {
        h_ray = safetyhook::create_mid(reinterpret_cast<void*>(fn.ray_site), &hk_ray);
        if (h_ray) g_client_guards |= kGuardRays;
        else LOGE("client guard: hooking the health bars' line of sight failed");
    }
    if (feat("rules.camera_check") && sym("rules/client.CameraInput", &fn.cam_site) && cst("rules/client.cam.obj", &L.cam_obj) &&
        cst("rules/client.cpi.camera", &L.cpi_camera) && cst("rules/client.cam.pivot", &L.cam_pivot)) {
        h_cam = safetyhook::create_mid(reinterpret_cast<void*>(fn.cam_site), &hk_cam);
        if (h_cam) g_client_guards |= kGuardCamera;
        else LOGE("client guard: hooking the camera check failed");
    }
    LOGI("client guards: health-bar line of sight %s (rays over %.0f m made zero-length), camera check %s (read only)",
         (g_client_guards & kGuardRays) ? "on" : "OFF (rules.healthbar_guard)", kMaxRay,
         (g_client_guards & kGuardCamera) ? "on" : "OFF (rules.camera_check)");
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
    // building zone (server cursor)
    if (common && feat("rules.zone") && sym("rules/ZoneFlag", &fn.zone_site)) {
        h_zone = safetyhook::create_mid(reinterpret_cast<void*>(fn.zone_site), &hk_zone);
        if (h_zone) g_hooks |= KILN_RULES_HOOK_ZONE;
        else LOGE("hooking the building-zone test failed");
    } else {
        note("building zone (rules.zone)");
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
    // Shroud timer
    if (common && feat("rules.shroud") && sym("rules/ShroudDrain", &fn.shroud_site) && cst("rules/fog.attr", &L.fog_attr) &&
        cst("rules/fog.actor", &L.fog_actor) && cst("rules/fog.state", &L.fog_state)) {
        h_shroud = safetyhook::create_mid(reinterpret_cast<void*>(fn.shroud_site), &hk_shroud);
        if (h_shroud) g_hooks |= KILN_RULES_HOOK_SHROUD;
        else LOGE("hooking the Shroud timer failed");
    } else {
        note("Shroud timer (rules.shroud)");
    }
    // gliding (the step, its gravity, its stamina): all three or none, so the server and the client agree
    g_client = static_cast<const KilnClientApi*>(
        g_api->service(KilnStr{KILN_CLIENT_SERVICE, sizeof(KILN_CLIENT_SERVICE) - 1}, KILN_CLIENT_VERSION));
    if (common && feat("rules.glide") && sym("rules/GlideStep", &fn.glide) && sym("rules/QuatRotate", &fn.quat_rotate) &&
        sym("rules/StepDelta", &fn.step_delta) && sym("rules/GlideGravity", &fn.grav_site) &&
        sym("rules/GlideStamina", &fn.stam_site) && sym("rules/ActorStateTest", &fn.actor_state) &&
        cst("rules/glide.actor_input", &L.glide_input) && cst("rules/glide.xform", &L.glide_xform) &&
        cst("rules/glide.velocity", &L.glide_velocity) && cst("rules/loco.player_input", &L.glide_player_input) &&
        cst("rules/xform.rotation", &L.xform_rotation) && cst("rules/input.trigger", &L.input_trigger) &&
        cst("rules/input.mask", &L.input_mask) && cst("rules/grav.actor", &L.grav_actor)) {
        // the camera's orientation for the plane's pitch: optional (without it the glider's own pitch is used)
        if (!(feat("rules.glide_view") && cst("rules/input.camera_rot", &L.input_camera_rot))) {
            L.input_camera_rot = -1;
            LOGW("gliding: the camera's orientation is not found on this build: a boosted glider takes its pitch from the glider");
        }
        h_grav = safetyhook::create_mid(reinterpret_cast<void*>(fn.grav_site), &hk_grav);
        h_stam = safetyhook::create_mid(reinterpret_cast<void*>(fn.stam_site), &hk_stam);
        const bool step = inline_hook(h_glide, reinterpret_cast<void*>(fn.glide), reinterpret_cast<void*>(&hk_glide), "the glide step");
        if (h_grav && h_stam && step) {
            g_hooks |= KILN_RULES_HOOK_GLIDE;
        } else {
            LOGE("hooking the glider failed (gravity %d, stamina %d, step %d): gliding stays the game's", bool(h_grav), bool(h_stam), step);
            h_grav = {};
            h_stam = {};
            h_glide = {};
            note("gliding (hook failed)");
        }
    } else {
        note("gliding (rules.glide)");
    }
    {
        std::lock_guard lock(g_detail_mu);
        g_detail = off.empty() ? "every hook installed" : "off on this build: " + off;
    }
    if (g_api->side() == KILN_SIDE_CLIENT) install_client_guards();
    auto on = [](uint32_t bit) { return (g_hooks & bit) ? "on" : "OFF"; };
    LOGI("hooks: flight %s, building zone %s, placement %s, costs %s, mining %s, Shroud timer %s, gliding %s",
         on(KILN_RULES_HOOK_FLY), on(KILN_RULES_HOOK_ZONE), on(KILN_RULES_HOOK_CURSOR), on(KILN_RULES_HOOK_COSTS),
         on(KILN_RULES_HOOK_DIG), on(KILN_RULES_HOOK_SHROUD), on(KILN_RULES_HOOK_GLIDE));
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
