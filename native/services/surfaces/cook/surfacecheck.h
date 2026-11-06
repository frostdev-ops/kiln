// SPDX-License-Identifier: MIT
// A building material's surface, as a block writes it ("surface" in buildingMaterials and materialTweaks): read and checked. Self-contained
// (nlohmann/json only), so the table merge links it to refuse a wrong surface by name on every side, and the writer that cooks it reads the same
// struct. No file is opened here: whether a PNG exists and has a usable size is the cook's business. Guide: docs/kiln/modding/world-blocks.md.
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace ksf {

using ojson = nlohmann::ordered_json;

// The six layers of a slot, in the table's order. Side, top and bottom are the faces of a block; edge and edgeTop its worn edges; noise breaks up the rest.
enum Part : int { kSide, kTop, kBottom, kEdge, kEdgeTop, kNoise, kPartCount };
inline constexpr const char* kPartJson[kPartCount] = {"side", "top", "bottom", "edge", "edgeTop", "noise"};

// The maps of one layer, in the table's order.
enum MapKind : int { kEmissive, kAlbedo, kMetallic, kRoughness, kReflectance, kNormal, kHeight, kAo, kMapCount };
inline constexpr const char* kMapJson[kMapCount] = {"emissive", "albedo", "metallic", "roughness", "reflectance", "normal", "height", "ao"};

struct MapDef {
    bool set = false;
    std::string file;          // a PNG, relative to the mod's folder; "" none
    bool has_value = false;    // a plain value 0..1 instead of a file (metallic, roughness, reflectance, height, ao)
    float value = 0;
    bool has_color = false;    // albedo ("tint") and emissive: a colour, linear; with a file it multiplies it
    float color[3] = {1, 1, 1};
    float strength = 1;        // normal: how strong; emissive: intensity
    bool y_down = false;       // normal: a DirectX map
    std::optional<float> exposure, gi;  // emissive
};

struct LayerDef {
    enum Kind { Unset, Own, SameAs } kind = Unset;
    int same_as = -1;          // SameAs: the part of this surface it repeats
    std::string game;          // Own: starts from this game material's layer ("" none)
    int game_part = -1;        // which of its layers; -1: the same part
    MapDef map[kMapCount];
    std::optional<float> tiling;                         // repeats per 4 m
    std::optional<float> displacement_scale, displacement_offset;  // metres
};

struct SurfaceDef {
    LayerDef layer[kPartCount];
    std::optional<float> bevel;                // metres
    std::optional<float> edge_curve;           // 0..1, as the game keeps it
    std::optional<float> noise_min, noise_max; // where the noise layer starts and is full, in noise units
    std::optional<bool> edge_only_side;
    std::optional<int> mapping;                // keen::BuildingMappingType: 0 default, 1 wood. A roof material is always 2
    std::optional<std::string> decoration;     // a game material whose edge decoration it takes; "" none
};

// Reads `j`. false: *errors has one line per mistake, "<label>, surface.<field>: <what>", and *out is incomplete. `tweak`: the surface of a
// game material (a MaterialTweakBlock): a layer it doesn't write stays the game's, so no layer is required.
bool parse_surface(const ojson& j, const std::string& label, SurfaceDef* out, std::vector<std::string>* errors, bool tweak = false);

// The same check, for the table merge: what is wrong with `j`, appended to *errors.
void check_surface(const ojson& j, const std::string& label, std::vector<std::string>* errors, bool tweak = false);

// One form for each meaning: two surfaces that say the same thing in other words (a short form, a colour's case, a number written as 2 or
// 2.0) give equal JSON. What the table merge compares to tell a block it already has from a changed one. A surface that doesn't parse is
// returned as it is.
ojson surface_json(const SurfaceDef& s);
ojson normal_surface(const ojson& j, bool tweak = false);

// The part a layer finally takes its look from, after "same as" links: the part itself when it is its own, -1 for a loop.
int resolve_part(const SurfaceDef& s, int part);

}  // namespace ksf
