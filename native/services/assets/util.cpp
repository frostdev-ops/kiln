// SPDX-License-Identifier: MIT
// kiln.assets: small helpers (hashing, GUID and key formatting, paths, file reads).

#include "assets.h"  // windows.h first (bcrypt.h needs its types)

#include <bcrypt.h>

#include <cstdarg>
#include <cstdio>

namespace ka {

void logf(int level, const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    const int n = std::vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    if (g_api && n > 0) g_api->log(g_self, level, KilnStr{buf, static_cast<size_t>(n < (int)sizeof buf ? n : (int)sizeof buf - 1)});
}

uint32_t fnv1a(const void* p, size_t n) {
    uint32_t h = 0x811c9dc5u;
    const auto* b = static_cast<const uint8_t*>(p);
    for (size_t i = 0; i < n; ++i) h = (h ^ b[i]) * 0x01000193u;
    return h;
}

// keen::ContentHash of a blob: size + the first 12 bytes of SHA-256 (docs/kiln/services/assets.md).
HashK content_hash_of(const void* data, size_t len) {
    uint8_t digest[32] = {};
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0) {
        BCryptHash(alg, nullptr, 0, static_cast<PUCHAR>(const_cast<void*>(data)), static_cast<ULONG>(len), digest, 32);
        BCryptCloseAlgorithmProvider(alg, 0);
    }
    HashK h;
    h.size = static_cast<uint32_t>(len);
    std::memcpy(&h.h0, digest + 0, 4);
    std::memcpy(&h.h1, digest + 4, 4);
    std::memcpy(&h.h2, digest + 8, 4);
    return h;
}

// Known resource type names; falls back to the hex of any value.
struct Named {
    const char* name;
    uint32_t hash;
};
static const Named kTypes[] = {
    {"keen::UiTextureResource", 0xf9e86997u},   {"keen::ItemInfo", 0xb5ce8765u},
    {"keen::RenderTextureResource", fnv1a("keen::RenderTextureResource", 27)},
    {"keen::RenderModel", 0x34e30853u},          {"keen::ecs::TemplateResource", 0x39768775u},
    {"keen::anim_graph::runtime_graph::AnimationGraphResource2_0", kPlayerGraphType},
};

bool client_only_type(uint32_t type) {
    static const uint32_t kClientOnly[] = {
        0x177394c7u,  // keen::RenderModel (a type's hash is the FNV-1a of its name)
        0xaee1894bu,  // keen::RenderMaterialResource
        0x3002d39du,  // keen::RenderTextureResource
        0xf9e86997u,  // keen::UiTextureResource
    };
    for (const uint32_t t : kClientOnly)
        if (t == type) return true;
    return false;
}

std::vector<bool> client_only_entries(const std::vector<ManifestItem>& items) {
    auto same = [](std::string a, std::string b) {  // one file under either slash
        for (char& c : a) if (c == '\\') c = '/';
        for (char& c : b) if (c == '\\') c = '/';
        return a == b;
    };
    std::vector<bool> out(items.size(), false);
    for (size_t i = 0; i < items.size(); ++i) {
        const ManifestItem& e = items[i];
        if (e.kind == "resource") {
            out[i] = client_only_type(e.type);
        } else if (e.kind == "blob" && !e.file.empty() && !e.fixed_hash) {
            // a blob is client-only when a client-only resource patches it in and nothing a server serves does
            bool by_client = false, by_other = false;
            for (const ManifestItem& r : items) {
                if (r.kind != "resource") continue;
                for (const std::string& b : r.patch_blobs)
                    if (same(b, e.file)) (client_only_type(r.type) ? by_client : by_other) = true;
            }
            out[i] = by_client && !by_other;
        }
    }
    return out;
}

uint32_t type_hash(const std::string& s, bool* ok) {
    *ok = true;
    if (s.size() > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X'))
        return static_cast<uint32_t>(std::strtoul(s.c_str(), nullptr, 16));
    return fnv1a(s.data(), s.size());
}

std::string type_name(uint32_t hash) {
    for (const Named& t : kTypes)
        if (t.hash == hash) return t.name;
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", hash);
    return b;
}

// .NET Guid.Parse + ToByteArray order: {u32 LE}{u16 LE}{u16 LE}{8 bytes BE}.
bool parse_guid(const std::string& s, uint8_t out[16]) {
    std::string hex;
    for (char c : s)
        if (c != '-') {
            if (!isxdigit(static_cast<unsigned char>(c))) return false;
            hex += c;
        }
    if (hex.size() != 32) return false;
    auto byte = [&](int i) { return static_cast<uint8_t>(std::strtoul(hex.substr(i * 2, 2).c_str(), nullptr, 16)); };
    uint8_t b[16];
    for (int i = 0; i < 16; ++i) b[i] = byte(i);
    out[0] = b[3]; out[1] = b[2]; out[2] = b[1]; out[3] = b[0];  // Data1 LE
    out[4] = b[5]; out[5] = b[4];                                 // Data2 LE
    out[6] = b[7]; out[7] = b[6];                                 // Data3 LE
    for (int i = 8; i < 16; ++i) out[i] = b[i];                   // Data4 as-is
    return true;
}

std::string guid_str(const uint8_t g[16]) {
    char b[40];
    std::snprintf(b, sizeof b, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x", g[3], g[2], g[1],
                  g[0], g[5], g[4], g[7], g[6], g[8], g[9], g[10], g[11], g[12], g[13], g[14], g[15]);
    return b;
}

std::string key_str(const Key& k) { return guid_str(k.g) + ":" + type_name(k.type) + ":" + std::to_string(k.part); }

std::string hash_str(const HashK& h) {
    char b[48];
    std::snprintf(b, sizeof b, "%u-%08x-%08x-%08x", h.size, h.h0, h.h1, h.h2);
    return b;
}

bool parse_hash(const std::string& s, HashK* out) {
    return std::sscanf(s.c_str(), "%u-%x-%x-%x", &out->size, &out->h0, &out->h1, &out->h2) == 4;
}

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}
std::string narrow(const std::wstring& s) {
    if (s.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    std::string o(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), o.data(), n, nullptr, nullptr);
    return o;
}

std::string api_path(int kind) {
    char buf[1024];
    size_t len = 0;
    if (g_api->path(kind, buf, sizeof buf, &len) != KILN_OK) return {};
    return std::string(buf, len);
}

bool read_file(const std::wstring& path, std::vector<uint8_t>* out, std::string* why) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        *why = "cannot open " + narrow(path);
        return false;
    }
    LARGE_INTEGER sz;
    GetFileSizeEx(h, &sz);
    out->resize(static_cast<size_t>(sz.QuadPart));
    DWORD read = 0;
    bool ok = !out->empty() ? ReadFile(h, out->data(), static_cast<DWORD>(out->size()), &read, nullptr) && read == out->size() : true;
    CloseHandle(h);
    if (!ok) *why = "read failed: " + narrow(path);
    return ok;
}

}  // namespace ka
