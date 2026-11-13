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
    int64_t tp_applied = -1, tp_find_ground = -1, tp_target_entity = -1, tp_add_size = -1, tp_remove_size = -1;
    int64_t actor_state = -1;
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

inline uintptr_t registry_of(uintptr_t world) {
    return *reinterpret_cast<const uintptr_t*>(world + L.world_storage);
}
inline uintptr_t storage_of(uintptr_t world) { return world + static_cast<uintptr_t>(L.world_storage); }

// Keen hash map lookups (u32 keys used as-is: components, singletons, events). Bounded; return false when absent.
bool map_find_u16(uintptr_t map, uint32_t key, uint16_t* value);

// ---- guarded calls (seh.cpp; no C++ objects with destructors in those frames) ----
bool seh_each(KilnProgramEachFn fn, KilnProgramCtx* ctx, uint64_t* iter, KilnEntity e, void* user, unsigned long* code);
bool seh_update(KilnProgramUpdateFn fn, KilnProgramCtx* ctx, void* user, unsigned long* code);
bool seh_teleport_cb(KilnTeleportFn fn, const KilnTeleportResult* r, void* user, unsigned long* code);
bool seh_register_program(RegisterProgramFn fn, uintptr_t world, const EngineDesc* d, bool* ok, unsigned long* code);
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

// ---- writes.cpp: tracked writes (kiln.world v3) ----
bool writes_available();
void writes_resolve();
std::vector<std::string> writes_declare(const std::string& who, const std::vector<std::string>& names);  // refused names
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

}  // namespace kw
