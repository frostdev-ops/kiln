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
#include <set>
#include <atomic>
#include <new>
#include <string>
#include <vector>

#include "../cook/vfxcook.h"

// Every operator new fails while armed: the lookup arms it before it throws, the progress callback (the first thing after the carry decision)
// disarms it, so whatever the failure path allocates fails too.
static std::atomic<bool> g_fail_alloc{false};
void* operator new(std::size_t n) {
    if (g_fail_alloc.load(std::memory_order_relaxed)) throw std::bad_alloc();
    if (void* p = std::malloc(n ? n : 1)) return p;
    throw std::bad_alloc();
}
void* operator new[](std::size_t n) { return ::operator new(n); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, std::size_t) noexcept { std::free(p); }
void operator delete[](void* p, std::size_t) noexcept { std::free(p); }

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
static bool g_throw_in_lookup = false;  // the index's lookup throws (out of memory while using it)

static Start start(const kvb::ModInput& mod, const fs::path& cache, const std::string* index_in, const kvb::CarryHeader& header = kHeader) {
    Start s;
    kvb::CookOptions opt;
    opt.cache = disk_cache(cache);
    opt.read_file = read_file;
    opt.game_has_blob = [](const kvb::ContentHash&) { return true; };
    std::vector<kvb::MaterialRecord> records, carried;
    std::map<std::pair<std::string, std::string>, size_t> at;
    opt.record_material = [&records](const kvb::MaterialRecord& r) { records.push_back(r); };
    opt.progress = [](const std::string&, size_t, size_t, const char*, const std::string&, size_t, size_t) { g_fail_alloc = false; };
    if (index_in) {
        if (kvb::read_carry_index(*index_in, header, &carried, &s.index_why)) {
            for (size_t i = 0; i < carried.size(); ++i) at[{carried[i].mod, carried[i].name}] = i;
            opt.carried_material = [&](const std::string& m, const std::string& n, const std::string& def) -> const kvb::MaterialRecord* {
                if (g_throw_in_lookup) {
                    g_fail_alloc = true;  // and the handler's own allocations fail as well
                    throw std::bad_alloc();
                }
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

// ---- the review's cases (the index is a hint: any index gives what no index gives) ----
static void write_mod(const fs::path& dir, ojson* mats) {
    fs::create_directories(dir / "blocks");
    fs::create_directories(dir / "tex");
    write_png(dir / "tex/a.png", 64, 64, 1);
    write_png(dir / "tex/b.png", 32, 32, 2);
    write_png(dir / "tex/c.png", 64, 64, 3);
    write_png(dir / "tex/d.png", 16, 16, 4);
    *mats = ojson::object();
    (*mats)["m1"] = kvb::parse_relaxed(R"({"albedo": "tex/a.png", "roughness": 0.5})");
    (*mats)["m2"] = kvb::parse_relaxed(R"({"albedo": "tex/b.png", "normal": {"map": "tex/a.png", "strength": 2}, "metallic": 1})");
    (*mats)["m3"] = kvb::parse_relaxed(R"({"tint": "#336699"})");
    (*mats)["m4"] = kvb::parse_relaxed(R"({"albedo": "tex/c.png", "normal": {"map": "tex/d.png"}, "emissive": {"map": "tex/c.png", "color": "#ffffff", "strength": 2}, "kind": "emissive", "metallicRoughness": "tex/b.png", "occlusion": "tex/d.png"})");
    write_blocks(dir, *mats);
}

static std::vector<kvb::MaterialRecord> records_of(const std::string& index) {
    std::vector<kvb::MaterialRecord> r;
    std::string why;
    CHECK(kvb::read_carry_index(index, kHeader, &r, &why));
    return r;
}

static kvb::MaterialRecord& rec(std::vector<kvb::MaterialRecord>& rs, const char* name) {
    return *std::find_if(rs.begin(), rs.end(), [&](const auto& r) { return r.name == name; });
}

// The index a start with `input` must write: what a start without any index writes (this start's records), and after them, as intended
// history, every record of the input the reader accepted whose material this start did not have, one start older (dropped past
// kCarryIndexMaxAge), the youngest first. An input the reader refused leaves no history.
static std::string expected_index(const Start& without, const std::string& input) {
    std::vector<kvb::MaterialRecord> now = records_of(without.index_out), before, history;
    std::string why;
    if (!kvb::read_carry_index(input, kHeader, &before, &why)) before.clear();
    std::stable_sort(before.begin(), before.end(), [](const auto& a, const auto& b) { return a.age < b.age; });
    for (const auto& r : before) {
        if (r.age + 1 > kvb::kCarryIndexMaxAge || std::any_of(now.begin(), now.end(), [&](const auto& n) { return n.mod == r.mod && n.name == r.name; })) continue;
        history.push_back(r);
        history.back().age = r.age + 1;
    }
    now.insert(now.end(), history.begin(), history.end());
    return kvb::carry_index_json(kHeader, now);
}

// Which check decided about an index (the reader's reason, else the cook's notes), for the counts.
static std::string decided_by(const Start& with) {
    if (!with.index_why.empty()) return "reader: " + with.index_why;
    std::set<std::string> seen;
    for (const std::string& n : with.r.carry_notes) {
        if (n.find("not built at an earlier start") != std::string::npos) seen.insert("cook: no record for the definition");
        else if (n.find("does not match what the definition") != std::string::npos) seen.insert("cook: the plan");
        else if (n.find(" changed") != std::string::npos || n.find("cannot be read") != std::string::npos) seen.insert("cook: the files' bytes");
        else if (n.find("no build of these inputs") != std::string::npos) seen.insert("cook: the binding");
        else if (n.find("no longer holds") != std::string::npos) seen.insert("cook: the cache");
        else seen.insert("cook: other");
    }
    if (seen.empty()) return with.r.materials_carried ? "taken as it is" : "nothing to take";
    std::string out;
    for (const auto& x : seen) out += (out.empty() ? "" : " + ") + x;
    return out;
}

// A start with `index` against one without it: the same output, and the index written back is expected_index's. Returns whether it was.
static bool same_as_without(const kvb::ModInput& mod, const fs::path& cache, const std::string& index, std::string* decided = nullptr) {
    const Start with = start(mod, cache, &index);
    const Start without = start(mod, cache, nullptr);
    std::string where;
    if (decided) *decided = decided_by(with);
    return same_output(with.r, without.r, &where) && with.index_out == expected_index(without, index);
}

// The valid index's JSON with each record's checksum written again (as carry.cpp's record_check: the record without "check", sorted keys).
static std::string resigned(ojson index) {
    if (index.is_object() && index.contains("materials") && index["materials"].is_array())
        for (auto& r : index["materials"]) {
            if (!r.is_object()) continue;
            nlohmann::json bare = nlohmann::json::parse(r.dump());
            bare.erase("check");
            const std::string text = bare.dump();
            r["check"] = kvb::content_hash_text(kvb::content_hash(text.data(), text.size()));
        }
    return index.dump();
}

static ojson& record_json(ojson& index, const char* name) {
    for (auto& r : index["materials"])
        if (r["name"] == name) return r;
    return index["materials"][0];
}

static void review_cases(const fs::path& root) {
    const fs::path dir = root / "review", cache = root / "review-cache";
    ojson mats;
    write_mod(dir, &mats);
    const kvb::ModInput mod = read_mod("review-mod", dir);
    const std::string index = start(mod, cache, nullptr).index_out;
    {
        // material_plan names exactly what a real build read and made, for every material of the case
        std::vector<kvb::MaterialRecord> rs = records_of(index);
        CHECK(rs.size() == 4);
        for (const auto& r : rs) {
            kvb::Diagnostics d;
            kvb::MaterialDef def;
            CHECK(kvb::parse_material(kvb::parse_relaxed(r.definition_text), r.name, "", &def, &d));
            const kvb::MaterialPlan plan = kvb::material_plan(def);
            CHECK(plan.inputs.size() == r.files.size() && plan.maps.size() == r.maps.size());
            for (size_t i = 0; i < std::min(plan.inputs.size(), r.files.size()); ++i) CHECK(plan.inputs[i] == r.files[i].first);
            for (size_t i = 0; i < std::min(plan.maps.size(), r.maps.size()); ++i)
                CHECK(plan.maps[i].slot == r.maps[i].slot && r.maps[i].what == plan.maps[i].what && plan.maps[i].format == r.maps[i].format);
        }
        CHECK(rec(rs, "m4").maps.size() == 4);
    }
    // 1. a cache miss at each map position of a four-map material: built in full, the same as without the index, and the index written back
    //    the same as a start without the index writes
    for (size_t pos = 0; pos < 4; ++pos) {
        std::vector<kvb::MaterialRecord> rs = records_of(index);
        const fs::path entry = cache / (rec(rs, "m4").maps[pos].key + ".tex");
        fs::remove(entry);
        const Start with = start(mod, cache, &index);
        fs::remove(entry);
        const Start without = start(mod, cache, nullptr);
        std::string where;
        const bool same = same_output(with.r, without.r, &where);
        std::printf("%-58s carried %u, built %u, output %s, index written back %s\n", ("m4's map " + std::to_string(pos) + " gone from the cache").c_str(),
                    with.r.materials_carried, with.r.materials_built, same ? "== without the index" : ("DIFFERS: " + where).c_str(),
                    with.index_out == without.index_out ? "== without the index" : "DIFFERS");
        CHECK(same && with.r.materials_carried == 3 && with.r.materials_built == 1);
        CHECK(with.index_out == without.index_out);
        CHECK(rec(rs = records_of(with.index_out), "m4").maps.size() == 4);
    }
    // 2. using the index throws (out of memory): every material built in full, the same output
    g_throw_in_lookup = true;
    {
        const Start with = start(mod, cache, &index);
        g_throw_in_lookup = false;
        const Start without = start(mod, cache, nullptr);
        std::string where;
        CHECK(same_output(with.r, without.r, &where) && with.r.materials_carried == 0 && with.r.materials_built == 4);
        std::printf("%-58s carried %u, built %u\n", "using the index throws", with.r.materials_carried, with.r.materials_built);
    }
    g_throw_in_lookup = false;
    {
        std::vector<kvb::MaterialRecord> out;
        std::string why;
        CHECK(!kvb::read_carry_index(std::string(kvb::kCarryIndexMaxBytes + 1, ' '), kHeader, &out, &why) && out.empty());
    }
    // 3. bounded parsing: a worst-case file of the limit's size
    {
        std::string huge = "{\"materials\":[{";
        for (size_t i = 0; huge.size() < kvb::kCarryIndexMaxBytes - 32; ++i) huge += "\"k" + std::to_string(i) + "\":0,";
        huge += "\"z\":0}]}";
        std::vector<kvb::MaterialRecord> out;
        std::string why;
        auto a = Clock::now();
        CHECK(!kvb::read_carry_index(huge, kHeader, &out, &why));
        const double members = ms(Clock::now() - a);
        std::string deep(kvb::kCarryIndexMaxBytes / 2, '[');
        a = Clock::now();
        CHECK(!kvb::read_carry_index(deep, kHeader, &out, &why));
        const double depth = ms(Clock::now() - a);
        // the largest index the writer makes: records with 60 KiB definitions up to the byte budget
        std::vector<kvb::MaterialRecord> big = records_of(index);
        std::vector<kvb::MaterialRecord> many;
        for (int i = 0; i < 300; ++i) {
            kvb::MaterialRecord r = big[0];
            r.name = "n" + std::to_string(i);
            r.definition_text = "{\"comment\":\"" + std::string(60 << 10, 'x') + "\"}";
            many.push_back(r);
        }
        const std::string full = kvb::carry_index_json(kHeader, many);
        a = Clock::now();
        const bool ok = kvb::read_carry_index(full, kHeader, &out, &why);
        const double valid = ms(Clock::now() - a);
        std::printf("bounded parsing: 16 MiB of one object's members refused in %.0f ms, 8 MiB of nesting in %.0f ms; the largest index the writer "
                    "makes (%zu bytes, %zu of 300 records of 60 KiB) read in %.0f ms\n",
                    members, depth, full.size(), out.size(), valid);
        CHECK(ok && full.size() <= kvb::kCarryIndexMaxBytes && out.size() > 200 && out.size() < 300);
        CHECK(members < 2000 && depth < 2000 && valid < 5000);
        // 7. one definition over the reader's limit: left out, the rest readable
        std::vector<kvb::MaterialRecord> one = records_of(index);
        one[0].definition_text = "{\"comment\":\"" + std::string(70 << 10, 'x') + "\"}";
        CHECK(kvb::read_carry_index(kvb::carry_index_json(kHeader, one), kHeader, &out, &why) && out.size() == 3);
    }
    // 4. keys and paths that would leave their folders, 5. a file list that does not cover the inputs, 6. metadata that disagrees: each
    //    written straight into the index's JSON with the record's checksum written again (no writer in between), so that the check meant
    //    for it is the one that refuses it
    {
        const ojson valid = ojson::parse(index);
        struct Hostile {
            const char* what;
            void (*change)(ojson&);
        };
        const Hostile hostile[] = {
            {"a key ../outside", [](ojson& j) { record_json(j, "m1")["maps"][0]["key"] = "../outside"; }},
            {"an absolute key", [](ojson& j) { record_json(j, "m1")["maps"][0]["key"] = "/etc/passwd"; }},
            {"a key of another size (width doubled)", [](ojson& j) { auto& m = record_json(j, "m1")["maps"][0]; m["width"] = m["width"].get<uint32_t>() * 2; }},
            {"a file path ../x.png", [](ojson& j) { record_json(j, "m1")["files"][0][0] = "../x.png"; }},
            {"an absolute file path", [](ojson& j) { record_json(j, "m1")["files"][0][0] = "/tmp/a.png"; }},
            {"files [] (the PNG is still named)", [](ojson& j) { record_json(j, "m1")["files"] = ojson::array(); }},
            {"another file in the list", [](ojson& j) { record_json(j, "m1")["files"][0][0] = "tex/b.png"; }},
            {"format 146 -> 145 (key to match)", [](ojson& j) {
                 auto& m = record_json(j, "m1")["maps"][1];
                 std::string key = m["key"];
                 m["format"] = 145;
                 m["key"] = key.substr(0, key.rfind("-f")) + "-f145-" + kvb::kEncoderVersion;
             }},
            {"two maps swapped", [](ojson& j) { auto& m = record_json(j, "m2")["maps"]; std::swap(m[0], m[1]); }},
            {"a slot changed", [](ojson& j) { auto& m = record_json(j, "m2")["maps"][0]; m["slot"] = m["slot"].get<uint32_t>() + 1; }},
            {"a map left out", [](ojson& j) { record_json(j, "m2")["maps"].erase(record_json(j, "m2")["maps"].size() - 1); }},
            {"another material's maps", [](ojson& j) { record_json(j, "m1")["maps"] = record_json(j, "m3")["maps"]; }},
            {"another material's key, same size", [](ojson& j) { record_json(j, "m1")["maps"][2]["key"] = record_json(j, "m3")["maps"][2]["key"]; }},
        };
        for (const auto& h : hostile) {
            ojson j = valid;
            h.change(j);
            std::string decided;
            const bool same = same_as_without(mod, cache, resigned(j), &decided);
            std::printf("%-58s output %s; decided by %s\n", h.what, same ? "== without the index" : "DIFFERS", decided.c_str());
            CHECK(same);
            CHECK(decided.find("checksum") == std::string::npos && decided != "taken as it is");  // a deeper check refused it
        }
        // 5 with the PNG gone: no index gives the missing-PNG error, and so does the index with files []
        ojson j = valid;
        record_json(j, "m1")["files"] = ojson::array();
        const std::string text = resigned(j);
        const fs::path moved = root / "a.png.moved";
        fs::rename(dir / "tex/a.png", moved);
        const Start with = start(mod, cache, &text);
        const Start without = start(mod, cache, nullptr);
        std::string where;
        CHECK(same_output(with.r, without.r, &where) && !with.r.errors.empty());
        fs::rename(moved, dir / "tex/a.png");
    }
    // 3 (review): the history an index keeps is written back as intended: a record of a material this start does not have stays, a start older
    {
        std::vector<kvb::MaterialRecord> rs = records_of(index);
        kvb::MaterialRecord gone = rs[0];
        gone.name = "gone";
        gone.age = 2;
        rs.push_back(gone);
        const std::string input = kvb::carry_index_json(kHeader, rs);
        const Start with = start(mod, cache, &input);
        const auto out = records_of(with.index_out);
        const auto g = std::find_if(out.begin(), out.end(), [](const auto& r) { return r.name == "gone"; });
        CHECK(out.size() == 5 && g != out.end() && g->age == 3);
        CHECK(same_as_without(mod, cache, input));
    }
    // the property test: single mutations of the valid index, each compared with no index
    {
        const ojson valid = ojson::parse(index);
        std::vector<std::string> mutated;
        const auto variants = [](const ojson& v) {
            std::vector<ojson> out = {ojson(), ojson(""), ojson::array(), ojson::object(), ojson(7), ojson(-1), ojson(1.5)};
            if (v.is_string()) out.push_back(v.get<std::string>() + "x");
            if (v.is_number_unsigned()) out.push_back(v.get<uint64_t>() + 1);
            return out;
        };
        for (auto it = valid.begin(); it != valid.end(); ++it) {
            ojson a = valid;
            a.erase(it.key());
            mutated.push_back(a.dump());
            for (const ojson& v : variants(it.value())) {
                ojson b = valid;
                b[it.key()] = v;
                mutated.push_back(b.dump());
            }
        }
        const auto& list = valid["materials"];
        for (size_t i = 0; i < list.size(); ++i) {
            for (auto it = list[i].begin(); it != list[i].end(); ++it) {
                ojson a = valid;
                a["materials"][i].erase(it.key());
                mutated.push_back(a.dump());
                for (const ojson& v : variants(it.value())) {
                    ojson b = valid;
                    b["materials"][i][it.key()] = v;
                    mutated.push_back(b.dump());
                }
                if (it.value().is_array())
                    for (size_t k = 0; k < it.value().size(); ++k) {
                        ojson c = valid;
                        c["materials"][i][it.key()].erase(k);
                        mutated.push_back(c.dump());
                        const ojson& e = it.value()[k];
                        if (e.is_object())
                            for (auto f = e.begin(); f != e.end(); ++f)
                                for (const ojson& v : variants(f.value())) {
                                    ojson d = valid;
                                    d["materials"][i][it.key()][k][f.key()] = v;
                                    mutated.push_back(d.dump());
                                }
                        if (e.is_array())
                            for (size_t q = 0; q < e.size(); ++q)
                                for (const ojson& v : variants(e[q])) {
                                    ojson d = valid;
                                    d["materials"][i][it.key()][k][q] = v;
                                    mutated.push_back(d.dump());
                                }
                    }
            }
            ojson dup = valid;
            dup["materials"].push_back(list[i]);
            mutated.push_back(dup.dump());
        }
        ojson reordered = valid;
        std::reverse(reordered["materials"].begin(), reordered["materials"].end());
        mutated.push_back(reordered.dump());
        mutated.push_back(index.substr(0, index.size() - 3));
        size_t same = 0, runs = 0;
        std::map<std::string, size_t> by[2];  // [0] the mutation as it is, [1] with its records' checksums written again
        for (const std::string& m : mutated)
            for (int resign = 0; resign < 2; ++resign) {
                std::string text = m;
                if (resign) {
                    const ojson j = ojson::parse(m, nullptr, false);
                    if (j.is_discarded()) continue;
                    text = resigned(j);
                }
                std::string decided;
                same += same_as_without(mod, cache, text, &decided) ? 1 : 0;
                ++runs;
                ++by[resign][decided];
            }
        std::printf("mutations of the valid index: %zu, run as they are and with their checksums written again: %zu runs, each the output (and "
                    "the index written back) of no index: %zu\n",
                    mutated.size(), runs, same);
        for (int resign = 0; resign < 2; ++resign) {
            std::printf("  %s:\n", resign ? "checksums written again" : "as they are");
            for (const auto& [k, n] : by[resign]) std::printf("    %4zu  %s\n", n, k.c_str());
        }
        CHECK(same == runs && mutated.size() > 300);
    }
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

    review_cases(root);

    fs::remove_all(root);
    if (failures) {
        std::fprintf(stderr, "%d check(s) failed\n", failures);
        return 1;
    }
    std::printf("test_carry: all checks passed\n");
    return 0;
}
