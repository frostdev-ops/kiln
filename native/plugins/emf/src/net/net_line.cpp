// SPDX-License-Identifier: MIT
// From matenm/emf-enshrouded v0.6.8, src/core/net/net_line.cpp.
// Copyright (c) 2026 EMF contributors. See licenses/EMF-LICENSE.txt.
#include "net_line.h"

#include <cstring>

namespace emf::core::net {

namespace {

constexpr std::string_view kMarker = "#!emf1 ";
constexpr char kB64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

std::string base36(uint32_t v) {
    if (v == 0) return "0";
    std::string s;
    while (v) {
        const uint32_t d = v % 36;
        s.insert(s.begin(), static_cast<char>(d < 10 ? '0' + d : 'a' + d - 10));
        v /= 36;
    }
    return s;
}

bool parse_base36(std::string_view s, uint32_t* out) {
    if (s.empty() || s.size() > 7) return false;
    uint64_t v = 0;
    for (const char c : s) {
        uint32_t d;
        if (c >= '0' && c <= '9') d = static_cast<uint32_t>(c - '0');
        else if (c >= 'a' && c <= 'z') d = static_cast<uint32_t>(c - 'a' + 10);
        else return false;
        v = v * 36 + d;
    }
    if (v > 0xFFFFFFFFull) return false;
    *out = static_cast<uint32_t>(v);
    return true;
}

bool parse_uint(std::string_view s, uint32_t max, uint32_t* out) {
    if (s.empty() || s.size() > 6) return false;
    uint32_t v = 0;
    for (const char c : s) {
        if (c < '0' || c > '9') return false;
        v = v * 10 + static_cast<uint32_t>(c - '0');
    }
    if (v > max) return false;
    *out = v;
    return true;
}

bool channel_ok(std::string_view c) {
    const size_t slash = c.find('/');
    if (slash == std::string_view::npos || slash == 0 || slash + 1 >= c.size() || c.size() > 96) return false;
    for (const char x : c)
        if (!((x >= 'a' && x <= 'z') || (x >= 'A' && x <= 'Z') || (x >= '0' && x <= '9') || x == '_' || x == '.' ||
              x == '-' || x == '/'))
            return false;
    return c.find('/', slash + 1) == std::string_view::npos;
}

bool printable(std::string_view d) {
    for (const char c : d)
        if (c < 0x20 || c > 0x7E) return false;
    return true;
}

std::string header(const NetLine& l) {
    return std::string(kMarker) + l.type + ' ' + base36(l.seq) + ' ' + l.channel + '@' + std::to_string(l.version) +
           ' ' + std::to_string(l.frag) + '/' + std::to_string(l.count) + ' ' + l.enc + ' ';
}

// Payload pieces of at most `room` bytes each (base64url pieces are cut at multiples of 4 so each decodes alone).
std::vector<std::string> cut(std::string_view payload, size_t room, bool b64) {
    if (b64) room -= room % 4;
    std::vector<std::string> out;
    for (size_t at = 0; at < payload.size(); at += room) out.emplace_back(payload.substr(at, room));
    if (out.empty()) out.emplace_back();
    return out;
}

}  // namespace

bool is_marked(std::string_view line) {
    return line.size() > kMarker.size() && line.substr(0, kMarker.size()) == kMarker;
}

std::string encode_line(const NetLine& l) { return header(l) + l.payload; }

std::optional<NetLine> parse_line(std::string_view line, std::string* why) {
    const auto fail = [&](const char* r) -> std::optional<NetLine> {
        if (why) *why = r;
        return std::nullopt;
    };
    if (line.size() > kMaxLine) return fail("longer than a chat line");
    if (!is_marked(line)) return fail("not an EMF line (#!emf1)");
    std::string_view rest = line.substr(kMarker.size());
    std::string_view f[5];
    for (int i = 0; i < 5; ++i) {  // five fields, then the payload (the rest of the line, possibly empty)
        const size_t sp = rest.find(' ');
        if (sp == std::string_view::npos) {
            if (i < 4) return fail("fields are missing");
            f[i] = rest;  // an empty payload whose space was trimmed
            rest = {};
            break;
        }
        f[i] = rest.substr(0, sp);
        rest = rest.substr(sp + 1);
    }
    NetLine l;
    if (f[0].size() != 1 || std::strchr("HWMBES", f[0][0]) == nullptr) return fail("unknown type");
    l.type = f[0][0];
    if (!parse_base36(f[1], &l.seq)) return fail("the sequence number is not base36");
    const size_t at = f[2].rfind('@');
    if (at == std::string_view::npos) return fail("no channel version");
    l.channel = std::string(f[2].substr(0, at));
    if (!channel_ok(l.channel)) return fail("the channel is not <mod>/<name>");
    if (!parse_uint(f[2].substr(at + 1), 65535, &l.version) || l.version == 0)
        return fail("the version is not 1..65535");
    const size_t slash = f[3].find('/');
    if (slash == std::string_view::npos || !parse_uint(f[3].substr(0, slash), 1000, &l.frag) ||
        !parse_uint(f[3].substr(slash + 1), 1000, &l.count))
        return fail("the fragment is not <n>/<count>");
    if (l.count == 0 || l.count > kMaxFragments || l.frag >= l.count)
        return fail("the fragment numbers are out of range");
    if (f[4].size() != 1 || (f[4][0] != 't' && f[4][0] != 'b')) return fail("unknown encoding");
    l.enc = f[4][0];
    l.payload = std::string(rest);
    if (l.enc == 'b' && !base64url_decode(l.payload)) return fail("the payload is not base64url");
    if (l.enc == 't' && !printable(l.payload)) return fail("the text is not printable ASCII");
    return l;
}

std::vector<NetLine> split_message(char type, uint32_t seq, const std::string& channel, uint32_t version,
                                   std::string_view data, std::string* why) {
    NetLine probe;
    probe.type = type;
    probe.seq = seq + static_cast<uint32_t>(kMaxFragments);  // the longest seq this message may use
    probe.channel = channel;
    probe.version = version;
    probe.frag = static_cast<uint32_t>(kMaxFragments - 1);
    probe.count = static_cast<uint32_t>(kMaxFragments);
    const size_t head = header(probe).size();
    if (head >= kMaxLine) {
        if (why) *why = "the channel name is too long";
        return {};
    }
    const size_t room = kMaxLine - head;
    bool text = printable(data);
    std::vector<std::string> pieces;
    if (text) {
        pieces = cut(data, room, false);
        for (const auto& p : pieces)
            if (!p.empty() && (p.front() == ' ' || p.back() == ' ')) text = false;
    }
    if (!text) pieces = cut(base64url_encode(data), room, true);
    if (pieces.size() > kMaxFragments) {
        if (why) *why = "the message needs more than 24 lines (about 16 KiB at most)";
        return {};
    }
    std::vector<NetLine> out;
    for (size_t i = 0; i < pieces.size(); ++i) {
        NetLine l;
        l.type = type;
        l.seq = seq + static_cast<uint32_t>(i);
        l.channel = channel;
        l.version = version;
        l.frag = static_cast<uint32_t>(i);
        l.count = static_cast<uint32_t>(pieces.size());
        l.enc = text ? 't' : 'b';
        l.payload = std::move(pieces[i]);
        out.push_back(std::move(l));
    }
    return out;
}

std::optional<std::string> join_payloads(const std::vector<NetLine>& lines) {
    std::string out;
    for (const auto& l : lines) {
        if (l.enc == 't') {
            out += l.payload;
        } else {
            const auto d = base64url_decode(l.payload);
            if (!d) return std::nullopt;
            out += *d;
        }
    }
    return out;
}

std::string base64url_encode(std::string_view data) {
    std::string out;
    size_t i = 0;
    for (; i + 2 < data.size(); i += 3) {
        const uint32_t v = (static_cast<uint8_t>(data[i]) << 16) | (static_cast<uint8_t>(data[i + 1]) << 8) |
                           static_cast<uint8_t>(data[i + 2]);
        out += kB64[(v >> 18) & 63];
        out += kB64[(v >> 12) & 63];
        out += kB64[(v >> 6) & 63];
        out += kB64[v & 63];
    }
    if (i < data.size()) {  // no padding: 2 or 3 characters for the last 1 or 2 bytes
        uint32_t v = static_cast<uint8_t>(data[i]) << 16;
        if (i + 1 < data.size()) v |= static_cast<uint8_t>(data[i + 1]) << 8;
        out += kB64[(v >> 18) & 63];
        out += kB64[(v >> 12) & 63];
        if (i + 1 < data.size()) out += kB64[(v >> 6) & 63];
    }
    return out;
}

std::optional<std::string> base64url_decode(std::string_view text) {
    if (text.size() % 4 == 1) return std::nullopt;
    std::string out;
    uint32_t v = 0;
    int bits = 0;
    for (const char c : text) {
        int d;
        if (c >= 'A' && c <= 'Z') d = c - 'A';
        else if (c >= 'a' && c <= 'z') d = c - 'a' + 26;
        else if (c >= '0' && c <= '9') d = c - '0' + 52;
        else if (c == '-') d = 62;
        else if (c == '_') d = 63;
        else return std::nullopt;
        v = (v << 6) | static_cast<uint32_t>(d);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out += static_cast<char>((v >> bits) & 0xFF);
        }
    }
    return out;
}

}  // namespace emf::core::net
