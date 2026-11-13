// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/net/net_link.cpp.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#include "net_link.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <iterator>

namespace emf::core::net {

namespace {

constexpr uint64_t kNoteEveryMs = 60000;
constexpr uint32_t kDropsBeforeIgnore = 400;  // 10 times the burst within 10 s: that account's lines are ignored
constexpr uint64_t kIgnoreMs = 60000;

std::string clean_version(const std::string& v) {
    std::string out;
    for (const char c : v)
        out += ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '.' || c == '-' ||
                c == '_')
                   ? c
                   : '_';
    return out;
}

bool parse_u32(std::string_view s, uint32_t* out) {
    if (s.empty() || s.size() > 10) return false;
    uint64_t v = 0;
    for (const char c : s) {
        if (c < '0' || c > '9') return false;
        v = v * 10 + static_cast<uint64_t>(c - '0');
    }
    if (v > 0xFFFFFFFFull) return false;
    *out = static_cast<uint32_t>(v);
    return true;
}

std::vector<std::string> encoded(const std::vector<NetLine>& lines) {
    std::vector<std::string> out;
    for (const auto& l : lines) out.push_back(encode_line(l));
    return out;
}

const std::string kControl = kControlChannel;

}  // namespace

std::string channels_text(const std::map<std::string, uint32_t>& channels) {
    std::string out;
    for (const auto& [name, version] : channels) {
        if (!out.empty()) out += ',';
        out += name + '@' + std::to_string(version);
    }
    return out;
}

std::map<std::string, uint32_t> parse_channels_text(std::string_view text) {
    std::map<std::string, uint32_t> out;
    while (!text.empty()) {
        const size_t comma = text.find(',');
        const std::string_view item = text.substr(0, comma);
        text = comma == std::string_view::npos ? std::string_view{} : text.substr(comma + 1);
        const size_t at = item.rfind('@');
        const size_t slash = item.find('/');
        uint32_t v = 0;
        if (at == std::string_view::npos || slash == std::string_view::npos || slash == 0 || slash + 1 >= at ||
            !parse_u32(item.substr(at + 1), &v) || v == 0 || v > 65535)
            continue;
        out[std::string(item.substr(0, at))] = v;
    }
    return out;
}

std::string hello_text(const HelloInfo& h) {
    return "p" + std::to_string(h.protocol) + ';' + clean_version(h.emf_version) + ';' + std::to_string(h.api) + ';' +
           std::to_string(h.build) + ';' + channels_text(h.channels);
}

std::optional<HelloInfo> parse_hello_text(std::string_view text) {
    std::string_view f[5];
    for (int i = 0; i < 5; ++i) {
        const size_t semi = i < 4 ? text.find(';') : std::string_view::npos;
        if (i < 4 && semi == std::string_view::npos) return std::nullopt;
        f[i] = text.substr(0, semi);
        if (i < 4) text = text.substr(semi + 1);
    }
    if (f[0].size() < 2 || f[0][0] != 'p') return std::nullopt;
    HelloInfo h;
    if (!parse_u32(f[0].substr(1), &h.protocol) || !parse_u32(f[2], &h.api) || !parse_u32(f[3], &h.build))
        return std::nullopt;
    h.emf_version = std::string(f[1]);
    h.channels = parse_channels_text(f[4]);
    return h;
}

// ---------------------------------------------------------------------------------------------------------------------
// ServerLink

namespace {
std::string acct_hex(uint64_t account) {
    char h[24];
    std::snprintf(h, sizeof h, "0x%016llx", static_cast<unsigned long long>(account));
    return h;
}
std::string handle_hex(uint32_t handle) {
    char h[16];
    std::snprintf(h, sizeof h, "0x%x", handle);
    return h;
}
}  // namespace

void ServerLink::note(uint64_t account, const std::string& what) {
    // called with m_ held: the log function must not call back in
    const uint64_t now = d_.now_ms ? d_.now_ms() : 0;
    Budget& b = budgets_[account];
    if (b.last_note && now < b.last_note + kNoteEveryMs) return;
    b.last_note = now ? now : 1;
    if (d_.log) d_.log("mod messages from account 0x" + [&] {
        char h[20];
        std::snprintf(h, sizeof h, "%016llx", static_cast<unsigned long long>(account));
        return std::string(h);
    }() + ": " + what + " (logged once a minute per account)");
}

bool ServerLink::queue(uint32_t handle, uint64_t account, const std::vector<std::string>& lines) {
    if (d_.send_lines) return d_.send_lines(handle, account, lines);
    for (const auto& l : lines)
        if (!d_.send || !d_.send(handle, account, l)) return false;
    return true;
}

bool ServerLink::allowed(uint64_t account, size_t bytes) {
    const uint64_t now = d_.now_ms ? d_.now_ms() : 0;
    Budget& b = budgets_[account];
    if (now < b.ignore_until) return false;
    if (b.lines.take(now) && b.bytes.take(now, static_cast<double>(bytes))) return true;
    if (now >= b.window_start + 10000) {
        b.window_start = now;
        b.window_drops = 0;
    }
    if (++b.window_drops >= kDropsBeforeIgnore) {
        b.ignore_until = now + kIgnoreMs;
        b.window_drops = 0;
        note(account, "far over the line rate: its lines are ignored for 60 s");
    } else {
        note(account, "over the line rate (20 lines a second, 16 KiB a second): lines dropped");
    }
    return false;
}

void ServerLink::drop_peer(uint32_t handle, uint64_t account, bool tell) {
    bool had = false;
    {
        const std::lock_guard<std::mutex> g(m_);
        had = peers_.find(handle, account) != nullptr;
        if (had) peers_.bye(handle);
        joining_.erase({handle, account});
    }
    if (had && tell && d_.on_peer) d_.on_peer(handle, account, false);
}

bool ServerLink::on_line(uint32_t handle, uint64_t account, std::string_view text) {
    std::string why;
    std::optional<NetLine> line;
    std::optional<Reassembly::Message> msg;
    uint32_t peer_ver = 0;
    std::vector<Connection> replaced;
    bool joined = false;
    std::string reply;  // an error line for the player
    std::string greeting;  // the log line of a hello answered with a welcome
    {
        const std::lock_guard<std::mutex> g(m_);
        if (!allowed(account, text.size())) return true;
        if (d_.is_local && d_.is_local(handle, account)) {
            note(account, "a marked line from this game's own player is ignored (it is delivered in process)");
            return true;
        }
        line = parse_line(text, &why);
        if (!line) {
            note(account, "a marked line dropped: " + why);
            return true;
        }
        const Connection conn{handle, account};
        switch (line->type) {
            case 'H': {
                if (line->channel != kControl) {
                    note(account, "a hello on the wrong channel");
                    return true;
                }
                msg = joining_[conn].in.add(*line, &why, d_.now_ms ? d_.now_ms() : 0);
                if (!msg) {
                    if (!why.empty()) note(account, "a hello line dropped: " + why);
                    return true;
                }
                break;  // whole: handled below, outside the lock
            }
            case 'M': {
                Peer* p = peers_.find(handle, account);
                if (!p) {
                    note(account, "a message before a hello: dropped");
                    return true;
                }
                msg = p->in.add(*line, &why, d_.now_ms ? d_.now_ms() : 0);
                if (!msg) {
                    if (!why.empty()) note(account, "a line dropped: " + why);
                    return true;
                }
                const auto it = p->channels.find(msg->channel);
                if (msg->channel == kControl || it == p->channels.end()) {
                    note(account, "a message on " + msg->channel + ", which its hello did not name: dropped");
                    return true;
                }
                if (it->second != msg->version) {
                    note(account, "a message on " + msg->channel + " at version " + std::to_string(msg->version) +
                                      ", its hello said " + std::to_string(it->second) + ": dropped");
                    return true;
                }
                peer_ver = it->second;
                break;
            }
            case 'B':
                break;
            default:
                note(account, std::string("a '") + line->type + "' line is the server's, not a player's: dropped");
                return true;
        }
        if (line->type == 'H') {
            auto hello = parse_hello_text(msg->data);
            Reassembly in = std::move(joining_[conn].in);
            joining_.erase(conn);
            if (!hello || hello->protocol != kProtocol) {
                note(account, "a hello this server does not understand (protocol " +
                                  (hello ? std::to_string(hello->protocol) : std::string("?")) + ")");
                const auto lines = split_message('E', 0, kControl, kControlVersion, "unsupported protocol", nullptr);
                reply = lines.empty() ? std::string() : encode_line(lines.front());
                msg.reset();
            } else {
                uint32_t keep_seq = 0;  // the same connection saying hello again: its numbers go on
                if (Peer* same = peers_.find(handle, account)) keep_seq = same->peek_seq();
                for (Peer* old : peers_.all())
                    if (old->account == account && old->handle != handle) replaced.push_back({old->handle, old->account});
                Peer& p = peers_.hello(handle, account, hello->channels);
                p.in = std::move(in);
                p.reserve(keep_seq);
                HelloInfo mine;
                mine.emf_version = d_.emf_version;
                mine.api = d_.api;
                mine.build = d_.build;
                if (d_.channels) mine.channels = d_.channels();
                const auto lines =
                    split_message('W', p.peek_seq(), kControl, kControlVersion, hello_text(mine), nullptr);
                if (queue(handle, account, encoded(lines))) {
                    p.reserve(static_cast<uint32_t>(lines.size()));
                    joined = true;
                } else {  // no welcome went: not a peer, so the beacon comes again
                    peers_.bye(handle);
                    note(account, "the welcome could not be queued (the chat outbox is full); the beacon comes again");
                }
                if (joined) {
                    greeting = "mod messages: hello from account " + acct_hex(account) + " on connection " +
                               handle_hex(handle) + " (EMF " + clean_version(hello->emf_version) + ", API " +
                               std::to_string(hello->api) + ", " + std::to_string(hello->channels.size()) +
                               " channel(s)); welcome sent";
                }
                msg.reset();
            }
        }
    }
    if (!greeting.empty() && d_.log) d_.log(greeting);
    if (!reply.empty() && d_.send) d_.send(handle, account, reply);
    for (const auto& r : replaced)
        if (d_.on_peer) d_.on_peer(r.handle, r.account, false);
    if (joined && d_.on_peer) d_.on_peer(handle, account, true);
    if (line && line->type == 'B') drop_peer(handle, account, true);
    if (msg && line && line->type == 'M' && d_.on_message) d_.on_message(handle, account, msg->channel, peer_ver, msg->data);
    return true;
}

void ServerLink::tick(const std::vector<Connection>& connected) {
    std::set<Connection> now;
    for (const auto& c : connected)
        if (!(d_.is_local && d_.is_local(c.handle, c.account))) now.insert(c);
    std::vector<Connection> left, fresh;
    std::vector<uint32_t> tries;  // which beacon each fresh one gets (1 = the first)
    const uint64_t ms = d_.now_ms ? d_.now_ms() : 0;
    {
        const std::lock_guard<std::mutex> g(m_);
        for (auto it = greeted_.begin(); it != greeted_.end();)
            it = now.count(it->first) ? std::next(it) : greeted_.erase(it);
        for (auto it = joining_.begin(); it != joining_.end();)
            it = now.count(it->first) ? std::next(it) : joining_.erase(it);
        for (Peer* p : peers_.all())
            if (!now.count({p->handle, p->account})) left.push_back({p->handle, p->account});
        for (const auto& c : left) peers_.bye(c.handle);
        if (d_.greet && (d_.greets ? d_.greets() : d_.channels && !d_.channels().empty()))
            for (const auto& c : now) {
                Beaconing& b = greeted_.try_emplace(c, Beaconing{ms, 0, 0}).first->second;
                // done, said hello, or a hello is coming in (another beacon would restart the conversation)
                if (b.sent >= d_.beacon_tries || peers_.find(c.handle, c.account) || joining_.count(c)) continue;
                const bool due = b.sent == 0 ? ms >= b.seen + d_.beacon_delay_ms : ms >= b.last + d_.beacon_retry_ms;
                if (!due) continue;
                b.last = ms;
                ++b.sent;
                fresh.push_back(c);
                tries.push_back(b.sent);
            }
        // an account with no connection any more starts over with a full budget
        for (auto it = budgets_.begin(); it != budgets_.end();) {
            bool seen = false;
            for (const auto& c : now) seen |= c.account == it->first;
            it = seen ? std::next(it) : budgets_.erase(it);
        }
    }
    const std::string beacon = [&] {
        const auto lines = split_message('S', 0, kControl, kControlVersion, kBeaconText, nullptr);
        return lines.empty() ? std::string() : encode_line(lines.front());
    }();
    for (size_t i = 0; i < fresh.size(); ++i) {
        const Connection& c = fresh[i];
        const bool sent = d_.send && !beacon.empty() && d_.send(c.handle, c.account, beacon);
        if (d_.log)
            d_.log("mod messages: the beacon to the player on connection " + handle_hex(c.handle) + " (account " +
                   acct_hex(c.account) + ")" +
                   (tries[i] > 1 ? ", again (" + std::to_string(tries[i]) + " of " + std::to_string(d_.beacon_tries) + "): no hello yet"
                                 : std::string()) +
                   (sent ? "" : ": it could not be queued"));
    }
    for (const auto& c : left)
        if (d_.on_peer) d_.on_peer(c.handle, c.account, false);
}

bool ServerLink::send(uint32_t handle, uint64_t account, const std::string& channel, uint32_t version,
                      const std::string& data, std::string* why) {
    const auto fail = [&](const std::string& r) {
        if (why) *why = r;
        return false;
    };
    std::vector<std::string> out;
    {
        const std::lock_guard<std::mutex> g(m_);
        Peer* p = peers_.find(handle, account);
        if (!p) return fail("that player's game has not said hello");
        const auto it = p->channels.find(channel);
        if (it == p->channels.end()) return fail("that player's game has no channel " + channel);
        if (it->second != version)
            return fail("that player's game has " + channel + " at version " + std::to_string(it->second) +
                        ", the server at " + std::to_string(version));
        std::string split_why;
        const auto lines = split_message('M', p->peek_seq(), channel, version, data, &split_why);
        if (lines.empty()) return fail(split_why);
        if (d_.room && d_.room() < lines.size()) return fail("the chat outbox is full");
        const uint64_t now = d_.now_ms ? d_.now_ms() : 0;
        if (!budgets_[account].out.take(now, static_cast<double>(lines.size())))
            return fail("over the line rate to that player (20 lines a second)");
        out = encoded(lines);
        // queued under the lock: the numbers are used only when the whole message went in, in order
        if (!queue(handle, account, out)) return fail("the chat outbox refused the message");
        p->reserve(static_cast<uint32_t>(lines.size()));
    }
    return true;
}

uint32_t ServerLink::peer_version(uint32_t handle, uint64_t account, const std::string& channel) {
    const std::lock_guard<std::mutex> g(m_);
    Peer* p = peers_.find(handle, account);
    if (!p) return 0;
    const auto it = p->channels.find(channel);
    return it == p->channels.end() ? 0 : it->second;
}

std::vector<Connection> ServerLink::peers() {
    const std::lock_guard<std::mutex> g(m_);
    std::vector<Connection> out;
    for (Peer* p : peers_.all()) out.push_back({p->handle, p->account});
    return out;
}

std::vector<Connection> ServerLink::peers_with(const std::string& channel) {
    const std::lock_guard<std::mutex> g(m_);
    std::vector<Connection> out;
    for (Peer* p : peers_.all())
        if (p->channels.count(channel)) out.push_back({p->handle, p->account});
    return out;
}

// ---------------------------------------------------------------------------------------------------------------------
// ClientLink

bool ClientLink::on_chat_line(std::string_view text, uint64_t speaker) {
    if (!is_marked(text)) return false;
    std::string why;
    const auto line = parse_line(text, &why);
    if (!line) {
        if (d_.log) d_.log("mod messages: a marked chat line dropped: " + why);
        return true;
    }
    if (d_.enabled && !d_.enabled()) return true;  // hosted or offline: hidden, never answered
    std::optional<Reassembly::Message> msg;
    bool up = false, down = false;
    std::string told;  // a log line, written after the lock
    bool foreign = false;
    {
        const std::lock_guard<std::mutex> g(m_);
        // only lines the server speaks as this very player count: until the game says who that is, none does (a
        // beacon another player typed would otherwise make this game talk to a server without EMF)
        const std::optional<uint64_t> self = d_.self_account ? d_.self_account() : std::nullopt;
        if (!self) {
            foreign = true;
            if (line->type == 'S') held_beacon_ = speaker;  // answered once the game knows (when it is this player)
            if (!told_no_self_) {
                told_no_self_ = true;
                told = "mod messages: this game does not know its own account yet: a marked chat line (speaker " +
                       acct_hex(speaker) + ") is hidden; a beacon is answered once it knows (logged once)";
            }
        } else if (speaker != *self) {  // spoken by someone else: a forged line (never shown)
            foreign = true;
            if (!told_foreign_) {
                told_foreign_ = true;
                told = "mod messages: a marked chat line not spoken as this player was dropped (speaker " +
                       acct_hex(speaker) + ", this player " + acct_hex(*self) + "; logged once)";
            }
        } else switch (line->type) {
            case 'S':
                held_beacon_.reset();  // this one answers
                told = beacon_locked(down);
                break;
            case 'W': {
                if (state_ == State::Idle || line->channel != kControl) break;
                msg = in_.add(*line, &why, d_.now_ms ? d_.now_ms() : 0);
                if (!msg) break;
                const auto hello = parse_hello_text(msg->data);
                msg.reset();
                if (!hello || hello->protocol != kProtocol) {
                    if (d_.log) d_.log("mod messages: the server's welcome is not protocol 1: not used");
                    break;
                }
                server_channels_ = hello->channels;
                const bool was = state_ == State::Welcomed;
                state_ = State::Welcomed;
                up = !was;
                told = "mod messages: the server welcomed this game (EMF " + clean_version(hello->emf_version) +
                       ", " + std::to_string(server_channels_.size()) + " channel(s))";
                break;
            }
            case 'M': {
                if (state_ != State::Welcomed) break;
                msg = in_.add(*line, &why, d_.now_ms ? d_.now_ms() : 0);
                if (!msg) break;
                const auto it = server_channels_.find(msg->channel);
                if (msg->channel == kControl || it == server_channels_.end() || it->second != msg->version) msg.reset();
                break;
            }
            case 'B':
                down = state_ == State::Welcomed;
                state_ = State::Idle;
                server_channels_.clear();
                break;
            case 'E':
                if (d_.log) d_.log("mod messages: the server refused this game: " + line->payload);
                break;
            default:
                break;  // an 'H' is the player's, not the server's
        }
    }
    if (!told.empty() && d_.log) d_.log(told);
    if (foreign) return true;
    if (up && d_.on_server) d_.on_server(true);
    if (down && d_.on_server) d_.on_server(false);
    if (msg && d_.on_message) d_.on_message(msg->channel, msg->version, msg->data);
    return true;
}

std::string ClientLink::beacon_locked(bool& down) {
    const bool was = state_ == State::Welcomed;
    state_ = State::Idle;
    server_channels_.clear();
    in_ = Reassembly();
    out_.clear();
    out_seq_ = 0;
    down = was;
    HelloInfo mine;
    mine.emf_version = d_.emf_version;
    mine.api = d_.api;
    mine.build = d_.build;
    if (d_.channels) mine.channels = d_.channels();
    if (mine.channels.empty())  // no mod of this game talks to a server
        return "mod messages: the server's beacon (hidden); no mod of this game has a channel: no hello";
    const auto lines = split_message('H', out_seq_, kControl, kControlVersion, hello_text(mine), nullptr);
    out_seq_ += static_cast<uint32_t>(lines.size());
    for (const auto& l : encoded(lines)) out_.push_back(l);
    state_ = State::HelloSent;
    return "mod messages: the server's beacon (hidden); hello queued with " + std::to_string(mine.channels.size()) +
           " channel(s)";
}

void ClientLink::answer_held() {
    std::string told;
    bool down = false;
    {
        const std::lock_guard<std::mutex> g(m_);
        if (!held_beacon_) return;
        if (d_.enabled && !d_.enabled()) {  // hosted or offline now: never answered
            held_beacon_.reset();
            return;
        }
        const std::optional<uint64_t> self = d_.self_account ? d_.self_account() : std::nullopt;
        if (!self) return;  // still not known
        const uint64_t speaker = *held_beacon_;
        held_beacon_.reset();
        if (speaker != *self) {  // spoken by someone else: a forged beacon
            if (!told_foreign_) {
                told_foreign_ = true;
                told = "mod messages: a marked chat line not spoken as this player was dropped (speaker " +
                       acct_hex(speaker) + ", this player " + acct_hex(*self) + "; logged once)";
            }
        } else {
            told = beacon_locked(down) + " (the beacon came before this game knew its own account)";
        }
    }
    if (!told.empty() && d_.log) d_.log(told);
    if (down && d_.on_server) d_.on_server(false);
}

bool ClientLink::send(const std::string& channel, uint32_t version, const std::string& data, std::string* why) {
    const auto fail = [&](const std::string& r) {
        if (why) *why = r;
        return false;
    };
    const std::lock_guard<std::mutex> g(m_);
    if (state_ != State::Welcomed) return fail("no server has welcomed this game yet");
    const auto it = server_channels_.find(channel);
    if (it == server_channels_.end()) return fail("the server has no channel " + channel);
    if (it->second != version)
        return fail("the server has " + channel + " at version " + std::to_string(it->second) + ", this game at " +
                    std::to_string(version));
    std::string split_why;
    const auto lines = split_message('M', out_seq_, channel, version, data, &split_why);
    if (lines.empty()) return fail(split_why);
    if (out_.size() + lines.size() > kClientOutboxLines) return fail("too many messages are waiting to be sent");
    out_seq_ += static_cast<uint32_t>(lines.size());
    for (const auto& l : encoded(lines)) out_.push_back(l);
    return true;
}

std::vector<std::string> ClientLink::take_lines(size_t max) {
    answer_held();
    const std::lock_guard<std::mutex> g(m_);
    const size_t n = std::min(max, out_.size());
    std::vector<std::string> out(out_.begin(), out_.begin() + static_cast<std::ptrdiff_t>(n));
    out_.erase(out_.begin(), out_.begin() + static_cast<std::ptrdiff_t>(n));
    return out;
}

void ClientLink::put_back(std::vector<std::string> lines) {
    const std::lock_guard<std::mutex> g(m_);
    out_.insert(out_.begin(), std::make_move_iterator(lines.begin()), std::make_move_iterator(lines.end()));
}

size_t ClientLink::waiting() {
    answer_held();
    const std::lock_guard<std::mutex> g(m_);
    return out_.size();
}

bool ClientLink::welcomed() {
    const std::lock_guard<std::mutex> g(m_);
    return state_ == State::Welcomed;
}

uint32_t ClientLink::server_version(const std::string& channel) {
    const std::lock_guard<std::mutex> g(m_);
    if (state_ != State::Welcomed) return 0;
    const auto it = server_channels_.find(channel);
    return it == server_channels_.end() ? 0 : it->second;
}

void ClientLink::reset(bool keep_held_beacon) {
    bool down = false;
    bool dropped = false;
    {
        const std::lock_guard<std::mutex> g(m_);
        down = state_ == State::Welcomed;
        state_ = State::Idle;
        server_channels_.clear();
        in_ = Reassembly();
        out_.clear();
        out_seq_ = 0;
        told_foreign_ = false;
        told_no_self_ = false;
        if (!keep_held_beacon) {
            dropped = held_beacon_.has_value();
            held_beacon_.reset();
        }
    }
    if (dropped && d_.log) d_.log("mod messages: a held beacon was dropped (a new connection before this game knew its own account)");
    if (down && d_.on_server) d_.on_server(false);
}

}  // namespace emf::core::net
