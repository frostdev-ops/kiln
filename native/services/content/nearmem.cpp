// SPDX-License-Identifier: MIT
// kiln.content: the near-memory policy (nearmem.h). No operating-system call here: everything goes through Space.
#include "nearmem.h"

#include <algorithm>
#include <cstdio>

namespace kc::nearmem {

namespace {

uintptr_t dist(uintptr_t a, uintptr_t b) { return a > b ? a - b : b - a; }
uintptr_t align_down(uintptr_t v, uintptr_t a) { return v & ~(a - 1); }
uintptr_t align_up(uintptr_t v, uintptr_t a) {
    const uintptr_t r = v + (a - 1);
    return r < v ? align_down(~uintptr_t(0), a) : align_down(r, a);
}
uintptr_t end_of(const Region& r) {
    const uintptr_t e = r.base + r.size;
    return e < r.base ? ~uintptr_t(0) : e;
}

// Addresses the way the log prints a pointer (16 upper-case digits), sizes the way a person reads them.
std::string hex(uintptr_t v) {
    char b[24];
    std::snprintf(b, sizeof b, "%016llX", static_cast<unsigned long long>(v));
    return b;
}
std::string size_text(uint64_t n) {
    char b[32];
    if (n >= (1ull << 30)) std::snprintf(b, sizeof b, "%.2f GB", static_cast<double>(n) / (1ull << 30));
    else if (n >= (1ull << 20)) std::snprintf(b, sizeof b, "%.1f MB", static_cast<double>(n) / (1ull << 20));
    else std::snprintf(b, sizeof b, "%llu KB", static_cast<unsigned long long>((n + 1023) >> 10));
    return b;
}
std::string num(unsigned long long v) { return std::to_string(v); }

}  // namespace

NearMemory::NearMemory(Space& space, Config cfg, Log log) : space_(space), cfg_(std::move(cfg)), lim_(space.limits()), log_(std::move(log)) {}

void NearMemory::boot() {
    // 1. Wine: the first free of the fixed low addresses.
    for (const uintptr_t at : cfg_.low_at) {
        if (at < lim_.lowest) continue;
        if (const uintptr_t p = space_.reserve(at, cfg_.low_size)) {
            arenas_.push_back(Arena{p, cfg_.low_size, 0});
            break;
        }
    }
    if (arenas_.empty()) note(0, "near memory: no room for " + num(cfg_.low_size >> 20) + " MB at any of the fixed low addresses");
    else note(0, "near memory: " + num(cfg_.low_size >> 20) + " MB set aside at " + hex(arenas_.front().base));

    // 2. Above 4 GB, from the front of what the system hands out now. The game's data goes there in the order it
    // allocates it, so arenas spread over that range, each reserved before anything else can take the place, leave
    // every address of it with one in reach.
    const uintptr_t gran = lim_.granularity;
    const uintptr_t first = space_.front(gran);
    const uintptr_t from = std::max(align_up(first, gran), align_up(cfg_.ladder_floor, gran));
    const uintptr_t gap = first_gap(from, cfg_.ladder_gap);  // something big below (the runtime's reservations) pushes the game's data past it
    const uintptr_t start = gap ? gap : from;
    std::string where;
    size_t made = 0;
    for (uintptr_t i = 0; i * cfg_.ladder_stride <= cfg_.ladder_span; ++i) {
        const uintptr_t at = start + i * cfg_.ladder_stride;
        if (at < start || at + cfg_.ladder_size > lim_.highest) break;
        // taken already (the system's own allocation, a module): the next free spot, a little further up
        for (uintptr_t k = 0; k < 4; ++k) {
            const uintptr_t try_at = at + k * cfg_.ladder_size;
            if (try_at + cfg_.ladder_size > lim_.highest) break;
            if (const uintptr_t p = space_.reserve(try_at, cfg_.ladder_size)) {
                arenas_.push_back(Arena{p, cfg_.ladder_size, 0});
                where += (where.empty() ? "" : ", ") + hex(p);
                ++made;
                break;
            }
        }
    }
    if (!made) note(0, "near memory: no room for the arenas above 4 GB (from " + hex(start) + ")");
    else
        note(0, "near memory: " + num(made) + " arena(s) of " + num(cfg_.ladder_size >> 20) + " MB set aside above 4 GB, from " + hex(start) + " (the system's next allocation would go to " +
                    hex(first) + "): " + where);
}

// The first free run of at least `size` bytes at or above `from` (0: none found in a few thousand regions).
uintptr_t NearMemory::first_gap(uintptr_t from, uintptr_t size) {
    uintptr_t at = from;
    for (uint32_t i = 0; i < 8192 && at < lim_.highest; ++i) {
        Region r;
        if (!space_.query(at, &r) || r.size == 0) break;
        const uintptr_t end = end_of(r);
        if (r.free) {
            const uintptr_t s = align_up(std::max(r.base, from), lim_.granularity);
            if (end > s && end - s >= size) return s;
        }
        if (end <= at) break;
        at = end;
    }
    return 0;
}

uintptr_t NearMemory::take(Chunk& c, size_t n) {
    const uintptr_t p = c.base + c.used;
    c.used += n;
    return p;
}

// A new chunk from the nearest arena that has the room and whose chunk is in reach.
bool NearMemory::from_arena(uintptr_t target, size_t n, uintptr_t window, uintptr_t* at, uintptr_t* size, bool* commit_failed) {
    const uintptr_t gran = lim_.granularity;
    const uintptr_t wanted = std::max<uintptr_t>(cfg_.chunk, align_up(n, gran));
    Arena* best = nullptr;
    uintptr_t best_d = 0, best_size = 0;
    for (Arena& a : arenas_) {
        const uintptr_t room = a.size - a.used;
        if (room < n) continue;
        const uintptr_t sz = std::min(wanted, room);
        const uintptr_t from = a.base + a.used;
        const uintptr_t d = std::max(dist(from, target), dist(from + sz, target));
        if (d < window && (!best || d < best_d)) {
            best = &a;
            best_d = d;
            best_size = sz;
        }
    }
    if (!best) return false;
    const uintptr_t from = best->base + best->used;
    if (!space_.commit(from, best_size)) {
        *commit_failed = true;
        return false;
    }
    best->used += best_size;
    *at = from;
    *size = best_size;
    return true;
}

// Walks the window around `target`, the side whose walk is nearer to the target first, for the first free run that
// holds `need` bytes; reserves up to `want` of it.
// Upward the walk is a query at the end of each run, which is where the next one starts. Downward it cannot be: under
// Wine a query reports the page it was asked about as the run's start, so a step down would crawl a page at a time
// through a free run. So the part below the target is walked in blocks (cfg_.scan_block), each upward from its start, the
// block nearest the target first; the allocation a used run belongs to is skipped whole.
bool NearMemory::find(uintptr_t target, uintptr_t need, uintptr_t want, uintptr_t window, uint32_t budget, Scan* sc, uintptr_t* at_out, uintptr_t* len_out) {
    const uintptr_t gran = lim_.granularity;
    *sc = Scan{};
    sc->target = target;
    sc->need = need;
    const uintptr_t lo = target > window + lim_.lowest ? target - window : lim_.lowest;
    uintptr_t hi = target + window;
    if (hi < target || hi > lim_.highest + 1) hi = lim_.highest + 1;
    sc->lo = lo;
    sc->hi = hi;
    const uint64_t t0 = space_.now_us();
    Side& up = sc->up;
    Side& down = sc->down;
    uintptr_t up_at = target;    // the next address to query upward
    uintptr_t down_hi = target;  // what lies at or above it, down to the target, has been walked
    up.reached = down.reached = target;
    bool up_live = target < hi, down_live = target > lo;
    if (!up_live) up.stop = Side::Edge;
    if (!down_live) down.stop = Side::Edge;
    const auto done = [&](Side& s, bool& live, Side::Stop why, uintptr_t at) {
        s.stop = why;
        s.reached = at;
        live = false;
    };

    // The part of the free run [from, to) that is usable (clamped to the window and the side of the target, aligned);
    // true when it holds a block of `need`.
    const auto usable = [&](uintptr_t from, uintptr_t to, bool upward, uintptr_t* s_out, uintptr_t* e_out) {
        const uintptr_t s = upward ? align_up(std::max(from, target), gran) : align_up(std::max(from, lo), gran);
        const uintptr_t e = upward ? align_down(std::min(to, hi), gran) : align_down(std::min(to, target), gran);
        if (e <= s) return false;
        if (e - s > sc->largest) {
            sc->largest = e - s;
            sc->largest_at = s;
        }
        *s_out = s;
        *e_out = e;
        return e - s >= need;
    };
    // Reserves up to `want` of the usable part [s, e), at the end nearest the target; false when the system refuses.
    const auto reserve_in = [&](uintptr_t s, uintptr_t e, bool upward) {
        const uintptr_t len = std::min(std::max(want, need), e - s);
        const uintptr_t at = upward ? s : e - len;
        if (!space_.reserve(at, len)) {
            ++sc->refused;
            return false;
        }
        *at_out = at;
        *len_out = len;
        return true;
    };

    while (up_live || down_live) {
        const uintptr_t down_far = down_hi - lo > cfg_.scan_block ? align_down(down_hi - cfg_.scan_block, gran) : lo;
        const bool go_up = up_live && (!down_live || up_at - target <= target - down_far);
        if (go_up) {
            if (up.regions >= budget) {
                done(up, up_live, Side::Budget, up_at);
                continue;
            }
            Region r;
            if (!space_.query(up_at, &r)) {
                done(up, up_live, Side::Limit, up_at);
                continue;
            }
            ++up.regions;
            if (r.free) ++up.free_regions;
            const uintptr_t end = end_of(r);
            if (r.size == 0 || end <= up_at) {  // a system that does not move on: stop rather than spin
                done(up, up_live, Side::Stuck, up_at);
                continue;
            }
            uintptr_t s, e;
            if (r.free && usable(r.base, end, true, &s, &e) && reserve_in(s, e, true)) {
                up.stop = Side::Fit;
                sc->us = space_.now_us() - t0;
                return true;
            }
            up_at = end;
            up.reached = end;
            if (up_at >= hi) done(up, up_live, Side::Edge, end);
            continue;
        }
        // the next block below
        const uintptr_t bs = down_far;
        uintptr_t at = bs, next_hi = bs;
        bool first = true;
        std::vector<std::pair<uintptr_t, uintptr_t>> fits;  // runs of the block that hold the block, nearest the start first
        while (at < down_hi) {
            if (down.regions >= budget) {
                done(down, down_live, Side::Budget, at);
                break;
            }
            Region r;
            if (!space_.query(at, &r)) {
                done(down, down_live, Side::Limit, at);
                break;
            }
            ++down.regions;
            if (r.free) ++down.free_regions;
            const uintptr_t end = end_of(r);
            if (r.size == 0 || end <= at) {
                done(down, down_live, Side::Stuck, at);
                break;
            }
            if (first) {  // what lies below the block's start of the run or the allocation that holds it is walked already or used
                first = false;
                if (r.free) next_hi = std::max(std::min(r.base, bs), lo);
                else if (r.alloc_base && r.alloc_base < bs) next_hi = std::max(r.alloc_base, lo);
            }
            uintptr_t s, e;
            if (r.free && usable(r.base, end, false, &s, &e)) fits.emplace_back(s, e);
            at = end;
        }
        for (size_t i = fits.size(); i-- > 0;)
            if (reserve_in(fits[i].first, fits[i].second, false)) {
                down.stop = Side::Fit;
                sc->us = space_.now_us() - t0;
                return true;
            }
        if (!down_live) continue;  // stopped inside the block
        down_hi = next_hi;
        down.reached = down_hi;
        if (down_hi <= lo) done(down, down_live, Side::Edge, lo);
    }
    sc->us = space_.now_us() - t0;
    return false;
}

// Looks for an arena around the target. A scan that found nothing is not repeated for the neighbourhood (a pack with
// hundreds of definitions on one base would walk the window for each) until `refusal_us` has passed.
bool NearMemory::new_arena(uintptr_t target, uintptr_t need, uintptr_t window, bool optional) {
    // A scan walks the whole window, so one for a target within 64 MB has the same window but for its edges: its refusal
    // is the neighbourhood's.
    const uintptr_t bucket = target >> 26;
    const uint64_t now = space_.now_us();
    scan_cached_ = false;
    for (const Refusal& r : refusals_)
        if (r.bucket == bucket && r.window == window && need >= r.need && now - r.at_us < cfg_.refusal_us) {
            ++refused_total_;
            scan_ = r.scan;
            scan_cached_ = true;
            return false;
        }
    uintptr_t at = 0, len = 0;
    ++scans_;
    if (find(target, need, std::max<uintptr_t>(cfg_.arena_found, need), window, optional ? cfg_.optional_budget : cfg_.scan_budget, &scan_, &at, &len)) {
        arenas_.push_back(Arena{at, len, 0});
        note(0, "near memory: " + size_text(len) + " set aside at " + hex(at) + " for target " + hex(target) + " (walked " + num(scan_.up.regions + scan_.down.regions) +
                    " regions in " + num((scan_.us + 500) / 1000) + " ms)");
        return true;
    }
    refusals_.erase(std::remove_if(refusals_.begin(), refusals_.end(),
                                   [&](const Refusal& r) { return now - r.at_us >= cfg_.refusal_us || (r.bucket == bucket && r.window == window); }),
                    refusals_.end());
    if (refusals_.size() >= 64) refusals_.erase(refusals_.begin());
    refusals_.push_back(Refusal{bucket, need, window, now, scan_});
    return false;
}

uintptr_t NearMemory::alloc(uintptr_t target, size_t n) {
    n = (n + 15) & ~size_t(15);
    // Free address space near a resource can be scarce: under Wine the low 4 GB are nearly full. So a target uses the nearest chunk with room, or, when that is far off, a new
    // chunk nearer than it: what a far target leaves free stays for those that can reach nothing else.
    Chunk* nearest = nullptr;
    uintptr_t nearest_d = 0;
    for (Chunk& c : chunks_) {
        if (c.used + n > c.size) continue;
        const uintptr_t from = c.base + c.used;
        const uintptr_t d = std::max(dist(from, target), dist(from + n, target));
        if (d < cfg_.reach && (!nearest || d < nearest_d)) {
            nearest = &c;
            nearest_d = d;
        }
    }
    if (nearest && nearest_d < cfg_.near_reach) return take(*nearest, n);
    const uintptr_t window = nearest ? cfg_.near_reach : cfg_.reach;
    const uintptr_t need = align_up(n, lim_.granularity);
    uintptr_t base = 0, size = 0;
    bool commit_failed = false;
    bool found = from_arena(target, n, window, &base, &size, &commit_failed);
    bool scanned = false;
    if (!found && !commit_failed) {
        scanned = true;
        if (new_arena(target, need, window, nearest != nullptr)) found = from_arena(target, n, window, &base, &size, &commit_failed);
    }
    if (found) {
        chunks_.push_back(Chunk{base, n, size});
        note(0, "near memory: chunk " + num(chunks_.size()) + " at " + hex(base) + ", " + num(size / 1024) + " KB, for target " + hex(target));
        return base;
    }
    if (nearest) return take(*nearest, n);
    // Nothing: say what was seen, once for a neighbourhood (the caller's own line names each definition that goes without).
    if (!scanned) note(1, failure_text(target, n, window, nullptr, commit_failed));
    else if (!scan_cached_) note(1, failure_text(target, n, window, &scan_, commit_failed));
    return 0;
}

void NearMemory::add(uintptr_t base, size_t size) {
    const uintptr_t at = (base + 15) & ~uintptr_t(15);
    if (at - base >= size) return;
    chunks_.push_back(Chunk{at, 0, size - (at - base)});
}

std::string NearMemory::report() const {
    uintptr_t reserved = 0, used = 0, arena_size = 0, arena_used = 0;
    for (const Chunk& c : chunks_) reserved += c.size, used += c.used;
    for (const Arena& a : arenas_) arena_size += a.size, arena_used += a.used;
    std::string s = num(chunks_.size()) + " chunks, " + num(used / 1024) + " of " + num(reserved / 1024) + " KB used, " + num(arena_used >> 20) + " of " + num(arena_size >> 20) +
                    " MB set aside taken (" + num(arenas_.size()) + " arenas)";
    if (scans_ || refused_total_) s += ", " + num(scans_) + " scans, " + num(refused_total_) + " refused without one";
    return s;
}

// The failure line. What a person needs to tell "no room" from "did not look far enough": the target, the window, how
// many regions each side saw and why each side stopped, the largest free run and why it did not fit, and the arenas.
std::string NearMemory::failure_text(uintptr_t target, size_t n, uintptr_t window, const Scan* sp, bool commit_failed) const {
    std::string t = "near memory: nothing free within 2 GB of " + hex(target) + " for " + num(n) + " bytes (" + num(chunks_.size()) + " chunks)";
    if (sp) {
        const Scan& s = *sp;
        t += ": window " + hex(s.lo) + " to " + hex(s.hi);
        if (window < cfg_.reach) t += " (" + size_text(window) + " each way, a chunk further off exists)";
        const auto side = [&](const char* name, const Side& d) {
            std::string x = num(d.regions) + " regions " + name;
            switch (d.stop) {
                case Side::Edge: x += " to the window's edge"; break;
                case Side::Limit: x += " to the top of the address space"; break;
                case Side::Budget: x += ", stopped by the limit of " + num(d.regions) + " regions at " + hex(d.reached); break;
                case Side::Stuck: x += ", stopped: the system did not move on at " + hex(d.reached); break;
                default: break;
            }
            return x;
        };
        t += "; walked " + side("above", s.up) + " and " + side("below", s.down) + " in " + num((s.us + 500) / 1000) + " ms";
        t += "; free runs seen: " + num(s.up.free_regions + s.down.free_regions);
        if (s.largest == 0) t += ", none inside the window that can hold a block";
        else t += ", the largest usable one " + size_text(s.largest) + " at " + hex(s.largest_at) + " (" + size_text(dist(s.largest_at, target)) + (s.largest_at > target ? " above" : " below") + ")";
        const bool cut = s.up.stop == Side::Budget || s.down.stop == Side::Budget || s.up.stop == Side::Stuck || s.down.stop == Side::Stuck;
        if (cut) t += "; the window was not seen in full, so room may exist beyond what was walked";
        else if (s.largest == 0) t += "; the whole window is reserved or committed";
        else t += "; too small for the " + size_text(s.need) + " this request needs";
        if (s.refused) t += "; the system refused " + num(s.refused) + " reservation(s) in runs that fit";
    }
    if (commit_failed) t += "; an arena had the room but the system refused to commit it";
    if (arenas_.empty()) {
        t += "; no arena was set aside at boot";
    } else {
        const Arena* nearest = &arenas_.front();
        uintptr_t best = ~uintptr_t(0);
        for (const Arena& a : arenas_) {
            const uintptr_t d = std::min(dist(a.base, target), dist(a.base + a.size, target));
            if (d < best) best = d, nearest = &a;
        }
        const uintptr_t far = std::max(dist(nearest->base, target), dist(nearest->base + nearest->size, target));
        t += "; " + num(arenas_.size()) + " arena(s), the nearest at " + hex(nearest->base) + " is " + size_text(best) + (nearest->base > target ? " above" : " below") + " the target: " +
             (far >= window ? "out of reach" : nearest->size - nearest->used < n ? "full" : "in reach");
    }
    return t;
}

}  // namespace kc::nearmem
