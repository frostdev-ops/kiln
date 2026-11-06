// SPDX-License-Identifier: MIT
// kiln.worldtables' portable part on the host: see run.sh.
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <map>

#include "merge.h"
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
    L.terrain = {184, 0, 4, 0x14, 0x18, 0x1c, 0x54, 0x68, UINT32_MAX};
    L.world_guids = 0x1c, L.world_size = 4360;
    L.pools = 0, L.pool_stride = 24, L.pool_materials = 8, L.pool_name = 16;
    L.blueprints = 0, L.bp_stride = 28, L.bp_item = 0, L.bp_data = 16;
    L.hardness_names = {"Soft", "SlightlyHard", "ModeratelyHard", "Hard", "VeryHard", "Unbreakable"};
    L.class_names = {"None", "Wood", "Stone", "Metal"};
    return L;
}

// A registry with building configs for the given cell ids (each with an item but `no_item`), one terrain config, and a conditional list on the first building
// config that points at real bytes past the arrays, as the game's do.
Bytes registry(const std::vector<int>& cells, int no_item) {
    const Layouts L = layouts();
    Bytes b(16 + 184 + cells.size() * 180 + 20, 0);
    wr<int32_t>(b, 0, 16), wr<uint32_t>(b, 4, 1);
    b[16] = 2;                                 // terrain id 2
    wr<int32_t>(b, 16 + 0x1c, 5);              // health
    const size_t at = 16 + 184;
    wr<int32_t>(b, 8, static_cast<int32_t>(at - 8)), wr<uint32_t>(b, 12, static_cast<uint32_t>(cells.size()));
    for (size_t i = 0; i < cells.size(); ++i) {
        const size_t c = at + i * 180;
        b[c] = static_cast<uint8_t>(cells[i] - 128);
        if (cells[i] != no_item) b[c + 4] = 0xAB;
        b[c + L.building.hardness] = 2;
        wr<int32_t>(b, c + L.building.health, 250);
        b[c + L.building.klass] = 2;
    }
    const size_t list = at + cells.size() * 180;
    wr<int32_t>(b, at + L.building.conditional, static_cast<int32_t>(list - (at + L.building.conditional)));
    wr<uint32_t>(b, at + L.building.conditional + 4, 1);
    wr<float>(b, list, 0.5f);
    return b;
}

Material material(const std::string& mod, const std::string& block) {
    Material m;
    m.mod = mod;
    m.block = block;
    m.guid = derive_guid(mod, "material", block);
    m.item = true;
    m.item_id = item_id_of(derive_guid(mod, "item", block));
    return m;
}

Bytes read(const char* path) {
    std::ifstream f(path, std::ios::binary);
    return Bytes(std::istreambuf_iterator<char>(f), {});
}

// A pool registry as the game's: each pool {slot, id, materials BlobArray<ItemId>, debugName BlobString}, the lists and names after the records.
Bytes pool_registry(const std::vector<std::pair<std::string, uint32_t>>& pools) {
    Bytes b(8 + pools.size() * 24, 0);
    wr<int32_t>(b, 0, 8), wr<uint32_t>(b, 4, static_cast<uint32_t>(pools.size()));
    for (size_t i = 0; i < pools.size(); ++i) {
        const size_t rec = 8 + i * 24;
        const size_t list = b.size();
        b.resize(list + size_t{pools[i].second} * 4);
        for (uint32_t k = 0; k < pools[i].second; ++k) wr<uint32_t>(b, list + k * 4, 0x1000u * static_cast<uint32_t>(i + 1) + k);
        wr<int32_t>(b, rec + 8, static_cast<int32_t>(list - (rec + 8))), wr<uint32_t>(b, rec + 12, pools[i].second);
        const size_t name = b.size();
        b.insert(b.end(), pools[i].first.begin(), pools[i].first.end());
        while (b.size() % 4) b.push_back(0);
        wr<int32_t>(b, rec + 16, static_cast<int32_t>(name - (rec + 16))), wr<uint32_t>(b, rec + 20, static_cast<uint32_t>(pools[i].first.size()));
    }
    return b;
}

uint32_t pool_size(const Bytes& b, const std::string& name) {
    std::map<std::string, uint32_t> n;
    std::string why;
    return pool_counts(b, layouts(), &n, &why) && n.count(name) ? n[name] : UINT32_MAX;
}

}  // namespace

int main(int argc, char** argv) {
    // ids follow Kiln.Blocks' BlockIds.Derive (the same GUID on every machine)
    check("guid.derive", derive_guid("wt-mat-a", "material", "brick-a").text() == "0bc0dd61-53c2-5d22-926f-005393ba84b7", derive_guid("wt-mat-a", "material", "brick-a").text());
    Guid parsed;
    check("guid.round-trip", Guid::parse("0bc0dd61-53c2-5d22-926f-005393ba84b7", &parsed) && parsed.text() == "0bc0dd61-53c2-5d22-926f-005393ba84b7");

    // names
    check("name.block", material_name("Block_T1_Wood_Rough_CRAFTING") == "WoodRough");
    check("name.roof", material_name("Block_Roof_T1_PlantFibre") == "RoofPlantFibre");
    check("name.terrain", material_name("Build_TerrainMaterial_T1_Stone") == "TerrainStone");
    check("name.underscore", material_name("_Block_T7_Stone_Ancient_Moonstone_CRAFTING") == "StoneAncientMoonstone");
    check("name.other", material_name("Material_T1_Twigs").empty());

    // ids: free ones first, then the deprecated, live before kept, each in GUID order; past the room, live ones are refused by name
    std::vector<int> free_ids, deprecated = {129, 131};
    for (int i = 213; i < 216; ++i) free_ids.push_back(i);
    std::vector<Material> live = {material("m", "c"), material("m", "a"), material("m", "b")};
    Material kept = material("gone", "old");
    kept.live = false;
    Allocation a = allocate(live, {kept}, free_ids, deprecated);
    bool sorted = true;
    for (size_t i = 1; i + 1 < a.placed.size(); ++i) sorted = sorted && a.placed[i - 1].m.guid.text() < a.placed[i].m.guid.text();
    check("ids.room", a.room == 5 && a.placed.size() == 4 && a.refused.empty());
    check("ids.live-in-guid-order", sorted && a.placed[0].cell == 213 && a.placed[2].cell == 215);
    check("ids.kept-after-live", !a.placed[3].m.live && a.placed[3].cell == 129 && a.placed[3].reused);
    std::vector<Material> many;
    for (int i = 0; i < 6; ++i) many.push_back(material("m", "x" + std::to_string(i)));
    a = allocate(many, {kept}, free_ids, deprecated);
    check("ids.refused-by-name", a.refused.size() == 2 && a.refused[0].find("block buildingMaterial ") == 0 && a.refused[0].find("kept for absent mods") != std::string::npos,
          a.refused.empty() ? "" : a.refused[0]);
    check("ids.kept-never-dropped", !a.placed.back().m.live);

    // the hash: live materials only, and the same text for none
    a = allocate(live, {kept}, free_ids, deprecated);
    const std::string h = handshake_hash(a.placed);
    Allocation without = allocate(live, {}, free_ids, deprecated);
    check("hash.kept-not-in-it", h == handshake_hash(without.placed) && h != kEmptyHash);
    check("hash.empty", handshake_hash({}) == kEmptyHash);
    Allocation moved = allocate({live[0], live[1]}, {}, free_ids, deprecated);
    check("hash.moves-with-ids", handshake_hash(moved.placed) != h);

    // a file's blocks: errors name the block and the field, the rest stay
    ModFile mf;
    mf.mod = "m", mf.file = "blocks/x.json", mf.clients = "none";
    read_blocks(parse_relaxed(R"({"materialTweaks": {"a": {"material": "StoneRough", "strength": {"hardness": "steely"}}, "b": {"material": "WoodRough", "strength": {"health": 7}}},
                                  "buildingMaterials": {"c": {"name": "C"}}})"),
                &mf);
    check("blocks.good-stays", mf.tweaks.size() == 1 && mf.tweaks[0].block == "b" && *mf.tweaks[0].strength.health == 7);
    bool named = false, clients = false;
    for (const auto& e : mf.errors) {
        named = named || e.find("block materialTweak a, strength.hardness: \"steely\" is not a hardness") != std::string::npos;
        clients = clients || e.find("\"clients\": \"none\"") != std::string::npos;
    }
    check("blocks.error-names-field", named);
    check("blocks.material-needs-clients-mod", clients && mf.materials.empty());

    // a surface is checked here, so a wrong one refuses its block on every side; a recipe's main input is what digging gives back
    ModFile sf;
    sf.mod = "m", sf.file = "blocks/s.json", sf.clients = "mod";
    read_blocks(parse_relaxed(R"({"buildingMaterials": {
                                    "good": {"name": "G", "surface": {"side": {"albedo": "t/a.png"}}, "recipe": {"makes": 100, "needs": {"Build_TerrainMaterial_T1_Stone": 2, "Material_T1_Twigs": 1}}},
                                    "bad": {"name": "B", "surface": {"side": {"albedo": "t/a.png", "tint": "#12"}}}},
                                  "materialTweaks": {"skin": {"material": "WoodRough", "surface": {"top": {"albedo": "t/b.png"}}},
                                                     "nothing": {"material": "WoodRough", "surface": {"bevel": 0.1}}}})"),
                &sf);
    bool tint = false, nothing = false;
    for (const auto& e : sf.errors) {
        tint = tint || e.find("block buildingMaterial bad, surface.side.tint: \"#12\" is not a colour") != std::string::npos;
        nothing = nothing || e.find("block materialTweak nothing, surface: changes no layer") != std::string::npos;
    }
    check("surface.refuses-by-field", tint && nothing && sf.materials.size() == 1 && sf.tweaks.size() == 1 && sf.tweaks[0].block == "skin");
    check("surface.loot-from-recipe", sf.materials.size() == 1 && sf.materials[0].loot_from == "Build_TerrainMaterial_T1_Stone" && sf.materials[0].loot_count == 2 &&
                                          sf.materials[0].loot_makes == 100 && sf.materials[0].item_guid == derive_guid("m", "item", "good"));
    // a surface by name: the mod's "surfaces" sections; a writer reads it written out
    ModFile nf;
    nf.mod = "m", nf.file = "blocks/n.json", nf.clients = "mod";
    nf.surfaces["moss"] = parse_relaxed(R"({"side": {"albedo": "t/moss.png"}})");
    read_blocks(parse_relaxed(R"({"buildingMaterials": {"named": {"name": "N", "surface": "moss"}, "missing": {"name": "M", "surface": "nope"},
                                                         "theirs": {"name": "T", "surface": "other:moss"}},
                                  "materialTweaks": {"re": {"material": "WoodRough", "surface": "moss"}}})"),
                &nf);
    bool nope = false, theirs = false;
    for (const auto& e : nf.errors) {
        nope = nope || e.find("block buildingMaterial missing, surface: no surface \"nope\" in this mod's \"surfaces\" sections") != std::string::npos;
        theirs = theirs || e.find("block buildingMaterial theirs, surface: \"other:moss\" is another mod's") != std::string::npos;
    }
    const auto inlined = [](const std::string& def) { const ojson j = parse_relaxed(def); return j.contains("surface") && j["surface"].is_object() && j["surface"]["side"]["albedo"] == "t/moss.png"; };
    check("surface.by-name", nf.materials.size() == 1 && inlined(nf.materials[0].definition) && nf.tweaks.size() == 1 && inlined(nf.tweaks[0].definition));
    check("surface.unknown-name-by-field", nope && theirs);
    check("surface.loot-rate", loot_rate(100, 2) == 50 && loot_rate(20, 10) == 2 && loot_rate(100, 3) == 33 && loot_rate(1, 3) == 1 && loot_rate(10, 0) == 1);

    // writers on a synthetic registry
    const Layouts L = layouts();
    const Bytes reg = registry({143, 155, 129}, 129);
    Tweak t;
    t.mod = "m", t.block = "vault", t.material = "StoneRough", t.cell = 155;
    t.strength.hardness = 5;
    Material nm = material("m", "brick");
    nm.strength.health = 2;
    a = allocate({nm}, {}, {213}, {129});
    Bytes out;
    std::vector<RegistryChange> changes;
    std::string why;
    const bool ok = merge_registry(reg, L, {t}, a.placed, &out, &changes, &why);
    RegistryInfo info;
    check("registry.merges", ok && read_registry(out, L, &info, &why), why);
    check("registry.tweak", out[info.building_at[155] + L.building.hardness] == 5);
    check("registry.new-config", info.building_at.count(213) && rd<int32_t>(out, info.building_at[213] + L.building.health) == 2 && out[info.building_at[213] + L.building.hardness] == 2);
    check("registry.new-has-no-conditionals", rd<uint32_t>(out, info.building_at[213] + L.building.conditional + 4) == 0);
    const size_t first = info.building_at[143];
    const int32_t off = rd<int32_t>(out, first + L.building.conditional);
    check("registry.inner-offsets-follow", rd<float>(out, first + L.building.conditional + off) == 0.5f);
    check("registry.nothing-to-do-is-identical", merge_registry(reg, L, {}, {}, &out, &changes, &why) && out == reg);

    // the reused slot is replaced where it is
    Allocation r = allocate({material("m", "a"), material("m", "b")}, {}, {213}, {129});
    merge_registry(reg, L, {}, r.placed, &out, &changes, &why);
    read_registry(out, L, &info, &why);
    check("registry.reuse-in-place", out.size() > reg.size() && info.building_at.size() == 4 && out[info.building_at[129] + 4] == 0xAB);

    // the world's GUID table
    Bytes world(4360, 0);
    std::memset(world.data() + L.world_guids + 155 * 16, 0x11, 16);
    check("world.free-ids", free_building_ids(world, L).size() == 127 && world_has_table(world, L));
    merge_world(world, L, a.placed, &out, &why);
    check("world.guid-at-id", std::memcmp(out.data() + L.world_guids + 213 * 16, nm.guid.b.data(), 16) == 0 && out.size() == world.size());

    // shapes: the authoring form gives the bits of the game's 4 m door wall, and the form code sends reads back the same
    {
        ModFile sm;
        sm.mod = "m", sm.file = "blocks/s.json", sm.clients = "mod";
        read_blocks(parse_relaxed(R"({"blueprints": {"door-wall": {"name": "Door Wall", "cells": [
            ["##....##"], ["##....##"], ["##....##"], ["##....##"], ["##....##"], ["########"], ["########"], ["########"]]},
            "bad": {"name": "Bad", "cells": [["#x"]]}}})"),
                    &sm);
        check("shape.door-wall-bits", sm.shapes.size() == 1 && sm.shapes[0].x == 8 && sm.shapes[0].y == 8 && sm.shapes[0].z == 1 &&
                                          sm.shapes[0].bits == Bytes{0xc3, 0xc3, 0xc3, 0xc3, 0xc3, 0xff, 0xff, 0xff});
        check("shape.item-follows-name", !sm.shapes.empty() && sm.shapes[0].item == derive_guid("m", "item", "door-wall"));
        check("shape.bad-cell-named", !sm.errors.empty() && sm.errors[0].find("block blueprint bad, cells[0][0]: 'x' at 1") != std::string::npos, sm.errors.empty() ? "" : sm.errors[0]);
        ModFile code;
        code.mod = "m", code.file = "code", code.clients = "mod";
        read_blocks(parse_relaxed(R"({"blueprints": {"door-wall": {"name": "Door Wall", "item": ")" + derive_guid("m", "item", "door-wall").text() +
                                  R"(", "size": [8, 8, 1], "cells": "w8PDw8P///8=", "roof": false}}})"),
                    &code);
        check("shape.code-form-same", code.shapes.size() == 1 && !sm.shapes.empty() && code.shapes[0].definition == sm.shapes[0].definition, code.errors.empty() ? "" : code.errors[0]);
        ModFile low;
        low.mod = "m", low.file = "x", low.clients = "kiln";
        read_blocks(parse_relaxed(R"({"blueprints": {"w": {"name": "W", "cells": [["#"]]}}})"), &low);
        check("shape.needs-clients-mod", low.shapes.empty() && !low.errors.empty());

        // the table takes it with its own size and cells; an id it has is refused by name
        Bytes table(8, 0);
        wr<int32_t>(table, 0, 8), wr<uint32_t>(table, 4, 1);
        table.resize(8 + 28 + 1, 0);
        wr<uint32_t>(table, 8, 0x1234);
        wr<uint32_t>(table, 8 + 4, 1), wr<uint32_t>(table, 8 + 8, 1), wr<uint32_t>(table, 8 + 12, 1);
        wr<int32_t>(table, 8 + 16, 28 - 16), wr<uint32_t>(table, 8 + 20, 1);
        table[8 + 24] = 1;
        table[8 + 28] = 0xff;
        Bytes merged;
        std::vector<std::string> refused;
        Shape twin = sm.shapes[0];
        twin.block = "twin";
        const bool ok2 = merge_shapes(table, L, {sm.shapes[0], twin}, &merged, &refused, &why);
        ArrayView entries;
        array_at(merged, L.blueprints, L.bp_stride, &entries);
        const size_t e = entries.base ? static_cast<size_t>(entries.base - merged.data()) + 28 : 0;
        ArrayView cells;
        const bool read2 = e && array_at(merged, static_cast<uint32_t>(e + L.bp_data), 1, &cells);
        check("shape.table-entry", ok2 && entries.count == 2 && rd<uint32_t>(merged, e) == sm.shapes[0].item_id && rd<uint32_t>(merged, e + 4) == 8 && merged[e + 24] == 1 && read2 &&
                                       Bytes(cells.base, cells.base + cells.count) == sm.shapes[0].bits, why);
        ArrayView first;
        array_at(merged, static_cast<uint32_t>(e - 28 + L.bp_data), 1, &first);
        check("shape.table-keeps-the-old", first.count == 1 && first.base[0] == 0xff);
        check("shape.duplicate-id-refused", refused.size() == 1 && refused[0].find("block blueprint twin (mod m)") == 0);

        // the preview: the game's door wall model, byte for byte, when given
        const ModelLayout M{24, 0, 12, 20};
        const Bytes model = voxel_model(sm.shapes[0], M);
        check("shape.model-cells", model.size() == 24 + 64 && model[24] == 0x12 && model[26] == 0 && model[24 + 63] == 0x12);
        if (argc >= 6) check("game.door-model-identical", model == read(argv[5]), std::to_string(model.size()) + " against " + std::to_string(read(argv[5]).size()) + " bytes");

        // A roof shape's cells are the roof pool's default (0x08), as in every roof-pool shape of the game; a wall material's 0x12 there is not the game's.
        // The game's 2 m flat roof drawn as cells gives its own model byte for byte.
        ModFile rm;
        rm.mod = "m", rm.file = "blocks/r.json", rm.clients = "mod";
        read_blocks(parse_relaxed(R"({"blueprints": {
            "flat-roof": {"name": "Flat Roof", "roof": true, "cells": [["####", "####", "####", "####"]]},
            "ridge": {"name": "Ridge", "roof": true, "cells": [["####", "####"], [".##.", ".##."]]}}})"),
                    &rm);
        const Shape* flat = nullptr;
        const Shape* ridge = nullptr;
        for (const Shape& sh : rm.shapes) (sh.block == "flat-roof" ? flat : ridge) = &sh;
        check("shape.roof-read", rm.errors.empty() && flat && ridge && flat->roof && flat->x == 4 && flat->y == 1 && flat->z == 4 && flat->bits == Bytes{0xff, 0xff},
              rm.errors.empty() ? "" : rm.errors[0]);
        if (flat && ridge) {
            const Bytes fm = voxel_model(*flat, M), rmodel = voxel_model(*ridge, M);
            check("shape.roof-model-cells", fm.size() == 24 + 16 && std::all_of(fm.begin() + 24, fm.end(), [](uint8_t c) { return c == 0x08; }));
            const auto roof_cells = std::count(rmodel.begin() + 24, rmodel.end(), uint8_t{0x08});
            const auto other = std::count_if(rmodel.begin() + 24, rmodel.end(), [](uint8_t c) { return c != 0 && c != 0x08; });
            check("shape.ridge-model-cells", ridge->x == 4 && ridge->y == 2 && ridge->z == 2 && rmodel.size() == 24 + 16 && roof_cells == 12 && other == 0,
                  std::to_string(roof_cells) + " roof cells, " + std::to_string(other) + " others");
            if (argc >= 7) check("game.roof-flat-model-identical", fm == read(argv[6]), std::to_string(fm.size()) + " against " + std::to_string(read(argv[6]).size()) + " bytes");
        }
    }

    // The build menu's pools. A player's game copies a pool into 64 items and stops at the 65th, so a pool never lists more: the game's 61 walls leave room
    // for 3, its 14 roofs for 50, its 56 terrain items for 8, and a material past that is refused by name before it takes an id.
    {
        const Bytes gp = pool_registry({{"BuildingDefaultMaterials", 61}, {"BuildingRoofMaterials", 14}, {"TerrainMaterials", 56}, {"OvergrowthMaterials", 5}});
        std::map<std::string, uint32_t> counts;
        check("pools.counts", pool_counts(gp, L, &counts, &why) && counts["BuildingDefaultMaterials"] == 61 && counts["BuildingRoofMaterials"] == 14 && counts["TerrainMaterials"] == 56,
              why);
        Material roof = material("m", "slate"), ore = material("m", "ore"), snow = material("m", "snow"), plain = material("m", "plain");
        roof.roof = true;
        ore.terrain = true, ore.item = false;
        snow.terrain = true;
        plain.item = false;
        check("pools.of", pool_of(material("m", "w")) == kWallPool && pool_of(roof) == kRoofPool && pool_of(snow) == "TerrainMaterials" && pool_of(ore).empty() && pool_of(plain).empty());

        // four walls: the first three in GUID order fit, the fourth is refused by name; roofs, terrain without an item and plain ones are untouched
        std::vector<Material> walls;
        for (int i = 0; i < 4; ++i) walls.push_back(material("m", "w" + std::to_string(i)));
        std::vector<Material> by_guid = walls;
        std::sort(by_guid.begin(), by_guid.end(), [](const Material& x, const Material& y) { return x.guid.text() < y.guid.text(); });
        const Material last = by_guid.back();
        std::vector<Material> fit = walls;
        fit.push_back(roof), fit.push_back(ore), fit.push_back(plain);
        std::vector<Material> held;
        std::vector<std::string> refused;
        fit_pools(&fit, counts, {}, &held, &refused);
        const bool last_out = std::none_of(fit.begin(), fit.end(), [&](const Material& m) { return m.guid == last.guid; });
        check("pools.walls-fit-three", fit.size() == 6 && last_out && held.empty(), std::to_string(fit.size()) + " stay");
        const std::string want = "block buildingMaterial " + last.block + " (mod m): refused: the game's build menu lists at most 64 wall materials and has 61 of its own: 3 new ones fit across all mods";
        check("pools.refused-by-name", refused.size() == 1 && refused[0] == want, refused.empty() ? "" : refused[0]);

        // a refused one a world remembers is held: no item, not live, its cells kept
        fit = walls, held.clear(), refused.clear();
        fit_pools(&fit, counts, {last.guid}, &held, &refused);
        check("pools.remembered-is-held", fit.size() == 3 && held.size() == 1 && held[0].guid == last.guid && !held[0].live && !held[0].item && refused.size() == 1 &&
                                              refused[0].find("they stay") != std::string::npos,
              refused.empty() ? "" : refused[0]);
        // and allocate() places it after the live ones, as an absent mod's
        Allocation ha = allocate(fit, held, {213, 214, 215, 216, 217}, {});
        check("pools.held-is-kept", ha.placed.size() == 4 && !ha.placed.back().m.live && ha.placed.back().m.guid == last.guid && handshake_hash(ha.placed) == handshake_hash(allocate(fit, {}, {213, 214, 215, 216, 217}, {}).placed));

        // nine placeable terrain materials: eight fit
        std::vector<Material> terrain;
        for (int i = 0; i < 9; ++i) {
            Material t2 = material("m", "t" + std::to_string(i));
            t2.terrain = true;
            terrain.push_back(t2);
        }
        held.clear(), refused.clear();
        fit_pools(&terrain, counts, {}, &held, &refused);
        check("pools.terrain-fits-eight", terrain.size() == 8 && refused.size() == 1 && refused[0].find("block terrainMaterial t") == 0 &&
                                              refused[0].find("list at most 64 placeable terrain materials and have 56 of their own: 8 new ones fit") != std::string::npos,
              refused.empty() ? "" : refused[0]);

        // 50 roofs fit their pool, so the ids are the limit there
        std::vector<Material> roofs;
        for (int i = 0; i < 51; ++i) {
            Material r2 = material("m", "r" + std::to_string(i));
            r2.roof = true;
            roofs.push_back(r2);
        }
        held.clear(), refused.clear();
        fit_pools(&roofs, counts, {}, &held, &refused);
        check("pools.roofs-fit-fifty", roofs.size() == 50 && refused.size() == 1 && refused[0].find("64 roof materials and has 14 of its own: 50 new ones fit") != std::string::npos,
              refused.empty() ? "" : refused[0]);

        // the writers never serve a pool past 64: three walls make it exactly 64, a fourth is refused rather than written
        std::vector<Material> three(by_guid.begin(), by_guid.begin() + 3);
        Allocation wa = allocate(three, {}, {213, 214, 215, 216}, {});
        Bytes np;
        check("pools.writer-fills-to-64", merge_pools(gp, L, wa.placed, &np, &why) && pool_size(np, "BuildingDefaultMaterials") == 64 && pool_size(np, "BuildingRoofMaterials") == 14, why);
        why.clear();
        Allocation over = allocate(by_guid, {}, {213, 214, 215, 216}, {});
        check("pools.writer-refuses-65", !merge_pools(gp, L, over.placed, &np, &why) && why.find("would list 65 items, past the 64") != std::string::npos, why);
        std::vector<Material> nine;
        for (int i = 0; i < 9; ++i) {
            Material t2 = material("m", "t" + std::to_string(i));
            t2.terrain = true;
            nine.push_back(t2);
        }
        why.clear();
        Allocation ta = allocate(nine, {}, {117, 118, 119, 120, 121, 122, 123, 124, 125}, {});
        check("pools.terrain-writer-refuses-65", !merge_terrain_pool(gp, L, ta.placed, &np, &why) && why.find("TerrainMaterials would list 65 items") != std::string::npos, why);
    }

    // the game's own resources, when given
    why.clear();
    if (argc >= 5) {
        const Bytes greg = read(argv[1]), gpools = read(argv[2]), gbp = read(argv[3]), gworld = read(argv[4]);
        check("game.registry-identical", merge_registry(greg, L, {}, {}, &out, &changes, &why) && out == greg, why);
        check("game.pools-identical", merge_pools(gpools, L, {}, &out, &why) && out == gpools, why);
        check("game.blueprints-identical", merge_blueprints(gbp, L, {}, first_pool_item(gpools, L), &out, &why) && out == gbp, why);
        std::map<std::string, uint32_t> gcounts;
        const bool gread = pool_counts(gpools, L, &gcounts, &why);
        check("game.pool-room", gread && gcounts[kWallPool] == 61 && gcounts[kRoofPool] == 14 && gcounts[kTerrainPool] == 56 &&
                                    pool_room_text(kWallPool, gcounts[kWallPool]).find(": 3 new ones fit") != std::string::npos,
              std::to_string(gcounts[kWallPool]) + " walls, " + std::to_string(gcounts[kRoofPool]) + " roofs, " + std::to_string(gcounts[kTerrainPool]) + " terrain items");
        RegistryInfo gi;
        read_registry(greg, L, &gi, &why);
        std::vector<int> f;
        for (int id : free_building_ids(gworld, L))
            if (!gi.building_at.count(id)) f.push_back(id);
        const std::vector<int> d = deprecated_ids(greg, gi, L);
        check("game.room", f.size() == 43 && d.size() == 6, std::to_string(f.size()) + " free, " + std::to_string(d.size()) + " deprecated");
        Allocation ga = allocate({nm}, {}, f, d);
        Bytes np, nb;
        check("game.new-material", merge_registry(greg, L, {}, ga.placed, &out, &changes, &why) && merge_pools(gpools, L, ga.placed, &np, &why) &&
                                       merge_blueprints(gbp, L, ga.placed, first_pool_item(gpools, L), &nb, &why), why);
        read_registry(out, L, &gi, &why);
        ArrayView pools, items;
        array_at(np, L.pools, L.pool_stride, &pools);
        array_at(np, static_cast<uint32_t>(pools.base - np.data()) + L.pool_materials, 4, &items);
        check("game.new-reads-back", gi.building_at.count(213) && items.count == 62 && rd<uint32_t>(np, items.base - np.data() + 61 * 4) == nm.item_id);
    }
    std::printf("%d passed, %d failed\n", g_passed, g_failed);
    return g_failed ? 1 : 0;
}
