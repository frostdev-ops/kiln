// SPDX-License-Identifier: MIT
// kiln.creatures: per-creature runtime (values with owner layers, private template copies), the creature catalog,
// AI state/target events, commands, the lab readiness switch. Contract: native/core/abi/services/kiln_creatures.h;
// evidence: docs/re/creatures.md; usage: docs/kiln/services/creatures.md.
#include <cmath>
#include <cstdarg>
#include <cstdio>

#include "internal.h"
#include "services/kiln_spawn.h"
#include "services/kiln_world.h"

namespace kcr {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
uint64_t g_step = 0;
Layout L;
Fns fn;
Features F;

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    std::va_list ap;
    va_start(ap, fmt);
    int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n < 0) return;
    if (n >= static_cast<int>(sizeof buf)) n = sizeof buf - 1;
    if (g_api) g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n)});
}

int64_t m_to_fixed(double m) { return static_cast<int64_t>(std::llround(m * 4294967296.0)); }

bool on_tick() { return g_api && g_api->on_tick_thread && g_api->on_tick_thread(); }
uintptr_t world() { return g_api && g_api->server_world ? g_api->server_world() : 0; }

void* component(KilnEntity e, uint32_t hash, uint32_t* size) {
    if (size) *size = 0;
    void* p = nullptr;
    uint32_t n = 0;
    if (!seh_component(world(), hash, e, &p, &n)) return nullptr;
    if (size) *size = n;
    return p;
}

bool entity_exists(KilnEntity e) {
    uintptr_t ti = 0;
    return e && seh_record_template(world(), e, &ti) && ti;
}

namespace {

const KilnSpawnApi* g_spawn = nullptr;
uint32_t g_arsenal_count = 0;
bool g_arsenals_checked = false;
bool g_ready = false;

bool sym(const char* name, uintptr_t* out) {
    return g_api->symbol(KilnStr{name, std::strlen(name)}, out) == KILN_OK && *out;
}
bool cst(const char* name, int64_t* out) {
    return g_api->constant(KilnStr{name, std::strlen(name)}, out) == KILN_OK;
}
bool feat(const char* name) { return g_api->feature(KilnStr{name, std::strlen(name)}); }

void resolve() {
    uintptr_t a = 0;
    if (cst("world.storage", &L.world_storage) && sym("GetComponent", &a)) {
        fn.get_component = reinterpret_cast<Fns::GetComponentFn>(a);
        F.components = true;
    }
    if (sym("GetEntityTransform", &a)) fn.get_transform = reinterpret_cast<Fns::GetTransformFn>(a);
    if (feat("creatures.slots") && sym("GetTemplateComponent", &a)) {
        fn.get_template_component = reinterpret_cast<Fns::GetTemplateComponentFn>(a);
        uintptr_t m = 0, e = 0;
        F.slots = F.components && sym("creatures/ComponentMapFind", &m) && sym("creatures/EntityMapFind", &e) &&
                  cst("creatures/registry.tpl_map", &L.tpl_map) && cst("creatures/storage.entity_map", &L.entity_map) &&
                  cst("creatures/map.cap", &L.map_cap) && cst("creatures/map.values", &L.map_values) &&
                  cst("creatures/record.archetype", &L.rec_archetype) && cst("creatures/record.index", &L.rec_index) &&
                  cst("creatures/record.chunk", &L.rec_chunk) && cst("creatures/archetype.tpl_columns", &L.tpl_columns);
        fn.component_map_find = reinterpret_cast<Fns::ComponentMapFindFn>(m);
        fn.entity_map_find = reinterpret_cast<Fns::EntityMapFindFn>(e);
        int64_t rt = 0;
        if (cst("spawn/record.template", &rt)) L.rec_template = rt;
    }
    if (feat("creatures.ai")) {
        uintptr_t s = 0, h = 0, b = 0, c = 0;
        F.ai = sym("creatures/SetState", &s) && sym("creatures/HandleCondition", &h) &&
               sym("creatures/BehaviorUpdate", &b) && sym("world/CurrentEntityId", &c) &&
               cst("creatures/ctx.dyn", &L.ctx_dyn) && cst("creatures/ctx.tpl", &L.ctx_tpl) &&
               cst("creatures/ctx.program", &L.ctx_program) && cst("creatures/fsm.conditions", &L.conditions) &&
               cst("creatures/dyn.enabled_features", &L.dyn_enabled) &&
               cst("creatures/dyn.disabled_features", &L.dyn_disabled) &&
               cst("creatures/tpl.enabled_features", &L.tpl_enabled);
        fn.set_state = s;
        fn.handle_condition = h;
        fn.behavior_update = b;
        fn.current_entity = reinterpret_cast<Fns::CurrentEntityFn>(c);
    } else {
        cst("creatures/dyn.enabled_features", &L.dyn_enabled);
        cst("creatures/dyn.disabled_features", &L.dyn_disabled);
    }
    if (feat("creatures.table") && sym("creatures/TransitionTable", &a)) {
        fn.transition_table = a;
        F.table = true;
    }
    F.ready = feat("creatures.ready") && cst("creatures/actor.state", &L.actor_state) &&
              cst("creatures/actor.ready_bit", &L.ready_bit) && L.ready_bit >= 0 && L.ready_bit < 64;
    if (feat("creatures.arsenals") && sym("creatures/ArsenalFind", &a)) {
        fn.arsenal_find = reinterpret_cast<Fns::ArsenalFindFn>(a);
        cst("creatures/arsenals.data", &L.arsenals_data);
        F.arsenals = true;
    }
    if (feat("creatures.reflection") && sym("creatures/TypeByHash", &a)) {
        fn.type_by_hash = reinterpret_cast<Fns::TypeByHashFn>(a);
        F.reflection = cst("creatures/type.hash", &L.type_hash) && cst("creatures/type.qname", &L.type_qname) &&
                       cst("creatures/type.inner", &L.type_inner) && cst("creatures/type.size", &L.type_size) &&
                       cst("creatures/type.count", &L.type_count) && cst("creatures/type.kind", &L.type_kind) &&
                       cst("creatures/type.fields", &L.type_fields) && cst("creatures/type.enum_values", &L.type_enums) &&
                       cst("creatures/field.stride", &L.field_stride) && cst("creatures/field.type", &L.field_type) &&
                       cst("creatures/field.offset", &L.field_offset) && cst("creatures/enum.stride", &L.enum_stride) &&
                       cst("creatures/enum.value", &L.enum_value);
    }
    if (feat("creatures.commands")) {
        uintptr_t e = 0, p = 0, b = 0;
        F.commands = sym("gameplay/EmitEvent", &e) && sym("gameplay/JobProgramId", &p) && sym("gameplay/JobEventBuffer", &b);
        fn.emit = reinterpret_cast<Fns::EmitFn>(e);
        fn.job_program = reinterpret_cast<Fns::JobProgramIdFn>(p);
        fn.job_buffer = reinterpret_cast<Fns::JobBufferFn>(b);
    }
}

void check_arsenals() {
    if (g_arsenals_checked || !F.arsenals) return;
    const uintptr_t w = world();
    if (!w) return;
    g_arsenals_checked = true;
    const uintptr_t reg = g_api->singleton(w, kstr("g38_enemy::EnemyArsenalRegistry"));
    uint8_t* data = nullptr;
    uint32_t blob[2] = {};
    if (!reg || !seh_read(reinterpret_cast<void*>(reg + L.arsenals_data), &data, 8) || !data ||
        !seh_read(data, blob, 8) || blob[0] != 8 || blob[1] == 0 || blob[1] > 100000) {
        KC_W("arsenal registry: unexpected layout (singleton 0x%llx); arsenal lookups off",
             static_cast<unsigned long long>(reg));
        F.arsenals = false;
        return;
    }
    g_arsenal_count = blob[1];
}

void KILN_CALL on_pre(const KilnTickInfo* t, void*) {
    g_step = t ? t->step : g_step + 1;
    if (!world()) return;
    if (!g_ready) {
        g_ready = true;
        check_arsenals();
        // The arsenal record layout and the rule offsets (AI switch, target hold, phases, buffs) come from the
        // reflection; read them now, not only when an arsenal is first written.
        if (F.reflection && !arsenal_layout_ready()) KC_W("arsenals: record layout unavailable: %s", arsenal_layout_error());
        KC_I("ready: slots %s, ai hooks %s, table %s, ready bit %s, arsenals %s (%u), reflection %s, commands %s, "
             "marks %s, lab readiness switch %s",
             F.slots ? "on" : "off", ai_hooks_on() ? "on" : "off", F.table ? "on" : "off", F.ready ? "on" : "off",
             F.arsenals ? "on" : "off", g_arsenal_count, F.reflection ? "on" : "off",
             program_commands_active() ? "on" : "off", program_marks_active() ? "on" : "off",
             lab_ready_switch() ? "ON (lab only)" : "off");
    }
    if (g_spawn && g_spawn->world_state && g_spawn->world_state() == KILN_SPAWN_WORLD_RUNNING) catalog_check_engine();
    layers_pre();
    arsenal_pre();
    rules_pre_work();
    ai_pre();
    rules_publish();
    rules_unhook_if_idle();
}

void KILN_CALL on_ready(void*) {
    g_spawn = static_cast<const KilnSpawnApi*>(g_api->service(kstr(KILN_SPAWN_SERVICE), 1));
    catalog_init(g_spawn);
    program_register(g_api->service(kstr(KILN_WORLD_SERVICE), 1));
}

// ---------------------------------------------------------------------------------------------- the table ----

#define TICK_ONLY()                                \
    do {                                           \
        if (!on_tick()) return KILN_ERR_WRONG_THREAD; \
        if (!world()) return KILN_ERR_UNAVAILABLE; \
    } while (0)

KilnResult KILN_CALL t_status(KilnCreaturesStatus* out) {
    if (!out || out->size < 8) return KILN_ERR_INVALID_ARGUMENT;
    KilnCreaturesStatus s{};
    s.size = sizeof s;
    s.slots = F.slots;
    s.ai = ai_hooks_on();
    s.ready = F.ready;
    s.arsenals = F.arsenals;
    s.reflection = F.reflection;
    s.commands = program_commands_active();
    s.marks = program_marks_active();
    s.lab_ready = lab_ready_switch();
    s.catalog = catalog_count();
    catalog_stats(&s.catalog_loaded, &s.engine_creatures);
    s.arsenal_count = g_arsenal_count;
    layers_stats(&s.entities, &s.layers, &s.private_copies, &s.pending_free);
    s.state_changes = AS.state_changes.load();
    s.events_delivered = AS.delivered.load();
    s.events_dropped = AS.dropped.load();
    s.commands_sent = program_commands_sent();
    s.marks_written = program_marks_written();
    s.copy_reads = AS.copy_reads.load();
    const uint32_t keep = out->size;
    std::memcpy(out, &s, std::min<size_t>(keep, sizeof s));
    out->size = keep;
    return KILN_OK;
}

uint32_t KILN_CALL t_template_count() { return catalog_count(); }
KilnResult KILN_CALL t_template_at(uint32_t i, KilnCreatureTemplate* out) {
    if (!out || out->size < sizeof(KilnCreatureTemplate)) return KILN_ERR_INVALID_ARGUMENT;
    return catalog_at(i, out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL t_template_get(const KilnGuid* g, KilnCreatureTemplate* out) {
    if (!g || !out || out->size < sizeof(KilnCreatureTemplate)) return KILN_ERR_INVALID_ARGUMENT;
    return catalog_get(g->b, out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}
KilnResult KILN_CALL t_template_find(KilnStr name, KilnCreatureTemplate* out) {
    if (!out || out->size < sizeof(KilnCreatureTemplate) || !name.len) return KILN_ERR_INVALID_ARGUMENT;
    return catalog_find(sv(name), out) ? KILN_OK : KILN_ERR_NOT_FOUND;
}

KilnResult KILN_CALL t_inspect(KilnEntity e, KilnCreatureInfo* out) {
    if (!out || out->size < sizeof(KilnCreatureInfo)) return KILN_ERR_INVALID_ARGUMENT;
    TICK_ONLY();
    if (!entity_exists(e)) return KILN_ERR_NOT_FOUND;
    KilnCreatureInfo i{};
    i.size = sizeof i;
    i.faction = 255;
    template_guid(e, i.template_guid.b);
    KilnCreatureTemplate t{};
    t.size = sizeof t;
    if (catalog_get(i.template_guid.b, &t)) i.kind = t.kind;
    uint32_t n = 0;
    if (auto* d = static_cast<uint8_t*>(component(e, H::Dyn, &n))) {
        i.flags |= KILN_CREATURE_I_AI;
        seh_read(d, &i.state, 1);
        if (L.dyn_enabled >= 0) seh_read(d + L.dyn_enabled, &i.features_enabled, 4);
        if (L.dyn_disabled >= 0) seh_read(d + L.dyn_disabled, &i.features_disabled, 4);
    }
    void* tpl = nullptr;
    if (L.tpl_enabled >= 0 && seh_template_component(world(), H::Tpl, e, &tpl) && tpl)
        seh_read(static_cast<uint8_t*>(tpl) + L.tpl_enabled, &i.features_template, 4);
    if (auto* ne = static_cast<uint8_t*>(component(e, H::NetEnemy, &n)); ne && n >= 8) {
        seh_read(ne + 1, &i.awareness_state, 1);
        seh_read(ne + 4, &i.awareness, 4);
    }
    KilnCreatureValue v{};
    if (layer_get(e, KILN_CF_FACTION, 0, &v) == KILN_OK) i.faction = static_cast<uint8_t>(v.number);
    if (auto* lv = static_cast<uint8_t*>(component(e, H::Level, &n)); lv && n >= 16) {
        seh_read(lv + 8, &i.level, 4);
        seh_read(lv + 12, &i.level_max, 4);
    }
    if (layer_get(e, KILN_CF_SCALE, 0, &v) == KILN_OK) i.scale = static_cast<float>(v.number);
    if (layer_get(e, KILN_CF_XP, 0, &v) == KILN_OK) i.xp_mod = static_cast<float>(v.number);
    if (layer_get(e, KILN_CF_DAMAGE, 0, &v) == KILN_OK) i.damage_multiplier = static_cast<float>(v.number);
    if (layer_get(e, KILN_CF_LOOT_LABELS, 0, &v) == KILN_OK) i.loot_labels = v.guid;
    if (layer_get(e, KILN_CF_LOOT_SLOTS, 0, &v) == KILN_OK) {
        i.loot_slots_min = static_cast<uint8_t>(v.vec.x);
        i.loot_slots_max = static_cast<uint8_t>(v.vec.y);
    }
    if (layer_get(e, KILN_CF_LOOT_DISABLED, 0, &v) == KILN_OK) i.loot_disabled = v.number != 0;
    i.speed_factor = 1;
    if (auto* h = static_cast<uint8_t*>(component(e, H::Health, &n)); h && n >= 40) {
        // Health attribute: slot 0 = current, slot 7 = Health_Max (attribute structure order, docs/re/creatures.md)
        uint16_t off = 0;
        seh_read(h + 4, &off, 2);
        if (off == 0 || off + 32u > n) off = 8;
        seh_read(h + off, &i.health, 4);
        seh_read(h + off + 7 * 4, &i.health_max, 4);
    }
    if (auto* a = static_cast<uint8_t*>(component(e, H::Actor, &n)); a && L.actor_state >= 0 &&
                                                                       static_cast<uint32_t>(L.actor_state) + 8 <= n) {
        uint64_t st = 0;
        if (seh_read(a + L.actor_state, &st, 8)) {
            if ((st >> L.dead_bit) & 1) i.flags |= KILN_CREATURE_I_DEAD;
            if (L.ready_bit >= 0 && ((st >> L.ready_bit) & 1)) i.flags |= KILN_CREATURE_I_READY;
        }
    }
    if (ai_forced(e)) i.flags |= KILN_CREATURE_I_FORCED;
    if (auto* st = static_cast<uint8_t*>(component(e, H::Targets, &n)); st) {
        // targets[0]: the root entity when the target is a part of one, else the entity itself (the game's own target
        // reads use targetEntityId; targetRootEntityId stays 0 for a creature fighting a creature)
        static int64_t off_root = -2, off_entity = -1;
        if (off_root == -2) {
            off_root = -1;
            TypeInfo ti;
            std::string err;
            FieldRef f;
            if (type_info("keen::ecs::SelectedTargets", &ti, &err)) {
                if (resolve_path(ti, "targets[0].targetRootEntityId", &f, &err)) off_root = f.offset;
                if (resolve_path(ti, "targets[0].targetEntityId", &f, &err)) off_entity = f.offset;
            }
        }
        if (off_root >= 0 && static_cast<uint32_t>(off_root) + 4 <= n) seh_read(st + off_root, &i.target, 4);
        if (!i.target && off_entity >= 0 && static_cast<uint32_t>(off_entity) + 4 <= n) seh_read(st + off_entity, &i.target, 4);
    }
    layers_info(e, &i);
    const uint32_t keep = out->size;
    std::memcpy(out, &i, std::min<size_t>(keep, sizeof i));
    out->size = keep;
    return KILN_OK;
}

KilnResult KILN_CALL t_set(KilnOwner owner, KilnEntity e, uint32_t field, uint32_t index, const KilnCreatureValue* v) {
    TICK_ONLY();
    return layer_set(owner, e, field, index, v);
}
KilnResult KILN_CALL t_get(KilnEntity e, uint32_t field, uint32_t index, KilnCreatureValue* out) {
    TICK_ONLY();
    return layer_get(e, field, index, out);
}
KilnResult KILN_CALL t_reset(KilnOwner owner, KilnEntity e, uint32_t field, uint32_t index, uint32_t* n) {
    TICK_ONLY();
    return layer_reset(owner, e, field, index, n);
}

KilnResult KILN_CALL t_ai_set(KilnOwner owner, KilnEntity e, KilnStr comp, KilnStr path, KilnStr value) {
    TICK_ONLY();
    return copy_set(owner, e, sv(comp), sv(path), sv(value));
}
KilnResult KILN_CALL t_ai_get(KilnEntity e, KilnStr comp, KilnStr path, char* buf, size_t cap, size_t* len,
                              uint32_t* is_private) {
    TICK_ONLY();
    std::string s;
    KilnResult r = copy_get(e, sv(comp), sv(path), &s, is_private);
    if (r != KILN_OK) return r;
    if (len) *len = s.size();
    if (!buf || cap < s.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, s.data(), s.size());
    buf[s.size()] = 0;
    return KILN_OK;
}
KilnResult KILN_CALL t_ai_reset(KilnOwner owner, KilnEntity e, KilnStr comp, KilnStr path, uint32_t* n) {
    TICK_ONLY();
    return copy_reset(owner, e, sv(comp), sv(path), n);
}
KilnResult KILN_CALL t_ai_refresh(KilnEntity e, uint32_t* n) {
    TICK_ONLY();
    return copy_refresh(e, n);
}
KilnResult KILN_CALL t_ai_fields(KilnStr comp, char* buf, size_t cap, size_t* len) {
    TypeInfo t;
    std::string err;
    if (!type_info(component_qname(sv(comp)), &t, &err)) return F.reflection ? KILN_ERR_NOT_FOUND : KILN_ERR_UNAVAILABLE;
    const std::string j = fields_json(t);
    if (len) *len = j.size();
    if (!buf || cap < j.size() + 1) return KILN_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buf, j.data(), j.size());
    buf[j.size()] = 0;
    return KILN_OK;
}

uint32_t KILN_CALL t_release_owner(KilnOwner owner) {
    if (!on_tick()) {
        request_release(owner);
        return 0;
    }
    return release_owner_now(owner);
}

KilnResult KILN_CALL t_set_listener(KilnCreatureListenerFn f, void* user) { return ai_set_listener(f, user); }
KilnResult KILN_CALL t_command(const KilnCreatureCommand* c) {
    TICK_ONLY();
    return program_command(c);
}
KilnResult KILN_CALL t_set_ready(KilnOwner owner, KilnEntity e, uint32_t mode) {
    TICK_ONLY();
    return ai_set_ready(owner, e, mode);
}
KilnResult KILN_CALL t_arsenal_of(KilnEntity e, KilnGuid* out, uint32_t* known) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    TICK_ONLY();
    return arsenal_of(e, out->b, known);
}

const KilnCreaturesApi kTable = {
    {sizeof(KilnCreaturesApi), KILN_CREATURES_VERSION},
    &t_status,
    &t_template_count, &t_template_at, &t_template_get, &t_template_find,
    &t_inspect, &t_set, &t_get, &t_reset,
    &t_ai_set, &t_ai_get, &t_ai_reset, &t_ai_refresh, &t_ai_fields,
    &t_release_owner,
    &t_set_listener, &t_command, &t_set_ready, &t_arsenal_of, &program_npc_command,
    &arsenal_define_api, &arsenal_remove_api, &arsenal_info_api, &arsenal_refresh_api, &arsenal_selftest_api,
    &rules_drain_api, &rules_condition_set_api, &rules_condition_clear_api, &rules_condition_stats_api, &rules_pump_api,
    &rules_phase_set_api, &rules_phase_get_api, &rules_status_api, &rules_job_handler_api,
    &attack_override_api, &attack_pick_arsenal_api, &ai_switch_api, &ai_switch_get_api, &target_prefer_api,
};

}  // namespace

bool template_guid(KilnEntity e, uint8_t out[16]) {
    std::memset(out, 0, 16);
    uintptr_t ti = 0;
    return seh_record_template(world(), e, &ti) && ti && seh_read(reinterpret_cast<void*>(ti), out, 16);
}

uint32_t arsenal_count() { return g_arsenal_count; }

KilnResult arsenal_of(KilnEntity e, uint8_t out[16], uint32_t* known) {
    if (known) *known = 0;
    std::memset(out, 0, 16);
    void* p = nullptr;
    if (!seh_template_component(world(), H::Arsenal, e, &p) || !p)
        return entity_exists(e) ? KILN_ERR_UNAVAILABLE : KILN_ERR_NOT_FOUND;
    if (!seh_read(p, out, 16)) return KILN_ERR_INTERNAL;
    if (known && F.arsenals) {
        const uintptr_t reg = g_api->singleton(world(), kstr("g38_enemy::EnemyArsenalRegistry"));
        void* a = nullptr;
        if (reg && seh_arsenal_find(reinterpret_cast<void*>(reg), out, &a) && a) *known = 1;
    }
    return KILN_OK;
}

KilnResult arsenal_record_of(KilnEntity e, const uint8_t** out) {
    *out = nullptr;
    uint8_t guid[16];
    if (const KilnResult rc = arsenal_of(e, guid, nullptr); rc != KILN_OK) return rc;
    if (!F.arsenals) return KILN_ERR_NOT_FOUND;
    const uintptr_t reg = g_api->singleton(world(), kstr("g38_enemy::EnemyArsenalRegistry"));
    void* a = nullptr;
    if (!reg || !seh_arsenal_find(reinterpret_cast<void*>(reg), guid, &a) || !a) return KILN_ERR_NOT_FOUND;
    *out = static_cast<const uint8_t*>(a);
    return KILN_OK;
}

}  // namespace kcr

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kcr;
    if (api->abi_version != KILN_ABI_VERSION || api->size < offsetof(KilnApi, side)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    info->id = kstr(KILN_CREATURES_SERVICE);
    info->version = kstr("1");
    info->on_ready = &on_ready;
    info->on_unload = &rules_lifetime_unload;
    resolve();
    if (!F.components) {
        KC_W("GetComponent / world.storage unresolved: kiln.creatures is unavailable on this build");
        return KILN_ERR_UNAVAILABLE;
    }
    ai_install();
    rules_resolve();
    // Before every service's on_ready: npcai prepares its own observer there and must chain through this one.
    rules_lifetime_init();
    arsenal_init();
    KilnHandle h = 0;
    if (KilnResult rc = api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &h); rc != KILN_OK) return rc;
    KC_I("resolved: slots %s, ai %s (hooks %s), ready %s, arsenals %s, reflection %s, commands %s",
         F.slots ? "on" : "off", F.ai ? "on" : "off", ai_hooks_on() ? "installed" : "off", F.ready ? "on" : "off",
         F.arsenals ? "on" : "off", F.reflection ? "on" : "off", F.commands ? "on" : "off");
    return api->register_service(owner, kstr(KILN_CREATURES_SERVICE), &kTable);
}
