// SPDX-License-Identifier: MIT
// Host-only SEH syntax shim. These tests use valid synthetic memory; faults are injected as C++ exceptions.
#pragma once
#include <chrono>
#define __try try
#define __except(...) catch (...)
#define EXCEPTION_EXECUTE_HANDLER 1
inline uint64_t GetTickCount64() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
