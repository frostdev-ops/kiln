// SPDX-License-Identifier: MIT
// An entity world inside the fake engine of native/services/engine/tests/fake_engine.h, laid out from a profile's
// ecsReadLayout, for the tests of kiln.world's engine backend (ecs_engine_test.cpp on the host, ecs_fault_win.cpp under
// Wine): the component registration catalog (256-byte rows, names in the image, the size array), the entity id table
// (occupancy bits, keys, record slots, the engine's own id hash and linear probe), entity records, archetype layouts
// (bits, offsets, strides), chunk rows and template definitions. Writes go straight to the fake's memory; the code under
// test reads it only through kiln.internal.engine's guarded copy.
#pragma once
#ifdef _WIN32
#include <windows.h>
#else
#include <sys/mman.h>
#endif

#include <cstdint>
#include <cstring>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

#include "fake_engine.h"
#include "include/kiln_engine_layout.h"

namespace fakeworld {
using kiln::engine::Layout;
inline uint32_t fnv1a(const std::string& s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}
inline uint32_t id_hash(uint32_t id) {  // the engine's entity map hash (docs/re/world.md; ShroudForge read_entity_membership.cpp)
    uint32_t h = id;
    h = ((h >> 16) ^ h) * 0x45d9f3bu;
    h = ((h >> 16) ^ h) * 0x45d9f3bu;
    return (h >> 16) ^ h;
}
template <class T> void put(uintptr_t a, T v) { fake::put<T>(a, v); }
inline int64_t fixed(double m) { return static_cast<int64_t>(m * 4294967296.0); }
template <class T> T get(uintptr_t a) { return fake::get<T>(a); }

struct Comp { std::string name; uint32_t hash; uint16_t size, index; };
constexpr size_t kRecord = 64, kLayout = 8192, kChunk = 65536, kDef = 32;

struct World {
    const Layout& L;
    fake::Region& image;
    uintptr_t registry, manager;
    uint64_t cap;
    fake::Region catalog{1024 * 256}, sizes{4096}, occ{4096}, keys{16384}, slots{32768}, recs{1024 * kRecord},
        layouts{16 * kLayout}, chunks{16 * kChunk}, defs{1024 * kDef};
    std::vector<Comp> comps;
    size_t names_used = 0, n_layouts = 0, n_recs = 0, n_defs = 0;
    std::map<uint32_t, uintptr_t> rec_of;            // id -> record
    std::map<uintptr_t, std::vector<int>> arch_comps; // layout -> component positions in comps

    World(const Layout& layout, fake::Region& img, uintptr_t reg, uintptr_t mgr, uint64_t capacity = 256)
        : L(layout), image(img), registry(reg), manager(mgr), cap(capacity) {
        put<uint64_t>(manager + L.entities.manager_count, cap);
        put<uint64_t>(manager + L.entities.manager_table, slots.at());
        put<uint64_t>(manager + L.entities.manager_occupancy, occ.at());
        put<uint64_t>(manager + L.entities.manager_keys, keys.at());
        put<uint64_t>(registry + 8, catalog.at());
        put<uint64_t>(registry + 16, 0);
        put<uint64_t>(registry + 24, 1024);
        put<uint64_t>(registry + 232, sizes.at());
        put<uint64_t>(registry + 240, 0);
    }
    const Comp& comp(const std::string& name) const {
        for (const auto& c : comps) if (c.name == name) return c;
        throw std::runtime_error("no component " + name);
    }
    // One registration row: the name (with its terminator) in the image, its hash and size.
    void reg(const std::string& short_name, uint16_t size) {
        const std::string name = "keen::ecs::" + short_name;
        const uint16_t index = static_cast<uint16_t>(comps.size());
        const uintptr_t text = image.at(0x10000 + names_used);
        std::memcpy(reinterpret_cast<void*>(text), name.c_str(), name.size() + 1);
        names_used += (name.size() + 16) & ~size_t{15};
        const uintptr_t row = catalog.at(index * 256);
        put<uint64_t>(row + 16, text);
        put<uint64_t>(row + 24, name.size() + 1);
        put<uint32_t>(row + 32, fnv1a(name));
        put<uint32_t>(row + 56, size | (3u << 16));  // storage flags in the high half
        put<uint16_t>(sizes.at(index * 2), size);
        comps.push_back({name, fnv1a(name), size, index});
        put<uint64_t>(registry + 16, comps.size());
        put<uint64_t>(registry + 240, comps.size());
    }
    // An archetype holding these components; component k of the chunk sits at k * 4096 (rows of its stride).
    uintptr_t archetype(const std::vector<std::string>& names) {
        const uintptr_t lay = layouts.at(n_layouts * kLayout);
        std::vector<int> mine;
        int k = 0;
        for (const auto& n : names) {
            const Comp& c = comp(n);
            const uintptr_t word = lay + L.entities.component_bits + (c.index / 64) * 8;
            put<uint64_t>(word, get<uint64_t>(word) | (uint64_t{1} << (c.index % 64)));
            put<uint16_t>(lay + L.entities.component_offsets + c.index * 2, static_cast<uint16_t>(k * 4096));
            put<uint16_t>(lay + L.entities.component_strides + c.index * 2, c.size);
            mine.push_back(static_cast<int>(&c - comps.data()));
            ++k;
        }
        arch_comps[lay] = mine;
        ++n_layouts;
        return lay;
    }
    uintptr_t chunk_of(uintptr_t lay) const { return chunks.at(((lay - layouts.at()) / kLayout) * kChunk); }
    uintptr_t row_address(uintptr_t lay, uint32_t row, const std::string& name) const {
        const Comp& c = comp(name);
        const auto off = get<uint16_t>(lay + L.entities.component_offsets + c.index * 2);
        return chunk_of(lay) + off + static_cast<uintptr_t>(row) * c.size;
    }
    size_t slot_of(uint32_t id) const {
        size_t s = id_hash(id) & (cap - 1);
        for (size_t n = 0; n < cap; ++n, s = (s + 1) & (cap - 1)) {
            const bool used = (get<uint64_t>(occ.at((s / 64) * 8)) >> (s % 64)) & 1;
            if (!used || get<uint32_t>(keys.at(s * 4)) == id) return s;
        }
        throw std::runtime_error("full");
    }
    // An entity: record, slot, row; uuid nullptr = no definition.
    uintptr_t add(uint32_t id, uint32_t generation, uintptr_t lay, uint32_t row, const uint8_t* uuid = nullptr) {
        const uintptr_t rec = recs.at(n_recs++ * kRecord);
        put<uint32_t>(rec + L.entities.entity_id, id);
        put<uint32_t>(rec + L.entities.entity_generation, generation);
        put<uint64_t>(rec + L.entities.entity_layout, lay);
        put<uint64_t>(rec + L.entities.entity_storage, chunk_of(lay));
        put<uint32_t>(rec + L.entities.entity_row, row);
        uintptr_t def = 0;
        if (uuid) {
            def = defs.at(n_defs++ * kDef);
            std::memcpy(reinterpret_cast<void*>(def + L.entities.entity_definition_uuid), uuid, 16);
        }
        put<uint64_t>(rec + L.entities.entity_definition, def);
        const size_t s = slot_of(id);
        put<uint64_t>(occ.at((s / 64) * 8), get<uint64_t>(occ.at((s / 64) * 8)) | (uint64_t{1} << (s % 64)));
        put<uint32_t>(keys.at(s * 4), id);
        put<uint64_t>(slots.at(s * 8), rec);
        rec_of[id] = rec;
        return rec;
    }
    void remove(uint32_t id) {
        const size_t s = slot_of(id);
        put<uint64_t>(occ.at((s / 64) * 8), get<uint64_t>(occ.at((s / 64) * 8)) & ~(uint64_t{1} << (s % 64)));
    }
    void bytes(uint32_t id, const std::string& name, const std::vector<uint8_t>& b) {
        const uintptr_t rec = rec_of.at(id);
        const uintptr_t at = row_address(get<uint64_t>(rec + L.entities.entity_layout), get<uint32_t>(rec + L.entities.entity_row), name);
        std::memcpy(reinterpret_cast<void*>(at), b.data(), b.size());
    }
    void transform(uint32_t id, double x, double y, double z) {
        std::vector<uint8_t> t(56, 0);
        const int64_t xyz[3] = {fixed(x), fixed(y), fixed(z)};
        std::memcpy(t.data(), xyz, sizeof xyz);
        const float scale[3] = {1, 1, 1};
        std::memcpy(t.data() + 0x28, scale, sizeof scale);
        bytes(id, "keen::ecs::CurrentTransform", t);
    }
};

inline const uint8_t kUuidPlayer[16] = {0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f};
inline const uint8_t kUuidChest[16] = {0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xcb, 0xcc, 0xcd, 0xce, 0xcf};

// A faulting area large enough for any record, row or array offset the tests point into it (fake::unmapped() is one
// page: a chunk offset past it could land in a mapped neighbour).
inline uintptr_t faulting() {
    static fake::Region area(1 << 20);
#ifdef _WIN32
    DWORD old = 0;
    static const bool sealed = VirtualProtect(area.base, area.size, PAGE_NOACCESS, &old) != 0;
#else
    static const bool sealed = mprotect(area.base, area.size, PROT_NONE) == 0;
#endif
    return sealed ? area.at() : fake::unmapped();
}

inline uint32_t H(const World& w, const char* short_name) { return w.comp(std::string("keen::ecs::") + short_name).hash; }

// A player (1), a chest (4097) 3 m away, a far tree (4098, no inventory), a save-loaded rock without definition
// (4099), and a few colliding ids to exercise the probe.
inline void populate(World& w) {
    w.reg("CurrentTransform", 56);
    w.reg("Inventory", 48);
    w.reg("Health", 8);
    w.reg("PlayerTag", 4);
    w.reg("Unused", 16);
    const uintptr_t player = w.archetype({"keen::ecs::CurrentTransform", "keen::ecs::Inventory", "keen::ecs::Health",
                                          "keen::ecs::PlayerTag"});
    const uintptr_t chest = w.archetype({"keen::ecs::CurrentTransform", "keen::ecs::Inventory"});
    const uintptr_t prop = w.archetype({"keen::ecs::CurrentTransform"});
    w.add(1, 1, player, 0, kUuidPlayer);
    w.add(4097, 3, chest, 0, kUuidChest);
    w.add(4098, 1, prop, 0, kUuidChest);
    w.add(4099, 1, prop, 1, nullptr);
    w.transform(1, 100.0, 50.0, -20.0);
    w.transform(4097, 103.0, 50.0, -20.0);
    w.transform(4098, 300.0, 50.0, -20.0);
    w.transform(4099, 101.0, 50.0, -20.0);
    std::vector<uint8_t> inv(48);
    for (size_t i = 0; i < inv.size(); ++i) inv[i] = static_cast<uint8_t>(i + 1);
    w.bytes(1, "keen::ecs::Inventory", inv);
    for (auto& b : inv) b ^= 0x80;
    w.bytes(4097, "keen::ecs::Inventory", inv);
    w.bytes(1, "keen::ecs::Health", {0x64, 0, 0, 0, 0, 0, 0x80, 0x3f});
    w.bytes(1, "keen::ecs::PlayerTag", {7, 0, 0, 0});
    // ids whose hashes collide in the table: they probe past each other
    std::vector<uint32_t> same;
    for (uint32_t id = 5000; same.size() < 3; ++id)
        if ((id_hash(id) & (w.cap - 1)) == (id_hash(5000) & (w.cap - 1))) same.push_back(id);
    for (size_t i = 0; i < same.size(); ++i) w.add(same[i], 1, prop, static_cast<uint32_t>(2 + i), nullptr);
}

}  // namespace fakeworld
