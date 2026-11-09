// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.0, src/core/net/net_service.cpp.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#include "net_service.h"

namespace emf::core::net {

namespace {

constexpr size_t kMaxRememberedErrors = 128;
bool remember_error(std::set<std::string>& logged, std::string message) {
    return logged.size() < kMaxRememberedErrors && logged.insert(std::move(message)).second;
}

bool part_ok(const std::string& s, size_t max) {
    if (s.empty() || s.size() > max) return false;
    for (const char c : s)
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-')) return false;
    return true;
}

bool fail(std::string* why, const std::string& r) {
    if (why) *why = r;
    return false;
}

std::string lower(std::string s) {
    for (char& c : s)
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return s;
}

}  // namespace

NetService::Channel* NetService::find(const std::string& name) {
    const auto it = channels_.find(name);
    return it == channels_.end() ? nullptr : &it->second;
}

bool NetService::add_channel(const std::string& owner, const ChannelDef& def, std::string* why) {
    const size_t slash = def.name.find('/');
    if (slash == std::string::npos) return fail(why, "a channel is named <mod id>/<name>");
    const std::string mod = def.name.substr(0, slash), name = def.name.substr(slash + 1);
    if (!part_ok(mod, 48) || !part_ok(name, 32))
        return fail(why, "a channel's mod id and name are 1-48 and 1-32 of a-z 0-9 _ . -");
    if (lower(owner) != "lua" && lower(owner) != mod)
        return fail(why, "a channel goes under your own mod id (" + lower(owner) + "/...), not " + mod);
    if (def.version == 0 || def.version > 65535) return fail(why, "the version is 1..65535");
    if (!(def.flags & (kToServer | kToPlayers)) || (def.flags & ~(kToServer | kToPlayers)))
        return fail(why, "flags: EMF_NET_TO_SERVER and/or EMF_NET_TO_PLAYERS");
    if (def.max_size > kMaxMessage) return fail(why, "max_size is at most 16384");
    const std::lock_guard<std::mutex> g(m_);
    if (find(def.name)) return fail(why, def.name + " is taken");
    Channel c;
    c.def = def;
    if (!c.def.max_size) c.def.max_size = kDefaultMaxMessage;
    if (!c.def.per_10s) c.def.per_10s = kDefaultPer10s;
    c.owner = lower(owner);
    channels_.emplace(def.name, std::move(c));
    return true;
}

bool NetService::sendable(const std::string& owner, Channel* c, uint32_t flag, const std::string& data,
                          uint64_t sender, std::string* why) {
    if (!c) return fail(why, "no such channel");
    if (lower(owner) != c->owner) return fail(why, "only the mod that registered the channel sends on it");
    if (!(c->def.flags & flag))
        return fail(why, flag == kToServer ? "the channel does not go to the server"
                                           : "the channel does not go to players");
    if (data.size() > c->def.max_size)
        return fail(why, "too large (" + std::to_string(data.size()) + " bytes; the channel takes " +
                             std::to_string(c->def.max_size) + ")");
    auto& rl = c->senders[sender];
    if (!rl) rl = std::make_unique<RateLimit>(c->def.per_10s / 10.0, static_cast<double>(c->def.per_10s));
    if (!rl->take(d_.now_ms()))
        return fail(why, "over the channel's rate (" + std::to_string(c->def.per_10s) + " in 10 s)");
    return true;
}

bool NetService::to_server(const std::string& owner, const std::string& channel, const std::string& data,
                           std::string* why) {
    uint32_t local = 0;
    NetHandler handler;
    NetFrom from;
    uint32_t version = 0;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!sendable(owner, c, kToServer, data, 0, why)) return false;
        version = c->def.version;
        if (d_.hosted(&local)) {
            handler = c->def.on_server;
            from.player = local;
            from.local = true;
            from.admin = true;
            from.peer_version = version;
        }
    }
    if (!local) {  // a joined game: through the chat, once the server has welcomed it
        if (!client_) return fail(why, "no server to send to yet (it has not welcomed this game)");
        return client_.load()->send(channel, version, data, why);
    }
    if (handler) d_.post(EMF_WORLD_SERVER, [handler, from, data](EmfWorld* w) { handler(w, from, data); });
    return true;
}

bool NetService::send_remote(const std::string& owner, uint32_t handle, uint64_t account, const std::string& channel,
                             const std::string& data, std::string* why) {
    uint32_t version = 0;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!sendable(owner, c, kToPlayers, data, account, why)) return false;
        version = c->def.version;
    }
    return server_.load()->send(handle, account, channel, version, data, why);
}

bool NetService::to_player(const std::string& owner, uint32_t player, const std::string& channel,
                           const std::string& data, std::string* why) {
    uint32_t local = 0;
    NetHandler handler;
    NetFrom from;
    bool loopback = false;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!c) return fail(why, "no such channel");
        loopback = d_.hosted(&local) && player == local;
        if (loopback) {
            if (!sendable(owner, c, kToPlayers, data, player, why)) return false;
            handler = c->def.on_player;
            from.local = true;
            from.peer_version = c->def.version;
        }
    }
    if (!loopback) {  // another player's game, over the chat
        if (!server_ || !d_.connection_of) return fail(why, "that player's game has not said hello with this channel");
        auto conn = d_.connection_of(player);
        if (!conn) return fail(why, "that player's game has not said hello with this channel");
        if (!server_.load()->peer_version(conn->first, conn->second, channel))
            for (const auto& peer : server_.load()->peers_with(channel))
                if (peer.account == conn->second) conn = std::make_pair(peer.handle, peer.account);
        return send_remote(owner, conn->first, conn->second, channel, data, why);
    }
    if (handler) d_.post(EMF_WORLD_CLIENT, [handler, from, data](EmfWorld* w) { handler(w, from, data); });
    return true;
}

uint32_t NetService::to_players(const std::string& owner, const std::string& channel, const std::string& data) {
    uint32_t count = 0;
    uint32_t local = 0;
    if (d_.hosted(&local) && to_player(owner, local, channel, data, nullptr)) ++count;
    if (!server_) return count;
    uint32_t version = 0;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!c) return count;
        version = c->def.version;
    }
    for (const auto& peer : server_.load()->peers_with(channel)) {
        if (server_.load()->peer_version(peer.handle, peer.account, channel) != version) continue;  // another version: none
        if (send_remote(owner, peer.handle, peer.account, channel, data, nullptr)) ++count;
    }
    return count;
}

uint32_t NetService::peer(uint32_t player, const std::string& channel) {
    uint32_t version = 0, local = 0;
    bool hosted = false;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!c) return 0;
        version = c->def.version;
        hosted = d_.hosted(&local);
    }
    if (hosted && (player == 0 || player == local)) return version;
    if (player == 0) return client_ ? client_.load()->server_version(channel) : 0;  // a joined game: the server's
    if (!server_ || !d_.connection_of) return 0;
    const auto conn = d_.connection_of(player);
    return conn ? server_.load()->peer_version(conn->first, conn->second, channel) : 0;
}

std::map<std::string, uint32_t> NetService::channel_versions() {
    const std::lock_guard<std::mutex> g(m_);
    std::map<std::string, uint32_t> out;
    for (const auto& [name, c] : channels_) out[name] = c.def.version;
    return out;
}

void NetService::enable_server(ServerEnv env) {
    ServerLinkDeps l;
    l.channels = [this] { return channel_versions(); };
    l.send = std::move(env.send);
    l.send_lines = std::move(env.send_lines);
    l.room = std::move(env.room);
    l.is_local = std::move(env.is_local);
    l.now_ms = d_.now_ms;
    l.log = d_.log;
    l.emf_version = env.emf_version;
    l.api = env.api;
    l.build = env.build;
    l.greet = env.greet;
    l.beacon_delay_ms = env.beacon_delay_ms;
    l.on_message = [this](uint32_t handle, uint64_t account, const std::string& channel, uint32_t peer_version,
                          const std::string& data) { remote_to_server(handle, account, channel, peer_version, data); };
    server_owned_ = std::make_unique<ServerLink>(std::move(l));
    server_.store(server_owned_.get());
}

void NetService::enable_client(ClientEnv env) {
    ClientLinkDeps l;
    l.now_ms = d_.now_ms;
    l.channels = [this] { return channel_versions(); };
    l.self_account = std::move(env.self_account);
    l.enabled = [this] {  // a joined game only: a game that hosts its server delivers in process
        uint32_t id = 0;
        return !d_.hosted(&id);
    };
    l.log = d_.log;
    l.emf_version = env.emf_version;
    l.api = env.api;
    l.build = env.build;
    l.on_message = [this](const std::string& channel, uint32_t version, const std::string& data) {
        remote_to_client(channel, version, data);
    };
    client_owned_ = std::make_unique<ClientLink>(std::move(l));
    client_.store(client_owned_.get());
}

void NetService::remote_to_server(uint32_t handle, uint64_t account, const std::string& channel, uint32_t peer_version,
                                  const std::string& data) {
    NetHandler handler;
    NetFrom from;
    std::string why;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!c || !(c->def.flags & kToServer)) {
            why = "no such channel here that takes messages from players";
        } else if (peer_version != c->def.version) {
            why = "the player's game has " + channel + " at version " + std::to_string(peer_version) +
                  ", the server at " + std::to_string(c->def.version);
        } else if (data.size() > c->def.max_size) {
            why = "a message over the channel's limit of " + std::to_string(c->def.max_size) + " bytes";
        } else {
            auto& rl = c->senders[account];
            if (!rl) rl = std::make_unique<RateLimit>(c->def.per_10s / 10.0, static_cast<double>(c->def.per_10s));
            if (!rl->take(d_.now_ms())) why = "over the channel's rate (" + std::to_string(c->def.per_10s) + " in 10 s)";
        }
        if (why.empty()) {
            handler = c->def.on_server;
            from.account = account;
            from.peer_version = peer_version;
        } else if (!remember_error(logged_, "in " + channel + ": " + why)) {
            why.clear();  // told before
        }
    }
    if (!why.empty()) {
        if (d_.log) d_.log("a message from a player on " + channel + " was dropped: " + why);
        return;
    }
    if (!handler) return;
    from.admin = d_.is_admin ? d_.is_admin(account) : false;
    auto entity_of = d_.entity_of;
    auto connection_of = d_.connection_of;
    d_.post(EMF_WORLD_SERVER, [handler, from, data, entity_of, connection_of, handle](EmfWorld* w) mutable {
        if (entity_of) from.player = entity_of(from.account);  // on the server world's thread
        // Kiln: a queued message belongs to this exact connection, never the account's later reconnect.
        if (connection_of) {
            const auto current = connection_of(from.player);
            if (!current || current->first != handle || current->second != from.account) return;
        }
        handler(w, from, data);
    });
}

void NetService::remote_to_client(const std::string& channel, uint32_t server_version, const std::string& data) {
    NetHandler handler;
    NetFrom from;
    std::string why;
    {
        const std::lock_guard<std::mutex> g(m_);
        Channel* c = find(channel);
        if (!c || !(c->def.flags & kToPlayers)) {
            why = "no such channel here that takes messages from the server";
        } else if (server_version != c->def.version) {
            why = "the server has " + channel + " at version " + std::to_string(server_version) + ", this game at " +
                  std::to_string(c->def.version);
        } else if (data.size() > c->def.max_size) {
            why = "a message over the channel's limit of " + std::to_string(c->def.max_size) + " bytes";
        } else {
            auto& rl = c->senders[~0ull];  // the server's messages to this game
            if (!rl) rl = std::make_unique<RateLimit>(c->def.per_10s / 10.0, static_cast<double>(c->def.per_10s));
            if (!rl->take(d_.now_ms())) why = "over the channel's rate (" + std::to_string(c->def.per_10s) + " in 10 s)";
        }
        if (why.empty()) {
            handler = c->def.on_player;
            from.peer_version = server_version;
        } else if (!remember_error(logged_, "from the server on " + channel + ": " + why)) {
            why.clear();
        }
    }
    if (!why.empty()) {
        if (d_.log) d_.log("a message from the server on " + channel + " was dropped: " + why);
        return;
    }
    if (handler) d_.post(EMF_WORLD_CLIENT, [handler, from, data](EmfWorld* w) { handler(w, from, data); });
}

bool NetService::server_line(uint32_t handle, uint64_t account, std::string_view text) {
    return server_ && server_.load()->on_line(handle, account, text);
}

void NetService::server_tick(const std::vector<Connection>& connected) {
    if (server_) server_.load()->tick(connected);
}

bool NetService::client_line(std::string_view text, uint64_t speaker) {
    return client_ && client_.load()->on_chat_line(text, speaker);
}

std::vector<std::string> NetService::client_take(size_t max) {
    return client_ ? client_.load()->take_lines(max) : std::vector<std::string>{};
}

void NetService::client_put_back(std::vector<std::string> lines) {
    if (client_) client_.load()->put_back(std::move(lines));
}

void NetService::client_reset() {
    if (client_) client_.load()->reset();
}

}  // namespace emf::core::net
