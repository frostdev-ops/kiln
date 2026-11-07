// SPDX-License-Identifier: MIT
// kiln-vfxcook: the builder of kiln-vfx.dll as a command line (docs/kiln/services/vfx-cook.md).
//
//   kiln-vfxcook cook --mod DIR [--mod DIR ...] --out DIR [--kfc FILE] [--cache DIR] [--pending DIR]
//        Build what the game would be served at boot: DIR/assets.json + DIR/cooked/*, report.json. 0 = all fine, 1 = errors or refusals.
//   kiln-vfxcook check --mod DIR [--mod DIR ...] [--kfc FILE]
//        The same without writing anything: the report on stdout.
//   kiln-vfxcook hash FILE ...          128-bit hash and ContentHash of files
//   kiln-vfxcook modules                the starter modules: name, size, hash, shape check
//   kiln-vfxcook id MOD KIND NAME       a block's GUID
//   kiln-vfxcook fingerprint --exe EXE [--gpc FILE] [--kfc FILE]
//        Layouts and bindings of this game against what the modules were made for (JSON). 0 = match.
//   kiln-vfxcook budget [--kfc FILE] [--counts vfx=N,meshes=N,curves=N,sounds=N,meshBytes=N,materials=N,streamedTextures=N]
//   kiln-vfxcook shader build --mod DIR [--glslang PATH] [--linked]
//        Compiles DIR/shaders/*.hlsl against the hook kit into hook modules DIR/shaders/build/*.spv (glslangValidator -Od), stamps the hook ABI,
//        verifies and links each into Kiln's sprite as the game would (and runs spirv-val on the result when it is installed). --linked also
//        writes the linked module as build/*.linked.spv, for inspection. 0 = all built and fine.
//   kiln-vfxcook shader check FILE.spv ...   the load-time verify-and-link on built hook modules (JSON). 0 = all fine.
//   kiln-vfxcook shader link FILE.spv --out LINKED.spv   writes the linked module the game would compile (for the harnesses)
//   kiln-vfxcook shader kit --out DIR        writes the kit's headers, for an editor or another compiler
//   kiln-vfxcook material-roundtrip FILE
//        FILE holds {u32 size, bytes} records of RenderMaterialResources: reads and rewrites each with the builder's own code. JSON; 0 = all byte-identical.
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>

#include "vfxcook.h"
#include "../shaders/kiln_vfx_params.h"

namespace fs = std::filesystem;
using namespace kvb;

namespace {

std::string slurp(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    std::stringstream s;
    s << f.rdbuf();
    return s.str();
}
void spit(const fs::path& p, const void* d, size_t n) {
    fs::create_directories(p.parent_path());
    std::ofstream f(p, std::ios::binary);
    f.write(static_cast<const char*>(d), static_cast<std::streamsize>(n));
}
std::string u8(const fs::path& p) {
    const auto s = p.u8string();
    return std::string(reinterpret_cast<const char*>(s.data()), s.size());
}

struct Args {
    std::vector<std::string> mods;
    std::string out, kfc, cache, pending, exe, gpc, counts, glslc, glslang;
    bool linked = false;
    std::vector<std::string> rest;
};
Args parse(int argc, char** argv, int from) {
    Args a;
    for (int i = from; i < argc; ++i) {
        const std::string s = argv[i];
        auto next = [&]() -> std::string {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "%s needs a value\n", s.c_str());
                std::exit(2);
            }
            return argv[++i];
        };
        if (s == "--mod") a.mods.push_back(next());
        else if (s == "--out") a.out = next();
        else if (s == "--kfc") a.kfc = next();
        else if (s == "--cache") a.cache = next();
        else if (s == "--pending") a.pending = next();
        else if (s == "--exe") a.exe = next();
        else if (s == "--gpc") a.gpc = next();
        else if (s == "--counts") a.counts = next();
        else if (s == "--glslc") a.glslc = next();
        else if (s == "--glslang") a.glslang = next();
        else if (s == "--linked") a.linked = true;
        else a.rest.push_back(s);
    }
    return a;
}

// kiln.mod.json + blocks/*.json + a pending folder (kiln/data/vfx/pending/<id>/*.json), as boot.cpp reads them.
bool read_mod(const std::string& dir, const std::string& pending, ModInput* m, std::string* why) {
    const fs::path d = fs::path(dir);
    const fs::path mf = d / "kiln.mod.json";
    if (!fs::exists(mf)) {
        *why = dir + ": no kiln.mod.json";
        return false;
    }
    const ojson j = parse_relaxed(slurp(mf));
    if (j.is_discarded() || !j.is_object() || !j.contains("id") || !j["id"].is_string()) {
        *why = dir + ": kiln.mod.json has no id";
        return false;
    }
    m->id = j["id"].get<std::string>();
    m->dir = u8(d);
    m->side = j.value("side", "server");
    m->clients = j.value("clients", m->side == "server" ? "none" : "mod");
    m->advanced = j.contains("advanced") && j["advanced"].is_boolean() && j["advanced"].get<bool>();
    auto add_folder = [&](const fs::path& folder, const std::string& prefix) {
        std::vector<fs::path> files;
        std::error_code ec;
        for (const auto& e : fs::directory_iterator(folder, ec))
            if (e.path().extension() == ".json") files.push_back(e.path());
        std::sort(files.begin(), files.end());
        for (const fs::path& f : files) m->documents.emplace_back(prefix + "/" + u8(f.filename()), slurp(f));
    };
    add_folder(d / "blocks", "blocks");
    if (!pending.empty()) add_folder(fs::path(pending) / m->id, "pending");
    return true;
}

TextureCache folder_cache(const std::string& dir) {
    TextureCache c;
    if (dir.empty()) return c;
    c.get = [dir](const std::string& key, Bytes* out) {
        std::ifstream f(fs::path(dir) / (key + ".tex"), std::ios::binary);
        if (!f) return false;
        out->assign(std::istreambuf_iterator<char>(f), {});
        return true;
    };
    c.put = [dir](const std::string& key, const Bytes& data) { spit(fs::path(dir) / (key + ".tex"), data.data(), data.size()); };
    return c;
}

GameCounts counts_of(const Args& a) {
    GameCounts g;
    if (!a.kfc.empty()) {
        std::map<uint32_t, KfcBundle> b;
        std::string why;
        if (read_kfc_bundles(a.kfc, &b, &why)) g = game_counts_from(b);
        else std::fprintf(stderr, "%s: using the known counts\n", why.c_str());
    }
    std::stringstream s(a.counts);
    for (std::string item; std::getline(s, item, ',');) {
        const size_t eq = item.find('=');
        if (eq == std::string::npos) continue;
        const std::string k = item.substr(0, eq);
        const uint64_t v = std::stoull(item.substr(eq + 1));
        if (k == "vfx") g.vfx = static_cast<uint32_t>(v);
        else if (k == "meshes") g.meshes = static_cast<uint32_t>(v);
        else if (k == "curves") g.curves = static_cast<uint32_t>(v);
        else if (k == "sounds") g.sounds = static_cast<uint32_t>(v);
        else if (k == "meshBytes") g.mesh_bytes = v;
        else if (k == "materials") g.materials = static_cast<uint32_t>(v);
        else if (k == "streamedTextures") g.streamed_textures = static_cast<uint32_t>(v);
    }
    return g;
}

const char* type_name(uint32_t t) {
    switch (t) {
        case kTypeVfx: return "keen::VfxResource";
        case kTypeMesh: return "keen::VfxMeshResource";
        case kTypeTexture: return "keen::RenderTextureResource";
        case kTypeMaterial: return "keen::RenderMaterialResource";
        default: return "?";
    }
}
const char* type_short(uint32_t t) {
    switch (t) {
        case kTypeVfx: return "vfx";
        case kTypeMesh: return "mesh";
        case kTypeTexture: return "texture";
        case kTypeMaterial: return "material";
        default: return "res";
    }
}

int cmd_cook(const Args& a, bool write) {
    if (a.mods.empty()) {
        std::fprintf(stderr, "--mod DIR, at least one\n");
        return 2;
    }
    std::vector<ModInput> mods;
    for (const std::string& dir : a.mods) {
        ModInput m;
        std::string why;
        if (!read_mod(dir, a.pending, &m, &why)) {
            std::fprintf(stderr, "%s\n", why.c_str());
            return 2;
        }
        mods.push_back(std::move(m));
    }
    CookOptions opt;
    opt.game = counts_of(a);
    opt.cache = folder_cache(a.cache);
    if (!a.kfc.empty()) opt.game_has_blob = [path = a.kfc](const ContentHash& h) { return kfc_has_content(path, h); };
    const CookResult r = cook_mods(mods, opt);
    ojson report = report_json(r);
    if (write) {
        const fs::path out = a.out;
        ojson entries = ojson::array();
        ojson blobs = ojson::array(), resources = ojson::array();
        for (const ServedBlob& b : r.blobs) {
            const std::string text = content_hash_text(b.hash);
            const std::string rel = "cooked/blob-" + text + ".bin";
            spit(out / rel, b.data.data(), b.data.size());
            ojson e = ojson::object();
            e["kind"] = "blob";
            e["hash"] = text;
            e["file"] = rel;
            e["side"] = "client";
            entries.push_back(e);
            blobs.push_back({{"what", b.what}, {"hash", text}, {"file", rel}, {"size", b.data.size()}});
        }
        ojson standby = ojson::array();
        for (const ServedResource& s : r.resources) {
            const std::string g = guid_text(s.guid);
            if (s.standby) {  // a second mod's replacement of the same game material: kiln.vfx serves it only if the first mod unloads
                standby.push_back({{"what", s.what}, {"guid", g}, {"mod", s.mod}, {"size", s.data.size()}});
                continue;
            }
            const std::string rel = std::string("cooked/") + type_short(s.type) + "-" + g + ".bin";
            spit(out / rel, s.data.data(), s.data.size());
            ojson e = ojson::object();
            e["kind"] = "resource";
            e["guid"] = g;
            e["type"] = type_name(s.type);
            e["part"] = 0;
            e["mode"] = s.override ? "override" : "new";
            e["file"] = rel;
            e["side"] = "client";
            entries.push_back(e);
            resources.push_back({{"what", s.what}, {"guid", g}, {"type", type_name(s.type)}, {"file", rel}, {"size", s.data.size()}});
            if (s.override) resources.back()["mod"] = s.mod;
        }
        ojson manifest = ojson::object();
        manifest["format"] = 1;
        manifest["entries"] = entries;
        const std::string ms = manifest.dump(2) + "\n";
        spit(out / "assets.json", ms.data(), ms.size());
        report["served"] = {{"blobs", blobs}, {"resources", resources}, {"standby", standby}};
        const std::string rs = report.dump(1) + "\n";
        spit(out / "report.json", rs.data(), rs.size());
    }
    for (const std::string& e : r.errors) std::fprintf(stderr, "error: %s\n", e.c_str());
    for (const std::string& e : r.refused_mods) std::fprintf(stderr, "refused: %s\n", e.c_str());
    for (const std::string& w : r.warnings) std::fprintf(stderr, "warning: %s\n", w.c_str());
    for (const std::string& n : r.notes) std::fprintf(stderr, "note: %s\n", n.c_str());
    if (!write) std::cout << report.dump(1) << "\n";
    else std::printf("%zu VFX, %zu materials, %zu resources, %zu blobs -> %s\n", r.blocks.size(), r.materials.size(), r.resources.size(), r.blobs.size(), a.out.c_str());
    return r.errors.empty() && r.refused_mods.empty() ? 0 : 1;
}

int cmd_hash(const Args& a) {
    for (const std::string& f : a.rest) {
        std::ifstream s(f, std::ios::binary);
        if (!s) {
            std::fprintf(stderr, "can't open %s\n", f.c_str());
            return 2;
        }
        const Bytes b((std::istreambuf_iterator<char>(s)), {});
        uint8_t h[16];
        keen_hash128(b.data(), b.size(), h);
        for (uint8_t c : h) std::printf("%02x", c);
        std::printf(" %s %s\n", content_hash_text(content_hash(b.data(), b.size())).c_str(), f.c_str());
    }
    return 0;
}

int cmd_modules() {
    ojson out = ojson::array();
    int rc = 0;
    for (Module m : {Module::Emitter, Module::Sprite, Module::Light}) {
        const ModuleInfo& i = starter_module(m);
        const std::string bad = check_module(i.data, i.size);
        rc |= bad.empty() ? 0 : 1;
        out.push_back({{"name", i.name}, {"size", i.size}, {"hash", content_hash_text(i.hash)}, {"check", bad.empty() ? "ok" : bad}});
    }
    std::cout << out.dump(1) << "\n";
    return rc;
}

int cmd_fingerprint(const Args& a) {
    ojson out = ojson::object();
    int rc = 0;
    if (!a.exe.empty()) {
        Bytes image;
        uint64_t base = 0;
        std::string why;
        if (!load_pe_file(a.exe, &image, &base, &why)) {
            std::fprintf(stderr, "%s\n", why.c_str());
            return 2;
        }
        const auto found = read_layouts({image.data(), image.size(), base});
        ojson l = ojson::array();
        for (const LayoutFound& f : found) {
            char e[16], g[16];
            std::snprintf(e, sizeof e, "%08x", f.expected);
            std::snprintf(g, sizeof g, "%08x", f.found);
            l.push_back({{"name", f.name}, {"expected", e}, {"found", g}, {"ok", f.found == f.expected}});
        }
        out["layouts"] = l;
        const auto material_found = read_layouts({image.data(), image.size(), base}, material_layouts());
        ojson ml = ojson::array();
        for (const LayoutFound& f : material_found) {
            char e[16], g[16];
            std::snprintf(e, sizeof e, "%08x", f.expected);
            std::snprintf(g, sizeof g, "%08x", f.found);
            ml.push_back({{"name", f.name}, {"expected", e}, {"found", g}, {"ok", f.found == f.expected}});
        }
        out["materialLayouts"] = ml;
        const std::string material_diff = compare_layouts(material_found);
        out["materialLayoutsOk"] = material_diff.empty();
        if (!material_diff.empty()) {
            out["materialDifference"] = material_diff;
            rc = 1;
        }
        const std::string diff = compare_layouts(found);
        out["layoutsOk"] = diff.empty();
        if (!diff.empty()) {
            out["difference"] = diff;
            rc = 1;
        }
    }
    if (!a.gpc.empty()) {
        const std::string g = slurp(a.gpc);
        uint32_t h = 0, n = 0;
        const bool ok = binding_hash_of_gpc(reinterpret_cast<const uint8_t*>(g.data()), g.size(), &h, &n);
        char hs[16], es[16];
        std::snprintf(hs, sizeof hs, "%08x", h);
        std::snprintf(es, sizeof es, "%08x", kBindingHash);
        out["bindings"] = {{"found", ok}, {"count", n}, {"hash", hs}, {"expected", es}, {"ok", ok && h == kBindingHash}};
        if (!ok || h != kBindingHash) rc = 1;
    }
    if (!a.kfc.empty()) {
        std::map<uint32_t, KfcBundle> b;
        std::string why;
        if (!read_kfc_bundles(a.kfc, &b, &why)) {
            std::fprintf(stderr, "%s\n", why.c_str());
            return 2;
        }
        const GameCounts g = game_counts_from(b);
        out["kfc"] = {{"vfx", g.vfx}, {"meshes", g.meshes}, {"curves", g.curves}};
        ojson kl = ojson::array();
        for (const LayoutExpect& e : expected_layouts()) {
            const auto it = b.find(e.type_hash);
            if (it == b.end()) continue;
            kl.push_back({{"name", e.name}, {"count", it->second.count}, {"ok", it->second.layout == e.layout}});
            if (it->second.layout != e.layout) rc = 1;
        }
        out["kfcLayouts"] = kl;
    }
    std::cout << out.dump(1) << "\n";
    return rc;
}

int cmd_budget(const Args& a) {
    const Budget b(counts_of(a));
    ojson o = ojson::object();
    o["vfx"] = {{"game", b.game().vfx}, {"capacity", b.capacity().vfx}, {"room", b.room_vfx()}};
    o["meshes"] = {{"game", b.game().meshes}, {"capacity", b.capacity().meshes}, {"room", b.room_meshes()}};
    o["meshBytes"] = {{"game", b.game().mesh_bytes}, {"capacity", b.capacity().mesh_bytes}, {"margin", b.capacity().mesh_margin}, {"room", b.room_mesh_bytes()}};
    o["sounds"] = {{"game", b.game().sounds}, {"capacity", b.capacity().sounds}, {"room", b.room_sounds()}};
    o["curves"] = {{"game", b.game().curves}, {"capacity", b.capacity().curves}, {"room", b.room_curves()}};
    o["materials"] = {{"game", b.game().materials}, {"capacity", b.capacity().materials}, {"room", b.room_materials()}};
    o["streamedTextures"] = {{"game", b.game().streamed_textures}, {"capacity", b.capacity().streamed_textures}, {"room", b.room_streamed_textures()}};
    std::cout << o.dump(1) << "\n";
    return 0;
}

int cmd_material_roundtrip(const Args& a) {
    if (a.rest.size() != 1) {
        std::fprintf(stderr, "material-roundtrip FILE\n");
        return 2;
    }
    const std::string all = slurp(a.rest[0]);
    size_t at = 0;
    uint64_t total = 0, identical = 0;
    ojson different = ojson::array();
    while (at + 4 <= all.size()) {
        uint32_t size = 0;
        std::memcpy(&size, all.data() + at, 4);
        at += 4;
        if (at + size > all.size()) {
            std::fprintf(stderr, "record %llu is cut short\n", static_cast<unsigned long long>(total));
            return 2;
        }
        const auto* p = reinterpret_cast<const uint8_t*>(all.data() + at);
        RenderMaterial m;
        std::string why;
        if (!read_material(p, size, &m, &why)) {
            different.push_back({{"record", total}, {"problem", why}});
        } else {
            const Bytes back = write_material(m);
            if (back.size() == size && std::memcmp(back.data(), p, size) == 0) ++identical;
            else different.push_back({{"record", total}, {"problem", "rewrote " + std::to_string(back.size()) + " bytes of " + std::to_string(size) + ": they differ"}, {"name", m.debug_name}});
        }
        at += size;
        ++total;
    }
    ojson o = ojson::object();
    o["records"] = total;
    o["identical"] = identical;
    if (different.size() > 20) different.erase(different.begin() + 20, different.end());
    o["different"] = different;
    std::cout << o.dump(1) << "\n";
    return identical == total ? 0 : 1;
}

// ---- shader: a mod's own particle modules (docs/kiln/modding/shaders.md) ----
#if defined(__clang__)
#pragma clang diagnostic ignored "-Wc23-extensions"
#endif
const unsigned char kKitHooks[] = {
#embed "../../../../native/services/vfx/shaders/kiln_hooks.hlsl"
    , 0};
const unsigned char kKitHooksMain[] = {
#embed "../../../../native/services/vfx/shaders/kiln_hooks_main.hlsl"
    , 0};
const unsigned char kKitMath[] = {
#embed "../../../../native/services/vfx/shaders/kiln_math.hlsl"
    , 0};
const unsigned char kKitParams[] = {
#embed "../../../../native/services/vfx/shaders/kiln_vfx_params.h"
    , 0};
// Sources written for the old whole-module kit include these two names: they now build a hook module unchanged.
const char kKitParticleShim[] = "// The hook kit under its old name: see kiln_hooks.hlsl.\n#include \"kiln_hooks.hlsl\"\n";
const char kKitMainShim[] = "// The hook kit under its old name: see kiln_hooks_main.hlsl.\n#include \"kiln_hooks_main.hlsl\"\n";

bool write_kit(const fs::path& dir) {
    std::error_code ec;
    fs::create_directories(dir, ec);
    const std::pair<const char*, const char*> files[] = {
        {"kiln_hooks.hlsl", reinterpret_cast<const char*>(kKitHooks)}, {"kiln_hooks_main.hlsl", reinterpret_cast<const char*>(kKitHooksMain)},
        {"kiln_math.hlsl", reinterpret_cast<const char*>(kKitMath)},   {"kiln_vfx_params.h", reinterpret_cast<const char*>(kKitParams)},
        {"kiln_particle.hlsl", kKitParticleShim},                      {"kiln_particle_main.hlsl", kKitMainShim}};
    for (const auto& [name, text] : files) spit(dir / name, text, std::strlen(text));
    return !ec;
}

std::string quote(const std::string& s) {
#ifdef _WIN32
    return "\"" + s + "\"";
#else
    std::string q = "'";
    for (char c : s) q += c == '\'' ? std::string("'\\''") : std::string(1, c);
    return q + "'";
#endif
}

#ifdef _WIN32
const char* kNull = " >NUL 2>&1";
#else
const char* kNull = " >/dev/null 2>&1";
#endif

ojson check_one(const Bytes& code, Bytes* linked_out = nullptr) {
    ModModuleInfo info;
    Bytes linked;
    const std::string bad = check_mod_module(code.data(), code.size(), &info, &linked);
    ojson o = ojson::object();
    o["ok"] = bad.empty();
    if (!bad.empty()) o["problem"] = bad;
    o["bytes"] = code.size();
    o["abi"] = info.abi;
    o["hooks"] = info.hooks;
    o["instructions"] = info.instructions;
    o["cost"] = info.cost;
    o["budget"] = kHookBudget;
    o["loops"] = info.loops;
    o["loopCap"] = info.loop_cap;
    o["clampedIndices"] = info.clamped;
    if (bad.empty()) {
        o["linkedBytes"] = linked.size();
        o["hash"] = content_hash_text(content_hash(linked.data(), linked.size()));
        if (linked.size() > 256 * 1024) o["warning"] = "the linked module is above 256 KB: it compiles slowly on the player's first play";
    }
    if (linked_out) *linked_out = std::move(linked);
    return o;
}

// spirv-val on a linked module, when it is installed: true = valid or not installed (o says which).
bool validate(const Bytes& linked, const fs::path& tmp, ojson& o) {
    if (std::system((std::string("spirv-val --version") + kNull).c_str()) != 0) {
        o["validated"] = "spirv-val not installed";
        return true;
    }
    spit(tmp, linked.data(), linked.size());
    const bool good = std::system(("spirv-val --target-env vulkan1.2 --scalar-block-layout " + quote(u8(tmp))).c_str()) == 0;
    std::error_code ec;
    fs::remove(tmp, ec);
    o["validated"] = good ? "spirv-val passed" : "spirv-val failed: see its messages above";
    return good;
}

int cmd_shader(const Args& a) {
    const std::string sub = a.rest.empty() ? "" : a.rest[0];
    if (sub == "kit") {
        if (a.out.empty()) {
            std::fprintf(stderr, "shader kit needs --out DIR\n");
            return 2;
        }
        write_kit(a.out);
        std::printf("%s\n", u8(fs::path(a.out)).c_str());
        return 0;
    }
    if (sub == "link") {
        // One hook module -> the linked module the game would compile, for the harnesses (test_mod_module.py, test_gpu.py).
        if (a.rest.size() != 2 || a.out.empty()) {
            std::fprintf(stderr, "usage: kiln-vfxcook shader link HOOKS.spv --out LINKED.spv\n");
            return 2;
        }
        Bytes code, linked;
        std::string why;
        ojson o = read_mod_module(a.rest[1], &code, &why) ? check_one(code, &linked) : ojson{{"ok", false}, {"problem", why}};
        if (o["ok"].get<bool>()) spit(fs::absolute(a.out), linked.data(), linked.size());
        std::cout << o.dump(1) << "\n";
        return o["ok"].get<bool>() ? 0 : 1;
    }
    if (sub == "check") {
        ojson all = ojson::array();
        bool ok = a.rest.size() > 1;
        for (size_t i = 1; i < a.rest.size(); ++i) {
            Bytes code;
            std::string why;
            const bool read = read_mod_module(a.rest[i], &code, &why);
            ojson o = check_one(code);
            if (!read) { o["ok"] = false; o["problem"] = why; }
            o["file"] = a.rest[i];
            ok = ok && o["ok"].get<bool>();
            all.push_back(std::move(o));
        }
        std::cout << all.dump(1) << "\n";
        return ok ? 0 : 1;
    }
    if (sub != "build" || a.mods.size() != 1) {
        std::fprintf(stderr, "usage: kiln-vfxcook shader build --mod DIR [--glslang PATH] [--linked] | shader check FILE.spv ... | shader kit --out DIR\n");
        return 2;
    }
    if (!a.glslc.empty()) {
        std::fprintf(stderr, "shader build: hook modules are built with glslangValidator (glslc always inlines HLSL): pass --glslang PATH\n");
        return 2;
    }
    const fs::path mod = a.mods[0], src = mod / "shaders", outdir = src / "build";
    std::vector<fs::path> sources;
    std::error_code ec;
    for (const auto& e : fs::directory_iterator(src, ec))
        if (e.path().extension() == ".hlsl") sources.push_back(e.path());
    std::sort(sources.begin(), sources.end());
    if (sources.empty()) {
        std::fprintf(stderr, "%s: no shaders/*.hlsl\n", u8(mod).c_str());
        return 1;
    }
    const fs::path kit = fs::temp_directory_path() / ("kiln-vfx-hook-kit-" + std::to_string(KILN_HOOKS_ABI));
    write_kit(kit);
    const std::string glslang = a.glslang.empty() ? "glslangValidator" : a.glslang;
    ojson all = ojson::array();
    bool ok = true;
    fs::create_directories(outdir, ec);
    for (const fs::path& s : sources) {
        const fs::path spv = outdir / (s.stem().string() + ".spv");
        // -Od keeps the hook functions apart (HLSL legalisation would inline them into the stub entry); the driver optimises the linked module.
        const std::string cmd = quote(glslang) + " -V -D -Od -e cs_main -S comp --target-env vulkan1.2 -I" + quote(u8(kit)) + " -I" + quote(u8(src)) + " " +
                                quote(u8(s)) + " -o " + quote(u8(spv)) + kNull;
        ojson o = ojson::object();
        o["source"] = u8(s);
        o["file"] = u8(spv);
        auto fail = [&](const std::string& why) {
            o["ok"] = false;
            o["problem"] = why;
            ok = false;
            all.push_back(std::move(o));
        };
        if (std::system(cmd.c_str()) != 0) {
            // run it again with its messages shown
            std::system((quote(glslang) + " -V -D -Od -e cs_main -S comp --target-env vulkan1.2 -I" + quote(u8(kit)) + " -I" + quote(u8(src)) + " " + quote(u8(s)) +
                         " -o " + quote(u8(spv)) + " 1>&2").c_str());
            fail("the compiler failed (" + glslang + "; install the Vulkan SDK or glslang, or pass --glslang): see its messages above");
            continue;
        }
        Bytes code;
        std::string why;
        if (!read_mod_module(u8(spv), &code, &why)) { fail(why); continue; }
        if (!stamp_module(&code, &why)) { fail(why); continue; }
        spit(spv, code.data(), code.size());
        Bytes linked;
        ojson c = check_one(code, &linked);
        for (auto it = c.begin(); it != c.end(); ++it) o[it.key()] = it.value();
        if (o["ok"].get<bool>()) {
            if (a.linked) spit(outdir / (s.stem().string() + ".linked.spv"), linked.data(), linked.size());
            if (!validate(linked, outdir / (s.stem().string() + ".linked.tmp"), o)) {
                o["ok"] = false;
                o["problem"] = "the linked module fails spirv-val: send the source to Kiln, this is Kiln's bug or a construct the linker should refuse";
            }
        }
        ok = ok && o["ok"].get<bool>();
        all.push_back(std::move(o));
    }
    std::cout << all.dump(1) << "\n";
    return ok ? 0 : 1;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: kiln-vfxcook cook|check|hash|modules|id|fingerprint|budget|material-roundtrip|shader ... (docs/kiln/services/vfx-cook.md)\n");
        return 2;
    }
    const std::string cmd = argv[1];
    const Args a = parse(argc, argv, 2);
    if (cmd == "cook") {
        if (a.out.empty()) {
            std::fprintf(stderr, "cook needs --out DIR\n");
            return 2;
        }
        return cmd_cook(a, true);
    }
    if (cmd == "check") return cmd_cook(a, false);
    if (cmd == "hash") return cmd_hash(a);
    if (cmd == "modules") return cmd_modules();
    if (cmd == "fingerprint") return cmd_fingerprint(a);
    if (cmd == "budget") return cmd_budget(a);
    if (cmd == "material-roundtrip") return cmd_material_roundtrip(a);
    if (cmd == "shader") return cmd_shader(a);
    if (cmd == "id" && a.rest.size() == 3) {
        std::printf("%s\n", guid_text(block_id(a.rest[0], a.rest[1], a.rest[2])).c_str());
        return 0;
    }
    std::fprintf(stderr, "unknown command %s\n", cmd.c_str());
    return 2;
}
