// SPDX-License-Identifier: MIT
// A block of the "vfx" section -> VfxDef. Errors name the block and the field, as the rest of the blocks do. A wrong block registers
// nothing; the file's other blocks load. The shape is in schema/kiln-vfx.schema.json and docs/kiln/services/vfx-cook.md: keep the three in step.
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cmath>
#include <set>

#include "../shaders/kiln_vfx_params.h"
#include "vfxcook.h"

namespace kvb {
namespace {

constexpr float kPi = 3.14159265358979f;
constexpr int kN = KS_CURVE_SAMPLES;
constexpr float kHuge = 1.0e6f;

ojson::const_iterator find(const ojson& o, const char* k) { return o.find(k); }

class P {
public:
    P(Diagnostics* d, std::string where) : d_(d), where_(std::move(where)) {}
    void err(const std::string& path, const std::string& msg) {
        d_->errors.push_back(where_ + "." + path + ": " + msg);
        ok_ = false;
    }
    void warn(const std::string& path, const std::string& msg) { d_->warnings.push_back(where_ + "." + path + ": " + msg); }
    void note(const std::string& path, const std::string& msg) { d_->notes.push_back(where_ + "." + path + ": " + msg); }
    bool ok() const { return ok_; }

    bool number(const ojson& v, const std::string& path, float lo, float hi, float* out) {
        if (!v.is_number()) {
            err(path, "expected a number, got " + v.dump());
            return false;
        }
        const double x = v.get<double>();
        if (!std::isfinite(x) || std::abs(x) > 1.0e30) {
            err(path, "not a usable number");
            return false;
        }
        const float f = static_cast<float>(x);
        if (f < lo) {
            err(path, v.dump() + " is less than " + show(lo));
            return false;
        }
        if (f > hi) {
            err(path, v.dump() + " is more than " + show(hi));
            return false;
        }
        *out = f;
        return true;
    }
    bool integer(const ojson& v, const std::string& path, int64_t lo, int64_t hi, uint32_t* out) {
        if (!v.is_number_integer()) {
            err(path, "expected a whole number, got " + v.dump());
            return false;
        }
        const int64_t x = v.get<int64_t>();
        if (x < lo || x > hi) {
            err(path, v.dump() + " is outside " + std::to_string(lo) + ".." + std::to_string(hi));
            return false;
        }
        *out = static_cast<uint32_t>(x);
        return true;
    }
    bool boolean(const ojson& v, const std::string& path, bool* out) {
        if (!v.is_boolean()) {
            err(path, "expected true or false, got " + v.dump());
            return false;
        }
        *out = v.get<bool>();
        return true;
    }
    // A number, or [min, max].
    bool range(const ojson& v, const std::string& path, float lo, float hi, Rangef* out) {
        if (v.is_number()) {
            float x;
            if (!number(v, path, lo, hi, &x)) return false;
            out->min = out->max = x;
            return true;
        }
        if (!v.is_array() || v.size() != 2) {
            err(path, "expected a number or [min, max], got " + v.dump());
            return false;
        }
        Rangef r;
        if (!number(v[0], path + "[0]", lo, hi, &r.min) || !number(v[1], path + "[1]", lo, hi, &r.max)) return false;
        if (r.min > r.max) {
            err(path, "min is above max");
            return false;
        }
        *out = r;
        return true;
    }
    // A curve: a number, {"linear":[a,b]}, {"keys":[[t,v],...]}, or [v,...] evenly spaced. Sampled to KS_CURVE_SAMPLES values.
    bool curve(const ojson& v, const std::string& path, float lo, float hi, float out[kN]) {
        std::vector<std::pair<float, float>> keys;
        if (v.is_number()) {
            float x;
            if (!number(v, path, lo, hi, &x)) return false;
            keys = {{0.f, x}};
        } else if (v.is_array()) {
            if (v.empty()) {
                err(path, "an empty curve");
                return false;
            }
            for (size_t i = 0; i < v.size(); ++i) {
                float x;
                if (!number(v[i], path + "[" + std::to_string(i) + "]", lo, hi, &x)) return false;
                keys.emplace_back(v.size() > 1 ? static_cast<float>(i) / static_cast<float>(v.size() - 1) : 0.f, x);
            }
        } else if (v.is_object() && v.contains("linear") && v.size() == 1) {
            const ojson& l = v["linear"];
            if (!l.is_array() || l.size() != 2) {
                err(path + ".linear", "expected [start, end]");
                return false;
            }
            float a, b;
            if (!number(l[0], path + ".linear[0]", lo, hi, &a) || !number(l[1], path + ".linear[1]", lo, hi, &b)) return false;
            keys = {{0.f, a}, {1.f, b}};
        } else if (v.is_object() && v.contains("keys") && v.size() == 1) {
            const ojson& ks = v["keys"];
            if (!ks.is_array() || ks.empty()) {
                err(path + ".keys", "expected [[t, value], ...]");
                return false;
            }
            float prev = -1;
            for (size_t i = 0; i < ks.size(); ++i) {
                const std::string p = path + ".keys[" + std::to_string(i) + "]";
                if (!ks[i].is_array() || ks[i].size() != 2) {
                    err(p, "expected [t, value]");
                    return false;
                }
                float t, x;
                if (!number(ks[i][0], p + "[0]", 0.f, 1.f, &t) || !number(ks[i][1], p + "[1]", lo, hi, &x)) return false;
                if (t <= prev) {
                    err(p, "t must grow from key to key");
                    return false;
                }
                prev = t;
                keys.emplace_back(t, x);
            }
        } else {
            err(path, "expected a number, a list, {\"linear\": [a, b]} or {\"keys\": [[t, v], ...]}, got " + v.dump());
            return false;
        }
        sample(keys, out);
        return true;
    }

    static void sample(const std::vector<std::pair<float, float>>& keys, float out[kN]) {
        for (int i = 0; i < kN; ++i) {
            const float t = static_cast<float>(i) / (kN - 1);
            float x = keys.back().second;
            if (t <= keys.front().first) x = keys.front().second;
            else
                for (size_t k = 0; k + 1 < keys.size(); ++k)
                    if (t <= keys[k + 1].first) {
                        const float u = (t - keys[k].first) / (keys[k + 1].first - keys[k].first);
                        x = keys[k].second + (keys[k + 1].second - keys[k].second) * u;
                        break;
                    }
            out[i] = x;
        }
    }

    static std::string show(float f) {
        char b[32];
        std::snprintf(b, sizeof b, "%g", static_cast<double>(f));
        return b;
    }

    // "#rrggbb" or "#rgb" -> linear RGB. Colours are written in sRGB; the modules take linear (docs/re/vfx.md 3.6).
    bool colour(const ojson& v, const std::string& path, float out[3]) {
        if (!v.is_string()) {
            err(path, "expected a colour like \"#7fd4ff\", got " + v.dump());
            return false;
        }
        std::string s = v.get<std::string>();
        if (!s.empty() && s[0] == '#') s.erase(0, 1);
        if (s.size() == 3) s = std::string{s[0], s[0], s[1], s[1], s[2], s[2]};
        auto hex = [](char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; };
        bool good = s.size() == 6;
        for (char c : s) good = good && hex(c) >= 0;
        if (!good) {
            err(path, "expected a colour like \"#7fd4ff\", got " + v.dump());
            return false;
        }
        for (int i = 0; i < 3; ++i) {
            const float c = static_cast<float>(hex(s[i * 2]) * 16 + hex(s[i * 2 + 1])) / 255.0f;
            out[i] = c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return true;
    }
    // A gradient: one colour, ["#a", "#b", ...] evenly spaced, or {"keys": [[t, "#a"], ...]}.
    bool gradient(const ojson& v, const std::string& path, float out[kN][3]) {
        struct K {
            float t, c[3];
        };
        std::vector<K> keys;
        if (v.is_string()) {
            K k{0.f, {}};
            if (!colour(v, path, k.c)) return false;
            keys.push_back(k);
        } else if (v.is_array()) {
            if (v.empty()) {
                err(path, "an empty gradient");
                return false;
            }
            for (size_t i = 0; i < v.size(); ++i) {
                K k{v.size() > 1 ? static_cast<float>(i) / static_cast<float>(v.size() - 1) : 0.f, {}};
                if (!colour(v[i], path + "[" + std::to_string(i) + "]", k.c)) return false;
                keys.push_back(k);
            }
        } else if (v.is_object() && v.contains("keys") && v.size() == 1 && v["keys"].is_array() && !v["keys"].empty()) {
            float prev = -1;
            const ojson& ks = v["keys"];
            for (size_t i = 0; i < ks.size(); ++i) {
                const std::string p = path + ".keys[" + std::to_string(i) + "]";
                if (!ks[i].is_array() || ks[i].size() != 2) {
                    err(p, "expected [t, \"#colour\"]");
                    return false;
                }
                K k{};
                if (!number(ks[i][0], p + "[0]", 0.f, 1.f, &k.t) || !colour(ks[i][1], p + "[1]", k.c)) return false;
                if (k.t <= prev) {
                    err(p, "t must grow from key to key");
                    return false;
                }
                prev = k.t;
                keys.push_back(k);
            }
        } else {
            err(path, "expected a colour, a list of colours or {\"keys\": [[t, \"#colour\"], ...]}, got " + v.dump());
            return false;
        }
        for (int c = 0; c < 3; ++c) {
            std::vector<std::pair<float, float>> one;
            for (const K& k : keys) one.emplace_back(k.t, k.c[c]);
            float s[kN];
            sample(one, s);
            for (int i = 0; i < kN; ++i) out[i][c] = s[i];
        }
        return true;
    }
    bool vec3(const ojson& v, const std::string& path, float lo, float hi, float out[3]) {
        if (!v.is_array() || v.size() != 3) {
            err(path, "expected [x, y, z], got " + v.dump());
            return false;
        }
        for (int i = 0; i < 3; ++i)
            if (!number(v[i], path + "[" + std::to_string(i) + "]", lo, hi, &out[i])) return false;
        return true;
    }
    void unknown(const ojson& o, const std::string& path, const std::set<std::string>& known) {
        for (auto it = o.begin(); it != o.end(); ++it)
            if (!known.count(it.key())) {
                std::string list;
                for (const std::string& k : known)
                    if (k != "comment") list += (list.empty() ? "" : ", ") + k;
                err((path.empty() ? "" : path + ".") + it.key(), "unknown field; there are " + list);
            }
    }

private:
    Diagnostics* d_;
    std::string where_;
    bool ok_ = true;
};

const std::set<std::string> kParticleKeys = {"burst",  "rate",     "duration", "delay", "loop",       "maxCount",   "lifetime",  "speed",   "spread",
                                             "spawnRadius", "gravity", "acceleration", "drag",    "spin",  "roll",  "size",  "alpha",  "color",
                                             "intensity",   "texture", "sampler",  "flipbook",    "blend", "lit",   "mesh",  "faceCamera", "depthFade",
                                             "emissive", "material", "shape", "align", "stretch", "shader", "comment"};

bool address_mode(const std::string& s, uint8_t* out) {
    if (s == "wrap") *out = 0;
    else if (s == "mirror") *out = 1;
    else if (s == "clamp") *out = 2;
    else if (s == "clampToZero") *out = 3;
    else return false;
    return true;
}

// {"sphere": r} {"sphere": r, "inner": r2} {"shell": r} {"hemisphere": r} {"box": [x, y, z]} (full sizes) {"disc": r} {"ring": r} {"line": length}
void parse_shape(P& p, const ojson& v, const std::string& path, ParticleDef* d) {
    static const char* kinds = "{\"sphere\": r}, {\"shell\": r}, {\"hemisphere\": r}, {\"box\": [x, y, z]}, {\"disc\": r}, {\"ring\": r}, {\"line\": length} or \"point\"";
    if (v.is_string() && v == "point") {
        d->shape = KS_SHAPE_POINT;
        return;
    }
    if (!v.is_object() || v.empty()) {
        p.err(path, std::string("expected ") + kinds + ", got " + v.dump());
        return;
    }
    std::string kind;
    for (auto it = v.begin(); it != v.end(); ++it)
        if (it.key() != "inner" && it.key() != "comment") {
            if (!kind.empty()) {
                p.err(path, "one shape at a time: " + kind + " and " + it.key());
                return;
            }
            kind = it.key();
        }
    const std::string at = path + "." + kind;
    float r = 0;
    if (kind == "box") {
        float size[3];
        if (!p.vec3(v[kind], at, 0.f, kHuge, size)) return;
        d->shape = KS_SHAPE_BOX;
        for (int i = 0; i < 3; ++i) d->shape_size[i] = size[i] * 0.5f;
    } else if (kind == "sphere" || kind == "shell" || kind == "hemisphere" || kind == "disc" || kind == "ring") {
        if (!p.number(v[kind], at, 0.f, kHuge, &r)) return;
        d->shape = kind == "disc" || kind == "ring" ? KS_SHAPE_DISC : kind == "hemisphere" ? KS_SHAPE_HEMISPHERE : KS_SHAPE_SPHERE;
        d->shape_size[0] = r;
        d->shape_size[1] = kind == "shell" || kind == "ring" ? r : 0.f;
    } else if (kind == "line") {
        if (!p.number(v[kind], at, 0.f, kHuge, &r)) return;
        d->shape = KS_SHAPE_LINE;
        d->shape_size[0] = r;
    } else {
        p.err(path, std::string("unknown shape \"") + kind + "\"; there are " + kinds);
        return;
    }
    if (v.contains("inner")) {
        float inner = 0;
        if (kind != "sphere" && kind != "hemisphere" && kind != "disc") p.err(path + ".inner", "only a sphere, hemisphere or disc has an inner radius");
        else if (p.number(v["inner"], path + ".inner", 0.f, kHuge, &inner)) {
            if (inner > r) p.err(path + ".inner", "is larger than the radius");
            else d->shape_size[1] = inner;
        }
    }
}

// "shaders/build/swirl.spv" or {"file": ..., "constants": [up to 8 numbers]}: a mod's own sprite module. Checked when the mod is built.
void parse_shader(P& p, const ojson& v, const std::string& path, ParticleDef* d) {
    if (v.is_string()) {
        d->shader = v.get<std::string>();
    } else if (v.is_object()) {
        p.unknown(v, path, {"file", "constants", "comment"});
        auto it = v.find("file");
        if (it != v.end() && it->is_string()) d->shader = it->get<std::string>();
        else p.err(path + ".file", "expected the path of a compiled .spv in your mod");
        if ((it = v.find("constants")) != v.end()) {
            if (!it->is_array() || it->size() > KS_USER_COUNT) p.err(path + ".constants", "expected up to " + std::to_string(KS_USER_COUNT) + " numbers");
            else
                for (size_t i = 0; i < it->size(); ++i) p.number((*it)[i], path + ".constants[" + std::to_string(i) + "]", -kHuge, kHuge, &d->user[i]);
        }
    } else {
        p.err(path, "expected the path of a compiled .spv in your mod, or {\"file\", \"constants\"}, got " + v.dump());
        return;
    }
    if (d->shader.empty() || d->shader.size() < 5 || d->shader.compare(d->shader.size() - 4, 4, ".spv") != 0)
        p.err(path, "expected the path of a compiled .spv in your mod (build it with kiln-vfxcook shader build), got " + v.dump());
}

void parse_particle(P& p, const ojson& v, const std::string& path, ParticleDef* d) {
    if (!v.is_object()) {
        p.err(path, "expected an object, got " + v.dump());
        return;
    }
    p.unknown(v, path, kParticleKeys);
    auto at = [&](const char* k) { return path + "." + k; };
    auto it = find(v, "burst");
    if (it != v.end()) p.integer(*it, at("burst"), 0, 65535, &d->burst);
    if ((it = find(v, "rate")) != v.end()) p.number(*it, at("rate"), 0.f, kHuge, &d->rate);
    if ((it = find(v, "duration")) != v.end() && p.number(*it, at("duration"), 0.f, 86400.f, &d->duration)) d->has_duration = true;
    if ((it = find(v, "delay")) != v.end()) p.number(*it, at("delay"), 0.f, 86400.f, &d->delay);
    if ((it = find(v, "loop")) != v.end()) p.boolean(*it, at("loop"), &d->loop);
    if ((it = find(v, "lifetime")) != v.end()) {
        p.range(*it, at("lifetime"), 0.f, 3600.f, &d->life);
        if (d->life.min <= 0) p.err(at("lifetime"), "must be above 0");
    }
    if ((it = find(v, "speed")) != v.end()) p.range(*it, at("speed"), -kHuge, kHuge, &d->speed);
    if ((it = find(v, "spread")) != v.end()) {
        if (it->is_string() && *it == "up") d->spread = KS_SPREAD_UP;
        else if (it->is_string() && *it == "sphere") d->spread = KS_SPREAD_SPHERE;
        else if (it->is_string() && *it == "outward") d->spread = KS_SPREAD_OUTWARD;
        else if (it->is_object() && it->size() == 1 && it->contains("cone")) {
            float deg = 0;
            if (p.number((*it)["cone"], at("spread") + ".cone", 0.f, 180.f, &deg)) {
                d->spread = KS_SPREAD_CONE;
                d->cone = deg * kPi / 180.f;
            }
        } else {
            p.err(at("spread"), "expected \"up\", \"sphere\", \"outward\" or {\"cone\": degrees}, got " + it->dump());
        }
    }
    if ((it = find(v, "spawnRadius")) != v.end() && p.number(*it, at("spawnRadius"), 0.f, kHuge, &d->spawn_radius) && d->spawn_radius > 0) {
        d->shape = KS_SHAPE_SPHERE;
        d->shape_size[0] = d->spawn_radius;
    }
    if ((it = find(v, "shape")) != v.end()) {
        if (find(v, "spawnRadius") != v.end()) p.err(at("shape"), "say either shape or spawnRadius (a sphere): not both");
        parse_shape(p, *it, at("shape"), d);
    }
    if ((it = find(v, "align")) != v.end()) {
        if (it->is_string() && *it == "velocity") d->align_velocity = true;
        else if (it->is_string() && *it == "camera") d->align_velocity = false;
        else p.err(at("align"), "expected \"camera\" or \"velocity\", got " + it->dump());
    }
    if ((it = find(v, "stretch")) != v.end()) {
        p.number(*it, at("stretch"), 0.f, 100.f, &d->stretch);
        if (!d->align_velocity) p.err(at("stretch"), "stretches along the velocity: it needs \"align\": \"velocity\"");
    }
    if ((it = find(v, "shader")) != v.end()) parse_shader(p, *it, at("shader"), d);
    if ((it = find(v, "acceleration")) != v.end()) p.vec3(*it, at("acceleration"), -kHuge, kHuge, d->accel);
    if ((it = find(v, "gravity")) != v.end()) {
        float g = 0;
        if (p.number(*it, at("gravity"), -kHuge, kHuge, &g)) d->accel[1] += g;  // world y, as ParticleBlock.Gravity = -4
    }
    if ((it = find(v, "drag")) != v.end()) p.number(*it, at("drag"), 0.f, kHuge, &d->drag);
    if ((it = find(v, "spin")) != v.end() && p.range(*it, at("spin"), -kHuge, kHuge, &d->spin)) {
        d->spin.min *= kPi / 180.f;
        d->spin.max *= kPi / 180.f;
    }
    if ((it = find(v, "roll")) != v.end() && p.range(*it, at("roll"), -kHuge, kHuge, &d->roll)) {
        d->roll.min *= kPi / 180.f;
        d->roll.max *= kPi / 180.f;
    }
    if ((it = find(v, "size")) != v.end()) p.curve(*it, at("size"), 0.f, kHuge, d->size);
    if ((it = find(v, "alpha")) != v.end()) p.curve(*it, at("alpha"), 0.f, 1.f, d->alpha);
    if ((it = find(v, "color")) != v.end()) {
        d->color_set = true;
        p.gradient(*it, at("color"), d->color);
    }
    if ((it = find(v, "intensity")) != v.end()) {
        float x = 1;
        if (p.number(*it, at("intensity"), 0.f, kHuge, &x))
            for (auto& c : d->color)
                for (float& channel : c) channel *= x;
    }
    if ((it = find(v, "material")) != v.end()) {
        if (it->is_string() && !it->get<std::string>().empty()) d->material = it->get<std::string>();
        else p.err(at("material"), "expected a material's name, got " + it->dump());
    }
    if ((it = find(v, "texture")) != v.end()) {
        d->texture_set = true;
        if (it->is_string()) d->texture = it->get<std::string>();
        else if (it->is_object()) {
            p.unknown(*it, at("texture"), {"file", "linear"});
            if (it->contains("file") && (*it)["file"].is_string()) d->texture = (*it)["file"].get<std::string>();
            else p.err(at("texture") + ".file", "expected a path to a PNG in your mod");
            if (it->contains("linear")) p.boolean((*it)["linear"], at("texture") + ".linear", &d->texture_linear);
        } else {
            p.err(at("texture"), "expected a path to a PNG in your mod, got " + it->dump());
        }
    }
    if ((it = find(v, "sampler")) != v.end()) {
        uint8_t m = 0;
        if (it->is_string() && address_mode(it->get<std::string>(), &m)) d->address[0] = d->address[1] = m;
        else p.err(at("sampler"), "expected \"wrap\", \"mirror\", \"clamp\" or \"clampToZero\", got " + it->dump());
    }
    if ((it = find(v, "flipbook")) != v.end()) {
        if (!it->is_object()) {
            p.err(at("flipbook"), "expected {\"columns\": n, \"rows\": m, \"fps\": f}");
        } else {
            p.unknown(*it, at("flipbook"), {"columns", "rows", "fps"});
            d->flip_columns = d->flip_rows = 1;
            if (it->contains("columns")) p.integer((*it)["columns"], at("flipbook") + ".columns", 1, 1024, &d->flip_columns);
            if (it->contains("rows")) p.integer((*it)["rows"], at("flipbook") + ".rows", 1, 1024, &d->flip_rows);
            if (it->contains("fps")) p.number((*it)["fps"], at("flipbook") + ".fps", 0.f, kHuge, &d->flip_fps);
            if (d->texture.empty() && d->material.empty()) p.warn(at("flipbook"), "a flipbook without a texture shows nothing but white");
        }
    }
    if ((it = find(v, "blend")) != v.end()) {
        if (it->is_string() && *it == "additive") d->additive = true;
        else if (it->is_string() && *it == "alpha") d->additive = false;
        else p.err(at("blend"), "expected \"alpha\" or \"additive\", got " + it->dump());
    }
    if ((it = find(v, "lit")) != v.end()) {
        d->lit_set = true;
        p.boolean(*it, at("lit"), &d->lit);
    }
    if ((it = find(v, "mesh")) != v.end()) {
        BuiltMesh unused;
        if (it->is_string() && builtin_mesh(it->get<std::string>(), &unused)) d->mesh = it->get<std::string>();
        else p.err(at("mesh"), "expected \"quad\" or \"quad-xz\", got " + it->dump());
    }
    d->face_camera = d->mesh == "quad";
    if ((it = find(v, "faceCamera")) != v.end()) p.boolean(*it, at("faceCamera"), &d->face_camera);
    if (d->align_velocity && !d->face_camera) p.err(at("align"), "\"velocity\" turns a camera-facing quad: it needs faceCamera (a \"quad\" mesh faces the camera unless it says otherwise)");
    if ((it = find(v, "depthFade")) != v.end()) p.number(*it, at("depthFade"), 0.f, kHuge, &d->depth_fade);
    if ((it = find(v, "emissive")) != v.end()) p.number(*it, at("emissive"), 0.f, 1.f, &d->emissive);

    // what has to be decided from the whole block
    if (d->burst == 0 && d->rate <= 0) p.err(path, "spawns nothing: give it a burst or a rate");
    if (d->loop && d->duration <= 0) p.err(at("loop"), "a loop needs a duration above 0");
    if (d->rate > KILN_VFX_MAX_SPAWNS_PER_TICK * 60)
        p.warn(at("rate"), "above " + std::to_string(KILN_VFX_MAX_SPAWNS_PER_TICK * 60) + " per second: the module spawns at most " +
                               std::to_string(KILN_VFX_MAX_SPAWNS_PER_TICK) + " per tick");
    // A burst-only block needs a small non-zero duration: with 0 its emitter runs until the instance stops.
    if (d->rate <= 0 && d->duration <= 0) d->duration = kBurstDuration;
    for (float s : d->size)
        if (s > KILN_VFX_MAX_SCALE) {
            p.warn(at("size"), "larger than " + P::show(static_cast<float>(KILN_VFX_MAX_SCALE)) + " m: the module clamps it");
            break;
        }

    // maxCount: the game reads it on the CPU, where the modules can't enforce it, so it stops here.
    if ((it = find(v, "maxCount")) != v.end()) {
        p.integer(*it, at("maxCount"), 1, KILN_VFX_MAX_PARTICLES, &d->max_count);
    } else {
        double need = 0;
        const double life = d->life.max;
        const double loops = d->loop && d->duration > 0 ? std::ceil(life / d->duration) + 1 : 1;
        need += d->burst * loops * KX_MAX_FACTOR;
        need += std::ceil(d->rate * KX_MAX_FACTOR * life) + (d->rate > 0 ? 1 : 0);
        if (need > KILN_VFX_MAX_PARTICLES)
            p.warn(path, "wants " + std::to_string(static_cast<int64_t>(need)) + " particles alive at once; held to " + std::to_string(KILN_VFX_MAX_PARTICLES));
        d->max_count = static_cast<uint32_t>(std::clamp(need, 1.0, static_cast<double>(KILN_VFX_MAX_PARTICLES)));
    }
    if (d->max_count && d->burst > d->max_count) p.warn(at("burst"), "more than maxCount: only " + std::to_string(d->max_count) + " can live at once");
}

void parse_light(P& p, const ojson& v, const std::string& path, LightDef* d) {
    if (!v.is_object()) {
        p.err(path, "expected an object, got " + v.dump());
        return;
    }
    p.unknown(v, path, {"color", "radius", "brightness", "lifetime", "fadeIn", "fadeOut", "flicker", "offset", "comment"});
    auto at = [&](const char* k) { return path + "." + k; };
    auto it = find(v, "color");
    if (it != v.end()) p.colour(*it, at("color"), d->color);
    if ((it = find(v, "radius")) != v.end()) {
        p.number(*it, at("radius"), 0.f, kHuge, &d->radius);
        if (d->radius <= 0) p.err(at("radius"), "must be above 0");
        if (d->radius > KILN_VFX_MAX_SCALE) p.warn(at("radius"), "larger than " + P::show(static_cast<float>(KILN_VFX_MAX_SCALE)) + " m: the module clamps it");
    }
    if ((it = find(v, "brightness")) != v.end()) p.number(*it, at("brightness"), 0.f, kHuge, &d->brightness);
    if ((it = find(v, "lifetime")) != v.end()) p.number(*it, at("lifetime"), 0.f, 86400.f, &d->lifetime);
    if ((it = find(v, "fadeIn")) != v.end()) p.number(*it, at("fadeIn"), 0.f, 86400.f, &d->fade_in);
    if ((it = find(v, "fadeOut")) != v.end()) p.number(*it, at("fadeOut"), 0.f, 86400.f, &d->fade_out);
    if ((it = find(v, "flicker")) != v.end()) p.number(*it, at("flicker"), 0.f, 1.f, &d->flicker);
    if ((it = find(v, "offset")) != v.end()) p.vec3(*it, at("offset"), -kHuge, kHuge, d->offset);
}

void kind_defaults(MaterialDef* d) {
    if (d->kind == "metal") {
        d->metallic = 1;
        d->roughness = 0.35f;
    } else if (d->kind == "cutout") {
        d->alpha_test = true;
    } else if (d->kind == "foliage") {
        d->alpha_test = true;
        d->double_sided = true;
        d->translucent = 1;
        d->roughness = 0.8f;
    } else if (d->kind == "cloth") {
        d->double_sided = true;
        d->roughness = 0.85f;
    } else if (d->kind == "emissive") {
        d->has_emissive = true;
        d->emissive_strength = 4;
    } else if (d->kind == "dithered") {
        d->hashed = true;
    }
}

const std::set<std::string> kMaterialKinds = {"opaque", "metal", "cutout", "foliage", "cloth", "emissive", "dithered"};

void parse_emissive(P& p, const ojson& v, const std::string& path, MaterialDef* d) {
    auto read_colour = [&](const std::string& text, const std::string& where) {
        ojson c = text;
        p.colour(c, where, d->emissive_color);
    };
    if (v.is_string()) {
        const std::string text = v.get<std::string>();
        if (!text.empty() && text[0] == '#') read_colour(text, path);
        else d->emissive_map = text;
    } else if (v.is_object()) {
        p.unknown(v, path, {"map", "color", "strength", "exposure", "comment"});
        auto it = v.find("map");
        if (it != v.end()) {
            if (it->is_string() && !it->get<std::string>().empty()) d->emissive_map = it->get<std::string>();
            else p.err(path + ".map", "expected a path to a PNG in your mod, got " + it->dump());
        }
        if ((it = v.find("color")) != v.end()) p.colour(*it, path + ".color", d->emissive_color);
        if ((it = v.find("strength")) != v.end()) p.number(*it, path + ".strength", 0.f, kHuge, &d->emissive_strength);
        if ((it = v.find("exposure")) != v.end()) p.number(*it, path + ".exposure", 0.f, 1.f, &d->emissive_exposure);
    } else {
        p.err(path, "expected a PNG, \"#rrggbb\" or {\"map\", \"color\", \"strength\"}, got " + v.dump());
    }
    d->has_emissive = true;
}

}  // namespace

bool parse_material(const ojson& value, const std::string& name, const std::string& where, MaterialDef* out, Diagnostics* d) {
    P p(d, where);
    bool name_ok = !name.empty();
    for (unsigned char c : name) name_ok = name_ok && (std::isalnum(c) || c == '_' || c == '.' || c == '-');
    if (!name_ok) {
        d->errors.push_back(where + ": the name may hold letters, digits, _ . and -");
        return false;
    }
    if (!value.is_object()) {
        d->errors.push_back(where + ": expected an object, got " + value.dump());
        return false;
    }
    p.unknown(value, "", {"kind", "albedo", "tint", "normal", "params", "metallicRoughness", "occlusion", "roughness", "metallic", "reflectance", "emissive", "alphaTest", "doubleSided",
                          "shadows", "contactShadows", "frost", "snow", "wetness", "vertexColor", "translucent", "dissolve", "uvScale", "shader", "was", "replaces", "comment"});
    MaterialDef def;
    def.name = name;
    def.id_name = name;
    def.where = where;
    auto it = value.find("kind");
    if (it != value.end()) {
        if (it->is_string() && kMaterialKinds.count(it->get<std::string>())) {
            def.kind = it->get<std::string>();
        } else if (it->is_string() && *it == "transparent") {
            p.err("kind", "\"transparent\": the game draws no transparent surface from a material's data alone. It comes with ShaderBlock; until then, \"dithered\" has soft edges");
        } else {
            p.err("kind", "expected opaque, metal, cutout, foliage, cloth, emissive or dithered, got " + it->dump());
        }
    }
    kind_defaults(&def);
    auto png = [&](const char* key, std::string* into) {
        const auto f = value.find(key);
        if (f == value.end()) return;
        if (f->is_string() && !f->get<std::string>().empty()) *into = f->get<std::string>();
        else p.err(key, "expected a path to a PNG in your mod, got " + f->dump());
    };
    auto flag = [&](const char* key, bool* into) {
        const auto f = value.find(key);
        if (f != value.end()) p.boolean(*f, key, into);
    };
    png("albedo", &def.albedo);
    if ((it = value.find("tint")) != value.end()) p.colour(*it, "tint", def.tint);
    if ((it = value.find("normal")) != value.end()) {
        if (it->is_string() && !it->get<std::string>().empty()) {
            def.normal = it->get<std::string>();
        } else if (it->is_object()) {
            p.unknown(*it, "normal", {"map", "strength", "yDown", "comment"});
            const auto m = it->find("map");
            if (m != it->end() && m->is_string() && !m->get<std::string>().empty()) def.normal = m->get<std::string>();
            else p.err("normal.map", "expected a path to a PNG in your mod");
            if (it->contains("strength")) p.number((*it)["strength"], "normal.strength", 0.f, 16.f, &def.normal_strength);
            if (it->contains("yDown")) p.boolean((*it)["yDown"], "normal.yDown", &def.normal_y_down);
        } else {
            p.err("normal", "expected a path to a PNG in your mod or {\"map\", \"strength\", \"yDown\"}, got " + it->dump());
        }
    }
    png("params", &def.params);
    png("metallicRoughness", &def.metallic_roughness);
    png("occlusion", &def.occlusion);
    if (!def.params.empty() && (!def.metallic_roughness.empty() || !def.occlusion.empty()))
        p.err("params", "holds metallic, roughness and occlusion already: give params, or metallicRoughness and occlusion");
    if ((it = value.find("roughness")) != value.end() && p.number(*it, "roughness", 0.f, 1.f, &def.roughness)) def.has_roughness = true;
    if ((it = value.find("metallic")) != value.end() && p.number(*it, "metallic", 0.f, 1.f, &def.metallic)) def.has_metallic = true;
    if ((it = value.find("reflectance")) != value.end()) {
        if (it->is_string() && *it == "params") {
            def.reflectance_from_params = true;
            if (def.params.empty()) p.err("reflectance", "\"params\" reads the alpha of a params map, and this material has none");
        } else {
            p.number(*it, "reflectance", 0.f, 1.f, &def.reflectance);
        }
    }
    if ((it = value.find("emissive")) != value.end()) parse_emissive(p, *it, "emissive", &def);
    // "emissive" with nothing said glows with its own surface.
    if (def.kind == "emissive" && def.emissive_map.empty() && !value.contains("emissive")) {
        def.emissive_map = def.albedo;
        for (int c = 0; c < 3; ++c) def.emissive_color[c] = def.tint[c];
    }
    if ((it = value.find("alphaTest")) != value.end()) {
        if (it->is_boolean()) {
            def.alpha_test = it->get<bool>();
        } else if (p.number(*it, "alphaTest", 0.f, 1.f, &def.alpha_cutoff)) {
            def.alpha_test = true;
            if (def.alpha_cutoff <= 0) p.err("alphaTest", "must be above 0: nothing would be cut");
        }
    }
    flag("doubleSided", &def.double_sided);
    flag("shadows", &def.shadows);
    flag("contactShadows", &def.contact_shadows);
    flag("frost", &def.frost);
    flag("snow", &def.snow);
    flag("wetness", &def.wetness);
    flag("vertexColor", &def.vertex_color);
    if ((it = value.find("translucent")) != value.end()) p.number(*it, "translucent", 0.f, 1.f, &def.translucent);
    if ((it = value.find("dissolve")) != value.end()) {
        if (it->is_boolean()) {
            def.dissolve = it->get<bool>();
        } else if (it->is_object()) {
            def.dissolve = true;
            p.unknown(*it, "dissolve", {"color", "width", "noiseTiling", "noiseBlend", "comment"});
            if (it->contains("color")) p.colour((*it)["color"], "dissolve.color", def.dissolve_color);
            if (it->contains("width")) {
                p.number((*it)["width"], "dissolve.width", 0.f, 1.f, &def.dissolve_width);
                if (def.dissolve_width <= 0) p.err("dissolve.width", "must be above 0");
            }
            if (it->contains("noiseTiling")) p.number((*it)["noiseTiling"], "dissolve.noiseTiling", 0.f, kHuge, &def.dissolve_tiling);
            if (it->contains("noiseBlend")) p.number((*it)["noiseBlend"], "dissolve.noiseBlend", 0.f, 1.f, &def.dissolve_fade);
        } else {
            p.err("dissolve", "expected true, false or {\"color\", \"width\", \"noiseTiling\", \"noiseBlend\"}, got " + it->dump());
        }
    }
    if ((it = value.find("uvScale")) != value.end()) {
        if (it->is_number()) {
            float x = 1;
            if (p.number(*it, "uvScale", 0.f, kHuge, &x)) def.uv_scale[0] = def.uv_scale[1] = x;
        } else if (it->is_array() && it->size() == 2) {
            p.number((*it)[0], "uvScale[0]", 0.f, kHuge, &def.uv_scale[0]);
            p.number((*it)[1], "uvScale[1]", 0.f, kHuge, &def.uv_scale[1]);
        } else {
            p.err("uvScale", "expected a number or [u, v], got " + it->dump());
        }
    }
    // The place for a surface shader. Nothing can run one yet, so a material that names one must not silently draw without it.
    if ((it = value.find("shader")) != value.end())
        p.err("shader", "surface shaders aren't built yet: a material draws with the game's own shaders. Take this out for now");
    if ((it = value.find("replaces")) != value.end()) {
        Guid g;
        if (it->is_string() && parse_guid(it->get<std::string>(), &g)) def.replaces = g;
        else p.err("replaces", "expected the GUID of one of the game's materials (kiln catalog, or kfc.py list --type keen::RenderMaterialResource), got " + it->dump());
        if (value.contains("was")) p.err("replaces", "a material that replaces one of the game's has that one's id: \"was\" has no meaning here");
    }
    if ((it = value.find("was")) != value.end()) {
        if (it->is_string() && !it->get<std::string>().empty()) def.id_name = it->get<std::string>();
        else p.err("was", "expected the block's old name, got " + it->dump());
    }
    if (!p.ok()) return false;
    *out = std::move(def);
    return true;
}

ParticleDef::ParticleDef() {
    for (int i = 0; i < kN; ++i) {
        size[i] = 0.25f;
        alpha[i] = 1.f;
        color[i][0] = color[i][1] = color[i][2] = 1.f;
    }
}

bool parse_block(const ojson& value, const std::string& name, const std::string& where, VfxDef* out, Diagnostics* d) {
    P p(d, where);
    bool name_ok = !name.empty();
    for (unsigned char c : name) name_ok = name_ok && (std::isalnum(c) || c == '_' || c == '.' || c == '-');
    if (!name_ok) {
        d->errors.push_back(where + ": the name may hold letters, digits, _ . and -");
        return false;
    }
    if (!value.is_object()) {
        d->errors.push_back(where + ": expected an object, got " + value.dump());
        return false;
    }
    p.unknown(value, "", {"particles", "light", "sound", "cullDistance", "was", "gameSpriteModule", "comment"});
    VfxDef def;
    def.name = name;
    def.id_name = name;
    def.where = where;
    auto it = value.find("was");
    if (it != value.end()) {
        if (it->is_string() && !it->get<std::string>().empty()) def.id_name = it->get<std::string>();
        else p.err("was", "expected the block's old name, got " + it->dump());
    }
    if ((it = value.find("particles")) != value.end()) {
        if (!it->is_array()) {
            p.err("particles", "expected a list, got " + it->dump());
        } else {
            def.particles.resize(it->size());
            for (size_t i = 0; i < it->size(); ++i) parse_particle(p, (*it)[i], "particles[" + std::to_string(i) + "]", &def.particles[i]);
        }
    }
    if ((it = value.find("light")) != value.end() && !it->is_null()) {
        def.light.emplace();
        parse_light(p, *it, "light", &*def.light);
    }
    if ((it = value.find("sound")) != value.end() && !it->is_null()) {
        if (it->is_string() && !it->get<std::string>().empty()) {
            def.sound = it->get<std::string>();
            p.warn("sound", "not played yet: a VFX can't start a sound until kiln_sound exists. Counted in the budget of 10");
        } else {
            p.err("sound", "expected a sound's name, got " + it->dump());
        }
    }
    if ((it = value.find("cullDistance")) != value.end()) {
        Rangef r;
        if (it->is_number()) {
            float x = 75;
            if (p.number(*it, "cullDistance", 0.f, kHuge, &x)) {
                def.cull_max = x;
                def.cull_min = x * 2 / 3;  // vanilla's most common pair is 50, 75
            }
        } else if (p.range(*it, "cullDistance", 0.f, kHuge, &r)) {
            def.cull_min = r.min;
            def.cull_max = r.max;
        }
        if (def.cull_max <= 0) p.err("cullDistance", "must be above 0");
    }
    if ((it = value.find("gameSpriteModule")) != value.end() && p.boolean(*it, "gameSpriteModule", &def.game_sprite) && def.game_sprite)
        p.note("gameSpriteModule", "a diagnostic: its particles draw with a game module, which takes the first colour and size, the texture and the mesh, and bakes in the rest");
    if (def.particles.empty() && !def.light) p.err("particles", "nothing to draw: give it particles or a light");
    const size_t entries = def.particles.size() * 2 + (def.light ? 1 : 0);
    if (entries > kMaxEntries)
        p.err("particles", "too many: " + std::to_string(entries) + " particle entries (two for each block, one for a light), at most " + std::to_string(kMaxEntries));
    if (!p.ok()) return false;
    *out = std::move(def);
    return true;
}

}  // namespace kvb
