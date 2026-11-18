// SPDX-License-Identifier: MIT
// Kiln programs in a game client's client world and prediction storage (chapter 14, since 0.2.0-dev.29), on the real
// programs.cpp against a fake engine that models three worlds in one process: a host's server world (kind 0), its client
// world (kind 2) and the prediction storage (kind 1, which runs the client world's table), each with its own name maps
// and overlapping entity ids, a second client world after the first stopped, and slices on several threads while the
// main thread binds, unbinds and stops a world. No game, no Windows.
#include "../programs.cpp"
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <thread>

#include "nlohmann/json.hpp"

// The four members appended in 0.2.0-dev.29, after game_program_contract (440): tests/abi.json says the same.
static_assert(offsetof(KilnWorldApi, register_program_world) == 496 && offsetof(KilnWorldApi, program_world_state) == 504 &&
              offsetof(KilnWorldApi, bind_program_world) == 512 && offsetof(KilnWorldApi, unbind_program_world) == 520 &&
              offsetof(KilnWorldApi, entity_open) == 528 && sizeof(KilnWorldApi) == 616); // the ECS reads append at 528, the writes at 584

namespace kw {
const KilnApi* g_api;
KilnOwner g_owner = 1;
Fns fn;
Layout L;
Features F;
std::atomic<uintptr_t> g_world{0};
std::atomic<uint64_t> g_internal_runs{0};
std::atomic<bool> g_world_live{false};
thread_local bool t_in_program = false;
std::vector<std::string> logged;
std::mutex log_mu;
void logf(int, const char* fmt, ...) {
    char b[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(b, sizeof b, fmt, ap);
    va_end(ap);
    std::lock_guard lock(log_mu);
    logged.emplace_back(b);
}
uint32_t fnv1a(std::string_view value, uint32_t hash) {
    for (unsigned char c : value) hash = (hash ^ c) * 16777619u;
    return hash;
}
uint32_t component_hash(std::string_view value) { return fnv1a(value, fnv1a("keen::ecs::")); }
// Each world's maps: keyed by the map's address (registry + the map's offset), so two worlds know different names.
std::map<std::pair<uintptr_t, uint32_t>, uint16_t> maps;
bool map_find_u16(uintptr_t map, uint32_t key, uint16_t* value) {
    const auto at = maps.find({map, key});
    if (at == maps.end()) return false;
    if (value) *value = at->second;
    return true;
}
void map_each_u16(uintptr_t map, const std::function<void(uint32_t, uint16_t)>& fn) {
    for (const auto& [k, v] : maps)
        if (k.first == map) fn(k.second, v);
}
// the core's answer about a client world's jobs at its stop (wraps.cpp's world_stop_drain in the service)
bool g_engine_drain = false;
StopDrain world_stop_drain(uint32_t, uint32_t wait_ms) {
    StopDrain d;
    d.engine = g_engine_drain;
    d.text = g_engine_drain ? "the engine drains the world's jobs first (world.stop_drain.client)"
                            : "a bounded wait (" + std::to_string(wait_ms) + " ms) only: this core has no world.stop_drain.client row";
    return d;
}
void teleport_in_program(KilnProgramCtx*) {}
void writes_world_changing() {}
void writes_before_world(uintptr_t) {}
void writes_after_world() {}
uint32_t writes_capability() { return 0; }
uint32_t ecs_capability() { return 0; }
thread_local bool t_in_stage = false;
bool game_in_slice() { return t_in_stage; }
uint32_t current_thread_id() { return static_cast<uint32_t>(std::hash<std::thread::id>{}(std::this_thread::get_id()) | 1u); }
KilnProgramEachFn g_faulting = nullptr;  // the fake SEH answers a fault for this callback, as for an access violation
KilnProgramEachFn g_faulting2 = nullptr; // ... and for this one (it runs first: a call that faults on its way out)
bool seh_each(KilnProgramEachFn f, KilnProgramCtx* ctx, uint64_t* iter, KilnEntity e, void* user, unsigned long* code) {
    if (!f) return *code = 0xC0000005, false;
    f(ctx, iter, e, user);
    if (f == g_faulting || f == g_faulting2) return *code = 0xC0000005, false;
    return true;
}
bool seh_update(KilnProgramUpdateFn f, KilnProgramCtx* ctx, void* user, unsigned long*) {
    f(ctx, user);
    return true;
}
// The fake engine misbehaves on demand: faults inside RegisterProgram for one program name, or adds two entries for one
// (a table that does not add up afterwards). seh_call fails once when asked (a raw read that faulted).
std::string engine_fault_name, engine_double_name;
int engine_calls = 0;
bool fail_seh_call = false;
bool seh_call(void (*f)(void*), void* c, unsigned long* code) {
    if (fail_seh_call) {
        fail_seh_call = false;
        *code = 0xC0000005;
        return false;
    }
    f(c);
    return true;
}
bool seh_register_program(RegisterProgramFn, uintptr_t world, const EngineDesc* d, bool* ok, unsigned long* code) {
    ++engine_calls;
    auto* count = reinterpret_cast<uint16_t*>(world + L.programs_count);
    const std::string name(d->name.s, d->name.n);
    if (name == engine_fault_name) return *code = 0xC0000005, false;
    if (name == engine_double_name) ++*count;
    if (*count >= L.programs_max) return *ok = false, true;
    const uintptr_t entry = world + L.programs_table + static_cast<uintptr_t>(*count) * L.programs_stride;
    std::memcpy(reinterpret_cast<void*>(entry), &d, 8);
    const uint16_t id = static_cast<uint16_t>(*count + 1);
    std::memcpy(reinterpret_cast<void*>(entry + L.entry_id), &id, 2);
    std::memcpy(reinterpret_cast<void*>(entry + L.entry_flags), &d->flags, 4);
    ++*count;
    return *ok = true, true;
}
bool seh_iter_begin(void* ctx, void* iter, uint32_t size, unsigned long*) {
    fn.iter_begin(ctx, iter, size);
    return true;
}
bool seh_iter_next(void* ctx, void* iter, uint32_t size, bool* more, unsigned long*) {
    *more = fn.iter_next(ctx, iter, size);
    return true;
}
bool seh_read_u64(uintptr_t address, uint64_t* value) {
    std::memcpy(value, reinterpret_cast<void*>(address), 8);
    return true;
}
bool seh_queue_push(void*, void*, uint8_t, uint32_t, uint32_t, void**) { return false; }
const void* fail_copy_from = nullptr;  // the fake guard answers a fault for a read of this address
bool seh_copy(void* to, const void* from, size_t size) {
    if (from && from == fail_copy_from) return false;
    std::memcpy(to, from, size);
    return true;
}
}  // namespace kw

using namespace kw;

namespace {

int checks = 0, failures = 0;
void check(bool ok, const char* message) {
    ++checks;
    if (!ok) {
        ++failures;
        std::fprintf(stderr, "FAIL %s\n", message);
    }
}
bool logged_has(const char* text) {
    std::lock_guard lock(log_mu);
    for (const auto& l : logged)
        if (l.find(text) != std::string::npos) return true;
    return false;
}

// ---- the fake process: worlds with a program table, storages with a kind and maps, jobs for slices ----
constexpr int64_t kStorage = 0x100, kKind = 0x8, kCount = 0x40, kTable = 0x200, kStride = 0x30;
constexpr size_t kWorldBytes = kTable + 896 * kStride + 0x100;

struct FakeWorld {
    std::vector<uint8_t> mem = std::vector<uint8_t>(kWorldBytes, 0);
    std::vector<uint8_t> registry = std::vector<uint8_t>(0x100, 0);
    uintptr_t at() { return reinterpret_cast<uintptr_t>(mem.data()); }
    uint8_t* storage() { return mem.data() + kStorage; }
    uint16_t& count() { return *reinterpret_cast<uint16_t*>(mem.data() + kCount); }
    void make(uint8_t kind, uint16_t game_programs) {
        std::fill(mem.begin(), mem.end(), 0);
        const uintptr_t reg = reinterpret_cast<uintptr_t>(registry.data());
        std::memcpy(storage(), &reg, 8);  // storage[0]: the registry
        storage()[kKind] = kind;
        count() = game_programs;
    }
    uintptr_t map(MapKind k) { return map_base(reinterpret_cast<uintptr_t>(registry.data()), k); }
    void know(const char* name, uint16_t index) { maps[{map(M_DYN), fnv1a(name, fnv1a("keen::ecs::"))}] = index; }
    void know_event(const char* name, uint16_t index) { maps[{map(M_EVENT), fnv1a(name, fnv1a("keen::ecs::"))}] = index; }
    uintptr_t entry_desc(uint16_t index) {
        uintptr_t d = 0;
        std::memcpy(&d, mem.data() + kTable + index * kStride, 8);
        return d;
    }
};

// A storage of its own for the prediction storage (it runs the client world's table, has no table of its own).
struct FakeStorage {
    std::vector<uint8_t> mem = std::vector<uint8_t>(0x100, 0);
    explicit FakeStorage(uint8_t kind) { mem[kKind] = kind; }
};

// One slice: job+0 its storage, then the counts the engine gives a program's iterator (all 0: a SYSTEM program without
// lists; read count 1: an entity program reading one component), the entity count and its ids.
struct Job {
    alignas(16) uint8_t bytes[0x100]{};
    std::vector<uint32_t> ids;
    Job(const uint8_t* storage, std::vector<uint32_t> entities, uint16_t reads = 0) : ids(std::move(entities)) {
        std::memcpy(bytes, &storage, 8);
        std::memcpy(bytes + 0x20, &reads, 2);
        const uint64_t n = ids.size();
        std::memcpy(bytes + 0x60, &n, 8);
    }
};

void slice(Program* p, Job& job) {
    ProgramCtx ctx{job.bytes, 0, 0};
    p->desc.update(&ctx);  // the trampoline the engine calls for both tables (one per program)
}

// Who ran, where: a callback counts its calls per storage kind and the entities it saw.
struct Probe {
    std::atomic<int> calls[3]{};
    std::atomic<int> after_close{0};
    std::atomic<bool> closed{false};
    std::mutex mu;
    std::vector<std::pair<uint32_t, uint32_t>> seen;  // (kind, entity)
    std::atomic<int> hold_ms{0};
    std::atomic<bool> inside{false};
};
uint32_t kind_of(KilnProgramCtx* ctx) {
    const uint8_t* storage = nullptr;
    std::memcpy(&storage, reinterpret_cast<ProgramCtx*>(ctx)->job, 8);
    return storage[kKind];
}
void KILN_CALL probe_each(KilnProgramCtx* ctx, uint64_t*, KilnEntity e, void* user) {
    auto* p = static_cast<Probe*>(user);
    const uint32_t k = kind_of(ctx);
    p->calls[k].fetch_add(1);
    if (p->closed.load()) p->after_close.fetch_add(1);
    if (e) {
        std::lock_guard lock(p->mu);
        p->seen.emplace_back(k, e);
    }
    if (const int ms = p->hold_ms.load()) {
        p->inside = true;
        std::this_thread::sleep_for(std::chrono::milliseconds(ms));
        p->inside = false;
    }
}
void KILN_CALL fault_each(KilnProgramCtx*, uint64_t*, KilnEntity, void*) {}
void KILN_CALL slow_fault_each(KilnProgramCtx* ctx, uint64_t* it, KilnEntity e, void* user) { probe_each(ctx, it, e, user); }

std::vector<std::string> g_names;
KilnProgramDesc system_desc(const std::string& name, uint32_t flags = KILN_PROGRAM_SYSTEM) {
    g_names.push_back(name);
    KilnProgramDesc d{};
    d.size = sizeof d;
    d.flags = flags;
    d.name = kstr(g_names.back());
    d.each = [](KilnProgramCtx*, uint64_t*, KilnEntity, void*) {};
    return d;
}

int state_of(KilnHandle h, uint32_t kind, std::string* why = nullptr) {
    int32_t st = -1;
    KilnStr r{};
    if (program_world_state(h, kind, &st, &r) != KILN_OK) return -1;
    if (why) *why = std::string(sv(r));
    return st;
}

void reset_programs() {
    for (Program* p : g_programs) delete p;
    g_programs.clear();
    std::fill(std::begin(g_by_slot), std::end(g_by_slot), nullptr);
    g_side_programs = 0;
    g_closing = 0;
    g_table_live = false;
    g_table_stopping = false;
    g_registered_once = false;
}

void world_event(uint32_t event, uint32_t kind, uintptr_t world) {
    KilnWorldEvent e{};
    e.size = sizeof e;
    e.event = event;
    e.kind = kind;
    e.world = world;
    programs_world_event(&e);
}
void client_stops(uintptr_t world) { world_event(KILN_WORLD_STOPPING, KILN_WORLD_CLIENT, world); }

// The fake core: one main thread; post_world refuses a revoked owner; the side and features of a game client.
std::thread::id g_main;
// kiln.caps' report, recorded: the last report of world.programs.client
struct Row {
    uint32_t state = 99, reason = 0;
    std::string text;
    int reports = 0;
} g_row;
const KilnCapsApi* caps_fake() {
    static KilnCapsApi caps{};
    caps.header.size = sizeof caps;
    caps.report = [](KilnOwner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) -> KilnResult {
        if (sv(name) == "world.programs.client") g_row = {state, reason, std::string(sv(text)), g_row.reports + 1};
        return KILN_OK;
    };
    return &caps;
}
void unlatch() {  // tests only: the latch is for the process
    g_latched = false;
    g_latch_reason = nullptr;
    g_latch_unreported = false;
    g_table_stopping = false;
}
int log_count(const char* text) {
    std::lock_guard lock(log_mu);
    int n = 0;
    for (const auto& l : logged) n += l.find(text) != std::string::npos;
    return n;
}
uintptr_t g_client_world = 0;  // what the fake core's world(KILN_WORLD_CLIENT) answers
std::atomic<int> g_revoked{0};
uint32_t g_side = KILN_SIDE_CLIENT;
}  // namespace

int main(int argc, char** argv) {
    if (argc > 1) {  // tests/abi.json's entry for the members appended in 0.2.0-dev.29
        std::ifstream in(argv[1]);
        const auto fx = nlohmann::json::parse(in).at("KilnWorldApiClientPrograms");
        nlohmann::json fields = nlohmann::json::array();
        auto field = [&](const char* name, size_t at) { fields.push_back(nlohmann::json::array({name, at})); };
        field("register_program_world", offsetof(KilnWorldApi, register_program_world));
        field("program_world_state", offsetof(KilnWorldApi, program_world_state));
        field("bind_program_world", offsetof(KilnWorldApi, bind_program_world));
        field("unbind_program_world", offsetof(KilnWorldApi, unbind_program_world));
        nlohmann::json want = nlohmann::json::object();
        want["size"] = offsetof(KilnWorldApi, unbind_program_world) + sizeof(void*); // this section's end (528); the ECS reads append after it
        want["fields"] = fields;
        check(fx == want, "abi.json: KilnWorldApiClientPrograms field for field (496, 504, 512, 520; 528 bytes)");
        check(fx.at("fields")[0][1] == offsetof(KilnWorldApi, foreign_attr_changed) + sizeof(void*),
              "the first appended member follows the foreign views (06c), the table's previous end");
    }
    KilnApi api{};
    api.size = sizeof api;
    api.on_tick_thread = [] { return std::this_thread::get_id() == g_main; };
    api.post = [](KilnOwner owner, KilnTaskFn, void*) -> KilnResult { return owner == 99 ? KILN_ERR_REVOKED : KILN_OK; };
    api.post_world = [](KilnOwner owner, uint32_t, KilnTaskFn, void*) -> KilnResult {
        return static_cast<int>(owner) == g_revoked.load() ? KILN_ERR_REVOKED : KILN_OK;
    };
    api.on_world_thread = [](uint32_t) { return std::this_thread::get_id() == g_main; };
    api.side = [] { return g_side; };
    api.feature = [](KilnStr n) { return sv(n) == "world.lifecycle"; };
    api.service = [](KilnStr name, uint32_t) -> const void* { return sv(name) == KILN_CAPS_SERVICE ? caps_fake() : nullptr; };
    api.world = [](uint32_t kind) -> uintptr_t { return kind == KILN_WORLD_CLIENT ? g_client_world : 0; };
    g_api = &api;
    g_faulting = &fault_each;
    g_faulting2 = &slow_fault_each;
    g_main = std::this_thread::get_id();

    F.programs = F.queue = F.write = F.read_other = F.names = F.singletons = F.events = true;
    L.world_storage = kStorage;
    L.storage_kind = kKind;
    L.programs_count = kCount;
    L.programs_table = kTable;
    L.programs_stride = kStride;
    L.programs_max = 896;
    L.reg_components = 8, L.reg_templates = 16, L.reg_singletons = 24, L.reg_events = 32;
    L.job_tpl_count = 0x10, L.job_read_count = 0x20, L.job_write_count = 0x22, L.job_singletons = 0x28,
    L.singletons_count = 0, L.job_other_tpl_count = 0x30, L.job_other_count = 0x31, L.job_events_in_count = 0x38,
    L.job_events_out_count = 0x40, L.job_entity_count = 0x60;
    fn.register_all = 0x1000;
    fn.iter_begin = [](void* ctx, void*, uint32_t) { static_cast<ProgramCtx*>(ctx)->index = 0; };
    fn.iter_next = [](void* c, void*, uint32_t) {
        auto* ctx = static_cast<ProgramCtx*>(c);
        uint64_t n = 0;
        std::memcpy(&n, ctx->job + 0x60, 8);
        if (ctx->index >= n) return false;
        ++ctx->index;
        return true;
    };
    fn.current_entity = [](void* c, uint32_t* out) {
        auto* ctx = static_cast<ProgramCtx*>(c);
        auto* job = reinterpret_cast<Job*>(ctx->job);  // bytes is Job's first member
        *out = job->ids[ctx->index - 1];
        return out;
    };
    check(programs_init(), "the RegisterAllPrograms link installs (the existing hook, no new one)");

    // ---- off: a dedicated server, and a client before the row's inputs ----
    g_side = KILN_SIDE_SERVER;
    programs_client_init(true, true);
    check(!programs_client_on(), "a dedicated server: world.programs.client is off (wrong side)");
    KilnProgramDesc early = system_desc("cw_early");
    KilnHandle h = 0;
    check(register_program_world(5, KILN_WORLD_CLIENT, &early, &h) == KILN_ERR_UNAVAILABLE && h == 0,
          "register_program_world(CLIENT) on a dedicated server: KILN_ERR_UNAVAILABLE");
    g_side = KILN_SIDE_CLIENT;
    programs_client_init(true, false);
    check(!programs_client_on(), "a client whose world events cannot be had: off (faulted)");
    api.feature = [](KilnStr) { return false; };
    programs_client_init(true, true);
    check(!programs_client_on(), "a client without world.lifecycle: off (a client world's stop would not be seen)");
    api.feature = [](KilnStr n) { return sv(n) == "world.lifecycle"; };
    F.names = false;
    programs_client_init(true, true);
    check(!programs_client_on(), "a client without the name maps: off (a client world's names could not be checked)");
    F.names = true;
    programs_client_init(true, true);
    check(programs_client_on(), "a client with every input: world.programs.client is available");

    // ---- a player whose mods declare no client program: the client world's RegisterAllPrograms registers nothing ----
    FakeWorld server, client, client2;
    FakeStorage prediction(KILN_WORLD_PREDICTION);
    server.make(KILN_WORLD_SERVER, 758);
    client.make(KILN_WORLD_CLIENT, 817);
    KilnProgramDesc only_server = system_desc("cw_server_only", KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY);
    Probe server_only;
    only_server.each = &probe_each;
    only_server.user = &server_only;
    KilnHandle hs = 0;
    check(register_program(7, &only_server, &hs, false) == KILN_OK, "a server program is accepted as before");
    on_world(client.at());
    check(client.count() == 817 && logged_has("RegisterAllPrograms for a world of kind 2: not the server world; no Kiln programs there"),
          "no client program: the client world gets nothing and kiln.log keeps its old line");
    programs_client_pre(nullptr);  // returns at once: g_side_programs is 0
    check(g_side_programs.load() == 0, "no client program: the client Pre has nothing to do");
    // The server path: a server program's slice in a storage whose kind byte reads 2 still runs as the server's (its
    // trampoline never reads the storage kind: the server branch is the one it always was).
    on_world(server.at());
    Job weird(client.storage(), {});
    slice(g_programs[0], weird);
    check(server_only.calls[KILN_WORLD_CLIENT].load() == 1 && state_of(hs, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE,
          "a server-only program's dispatch does not look at the storage kind (unchanged server path)");
    check(state_of(hs, KILN_WORLD_CLIENT) == -1, "program_world_state: a server program is not for the client world (NOT_FOUND)");
    reset_programs();

    // ---- registration: flags, HOST_ONLY, both, refusal per world, one slot ----
    server.make(KILN_WORLD_SERVER, 758);
    client.make(KILN_WORLD_CLIENT, 817);
    KilnProgramDesc host_only = system_desc("cw_host_only", KILN_PROGRAM_SYSTEM | KILN_PROGRAM_HOST_ONLY);
    check(register_program_world(5, KILN_WORLD_CLIENT, &host_only, &h) == KILN_ERR_INVALID_ARGUMENT && h == 0,
          "HOST_ONLY is refused for the client world");
    check(register_program_world(5, KILN_WORLD_PREDICTION, &host_only, &h) == KILN_ERR_INVALID_ARGUMENT,
          "HOST_ONLY is refused for the prediction storage");
    check(register_program_world(5, 7, &host_only, &h) == KILN_ERR_INVALID_ARGUMENT, "a bad world kind is refused");
    t_in_stage = true;
    check(register_program_world(5, KILN_WORLD_CLIENT, &early, &h) == KILN_ERR_WRONG_THREAD, "inside a wrap stage: wrong thread");
    t_in_stage = false;

    // Reservations the boot made from a mod's kiln.programs.json: "both", "client", "server", "prediction", and two more.
    server.know("Health", 20);
    server.know("ServerThing", 30);
    client.know("Health", 20);  // the client world knows Health, not ServerThing
    client.know("RenderTransform", 40);
    client2.make(KILN_WORLD_CLIENT, 817);
    client2.know("Health", 20);  // a later client world of the same build: its own maps, the same names
    client2.know("RenderTransform", 40);
    KilnProgramDesc both = system_desc("kiln_managed_both");
    KilnProgramDesc only_client = system_desc("kiln_managed_client");
    KilnProgramDesc only_srv = system_desc("kiln_managed_server");
    KilnProgramDesc predicted = system_desc("kiln_managed_prediction");
    const KilnStr thing[] = {kstr("ServerThing")};
    KilnProgramDesc lacking = system_desc("kiln_managed_lacking");
    lacking.other_read = KilnNames{thing, 1, 0};
    const KilnStr health[] = {kstr("Health")};
    KilnProgramDesc entity = system_desc("kiln_managed_entity", 0);
    entity.read = KilnNames{health, 1, 0};
    check(reserve_program_world("writer", "both", &both, kWorldBitServer | kWorldBitClient) == KILN_OK, "a both reservation");
    check(reserve_program_world("writer", "client", &only_client, kWorldBitClient) == KILN_OK, "a client reservation");
    check(reserve_program_world("writer", "server", &only_srv, kWorldBitServer) == KILN_OK, "a server reservation");
    check(reserve_program_world("writer", "prediction", &predicted, kWorldBitPrediction) == KILN_OK, "a prediction reservation");
    check(reserve_program_world("writer", "lacking", &lacking, kWorldBitServer | kWorldBitClient) == KILN_OK,
          "a both reservation naming a component only the server world has");
    check(reserve_program_world("writer", "entity", &entity, kWorldBitServer | kWorldBitClient) == KILN_OK,
          "an entity program for both worlds");
    // events: a name only the server world's event map has, and one only the client world's has (question 4)
    server.know_event("ServerEvent", 3);
    client.know_event("ClientEvent", 5);
    client2.know_event("ClientEvent", 5);
    const KilnStr server_event[] = {kstr("ServerEvent")}, client_event[] = {kstr("ClientEvent")};
    KilnProgramDesc on_server_event = system_desc("kiln_managed_events");
    on_server_event.events_in = KilnNames{server_event, 1, 0};
    KilnProgramDesc on_client_event = system_desc("kiln_managed_client_events");
    on_client_event.events_in = KilnNames{client_event, 1, 0};
    check(reserve_program_world("writer", "events", &on_server_event, kWorldBitServer | kWorldBitClient) == KILN_OK &&
              reserve_program_world("writer", "client_events", &on_client_event, kWorldBitClient) == KILN_OK,
          "two event programs");
    check(programs_registered() == 8 && g_side_programs.load() == 7, "eight reservations take eight slots; seven have a client-table side");
    Program* p_both = g_programs[0];
    Program* p_client = g_programs[1];
    Program* p_pred = g_programs[3];
    Program* p_lack = g_programs[4];
    Program* p_entity = g_programs[5];
    check(p_both->side[KILN_WORLD_CLIENT].desc.update == p_both->desc.update && p_both->desc.update == kTrampolines[0],
          "\"both\" costs one slot: one trampoline behind both tables' descriptors");
    check(p_both->desc.flags == KILN_PROGRAM_SYSTEM && p_both->side[KILN_WORLD_CLIENT].desc.flags == (KILN_PROGRAM_SYSTEM | 0x1u),
          "the client world's descriptor has the client flag 0x1 ORed in; the server's has none");
    check(p_pred->side[KILN_WORLD_PREDICTION].desc.flags == KILN_PROGRAM_SYSTEM, "a prediction descriptor has none of 0x1, 0x10, 0x40");
    std::string why;
    check(state_of(1, KILN_WORLD_CLIENT, &why) == KILN_PROGRAM_PENDING && why == "waiting for a client world",
          "before any client world: PENDING, \"waiting for a client world\"");

    // Binding: (mod, key, world) is the scope; the server and client callbacks of a "both" program are independent.
    Probe srv_probe, cli_probe, pred_probe, ent_srv, ent_cli;
    KilnHandle bs = 0, bc = 0, bp = 0, bx = 0;
    check(bind_program(10, kstr("writer"), kstr("client"), &probe_each, &srv_probe, &bx) == KILN_ERR_NOT_FOUND && bx == 0,
          "bind_program (the server world's) does not take a client-only reservation");
    check(bind_program_world(10, kstr("writer"), kstr("server"), KILN_WORLD_CLIENT, &probe_each, nullptr, &cli_probe, &bx) ==
              KILN_ERR_NOT_FOUND, "bind_program_world(CLIENT) does not take a server-only reservation");
    check(bind_program(10, kstr("writer"), kstr("both"), &probe_each, &srv_probe, &bs) == KILN_OK, "the server side of both binds");
    check(bind_program_world(11, kstr("writer"), kstr("both"), KILN_WORLD_CLIENT, &probe_each, nullptr, &cli_probe, &bc) == KILN_OK &&
              bc != bs, "the client side of both binds on its own (another owner, another handle)");
    check(bind_program_world(11, kstr("writer"), kstr("both"), KILN_WORLD_CLIENT, &probe_each, nullptr, &cli_probe, &bx) ==
              KILN_ERR_INVALID_ARGUMENT, "a second binding of the same side is refused");
    check(bind_program_world(11, kstr("writer"), kstr("prediction"), KILN_WORLD_PREDICTION, &probe_each, nullptr, &pred_probe, &bp) ==
              KILN_OK, "the prediction side binds");
    check(bind_program(10, kstr("writer"), kstr("entity"), &probe_each, &ent_srv, &bx) == KILN_OK &&
              bind_program_world(10, kstr("writer"), kstr("entity"), KILN_WORLD_CLIENT, &probe_each, nullptr, &ent_cli, &bx) == KILN_OK,
          "both sides of the entity program bind");
    KilnResult off_thread = KILN_OK;
    std::thread([&] {
        off_thread = bind_program_world(11, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &cli_probe, &bx);
    }).join();
    check(off_thread == KILN_ERR_WRONG_THREAD, "bind_program_world(CLIENT) off the main thread: KILN_ERR_WRONG_THREAD");
    check(bind_program_world(11, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, nullptr, nullptr, &cli_probe, &bx) ==
              KILN_ERR_INVALID_ARGUMENT, "neither each nor update: refused");
    g_revoked = 12;
    check(bind_program_world(12, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &cli_probe, &bx) ==
              KILN_ERR_REVOKED, "a revoked owner cannot bind");
    g_revoked = 0;

    // The host's worlds register: the server world first, then the client world, each against its own maps.
    on_world(server.at());
    check(state_of(1, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE && state_of(5, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE &&
              server.count() == 758 + 5, "the server world registers the five server sides, no client or prediction one");
    on_world(client.at());
    g_client_world = client.at();
    check(state_of(1, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE && state_of(2, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE &&
              state_of(4, KILN_WORLD_PREDICTION) == KILN_PROGRAM_ACTIVE && state_of(6, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE,
          "the client world registers the client and prediction sides into its table");
    check(state_of(5, KILN_WORLD_CLIENT, &why) == KILN_PROGRAM_REFUSED && why.find("ServerThing") != std::string::npos &&
              state_of(5, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE,
          "a name on the server map and not on the client map is refused for the client world only, with the name");
    check(logged_has("program kiln_managed_client registered into the client world as #"),
          "kiln.log: \"program <id> registered into the client world as #N\" (the guided check's line)");
    check(client.count() == 817 + 5 && client.entry_desc(817) == reinterpret_cast<uintptr_t>(&p_both->side[KILN_WORLD_CLIENT].desc),
          "the client table got five entries after the game's 817, each pointing at its client-side descriptor");
    check(state_of(7, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE && state_of(7, KILN_WORLD_CLIENT, &why) == KILN_PROGRAM_REFUSED &&
              why == "events_in: unknown event 'ServerEvent'",
          "a server-only event name is refused for the client world only, with the name");
    check(state_of(8, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE, "an event the client world has: registered there");
    programs_client_pre(nullptr);  // the client world's first Pre: its event map into kiln.log (question 4)
    check(logged_has("event map (question 4): 1 event types [1/1]: #"), "kiln.log lists the client world's event types");
    check(p_both->side[KILN_WORLD_CLIENT].engine_id.load() == 818 && p_both->engine_id.load() == 759,
          "the two entries of a both program have each table's own id");

    // Slices: one trampoline; each world's callback and counters.
    Job srv_job(server.storage(), {}), cli_job(client.storage(), {}), pred_job(prediction.mem.data(), {});
    slice(p_both, srv_job);
    slice(p_both, cli_job);
    slice(p_both, pred_job);  // a prediction slice of a client program never comes; if it did, it runs nothing
    check(srv_probe.calls[KILN_WORLD_SERVER] == 1 && srv_probe.calls[KILN_WORLD_CLIENT] == 0 && cli_probe.calls[KILN_WORLD_CLIENT] == 1 &&
              cli_probe.calls[KILN_WORLD_SERVER] == 0 && cli_probe.calls[KILN_WORLD_PREDICTION] == 0 && srv_probe.calls[KILN_WORLD_PREDICTION] == 0,
          "one trampoline: a server slice runs the server callback, a client slice the client one, a prediction slice none");
    slice(p_pred, pred_job);
    slice(p_pred, cli_job);  // a client slice of a prediction program never comes either: nothing
    check(pred_probe.calls[KILN_WORLD_PREDICTION] == 1 && pred_probe.calls[KILN_WORLD_CLIENT] == 0,
          "a prediction program runs on prediction slices only");
    check(p_both->updates.load() == 1 && p_both->side[KILN_WORLD_CLIENT].updates.load() == 1, "each world counts its own updates");
    slice(p_lack, cli_job);
    check(p_lack->side[KILN_WORLD_CLIENT].updates.load() == 0, "a side the client world refused never runs");
    // Overlapping entity ids: entity 7 is a different entity in each world.
    Job srv_ents(server.storage(), {7, 8}, 1), cli_ents(client.storage(), {7, 9}, 1);
    slice(p_entity, srv_ents);
    slice(p_entity, cli_ents);
    check(ent_srv.seen == std::vector<std::pair<uint32_t, uint32_t>>({{0, 7}, {0, 8}}) &&
              ent_cli.seen == std::vector<std::pair<uint32_t, uint32_t>>({{2, 7}, {2, 9}}),
          "overlapping entity ids: each world's callback sees its own world's entities");
    check(p_entity->entities.load() == 2 && p_entity->side[KILN_WORLD_CLIENT].entities.load() == 2, "each world counts its own entities");

    // A program a native caller registers while a client world runs: from the next client world.
    KilnProgramDesc late = system_desc("cw_late");
    Probe late_probe;
    late.each = &probe_each;
    late.user = &late_probe;
    KilnHandle hl = 0;
    check(register_program_world(21, KILN_WORLD_CLIENT, &late, &hl) == KILN_OK && hl == 9 &&
              state_of(hl, KILN_WORLD_CLIENT, &why) == KILN_PROGRAM_PENDING && why == "registered from the next client world",
          "accepted while a client world runs: PENDING, \"registered from the next client world\"");
    check(state_of(hl, KILN_WORLD_SERVER) == -1, "a client program is not for the server world");

    // A fault in the client world disables the program there only; the next client world keeps it off until a rebind.
    check(unbind_program_world(bc) == KILN_OK, "the client binding of both unbinds on the main thread at once");
    KilnHandle bf = 0;
    check(bind_program_world(11, kstr("writer"), kstr("both"), KILN_WORLD_CLIENT, &fault_each, nullptr, nullptr, &bf) == KILN_OK,
          "a faulting callback binds");
    slice(p_both, cli_job);
    slice(p_both, srv_job);
    check(state_of(1, KILN_WORLD_CLIENT, &why) == KILN_PROGRAM_FAULTED && why.find("faulted") != std::string::npos &&
              state_of(1, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE && srv_probe.calls[KILN_WORLD_SERVER] == 2,
          "a fault in the client world disables the client side only; the server side runs on");

    // ---- the client world stops while the host's server world steps on another thread ----
    Probe slow;
    KilnHandle hslow = 0;
    check(bind_program_world(13, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &slow, &hslow) == KILN_OK,
          "the client program binds");
    std::atomic<bool> stop_server{false};
    std::atomic<int> server_slices{0};
    std::thread server_thread([&] {
        Job j(server.storage(), {});
        while (!stop_server.load()) {
            slice(p_both, j);
            server_slices.fetch_add(1);
        }
    });
    slow.hold_ms = 60;  // a client call in flight on a worker when the stop comes: the stop waits for it (bounded)
    std::thread worker([&] {
        Job j(client.storage(), {});
        slice(p_client, j);
    });
    while (!slow.inside.load()) std::this_thread::yield();
    const auto t0 = std::chrono::steady_clock::now();
    client_stops(client.at());
    const auto waited = std::chrono::steady_clock::now() - t0;
    worker.join();
    check(waited >= std::chrono::milliseconds(40), "the client world's stop waits for a client call in flight on a worker");
    check(state_of(2, KILN_WORLD_CLIENT) == KILN_PROGRAM_PENDING && p_client->side[KILN_WORLD_CLIENT].entry.load() == 0,
          "after the stop the registration is PENDING for the next client world (no entry of the old one kept)");
    const int during = server_slices.load();
    std::this_thread::sleep_for(std::chrono::milliseconds(5));
    stop_server = true;
    server_thread.join();
    check(server_slices.load() > during && state_of(1, KILN_WORLD_SERVER) == KILN_PROGRAM_ACTIVE,
          "the host's server world runs the both program on through the client world's stop");
    slow.hold_ms = 0;
    const int slow_calls = slow.calls[KILN_WORLD_CLIENT].load();
    Job stale(client.storage(), {});
    slice(p_client, stale);
    check(slow.calls[KILN_WORLD_CLIENT].load() == slow_calls, "a slice after the stop runs nothing (the old world is closed)");

    // ---- a second client world (leave, join again) at the same address: registers anew; bindings stay ----
    client.make(KILN_WORLD_CLIENT, 817);  // the allocator gave the same address again (seen in client sessions)
    on_world(client.at());
    check(state_of(2, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE && state_of(hl, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE &&
              client.count() == 817 + 6, "a second client world registers again, the late program too");
    check(state_of(1, KILN_WORLD_CLIENT) == KILN_PROGRAM_FAULTED, "a side that faulted in the first world stays off in the second");
    slice(p_client, stale);
    slice(g_programs[8], stale);
    check(slow.calls[KILN_WORLD_CLIENT].load() == slow_calls + 1 && late_probe.calls[KILN_WORLD_CLIENT].load() == 1,
          "a binding made before runs in the second world, and the late program runs there");
    KilnHandle rebound = 0;
    check(unbind_program_world(bf) == KILN_OK &&
              bind_program_world(11, kstr("writer"), kstr("both"), KILN_WORLD_CLIENT, &probe_each, nullptr, &cli_probe, &rebound) == KILN_OK &&
              state_of(1, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE, "a new bind turns a faulted side back on in the world that runs");
    check(unbind_program_world(bc) == KILN_OK && p_both->side[KILN_WORLD_CLIENT].open.load(),
          "an earlier binding's handle is harmless: KILN_OK, the current binding stays");
    const int cli_before = cli_probe.calls[KILN_WORLD_CLIENT].load();
    slice(p_both, stale);
    check(cli_probe.calls[KILN_WORLD_CLIENT].load() == cli_before + 1, "and the rebound callback runs");
    check(unbind_program_world(hslow) == KILN_OK, "the client program's binding unbinds");

    // ---- unbind drains: 8 threads slice while the main thread binds and unbinds; no call after an unbind's OK ----
    Probe stress;
    std::atomic<bool> stop{false};
    std::atomic<int> slices{0};
    std::vector<std::thread> workers;
    for (int i = 0; i < 8; ++i)
        workers.emplace_back([&] {
            Job j(client.storage(), {});
            while (!stop.load()) {
                slice(p_client, j);
                slices.fetch_add(1);
            }
        });
    KilnHandle cur = 0;
    int binds = 0, unbinds = 0, pending = 0;
    for (int round = 0; round < 300; ++round) {
        stress.closed = false;
        if (bind_program_world(14, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &stress, &cur) == KILN_OK) ++binds;
        std::this_thread::sleep_for(std::chrono::microseconds(50));
        if (unbind_program_world(cur) == KILN_OK) {
            stress.closed = true;  // from here no call of it may run
            ++unbinds;
        } else {
            ++pending;
            while (g_closing.load()) programs_client_pre(nullptr);
        }
    }
    stop = true;
    for (auto& w : workers) w.join();
    check(binds == 300 && unbinds + pending == 300 && stress.after_close.load() == 0 && slices.load() > 1000,
          "8 slicing threads while the main thread binds and unbinds 300 times: no call after an unbind's OK");
    check(p_client->side[KILN_WORLD_CLIENT].calls_in.load() == 0, "and every call that started was counted out");

    // ---- an unbind off the client world's thread while a call runs: closed at once, forgotten later ----
    Probe held;
    KilnHandle hb = 0;
    check(bind_program_world(15, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &held, &hb) == KILN_OK,
          "bound again");
    held.hold_ms = 40;
    std::thread long_call([&] {
        Job j(client.storage(), {});
        slice(p_client, j);
    });
    while (!held.inside.load()) std::this_thread::yield();
    KilnResult from_server = KILN_OK;
    std::thread([&] { from_server = unbind_program_world(hb); }).join();
    check(from_server == KILN_ERR_WRONG_THREAD && g_closing.load() == 1,
          "an unbind from the host's server thread while a client call runs: closed, not waited for (WRONG_THREAD)");
    KilnHandle other = 0;
    check(bind_program_world(16, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &held, &other) ==
              KILN_ERR_UNAVAILABLE, "no new binding while the last one's call still finishes");
    long_call.join();
    held.hold_ms = 0;
    const int held_calls = held.calls[KILN_WORLD_CLIENT].load();
    slice(p_client, stale);
    check(held.calls[KILN_WORLD_CLIENT].load() == held_calls, "no call starts after the closing unbind");
    programs_client_pre(nullptr);
    check(g_closing.load() == 0 && p_client->side[KILN_WORLD_CLIENT].user.load() == nullptr,
          "the client world's next Pre forgets it once its call finished");
    check(unbind_program_world(hb) == KILN_OK, "asking again: KILN_OK (its code and data may go now)");

    // ---- a revoked owner's client binding is closed in the client Pre (once a second) ----
    Probe rev;
    KilnHandle hr = 0;
    check(bind_program_world(17, kstr("writer"), kstr("client"), KILN_WORLD_CLIENT, &probe_each, nullptr, &rev, &hr) == KILN_OK, "bound");
    g_revoked = 17;
    std::this_thread::sleep_for(std::chrono::milliseconds(1100));
    programs_client_pre(nullptr);
    g_revoked = 0;
    slice(p_client, stale);
    check(rev.calls[KILN_WORLD_CLIENT].load() == 0 && !p_client->side[KILN_WORLD_CLIENT].open.load(),
          "a revoked owner's client binding is closed by the client world's Pre");

    // ---- the slot ledger (kiln.world.budget): Kiln's entries in the client table are found by their descriptors ----
    const ProgramLedger ledger = program_ledger();
    check(std::binary_search(ledger.descs.begin(), ledger.descs.end(), client.entry_desc(817)) &&
              std::binary_search(ledger.descs.begin(), ledger.descs.end(), reinterpret_cast<uintptr_t>(&p_both->desc)),
          "the ledger lists the client-table descriptors next to the server ones");
    check(ledger.taken == 9, "a both program holds one slot of the 64 (nine programs, nine slots)");

    // ---- the client world's table is full: the live count is checked before each registration ----
    client_stops(client.at());
    client.make(KILN_WORLD_CLIENT, 894);
    on_world(client.at());
    int active = 0, full = 0;
    for (KilnHandle i = 1; i <= 9; ++i)
        for (uint32_t k : {uint32_t{KILN_WORLD_CLIENT}, uint32_t{KILN_WORLD_PREDICTION}}) {
            std::string w;
            const int st = state_of(i, k, &w);
            if (st == KILN_PROGRAM_ACTIVE) ++active;
            if (st == KILN_PROGRAM_REFUSED && w.find("full") != std::string::npos) ++full;
        }
    check(client.count() == 896 && active == 2 && full == 4, "894 of 896: two register, four are refused before the game is called");

    // A side-client mod or dedicated process filters a declared "both" to one world.
    // The boot identity must still compare equal to the original file on reload.
    auto filtered = both;
    filtered.name = kstr("filtered_both");
    check(reserve_program_world("filtered", "both", &filtered, kWorldBitClient,
                                kWorldBitServer | kWorldBitClient) == KILN_OK,
          "a filtered both declaration reserves its available side");
    uint32_t snapshot_size = 0;
    program_reservation(kstr("filtered"), kstr("both"), nullptr, 0, &snapshot_size);
    std::string snapshot(snapshot_size, '\0');
    program_reservation(kstr("filtered"), kstr("both"), snapshot.data(), snapshot_size, &snapshot_size);
    check(snapshot.ends_with("world=both\n"), "boot identity preserves the declared world after side filtering");

    // ---- stop: nothing of a stopped world is reachable; the reason says why ----
    client_stops(client.at());
    check(!g_table_live.load() && g_table_stopping.load(), "after the stop no client-table call starts until the next world");
    check(state_of(2, KILN_WORLD_CLIENT, &why) == KILN_PROGRAM_PENDING && why == "waiting for a client world", "and the reason says so");

    // ==== the package's review (P1): a call still inside when its client world is destroyed, a fault in registration ====
    reset_programs();
    unlatch();
    auto reserve_client = [&](const char* key, const char* name) {
        g_names.push_back(name);
        KilnProgramDesc d = system_desc(name);
        return reserve_program_world("writer", key, &d, kWorldBitClient, kWorldBitClient);
    };
    check(reserve_client("p1", "kiln_r_p1") == KILN_OK && reserve_client("p2", "kiln_r_p2") == KILN_OK &&
              reserve_client("p3", "kiln_r_p3") == KILN_OK, "three client reservations");
    Program* r1 = g_programs[0];
    Program* r3 = g_programs[2];

    // (1) the stop's bounded wait ends with a call still inside: latched for the process, that side never PENDING, the next
    // world registers nothing, and the late call's fault on its way out changes nothing
    g_table_stop_wait_ms = 50;
    client.make(KILN_WORLD_CLIENT, 817);
    on_world(client.at());
    check(state_of(1, KILN_WORLD_CLIENT) == KILN_PROGRAM_ACTIVE && g_row.state == KILN_CAP_AVAILABLE, "registered; the row is available");
    Probe lateProbe;
    lateProbe.hold_ms = 300;
    KilnHandle hlate = 0;
    check(bind_program_world(30, kstr("writer"), kstr("p1"), KILN_WORLD_CLIENT, &slow_fault_each, nullptr, &lateProbe, &hlate) == KILN_OK,
          "a callback that runs long, then faults on its way out");
    std::thread lingering([&] {
        Job j(client.storage(), {});
        slice(r1, j);
    });
    while (!lateProbe.inside.load()) std::this_thread::yield();
    client_stops(client.at());
    check(g_latched.load() && log_count("client world stops: 1 call(s) of Kiln programs still running after") == 1,
          "a call still inside after the wait: the review's line, and the process latch");
    std::string w1;
    check(state_of(1, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("off until the game restarts") != std::string::npos &&
              w1.find("still running") != std::string::npos,
          "that side is refused for good with the latch's reason, never reset to PENDING");
    check(log_count("program kiln_r_p1: a call was still running in the client world when it stopped") == 1,
          "kiln.log names the program whose call was inside");
    check(state_of(2, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("off until the game restarts") != std::string::npos,
          "the others say the process latch");
    check(g_row.state == KILN_CAP_UNAVAILABLE && g_row.reason == KILN_CAP_REASON_FAULTED && g_row.text.find("still running") != std::string::npos,
          "world.programs.client goes unavailable (faulted) with the reason");
    client.make(KILN_WORLD_CLIENT, 817);  // the next client world, at the same address
    on_world(client.at());
    check(client.count() == 817 && state_of(1, KILN_WORLD_CLIENT) == KILN_PROGRAM_REFUSED,
          "the next client world registers nothing while the old call is still inside");
    lingering.join();  // the old call returns now, after the next world was set up, and its callback faults
    check(state_of(1, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("off until the game restarts") != std::string::npos &&
              r1->side[KILN_WORLD_CLIENT].calls_in.load() == 0,
          "the old call's fault on its way out changes nothing of the side (its session was cleared)");
    KilnProgramDesc after_latch = system_desc("kiln_r_after");
    after_latch.each = &probe_each;
    KilnHandle hx = 0;
    check(register_program_world(31, KILN_WORLD_CLIENT, &after_latch, &hx) == KILN_ERR_UNAVAILABLE &&
              bind_program_world(31, kstr("writer"), kstr("p2"), KILN_WORLD_CLIENT, &probe_each, nullptr, &lateProbe, &hx) == KILN_ERR_UNAVAILABLE,
          "after the latch nothing registers or binds for the client world in this process");
    const int rows = g_row.reports;
    world_event(KILN_WORLD_STARTED, KILN_WORLD_CLIENT, client.at());
    client_stops(client.at());
    check(g_row.reports == rows && g_row.state == KILN_CAP_UNAVAILABLE, "later starts and stops do not report the row available again");
    check(log_count("client-world programs are off until the game restarts:") == 1, "kiln.log says it once");

    // (1b) a late call of a stopped world cannot run in the next world's registration either (the session gate)
    unlatch();
    for (Program* p : g_programs)
        for (Side& sd : p->side) sd.state.store(KILN_PROGRAM_PENDING), sd.reason.store(nullptr);
    client.make(KILN_WORLD_CLIENT, 817);
    on_world(client.at());
    Probe gate;
    KilnHandle hg = 0;
    check(unbind_program_world(hlate) == KILN_OK &&
              bind_program_world(32, kstr("writer"), kstr("p2"), KILN_WORLD_CLIENT, &probe_each, nullptr, &gate, &hg) == KILN_OK, "bound again");
    const uint64_t old_session = g_programs[1]->side[KILN_WORLD_CLIENT].session.load();
    client.make(KILN_WORLD_CLIENT, 817);
    on_world(client.at());  // another registration without a stop seen in between (no world.lifecycle line)
    check(g_programs[1]->side[KILN_WORLD_CLIENT].session.load() != old_session && g_programs[1]->side[KILN_WORLD_CLIENT].session.load() != 0,
          "each client world's registration is a session of its own");

    // (2) a fault in the engine's RegisterProgram: what registered stays, nothing more is attempted, the row says why
    unlatch();
    client_stops(client.at());
    unlatch();
    client.make(KILN_WORLD_CLIENT, 817);
    engine_fault_name = "kiln_r_p2";
    engine_calls = 0;
    on_world(client.at());
    engine_fault_name.clear();
    check(client.count() == 818 && state_of(1, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED &&
              w1.find("off until the game restarts: the engine's RegisterProgram faulted") != std::string::npos &&
              r1->side[KILN_WORLD_CLIENT].session.load() == 0,
          "the program registered before the fault stays in the engine's table (not undone) but is refused with the latch's "
          "reason and out of the registration (one truth)");
    {
        Probe before_latch;
        KilnHandle hb2 = 0;
        r1->side[KILN_WORLD_CLIENT].open.store(false);
        r1->side[KILN_WORLD_CLIENT].closing = false;
        r1->side[KILN_WORLD_CLIENT].each.store(&probe_each);
        r1->side[KILN_WORLD_CLIENT].user.store(&before_latch);
        r1->side[KILN_WORLD_CLIENT].open.store(true);  // as if bound before the latch
        Job j(client.storage(), {});
        slice(r1, j);
        check(before_latch.calls[KILN_WORLD_CLIENT].load() == 0 && hb2 == 0,
              "its slices in that world run nothing (it never runs while the row says faulted)");
        r1->side[KILN_WORLD_CLIENT].open.store(false);
        r1->side[KILN_WORLD_CLIENT].each.store(nullptr);
        r1->side[KILN_WORLD_CLIENT].user.store(nullptr);
    }
    int32_t table_state = -1;
    KilnStr table_why{};
    check(program_world_state(0, KILN_WORLD_CLIENT, &table_state, &table_why) == KILN_OK && table_state == KILN_PROGRAM_REFUSED &&
              sv(table_why).find("RegisterProgram faulted") != std::string_view::npos,
          "program_world_state(0, CLIENT): the client table's own state says the latch and why");
    check(state_of(2, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("RegisterProgram faulted") != std::string::npos,
          "the faulting one is refused with the fault");
    check(state_of(3, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("off until the game restarts") != std::string::npos &&
              engine_calls == 2, "the next one is not attempted (two engine calls, not three)");
    check(g_latched.load() && g_row.state == KILN_CAP_UNAVAILABLE && g_row.reason == KILN_CAP_REASON_FAULTED &&
              g_row.text.find("RegisterProgram faulted") != std::string::npos, "the row goes unavailable with the reason");
    check(log_count("client-world programs are off until the game restarts: the engine's RegisterProgram faulted") == 1, "kiln.log once");
    client_stops(client.at());
    client.make(KILN_WORLD_CLIENT, 817);
    engine_calls = 0;
    on_world(client.at());
    check(engine_calls == 0 && client.count() == 817, "a later client world: no registration is attempted");

    // (2b) a table that does not add up after RegisterProgram (two entries for one call)
    unlatch();
    client_stops(client.at());
    unlatch();
    client.make(KILN_WORLD_CLIENT, 817);
    engine_double_name = "kiln_r_p1";
    engine_calls = 0;
    on_world(client.at());
    engine_double_name.clear();
    check(state_of(1, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("does not add up") != std::string::npos &&
              g_latched.load() && engine_calls == 1, "a count that does not add up: refused, latched, nothing more attempted");

    // (2c) a fault in Kiln's own raw reads around the call (the maps, the count): latched as well
    unlatch();
    client_stops(client.at());
    unlatch();
    client.make(KILN_WORLD_CLIENT, 817);
    fail_seh_call = true;
    engine_calls = 0;
    on_world(client.at());
    check(state_of(1, KILN_WORLD_CLIENT, &w1) == KILN_PROGRAM_REFUSED && w1.find("reading the client world's maps or program table faulted") != std::string::npos &&
              g_latched.load() && engine_calls == 0 && r3->side[KILN_WORLD_CLIENT].state.load() == KILN_PROGRAM_REFUSED,
          "a raw read that faulted: refused, latched, the engine never called");
    unlatch();
    g_table_stop_wait_ms = 2000;

    {  // ==== the package's closing review (its own scope: names of the earlier sections are reused) ====
    reset_programs();
    unlatch();
    for (auto& t : g_table_storage) t.store(0);
    g_names.push_back("kiln_c_ent");
    KilnProgramDesc ent = system_desc("kiln_c_ent", 0);  // an entity program reading Health: one callback per entity
    ent.read = KilnNames{health, 1, 0};
    check(reserve_program_world("writer", "ent", &ent, kWorldBitClient | kWorldBitPrediction, kWorldBitClient) == KILN_OK,
          "an entity program for the client world and the prediction storage");
    Program* ce = g_programs[0];
    client.make(KILN_WORLD_CLIENT, 817);
    on_world(client.at());
    int32_t st0 = -1;
    check(program_world_state(0, KILN_WORLD_CLIENT, &st0, nullptr) == KILN_OK && st0 == KILN_PROGRAM_ACTIVE &&
              program_world_state(0, KILN_WORLD_SERVER, &st0, nullptr) == KILN_ERR_NOT_FOUND,
          "program_world_state(0, CLIENT) while a client world runs: ACTIVE (and handle 0 is no server program)");

    // (1) a slice of 1,000 entities whose world begins to stop at entity 10: at most one more callback, then nothing of
    // that world is touched (no iteration, no CurrentEntity, no helper), and the slice is finished on paper
    struct Late {
        std::atomic<int> calls{0};
        std::atomic<bool> at_ten{false};
        std::atomic<int> entity_before{-1}, entity_after{-1};
        bool helpers_refused = false;
    } late;
    std::atomic<int> marks{0};
    static std::atomic<int>* g_marks = nullptr;
    g_marks = &marks;
    fn.mark_written = [](void*, void*, void*, void*, uint16_t, uint8_t) { g_marks->fetch_add(1); };
    KilnHandle hl = 0;
    auto late_each = [](KilnProgramCtx* c, uint64_t* it, KilnEntity, void* user) {
        auto& l = *static_cast<Late*>(user);
        const int n = l.calls.fetch_add(1) + 1;
        if (n == 1) l.entity_before = static_cast<int>(current_entity(c));
        if (n != 10) return;
        l.at_ten = true;
        while (!g_table_stopping.load()) std::this_thread::yield();  // the main thread began the stop
        uint32_t size = 7;
        const uint64_t w[6] = {0, 1, 2, 3, 4, 5};
        mark_written(c, w);
        l.entity_after = static_cast<int>(current_entity(c));
        l.helpers_refused = l.entity_after == 0 && !command_buffer(c) && !read_other(c, 1, 5, &size) && size == 0 &&
                            !iter_next(c, it, 4096) && iter_begin(c, it, 4096) == KILN_ERR_UNAVAILABLE &&
                            program_emit(c, kstr("ClientEvent"), w, 8) == KILN_ERR_UNAVAILABLE &&
                            program_events(c, kstr("ClientEvent"), [](const void*, uint32_t, void*) {}, nullptr) == KILN_ERR_UNAVAILABLE &&
                            !write_other(c, 1, 5, &size);
    };
    check(bind_program_world(40, kstr("writer"), kstr("ent"), KILN_WORLD_CLIENT, late_each, nullptr, &late, &hl) == KILN_OK,
          "bound: a callback that holds the slice at entity 10 until the world begins to stop");
    std::vector<uint32_t> thousand(1000);
    for (uint32_t i = 0; i < 1000; ++i) thousand[i] = 5000 + i;
    Job big(client.storage(), thousand, 1);
    ProgramCtx big_ctx{big.bytes, 0, 0};
    const uint64_t late_before = g_late_slices.load();
    const int off_lines = log_count("off until the game restarts");
    std::thread worker([&] { ce->desc.update(&big_ctx); });
    while (!late.at_ten.load()) std::this_thread::yield();
    client_stops(client.at());  // on the main thread, while the worker's slice is inside
    worker.join();
    check(late.calls.load() >= 10 && late.calls.load() <= 11, "a slice of 1,000 whose world stops at entity 10: at most one more callback");
    check(late.entity_before.load() == 5000 && late.helpers_refused,
          "inside that call after the stop began: CurrentEntity, the command buffer, read_other, iteration, emit and events refuse");
    check(marks.load() == 0, "mark_written from a client program is a no-op (nothing marked for the save or replication)");
    check(big_ctx.index == 1000 && g_late_slices.load() == late_before + 1,
          "the slice is finished on paper (index = its entity count) and counted");
    check(!g_latched.load() && log_count("its world began to stop while a slice of it ran") >= 1,
          "no latch: the call left at its next entity, inside the stop's wait");
    check(log_count("off until the game restarts") == off_lines, "no 'off until the game restarts' line without the latch");
    fn.mark_written = nullptr;

    // (2) the gate tells which world a slice belongs to: after world N stopped and N+1 registered, a job of N (its storage)
    // is refused and counted; N+1's own slices and the prediction storage's run, a second prediction storage is refused
    late.calls = 0;
    late.at_ten = true;  // no hold
    client2.make(KILN_WORLD_CLIENT, 817);
    on_world(client2.at());  // world N+1 at another address: its storage is another
    const uint64_t other_before = g_other_world_slices.load();
    Job stale_job(client.storage(), {1, 2, 3}, 1);  // a job of world N entering late
    slice(ce, stale_job);
    check(late.calls.load() == 0 && g_other_world_slices.load() == other_before + 1 && logged_has("a slice of another world"),
          "a job of the stopped world (its own storage) is refused and counted, with N+1's session");
    Job fresh_job(client2.storage(), {1, 2, 3}, 1);
    slice(ce, fresh_job);
    check(late.calls.load() == 3, "N+1's own slice runs");
    Probe pred_probe;
    KilnHandle hpred = 0;
    check(bind_program_world(41, kstr("writer"), kstr("ent"), KILN_WORLD_PREDICTION, &probe_each, nullptr, &pred_probe, &hpred) == KILN_OK,
          "the prediction side bound");
    FakeStorage prediction2(KILN_WORLD_PREDICTION);
    Job pj(prediction.mem.data(), {9}, 1);
    slice(ce, pj);
    Job pj2(prediction2.mem.data(), {9}, 1);
    slice(ce, pj2);
    check(pred_probe.calls[KILN_WORLD_PREDICTION].load() == 1 && g_other_world_slices.load() == other_before + 2,
          "the prediction storage's first slice names it for the session; another prediction storage is refused");
    check(unbind_program_world(hpred) == KILN_OK, "prediction side unbound");

    // (1c) the core's drain (06a, world.stop_drain.client): the stop's row and line say it; a call inside at the stop
    // although the engine drained the world's jobs is an error and latches, also when it leaves within the wait
    g_engine_drain = true;
    client_stops(client2.at());
    check(logged_has("Kiln client-table registration(s) closed for the next client world (0 call(s) running at the stop, 0 after") &&
              logged_has("the engine drains the world's jobs first (world.stop_drain.client))") && !g_latched.load(),
          "with the engine's drain: the stop line names it, nothing inside, no latch");
    check(g_row.state == KILN_CAP_AVAILABLE && g_row.text.find("waiting for a world; at a world stop the engine drains") != std::string::npos,
          "the row's text says which guarantee a stop has");
    world_event(KILN_WORLD_STARTED, KILN_WORLD_CLIENT, client2.at());
    check(g_row.text.find("a client world runs; at a world stop the engine drains") != std::string::npos, "also while a world runs");
    client2.make(KILN_WORLD_CLIENT, 817);
    on_world(client2.at());
    Late brief;
    KilnHandle hbrief = 0;
    check(unbind_program_world(hl) == KILN_OK &&
              bind_program_world(42, kstr("writer"), kstr("ent"), KILN_WORLD_CLIENT, late_each, nullptr, &brief, &hbrief) == KILN_OK,
          "bound again: a callback that holds its slice at entity 10 until the stop begins, then leaves");
    Job brief_job(client2.storage(), thousand, 1);
    ProgramCtx brief_ctx{brief_job.bytes, 0, 0};
    std::thread brief_worker([&] { ce->desc.update(&brief_ctx); });
    while (!brief.at_ten.load()) std::this_thread::yield();
    client_stops(client2.at());
    brief_worker.join();
    check(logged_has("call(s) of Kiln programs were inside although the engine drains") && g_latched.load() &&
              g_row.state == KILN_CAP_UNAVAILABLE && g_row.text.find("although the engine drains") != std::string::npos,
          "with the engine's drain a call inside at the stop is an error and latches, though it left within the wait");
    std::string wb;
    check(state_of(1, KILN_WORLD_CLIENT, &wb) == KILN_PROGRAM_REFUSED && wb.find("although the engine drains") != std::string::npos,
          "every side says it");
    g_engine_drain = false;
    unlatch();
    for (Program* p : g_programs)
        for (Side& sd : p->side) sd.state.store(KILN_PROGRAM_PENDING), sd.reason.store(nullptr);
    hl = hbrief;

    // (4) raw reads under the guard: the storage kind in on_world, the event map in the first client Pre
    client_stops(client2.at());
    client.make(KILN_WORLD_CLIENT, 817);
    fail_copy_from = client.storage() + kKind;
    on_world(client.at());
    fail_copy_from = nullptr;
    check(logged_has("its storage's kind could not be read; no Kiln programs registered there") && client.count() == 817,
          "a storage kind that cannot be read: nothing registered, kiln.log says so");
    on_world(client.at());
    g_client_world = client.at();
    fail_seh_call = true;  // the event map's read faults
    programs_client_pre(nullptr);
    check(logged_has("client world 0x") && logged_has("reading its event map faulted"),
          "the first client Pre's event-map dump under the guard: a fault is said, nothing else happens");
    g_client_world = 0;
    check(unbind_program_world(hl) == KILN_OK, "unbound");
    client_stops(client.at());

    }

    reset_programs();
    std::printf("client world programs: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
