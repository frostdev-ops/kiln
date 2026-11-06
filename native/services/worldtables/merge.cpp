// SPDX-License-Identifier: MIT
// See merge.h. The resources are the game's blob format: a struct at the start, BlobArray {int32 offset from the field itself,
// uint32 count} pointing further in. A table only grows by appending a new copy of its array to the end and pointing the
// header at it; what the old array's records point at stays where it is, so inner offsets are recomputed for the new
// address. The old array stays behind as dead bytes.
#include "merge.h"

#include "surfacecheck.h"
#include "terrain.h"

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <set>

namespace kwt {

namespace {

template <class T>
T rd(const uint8_t* p) {
    T v;
    std::memcpy(&v, p, sizeof v);
    return v;
}
template <class T>
void wr(uint8_t* p, T v) {
    std::memcpy(p, &v, sizeof v);
}

void sha1(const uint8_t* data, size_t len, uint8_t out[20]) {
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};
    Bytes m(data, data + len);
    m.push_back(0x80);
    while (m.size() % 64 != 56) m.push_back(0);
    const uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 7; i >= 0; --i) m.push_back(static_cast<uint8_t>(bits >> (i * 8)));
    auto rol = [](uint32_t x, int n) { return x << n | x >> (32 - n); };
    for (size_t o = 0; o < m.size(); o += 64) {
        uint32_t w[80];
        for (int t = 0; t < 16; ++t)
            w[t] = uint32_t{m[o + t * 4]} << 24 | uint32_t{m[o + t * 4 + 1]} << 16 | uint32_t{m[o + t * 4 + 2]} << 8 | uint32_t{m[o + t * 4 + 3]};
        for (int t = 16; t < 80; ++t) w[t] = rol(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int t = 0; t < 80; ++t) {
            uint32_t f, k;
            if (t < 20) f = (b & c) | (~b & d), k = 0x5A827999u;
            else if (t < 40) f = b ^ c ^ d, k = 0x6ED9EBA1u;
            else if (t < 60) f = (b & c) | (b & d) | (c & d), k = 0x8F1BBCDCu;
            else f = b ^ c ^ d, k = 0xCA62C1D6u;
            const uint32_t x = rol(a, 5) + f + e + k + w[t];
            e = d;
            d = c;
            c = rol(b, 30);
            b = a;
            a = x;
        }
        h[0] += a;
        h[1] += b;
        h[2] += c;
        h[3] += d;
        h[4] += e;
    }
    for (int i = 0; i < 5; ++i)
        for (int j = 0; j < 4; ++j) out[i * 4 + j] = static_cast<uint8_t>(h[i] >> (24 - j * 8));
}

void pad4(Bytes* b) {
    while (b->size() % 4) b->push_back(0);
}

int enum_index(const std::string& v, const char* const* names, int count) {
    for (int i = 0; i < count; ++i)
        if (v == names[i]) return i;
    return -1;
}

std::string join_names(const char* const* names, int count) {
    std::string s;
    for (int i = 0; i < count; ++i) s += (i ? ", " : "") + std::string(names[i]);
    return s;
}

bool valid_name(const std::string& s) {
    if (s.empty()) return false;
    for (unsigned char c : s)
        if (!std::isalnum(c) && c != '-' && c != '_' && c != '.') return false;
    return true;
}

}  // namespace

bool Guid::zero() const {
    for (uint8_t x : b)
        if (x) return false;
    return true;
}

// .NET order: the first three groups are little-endian.
std::string Guid::text() const {
    char s[37];
    std::snprintf(s, sizeof s, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12],
                  b[13], b[14], b[15]);
    return s;
}

bool Guid::parse(const std::string& s, Guid* out) {
    if (s.size() != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-') return false;
    static constexpr int order[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    int at = 0;
    Guid g;
    for (int i = 0; i < 16; ++i) {
        if (s[at] == '-') ++at;
        auto hex = [](char c) { return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1; };
        const int hi = hex(s[at]), lo = hex(s[at + 1]);
        if (hi < 0 || lo < 0) return false;
        g.b[order[i]] = static_cast<uint8_t>(hi << 4 | lo);
        at += 2;
    }
    *out = g;
    return true;
}

uint32_t fnv1a(const void* data, size_t n) {
    uint32_t h = 0x811c9dc5u;
    const auto* p = static_cast<const uint8_t*>(data);
    for (size_t i = 0; i < n; ++i) h = (h ^ p[i]) * 0x01000193u;
    return h;
}

Guid derive_guid(const std::string& mod, const std::string& kind, const std::string& name) {
    static constexpr uint8_t ns[16] = {0x6f, 0x6d, 0x2a, 0x44, 0x53, 0xc1, 0x5c, 0x52, 0x9d, 0x51, 0x0a, 0x3c, 0x6e, 0x1f, 0x2b, 0x77};  // big-endian
    const std::string text = mod + ":" + kind + ":" + name;
    Bytes all(ns, ns + 16);
    all.insert(all.end(), text.begin(), text.end());
    uint8_t h[20];
    sha1(all.data(), all.size(), h);
    h[6] = static_cast<uint8_t>((h[6] & 0x0f) | 0x50);
    h[8] = static_cast<uint8_t>((h[8] & 0x3f) | 0x80);
    Guid g;
    static constexpr int order[16] = {3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15};
    for (int i = 0; i < 16; ++i) g.b[order[i]] = h[i];
    return g;
}

uint32_t item_id_of(const Guid& g) { return fnv1a(g.b.data(), 16); }

ojson parse_relaxed(const std::string& text) { return ojson::parse(text, nullptr, false, true); }

// ---- blocks ------------------------------------------------------------------------------------------------------
void read_strength(const ojson& j, const std::string& where, const std::string& label, Strength* s, std::vector<std::string>* errors) {
    auto fail = [&](const std::string& field, const std::string& why) { errors->push_back(label + ", " + where + "." + field + ": " + why); };
    if (!j.is_object()) {
        errors->push_back(label + ", " + where + ": expected an object");
        return;
    }
    for (auto it = j.begin(); it != j.end(); ++it) {
        const std::string& k = it.key();
        const ojson& v = it.value();
        if (k == "hardness") {
            const int i = v.is_string() ? enum_index(v.get<std::string>(), kHardnessJson, 6) : -1;
            if (i < 0) fail(k, v.dump() + " is not a hardness; there are " + join_names(kHardnessJson, 6));
            else s->hardness = i;
        } else if (k == "health") {
            if (!v.is_number_integer() || v.get<int64_t>() < 1 || v.get<int64_t>() > 1000000) fail(k, v.dump() + ": takes a whole number, 1 to 1000000");
            else s->health = static_cast<int32_t>(v.get<int64_t>());
        } else if (k == "breaks") {
            const int i = v.is_string() ? enum_index(v.get<std::string>(), kBreaksJson, 4) : -1;
            if (i < 0) fail(k, v.dump() + " is not a class; there are " + join_names(kBreaksJson, 4));
            else s->breaks = i;
        } else if (k == "itemsPerBlocks") {
            if (!v.is_number_integer() || v.get<int64_t>() < 1 || v.get<int64_t>() > 100000) fail(k, v.dump() + ": takes a whole number, 1 to 100000");
            else s->items_per_blocks = static_cast<uint32_t>(v.get<int64_t>());
        } else if (k == "drops") {
            if (!v.is_string() || v.get<std::string>().empty()) fail(k, "takes an item: a GameItems name or a GUID");
            else s->drops = v.get<std::string>();
        } else {
            fail(k, "is not a field of a strength; there are hardness, health, breaks, itemsPerBlocks, drops");
        }
    }
}

namespace {
// "strength": { ... }, or the name of a block in the mod's "strengths" section.
void read_strength_or_name(const ojson& j, const ModFile& mf, const std::string& label, Strength* s, std::vector<std::string>* errors) {
    if (!j.is_string()) {
        read_strength(j, "strength", label, s, errors);
        return;
    }
    const auto it = mf.strengths.find(j.get<std::string>());
    if (it == mf.strengths.end()) {
        errors->push_back(label + ", strength: no strength \"" + j.get<std::string>() + "\" in this mod's \"strengths\" sections");
        return;
    }
    read_strength(it->second, "strength", label, s, errors);
}
// "surface": { ... }, or the name of a block in the mod's "surfaces" sections. The surface it means, or null with an error.
const ojson* read_surface_or_name(const ojson& j, const ModFile& mf, const std::string& label, bool tweak, std::vector<std::string>* errors) {
    const ojson* s = &j;
    if (j.is_string()) {
        const std::string name = j.get<std::string>();
        const auto it = mf.surfaces.find(name);
        if (it == mf.surfaces.end()) {
            errors->push_back(label + ", surface: " +
                              (name.find(':') != std::string::npos ? "\"" + name + "\" is another mod's: a data file names a surface of its own mod, or writes it here"
                                                                   : "no surface \"" + name + "\" in this mod's \"surfaces\" sections"));
            return nullptr;
        }
        s = &it->second;
    }
    const size_t before = errors->size();
    ksf::check_surface(*s, label, errors, tweak);
    return errors->size() == before ? s : nullptr;
}

// The block as a writer reads it: a surface named by a block is written out in it.
std::string with_surface(const ojson& j, const ojson* surface) {
    if (!surface || !j.contains("surface") || !j["surface"].is_string()) return j.dump();
    ojson copy = j;
    copy["surface"] = *surface;
    return copy.dump();
}
}  // namespace

void read_blocks(const ojson& doc, ModFile* out) {
    auto err = [&](const std::string& t) { out->errors.push_back(out->file + ": " + t); };
    if (!doc.is_object()) {
        err("expected an object");
        return;
    }
    if (auto it = doc.find("materialTweaks"); it != doc.end()) {
        if (!it->is_object()) err("materialTweaks: expected block name -> block");
        else
            for (auto b = it->begin(); b != it->end(); ++b) {
                const std::string label = "block materialTweak " + b.key();
                const ojson& j = b.value();
                if (!valid_name(b.key())) { err(label + ": a block name takes letters, digits, '-', '_' and '.'"); continue; }
                if (!j.is_object()) { err(label + ": expected an object"); continue; }
                Tweak t;
                t.mod = out->mod;
                t.dir = out->dir;
                t.block = b.key();
                t.definition = j.dump();
                std::vector<std::string> errors;
                for (auto f = j.begin(); f != j.end(); ++f) {
                    if (f.key() == "material") {
                        if (!f->is_string() || f->get<std::string>().empty()) errors.push_back(label + ", material: takes a game material: a GameMaterials name");
                        else t.material = f->get<std::string>();
                    } else if (f.key() == "strength") {
                        read_strength_or_name(*f, *out, label, &t.strength, &errors);
                    } else if (f.key() == "surface") {
                        // what kiln.surfaces cooks on a client; checked here on every side, so a wrong one is refused alike everywhere
                        if (const ojson* sf = read_surface_or_name(*f, *out, label, true, &errors)) t.definition = with_surface(j, sf);
                    } else if (f.key() == "feel" || f.key() == "comment" || f.key() == "was") {
                        // a table writer's fields; nothing the merge here has to read
                    } else {
                        errors.push_back(label + ", " + f.key() + ": is not a field of a material tweak; there are material, strength, surface");
                    }
                }
                if (t.material.empty() && errors.empty()) errors.push_back(label + ", material: is required: which game material it changes");
                if (!t.strength.any() && !j.contains("surface") && errors.empty()) errors.push_back(label + ": changes nothing: it needs a strength or a surface");
                if (!errors.empty()) {
                    for (const auto& e : errors) err(e);
                    continue;
                }
                out->tweaks.push_back(std::move(t));
            }
    }
    if (auto it = doc.find("buildingMaterials"); it != doc.end()) {
        if (!it->is_object()) err("buildingMaterials: expected block name -> block");
        else
            for (auto b = it->begin(); b != it->end(); ++b) {
                const std::string label = "block buildingMaterial " + b.key();
                const ojson& j = b.value();
                if (!valid_name(b.key())) { err(label + ": a block name takes letters, digits, '-', '_' and '.'"); continue; }
                if (!j.is_object()) { err(label + ": expected an object"); continue; }
                std::vector<std::string> errors;
                Material m;
                m.mod = out->mod;
                m.dir = out->dir;
                m.block = b.key();
                m.definition = j.dump();
                std::string was;
                for (auto f = j.begin(); f != j.end(); ++f) {
                    const std::string& k = f.key();
                    if (k == "strength") read_strength_or_name(*f, *out, label, &m.strength, &errors);
                    else if (k == "roof") {
                        if (!f->is_boolean()) errors.push_back(label + ", roof: takes true or false");
                        else m.roof = f->get<bool>();
                    } else if (k == "was") {
                        if (!f->is_string()) errors.push_back(label + ", was: takes the block's old name");
                        else was = f->get<std::string>();
                    } else if (k == "name") {
                        if (!f->is_string() || f->get<std::string>().empty()) errors.push_back(label + ", name: takes the name players read");
                    } else if (k == "surface") {
                        if (const ojson* sf = read_surface_or_name(*f, *out, label, false, &errors)) m.definition = with_surface(j, sf);
                    } else if (k == "recipe") {
                        // the block item's; its main input is also what digging gives back. The rest is Kiln.Blocks' to check
                        if (f->is_object()) {
                            if (f->contains("makes") && (*f)["makes"].is_number_integer() && (*f)["makes"].get<int64_t>() > 0)
                                m.loot_makes = static_cast<uint32_t>((*f)["makes"].get<int64_t>());
                            if (f->contains("needs") && (*f)["needs"].is_object() && !(*f)["needs"].empty()) {
                                const auto first = (*f)["needs"].begin();
                                if (first.value().is_number_integer() && first.value().get<int64_t>() > 0) {
                                    m.loot_from = first.key();
                                    m.loot_count = static_cast<uint32_t>(first.value().get<int64_t>());
                                }
                            }
                        }
                    } else if (k == "description" || k == "icon" || k == "feel" || k == "loot" || k == "comment" || k == "tweak") {
                        // the block item's and the table writers' fields
                    } else {
                        errors.push_back(label + ", " + k + ": is not a field of a building material; there are name, description, strength, roof, surface, icon, feel, recipe, loot, was");
                    }
                }
                if (!j.contains("name") && errors.empty()) errors.push_back(label + ", name: is required: what players read");
                if (out->clients != "mod" && errors.empty())
                    errors.push_back(label + ": mod " + out->mod + " says \"clients\": \"" + out->clients + "\", and a new material has to be on every player's game. Set \"clients\": \"mod\" in kiln.mod.json");
                if (!errors.empty()) {
                    for (const auto& e : errors) err(e);
                    continue;
                }
                const std::string key = was.empty() ? b.key() : was;
                m.guid = derive_guid(out->mod, "material", key);
                m.item = true;
                m.item_guid = derive_guid(out->mod, "item", key);
                m.item_id = item_id_of(m.item_guid);
                out->materials.push_back(std::move(m));
            }
    }
    read_terrain_blocks(doc, out);
    if (auto it = doc.find("blueprints"); it != doc.end()) {
        if (!it->is_object()) err("blueprints: expected block name -> block");
        else
            for (auto b = it->begin(); b != it->end(); ++b) {
                std::vector<std::string> errors;
                Shape sh;
                read_shape(b.key(), b.value(), *out, &sh, &errors);
                if (!errors.empty()) {
                    for (const auto& e : errors) err(e);
                    continue;
                }
                out->shapes.push_back(std::move(sh));
            }
    }
}

// The same cells Kiln.Blocks' ShapeCells makes: layers from the bottom up, rows from the back, cells from the left; # a cell, . or a space none. Or, from code,
// the cells already made: {"item": GUID, "size": [x, y, z], "cells": base64}.
void read_shape(const std::string& name, const ojson& j, const ModFile& mf, Shape* sh, std::vector<std::string>* errors) {
    const std::string label = "block blueprint " + name;
    auto fail = [&](const std::string& field, const std::string& why) { errors->push_back(label + (field.empty() ? "" : ", " + field) + ": " + why); };
    if (!valid_name(name)) return fail("", "a block name takes letters, digits, '-', '_' and '.'");
    if (!j.is_object()) return fail("", "expected an object");
    sh->mod = mf.mod;
    sh->block = name;
    std::string was;
    const ojson* cells = nullptr;
    const ojson* size = nullptr;
    std::string item;
    for (auto f = j.begin(); f != j.end(); ++f) {
        const std::string& k = f.key();
        if (k == "cells") cells = &f.value();
        else if (k == "size") size = &f.value();
        else if (k == "item") {
            if (!f->is_string() || !Guid::parse(f->get<std::string>(), &sh->item)) fail(k, "takes the blueprint item's GUID");
            item = f->is_string() ? f->get<std::string>() : "";
        } else if (k == "roof") {
            if (!f->is_boolean()) fail(k, "takes true or false");
            else sh->roof = f->get<bool>();
        } else if (k == "was") {
            if (!f->is_string()) fail(k, "takes the block's old name");
            else was = f->get<std::string>();
        } else if (k == "name") {
            if (!f->is_string() || f->get<std::string>().empty()) fail(k, "takes the name players read");
        } else if (k == "cost") {
            if (!f->is_number_integer() || f->get<int64_t>() < 1 || f->get<int64_t>() > 9999) fail(k, f->dump() + " is outside 1..9999");
        } else if (k == "description" || k == "recipe" || k == "tweak" || k == "comment") {
            // the blueprint item's fields, Kiln.Blocks' to read
        } else {
            fail(k, "is not a field of a shape; there are name, description, cells, roof, cost, recipe, was");
        }
    }
    if (!j.contains("name") && item.empty()) fail("name", "is required: what players read");
    if (!cells) fail("cells", "a shape needs its cells: layers of rows of # and .");
    if (!errors->empty()) return;
    if (cells->is_string()) {
        // the computed form code sends
        if (!size || !size->is_array() || size->size() != 3) return fail("size", "takes [x, y, z] with the cells");
        uint32_t dims[3];
        for (int i = 0; i < 3; ++i) {
            if (!(*size)[i].is_number_integer() || (*size)[i].get<int64_t>() < 1 || (*size)[i].get<int64_t>() > kMaxShapeCells) return fail("size", "each side is 1.." + std::to_string(kMaxShapeCells));
            dims[i] = static_cast<uint32_t>((*size)[i].get<int64_t>());
        }
        sh->x = dims[0], sh->y = dims[1], sh->z = dims[2];
        if (!base64_decode(cells->get<std::string>(), &sh->bits) || sh->bits.size() != (sh->x * sh->y * sh->z + 7) / 8) return fail("cells", "doesn't hold one bit per cell of its size");
    } else {
        if (!cells->is_array() || cells->empty()) return fail("cells", "a shape needs its cells: layers of rows of # and .");
        uint32_t x = 0, y = static_cast<uint32_t>(cells->size()), z = 0;
        for (size_t ly = 0; ly < cells->size(); ++ly) {
            const ojson& rows = (*cells)[ly];
            if (!rows.is_array()) return fail("cells[" + std::to_string(ly) + "]", "a layer is a list of rows");
            z = std::max<uint32_t>(z, static_cast<uint32_t>(rows.size()));
            for (size_t lz = 0; lz < rows.size(); ++lz) {
                if (!rows[lz].is_string()) return fail("cells[" + std::to_string(ly) + "][" + std::to_string(lz) + "]", "a row is a string of # and .");
                std::string r = rows[lz].get<std::string>();
                while (!r.empty() && std::isspace(static_cast<unsigned char>(r.back()))) r.pop_back();
                x = std::max<uint32_t>(x, static_cast<uint32_t>(r.size()));
            }
        }
        if (x == 0 || z == 0) return fail("cells", "the shape has no cells: # is a cell");
        if (x > kMaxShapeCells || y > kMaxShapeCells || z > kMaxShapeCells)
            return fail("cells", std::to_string(x) + " x " + std::to_string(y) + " x " + std::to_string(z) + " cells: a shape fits in " + std::to_string(kMaxShapeCells) + " each way (" +
                                     std::to_string(kMaxShapeCells / 2) + " m)");
        sh->x = x, sh->y = y, sh->z = z;
        sh->bits.assign((x * y * z + 7) / 8, 0);
        uint32_t full = 0;
        for (uint32_t ly = 0; ly < y; ++ly) {
            const ojson& rows = (*cells)[ly];
            for (uint32_t lz = 0; lz < rows.size(); ++lz) {
                const std::string r = rows[lz].get<std::string>();
                for (uint32_t lx = 0; lx < r.size(); ++lx) {
                    const char c = r[lx];
                    if (c == '.' || c == ' ') continue;
                    if (c != '#') return fail("cells[" + std::to_string(ly) + "][" + std::to_string(lz) + "]", std::string("'") + c + "' at " + std::to_string(lx) + ": a cell is # and none is . or a space");
                    const uint32_t i = lx + x * (ly + y * lz);
                    sh->bits[i / 8] |= static_cast<uint8_t>(1u << (i % 8));
                    ++full;
                }
            }
        }
        if (full == 0) return fail("cells", "the shape has no cells: # is a cell");
    }
    if (mf.clients != "mod")
        return fail("", "mod " + mf.mod + " says \"clients\": \"" + mf.clients + "\", and a new shape has to be on every player's game. Set \"clients\": \"mod\" in kiln.mod.json");
    if (item.empty()) sh->item = derive_guid(mf.mod, "item", was.empty() ? name : was);
    sh->item_id = item_id_of(sh->item);
    std::string hex;
    for (uint8_t b : sh->bits) {
        char h[3];
        std::snprintf(h, sizeof h, "%02x", b);
        hex += h;
    }
    ojson n = ojson::object();
    n["item"] = sh->item.text();
    n["size"] = ojson::array({sh->x, sh->y, sh->z});
    n["cells"] = hex;
    n["roof"] = sh->roof;
    sh->definition = n.dump();
}

// ---- ids ---------------------------------------------------------------------------------------------------------
Allocation allocate(std::vector<Material> live, std::vector<Material> kept, const std::vector<int>& free_ids, const std::vector<int>& deprecated) {
    Allocation a;
    auto by_guid = [](const Material& x, const Material& y) { return x.guid.text() < y.guid.text(); };
    std::sort(live.begin(), live.end(), by_guid);
    std::sort(kept.begin(), kept.end(), by_guid);
    std::vector<int> room = free_ids;
    room.insert(room.end(), deprecated.begin(), deprecated.end());
    a.room = room.size();
    const size_t holds = std::min(kept.size(), room.size());
    const size_t for_live = room.size() - holds;
    size_t at = 0;
    const size_t free_count = free_ids.size();
    auto place = [&](Material&& m) {
        Placed p;
        p.reused = at >= free_count;
        p.cell = room[at++];
        p.m = std::move(m);
        a.placed.push_back(std::move(p));
    };
    for (size_t i = 0; i < live.size(); ++i) {
        if (i >= for_live) {
            char b[256];
            std::snprintf(b, sizeof b, "the game has room for %zu new building materials across all mods%s", room.size(),
                          kept.empty() ? "" : (", " + std::to_string(holds) + " of them kept for absent mods (WorldTables.Forget frees one)").c_str());
            a.refused.push_back("block buildingMaterial " + live[i].block + " (mod " + live[i].mod + "): refused: " + b);
            continue;
        }
        place(std::move(live[i]));
    }
    for (size_t i = 0; i < kept.size(); ++i) {
        if (at >= room.size()) {
            a.refused.push_back("kept material " + kept[i].guid.text() + " (" + kept[i].block + ", mod " + kept[i].mod + "): no room left: a world's cells of it are lost when it loads");
            continue;
        }
        place(std::move(kept[i]));
    }
    return a;
}

uint32_t loot_rate(uint32_t makes, uint32_t count) {
    if (!count) return 1;
    return std::max<uint32_t>(1, (makes + count / 2) / count);
}

std::string handshake_hash(const std::vector<Placed>& placed) {
    uint64_t h = 0xcbf29ce484222325ull;
    bool any = false;
    auto add = [&](const void* p, size_t n) {
        const auto* b = static_cast<const uint8_t*>(p);
        for (size_t i = 0; i < n; ++i) h = (h ^ b[i]) * 0x100000001b3ull;
    };
    for (const Placed& p : placed) {
        if (!p.m.live) continue;
        any = true;
        const uint8_t id = static_cast<uint8_t>(p.cell);
        add(&id, 1);
        add(p.m.guid.b.data(), 16);
    }
    if (!any) return kEmptyHash;
    char s[17];
    std::snprintf(s, sizeof s, "%016llx", static_cast<unsigned long long>(h));
    return s;
}

// ---- reading -----------------------------------------------------------------------------------------------------
bool array_at(const Bytes& res, uint32_t field, uint32_t stride, ArrayView* out) {
    if (static_cast<uint64_t>(field) + 8 > res.size()) return false;
    const int32_t off = rd<int32_t>(res.data() + field);
    const uint32_t count = rd<uint32_t>(res.data() + field + 4);
    out->count = count;
    if (!count) {
        out->base = nullptr;
        return true;
    }
    const int64_t at = int64_t{field} + off;
    if (off == 0 || at < 0 || static_cast<uint64_t>(at) + uint64_t{count} * stride > res.size()) return false;
    out->base = res.data() + at;
    return true;
}

bool read_registry(const Bytes& res, const Layouts& L, RegistryInfo* out, std::string* why) {
    ArrayView t, b;
    if (res.size() < L.reg_size || !array_at(res, L.reg_terrain, L.terrain.stride, &t) || !array_at(res, L.reg_building, L.building.stride, &b)) {
        *why = "the terraforming registry doesn't read as its layout says";
        return false;
    }
    for (uint32_t i = 0; i < t.count; ++i) {
        const uint8_t* c = t.base + size_t{i} * L.terrain.stride;
        out->terrain_at[c[L.terrain.id]] = static_cast<uint32_t>(c - res.data());
    }
    for (uint32_t i = 0; i < b.count; ++i) {
        const uint8_t* c = b.base + size_t{i} * L.building.stride;
        out->building_at[128 + c[L.building.id]] = static_cast<uint32_t>(c - res.data());
    }
    if (out->building_at.empty()) {
        *why = "the terraforming registry has no building material";
        return false;
    }
    return true;
}

bool world_has_table(const Bytes& world, const Layouts& L) {
    if (world.size() < L.world_size || world.size() < size_t{L.world_guids} + 256 * 16) return false;
    for (size_t i = 0; i < 256 * 16; ++i)
        if (world[L.world_guids + i]) return true;
    return false;
}

std::vector<int> free_building_ids(const Bytes& world, const Layouts& L) {
    std::vector<int> out;
    for (int id = 128; id < 256; ++id) {
        bool any = false;
        for (int i = 0; i < 16; ++i) any = any || world[L.world_guids + size_t(id) * 16 + i];
        if (!any) out.push_back(id);
    }
    return out;
}

std::vector<int> deprecated_ids(const Bytes& reg, const RegistryInfo& info, const Layouts& L) {
    std::vector<int> out;
    for (const auto& [cell, at] : info.building_at) {
        bool any = false;
        for (int i = 0; i < 16; ++i) any = any || reg[at + L.building.item + i];
        if (!any) out.push_back(cell);
    }
    return out;
}

// ---- writing -----------------------------------------------------------------------------------------------------
namespace {

std::string hardness_text(const Layouts& L, int v) { return v >= 0 && static_cast<size_t>(v) < L.hardness_names.size() ? L.hardness_names[v] : std::to_string(v); }
std::string class_text(const Layouts& L, int v) { return v >= 0 && static_cast<size_t>(v) < L.class_names.size() ? L.class_names[v] : std::to_string(v); }

// Writes a strength into one config. Returns what changed, for the log; empty: nothing did.
std::string apply_strength(uint8_t* c, const ConfigLayout& K, const Layouts& L, const Strength& s) {
    std::string out;
    auto note = [&](const std::string& t) { out += (out.empty() ? "" : ", ") + t; };
    if (s.hardness && c[K.hardness] != *s.hardness) {
        note("hardness " + hardness_text(L, c[K.hardness]) + " -> " + hardness_text(L, *s.hardness));
        c[K.hardness] = static_cast<uint8_t>(*s.hardness);
    }
    if (s.health && rd<int32_t>(c + K.health) != *s.health) {
        note("health " + std::to_string(rd<int32_t>(c + K.health)) + " -> " + std::to_string(*s.health));
        wr<int32_t>(c + K.health, *s.health);
    }
    if (s.breaks && c[K.klass] != *s.breaks) {
        note("breaks " + class_text(L, c[K.klass]) + " -> " + class_text(L, *s.breaks));
        c[K.klass] = static_cast<uint8_t>(*s.breaks);
    }
    if (s.items_per_blocks && rd<uint32_t>(c + K.rate) != *s.items_per_blocks) {
        note("itemsPerBlocks " + std::to_string(rd<uint32_t>(c + K.rate)) + " -> " + std::to_string(*s.items_per_blocks));
        wr<uint32_t>(c + K.rate, *s.items_per_blocks);
    }
    if (s.drops) {
        Guid g;
        if (Guid::parse(*s.drops, &g) && std::memcmp(c + K.item, g.b.data(), 16) != 0) {
            note("drops " + *s.drops);
            std::memcpy(c + K.item, g.b.data(), 16);
        }
    }
    return out;
}

// The config a new material starts from: the game's plainest of that class. A material's feel (sounds, friction, footsteps, what an
// NPC's path thinks of it) stays this one's until the block says otherwise.
const uint8_t* template_config(const Bytes& reg, const RegistryInfo& info, const Layouts& L, int klass, bool roof) {
    const ConfigLayout& K = L.building;
    const uint8_t* fallback = nullptr;
    for (const auto& [cell, at] : info.building_at) {
        const uint8_t* c = reg.data() + at;
        bool has_item = false;
        for (int i = 0; i < 16; ++i) has_item = has_item || c[K.item + i];
        if (!has_item || (K.roof != UINT32_MAX && (c[K.roof] != 0) != roof)) continue;
        if (!fallback) fallback = c;
        if (c[K.klass] == klass) return c;
    }
    return fallback ? fallback : reg.data() + info.building_at.begin()->second;
}

}  // namespace

bool merge_registry(const Bytes& game, const Layouts& L, const std::vector<Tweak>& tweaks, const std::vector<Placed>& placed, Bytes* out,
                    std::vector<RegistryChange>* changes, std::string* why) {
    RegistryInfo info;
    if (!read_registry(game, L, &info, why)) return false;
    *out = game;
    const ConfigLayout& B = L.building;
    const ConfigLayout& T = L.terrain;

    for (const Tweak& t : tweaks) {
        if (t.cell < 0 || !t.strength.any()) continue;
        const bool building = t.cell >= 128;
        const auto& map = building ? info.building_at : info.terrain_at;
        const auto it = map.find(t.cell);
        if (it == map.end()) {
            changes->push_back({"tweak " + t.block + " (mod " + t.mod + "): " + t.material + " has no config: nothing changed"});
            continue;
        }
        const std::string text = apply_strength(out->data() + it->second, building ? B : T, L, t.strength);
        changes->push_back({"tweak " + t.block + " (mod " + t.mod + "): " + t.material + " (" + std::to_string(t.cell) + "): " + (text.empty() ? "already so" : text)});
    }

    // New configs. A deprecated slot's config is replaced where it is; the rest make the array grow.
    std::vector<Bytes> added;
    for (const Placed& p : placed) {
        const int klass = p.m.strength.breaks.value_or(2);
        const uint8_t* tpl = template_config(game, info, L, klass, p.m.roof);
        Bytes c(tpl, tpl + B.stride);
        c[B.id] = static_cast<uint8_t>(p.cell - 128);
        std::memset(c.data() + B.conditional, 0, 8);
        if (B.roof != UINT32_MAX) c[B.roof] = p.m.roof ? 1 : 0;
        apply_strength(c.data(), B, L, p.m.strength);
        Guid loot;
        std::memcpy(loot.b.data(), c.data() + B.item, 16);
        changes->push_back({"material " + std::to_string(p.cell) + " " + p.m.guid.text() + " " + p.m.block + " (mod " + p.m.mod + ", " + (p.m.live ? "live" : "kept") + (p.reused ? ", reusing a deprecated slot" : "") +
                            "): hardness " + hardness_text(L, c[B.hardness]) + ", health " + std::to_string(rd<int32_t>(c.data() + B.health)) + ", breaks " + class_text(L, c[B.klass]) +
                            ", drops " + loot.text() + " per " + std::to_string(rd<uint32_t>(c.data() + B.rate)) + " blocks"});
        const auto at = info.building_at.find(p.cell);
        if (at != info.building_at.end()) std::memcpy(out->data() + at->second, c.data(), c.size());
        else added.push_back(std::move(c));
    }
    if (added.empty()) return true;

    ArrayView old;
    if (!array_at(*out, L.reg_building, B.stride, &old)) {
        *why = "the terraforming registry's building list doesn't read";
        return false;
    }
    const uint32_t old_count = old.count;
    const size_t old_at = static_cast<size_t>(old.base - out->data());
    pad4(out);
    const size_t at = out->size();
    out->resize(at + (size_t{old_count} + added.size()) * B.stride);
    std::memcpy(out->data() + at, out->data() + old_at, size_t{old_count} * B.stride);
    for (uint32_t i = 0; i < old_count; ++i) {
        const size_t field_old = old_at + size_t{i} * B.stride + B.conditional;
        const size_t field_new = at + size_t{i} * B.stride + B.conditional;
        const int32_t off = rd<int32_t>(out->data() + field_old);
        if (off) wr<int32_t>(out->data() + field_new, static_cast<int32_t>(int64_t(field_old) + off - int64_t(field_new)));
    }
    for (size_t i = 0; i < added.size(); ++i) std::memcpy(out->data() + at + (old_count + i) * B.stride, added[i].data(), B.stride);
    wr<int32_t>(out->data() + L.reg_building, static_cast<int32_t>(at - L.reg_building));
    wr<uint32_t>(out->data() + L.reg_building + 4, static_cast<uint32_t>(old_count + added.size()));
    return true;
}

bool merge_world(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::string* why) {
    if (game.size() < size_t{L.world_guids} + 256 * 16) {
        *why = "a world resource is smaller than its material table";
        return false;
    }
    *out = game;
    for (const Placed& p : placed) std::memcpy(out->data() + L.world_guids + size_t(p.cell) * 16, p.m.guid.b.data(), 16);
    return true;
}

// ---- the build menu's pools ------------------------------------------------------------------------------------------
std::string pool_of(const Material& m) {
    if (!m.item) return "";
    if (m.terrain) return kTerrainPool;
    return m.roof ? kRoofPool : kWallPool;
}

bool pool_counts(const Bytes& game, const Layouts& L, std::map<std::string, uint32_t>* out, std::string* why) {
    ArrayView pools;
    if (!array_at(game, L.pools, L.pool_stride, &pools)) {
        *why = "the material pools don't read as their layout says";
        return false;
    }
    out->clear();
    for (uint32_t i = 0; i < pools.count; ++i) {
        const size_t rec = static_cast<size_t>(pools.base - game.data()) + size_t{i} * L.pool_stride;
        const int32_t off = rd<int32_t>(game.data() + rec + L.pool_name);
        const uint32_t len = rd<uint32_t>(game.data() + rec + L.pool_name + 4);
        const int64_t at = int64_t(rec) + L.pool_name + off;
        ArrayView items;
        if (!off || at <= 0 || static_cast<uint64_t>(at) + len > game.size() || !array_at(game, static_cast<uint32_t>(rec + L.pool_materials), 4, &items)) {
            *why = "a material pool's name or list doesn't read";
            return false;
        }
        (*out)[std::string(reinterpret_cast<const char*>(game.data() + at), len)] = items.count;
    }
    return true;
}

std::string pool_room_text(const std::string& pool, uint32_t game_count) {
    const uint32_t room = game_count < kPoolCapacity ? kPoolCapacity - game_count : 0;
    const std::string cap = std::to_string(kPoolCapacity), have = std::to_string(game_count), fit = std::to_string(room);
    if (pool == kTerrainPool)
        return "the game's terrain shapes list at most " + cap + " placeable terrain materials and have " + have + " of their own: " + fit + " new ones fit across all mods";
    return "the game's build menu lists at most " + cap + (pool == kRoofPool ? " roof" : " wall") + " materials and has " + have + " of its own: " + fit + " new ones fit across all mods";
}

void fit_pools(std::vector<Material>* live, const std::map<std::string, uint32_t>& game_counts, const std::set<Guid>& remembered, std::vector<Material>* held,
               std::vector<std::string>* refused) {
    std::vector<size_t> order(live->size());
    for (size_t i = 0; i < order.size(); ++i) order[i] = i;
    std::sort(order.begin(), order.end(), [&](size_t x, size_t y) { return (*live)[x].guid.text() < (*live)[y].guid.text(); });
    std::map<std::string, uint32_t> used;
    std::vector<bool> out(live->size(), false);
    for (const size_t i : order) {
        const Material& m = (*live)[i];
        const std::string pool = pool_of(m);
        if (pool.empty()) continue;
        const auto it = game_counts.find(pool);
        const uint32_t have = it == game_counts.end() ? 0 : it->second;
        if (have < kPoolCapacity && used[pool] < kPoolCapacity - have) {
            ++used[pool];
            continue;
        }
        out[i] = true;
        std::string text = std::string("block ") + (m.terrain ? "terrainMaterial " : "buildingMaterial ") + m.block + " (mod " + m.mod + "): refused: " + pool_room_text(pool, have);
        if (remembered.count(m.guid)) {
            text += ". A world has cells of it: it's kept as an absent mod's material is, so they stay";
            Material h = m;
            h.live = false;
            h.item = false;
            held->push_back(std::move(h));
        }
        refused->push_back(std::move(text));
    }
    std::vector<Material> keep;
    for (size_t i = 0; i < live->size(); ++i)
        if (!out[i]) keep.push_back(std::move((*live)[i]));
    *live = std::move(keep);
}

bool merge_pools(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, Bytes* out, std::string* why) {
    ArrayView pools;
    if (!array_at(game, L.pools, L.pool_stride, &pools)) {
        *why = "the material pools don't read as their layout says";
        return false;
    }
    *out = game;
    const auto pool_named = [&](const std::string& want) -> size_t {
        for (uint32_t i = 0; i < pools.count; ++i) {
            const size_t rec = static_cast<size_t>(pools.base - game.data()) + size_t{i} * L.pool_stride;
            const int32_t off = rd<int32_t>(game.data() + rec + L.pool_name);
            const uint32_t len = rd<uint32_t>(game.data() + rec + L.pool_name + 4);
            const int64_t at = int64_t(rec) + L.pool_name + off;
            if (off && at > 0 && static_cast<uint64_t>(at) + len <= game.size() && std::string(reinterpret_cast<const char*>(game.data() + at), len) == want) return rec;
        }
        return SIZE_MAX;
    };
    for (const bool roof : {false, true}) {
        std::vector<uint32_t> ids;
        for (const Placed& p : placed)
            if (p.m.live && p.m.item && p.m.roof == roof) ids.push_back(p.m.item_id);
        if (ids.empty()) continue;
        const size_t rec = pool_named(roof ? "BuildingRoofMaterials" : "BuildingDefaultMaterials");
        if (rec == SIZE_MAX) {
            *why = std::string("the game has no material pool ") + (roof ? "BuildingRoofMaterials" : "BuildingDefaultMaterials");
            return false;
        }
        const size_t field = rec + L.pool_materials;
        ArrayView cur;
        if (!array_at(*out, static_cast<uint32_t>(field), 4, &cur)) {
            *why = "a material pool's list doesn't read";
            return false;
        }
        const uint32_t count = cur.count;
        if (size_t{count} + ids.size() > kPoolCapacity) {
            *why = std::string("the material pool ") + (roof ? kRoofPool : kWallPool) + " would list " + std::to_string(size_t{count} + ids.size()) + " items, past the " +
                   std::to_string(kPoolCapacity) + " a player's game holds";
            return false;
        }
        const size_t cur_at = cur.base ? static_cast<size_t>(cur.base - out->data()) : 0;
        pad4(out);
        const size_t at = out->size();
        out->resize(at + (size_t{count} + ids.size()) * 4);
        if (count) std::memcpy(out->data() + at, out->data() + cur_at, size_t{count} * 4);
        for (size_t i = 0; i < ids.size(); ++i) wr<uint32_t>(out->data() + at + (count + i) * 4, ids[i]);
        wr<int32_t>(out->data() + field, static_cast<int32_t>(at - field));
        wr<uint32_t>(out->data() + field + 4, static_cast<uint32_t>(count + ids.size()));
    }
    return true;
}

uint32_t first_pool_item(const Bytes& pools, const Layouts& L) {
    ArrayView v;
    if (!array_at(pools, L.pools, L.pool_stride, &v) || !v.count) return 0;
    const size_t rec = static_cast<size_t>(v.base - pools.data());
    ArrayView items;
    if (!array_at(pools, static_cast<uint32_t>(rec + L.pool_materials), 4, &items) || !items.count) return 0;
    return rd<uint32_t>(items.base);
}

bool merge_blueprints(const Bytes& game, const Layouts& L, const std::vector<Placed>& placed, uint32_t template_item, Bytes* out, std::string* why) {
    ArrayView v;
    if (!array_at(game, L.blueprints, L.bp_stride, &v)) {
        *why = "the blueprint items don't read as their layout says";
        return false;
    }
    *out = game;
    std::vector<uint32_t> ids;
    for (const Placed& p : placed)
        if (p.m.live && p.m.item) ids.push_back(p.m.item_id);
    if (ids.empty()) return true;
    const size_t old_at = static_cast<size_t>(v.base - game.data());
    size_t tpl = SIZE_MAX;
    for (uint32_t i = 0; i < v.count; ++i)
        if (rd<uint32_t>(game.data() + old_at + size_t{i} * L.bp_stride + L.bp_item) == template_item) tpl = i;
    if (tpl == SIZE_MAX) {
        *why = "the first pool item has no blueprint entry to start from";
        return false;
    }
    pad4(out);
    const size_t at = out->size();
    const size_t total = size_t{v.count} + ids.size();
    out->resize(at + total * L.bp_stride);
    auto copy = [&](size_t from_index, size_t to_index) {
        const size_t from = old_at + from_index * L.bp_stride, to = at + to_index * L.bp_stride;
        std::memcpy(out->data() + to, out->data() + from, L.bp_stride);
        const int32_t off = rd<int32_t>(out->data() + from + L.bp_data);
        if (off) wr<int32_t>(out->data() + to + L.bp_data, static_cast<int32_t>(int64_t(from) + off - int64_t(to)));
    };
    for (uint32_t i = 0; i < v.count; ++i) copy(i, i);
    for (size_t i = 0; i < ids.size(); ++i) {
        copy(tpl, v.count + i);
        wr<uint32_t>(out->data() + at + (v.count + i) * L.bp_stride + L.bp_item, ids[i]);
    }
    wr<int32_t>(out->data() + L.blueprints, static_cast<int32_t>(at - L.blueprints));
    wr<uint32_t>(out->data() + L.blueprints + 4, static_cast<uint32_t>(total));
    return true;
}

// ---- names -------------------------------------------------------------------------------------------------------
std::string material_name(const std::string& item_debug_name) {
    std::string s = item_debug_name;
    while (!s.empty() && s[0] == '_') s.erase(0, 1);
    std::string prefix;
    const std::string terrain = "Build_TerrainMaterial_", block = "Block_";
    if (s.rfind(terrain, 0) == 0) {
        s.erase(0, terrain.size());
        prefix = "Terrain";
    } else if (s.rfind(block, 0) == 0) {
        s.erase(0, block.size());
    } else {
        return "";
    }
    std::string out = prefix;
    size_t at = 0;
    while (at <= s.size()) {
        const size_t end = std::min(s.find('_', at), s.size());
        std::string tok = s.substr(at, end - at);
        at = end + 1;
        const bool tier = tok.size() >= 2 && tok[0] == 'T' && std::all_of(tok.begin() + 1, tok.end(), [](unsigned char c) { return std::isdigit(c); });
        std::string up = tok;
        std::transform(up.begin(), up.end(), up.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
        if (tok.empty() || tier || up == "CRAFTING" || up == "REWARD") continue;
        tok[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(tok[0])));
        out += tok;
    }
    return out;
}

bool base64_decode(const std::string& text, Bytes* out) {
    out->clear();
    uint32_t acc = 0;
    int bits = 0;
    for (const char c : text) {
        int v;
        if (c >= 'A' && c <= 'Z') v = c - 'A';
        else if (c >= 'a' && c <= 'z') v = c - 'a' + 26;
        else if (c >= '0' && c <= '9') v = c - '0' + 52;
        else if (c == '+') v = 62;
        else if (c == '/') v = 63;
        else if (c == '=') break;
        else return false;
        acc = acc << 6 | static_cast<uint32_t>(v);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out->push_back(static_cast<uint8_t>(acc >> bits));
        }
    }
    return true;
}

bool merge_shapes(const Bytes& table, const Layouts& L, const std::vector<Shape>& shapes, Bytes* out, std::vector<std::string>* refused, std::string* why) {
    ArrayView v;
    if (!array_at(table, L.blueprints, L.bp_stride, &v)) {
        *why = "the blueprint items don't read as their layout says";
        return false;
    }
    *out = table;
    std::set<uint32_t> have;
    const size_t old_at = v.base ? static_cast<size_t>(v.base - table.data()) : 0;
    for (uint32_t i = 0; i < v.count; ++i) have.insert(rd<uint32_t>(table.data() + old_at + size_t{i} * L.bp_stride + L.bp_item));
    std::vector<const Shape*> in;
    for (const Shape& s : shapes) {
        if (!have.insert(s.item_id).second) {
            refused->push_back("block blueprint " + s.block + " (mod " + s.mod + "): its item's id is taken in the shapes table: two shapes with one name?");
            continue;
        }
        in.push_back(&s);
    }
    if (in.empty()) return true;
    // the entries: the table's, then the shapes'
    pad4(out);
    const size_t at = out->size();
    const size_t total = size_t{v.count} + in.size();
    out->resize(at + total * L.bp_stride);
    for (uint32_t i = 0; i < v.count; ++i) {
        const size_t from = old_at + size_t{i} * L.bp_stride, to = at + size_t{i} * L.bp_stride;
        std::memcpy(out->data() + to, out->data() + from, L.bp_stride);
        const int32_t off = rd<int32_t>(out->data() + from + L.bp_data);
        if (off) wr<int32_t>(out->data() + to + L.bp_data, static_cast<int32_t>(int64_t(from) + off - int64_t(to)));
    }
    // each shape's cells after the entries
    for (size_t k = 0; k < in.size(); ++k) {
        const Shape& s = *in[k];
        const size_t cells_at = out->size();
        out->insert(out->end(), s.bits.begin(), s.bits.end());
        const size_t e = at + (v.count + k) * L.bp_stride;
        std::memset(out->data() + e, 0, L.bp_stride);
        wr<uint32_t>(out->data() + e + L.bp_item, s.item_id);
        wr<uint32_t>(out->data() + e + L.bp_size, s.x);
        wr<uint32_t>(out->data() + e + L.bp_size + 4, s.y);
        wr<uint32_t>(out->data() + e + L.bp_size + 8, s.z);
        wr<int32_t>(out->data() + e + L.bp_data, static_cast<int32_t>(int64_t(cells_at) - int64_t(e + L.bp_data)));
        wr<uint32_t>(out->data() + e + L.bp_data + 4, static_cast<uint32_t>(s.bits.size()));
        (*out)[e + L.bp_compressed] = 1;
        pad4(out);
    }
    wr<int32_t>(out->data() + L.blueprints, static_cast<int32_t>(at - L.blueprints));
    wr<uint32_t>(out->data() + L.blueprints + 4, static_cast<uint32_t>(total));
    return true;
}

Bytes voxel_model(const Shape& s, const ModelLayout& M) {
    Bytes b(M.size, 0);
    wr<uint32_t>(b.data() + M.size_field, s.x);
    wr<uint32_t>(b.data() + M.size_field + 4, s.y);
    wr<uint32_t>(b.data() + M.size_field + 8, s.z);
    b[M.terrain] = 0;
    pad4(&b);
    const size_t at = b.size();
    const uint32_t n = s.x * s.y * s.z;
    b.resize(at + n, 0);
    for (uint32_t i = 0; i < n; ++i)
        if ((s.bits[i / 8] >> (i % 8)) & 1) b[at + i] = s.roof ? kRoofCell : kFullCell;
    wr<int32_t>(b.data() + M.data, static_cast<int32_t>(at - M.data));
    wr<uint32_t>(b.data() + M.data + 4, n);
    return b;
}

}  // namespace kwt
