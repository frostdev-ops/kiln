// SPDX-License-Identifier: MIT
// The hold-safe lookups of kiln.world's engine backend; see ecs_lookup.h. No wait, lock, allocation, log or KilnApi
// call in this file (tests/lint_hold.py).
#include <algorithm>
#include <cstring>

#include "ecs_engine.h"
#include "ecs_lookup.h"

namespace kw::ecs::engine {
namespace {
bool any_nonzero(const uint8_t* p, size_t n) {
    for (size_t i = 0; i < n; ++i)
        if (p[i]) return true;
    return false;
}
}  // namespace

uint32_t Reader::refuse(uint32_t status, const char* why) {
    last_.store(why, std::memory_order_relaxed);
    return status;
}
bool Reader::copy(uintptr_t address, void* out, size_t bytes) const {
    if (!bytes) return true;
    return src_.engine && src_.engine->copy && address && src_.engine->copy(address, out, bytes) == KILN_OK;
}

uint32_t Reader::table(uintptr_t manager, Table& out) const {
    const auto& e = src_.layout->entities;
    uintptr_t at = 0;
    Table t{};
    if (!add(manager, e.manager_count, at) || !copy(at, &t.count, sizeof t.count) || !add(manager, e.manager_table, at) ||
        !copy(at, &t.slots, sizeof t.slots) || !add(manager, e.manager_occupancy, at) || !copy(at, &t.bits, sizeof t.bits) ||
        !add(manager, e.manager_keys, at) || !copy(at, &t.keys, sizeof t.keys))
        return KILN_ECS_UNREADABLE;
    // ShroudForge's checks of the original table (read_entity_membership.cpp:110-123): a power-of-two capacity within
    // the profile's bound, and no array that would wrap the address space.
    if (!t.count || t.count > e.maximum_entities || (t.count & (t.count - 1)) || !t.slots || !t.bits || !t.keys ||
        t.slots > UINTPTR_MAX - t.count * sizeof(uintptr_t) || t.keys > UINTPTR_MAX - t.count * sizeof(uint32_t) ||
        t.bits > UINTPTR_MAX - ((t.count + 63) / 64) * 8)
        return KILN_ECS_UNREADABLE;
    out = t;
    return KILN_ECS_OK;
}

uint32_t Reader::find(const ContextFact& ctx, KilnEntity id, uint64_t probe_bound, EntityFact* out) {
    if (ctx.engine_status != KILN_ECS_OK) return ctx.engine_status;
    if (!id) return KILN_ECS_ABSENT;
    const auto& e = src_.layout->entities;
    Table t{};
    if (const uint32_t s = table(ctx.manager, t); s != KILN_ECS_OK) return s;
    // The engine's own lookup: hash, linear probe over the occupied slots, the first free one ends it. Bounded:
    // exhausting the bound is unreadable, never an absent answer.
    const uint64_t mask = t.count - 1;
    uint64_t slot = 0;
    const auto probe = [&]() -> uint32_t {
        slot = id_hash(id) & mask;
        const uint64_t bound = std::min<uint64_t>(t.count, probe_bound);
        for (uint64_t visited = 0; visited < bound; ++visited, slot = (slot + 1) & mask) {
            uint64_t bits = 0;
            uint32_t key = 0;
            if (!copy(t.bits + (slot / 64) * 8, &bits, sizeof bits)) return KILN_ECS_UNREADABLE;
            if (!((bits >> (slot % 64)) & 1)) return KILN_ECS_ABSENT;
            if (!copy(t.keys + slot * sizeof key, &key, sizeof key)) return KILN_ECS_UNREADABLE;
            if (key == id) return KILN_ECS_OK;
        }
        return refuse(KILN_ECS_UNREADABLE, "the entity id probe did not end within its bound");
    };
    if (const uint32_t s = probe(); s != KILN_ECS_OK) {
        // Absent only from two lookups that agree in the same table (ShroudForge's rule): an insertion or a move
        // during the first one may hide the id.
        if (s != KILN_ECS_ABSENT) return s;
        Table t2{};
        return probe() == KILN_ECS_ABSENT && table(ctx.manager, t2) == KILN_ECS_OK && t2 == t ? KILN_ECS_ABSENT
                                                                                              : KILN_ECS_UNREADABLE;
    }
    uintptr_t record = 0;
    if (!copy(t.slots + slot * sizeof record, &record, sizeof record) || !record) return KILN_ECS_UNREADABLE;
    uint32_t seen_id = 0, generation = 0, row = 0;
    uintptr_t layout = 0, storage = 0, definition = 0;
    if (!copy(record + e.entity_id, &seen_id, sizeof seen_id) || !copy(record + e.entity_generation, &generation, sizeof generation) ||
        !copy(record + e.entity_layout, &layout, sizeof layout) || !copy(record + e.entity_storage, &storage, sizeof storage) ||
        !copy(record + e.entity_row, &row, sizeof row) || !copy(record + e.entity_definition, &definition, sizeof definition))
        return KILN_ECS_UNREADABLE;
    if (seen_id != id || !layout || !storage || row > kMaxRow) return KILN_ECS_UNREADABLE;
    // The template the entity was spawned from: 16 bytes through its definition (none for a save-loaded entity).
    uint8_t uuid[16] = {};
    bool has_template = false;
    if (definition && e.entity_definition_uuid_validated) {
        if (definition > UINTPTR_MAX - e.entity_definition_uuid - sizeof uuid ||
            !copy(definition + e.entity_definition_uuid, uuid, sizeof uuid))
            return KILN_ECS_UNREADABLE;
        has_template = any_nonzero(uuid, sizeof uuid);
    }
    // The slot still holds this record under this id, in the same table: the record is the id's now.
    uint64_t bits = 0;
    uint32_t key = 0;
    uintptr_t again = 0;
    Table t2{};
    if (!copy(t.bits + (slot / 64) * 8, &bits, sizeof bits) || !((bits >> (slot % 64)) & 1) ||
        !copy(t.keys + slot * sizeof key, &key, sizeof key) || key != id ||
        !copy(t.slots + slot * sizeof again, &again, sizeof again) || again != record || table(ctx.manager, t2) != KILN_ECS_OK ||
        !(t2 == t))
        return KILN_ECS_UNREADABLE;
    out->generation = generation;
    out->layout_stamp = layout;
    out->alive = true;
    out->has_template = has_template;
    std::memcpy(out->template_uuid, uuid, sizeof uuid);
    out->record = record;
    out->storage = storage;
    out->row = row;
    return KILN_ECS_OK;
}

uint32_t Reader::locate(const ContextFact& ctx, KilnEntity id, uint16_t index, uint16_t size, uint64_t probe_bound,
                        Located* out) noexcept {
    *out = Located{};
    EntityFact entity{};
    if (const uint32_t s = find(ctx, id, probe_bound, &entity); s != KILN_ECS_OK) return s;
    out->generation = entity.generation;
    out->layout_stamp = entity.layout_stamp;
    // Presence, offset and stride from the archetype, as component() reads them, for a row known before the hold.
    const auto& e = src_.layout->entities;
    const uintptr_t layout = static_cast<uintptr_t>(entity.layout_stamp);
    uint64_t word = 0;
    if (!copy(layout + e.component_bits + (index / 64) * 8, &word, sizeof word)) return KILN_ECS_UNREADABLE;
    if (!((word >> (index % 64)) & 1)) return KILN_ECS_OK;  // the archetype lacks it
    uint16_t offset = 0, stride = 0;
    if (!copy(layout + e.component_offsets + index * 2, &offset, sizeof offset) ||
        !copy(layout + e.component_strides + index * 2, &stride, sizeof stride))
        return KILN_ECS_UNREADABLE;
    if (stride != size) return KILN_ECS_UNREADABLE;
    const uint64_t delta = offset + static_cast<uint64_t>(entity.row) * stride;
    if (entity.storage > UINTPTR_MAX - delta || entity.storage + delta > UINTPTR_MAX - stride) return KILN_ECS_UNREADABLE;
    out->present = true;
    out->address = entity.storage + static_cast<uintptr_t>(delta);
    return KILN_ECS_OK;
}

bool Reader::tracking(uintptr_t registry, uint16_t index, size_t offset_a, size_t offset_b, bool* a, bool* b) const noexcept {
    // kiln.internal.engine's untracked_component (resolve.cpp), field for field: count at +16 (1..1024), the index's
    // word of each mask, both words again, the count again.
    uint64_t count = 0, again = 0, wa = 0, wb = 0, wa2 = 0, wb2 = 0;
    const size_t word = static_cast<size_t>(index / 64) * sizeof(uint64_t);
    uintptr_t at = 0;
    if (!registry || !offset_a || !offset_b || !add(registry, 16, at) || !copy(at, &count, sizeof count) || !count ||
        count > 1024 || index >= count || !add(registry, offset_a + word, at) || !copy(at, &wa, sizeof wa) ||
        !add(registry, offset_b + word, at) || !copy(at, &wb, sizeof wb) || !add(registry, offset_a + word, at) ||
        !copy(at, &wa2, sizeof wa2) || !add(registry, offset_b + word, at) || !copy(at, &wb2, sizeof wb2) ||
        !add(registry, 16, at) || !copy(at, &again, sizeof again) || again != count || wa2 != wa || wb2 != wb)
        return false;
    const uint64_t bit = uint64_t{1} << (index % 64);
    *a = (wa & bit) != 0;
    *b = (wb & bit) != 0;
    return true;
}

}  // namespace kw::ecs::engine
