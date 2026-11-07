// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
namespace safetyhook {
struct Context { uintptr_t rbp = 0, r14 = 0; };
struct Mid { bool on = false; explicit operator bool() const { return on; } };
inline Mid create_mid(void*, void (*)(Context&)) { return {true}; }
}
using SafetyHookMid = safetyhook::Mid;
