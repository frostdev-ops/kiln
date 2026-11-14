// SPDX-License-Identifier: MIT
// kiln.world v4 without the game: the program directory of a fake world table (names, game and mod programs, client
// programs with no update), the players mask and the lab parallel switch, and the v4 ABI layout (tests/abi.json).
#include "../game_programs.cpp"

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
        F(KilnWorldApi, lab_parallel);
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
    if (argc > 1) abi(argv[1]);
    else check(false, "pass tests/abi.json");
    std::printf("world game programs: %s %d checks, %d failures\n", failures ? "FAIL" : "PASS", checks, failures);
    return failures ? 1 : 0;
}
