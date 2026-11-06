// SPDX-License-Identifier: MIT
// A surface definition -> one slot of the three tables, with its maps cooked as the game's own building surfaces are: BC7 sRGB albedo, BC5 normal,
// BC4 roughness, metallic, reflectance, height and AO, BC1 sRGB emissive, each a full mip chain under its real ContentHash with the game's
// streaming layout. A layer can start from a game material's layer, whose textures the game already serves. docs/re/building.md 1.4.
#include <algorithm>
#include <cmath>
#include <cstring>
#include <set>

#include "bc7enc/bc7decomp.h"
#include "bc7enc/rgbcx.h"
#include "surfacecook.h"

namespace ksf {
namespace {

constexpr uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<unsigned char>(*s)) * 0x01000193u;
    return h;
}

// The game sorts a slot's textures by source layer: edgeTop is drawn from the edge's maps, so it shares the edge's number.
constexpr uint32_t kSortLayer[kPartCount] = {0, 1, 2, 3, 3, 4};
// What a map is when a layer has no file and no value for it: the game's own constant maps are 16 x 16 of one value like these.
constexpr uint8_t kRoughnessDefault = 204;  // 0.8
constexpr uint8_t kReflectanceDefault = 128;
constexpr float kTilingPerFourMetres = 0.25f;  // the game's tilingFactor for one copy of a texture every 4 m
constexpr float kDefaultEmissiveExposure = 0.75f, kDefaultEmissiveGi = 4.0f;
constexpr float kDefaultDisplacementScale = 0.1f, kDefaultDisplacementOffset = -0.05f;

uint8_t unit_byte(float v) { return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255.0f)); }
float srgb_to_linear(uint8_t v) {
    const float c = static_cast<float>(v) / 255.0f;
    return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f);
}
std::string hash_key(const ContentHash& h) { return kvb::content_hash_text(h); }

// A cooked map and where it came from, so maps two layers share are cooked once.
struct Cooked {
    kvb::EncodedChain chain;
    ContentHash content, tail;
};

struct Built {
    SlotLayer layer;
    int origin_slot = -1, origin_part = -1;  // a game layer taken unchanged: where from
};

class Writer {
public:
    Writer(const BlendingTable& b, const GiTable& g, const ParamsTable& p, const WriteOptions& opt, WriteResult* out) : b_(b), g_(g), p_(p), opt_(opt), out_(out) {
        for (const auto& slot : b.block.slots)
            for (const Patch& patch : slot)
                if (patch.type == kPatchTexture) game_hashes_.insert(hash_key(patch.texture.tex.content.hash));
    }

    // A game material's slot, by name. nullptr: *why says why not.
    const Slot* game_slot(const std::string& name, std::string* why) {
        const int id = opt_.game_material ? opt_.game_material(name) : -1;
        if (id < 0) {
            *why = "the game has no material \"" + name + "\"";
            return nullptr;
        }
        if (id < 128) {
            *why = "\"" + name + "\" is a terrain material: its look isn't a building surface";
            return nullptr;
        }
        const uint32_t index = static_cast<uint32_t>(id - 128);
        if (index >= b_.layer_count) {
            *why = "\"" + name + "\" has no surface of the game's own";
            return nullptr;
        }
        auto it = game_slots_.find(index);
        if (it == game_slots_.end()) {
            Slot s;
            if (!slot_of(b_, g_, p_, index, &s, why)) return nullptr;
            it = game_slots_.emplace(index, std::move(s)).first;
        }
        return &it->second;
    }
    int game_index(const std::string& name) {
        const int id = opt_.game_material ? opt_.game_material(name) : -1;
        return id - 128;
    }

    // One job's slot. false: errors say why, nothing of it is kept.
    bool build(const SurfaceJob& job, const SurfaceDef& def, Slot* out, std::vector<std::string>* errors) {
        job_ = &job;
        errors_ = errors;
        cooked_.clear();
        job_new_.clear();
        job_blobs_.clear();
        const size_t before = errors->size();

        const Slot* base = nullptr;
        int base_index = -1;
        std::string why;
        if (job.tweak) {
            if (job.slot >= b_.layer_count) {
                fail("material", "has no surface of the game's own to change");
                return false;
            }
            base_index = static_cast<int>(job.slot);
            base = game_slot("Cell" + std::to_string(job.slot + 128), &why);
            if (!base) {
                // resolve by cell id even without a name resolver
                Slot s;
                if (!slot_of(b_, g_, p_, job.slot, &s, &why)) {
                    fail("material", why);
                    return false;
                }
                base = &game_slots_.emplace(job.slot, std::move(s)).first->second;
            }
        } else if (def.layer[kSide].kind == LayerDef::Own && !def.layer[kSide].game.empty()) {
            base = game_slot(def.layer[kSide].game, &why);
            if (!base) {
                fail("side.game", why);
                return false;
            }
            base_index = game_index(def.layer[kSide].game);
        }

        Slot s = base ? *base : Slot{};
        Built built[kPartCount];
        for (int p = 0; p < kPartCount; ++p) {
            if (!layer(def, p, base, base_index, &built[p])) continue;
            s.layer[p] = built[p].layer;
        }
        if (errors->size() != before) return false;

        // The slot's own numbers: the base's, else the most common of the game's (Slot's defaults), and what the block says wins.
        if (def.bevel) s.bevel = *def.bevel;
        if (def.edge_curve) s.edge_curve = *def.edge_curve;
        if (def.noise_min && def.noise_max) {
            s.noise_scale = 1.0f / (*def.noise_max - *def.noise_min);
            s.noise_bias = -*def.noise_min / (*def.noise_max - *def.noise_min);
        }
        if (def.edge_only_side) s.edge_only_side = *def.edge_only_side ? 1 : 0;
        if (!job.tweak) s.is_roof = job.roof ? 1 : 0;

        // Parameters: mapping and edge decoration.
        MaterialParams params = base ? base->params : MaterialParams{};
        if (!job.tweak) {
            if (job.roof) params.mapping = 2;
            else if (def.mapping) params.mapping = static_cast<uint8_t>(*def.mapping);
            else if (params.mapping == 2) params.mapping = 0;
        } else if (def.mapping && params.mapping != 2) {
            params.mapping = static_cast<uint8_t>(*def.mapping);
        }
        if (def.decoration) {
            params.rules.clear();
            if (!def.decoration->empty()) {
                const Slot* deco = game_slot(*def.decoration, &why);
                if (!deco) fail("decoration", why);
                else params.rules = deco->params.rules;
            }
        } else if (!base) {
            params.rules.clear();
        }
        params.edge_only_side = static_cast<uint8_t>(s.edge_only_side);
        s.params = std::move(params);

        // GI: an unchanged game face is the game's; any other is averaged from the layer's smallest mips.
        for (int f = 0; f < 3; ++f) {
            const int p = kGiFacePart[f];
            const Built& bl = built[p];
            if (bl.origin_slot >= 0 && bl.origin_part == p) {
                std::string w;
                const Slot* origin = game_slot("Cell" + std::to_string(bl.origin_slot + 128), &w);
                Slot direct;
                if (!origin && slot_of(b_, g_, p_, static_cast<uint32_t>(bl.origin_slot), &direct, &w)) origin = &direct;
                if (origin) {
                    s.gi[f] = origin->gi[f];
                    continue;
                }
            }
            s.gi[f] = average(s.layer[p]);
        }
        if (errors->size() != before) return false;

        // The game's limit on streamed textures, counted by content as the game counts them.
        size_t fresh = 0;
        for (const std::string& h : job_new_)
            if (!all_new_.count(h)) ++fresh;
        if (out_->new_streamed + fresh > opt_.streamed_room) {
            char b[200];
            std::snprintf(b, sizeof b, "refused: its %zu new textures don't fit: the game has room for %u more streamed textures, and %u are taken by other surfaces",
                          fresh, opt_.streamed_room, out_->new_streamed);
            errors->push_back(job.label + ", surface: " + b);
            return false;
        }
        for (const std::string& h : job_new_) all_new_.insert(h);
        out_->new_streamed += static_cast<uint32_t>(fresh);
        for (ServedBlob& blob : job_blobs_) {
            if (served_.insert(hash_key(blob.hash)).second) out_->blobs.push_back(std::move(blob));
        }
        report_new_ = static_cast<uint32_t>(job_new_.size());
        *out = std::move(s);
        return true;
    }
    uint32_t last_new_textures() const { return report_new_; }

    // The block item's icon, from the slot's side and top albedo. false: a map can't be read here (a game texture without a blob reader).
    bool icon(const SurfaceJob& job, const Slot& s) {
        kvb::RawImage side, top;
        if (!s.layer[kSide].map[kAlbedo] || !s.layer[kTop].map[kAlbedo] || !thumb(*s.layer[kSide].map[kAlbedo], &side) || !thumb(*s.layer[kTop].map[kAlbedo], &top)) return false;
        const kvb::RawImage img = block_icon(side, top);
        out_->icons.push_back({job.mod, job.block, job.item, png_rgba(img.w, img.h, img.px)});
        return true;
    }

private:
    void fail(const std::string& field, const std::string& why) { errors_->push_back(job_->label + ", surface." + field + ": " + why); }

    // Which part's definition a part ends up with. A tweak's unwritten layer is the game's own: -1 - part.
    int final_part(const SurfaceDef& def, int p) {
        if (!job_->tweak) return resolve_part(def, p);
        for (int steps = 0; steps <= kPartCount; ++steps) {
            const LayerDef& l = def.layer[p];
            if (l.kind == LayerDef::Own) return p;
            if (l.kind == LayerDef::Unset) return -1 - p;
            p = l.same_as;
        }
        return -1 - p;
    }

    bool layer(const SurfaceDef& def, int p, const Slot* base, int base_index, Built* out) {
        const int r = final_part(def, p);
        if (r < 0) {
            // a tweak's layer the block doesn't write: the game's own
            const int q = -1 - r;
            out->layer = base->layer[q];
            out->origin_slot = base_index;
            out->origin_part = q;
            return true;
        }
        const LayerDef& L = def.layer[r];
        const std::string field = kPartJson[r];
        SlotLayer start;
        bool from_game = false;
        if (!L.game.empty()) {
            std::string why;
            const Slot* gs = game_slot(L.game, &why);
            if (!gs) {
                fail(field + ".game", why);
                return false;
            }
            const int part = L.game_part >= 0 ? L.game_part : p;
            start = gs->layer[part];
            if (!start.map[kAlbedo]) {
                fail(field + ".layer", "the game's \"" + L.game + "\" has no " + kPartJson[part] + " layer");
                return false;
            }
            out->origin_slot = game_index(L.game);
            out->origin_part = part;
            from_game = true;
        } else if (job_->tweak) {
            start = base->layer[r];
            out->origin_slot = base_index;
            out->origin_part = r;
            from_game = true;
        }
        SlotLayer l = start;
        bool changed = false;
        for (int m = 0; m < kMapCount; ++m) {
            const MapDef& md = L.map[m];
            if (!md.set) continue;
            const Cooked* c = cook(r, m, md, field + "." + kMapJson[m]);
            if (!c) return false;
            l.map[m] = param(*c, p, m);
            changed = true;
        }
        // Maps nobody gave: the game's constants, as values.
        for (int m = kAlbedo; m < kMapCount; ++m) {
            if (l.map[m]) continue;
            MapDef md;
            md.set = md.has_value = true;
            md.value = m == kMetallic ? 0.0f : m == kRoughness ? kRoughnessDefault / 255.0f : m == kReflectance ? kReflectanceDefault / 255.0f : m == kHeight ? 0.5f : 1.0f;
            const Cooked* c = cook(r, m, md, field + "." + kMapJson[m]);
            if (!c) return false;
            l.map[m] = param(*c, p, m);
            changed = true;
        }
        if (!from_game) l.tiling = kTilingPerFourMetres;
        if (L.tiling) {
            l.tiling = kTilingPerFourMetres * *L.tiling;
            changed = true;
        }
        const bool own_height = L.map[kHeight].set && !L.map[kHeight].file.empty();
        if (!from_game) {
            l.displacement_scale = own_height ? kDefaultDisplacementScale : 0.0f;
            l.displacement_offset = own_height ? kDefaultDisplacementOffset : 0.0f;
        }
        if (L.displacement_scale) l.displacement_scale = *L.displacement_scale, changed = true;
        if (L.displacement_offset) l.displacement_offset = *L.displacement_offset, changed = true;
        if (L.map[kEmissive].set) {
            const MapDef& e = L.map[kEmissive];
            l.emissive_intensity = e.strength;
            l.emissive_exposure = e.exposure.value_or(kDefaultEmissiveExposure);
            l.emissive_gi = e.gi.value_or(kDefaultEmissiveGi);
        } else if (!l.map[kEmissive]) {
            l.emissive_intensity = l.emissive_exposure = l.emissive_gi = 0;
        }
        l.feedback = true;
        if (changed) out->origin_slot = out->origin_part = -1;
        // A game layer taken as it is keeps the game's textures, sort keys included, so it is the game's own layer to the byte.
        out->layer = l;
        return true;
    }

    // The pixels of one map, cooked. Cached per (part, map) so layers that repeat a part cook it once.
    const Cooked* cook(int r, int m, const MapDef& md, const std::string& field) {
        const int key = r * kMapCount + m;
        if (auto it = cooked_.find(key); it != cooked_.end()) return &it->second;
        kvb::RawImage img;
        std::string why;
        if (!md.file.empty()) {
            if (!opt_.load || !opt_.load(job_->dir, md.file, &img, &why)) {
                fail(field, md.file + ": " + (why.empty() ? "can't be read" : why));
                return nullptr;
            }
        }
        switch (m) {
            case kAlbedo:
            case kEmissive:
                if (md.file.empty()) {
                    img = kvb::solid_image(kvb::linear_to_srgb_byte(md.color[0]), kvb::linear_to_srgb_byte(md.color[1]), kvb::linear_to_srgb_byte(md.color[2]), 255);
                } else if (md.has_color) {
                    kvb::multiply_srgb(&img, md.color);
                }
                for (size_t i = 3; i < img.px.size(); i += 4) img.px[i] = 255;
                break;
            case kNormal:
                if (md.file.empty()) {
                    img = kvb::solid_image(128, 128, 0, 255);
                } else {
                    for (size_t i = 0; i < img.px.size(); i += 4) {
                        const float x = (static_cast<float>(img.px[i]) / 255.0f - 0.5f) * md.strength + 0.5f;
                        float y = (static_cast<float>(img.px[i + 1]) / 255.0f - 0.5f) * md.strength + 0.5f;
                        if (md.y_down) y = 1.0f - y;
                        img.px[i] = unit_byte(x);
                        img.px[i + 1] = unit_byte(y);
                        img.px[i + 2] = 0;
                        img.px[i + 3] = 255;
                    }
                }
                break;
            default:  // one channel: the PNG's red, or the value
                if (md.file.empty()) img = kvb::solid_image(unit_byte(md.value), 0, 0, 255);
                break;
        }
        Cooked c;
        c.chain = kvb::encode_chain(img, kMapFormat[m], opt_.cache);
        c.content = kvb::content_hash(c.chain.data.data(), c.chain.data.size());
        c.tail = kvb::content_hash(c.chain.data.data() + c.chain.tail_start, c.chain.data.size() - c.chain.tail_start);
        if (c.chain.ranges.size() > kvb::kMaxMipRanges) {
            fail(field, "has more mip levels than the game streams");
            return nullptr;
        }
        const std::string k = hash_key(c.content);
        if (!game_hashes_.count(k)) job_new_.insert(k);
        const std::string what = job_->mod + ":" + job_->block + " " + kPartJson[r] + " " + kMapJson[m];
        job_blobs_.push_back({c.content, c.chain.data, what});
        job_blobs_.push_back({c.tail, Bytes(c.chain.data.begin() + static_cast<std::ptrdiff_t>(c.chain.tail_start), c.chain.data.end()), what + " mip tail"});
        last_blocks_[k] = Bytes(c.chain.data.end() - kvb::format_block_bytes(kMapFormat[m]), c.chain.data.end());
        if (m == kAlbedo) thumbs_[k] = std::max(img.w, img.h) > 128 ? kvb::resize_image(img, std::max(1u, img.w * 128 / std::max(img.w, img.h)), std::max(1u, img.h * 128 / std::max(img.w, img.h))) : img;
        return &cooked_.emplace(key, std::move(c)).first->second;
    }

    TextureParam param(const Cooked& c, int p, int m) const {
        TextureParam tp;
        kvb::StreamedTexture& t = tp.tex;
        t.width = static_cast<uint16_t>(c.chain.width);
        t.height = static_cast<uint16_t>(c.chain.height);
        t.levels = static_cast<uint8_t>(c.chain.levels);
        t.format = c.chain.format;
        uint32_t bundle;
        std::memcpy(&bundle, job_->guid.b, 4);
        t.content = {c.content, kCategorySurfaceChain, bundle, (uint64_t{kSortLayer[p]} << 8) | static_cast<uint64_t>(m)};
        t.tail = {c.tail, kvb::kCategoryMipTail, 0, c.tail.h2};
        t.ranges = c.chain.ranges;
        t.debug_name = "Building_kiln_" + job_->mod + "_" + job_->block + "_" + kPartJson[p] + "_" + kMapJson[m];
        tp.category = kStreamBuilding;
        return tp;
    }

    // A small picture of an albedo: ours from its pixels, the game's from a mip of its blob.
    bool thumb(const TextureParam& tp, kvb::RawImage* out) {
        const std::string k = hash_key(tp.tex.content.hash);
        if (auto it = thumbs_.find(k); it != thumbs_.end()) {
            *out = it->second;
            return true;
        }
        Bytes chain;
        bool ok = opt_.read_blob && opt_.read_blob(tp.tex.content, &chain) && decode_level(chain, tp.tex.width, tp.tex.height, tp.tex.format, 128, out);
        if (!ok && opt_.read_blob && opt_.read_blob(tp.tex.tail, &chain)) {
            // the mip tail alone: its first level is the largest it holds
            const ChainLayout c = chain_layout(tp.tex.width, tp.tex.height, tp.tex.format);
            uint32_t w = tp.tex.width, h = tp.tex.height;
            for (uint64_t at = 0; at < c.tail_start; w = std::max(1u, w / 2), h = std::max(1u, h / 2)) at += uint64_t{(w + 3) / 4} * ((h + 3) / 4) * 16;
            ok = decode_level(chain, w, h, tp.tex.format, 128, out);
        }
        if (!ok) return false;
        thumbs_[k] = *out;
        return true;
    }

    // The colour of a texture's last 1 x 1 level: ours from the chain, the game's from its mip tail. Empty: not readable.
    bool last_pixel(const TextureParam& tp, uint8_t rgba[4]) {
        const std::string k = hash_key(tp.tex.content.hash);
        Bytes block;
        const uint32_t bs = kvb::format_block_bytes(tp.tex.format);
        if (auto it = last_blocks_.find(k); it != last_blocks_.end()) block = it->second;
        else {
            Bytes tail;
            if (!opt_.read_blob || !opt_.read_blob(tp.tex.tail, &tail) || tail.size() < bs) return false;
            block.assign(tail.end() - bs, tail.end());
            last_blocks_[k] = block;
        }
        uint8_t px[16 * 4] = {};
        switch (tp.tex.format) {
            case kvb::kFormatBc7Srgb:
            case kvb::kFormatBc7Unorm: {
                bc7decomp::color_rgba c[16];
                if (!bc7decomp::unpack_bc7(block.data(), c)) return false;
                std::memcpy(rgba, &c[0], 4);
                return true;
            }
            case kvb::kFormatBc1Srgb:
            case kvb::kFormatBc1Unorm:
                rgbcx::unpack_bc1(block.data(), px, true);
                std::memcpy(rgba, px, 4);
                return true;
            case kvb::kFormatBc4Unorm:
                rgbcx::unpack_bc4(block.data(), px, 1);
                rgba[0] = rgba[1] = rgba[2] = px[0];
                rgba[3] = 255;
                return true;
            default:
                return false;
        }
    }

    GiFace average(const SlotLayer& l) {
        GiFace f;
        uint8_t c[4];
        if (l.map[kAlbedo] && last_pixel(*l.map[kAlbedo], c)) {
            for (int i = 0; i < 3; ++i) f.values[i] = srgb_to_linear(c[i]);
        } else {
            f.values[0] = f.values[1] = f.values[2] = 0.18f;
        }
        f.values[3] = l.map[kRoughness] && last_pixel(*l.map[kRoughness], c) ? c[0] / 255.0f : kRoughnessDefault / 255.0f;
        if (l.map[kEmissive] && last_pixel(*l.map[kEmissive], c))
            for (int i = 0; i < 3; ++i) f.values[4 + i] = srgb_to_linear(c[i]);
        f.values[7] = l.emissive_gi;
        f.values[8] = l.emissive_exposure;
        f.values[9] = l.emissive_intensity;
        f.values[10] = l.map[kMetallic] && last_pixel(*l.map[kMetallic], c) ? c[0] / 255.0f : 0.0f;
        f.values[11] = l.map[kReflectance] && last_pixel(*l.map[kReflectance], c) && c[0] != kReflectanceDefault ? c[0] / 255.0f : kvb::kDefaultReflectance;
        f.values[12] = l.tiling;
        if (l.map[kEmissive]) {
            f.emissive = *l.map[kEmissive];
            f.emissive->category = kStreamGi;
        }
        if (l.map[kAlbedo]) {
            f.albedo = *l.map[kAlbedo];
            f.albedo->category = kStreamGi;
        }
        return f;
    }

    const BlendingTable& b_;
    const GiTable& g_;
    const ParamsTable& p_;
    const WriteOptions& opt_;
    WriteResult* out_;
    std::set<std::string> game_hashes_, all_new_, served_;
    std::map<uint32_t, Slot> game_slots_;
    std::map<std::string, Bytes> last_blocks_;
    std::map<std::string, kvb::RawImage> thumbs_;
    // per job
    const SurfaceJob* job_ = nullptr;
    std::vector<std::string>* errors_ = nullptr;
    std::map<int, Cooked> cooked_;
    std::set<std::string> job_new_;
    std::vector<ServedBlob> job_blobs_;
    uint32_t report_new_ = 0;
};

uint32_t texture_count(const Slot& s) {
    uint32_t n = 0;
    for (const SlotLayer& l : s.layer)
        for (const auto& m : l.map) n += m ? 1 : 0;
    return n;
}

}  // namespace

bool write_surfaces(const BlendingTable& b, const GiTable& g, const ParamsTable& p, const std::vector<SurfaceJob>& jobs, const WriteOptions& opt, WriteResult* out) {
    out->blend = b;
    out->gi = g;
    out->params = p;
    if (b.block.slots.size() < kMaxSlots || g.block.slots.size() < kMaxSlots || b.layer_count > kMaxSlots || g.material_count != b.layer_count ||
        p.materials.size() != b.layer_count) {
        out->errors.push_back("the game's surface tables don't have the room or the counts they had when this was written: surfaces are off");
        return false;
    }
    if (b.block.signature != kBlendSignature || g.block.signature != kGiSignature) {
        out->errors.push_back("the game's surface tables have another shader layout: surfaces are off until Kiln is updated");
        return false;
    }
    Writer w(b, g, p, opt, out);
    const auto default_def = [] {
        SurfaceDef d;
        std::vector<std::string> e;
        parse_surface(ojson::parse(std::string("{\"side\": {\"game\": \"") + kDefaultSurface + "\"}}"), "", &d, &e);
        return d;
    }();
    std::set<uint32_t> written;
    for (const SurfaceJob& job : jobs) {
        JobReport rep{job.mod, job.block, job.slot, "", 0, 0};
        if (job.slot >= kMaxSlots) {
            out->errors.push_back(job.label + ": slot " + std::to_string(job.slot) + " is past the game's tables");
            rep.status = "skipped";
            out->jobs.push_back(rep);
            continue;
        }
        SurfaceDef def;
        bool ok = true;
        if (job.surface) ok = parse_surface(*job.surface, job.label, &def, &out->errors, job.tweak);
        else if (job.tweak) ok = false;
        Slot s;
        if (ok && (job.surface || !job.tweak)) ok = w.build(job, job.surface ? def : default_def, &s, &out->errors);
        if (ok) {
            rep.status = job.surface ? "written" : "default";
            rep.new_textures = w.last_new_textures();
        } else if (!job.tweak) {
            // a new material has to have a slot: its count covers it. It draws like the default.
            std::vector<std::string> quiet;
            if (!w.build(job, default_def, &s, &quiet)) {
                Slot first;
                std::string why;
                if (!slot_of(b, g, p, 0, &first, &why)) {
                    out->errors.push_back(job.label + ": no surface to fall back on: " + why);
                    rep.status = "skipped";
                    out->jobs.push_back(rep);
                    continue;
                }
                s = first;
                s.is_roof = job.roof ? 1 : 0;
            }
            rep.status = "default";
        } else {
            rep.status = "skipped";
            out->jobs.push_back(rep);
            continue;
        }
        rep.textures = texture_count(s);
        if (job.icon) rep.icon = w.icon(job, s);
        put_slot(&out->blend, &out->gi, &out->params, job.slot, s);
        written.insert(job.slot);
        out->jobs.push_back(rep);
    }
    // No slot under the count may stay empty: zero handles would be read as textures.
    for (uint32_t i = b.layer_count; i < out->blend.layer_count; ++i) {
        if (written.count(i)) continue;
        SurfaceJob gap;
        gap.mod = "kiln";
        gap.block = "slot" + std::to_string(i);
        gap.label = "slot " + std::to_string(i);
        gap.slot = i;
        Slot s;
        std::vector<std::string> quiet;
        std::string why;
        if (!w.build(gap, default_def, &s, &quiet) && !slot_of(b, g, p, 0, &s, &why)) continue;
        put_slot(&out->blend, &out->gi, &out->params, i, s);
        out->warnings.push_back("slot " + std::to_string(i) + " had no material: it draws like " + kDefaultSurface);
    }
    return true;
}

const std::vector<kvb::LayoutExpect>& surface_layouts() {
    static const std::vector<kvb::LayoutExpect> k = [] {
        const std::vector<std::pair<const char*, uint32_t>> list = {
            {"keen::BuildingMaterialBlendingResource", 0x4793366fu},
            {"keen::RenderDataBlock", 0xd77ef542u},
            {"keen::RenderDataBlockPatchPoint", 0x18b8654eu},
            {"keen::RenderDataBlockTextureParameter", 0xb595cd1bu},
            {"keen::RenderDataBlockTextureFeedback", 0x57012310u},
            {"keen::RenderStreamedTexture", 0x37063ad9u},
            {"keen::ResourceContentReference", 0x86ac181eu},
            {"keen::BuildingMaterialBlendingMaterialLayerShaderData", 0xf0627487u},
            {"keen::BuildingMaterialBlendingMaterialLayerTextureShaderData", 0xb6177939u},
            {"keen::GiVoxelBuildingMaterialResource", 0x232e3159u},
            {"keen::BuildingMaterialParametersResource", 0x94ee9f2eu},
            {"keen::BuildingMaterialParameters", 0x6b4e870du},
            {"keen::DecoObjectRuleSet", 0x97a3d2f5u},
        };
        std::vector<kvb::LayoutExpect> v;
        for (const auto& [name, layout] : list) v.push_back({name, fnv1a(name), layout});
        return v;
    }();
    return k;
}

ojson report_json(const WriteResult& r) {
    ojson j = ojson::object();
    j["ok"] = r.errors.empty();
    j["layerCount"] = r.blend.layer_count;
    ojson jobs = ojson::array();
    for (const JobReport& x : r.jobs)
        jobs.push_back({{"mod", x.mod}, {"block", x.block}, {"slot", x.slot}, {"cellId", x.slot + 128}, {"status", x.status}, {"textures", x.textures}, {"newTextures", x.new_textures},
                        {"icon", x.icon}});
    j["surfaces"] = std::move(jobs);
    j["newStreamedTextures"] = r.new_streamed;
    j["blobs"] = r.blobs.size();
    j["errors"] = r.errors;
    j["warnings"] = r.warnings;
    return j;
}

}  // namespace ksf
