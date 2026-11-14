// SPDX-License-Identifier: MIT
// Host tests of kiln.queries' pure parts (run.sh). Each CHECK names what it proves; a failure prints the line and exits 1.
#include <cstdio>
#include <cstdlib>
#include <map>
#include <string>
#include <vector>

#include "../budget.h"
#include "../config.h"
#include "../entities.h"
#include "../grid.h"
#include "../ray.h"

static int g_checks = 0;
#define CHECK(x)                                                              \
    do {                                                                      \
        ++g_checks;                                                           \
        if (!(x)) {                                                           \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); \
            std::exit(1);                                                     \
        }                                                                     \
    } while (0)

using namespace kq;

// ---- fake game memory for the grid reader ----
static std::map<uintptr_t, std::vector<unsigned char>> g_mem;
static int g_reads = 0;
static uintptr_t g_fault_at = 0;
static bool fake_read(uintptr_t a, void* out, size_t n) {
    ++g_reads;
    if (a == g_fault_at) return false;
    for (auto& [base, bytes] : g_mem)
        if (a >= base && a + n <= base + bytes.size()) {
            std::memcpy(out, bytes.data() + (a - base), n);
            return true;
        }
    return false;
}
template <class T> static void poke(uintptr_t base, size_t off, T v) { std::memcpy(g_mem[base].data() + off, &v, sizeof v); }

static const GridLayout kLayout{0x1bf8, 0x1c00, 0x3870, 0x70, 0x10, 0x14, 0x18};
static const uintptr_t kGrid = 0x100000, kRealms = 0x200000, kRayGrid = 0x400000;

static void world(uint64_t realm_count, float ox, float oy, float oz, uint32_t shift, uint32_t ch, uint32_t cv) {
    g_mem.clear();
    g_mem[kGrid].assign(0x2000, 0);
    g_mem[kRealms].assign(0x3870 * 3, 0);
    g_mem[kRayGrid].assign(0x100, 0);
    poke<uintptr_t>(kGrid, 0x1bf8, kRealms);
    poke<uint64_t>(kGrid, 0x1c00, realm_count);
    poke<uintptr_t>(kRealms, 0, kRayGrid);
    poke<float>(kRayGrid, 0x70, ox);
    poke<float>(kRayGrid, 0x74, oy);
    poke<float>(kRayGrid, 0x78, oz);
    poke<uint32_t>(kRayGrid, 0x10, shift);
    poke<uint32_t>(kRayGrid, 0x14, ch);
    poke<uint32_t>(kRayGrid, 0x18, cv);
}

static void test_grid() {
    RealmBox b;
    world(2, -100.0f, 0.0f, -100.0f, 4, 8, 4);  // X,Z: [-100, 28); Y: [0, 64)
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::Ok);
    CHECK(b.origin[0] == -100.0f && b.shift == 4 && b.count_h == 8 && b.count_v == 4);
    // lower bound inclusive, upper exclusive, on each axis
    const float in[3] = {0, 10, 0};
    CHECK(box_contains(b, in));
    for (int axis = 0; axis < 3; ++axis) {
        const float lo = axis == 1 ? 0.0f : -100.0f, hi = axis == 1 ? 64.0f : 28.0f;
        float p[3] = {0, 10, 0};
        p[axis] = lo;
        CHECK(box_contains(b, p));
        p[axis] = std::nextafter(lo, -1e9f);
        CHECK(!box_contains(b, p));
        p[axis] = hi - 0.5f;
        CHECK(box_contains(b, p));
        p[axis] = hi;
        CHECK(!box_contains(b, p));
        p[axis] = hi + 1000.0f;
        CHECK(!box_contains(b, p));
        p[axis] = std::nanf("");
        CHECK(!box_contains(b, p));
        p[axis] = 3e9f;  // would overflow the game's int conversion
        CHECK(!box_contains(b, p));
    }
    // the test is the game's, on p - origin as a float: the last float below the face is already on it once the origin is taken off
    const float edge[3] = {std::nextafter(28.0f, -1e9f), 10, 0};
    CHECK(edge[0] < 28.0f && edge[0] - -100.0f == 128.0f && !box_contains(b, edge));
    // a double just below the upper face that rounds onto it as a float is outside: the test is on the floats
    CHECK(27.9999999999 < 28.0 && static_cast<float>(27.9999999999) == 28.0f);
    const float rounded[3] = {static_cast<float>(27.9999999999), 10, 0};
    CHECK(!box_contains(b, rounded));

    CHECK(read_realm_box(kGrid, 1, kLayout, fake_read, &b) == GridRead::NoGrid);    // in range, no grid yet
    CHECK(read_realm_box(kGrid, 2, kLayout, fake_read, &b) == GridRead::BadRealm);  // not below the live count
    CHECK(read_realm_box(0, 0, kLayout, fake_read, &b) == GridRead::NoGrid);
    world(0, 0, 0, 0, 4, 8, 4);
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::NoGrid);
    world(257, 0, 0, 0, 4, 8, 4);
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::BadMetadata);
    world(1, 0, 0, 0, 31, 8, 4);
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::BadMetadata);
    world(1, 0, 0, 0, 4, 0, 4);
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::BadMetadata);
    world(1, 0, 0, 0, 4, 8, 0);
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::BadMetadata);
    world(1, std::nanf(""), 0, 0, 4, 8, 4);
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::BadMetadata);
    world(1, 0, 0, 0, 4, 8, 4);
    g_fault_at = kRayGrid + 0x70;
    CHECK(read_realm_box(kGrid, 0, kLayout, fake_read, &b) == GridRead::Fault);
    g_fault_at = 0;
    CHECK(sane_layout(kLayout));
    GridLayout bad = kLayout;
    bad.realm_stride = 0;
    CHECK(!sane_layout(bad));
    bad = kLayout;
    bad.origin = -1;
    CHECK(!sane_layout(bad));
}

static void test_ray_numbers() {
    RayFloats r;
    const double a[3] = {3845.25, 935, 1480.25}, b[3] = {3845.25, 735, 1480.25};
    CHECK(narrow_ray(a, b, &r));
    CHECK(r.length == 200.0f && r.from[3] == 0.0f && r.to[3] == 0.0f);
    CHECK((reinterpret_cast<uintptr_t>(r.from) & 15) == 0 && (reinterpret_cast<uintptr_t>(r.to) & 15) == 0);
    CHECK(min_ray_length() == 1e-20f);
    CHECK(!narrow_ray(a, a, &r));  // no length
    // distinct doubles, one point as floats: refused, never "a loaded miss"
    const double c[3] = {1000, 0, 0}, d[3] = {1000.000001, 0, 0};
    CHECK(c[0] != d[0] && static_cast<float>(c[0]) == static_cast<float>(d[0]));
    CHECK(!narrow_ray(c, d, &r));
    const double e[3] = {0, 0, 0}, f256[3] = {256, 0, 0}, over[3] = {256.001, 0, 0};
    CHECK(narrow_ray(e, f256, &r));
    CHECK(!narrow_ray(e, over, &r));
    const double nan[3] = {0, std::nan(""), 0}, inf[3] = {HUGE_VAL, 0, 0}, huge[3] = {1e300, 0, 0}, huge2[3] = {1e300, 1, 0};
    CHECK(!narrow_ray(e, nan, &r));
    CHECK(!narrow_ray(inf, e, &r));
    CHECK(!narrow_ray(huge, huge2, &r));  // finite doubles that are not finite floats
    // both directions
    CHECK(narrow_ray(b, a, &r) && r.length == 200.0f);
}

static void hit(unsigned char* h, bool available, bool hitf, bool outside, float x = 1, float y = 2, float z = 3) {
    std::memset(h, 0, kHitBytes);
    const float p[3] = {x, y, z};
    std::memcpy(h, p, 12);
    const uint32_t mat = 0x2f2607aa;
    const uint16_t vox = 0x6c01;
    std::memcpy(h + 0x0c, &mat, 4);
    std::memcpy(h + 0x10, &vox, 2);
    h[0x12] = available, h[0x13] = hitf, h[0x14] = outside;
}

static void test_ray_mapping() {
    unsigned char h[kHitBytes];
    RayHit out{};
    hit(h, true, true, false);
    CHECK(read_hit(h, &out) == RayOutcome::Hit && out.material == 0x2f2607aa && out.voxel == 0x6c01 && out.position[1] == 2.0f);
    hit(h, true, false, false);
    CHECK(read_hit(h, &out) == RayOutcome::Clear);
    // not loaded is never clear and never a hit, whatever the hit flag says
    hit(h, false, false, false);
    CHECK(read_hit(h, &out) == RayOutcome::NotLoaded);
    hit(h, false, true, false);
    CHECK(read_hit(h, &out) == RayOutcome::NotLoaded);
    // the last cell was outside the grid: the game says "available, no hit", and it is not clear
    hit(h, true, false, true);
    CHECK(read_hit(h, &out) == RayOutcome::OutOfGrid);
    hit(h, true, true, true);
    CHECK(read_hit(h, &out) == RayOutcome::OutOfGrid);
    hit(h, true, true, false, 1, std::nanf(""), 3);
    CHECK(read_hit(h, &out) == RayOutcome::InvalidHit);
}

static void test_budget() {
    Budget b;
    CHECK(b.limits().max_rays == 64 && b.limits().max_microseconds == 500);
    CHECK(b.admit(1, 10) == Admit::WorldNotReady);  // no world was seen
    b.world_started(1, 10);
    CHECK(!b.ready(1, 10));                          // before the first published Pre of the session
    CHECK(b.admit(1, 10) == Admit::WorldNotReady && b.calls() == 0);
    CHECK(b.ready(1, 11));
    CHECK(!b.ready(2, 11) && !b.ready(0, 11));
    // Pre, Post and a task posted before the next Pre share step 11's bucket
    for (int i = 0; i < 64; ++i) CHECK(b.admit(1, 11) == Admit::Ok);
    CHECK(b.admit(1, 11) == Admit::BudgetExceeded);  // the 65th
    CHECK(b.calls() == 64);
    CHECK(b.admit(1, 12) == Admit::Ok && b.calls() == 1);  // the next published step
    // time: a 700 us call into an empty bucket completes; the next is refused; the one after the step is not
    CHECK(b.admit(1, 13) == Admit::Ok);
    b.charge(700'000);
    CHECK(b.admit(1, 13) == Admit::BudgetExceeded && b.calls() == 1);
    CHECK(b.admit(1, 14) == Admit::Ok);
    b.charge(499'999);
    CHECK(b.admit(1, 14) == Admit::Ok);  // 499.999 us used: still under
    b.charge(1);
    CHECK(b.admit(1, 14) == Admit::BudgetExceeded);
    // the world goes: nothing until the next one's first published step, and that one starts empty
    b.world_stopping();
    CHECK(b.admit(1, 15) == Admit::WorldNotReady);
    b.world_started(2, 15);
    CHECK(b.admit(2, 15) == Admit::WorldNotReady);
    CHECK(b.admit(1, 16) == Admit::WorldNotReady);  // the old session's number
    CHECK(b.admit(2, 16) == Admit::Ok && b.calls() == 1 && b.elapsed_ns() == 0);
    b.set_limits({2, 500});
    CHECK(b.admit(2, 17) == Admit::Ok && b.admit(2, 17) == Admit::Ok && b.admit(2, 17) == Admit::BudgetExceeded);
}

static void test_config() {
    std::string w;
    Limits l = parse_limits(R"({"format":1,"maxRaysPerStep":128,"maxRayMicrosecondsPerStep":2000})", &w);
    CHECK(l.max_rays == 128 && l.max_microseconds == 2000 && w.empty());
    l = parse_limits(R"({"format":1})", &w);
    CHECK(l.max_rays == 64 && l.max_microseconds == 500 && w.empty());
    l = parse_limits(R"({"format":1,"maxRaysPerStep":4096,"maxRayMicrosecondsPerStep":1})", &w);
    CHECK(l.max_rays == 4096 && l.max_microseconds == 1 && w.empty());
    // wrong in one known part: the whole default pair, not half of the file
    for (const char* bad : {R"({"format":1,"maxRaysPerStep":128,"maxRayMicrosecondsPerStep":0})", R"({"format":1,"maxRaysPerStep":4097,"maxRayMicrosecondsPerStep":2000})",
                            R"({"format":2,"maxRaysPerStep":128})", R"({"maxRaysPerStep":128})", R"({"format":1,"maxRaysPerStep":"128"})", R"({"format":1,"maxRaysPerStep":12.5})",
                            R"({"format":1,"maxRaysPerStep":-3})", R"({"format":1,"maxRayMicrosecondsPerStep":10001,"maxRaysPerStep":128})", "[1]", "{ not json", ""}) {
        w.clear();
        l = parse_limits(bad, &w);
        CHECK(l.max_rays == 64 && l.max_microseconds == 500 && !w.empty());
    }
    w.clear();
    l = parse_limits(R"({"format":1,"maxRaysPerStep":32,"colour":"red"})", &w);
    CHECK(l.max_rays == 32 && l.max_microseconds == 500 && w.find("colour") != std::string::npos);
    w.clear();
    l = parse_limits("{\"format\":1, // a comment\n \"maxRaysPerStep\":16}", &w);
    CHECK(l.max_rays == 16 && w.empty());
}

static void test_entities() {
    const std::vector<Origin> all = {{5, 3, 0, 0}, {4, 0, 3, 0}, {9, 0, 0, 0}, {7, -1, -1, -1}, {8, 3.0000001, 0, 0}, {6, 0, 0, std::nan("")}, {0, 0, 0, 0}};
    Shape s;
    s.radius = 3;
    // closed sphere: exactly on it is inside; ties by id; nearest first; negative coordinates; a NaN origin and id 0 are skipped
    CHECK((origins_inside(all, s, 0) == std::vector<uint32_t>{9, 7, 4, 5}));
    CHECK((origins_inside(all, s, 9) == std::vector<uint32_t>{7, 4, 5}));
    s.radius = 0;
    CHECK((origins_inside(all, s, 0) == std::vector<uint32_t>{9}));
    Shape box;
    box.box = true;
    box.half[0] = 3, box.half[1] = 1, box.half[2] = 1;
    CHECK((origins_inside(all, box, 0) == std::vector<uint32_t>{9, 7, 5}));  // 4 is above the box, 8 just past its face
    box.center[0] = -1, box.center[1] = -1, box.center[2] = -1;
    box.half[0] = box.half[1] = box.half[2] = 0;
    CHECK((origins_inside(all, box, 0) == std::vector<uint32_t>{7}));
    CHECK(valid_shape(s) && valid_shape(box));
    s.radius = -1;
    CHECK(!valid_shape(s));
    s.radius = std::nan("");
    CHECK(!valid_shape(s));
    box.half[1] = -0.5;
    CHECK(!valid_shape(box));
    box.half[1] = 0;
    box.center[2] = HUGE_VAL;
    CHECK(!valid_shape(box));
}

int main() {
    test_grid();
    test_ray_numbers();
    test_ray_mapping();
    test_budget();
    test_config();
    test_entities();
    std::printf("queries-test: PASS (%d checks)\n", g_checks);
    return 0;
}
