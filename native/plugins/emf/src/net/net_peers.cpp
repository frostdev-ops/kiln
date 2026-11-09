// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.0, src/core/net/net_peers.cpp.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#include "net_peers.h"

#include <algorithm>

namespace emf::core::net {

std::optional<Reassembly::Message> Reassembly::add(const NetLine& line, std::string* why, uint64_t now_ms) {
    if (now_ms && partial_ms_ && now_ms - partial_ms_ > 30000) partial_.clear();
    const auto drop = [&](const char* r) -> std::optional<Message> {
        if (why) *why = r;
        return std::nullopt;
    };
    if (next_ && line.seq != *next_) {
        // behind the expected number (wrapping counts as behind for a window of 2^31): a line seen already
        const uint32_t ahead = line.seq - *next_;
        if (ahead >= 0x80000000u) return drop("a duplicate line");
        partial_.clear();  // a gap: the message it broke is lost
        next_ = line.seq + 1;
        if (line.frag != 0) return drop("a gap: lines were lost, the message they broke is dropped");
    } else {
        next_ = line.seq + 1;
    }
    if (line.frag == 0) {
        partial_.clear();
    } else if (partial_.empty() || partial_.back().frag + 1 != line.frag || partial_.back().count != line.count ||
               partial_.back().channel != line.channel || partial_.back().version != line.version ||
               partial_.back().type != line.type || partial_.back().enc != line.enc) {
        partial_.clear();
        return drop("a fragment without the ones before it");
    }
    partial_.push_back(line);
    partial_ms_ = now_ms;
    if (line.frag + 1 < line.count) return std::nullopt;
    const auto data = join_payloads(partial_);
    Message m;
    m.type = line.type;
    m.channel = line.channel;
    m.version = line.version;
    partial_.clear();
    if (!data) return drop("a payload that does not decode");
    m.data = *data;
    return m;
}

Peer& PeerTable::hello(uint32_t handle, uint64_t account, std::map<std::string, uint32_t> channels) {
    peers_.erase(std::remove_if(peers_.begin(), peers_.end(),
                                [&](const Peer& p) { return p.handle == handle || p.account == account; }),
                 peers_.end());
    Peer p;
    p.handle = handle;
    p.account = account;
    p.channels = std::move(channels);
    peers_.push_back(std::move(p));
    return peers_.back();
}

void PeerTable::bye(uint32_t handle) {
    peers_.erase(std::remove_if(peers_.begin(), peers_.end(), [&](const Peer& p) { return p.handle == handle; }),
                 peers_.end());
}

Peer* PeerTable::find(uint32_t handle, uint64_t account) {
    for (Peer& p : peers_)
        if (p.handle == handle && p.account == account) return &p;
    return nullptr;
}

std::vector<Peer*> PeerTable::all() {
    std::vector<Peer*> out;
    for (Peer& p : peers_) out.push_back(&p);
    return out;
}

bool RateLimit::take(uint64_t now_ms, double n) {
    if (last_ && now_ms > *last_)
        tokens_ = std::min(burst_, tokens_ + rate_ * static_cast<double>(now_ms - *last_) / 1000.0);
    if (!last_ || now_ms > *last_) last_ = now_ms;
    if (tokens_ + 1e-9 < n) return false;
    tokens_ -= n;
    return true;
}

}  // namespace emf::core::net
