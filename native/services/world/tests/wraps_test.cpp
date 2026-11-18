// SPDX-License-Identifier: MIT
// kiln.world v4 without the game: the program directory of a fake world table (names, game and mod programs, client
// programs with no update), the players mask and the lab parallel switch, the wraps over a fake program runner (stage
// order, one Replace, index repair after faults, in-flight balance, the prediction forward, foreign swaps, world stop,
// budgets, owners, two stress rows on 8 threads), in_program on the threads inside and outside, and the v4 ABI layout
// (tests/abi.json).
#include "../game_programs.cpp"
#include "../layout.cpp"
#include "../wraps.cpp"
#include "../foreign.cpp"
#include <barrier>

#include <cstdarg>
#include <deque>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <map>
#include <thread>

#include "nlohmann/json.hpp"

namespace kw {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 1;
Fns fn;
Layout L;
Features F;
std::atomic<uintptr_t> g_world{0};
std::atomic<uint64_t> g_internal_runs{0};
std::atomic<bool> g_world_live{false};
thread_local bool t_in_program = false;
std::atomic<int> g_stop_timeout_logs{0};
std::mutex g_log_mu;
std::vector<std::string> g_log_lines;  // every kiln.log line the service wrote, formatted
void logf(int, const char* fmt, ...) {
    if (std::strstr(fmt, "wrap drain timed out")) ++g_stop_timeout_logs;
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    {
        std::lock_guard lock(g_log_mu);
        g_log_lines.emplace_back(buf);
    }
    if (std::getenv("KILN_TEST_LOG")) std::fprintf(stderr, "%s\n", buf);
}
int logs_with(const char* text) {
    std::lock_guard lock(g_log_mu);
    int n = 0;
    for (const auto& l : g_log_lines) n += l.find(text) != std::string::npos;
    return n;
}
bool seh_copy(void* to, const void* from, size_t size) {
    if (!from || !to) return false;
    std::memcpy(to, from, size);
    return true;
}
// A "fault" in a stage is a thrown int here; kiln.world's frames see the same: the guard returns false.
void runner_scope(uintptr_t* slot, uintptr_t record, void (*body)(void*), void* context) {
    const auto outer = std::exchange(*slot, record);
    try { body(context); }
    catch (...) { *slot = outer; throw; }
    *slot = outer;
}
bool seh_wrap_stage(KilnWrapStageFn f, KilnGameSlice* slice, void* user, unsigned long* code) {
    try {
        f(slice, user);
        return true;
    } catch (int) {
        *code = 0xC0000005u;
        return false;
    }
}
bool seh_cas_ptr(uintptr_t address, uintptr_t expected, uintptr_t desired, uintptr_t* seen) {
    std::atomic_ref<uintptr_t> at(*reinterpret_cast<uintptr_t*>(address));
    at.compare_exchange_strong(expected, desired);
    *seen = expected;
    return true;
}
bool seh_attr_changed(void (*f)(void*, void*), void* component, void* registry, unsigned long* code) {
    try {
        f(component, registry);
        return true;
    } catch (int) {
        *code = 0xC0000005u;
        return false;
    }
}
// kiln.types with only what kiln.world asks of it here: whether native SET owns the program runner.
std::atomic<uint32_t> g_fake_external{0};
// kiln.types: the external runner, and the reflected sizes of a few components (a type handle is its size).
const KilnTypesApi* program_types() {
    static KilnTypesApi t = [] {
        KilnTypesApi v{};
        v.header = {sizeof(KilnTypesApi), KILN_TYPES_VERSION};
        v.external_runner = [] { return g_fake_external.load(); };
        v.type_by_name = [](KilnStr n) -> const KilnType* {
            const std::string s(n.ptr, n.len);
            const uintptr_t size = s == "keen::ecs::Fuel" ? 16 : s == "keen::ecs::DynamicFuelSetup" ? 24 :
                                   s == "keen::ecs::CurrentTransform" ? 48 : 0;
            return reinterpret_cast<const KilnType*>(size);
        };
        v.type_info = [](const KilnType* type, KilnTypeInfo* out) -> KilnResult {
            out->type_size = static_cast<uint32_t>(reinterpret_cast<uintptr_t>(type));
            out->name = reinterpret_cast<uintptr_t>(type) == 1 ? kstr("AttributeComponent") : kstr("Fixture");
            out->inner = reinterpret_cast<uintptr_t>(type) == 16 ? reinterpret_cast<const KilnType*>(1) : nullptr;
            return KILN_OK;
        };
        return v;
    }();
    return &t;
}
uint32_t fnv1a(std::string_view value, uint32_t hash) {
    for (unsigned char c : value) hash = (hash ^ c) * 16777619u;
    return hash;
}
// The world's component/template/event maps: one hash -> type index map for all of them.
std::map<uint32_t, uint16_t> g_type_index;
bool map_find_u16(uintptr_t, uint32_t key, uint16_t* value) {
    const auto it = g_type_index.find(key);
    if (it == g_type_index.end()) return false;
    if (value) *value = it->second;
    return true;
}
// the engine's ReadOther / WriteOther over a fake store: entity -> component bytes, per accessor
std::map<std::pair<uintptr_t, uint32_t>, std::vector<uint8_t>> g_fstore;
int g_fwrites = 0;
bool g_client_program = false;  // what programs.cpp's in_client_program answers (foreign.cpp refuses its writes then)
bool in_client_program() { return g_client_program; }
bool seh_read_other(Slice* out, void*, void* accessor, uint32_t entity, unsigned long*) {
    const auto at = g_fstore.find({reinterpret_cast<uintptr_t>(accessor), entity});
    *out = at == g_fstore.end() ? Slice{} : Slice{at->second.data(), at->second.size()};
    return true;
}
bool seh_write_other(Slice* out, void*, ProgramCtx*, uint64_t accessor, uint32_t entity, unsigned long*) {
    ++g_fwrites;  // the tracker marks it
    const auto at = g_fstore.find({static_cast<uintptr_t>(accessor), entity});
    *out = at == g_fstore.end() ? Slice{} : Slice{at->second.data(), at->second.size()};
    return true;
}
}  // namespace kw

using namespace kw;

namespace {
int checks = 0, failures = 0;
void check(bool ok, const char* what) {
    ++checks;
    if (!ok) {
        ++failures;
        std::fprintf(stderr, "FAIL %s\n", what);
    }
}

// ---- a fake world: its program table and storage, in one block ----
constexpr int64_t kTable = 0x100, kStride = 0x40, kCount = 0x10, kStorage = 0x20000, kMask = 0x40;
constexpr uint32_t kCapacity = 896;
alignas(64) uint8_t g_world_mem[kStorage + 0x100];
alignas(64) EngineDesc g_image_descs[16];     // "the game's image": descriptors inside [image lo, hi)
EngineDesc g_mod_desc;                        // a mod's program: its descriptor is outside
std::deque<std::string> g_name_store;   // stable: the table points into it
uintptr_t g_live_world[KILN_WORLD_KINDS];
uint32_t g_side = KILN_SIDE_SERVER;

void update_a(void*) {}
void update_b(void*) {}

uintptr_t entry_at(int i) { return reinterpret_cast<uintptr_t>(g_world_mem) + kTable + static_cast<uintptr_t>(i) * kStride; }
void put_program(int i, const char* name, const EngineDesc* desc, void (*update)(void*), uint32_t desc_flags) {
    const uintptr_t e = entry_at(i);
    std::memset(reinterpret_cast<void*>(e), 0, kStride);
    g_name_store.emplace_back(name);
    const char* p = g_name_store.back().c_str();
    const uint64_t len = std::strlen(name);
    const auto d = reinterpret_cast<uintptr_t>(desc);
    const uint16_t id = static_cast<uint16_t>(i + 1);
    const auto u = reinterpret_cast<uintptr_t>(update);
    std::memcpy(reinterpret_cast<void*>(e), &d, 8);
    std::memcpy(reinterpret_cast<void*>(e + 8), &id, 2);
    std::memcpy(reinterpret_cast<void*>(e + 0x10), &p, 8);
    std::memcpy(reinterpret_cast<void*>(e + 0x18), &len, 8);
    std::memcpy(reinterpret_cast<void*>(e + 0x20), &desc_flags, 4);
    std::memcpy(reinterpret_cast<void*>(e + 0x28), &u, 8);
    const_cast<EngineDesc*>(desc)->flags = desc_flags;
}
void set_count(uint16_t n) { std::memcpy(g_world_mem + kCount, &n, 2); }
uint16_t mask() {
    uint16_t m = 0;
    std::memcpy(&m, g_world_mem + kStorage + kMask, 2);
    return m;
}
void set_mask(uint16_t m) { std::memcpy(g_world_mem + kStorage + kMask, &m, 2); }

// kiln.hooks with only its owner cleanup: the test calls the observer as the core would when it revokes an owner.
bool g_hooks_on = true;
KilnOwnerRetiringFn g_retiring_fn = nullptr;
void* g_retiring_user = nullptr;
KilnOwner g_retiring_observer = 0;
constexpr uintptr_t kRunner = 0x14064ac00;  // types/ProgramRunner, as sigdb resolves it
KilnTypedFn g_mid_fn = nullptr;
thread_local const std::function<void()>* g_runner_body = nullptr;
void* g_mid_user = nullptr;
uintptr_t g_mid_target = 0;
int g_mid_calls = 0;
KilnResult g_mid_rc = KILN_OK;
KilnHooksApi g_fake_hooks = [] {
    KilnHooksApi h{};
    h.header = {sizeof(KilnHooksApi), KILN_HOOKS_VERSION};
    h.subscribe_owner_retiring = [](KilnOwner observer, KilnOwnerRetiringFn fn, void* user, KilnHandle* out) -> KilnResult {
        g_retiring_observer = observer, g_retiring_fn = fn, g_retiring_user = user;
        if (out) *out = 1;
        return KILN_OK;
    };
    h.unsubscribe_owner_retiring = [](KilnHandle) -> KilnResult { return KILN_OK; };
    // the checked mid link kiln.world puts on types/ProgramRunner's entry (world.slice_record): recorded, so a test can run
    // it as the runner would; g_mid_rc != OK refuses it as an unchecked thread would
    h.call_original_frame = [](KilnCallFrame*) -> KilnResult { if (g_runner_body) (*g_runner_body)(); return KILN_OK; };
    h.detour_typed_checked = [](KilnOwner, uintptr_t target, const KilnHookOrder*, const KilnSig* sig, KilnTypedFn fn, void* user, KilnHandle* out) -> KilnResult {
        if (!sig || sig->argc != 4 || sig->ret_kind != KILN_SIG_VOID) return KILN_ERR_INVALID_ARGUMENT;
        ++g_mid_calls;
        if (g_mid_rc != KILN_OK) {
            return g_mid_rc;
        }
        g_mid_fn = fn, g_mid_user = user, g_mid_target = target;
        if (out) *out = 7;
        return KILN_OK;
    };
    return h;
}();
KilnApi g_fake{};
void fake_api() {
    g_fake.size = sizeof(KilnApi);
    g_fake.abi_version = KILN_ABI_VERSION;
    g_fake.log = [](KilnOwner, int32_t, KilnStr) {};
    g_fake.feature = [](KilnStr) { return true; };
    g_fake.constant = [](KilnStr name, int64_t* out) -> KilnResult {
        const std::string n(name.ptr, name.len);
        if (n == "world/storage.players_mask") return *out = kMask, KILN_OK;
        if (n == "world/programs.names") return *out = kTable + 0x10, KILN_OK;
        return KILN_ERR_NOT_FOUND;
    };
    g_fake.world = [](uint32_t kind) { return kind < KILN_WORLD_KINDS ? g_live_world[kind] : uintptr_t{0}; };
    g_fake.server_world = [] { return g_live_world[0]; };
    g_fake.side = [] { return g_side; };
    g_fake.service = [](KilnStr name, uint32_t) -> const void* {
        return g_hooks_on && std::string(name.ptr, name.len) == KILN_HOOKS_SERVICE ? &g_fake_hooks : nullptr;
    };
    g_fake.path = [](int32_t, char*, size_t, size_t*) -> KilnResult { return KILN_ERR_UNAVAILABLE; };
    g_fake.symbol = [](KilnStr name, uintptr_t* out) -> KilnResult {
        if (std::string(name.ptr, name.len) != "types/ProgramRunner") return KILN_ERR_NOT_FOUND;
        *out = kRunner;
        return KILN_OK;
    };
    g_api = &g_fake;
}

// The core's world.stop_drain.<kind> rows as kiln.caps answers them (since dev.29): g_drain[kind] = 1 available
// ("drained by the engine"), 0 unavailable (a bounded wait only), -1 no row (an older core).
int g_drain[KILN_WORLD_KINDS] = {-1, -1, -1};
KilnResult KILN_CALL fake_caps_find(KilnStr name, KilnCapRow* out) {
    static const char* kNames[KILN_WORLD_KINDS] = {"world.stop_drain.server", "world.stop_drain.prediction",
                                                  "world.stop_drain.client"};
    for (uint32_t k = 0; k < KILN_WORLD_KINDS; ++k) {
        if (std::string_view(name.ptr, name.len) != kNames[k] || g_drain[k] < 0) continue;
        static const char kOn[] = "drained by the engine: WaitWorldJobs on the world before STOPPING";
        static const char kOff[] = "bounded wait only: tick.wait_jobs did not resolve";
        out->state = g_drain[k] ? KILN_CAP_AVAILABLE : KILN_CAP_UNAVAILABLE;
        out->reason = g_drain[k] ? KILN_CAP_REASON_NONE : KILN_CAP_REASON_SYMBOL_MISSING;
        out->name = name;
        out->text = g_drain[k] ? KilnStr{kOn, sizeof kOn - 1} : KilnStr{kOff, sizeof kOff - 1};
        return KILN_OK;
    }
    return KILN_ERR_NOT_FOUND;
}
KilnCapsApi g_stop_caps = [] {
    KilnCapsApi c{};
    c.header = {sizeof c, 1};
    c.report = [](KilnOwner, KilnStr, uint32_t, uint32_t, KilnStr) -> KilnResult { return KILN_OK; };
    c.find = &fake_caps_find;
    return c;
}();

// A client without the destroy hook cannot retire wraps when its world disappears.
void client_lifecycle_row() {
    KilnCapsApi caps{};
    caps.header = {sizeof caps, 1};
    static uint32_t state, wraps_state = 99, typed_state = 99, define_state = 99;
    static std::string detail;
    caps.report = [](KilnOwner, KilnStr name, uint32_t value, uint32_t, KilnStr text) -> KilnResult {
        if (std::string_view(name.ptr, name.len) == "systems.wraps.client") {
            state = value;
            detail.assign(text.ptr, text.len);
        }
        if (std::string_view(name.ptr, name.len) == "systems.wraps") wraps_state = value;
        if (std::string_view(name.ptr, name.len) == "systems.wraps.typed") typed_state = value;
        if (std::string_view(name.ptr, name.len) == "systems.define") define_state = value;
        return KILN_OK;
    };
    static const KilnCapsApi* table;
    table = &caps;
    g_fake.service = [](KilnStr, uint32_t) -> const void* { return table; };
    g_fake.feature = [](KilnStr name) { return std::string_view(name.ptr, name.len) != "world.lifecycle"; };
    g_side = KILN_SIDE_CLIENT;
    wraps_init();
    check(state == KILN_CAP_UNAVAILABLE && detail.find("world.lifecycle") != std::string::npos,
          "client wraps refuse a build without world-stop notifications");
    wraps_client_ready(true, true);
    check(state == KILN_CAP_UNAVAILABLE, "subscriptions cannot enable wraps without lifecycle");
    g_fake.feature = [](KilnStr) { return true; };
    wraps_client_ready(false, true);
    check(state == KILN_CAP_UNAVAILABLE, "client tick subscription failure keeps wraps off");
    wraps_client_ready(true, false);
    check(state == KILN_CAP_UNAVAILABLE, "world event subscription failure keeps wraps off");
    caps.find = &fake_caps_find;
    g_drain[KILN_WORLD_CLIENT] = 1;
    wraps_client_ready(true, true);
    check(state == KILN_CAP_AVAILABLE, "native client wraps available with lifecycle and both subscriptions");
    check(detail.find("at a world stop the engine drains the world's jobs first (world.stop_drain.client)") != std::string::npos,
          "the client wrap row says the core drains the client world's jobs before its stop");
    g_drain[KILN_WORLD_CLIENT] = 0;
    wraps_client_ready(true, true);
    check(state == KILN_CAP_AVAILABLE &&
              detail.find("at a world stop a bounded wait (2000 ms) only (world.stop_drain.client: bounded wait only") !=
                  std::string::npos,
          "without the engine's drain the client wrap row says the stop has a bounded wait only");
    g_drain[KILN_WORLD_CLIENT] = 1;
    wraps_client_ready(true, true);
    check(typed_state == wraps_state && wraps_state != 99, "systems.wraps.typed is reported with systems.wraps");
    g_fake_external.store(1);
    wraps_pre(KILN_WORLD_CLIENT, 0);
    check(state == KILN_CAP_UNAVAILABLE, "native SET lowers the client wrap row too");
    check(typed_state == KILN_CAP_UNAVAILABLE && wraps_state == KILN_CAP_UNAVAILABLE, "native SET lowers the typed wrap row too");
    g_fake_external.store(0);
    wraps_pre(KILN_WORLD_CLIENT, 0);
    check(state == KILN_CAP_AVAILABLE, "client wrap row recovers when native SET leaves");
    check(typed_state == wraps_state, "the typed wrap row follows systems.wraps back");
    wraps_define_row(false);
    const uint32_t define_off = define_state;
    wraps_define_row(true);
    check(define_off == KILN_CAP_UNAVAILABLE && define_state == KILN_CAP_AVAILABLE,
          "systems.define follows kiln.world's own programs (off when they did not hook, on when they did)");
    g_side = KILN_SIDE_SERVER;
    fake_api();
    g_wrap_caps = nullptr;  // `caps` goes with this frame; the next wraps_init looks kiln.caps up again
    g_drain[KILN_WORLD_CLIENT] = -1;
}

void layout() {
    L.world_storage = kStorage;
    L.storage_kind = 8;
    L.programs_table = kTable;
    L.programs_count = kCount;
    L.programs_stride = kStride;
    L.programs_max = kCapacity;
    L.entry_flags = 0x20;
    L.entry_id = 8;
    L.job_entity_count = 0x838;
    L.job_entity_list = 0x830;
    // the job's group counts (chapter 06a 2.3) and the maps per-entity work reads
    L.job_tpl_count = 0x4B0;
    L.job_read_count = 0x4B8;
    L.job_write_count = 0x4BA;
    L.job_singletons = 0xC0;
    L.singletons_count = 0x400;
    L.job_other_tpl_count = 0x7F8;
    L.job_other_count = 0x7A8;
    L.job_events_in_count = 0x1A8;
    L.job_events_out_count = 0x1B0;
    L.reg_components = 0x10;
    L.reg_templates = 0x20;
    L.storage_entities = 0x40;
    L.entity_layout = 0x18;
    L.entity_id = 0x10;
    L.map = MapFields{0x08, 0x10, 0x28, 0x40, 0x54};
    L.slice_entry = 0x10;  // the engine's slice record (world.slice_record)
    L.slice_layout = 0x18;
    L.slice_first = 0x28;
    L.slice_end = 0x2C;
    F.write = true;
}

KilnTickInfo tick(uint64_t step) {
    KilnTickInfo t{};
    t.size = sizeof t;
    t.step = step;
    return t;
}

void world_event(uint32_t event, uint32_t kind) {
    KilnWorldEvent e{};
    e.size = sizeof e;
    e.event = event;
    e.kind = kind;
    e.world = g_live_world[kind];
    game_programs_world_event(&e);
}

// ---- the directory ----
void directory() {
    const uintptr_t world = reinterpret_cast<uintptr_t>(g_world_mem);
    put_program(0, "fuel_setup", &g_image_descs[0], &update_a, 0x10);
    put_program(1, "fuel_network", &g_image_descs[1], &update_b, 0x10);
    put_program(2, "client_event_tracking", &g_image_descs[2], nullptr, 0x1);   // a client program: no update here
    put_program(3, "player_ui", &g_image_descs[3], nullptr, 0x1);
    put_program(4, "kiln_world", &g_mod_desc, &update_a, 0x14);                 // Kiln's own: not the game's
    set_count(5);
    const auto lo = reinterpret_cast<uintptr_t>(g_image_descs);
    game_programs_test_setup(lo, lo + sizeof g_image_descs, true, kMask, false);

    check(game_program_find(KILN_WORLD_SERVER, kstr("fuel_setup")) == -1, "no directory before the world's first Pre");
    g_live_world[0] = world;
    KilnTickInfo t = tick(1);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    check(game_program_find(KILN_WORLD_SERVER, kstr("fuel_setup")) == 0 &&
              game_program_find(KILN_WORLD_SERVER, kstr("kiln_world")) == 4 &&
              game_program_find(KILN_WORLD_SERVER, kstr("nope")) == -1,
          "find by name after the first Pre");
    check(game_program_find(KILN_WORLD_PREDICTION, kstr("fuel_setup")) == -1 &&
              game_program_find(KILN_WORLD_CLIENT, kstr("fuel_setup")) == -1 && game_program_find(7, kstr("fuel_setup")) == -1,
          "other world kinds have no directory (prediction never has one)");

    KilnGameProgramInfo info{};
    info.size = sizeof info;
    check(game_program_info(KILN_WORLD_SERVER, 1, &info) == KILN_OK && info.index == 1 && info.id == 2 &&
              sv(info.name) == "fuel_network" && info.flags == KILN_GAME_PROGRAM_GAME && info.desc_flags == 0x10 &&
              info.entry_flags == 0x10 && info.world_kind == KILN_WORLD_SERVER,
          "info: index, id, name, flags of a game program");
    check(info.update_rva == 0, "an update outside the game's image has no rva");
    check(game_program_info(KILN_WORLD_SERVER, 2, &info) == KILN_OK &&
              info.flags == (KILN_GAME_PROGRAM_GAME | KILN_GAME_PROGRAM_NO_UPDATE) && info.desc_flags == 0x1,
          "a client program in the server world: the game's, with no update");
    check(game_program_info(KILN_WORLD_SERVER, 4, &info) == KILN_OK && info.flags == 0, "Kiln's own program is not the game's");
    check(game_program_info(KILN_WORLD_SERVER, 5, &info) == KILN_ERR_NOT_FOUND &&
              game_program_info(KILN_WORLD_SERVER, -1, &info) == KILN_ERR_NOT_FOUND,
          "info refuses an index outside the table");
    info.size = 8;
    check(game_program_info(KILN_WORLD_SERVER, 0, &info) == KILN_ERR_INVALID_ARGUMENT &&
              game_program_info(KILN_WORLD_SERVER, 0, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "info refuses a small or missing struct");

    KilnGameProgramsStatus st{};
    st.size = sizeof st;
    check(game_programs_status(&st) == KILN_OK && st.directory[0] == 1 && st.directory[2] == 0 && st.programs[0] == 5 &&
              st.game_programs[0] == 4 && st.no_update[0] == 2,
          "status: 5 programs, 4 of the game's, 2 of them client programs");

    // Names stay valid after the directory is read again (a new world), and the new one is what find answers.
    const KilnStr kept = [] {
        KilnGameProgramInfo i{};
        i.size = sizeof i;
        game_program_info(KILN_WORLD_SERVER, 0, &i);
        return i.name;
    }();
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    check(game_program_find(KILN_WORLD_SERVER, kstr("fuel_setup")) == -1, "a stopping world's directory goes");
    put_program(5, "fuel_setup_init", &g_image_descs[5], &update_b, 0x18);
    set_count(6);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    t = tick(2);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    check(game_program_find(KILN_WORLD_SERVER, kstr("fuel_setup_init")) == 5 && sv(kept) == "fuel_setup",
          "the next world's directory has its programs; old names stay readable");
    // A count change in the same world is read again too (a world that registered more programs).
    put_program(6, "late_program", &g_image_descs[6], &update_a, 0x10);
    set_count(7);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    check(game_program_find(KILN_WORLD_SERVER, kstr("late_program")) == 6, "a grown table is read again");
    set_count(kCapacity + 1);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    check(game_program_find(KILN_WORLD_SERVER, kstr("late_program")) == 6, "a count over the table's capacity is not believed");
    set_count(7);

    // The client world (a game client): its own directory from its own Pre.
    alignas(64) static uint8_t client_mem[kStorage + 0x100];
    std::memcpy(client_mem, g_world_mem, sizeof client_mem);
    g_live_world[2] = reinterpret_cast<uintptr_t>(client_mem);
    game_programs_pre(KILN_WORLD_CLIENT, &t);
    game_programs_pre(KILN_WORLD_PREDICTION, &t);
    check(game_program_find(KILN_WORLD_CLIENT, kstr("fuel_network")) == 1 &&
              game_program_find(KILN_WORLD_PREDICTION, kstr("fuel_network")) == -1,
          "the client world has its own directory; prediction none");
    g_live_world[2] = 0;
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_CLIENT);
}

// ---- the players mask and the lab switch ----
void lab_switch() {
    KilnTickInfo t = tick(10);
    check(lab_parallel(1, 100) == KILN_ERR_UNAVAILABLE, "the switch is off without kiln/control/systems.parallel");
    const auto lo = reinterpret_cast<uintptr_t>(g_image_descs);
    game_programs_test_setup(lo, lo + sizeof g_image_descs, true, kMask, true);
    set_mask(0);
    check(lab_parallel(1, 0) == KILN_ERR_INVALID_ARGUMENT && lab_parallel(1, kMaxForcedSteps + 1) == KILN_ERR_INVALID_ARGUMENT,
          "a forced window is 1..3000 steps");
    t_in_program = true;
    check(lab_parallel(1, 3) == KILN_ERR_WRONG_THREAD, "not from inside a program update");
    t_in_program = false;
    check(lab_parallel(1, 3) == KILN_OK && mask() == 0, "on: nothing changes before the next Pre");
    game_programs_pre(KILN_WORLD_SERVER, &t);
    KilnGameProgramsStatus st{};
    st.size = sizeof st;
    game_programs_status(&st);
    check(mask() == 0x8000 && st.players_mask == 0x8000 && st.parallel_forced == 1 && st.parallel_steps_left == 3 &&
              st.parallel_switch == 1,
          "the next Pre sets bit 15 and the status says so");
    set_mask(0x8001);  // player 1 joins meanwhile: the game's own bit
    game_programs_pre(KILN_WORLD_SERVER, &t);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    check(mask() == 0x8001, "held for its steps");
    game_programs_pre(KILN_WORLD_SERVER, &t);
    game_programs_status(&st);
    check(mask() == 0x0001 && st.parallel_forced == 0 && st.players_mask == 0x0001,
          "after 3 steps bit 15 goes, the game's own bit stays");
    set_mask(0);
    lab_parallel(1, 50);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    lab_parallel(0, 0);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    check(mask() == 0, "off lets go in the next Pre");
    set_mask(0x8000);  // a real player 16
    lab_parallel(1, 5);
    for (int i = 0; i < 8; ++i) game_programs_pre(KILN_WORLD_SERVER, &t);
    check(mask() == 0x8000, "a bit 15 the game set is never taken away");
    set_mask(0);
    lab_parallel(1, 5);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    game_programs_status(&st);
    check(st.parallel_forced == 0 && st.players_mask == 0xFFFF, "a stopping world ends the forced window");
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    game_programs_pre(KILN_WORLD_SERVER, &t);
    game_programs_status(&st);
    check(st.players_mask == 0x8000 && st.parallel_forced == 0, "the next world's mask is read, not forced");
    set_mask(0);
}

// ---- wraps over a fake program runner ----
// A fake J: +0 the storage (its kind byte at +8), +0x830 the entity list (16 bytes per entity), +0x838 the count.
struct Job {
    alignas(16) uint8_t bytes[0x840]{};
    uint8_t storage[16]{};
    uint32_t ids[64 * 4]{};
    explicit Job(uint32_t count, uint8_t kind = KILN_WORLD_SERVER) {
        storage[8] = kind;
        uint8_t* st = storage;
        std::memcpy(bytes, &st, 8);
        for (uint32_t i = 0; i < 64; ++i) ids[i * 4] = 100 + i;
        uint32_t* list = ids;
        std::memcpy(bytes + 0x830, &list, 8);
        const uint64_t n = count;
        std::memcpy(bytes + 0x838, &n, 8);
    }
};

std::atomic<uint64_t> g_bad_index{0}, g_mid_start{0};
std::atomic<uint64_t> g_original_runs[8];
std::vector<std::string> g_trace;  // single-threaded rows only
bool g_tracing = false;
void trace(const std::string& s) {
    if (g_tracing) g_trace.push_back(s);
}

// The game's updates: each iterates its whole slice (ctx.index = count), counting its runs; one that starts with an
// index other than 0 would read the wrong entities.
std::atomic<bool> g_slow_update[8];
template <int N>
void game_update(void* raw) {
    if (g_slow_update[N].load()) std::this_thread::sleep_for(std::chrono::microseconds(200));
    auto* ctx = static_cast<ProgramCtx*>(raw);
    if (ctx->index != 0) g_mid_start.fetch_add(1);
    uint64_t count = 0;
    std::memcpy(&count, ctx->job + 0x838, 8);
    ctx->index = static_cast<uint32_t>(count);
    g_original_runs[N].fetch_add(1);
    trace("orig");
}
// What the engine's ProgramRunner does with an entry for one slice: read entry+0x28, call it, check the index.
void run_slice(uintptr_t entry, Job& job) {
    ProgramCtx ctx{job.bytes, 0, 0};
    const auto u = std::atomic_ref<uintptr_t>(*reinterpret_cast<uintptr_t*>(entry + 0x28)).load();
    reinterpret_cast<UpdateFn>(u)(&ctx);
    uint64_t count = 0;
    std::memcpy(&count, job.bytes + 0x838, 8);
    if (ctx.index != 0 && ctx.index < count) g_bad_index.fetch_add(1);
}
uintptr_t update_of(uintptr_t entry) { return std::atomic_ref<uintptr_t>(*reinterpret_cast<uintptr_t*>(entry + 0x28)).load(); }

EngineDesc g_wdesc[8];
alignas(64) uint8_t g_wworld[kStorage + 0x100];
const char* kWNames[8] = {"fuel_setup", "fuel_network", "is_actor_in_shape", "impact_prepare",
                                "despawn_link", "move_colliders", "client_only", "base_comfort"};
UpdateFn const kWUpdates[8] = {&game_update<0>, &game_update<1>, &game_update<2>, &game_update<3>,
                               &game_update<4>, &game_update<5>, nullptr, &game_update<7>};
uintptr_t wentry(int i) { return reinterpret_cast<uintptr_t>(g_wworld) + kTable + static_cast<uintptr_t>(i) * kStride; }

// A world whose table holds the eight programs, `shift` entries down (a later world that inserted programs).
void wworld(int shift) {
    std::memset(g_wworld, 0, sizeof g_wworld);
    for (int i = 0; i < shift; ++i) {
        static EngineDesc filler[8];
        filler[i].update = &update_a;
        g_name_store.emplace_back("inserted_" + std::to_string(i));
        const uintptr_t e = wentry(i);
        const auto d = reinterpret_cast<uintptr_t>(&filler[i]);
        const char* p = g_name_store.back().c_str();
        const uint64_t len = g_name_store.back().size();
        const auto u = reinterpret_cast<uintptr_t>(&update_a);
        std::memcpy(reinterpret_cast<void*>(e), &d, 8);
        std::memcpy(reinterpret_cast<void*>(e + 0x10), &p, 8);
        std::memcpy(reinterpret_cast<void*>(e + 0x18), &len, 8);
        std::memcpy(reinterpret_cast<void*>(e + 0x28), &u, 8);
    }
    for (int i = 0; i < 8; ++i) {
        g_wdesc[i].update = kWUpdates[i];
        const uintptr_t e = wentry(shift + i);
        const auto d = reinterpret_cast<uintptr_t>(&g_wdesc[i]);
        const char* p = kWNames[i];
        const uint64_t len = std::strlen(kWNames[i]);
        const auto u = reinterpret_cast<uintptr_t>(kWUpdates[i]);
        const uint16_t id = static_cast<uint16_t>(shift + i + 1);
        std::memcpy(reinterpret_cast<void*>(e), &d, 8);
        std::memcpy(reinterpret_cast<void*>(e + 8), &id, 2);
        std::memcpy(reinterpret_cast<void*>(e + 0x10), &p, 8);
        std::memcpy(reinterpret_cast<void*>(e + 0x18), &len, 8);
        std::memcpy(reinterpret_cast<void*>(e + 0x28), &u, 8);
    }
    const uint16_t n = static_cast<uint16_t>(shift + 8);
    std::memcpy(g_wworld + kCount, &n, 2);
}

KilnTickInfo g_wtick = tick(1000);
void wpre(uint32_t kind = KILN_WORLD_SERVER) {
    ++g_wtick.step;
    game_programs_pre(kind, &g_wtick);
}
int32_t idx(const char* name) { return game_program_find(KILN_WORLD_SERVER, kstr(name)); }

KilnHandle wrap(KilnOwner owner, const char* program, int32_t stage, int32_t priority, KilnWrapStageFn fn, void* user,
                KilnResult* rc = nullptr) {
    KilnHandle h = 0;
    const KilnResult r = game_program_wrap(owner, KILN_WORLD_SERVER, idx(program), stage, priority, fn, user, &h);
    if (rc) *rc = r;
    return h;
}
KilnGameWrapInfo winfo(KilnHandle h) {
    KilnGameWrapInfo i{};
    i.size = sizeof i;
    game_wrap_info(h, &i);
    return i;
}

void KILN_CALL st_trace(KilnGameSlice* s, void* user) {
    trace(static_cast<const char*>(user));
    if (s->program_ctx == nullptr || !game_in_slice()) trace("bad slice");
}
void KILN_CALL st_fault_mid(KilnGameSlice* s, void*) {  // iterates 3 entities, then faults
    reinterpret_cast<ProgramCtx*>(s->program_ctx)->index = 3;
    trace("fault");
    throw 1;
}
void KILN_CALL st_replace_run_then_fault(KilnGameSlice* s, void*) {
    trace("replace");
    game_slice_run_original(s);
    throw 1;
}
void KILN_CALL st_replace_skip(KilnGameSlice*, void*) { trace("replace"); }
void KILN_CALL st_replace_partial(KilnGameSlice* s, void*) {
    trace("replace");
    reinterpret_cast<ProgramCtx*>(s->program_ctx)->index = 4;  // left mid-way: the engine would abort
}
std::vector<KilnResult> g_run_rcs;
void KILN_CALL st_replace_run_twice(KilnGameSlice* s, void*) {
    g_run_rcs.push_back(game_slice_run_original(s));
    g_run_rcs.push_back(game_slice_run_original(s));
}
void KILN_CALL st_before_runs_original(KilnGameSlice* s, void*) { g_run_rcs.push_back(game_slice_run_original(s)); }
KilnGameSlice g_seen{};
std::string g_seen_name;
std::vector<KilnResult> g_inside;
void KILN_CALL st_look(KilnGameSlice* s, void*) {
    g_seen = *s;
    g_seen_name.assign(s->program.ptr, s->program.len);
    KilnHandle h = 0;
    g_inside.push_back(game_program_wrap(7, KILN_WORLD_SERVER, 0, KILN_WRAP_AFTER, 0, &st_look, nullptr, &h));
    g_inside.push_back(game_program_unwrap(1));
    g_inside.push_back(lab_parallel(1, 5));
    uint32_t pending = 0;
    g_inside.push_back(game_wraps_release(7, &pending));
}
void KILN_CALL st_slow(KilnGameSlice*, void*) { std::this_thread::sleep_for(std::chrono::microseconds(200)); }
void KILN_CALL st_replace_runs_slow_original(KilnGameSlice* s, void*) { game_slice_run_original(s); }
std::atomic<uint64_t> g_before_release{0};
void KILN_CALL st_count_before_release(KilnGameSlice*, void*) { g_before_release.fetch_add(1); }
std::atomic<uint64_t> g_entries{0};
void KILN_CALL st_entry(KilnGameSlice*, void*) {
    g_entries.fetch_add(1);
    std::this_thread::sleep_for(std::chrono::microseconds(20));
}
// A foreign tool that swapped an entry after kiln.world: it calls whatever it found there.
std::atomic<uintptr_t> g_foreign_next{0};
std::atomic<uint64_t> g_foreign_calls{0};
void foreign_update(void* ctx) {
    g_foreign_calls.fetch_add(1);
    if (const uintptr_t n = g_foreign_next.load()) reinterpret_cast<UpdateFn>(n)(ctx);
}

void wraps_basics() {
    wraps_init();
    wworld(0);
    g_live_world[0] = reinterpret_cast<uintptr_t>(g_wworld);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    check(idx("fuel_setup") == 0 && idx("client_only") == 6, "the wrap world's directory");
    const uintptr_t e0 = wentry(0);
    const uintptr_t orig0 = update_of(e0);
    KilnHandle h = 0;
    check(game_program_wrap(0, KILN_WORLD_SERVER, 0, KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT &&
              game_program_wrap(1, KILN_WORLD_PREDICTION, 0, KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT &&
              game_program_wrap(1, KILN_WORLD_SERVER, 0, 3, 0, &st_trace, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT &&
              game_program_wrap(1, KILN_WORLD_SERVER, 0, KILN_WRAP_BEFORE, 0, nullptr, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT,
          "wrap refuses no owner, prediction, a bad stage, no function");
    check(game_program_wrap(1, KILN_WORLD_SERVER, 99, KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &h) == KILN_ERR_NOT_FOUND,
          "wrap refuses an index outside the directory");
    check(game_program_wrap(1, KILN_WORLD_SERVER, idx("client_only"), KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &h) ==
              KILN_ERR_INVALID_ARGUMENT,
          "wrap refuses a program with no update in this world (a client program on the server)");
    check(game_program_wrap(1, KILN_WORLD_CLIENT, 0, KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &h) == KILN_ERR_UNAVAILABLE,
          "a client-world wrap needs systems.wraps.client (no caps table here: refused)");

    // Order: Befores high to low (ties in the order made), the update, Afters in the exact reverse.
    char b5[] = "B5", b0a[] = "B0a", b0b[] = "B0b", bm5[] = "B-5", a5[] = "A5", a0[] = "A0", am5[] = "A-5";
    const KilnHandle hb0a = wrap(1, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, b0a);
    wrap(2, "fuel_setup", KILN_WRAP_BEFORE, 5, &st_trace, b5);
    wrap(1, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, b0b);
    wrap(2, "fuel_setup", KILN_WRAP_BEFORE, -5, &st_trace, bm5);
    wrap(1, "fuel_setup", KILN_WRAP_AFTER, 5, &st_trace, a5);
    wrap(2, "fuel_setup", KILN_WRAP_AFTER, 0, &st_trace, a0);
    wrap(1, "fuel_setup", KILN_WRAP_AFTER, -5, &st_trace, am5);
    check(winfo(hb0a).state == KILN_WRAP_PENDING && update_of(e0) == orig0, "a wrap is pending and nothing is swapped before the Pre");
    wpre();
    check(winfo(hb0a).state == KILN_WRAP_ACTIVE && winfo(hb0a).program_index == 0 && update_of(e0) != orig0 &&
              wrap_original(e0) == orig0 && wrap_original(wentry(1)) == 0,
          "the Pre swaps the entry; wrap_original names the game's update");
    {
        KilnGameProgramInfo pi{};
        pi.size = sizeof pi;
        game_program_info(KILN_WORLD_SERVER, 0, &pi);
        check(pi.wraps == 7, "program info counts its wraps");
    }
    Job job(10);
    g_trace.clear();
    g_tracing = true;
    run_slice(e0, job);
    g_tracing = false;
    const std::vector<std::string> want = {"B5", "B0a", "B0b", "B-5", "orig", "A-5", "A0", "A5"};
    check(g_trace == want, "stage order: Before 5, 0, 0 (made first first), -5; the update; After -5, 0, 5");
    for (auto& [hh, st] : g_stages) game_program_unwrap(hh);
    wpre();
    check(update_of(e0) == orig0 && winfo(hb0a).state == KILN_WRAP_REMOVED && winfo(hb0a).reason == KILN_WRAP_UNWRAPPED &&
              wraps_test_in_flight(0) == 0,
          "the last unwrap puts the game's update back");

    // One Replace per program.
    KilnResult rc = 0;
    const KilnHandle r1 = wrap(1, "fuel_network", KILN_WRAP_REPLACE, 0, &st_replace_skip, nullptr, &rc);
    const KilnHandle r2 = wrap(2, "fuel_network", KILN_WRAP_REPLACE, 0, &st_replace_skip, nullptr, &rc);
    check(r1 && r2 && rc == KILN_OK && winfo(r2).state == KILN_WRAP_REFUSED && winfo(r2).reason == KILN_WRAP_REPLACE_TAKEN &&
              std::string(winfo(r2).detail).find("owner 1 ") != std::string::npos &&
              std::string(winfo(r2).detail).find("wrap " + std::to_string(r1)) != std::string::npos,
          "a second Replace is a refusal: a handle REFUSED at once, REPLACE_TAKEN, naming the owner and wrap that hold it");
    wpre();
    const uintptr_t e1 = wentry(1);
    const uint64_t runs1 = g_original_runs[1].load();
    for (int i = 0; i < 300; ++i) run_slice(e1, job);
    check(g_original_runs[1].load() == runs1 && g_bad_index.load() == 0,
          "replace: the original ran 0 times in 300 slices, the runner accepted every index");
    game_program_unwrap(r1);
    const KilnHandle r3 = wrap(2, "fuel_network", KILN_WRAP_REPLACE, 0, &st_replace_run_twice, nullptr, &rc);
    const KilnHandle bro = wrap(2, "fuel_network", KILN_WRAP_BEFORE, 0, &st_before_runs_original, nullptr);
    check(r3 != 0, "another owner may replace it once the first's unwrap is queued (the Pre swaps them in order)");
    wpre();
    check(winfo(r1).state == KILN_WRAP_REMOVED && winfo(r3).state == KILN_WRAP_ACTIVE, "one Replace out, the next in, in one Pre");
    g_run_rcs.clear();
    run_slice(e1, job);
    check(g_run_rcs.size() == 3 && g_run_rcs[0] == KILN_ERR_INVALID_ARGUMENT && g_run_rcs[1] == KILN_OK &&
              g_run_rcs[2] == KILN_ERR_INVALID_ARGUMENT && g_original_runs[1].load() == runs1 + 1,
          "run_original: refused in a Before, once in a Replace, never twice");
    KilnGameSlice outside{};
    outside.size = sizeof outside;
    check(game_slice_run_original(&outside) == KILN_ERR_WRONG_THREAD, "run_original outside a stage is refused");
    game_program_unwrap(r3);
    game_program_unwrap(bro);
    wpre();

    // Faults: a Before that faults mid-iteration; a Replace that faults after running the original; a Replace that
    // leaves a partial iteration.
    const uintptr_t e2 = wentry(2);
    const KilnHandle bf = wrap(1, "is_actor_in_shape", KILN_WRAP_BEFORE, 0, &st_fault_mid, nullptr);
    const KilnHandle ok_after = wrap(1, "is_actor_in_shape", KILN_WRAP_AFTER, 0, &st_trace, a0);
    wpre();
    const uint64_t runs2 = g_original_runs[2].load(), mid = g_mid_start.load(), bad = g_bad_index.load();
    g_trace.clear();
    g_tracing = true;
    run_slice(e2, job);
    run_slice(e2, job);
    g_tracing = false;
    check(g_original_runs[2].load() == runs2 + 2 && g_mid_start.load() == mid && g_bad_index.load() == bad,
          "a Before that faulted mid-iteration: the update still starts at 0, the index ends right");
    check(g_trace == std::vector<std::string>({"fault", "orig", "A0", "orig", "A0"}) && winfo(bf).disabled == KILN_WRAP_FAULT &&
              winfo(bf).faults == 1 && winfo(bf).fault_code == 0xC0000005u && winfo(ok_after).calls == 2,
          "the faulting stage is off from its fault on; the others run");
    wpre();
    check(winfo(bf).state == KILN_WRAP_ACTIVE && std::string(winfo(bf).detail).find("faulted") != std::string::npos,
          "the next Pre logs it once and says why");
    game_program_unwrap(bf);
    game_program_unwrap(ok_after);
    wpre();
    const KilnHandle rf = wrap(1, "is_actor_in_shape", KILN_WRAP_REPLACE, 0, &st_replace_run_then_fault, nullptr);
    wpre();
    const uint64_t runs2b = g_original_runs[2].load();
    run_slice(e2, job);
    check(g_original_runs[2].load() == runs2b + 1 && g_bad_index.load() == bad && winfo(rf).disabled == KILN_WRAP_FAULT,
          "a fault after RunOriginal: the update is not run again, the index is the count");
    run_slice(e2, job);
    check(g_original_runs[2].load() == runs2b + 2, "the slice after that is the game's own");
    game_program_unwrap(rf);
    wpre();
    const KilnHandle rp = wrap(1, "is_actor_in_shape", KILN_WRAP_REPLACE, 0, &st_replace_partial, nullptr);
    wpre();
    run_slice(e2, job);
    check(g_bad_index.load() == bad && winfo(rp).disabled == KILN_WRAP_OK, "a Replace that stops mid-slice: the index is finished");
    game_program_unwrap(rp);
    wpre();

    // The slice a stage sees, and kiln.world's calls from inside a stage.
    const KilnHandle look = wrap(1, "impact_prepare", KILN_WRAP_BEFORE, 0, &st_look, nullptr);
    wpre();
    g_inside.clear();
    run_slice(wentry(3), job);
    check(g_seen.size == sizeof(KilnGameSlice) && g_seen.count == 10 && g_seen.entity_stride == 16 && g_seen.entity_ids &&
              g_seen.entity_ids[0] == 100 && g_seen.entity_ids[4] == 101 && g_seen_name == "impact_prepare" &&
              g_seen.program_index == 3 && g_seen.step == g_wtick.step && g_seen.thread_is_world == 1 &&
              g_seen.world_kind == KILN_WORLD_SERVER && g_seen.stage == KILN_WRAP_BEFORE,
          "the slice: count, entity ids (16 bytes apart), name, index, step, the world's thread");
    std::thread([&] { run_slice(wentry(3), job); }).join();
    check(g_seen.thread_is_world == 0, "a slice on a worker says so");
    check(g_inside.size() >= 4 && std::all_of(g_inside.begin(), g_inside.end(), [](KilnResult r) { return r == KILN_ERR_WRONG_THREAD; }) &&
              !game_in_slice(),
          "wrap, unwrap, the lab switch and release refuse from inside a stage");
    // the prediction forward: a slice of another storage kind goes to the update untouched
    Job pred(10, KILN_WORLD_PREDICTION);
    const uint64_t calls = winfo(look).calls, runs3 = g_original_runs[3].load();
    run_slice(wentry(3), pred);
    KilnGameProgramInfo pi{};
    pi.size = sizeof pi;
    game_program_info(KILN_WORLD_SERVER, 3, &pi);
    check(winfo(look).calls == calls && g_original_runs[3].load() == runs3 + 1 && pi.forwarded == 1 && pi.slices == 2 &&
              wraps_test_in_flight(0) == 0,
          "a slice of another storage kind is forwarded, counted, not dispatched; in-flight back to 0");
    game_program_unwrap(look);
    wpre();

    // Budget: 10 slices in a row over it turn the stage off.
    wraps_test_budget_ns(50000);  // 50 us
    const KilnHandle slow = wrap(1, "despawn_link", KILN_WRAP_AFTER, 0, &st_slow, nullptr);
    wpre();
    for (int i = 0; i < 12; ++i) run_slice(wentry(4), job);
    check(winfo(slow).disabled == KILN_WRAP_BUDGET && winfo(slow).calls == 10 && winfo(slow).overruns == 10,
          "10 slices in a row over the budget disable the stage");
    game_program_unwrap(slow);
    wpre();
    // A Replace is charged its own time, not the game's update it runs through game_slice_run_original.
    g_slow_update[4] = true;
    const KilnHandle rs = wrap(1, "despawn_link", KILN_WRAP_REPLACE, 0, &st_replace_runs_slow_original, nullptr);
    wpre();
    for (int i = 0; i < 12; ++i) run_slice(wentry(4), job);
    check(winfo(rs).disabled == KILN_WRAP_OK && winfo(rs).calls == 12 && winfo(rs).overruns == 0 &&
              winfo(rs).total_ns < 12ull * 150000,
          "a Replace whose game update (run through it) is slow is not charged for it: not disabled, its own time only");
    game_program_unwrap(rs);
    wpre();
    g_slow_update[4] = false;
    const KilnHandle rslow = wrap(1, "despawn_link", KILN_WRAP_REPLACE, 0, &st_slow, nullptr);
    wpre();
    for (int i = 0; i < 12; ++i) run_slice(wentry(4), job);
    check(winfo(rslow).disabled == KILN_WRAP_BUDGET, "a Replace that is slow itself is disabled by its budget");
    wraps_test_budget_ns(1000000);
    game_program_unwrap(rslow);
    wpre();
}

// ---- in_program (appended in 0.2.0-dev.28): a Kiln program's update or a wrap stage on this thread ----
std::atomic<uint32_t> g_ip_calls{0}, g_ip_inside{0}, g_ip_other{0};
void KILN_CALL st_in_program(KilnGameSlice*, void*) {
    g_ip_calls.fetch_add(1);
    if (in_program() == 1) g_ip_inside.fetch_add(1);
    uint32_t other = 9;
    std::thread([&] { other = in_program(); }).join();  // another thread while this one is inside the stage
    if (other == 0) g_ip_other.fetch_add(1);
}
void KILN_CALL st_in_program_fault(KilnGameSlice*, void*) {
    if (in_program() == 1) g_ip_inside.fetch_add(1);
    throw 1;
}

void in_program_rows() {
    check(in_program() == 0, "in_program: 0 outside any program update or stage");
    // A Kiln program's update: programs.cpp's dispatch sets t_in_program around it (programs.cpp is not compiled here).
    t_in_program = true;
    uint32_t other = 9;
    std::thread([&] { other = in_program(); }).join();
    check(in_program() == 1 && other == 0, "in_program: 1 inside a Kiln program's update, 0 on another thread meanwhile");
    t_in_program = false;
    check(in_program() == 0, "in_program: 0 again after the update");

    const int32_t i = idx("impact_prepare");
    const KilnHandle b = wrap(90, "impact_prepare", KILN_WRAP_BEFORE, 0, &st_in_program, nullptr);
    const KilnHandle a = wrap(90, "impact_prepare", KILN_WRAP_AFTER, 0, &st_in_program, nullptr);
    wpre();
    Job job(10);
    run_slice(wentry(i), job);
    check(winfo(b).state == KILN_WRAP_ACTIVE && g_ip_calls.load() == 2 && g_ip_inside.load() == 2 && g_ip_other.load() == 2 &&
              in_program() == 0,
          "in_program: 1 inside a Before and an After stage, 0 on another thread meanwhile, 0 after the slice");
    uint32_t after = 9;
    std::thread worker([&] {
        Job wjob(10);
        run_slice(wentry(i), wjob);
        after = in_program();
    });
    worker.join();
    check(g_ip_calls.load() == 4 && g_ip_inside.load() == 4 && g_ip_other.load() == 4 && after == 0 && in_program() == 0,
          "in_program: 1 inside the stages of a slice on a worker thread, 0 there after the slice");
    game_program_unwrap(b);
    game_program_unwrap(a);
    const KilnHandle f = wrap(90, "impact_prepare", KILN_WRAP_BEFORE, 0, &st_in_program_fault, nullptr);
    wpre();
    run_slice(wentry(i), job);
    check(winfo(f).faults == 1 && g_ip_inside.load() == 5 && in_program() == 0, "in_program: 0 after a stage that faulted");
    game_program_unwrap(f);
    wpre();

    // kiln.types' in_system_callback (0.2.0-dev.28), resolved in on_ready: a system_hook callback counts as inside too
    static uint32_t types_says = 0;
    KilnTypesApi types{};
    types.header = {sizeof(KilnTypesApi), KILN_TYPES_VERSION};
    types.in_system_callback = [] { return types_says; };
    types_says = 1;
    in_program_resolve(&types);
    check(in_program() == 1, "in_program: 1 while kiln.types says a system_hook callback runs on this thread");
    types_says = 0;
    check(in_program() == 0, "in_program: 0 when kiln.types says none runs");
    types_says = 1;
    types.header.size = offsetof(KilnTypesApi, in_system_callback);  // an older kiln.types: the slot is not its
    in_program_resolve(&types);
    check(in_program() == 0, "in_program: a kiln.types table too small for in_system_callback is not asked");
    types.header.size = sizeof(KilnTypesApi);
    types.in_system_callback = nullptr;
    in_program_resolve(&types);
    const uint32_t no_member = in_program();
    in_program_resolve(nullptr);
    check(no_member == 0 && in_program() == 0, "in_program: no member, or no kiln.types: that part answers 0");
}

void wraps_foreign() {
    // Another tool's pointer in the entry before kiln.world: refused, never stacked on.
    const uintptr_t e5 = wentry(5);
    const uintptr_t orig5 = update_of(e5);
    std::atomic_ref<uintptr_t>(*reinterpret_cast<uintptr_t*>(e5 + 0x28)).store(reinterpret_cast<uintptr_t>(&foreign_update));
    g_foreign_next = orig5;
    const KilnHandle f1 = wrap(1, "move_colliders", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    wpre();
    check(winfo(f1).state == KILN_WRAP_REFUSED && winfo(f1).reason == KILN_WRAP_FOREIGN &&
              update_of(e5) == reinterpret_cast<uintptr_t>(&foreign_update),
          "a program another tool swapped is refused, its pointer left alone");
    std::atomic_ref<uintptr_t>(*reinterpret_cast<uintptr_t*>(e5 + 0x28)).store(orig5);
    // ... and one that swaps over kiln.world's trampoline: the unwrap leaves it, the trampoline keeps forwarding.
    char tag[] = "B";
    const KilnHandle f2 = wrap(1, "move_colliders", KILN_WRAP_BEFORE, 0, &st_trace, tag);
    wpre();
    const uintptr_t ours = update_of(e5);
    g_foreign_next = ours;
    std::atomic_ref<uintptr_t>(*reinterpret_cast<uintptr_t*>(e5 + 0x28)).store(reinterpret_cast<uintptr_t>(&foreign_update));
    game_program_unwrap(f2);
    wpre();
    Job job(10);
    const uint64_t runs = g_original_runs[5].load(), fc = g_foreign_calls.load();
    g_trace.clear();
    g_tracing = true;
    run_slice(e5, job);
    g_tracing = false;
    check(winfo(f2).state == KILN_WRAP_REMOVED && update_of(e5) == reinterpret_cast<uintptr_t>(&foreign_update) &&
              g_foreign_calls.load() == fc + 1 && g_original_runs[5].load() == runs + 1 &&
              g_trace == std::vector<std::string>({"orig"}) && wraps_test_in_flight(0) == 0,
          "a trampoline under another tool's swap is not taken back; it forwards (empty chain), in-flight back to 0");
    const KilnHandle f3 = wrap(1, "move_colliders", KILN_WRAP_AFTER, 0, &st_trace, nullptr);
    wpre();
    check(winfo(f3).state == KILN_WRAP_REFUSED && winfo(f3).reason == KILN_WRAP_FOREIGN, "and the program is refused from then on");
    int pool = -1, slot = -1;
    check(tramp_of(ours, &pool, &slot) && g_pools[pool].slots[slot].state == S_RETAINED, "its slot is never used again");
    std::atomic_ref<uintptr_t>(*reinterpret_cast<uintptr_t*>(e5 + 0x28)).store(orig5);
}

void wraps_worlds() {
    // A stopping world: entries back, wraps pending; the next world (programs moved) gets them again by name.
    char tag[] = "B";
    const KilnHandle w = wrap(1, "base_comfort", KILN_WRAP_BEFORE, 0, &st_trace, tag);
    wpre();
    const uintptr_t e7 = wentry(7), orig7 = reinterpret_cast<uintptr_t>(kWUpdates[7]);
    check(update_of(e7) != orig7, "wrapped");
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    check(update_of(e7) == orig7 && winfo(w).state == KILN_WRAP_PENDING && winfo(w).program_index == -1,
          "a stopping world gets its updates back; the wrap waits for the next world");
    wworld(3);  // the next world inserted three programs before them
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    const uintptr_t moved = wentry(3 + 7);
    check(winfo(w).state == KILN_WRAP_ACTIVE && winfo(w).program_index == 10 && wrap_original(moved) == orig7,
          "the next world: applied again by name, at the program's new index");
    // A new world without a stop of the last: nothing of the old table is written; wraps applied again.
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wworld(0);
    wpre();
    check(winfo(w).state == KILN_WRAP_ACTIVE && winfo(w).program_index == 7 && wrap_original(wentry(7)) == orig7,
          "a world that started without a stop of the last: applied again");
    // A program that a later world does not have: refused.
    const KilnHandle gone = wrap(1, "despawn_link", KILN_WRAP_BEFORE, 0, &st_trace, tag);
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    const char* keep = kWNames[4];
    kWNames[4] = "renamed_program";
    wworld(0);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    kWNames[4] = keep;
    check(winfo(gone).state == KILN_WRAP_REFUSED && winfo(gone).reason == KILN_WRAP_NO_PROGRAM, "a program the new world lacks: refused");
    game_program_unwrap(w);
    wpre();
    check(update_of(wentry(7)) == orig7, "unwrapped in the new world");
}

// Native SET (kiln.types' external_runner): no new wrap; the live ones go in the next Pre, entries restored.
void wraps_native_set() {
    const KilnHandle a = wrap(50, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    const KilnHandle b = wrap(50, "fuel_network", KILN_WRAP_REPLACE, 0, &st_replace_skip, nullptr);
    wpre();
    const KilnHandle c = wrap(50, "impact_prepare", KILN_WRAP_AFTER, 0, &st_trace, nullptr);  // still pending
    check(winfo(a).state == KILN_WRAP_ACTIVE && winfo(b).state == KILN_WRAP_ACTIVE && winfo(c).state == KILN_WRAP_PENDING,
          "SET: two wraps live, one pending");
    g_fake_external = 1;
    KilnResult rc = 0;
    check(!wrap(51, "base_comfort", KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &rc) && rc == KILN_ERR_UNAVAILABLE,
          "SET: a wrap request while native SET is loaded is refused (unavailable)");
    wpre();
    KilnGameProgramsStatus st{};
    st.size = sizeof st;
    game_programs_status(&st);
    check(winfo(a).state == KILN_WRAP_REMOVED && winfo(a).reason == KILN_WRAP_NATIVE_SET &&
              winfo(b).reason == KILN_WRAP_NATIVE_SET && winfo(c).state == KILN_WRAP_REMOVED &&
              winfo(c).reason == KILN_WRAP_NATIVE_SET && st.wraps_active == 0 && st.wraps_pending == 0 &&
              std::string(st.detail).find("native SET") != std::string::npos,
          "SET: every wrap is removed in one Pre, the status says why");
    check(update_of(wentry(0)) == reinterpret_cast<uintptr_t>(kWUpdates[0]) &&
              update_of(wentry(1)) == reinterpret_cast<uintptr_t>(kWUpdates[1]) && wrap_original(wentry(0)) == 0,
          "SET: the entries hold the game's updates again (SET names programs by them)");
    g_fake_external = 2;
    check(!wrap(51, "base_comfort", KILN_WRAP_BEFORE, 0, &st_trace, nullptr, &rc) && rc == KILN_ERR_UNAVAILABLE,
          "SET: modules that could not be read also refuse wraps");
    g_fake_external = 0;
    wpre();
    const KilnHandle d = wrap(51, "base_comfort", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    wpre();
    check(d && winfo(d).state == KILN_WRAP_ACTIVE && winfo(a).state == KILN_WRAP_REMOVED,
          "SET gone: new wraps are taken again, the removed ones stay removed");
    game_program_unwrap(d);
    wpre();
}

void wraps_owners() {
    // release: no call starts after it returns (nothing runs here: out_pending 0 at once); the chains drop them in
    // the world's Pre
    const KilnHandle a = wrap(40, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    const KilnHandle b = wrap(40, "fuel_network", KILN_WRAP_AFTER, 0, &st_trace, nullptr);
    wpre();
    const KilnHandle c = wrap(40, "impact_prepare", KILN_WRAP_AFTER, 0, &st_trace, nullptr);
    uint32_t pending = 99;
    check(game_wraps_release(40, &pending) == KILN_OK && pending == 0 && winfo(c).state == KILN_WRAP_REMOVED &&
              winfo(a).remove_queued == 1 && winfo(a).gone == 1 && winfo(b).gone == 1,
          "release: a pending wrap goes at once, the live ones are gone (no call starts), out_pending 0");
    {
        Job job(10);
        g_trace.clear();
        g_tracing = true;
        run_slice(wentry(0), job);
        g_tracing = false;
        check(g_trace == std::vector<std::string>({"orig"}), "a released stage is not called before the Pre either");
    }
    wpre();
    check(game_wraps_release(40, &pending) == KILN_OK && pending == 0 && winfo(a).state == KILN_WRAP_REMOVED &&
              winfo(b).state == KILN_WRAP_REMOVED && update_of(wentry(0)) == reinterpret_cast<uintptr_t>(kWUpdates[0]),
          "after the Pre nothing of the owner can run");
    // a revoked owner, told by kiln.hooks' owner cleanup: its stages are gone at once (no Pre, no probe), out of the
    // chain in the next Pre; a pending one is removed at once
    check(g_retiring_subscribed && g_retiring_fn && g_retiring_observer == g_owner, "kiln.world subscribes to owner cleanup");
    const KilnHandle d = wrap(41, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    wpre();
    const KilnHandle e = wrap(41, "fuel_network", KILN_WRAP_AFTER, 0, &st_trace, nullptr);
    g_retiring_fn(41, g_retiring_user);
    {
        Job job(10);
        g_trace.clear();
        g_tracing = true;
        run_slice(wentry(0), job);
        g_tracing = false;
        check(winfo(d).gone == 1 && winfo(e).state == KILN_WRAP_REMOVED && winfo(e).reason == KILN_WRAP_OWNER_GONE &&
                  g_trace == std::vector<std::string>({"orig"}),
              "a retiring owner's stages are gone at once: not called before the Pre, its pending wrap removed");
    }
    wpre();
    check(winfo(d).state == KILN_WRAP_REMOVED && winfo(d).reason == KILN_WRAP_OWNER_GONE, "and out of the chain in the Pre");
    // without kiln.hooks' owner cleanup (an older core): the probe, once a second
    g_hooks_on = false;
    wraps_init();
    check(!g_retiring_subscribed, "no owner cleanup: kiln.world falls back to the probe");
    g_fake.post = [](KilnOwner owner, KilnTaskFn, void*) -> KilnResult { return owner == 42 ? KILN_ERR_REVOKED : KILN_OK; };
    const KilnHandle f = wrap(42, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    wpre();
    g_next_reap = {};
    wpre();
    g_next_reap = {};
    wpre();
    check(winfo(f).state == KILN_WRAP_REMOVED && winfo(f).reason == KILN_WRAP_OWNER_GONE, "the probe finds a revoked owner's wraps");
    g_hooks_on = true;
    wraps_init();
}

// No call starts into a released owner's stages, also while its world keeps stepping (no Pre in between); a call that
// had started is counted until it returns.
void release_under_load() {
    const KilnHandle h = wrap(80, "fuel_network", KILN_WRAP_AFTER, 0, &st_entry, nullptr);
    const KilnHandle other = wrap(81, "fuel_network", KILN_WRAP_BEFORE, 0, &st_count_before_release, nullptr);
    wpre();
    std::atomic<bool> stop{false};
    std::vector<std::thread> workers;
    for (int t = 0; t < 8; ++t)
        workers.emplace_back([&] {
            Job job(4);
            while (!stop.load(std::memory_order_relaxed)) run_slice(wentry(1), job);
        });
    while (g_entries.load() < 2000) std::this_thread::yield();
    uint32_t pending = 99;
    game_wraps_release(80, &pending);
    const uint64_t at_return = g_entries.load();
    const uint32_t running_at_return = winfo(h).running;
    int polls = 0;
    while (pending && polls < 100000) game_wraps_release(80, &pending), ++polls;
    const uint64_t at_zero = g_entries.load();
    const uint64_t others = g_before_release.load();
    std::this_thread::sleep_for(std::chrono::milliseconds(50));  // 8 threads keep running slices of the program
    const uint64_t later = g_entries.load();
    const uint64_t others_later = g_before_release.load();
    stop = true;
    for (auto& w : workers) w.join();
    check(pending == 0 && later == at_zero && at_zero - at_return <= 8 && running_at_return <= 8,
          "release under 8 threads: in-flight calls finish, then 0 entries in 50 ms of slices; out_pending reached 0 "
          "with no Pre (the world kept stepping)");
    check(others_later > others, "the other owner's stage on the same program kept running");
    check(winfo(h).state == KILN_WRAP_ACTIVE && winfo(h).gone == 1 && winfo(h).remove_queued == 1 && winfo(h).running == 0,
          "released: still in the chain until the Pre, gone, nothing running");
    wpre();
    check(winfo(h).state == KILN_WRAP_REMOVED && winfo(other).state == KILN_WRAP_ACTIVE, "the Pre takes it out of the chain");
    // a single unwrap: the same gate
    g_entries = 0;
    const KilnHandle u = wrap(82, "fuel_network", KILN_WRAP_AFTER, 0, &st_entry, nullptr);
    wpre();
    stop = false;
    workers.clear();
    for (int t = 0; t < 8; ++t)
        workers.emplace_back([&] {
            Job job(4);
            while (!stop.load(std::memory_order_relaxed)) run_slice(wentry(1), job);
        });
    while (g_entries.load() < 500) std::this_thread::yield();
    game_program_unwrap(u);
    for (int i = 0; i < 2000000 && winfo(u).running; ++i) std::this_thread::yield();
    const uint64_t done = g_entries.load();
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    stop = true;
    for (auto& w : workers) w.join();
    check(g_entries.load() == done, "unwrap: once nothing runs, no call starts (30 ms of slices on 8 threads)");
    game_program_unwrap(other);
    wpre();
    check(wraps_test_in_flight(0) == 0 && update_of(wentry(1)) == reinterpret_cast<uintptr_t>(kWUpdates[1]),
          "in-flight 0 and the update back after the Pre");
}

// STOPPING must wait for an entered stage without holding the registry lock it may need.
void stopping_under_load() {
    wworld(0);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    struct Blocked { std::atomic<bool> entered{false}, release{false}; std::atomic<int> calls{0}; KilnHandle h = 0; } b;
    b.h = wrap(91, "fuel_network", KILN_WRAP_BEFORE, 0, [](KilnGameSlice*, void* user) {
        auto& b = *static_cast<Blocked*>(user);
        ++b.calls;
        b.entered = true;
        while (!b.release.load()) std::this_thread::yield();
        KilnGameWrapInfo info{};
        info.size = sizeof info;
        game_wrap_info(b.h, &info); // needs g_mu: STOPPING must not hold it while waiting
    }, &b);
    wpre();
    const auto trampoline = reinterpret_cast<UpdateFn>(update_of(wentry(1)));
    std::thread worker([&] { Job j(3); run_slice(wentry(1), j); });
    while (!b.entered.load()) std::this_thread::yield();
    g_wrap_caps = &g_stop_caps;  // the core says the engine drained the server world's jobs (and yet a call is in flight)
    g_drain[KILN_WORLD_SERVER] = 1;
    const int contradictions = logs_with("wrap calls were in flight although the engine drains");
    std::atomic<bool> stopped{false};
    std::thread stopper([&] { world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER); stopped = true; });
    auto& entry = *reinterpret_cast<uintptr_t*>(wentry(1) + kEntryUpdateAt);
    while (std::atomic_ref<uintptr_t>(entry).load() == reinterpret_cast<uintptr_t>(trampoline)) std::this_thread::yield();
    std::this_thread::sleep_for(std::chrono::milliseconds(20));
    check(!stopped.load(), "STOPPING waits for a blocked stage before releasing world state");
    // A runner that already fetched the old pointer still forwards after STOPPING.
    Job late(3);
    ProgramCtx ctx{late.bytes, 0, 0};
    trampoline(&ctx);
    check(b.calls.load() == 1, "a late trampoline entry after STOPPING starts no stage");
    b.release = true;
    worker.join();
    stopper.join();
    check(stopped.load() && wraps_test_in_flight(0) == 0 && winfo(b.h).state == KILN_WRAP_PENDING,
          "blocked stage finishes normally; stop drains and returns it to pending");
    check(std::string(winfo(b.h).detail).rfind("world stop: 1 in flight at the stop, 0 after the wait (", 0) == 0,
          "a stage back in PENDING says what the stop's drain found (1 call in flight at the stop, none after the wait)");
    check(std::string(winfo(b.h).detail).find("; drained by the engine") != std::string::npos &&
              logs_with("the engine drains the world's jobs first (world.stop_drain.server)") >= 1,
          "the stage's detail and the stop line say the core's guarantee (world.stop_drain.server)");
    check(logs_with("1 wrap calls were in flight although the engine drains") == contradictions + 1,
          "a call in flight at a stop the engine drained is logged as an error");
    g_drain[KILN_WORLD_SERVER] = 0;
    game_program_unwrap(b.h);
    check(std::string(winfo(b.h).detail) == "unwrapped", "once removed its detail is the removal's again");
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
}

void stopping_timeout() {
    struct Blocked { std::atomic<bool> entered{false}, release{false}; } b;
    const auto h = wrap(93, "fuel_network", KILN_WRAP_BEFORE, 0, [](KilnGameSlice*, void* user) {
        auto& b = *static_cast<Blocked*>(user);
        b.entered = true;
        while (!b.release.load()) std::this_thread::yield();
    }, &b);
    wpre();
    std::thread worker([&] { Job j(3); run_slice(wentry(1), j); });
    while (!b.entered.load()) std::this_thread::yield();
    g_stop_wait_ms = 10; // same production branch, a short test bound
    const auto logs = g_stop_timeout_logs.load();
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    check(g_stop_timeout_logs.load() == logs + 1 && winfo(h).running == 1 && !g_retired.empty(),
          "bounded stop logs once and retains the chain while a callback still runs");
    check(std::string(winfo(h).detail).rfind("world stop: 1 in flight at the stop, 1 after the wait (", 0) == 0 &&
              std::string(winfo(h).detail).find("; bounded wait only") != std::string::npos,
          "a timed-out drain says so in the waiting stage's detail (1 call still in flight after the wait; bounded wait only)");
    b.release = true;
    worker.join();
    game_program_unwrap(h);
    g_stop_wait_ms = 2000;
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    check(g_retired.empty(), "timed-out stop's chain is reclaimed only after its callback exits");
    g_wrap_caps = nullptr;
    g_drain[KILN_WORLD_SERVER] = -1;
}

// The core's tick faulted (tick.cpp disable(): every world pointer cleared, no STOPPING will come): a slice that finds no
// world of its kind closes the pool's admission, logs it once, and the game's update runs untouched from then on.
std::atomic<int> g_fault_calls{0};
void tick_fault_closes_admission() {
    wworld(0);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    const auto h = wrap(97, "fuel_network", KILN_WRAP_BEFORE, 0, [](KilnGameSlice*, void*) { ++g_fault_calls; }, nullptr);
    wpre();
    Job j(3);
    run_slice(wentry(1), j);
    check(g_fault_calls.load() == 1 && winfo(h).state == KILN_WRAP_ACTIVE, "tick fault: the stage runs while the tick works");
    const auto runs = g_original_runs[1].load();
    const auto bad = g_bad_index.load();
    const int logged = logs_with("wrap admission is closed");
    const uintptr_t live = g_live_world[0];
    const auto saved_caps = g_wrap_caps;
    static uint32_t fault_row = KILN_CAP_AVAILABLE, fault_reason = 0;
    auto caps = g_stop_caps;
    caps.report = [](KilnOwner, KilnStr name, uint32_t state, uint32_t reason, KilnStr) -> KilnResult {
        if (std::string_view(name.ptr, name.len) == "systems.wraps") { fault_row = state; fault_reason = reason; }
        return KILN_OK;
    };
    g_wrap_caps = &caps;
    g_live_world[0] = 0;  // disable(): Kiln no longer knows the world, and no STOPPING follows
    run_slice(wentry(1), j);
    run_slice(wentry(1), j);
    check(g_fault_calls.load() == 1 && g_original_runs[1].load() == runs + 2 && g_bad_index.load() == bad,
          "tick fault: no stage starts once the world is unknown; the game's update runs untouched");
    check(logs_with("wrap admission is closed") == logged + 1 && logs_with("its tick hook faulted") >= 1,
          "tick fault: the closed admission is logged once for the world kind");
    check(fault_row == KILN_CAP_UNAVAILABLE && fault_reason == KILN_CAP_REASON_FAULTED,
          "tick fault: the wrap capability stops advertising runnable callbacks when admission closes");
    g_wrap_caps = saved_caps;
    g_live_world[0] = live;
    run_slice(wentry(1), j);
    check(g_fault_calls.load() == 1, "tick fault: admission stays closed (a faulted tick sends no STARTED)");
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    game_program_unwrap(h);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    check(update_of(wentry(1)) == reinterpret_cast<uintptr_t>(kWUpdates[1]), "tick fault row: the entry is the game's again");
}

void retirement_under_load() {
    struct Blocked { std::atomic<bool> entered{false}, release{false}; std::atomic<int> calls{0}, exits{0}; } b;
    const KilnHandle h = wrap(92, "fuel_network", KILN_WRAP_BEFORE, 0, [](KilnGameSlice*, void* user) {
        auto& b = *static_cast<Blocked*>(user);
        ++b.calls;
        b.entered = true;
        while (!b.release.load()) std::this_thread::yield();
        ++b.exits;
    }, &b);
    wpre();
    std::thread worker([&] { Job j(3); run_slice(wentry(1), j); });
    while (!b.entered.load()) std::this_thread::yield();
    g_retiring_fn(92, g_retiring_user); // synchronous admission closure, never a synchronous drain
    Job later(3);
    for (int i = 0; i < 20; ++i) run_slice(wentry(1), later);
    check(b.calls.load() == 1 && b.exits.load() == 0 && winfo(h).running == 1,
          "owner retirement starts no new callback while the existing one is blocked");
    b.release = true;
    worker.join();
    uint32_t pending = 99;
    game_wraps_release(92, &pending);
    check(b.exits.load() == 1 && pending == 0, "retired callback finishes normally before its user data can be freed");
    wpre();
}

// ---- per entity (S4) ----
// The fake engine's iterator for fuel_setup: [ctx][T FuelSetup][R CurrentTransform][W DynamicFuelSetup x6][W Fuel x6]
// (type indices FuelSetup 3, CurrentTransform 5, DynamicFuelSetup 10, Fuel 20: the WRITE group is NOT descriptor
// order, which lists Fuel first). Each slot of row r points at base(slot) + 64 r; a write record's tracker is rec[5].
constexpr uint32_t kRowStride = 64;
uintptr_t g_slot_override[32];  // a slot whose rows point into real memory (base + 64 r)
uintptr_t slot_base(int slot) {
    return g_slot_override[slot] ? g_slot_override[slot] : 0x7000000ull + 0x10000ull * static_cast<unsigned>(slot);
}
std::atomic<int> g_marks{0};
std::atomic<uint64_t> g_mark_rec{0};
void fake_iter_begin(void*, void* iter, uint32_t size) {
    std::memset(iter, 0, size);
}
bool fake_iter_next(void* raw, void* iter, uint32_t size) {
    auto* ctx = static_cast<ProgramCtx*>(raw);
    uint64_t count = 0;
    std::memcpy(&count, ctx->job + 0x838, 8);
    if (ctx->index >= count) return false;
    auto* it = static_cast<uint64_t*>(iter);
    const uint32_t r = ctx->index;
    for (uint32_t k = 1; k < size / 8; ++k) it[k] = slot_base(static_cast<int>(k)) + uint64_t{kRowStride} * r;
    it[3 + 5] = 0xAA00 + 3;  // trackers of the two write records
    it[9 + 5] = 0xAA00 + 9;
    if (g_slot_override[15]) it[15] = g_slot_override[15];  // the singleton: one object, not per row
    ctx->index = r + 1;
    return true;
}
uint32_t* fake_current_entity(void* raw, uint32_t* out) {
    auto* ctx = static_cast<ProgramCtx*>(raw);
    const uint32_t* ids = nullptr;
    std::memcpy(&ids, ctx->job + 0x830, sizeof ids);
    *out = ctx->index ? ids[(ctx->index - 1) * 4] : 0;
    return out;
}
void fake_mark(void* tracker, void*, void*, void*, uint16_t, uint8_t) {
    g_marks.fetch_add(1);
    g_mark_rec = reinterpret_cast<uintptr_t>(tracker);
}

// The game's attribute formulas: a Fuel block {u32 root; u16 offset; u8 flags; u8 count; s32 current; s32 max} is clamped.
std::atomic<int> g_attr_calls{0};
uintptr_t g_attr_registry = 0;
void fake_attr_changed(void* component, void* registry) {
    g_attr_calls.fetch_add(1);
    g_attr_registry = reinterpret_cast<uintptr_t>(registry);
    auto* c = static_cast<uint8_t*>(component);
    int32_t cur = 0, max = 0;
    std::memcpy(&cur, c + 8, 4);
    std::memcpy(&max, c + 12, 4);
    if (cur > max) std::memcpy(c + 8, &max, 4);
}
std::vector<KilnResult> g_attr_rcs;
uint32_t KILN_CALL each_attr(uint64_t* iter, uint32_t row, KilnEntity, void* user) {
    auto* sl = static_cast<KilnGameSlice*>(user);
    if (row != 0) return 1;
    auto* fuel = static_cast<uint8_t*>(game_slice_slot(sl, iter, KILN_GAME_LIST_WRITE, 0, nullptr, nullptr));
    if (!fuel) {
        g_attr_rcs.push_back(-1);
        return 1;
    }
    const int32_t over = 5000;
    std::memcpy(fuel + 8, &over, 4);
    g_attr_rcs.push_back(game_slice_attr_changed(sl, iter, KILN_GAME_LIST_WRITE, 0));
    g_attr_rcs.push_back(game_slice_attr_changed(sl, iter, KILN_GAME_LIST_READ_OPT, 0));
    return 1;
}
void KILN_CALL st_attr(KilnGameSlice* s, void*) { game_slice_iterate(s, &each_attr, s); }

// fuel_setup's descriptor lists (names as the engine stores them)
NameRef g_fs_sread[] = {{"impact::AttributeRegistry", 25}};
NameRef g_fs_tpl[] = {{"FuelSetup", 9}}, g_fs_ropt[] = {{"CurrentTransform", 16}},
        g_fs_write[] = {{"Fuel", 4}, {"DynamicFuelSetup", 16}}, g_unser[] = {{"Health", 6}};
void counts(Job& j, uint8_t t, uint16_t r, uint16_t w) {
    j.bytes[0x4B0] = t;
    std::memcpy(j.bytes + 0x4B8, &r, 2);
    std::memcpy(j.bytes + 0x4BA, &w, 2);
}

struct Seen {
    std::vector<uint32_t> ids;
    std::vector<uint32_t> rows;
    uint32_t count = 0;
    std::vector<KilnResult> rcs;
    uintptr_t fuel = 0, dfs = 0, ct = 0, tpl = 0, archetype = 0;
    KilnResult archetype_rc = KILN_OK;
    uint32_t fuel_size = 0, ct_size = 0;
    uint8_t fuel_writable = 9, ct_writable = 9;
    bool slot_outside = true;
    uint32_t index_after = 99;
};
Seen g_seen4;
uint32_t KILN_CALL each_record(uint64_t* iter, uint32_t row, KilnEntity e, void* user) {
    auto* sl = static_cast<KilnGameSlice*>(user);
    g_seen4.ids.push_back(e);
    g_seen4.rows.push_back(row);
    if (row == 1) {
        uint32_t size = 0;
        uint8_t w = 0;
        g_seen4.fuel = reinterpret_cast<uintptr_t>(game_slice_slot(sl, iter, KILN_GAME_LIST_WRITE, 0, &size, &w));
        g_seen4.fuel_size = size, g_seen4.fuel_writable = w;
        g_seen4.dfs = reinterpret_cast<uintptr_t>(game_slice_slot(sl, iter, KILN_GAME_LIST_WRITE, 1, nullptr, nullptr));
        g_seen4.ct = reinterpret_cast<uintptr_t>(game_slice_slot(sl, iter, KILN_GAME_LIST_READ_OPT, 0, &size, &w));
        g_seen4.ct_size = size, g_seen4.ct_writable = w;
        g_seen4.tpl = reinterpret_cast<uintptr_t>(game_slice_slot(sl, iter, KILN_GAME_LIST_TPL, 0, nullptr, nullptr));
        g_seen4.rcs.push_back(game_slice_mark(sl, iter, KILN_GAME_LIST_WRITE, 0));
        g_seen4.rcs.push_back(game_slice_mark(sl, iter, KILN_GAME_LIST_WRITE, 0));  // once per entity and component
        g_seen4.rcs.push_back(game_slice_mark(sl, iter, KILN_GAME_LIST_READ_OPT, 0));
        g_seen4.rcs.push_back(game_slice_mark(sl, iter, KILN_GAME_LIST_WRITE, 5));
        g_seen4.rcs.push_back(game_slice_iterate(sl, &each_record, sl));  // no iteration inside an iteration
    }
    if (row == 2) g_seen4.rcs.push_back(game_slice_mark(sl, iter, KILN_GAME_LIST_WRITE, 0));  // the next entity
    return 0;
}
void KILN_CALL st_iterate(KilnGameSlice* s, void*) {
    g_seen4.count = s->count;
    uint64_t dummy[4] = {};
    g_seen4.slot_outside = game_slice_slot(s, dummy, KILN_GAME_LIST_WRITE, 0, nullptr, nullptr) == nullptr &&
                           game_slice_mark(s, dummy, KILN_GAME_LIST_WRITE, 0) == KILN_ERR_WRONG_THREAD;
    g_seen4.rcs.push_back(game_slice_iterate(s, &each_record, s));
    g_seen4.index_after = reinterpret_cast<ProgramCtx*>(s->program_ctx)->index;
    g_seen4.archetype_rc = game_slice_archetype(s, &g_seen4.archetype);
}
uint32_t KILN_CALL each_stop(uint64_t*, uint32_t row, KilnEntity, void*) { return row == 2; }
void KILN_CALL st_iterate_after(KilnGameSlice* s, void*) {
    g_seen4.rcs.push_back(game_slice_iterate(s, &each_stop, nullptr));
    g_seen4.index_after = reinterpret_cast<ProgramCtx*>(s->program_ctx)->index;
}

// The runner link is installed with mid2_checked, which refuses while a thread cannot be checked: tried again
// kRecordSpacing Pres later, kRecordAttempts times at most, then WhenArchetype keeps the entity-map read and says so.
void record_link_refused() {
    check(g_record_ok && !g_record_live.load() && !g_record_wanted.load(),
          "slice record: the feature resolved; no link before a stage asks for an archetype");
    wpre();
    check(g_mid_calls == 0, "slice record: no link is put in while nobody asked");
    g_record_wanted = true;
    g_record_wait = 1;
    g_record_busy = true; // another world's Pre is still publishing the spacing counter
    record_install();
    check(g_record_wait.load() == 1, "slice record: a competing Pre cannot decrement the retry clock outside its install gate");
    g_record_busy = false;
    g_record_wait = 0;
    g_mid_rc = KILN_HOOKS_ERR_UNCHECKED;
    g_record_wanted = true;  // what a stage's first archetype read sets
    wpre();
    check(g_mid_calls == 1 && !g_record_live.load() && logs_with("tried again in 16 Pres") == 1,
          "slice record: a refused link is said and not live");
    for (int i = 0; i < 16; ++i) wpre();
    check(g_mid_calls == 1, "slice record: no second try within 16 Pres");
    wpre();
    check(g_mid_calls == 2, "slice record: the second try comes 16 Pres later");
    for (int i = 0; i < 8 * 17; ++i) wpre();
    check(g_mid_calls == 8 && g_record_gaveup.load() && logs_with("was refused 8 times") == 1,
          "slice record: after 8 refusals it gives up and says WhenArchetype keeps the entity-map read");
    // as before the row: the next stage that asks puts the link in at once
    g_record_gaveup = false;
    g_record_attempts = 0;
    g_record_wait = 0;
    g_record_wanted = false;
    g_mid_rc = KILN_OK;
    g_mid_calls = 0;
}

void per_entity() {
    fn.iter_begin = &fake_iter_begin;
    fn.iter_next = &fake_iter_next;
    fn.current_entity = &fake_current_entity;
    fn.mark_written = &fake_mark;
    g_type_index = {{fnv1a("FuelSetup", fnv1a("keen::ecs::")), 3}, {fnv1a("CurrentTransform", fnv1a("keen::ecs::")), 5},
                    {fnv1a("DynamicFuelSetup", fnv1a("keen::ecs::")), 10}, {fnv1a("Fuel", fnv1a("keen::ecs::")), 20},
                    {fnv1a("Health", fnv1a("keen::ecs::")), 30}};
    // the next world: fuel_setup with its lists, despawn_link with other_write_unser (no view)
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    wworld(0);
    g_wdesc[0].tpl = {g_fs_tpl, 1};
    g_wdesc[0].read_opt = {g_fs_ropt, 1};
    g_wdesc[0].write = {g_fs_write, 2};
    g_wdesc[4].other_write_unser = {g_unser, 1};
    static uintptr_t registry[8]{};
    std::memcpy(g_wworld + kStorage, &registry, 0);
    const auto reg = reinterpret_cast<uintptr_t>(registry);
    std::memcpy(g_wworld + kStorage, &reg, 8);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    KilnStr names[4];
    check(game_program_list(KILN_WORLD_SERVER, 0, KILN_GAME_LIST_WRITE, names, 4) == 2 && sv(names[0]) == "Fuel" &&
              sv(names[1]) == "DynamicFuelSetup" && game_program_list(KILN_WORLD_SERVER, 0, KILN_GAME_LIST_READ, names, 4) == 0 &&
              game_program_list(KILN_WORLD_SERVER, 0, 16, names, 4) == -1 && game_program_list(KILN_WORLD_SERVER, 99, 0, names, 4) == -1,
          "program list: the descriptor's names in descriptor order");
    {
        // the contract (chapter 06a 9.7): every one of the 21 lists (also the five the slot view leaves out), the masks,
        // flags and interval, as tools/typegen/catalog.py writes it
        auto text_of = [](const std::string& lists) {
            const char* order[] = {"tpl", "tpl_opt", "read", "read_opt", "write", "write_opt", "add", "remove", "sys_read",
                                   "sys_write", "other_tpl", "other_read", "other_write", "other_write_unser", "with",
                                   "without", "any_of", "all_write", "all_read", "events_in", "events_out"};
            std::string t;
            for (const char* l : order) {
                t += l;
                t += '=';
                const std::string key = std::string("\n") + l + "=";
                const auto at = ("\n" + lists).find(key);
                if (at != std::string::npos) {
                    const auto from = at + key.size() - 1;
                    t += lists.substr(from, lists.find('\n', from) - from);
                }
                t += '\n';
            }
            return t;
        };
        auto fnv = [](const std::string& s) {
            uint32_t h = 0x811C9DC5u;
            for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
            return h;
        };
        g_wdesc[0].interval = 1;
        g_wdesc[0].sys_write_parallel = 1;
        const std::string want0 = text_of("tpl=FuelSetup\nread_opt=CurrentTransform\nwrite=Fuel,DynamicFuelSetup\n") +
                                  "sys_read_opt=0000000000000000\nsys_write_opt=0000000000000000\n"
                                  "sys_write_parallel=0000000000000001\nflags=00000000\ninterval=1\n";
        uint32_t c = 0, size = 0;
        char text[1024];
        const KilnResult rc = game_program_contract(KILN_WORLD_SERVER, 0, &c, text, sizeof text, &size);
        check(rc == KILN_OK && want0 == text && size == want0.size() && c == fnv(want0),
              "contract: the descriptor's text and its FNV-1a-32, as the program catalog writes them");
        const std::string want4 = text_of("other_write_unser=Health\n") +
                                  "sys_read_opt=0000000000000000\nsys_write_opt=0000000000000000\n"
                                  "sys_write_parallel=0000000000000000\nflags=00000000\ninterval=0\n";
        uint32_t c4 = 0;
        check(game_program_contract(KILN_WORLD_SERVER, 4, &c4, text, sizeof text, nullptr) == KILN_OK && want4 == text &&
                  c4 == fnv(want4),
              "contract: other_write_unser (a list the slot view does not model) is in it too");
        char small[8];
        uint32_t c2 = 0, size2 = 0;
        check(game_program_contract(KILN_WORLD_SERVER, 0, &c2, small, sizeof small, &size2) == KILN_OK && c2 == c &&
                  size2 == want0.size() && std::string(small) == want0.substr(0, 7),
              "contract: a short buffer gets the text cut and NUL-terminated, the size and hash of the whole");
        g_wdesc[0].flags = 0x400;
        uint32_t c3 = 0;
        check(game_program_contract(KILN_WORLD_SERVER, 0, &c3, nullptr, 0, nullptr) == KILN_OK && c3 != c,
              "contract: a flag change changes it (a flag moves a program between threads)");
        g_wdesc[0].flags = 0;
        check(game_program_contract(KILN_WORLD_SERVER, 0, nullptr, nullptr, 0, nullptr) == KILN_ERR_INVALID_ARGUMENT &&
                  game_program_contract(KILN_WORLD_SERVER, 99, &c2, nullptr, 0, nullptr) == KILN_ERR_NOT_FOUND &&
                  game_program_contract(KILN_WORLD_PREDICTION, 0, &c2, nullptr, 0, nullptr) == KILN_ERR_NOT_FOUND,
              "contract: no out pointer, no such index, no directory of that kind");
    }
    KilnGameProgramInfo pi{};
    pi.size = sizeof pi;
    game_program_info(KILN_WORLD_SERVER, 4, &pi);
    check((pi.flags & KILN_GAME_PROGRAM_NO_VIEW) != 0, "a program with other_write_unser has no view (flag)");

    // the slice's archetype: the entity map holds entity 100 -> a record whose layout has bits 5 and 20
    alignas(16) static uint64_t layout_bits[2] = {(1ull << 5) | (1ull << 20), 0};
    alignas(16) static uint8_t record[0x20]{};
    const auto lb = reinterpret_cast<uintptr_t>(layout_bits);
    std::memcpy(record + 0x18, &lb, 8);
    const uint32_t id100 = 100;
    std::memcpy(record + 0x10, &id100, 4);  // the record names its entity (CreateEntity writes it, both builds)
    static uint64_t bits[1] = {0};
    static uint32_t keys[8]{};
    static uint64_t values[8]{};
    keys[100 & 7] = 100;
    values[100 & 7] = reinterpret_cast<uintptr_t>(record);
    bits[0] = 1ull << (100 & 7);
    alignas(16) static uint8_t storage[0x200]{};
    storage[8] = KILN_WORLD_SERVER;
    uint8_t* map = storage + 0x40;
    const uint64_t cap = 8;
    const uint32_t mcount = 1;
    const auto pb = reinterpret_cast<uintptr_t>(bits), pk = reinterpret_cast<uintptr_t>(keys), pv = reinterpret_cast<uintptr_t>(values);
    std::memcpy(map + 0x08, &pb, 8);
    std::memcpy(map + 0x10, &cap, 8);
    std::memcpy(map + 0x28, &pk, 8);
    std::memcpy(map + 0x40, &pv, 8);
    std::memcpy(map + 0x54, &mcount, 4);

    Job job(5);
    counts(job, 1, 1, 2);
    uint8_t* st = storage;
    std::memcpy(job.bytes, &st, 8);
    const KilnHandle it = wrap(90, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_iterate, nullptr);
    wpre();
    const uint64_t runs = g_original_runs[0].load(), mid = g_mid_start.load(), bad = g_bad_index.load();
    run_slice(wentry(0), job);
    check(g_seen4.rcs.size() == 7 && g_seen4.rcs[6] == KILN_OK && g_seen4.count == 5 && g_seen4.ids.size() == 5,
          "iterate: every entity of the slice, once");
    bool same = true;
    for (uint32_t r = 0; r < g_seen4.ids.size(); ++r) same &= g_seen4.ids[r] == job.ids[r * 4] && g_seen4.rows[r] == r;
    check(same, "iterate: per-entity ids equal the job's id list, in row order");
    check(g_seen4.index_after == 0 && g_original_runs[0].load() == runs + 1 && g_mid_start.load() == mid &&
              g_bad_index.load() == bad,
          "iterate in a Before: the index is 0 again, the update runs over the whole slice");
    check(g_seen4.fuel == slot_base(9) + kRowStride && g_seen4.dfs == slot_base(3) + kRowStride &&
              g_seen4.ct == slot_base(2) + kRowStride && g_seen4.tpl == slot_base(1) + kRowStride,
          "slots: by type index, not descriptor order (Fuel is write[0] and the second write record)");
    check(g_seen4.fuel_size == 16 && g_seen4.fuel_writable == 1 && g_seen4.ct_size == 48 && g_seen4.ct_writable == 0,
          "slots: reflected sizes, writable only for the write lists");
    check(g_seen4.rcs[0] == KILN_OK && g_seen4.rcs[1] == KILN_OK && g_seen4.rcs[2] == KILN_ERR_INVALID_ARGUMENT &&
              g_seen4.rcs[3] == KILN_ERR_INVALID_ARGUMENT && g_seen4.rcs[4] == KILN_ERR_WRONG_THREAD &&
              g_seen4.rcs[5] == KILN_OK && g_marks.load() == 2 && g_mark_rec.load() == 0xAA00 + 9,
          "mark: once per entity and component (the Fuel record's tracker), refused for a read list, a name past the "
          "list, and a nested iteration");
    check(g_seen4.slot_outside, "slot and mark outside an iteration are refused");
    check(g_seen4.archetype == lb && game_archetype_has(KILN_WORLD_SERVER, lb, fnv1a("Fuel", fnv1a("keen::ecs::"))) == 1 &&
              game_archetype_has(KILN_WORLD_SERVER, lb, fnv1a("DynamicFuelSetup", fnv1a("keen::ecs::"))) == 0 &&
              game_archetype_has(KILN_WORLD_SERVER, lb, 0x12345678) == -1,
          "archetype: the first entity's layout; membership by the layout's type bits");
    {
        // The entity map hashes its keys (docs/re/spawn.md: the game's entity map find, 0x650c50, linear probing from
        // the hash): entity 101 sits in the slot of its hash (3), not of its id (5). Entity 100's hash is 4, its id's too.
        auto entity_hash = [](uint32_t k) {
            uint32_t x = ((k >> 16) ^ k) * 0x45d9f3bu;
            x = ((x >> 16) ^ x) * 0x45d9f3bu;
            return (x >> 16) ^ x;
        };
        alignas(16) static uint64_t layout2[2] = {1ull << 5, 0};
        alignas(16) static uint8_t record2[0x20]{};
        const auto lb2 = reinterpret_cast<uintptr_t>(layout2);
        std::memcpy(record2 + 0x18, &lb2, 8);
        const uint32_t id101 = 101;
        std::memcpy(record2 + 0x10, &id101, 4);
        const uint32_t at = entity_hash(101) & 7;
        keys[at] = 101;
        values[at] = reinterpret_cast<uintptr_t>(record2);
        bits[0] |= 1ull << at;
        const uint32_t count2 = 2;
        std::memcpy(map + 0x54, &count2, 4);
        Job j2(5);
        j2.ids[0] = 101;
        counts(j2, 1, 1, 2);
        std::memcpy(j2.bytes, &st, 8);
        g_seen4 = Seen{};
        run_slice(wentry(0), j2);
        check(at == 3 && g_seen4.archetype == lb2,
              "archetype: the first entity is found in the entity map's slot of its id's hash, as the game finds it");
        bits[0] &= ~(1ull << at);
        g_seen4 = Seen{};
        run_slice(wentry(0), j2);
        check(g_seen4.archetype == 0 && g_seen4.archetype_rc == KILN_WORLD_ERR_CHANGED,
              "a nonempty slice with no entity-map entry is a change under the read (transient), not an empty slice");
        bits[0] |= 1ull << at;
        Job empty(0);
        counts(empty, 1, 1, 2);
        std::memcpy(empty.bytes, &st, 8);
        g_seen4 = Seen{};
        run_slice(wentry(0), empty);
        check(g_seen4.archetype_rc == KILN_ERR_NOT_FOUND,
              "an empty slice remains a transient archetype miss");
    }
    {
        // The game's own removal from the entity map (MapRemoveSlot: server 0x6848a0, client 0x9e8900, the same 74
        // instructions; out/06a-program-wraps/client-static): a backward shift, no tombstone. Entries after the hole
        // whose home slot allows it move back into it; only the last hole's bit is cleared, and its key and value stay
        // behind as they were. Ported here line by line from the listing.
        auto entity_hash = [](uint32_t k) {
            uint32_t x = ((k >> 16) ^ k) * 0x45d9f3bu;
            x = ((x >> 16) ^ x) * 0x45d9f3bu;
            return (x >> 16) ^ x;
        };
        constexpr uint64_t cap = 8, mask = cap - 1;
        static uint64_t mbits[1] = {0};
        static uint32_t mkeys[cap]{};
        static uint64_t mvalues[cap]{};
        uint32_t mcount = 0;
        auto occupied = [&](uint64_t s) { return ((mbits[s >> 6] >> (s & 63)) & 1) != 0; };
        auto insert = [&](uint32_t id, uintptr_t rec) {  // the insert's slot probe: the key or the first empty slot
            uint64_t s = entity_hash(id) & mask;
            while (occupied(s) && mkeys[s] != id) s = (s + 1) & mask;
            mbits[0] |= 1ull << s;
            mkeys[s] = id;
            mvalues[s] = rec;
            ++mcount;
            return s;
        };
        auto remove_slot = [&](uint64_t i) {
            if (i >= cap || !mcount || !occupied(i)) return;
            uint64_t hole = i, j = (i + 1) & mask;
            while (occupied(j)) {
                const uint64_t k = entity_hash(mkeys[j]) & mask;
                const bool move = j > hole ? (k <= hole || k > j) : (j < i && k <= hole && k > j);
                if (move) {
                    mkeys[hole] = mkeys[j];
                    mvalues[hole] = mvalues[j];
                    hole = j;
                }
                j = (j + 1) & mask;
            }
            mbits[0] &= ~(1ull << hole);
            --mcount;
        };
        // three entities whose ids hash to the last slot: their chain wraps around the end of the table (7, 0, 1)
        uint32_t ids[3]{};
        for (uint32_t id = 1000, n = 0; n < 3; ++id)
            if ((entity_hash(id) & mask) == 7) ids[n++] = id;
        alignas(16) static uint64_t lay[3][2] = {{1ull << 1, 0}, {1ull << 2, 0}, {1ull << 3, 0}};
        alignas(16) static uint8_t recs[3][0x20]{};
        uint64_t slots[3]{};
        for (int n = 0; n < 3; ++n) {
            const auto p = reinterpret_cast<uintptr_t>(lay[n]);
            std::memcpy(recs[n] + 0x18, &p, 8);
            std::memcpy(recs[n] + 0x10, &ids[n], 4);
            slots[n] = insert(ids[n], reinterpret_cast<uintptr_t>(recs[n]));
        }
        alignas(16) static uint8_t st2[0x200]{};
        st2[8] = KILN_WORLD_SERVER;
        uint8_t* m2 = st2 + 0x40;
        const auto pb = reinterpret_cast<uintptr_t>(mbits), pk = reinterpret_cast<uintptr_t>(mkeys),
                   pv = reinterpret_cast<uintptr_t>(mvalues);
        auto publish = [&] {
            std::memcpy(m2 + 0x08, &pb, 8);
            std::memcpy(m2 + 0x10, &cap, 8);
            std::memcpy(m2 + 0x28, &pk, 8);
            std::memcpy(m2 + 0x40, &pv, 8);
            std::memcpy(m2 + 0x54, &mcount, 4);
        };
        publish();
        uint8_t* s2 = st2;
        auto archetype_of = [&](uint32_t first, KilnResult* rc) {
            Job j(3);
            j.ids[0] = first;
            counts(j, 1, 1, 2);
            std::memcpy(j.bytes, &s2, 8);
            g_seen4 = Seen{};
            run_slice(wentry(0), j);
            *rc = g_seen4.archetype_rc;
            return g_seen4.archetype;
        };
        KilnResult rc = KILN_OK;
        check(slots[0] == 7 && slots[1] == 0 && slots[2] == 1 &&
                  archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK,
              "archetype: an entity at the end of a probe chain that wraps around the table is found");
        remove_slot(slots[0]);  // the game destroys the chain's first entity
        publish();
        check(!occupied(1) && mkeys[1] == ids[2] && mvalues[1] == reinterpret_cast<uintptr_t>(recs[2]) &&
                  mkeys[7] == ids[1] && mkeys[0] == ids[2] && mcount == 2,
              "the game's removal shifts the chain back and leaves the last hole's stale key and record behind");
        check(archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK &&
                  archetype_of(ids[1], &rc) == reinterpret_cast<uintptr_t>(lay[1]) && rc == KILN_OK,
              "archetype: after an earlier entity of its probe chain was removed, a later one is still found");
        check(archetype_of(ids[0], &rc) == 0 && rc == KILN_WORLD_ERR_CHANGED,
              "archetype: a removed entity is a transient miss (its emptied slot stops the probe; a stale key there is not read)");
        // a record the pool gave to another entity: the map's slot still names the id, the record names another one
        const uint32_t other = ids[0] + 77777;
        std::memcpy(recs[1] + 0x10, &other, 4);
        check(archetype_of(ids[1], &rc) == 0 && rc == KILN_WORLD_ERR_CHANGED,
              "archetype: a record that names another entity is a transient miss, not that entity's archetype");
        std::memcpy(recs[1] + 0x10, &ids[1], 4);
        const int64_t saved = L.entity_id;
        L.entity_id = -1;
        check(archetype_of(ids[1], &rc) == 0 && rc == KILN_WORLD_ERR_CHANGED,
              "archetype: without the record's id offset the map's layout is not trusted; the slice-record link is coming");
        g_record_ok = false;
        check(archetype_of(ids[1], &rc) == 0 && rc == KILN_ERR_UNAVAILABLE,
              "archetype: with neither the record's id offset nor the slice record, this build cannot read archetypes");
        g_record_ok = true;
        L.entity_id = saved;
        // A concurrent map mutation during the archetype read (a grow, or an insert/erase) is detected: the test seam
        // changes the map between the layout read and the coherence re-check, and the read turns undecidable instead of
        // trusting a torn result. The decision is correct again once nothing changes under it.
        static uintptr_t g_m2;
        static uint32_t g_m2count;
        g_m2 = reinterpret_cast<uintptr_t>(m2);
        g_m2count = mcount;
        check(archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK,
              "archetype: decides when the map does not change under the read");
        wraps_test_archetype_gap(+[] { uint32_t c = g_m2count + 1; std::memcpy(reinterpret_cast<void*>(g_m2 + 0x54), &c, 4); });
        check(archetype_of(ids[2], &rc) == 0 && rc == KILN_WORLD_ERR_CHANGED,
              "archetype: a count change under the read (a concurrent insert or erase) is transient, not a wrong archetype");
        std::memcpy(m2 + 0x54, &mcount, 4);
        wraps_test_archetype_gap(+[] { uint64_t c = 16; std::memcpy(reinterpret_cast<void*>(g_m2 + 0x10), &c, 8); });
        check(archetype_of(ids[2], &rc) == 0 && rc == KILN_WORLD_ERR_CHANGED,
              "archetype: a capacity change under the read (a grow that moved the arrays) is transient");
        std::memcpy(m2 + 0x10, &cap, 8);
        wraps_test_archetype_gap(nullptr);
        check(archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK,
              "archetype: decides again once the map is steady under the read");

        // The scheduler-stable source: the engine's slice record that ProgramRunner runs. The archetype reads above asked
        // for it, so the next Pre put the link on the runner's entry; from then on a slice whose runner recorded its
        // record reads the layout from there, checked against the trampoline's entry and the job's count.
        wpre();
        check(g_record_live.load() && g_mid_fn && g_mid_target == kRunner && g_mid_calls == 1,
              "slice record: the first archetype read put the mid link on types/ProgramRunner in the next Pre");
        alignas(16) static uint64_t rec_layout[2] = {1ull << 4, 0};
        alignas(16) static uint8_t srec[0x38]{};
        auto set_rec = [&](uintptr_t entry, uint32_t first, uint32_t end) {
            const auto lp = reinterpret_cast<uintptr_t>(rec_layout);
            std::memcpy(srec + 0x10, &entry, 8);
            std::memcpy(srec + 0x18, &lp, 8);
            std::memcpy(srec + 0x28, &first, 4);
            std::memcpy(srec + 0x2C, &end, 4);
        };
        auto runner_call = [&](uintptr_t record, const std::function<void()>& body) {
            KilnCallFrame frame{};
            frame.gpr[1] = record;
            const auto outer = g_runner_body;
            g_runner_body = &body;
            try { g_mid_fn(&frame, g_mid_user); }
            catch (...) { g_runner_body = outer; throw; }
            g_runner_body = outer;
        };
        auto via_runner = [&](uint32_t first, KilnResult* rc) {
            uintptr_t result = 0;
            runner_call(reinterpret_cast<uintptr_t>(srec), [&] { result = archetype_of(first, rc); });
            return result;
        };
        const auto rl = reinterpret_cast<uintptr_t>(rec_layout);
        const uint64_t from_rec = g_arch_record[0].load(), mism = g_arch_mismatch[0].load();
        set_rec(wentry(0), 5, 8);  // 3 rows: the job's count
        check(via_runner(ids[2], &rc) == rl && rc == KILN_OK && g_arch_record[0].load() == from_rec + 1,
              "slice record: the layout comes from the slice record the runner recorded, not from the entity map");
        wraps_test_archetype_gap(+[] { uint32_t c = g_m2count + 1; std::memcpy(reinterpret_cast<void*>(g_m2 + 0x54), &c, 4); });
        check(via_runner(ids[2], &rc) == rl && rc == KILN_OK,
              "slice record: a map that changes meanwhile does not matter to it");
        std::memcpy(m2 + 0x54, &mcount, 4);
        wraps_test_archetype_gap(nullptr);
        check(archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK,
              "slice record: taken and cleared by the trampoline: a later slice with no record of its own reads the map");
        set_rec(wentry(1), 5, 8);
        check(via_runner(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK && g_arch_mismatch[0].load() == mism + 1,
              "slice record: a record of another program's entry is not trusted (the map answers)");
        set_rec(wentry(0), 5, 9);
        check(via_runner(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK && g_arch_mismatch[0].load() == mism + 2,
              "slice record: a record whose rows are not the job's count is not trusted (the map answers)");
        set_rec(wentry(0), 5, 8);
        const int64_t saved_entities = L.storage_entities;
        L.storage_entities = -1;
        check(via_runner(ids[2], &rc) == rl && rc == KILN_OK,
              "slice record: decides on a build whose entity map did not resolve");
        check(archetype_of(ids[2], &rc) == 0 && rc == KILN_WORLD_ERR_CHANGED,
              "slice record: there, a slice with no record is left to the game (transient), not turned off");
        L.storage_entities = saved_entities;
        runner_call(reinterpret_cast<uintptr_t>(srec), [] {});
        check(archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]) && rc == KILN_OK,
              "slice record: a runner skipped by a downstream hook leaves no stale same-program/count capture");
        try { runner_call(reinterpret_cast<uintptr_t>(srec), [] { throw 17; }); } catch (int) {}
        check(archetype_of(ids[2], &rc) == reinterpret_cast<uintptr_t>(lay[2]),
              "slice record: unwinding a runner clears its capture before an unrelated trampoline");
        alignas(16) uint8_t inner[0x38];
        std::memcpy(inner, srec, sizeof inner);
        const auto other_layout = reinterpret_cast<uintptr_t>(lay[2]);
        std::memcpy(inner + 0x18, &other_layout, 8);
        uintptr_t nested_result = 0;
        runner_call(reinterpret_cast<uintptr_t>(srec), [&] {
            runner_call(reinterpret_cast<uintptr_t>(inner), [] {});
            nested_result = archetype_of(ids[2], &rc);
        });
        check(nested_result == rl, "slice record: a nested skipped runner restores the outer runner's capture");
        Job client_job(3, KILN_WORLD_CLIENT);
        client_job.ids[0] = ids[2];
        std::memcpy(client_job.bytes, &s2, 8);
        ProgramCtx client_ctx{client_job.bytes, 0, 0};
        Frame client_frame{}; client_frame.ctx = &client_ctx; client_frame.count = 3;
        KilnGameSlice client_slice{}; client_slice.size = sizeof client_slice; client_slice.world_kind = KILN_WORLD_CLIENT;
        client_slice.count = 3; client_slice.entity_ids = client_job.ids; client_slice.internal = &client_frame;
        t_frame = &client_frame; t_in_slice = true;
        uintptr_t client_layout = 0;
        check(game_slice_archetype(&client_slice, &client_layout) == KILN_WORLD_ERR_CHANGED && !client_layout,
              "client archetype: a missing runner capture leaves the slice to the game instead of trusting the unproven map");
        g_record_gaveup = true;
        check(game_slice_archetype(&client_slice, &client_layout) == KILN_ERR_UNAVAILABLE && !client_layout,
              "client archetype: exhausted checked installs refuse the filter instead of enabling an unsafe fallback");
        g_record_gaveup = false;
        t_frame = nullptr; t_in_slice = false;

    }
    KilnGameSlice outside{};
    outside.size = sizeof outside;
    uintptr_t lay = 0;
    check(game_slice_iterate(&outside, &each_record, nullptr) == KILN_ERR_WRONG_THREAD &&
              game_slice_archetype(&outside, &lay) == KILN_ERR_WRONG_THREAD,
          "iterate and archetype outside a stage are refused");
    game_program_unwrap(it);
    // an After that stops early leaves the index as the update left it
    const KilnHandle after = wrap(90, "fuel_setup", KILN_WRAP_AFTER, 0, &st_iterate_after, nullptr);
    wpre();
    g_seen4.rcs.clear();
    run_slice(wentry(0), job);
    check(g_seen4.rcs.size() == 1 && g_seen4.rcs[0] == KILN_OK && g_seen4.index_after == 5 && g_bad_index.load() == bad,
          "iterate in an After, stopped at row 2: the index is the update's (the count) again");
    // a job whose counts differ from the view: no slot (and kiln.log once)
    game_program_unwrap(after);
    wpre();
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    g_seen4 = Seen{};
    const KilnHandle it2 = wrap(90, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_iterate, nullptr);
    wpre();
    Job other(5);
    counts(other, 1, 1, 3);  // three write records in the engine's job: the view has two
    run_slice(wentry(0), other);
    check(g_seen4.ids.size() == 5 && g_seen4.fuel == 0 && g_seen4.ct == 0, "counts that differ from the view: iteration "
          "works, no slot");
    game_program_unwrap(it2);
    wpre();
    // a program with no view: iterate works, no slot
    g_seen4 = Seen{};
    const KilnHandle nv = wrap(90, "despawn_link", KILN_WRAP_BEFORE, 0, &st_iterate, nullptr);
    wpre();
    run_slice(wentry(4), job);
    check(g_seen4.ids.size() == 5 && g_seen4.fuel == 0, "a NO_VIEW program: iteration works, no slot");
    game_program_unwrap(nv);
    wpre();
    // Attribute formulas: the program declares impact::AttributeRegistry; the Fuel block is clamped by them
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    g_wdesc[0].sys_read = {g_fs_sread, 1};
    g_type_index[fnv1a("impact::AttributeRegistry")] = 1;
    F.singletons = true;
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    alignas(16) static uint8_t fuel_block[64 * 8]{};
    fuel_block[4] = 8;
    fuel_block[7] = 2;
    fuel_block[6] = 1;  // the block's "has values" flag
    const int32_t max = 1280;
    std::memcpy(fuel_block + 12, &max, 4);
    alignas(16) static uint8_t registry_obj[16]{};
    alignas(16) static uint8_t singletons[0x401]{};
    singletons[0x400] = 1;
    g_slot_override[9] = reinterpret_cast<uintptr_t>(fuel_block);
    g_slot_override[15] = reinterpret_cast<uintptr_t>(registry_obj);
    Job attr_job(2);
    counts(attr_job, 1, 1, 2);
    uint8_t* sp = singletons;
    std::memcpy(attr_job.bytes + 0xC0, &sp, 8);
    const KilnHandle at = wrap(90, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_attr, nullptr);
    wpre();
    check(game_slice_attr_changed(nullptr, nullptr, KILN_GAME_LIST_WRITE, 0) == KILN_ERR_WRONG_THREAD,
          "attribute formulas outside a stage are refused");
    fn.iter_begin = &fake_iter_begin;
    g_attr_changed_fn = &fake_attr_changed;
    run_slice(wentry(0), attr_job);
    int32_t cur = 0;
    std::memcpy(&cur, fuel_block + 8, 4);
    check(g_attr_rcs.size() == 2 && g_attr_rcs[0] == KILN_OK && g_attr_rcs[1] == KILN_ERR_INVALID_ARGUMENT &&
              g_attr_calls.load() == 1 && cur == 1280 && g_attr_registry == reinterpret_cast<uintptr_t>(registry_obj),
          "attribute formulas: run with the program's AttributeRegistry, clamp the written Fuel to its max; refused "
          "for a read list");
    fuel_block[7] = 3; // reflected Fuel is 16 bytes: at most two values
    const auto formula_calls = g_attr_calls.load();
    run_slice(wentry(0), attr_job);
    check(g_attr_rcs[g_attr_rcs.size() - 2] == KILN_ERR_INVALID_ARGUMENT && g_attr_calls.load() == formula_calls,
          "attribute value count outside reflected block never reaches formulas");
    fuel_block[7] = 2;
    fuel_block[4] = 0xFC; // storage offset itself must also be inside the reflected block
    const auto offset_calls = g_attr_calls.load();
    run_slice(wentry(0), attr_job);
    check(g_attr_rcs[g_attr_rcs.size() - 2] == KILN_ERR_INVALID_ARGUMENT && g_attr_calls.load() == offset_calls,
          "attribute storage offset outside reflected block never reaches formulas");
    fuel_block[4] = 8;
    // A non-attribute write slot with a plausible block must never reach AttrChanged.
    alignas(16) static uint8_t non_attribute[64 * 8]{};
    non_attribute[6] = 1;
    non_attribute[7] = 2;
    g_slot_override[3] = reinterpret_cast<uintptr_t>(non_attribute);
    KilnSliceEntityFn invalid_attribute = [](uint64_t* iter, uint32_t row, KilnEntity, void* user) -> uint32_t {
        if (row) return 1;
        auto* sl = static_cast<KilnGameSlice*>(user);
        const auto before = g_attr_calls.load();
        check(game_slice_attr_changed(sl, iter, KILN_GAME_LIST_WRITE, 1) == KILN_ERR_INVALID_ARGUMENT &&
                  g_attr_calls.load() == before, "non-attribute write slot never reaches formulas");
        return 1;
    };
    game_program_unwrap(at);
    wpre();
    const KilnHandle invalid_at = wrap(90, "fuel_setup", KILN_WRAP_BEFORE, 0,
        [](KilnGameSlice* sl, void* u) { game_slice_iterate(sl, *static_cast<KilnSliceEntityFn*>(u), sl); }, &invalid_attribute);
    wpre();
    run_slice(wentry(0), attr_job);
    game_program_unwrap(invalid_at);
    wpre();
    g_slot_override[3] = 0;
    game_program_unwrap(at);
    wpre();
    g_slot_override[9] = g_slot_override[15] = 0;
    g_wdesc[0].sys_read = NameListE{};
    g_wdesc[0].tpl = g_wdesc[0].read_opt = g_wdesc[0].write = g_wdesc[4].other_write_unser = NameListE{};
}

// Stress, as the engine runs it: Pre on the main thread (4 wraps on entity programs come and go every 50 steps), then
// the step: 8 workers run every program's slices at once (two slices of one program concurrently), 2,000 steps.
std::atomic<uint64_t> g_before[8], g_after[8];
void KILN_CALL st_count_before(KilnGameSlice* s, void*) { g_before[s->program_index].fetch_add(1); }
void KILN_CALL st_count_after(KilnGameSlice* s, void*) { g_after[s->program_index].fetch_add(1); }
void KILN_CALL st_replace_half(KilnGameSlice* s, void*) {
    if (s->entity_ids[0] & 1) game_slice_run_original(s);
}
void stress_steps() {
    wworld(0);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    const int programs[] = {0, 1, 2, 3, 4, 5};
    constexpr int kThreads = 8, kSteps = 2000;
    std::barrier start(kThreads + 1), done(kThreads + 1);
    std::atomic<bool> stop{false};
    for (auto& c : g_before) c = 0;
    for (auto& c : g_after) c = 0;
    const uint64_t bad0 = g_bad_index.load(), mid0 = g_mid_start.load();
    std::vector<std::thread> workers;
    for (int t = 0; t < kThreads; ++t)
        workers.emplace_back([&, t] {
            Job job(8 + t);
            job.ids[0] = 100 + static_cast<uint32_t>(t);  // odd/even first ids: the Replace runs the original for half
            for (;;) {
                start.arrive_and_wait();
                if (stop.load()) return;
                for (int p : programs) run_slice(wentry(p), job);
                done.arrive_and_wait();
            }
        });
    std::vector<KilnHandle> live;
    uint64_t dispatched_steps[8] = {};
    bool wrapped = false;
    for (int step = 0; step < kSteps; ++step) {
        if (step % 50 == 0) {
            if (wrapped) {
                for (KilnHandle h : live) game_program_unwrap(h);
                live.clear();
            } else {
                live.push_back(wrap(60, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_count_before, nullptr));
                live.push_back(wrap(60, "fuel_setup", KILN_WRAP_AFTER, 0, &st_count_after, nullptr));
                live.push_back(wrap(60, "is_actor_in_shape", KILN_WRAP_AFTER, 0, &st_count_after, nullptr));
                live.push_back(wrap(60, "impact_prepare", KILN_WRAP_REPLACE, 0, &st_replace_half, nullptr));
            }
            wrapped = !wrapped;
        }
        wpre();
        if (wrapped) dispatched_steps[0]++, dispatched_steps[2]++, dispatched_steps[3]++;
        start.arrive_and_wait();
        done.arrive_and_wait();
    }
    stop = true;
    start.arrive_and_wait();
    for (auto& w : workers) w.join();
    for (KilnHandle h : live) game_program_unwrap(h);
    wpre();
    uint64_t faults = 0;
    for (auto& [h, st] : g_stages)
        if (st->owner == 60) faults += st->faults.load();
    check(g_before[0].load() == dispatched_steps[0] * kThreads && g_after[0].load() == g_before[0].load() &&
              g_after[2].load() == dispatched_steps[2] * kThreads,
          "stress: every wrapped slice ran its Before and its After, on 8 threads, 2,000 steps");
    if (g_bad_index.load() != bad0 || g_mid_start.load() != mid0 || faults || wraps_test_in_flight(0))
        std::fprintf(stderr, "stress: bad %llu, mid %llu, faults %llu, in flight %u\n",
                     static_cast<unsigned long long>(g_bad_index.load() - bad0), static_cast<unsigned long long>(g_mid_start.load() - mid0),
                     static_cast<unsigned long long>(faults), wraps_test_in_flight(0));
    check(g_bad_index.load() == bad0 && g_mid_start.load() == mid0 && faults == 0 && wraps_test_in_flight(0) == 0,
          "stress: 0 bad index, 0 faults, in-flight 0");
    bool restored = true;
    for (int p : programs) {
        restored &= update_of(wentry(p)) == reinterpret_cast<uintptr_t>(kWUpdates[p]);
        if (update_of(wentry(p)) != reinterpret_cast<uintptr_t>(kWUpdates[p])) std::fprintf(stderr, "stress: %s not restored\n", kWNames[p]);
    }
    check(restored, "stress: every entry holds the game's update again");
}

// Stress without the engine's order: the chain of a live slot changes in "Pre" while 8 threads run its slices. Chains
// are freed only when nothing is in flight; ASan sees any use after free.
void stress_overlap() {
    std::atomic<bool> stop{false};
    const KilnHandle keep = wrap(70, "fuel_network", KILN_WRAP_BEFORE, 0, &st_count_before, nullptr);
    wpre();
    std::vector<std::thread> workers;
    std::atomic<uint64_t> slices{0};
    for (int t = 0; t < 8; ++t)
        workers.emplace_back([&] {
            Job job(6);
            while (!stop.load(std::memory_order_relaxed)) {
                run_slice(wentry(1), job);
                slices.fetch_add(1, std::memory_order_relaxed);
            }
        });
    for (int i = 0; i < 2000; ++i) {
        const KilnHandle h = wrap(70, "fuel_network", KILN_WRAP_AFTER, i % 7, &st_count_after, nullptr);
        wraps_pre(KILN_WORLD_SERVER, 0);
        game_program_unwrap(h);
        wraps_pre(KILN_WORLD_SERVER, 0);
    }
    stop = true;
    for (auto& w : workers) w.join();
    game_program_unwrap(keep);
    wpre();
    wpre();
    check(slices.load() > 1000 && wraps_test_in_flight(0) == 0 && g_retired.empty() && g_bad_index.load() == 0,
          "overlap stress: 2,000 chain changes under 8 threads' slices, every chain freed, in-flight 0");
}

// ---- foreign views (chapter 06c 9.1): another service's code inside a game program's update, with its iterator ----
NameRef g_fo_read[] = {{"Health", 6}, {"CurrentTransform", 16}}, g_fo_write[] = {{"Fuel", 4}};
void foreign_views() {
    // program 1 of the live world (per_entity's): other_read Health and CurrentTransform, other_write Fuel
    g_wdesc[1].other_read = {g_fo_read, 2};
    g_wdesc[1].other_write = {g_fo_write, 1};
    KilnForeignLayout l{};
    l.size = sizeof l;
    check(foreign_layout(KILN_WORLD_SERVER, kstr("no_such_program"), &l) == KILN_ERR_NOT_FOUND, "foreign: no such program");
    check(foreign_layout(KILN_WORLD_SERVER, kstr(kWNames[1]), &l) == KILN_OK && l.program == idx(kWNames[1]) &&
              l.session == game_world_session(KILN_WORLD_SERVER) && l.counts[5] == 3 && l.iter_size == 4 * 8 && l.handle,
          "foreign: the view of a program: its other lists (3 accessors after ctx), the session and index");
    // other_read by type index (CurrentTransform 5, Health 30), then other_write (Fuel)
    check(foreign_slot(&l, KILN_GAME_LIST_OTHER_READ, kstr("CurrentTransform")) == 1 &&
              foreign_slot(&l, KILN_GAME_LIST_OTHER_READ, kstr("Health")) == 2 &&
              foreign_slot(&l, KILN_GAME_LIST_OTHER_WRITE, kstr("Fuel")) == 3 &&
              foreign_slot(&l, KILN_GAME_LIST_OTHER_WRITE, kstr("Health")) == -1,
          "foreign: slots by the view algorithm (sorted by type index within a list)");
    Job j(1);
    counts(j, 0, 0, 0);
    j.bytes[0x7A8] = 3;  // the job's other count
    L.job_storage = 0x20;  // the job's storage (JobStorage) for ReadOther / WriteOther
    static uint8_t job_storage[16];
    const auto registry = view_of(&l)->registry;
    std::memcpy(job_storage, &registry, sizeof registry);
    const auto js = reinterpret_cast<uintptr_t>(job_storage);
    std::memcpy(j.bytes + 0x20, &js, 8);
    ProgramCtx ctx{j.bytes, 0, 0};
    uint16_t health_type = 30, fuel_type = 20, wrong_type = 21, ct_type = 5;
    uint64_t iter[4] = {reinterpret_cast<uint64_t>(&ctx), reinterpret_cast<uint64_t>(&ct_type),
                        reinterpret_cast<uint64_t>(&health_type), reinterpret_cast<uint64_t>(&fuel_type)};
    uint32_t jc[8] = {};
    check(foreign_check(&l, iter, jc) == KILN_OK && jc[5] == 3, "foreign: the job's group counts are the view's");
    j.bytes[0x7A8] = 2;
    check(foreign_check(&l, iter, jc) == KILN_ERR_UNAVAILABLE && jc[5] == 2, "foreign: other counts differ: refused");
    j.bytes[0x7A8] = 3;
    const auto other_registry = registry + 8;
    std::memcpy(job_storage, &other_registry, sizeof other_registry);
    check(foreign_check(&l, iter, nullptr) == KILN_ERR_UNAVAILABLE,
          "foreign: identical job counts from another world's storage are refused");
    std::memcpy(job_storage, &registry, sizeof registry);
    F.read_other = true;
    fn.read_other = [](Slice*, void*, void*, uint32_t) {};
    F.write_other = true;
    g_fstore[{iter[2], 7}] = {1, 2, 3, 4};
    g_fstore[{iter[3], 7}] = {9, 9};
    uint32_t size = 0;
    KilnResult rc = KILN_ERR_INTERNAL;
    const void* h = foreign_read(&l, iter, kstr("Health"), 7, &size, &rc);
    check(h && rc == KILN_OK && size == 4 && static_cast<const uint8_t*>(h)[2] == 3, "foreign: read another entity's component");
    check(!foreign_read(&l, iter, kstr("Health"), 8, &size, &rc) && rc == KILN_OK && size == 0,
          "foreign: an entity without it: null, no error");
    check(!foreign_read(&l, iter, kstr("Mana"), 7, &size, &rc) && rc == KILN_ERR_NOT_FOUND,
          "foreign: a component the program does not declare: refused, no access");
    const int writes = g_fwrites;
    auto* f = static_cast<uint8_t*>(foreign_write(&l, iter, kstr("Fuel"), 7, &size, &rc));
    check(f && rc == KILN_OK && size == 2 && g_fwrites == writes + 1, "foreign: write through other_write, marked by the tracker");
    g_client_program = true;  // the same call from a Kiln client or prediction program's callback (package 14)
    check(!foreign_write(&l, iter, kstr("Fuel"), 7, &size, &rc) && rc == KILN_ERR_UNAVAILABLE && g_fwrites == writes + 1 &&
              foreign_attr_changed(&l, iter, f) == KILN_ERR_UNAVAILABLE,
          "foreign: from a Kiln client program, foreign_write and foreign_attr_changed refuse (nothing saved or sent)");
    g_client_program = false;
    check(!foreign_write(&l, iter, kstr("Health"), 7, &size, &rc) && rc == KILN_ERR_NOT_FOUND && g_fwrites == writes + 1,
          "foreign: a read-only component is not written");
    check(foreign_read(&l, iter, kstr("Fuel"), 7, &size, &rc) && rc == KILN_OK, "foreign: other_write is readable too");
    uint64_t changed[4] = {iter[0], iter[1], reinterpret_cast<uint64_t>(&wrong_type), iter[3]};
    g_fstore[{changed[2], 7}] = {5, 6, 7, 8};
    check(!foreign_read(&l, changed, kstr("Health"), 7, &size, &rc) && rc == KILN_ERR_UNAVAILABLE,
          "foreign: a previously verified slot cannot change to another accessor type");
    // an accessor that is not the component map's type: refused, and stays refused for that view
    KilnForeignLayout l2{};
    l2.size = sizeof l2;
    check(foreign_layout(KILN_WORLD_SERVER, kstr(kWNames[1]), &l2) == KILN_OK, "foreign: a second view");
    uint64_t bad[4] = {iter[0], iter[1], reinterpret_cast<uint64_t>(&wrong_type), iter[3]};
    g_fstore[{bad[2], 7}] = {1};
    check(!foreign_read(&l2, bad, kstr("Health"), 7, &size, &rc) && rc == KILN_ERR_UNAVAILABLE &&
              !foreign_read(&l2, iter, kstr("Health"), 7, &size, &rc) && rc == KILN_ERR_UNAVAILABLE,
          "foreign: an accessor of another type is refused once and for the view");
    check(foreign_attr_changed(&l, iter, f) == KILN_ERR_UNAVAILABLE, "foreign: attribute formulas not known here: unavailable");
    auto* attribute_view = const_cast<ForeignView*>(view_of(&l));
    attribute_view->attr_registry = 1;
    g_fstore[{iter[3], 9}] = {0, 0, 0, 0, 8, 0, 1, 0};
    void* attribute = foreign_write(&l, iter, kstr("Fuel"), 9, &size, &rc);
    const auto attr_calls = g_attr_calls.load();
    g_attr_fn = +[](void*, void*) { ++g_attr_calls; };
    check(attribute && foreign_attr_changed(&l, iter, attribute) == KILN_OK && g_attr_calls == attr_calls + 1,
          "foreign: formulas accept the last declared attribute write on this thread");
    check(foreign_attr_changed(&l, iter, job_storage) == KILN_ERR_INVALID_ARGUMENT && g_attr_calls == attr_calls + 1,
          "foreign: formulas refuse a pointer not returned by the last write");
    NameRef non_attr[] = {{"CurrentTransform", 16}};
    g_wdesc[1].other_write = {non_attr, 1};
    KilnForeignLayout l3{}; l3.size = sizeof l3;
    check(foreign_layout(KILN_WORLD_SERVER, kstr(kWNames[1]), &l3) == KILN_OK, "foreign: non-attribute fixture view");
    auto* live_view = const_cast<ForeignView*>(view_of(&l3));
    live_view->attr_registry = 1;
    uint64_t attr_iter[4] = {iter[0], iter[1], iter[2], iter[1]};
    g_fstore[{attr_iter[3], 9}] = {0, 0, 0, 0, 8, 0, 1, 0};
    void* not_attribute = foreign_write(&l3, attr_iter, kstr("CurrentTransform"), 9, &size, &rc);
    g_attr_fn = +[](void*, void*) {};
    check(foreign_attr_changed(&l3, attr_iter, not_attribute) == KILN_ERR_INVALID_ARGUMENT,
          "foreign: an unverified attribute type cannot run attribute formulas");
    g_attr_fn = nullptr;
    // the world goes: the view is void
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    check(!foreign_read(&l, iter, kstr("Health"), 7, &size, &rc) && rc == KILN_ERR_UNAVAILABLE &&
              foreign_check(&l, iter, nullptr) == KILN_ERR_UNAVAILABLE && foreign_slot(&l, KILN_GAME_LIST_OTHER_READ, kstr("Health")) == -1,
          "foreign: a view of a world that stopped answers nothing");
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    wpre();
    g_wdesc[1].other_read = {};
    g_wdesc[1].other_write = {};
    F.read_other = F.write_other = false;
    L.job_storage = -1;
}

// ---- tests/abi.json: the v4 table and its structs, field for field ----
void abi(const char* path) {
    std::ifstream in(path);
    const auto fx = nlohmann::json::parse(in);
    nlohmann::json now;
    auto list = [&](const char* name, size_t size) -> nlohmann::json& {
        now[name]["size"] = size;
        now[name]["fields"] = nlohmann::json::array();
        return now[name]["fields"];
    };
#define F(S, f) l.push_back(nlohmann::json::array({#f, offsetof(S, f)}))
    {
        // the v4 table up to the foreign views (0.2.0-dev.29's client-world members after them: client_world_test.cpp)
        auto& l = list("KilnWorldApiV4", offsetof(KilnWorldApi, foreign_attr_changed) + sizeof(void*));
        F(KilnWorldApi, header); F(KilnWorldApi, register_program); F(KilnWorldApi, program_slot); F(KilnWorldApi, program_info);
        F(KilnWorldApi, iter_begin); F(KilnWorldApi, iter_next); F(KilnWorldApi, current_entity); F(KilnWorldApi, command_buffer);
        F(KilnWorldApi, queue_push); F(KilnWorldApi, queue_add); F(KilnWorldApi, queue_remove); F(KilnWorldApi, mark_written);
        F(KilnWorldApi, read_other); F(KilnWorldApi, component_hash); F(KilnWorldApi, component); F(KilnWorldApi, exists);
        F(KilnWorldApi, transform); F(KilnWorldApi, position); F(KilnWorldApi, find); F(KilnWorldApi, entity_count);
        F(KilnWorldApi, server_world); F(KilnWorldApi, teleport); F(KilnWorldApi, teleport_cancel); F(KilnWorldApi, status);
        F(KilnWorldApi, write_other); F(KilnWorldApi, unregister_program); F(KilnWorldApi, bind_program); F(KilnWorldApi, capabilities);
        F(KilnWorldApi, program_events); F(KilnWorldApi, program_emit); F(KilnWorldApi, write_submit); F(KilnWorldApi, write_cancel);
        F(KilnWorldApi, write_info); F(KilnWorldApi, write_status); F(KilnWorldApi, write_declare);
        F(KilnWorldApi, game_program_find); F(KilnWorldApi, game_program_info); F(KilnWorldApi, game_programs_status);
        F(KilnWorldApi, lab_parallel); F(KilnWorldApi, game_program_wrap); F(KilnWorldApi, game_program_unwrap);
        F(KilnWorldApi, game_wrap_info); F(KilnWorldApi, game_wraps_release); F(KilnWorldApi, game_slice_run_original);
        F(KilnWorldApi, wrap_original); F(KilnWorldApi, in_program); F(KilnWorldApi, game_program_list);
        F(KilnWorldApi, game_slice_iterate); F(KilnWorldApi, game_slice_slot); F(KilnWorldApi, game_slice_mark);
        F(KilnWorldApi, game_slice_archetype); F(KilnWorldApi, game_archetype_has); F(KilnWorldApi, game_slice_attr_changed); F(KilnWorldApi, game_slice_attr_validate); F(KilnWorldApi, game_world_session);
        F(KilnWorldApi, game_program_contract);
        F(KilnWorldApi, foreign_layout); F(KilnWorldApi, foreign_slot); F(KilnWorldApi, foreign_check);
        F(KilnWorldApi, foreign_read); F(KilnWorldApi, foreign_write); F(KilnWorldApi, foreign_attr_changed);
    }
    {
        // The ECS-read members appended after the client-world members, and their records.
        auto& l = list("KilnWorldApiEcsReads", offsetof(KilnWorldApi, ecs_write_policy));
        F(KilnWorldApi, entity_open); F(KilnWorldApi, entity_validate); F(KilnWorldApi, ecs_context_snapshot);
        F(KilnWorldApi, ecs_query); F(KilnWorldApi, ecs_snapshot); F(KilnWorldApi, ecs_snapshot_many);
        F(KilnWorldApi, ecs_template_uuid);
    }
    {
        // The compare-exchange members appended after the ECS reads, and their records.
        auto& l = list("KilnWorldApiEcsWrites", sizeof(KilnWorldApi));
        F(KilnWorldApi, ecs_write_policy); F(KilnWorldApi, ecs_compare_exchange); F(KilnWorldApi, ecs_write_subscribe);
        F(KilnWorldApi, ecs_write_unsubscribe);
    }
    {
        auto& l = list("KilnEcsWritePolicy", sizeof(KilnEcsWritePolicy));
        F(KilnEcsWritePolicy, size); F(KilnEcsWritePolicy, status); F(KilnEcsWritePolicy, world_kind);
        F(KilnEcsWritePolicy, component); F(KilnEcsWritePolicy, component_size); F(KilnEcsWritePolicy, budget);
        F(KilnEcsWritePolicy, flags); F(KilnEcsWritePolicy, why_code); F(KilnEcsWritePolicy, why);
    }
    {
        auto& l = list("KilnMutationReply", sizeof(KilnMutationReply));
        F(KilnMutationReply, size); F(KilnMutationReply, status); F(KilnMutationReply, flags); F(KilnMutationReply, why_code);
        F(KilnMutationReply, engine_reason); F(KilnMutationReply, generation); F(KilnMutationReply, session);
        F(KilnMutationReply, step); F(KilnMutationReply, hold_ns);
    }
    {
        auto& l = list("KilnEcsWriteEvent", sizeof(KilnEcsWriteEvent));
        F(KilnEcsWriteEvent, size); F(KilnEcsWriteEvent, world_kind); F(KilnEcsWriteEvent, entity);
        F(KilnEcsWriteEvent, generation); F(KilnEcsWriteEvent, component); F(KilnEcsWriteEvent, reserved);
        F(KilnEcsWriteEvent, owner); F(KilnEcsWriteEvent, session); F(KilnEcsWriteEvent, step);
    }
    {
        auto& l = list("KilnObjectRef", sizeof(KilnObjectRef));
        F(KilnObjectRef, size); F(KilnObjectRef, entity); F(KilnObjectRef, generation); F(KilnObjectRef, world_kind);
        F(KilnObjectRef, session); F(KilnObjectRef, layout_stamp); F(KilnObjectRef, reload_epoch);
    }
    {
        auto& l = list("KilnObjectInfo", sizeof(KilnObjectInfo));
        F(KilnObjectInfo, size); F(KilnObjectInfo, status); F(KilnObjectInfo, entity); F(KilnObjectInfo, generation);
        F(KilnObjectInfo, world_kind); F(KilnObjectInfo, flags); F(KilnObjectInfo, session);
        F(KilnObjectInfo, layout_stamp); F(KilnObjectInfo, reload_epoch);
    }
    {
        auto& l = list("KilnEcsContextInfo", sizeof(KilnEcsContextInfo));
        F(KilnEcsContextInfo, size); F(KilnEcsContextInfo, status); F(KilnEcsContextInfo, present_mask);
        F(KilnEcsContextInfo, exact_profile); F(KilnEcsContextInfo, sessions); F(KilnEcsContextInfo, layout_stamp);
    }
    {
        auto& l = list("KilnEcsQueryRequest", sizeof(KilnEcsQueryRequest));
        F(KilnEcsQueryRequest, size); F(KilnEcsQueryRequest, world_kind); F(KilnEcsQueryRequest, flags);
        F(KilnEcsQueryRequest, component_count); F(KilnEcsQueryRequest, components); F(KilnEcsQueryRequest, center);
        F(KilnEcsQueryRequest, radius);
    }
    {
        auto& l = list("KilnEcsSnapshotSpec", sizeof(KilnEcsSnapshotSpec));
        F(KilnEcsSnapshotSpec, component); F(KilnEcsSnapshotSpec, flags);
    }
    {
        auto& l = list("KilnForeignLayout", sizeof(KilnForeignLayout));
        F(KilnForeignLayout, size); F(KilnForeignLayout, world_kind); F(KilnForeignLayout, session);
        F(KilnForeignLayout, program); F(KilnForeignLayout, iter_size); F(KilnForeignLayout, counts);
        F(KilnForeignLayout, handle); F(KilnForeignLayout, why);
    }
    {
        auto& l = list("KilnGameSlice", sizeof(KilnGameSlice));
        F(KilnGameSlice, size); F(KilnGameSlice, world_kind); F(KilnGameSlice, program_ctx); F(KilnGameSlice, count);
        F(KilnGameSlice, thread_is_world); F(KilnGameSlice, entity_ids); F(KilnGameSlice, entity_stride);
        F(KilnGameSlice, program_index); F(KilnGameSlice, step); F(KilnGameSlice, program); F(KilnGameSlice, stage);
        F(KilnGameSlice, reserved); F(KilnGameSlice, internal);
    }
    {
        auto& l = list("KilnGameWrapInfo", sizeof(KilnGameWrapInfo));
        F(KilnGameWrapInfo, size); F(KilnGameWrapInfo, state); F(KilnGameWrapInfo, disabled); F(KilnGameWrapInfo, reason);
        F(KilnGameWrapInfo, world_kind); F(KilnGameWrapInfo, stage); F(KilnGameWrapInfo, priority);
        F(KilnGameWrapInfo, program_index); F(KilnGameWrapInfo, program); F(KilnGameWrapInfo, calls);
        F(KilnGameWrapInfo, entities); F(KilnGameWrapInfo, faults); F(KilnGameWrapInfo, overruns);
        F(KilnGameWrapInfo, total_ns); F(KilnGameWrapInfo, threads); F(KilnGameWrapInfo, fault_code);
        F(KilnGameWrapInfo, remove_queued); F(KilnGameWrapInfo, gone); F(KilnGameWrapInfo, reserved);
        F(KilnGameWrapInfo, running); F(KilnGameWrapInfo, detail);
    }
    {
        auto& l = list("KilnGameProgramInfo", sizeof(KilnGameProgramInfo));
        F(KilnGameProgramInfo, size); F(KilnGameProgramInfo, world_kind); F(KilnGameProgramInfo, index); F(KilnGameProgramInfo, id);
        F(KilnGameProgramInfo, reserved); F(KilnGameProgramInfo, name); F(KilnGameProgramInfo, desc_flags);
        F(KilnGameProgramInfo, entry_flags); F(KilnGameProgramInfo, update_rva); F(KilnGameProgramInfo, flags);
        F(KilnGameProgramInfo, wraps); F(KilnGameProgramInfo, faults); F(KilnGameProgramInfo, reserved2);
        F(KilnGameProgramInfo, slices); F(KilnGameProgramInfo, entities); F(KilnGameProgramInfo, forwarded);
    }
    {
        auto& l = list("KilnGameProgramsStatus", sizeof(KilnGameProgramsStatus));
        F(KilnGameProgramsStatus, size); F(KilnGameProgramsStatus, directory); F(KilnGameProgramsStatus, threads);
        F(KilnGameProgramsStatus, programs); F(KilnGameProgramsStatus, game_programs); F(KilnGameProgramsStatus, no_update);
        F(KilnGameProgramsStatus, players_mask); F(KilnGameProgramsStatus, parallel_forced);
        F(KilnGameProgramsStatus, parallel_switch); F(KilnGameProgramsStatus, parallel_steps_left);
        F(KilnGameProgramsStatus, wraps_active); F(KilnGameProgramsStatus, wraps_pending); F(KilnGameProgramsStatus, detail);
    }
#undef F
    for (const auto& [name, shape] : now.items()) {
        const std::string what = std::string("abi.json: ") + name + " field for field";
        check(fx.contains(name) && fx.at(name) == shape, what.c_str());
        if (std::getenv("KILN_ABI_DUMP")) std::printf("\"%s\": %s\n", name.c_str(), shape.dump().c_str());
    }
    // The ECS reads' constants by value: the header against abi.json (and Kiln.World.Tests the C# mirror against it).
    {
        const std::map<std::string, long long> header = {
            {"KILN_ECS_OK", KILN_ECS_OK}, {"KILN_ECS_ABSENT", KILN_ECS_ABSENT}, {"KILN_ECS_STALE", KILN_ECS_STALE},
            {"KILN_ECS_UNREADABLE", KILN_ECS_UNREADABLE}, {"KILN_ECS_UNAVAILABLE", KILN_ECS_UNAVAILABLE},
            {"KILN_ECS_BUSY", KILN_ECS_BUSY}, {"KILN_ECS_UNSUPPORTED", KILN_ECS_UNSUPPORTED},
            {"KILN_WORLD_ERR_CHANGED", KILN_WORLD_ERR_CHANGED}, {"KILN_ECS_ERR_STALE", KILN_ECS_ERR_STALE},
            {"KILN_ECS_ERR_UNREADABLE", KILN_ECS_ERR_UNREADABLE}, {"KILN_ECS_ERR_BUSY", KILN_ECS_ERR_BUSY},
            {"KILN_ECS_ERR_UNSUPPORTED", KILN_ECS_ERR_UNSUPPORTED}, {"KILN_ECS_QUERY_BOUNDS", KILN_ECS_QUERY_BOUNDS},
            {"KILN_ECS_QUERY_FRAME_QUIET", KILN_ECS_QUERY_FRAME_QUIET},
            {"KILN_ECS_READ_FRAME_QUIET", KILN_ECS_READ_FRAME_QUIET}, {"KILN_OBJECT_ALIVE", KILN_OBJECT_ALIVE},
            {"KILN_OBJECT_HAS_TEMPLATE", KILN_OBJECT_HAS_TEMPLATE}, {"KILN_WORLD_CAP_ECS_READ", KILN_WORLD_CAP_ECS_READ}};
        std::map<std::string, long long> listed;
        for (const auto& v : fx.at("KilnEcsReadConstants").at("values")) listed[v.at(0).get<std::string>()] = v.at(1).get<long long>();
        check(listed == header, "abi.json: the ECS reads' constants by value");
        // Result codes are distinct among themselves and from the core's (1..7).
        const long long codes[] = {KILN_WORLD_ERR_CHANGED, KILN_ECS_ERR_STALE, KILN_ECS_ERR_UNREADABLE, KILN_ECS_ERR_BUSY,
                                   KILN_ECS_ERR_UNSUPPORTED};
        bool distinct = true;
        for (size_t i = 0; i < 5; ++i) {
            distinct = distinct && codes[i] > KILN_ERR_INTERNAL;
            for (size_t j = i + 1; j < 5; ++j) distinct = distinct && codes[i] != codes[j];
        }
        check(distinct, "the ECS reads' result codes are distinct and above the core's");
    }
    // The compare-exchange writes' constants by value (Kiln.World.Tests checks the C# mirror against the same list).
    {
        const std::map<std::string, long long> header = {
            {"KILN_ECS_CONFLICT", KILN_ECS_CONFLICT}, {"KILN_ECS_NOT_WRITABLE", KILN_ECS_NOT_WRITABLE},
            {"KILN_ECS_UNCERTAIN", KILN_ECS_UNCERTAIN}, {"KILN_ECS_ERR_CONFLICT", KILN_ECS_ERR_CONFLICT},
            {"KILN_ECS_ERR_NOT_WRITABLE", KILN_ECS_ERR_NOT_WRITABLE}, {"KILN_ECS_ERR_UNCERTAIN", KILN_ECS_ERR_UNCERTAIN},
            {"KILN_WORLD_CAP_ECS_WRITE", KILN_WORLD_CAP_ECS_WRITE},
            {"KILN_ECS_WRITE_POLICY_WRITABLE", KILN_ECS_WRITE_POLICY_WRITABLE},
            {"KILN_ECS_WRITE_POLICY_TRACKING_KNOWN", KILN_ECS_WRITE_POLICY_TRACKING_KNOWN},
            {"KILN_ECS_WRITE_POLICY_TRACKED_A", KILN_ECS_WRITE_POLICY_TRACKED_A},
            {"KILN_ECS_WRITE_POLICY_TRACKED_B", KILN_ECS_WRITE_POLICY_TRACKED_B},
            {"KILN_ECS_WRITE_POLICY_PLAIN", KILN_ECS_WRITE_POLICY_PLAIN},
            {"KILN_ECS_WRITE_POLICY_OVER_BUDGET", KILN_ECS_WRITE_POLICY_OVER_BUDGET},
            {"KILN_MUTATION_WRITTEN", KILN_MUTATION_WRITTEN}, {"KILN_MUTATION_NO_OP", KILN_MUTATION_NO_OP},
            {"KILN_MUTATION_CONFIRMED", KILN_MUTATION_CONFIRMED}, {"KILN_MUTATION_ROLLED_BACK", KILN_MUTATION_ROLLED_BACK},
            {"KILN_MUTATION_TARGET_LATCHED", KILN_MUTATION_TARGET_LATCHED},
            {"KILN_MUTATION_EVENT_QUEUED", KILN_MUTATION_EVENT_QUEUED},
            {"KILN_ECS_WRITE_WHY_NONE", KILN_ECS_WRITE_WHY_NONE},
            {"KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD", KILN_ECS_WRITE_WHY_NOT_SERVER_WORLD},
            {"KILN_ECS_WRITE_WHY_NO_PROFILE", KILN_ECS_WRITE_WHY_NO_PROFILE},
            {"KILN_ECS_WRITE_WHY_NO_WORLD", KILN_ECS_WRITE_WHY_NO_WORLD},
            {"KILN_ECS_WRITE_WHY_UNREGISTERED", KILN_ECS_WRITE_WHY_UNREGISTERED},
            {"KILN_ECS_WRITE_WHY_TRACKED", KILN_ECS_WRITE_WHY_TRACKED},
            {"KILN_ECS_WRITE_WHY_TRACKING_UNKNOWN", KILN_ECS_WRITE_WHY_TRACKING_UNKNOWN},
            {"KILN_ECS_WRITE_WHY_NOT_PLAIN", KILN_ECS_WRITE_WHY_NOT_PLAIN}, {"KILN_ECS_WRITE_WHY_SIZE", KILN_ECS_WRITE_WHY_SIZE},
            {"KILN_ECS_WRITE_WHY_OVER_BUDGET", KILN_ECS_WRITE_WHY_OVER_BUDGET},
            {"KILN_ECS_WRITE_WHY_IN_FRAME", KILN_ECS_WRITE_WHY_IN_FRAME}, {"KILN_ECS_WRITE_WHY_GATE", KILN_ECS_WRITE_WHY_GATE},
            {"KILN_ECS_WRITE_WHY_EVENTS_FULL", KILN_ECS_WRITE_WHY_EVENTS_FULL},
            {"KILN_ECS_WRITE_WHY_TARGET_LATCHED", KILN_ECS_WRITE_WHY_TARGET_LATCHED},
            {"KILN_ECS_WRITE_WHY_OWNER_RETIRED", KILN_ECS_WRITE_WHY_OWNER_RETIRED},
            {"KILN_ECS_WRITE_WHY_REF", KILN_ECS_WRITE_WHY_REF}, {"KILN_ECS_WRITE_WHY_COMPARE", KILN_ECS_WRITE_WHY_COMPARE},
            {"KILN_ECS_WRITE_WHY_FAULT", KILN_ECS_WRITE_WHY_FAULT}, {"KILN_ECS_WRITE_WHY_READBACK", KILN_ECS_WRITE_WHY_READBACK},
            {"KILN_ECS_WRITE_WHY_NOT_OWNERS_REF", KILN_ECS_WRITE_WHY_NOT_OWNERS_REF},
            {"KILN_ECS_WRITE_WHY_RETIRE_RACE", KILN_ECS_WRITE_WHY_RETIRE_RACE}};
        std::map<std::string, long long> listed;
        for (const auto& v : fx.at("KilnEcsWriteConstants").at("values")) listed[v.at(0).get<std::string>()] = v.at(1).get<long long>();
        check(listed == header, "abi.json: the compare-exchange writes' constants by value");
        const long long codes[] = {KILN_WORLD_ERR_CHANGED, KILN_ECS_ERR_STALE, KILN_ECS_ERR_UNREADABLE, KILN_ECS_ERR_BUSY,
                                   KILN_ECS_ERR_UNSUPPORTED, KILN_ECS_ERR_CONFLICT, KILN_ECS_ERR_NOT_WRITABLE, KILN_ECS_ERR_UNCERTAIN};
        bool distinct = true;
        for (size_t i = 0; i < 8; ++i)
            for (size_t j = i + 1; j < 8; ++j) distinct = distinct && codes[i] != codes[j];
        check(distinct, "every kiln.world result code is distinct");
    }
    // v4 only appends: the v3 table (the "KilnWorldApi" entry) is a prefix of it, entry for entry.
    const auto& v3 = fx.at("KilnWorldApi").at("fields");
    const auto& v4 = now.at("KilnWorldApiV4").at("fields");
    bool prefix = v3.size() < v4.size();
    for (size_t i = 0; prefix && i < v3.size(); ++i) prefix = v3[i] == v4[i];
    check(prefix && fx.at("KilnWorldApi").at("size").get<size_t>() == offsetof(KilnWorldApi, game_program_find),
          "abi.json: v3 is a prefix of v4 and ends where v4's first member starts");
}
// A stage that reads kiln.world's directory (what the first typed bind on a worker does: game_program_list and
// game_program_contract) while another thread holds the directory's lock: the read gives up after its bound instead of
// waiting inside the game's step.
std::atomic<int32_t> g_dir_read{-99};
std::atomic<bool> g_dir_done{false};
void KILN_CALL st_dir_read(KilnGameSlice*, void*) {
    g_dir_read = game_program_list(KILN_WORLD_SERVER, idx("fuel_setup"), KILN_GAME_LIST_WRITE, nullptr, 0);
    g_dir_done = true;
}
void directory_read_in_stage() {
    const KilnHandle h = wrap(95, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_dir_read, nullptr);
    wpre();
    Job job(2);
    run_slice(wentry(0), job);
    const int32_t unlocked = g_dir_read.load();
    check(unlocked >= 0, "a directory read inside a stage: the program's write list when nobody holds the lock");
    const uint64_t bypasses = game_programs_test_directory_bypasses();
    g_dir_done = false;
    g_dir_read = -99;
    game_programs_test_hold_directory_lock(true);
    std::thread worker([&] {
        Job j(2);
        run_slice(wentry(0), j);
    });
    bool done = false;
    for (int i = 0; i < 500 && !(done = g_dir_done.load()); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
    game_programs_test_hold_directory_lock(false);
    worker.join();
    check(done && g_dir_read.load() == -1 && game_programs_test_directory_bypasses() > bypasses,
          "a directory read inside a stage gives up after its bound while the lock is held (no unbounded wait on a worker)");
    game_program_unwrap(h);
    wpre();
}
}  // namespace

void archetype_stop_counts_per_world() {
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_CLIENT); // discard earlier tests' counts
    uint8_t record[0x38]{};
    uintptr_t entry = 0xAA00, layout = 0xBB00, out = 0;
    uint32_t end = 1, entity = 17;
    std::memcpy(record + L.slice_entry, &entry, 8);
    std::memcpy(record + L.slice_layout, &layout, 8);
    std::memcpy(record + L.slice_end, &end, 4);
    Frame f{}; f.entry = entry; f.record = reinterpret_cast<uintptr_t>(record); f.count = 1;
    KilnGameSlice slice{}; slice.size = sizeof slice; slice.internal = &f; slice.count = 1; slice.entity_ids = &entity;
    t_frame = &f; t_in_slice = true;
    slice.world_kind = KILN_WORLD_SERVER;
    check(game_slice_archetype(&slice, &out) == KILN_OK, "count sources: server slice reads its record");
    slice.world_kind = KILN_WORLD_CLIENT;
    check(game_slice_archetype(&slice, &out) == KILN_OK, "count sources: client slice reads its record");
    t_frame = nullptr; t_in_slice = false;
    const int server = logs_with("world 0 stops: WhenArchetype read 1 slices");
    const int client = logs_with("world 2 stops: WhenArchetype read 1 slices");
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    check(logs_with("world 0 stops: WhenArchetype read 1 slices") == server + 1,
          "count sources: server stop reports only server reads");
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_CLIENT);
    check(logs_with("world 2 stops: WhenArchetype read 1 slices") == client + 1,
          "count sources: server stop did not steal the client world's proof counters");
}

int main(int argc, char** argv) {
    fake_api();
    layout();
    client_lifecycle_row();
    directory();
    lab_switch();
    wraps_basics();
    in_program_rows();
    wraps_foreign();
    wraps_worlds();
    wraps_native_set();
    wraps_owners();
    release_under_load();
    const auto old_session = game_world_session(KILN_WORLD_SERVER);
    const auto client_session = game_world_session(KILN_WORLD_CLIENT);
    world_event(KILN_WORLD_STOPPING, KILN_WORLD_SERVER);
    world_event(KILN_WORLD_STARTED, KILN_WORLD_SERVER);
    check(game_world_session(KILN_WORLD_SERVER) > old_session && game_world_session(KILN_WORLD_CLIENT) == client_session,
          "session advances across stop/start without polling; the other world kind is unchanged");
    wpre();
    stopping_under_load();
    stopping_timeout();
    tick_fault_closes_admission();
    retirement_under_load();
    record_link_refused();
    per_entity();
    foreign_views();
    directory_read_in_stage();
    stress_steps();
    stress_overlap();
    archetype_stop_counts_per_world();
    if (argc > 1) abi(argv[1]);
    else check(false, "pass tests/abi.json");
    std::printf("world game programs: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
