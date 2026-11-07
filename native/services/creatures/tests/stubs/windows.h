// SPDX-License-Identifier: MIT
#pragma once
#include <cstdint>
#include <chrono>
inline uint64_t GetTickCount64() { return 1000; }
struct LARGE_INTEGER { int64_t QuadPart = 0; };
inline void QueryPerformanceCounter(LARGE_INTEGER* out) {
    out->QuadPart = std::chrono::steady_clock::now().time_since_epoch().count();
}
inline void QueryPerformanceFrequency(LARGE_INTEGER* out) { out->QuadPart = 1000000000; }
