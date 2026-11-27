// SPDX-License-Identifier: MIT
// The 32-bit number of a registry entry that is an array element without an id member (regmap kind arrays: randomLoot's
// world tables). The registry stage's id table (regmap.cpp, model.cpp) and kiln.content's live registry (registries.cpp)
// both key such an entry by it, so they share this one definition; tools/registrygen/regmap_entries.py has its twin.
// FNV-1a 32 over the resource's 16 GUID bytes in the game's memory order, mixed with the element's key: the 32-bit id
// that names it (the scene node of the first chest that uses a world table), never its index, which a game update moves.
#pragma once
#include <cstdint>

namespace kc::registry {

inline uint32_t element_num(const uint8_t guid[16], uint32_t key) {
    uint32_t h = 2166136261u;
    for (int i = 0; i < 16; ++i) h = (h ^ guid[i]) * 16777619u;
    return h ^ ((key + 1u) * 0x9E3779B1u);
}

}  // namespace kc::registry
