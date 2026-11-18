// SPDX-License-Identifier: MIT
// Issue #44. Portable client-side history, independent of game memory and Windows.
#pragma once
#include <algorithm>
#include <cstddef>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace kui::chat_history {

struct Entry {
    std::string line;
    bool persisted = false;
};

// TextInputProcess synchronizes the caller-owned widget after its call.
// The widget snapshot BEFORE the next call can be stale or empty even when
// the player has typed a nonempty draft. Cache a validated, post-call
// snapshot while not browsing and use it for the first Up capture only.
// Each chat field owns a separate cache; clear it at submit/close/focus exit.
class DraftCapture {
public:
    void observe(std::string_view postcall) {
        if (postcall.size() > 1024) {
            clear();
            return;
        }
        value_.assign(postcall);
        valid_ = true;
    }
    void clear() { valid_ = false; value_.clear(); }
    bool valid() const { return valid_; }
    std::string_view source(std::string_view precall) const {
        return valid_ ? std::string_view(value_) : precall;
    }
private:
    bool valid_ = false;
    std::string value_;
};

// A native chat widget can report a frame-old text snapshot before the
// game's TextInputProcess synchronizes it. While browsing, the last text
// successfully applied through EditSetText is our known displayed value.
// Compare a proposed recall against that value, not the stale snapshot.
// Without browsing, the live widget value remains the source of truth.
inline bool needs_replacement(std::string_view next, std::string_view widget,
                              std::string_view last_applied, bool browsing_before) {
    const std::string_view shown =
        browsing_before && !last_applied.empty() ? last_applied : widget;
    return next != shown;
}

// One UI thread owns History. Window-thread keys are passed through chat_keys::Router.
class History {
public:
    explicit History(std::size_t limit = 200) : limit_(std::clamp<std::size_t>(limit, 1, 2000)) {}

    // Persisted entries, from kiln/data/kiln.ui/chat-history.json (chat_store.h).
    void load(const std::vector<std::string>& lines) {
        entries_.clear();
        edited();
        for (const auto& line : lines) {
            if (!valid(line) || line.front() != '/' || sensitive(line)) continue;
            if (!entries_.empty() && entries_.back().line == line) continue;
            entries_.push_back({line, true});
            trim();
        }
    }

    // Only call on an actual client chat submission, never on incoming/protocol lines.
    // Sensitive lines are excluded from both memory and disk. Plain chat is session-only.
    // Returns true if the persisted list changed and needs saving.
    bool submit(std::string_view line) {
        edited();
        if (!valid(line) || sensitive(line)) return false;
        const bool command = line.front() == '/';
        if (!entries_.empty() && entries_.back().line == line) return false;
        entries_.push_back({std::string(line), command});
        trim();
        return command || persisted_removed_;
    }

    std::string older(std::string_view live) {
        if (entries_.empty()) return std::string(live);
        if (cursor_ < 0) {
            draft_ = live;
            cursor_ = static_cast<int>(entries_.size()) - 1;
        } else if (cursor_ > 0) {
            --cursor_;
        }
        return entries_[static_cast<std::size_t>(cursor_)].line;
    }

    std::string newer(std::string_view live) {
        if (cursor_ < 0) return std::string(live);
        if (++cursor_ >= static_cast<int>(entries_.size())) {
            cursor_ = -1;
            return draft_;
        }
        return entries_[static_cast<std::size_t>(cursor_)].line;
    }

    void edited() {
        cursor_ = -1;
        draft_.clear();
    }

    std::vector<std::string> persisted() const {
        std::vector<std::string> out;
        for (const auto& e : entries_)
            if (e.persisted) out.push_back(e.line);
        return out;
    }

    std::size_t count() const { return entries_.size(); }
    bool browsing() const { return cursor_ >= 0; }

    // Reject control bytes and malformed UTF-8, to keep the game and JSON safe.
    static bool valid(std::string_view s) {
        if (s.empty() || s.size() > 1024) return false;
        bool any = false;
        for (std::size_t i = 0; i < s.size();) {
            const unsigned char a = static_cast<unsigned char>(s[i]);
            if (a < 0x20 || a == 0x7f) return false;
            if (a < 0x80) {
                if (a != ' ' && a != '\t') any = true;
                ++i;
                continue;
            }
            const unsigned n = (a >= 0xc2 && a <= 0xdf) ? 2 :
                               (a >= 0xe0 && a <= 0xef) ? 3 :
                               (a >= 0xf0 && a <= 0xf4) ? 4 : 0;
            if (!n || i + n > s.size()) return false;
            for (unsigned j = 1; j < n; ++j)
                if ((static_cast<unsigned char>(s[i+j]) & 0xc0) != 0x80) return false;
            const auto b = static_cast<unsigned char>(s[i + 1]);
            if ((a == 0xe0 && b < 0xa0) || (a == 0xed && b >= 0xa0) ||
                (a == 0xf0 && b < 0x90) || (a == 0xf4 && b >= 0x90)) return false;
            any = true;
            i += n;
        }
        return any;
    }

    // A line that may carry a secret is kept nowhere, not even for this session. Words are runs of ASCII letters,
    // so "api_key", "password123" and "--token=x" match; "keyboard" and "monkey" do not. Case-insensitive.
    static bool sensitive(std::string_view s) {
        constexpr std::string_view words[] = {
            "login", "register", "password", "passwd", "pass", "pwd", "token", "key", "apikey", "secret", "auth"
        };
        const auto ascii_word = [](unsigned char c) {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        };
        for (std::size_t pos = 0; pos < s.size();) {
            if (!ascii_word(static_cast<unsigned char>(s[pos]))) { ++pos; continue; }
            const std::size_t start = pos;
            while (pos < s.size() && ascii_word(static_cast<unsigned char>(s[pos]))) ++pos;
            const auto part = s.substr(start, pos - start);
            for (auto w : words) {
                if (part.size() != w.size()) continue;
                bool same = true;
                for (std::size_t i = 0; i < w.size(); ++i) {
                    const unsigned char c = static_cast<unsigned char>(part[i]);
                    const char lower = static_cast<char>((c >= 'A' && c <= 'Z') ? c + ('a' - 'A') : c);
                    if (lower != w[i]) { same = false; break; }
                }
                if (same) return true;
            }
        }
        return false;
    }

private:
    void trim() {
        persisted_removed_ = false;
        while (entries_.size() > limit_) {
            persisted_removed_ |= entries_.front().persisted;
            entries_.erase(entries_.begin());
        }
    }

    std::vector<Entry> entries_;
    std::size_t limit_;
    int cursor_ = -1;
    std::string draft_;
    bool persisted_removed_ = false;
};

} // namespace kui::chat_history
