// SPDX-License-Identifier: MIT
// What kiln.world's compare-exchange writes may write (ecs_write_policy and the first step of ecs_compare_exchange,
// kiln_world.h): the rules as pure functions over facts, so the host tests drive every rule without an engine.
//   1. the server world (the engine's write gate is the local authority's);
//   2. an exact engine profile whose write gate is enabled, and a server world now;
//   3. a registered component whose two change-tracking bits were read from the engine and are both clear (the
//      engine's own "untracked" test, kiln.internal.engine's untracked_component; checked again inside every hold);
//   4. plain bytes all the way down (classify_plain over kiln.types' reflection);
//   5. the reflected size is the registered size, and 0 < size <= the profile's write budget.
// Nothing is allowed by name.
#pragma once
#include <cstdint>

#include "services/kiln_types.h"
#include "services/kiln_world.h"

namespace kw::ecs::writes {

enum class Plain : uint32_t { Yes, No, Unknown };

// Walks the component type of `hash` through kiln.types (type_by_hash, type_info, type_field): every base class,
// field, typedef and static-array element. Plain: only BOOL, the integer kinds, F32, F64, ENUM, the BITMASK kinds,
// GUID, and STRUCT, STATIC_ARRAY and TYPEDEF of those. No: a DS_* kind (owns heap memory), a BLOB_* kind (offsets into
// blob data), OBJECT_REFERENCE (a pointer), or a kind kiln.types does not name. Unknown: no type under the hash, a
// type or field kiln.types cannot describe, or a walk beyond its fixed bounds (kMaxNodes types, kMaxDepth levels).
// *reflected_size: the component type's size (0 when unknown). *why: a static sentence for No and Unknown.
inline constexpr uint32_t kMaxNodes = 4096, kMaxDepth = 32;
Plain classify_plain(const KilnTypesApi* types, uint32_t hash, uint32_t* reflected_size, const char** why);

struct PolicyFacts {
    uint32_t world_kind = 0;
    bool exact_profile = false;    // an exact engine profile matched this build
    bool gate_enabled = false;     // its write gate is enabled (layout.write.enabled) and kiln.internal.engine has it
    bool world = false;            // a server world runs now
    bool registered = false;       // a component is registered under the hash
    uint32_t registered_size = 0;  // its registration's size
    bool tracking_known = false;   // the two tracking bits were read from the engine
    bool tracked_a = false, tracked_b = false;
    Plain plain = Plain::Unknown;
    uint32_t reflected_size = 0;
    const char* plain_why = nullptr;
    uint32_t budget = 0;           // the profile's per-component write budget (layout.write.maximum_bytes)
};

// Fills `out` (its size kept) from the facts; returns KILN_OK, or KILN_ERR_UNAVAILABLE for rules 1 and 2 (status
// UNAVAILABLE). Writable: status KILN_ECS_OK with KILN_ECS_WRITE_POLICY_WRITABLE.
KilnResult decide(const PolicyFacts& facts, uint32_t component, KilnEcsWritePolicy* out);

} // namespace kw::ecs::writes
