// SPDX-License-Identifier: MIT
// kc::registry::ReportWriter: the thread that makes a live registry report's JSON and writes state/registry.json (the stage
// runner's Env.post_report), so the world tick that publishes a report never does either (owner 11-26, registry-followups
// review Q1: a late rebuild's JSON and file took 87 ms at 10,000 bound fields and 370 ms at 40,000 on the tick).
// Header-only, without windows.h (host test: tests/registry_stage_review_test.cpp).
#pragma once
#include <chrono>
#include <condition_variable>
#include <exception>
#include <functional>
#include <mutex>
#include <thread>
#include <utility>

namespace kc::registry {

class ReportWriter {
public:
    using Fail = std::function<void(const char* what)>;
    explicit ReportWriter(Fail fail = {}) : fail_(std::move(fail)) {}
    ReportWriter(const ReportWriter&) = delete;
    ReportWriter& operator=(const ReportWriter&) = delete;
    // Runs what is pending and ends the thread. The service never destroys its writer (as the runner: a DLL detach at
    // process exit never waits for a thread); its exit step flushes it instead (flush). Tests do.
    ~ReportWriter() {
        {
            std::lock_guard lock(mu_);
            stopping_ = true;
            cv_.notify_all();
        }
        if (thread_.joinable()) thread_.join();
    }
    // Runs fn on the thread; returns at once. Every fn writes the newest report (Runner's write_live_report), so a newer fn
    // replaces one that has not started: a writer that falls behind writes the newest report once, never each in turn.
    void post(std::function<void()> fn) {
        std::lock_guard lock(mu_);
        pending_ = std::move(fn);
        if (!thread_.joinable()) thread_ = std::thread([this] { run(); });
        cv_.notify_all();
    }
    // Waits until nothing is pending or running (tests).
    void wait_idle() {
        std::unique_lock lock(mu_);
        idle_cv_.wait(lock, [this] { return !pending_ && !running_; });
    }
    // The same for at most `max`; false: something is still pending or running (kiln.content's exit step, which has 3 s).
    bool flush(std::chrono::milliseconds max) {
        std::unique_lock lock(mu_);
        return idle_cv_.wait_for(lock, max, [this] { return !pending_ && !running_; });
    }
    size_t runs() const {
        std::lock_guard lock(mu_);
        return runs_;
    }

private:
    void run() {
        std::unique_lock lock(mu_);
        for (;;) {
            cv_.wait(lock, [this] { return bool(pending_) || stopping_; });
            if (!pending_) return;  // stopping, nothing pending
            std::function<void()> fn = std::move(pending_);
            pending_ = nullptr;
            running_ = true;
            lock.unlock();
            try {
                fn();
            } catch (const std::exception& e) {
                if (fail_) fail_(e.what());
            } catch (...) {
                if (fail_) fail_("unknown exception");
            }
            lock.lock();
            running_ = false;
            ++runs_;
            if (!pending_) idle_cv_.notify_all();
        }
    }
    Fail fail_;
    mutable std::mutex mu_;
    std::condition_variable cv_, idle_cv_;
    std::thread thread_;
    std::function<void()> pending_;
    bool running_ = false, stopping_ = false;
    size_t runs_ = 0;
};

}  // namespace kc::registry
