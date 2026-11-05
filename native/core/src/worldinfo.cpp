// SPDX-License-Identifier: MIT
// Singleton map layout (open addressing; FNV-1a32 of the registered name -> u16 index into the singleton array),
// found in RegisterSingleton; see docs/re/players.md.
#include "worldinfo.h"

#include <atomic>

#include "guard.h"
#include "sigdb.h"

namespace kiln::worldinfo {
namespace {
std::atomic<uintptr_t> g_worlds[KILN_WORLD_KINDS]{};

struct Layout {
    bool ok = false;
    int64_t bits, cap, keys, values, array, stride, object, hash;
};

const Layout& layout() {
    static const Layout l = [] {
        Layout r{};
        r.ok = sigdb::feature("singletons") && sigdb::constant("world.singleton_map.bits", &r.bits) &&
               sigdb::constant("world.singleton_map.capacity", &r.cap) &&
               sigdb::constant("world.singleton_map.keys", &r.keys) &&
               sigdb::constant("world.singleton_map.values", &r.values) &&
               sigdb::constant("world.singletons", &r.array) && sigdb::constant("singleton.stride", &r.stride) &&
               sigdb::constant("singleton.object", &r.object) && sigdb::constant("singleton.hash", &r.hash);
        return r;
    }();
    return l;
}

uint32_t fnv1a(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x1000193u;
    return h;
}

template <class T>
bool rd(T& out, uintptr_t at) {
    return guard::copy(&out, at, sizeof(T));
}
}  // namespace

void set_world(uint32_t kind, uintptr_t world) {
    if (kind < KILN_WORLD_KINDS) g_worlds[kind].store(world, std::memory_order_release);
}
uintptr_t world(uint32_t kind) {
    return kind < KILN_WORLD_KINDS ? g_worlds[kind].load(std::memory_order_acquire) : 0;
}

uintptr_t singleton(uintptr_t world, std::string_view name) {
    const Layout& L = layout();
    if (!L.ok || !world || name.empty()) return 0;
    uint64_t cap = 0;
    uintptr_t bits = 0, keys = 0, values = 0, array = 0;
    if (!rd(cap, world + L.cap) || !rd(bits, world + L.bits) || !rd(keys, world + L.keys) ||
        !rd(values, world + L.values) || !rd(array, world + L.array))
        return 0;
    if (cap == 0 || cap > (1u << 16) || (cap & (cap - 1)) != 0) return 0;
    const uint32_t h = fnv1a(name);
    uint64_t i = h & (cap - 1);
    for (uint64_t n = 0; n < cap; ++n) {
        uint64_t word = 0;
        uint32_t key = 0;
        if (!rd(word, bits + (i >> 6) * 8) || !((word >> (i & 63)) & 1)) return 0;
        if (!rd(key, keys + i * 4)) return 0;
        if (key == h) {
            uint16_t index = 0;
            uint32_t entry_hash = 0;
            uintptr_t object = 0;
            if (!rd(index, values + i * 2)) return 0;
            const uintptr_t e = array + static_cast<uintptr_t>(index) * static_cast<uintptr_t>(L.stride);
            if (!rd(entry_hash, e + L.hash) || entry_hash != h || !rd(object, e + L.object)) return 0;
            return object;
        }
        i = i + 1 < cap ? i + 1 : 0;
    }
    return 0;
}

}  // namespace kiln::worldinfo
