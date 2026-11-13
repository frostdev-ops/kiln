// SPDX-License-Identifier: MIT
// kiln-vfx's carry-over index (vfxcook.h, "the carry-over index"): the material records of the last starts, as JSON. Reading it checks
// everything: a file that is cut short, not JSON, of another format or header, too large, or with a record of the wrong shape is not
// used at all (the start builds as without it). Nothing here is trusted later without a check either: pack.cpp hashes the files again,
// and the texture cache checks its own entries.
#include <algorithm>
#include <cstdio>
#include <set>

#include "vfxcook.h"

namespace kvb {
namespace {

bool parse_hash(const std::string& text, ContentHash* out) {
    unsigned a = 0, b = 0, c = 0, d = 0;
    char tail = 0;
    if (text.size() > 40 || std::sscanf(text.c_str(), "%x-%x-%x-%x%c", &a, &b, &c, &d, &tail) != 4) return false;
    *out = ContentHash{a, b, c, d};
    return content_hash_text(*out) == text;  // one spelling only
}

bool str(const ojson& o, const char* key, size_t max, std::string* out) {
    const auto it = o.find(key);
    if (it == o.end() || !it->is_string()) return false;
    *out = it->get<std::string>();
    return !out->empty() && out->size() <= max;
}

bool num(const ojson& o, const char* key, uint64_t max, uint64_t* out) {
    const auto it = o.find(key);
    if (it == o.end() || !it->is_number_unsigned()) return false;
    *out = it->get<uint64_t>();
    return *out <= max;
}

}  // namespace

std::string carry_index_json(const CarryHeader& header, const std::vector<MaterialRecord>& records) {
    ojson j = ojson::object();
    j["format"] = kCarryIndexFormat;
    j["encoder"] = kEncoderVersion;
    j["kiln"] = header.kiln;
    j["game"] = header.game;
    j["side"] = header.side;
    ojson list = ojson::array();
    for (const MaterialRecord& r : records) {
        ojson o = ojson::object();
        o["mod"] = r.mod;
        o["name"] = r.name;
        o["definition"] = r.definition_text;
        o["age"] = r.age;
        ojson files = ojson::array();
        for (const auto& [rel, hash] : r.files) files.push_back(ojson::array({rel, content_hash_text(hash)}));
        o["files"] = std::move(files);
        ojson maps = ojson::array();
        for (const MaterialRecord::Map& m : r.maps)
            maps.push_back({{"slot", m.slot}, {"what", m.what}, {"width", m.width}, {"height", m.height}, {"format", m.format}, {"key", m.key}});
        o["maps"] = std::move(maps);
        list.push_back(std::move(o));
    }
    j["materials"] = std::move(list);
    return j.dump() + "\n";
}

bool read_carry_index(const std::string& text, const CarryHeader& header, std::vector<MaterialRecord>* out, std::string* why) {
    out->clear();
    if (text.size() > kCarryIndexMaxBytes) return *why = "larger than " + std::to_string(kCarryIndexMaxBytes >> 20) + " MiB", false;
    const ojson j = ojson::parse(text, nullptr, false);
    if (j.is_discarded() || !j.is_object()) return *why = "not JSON (cut short?)", false;
    uint64_t format = 0;
    std::string encoder, kiln, game, side;
    if (!num(j, "format", 1u << 20, &format) || format != kCarryIndexFormat) return *why = "another format", false;
    if (!str(j, "encoder", 64, &encoder) || encoder != kEncoderVersion) return *why = "another texture encoder", false;
    if (!str(j, "kiln", 256, &kiln) || kiln != header.kiln) return *why = "written by another Kiln build", false;
    if (!str(j, "game", 128, &game) || game != header.game) return *why = "written for another game build", false;
    if (!str(j, "side", 16, &side) || side != header.side) return *why = "written for the other side", false;
    const auto list = j.find("materials");
    if (list == j.end() || !list->is_array() || list->size() > kCarryIndexMaxRecords) return *why = "no list of materials, or too long a one", false;
    std::vector<MaterialRecord> records;
    std::set<std::pair<std::string, std::string>> seen;
    for (const ojson& o : *list) {
        MaterialRecord r;
        uint64_t age = 0;
        if (!o.is_object() || !str(o, "mod", 256, &r.mod) || !str(o, "name", 256, &r.name) || !str(o, "definition", 1u << 16, &r.definition_text) ||
            !num(o, "age", kCarryIndexMaxAge, &age))
            return *why = "a record of the wrong shape", false;
        r.age = static_cast<uint32_t>(age);
        if (!seen.insert({r.mod, r.name}).second) return *why = "a material twice", false;
        const auto files = o.find("files");
        const auto maps = o.find("maps");
        if (files == o.end() || !files->is_array() || files->size() > 16 || maps == o.end() || !maps->is_array() || maps->empty() || maps->size() > 8)
            return *why = "a record of the wrong shape", false;
        for (const ojson& f : *files) {
            ContentHash h;
            if (!f.is_array() || f.size() != 2 || !f[0].is_string() || !f[1].is_string() || f[0].get<std::string>().empty() ||
                f[0].get<std::string>().size() > 1024 || !parse_hash(f[1].get<std::string>(), &h))
                return *why = "a record of the wrong shape", false;
            r.files.emplace_back(f[0].get<std::string>(), h);
        }
        for (const ojson& m : *maps) {
            MaterialRecord::Map map;
            uint64_t slot = 0, w = 0, h = 0, fmt = 0;
            if (!m.is_object() || !num(m, "slot", 64, &slot) || !str(m, "what", 32, &map.what) || !num(m, "width", 1u << 15, &w) ||
                !num(m, "height", 1u << 15, &h) || !num(m, "format", 255, &fmt) || !str(m, "key", 256, &map.key))
                return *why = "a record of the wrong shape", false;
            map.slot = static_cast<uint32_t>(slot);
            map.width = static_cast<uint32_t>(w);
            map.height = static_cast<uint32_t>(h);
            map.format = static_cast<uint8_t>(fmt);
            r.maps.push_back(std::move(map));
        }
        records.push_back(std::move(r));
    }
    *out = std::move(records);
    return true;
}

std::vector<MaterialRecord> carry_merge(const std::vector<MaterialRecord>& now, const std::vector<MaterialRecord>& before) {
    std::vector<MaterialRecord> out;
    std::set<std::pair<std::string, std::string>> have;
    for (const MaterialRecord& r : now) {
        if (out.size() >= kCarryIndexMaxRecords) break;
        // a block no cache saw is of no use later
        if (r.maps.empty() || std::any_of(r.maps.begin(), r.maps.end(), [](const MaterialRecord::Map& m) { return m.key.empty(); }) ||
            !have.insert({r.mod, r.name}).second)
            continue;
        out.push_back(r);
        out.back().age = 0;
    }
    for (const MaterialRecord& r : before) {
        if (out.size() >= kCarryIndexMaxRecords) break;
        if (r.age + 1 > kCarryIndexMaxAge || !have.insert({r.mod, r.name}).second) continue;
        out.push_back(r);
        out.back().age = r.age + 1;
    }
    return out;
}

}  // namespace kvb
