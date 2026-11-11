// SPDX-License-Identifier: MIT
// kiln.rules' creative hooks (creative.cpp) and the few things they take from rules.cpp. Internal to the service.
#pragma once
#include <cstdint>
#include <string>

#include "kiln_api.h"
#include "services/kiln_rules.h"

// rules.cpp gives creative.cpp the rule snapshots and its log.
namespace kiln_rules_bridge {
bool rules_of(uint32_t entity, KilnRulesPlayer* out);  // the server world's per-entity rules (false: defaults)
bool local_rules(KilnRulesPlayer* out);                // the local player's rules (client)
void log(int level, const char* text);
// Lock-free hints for hooks that run for every actor: may `entity` have server rules (a player id, or an extra id as
// set), and published flags (character flight only while at least one snapshot carries it).
bool may_have_rules(uint32_t entity);
uint32_t flags_seen();
}  // namespace kiln_rules_bridge

namespace kiln_rules_creative {
// True when Creative Mode (the Flame mod "creative") runs in this process (kiln.flame; logged once, stays true).
bool flame_creative();
bool idle();  // lock-free cached result; refreshed at tick boundaries
// Installs what this build has, nothing while Creative Mode runs; `off` collects what is off (for status). Returns
// KILN_RULES_HOOK_* bits. Called at the first world (kiln.flame answers only after every plugin's on_ready).
uint32_t install(const KilnApi* api, KilnOwner owner, std::string& off);
// rules.cpp's payment hook asks this for every payment that is not the storage upgrade's: true = answer "paid".
bool pay_free(uintptr_t ret, void* tx, uint32_t owner);
// rules.cpp could not hook the payment: free crafting goes off (the second payer too).
void pay_hook_failed();
KilnResult KILN_CALL map_reveal(uint32_t mode, float radius);
void reset_map();  // client world lifecycle; object addresses may be reused by the next world
KilnResult KILN_CALL creative_status(KilnRulesCreative* out);
}  // namespace kiln_rules_creative

namespace kiln_rules_flight {
void reset();  // world lifecycle: forget local identity and flight ownership before ids/objects can be reused
void refresh();
// Character flight's three hooks (flight.cpp; feature rules.flight), nothing while Creative Mode runs. Returns
// KILN_RULES_HOOK_FLIGHT or 0 (`off` says why). Called with the creative hooks, at the first world.
uint32_t install(const KilnApi* api, KilnOwner owner, std::string& off);
void counters(uint64_t* takeoffs, uint64_t* landings, uint64_t* ends, uint64_t* faults);
}  // namespace kiln_rules_flight

namespace kiln_rules_steer {
// Glider steering's mid hook (steer.cpp; feature rules.glide_steer). Returns KILN_RULES_HOOK_GLIDE_STEER or 0 (`off`
// says why). Called at boot with the other gliding hooks.
uint32_t install(const KilnApi* api, KilnOwner owner, std::string& off);
bool active();  // the hook is installed: GLIDE_STEER steers, and GLIDE_BOOST takes its steering form (rules.cpp)
uint64_t steered();
uint64_t faults();
}  // namespace kiln_rules_steer
