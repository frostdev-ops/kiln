// SPDX-License-Identifier: MIT
// kiln.item_costs (since 0.2.0-dev.32, kiln_item_costs.h): exact costs from a selected slot with receipts. The table and
// its checks are costs.cpp (logic: costs_logic.h); the costs run inside kiln_items_gear (gear.cpp's run_costs), which
// has the game's inventory transaction and the tracked Durability write in one serial program.
#pragma once
#include <cstdint>
#include <vector>

#include "costs_logic.h"
#include "kiln_api.h"

namespace kitems::costs {

// kiln_plugin_main: the table is published (also where costs are off: status says why).
void publish(const KilnApi* api, KilnOwner owner);
// Tick thread, Pre (after gear_pre): what did not run ends, unacknowledged receipts expire, settled ones are logged.
void pre(uint64_t step);
// Any thread, lock-free: costs wait for the program.
bool pending();
// The program (kiln_items_gear's update): up to `max` queued costs, oldest first; each must be settled.
std::vector<Job> take(size_t max);
void settle(const KilnCostResult& result);

}  // namespace kitems::costs

namespace kitems {
// gear.cpp: whether kiln_items_gear can run stack costs (its transaction) and gear costs (the durability debit's
// facts and the Durability accessor) now; why not in `why` (160 bytes). The step of the program's last update.
bool gear_costs_stacks(char* why);
bool gear_costs_wear(char* why);
uint64_t gear_last_update_step();
bool gear_switched_off(char* why);
}  // namespace kitems
