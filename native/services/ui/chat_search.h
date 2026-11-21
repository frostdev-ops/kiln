// SPDX-License-Identifier: MIT
// Issue #44 Phase 1c: client-only Ctrl+R search of the already-filtered native chat history.
// Portable model: no game pointers, Windows, network or disk access.
#pragma once
#include "chat_history.h"
#include <algorithm>
#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace kui::chat_search {
inline unsigned char ascii_fold(unsigned char c) {
    return c >= 'A' && c <= 'Z' ? static_cast<unsigned char>(c + ('a' - 'A')) : c;
}

// UTF-8 input is validated by History::valid at insertion and by append_utf16 here.
// ASCII-only folding never changes UTF-8 continuation bytes or matches a split codepoint.
inline bool contains(std::string_view text, std::string_view query) {
    if (query.empty()) return true;
    if (query.size() > text.size()) return false;
    for (size_t i = 0; i + query.size() <= text.size(); ++i) {
        bool equal = true;
        for (size_t j = 0; j < query.size(); ++j)
            if (ascii_fold(static_cast<unsigned char>(text[i+j])) !=
                ascii_fold(static_cast<unsigned char>(query[j]))) { equal = false; break; }
        if (equal) return true;
    }
    return false;
}

class Search {
public:
    void begin(const std::vector<chat_history::Entry>& entries) {
        active_ = true;
        query_.clear();
        high_surrogate_ = 0;
        selected_ = 0;
        rebuild(entries);
    }
    void cancel() {
        ++generation_; // invalidate snapshots and mouse clicks from this session
        active_ = false;
        query_.clear();
        matches_.clear();
        high_surrogate_ = 0;
        selected_ = 0;
    }
    bool active() const { return active_; }
    uint32_t generation() const { return generation_; }
    // The rendered row index is only valid for the exact result list shown.
    bool select_at_generation(size_t index, uint32_t generation) {
        return active_ && generation == generation_ && select(index);
    }
    std::string_view query() const { return query_; }
    const std::vector<std::string>& matches() const { return matches_; }
    size_t selected() const { return selected_; }
    const std::string* match(size_t index) const {
        return index < matches_.size() ? &matches_[index] : nullptr;
    }
    const std::string* chosen() const { return match(selected_); }
    bool select(size_t index) {
        if (index >= matches_.size()) return false;
        selected_ = index;
        return true;
    }
    void older() {
        if (!matches_.empty()) selected_ = (selected_ + 1) % matches_.size();
    }
    void newer() {
        if (!matches_.empty()) selected_ = (selected_ + matches_.size() - 1) % matches_.size();
    }
    bool backspace(const std::vector<chat_history::Entry>& entries) {
        high_surrogate_ = 0;
        if (query_.empty()) return false;
        size_t n = query_.size() - 1;
        while (n > 0 && (static_cast<unsigned char>(query_[n]) & 0xc0) == 0x80) --n;
        query_.resize(n);
        rebuild(entries);
        return true;
    }
    // WM_CHAR is a UTF-16 code unit. Only complete scalar values are added to the query.
    // A dangling high surrogate is never turned into malformed UTF-8.
    bool append_utf16(uint32_t unit, const std::vector<chat_history::Entry>& entries) {
        if (!active_ || unit > 0x10ffff) return false;
        uint32_t cp = unit;
        if (unit >= 0xd800 && unit <= 0xdbff) {
            high_surrogate_ = static_cast<uint16_t>(unit);
            return false;
        }
        if (unit >= 0xdc00 && unit <= 0xdfff) {
            if (!high_surrogate_) return false;
            cp = 0x10000 + (static_cast<uint32_t>(high_surrogate_ - 0xd800) << 10) + (unit - 0xdc00);
        } else if (unit < 0x20 || unit == 0x7f) {
            high_surrogate_ = 0;
            return false;
        }
        high_surrogate_ = 0;
        char bytes[4]{};
        size_t n = 0;
        if (cp < 0x80) bytes[n++] = static_cast<char>(cp);
        else if (cp < 0x800) {
            bytes[n++] = static_cast<char>(0xc0 | (cp >> 6));
            bytes[n++] = static_cast<char>(0x80 | (cp & 0x3f));
        } else if (cp < 0x10000) {
            bytes[n++] = static_cast<char>(0xe0 | (cp >> 12));
            bytes[n++] = static_cast<char>(0x80 | ((cp >> 6) & 0x3f));
            bytes[n++] = static_cast<char>(0x80 | (cp & 0x3f));
        } else {
            bytes[n++] = static_cast<char>(0xf0 | (cp >> 18));
            bytes[n++] = static_cast<char>(0x80 | ((cp >> 12) & 0x3f));
            bytes[n++] = static_cast<char>(0x80 | ((cp >> 6) & 0x3f));
            bytes[n++] = static_cast<char>(0x80 | (cp & 0x3f));
        }
        if (query_.size() + n > 256) return false;
        query_.append(bytes, n);
        rebuild(entries);
        return true;
    }
private:
    void rebuild(const std::vector<chat_history::Entry>& entries) {
        ++generation_; // begin/query edit rebuilds the result list
        matches_.clear();
        std::unordered_set<std::string> seen;
        for (auto it = entries.rbegin(); it != entries.rend(); ++it) {
            if (contains(it->line, query_) && seen.insert(it->line).second)
                matches_.push_back(it->line);
        }
        selected_ = 0;
    }
    bool active_ = false;
    uint32_t generation_ = 0;
    std::string query_;
    std::vector<std::string> matches_;
    size_t selected_ = 0;
    uint16_t high_surrogate_ = 0;
};
} // namespace kui::chat_search
