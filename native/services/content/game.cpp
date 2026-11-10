// SPDX-License-Identifier: MIT
// kiln.content: everything that touches game memory directly. Each __try function holds only plain data (no C++
// objects with destructors), as SEH requires; the callers build strings and vectors outside.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "content.h"
#include "nearwin.h"

namespace kc {

Engine E;

uint32_t fnv1a(const void* data, size_t n) {
    const auto* p = static_cast<const uint8_t*>(data);
    uint32_t h = 0x811c9dc5u;
    for (size_t i = 0; i < n; ++i) h = (h ^ p[i]) * 0x01000193u;
    return h;
}

std::string lower(std::string s) {
    for (char& c : s) c = static_cast<char>((c >= 'A' && c <= 'Z') ? c - 'A' + 'a' : c);
    return s;
}

// keen::Guid in memory: {u32 data1; u16 data2; u16 data3; u8 data4[8]} (Windows layout), printed the usual way.
std::string guid_text(const uint8_t g[16]) {
    uint32_t d1;
    uint16_t d2, d3;
    std::memcpy(&d1, g, 4);
    std::memcpy(&d2, g + 4, 2);
    std::memcpy(&d3, g + 6, 2);
    char b[40];
    std::snprintf(b, sizeof b, "%08x-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x", d1, d2, d3, g[8], g[9], g[10], g[11],
                  g[12], g[13], g[14], g[15]);
    return b;
}

std::string ref_text(const uint8_t g[16]) {
    return std::all_of(g, g + 16, [](uint8_t b) { return b == 0; }) ? "none" : guid_text(g);
}

bool guid_parse(const std::string& s, uint8_t out[16]) {
    unsigned d1, d2, d3, b[8];
    if (s.size() != 36 || std::sscanf(s.c_str(), "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x", &d1, &d2, &d3, &b[0], &b[1],
                                      &b[2], &b[3], &b[4], &b[5], &b[6], &b[7]) != 11)
        return false;
    const uint32_t v1 = d1;
    const uint16_t v2 = static_cast<uint16_t>(d2), v3 = static_cast<uint16_t>(d3);
    std::memcpy(out, &v1, 4);
    std::memcpy(out + 4, &v2, 2);
    std::memcpy(out + 6, &v3, 2);
    for (int i = 0; i < 8; ++i) out[8 + i] = static_cast<uint8_t>(b[i]);
    return true;
}

bool rd(uintptr_t address, void* out, size_t n) {
    if (!address || address < 0x10000 || address > UINTPTR_MAX - n) return false;
    __try {
        std::memcpy(out, reinterpret_cast<const void*>(address), n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::string read_text(uintptr_t ptr, uint64_t len) {
    char buf[513];
    if (!ptr || len == 0 || len > 512 || !rd(ptr, buf, static_cast<size_t>(len))) return {};
    return std::string(buf, static_cast<size_t>(len));
}

std::string read_blob_string(uintptr_t field) {
    int32_t off = 0;
    uint32_t len = 0;
    if (!rdv(field, off) || !rdv(field + 4, len) || off == 0 || len == 0 || len > 512) return {};
    std::string s = read_text(field + static_cast<intptr_t>(off), len);
    for (char c : s)
        if (static_cast<unsigned char>(c) < 0x20 || static_cast<unsigned char>(c) >= 0x7F) return {};
    return s;
}

static uintptr_t call_type_by_hash(Engine::TypeByHashFn fn, uint32_t hash) {
    __try {
        return fn(hash);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}

uintptr_t type_by_hash(uint32_t hash) { return E.type_by_hash ? call_type_by_hash(E.type_by_hash, hash) : 0; }

// ---- the game's HashMap<u32, u64> (bits, capacity, keys, values, count at E.hm_*) ----
static bool map_header(uintptr_t map, uint64_t* cap, uint32_t* count, uintptr_t* bits, uintptr_t* keys,
                       uintptr_t* values) {
    if (!rdv(map + E.hm_cap, *cap) || !rdv(map + E.hm_count, *count) || !rdv(map + E.hm_bits, *bits) ||
        !rdv(map + E.hm_keys, *keys) || !rdv(map + E.hm_values, *values))
        return false;
    return *cap && (*cap & (*cap - 1)) == 0 && *cap <= (1ull << 24) && *count <= *cap && *bits && *keys && *values;
}

uint32_t map_count(uintptr_t map) {
    uint64_t cap;
    uint32_t count;
    uintptr_t bits, keys, values;
    return map_header(map, &cap, &count, &bits, &keys, &values) ? count : 0;
}

static bool map_copy(uintptr_t bits, uintptr_t keys, uintptr_t values, uint64_t cap, uint32_t* k, uint64_t* v,
                     uint32_t room, uint32_t* n) {
    __try {
        const auto* b = reinterpret_cast<const uint64_t*>(bits);
        const auto* ks = reinterpret_cast<const uint32_t*>(keys);
        const auto* vs = reinterpret_cast<const uint64_t*>(values);
        *n = 0;
        for (uint64_t i = 0; i < cap && *n < room; ++i) {
            if (!((b[i >> 6] >> (i & 63)) & 1)) continue;
            k[*n] = ks[i];
            v[*n] = vs[i];
            ++*n;
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool map_read(uintptr_t map, std::vector<uint32_t>* keys, std::vector<uint64_t>* values) {
    uint64_t cap;
    uint32_t count;
    uintptr_t bits, ks, vs;
    if (!map_header(map, &cap, &count, &bits, &ks, &vs)) return false;
    keys->assign(count + 16, 0);
    values->assign(count + 16, 0);
    uint32_t n = 0;
    if (!map_copy(bits, ks, vs, cap, keys->data(), values->data(), static_cast<uint32_t>(keys->size()), &n)) return false;
    keys->resize(n);
    values->resize(n);
    return n == count;
}

// ---- writes ----
static bool guarded_copy(uintptr_t dst, const void* src, size_t n) {
    __try {
        std::memcpy(reinterpret_cast<void*>(dst), src, n);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

namespace {
std::atomic<uint64_t> g_direct{0}, g_unprotected{0}, g_protect_calls{0}, g_refused{0};
}
WriteStats write_stats() { return WriteStats{g_direct.load(), g_unprotected.load(), g_protect_calls.load(), g_refused.load()}; }

static bool writable(DWORD protect) {
    const DWORD p = protect & 0xFF;
    return p == PAGE_READWRITE || p == PAGE_WRITECOPY || p == PAGE_EXECUTE_READWRITE || p == PAGE_EXECUTE_WRITECOPY;
}

size_t write_batch(std::vector<Write>& w, uintptr_t* failed) {
    if (failed) *failed = 0;
    std::sort(w.begin(), w.end(), [](const Write& a, const Write& b) { return a.addr < b.addr; });
    constexpr uintptr_t kPage = 0x1000;
    size_t done = 0;
    size_t i = 0;
    while (i < w.size()) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<void*>(w[i].addr), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT) {
            if (failed && !*failed) *failed = w[i].addr;
            ++i;
            continue;
        }
        const uintptr_t rbase = reinterpret_cast<uintptr_t>(mbi.BaseAddress), rend = rbase + mbi.RegionSize;
        // every write that lies inside this region (same protection throughout)
        size_t j = i;
        while (j < w.size() && w[j].addr >= rbase && w[j].addr + w[j].n <= rend) ++j;
        if (j == i) {  // straddles the region's end: write it alone below with its own query
            j = i + 1;
        }
        if (writable(mbi.Protect)) {
            for (size_t k = i; k < j; ++k) {
                if (guarded_copy(w[k].addr, w[k].bytes, w[k].n)) ++done, ++g_direct;
                else if (failed && !*failed) *failed = w[k].addr;
            }
        } else if ((mbi.Protect & 0xFF) == PAGE_READONLY && mbi.Type == MEM_PRIVATE) {
            // Small clusters (writes on neighbouring pages, at most 16 pages): one VirtualProtect pair each, and only
            // over pages that hold a write, so pages the game itself might be working on are never touched. Each
            // cluster is re-checked right before (the region could change between clusters).
            size_t k = i;
            while (k < j) {
                uintptr_t lo = w[k].addr & ~(kPage - 1);
                uintptr_t hi = (w[k].addr + w[k].n + kPage - 1) & ~(kPage - 1);
                size_t m = k + 1;
                while (m < j && (w[m].addr & ~(kPage - 1)) <= hi && ((w[m].addr + w[m].n + kPage - 1) & ~(kPage - 1)) - lo <= 16 * kPage) {
                    hi = std::max(hi, (w[m].addr + w[m].n + kPage - 1) & ~(kPage - 1));
                    ++m;
                }
                hi = std::min(hi, rend);
                MEMORY_BASIC_INFORMATION cq{};
                const bool still = VirtualQuery(reinterpret_cast<void*>(lo), &cq, sizeof cq) && cq.State == MEM_COMMIT &&
                                   (cq.Protect & 0xFF) == PAGE_READONLY && cq.Type == MEM_PRIVATE &&
                                   reinterpret_cast<uintptr_t>(cq.BaseAddress) + cq.RegionSize >= hi;
                DWORD old = 0;
                if (still && VirtualProtect(reinterpret_cast<void*>(lo), hi - lo, PAGE_READWRITE, &old)) {
                    ++g_protect_calls;
                    for (size_t q = k; q < m; ++q) {
                        if (guarded_copy(w[q].addr, w[q].bytes, w[q].n)) ++done, ++g_unprotected;
                        else if (failed && !*failed) *failed = w[q].addr;
                    }
                    DWORD dummy = 0;
                    VirtualProtect(reinterpret_cast<void*>(lo), hi - lo, old, &dummy);
                } else if (failed && !*failed) {
                    *failed = w[k].addr;
                }
                k = m;
            }
        } else {
            for (size_t k = i; k < j; ++k) {
                ++g_refused;
                if (failed && !*failed) *failed = w[k].addr;
            }
        }
        i = j;
    }
    return done;
}

bool patch_code_byte(uintptr_t address, uint8_t expect, uint8_t value) {
    uint8_t now = 0;
    if (!rdv(address, now) || (now != expect && now != value)) return false;
    if (now == value) return true;
    DWORD old = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), 1, PAGE_EXECUTE_READWRITE, &old)) return false;
    const bool ok = guarded_copy(address, &value, 1);
    DWORD dummy = 0;
    VirtualProtect(reinterpret_cast<void*>(address), 1, old, &dummy);
    FlushInstructionCache(GetCurrentProcess(), reinterpret_cast<void*>(address), 1);
    return ok;
}

// ---- target allocation: chunks of 256 KB within reach of the data they point into (the policy: nearmem.cpp) ----
namespace {
std::mutex g_alloc_mu;

nearmem::NearMemory& near_memory() {
    static nearmem::WinSpace space;
    static nearmem::NearMemory memory(space, nearmem::Config{}, [](int level, const std::string& text) {
        if (level) LOGW("%s", text.c_str());
        else LOGI("%s", text.c_str());
    });
    return memory;
}
}  // namespace

void near_alloc_boot() {
    std::lock_guard lock(g_alloc_mu);
    near_memory().boot();
}

void* near_alloc(uintptr_t target, size_t n) {
    PhaseTimer pt(PH_NEAR_ALLOC);
    std::lock_guard lock(g_alloc_mu);
    return reinterpret_cast<void*>(near_memory().alloc(target, n));
}

void near_alloc_add(uintptr_t base, size_t size) {
    std::lock_guard lock(g_alloc_mu);
    near_memory().add(base, size);
}

std::string near_alloc_report() {
    std::lock_guard lock(g_alloc_mu);
    return near_memory().report();
}

}  // namespace kc
