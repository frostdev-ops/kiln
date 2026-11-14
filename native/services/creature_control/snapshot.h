// SPDX-License-Identifier: MIT
// kiln.creature_control: what the broker writes into a body's components, as plain byte edits (tested on the host).
//   - the SteeringAgent it puts back: the bytes it saved at the first lease, with the transient queryId (u32 +0) and both
//     velocity vectors (desiredVelocity float3 +8, velocity float3 +0x1c) zeroed, so the game never reuses a range query
//     handle or a movement vector from before a long ride; the game makes a new query within 20 steps (riding G4);
//   - ActorInput while a body changes hands: the movement it asks for (local and world move, speed) zeroed. The look
//     direction stays: a zero vector is no direction.
#pragma once

#include <cstdint>
#include <cstring>
#include <vector>

namespace kcc {

struct SteeringLayout {
    uint32_t size = 80;
    uint32_t query_id = 0x0;          // RangeQueryId, u32
    uint32_t desired_velocity = 0x8;  // float3
    uint32_t velocity = 0x1c;         // float3
};

struct InputLayout {
    uint32_t size = 456;
    uint32_t local_move = 0x18c;  // float3
    uint32_t world_move = 0x198;  // float3
    uint32_t speed = 0x1c4;       // LocomotionSpeedSelector, u8 (0 = Zero)
};

inline void zero_bytes(uint8_t* p, uint32_t size, uint32_t off, uint32_t n) {
    if (p && off <= size && n <= size - off) std::memset(p + off, 0, n);
}

// The SteeringAgent to put back.
inline std::vector<uint8_t> steering_restore_bytes(const std::vector<uint8_t>& saved, const SteeringLayout& l) {
    std::vector<uint8_t> v = saved;
    const uint32_t n = static_cast<uint32_t>(v.size());
    zero_bytes(v.data(), n, l.query_id, 4);
    zero_bytes(v.data(), n, l.desired_velocity, 12);
    zero_bytes(v.data(), n, l.velocity, 12);
    return v;
}

// Zeroes the movement an ActorInput asks for. False when the component is smaller than the layout says.
inline bool zero_input(uint8_t* input, uint32_t size, const InputLayout& l) {
    if (!input || size < l.size) return false;
    zero_bytes(input, size, l.local_move, 12);
    zero_bytes(input, size, l.world_move, 12);
    zero_bytes(input, size, l.speed, 1);
    return true;
}

}  // namespace kcc
