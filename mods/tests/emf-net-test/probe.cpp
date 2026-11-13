// SPDX-License-Identifier: MIT
// Player-free conformance of the current API table; remote delivery is a host-wire and client check.
#include "emf/emf_api.h"
namespace {
const EmfApi *api;
int pass = 0, fail = 0;
void check(bool yes, const char *what) {
    if (yes)
        ++pass;
    else
        ++fail;
    api->log("emf_net_probe", "%s %s", yes ? "PASS" : "FAIL", what);
}
void message(EmfWorld *, const EmfNetFrom *, const void *, uint32_t, void *) {}
void tick(EmfWorld *world, void *) {
    static bool done = false;
    if (done || api->world_kind(world) != EMF_WORLD_SERVER)
        return;
    done = true;
    check(api->net_peer(world, 7, "emf_net_probe/pick") == 0, "no unnegotiated peer");
    check(!api->net_to_server("emf_net_probe/pick", "x", 1),
          "dedicated process has no local player or welcomed server");
    check(!api->net_to_player(world, 7, "emf_net_probe/pick", "x", 1), "no send to an unnegotiated player");
    check(api->net_to_players(world, "emf_net_probe/pick", "x", 1) == 0, "broadcast reaches zero unnegotiated games");
    check(!api->net_to_server("emf_net_probe/pick", nullptr, 1), "null payload with size refused");
    // the core's own channel (the mod check on join): a mod cannot register in it, send on it or see a peer without one
    check(api->net_peer(world, 7, "emf/mods") == 0, "emf/mods: no peer before a game said hello");
    check(!api->net_to_player(world, 7, "emf/mods", "x", 1) && api->net_to_players(world, "emf/mods", "x", 1) == 0,
          "emf/mods: a mod cannot send on the core's channel");
    api->log("emf_net_probe", "EMF v40 lab probe done: %d PASS, %d FAIL", pass, fail);
}
} // namespace
extern "C" __declspec(dllexport) bool emf_mod_load(const EmfApi *table) {
    api = table;
    check(api && api->version == 40 && api->size == sizeof(EmfApi) && sizeof(EmfApi) == 1088, "official v40 table (1088 bytes)");
    check(api->net_channel && api->net_to_server && api->net_to_player && api->net_to_players && api->net_peer,
          "five callable net slots");
    EmfNetChannel c{};
    c.size = sizeof c;
    c.name = "emf_net_probe/pick";
    c.version = 1;
    c.flags = 3;
    c.on_server = message;
    c.on_player = message;
    check(api->net_channel(&c), "own channel registered");
    check(!api->net_channel(&c), "duplicate channel refused");
    c.name = "othermod/pick";
    check(!api->net_channel(&c), "another mod's namespace refused");
    c.name = "emf/mods";
    check(!api->net_channel(&c), "the core's namespace refused");
    check(!api->net_channel(nullptr), "null channel refused");
    api->on_tick(tick, nullptr);
    return fail == 0;
}
