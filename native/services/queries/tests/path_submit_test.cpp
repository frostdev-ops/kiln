// SPDX-License-Identifier: MIT
// Host test of path_submit's admission against kiln.world's in_program (run.sh): path.cpp itself, with the service's
// globals faked, a voxel grid in plain memory and a fake kiln.world. The game's path calls are never reached here.
#include <cstdio>
#include <cstdlib>
#include <string>

#include "../path.cpp"

static int g_checks = 0;
#define CHECK(x)                                                              \
    do {                                                                      \
        ++g_checks;                                                           \
        if (!(x)) {                                                           \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); \
            std::exit(1);                                                     \
        }                                                                     \
    } while (0)

// ---- what queries.cpp would provide ----
namespace kq {
const KilnApi* g_api = nullptr;
KilnOwner g_owner = 1;
const KilnWorldApi* g_world = nullptr;
const KilnCapsApi* g_caps = nullptr;
GridLayout g_layout{};
std::atomic<bool> g_grid_ok{false};
void logf(int32_t, const char*, ...) {}
Clock now() {
    Clock c;
    c.session = 1;
    c.step = 10;
    c.world = true;
    return c;
}
bool world_ready(const Clock& c) { return c.world; }
bool seh_copy(uintptr_t address, void* out, size_t n) {
    if (!address) return false;
    std::memcpy(out, reinterpret_cast<const void*>(address), n);
    return true;
}
bool seh_path_defaults(uintptr_t, void*) { return false; }
bool seh_path_create(uintptr_t, void*, uint32_t*, const void*) { return false; }
bool seh_path_result(uintptr_t, void*, void*, uint32_t, bool*) { return false; }
bool seh_path_destroy(uintptr_t, void*, uint32_t, uint32_t, bool*) { return false; }
}  // namespace kq

using namespace kq;

namespace {
// realm 0's ray grid: origin -1000 on each axis, 64 chunks of 64 m (layout below)
struct FakeRayGrid {
    float origin[3] = {-1000, -1000, -1000};
    uint32_t shift = 6, count_h = 64, count_v = 64;
};
FakeRayGrid g_ray_grid;
uintptr_t g_realms[1] = {reinterpret_cast<uintptr_t>(&g_ray_grid)};
struct FakeVoxelGrid {
    uintptr_t realms = reinterpret_cast<uintptr_t>(g_realms);
    uint64_t count = 1;
} g_voxel_grid;
int g_grid_lookups = 0;

// kiln.world: in_program as the test sets it; inside a stage it answers no components, as world.cpp's t_component does
uint32_t g_in_program = 0;
bool g_in_stage = false;
uint64_t g_stamp = 77;
float g_transform[12] = {};
KilnWorldApi g_fake_world{};
KilnApi g_fake_api{};
bool g_tick = true;

KilnQueryPathRequest request() {
    KilnQueryPathRequest r{};
    r.size = sizeof r;
    r.anchor = 2000;
    r.from = KilnVec3{0, 0, 0};
    r.to = KilnVec3{3, 0, 0};
    r.horizontal_space = 0.5;
    r.vertical_space = 2;
    r.max_iterations = 2000;
    return r;
}

KilnResult submit(KilnQueryPathAdmission* out) {
    const KilnQueryPathRequest r = request();
    *out = {};
    out->size = sizeof *out;
    return t_path_submit(5, &r, out);
}
std::string reason(const KilnQueryPathAdmission& a) { return std::string(a.reason.ptr, a.reason.len); }
}  // namespace

int main() {
    g_fake_api.size = sizeof g_fake_api;
    g_fake_api.abi_version = KILN_ABI_VERSION;
    g_fake_api.on_tick_thread = [] { return g_tick; };
    g_fake_api.post = [](KilnOwner, KilnTaskFn, void*) -> KilnResult { return KILN_OK; };
    g_fake_api.server_world = []() -> uintptr_t { return 0x700000; };
    g_fake_api.singleton = [](uintptr_t, KilnStr) -> uintptr_t {
        ++g_grid_lookups;
        return reinterpret_cast<uintptr_t>(&g_voxel_grid);
    };
    g_api = &g_fake_api;
    g_fake_world.header = {sizeof g_fake_world, KILN_WORLD_VERSION};
    g_fake_world.exists = [](KilnEntity e) { return !g_in_stage && e == 2000; };
    g_fake_world.component = [](KilnEntity, uint32_t hash, uint32_t* size) -> const void* {
        if (g_in_stage) return *size = 0, nullptr;
        if (hash == 11) return *size = sizeof g_transform, g_transform;
        if (hash == 12) return *size = sizeof g_stamp, &g_stamp;
        return *size = 0, nullptr;
    };
    g_fake_world.in_program = [] { return g_in_program; };
    g_world = &g_fake_world;
    g_layout = GridLayout{0, 8, 8, 0, 12, 16, 20};
    g_grid_ok = true;
    g_path_on = true;
    h_transform = 11;
    h_stamp = 12;

    KilnQueryPathAdmission a{};
    CHECK(submit(&a) == KILN_OK && a.ok && a.request != 0 && a.status == KQ_PENDING && g_queue.open() == 1);

    // inside a program's update or a wrap stage: refused before the grid or the anchor is read, nothing queued
    g_in_program = 1;
    const int lookups = g_grid_lookups;
    CHECK(submit(&a) == KILN_ERR_WRONG_THREAD && !a.ok && a.request == 0 && reason(a) == "InsideProgram");
    CHECK(g_grid_lookups == lookups && g_queue.open() == 1);
    // inside a stage kiln.world answers no components: without the gate this would be refused OwnerGone, a refusal
    // that is not true and would read otherwise when asked again from a tick handler
    g_in_stage = true;
    CHECK(submit(&a) == KILN_ERR_WRONG_THREAD && a.status != KQ_OWNER_GONE && reason(a) == "InsideProgram");
    g_in_stage = false;

    // an older kiln.world whose table ends before in_program: its slot is not read, the submit is admitted as before
    g_fake_world.header.size = offsetof(KilnWorldApi, in_program);
    CHECK(submit(&a) == KILN_OK && a.ok && g_queue.open() == 2);
    g_fake_world.header.size = sizeof g_fake_world;
    g_fake_world.in_program = nullptr;
    CHECK(submit(&a) == KILN_OK && a.ok && g_queue.open() == 3);
    g_fake_world.in_program = [] { return g_in_program; };

    // off the tick thread the old refusal comes first, without a reason
    g_tick = false;
    CHECK(submit(&a) == KILN_ERR_WRONG_THREAD && a.reason.len == 0);
    g_tick = true;
    g_in_program = 0;
    CHECK(submit(&a) == KILN_OK && a.ok && g_queue.open() == 4);  // 0 again after the program: admitted again
    std::printf("queries path admission: PASS (%d checks)\n", g_checks);
    return 0;
}
