// SPDX-License-Identifier: MIT
// VfxDef -> the bytes of a keen::VfxResource, field by field as docs/re/vfx.md 3.6 lists them.
//
// The layout rule of a reflected resource (tools/kfc/kfc/blobwrite.py, checked by re-encoding the game's own): the root struct first;
// out-of-line data behind it in field order, depth first, each block aligned to its element type; an array reserves its whole element
// block, then the elements' own out-of-line data follows; offsets are self-relative; the total is not padded.
#include <algorithm>
#include <cmath>
#include <cstring>

#include "../shaders/kiln_vfx_params.h"
#include "vfxcook.h"

namespace kvb {
namespace {

class W {
public:
    explicit W(size_t root) : b(root, 0) {}
    Bytes b;
    size_t reserve(size_t size, size_t align) {
        const size_t at = (b.size() + align - 1) / align * align;
        b.resize(at + size, 0);
        return at;
    }
    void u8(size_t at, uint8_t v) { b[at] = v; }
    void u16(size_t at, uint16_t v) { std::memcpy(&b[at], &v, 2); }
    void u32(size_t at, uint32_t v) { std::memcpy(&b[at], &v, 4); }
    void u64(size_t at, uint64_t v) { std::memcpy(&b[at], &v, 8); }
    void f32(size_t at, float v) { std::memcpy(&b[at], &v, 4); }
    void guid(size_t at, const Guid& g) { std::memcpy(&b[at], g.b, 16); }
    void array(size_t field, size_t at, uint32_t count) {
        u32(field, static_cast<uint32_t>(static_cast<int32_t>(static_cast<int64_t>(at) - static_cast<int64_t>(field))));
        u32(field + 4, count);
    }
    void string(size_t field, const std::string& s) {
        if (s.empty()) return;
        const size_t at = reserve(s.size(), 1);
        std::memcpy(&b[at], s.data(), s.size());
        array(field, at, static_cast<uint32_t>(s.size()));
    }
    void content_ref(size_t at, const ContentHash& h, uint32_t category, uint32_t bundle, uint64_t sort) {
        u32(at, h.size);
        u32(at + 4, h.h0);
        u32(at + 8, h.h1);
        u32(at + 12, h.h2);
        u32(at + 0x10, category);
        u32(at + 0x14, bundle);
        u64(at + 0x18, sort);
    }
};

// VfxResource
constexpr size_t kVfxSize = 216, kParticleSize = 104;
constexpr uint32_t kCategoryPipeline = 2603049049u;  // strippedCode's category on all 14,352 vanilla entries
constexpr uint32_t kBundlePipeline = 1;              // and its bundleId

struct Member {
    const char* name;
    uint32_t components;
    uint8_t type;  // VfxBufferMemberComponentType: Float 0, UnsignedInt 2
    uint32_t offset;
};
const std::vector<Member> kEmitterState = {{"age", 1, 0, KE_S_AGE}, {"accum", 1, 0, KE_S_ACCUM}, {"pending", 1, 2, KE_S_PENDING}};
const std::vector<Member> kSpriteState = {{"age", 1, 0, KS_S_AGE},   {"lifetime", 1, 0, KS_S_LIFE}, {"position", 3, 0, KS_S_POS},
                                          {"velocity", 3, 0, KS_S_VEL}, {"roll", 1, 0, KS_S_ROLL},  {"spin", 1, 0, KS_S_SPIN}, {"aux", 1, 0, KS_S_AUX}};
const std::vector<Member> kLightState = {{"age", 1, 0, KL_S_AGE}, {"stopAge", 1, 0, KL_S_STOP_AGE}};
// The game's compact sprite module: its own state, and a block with fixed offsets (docs/re/vfx.md 3.7).
const std::vector<Member> kGameSpriteState = {{"age", 1, 0, 20}, {"alpha", 1, 0, 24}, {"max_life", 1, 0, 28}, {"offset", 1, 0, 32},
                                              {"position", 3, 0, 36}, {"size", 1, 0, 48}, {"speed", 1, 0, 52}};
constexpr uint32_t kGameSpriteStateSize = 64, kGameSpriteBlockSize = 48;
constexpr uint32_t kGameSpriteHead = 0, kGameSpriteImage = 16, kGameSpriteMesh = 28, kGameSpriteColour = 36, kGameSpriteSize = 44;

struct Entry {
    Module module;
    bool game_module = false;
    uint16_t depth = 0, state = 0;
    uint32_t parent = 0xffffffffu, child = 0, max_count = 1;
    uint32_t start = 0, count = 0;
    const std::vector<Member>* members = nullptr;
    ContentHash mod_module{};  // a mod's own sprite module, checked by cook_mods
};

void put_f(Bytes& d, size_t at, float v) { std::memcpy(&d[at], &v, 4); }
void put_u(Bytes& d, size_t at, uint32_t v) { std::memcpy(&d[at], &v, 4); }

Bytes emitter_block(const ParticleDef& p) {
    Bytes d(KE_BLOCK_SIZE, 0);
    put_u(d, KE_VERSION, KILN_VFX_ABI);
    put_u(d, KE_FLAGS, p.loop ? KE_FLAG_LOOP : 0u);
    put_f(d, KE_DURATION, p.duration);
    put_f(d, KE_DELAY, p.delay);
    put_f(d, KE_RATE, p.rate);
    put_u(d, KE_BURST, p.burst);
    put_u(d, KE_CHILD_STATE, KS_STATE_SIZE);
    return d;
}

Bytes sprite_block(const ParticleDef& p) {
    Bytes d(KS_BLOCK_SIZE, 0);
    put_u(d, KS_VERSION, KILN_VFX_ABI);
    uint32_t draw = 0;
    if (p.additive) draw |= KS_DRAW_ADDITIVE;
    if (p.lit) draw |= KS_DRAW_LIT;
    put_u(d, KS_DRAW_FLAGS, draw);
    put_u(d, KS_FLAGS, (p.face_camera ? KS_FLAG_FACE_CAMERA : 0u) | (p.align_velocity ? KS_FLAG_ALIGN_VELOCITY : 0u));
    // KS_IMAGE, KS_SAMPLER, KS_MESH stay 0: the game writes them through the patch points
    put_f(d, KS_LIFE_MIN, p.life.min);
    put_f(d, KS_LIFE_MAX, p.life.max);
    put_f(d, KS_SPEED_MIN, p.speed.min);
    put_f(d, KS_SPEED_MAX, p.speed.max);
    put_u(d, KS_SPREAD_KIND, p.spread);
    put_f(d, KS_SPREAD_ANGLE, p.cone);
    put_f(d, KS_SPAWN_RADIUS, p.shape == KS_SHAPE_SPHERE ? p.shape_size[0] : 0.0f);
    put_u(d, KS_SHAPE, p.shape);
    put_f(d, KS_SHAPE_A, p.shape_size[0]);
    put_f(d, KS_SHAPE_B, p.shape_size[1]);
    put_f(d, KS_SHAPE_C, p.shape_size[2]);
    put_f(d, KS_STRETCH, p.stretch);
    for (int i = 0; i < KS_USER_COUNT; ++i) put_f(d, KS_USER + i * 4, p.user[i]);
    put_f(d, KS_ACCEL_X, p.accel[0]);
    put_f(d, KS_ACCEL_Y, p.accel[1]);
    put_f(d, KS_ACCEL_Z, p.accel[2]);
    put_f(d, KS_DRAG, p.drag);
    put_f(d, KS_SPIN_MIN, p.spin.min);
    put_f(d, KS_SPIN_MAX, p.spin.max);
    put_f(d, KS_ROLL_MIN, p.roll.min);
    put_f(d, KS_ROLL_MAX, p.roll.max);
    put_f(d, KS_DEPTH_FADE, p.depth_fade);
    put_u(d, KS_FLIPBOOK, p.flip_columns ? (p.flip_columns | p.flip_rows << 16) : 0u);
    put_f(d, KS_FLIPBOOK_FPS, p.flip_fps);
    put_f(d, KS_ROUGHNESS, 1.0f);
    put_f(d, KS_EMISSIVE, p.emissive);
    for (int i = 0; i < KS_CURVE_SAMPLES; ++i) {
        put_f(d, KS_SIZE_CURVE + i * 4, p.size[i]);
        put_f(d, KS_ALPHA_CURVE + i * 4, p.alpha[i]);
        for (int c = 0; c < 3; ++c) put_f(d, KS_COLOR_CURVE + i * 12 + c * 4, p.color[i][c]);
    }
    return d;
}

Bytes game_sprite_block(const ParticleDef& p) {
    Bytes d(kGameSpriteBlockSize, 0);
    put_u(d, kGameSpriteHead, 0x20);  // what the game's own blocks hold here; the module doesn't read it
    auto srgb = [](float l) {
        l = std::clamp(l, 0.0f, 1.0f);
        const float c = l <= 0.0031308f ? l * 12.92f : 1.055f * std::pow(l, 1.0f / 2.4f) - 0.055f;
        return static_cast<uint32_t>(std::lround(c * 255.0f));
    };
    put_u(d, kGameSpriteColour, srgb(p.color[0][0]) | srgb(p.color[0][1]) << 8 | srgb(p.color[0][2]) << 16 | 0xffu << 24);
    put_f(d, kGameSpriteSize, p.size[0]);
    return d;
}

Bytes light_block(const LightDef& l) {
    Bytes d(KL_BLOCK_SIZE, 0);
    put_u(d, KL_VERSION, KILN_VFX_ABI);
    put_f(d, KL_LIFETIME, l.lifetime);
    put_f(d, KL_FADE_IN, l.fade_in);
    put_f(d, KL_FADE_OUT, l.fade_out);
    put_f(d, KL_RADIUS, l.radius);
    put_f(d, KL_BRIGHTNESS, l.brightness);
    for (int c = 0; c < 3; ++c) put_f(d, KL_COLOR + c * 4, l.color[c]);
    put_f(d, KL_FLICKER, l.flicker);
    for (int c = 0; c < 3; ++c) put_f(d, KL_OFFSET + c * 4, l.offset[c]);
    return d;
}

}  // namespace

const std::vector<PlayParameter>& play_parameters() {
    static const std::vector<PlayParameter> list = [] {
        auto fnv = [](const char* s) {
            uint32_t h = 0x811c9dc5u;
            for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
            return h;
        };
        return std::vector<PlayParameter>{
            {"color", fnv("color"), kParamColor, KX_COLOR, 4},       {"size", fnv("size"), kParamFloat, KX_SIZE, 4},
            {"speed", fnv("speed"), kParamFloat, KX_SPEED, 4},       {"brightness", fnv("brightness"), kParamFloat, KX_BRIGHTNESS, 4},
            {"count", fnv("count"), kParamFloat, KX_COUNT, 4},
        };
    }();
    return list;
}

Bytes play_defaults() {
    Bytes d(64, 0);
    put_u(d, KX_COLOR, 0xffffffffu);
    put_f(d, KX_SIZE, 1.0f);
    put_f(d, KX_SPEED, 1.0f);
    put_f(d, KX_BRIGHTNESS, 1.0f);
    put_f(d, KX_COUNT, 1.0f);
    put_u(d, KX_MAGIC, KX_MAGIC_VALUE);
    return d;
}

bool build_vfx(const VfxDef& def, const std::string& mod, const std::function<Guid(const ParticleDef&)>& mesh_of,
               const std::function<Guid(const ParticleDef&)>& tex_of, BuiltVfx* out, std::string* why) {
    Bytes data;
    std::vector<Entry> entries;
    struct MeshPatch {
        uint32_t at;
        Guid g;
    };
    struct ImagePatch {
        uint32_t at;
        Guid g;
        uint8_t address[3];
    };
    std::vector<MeshPatch> mesh_patches;
    std::vector<ImagePatch> image_patches;
    auto add = [&](const Bytes& block) {
        const uint32_t at = static_cast<uint32_t>(data.size());
        data.insert(data.end(), block.begin(), block.end());
        return at;
    };

    // Two entries per ParticleBlock (root, child); a LightBlock is one more root.
    for (const ParticleDef& p : def.particles) {
        Entry root;
        root.module = Module::Emitter;
        root.depth = 0;
        root.state = KE_STATE_SIZE;
        root.child = static_cast<uint32_t>(entries.size()) + 1;
        root.members = &kEmitterState;
        const Bytes eb = emitter_block(p);
        root.start = add(eb);
        root.count = static_cast<uint32_t>(eb.size());
        const uint32_t root_index = static_cast<uint32_t>(entries.size());
        entries.push_back(root);

        Entry kid;
        kid.module = Module::Sprite;
        kid.game_module = def.game_sprite;
        if (!def.game_sprite) kid.mod_module = p.shader_hash;
        kid.depth = 1;
        kid.state = def.game_sprite ? kGameSpriteStateSize : KS_STATE_SIZE;
        kid.parent = root_index;
        kid.max_count = p.max_count;
        kid.members = def.game_sprite ? &kGameSpriteState : &kSpriteState;
        const Bytes sb = def.game_sprite ? game_sprite_block(p) : sprite_block(p);
        kid.start = add(sb);
        kid.count = static_cast<uint32_t>(sb.size());
        entries.push_back(kid);

        if (!p.texture.empty()) {
            ImagePatch ip{kid.start + (def.game_sprite ? kGameSpriteImage : KS_IMAGE), tex_of(p), {p.address[0], p.address[1], p.address[2]}};
            image_patches.push_back(ip);
        }
        mesh_patches.push_back({kid.start + (def.game_sprite ? kGameSpriteMesh : KS_MESH), mesh_of(p)});
    }
    if (def.light) {
        Entry l;
        l.module = Module::Light;
        l.state = KL_STATE_SIZE;
        l.members = &kLightState;
        const Bytes lb = light_block(*def.light);
        l.start = add(lb);
        l.count = static_cast<uint32_t>(lb.size());
        entries.push_back(l);
    }

    // The checks a mistake here would otherwise only meet in the game.
    if (entries.empty() || entries.size() > kMaxEntries) {
        *why = "needs 1.." + std::to_string(kMaxEntries) + " particle entries, has " + std::to_string(entries.size());
        return false;
    }
    if (data.size() > kMaxResourceData || data.size() % 4) {
        *why = "its parameter data (" + std::to_string(data.size()) + " bytes) does not fit the game's 16-bit ranges";
        return false;
    }
    for (const Entry& e : entries)
        if (e.max_count < 1 || e.max_count > KILN_VFX_MAX_PARTICLES) {
            *why = "maxCount " + std::to_string(e.max_count) + " is outside 1.." + std::to_string(KILN_VFX_MAX_PARTICLES);
            return false;
        }

    W w(kVfxSize);
    float longest_life = 0, stop_after = 0;
    bool loops = false;
    for (const ParticleDef& p : def.particles) {
        longest_life = std::max(longest_life, p.life.max);
        if (p.loop || (p.duration <= 0 && p.rate > 0)) {
            loops = true;
        } else {
            const float spawning = std::ceil(static_cast<float>(p.burst) * KX_MAX_FACTOR / KILN_VFX_MAX_SPAWNS_PER_TICK) / 60.0f;
            stop_after = std::max(stop_after, p.delay + p.duration + spawning + p.life.max);
        }
    }
    float fade_out = 0;
    if (def.light) {
        fade_out = def.light->fade_out;
        if (def.light->lifetime > 0) stop_after = std::max(stop_after, def.light->lifetime);
        else if (def.particles.empty()) loops = true;
    }
    const uint32_t kill = std::max(kDefaultKillTime, static_cast<uint32_t>(std::ceil(longest_life + fade_out)));
    uint32_t emitters = 0;
    for (const Entry& e : entries) emitters += e.child ? 1 : 0;

    w.f32(0x00, def.cull_min);
    w.f32(0x04, def.cull_max);
    w.u32(0x08, kill);
    w.u32(0x0c, emitters);
    // useNewVm (0x10) false

    // particles
    const size_t pat = w.reserve(kParticleSize * entries.size(), 8);
    w.array(0x14, pat, static_cast<uint32_t>(entries.size()));
    for (size_t i = 0; i < entries.size(); ++i) {
        const Entry& e = entries[i];
        const size_t o = pat + kParticleSize * i;
        const ContentHash code = e.game_module ? kGameSpriteModule : e.mod_module.size ? e.mod_module : starter_module(e.module).hash;
        w.string(o + 0x00, guid_text(block_id(mod, "vfx-entry", def.id_name + ":" + std::to_string(i))));
        w.u16(o + 0x08, e.depth);
        w.u16(o + 0x0a, e.state);
        w.u32(o + 0x0c, e.parent);
        w.u32(o + 0x10, e.child);
        w.u32(o + 0x14, e.max_count);
        // code (0x18): zeros. The selector 0xabde30 never picks it, and vanilla's isn't in the KFC either.
        w.content_ref(o + 0x38, code, kCategoryPipeline, kBundlePipeline, i);
        // initialState (0x58) empty: state starts zeroed, the modules initialise on tick 0
        w.u16(o + 0x60, static_cast<uint16_t>(e.start));  // initializeStateGpuResourceDataRange {start, 0}
        w.u16(o + 0x64, static_cast<uint16_t>(e.start));  // evaluateParametersGpuResourceDataRange
        w.u16(o + 0x66, static_cast<uint16_t>(e.count));
    }
    // stateInfos: debug data, but kept correct
    const size_t sat = w.reserve(12 * entries.size(), 4);
    w.array(0x1c, sat, static_cast<uint32_t>(entries.size()));
    for (size_t i = 0; i < entries.size(); ++i) {
        const Entry& e = entries[i];
        const size_t s = sat + 12 * i;
        const size_t mat = w.reserve(20 * e.members->size(), 4);
        w.array(s, mat, static_cast<uint32_t>(e.members->size()));
        w.u32(s + 8, e.state);
        for (size_t k = 0; k < e.members->size(); ++k) {
            const Member& m = (*e.members)[k];
            const size_t mo = mat + 20 * k;
            w.string(mo, m.name);
            w.u32(mo + 8, m.components);
            w.u8(mo + 0xc, m.type);
            w.u32(mo + 0x10, m.offset);
        }
    }
    // springs (0x24) empty; gpuResourceData
    const size_t dat = w.reserve(data.size(), 1);
    std::memcpy(&w.b[dat], data.data(), data.size());
    w.array(0x2c, dat, static_cast<uint32_t>(data.size()));
    // meshes
    if (!mesh_patches.empty()) {
        const size_t at = w.reserve(20 * mesh_patches.size(), 4);
        w.array(0x34, at, static_cast<uint32_t>(mesh_patches.size()));
        for (size_t i = 0; i < mesh_patches.size(); ++i) {
            w.u16(at + 20 * i, static_cast<uint16_t>(mesh_patches[i].at));
            w.u16(at + 20 * i + 2, 4);
            w.guid(at + 20 * i + 4, mesh_patches[i].g);
        }
    }
    // models (0x3c), decals (0x44) empty; images
    if (!image_patches.empty()) {
        const size_t at = w.reserve(24 * image_patches.size(), 4);
        w.array(0x4c, at, static_cast<uint32_t>(image_patches.size()));
        for (size_t i = 0; i < image_patches.size(); ++i) {
            w.u16(at + 24 * i, static_cast<uint16_t>(image_patches[i].at));
            w.u16(at + 24 * i + 2, 8);
            w.guid(at + 24 * i + 4, image_patches[i].g);
            for (int c = 0; c < 3; ++c) w.u8(at + 24 * i + 0x14 + c, image_patches[i].address[c]);
        }
    }
    // lightFilters, sounds, curves: empty. parameters and defaultParameterData: the per-play parameters every Kiln VFX has (KX_*), so a play,
    // a template, a sequence or a buff sets them by name. references, joints: empty.
    const auto& params = play_parameters();
    {
        const size_t at = w.reserve(12 * params.size(), 4);
        w.array(0x6c, at, static_cast<uint32_t>(params.size()));
        for (size_t i = 0; i < params.size(); ++i) {
            w.u32(at + 12 * i, params[i].hash);
            w.u32(at + 12 * i + 4, params[i].type);
            w.u8(at + 12 * i + 8, static_cast<uint8_t>(params[i].offset));
            w.u8(at + 12 * i + 9, static_cast<uint8_t>(params[i].size));
            // jointCollectionIndex 0, as all of vanilla's
        }
    }
    const Bytes defaults = play_defaults();
    std::memcpy(&w.b[0x74], defaults.data(), defaults.size());

    out->guid = block_id(mod, "vfx", def.id_name);
    out->resource = std::move(w.b);
    out->entries = static_cast<uint32_t>(entries.size());
    out->emitters = emitters;
    out->kill_time = kill;
    out->loops = loops;
    out->stop_after = loops ? -1.0f : stop_after;
    uint32_t total = 0;
    for (const Entry& e : entries) total += e.max_count;
    out->max_count_total = total;
    return true;
}

bool builtin_mesh(const std::string& name, BuiltMesh* out) {
    // 28 B per vertex (docs/re/vfx.md 2.5): position f32x3, UV f16x2 (origin top-left), normal 11:11:10, tangent 10:10:10:2, colour RGBA8.
    struct V {
        float x, y, z;
        uint16_t u, v;
    };
    constexpr uint16_t k0 = 0x0000, k1 = 0x3c00;  // half float 0, 1
    std::vector<V> vs;
    uint32_t normal;
    const uint32_t tangent = 0x200803ffu, colour = 0xffffffffu;
    if (name == "quad") {  // XY plane, facing -Z: what a billboard draws
        vs = {{-0.5f, 0.5f, 0, k0, k0}, {-0.5f, -0.5f, 0, k0, k1}, {0.5f, -0.5f, 0, k1, k1}, {0.5f, 0.5f, 0, k1, k0}};
        normal = 0x00200400u;
    } else if (name == "quad-xz") {  // lying flat, facing +Y
        vs = {{0.5f, 0, -0.5f, k1, k1}, {0.5f, 0, 0.5f, k1, k0}, {-0.5f, 0, 0.5f, k0, k0}, {-0.5f, 0, -0.5f, k0, k1}};
        normal = 0x803ffc00u;
    } else {
        return false;
    }
    const uint16_t idx[6] = {0, 1, 2, 2, 3, 0};
    Bytes b;
    auto put = [&](const void* p, size_t n) { b.insert(b.end(), static_cast<const uint8_t*>(p), static_cast<const uint8_t*>(p) + n); };
    for (const V& v : vs) {
        put(&v.x, 4);
        put(&v.y, 4);
        put(&v.z, 4);
        put(&v.u, 2);
        put(&v.v, 2);
        put(&normal, 4);
        put(&tangent, 4);
        put(&colour, 4);
    }
    put(idx, sizeof idx);
    b = pad_mesh_blob(std::move(b));

    constexpr uint32_t kCategoryMesh = 2543730610u;
    const ContentHash h = content_hash(b.data(), b.size());
    W w(56);
    w.u32(0x00, 6);
    w.u32(0x04, static_cast<uint32_t>(vs.size()));
    w.content_ref(0x08, h, kCategoryMesh, 0, 0);
    w.f32(0x28 + 12, 0.70710677f);  // bounding sphere (0, 0, 0, r)

    out->guid = block_id("kiln", "vfx-mesh", name);
    out->resource = std::move(w.b);
    out->blob = std::move(b);
    out->hash = h;
    out->vertices = static_cast<uint32_t>(vs.size());
    out->indices = 6;
    return true;
}

Bytes pad_mesh_blob(Bytes blob) {
    // All 638 vanilla blobs are multiples of 28, and client 0x1ae46b computes a mesh's vertexOffset as allocation offset / 28 with the blob
    // sizes as the allocations. A size off the grid would shift the vertices of every mesh loaded after it, game meshes included.
    while (blob.size() % 28) blob.push_back(0);
    return blob;
}

}  // namespace kvb
