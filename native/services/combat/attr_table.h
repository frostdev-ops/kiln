// SPDX-License-Identifier: MIT
// kiln.combat attributes: reads the game's attribute resource (keen::AttributeContainerResource) into groups of
// named attribute ids. Portable (no Windows headers), so tests/attr_test.cpp runs the same code on the resource bytes
// that tools/kfc extracts. Evidence: docs/re/combat.md "Attributes".
//
// The resource: `attributes` = BlobArray<BaseAttributeResource>. A group is the attributes of one component (Health,
// Stamina, Mana, BodyHeat, Oxygen, ...): BaseAttributeResource {HashKey32 type; BlobArray<HashKey32> ids;
// BlobArray<AttributeStructure> structure; BlobArray<string> debugNames}. ids[i], debugNames[i] and structure[i] are the
// same slot i of the component's dataStorage; ids[0] is the group's root id (the component's rootId). structure[i].
// calculation is the formula the game re-runs after a write: a BlobArray of u32 commands (op << 16 | argument), e.g.
// Stamina's slot 0 = [Load 0, Load 1, Load 2, Clamp]: current = clamp(current, Stamina_Min, Stamina_Max).
// A BlobArray is {s32 offset from the field's own address, u32 count}; a BlobString {s32 offset, u32 length}.
// The offsets and sizes below are the defaults of svn 1287369; the service replaces them with what kiln.types reflects.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>

namespace kc::attr {

struct Layout {
    uint32_t container_attrs = 0;                      // AttributeContainerResource.attributes
    uint32_t elem_size = 28;                           // BaseAttributeResource
    uint32_t off_type = 0, off_ids = 4, off_structure = 0xC, off_names = 0x14;
    uint32_t id_size = 4, name_size = 8;               // HashKey32, keen::string (a BlobString)
    uint32_t st_size = 16, st_calc = 8;                // AttributeStructure, .calculation
    uint32_t cmd_size = 4;                             // AttributeCommand
    uint32_t op_load = 2, op_clamp = 12;               // keen::AttributeOps Load, Clamp
};

struct Attr {
    std::string name;
    uint32_t id = 0;
};

struct Group {
    std::vector<Attr> attrs;               // slot order
    std::vector<uint32_t> first_formula;   // structure[0].calculation: the current value's formula
    uint32_t root() const { return attrs.empty() ? 0 : attrs[0].id; }
    const std::string& root_name() const { return attrs[0].name; }
    int slot_of(std::string_view name) const {
        for (size_t i = 0; i < attrs.size(); ++i)
            if (attrs[i].name == name) return static_cast<int>(i);
        return -1;
    }
};

namespace detail {
template <class T>
inline bool rd(const uint8_t* d, size_t n, size_t off, T* out) {
    if (off > n || n - off < sizeof(T)) return false;
    std::memcpy(out, d + off, sizeof(T));
    return true;
}
// A BlobArray field at `field`: where its elements start and how many. false when it does not fit the buffer.
inline bool blob(const uint8_t* d, size_t n, size_t field, uint32_t elem, size_t* start, uint32_t* count) {
    int32_t rel = 0;
    uint32_t c = 0;
    if (!rd(d, n, field, &rel) || !rd(d, n, field + 4, &c)) return false;
    if (c == 0) {
        *start = 0, *count = 0;
        return true;
    }
    const int64_t at = static_cast<int64_t>(field) + rel;
    if (at < 0 || static_cast<uint64_t>(at) > n || elem == 0 || c > (n - static_cast<size_t>(at)) / elem) return false;
    *start = static_cast<size_t>(at), *count = c;
    return true;
}
}  // namespace detail

// Parses the resource bytes. false (with `why`) when anything is outside the buffer or the groups are inconsistent.
inline bool parse_container(const uint8_t* d, size_t n, const Layout& L, std::vector<Group>* out, std::string* why) {
    using detail::blob;
    using detail::rd;
    out->clear();
    size_t groups = 0;
    uint32_t group_count = 0;
    if (!blob(d, n, L.container_attrs, L.elem_size, &groups, &group_count)) return *why = "the group array is outside the resource", false;
    for (uint32_t g = 0; g < group_count; ++g) {
        const size_t base = groups + static_cast<size_t>(g) * L.elem_size;
        size_t ids = 0, names = 0, structure = 0;
        uint32_t id_count = 0, name_count = 0, st_count = 0;
        if (!blob(d, n, base + L.off_ids, L.id_size, &ids, &id_count) ||
            !blob(d, n, base + L.off_names, L.name_size, &names, &name_count) ||
            !blob(d, n, base + L.off_structure, L.st_size, &structure, &st_count))
            return *why = "group " + std::to_string(g) + ": an array is outside the resource", false;
        if (id_count == 0 || id_count != name_count || id_count > 255)
            return *why = "group " + std::to_string(g) + ": ids and names differ in number", false;
        Group grp;
        for (uint32_t i = 0; i < id_count; ++i) {
            Attr a;
            if (!rd(d, n, ids + static_cast<size_t>(i) * L.id_size, &a.id)) return *why = "an id is outside the resource", false;
            const size_t field = names + static_cast<size_t>(i) * L.name_size;
            int32_t rel = 0;
            uint32_t len = 0;
            if (!rd(d, n, field, &rel) || !rd(d, n, field + 4, &len)) return *why = "a name is outside the resource", false;
            const int64_t at = static_cast<int64_t>(field) + rel;
            if (at < 0 || static_cast<uint64_t>(at) > n || len > 256 || len > n - static_cast<size_t>(at))
                return *why = "a name is outside the resource", false;
            a.name.assign(reinterpret_cast<const char*>(d + at), len);
            grp.attrs.push_back(std::move(a));
        }
        if (st_count >= 1) {
            size_t calc = 0;
            uint32_t calc_count = 0;
            if (!blob(d, n, structure + L.st_calc, L.cmd_size, &calc, &calc_count))
                return *why = "a formula is outside the resource", false;
            for (uint32_t i = 0; i < calc_count; ++i) {
                uint32_t cmd = 0;
                rd(d, n, calc + static_cast<size_t>(i) * L.cmd_size, &cmd);
                grp.first_formula.push_back(cmd);
            }
        }
        out->push_back(std::move(grp));
    }
    return true;
}

// What kiln.combat exposes: one pair per kind. The names are the attribute resource's debug names.
struct KindSpec {
    uint32_t kind;
    const char* root;  // the group's first attribute, which is also the component's name (keen::ecs::<root>)
};
inline constexpr KindSpec kKinds[] = {
    {1, "Health"}, {2, "Stamina"}, {3, "Mana"}, {4, "BodyHeat"}, {5, "Oxygen"},
};

struct Pair {
    uint32_t kind = 0;
    std::string root;
    uint32_t root_id = 0;
    uint32_t cur_id = 0, min_id = 0, max_id = 0;
    int cur_slot = 0, min_slot = -1, max_slot = -1;
    bool game_clamp = false;            // the current value's formula is Clamp(current, min, max)
    std::vector<Attr> attrs;            // every attribute of the component, in slot order (slot = index)
};

// The pair of `spec` in the parsed groups: the group whose first attribute is `root`, with its `<root>_Min` and
// `<root>_Max`. game_clamp is true when the group's first formula is exactly [Load cur, Load min, Load max, Clamp].
inline bool resolve_pair(const std::vector<Group>& groups, const KindSpec& spec, const Layout& L, Pair* out, std::string* why) {
    for (const Group& g : groups) {
        if (g.attrs.empty() || g.attrs[0].name != spec.root) continue;
        Pair p;
        p.kind = spec.kind;
        p.root = spec.root;
        p.root_id = g.root();
        p.cur_id = g.attrs[0].id;
        p.cur_slot = 0;
        p.attrs = g.attrs;
        const int mx = g.slot_of(std::string(spec.root) + "_Max");
        const int mn = g.slot_of(std::string(spec.root) + "_Min");
        if (mx < 0) return *why = std::string(spec.root) + ": no maximum attribute", false;
        p.max_slot = mx, p.max_id = g.attrs[mx].id;
        if (mn >= 0) p.min_slot = mn, p.min_id = g.attrs[mn].id;
        const auto& f = g.first_formula;
        auto cmd = [&](uint32_t op, int slot) { return (op << 16) | static_cast<uint32_t>(slot & 0xFFFF); };
        p.game_clamp = f.size() == 4 && f[0] == cmd(L.op_load, 0) && mn >= 0 && f[1] == cmd(L.op_load, mn) &&
                       f[2] == cmd(L.op_load, mx) && (f[3] >> 16) == L.op_clamp;
        *out = p;
        return true;
    }
    return *why = std::string(spec.root) + ": no such attribute group", false;
}

}  // namespace kc::attr
