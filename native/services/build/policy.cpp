// SPDX-License-Identifier: MIT
// Who may edit where.
//  - Zones: Kiln's edits never pass the game's building programs, so kiln.gameplay's zone hooks do not see them. With
//    KILN_BUILD_F_RESPECT_ZONES the edit's box is sampled against the zone table for its actor.
//  - Player bases: the game's own BuildZoneSystem singleton holds the base build zones the server's
//    add_buildzones_server program rebuilds every step (list build/bzs.bases: {data, count, capacity, ...}, entries of
//    0x48 bytes {i64 min[3], i64 max[3] (32.32 metres), BaseId[16], u32 entity (the altar), u8 flag, u8 owner kind,
//    u8 abandoned-derived}). Edits that touch one are refused unless the caller says KILN_BUILD_F_IN_BASES.
#include <algorithm>

#include "internal.h"

namespace kb {
namespace {

struct Base {
    KilnEntity altar;
    KilnVec3 lo, hi;
    bool abandoned;
};

bool read_bases(std::vector<Base>& out) {
    out.clear();
    if (!F.zones) return false;
    const uintptr_t w = server_world();
    if (!w) return false;
    const uintptr_t bzs = g_api->singleton(w, kstr("BuildZoneSystem"));
    if (!bzs) return false;
    uint64_t list[2] = {};
    if (!seh_copy(list, reinterpret_cast<const void*>(bzs + static_cast<uintptr_t>(L.bzs_bases)), sizeof list)) return false;
    if (!list[0] || list[1] > 4096) return list[1] == 0;
    std::vector<uint8_t> raw(static_cast<size_t>(list[1] * L.zone_stride));
    if (!seh_copy(raw.data(), reinterpret_cast<const void*>(list[0]), raw.size())) return false;
    for (uint64_t i = 0; i < list[1]; ++i) {
        const uint8_t* e = raw.data() + i * L.zone_stride;
        int64_t mm[6];
        std::memcpy(mm, e, sizeof mm);
        Base b{};
        std::memcpy(&b.altar, e + L.zone_entity, 4);
        b.lo = KilnVec3{fixed_to_m(mm[0]), fixed_to_m(mm[1]), fixed_to_m(mm[2])};
        b.hi = KilnVec3{fixed_to_m(mm[3]), fixed_to_m(mm[4]), fixed_to_m(mm[5])};
        b.abandoned = L.zone_abandoned > 0 && e[L.zone_abandoned] != 0;
        if (b.hi.x < b.lo.x || b.hi.y < b.lo.y || b.hi.z < b.lo.z) continue;
        out.push_back(b);
    }
    return true;
}

}  // namespace

uint32_t zone_denies_box(KilnEntity actor, KilnVec3 lo, KilnVec3 hi, uint32_t action) {
    if (!g_gameplay || !g_gameplay->zone_denies || !action) return 0;
    // sample a grid of at most 9^3 points, spacing <= 4 m where the box allows, corners and centre included
    int steps[3];
    const double ext[3] = {hi.x - lo.x, hi.y - lo.y, hi.z - lo.z};
    for (int a = 0; a < 3; ++a) steps[a] = std::clamp(static_cast<int>(std::ceil(ext[a] / 4.0)), 1, 8);
    for (int i = 0; i <= steps[0]; ++i)
        for (int j = 0; j <= steps[1]; ++j)
            for (int k = 0; k <= steps[2]; ++k) {
                const KilnVec3 p{lo.x + ext[0] * i / steps[0], lo.y + ext[1] * j / steps[1], lo.z + ext[2] * k / steps[2]};
                for (uint32_t bit = 1; bit <= KILN_GAMEPLAY_DENY_TERRAFORM; bit <<= 1) {
                    if (!(action & bit)) continue;
                    uint32_t zone = 0;
                    if (g_gameplay->zone_denies(actor, p, bit, &zone)) return zone ? zone : ~0u;
                }
            }
    return 0;
}

bool touches_base(KilnVec3 lo, KilnVec3 hi, KilnEntity* altar) {
    std::vector<Base> bases;
    if (!read_bases(bases)) return false;
    for (const auto& b : bases) {
        if (hi.x < b.lo.x || lo.x > b.hi.x || hi.y < b.lo.y || lo.y > b.hi.y || hi.z < b.lo.z || lo.z > b.hi.z) continue;
        if (altar) *altar = b.altar;
        return true;
    }
    return false;
}

KilnResult bases_list(KilnBuildBase* out, size_t capacity, size_t* count) {
    if (!tick_thread()) return KILN_ERR_WRONG_THREAD;
    if (!F.zones) return KILN_ERR_UNAVAILABLE;
    std::vector<Base> bases;
    if (!read_bases(bases)) return KILN_ERR_UNAVAILABLE;
    if (count) *count = bases.size();
    for (size_t i = 0; i < bases.size() && i < capacity && out; ++i) {
        if (out[i].size < sizeof(KilnBuildBase)) return KILN_ERR_INVALID_ARGUMENT;
        out[i].altar = bases[i].altar;
        out[i].center = KilnVec3{(bases[i].lo.x + bases[i].hi.x) / 2, (bases[i].lo.y + bases[i].hi.y) / 2,
                                 (bases[i].lo.z + bases[i].hi.z) / 2};
        out[i].half = KilnVec3{(bases[i].hi.x - bases[i].lo.x) / 2, (bases[i].hi.y - bases[i].lo.y) / 2,
                               (bases[i].hi.z - bases[i].lo.z) / 2};
        out[i].abandoned = bases[i].abandoned ? 1u : 0u;
        out[i].reserved = 0;
    }
    return capacity < bases.size() && out ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

}  // namespace kb
