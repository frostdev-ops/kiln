// SPDX-License-Identifier: MIT
// A test Platform (host suites): std::thread workers, a fake injectable clock, and a fault that a test
// arms for the next guarded call of a chosen callback address (instead of the call, or after it ran). There is no
// real SEH or module here; Windows proves those under Wine (tasks_windows_host.cpp). Modules are modelled as present
// with a balanced ref count so the pool's pin lifetime is still exercised. Header-only so each test links its own
// instance.
#pragma once
#include <atomic>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

#include "tasks_platform.h"

namespace kiln::tasks::test {

class HostPlatform : public Platform {
public:
    ThreadHandle start_thread(const char* name, ThreadFn fn, void* context) override {
        std::lock_guard lock(mutex_);
        ++starts_;
        if (fail_starts_ > 0 && name && fail_name_ == name) {
            --fail_starts_;
            return {0};  // CreateThread failed
        }
        uint64_t id = 0;
        if (reuse_ids_ && !free_ids_.empty()) {  // Windows reuses a closed HANDLE's value
            id = free_ids_.back();
            free_ids_.pop_back();
        } else {
            id = ++last_id_;
        }
        threads_.emplace(id, std::thread([this, id, name, fn, context] {
            this_id_ = id;  // thread_local
            {
                std::lock_guard l(mutex_);
                descriptions_[id] = name ? name : "";
            }
            fn(context);
        }));
        return {id};
    }
    void join_thread(ThreadHandle thread) override {
        std::thread t;
        {
            std::lock_guard lock(mutex_);
            const auto it = threads_.find(thread.id);
            if (it == threads_.end()) return;
            t = std::move(it->second);
            threads_.erase(it);
        }
        if (t.joinable()) t.join();
        std::lock_guard lock(mutex_);
        free_ids_.push_back(thread.id);  // closed: the value may be handed out again
    }
    void detach_thread(ThreadHandle thread) override {
        std::thread t;
        {
            std::lock_guard lock(mutex_);
            const auto it = threads_.find(thread.id);
            if (it == threads_.end()) return;
            t = std::move(it->second);
            threads_.erase(it);
        }
        if (t.joinable()) t.detach();  // host tests never leave a stuck worker; present only for the interface
    }
    uint64_t current_thread_id() override { return this_id_; }  // thread_local: no lock
    void set_description(const char* utf8) override {
        std::lock_guard lock(mutex_);
        descriptions_[this_id_] = utf8 ? utf8 : "";
        description_log_.emplace_back(this_id_, descriptions_[this_id_]);
    }
    void lower_current_priority() override { ++lowered_; }

    ModuleRef acquire_module(uintptr_t address) override {
        ModuleRef ref;
        std::function<void(uintptr_t)> hook;
        {
            std::lock_guard lock(mutex_);
            ++module_refs_[address];
            ++acquires_;
            ref = {static_cast<uint64_t>(address), module_present_};
            hook = acquire_hook_;
        }
        if (hook) hook(address);  // outside the platform lock: a test may hold the caller here
        return ref;
    }
    void release_module(ModuleRef ref) override {
        std::lock_guard lock(mutex_);
        if (ref.token == 0 && !ref.present) return;
        ++releases_;
        auto it = module_refs_.find(static_cast<uintptr_t>(ref.token));
        if (it != module_refs_.end() && --it->second == 0) module_refs_.erase(it);
    }

    void guarded_call(GuardedFn fn, void* context, FaultInfo* info) override {
        *info = {};
        uintptr_t target = 0;
        {
            std::lock_guard lock(mutex_);
            target = fault_target_.load();
        }
        const uintptr_t about_to_call = static_cast<const GuardedHeader*>(context)->callback;
        if (target && about_to_call == target) {
            bool run_first = false;
            {
                std::lock_guard lock(mutex_);
                fault_target_.store(0);
                run_first = fault_after_;
                fault_after_ = false;
                info->code = fault_code_;
                info->thread_tainted = fault_tainted_;
            }
            if (run_first) fn(context);  // the callback runs, then raises (outside the platform lock)
            info->faulted = true;
            return;
        }
        fn(context);
    }
    uint64_t now_ns() override { return clock_.load(); }
    uint64_t thread_cpu_ns() override { return cpu_clock_.load(); }

    // ---- test controls ----
    void set_clock(uint64_t ns) { clock_.store(ns); }
    void advance(uint64_t ns) { clock_.fetch_add(ns); }
    // The fake CPU clock, apart from the wall clock above: a body advances it to say what the
    // executing thread burned, while wall time it awaited is advanced with advance() alone.
    void set_cpu_clock(uint64_t ns) { cpu_clock_.store(ns); }
    void advance_cpu(uint64_t ns) { cpu_clock_.fetch_add(ns); }
    void fault_next_call_of(uintptr_t address, uint32_t code = 0xC0000005, bool tainted = true) {
        std::lock_guard lock(mutex_);
        fault_target_.store(address);
        fault_after_ = false;
        fault_code_ = code;
        fault_tainted_ = tainted;
    }
    // The next call of `address` runs to its end and then reports a fault (a callback that raised late).
    void fault_after_call_of(uintptr_t address, uint32_t code = 0xC0000005, bool tainted = true) {
        std::lock_guard lock(mutex_);
        fault_target_.store(address);
        fault_after_ = true;
        fault_code_ = code;
        fault_tainted_ = tainted;
    }
    // The next `count` start_thread calls for a thread named `name` fail (CreateThread returning NULL).
    void fail_starts(const char* name, int count) {
        std::lock_guard lock(mutex_);
        fail_name_ = name;
        fail_starts_ = count;
    }
    int pending_start_failures() {
        std::lock_guard lock(mutex_);
        return fail_starts_;
    }
    // Hand a joined thread's id to the next started thread (as Windows reuses a closed HANDLE value).
    void reuse_ids(bool on) {
        std::lock_guard lock(mutex_);
        reuse_ids_ = on;
    }
    // Called (outside the platform lock) after every acquire_module, on the acquiring thread.
    void set_acquire_hook(std::function<void(uintptr_t)> hook) {
        std::lock_guard lock(mutex_);
        acquire_hook_ = std::move(hook);
    }
    void set_module_present(bool present) { module_present_ = present; }
    std::size_t module_refcount(uintptr_t address) {
        std::lock_guard lock(mutex_);
        const auto it = module_refs_.find(address);
        return it == module_refs_.end() ? 0 : it->second;
    }
    uint64_t acquires() const { return acquires_; }
    uint64_t releases() const { return releases_; }
    uint64_t lowered() const { return lowered_; }
    std::string description(uint64_t id) {
        std::lock_guard lock(mutex_);
        return descriptions_[id];
    }
    std::vector<std::pair<uint64_t, std::string>> description_log() {
        std::lock_guard lock(mutex_);
        return description_log_;
    }
    bool all_joined() {
        std::lock_guard lock(mutex_);
        return threads_.empty();
    }

private:
    std::mutex mutex_;
    std::unordered_map<uint64_t, std::thread> threads_;
    std::unordered_map<uint64_t, std::string> descriptions_;
    std::vector<std::pair<uint64_t, std::string>> description_log_;
    std::unordered_map<uintptr_t, std::size_t> module_refs_;
    static inline thread_local uint64_t this_id_ = 0;  // 0 on the main/test thread
    uint64_t last_id_ = 0;
    uint64_t starts_ = 0;
    std::string fail_name_;
    int fail_starts_ = 0;
    bool reuse_ids_ = false;
    std::vector<uint64_t> free_ids_;
    std::function<void(uintptr_t)> acquire_hook_;
    std::atomic<uint64_t> clock_{0};
    std::atomic<uint64_t> cpu_clock_{0};
    std::atomic<uintptr_t> fault_target_{0};
    bool fault_after_ = false;
    uint32_t fault_code_ = 0xC0000005;
    bool fault_tainted_ = true;
    bool module_present_ = true;
    std::atomic<uint64_t> acquires_{0}, releases_{0}, lowered_{0};
};

}  // namespace kiln::tasks::test
