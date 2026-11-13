// SPDX-License-Identifier: MIT
// Where kiln-vfx's boot build (build_on_ready's cook_mods) spends its time, on a copy of a mod: the parts the cook can be timed by from
// outside (file reads, the texture cache) and, for every material texture, PNG decode and pixel hashing on their own. Cold (empty
// cache folder) and warm (the cache the cold run wrote), the cache on disk in the format of boot.cpp's disk_cache.
//   measure_boot <mod folder> <cache folder>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <set>
#include <string>

#include "../cook/vfxcook.h"

namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;
static double ms(Clock::duration d) { return std::chrono::duration<double, std::milli>(d).count(); }

struct Timers {
    double read = 0, cache_get = 0, cache_put = 0;
    uint64_t read_bytes = 0, reads = 0, hits = 0, misses = 0, puts = 0, cache_bytes = 0;
};

static bool read_file(const std::string& p, kvb::Bytes* out, std::string* why) {
    std::ifstream f(p, std::ios::binary | std::ios::ate);
    if (!f) {
        *why = "not there";
        return false;
    }
    out->resize(static_cast<size_t>(f.tellg()));
    f.seekg(0);
    f.read(reinterpret_cast<char*>(out->data()), static_cast<std::streamsize>(out->size()));
    return static_cast<bool>(f);
}

// boot.cpp's disk_cache, with timers
static kvb::TextureCache disk_cache(const fs::path& dir, Timers* t) {
    kvb::TextureCache c;
    c.get = [dir, t](const std::string& key, kvb::Bytes* out) {
        const auto a = Clock::now();
        kvb::Bytes raw;
        std::string why;
        bool ok = read_file((dir / (key + ".tex")).string(), &raw, &why) && raw.size() >= 16;
        if (ok) {
            kvb::ContentHash want;
            std::memcpy(&want, raw.data(), 16);
            ok = want == kvb::content_hash(raw.data() + 16, raw.size() - 16);
            if (ok) out->assign(raw.begin() + 16, raw.end()), t->cache_bytes += raw.size();
        }
        t->cache_get += ms(Clock::now() - a);
        ++(ok ? t->hits : t->misses);
        return ok;
    };
    c.put = [dir, t](const std::string& key, const kvb::Bytes& chain) {
        const auto a = Clock::now();
        fs::create_directories(dir);
        std::ofstream f(dir / (key + ".tex"), std::ios::binary);
        const kvb::ContentHash h = kvb::content_hash(chain.data(), chain.size());
        f.write(reinterpret_cast<const char*>(&h), 16);
        f.write(reinterpret_cast<const char*>(chain.data()), static_cast<std::streamsize>(chain.size()));
        t->cache_put += ms(Clock::now() - a);
        ++t->puts;
    };
    return c;
}

static kvb::ModInput read_mod(const fs::path& dir) {
    kvb::ModInput m;
    m.id = "kiln-equipment";
    m.dir = dir.string();
    m.side = "client";
    std::vector<fs::path> files;
    for (const auto& e : fs::directory_iterator(dir / "blocks"))
        if (e.path().extension() == ".json") files.push_back(e.path());
    std::sort(files.begin(), files.end());
    for (const auto& f : files) {
        std::ifstream in(f, std::ios::binary);
        m.documents.emplace_back("blocks/" + f.filename().string(), std::string((std::istreambuf_iterator<char>(in)), {}));
    }
    return m;
}

int main(int argc, char** argv) {
    if (argc < 3) return 2;
    const fs::path mod_dir = argv[1], cache = argv[2];
    const kvb::ModInput mod = read_mod(mod_dir);
    std::vector<kvb::MaterialRecord> records, carried;
    for (int run = 0; run < 3; ++run) {
        Timers t;
        kvb::CookOptions opt;
        if (run == 2) {
            std::string why;
            kvb::read_carry_index(kvb::carry_index_json({"k", "g", "client"}, kvb::carry_merge(records, {})), {"k", "g", "client"}, &carried, &why);
            opt.carried_material = [&carried](const std::string& m, const std::string& n, const std::string& d) -> const kvb::MaterialRecord* {
                for (const auto& r : carried)
                    if (r.mod == m && r.name == n && r.definition_text == d) return &r;
                return nullptr;
            };
        }
        records.clear();
        opt.record_material = [&records](const kvb::MaterialRecord& r) { records.push_back(r); };
        opt.cache = disk_cache(cache, &t);
        opt.read_file = [&t](const std::string& p, kvb::Bytes* out, std::string* why) {
            const auto a = Clock::now();
            const bool ok = read_file(p, out, why);
            t.read += ms(Clock::now() - a);
            ++t.reads;
            if (ok) t.read_bytes += out->size();
            return ok;
        };
        opt.game_has_blob = [](const kvb::ContentHash&) { return true; };
        const auto a = Clock::now();
        const kvb::CookResult r = kvb::cook_mods({mod}, opt);
        const double total = ms(Clock::now() - a);
        std::printf("%s: cook %.0f ms (%u carried): %u materials, %zu VFX, %zu errors; reads %llu (%.0f MB) %.0f ms; cache get %.0f ms (%llu hits, %llu misses, %.0f MB), "
                    "put %.0f ms (%llu); the rest (decode, pixel hashes, BC7 encode on a miss, building) %.0f ms\n",
                    run == 2 ? "warm, with the carry-over index" : run ? "warm" : "cold", total, r.materials_carried, r.used_materials, r.blocks.size(), r.errors.size(), static_cast<unsigned long long>(t.reads),
                    static_cast<double>(t.read_bytes) / 1e6, t.read, t.cache_get, static_cast<unsigned long long>(t.hits), static_cast<unsigned long long>(t.misses),
                    static_cast<double>(t.cache_bytes) / 1e6, t.cache_put, static_cast<unsigned long long>(t.puts), total - t.read - t.cache_get - t.cache_put);
        for (const auto& e : r.errors) std::printf("  error: %s\n", e.c_str());
    }
    // every PNG a material names, on its own: decode and the pixel hash (what encode_chain keys the cache by)
    std::set<std::string> pngs;
    for (const auto& [file, text] : mod.documents) {
        const auto doc = kvb::parse_relaxed(text);
        if (!doc.is_object() || !doc.contains("materials")) continue;
        for (auto it = doc["materials"].begin(); it != doc["materials"].end(); ++it)
            for (const char* k : {"albedo", "normal", "params", "metallicRoughness", "occlusion", "emissive"}) {
                if (!it.value().contains(k)) continue;
                const auto& v = it.value()[k];
                if (v.is_string()) pngs.insert(v.get<std::string>());
                else if (v.is_object() && v.contains("map") && v["map"].is_string()) pngs.insert(v["map"].get<std::string>());
            }
    }
    double rd = 0, file_hash = 0, dec = 0, px_hash = 0;
    uint64_t bytes = 0, px_bytes = 0;
    for (const std::string& rel : pngs) {
        kvb::Bytes png;
        std::string why;
        auto a = Clock::now();
        if (!read_file((mod_dir / rel).string(), &png, &why)) continue;
        rd += ms(Clock::now() - a);
        bytes += png.size();
        a = Clock::now();
        (void)kvb::content_hash(png.data(), png.size());
        file_hash += ms(Clock::now() - a);
        kvb::RawImage img;
        a = Clock::now();
        if (!kvb::decode_png(png, kvb::kMaxMaterialTextureSide, &img, &why)) continue;
        dec += ms(Clock::now() - a);
        px_bytes += img.px.size();
        a = Clock::now();
        (void)kvb::content_hash(img.px.data(), img.px.size());
        px_hash += ms(Clock::now() - a);
    }
    std::printf("materials' %zu PNGs on their own: read %.0f ms (%.0f MB), hash of the file bytes %.0f ms, decode %.0f ms, pixel hash %.0f ms (%.0f MB of pixels)\n",
                pngs.size(), rd, static_cast<double>(bytes) / 1e6, file_hash, dec, px_hash, static_cast<double>(px_bytes) / 1e6);
    return 0;
}
