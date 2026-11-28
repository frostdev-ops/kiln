// SPDX-License-Identifier: MIT
// kiln.markers' admission, visibility and native map list on fakes (no game): chapter 04 §6 M03 (ordering and map
// capacity on a fake list with sentinels: prefix bytes unchanged, a mismatched header refused), M04 (camera and range
// transitions), M14 (visibility collapse), the reason precedence of §4.6, the entry encoder (port of the research
// probe's layout checks) and the heading of §4.5; issue 159's list in a frame and issue 160's tooltip text.
#include <cmath>
#include <cstring>
#include <limits>
#include <string>
#include <string_view>
#include <vector>

#include "check.h"
#include "compass.h"
#include "map_list.h"
#include "select.h"
#include "services/kiln_caps.h"
#include "services/kiln_markers.h"
#include "visibility.h"

using namespace kmk;

namespace {

constexpr double kPi = 3.14159265358979323846;

// The measured layout (client 26430625): list header at +0x36ea00, entries of 0xa0.
MapLayout layout() {
    MapLayout l;
    l.header = 0x36ea00;
    l.capacity = 1024;
    l.entry_size = 0xa0;
    l.position = 0x10;
    l.z = 0x20;
    l.type = 0x30;
    l.visibility = 0x34;
    return l;
}

// A fake UI play data object: the list header and its inline storage, every byte outside our rows a sentinel.
struct FakeUi {
    MapLayout l = layout();
    std::vector<uint8_t> mem;
    FakeUi() : mem(static_cast<size_t>(l.header) + 0x28 + 1024 * 0xa0 + 64, 0xab) { set_header(0, 1024); }
    uint8_t* ui() { return mem.data(); }
    uint8_t* h() { return mem.data() + l.header; }
    void set_header(uint64_t count, uint64_t cap, int64_t data_skew = 0) {
        const uint64_t data = reinterpret_cast<uintptr_t>(h()) + 0x28 + data_skew;
        std::memcpy(h(), &data, 8);
        std::memcpy(h() + 8, &count, 8);
        std::memcpy(h() + 16, &cap, 8);
    }
    uint64_t count() {
        uint64_t c;
        std::memcpy(&c, h() + 8, 8);
        return c;
    }
    uint8_t* entry(uint64_t i) { return h() + 0x28 + i * 0xa0; }
};

ClientRow row(const char* ns, const char* key, uint32_t surfaces, uint32_t source = KILN_MARKER_LOCAL, uint64_t gen = 1) {
    ClientRow r;
    r.source = source;
    r.publisher_generation = gen;
    r.ns = ns;
    r.key = key;
    r.rec.surfaces = surfaces;
    r.rec.x = 3670, r.rec.y = 820, r.rec.z = 1500;
    r.rec.icon = surfaces & KILN_MARKER_MAP ? 0xffe5e444u : 0;
    r.rec.sequence = 1;
    return r;
}

Candidate cand(const ClientRow& r) {
    Candidate c;
    c.requested = r.rec.surfaces;
    c.source_rank = r.source == KILN_MARKER_LOCAL ? 0 : 1;
    c.priority = r.rec.priority;
    c.ns = r.ns;
    c.key = r.key;
    c.publisher_generation = r.publisher_generation;
    c.x = r.rec.x, c.y = r.rec.y, c.z = r.rec.z;
    c.height = r.rec.height;
    c.max_distance = r.rec.max_distance;
    return c;
}

View view_all(uint32_t map_room = 1024) {
    View v;
    v.world_active = true;
    v.cap[0] = v.cap[1] = v.cap[2] = true;
    v.map_room = map_room;
    return v;
}

void with_camera(View* v, double x, double y, double z, double fx = 0, double fz = 1) {
    v->camera = true;
    v->viewport = true;
    v->cam[0] = x, v->cam[1] = y, v->cam[2] = z;
    v->fwd[0] = fx, v->fwd[1] = 0, v->fwd[2] = fz;
}

struct Post {
    Visibility vis;
    std::vector<Note> notes;
    std::vector<Admitted> adm;
    std::vector<uint32_t> order[3];
    void run(const View& v, const std::vector<ClientRow>& rows) {
        std::vector<Candidate> c;
        for (const auto& r : rows) c.push_back(cand(r));
        run(v, rows, c);
    }
    void run(const View& v, const std::vector<ClientRow>& rows, const std::vector<Candidate>& c) {
        notes.clear();
        admit(v, c, &adm, order);
        vis.step(7, rows, adm, &notes);
    }
};

void order_is_exact() {
    kmk_test::begin();
    // equal everything but one field at a time: the earlier field decides
    std::vector<ClientRow> rows = {row("b", "k", KILN_MARKER_COMPASS), row("a", "k", KILN_MARKER_COMPASS),
                                   row("a", "k", KILN_MARKER_COMPASS, KILN_MARKER_RECEIVED), row("a", "j", KILN_MARKER_COMPASS, KILN_MARKER_LOCAL, 2),
                                   row("a", "j", KILN_MARKER_COMPASS)};
    rows[0].rec.priority = 5;  // highest priority wins although its namespace sorts last
    std::vector<Candidate> c;
    for (const auto& r : rows) c.push_back(cand(r));
    View v = view_all();
    with_camera(&v, 3670, 820, 1400);
    std::vector<Admitted> adm;
    std::vector<uint32_t> order[3];
    admit(v, c, &adm, order);
    // 0 (priority), then local "a"/gen 1: key j (4) before k (1), then gen 2 "j" (3), then received (2)
    const std::vector<uint32_t> want = {0, 4, 1, 3, 2};
    CHECK(order[kSurfCompass] == want);
    // distance comes before source: a nearer received row beats a farther local one
    c[2].z = 1450;
    admit(v, c, &adm, order);
    CHECK_EQ(order[kSurfCompass][1], 2u);
    // without a camera every distance is infinite: the tie goes on to source, namespace, generation, key
    View nocam = view_all();
    std::vector<ClientRow> mrows = rows;  // the candidates' names point into these
    std::vector<Candidate> m;
    for (auto& r : mrows) {
        r.rec.surfaces = KILN_MARKER_MAP;
        m.push_back(cand(r));
    }
    m[0].priority = 0;
    admit(nocam, m, &adm, order);
    const std::vector<uint32_t> want2 = {4, 1, 3, 0, 2};
    CHECK(order[kSurfMap] == want2);
    kmk_test::end("selection order: priority, camera distance, source, namespace, generation, key (no insertion order)");
}

void m03_map_capacity() {
    kmk_test::begin();
    FakeUi f;
    f.set_header(1023, 1024);
    const std::vector<uint8_t> before(f.mem.begin(), f.mem.end());
    std::vector<ClientRow> rows = {row("modb", "k", KILN_MARKER_MAP), row("moda", "k", KILN_MARKER_MAP)};
    uint64_t count = 0, room = 0;
    CHECK(read_list(f.ui(), f.l, &count, &room) == ListState::Ok);
    CHECK_EQ(room, 1u);
    Post p;
    p.run(view_all(static_cast<uint32_t>(room)), rows);
    CHECK_EQ(p.adm[1].after, static_cast<uint32_t>(KILN_MARKER_MAP));  // "moda" wins the only slot
    CHECK_EQ(p.adm[0].after, 0u);
    CHECK_EQ(p.adm[0].reason[kSurfMap], static_cast<uint32_t>(KILN_MARKER_MAP_CAPACITY));
    CHECK_EQ(p.notes.size(), 2u);  // one initial event each
    std::vector<MapRow> mr;
    for (uint32_t i : p.order[kSurfMap]) mr.push_back(MapRow{rows[i].rec.x, rows[i].rec.y, rows[i].rec.z, rows[i].rec.icon});
    Appended last;
    ListState st;
    CHECK_EQ(append_rows(f.ui(), f.l, mr.data(), static_cast<uint32_t>(mr.size()), &last, &st), 1u);
    CHECK(st == ListState::Ok);
    CHECK_EQ(f.count(), 1024u);
    // every byte but the count and our one entry is as it was: the 1023 entries before, the header, the tail
    const size_t ours = static_cast<size_t>(f.entry(1023) - f.ui());
    bool prefix = std::memcmp(f.mem.data(), before.data(), static_cast<size_t>(f.l.header) + 8) == 0 &&
                  std::memcmp(f.mem.data() + f.l.header + 16, before.data() + f.l.header + 16, ours - (f.l.header + 16)) == 0 &&
                  std::memcmp(f.mem.data() + ours + 0xa0, before.data() + ours + 0xa0, f.mem.size() - ours - 0xa0) == 0;
    CHECK(prefix);
    // the next step the game cleared and refilled 1022 rows: both fit, the loser recovers with a visibility event only
    f.set_header(1022, 1024);
    CHECK(read_list(f.ui(), f.l, &count, &room) == ListState::Ok);
    p.run(view_all(static_cast<uint32_t>(room)), rows);
    CHECK_EQ(p.adm[0].after, static_cast<uint32_t>(KILN_MARKER_MAP));
    CHECK_EQ(p.notes.size(), 1u);
    if (p.notes.size() == 1) {
        CHECK_EQ(p.notes[0].visibility.before, 0u);
        CHECK_EQ(p.notes[0].visibility.after, static_cast<uint32_t>(KILN_MARKER_MAP));
        CHECK_EQ(p.notes[0].visibility.initial, 0u);
        CHECK_EQ(p.notes[0].visibility.reason[kSurfMap], static_cast<uint32_t>(KILN_MARKER_READY));
        CHECK(std::strcmp(p.notes[0].visibility.key, "k") == 0 && std::strcmp(p.notes[0].visibility.owner, "modb") == 0);
    }
    // a mismatched data pointer or capacity is refused before anything is written
    for (int bad = 0; bad < 3; ++bad) {
        FakeUi g;
        if (bad == 0) g.set_header(10, 1024, 8);
        if (bad == 1) g.set_header(10, 2048);
        if (bad == 2) g.set_header(1025, 1024);
        const std::vector<uint8_t> was(g.mem.begin(), g.mem.end());
        Appended l2;
        CHECK_EQ(append_rows(g.ui(), g.l, mr.data(), 1, &l2, &st), 0u);
        CHECK(st == ListState::BadHeader);
        CHECK(g.mem == was);
    }
    // 1024 candidates with an empty list: at most 128 map rows, the rest SURFACE_BUDGET; world 128 and compass 64 apart
    std::vector<std::string> keys(1024);
    std::vector<ClientRow> many;
    for (int i = 0; i < 1024; ++i) {
        char k[16];
        std::snprintf(k, sizeof k, "k%04d", i);
        many.push_back(row("m", k, KILN_MARKER_MAP | KILN_MARKER_COMPASS | KILN_MARKER_WORLD));
    }
    View v = view_all(1024);
    with_camera(&v, 3670, 820, 1400);
    Post q;
    q.run(v, many);
    CHECK_EQ(q.order[kSurfMap].size(), 128u);
    CHECK_EQ(q.order[kSurfCompass].size(), 64u);
    CHECK_EQ(q.order[kSurfWorld].size(), 128u);
    CHECK_EQ(q.adm[1000].reason[kSurfMap], static_cast<uint32_t>(KILN_MARKER_SURFACE_BUDGET));
    CHECK_EQ(q.notes.size(), 1024u);
    kmk_test::end("M03 ordering and map capacity: one slot to the full tie order, prefix bytes unchanged, recovery without a logical change, bad headers refused, budgets 128/64/128");
}

void m04_camera_range() {
    kmk_test::begin();
    std::vector<ClientRow> rows = {row("m", "w", KILN_MARKER_WORLD | KILN_MARKER_COMPASS)};
    rows[0].rec.max_distance = 50;
    Post p;
    View v = view_all();
    p.run(v, rows);  // no camera
    CHECK_EQ(p.notes.size(), 1u);
    CHECK_EQ(p.notes[0].visibility.initial, 1u);
    CHECK_EQ(p.notes[0].visibility.reason[kSurfWorld], static_cast<uint32_t>(KILN_MARKER_NO_CAMERA));
    CHECK_EQ(p.notes[0].visibility.reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_NO_CAMERA));
    CHECK_EQ(p.notes[0].visibility.reason[kSurfMap], static_cast<uint32_t>(KILN_MARKER_NOT_REQUESTED));
    with_camera(&v, 3670, 820, 1400);  // 100 m away: out of range
    p.run(v, rows);
    CHECK_EQ(p.notes.size(), 1u);
    CHECK_EQ(p.adm[0].reason[kSurfWorld], static_cast<uint32_t>(KILN_MARKER_OUT_OF_RANGE));
    p.run(v, rows);  // nothing changed: no event
    CHECK_EQ(p.notes.size(), 0u);
    with_camera(&v, 3670, 820, 1470);  // 30 m, facing it
    p.run(v, rows);
    CHECK_EQ(p.notes.size(), 1u);
    CHECK_EQ(p.adm[0].after, static_cast<uint32_t>(KILN_MARKER_WORLD | KILN_MARKER_COMPASS));
    with_camera(&v, 3670, 820, 1475);  // moved 5 m closer: same admission, no event
    p.run(v, rows);
    CHECK_EQ(p.notes.size(), 0u);
    with_camera(&v, 3670, 820, 1470, 0, -1);  // turned around: World is behind, the compass still has it
    p.run(v, rows);
    CHECK_EQ(p.notes.size(), 1u);
    CHECK_EQ(p.adm[0].reason[kSurfWorld], static_cast<uint32_t>(KILN_MARKER_BEHIND));
    CHECK_EQ(p.adm[0].after, static_cast<uint32_t>(KILN_MARKER_COMPASS));
    with_camera(&v, 3670, 820, 1470);
    p.run(v, rows);
    CHECK_EQ(p.notes.size(), 1u);
    CHECK_EQ(p.adm[0].after, static_cast<uint32_t>(KILN_MARKER_WORLD | KILN_MARKER_COMPASS));
    // an unlimited map row needs no camera; a ranged one does
    std::vector<ClientRow> maps = {row("m", "a", KILN_MARKER_MAP), row("m", "b", KILN_MARKER_MAP)};
    maps[1].rec.max_distance = 10;
    Post q;
    q.run(view_all(), maps);
    CHECK_EQ(q.adm[0].after, static_cast<uint32_t>(KILN_MARKER_MAP));
    CHECK_EQ(q.adm[1].reason[kSurfMap], static_cast<uint32_t>(KILN_MARKER_NO_CAMERA));
    // a viewport of zero is no camera for the drawn surfaces
    View z = view_all();
    with_camera(&z, 3670, 820, 1470);
    z.viewport = false;
    Post r;
    r.run(z, rows);
    CHECK_EQ(r.adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_NO_CAMERA));
    kmk_test::end("M04 camera and range: NoCamera, OutOfRange, Ready, Behind (World only), each transition once, none for camera motion");
}

void m14_visibility_collapse() {
    kmk_test::begin();
    Post p;
    // a row set and removed before any Post was never evaluated: no visibility at all
    p.run(view_all(), {});
    CHECK_EQ(p.notes.size(), 0u);
    std::vector<ClientRow> rows = {row("m", "a", KILN_MARKER_MAP), row("m", "b", KILN_MARKER_MAP | KILN_MARKER_COMPASS),
                                   row("n", "a", KILN_MARKER_MAP, KILN_MARKER_RECEIVED, 9)};
    p.run(view_all(), rows);
    CHECK_EQ(p.notes.size(), 3u);
    // a pose-only change with the same admission: nothing
    rows[0].rec.x += 0.5;
    p.run(view_all(), rows);
    CHECK_EQ(p.notes.size(), 0u);
    // a clear of the evaluated rows: one terminal event each, after 0, requested surfaces Removed
    p.run(view_all(), {});
    CHECK_EQ(p.notes.size(), 3u);
    bool terminal = true;
    for (const Note& n : p.notes) {
        terminal &= n.kind == Note::Visibility && n.visibility.after == 0 && n.visibility.initial == 0;
        for (uint32_t s = 0; s < 3; ++s)
            terminal &= n.visibility.reason[s] == ((n.visibility.requested & kSurfaceBit[s]) ? uint32_t(KILN_MARKER_VIS_REMOVED) : uint32_t(KILN_MARKER_NOT_REQUESTED));
    }
    CHECK(terminal);
    CHECK_EQ(p.vis.size(), 0u);
    // world stop: terminal for every evaluated row, then nothing remembered
    p.run(view_all(), rows);
    std::vector<Note> stop;
    p.vis.clear(7, &stop);
    CHECK_EQ(stop.size(), 3u);
    CHECK_EQ(p.vis.size(), 0u);
    // a spec change that changes the requested mask is a visibility change too
    p.run(view_all(), rows);
    rows[0].rec.surfaces = KILN_MARKER_MAP | KILN_MARKER_COMPASS;
    p.run(view_all(), rows);
    CHECK_EQ(p.notes.size(), 1u);
    if (!p.notes.empty()) CHECK_EQ(p.notes[0].visibility.reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_NO_CAMERA));
    kmk_test::end("M14 visibility: none for a never-evaluated row, none for a pose-only change, one terminal per evaluated row on clear and world stop");
}

void reason_precedence() {
    kmk_test::begin();
    std::vector<ClientRow> rows = {row("m", "a", KILN_MARKER_COMPASS)};
    Candidate c = cand(rows[0]);
    std::vector<Admitted> adm;
    std::vector<uint32_t> order[3];
    View v;  // no world, capability off, no camera
    c.source_state = KILN_MARKER_ANCHOR_ENDED;
    admit(v, {c}, &adm, order);
    CHECK_EQ(adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_NO_WORLD));
    v.world_active = true;
    admit(v, {c}, &adm, order);
    CHECK_EQ(adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_CAPABILITY_OFF));
    v.cap[kSurfCompass] = true;
    admit(v, {c}, &adm, order);
    CHECK_EQ(adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_ANCHOR_ENDED));
    c.source_state = KILN_MARKER_SOURCE_STALE;
    admit(v, {c}, &adm, order);
    CHECK_EQ(adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_SOURCE_STALE));
    c.source_state = KILN_MARKER_READY;
    c.x = std::numeric_limits<double>::quiet_NaN();
    admit(v, {c}, &adm, order);
    CHECK_EQ(adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_BAD_POSITION));
    c.x = 3670;
    admit(v, {c}, &adm, order);
    CHECK_EQ(adm[0].reason[kSurfCompass], static_cast<uint32_t>(KILN_MARKER_NO_CAMERA));
    kmk_test::end("reason precedence: NoWorld, CapabilityOff, AnchorEnded, SourceStale, BadPosition, NoCamera");
}

void encoder_and_reuse() {
    kmk_test::begin();
    const MapLayout l = layout();
    std::vector<uint8_t> e(0xa0, 0xcd);
    encode_entry(e.data(), l, MapRow{3670.5, -12.25, 1500.0, 0xa13a918cu});
    int64_t x, y, z;
    uint32_t type;
    std::memcpy(&x, e.data() + 0x10, 8);
    std::memcpy(&y, e.data() + 0x18, 8);
    std::memcpy(&z, e.data() + 0x20, 8);
    std::memcpy(&type, e.data() + 0x30, 4);
    CHECK(x == static_cast<int64_t>(3670.5 * 4294967296.0) && y == -static_cast<int64_t>(12.25 * 4294967296.0) && z == (int64_t{1500} << 32));
    CHECK_EQ(type, 0xa13a918cu);
    CHECK_EQ(e[0x34], 2);
    bool zero = true;  // entity, second key, marker loca id, both text views and every optional field stay zero
    for (size_t i = 0; i < e.size(); ++i) {
        if ((i >= 0x10 && i < 0x28) || (i >= 0x30 && i < 0x35)) continue;
        zero &= e[i] == 0;
    }
    CHECK(zero);
    // the game did not clear its list between two of our steps: our rows are replaced, not appended again
    FakeUi f;
    f.set_header(5, 1024);
    MapRow r[2] = {{3670, 820, 1500, 1}, {3671, 820, 1500, 2}};
    Appended last;
    ListState st;
    CHECK_EQ(append_rows(f.ui(), f.l, r, 2, &last, &st), 2u);
    CHECK_EQ(f.count(), 7u);
    CHECK_EQ(append_rows(f.ui(), f.l, r, 1, &last, &st), 1u);
    CHECK_EQ(f.count(), 6u);
    CHECK_EQ(append_rows(f.ui(), f.l, r, 0, &last, &st), 0u);
    CHECK_EQ(f.count(), 5u);
    // the game cleared and refilled with other rows: we append after them
    CHECK_EQ(append_rows(f.ui(), f.l, r, 2, &last, &st), 2u);
    f.set_header(7, 1024);
    std::memset(f.entry(5), 0x11, 0xa0);  // the game's own row now where ours was
    CHECK_EQ(append_rows(f.ui(), f.l, r, 2, &last, &st), 2u);
    CHECK_EQ(f.count(), 9u);
    CHECK_EQ(f.entry(5)[0], 0x11);
    kmk_test::end("entry encoder (MapLabel.None: 32.32 position, type, visibility 2, every other byte zero) and a list the game did not clear");
}

void heading() {
    kmk_test::begin();
    double fwd[3];
    const float id[4] = {0, 0, 0, 1};
    CHECK(forward_of(id, fwd));
    CHECK(std::fabs(fwd[2] - 1) < 1e-9 && std::fabs(heading_of(fwd, 9)) < 1e-9);
    const float s = static_cast<float>(std::sqrt(0.5));
    const float east[4] = {0, s, 0, s};  // 90 degrees about +Y turns +Z to +X
    CHECK(forward_of(east, fwd));
    CHECK(std::fabs(heading_of(fwd, 0) - kPi / 2) < 1e-6);
    const float west[4] = {0, -s, 0, s};
    CHECK(forward_of(west, fwd));
    CHECK(std::fabs(heading_of(fwd, 0) + kPi / 2) < 1e-6);
    const double down[3] = {0, -1, 0};
    CHECK_EQ(heading_of(down, 1.25), 1.25);
    const float zero[4] = {0, 0, 0, 0};
    CHECK(!forward_of(zero, fwd));
    const float scaled[4] = {0, 2 * s, 0, 2 * s};  // normalized first
    CHECK(forward_of(scaled, fwd) && std::fabs(fwd[0] - 1) < 1e-6);
    kmk_test::end("camera heading: north 0, east pi/2, west -pi/2, the last heading kept looking straight down");
}

void compass_strip() {
    kmk_test::begin();
    // a point 50 m east of the camera (chapter 04 §6 M07): facing north it is right of centre, facing east centred,
    // facing west on the far side (the strip's edge, wrapped), facing south left
    const double cam[3] = {3670, 820, 1500};
    const double east = bearing_to(cam, 3720, 1500);
    CHECK(std::fabs(east - kPi / 2) < 1e-12);
    const Strip s = strip_of(1920, 1.0f);
    CHECK(s.cx == 960 && s.y == 54 && s.width == 640);
    const float north = strip_x(s, 0, east), facing = strip_x(s, kPi / 2, east), west = strip_x(s, -kPi / 2, east), south = strip_x(s, kPi, east);
    CHECK(std::fabs(north - (960 + 160)) < 1e-3);
    CHECK(std::fabs(facing - 960) < 1e-3);
    CHECK(std::fabs(west - (960 - 320)) < 1e-3);  // exactly opposite: -pi, the left edge of [-pi, pi)
    CHECK(std::fabs(south - (960 - 160)) < 1e-3);
    // a narrow window: 0.9 of its width; the scale moves the strip down and widens it up to that
    const Strip narrow = strip_of(500, 2.0f);
    CHECK(narrow.width == 450 && narrow.y == 108 && narrow.cx == 250);
    const Strip bad = strip_of(1920, 0.0f);
    CHECK(bad.scale == 1.0f);
    // wrap stays in [-pi, pi)
    CHECK(std::fabs(wrap_pi(3 * kPi) + kPi) < 1e-9 && std::fabs(wrap_pi(-3 * kPi) + kPi) < 1e-9 && wrap_pi(0.5) == 0.5);
    CHECK(wrap_pi(std::numeric_limits<double>::quiet_NaN()) == 0);
    // smoothing: linear over 100 ms, clamped, no extrapolation
    const double a[3] = {0, 0, 0}, b[3] = {10, 0, -10};
    double p[3];
    interpolate(a, b, 50, p);
    CHECK(p[0] == 5 && p[2] == -5);
    interpolate(a, b, 250, p);
    CHECK(p[0] == 10 && p[2] == -10);
    interpolate(a, b, -5, p);
    CHECK(p[0] == 0);
    kmk_test::end("M07 compass maths: east of a north-facing camera right of centre, centred facing east, opposite facing west; strip size; 100 ms smoothing");
}

// ---- issue 159: the list in a frame -------------------------------------------------------------------------------------
// The game's order on a fake list (docs/re/map.md, "The list in a frame"). Each client SimStep: the clear (count 0) and the
// refill with the game's rows, then Kiln's Post at the steps that advance the clock, then the end of the step; the UI
// frame reads the list after it. A paused frame runs no SimStep.
struct Frames {
    FakeUi f;
    uint64_t game = 833;  // the game's rows this step (the 26430625 client's count in a hosted world, session 1)
    uint64_t epoch = 1;
    Appended last;
    MapKeep keep;
    std::vector<MapRow> rows = {{3815.4, 850.2, 1383.0, 0x8c1a2b3cu}, {3700.0, 820.0, 1500.0, 0x11223344u}};
    std::vector<MapRow> post_rows;  // what the next Post admits (surfaces.cpp: the MAP surface's order)
    ListState st = ListState::Ok;
    bool post_alive = true;  // the core removes a tick subscriber that faults: no Post of ours from then on

    Frames() { post_rows = rows; }
    void programs() {  // clear_map_markers_for_ui, then replicate_map_markers_for_ui; their jobs joined
        f.set_header(0, 1024);
        for (uint64_t i = 0; i < game; ++i) std::memset(f.entry(i), 0x60 + static_cast<int>(i & 0x0f), 0xa0);
        f.set_header(game, 1024);
    }
    void post() {  // surfaces_client_post: the keep goes, post_map appends and keeps what it wrote
        keep.begin_post();
        if (post_rows.empty() && !last.n) return;
        const uint32_t n = static_cast<uint32_t>(post_rows.size());
        append_rows(f.ui(), f.l, post_rows.data(), n, &last, &st);
        if (st == ListState::Ok) keep.set(epoch, post_rows.data(), n);
    }
    uint32_t step(bool ticked) {  // one client SimStep and the link after it
        programs();
        if (ticked && post_alive) post();
        if (!keep.end_step(ticked)) return 0;  // surfaces.cpp step_end
        return rewrite_kept(f.ui(), f.l, keep, epoch, &last, &st);
    }
    // the UI frame's read: the game's rows untouched and exactly `want` of ours after them, in the Post's order
    bool read(const std::vector<MapRow>& want) {
        if (f.count() != game + want.size()) return false;
        for (uint64_t i = 0; i < game; ++i)
            if (f.entry(i)[0] != 0x60 + static_cast<int>(i & 0x0f) || f.entry(i)[0x9f] != f.entry(i)[0]) return false;
        std::vector<uint8_t> e(0xa0);
        for (size_t k = 0; k < want.size(); ++k) {
            encode_entry(e.data(), f.l, want[k]);
            if (std::memcmp(f.entry(game + k), e.data(), e.size()) != 0) return false;
        }
        return true;
    }
};

void every_frame_reads_kilns_rows() {
    kmk_test::begin();
    // 144 frames in one second against the 60 Hz client clock: a frame's SimStep is ticked when the clock advanced since
    // the frame before (the client world update, C 0xa1fae0: ticked = step counter != previous)
    Frames w;
    int missing = 0, ticked_frames = 0, first_missing = -1;
    uint64_t prev = 0;
    for (int frame = 0; frame < 144; ++frame) {
        const uint64_t clock = static_cast<uint64_t>(frame) * 60 / 144 + 1;
        const bool ticked = clock != prev;
        prev = clock;
        ticked_frames += ticked;
        w.step(ticked);
        CHECK(w.st == ListState::Ok);
        if (!w.read(w.rows)) {
            ++missing;
            if (first_missing < 0) first_missing = frame;
        }
    }
    CHECK_EQ(ticked_frames, 60);
    CHECK_EQ(missing, 0);
    if (missing) std::printf("  %d of 144 frames read the list without Kiln's rows (first: frame %d)\n", missing, first_missing);
    // a ticked step: the link writes the Post's rows over in place (same count, same bytes)
    w.step(true);
    CHECK(w.read(w.rows));
    CHECK_EQ(w.last.base, w.game);
    CHECK_EQ(w.last.n, 2u);
    kmk_test::end("issue 159: at 144 fps on the 60 Hz client clock every UI frame reads the game's rows and Kiln's two after them");
}

void kept_rows_follow_the_post() {
    kmk_test::begin();
    Frames w;
    w.step(true);
    CHECK(w.read(w.rows));
    // a paused frame (no SimStep: the client world update skips it while the world is paused): the list as the step left it
    CHECK(w.read(w.rows));
    // a moved marker: the Post's new position, kept for the steps after it
    w.post_rows[0].x += 25;
    w.step(true);
    CHECK(w.read(w.post_rows));
    w.step(false);
    w.step(false);
    CHECK(w.read(w.post_rows));
    // the game's rows grow until one of ours has no room: only the room is used, the game's rows stay whole
    w.game = 1023;
    CHECK_EQ(w.step(false), 1u);
    CHECK(w.read({w.post_rows[0]}));
    w.game = 1024;
    CHECK_EQ(w.step(false), 0u);
    CHECK(w.st == ListState::Ok && w.read({}));
    w.game = 833;
    CHECK_EQ(w.step(false), 2u);
    CHECK(w.read(w.post_rows));
    // the marker goes: the Post writes nothing and keeps nothing, the steps after it write nothing
    w.post_rows.clear();
    w.step(true);
    CHECK(w.read({}));
    CHECK_EQ(w.step(false), 0u);
    CHECK(w.read({}));
    CHECK(w.keep.rows.empty() && w.keep.epoch == 0);
    kmk_test::end("issue 159: kept rows follow the last Post (moved, cut to the room the game leaves, gone), a paused frame keeps them");
}

void kept_rows_stay_in_their_world() {
    kmk_test::begin();
    Frames w;
    w.step(true);
    CHECK(w.read(w.rows));
    // the next client world (a new epoch) before its first Post: nothing of the last world's goes into its list
    w.epoch = 2;
    CHECK_EQ(w.step(false), 0u);
    CHECK(w.st == ListState::Ok && w.read({}));
    w.step(true);
    CHECK(w.read(w.rows));
    // a header that fails its guards: nothing is written and the state says so (surfaces.cpp latches the map off)
    w.programs();
    w.f.set_header(w.game, 1024, 8);
    std::vector<uint8_t> before(w.f.mem);
    CHECK_EQ(rewrite_kept(w.f.ui(), w.f.l, w.keep, w.epoch, &w.last, &w.st), 0u);
    CHECK(w.st == ListState::BadHeader);
    CHECK(before == w.f.mem);
    // nothing kept: no write, state Ok
    MapKeep none;
    w.programs();
    CHECK_EQ(rewrite_kept(w.f.ui(), w.f.l, none, w.epoch, &w.last, &w.st), 0u);
    CHECK(w.st == ListState::Ok && w.read({}));
    kmk_test::end("issue 159: kept rows go only into their own world's list, never through a header that fails its guards");
}

void kept_rows_end_with_the_post() {
    kmk_test::begin();
    Frames w;
    w.step(true);
    w.step(false);
    CHECK(w.read(w.rows));
    // the client Post faults and the core removes it (tick.cpp run_subs): from then on nothing moves or removes the rows.
    // The frames up to the next ticked step still show the last Post's rows (as the drawn surfaces show its snapshot)...
    w.post_alive = false;
    w.step(false);
    CHECK(w.read(w.rows));
    // ...and the first ticked step without our Post drops them: they never outlive the Post that would remove them
    CHECK_EQ(w.step(true), 0u);
    CHECK(w.st == ListState::Ok && w.read({}));
    CHECK_EQ(w.step(false), 0u);
    CHECK(w.read({}));
    CHECK(w.keep.rows.empty() && w.keep.epoch == 0);
    // a Post again (the next world's subscriber, say): kept and written again as before
    w.post_alive = true;
    w.step(true);
    w.step(false);
    CHECK(w.read(w.rows));
    kmk_test::end("issue 159: a ticked step without Kiln's Post (its subscriber faulted and was removed) drops the kept rows");
}

// ---- issue 160: the tooltip text ----------------------------------------------------------------------------------------
// MapLabel.NativeTooltip writes the record's text into the text view of Kiln's own entries (+0x50 pointer, +0x58 length,
// +0x60 hash), which the game's name resolver (client 0xd3d350) takes before anything else; the game's append (0x302970)
// clears both views of every entry it adds, and replicate fills the view only from an entity's CustomPlayerString
// (docs/re/map.md, "The tooltip's name").

MapLayout text_layout() {
    MapLayout l = layout();
    l.label = 0x50;
    l.label_hash = 0x60;
    return l;
}

// The game's resolver, its first step: the entry's text view when its length is not 0.
std::string game_name(const uint8_t* e) {
    uint64_t p = 0, n = 0;
    std::memcpy(&p, e + 0x50, 8);
    std::memcpy(&n, e + 0x58, 8);
    return n ? std::string(reinterpret_cast<const char*>(static_cast<uintptr_t>(p)), static_cast<size_t>(n)) : std::string();
}

uint64_t u64_at(const uint8_t* e, size_t off) {
    uint64_t v = 0;
    std::memcpy(&v, e + off, 8);
    return v;
}

void text_hash_is_the_games() {
    kmk_test::begin();
    // 64-bit FNV-1a over each byte sign-extended (client 0x316830, 0x308970, 0x11b0fb0)
    CHECK(label_hash("", 0) == 0xcbf29ce484222325ull);
    CHECK(label_hash("\xc3\xa9", 2) == 0x080d7307b4cc3001ull);  // docs/re/map.md (VERIFY): not the unsigned FNV
    CHECK(label_hash("Golden chest 1", 14) == 0xdf44b8d7a13f30deull);
    const std::string cjk = "\xe9\x87\x91\xe3\x81\xae\xe5\xae\x9d\xe7\xae\xb1 3";  // 金の宝箱 3
    CHECK(label_hash(cjk.data(), cjk.size()) == 0x1e75fda44285406dull);
    const std::string hangul = "\xed\x99\xa9\xea\xb8\x88 \xec\x83\x81\xec\x9e\x90";  // 황금 상자
    CHECK(label_hash(hangul.data(), hangul.size()) == 0x1ed15a4302fb202aull);
    kmk_test::end("issue 160: the text hash is the game's (64-bit FNV-1a of sign-extended bytes; UTF-8 and CJK vectors)");
}

void tooltip_text_in_the_entry() {
    kmk_test::begin();
    const MapLayout l = text_layout();
    CHECK(l.valid() && l.has_label());
    CHECK(!layout().has_label() && layout().valid());
    const std::string text = "Golden chest 1";
    std::vector<uint8_t> plain(0xa0, 0xcd), tip(0xa0, 0xcd);
    encode_entry(plain.data(), layout(), MapRow{3670.5, -12.25, 1500.0, 0x3a761350u});
    // NativeTooltip with the text on: the view and the game's hash, every other byte as MapLabel.None writes it
    const MapRow r = map_row(3670.5, -12.25, 1500.0, 0x3a761350u, KILN_MARKER_LABEL_NATIVE_TOOLTIP, text, true);
    CHECK(r.text == text.data() && r.text_len == text.size() && r.text_hash == label_hash(text.data(), text.size()));
    encode_entry(tip.data(), l, r);
    CHECK_EQ(u64_at(tip.data(), 0x50), static_cast<uint64_t>(reinterpret_cast<uintptr_t>(text.data())));
    CHECK_EQ(u64_at(tip.data(), 0x58), text.size());
    CHECK(u64_at(tip.data(), 0x60) == label_hash(text.data(), text.size()));
    CHECK(game_name(tip.data()) == text);
    bool rest = true;
    for (size_t i = 0; i < tip.size(); ++i)
        if (i < 0x50 || i >= 0x68) rest &= tip[i] == plain[i];
    CHECK(rest);
    CHECK(std::memcmp(plain.data() + 0x50, std::vector<uint8_t>(0x18, 0).data(), 0x18) == 0);
    // MapLabel.None, NativeTooltip with the text off (markers.map_text off), an empty text: no text, byte for byte the
    // entry of before
    const MapRow none[] = {map_row(3670.5, -12.25, 1500.0, 0x3a761350u, KILN_MARKER_LABEL_NONE, text, true),
                           map_row(3670.5, -12.25, 1500.0, 0x3a761350u, KILN_MARKER_LABEL_NATIVE_TOOLTIP, text, false),
                           map_row(3670.5, -12.25, 1500.0, 0x3a761350u, KILN_MARKER_LABEL_NATIVE_TOOLTIP, std::string_view(), true)};
    for (const MapRow& n : none) {
        CHECK(n.text == nullptr && n.text_len == 0 && n.text_hash == 0);
        std::vector<uint8_t> e(0xa0, 0xcd);
        encode_entry(e.data(), l, n);
        CHECK(e == plain);
    }
    // a layout without the text fields (markers.map_tooltip_text unresolved) writes none, whatever the row says
    std::vector<uint8_t> e(0xa0, 0xcd);
    encode_entry(e.data(), layout(), r);
    CHECK(e == plain);
    // a text layout that would overlap the position, type or visibility, or leave the entry, is not a text layout
    MapLayout bad = text_layout();
    bad.label = 0x18;
    CHECK(!bad.has_label() && !bad.valid());
    bad = text_layout();
    bad.label_hash = 0x9c;
    CHECK(!bad.has_label() && !bad.valid());
    bad = text_layout();
    bad.label_hash = 0x58;
    CHECK(!bad.has_label());
    kmk_test::end("issue 160: NativeTooltip fills the entry's text view (pointer, length, the game's hash); None, the text off or an empty text leave the entry as before");
}

// The game's order with text (Frames' clear and refill, the game's append clearing both views): every seventh game row
// is an entity with a CustomPlayerString name, a view into the game's own string storage.
struct TextFrames {
    FakeUi f;
    uint64_t game = 40;
    uint64_t epoch = 1;
    Appended last;
    MapKeep keep;
    ListState st = ListState::Ok;
    std::string player_name = "Bob's altar";  // the game's string storage of one CustomPlayerString
    std::vector<std::string> texts = {"Golden chest 1", "Golden chest 2", "Golden chest 3"};  // kiln.markers' arena
    std::vector<MapRow> post_rows;

    TextFrames() {
        f.l = text_layout();
        for (size_t k = 0; k < texts.size(); ++k)
            post_rows.push_back(map_row(3700.0 + static_cast<double>(k), 820, 1500, 0x3a761350u, KILN_MARKER_LABEL_NATIVE_TOOLTIP, texts[k], true));
    }
    void programs() {  // clear, then the game's append (0x302970: both views cleared) and replicate's fields
        f.set_header(0, 1024);
        for (uint64_t i = 0; i < game; ++i) {
            uint8_t* e = f.entry(i);
            std::memset(e, 0x60 + static_cast<int>(i & 0x0f), 0x50);
            std::memset(e + 0x50, 0, 0x30);
            std::memset(e + 0x80, 0x60 + static_cast<int>(i & 0x0f), 0x20);
            if (i % 7 == 0) {  // replicate: the entity's CustomPlayerString
                const uint64_t p = reinterpret_cast<uintptr_t>(player_name.data()), n = player_name.size(),
                               h = label_hash(player_name.data(), player_name.size());
                std::memcpy(e + 0x50, &p, 8);
                std::memcpy(e + 0x58, &n, 8);
                std::memcpy(e + 0x60, &h, 8);
            }
        }
        f.set_header(game, 1024);
    }
    void post() {
        keep.begin_post();
        if (post_rows.empty() && !last.n) return;
        const uint32_t n = static_cast<uint32_t>(post_rows.size());
        append_rows(f.ui(), f.l, post_rows.data(), n, &last, &st);
        if (st == ListState::Ok) keep.set(epoch, post_rows.data(), n);
    }
    void step(bool ticked) {
        programs();
        if (ticked) post();
        if (keep.end_step(ticked)) rewrite_kept(f.ui(), f.l, keep, epoch, &last, &st);
    }
    // what the hover can name this frame: every slot below the count (docs/re/map.md: the hover is cleared and hit-tested
    // again over the current list every frame), the game's rows by their own view, Kiln's by its text, in the Post's order
    bool names(const std::vector<std::string>& want) {
        if (f.count() != game + want.size()) return false;
        for (uint64_t i = 0; i < game; ++i)
            if (game_name(f.entry(i)) != (i % 7 == 0 ? player_name : std::string())) return false;
        for (size_t k = 0; k < want.size(); ++k)
            if (game_name(f.entry(game + k)) != want[k]) return false;
        return true;
    }
};

void kiln_rows_and_game_rows_keep_their_own_names() {
    kmk_test::begin();
    TextFrames w;
    w.step(true);
    CHECK(w.st == ListState::Ok);
    CHECK(w.names(w.texts));
    // frames without a new step: the game refills, the kept rows bring their text back after the game's
    for (int i = 0; i < 5; ++i) w.step(false);
    CHECK(w.names(w.texts));
    // the game's list grows by a row: its new row takes the slot Kiln's first row had and none of its text (the append
    // clears the view); Kiln's rows come after it with their own
    ++w.game;
    w.step(false);
    CHECK(w.names(w.texts));
    CHECK(game_name(w.f.entry(w.game - 1)).empty());
    // a moved marker keeps its own text at its new position
    w.post_rows[1].x += 40;
    w.step(true);
    CHECK(w.names(w.texts));
    int64_t x = 0;
    std::memcpy(&x, w.f.entry(w.game + 1) + 0x10, 8);
    CHECK(x == static_cast<int64_t>(std::llround(3741.0 * 4294967296.0)));
    // a changed text: the next Post writes the new view, no step after it shows the old one
    w.texts[2] = "Golden chest 3 (looted)";
    w.post_rows[2] = map_row(3702, 820, 1500, 0x3a761350u, KILN_MARKER_LABEL_NATIVE_TOOLTIP, w.texts[2], true);
    w.step(true);
    CHECK(w.names(w.texts));
    w.step(false);
    CHECK(w.names(w.texts));
    kmk_test::end("issue 160: each frame names the game's rows by their own views and Kiln's by their texts (refills, a grown list, a move, a new text)");
}

void removed_marker_shows_no_stale_text() {
    kmk_test::begin();
    TextFrames w;
    w.step(true);
    CHECK(w.names(w.texts));
    // the second marker goes: the third moves up into its slot with its own text, nothing below the count says "2"
    w.post_rows.erase(w.post_rows.begin() + 1);
    w.step(true);
    CHECK(w.names({w.texts[0], w.texts[2]}));
    for (int i = 0; i < 3; ++i) w.step(false);
    CHECK(w.names({w.texts[0], w.texts[2]}));
    for (uint64_t i = 0; i < w.f.count(); ++i) CHECK(game_name(w.f.entry(i)) != w.texts[1]);
    // NativeTooltip set back to None: the same marker, its slot without a text view from the next Post on
    w.post_rows[1] = map_row(3702, 820, 1500, 0x3a761350u, KILN_MARKER_LABEL_NONE, w.texts[2], true);
    w.step(true);
    CHECK(w.names({w.texts[0], std::string()}));
    // every marker gone: the game's rows only, with their own names
    w.post_rows.clear();
    w.step(true);
    CHECK(w.names({}));
    w.step(false);
    CHECK(w.names({}));
    kmk_test::end("issue 160: a removed marker's text is in no slot below the count; a slot reused by another marker carries that marker's text; None clears it");
}

void long_and_cjk_text() {
    kmk_test::begin();
    TextFrames w;
    std::string longest;
    for (int i = 0; i < 42; ++i) longest += "\xe5\xae\x9d";  // 宝 x 42
    longest += "ab";                                         // 128 bytes: the most a marker's text may have
    CHECK_EQ(longest.size(), 128u);
    w.texts = {longest, "\xe9\x87\x91\xe3\x81\xae\xe5\xae\x9d\xe7\xae\xb1 3", "\xed\x99\xa9\xea\xb8\x88 \xec\x83\x81\xec\x9e\x90"};
    w.post_rows.clear();
    for (size_t k = 0; k < w.texts.size(); ++k)
        w.post_rows.push_back(map_row(3700.0 + static_cast<double>(k), 820, 1500, 0x5eba3084u, KILN_MARKER_LABEL_NATIVE_TOOLTIP, w.texts[k], true));
    w.step(true);
    w.step(false);
    CHECK(w.names(w.texts));
    CHECK_EQ(u64_at(w.f.entry(w.game), 0x58), 128u);
    CHECK(u64_at(w.f.entry(w.game), 0x60) == 0x9ae50fe336aa030aull);
    CHECK(u64_at(w.f.entry(w.game + 1), 0x60) == 0x1e75fda44285406dull);
    // the view is the record's own bytes (no copy, no terminator): the game's player-text path copies them when it names
    // the entry
    CHECK_EQ(u64_at(w.f.entry(w.game + 2), 0x50), static_cast<uint64_t>(reinterpret_cast<uintptr_t>(w.texts[2].data())));
    kmk_test::end("issue 160: a 128-byte text and Chinese, Japanese and Korean text go into the view whole, with the game's hash");
}

void map_text_row_follows_the_map_and_its_guards() {
    kmk_test::begin();
    const char* why = "the game's map tooltip cannot be found on this build (markers.map_tooltip_text)";
    TextRow t = map_text_row(true, true, KILN_CAP_REASON_SYMBOL_MISSING, why);
    CHECK(t.state == KILN_CAP_AVAILABLE && t.reason == KILN_CAP_REASON_NONE && std::string(t.text).empty());
    // the feature unresolved: off with its words, whether the map is on or not (Set's NativeTooltip refusal applies)
    t = map_text_row(true, false, KILN_CAP_REASON_SYMBOL_MISSING, why);
    CHECK(t.state == KILN_CAP_UNAVAILABLE && t.reason == KILN_CAP_REASON_SYMBOL_MISSING && std::string(t.text) == why);
    t = map_text_row(false, false, KILN_CAP_REASON_FAULTED, why);
    CHECK(t.state == KILN_CAP_UNAVAILABLE && t.reason == KILN_CAP_REASON_FAULTED && std::string(t.text) == why);
    // the map off (its guards, a latch or the catalog): off too, naming markers.map
    t = map_text_row(false, true, KILN_CAP_REASON_SYMBOL_MISSING, why);
    CHECK(t.state == KILN_CAP_UNAVAILABLE && t.reason == KILN_CAP_REASON_SERVICE_MISSING && std::strstr(t.text, "markers.map") != nullptr);
    kmk_test::end("issue 160: markers.map_text is on exactly when markers.map is on and markers.map_tooltip_text resolved");
}

}  // namespace

int main() {
    order_is_exact();
    m03_map_capacity();
    m04_camera_range();
    m14_visibility_collapse();
    reason_precedence();
    encoder_and_reuse();
    heading();
    compass_strip();
    every_frame_reads_kilns_rows();
    kept_rows_follow_the_post();
    kept_rows_stay_in_their_world();
    kept_rows_end_with_the_post();
    text_hash_is_the_games();
    tooltip_text_in_the_entry();
    kiln_rows_and_game_rows_keep_their_own_names();
    removed_marker_shows_no_stale_text();
    long_and_cjk_text();
    map_text_row_follows_the_map_and_its_guards();
    return kmk_test::finish("surfaces_test");
}
