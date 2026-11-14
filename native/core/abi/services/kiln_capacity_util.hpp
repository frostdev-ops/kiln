// SPDX-License-Identifier: MIT
// Header-only helpers for services that answer with kiln_capacity.h (since 0.2.0-dev.28): filling a reply (checked
// accounting, the not-ready form), a revision counter, the stable registration id, and the pending-definition files
// (atomic write, deterministic replay order). No Windows dependency; C++20.
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

#include "kiln_capacity.h"

namespace kiln::capacity {

static_assert(sizeof(KilnCapacityInfo) == 48 && offsetof(KilnCapacityInfo, revision) == 40, "KilnCapacityInfo layout");
static_assert(sizeof(KilnDefinitionReply) == 64 && offsetof(KilnDefinitionReply, snapshot_revision) == 48,
              "KilnDefinitionReply layout");

// Where a budget lives. world(): kind is KILN_WORLD_SERVER (0) or KILN_WORLD_CLIENT (2); generation is the core
// session's counter of that kind (0 = there is no such world now).
struct Scope {
    bool world_scope = false;
    uint32_t world_kind = KILN_CAPACITY_NO_WORLD_KIND;
    uint32_t generation = 0;
    static Scope process() { return {}; }
    static Scope world(uint32_t kind, uint32_t generation) { return {true, kind, generation}; }
};

inline bool valid_out(const KilnCapacityInfo* out) { return out && out->size >= sizeof(KilnCapacityInfo); }

inline void fill_scope(KilnCapacityInfo* out, const Scope& s) {
    out->flags |= s.world_scope ? KILN_CAPACITY_WORLD : KILN_CAPACITY_PROCESS;
    out->world_kind = s.world_scope ? s.world_kind : KILN_CAPACITY_NO_WORLD_KIND;
    out->generation = s.world_scope ? s.generation : 0;
}

// A reply that is not ready: the four counts are 0, READY is clear. `readiness` is any value but VALID.
inline void not_ready(KilnCapacityInfo* out, uint32_t readiness, const Scope& scope, uint64_t revision,
                      bool expanded = false) {
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->readiness = readiness == KILN_CAPACITY_VALID ? KILN_CAPACITY_NOT_READY : readiness;
    if (expanded) out->flags |= KILN_CAPACITY_EXPANDED;
    if (out->readiness == KILN_CAPACITY_UNKNOWN_EXTERNAL) out->flags |= KILN_CAPACITY_EXTERNAL_UNKNOWN;
    fill_scope(out, scope);
    out->revision = revision;
}

// A ready reply, when the counts add up: game + kiln + reserved <= capacity in 64 bits. Otherwise the reply is
// ACCOUNTING_MISMATCH with zero counts (returns false): a budget is never clamped into something positive.
inline bool publish(KilnCapacityInfo* out, uint64_t capacity, uint64_t game_used, uint64_t kiln_used, uint64_t reserved,
                    const Scope& scope, uint64_t revision, bool expanded) {
    const uint64_t sum = game_used + kiln_used + reserved;
    const bool wraps = sum < game_used || game_used + kiln_used < game_used;
    if (wraps || sum > capacity || capacity > UINT32_MAX || (scope.world_scope && scope.generation == 0)) {
        not_ready(out, scope.world_scope && scope.generation == 0 ? KILN_CAPACITY_NO_WORLD : KILN_CAPACITY_ACCOUNTING_MISMATCH,
                  scope, revision, expanded);
        return false;
    }
    const uint32_t size = out->size;
    std::memset(out, 0, sizeof *out);
    out->size = size;
    out->capacity = static_cast<uint32_t>(capacity);
    out->game_used = static_cast<uint32_t>(game_used);
    out->kiln_used = static_cast<uint32_t>(kiln_used);
    out->reserved = static_cast<uint32_t>(reserved);
    out->readiness = KILN_CAPACITY_VALID;
    out->flags = KILN_CAPACITY_READY | (expanded ? KILN_CAPACITY_EXPANDED : 0u);
    fill_scope(out, scope);
    out->revision = revision;
    return true;
}

// A service's revision: starts at 1, rises by one with every published change (also a receipt-only one).
class Revision {
public:
    uint64_t current() const { return v_.load(std::memory_order_acquire); }
    uint64_t next() { return v_.fetch_add(1, std::memory_order_acq_rel) + 1; }

private:
    std::atomic<uint64_t> v_{1};
};

// ---- the registration id ----
// UUIDv5 (SHA-1) in Kiln's id namespace over the UTF-8 text
//   "<mod>:<kind>:<definition guid, 32 lower-case hex digits of its 16 bytes>:<sha256, 64 lower-case hex digits>"
// kind is "vfx", "buff" or "building-material-pool"; sha256 is the hash of the compiler's canonical definition (no
// absolute paths in it). Managed: Kiln.DefinitionRegistration.DeriveId gives the same 16 bytes (Guid.ToByteArray of a
// big-endian-read Guid differs: compare bytes through its own helper).
namespace detail {
inline std::array<uint8_t, 20> sha1(const uint8_t* data, size_t len) {
    uint32_t h[5] = {0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u};
    std::vector<uint8_t> msg(data, data + len);
    msg.push_back(0x80);
    while (msg.size() % 64 != 56) msg.push_back(0);
    const uint64_t bits = static_cast<uint64_t>(len) * 8;
    for (int i = 7; i >= 0; --i) msg.push_back(static_cast<uint8_t>(bits >> (8 * i)));
    auto rol = [](uint32_t x, int n) { return (x << n) | (x >> (32 - n)); };
    for (size_t at = 0; at < msg.size(); at += 64) {
        uint32_t w[80];
        for (int i = 0; i < 16; ++i)
            w[i] = (uint32_t{msg[at + 4 * i]} << 24) | (uint32_t{msg[at + 4 * i + 1]} << 16) |
                   (uint32_t{msg[at + 4 * i + 2]} << 8) | uint32_t{msg[at + 4 * i + 3]};
        for (int i = 16; i < 80; ++i) w[i] = rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
        uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4];
        for (int i = 0; i < 80; ++i) {
            uint32_t f, k;
            if (i < 20) f = (b & c) | (~b & d), k = 0x5A827999u;
            else if (i < 40) f = b ^ c ^ d, k = 0x6ED9EBA1u;
            else if (i < 60) f = (b & c) | (b & d) | (c & d), k = 0x8F1BBCDCu;
            else f = b ^ c ^ d, k = 0xCA62C1D6u;
            const uint32_t t = rol(a, 5) + f + e + k + w[i];
            e = d, d = c, c = rol(b, 30), b = a, a = t;
        }
        h[0] += a, h[1] += b, h[2] += c, h[3] += d, h[4] += e;
    }
    std::array<uint8_t, 20> out{};
    for (int i = 0; i < 5; ++i)
        for (int k = 0; k < 4; ++k) out[static_cast<size_t>(4 * i + k)] = static_cast<uint8_t>(h[i] >> (24 - 8 * k));
    return out;
}
inline void hex(std::string& to, const uint8_t* bytes, size_t n) {
    static const char* const digits = "0123456789abcdef";
    for (size_t i = 0; i < n; ++i) to += digits[bytes[i] >> 4], to += digits[bytes[i] & 15];
}
}  // namespace detail

inline const char* kind_name(uint32_t kind) {
    switch (kind) {
        case KILN_DEFINITION_VFX: return "vfx";
        case KILN_DEFINITION_BUFF: return "buff";
        case KILN_DEFINITION_BUILDING_MATERIAL_POOL: return "building-material-pool";
        default: return nullptr;
    }
}

// False for an unknown kind. `out` receives the 16 bytes in network order (as the UUID is written).
inline bool registration_id(std::string_view mod, uint32_t kind, const uint8_t definition_guid[16], const uint8_t sha256[32],
                            uint8_t out[16]) {
    const char* k = kind_name(kind);
    if (!k) return false;
    static constexpr uint8_t kNamespace[16] = {0x6f, 0x6d, 0x2a, 0x44, 0x53, 0xc1, 0x5c, 0x52,
                                               0x9d, 0x51, 0x0a, 0x3c, 0x6e, 0x1f, 0x2b, 0x77};
    std::string text(reinterpret_cast<const char*>(kNamespace), 16);
    text += mod;
    text += ':';
    text += k;
    text += ':';
    detail::hex(text, definition_guid, 16);
    text += ':';
    detail::hex(text, sha256, 32);
    const auto h = detail::sha1(reinterpret_cast<const uint8_t*>(text.data()), text.size());
    std::memcpy(out, h.data(), 16);
    out[6] = static_cast<uint8_t>((out[6] & 0x0f) | 0x50);
    out[8] = static_cast<uint8_t>((out[8] & 0x3f) | 0x80);
    return true;
}

// ---- a define reply ----
inline void reply_init(KilnDefinitionReply* r, uint32_t kind, const uint8_t registration[16], const uint8_t definition[16]) {
    const uint32_t size = r->size;
    std::memset(r, 0, sizeof *r);
    r->size = size;
    r->kind = kind;
    std::memcpy(r->registration_id, registration, 16);
    std::memcpy(r->definition_guid, definition, 16);
}
// ok = 1 for REGISTERED and ACCEPTED_NEXT_START (the latter always with 0 slots); reason NONE.
inline void reply_ok(KilnDefinitionReply* r, uint32_t disposition, uint32_t slots, uint64_t revision) {
    r->ok = 1;
    r->disposition = disposition;
    r->slots_consumed = disposition == KILN_DEFINITION_ACCEPTED_NEXT_START ? 0 : slots;
    r->reason_code = KILN_DEFINE_NONE;
    r->snapshot_revision = revision;
}
// ok = 0: REFUSED (slots 0 unless a record could not be freed) or RETAINED with the slots it still occupies.
inline void reply_refused(KilnDefinitionReply* r, uint32_t reason_code, uint64_t revision,
                          uint32_t disposition = KILN_DEFINITION_REFUSED, uint32_t slots_still_occupied = 0) {
    r->ok = 0;
    r->disposition = disposition == KILN_DEFINITION_RETAINED ? KILN_DEFINITION_RETAINED : KILN_DEFINITION_REFUSED;
    r->slots_consumed = slots_still_occupied;
    r->reason_code = reason_code == KILN_DEFINE_NONE ? KILN_DEFINE_GAME_REFUSED : reason_code;
    r->snapshot_revision = revision;
}

// ---- pending definitions: <dir>/<mod>/<file> ----
// Writes `bytes` to `file` through a temporary file in the same folder and a rename, so a reader sees the old file or
// the new one, never a part. False when anything failed (the temporary file is removed).
inline bool write_atomic(const std::filesystem::path& file, std::string_view bytes) {
    std::error_code ec;
    std::filesystem::create_directories(file.parent_path(), ec);
    std::filesystem::path tmp = file;
    tmp += ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out) return false;
        out.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
        out.flush();
        if (!out) {
            out.close();
            std::filesystem::remove(tmp, ec);
            return false;
        }
    }
    std::filesystem::rename(tmp, file, ec);
    if (ec) {
        std::filesystem::remove(tmp, ec);
        return false;
    }
    return true;
}

struct PendingFile {
    std::string mod;   // the folder under `dir`
    std::string name;  // the file's name (a definition GUID plus an extension, by convention)
    std::filesystem::path path;
};

// The pending files of every mod in replay order: by mod folder name, then file name, both compared as bytes.
// Temporary files (".tmp") and folders inside a mod's folder are skipped. A missing `dir` gives an empty list.
inline std::vector<PendingFile> pending_files(const std::filesystem::path& dir) {
    std::vector<PendingFile> out;
    std::error_code ec;
    for (std::filesystem::directory_iterator mods(dir, ec), end; !ec && mods != end; mods.increment(ec)) {
        if (!mods->is_directory(ec)) continue;
        std::error_code ec2;
        for (std::filesystem::directory_iterator files(mods->path(), ec2), fend; !ec2 && files != fend; files.increment(ec2)) {
            if (!files->is_regular_file(ec2) || files->path().extension() == ".tmp") continue;
            out.push_back({mods->path().filename().string(), files->path().filename().string(), files->path()});
        }
    }
    std::sort(out.begin(), out.end(), [](const PendingFile& a, const PendingFile& b) {
        return a.mod != b.mod ? a.mod < b.mod : a.name < b.name;
    });
    return out;
}

}  // namespace kiln::capacity
