// SPDX-License-Identifier: MIT
// Host tests of kiln.queries' pure parts (run.sh). Each CHECK names what it proves; a failure prints the line and exits 1.
#include <array>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <string>
#include <vector>

#include "../budget.h"
#include "../config.h"
#include "../entities.h"
#include "../grid.h"
#include "../path_core.h"
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


// ---- paths -----------------------------------------------------------------------------------------------------------

static std::vector<RawPoint> route(std::initializer_list<std::array<float, 3>> pts, uint8_t move = 0) {
    std::vector<RawPoint> v;
    for (auto& p : pts) v.push_back(RawPoint{p[0], p[1], p[2], move, {}});
    return v;
}

static void test_path_classify() {
    const double target[3] = {10, 0, 0};
    // found: walking points, the last within 1.5 m (XZ) and 3 m (Y) of the target; length is along the points
    auto walk = route({{0, 0, 0}, {5, 0, 0}, {9, 2.9f, 0}});
    PathOutcome o = classify(1, 3, true, walk, target);
    CHECK(o.status == KQ_FOUND && o.ok && o.reaches && o.points.size() == 3 && o.length > 9.9 && o.length < 10.0 && std::strlen(o.reason) == 0);
    // the same route ending elsewhere is partial: ok, and not reaching
    const double far[3] = {20, 0, 0};
    o = classify(1, 3, true, walk, far);
    CHECK(o.status == KQ_PARTIAL && o.ok && !o.reaches && o.points.size() == 3);
    const double high[3] = {9, 6, 0};  // near in XZ, 3.1 m above
    CHECK(classify(1, 3, true, walk, high).status == KQ_PARTIAL);
    // every game result that is no route, by its own word
    struct Row { uint8_t game; uint32_t status; const char* reason; };
    for (const Row& r : {Row{0, KQ_UNKNOWN, "NoEngineAnswer"}, Row{2, KQ_NO_PATH, "NoPathFound"}, Row{3, KQ_NO_PATH, "NoFreePath"}, Row{4, KQ_NO_PATH, "NoOccupiedPath"},
                         Row{5, KQ_NO_START, "NoStartNodeFound"}, Row{6, KQ_NO_TARGET, "NoTargetNodeFound"}, Row{7, KQ_ITERATION_LIMIT, "MaxIterationsReached"},
                         Row{8, KQ_OUT_OF_MEMORY, "OpenListOutOfMemory"}, Row{9, KQ_OUT_OF_MEMORY, "NodesOutOfMemory"}, Row{10, KQ_UNKNOWN, "UnknownGameResult"},
                         Row{255, KQ_UNKNOWN, "UnknownGameResult"}}) {
        o = classify(r.game, 3, true, walk, target);  // points that came with it are not a route
        CHECK(o.status == r.status && !o.ok && !o.reaches && o.points.empty() && o.length == 0 && std::strcmp(o.reason, r.reason) == 0);
    }
    o = classify(1, 0, false, {}, target);
    CHECK(o.status == KQ_MALFORMED && std::strcmp(o.reason, "EmptyFoundPath") == 0 && !o.ok);
    o = classify(1, 4097, false, {}, target);
    CHECK(o.status == KQ_TRUNCATED && std::strcmp(o.reason, "PointLimitExceeded") == 0 && o.points.empty());
    o = classify(1, 3, false, {}, target);  // the array could not be read
    CHECK(o.status == KQ_MALFORMED && std::strcmp(o.reason, "MalformedPoints") == 0);
    auto nan = walk;
    nan[1].y = std::nanf("");
    CHECK(classify(1, 3, true, nan, target).status == KQ_MALFORMED);
    // a move that is not walking is never found, though the route ends on the target; its points stay for diagnosis
    auto jump = walk;
    jump[1].move = 2;
    o = classify(1, 3, true, jump, target);
    CHECK(o.status == KQ_UNSUPPORTED && !o.ok && !o.reaches && o.length == 0 && o.points.size() == 3 && o.points[1].raw_move == 2);
    auto odd = walk;
    odd[2].move = 77;
    CHECK(classify(1, 3, true, odd, target).status == KQ_UNSUPPORTED);
    // one point on the target is a route
    o = classify(1, 1, true, route({{10, 0, 0}}), target);
    CHECK(o.status == KQ_FOUND && o.length == 0);
}

static KilnQueryPathRequest request(double x = 3) {
    KilnQueryPathRequest r{};
    r.size = sizeof r;
    r.anchor = 2000;
    r.from = KilnVec3{0, 0, 0};
    r.to = KilnVec3{x, 0, 0};
    r.horizontal_space = 0.5;
    r.vertical_space = 2;
    r.max_iterations = 2000;
    return r;
}

static void test_path_params() {
    PathParams p;
    CHECK(path_params(request(), &p) && p.hspace == 1 && p.vspace == 4 && p.max_iterations == 2000 && !p.fallback && p.anchor == 2000 && p.target[0] == 3);
    CHECK(path_params(request(0), &p));      // the same point is a request (the game answers a same-start route)
    CHECK(path_params(request(200), &p));
    CHECK(!path_params(request(200.5), &p));
    KilnQueryPathRequest r = request();
    r.to.y = std::nan("");
    CHECK(!path_params(r, &p));
    r = request();
    r.horizontal_space = -0.1;
    CHECK(!path_params(r, &p));
    r.horizontal_space = 16.5;
    CHECK(!path_params(r, &p));
    r = request();
    r.max_iterations = 0;
    CHECK(!path_params(r, &p));
    r.max_iterations = 65536;
    CHECK(!path_params(r, &p));
    r = request();
    r.closest_fallback = 2;
    CHECK(!path_params(r, &p));
    r = request();
    r.anchor = 0;
    CHECK(!path_params(r, &p));
    r = request();
    r.closest_fallback = 1;
    r.horizontal_space = 0.75;  // rounded up to whole half metres
    CHECK(path_params(r, &p) && p.fallback && p.hspace == 2);
    CHECK(sizeof(KilnQueryPathRequest) == 80 && sizeof(KilnQueryPathAdmission) == 40 && sizeof(KilnQueryPathUpdate) == 88 && sizeof(KilnQueryPathPoint) == 32 &&
          sizeof(KilnQueriesApi) == 56);
}

static PathParams params() {
    PathParams p;
    path_params(request(), &p);
    p.stamp = 42;
    return p;
}

static KilnResult take(PathQueue& q, KilnOwner owner, KilnQueryPathUpdate* u, std::vector<KilnQueryPathPoint>* pts = nullptr, uint32_t capacity = 4096) {
    static KilnQueryPathPoint buffer[4096];
    uint32_t needed = 0;
    *u = KilnQueryPathUpdate{};
    u->size = sizeof *u;
    const KilnResult rc = q.poll(owner, u, buffer, capacity, &needed);
    if (rc == KILN_OK && pts) pts->assign(buffer, buffer + u->point_count);
    return rc;
}

static void test_path_queue() {
    const double target[3] = {3, 0, 0};
    PathQueue q;
    uint64_t t = 0;
    KilnQueryPathUpdate u{};
    const KilnOwner A = 7, B = 8;
    // admission: a token, and the first update waits
    CHECK(q.submit(A, params(), 1, 100, 0, &t) == KQ_PENDING && t == 1 && q.open() == 1 && q.queued(A) == 1);
    CHECK(take(q, A, &u) == KILN_OK && u.before == KQS_CREATED && u.after == KQS_QUEUED && u.status == KQ_PENDING && !u.terminal && u.request == 1 &&
          u.generation == 1 && u.anchor == 2000 && u.game_result == KILN_QUERY_PATH_NO_GAME_RESULT && u.point_count == 0 && u.sequence == 1);
    CHECK(take(q, A, &u) == KILN_ERR_NOT_FOUND);
    CHECK(take(q, B, &u) == KILN_ERR_NOT_FOUND);  // another owner's queue is not this one
    // the program: create (id 0 = the buffer was full, tried again), then searching, then an answer
    uint32_t creates = 0;
    PathQueue::Work w = q.next(1, 1, &creates);
    CHECK(w.op == PathQueue::Op::Create && w.check_identity && creates == 1);
    q.created(1, 0);
    CHECK(q.queued(A) == 0);
    w = q.next(1, 1, &creates);
    CHECK(w.op == PathQueue::Op::Create);
    q.created(1, 55);
    CHECK(take(q, A, &u) == KILN_OK && u.before == KQS_QUEUED && u.after == KQS_SEARCHING && !u.terminal);
    w = q.next(1, 1, &creates);
    CHECK(w.op == PathQueue::Op::Poll && w.game_id == 55);
    q.answered(1, 1, classify(1, 2, true, route({{0, 0, 0}, {3, 0, 0}}), target));
    // the terminal waits with its points; a buffer that is too small takes nothing
    uint32_t needed = 0;
    KilnQueryPathPoint small[1];
    u.size = sizeof u;
    CHECK(q.poll(A, &u, small, 1, &needed) == KILN_ERR_BUFFER_TOO_SMALL && needed == 2 && q.queued(A) == 1);
    // the game's query is destroyed whether or not anyone polled; the slot stays until both happened
    w = q.next(1, 1, &creates);
    CHECK(w.op == PathQueue::Op::Destroy && w.game_id == 55 && !w.check_identity);
    q.destroyed(1);
    CHECK(q.open() == 1);
    std::vector<KilnQueryPathPoint> pts;
    CHECK(take(q, A, &u, &pts) == KILN_OK && u.terminal && u.ok && u.status == KQ_FOUND && u.after == KQS_COMPLETED && u.before == KQS_SEARCHING && u.game_result == 1 &&
          u.reaches_target && u.point_count == 2 && pts.size() == 2 && pts[1].position.x == 3 && u.length == 3 && u.reason.len == 0 && u.steps == 3);
    CHECK(q.open() == 0);
    // polled first, destroyed later: the same
    CHECK(q.submit(A, params(), 1, 100, 0, &t) == KQ_PENDING && t == 2);
    q.next(2, 1, &creates);
    q.created(2, 56);
    q.answered(2, 2, classify(2, 0, false, {}, target));
    while (take(q, A, &u) == KILN_OK && !u.terminal) {}
    CHECK(u.terminal && !u.ok && u.status == KQ_NO_PATH && u.point_count == 0 && std::string(u.reason.ptr, u.reason.len) == "NoPathFound" && q.open() == 1);
    CHECK(q.next(2, 1, &creates).op == PathQueue::Op::Destroy);
    q.destroyed(2);
    CHECK(q.open() == 0);

    // limits: 8 for one owner, 32 in all, counting results nobody polled yet; a refusal queues nothing
    PathQueue full;
    for (int i = 0; i < 8; ++i) CHECK(full.submit(A, params(), 1, 100, 0, &t) == KQ_PENDING);
    CHECK(full.submit(A, params(), 1, 100, 0, &t) == KQ_QUEUE_FULL && full.queued(A) == 8);
    for (KilnOwner o = 20; o < 23; ++o)
        for (int i = 0; i < 8; ++i) CHECK(full.submit(o, params(), 1, 100, 0, &t) == KQ_PENDING);
    CHECK(full.open() == 32 && full.submit(99, params(), 1, 100, 0, &t) == KQ_QUEUE_FULL);
    // 8 creates in one update, the rest wait for the next
    creates = 0;
    int made = 0;
    for (uint64_t token : full.tokens(1))
        if (full.next(token, 1, &creates).op == PathQueue::Op::Create) ++made;
    CHECK(made == 8 && creates == 8);
    // a cancelled request frees its slot once its terminal is polled (it never had a game query)
    CHECK(full.cancel(A, 1) == KILN_OK && full.cancel(A, 1) == KILN_ERR_NOT_FOUND && full.cancel(B, 2) == KILN_ERR_NOT_FOUND && full.cancel(A, 999) == KILN_ERR_NOT_FOUND);
    CHECK(full.submit(A, params(), 1, 100, 0, &t) == KQ_QUEUE_FULL);  // the terminal is not polled yet
    int terminals = 0;
    while (take(full, A, &u) == KILN_OK)
        if (u.terminal) {
            ++terminals;
            CHECK(u.request == 1 && u.status == KQ_CANCELLED && u.after == KQS_CANCELLED && std::string(u.reason.ptr, u.reason.len) == "Cancelled");
        }
    CHECK(terminals == 1 && full.open() == 31 && full.submit(A, params(), 1, 100, 0, &t) == KQ_PENDING);

    // the deadline: 10 s and 150 steps, both; one terminal only
    PathQueue late;
    CHECK(late.submit(A, params(), 1, 100, 1'000, &t) == KQ_PENDING);
    late.deadlines(1, 250, 5'000'000'000ull);                      // the steps passed, the time did not
    late.deadlines(1, 249, 20'000'000'000ull);                     // the time passed, the steps did not
    CHECK(late.queued(A) == 1);
    late.deadlines(1, 250, 10'000'001'000ull);
    late.deadlines(1, 251, 11'000'000'000ull);
    CHECK(late.queued(A) == 2);
    take(late, A, &u);
    CHECK(take(late, A, &u) == KILN_OK && u.terminal && u.status == KQ_TIMED_OUT && u.after == KQS_TIMED_OUT && late.open() == 0);
    CHECK(late.cancel(A, t) == KILN_ERR_NOT_FOUND);

    // a request that ended while its create was on the way still has its game query destroyed
    PathQueue race;
    race.submit(A, params(), 1, 100, 0, &t);
    creates = 0;
    race.next(t, 1, &creates);
    race.cancel(A, t);
    race.created(t, 77);
    w = race.next(t, 1, &creates);
    CHECK(w.op == PathQueue::Op::Destroy && w.game_id == 77);
    CHECK(race.queued(A) == 2);  // created->queued and the cancel: no "searching" after the end
    // the anchor went
    race.submit(A, params(), 1, 100, 0, &t);
    race.owner_gone(t);
    race.owner_gone(t);
    int gone = 0;
    while (take(race, A, &u) == KILN_OK) gone += u.terminal && u.status == KQ_OWNER_GONE && u.after == KQS_CANCELLED;
    CHECK(gone == 1);

    // retire: the queue is dropped, later calls are refused, and the game's query is still destroyed
    PathQueue gone_owner;
    gone_owner.submit(A, params(), 1, 100, 0, &t);
    creates = 0;
    gone_owner.next(t, 1, &creates);
    gone_owner.created(t, 90);
    gone_owner.retire(A);
    gone_owner.retire(A);
    CHECK(take(gone_owner, A, &u) == KILN_ERR_REVOKED && gone_owner.cancel(A, t) == KILN_ERR_REVOKED && gone_owner.submit(A, params(), 1, 100, 0, &t) == KQ_OWNER_RETIRED);
    CHECK(gone_owner.open() == 1);
    w = gone_owner.next(1, 1, &creates);
    CHECK(w.op == PathQueue::Op::Destroy && w.game_id == 90);
    gone_owner.destroyed(1);
    CHECK(gone_owner.open() == 0 && gone_owner.owners().empty());

    // the world closes: what waits ends WorldClosed, a result nobody polled yet becomes WorldClosed, and no game query of
    // that world is ever destroyed in another
    PathQueue closing;
    uint64_t done = 0, pending = 0;
    closing.submit(A, params(), 1, 100, 0, &done);
    closing.submit(A, params(), 1, 100, 0, &pending);
    creates = 0;
    closing.next(done, 1, &creates);
    closing.created(done, 5);
    closing.answered(done, 1, classify(1, 1, true, route({{3, 0, 0}}), target));
    closing.next(pending, 1, &creates);
    closing.created(pending, 6);
    closing.world_closed(1);
    CHECK(closing.tokens(1).empty() || closing.next(done, 1, &creates).op == PathQueue::Op::None);
    int closed = 0, found = 0;
    while (take(closing, A, &u) == KILN_OK)
        if (u.terminal) {
            closed += u.status == KQ_WORLD_CLOSED && !u.ok && u.point_count == 0 && u.after == KQS_CANCELLED;
            found += u.status == KQ_FOUND;
        }
    CHECK(closed == 2 && found == 0 && closing.open() == 0);
    CHECK(take(closing, A, &u) == KILN_ERR_NOT_FOUND);  // polling still works with no world
    // the next world's requests are its own
    CHECK(closing.submit(A, params(), 2, 400, 0, &t) == KQ_PENDING && closing.tokens(1).empty() && closing.tokens(2).size() == 1);

    // a fault in the game: everything pending is refused once, and the program gets no more work
    PathQueue dead;
    dead.submit(A, params(), 1, 100, 0, &t);
    dead.fail_all("GameCallFault");
    CHECK(dead.dead() && dead.tokens(1).empty());
    take(dead, A, &u);
    CHECK(take(dead, A, &u) == KILN_OK && u.terminal && u.status == KQ_REFUSED && std::string(u.reason.ptr, u.reason.len) == "GameCallFault" && dead.open() == 0);
}

int main() {
    test_grid();
    test_ray_numbers();
    test_ray_mapping();
    test_budget();
    test_config();
    test_entities();
    test_path_classify();
    test_path_params();
    test_path_queue();
    std::printf("queries-test: PASS (%d checks)\n", g_checks);
    return 0;
}
