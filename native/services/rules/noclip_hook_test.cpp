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
// The server world as the server tick reads it: each entity's DynamicLocomotion (none: 0).
std::unordered_map<uint32_t, uintptr_t> server_world;
uintptr_t server_record(uint32_t e) {
    const auto it = server_world.find(e);
    return it == server_world.end() ? 0 : it->second;
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

    // ---- the game moves the character's records. Components live by archetype: adding one or taking one away moves all
    // of an entity's components to another place, and every teleport adds the Teleport component and takes it away the
    // step after (docs/re/creatures.md 1.4). The record is copied byte for byte; another entity's may take the old place.
    // Each case starts with no flight and at addresses Kiln has never seen.
    alignas(16) uint8_t rec[12][128]{};
    auto use = [&](uint8_t* r) {
        stack[1] = r;
        put(kDynloco, r);
    };
    auto move_to = [&](uint8_t* from, uint8_t* to) {
        std::memcpy(to, from, 128);
        std::memset(from, 0, 128);
        use(to);
    };
    auto takeoff = [&] {
        actor[0] = true; c.rax = flight_math::kWalking; f::state_body(c);
        reinterpret_cast<uint8_t*>(stack[1])[0x59] = static_cast<uint8_t>(c.rax);
        actor[0] = false;
    };
    auto clean = [&] { f::reset(); n::reset(); kiln_rules_creative::test_idle = false; };
    e = 3; rule.flags = KILN_RULES_NOCLIP;

    // a teleport while NoClip is on, which then ends it
    clean(); set_rule(3, &rule); use(rec[0]);
    takeoff();
    step();
    check(rec[0][0x59] == flight_math::kBuildingFlying && seen_enabled == 0, "moved records: NoClip flies without collision first");
    move_to(rec[0], rec[1]);
    c.rax = flight_math::kBuildingFlying;  // LocoNextState keeps BuildingFlying first of all
    f::state_body(c);
    step();
    check(c.rax == flight_math::kBuildingFlying && seen_enabled == 0 && f::flown_recently(3, rec[1]),
          "after a teleport moved its records, the character is still in Kiln's flight, without collision while NoClip is on");
    move_to(rec[1], rec[2]);  // the Teleport component taken away again: another move
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    set_rule(3, nullptr);  // the teleport ends NoClip
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    check(c.rax == flight_math::kFalling, "NoClip ended by the teleport: the moved character falls (walking and animation are the game's again)");
    rec[2][0x59] = flight_math::kFalling;
    step();
    check(seen_enabled == 1 && !f::flown_recently(3, rec[2]) && !f::flown_recently(3, rec[1]) && !f::flown_recently(3, rec[0]),
          "with collision, and nothing of the flight is left");

    // the move and the end in one step: the records moved and the rules no longer carry NoClip when the state hook runs
    clean(); set_rule(3, &rule); use(rec[3]);
    takeoff();
    move_to(rec[3], rec[4]);
    set_rule(3, nullptr);
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    check(c.rax == flight_math::kFalling, "records moved in the step that ended NoClip: it falls too");
    rec[4][0x59] = flight_math::kFalling;
    // a flight that is over is not Kiln's to take again: a BuildingFlying record of that player that turns up later
    // (another mod's flight) is left alone
    std::memcpy(rec[5], rec[4], 128); rec[5][0x59] = flight_math::kBuildingFlying; use(rec[5]);
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    step();
    check(c.rax == flight_math::kBuildingFlying && seen_enabled == 1 && !f::flown_recently(3, rec[5]),
          "after Kiln's flight ended, another BuildingFlying record of the player is not adopted");

    // A host: the server world's record and the prediction's. The server tick, before each server step, reads the server
    // world's record of the player (kiln.world); that is how the hooks tell the two worlds apart.
    auto tick = [&] { f::server_tick(&server_record); };
    auto at = [](uint8_t* r) { return reinterpret_cast<uintptr_t>(r); };
    uint8_t* pre = rec[6];
    void* stack2[4] = {nullptr, pre, actor, nullptr};
    safetyhook::Context c2{};
    c2.rbp = c2.rsp = reinterpret_cast<uintptr_t>(stack2); c2.rsi = c2.rdi = reinterpret_cast<uintptr_t>(&e);
    auto both = [&](int steps) {
        for (int i = 0; i < steps; ++i) {
            tick();
            c.rax = flight_math::kBuildingFlying; f::state_body(c);
            c2.rax = flight_math::kBuildingFlying; f::state_body(c2);
        }
    };
    // both records take off; the first step waits for the server tick to tell the worlds apart
    auto host_takeoff = [&](uint8_t* srv) {
        std::memset(srv, 0, 128); std::memset(pre, 0, 128);
        use(srv); server_world[3] = at(srv);
        tick();  // a server step before the rule (the server world runs)
        actor[0] = true;
        c.rax = flight_math::kWalking; f::state_body(c);
        const bool waited = c.rax == flight_math::kWalking;
        tick();
        c.rax = flight_math::kWalking; f::state_body(c); srv[0x59] = static_cast<uint8_t>(c.rax);
        c2.rax = flight_math::kWalking; f::state_body(c2); pre[0x59] = static_cast<uint8_t>(c2.rax);
        actor[0] = false;
        return waited;
    };
    {
        clean(); set_rule(3, &rule); server_world.clear();
        const bool waited = host_takeoff(rec[7]);
        check(waited && rec[7][0x59] == flight_math::kBuildingFlying && pre[0x59] == flight_math::kBuildingFlying,
              "a host: a take-off waits one server step until the worlds are told apart, then both records fly");
        both(3);
        check(f::flown_recently(3, rec[7]) && f::flown_recently(3, pre), "a host: the server's and the prediction's records both fly");
        move_to(rec[7], rec[8]); server_world[3] = at(rec[8]);
        both(3);
        check(f::flown_recently(3, rec[8]) && f::flown_recently(3, pre) && !f::flown_recently(3, rec[7]),
              "the server's record moved: it is still Kiln's, and the prediction's record stays Kiln's beside it");
        // the prediction pauses while the server's record moves on twice, then resumes
        move_to(rec[8], rec[9]); server_world[3] = at(rec[9]);
        for (int i = 0; i < 3; ++i) { tick(); c.rax = flight_math::kBuildingFlying; f::state_body(c); }
        move_to(rec[9], rec[10]); server_world[3] = at(rec[10]);
        for (int i = 0; i < 3; ++i) { tick(); c.rax = flight_math::kBuildingFlying; f::state_body(c); }
        both(2);
        check(f::flown_recently(3, rec[10]) && f::flown_recently(3, pre), "a paused prediction that resumes is still Kiln's");
        set_rule(3, nullptr);
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        c2.rax = flight_math::kBuildingFlying; f::state_body(c2);
        check(c.rax == flight_math::kFalling && c2.rax == flight_math::kFalling && !f::flown_recently(3, rec[10]) &&
              !f::flown_recently(3, pre) && !f::flown_recently(3, rec[8]) && !f::flown_recently(3, rec[9]),
              "NoClip ends: both records fall, and nothing of the flight is left");
    }
    // One world's flight never vouches for a record in the other. The prediction pauses, the rule ends and the server's
    // character leaves the flight; then another mod flies the server's character.
    {
        clean(); set_rule(3, &rule); server_world.clear();
        host_takeoff(rec[7]);
        both(2);
        set_rule(3, nullptr);
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c); rec[7][0x59] = static_cast<uint8_t>(c.rax);
        rec[7][0x59] = flight_math::kBuildingFlying;  // another mod's flight
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        check(c.rax == flight_math::kBuildingFlying && !f::flown_recently(3, rec[7]) && f::flown_recently(3, pre),
              "a paused prediction's flight does not make another mod's flight of the server's character Kiln's");
        c2.rax = flight_math::kBuildingFlying; f::state_body(c2);
        check(c2.rax == flight_math::kFalling && !f::flown_recently(3, pre),
              "and when the prediction resumes, its own flight is still Kiln's and ends: it falls, not stuck flying");
    }
    // ...and the other way round: the prediction leaves the flight first, another mod flies its character
    {
        clean(); set_rule(3, &rule); server_world.clear();
        host_takeoff(rec[7]);
        both(2);
        set_rule(3, nullptr);
        c2.rax = flight_math::kBuildingFlying; f::state_body(c2);
        const bool fell = c2.rax == flight_math::kFalling;
        pre[0x59] = flight_math::kBuildingFlying;  // another mod's flight
        c2.rax = flight_math::kBuildingFlying; f::state_body(c2);
        check(fell && c2.rax == flight_math::kBuildingFlying && !f::flown_recently(3, pre),
              "the server's flight does not make another mod's flight of the prediction's character Kiln's");
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        check(c.rax == flight_math::kFalling && !f::flown_recently(3, rec[7]), "and the server's own flight still ends");
    }
    // Ownership lives as long as the character. A player flies on a dedicated server (the server tick reads its record)...
    auto server_takeoff = [&](uint32_t who, uint8_t* r) {
        e = who; std::memset(r, 0, 128); use(r); server_world[who] = at(r);
        tick();  // a server step before the rule (the server world runs)
        actor[0] = true; c.rax = flight_math::kWalking; f::state_body(c); actor[0] = false;
        tick();
        takeoff();
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        return r[0x59] == flight_math::kBuildingFlying && f::flown_recently(who, r);
    };
    {
        // ...and disconnects while flying, before a hook saw the flight end: its rules go, then its character
        clean(); server_world.clear(); set_rule(7, &rule);
        const bool flew = server_takeoff(7, rec[0]);
        set_rule(7, nullptr);
        server_world.erase(7);
        tick();
        // a newcomer gets the id, and another mod flies its new character
        std::memset(rec[1], 0, 128); rec[1][0x59] = flight_math::kBuildingFlying; use(rec[1]); server_world[7] = at(rec[1]);
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        check(flew && c.rax == flight_math::kBuildingFlying && !f::flown_recently(7, rec[1]) && !f::flown_recently(7, rec[0]),
              "a departed player's flight goes with its character: another mod's flight of the newcomer with its id is left alone");
    }
    {
        // ...or its connection is replaced in one go (the server tick never sees the id empty): Kiln.Rules' Leave says so
        clean(); server_world.clear(); set_rule(8, &rule);
        const bool flew = server_takeoff(8, rec[2]);
        set_rule(8, nullptr);
        f::retire(8);  // KilnRulesApi.player_left
        std::memset(rec[3], 0, 128); rec[3][0x59] = flight_math::kBuildingFlying; use(rec[3]); server_world[8] = at(rec[3]);
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        check(flew && c.rax == flight_math::kBuildingFlying && !f::flown_recently(8, rec[3]),
              "a player whose connection was replaced leaves no flight: the newcomer's flight by another mod is left alone");
    }
    {
        // the rule ending alone (the player stays) keeps the flight Kiln's until it has ended, also when a teleport
        // moves the record in the same step
        clean(); server_world.clear(); set_rule(9, &rule);
        const bool flew = server_takeoff(9, rec[4]);
        set_rule(9, nullptr);
        move_to(rec[4], rec[5]); server_world[9] = at(rec[5]);
        tick();
        c.rax = flight_math::kBuildingFlying; f::state_body(c);
        check(flew && c.rax == flight_math::kFalling && !f::flown_recently(9, rec[5]),
              "a rule that ends while the player stays: Kiln still owns the moved flight and ends it");
    }
    e = 3; server_world.clear(); use(dyn);

    // a lab's simulated character (an extra id) follows its record the same way
    clean(); e = 5000; set_rule(5000, &rule); std::memset(rec[0], 0, 128); use(rec[0]);
    takeoff();
    move_to(rec[0], rec[11]);
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    step();
    check(c.rax == flight_math::kBuildingFlying && seen_enabled == 0 && f::flown_recently(5000, rec[11]),
          "a simulated character's moved record stays Kiln's flight");
    set_rule(5000, nullptr);
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    check(c.rax == flight_math::kFalling, "and falls when its NoClip ends");
    e = 3;

    // while Creative Mode runs, Kiln adopts nothing: that mod's flight is its own
    clean(); set_rule(3, &rule); std::memset(rec[1], 0, 128); use(rec[1]);
    takeoff();
    kiln_rules_creative::test_idle = true;
    move_to(rec[1], rec[2]);
    c.rax = flight_math::kBuildingFlying; f::state_body(c);
    check(c.rax == flight_math::kBuildingFlying && !f::flown_recently(3, rec[2]), "while Creative Mode runs, a moved record is not taken");
    set_rule(3, nullptr); clean(); use(dyn); dyn[0x59] = flight_math::kFalling;

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
