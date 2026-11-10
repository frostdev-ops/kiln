// SPDX-License-Identifier: MIT
// kiln.content near memory on a real address space: nearmem.cpp with nearwin.h (VirtualQuery / VirtualAlloc) under
// Wine, no game. near_test.cpp replays maps of address spaces on the host; this one checks that the Windows side of the
// seam does what the simulation assumes, and measures the walk of a window over a fragmented space.
//   native/services/content/tests/run.sh
#include <windows.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "../nearmem.h"
#include "../nearwin.h"

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

static void log_line(int level, const std::string& s) { std::printf("   [%s] %s\n", level ? "W" : "I", s.c_str()); }

static uintptr_t dist(uintptr_t a, uintptr_t b) { return a > b ? a - b : b - a; }

int main() {
    WinSpace space;
    const Limits lim = space.limits();
    std::printf("limits: granularity %llx, lowest %llx, highest %llx\n", (unsigned long long)lim.granularity, (unsigned long long)lim.lowest,
                (unsigned long long)lim.highest);

    // ---- the seam: query, reserve, commit, front
    {
        Region r;
        CHECK(space.query(reinterpret_cast<uintptr_t>(&g_fail), &r) && !r.free && r.size > 0, "query of a global");
        const uintptr_t front = space.front(lim.granularity);
        CHECK(front != 0, "front");
        CHECK(space.query(front, &r) && r.free, "the front is free");
        const uintptr_t at = space.reserve(front, 4 * lim.granularity);
        CHECK(at == front, "reserve exactly at the front (%llx)", (unsigned long long)at);
        CHECK(space.reserve(front, lim.granularity) == 0, "a taken address is refused");
        CHECK(space.commit(front, lim.granularity), "commit");
        std::memset(reinterpret_cast<void*>(front), 0xAB, lim.granularity);
        CHECK(space.query(front, &r) && !r.free, "reserved is not free");
        CHECK(space.query(~uintptr_t(0) - 0xFFF, &r) == false, "beyond the top of the address space");
        std::printf("front of the address space: %llx\n", (unsigned long long)front);
    }

    // ---- boot arenas under Wine
    {
        NearMemory nm(space, Config{}, log_line);
        nm.boot();
        CHECK(!nm.arenas().empty(), "arenas");
        const uintptr_t target = reinterpret_cast<uintptr_t>(&g_fail);
        const uintptr_t p = nm.alloc(target, 2112);
        CHECK(p != 0 && dist(p, target) < Config{}.reach, "a chunk near the image (%llx for %llx)", (unsigned long long)p, (unsigned long long)target);
        if (p) std::memset(reinterpret_cast<void*>(p), 0xCD, 2112);
        std::printf("   %s\n", nm.report().c_str());
    }

    // ---- a fragmented window: 4 GB of back-to-back 64 KB reservations around a target (more than 65,000 regions)
    {
        void* play = VirtualAlloc(nullptr, 6ull << 30, MEM_RESERVE, PAGE_NOACCESS);
        CHECK(play != nullptr, "6 GB of address space to play in");
        if (!play) return 1;
        const uintptr_t base = reinterpret_cast<uintptr_t>(play);
        VirtualFree(play, 0, MEM_RELEASE);
        const uintptr_t first = base + (1ull << 30), count = (4ull << 30) / lim.granularity;
        std::vector<uintptr_t> held;
        held.reserve(count);
        LARGE_INTEGER f, a, b;
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&a);
        for (uintptr_t i = 0; i < count; ++i) {
            if (void* p = VirtualAlloc(reinterpret_cast<void*>(first + i * lim.granularity), lim.granularity, MEM_RESERVE, PAGE_NOACCESS)) held.push_back(first + i * lim.granularity);
        }
        QueryPerformanceCounter(&b);
        std::printf("fragmented: %zu reservations of %llx at %llx in %.0f ms\n", held.size(), (unsigned long long)lim.granularity, (unsigned long long)first,
                    1000.0 * (b.QuadPart - a.QuadPart) / f.QuadPart);
        CHECK(held.size() == count, "all reserved");
        const uintptr_t target = base + (3ull << 30);

        Config cfg;
        {
            NearMemory nm(space, cfg, log_line);
            QueryPerformanceCounter(&a);
            const uintptr_t p = nm.alloc(target, 2112);
            QueryPerformanceCounter(&b);
            const double ms = 1000.0 * (b.QuadPart - a.QuadPart) / f.QuadPart;
            const auto& s = nm.last_scan();
            const double regions = s.up.regions + s.down.regions;
            std::printf("full walk with no hole: %u + %u regions in %.1f ms (%.2f us per region)\n", s.up.regions, s.down.regions, ms, regions ? ms * 1000.0 / regions : 0.0);
            CHECK(p == 0, "no memory in a window that is full");
            CHECK(s.up.regions > 4096 && s.down.regions > 4096, "the walk went past 4096 regions each way (%u / %u)", s.up.regions, s.down.regions);
            CHECK(s.up.stop == NearMemory::Side::Edge && s.down.stop == NearMemory::Side::Edge, "both sides walked to the edge");
            CHECK(ms < 10000, "the walk is bounded (%.0f ms)", ms);
        }
        // a hole far past 4096 regions
        const uintptr_t hole = first + (3ull << 30) + (600ull << 20);
        const uintptr_t hole_at = hole & ~(lim.granularity - 1);
        CHECK(VirtualFree(reinterpret_cast<void*>(hole_at), 0, MEM_RELEASE) != 0, "free a granule 600 MB above the target");
        {
            NearMemory nm(space, cfg, log_line);
            QueryPerformanceCounter(&a);
            const uintptr_t p = nm.alloc(target, 2112);
            QueryPerformanceCounter(&b);
            const auto& s = nm.last_scan();
            std::printf("walk to a hole 600 MB away: %u + %u regions in %.1f ms\n", s.up.regions, s.down.regions, 1000.0 * (b.QuadPart - a.QuadPart) / f.QuadPart);
            CHECK(p >= hole_at && p < hole_at + lim.granularity, "the hole is used (%llx)", (unsigned long long)p);
            if (p) std::memset(reinterpret_cast<void*>(p), 0x5A, 2112);
            CHECK(s.up.regions > 4096, "past the limit of dev.21 (%u regions up)", s.up.regions);
        }
    }
    std::printf("near-win-test: %d checks, %d failed\n", g_checks, g_fail);
    return g_fail ? 1 : 0;
}
