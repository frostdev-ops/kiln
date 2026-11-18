// SPDX-License-Identifier: MIT
// The hold-safe part of kiln.world's engine backend (ecs_engine.h): the entity table, the id lookup and a component's
// place in an entity's row, read through kiln.internal.engine's guarded copy only. Every function here also runs inside
// an engine write-gate hold (ecs_write_hold.cpp), so this file and ecs_lookup.cpp hold no wait, lock, allocation, log
// or KilnApi call, and every loop over engine data has a fixed bound (tests/lint_hold.py checks the files).
// Adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer): modules/game-runtime/native/ecs/
// read_entity_membership.cpp:116-201 and resolve_component_storage.cpp:4-38 (details in ecs_engine.h).
#pragma once
#include <cstddef>
#include <cstdint>

namespace kw::ecs::engine {

inline constexpr size_t kMaxProbe = 8192;     // ShroudForge's bound on the id probe (read_entity_membership.cpp:140)
inline constexpr uint32_t kMaxRow = 1u << 24; // ShroudForge's bound on an entity's row (read_entity_membership.cpp:194)
inline constexpr uint64_t kMaxProbeForWrites = kMaxProbe; // the same bound inside a write's hold

inline bool add(uintptr_t base, uint64_t offset, uintptr_t& out) {
    if (!base || offset > UINTPTR_MAX - base) return false;
    out = base + static_cast<uintptr_t>(offset);
    return true;
}

// The engine's entity map hash (docs/re/world.md; the map's find at server 0x650c50 / client equivalent).
inline uint32_t id_hash(uint32_t id) {
    uint32_t h = id;
    h = ((h >> 16) ^ h) * 0x45d9f3bu;
    h = ((h >> 16) ^ h) * 0x45d9f3bu;
    return (h >> 16) ^ h;
}

} // namespace kw::ecs::engine
