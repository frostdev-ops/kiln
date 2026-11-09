// SPDX-License-Identifier: MIT
// Host tests exercise program ownership/helpers, never Windows detour installation.
#pragma once
struct SafetyHookInline {
    explicit operator bool() const { return false; }
    template<class T, class... Args> T call(Args...) {}
};
namespace safetyhook {
inline SafetyHookInline create_inline(void*, void*) { return {}; }
}
