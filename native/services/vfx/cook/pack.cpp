// SPDX-License-Identifier: MIT
// A set of mods -> everything to serve. One mod's mistakes stay its own: a wrong block is dropped by name, a mod over the budget is refused
// whole and says which limit, and none of it can reach the game's fatal paths.
#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <sstream>

#include "../shaders/kiln_vfx_params.h"
#include "vfxcook.h"

namespace kvb {
namespace {

namespace fs = std::filesystem;

fs::path to_path(const std::string& utf8) { return fs::path(std::u8string(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size())); }

bool host_read(const std::string& path, Bytes* out, std::string* why) {
    std::ifstream f(to_path(path), std::ios::binary);
    if (!f) {
        *why = "can't open it";
        return false;
    }
    out->assign(std::istreambuf_iterator<char>(f), {});
    return true;
}

// Relative, forward or back slashes, no "..", inside the mod's folder: the rule kiln.assets has for manifests.
bool safe_rel(const std::string& rel) {
    if (rel.empty() || rel[0] == '/' || rel[0] == '\\' || rel.find(':') != std::string::npos) return false;
    std::string part;
    for (size_t i = 0; i <= rel.size(); ++i) {
        const char c = i < rel.size() ? rel[i] : '/';
        if (c == '/' || c == '\\') {
            if (part == "..") return false;
            part.clear();
        } else {
            part += c;
        }
    }
    return true;
}

uint64_t fnv64(const std::string& s) {
    uint64_t h = 0xcbf29ce484222325ull;
    for (unsigned char c : s) h = (h ^ c) * 0x100000001b3ull;
    return h;
}

struct Shared {
    std::set<std::string> blobs;  // content hash text
    std::set<Guid> resources;
    bool modules = false;
};

void add_blob(CookResult& r, Shared& sh, const ContentHash& h, const uint8_t* data, size_t size, const std::string& what) {
    if (!sh.blobs.insert(content_hash_text(h)).second) return;
    r.blobs.push_back({h, Bytes(data, data + size), what});
}
void add_resource(CookResult& r, Shared& sh, const Guid& g, uint32_t type, Bytes data, const std::string& what) {
    if (!sh.resources.insert(g).second) return;
    r.resources.push_back({g, type, std::move(data), what});
}

}  // namespace

ojson parse_relaxed(const std::string& text) {
    // The vendored JSON parser takes comments but not trailing commas, so those go first. Strings and comments are copied untouched.
    std::string s;
    s.reserve(text.size());
    size_t last_comma = std::string::npos;  // where a comma was written, if only whitespace or comments have followed
    for (size_t i = 0; i < text.size(); ++i) {
        const char c = text[i];
        if (c == '"') {
            last_comma = std::string::npos;
            s += c;
            for (++i; i < text.size(); ++i) {
                s += text[i];
                if (text[i] == '\\' && i + 1 < text.size()) s += text[++i];
                else if (text[i] == '"') break;
            }
        } else if (c == '/' && i + 1 < text.size() && text[i + 1] == '/') {
            for (; i < text.size() && text[i] != '\n'; ++i) s += text[i];
            if (i < text.size()) s += '\n';
        } else if (c == '/' && i + 1 < text.size() && text[i + 1] == '*') {
            const size_t end = text.find("*/", i + 2);
            const size_t stop = end == std::string::npos ? text.size() : end + 2;
            s.append(text, i, stop - i);
            i = stop - 1;
        } else if (c == '}' || c == ']') {
            if (last_comma != std::string::npos) s[last_comma] = ' ';
            last_comma = std::string::npos;
            s += c;
        } else if (c == ',') {
            last_comma = s.size();
            s += c;
        } else {
            if (!std::isspace(static_cast<unsigned char>(c))) last_comma = std::string::npos;
            s += c;
        }
    }
    return ojson::parse(s, nullptr, false, /*ignore_comments=*/true);
}

CookResult cook_mods(const std::vector<ModInput>& mods, const CookOptions& opt) {
    CookResult out;
    out.game = opt.game;
    out.capacity = opt.capacity;
    const ReadFile read = opt.read_file ? opt.read_file : ReadFile(host_read);
    Budget budget(opt.game, opt.capacity);

    // A bad module is a fatal compile in the game: refuse everything before it can get there.
    for (Module m : {Module::Emitter, Module::Sprite, Module::Light}) {
        const ModuleInfo& info = starter_module(m);
        const std::string bad = check_module(info.data, info.size);
        if (!bad.empty()) {
            out.errors.push_back(std::string("starter module ") + info.name + ": " + bad + ": no Kiln VFX this session");
            return out;
        }
    }

    Shared shared;
    std::map<std::string, BuiltMesh> meshes;  // built-ins used so far
    std::set<std::string> seen_mods;
    std::set<Guid> counted_meshes;

    for (const ModInput& mod : mods) {
        if (!seen_mods.insert(mod.id).second) {
            out.warnings.push_back("mod " + mod.id + ": listed twice, the second is ignored");
            continue;
        }
        Diagnostics diag;
        struct Block {
            VfxDef def;
            uint64_t definition = 0;
        };
        std::vector<Block> blocks;
        std::set<std::string> names;
        std::set<Guid> ids;
        for (const auto& [file, text] : mod.documents) {
            const ojson doc = parse_relaxed(text);
            if (doc.is_discarded() || !doc.is_object()) {
                diag.errors.push_back(mod.id + ": " + file + ": not valid JSON");
                continue;
            }
            const auto sec = doc.find("vfx");
            if (sec == doc.end()) continue;
            const auto fmt = doc.find("format");
            if (fmt != doc.end() && (!fmt->is_number_integer() || fmt->get<int>() != 1)) {
                diag.errors.push_back(mod.id + ": " + file + ": format " + fmt->dump() + ": this Kiln reads format 1");
                continue;
            }
            if (!sec->is_object()) {
                diag.errors.push_back(mod.id + ": " + file + ": vfx: expected an object of blocks by name");
                continue;
            }
            for (auto it = sec->begin(); it != sec->end(); ++it) {
                const std::string where = mod.id + ": " + file + ": vfx." + it.key();
                Block b;
                if (!parse_block(it.value(), it.key(), where, &b.def, &diag)) continue;
                if (!names.insert(it.key()).second) {
                    // The same definition coming again (a pending copy of a block in blocks/) is fine; another is a mistake.
                    diag.warnings.push_back(where + ": a block of this name is already defined; the first stays");
                    continue;
                }
                if (!ids.insert(block_id(mod.id, "vfx", b.def.id_name)).second) {
                    diag.errors.push_back(where + ": \"was\" names the id of another block");
                    continue;
                }
                b.definition = fnv64(it.value().dump());
                blocks.push_back(std::move(b));
            }
        }
        if (!blocks.empty() || !diag.errors.empty()) ++out.mods_with_vfx;

        auto finish = [&] {
            for (const std::string& e : diag.errors) out.errors.push_back(e);
            for (const std::string& w : diag.warnings) out.warnings.push_back(w);
        };
        if (blocks.empty()) {
            finish();
            continue;
        }
        // A manifest rule (lint KL208 says it first): a new VFX has to be on every client that may see it.
        if (mod.side == "server") {
            diag.warnings.push_back(mod.id + ": side \"server\": clients never load this mod, so its " + std::to_string(blocks.size()) + " VFX block(s) are skipped");
            finish();
            continue;
        }
        if (mod.side != "client" && mod.clients != "mod") {
            out.refused_mods.push_back(mod.id + ": a new VFX needs \"clients\": \"mod\" (it has \"" + mod.clients + "\"): players without the mod could be sent an effect their game aborts on");
            finish();
            continue;
        }

        // Textures, meshes and the VFX of this mod, kept aside until the budget says yes.
        std::map<std::string, BuiltTexture> textures;
        std::vector<BuiltBlock> built_blocks;
        std::vector<std::pair<Guid, Bytes>> built_vfx;
        std::vector<std::string> vfx_names;
        BudgetNeed need;
        std::vector<std::string> used_meshes, used_textures;
        for (const Block& b : blocks) {
            bool bad = false;
            if (b.def.game_sprite && !(opt.game_has_blob && opt.game_has_blob(kGameSpriteModule))) {
                // A pipeline whose blob the game can't find is fatal, so a game update that removes this module must only lose the diagnostic.
                diag.errors.push_back(b.def.where + ".gameSpriteModule: the game's sprite module is not in this game: this block is skipped");
                continue;
            }
            for (size_t i = 0; i < b.def.particles.size() && !bad; ++i) {
                const ParticleDef& p = b.def.particles[i];
                if (p.texture.empty()) continue;
                const std::string key = p.texture + (p.texture_linear ? "#linear" : "");
                if (textures.count(key)) continue;
                std::string why;
                Bytes png;
                const std::string path = mod.dir + "/" + p.texture;
                if (!safe_rel(p.texture)) why = "must be relative, inside the mod's folder";
                else if (!read(path, &png, &why)) why = "can't read it (" + why + ")";
                BuiltTexture t;
                if (why.empty()) build_texture(png, mod.id, p.texture, p.texture_linear, opt.cache, &t, &why);
                if (!why.empty()) {
                    diag.errors.push_back(b.def.where + ".particles[" + std::to_string(i) + "].texture: " + p.texture + ": " + why);
                    bad = true;
                    break;
                }
                textures[key] = std::move(t);
            }
            if (bad) continue;
            // Only a size that can't divide into the grid is detectable: nothing in a PNG says whether it holds frames at all.
            for (size_t i = 0; i < b.def.particles.size(); ++i) {
                const ParticleDef& p = b.def.particles[i];
                if (p.texture.empty() || !p.flip_columns) continue;
                const BuiltTexture& t = textures.at(p.texture + (p.texture_linear ? "#linear" : ""));
                if (t.width % p.flip_columns || t.height % p.flip_rows)
                    diag.warnings.push_back(b.def.where + ".particles[" + std::to_string(i) + "].flipbook: " + std::to_string(t.width) + "x" + std::to_string(t.height) + " doesn't divide into " +
                                            std::to_string(p.flip_columns) + " columns and " + std::to_string(p.flip_rows) + " rows: frames will be cut");
            }
            for (const ParticleDef& p : b.def.particles)
                if (!meshes.count(p.mesh)) {
                    BuiltMesh m;
                    if (builtin_mesh(p.mesh, &m)) meshes[p.mesh] = std::move(m);
                }
            BuiltVfx v;
            std::string why;
            const auto mesh_of = [&](const ParticleDef& p) { return meshes.at(p.mesh).guid; };
            const auto tex_of = [&](const ParticleDef& p) { return textures.at(p.texture + (p.texture_linear ? "#linear" : "")).guid; };
            if (!build_vfx(b.def, mod.id, mesh_of, tex_of, &v, &why)) {
                diag.errors.push_back(b.def.where + ": " + why);
                continue;
            }
            ++need.vfx;
            for (const ParticleDef& p : b.def.particles) {
                const BuiltMesh& m = meshes.at(p.mesh);
                if (need.meshes.insert(m.guid).second) used_meshes.push_back(p.mesh);
            }
            if (!b.def.sound.empty()) need.sounds.insert(b.def.sound);
            BuiltBlock info;
            info.mod = mod.id;
            info.name = b.def.name;
            info.guid = v.guid;
            info.entries = v.entries;
            info.emitters = v.emitters;
            info.kill_time = v.kill_time;
            info.stop_after = v.stop_after;
            info.loops = v.loops;
            info.definition = b.definition;
            built_blocks.push_back(info);
            built_vfx.emplace_back(v.guid, std::move(v.resource));
            vfx_names.push_back(mod.id + ":" + b.def.name);
        }
        if (built_vfx.empty()) {
            finish();
            continue;
        }
        for (const std::string& name : used_meshes) {
            const BuiltMesh& m = meshes.at(name);
            if (!counted_meshes.count(m.guid)) need.mesh_bytes += m.blob.size();
        }
        std::string over;
        if (!budget.try_add(need, &over)) {
            out.refused_mods.push_back(mod.id + ": over the game's limits, none of its " + std::to_string(built_vfx.size()) + " VFX are registered (" + over + ")");
            finish();
            continue;
        }
        for (const std::string& name : used_meshes) counted_meshes.insert(meshes.at(name).guid);

        // Yes: serve it, blobs before the resources that name them.
        if (!shared.modules) {
            shared.modules = true;
            for (Module m : {Module::Emitter, Module::Sprite, Module::Light}) {
                const ModuleInfo& info = starter_module(m);
                add_blob(out, shared, info.hash, info.data, info.size, info.name);
            }
        }
        for (const std::string& name : used_meshes) {
            const BuiltMesh& m = meshes.at(name);
            add_blob(out, shared, m.hash, m.blob.data(), m.blob.size(), "kiln mesh " + name);
            add_resource(out, shared, m.guid, kTypeMesh, m.resource, "kiln mesh " + name);
        }
        for (auto& [key, t] : textures) {
            add_blob(out, shared, t.hash, t.blob.data(), t.blob.size(), mod.id + " texture " + key);
            add_resource(out, shared, t.guid, kTypeTexture, std::move(t.resource), mod.id + " texture " + key);
        }
        for (size_t i = 0; i < built_vfx.size(); ++i) add_resource(out, shared, built_vfx[i].first, kTypeVfx, std::move(built_vfx[i].second), vfx_names[i]);
        for (BuiltBlock& b : built_blocks) out.blocks.push_back(std::move(b));
        finish();
    }
    out.used_vfx = budget.used_vfx();
    out.used_meshes = budget.used_meshes();
    out.used_sounds = budget.used_sounds();
    out.used_curves = budget.used_curves();
    out.used_mesh_bytes = budget.used_mesh_bytes();
    return out;
}

ojson report_json(const CookResult& r) {
    ojson j = ojson::object();
    j["ok"] = r.errors.empty() && r.refused_mods.empty();
    ojson blocks = ojson::array();
    for (const BuiltBlock& b : r.blocks) {
        ojson o = ojson::object();
        o["mod"] = b.mod;
        o["name"] = b.name;
        o["guid"] = guid_text(b.guid);
        o["entries"] = b.entries;
        o["emitters"] = b.emitters;
        o["killTime"] = b.kill_time;
        o["loops"] = b.loops;
        if (b.stop_after >= 0) o["stopAfterSeconds"] = b.stop_after;
        blocks.push_back(std::move(o));
    }
    j["blocks"] = std::move(blocks);
    j["errors"] = r.errors;
    j["warnings"] = r.warnings;
    j["refusedMods"] = r.refused_mods;
    ojson b = ojson::object();
    b["vfx"] = {{"game", r.game.vfx}, {"mods", r.used_vfx}, {"capacity", r.capacity.vfx}};
    b["meshes"] = {{"game", r.game.meshes}, {"mods", r.used_meshes}, {"capacity", r.capacity.meshes}};
    b["meshBytes"] = {{"game", r.game.mesh_bytes}, {"mods", r.used_mesh_bytes}, {"capacity", r.capacity.mesh_bytes}, {"margin", r.capacity.mesh_margin}};
    b["sounds"] = {{"game", r.game.sounds}, {"mods", r.used_sounds}, {"capacity", r.capacity.sounds}};
    b["curves"] = {{"game", r.game.curves}, {"mods", r.used_curves}, {"capacity", r.capacity.curves}};
    j["budget"] = std::move(b);
    return j;
}

}  // namespace kvb
