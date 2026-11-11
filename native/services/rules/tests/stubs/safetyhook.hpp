// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
namespace safetyhook {
struct Xmm {
    float f32[4]{};
};
struct Context {
    uintptr_t rbp = 0, rdi = 0, rsp = 0, rsi = 0, rax = 0;
    Xmm xmm2, xmm6, xmm14;
};
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
inline bool test_fail_inline = false;
inline InlineHook create_inline(void* target, void*) { return {test_fail_inline ? nullptr : target}; }
inline MidHook create_mid(void* target, void(*)(Context&)) { return {target != nullptr}; }
}
using SafetyHookInline = safetyhook::InlineHook;
using SafetyHookMid = safetyhook::MidHook;
