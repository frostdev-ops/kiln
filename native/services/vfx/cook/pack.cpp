// SPDX-License-Identifier: MIT
// A set of mods -> everything to serve. One mod's mistakes stay its own: a wrong block is dropped by name, a mod over the budget is refused
// whole and says which limit, and none of it can reach the game's fatal paths.
#include <algorithm>
#include <cctype>
#include <deque>
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
void add_resource(CookResult& r, Shared& sh, const Guid& g, uint32_t type, Bytes data, const std::string& what, bool override = false,
                  const std::string& mod = {}) {
    const bool fresh = sh.resources.insert(g).second;
    // A second replacement of the same game material is kept on standby (kiln.vfx serves it if the first one's mod unloads); anything else
    // under a GUID that is taken is dropped.
    if (!fresh && !override) return;
    r.resources.push_back({g, type, std::move(data), what, override, override ? mod : std::string(), !fresh});
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

    size_t nth = 0;
    for (const ModInput& mod : mods) {
        ++nth;
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
        struct MaterialBlock {
            MaterialDef def;
            uint64_t definition = 0;
            std::string definition_text;
        };
        std::vector<MaterialBlock> material_blocks;
        std::map<std::string, const MaterialDef*> material_named;
        std::set<Guid> material_ids;
        for (const auto& [file, text] : mod.documents) {
            const ojson doc = parse_relaxed(text);
            if (doc.is_discarded() || !doc.is_object()) {
                diag.errors.push_back(mod.id + ": " + file + ": not valid JSON");
                continue;
            }
            const auto mat = doc.find("materials");
            if (mat != doc.end() && !opt.skip_materials) {
                const auto mfmt = doc.find("format");
                if (mfmt != doc.end() && (!mfmt->is_number_integer() || mfmt->get<int>() != 1)) {
                    diag.errors.push_back(mod.id + ": " + file + ": format " + mfmt->dump() + ": this Kiln reads format 1");
                } else if (!mat->is_object()) {
                    diag.errors.push_back(mod.id + ": " + file + ": materials: expected an object of blocks by name");
                } else {
                    for (auto it = mat->begin(); it != mat->end(); ++it) {
                        const std::string where = mod.id + ": " + file + ": materials." + it.key();
                        MaterialBlock b;
                        if (!parse_material(it.value(), it.key(), where, &b.def, &diag)) continue;
                        if (b.def.replaces && opt.game_has_material && !opt.game_has_material(*b.def.replaces)) {
                            diag.errors.push_back(where + ".replaces: not a material of this game build (mod-defined materials cannot be replaced)");
                            continue;
                        }
                        if (std::any_of(material_blocks.begin(), material_blocks.end(), [&](const MaterialBlock& o) { return o.def.name == it.key(); })) {
                            diag.warnings.push_back(where + ": a block of this name is already defined; the first stays");
                            continue;
                        }
                        if (!material_ids.insert(material_guid(b.def, mod.id)).second) {
                            diag.errors.push_back(where + (b.def.replaces ? ": another block of this mod replaces the same game material" : ": \"was\" names the id of another block"));
                            continue;
                        }
                        b.definition_text = it.value().dump();
                        b.definition = fnv64(b.definition_text);
                        material_blocks.push_back(std::move(b));
                    }
                }
            }
            const auto sec = doc.find("vfx");
            if (sec == doc.end() || opt.skip_vfx) continue;
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
        if (!blocks.empty() || !material_blocks.empty() || !diag.errors.empty()) ++out.mods_with_vfx;
        for (const MaterialBlock& m : material_blocks) material_named[m.def.name] = &m.def;
        // Materials that aren't built here but that a particle may name: what a definition from code came without, or all of them when new materials are off.
        // What is wrong in them is said where they are built.
        std::deque<MaterialDef> looked_up;
        if (!blocks.empty()) {
            Diagnostics ignored;
            const auto look_in = [&](const std::string& text) {
                const ojson doc = parse_relaxed(text);
                if (doc.is_discarded() || !doc.is_object() || !doc.contains("materials") || !doc["materials"].is_object()) return;
                for (auto it = doc["materials"].begin(); it != doc["materials"].end(); ++it) {
                    MaterialDef def;
                    if (!material_named.count(it.key()) && parse_material(it.value(), it.key(), "", &def, &ignored)) {
                        looked_up.push_back(std::move(def));
                        material_named[it.key()] = &looked_up.back();
                    }
                }
            };
            for (const std::string& text : mod.lookup_documents) look_in(text);
            if (opt.skip_materials)
                for (const auto& [file, text] : mod.documents) look_in(text);
        }

        auto finish = [&] {
            for (const std::string& e : diag.errors) out.errors.push_back(e);
            for (const std::string& w : diag.warnings) out.warnings.push_back(w);
            for (const std::string& n : diag.notes) out.notes.push_back(n);
        };
        if (blocks.empty() && material_blocks.empty()) {
            finish();
            continue;
        }
        // A manifest rule (lint KL208 says it first): a new VFX or material has to be on every client that may see it.
        if (mod.side == "server") {
            diag.warnings.push_back(mod.id + ": side \"server\": clients never load this mod, so its " + std::to_string(blocks.size()) + " VFX block(s) and " + std::to_string(material_blocks.size()) + " material(s) are skipped");
            finish();
            continue;
        }
        if (mod.side != "client" && mod.clients != "mod") {
            out.refused_mods.push_back(mod.id + ": a new VFX or material needs \"clients\": \"mod\" (it has \"" + mod.clients + "\"): players without the mod could be sent an effect their game aborts on");
            finish();
            continue;
        }
        // A particle that names a material takes its albedo, its tint and light from it, as far as it says nothing itself. A sprite draws with the game's
        // transparent shaders, so the rest of a material has no meaning there.
        for (Block& b : blocks)
            for (size_t i = 0; i < b.def.particles.size(); ++i) {
                ParticleDef& p = b.def.particles[i];
                if (p.material.empty()) continue;
                const auto found = material_named.find(p.material);
                if (found == material_named.end()) {
                    diag.errors.push_back(b.def.where + ".particles[" + std::to_string(i) + "].material: \"" + p.material + "\" is no material of this mod");
                    continue;
                }
                const MaterialDef& m = *found->second;
                if (!p.texture_set) {
                    p.texture = m.albedo;
                    p.texture_linear = false;
                }
                if (!p.color_set)
                    for (auto& c : p.color)
                        for (int k = 0; k < 3; ++k) c[k] *= m.tint[k];
                if (!p.lit_set) p.lit = true;
            }

        // A mod's own particle hooks (ShaderBlock), verified and linked into Kiln's sprite (cook/spvlink.cpp). Any problem draws the particle with
        // Kiln's sprite instead, with a warning: the block keeps its id and still shows, and the game never compiles a module that failed a check.
        std::map<std::string, Bytes> mod_modules;  // by path, those that passed
        for (Block& b : blocks)
            for (size_t i = 0; i < b.def.particles.size(); ++i) {
                ParticleDef& p = b.def.particles[i];
                if (p.shader.empty()) continue;
                const std::string at = b.def.where + ".particles[" + std::to_string(i) + "].shader: " + p.shader + ": ";
                std::string why;
                if (b.def.game_sprite) why = "gameSpriteModule draws with the game's module, so this one isn't used";
                else if (opt.skip_shaders) why = "mods' own particle shaders are off (kiln/config/vfx.json \"shaders\": false)";
                else if (opt.skip_shader_mods.count(mod.id)) why = "this mod's particle shaders are off (kiln/config/vfx.json \"skipShaderMods\")";
                else if (!mod.advanced) why = "a mod with its own GPU code has to say \"advanced\": true in kiln.mod.json";
                else if (!safe_rel(p.shader)) why = "must be relative, inside the mod's folder";
                if (why.empty() && !mod_modules.count(p.shader)) {
                    Bytes code;
                    Bytes linked;
                    ModModuleInfo info;
                    if (!read_mod_module(mod.dir + "/" + p.shader, &code, &why)) why = "can't read it (" + why + ")";
                    else if (std::string bad = check_mod_module(code.data(), code.size(), &info, &linked); !bad.empty()) why = bad;
                    else {
                        // What the game compiles is Kiln's sprite with the verified hooks linked in, never the mod's bytes.
                        std::string hooks;
                        for (const std::string& h : info.hooks) hooks += (hooks.empty() ? "" : ", ") + h;
                        diag.notes.push_back(mod.id + ": " + p.shader + ": hooks " + hooks + " linked into Kiln's sprite (" + std::to_string(info.cost) +
                                             " instructions" + (info.loop_cap ? ", loops capped at " + std::to_string(info.loop_cap) + " passes" : "") + ")");
                        mod_modules[p.shader] = std::move(linked);
                    }
                }
                if (!why.empty()) {
                    diag.warnings.push_back(at + why + ": Kiln's own sprite draws this particle instead");
                    continue;
                }
                const Bytes& code = mod_modules.at(p.shader);
                p.shader_hash = content_hash(code.data(), code.size());
            }

        // Textures, meshes and the VFX of this mod, kept aside until the budget says yes.
        std::map<std::string, BuiltTexture> textures;
        std::vector<BuiltBlock> built_blocks;
        std::vector<std::pair<Guid, Bytes>> built_vfx;
        std::vector<std::string> vfx_names;
        BudgetNeed need;
        std::vector<std::string> used_meshes, used_textures;
        size_t built_n = 0;
        const size_t built_total = blocks.size() + material_blocks.size();
        const auto say = [&](const char* kind, const std::string& name) {
            if (opt.progress) opt.progress(mod.id, nth, mods.size(), kind, name, built_n, built_total);
            ++built_n;
        };
        for (const Block& b : blocks) {
            say("VFX", b.def.name);
            bool bad = false;
            // It has said which material isn't there.
            if (std::any_of(b.def.particles.begin(), b.def.particles.end(), [&](const ParticleDef& p) { return !p.material.empty() && !material_named.count(p.material); })) continue;
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
        // Materials: their textures are encoded here, and nothing is kept until the budget says yes.
        struct MaterialBuilt {
            BuiltMaterial built;
            std::vector<MaterialMap> maps;
            BuiltMaterialBlock info;
            std::vector<ContentHash> reused_chains;  // taken from a MaterialRecord: no maps, these hashes
            bool reused = false;
        };
        std::vector<MaterialBuilt> built_materials;
        std::map<std::string, RawImage> decoded;
        std::map<std::string, ContentHash> file_hash;  // the bytes of each file read, by the path a block names
        std::vector<std::string>* reading = nullptr;   // the files the material being built reads (record_material)
        const ImageLoader load_image = [&](const std::string& rel, RawImage* image, std::string* why) {
            if (reading) reading->push_back(rel);
            const auto got = decoded.find(rel);
            if (got != decoded.end()) {
                *image = got->second;
                return true;
            }
            Bytes png;
            if (!safe_rel(rel)) {
                *why = "must be relative, inside the mod's folder";
                return false;
            }
            if (!read(mod.dir + "/" + rel, &png, why)) {
                *why = "can't read it (" + *why + ")";
                return false;
            }
            if (opt.record_material) file_hash[rel] = content_hash(png.data(), png.size());
            if (!decode_png(png, kMaxMaterialTextureSide, image, why)) return false;
            decoded[rel] = *image;
            return true;
        };
        // A record of the boot build for this block, whose every file still has the recorded bytes (read and hashed here: an edited, missing
        // or unreadable file is a miss, and the block is built from its textures as before).
        const auto reusable = [&](const MaterialBlock& b) -> const MaterialRecord* {
            if (!opt.known_material) return nullptr;
            const MaterialRecord* r = opt.known_material(mod.id, b.def.name, b.definition);
            if (!r || r->mod != mod.id || r->name != b.def.name || r->definition != b.definition) return nullptr;
            for (const auto& [rel, hash] : r->files) {
                Bytes png;
                std::string ignored;
                if (!safe_rel(rel) || !read(mod.dir + "/" + rel, &png, &ignored) || !(content_hash(png.data(), png.size()) == hash)) return nullptr;
            }
            return r;
        };
        // A carried record (an earlier start's): its files must have the same bytes now and the cache must still hold its every chain;
        // then the maps are those chains, as material_maps made them, and nothing is decoded. false: build in full (*note says why).
        const auto carried = [&](const MaterialBlock& b, std::vector<MaterialMap>* maps, std::string* note) {
            if (!opt.carried_material) return false;
            const MaterialRecord* r = opt.carried_material(mod.id, b.def.name, b.definition_text);
            if (!r) {
                *note = "not built at an earlier start (or its definition changed)";
                return false;
            }
            if (r->mod != mod.id || r->name != b.def.name || r->definition_text != b.definition_text || r->maps.empty()) {
                *note = "its record is for another definition";
                return false;
            }
            for (const auto& [rel, hash] : r->files) {
                Bytes png;
                std::string ignored;
                if (!safe_rel(rel) || !read(mod.dir + "/" + rel, &png, &ignored)) {
                    *note = rel + " cannot be read";
                    return false;
                }
                if (!(content_hash(png.data(), png.size()) == hash)) {
                    *note = rel + " changed";
                    return false;
                }
            }
            static const char* const kWhat[] = {"emissive", "albedo", "normal", "material"};
            maps->clear();
            for (const MaterialRecord::Map& rm : r->maps) {
                const char* what = nullptr;
                for (const char* w : kWhat)
                    if (rm.what == w) what = w;
                MaterialMap map;
                if (!what || !chain_from_cache(rm.key, rm.width, rm.height, rm.format, opt.cache, &map.chain)) {
                    *note = "the texture cache no longer holds its " + rm.what + " map";
                    return false;
                }
                map.slot = rm.slot;
                map.what = what;
                maps->push_back(std::move(map));
            }
            return true;
        };
        for (const MaterialBlock& b : material_blocks) {
            MaterialBuilt m;
            std::string field, why, note;
            const bool from_cache = !opt.known_material && carried(b, &m.maps, &note);
            if (!from_cache && opt.carried_material) out.carry_notes.push_back(mod.id + ": material " + b.def.name + ": " + note);
            say(from_cache ? "material as the last start built it:" : "material", b.def.name);
            if (from_cache) {
                if (!build_material(b.def, mod.id, m.maps, &m.built, &why)) {
                    diag.errors.push_back(b.def.where + ": " + why);
                    continue;
                }
                ++out.materials_carried;
                for (const MaterialMap& map : m.maps) m.reused_chains.push_back(content_hash(map.chain.data.data(), map.chain.data.size()));
                if (opt.record_material) {
                    MaterialRecord rec = *opt.carried_material(mod.id, b.def.name, b.definition_text);
                    rec.guid = m.built.guid;
                    rec.render_flags = m.built.render_flags;
                    rec.block_flags = m.built.block_flags;
                    rec.textures = static_cast<uint32_t>(m.maps.size());
                    rec.chains = m.reused_chains;
                    rec.definition = b.definition;  // the index keeps the text; define_json's records go by this hash
                    opt.record_material(rec);       // its files and maps as carried
                }
            } else if (const MaterialRecord* r = reusable(b)) {
                // the same definition and the same file bytes as a build already done: what material_maps and build_material gave then
                m.reused = true;
                m.built.guid = r->guid;
                m.built.render_flags = r->render_flags;
                m.built.block_flags = r->block_flags;
                m.reused_chains = r->chains;
                ++out.materials_reused;
            } else {
                std::vector<std::string> files;
                reading = opt.record_material ? &files : nullptr;
                const bool maps_ok = material_maps(b.def, load_image, opt.cache, &m.maps, &field, &why);
                reading = nullptr;
                if (!maps_ok) {
                    diag.errors.push_back(b.def.where + "." + field + ": " + why);
                    continue;
                }
                if (!build_material(b.def, mod.id, m.maps, &m.built, &why)) {
                    diag.errors.push_back(b.def.where + ": " + why);
                    continue;
                }
                ++out.materials_built;
                for (const MaterialMap& map : m.maps) m.reused_chains.push_back(content_hash(map.chain.data.data(), map.chain.data.size()));
                if (opt.record_material) {
                    MaterialRecord rec;
                    rec.mod = mod.id;
                    rec.name = b.def.name;
                    rec.definition = b.definition;
                    for (const std::string& rel : files)
                        if (std::none_of(rec.files.begin(), rec.files.end(), [&](const auto& f) { return f.first == rel; })) rec.files.emplace_back(rel, file_hash.at(rel));
                    rec.guid = m.built.guid;
                    rec.render_flags = m.built.render_flags;
                    rec.block_flags = m.built.block_flags;
                    rec.textures = static_cast<uint32_t>(m.maps.size());
                    rec.chains = m.reused_chains;
                    rec.definition_text = b.definition_text;
                    for (const MaterialMap& map : m.maps)
                        rec.maps.push_back({map.slot, map.what, map.chain.width, map.chain.height, map.chain.format, map.chain.cache_key});
                    opt.record_material(rec);
                }
            }
            if (!b.def.replaces) ++need.materials;  // a replacement takes the game material's own slot
            for (const ContentHash& chain : m.reused_chains) need.streamed_textures.insert(content_hash_text(chain));
            m.info.mod = mod.id;
            m.info.name = b.def.name;
            m.info.guid = m.built.guid;
            m.info.render_flags = m.built.render_flags;
            m.info.block_flags = m.built.block_flags;
            m.info.textures = m.reused ? static_cast<uint32_t>(m.reused_chains.size()) : static_cast<uint32_t>(m.maps.size());
            m.info.definition = b.definition;
            m.info.replaces = b.def.replaces.has_value();
            built_materials.push_back(std::move(m));
        }
        if (built_vfx.empty() && built_materials.empty()) {
            finish();
            continue;
        }
        for (const std::string& name : used_meshes) {
            const BuiltMesh& m = meshes.at(name);
            if (!counted_meshes.count(m.guid)) need.mesh_bytes += m.blob.size();
        }
        std::string over;
        if (!budget.try_add(need, &over)) {
            out.refused_mods.push_back(mod.id + ": over the game's limits, none of its " + std::to_string(built_vfx.size()) + " VFX and " + std::to_string(built_materials.size()) +
                                       " material(s) are registered (" + over + ")");
            finish();
            continue;
        }
        for (const std::string& name : used_meshes) counted_meshes.insert(meshes.at(name).guid);

        // Yes: serve it, blobs before the resources that name them.
        for (MaterialBuilt& m : built_materials) {
            for (const MaterialMap& map : m.maps) {
                const ContentHash chain = content_hash(map.chain.data.data(), map.chain.data.size());
                add_blob(out, shared, chain, map.chain.data.data(), map.chain.data.size(), mod.id + " material " + m.info.name + " " + map.what);
                const size_t tail_size = map.chain.data.size() - map.chain.tail_start;
                const uint8_t* tail = map.chain.data.data() + map.chain.tail_start;
                add_blob(out, shared, content_hash(tail, tail_size), tail, tail_size, mod.id + " material " + m.info.name + " " + map.what + " mip tail");
            }
            if (m.info.replaces && shared.resources.count(m.built.guid))
                diag.warnings.push_back(mod.id + ": material " + m.info.name + ": another mod earlier in the load order replaces " + guid_text(m.built.guid) +
                                        " already: that one draws (this one stands by, and draws if that mod unloads)");
            if (m.reused) shared.resources.insert(m.built.guid);  // nothing to serve: the build it was taken from served it
            else add_resource(out, shared, m.built.guid, kTypeMaterial, std::move(m.built.resource), mod.id + " material " + m.info.name, m.info.replaces, mod.id);
            out.materials.push_back(std::move(m.info));
        }
        if (built_vfx.empty()) {
            finish();
            continue;
        }
        if (!shared.modules) {
            shared.modules = true;
            for (Module m : {Module::Emitter, Module::Sprite, Module::Light}) {
                const ModuleInfo& info = starter_module(m);
                add_blob(out, shared, info.hash, info.data, info.size, info.name);
            }
        }
        for (const auto& [path, code] : mod_modules) {
            add_blob(out, shared, content_hash(code.data(), code.size()), code.data(), code.size(), mod.id + " particle shader " + path);
            ++out.mod_modules;
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
    out.used_materials = budget.used_materials();
    out.used_streamed_textures = budget.used_streamed_textures();
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
    ojson materials = ojson::array();
    for (const BuiltMaterialBlock& m : r.materials) {
        ojson o = ojson::object();
        o["mod"] = m.mod;
        o["name"] = m.name;
        o["guid"] = guid_text(m.guid);
        o["renderFlags"] = m.render_flags;
        o["blockFlags"] = m.block_flags;
        o["textures"] = m.textures;
        o["pipelines"] = material_pipelines(m.render_flags);
        if (m.replaces) o["replaces"] = true;
        materials.push_back(std::move(o));
    }
    j["materials"] = std::move(materials);
    j["errors"] = r.errors;
    j["warnings"] = r.warnings;
    j["notes"] = r.notes;
    j["refusedMods"] = r.refused_mods;
    j["modModules"] = r.mod_modules;
    ojson b = ojson::object();
    b["vfx"] = {{"game", r.game.vfx}, {"mods", r.used_vfx}, {"capacity", r.capacity.vfx}};
    b["meshes"] = {{"game", r.game.meshes}, {"mods", r.used_meshes}, {"capacity", r.capacity.meshes}};
    b["meshBytes"] = {{"game", r.game.mesh_bytes}, {"mods", r.used_mesh_bytes}, {"capacity", r.capacity.mesh_bytes}, {"margin", r.capacity.mesh_margin}};
    b["sounds"] = {{"game", r.game.sounds}, {"mods", r.used_sounds}, {"capacity", r.capacity.sounds}};
    b["curves"] = {{"game", r.game.curves}, {"mods", r.used_curves}, {"capacity", r.capacity.curves}};
    b["materials"] = {{"game", r.game.materials}, {"mods", r.used_materials}, {"capacity", r.capacity.materials}};
    b["streamedTextures"] = {{"game", r.game.streamed_textures}, {"mods", r.used_streamed_textures}, {"capacity", r.capacity.streamed_textures}};
    j["budget"] = std::move(b);
    return j;
}

}  // namespace kvb
