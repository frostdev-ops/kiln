// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
namespace safetyhook {
struct Context { uintptr_t rbp = 0, rdi = 0; };
struct InlineHook {
    void* target = nullptr;
    explicit operator bool() const { return target != nullptr; }
    template <typename R, typename... A> R call(A... args) {
        return reinterpret_cast<R(*)(A...)>(target)(args...);
    }
};
struct MidHook {
    bool present = false;
    explicit operator bool() const { return present; }
};
inline InlineHook create_inline(void* target, void*) { return {target}; }
inline MidHook create_mid(void* target, void(*)(Context&)) { return {target != nullptr}; }
}
using SafetyHookInline = safetyhook::InlineHook;
using SafetyHookMid = safetyhook::MidHook;
