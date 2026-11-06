// SPDX-License-Identifier: MIT
// See terrain.h. Tables grow the way merge.cpp grows them: a new copy of the array at the end, inner offsets recomputed for the new address.
#include "terrain.h"

#include <algorithm>
#include <cctype>
#include <cstring>

namespace kwt {

namespace {

template <class T>
T rd(const uint8_t* p) {
    T v;
    std::memcpy(&v, p, sizeof v);
    return v;
}
template <class T>
void wr(uint8_t* p, T v) {
    std::memcpy(p, &v, sizeof v);
}
void pad4(Bytes* b) {
    while (b->size() % 4) b->push_back(0);
}
bool valid_name(const std::string& s) {
    if (s.empty()) return false;
    for (unsigned char c : s)
        if (!std::isalnum(c) && c != '-' && c != '_' && c != '.') return false;
    return true;
}
std::string name_of(const std::vector<std::string>& names, int v) { return v >= 0 && static_cast<size_t>(v) < names.size() ? names[v] : std::to_string(v); }

// A strength into one terrain config: only what it says.
void apply(uint8_t* c, const ConfigLayout& K, const Strength& s) {
    if (s.hardness) c[K.hardness] = static_cast<uint8_t>(*s.hardness);
    if (s.health) wr<int32_t>(c + K.health, *s.health);
    if (s.breaks) c[K.klass] = static_cast<uint8_t>(*s.breaks);
    if (s.items_per_blocks) wr<uint32_t>(c + K.rate, *s.items_per_blocks);
    Guid g;
    if (s.drops && Guid::parse(*s.drops, &g)) std::memcpy(c + K.item, g.b.data(), 16);
}

// A BlobArray of `elem`-byte elements at `field`, `per_id` bytes per cell id: each placed terrain material takes its look_cell's bytes.
bool copy_by_id(const Bytes& res, uint32_t field, uint32_t elem, uint32_t per_id, const std::vector<Placed>& placed, Bytes* out, const char* what, std::string* why) {
    ArrayView v;
    if (!array_at(res, field, elem, &v) || static_cast<uint64_t>(v.count) * elem < static_cast<uint64_t>(kTerrainLast + 1) * per_id) {
        *why = std::string("the ") + what + " doesn't read as its layout says";
        return false;
    }
    *out = res;
    const size_t base = static_cast<size_t>(v.base - res.data());
    for (const Placed& p : placed) {
        if (!p.m.terrain || p.m.look_cell < 1 || p.m.look_cell > kTerrainLast) continue;
        std::memcpy(out->data() + base + size_t(p.cell) * per_id, res.data() + base + size_t(p.m.look_cell) * per_id, per_id);
    }
    return true;
}

// A render data block's elements have to be what the reflection says, or the bytes mean something else on this build.
bool signature_is(const Bytes& res, uint32_t sig_at, uint32_t want, const char* what, std::string* why) {
    if (static_cast<uint64_t>(sig_at) + 4 > res.size() || rd<uint32_t>(res.data() + sig_at) != want) {
        *why = std::string("the ") + what + "'s data isn't the type this build's reflection names";
        return false;
    }
    return true;
}

}  // namespace

void read_terrain_blocks(const ojson& doc, ModFile* out) {
    auto err = [&](const std::string& t) { out->errors.push_back(out->file + ": " + t); };
    const auto it = doc.find("terrainMaterials");
    if (it == doc.end()) return;
    if (!it->is_object()) {
        err("terrainMaterials: expected block name -> block");
        return;
    }
    for (auto b = it->begin(); b != it->end(); ++b) {
        const std::string label = "block terrainMaterial " + b.key();
        const ojson& j = b.value();
        if (!valid_name(b.key())) {
            err(label + ": a block name takes letters, digits, '-', '_' and '.'");
            continue;
        }
        if (!j.is_object()) {
            err(label + ": expected an object");
            continue;
        }
        std::vector<std::string> errors;
        Material m;
        m.terrain = true;
        m.mod = out->mod;
        m.dir = out->dir;
        m.block = b.key();
        m.definition = j.dump();
        std::string was;
        bool recipe = false;
        for (auto f = j.begin(); f != j.end(); ++f) {
            const std::string& k = f.key();
            if (k == "strength") {
                if (f->is_string()) {
                    const auto s = out->strengths.find(f->get<std::string>());
                    if (s == out->strengths.end()) errors.push_back(label + ", strength: no strength \"" + f->get<std::string>() + "\" in this mod's \"strengths\" sections");
                    else read_strength(s->second, "strength", label, &m.strength, &errors);
                } else {
                    read_strength(*f, "strength", label, &m.strength, &errors);
                }
            } else if (k == "like" || k == "look") {
                if (!f->is_string() || f->get<std::string>().empty()) errors.push_back(label + ", " + k + ": takes a game terrain material: a GameMaterials name or Cell<id>");
                else (k == "like" ? m.like : m.look) = f->get<std::string>();
            } else if (k == "flatten" || k == "sliding") {
                if (!f->is_boolean()) errors.push_back(label + ", " + k + ": takes true or false");
                else (k == "flatten" ? m.flatten : m.sliding) = f->get<bool>();
            } else if (k == "placeable") {
                if (!f->is_boolean()) errors.push_back(label + ", placeable: takes true or false");
                else m.item = f->get<bool>();
            } else if (k == "was") {
                if (!f->is_string()) errors.push_back(label + ", was: takes the block's old name");
                else was = f->get<std::string>();
            } else if (k == "name") {
                if (!f->is_string() || f->get<std::string>().empty()) errors.push_back(label + ", name: takes the name players read");
            } else if (k == "recipe") {
                recipe = true;
            } else if (k == "description" || k == "icon" || k == "comment" || k == "tweak") {
                // the placeable item's fields, Kiln.Blocks' to read
            } else {
                errors.push_back(label + ", " + k + ": is not a field of a terrain material; there are name, description, like, look, strength, flatten, sliding, placeable, icon, recipe, was");
            }
        }
        if (!j.contains("name") && errors.empty()) errors.push_back(label + ", name: is required: what players read");
        if (recipe && !m.item && errors.empty()) errors.push_back(label + ", recipe: makes its item, and it has none: say \"placeable\": true");
        if (m.strength.drops && *m.strength.drops == kDropsOwnBlock && !m.item && errors.empty())
            errors.push_back(label + ", strength.drops: \"block\" is its own item, and it has none: say \"placeable\": true, or name an item");
        if (out->clients != "mod" && errors.empty())
            errors.push_back(label + ": mod " + out->mod + " says \"clients\": \"" + out->clients + "\", and a new terrain material has to be on every player's game. Set \"clients\": \"mod\" in kiln.mod.json");
        if (!errors.empty()) {
            for (const auto& e : errors) err(e);
            continue;
        }
        if (m.like.empty()) m.like = kTerrainLike;
        const std::string key = was.empty() ? b.key() : was;
        m.guid = derive_guid(out->mod, "terrain", key);
        m.item_guid = derive_guid(out->mod, "item", key);
        if (m.item) m.item_id = item_id_of(m.item_guid);
        out->materials.push_back(std::move(m));
    }
}

std::vector<int> free_terrain_ids(const Bytes& world, const Layouts& L, const RegistryInfo& info) {
    std::vector<int> out;
    if (world.size() < size_t{L.world_guids} + 256 * 16) return out;
    for (int id = 1; id <= kTerrainLast; ++id) {
        bool any = false;
        for (int i = 0; i < 16; ++i) any = any || world[L.world_guids + size_t(id) * 16 + i];
        if (!any && !info.terrain_at.count(id)) out.push_back(id);
    }
    return out;
}

Allocation allocate_terrain(std::vector<Material> live, std::vector<Material> kept, const std::vector<int>& free_ids) {
    Allocation a;
    auto by_guid = [](const Material& x, const Material& y) { return x.guid.text() < y.guid.text(); };
    std::sort(live.begin(), live.end(), by_guid);
    std::sort(kept.begin(), kept.end(), by_guid);
    a.room = free_ids.size();
    const size_t holds = std::min(kept.size(), a.room), for_live = a.room - holds;
    size_t at = 0;
    for (size_t i = 0; i < live.size(); ++i) {
        if (i >= for_live) {
            std::string why = "the game has room for " + std::to_string(a.room) + " new terrain materials across all mods";
            if (!kept.empty()) why += ", " + std::to_string(holds) + " of them kept for absent mods (WorldTables.Forget frees one)";
            a.refused.push_back("block terrainMaterial " + live[i].block + " (mod " + live[i].mod + "): refused: " + why);
            continue;
        }
        a.placed.push_back(Placed{std::move(live[i]), free_ids[at++], false});
    }
    for (size_t i = 0; i < kept.size(); ++i) {
        if (at >= free_ids.size()) {
            a.refused.push_back("kept terrain material " + kept[i].guid.text() + " (" + kept[i].block + ", mod " + kept[i].mod + "): no room left: a world's cells of it are lost when it loads");
            continue;
        }
        a.placed.push_back(Placed{std::move(kept[i]), free_ids[at++], false});
    }
    return a;
}

bool merge_terrain_registry(const Bytes& reg, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::vector<RegistryChange>* changes, std::string* why) {
    RegistryInfo info;
    if (!read_registry(reg, L, &info, why)) return false;
    *out = reg;
    const ConfigLayout& T = L.terrain;
    struct New {
        Bytes c;
        size_t list = 0;  // where the like's conditional buff list is, or 0
        uint32_t list_count = 0;
    };
    std::vector<New> added;
    for (const Placed& p : placed) {
        if (!p.m.terrain) continue;
        const auto it = info.terrain_at.find(p.m.like_cell);
        if (it == info.terrain_at.end()) {
            *why = "terrain material " + p.m.block + " (mod " + p.m.mod + ") starts from cell " + std::to_string(p.m.like_cell) + ", which has no terrain config";
            return false;
        }
        const uint8_t* tpl = reg.data() + it->second;
        New n;
        n.c.assign(tpl, tpl + T.stride);
        n.c[T.id] = static_cast<uint8_t>(p.cell);
        const int32_t off = rd<int32_t>(tpl + T.conditional);
        if (off) n.list = it->second + T.conditional + off, n.list_count = rd<uint32_t>(tpl + T.conditional + 4);
        if (p.m.flatten && T.flatten != UINT32_MAX) n.c[T.flatten] = *p.m.flatten ? 1 : 0;
        if (p.m.sliding && T.sliding != UINT32_MAX) n.c[T.sliding] = *p.m.sliding ? 1 : 0;
        apply(n.c.data(), T, p.m.strength);
        Guid loot;
        std::memcpy(loot.b.data(), n.c.data() + T.item, 16);
        std::string flags;
        if (T.flatten != UINT32_MAX && n.c[T.flatten]) flags += ", flattens";
        if (T.sliding != UINT32_MAX && n.c[T.sliding]) flags += ", sliding";
        changes->push_back({"terrain material " + std::to_string(p.cell) + " " + p.m.guid.text() + " " + p.m.block + " (mod " + p.m.mod + ", " + (p.m.live ? "live" : "kept") + "): like " +
                            std::to_string(p.m.like_cell) + ", look " + std::to_string(p.m.look_cell) + ", hardness " + name_of(L.hardness_names, n.c[T.hardness]) + ", health " +
                            std::to_string(rd<int32_t>(n.c.data() + T.health)) + ", breaks " + name_of(L.class_names, n.c[T.klass]) + ", drops " + loot.text() + " per " +
                            std::to_string(rd<uint32_t>(n.c.data() + T.rate)) + " cells" + flags});
        added.push_back(std::move(n));
    }
    if (added.empty()) return true;

    ArrayView old;
    if (!array_at(*out, L.reg_terrain, T.stride, &old)) {
        *why = "the terraforming registry's terrain list doesn't read";
        return false;
    }
    const uint32_t old_count = old.count;
    const size_t old_at = static_cast<size_t>(old.base - out->data());
    pad4(out);
    const size_t at = out->size();
    out->resize(at + (size_t{old_count} + added.size()) * T.stride);
    std::memcpy(out->data() + at, out->data() + old_at, size_t{old_count} * T.stride);
    for (uint32_t i = 0; i < old_count; ++i) {
        const size_t field_old = old_at + size_t{i} * T.stride + T.conditional, field_new = at + size_t{i} * T.stride + T.conditional;
        const int32_t off = rd<int32_t>(out->data() + field_old);
        if (off) wr<int32_t>(out->data() + field_new, static_cast<int32_t>(int64_t(field_old) + off - int64_t(field_new)));
    }
    for (size_t i = 0; i < added.size(); ++i) {
        uint8_t* c = out->data() + at + (old_count + i) * T.stride;
        std::memcpy(c, added[i].c.data(), T.stride);
        const size_t field = static_cast<size_t>(c - out->data()) + T.conditional;
        // the like's buffs on this material too (tar, deadly fog): the same bytes, pointed at from here
        wr<int32_t>(out->data() + field, added[i].list ? static_cast<int32_t>(int64_t(added[i].list) - int64_t(field)) : 0);
        wr<uint32_t>(out->data() + field + 4, added[i].list ? added[i].list_count : 0);
    }
    wr<int32_t>(out->data() + L.reg_terrain, static_cast<int32_t>(at - L.reg_terrain));
    wr<uint32_t>(out->data() + L.reg_terrain + 4, static_cast<uint32_t>(old_count + added.size()));
    return true;
}

bool merge_terrain_pool(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::string* why) {
    *out = game;
    std::vector<uint32_t> ids;
    for (const Placed& p : placed)
        if (p.m.terrain && p.m.live && p.m.item) ids.push_back(p.m.item_id);
    if (ids.empty()) return true;
    ArrayView pools;
    if (!array_at(game, L.pools, L.pool_stride, &pools)) {
        *why = "the material pools don't read as their layout says";
        return false;
    }
    size_t rec = SIZE_MAX;
    for (uint32_t i = 0; i < pools.count && rec == SIZE_MAX; ++i) {
        const size_t r = static_cast<size_t>(pools.base - game.data()) + size_t{i} * L.pool_stride;
        const int32_t off = rd<int32_t>(game.data() + r + L.pool_name);
        const uint32_t len = rd<uint32_t>(game.data() + r + L.pool_name + 4);
        const int64_t at = int64_t(r) + L.pool_name + off;
        if (off && at > 0 && static_cast<uint64_t>(at) + len <= game.size() && std::string(reinterpret_cast<const char*>(game.data() + at), len) == kTerrainPool) rec = r;
    }
    if (rec == SIZE_MAX) {
        *why = std::string("the game has no material pool ") + kTerrainPool;
        return false;
    }
    const size_t field = rec + L.pool_materials;
    ArrayView cur;
    if (!array_at(*out, static_cast<uint32_t>(field), 4, &cur)) {
        *why = "a material pool's list doesn't read";
        return false;
    }
    const uint32_t count = cur.count;
    const size_t cur_at = cur.base ? static_cast<size_t>(cur.base - out->data()) : 0;
    pad4(out);
    const size_t at = out->size();
    out->resize(at + (size_t{count} + ids.size()) * 4);
    if (count) std::memcpy(out->data() + at, out->data() + cur_at, size_t{count} * 4);
    for (size_t i = 0; i < ids.size(); ++i) wr<uint32_t>(out->data() + at + (count + i) * 4, ids[i]);
    wr<int32_t>(out->data() + field, static_cast<int32_t>(at - field));
    wr<uint32_t>(out->data() + field + 4, static_cast<uint32_t>(count + ids.size()));
    return true;
}

bool merge_terrain_vfx(const Bytes& res, const TerrainTables& T, const std::vector<Placed>& placed, Bytes* out, std::string* why) {
    return copy_by_id(res, T.vfx, 4, 4, placed, out, "material particle table", why);
}

bool merge_terrain_blend(const Bytes& res, const TerrainTables& T, const std::vector<Placed>& placed, Bytes* out, std::string* why) {
    return signature_is(res, T.blend_sig, T.blend_type, "terrain blending table", why) && copy_by_id(res, T.blend, 1, 4, placed, out, "terrain blending table", why);
}

bool merge_terrain_simple(const Bytes& res, const TerrainTables& T, const std::vector<Placed>& placed, Bytes* out, std::string* why) {
    // two entries per id: on the surface, underground
    return signature_is(res, T.simple_sig, T.simple_type, "far terrain table", why) && copy_by_id(res, T.simple, 1, 8, placed, out, "far terrain table", why);
}

}  // namespace kwt
