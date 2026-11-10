// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <optional>

#include "services/kiln_hooks.h"  // KilnRegisters: kiln::MidHook2 wants a context of the same size
namespace safetyhook {
// Only rcx and rdx are named; the rest pads the struct to the real Context's size.
struct Context {
    uintptr_t rcx = 0, rdx = 0;
    unsigned char rest[sizeof(KilnRegisters) - 2 * sizeof(uintptr_t)] = {};
};
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
