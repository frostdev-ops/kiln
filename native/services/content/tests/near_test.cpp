// SPDX-License-Identifier: MIT
// kiln.content near memory (nearmem.cpp), host test: no game, no Wine. The policy runs against a simulated address
// space (SimSpace) built from maps of real layouts, so what happens on a native Windows server can be replayed:
//   - the layout of GitHub issue 14: targets 0x152F5D230 and 0x160A80F00 about 3.8 GB above the arena at 0x70000000, a
//     neighbourhood of more than 4096 regions and a few free holes;
//   - the Wine layout of docs/re/content.md section 10: the low 4 GB nearly full, targets 0x1714260 / 0xd7a83f0;
//   - a window with no free hole at all, a hole that is too small, a scan stopped by its budget;
//   - targets at the bottom and the top of the address space.
// run.sh builds and runs it. Exit code 0 = every check passed.
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <string>
#include <utility>
#include <vector>

#include "../nearmem.h"

using namespace kc::nearmem;

static int g_fail = 0, g_checks = 0;
#define CHECK(cond, ...)                                                \
    do {                                                                \
        ++g_checks;                                                     \
        if (!(cond)) {                                                  \
            ++g_fail;                                                   \
            std::printf("FAIL %s:%d: %s: ", __FILE__, __LINE__, #cond); \
            std::printf(__VA_ARGS__);                                   \
            std::printf("\n");                                          \
        }                                                               \
    } while (0)

constexpr uintptr_t KB = 0x400, MB = 0x100000, GB = 0x40000000;

// ---------------------------------------------------------------------------------------------- the simulation ----
// Used ranges of an address space (each one a region, as VirtualQuery reports an allocation) and the free gaps between.
static bool g_wine_pages = false;  // query() answers as Wine does (see Region in nearmem.h)

class SimSpace final : public Space {
public:
    Limits lim;
    bool wine_pages = g_wine_pages;
    std::map<uintptr_t, uintptr_t> used;  // base -> size, no overlaps
    std::map<uintptr_t, uintptr_t> ours;  // what reserve() made: where commit may work
    uint64_t clock = 0, queries = 0, reserves = 0, commits = 0, cost_us = 1;
    uintptr_t front_hint = 0;  // the lowest address front() may return
    bool fail_commit = false, refuse_reserve = false;
    std::vector<uintptr_t> asked;  // every address query() was called with

    uintptr_t round(uintptr_t n) const { return (n + lim.granularity - 1) & ~(lim.granularity - 1); }

    Limits limits() override { return lim; }

    bool query(uintptr_t addr, Region* out) override {
        const bool ok = query_true(addr, out);
        if (ok && wine_pages && addr >= lim.lowest) {  // the page asked about is the start of the region, the end is the run's
            const uintptr_t page = addr & ~uintptr_t(0xFFF), end = out->base + out->size;
            out->base = page;
            out->size = end - page;
        }
        return ok;
    }

    bool query_true(uintptr_t addr, Region* out) {
        ++queries;
        clock += cost_us;
        asked.push_back(addr);
        if (addr > lim.highest) return false;
        if (addr < lim.lowest) {
            *out = Region{0, lim.lowest, false, 0};  // never free for an application
            return true;
        }
        auto next = used.upper_bound(addr);
        uintptr_t gap_start = lim.lowest;
        if (next != used.begin()) {
            auto prev = std::prev(next);
            if (addr < prev->first + prev->second) {
                *out = Region{prev->first, prev->second, false, prev->first};
                return true;
            }
            gap_start = prev->first + prev->second;
        }
        const uintptr_t gap_end = next == used.end() ? lim.highest + 1 : next->first;
        *out = Region{gap_start, gap_end - gap_start, true, 0};
        return true;
    }

    bool is_free(uintptr_t at, uintptr_t n) const {
        if (at < lim.lowest || at + n > lim.highest + 1 || at + n < at) return false;
        auto next = used.lower_bound(at);
        if (next != used.end() && next->first < at + n) return false;
        if (next != used.begin()) {
            auto prev = std::prev(next);
            if (prev->first + prev->second > at) return false;
        }
        return true;
    }

    uintptr_t reserve(uintptr_t at, uintptr_t n) override {
        ++reserves;
        n = round(n);
        if (refuse_reserve || at % lim.granularity || !is_free(at, n)) return 0;
        used[at] = n;
        ours[at] = n;
        return at;
    }

    bool commit(uintptr_t at, uintptr_t n) override {
        ++commits;
        if (fail_commit) return false;
        auto it = ours.upper_bound(at);
        if (it == ours.begin()) return false;
        --it;
        return at >= it->first && at + n <= it->first + it->second;
    }

    uintptr_t front(uintptr_t n) override {
        n = round(n);
        uintptr_t a = std::max(lim.lowest, (front_hint + lim.granularity - 1) & ~(lim.granularity - 1));
        for (;;) {
            if (a + n > lim.highest + 1) return 0;
            if (is_free(a, n)) return a;
            auto it = used.upper_bound(a);
            if (it == used.begin()) return 0;
            --it;
            a = (it->first + it->second + lim.granularity - 1) & ~(lim.granularity - 1);
        }
    }

    uint64_t now_us() override { return clock; }

    // The game's own allocations ---------------------------------------------------------------------------------
    void take(uintptr_t at, uintptr_t n) {
        n = round(n);
        if (!is_free(at, n)) {
            std::printf("SimSpace: %llx+%llx is not free\n", (unsigned long long)at, (unsigned long long)n);
            std::abort();
        }
        used[at] = n;
    }
    // `count` regions of `size` bytes, back to back from `at`
    void take_run(uintptr_t at, uintptr_t count, uintptr_t size) {
        for (uintptr_t i = 0; i < count; ++i) take(at + i * size, size);
    }
    // everything free in [lo, hi) as regions of `piece` bytes (what the game's pools and heaps do to the space)
    void fill(uintptr_t lo, uintptr_t hi, uintptr_t piece) {
        uintptr_t a = lo;
        while (a < hi) {
            Region r;
            const bool ok = query_true(a, &r);
            if (!ok) break;
            const uintptr_t e = std::min(r.base + r.size, hi);
            if (r.free) {
                for (uintptr_t b = std::max(a, r.base); b < e; b += piece) used[b] = std::min(piece, e - b);
            }
            a = e;
        }
        queries = 0;
        clock = 0;
        asked.clear();
    }
    // The region that holds `at` becomes free (an allocation the game released).
    void open(uintptr_t at) {
        Region r;
        if (query_true(at, &r) && !r.free) used.erase(r.base);
    }
    // [at, at + n) becomes free inside the used region that holds it; the rest of the region stays used.
    void punch(uintptr_t at, uintptr_t n) {
        Region r;
        if (!query_true(at, &r) || r.free) return;
        used.erase(r.base);
        if (at > r.base) used[r.base] = at - r.base;
        if (at + n < r.base + r.size) used[at + n] = r.base + r.size - (at + n);
    }
};

// ---------------------------------------------------------------------------------------------- harness ----------
struct Logs {
    std::vector<std::pair<int, std::string>> lines;
    int warnings() const {
        int n = 0;
        for (auto& l : lines) n += l.first == 1;
        return n;
    }
    const std::string* warning(size_t i = 0) const {
        for (auto& l : lines)
            if (l.first == 1 && i-- == 0) return &l.second;
        return nullptr;
    }
    bool has(const std::string& s) const {
        for (auto& l : lines)
            if (l.second.find(s) != std::string::npos) return true;
        return false;
    }
};

static NearMemory make(SimSpace& sp, Logs& lg, Config cfg = {}) {
    return NearMemory(sp, std::move(cfg), [&lg](int level, const std::string& s) { lg.lines.emplace_back(level, s); });
}

static bool contains(const std::string* s, const char* part) { return s && s->find(part) != std::string::npos; }

// What every allocation must satisfy, whatever the layout: 16-aligned, both ends within reach of the target, inside
// memory the policy reserved.
static void check_alloc(SimSpace& sp, const Config& cfg, uintptr_t p, uintptr_t target, size_t n, const char* what) {
    CHECK(p != 0, "%s: no memory", what);
    if (!p) return;
    CHECK(p % 16 == 0, "%s: %llx is not 16-aligned", what, (unsigned long long)p);
    const uintptr_t far = std::max(p > target ? p - target : target - p, (p + n) > target ? p + n - target : target - p - n);
    CHECK(far < cfg.reach, "%s: %llx is %llx from the target %llx (reach %llx)", what, (unsigned long long)p, (unsigned long long)far,
          (unsigned long long)target, (unsigned long long)cfg.reach);
    auto it = sp.ours.upper_bound(p);
    CHECK(it != sp.ours.begin() && (--it, p + n <= it->first + it->second), "%s: %llx is not inside a reservation made by the policy", what, (unsigned long long)p);
}

// ---- the scan of dev.21, for contrast (native/services/content/game.cpp before this change): 4096 regions per direction
static uintptr_t legacy_reserve_near(Space& sp, uintptr_t target, size_t size, intptr_t reach) {
    const Limits lim = sp.limits();
    const uintptr_t gran = lim.granularity;
    const uintptr_t lo = target > static_cast<uintptr_t>(reach) + 0x10000 ? target - reach : 0x10000;
    const uintptr_t hi = target + reach;
    for (int dir = 0; dir < 2; ++dir) {
        uintptr_t a = dir == 0 ? (target + gran - 1) & ~(gran - 1) : (target - size) & ~(gran - 1);
        for (int tries = 0; tries < 4096 && a >= lo && a + size <= hi; ++tries) {
            Region r;
            if (!sp.query(a, &r)) break;
            if (r.free) {
                const uintptr_t fbase = (r.base + gran - 1) & ~(gran - 1);
                const uintptr_t fend = r.base + r.size;
                const uintptr_t at = dir == 0 ? std::max(fbase, a) : std::min(a, (fend - size) & ~(gran - 1));
                if (at >= fbase && at + size <= fend)
                    if (uintptr_t p = sp.reserve(at, size)) return p;
            }
            if (dir == 0) a = (r.base + r.size + gran - 1) & ~(gran - 1);
            else {
                if (r.base < size + gran) break;
                a = (r.base - size) & ~(gran - 1);
            }
        }
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------- the layouts ------
constexpr uintptr_t kItem = 0x152F5D230ull, kRecipe = 0x160A80F00ull;  // GitHub issue 14

// The neighbourhood of issue 14, as the game left it: 4 GB of back-to-back 128 KB regions around the item data (more than
// 4096 either way), a hole of 1 MB 768 MB above the item data and one of 512 KB 1.75 GB below it.
static void dense_neighbourhood(SimSpace& sp, uintptr_t* hole_up, uintptr_t* hole_down) {
    const uintptr_t first = 0xD3000000ull, last = 0x1D3000000ull;
    *hole_up = (kItem + 0x30000000ull) & ~uintptr_t(0xFFFF);
    *hole_down = (kItem - 0x70000000ull) & ~uintptr_t(0xFFFF);
    for (uintptr_t a = first; a < last; a += 128 * KB) {
        if ((a >= *hole_up && a < *hole_up + MB) || (a >= *hole_down && a < *hole_down + 512 * KB)) continue;
        sp.take(a, 128 * KB);
    }
}

// ---------------------------------------------------------------------------------------------- the tests ---------

// Issue 14, the arena of dev.21 at 0x70000000 and no other: what the old scan could not do, the new one does.
static void test_issue_dense() {
    std::printf("== issue 14: dense neighbourhood, free holes beyond 4096 regions\n");
    SimSpace sp;
    uintptr_t up = 0, down = 0;
    dense_neighbourhood(sp, &up, &down);
    CHECK(sp.reserve(0x70000000, 128 * MB) == 0x70000000, "the dev.21 arena");
    // the old scan: 4096 regions up are 512 MB of these regions, and 4096 steps down about 1.3 GB: neither hole is reached
    CHECK(legacy_reserve_near(sp, kItem, 0x40000, 0x7F000000) == 0, "dev.21's scan found a hole (the layout does not reproduce the issue)");
    CHECK(legacy_reserve_near(sp, kRecipe, 0x40000, 0x7F000000) == 0, "dev.21's scan found a hole near the recipe");
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    nm.add(0x70000000, 0);  // no room is no chunk
    CHECK(nm.chunks().empty(), "add of nothing");
    const uintptr_t p = nm.alloc(kItem, 2112);
    check_alloc(sp, cfg, p, kItem, 2112, "item clone");
    CHECK(p >= up && p < up + MB, "the clone is in the hole above the item data (%llx)", (unsigned long long)p);
    CHECK(nm.last_scan().up.regions > 4096, "the walk went on past 4096 regions (%u)", nm.last_scan().up.regions);
    CHECK(nm.arenas().size() == 1, "one arena was made (%zu)", nm.arenas().size());
    CHECK(lg.warnings() == 0, "no warning");
    // the recipe data is 205 MB above: the same chunk serves it, without a second scan
    const uint64_t q = sp.queries;
    const uintptr_t r = nm.alloc(kRecipe, 384);
    check_alloc(sp, cfg, r, kRecipe, 384, "recipe clone");
    CHECK(sp.queries == q, "the recipe took no scan (%llu queries)", (unsigned long long)(sp.queries - q));
    CHECK(nm.chunks().size() == 1, "one chunk for both (%zu)", nm.chunks().size());
}

// Issue 14 with the boot arenas: the game fills the whole space around its data and nothing is left to scan.
static void test_issue_boot_ladder() {
    std::printf("== issue 14: arenas reserved at boot, the game then fills every free address\n");
    SimSpace sp;
    sp.front_hint = 0x20000000;  // the system's next default allocation: in the low region
    sp.take(0x10000000, 0x2000000);  // a module
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    nm.boot();
    CHECK(nm.arenas().size() == 8, "low arena + 7 above 4 GB (%zu)", nm.arenas().size());
    CHECK(!nm.arenas().empty() && nm.arenas()[0].base == 0x78000000, "the low arena stays at 0x78000000");
    CHECK(nm.arenas().size() > 1 && nm.arenas()[1].base == 0x100000000ull, "the ladder starts at 4 GB (%llx)", (unsigned long long)nm.arenas()[1].base);
    CHECK(lg.has("128 MB set aside at 0000000078000000"), "the boot line of dev.21 is still logged");
    // the game's allocations land everywhere else, back to back
    sp.fill(0x10000, 0x100000000ull, 256 * KB);
    sp.fill(0x100000000ull, 0x700000000ull, 256 * KB);
    const uintptr_t p = nm.alloc(kItem, 2112);
    check_alloc(sp, cfg, p, kItem, 2112, "item clone");
    const uintptr_t r = nm.alloc(kRecipe, 384);
    check_alloc(sp, cfg, r, kRecipe, 384, "recipe clone");
    CHECK(lg.warnings() == 0, "no warning (%s)", lg.warning() ? lg.warning()->c_str() : "");
    // hundreds of definitions: 3000 items of 2112 bytes from different places of the data
    int fail = 0;
    for (int i = 0; i < 3000; ++i) {
        const uintptr_t t = 0x120000000ull + static_cast<uintptr_t>(i) * 0x40000;
        const uintptr_t q = nm.alloc(t, 2112);
        if (!q) ++fail;
        else check_alloc(sp, cfg, q, t, 2112, "many");
    }
    CHECK(fail == 0, "%d of 3000 allocations failed", fail);
    std::printf("   %s\n", nm.report().c_str());
}

// The Wine layout: the low 4 GB nearly full, the data in it; a clone served by kiln.assets above 4 GB.
static void test_wine() {
    std::printf("== Wine: low 4 GB nearly full\n");
    SimSpace sp;
    sp.front_hint = 0x10030000;
    sp.take(0x400000, 0x1000000);
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    nm.boot();
    CHECK(!nm.arenas().empty() && nm.arenas()[0].base == 0x78000000, "the arena is where dev.21 put it");
    // 1 GB of pools from 0x10030000, the 490 MB read-only mapping, the 2 GB block above the arena, a few MB of holes
    sp.fill(0x10030000, 0x50030000, 64 * MB);
    sp.fill(0x50030000, 0x78000000, 490 * MB);
    sp.fill(0x80000000ull, 0x100000000ull, 2048 * MB);
    sp.fill(0x10000, 0x10030000, 1 * MB);
    sp.punch(0x10030000 + 5 * 64 * MB + MB, 2 * MB);
    sp.punch(0x50030000 + 100 * MB, 1 * MB);
    {
        // a skill node low in memory, then one 226 MB above it: the arena is 1.87 GB / 1.65 GB away
        uintptr_t t = 0x1714260;
        const uint64_t before = sp.queries;
        uintptr_t p = nm.alloc(t, 2112);
        check_alloc(sp, cfg, p, t, 2112, "wine low");
        CHECK(sp.queries == before, "the low arena served %llx without a scan", (unsigned long long)t);
        t = 0xd7a83f0;
        p = nm.alloc(t, 2112);
        check_alloc(sp, cfg, p, t, 2112, "wine low 2");
        t = 0x1000000;  // beside the first chunk: no scan
        const uint64_t q = sp.queries;
        p = nm.alloc(t, 4000);
        check_alloc(sp, cfg, p, t, 4000, "wine low 3");
        CHECK(sp.queries == q, "a target next to a chunk needs no scan");
    }
    CHECK(lg.warnings() == 0, "no warning");
    // a target above 4 GB, heap around it full: a ladder arena is in reach
    sp.fill(0x100000000ull, 0x400000000ull, 4 * MB);
    const uintptr_t t = 0x250000000ull;
    const uintptr_t p = nm.alloc(t, 2112);
    check_alloc(sp, cfg, p, t, 2112, "wine heap above 4 GB");
    // the low arena candidates: the first free one is taken
    SimSpace sp2;
    sp2.take(0x78000000, 0x10000);
    Logs lg2;
    NearMemory nm2 = make(sp2, lg2, cfg);
    nm2.boot();
    CHECK(nm2.arenas()[0].base == 0x70000000, "0x78000000 taken: the arena is at 0x70000000 (%llx)", (unsigned long long)nm2.arenas()[0].base);
    SimSpace sp3;
    for (uintptr_t a : {0x78000000ull, 0x70000000ull, 0x60000000ull, 0x50000000ull, 0x40000000ull}) sp3.take(a, 0x10000);
    Logs lg3;
    NearMemory nm3 = make(sp3, lg3, cfg);
    nm3.boot();
    CHECK(lg3.has("no room for 128 MB at any of the fixed low addresses"), "the log says that no low arena was possible");
    CHECK(nm3.arenas().size() == 7, "the ladder is made anyway (%zu)", nm3.arenas().size());
}

// The ladder leaves no address up to its end out of reach of an arena; it follows the system's front.
static void test_ladder() {
    std::printf("== the ladder\n");
    SimSpace sp;
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    nm.boot();
    int bad = 0;
    uintptr_t last = 0;
    for (const auto& a : nm.arenas()) last = std::max(last, a.base);
    CHECK(nm.arenas().size() == 8 && last == 0x100000000ull + 6 * cfg.ladder_stride, "7 arenas up to span (%llx)", (unsigned long long)last);
    // every address from the first arena to its end plus the reach has an arena in reach
    for (uintptr_t t = 0x100000000ull; t < last + cfg.reach - 256 * KB; t += 64 * MB) {
        uintptr_t best = ~uintptr_t(0);
        for (const auto& a : nm.arenas()) {
            if (a.base < 0x100000000ull) continue;
            best = std::min(best, std::max(a.base > t ? a.base - t : t - a.base, a.base + 256 * KB > t ? a.base + 256 * KB - t : t - a.base - 256 * KB));
        }
        if (best >= cfg.reach) ++bad;
    }
    CHECK(bad == 0, "%d targets in the ladder's range have no arena in reach", bad);
    // a front above 4 GB: the ladder starts there
    SimSpace sp2;
    sp2.front_hint = 0x180000000ull;
    Logs lg2;
    NearMemory nm2 = make(sp2, lg2, cfg);
    nm2.boot();
    CHECK(nm2.arenas().size() > 1 && nm2.arenas()[1].base == 0x180000000ull, "the ladder starts at the front (%llx)", (unsigned long long)(nm2.arenas().size() > 1 ? nm2.arenas()[1].base : 0));
    // something big reserved above 4 GB (the runtime's heap): the game's blocks go after it, and so does the ladder
    SimSpace sp5;
    sp5.take(0x100000000ull, 0x1000000000ull);  // 64 GB
    sp5.take(0x1100010000ull, 0x10000);  // a gap of 64 KB after it, too small
    Logs lg5;
    NearMemory nm5 = make(sp5, lg5, cfg);
    nm5.boot();
    CHECK(nm5.arenas().size() > 1 && nm5.arenas()[1].base >= 0x1100020000ull, "the ladder starts after the reservation (%llx)", (unsigned long long)(nm5.arenas().size() > 1 ? nm5.arenas()[1].base : 0));
    CHECK(lg5.has("arena(s) of 128 MB set aside above 4 GB, from 00000011"), "said where it starts");
    // a slot taken by something else (the system's own allocation, a module): the next free spot up
    SimSpace sp3;
    sp3.take(0x1E0000000ull, 0x10000);
    Logs lg3;
    NearMemory nm3 = make(sp3, lg3, cfg);
    nm3.boot();
    CHECK(nm3.arenas().size() > 2 && nm3.arenas()[1].base == 0x100000000ull && nm3.arenas()[2].base == 0x1E8000000ull, "a taken slot: the next one (%llx)",
          (unsigned long long)(nm3.arenas().size() > 2 ? nm3.arenas()[2].base : 0));
    // an address space with a small top (a 32-bit process): nothing above 4 GB, no crash
    SimSpace sp4;
    sp4.lim.highest = 0xFFFEFFFF;
    Logs lg4;
    NearMemory nm4 = make(sp4, lg4, cfg);
    nm4.boot();
    CHECK(nm4.arenas().size() == 1, "32-bit space: the low arena only (%zu)", nm4.arenas().size());
    CHECK(lg4.has("no room for the arenas above 4 GB"), "said so");
}

// No free hole in the window at all: the line names everything a person needs.
static void test_no_room() {
    std::printf("== no free hole in the window\n");
    SimSpace sp;
    sp.fill(0x10000, 0x300000000ull, 128 * KB);
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    CHECK(nm.alloc(kItem, 2112) == 0, "memory from nowhere");
    CHECK(lg.warnings() == 1, "one warning (%d)", lg.warnings());
    const std::string* w = lg.warning();
    CHECK(contains(w, "nothing free within 2 GB of 0000000152F5D230 for 2112 bytes (0 chunks)"), "the dev.21 prefix and the target: %s", w ? w->c_str() : "");
    CHECK(contains(w, "window 00000000D2F5D230 to 00000001D1F5D230") || contains(w, "window 00000000D3F5D230 to 00000001D1F5D230") || contains(w, "window 00000000D2"), "the window: %s", w ? w->c_str() : "");
    CHECK(contains(w, "regions above to the window's edge") && contains(w, "regions below to the window's edge"), "both sides walked to the edge: %s", w ? w->c_str() : "");
    CHECK(contains(w, "none inside the window that can hold a block") && contains(w, "the whole window is reserved or committed"), "why: %s", w ? w->c_str() : "");
    CHECK(contains(w, "no arena was set aside at boot"), "arenas: %s", w ? w->c_str() : "");
    CHECK(nm.last_scan().up.regions > 4096 && nm.last_scan().down.regions > 4096, "regions %u / %u", nm.last_scan().up.regions, nm.last_scan().down.regions);
    std::printf("   %s\n", w ? w->c_str() : "(none)");
    // the neighbourhood is not walked again for each definition, and not logged again
    const uint64_t q = sp.queries;
    CHECK(nm.alloc(kItem + 0x10000, 2112) == 0 && nm.alloc(kItem, 100) == 0, "still nothing");
    CHECK(sp.queries == q, "no second walk (%llu queries)", (unsigned long long)(sp.queries - q));
    CHECK(lg.warnings() == 1, "no second line (%d)", lg.warnings());
    CHECK(nm.refusals() == 2, "two refused without a walk (%llu)", (unsigned long long)nm.refusals());
    // another neighbourhood is walked
    CHECK(nm.alloc(kItem + 100 * MB, 2112) == 0 && lg.warnings() == 2, "another neighbourhood: its own line");
    // after a while it is looked at again: a hole has appeared
    sp.clock += 6'000'000;
    sp.open(kItem + 0x100000);
    const uintptr_t p = nm.alloc(kItem, 2112);
    check_alloc(sp, cfg, p, kItem, 2112, "after the hole opened");
}

static void test_too_small() {
    std::printf("== a hole too small\n");
    SimSpace sp;
    sp.fill(0x10000, 0x300000000ull, 128 * KB);
    const uintptr_t hole = (kItem + 0x20000000ull) & ~uintptr_t(0xFFFF);
    sp.punch(hole, 64 * KB);  // one free run of 64 KB, one granule
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    // 100000 bytes need 128 KB (two granules)
    CHECK(nm.alloc(kItem, 100000) == 0, "memory from a run of 64 KB for 100000 bytes");
    const std::string* w = lg.warning();
    CHECK(contains(w, "the largest usable one 64 KB") && contains(w, "too small for the 128 KB this request needs"), "%s", w ? w->c_str() : "");
    std::printf("   %s\n", w ? w->c_str() : "(none)");
    // a request that fits in 64 KB uses the run
    const uintptr_t p = nm.alloc(kItem + 0x1000000, 2112);
    check_alloc(sp, cfg, p, kItem + 0x1000000, 2112, "a small request in a 64 KB run");
    CHECK(nm.arenas().size() == 1 && nm.arenas()[0].size == 64 * KB, "the arena is the run (%zu arenas)", nm.arenas().size());
}

static void test_budget() {
    std::printf("== the region budget\n");
    SimSpace sp;
    const uintptr_t hole = (kItem + 0x20000000ull) & ~uintptr_t(0xFFFF);
    for (uintptr_t a = 0xD3000000ull; a < 0x1D3000000ull; a += 128 * KB)
        if (a < hole || a >= hole + MB) sp.take(a, 128 * KB);
    Logs lg;
    Config cfg;
    cfg.scan_budget = 1000;
    NearMemory nm = make(sp, lg, cfg);
    CHECK(nm.alloc(kItem, 2112) == 0, "the budget ends the walk before the hole (4096 regions away)");
    const std::string* w = lg.warning();
    CHECK(contains(w, "stopped by the limit of 1000 regions at") && contains(w, "the window was not seen in full"), "%s", w ? w->c_str() : "");
    std::printf("   %s\n", w ? w->c_str() : "(none)");
    Logs lg2;
    NearMemory full = make(sp, lg2, Config{});
    const uintptr_t p = full.alloc(kItem, 2112);
    check_alloc(sp, Config{}, p, kItem, 2112, "the default budget");
    CHECK(p >= hole && p < hole + MB, "found the hole");
}

static void test_edges() {
    std::printf("== the top and the bottom of the address space\n");
    Config cfg;
    {
        // bottom: the target near 0x10000, the window starts at the lowest address
        SimSpace sp;
        sp.take(0x10000, 0x20000);
        sp.take(0x40000, 0x10000);
        Logs lg;
        NearMemory nm = make(sp, lg, cfg);
        const uintptr_t p = nm.alloc(0x18000, 2112);
        check_alloc(sp, cfg, p, 0x18000, 2112, "bottom");
        CHECK(p >= sp.lim.lowest, "not below the lowest address");
        CHECK(std::all_of(sp.asked.begin(), sp.asked.end(), [&](uintptr_t a) { return a >= sp.lim.lowest - 1 && a <= sp.lim.highest; }), "a query outside the address space");
        CHECK(nm.last_scan().lo == sp.lim.lowest, "window starts at the lowest address (%llx)", (unsigned long long)nm.last_scan().lo);
    }
    {
        // the bottom with nothing free but a hole far above
        SimSpace sp;
        sp.fill(0x10000, 0x90000000ull, 1 * MB);
        Logs lg;
        NearMemory nm = make(sp, lg, cfg);
        CHECK(nm.alloc(0x20000, 2112) == 0, "no room at the bottom");
        CHECK(nm.last_scan().down.regions == 1 && nm.last_scan().down.stop == NearMemory::Side::Edge, "one region below, then the edge");
        CHECK(contains(lg.warning(), "1 regions below to the window's edge"), "%s", lg.warning() ? lg.warning()->c_str() : "");
    }
    {
        // top: the target just under the top, the free space below it
        SimSpace sp;
        const uintptr_t target = 0x7FFFFFFE0000ull;
        sp.take(0x7FFFFFFD0000ull, 0x20000);
        sp.take(0x7FFFFFFB0000ull, 0x20000);
        Logs lg;
        NearMemory nm = make(sp, lg, cfg);
        const uintptr_t p = nm.alloc(target, 2112);
        check_alloc(sp, cfg, p, target, 2112, "top");
        CHECK(p + 2112 <= sp.lim.highest + 1, "not above the top");
        CHECK(std::all_of(sp.asked.begin(), sp.asked.end(), [&](uintptr_t a) { return a <= sp.lim.highest; }), "a query above the top of the address space");
        CHECK(nm.arenas().size() == 1, "one arena");
        // the top with no room: the failure text, no overflow
        SimSpace sp2;
        sp2.fill(0x7FFF00000000ull, 0x7FFFFFFF0000ull, 1 * MB);
        Logs lg2;
        NearMemory nm2 = make(sp2, lg2, cfg);
        CHECK(nm2.alloc(target, 2112) == 0, "no room at the top");
        CHECK(nm2.last_scan().up.stop == NearMemory::Side::Edge, "the upper side ends at the top");
        CHECK(sp2.queries < 100000, "a bounded walk (%llu)", (unsigned long long)sp2.queries);
        // a target above the address space (a bad pointer): no crash, no wrap
        const int warned = lg2.warnings();
        CHECK(nm2.alloc(0xFFFFFFFFFFFFF000ull, 2112) == 0 && lg2.warnings() == warned + 1, "a wild target: nothing, and said so");
    }
    {
        // a 32-bit sized space
        SimSpace sp;
        sp.lim.highest = 0xFFFEFFFF;
        sp.fill(0x10000, 0xF0000000, 1 * MB);
        Logs lg;
        NearMemory nm = make(sp, lg, cfg);
        const uintptr_t t = 0xE0000000;
        const uintptr_t p = nm.alloc(t, 4096);
        check_alloc(sp, cfg, p, t, 4096, "32-bit space");
        CHECK(p + 4096 <= 0xFFFF0000ull, "inside");
    }
}

static void test_nearest() {
    std::printf("== the nearer side first\n");
    SimSpace sp;
    const uintptr_t t = 0x200000000ull;
    sp.fill(0x100000000ull, 0x300000000ull, 256 * KB);
    const uintptr_t up = t + 100 * MB, down = t - 50 * MB;
    sp.open(up);
    sp.open(down);
    Logs lg;
    Config cfg;
    NearMemory nm = make(sp, lg, cfg);
    const uintptr_t p = nm.alloc(t, 2112);
    check_alloc(sp, cfg, p, t, 2112, "nearest");
    CHECK(p < t && t - p < 60 * MB, "the hole 50 MB below, not the one 100 MB above (%llx)", (unsigned long long)p);
    // the block is taken from the end of the run nearest the target
    CHECK(nm.arenas().size() == 1 && nm.arenas()[0].base + nm.arenas()[0].size <= t, "below the target");
}

static void test_arena_use() {
    std::printf("== chunks, arenas that run out, refusals\n");
    SimSpace sp;
    Logs lg;
    Config cfg;
    cfg.low_size = 512 * KB;  // two chunks
    cfg.ladder_span = 0;
    cfg.ladder_size = 512 * KB;
    NearMemory nm = make(sp, lg, cfg);
    nm.boot();
    CHECK(nm.arenas().size() == 2, "low arena + one (%zu)", nm.arenas().size());
    sp.fill(0x10000, 0x100000000ull, 1 * MB);  // the game fills the rest of the space
    // targets in reach of the low arena only; a request of nearly a chunk never shares
    const uintptr_t t = 0x60000000;
    for (int i = 0; i < 2; ++i) check_alloc(sp, cfg, nm.alloc(t, 0x40000 - 16), t, 0x40000 - 16, "chunk");
    CHECK(nm.alloc(t, 0x40000 - 16) == 0, "the low arena is full: no memory");
    CHECK(contains(lg.warning(), "2 arena(s), the nearest at 0000000078000000 is") && contains(lg.warning(), "full"), "arena state named: %s", lg.warning() ? lg.warning()->c_str() : "");
    // add(): a clone's spare room serves the next request in reach
    nm.add(0x6FF00000, 8192);
    const uintptr_t p = nm.alloc(0x6FF01000, 4000);
    CHECK(p >= 0x6FF00000 && p < 0x6FF02000, "the spare room (%llx)", (unsigned long long)p);
    // commit refused
    SimSpace sp2;
    sp2.fail_commit = true;
    Logs lg2;
    NearMemory nm2 = make(sp2, lg2, Config{});
    nm2.boot();
    CHECK(nm2.alloc(0x70001000, 1000) == 0, "the system refuses to commit");
    CHECK(contains(lg2.warning(), "refused to commit"), "%s", lg2.warning() ? lg2.warning()->c_str() : "");
    // reservations refused in runs that fit
    SimSpace sp3;
    sp3.fill(0x10000, 0x300000000ull, 128 * KB);
    sp3.open(kItem + 0x1000000);
    sp3.refuse_reserve = true;
    Logs lg3;
    NearMemory nm3 = make(sp3, lg3, Config{});
    CHECK(nm3.alloc(kItem, 2112) == 0, "reserve refused");
    CHECK(contains(lg3.warning(), "refused 1 reservation(s) in runs that fit"), "%s", lg3.warning() ? lg3.warning()->c_str() : "");
}

// A scan that finds a chunk further off than 1 GB is optional when a chunk of 1-2 GB already serves: it is bounded.
static void test_optional() {
    std::printf("== an optional scan is bounded\n");
    SimSpace sp;
    Config cfg;
    Logs lg;
    NearMemory nm = make(sp, lg, cfg);
    nm.add(0x150000000ull, 4 * MB);            // a chunk 1.5 GB below the target
    sp.take(0x150000000ull, 4 * MB);
    sp.ours[0x150000000ull] = 4 * MB;
    sp.fill(0x100000000ull, 0x300000000ull, 128 * KB);
    const uintptr_t t = 0x1B0000000ull;        // 1.5 GB above the chunk
    const uint64_t q = sp.queries;
    const uintptr_t p = nm.alloc(t, 2112);
    CHECK(p >= 0x150000000ull && p < 0x150400000ull, "the far chunk serves it (%llx)", (unsigned long long)p);
    CHECK(sp.queries - q <= 2ull * cfg.optional_budget + 4, "the courtesy scan is bounded: %llu queries", (unsigned long long)(sp.queries - q));
    CHECK(lg.warnings() == 0, "no warning: a chunk served");
    const uint64_t q2 = sp.queries;
    nm.alloc(t + 0x100000, 2112);
    CHECK(sp.queries == q2, "and not repeated for the neighbourhood");
}

int main() {
    std::setvbuf(stdout, nullptr, _IOLBF, 0);
    // every test twice: as Windows answers a query, and as Wine does
    for (const bool wine : {false, true}) {
        g_wine_pages = wine;
        std::printf("#### %s\n", wine ? "queries answered as Wine does (the queried page is the region's base)" : "queries answered as Windows does");
        test_issue_dense();
        test_issue_boot_ladder();
        test_wine();
        test_ladder();
        test_no_room();
        test_too_small();
        test_budget();
        test_edges();
        test_nearest();
        test_arena_use();
        test_optional();
    }
    std::printf("near-test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
