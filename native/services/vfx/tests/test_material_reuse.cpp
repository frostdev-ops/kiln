// SPDX-License-Identifier: MIT
// kiln-vfx's define_json takes a material the boot build already built (same mod, block, definition, and file bytes) from the boot's
// record instead of decoding its textures again (kvb::MaterialRecord, CookOptions::known_material). This test counts full builds and
// checks that every case reports exactly what a cook without records reports (today's define_json):
//   unchanged: 0 builds; an edited PNG, a missing PNG, a changed definition, a new definition: a full build each, same report.
// With a mod folder as argument it also times define_json's cook for every material of that mod, with and without the records:
//   native/services/vfx/tests/run_material_reuse.sh [mod folder with blocks/*.materials.json]
#include <unistd.h>

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <map>
#include <string>
#include <vector>

#include "../cook/vfxcook.h"

namespace fs = std::filesystem;
using kvb::ojson;

static int failures = 0;
#define CHECK(cond)                                                              \
    do {                                                                         \
        if (!(cond)) {                                                           \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            ++failures;                                                          \
        }                                                                        \
    } while (0)

// ---- a PNG writer (stored deflate blocks): RGBA, w x h ----
static uint32_t crc32(const uint8_t* p, size_t n, uint32_t c = 0xffffffffu) {
    for (size_t i = 0; i < n; ++i) {
        c ^= p[i];
        for (int k = 0; k < 8; ++k) c = (c >> 1) ^ (0xedb88320u & (0u - (c & 1u)));
    }
    return c;
}
static void be32(std::vector<uint8_t>& v, uint32_t x) {
    for (int i = 3; i >= 0; --i) v.push_back(static_cast<uint8_t>(x >> (8 * i)));
}
static void chunk(std::vector<uint8_t>& out, const char* type, const std::vector<uint8_t>& data) {
    be32(out, static_cast<uint32_t>(data.size()));
    std::vector<uint8_t> td(type, type + 4);
    td.insert(td.end(), data.begin(), data.end());
    out.insert(out.end(), td.begin(), td.end());
    be32(out, crc32(td.data(), td.size()) ^ 0xffffffffu);
}
static void write_png(const fs::path& file, uint32_t w, uint32_t h, uint8_t seed) {
    std::vector<uint8_t> raw;
    for (uint32_t y = 0; y < h; ++y) {
        raw.push_back(0);
        for (uint32_t x = 0; x < w; ++x)
            for (int c = 0; c < 4; ++c) raw.push_back(c == 3 ? 255 : static_cast<uint8_t>(x * 7 + y * 3 + c * 50 + seed));
    }
    std::vector<uint8_t> z = {0x78, 0x01};
    uint32_t a = 1, b = 0;
    for (uint8_t v : raw) a = (a + v) % 65521, b = (b + a) % 65521;
    for (size_t i = 0; i < raw.size(); i += 65535) {
        const size_t n = std::min<size_t>(65535, raw.size() - i);
        z.push_back(i + n == raw.size() ? 1 : 0);
        z.push_back(static_cast<uint8_t>(n));
        z.push_back(static_cast<uint8_t>(n >> 8));
        z.push_back(static_cast<uint8_t>(~n));
        z.push_back(static_cast<uint8_t>(~n >> 8));
        z.insert(z.end(), raw.begin() + static_cast<long>(i), raw.begin() + static_cast<long>(i + n));
    }
    be32(z, (b << 16) | a);
    std::vector<uint8_t> png = {0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a};
    std::vector<uint8_t> ihdr;
    be32(ihdr, w);
    be32(ihdr, h);
    ihdr.insert(ihdr.end(), {8, 6, 0, 0, 0});
    chunk(png, "IHDR", ihdr);
    chunk(png, "IDAT", z);
    chunk(png, "IEND", {});
    std::ofstream(file, std::ios::binary).write(reinterpret_cast<const char*>(png.data()), static_cast<std::streamsize>(png.size()));
}

// ---- the two cooks: kiln-vfx's boot build (records) and define_json's (one material, with or without the records) ----
struct Records {
    std::map<std::pair<std::string, std::string>, kvb::MaterialRecord> by_block;
};

static kvb::TextureCache memory_cache(std::map<std::string, kvb::Bytes>* store) {
    kvb::TextureCache c;
    c.get = [store](const std::string& k, kvb::Bytes* out) {
        const auto it = store->find(k);
        if (it == store->end()) return false;
        *out = it->second;
        return true;
    };
    c.put = [store](const std::string& k, const kvb::Bytes& d) { (*store)[k] = d; };
    return c;
}

static kvb::CookResult boot(const kvb::ModInput& mod, Records* recs, std::map<std::string, kvb::Bytes>* cache) {
    kvb::CookOptions opt;
    opt.cache = memory_cache(cache);
    opt.record_material = [recs](const kvb::MaterialRecord& r) { recs->by_block[{r.mod, r.name}] = r; };
    return kvb::cook_mods({mod}, opt);
}

// define_json's cook of one material block from code ({"format": 1, "materials": {name: definition}})
static kvb::CookResult define(const std::string& id, const std::string& dir, const std::string& name, const ojson& definition, const Records* recs,
                              std::map<std::string, kvb::Bytes>* cache) {
    kvb::ModInput m;
    m.id = id;
    m.dir = dir;
    m.side = "client";
    ojson only = ojson::object();
    only["format"] = 1;
    only["materials"] = ojson::object();
    only["materials"][name] = definition;
    m.documents.emplace_back("code", only.dump());
    kvb::CookOptions opt;
    opt.cache = memory_cache(cache);
    if (recs)
        opt.known_material = [recs, copy = kvb::MaterialRecord{}](const std::string& mod, const std::string& n, uint64_t def) mutable -> const kvb::MaterialRecord* {
            const auto it = recs->by_block.find({mod, n});
            if (it == recs->by_block.end() || it->second.definition != def) return nullptr;
            copy = it->second;
            return &copy;
        };
    return kvb::cook_mods({m}, opt);
}

static std::string report(const kvb::CookResult& r) { return kvb::report_json(r).dump(); }

// One define with the records and one without (today's): the same report, and the builds counted.
static void same_as_today(const char* what, const std::string& dir, const std::string& name, const ojson& def, const Records& recs,
                          std::map<std::string, kvb::Bytes>* cache, uint32_t want_built, uint32_t want_reused) {
    const kvb::CookResult with = define("test-mod", dir, name, def, &recs, cache);
    const kvb::CookResult today = define("test-mod", dir, name, def, nullptr, cache);
    const bool same = report(with) == report(today);
    const bool counted = with.materials_built == want_built && with.materials_reused == want_reused;
    std::printf("%-44s builds %u, reused %u, report %s today's (errors %zu)\n", what, with.materials_built, with.materials_reused,
                same ? "==" : "!=", with.errors.size());
    CHECK(same);
    CHECK(counted);
    CHECK(today.materials_built == (today.errors.empty() ? 1u : 0u) && today.materials_reused == 0);
}

static int timed(const fs::path& modDir) {
    // every blocks/*.materials.json of the mod: the boot build of them all, then define_json's cook of each block on its own
    kvb::ModInput mod;
    mod.id = "kiln-equipment";
    mod.dir = modDir.string();
    mod.side = "client";
    std::vector<std::pair<std::string, ojson>> blocks;
    for (const auto& e : fs::directory_iterator(modDir / "blocks")) {
        const std::string n = e.path().filename().string();
        if (n.size() < 15 || n.substr(n.size() - 15) != ".materials.json") continue;
        std::ifstream f(e.path(), std::ios::binary);
        const std::string text((std::istreambuf_iterator<char>(f)), {});
        mod.documents.emplace_back("blocks/" + n, text);
        const ojson doc = kvb::parse_relaxed(text);
        for (auto it = doc["materials"].begin(); it != doc["materials"].end(); ++it) blocks.emplace_back(it.key(), it.value());
    }
    Records recs;
    std::map<std::string, kvb::Bytes> cache;
    const auto t0 = std::chrono::steady_clock::now();
    const kvb::CookResult b = boot(mod, &recs, &cache);
    const auto t1 = std::chrono::steady_clock::now();
    double with_ms = 0, today_ms = 0;
    uint32_t built = 0, reused = 0, differ = 0;
    for (const auto& [name, def] : blocks) {
        const auto a = std::chrono::steady_clock::now();
        const kvb::CookResult r1 = define(mod.id, mod.dir, name, def, &recs, &cache);
        const auto m = std::chrono::steady_clock::now();
        const kvb::CookResult r2 = define(mod.id, mod.dir, name, def, nullptr, &cache);
        const auto z = std::chrono::steady_clock::now();
        with_ms += std::chrono::duration<double, std::milli>(m - a).count();
        today_ms += std::chrono::duration<double, std::milli>(z - m).count();
        built += r1.materials_built;
        reused += r1.materials_reused;
        if (report(r1) != report(r2)) ++differ;
    }
    std::printf("%s: boot build of %zu materials (%u built, %zu records) %.0f ms (textures encoded, cache cold)\n", mod.id.c_str(), blocks.size(),
                b.materials_built, recs.by_block.size(), std::chrono::duration<double, std::milli>(t1 - t0).count());
    std::printf("define_json's cook of each of the %zu materials, the texture cache warm: today %.0f ms (%.1f ms each), with the boot's records %.0f ms "
                "(%.1f ms each): %u built, %u reused, %u report(s) differ\n",
                blocks.size(), today_ms, today_ms / static_cast<double>(blocks.size()), with_ms, with_ms / static_cast<double>(blocks.size()), built, reused,
                differ);
    CHECK(differ == 0 && built == 0 && reused == blocks.size());
    return failures ? 1 : 0;
}

int main(int argc, char** argv) {
    std::setvbuf(stdout, nullptr, _IOLBF, 0);
    if (argc > 1) return timed(argv[1]);

    const char* tmp = std::getenv("TMPDIR");
    const fs::path dir = fs::path(tmp && *tmp ? tmp : "/tmp") / ("kiln-material-reuse-" + std::to_string(getpid()));
    fs::create_directories(dir / "tex");
    write_png(dir / "tex/a.png", 64, 64, 1);
    write_png(dir / "tex/b.png", 32, 32, 2);
    write_png(dir / "tex/c.png", 64, 64, 3);
    const ojson m1 = kvb::parse_relaxed(R"({"albedo": "tex/a.png", "roughness": 0.5})");
    const ojson m2 = kvb::parse_relaxed(R"({"albedo": "tex/b.png", "normal": {"map": "tex/a.png", "strength": 2}, "metallic": 1})");
    const ojson m3 = kvb::parse_relaxed(R"({"tint": "#336699"})");
    const ojson m5 = kvb::parse_relaxed(R"({"albedo": "tex/c.png"})");
    ojson doc = ojson::object();
    doc["format"] = 1;
    doc["materials"] = ojson::object();
    doc["materials"]["m1"] = m1;
    doc["materials"]["m2"] = m2;
    doc["materials"]["m3"] = m3;
    doc["materials"]["m5"] = m5;
    kvb::ModInput mod;
    mod.id = "test-mod";
    mod.dir = dir.string();
    mod.side = "client";
    mod.documents.emplace_back("blocks/m.materials.json", doc.dump(2));

    Records recs;
    std::map<std::string, kvb::Bytes> cache;
    const kvb::CookResult b = boot(mod, &recs, &cache);
    CHECK(b.errors.empty() && b.materials_built == 4 && b.materials_reused == 0 && recs.by_block.size() == 4);
    const auto key = [](const char* n) { return std::make_pair(std::string("test-mod"), std::string(n)); };
    CHECK((recs.by_block[key("m2")].files.size() == 2 && recs.by_block[key("m3")].files.empty()));

    same_as_today("unchanged (a file)", dir.string(), "m1", m1, recs, &cache, 0, 1);
    same_as_today("unchanged (two files, one shared)", dir.string(), "m2", m2, recs, &cache, 0, 1);
    same_as_today("unchanged (no file)", dir.string(), "m3", m3, recs, &cache, 0, 1);
    same_as_today("the same again (a second world)", dir.string(), "m1", m1, recs, &cache, 0, 1);
    // a definition changed in code: its hash differs
    ojson m1b = m1;
    m1b["roughness"] = 0.25;
    same_as_today("a changed definition", dir.string(), "m1", m1b, recs, &cache, 1, 0);
    // a block the boot build never saw
    same_as_today("a new definition", dir.string(), "m4", kvb::parse_relaxed(R"({"albedo": "tex/b.png"})"), recs, &cache, 1, 0);
    // a reloaded pack from another folder with the same bytes: the same build, so the record is good
    fs::create_directories(dir / "copy/tex");
    fs::copy_file(dir / "tex/a.png", dir / "copy/tex/a.png");
    same_as_today("the same bytes in another folder", (dir / "copy").string(), "m1", m1, recs, &cache, 0, 1);
    // a PNG edited after boot (same size, other pixels)
    write_png(dir / "tex/a.png", 64, 64, 9);
    same_as_today("an edited PNG", dir.string(), "m1", m1, recs, &cache, 1, 0);
    same_as_today("an edited PNG another block shares", dir.string(), "m2", m2, recs, &cache, 1, 0);
    // a PNG gone after boot: today's error, the same
    fs::remove(dir / "tex/c.png");
    same_as_today("a missing PNG", dir.string(), "m5", m5, recs, &cache, 0, 0);
    // a PNG turned into something that is not a PNG: today's error
    std::ofstream(dir / "tex/b.png", std::ios::binary) << "not a png";
    same_as_today("a PNG that no longer decodes", dir.string(), "m4", kvb::parse_relaxed(R"({"albedo": "tex/b.png"})"), recs, &cache, 0, 0);

    fs::remove_all(dir);
    if (failures) {
        std::fprintf(stderr, "%d check(s) failed\n", failures);
        return 1;
    }
    std::printf("test_material_reuse: all checks passed\n");
    return 0;
}
