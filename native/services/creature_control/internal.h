// SPDX-License-Identifier: MIT
// kiln.creature_control: what the parts of kiln-creature-control.dll share (the broker, its world program, the service
// glue). Private to this DLL target; 19d-pets' native sources link into the same target and include it.
#pragma once

#include <atomic>
#include <cstdint>
#include <string>
#include <vector>

#include "kiln_api.h"
#include "registry.h"
#include "services/kiln_world.h"
#include "types_guard.h"

namespace kcc {

extern const KilnApi* g_api;
extern KilnOwner g_owner;
extern Broker* g_broker;  // process lifetime
extern Layout g_layout;   // as the types guard resolved it

void logf(int32_t level, const char* fmt, ...);
bool on_tick();

// program.cpp: the broker's serial world program (SYSTEM, host only, other-write ActorInput and SteeringAgent).
// Registered once from on_ready; executes the ops each Pre hands it, in the step that follows.
bool program_register(const KilnWorldApi* world, std::string* why);
uint32_t program_state();             // KILN_PROGRAM_* (PENDING before the first server world registers its programs)
void program_publish(std::vector<Op>&& ops);  // replaces what the program has not run yet
struct ProgramCounters {
    uint64_t runs, zeroed, removed, added, failed;
};
ProgramCounters program_counters();

}  // namespace kcc
