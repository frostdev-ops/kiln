// SPDX-License-Identifier: MIT
// Host-test stand-in for native/core/abi/kiln_hook.hpp: no kiln.hooks, no patching. A link "installs" by recording its
// target; original() is the target itself, so a handler's call to the original reaches the fake game function.
// A test may also set `target` directly to give a hook a fake original without installing it.
// The same file sits in each host test's stub folder (keep the copies identical).
#pragma once
#include <cstdint>
#include <utility>
#include "kiln_api.h"

namespace kiln {
namespace test {
inline uintptr_t fail_target = 0;           // install() on this target fails
inline bool fail_all = false;               // every install() fails
inline thread_local uintptr_t caller = 0;   // what caller() answers (the game's call site)
inline int installs = 0, revokes = 0;       // counters
inline void (*on_install)(uintptr_t target) = nullptr;  // runs after a link went in
}  // namespace test

inline const void* hooks_v2(const KilnApi*) { return &test::installs; }

class RawHook {
public:
    RawHook() = default;
    RawHook(const RawHook&) = delete;
    RawHook& operator=(const RawHook&) = delete;
    RawHook(RawHook&& o) noexcept : target(o.target), live_(o.live_), error_(o.error_) { o.live_ = false; }
    RawHook& operator=(RawHook&& o) noexcept {
        if (this == &o) return *this;
        if (live_) revoke();
        if (o.target) target = o.target;
        live_ = o.live_, error_ = o.error_, o.live_ = false;
        return *this;
    }
    bool install(const KilnApi*, KilnOwner, uintptr_t target, void*, const char*, int32_t = 0, const char* = nullptr,
                 const char* = nullptr) {
        if (test::fail_all || (test::fail_target && target == test::fail_target) || !target)
            return error_ = "test: install refused", false;
        if (live_) revoke();
        this->target = reinterpret_cast<void*>(target), live_ = true, error_ = nullptr;
        ++test::installs;
        if (test::on_install) test::on_install(target);
        return true;
    }
    explicit operator bool() const { return live_; }
    const char* error() const { return error_; }
    template <class T> T original() const { return reinterpret_cast<T>(target); }
    template <class R = void, class... A> R call(A... a) const { return reinterpret_cast<R (*)(A...)>(target)(a...); }
    uintptr_t caller() const { return test::caller; }
    void revoke() {
        if (live_) ++test::revokes;
        live_ = false;
    }

    void* target = nullptr;

private:
    bool live_ = false;
    const char* error_ = nullptr;
};

class MidHook2 {
public:
    MidHook2() = default;
    MidHook2(const MidHook2&) = delete;
    MidHook2& operator=(const MidHook2&) = delete;
    MidHook2(MidHook2&& o) noexcept : live_(o.live_), error_(o.error_) { o.live_ = false; }
    MidHook2& operator=(MidHook2&& o) noexcept {
        if (this == &o) return *this;
        if (live_) revoke();
        live_ = o.live_, error_ = o.error_, o.live_ = false;
        return *this;
    }
    template <class Ctx>
    bool install(const KilnApi*, KilnOwner, uintptr_t target, void (*)(Ctx&), const char*, int32_t = 0,
                 const char* = nullptr, const char* = nullptr) {
        if (test::fail_all || (test::fail_target && target == test::fail_target) || !target)
            return error_ = "test: install refused", false;
        live_ = true, error_ = nullptr;
        ++test::installs;
        if (test::on_install) test::on_install(target);
        return true;
    }
    explicit operator bool() const { return live_; }
    const char* error() const { return error_; }
    uintptr_t caller() const { return test::caller; }
    void revoke() {
        if (live_) ++test::revokes;
        live_ = false;
    }

private:
    bool live_ = false;
    const char* error_ = nullptr;
};
}  // namespace kiln
