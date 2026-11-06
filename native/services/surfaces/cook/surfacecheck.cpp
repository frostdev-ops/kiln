// SPDX-License-Identifier: MIT
// See surfacecheck.h.
#include "surfacecheck.h"

#include <cctype>
#include <cmath>
#include <cstdio>

namespace ksf {
namespace {

struct Ctx {
    const std::string& label;
    std::vector<std::string>* errors;
    void fail(const std::string& field, const std::string& why) const { errors->push_back(label + ", surface." + field + ": " + why); }
};

std::string list(const char* const* names, int n) {
    std::string s;
    for (int i = 0; i < n; ++i) s += (i ? ", " : "") + std::string(names[i]);
    return s;
}

int part_of(const std::string& s) {
    for (int i = 0; i < kPartCount; ++i)
        if (s == kPartJson[i]) return i;
    return -1;
}

float srgb_to_linear(int v) {
    const float c = static_cast<float>(v) / 255.0f;
    return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f);
}

bool is_color(const ojson& v) { return v.is_string() && !v.get<std::string>().empty() && v.get<std::string>()[0] == '#'; }

bool read_color(const ojson& v, const Ctx& c, const std::string& field, float out[3]) {
    const std::string s = v.is_string() ? v.get<std::string>() : "";
    if (s.size() != 7 || s[0] != '#') {
        c.fail(field, v.dump() + " is not a colour: \"#rrggbb\"");
        return false;
    }
    for (int i = 0; i < 3; ++i) {
        int x = 0;
        for (int k = 0; k < 2; ++k) {
            const char ch = static_cast<char>(std::tolower(static_cast<unsigned char>(s[1 + i * 2 + k])));
            const int d = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : -1;
            if (d < 0) {
                c.fail(field, v.dump() + " is not a colour: \"#rrggbb\"");
                return false;
            }
            x = x * 16 + d;
        }
        out[i] = srgb_to_linear(x);
    }
    return true;
}

bool read_number(const ojson& v, const Ctx& c, const std::string& field, float lo, float hi, float* out, bool open_low = false) {
    if (!v.is_number()) {
        c.fail(field, v.dump() + " is not a number");
        return false;
    }
    const double x = v.get<double>();
    if (!std::isfinite(x) || x < lo || x > hi || (open_low && x <= lo)) {
        char b[96];
        std::snprintf(b, sizeof b, " is outside %s%g..%g", open_low ? "above " : "", lo, hi);
        c.fail(field, v.dump() + b);
        return false;
    }
    *out = static_cast<float>(x);
    return true;
}

// A PNG in the mod: relative, inside it, ending in .png.
bool read_file(const ojson& v, const Ctx& c, const std::string& field, std::string* out) {
    if (!v.is_string() || v.get<std::string>().empty()) {
        c.fail(field, "takes a PNG in the mod's folder");
        return false;
    }
    std::string s = v.get<std::string>();
    for (char& ch : s)
        if (ch == '\\') ch = '/';
    std::string low = s;
    for (char& ch : low) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    if (low.size() < 5 || low.compare(low.size() - 4, 4, ".png") != 0) {
        c.fail(field, "\"" + s + "\" is not a .png file");
        return false;
    }
    if (s[0] == '/' || (s.size() > 1 && s[1] == ':') || ("/" + s + "/").find("/../") != std::string::npos) {
        c.fail(field, "\"" + s + "\" has to be a path inside the mod's folder");
        return false;
    }
    *out = s;
    return true;
}

bool read_bool(const ojson& v, const Ctx& c, const std::string& field, bool* out) {
    if (!v.is_boolean()) {
        c.fail(field, "takes true or false");
        return false;
    }
    *out = v.get<bool>();
    return true;
}

// metallic, roughness, reflectance, height, ao: a PNG (its red channel) or a value 0..1
void read_scalar_map(const ojson& v, const Ctx& c, const std::string& field, MapDef* m) {
    if (v.is_number()) {
        float x = 0;
        if (read_number(v, c, field, 0, 1, &x)) {
            m->set = m->has_value = true;
            m->value = x;
        }
        return;
    }
    if (!v.is_string()) {
        c.fail(field, "takes a PNG or a value 0 to 1");
        return;
    }
    if (read_file(v, c, field, &m->file)) m->set = true;
}

void read_normal(const ojson& v, const Ctx& c, const std::string& field, MapDef* m) {
    if (v.is_string()) {
        if (read_file(v, c, field, &m->file)) m->set = true;
        return;
    }
    if (!v.is_object()) {
        c.fail(field, "takes a PNG or {\"map\", \"strength\", \"yDown\"}");
        return;
    }
    bool ok = true;
    for (auto it = v.begin(); it != v.end(); ++it) {
        if (it.key() == "map") ok = read_file(*it, c, field + ".map", &m->file) && ok;
        else if (it.key() == "strength") ok = read_number(*it, c, field + ".strength", 0, 8, &m->strength) && ok;
        else if (it.key() == "yDown") ok = read_bool(*it, c, field + ".yDown", &m->y_down) && ok;
        else {
            c.fail(field + "." + it.key(), "is not a field of a normal map; there are map, strength, yDown");
            ok = false;
        }
    }
    if (ok && m->file.empty()) {
        c.fail(field + ".map", "is required");
        ok = false;
    }
    m->set = ok;
}

void read_emissive(const ojson& v, const Ctx& c, const std::string& field, MapDef* m) {
    if (is_color(v)) {
        if (read_color(v, c, field, m->color)) m->set = m->has_color = true;
        return;
    }
    if (v.is_string()) {
        if (read_file(v, c, field, &m->file)) m->set = true;
        return;
    }
    if (!v.is_object()) {
        c.fail(field, "takes a PNG, a colour \"#rrggbb\", or {\"map\", \"color\", \"strength\", \"exposure\", \"gi\"}");
        return;
    }
    bool ok = true;
    for (auto it = v.begin(); it != v.end(); ++it) {
        const std::string f = field + "." + it.key();
        float x = 0;
        if (it.key() == "map") ok = read_file(*it, c, f, &m->file) && ok;
        else if (it.key() == "color") ok = (m->has_color = read_color(*it, c, f, m->color)) && ok;
        else if (it.key() == "strength") ok = read_number(*it, c, f, 0, 1000, &m->strength) && ok;
        else if (it.key() == "exposure") {
            if (read_number(*it, c, f, 0, 1, &x)) m->exposure = x;
            else ok = false;
        } else if (it.key() == "gi") {
            if (read_number(*it, c, f, 0, 1000, &x)) m->gi = x;
            else ok = false;
        } else {
            c.fail(f, "is not a field of an emissive; there are map, color, strength, exposure, gi");
            ok = false;
        }
    }
    if (ok && m->file.empty() && !m->has_color) {
        c.fail(field, "needs a map or a color");
        ok = false;
    }
    m->set = ok;
}

void read_layer(const ojson& v, int part, const Ctx& c, LayerDef* l) {
    const std::string field = kPartJson[part];
    if (v.is_string()) {
        const int other = part_of(v.get<std::string>());
        if (other < 0 || other == part) {
            c.fail(field, v.dump() + " is not another layer of this surface; there are " + list(kPartJson, kPartCount));
            return;
        }
        l->kind = LayerDef::SameAs;
        l->same_as = other;
        return;
    }
    if (!v.is_object()) {
        c.fail(field, "takes a layer {\"albedo\", ...}, a game material's layer {\"game\", \"layer\"}, or the name of another layer");
        return;
    }
    l->kind = LayerDef::Own;
    for (auto it = v.begin(); it != v.end(); ++it) {
        const std::string& k = it.key();
        const std::string f = field + "." + k;
        float x = 0;
        if (k == "albedo") {
            if (read_file(*it, c, f, &l->map[kAlbedo].file)) l->map[kAlbedo].set = true;
        } else if (k == "tint") {
            if (read_color(*it, c, f, l->map[kAlbedo].color)) l->map[kAlbedo].set = l->map[kAlbedo].has_color = true;
        }
        else if (k == "normal") read_normal(*it, c, f, &l->map[kNormal]);
        else if (k == "emissive") read_emissive(*it, c, f, &l->map[kEmissive]);
        else if (k == "metallic") read_scalar_map(*it, c, f, &l->map[kMetallic]);
        else if (k == "roughness") read_scalar_map(*it, c, f, &l->map[kRoughness]);
        else if (k == "reflectance") read_scalar_map(*it, c, f, &l->map[kReflectance]);
        else if (k == "height") read_scalar_map(*it, c, f, &l->map[kHeight]);
        else if (k == "ao") read_scalar_map(*it, c, f, &l->map[kAo]);
        else if (k == "tiling") {
            if (read_number(*it, c, f, 0, 64, &x, true)) l->tiling = x;
        } else if (k == "displacement") {
            if (!it->is_object()) {
                c.fail(f, "takes {\"scale\", \"offset\"} in metres");
                continue;
            }
            for (auto d = it->begin(); d != it->end(); ++d) {
                if (d.key() == "scale") {
                    if (read_number(*d, c, f + ".scale", 0, 2, &x)) l->displacement_scale = x;
                } else if (d.key() == "offset") {
                    if (read_number(*d, c, f + ".offset", -2, 2, &x)) l->displacement_offset = x;
                } else {
                    c.fail(f + "." + d.key(), "is not a field of a displacement; there are scale, offset");
                }
            }
        } else if (k == "game") {
            if (!it->is_string() || it->get<std::string>().empty()) c.fail(f, "takes a game material: a GameMaterials name");
            else l->game = it->get<std::string>();
        } else if (k == "layer") {
            const int p = it->is_string() ? part_of(it->get<std::string>()) : -1;
            if (p < 0) c.fail(f, it->dump() + " is not a layer; there are " + list(kPartJson, kPartCount));
            else l->game_part = p;
        } else if (k == "comment") {
        } else {
            c.fail(f, "is not a field of a surface layer; there are albedo, tint, normal, roughness, metallic, reflectance, height, ao, emissive, tiling, "
                      "displacement, game, layer");
        }
    }
    if (l->game.empty() && l->game_part >= 0) c.fail(field + ".layer", "names a game material's layer: it needs \"game\" too");
    if (l->game.empty() && !l->map[kAlbedo].set) c.fail(field + ".albedo", "is required: a PNG, or a \"tint\" for a plain colour, unless the layer starts from a game material's (\"game\")");
}

}  // namespace

int resolve_part(const SurfaceDef& s, int part) {
    for (int steps = 0; steps <= kPartCount; ++steps) {
        const LayerDef& l = s.layer[part];
        if (l.kind == LayerDef::Own) return part;
        if (l.kind == LayerDef::SameAs) part = l.same_as;
        else if (part == kSide) return -1;  // the side layer is the one every other defaults to
        else part = part == kEdgeTop ? kEdge : kSide;
    }
    return -1;
}

bool parse_surface(const ojson& j, const std::string& label, SurfaceDef* out, std::vector<std::string>* errors, bool tweak) {
    const size_t before = errors->size();
    const Ctx c{label, errors};
    *out = SurfaceDef{};
    if (!j.is_object()) {
        errors->push_back(label + ", surface: expected an object: {\"side\": {\"albedo\": \"textures/x.png\"}, ...}");
        return false;
    }
    for (auto it = j.begin(); it != j.end(); ++it) {
        const std::string& k = it.key();
        float x = 0;
        const int part = part_of(k);
        if (part >= 0) read_layer(*it, part, c, &out->layer[part]);
        else if (k == "bevel") {
            if (read_number(*it, c, k, 0, 1, &x)) out->bevel = x;
        } else if (k == "edgeCurve") {
            if (read_number(*it, c, k, 0, 1, &x, true)) out->edge_curve = x;
        } else if (k == "noiseRange") {
            float lo = 0, hi = 0;
            if (!it->is_array() || it->size() != 2) c.fail(k, "takes [start, full]: where the noise layer starts to show and where it covers all, 0 to 1");
            else if (read_number((*it)[0], c, k + "[0]", -4, 4, &lo) && read_number((*it)[1], c, k + "[1]", -4, 4, &hi)) {
                if (hi - lo < 0.001f) c.fail(k, "the second value has to be above the first");
                else {
                    out->noise_min = lo;
                    out->noise_max = hi;
                }
            }
        } else if (k == "edgeOnlySide") {
            bool b = false;
            if (read_bool(*it, c, k, &b)) out->edge_only_side = b;
        } else if (k == "mapping") {
            const std::string m = it->is_string() ? it->get<std::string>() : "";
            if (m == "default") out->mapping = 0;
            else if (m == "wood") out->mapping = 1;
            else c.fail(k, it->dump() + " is not a mapping; there are default, wood (a roof material always maps as a roof)");
        } else if (k == "decoration") {
            if (!it->is_string()) c.fail(k, "takes a game material whose edge decoration this surface shows, or \"none\"");
            else out->decoration = it->get<std::string>() == "none" ? "" : it->get<std::string>();
        } else if (k == "comment") {
        } else {
            c.fail(k, "is not a field of a surface; there are side, top, bottom, edge, edgeTop, noise, bevel, edgeCurve, noiseRange, edgeOnlySide, mapping, decoration");
        }
    }
    if (!tweak && out->layer[kSide].kind != LayerDef::Own) {
        if (out->layer[kSide].kind == LayerDef::Unset) c.fail("side", "is required: the layer the others start from");
        else c.fail("side", "has to be a layer of its own: the others start from it");
    }
    for (int p = 0; p < kPartCount && errors->size() == before; ++p) {
        // a tweak's "same as" chain may end at a layer it doesn't write (the game's); only a circle is wrong
        int at = p, steps = 0;
        while (out->layer[at].kind == LayerDef::SameAs && steps++ <= kPartCount) at = out->layer[at].same_as;
        if (steps > kPartCount || (!tweak && resolve_part(*out, p) < 0)) c.fail(kPartJson[p], "its \"same as\" links go round in a circle");
    }
    if (tweak && errors->size() == before) {
        bool any = false;
        for (const LayerDef& l : out->layer) any = any || l.kind != LayerDef::Unset;
        if (!any) errors->push_back(label + ", surface: changes no layer: write one of side, top, bottom, edge, edgeTop, noise");
    }
    return errors->size() == before;
}

namespace {
ojson rgb(const float c[3]) { return ojson::array({c[0], c[1], c[2]}); }
}  // namespace

ojson surface_json(const SurfaceDef& s) {
    ojson o = ojson::object();
    for (int p = 0; p < kPartCount; ++p) {
        const LayerDef& l = s.layer[p];
        if (l.kind == LayerDef::Unset) continue;
        if (l.kind == LayerDef::SameAs) {
            o[kPartJson[p]] = kPartJson[l.same_as];
            continue;
        }
        ojson L = ojson::object();
        if (!l.game.empty()) L["game"] = l.game;
        if (l.game_part >= 0) L["layer"] = kPartJson[l.game_part];
        for (int m = 0; m < kMapCount; ++m) {
            const MapDef& d = l.map[m];
            if (!d.set) continue;
            ojson x = ojson::object();
            if (!d.file.empty()) x["file"] = d.file;
            if (d.has_value) x["value"] = d.value;
            if (d.has_color) x["color"] = rgb(d.color);
            if (m == kNormal || m == kEmissive) x["strength"] = d.strength;
            if (m == kNormal) x["yDown"] = d.y_down;
            if (d.exposure) x["exposure"] = *d.exposure;
            if (d.gi) x["gi"] = *d.gi;
            L[kMapJson[m]] = std::move(x);
        }
        if (l.tiling) L["tiling"] = *l.tiling;
        if (l.displacement_scale) L["displacementScale"] = *l.displacement_scale;
        if (l.displacement_offset) L["displacementOffset"] = *l.displacement_offset;
        o[kPartJson[p]] = std::move(L);
    }
    if (s.bevel) o["bevel"] = *s.bevel;
    if (s.edge_curve) o["edgeCurve"] = *s.edge_curve;
    if (s.noise_min) o["noiseRange"] = ojson::array({*s.noise_min, *s.noise_max});
    if (s.edge_only_side) o["edgeOnlySide"] = *s.edge_only_side;
    if (s.mapping) o["mapping"] = *s.mapping;
    if (s.decoration) o["decoration"] = *s.decoration;
    return o;
}

ojson normal_surface(const ojson& j, bool tweak) {
    SurfaceDef s;
    std::vector<std::string> errors;
    return parse_surface(j, "", &s, &errors, tweak) ? surface_json(s) : j;
}

void check_surface(const ojson& j, const std::string& label, std::vector<std::string>* errors, bool tweak) {
    SurfaceDef s;
    parse_surface(j, label, &s, errors, tweak);
}

}  // namespace ksf
