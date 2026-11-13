// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/net/net_line.h.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#pragma once
// Mod messages (API v27): the marked chat line EMF sends between a player's game and the server, and a message split
// into such lines and put back. Pure (unit-tested); docs/research/client_server_messages.md §5.3 (private notes).
//   #!emf1 <type> <seq> <channel>@<ver> <frag>/<count> <enc> <payload>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace emf::core::net {

constexpr size_t kMaxLine = 1024;      // the game's chat line limit (chatClientSend refuses more)
constexpr size_t kMaxFragments = 24;   // so a message is at most about 16 KiB

struct NetLine {
    char type = 'M';       // S server beacon, H hello, W welcome, M message, B bye, E refused
    uint32_t seq = 0;      // per peer and direction; base36 on the wire
    std::string channel;   // "<mod id>/<name>"
    uint32_t version = 1;  // the channel's version, 1..65535
    uint32_t frag = 0, count = 1;
    char enc = 't';        // t: the payload is the text itself; b: base64url
    std::string payload;   // as on the wire
};

// True for a line that starts with the marker and a space.
bool is_marked(std::string_view line);
std::string encode_line(const NetLine& l);
// The line's fields, or nothing and `why` (wrong marker, a bad field, too long).
std::optional<NetLine> parse_line(std::string_view line, std::string* why);

// `data` as the lines of one message: `seq` for the first and one more for each next, each line at most kMaxLine
// bytes. Text when every byte is printable ASCII and no line's payload starts or ends with a space, else base64url.
// Empty and `why` when it needs more than kMaxFragments lines.
std::vector<NetLine> split_message(char type, uint32_t seq, const std::string& channel, uint32_t version,
                                   std::string_view data, std::string* why);
// The message's bytes from its lines (in order); nothing when a base64url payload does not decode.
std::optional<std::string> join_payloads(const std::vector<NetLine>& lines);

std::string base64url_encode(std::string_view data);
std::optional<std::string> base64url_decode(std::string_view text);

}  // namespace emf::core::net
