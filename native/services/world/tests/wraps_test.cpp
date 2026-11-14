// SPDX-License-Identifier: MIT
// kiln.world v4 without the game: the program directory of a fake world table (names, game and mod programs, client
// programs with no update), the players mask and the lab parallel switch, the wraps over a fake program runner (stage
// order, one Replace, index repair after faults, in-flight balance, the prediction forward, foreign swaps, world stop,
// budgets, owners, two stress rows on 8 threads), and the v4 ABI layout (tests/abi.json).
#include "../game_programs.cpp"
#include "../wraps.cpp"
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
void logf(int, const char* fmt, ...) {
    if (!std::getenv("KILN_TEST_LOG")) return;
    va_list ap;
    va_start(ap, fmt);
    std::vfprintf(stderr, fmt, ap);
    va_end(ap);
    std::fputc('\n', stderr);
}
bool seh_copy(void* to, const void* from, size_t size) {
    if (!from || !to) return false;
    std::memcpy(to, from, size);
    return true;
}
// A "fault" in a stage is a thrown int here; kiln.world's frames see the same: the guard returns false.
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
// kiln.types with only what kiln.world asks of it here: whether native SET owns the program runner.
std::atomic<uint32_t> g_fake_external{0};
const KilnTypesApi* program_types() {
    static KilnTypesApi t = [] {
        KilnTypesApi v{};
        v.header = {sizeof(KilnTypesApi), KILN_TYPES_VERSION};
        v.external_runner = [] { return g_fake_external.load(); };
        return v;
    }();
    return &t;
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
    g_fake.service = [](KilnStr, uint32_t) -> const void* { return nullptr; };
    g_fake.path = [](int32_t, char*, size_t, size_t*) -> KilnResult { return KILN_ERR_UNAVAILABLE; };
    g_api = &g_fake;
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
template <int N>
void game_update(void* raw) {
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
    check(r1 && !r2 && rc == KILN_ERR_INVALID_ARGUMENT, "a second Replace is refused (kiln.log names the first's owner)");
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
    wraps_test_budget_ns(1000000);
    game_program_unwrap(slow);
    wpre();
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
    // release: queued, then 0 after the world's Pre
    const KilnHandle a = wrap(40, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    const KilnHandle b = wrap(40, "fuel_network", KILN_WRAP_AFTER, 0, &st_trace, nullptr);
    wpre();
    const KilnHandle c = wrap(40, "impact_prepare", KILN_WRAP_AFTER, 0, &st_trace, nullptr);
    uint32_t pending = 99;
    check(game_wraps_release(40, &pending) == KILN_OK && pending == 1 && winfo(c).state == KILN_WRAP_REMOVED &&
              winfo(a).remove_queued == 1,
          "release: a pending wrap goes at once, the server world still holds two");
    wpre();
    check(game_wraps_release(40, &pending) == KILN_OK && pending == 0 && winfo(a).state == KILN_WRAP_REMOVED &&
              winfo(b).state == KILN_WRAP_REMOVED && update_of(wentry(0)) == reinterpret_cast<uintptr_t>(kWUpdates[0]),
          "after the Pre nothing of the owner can run");
    // a revoked owner's wraps go in a Pre (at most once a second)
    g_fake.post = [](KilnOwner owner, KilnTaskFn, void*) -> KilnResult { return owner == 41 ? KILN_ERR_REVOKED : KILN_OK; };
    const KilnHandle d = wrap(41, "fuel_setup", KILN_WRAP_BEFORE, 0, &st_trace, nullptr);
    wpre();
    g_next_reap = {};
    wpre();
    g_next_reap = {};
    wpre();
    check(winfo(d).state == KILN_WRAP_REMOVED && winfo(d).reason == KILN_WRAP_OWNER_GONE, "a revoked owner's wraps are removed");
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
        auto& l = list("KilnWorldApiV4", sizeof(KilnWorldApi));
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
        F(KilnWorldApi, wrap_original);
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
        F(KilnGameWrapInfo, remove_queued); F(KilnGameWrapInfo, reserved); F(KilnGameWrapInfo, detail);
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
    // v4 only appends: the v3 table (the "KilnWorldApi" entry) is a prefix of it, entry for entry.
    const auto& v3 = fx.at("KilnWorldApi").at("fields");
    const auto& v4 = now.at("KilnWorldApiV4").at("fields");
    bool prefix = v3.size() < v4.size();
    for (size_t i = 0; prefix && i < v3.size(); ++i) prefix = v3[i] == v4[i];
    check(prefix && fx.at("KilnWorldApi").at("size").get<size_t>() == offsetof(KilnWorldApi, game_program_find),
          "abi.json: v3 is a prefix of v4 and ends where v4's first member starts");
}
}  // namespace

int main(int argc, char** argv) {
    fake_api();
    layout();
    directory();
    lab_switch();
    wraps_basics();
    wraps_foreign();
    wraps_worlds();
    wraps_native_set();
    wraps_owners();
    stress_steps();
    stress_overlap();
    if (argc > 1) abi(argv[1]);
    else check(false, "pass tests/abi.json");
    std::printf("world game programs: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
