// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.0, src/core/net/net_service.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// Mod messages (API v27): the channels mods register and the delivery of their messages. Loopback in a game this
// process hosts (or an offline one): the local game's messages reach the server's handler, the server's messages to the
// host's own player reach the local handler, each after the next step of the world it is for. Remote peers (joined
// players, the dedicated server) go through the chat transport (net_link.h): enable_server / enable_client switch each
// end on, and the game's chat hooks feed it. Testable: the game is reached only through NetDeps and the Envs.
#include <atomic>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "emf/emf_api.h"
#include "net_link.h"
#include "net_peers.h"

namespace emf::core::net {

constexpr uint32_t kToServer = 0x01u;   // players' games send it, the server receives it
constexpr uint32_t kToPlayers = 0x02u;  // the server sends it, players' games receive it
constexpr uint32_t kMaxMessage = 16384u;
constexpr uint32_t kDefaultMaxMessage = 4096u;
constexpr uint32_t kDefaultPer10s = 100u;

struct NetFrom {
    uint32_t player = 0;       // server: the sender's player entity; a player's game: 0
    uint64_t account = 0;      // server: the sender's account (the server's own records); 0 in process
    uint32_t peer_version = 0; // the channel version the other side has
    bool admin = false;        // server: the host's own game (or a [chat] admin)
    bool local = false;        // same process (the host's own game, an offline game)
};
using NetHandler = std::function<void(EmfWorld*, const NetFrom&, const std::string& data)>;

struct ChannelDef {
    std::string name;          // "<mod id>/<name>"
    uint32_t version = 1;      // 1..65535
    uint32_t flags = 0;        // kToServer | kToPlayers
    uint32_t max_size = 0;     // 0 = kDefaultMaxMessage
    uint32_t per_10s = 0;      // messages one sender may send in 10 s; 0 = kDefaultPer10s
    NetHandler on_server;      // the server world's thread, after its step
    NetHandler on_player;      // the client world's thread, after its step
};

struct NetDeps {
    std::function<bool(uint32_t* local_player)> hosted;  // this process hosts the server world with a local player
    std::function<void(uint8_t world_kind, std::function<void(EmfWorld*)>)> post;  // run after that world's next step
    std::function<uint64_t()> now_ms;
    std::function<void(const std::string&)> log;
    // server side, for remote peers (the chat transport)
    std::function<std::optional<std::pair<uint32_t, uint64_t>>(uint32_t entity)> connection_of;  // entity -> (handle, account)
    std::function<uint32_t(uint64_t account)> entity_of;  // the server world's thread: account -> player entity (0: none)
    std::function<bool(uint64_t account)> is_admin;       // as EmfCommandSender.admin
};

// The game-facing parts of the two ends of the chat transport (the service adds the channels and the delivery).
struct ServerEnv {
    std::function<bool(uint32_t handle, uint64_t account, const std::string& line)> send;  // one marked line to a player
    std::function<bool(uint32_t handle, uint64_t account, const std::vector<std::string>& lines)> send_lines;  // all or none
    std::function<size_t()> room;                                                           // lines the outbox takes
    std::function<bool(uint32_t handle, uint64_t account)> is_local;  // this process's own player (loopback, no chat)
    std::string emf_version;
    uint32_t api = 0, build = 0;
    bool greet = true;
    uint64_t beacon_delay_ms = 5000;  // ServerLinkDeps: the first beacon after a connection appears
};
struct ClientEnv {
    std::function<std::optional<uint64_t>()> self_account;  // empty or nullopt: the first beacon's speaker
    std::string emf_version;
    uint32_t api = 0, build = 0;
};

class NetService {
public:
    explicit NetService(NetDeps deps) : d_(std::move(deps)) {}

    // `owner`: the calling mod (its module name); the script host "lua" may register its scripts' channels.
    bool add_channel(const std::string& owner, const ChannelDef& def, std::string* why);
    // A player's game -> the server. True when it is on its way.
    bool to_server(const std::string& owner, const std::string& channel, const std::string& data, std::string* why);
    // The server -> one player's game (player: its entity in the server world).
    bool to_player(const std::string& owner, uint32_t player, const std::string& channel, const std::string& data,
                   std::string* why);
    // The server -> every player whose game has the channel; how many it went to.
    uint32_t to_players(const std::string& owner, const std::string& channel, const std::string& data);
    // The channel version `player`'s game has (server side), or the server's (player 0, a player's game); 0 = none.
    uint32_t peer(uint32_t player, const std::string& channel);

    // ---- the chat transport ----
    void enable_server(ServerEnv env);
    void enable_client(ClientEnv env);
    // A marked chat line a player sent (the server's receive loop): true when EMF took it (always for a marked line once
    // the server end is on).
    bool server_line(uint32_t handle, uint64_t account, std::string_view text);
    // The connections the server has now (after a run of the receive loop).
    void server_tick(const std::vector<Connection>& connected);
    // A marked line the client's chat got: true when it is EMF's (never shown).
    bool client_line(std::string_view text, uint64_t speaker);
    std::vector<std::string> client_take(size_t max);
    void client_put_back(std::vector<std::string> lines);
    void client_reset();
    bool server_on() const { return server_ != nullptr; }
    bool client_on() const { return client_ != nullptr; }
    // This game's channels with their versions (what hello and welcome say).
    std::map<std::string, uint32_t> channel_versions();

private:
    struct Channel {
        ChannelDef def;
        std::string owner;
        std::map<uint64_t, std::unique_ptr<RateLimit>> senders;  // by sender (0 = the local game)
    };
    Channel* find(const std::string& name);
    bool sendable(const std::string& owner, Channel* c, uint32_t flag, const std::string& data, uint64_t sender,
                  std::string* why);
    void remote_to_server(uint32_t handle, uint64_t account, const std::string& channel, uint32_t peer_version,
                          const std::string& data);
    void remote_to_client(const std::string& channel, uint32_t server_version, const std::string& data);
    // server -> the peer on (handle, account), whole or not at all
    bool send_remote(const std::string& owner, uint32_t handle, uint64_t account, const std::string& channel,
                     const std::string& data, std::string* why);

    NetDeps d_;
    std::mutex m_;
    std::map<std::string, Channel> channels_;
    std::set<std::string> logged_;
    // set once, then published: the chat hooks can already run on other threads while the ends are made
    std::unique_ptr<ServerLink> server_owned_;
    std::unique_ptr<ClientLink> client_owned_;
    std::atomic<ServerLink*> server_{nullptr};
    std::atomic<ClientLink*> client_{nullptr};
};

}  // namespace emf::core::net
