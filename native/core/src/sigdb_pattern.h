// SPDX-License-Identifier: MIT
// sigdb patterns: parsing, scanning and hole values. Platform independent (the host tests build it as is); sigdb.h
// re-exports it as kiln::sigdb::{Hole, Pattern, parse_pattern, scan, hole_value}.
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace kiln::sigdb {

struct Hole {
    std::string name;
    enum Kind { U8, U16, U32, S32, Rel32 } kind;
    size_t offset;
    size_t width;
};
struct Pattern {
    std::vector<int> bytes;  // -1 = any byte
    std::vector<Hole> holes;
    size_t anchor_offset = 0, anchor_length = 0;
};

namespace pattern_detail {
inline int hexval(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}
}  // namespace pattern_detail

inline bool parse_pattern(std::string_view text, Pattern& out, std::string& error) {
    using pattern_detail::hexval;
    out = Pattern{};
    size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() && (text[i] == ' ' || text[i] == '\t')) ++i;
        if (i >= text.size()) break;
        if (text[i] == '{') {
            const size_t close = text.find('}', i);
            if (close == std::string_view::npos) {
                error = "unterminated hole";
                return false;
            }
            const std::string_view body = text.substr(i + 1, close - i - 1);
            const size_t colon = body.rfind(':');
            if (colon == std::string_view::npos || colon == 0) {
                error = "hole needs name:type";
                return false;
            }
            Hole h;
            h.name = std::string(body.substr(0, colon));
            const std::string_view kind = body.substr(colon + 1);
            if (kind == "d8") h.kind = Hole::U8, h.width = 1;
            else if (kind == "d16") h.kind = Hole::U16, h.width = 2;
            else if (kind == "d32") h.kind = Hole::U32, h.width = 4;
            else if (kind == "s32") h.kind = Hole::S32, h.width = 4;
            else if (kind == "rel32") h.kind = Hole::Rel32, h.width = 4;
            else {
                error = "unknown hole type '" + std::string(kind) + "'";
                return false;
            }
            h.offset = out.bytes.size();
            out.bytes.insert(out.bytes.end(), h.width, -1);
            out.holes.push_back(std::move(h));
            i = close + 1;
            continue;
        }
        if (text[i] == '?') {
            out.bytes.push_back(-1);
            i += (i + 1 < text.size() && text[i + 1] == '?') ? 2 : 1;
            continue;
        }
        const int hi = hexval(text[i]);
        const int lo = i + 1 < text.size() ? hexval(text[i + 1]) : -1;
        if (hi < 0 || lo < 0) {
            error = "bad byte at column " + std::to_string(i);
            return false;
        }
        out.bytes.push_back(hi * 16 + lo);
        i += 2;
    }
    // Anchor: the longest run of literal bytes.
    size_t best = 0, best_off = 0, run = 0;
    for (size_t k = 0; k <= out.bytes.size(); ++k) {
        if (k < out.bytes.size() && out.bytes[k] >= 0) {
            ++run;
            continue;
        }
        if (run > best) best = run, best_off = k - run;
        run = 0;
    }
    if (best == 0) {
        error = "pattern has no literal bytes";
        return false;
    }
    out.anchor_offset = best_off;
    out.anchor_length = best;
    return true;
}

// Matches of `p` that lie wholly in [begin, end), in address order, at most `limit`.
inline std::vector<const uint8_t*> scan(const Pattern& p, const uint8_t* begin, const uint8_t* end, size_t limit) {
    std::vector<const uint8_t*> hits;
    const size_t n = p.bytes.size();
    if (!begin || end <= begin || static_cast<size_t>(end - begin) < n) return hits;
    std::vector<uint8_t> anchor(p.anchor_length);
    for (size_t k = 0; k < p.anchor_length; ++k) anchor[k] = static_cast<uint8_t>(p.bytes[p.anchor_offset + k]);
    const std::boyer_moore_horspool_searcher searcher(anchor.begin(), anchor.end());
    const uint8_t* search_begin = begin + p.anchor_offset;
    const uint8_t* search_end = end - n + p.anchor_offset + p.anchor_length;
    while (search_begin < search_end) {
        const auto [first, last] = searcher(search_begin, search_end);
        if (first == search_end) break;
        const uint8_t* start = first - p.anchor_offset;
        bool ok = true;
        for (size_t k = 0; k < n; ++k) {
            if (p.bytes[k] >= 0 && start[k] != static_cast<uint8_t>(p.bytes[k])) {
                ok = false;
                break;
            }
        }
        if (ok) {
            hits.push_back(start);
            if (hits.size() >= limit) break;
        }
        search_begin = first + 1;
    }
    return hits;
}

// Value of hole `name` in a match of `p` at `match` (rel32: the absolute target). False when there is no such hole.
// `live_delta` is added to `match` to get the live address of the matched bytes: 0 when `match` points into the live
// code, CodeView::delta() when it points into the file's copy (only rel32 targets depend on where the bytes are).
inline bool hole_value(const Pattern& p, const uint8_t* match, std::string_view name, int64_t* out,
                       intptr_t live_delta = 0) {
    for (const Hole& h : p.holes) {
        if (h.name != name) continue;
        const uint8_t* at = match + h.offset;
        int64_t v = 0;
        switch (h.kind) {
            case Hole::U8: v = at[0]; break;
            case Hole::U16: { uint16_t x; std::memcpy(&x, at, 2); v = x; } break;
            case Hole::U32: { uint32_t x; std::memcpy(&x, at, 4); v = x; } break;
            case Hole::S32: { int32_t x; std::memcpy(&x, at, 4); v = x; } break;
            case Hole::Rel32: {
                int32_t x;
                std::memcpy(&x, at, 4);
                v = static_cast<int64_t>(reinterpret_cast<uintptr_t>(at + 4) + static_cast<uintptr_t>(live_delta) +
                                         static_cast<uintptr_t>(static_cast<intptr_t>(x)));
            } break;
        }
        if (out) *out = v;
        return true;
    }
    return false;
}

}  // namespace kiln::sigdb
