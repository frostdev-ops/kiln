// SPDX-License-Identifier: MIT
// kiln.creatures internals. Engine facts come from sigdb/symbols/server.d/creatures.json (and kiln.gameplay's emitter,
// world/CurrentEntityId); docs/re/creatures.md has the evidence. Every raw read or engine call that depends on a layout
// is wrapped in SEH (seh.cpp): a wrong layout on an unknown build turns into "no value", never a crash.
#pragma once
#include <atomic>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_creatures.h"

namespace kcr {

extern const KilnApi* g_api;
extern KilnOwner g_owner;
extern uint64_t g_step;

void logf(int level, const char* fmt, ...);
#define KC_E(...) ::kcr::logf(KILN_LOG_ERROR, __VA_ARGS__)
#define KC_W(...) ::kcr::logf(KILN_LOG_WARN, __VA_ARGS__)
#define KC_I(...) ::kcr::logf(KILN_LOG_INFO, __VA_ARGS__)
#define KC_D(...) ::kcr::logf(KILN_LOG_DEBUG, __VA_ARGS__)

inline KilnStr kstr(std::string_view s) { return KilnStr{s.data(), s.size()}; }
inline std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

constexpr uint32_t fnv1a(std::string_view s, uint32_t h = 0x811c9dc5u) {
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}
constexpr uint32_t ecs_hash(std::string_view name) {
    return name.find("::") != std::string_view::npos ? fnv1a(name) : fnv1a(name, fnv1a("keen::ecs::"));
}
static_assert(ecs_hash("CurrentTransform") == 0x027ac564u && ecs_hash("Enemy01BehaviorState") == 0x52487b01u);

inline double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }
int64_t m_to_fixed(double m);

bool on_tick();
uintptr_t world();

// ---------------------------------------------------------------------------------------------- layout ----
struct Layout {
    int64_t world_storage = -1;
    // template slots
    int64_t tpl_map = -1, entity_map = -1, map_cap = -1, map_values = -1;
    int64_t rec_archetype = -1, rec_index = -1, rec_chunk = -1, rec_template = 0x28, tpl_columns = -1;
    // enemy01
    int64_t ctx_dyn = -1, ctx_tpl = -1, ctx_program = 0, conditions = 33, states = 30;
    int64_t dyn_enabled = -1, dyn_disabled = -1, tpl_enabled = -1;
    // actor
    int64_t actor_state = -1, ready_bit = -1, dead_bit = 7;
    // arsenals
    int64_t arsenals_data = 0x78;
    // reflection type records
    int64_t type_hash = -1, type_qname = 32, type_inner = 56, type_size = 64, type_count = 72, type_kind = 76,
            type_fields = 88, type_enums = 96, field_stride = 48, field_type = 16, field_offset = 24, enum_stride = 40,
            enum_value = 16;
};
extern Layout L;

struct Slice {
    void* ptr;
    uint64_t size;
};
struct Fns {
    using GetComponentFn = void (*)(Slice* out, uintptr_t world, uint32_t hash, uint32_t entity);
    using GetTemplateComponentFn = void* (*)(uintptr_t world, uint32_t hash, uint32_t entity);
    using GetTransformFn = int64_t* (*)(uintptr_t world, uint32_t entity);
    using ComponentMapFindFn = uint16_t* (*)(void* map, const uint32_t* hash);
    using EntityMapFindFn = uint64_t (*)(void* map, const uint32_t* id);
    using CurrentEntityFn = uint32_t* (*)(void* ctx, uint32_t* out);
    using TypeByHashFn = uintptr_t (*)(uint32_t hash);
    using ArsenalFindFn = void* (*)(void* registry, const uint8_t* guid);
    using EmitFn = void* (*)(void* buffer, uint32_t hash, uint32_t size, uint16_t program);
    using JobProgramIdFn = uint16_t (*)(void* job);
    using JobBufferFn = void* (*)(void* job);
    GetComponentFn get_component = nullptr;
    GetTemplateComponentFn get_template_component = nullptr;
    GetTransformFn get_transform = nullptr;
    ComponentMapFindFn component_map_find = nullptr;
    EntityMapFindFn entity_map_find = nullptr;
    CurrentEntityFn current_entity = nullptr;
    TypeByHashFn type_by_hash = nullptr;
    ArsenalFindFn arsenal_find = nullptr;
    EmitFn emit = nullptr;
    JobProgramIdFn job_program = nullptr;
    JobBufferFn job_buffer = nullptr;
    uintptr_t set_state = 0, handle_condition = 0, behavior_update = 0, transition_table = 0;
};
extern Fns fn;

struct Features {
    bool components = false, slots = false, ai = false, table = false, ready = false, arsenals = false,
         reflection = false, commands = false;
};
extern Features F;

// ---------------------------------------------------------------------------------------------- seh.cpp ----
// All return false on a fault (or a missing function).
bool seh_component(uintptr_t w, uint32_t hash, uint32_t entity, void** out, uint32_t* size);
bool seh_template_component(uintptr_t w, uint32_t hash, uint32_t entity, void** out);
bool seh_transform(uintptr_t w, uint32_t entity, int64_t** out);
// The address of the entity's per-entity template slot of template component `hash` (0 if it has none).
bool seh_template_slot(uintptr_t w, uint32_t hash, uint32_t entity, void*** out);
// The entity record's TemplateInfo* (identity check for despawn detection).
bool seh_record_template(uintptr_t w, uint32_t entity, uintptr_t* out);
bool seh_component_index(void* map, uint32_t hash, uint16_t* out);  // u16 type index from a component map
bool seh_read(const void* p, void* out, size_t n);
bool seh_write(void* p, const void* in, size_t n);
bool seh_current_entity(void* program_ctx, uint32_t* out);
bool seh_type_by_hash(uint32_t hash, uintptr_t* out);
bool seh_arsenal_find(void* registry, const uint8_t* guid, void** out);
bool seh_listener(KilnCreatureListenerFn f, const KilnCreatureEvent* ev, uint32_t n, void* user, uint32_t* out,
                  unsigned long* code);

// Convenience on the tick thread (world() resolved).
void* component(KilnEntity e, uint32_t hash, uint32_t* size = nullptr);
bool entity_exists(KilnEntity e);
bool template_guid(KilnEntity e, uint8_t out[16]);

// ---------------------------------------------------------------------------------------------- refl.cpp ----
enum class VKind : uint8_t { None, Bool, Int, Float, Enum, Flags, Time, Guid };
struct FieldRef {
    uint32_t offset = 0;
    uint8_t bytes = 0;
    VKind kind = VKind::None;
    bool is_signed = false, f64 = false;
    uintptr_t enum_type = 0;  // reflection record of the enum (Enum, Flags)
    std::string type_name;
};
struct TypeInfo {
    std::string qname;
    uint32_t size = 0;
    uintptr_t record = 0;
};
// Qualified type name of a component short name: "keen::ecs::<name>" (as given when it contains "::").
std::string component_qname(std::string_view component);
bool type_info(std::string_view qname, TypeInfo* out, std::string* err);
// Resolves "a.b[2].c" inside a type; err set on failure.
bool resolve_path(const TypeInfo& t, std::string_view path, FieldRef* out, std::string* err);
// Value text <-> bytes at base + f.offset.
bool parse_value(const FieldRef& f, std::string_view text, uint8_t* dst, std::string* err);
std::string format_value(const FieldRef& f, const uint8_t* src);
// JSON array of the flattened scalar fields of a type.
std::string fields_json(const TypeInfo& t);
// A field of any kind by name, base structs included: its offset and size.
bool field_layout(const TypeInfo& t, std::string_view name, uint32_t* offset, uint32_t* size);
// Enum helpers used by the catalog and commands.
bool enum_value(uintptr_t enum_type, std::string_view name, int64_t* out);
std::string enum_name(uintptr_t enum_type, int64_t v);

// ---------------------------------------------------------------------------------------------- catalog.cpp ----
void catalog_init(const void* spawn_api);  // spawn_api = KilnSpawnApi* or null
uint32_t catalog_count();
bool catalog_at(uint32_t i, KilnCreatureTemplate* out);
bool catalog_get(const uint8_t guid[16], KilnCreatureTemplate* out);
bool catalog_find(std::string_view name, KilnCreatureTemplate* out);
void catalog_stats(uint32_t* loaded, uint32_t* engine_extra);
void catalog_check_engine();  // tick: counts engine templates with enemy AI / NPC state not in the catalog
bool parse_guid(std::string_view s, uint8_t out[16]);
std::string guid_text(const uint8_t g[16]);

// ---------------------------------------------------------------------------------------------- layers.cpp ----
KilnResult layer_set(KilnOwner owner, KilnEntity e, uint32_t field, uint32_t index, const KilnCreatureValue* v);
KilnResult layer_get(KilnEntity e, uint32_t field, uint32_t index, KilnCreatureValue* out);
KilnResult layer_reset(KilnOwner owner, KilnEntity e, uint32_t field, uint32_t index, uint32_t* out_count);
KilnResult copy_set(KilnOwner owner, KilnEntity e, std::string_view component, std::string_view path,
                    std::string_view value);
KilnResult copy_get(KilnEntity e, std::string_view component, std::string_view path, std::string* out,
                    uint32_t* is_private);
KilnResult copy_reset(KilnOwner owner, KilnEntity e, std::string_view component, std::string_view path,
                      uint32_t* out_count);
KilnResult copy_refresh(KilnEntity e, uint32_t* out_count);
uint32_t release_owner_now(KilnOwner owner);
void layers_pre();  // tick: despawn checks, deferred frees, deferred owner releases, marks
void layers_info(KilnEntity e, KilnCreatureInfo* io);
void layers_stats(uint32_t* entities, uint32_t* layers, uint32_t* copies, uint32_t* pending_free);
void request_release(KilnOwner owner);  // any thread
// Components the marks program should mark for an entity this step (bit per MarkComp); consumes the request.
enum MarkComp : uint32_t {
    MK_FACTION = 1u << 0, MK_SCALE = 1u << 1, MK_TRANSFORM = 1u << 2, MK_LEVEL = 1u << 3, MK_XP = 1u << 4,
    MK_LOOT = 1u << 5, MK_LOOT_SLOTS = 1u << 6, MK_RESIST = 1u << 7, MK_DAMAGE = 1u << 8, MK_MOVEMENT = 1u << 9,
    MK_AI = 1u << 10,
};
void mark_request(KilnEntity e, uint32_t comps);
// The marks program (worker threads, once per slice): a snapshot of the requests, then the entities it marked.
void mark_snapshot(std::unordered_map<KilnEntity, uint32_t>* out);
void mark_done(const std::vector<KilnEntity>& done);
void marks_expire();
bool marks_pending();
// Private Enemy01BehaviorState copies (for the diagnostic copy-read counter in the behaviour hook).
bool is_private_copy(const void* p);

// ---------------------------------------------------------------------------------------------- ai.cpp ----
void ai_install();
void ai_pre();
KilnResult ai_set_listener(KilnCreatureListenerFn f, void* user);
KilnResult ai_set_ready(KilnOwner owner, KilnEntity e, uint32_t mode);
void ai_release_owner(KilnOwner owner);
bool ai_forced(KilnEntity e);
bool lab_ready_switch();
struct AiStats {
    std::atomic<uint64_t> state_changes{0}, delivered{0}, dropped{0}, copy_reads{0};
};
extern AiStats AS;
bool ai_hooks_on();

// ---------------------------------------------------------------------------------------------- program.cpp ----
void program_register(const void* world_api);
KilnResult program_command(const KilnCreatureCommand* c);
KilnResult KILN_CALL program_npc_command(const KilnNpcCommand* c);
bool program_commands_active();
bool program_marks_active();
uint64_t program_commands_sent();
uint64_t program_marks_written();

// ---------------------------------------------------------------------------------------------- arsenals ----
uint32_t arsenal_count();
KilnResult arsenal_of(KilnEntity e, uint8_t out[16], uint32_t* known);
// The arsenal record the creature's AI reads now (the game's lookup by its EnemyArsenalReference GUID). Tick.
// KILN_ERR_UNAVAILABLE: no EnemyArsenalReference; KILN_ERR_NOT_FOUND: the entity is gone, or its arsenal's GUID is not
// in the registry (yet: a Kiln arsenal is installed in the next Pre).
KilnResult arsenal_record_of(KilnEntity e, const uint8_t** out);

// ---------------------------------------------------------------------------------------------- arsenal.cpp ----
// Offsets inside the arsenal blob, from the game's reflection (arsenal_init); 0 sizes mean the writer is off.
struct ArsenalLayout {
    uint32_t record_size = 0, attacks = 0, rules = 0;
    uint32_t attack_size = 0, attack_sequence = 0, attack_style = 0, attack_threat = 0, attack_selection = 0,
             attack_execution = 0, attack_abort = 0, attack_commands = 0, attack_data = 0;
    uint32_t rule_size = 0, rule_when = 0, rule_abort = 0, rule_actions = 0;
    uint32_t command_size = 0, command_constraints = 0, command_actions = 0;
};
extern ArsenalLayout AL;

// Kiln's own variant types in the arsenal's constraint and action lists. Both payloads start with what the game's
// dispatchers read of any variant: a constraint's first byte is `negated`.
constexpr uint32_t kRuleConstraintHash = fnv1a("kiln::npc::Constraint"), kRuleActionHash = fnv1a("kiln::npc::Action");
struct RuleCond {
    uint8_t negated;
    uint8_t kind;   // KILN_RC_*
    uint16_t slot;  // CONDITION: slot; JOB: the handler's argument
    uint32_t id;    // BUFF: buff type id; PHASE: phase hash; JOB: handler id
    float a, b;     // TIME: from, to (hours)
};
struct RuleAct {
    uint8_t kind;   // KILN_RA_*
    uint8_t reserved;
    uint16_t slot;  // JOB: the handler's argument
    uint32_t id;    // QUEUE: action id; PHASE: phase hash; JOB: handler id
    uint8_t guid[16];  // ATTACK: the attack's GUID
};

// What Kiln's arsenals use, so that the tick reads only that.
struct RuleUses {
    bool any_kiln = false, buffs = false, time = false, phase = false, attack_action = false;
};
RuleUses arsenal_uses();
void arsenal_init();
void arsenal_pre();
void arsenal_release_owner(KilnOwner owner);
bool arsenal_writer_ready();
// The arsenal record layout (AL) and the rule offsets, read from the reflection once. Everything that reads an arsenal's
// records (attack overrides included) needs it; the registry map is needed only for writing.
bool arsenal_layout_ready();
const char* arsenal_layout_error();
uint32_t arsenal_count_kiln();
void arsenal_map_stats(uint32_t* capacity, uint32_t* count);
KilnResult KILN_CALL arsenal_define_api(KilnOwner owner, KilnStr json, char* error, size_t cap, size_t* len);
KilnResult KILN_CALL arsenal_remove_api(KilnOwner owner, const KilnGuid* guid);
KilnResult KILN_CALL arsenal_info_api(const KilnGuid* guid, char* buffer, size_t cap, size_t* len);
KilnResult KILN_CALL arsenal_refresh_api(uint32_t* out);
KilnResult KILN_CALL arsenal_selftest_api(uint32_t* checked, uint32_t* bad);

// ---------------------------------------------------------------------------------------------- rules.cpp ----
void rules_resolve();
void rules_lifetime_init();
void rules_lifetime_unload(void*);
void rules_read_layout();
bool rules_ensure_hooks();
bool rules_ensure_attack_hooks();
bool rules_dispatcher_resolved();
bool rules_attacks_resolved();
void rules_unhook_if_idle();
void rules_pre_work();
void rules_publish();
bool rules_wants_listener();
void rules_release_owner(KilnOwner owner);
void rules_behavior_guard(uint8_t* ctx);
uint32_t KILN_CALL rules_drain_api(KilnRuleWork* out, uint32_t capacity);
KilnResult KILN_CALL rules_condition_set_api(KilnEntity e, uint32_t slot, uint32_t value);
KilnResult KILN_CALL rules_condition_clear_api(uint32_t slot);
KilnResult KILN_CALL rules_condition_stats_api(uint32_t slot, uint64_t* evaluated, uint64_t* truthy);
void KILN_CALL rules_pump_api(uint32_t flags);
KilnResult KILN_CALL rules_phase_set_api(KilnEntity e, uint32_t phase);
KilnResult KILN_CALL rules_phase_get_api(KilnEntity e, uint32_t* out);
KilnResult KILN_CALL rules_status_api(KilnRulesStatus* out);
KilnResult KILN_CALL rules_job_handler_api(KilnOwner owner, uint32_t id, KilnRuleJobFn fn, void* user);
KilnResult KILN_CALL attack_override_api(KilnOwner owner, KilnEntity e, const KilnAttackOverride* o);
KilnResult KILN_CALL attack_pick_arsenal_api(KilnOwner owner, const KilnGuid* arsenal, uint32_t enabled);
KilnResult KILN_CALL ai_switch_api(KilnOwner owner, KilnEntity e, uint32_t enabled);
KilnResult KILN_CALL ai_switch_get_api(KilnEntity e, uint32_t* out);
KilnResult KILN_CALL target_prefer_api(KilnOwner owner, KilnEntity e, KilnEntity target, float seconds);
void ai_behavior_guard_arm();  // ai.cpp: the BehaviorUpdate hook, for rules_behavior_guard

// Component hashes used across files.
namespace H {
constexpr uint32_t Faction = ecs_hash("Faction"), Level = ecs_hash("Level"), Scale = ecs_hash("ScaleComponent"),
                   Transform = ecs_hash("CurrentTransform"), Xp = ecs_hash("ExperienceSource"),
                   Loot = ecs_hash("RandomLoot"), LootSlots = ecs_hash("RandomLootSlotCount"),
                   Resist = ecs_hash("DamageSusceptibility"), Damage = ecs_hash("DamageMod"),
                   Movement = ecs_hash("Movement"), Dyn = ecs_hash("DynamicEnemy01BehaviorState"),
                   Tpl = ecs_hash("Enemy01BehaviorState"), NetEnemy = ecs_hash("NetworkEnemy"),
                   Targets = ecs_hash("SelectedTargets"), Actor = ecs_hash("Actor"), Health = ecs_hash("Health"),
                   Arsenal = ecs_hash("EnemyArsenalReference"), Scaling = ecs_hash("DynamicEnemyScaling");
}  // namespace H

}  // namespace kcr
