// SPDX-License-Identifier: MIT
// kiln.world internals shared by the service's translation units. Engine facts (offsets, signatures) are resolved
// from sigdb/symbols/server.d/world.json; docs/re/world.md has the evidence for each.
#pragma once
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <functional>
#include <vector>

#include "kiln_api.h"
#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace kw {

extern const KilnApi* g_api;
extern KilnOwner g_owner;

void logf(int level, const char* fmt, ...);
#define KW_E(...) ::kw::logf(KILN_LOG_ERROR, __VA_ARGS__)
#define KW_W(...) ::kw::logf(KILN_LOG_WARN, __VA_ARGS__)
#define KW_I(...) ::kw::logf(KILN_LOG_INFO, __VA_ARGS__)
#define KW_D(...) ::kw::logf(KILN_LOG_DEBUG, __VA_ARGS__)

inline KilnStr kstr(std::string_view s) { return KilnStr{s.data(), s.size()}; }
inline std::string_view sv(KilnStr s) { return s.ptr ? std::string_view(s.ptr, s.len) : std::string_view(); }

uint32_t fnv1a(std::string_view s, uint32_t h = 0x811c9dc5u);
uint32_t component_hash(std::string_view name);  // "keen::ecs::" + name unless it contains "::"

// ---- engine types (layouts verified on svn 1285601, see docs/re/world.md) ----
struct NameRef {
    const char* s;
    uint64_t n;
};
struct NameListE {
    const NameRef* p;
    uint64_t n;
};
// keen ECS program descriptor (0x188 bytes); field offsets are checked against sigdb captures at boot.
struct EngineDesc {
    NameRef name;                                                      // 0x000
    void (*update)(void* ctx);                                         // 0x010
    NameListE tpl, tpl_opt, read, read_opt, write, write_opt, add, rem; // 0x018..0x097
    NameListE sys_read;                                                // 0x098
    uint64_t sys_read_opt;                                             // 0x0A8
    NameListE sys_write;                                               // 0x0B0
    uint64_t sys_write_opt, sys_write_parallel;                        // 0x0C0, 0x0C8
    NameListE other_tpl, other_read, other_write, other_write_unser;   // 0x0D0..0x10F
    NameListE with, without, any_of, all_write, all_read;              // 0x110..0x15F
    NameListE events_in, events_out;                                   // 0x160, 0x170
    uint32_t flags;                                                    // 0x180
    uint32_t interval;                                                 // 0x184
};
static_assert(sizeof(EngineDesc) == 0x188);
static_assert(offsetof(EngineDesc, sys_read) == 0x98 && offsetof(EngineDesc, sys_write) == 0xB0);
static_assert(offsetof(EngineDesc, other_tpl) == 0xD0 && offsetof(EngineDesc, with) == 0x110);
static_assert(offsetof(EngineDesc, events_in) == 0x160 && offsetof(EngineDesc, flags) == 0x180);

struct ProgramCtx {  // the engine's ProgramContext
    uint8_t* job;
    uint32_t index;   // entities iterated so far in this slice (iter_next count)
    uint32_t pad;
};
struct Slice {
    const void* ptr;
    uint64_t size;
};

using RegisterProgramFn = bool (*)(uintptr_t world, const EngineDesc* desc, bool disabled);
using RegisterAllProgramsFn = void (*)(uintptr_t world);
using IterBeginFn = void (*)(void* ctx, void* iter, uint32_t size);
using IterNextFn = bool (*)(void* ctx, void* iter, uint32_t size);
using CurrentEntityFn = uint32_t* (*)(void* ctx, uint32_t* out);
using QueuePushFn = void* (*)(void* cb, void* buf, uint8_t kind, uint32_t size, uint32_t entity);
using MarkWrittenFn = void (*)(void* tracker, void* mask_a, void* mask_b, void* entity_rec, uint16_t type_index,
                               uint8_t variant);
using ReadOtherFn = void (*)(Slice* out, void* storage, void* accessor, uint32_t entity);
using WriteOtherFn = Slice* (*)(Slice* out, void* storage, void* tracker, uint64_t accessor, uint32_t entity, uint8_t flag);
using JobTrackerFn = void* (*)(void* job);
using JobWriteFlagFn = uint8_t (*)(void* ctx);
using EmitEventFn = void* (*)(void* buffer, uint32_t hash, uint32_t size, uint16_t program);
using JobProgramIdFn = uint16_t (*)(void* job);
using JobEventBufferFn = void* (*)(void* job);
using ProgramTimeFn = int64_t* (*)(void* ctx, int64_t* out);
using GetComponentFn = void (*)(Slice* out, uintptr_t world, uint32_t hash, uint32_t entity);
using GetEntityTransformFn = const void* (*)(uintptr_t world, uint32_t entity);

struct Fns {
    uintptr_t register_all = 0;
    RegisterProgramFn register_program = nullptr;
    IterBeginFn iter_begin = nullptr;
    IterNextFn iter_next = nullptr;
    CurrentEntityFn current_entity = nullptr;
    QueuePushFn queue_push = nullptr;
    MarkWrittenFn mark_written = nullptr;
    ReadOtherFn read_other = nullptr;
    WriteOtherFn write_other = nullptr;
    JobTrackerFn job_tracker = nullptr;
    JobWriteFlagFn job_write_flag = nullptr;
    EmitEventFn emit_event = nullptr;
    JobProgramIdFn job_program_id = nullptr;
    JobEventBufferFn job_event_buffer = nullptr;
    ProgramTimeFn program_time = nullptr;
    GetComponentFn get_component = nullptr;
    GetEntityTransformFn get_transform = nullptr;
};
extern Fns fn;

// Generic keen hash map field offsets (relative to the map) and a few map bases.
struct MapFields {
    int64_t bits = -1, cap = -1, keys = -1, values = -1, count = -1;
    bool ok() const { return bits >= 0 && cap >= 0 && keys >= 0 && values >= 0 && count >= 0; }
};

struct Layout {
    int64_t world_storage = -1, storage_kind = -1;
    int64_t programs_count = -1, programs_max = -1, programs_table = -1, programs_stride = -1;
    int64_t entry_flags = 0x20, entry_id = 8, may_finalize = -1;
    int64_t job_entity_list = -1, job_entity_count = -1, ctx_index = 8;
    int64_t job_tpl_count = -1, job_read_count = -1, job_write_count = -1, job_singletons = -1,
            singletons_count = -1, job_other_tpl_count = -1, job_other_count = -1, job_events_in_count = -1,
            job_events_out_count = -1;
    int64_t job_cmdbuf = -1, cmdbuf_queue = -1, job_storage = -1;
    int64_t rec_hashes = -1, rec_sizes = -1, rec_data = -1;
    int64_t reg_components = -1, reg_templates = -1, reg_singletons = -1, reg_events = -1;  // map bases in the registry
    MapFields map;
    int64_t storage_entities = -1, entity_layout = -1;
    int64_t entity_id = -1;  // u32 entity id in an entity record (spawn/record.id): a record names the entity it is of
    int64_t tp_applied = -1, tp_find_ground = -1, tp_target_entity = -1, tp_add_size = -1, tp_remove_size = -1;
    int64_t actor_state = -1;
    // the engine's slice record that types/ProgramRunner runs (world.slice_record): its program-table entry, archetype
    // layout and first/end rows
    int64_t slice_entry = -1, slice_layout = -1, slice_first = -1, slice_end = -1;
};
extern Layout L;

struct Features {
    bool programs = false, names = false, singletons = false, events = false, queue = false, write = false,
         read_other = false, write_other = false, entities = false, find = false, teleport = false, dead = false;
};
extern Features F;

// ---- world state ----
extern std::atomic<uintptr_t> g_world;         // server world (set by the program-registration hook)
extern std::atomic<uint64_t> g_internal_runs;  // updates of kiln.world's own program
extern std::atomic<bool> g_world_live;         // our program ran during the previous step
extern thread_local bool t_in_program;         // inside a Kiln program update on this thread

// The one question every KILN_WORLD_STOPPING handler of kiln.world asks (the wrap drain, the client-world programs' stop):
// were this world kind's jobs drained by the engine before STOPPING, or is there only the handler's own bounded wait?
// The core answers it with its capability rows world.stop_drain.server / .client / .prediction (since 0.2.0-dev.29,
// native/core/src/tick_drain.h): available = the core ran the game's WaitWorldJobs on the world (the prediction world:
// on the client world it stops with) before dispatching STOPPING. engine is false for an unavailable row and for a core
// without the rows. text: a few words for a log line or a capability text ("the engine drains the world's jobs first
// (world.stop_drain.client)" / "a bounded wait (2000 ms) only (...)"); wait_ms is the handler's own bound, for the
// words only. Any thread. Call it without kiln.world's own locks held: kiln.caps may call its subscribers on this
// thread.
struct StopDrain {
    bool engine = false;
    std::string text;
};
StopDrain world_stop_drain(uint32_t kind, uint32_t wait_ms);

inline uintptr_t registry_of(uintptr_t world) {
    return *reinterpret_cast<const uintptr_t*>(world + L.world_storage);
}
inline uintptr_t storage_of(uintptr_t world) { return world + static_cast<uintptr_t>(L.world_storage); }

// Keen hash map lookups (u32 keys used as-is: components, singletons, events). Bounded; return false when absent.
bool map_find_u16(uintptr_t map, uint32_t key, uint16_t* value);
// Every key of such a map with its value, in slot order (the server thread, inside a world's setup).
void map_each_u16(uintptr_t map, const std::function<void(uint32_t key, uint16_t value)>& fn);

// ---- guarded calls (seh.cpp; no C++ objects with destructors in those frames) ----
bool seh_each(KilnProgramEachFn fn, KilnProgramCtx* ctx, uint64_t* iter, KilnEntity e, void* user, unsigned long* code);
bool seh_update(KilnProgramUpdateFn fn, KilnProgramCtx* ctx, void* user, unsigned long* code);
bool seh_teleport_cb(KilnTeleportFn fn, const KilnTeleportResult* r, void* user, unsigned long* code);
bool seh_register_program(RegisterProgramFn fn, uintptr_t world, const EngineDesc* d, bool* ok, unsigned long* code);
bool seh_call(void (*fn)(void*), void* ctx, unsigned long* code);  // fn(ctx) under the structured-exception guard
bool seh_iter_begin(void* ctx, void* iter, uint32_t size, unsigned long* code);
bool seh_iter_next(void* ctx, void* iter, uint32_t size, bool* more, unsigned long* code);
bool seh_get_component(uintptr_t world, uint32_t hash, uint32_t entity, Slice* out);
bool seh_get_transform(uintptr_t world, uint32_t entity, const void** out);
bool seh_read_u64(uintptr_t address, uint64_t* out);
bool seh_queue_push(void* cb, void* buf, uint8_t kind, uint32_t size, uint32_t entity, void** out);
extern std::atomic<bool> g_queue_broken;  // QueuePush faulted once: no more pushes (a half-written record may exist)

// ---- programs.cpp ----
bool programs_init();                      // install the RegisterAllPrograms hook
void programs_after_first_step(uintptr_t world);
KilnResult register_program(KilnOwner owner, const KilnProgramDesc* desc, KilnHandle* out, bool internal);
int32_t program_slot(KilnHandle h, int32_t kind, KilnStr name);
KilnResult program_info(KilnHandle h, KilnProgramInfo* out);
uint32_t programs_registered();
uint32_t program_quota();  // kiln.world's own quota of programs (64 since 0.2.0-dev.29)
struct ProgramLedger {
    uint32_t taken = 0, active = 0;  // slots held; of them, in active use
    std::vector<uintptr_t> descs;    // the descriptors of Kiln's programs (sorted): its entries in a world's table
};
ProgramLedger program_ledger();
bool program_budget_register();  // kiln.world.budget (program_budget.cpp), after kiln.world's own table
uint32_t engine_program_count(uintptr_t world);
KilnResult iter_begin(KilnProgramCtx* ctx, uint64_t* iter, uint32_t size);
bool iter_next(KilnProgramCtx* ctx, uint64_t* iter, uint32_t size);
KilnEntity current_entity(KilnProgramCtx* ctx);
void* command_buffer(KilnProgramCtx* ctx);
void* queue_push(KilnProgramCtx* ctx, uint8_t kind, uint32_t size, KilnEntity entity);
KilnResult queue_add(KilnProgramCtx* ctx, KilnEntity entity, uint32_t hash, const void* data, uint32_t size);
KilnResult queue_remove(KilnProgramCtx* ctx, KilnEntity entity, uint32_t hash);
void mark_written(KilnProgramCtx* ctx, const uint64_t* write_record);
const void* read_other(KilnProgramCtx* ctx, uint64_t accessor, KilnEntity entity, uint32_t* out_size);

void* write_other(KilnProgramCtx* ctx, uint64_t accessor, KilnEntity entity, uint32_t* out_size);
KilnResult unregister_program(KilnHandle program);
void programs_reap_owners();
void boot_programs_init();
KilnResult KILN_CALL program_reservation(KilnStr mod, KilnStr key, char* text, uint32_t capacity, uint32_t* out_size);
KilnResult reserve_program(const std::string& mod, const std::string& key, const KilnProgramDesc* desc);
KilnResult KILN_CALL bind_program(KilnOwner owner, KilnStr mod, KilnStr key, KilnProgramEachFn each, void* user, KilnHandle* out);
uint32_t KILN_CALL program_capabilities();
KilnResult KILN_CALL program_events(KilnProgramCtx* ctx, KilnStr name, KilnProgramEventFn callback, void* user);
KilnResult KILN_CALL program_emit(KilnProgramCtx* ctx, KilnStr name, const void* data, uint32_t size);
bool seh_copy(void* out, const void* source, size_t size);
const KilnTypesApi* program_types();  // kiln.types, or null
// A program that asked for it is registered without the other-write names the world does not know (each reported to
// `removed`), instead of being refused whole. Before the first server world registers.
using ProgramPrunedFn = void (*)(std::string_view name);
void program_set_prune(KilnHandle program, ProgramPrunedFn removed);

// ---- programs.cpp: Kiln programs in a client's client world and prediction storage (since 0.2.0-dev.29) ----
// Which worlds a program is for (a boot reservation's "world"): bit (1 << KILN_WORLD_*).
constexpr uint32_t kWorldBitServer = 1u << KILN_WORLD_SERVER, kWorldBitPrediction = 1u << KILN_WORLD_PREDICTION,
                   kWorldBitClient = 1u << KILN_WORLD_CLIENT;
// boot: world.programs.client's inputs (a client, the RegisterAllPrograms hook, the maps, world.lifecycle, the client
// world's tick and world events); reports the row. Before boot_programs_init (its reservations need to know).
void programs_client_init(bool client_tick, bool world_events);
bool programs_client_on();                                // world.programs.client is available
bool in_client_program();  // a Kiln client or prediction program's call runs on this thread (0.2.0-dev.29)
void programs_client_pre(const KilnTickInfo* tick);       // the client world's Pre (main thread)
void programs_world_event(const KilnWorldEvent* event);   // a world starts or stops
KilnResult reserve_program_world(const std::string& mod, const std::string& key, const KilnProgramDesc* desc, uint32_t worlds, uint32_t declared_worlds = 0);
KilnResult KILN_CALL register_program_world(KilnOwner owner, uint32_t kind, const KilnProgramDesc* desc, KilnHandle* out);
KilnResult KILN_CALL program_world_state(KilnHandle program, uint32_t kind, int32_t* state, KilnStr* reason);
KilnResult KILN_CALL bind_program_world(KilnOwner owner, KilnStr mod, KilnStr key, uint32_t kind, KilnProgramEachFn each,
                                        KilnProgramUpdateFn update, void* user, KilnHandle* out);
KilnResult KILN_CALL unbind_program_world(KilnHandle binding);

// ---- writes.cpp: tracked writes (kiln.world v3) ----
bool writes_available();
void writes_resolve();
std::vector<std::string> writes_declare(const std::string& who, const std::vector<std::string>& names);  // refused names
void writes_world_changing();               // a new server world, before g_world names it
void writes_refuse_list(const std::string& mod, const std::string& why);  // boot: the mod's "writes" was not read
void writes_before_world(uintptr_t world);  // server thread, before Kiln's programs register into a new server world
void writes_after_world();                  // after they registered
void writes_pre(const KilnTickInfo* tick);  // tick thread, Pre
uint32_t writes_capability();
KilnResult KILN_CALL write_submit(KilnOwner owner, const KilnWorldWriteRequest* request, uint64_t* out_ticket);
KilnResult KILN_CALL write_cancel(uint64_t ticket);
KilnResult KILN_CALL write_info(KilnStr mod_id, uint32_t component, KilnWorldWriteInfo* out);
KilnResult KILN_CALL write_status(KilnWorldWriteStatus* out);
KilnResult KILN_CALL write_declare(KilnOwner owner, KilnStr who, const KilnStr* names, uint32_t count);
bool seh_read_other(Slice* out, void* storage, void* accessor, uint32_t entity, unsigned long* code);
bool seh_write_other(Slice* out, void* storage, ProgramCtx* ctx, uint64_t accessor, uint32_t entity, unsigned long* code);
bool seh_attr_changed(void (*fn)(void*, void*), void* component, void* registry, unsigned long* code);
bool seh_write_change(KilnWorldChangeFn fn, uint32_t entity, void* data, uint32_t size, void* user, bool* accepted,
                      unsigned long* code);
bool seh_write_cb(KilnWorldWriteFn fn, const KilnWorldWriteResult* result, void* user, unsigned long* code);

// ---- ecs_prod.cpp: verified-copy ECS reads (T8, ecs_reads.cpp) ----
void ecs_prod_init();       // boot: exact-profile flag and the world.ecs_read capability row
bool ecs_prod_available();  // entity-table reads validated on this build (none yet)
uint32_t ecs_capability();  // KILN_WORLD_CAP_ECS_READ or 0
KilnResult KILN_CALL t_entity_open(KilnOwner owner, uint64_t reload_epoch, uint32_t world_kind,
                                   uint64_t expected_session, KilnEntity entity, KilnObjectRef* out);
KilnResult KILN_CALL t_entity_validate(KilnOwner owner, const KilnObjectRef* ref, KilnObjectInfo* out);
KilnResult KILN_CALL t_ecs_context_snapshot(KilnEcsContextInfo* out);
KilnResult KILN_CALL t_ecs_query(KilnOwner owner, const KilnEcsQueryRequest* request, KilnObjectRef* out,
                                 uint32_t capacity, uint32_t* required, uint32_t* state);
KilnResult KILN_CALL t_ecs_snapshot(KilnOwner owner, const KilnObjectRef* ref, uint32_t component, void* out,
                                    uint32_t capacity, uint32_t* required);
KilnResult KILN_CALL t_ecs_snapshot_many(KilnOwner owner, const KilnObjectRef* ref,
                                         const KilnEcsSnapshotSpec* specs, uint32_t count, void* out,
                                         uint32_t capacity, uint32_t* required, uint32_t* presence);
KilnResult KILN_CALL t_ecs_template_uuid(KilnOwner owner, const KilnObjectRef* ref, uint8_t guid[16]);

// ---- entities.cpp ----
bool may_touch_world();  // tick thread or inside a program
const void* component(KilnEntity entity, uint32_t hash, uint32_t* out_size);
bool exists(KilnEntity entity);
KilnResult transform(KilnEntity entity, KilnTransform* out);
KilnResult position(KilnEntity entity, KilnVec3* out);
KilnResult find(uint32_t hash, KilnEntity* out, uint32_t capacity, uint32_t* out_count);
uint32_t entity_count();
inline double fixed_to_m(int64_t v) { return static_cast<double>(v) / 4294967296.0; }
int64_t m_to_fixed(double m);

// ---- teleport.cpp ----
bool teleport_init();
void teleport_pre(const KilnTickInfo* tick);       // tick thread, Pre
void teleport_in_program(KilnProgramCtx* ctx);      // kiln.world's own program update (worker thread)
KilnResult teleport(KilnOwner owner, const KilnTeleportRequest* req, uint64_t* out);
KilnResult teleport_cancel(uint64_t id);
uint32_t teleports_pending();
KilnResult teleport_force_stuck(KilnEntity entity, KilnTeleportFn cb, void* user);  // self-test only

// ---- selftest.cpp (labs: <kiln>/data/kiln.world/selftest) ----
void selftest_init();
void selftest_pre(const KilnTickInfo* tick);

// ---- game_programs.cpp: the game's own programs per world kind (v4, since 0.2.0-dev.28) ----
void game_programs_resolve();                                  // boot, after resolve()
void game_programs_init();                                     // boot: capability rows
void game_programs_pre(uint32_t kind, const KilnTickInfo* tick);  // that world's thread, Pre
void game_programs_world_event(const KilnWorldEvent* event);   // world started / stopping
uint32_t game_programs_world_tid(uint32_t kind);               // the thread of that world's last Pre
uint64_t game_programs_step(uint32_t kind);                    // that world's step at its last Pre
int32_t KILN_CALL game_program_find(uint32_t kind, KilnStr name);
KilnResult KILN_CALL game_program_info(uint32_t kind, int32_t index, KilnGameProgramInfo* out);
KilnResult KILN_CALL game_programs_status(KilnGameProgramsStatus* out);
KilnResult KILN_CALL lab_parallel(uint32_t on, uint32_t steps);
void game_programs_test_hold_directory_lock(bool hold);  // tests: hold the lock a directory read takes
uint64_t game_programs_test_directory_bypasses();
void game_programs_test_setup(uintptr_t image_lo, uintptr_t image_hi, bool table_ok, int64_t players_mask,
                              bool switch_enabled);  // host tests only

}  // namespace kw
