// SPDX-License-Identifier: MIT
// Entity access on the tick thread: components through the game's GetComponent / GetEntityTransform (core symbols),
// "every entity with component X" by walking the world's entity map and testing each entity's archetype bitset.
// Map and record layouts: docs/re/world.md ("Entity map").
#include <bit>
#include <cmath>

#include "world_internal.h"

namespace kw {
namespace {

const uint32_t kCurrentTransform = 0x027ac564u;  // FNV-1a("keen::ecs::CurrentTransform")

// Fast-path self check: the first find() of each component compares the archetype-bitset answer with GetComponent.
std::atomic<int> g_find_mode{0};  // 0 = unchecked, 1 = bitset verified, 2 = bitset disagreed (GetComponent per entity)

template <class T>
T rd(uintptr_t a) {
    T v;
    std::memcpy(&v, reinterpret_cast<const void*>(a), sizeof v);
    return v;
}

bool has_by_layout(uintptr_t rec, uint16_t type) {
    const uintptr_t layout = rd<uintptr_t>(rec + L.entity_layout);
    if (!layout) return false;
    const uint64_t word = rd<uint64_t>(layout + (type >> 6) * 8);
    return (word >> (type & 63)) & 1;
}

bool has_by_call(uintptr_t world, uint32_t hash, uint32_t e) {
    Slice s{};
    return seh_get_component(world, hash, e, &s) && s.ptr;
}

}  // namespace

bool map_find_u16(uintptr_t map, uint32_t key, uint16_t* value) {
    if (!map || !L.map.ok()) return false;
    if (rd<uint32_t>(map + L.map.count) == 0) return false;
    const uint64_t cap = rd<uint64_t>(map + L.map.cap);
    if (cap == 0 || (cap & (cap - 1))) return false;
    const uintptr_t bits = rd<uintptr_t>(map + L.map.bits);
    const uintptr_t keys = rd<uintptr_t>(map + L.map.keys);
    const uintptr_t values = rd<uintptr_t>(map + L.map.values);
    if (!bits || !keys || !values) return false;
    uint64_t i = key & (cap - 1);
    for (uint64_t probes = 0; probes < cap; ++probes) {
        if (!((rd<uint64_t>(bits + (i >> 6) * 8) >> (i & 63)) & 1)) return false;
        if (rd<uint32_t>(keys + i * 4) == key) {
            if (value) *value = rd<uint16_t>(values + i * 2);
            return true;
        }
        i = (i + 1 == cap) ? 0 : i + 1;
    }
    return false;
}

int64_t m_to_fixed(double m) { return static_cast<int64_t>(std::llround(m * 4294967296.0)); }

bool may_touch_world() { return t_in_program || g_api->on_tick_thread(); }

const void* component(KilnEntity entity, uint32_t hash, uint32_t* out_size) {
    if (out_size) *out_size = 0;
    const uintptr_t world = g_world.load(std::memory_order_acquire);
    if (!F.entities || !world || !entity || !hash || !may_touch_world()) return nullptr;
    Slice s{};
    if (!seh_get_component(world, hash, entity, &s) || !s.ptr) return nullptr;
    if (out_size) *out_size = static_cast<uint32_t>(s.size);
    return s.ptr;
}

bool exists(KilnEntity entity) {
    const uintptr_t world = g_world.load(std::memory_order_acquire);
    if (!F.entities || !world || !entity || !may_touch_world()) return false;
    const void* t = nullptr;
    return seh_get_transform(world, entity, &t) && t;
}

KilnResult transform(KilnEntity entity, KilnTransform* out) {
    if (!out || !entity) return KILN_ERR_INVALID_ARGUMENT;
    if (!F.entities) return KILN_ERR_UNAVAILABLE;
    if (!may_touch_world()) return KILN_ERR_WRONG_THREAD;
    if (!g_world.load()) return KILN_ERR_UNAVAILABLE;
    uint32_t size = 0;
    const auto* p = static_cast<const uint8_t*>(component(entity, kCurrentTransform, &size));
    if (!p || size < 52) return KILN_ERR_NOT_FOUND;
    // keen::WorldTransform: i64 x, y, z (32.32 fixed) @0, quaternion @0x18, scale float3 @0x28.
    out->position = {fixed_to_m(rd<int64_t>(reinterpret_cast<uintptr_t>(p))),
                     fixed_to_m(rd<int64_t>(reinterpret_cast<uintptr_t>(p) + 8)),
                     fixed_to_m(rd<int64_t>(reinterpret_cast<uintptr_t>(p) + 16))};
    std::memcpy(out->rotation, p + 0x18, 16);
    std::memcpy(out->scale, p + 0x28, 12);
    out->reserved = 0;
    return KILN_OK;
}

KilnResult position(KilnEntity entity, KilnVec3* out) {
    if (!out) return KILN_ERR_INVALID_ARGUMENT;
    KilnTransform t{};
    const KilnResult rc = transform(entity, &t);
    if (rc == KILN_OK) *out = t.position;
    return rc;
}

KilnResult find(uint32_t hash, KilnEntity* out, uint32_t capacity, uint32_t* out_count) {
    if (out_count) *out_count = 0;
    if (!hash || (capacity && !out)) return KILN_ERR_INVALID_ARGUMENT;
    if (!F.find || !F.entities) return KILN_ERR_UNAVAILABLE;
    if (!may_touch_world()) return KILN_ERR_WRONG_THREAD;
    const uintptr_t world = g_world.load(std::memory_order_acquire);
    if (!world) return KILN_ERR_UNAVAILABLE;
    uint16_t type = 0;
    if (!map_find_u16(registry_of(world) + L.reg_components, hash, &type)) return KILN_ERR_NOT_FOUND;
    const uintptr_t map = storage_of(world) + L.storage_entities;
    const uint64_t cap = rd<uint64_t>(map + L.map.cap);
    const uintptr_t bits = rd<uintptr_t>(map + L.map.bits);
    const uintptr_t keys = rd<uintptr_t>(map + L.map.keys);
    const uintptr_t values = rd<uintptr_t>(map + L.map.values);
    if (!cap || !bits || !keys || !values) return KILN_ERR_UNAVAILABLE;

    int mode = g_find_mode.load(std::memory_order_relaxed);
    uint32_t n = 0, checked = 0, disagree = 0;
    for (uint64_t w = 0; w < (cap + 63) / 64; ++w) {
        uint64_t word = rd<uint64_t>(bits + w * 8);
        while (word) {
            const unsigned b = static_cast<unsigned>(std::countr_zero(word));
            word &= word - 1;
            const uint64_t i = w * 64 + b;
            if (i >= cap) break;
            const uint32_t e = rd<uint32_t>(keys + i * 4);
            const uintptr_t rec = rd<uintptr_t>(values + i * 8);
            if (!e || !rec) continue;
            bool has;
            if (mode == 2) {
                has = has_by_call(world, hash, e);
            } else {
                has = has_by_layout(rec, type);
                if (mode == 0 && checked < 256) {
                    ++checked;
                    if (has != has_by_call(world, hash, e)) ++disagree;
                }
            }
            if (!has) continue;
            if (n < capacity) out[n] = e;
            ++n;
        }
    }
    if (mode == 0 && checked > 0) {
        if (disagree) {
            g_find_mode.store(2);
            KW_E("find: the archetype bitset disagreed with GetComponent for %u of %u entities; using GetComponent "
                 "per entity from now on",
                 disagree, checked);
            return find(hash, out, capacity, out_count);
        }
        g_find_mode.store(1);
        KW_I("find: archetype bitset agrees with GetComponent (%u entities checked)", checked);
    }
    if (out_count) *out_count = n;
    return n > capacity ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

uint32_t entity_count() {
    const uintptr_t world = g_world.load(std::memory_order_acquire);
    if (!F.find || !world || !may_touch_world()) return 0;
    return rd<uint32_t>(storage_of(world) + L.storage_entities + L.map.count);
}

}  // namespace kw
