// SPDX-License-Identifier: MIT
// kiln.content: the Windows side of nearmem.h's Space. Nothing else in it (the policy and its unit test do not include
// <windows.h>).
#pragma once
#include <windows.h>

#include "nearmem.h"

namespace kc::nearmem {

class WinSpace final : public Space {
public:
    Limits limits() override {
        SYSTEM_INFO si{};
        GetSystemInfo(&si);
        Limits l;
        if (si.dwAllocationGranularity) l.granularity = si.dwAllocationGranularity;
        if (si.lpMinimumApplicationAddress) l.lowest = reinterpret_cast<uintptr_t>(si.lpMinimumApplicationAddress);
        if (si.lpMaximumApplicationAddress) l.highest = reinterpret_cast<uintptr_t>(si.lpMaximumApplicationAddress);
        return l;
    }

    bool query(uintptr_t addr, Region* out) override {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<void*>(addr), &mbi, sizeof mbi)) return false;
        out->base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
        out->size = mbi.RegionSize;
        out->free = mbi.State == MEM_FREE;
        out->alloc_base = out->free ? 0 : reinterpret_cast<uintptr_t>(mbi.AllocationBase);
        return true;
    }

    uintptr_t reserve(uintptr_t at, uintptr_t n) override {
        return reinterpret_cast<uintptr_t>(VirtualAlloc(reinterpret_cast<void*>(at), n, MEM_RESERVE, PAGE_READWRITE));
    }

    bool commit(uintptr_t at, uintptr_t n) override {
        return VirtualAlloc(reinterpret_cast<void*>(at), n, MEM_COMMIT, PAGE_READWRITE) != nullptr;
    }

    uintptr_t front(uintptr_t n) override {
        void* p = VirtualAlloc(nullptr, n, MEM_RESERVE, PAGE_NOACCESS);
        if (!p) return 0;
        VirtualFree(p, 0, MEM_RELEASE);
        return reinterpret_cast<uintptr_t>(p);
    }

    uint64_t now_us() override {
        LARGE_INTEGER f{}, c{};
        QueryPerformanceFrequency(&f);
        QueryPerformanceCounter(&c);
        if (f.QuadPart <= 0) return 0;
        const auto ticks = static_cast<uint64_t>(c.QuadPart), hz = static_cast<uint64_t>(f.QuadPart);
        return ticks / hz * 1000000ull + ticks % hz * 1000000ull / hz;  // no overflow after days of uptime
    }
};

}  // namespace kc::nearmem
