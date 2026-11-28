// SPDX-License-Identifier: MIT
// Host tests of kiln.client.game_input (src/services/client_game_input.cpp): the action bits of client 26430625 (decode,
// press, release), keen::Time as the game computes it, the gates' rule (menus, Kiln's UI, focus), the held item's kind
// and the bow's draw, repeat timing, the samples (held, pressed edges, freshness, sessions), the subscribers (order,
// gates, faults, unsubscribe from inside a callback, revoked owners), the stale press guard of the input message, the
// lazy install and the refusals, and torn-free reads from other threads. Run by test-client-game-input.sh (asan, tsan).
#include "services/client_game_input.h"

#include <atomic>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <thread>
#include <vector>

namespace gi = kiln::game_input;
namespace {
int passed = 0, failed = 0;
void check(bool ok, const char* text, const std::string& detail = "") {
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", text, detail.empty() ? "" : ": ", detail.c_str());
    ok ? ++passed : ++failed;
}
std::string hex(uint64_t v) {
    char b[32];
    std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

std::atomic<int64_t> g_now{1'000'000'000};
int64_t fake_clock() { return g_now.load(std::memory_order_relaxed); }
void advance_ms(int64_t ms) { g_now.fetch_add(ms * 1'000'000, std::memory_order_relaxed); }

// client 26430625's keen::ecs::PlayerInputType numbers (sigdb client.game_input)
constexpr gi::Bits kBits{0, 1, 2, 3, 5, 6, 7, 30, 31, 32, 50, 51, 52};
constexpr uint64_t B(int n) { return uint64_t{1} << n; }
constexpr uint32_t A(int a) { return 1u << a; }
enum { P = KILN_GAME_ACTION_PRIMARY_ATTACK, S = KILN_GAME_ACTION_SECONDARY_ATTACK, BL = KILN_GAME_ACTION_BLOCK,
       DO = KILN_GAME_ACTION_DODGE, JU = KILN_GAME_ACTION_JUMP, IN = KILN_GAME_ACTION_INTERACT };

gi::Layout layout() {
    gi::Layout l{};
    l.digital = 936;   // client/cpi.digital
    l.play = 0x3da8;   // client/input.play
    l.held = 0x14f68;  // client/play.held_item
    l.step = 0xe17e9c;
    l.period = 0xe17e80;
    gi::make_layout(kBits, &l);
    return l;
}

// ---- the game: a record, an input object with its play state, a world with its period; fakes for the rest ----
struct FakeGame {
    std::vector<uint8_t> record = std::vector<uint8_t>(1536);
    std::vector<uint8_t> input = std::vector<uint8_t>(0x3db0);
    std::vector<uint8_t> play = std::vector<uint8_t>(0x14f70);
    std::vector<uint8_t> world = std::vector<uint8_t>(0x10);  // only the period is read, at world + period
    uintptr_t rec() const { return reinterpret_cast<uintptr_t>(record.data()); }
    uintptr_t in() const { return reinterpret_cast<uintptr_t>(input.data()); }
    uintptr_t w() const { return reinterpret_cast<uintptr_t>(world.data()) - 0xe17e80; }
    uint64_t digital() const { uint64_t v; std::memcpy(&v, record.data() + 936, 8); return v; }
    void set_digital(uint64_t v) { std::memcpy(record.data() + 936, &v, 8); }
    void hold(uint32_t item) { std::memcpy(play.data() + 0x14f68, &item, 4); }
    FakeGame() {
        const uintptr_t p = reinterpret_cast<uintptr_t>(play.data());
        std::memcpy(input.data() + 0x3da8, &p, sizeof p);
        const int64_t period = 16'666'666;  // keen::Time ns per client step
        std::memcpy(world.data(), &period, 8);
    }
};
FakeGame* g_game = nullptr;
std::atomic<bool> g_read_fault{false}, g_write_fault{false};
int g_writes = 0;
bool fake_read(void* dst, uintptr_t src, size_t n) {
    if (g_read_fault || !src) return false;
    std::memcpy(dst, reinterpret_cast<const void*>(src), n);
    return true;
}
bool fake_write(uintptr_t dst, const void* src, size_t n) {
    if (g_write_fault || !dst) return false;
    std::memcpy(reinterpret_cast<void*>(dst), src, n);
    ++g_writes;
    return true;
}
gi::Ranged g_ranged{};
bool fake_ranged(uintptr_t, KilnEntity, gi::Ranged* out) {
    if (!g_ranged.present) return false;
    *out = g_ranged;
    return true;
}
int g_item_asks = 0;
bool fake_item(uint32_t id, gi::ItemFacts* out) {
    ++g_item_asks;
    switch (id) {
        case 100: *out = {true, gi::kSlotMelee, false}; return true;   // Weapon_T1_1H_Sword
        case 200: *out = {true, gi::kSlotTool, false}; return true;    // Tool_Pickaxe
        case 300: *out = {true, gi::kSlotRanged, true}; return true;   // Weapon_T1_2H_Bow
        case 400: *out = {true, gi::kSlotRanged, false}; return true;  // Weapon_T1_2H_Staff
        case 500: *out = {true, 4, false}; return true;                // a wand: MidRangeWeapon
        case 600: *out = {true, 7, false}; return true;                // a spell
        default: return false;
    }
}
bool fault_call = false;
bool fake_call(KilnGameInputFn fn, KilnGameInputSample* s, void* user) {
    if (fault_call) return false;
    fn(s, user);
    return true;
}
bool fake_cond(KilnGameInputCondFn fn, const KilnGameInputSample* s, void* user, bool* out) {
    if (fault_call) return false;
    *out = fn(s, user);
    return true;
}
const gi::Game kGame{&fake_read, &fake_write, &fake_ranged, &fake_item, &fake_call, &fake_cond};

constexpr KilnEntity kPlayer = 4242;
constexpr KilnOwner kOwner = 77, kOther = 78;
uint32_t g_version = 100;
void visit(uint32_t session = 3) { gi::observe({g_game->rec(), g_game->in(), g_game->w(), kPlayer, session, ++g_version}, kGame); }

// ---- env and installer ----
std::atomic<uint32_t> g_live{3};
bool g_revoked = false;
bool alive(KilnOwner o) { return !(g_revoked && o == kOwner); }
gi::Env client() { return {true, true, g_live.load(), &alive}; }
int g_installs = 0;
gi::Install g_install_result = gi::Install::Bound;
gi::Install installer(gi::Layout* l, const char** why) {
    ++g_installs;
    if (g_install_result != gi::Install::Bound) {
        *why = "refused in a test";
        return g_install_result;
    }
    *l = layout();
    return gi::Install::Bound;
}
const gi::GateFacts kOpen{true, true, gi::kHudInGame, false, true};

void fresh() {
    gi::reset_for_tests();
    gi::set_clock_for_tests(&fake_clock);
    g_installs = 0;
    g_install_result = gi::Install::Bound;
    g_live = 3;
    g_revoked = false;
    g_read_fault = g_write_fault = false;
    fault_call = false;
    g_ranged = {};
    g_writes = 0;
    g_item_asks = 0;
    static FakeGame game;
    game = FakeGame{};
    g_game = &game;
}
void bound() {
    fresh();
    gi::set_installer(&installer);
    KilnGameInputSample s{};
    s.size = sizeof s;
    gi::last(client(), &s, nullptr);  // installs
    gi::publish_gate(kOpen);
}

// ---- subscribers ----
struct Seen {
    std::vector<KilnGameInputSample> samples;
    uint32_t press = 0, release = 0;
    KilnHandle self = 0;
    bool unsubscribe_self = false;
    std::vector<int>* order = nullptr;
    int tag = 0;
};
void KILN_CALL on_sample(KilnGameInputSample* s, void* user) {
    auto* seen = static_cast<Seen*>(user);
    seen->samples.push_back(*s);
    if (seen->order) seen->order->push_back(seen->tag);
    // as Kiln.GameInputSample's Press and Release do: the later call wins
    s->press |= seen->press;
    s->release &= ~seen->press;
    s->release |= seen->release;
    s->press &= ~seen->release;
    if (seen->unsubscribe_self) gi::unsubscribe(seen->self);
}
std::atomic<bool> g_cond{true};
bool KILN_CALL cond(const KilnGameInputSample*, void*) { return g_cond.load(); }

void test_bits() {
    const gi::Layout l = layout();
    check(gi::decode(l, 0) == 0, "decode: nothing held");
    check(gi::decode(l, B(0)) == A(P) && gi::decode(l, B(51)) == A(P) && gi::decode(l, B(52)) == A(P),
          "decode: MainhandAction, RangedCombatShoot or its tap is the primary attack");
    check(gi::decode(l, B(1) | B(2) | B(3)) == 0, "decode: tap, hold and release alone are not the primary held");
    check(gi::decode(l, B(50)) == A(S), "decode: RangedCombatArm is the secondary attack (aim)");
    check(gi::decode(l, B(30)) == A(BL) && gi::decode(l, B(31)) == A(DO) && gi::decode(l, B(32)) == A(JU),
          "decode: Block, Evade (dodge), Jump");
    check(gi::decode(l, B(5)) == A(IN) && gi::decode(l, B(6) | B(7)) == 0, "decode: ContextualAction is interact");
    check(gi::decode(l, B(4) | B(40) | B(53)) == 0, "decode: building, undo and sprint bits are no action");
    const uint64_t all = ~uint64_t{0} >> 8;
    check(gi::decode(l, all) == (A(P) | A(S) | A(BL) | A(DO) | A(JU) | A(IN)), "decode: every action");

    // a held physical primary past the hold threshold: down + hold; Press keeps it down without the hold
    check(gi::apply(l, B(0) | B(2) | B(53), A(P), 0) == (B(0) | B(51) | B(52) | B(53)),
          "press: the primary's family cleared, its down bits set, other bits kept", hex(gi::apply(l, B(0) | B(2), A(P), 0)));
    check(gi::apply(l, B(0) | B(1) | B(2) | B(3) | B(51) | B(52) | B(53), 0, A(P)) == B(53),
          "release: the primary's whole family cleared, other bits kept");
    check(gi::apply(l, B(5) | B(7), 0, A(IN)) == 0 && gi::apply(l, B(7), A(IN), 0) == B(5), "interact: family 5-7, down 5");
    check(gi::apply(l, 0, A(BL) | A(JU), A(DO)) == (B(30) | B(32)), "press and release of several actions");
    check(gi::apply(l, B(31), 0, 0) == B(31), "no answer: the word as it was");
    gi::Bits bad = kBits;
    bad.jump = 64;
    gi::Layout x{};
    check(!gi::make_layout(bad, &x), "make_layout refuses a bit above 63");
}

void test_time() {
    constexpr int64_t inf = std::numeric_limits<int64_t>::max(), ninf = std::numeric_limits<int64_t>::min();
    check(gi::world_time(0, 16'666'666) == 0, "world time: step 0 is 0");
    check(gi::world_time(60, 16'666'666) == 999'999'960, "world time: step x period");
    check(gi::world_time(5, inf) == inf && gi::world_time(5, ninf) == ninf, "world time: an infinite period itself");
    check(gi::elapsed(1000, 400) == 600, "elapsed: now - start");
    check(gi::elapsed(inf, inf) == 0 && gi::elapsed(inf, 5) == inf, "elapsed: now +inf");
    check(gi::elapsed(ninf, ninf) == 0 && gi::elapsed(ninf, 5) == ninf, "elapsed: now -inf");
    check(gi::elapsed(5, inf) == ninf && gi::elapsed(5, ninf) == inf, "elapsed: start infinite");
}

void test_gates() {
    check(gi::gate_reason(kOpen) == KILN_GAME_INPUT_GATE_OPEN, "gate: in game, HUD, no Kiln UI, focused: open");
    check(gi::gate_reason({false, true, 1, false, true}) == KILN_GAME_INPUT_GATE_UNKNOWN, "gate: no kiln.ui screen state");
    check(gi::gate_reason({true, false, 1, false, true}) == KILN_GAME_INPUT_GATE_NOT_PLAYING, "gate: not playing");
    check(gi::gate_reason({true, true, 1, true, true}) == KILN_GAME_INPUT_GATE_KILN_UI, "gate: Kiln's menu or window");
    const uint8_t menus[] = {2, 3, 4, 5, 9, 22, 26, 43, 51, 0};  // system menu, settings, tabs, map, backpack, chest,
                                                                 // dialog, text chat, no UI, none
    bool all = true;
    for (uint8_t h : menus) all = all && gi::gate_reason({true, true, h, false, true}) == KILN_GAME_INPUT_GATE_GAME_MENU;
    check(all, "gate: every game screen but the in-game HUD (menus, map, inventory, chat) closes it");
    check(gi::gate_reason({true, true, 1, false, false}) == KILN_GAME_INPUT_GATE_UNFOCUSED, "gate: unfocused");
    check(gi::gate_reason({true, true, 4, true, false}) == KILN_GAME_INPUT_GATE_KILN_UI, "gate: Kiln's UI is named first");
}

void test_kind() {
    const gi::ItemFacts sword{true, gi::kSlotMelee, false}, pick{true, gi::kSlotTool, false},
        bow{true, gi::kSlotRanged, true}, staff{true, gi::kSlotRanged, false}, wand{true, 4, false};
    const gi::Ranged none{};
    check(gi::held_kind(100, sword, none) == KILN_GAME_HELD_MELEE, "kind: a MeleeWeapon is melee");
    check(gi::held_kind(200, pick, none) == KILN_GAME_HELD_TOOL, "kind: a Tool is tool");
    check(gi::held_kind(500, wand, none) == KILN_GAME_HELD_NONE, "kind: a wand (MidRangeWeapon) is none");
    check(gi::held_kind(0, sword, none) == KILN_GAME_HELD_NONE && gi::held_kind(9, gi::ItemFacts{}, none) == KILN_GAME_HELD_NONE,
          "kind: no item, or one the catalog does not know");
    check(gi::held_kind(300, bow, none) == KILN_GAME_HELD_BOW, "kind: a RangeWeapon named Bow, before the game's state has it");
    check(gi::held_kind(400, staff, none) == KILN_GAME_HELD_NONE, "kind: a staff before the game's state has it");
    const gi::Ranged as_bow{true, 1, gi::kWeaponBow, 1'000'000'000, 0, 400}, as_staff{true, 1, 1, 0, 0, 300};
    check(gi::held_kind(400, staff, as_bow) == KILN_GAME_HELD_BOW, "kind: the game's ranged state says bow for this item");
    check(gi::held_kind(300, bow, as_staff) == KILN_GAME_HELD_NONE,
          "kind: the game's ranged state says staff for this item (its word beats the name)");
    check(gi::held_kind(300, bow, as_bow) == KILN_GAME_HELD_BOW, "kind: the state of another item: the name decides");

    gi::Ranged draw{true, gi::kRangedAimAndCharge, gi::kWeaponBow, 800'000'000, 1'000'000'000, 300};
    check(!gi::bow_drawn(KILN_GAME_HELD_BOW, 300, draw, 1'799'999'999), "bow: not drawn before the charge duration");
    check(gi::bow_drawn(KILN_GAME_HELD_BOW, 300, draw, 1'800'000'000), "bow: drawn at the charge duration (the game's >=)");
    gi::Ranged aim = draw;
    aim.state = 1;  // Aim, no charge
    check(!gi::bow_drawn(KILN_GAME_HELD_BOW, 300, aim, 9'000'000'000), "bow: aiming without a draw is not drawn");
    check(!gi::bow_drawn(KILN_GAME_HELD_BOW, 301, draw, 9'000'000'000), "bow: the state of another item is not drawn");
    check(!gi::bow_drawn(KILN_GAME_HELD_MELEE, 300, draw, 9'000'000'000), "bow: only with a bow held");
    draw.charge = std::numeric_limits<int64_t>::max();
    check(!gi::bow_drawn(KILN_GAME_HELD_BOW, 300, draw, 9'000'000'000), "bow: an infinite charge never draws");
}

void test_repeat_timing() {
    gi::RepeatState r;
    using St = gi::Step;
    check(gi::repeat_step(r, true, 1000, 100) == St::Press, "repeat: the first active sample presses");
    check(gi::repeat_step(r, true, 1016, 100) == St::Release, "repeat: the next sample releases");
    check(gi::repeat_step(r, true, 1033, 100) == St::Release && gi::repeat_step(r, true, 1099, 100) == St::Release,
          "repeat: released until the interval since the press");
    check(gi::repeat_step(r, true, 1100, 100) == St::Press, "repeat: pressed again at the interval");
    check(gi::repeat_step(r, true, 1116, 100) == St::Release, "repeat: and released after one sample");
    check(gi::repeat_step(r, false, 1133, 100) == St::None && r.phase == gi::Phase::Idle,
          "repeat: inactive writes nothing and starts over");
    check(gi::repeat_step(r, true, 1150, 100) == St::Press, "repeat: active again presses at once");
    // 60 Hz samples for one second at 100 ms: a press every 100 ms (the sample at or after it), never two in a row
    gi::RepeatState q;
    int presses = 0;
    bool two = false;
    St prev = St::None;
    for (int i = 0; i < 60; ++i) {
        const St s = gi::repeat_step(q, true, static_cast<uint64_t>(i * 1000 / 60), 100);
        presses += s == St::Press;
        two = two || (s == St::Press && prev == St::Press);
        prev = s;
    }
    check(presses == 10 && !two, "repeat: 10 presses a second at 100 ms, each followed by a release",
          std::to_string(presses));
}

void test_refusals() {
    fresh();
    KilnGameInputSample s{};
    s.size = sizeof s;
    KilnHandle h = 9;
    Seen seen;
    check(gi::last({false, true, 3, nullptr}, &s, nullptr) == KILN_ERR_UNAVAILABLE &&
              gi::subscribe({false, true, 3, nullptr}, kOwner, &on_sample, &seen, &h) == KILN_ERR_UNAVAILABLE && h == 0,
          "a dedicated server: unavailable");
    check(gi::last({true, false, 3, nullptr}, &s, nullptr) == KILN_ERR_UNAVAILABLE, "safe idle: unavailable");
    check(gi::last(client(), &s, nullptr) == KILN_ERR_UNAVAILABLE, "no installer (an unresolved build): unavailable");
    const char* why = nullptr;
    check(!gi::usable(&why, nullptr) && why && std::string(why).find("client.game_input") != std::string::npos,
          "usable says the build has no sites", why ? why : "");
    gi::set_installer(&installer);
    KilnGameInputSample shortish{};
    shortish.size = sizeof shortish - 8;
    check(gi::last(client(), &shortish, nullptr) == KILN_ERR_INVALID_ARGUMENT && gi::last(client(), nullptr, nullptr) == KILN_ERR_INVALID_ARGUMENT,
          "last: a short or null sample");
    check(gi::subscribe(client(), 0, &on_sample, &seen, &h) == KILN_ERR_INVALID_ARGUMENT, "subscribe: owner 0");
    check(gi::subscribe(client(), kOwner, nullptr, &seen, &h) == KILN_ERR_INVALID_ARGUMENT, "subscribe: no fn");
    check(gi::repeat(client(), kOwner, KILN_GAME_ACTION_COUNT, 100, nullptr, nullptr, &h) == KILN_ERR_INVALID_ARGUMENT,
          "repeat: an unknown action");
    g_revoked = true;
    check(gi::subscribe(client(), kOwner, &on_sample, &seen, &h) == KILN_ERR_REVOKED, "subscribe: a revoked owner");
    g_revoked = false;
    check(g_installs == 0, "no refusal installs the hooks");
}

void test_install() {
    fresh();
    gi::set_installer(&installer);
    bool installed = true;
    check(gi::usable(nullptr, &installed) && !installed, "ready: usable, not installed");
    g_install_result = gi::Install::Again;
    KilnGameInputSample s{};
    s.size = sizeof s;
    uint32_t reason = 9;
    check(gi::last(client(), &s, &reason) == KILN_OK && reason == KILN_GAME_INPUT_REASON_NO_SAMPLE && g_installs == 1,
          "a refused install (threads not checked): last answers NO_SAMPLE");
    KilnHandle h = 0;
    Seen seen;
    check(gi::subscribe(client(), kOwner, &on_sample, &seen, &h) == KILN_ERR_UNAVAILABLE && g_installs == 1,
          "a second use within a second waits (unavailable, no attempt)");
    advance_ms(1001);
    g_install_result = gi::Install::Bound;
    check(gi::subscribe(client(), kOwner, &on_sample, &seen, &h) == KILN_OK && h && g_installs == 2,
          "a later use installs and subscribes");
    check(gi::usable(nullptr, &installed) && installed, "installed");
    check(gi::last(client(), &s, &reason) == KILN_OK && g_installs == 2, "installed once");

    fresh();
    gi::set_installer(&installer);
    g_install_result = gi::Install::Failed;
    check(gi::last(client(), &s, &reason) == KILN_ERR_UNAVAILABLE && gi::install_failed(), "a failed install is final");
    const char* why = nullptr;
    check(!gi::usable(&why, nullptr) && why && std::string(why) == "refused in a test", "its reason stands", why ? why : "");
    advance_ms(5000);
    check(gi::last(client(), &s, &reason) == KILN_ERR_UNAVAILABLE && g_installs == 1, "and is not tried again");
}

void test_samples() {
    bound();
    KilnGameInputSample s{};
    s.size = sizeof s;
    uint32_t reason = 9;
    check(gi::last(client(), &s, &reason) == KILN_OK && reason == KILN_GAME_INPUT_REASON_NO_SAMPLE && s.sequence == 0,
          "no sample before the first visit");
    g_game->set_digital(B(0) | B(53));
    g_game->hold(100);
    visit();
    check(gi::last(client(), &s, &reason) == KILN_OK && reason == KILN_GAME_INPUT_REASON_NONE, "a fresh sample");
    check(s.player == kPlayer && s.client_session == 3 && s.step == g_version && s.sequence == 1, "its player, session, step");
    check(s.held == A(P) && s.pressed == A(P) && s.digital == (B(0) | B(53)), "held and pressed at the first sample");
    check(s.held_kind == KILN_GAME_HELD_MELEE && s.held_item == 100 && s.flags == 0, "the held sword is melee");
    check(s.gate == KILN_GAME_INPUT_GATE_OPEN, "the gate of the sample");
    visit();
    gi::last(client(), &s, &reason);
    check(s.held == A(P) && s.pressed == 0, "still held: not pressed again");
    g_game->set_digital(B(0) | B(30));
    visit();
    gi::last(client(), &s, &reason);
    check(s.held == (A(P) | A(BL)) && s.pressed == A(BL), "block went down");
    check(g_item_asks == 1, "the catalog is asked once per held item", std::to_string(g_item_asks));
    g_game->hold(300);
    g_ranged = {true, gi::kRangedAimAndCharge, gi::kWeaponBow, 500'000'000, 0, 300};
    g_version = 99;  // the client world's step: 100 at the visit -> 100 x 16.67 ms = 1.67 s since a draw at 0
    visit();
    gi::last(client(), &s, &reason);
    check(s.held_kind == KILN_GAME_HELD_BOW && (s.flags & KILN_GAME_INPUT_BOW_DRAWN), "a bow drawn past its charge");
    g_ranged.start = 1'200'000'000;
    visit();
    gi::last(client(), &s, &reason);
    check(s.held_kind == KILN_GAME_HELD_BOW && !(s.flags & KILN_GAME_INPUT_BOW_DRAWN), "a bow still charging");
    advance_ms(251);
    gi::last(client(), &s, &reason);
    check(reason == KILN_GAME_INPUT_REASON_STALE && s.age_ns == 251'000'000, "older than 250 ms: STALE");
    g_live = 4;
    gi::last(client(), &s, &reason);
    check(reason == KILN_GAME_INPUT_REASON_NO_SAMPLE, "another live session: no sample");
    g_live = 3;
    gi::session_ended(3);
    gi::last(client(), &s, &reason);
    check(reason == KILN_GAME_INPUT_REASON_NO_SAMPLE, "after the session's STOPPING: no sample");
    g_live = 5;
    g_game->set_digital(B(0));
    visit(5);
    gi::last(client(), &s, &reason);
    check(reason == KILN_GAME_INPUT_REASON_NONE && s.pressed == A(P), "a new session: the first sample presses again");
    g_read_fault = true;
    const uint64_t faults = gi::stats().faults;
    visit(5);
    g_read_fault = false;
    check(gi::stats().faults == faults + 1, "a faulted read skips the sample");
}

void test_subscribers() {
    bound();
    Seen a, b;
    std::vector<int> order;
    a.order = b.order = &order;
    a.tag = 1;
    b.tag = 2;
    KilnHandle ha = 0, hb = 0;
    gi::subscribe(client(), kOwner, &on_sample, &a, &ha);
    gi::subscribe(client(), kOther, &on_sample, &b, &hb);
    g_game->set_digital(B(53));
    visit();
    check(order == std::vector<int>{1, 2}, "subscribers are asked in order");
    check(a.samples.size() == 1 && a.samples[0].held == 0 && a.samples[0].press == 0, "they see the game's sample");
    check(g_game->digital() == B(53) && g_writes == 0, "no answer writes nothing");

    a.press = A(P);
    visit();
    check(g_game->digital() == (B(0) | B(51) | B(52) | B(53)), "a press is written", hex(g_game->digital()));
    check(b.samples.back().press == A(P), "a later subscriber sees the earlier answer");
    b.release = A(P);
    visit();
    check(g_game->digital() == B(53), "the later answer wins (release after press)");
    a.press = b.release = 0;

    g_game->set_digital(B(0) | B(2));
    b.release = A(P);
    visit();
    check(g_game->digital() == 0, "a held primary (with its hold) released for the sample");
    check(b.samples.back().held == A(P), "IsHeld is the game's sample, before any answer");
    b.release = 0;

    // closed gates: nobody asked, nothing written
    const uint8_t closing[] = {4, 5, 43};
    for (uint8_t hud : closing) {
        gi::publish_gate({true, true, hud, false, true});
        const size_t n = a.samples.size();
        a.press = A(JU);
        g_game->set_digital(0);
        visit();
        check(a.samples.size() == n && g_game->digital() == 0, ("a game screen closes the gate: hud " + std::to_string(hud)).c_str());
    }
    gi::publish_gate({true, true, 1, true, true});
    size_t n = a.samples.size();
    visit();
    check(a.samples.size() == n && g_game->digital() == 0, "Kiln's UI closes the gate");
    gi::publish_gate({true, true, 1, false, false});
    visit();
    check(a.samples.size() == n && g_game->digital() == 0, "an unfocused window closes the gate");
    KilnGameInputSample s{};
    s.size = sizeof s;
    gi::last(client(), &s, nullptr);
    check(s.gate == KILN_GAME_INPUT_GATE_UNFOCUSED, "the sample still reads, with its gate");
    gi::publish_gate(kOpen);
    visit();
    check(a.samples.size() == n + 1 && g_game->digital() == B(32), "open again: asked and written");
    a.press = 0;

    // unsubscribe from inside the callback, a fault, a revoked owner
    b.self = hb;
    b.unsubscribe_self = true;
    visit();
    n = b.samples.size();
    visit();
    check(b.samples.size() == n && gi::subscriptions_for_tests() == 1, "unsubscribe from inside its call ends it");
    check(gi::unsubscribe(hb) == KILN_ERR_NOT_FOUND, "a second unsubscribe: not found");
    fault_call = true;
    visit();
    fault_call = false;
    check(gi::subscriptions_for_tests() == 0 && gi::stats().sub_faults == 1, "a faulting subscriber is removed");
    KilnHandle hc = 0;
    gi::subscribe(client(), kOwner, &on_sample, &a, &hc);
    gi::owner_gone(kOwner);
    check(gi::subscriptions_for_tests() == 0, "a revoked owner's subscriptions end");
    KilnHandle many[gi::kMaxSubscriptions] = {};
    KilnResult rc = KILN_OK;
    for (auto& m : many) rc = rc == KILN_OK ? gi::subscribe(client(), kOwner, &on_sample, &a, &m) : rc;
    KilnHandle extra = 0;
    check(rc == KILN_OK && gi::subscribe(client(), kOwner, &on_sample, &a, &extra) == KILN_ERR_BUFFER_TOO_SMALL,
          "64 subscriptions at most");
    for (auto m : many) gi::unsubscribe(m);
}

void test_repeat_through_samples() {
    bound();
    KilnHandle h = 0;
    check(gi::repeat(client(), kOwner, P, 10, nullptr, nullptr, &h) == KILN_OK && h, "repeat subscribes");
    // the player holds the primary (its hold bit already set by the game): press, then releases, then a press at 50 ms
    std::string got;
    for (int i = 0; i < 8; ++i) {
        g_game->set_digital(B(0) | B(2));
        visit();
        const uint64_t d = g_game->digital();
        got += d == (B(0) | B(51) | B(52)) ? 'P' : d == 0 ? 'r' : '?';
        advance_ms(17);
    }
    check(got == "PrrPrrPr", "held: press, release until 50 ms (the least interval), press again", got);
    g_game->set_digital(0);
    visit();
    check(g_game->digital() == 0, "let go: the repeat writes nothing");
    gi::unsubscribe(h);

    g_cond = true;
    gi::repeat(client(), kOwner, BL, 100, &cond, nullptr, &h);
    g_game->set_digital(0);
    visit();
    check(g_game->digital() == B(30), "a condition repeats an action that is not held");
    g_cond = false;
    g_game->set_digital(0);  // the game's writer rewrites the word at every sample
    visit();
    check(g_game->digital() == 0, "a false condition writes nothing");
    g_cond = true;
    gi::publish_gate({true, true, 5, false, true});
    g_game->set_digital(0);
    visit();
    check(g_game->digital() == 0, "a closed gate writes nothing");
    gi::publish_gate(kOpen);
    g_game->set_digital(0);
    visit();
    check(g_game->digital() == B(30), "open again: starts over with a press");
    gi::unsubscribe(h);
}

void test_guard() {
    bound();
    Seen a;
    a.press = A(P);
    KilnHandle h = 0;
    gi::subscribe(client(), kOwner, &on_sample, &a, &h);
    g_game->set_digital(B(53));
    visit();
    const uint32_t v = g_version;
    const uint64_t pressed = B(0) | B(51) | B(52) | B(53);
    check(g_game->digital() == pressed, "a synthetic press");
    gi::message(g_game->rec(), kPlayer, v, 3, kGame);
    check(g_game->digital() == pressed, "the message of the sample itself sends the press");
    gi::message(g_game->rec(), kPlayer, v + 1, 3, kGame);
    check(g_game->digital() == B(53) && gi::stats().cleared == 1,
          "a skipped writer: the next message's step clears Kiln's bits only", hex(g_game->digital()));
    g_game->set_digital(pressed);
    gi::message(g_game->rec(), kPlayer, v + 2, 3, kGame);
    check(g_game->digital() == pressed && gi::stats().cleared == 1, "once: the record is the game's after that");

    // the game's own press of the same bits is never cleared: the sample already had them
    g_game->set_digital(B(0) | B(51) | B(52));
    visit();
    gi::message(g_game->rec(), kPlayer, g_version + 1, 3, kGame);
    check(g_game->digital() == (B(0) | B(51) | B(52)), "bits the game sampled itself are not Kiln's");

    struct Case { const char* text; uintptr_t record; KilnEntity entity; uint32_t session; uint64_t change; };
    std::vector<uint8_t> other(1536);
    const Case cases[] = {
        {"another record: untouched", reinterpret_cast<uintptr_t>(other.data()), kPlayer, 3, 0},
        {"another entity: untouched", 0, kPlayer + 1, 3, 0},
        {"another session: untouched", 0, kPlayer, 4, 0},
        {"the word changed since: untouched", 0, kPlayer, 3, B(31)},
    };
    for (const Case& c : cases) {
        g_game->set_digital(B(53));
        visit();
        const uint64_t w = g_game->digital() | c.change;
        g_game->set_digital(w);
        gi::message(c.record ? c.record : g_game->rec(), c.entity, g_version + 1, c.session, kGame);
        check(g_game->digital() == w, c.text, hex(g_game->digital()));
    }
    // a new visit replaces the guard: its own sample is the game's
    g_game->set_digital(B(53));
    visit();
    a.press = 0;
    g_game->set_digital(B(0));
    visit();
    gi::message(g_game->rec(), kPlayer, g_version + 1, 3, kGame);
    check(g_game->digital() == B(0), "a visit without a press leaves nothing to clear");
    gi::unsubscribe(h);
}

void test_threads() {
    bound();
    std::atomic<bool> stop{false};
    std::atomic<int> torn{0}, reads{0};
    std::thread reader([&] {
        KilnGameInputSample s{};
        s.size = sizeof s;
        while (!stop) {
            uint32_t reason = 0;
            gi::last(client(), &s, &reason);
            if (reason == KILN_GAME_INPUT_REASON_NONE) {
                reads.fetch_add(1);
                // every visit below holds the primary exactly at odd steps: held, digital and step agree in one sample
                const bool odd = (s.step & 1) != 0;
                if (s.held != (odd ? A(P) : 0u) || s.digital != (odd ? B(0) : 0) || s.player != kPlayer)
                    torn.fetch_add(1);
            }
        }
    });
    std::thread subscriber([&] {
        Seen seen;
        while (!stop) {
            KilnHandle h = 0;
            if (gi::subscribe(client(), kOther, &on_sample, &seen, &h) == KILN_OK) gi::unsubscribe(h);
        }
    });
    for (int i = 0; i < 20000; ++i) {
        g_game->set_digital((g_version + 1) & 1 ? B(0) : 0);
        visit();
    }
    stop = true;
    reader.join();
    subscriber.join();
    check(torn == 0 && reads > 0, "readers on another thread never see a torn sample", std::to_string(reads.load()));
    check(gi::stats().samples >= 20000, "every visit published");
}

}  // namespace

int main() {
    test_bits();
    test_time();
    test_gates();
    test_kind();
    test_repeat_timing();
    test_refusals();
    test_install();
    test_samples();
    test_subscribers();
    test_repeat_through_samples();
    test_guard();
    test_threads();
    std::printf("%d passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
