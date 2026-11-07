// SPDX-License-Identifier: MIT
// keen::RenderMaterialResource: its reader and writer, and a MaterialDef -> the resource and its textures. Layouts and every rule here are in
// docs/re/vfx.md section 5. The reader and writer round-trip the game's own materials byte for byte, which tools/vfxcook/tests checks when the
// game is installed; nothing from the game goes into what is built.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

#include "vfxcook.h"

namespace kvb {
namespace {

constexpr size_t kMaterialSize = 40, kImageSize = 96, kFeedbackSize = 12;
// keen::RenderStreamedTexture, at +8 of an image
constexpr size_t kTexContent = 8, kTexTail = 0x28, kTexRanges = 0x48, kTexName = 0x50;

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

class Out {
public:
    explicit Out(size_t root) : b(root, 0) {}
    Bytes b;
    size_t reserve(size_t size, size_t align) {
        const size_t at = (b.size() + align - 1) / align * align;
        b.resize(at + size, 0);
        return at;
    }
    void put32(size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
    void array(size_t field, size_t at, uint32_t count) {
        put32(field, static_cast<uint32_t>(static_cast<int32_t>(static_cast<int64_t>(at) - static_cast<int64_t>(field))));
        put32(field + 4, count);
    }
    void string(size_t field, const std::string& s) {
        if (s.empty()) return;
        const size_t at = reserve(s.size(), 1);
        std::memcpy(&b[at], s.data(), s.size());
        array(field, at, static_cast<uint32_t>(s.size()));
    }
    void reference(size_t at, const ContentRef& r) {
        put32(at, r.hash.size);
        put32(at + 4, r.hash.h0);
        put32(at + 8, r.hash.h1);
        put32(at + 12, r.hash.h2);
        put32(at + 0x10, r.category);
        put32(at + 0x14, r.bundle);
        std::memcpy(&b[at + 0x18], &r.sort, 8);
    }
};

// A blob array or string: where its elements start, or false if they don't fit.
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

ContentRef read_reference(const uint8_t* p) {
    ContentRef r;
    r.hash = {rd32(p), rd32(p + 4), rd32(p + 8), rd32(p + 12)};
    r.category = rd32(p + 0x10);
    r.bundle = rd32(p + 0x14);
    r.sort = rd64(p + 0x18);
    return r;
}

}  // namespace

Bytes write_material(const RenderMaterial& m) {
    Out w(kMaterialSize);
    w.b[0] = m.effect;
    w.b[1] = m.flags;
    w.put32(0x0c, m.signature);
    if (!m.data.empty()) {
        const size_t at = w.reserve(m.data.size(), 1);
        std::memcpy(&w.b[at], m.data.data(), m.data.size());
        w.array(4, at, static_cast<uint32_t>(m.data.size()));
    }
    if (!m.images.empty()) {
        const size_t images = w.reserve(kImageSize * m.images.size(), 8);
        w.array(0x10, images, static_cast<uint32_t>(m.images.size()));
        for (size_t i = 0; i < m.images.size(); ++i) {
            const size_t im = images + kImageSize * i;
            const MaterialImage& image = m.images[i];
            const StreamedTexture& t = image.texture;
            const size_t tx = im + 8;
            w.put32(im, image.data_offset);
            std::memcpy(&w.b[tx], &t.width, 2);
            std::memcpy(&w.b[tx + 2], &t.height, 2);
            w.b[tx + 4] = t.levels;
            w.b[tx + 5] = t.format;
            w.reference(tx + kTexContent, t.content);
            w.reference(tx + kTexTail, t.tail);
            if (!t.ranges.empty()) {
                const size_t r = w.reserve(8 * t.ranges.size(), 4);
                w.array(tx + kTexRanges, r, static_cast<uint32_t>(t.ranges.size()));
                for (size_t k = 0; k < t.ranges.size(); ++k) {
                    w.put32(r + k * 8, t.ranges[k].start);
                    w.put32(r + k * 8 + 4, t.ranges[k].count);
                }
            }
            w.string(tx + kTexName, t.debug_name);
        }
    }
    if (!m.feedbacks.empty()) {
        const size_t feedbacks = w.reserve(kFeedbackSize * m.feedbacks.size(), 4);
        w.array(0x18, feedbacks, static_cast<uint32_t>(m.feedbacks.size()));
        for (size_t i = 0; i < m.feedbacks.size(); ++i) {
            const size_t f = feedbacks + kFeedbackSize * i;
            w.put32(f, m.feedbacks[i].data_offset);
            const std::vector<uint32_t>& ix = m.feedbacks[i].images;
            if (!ix.empty()) {
                const size_t a = w.reserve(4 * ix.size(), 4);
                w.array(f + 4, a, static_cast<uint32_t>(ix.size()));
                std::memcpy(&w.b[a], ix.data(), 4 * ix.size());
            }
        }
    }
    w.string(0x20, m.debug_name);
    return std::move(w.b);
}

bool read_material(const uint8_t* p, size_t n, RenderMaterial* out, std::string* why) {
    auto fail = [&](const char* what) {
        *why = what;
        return false;
    };
    if (n < kMaterialSize) return fail("shorter than the resource's 40 bytes");
    RenderMaterial m;
    m.effect = p[0];
    m.flags = p[1];
    m.signature = rd32(p + 0x0c);
    size_t at = 0;
    uint32_t count = 0;
    if (!array_at(p, n, 4, 1, &at, &count)) return fail("data doesn't fit");
    if (count) m.data.assign(p + at, p + at + count);
    if (!array_at(p, n, 0x10, kImageSize, &at, &count)) return fail("images don't fit");
    m.images.resize(count);
    for (uint32_t i = 0; i < count; ++i) {
        const size_t im = at + size_t{i} * kImageSize;
        const size_t tx = im + 8;
        MaterialImage& image = m.images[i];
        image.data_offset = rd32(p + im);
        StreamedTexture& t = image.texture;
        std::memcpy(&t.width, p + tx, 2);
        std::memcpy(&t.height, p + tx + 2, 2);
        t.levels = p[tx + 4];
        t.format = p[tx + 5];
        t.content = read_reference(p + tx + kTexContent);
        t.tail = read_reference(p + tx + kTexTail);
        size_t ra = 0;
        uint32_t rc = 0;
        if (!array_at(p, n, tx + kTexRanges, 8, &ra, &rc)) return fail("mip ranges don't fit");
        t.ranges.resize(rc);
        for (uint32_t k = 0; k < rc; ++k) t.ranges[k] = {rd32(p + ra + size_t{k} * 8), rd32(p + ra + size_t{k} * 8 + 4)};
        size_t sa = 0;
        uint32_t sc = 0;
        if (!array_at(p, n, tx + kTexName, 1, &sa, &sc)) return fail("a texture's name doesn't fit");
        if (sc) t.debug_name.assign(reinterpret_cast<const char*>(p + sa), sc);
    }
    if (!array_at(p, n, 0x18, kFeedbackSize, &at, &count)) return fail("feedbacks don't fit");
    m.feedbacks.resize(count);
    for (uint32_t i = 0; i < count; ++i) {
        const size_t f = at + size_t{i} * kFeedbackSize;
        m.feedbacks[i].data_offset = rd32(p + f);
        size_t ia = 0;
        uint32_t ic = 0;
        if (!array_at(p, n, f + 4, 4, &ia, &ic)) return fail("a feedback's list doesn't fit");
        m.feedbacks[i].images.resize(ic);
        if (ic) std::memcpy(m.feedbacks[i].images.data(), p + ia, size_t{ic} * 4);
    }
    size_t sa = 0;
    uint32_t sc = 0;
    if (!array_at(p, n, 0x20, 1, &sa, &sc)) return fail("the name doesn't fit");
    if (sc) m.debug_name.assign(reinterpret_cast<const char*>(p + sa), sc);
    *out = std::move(m);
    return true;
}

// ---- a MaterialDef to the game's flags ---------------------------------------------------------------------------------------------------
void material_flags(const MaterialDef& d, uint8_t* render, uint32_t* block) {
    // keen::RenderMaterialFlag. Foliage: on nearly all of the game's materials, weapons and armour included, and nothing reads it for pipelines.
    uint8_t r = kMatFoliage;
    uint32_t b = 0;
    const bool cut = d.alpha_test || d.hashed;
    if (cut) {
        r |= kMatAlphaTested;
        b |= kPbrSampleOpacity;
    }
    if (d.hashed) {
        r |= kMatHashedAlpha;
        b |= kPbrHashedAlpha;
    }
    if (d.double_sided) {
        r |= kMatDoubleSided;
        b |= kPbrFlipBackfaceNormals;
    }
    if (!d.shadows) r |= kMatDisableShadows;
    if (d.dissolve) {
        r |= kMatCanDissolve;
        b |= kPbrDissolve;
    }
    if (d.translucent > 0) {
        r |= kMatTranslucent;
        b |= kPbrFoliageTranslucency;
    }
    if (!d.occlusion.empty() || !d.params.empty()) b |= kPbrSampleAo;
    if (d.contact_shadows) b |= kPbrContactShadows;
    if (!d.frost) b |= kPbrDisableFrost;
    if (!d.snow) b |= kPbrDisableSnow;
    if (!d.wetness) b |= kPbrDisableWetness;
    *render = r;
    *block = b;
}

std::vector<std::string> material_pipelines(uint8_t render) {
    const std::string variant = std::string(render & kMatHashedAlpha ? "_HashedAlpha" : render & kMatAlphaTested ? "_Discard" : "") +
                                (render & kMatDoubleSided ? "_DoubleSided" : "");
    const std::string sided = render & kMatDoubleSided ? "_DoubleSided" : "";
    std::vector<std::string> out = {"Model_Visibility" + variant + "_GpuCulling2_Instanced", "Model_GBuffer" + sided + "_GpuCulling2_Instanced"};
    if (!(render & kMatDisableShadows)) out.push_back("Model_Shadow" + variant + "_GpuCulling2_Instanced");
    return out;
}

// ---- the parameter block -------------------------------------------------------------------------------------------------------------------
namespace {

void set_f(Bytes& b, size_t at, float v) { std::memcpy(&b[at], &v, 4); }
void set_u(Bytes& b, size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }

// What every one of the game's single-layer blocks holds where a block says nothing. Slots for textures stay 0: the loader writes handles there.
Bytes pbr_block(const MaterialDef& d, uint32_t flags) {
    Bytes b(kPbrBlockSize, 0);
    set_f(b, kPbrTexCoordScaling, d.uv_scale[0]);
    set_f(b, kPbrTexCoordScaling + 4, d.uv_scale[1]);
    set_f(b, kPbrConstantReflectance, d.reflectance_from_params ? -1.0f : d.reflectance);
    set_f(b, kPbrHeightBlendingRange, 1.0f);
    set_u(b, kPbrEmissiveSwizzle, kSwizzleNone);
    set_u(b, kPbrAlbedoSwizzle, d.vertex_color ? 0u : kSwizzleNone);
    set_u(b, kPbrAoSwizzle, kSwizzleNone);
    set_f(b, kPbrTranslucencyStrength, d.translucent);
    set_f(b, kPbrEmissiveShaderData, d.has_emissive ? d.emissive_strength : 1.0f);
    set_f(b, kPbrEmissiveShaderData + 4, d.emissive_exposure);
    if (d.dissolve) {
        for (int c = 0; c < 3; ++c) set_f(b, kPbrDissolveColor + c * 4, d.dissolve_color[c]);
        set_f(b, kPbrDissolveWidthInv, 1.0f / d.dissolve_width);
        set_f(b, kPbrDissolveParam, 1.0f);
        set_u(b, kPbrDissolveNoiseMap, kDissolveNoiseMap);
        set_f(b, kPbrDissolveNoiseTiling, d.dissolve_tiling);
        set_f(b, kPbrDissolveFadeRange, d.dissolve_fade);
    }
    set_u(b, kPbrFlags, flags);
    return b;
}

uint32_t first_guid_field(const Guid& g);
}  // namespace

Guid material_guid(const MaterialDef& d, const std::string& mod) { return d.replaces ? *d.replaces : block_id(mod, "material", d.id_name); }

namespace {
uint32_t first_guid_field(const Guid& g) {
    uint32_t v;
    std::memcpy(&v, g.b, 4);
    return v;
}

uint8_t unit_byte(float v) { return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255.0f)); }

}  // namespace

bool material_maps(const MaterialDef& d, const ImageLoader& load, const TextureCache& cache, std::vector<MaterialMap>* out, std::string* field, std::string* why) {
    auto read = [&](const std::string& rel, const char* name, RawImage* img) {
        if (load(rel, img, why)) return true;
        *field = name;
        *why = rel + ": " + *why;
        return false;
    };
    // Emissive first: the game's order. A shader without one skips it.
    if (d.has_emissive) {
        RawImage img;
        if (!d.emissive_map.empty()) {
            if (!read(d.emissive_map, d.kind == "emissive" && d.emissive_map == d.albedo ? "albedo" : "emissive", &img)) return false;
            multiply_srgb(&img, d.emissive_color);
        } else {
            img = solid_image(linear_to_srgb_byte(d.emissive_color[0]), linear_to_srgb_byte(d.emissive_color[1]), linear_to_srgb_byte(d.emissive_color[2]), 255);
        }
        out->push_back({kPbrEmissiveMap, "emissive", encode_chain(img, kFormatBc7Srgb, cache)});
    }
    {
        RawImage img;
        if (!d.albedo.empty()) {
            if (!read(d.albedo, "albedo", &img)) return false;
            multiply_srgb(&img, d.tint);
            // The game cuts at alpha 0.5, fixed in its shaders. Another cutoff scales alpha so that it falls at 0.5.
            if ((d.alpha_test || d.hashed) && d.alpha_cutoff != 0.5f)
                for (size_t i = 3; i < img.px.size(); i += 4) img.px[i] = unit_byte(static_cast<float>(img.px[i]) / 255.0f * 0.5f / d.alpha_cutoff);
        } else {
            img = solid_image(linear_to_srgb_byte(d.tint[0]), linear_to_srgb_byte(d.tint[1]), linear_to_srgb_byte(d.tint[2]), 255);
        }
        out->push_back({kPbrAlbedoMap, "albedo", encode_chain(img, kFormatBc7Srgb, cache)});
    }
    {
        RawImage img;
        if (!d.normal.empty()) {
            if (!read(d.normal, "normal", &img)) return false;
            for (size_t i = 0; i < img.px.size(); i += 4) {
                const float x = (static_cast<float>(img.px[i]) / 255.0f - 0.5f) * d.normal_strength + 0.5f;
                float y = (static_cast<float>(img.px[i + 1]) / 255.0f - 0.5f) * d.normal_strength + 0.5f;
                if (d.normal_y_down) y = 1.0f - y;
                img.px[i] = unit_byte(x);
                img.px[i + 1] = unit_byte(y);
                img.px[i + 2] = 0;
                img.px[i + 3] = 255;
            }
        } else {
            img = solid_image(128, 128, 0, 255);
        }
        out->push_back({kPbrNormalMap, "normal", encode_chain(img, kFormatBc5Unorm, cache)});
    }
    {
        // The game's channels: R metallic, G roughness, B occlusion, A reflectance. A number is the value without a map and a factor with one, as in glTF.
        RawImage img;
        const float metal = d.metallic, rough = d.roughness;
        if (!d.params.empty()) {
            if (!read(d.params, "params", &img)) return false;
            const float fm = d.has_metallic ? metal : 1.0f, fr = d.has_roughness ? rough : 1.0f;
            for (size_t i = 0; i < img.px.size(); i += 4) {
                img.px[i] = unit_byte(static_cast<float>(img.px[i]) / 255.0f * fm);
                img.px[i + 1] = unit_byte(static_cast<float>(img.px[i + 1]) / 255.0f * fr);
            }
        } else if (!d.metallic_roughness.empty() || !d.occlusion.empty()) {
            RawImage mr, ao;
            if (!d.metallic_roughness.empty() && !read(d.metallic_roughness, "metallicRoughness", &mr)) return false;
            if (!d.occlusion.empty() && !read(d.occlusion, "occlusion", &ao)) return false;
            const uint32_t w = std::max(mr.w, ao.w), h = std::max(mr.h, ao.h);
            if (!mr.px.empty() && (mr.w != w || mr.h != h)) mr = resize_image(mr, w, h);
            if (!ao.px.empty() && (ao.w != w || ao.h != h)) ao = resize_image(ao, w, h);
            img.w = w;
            img.h = h;
            img.px.resize(size_t{w} * h * 4);
            const float fm = mr.px.empty() || d.has_metallic ? metal : 1.0f, fr = mr.px.empty() || d.has_roughness ? rough : 1.0f;
            for (size_t i = 0; i < img.px.size(); i += 4) {
                img.px[i] = unit_byte((mr.px.empty() ? 1.0f : static_cast<float>(mr.px[i + 2]) / 255.0f) * fm);
                img.px[i + 1] = unit_byte((mr.px.empty() ? 1.0f : static_cast<float>(mr.px[i + 1]) / 255.0f) * fr);
                img.px[i + 2] = ao.px.empty() ? 255 : ao.px[i];
                img.px[i + 3] = 255;
            }
        } else {
            img = solid_image(unit_byte(metal), unit_byte(rough), 255, 255);
        }
        // BC1 as the game's, or BC7 where alpha holds reflectance.
        out->push_back({kPbrParamsMap, "material", encode_chain(img, d.reflectance_from_params ? kFormatBc7Unorm : kFormatBc1Unorm, cache)});
    }
    return true;
}

bool build_material(const MaterialDef& d, const std::string& mod, const std::vector<MaterialMap>& maps, BuiltMaterial* out, std::string* why) {
    uint8_t render = 0;
    uint32_t block = 0;
    material_flags(d, &render, &block);
    RenderMaterial m;
    m.effect = kEffectPbr;
    m.flags = render;
    m.signature = kPbrSignature;
    m.data = pbr_block(d, block);
    m.debug_name = "kiln_" + mod + "_" + d.name;
    const Guid guid = material_guid(d, mod);
    const uint32_t bundle = first_guid_field(guid);
    // In the order the game's own materials list them: the emissive map first, then albedo, normal, params.
    std::vector<uint32_t> feedback;
    uint64_t sort = 0;
    for (const MaterialMap& map : maps) {
        MaterialImage image;
        image.data_offset = map.slot;
        StreamedTexture& t = image.texture;
        t.width = static_cast<uint16_t>(map.chain.width);
        t.height = static_cast<uint16_t>(map.chain.height);
        t.levels = static_cast<uint8_t>(map.chain.levels);
        t.format = map.chain.format;
        t.content.hash = content_hash(map.chain.data.data(), map.chain.data.size());
        t.content.category = kCategoryMaterialChain;
        t.content.bundle = bundle;
        t.content.sort = sort;
        for (const StreamRange& r : map.chain.ranges) t.ranges.push_back(r);
        // Levels a range doesn't start at are the mip tail, kept resident: a second blob, the end of the chain. With no ranges it is the chain.
        t.tail.hash = content_hash(map.chain.data.data() + map.chain.tail_start, map.chain.data.size() - map.chain.tail_start);
        t.tail.category = kCategoryMipTail;
        t.tail.bundle = 0;
        t.tail.sort = t.tail.hash.h2;
        t.debug_name = "Material_" + mod + "_" + d.name + "_" + map.what + "_map_0";
        feedback.push_back(static_cast<uint32_t>(m.images.size()));
        m.images.push_back(std::move(image));
        ++sort;
    }
    if (feedback.size() > kMaxFeedbackImages) {
        *why = "uses " + std::to_string(feedback.size()) + " textures: the game's limit for one surface is " + std::to_string(kMaxFeedbackImages);
        return false;
    }
    m.feedbacks.push_back({kPbrLayerFeedback, std::move(feedback)});
    out->guid = guid;
    out->resource = write_material(m);
    out->render_flags = render;
    out->block_flags = block;
    return true;
}

}  // namespace kvb
