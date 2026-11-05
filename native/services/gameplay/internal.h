// SPDX-License-Identifier: MIT
// kiln.gameplay internals. Engine facts come from sigdb/symbols/server.d/gameplay.json; docs/re/gameplay.md has the
// evidence for each.
#pragma once
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_gameplay.h"

namespace kg {

extern const KilnApi* g_api;
extern KilnOwner g_owner;

void logf(int level, const char* fmt, ...);
#define KG_E(...) ::kg::logf(KILN_LOG_ERROR, __VA_ARGS__)
#define KG_W(...) ::kg::logf(KILN_LOG_WARN, __VA_ARGS__)
#define KG_I(...) ::kg::logf(KILN_LOG_INFO, __VA_ARGS__)
#define KG_D(...) ::kg::logf(KILN_LOG_DEBUG, __VA_ARGS__)

inline KilnStr kstr(std::string_view s) { return KilnStr{s.data(), s.size()}; }
inline std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

uint32_t fnv1a(std::string_view s, uint32_t h = 0x811c9dc5u);
inline uint32_t ecs_hash(std::string_view name) { return fnv1a(name, fnv1a("keen::ecs::")); }

inline double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }
int64_t m_to_fixed(double m);

// ---- engine layout (resolved at boot) ----
struct Layout {
    int64_t world_storage = -1;
    int64_t pending = -1, pending_count = -1, buffer_used = -1, buffer_stride = 24, record_size = -1;
    int64_t queues = -1, ev_cap = -1, ev_count = -1, ev_bits = -1, ev_keys = -1, ev_values = -1;
    int64_t op_player = -1;
    int64_t arg_offset = -1, arg_base = -1, arg_stride = 24;
    int64_t tp_applied = 72, tp_find_ground = 73, tp_target_entity = 56;
    // discard actions (PlayerInput / ServerConsumedPlayerInput, captured in inventory_actions)
    int64_t pi_item_version = -1, pi_item_type = -1, pi_item_slot = -1, scpi_item = -1;
    int64_t pi_tr_version = -1, pi_tr_type = -1, pi_tr_slot = -1, scpi_tr = -1;
};
extern Layout L;

struct Fns {
    using GetComponentFn = void (*)(void* out, uintptr_t world, uint32_t hash, uint32_t entity);
    using GetTransformFn = const int64_t* (*)(uintptr_t world, uint32_t entity);
    using EmitFn = void* (*)(void* buffer, uint32_t hash, uint32_t size, uint16_t program);
    using JobProgramIdFn = uint16_t (*)(void* job);
    using JobBufferFn = void* (*)(void* job);
    GetComponentFn get_component = nullptr;
    GetTransformFn get_transform = nullptr;
    EmitFn emit = nullptr;
    JobProgramIdFn job_program = nullptr;
    JobBufferFn job_buffer = nullptr;
    uintptr_t craft = 0, place_block = 0, place_prop = 0, dismantle = 0, dismantle2 = 0, pick_up = 0, terraform = 0,
              queue_spawn = 0, spawner_ret = 0;
};
extern Fns fn;

struct Features {
    bool intercept = false, queues = false, emit = false, craft = false, build = false, terraform = false,
         spawn = false, components = false, transforms = false, discard = false;
};
extern Features F;

// Event hashes (FNV-1a-32 of "keen::ecs::<name>") and the sizes the game emits them with (checked per record).
struct EventIds {
    uint32_t craft_request, accept_offer, loot, pickup, accepted, declined, teleport, teleport_count;
};
extern EventIds H;
constexpr uint32_t kCraftSize = 0x38, kAcceptSize = 0x18, kLootSize = 0x80, kPickupSize = 0x30, kAcceptedSize = 0x18,
                   kDeclinedSize = 0x0C;

// ---- world ----
uintptr_t world();               // 0 before the first step
inline uintptr_t storage_of(uintptr_t w) { return w + static_cast<uintptr_t>(L.world_storage); }
extern uint64_t g_step;          // Pre passes with a world
extern std::atomic<bool> g_in_step;
// true while the game's sim step runs: from the last Pre subscriber (a late subscription of kiln.gameplay, after the
// managed host's) to kiln.gameplay's Post, which runs before the host's. Entity creations outside it are Kiln's.
extern std::atomic<bool> g_in_sim;

// ---- dispatch (gameplay.cpp) ----
bool has_subscribers(uint32_t kind);
// Runs subscribers; returns the final canceled state (flags & CANCELED). ev->flags is updated.
bool dispatch(KilnGameplayEvent* ev);
uint32_t subscriber_count();

struct Stats {
    uint64_t steps = 0, events_seen = 0, dispatched = 0, canceled = 0;
    std::atomic<uint64_t> denied{0};
    uint64_t hook_records = 0;
    std::atomic<uint64_t> spawns_replaced{0};  // natural spawns whose template a spawn_replace rule swapped
};
extern Stats S;

// ---- positions / components (tick thread; transform also from workers, guarded) ----
bool entity_position(KilnEntity e, KilnVec3* out);
void* component(KilnEntity e, uint32_t hash, uint32_t* size);  // tick thread

// ---- zones (zones.cpp) ----
// Zone sets are immutable snapshots; workers read the current one lock-free. Changes and the per-step refresh of the
// players allowed in each zone happen on the tick thread.
struct Zone {
    uint32_t id = 0;
    KilnOwner owner = 0;
    uint32_t deny = 0, shape = 0, flags = 0;
    KilnVec3 center{}, min{}, max{};
    double radius = 0;
    std::vector<uint64_t> allowed;
    std::string name;
    uint64_t allowed_mask = 0;  // player entities 1..64 allowed now (bit e-1)
};
uint32_t zone_check(KilnEntity player, double x, double y, double z, uint32_t action);  // any thread; 0 = allowed
size_t zone_count();
bool zones_any(uint32_t action_mask);
void zones_refresh();     // Pre: resolve allowed players
void zones_retire();      // Post: free snapshots no worker can read any more
KilnResult zone_add(KilnOwner owner, const KilnGameplayZone* z, uint32_t* out);
KilnResult zone_update(uint32_t id, const KilnGameplayZone* z);
KilnResult zone_remove(uint32_t id);
bool zone_denies(KilnEntity player, KilnVec3 pos, uint32_t action, uint32_t* out_zone);
void zones_set_players_service(const void* players_api);

// ---- pending events (intercept.cpp) ----
void intercept_pre();     // walk the pending buffers
void teleport_pre();
void discard_pre();
void discard_status(uint64_t* seen, uint64_t* canceled);
void teleport_post();
KilnResult watch(KilnEntity e, bool on);
bool intercept_wanted();
void declined_check(const char* when);  // diagnostics: were Kiln's declines delivered as DeclinedInteraction?
void declined_reset();

// ---- hooks (hooks.cpp) ----
bool hooks_install();
void hooks_post();        // report what the hooks recorded this step
void hooks_set_spawn_service(const void* spawn_api);
KilnResult hooks_spawn_replace(KilnOwner owner, const uint8_t* from, const uint8_t* to, float chance);
uint32_t hooks_spawn_replace_clear(KilnOwner owner);

// ---- every entity created / destroyed (entities.cpp) ----
bool entities_install();
bool entities_ready();
void entities_set_spawn_service(const void* spawn_api);
void entities_post();
KilnResult entity_filter(uint32_t categories);
void entities_status(uint64_t* joined, uint64_t* left, uint64_t* filtered, uint32_t* categories);

// ---- Kiln's own program (program.cpp) ----
void program_register(const void* world_api);
KilnResult inject(const KilnGameplayInject* r);
bool program_active();
uint16_t program_pid();  // engine id of kiln_gameplay (0 before its first emit)

// ---- recipes (recipes.cpp) ----
void recipes_init();
KilnResult recipe(uint32_t id, KilnGameplayRecipe* out);
KilnResult workshop_recipes(uint32_t workshop, uint32_t* out, uint32_t capacity, uint32_t* out_count);

// ---- SEH guards (seh.cpp; no C++ objects with destructors in these frames) ----
using WalkFn = void (*)(void* ctx);
bool seh_call(WalkFn f, void* ctx, unsigned long* code);
bool seh_subscriber(KilnGameplayFn f, KilnGameplayEvent* ev, void* user, uint32_t* verdict, unsigned long* code);
bool seh_get_component(uintptr_t world, uint32_t hash, uint32_t entity, void** ptr, uint32_t* size);
bool seh_get_transform(uintptr_t world, uint32_t entity, const int64_t** out);

}  // namespace kg
