// SPDX-License-Identifier: MIT
// kui::IniFile: ImGui's settings (kiln/data/kiln.ui/imgui.ini: window places and sizes) read and written on a thread of
// its own; ImGui gets them from memory (io.IniFilename stays null). Header-only and without windows.h (host test:
// tests/ini_file_test.cpp).
//
// With io.IniFilename set, ImGui opened and read the file in the first frame's NewFrame and wrote it from a frame after a
// window moved, on the game's present call. On a hard disk under the game's own loading, opening the file alone held
// the first Preload frame for a quarter second (perfdiag 2026-11-17, inbox 50: CreateFileW < _wfsopen < ImFileOpen <
// ImFileLoadToMemory < LoadIniSettingsFromDisk < NewFrame < kui::build_frame < hk_present, 24 samples).
#pragma once
#include <condition_variable>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <utility>

namespace kui {

class IniFile {
public:
    // read(path): the file's text, nothing when there is none or it cannot be read. write(path, text): replace it.
    using Read = std::function<std::optional<std::string>(const std::string&)>;
    using Write = std::function<bool(const std::string&, const std::string&)>;
    IniFile(Read read, Write write) : read_(std::move(read)), write_(std::move(write)) {}
    IniFile(const IniFile&) = delete;
    IniFile& operator=(const IniFile&) = delete;
    // At process exit the thread is gone or about to be: a DLL detach never waits for it (stop() is the orderly end).
    ~IniFile() {
        if (thread_.joinable()) thread_.detach();
    }
    // Starts the thread, which reads the file first. Once; an empty path (no data folder) starts nothing.
    void start(std::string path) {
        std::lock_guard lock(mu_);
        if (started_ || path.empty()) return;
        started_ = true;
        path_ = std::move(path);
        thread_ = std::thread([this] { run(); });
    }
    // The settings to give ImGui: the file's text once read (waiting for the read, which starts with the plugin, long
    // before the first frame), or the last text saved since. Nothing without a file or a start.
    std::optional<std::string> text() {
        std::unique_lock lock(mu_);
        cv_.wait(lock, [this] { return !started_ || read_done_; });
        return text_;
    }
    // ImGui's settings changed (io.WantSaveIniSettings): written on the thread; a newer text replaces one not yet
    // written. Returns at once.
    void save(std::string text) {
        std::lock_guard lock(mu_);
        if (!started_ || stopping_) return;
        text_ = text;
        pending_ = std::move(text);
        cv_.notify_all();
    }
    // Writes what is pending and ends the thread.
    void stop() {
        {
            std::lock_guard lock(mu_);
            stopping_ = true;
            cv_.notify_all();
        }
        if (thread_.joinable()) thread_.join();
    }
    size_t writes() const {
        std::lock_guard lock(mu_);
        return writes_;
    }

private:
    void run() {
        std::optional<std::string> read = read_(path_);
        std::unique_lock lock(mu_);
        if (!text_) text_ = std::move(read);  // a save before the read finished is newer than the file
        read_done_ = true;
        cv_.notify_all();
        for (;;) {
            cv_.wait(lock, [this] { return pending_.has_value() || stopping_; });
            if (pending_) {
                std::string text = std::move(*pending_);
                pending_.reset();
                lock.unlock();
                write_(path_, text);
                lock.lock();
                ++writes_;
                continue;
            }
            return;  // stopping, nothing pending
        }
    }
    Read read_;
    Write write_;
    mutable std::mutex mu_;
    std::condition_variable cv_;
    std::thread thread_;
    std::string path_;
    std::optional<std::string> text_, pending_;
    bool started_ = false, read_done_ = false, stopping_ = false;
    size_t writes_ = 0;
};

}  // namespace kui
