// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <optional>
namespace safetyhook {
struct Context { uintptr_t rcx = 0, rdx = 0; };
struct InlineHook {
    void* at = nullptr;
    explicit operator bool() const { return at != nullptr; }
    template<class T> T original() const { return reinterpret_cast<T>(at); }
};
inline InlineHook create_inline(void* at, void*) { return {at}; }
struct MidHook {
    bool exists = false;
    static std::optional<MidHook> create(void*, void(*)(Context&)) { return MidHook{true}; }
    explicit operator bool() const { return exists; }
};
}
