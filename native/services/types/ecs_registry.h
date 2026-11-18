// SPDX-License-Identifier: MIT
// kiln.types' ecs_registry member (kiln_types.h) as a pure builder over a catalog source, so the host
// test can drive it with a fake source (including a catalog replaced while it is being read). The source's stamp
// is read before and after the rows; a change refuses the read and leaves the caller's rows untouched. Rows are copied records: the
// caller never sees the provider's storage.
#pragma once
#include <cstdint>
#include <cstring>
#include <vector>

#include "services/kiln_types.h"
#include "services/kiln_world.h" // KILN_ECS_* statuses and the registry result codes

namespace ktypes::ecs {

struct Source {
    void* user = nullptr;
    uint32_t (*count)(void* user);
    uint64_t (*stamp)(void* user); // catalog identity; changes when the catalog is replaced
    bool (*row)(void* user, uint32_t index, KilnEcsRegistryRow* out); // false: the row vanished mid-read
};

inline KilnResult build(const Source& src, uint32_t world_kind, KilnEcsRegistryInfo* info,
                        KilnEcsRegistryRow* rows, uint32_t capacity, uint32_t* required) {
    if (required) *required = 0;
    if (!src.count || !src.stamp || !src.row || !info || info->size < sizeof(KilnEcsRegistryInfo) ||
        world_kind >= KILN_WORLD_KINDS || (capacity && !rows))
        return KILN_ERR_INVALID_ARGUMENT;
    const uint32_t size = info->size;
    const uint64_t before = src.stamp(src.user);
    const uint32_t count = src.count(src.user);
    *info = KilnEcsRegistryInfo{};
    info->size = size;
    info->world_kind = world_kind;
    info->count = count;
    info->layout_stamp = before;
    info->status = KILN_ECS_OK;
    if (required) *required = count;
    if (count > capacity) return KILN_ERR_BUFFER_TOO_SMALL; // nothing copied
    auto refuse = [&]() -> KilnResult {
        info->status = KILN_ECS_UNREADABLE;
        info->count = 0;
        if (required) *required = 0;
        return KILN_ECS_ERR_UNREADABLE;
    };
    // Staged: the caller's rows are written only after the stamp and count were read again and still agree.
    std::vector<KilnEcsRegistryRow> staging(count);
    for (uint32_t i = 0; i < count; ++i)
        if (!src.row(src.user, i, &staging[i]) || staging[i].index != i) return refuse();
    if (src.stamp(src.user) != before || src.count(src.user) != count) return refuse();
    if (count) std::memcpy(rows, staging.data(), count * sizeof(KilnEcsRegistryRow));
    return KILN_OK;
}

} // namespace ktypes::ecs
