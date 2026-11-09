// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.0, src/core/net/net_peers.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// Mod messages (API v27): lines back into messages per peer and direction, the server's peers, rate limits. Pure
// (unit-tested).
#include <cstddef>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "net_line.h"

namespace emf::core::net {

// The lines of one peer and direction back into messages. Lines come numbered in sequence; a message is whole when its
// last fragment arrived after all the others. A duplicate is dropped; a gap (a lost line) drops the message it broke,
// and the next message that starts with fragment 0 is read again.
class Reassembly {
public:
    struct Message {
        char type = 'M';
        std::string channel;
        uint32_t version = 1;
        std::string data;
    };
    // `line` in; the message it completed, if any; `why` says why a line was dropped.
    std::optional<Message> add(const NetLine& line, std::string* why, uint64_t now_ms = 0);

private:
    std::optional<uint32_t> next_;  // the sequence number expected next
    std::vector<NetLine> partial_;
    uint64_t partial_ms_ = 0;  // Kiln: stale fragments cannot be completed after a long pause
};

// A player's game that said hello (server side): its connection handle and account (the server's own records), the
// channels it has, its lines coming in, the numbers of the lines going to it.
struct Peer {
    uint32_t handle = 0;
    uint64_t account = 0;
    std::map<std::string, uint32_t> channels;  // name -> version
    Reassembly in;
    uint32_t next_seq() { return out_seq_++; }
    uint32_t peek_seq() const { return out_seq_; }
    void reserve(uint32_t lines) { out_seq_ += lines; }  // the numbers of a message just split

private:
    uint32_t out_seq_ = 0;
};

class PeerTable {
public:
    // A hello from (handle, account): its peer, new; a peer of the same account on another handle goes (it
    // reconnected).
    Peer& hello(uint32_t handle, uint64_t account, std::map<std::string, uint32_t> channels);
    void bye(uint32_t handle);
    // The peer on `handle` whose account is `account`; null when none (a reused handle is another player).
    Peer* find(uint32_t handle, uint64_t account);
    size_t size() const { return peers_.size(); }
    std::vector<Peer*> all();

private:
    std::vector<Peer> peers_;
};

// A token bucket: `per_s` units a second, at most `burst` at once.
class RateLimit {
public:
    RateLimit(double per_s, double burst) : rate_(per_s), burst_(burst), tokens_(burst) {}
    bool take(uint64_t now_ms, double n = 1.0);

private:
    double rate_, burst_, tokens_;
    std::optional<uint64_t> last_;
};

}  // namespace emf::core::net
