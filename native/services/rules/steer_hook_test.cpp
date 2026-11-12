// SPDX-License-Identifier: MIT
// Execute the production glider-steering hook (steer.cpp) on a fake actor_rotation frame, with the real rule snapshots.
#define KILN_RULES_HOST_TEST
#include "rules.cpp"
#include "steer.cpp"

#include <cstdlib>
#include <string>

namespace {
int failures = 0, lookups = 0;
bool throw_lookup = false;
void check(bool ok, const char* why) { std::printf("%s %s\n", ok ? "PASS" : "FAIL", why); failures += !ok; }
uint32_t* entity(void* ctx, uint32_t* out) {
    ++lookups;
    if (throw_lookup) throw 1;  // a fault in the game's function (SEH on Windows)
    *out = *static_cast<uint32_t*>(ctx);
    return out;
}
uint32_t local = 0;
KilnResult KILN_CALL client_state(KilnClientState* s) { s->local_player = local; s->session = KILN_SESSION_REMOTE; return KILN_OK; }
KilnClientApi client{};

bool feature_on = true;
uintptr_t entity_capture = 0;
bool KILN_CALL feature(KilnStr n) { return feature_on && std::string(n.ptr, n.len) == "rules.glide_steer"; }
KilnResult KILN_CALL symbol(KilnStr n, uintptr_t* out) {
    const std::string s(n.ptr, n.len);
    if (s == "rules/GlideSteer") *out = 0x1000;
    else if (s == "rules/CurrentEntityId") *out = reinterpret_cast<uintptr_t>(&entity);
    else if (s == "rules/GlideSteerEntityId") *out = entity_capture;
    else return KILN_ERR_NOT_FOUND;
    return KILN_OK;
}
KilnResult KILN_CALL constant(KilnStr n, int64_t* out) {
    const std::string s(n.ptr, n.len);
    if (s == "rules/rot.ctx") *out = 0x40;
    else if (s == "rules/rot.actor_input") *out = 0xd8;  // rbp - 0x28 as the game's disp8
    else if (s == "rules/rot.player_input") *out = 0x10;
    else if (s == "rules/rot.move_z") *out = 0x194;
    else if (s == "rules/input.stick_mode") *out = 0x2c8;
    else return KILN_ERR_NOT_FOUND;
    return KILN_OK;
}
const void* KILN_CALL service(KilnStr n, uint32_t) {
    return std::string(n.ptr, n.len) == KILN_CLIENT_SERVICE ? &client : nullptr;
}

void set_rule(uint32_t e, const KilnRulesPlayer* rule) { store(g_players[e], rule); }
}  // namespace

int main() {
    namespace st = kiln_rules_steer;
    client.state = &client_state;
    KilnApi api{};
    api.service = &service;
    api.feature = &feature;
    api.symbol = &symbol;
    api.constant = &constant;

    // ---- install ----
    std::string off;
    feature_on = false;
    check(st::install(&api, 0, off) == 0 && !st::active() && off.find("rules.glide_steer") != std::string::npos,
          "without rules.glide_steer nothing is hooked and status says why");
    feature_on = true;
    off.clear();
    entity_capture = 0x5000;
    check(st::install(&api, 0, off) == 0 && !st::active() && off.find("glider steering") != std::string::npos,
          "a context slot passed to another function than CurrentEntityId: no hook");
    entity_capture = reinterpret_cast<uintptr_t>(&entity);
    off.clear();
    kiln::test::fail_all = true;
    check(st::install(&api, 0, off) == 0 && !st::active() && off.find("hook failed") != std::string::npos,
          "a failed hook leaves steering off");
    kiln::test::fail_all = false;
    {
        alignas(16) uint8_t no_hook_input[0x300]{};
        auto r = kDefault;
        r.flags = KILN_RULES_GLIDE_STEER | KILN_RULES_GLIDE_BOOST;
        check(!st::steers(r, no_hook_input), "without the hook the glide wrap has no steering form (GlideBoost flies as before)");
    }
    off.clear();
    check(st::install(&api, 0, off) == KILN_RULES_HOOK_GLIDE_STEER && st::active() && off.empty(),
          "the hook installs with the feature and the agreeing context check");

    // ---- a fake actor_rotation frame ----
    alignas(16) uint8_t frame[0x400]{};
    alignas(16) uint8_t actor_input[0x200]{}, player_input[0x300]{};
    uint8_t* rbp = frame + 0x100;
    uint32_t e = 3;
    void* ctx = &e;
    std::memcpy(rbp + 0x40, &ctx, sizeof ctx);
    void* ai = actor_input;
    void* pi = player_input;
    std::memcpy(rbp - 0x28, &ai, sizeof ai);
    std::memcpy(rbp + 0x10, &pi, sizeof pi);
    auto z = [&](float v) { std::memcpy(actor_input + 0x194, &v, sizeof v); };
    const float kD = 3.14159265f / 180.0f, max = 85 * kD / 60;
    safetyhook::Context c{};
    c.rbp = reinterpret_cast<uintptr_t>(rbp);
    auto step = [&](float game, float pitch) {
        c.xmm6.f32[0] = game, c.xmm14.f32[0] = pitch, c.xmm2.f32[0] = max;
        st::hk_steer(c);
        return c.xmm6.f32[0];
    };
    z(-1);
    check(step(0, 5 * kD) == 0 && lookups == 0, "no rule set has steering: no entity lookup, no lock, the game's step");

    auto rule = kDefault;
    rule.flags = KILN_RULES_GLIDE_STEER;
    rule.glide_steer_limit = 85;
    set_rule(3, &rule);
    check(std::fabs(step(0, 5 * kD) + max) < 1e-7f && st::steered() == 1,
          "the rule's player: S raises the nose at the game's max step (xmm6 written, counted)");
    z(0);
    check(step(max, -30 * kD) == 0, "the rule's player, centred above 5 degrees: the levelling step is dropped");
    z(1);
    check(step(max, -30 * kD) == max, "the rule's player, W: the game's step");
    z(-1);
    e = 4;
    check(step(0, 5 * kD) == 0, "another entity (no rules): the game's step");
    auto boost = kDefault;
    boost.flags = KILN_RULES_GLIDE_BOOST;
    set_rule(5, &boost);
    e = 5;
    check(step(0, 5 * kD) == 0, "an entity whose rules have no GLIDE_STEER: the game's step");
    e = 3;
    void* none = nullptr;
    std::memcpy(rbp + 0x10, &none, sizeof none);
    const int before = lookups;
    check(step(0, 5 * kD) == 0 && lookups == before, "a null PlayerInput (not a player): the game's step, no lookup");
    std::memcpy(rbp + 0x10, &pi, sizeof pi);
    std::memcpy(rbp - 0x28, &none, sizeof none);
    check(step(0, 5 * kD) == 0, "a null ActorInput: the game's step");
    std::memcpy(rbp - 0x28, &ai, sizeof ai);
    player_input[0x2c8] = 1;
    check(step(0.01f, 20 * kD) == 0.01f, "input mode 1: the game's step");
    player_input[0x2c8] = 0;
    check(std::isnan(step(NAN, 5 * kD)), "a non-finite game step is left as it is");
    c.xmm2.f32[0] = -max;
    c.xmm6.f32[0] = 0.02f, c.xmm14.f32[0] = 5 * kD;
    st::hk_steer(c);
    check(c.xmm6.f32[0] == 0.02f, "a negative max step (another mod owns the rate): the game's step");

    // the glide wrap's steering form (rules.cpp glide_pre asks steers): only where the pitch is steered, read from the
    // same replicated PlayerInput byte as the hook's
    {
        auto r = kDefault;
        r.flags = KILN_RULES_GLIDE_STEER | KILN_RULES_GLIDE_BOOST;
        player_input[0x2c8] = 0;
        check(st::steers(r, player_input), "input mode 0: the glide wrap takes its steering form");
        player_input[0x2c8] = 1;
        check(!st::steers(r, player_input),
              "input mode 1 (the pitch stays the game's): the glide wrap keeps the plane path and the altitude hold");
        player_input[0x2c8] = 2;
        check(!st::steers(r, player_input), "an unknown input mode: no steering form either");
        player_input[0x2c8] = 0;
        r.flags = KILN_RULES_GLIDE_BOOST;
        check(!st::steers(r, player_input), "no GLIDE_STEER: the glide wrap flies as before");
        r.flags |= KILN_RULES_GLIDE_STEER;
        check(!st::steers(r, nullptr), "no PlayerInput: no steering form");
        for (int m = 0; m < 256; ++m) {
            player_input[0x2c8] = static_cast<uint8_t>(m);
            set_rule(3, &rule);
            e = 3;
            z(-1);
            const bool hook_steers = step(0, 5 * kD) != 0;
            if (hook_steers != st::steers(r, player_input)) {
                check(false, "the glide wrap's steering form and the hook disagree on an input mode");
                break;
            }
            if (m == 255) check(true, "the glide wrap's steering form and the pitch hook agree for all 256 input modes");
        }
        player_input[0x2c8] = 0;
        set_rule(3, nullptr);
    }

    // a joined client: no per-entity rules, the local player's
    set_rule(3, nullptr);
    store(g_local, &rule);
    local = 7;
    e = 7;
    check(std::fabs(step(0, 5 * kD) + max) < 1e-7f, "a joined client's local player steers from the local rules");
    e = 8;
    check(step(0, 5 * kD) == 0, "a joined client: another player's glider keeps the game's step");
    local = 0;
    e = 7;
    check(step(0, 5 * kD) == 0, "no local player known: the game's step");
    store(g_local, nullptr);

    // a fault in a game function counts and the game's step stands
    set_rule(3, &rule);
    e = 3;
    throw_lookup = true;
    const uint64_t faults = st::faults();
    check(step(0, 5 * kD) == 0 && st::faults() == faults + 1, "a fault counts and the game's step stands");
    throw_lookup = false;
    rule.glide_steer_limit = 30;
    set_rule(3, &rule);
    check(std::fabs(-29.5f * kD + step(-max, -29.5f * kD) + 30 * kD) < 1e-6f, "the rule's limit is the player's (30 degrees)");
    set_rule(3, nullptr);
    std::printf("steer hook: %d failure(s)\n", failures);
    return failures ? EXIT_FAILURE : EXIT_SUCCESS;
}
