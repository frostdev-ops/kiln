// SPDX-License-Identifier: MIT
// kiln.combat internals: engine layouts and guarded engine calls (engine.cpp), shared with combat.cpp.
// Layouts are for svn 1285601 and resolved through sigdb/symbols/server.d/combat.json; evidence in docs/re/combat.md.
#pragma once
#include <cstdint>
#include <cstring>

#include "kiln_api.h"

namespace kc {

constexpr uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}
constexpr uint32_t kHealthHash = fnv1a("keen::ecs::Health");                      // 0xDC101091
constexpr uint32_t kActorHash = fnv1a("keen::ecs::Actor");                        // 0x009E8B34
constexpr uint32_t kFactionHash = fnv1a("keen::ecs::Faction");
constexpr uint32_t kCurrentTransformHash = fnv1a("keen::ecs::CurrentTransform");  // 0x027AC564
constexpr uint32_t kPlayerStateHash = fnv1a("keen::ecs::PlayerState");
static_assert(kHealthHash == 0xDC101091u && kCurrentTransformHash == 0x027AC564u && kActorHash == 0x009E8B34u);

// Attribute ids (HashKey32 from the attribute resources; immediates in the game code, docs/re/combat.md).
constexpr uint32_t kAttrHealth = 0x8EB84995u;
constexpr uint32_t kAttrHealthMax = 0xDC8358BEu;

// HitCandidate (keen::ecs::HitCandidate, 0x150 B on svn 1285601; the first 0xF8 B are the HitEvent). Offsets that the
// code depends on are captured (kind, flags, healthChange, size); the rest are from EMF's layout, re-read in
// applyHitToEntity / the conclusion (docs/re/combat.md).
struct HitOffsets {
    int64_t size = 0x150, kind = 0x144, flags = 0x98, health_change = 0xBC;
    static constexpr int64_t target_transform = 0x08, position = 0x6C, normal = 0x78, direction = 0x84,
                             root_source = 0x90, target = 0x94, root_target = 0xA0, source = 0xA4,
                             permission_source = 0xA8, stamina_change = 0xC0, aggro = 0xC8, impact_id = 0xD4,
                             weapon_category = 0xD8, damage_types = 0xDC, attack_category = 0xDF, reaction = 0xE0,
                             push_back = 0xE2, target_faction = 0xF0, source_transform = 0x108, mana_cost = 0x140,
                             send_hit_event = 0x14C, ignore_consecutive = 0x14D;
};
extern HitOffsets H;
constexpr size_t kMaxHit = 0x200;

constexpr uint32_t kFlagBlocked = 0x1, kFlagParried = 0x2, kFlagKillingBlow = 0x2000, kFlagHealing = 0x4000,
                   kFlagForceInterrupt = 0x800000;

#pragma pack(push, 8)
struct WorldTransform {  // keen::ecs::CurrentTransform, 56 bytes
    int64_t pos[3];      // 32.32 fixed point metres
    float rot[4];
    float scale[3];
    uint32_t pad;
};
#pragma pack(pop)
static_assert(sizeof(WorldTransform) == 56);

struct Slice {
    void* ptr;
    uint64_t size;
};
struct AttrFindResult {
    uint8_t err;
    uint8_t pad[3];
    uint32_t zero;
    const uint8_t* info;  // +0 u32 rootId, +0xC u16 slot
};

using AttrFindFn = void*(__fastcall*)(AttrFindResult* out, uint32_t id, const void* registry);
using AttrAddFn = void(__fastcall*)(void* comp, const void* registry, int32_t delta, uint32_t id);
using GetComponentFn = void(__fastcall*)(Slice* out, uintptr_t world, uint32_t hash, uint32_t entity);
using GetEntityTransformFn = const WorldTransform*(__fastcall*)(uintptr_t world, uint32_t entity);
using AllocPendingHitFn = uint8_t*(__fastcall*)(void* impact_system, uint8_t kind);
using ApplyHitFn = void(__fastcall*)(uint8_t* out_propagate, void* handle, void* ctx, uint8_t* hit);
using ConclusionFn = void(__fastcall*)(void* program_ctx);
using AlreadyHitFn = bool(__fastcall*)(void* impact_system, uint32_t impact_id, uint32_t target);

struct Engine {
    AttrFindFn attr_find = nullptr;
    AttrAddFn attr_add = nullptr;
    GetComponentFn get_component = nullptr;
    GetEntityTransformFn get_transform = nullptr;
    AllocPendingHitFn alloc_hit = nullptr;
    AlreadyHitFn already_hit = nullptr;  // impact `id` already hit `target` (the conclusion's repeat-hit check)
    uintptr_t apply_hit = 0, conclusion = 0;
    int64_t hits_pool = -1, hits_count = -1;  // ImpactSystem: primary pending array {data +pool, count +count}
    int64_t actor_state = -1;                 // Actor u64 state bits; bit 7 = dead
    // The game's state test (combat/ActorStateTest): with Actor[actor_flags] bit 0 set, a state bit counts from
    // (state | on) & ~off (an override layer), otherwise from state alone. -1 = unresolved: the raw bits are used.
    int64_t actor_flags = -1, actor_on = -1, actor_off = -1;
    int64_t evq_first = -1, evq_blocks = -1, evq_count = -1, evq_next = -1, evq_shift = -1;  // event-in queue
    // fall_damage_infliction mid points (docs/re/combat.md "Fall damage"): the per-entity pct test and the flip store
    uintptr_t fall_check = 0, fall_store = 0;
    int64_t fall_frame = 0;   // rbp-relative slot of the DynamicFallDamage pointer (signed)
    int64_t fall_pct = -1;    // DynamicFallDamage.detectedFallDamagePercentage (+0xC); distance is pct - 4
    // health_recharge: the add of the whole regenerated points; changeAttributeValue: its Health/Stamina/Mana record
    uintptr_t regen_add = 0, attr_record = 0;
    using CurrentEntityFn = uint32_t*(__fastcall*)(void* program_ctx, uint32_t* out);
    CurrentEntityFn current_entity = nullptr;
};
extern Engine E;

// ---- guarded engine access (engine.cpp). World/registry pointers come from the caller. ----
// The entity's per-entity component (NULL when absent or on a fault).
void* component(uintptr_t world, uint32_t hash, uint32_t entity, uint64_t* size = nullptr);
// Health attribute slot pointers of a Health component (false if the component is not initialised or the ids do not
// belong to it).
bool health_slots(const void* registry, uint8_t* comp, int32_t** cur, int32_t** max);
bool read_health(uintptr_t world, const void* registry, uint32_t entity, int32_t* cur, int32_t* max);
// current += delta through the game's attrAdd (formulas re-run). false on a fault.
bool add_health(const void* registry, uint8_t* comp, int32_t delta);
bool transform(uintptr_t world, uint32_t entity, WorldTransform* out);

// ---- any attribute component (kiln.combat v2 attributes; docs/re/combat.md "Attributes") ----
// attrFind of `id`: the root id of the component group it belongs to and its slot index. false: not an id of the
// registry (or a fault).
bool attr_lookup(const void* registry, uint32_t id, uint32_t* root, uint16_t* slot);
// The int32 slots of an attribute component {u32 rootId, u16 storageOffset, u8 flags, u8 count, ...}: false unless it is
// set up (flags bit 0), its rootId is `root` and its storage lies inside the component's `size` bytes.
bool attr_values(const uint8_t* comp, uint64_t size, uint32_t root, int32_t** values, uint8_t* count);
bool read_i32(const int32_t* p, int32_t* out);
// current += delta of attribute `id` through the game's attrAdd (formulas re-run, so a clamp in them applies).
bool attr_add_id(const void* registry, uint8_t* comp, int32_t delta, uint32_t id);

// An Actor's state words, read like the game's state test reads them.
struct ActorStates {
    uint64_t state = 0, on = 0, off = 0;
    bool overrides = false;  // the override layer applies (Actor[actor_flags] bit 0)
};
constexpr unsigned kStateDead = 7, kStateDeathSequence = 12;  // the bits the death program tests
bool actor_states(uintptr_t world, uint32_t entity, ActorStates* out, bool* has_actor);
// The game's test of `bit` (0x3b7a0 on the server, svn 1287369), over `state` in place of the actor's own.
inline bool state_has(const ActorStates& s, uint64_t state, unsigned bit) {
    const uint64_t m = 1ull << bit;
    const uint64_t v = s.overrides ? (state | s.on) & ~s.off : state;
    return (v & m) == m;
}
// The dead mark right now (state 7 through the game's test): what the death program and the game's consumers see.
bool actor_dead(uintptr_t world, uint32_t entity, bool* has_actor);
// The dead mark as the game's death program will leave it at its next run, given the entity's current health: it
// clears the mark once Health >= 1 and sets it while Health < 1 (unless the actor already counts as dead or is in its
// death sequence, state 12). Kiln's own health writes (heal, set_health, god mode) therefore read back consistently
// in the same step.
bool actor_dead_after_check(uintptr_t world, uint32_t entity, int32_t health, bool* has_actor);
uint8_t* alloc_hit(void* impact_system);
// The conclusion's repeat-hit test for hits with ignoreConsecutive set (false when unknown).
bool already_hit(void* impact_system, uint32_t impact_id, uint32_t target);
bool read_u8(const void* p, uint8_t* out);
bool copy_bytes(void* dst, const void* src, size_t n);
bool move_bytes(void* dst, const void* src, size_t n);
bool read_u32(const void* p, uint32_t* out);
// ImpactSystem's primary pending-hit array: data pointer and count (records of H.size bytes).
bool read_list(const void* impact_system, uint8_t** data, uint64_t* count);
bool write_count(void* impact_system, uint64_t count);
// EntityDiedEvent queue walk: writes up to cap dead entity ids (record stride 0x10, entity at +8). false on a fault.
bool walk_died_queue(const uint8_t* queue, uint32_t* out, uint32_t cap, uint32_t* count);

inline double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }

}  // namespace kc
