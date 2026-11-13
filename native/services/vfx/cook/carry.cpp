// SPDX-License-Identifier: MIT
// kiln-vfx's carry-over index (vfxcook.h, "the carry-over index"): the material records of the last starts, as JSON. The index is a hint:
// for every material, a start with any index file whatsoever (valid, stale, cut short, hostile) gives what a start without one gives, in
// bounded time and memory. So reading it checks everything before anything is kept, and in bounded time:
//  - the size first (kCarryIndexMaxBytes; the caller checks it before reading the file too);
//  - a SAX pass that refuses depth and members beyond the format's shape before a value is built (no object can have more than a few
//    members, so the parse that follows is linear);
//  - the header (format, encoder, Kiln build, game build, side), then every record: its shape, its own checksum (an edited or damaged
//    record is caught as a whole), every path in the rule the cook applies before opening a file (safe_relative_path), every cache key
//    in exactly the syntax chain_cache_key writes and agreeing with the record's size and format. One bad record: the whole index is not
//    used. pack.cpp checks the rest against this start (material_plan, the files' bytes, the cache's own entries).
// The writer applies the reader's limits: a record the reader would refuse is left out, and a byte budget decides which are written
// (this start's first, then the others by age), so what it writes is always readable.
#include <algorithm>
#include <cstdio>
#include <set>

#include "vfxcook.h"

namespace kvb {
namespace {

using json = nlohmann::json;  // std::map objects: insertion is logarithmic, never a linear scan

constexpr size_t kMaxDepth = 5;            // index > materials > record > files > [path, hash]
constexpr size_t kMaxMembers = 16;         // members of any object
constexpr size_t kMaxDefinition = 1u << 16, kMaxName = 256, kMaxPath = 1024, kMaxKey = 128;
constexpr size_t kMaxFiles = 16, kMaxMaps = 8;
constexpr size_t kWriteMargin = 64u << 10;  // the writer stops this far below kCarryIndexMaxBytes

// Refuses, before a value is built, what no index of this format has: deeper nesting, objects with more members, longer arrays.
struct Shape : json::json_sax_t {
    struct Open {
        bool object;
        size_t count;  // members of an object, elements of an array
    };
    std::vector<Open> open_;
    bool fail = false;
    bool open(bool object) {
        if (open_.size() >= kMaxDepth) return fail = true, false;
        open_.push_back({object, 0});
        return true;
    }
    bool value() {  // an element of an array (a member's value is counted at its key)
        if (!open_.empty() && !open_.back().object && ++open_.back().count > kCarryIndexMaxRecords) return fail = true, false;
        return true;
    }
    bool member() {
        if (!open_.empty() && ++open_.back().count > kMaxMembers) return fail = true, false;
        return true;
    }
    bool null() override { return value(); }
    bool boolean(bool) override { return value(); }
    bool number_integer(number_integer_t) override { return value(); }
    bool number_unsigned(number_unsigned_t) override { return value(); }
    bool number_float(number_float_t, const string_t&) override { return value(); }
    bool string(string_t&) override { return value(); }
    bool binary(binary_t&) override { return fail = true, false; }
    bool start_object(std::size_t) override { return value() && open(true); }
    bool key(string_t&) override { return member(); }
    bool end_object() override { return open_.pop_back(), true; }
    bool start_array(std::size_t) override { return value() && open(false); }
    bool end_array() override { return open_.pop_back(), true; }
    bool parse_error(std::size_t, const std::string&, const nlohmann::detail::exception&) override { return fail = true, false; }
};

bool parse_hash(const std::string& text, ContentHash* out) {
    unsigned a = 0, b = 0, c = 0, d = 0;
    char tail = 0;
    if (text.empty() || text.size() > 40 || std::sscanf(text.c_str(), "%x-%x-%x-%x%c", &a, &b, &c, &d, &tail) != 4) return false;
    *out = ContentHash{a, b, c, d};
    return content_hash_text(*out) == text;  // one spelling only: lower-case hex without leading zeros, as content_hash_text writes it
}

// A texture cache key in exactly chain_cache_key's syntax, <pixels' hash>-<w>x<h>-f<format>-<encoder>, for this size and format.
bool key_ok(const std::string& key, uint32_t w, uint32_t h, uint8_t format) {
    if (key.empty() || key.size() > kMaxKey) return false;
    size_t dashes = 0, at = 0;
    for (; at < key.size() && dashes < 4; ++at)
        if (key[at] == '-') ++dashes;
    if (dashes != 4) return false;
    ContentHash pixels;
    return parse_hash(key.substr(0, at - 1), &pixels) && chain_cache_key(pixels, w, h, format) == key;
}

bool what_ok(const std::string& w) { return w == "emissive" || w == "albedo" || w == "normal" || w == "material"; }

bool format_ok(uint8_t f) {
    return f == kFormatBc1Unorm || f == kFormatBc1Srgb || f == kFormatBc4Unorm || f == kFormatBc5Unorm || f == kFormatBc7Unorm || f == kFormatBc7Srgb;
}

bool side_ok(uint32_t s) { return s >= 1 && s <= (1u << 15) && (s & (s - 1)) == 0; }  // a power of two, as decode_png requires

// What the reader accepts of a record, for the writer too.
bool record_ok(const MaterialRecord& r) {
    if (r.mod.empty() || r.mod.size() > kMaxName || r.name.empty() || r.name.size() > kMaxName || r.definition_text.empty() ||
        r.definition_text.size() > kMaxDefinition || r.age > kCarryIndexMaxAge || r.files.size() > kMaxFiles || r.maps.empty() || r.maps.size() > kMaxMaps)
        return false;
    for (const auto& [rel, hash] : r.files)
        if (rel.empty() || rel.size() > kMaxPath || !safe_relative_path(rel)) return false;
    for (const MaterialRecord::Map& m : r.maps)
        if (m.slot > 64 || !what_ok(m.what) || !side_ok(m.width) || !side_ok(m.height) || !format_ok(m.format) || !key_ok(m.key, m.width, m.height, m.format))
            return false;
    return true;
}

json record_json(const MaterialRecord& r) {
    json o = json::object();
    o["mod"] = r.mod;
    o["name"] = r.name;
    o["definition"] = r.definition_text;
    o["age"] = r.age;
    json files = json::array();
    for (const auto& [rel, hash] : r.files) files.push_back(json::array({rel, content_hash_text(hash)}));
    o["files"] = std::move(files);
    json maps = json::array();
    for (const MaterialRecord::Map& m : r.maps)
        maps.push_back({{"slot", m.slot}, {"what", m.what}, {"width", m.width}, {"height", m.height}, {"format", m.format}, {"key", m.key}});
    o["maps"] = std::move(maps);
    return o;
}

// The record's own checksum: over its JSON without "check" (sorted keys, so one spelling).
std::string record_check(const json& o) {
    const std::string text = o.dump();
    return content_hash_text(content_hash(text.data(), text.size()));
}

bool str(const json& o, const char* key, size_t max, std::string* out) {
    const auto it = o.find(key);
    if (it == o.end() || !it->is_string()) return false;
    *out = it->get<std::string>();
    return !out->empty() && out->size() <= max;
}

bool num(const json& o, const char* key, uint64_t max, uint64_t* out) {
    const auto it = o.find(key);
    if (it == o.end() || !it->is_number_unsigned()) return false;
    *out = it->get<uint64_t>();
    return *out <= max;
}

bool only(const json& o, std::initializer_list<const char*> keys) {
    if (o.size() != keys.size()) return false;
    for (const char* k : keys)
        if (!o.contains(k)) return false;
    return true;
}

bool read_record(const json& o, MaterialRecord* r) {
    if (!o.is_object() || !only(o, {"mod", "name", "definition", "age", "files", "maps", "check"})) return false;
    std::string check;
    if (!str(o, "check", 64, &check)) return false;
    json bare = o;
    bare.erase("check");
    if (record_check(bare) != check) return false;
    uint64_t age = 0;
    if (!str(o, "mod", kMaxName, &r->mod) || !str(o, "name", kMaxName, &r->name) || !str(o, "definition", kMaxDefinition, &r->definition_text) ||
        !num(o, "age", kCarryIndexMaxAge, &age))
        return false;
    r->age = static_cast<uint32_t>(age);
    const json& files = o["files"];
    const json& maps = o["maps"];
    if (!files.is_array() || files.size() > kMaxFiles || !maps.is_array() || maps.empty() || maps.size() > kMaxMaps) return false;
    for (const json& f : files) {
        ContentHash h;
        if (!f.is_array() || f.size() != 2 || !f[0].is_string() || !f[1].is_string() || !parse_hash(f[1].get<std::string>(), &h)) return false;
        r->files.emplace_back(f[0].get<std::string>(), h);
    }
    for (const json& m : maps) {
        MaterialRecord::Map map;
        uint64_t slot = 0, w = 0, h = 0, fmt = 0;
        if (!m.is_object() || !only(m, {"slot", "what", "width", "height", "format", "key"}) || !num(m, "slot", 64, &slot) || !str(m, "what", 16, &map.what) ||
            !num(m, "width", 1u << 15, &w) || !num(m, "height", 1u << 15, &h) || !num(m, "format", 255, &fmt) || !str(m, "key", kMaxKey, &map.key))
            return false;
        map.slot = static_cast<uint32_t>(slot);
        map.width = static_cast<uint32_t>(w);
        map.height = static_cast<uint32_t>(h);
        map.format = static_cast<uint8_t>(fmt);
        r->maps.push_back(std::move(map));
    }
    return record_ok(*r);
}

}  // namespace

std::string carry_index_json(const CarryHeader& header, const std::vector<MaterialRecord>& records) {
    json head = json::object();
    head["format"] = kCarryIndexFormat;
    head["encoder"] = kEncoderVersion;
    head["kiln"] = header.kiln;
    head["game"] = header.game;
    head["side"] = header.side;
    std::string text = head.dump();  // {...}: the list of records is appended before the closing brace
    text.pop_back();
    text += ",\"materials\":[";
    std::set<std::pair<std::string, std::string>> have;
    bool first = true;
    for (const MaterialRecord& r : records) {
        if (!record_ok(r) || !have.insert({r.mod, r.name}).second) continue;  // what the reader would refuse is left out
        json o = record_json(r);
        o["check"] = record_check(o);
        const std::string one = o.dump();
        if (text.size() + one.size() + 1 + 3 > kCarryIndexMaxBytes - kWriteMargin) break;  // the byte budget: earlier records first
        text += (first ? "" : ",") + one;
        first = false;
    }
    return text + "]}\n";
}

bool read_carry_index(const std::string& text, const CarryHeader& header, std::vector<MaterialRecord>* out, std::string* why) {
    out->clear();
    try {
        if (text.size() > kCarryIndexMaxBytes) return *why = "larger than " + std::to_string(kCarryIndexMaxBytes >> 20) + " MiB", false;
        Shape shape;
        if (!json::sax_parse(text, &shape, json::input_format_t::json, true) || shape.fail) return *why = "not an index of this shape (cut short?)", false;
        const json j = json::parse(text, nullptr, false);
        if (j.is_discarded() || !j.is_object()) return *why = "not JSON (cut short?)", false;
        if (!only(j, {"format", "encoder", "kiln", "game", "side", "materials"})) return *why = "not an index of this shape", false;
        uint64_t format = 0;
        std::string encoder, kiln, game, side;
        if (!num(j, "format", 1u << 20, &format) || format != kCarryIndexFormat) return *why = "another format", false;
        if (!str(j, "encoder", 64, &encoder) || encoder != kEncoderVersion) return *why = "another texture encoder", false;
        if (!str(j, "kiln", 256, &kiln) || kiln != header.kiln) return *why = "written by another Kiln build", false;
        if (!str(j, "game", 128, &game) || game != header.game) return *why = "written for another game build", false;
        if (!str(j, "side", 16, &side) || side != header.side) return *why = "written for the other side", false;
        const json& list = j["materials"];
        if (!list.is_array() || list.size() > kCarryIndexMaxRecords) return *why = "no list of materials, or too long a one", false;
        std::vector<MaterialRecord> records;
        std::set<std::pair<std::string, std::string>> seen;
        for (const json& o : list) {
            MaterialRecord r;
            if (!read_record(o, &r)) return *why = "a record that does not check out", false;
            if (!seen.insert({r.mod, r.name}).second) return *why = "a material twice", false;
            records.push_back(std::move(r));
        }
        *out = std::move(records);
        return true;
    } catch (...) {
        out->clear();
        return *why = "it could not be read", false;
    }
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
    // the earlier ones the newest first (the writer's byte budget keeps the front)
    std::vector<const MaterialRecord*> older;
    for (const MaterialRecord& r : before) older.push_back(&r);
    std::stable_sort(older.begin(), older.end(), [](const MaterialRecord* a, const MaterialRecord* b) { return a->age < b->age; });
    for (const MaterialRecord* r : older) {
        if (out.size() >= kCarryIndexMaxRecords) break;
        if (r->age + 1 > kCarryIndexMaxAge || !have.insert({r->mod, r->name}).second) continue;
        out.push_back(*r);
        out.back().age = r->age + 1;
    }
    return out;
}

}  // namespace kvb
