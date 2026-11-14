// SPDX-License-Identifier: MIT
// kiln.ui.values: change publication (chapter 05a §5.4). subscribe_changes observers get what the game's UI showed for
// each presentation identity, as Added / Updated / Removed / Evicted records, and one Reset when the cache is cleared.
//
// While at least one change subscriber exists, every value chain run (detail, slot, held, HUD) stages its final value
// under its identity; repeated calls of one identity in a frame keep the last one. When kiln.ui says a game UI frame
// completed, the staged values are compared with the cache: a new identity is Added (no before), a different value
// Updated, an equal one nothing. Absence for two completed frames is Removed (no after); held and HUD values come from
// the player-UI update, which need not run every frame, so theirs count two updates without them instead. At most 4096
// identities are cached: a new one beyond that evicts the least recently seen (then the smallest key), Evicted. A new
// client session, a capability lost or the last value provider gone clears the cache with one Reset instead of
// thousands of Removed. Records are delivered on the UI thread after the frame; they are borrowed for the call, their
// label bytes are the text store's (process-stable). These are publications of what was shown, never engine writes.
//
// Identities (chapter §5.4): HUD kinds = kind, session, local player; held = those + the held entity and item; a
// physical detail or slot = kind, session, local player, the screen key (UI state, HUD screen, crafting, station),
// page, index, entity, item; a synthetic detail = kind, session, screen key, item, synthetic_group 1, the rest 0.
#include <cstring>
#include <map>
#include <mutex>
#include <tuple>
#include <vector>

#include "ui_values_internal.h"

namespace kiln::uivalues {
namespace {

constexpr size_t kCacheMax = 4096;

struct Key {
    uint32_t kind, local, ui_state, hud, crafting, station, page, index, entity, item, synthetic;
    uint64_t session;
    auto tie() const {
        return std::tie(kind, session, local, ui_state, hud, crafting, station, page, index, entity, item, synthetic);
    }
    bool operator<(const Key& o) const { return tie() < o.tie(); }
};

struct Cached {
    KilnUiBarValue value;
    uint64_t seen = 0;   // absence: the frame (detail, slot) or the update (held, HUD) it was last staged in
    uint64_t frame = 0;  // recency for eviction: the frame it was last staged in (every kind)
};

struct State {
    std::mutex mu;
    std::map<Key, KilnUiBarValue> staged;
    std::map<Key, Cached> cache;
    uint64_t session = 0;  // the client session the cache belongs to
    uint64_t updates = 0;  // player-UI updates that captured the local player
    bool frames_hooked = false;
    uint32_t pending_reset = 0;  // 1 + KilnUiResetReason when a reset waits for the next frame
    uint64_t reset_old = 0;
};
State& st() {
    static State* s = new State;  // never destroyed: the UI thread may publish while the process ends
    return *s;
}
std::atomic<bool> g_wanted{false};

bool pair_kind(uint32_t kind) { return kind >= KUI_HELD && kind < kValueKinds; }

Key key_of(uint32_t kind, const KilnUiItemView& v) {
    Key k{};
    k.kind = kind;
    k.session = v.session;
    if (kind >= KUI_HEALTH) {
        k.local = v.local_player;
        return k;
    }
    if (kind == KUI_HELD) {
        k.local = v.local_player;
        k.entity = v.client_entity;
        k.item = v.item;
        return k;
    }
    k.ui_state = v.screen.ui_state;
    k.hud = v.screen.hud_screen;
    k.crafting = v.screen.crafting;
    k.station = v.screen.station;
    k.item = v.item;
    if (v.flags & KUI_SYNTHETIC) {
        k.synthetic = 1;
        return k;
    }
    k.local = v.local_player;
    k.page = v.inventory_page;
    k.index = v.slot_index;
    k.entity = v.client_entity;
    return k;
}

KilnUiValueIdentity identity_of(const Key& k) {
    KilnUiValueIdentity id{};
    id.size = sizeof id;
    id.kind = k.kind;
    id.local_player = k.local;
    id.ui_state = k.ui_state;
    id.hud_screen = k.hud;
    id.crafting = k.crafting;
    id.station = k.station;
    id.inventory_page = k.page;
    id.slot_index = k.index;
    id.client_entity = k.entity;
    id.item = k.item;
    id.synthetic_group = k.synthetic;
    id.session = k.session;
    return id;
}

bool same(const KilnUiBarValue& a, const KilnUiBarValue& b) {
    return std::memcmp(&a.fraction, &b.fraction, sizeof a.fraction) == 0 && a.current == b.current &&
           a.maximum == b.maximum && a.label_is_override == b.label_is_override &&
           (!a.label_is_override || a.label_id == b.label_id);
}

KilnStr label_of(const KilnUiBarValue& v) {
    std::string_view s;
    if (!v.label_is_override || !text::bytes(v.label_id, &s)) return KilnStr{nullptr, 0};
    return KilnStr{s.data(), s.size()};
}

KilnUiValueChange change(uint32_t kind, const Key& k, const KilnUiBarValue* before, const KilnUiBarValue* after,
                         uint64_t frame) {
    KilnUiValueChange c{};
    c.size = sizeof c;
    c.change_kind = kind;
    c.frame = frame;
    c.identity = identity_of(k);
    if (before) {
        c.has_before = 1;
        c.before = *before;
        c.before_label = label_of(*before);
    }
    if (after) {
        c.has_after = 1;
        c.after = *after;
        c.after_label = label_of(*after);
    }
    c.old_session = c.new_session = k.session;
    return c;
}

void deliver(const std::vector<KilnUiValueChange>& out) {
    if (out.empty()) return;
    const auto subs = providers(kListChanges);
    for (const auto& sp : *subs) {
        Provider& p = *sp;
        for (const KilnUiValueChange& c : out) {
            if (!p.live.load(std::memory_order_acquire)) break;
            if (!env().call_changed || !env().call_changed(reinterpret_cast<KilnUiValueChangedFn>(p.fn), &c, p.user)) {
                fault(p, "faulted");
                break;
            }
            succeeded(p);
        }
    }
}

void on_frame(uint64_t frame, void*) {
    if (!g_wanted.load(std::memory_order_acquire)) return;
    if (!any(kListChanges)) {  // the last subscriber went: stop staging, forget the cache
        g_wanted.store(false, std::memory_order_release);
        State& s = st();
        std::lock_guard lk(s.mu);
        s.staged.clear();
        s.cache.clear();
        s.pending_reset = 0;
        return;
    }
    bool values = false;
    for (uint32_t k = 0; k < kValueKinds; ++k) values |= any(k);
    ClientNow now{};
    if (env().client) env().client(&now);
    std::vector<KilnUiValueChange> out;
    {
        State& s = st();
        std::lock_guard lk(s.mu);
        // a new client session (a world left or joined), or a reset asked for: one Reset, then a fresh cache
        uint32_t reset = s.pending_reset;
        uint64_t old = s.pending_reset ? s.reset_old : s.session;
        if (!reset && now.session != s.session && (s.session != 0 || !s.cache.empty())) reset = 1 + KUI_RESET_WORLD;
        if (!reset && !values && !s.cache.empty()) reset = 1 + KUI_RESET_REGISTRY;  // the last value provider went
        if (reset) {
            KilnUiValueChange c{};
            c.size = sizeof c;
            c.change_kind = KUI_RESET;
            c.reason = reset - 1;
            c.frame = frame;
            c.old_session = old;
            c.new_session = now.session;
            c.identity.size = sizeof c.identity;
            out.push_back(c);
            s.cache.clear();
            std::erase_if(s.staged, [&](const auto& kv) { return kv.first.session != now.session; });
            s.pending_reset = 0;
        }
        s.session = now.session;
        for (const auto& [k, v] : s.staged) {
            if (k.session != now.session) continue;
            const uint64_t mark = pair_kind(k.kind) ? s.updates : frame;
            auto it = s.cache.find(k);
            if (it != s.cache.end()) {
                if (!same(it->second.value, v)) out.push_back(change(KUI_UPDATED, k, &it->second.value, &v, frame));
                it->second = Cached{v, mark, frame};
                continue;
            }
            if (s.cache.size() >= kCacheMax) {  // the least recently seen goes, then the smallest key
                auto victim = s.cache.begin();
                for (auto c = s.cache.begin(); c != s.cache.end(); ++c)
                    if (c->second.frame < victim->second.frame) victim = c;
                out.push_back(change(KUI_EVICTED, victim->first, &victim->second.value, nullptr, frame));
                s.cache.erase(victim);
            }
            s.cache.emplace(k, Cached{v, mark, frame});
            out.push_back(change(KUI_ADDED, k, nullptr, &v, frame));
        }
        s.staged.clear();
        for (auto it = s.cache.begin(); it != s.cache.end();) {
            const uint64_t now_mark = pair_kind(it->first.kind) ? s.updates : frame;
            if (it->second.seen + 2 <= now_mark) {
                out.push_back(change(KUI_REMOVED, it->first, &it->second.value, nullptr, frame));
                it = s.cache.erase(it);
            } else {
                ++it;
            }
        }
    }
    deliver(out);
}

}  // namespace

bool changes_wanted() { return g_wanted.load(std::memory_order_acquire); }

void publish_value(uint32_t kind, const KilnUiItemView& view, const KilnUiBarValue& value) {
    if (!changes_wanted() || kind >= kValueKinds) return;
    State& s = st();
    std::lock_guard lk(s.mu);
    s.staged[key_of(kind, view)] = value;  // the last occurrence of the frame wins
}

void update_done() {
    if (!changes_wanted()) return;
    State& s = st();
    std::lock_guard lk(s.mu);
    ++s.updates;
}

void changes_reset(uint32_t reason) {
    if (!changes_wanted()) return;
    State& s = st();
    std::lock_guard lk(s.mu);
    if (s.cache.empty() && s.staged.empty()) return;
    if (!s.pending_reset) s.reset_old = s.session;
    s.pending_reset = 1 + reason;
    s.staged.clear();
}

void changes_watch() {
    const bool any_left = any(kListChanges);
    g_wanted.store(any_left, std::memory_order_release);
}

// subscribe_changes: the frame hook goes in with the first subscriber.
KilnResult changes_subscribe(KilnOwner owner, KilnUiValueChangedFn fn, void* user, KilnUiRegistration* out) {
    if (!env().client_side || !env().client_side()) return KILN_ERR_UNAVAILABLE;
    if (!env().owner_alive(owner)) return KILN_ERR_REVOKED;
    {
        State& s = st();
        std::lock_guard lk(s.mu);
        if (!s.frames_hooked) {
            if (!env().subscribe_frames || !env().subscribe_frames(&on_frame, nullptr)) {
                logf(KILN_LOG_WARN, "ui.values: no game UI frame callback (kiln.ui missing?): no change publication");
                return KILN_ERR_UNAVAILABLE;
            }
            s.frames_hooked = true;
            ClientNow now{};
            if (env().client) env().client(&now);
            s.session = now.session;
        }
    }
    const KilnResult rc = add(owner, kListChanges, 0, reinterpret_cast<void*>(fn), user, out);
    changes_watch();
    return rc;
}

void changes_reset_for_tests() {
    State& s = st();
    std::lock_guard lk(s.mu);
    s.staged.clear();
    s.cache.clear();
    s.session = s.updates = 0;
    s.frames_hooked = false;
    s.pending_reset = 0;
    g_wanted.store(false);
}

}  // namespace kiln::uivalues
