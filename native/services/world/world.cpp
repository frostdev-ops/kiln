// SPDX-License-Identifier: MIT
// kiln.world: the server world's ECS for other services (native/core/abi/services/kiln_world.h).
// Resolves its symbols and layout constants (sigdb/symbols/server.d/world.json), hooks RegisterAllPrograms, registers
// its own system program (teleport pushes, "the world is live"), and publishes the table. Every piece fails soft: a
// missing symbol turns off only what needs it; the server runs on.
#include <cstdarg>
#include <cstdio>

#include "wraps.h"

namespace kw {

const KilnApi* g_api = nullptr;
KilnOwner g_owner = 0;
Fns fn;
Layout L;
Features F;
std::atomic<uintptr_t> g_world{0};
std::atomic<uint64_t> g_internal_runs{0};
std::atomic<bool> g_world_live{false};
thread_local bool t_in_program = false;

void logf(int level, const char* fmt, ...) {
    if (!g_api) return;
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (n <= 0) return;
    g_api->log(g_owner, level, KilnStr{buf, static_cast<size_t>(n < static_cast<int>(sizeof buf) ? n : sizeof buf - 1)});
}

uint32_t fnv1a(std::string_view s, uint32_t h) {
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}

uint32_t component_hash(std::string_view name) {
    if (name.find("::") != std::string_view::npos) return fnv1a(name);
    return fnv1a(name, fnv1a("keen::ecs::"));
}

namespace {

KilnHandle g_internal = 0;
KilnHandle g_tick = 0;
uint64_t g_last_runs = 0;
bool g_seen_world_step = false;

bool feature(const char* n) { return g_api->feature(KilnStr{n, std::strlen(n)}); }

uintptr_t sym(const char* n) {
    uintptr_t a = 0;
    return g_api->symbol(KilnStr{n, std::strlen(n)}, &a) == KILN_OK ? a : 0;
}

int64_t cst(const char* n) {
    int64_t v = -1;
    return g_api->constant(KilnStr{n, std::strlen(n)}, &v) == KILN_OK ? v : -1;
}

bool resolve() {
    F.programs = feature("world.programs");
    F.queue = feature("world.queue");
    F.write = feature("world.write");
    F.read_other = feature("world.read_other");
    F.entities = feature("world.entities");
    F.find = feature("world.find");
    F.names = feature("world.names");
    F.singletons = feature("world.names.singletons");
    F.events = feature("world.names.events");
    F.teleport = feature("world.teleport");
    F.dead = feature("world.dead_check");

    fn.register_all = sym("world/RegisterAllPrograms");
    fn.register_program = reinterpret_cast<RegisterProgramFn>(sym("world/RegisterProgram"));
    fn.iter_begin = reinterpret_cast<IterBeginFn>(sym("world/IterBegin"));
    fn.iter_next = reinterpret_cast<IterNextFn>(sym("world/IterNext"));
    fn.current_entity = reinterpret_cast<CurrentEntityFn>(sym("world/CurrentEntityId"));
    fn.queue_push = reinterpret_cast<QueuePushFn>(sym("world/QueuePush"));
    fn.mark_written = reinterpret_cast<MarkWrittenFn>(sym("world/MarkWritten"));
    fn.read_other = reinterpret_cast<ReadOtherFn>(sym("world/ReadOther"));
    // Tracked path already captured and lab checked by kiln.items (docs/re/items.md section 12).
    fn.write_other = reinterpret_cast<WriteOtherFn>(sym("items/WriteOther"));
    fn.job_tracker = reinterpret_cast<JobTrackerFn>(sym("items/JobTracker"));
    fn.job_write_flag = reinterpret_cast<JobWriteFlagFn>(sym("items/JobWriteFlag"));
    F.write_other = fn.write_other && fn.job_tracker && fn.job_write_flag;
    writes_resolve();
    fn.emit_event = reinterpret_cast<EmitEventFn>(sym("gameplay/EmitEvent"));
    fn.job_program_id = reinterpret_cast<JobProgramIdFn>(sym("gameplay/JobProgramId"));
    fn.job_event_buffer = reinterpret_cast<JobEventBufferFn>(sym("gameplay/JobEventBuffer"));
    fn.program_time = reinterpret_cast<ProgramTimeFn>(sym("gameplay/ProgramTime"));
    fn.get_component = reinterpret_cast<GetComponentFn>(sym("GetComponent"));
    fn.get_transform = reinterpret_cast<GetEntityTransformFn>(sym("GetEntityTransform"));

    L.world_storage = cst("world.storage");
    L.storage_kind = cst("storage.kind");
    L.programs_count = cst("world/programs.count");
    L.programs_max = cst("world/programs.max");
    L.programs_table = cst("world/programs.table");
    L.programs_stride = cst("world/programs.stride");
    if (const int64_t v = cst("world/entry.flags"); v >= 0) L.entry_flags = v;
    if (const int64_t v = cst("world/entry.id"); v >= 0) L.entry_id = v;
    L.may_finalize = cst("world/world.may_finalize");
    L.job_entity_list = cst("world/job.entity_list");
    L.job_entity_count = cst("world/job.entity_count");
    if (const int64_t v = cst("world/ctx.index"); v >= 0) L.ctx_index = v;
    L.job_tpl_count = cst("world/job.tpl_count");
    L.job_read_count = cst("world/job.read_count");
    L.job_write_count = cst("world/job.write_count");
    L.job_singletons = cst("world/job.singletons");
    L.singletons_count = cst("world/singletons.count");
    L.job_other_tpl_count = cst("world/job.other_tpl_count");
    L.job_other_count = cst("world/job.other_count");
    L.job_events_in_count = cst("world/job.events_in_count");
    L.job_events_out_count = cst("world/job.events_out_count");
    L.job_cmdbuf = cst("world/job.cmdbuf");
    L.cmdbuf_queue = cst("world/cmdbuf.queue");
    L.job_storage = cst("world/job.storage");
    L.rec_hashes = cst("world/record.hashes");
    L.rec_sizes = cst("world/record.sizes");
    L.rec_data = cst("world/record.data");
    L.reg_components = cst("world/registry.components");
    L.reg_templates = cst("world/registry.templates");
    L.storage_entities = cst("world/storage.entities");
    L.entity_layout = cst("world/entity.layout");
    L.tp_applied = cst("world/teleport.applied");
    L.tp_find_ground = cst("world/teleport.find_ground");
    L.tp_target_entity = cst("world/teleport.target_entity");
    L.tp_add_size = cst("world/record.teleport_size");
    L.tp_remove_size = cst("world/record.remove_size");
    L.actor_state = cst("world/actor.state");

    // The descriptor layout kiln.world builds must be the one RegisterProgram reads.
    if (F.programs && (cst("world/desc.flags") != offsetof(EngineDesc, flags) ||
                       (cst("world/desc.interval") >= 0 && cst("world/desc.interval") != offsetof(EngineDesc, interval)) ||
                       (cst("world/programs.names") >= 0 && cst("world/programs.names") != L.programs_table + 0x10) ||
                       L.ctx_index != offsetof(ProgramCtx, index))) {
        KW_E("program descriptor/context layout differs from kiln.world's; programs are off");
        F.programs = false;
    }

    // Generic keen hash map fields. The entity map (storage+0x148 on 1285601) gives its capacity and values offsets;
    // the singleton and event maps (both inline in their resolvers) give every field and must agree with them.
    const int64_t ent = L.storage_entities, ent_cap = cst("world/storage.entities.cap"),
                  ent_val = cst("world/storage.entities.values");
    const int64_t s_cnt = cst("world/registry.singletons.count"), s_cap = cst("world/registry.singletons.cap"),
                  s_bits = cst("world/registry.singletons.bits"), s_keys = cst("world/registry.singletons.keys"),
                  s_val = cst("world/registry.singletons.values");
    const int64_t e_cnt = cst("world/registry.events.count"), e_cap = cst("world/registry.events.cap"),
                  e_bits = cst("world/registry.events.bits"), e_keys = cst("world/registry.events.keys"),
                  e_val = cst("world/registry.events.values");
    bool map_ok = ent >= 0 && ent_cap > ent && ent_val > ent && s_cap >= 0 && s_cnt >= 0 && s_bits >= 0 &&
                  s_keys >= 0 && s_val >= 0;
    if (map_ok) {
        L.map.cap = ent_cap - ent;
        L.map.values = ent_val - ent;
        L.reg_singletons = s_cap - L.map.cap;
        L.map.bits = s_bits - L.reg_singletons;
        L.map.keys = s_keys - L.reg_singletons;
        L.map.count = s_cnt - L.reg_singletons;
        map_ok = s_val - L.reg_singletons == L.map.values && L.map.bits >= 0 && L.map.keys >= 0 && L.map.count >= 0;
    }
    if (map_ok && e_cap >= 0) {
        L.reg_events = e_cap - L.map.cap;
        const bool agree = e_bits - L.reg_events == L.map.bits && e_keys - L.reg_events == L.map.keys &&
                           e_val - L.reg_events == L.map.values && e_cnt - L.reg_events == L.map.count;
        if (!agree) {
            KW_W("event map fields disagree with the generic hash map layout; event names cannot be checked");
            F.events = false;
        }
    } else {
        F.events = false;
    }
    if (!map_ok) {
        KW_W("hash map layout not confirmed on this build: name checks and find are off");
        L.map = MapFields{};
        F.names = F.singletons = F.events = F.find = false;
    }

    if (F.programs && !fn.register_all) F.programs = false;
    if (!fn.get_component || !fn.get_transform || L.world_storage < 0) F.entities = false;
    if (!F.entities) F.find = F.teleport = false;
    if (!F.queue || !F.programs) F.teleport = false;
    if (L.actor_state < 0) F.dead = false;
    if (L.reg_components < 0) F.find = false;
    if (!F.names) F.singletons = F.events = false;
    return true;
}

void KILN_CALL on_pre(const KilnTickInfo* tick, void*) {
    game_programs_pre(KILN_WORLD_SERVER, tick);
    const uint64_t runs = g_internal_runs.load(std::memory_order_acquire);
    const bool live = runs != g_last_runs;
    g_last_runs = runs;
    g_world_live.store(live, std::memory_order_relaxed);
    if (live && !g_seen_world_step) {
        g_seen_world_step = true;
        KW_I("server world 0x%llx is live (own program ran; %u engine programs)",
             static_cast<unsigned long long>(g_world.load()), engine_program_count(g_world.load()));
    }
    if (live) programs_after_first_step(g_world.load());
    programs_reap_owners();
    teleport_pre(tick);
    writes_pre(tick);
    selftest_pre(tick);
}

// ---- the table ----
KilnResult KILN_CALL t_register_program(KilnOwner owner, const KilnProgramDesc* desc, KilnHandle* out) {
    return register_program(owner, desc, out, false);
}
int32_t KILN_CALL t_program_slot(KilnHandle h, int32_t kind, KilnStr name) { return program_slot(h, kind, name); }
KilnResult KILN_CALL t_program_info(KilnHandle h, KilnProgramInfo* out) { return program_info(h, out); }
KilnResult KILN_CALL t_iter_begin(KilnProgramCtx* c, uint64_t* it, uint32_t size) { return iter_begin(c, it, size); }
bool KILN_CALL t_iter_next(KilnProgramCtx* c, uint64_t* it, uint32_t size) { return iter_next(c, it, size); }
KilnEntity KILN_CALL t_current_entity(KilnProgramCtx* c) { return current_entity(c); }
void* KILN_CALL t_command_buffer(KilnProgramCtx* c) { return command_buffer(c); }
void* KILN_CALL t_queue_push(KilnProgramCtx* c, uint8_t kind, uint32_t size, KilnEntity e) {
    return queue_push(c, kind, size, e);
}
KilnResult KILN_CALL t_queue_add(KilnProgramCtx* c, KilnEntity e, uint32_t hash, const void* data, uint32_t size) {
    return queue_add(c, e, hash, data, size);
}
KilnResult KILN_CALL t_queue_remove(KilnProgramCtx* c, KilnEntity e, uint32_t hash) { return queue_remove(c, e, hash); }
void KILN_CALL t_mark_written(KilnProgramCtx* c, const uint64_t* w) { mark_written(c, w); }
const void* KILN_CALL t_read_other(KilnProgramCtx* c, uint64_t accessor, KilnEntity e, uint32_t* size) {
    return read_other(c, accessor, e, size);
}
uint32_t KILN_CALL t_component_hash(KilnStr name) { return component_hash(sv(name)); }
// Inside a wrap stage the world is in the middle of its step (maybe on a worker): the tick-thread calls refuse there,
// also on the world's own thread, where on_tick_thread() alone would let them through.
const void* KILN_CALL t_component(KilnEntity e, uint32_t hash, uint32_t* size) {
    if (game_in_slice()) return size ? (*size = 0, nullptr) : nullptr;
    return component(e, hash, size);
}
bool KILN_CALL t_exists(KilnEntity e) { return !game_in_slice() && exists(e); }
KilnResult KILN_CALL t_transform(KilnEntity e, KilnTransform* out) {
    return game_in_slice() ? KILN_ERR_WRONG_THREAD : transform(e, out);
}
KilnResult KILN_CALL t_position(KilnEntity e, KilnVec3* out) {
    return game_in_slice() ? KILN_ERR_WRONG_THREAD : position(e, out);
}
KilnResult KILN_CALL t_find(uint32_t hash, KilnEntity* out, uint32_t cap, uint32_t* count) {
    if (game_in_slice()) {
        if (count) *count = 0;
        return KILN_ERR_WRONG_THREAD;
    }
    return find(hash, out, cap, count);
}
uint32_t KILN_CALL t_entity_count() { return game_in_slice() ? 0 : entity_count(); }
KilnResult KILN_CALL t_unregister_program(KilnHandle h) {
    return game_in_slice() ? KILN_ERR_WRONG_THREAD : unregister_program(h);
}
KilnResult KILN_CALL t_bind_program(KilnOwner owner, KilnStr mod, KilnStr key, KilnProgramEachFn each, void* user,
                                    KilnHandle* out) {
    if (game_in_slice()) {
        if (out) *out = 0;
        return KILN_ERR_WRONG_THREAD;
    }
    return bind_program(owner, mod, key, each, user, out);
}
KilnResult KILN_CALL t_write_submit(KilnOwner owner, const KilnWorldWriteRequest* request, uint64_t* out_ticket) {
    if (game_in_slice()) {
        if (out_ticket) *out_ticket = 0;
        return KILN_ERR_WRONG_THREAD;
    }
    return write_submit(owner, request, out_ticket);
}
KilnResult KILN_CALL t_write_cancel(uint64_t ticket) { return game_in_slice() ? KILN_ERR_WRONG_THREAD : write_cancel(ticket); }
uintptr_t KILN_CALL t_server_world() { return g_world.load(); }
KilnResult KILN_CALL t_teleport(KilnOwner owner, const KilnTeleportRequest* req, uint64_t* out) {
    return teleport(owner, req, out);
}
KilnResult KILN_CALL t_teleport_cancel(uint64_t id) { return teleport_cancel(id); }

KilnResult KILN_CALL t_status(KilnWorldStatus* out) {
    if (!out || out->size < sizeof(KilnWorldStatus)) return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    const uintptr_t world = g_world.load();
    KilnProgramInfo info{};
    info.size = sizeof info;
    const bool internal_ok = program_info(g_internal, &info) == KILN_OK && info.state == KILN_PROGRAM_ACTIVE;
    out->programs = F.programs;
    out->world = world && g_world_live.load();
    out->teleport = teleport_init() && internal_ok;
    out->find = F.find;
    out->kiln_programs = programs_registered();
    out->engine_programs = engine_program_count(world);
    out->engine_max = L.programs_max > 0 ? static_cast<uint32_t>(L.programs_max) : 0;
    out->teleports_pending = teleports_pending();
    out->world_steps = g_internal_runs.load();
    return KILN_OK;
}

const KilnWorldApi kTable = {
    {sizeof(KilnWorldApi), KILN_WORLD_VERSION},
    &t_register_program,
    &t_program_slot,
    &t_program_info,
    &t_iter_begin,
    &t_iter_next,
    &t_current_entity,
    &t_command_buffer,
    &t_queue_push,
    &t_queue_add,
    &t_queue_remove,
    &t_mark_written,
    &t_read_other,
    &t_component_hash,
    &t_component,
    &t_exists,
    &t_transform,
    &t_position,
    &t_find,
    &t_entity_count,
    &t_server_world,
    &t_teleport,
    &t_teleport_cancel,
    &t_status,
    &write_other,
    &t_unregister_program,
    &t_bind_program,
    &program_capabilities,
    &program_events,
    &program_emit,
    &t_write_submit,
    &t_write_cancel,
    &write_info,
    &write_status,
    &write_declare,
    &game_program_find,
    &game_program_info,
    &game_programs_status,
    &lab_parallel,
    &game_program_wrap,
    &game_program_unwrap,
    &game_wrap_info,
    &game_wraps_release,
    &game_slice_run_original,
    &wrap_original,
    &in_program,
};

void KILN_CALL on_client_pre(const KilnTickInfo* tick, void*) { game_programs_pre(KILN_WORLD_CLIENT, tick); }
void KILN_CALL on_world_event(const KilnWorldEvent* event, void*) { game_programs_world_event(event); }

}  // namespace
}  // namespace kw

extern "C" KILN_EXPORT KilnResult KILN_CALL kiln_plugin_main(const KilnApi* api, KilnOwner owner, KilnPluginInfo* info) {
    using namespace kw;
    if (api->abi_version != KILN_ABI_VERSION || api->size < sizeof(KilnApi)) return KILN_ERR_UNAVAILABLE;
    g_api = api;
    g_owner = owner;
    if (info->size >= offsetof(KilnPluginInfo, on_ready) + sizeof(info->on_ready))
        info->on_ready = [](void*) { boot_programs_init(); };
    info->id = KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1};
    info->version = KilnStr{"4", 1};
    resolve();
    game_programs_resolve();
    wraps_init();
    const bool hooked = programs_init();
    if (!hooked) F.programs = F.teleport = false;
    if (hooked) {
        // kiln.world's own program: no entity query, no access; pushes teleport records and proves the world runs.
        KilnProgramDesc d{};
        d.size = sizeof d;
        d.flags = KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY;
        d.name = KilnStr{"kiln_world", 10};
        if (register_program(owner, &d, &g_internal, true) != KILN_OK) F.teleport = false;
        selftest_init();
    }
    if (api->subscribe_tick(owner, KILN_TICK_PRE, &on_pre, nullptr, &g_tick) != KILN_OK) {
        KW_W("no tick subscription: teleport is off");
        F.teleport = false;
    }
    // The game's own programs: the client world's directory (a game client) and every world's start and stop.
    if (api->size >= offsetof(KilnApi, session) + sizeof(void*)) {
        KilnHandle h = 0;
        if (api->side() == KILN_SIDE_CLIENT)
            api->subscribe_world_tick(owner, KILN_WORLD_CLIENT, KILN_TICK_PRE, &on_client_pre, nullptr, &h);
        api->subscribe_world(owner, &on_world_event, nullptr, &h);
    }
    game_programs_init();
    KW_I("kiln.world: programs %s, entities %s, find %s, names %s (singletons %s, events %s), queue %s, "
         "write %s, read_other %s, teleport %s, dead check %s, tracked writes %s",
         F.programs ? "on" : "off", F.entities ? "on" : "off", F.find ? "on" : "off", F.names ? "on" : "off",
         F.singletons ? "on" : "off", F.events ? "on" : "off", F.queue ? "on" : "off", F.write ? "on" : "off",
         F.read_other ? "on" : "off", F.teleport ? "on" : "off", F.dead ? "on" : "off", writes_available() ? "on" : "off");
    if (!F.entities && !F.programs) return KILN_ERR_UNAVAILABLE;  // nothing works on this build
    return api->register_service(owner, KilnStr{KILN_WORLD_SERVICE, sizeof(KILN_WORLD_SERVICE) - 1}, &kTable);
}
