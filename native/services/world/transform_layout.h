// SPDX-License-Identifier: MIT
// keen::WorldTransform as an entity's CurrentTransform component holds it: i64 x, y, z in metres as 32.32 fixed point
// at 0, a quaternion (float4) at 0x18, a scale (float3) at 0x28. Portable, so the host test and Kiln.TestKit's fake
// world decode the same bytes the same way (tests/parity.json "transform"). Evidence: docs/re/world.md, entity access.
#pragma once
#include <cstdint>
#include <cstring>

#include "services/kiln_world.h"

namespace kw {

constexpr uint32_t kCurrentTransformHash = 0x027ac564u;  // FNV-1a("keen::ecs::CurrentTransform")
constexpr uint32_t kTransformMinSize = 52;               // the last scale float ends at 0x34

inline double fixed_to_metres(int64_t v) { return static_cast<double>(v) / 4294967296.0; }

// False when the component is too small to be a transform (out is left alone).
inline bool transform_decode(const uint8_t* p, uint32_t size, KilnTransform* out) {
    if (!p || size < kTransformMinSize || !out) return false;
    int64_t xyz[3];
    std::memcpy(xyz, p, sizeof xyz);
    out->position = {fixed_to_metres(xyz[0]), fixed_to_metres(xyz[1]), fixed_to_metres(xyz[2])};
    std::memcpy(out->rotation, p + 0x18, 16);
    std::memcpy(out->scale, p + 0x28, 12);
    out->reserved = 0;
    return true;
}

}  // namespace kw
