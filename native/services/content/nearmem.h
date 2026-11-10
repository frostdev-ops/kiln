// SPDX-License-Identifier: MIT
// kiln.content: where Kiln's copies of game data go. Standard C++ only, so the policy runs on the host in a unit test
// that replays address-space maps (tests/near_test.cpp); nearwin.h is the Windows side (VirtualQuery / VirtualAlloc).
//
// Why memory must be "near": a BlobArray / BlobString in game data is {i32 self-relative offset, u32 count}, and a
// config element holds an i32 too. A clone of an item, a recipe or a skill node keeps the offsets of what it was copied
// from (rebased), so it has to lie within 2 GB of that data; the list a rule adds has to lie within 2 GB of the field
// that points at it. Every caller names its target (an address in game data) and gets memory within `reach` of it.
//
// Where the memory comes from, in order:
//   1. a chunk (256 KB, or more) already in use that has room and is in reach;
//   2. a new chunk from an arena: address space reserved earlier and committed a chunk at a time. Arenas are set aside
//      at boot (boot()), before the game and the runtime fill the address space near their resources: one at a fixed
//      low address (under Wine the game's data lands in the low 4 GB) and a ladder of them above 4 GB, spaced so that
//      every address up to the ladder's end has one within reach (on Windows the data lands above 4 GB, far from the low
//      arena);
//   3. a new arena found around the target: a walk of the whole window, both directions, nearest first.
// When all three fail the log line says what the scan saw (see Failure text in nearmem.cpp).
#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace kc::nearmem {

// The run of pages around an address that share one state, as VirtualQuery reports it. `base` is the start of the run
// on Windows, but under Wine it is the page of the queried address (the run's end is right in both), so the policy
// never relies on it for a run it reached from below. `alloc_base`: the start of the allocation a used run belongs to
// (right in both), 0 for a free run.
struct Region {
    uintptr_t base = 0;
    uintptr_t size = 0;
    bool free = false;
    uintptr_t alloc_base = 0;
};

struct Limits {
    uintptr_t granularity = 0x10000;  // allocation granularity: where a reservation can start
    uintptr_t lowest = 0x10000;       // lowest address an application can reserve
    uintptr_t highest = 0x7FFFFFFEFFFF;  // highest address of the application range (inclusive)
};

// The seam: every call into the operating system. nearwin.h implements it with VirtualQuery / VirtualAlloc; the unit
// test with a map of the address space.
class Space {
public:
    virtual ~Space() = default;
    virtual Limits limits() = 0;
    // The region that holds `addr`; false when `addr` is beyond the top of the address space (or the call failed).
    virtual bool query(uintptr_t addr, Region* out) = 0;
    // Reserves exactly [at, at + n): the address `at`, or 0 when the system refuses (taken, or no room).
    virtual uintptr_t reserve(uintptr_t at, uintptr_t n) = 0;
    // Commits read-write pages inside a reservation of ours.
    virtual bool commit(uintptr_t at, uintptr_t n) = 0;
    // Where the system would put a default allocation of n bytes right now (the front of the heap the game is about to
    // fill); 0 when unknown.
    virtual uintptr_t front(uintptr_t n) = 0;
    virtual uint64_t now_us() = 0;
};

struct Config {
    uintptr_t reach = 0x7F000000;       // callers store a signed 32-bit offset: 2 GB less 16 MB for how far their field is from the target
    uintptr_t near_reach = 0x40000000;  // a chunk further off than this is used only when there is no room for a new one
    uintptr_t chunk = 0x40000;          // what a chunk is made of when the arena has it
    uintptr_t arena_found = 0x200000;   // an arena made around a target: 2 MB when the free run has it, else the run (holes are scarce under Wine)
    // Under Wine the game's data lands in the low 4 GB, and from the middle of them 2 GB reaches nearly all of it.
    uintptr_t low_size = 0x8000000;
    std::vector<uintptr_t> low_at = {0x78000000, 0x70000000, 0x60000000, 0x50000000, 0x40000000};
    // Above 4 GB, from the first free run of ladder_gap bytes at or above where the system hands out memory (never below
    // ladder_floor): arenas ladder_stride apart, up to ladder_span from the first. A stride under 2 x reach leaves no address in between out of reach, and a gap of
    // stride - size still holds the game's biggest blocks (a 2 GB one under Wine).
    uintptr_t ladder_size = 0x8000000;
    uintptr_t ladder_floor = 0x100000000;
    uintptr_t ladder_gap = 0x40000000;   // the ladder starts in the first free run of this size at or above the front: where the game's big blocks go next
    uintptr_t ladder_stride = 0xE0000000;
    uintptr_t ladder_span = 0x600000000;
    uint32_t scan_budget = 1u << 18;     // region queries per direction when a scan is the only way to get memory
    uintptr_t scan_block = 0x4000000;    // the part of a window below the target is walked upward in blocks of this size (see find)
    uint32_t optional_budget = 4096;     // ... when it only looks for a chunk nearer than one that already serves (the usual case needs no scan)
    uint64_t refusal_us = 5'000'000;     // how long a scan that found nothing is not repeated for a target next to it
};

class NearMemory {
public:
    using Log = std::function<void(int level, const std::string&)>;  // level 0 = info, 1 = warning
    NearMemory(Space& space, Config cfg, Log log);

    // At boot, before the game fills the address space near its resources.
    void boot();
    // `n` bytes (rounded up to 16) within `reach` of `target`, or 0. Never freed.
    uintptr_t alloc(uintptr_t target, size_t n);
    // Memory of Kiln's own that alloc may hand out (a clone's spare room).
    void add(uintptr_t base, size_t size);
    std::string report() const;

    struct Arena {
        uintptr_t base = 0, size = 0, used = 0;
    };
    struct Chunk {
        uintptr_t base = 0, used = 0, size = 0;
    };
    const std::vector<Arena>& arenas() const { return arenas_; }
    const std::vector<Chunk>& chunks() const { return chunks_; }

    // What a walk around a target saw.
    struct Side {
        enum Stop { Running, Edge, Limit, Budget, Fit, Stuck };
        uint32_t regions = 0, free_regions = 0;
        Stop stop = Running;
        uintptr_t reached = 0;  // the last address the walk got to
    };
    struct Scan {
        uintptr_t target = 0, lo = 0, hi = 0, need = 0;
        Side up, down;
        uintptr_t largest = 0, largest_at = 0;  // the largest free run that lies in the window (usable part), and where
        uint32_t refused = 0;                   // runs that fit and whose reservation the system refused
        uint64_t us = 0;
    };
    // The last scan, for tests and the failure text.
    const Scan& last_scan() const { return scan_; }
    uint64_t refusals() const { return refused_total_; }

private:
    struct Refusal {
        uintptr_t bucket = 0, need = 0, window = 0;
        uint64_t at_us = 0;
        Scan scan;
    };
    uintptr_t take(Chunk& c, size_t n);
    bool from_arena(uintptr_t target, size_t n, uintptr_t window, uintptr_t* at, uintptr_t* size, bool* commit_failed);
    bool new_arena(uintptr_t target, uintptr_t need, uintptr_t window, bool optional);
    uintptr_t first_gap(uintptr_t from, uintptr_t size);
    bool find(uintptr_t target, uintptr_t need, uintptr_t want, uintptr_t window, uint32_t budget, Scan* scan, uintptr_t* at, uintptr_t* len);
    std::string failure_text(uintptr_t target, size_t n, uintptr_t window, const Scan* scan, bool commit_failed) const;
    void note(int level, const std::string& text) const {
        if (log_) log_(level, text);
    }

    Space& space_;
    Config cfg_;
    Limits lim_;
    Log log_;
    std::vector<Arena> arenas_;
    std::vector<Chunk> chunks_;
    std::vector<Refusal> refusals_;
    Scan scan_;
    bool scan_cached_ = false;  // scan_ is an earlier scan of the neighbourhood, not a new one
    uint64_t refused_total_ = 0, scans_ = 0;
};

}  // namespace kc::nearmem
