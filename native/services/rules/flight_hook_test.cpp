// SPDX-License-Identifier: MIT
// Execute the production flight hooks, including entity selection, fast paths and lifecycle, without a game.
#define KILN_RULES_HOST_TEST
#include "rules.cpp"
#include "flight.cpp"

#include <cstdlib>

namespace kiln_rules_creative {
bool test_idle = false;
bool idle() { return test_idle; }
bool flame_creative() { return test_idle; }
}

// noclip.cpp is not part of this test (noclip_hook_test.cpp runs it with these hooks)
namespace kiln_rules_noclip {
int ticks = 0;
void tick() { ++ticks; }
}

namespace {
int failures = 0, entities = 0, flags = 0;
void check(bool ok, const char* why) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", why); failures += !ok; }
uint32_t* entity(void* p, uint32_t* out) { ++entities; *out = *static_cast<uint32_t*>(p); return out; }
bool actor_flag(void* p, uint8_t flag) { ++flags; return static_cast<bool*>(p)[flag]; }
uint32_t session = KILN_SESSION_REMOTE, local = 3;
KilnResult KILN_CALL client_state(KilnClientState* s) { s->local_player = local; s->session = session; return KILN_OK; }
const void* KILN_CALL service(KilnStr, uint32_t) { return nullptr; }
bool KILN_CALL feature(KilnStr) { return false; }
bool KILN_CALL have_feature(KilnStr) { return true; }
KilnResult KILN_CALL symbol(KilnStr, uintptr_t* out) { *out = 0x1000; return KILN_OK; }
KilnResult KILN_CALL constant(KilnStr, int64_t* out) { *out = 8; return KILN_OK; }
void set_rule(uint32_t e, const KilnRulesPlayer* rule) {
    if (e <= kMaxPlayer) { store(g_players[e], rule); return; }
    store(g_extra[0], rule); g_extra_ids[0] = rule ? e : 0; g_extra_hint[0] = rule ? e : 0;
}
}

int main() {
    namespace f = kiln_rules_flight;
    f::g_entity_of = &entity;
    f::g_actor_state = &actor_flag;
    f::L.loco_actor = 16; f::L.loco_dynloco = 8; f::L.loco_state = 0x59; f::L.grav_actor = 16;
    f::L.mover_dynloco = 8; f::L.player_input = 16; f::L.input_mask = 0;
    alignas(16) uint8_t dyn[128]{};
    bool actor[64]{};
    void* frame[4] = {nullptr, dyn, actor, nullptr};
    uint32_t e = 3;
    safetyhook::Context c{};
    c.rbp = c.rsp = reinterpret_cast<uintptr_t>(frame); c.rsi = c.rdi = reinterpret_cast<uintptr_t>(&e);
    c.rax = flight_math::kFalling;
    f::state_body(c);
    check(entities == 0 && flags == 0, "no flight rules: no entity lookup, locks or actor tests");
    auto rule = kDefault; rule.flags = KILN_RULES_CHARACTER_FLIGHT;
    set_rule(e, &rule);
    e = 5000;
    f::state_body(c);
    check(flags == 0 && c.rax == flight_math::kFalling, "unrelated actors never read flags or acquire flight");
    e = 3;
    f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying, "the rule's player takes off through the production hook");
    c.xmm6.f32[0] = 9;
    f::grav_body(c);
    check(c.xmm6.f32[0] == 0, "only the authorized player's gravity is waived");
    e = 4; c.xmm6.f32[0] = 9;
    f::grav_body(c);
    check(c.xmm6.f32[0] == 9, "another player's gravity stays intact");
    e = 3;
    uint64_t input[2] = {0, uint64_t{1} << 32};
    void* it[3] = {&e, dyn, input};
    f::mover_pre(reinterpret_cast<uint8_t*>(it));
    check(reinterpret_cast<float*>(dyn)[1] == flight_math::kClimb, "replicated held jump drives vertical flight");
    dyn[0x59] = flight_math::kBuildingFlying;
    input[1] = uint64_t{1} << 55;
    f::mover_pre(reinterpret_cast<uint8_t*>(it));
    actor[0] = true; c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    check(c.rax == flight_math::kWalking, "sinking while grounded lands through the production hook");
    actor[0] = false;
    dyn[0x59] = flight_math::kFalling; c.rax = flight_math::kFalling;
    f::state_body(c); dyn[0x59] = flight_math::kBuildingFlying;
    set_rule(3, nullptr); c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    check(c.rax == flight_math::kFalling && !(kiln_rules_bridge::flags_seen() & KILN_RULES_CHARACTER_FLIGHT),
          "revoking the last flight rule falls without a wall-clock expiry and clears the fast hint");
    e = 5000; c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying, "a building spark is untouched");
    // an entity WITH the rule, in a BuildingFlying state Kiln did not start: left alone too, and not taken over
    e = 6; set_rule(6, &rule); dyn[0x59] = flight_math::kBuildingFlying; c.rax = flight_math::kBuildingFlying;
    actor[0] = true; input[1] = uint64_t{1} << 55; f::mover_pre(reinterpret_cast<uint8_t*>(it));  // grounded and sinking: Kiln's own flight would land
    f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying && !f::flown_recently(6, dyn), "a flight Kiln did not start is not landed, ended or adopted, rule or not");
    set_rule(6, nullptr); actor[0] = false; e = 5000;
    set_rule(e, &rule); f::note_flying(e, dyn); set_rule(e, nullptr);
    f::state_body(c);
    check(c.rax == flight_math::kFalling, "a simulated character falls after revocation too");
    uint8_t other_dyn[128]{};
    check(!f::flown_recently(e, other_dyn), "a reused non-player id does not borrow an old object's flight ownership");
    f::reset();
    check(!f::flight_possible() && !f::flown_recently(e, dyn), "world reset forgets flight ownership and restores the fast path");
    // a world that starts while a flight runs in another one (a host's prediction beside its server world)
    set_rule(e, &rule); f::note_flying(e, dyn); f::note_flying(3, dyn);
    f::world(KILN_WORLD_STARTED, true);
    check(f::flown_recently(e, dyn) && f::flown_recently(3, dyn) && f::flight_possible(),
          "a world that starts leaves the flights of a running world Kiln's");
    dyn[0x59] = flight_math::kBuildingFlying; c.rax = flight_math::kBuildingFlying; set_rule(e, nullptr);
    f::state_body(c);
    check(c.rax == flight_math::kFalling, "and such a flight still ends when its rule goes");
    f::note_flying(e, dyn); f::note_flying(3, dyn);
    f::world(KILN_WORLD_STOPPING, true);
    check(!f::flown_recently(e, dyn) && !f::flown_recently(3, dyn) && !f::flight_possible(), "a world that stops forgets flight ownership");
    // where the core does not see world stops, a start may replace a world nobody saw stop: everything is forgotten
    f::note_flying(e, dyn); f::note_flying(3, dyn);
    f::world(KILN_WORLD_STARTED, false);
    check(!f::flown_recently(e, dyn) && !f::flown_recently(3, dyn) && !f::flight_possible(),
          "without seen stops, a world that starts forgets flight ownership (no old flight meets a new character)");
    dyn[0x59] = flight_math::kFalling; c.rax = flight_math::kFalling;

    KilnApi api{}; api.service = &service; api.feature = &feature;
    KilnClientApi client{}; client.state = &client_state;
    f::g_api = &api; f::g_client = &client;
    store(g_local, &rule); e = 3; c.rax = flight_math::kFalling; dyn[0x59] = flight_math::kFalling;
    f::refresh(); f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying, "a joined client's local player predicts its flight");
    const int ticks = kiln_rules_noclip::ticks;
    f::client_pre(nullptr, nullptr);
    check(kiln_rules_noclip::ticks == ticks + 1, "a joined client's tick ages NoClip's activity");
    session = KILN_SESSION_HOST; f::refresh(); c.rax = flight_math::kFalling;
    f::state_body(c);
    check(c.rax == flight_math::kFalling, "a host never regrants revoked server flight from stale local rules");
    const int host_ticks = kiln_rules_noclip::ticks;
    f::client_pre(nullptr, nullptr);
    check(kiln_rules_noclip::ticks == host_ticks, "a host's client tick does not age it (its server tick does)");
    session = KILN_SESSION_REMOTE; f::refresh();
    check(f::g_local_player != 0, "a joined client caches its local player");
    f::world(KILN_WORLD_STARTED, true);
    check(f::g_local_player == 0, "a world that starts drops the cached local identity immediately");
    f::refresh(); f::reset();
    check(f::g_local_player == 0, "world reset drops the cached local identity immediately");
    set_rule(e, &rule);
    kiln_rules_creative::test_idle = true;
    const auto before = entities;
    f::state_body(c); f::grav_body(c); f::mover_pre(reinterpret_cast<uint8_t*>(it));
    check(entities == before, "late Creative Mode leaves all three flight hooks idle before entity lookup");
    f::note_flying(e, dyn); dyn[0x59] = flight_math::kBuildingFlying; c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    check(c.rax == flight_math::kFalling, "late Creative Mode ends an existing Kiln flight before handing movement over");
    c.rax = flight_math::kBuildingFlying;
    f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying, "after handing over, a third-party flight is left alone");
    kiln_rules_creative::test_idle = false;
    std::string off;
    check(f::install(&api, 0, off) == 0 && off.find("rules.flight") != std::string::npos,
          "a missing flight feature disables flight alone without changing other rule snapshots");
    api.feature = &have_feature; api.symbol = &symbol; api.constant = &constant;
    kiln::test::fail_all = true;
    check(f::install(&api, 0, off) == 0 && !f::h_state && !f::h_grav && !f::h_mover,
          "a failed mover hook removes both state and gravity hooks too");
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
