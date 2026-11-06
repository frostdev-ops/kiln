// SPDX-License-Identifier: MIT
// The three surface tables: read from the game's bytes into slots, and written back in the game's own blob layout. A blob resource is its
// struct, then each BlobArray's elements in field order, each element's own arrays right after its array, at the element type's alignment.
// The game's tables come back byte for byte (tools/surfacecook/tests).
#include <algorithm>
#include <cstring>

#include "surfacecook.h"

namespace ksf {
namespace {

uint32_t rd32(const uint8_t* p) {
    uint32_t v;
    std::memcpy(&v, p, 4);
    return v;
}
uint64_t rd64(const uint8_t* p) {
    uint64_t v;
    std::memcpy(&v, p, 8);
    return v;
}
float rdf(const uint8_t* p) {
    float v;
    std::memcpy(&v, p, 4);
    return v;
}

// A BlobArray or BlobString at `field`: where its elements start. false: they don't fit the resource.
bool array_at(const uint8_t* p, size_t n, size_t field, size_t element, size_t* at, uint32_t* count) {
    if (field + 8 > n) return false;
    const int32_t rel = static_cast<int32_t>(rd32(p + field));
    *count = rd32(p + field + 4);
    if (*count == 0 || rel == 0) {
        *at = 0;
        return *count == 0;
    }
    const int64_t start = static_cast<int64_t>(field) + rel;
    if (start < 0 || static_cast<uint64_t>(start) + uint64_t{*count} * element > n) return false;
    *at = static_cast<size_t>(start);
    return true;
}

class Out {
public:
    explicit Out(size_t root) : b(root, 0) {}
    Bytes b;
    size_t reserve(size_t size, size_t align) {
        const size_t at = (b.size() + align - 1) / align * align;
        b.resize(at + size, 0);
        return at;
    }
    void put8(size_t at, uint8_t v) { b[at] = v; }
    void put16(size_t at, uint16_t v) { std::memcpy(&b[at], &v, 2); }
    void put32(size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
    void put64(size_t at, uint64_t v) { std::memcpy(&b[at], &v, 8); }
    void putf(size_t at, float v) { std::memcpy(&b[at], &v, 4); }
    void array(size_t field, size_t at, uint32_t count) {
        put32(field, static_cast<uint32_t>(static_cast<int32_t>(static_cast<int64_t>(at) - static_cast<int64_t>(field))));
        put32(field + 4, count);
    }
    void bytes(size_t field, const uint8_t* data, size_t n, size_t align) {
        if (!n) return;
        const size_t at = reserve(n, align);
        std::memcpy(&b[at], data, n);
        array(field, at, static_cast<uint32_t>(n));
    }
    void string(size_t field, const std::string& s) { bytes(field, reinterpret_cast<const uint8_t*>(s.data()), s.size(), 1); }
    void reference(size_t at, const kvb::ContentRef& r) {
        put32(at, r.hash.size);
        put32(at + 4, r.hash.h0);
        put32(at + 8, r.hash.h1);
        put32(at + 12, r.hash.h2);
        put32(at + 0x10, r.category);
        put32(at + 0x14, r.bundle);
        put64(at + 0x18, r.sort);
    }
};

kvb::ContentRef read_reference(const uint8_t* p) {
    kvb::ContentRef r;
    r.hash = {rd32(p), rd32(p + 4), rd32(p + 8), rd32(p + 12)};
    r.category = rd32(p + 0x10);
    r.bundle = rd32(p + 0x14);
    r.sort = rd64(p + 0x18);
    return r;
}

// keen::RenderDataBlock
constexpr size_t kRdbName = 0, kRdbSignature = 8, kRdbData = 0x0c, kRdbPatches = 0x14, kRdbTextures = 0x1c, kRdbFeedbacks = 0x24;
constexpr size_t kPatchSize = 16, kParamSize = 96, kFeedbackSize = 8;
// keen::RenderStreamedTexture, in a texture parameter
constexpr size_t kTexContent = 8, kTexTail = 0x28, kTexRanges = 0x48, kTexName = 0x50, kParamCategory = 0x58;

bool read_block(const uint8_t* p, size_t n, size_t base, uint32_t stride, DataBlock* out, std::string* why) {
    auto fail = [&](const std::string& w) {
        *why = w;
        return false;
    };
    DataBlock d;
    d.stride = stride;
    size_t at = 0;
    uint32_t count = 0;
    if (!array_at(p, n, base + kRdbName, 1, &at, &count)) return fail("its name doesn't fit");
    if (count) d.debug_name.assign(reinterpret_cast<const char*>(p + at), count);
    d.signature = rd32(p + base + kRdbSignature);
    if (!array_at(p, n, base + kRdbData, 1, &at, &count)) return fail("its data doesn't fit");
    if (count) d.data.assign(p + at, p + at + count);
    if (d.data.size() % stride) return fail("its data isn't a whole number of slots");
    d.slots.resize(d.data.size() / stride);

    // Texture parameters and feedback lists, each used by exactly one patch in order: that is how the game's are, and what the slots can say.
    size_t tx_at = 0, fb_at = 0, pp_at = 0;
    uint32_t tx_n = 0, fb_n = 0, pp_n = 0;
    if (!array_at(p, n, base + kRdbPatches, kPatchSize, &pp_at, &pp_n)) return fail("its patch points don't fit");
    if (!array_at(p, n, base + kRdbTextures, kParamSize, &tx_at, &tx_n)) return fail("its textures don't fit");
    if (!array_at(p, n, base + kRdbFeedbacks, kFeedbackSize, &fb_at, &fb_n)) return fail("its feedback lists don't fit");
    std::vector<TextureParam> textures(tx_n);
    for (uint32_t i = 0; i < tx_n; ++i) {
        const uint8_t* t = p + tx_at + size_t{i} * kParamSize;
        TextureParam& tp = textures[i];
        std::memcpy(&tp.tex.width, t, 2);
        std::memcpy(&tp.tex.height, t + 2, 2);
        tp.tex.levels = t[4];
        tp.tex.format = t[5];
        tp.tex.content = read_reference(t + kTexContent);
        tp.tex.tail = read_reference(t + kTexTail);
        size_t ra = 0, sa = 0;
        uint32_t rc = 0, sc = 0;
        const size_t field = static_cast<size_t>(t - p);
        if (!array_at(p, n, field + kTexRanges, 8, &ra, &rc)) return fail("a texture's mip ranges don't fit");
        for (uint32_t k = 0; k < rc; ++k) tp.tex.ranges.push_back({rd32(p + ra + size_t{k} * 8), rd32(p + ra + size_t{k} * 8 + 4)});
        if (!array_at(p, n, field + kTexName, 1, &sa, &sc)) return fail("a texture's name doesn't fit");
        if (sc) tp.tex.debug_name.assign(reinterpret_cast<const char*>(p + sa), sc);
        std::memcpy(&tp.category, t + kParamCategory, 2);
    }
    std::vector<std::vector<uint32_t>> lists(fb_n);
    for (uint32_t i = 0; i < fb_n; ++i) {
        size_t la = 0;
        uint32_t lc = 0;
        if (!array_at(p, n, fb_at + size_t{i} * kFeedbackSize, 4, &la, &lc)) return fail("a feedback list doesn't fit");
        for (uint32_t k = 0; k < lc; ++k) lists[i].push_back(rd32(p + la + size_t{k} * 4));
    }
    struct Where {
        uint32_t slot, ordinal;
    };
    std::vector<Where> where(tx_n);
    std::vector<uint32_t> ordinals(d.slots.size(), 0);
    uint32_t next_tex = 0, next_fb = 0;
    uint32_t last = 0;
    for (uint32_t i = 0; i < pp_n; ++i) {
        const uint8_t* q = p + pp_at + size_t{i} * kPatchSize;
        Patch patch;
        patch.type = q[0];
        const uint32_t offset = rd32(q + 4);
        patch.size = rd32(q + 8);
        const uint32_t index = rd32(q + 12);
        if (q[1] || q[2] || q[3]) return fail("a patch point has bytes the layout doesn't name");
        if (offset / stride >= d.slots.size()) return fail("a patch point is past the data");
        const uint32_t slot = offset / stride;
        if (slot < last) return fail("its patch points aren't in slot order");
        last = slot;
        patch.offset = offset % stride;
        if (patch.type == kPatchTexture) {
            if (index != next_tex++) return fail("its textures aren't one per patch point, in order");
            patch.texture = textures[index];
            where[index] = {slot, ordinals[slot]++};
        } else if (patch.type == kPatchFeedback) {
            if (index != next_fb++) return fail("its feedback lists aren't one per patch point, in order");
            for (uint32_t t : lists[index]) {
                if (t >= tx_n || t >= next_tex || where[t].slot != slot) return fail("a feedback list names a texture of another slot");
                patch.list.push_back(where[t].ordinal);
            }
        } else {
            return fail("a patch point has an unknown type");
        }
        d.slots[slot].push_back(std::move(patch));
    }
    if (next_tex != tx_n || next_fb != fb_n) return fail("it has textures or feedback lists no patch point uses");
    *out = std::move(d);
    return true;
}

void write_block(Out& w, size_t base, const DataBlock& d) {
    w.string(base + kRdbName, d.debug_name);
    w.put32(base + kRdbSignature, d.signature);
    w.bytes(base + kRdbData, d.data.data(), d.data.size(), 1);
    std::vector<const Patch*> textures;
    struct Row {
        uint8_t type;
        uint32_t offset, size, index;
    };
    std::vector<Row> rows;
    std::vector<std::vector<uint32_t>> lists;
    for (size_t s = 0; s < d.slots.size(); ++s) {
        const uint32_t base_tex = static_cast<uint32_t>(textures.size());
        for (const Patch& patch : d.slots[s]) {
            const uint32_t offset = static_cast<uint32_t>(s * d.stride + patch.offset);
            if (patch.type == kPatchTexture) {
                rows.push_back({kPatchTexture, offset, patch.size, static_cast<uint32_t>(textures.size())});
                textures.push_back(&patch);
            } else {
                rows.push_back({kPatchFeedback, offset, patch.size, static_cast<uint32_t>(lists.size())});
                std::vector<uint32_t> l;
                for (uint32_t o : patch.list) l.push_back(base_tex + o);
                lists.push_back(std::move(l));
            }
        }
    }
    if (!rows.empty()) {
        const size_t at = w.reserve(kPatchSize * rows.size(), 4);
        w.array(base + kRdbPatches, at, static_cast<uint32_t>(rows.size()));
        for (size_t i = 0; i < rows.size(); ++i) {
            const size_t r = at + i * kPatchSize;
            w.put8(r, rows[i].type);
            w.put32(r + 4, rows[i].offset);
            w.put32(r + 8, rows[i].size);
            w.put32(r + 12, rows[i].index);
        }
    }
    if (!textures.empty()) {
        const size_t at = w.reserve(kParamSize * textures.size(), 8);
        w.array(base + kRdbTextures, at, static_cast<uint32_t>(textures.size()));
        for (size_t i = 0; i < textures.size(); ++i) {
            const size_t t = at + i * kParamSize;
            const TextureParam& tp = textures[i]->texture;
            w.put16(t, tp.tex.width);
            w.put16(t + 2, tp.tex.height);
            w.put8(t + 4, tp.tex.levels);
            w.put8(t + 5, tp.tex.format);
            w.reference(t + kTexContent, tp.tex.content);
            w.reference(t + kTexTail, tp.tex.tail);
            w.put16(t + kParamCategory, tp.category);
        }
        for (size_t i = 0; i < textures.size(); ++i) {
            const size_t t = at + i * kParamSize;
            const kvb::StreamedTexture& tex = textures[i]->texture.tex;
            if (!tex.ranges.empty()) {
                const size_t r = w.reserve(8 * tex.ranges.size(), 4);
                w.array(t + kTexRanges, r, static_cast<uint32_t>(tex.ranges.size()));
                for (size_t k = 0; k < tex.ranges.size(); ++k) {
                    w.put32(r + k * 8, tex.ranges[k].start);
                    w.put32(r + k * 8 + 4, tex.ranges[k].count);
                }
            }
            w.string(t + kTexName, tex.debug_name);
        }
    }
    if (!lists.empty()) {
        const size_t at = w.reserve(kFeedbackSize * lists.size(), 4);
        w.array(base + kRdbFeedbacks, at, static_cast<uint32_t>(lists.size()));
        for (size_t i = 0; i < lists.size(); ++i) {
            if (lists[i].empty()) continue;
            const size_t la = w.reserve(4 * lists[i].size(), 4);
            w.array(at + i * kFeedbackSize, la, static_cast<uint32_t>(lists[i].size()));
            std::memcpy(&w.b[la], lists[i].data(), 4 * lists[i].size());
        }
    }
}

constexpr size_t kResourceSize = 48;  // both table resources
constexpr size_t kParamsSize = 16, kMaterialSize = 12, kRuleSize = 80, kModelSize = 16;
constexpr size_t kRuleLists[3] = {0x34, 0x3c, 0x44};

}  // namespace

bool read_blending(const uint8_t* p, size_t n, BlendingTable* out, std::string* why) {
    if (n < kResourceSize) {
        *why = "the blending table is shorter than its struct";
        return false;
    }
    BlendingTable t;
    t.layer_count = rd32(p);
    if (!read_block(p, n, 4, kSlotBytes, &t.block, why)) {
        *why = "the blending table: " + *why;
        return false;
    }
    *out = std::move(t);
    return true;
}

bool read_gi(const uint8_t* p, size_t n, GiTable* out, std::string* why) {
    if (n < kResourceSize) {
        *why = "the GI table is shorter than its struct";
        return false;
    }
    GiTable t;
    if (!read_block(p, n, 0, kGiSlotBytes, &t.block, why)) {
        *why = "the GI table: " + *why;
        return false;
    }
    t.material_count = rd32(p + 0x2c);
    *out = std::move(t);
    return true;
}

bool read_params(const uint8_t* p, size_t n, ParamsTable* out, std::string* why) {
    auto fail = [&](const char* w) {
        *why = std::string("the parameter table: ") + w;
        return false;
    };
    if (n < kParamsSize) return fail("shorter than its struct");
    ParamsTable t;
    size_t at = 0;
    uint32_t count = 0;
    if (!array_at(p, n, 0, kMaterialSize, &at, &count)) return fail("its materials don't fit");
    for (uint32_t i = 0; i < count; ++i) {
        const size_t m = at + size_t{i} * kMaterialSize;
        MaterialParams mp;
        mp.mapping = p[m];
        mp.edge_only_side = p[m + 1];
        if (p[m + 2] || p[m + 3]) return fail("a material has bytes the layout doesn't name");
        size_t ra = 0;
        uint32_t rc = 0;
        if (!array_at(p, n, m + 4, kRuleSize, &ra, &rc)) return fail("a material's decoration doesn't fit");
        for (uint32_t r = 0; r < rc; ++r) {
            const size_t rr = ra + size_t{r} * kRuleSize;
            DecoRule rule;
            std::memcpy(rule.raw.data(), p + rr, kRuleSize);
            for (int l = 0; l < 3; ++l) {
                size_t la = 0;
                uint32_t lc = 0;
                if (!array_at(p, n, rr + kRuleLists[l], 4, &la, &lc)) return fail("a decoration's models don't fit");
                for (uint32_t k = 0; k < lc; ++k) rule.models[l].push_back(rd32(p + la + size_t{k} * 4));
                std::memset(rule.raw.data() + kRuleLists[l], 0, 8);
            }
            mp.rules.push_back(std::move(rule));
        }
        t.materials.push_back(std::move(mp));
    }
    if (!array_at(p, n, 8, kModelSize, &at, &count)) return fail("its models don't fit");
    for (uint32_t i = 0; i < count; ++i) {
        std::array<uint8_t, 16> g;
        std::memcpy(g.data(), p + at + size_t{i} * kModelSize, 16);
        t.models.push_back(g);
    }
    *out = std::move(t);
    return true;
}

Bytes write_blending(const BlendingTable& t) {
    Out w(kResourceSize);
    w.put32(0, t.layer_count);
    write_block(w, 4, t.block);
    return std::move(w.b);
}

Bytes write_gi(const GiTable& t) {
    Out w(kResourceSize);
    write_block(w, 0, t.block);
    w.put32(0x2c, t.material_count);
    return std::move(w.b);
}

Bytes write_params(const ParamsTable& t) {
    Out w(kParamsSize);
    if (!t.materials.empty()) {
        const size_t at = w.reserve(kMaterialSize * t.materials.size(), 4);
        w.array(0, at, static_cast<uint32_t>(t.materials.size()));
        for (size_t i = 0; i < t.materials.size(); ++i) {
            const size_t m = at + i * kMaterialSize;
            const MaterialParams& mp = t.materials[i];
            w.put8(m, mp.mapping);
            w.put8(m + 1, mp.edge_only_side);
            if (mp.rules.empty()) continue;
            const size_t ra = w.reserve(kRuleSize * mp.rules.size(), 16);
            w.array(m + 4, ra, static_cast<uint32_t>(mp.rules.size()));
            for (size_t r = 0; r < mp.rules.size(); ++r) std::memcpy(&w.b[ra + r * kRuleSize], mp.rules[r].raw.data(), kRuleSize);
            for (size_t r = 0; r < mp.rules.size(); ++r)
                for (int l = 0; l < 3; ++l) {
                    const std::vector<uint32_t>& list = mp.rules[r].models[l];
                    if (list.empty()) continue;
                    const size_t la = w.reserve(4 * list.size(), 4);
                    w.array(ra + r * kRuleSize + kRuleLists[l], la, static_cast<uint32_t>(list.size()));
                    std::memcpy(&w.b[la], list.data(), 4 * list.size());
                }
        }
    }
    if (!t.models.empty()) {
        const size_t at = w.reserve(kModelSize * t.models.size(), 4);
        w.array(8, at, static_cast<uint32_t>(t.models.size()));
        for (size_t i = 0; i < t.models.size(); ++i) std::memcpy(&w.b[at + i * kModelSize], t.models[i].data(), 16);
    }
    return std::move(w.b);
}

// ---- slots -------------------------------------------------------------------------------------------------------------------------
namespace {
constexpr uint32_t kLayerEmInt = 0x04, kLayerEmExp = 0x08, kLayerTiling = 0x2c, kLayerDispScale = 0x30, kLayerDispOffset = 0x34, kLayerEmGi = 0x38;
constexpr uint32_t kSlotHeightBlend = 0x168, kSlotHeightBlendNoise = 0x16c, kSlotEdgeCurve = 0x170, kSlotEdgeOnlySide = 0x174, kSlotNoise = 0x178,
                   kSlotBevel = 0x180, kSlotRoof = 0x184;

int map_at(uint32_t offset) {
    for (int m = 0; m < kMapCount; ++m)
        if (kMapOffset[m] == offset) return m;
    return -1;
}
}  // namespace

bool slot_of(const BlendingTable& b, const GiTable& g, const ParamsTable& p, uint32_t index, Slot* out, std::string* why) {
    auto fail = [&](const std::string& w) {
        *why = "slot " + std::to_string(index) + ": " + w;
        return false;
    };
    if (index >= b.block.slots.size() || index >= g.block.slots.size()) return fail("is past the tables");
    Slot s;
    const uint8_t* d = b.block.data.data() + size_t{index} * kSlotBytes;
    // which ordinal each layer's textures have, to check the feedback lists
    std::vector<std::pair<int, int>> ordinal;  // (layer, map) per texture patch
    for (const Patch& patch : b.block.slots[index]) {
        const int layer = static_cast<int>(patch.offset / kLayerBytes);
        const uint32_t field = patch.offset % kLayerBytes;
        if (layer >= kPartCount || patch.size != 4) return fail("a patch point outside the layers");
        if (patch.type == kPatchTexture) {
            const int m = map_at(field);
            if (m < 0 || s.layer[layer].map[m]) return fail("a texture patch at an unknown place");
            s.layer[layer].map[m] = patch.texture;
            ordinal.push_back({layer, m});
        } else {
            if (field != kFeedbackOffset) return fail("a feedback patch at an unknown place");
            std::vector<uint32_t> want;
            for (uint32_t o = 0; o < ordinal.size(); ++o)
                if (ordinal[o].first == layer) want.push_back(o);
            if (patch.list != want) return fail("a feedback list that isn't its layer's textures");
        }
    }
    for (int l = 0; l < kPartCount; ++l) {
        SlotLayer& L = s.layer[l];
        const uint8_t* q = d + size_t(l) * kLayerBytes;
        bool any = false;
        for (int m = 0; m < kMapCount; ++m) {
            if (rd32(q + kMapOffset[m])) return fail("a map handle that isn't 0");
            any = any || L.map[m].has_value();
        }
        if (rd32(q + kFeedbackOffset)) return fail("a feedback handle that isn't 0");
        bool fb = false;
        for (const Patch& patch : b.block.slots[index])
            fb = fb || (patch.type == kPatchFeedback && patch.offset == l * kLayerBytes + kFeedbackOffset);
        if (fb != any) return fail("a layer whose feedback list doesn't match its maps");
        L.feedback = fb;
        L.emissive_intensity = rdf(q + kLayerEmInt);
        L.emissive_exposure = rdf(q + kLayerEmExp);
        L.tiling = rdf(q + kLayerTiling);
        L.displacement_scale = rdf(q + kLayerDispScale);
        L.displacement_offset = rdf(q + kLayerDispOffset);
        L.emissive_gi = rdf(q + kLayerEmGi);
    }
    s.height_blend = rdf(d + kSlotHeightBlend);
    s.height_blend_noise = rdf(d + kSlotHeightBlendNoise);
    s.edge_curve = rdf(d + kSlotEdgeCurve);
    s.edge_only_side = rd32(d + kSlotEdgeOnlySide);
    s.noise_scale = rdf(d + kSlotNoise);
    s.noise_bias = rdf(d + kSlotNoise + 4);
    s.bevel = rdf(d + kSlotBevel);
    s.is_roof = rd32(d + kSlotRoof);

    const uint8_t* gd = g.block.data.data() + size_t{index} * kGiSlotBytes;
    for (int f = 0; f < 3; ++f) {
        const uint8_t* q = gd + size_t(f) * kGiFaceBytes;
        for (int i = 0; i < 13; ++i) s.gi[f].values[i] = rdf(q + i * 4);
        if (rd32(q + kGiEmissiveOffset) || rd32(q + kGiAlbedoOffset)) return fail("a GI map handle that isn't 0");
    }
    for (const Patch& patch : g.block.slots[index]) {
        const uint32_t f = patch.offset / kGiFaceBytes, field = patch.offset % kGiFaceBytes;
        if (patch.type != kPatchTexture || f >= 3 || patch.size != 4) return fail("a GI patch point it can't say");
        if (field == kGiEmissiveOffset && !s.gi[f].emissive) s.gi[f].emissive = patch.texture;
        else if (field == kGiAlbedoOffset && !s.gi[f].albedo) s.gi[f].albedo = patch.texture;
        else return fail("a GI patch point at an unknown place");
    }
    if (index < p.materials.size()) s.params = p.materials[index];
    *out = std::move(s);
    return true;
}

void put_slot(BlendingTable* b, GiTable* g, ParamsTable* p, uint32_t index, const Slot& s) {
    // blending
    uint8_t* d = b->block.data.data() + size_t{index} * kSlotBytes;
    std::memset(d, 0, kSlotBytes);
    std::vector<Patch>& patches = b->block.slots[index];
    patches.clear();
    uint32_t ordinal = 0;
    for (int l = 0; l < kPartCount; ++l) {
        const SlotLayer& L = s.layer[l];
        uint8_t* q = d + size_t(l) * kLayerBytes;
        std::memcpy(q + kLayerEmInt, &L.emissive_intensity, 4);
        std::memcpy(q + kLayerEmExp, &L.emissive_exposure, 4);
        std::memcpy(q + kLayerTiling, &L.tiling, 4);
        std::memcpy(q + kLayerDispScale, &L.displacement_scale, 4);
        std::memcpy(q + kLayerDispOffset, &L.displacement_offset, 4);
        std::memcpy(q + kLayerEmGi, &L.emissive_gi, 4);
        std::vector<uint32_t> list;
        for (int m = 0; m < kMapCount; ++m) {
            if (!L.map[m]) continue;
            Patch patch;
            patch.type = kPatchTexture;
            patch.offset = l * kLayerBytes + kMapOffset[m];
            patch.texture = *L.map[m];
            patches.push_back(std::move(patch));
            list.push_back(ordinal++);
        }
        if (L.feedback && !list.empty()) {
            Patch patch;
            patch.type = kPatchFeedback;
            patch.offset = l * kLayerBytes + kFeedbackOffset;
            patch.list = std::move(list);
            patches.push_back(std::move(patch));
        }
    }
    std::memcpy(d + kSlotHeightBlend, &s.height_blend, 4);
    std::memcpy(d + kSlotHeightBlendNoise, &s.height_blend_noise, 4);
    std::memcpy(d + kSlotEdgeCurve, &s.edge_curve, 4);
    std::memcpy(d + kSlotEdgeOnlySide, &s.edge_only_side, 4);
    std::memcpy(d + kSlotNoise, &s.noise_scale, 4);
    std::memcpy(d + kSlotNoise + 4, &s.noise_bias, 4);
    std::memcpy(d + kSlotBevel, &s.bevel, 4);
    std::memcpy(d + kSlotRoof, &s.is_roof, 4);
    b->layer_count = std::max(b->layer_count, index + 1);

    // GI
    uint8_t* gd = g->block.data.data() + size_t{index} * kGiSlotBytes;
    std::memset(gd, 0, kGiSlotBytes);
    std::vector<Patch>& gp = g->block.slots[index];
    gp.clear();
    for (int f = 0; f < 3; ++f) {
        uint8_t* q = gd + size_t(f) * kGiFaceBytes;
        std::memcpy(q, s.gi[f].values.data(), 13 * 4);
        // the game lists a face's albedo before its emissive
        for (const auto& [field, tex] : {std::pair{kGiAlbedoOffset, &s.gi[f].albedo}, std::pair{kGiEmissiveOffset, &s.gi[f].emissive}}) {
            if (!*tex) continue;
            Patch patch;
            patch.type = kPatchTexture;
            patch.offset = f * kGiFaceBytes + field;
            patch.texture = **tex;
            gp.push_back(std::move(patch));
        }
    }
    g->material_count = std::max(g->material_count, index + 1);

    // parameters: a list as long as the count
    if (p->materials.size() <= index) p->materials.resize(index + 1);
    p->materials[index] = s.params;
}

ChainLayout chain_layout(uint32_t w, uint32_t h, uint8_t format) {
    ChainLayout c;
    const uint32_t block = kvb::format_block_bytes(format);
    std::vector<uint64_t> sizes;
    for (uint32_t lw = w, lh = h;; lw = std::max(1u, lw / 2), lh = std::max(1u, lh / 2)) {
        sizes.push_back(uint64_t{(lw + 3) / 4} * ((lh + 3) / 4) * block);
        c.total += sizes.back();
        if (lw == 1 && lh == 1) break;
    }
    c.levels = static_cast<uint32_t>(sizes.size());
    uint64_t cum = 0;
    for (uint64_t s : sizes) {
        if (c.total - cum > 4096) {
            c.ranges.push_back({static_cast<uint32_t>(cum), static_cast<uint32_t>(c.total - cum)});
            c.tail_start = cum + s;
        }
        cum += s;
    }
    return c;
}

}  // namespace ksf
