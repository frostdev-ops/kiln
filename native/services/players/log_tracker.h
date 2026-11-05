// SPDX-License-Identifier: MIT
// kiln.players: follows the game log's join/leave lines (svn 1285601 wording, unchanged since 1273043) and keeps a
// per-slot view of who is on the server. Used for SteamID64s (only the log has them) and as the whole player list on
// a build where the player records are not found. Pure C++ (no game memory, no Windows headers): the service feeds it
// the core's game-log mirror, `replay_log` and the Linux unit test feed it fixtures.
//
// The lines it understands, in the order a join writes them (real lines in docs/re/players.md):
//   [online] Added peer 0(1) (steamid:76561198000000001)
//   [online] Client '76561198000000001' authenticated by steam
//   [session] Remote player added. Player handle: 0(0)
//   [server] Machine '1': Player '0(0)' logged in
//   [server] Player 'Name' logged in with Permissions:\n\t - CanKickBan\n ...      (one message, or one line each)
// and a leave:
//   [server] Remove Entity for Player 'Name'   /   [server] Remove Player 'Name'
//   [online] Removed peer 0(1)                 /   [session] Player removed. Player handle: 0(0)
// Player handle "N(g)": N is the slot; the player entity is N + 1 ("Player EntityId 1" is slot 0's).
#pragma once
#include <array>
#include <cstdint>
#include <deque>
#include <map>
#include <string>
#include <string_view>
#include <vector>

namespace kiln::players {

struct LogSlot {
    bool used = false;      // a connection holds this slot
    bool in_game = false;   // the login line was seen and no leave since
    bool leaving = false;   // was in game, a leave line came, the slot is not freed yet
    bool has_perms = false;
    uint32_t perms = 0;     // KILN_PLAYER_CAN_* bits from the "\t - CanX" lines
    uint64_t steam_id = 0;
    std::string name;
};

struct LogEvent {
    int kind;      // 1 joined, 2 left (KILN_PLAYER_JOINED / _LEFT)
    uint32_t slot;
    LogSlot player; // as it was at the event
};

class LogTracker {
public:
    static constexpr uint32_t kSlots = 64;  // the slot field is 6 bits (handle & 0x3F)

    // One game-log message (may hold several lines separated by '\n'). Appends events to `out`. A JOINED waits for
    // the login line's permission list: it is emitted at the next other line, at the end of a multi-line message, or
    // by flush().
    void feed(std::string_view message, std::vector<LogEvent>& out);
    void flush(std::vector<LogEvent>& out);
    const LogSlot& slot(uint32_t i) const { return slots_[i]; }
    uint64_t lines() const { return lines_; }
    uint64_t matched() const { return matched_; }
    // The steam id of the most recent join that had one, for a slot the tracker does not know (diagnostics).
    uint64_t peer_steam(uint32_t peer) const;

private:
    void line(std::string_view l, std::vector<LogEvent>& out);
    void leave(uint32_t slot, std::vector<LogEvent>& out);
    int slot_by_name(std::string_view name, bool in_game_only) const;

    std::array<LogSlot, kSlots> slots_{};
    std::map<uint32_t, uint64_t> peers_;  // peer index -> steam id ("Added peer")
    std::deque<uint64_t> authed_;         // authenticated steam ids not yet given to a player
    uint64_t last_peer_steam_ = 0;
    int pending_login_ = -1;              // slot of the last "Machine 'm': Player 'N(g)' logged in"
    int perms_for_ = -1;                  // slot whose "\t - CanX" lines follow
    int pending_join_ = -1;               // slot whose JOINED waits for its permission lines
    uint64_t lines_ = 0, matched_ = 0;
};

// Helpers shared with the tests.
std::string_view strip_file_prefix(std::string_view l);  // "[I 00:53:15,846] text" -> "text"
bool parse_handle(std::string_view s, uint32_t* index);   // "12(3)..." -> 12
uint32_t permission_bit(std::string_view name);            // "CanKickBan" -> 0x01; 0 if unknown

}  // namespace kiln::players
