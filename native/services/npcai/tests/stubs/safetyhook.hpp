// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <optional>
namespace safetyhook {
inline int test_enable_fail = -1, test_hooks = 0;
inline void (*test_enabled)(int) = nullptr;
struct Context { uintptr_t rcx = 0, rdx = 0; };
struct InlineHook {
    enum Flags { StartDisabled };
    void* at = nullptr;
    int index = -1;
    bool on = false;
    explicit operator bool() const { return at != nullptr; }
    template<class T> T original() const { return reinterpret_cast<T>(at); }
    bool enable() { if (index == test_enable_fail) return false; on = true; if (test_enabled) test_enabled(index); return true; }
    bool disable() { on = false; return true; }
    bool enabled() const { return on; }
};
inline InlineHook create_inline(void* at, void*, InlineHook::Flags) { return {at, test_hooks++, false}; }
struct MidHook {
    enum Flags { StartDisabled };
    bool exists = false, on = false;
    static std::optional<MidHook> create(void*, void(*)(Context&), Flags) { return MidHook{true, false}; }
    explicit operator bool() const { return exists; }
    bool enable() { on = true; return true; }
    bool disable() { on = false; return true; }
    bool enabled() const { return on; }
};
}
