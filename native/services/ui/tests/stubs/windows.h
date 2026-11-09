// SPDX-License-Identifier: MIT
// Host-only stand-ins for the OS calls used by render.cpp; no game or Windows runtime.
#pragma once
#include <chrono>
#include <cstdint>
using HWND = void*;
inline uint64_t GetTickCount64() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
#define VK_ESCAPE 0x1B
#define EXCEPTION_EXECUTE_HANDLER 1
#define __try try
#define __except(x) catch (...)
