// SPDX-License-Identifier: MIT
// kiln.abilities: guarded engine access. A wrong layout on an unknown build turns into "no value", never a crash.
#include <windows.h>

#include "engine.h"

namespace kab {

Engine E;

bool read_bytes(const void* src, void* dst, size_t n) {
    if (!src || !dst) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool write_bytes(void* dst, const void* src, size_t n) {
    if (!src || !dst) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

const uint8_t* get_program(uintptr_t program_registry, uint32_t id) {
    if (!program_registry || !E.get_program || !id) return nullptr;
    __try {
        return E.get_program(program_registry, id);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

uint8_t* instance(uintptr_t system, uint32_t impact_id) {
    if (!system || !E.instance_lookup || !impact_id) return nullptr;
    __try {
        return E.instance_lookup(system, impact_id);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

void* component(uintptr_t world, uint32_t hash, uint32_t entity, uint64_t* size) {
    if (!world || !E.get_component || !entity) return nullptr;
    Engine::Slice s{};
    __try {
        E.get_component(&s, world, hash, entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    if (size) *size = s.size;
    return s.ptr;
}

// A SInt32Attribute component ({u32 rootId, u16 storageOffset, u8 flags, u8 storageCount, s32 data[]}) and one of
// its attribute ids -> the slot (the same rule kiln.combat uses for Health).
bool attr_slot(const void* registry, uint8_t* comp, uint32_t attr, int32_t** slot) {
    if (!registry || !comp || !E.attr_find) return false;
    __try {
        if (!(comp[6] & 1)) return false;  // flags bit 0: initialised
        const uint32_t root = *reinterpret_cast<const uint32_t*>(comp);
        const uint16_t off = *reinterpret_cast<const uint16_t*>(comp + 4);
        const uint8_t count = comp[7];
        Engine::AttrFindResult r{};
        E.attr_find(&r, attr, registry);
        if (r.err || !r.info || *reinterpret_cast<const uint32_t*>(r.info) != root) return false;
        const uint16_t s = *reinterpret_cast<const uint16_t*>(r.info + 0xC);
        if (s >= count) return false;
        *slot = reinterpret_cast<int32_t*>(comp + off + s * 4u);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool attr_add(const void* registry, void* comp, uint32_t attr, int32_t delta) {
    if (!registry || !comp || !E.attr_add) return false;
    __try {
        E.attr_add(comp, registry, delta, attr);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool trigger_sequence(void* actor_input, uint32_t entity, uint32_t sequence, int64_t now, uint16_t trigger) {
    if (!E.trigger_sequence || !actor_input) return false;
    alignas(8) uint8_t out[0x18] = {};
    Engine::EntityTime who{entity, 0, now};
    __try {
        E.trigger_sequence(out, actor_input, &who, sequence, trigger, 0x38 /* PlayerInputType: none */);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

int64_t ecs_now(uintptr_t world) {  // tick period x 60 Hz tick, as the game's own ECS time (kiln.spawn's rule)
    if (!world || E.world_storage < 0 || E.storage_clock < 0 || E.storage_period < 0) return 0;
    __try {
        const uintptr_t s = world + static_cast<uintptr_t>(E.world_storage);
        const int64_t period = *reinterpret_cast<const int64_t*>(s + E.storage_period);
        const uint32_t tick = *reinterpret_cast<const uint32_t*>(s + E.storage_clock);
        return period * static_cast<int64_t>(tick);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

namespace {
struct NameSlice {  // keen Slice<char>
    const char* ptr;
    uint64_t len;
};
}  // namespace

bool register_native(uintptr_t registry, uint32_t hash, NativeFn fn, const char* name, size_t name_len, uint8_t argc) {
    if (!registry || !E.register_native) return false;
    if (name_len > static_cast<size_t>(E.name_cap)) name_len = static_cast<size_t>(E.name_cap);  // the engine traps
    NameSlice s{name, name_len};
    __try {
        return (E.register_native(registry, hash, fn, &s, argc) & 0xff) != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool program_map(uintptr_t reg, MapView* out) {
    if (!reg) return false;
    __try {
        out->bits = *reinterpret_cast<uint64_t**>(reg + E.prog_bits);
        out->cap = *reinterpret_cast<uint64_t*>(reg + E.prog_cap);
        out->keys = *reinterpret_cast<uint32_t**>(reg + E.prog_keys);
        out->values = *reinterpret_cast<uint8_t**>(reg + E.prog_values);
        out->count = reinterpret_cast<uint32_t*>(reg + E.prog_count);
        return out->cap && (out->cap & (out->cap - 1)) == 0 && out->bits && out->keys && out->values;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool program_map_insert(uintptr_t reg, uint32_t id, const void* program, bool* existed) {
    *existed = false;
    MapView m{};
    if (!program_map(reg, &m)) return false;
    __try {
        if (*m.count + 1 >= m.cap) return false;  // keep at least one empty slot: lookups stop at the first one
        uint64_t i = id & (m.cap - 1);
        for (uint64_t steps = 0; steps < m.cap; ++steps) {
            const bool used = (m.bits[i >> 6] >> (i & 63)) & 1;
            if (!used) {
                reinterpret_cast<const void**>(m.values)[i] = program;
                m.keys[i] = id;
                m.bits[i >> 6] |= 1ull << (i & 63);
                *m.count += 1;
                return true;
            }
            if (m.keys[i] == id) {
                *existed = true;
                return true;
            }
            i = i + 1 < m.cap ? i + 1 : 0;
        }
        return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

size_t program_map_snapshot(uintptr_t reg, uint32_t* ids, const uint8_t** programs, size_t cap) {
    MapView m{};
    if (!program_map(reg, &m)) return SIZE_MAX;
    __try {
        size_t n = 0;
        for (uint64_t i = 0; i < m.cap; ++i) {
            if (!((m.bits[i >> 6] >> (i & 63)) & 1)) continue;
            if (n < cap) {
                ids[n] = m.keys[i];
                programs[n] = reinterpret_cast<const uint8_t* const*>(m.values)[i];
            }
            ++n;
        }
        return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return SIZE_MAX;
    }
}

}  // namespace kab
