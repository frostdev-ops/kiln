// SPDX-License-Identifier: MIT
// kiln.boot's offline game-data view (kiln_boot.h read_resource; Kiln.IReadOnlyGameData.Read): a reader of the boot
// game-data snapshot, format 1 (docs/kiln/modding/boot-gamedata.md). The snapshot is made before the start by
// `kiln boot-snapshot`; this reads it and nothing else: never a live registry, never the game's archive beyond the
// 64 KiB that hold its version string, never a directory whose name is not the key computed here, and it never writes.
//
// Header-only and free of the core's other headers, so boot_service.cpp and its host test compile it as it is.
// Threads: open() and read() are boot-thread calls (kiln_boot.h); a mutex of the reader's own guards its indexes all
// the same, and no caller's lock is needed or held across its file IO.
//
// Paths: a file name is always "<kind>.index.json" or "<kind>.data" of the four fixed kinds below; the query's text
// is only ever a key into a loaded index, never part of a path.
#pragma once
#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>

#include "kiln_api.h"
#include "nlohmann/json.hpp"

namespace kiln::boot_gd {

constexpr size_t kKfcHeadMax = 64u << 10;      // bytes of <exe stem>.kfc read for its version string
constexpr uint64_t kIndexMax = 64ull << 20;    // bytes of one <kind>.index.json (and of index.json): larger is refused
constexpr uint64_t kOffsetMax = 1ull << 53;    // offsets and lengths are below this (the contract)
constexpr size_t kWrapperMax = 256;            // bytes of {"resourceId",...} around a value
constexpr const char* kKinds[4] = {"items", "templates", "skills", "materials"};
constexpr const char* kHint =
    "no game-data snapshot for this game build: start through the Kiln launcher, or run `kiln boot-snapshot --game <dir>`";

// ---- SHA-256 (FIPS 180-4), for the key: the core's own hash is the OS's and the host test has none ----
inline std::array<uint8_t, 32> sha256(std::string_view data) {
    static constexpr uint32_t k[64] = {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be,
        0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa,
        0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85,
        0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3,
        0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f,
        0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    std::string m(data);
    const uint64_t bits = static_cast<uint64_t>(data.size()) * 8;
    m.push_back(static_cast<char>(0x80));
    while (m.size() % 64 != 56) m.push_back(0);
    for (int i = 7; i >= 0; --i) m.push_back(static_cast<char>(bits >> (i * 8)));
    auto rotr = [](uint32_t x, int n) { return (x >> n) | (x << (32 - n)); };
    for (size_t at = 0; at < m.size(); at += 64) {
        uint32_t w[64];
        for (int i = 0; i < 16; ++i) {
            const auto* p = reinterpret_cast<const uint8_t*>(m.data() + at + i * 4);
            w[i] = static_cast<uint32_t>(p[0]) << 24 | static_cast<uint32_t>(p[1]) << 16 | static_cast<uint32_t>(p[2]) << 8 | p[3];
        }
        for (int i = 16; i < 64; ++i) {
            const uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int i = 0; i < 64; ++i) {
            const uint32_t t1 = hh + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + k[i] + w[i];
            const uint32_t t2 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
            hh = g, g = f, f = e, e = d + t1, d = c, c = b, b = a, a = t1 + t2;
        }
        h[0] += a, h[1] += b, h[2] += c, h[3] += d, h[4] += e, h[5] += f, h[6] += g, h[7] += hh;
    }
    std::array<uint8_t, 32> out{};
    for (int i = 0; i < 8; ++i)
        for (int j = 0; j < 4; ++j) out[static_cast<size_t>(i * 4 + j)] = static_cast<uint8_t>(h[i] >> (24 - j * 8));
    return out;
}

inline bool lower_hex(std::string_view s, size_t n) {
    if (s.size() != n) return false;
    for (const char c : s)
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
    return true;
}

inline std::string lowered(std::string_view s) {
    std::string out(s);
    for (char& c : out)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return out;
}

/// The key of the snapshot for a game build: the first 32 hex digits of SHA-256("<exe sha256 hex>\n<kfc version>").
inline std::string key_of(std::string_view exe_sha256, std::string_view kfc_version) {
    std::string text(exe_sha256);
    text += '\n';
    text += kfc_version;
    static constexpr char digits[] = "0123456789abcdef";
    std::string key;
    const auto digest = sha256(text);
    for (size_t i = 0; i < 16; ++i) {
        key += digits[digest[i] >> 4];
        key += digits[digest[i] & 15];
    }
    return key;
}

/// Up to `max` bytes of `file` from `offset`. False when it cannot be opened or holds fewer than `want` bytes there
/// (want 0: whatever is there, at most max).
inline bool read_at(const std::filesystem::path& file, uint64_t offset, size_t max, size_t want, std::string* out) {
    std::ifstream in(file, std::ios::binary);
    if (!in) return false;
    if (offset && !in.seekg(static_cast<std::streamoff>(offset))) return false;
    out->resize(max);
    in.read(out->data(), static_cast<std::streamsize>(max));
    out->resize(static_cast<size_t>(in.gcount()));
    return want == 0 || out->size() >= want;
}

/// The version string of a KFC4 file: header 0x10 holds {u32 rel, u32 len}, the string is the len bytes at 0x10 + rel.
/// One read of at most 64 KiB. False with *why when the file is missing, short, not KFC4 or the string lies outside
/// what was read.
inline bool kfc_version(const std::filesystem::path& kfc, std::string* version, std::string* why) {
    std::string head;
    if (!read_at(kfc, 0, kKfcHeadMax, 0, &head)) return *why = "the game's .kfc file could not be opened", false;
    if (head.size() < 0x18) return *why = "the game's .kfc file is shorter than its header", false;
    if (std::memcmp(head.data(), "KFC4", 4) != 0) return *why = "the game's .kfc file does not start with KFC4", false;
    auto u32 = [&](size_t at) {
        const auto* p = reinterpret_cast<const uint8_t*>(head.data() + at);
        return static_cast<uint32_t>(p[0]) | static_cast<uint32_t>(p[1]) << 8 | static_cast<uint32_t>(p[2]) << 16 |
               static_cast<uint32_t>(p[3]) << 24;
    };
    const uint64_t at = 0x10ull + u32(0x10), len = u32(0x14);
    if (len == 0 || at + len > head.size())
        return *why = "the version string of the game's .kfc file lies outside its first 64 KiB", false;
    version->assign(head.data() + at, static_cast<size_t>(len));
    return true;
}

class Reader {
   public:
    /// Looks for the snapshot of this game build under `root` (kiln/data/boot-gamedata). True: it is there and passed
    /// every test of the contract. False: *why names the first test that failed; nothing is read afterwards.
    bool open(const std::filesystem::path& root, std::string_view exe_sha256, const std::filesystem::path& kfc, std::string* why) {
        std::lock_guard lk(mutex_);
        open_ = false;
        for (auto& k : kinds_) k = Kind{};
        resources_loaded_ = false;
        resources_.clear();
        if (!lower_hex(exe_sha256, 64)) return *why = "the game executable's SHA-256 is not known", false;
        std::string version;
        if (!kfc_version(kfc, &version, why)) return false;
        key_ = key_of(exe_sha256, version);
        dir_ = root / key_;  // the only directory ever opened: 32 hex digits computed here
        std::error_code ec;
        if (!std::filesystem::is_directory(dir_, ec)) return *why = "there is no snapshot directory " + key_, false;
        const auto index = dir_ / "index.json";
        const auto size = std::filesystem::file_size(index, ec);
        if (ec) return *why = "the snapshot " + key_ + " has no index.json (it was not finished)", false;
        std::string text;
        if (size > kIndexMax || !read_at(index, 0, static_cast<size_t>(size), static_cast<size_t>(size), &text))
            return *why = "the snapshot's index.json could not be read", false;
        const auto j = nlohmann::json::parse(text, nullptr, false);
        if (!j.is_object()) return *why = "the snapshot's index.json is not a JSON object", false;
        const auto format = j.find("format");
        if (format == j.end() || !format->is_number_integer() || *format != 1)
            return *why = "the snapshot's format is not 1 (this Kiln reads format 1)", false;
        const auto key = j.find("key");
        if (key == j.end() || !key->is_string() || key->get_ref<const std::string&>() != key_)
            return *why = "the snapshot's index.json names another key than its directory " + key_, false;
        for (const char* kind : kKinds)
            for (const char* ext : {".index.json", ".data"})
                if (!std::filesystem::is_regular_file(dir_ / (std::string(kind) + ext), ec))
                    return *why = "the snapshot " + key_ + " has no " + kind + ext, false;
        open_ = true;
        return true;
    }

    bool is_open() const {
        std::lock_guard lk(mutex_);
        return open_;
    }
    std::string key() const {
        std::lock_guard lk(mutex_);
        return key_;
    }

    /// One query of the grammar kiln.boot checked (item:, template:, skill:, material:, resource:).
    ///   KILN_OK              *json holds {"resourceId","typeHash","part","value"}; *value_bytes the value's length
    ///   KILN_ERR_BUFFER_TOO_SMALL  capacity (which counts the NUL) is too small: *required is set, no data is read
    ///   KILN_ERR_NOT_FOUND   no such record in the snapshot
    ///   KILN_ERR_UNAVAILABLE no snapshot, an index or entry that is broken, a value over `result_max` or one that
    ///                        would take the author over `budget_left`; *why says which
    KilnResult read(std::string_view query, size_t capacity, size_t result_max, uint64_t budget_left, std::string* json,
                    size_t* required, size_t* value_bytes, std::string* why) {
        std::lock_guard lk(mutex_);
        if (!open_) return *why = "no snapshot", KILN_ERR_UNAVAILABLE;
        const auto colon = query.find(':');
        if (colon == std::string_view::npos) return KILN_ERR_INVALID_ARGUMENT;
        const std::string_view form = query.substr(0, colon), arg = query.substr(colon + 1);
        const Entry* e = nullptr;
        size_t kind = 0;
        if (form == "resource") {
            if (!load_resources(why)) return KILN_ERR_UNAVAILABLE;
            const auto it = resources_.find(lowered(arg));
            if (it == resources_.end()) return *why = "not in the boot snapshot", KILN_ERR_NOT_FOUND;
            kind = it->second.first;
            e = it->second.second;
        } else {
            kind = form == "item" ? 0 : form == "template" ? 1 : form == "skill" ? 2 : form == "material" ? 3 : 4;
            if (kind == 4) return KILN_ERR_INVALID_ARGUMENT;
            if (!load(kind, why)) return KILN_ERR_UNAVAILABLE;
            const auto it = kinds_[kind].entries.find(kind == 2 ? lowered(arg) : std::string(arg));
            if (it == kinds_[kind].entries.end()) return *why = "not in the boot snapshot", KILN_ERR_NOT_FOUND;
            e = &it->second;
        }
        const std::string head = "{\"resourceId\":\"" + e->guid + "\",\"typeHash\":\"" + e->type + "\",\"part\":" + std::to_string(e->part) +
                                 ",\"value\":";
        // the whole result with its '}' and its NUL is at most result_max
        if (e->length > result_max || head.size() + e->length + 2 > result_max)
            return *why = "the record is larger than one result may be", KILN_ERR_UNAVAILABLE;
        if (e->length > budget_left) return *why = "the mod's game-data budget is spent", KILN_ERR_UNAVAILABLE;
        const size_t need = head.size() + static_cast<size_t>(e->length) + 2;
        if (capacity < need) return *required = need, KILN_ERR_BUFFER_TOO_SMALL;  // the only answer that sets it
        // the entry has to lie inside its data file: never read past it, never answer with a short read
        const auto data = dir_ / (std::string(kKinds[kind]) + ".data");
        std::error_code ec;
        const auto size = std::filesystem::file_size(data, ec);
        if (ec || e->offset > size || e->length > size - e->offset)
            return *why = std::string("an entry of ") + kKinds[kind] + ".index.json points outside " + kKinds[kind] + ".data",
                   KILN_ERR_UNAVAILABLE;
        std::string value;
        if (!read_at(data, e->offset, static_cast<size_t>(e->length), static_cast<size_t>(e->length), &value))
            return *why = std::string(kKinds[kind]) + ".data could not be read", KILN_ERR_UNAVAILABLE;
        // one complete JSON object, as the contract says: anything else would break the result around it
        const auto first = value.find_first_not_of(" \t\r\n");
        if (first == std::string::npos || value[first] != '{' || !nlohmann::json::accept(value))
            return *why = std::string("an entry of ") + kKinds[kind] + ".data is not one JSON object", KILN_ERR_UNAVAILABLE;
        *json = head;
        *json += value;
        *json += '}';
        *value_bytes = value.size();
        return KILN_OK;
    }

   private:
    struct Entry {
        uint64_t offset = 0, length = 0;
        std::string guid, type;
        uint32_t part = 0;
    };
    struct Kind {
        bool loaded = false, ok = false;
        std::string why;
        std::unordered_map<std::string, Entry> entries;
    };

    // One kind's index, loaded once per process; a broken index stays broken (and says why each time).
    bool load(size_t kind, std::string* why) {
        Kind& k = kinds_[kind];
        if (!k.loaded) {
            k.loaded = true;
            k.ok = parse(kind, &k);
        }
        if (!k.ok) *why = k.why;
        return k.ok;
    }

    bool parse(size_t kind, Kind* k) {
        const std::string name = std::string(kKinds[kind]) + ".index.json";
        const auto file = dir_ / name;
        std::error_code ec;
        const auto size = std::filesystem::file_size(file, ec);
        if (ec) return k->why = name + " could not be opened", false;
        if (size > kIndexMax) return k->why = name + " is larger than 64 MiB", false;
        std::string text;
        if (!read_at(file, 0, static_cast<size_t>(size), static_cast<size_t>(size), &text)) return k->why = name + " could not be read", false;
        const auto j = nlohmann::json::parse(text, nullptr, false);
        if (!j.is_object() || !j.contains("format") || !j["format"].is_number_integer() || j["format"] != 1 || !j.contains("kind") ||
            !j["kind"].is_string() || j["kind"].get_ref<const std::string&>() != kKinds[kind] || !j.contains("entries") ||
            !j["entries"].is_object())
            return k->why = name + " is not a format 1 index of " + kKinds[kind], false;
        for (const auto& [key, v] : j["entries"].items()) {
            // [offset, length, "<guid>", "<8 hex>", part]; an entry of another shape breaks the index: a producer's defect
            if (!v.is_array() || v.size() != 5 || !v[0].is_number_unsigned() || !v[1].is_number_unsigned() || !v[2].is_string() ||
                !v[3].is_string() || !v[4].is_number_unsigned())
                return k->why = name + ": the entry " + key.substr(0, 64) + " is not [offset, length, guid, type hash, part]", false;
            Entry e;
            e.offset = v[0].get<uint64_t>();
            e.length = v[1].get<uint64_t>();
            e.guid = v[2].get<std::string>();
            e.type = v[3].get<std::string>();
            const uint64_t part = v[4].get<uint64_t>();
            if (e.offset >= kOffsetMax || e.length >= kOffsetMax || e.length == 0 || !guid_ok(e.guid) || !lower_hex(e.type, 8) || part > 0x7fffffffu)
                return k->why = name + ": the entry " + key.substr(0, 64) + " has an offset, length, GUID, type hash or part out of range", false;
            e.part = static_cast<uint32_t>(part);
            k->entries.emplace(key, std::move(e));
        }
        return true;
    }

    static bool guid_ok(std::string_view g) {
        if (g.size() != 36) return false;
        for (size_t i = 0; i < g.size(); ++i) {
            const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
            if (dash ? g[i] != '-' : !lower_hex(g.substr(i, 1), 1)) return false;
        }
        return true;
    }

    // "<guid>/<type>/<part>" -> the item or template entry it backs, built at the first resource: query. Items first:
    // a resource is one or the other.
    bool load_resources(std::string* why) {
        if (!load(0, why) || !load(1, why)) return false;
        if (resources_loaded_) return true;
        for (size_t kind = 0; kind < 2; ++kind)
            for (const auto& [name, e] : kinds_[kind].entries)
                resources_.emplace(e.guid + "/" + e.type + "/" + std::to_string(e.part), std::make_pair(kind, &e));
        resources_loaded_ = true;
        return true;
    }

    mutable std::mutex mutex_;
    bool open_ = false;
    std::string key_;
    std::filesystem::path dir_;
    Kind kinds_[4];
    bool resources_loaded_ = false;
    std::unordered_map<std::string, std::pair<size_t, const Entry*>> resources_;
};

}  // namespace kiln::boot_gd
