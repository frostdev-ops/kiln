// SPDX-License-Identifier: MIT
// kiln.vfx: the queue and the guard's logic on a host compiler, against the simulated game. tests/run.sh builds and runs it.
//   test_core [guids [resources]]   local data, never in the repo: real VFX GUIDs, one per line in .NET text form (the map rule over
//   them), and real VfxResource bytes with their expected numbers (test_real_resources)
#include <array>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <random>
#include <string>
#include <thread>
#include <vector>

#include "../game.h"

namespace kvx {
void vlog(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (std::getenv("VFX_TEST_LOG")) std::printf("  log[%d] %s\n", level, buf);
}
uint32_t thread_id() { return 7; }
uint64_t now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
}  // namespace kvx

using namespace kvx;

static int g_pass = 0, g_fail = 0;
static void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    (ok ? g_pass : g_fail)++;
}

static KilnVfxPlay at(const uint8_t g[16], int64_t life = 0) {
    KilnVfxPlay p{};
    p.size = sizeof p;
    p.flags = KILN_VFX_AT;
    std::memcpy(p.vfx, g, 16);
    p.position = KilnVec3{10, 20, 30};
    p.lifetime_ns = life;
    return p;
}

static KilnVfxInfo info(KilnVfx h) {
    KilnVfxInfo i{};
    i.size = sizeof i;
    core_info(h, &i);
    return i;
}

static KilnVfxStatus status() {
    KilnVfxStatus s{};
    s.size = sizeof s;
    core_status(&s);
    return s;
}

static void ticks(int n, int sleep_ms = 0) {
    for (int i = 0; i < n; ++i) {
        sim_update();
        if (sleep_ms) std::this_thread::sleep_for(std::chrono::milliseconds(sleep_ms));
    }
}

static bool parse_guid(const std::string& s, uint8_t out[16]) {  // .NET text -> game byte order
    unsigned v[16];
    if (std::sscanf(s.c_str(), "%2x%2x%2x%2x-%2x%2x-%2x%2x-%2x%2x-%2x%2x%2x%2x%2x%2x", &v[3], &v[2], &v[1], &v[0], &v[5], &v[4], &v[7], &v[6], &v[8],
                    &v[9], &v[10], &v[11], &v[12], &v[13], &v[14], &v[15]) != 16)
        return false;
    for (int i = 0; i < 16; ++i) out[i] = static_cast<uint8_t>(v[i]);
    return true;
}

static void test_map_rules() {
    MapView empty;
    uint8_t g[16] = {1};
    check(map_find(empty, g) == -1, "map: no system yet reads as not ready");
    check(!map_valid(empty), "map: an empty view is not valid");

    // a small map with a collision chain
    std::vector<uint64_t> bits(1, 0), keys(16 * 2, 0);
    auto put = [&](const uint8_t* k) {
        uint64_t k0, k1;
        std::memcpy(&k0, k, 8);
        std::memcpy(&k1, k + 8, 8);
        uint64_t i = k0 & 0xffffffffu & 15;
        while ((bits[0] >> i) & 1) i = i + 1 < 16 ? i + 1 : 0;
        bits[0] |= 1ull << i;
        keys[i * 2] = k0;
        keys[i * 2 + 1] = k1;
    };
    uint8_t a[16] = {0x0f}, b[16] = {0x1f}, c[16] = {0x2f}, d[16] = {0x3f};  // all hash to slot 15, wrap to 0, 1
    put(a), put(b), put(c);
    MapView v;
    v.count = 3;
    v.capacity = 16;
    v.bits = bits.data();
    v.keys = keys.data();
    check(map_valid(v), "map: bit count equals the count field");
    check(map_find(v, a) == 1 && map_find(v, b) == 1 && map_find(v, c) == 1, "map: finds keys through a chain that wraps");
    check(map_find(v, d) == 0, "map: a key that hashes into the chain but is not there: unknown");
    uint8_t zero[16] = {};
    check(map_find(v, zero) == 0, "map: the empty GUID is unknown");
    MapView wrong = v;
    wrong.count = 4;
    check(!map_valid(wrong), "map: a count that disagrees with the bits is not valid");
    wrong = v;
    wrong.capacity = 17;
    check(!map_valid(wrong), "map: a capacity that is no power of two is not valid");
    // a full map must end
    std::vector<uint64_t> fb(1, 0xffffull), fk(32, 0);
    MapView full;
    full.count = 16;
    full.capacity = 16;
    full.bits = fb.data();
    full.keys = fk.data();
    check(map_find(full, d) == 0, "map: a full map without the key ends with unknown");
}

// Real VfxResource bytes against numbers from the same data read through tools/kfc: "<file> <kill> <duration> <lifetime>" per line.
static void test_real_resources(const char* path) {
    std::ifstream f(path);
    std::string file;
    uint32_t kill;
    float duration, lifetime;
    size_t n = 0, good = 0;
    while (f >> file >> kill >> duration >> lifetime) {
        std::ifstream b(file, std::ios::binary);
        std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(b)), std::istreambuf_iterator<char>());
        if (bytes.size() < 216) continue;
        VfxLengths v;
        read_lengths(bytes.data(), ResLayout{}, &v);
        ++n;
        if (v.kill_seconds == kill && v.duration == duration && v.lifetime == lifetime) ++good;
        else std::printf("  %s: read %u %g %g, want %u %g %g\n", file.c_str(), v.kill_seconds, v.duration, v.lifetime, kill, duration, lifetime);
    }
    std::printf("INFO %zu real VfxResources from %s\n", n, path);
    check(n > 0 && good == n, "real resources: kill time, duration and lifetime read as tools/kfc reads them");
}

static void test_real_guids(const char* path) {
    std::ifstream f(path);
    std::vector<std::array<uint8_t, 16>> all;
    std::string line;
    while (std::getline(f, line)) {
        std::array<uint8_t, 16> g;
        if (parse_guid(line, g.data())) all.push_back(g);
    }
    std::printf("INFO %zu real VFX GUIDs from %s\n", all.size(), path);
    const uint64_t cap = 4096;  // VFX capacity 2,048 at the game's load factor
    std::vector<uint64_t> bits(cap / 64, 0), keys(cap * 2, 0);
    for (auto& g : all) {
        uint64_t k0, k1;
        std::memcpy(&k0, g.data(), 8);
        std::memcpy(&k1, g.data() + 8, 8);
        uint64_t i = k0 & 0xffffffffu & (cap - 1);
        while ((bits[i >> 6] >> (i & 63)) & 1) i = i + 1 < cap ? i + 1 : 0;
        bits[i >> 6] |= 1ull << (i & 63);
        keys[i * 2] = k0;
        keys[i * 2 + 1] = k1;
    }
    MapView v;
    v.count = all.size();
    v.capacity = cap;
    v.bits = bits.data();
    v.keys = keys.data();
    check(map_valid(v), "real set: the map validates");
    size_t found = 0;
    for (auto& g : all) found += map_find(v, g.data()) == 1;
    check(found == all.size(), "real set: every registered GUID passes the guard");
    std::mt19937_64 rng(1234);
    size_t refused = 0;
    const size_t tries = 100000;
    for (size_t t = 0; t < tries; ++t) {
        uint8_t g[16];
        uint64_t a = rng(), b = rng();
        std::memcpy(g, &a, 8);
        std::memcpy(g + 8, &b, 8);
        refused += map_find(v, g) == 0;
    }
    check(refused == tries, "real set: 100,000 random GUIDs are all refused");
    // flip one bit of each real GUID: same slot neighbourhood, different key
    size_t near = 0;
    for (auto& g : all) {
        auto x = g;
        x[15] ^= 1;
        near += map_find(v, x.data()) == 0;
    }
    check(near == all.size(), "real set: every real GUID with one bit flipped is refused");
}

int main(int argc, char** argv) {
    static_assert(sizeof(KilnVfxPlay) == 128 && sizeof(KilnVfxInfo) == 88 && sizeof(KilnVfxStatus) == 64);
    test_map_rules();
    if (argc > 1) test_real_guids(argv[1]);
    if (argc > 2) test_real_resources(argv[2]);

    uint8_t known[16], known2[16], zero[16] = {}, unknown[16] = {0xde, 0xad, 0xbe, 0xef, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12};
    sim_known_guid(0, known);
    sim_known_guid(1, known2);

    // ---- no game yet ----
    auto p0 = at(known);
    check(core_play(1, &p0) == 0, "no game: play returns none");
    check(core_is_registered(known) == -1, "no game: is_registered says not ready");
    KilnVfxStatus s0 = status();
    check(s0.flags == 0 && s0.updates == 0, "no game: status is all off");

    core_set_game(sim_game());
    KilnVfxStatus s1 = status();
    check((s1.flags & KILN_VFX_SIM) && (s1.flags & KILN_VFX_HAS_GUARD) && (s1.flags & KILN_VFX_HAS_PLAY) && s1.registered == kSimKnown, "sim: status");
    check(core_is_registered(known) == 1 && core_is_registered(unknown) == 0, "is_registered: known and unknown");

    // ---- how long a one-shot runs ----
    VfxLengths len;
    len.kill_seconds = 10;
    check(one_shot_ns(len) == 10'000'000'000, "one-shot: the kill time of the resource");
    len.kill_seconds = 0;
    check(one_shot_ns(len) == kOneShotFallbackNs, "one-shot: a resource that says nothing: the fallback");
    len.duration = 2.f;
    len.lifetime = 1.5f;
    check(one_shot_ns(len) == 3'500'000'000, "one-shot: duration + lifetime when there is no kill time");
    len.kill_seconds = 2;
    check(one_shot_ns(len) == 3'500'000'000, "one-shot: duration + lifetime when longer than the kill time");
    len.kill_seconds = 30;
    check(one_shot_ns(len) == 30'000'000'000, "one-shot: the kill time when longer");
    len = VfxLengths{};
    len.duration = 0.1f;
    check(one_shot_ns(len) == 1'000'000'000, "one-shot: at least 1 s");
    len.kill_seconds = 3600;
    check(one_shot_ns(len) == 60'000'000'000, "one-shot: at most 60 s");

    auto p_len = at(known);
    const KilnVfx h_len = core_play(1, &p_len);
    check(info(h_len).lifetime_ns == 0, "one-shot: not settled before the start");
    ticks(1);
    check(info(h_len).lifetime_ns == 10'000'000'000, "one-shot: the game's resource gives 10 s");
    core_stop(h_len, 1);
    auto p_len2 = at(known2);
    const KilnVfx h_len2 = core_play(1, &p_len2);
    ticks(1);
    check(info(h_len2).lifetime_ns == 3'500'000'000, "one-shot: read from the resource's parameters: 3.5 s");
    core_stop(h_len2, 1);
    auto p_len3 = at(known, 123'000'000);
    const KilnVfx h_len3 = core_play(1, &p_len3);
    ticks(1);
    check(info(h_len3).lifetime_ns == 123'000'000, "a lifetime the caller gave stands");
    core_stop(h_len3, 1);
    auto p_len4 = at(known, -1);
    const KilnVfx h_len4 = core_play(1, &p_len4);
    ticks(1);
    check(info(h_len4).lifetime_ns < 0, "until stop stands");
    core_stop(h_len4, 1);
    ticks(1);

    // ---- a GUID that comes later ----
    KilnVfxPlay wait = at(zero);
    wait.flags |= KILN_VFX_WAIT_GUID;
    const KilnVfx h_w = core_play(1, &wait);
    check(h_w != 0, "a play waiting for its GUID gets a handle");
    ticks(2);
    check(core_state(h_w) == KILN_VFX_PENDING && info(h_w).fx_handle == 0, "it waits, and the game isn't called");
    check(core_bind_guid(h_w, known) == KILN_OK, "bind_guid");
    ticks(1);
    check(core_state(h_w) == KILN_VFX_PLAYING, "the update after the bind starts it");
    core_stop(h_w, 1);
    const KilnVfx h_w2 = core_play(1, &wait);
    core_bind_guid(h_w2, zero);
    check(core_state(h_w2) == KILN_VFX_FAILED && info(h_w2).reason == KILN_VFX_END_UNKNOWN_VFX, "bound to nothing: unknown");
    const KilnVfx h_w3 = core_play(1, &wait);
    core_bind_guid(h_w3, unknown);
    ticks(1);
    check(info(h_w3).reason == KILN_VFX_END_UNKNOWN_VFX, "bound to a GUID the game lacks: Kiln's check still applies");
    const KilnVfx h_w4 = core_play(1, &wait);
    core_stop(h_w4, 0);
    ticks(1);
    check(info(h_w4).reason == KILN_VFX_END_STOPPED, "a stop while it waits");
    check(core_bind_guid(999999, known) == KILN_ERR_NOT_FOUND, "bind_guid of an unknown handle");
    ticks(1);

    // ---- bad arguments ----
    KilnVfxPlay bad = at(known);
    bad.size = 8;
    check(core_play(1, &bad) == 0, "play: a short struct is refused");
    bad = at(zero);
    check(core_play(1, &bad) == 0, "play: the empty GUID is refused");
    bad = at(known);
    bad.flags = 0;
    check(core_play(1, &bad) == 0, "play: neither AT nor ON is refused");
    bad = at(known);
    bad.position.x = 1.0 / 0.0;
    check(core_play(1, &bad) == 0, "play: a position that is not finite is refused");
    bad = at(known);
    bad.flags = KILN_VFX_ON;
    bad.entity = kSimEntity;
    bad.slot = KILN_VFX_SLOTS;
    check(core_play(1, &bad) == 0, "play: a slot out of range is refused");

    // ---- a play goes pending, then playing, then ends ----
    auto p1 = at(known, 50'000'000);  // 50 ms
    const KilnVfx h1 = core_play(1, &p1);
    check(h1 != 0 && core_state(h1) == KILN_VFX_PENDING, "play: a handle at once, pending until the update");
    ticks(1);
    KilnVfxInfo i1 = info(h1);
    check(i1.state == KILN_VFX_PLAYING && i1.fx_handle != 0 && i1.instance != 0 && i1.thread == 7, "update: playing, with the game's handle");
    ticks(5, 20);
    i1 = info(h1);
    check(i1.state == KILN_VFX_STOPPED && i1.reason == KILN_VFX_END_FINISHED, "a lifetime that ran out reads finished");

    // ---- unknown GUID: Kiln's own check ----
    auto p2 = at(unknown);
    const KilnVfx h2 = core_play(1, &p2);
    check(h2 != 0, "an unknown GUID still gets a handle");
    ticks(1);
    KilnVfxInfo i2 = info(h2);
    check(i2.state == KILN_VFX_FAILED && i2.reason == KILN_VFX_END_UNKNOWN_VFX && i2.fx_handle == 0, "unknown GUID: failed, and the game was not called");
    check(status().refused == 0, "Kiln's check is not counted as a guard refusal");

    // ---- the guard in the game's path ----
    auto p3 = at(unknown, -1);
    p3.flags |= KILN_VFX_TEST_FX;
    const KilnVfx h3 = core_play(1, &p3);
    ticks(1);
    check(status().refused == 1, "guard at FxStart: refused, counted");
    KilnVfxInfo i3 = info(h3);
    check(i3.state == KILN_VFX_PLAYING, "guard at FxStart: the game's call returned a handle");
    ticks(1);
    i3 = info(h3);
    check(i3.state == KILN_VFX_STOPPED && i3.reason == KILN_VFX_END_FINISHED, "guard at FxStart: the emptied entry is gone at the next update, no retry for ever");

    KilnVfxPlay p4{};
    p4.size = sizeof p4;
    p4.flags = KILN_VFX_TEST_CREATE;
    std::memcpy(p4.vfx, unknown, 16);
    const KilnVfx h4 = core_play(1, &p4);
    ticks(1);
    check(status().refused == 2, "guard at VfxCreateInstance: refused, counted");
    check(info(h4).state == KILN_VFX_FAILED && info(h4).reason == KILN_VFX_END_UNKNOWN_VFX, "guard at VfxCreateInstance: the game's call returned 0");

    auto p5 = at(known);
    p5.flags |= KILN_VFX_TEST_FX;
    const KilnVfx h5 = core_play(1, &p5);
    ticks(1);
    check(info(h5).state == KILN_VFX_FAILED && info(h5).reason == KILN_VFX_END_REFUSED, "test flags are refused on a GUID the game knows");

    // ---- stop ----
    auto p6 = at(known, -1);
    const KilnVfx h6 = core_play(1, &p6);
    ticks(1);
    check(info(h6).state == KILN_VFX_PLAYING, "a loop plays");
    ticks(5);
    check(info(h6).state == KILN_VFX_PLAYING, "a loop keeps playing");
    check(core_stop(h6, 0) == KILN_OK && core_state(h6) == KILN_VFX_STOPPED, "stop: reads stopped at once");
    ticks(1);
    KilnVfxInfo i6 = info(h6);
    check(i6.state == KILN_VFX_STOPPED && i6.reason == KILN_VFX_END_STOPPED, "stop: done at the next update");
    check(core_stop(h6, 0) == KILN_OK, "stop again: no harm");
    check(core_stop(999999, 0) == KILN_ERR_NOT_FOUND, "stop of an unknown handle: not found");

    auto p7 = at(known, -1);
    const KilnVfx h7 = core_play(1, &p7);
    core_stop(h7, 0);
    ticks(1);
    check(info(h7).state == KILN_VFX_STOPPED && info(h7).fx_handle == 0, "a stop before the update: the game is never called");

    auto p8 = at(known, -1);
    const KilnVfx h8 = core_play(1, &p8);
    ticks(1);
    core_stop(h8, 1);
    ticks(1);
    check(info(h8).state == KILN_VFX_STOPPED, "an immediate stop");

    // ---- attached ----
    KilnVfxPlay on{};
    on.size = sizeof on;
    on.flags = KILN_VFX_ON;
    std::memcpy(on.vfx, known, 16);
    on.entity = kSimEntity;
    on.slot = 8;
    on.lifetime_ns = -1;
    const KilnVfx h9 = core_play(1, &on);
    ticks(1);
    KilnVfxInfo i9 = info(h9);
    check(i9.state == KILN_VFX_PLAYING && (i9.flags & KILN_VFX_INFO_ATTACHED) && i9.position.x == 1.0, "on an entity: attached");
    core_stop(h9, 0);
    ticks(1);

    KilnVfxPlay gone = on;
    gone.entity = 0x70000099;
    const KilnVfx h10 = core_play(1, &gone);
    ticks(1);
    check(info(h10).state == KILN_VFX_FAILED && info(h10).reason == KILN_VFX_END_ENTITY_GONE, "on an entity that does not exist: failed");

    KilnVfxPlay goes = on;
    goes.entity = kSimEntityGoes;
    const KilnVfx h11 = core_play(1, &goes);
    ticks(1);
    check(info(h11).state == KILN_VFX_PLAYING, "on an entity: playing while it lives");
    ticks(40);
    check(info(h11).state == KILN_VFX_STOPPED && info(h11).reason == KILN_VFX_END_ENTITY_GONE, "it ends when the entity goes");

    KilnVfxPlay noslot = on;
    noslot.entity = kSimEntityNoSlot;
    const KilnVfx h_ns = core_play(1, &noslot);
    ticks(2);
    check(info(h_ns).state == KILN_VFX_PLAYING && info(h_ns).instance == 0, "a slot that never resolves: held, no instance");
    const KilnVfx h_ns2 = core_play(1, &noslot);
    ticks(1);
    core_stop(h_ns2, 0);
    ticks(1);
    check(info(h_ns2).reason == KILN_VFX_END_STOPPED, "a stop of an entry without an instance");
    uint32_t leftover = 0;
    check(!sim_game()->lookup(Frame{sim_game(), 1}, info(h_ns2).fx_handle, &leftover), "the stop really released it: the FxController would retry for ever otherwise");
    std::this_thread::sleep_for(std::chrono::milliseconds(5100));
    ticks(1);
    check(info(h_ns).state == KILN_VFX_STOPPED && info(h_ns).reason == KILN_VFX_END_NO_INSTANCE, "no instance after 5 s: Kiln gives up and says why");
    check(!sim_game()->lookup(Frame{sim_game(), 1}, info(h_ns).fx_handle, &leftover), "and releases the entry");

    KilnVfxPlay verify = on;
    verify.flags |= KILN_VFX_AT | KILN_VFX_VERIFY;
    verify.position = KilnVec3{1, 2, 4};
    const KilnVfx h12 = core_play(1, &verify);
    ticks(1);
    check((info(h12).flags & KILN_VFX_INFO_ATTACHED) != 0, "verify: the entity stands where the server said: attached");
    core_stop(h12, 0);
    verify.position = KilnVec3{100, 2, 4};
    const KilnVfx h13 = core_play(1, &verify);
    ticks(1);
    KilnVfxInfo i13 = info(h13);
    check((i13.flags & KILN_VFX_INFO_UNMATCHED) && !(i13.flags & KILN_VFX_INFO_ATTACHED) && i13.state == KILN_VFX_PLAYING, "verify: it stands elsewhere: at the position, not attached");
    core_stop(h13, 0);
    verify.entity = 0x70000099;
    const KilnVfx h14 = core_play(1, &verify);
    ticks(1);
    check((info(h14).flags & KILN_VFX_INFO_UNMATCHED) && info(h14).state == KILN_VFX_PLAYING, "verify: no such entity: at the position");
    core_stop(h14, 0);
    ticks(1);

    // ---- parameters ----
    auto p15 = at(known, -1);
    const KilnVfx h15 = core_play(1, &p15);
    const float radius = 3.5f, radius2 = 7.f;
    check(core_set_param(h15, 0x11, 0x90b5085d, &radius, 4) == KILN_OK, "a parameter before the start is kept");
    check(core_set_param(h15, 0x11, 0x90b5085d, &radius2, 4) == KILN_OK, "a second value of the same name");
    uint8_t big[32] = {};
    check(core_set_param(h15, 0x22, 1, big, sizeof big) == KILN_ERR_INVALID_ARGUMENT, "a parameter over 16 B is refused");
    const uint64_t params_before = sim_params_applied();
    ticks(2);
    float got = 0;
    uint32_t got_hash = 0, got_type = 0;
    sim_last_param(&got_hash, &got_type, &got);
    check(sim_params_applied() == params_before + 1 && got_hash == 0x11 && got_type == 0x90b5085d && got == radius2, "the update applies it once, with the last value");
    core_stop(h15, 0);
    ticks(1);

    // ---- owners ----
    auto p16 = at(known, -1);
    const KilnVfx h16 = core_play(5, &p16), h17 = core_play(5, &p16), h18 = core_play(6, &p16);
    ticks(1);
    check(core_stop_owner(5, 0) == 2, "stop_owner: stops that owner's handles");
    ticks(1);
    check(core_state(h16) == KILN_VFX_STOPPED && core_state(h17) == KILN_VFX_STOPPED && core_state(h18) == KILN_VFX_PLAYING, "another owner's keeps playing");

    // ---- a world ending ----
    auto p19 = at(known, -1);
    const KilnVfx h19 = core_play(6, &p19);
    core_world_ended();
    check(core_state(h18) == KILN_VFX_STOPPED && info(h18).reason == KILN_VFX_END_WORLD_ENDED, "world ended: a playing handle ends");
    check(core_state(h19) == KILN_VFX_FAILED && info(h19).reason == KILN_VFX_END_WORLD_ENDED, "world ended: a pending one fails");
    check((status().flags & KILN_VFX_CONTROLLER) == 0, "world ended: no controller until the next update");
    ticks(1);
    check((status().flags & KILN_VFX_CONTROLLER) != 0, "the next world's update: a controller again");

    // ---- no controller: a request waits, then times out ----
    core_world_ended();
    auto p20 = at(known);
    const KilnVfx h20 = core_play(1, &p20);
    check(core_state(h20) == KILN_VFX_PENDING, "no world: pending");
    ticks(1);
    check(core_state(h20) == KILN_VFX_PLAYING, "the next world's update: it starts");
    core_stop(h20, 1);
    ticks(1);

    // ---- many requests from threads ----
    std::vector<KilnVfx> many(400);
    std::vector<std::thread> ts;
    for (int t = 0; t < 4; ++t)
        ts.emplace_back([&, t] {
            auto p = at(known, -1);
            for (int i = 0; i < 100; ++i) many[t * 100 + i] = core_play(9, &p);
        });
    ticks(10);  // the update runs while requests come in
    for (auto& t : ts) t.join();
    ticks(10);
    size_t playing = 0;
    for (auto h : many) playing += core_state(h) == KILN_VFX_PLAYING;
    check(playing == many.size(), "400 requests from 4 threads, during updates: all play, at most 64 starts per update");
    check(status().live == 400, "live count");
    core_stop_owner(9, 1);
    ticks(1);
    check(status().live == 0, "all stopped: live count 0");

    // ---- too many ----
    auto p21 = at(known, -1);
    std::vector<KilnVfx> flood;
    for (int i = 0; i < 2000; ++i) {
        const KilnVfx h = core_play(10, &p21);
        if (h) flood.push_back(h);
    }
    check(flood.size() == 1024, "the 1,025th live handle is refused");
    core_stop_owner(10, 1);
    ticks(30);
    check(status().live == 0, "the flood stops");

    // ---- status json ----
    char json[1 << 16];
    size_t n = 0;
    check(core_status_json(nullptr, 0, &n) == KILN_ERR_BUFFER_TOO_SMALL && n > 0, "status_json: says what it needs");
    check(core_status_json(json, sizeof json, &n) == KILN_OK && std::strstr(json, "\"sim\":true") != nullptr, "status_json");

    // ---- preload ----
    check(core_preload(known) == KILN_OK && core_preload(known) == KILN_OK && core_preload(zero) == KILN_ERR_INVALID_ARGUMENT, "preload: queued, twice is no harm, empty refused");
    ticks(1);
    check(sim_preloads() == 1, "preload: the game is asked once");
    core_preload(unknown);
    ticks(1);
    check(sim_preloads() == 1, "preload of an unknown GUID: the game is not asked");

    // ---- a registered VFX the builder turned off, and its update hook ----
    static int build_updates = 0;
    core_set_build_hooks([] { ++build_updates; }, [](const uint8_t*) { return true; }, nullptr);
    check(core_is_registered(known) == 0, "build hook: a refused VFX reads as unknown");
    auto p_off = at(known);
    const KilnVfx h_off = core_play(1, &p_off);
    ticks(1);
    check(info(h_off).state == KILN_VFX_FAILED && info(h_off).reason == KILN_VFX_END_UNKNOWN_VFX, "build hook: a refused VFX does not reach the game");
    check(build_updates > 0, "build hook: runs in the FxController update");
    core_set_build_hooks(nullptr, nullptr, nullptr);
    check(core_is_registered(known) == 1, "build hook: no hook, no refusal");

    // ---- how long a built one-shot runs: the builder knows; anything else keeps the rule ----
    core_set_build_hooks(nullptr, nullptr, [](const uint8_t* g) -> int64_t { return g[15] == 1 ? 917'000'000 : 0; });
    auto p_built = at(known);
    const KilnVfx h_built = core_play(1, &p_built);
    ticks(1);
    check(info(h_built).lifetime_ns == (known[15] == 1 ? 917'000'000 : 10'000'000'000), "built one-shot: the builder's length, below the 1 s floor of the rule");
    core_stop(h_built, 1);
    auto p_rule = at(known2);
    const KilnVfx h_rule = core_play(1, &p_rule);
    ticks(1);
    check(info(h_rule).lifetime_ns == 3'500'000'000, "not a built VFX: the resource's rule stands");
    core_stop(h_rule, 1);
    auto p_given = at(known, 123'000'000);
    const KilnVfx h_given = core_play(1, &p_given);
    ticks(1);
    check(info(h_given).lifetime_ns == 123'000'000, "built one-shot: a lifetime the caller gave stands");
    core_stop(h_given, 1);
    core_set_build_hooks(nullptr, nullptr, nullptr);

    std::printf("%d PASS, %d FAIL\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
