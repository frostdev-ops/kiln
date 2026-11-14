// SPDX-License-Identifier: MIT
// kiln.creature_control: the layout guard. Every field the broker reads or writes in a body is resolved by name through
// kiln.types (the game's own reflection, base classes walked) and compared with the layout the contract was written
// against (19d §3, research evidence out/pets/evidence/broker-{types,trigger}-*.json, identical on server 26430253 and
// client 26430625). A path that does not resolve, or resolves elsewhere, lowers creature_control: the broker never
// guesses a nearby field. The footprint paths (pets' placement) are checked and reported without lowering it.
// tests/guard_dump_test.py checks this table against the committed type dumps (sigdb/types/).
#pragma once

#include <cstdint>
#include <string>

#include "services/kiln_types.h"

namespace kcc {

enum class GuardUse : uint8_t {
    Broker = 0,     // the broker reads or writes it: a mismatch lowers creature_control
    Footprint = 1,  // pets' placement footprint: reported, does not lower creature_control
};

struct GuardPath {
    const char* type;    // qualified name of the root type
    const char* path;    // dotted field path ("" = the type's own size; "::Name" = an enum value)
    uint32_t expect;     // offset from the root (enum: the value; size check: the size)
    uint32_t size;       // the leaf's size (0 for enum values and size checks)
    GuardUse use;
};

// guard-table-begin (one entry per line: tests/guard_dump_test.py reads these lines)
inline constexpr GuardPath kGuardPaths[] = {
    {"keen::ecs::SteeringAgent", "", 80, 0, GuardUse::Broker},
    {"keen::ecs::SteeringAgent", "queryId", 0x0, 4, GuardUse::Broker},
    {"keen::ecs::SteeringAgent", "desiredVelocity", 0x8, 12, GuardUse::Broker},
    {"keen::ecs::SteeringAgent", "velocity", 0x1c, 12, GuardUse::Broker},
    {"keen::ecs::ActorInput", "", 456, 0, GuardUse::Broker},
    {"keen::ecs::ActorInput", "triggerContext.sequenceRuntimeId.sequenceId", 0x8, 4, GuardUse::Broker},
    {"keen::ecs::ActorInput", "desiredLocalMoveInput", 0x18c, 12, GuardUse::Broker},
    {"keen::ecs::ActorInput", "desiredWorldMoveInput", 0x198, 12, GuardUse::Broker},
    {"keen::ecs::ActorInput", "desiredWorldLookDirection", 0x1b8, 12, GuardUse::Broker},
    {"keen::ecs::ActorInput", "locomotionSpeed", 0x1c4, 1, GuardUse::Broker},
    {"keen::ecs::Actor", "currentAction.playbackContext.state", 0x1b1, 1, GuardUse::Broker},
    {"keen::ecs::Actor", "currentState", 0xbf8, 8, GuardUse::Broker},
    {"keen::actor::ActionSequenceFlags", "::IsActive", 0, 0, GuardUse::Broker},
    {"keen::actor::StateFlag", "::IsReadyForGameplay", 50, 0, GuardUse::Broker},
    {"keen::actor::StateFlag", "::Dead", 7, 0, GuardUse::Broker},
    {"keen::ecs::MovementColliderConfig", "width", 0x0, 4, GuardUse::Footprint},
    {"keen::ecs::MovementColliderConfig", "height", 0x4, 4, GuardUse::Footprint},
    {"keen::ecs::PathWalker", "horizontalRadius", 0x38, 4, GuardUse::Footprint},
    {"keen::ecs::PathWalker", "height", 0x3c, 4, GuardUse::Footprint},
};
// guard-table-end

// What the broker uses, as the guard resolved it (equal to the table when `broker_ok`).
struct Layout {
    uint32_t steering_size = 80, steering_query_id = 0, steering_desired_velocity = 8, steering_velocity = 0x1c;
    uint32_t input_size = 456, input_trigger_sequence = 8, input_trigger_sequence_size = 4;
    uint32_t input_local_move = 0x18c, input_world_move = 0x198, input_look = 0x1b8, input_speed = 0x1c4;
    uint32_t actor_play_state = 0x1b1, actor_state = 0xbf8;
    uint32_t bit_active = 0, bit_ready = 50, bit_dead = 7;
};

struct GuardResult {
    bool broker_ok = false;     // every Broker path matched
    bool footprint_ok = false;  // every Footprint path matched
    uint32_t matched = 0, total = 0;
    std::string line;           // every path with "ok" or what was found, for kiln.log
    std::string why;            // the first Broker mismatch, empty when broker_ok
};

// Runs the guard against kiln.types (any thread; the type table is immutable). types NULL: nothing matches.
GuardResult run_guard(const KilnTypesApi* types, Layout* out);

}  // namespace kcc
