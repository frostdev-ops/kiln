// SPDX-License-Identifier: MIT
#include "log_tracker.h"

namespace kiln::players {
namespace {

bool starts(std::string_view s, std::string_view p) { return s.substr(0, p.size()) == p; }

std::string_view trim_right(std::string_view s) {
    while (!s.empty() && (s.back() == '\r' || s.back() == ' ' || s.back() == '\n')) s.remove_suffix(1);
    return s;
}

bool digit(char c) { return c >= '0' && c <= '9'; }

// s starts with p: *rest = the text after it.
bool after(std::string_view s, std::string_view p, std::string_view* rest) {
    if (!starts(s, p)) return false;
    *rest = s.substr(p.size());
    return true;
}

// Leading decimal number of s (up to 20 digits); false if none.
bool parse_u64(std::string_view s, uint64_t* out) {
    uint64_t v = 0;
    size_t i = 0;
    while (i < s.size() && i < 20 && digit(s[i])) v = v * 10 + static_cast<uint64_t>(s[i++] - '0');
    if (i == 0) return false;
    *out = v;
    return true;
}

// Text between `open` and the last `close` after it: "Player 'a'b' logged in" with open "Player '" and
// close "' logged in" gives "a'b".
bool between(std::string_view s, std::string_view open, std::string_view close, std::string_view* out) {
    const size_t a = s.find(open);
    if (a == std::string_view::npos) return false;
    const size_t from = a + open.size();
    const size_t b = s.rfind(close);
    if (b == std::string_view::npos || b < from) return false;
    *out = s.substr(from, b - from);
    return true;
}

}  // namespace

std::string_view strip_file_prefix(std::string_view l) {
    // "[I 00:53:15,846] " (level letter, time with milliseconds), as the game's log file writes every line.
    if (l.size() >= 17 && l[0] == '[' && l[2] == ' ' && digit(l[3]) && digit(l[4]) && l[5] == ':' && l[8] == ':' &&
        l[11] == ',') {
        const size_t close = l.find("] ");
        if (close != std::string_view::npos && close <= 20) return l.substr(close + 2);
    }
    return l;
}

bool parse_handle(std::string_view s, uint32_t* index) {
    uint64_t v = 0;
    if (!parse_u64(s, &v) || v >= LogTracker::kSlots) return false;
    size_t i = 0;
    while (i < s.size() && digit(s[i])) ++i;
    if (i >= s.size() || s[i] != '(') return false;
    *index = static_cast<uint32_t>(v);
    return true;
}

uint32_t permission_bit(std::string_view name) {
    if (name == "CanKickBan") return 0x01;
    if (name == "CanAccessInventories") return 0x02;
    if (name == "CanEditBase") return 0x04;
    if (name == "CanEditWorld") return 0x08;
    if (name == "CanExtendBase") return 0x10;
    if (name == "CanReceiveEXP") return 0x20;
    return 0;
}

uint64_t LogTracker::peer_steam(uint32_t peer) const {
    auto it = peers_.find(peer);
    return it == peers_.end() ? 0 : it->second;
}

int LogTracker::slot_by_name(std::string_view name, bool in_game_only) const {
    for (uint32_t i = 0; i < kSlots; ++i)
        if (slots_[i].used && slots_[i].name == name && (!in_game_only || slots_[i].in_game)) return static_cast<int>(i);
    return -1;
}

void LogTracker::leave(uint32_t slot, std::vector<LogEvent>& out) {
    LogSlot& s = slots_[slot];
    if (!s.in_game) return;
    out.push_back({2, slot, s});
    s.in_game = false;
    s.leaving = true;
}

void LogTracker::flush(std::vector<LogEvent>& out) {
    if (pending_join_ < 0) return;
    const uint32_t slot = static_cast<uint32_t>(pending_join_);
    pending_join_ = -1;
    if (slots_[slot].in_game) out.push_back({1, slot, slots_[slot]});
}

void LogTracker::feed(std::string_view message, std::vector<LogEvent>& out) {
    if (message.find('\n') != std::string_view::npos) {
        size_t start = 0;
        while (start <= message.size()) {
            size_t nl = message.find('\n', start);
            if (nl == std::string_view::npos) nl = message.size();
            if (!(nl == message.size() && nl == start)) line(message.substr(start, nl - start), out);
            start = nl + 1;
        }
        flush(out);
        return;
    }
    line(message, out);
}

void LogTracker::line(std::string_view raw, std::vector<LogEvent>& out) {
    ++lines_;
    const std::string_view l = trim_right(strip_file_prefix(raw));

    // "\t - CanKickBan": a permission of the player whose login line came just before.
    {
        size_t i = 0;
        while (i < l.size() && (l[i] == '\t' || l[i] == ' ')) ++i;
        if (i > 0 && l.substr(i, 2) == "- ") {
            if (perms_for_ >= 0) {
                slots_[perms_for_].perms |= permission_bit(l.substr(i + 2));
                ++matched_;
            }
            return;
        }
    }
    perms_for_ = -1;
    flush(out);
    if (l.empty()) return;

    std::string_view v, rest;
    uint32_t n = 0;
    uint64_t id = 0;

    if (after(l, "[online] Added peer ", &rest)) {  // "[online] Added peer 0(1) (steamid:7656...)" / "(7656...)"
        uint32_t peer = 0;
        size_t p = rest.find("steamid:");
        if (p != std::string_view::npos) p += 8;
        else if ((p = rest.find(" (")) != std::string_view::npos) p += 2;
        if (parse_handle(rest, &peer) && p < rest.size() && parse_u64(rest.substr(p), &id)) {
            peers_[peer] = id;
            last_peer_steam_ = id;
            ++matched_;
        }
        return;
    }
    if (starts(l, "[online] Client '") && between(l, "Client '", "' authenticated by steam", &v) &&
        parse_u64(v, &id)) {
        bool queued = false;
        for (uint64_t q : authed_) queued = queued || q == id;
        if (!queued) authed_.push_back(id);
        ++matched_;
        return;
    }
    if (after(l, "[session] Remote player added. Player handle: ", &rest) && parse_handle(rest, &n)) {
        LogSlot s;
        s.used = true;
        if (!authed_.empty()) {
            s.steam_id = authed_.front();
            authed_.pop_front();
        } else {
            s.steam_id = last_peer_steam_;
            for (const LogSlot& o : slots_)
                if (o.used && o.steam_id == s.steam_id) s.steam_id = 0;  // that one is taken
        }
        if (slots_[n].in_game) leave(n, out);
        slots_[n] = s;
        ++matched_;
        return;
    }
    if (starts(l, "[server] Machine '") && between(l, "': Player '", "' logged in", &v) && parse_handle(v, &n)) {
        slots_[n].used = true;
        pending_login_ = static_cast<int>(n);
        ++matched_;
        return;
    }
    if (starts(l, "[server] Player '") && between(l, "[server] Player '", "' logged in with Permissions:", &v)) {
        int slot = pending_login_;
        pending_login_ = -1;
        if (slot < 0) slot = slot_by_name(v, false);
        if (slot < 0)
            for (uint32_t i = 0; i < kSlots && slot < 0; ++i)
                if (slots_[i].used && !slots_[i].in_game && slots_[i].name.empty()) slot = static_cast<int>(i);
        if (slot < 0)
            for (uint32_t i = 0; i < kSlots && slot < 0; ++i)
                if (!slots_[i].used) slot = static_cast<int>(i);
        if (slot < 0) return;
        LogSlot& s = slots_[slot];
        if (s.in_game && s.name != v) leave(static_cast<uint32_t>(slot), out);
        s.used = true;
        s.name.assign(v);
        s.has_perms = true;
        s.perms = 0;
        s.leaving = false;
        const bool was = s.in_game;
        s.in_game = true;
        perms_for_ = slot;
        if (!was) pending_join_ = slot;
        ++matched_;
        return;
    }
    if (starts(l, "[server] Remove Entity for Player '") || starts(l, "[server] Remove Player '")) {
        // "Remove Entity for Player": the entity goes (left); "Remove Player": the server frees the slot.
        if (between(l, "Player '", "'", &v)) {
            const int slot = slot_by_name(v, false);
            if (slot >= 0) {
                leave(static_cast<uint32_t>(slot), out);
                if (starts(l, "[server] Remove Player '")) slots_[slot] = LogSlot{};
            }
            ++matched_;
        }
        return;
    }
    if (after(l, "[session] Player removed. Player handle: ", &rest) && parse_handle(rest, &n)) {
        leave(n, out);
        slots_[n] = LogSlot{};
        ++matched_;
        return;
    }
    if (starts(l, "[session] ") && l.find("Remove Player: ") != std::string_view::npos) {
        // "[session] Timed out while waiting for player. Remove Player: 0(0)" and the "Unable to wait" variant.
        if (parse_handle(l.substr(l.find("Remove Player: ") + 15), &n)) {
            leave(n, out);
            slots_[n] = LogSlot{};
            ++matched_;
        }
        return;
    }
    if (after(l, "[online] Removed peer ", &rest)) {
        uint32_t peer = 0;
        if (parse_handle(rest, &peer)) peers_.erase(peer);
        ++matched_;
        return;
    }
    if (l == "[online] Removing all peers") {
        peers_.clear();
        authed_.clear();
        ++matched_;
        return;
    }
    if (starts(l, "[game_server] Switching state from Run to Shutdown") ||
        starts(l, "[Session] started transition from 'Host_Online'")) {
        for (uint32_t i = 0; i < kSlots; ++i) leave(i, out);
        ++matched_;
        return;
    }
}

}  // namespace kiln::players
