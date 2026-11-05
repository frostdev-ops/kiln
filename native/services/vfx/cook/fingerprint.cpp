// SPDX-License-Identifier: MIT
// What the modules were made against. Reflection records are static data in the exe, so reading them needs no signature and no game code
// (record layout: tools/kfc/kfc/refl.py). A game update that changes a layout turns Kiln's particles off with a clear line.
#include <algorithm>
#include <bitset>
#include <cstring>
#include <filesystem>
#include <fstream>

#include "vfxcook.h"

namespace kvb {
namespace {

namespace fs = std::filesystem;

constexpr uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<unsigned char>(*s)) * 0x01000193u;
    return h;
}

template <class T>
T rd(const uint8_t* p) {
    T v;
    std::memcpy(&v, p, sizeof v);
    return v;
}

// Reflection record
constexpr size_t kRecName = 0x20, kRecNameLen = 0x28, kRecHash = 0x50, kRecLayout = 0x54, kRecSize = 0x68;

fs::path to_path(const std::string& utf8) { return fs::path(std::u8string(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size())); }

struct Section {
    uint32_t va, size;
};

bool sections(const uint8_t* img, size_t size, std::vector<Section>* out) {
    if (size < 0x400 || img[0] != 'M' || img[1] != 'Z') return false;
    const uint32_t pe = rd<uint32_t>(img + 0x3c);
    if (pe + 0x18 > size || std::memcmp(img + pe, "PE\0\0", 4) != 0) return false;
    const uint16_t count = rd<uint16_t>(img + pe + 6), opt = rd<uint16_t>(img + pe + 0x14);
    const size_t table = pe + 0x18 + opt;
    if (table + size_t{count} * 40 > size) return false;
    for (uint16_t i = 0; i < count; ++i) {
        const uint8_t* s = img + table + size_t{i} * 40;
        if (std::memcmp(s, ".data\0", 6) == 0 || std::memcmp(s, ".rdata\0", 7) == 0)
            out->push_back({rd<uint32_t>(s + 12), rd<uint32_t>(s + 8)});
    }
    return !out->empty();
}

}  // namespace

const std::vector<LayoutExpect>& expected_layouts() {
    // Client 26430625, svn 1287369. The first group is what the builder writes, the second what the modules read and write on the GPU.
    static const std::vector<LayoutExpect> k = [] {
        const std::vector<std::pair<const char*, uint32_t>> list = {
            {"keen::VfxResource", 0xf6128527u},
            {"keen::VfxParticleResource", 0x0b354243u},
            {"keen::VfxParticleStateBufferInfo", 0x50b319a0u},
            {"keen::VfxBufferMember", 0x562c8e7fu},
            {"keen::VfxMeshResource", 0xe79cbc08u},
            {"keen::VfxPatchPoint", 0x85d9cb74u},
            {"keen::VfxMeshPatchPoint", 0x57298d7bu},
            {"keen::VfxImagePatchPoint", 0xe46e2e74u},
            {"keen::RenderTextureResource", 0xef11efbcu},
            {"keen::ResourceContentReference", 0x86ac181eu},
            {"keen::TransparentInstanceData", 0x510d10efu},
            {"keen::VfxUpdateBuffer", 0x3609c027u},
            {"keen::VfxChunkUpdateBuffer", 0xb7750d24u},
            {"keen::VfxPushConstants", 0x4ee4d238u},
            {"keen::PointLightData", 0x727316fcu},
            {"keen::VfxEmitterState", 0x5685ca19u},
            {"keen::VfxMeshLayout", 0x3ef8c1beu},
            {"keen::VfxCombinedEffectParameters", 0x12e19673u},
            {"keen::GpcResource", 0x3ea062ceu},
            {"keen::GpcDescriptorSetLayoutResource", 0x0fc3525fu},
            {"keen::GpcDescriptorSetBindingResource", 0x8e28cbb5u},
        };
        std::vector<LayoutExpect> v;
        for (const auto& [name, layout] : list) v.push_back({name, fnv1a(name), layout});
        return v;
    }();
    return k;
}

Guid vfx_gpc_guid() {
    // vfx/vfx.gpc: ad606c3f-f737-51b2-9617-e0c5aba82b11, in the game's byte order
    return Guid{{0x3f, 0x6c, 0x60, 0xad, 0x37, 0xf7, 0xb2, 0x51, 0x96, 0x17, 0xe0, 0xc5, 0xab, 0xa8, 0x2b, 0x11}};
}

std::vector<LayoutFound> read_layouts(const ImageView& v) {
    const auto& want = expected_layouts();
    std::vector<LayoutFound> out;
    for (const LayoutExpect& e : want) out.push_back({e.name, e.layout, 0});
    std::vector<Section> secs;
    if (!sections(v.image, v.size, &secs)) return out;
    std::bitset<65536> filter;
    for (const LayoutExpect& e : want) filter.set(e.type_hash & 0xffff);
    for (const Section& s : secs) {
        const size_t end = std::min<size_t>(size_t{s.va} + s.size, v.size);
        for (size_t o = (s.va + 7) & ~size_t{7}; o + kRecSize <= end; o += 8) {
            const uint32_t h = rd<uint32_t>(v.image + o + kRecHash);
            if (!filter.test(h & 0xffff)) continue;
            for (size_t i = 0; i < want.size(); ++i) {
                if (want[i].type_hash != h) continue;
                const uint64_t name = rd<uint64_t>(v.image + o + kRecName), len = rd<uint64_t>(v.image + o + kRecNameLen);
                if (name < v.base || name - v.base >= v.size || len != std::strlen(want[i].name) || name - v.base + len > v.size) break;
                if (std::memcmp(v.image + (name - v.base), want[i].name, len) == 0) out[i].found = rd<uint32_t>(v.image + o + kRecLayout);
                break;
            }
        }
    }
    return out;
}

bool load_pe_file(const std::string& path, Bytes* image, uint64_t* base, std::string* why) {
    std::ifstream f(to_path(path), std::ios::binary);
    if (!f) {
        *why = "can't open " + path;
        return false;
    }
    Bytes file((std::istreambuf_iterator<char>(f)), {});
    if (file.size() < 0x400 || file[0] != 'M' || file[1] != 'Z') {
        *why = path + " is not a PE file";
        return false;
    }
    const uint32_t pe = rd<uint32_t>(&file[0x3c]);
    if (pe + 0x88 > file.size() || std::memcmp(&file[pe], "PE\0\0", 4) != 0) {
        *why = path + " is not a PE file";
        return false;
    }
    const uint16_t count = rd<uint16_t>(&file[pe + 6]), opt = rd<uint16_t>(&file[pe + 0x14]);
    *base = rd<uint64_t>(&file[pe + 0x18 + 0x18]);
    const uint32_t image_size = rd<uint32_t>(&file[pe + 0x18 + 0x38]);
    const uint32_t headers = rd<uint32_t>(&file[pe + 0x18 + 0x3c]);
    image->assign(image_size, 0);
    std::memcpy(image->data(), file.data(), std::min<size_t>({headers, file.size(), image_size}));
    const size_t table = pe + 0x18 + opt;
    for (uint16_t i = 0; i < count; ++i) {
        const uint8_t* s = &file[table + size_t{i} * 40];
        const uint32_t va = rd<uint32_t>(s + 12), raw_size = rd<uint32_t>(s + 16), raw = rd<uint32_t>(s + 20);
        if (raw && raw + size_t{raw_size} <= file.size() && va + size_t{raw_size} <= image_size) std::memcpy(image->data() + va, &file[raw], raw_size);
    }
    return true;
}

std::string compare_layouts(const std::vector<LayoutFound>& found) {
    std::string s;
    for (const LayoutFound& f : found) {
        if (f.found == f.expected) continue;
        char b[160];
        if (f.found) std::snprintf(b, sizeof b, "%s layout %08x, made for %08x", f.name, f.found, f.expected);
        else std::snprintf(b, sizeof b, "%s not found", f.name);
        s += (s.empty() ? "" : "; ") + std::string(b);
    }
    return s;
}

bool binding_hash_of_gpc(const uint8_t* gpc, size_t len, uint32_t* out, uint32_t* count) {
    // GpcResource: descriptorSetLayouts BlobArray at +0x28. GpcDescriptorSetLayoutResource (28 B): debugName at +0, bindings at +0x0c.
    // GpcDescriptorSetBindingResource (24 B): debugName at +0, type u8 at +8, arraySizeOrBufferStride u32 at +0x10.
    auto arr = [&](size_t field, size_t stride, size_t* at, uint32_t* n) {
        if (field + 8 > len) return false;
        const int32_t rel = rd<int32_t>(gpc + field);
        *n = rd<uint32_t>(gpc + field + 4);
        if (*n == 0) {
            *at = 0;
            return true;
        }
        const int64_t a = static_cast<int64_t>(field) + rel;
        if (a < 0 || static_cast<uint64_t>(a) + uint64_t{*n} * stride > len) return false;
        *at = static_cast<size_t>(a);
        return true;
    };
    const char kName[] = "VfxUpdateDescriptorSetLayout";
    size_t layouts = 0;
    uint32_t nl = 0;
    if (!arr(0x28, 28, &layouts, &nl)) return false;
    for (uint32_t i = 0; i < nl; ++i) {
        const size_t l = layouts + size_t{i} * 28;
        size_t name = 0;
        uint32_t name_len = 0;
        if (!arr(l, 1, &name, &name_len)) return false;
        if (name_len != sizeof kName - 1 || std::memcmp(gpc + name, kName, name_len) != 0) continue;
        size_t b = 0;
        uint32_t nb = 0;
        if (!arr(l + 0x0c, 24, &b, &nb)) return false;
        uint32_t h = 0x811c9dc5u;
        auto mix = [&](const void* p, size_t n) {
            const uint8_t* c = static_cast<const uint8_t*>(p);
            for (size_t k = 0; k < n; ++k) h = (h ^ c[k]) * 0x01000193u;
        };
        for (uint32_t k = 0; k < nb; ++k) {
            const size_t e = b + size_t{k} * 24;
            size_t en = 0;
            uint32_t el = 0;
            if (!arr(e, 1, &en, &el)) return false;
            mix(gpc + en, el);
            mix("\0", 1);
            mix(gpc + e + 8, 1);
            mix(gpc + e + 0x10, 4);
        }
        *out = h;
        *count = nb;
        return true;
    }
    return false;
}

bool read_kfc_bundles(const std::string& path, std::map<uint32_t, KfcBundle>* out, std::string* why) {
    std::ifstream f(to_path(path), std::ios::binary);
    if (!f) {
        *why = "can't open " + path;
        return false;
    }
    uint8_t head[0x90];
    f.read(reinterpret_cast<char*>(head), sizeof head);
    if (!f || std::memcmp(head, "KFC4", 4) != 0) {
        *why = path + " is not a KFC4 directory";
        return false;
    }
    // A location is {u32 offset relative to its own position, u32 count}. 0x78 keys (u32 type hash), 0x80 values {layout, first, count}.
    const uint32_t keys_off = rd<uint32_t>(head + 0x78), keys_n = rd<uint32_t>(head + 0x7c);
    const uint32_t vals_off = rd<uint32_t>(head + 0x80), vals_n = rd<uint32_t>(head + 0x84);
    if (keys_n != vals_n || keys_n > 65536) {
        *why = path + ": its tables don't look right";
        return false;
    }
    std::vector<uint32_t> keys(keys_n);
    std::vector<uint32_t> vals(size_t{vals_n} * 3);
    f.seekg(0x78 + std::streamoff{keys_off});
    f.read(reinterpret_cast<char*>(keys.data()), static_cast<std::streamsize>(keys.size() * 4));
    f.seekg(0x80 + std::streamoff{vals_off});
    f.read(reinterpret_cast<char*>(vals.data()), static_cast<std::streamsize>(vals.size() * 4));
    if (!f) {
        *why = path + ": its tables are cut short";
        return false;
    }
    for (uint32_t i = 0; i < keys_n; ++i) (*out)[keys[i]] = KfcBundle{vals[size_t{i} * 3], vals[size_t{i} * 3 + 2]};
    return true;
}

bool kfc_has_content(const std::string& path, const ContentHash& hash) {
    std::ifstream f(to_path(path), std::ios::binary);
    if (!f) return false;
    uint8_t head[0x50];
    f.read(reinterpret_cast<char*>(head), sizeof head);
    if (!f || std::memcmp(head, "KFC4", 4) != 0) return false;
    // 0x48: content keys, {u32 size, h0, h1, h2}.
    const uint32_t off = rd<uint32_t>(head + 0x48), count = rd<uint32_t>(head + 0x4c);
    f.seekg(0x48 + std::streamoff{off});
    std::vector<ContentHash> keys(count);
    f.read(reinterpret_cast<char*>(keys.data()), static_cast<std::streamsize>(keys.size() * sizeof(ContentHash)));
    return f && std::find(keys.begin(), keys.end(), hash) != keys.end();
}

GameCounts game_counts_from(const std::map<uint32_t, KfcBundle>& bundles) {
    GameCounts c;
    auto take = [&](uint32_t type, uint32_t* out) {
        const auto it = bundles.find(type);
        if (it != bundles.end() && it->second.count) *out = it->second.count;
    };
    take(kTypeVfx, &c.vfx);
    take(kTypeMesh, &c.meshes);
    take(fnv1a("keen::VfxCurveResource"), &c.curves);
    return c;
}

}  // namespace kvb
