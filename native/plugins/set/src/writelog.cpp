// SPDX-License-Identifier: MIT
// After Shrouded Engine Tweaks 0.2.0 runtime/writelog.cpp, Copyright (c) 2026 Cacaida, MIT
// (licenses/ShroudedEngineTweaks-LICENSE.txt). See writelog.h for what Kiln changed.
#include "writelog.h"

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <map>
#include <vector>

#include "game.h"
#include "level.h"

namespace {

struct Rec {
    std::vector<uint8_t> orig, latest;  // same size: the record's bytes
    std::vector<std::string> writers;
};
using Key = std::pair<const res::Entry*, uint32_t>;  // resource, first byte; records of one resource never overlap
std::map<Key, Rec> gRecs;
size_t gBytes;
size_t gBudget = 16 << 20;  // far beyond what a mod changes: every item's every field is a few MB
std::atomic<bool> gWaiting{false};  // records with no writers left wait for settle() on the tick thread

size_t cost(size_t n) { return 2 * n + 64; }
uint64_t stop(const std::pair<const Key, Rec>& r) { return uint64_t(r.first.second) + r.second.orig.size(); }

// The record's bytes as the game has them now, or null: the resource is gone, smaller, or this isn't the tick thread.
uint8_t* now(const Key& k, const Rec& r) {
    uint32_t size = 0;
    uint8_t* p = res::data(*k.first, &size);
    if (!p || k.second > size || r.orig.size() > size - k.second) return nullptr;
    return p + k.second;
}

// For log lines: "ItemInfo Test_Wood_Log at +0x10".
std::string what(const Key& k) {
    char off[32];
    snprintf(off, sizeof off, " at +0x%x", unsigned(k.second));
    const refl::Type t = refl::byHash(k.first->type);
    const std::string_view l = res::label(*k.first);
    return (t ? std::string(t.qname()) : std::string("a resource")) + " " +
           (l.empty() ? res::guidStr(k.first->guid) : std::string(l)) + off;
}

// The last writer left: the original goes back if the value is still the latest logged one. 1 if it was restored.
int finish(const Key& k, const Rec& r, wlog::Sink sink) {
    std::string why;
    uint8_t* p = now(k, r);
    uint32_t prot = 0;
    if (!p) why = " can't be reached any more: Not restored";
    else if (!memcmp(p, r.orig.data(), r.orig.size())) return 0;
    else if (memcmp(p, r.latest.data(), r.latest.size())) why = " was changed outside the mods (console or the game): Left as it is";
    else if (!res::write(p, r.orig.data(), r.orig.size(), prot)) why = " couldn't be restored";
    else return 1;
    sink((LV_WARN "Write Log: " + what(k) + why).c_str());
    return 0;
}

void summary(const std::string& who, int n, wlog::Sink sink) {
    if (n) sink((LV_OK "Write Log: " + who + std::to_string(n) + " change(s) undone").c_str());
}

// n records were left by their last writer off the tick thread: they stay, and settle() restores them there.
void later(const std::string& who, size_t n, wlog::Sink sink) {
    if (!n) return;
    gWaiting = true;
    sink((LV_OK "Write Log: " + who + std::to_string(n) + " change(s) go back at the next tick of a world you host").c_str());
}

}  // namespace

bool wlog::reserve(const res::Entry* e, const uint8_t* data, uint32_t off, uint32_t n, const std::string& mod) {
    if (!e || !data || !n) return false;
    const uint64_t lo = off, hi = uint64_t(off) + n;
    // [first, last): this resource's records overlapping [lo, hi)
    auto last = gRecs.lower_bound(Key{e, uint32_t(std::min<uint64_t>(hi, UINT32_MAX))}), first = last;
    while (first != gRecs.begin() && std::prev(first)->first.first == e && stop(*std::prev(first)) > lo) --first;
    if (first != last && std::next(first) == last && first->first.second <= lo && hi <= stop(*first)) {
        auto& w = first->second.writers;  // inside one record: nothing new to save
        if (std::find(w.begin(), w.end(), mod) == w.end()) w.push_back(mod);
        return true;
    }
    const uint64_t a = first == last ? lo : std::min<uint64_t>(lo, first->first.second);
    const uint64_t b = first == last ? hi : std::max<uint64_t>(hi, stop(*std::prev(last)));
    size_t old = 0;
    for (auto it = first; it != last; ++it) old += cost(it->second.orig.size());
    if (gBytes - old + cost(size_t(b - a)) > gBudget) return false;
    Rec m{std::vector<uint8_t>(size_t(b - a)), {}, {mod}};
    // The game's original wherever no record had it yet. Only [lo, hi) is new; the rest comes from the old records.
    memcpy(m.orig.data() + (lo - a), data, n);
    m.latest = m.orig;
    for (auto it = first; it != last; ++it) {
        const Rec& r = it->second;
        std::copy(r.orig.begin(), r.orig.end(), m.orig.begin() + (it->first.second - a));
        std::copy(r.latest.begin(), r.latest.end(), m.latest.begin() + (it->first.second - a));
        for (const std::string& w : r.writers)
            if (std::find(m.writers.begin(), m.writers.end(), w) == m.writers.end()) m.writers.push_back(w);
    }
    gRecs.erase(first, last);
    gBytes += cost(size_t(b - a)) - old;
    gRecs.emplace(Key{e, uint32_t(a)}, std::move(m));
    return true;
}

void wlog::wrote(const res::Entry* e, uint32_t off, const void* src, uint32_t n) {
    auto it = gRecs.upper_bound(Key{e, off});
    if (it == gRecs.begin()) return;
    --it;
    if (it->first.first != e || stop(*it) < uint64_t(off) + n) return;  // reserve wasn't called: not logged
    memcpy(it->second.latest.data() + (off - it->first.second), src, n);
}

void wlog::leave(const std::string& mod, Sink sink) {
    int n = 0;
    size_t waits = 0;
    const bool reachable = kset::tickThread();
    for (auto it = gRecs.begin(); it != gRecs.end();) {
        auto& w = it->second.writers;
        auto mine = std::find(w.begin(), w.end(), mod);
        if (mine == w.end()) { ++it; continue; }
        w.erase(mine);
        if (!w.empty()) {  // the value stays as the others left it; it goes back when the last of them leaves
            std::string others;
            for (const std::string& o : w) others += (others.empty() ? "" : ", ") + o;
            sink((LV_WARN "Write Log: " + what(it->first) + " stays changed: " + others + " changed it too").c_str());
            ++it;
            continue;
        }
        if (!reachable) {  // kept with no writers: settle() restores it on the tick thread
            ++waits;
            ++it;
            continue;
        }
        n += finish(it->first, it->second, sink);
        gBytes -= cost(it->second.orig.size());
        it = gRecs.erase(it);
    }
    summary(mod + ": ", n, sink);
    later(mod + ": ", waits, sink);
}

void wlog::leaveAll(Sink sink) {
    if (!kset::tickThread()) {
        for (auto& [k, r] : gRecs) {
            (void)k;
            r.writers.clear();
        }
        later("", gRecs.size(), sink);
        return;
    }
    int n = 0;
    for (const auto& [k, r] : gRecs) n += finish(k, r, sink);
    forget();
    summary("", n, sink);
}

void wlog::settle(Sink sink) {
    if (!gWaiting.load() || !kset::tickThread()) return;
    gWaiting = false;
    int n = 0;
    for (auto it = gRecs.begin(); it != gRecs.end();) {
        if (!it->second.writers.empty()) {  // a mod wrote it again first: it goes back when that mod stops
            ++it;
            continue;
        }
        n += finish(it->first, it->second, sink);
        gBytes -= cost(it->second.orig.size());
        it = gRecs.erase(it);
    }
    summary("", n, sink);
}

bool wlog::waiting() { return gWaiting.load(std::memory_order_acquire); }

void wlog::forget() {
    gRecs.clear();
    gBytes = 0;
    gWaiting = false;
}

size_t wlog::records() { return gRecs.size(); }
void wlog::setBudget(size_t bytes) { gBudget = bytes; }
