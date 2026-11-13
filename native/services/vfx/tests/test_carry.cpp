// SPDX-License-Identifier: MIT
// kiln-vfx's carry-over index (cook/carry.cpp, CookOptions::carried_material): a start whose mods did not change takes every material's
// maps from the texture cache instead of decoding its textures, and gives bit for bit what a start without the index gives: every blob
// and resource handed to kiln.assets, the report, the GUIDs, the budget. Each invalidation builds what it must in full (counted), and a
// bad index is never used.
//   native/services/vfx/tests/run_carry.sh [mod folder]   (a mod folder: the owner's shape, cold and warm, timed; else a small mod)
#include <unistd.h>

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <string>
#include <vector>

#include "../cook/vfxcook.h"

namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;
using kvb::ojson;

static int failures = 0;
#define CHECK(cond)                                                              \
    do {                                                                         \
        if (!(cond)) {                                                           \
            std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); \
            ++failures;                                                          \
        }                                                                        \
    } while (0)

static double ms(Clock::duration d) { return std::chrono::duration<double, std::milli>(d).count(); }

static bool read_file(const std::string& p, kvb::Bytes* out, std::string* why) {
    std::ifstream f(p, std::ios::binary | std::ios::ate);
    if (!f) return *why = "not there", false;
    out->resize(static_cast<size_t>(f.tellg()));
    f.seekg(0);
    f.read(reinterpret_cast<char*>(out->data()), static_cast<std::streamsize>(out->size()));
    return static_cast<bool>(f);
}

// boot.cpp's disk_cache: <dir>/<key>.tex = {ContentHash of the chain, the chain}; an entry that doesn't check out is a miss
static kvb::TextureCache disk_cache(const fs::path& dir) {
    kvb::TextureCache c;
    c.get = [dir](const std::string& key, kvb::Bytes* out) {
        kvb::Bytes raw;
        std::string why;
        if (!read_file((dir / (key + ".tex")).string(), &raw, &why) || raw.size() < 16) return false;
        kvb::ContentHash want;
        std::memcpy(&want, raw.data(), 16);
        if (!(want == kvb::content_hash(raw.data() + 16, raw.size() - 16))) return false;
        out->assign(raw.begin() + 16, raw.end());
        return true;
    };
    c.put = [dir](const std::string& key, const kvb::Bytes& chain) {
        fs::create_directories(dir);
        std::ofstream f(dir / (key + ".tex"), std::ios::binary);
        const kvb::ContentHash h = kvb::content_hash(chain.data(), chain.size());
        f.write(reinterpret_cast<const char*>(&h), 16);
        f.write(reinterpret_cast<const char*>(chain.data()), static_cast<std::streamsize>(chain.size()));
    };
    return c;
}

static kvb::ModInput read_mod(const std::string& id, const fs::path& dir) {
    kvb::ModInput m;
    m.id = id;
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

static const kvb::CarryHeader kHeader{"0.2.0-dev.27+20261113.test", "5d9ef2f1", "client"};

// One start as build_on_ready runs it: the index (if any) in, the cook, the merged index out.
struct Start {
    kvb::CookResult r;
    std::string index_out;
    std::string index_why;
    double ms = 0;
};
static Start start(const kvb::ModInput& mod, const fs::path& cache, const std::string* index_in, const kvb::CarryHeader& header = kHeader) {
    Start s;
    kvb::CookOptions opt;
    opt.cache = disk_cache(cache);
    opt.read_file = read_file;
    opt.game_has_blob = [](const kvb::ContentHash&) { return true; };
    std::vector<kvb::MaterialRecord> records, carried;
    std::map<std::pair<std::string, std::string>, size_t> at;
    opt.record_material = [&records](const kvb::MaterialRecord& r) { records.push_back(r); };
    if (index_in) {
        if (kvb::read_carry_index(*index_in, header, &carried, &s.index_why)) {
            for (size_t i = 0; i < carried.size(); ++i) at[{carried[i].mod, carried[i].name}] = i;
            opt.carried_material = [&](const std::string& m, const std::string& n, const std::string& def) -> const kvb::MaterialRecord* {
                const auto it = at.find({m, n});
                return it != at.end() && carried[it->second].definition_text == def ? &carried[it->second] : nullptr;
            };
        }
    }
    const auto a = Clock::now();
    s.r = kvb::cook_mods({mod}, opt);
    s.ms = ms(Clock::now() - a);
    s.index_out = kvb::carry_index_json(header, kvb::carry_merge(records, carried));
    return s;
}

// Everything kiln-vfx hands on or prints from a cook: the blobs and resources for kiln.assets, the built blocks and materials, the report
// (GUIDs, budget lines, errors, warnings, notes, refusals). Not the counters of how a material was made.
static bool same_output(const kvb::CookResult& a, const kvb::CookResult& b, std::string* where) {
    if (a.blobs.size() != b.blobs.size()) return *where = "blob count", false;
    for (size_t i = 0; i < a.blobs.size(); ++i)
        if (!(a.blobs[i].hash == b.blobs[i].hash) || a.blobs[i].data != b.blobs[i].data || a.blobs[i].what != b.blobs[i].what)
            return *where = "blob " + a.blobs[i].what, false;
    if (a.resources.size() != b.resources.size()) return *where = "resource count", false;
    for (size_t i = 0; i < a.resources.size(); ++i) {
        const auto &x = a.resources[i], &y = b.resources[i];
        if (!(x.guid == y.guid) || x.type != y.type || x.data != y.data || x.what != y.what || x.override != y.override ||
            x.mod != y.mod || x.standby != y.standby)
            return *where = "resource " + x.what, false;
    }
    if (kvb::report_json(a).dump() != kvb::report_json(b).dump()) return *where = "report", false;
    for (size_t i = 0; i < a.materials.size(); ++i)
        if (a.materials[i].definition != b.materials[i].definition) return *where = "material definitions", false;
    return true;
}

// ---- a PNG writer (stored deflate blocks), RGBA ----
static uint32_t crc32(const uint8_t* p, size_t n) {
    uint32_t c = 0xffffffffu;
    for (size_t i = 0; i < n; ++i) {
        c ^= p[i];
        for (int k = 0; k < 8; ++k) c = (c >> 1) ^ (0xedb88320u & (0u - (c & 1u)));
    }
    return c ^ 0xffffffffu;
}
static void be32(std::vector<uint8_t>& v, uint32_t x) {
    for (int i = 3; i >= 0; --i) v.push_back(static_cast<uint8_t>(x >> (8 * i)));
}
static void chunk(std::vector<uint8_t>& out, const char* type, const std::vector<uint8_t>& data) {
    be32(out, static_cast<uint32_t>(data.size()));
    std::vector<uint8_t> td(type, type + 4);
    td.insert(td.end(), data.begin(), data.end());
    out.insert(out.end(), td.begin(), td.end());
    be32(out, crc32(td.data(), td.size()));
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
        z.insert(z.end(), {static_cast<uint8_t>(i + n == raw.size()), static_cast<uint8_t>(n), static_cast<uint8_t>(n >> 8), static_cast<uint8_t>(~n),
                           static_cast<uint8_t>(~n >> 8)});
        z.insert(z.end(), raw.begin() + static_cast<long>(i), raw.begin() + static_cast<long>(i + n));
    }
    be32(z, (b << 16) | a);
    std::vector<uint8_t> png = {0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a}, ihdr;
    be32(ihdr, w);
    be32(ihdr, h);
    ihdr.insert(ihdr.end(), {8, 6, 0, 0, 0});
    chunk(png, "IHDR", ihdr);
    chunk(png, "IDAT", z);
    chunk(png, "IEND", {});
    std::ofstream(file, std::ios::binary).write(reinterpret_cast<const char*>(png.data()), static_cast<std::streamsize>(png.size()));
}

static void write_blocks(const fs::path& dir, const ojson& materials) {
    ojson doc = ojson::object();
    doc["format"] = 1;
    doc["materials"] = materials;
    std::ofstream(dir / "blocks" / "m.materials.json", std::ios::binary) << doc.dump(2);
}

// A start with the index against a start without it, the same cache state before each: same output; the counts as given.
static void case_(const char* what, const kvb::ModInput& mod, const fs::path& cache, const std::string* index, uint32_t want_carried, uint32_t want_built,
                  std::string* index_out = nullptr) {
    const Start with = start(mod, cache, index);
    const Start without = start(mod, cache, nullptr);
    std::string where;
    const bool same = same_output(with.r, without.r, &where);
    std::printf("%-58s carried %u, built %u, output %s%s%s\n", what, with.r.materials_carried, with.r.materials_built, same ? "== without the index" : "DIFFERS: ",
                same ? "" : where.c_str(), with.index_why.empty() ? "" : (" (index not used: " + with.index_why + ")").c_str());
    CHECK(same);
    CHECK(with.r.materials_carried == want_carried && with.r.materials_built == want_built);
    if (index_out) *index_out = with.index_out;
}

static int owners_shape(const fs::path& mod_dir, const fs::path& cache) {
    const kvb::ModInput mod = read_mod("kiln-equipment", mod_dir);
    fs::remove_all(cache);
    const Start cold = start(mod, cache, nullptr);          // the first start: no cache, no index
    const Start warm = start(mod, cache, nullptr);          // today's every later start: the cache, no index
    const Start carried = start(mod, cache, &cold.index_out);  // with the index the first start wrote
    std::string where;
    const bool same = same_output(carried.r, warm.r, &where);
    const bool same_cold = same_output(carried.r, cold.r, &where);
    std::printf("kiln-equipment: cold %.0f ms (%u built), warm without the index %.0f ms (%u built), warm with the index %.0f ms (%u carried, %u built); "
                "%zu blobs (%.0f MB), %zu resources, the report: %s; against the cold start: %s; index %zu bytes\n",
                cold.ms, cold.r.materials_built, warm.ms, warm.r.materials_built, carried.ms, carried.r.materials_carried, carried.r.materials_built,
                carried.r.blobs.size(), [&] { double b = 0; for (const auto& x : carried.r.blobs) b += static_cast<double>(x.data.size()); return b / 1e6; }(),
                carried.r.resources.size(), same ? "bit for bit the same" : ("DIFFERS at " + where).c_str(), same_cold ? "the same" : "DIFFERS",
                cold.index_out.size());
    std::printf("budget: materials %u+%u, streamed textures %u+%u, VFX %u+%u, meshes %u+%u (with the index: the same: %s)\n", carried.r.game.materials,
                carried.r.used_materials, carried.r.game.streamed_textures, carried.r.used_streamed_textures, carried.r.game.vfx, carried.r.used_vfx,
                carried.r.game.meshes, carried.r.used_meshes,
                carried.r.used_materials == warm.r.used_materials && carried.r.used_streamed_textures == warm.r.used_streamed_textures ? "yes" : "NO");
    CHECK(same && same_cold);
    CHECK(carried.r.materials_carried == 85 && carried.r.materials_built == 0);
    return failures ? 1 : 0;
}

int main(int argc, char** argv) {
    std::setvbuf(stdout, nullptr, _IOLBF, 0);
    const char* tmp = std::getenv("TMPDIR");
    const fs::path root = fs::path(tmp && *tmp ? tmp : "/tmp") / ("kiln-carry-" + std::to_string(getpid()));
    if (argc > 1) {
        const int rc = owners_shape(argv[1], root / "cache");
        fs::remove_all(root);
        return rc;
    }
    const fs::path dir = root / "mod", cache = root / "cache";
    fs::create_directories(dir / "blocks");
    fs::create_directories(dir / "tex");
    write_png(dir / "tex/a.png", 64, 64, 1);
    write_png(dir / "tex/b.png", 32, 32, 2);
    write_png(dir / "tex/c.png", 64, 64, 3);
    ojson mats = ojson::object();
    mats["m1"] = kvb::parse_relaxed(R"({"albedo": "tex/a.png", "roughness": 0.5})");
    mats["m2"] = kvb::parse_relaxed(R"({"albedo": "tex/b.png", "normal": {"map": "tex/a.png", "strength": 2}, "metallic": 1})");
    mats["m3"] = kvb::parse_relaxed(R"({"tint": "#336699"})");
    mats["m4"] = kvb::parse_relaxed(R"({"albedo": "tex/c.png", "emissive": {"map": "tex/c.png", "color": "#ffffff", "strength": 2}, "kind": "emissive"})");
    write_blocks(dir, mats);
    kvb::ModInput mod = read_mod("test-mod", dir);

    std::string index;
    case_("the first start (no index)", mod, cache, nullptr, 0, 4, &index);
    case_("nothing changed", mod, cache, &index, 4, 0, &index);
    const std::string first_index = index;
    case_("nothing changed, again (the index the last start wrote)", mod, cache, &index, 4, 0, &index);
    CHECK(index == first_index);  // a start that carried everything writes the same index again

    // a bad index is never used, never an error: everything built as without it
    { const std::string cut = index.substr(0, index.size() / 2); case_("index cut short", mod, cache, &cut, 0, 4); }
    const std::string garbage = "\x01\x02 not json";
    case_("index not JSON", mod, cache, &garbage, 0, 4);
    std::string other = index;
    other.replace(other.find("\"format\":1"), 10, "\"format\":2");
    case_("index of another format", mod, cache, &other, 0, 4);
    {
        const Start s = start(mod, cache, &index, kvb::CarryHeader{"0.2.0-dev.28+20261120.abc", kHeader.game, kHeader.side});
        std::printf("%-58s carried %u, built %u (index not used: %s)\n", "the first start after a Kiln update", s.r.materials_carried, s.r.materials_built, s.index_why.c_str());
        CHECK(s.r.materials_carried == 0 && s.r.materials_built == 4);
        const Start g = start(mod, cache, &index, kvb::CarryHeader{kHeader.kiln, "0000", kHeader.side});
        const Start sd = start(mod, cache, &index, kvb::CarryHeader{kHeader.kiln, kHeader.game, "server"});
        CHECK(g.r.materials_carried == 0 && sd.r.materials_carried == 0);
        std::printf("%-58s carried %u / %u (index not used: %s / %s)\n", "another game build / the other side", g.r.materials_carried, sd.r.materials_carried,
                    g.index_why.c_str(), sd.index_why.c_str());
    }
    std::string wrong_shape = index;
    wrong_shape.replace(wrong_shape.find("\"slot\":"), 7, "\"slot\":\"x\",\"_\":");
    case_("a record of the wrong shape", mod, cache, &wrong_shape, 0, 4);

    // a cache entry the index names, gone or cut short: that material in full (and the cache repaired), the others carried
    {
        std::vector<kvb::MaterialRecord> recs;
        std::string why;
        CHECK(kvb::read_carry_index(index, kHeader, &recs, &why));
        const auto found = std::find_if(recs.begin(), recs.end(), [](const auto& r) { return r.name == "m1"; });
        if (found == recs.end()) return 1;
        const auto& m1 = *found;
        const fs::path entry = cache / (m1.maps[0].key + ".tex");
        CHECK(fs::exists(entry));
        fs::remove(entry);
        case_("a cache entry the index names is gone", mod, cache, &index, 3, 1);
        CHECK(fs::exists(entry));  // built in full: the cache has it again
        fs::resize_file(entry, fs::file_size(entry) / 2);
        case_("a cache entry the index names is cut short", mod, cache, &index, 3, 1);
    }
    // a PNG edited between starts (same size): the materials that read it in full
    write_png(dir / "tex/a.png", 64, 64, 9);
    case_("a PNG edited (m1 and m2 read it)", mod, cache, &index, 2, 2, &index);
    // a mod updated: a definition changed, a material removed, a new one
    mats["m3"] = kvb::parse_relaxed(R"({"tint": "#663399"})");
    mats.erase("m4");
    mats["m5"] = kvb::parse_relaxed(R"({"albedo": "tex/b.png"})");
    write_blocks(dir, mats);
    mod = read_mod("test-mod", dir);
    std::string after_update;
    case_("a mod updated (m3 changed, m4 removed, m5 new)", mod, cache, &index, 2, 2, &after_update);
    {
        std::vector<kvb::MaterialRecord> recs;
        std::string why;
        CHECK(kvb::read_carry_index(after_update, kHeader, &recs, &why));
        const auto m4 = std::find_if(recs.begin(), recs.end(), [](const auto& r) { return r.name == "m4"; });
        CHECK(recs.size() == 5 && m4 != recs.end() && m4->age == 1);  // a removed material stays a few starts (a mod switched off for one)
        std::vector<kvb::MaterialRecord> aged = recs;
        for (auto& r : aged) r.age = r.name == "m4" ? kvb::kCarryIndexMaxAge : 0;
        const auto merged = kvb::carry_merge({}, aged);
        CHECK(std::none_of(merged.begin(), merged.end(), [](const auto& r) { return r.name == "m4"; }));  // then it is dropped
        std::vector<kvb::MaterialRecord> many(kvb::kCarryIndexMaxRecords + 10, recs[0]);
        for (size_t i = 0; i < many.size(); ++i) many[i].name = "n" + std::to_string(i);
        CHECK(kvb::carry_merge(many, {}).size() == kvb::kCarryIndexMaxRecords);  // the cap
    }
    // a file a carried record names, gone: that material in full, today's error if it is needed
    fs::remove(dir / "tex/b.png");
    case_("a PNG gone (m2 and m5 read it: today's errors)", mod, cache, &after_update, 2, 0);

    fs::remove_all(root);
    if (failures) {
        std::fprintf(stderr, "%d check(s) failed\n", failures);
        return 1;
    }
    std::printf("test_carry: all checks passed\n");
    return 0;
}
