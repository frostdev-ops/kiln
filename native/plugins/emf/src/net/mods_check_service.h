// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/mods_check_service.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// The mod check on join between an EMF game and an EMF server (the core channel emf/mods, mods_check.h): a joined game
// sends its list once per session when the server has the channel; the server keeps it per account, logs the
// differences and answers with its own list; the game compares and shows a note once per session when it lacks or has
// another version of a mod that must match. The host's own game never sends (its server is this process).
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "mods_check.h"

namespace emf::core {

constexpr const char* kModsChannel = "emf/mods";
constexpr uint32_t kModsChannelVersion = 1;
constexpr size_t kModsCheckMaxPlayers = 256;  // accounts kept on the server (the oldest goes)
// A joined game sends its list again this many client ticks (seconds) after the last send while the server's list has
// not come (a list the server could not place, or dropped on a new hello, is not lost for good), at most this often.
constexpr uint32_t kModsResendTicks = 5;
constexpr uint32_t kModsSendTries = 6;

class ModsCheck {
public:
    struct Deps {
        std::function<std::vector<ModEntry>()> mine;  // this game's list (with its "e emf <version>" entry)
        std::function<bool()> joined;                 // this game is in a world another process runs
        std::function<uint32_t()> server_version;     // the server's emf/mods version (0: none, or no welcome yet)
        std::function<uint32_t()> client_session;     // +1 per client world: a new session sends and notes again
        std::function<bool(const std::string&)> to_server;
        std::function<bool(uint32_t player, const std::string&)> to_player;
        std::function<void(const std::string&)> notify;  // the player's note
        std::function<void(const std::string&)> log;
        std::function<bool(uint32_t player)> has_peer;   // the server: that player's connection has emf/mods now
    };
    struct Seen {
        uint32_t player = 0;
        std::string emf;  // the player's EMF version ("" when the list did not say)
        std::vector<ModEntry> mods;
        std::string diff;  // diff_text against the server's list
    };

    explicit ModsCheck(Deps d) : d_(std::move(d)) {}

    // A joined game, once a second (client world).
    void client_tick() {
        if (!d_.joined || !d_.joined()) return;
        if (!d_.server_version || d_.server_version() == 0) return;
        const uint32_t session = d_.client_session ? d_.client_session() : 0;
        if (!send_started_ || send_session_ != session) {  // a new session: sends start over
            send_started_ = true;
            send_session_ = session;
            tries_ = 0;
            wait_ = 0;
        }
        if (answered_ && answered_session_ == session) return;
        if (tries_ >= kModsSendTries) return;
        if (tries_ > 0 && ++wait_ < kModsResendTicks) return;
        if (d_.to_server && d_.to_server(encode_mods(d_.mine ? d_.mine() : std::vector<ModEntry>{}))) {
            ++tries_;
            wait_ = 0;
        }
    }

    // The server: a player's game sent its list.
    void on_server_message(uint32_t player, uint64_t account, const std::string& data) {
        std::string why;
        const auto theirs = decode_mods(data, &why);
        if (!theirs) {
            told("a mod list from " + acct(account) + " was not taken: " + why);
            return;
        }
        bool has_emf = false;  // every game's list starts with its EMF: without it (or with broken lines only) it is no list
        for (const auto& e : *theirs) has_emf = has_emf || (e.kind == 'e' && e.id == "emf");
        if (!has_emf) {
            told("a mod list from " + acct(account) + " was not taken: no EMF entry");
            return;
        }
        const auto mine = d_.mine ? d_.mine() : std::vector<ModEntry>{};
        Seen s;
        s.player = player;
        for (const auto& m : *theirs)
            if (m.kind == 'e' && m.id == "emf") s.emf = m.version;
        s.mods = *theirs;
        s.diff = diff_text(compare_mods(mine, *theirs));
        if (d_.log)
            d_.log("mods check: player " + acct(account) + " (entity " + std::to_string(player) + ", EMF " +
                   (s.emf.empty() ? std::string("?") : s.emf) + "): " + s.diff);
        {
            std::lock_guard<std::mutex> l(m_);
            if (!seen_.count(account) && seen_.size() >= kModsCheckMaxPlayers) {
                seen_.erase(order_.front());
                order_.erase(order_.begin());
            }
            if (!seen_.count(account)) order_.push_back(account);
            seen_[account] = std::move(s);
        }
        if (d_.to_player) d_.to_player(player, encode_mods(mine));
    }

    // The joined game: the server's list came.
    void on_player_message(const std::string& data) {
        std::string why;
        const auto server = decode_mods(data, &why);
        if (!server) {
            told("the server's mod list was not taken: " + why);
            return;
        }
        const uint32_t session = d_.client_session ? d_.client_session() : 0;
        answered_ = true;  // the server has our list: no more sends this session
        answered_session_ = session;
        const ModDiff d = compare_mods(*server, d_.mine ? d_.mine() : std::vector<ModEntry>{});
        if (d_.log) d_.log("mods check: against the server's mods: " + diff_text(d));
        const std::string note = player_note(d);
        if (note.empty() || (noted_ && noted_session_ == session)) return;
        noted_ = true;
        noted_session_ = session;
        if (d_.notify) d_.notify(note);
    }

    // The list `player` (entity) sent, while their connection still has emf/mods (a player back without EMF, or with
    // an older one, has none: the old list is not theirs any more).
    std::optional<Seen> current(uint64_t account, uint32_t player) const {
        auto s = this->player(account);
        if (!s || s->player != player || !d_.has_peer || !d_.has_peer(player)) return std::nullopt;
        return s;
    }

    // The server: `account`'s game said hello again, or its peer is gone: the list it sent before is not this
    // connection's (a player back as the same entity on the same connection kept it until the new list came).
    void forget(uint64_t account) {
        std::lock_guard<std::mutex> l(m_);
        if (!seen_.erase(account)) return;
        order_.erase(std::remove(order_.begin(), order_.end(), account), order_.end());
    }

    // This game's own list (with its "e emf <version>" entry).
    std::vector<ModEntry> mine() const { return d_.mine ? d_.mine() : std::vector<ModEntry>{}; }

    std::optional<Seen> player(uint64_t account) const {
        std::lock_guard<std::mutex> l(m_);
        const auto it = seen_.find(account);
        return it == seen_.end() ? std::nullopt : std::optional<Seen>(it->second);
    }

    // Whether this game has mods its players must match (content or side "both"): only then does the core's channel
    // alone make the server greet new connections.
    bool has_shared_mods() const {
        for (const auto& m : d_.mine ? d_.mine() : std::vector<ModEntry>{})
            if (mod_kind_compared(m.kind)) return true;
        return false;
    }

    bool server_has_content() const {
        for (const auto& m : d_.mine ? d_.mine() : std::vector<ModEntry>{})
            if (m.kind == 'c') return true;
        return false;
    }

private:
    static std::string acct(uint64_t a) {
        char b[24];
        std::snprintf(b, sizeof b, "0x%016llx", static_cast<unsigned long long>(a));
        return b;
    }
    void told(const std::string& line) {  // once per kind of problem
        const std::string key = line.substr(line.rfind(": ") + 2);
        {
            std::lock_guard<std::mutex> l(m_);
            if (!told_.insert(key).second) return;
        }
        if (d_.log) d_.log("mods check: " + line + " (logged once)");
    }

    Deps d_;
    mutable std::mutex m_;
    std::map<uint64_t, Seen> seen_;
    std::vector<uint64_t> order_;
    std::set<std::string> told_;
    // the client world's thread
    bool send_started_ = false, answered_ = false, noted_ = false;
    uint32_t send_session_ = 0, answered_session_ = 0, noted_session_ = 0;
    uint32_t tries_ = 0, wait_ = 0;
};

}  // namespace emf::core
