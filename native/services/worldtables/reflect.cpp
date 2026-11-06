// SPDX-License-Identifier: MIT
// See reflect.h.
#include "reflect.h"

#include <cstring>
#include <map>

namespace kwt {
namespace {

constexpr size_t kRecQname = 0x20, kRecQlen = 0x28, kRecInner = 0x38, kRecSize = 0x40, kRecCount = 0x48, kRecKind = 0x4C, kRecHash = 0x50, kRecLayout = 0x54, kRecFields = 0x58,
                 kRecEnums = 0x60, kRecLen = 0x68;
constexpr size_t kFieldStride = 0x30, kEnumStride = 0x28;
constexpr uint32_t kKindTypedef = 0x11;

template <class T>
T rd(const uint8_t* p) {
    T v;
    std::memcpy(&v, p, sizeof v);
    return v;
}

struct Image {
    const uint8_t* base;
    size_t size;
    std::vector<std::pair<uint32_t, uint32_t>> sections;  // va, size of .data, .rdata
    std::map<std::string, const uint8_t*> found;

    bool inside(uint64_t va, size_t n) const {
        const uint64_t b = reinterpret_cast<uint64_t>(base);
        return va >= b && va + n <= b + size;
    }
    std::string text(const uint8_t* at) const {
        const uint64_t p = rd<uint64_t>(at), n = rd<uint64_t>(at + 8);
        return n && n < 512 && inside(p, n) ? std::string(reinterpret_cast<const char*>(p), n) : std::string();
    }
    bool open() {
        if (size < 0x400 || base[0] != 'M' || base[1] != 'Z') return false;
        const uint32_t pe = rd<uint32_t>(base + 0x3c);
        const uint16_t count = rd<uint16_t>(base + pe + 6), opt = rd<uint16_t>(base + pe + 0x14);
        const uint8_t* table = base + pe + 0x18 + opt;
        for (uint16_t i = 0; i < count; ++i) {
            const uint8_t* s = table + size_t{i} * 40;
            if (std::memcmp(s, ".data\0", 6) == 0 || std::memcmp(s, ".rdata\0", 7) == 0) sections.emplace_back(rd<uint32_t>(s + 12), rd<uint32_t>(s + 8));
        }
        return !sections.empty();
    }
    // Finds records by qualified name in one pass over the data sections.
    void scan(const std::vector<std::string>& names) {
        std::map<uint32_t, std::string> want;
        for (const std::string& n : names)
            if (!found.count(n)) want[fnv1a(n.data(), n.size())] = n;
        if (want.empty()) return;
        for (const auto& [va, sz] : sections) {
            const size_t end = std::min<size_t>(size_t{va} + sz, size);
            for (size_t o = (va + 7) & ~size_t{7}; o + kRecLen <= end; o += 8) {
                const auto it = want.find(rd<uint32_t>(base + o + kRecHash));
                if (it == want.end() || text(base + o + kRecQname) != it->second) continue;
                found[it->second] = base + o;
            }
        }
    }
    const uint8_t* rec(const std::string& n) {
        scan({n});
        const auto it = found.find(n);
        return it == found.end() ? nullptr : it->second;
    }
};

struct Reader {
    Image& img;
    const std::string& missing;
    std::string* why;
    bool ok = true;

    void fail(const std::string& t) {
        if (ok) *why = t;
        ok = false;
    }
    const uint8_t* type(const std::string& qname) {
        const uint8_t* r = img.rec(qname);
        if (!r) fail("the game has no type " + qname);
        return r;
    }
    uint32_t size_of(const uint8_t* r) { return r ? rd<uint32_t>(r + kRecSize) : 0; }
    const uint8_t* field_rec(const uint8_t* r, const char* name, uint32_t* offset, const uint8_t** type_out = nullptr) {
        if (!r) return nullptr;
        const uint32_t count = rd<uint32_t>(r + kRecCount);
        const uint64_t fields = rd<uint64_t>(r + kRecFields);
        if (name != missing && fields && img.inside(fields, size_t{count} * kFieldStride))
            for (uint32_t i = 0; i < count; ++i) {
                const uint8_t* f = reinterpret_cast<const uint8_t*>(fields) + size_t{i} * kFieldStride;
                if (img.text(f) != name) continue;
                *offset = static_cast<uint32_t>(rd<uint64_t>(f + 0x18));
                if (type_out) *type_out = reinterpret_cast<const uint8_t*>(rd<uint64_t>(f + 0x10));
                return f;
            }
        fail(img.text(r + kRecQname) + " has no field " + name);
        return nullptr;
    }
    uint32_t off(const uint8_t* r, const char* name) {
        uint32_t o = 0;
        field_rec(r, name, &o);
        return o;
    }
    std::vector<std::string> enum_names(const uint8_t* r) {
        std::vector<std::string> out;
        if (!r) return out;
        const uint32_t count = rd<uint32_t>(r + kRecCount);
        const uint64_t vals = rd<uint64_t>(r + kRecEnums);
        if (!vals || !img.inside(vals, size_t{count} * kEnumStride)) {
            fail(img.text(r + kRecQname) + " has no values");
            return out;
        }
        for (uint32_t i = 0; i < count; ++i) {
            const uint8_t* e = reinterpret_cast<const uint8_t*>(vals) + size_t{i} * kEnumStride;
            const uint64_t v = rd<uint64_t>(e + 0x10);
            if (v >= 64) continue;
            if (out.size() <= v) out.resize(static_cast<size_t>(v) + 1);
            out[static_cast<size_t>(v)] = img.text(e);
        }
        return out;
    }
    const uint8_t* resolved(const uint8_t* r) {
        for (int i = 0; r && i < 8 && (rd<uint32_t>(r + kRecKind) & 0xFF) == kKindTypedef; ++i) r = reinterpret_cast<const uint8_t*>(rd<uint64_t>(r + kRecInner));
        return r;
    }
};

}  // namespace

bool read_layouts(const uint8_t* image_base, size_t image_size, const std::string& test_missing, Layouts* L, ItemLayout* I, std::string* why) {
    Image img{image_base, image_size, {}, {}};
    if (!img.open()) {
        *why = "the game's image has no data sections";
        return false;
    }
    img.scan({"keen::TerraformingEfficiencyRegistryResource", "keen::BuildingTerraformingConfig", "keen::TerrainTerraformingConfig", "keen::VoxelWorldResource",
              "keen::VoxelBlueprintMaterialPoolRegistryResource", "keen::VoxelBlueprintMaterialPool", "keen::VoxelBlueprintItemRegistryResource", "keen::VoxelBlueprintItem",
              "keen::HardnessCategory", "keen::PropDamageSusceptibility", "keen::ItemInfo"});
    Reader r{img, test_missing, why};
    const uint8_t* reg = r.type("keen::TerraformingEfficiencyRegistryResource");
    L->reg_terrain = r.off(reg, "terrainConfigs");
    L->reg_building = r.off(reg, "buildingConfigs");
    L->reg_size = r.size_of(reg);

    const uint8_t* b = r.type("keen::BuildingTerraformingConfig");
    L->building.stride = r.size_of(b);
    L->building.id = r.off(b, "buildingMaterial");
    L->building.item = r.off(b, "materialItem");
    L->building.hardness = r.off(b, "hardness");
    L->building.rate = r.off(b, "blocksPerLootItemExchangeRate");
    L->building.health = r.off(b, "healthPoints");
    L->building.klass = r.off(b, "damageSusceptibility");
    L->building.conditional = r.off(b, "conditionalBuffTypes");
    L->building.roof = r.off(b, "isRoofBlock");

    const uint8_t* t = r.type("keen::TerrainTerraformingConfig");
    L->terrain.stride = r.size_of(t);
    L->terrain.id = r.off(t, "terrainMaterial");
    L->terrain.item = r.off(t, "terrainItem");
    L->terrain.hardness = r.off(t, "hardness");
    L->terrain.rate = r.off(t, "terrainPerLootItemExchangeRate");
    L->terrain.health = r.off(t, "healthPoints");
    L->terrain.klass = r.off(t, "damageSusceptibility");
    L->terrain.conditional = r.off(t, "conditionalBuffTypes");

    const uint8_t* w = r.type("keen::VoxelWorldResource");
    L->world_guids = r.off(w, "materialGuids");
    L->world_size = r.size_of(w);

    const uint8_t* poolreg = r.type("keen::VoxelBlueprintMaterialPoolRegistryResource");
    L->pools = r.off(poolreg, "materialPools");
    const uint8_t* pool = r.type("keen::VoxelBlueprintMaterialPool");
    L->pool_stride = r.size_of(pool);
    L->pool_materials = r.off(pool, "materials");
    L->pool_name = r.off(pool, "debugName");

    const uint8_t* bpreg = r.type("keen::VoxelBlueprintItemRegistryResource");
    L->blueprints = r.off(bpreg, "blueprintItems");
    const uint8_t* bp = r.type("keen::VoxelBlueprintItem");
    L->bp_stride = r.size_of(bp);
    L->bp_item = r.off(bp, "itemId");
    L->bp_data = r.off(bp, "data");
    L->bp_size = r.off(bp, "size");
    L->bp_compressed = r.off(bp, "isDataCompressed");

    L->hardness_names = r.enum_names(r.type("keen::HardnessCategory"));
    L->class_names = r.enum_names(r.type("keen::PropDamageSusceptibility"));
    // What mods write has to mean what it says here.
    if (r.ok) {
        for (int i = 0; i < 6; ++i) {
            std::string want = kHardnessJson[i];
            want[0] = static_cast<char>(want[0] - 'a' + 'A');
            if (L->hardness_names.size() <= static_cast<size_t>(i) || L->hardness_names[i] != want) r.fail("keen::HardnessCategory changed: value " + std::to_string(i) + " isn't " + want);
        }
        for (int i = 0; i < 4; ++i) {
            std::string want = kBreaksJson[i];
            want[0] = static_cast<char>(want[0] - 'a' + 'A');
            if (L->class_names.size() <= static_cast<size_t>(i) || L->class_names[i] != want) r.fail("keen::PropDamageSusceptibility changed: value " + std::to_string(i) + " isn't " + want);
        }
    }

    const uint8_t* item = r.type("keen::ItemInfo");
    I->stride = r.size_of(item);
    I->object_id = r.off(item, "objectId");
    I->debug_name = r.off(item, "debugName");
    uint32_t equipment = 0, voxel = 0;
    const uint8_t* eq_type = nullptr;
    const uint8_t* vox_type = nullptr;
    r.field_rec(item, "equipment", &equipment, &eq_type);
    eq_type = r.resolved(eq_type);
    r.field_rec(eq_type, "voxelData", &voxel, &vox_type);
    vox_type = r.resolved(vox_type);
    I->voxel_guid = equipment + voxel + r.off(vox_type, "placeVoxelMaterial");
    I->voxel_id = equipment + voxel + r.off(vox_type, "placeVoxelMaterialId");
    I->is_building = equipment + voxel + r.off(vox_type, "isBuildingVoxel");
    return r.ok;
}

bool read_model_layout(const uint8_t* image_base, size_t image_size, ModelLayout* M, std::string* why) {
    Image img{image_base, image_size, {}, {}};
    if (!img.open()) {
        *why = "the game's image has no data sections";
        return false;
    }
    const std::string none;
    Reader r{img, none, why};
    const uint8_t* m = r.type("keen::VoxelModelResource");
    M->size = r.size_of(m);
    M->size_field = r.off(m, "size");
    M->data = r.off(m, "data");
    M->terrain = r.off(m, "isTerrain");
    return r.ok;
}

uint32_t layout_hash_of(const uint8_t* image_base, size_t image_size, const std::string& qname) {
    Image img{image_base, image_size, {}, {}};
    if (!img.open()) return 0;
    const uint8_t* r = img.rec(qname);
    return r ? rd<uint32_t>(r + kRecLayout) : 0;
}

}  // namespace kwt
