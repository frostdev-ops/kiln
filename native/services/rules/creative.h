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
}  // namespace kiln_rules_bridge

namespace kiln_rules_creative {
// Installs what this build has; `off` collects what is off (for status). Returns KILN_RULES_HOOK_* bits.
uint32_t install(const KilnApi* api, std::string& off);
// rules.cpp's payment hook asks this for every payment that is not the storage upgrade's: true = answer "paid".
bool pay_free(uintptr_t ret, void* tx, uint32_t owner);
// rules.cpp could not hook the payment: free crafting goes off (the second payer too).
void pay_hook_failed();
KilnResult KILN_CALL map_reveal(uint32_t mode, float radius);
KilnResult KILN_CALL creative_status(KilnRulesCreative* out);
}  // namespace kiln_rules_creative
