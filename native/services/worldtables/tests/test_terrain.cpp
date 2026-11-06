// SPDX-License-Identifier: MIT
// kiln.worldtables' terrain materials on the host: see run.sh. With a folder of the game's own resources (registry.bin pools.bin world.bin vfx.bin blend.bin
// simple.bin, local data, never in the repo) it also merges two terrain materials into them and writes the results there as merged-*.bin, for
// tools/kfc to decode and encode again (tests/terrain_roundtrip.py).
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>

#include "terrain.h"

using namespace kwt;

namespace {

int g_failed = 0, g_passed = 0;
void check(const char* name, bool ok, const std::string& detail = "") {
    (ok ? g_passed : g_failed)++;
    std::printf("%s %s%s%s\n", ok ? "PASS" : "FAIL", name, detail.empty() ? "" : ": ", detail.c_str());
}

template <class T>
void wr(Bytes& b, size_t at, T v) {
    std::memcpy(b.data() + at, &v, sizeof v);
}
template <class T>
T rd(const Bytes& b, size_t at) {
    T v;
    std::memcpy(&v, b.data() + at, sizeof v);
    return v;
}

// The game's layouts on the current builds (the service reads them from reflection).
Layouts layouts() {
    Layouts L;
    L.reg_terrain = 0, L.reg_building = 8, L.reg_size = 16;
    L.building = {180, 0, 4, 0x14, 0x18, 0x1c, 0x50, 0x64, 0x6c};
    L.terrain = {184, 0, 4, 0x14, 0x18, 0x1c, 0x54, 0x68, UINT32_MAX, 0x2c, 0x2d};
    L.world_guids = 0x1c, L.world_size = 4360;
    L.pools = 0, L.pool_stride = 24, L.pool_materials = 8, L.pool_name = 16;
    L.blueprints = 0, L.bp_stride = 28, L.bp_item = 0, L.bp_data = 16;
    L.hardness_names = {"Soft", "SlightlyHard", "ModeratelyHard", "Hard", "VeryHard", "Unbreakable"};
    L.class_names = {"None", "Wood", "Stone", "Metal"};
    return L;
}
TerrainTables tables() {
    TerrainTables T;
    T.vfx = 0;
    T.blend = 0x04 + 0x0c, T.blend_sig = 0x04 + 0x08, T.blend_type = 0x438ecaa1;
    T.simple = 0x00 + 0x0c, T.simple_sig = 0x00 + 0x08, T.simple_type = 0x23ac6959;
    T.ok = true;
    return T;
}

// A registry with terrain configs for ids 1, 2 and 26 (2 with a conditional buff list past the arrays, as tar's) and one building config.
Bytes registry() {
    const Layouts L = layouts();
    const int ids[] = {1, 2, 26};
    Bytes b(16 + 3 * 184 + 180 + 40, 0);
    wr<int32_t>(b, 0, 16), wr<uint32_t>(b, 4, 3);
    for (int i = 0; i < 3; ++i) {
        const size_t c = 16 + size_t(i) * 184;
        b[c] = static_cast<uint8_t>(ids[i]);
        b[c + L.terrain.item] = static_cast<uint8_t>(0xA0 + i);
        b[c + L.terrain.hardness] = static_cast<uint8_t>(i + 1);
        wr<uint32_t>(b, c + L.terrain.rate, 5);
        wr<int32_t>(b, c + L.terrain.health, 10 * (i + 1));
        b[c + L.terrain.klass] = 2;
        b[c + L.terrain.flatten] = 1;
    }
    const size_t bc = 16 + 3 * 184;
    wr<int32_t>(b, 8, static_cast<int32_t>(bc - 8)), wr<uint32_t>(b, 12, 1);
    b[bc] = 27;  // building 155
    b[bc + 4] = 0xBB;
    const size_t list = bc + 180, field = 16 + 184 + L.terrain.conditional;
    wr<int32_t>(b, field, static_cast<int32_t>(list - field)), wr<uint32_t>(b, field + 4, 2);
    wr<float>(b, list, 0.25f), wr<float>(b, list + 20, 0.75f);
    return b;
}

Bytes pools() {
    // two pools: "BuildingDefaultMaterials" with 1 item, "TerrainMaterials" with 2
    Bytes b(8 + 2 * 24, 0);
    wr<int32_t>(b, 0, 8), wr<uint32_t>(b, 4, 2);
    const auto pool = [&](size_t rec, const std::string& name, std::vector<uint32_t> items) {
        const size_t list = b.size();
        for (uint32_t x : items) b.resize(b.size() + 4), wr<uint32_t>(b, b.size() - 4, x);
        const size_t str = b.size();
        b.insert(b.end(), name.begin(), name.end());
        while (b.size() % 4) b.push_back(0);
        wr<int32_t>(b, rec + 8, static_cast<int32_t>(list - (rec + 8))), wr<uint32_t>(b, rec + 12, static_cast<uint32_t>(items.size()));
        wr<int32_t>(b, rec + 16, static_cast<int32_t>(str - (rec + 16))), wr<uint32_t>(b, rec + 20, static_cast<uint32_t>(name.size()));
    };
    pool(8, "BuildingDefaultMaterials", {0x111});
    pool(8 + 24, "TerrainMaterials", {0x222, 0x333});
    return b;
}

std::vector<uint32_t> pool_items(const Bytes& b, const Layouts& L, int index) {
    ArrayView p, items;
    array_at(b, L.pools, L.pool_stride, &p);
    array_at(b, static_cast<uint32_t>(p.base - b.data()) + index * L.pool_stride + L.pool_materials, 4, &items);
    std::vector<uint32_t> out;
    for (uint32_t i = 0; i < items.count; ++i) out.push_back(rd<uint32_t>(b, items.base - b.data() + i * 4));
    return out;
}

Material terrain(const std::string& mod, const std::string& block, int like, int look) {
    Material m;
    m.terrain = true;
    m.mod = mod, m.block = block;
    m.guid = derive_guid(mod, "terrain", block);
    m.item_guid = derive_guid(mod, "item", block);
    m.like_cell = like, m.look_cell = look;
    return m;
}

Bytes read(const std::string& path) {
    std::ifstream f(path, std::ios::binary);
    return Bytes(std::istreambuf_iterator<char>(f), {});
}
void write(const std::string& path, const Bytes& b) {
    std::ofstream f(path, std::ios::binary);
    f.write(reinterpret_cast<const char*>(b.data()), static_cast<std::streamsize>(b.size()));
}

}  // namespace

int main(int argc, char** argv) {
    const Layouts L = layouts();

    // a file's blocks: defaults, ids from the mod and the name, and refusals that name the block and the field
    ModFile mf;
    mf.mod = "tm", mf.file = "blocks/t.json", mf.clients = "mod";
    mf.strengths["vein"] = parse_relaxed(R"({"hardness": "veryHard", "health": 12, "breaks": "metal"})");
    read_blocks(parse_relaxed(R"({"terrainMaterials": {
        "frost-ore": {"name": "Frost Ore", "like": "Cell26", "look": "Cell113", "strength": "vein", "flatten": false},
        "frost-soil": {"name": "Frost Soil", "placeable": true, "sliding": true, "strength": {"drops": "block"}},
        "bad-field": {"name": "B", "shine": 1},
        "bad-hardness": {"name": "B", "strength": {"hardness": "steely"}},
        "no-item-recipe": {"name": "B", "recipe": {"needs": {"Material_T1_Twigs": 1}}},
        "no-item-drops": {"name": "B", "strength": {"drops": "block"}},
        "no-name": {"like": "TerrainStone"}}})"),
                &mf);
    const auto err = [&](const std::string& want) {
        for (const auto& e : mf.errors)
            if (e.find(want) != std::string::npos) return true;
        return false;
    };
    check("read.good-blocks", mf.materials.size() == 2 && mf.materials[0].terrain && mf.materials[1].terrain);
    if (mf.materials.size() == 2) {
        const Material& ore = mf.materials[0];
        const Material& soil = mf.materials[1];
        check("read.ore", ore.like == "Cell26" && ore.look == "Cell113" && *ore.strength.hardness == 4 && *ore.strength.health == 12 && *ore.strength.breaks == 3 && ore.flatten == false && !ore.item);
        check("read.soil-defaults", soil.like == kTerrainLike && soil.look.empty() && soil.item && soil.item_id == item_id_of(derive_guid("tm", "item", "frost-soil")) && soil.sliding == true &&
                                        !soil.flatten.has_value());
        check("read.guid-from-name", ore.guid == derive_guid("tm", "terrain", "frost-ore") && ore.guid != derive_guid("tm", "material", "frost-ore"), ore.guid.text());
    }
    check("read.bad-field-named", err("block terrainMaterial bad-field, shine: is not a field of a terrain material"));
    check("read.bad-hardness-named", err("block terrainMaterial bad-hardness, strength.hardness: \"steely\" is not a hardness"));
    check("read.recipe-needs-item", err("block terrainMaterial no-item-recipe, recipe: makes its item, and it has none"));
    check("read.drops-block-needs-item", err("block terrainMaterial no-item-drops, strength.drops: \"block\" is its own item"));
    check("read.name-required", err("block terrainMaterial no-name, name: is required"));
    ModFile low;
    low.mod = "tm", low.file = "x", low.clients = "kiln";
    read_blocks(parse_relaxed(R"({"terrainMaterials": {"t": {"name": "T"}}})"), &low);
    check("read.needs-clients-mod", low.materials.empty() && !low.errors.empty() && low.errors[0].find("\"clients\": \"kiln\"") != std::string::npos);

    // ids: 117.. in GUID order, kept after live, a live one past the room refused by name
    std::vector<int> free_ids;
    for (int i = 117; i <= 127; ++i) free_ids.push_back(i);
    std::vector<Material> live;
    for (int i = 0; i < 3; ++i) live.push_back(terrain("tm", "t" + std::to_string(i), 2, 2));
    Material gone = terrain("old", "gone", 2, 2);
    gone.live = false;
    Allocation a = allocate_terrain(live, {gone}, free_ids);
    bool sorted = a.placed.size() == 4 && a.placed[0].m.guid.text() < a.placed[1].m.guid.text() && a.placed[1].m.guid.text() < a.placed[2].m.guid.text();
    check("ids.live-in-guid-order", sorted && a.placed[0].cell == 117 && a.placed[2].cell == 119 && a.room == 11);
    check("ids.kept-after-live", a.placed.size() == 4 && !a.placed[3].m.live && a.placed[3].cell == 120);
    std::vector<Material> many;
    for (int i = 0; i < 12; ++i) many.push_back(terrain("tm", "m" + std::to_string(i), 2, 2));
    a = allocate_terrain(many, {gone}, free_ids);
    check("ids.refused-by-name", a.refused.size() == 2 && a.refused[0].find("block terrainMaterial m") == 0 && a.refused[0].find("room for 11 new terrain materials") != std::string::npos &&
                                     !a.placed.back().m.live,
          a.refused.empty() ? "" : a.refused[0]);
    check("hash.terrain-in-it", handshake_hash(allocate_terrain(live, {}, free_ids).placed) != kEmptyHash);

    // the registry: a copy of the like with its id, flags and strength; the like's buffs pointed at, not copied
    const Bytes reg = registry();
    RegistryInfo info;
    std::string why;
    read_registry(reg, L, &info, &why);
    Bytes world(4360, 0);
    for (int id : {1, 2, 26}) world[L.world_guids + size_t(id) * 16] = 1;
    check("ids.free-terrain", free_terrain_ids(world, L, info).size() == 124 && free_terrain_ids(world, L, info).front() == 3);
    Material ore = terrain("tm", "ore", 26, 2);
    ore.strength.health = 99;
    ore.strength.drops = "8870fa83-04e6-4b5a-ac2a-ea13205ce469";
    ore.flatten = true;
    Material tar = terrain("tm", "tar", 2, 2);
    tar.sliding = true;
    Allocation two = allocate_terrain({ore, tar}, {}, {117, 118});
    Bytes out;
    std::vector<RegistryChange> changes;
    const bool ok = merge_terrain_registry(reg, L, two.placed, &out, &changes, &why);
    RegistryInfo got;
    check("registry.merges", ok && read_registry(out, L, &got, &why) && got.terrain_at.size() == 5, why);
    const auto cfg = [&](int id) { return got.terrain_at.count(id) ? got.terrain_at[id] : 0; };
    int ore_id = 0, tar_id = 0;
    for (const Placed& p : two.placed) (p.m.block == "ore" ? ore_id : tar_id) = p.cell;
    check("registry.copies-like", cfg(ore_id) && out[cfg(ore_id) + L.terrain.hardness] == 3 && rd<uint32_t>(out, cfg(ore_id) + L.terrain.rate) == 5);
    check("registry.strength", cfg(ore_id) && rd<int32_t>(out, cfg(ore_id) + L.terrain.health) == 99 && out[cfg(ore_id) + L.terrain.item] == 0x83 && out[cfg(ore_id) + L.terrain.flatten] == 1);
    check("registry.flags", cfg(tar_id) && out[cfg(tar_id) + L.terrain.sliding] == 1 && out[cfg(tar_id) + L.terrain.flatten] == 1 && rd<int32_t>(out, cfg(tar_id) + L.terrain.health) == 20);
    ArrayView list;
    const bool tar_list = cfg(tar_id) && array_at(out, static_cast<uint32_t>(cfg(tar_id) + L.terrain.conditional), 20, &list) && list.count == 2;
    check("registry.like-buffs-shared", tar_list && rd<float>(out, list.base - out.data()) == 0.25f && rd<float>(out, list.base - out.data() + 20) == 0.75f);
    ArrayView old;
    check("registry.old-buffs-follow", array_at(out, static_cast<uint32_t>(cfg(2) + L.terrain.conditional), 20, &old) && old.count == 2 && rd<float>(out, old.base - out.data()) == 0.25f);
    check("registry.building-untouched", read_registry(out, L, &got, &why) && got.building_at.count(155) && out[got.building_at[155] + 4] == 0xBB);
    check("registry.nothing-is-identical", merge_terrain_registry(reg, L, {}, &out, &changes, &why) && out == reg);
    Material lost = terrain("tm", "lost", 50, 2);
    check("registry.like-without-config", !merge_terrain_registry(reg, L, allocate_terrain({lost}, {}, {117}).placed, &out, &changes, &why) && why.find("cell 50") != std::string::npos, why);

    // the pool: placeable live ones only, after the pool's own
    Material soil = terrain("tm", "soil", 2, 2);
    soil.item = true, soil.item_id = 0x444;
    Material away = soil;
    away.live = false, away.item_id = 0x555;
    const Bytes pl = pools();
    check("pool.merges", (why.clear(), true) && merge_terrain_pool(pl, L, allocate_terrain({soil, ore}, {away}, {117, 118, 119}).placed, &out, &why), why);
    check("pool.terrain-gets-it", pool_items(out, L, 1) == std::vector<uint32_t>{0x222, 0x333, 0x444} && pool_items(out, L, 0) == std::vector<uint32_t>{0x111});
    check("pool.nothing-is-identical", merge_terrain_pool(pl, L, {}, &out, &why) && out == pl);

    // the per-id tables: each new id takes its look's entries
    const TerrainTables T = tables();
    Bytes vfx(8 + 256 * 4, 0);
    wr<int32_t>(vfx, 0, 8), wr<uint32_t>(vfx, 4, 256);
    wr<uint32_t>(vfx, 8 + 57 * 4, 4);
    Material ice = terrain("tm", "ice", 2, 57);
    const std::vector<Placed> one = allocate_terrain({ice}, {}, {117}).placed;
    check("vfx.copies-look", (why.clear(), true) && merge_terrain_vfx(vfx, T, one, &out, &why) && rd<uint32_t>(out, 8 + 117 * 4) == 4 && out.size() == vfx.size(), why);
    Bytes blend(0x04 + 44 + 512, 0);
    wr<uint32_t>(blend, T.blend_sig, T.blend_type);
    wr<int32_t>(blend, T.blend, static_cast<int32_t>(0x04 + 44 - T.blend)), wr<uint32_t>(blend, T.blend + 4, 512);
    wr<uint32_t>(blend, 0x04 + 44 + 57 * 4, 0x30077);
    check("blend.copies-look", (why.clear(), true) && merge_terrain_blend(blend, T, one, &out, &why) && rd<uint32_t>(out, 0x04 + 44 + 117 * 4) == 0x30077, why);
    wr<uint32_t>(blend, T.blend_sig, 0x12345678);
    check("blend.refuses-other-type", !merge_terrain_blend(blend, T, one, &out, &why) && why.find("isn't the type") != std::string::npos, why);

    // the game's own resources
    if (argc >= 2) {
        const std::string dir = argv[1];
        const Bytes greg = read(dir + "/registry.bin"), gpools = read(dir + "/pools.bin"), gworld = read(dir + "/world.bin"), gvfx = read(dir + "/vfx.bin"),
                    gblend = read(dir + "/blend.bin"), gsimple = read(dir + "/simple.bin");
        RegistryInfo gi;
        const bool reads = read_registry(greg, L, &gi, &why);
        check("game.registry-reads", reads && gi.terrain_at.size() == 116, std::to_string(gi.terrain_at.size()) + " terrain configs");
        const std::vector<int> f = free_terrain_ids(gworld, L, gi);
        check("game.room", f.size() == 11 && f.front() == 117 && f.back() == 127, std::to_string(f.size()) + " free terrain ids");
        // nothing to do: every table back as the game has it
        check("game.nothing-is-identical", (why.clear(), true) && merge_terrain_registry(greg, L, {}, &out, &changes, &why) && out == greg && merge_terrain_pool(gpools, L, {}, &out, &why) && out == gpools &&
                                               merge_terrain_vfx(gvfx, T, {}, &out, &why) && out == gvfx && merge_terrain_blend(gblend, T, {}, &out, &why) && out == gblend &&
                                               merge_terrain_simple(gsimple, T, {}, &out, &why) && out == gsimple,
              why);
        // two: an ore vein like copper (26) that looks like celesthium ore (115), and placeable soil like grassland dirt (1)
        Material vein = terrain("tm", "frost-ore", 26, 115);
        vein.strength.health = 12;
        Material dirt = terrain("tm", "frost-soil", 1, 1);
        dirt.item = true, dirt.item_id = item_id_of(dirt.item_guid);
        dirt.strength.drops = dirt.item_guid.text();
        dirt.sliding = true;
        const Allocation ga = allocate_terrain({vein, dirt}, {}, f);
        Bytes mreg, mpools, mworld, mvfx, mblend, msimple;
        why.clear();
        const bool all = merge_terrain_registry(greg, L, ga.placed, &mreg, &changes, &why) && merge_terrain_pool(gpools, L, ga.placed, &mpools, &why) &&
                         merge_world(gworld, L, ga.placed, &mworld, &why) && merge_terrain_vfx(gvfx, T, ga.placed, &mvfx, &why) && merge_terrain_blend(gblend, T, ga.placed, &mblend, &why) &&
                         merge_terrain_simple(gsimple, T, ga.placed, &msimple, &why);
        check("game.merges", all, why);
        RegistryInfo mi;
        read_registry(mreg, L, &mi, &why);
        int vid = 0, did = 0;
        for (const Placed& p : ga.placed) (p.m.block == "frost-ore" ? vid : did) = p.cell;
        check("game.configs", mi.terrain_at.size() == 118 && mi.terrain_at.count(vid) && mi.terrain_at.count(did) && rd<int32_t>(mreg, mi.terrain_at[vid] + L.terrain.health) == 12 &&
                                  mreg[mi.terrain_at[vid] + L.terrain.klass] == 3 && mreg[mi.terrain_at[did] + L.terrain.sliding] == 1,
              std::to_string(mi.terrain_at.size()) + " terrain configs, ids " + std::to_string(vid) + " " + std::to_string(did));
        // the rest of the registry reads as before: every old config's bytes, building configs included
        bool same = true;
        for (const auto& [id, at] : gi.terrain_at) {
            const size_t past = L.terrain.conditional + 8;  // the list's offset moves with the array; its count and everything else stays
            same = same && std::memcmp(greg.data() + at, mreg.data() + mi.terrain_at[id], L.terrain.conditional) == 0 &&
                   std::memcmp(greg.data() + at + L.terrain.conditional + 4, mreg.data() + mi.terrain_at[id] + L.terrain.conditional + 4, 4) == 0 &&
                   std::memcmp(greg.data() + at + past, mreg.data() + mi.terrain_at[id] + past, L.terrain.stride - past) == 0;
        }
        for (const auto& [id, at] : gi.building_at) same = same && std::memcmp(greg.data() + at, mreg.data() + mi.building_at[id], L.building.stride) == 0;
        check("game.old-configs-same", same);
        size_t diff_vfx = 0, diff_blend = 0, diff_simple = 0, diff_world = 0;
        for (size_t i = 0; i < gvfx.size(); ++i) diff_vfx += gvfx[i] != mvfx[i];
        for (size_t i = 0; i < gblend.size(); ++i) diff_blend += gblend[i] != mblend[i];
        for (size_t i = 0; i < gsimple.size(); ++i) diff_simple += gsimple[i] != msimple[i];
        for (size_t i = 0; i < gworld.size(); ++i) diff_world += gworld[i] != mworld[i];
        check("game.in-place-tables", mvfx.size() == gvfx.size() && mblend.size() == gblend.size() && msimple.size() == gsimple.size() && mworld.size() == gworld.size() && diff_blend > 0 &&
                                          diff_blend <= 8 && diff_simple <= 16 && diff_world <= 32,
              "bytes changed: vfx " + std::to_string(diff_vfx) + ", blend " + std::to_string(diff_blend) + ", simple " + std::to_string(diff_simple) + ", world " + std::to_string(diff_world));
        ArrayView b;
        array_at(mblend, T.blend, 1, &b);
        const size_t bb = static_cast<size_t>(b.base - mblend.data());
        check("game.blend-entries", rd<uint32_t>(mblend, bb + vid * 4) == rd<uint32_t>(gblend, bb + 115 * 4) && rd<uint32_t>(mblend, bb + did * 4) == rd<uint32_t>(gblend, bb + 1 * 4));
        check("game.pool", pool_items(mpools, L, 2).size() == 57 && pool_items(mpools, L, 2).back() == dirt.item_id);
        for (const auto& [name, bytes] : {std::pair{"registry", &mreg}, {"pools", &mpools}, {"world", &mworld}, {"vfx", &mvfx}, {"blend", &mblend}, {"simple", &msimple}})
            write(dir + "/merged-" + name + ".bin", *bytes);
        std::printf("ids: frost-ore %d, frost-soil %d (item 0x%08x)\n", vid, did, dirt.item_id);
    }
    std::printf("%d passed, %d failed\n", g_passed, g_failed);
    return g_failed ? 1 : 0;
}
