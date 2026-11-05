// SPDX-License-Identifier: MIT
// kiln.combat: guarded engine access. Every raw read or engine call that depends on a layout runs under structured
// exception handling, so a wrong layout on an unknown build turns into "no value", never a crash. No C++ object with
// a destructor lives in a __try frame.
#include <windows.h>

#include "combat_internal.h"

namespace kc {

HitOffsets H;
Engine E;

void* component(uintptr_t world, uint32_t hash, uint32_t entity, uint64_t* size) {
    if (!world || !E.get_component || !entity) return nullptr;
    Slice s{};
    __try {
        E.get_component(&s, world, hash, entity);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
    if (size) *size = s.size;
    return s.ptr;
}

bool health_slots(const void* registry, uint8_t* comp, int32_t** cur, int32_t** max) {
    if (!registry || !comp || !E.attr_find) return false;
    __try {
        if (!(comp[6] & 1)) return false;  // AttributeComponent.flags bit 0: initialised
        const uint32_t root = *reinterpret_cast<const uint32_t*>(comp);
        const uint16_t off = *reinterpret_cast<const uint16_t*>(comp + 4);
        const uint8_t count = comp[7];
        AttrFindResult r{};
        E.attr_find(&r, kAttrHealth, registry);
        if (r.err || !r.info || *reinterpret_cast<const uint32_t*>(r.info) != root) return false;
        const uint16_t s_cur = *reinterpret_cast<const uint16_t*>(r.info + 0xC);
        AttrFindResult m{};
        E.attr_find(&m, kAttrHealthMax, registry);
        if (m.err || !m.info || *reinterpret_cast<const uint32_t*>(m.info) != root) return false;
        const uint16_t s_max = *reinterpret_cast<const uint16_t*>(m.info + 0xC);
        if (s_cur >= count || s_max >= count) return false;
        *cur = reinterpret_cast<int32_t*>(comp + off + s_cur * 4u);
        *max = reinterpret_cast<int32_t*>(comp + off + s_max * 4u);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_health(uintptr_t world, const void* registry, uint32_t entity, int32_t* cur, int32_t* max) {
    auto* comp = static_cast<uint8_t*>(component(world, kHealthHash, entity));
    int32_t *pc = nullptr, *pm = nullptr;
    if (!comp || !health_slots(registry, comp, &pc, &pm)) return false;
    __try {
        *cur = *pc;
        *max = *pm;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool add_health(const void* registry, uint8_t* comp, int32_t delta) {
    if (!E.attr_add) return false;
    __try {
        E.attr_add(comp, registry, delta, kAttrHealth);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool transform(uintptr_t world, uint32_t entity, WorldTransform* out) {
    if (!world || !E.get_transform || !entity) return false;
    __try {
        const WorldTransform* t = E.get_transform(world, entity);
        if (!t) return false;
        *out = *t;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool actor_states(uintptr_t world, uint32_t entity, ActorStates* out, bool* has_actor) {
    *has_actor = false;
    *out = ActorStates{};
    if (E.actor_state < 0) return false;
    uint64_t size = 0;
    auto* a = static_cast<const uint8_t*>(component(world, kActorHash, entity, &size));
    if (!a || size < static_cast<uint64_t>(E.actor_state) + 8) return false;
    const bool layer = E.actor_flags >= 0 && E.actor_on >= 0 && E.actor_off >= 0 &&
                       size > static_cast<uint64_t>(E.actor_flags) && size >= static_cast<uint64_t>(E.actor_on) + 8 &&
                       size >= static_cast<uint64_t>(E.actor_off) + 8;
    __try {
        *has_actor = true;
        out->state = *reinterpret_cast<const uint64_t*>(a + E.actor_state);
        if (layer && (a[E.actor_flags] & 1)) {
            out->overrides = true;
            out->on = *reinterpret_cast<const uint64_t*>(a + E.actor_on);
            out->off = *reinterpret_cast<const uint64_t*>(a + E.actor_off);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        *out = ActorStates{};
        return false;
    }
}

bool actor_dead(uintptr_t world, uint32_t entity, bool* has_actor) {
    ActorStates s;
    return actor_states(world, entity, &s, has_actor) && state_has(s, s.state, kStateDead);
}

bool actor_dead_after_check(uintptr_t world, uint32_t entity, int32_t health, bool* has_actor) {
    ActorStates s;
    if (!actor_states(world, entity, &s, has_actor)) return false;
    constexpr uint64_t kDeadBit = 1ull << kStateDead;
    uint64_t next = s.state;
    if (health >= 1)
        next &= ~kDeadBit;  // death: `and [actor+state], ~0x80`
    else if (!state_has(s, next, kStateDeathSequence) && !state_has(s, next, kStateDead))
        next |= kDeadBit;   // death: `or [actor+state], 0x80`
    return state_has(s, next, kStateDead);
}

uint8_t* alloc_hit(void* impact_system) {
    if (!impact_system || !E.alloc_hit) return nullptr;
    __try {
        return E.alloc_hit(impact_system, 0);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return nullptr;
    }
}

bool already_hit(void* impact_system, uint32_t impact_id, uint32_t target) {
    if (!impact_system || !E.already_hit || !target) return false;
    __try {
        return E.already_hit(impact_system, impact_id, target);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_u8(const void* p, uint8_t* out) {
    if (!p) return false;
    __try {
        *out = *static_cast<const uint8_t*>(p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool copy_bytes(void* dst, const void* src, size_t n) {
    if (!dst || !src) return false;
    __try {
        std::memcpy(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// The events-in queue of a program (what the engine puts in the iterator slot; read the same way by
// trigger_knowledge_on_kill): u32 count at +evq_count, blocks of 1 << evq_shift bytes at *(+evq_blocks), first block
// index (1-based, u16) at +evq_first, next index at block+evq_next; records packed from block+8, a record never
// crossing the block end. EntityDiedEvent: 16 bytes, dead entity id at +8.
bool walk_died_queue(const uint8_t* q, uint32_t* out, uint32_t cap, uint32_t* count) {
    *count = 0;
    if (!q || E.evq_count < 0) return false;
    __try {
        const uint32_t n = *reinterpret_cast<const uint32_t*>(q + E.evq_count);
        if (n == 0) return true;
        if (n > 100000) return false;
        const uint64_t block_size = 1ull << E.evq_shift;
        const uint8_t* base = *reinterpret_cast<uint8_t* const*>(q + E.evq_blocks);
        uint16_t idx = *reinterpret_cast<const uint16_t*>(q + E.evq_first);
        const uint8_t* block = idx ? base + (idx - 1) * block_size : nullptr;
        uint32_t off = 0;
        constexpr uint32_t kRec = 0x10;
        for (uint32_t i = 0; i < n; ++i) {
            if (off + kRec > block_size - 8) {
                idx = block ? *reinterpret_cast<const uint16_t*>(block + E.evq_next) : 0;
                block = idx ? base + (idx - 1) * block_size : nullptr;
                off = 0;
            }
            if (!block) return false;
            if (*count < cap) out[*count] = *reinterpret_cast<const uint32_t*>(block + 8 + off + 8);
            off += kRec;
            if (*count < cap) ++*count;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kc

namespace kc {

bool move_bytes(void* dst, const void* src, size_t n) {
    if (!dst || !src) return false;
    __try {
        std::memmove(dst, src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_list(const void* sys, uint8_t** data, uint64_t* count) {
    if (!sys || E.hits_pool < 0 || E.hits_count < 0) return false;
    __try {
        const auto* p = static_cast<const uint8_t*>(sys);
        *data = *reinterpret_cast<uint8_t* const*>(p + E.hits_pool);
        *count = *reinterpret_cast<const uint64_t*>(p + E.hits_count);
        return *count == 0 || *data != nullptr;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool write_count(void* sys, uint64_t count) {
    __try {
        *reinterpret_cast<uint64_t*>(static_cast<uint8_t*>(sys) + E.hits_count) = count;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool read_u32(const void* p, uint32_t* out) {
    if (!p) return false;
    __try {
        *out = *static_cast<const uint32_t*>(p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

}  // namespace kc
