// SPDX-License-Identifier: MIT
// kiln.items: container names (docs/re/items.md section 14). The Rename button stores the text in the game's string
// table, the `CustomPlayerStringsSystem` singleton (a HashMap<u64 id, record*>, the same map type as the item registry's),
// and puts the id, the FNV-1a 64 of the text, into the object's `CustomPlayerString` component, which only a renamed
// object carries. A name is therefore two reads: the component, then the table.
//
// shroudlink finds the same records by scanning the process heap for their layout (names.rs, MIT, bjck/shroudlink
// v0.8.0). This service walks the table instead and keeps shroudlink's layout test, plus the hash check the ids allow.
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "engine.h"
#include "names.h"
#include "xfer.h"

namespace kitems {
namespace {

template <typename T>
T rdn(const void* p, int64_t off) {
    T v;
    std::memcpy(&v, static_cast<const uint8_t*>(p) + off, sizeof v);
    return v;
}

constexpr size_t kRecordBytes = 0x28 + 0x100 + 0x40;  // the record, with room for the offsets parse_name_record tries
constexpr size_t kRecordOffsets = 0x38;                // 8-aligned offsets 0..0x38 inside what the table points at

std::atomic<uintptr_t> g_table_world{0};
std::atomic<uintptr_t> g_table{0};  // the singleton, for g_table_world
std::atomic<uint32_t> g_found{0};
std::atomic<bool> g_layout_warned{false};
std::atomic<bool> g_layout_logged{false};
std::mutex g_names_detail_mu;
char g_names_detail[160] = "";

void set_detail(const char* s) {
    std::lock_guard lock(g_names_detail_mu);
    std::snprintf(g_names_detail, sizeof g_names_detail, "%s", s);
}

uintptr_t table_for(uintptr_t world) {
    if (g_table_world.load(std::memory_order_acquire) == world) return g_table.load(std::memory_order_acquire);
    const KilnApi* api = items_api();
    if (!api || !api->on_tick_thread()) return 0;  // the singleton map is looked up on the tick thread, once per world
    const uintptr_t t = api->singleton(world, KilnStr{"CustomPlayerStringsSystem", 25});
    g_table.store(t, std::memory_order_release);
    g_table_world.store(world, std::memory_order_release);
    if (!t) set_detail("the CustomPlayerStringsSystem singleton is not in this world");
    return t;
}

// 0 found, 1 no such name, 2 layout / table problem
int lookup_impl(uintptr_t world, KilnEntity entity, char* buf, uint32_t capacity, uint32_t* len, uint32_t* total) {
    PtrSize ps{};
    E.get_component(&ps, world, kHashCustomPlayerString, entity);
    if (!ps.ptr || ps.size < 8) return 1;
    const uint64_t key = rdn<uint64_t>(ps.ptr, 0);
    if (key == 0) return 1;
    const uintptr_t strings = table_for(world);
    if (!strings) return 2;
    // the table's HashMap sits where its bitmap pointer is, minus the map's own bitmap offset
    const uint8_t* map = reinterpret_cast<const uint8_t*>(strings) + (E.strings_bitmap - E.hmap_bits);
    const uint64_t cap = rdn<uint64_t>(map, E.hmap_capacity);
    const uint32_t count = rdn<uint32_t>(map, E.hmap_count);
    const auto* bits = rdn<const uint64_t*>(map, E.hmap_bits);
    const auto* keys = rdn<const uint64_t*>(map, E.hmap_keys);
    const auto* values = rdn<const uint8_t* const*>(map, E.hmap_values);
    if (!bits || !keys || !values || (cap & (cap - 1)) != 0 || count > cap || cap == 0 || cap > (1ull << 24)) return 2;
    // open addressing with linear probing from the low bits of the id, as the game's own lookup
    uint64_t idx = static_cast<uint32_t>(key) & (cap - 1);
    for (uint64_t probe = 0; probe < cap; ++probe) {
        if (!((bits[idx >> 6] >> (idx & 63)) & 1)) return 1;  // an empty slot ends the probe: the id is not in the table
        if (keys[idx] == key) {
            const uint8_t* rec = values[idx];
            if (!rec) return 2;
            uint8_t copy[kRecordBytes];
            std::memcpy(copy, rec, sizeof copy);
            uint32_t n = 0;
            size_t offset = 0;
            if (!parse_name_record(copy, sizeof copy, reinterpret_cast<uintptr_t>(rec), key, kRecordOffsets, buf, capacity,
                                   &n, &offset)) {
                if (!g_layout_warned.exchange(true)) {
                    char dump[3 * 0x58 + 1];
                    for (int i = 0; i < 0x58; ++i) std::snprintf(dump + 3 * i, 4, "%02x ", copy[i]);
                    items_log(KILN_LOG_WARN, "container names: the string table's record for id %016llx is not the layout this "
                              "build was checked with; first bytes: %s", static_cast<unsigned long long>(key), dump);
                    set_detail("the string table's records do not match the layout kiln.items knows");
                }
                return 2;
            }
            *len = capacity ? (n < capacity ? n : capacity - 1) : 0;
            *total = n;
            if (!g_layout_logged.exchange(true))
                items_log(KILN_LOG_INFO, "container names: string table at %p, %u of %llu slots used; the record for id %016llx is "
                          "at +0x%zx of what the table points at (text %u bytes)", reinterpret_cast<const void*>(strings), count,
                          static_cast<unsigned long long>(cap), static_cast<unsigned long long>(key), offset, n);
            return 0;
        }
        idx = (idx + 1 < cap) ? idx + 1 : 0;
    }
    return 1;
}

}  // namespace

KilnResult name_of(uintptr_t world, KilnEntity entity, char* buf, uint32_t capacity, uint32_t* out_len) {
    if (out_len) *out_len = 0;
    if (!E.names_ok || !E.read_ok || !world) return KILN_ERR_UNAVAILABLE;
    uint32_t len = 0, total = 0;
    int rc = 2;
    __try {
        rc = lookup_impl(world, entity, buf, capacity, &len, &total);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        rc = 2;
        set_detail("reading the string table faulted");
    }
    if (rc == 1) return KILN_ERR_NOT_FOUND;
    if (rc != 0) return KILN_ERR_UNAVAILABLE;
    g_found.fetch_add(1, std::memory_order_relaxed);
    if (out_len) *out_len = total;
    return total > len ? KILN_ERR_BUFFER_TOO_SMALL : KILN_OK;
}

void names_world_closed() {
    g_table_world.store(0);
    g_table.store(0);
}

uint32_t names_found() { return g_found.load(); }

void names_detail(char* out, uint32_t cap) {
    std::lock_guard lock(g_names_detail_mu);
    std::snprintf(out, cap, "%s", g_names_detail);
}

// ---- lab: what the rename handler does, minus the client ----
// The game's handler for a client's rename (message dispatcher, docs/re/items.md section 14) calls set_string(out,
// strings, oldId, &text) for the id and then either writes the id into the entity's component or, when the object has
// none yet, adds the component with it. This does the same on the tick thread, for the lab's named-chest check.
namespace {
struct TextView {
    const char* ptr;
    uint64_t len;
};
using SetStringFn = void*(__fastcall*)(void* out, uintptr_t strings, uint64_t old_id, const TextView* text);
using AddComponentFn = void*(__fastcall*)(uintptr_t world, uint32_t entity, uint32_t hash, const TextView* data);

bool set_for_lab_impl(uintptr_t world, KilnEntity entity, const char* text, char* msg, uint32_t cap) {
    const uintptr_t strings = table_for(world);
    if (!strings || !E.set_string || !E.add_component) {
        std::snprintf(msg, cap, "no string table or no set_string / AddComponent on this build");
        return false;
    }
    alignas(16) uint8_t out[32] = {};
    const TextView tv{text, std::strlen(text)};
    reinterpret_cast<SetStringFn>(E.set_string)(out, strings, 0, &tv);
    if (out[0] != 0) {
        std::snprintf(msg, cap, "set_string refused the text (error 0x%02x)", out[0]);
        return false;
    }
    const uint64_t id = rdn<uint64_t>(out, 8);
    if (id != fnv1a64(reinterpret_cast<const uint8_t*>(text), tv.len)) {
        std::snprintf(msg, cap, "set_string returned id %016llx, not the text's FNV-1a 64", static_cast<unsigned long long>(id));
        return false;
    }
    PtrSize ps{};
    E.get_component(&ps, world, kHashCustomPlayerString, entity);
    if (ps.ptr && ps.size >= 8) {
        std::memcpy(ps.ptr, &id, 8);
    } else {
        const TextView data{reinterpret_cast<const char*>(&id), 8};
        reinterpret_cast<AddComponentFn>(E.add_component)(world, entity, kHashCustomPlayerString, &data);
    }
    std::snprintf(msg, cap, "named entity %u \"%s\" (id %016llx)", entity, text, static_cast<unsigned long long>(id));
    return true;
}
}  // namespace

bool name_set_for_lab(uintptr_t world, KilnEntity entity, const char* text, char* msg, uint32_t cap) {
    __try {
        return set_for_lab_impl(world, entity, text, msg, cap);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        std::snprintf(msg, cap, "naming faulted");
        return false;
    }
}

}  // namespace kitems
