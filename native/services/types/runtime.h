// SPDX-License-Identifier: MIT
// Portable bounds/policy helpers shared by the service and hostile-memory regression tests.
#pragma once
#include "scan.h"
#include <cmath>
#include <set>
#include <map>

namespace ktypes {
inline bool add_address(uint64_t a, uint64_t b, uint64_t& out) {
    if (b > UINT64_MAX - a) return false;
    out = a + b;
    return true;
}
inline bool can_disable(std::string_view name) {
    // Deliberately small: no scheduler, save, network, physics, init, or structural systems.
    return name == "stamina_depletion_sprint" || name == "fall_damage_infliction";
}
inline bool can_disable(const std::vector<System>& systems, size_t index) {
    return index < systems.size() && can_disable(systems[index].name) &&
        std::count_if(systems.begin(), systems.end(), [&](const System& s) { return s.name == systems[index].name; }) == 1 &&
        std::count_if(systems.begin(), systems.end(), [&](const System& s) { return s.fn == systems[index].fn; }) == 1;
}
// Access under the service mutex. A slice carries a generation across the original call, so stop/restart cannot
// move old work into the new profile. Repeated Stop preserves the original stop time.
struct ProfileSession {
    uint64_t generation = 0;
    int64_t start = 0, stop = 0;
    bool active = false;
    void set(bool on, int64_t now) {
        ++generation;
        if (on) { start = now; stop = 0; }
        else if (active) stop = now;
        active = on;
    }
    bool accepts(uint64_t slice_generation) const { return active && slice_generation == generation; }
    double seconds(int64_t now, int64_t frequency) const {
        return start && frequency > 0 ? double(std::max<int64_t>(0, (active ? now : stop) - start)) / double(frequency) : 0;
    }
};

struct SystemOwners {
    std::map<uint32_t, std::set<uint64_t>> disabled;
    bool set(uint64_t owner, uint32_t index, bool enabled) {
        if (enabled) {
            auto it = disabled.find(index);
            if (it != disabled.end()) {
                it->second.erase(owner);
                if (it->second.empty()) disabled.erase(it);
            }
        } else disabled[index].insert(owner);
        return disabled.count(index) != 0;
    }
    template<class IsAlive> bool reap(IsAlive alive) {
        bool changed = false;
        for (auto it = disabled.begin(); it != disabled.end();) {
            for (auto owner = it->second.begin(); owner != it->second.end();) {
                if (!alive(*owner)) { owner = it->second.erase(owner); changed = true; } else ++owner;
            }
            if (it->second.empty()) it = disabled.erase(it); else ++it;
        }
        return changed;
    }
    void release(uint64_t owner) {
        for (auto it = disabled.begin(); it != disabled.end();) {
            it->second.erase(owner);
            if (it->second.empty()) it = disabled.erase(it); else ++it;
        }
    }
};

// A later native runtime may retain our trampoline. Release global state without detaching that live chain.
inline bool displace_runner(bool conflict, bool& displaced, SystemOwners& owners, ProfileSession& profile, int64_t now) {
    if (!conflict || displaced) return false;
    displaced = true;
    owners.disabled.clear();
    profile.set(false, now);
    return true;
}

template<class Reader, class T>
inline bool read_at(const Reader& mem, uint64_t base, uint64_t off, T& value) {
    uint64_t addr;
    return add_address(base, off, addr) && mem.read(addr, &value, sizeof value);
}
// Every table and index is bounded before pointer arithmetic; corrupt/full maps have finite work.
template<class Reader>
int event_slot(const Reader& mem, uint64_t map, uint32_t hash) {
    uint64_t cap, used, keys;
    uint32_t count;
    if (!read_at(mem, map, 0x10, cap) || !read_at(mem, map, 0x54, count) || cap == 0 || cap > 65536 ||
        (cap & (cap - 1)) || count > cap || !read_at(mem, map, 8, used) || !read_at(mem, map, 0x28, keys)) return -2;
    if (!count) return -1;
    for (uint64_t s = hash & (cap - 1), n = 0; n < cap; ++n, s = (s + 1) & (cap - 1)) {
        uint64_t bitmap;
        uint32_t key;
        if (!read_at(mem, used, (s >> 6) * 8, bitmap)) return -2;
        if (!(bitmap >> (s & 63) & 1)) return -1;
        if (!read_at(mem, keys, s * 4, key)) return -2;
        if (key == hash) return static_cast<int>(s);
    }
    return -1;
}
// Exact stream size and full poster extent checked before the first write. The caller supplies a checked payload.
template<class Memory>
int append_event(const Memory& mem, uint64_t stream, uint64_t poster, uint32_t hash, const uint8_t* data, uint32_t size) {
    if (!data || !size || size >= 256) return 1;
    const uint32_t padded = (size + 7) & ~7u;
    uint32_t stride, used;
    uint64_t buf, capacity;
    if (!read_at(mem, stream, 0x14, stride) || stride != padded) return 1;
    if (!poster || !read_at(mem, poster, 0, buf) || !read_at(mem, poster, 8, capacity) ||
        !read_at(mem, poster, 0x10, used) || !buf || capacity > UINT32_MAX || used > capacity) return 2;
    const uint64_t length = 8ull + padded;
    uint64_t at, end_used_addr;
    if (length > capacity - used || !add_address(buf, used, at) || !mem.writable(at, length) ||
        !add_address(poster, 0x10, end_used_addr) || !mem.writable(end_used_addr, 4)) return 2;
    uint8_t record[264]{};
    std::memcpy(record, &hash, 4);
    record[6] = static_cast<uint8_t>(size);
    std::memcpy(record + 8, data, size);
    const uint32_t end = static_cast<uint32_t>(used + length);
    return mem.write(at, record, length) && mem.write(end_used_addr, &end, 4) ? 0 : 3;
}

// Snapshot a bounded page chain before exposing events to callbacks. A corrupt chain delivers no partial batch.
template<class Reader>
bool copy_events(const Reader& mem, uint64_t stream, uint32_t size, std::vector<std::vector<uint8_t>>& copies) {
    uint64_t pool;
    uint32_t count, stride;
    uint16_t page;
    if (!size || size >= 256 || !read_at(mem, stream, 0x30, pool) || !read_at(mem, stream, 0x18, count) || count > 65536 ||
        !read_at(mem, stream, 0x14, stride) || stride != ((size + 7) & ~7u) || !read_at(mem, stream, 0x40, page)) return false;
    if (!count) return true;
    copies.reserve(count);
    std::set<uint16_t> pages;
    uint8_t bytes[0x800];
    uint32_t off = 0x7f8;
    for (uint32_t i = 0; i < count; ++i) {
        if (off + stride > 0x7f8) {
            if (i) std::memcpy(&page, bytes + 2, 2);
            uint64_t address;
            if (!pool || !page || !pages.insert(page).second || !add_address(pool, uint64_t(page - 1) * 0x800, address) ||
                !mem.read(address, bytes, sizeof bytes)) return false;
            off = 0;
        }
        copies.emplace_back(bytes + 8 + off, bytes + 8 + off + size);
        off += stride;
    }
    return true;
}

// Validate a complete event value using only inline layouts. Game-owned strings/arrays/pointers are never injected.
inline bool valid_payload_walk(const Image& im, uint64_t type, const uint8_t* data, size_t length, unsigned depth, size_t& budget) {
    if (!budget || !data || depth > 32 || !is_type(im, type)) return false;
    --budget;
    const uint32_t size = im.d(type + td::size), count = im.d(type + td::count);
    const uint8_t kind = im.b(type + td::kind);
    if (size > length || size > 255 || count > 65536) return false;
    uint64_t value = 0;
    auto scalar = [&](uint32_t want) { if (size != want) return false; std::memcpy(&value, data, size); return true; };
    switch (kind) {
        case 1: return scalar(1) && value <= 1;
        case 2: case 3: case 13: return scalar(1);
        case 4: case 5: case 14: return scalar(2);
        case 6: case 7: case 15: return scalar(4);
        case 8: case 9: case 16: return scalar(8);
        case 10: { float v; if (size != 4) return false; std::memcpy(&v, data, 4); return std::isfinite(v); }
        case 11: { double v; if (size != 8) return false; std::memcpy(&v, data, 8); return std::isfinite(v); }
        case 12: {
            if (size != 1 && size != 2 && size != 4 && size != 8) return false;
            std::memcpy(&value, data, size);
            const uint64_t mask = size == 8 ? UINT64_MAX : (1ull << (size * 8)) - 1;
            for (uint32_t i = 0; i < count; ++i) {
                if (!budget) return false;
                --budget;
                const uint64_t e = enum_entry(im, type, i);
                if (!e) return false;
                const int64_t declared = static_cast<int64_t>(im.q(e + td::enum_value));
                if (size < 8 && (declared < -(1ll << (size * 8 - 1)) || (declared >= 0 && uint64_t(declared) > mask))) return false;
                if ((uint64_t(declared) & mask) == value) return true;
            }
            return count == 0;
        }
        case 17: {
            const uint64_t inner = im.q(type + td::inner);
            return inner && im.d(inner + td::size) == size && valid_payload_walk(im, inner, data, size, depth + 1, budget);
        }
        case 18: {
            const uint64_t base = im.q(type + td::inner);
            if (base && (!is_type(im, base) || im.d(base + td::size) > size || !valid_payload_walk(im, base, data, size, depth + 1, budget))) return false;
            for (uint32_t i = 0; i < count; ++i) {
                const uint64_t e = field_entry(im, type, i);
                if (!e) return false;
                const uint64_t off = im.q(e + td::field_offset);
                if (!valid_payload_walk(im, im.q(e + td::field_type), data + off, size - off, depth + 1, budget)) return false;
            }
            return true;
        }
        case 19: {
            const uint64_t inner = im.q(type + td::inner);
            if (!is_type(im, inner)) return false;
            const uint32_t stride = im.d(inner + td::size);
            if (!stride || size % stride) return false;
            for (uint32_t off = 0; off < size; off += stride)
                if (!valid_payload_walk(im, inner, data + off, stride, depth + 1, budget)) return false;
            return true;
        }
        case 28: case 29: return size == 16;
        default: return false;
    }
}
inline bool valid_payload(const Image& im, uint64_t type, const uint8_t* data, size_t length) {
    size_t budget = 4096;
    return valid_payload_walk(im, type, data, length, 0, budget);
}
} // namespace ktypes
