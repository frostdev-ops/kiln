// SPDX-License-Identifier: MIT
// Execute the production NoClip hook with the real flight hooks and rule snapshots, without a game: whose collision step
// it skips, that the game's switch is put back, and every way it steps aside.
#define KILN_RULES_HOST_TEST
#include "rules.cpp"
#include "flight.cpp"
#include "noclip.cpp"

#include <cstdlib>

namespace kiln_rules_creative {
bool test_idle = false;
bool idle() { return test_idle; }
bool flame_creative() { return test_idle; }
}  // namespace kiln_rules_creative

namespace {
int failures = 0, entities = 0, steps = 0;
bool entity_faults = false, step_faults = false;
uint8_t seen_enabled = 0xff;  // MovementCollider.isEnabled as the game's step saw it
void* seen_args[9]{};
void check(bool ok, const char* why) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", why); failures += !ok; }
uint32_t* entity(void* p, uint32_t* out) {
    ++entities;
    if (entity_faults) throw 1;  // stands in for a fault while reading the game's memory
    *out = *static_cast<uint32_t*>(p);
    return out;
}
bool actor_flag(void* p, uint8_t flag) { return static_cast<bool*>(p)[flag]; }

constexpr int kCollider = 0xc0, kDynloco = 0x90, kEnabled = 5;
// The game's per-entity collision step: it only looks at the switch here.
void game_step(void* ctx, void* iter, void* frame, void* platform, void* a5, uint8_t* slots, void* a7, void* a8, void* a9) {
    ++steps;
    void* args[9] = {ctx, iter, frame, platform, a5, slots, a7, a8, a9};
    std::memcpy(seen_args, args, sizeof args);
    auto* collider = *reinterpret_cast<uint8_t**>(slots + kCollider);
    seen_enabled = collider ? collider[kEnabled] : 0xfe;
    if (step_faults) throw 2;  // the game's own step (or a later link of the chain) faults
}

uint32_t session = KILN_SESSION_REMOTE, local = 3;
KilnResult KILN_CALL client_state(KilnClientState* s) { s->local_player = local; s->session = session; return KILN_OK; }
// kiln.caps, as far as the hook uses it: the last report for rules.noclip
uint32_t cap_state = 99, cap_reason = 99;
std::string cap_text;
KilnResult KILN_CALL cap_report(KilnOwner, KilnStr name, uint32_t state, uint32_t reason, KilnStr text) {
    if (std::string(name.ptr, name.len) != "rules.noclip") return KILN_ERR_INVALID_ARGUMENT;
    cap_state = state; cap_reason = reason; cap_text.assign(text.ptr, text.len);
    return KILN_OK;
}
KilnCapsApi caps_table{};
const void* KILN_CALL service(KilnStr name, uint32_t) {
    return std::string(name.ptr, name.len) == KILN_CAPS_SERVICE ? &caps_table : nullptr;
}
bool have = true;
bool KILN_CALL feature(KilnStr) { return have; }
int64_t collider_at = kCollider, dynloco_at = kDynloco, enabled_at = kEnabled;
KilnResult KILN_CALL symbol(KilnStr, uintptr_t* out) { *out = reinterpret_cast<uintptr_t>(&game_step); return KILN_OK; }
KilnResult KILN_CALL constant(KilnStr name, int64_t* out) {
    const std::string n(name.ptr, name.len);
    *out = n == "rules/resolve.collider" ? collider_at : n == "rules/resolve.dynloco" ? dynloco_at : n == "rules/collider.enabled" ? enabled_at : 8;
    return KILN_OK;
}
void set_rule(uint32_t e, const KilnRulesPlayer* rule) {
    if (e <= kMaxPlayer) { store(g_players[e], rule); return; }
    store(g_extra[0], rule); g_extra_ids[0] = rule ? e : 0; g_extra_hint[0] = rule ? e : 0;
}
KilnRulesNoClip state_of(uint32_t e) {
    KilnRulesNoClip s{};
    s.size = sizeof s;
    kiln_rules_noclip::state(e, &s);
    return s;
}
}  // namespace

int main() {
    namespace f = kiln_rules_flight;
    namespace n = kiln_rules_noclip;
    f::g_entity_of = &entity;
    f::g_actor_state = &actor_flag;
    f::L.loco_actor = 16; f::L.loco_dynloco = 8; f::L.loco_state = 0x59; f::L.grav_actor = 16;
    f::L.mover_dynloco = 8; f::L.player_input = 16; f::L.input_mask = 0;
    caps_table.header = {sizeof caps_table, KILN_CAPS_VERSION};
    caps_table.report = &cap_report;
    KilnApi api{}; api.service = &service; api.feature = &feature; api.symbol = &symbol; api.constant = &constant;
    std::string off;
    check(n::install(&api, 0, true, off) == KILN_RULES_HOOK_NOCLIP && off.empty() && n::h_resolve &&
          n::L.collider == kCollider && n::L.dynloco == kDynloco && n::L.enabled == kEnabled,
          "with the feature, its layout and character flight's hooks, the hook goes in");
    check(cap_state == 99, "and the capability row stays as sigdb reported it");

    alignas(16) uint8_t dyn[128]{};
    uint8_t collider[84]{};
    collider[kEnabled] = 1;
    bool actor[64]{};
    uint32_t e = 3;
    uint8_t slots[0x120]{};
    auto put = [&](int at, void* p) { std::memcpy(slots + at, &p, sizeof p); };
    put(kDynloco, dyn);
    put(kCollider, collider);
    int a5 = 0, a7 = 0, a8 = 0, a9 = 0, iter = 0, frame = 0, platform = 0;
    auto step = [&] { n::hk_resolve(&e, &iter, &frame, &platform, &a5, slots, &a7, &a8, &a9); };

    step();
    check(steps == 1 && seen_enabled == 1 && entities == 0, "nobody has NoClip: the game's step runs untouched, with no entity lookup");
    check(seen_args[0] == &e && seen_args[1] == &iter && seen_args[2] == &frame && seen_args[3] == &platform && seen_args[4] == &a5 &&
          seen_args[5] == slots && seen_args[6] == &a7 && seen_args[7] == &a8 && seen_args[8] == &a9,
          "all nine arguments reach the game's step unchanged");

    auto rule = kDefault;
    rule.flags = KILN_RULES_NOCLIP;
    set_rule(3, &rule);
    check((kiln_rules_bridge::flags_seen() & KILN_RULES_NOCLIP) != 0 && !(kiln_rules_bridge::flags_seen() & KILN_RULES_CHARACTER_FLIGHT),
          "a NoClip rule publishes its own flag, not character flight's");
    step();
    check(seen_enabled == 1, "the rule alone skips nothing: the character is not flying yet");
    void* stack[4] = {nullptr, dyn, actor, nullptr};
    safetyhook::Context c{};
    c.rbp = c.rsp = reinterpret_cast<uintptr_t>(stack); c.rsi = c.rdi = reinterpret_cast<uintptr_t>(&e);
    dyn[0x59] = flight_math::kBuildingFlying;
    step();
    check(seen_enabled == 1, "a BuildingFlying state Kiln did not start (another flight, a spark) keeps its collision");
    // and stays so step after step: the state hook runs between collision steps and must not adopt that flight
    for (int i = 0; i < 3; ++i) {
        c.rax = flight_math::kBuildingFlying;
        f::state_body(c);
        step();
    }
    uint64_t takeoffs = 0, landings = 0, ends = 0, flight_faults = 0;
    f::counters(&takeoffs, &landings, &ends, &flight_faults);
    check(seen_enabled == 1 && c.rax == flight_math::kBuildingFlying && takeoffs == 0 && ends == 0 && !f::flown_recently(3, dyn),
          "the state hook leaves a flight it did not start alone: not adopted, not ended, collision kept");
    dyn[0x59] = flight_math::kWalking;

    // the production state hook takes off: from the ground, at once
    actor[0] = true;  // Grounded
    c.rax = flight_math::kWalking;
    f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying, "NoClip takes off from the ground through the production state hook");
    dyn[0x59] = flight_math::kBuildingFlying;
    c.xmm6.f32[0] = 9;
    f::grav_body(c);
    check(c.xmm6.f32[0] == 0, "NoClip's flight has no gravity");
    uint64_t input[2] = {0, uint64_t{1} << 32};
    void* it[3] = {&e, dyn, input};
    f::mover_pre(reinterpret_cast<uint8_t*>(it));
    check(reinterpret_cast<float*>(dyn)[1] == flight_math::kClimb, "held jump climbs in NoClip's flight");

    const auto before = steps;
    step();
    check(steps == before + 1 && seen_enabled == 0, "in Kiln's flight with the rule, the game's step sees the collision switch off");
    check(collider[kEnabled] == 1, "the switch is back on when the hook returns");
    auto st = state_of(3);
    check(st.active == 1 && st.skipped == 1 && st.total == 1 && st.faults == 0, "the entity's state says active, one step skipped");
    n::tick(); step(); n::tick();
    st = state_of(3);
    check(st.active == 1 && st.skipped == 2, "active holds from one tick to the next while steps are skipped");
    check(state_of(4).active == 0 && state_of(4).skipped == 0 && state_of(0).total == 2, "another entity has no state; entity 0 gives the totals");

    collider[kEnabled] = 0;
    step();
    check(seen_enabled == 0 && collider[kEnabled] == 0, "a collider the game itself switched off stays off afterwards");
    collider[kEnabled] = 1;

    e = 4;
    step();
    check(seen_enabled == 1, "another player in the same step keeps collision");
    e = 3;

    actor[44] = true; c.rax = flight_math::kDiving;  // UnderWater: the game wants to dive
    dyn[0x59] = flight_math::kBuildingFlying;
    // LocoNextState keeps BuildingFlying while the state is BuildingFlying; water does not end NoClip's flight
    c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    step();
    check(c.rax == flight_math::kBuildingFlying && seen_enabled == 0, "under water NoClip keeps flying, without collision");
    actor[44] = false;

    put(kDynloco, nullptr);
    step();
    check(seen_enabled == 1, "an entity without DynamicLocomotion is never touched");
    put(kDynloco, dyn);
    put(kCollider, nullptr);
    step();
    check(seen_enabled == 0xfe, "a null collider record is passed through");
    put(kCollider, collider);

    entity_faults = true;
    const auto calls = steps;
    step();
    entity_faults = false;
    check(steps == calls + 1 && seen_enabled == 1 && state_of(0).faults == 1, "a fault in the hook's own body: the game's step runs once, with collision");

    // the game's own step faults with the switch held off: the fault goes on, the switch does not stay off
    step_faults = true;
    bool passed_on = false;
    try { step(); } catch (int) { passed_on = true; }
    step_faults = false;
    check(passed_on && seen_enabled == 0 && collider[kEnabled] == 1, "a fault in the game's step passes through, and the switch is back on behind it");
    set_rule(3, nullptr);
    step();
    check(seen_enabled == 1, "so with the rule gone the next step collides, also after such a fault");
    set_rule(3, &rule);
    collider[kEnabled] = 0; step_faults = true;
    try { step(); } catch (int) {}
    step_faults = false;
    check(collider[kEnabled] == 0, "a collider the game had switched off itself is still off after a fault");
    collider[kEnabled] = 1;

    kiln_rules_creative::test_idle = true;
    step();
    check(seen_enabled == 1, "while Creative Mode runs, NoClip skips nothing");
    kiln_rules_creative::test_idle = false;
    f::note_flying(3, dyn);  // (the idle state hook was not run: ownership is as before)

    actor[7] = true;  // Dead
    c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    check(c.rax == flight_math::kFalling, "death ends NoClip's flight");
    dyn[0x59] = flight_math::kFalling;
    step();
    check(seen_enabled == 1, "and collision is back in the same step");
    actor[7] = false;

    // take off again, then the rule ends
    c.rax = flight_math::kFalling; f::state_body(c); dyn[0x59] = flight_math::kBuildingFlying;
    step();
    check(seen_enabled == 0, "it takes off again after respawning with the rule");
    set_rule(3, nullptr);
    check(!(kiln_rules_bridge::flags_seen() & KILN_RULES_NOCLIP), "revoking the last NoClip rule clears the fast hint");
    const auto looked = entities;
    step();
    check(seen_enabled == 1 && entities == looked, "the rule ended: collision is back at once, before the state hook even runs");
    n::tick(); n::tick();
    check(state_of(3).active == 0, "active ends within two ticks of the last skipped step");
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    check(c.rax == flight_math::kFalling, "and the character falls");
    dyn[0x59] = flight_math::kFalling;

    // character flight alone never skips collision
    rule.flags = KILN_RULES_CHARACTER_FLIGHT;
    set_rule(3, &rule);
    actor[0] = false; c.rax = flight_math::kFalling; f::state_body(c); dyn[0x59] = flight_math::kBuildingFlying;
    step();
    check(c.rax == flight_math::kBuildingFlying && seen_enabled == 1, "character flight without NoClip collides as before");
    set_rule(3, nullptr); f::reset(); n::reset(); dyn[0x59] = flight_math::kFalling;
    check(state_of(3).skipped == 0, "a world reset forgets the per-entity numbers");

    // a lab's player character (not a player id)
    e = 5000;
    rule.flags = KILN_RULES_NOCLIP;
    set_rule(5000, &rule);
    c.rax = flight_math::kWalking; f::state_body(c); dyn[0x59] = flight_math::kBuildingFlying;
    step();
    st = state_of(5000);
    check(seen_enabled == 0 && st.active == 1 && st.skipped == 1, "a simulated character with the rule flies without collision too");
    set_rule(5000, nullptr);
    check(state_of(5000).skipped == 0, "its numbers end with its rule slot");
    f::reset(); n::reset(); dyn[0x59] = flight_math::kFalling;

    // a joined client: only the local player's prediction
    KilnClientApi client{}; client.state = &client_state;
    f::g_api = &api; f::g_client = &client;
    store(g_local, &rule); f::refresh();
    e = 3; c.rax = flight_math::kWalking; f::state_body(c); dyn[0x59] = flight_math::kBuildingFlying;
    step();
    check(seen_enabled == 0, "a joined client's local player predicts NoClip from its local rules");
    e = 9;
    step();
    check(seen_enabled == 1, "another character in the client's prediction keeps collision");
    // a joined game has no server tick: its client tick ages "active"
    check(state_of(3).active == 1, "the joined client's hook says active while it skips");
    store(g_local, nullptr);
    step();
    f::client_pre(nullptr, nullptr); f::client_pre(nullptr, nullptr);
    check(seen_enabled == 1 && state_of(3).active == 0, "after the rule went, two client ticks later it is no longer active");
    store(g_local, &rule); f::refresh();
    session = KILN_SESSION_HOST; f::refresh(); e = 3;
    const auto before_host = state_of(0).total;
    step();
    check(seen_enabled == 1, "a host never skips collision from stale local rules: the server's per-entity rules decide");
    n::tick(); n::tick();
    step();
    const auto age = n::g_tick.load();
    f::client_pre(nullptr, nullptr);
    check(n::g_tick.load() == age && state_of(0).total == before_host, "a host's client tick does not age it a second time (its server tick does)");
    store(g_local, nullptr); session = KILN_SESSION_REMOTE; f::refresh(); f::reset(); n::reset();

    // install, the ways it stays out
    have = false;
    check(n::install(&api, 0, true, off) == 0 && off.find("rules.noclip") != std::string::npos && cap_state == 99,
          "a build without the feature: NoClip is off and says so (the sigdb row already carries the reason)");
    have = true; off.clear();
    check(n::install(&api, 0, false, off) == 0 && off.find("needs character flight") != std::string::npos,
          "without character flight's hooks NoClip is never installed");
    check(cap_state == KILN_CAP_UNAVAILABLE && cap_reason == KILN_CAP_REASON_POLICY && cap_text.find("character flight") != std::string::npos,
          "and the capability row is lowered with that reason");
    off.clear(); enabled_at = 0x200;
    check(n::install(&api, 0, true, off) == 0 && off.find("unexpected layout") != std::string::npos, "a layout that is not a bool in a small component keeps it off");
    enabled_at = kEnabled; collider_at = 0x93; off.clear();
    check(n::install(&api, 0, true, off) == 0 && off.find("unexpected layout") != std::string::npos, "a record offset that is not a slot keeps it off");
    collider_at = dynloco_at; off.clear();
    check(n::install(&api, 0, true, off) == 0, "two records at one offset keep it off");
    collider_at = kCollider; off.clear();
    n::h_resolve.revoke();
    kiln::test::fail_all = true;
    check(n::install(&api, 0, true, off) == 0 && off.find("hook failed") != std::string::npos && !n::h_resolve &&
          cap_reason == KILN_CAP_REASON_FAULTED, "a refused hook: off, and the row says faulted");
    KilnRulesNoClip none{}; none.size = sizeof none;
    check(n::state(3, &none) == KILN_ERR_UNAVAILABLE, "without the hook the state call says unavailable");
    kiln::test::fail_all = false; off.clear();
    check(n::install(&api, 0, true, off) == KILN_RULES_HOOK_NOCLIP && off.empty() && n::h_resolve, "and it goes in again once the hook is accepted");
    KilnRulesNoClip small{}; small.size = 8;
    check(n::state(0, &small) == KILN_OK && small.size == 8, "an older caller's smaller struct is filled to its own size");
    small.size = 4;
    check(n::state(0, &small) == KILN_ERR_INVALID_ARGUMENT && n::state(0, nullptr) == KILN_ERR_INVALID_ARGUMENT, "a struct too small is refused");
    std::printf("noclip hook: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
