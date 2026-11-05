// SPDX-License-Identifier: MIT
// kiln.creatures: guarded engine access. No C++ object with a destructor lives in a __try frame.
#include <windows.h>

#include "internal.h"

namespace kcr {

bool seh_component(uintptr_t w, uint32_t hash, uint32_t entity, void** out, uint32_t* size) {
    *out = nullptr;
    if (size) *size = 0;
    if (!w || !fn.get_component || !entity) return false;
    Slice s{};
    __try {
        fn.get_component(&s, w, hash, entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    *out = s.ptr;
    if (size) *size = static_cast<uint32_t>(s.size);
    return s.ptr != nullptr;
}

bool seh_template_component(uintptr_t w, uint32_t hash, uint32_t entity, void** out) {
    *out = nullptr;
    if (!w || !fn.get_template_component || !entity) return false;
    __try {
        *out = fn.get_template_component(w, hash, entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
    return *out != nullptr;
}

bool seh_transform(uintptr_t w, uint32_t entity, int64_t** out) {
    *out = nullptr;
    if (!w || !fn.get_transform || !entity) return false;
    __try {
        *out = fn.get_transform(w, entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
    return *out != nullptr;
}

namespace {
// The entity record from the entity map (storage + entity_map), checked against its id.
bool record_of(uintptr_t w, uint32_t entity, uint8_t** rec) {
    *rec = nullptr;
    uint8_t* storage = reinterpret_cast<uint8_t*>(w + L.world_storage);
    uint8_t* map = storage + L.entity_map;
    const uint32_t id = entity;
    const uint64_t si = fn.entity_map_find(map, &id);
    if (si >= *reinterpret_cast<uint64_t*>(map + L.map_cap)) return false;
    uint8_t* r = *reinterpret_cast<uint8_t**>(*reinterpret_cast<uint8_t**>(map + L.map_values) + si * 8);
    if (!r || *reinterpret_cast<uint32_t*>(r + 0x10) != entity) return false;
    *rec = r;
    return true;
}
}  // namespace

bool seh_template_slot(uintptr_t w, uint32_t hash, uint32_t entity, void*** out) {
    *out = nullptr;
    if (!w || !F.slots || !entity) return false;
    __try {
        uint8_t* storage = reinterpret_cast<uint8_t*>(w + L.world_storage);
        uint8_t* registry = *reinterpret_cast<uint8_t**>(storage);
        const uint32_t h = hash;
        const uint16_t* t = fn.component_map_find(registry + L.tpl_map, &h);
        if (!t) return false;
        uint8_t* rec = nullptr;
        if (!record_of(w, entity, &rec)) return false;
        const uint8_t* arch = *reinterpret_cast<uint8_t**>(rec + L.rec_archetype);
        if (!arch) return false;
        const uint16_t ti = *t;
        const uint64_t bits = *reinterpret_cast<const uint64_t*>(arch + (ti >> 6) * 8);
        if (!((bits >> (ti & 63)) & 1)) return true;  // the archetype has no such template component: no slot
        const uint16_t col = *reinterpret_cast<const uint16_t*>(arch + L.tpl_columns + 2 * ti);
        uint8_t* chunk = *reinterpret_cast<uint8_t**>(rec + L.rec_chunk);
        const uint32_t index = *reinterpret_cast<uint32_t*>(rec + L.rec_index);
        if (!chunk) return false;
        *out = reinterpret_cast<void**>(chunk + col + static_cast<uint64_t>(index) * 8);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}

bool seh_component_index(void* map, uint32_t hash, uint16_t* out) {
    *out = 0;
    if (!map || !fn.component_map_find) return false;
    __try {
        const uint32_t h = hash;
        const uint16_t* t = fn.component_map_find(map, &h);
        if (!t) return false;
        *out = *t;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_record_template(uintptr_t w, uint32_t entity, uintptr_t* out) {
    *out = 0;
    if (!w || !fn.entity_map_find || !entity) return false;
    __try {
        uint8_t* rec = nullptr;
        if (!record_of(w, entity, &rec)) return false;
        *out = *reinterpret_cast<uintptr_t*>(rec + L.rec_template);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}

bool seh_read(const void* p, void* out, size_t n) {
    if (!p) return false;
    __try {
        std::memcpy(out, p, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_write(void* p, const void* in, size_t n) {
    if (!p) return false;
    __try {
        std::memcpy(p, in, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_current_entity(void* program_ctx, uint32_t* out) {
    *out = 0;
    if (!program_ctx || !fn.current_entity) return false;
    __try {
        uint32_t v = 0;
        const uint32_t* r = fn.current_entity(program_ctx, &v);
        *out = r ? *r : v;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool seh_type_by_hash(uint32_t hash, uintptr_t* out) {
    *out = 0;
    if (!fn.type_by_hash) return false;
    __try {
        *out = fn.type_by_hash(hash);
        return *out != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = 0;
        return false;
    }
}

bool seh_arsenal_find(void* registry, const uint8_t* guid, void** out) {
    *out = nullptr;
    if (!registry || !fn.arsenal_find) return false;
    __try {
        *out = fn.arsenal_find(registry, guid);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = nullptr;
        return false;
    }
}

bool seh_listener(KilnCreatureListenerFn f, const KilnCreatureEvent* ev, uint32_t n, void* user, uint32_t* out,
                  unsigned long* code) {
    __try {
        *out = f(ev, n, user);
        return true;
    } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kcr
