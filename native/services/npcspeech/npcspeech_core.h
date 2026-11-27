// SPDX-License-Identifier: MIT
// kiln.npcspeech's state and the two things it does inside the game's calls, without Windows or the game: what one
// bubble draw shows (draw) and the mute/restore of a dialogue record's greeting voice-overs (voice). npcspeech.cpp wraps
// it in the hooks; tests/npcspeech_test.cpp drives it on fake structs. Since 0.2.0-dev.32 an NPC has lines of three
// kinds (the greeting, the menu's farewell, the menu's own line), each with its pick rule and open count, and the core
// queues what the host needs to hear (opens of host-picked lines).
#pragma once
#include <atomic>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "services/kiln_npcspeech.h"

namespace npcspeech {

// Offsets from sigdb (client.d/npcspeech.json). entry_size 0 = the record layout is unknown: voice() does nothing.
struct Layout {
    int64_t entry_record = 8;   // NpcBubble's entry: +0 entity id, +8 the dialogue record
    int64_t bubble_text = 0;    // BubbleDraw's params: +0 loca tag
    int64_t bubble_show = 4;    //                      +4 show (fades in / out)
    int64_t bubble_reset = 5;   //                      +5 hide at once
    int64_t prio_list = 0, prio_count = 0, plain_list = 0, plain_count = 0;  // the record's two greeting lists (ptr, u64 count)
    int64_t entry_size = 0, entry_vo = 0, vo_slots = 0;                     // a line entry: its inline voice-over GUIDs
    int64_t entry_line = 0;                                                  // a line entry: its line id (u32 at +0, PickLines)
    bool voice_sane() const {
        return prio_list > 0 && prio_count > 0 && plain_list > 0 && plain_count > 0 && entry_size >= 0x100 && entry_size <= 0x1000 &&
               entry_vo > 0 && vo_slots > 0 && vo_slots <= 64 && entry_vo + vo_slots * 16 <= entry_size && entry_line >= 0 &&
               entry_line + 4 <= entry_vo && prio_list < 0x20000 &&
               plain_list < 0x20000 && prio_count < 0x20000 && plain_count < 0x20000;
    }
};

struct Counts {
    uint32_t overrides = 0, says = 0, texts = 0, lines = 0, queued = 0;
    uint64_t bubbles = 0, changed = 0, muted = 0, opens = 0, dropped = 0, farewells = 0, menu_lines = 0;
};

// The dialogue window's offsets (sigdb client.d/npcdialog.json; 26430625's values as defaults for the tests).
struct DialogLayout {
    int64_t line = 0x2b0;   // the speech box's text (LocaTagId)
    int64_t close = 0x16;   // the close request (u8): the farewell
    int64_t open = 0x15;    // the open request (u8)
    int64_t direct = 0x14;  // straight to the crafting screen (u8)
    bool sane() const {
        return line > 0 && line + 4 <= 0x4000 && close > 0 && close < 0x100 && open > 0 && open < 0x100 && direct >= 0 &&
               direct < 0x100;
    }
};

// What the caller does after the game's DialogUpdate: show a line with the game's SetLine, keep the menu open.
struct DialogStep {
    uint32_t set_line = 0;
    bool clear_direct = false;
};

constexpr uint64_t kMaxEntries = 256;  // a list longer than this is not a dialogue record: leave it
constexpr uint64_t kReopenMs = KILN_SPEECH_REOPEN_MS;  // hidden this long, then shown: a new greeting
constexpr uint32_t kMaxEvents = 512;                   // events() keeps this many; older ones are dropped
constexpr uint32_t kKinds = 3;
constexpr uint64_t kMenuSayMs = 5000;                  // a host's reply waits this long for its NPC's menu

class Core {
public:
    static bool valid_set(uint32_t npc, uint32_t mode, const uint32_t* tags, uint32_t n) {
        if (!npc || mode > KILN_SPEECH_REPLACE) return false;
        if (mode != KILN_SPEECH_REPLACE) return true;
        if (!tags || n == 0 || n > KILN_SPEECH_MAX_LINES) return false;
        for (uint32_t i = 0; i < n; ++i)
            if (!tags[i]) return false;
        return true;
    }

    static bool valid_lines(uint32_t npc, uint32_t kind, uint32_t mode, const uint32_t* tags, uint32_t n, uint32_t pick) {
        if (kind >= kKinds || pick > KILN_SPEECH_PICK_HOST) return false;
        if (kind != KILN_SPEECH_KIND_GREETING && mode == KILN_SPEECH_SUPPRESS) return false;
        return valid_set(npc, mode, tags, n);
    }

    // The line of open number `open` (0, 1, ...) for a SEEDED pick: a pure function, the same on every client and run.
    static uint32_t seeded(uint32_t seed, uint32_t npc, uint32_t open, uint32_t n) {
        if (n <= 1) return 0;
        uint64_t x = (uint64_t(seed) << 32 | npc) ^ (uint64_t(open) * 0x9e3779b97f4a7c15ull);
        x ^= x >> 30;
        x *= 0xbf58476d1ce4e5b9ull;
        x ^= x >> 27;
        x *= 0x94d049bb133111ebull;
        x ^= x >> 31;
        return static_cast<uint32_t>(x % n);
    }

    // v1 (31.16): the greeting with PICK_CHANGE; each call picks anew at the next draw, as it did. Any thread.
    void set(uint32_t npc, uint32_t mode, const uint32_t* tags, uint32_t n) {
        std::lock_guard lock(mu_);
        auto& s = npcs_[npc];
        assign(s.kinds[KILN_SPEECH_KIND_GREETING], mode, tags, n, KILN_SPEECH_PICK_CHANGE, 0, true);
        s.seen_game = 0;
        prune(npc, s);
        publish();
    }

    // dev.32: one kind's override and pick; the same again keeps its open count. False (nothing changes) when invalid.
    bool set_lines(uint32_t npc, uint32_t kind, uint32_t mode, const uint32_t* tags, uint32_t n, uint32_t pick, uint32_t seed) {
        if (!valid_lines(npc, kind, mode, tags, n, pick)) return false;
        std::lock_guard lock(mu_);
        auto& s = npcs_[npc];
        assign(s.kinds[kind], mode, tags, n, pick, seed, false);
        prune(npc, s);
        publish();
        return true;
    }

    // HOST lines: what the next open of that kind shows (-1: the game's own); stays until the next call.
    bool next(uint32_t npc, uint32_t kind, int32_t line) {
        if (kind >= kKinds) return false;
        std::lock_guard lock(mu_);
        auto it = npcs_.find(npc);
        if (it == npcs_.end()) return false;
        auto& L = it->second.kinds[kind];
        if (L.mode != KILN_SPEECH_REPLACE || L.pick != KILN_SPEECH_PICK_HOST || line < KILN_SPEECH_LINE_GAME ||
            line >= static_cast<int32_t>(L.tags.size()))
            return false;
        L.next = line;
        return true;
    }

    // Up to max events, oldest first.
    uint32_t drain(KilnSpeechEvent* out, uint32_t max) {
        std::lock_guard lock(mu_);
        uint32_t k = 0;
        while (k < max && !events_.empty()) {
            out[k++] = events_.front();
            events_.pop_front();
        }
        return k;
    }

    // A farewell or menu line is set for some NPC, or a reply waits (the dialogue window's hook has work).
    bool dialog_active() const { return lines_.load(std::memory_order_acquire) || says_waiting_.load(std::memory_order_acquire); }

    // A host's reply waits for a menu (the window's hook looks every frame, not only at an open or a close).
    bool menu_say_pending() const { return says_waiting_.load(std::memory_order_acquire); }

    // A reply in the speech box of npc's menu: now if it is open, else at its open within kMenuSayMs; once. Tag 0 cancels.
    bool menu_say(uint32_t npc, uint32_t tag, uint64_t now) {
        if (!npc) return false;
        std::lock_guard lock(mu_);
        if (tag) says_[npc] = MenuSay{tag, now + kMenuSayMs};
        else says_.erase(npc);
        publish();
        return true;
    }

    // After the game's DialogUpdate(W) for the dialogue with `npc` (play.target): `was_open` / `was_close` are W's
    // requests at the call's entry, `kept` its answer (false: the caller closes the dialogue). A farewell of Kiln's
    // replaces the game's in W's box. Faults are the caller's (SEH): this writes W's line only.
    DialogStep dialog(uint32_t npc, uint8_t* w, const DialogLayout& d, bool was_open, bool was_close, bool kept, uint64_t now) {
        (void)kept;
        (void)now;
        DialogStep out;
        if (!npc || !w) return out;
        uint32_t farewell = 0;
        {
            std::lock_guard lock(mu_);
            bool replied = false;
            if (!says_.empty()) {
                for (auto x = says_.begin(); x != says_.end();) x = x->second.until < now ? says_.erase(x) : std::next(x);
                auto r = says_.find(npc);
                if (r != says_.end() && !was_close && (!was_open || kept)) {  // a host's reply to this player
                    out.set_line = r->second.tag;
                    out.clear_direct = was_open;
                    says_.erase(r);
                    menu_lines_++;
                    replied = true;
                }
                publish();
            }
            auto it = npcs_.find(npc);
            if (it == npcs_.end()) return out;
            auto& s = it->second;
            if (was_open && kept && !replied) {  // the menu's own line (the game clears it at an open, Flame NPCs aside)
                auto& m = s.kinds[KILN_SPEECH_KIND_MENU];
                if (m.mode == KILN_SPEECH_REPLACE && !m.tags.empty()) {
                    const int32_t i = choose(npc, KILN_SPEECH_KIND_MENU, m);
                    if (i >= 0) {
                        out.set_line = m.tags[static_cast<size_t>(i) < m.tags.size() ? i : 0];
                        out.clear_direct = true;
                        menu_lines_++;
                    }
                }
            }
            if (was_close && !was_open) {
                auto& f = s.kinds[KILN_SPEECH_KIND_FAREWELL];
                if (f.mode == KILN_SPEECH_REPLACE && !f.tags.empty()) {
                    const int32_t i = choose(npc, KILN_SPEECH_KIND_FAREWELL, f);
                    if (i >= 0) {
                        farewell = f.tags[static_cast<size_t>(i) < f.tags.size() ? i : 0];
                        farewells_++;
                    }
                }
            }
        }
        // game memory only outside the lock: a fault (the caller's SEH) must not leave it held
        if (farewell) std::memcpy(w + d.line, &farewell, 4);
        return out;
    }

    void say(uint32_t npc, uint32_t tag, uint32_t ms, uint64_t now) {
        std::lock_guard lock(mu_);
        auto& s = npcs_[npc];
        s.say_tag = tag && ms ? tag : 0;
        s.say_until = tag && ms ? now + ms : 0;
        prune(npc, s);
        publish();
    }

    void clear_all() {
        std::lock_guard lock(mu_);
        says_.clear();
        for (auto it = npcs_.begin(); it != npcs_.end();) {
            for (auto& L : it->second.kinds) L = Lines{};
            it->second.say_tag = 0;
            it->second.say_until = 0;
            // keep the NPC while voice-overs are muted: its next bubble call puts them back
            it = it->second.muted.empty() ? npcs_.erase(it) : std::next(it);
        }
        publish();
    }

    void count_text(uint32_t) { texts_.fetch_add(1, std::memory_order_relaxed); }

    // Something to do in a bubble call (an override, a Say, or voice-overs to put back).
    bool active() const { return any_.load(std::memory_order_acquire); }

    // BubbleDraw inside npc's bubble call: what the bubble shows. now: ms.
    void draw(uint32_t npc, uint8_t* params, const Layout& l, uint64_t now) {
        std::lock_guard lock(mu_);
        auto it = npcs_.find(npc);
        if (it == npcs_.end()) return;
        auto& s = it->second;
        if (s.say_tag && now >= s.say_until) {
            s.say_tag = 0;
            s.say_until = 0;
            prune_later_ = true;
        }
        auto& g = s.kinds[KILN_SPEECH_KIND_GREETING];
        if (!s.say_tag && g.mode == KILN_SPEECH_GAME) {
            if (s.muted.empty() && s.idle()) {  // nothing left for this NPC (no voice-overs to put back: voice() may never run)
                npcs_.erase(it);
                publish();
            }
            return;
        }
        bubbles_++;
        uint32_t text;
        std::memcpy(&text, params + l.bubble_text, 4);
        if (s.say_tag) {
            std::memcpy(params + l.bubble_text, &s.say_tag, 4);
            params[l.bubble_show] = 1;
            params[l.bubble_reset] = 0;
            changed_++;
            return;
        }
        if (g.mode == KILN_SPEECH_SUPPRESS) {
            if (params[l.bubble_show] || text) changed_++;
            params[l.bubble_show] = 0;
            return;
        }
        // Replace: a game pick (nonzero) becomes one of the mod's lines. A new line of the mod's at each greeting: the
        // game picks anew (PICK_CHANGE: only then, the 31.16 rule), or the bubble shows after kReopenMs hidden.
        const bool show = params[l.bubble_show] != 0;
        if (!text || g.tags.empty()) {
            if (s.shown) {
                s.shown = false;
                s.hidden_since = now;
            }
            return;
        }
        if (text != s.seen_game && !is_ours(g, text)) {
            s.seen_game = text;
            s.game_changed = true;
        }
        bool pick = false;
        if (g.pick == KILN_SPEECH_PICK_CHANGE) {
            pick = s.game_changed || g.cur == kUnpicked;
        } else if (show) {
            const bool reopen = !s.shown && (!s.ever_shown || now - s.hidden_since >= kReopenMs);
            pick = reopen || s.game_changed || g.cur == kUnpicked;
        }
        if (show) {
            s.shown = true;
            s.ever_shown = true;
        } else if (s.shown) {
            s.shown = false;
            s.hidden_since = now;
        }
        if (pick) {
            s.game_changed = false;
            g.cur = choose(npc, KILN_SPEECH_KIND_GREETING, g);
        }
        if (g.cur < 0) return;  // the game's own line this time (a host's pick), or not picked yet (still hidden)
        const uint32_t ours = g.tags[static_cast<size_t>(g.cur) < g.tags.size() ? g.cur : 0];
        if (text != ours) {
            std::memcpy(params + l.bubble_text, &ours, 4);
            changed_++;
        }
    }

    // NpcBubble with the NPC's record: mutes the greeting voice-overs while Kiln speaks for it, puts them back after.
    // The record is the one the game passes now: its record map keeps records inline and copies all of them to new
    // storage when it grows (0x459590 on 26430625), Kiln's zeros with them, so the saved voice-overs follow the NPC, not
    // an address. Each is put back only where the slot is still empty and the entry still holds the same line.
    // Faults are the caller's (SEH): this only reads and writes inside the record's own lists.
    void voice(uint32_t npc, uint8_t* record, const Layout& l) {
        if (!l.entry_size || !record) return;
        std::lock_guard lock(mu_);
        auto it = npcs_.find(npc);
        if (it == npcs_.end()) return;
        auto& s = it->second;
        const bool speaking = s.kinds[KILN_SPEECH_KIND_GREETING].mode != KILN_SPEECH_GAME || s.say_tag;
        if (speaking) {
            mute_list(s, record, l, l.prio_list, l.prio_count, 0);
            mute_list(s, record, l, l.plain_list, l.plain_count, 1);
        } else {  // nothing left for this NPC's bubble: put its voice-overs back (and forget it when nothing else is set)
            if (!s.muted.empty()) restore(s, record, l);
            s.muted.clear();
            if (s.idle()) npcs_.erase(it);
            publish();
        }
        if (prune_later_) {
            prune_later_ = false;
            publish();
        }
    }

    Counts counts() const {
        std::lock_guard lock(mu_);
        Counts c;
        tally(c);
        c.texts = texts_.load(std::memory_order_relaxed);
        c.bubbles = bubbles_;
        c.changed = changed_;
        c.muted = muted_;
        c.opens = opens_;
        c.dropped = dropped_;
        c.farewells = farewells_;
        c.menu_lines = menu_lines_;
        c.queued = static_cast<uint32_t>(events_.size());
        return c;
    }

    // tests
    size_t tracked() const {
        std::lock_guard lock(mu_);
        return npcs_.size();
    }

private:
    struct Muted {
        uint8_t list;     // 0 priority, 1 plain
        uint32_t index;   // entry in that list
        uint32_t slot;    // voice-over slot in the entry
        uint32_t line;    // the entry's line id when it was muted
        uint8_t guid[16];
    };
    static constexpr int32_t kUnpicked = -2;  // Lines::cur before the first pick
    // One kind of line of one NPC: its override, how it picks, and how many times it was shown.
    struct Lines {
        uint32_t mode = KILN_SPEECH_GAME;
        std::vector<uint32_t> tags;
        uint32_t pick = KILN_SPEECH_PICK_CHANGE, seed = 0;
        uint32_t opens = 0;       // picks so far (the next is open number `opens`)
        int32_t cur = kUnpicked;  // the index shown now; -1 the game's own line
        int32_t last = -1;        // the last index picked
        int32_t next = kUnpicked; // PICK_HOST: what next() gave; kUnpicked: nothing yet
    };
    struct Npc {
        Lines kinds[kKinds];
        uint32_t say_tag = 0;
        uint64_t say_until = 0;
        uint32_t seen_game = 0;        // the game's last pick in the bubble
        bool game_changed = false;     // ... changed since Kiln last picked
        bool shown = false, ever_shown = false;
        uint64_t hidden_since = 0;
        std::vector<Muted> muted;
        bool idle() const {
            for (auto& L : kinds)
                if (L.mode != KILN_SPEECH_GAME) return false;
            return !say_tag;
        }
    };

    static bool is_ours(const Lines& L, uint32_t t) {
        for (auto x : L.tags)
            if (x == t) return true;
        return false;
    }

    // Sets one kind's override; the same mode, tags and pick again keep its state (unless `fresh`).
    static void assign(Lines& L, uint32_t mode, const uint32_t* tags, uint32_t n, uint32_t pick, uint32_t seed, bool fresh) {
        std::vector<uint32_t> t;
        if (mode == KILN_SPEECH_REPLACE && tags) t.assign(tags, tags + n);
        if (!fresh && L.mode == mode && L.tags == t && L.pick == pick && L.seed == seed) return;
        L = Lines{};
        L.mode = mode;
        L.tags = std::move(t);
        L.pick = pick;
        L.seed = seed;
    }

    // The line of the next open of L (an index, or -1 for the game's own line); counts it, queues an event for the host.
    int32_t choose(uint32_t npc, uint32_t kind, Lines& L) {
        const uint32_t n = static_cast<uint32_t>(L.tags.size());
        int32_t i = 0;
        switch (L.pick) {
        case KILN_SPEECH_PICK_CHANGE:
            i = n > 1 ? static_cast<int32_t>(next_rand() % n) : 0;
            break;
        case KILN_SPEECH_PICK_ORDER:
            i = static_cast<int32_t>((uint64_t(L.seed) + L.opens) % (n ? n : 1));
            break;
        case KILN_SPEECH_PICK_SEEDED:
            i = static_cast<int32_t>(seeded(L.seed, npc, L.opens, n));
            break;
        case KILN_SPEECH_PICK_HOST:
            if (L.next != kUnpicked) {
                i = L.next;
                break;
            }
            [[fallthrough]];
        default:  // RANDOM: never the same line twice in a row
            if (n <= 1) i = 0;
            else if (L.last >= 0 && static_cast<uint32_t>(L.last) < n) {
                const uint32_t r = next_rand() % (n - 1);
                i = static_cast<int32_t>(r >= static_cast<uint32_t>(L.last) ? r + 1 : r);
            } else i = static_cast<int32_t>(next_rand() % n);
        }
        L.opens++;
        opens_++;
        if (i >= 0) L.last = i;
        if (L.pick == KILN_SPEECH_PICK_HOST) push({npc, KILN_SPEECH_EVENT_OPEN, static_cast<uint8_t>(kind), 0, i, L.opens});
        return i;
    }

    void push(const KilnSpeechEvent& e) {
        if (events_.size() >= kMaxEvents) {
            events_.pop_front();
            dropped_++;
        }
        events_.push_back(e);
    }

    // An NPC with nothing left is dropped unless voice-overs still need putting back.
    void prune(uint32_t npc, Npc& s) {
        if (s.idle() && s.muted.empty()) npcs_.erase(npc);
    }

    void publish() {
        bool any = false, lines = false;
        for (auto& [k, v] : npcs_) {
            if (v.kinds[KILN_SPEECH_KIND_GREETING].mode != KILN_SPEECH_GAME || v.say_tag || !v.muted.empty()) any = true;
            if (v.kinds[KILN_SPEECH_KIND_FAREWELL].mode != KILN_SPEECH_GAME || v.kinds[KILN_SPEECH_KIND_MENU].mode != KILN_SPEECH_GAME)
                lines = true;
        }
        any_.store(any, std::memory_order_release);
        lines_.store(lines, std::memory_order_release);
        says_waiting_.store(!says_.empty(), std::memory_order_release);
    }

    void tally(Counts& c) const {
        for (auto& [k, v] : npcs_) {
            if (v.kinds[KILN_SPEECH_KIND_GREETING].mode != KILN_SPEECH_GAME) c.overrides++;
            if (v.say_tag) c.says++;
            if (v.kinds[KILN_SPEECH_KIND_FAREWELL].mode != KILN_SPEECH_GAME || v.kinds[KILN_SPEECH_KIND_MENU].mode != KILN_SPEECH_GAME)
                c.lines++;
        }
    }

    void mute_list(Npc& s, uint8_t* record, const Layout& l, int64_t list_off, int64_t count_off, uint8_t which) {
        uint8_t* base;
        uint64_t n;
        std::memcpy(&base, record + list_off, sizeof base);
        std::memcpy(&n, record + count_off, sizeof n);
        if (!base || n > kMaxEntries) return;
        for (uint64_t i = 0; i < n; ++i) {
            uint8_t* entry = base + i * static_cast<uint64_t>(l.entry_size);
            uint8_t* vo = entry + l.entry_vo;
            for (int64_t k = 0; k < l.vo_slots; ++k) {
                uint8_t* g = vo + k * 16;
                static const uint8_t zero[16] = {};
                if (std::memcmp(g, zero, 16) == 0) continue;  // already muted, or a line without voice
                Muted m{which, static_cast<uint32_t>(i), static_cast<uint32_t>(k), 0, {}};
                std::memcpy(&m.line, entry + l.entry_line, 4);
                std::memcpy(m.guid, g, 16);
                // a slot Kiln already holds that is filled again: the game rebuilt the record, its value is the current one
                bool held = false;
                for (auto& x : s.muted)
                    if (x.list == which && x.index == i && x.slot == k) {
                        x = m;
                        held = true;
                    }
                if (!held) s.muted.push_back(m);
                std::memset(g, 0, 16);
                muted_++;
            }
        }
    }

    void restore(Npc& s, uint8_t* record, const Layout& l) {
        for (auto& m : s.muted) {
            const int64_t list_off = m.list ? l.plain_list : l.prio_list, count_off = m.list ? l.plain_count : l.prio_count;
            uint8_t* base;
            uint64_t n;
            std::memcpy(&base, record + list_off, sizeof base);
            std::memcpy(&n, record + count_off, sizeof n);
            if (!base || m.index >= n || n > kMaxEntries) continue;  // the record changed shape: the game refilled it itself
            uint8_t* entry = base + m.index * static_cast<uint64_t>(l.entry_size);
            uint32_t line;
            std::memcpy(&line, entry + l.entry_line, 4);
            if (line != m.line) continue;  // another line sits there now (a rebuilt list): not this voice-over's place
            uint8_t* g = entry + l.entry_vo + m.slot * 16;
            static const uint8_t zero[16] = {};
            if (std::memcmp(g, zero, 16) == 0) std::memcpy(g, m.guid, 16);
        }
    }

    uint32_t next_rand() {
        rng_ ^= rng_ << 13;
        rng_ ^= rng_ >> 17;
        rng_ ^= rng_ << 5;
        return rng_;
    }

    mutable std::mutex mu_;
    std::unordered_map<uint32_t, Npc> npcs_;
    std::deque<KilnSpeechEvent> events_;
    std::atomic<bool> any_{false}, lines_{false};
    std::atomic<uint32_t> texts_{0};
    bool prune_later_ = false;
    uint64_t bubbles_ = 0, changed_ = 0, muted_ = 0, opens_ = 0, dropped_ = 0, farewells_ = 0, menu_lines_ = 0;
    struct MenuSay {
        uint32_t tag;
        uint64_t until;
    };
    std::unordered_map<uint32_t, MenuSay> says_;  // a host's replies waiting for their NPC's menu
    std::atomic<bool> says_waiting_{false};
    uint32_t rng_ = 0x9e3779b9u;
};

}  // namespace npcspeech
