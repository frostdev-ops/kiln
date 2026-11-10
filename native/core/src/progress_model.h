// SPDX-License-Identifier: MIT
// kiln.progress (abi/services/kiln_progress.h): the part of the service that is not Win32, so that a host test can run it.
// The tasks that are running, which one the window shows, its text and its bar, and when the window is due. Thread-safe:
// the callers (any thread) and the window's thread share it, and every call holds the lock for a copy only.
#pragma once
#include <algorithm>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

namespace kiln::progress {

inline constexpr size_t kMaxText = 255;

// `s` cut to at most kMaxText bytes, not inside a UTF-8 sequence.
inline std::string clip(std::string_view s) {
    if (s.size() <= kMaxText) return std::string(s);
    size_t n = kMaxText;
    while (n > 0 && (static_cast<unsigned char>(s[n]) & 0xC0) == 0x80) --n;  // s[n] continues a sequence: back up to its start
    return std::string(s.substr(0, n));
}

class Model {
public:
    struct View {
        bool active = false;
        std::string title;
        std::string text;
        uint64_t done = 0;
        uint64_t total = 0;
        // Bumped by every change: the window repaints when it differs from the one it painted.
        uint64_t revision = 0;
        // 0..100; meaningless while `indeterminate`.
        uint32_t percent = 0;
        // No total is known: the bar moves on its own.
        bool indeterminate = true;
    };

    // Starts a task. `now_ms` is the caller's monotonic clock (the delay before the window shows runs from the moment
    // the first of the tasks that run now began).
    uint64_t begin(std::string_view title, uint64_t now_ms) {
        std::lock_guard lock(m_);
        if (tasks_.empty()) since_ms_ = now_ms;
        Task t;
        t.id = ++next_id_;
        t.title = clip(title);
        tasks_.push_back(std::move(t));
        ++revision_;
        return tasks_.back().id;
    }

    void update(uint64_t id, std::string_view text, uint64_t done, uint64_t total) {
        std::lock_guard lock(m_);
        for (auto& t : tasks_) {
            if (t.id != id) continue;
            t.text = clip(text);
            t.done = done;
            t.total = total;
            ++revision_;
            return;
        }
    }

    void end(uint64_t id) {
        std::lock_guard lock(m_);
        const auto it = std::remove_if(tasks_.begin(), tasks_.end(), [id](const Task& t) { return t.id == id; });
        if (it == tasks_.end()) return;
        tasks_.erase(it, tasks_.end());
        ++revision_;
    }

    void end_all() {
        std::lock_guard lock(m_);
        if (tasks_.empty()) return;
        tasks_.clear();
        ++revision_;
    }

    bool active() const {
        std::lock_guard lock(m_);
        return !tasks_.empty();
    }

    // The task that began last and still runs.
    View view() const {
        std::lock_guard lock(m_);
        View v;
        v.revision = revision_;
        if (tasks_.empty()) return v;
        const Task& t = tasks_.back();
        v.active = true;
        v.title = t.title;
        v.text = t.text;
        v.done = t.done;
        v.total = t.total;
        v.indeterminate = t.total == 0;
        if (t.total > 0) v.percent = static_cast<uint32_t>(std::min<double>(100.0, 100.0 * static_cast<double>(t.done) / static_cast<double>(t.total)));
        return v;
    }

    // Is a window due: something runs, and the first of the tasks running now began `delay_ms` ago?
    bool due(uint64_t now_ms, uint64_t delay_ms) const {
        std::lock_guard lock(m_);
        return !tasks_.empty() && now_ms >= since_ms_ && now_ms - since_ms_ >= delay_ms;
    }

private:
    struct Task {
        uint64_t id = 0;
        std::string title;
        std::string text;
        uint64_t done = 0;
        uint64_t total = 0;
    };
    mutable std::mutex m_;
    std::vector<Task> tasks_;
    uint64_t next_id_ = 0;
    uint64_t revision_ = 0;
    uint64_t since_ms_ = 0;
};

}  // namespace kiln::progress
