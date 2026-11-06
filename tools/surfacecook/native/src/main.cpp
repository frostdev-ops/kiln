// SPDX-License-Identifier: MIT
// kiln-surfacecook: the surface cooker of kiln-surfaces.dll as a command line (docs/kiln/services/surfaces.md). The tables are the client's
// resources as tools/kfc exports them (`kfc.py get`); nothing here reads the game's archive.
//
//   kiln-surfacecook roundtrip --blend F --gi F --params F
//        Reads the three tables and writes them back, whole and slot by slot. JSON; 0 = every byte the same.
//   kiln-surfacecook layouts --blend F --gi F
//        Every texture of the tables against the streaming layout the cooker writes: levels, mip ranges, tail size. JSON; 0 = all agree.
//   kiln-surfacecook cook --blend F --gi F --params F --mod DIR [--mod DIR ...] --out DIR [--names FILE] [--blobs DIR] [--cache DIR] [--room N]
//        What the game would be served: DIR/blending.bin, gi.bin, params.bin, blobs/<hash>.bin, report.json. Ids as kiln.worldtables gives them,
//        from the first free slot. --names: {"GameMaterialName": cellId}; --blobs: game blobs by hash, for averages of game maps. 0 = no errors.
//   kiln-surfacecook check JSON [--label TEXT] [--tweak 1]
//        A surface, as a block writes it (--tweak 1: a retexture's): what is wrong with it, and its normal form. 0 = nothing.
//   kiln-surfacecook hash FILE [--from N]
//        The ContentHash of a file, or of its bytes from N on.
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <sstream>

#include "merge.h"
#include "surfacecook.h"

namespace fs = std::filesystem;
using namespace ksf;

namespace {

Bytes slurp(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    return Bytes(std::istreambuf_iterator<char>(f), {});
}
std::string slurp_text(const fs::path& p) {
    const Bytes b = slurp(p);
    return std::string(b.begin(), b.end());
}
void spit(const fs::path& p, const void* d, size_t n) {
    fs::create_directories(p.parent_path());
    std::ofstream f(p, std::ios::binary);
    f.write(static_cast<const char*>(d), static_cast<std::streamsize>(n));
}

struct Args {
    std::map<std::string, std::string> one;
    std::vector<std::string> mods, rest;
};
Args parse(int argc, char** argv, int from) {
    Args a;
    for (int i = from; i < argc; ++i) {
        const std::string s = argv[i];
        if (s.rfind("--", 0) == 0) {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "%s needs a value\n", s.c_str());
                std::exit(2);
            }
            if (s == "--mod") a.mods.push_back(argv[++i]);
            else a.one[s.substr(2)] = argv[++i];
        } else {
            a.rest.push_back(s);
        }
    }
    return a;
}
std::string need(const Args& a, const char* k) {
    const auto it = a.one.find(k);
    if (it == a.one.end()) {
        std::fprintf(stderr, "--%s is required\n", k);
        std::exit(2);
    }
    return it->second;
}

struct Tables {
    BlendingTable b;
    GiTable g;
    ParamsTable p;
    Bytes rb, rg, rp;
};
bool load(const Args& a, Tables* t, bool params = true) {
    std::string why;
    t->rb = slurp(need(a, "blend"));
    t->rg = slurp(need(a, "gi"));
    if (!read_blending(t->rb.data(), t->rb.size(), &t->b, &why) || !read_gi(t->rg.data(), t->rg.size(), &t->g, &why)) {
        std::fprintf(stderr, "%s\n", why.c_str());
        return false;
    }
    if (params) {
        t->rp = slurp(need(a, "params"));
        if (!read_params(t->rp.data(), t->rp.size(), &t->p, &why)) {
            std::fprintf(stderr, "%s\n", why.c_str());
            return false;
        }
    }
    return true;
}

size_t first_difference(const Bytes& x, const Bytes& y) {
    const size_t n = std::min(x.size(), y.size());
    for (size_t i = 0; i < n; ++i)
        if (x[i] != y[i]) return i;
    return x.size() == y.size() ? SIZE_MAX : n;
}

int roundtrip(const Args& a) {
    Tables t;
    if (!load(a, &t)) return 1;
    ojson r = ojson::object();
    bool ok = true;
    const auto same = [&](const char* name, const Bytes& game, const Bytes& again) {
        const size_t d = first_difference(game, again);
        ojson o = {{"bytes", game.size()}, {"identical", d == SIZE_MAX}};
        if (d != SIZE_MAX) {
            o["firstDifference"] = d;
            o["writtenBytes"] = again.size();
        }
        ok = ok && d == SIZE_MAX;
        r[name] = std::move(o);
    };
    same("blending", t.rb, write_blending(t.b));
    same("gi", t.rg, write_gi(t.g));
    same("params", t.rp, write_params(t.p));

    // Every slot as parts, put back into a copy: the parts say all of it.
    BlendingTable b = t.b;
    GiTable g = t.g;
    ParamsTable p = t.p;
    uint32_t slots = 0;
    ojson failed = ojson::array();
    for (uint32_t i = 0; i < t.b.layer_count; ++i) {
        Slot s;
        std::string why;
        if (!slot_of(t.b, t.g, t.p, i, &s, &why)) {
            failed.push_back(why);
            continue;
        }
        put_slot(&b, &g, &p, i, s);
        ++slots;
    }
    r["slots"] = slots;
    r["slotErrors"] = failed;
    ok = ok && failed.empty();
    same("blendingBySlot", t.rb, write_blending(b));
    same("giBySlot", t.rg, write_gi(g));
    same("paramsBySlot", t.rp, write_params(p));
    r["layerCount"] = t.b.layer_count;
    r["ok"] = ok;
    std::cout << r.dump(1) << "\n";
    return ok ? 0 : 1;
}

int layouts(const Args& a) {
    Tables t;
    if (!load(a, &t, false)) return 1;
    uint32_t checked = 0;
    ojson bad = ojson::array();
    std::map<std::string, uint32_t> formats;
    const auto check = [&](const char* table, uint32_t slot, const Patch& patch) {
        const kvb::StreamedTexture& x = patch.texture.tex;
        const ChainLayout c = chain_layout(x.width, x.height, x.format);
        std::vector<std::string> wrong;
        if (c.levels != x.levels) wrong.push_back("levels");
        if (c.total != x.content.hash.size) wrong.push_back("size");
        if (c.total - c.tail_start != x.tail.hash.size) wrong.push_back("tail size");
        bool ranges = c.ranges.size() == x.ranges.size();
        for (size_t i = 0; ranges && i < c.ranges.size(); ++i) ranges = c.ranges[i].start == x.ranges[i].start && c.ranges[i].count == x.ranges[i].count;
        if (!ranges) wrong.push_back("mip ranges");
        if (x.content.category != kCategorySurfaceChain) wrong.push_back("content category");
        if (x.tail.category != kvb::kCategoryMipTail || x.tail.bundle != 0 || x.tail.sort != x.tail.hash.h2) wrong.push_back("tail reference");
        ++checked;
        ++formats[std::to_string(x.format)];
        if (!wrong.empty()) {
            std::string w;
            for (const std::string& s : wrong) w += (w.empty() ? "" : ", ") + s;
            bad.push_back(std::string(table) + " slot " + std::to_string(slot) + " " + x.debug_name + ": " + w);
        }
    };
    for (uint32_t s = 0; s < t.b.block.slots.size(); ++s)
        for (const Patch& patch : t.b.block.slots[s])
            if (patch.type == kPatchTexture) check("blending", s, patch);
    for (uint32_t s = 0; s < t.g.block.slots.size(); ++s)
        for (const Patch& patch : t.g.block.slots[s])
            if (patch.type == kPatchTexture) check("gi", s, patch);
    ojson r = {{"textures", checked}, {"formats", formats}, {"disagree", bad.size()}, {"examples", ojson::array()}};
    for (size_t i = 0; i < bad.size() && i < 20; ++i) r["examples"].push_back(bad[i]);
    r["ok"] = bad.empty();
    std::cout << r.dump(1) << "\n";
    return bad.empty() ? 0 : 1;
}

kvb::TextureCache dir_cache(const std::string& dir) {
    kvb::TextureCache c;
    if (dir.empty()) return c;
    const fs::path d = dir;
    c.get = [d](const std::string& key, Bytes* out) {
        const fs::path f = d / (key + ".tex");
        if (!fs::exists(f)) return false;
        *out = slurp(f);
        return true;
    };
    c.put = [d](const std::string& key, const Bytes& data) { spit(d / (key + ".tex"), data.data(), data.size()); };
    return c;
}

// kiln.mod.json, or for a project folder that hasn't been built, the KilnModId of its .csproj (clients "mod").
ojson manifest(const std::string& dir) {
    if (fs::exists(fs::path(dir) / "kiln.mod.json")) return kwt::parse_relaxed(slurp_text(fs::path(dir) / "kiln.mod.json"));
    for (const auto& e : fs::directory_iterator(dir)) {
        if (e.path().extension() != ".csproj") continue;
        const std::string text = slurp_text(e.path());
        const size_t at = text.find("<KilnModId>"), end = text.find("</KilnModId>");
        if (at != std::string::npos && end > at) return ojson{{"id", text.substr(at + 11, end - at - 11)}, {"clients", "mod"}};
    }
    return ojson::value_t::discarded;
}

int cook(const Args& a) {
    Tables t;
    if (!load(a, &t)) return 1;
    const fs::path out = need(a, "out");
    ojson names = ojson::object();
    if (a.one.count("names")) names = kwt::parse_relaxed(slurp_text(a.one.at("names")));

    // The mods' blocks, read and given ids by kiln.worldtables' own code.
    std::vector<kwt::Material> live;
    std::vector<kwt::Tweak> tweaks;
    std::vector<std::string> errors;
    for (const std::string& dir : a.mods) {
        const ojson man = manifest(dir);
        if (man.is_discarded() || !man.contains("id")) {
            errors.push_back(dir + ": no kiln.mod.json with an id");
            continue;
        }
        std::vector<fs::path> files;
        for (const auto& e : fs::directory_iterator(fs::path(dir) / "blocks"))
            if (e.path().extension() == ".json") files.push_back(e.path());
        std::sort(files.begin(), files.end());
        // what a block names by "strength" or "surface": any of the mod's files, as at boot
        std::map<std::string, ojson> strengths, surfaces;
        for (const fs::path& f : files) {
            const ojson d = kwt::parse_relaxed(slurp_text(f));
            if (!d.is_object()) continue;
            for (const auto& [section, into] : {std::pair{"strengths", &strengths}, std::pair{"surfaces", &surfaces}})
                if (d.contains(section) && d[section].is_object())
                    for (auto it = d[section].begin(); it != d[section].end(); ++it) into->emplace(it.key(), it.value());
        }
        for (const fs::path& f : files) {
            kwt::ModFile mf;
            mf.strengths = strengths;
            mf.surfaces = surfaces;
            mf.mod = man["id"].get<std::string>();
            mf.dir = dir;
            mf.file = mf.mod + ": blocks/" + f.filename().string();
            mf.clients = man.value("clients", "mod");
            kwt::read_blocks(kwt::parse_relaxed(slurp_text(f)), &mf);
            errors.insert(errors.end(), mf.errors.begin(), mf.errors.end());
            for (auto& m : mf.materials)
                if (!m.terrain) live.push_back(std::move(m));  // a terrain material has no building surface slot
            for (auto& tw : mf.tweaks) tweaks.push_back(std::move(tw));
        }
    }
    std::vector<int> free_ids;
    for (int id = 128 + static_cast<int>(t.b.layer_count); id < 256; ++id) free_ids.push_back(id);
    const kwt::Allocation alloc = kwt::allocate(live, {}, free_ids, {});
    errors.insert(errors.end(), alloc.refused.begin(), alloc.refused.end());

    const auto resolve = [&](const std::string& text) -> int {
        if (text.rfind("Cell", 0) == 0 && text.size() > 4) {
            char* end = nullptr;
            const long v = std::strtol(text.c_str() + 4, &end, 10);
            return *end == 0 && v > 0 && v < 256 ? static_cast<int>(v) : -1;
        }
        if (names.is_object() && names.contains(text) && names[text].is_number_integer()) return names[text].get<int>();
        return -1;
    };
    std::vector<SurfaceJob> jobs;
    ojson ids = ojson::array();
    for (const kwt::Placed& p : alloc.placed) {
        SurfaceJob j;
        j.mod = p.m.mod;
        j.block = p.m.block;
        j.dir = p.m.dir;
        j.label = "block buildingMaterial " + p.m.block + " (mod " + p.m.mod + ")";
        std::memcpy(j.guid.b, p.m.guid.b.data(), 16);
        j.slot = static_cast<uint32_t>(p.cell - 128);
        j.roof = p.m.roof;
        const ojson def = kwt::parse_relaxed(p.m.definition);
        if (def.is_object() && def.contains("surface")) j.surface = def["surface"];
        j.icon = def.is_object() && !def.contains("icon");
        std::memcpy(j.item.b, p.m.item_guid.b.data(), 16);
        jobs.push_back(std::move(j));
        ids.push_back({{"mod", p.m.mod}, {"block", p.m.block}, {"cellId", p.cell}, {"guid", p.m.guid.text()}});
    }
    for (const kwt::Tweak& tw : tweaks) {
        const ojson def = kwt::parse_relaxed(tw.definition);
        if (!def.is_object() || !def.contains("surface")) continue;
        const int cell = resolve(tw.material);
        const std::string label = "block materialTweak " + tw.block + " (mod " + tw.mod + ")";
        if (cell < 128) {
            errors.push_back(label + ", material: " + (cell < 0 ? "the game has no material \"" + tw.material + "\"" : "a terrain material has no building surface"));
            continue;
        }
        SurfaceJob j;
        j.mod = tw.mod;
        j.block = tw.block;
        for (const std::string& dir : a.mods) {
            const ojson man = manifest(dir);
            if (man.is_object() && man.value("id", "") == tw.mod) j.dir = dir;
        }
        j.label = label;
        const kwt::Guid g = kwt::derive_guid(tw.mod, "materialtweak", tw.block);
        std::memcpy(j.guid.b, g.b.data(), 16);
        j.slot = static_cast<uint32_t>(cell - 128);
        j.tweak = true;
        j.surface = def["surface"];
        jobs.push_back(std::move(j));
    }

    WriteOptions opt;
    opt.load = [](const std::string& dir, const std::string& rel, kvb::RawImage* img, std::string* why) {
        const fs::path f = fs::path(dir) / rel;
        if (!fs::exists(f)) {
            *why = "no such file";
            return false;
        }
        return kvb::decode_png(slurp(f), kvb::kMaxMaterialTextureSide, img, why);
    };
    opt.cache = dir_cache(a.one.count("cache") ? a.one.at("cache") : "");
    opt.game_material = resolve;
    if (a.one.count("blobs")) {
        const fs::path dir = a.one.at("blobs");
        opt.read_blob = [dir](const kvb::ContentRef& r, Bytes* out) {
            const fs::path f = dir / (kvb::content_hash_text(r.hash) + ".bin");
            if (!fs::exists(f)) return false;
            *out = slurp(f);
            return true;
        };
    }
    if (a.one.count("room")) opt.streamed_room = static_cast<uint32_t>(std::stoul(a.one.at("room")));

    WriteResult w;
    const bool ran = write_surfaces(t.b, t.g, t.p, jobs, opt, &w);
    errors.insert(errors.end(), w.errors.begin(), w.errors.end());
    ojson report = report_json(w);
    report["errors"] = errors;
    report["ids"] = ids;
    if (ran) {
        const Bytes b = write_blending(w.blend), g = write_gi(w.gi), p = write_params(w.params);
        spit(out / "blending.bin", b.data(), b.size());
        spit(out / "gi.bin", g.data(), g.size());
        spit(out / "params.bin", p.data(), p.size());
        for (const ServedBlob& blob : w.blobs) spit(out / "blobs" / (kvb::content_hash_text(blob.hash) + ".bin"), blob.data.data(), blob.data.size());
        for (const BuiltIcon& icon : w.icons) spit(out / "icons" / (icon.mod + "." + icon.block + ".png"), icon.png.data(), icon.png.size());
    }
    report["ok"] = ran && errors.empty();
    const std::string text = report.dump(1);
    spit(out / "report.json", text.data(), text.size());
    std::cout << text << "\n";
    return ran && errors.empty() ? 0 : 1;
}

int check(const Args& a) {
    if (a.rest.empty()) {
        std::fprintf(stderr, "check JSON\n");
        return 2;
    }
    const ojson j = kwt::parse_relaxed(a.rest[0]);
    std::vector<std::string> errors;
    if (j.is_discarded()) errors.push_back("not valid JSON");
    else check_surface(j, a.one.count("label") ? a.one.at("label") : "block x", &errors, a.one.count("tweak") && a.one.at("tweak") == "1");
    ojson r = {{"ok", errors.empty()}, {"errors", errors}};
    if (errors.empty()) r["normal"] = normal_surface(j, a.one.count("tweak") && a.one.at("tweak") == "1");
    std::cout << r.dump(1) << "\n";
    return errors.empty() ? 0 : 1;
}

int hash(const Args& a) {
    if (a.rest.empty()) return 2;
    const Bytes b = slurp(a.rest[0]);
    const size_t from = a.one.count("from") ? std::stoull(a.one.at("from")) : 0;
    if (from > b.size()) return 2;
    std::cout << kvb::content_hash_text(kvb::content_hash(b.data() + from, b.size() - from)) << "\n";
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "kiln-surfacecook roundtrip | layouts | cook | check | hash: see the top of tools/surfacecook/native/src/main.cpp\n");
        return 2;
    }
    const std::string cmd = argv[1];
    const Args a = parse(argc, argv, 2);
    try {
        if (cmd == "roundtrip") return roundtrip(a);
        if (cmd == "layouts") return layouts(a);
        if (cmd == "cook") return cook(a);
        if (cmd == "check") return check(a);
        if (cmd == "hash") return hash(a);
    } catch (const std::exception& e) {
        std::fprintf(stderr, "%s\n", e.what());
        return 1;
    }
    std::fprintf(stderr, "unknown command %s\n", cmd.c_str());
    return 2;
}
