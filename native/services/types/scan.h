// SPDX-License-Identifier: MIT
// kiln.types: finds the game's static type table and its ECS system descriptors in an image, by data alone.
// Portable (no Windows headers), so types_test.cpp runs the same code on Linux over the exe file mapped by hand.
//
// The type table (layout documented by Shrouded Engine Tweaks, MIT; this is Kiln's own code): a .rdata array of
// TypeMetadata pointers, preceded nowhere and followed nowhere, found because the descriptors of "BlobString" and
// "uint32" sit next to each other at its start; the qword pointing at the start begins a {TypeMetadata**, u64 count}
// header, followed by {"ecs.component", list, count} and {"ecs.event", list, count}.
// TypeMetadata: +0x00 name {ptr,len}, +0x10 Impact name {ptr,len} ("ecs.Health": the name Impact scripts and
// ImpactVariable.type use, docs/re/impact.md), +0x20 qualified name {ptr,len}, +0x38 inner (base class / element), +0x40 u32
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
#include <unordered_set>
#include <limits>
#include <vector>

namespace ktypes {

struct Range {
    uint64_t lo = 0, hi = 0;  // VAs, [lo, hi)
    bool has(uint64_t va, uint64_t n = 1) const { return lo <= hi && va >= lo && va <= hi && n <= hi - va; }
};

// An image as seen through `mem`: VA v is at mem + (v - base). The live service owns a guarded snapshot.
struct Image {
    const uint8_t* mem = nullptr;
    uint64_t base = 0;
    Range text, rdata;
    Range all;  // the whole image: some descriptors (and their strings) live in .data
    // Optional live-process page check. File-mapped tests need only the image extent.
    bool (*readable)(uint64_t, uint64_t) = nullptr;
    bool has(uint64_t va, uint64_t n = 1) const {
        return mem && all.lo == base && all.has(va, n) && (!readable || readable(va, n));
    }
    bool valid() const {
        return all.lo == base && all.lo < all.hi && text.lo < text.hi && rdata.lo < rdata.hi &&
               has(text.lo, text.hi - text.lo) && has(rdata.lo, rdata.hi - rdata.lo);
    }
    const uint8_t* at(uint64_t va) const { return has(va, 0) ? mem + (va - base) : nullptr; }
    template<class T> T read(uint64_t va) const {
        T v{};
        if (has(va, sizeof v)) std::memcpy(&v, mem + (va - base), sizeof v);
        return v;
    }
    uint64_t q(uint64_t va) const { return read<uint64_t>(va); }
    uint32_t d(uint64_t va) const { return read<uint32_t>(va); }
    uint16_t w(uint64_t va) const { return read<uint16_t>(va); }
    uint8_t b(uint64_t va) const { return read<uint8_t>(va); }
};

inline uint32_t fnv1a(std::string_view s) {
    uint32_t h = 0x811c9dc5u;
    for (unsigned char c : s) h = (h ^ c) * 0x01000193u;
    return h;
}

namespace td {  // TypeMetadata offsets
constexpr uint64_t name = 0x00, impact = 0x10, qname = 0x20, inner = 0x38, size = 0x40, align = 0x44, count = 0x48,
                   kind = 0x4C, flags = 0x4D, hash = 0x50, fields = 0x58, enums = 0x60, defaults = 0x70, bytes = 0x90;
constexpr uint64_t field_stride = 0x30, field_type = 0x10, field_offset = 0x18;
constexpr uint64_t enum_stride = 0x28, enum_value = 0x10;
}  // namespace td

// A {ptr,len} string in .rdata, or empty when it does not look like one.
inline std::string_view str_at(const Image& im, uint64_t va) {
    if (!im.has(va, 16)) return {};
    const uint64_t p = im.q(va), n = im.q(va + 8);
    if (n == 0 || n > 4096 || !im.has(p, n)) return {};
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
    if (!im.valid()) return 0;
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
    if (!im.valid() || im.rdata.lo > UINT64_MAX - 7) return out;
    for (uint64_t a = (im.rdata.lo + 7) & ~7ull; im.rdata.has(a, 8); a += 8)
        if (im.q(a) == v) out.push_back(a);
    return out;
}

inline bool is_type(const Image& im, uint64_t t) {
    if (!im.has(t, td::bytes)) return false;
    const std::string_view qn = str_at(im, t + td::qname);
    return !qn.empty() && fnv1a(qn) == im.d(t + td::hash);
}

// Finds and checks the table. Checks: every entry is a descriptor whose hash is FNV-1a of its qualified name, the
// count is plausible, and the ECS lists are named as expected.
inline TypeTable find_type_table(const Image& im) {
    TypeTable r;
    if (!im.valid()) return r.error = "invalid image ranges", r;
    const uint64_t s_blob = find_cstr(im, "BlobString"), s_u32 = find_cstr(im, "uint32");
    if (!s_blob || !s_u32) return r.error = "the anchor names are not in .rdata", r;
    std::vector<uint64_t> d_blob, d_u32;  // descriptors: their name pointer (offset 0) is the string
    for (uint64_t a : qwords_equal(im, s_blob)) if (is_type(im, a)) d_blob.push_back(a);
    for (uint64_t a : qwords_equal(im, s_u32)) if (is_type(im, a)) d_u32.push_back(a);
    // The ECS lists follow the header: {"ecs.component", len}, {list, count}, {"ecs.event", len}, {list, count}.
    auto list = [&](uint64_t name_at, std::string_view want, uint64_t& va, uint32_t& n) {
        va = 0;
        n = 0;
        if (!im.has(name_at, 32) || str_at(im, name_at) != want) return;
        const uint64_t l = im.q(name_at + 16), c = im.q(name_at + 24);
        if (c == 0 || c > 10000 || !im.has(l, c * 8)) return;
        for (uint64_t i = 0; i < c; ++i) if (!is_type(im, im.q(l + 8 * i))) return;
        va = l;
        n = static_cast<uint32_t>(c);
    };
    // Several qwords may point at the table start; the header is the one followed by the ECS lists (else any one with
    // a plausible count).
    for (uint64_t db : d_blob) {
        for (uint64_t at : qwords_equal(im, db)) {
            if (!im.rdata.has(at, 16)) continue;
            const uint64_t next = im.q(at + 8);
            if (std::find(d_u32.begin(), d_u32.end(), next) == d_u32.end()) continue;
            for (uint64_t h : qwords_equal(im, at)) {
                if (!im.rdata.has(h, 16)) continue;
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
    std::unordered_set<uint64_t> entries;
    for (uint32_t i = 0; i < r.count; ++i) {
        const uint64_t t = im.q(r.table + 8ull * i);
        if (!is_type(im, t)) {
            TypeTable bad;
            bad.error = "type table entry " + std::to_string(i) + " is not a type descriptor";
            return bad; // never expose ECS lists from a rejected registry
        }
        entries.insert(t);
    }
    auto keep_list = [&](uint64_t& list, uint32_t& count) {
        for (uint32_t i = 0; i < count; ++i)
            if (!entries.count(im.q(list + 8ull * i))) { list = 0; count = 0; break; }
    };
    keep_list(r.components, r.component_count);
    keep_list(r.events, r.event_count);
    return r;
}

inline bool scalar_layout(uint8_t kind, uint32_t size) {
    switch (kind) {
        case 1: case 2: case 3: case 13: return size == 1;
        case 4: case 5: case 14: return size == 2;
        case 6: case 7: case 10: case 15: return size == 4;
        case 8: case 9: case 11: case 16: return size == 8;
        case 12: return size == 1 || size == 2 || size == 4 || size == 8;
        case 28: case 29: return size == 16;
        default: return kind >= 17 && kind <= 27;
    }
}
template<class Map, class Key>
inline void unique_insert(Map& map, const Key& key, uint64_t value) {
    auto [it, fresh] = map.emplace(key, value);
    if (!fresh && it->second != value) it->second = 0;
}

// Validate each member on access. Metadata can describe dynamic/opaque layouts, but it may never authorize an
// out-of-object read/write. The multiplication and addition are checked before forming a virtual address.
inline uint64_t field_entry(const Image& im, uint64_t t, uint32_t index) {
    if (!is_type(im, t) || im.b(t + td::kind) != 18 || index >= im.d(t + td::count)) return 0;
    const uint64_t p = im.q(t + td::fields), delta = uint64_t(index) * td::field_stride;
    if (p > UINT64_MAX - delta || !im.has(p + delta, td::field_stride)) return 0;
    const uint64_t e = p + delta, ft = im.q(e + td::field_type), off = im.q(e + td::field_offset);
    if (!is_type(im, ft) || str_at(im, e).empty() || off > im.d(t + td::size) ||
        im.d(ft + td::size) > im.d(t + td::size) - off) return 0;
    return e;
}
inline uint64_t enum_entry(const Image& im, uint64_t t, uint32_t index) {
    if (!is_type(im, t) || im.b(t + td::kind) < 12 || im.b(t + td::kind) > 16 ||
        index >= im.d(t + td::count)) return 0;
    const uint64_t p = im.q(t + td::enums), delta = uint64_t(index) * td::enum_stride;
    return p <= UINT64_MAX - delta && im.has(p + delta, td::enum_stride) && !str_at(im, p + delta).empty() ? p + delta : 0;
}

// ---- Impact names (kiln.types type_impact_name, capability types.impact_names) ----
// A descriptor's Impact name is the {ptr,len} at +0x10, read through the same bounded reader as the other names.
enum class ImpactField { empty, valid, malformed };
inline ImpactField impact_name_at(const Image& im, uint64_t t, std::string_view& out) {
    out = {};
    if (!im.has(t + td::impact, 16)) return ImpactField::malformed;
    const uint64_t p = im.q(t + td::impact), n = im.q(t + td::impact + 8);
    if (n == 0) return ImpactField::empty;  // {0,0} or {ptr,0}: the type has no Impact name
    out = str_at(im, t + td::impact);
    return out.empty() || p == 0 ? (out = {}, ImpactField::malformed) : ImpactField::valid;
}

// The field holds Impact names on this build when at least 90 % of the table's types carry a readable one and some of
// them differ from the short name (else the field is something else, or a copy of the short name). A malformed entry
// of one type does not turn the field off; that type answers "no Impact name" (KILN_ERR_NOT_FOUND).
struct ImpactNames {
    bool ok = false;
    uint32_t valid = 0, empty = 0, malformed = 0, differs = 0;
    uint32_t distinct = 0, shared = 0;  // distinct names; names more than one type carries
    std::vector<std::pair<std::string_view, uint32_t>> index;  // (name, table index), sorted ordinally: the multimap
    std::string why;  // why not ok, "" when ok
    // Every table index carrying `name` exactly (ordinal), in the game's order.
    std::vector<uint32_t> all(std::string_view name) const {
        std::vector<uint32_t> r;
        auto lo = std::lower_bound(index.begin(), index.end(), std::pair<std::string_view, uint32_t>{name, 0});
        for (; lo != index.end() && lo->first == name; ++lo) r.push_back(lo->second);
        return r;
    }
    // The one table index carrying `name`; -1 when none or more than one.
    int64_t single(std::string_view name) const {
        const auto r = all(name);
        return r.size() == 1 ? int64_t(r[0]) : -1;
    }
};
inline ImpactNames index_impact_names(const Image& im, const TypeTable& t) {
    ImpactNames r;
    if (!t.error.empty() || !t.count) return r.why = "the type table was not found", r;
    r.index.reserve(t.count);
    for (uint32_t i = 0; i < t.count; ++i) {
        const uint64_t d = im.q(t.table + 8ull * i);
        std::string_view s;
        switch (impact_name_at(im, d, s)) {
            case ImpactField::empty: ++r.empty; break;
            case ImpactField::malformed: ++r.malformed; break;
            case ImpactField::valid:
                ++r.valid;
                r.differs += s != str_at(im, d + td::name);
                r.index.emplace_back(s, i);
                break;
        }
    }
    std::sort(r.index.begin(), r.index.end());
    for (size_t i = 0; i < r.index.size();) {
        size_t j = i + 1;
        while (j < r.index.size() && r.index[j].first == r.index[i].first) ++j;
        ++r.distinct;
        r.shared += j - i > 1;
        i = j;
    }
    if (uint64_t(r.valid) * 10 < uint64_t(t.count) * 9)
        r.why = "the Impact-name field did not validate on this build: " + std::to_string(r.valid) + " of " +
                std::to_string(t.count) + " types carry a readable one (" + std::to_string(r.malformed) + " malformed)";
    else if (!r.differs)
        r.why = "the Impact-name field did not validate on this build: no Impact name differs from its type's short name";
    r.ok = r.why.empty();
    if (!r.ok) r.index.clear();
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

// Every distinct descriptor, sorted by name/function. Conflicting names remain visible so neither the name nor
// an aliased update function can be disabled by pretending that only the first descriptor exists.
inline std::vector<System> scan_systems(const Image& im) {
    std::vector<System> out;
    if (!im.valid() || im.rdata.lo > UINT64_MAX - 7) return out;
    for (uint64_t a = (im.rdata.lo + 7) & ~7ull; im.rdata.has(a, 24); a += 8) {
        const uint64_t p = im.q(a), n = im.q(a + 8), fn = im.q(a + 16);
        if (n == 0 || n > 127 || !im.rdata.has(p, n + 1) || !im.text.has(fn) || im.b(p + n) != 0) continue;
        const std::string_view s(reinterpret_cast<const char*>(im.at(p)), static_cast<size_t>(n));
        if (system_name(s)) out.push_back({s, fn});
    }
    std::stable_sort(out.begin(), out.end(), [](const System& x, const System& y) { return x.name < y.name || (x.name == y.name && x.fn < y.fn); });
    out.erase(std::unique(out.begin(), out.end(), [](const System& x, const System& y) { return x.name == y.name && x.fn == y.fn; }), out.end());
    return out;
}

}  // namespace ktypes
