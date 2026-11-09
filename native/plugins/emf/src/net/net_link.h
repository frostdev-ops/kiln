// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.0, src/core/net/net_link.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// Mod messages (API v27): the chat transport between a player's game and the server, without the game. Two ends of
// one conversation, each talking to the game only through the functions in its Deps:
//   ServerLink  (a dedicated server, or a game this process hosts) takes the marked lines players send, answers a hello
//               with a welcome, puts a player's lines back into messages and hands them over; sends the beacon to each
//               new connection and lines to players that said hello.
//   ClientLink  (a joined player's game) takes the marked lines the server sends (never shown), answers the beacon
//               with a hello, and keeps the lines a mod's messages become until the game sends them.
// The conversation (docs/research/client_server_messages.md in the private notes, §5.2):
//   server -> each new connection   S 0 emf/net@1   a readable text (a game without EMF shows it once)
//   client -> server                H n emf/net@1   p1;<EMF version>;<API>;<build>;<channel>@<ver>,...
//   server -> client                W n emf/net@1   the same for the server
//   either way                      M n <channel>@<ver> <frag>/<count> ...   a message
//   either way                      B n emf/net@1   bye
//   server -> client                E n emf/net@1   a refusal (the reason as text)
// Nothing but the beacon goes to a connection before its hello; a marked line from a connection that did not say hello
// (or, on the client, before a welcome) is dropped. Lines come numbered per peer and direction, so a lost or doubled line
// never delivers half a message or one twice. Thread-safe. The link's lock is held while it calls the game-side Deps
// (send, send_lines, room, channels, is_local, now_ms, log): they must not call back into the link; the mods'
// callbacks (on_message, on_peer, on_server) run with no lock held.
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "net_line.h"
#include "net_peers.h"

namespace emf::core::net {

constexpr uint32_t kProtocol = 1;
constexpr char kControlChannel[] = "emf/net";
constexpr uint32_t kControlVersion = 1;

// "name@version,name@version" (sorted by name), and back (an entry that is not name@1..65535 is skipped).
std::string channels_text(const std::map<std::string, uint32_t>& channels);
std::map<std::string, uint32_t> parse_channels_text(std::string_view text);

// What a hello or welcome says about its sender.
struct HelloInfo {
    uint32_t protocol = kProtocol;
    std::string emf_version;  // "0.5.1": printable, no ';'
    uint32_t api = 0;
    uint32_t build = 0;
    std::map<std::string, uint32_t> channels;
};
std::string hello_text(const HelloInfo& h);
std::optional<HelloInfo> parse_hello_text(std::string_view text);

// ---- the server's end ----
struct ServerLinkDeps {
    // the channels this server has (name -> version); none: no beacon is sent
    std::function<std::map<std::string, uint32_t>()> channels;
    // one marked line for the player on (handle, account); false: it could not be queued
    std::function<bool(uint32_t handle, uint64_t account, const std::string& line)> send;
    // a message's (or welcome's) lines for the player on (handle, account), all or none; when set it is used for them
    // (under the link's lock, so the numbers and the queue order agree)
    std::function<bool(uint32_t handle, uint64_t account, const std::vector<std::string>& lines)> send_lines;
    // how many more lines the chat outbox takes (a message goes out whole or not at all)
    std::function<size_t()> room;
    // the connection that is this process's own player (a hosted game's host): never a peer, never sent a beacon
    std::function<bool(uint32_t handle, uint64_t account)> is_local;
    std::function<uint64_t()> now_ms;
    std::function<void(const std::string&)> log;
    // a whole message from a peer (the receive loop's thread); the channel is one the peer said it has
    std::function<void(uint32_t handle, uint64_t account, const std::string& channel, uint32_t peer_version,
                       const std::string& data)>
        on_message;
    std::function<void(uint32_t handle, uint64_t account, bool joined)> on_peer;
    std::string emf_version;
    uint32_t api = 0;
    uint32_t build = 0;
    bool greet = true;  // [net] greet: send the beacon (a server without it never gets a hello)
    // The first beacon this long after a connection appears (a line sent while the game still loads is lost), then
    // again this often until the game says hello, at most this many times (a game without EMF shows each one).
    uint64_t beacon_delay_ms = 5000;
    uint64_t beacon_retry_ms = 15000;
    uint32_t beacon_tries = 4;
};

// A player on a connection, as the server's records name it.
struct Connection {
    uint32_t handle = 0;
    uint64_t account = 0;
    bool operator<(const Connection& o) const {
        return handle != o.handle ? handle < o.handle : account < o.account;
    }
};

// Limits (research §5.6): lines a player's game may send, and the lines the server sends to one player.
constexpr double kLinesPerSecond = 20.0;
constexpr double kLineBurst = 40.0;
constexpr double kBytesPerSecond = 16384.0;
constexpr double kBytesBurst = 32768.0;

class ServerLink {
public:
    explicit ServerLink(ServerLinkDeps deps) : d_(std::move(deps)) {}

    // A marked line (is_marked) a player sent. Always true: EMF has taken it (the game must not add or show it).
    bool on_line(uint32_t handle, uint64_t account, std::string_view text);
    // After a run of the receive loop: the connections there are now. A new one gets the beacon; one that left (or whose
    // handle another account took) says bye to its mods.
    void tick(const std::vector<Connection>& connected);

    // A message to the peer on (handle, account): the lines are queued whole, or nothing is and `why` says why.
    bool send(uint32_t handle, uint64_t account, const std::string& channel, uint32_t version, const std::string& data,
              std::string* why);
    // The version of `channel` the peer said it has; 0 when it is no peer or has none.
    uint32_t peer_version(uint32_t handle, uint64_t account, const std::string& channel);
    std::vector<Connection> peers();
    // Every peer that said it has `channel` (any version).
    std::vector<Connection> peers_with(const std::string& channel);

private:
    struct Joining {
        Reassembly in;
    };
    bool allowed(uint64_t account, size_t bytes);
    bool queue(uint32_t handle, uint64_t account, const std::vector<std::string>& lines);  // send_lines, else send
    void note(uint64_t account, const std::string& what);
    void drop_peer(uint32_t handle, uint64_t account, bool tell);

    ServerLinkDeps d_;
    std::mutex m_;
    PeerTable peers_;
    std::map<Connection, Joining> joining_;
    struct Beaconing {
        uint64_t seen = 0;  // when the connection appeared
        uint64_t last = 0;  // the last beacon
        uint32_t sent = 0;
    };
    std::map<Connection, Beaconing> greeted_;  // the connections there are, and their beacons
    struct Budget {
        RateLimit lines{kLinesPerSecond, kLineBurst};
        RateLimit bytes{kBytesPerSecond, kBytesBurst};
        RateLimit out{kLinesPerSecond, kLineBurst};  // lines the server sends this player
        uint64_t window_start = 0;
        uint32_t window_drops = 0;
        uint64_t ignore_until = 0;
        uint64_t last_note = 0;
    };
    std::map<uint64_t, Budget> budgets_;  // by account
};

// ---- the client's end ----
struct ClientLinkDeps {
    std::function<uint64_t()> now_ms;  // Kiln: optional fragment expiry clock; no wire change
    std::function<std::map<std::string, uint32_t>()> channels;  // this game's channels
    // the local player's account as the server stamps it on a line it speaks as that player; nullopt: not known yet
    // (marked lines are then hidden and never answered)
    std::function<std::optional<uint64_t>()> self_account;
    // a whole message from the server (a channel the server said it has)
    std::function<void(const std::string& channel, uint32_t server_version, const std::string& data)> on_message;
    std::function<void(bool up)> on_server;  // the server welcomed this game / is gone
    std::function<void(const std::string&)> log;
    // false: this game hosts its server (or is offline): marked lines are hidden but not answered (the loopback delivers)
    std::function<bool()> enabled;
    std::string emf_version;
    uint32_t api = 0;
    uint32_t build = 0;
};
constexpr size_t kClientOutboxLines = 64;

class ClientLink {
public:
    explicit ClientLink(ClientLinkDeps deps) : d_(std::move(deps)) {}

    // A line the game's own chat got: true when it is EMF's (the game must not add it). `speaker`: the account the line
    // carries. Only the server's lines count (spoken as this player); the rest is dropped, but still taken.
    bool on_chat_line(std::string_view text, uint64_t speaker);
    // A message for the server: queued whole or not at all. Needs a welcome that has the channel at the same version.
    bool send(const std::string& channel, uint32_t version, const std::string& data, std::string* why);
    // At most `max` waiting lines, oldest first; put_back returns lines the game would not take (pool full) to the front.
    std::vector<std::string> take_lines(size_t max);
    void put_back(std::vector<std::string> lines);
    size_t waiting();
    bool welcomed();
    uint32_t server_version(const std::string& channel);  // 0: no welcome or no such channel
    // The server's world is gone (or a new one begins): no welcome, nothing waiting.
    void reset();

private:
    ClientLinkDeps d_;
    std::mutex m_;
    enum class State { Idle, HelloSent, Welcomed } state_ = State::Idle;
    bool told_foreign_ = false;  // a line not spoken as this player was logged
    bool told_no_self_ = false;  // a line before the game knew its own account was logged
    std::map<std::string, uint32_t> server_channels_;
    Reassembly in_;
    uint32_t out_seq_ = 0;
    std::vector<std::string> out_;
};

}  // namespace emf::core::net
