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
// set), and published flags (character flight and NoClip only while at least one snapshot carries them).
bool may_have_rules(uint32_t entity);
uint32_t flags_seen();
// The index of `entity`'s extra slot (a player character that is not a player: a lab's), or -1. Lock-free.
int extra_slot(uint32_t entity);
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
// For noclip.cpp, on a program thread: `dyn` (an entity's DynamicLocomotion) is in a flight Kiln started, and the rules
// of `ctx`'s entity carry NoClip now (the server world's per-entity rules, or the local player's in a joined client's
// prediction). Fills `entity`. False while Creative Mode runs and before flight's hooks are installed.
bool noclip_flight(void* ctx, const uint8_t* dyn, uint32_t* entity);
}  // namespace kiln_rules_flight

namespace kiln_rules_noclip {
// NoClip's hook on the character's collision step (noclip.cpp; feature rules.noclip; docs/re/rules.md section 8).
// Installed only with character flight's hooks (`flight`: they are in): a character without collision that cannot fly
// falls through the world. Returns KILN_RULES_HOOK_NOCLIP or 0 (`off` says why).
uint32_t install(const KilnApi* api, KilnOwner owner, bool flight, std::string& off);
void reset();  // world lifecycle: entity ids start over
void tick();   // once per tick of the side that simulates (the server's; a joined game's client tick): ages "active"
KilnResult KILN_CALL state(KilnEntity entity, KilnRulesNoClip* out);
}  // namespace kiln_rules_noclip

namespace kiln_rules_steer {
// Glider steering's mid hook (steer.cpp; feature rules.glide_steer). Returns KILN_RULES_HOOK_GLIDE_STEER or 0 (`off`
// says why). Called at boot with the other gliding hooks.
uint32_t install(const KilnApi* api, KilnOwner owner, std::string& off);
bool active();  // the hook is installed: GLIDE_STEER steers
// The glide wrap's steering form (rules.cpp glide_pre: GLIDE_BOOST along the nose, no altitude hold at sink 0) for a
// player's rules and PlayerInput: GLIDE_STEER, the hook installed, and the input mode the hook steers (0).
bool steers(const KilnRulesPlayer& r, const uint8_t* player_input);
uint64_t steered();
uint64_t faults();
}  // namespace kiln_rules_steer
