// SPDX-License-Identifier: MIT
// Chat history's file (GitHub issue 44): <game>/kiln/data/kiln.ui/chat-history.json, written off the game's UI thread.
// Portable: no Windows, no game memory (tests/chat_store_test.cpp).
#pragma once
#include <condition_variable>
#include <cstddef>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#include "nlohmann/json.hpp"

namespace kui::chat_store {

constexpr size_t kMaxFileBytes = 1 << 20;  // 200 lines of at most 1 KiB fit many times; a bigger file is not ours
constexpr size_t kMaxLines = 2000;

// {"version": 1, "entries": [{"text": "/cmd", "server": null}, ...]}, oldest first. "server" is kept for a later
// per-server history and is always null today.
inline std::string serialize(const std::vector<std::string>& lines) {
    nlohmann::json doc = {{"version", 1}, {"entries", nlohmann::json::array()}};
    for (const auto& line : lines) doc["entries"].push_back({{"text", line}, {"server", nullptr}});
    // Lines are valid UTF-8 (History::valid); "replace" keeps a bad byte from throwing on the writer thread.
    return doc.dump(-1, ' ', false, nlohmann::json::error_handler_t::replace);
}

// The lines of a file's text; anything unexpected gives what could be read, never an exception.
inline std::vector<std::string> parse(std::string_view text) {
    std::vector<std::string> lines;
    if (text.size() > kMaxFileBytes) return lines;
    const auto doc = nlohmann::json::parse(text.begin(), text.end(), nullptr, false);
    if (!doc.is_object()) return lines;
    const auto version = doc.find("version");
    if (version == doc.end() || !version->is_number_integer() || version->get<int64_t>() != 1) return lines;
    const auto it = doc.find("entries");
    if (it == doc.end() || !it->is_array()) return lines;
    for (const auto& e : *it) {
        if (lines.size() >= kMaxLines) break;
        if (e.is_object() && e.contains("text") && e["text"].is_string()) lines.push_back(e["text"].get<std::string>());
    }
    return lines;
}

// One writer thread, started at the first save. The UI thread hands it the newest list and never waits for the disk;
// lists that arrive while a write runs collapse into the newest. stop() writes what is still pending and joins (the
// service's on_unload). Keep it alive for the process (a static destructor must not join a thread).
class Saver {
public:
    using Write = std::function<void(const std::string& text)>;
    explicit Saver(Write write) : write_(std::move(write)) {}
    Saver(const Saver&) = delete;
    Saver& operator=(const Saver&) = delete;

    void request(std::vector<std::string> lines) {
        std::lock_guard lock(mu_);
        if (stopped_) return;
        pending_ = std::move(lines);
        if (!thread_.joinable()) thread_ = std::thread([this] { run(); });
        cv_.notify_one();
    }

    void stop() {
        {
            std::lock_guard lock(mu_);
            if (stopped_) return;
            stopped_ = true;
        }
        cv_.notify_one();
        if (thread_.joinable()) thread_.join();
    }

    size_t writes() const {
        std::lock_guard lock(mu_);
        return writes_;
    }

private:
    void run() {
        std::unique_lock lock(mu_);
        for (;;) {
            cv_.wait(lock, [this] { return pending_.has_value() || stopped_; });
            if (!pending_) return;  // stopped, nothing left
            const std::vector<std::string> lines = std::move(*pending_);
            pending_.reset();
            lock.unlock();
            write_(serialize(lines));
            lock.lock();
            ++writes_;
        }
    }

    Write write_;
    mutable std::mutex mu_;
    std::condition_variable cv_;
    std::optional<std::vector<std::string>> pending_;
    bool stopped_ = false;
    size_t writes_ = 0;
    std::thread thread_;
};

} // namespace kui::chat_store
