// SPDX-License-Identifier: MIT
// kiln.types: finds the game's static type table and its ECS system descriptors in an image, by data alone.
// Portable (no Windows headers), so types_test.cpp runs the same code on Linux over the exe file mapped by hand.
//
// The type table (layout documented by Shrouded Engine Tweaks, MIT; this is Kiln's own code): a .rdata array of
// TypeMetadata pointers, preceded nowhere and followed nowhere, found because the descriptors of "BlobString" and
// "uint32" sit next to each other at its start; the qword pointing at the start begins a {TypeMetadata**, u64 count}
// header, followed by {"ecs.component", list, count} and {"ecs.event", list, count}.
// TypeMetadata: +0x00 name {ptr,len}, +0x20 qualified name {ptr,len}, +0x38 inner (base class / element), +0x40 u32
// size, +0x44 u16 alignment, +0x48 u32 field count, +0x4C u8 kind, +0x4D u8 flags, +0x50 u32 FNV-1a(qualified name),
// +0x58 fields (stride 0x30: name {ptr,len}, type +0x10, offset u64 +0x18), +0x60 enum values (stride 0x28: name
// {ptr,len}, value u64 +0x10), +0x70 default value {ptr,len}.
// Systems: .rdata descriptors {char* name, u64 len, fn}: name inside .rdata and NUL-terminated, lower_snake_case,
// fn inside .text.
#pragma once
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace ktypes {

struct Range {
    uint64_t lo = 0, hi = 0;  // VAs, [lo, hi)
    bool has(uint64_t va, uint64_t n = 1) const { return va >= lo && va <= hi && n <= hi - va; }
};

// An image as seen through `mem`: VA v is at mem + (v - base). In the game, mem == base.
struct Image {
    const uint8_t* mem = nullptr;
    uint64_t base = 0;
    Range text, rdata;
    Range all;  // the whole image: some descriptors (and their strings) live in .data
    const uint8_t* at(uint64_t va) const { return mem + (va - base); }
    uint64_t q(uint64_t va) const { uint64_t v; std::memcpy(&v, at(va), 8); return v; }
    uint32_t d(uint64_t va) const { uint32_t v; std::memcpy(&v, at(va), 4); return v; }
    uint16_t w(uint64_t va) const { uint16_t v; std::memcpy(&v, at(va), 2); return v; }
    uint8_t b(uint64_t va) const { return *at(va); }
};

inline uint32_t fnv1a(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}

namespace td {  // TypeMetadata offsets
constexpr uint64_t name = 0x00, qname = 0x20, inner = 0x38, size = 0x40, align = 0x44, count = 0x48, kind = 0x4C,
                   flags = 0x4D, hash = 0x50, fields = 0x58, enums = 0x60, defaults = 0x70, bytes = 0x90;
constexpr uint64_t field_stride = 0x30, field_type = 0x10, field_offset = 0x18;
constexpr uint64_t enum_stride = 0x28, enum_value = 0x10;
}  // namespace td

// A {ptr,len} string in .rdata, or empty when it does not look like one.
inline std::string_view str_at(const Image& im, uint64_t va) {
    if (!im.all.has(va, 16)) return {};
    const uint64_t p = im.q(va), n = im.q(va + 8);
    if (n == 0 || n > 4096 || !im.all.has(p, n)) return {};
    return {reinterpret_cast<const char*>(im.at(p)), static_cast<size_t>(n)};
}

struct TypeTable {
    uint64_t table = 0, header = 0;  // VAs
    uint32_t count = 0;
    uint64_t components = 0, events = 0;  // VAs of the pointer lists
    uint32_t component_count = 0, event_count = 0;
    std::string error;  // why not found, or "" when found and checked
};

// The VA of the first exact, NUL-delimited occurrence of s in .rdata (0 when none).
inline uint64_t find_cstr(const Image& im, std::string_view s) {
    const uint8_t* lo = im.at(im.rdata.lo);
    const uint8_t* hi = im.at(im.rdata.hi);
    std::string pat;
    pat.push_back('\0');
    pat.append(s);
    pat.push_back('\0');
    auto it = std::search(lo, hi, pat.begin(), pat.end());
    return it == hi ? 0 : im.rdata.lo + static_cast<uint64_t>(it - lo) + 1;
}

// Every 8-aligned qword in .rdata equal to v.
inline std::vector<uint64_t> qwords_equal(const Image& im, uint64_t v) {
    std::vector<uint64_t> out;
    for (uint64_t a = (im.rdata.lo + 7) & ~7ull; a + 8 <= im.rdata.hi; a += 8)
        if (im.q(a) == v) out.push_back(a);
    return out;
}

inline bool is_type(const Image& im, uint64_t t) {
    if (!im.all.has(t, td::bytes)) return false;
    const std::string_view qn = str_at(im, t + td::qname);
    return !qn.empty() && fnv1a(qn) == im.d(t + td::hash);
}

// Finds and checks the table. Checks: every entry is a descriptor whose hash is FNV-1a of its qualified name, the
// count is plausible, and the ECS lists are named as expected.
inline TypeTable find_type_table(const Image& im) {
    TypeTable r;
    const uint64_t s_blob = find_cstr(im, "BlobString"), s_u32 = find_cstr(im, "uint32");
    if (!s_blob || !s_u32) return r.error = "the anchor names are not in .rdata", r;
    std::vector<uint64_t> d_blob, d_u32;  // descriptors: their name pointer (offset 0) is the string
    for (uint64_t a : qwords_equal(im, s_blob)) if (is_type(im, a)) d_blob.push_back(a);
    for (uint64_t a : qwords_equal(im, s_u32)) if (is_type(im, a)) d_u32.push_back(a);
    // The ECS lists follow the header: {"ecs.component", len}, {list, count}, {"ecs.event", len}, {list, count}.
    auto list = [&](uint64_t name_at, std::string_view want, uint64_t& va, uint32_t& n) {
        va = 0;
        n = 0;
        if (str_at(im, name_at) != want) return;
        const uint64_t l = im.q(name_at + 16), c = im.q(name_at + 24);
        if (c == 0 || c > 10000 || !im.all.has(l, c * 8)) return;
        for (uint64_t i = 0; i < c; ++i) if (!is_type(im, im.q(l + 8 * i))) return;
        va = l;
        n = static_cast<uint32_t>(c);
    };
    // Several qwords may point at the table start; the header is the one followed by the ECS lists (else any one with
    // a plausible count).
    for (uint64_t db : d_blob) {
        for (uint64_t at : qwords_equal(im, db)) {
            const uint64_t next = im.q(at + 8);
            if (std::find(d_u32.begin(), d_u32.end(), next) == d_u32.end()) continue;
            for (uint64_t h : qwords_equal(im, at)) {
                const uint64_t n = im.q(h + 8);
                if (n < 1000 || n > 200000 || !im.rdata.has(at, n * 8)) continue;
                TypeTable c;
                c.table = at;
                c.header = h;
                c.count = static_cast<uint32_t>(n);
                list(h + 0x10, "ecs.component", c.components, c.component_count);
                list(h + 0x30, "ecs.event", c.events, c.event_count);
                if (!r.table || (c.component_count && !r.component_count)) r = c;
            }
        }
    }
    if (!r.table) return r.error = "the type table's start or header was not found", r;
    for (uint32_t i = 0; i < r.count; ++i)
        if (!is_type(im, im.q(r.table + 8ull * i)))
            return r.error = "type table entry " + std::to_string(i) + " is not a type descriptor", r;
    return r;
}

struct System {
    std::string_view name;
    uint64_t fn;  // VA
};

inline bool system_name(std::string_view s) {
    if (s.empty() || s[0] < 'a' || s[0] > 'z') return false;
    for (char c : s)
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
    return true;
}

// Every system descriptor, sorted by name; a name seen twice keeps its first descriptor.
inline std::vector<System> scan_systems(const Image& im) {
    std::vector<System> out;
    for (uint64_t a = (im.rdata.lo + 7) & ~7ull; a + 24 <= im.rdata.hi; a += 8) {
        const uint64_t p = im.q(a), n = im.q(a + 8), fn = im.q(a + 16);
        if (n == 0 || n > 127 || !im.rdata.has(p, n + 1) || !im.text.has(fn) || im.b(p + n) != 0) continue;
        const std::string_view s(reinterpret_cast<const char*>(im.at(p)), static_cast<size_t>(n));
        if (system_name(s)) out.push_back({s, fn});
    }
    std::stable_sort(out.begin(), out.end(), [](const System& x, const System& y) { return x.name < y.name; });
    out.erase(std::unique(out.begin(), out.end(), [](const System& x, const System& y) { return x.name == y.name; }), out.end());
    return out;
}

}  // namespace ktypes
