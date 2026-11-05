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

// ---- target allocation: chunks of 256 KB within reach of the data they point into ----
namespace {
struct Chunk {
    uintptr_t base, used, size;
};
std::vector<Chunk> g_chunks;
std::mutex g_alloc_mu;
// Each caller stores a signed 32-bit offset from a field at or next to the target, and checks it. So 2 GB is the limit; kReach leaves 16 MB for how far that field can be from the target.
constexpr intptr_t kReach = 0x7F000000;
constexpr intptr_t kNear = 0x40000000;  // a chunk further off than this is used only when there is no room for a new one

// Address space set aside at boot, committed a chunk at a time. By the time definitions go in, the game and the runtime have filled what is near their resources.
constexpr size_t kArena = 0x8000000;  // 128 MB
uintptr_t g_arena = 0;
size_t g_arena_used = 0;

uintptr_t from_arena(uintptr_t target, size_t size, intptr_t reach) {
    if (!g_arena || g_arena_used + size > kArena) return 0;
    const uintptr_t at = g_arena + g_arena_used;
    const intptr_t from = std::abs(static_cast<intptr_t>(at) - static_cast<intptr_t>(target));
    const intptr_t to = std::abs(static_cast<intptr_t>(at + size) - static_cast<intptr_t>(target));
    if (std::max(from, to) >= reach || !VirtualAlloc(reinterpret_cast<void*>(at), size, MEM_COMMIT, PAGE_READWRITE)) return 0;
    g_arena_used += size;
    return at;
}

// A free block within `reach` of the target, or 0.
uintptr_t reserve_near(uintptr_t target, size_t size, intptr_t reach) {
    SYSTEM_INFO si{};
    GetSystemInfo(&si);
    const uintptr_t gran = si.dwAllocationGranularity ? si.dwAllocationGranularity : 0x10000;
    const uintptr_t lo = target > static_cast<uintptr_t>(reach) + 0x10000 ? target - reach : 0x10000;
    const uintptr_t hi = target + reach;
    // Search upwards from `target`, then downwards.
    for (int dir = 0; dir < 2; ++dir) {
        uintptr_t a = dir == 0 ? (target + gran - 1) & ~(gran - 1) : (target - size) & ~(gran - 1);
        for (int tries = 0; tries < 4096 && a >= lo && a + size <= hi; ++tries) {
            MEMORY_BASIC_INFORMATION mbi{};
            if (!VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof mbi)) break;
            if (mbi.State == MEM_FREE) {
                const uintptr_t fbase = (reinterpret_cast<uintptr_t>(mbi.BaseAddress) + gran - 1) & ~(gran - 1);
                const uintptr_t fend = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
                const uintptr_t at = dir == 0 ? std::max(fbase, a) : std::min(a, (fend - size) & ~(gran - 1));
                if (at >= fbase && at + size <= fend) {
                    if (void* p = VirtualAlloc(reinterpret_cast<void*>(at), size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE))
                        return reinterpret_cast<uintptr_t>(p);
                }
            }
            if (dir == 0) a = (reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize + gran - 1) & ~(gran - 1);
            else {
                const uintptr_t b = reinterpret_cast<uintptr_t>(mbi.AllocationBase ? mbi.AllocationBase : mbi.BaseAddress);
                if (b < size + gran) break;
                a = (b - size) & ~(gran - 1);
            }
        }
    }
    return 0;
}
}  // namespace

void near_alloc_boot() {
    std::lock_guard lock(g_alloc_mu);
    // Under Wine the game's data lands in the low 4 GB, and from the middle of them 2 GB reaches nearly all of it. Elsewhere there is free space around any target, and this goes unused.
    for (const uintptr_t at : {0x78000000ull, 0x70000000ull, 0x60000000ull, 0x50000000ull, 0x40000000ull}) {
        g_arena = reinterpret_cast<uintptr_t>(VirtualAlloc(reinterpret_cast<void*>(at), kArena, MEM_RESERVE, PAGE_READWRITE));
        if (g_arena) break;
    }
    LOGI("near memory: %zu MB set aside at %p", kArena >> 20, reinterpret_cast<void*>(g_arena));
}

void* near_alloc(uintptr_t target, size_t n) {
    n = (n + 15) & ~size_t(15);
    std::lock_guard lock(g_alloc_mu);
    // Free address space near a resource can be scarce: under Wine the low 4 GB are nearly full. So a target uses the nearest chunk with room, or, when that is far off, a new
    // chunk nearer than it: what a far target leaves free stays for those that can reach nothing else.
    Chunk* nearest = nullptr;
    intptr_t nearest_d = 0;
    for (Chunk& c : g_chunks) {
        if (c.used + n > c.size) continue;
        const intptr_t from = std::abs(static_cast<intptr_t>(c.base + c.used) - static_cast<intptr_t>(target));
        const intptr_t to = std::abs(static_cast<intptr_t>(c.base + c.used + n) - static_cast<intptr_t>(target));
        const intptr_t d = std::max(from, to);
        if (d < kReach && (!nearest || d < nearest_d)) {
            nearest = &c;
            nearest_d = d;
        }
    }
    const auto take = [&](Chunk& c) {
        void* p = reinterpret_cast<void*>(c.base + c.used);
        c.used += n;
        return p;
    };
    if (nearest && nearest_d < kNear) return take(*nearest);
    const size_t size = std::max<size_t>(0x40000, (n + 0xFFFF) & ~size_t(0xFFFF));
    const intptr_t window = nearest ? kNear : kReach;
    uintptr_t base = from_arena(target, size, window);
    if (!base) base = reserve_near(target, size, window);
    if (base) {
        g_chunks.push_back(Chunk{base, n, size});
        LOGI("near memory: chunk %zu at %p, %zu KB, for target %p", g_chunks.size(), reinterpret_cast<void*>(base), size / 1024, reinterpret_cast<void*>(target));
        return reinterpret_cast<void*>(base);
    }
    if (nearest) return take(*nearest);
    LOGW("near memory: nothing free within 2 GB of %p for %zu bytes (%zu chunks)", reinterpret_cast<void*>(target), n, g_chunks.size());
    return nullptr;
}

void near_alloc_add(uintptr_t base, size_t size) {
    const uintptr_t at = (base + 15) & ~uintptr_t(15);
    if (at - base >= size) return;
    std::lock_guard lock(g_alloc_mu);
    g_chunks.push_back(Chunk{at, 0, size - (at - base)});
}

std::string near_alloc_report() {
    std::lock_guard lock(g_alloc_mu);
    size_t reserved = 0, used = 0;
    for (const Chunk& c : g_chunks) reserved += c.size, used += c.used;
    char b[128];
    std::snprintf(b, sizeof b, "%zu chunks, %zu of %zu KB used, %zu of %zu MB set aside taken", g_chunks.size(), used / 1024, reserved / 1024, g_arena_used >> 20, kArena >> 20);
    return b;
}

}  // namespace kc
