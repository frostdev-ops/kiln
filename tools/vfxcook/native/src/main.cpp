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
//   kiln-vfxcook budget [--kfc FILE] [--counts vfx=N,meshes=N,curves=N,sounds=N,meshBytes=N]
#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <sstream>

#include "vfxcook.h"

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
    std::string out, kfc, cache, pending, exe, gpc, counts;
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
    }
    return g;
}

const char* type_name(uint32_t t) {
    switch (t) {
        case kTypeVfx: return "keen::VfxResource";
        case kTypeMesh: return "keen::VfxMeshResource";
        case kTypeTexture: return "keen::RenderTextureResource";
        default: return "?";
    }
}
const char* type_short(uint32_t t) {
    switch (t) {
        case kTypeVfx: return "vfx";
        case kTypeMesh: return "mesh";
        case kTypeTexture: return "texture";
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
        for (const ServedResource& s : r.resources) {
            const std::string g = guid_text(s.guid);
            const std::string rel = std::string("cooked/") + type_short(s.type) + "-" + g + ".bin";
            spit(out / rel, s.data.data(), s.data.size());
            ojson e = ojson::object();
            e["kind"] = "resource";
            e["guid"] = g;
            e["type"] = type_name(s.type);
            e["part"] = 0;
            e["mode"] = "new";
            e["file"] = rel;
            e["side"] = "client";
            entries.push_back(e);
            resources.push_back({{"what", s.what}, {"guid", g}, {"type", type_name(s.type)}, {"file", rel}, {"size", s.data.size()}});
        }
        ojson manifest = ojson::object();
        manifest["format"] = 1;
        manifest["entries"] = entries;
        const std::string ms = manifest.dump(2) + "\n";
        spit(out / "assets.json", ms.data(), ms.size());
        report["served"] = {{"blobs", blobs}, {"resources", resources}};
        const std::string rs = report.dump(1) + "\n";
        spit(out / "report.json", rs.data(), rs.size());
    }
    for (const std::string& e : r.errors) std::fprintf(stderr, "error: %s\n", e.c_str());
    for (const std::string& e : r.refused_mods) std::fprintf(stderr, "refused: %s\n", e.c_str());
    for (const std::string& w : r.warnings) std::fprintf(stderr, "warning: %s\n", w.c_str());
    if (!write) std::cout << report.dump(1) << "\n";
    else std::printf("%zu VFX, %zu resources, %zu blobs -> %s\n", r.blocks.size(), r.resources.size(), r.blobs.size(), a.out.c_str());
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
    std::cout << o.dump(1) << "\n";
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: kiln-vfxcook cook|check|hash|modules|id|fingerprint|budget ... (docs/kiln/services/vfx-cook.md)\n");
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
    if (cmd == "id" && a.rest.size() == 3) {
        std::printf("%s\n", guid_text(block_id(a.rest[0], a.rest[1], a.rest[2])).c_str());
        return 0;
    }
    std::fprintf(stderr, "unknown command %s\n", cmd.c_str());
    return 2;
}
