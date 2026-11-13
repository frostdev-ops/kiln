// SPDX-License-Identifier: MIT
// kiln.assets: which cooked files a game loads (cooktrust.h).
#include "cooktrust.h"

#include <array>
#include <cctype>
#include <cstring>
#include <fstream>
#include <sstream>
#include <system_error>

#include "nlohmann/json.hpp"

namespace ka {
namespace {

// ---- SHA-256 (FIPS 180-4) --------------------------------------------------------------------------------------
constexpr std::array<uint32_t, 64> K = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01,
    0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
    0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
    0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08,
    0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};

inline uint32_t rotr(uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }

void block(uint32_t h[8], const uint8_t* p) {
    uint32_t w[64];
    for (int i = 0; i < 16; ++i)
        w[i] = uint32_t(p[i * 4]) << 24 | uint32_t(p[i * 4 + 1]) << 16 | uint32_t(p[i * 4 + 2]) << 8 | uint32_t(p[i * 4 + 3]);
    for (int i = 16; i < 64; ++i) {
        const uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
        const uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
    for (int i = 0; i < 64; ++i) {
        const uint32_t t1 = hh + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i];
        const uint32_t t2 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
        hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
}

std::string lower(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return s;
}

// "cooked": "local" | "warn" | "any" in `sec`; anything else is a note and keeps `cur`.
CookedPolicy read_policy(const nlohmann::json& sec, CookedPolicy cur, const char* where, std::vector<std::string>* notes) {
    if (!sec.is_object() || !sec.contains("cooked")) return cur;
    const auto& v = sec["cooked"];
    const std::string s = v.is_string() ? v.get<std::string>() : "";
    if (s == "any") return CookedPolicy::Any;
    if (s == "warn") return CookedPolicy::Warn;
    if (s == "local") return CookedPolicy::Local;
    notes->push_back(std::string(where) + " \"cooked\" is not \"local\", \"warn\" or \"any\"; kept as " + policy_name(cur));
    return cur;
}

}  // namespace

const char* policy_name(CookedPolicy p) {
    return p == CookedPolicy::Any ? "any" : p == CookedPolicy::Warn ? "warn" : "local";
}

std::string sha256_hex(const void* data, size_t len) {
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    const auto* p = static_cast<const uint8_t*>(data);
    size_t n = len;
    while (n >= 64) {
        block(h, p);
        p += 64;
        n -= 64;
    }
    uint8_t tail[128] = {};
    std::memcpy(tail, p, n);
    tail[n] = 0x80;
    const size_t total = n + 9 <= 64 ? 64 : 128;
    const uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 0; i < 8; ++i) tail[total - 1 - i] = static_cast<uint8_t>(bits >> (8 * i));
    block(h, tail);
    if (total == 128) block(h, tail + 64);
    static const char* hex = "0123456789abcdef";
    std::string out(64, '0');
    for (int i = 0; i < 8; ++i)
        for (int j = 0; j < 4; ++j) {
            const uint8_t b = static_cast<uint8_t>(h[i] >> (24 - 8 * j));
            out[i * 8 + j * 2] = hex[b >> 4];
            out[i * 8 + j * 2 + 1] = hex[b & 15];
        }
    return out;
}

bool is_cooked_path(const std::string& rel) {
    std::string first;
    for (char c : rel) {
        if (c == '/' || c == '\\') break;
        first += c;
    }
    if (first == "." ) {  // "./cooked/x": the same folder
        const size_t at = rel.find_first_of("/\\");
        return at != std::string::npos && is_cooked_path(rel.substr(at + 1));
    }
    return lower(first) == "cooked" && first.size() < rel.size();
}

CookTrust load_cook_trust(const std::filesystem::path& kiln_dir, const std::filesystem::path& state_dir, bool client,
                          bool store_linked) {
    namespace fs = std::filesystem;
    CookTrust t;
    {
        std::ifstream in(kiln_dir / "kiln.json", std::ios::binary);
        if (in) {
            try {
                const auto doc = nlohmann::json::parse(in, nullptr, true, true);
                t.policy = read_policy(doc, t.policy, "kiln.json", &t.notes);
                const char* side = client ? "client" : "server";
                if (doc.is_object() && doc.contains(side)) t.policy = read_policy(doc[side], t.policy, side, &t.notes);
            } catch (const std::exception& e) {
                t.notes.push_back(std::string("kiln.json unreadable (") + e.what() + "): \"cooked\" is \"" + policy_name(t.policy) + "\"");
            }
        }
    }
    std::error_code ec;
    const fs::path dir = state_dir / "cook-receipts";
    // the store is <game>/kiln/state/cook-receipts itself: a link there (or at kiln/state) would let it lie elsewhere
    if (store_linked || fs::is_symlink(state_dir, ec) || fs::is_symlink(dir, ec)) {
        t.store_refused = true;
        t.notes.push_back("the receipt store " + dir.string() + " is a link (or kiln/state is): no receipt is read from it");
        return t;
    }
    for (auto it = fs::directory_iterator(dir, ec); !ec && it != fs::directory_iterator(); it.increment(ec)) {
        const fs::path p = it->path();
        std::error_code e2;
        if (p.extension() != ".json" || !it->is_regular_file(e2) || it->is_symlink(e2)) continue;
        if (fs::file_size(p, e2) > (4u << 20)) {
            t.notes.push_back(p.filename().string() + ": larger than 4 MB, not read");
            continue;
        }
        std::ifstream in(p, std::ios::binary);
        try {
            const auto doc = nlohmann::json::parse(in, nullptr, true, false);
            if (!doc.is_object() || doc.value("format", 0) != 1 || !doc.contains("files") || !doc["files"].is_object()) {
                t.notes.push_back(p.filename().string() + ": not a cook receipt");
                continue;
            }
            for (const auto& [name, digest] : doc["files"].items())
                if (digest.is_string() && digest.get<std::string>().size() == 64) t.digests.insert(lower(digest.get<std::string>()));
            ++t.receipts;
        } catch (const std::exception&) {
            t.notes.push_back(p.filename().string() + ": not readable JSON");
        }
    }
    return t;
}

CookedVerdict cooked_verdict(const CookTrust& t, const void* data, size_t len) {
    if (t.policy == CookedPolicy::Any || t.digests.count(sha256_hex(data, len)) != 0) return CookedVerdict::Load;
    return t.policy == CookedPolicy::Warn ? CookedVerdict::LoadUnmade : CookedVerdict::Refuse;
}

bool cooked_trusted(const CookTrust& t, const void* data, size_t len) {
    return cooked_verdict(t, data, len) != CookedVerdict::Refuse;
}

}  // namespace ka
