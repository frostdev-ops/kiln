// SPDX-License-Identifier: MIT
#pragma once
#include "stations_logic.h"
#include <mutex>

namespace kitems {
// Immutable registrations. No managed callback runs in a game program's payment hook.
struct CraftSource {
    uint64_t owner = 0, handle = 0;
    uint32_t player = 0, container = 0;
    bool after_outputs = false, deny = true;
    std::vector<uint32_t> items;
    uint32_t player_session = 0;
    bool locks = false;  // GitHub #147: also counts and pays an InteractionLock's required item (KILN_CRAFT_SOURCE_LOCKS)
    bool fills = false;     // GitHub #132: production-station fills named KILN_STATION_FILL_LINKED (KILN_CRAFT_SOURCE_FILLS)
    bool building = false;  // GitHub #132: the player's block placements (KILN_CRAFT_SOURCE_BUILDING)
    bool respec = false;    // GitHub #150: the skill tree's paid reset and a node's unlearn (KILN_CRAFT_SOURCE_RESPEC)
    bool allows(uint32_t item) const {
        const bool found = std::binary_search(items.begin(), items.end(), item);
        return item && (deny ? !found : found);
    }
};
inline bool source_identity_ok(uint32_t player, uint32_t container, bool character, bool inventory) {
    // Characters (including somebody else's bag) are not linked storage containers.
    return player && player <= 1023 && container > 1023 && !character && inventory;
}
class CraftSourceRegistry {
    mutable std::mutex mutex_;
    uint64_t next_ = 0;
    std::vector<CraftSource> sources_;
public:
    // flags: KILN_CRAFT_SOURCE_* (a bool true is KILN_CRAFT_SOURCE_LOCKS, as the #147 callers pass it)
    uint64_t add(uint64_t owner, uint32_t player, uint32_t container, bool after, bool deny, std::vector<uint32_t> items, uint32_t session = 0,
                 uint32_t flags = 0) {
        std::lock_guard lock(mutex_);
        // This player's registrations from an earlier login are dead (every snapshot drops them): free their slots, so
        // a mod that re-registers on each join without disposing cannot exhaust the shared 4096 limit for everyone.
        sources_.erase(std::remove_if(sources_.begin(),sources_.end(),[&](const CraftSource& s) { return s.player == player && s.player_session != session; }),sources_.end());
        if (sources_.size() >= 4096) return 0;
        std::sort(items.begin(), items.end());
        items.erase(std::unique(items.begin(), items.end()), items.end());
        const auto id = ++next_;
        sources_.push_back({owner,id,player,container,after,deny,std::move(items),session,(flags & 1u) != 0,(flags & 2u) != 0,
                            (flags & 4u) != 0,(flags & 8u) != 0});
        return id;
    }
    bool remove(uint64_t owner, uint64_t handle) {
        std::lock_guard lock(mutex_);
        const auto it = std::find_if(sources_.begin(),sources_.end(),[&](const CraftSource& s) { return s.owner == owner && s.handle == handle; });
        if (it == sources_.end()) return false;
        sources_.erase(it); return true;
    }
    void cancel_owner(uint64_t owner) {
        std::lock_guard lock(mutex_);
        sources_.erase(std::remove_if(sources_.begin(),sources_.end(),[&](const CraftSource& s) { return s.owner == owner; }),sources_.end());
    }
    bool empty() const { std::lock_guard lock(mutex_); return sources_.empty(); }
    void clear() { std::lock_guard lock(mutex_); sources_.clear(); }
    // The players with any registration, sorted and unique (the lock checks' cache, GitHub #147).
    std::vector<uint32_t> players() const {
        std::lock_guard lock(mutex_);
        std::vector<uint32_t> out;
        for (const auto& s : sources_) out.push_back(s.player);
        std::sort(out.begin(), out.end());
        out.erase(std::unique(out.begin(), out.end()), out.end());
        return out;
    }
    bool any_locks() const {
        std::lock_guard lock(mutex_);
        return std::any_of(sources_.begin(), sources_.end(), [](const CraftSource& s) { return s.locks; });
    }
    bool any_building() const {
        std::lock_guard lock(mutex_);
        return std::any_of(sources_.begin(), sources_.end(), [](const CraftSource& s) { return s.building; });
    }
    bool any_respec() const {  // GitHub #150
        std::lock_guard lock(mutex_);
        return std::any_of(sources_.begin(), sources_.end(), [](const CraftSource& s) { return s.respec; });
    }
    std::vector<CraftSource> copy(uint32_t player) const {
        std::lock_guard lock(mutex_);
        std::vector<CraftSource> out;
        for (const auto& s : sources_) if (s.player == player) out.push_back(s);
        return out;
    }
};

// Per-item order: normal bag/base sources, then the registrations around station outputs. A container is counted once
// per item, even if several registrations or an opted-in station name it. Registration order breaks ties.
template<class Access, class Count>
void merge_craft_sources(const std::vector<CraftSource>& sources, uint32_t player, uint32_t exclude,
                         const std::vector<uint32_t>& order, const std::vector<uint32_t>& items,
                         Access access, Count count, stations::CraftLent* lent) {
    for (uint32_t item : items) {
        std::unordered_set<uint32_t> seen(order.begin(),order.end());
        seen.insert(exclude);
        std::vector<uint32_t> out;
        uint64_t total = 0;
        auto push = [&](uint32_t e) { if (seen.insert(e).second) { const uint64_t n = count(e,item); if(n) {out.push_back(e); total += n;} } };
        auto registered = [&](bool after) {
            for (const auto& s : sources)
                if (s.player == player && s.after_outputs == after && s.allows(item) && !seen.count(s.container) && access(player,s.container))
                    push(s.container);
        };
        registered(false);
        for (uint32_t e : lent->by[item]) push(e);
        registered(true);
        lent->by[item] = std::move(out);
        lent->count[item] = total;
    }
}

// ---- GitHub #132 (0.2.0-dev.31.12): production-station fills and block placements from the player's linked sources ----
// Which links a fill (opt = &CraftSource::fills) or a placement (&CraftSource::building) may take each item from: this
// player's, opted in, the filter allowing the item, every container once per item across both groups, never one in
// `skip` (the station, the player, the base's Magic chests, named chests, lending stations: counted already or never a
// source). BeforeStationOutputs links come in `before`, AfterStationOutputs in `after`, each in registration order.
// `access` (the payment's permission and container test) runs once per container; containers holding none are left out.
struct LinkUse {
    std::unordered_map<uint32_t, std::vector<uint32_t>> before, after;
    std::unordered_map<uint32_t, uint64_t> count_before, count_after;
    uint64_t before_of(uint32_t item) const { const auto it = count_before.find(item); return it == count_before.end() ? 0 : it->second; }
    uint64_t after_of(uint32_t item) const { const auto it = count_after.find(item); return it == count_after.end() ? 0 : it->second; }
    bool empty() const { return count_before.empty() && count_after.empty(); }
};
template<class Access, class Count>
LinkUse links_for(const std::vector<CraftSource>& sources, bool CraftSource::*opt, uint32_t player, const std::vector<uint32_t>& items,
                  const std::unordered_set<uint32_t>& skip, Access access, Count count) {
    LinkUse u;
    std::unordered_map<uint32_t, bool> ok;  // access, once per container
    auto allowed = [&](uint32_t c) {
        const auto it = ok.find(c);
        if (it != ok.end()) return it->second;
        return ok[c] = access(player, c);
    };
    for (uint32_t item : items) {
        if (!item) continue;
        std::unordered_set<uint32_t> seen(skip.begin(), skip.end());
        seen.insert(player);
        for (bool after : {false, true})
            for (const auto& s : sources) {
                if (s.player != player || !(s.*opt) || s.after_outputs != after || !s.allows(item) || seen.count(s.container)) continue;
                seen.insert(s.container);
                if (!allowed(s.container)) continue;
                if (const uint64_t n = count(s.container, item)) {
                    (after ? u.after : u.before)[item].push_back(s.container);
                    (after ? u.count_after : u.count_before)[item] += n;
                }
            }
    }
    return u;
}

// ---- GitHub #147: an InteractionLock's required item from the player's linked sources ----
// Which links a lock may use for `item`: this player's, opted in with Locks, the filter allowing the item, in crafting's
// order (BeforeStationOutputs links first, then AfterStationOutputs, each in registration order), every container once,
// never the player's own bag (`exclude`). `access` is the payment's permission and container test, `count` what the
// container holds of the item in the reader's view. Containers holding none are left out.
struct LockHeld {
    uint32_t container = 0;
    uint64_t count = 0;
};
template<class Access, class Count>
std::vector<LockHeld> lock_sources_for(const std::vector<CraftSource>& sources, uint32_t player, uint32_t item, uint32_t exclude,
                                       Access access, Count count) {
    std::vector<LockHeld> out;
    if (!item) return out;
    std::unordered_set<uint32_t> seen{exclude};
    for (bool after : {false, true})
        for (const auto& s : sources)
            if (s.player == player && s.locks && s.after_outputs == after && s.allows(item) && !seen.count(s.container) &&
                access(player, s.container)) {
                seen.insert(s.container);
                if (const uint64_t n = count(s.container, item)) out.push_back({s.container, n});
            }
    return out;
}
// Why the links cannot pay a lock (lock_why): kLockNone they can; kLockNoLinks the player has none (the game's own lock,
// no line); kLockNotOptedIn links, none with Locks; kLockFiltered every opted-in link's filter refuses the item;
// kLockShort the opted-in links hold too few.
enum : uint32_t { kLockNone = 0, kLockNoLinks = 1, kLockNotOptedIn = 2, kLockFiltered = 3, kLockShort = 4 };
inline uint32_t lock_why(const std::vector<CraftSource>& sources, uint32_t player, uint32_t item, uint64_t held, uint64_t need) {
    bool any = false, opted = false, allowed = false;
    for (const auto& s : sources) {
        if (s.player != player) continue;
        any = true;
        if (!s.locks) continue;
        opted = true;
        allowed = allowed || s.allows(item);
    }
    if (!any) return kLockNoLinks;
    if (!opted) return kLockNotOptedIn;
    if (!allowed) return kLockFiltered;
    return held >= need ? kLockNone : kLockShort;
}
inline const char* lock_why_text(uint32_t why) {
    switch (why) {
    case kLockNone: return "covered";
    case kLockNoLinks: return "no linked source";
    case kLockNotOptedIn: return "source not opted in (CraftSourceOptions.Locks is off on every link)";
    case kLockFiltered: return "every opted-in link's item filter refuses the item";
    default: return "short count";
    }
}
// The takes for `need` from `held` (in that order), all or nothing: false (no takes) when they hold less in total.
inline bool plan_lock_take(const std::vector<LockHeld>& held, uint32_t need, std::vector<LockHeld>* takes) {
    takes->clear();
    uint64_t total = 0;
    for (const auto& h : held) total += h.count;
    if (total < need) return false;
    uint64_t left = need;
    for (const auto& h : held) {
        if (!left) break;
        const uint64_t n = std::min<uint64_t>(left, h.count);
        takes->push_back({h.container, n});
        left -= n;
    }
    return true;
}
// What a lock check counts: the game's own count of the bag, plus the links' when the bag alone is short; capped at u32.
// The game compares it with requiredItemAmount itself.
inline uint32_t lock_counted(uint32_t bag, uint64_t linked, uint32_t need) {
    if (bag >= need) return bag;
    return static_cast<uint32_t>(std::min<uint64_t>(static_cast<uint64_t>(bag) + linked, 0xffffffffu));
}
} // namespace kitems
